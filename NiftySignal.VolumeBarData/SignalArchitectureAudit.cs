using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.Scoring;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23 correctness-review investigation (docs/Price_Based_Findings.md, "Investigating the
/// zero-trade result" section) -- read-only, structural-only diagnostic. Never opens a trade, never
/// touches <see cref="TradeSimulator"/>'s dispatch chain (same "wholly separate method" discipline
/// <see cref="MomentumRelationshipAnalyzer"/> already established for this exact reason). Exists to
/// answer, with evidence rather than assumption, why the reset-on-ATM-roll fix
/// (<see cref="PriceCrossoverEngine.Reset"/>, wired into <see cref="TradeSimulator.SimulatePriceCrossoverDayAsync"/>)
/// produced zero trades, and to compare three candidate signal architectures on STRUCTURAL terms
/// only (signal observations, resets, candidate crossings) -- explicitly NOT profitability, per the
/// user's own instruction not to optimize thresholds or run a parameter sweep at this stage.
/// </summary>
public static class SignalArchitectureAudit
{
    /// <summary>
    /// A: current production behavior (<see cref="TradeSimulator.SimulatePriceCrossoverDayAsync"/>)
    /// -- ATM re-picked every bar; the engine is Reset() the instant the ATM strike changes, so no
    /// two bars in one live window can ever come from different contracts.
    /// B: ATM picked ONCE from the day's very first bar and held fixed all day (mirrors
    /// <see cref="MomentumRelationshipAnalyzer.StrikeMode.SignalFixed"/>) -- one real contract's own
    /// price series, untouched, for the whole session.
    /// C: ATM still re-picked every bar (never reset), but every price from a NEW contract is scaled
    /// by a cumulative multiplicative adjustment factor computed from BOTH contracts' own REAL prices
    /// at the exact transition bar (outgoing/incoming), so the fed series stays numerically
    /// continuous across a roll without ever fabricating a price or silently mixing raw premium
    /// levels. Standard technique (same idea as a back-adjusted continuous futures contract); no
    /// prior precedent for it existed anywhere else in this codebase (checked before writing this).
    /// </summary>
    public enum SignalArchitecture
    {
        RollingReset,
        FixedAtm,
        RollingBackAdjusted,
        /// <summary>Reference only -- the ORIGINAL, pre-fix behavior this whole investigation started from: ATM re-picked every bar, raw price fed directly, no reset and no adjustment (the splicing bug). Included purely so the three corrected architectures can be compared against what actually produced the numbers recorded earlier in docs/Price_Based_Findings.md, not against each other in a vacuum.</summary>
        RollingSplice,
    }

    public sealed record BarTrace(
        int BarIndex, DateTimeOffset Timestamp, decimal FuturePrice,
        decimal? AtmStrike, string? Token, double? RawPrice, double? EngineInputPrice,
        bool StrikeChangedThisBar, bool EngineReset, double? FastMa, double? SlowMa,
        double? DiffFraction, bool RawSignFlip, bool ThresholdCrossedUp, bool ThresholdCrossedDown);

    public sealed record MarketStructureStats(
        DateOnly Date, OptionType Side, int BarCount, int DistinctStrikesSeen, int StrikeChanges,
        int MaxStableRun, int RunsAtLeast10, int RunsAtLeast20, int RunsAtLeast40, int RunsAtLeast60,
        IReadOnlyList<int> AllRunLengths);

    public sealed record SignalStats(
        DateOnly Date, OptionType Side, SignalArchitecture Architecture,
        int EngineResets, int WarmedUpBars, int RawSignFlips, int ThresholdCrossingsUp, int ThresholdCrossingsDown, int TradeCandidates,
        int FallbackAdjustments)
    {
        public int ThresholdCrossings => ThresholdCrossingsUp + ThresholdCrossingsDown;
    }

    /// <summary>Pure, unit-testable: should the engine reset given the previous and current bar's ATM strike? Null current strike (no data this bar) never triggers a reset -- matches "no fabricated warm-up value" convention.</summary>
    public static bool ShouldResetOnStrikeChange(decimal? lastStrike, decimal? currentStrike)
        => lastStrike is { } last && currentStrike is { } current && last != current;

    public static async Task<MarketStructureStats> ComputeMarketStructureAsync(
        NiftySignalDbContext source, VolumeBarDbContext volumeBarDb, DateOnly asOfDate, long barVolumeThreshold,
        OptionType side, CancellationToken cancellationToken)
    {
        var (bars, chain) = await LoadDayAsync(source, volumeBarDb, asOfDate, barVolumeThreshold, cancellationToken);
        if (bars.Count == 0 || chain.Count == 0)
        {
            return new MarketStructureStats(asOfDate, side, 0, 0, 0, 0, 0, 0, 0, 0, []);
        }

        var strikes = bars.Select(b => AtmStrikeSelector.PickAtm(chain, side, b.ClosePrice)?.StrikePrice).ToList();

        var runLengths = new List<int>();
        var currentRun = 0;
        var changes = 0;
        decimal? prev = null;
        foreach (var s in strikes)
        {
            if (s is null)
            {
                continue;
            }
            if (prev is { } p && s != p)
            {
                changes++;
                runLengths.Add(currentRun);
                currentRun = 1;
            }
            else
            {
                currentRun++;
            }
            prev = s;
        }
        if (currentRun > 0)
        {
            runLengths.Add(currentRun);
        }

        return new MarketStructureStats(
            asOfDate, side, bars.Count, strikes.Where(s => s is not null).Distinct().Count(), changes,
            runLengths.Count > 0 ? runLengths.Max() : 0,
            runLengths.Count(r => r >= 10), runLengths.Count(r => r >= 20), runLengths.Count(r => r >= 40), runLengths.Count(r => r >= 60),
            runLengths);
    }

    public static async Task<(List<BarTrace> Trace, SignalStats Stats)> RunDayAsync(
        NiftySignalDbContext source, VolumeBarDbContext volumeBarDb, DateOnly asOfDate, long barVolumeThreshold,
        OptionType side, int fastBars, int slowBars, double thresholdFraction, SignalArchitecture architecture,
        CancellationToken cancellationToken, TimeSpan? entryWindowStart = null, TimeSpan? entryWindowEnd = null)
    {
        var (bars, chain) = await LoadDayAsync(source, volumeBarDb, asOfDate, barVolumeThreshold, cancellationToken);
        var trace = new List<BarTrace>();
        if (bars.Count == 0 || chain.Count == 0)
        {
            return (trace, new SignalStats(asOfDate, side, architecture, 0, 0, 0, 0, 0, 0, 0));
        }

        var dayStart = bars[0].StartTimestamp;
        var dayEnd = bars[^1].EndTimestamp;
        var priceCache = new Dictionary<string, OptionPriceSeries>();
        async Task<OptionPriceSeries> GetSeriesAsync(string token)
        {
            if (priceCache.TryGetValue(token, out var cached))
            {
                return cached;
            }
            var series = await OptionPriceSeries.LoadAsync(source, token, dayStart, dayEnd, cancellationToken);
            priceCache[token] = series;
            return series;
        }

        var engine = new PriceCrossoverEngine(fastBars, slowBars);
        decimal? lastStrike = null;
        string? lastToken = null;
        double? lastRawPrice = null;
        double? previousDiffFraction = null;
        var adjustmentFactor = 1.0;

        var resets = 0;
        var warmedUpBars = 0;
        var rawSignFlips = 0;
        var thresholdCrossingsUp = 0;
        var thresholdCrossingsDown = 0;
        var tradeCandidates = 0;
        var fallbackAdjustments = 0;

        var istOffset = TimeSpan.FromHours(5.5);

        foreach (var bar in bars)
        {
            var atm = AtmStrikeSelector.PickAtm(chain, side, bar.ClosePrice);
            decimal? currentStrike = architecture == SignalArchitecture.FixedAtm ? (lastToken is null ? atm?.StrikePrice : lastStrike) : atm?.StrikePrice;
            var currentToken = architecture == SignalArchitecture.FixedAtm ? (lastToken ?? atm?.Token) : atm?.Token;

            double? rawPrice = null;
            if (currentToken is not null)
            {
                var series = await GetSeriesAsync(currentToken);
                var p = series.PriceAtOrBefore(bar.EndTimestamp);
                rawPrice = p is { } pr && pr > 0 ? (double)pr : null;
            }

            var strikeChangedThisBar = architecture != SignalArchitecture.FixedAtm && ShouldResetOnStrikeChange(lastStrike, currentStrike);
            var didReset = false;
            double? engineInputPrice = rawPrice;

            if (architecture == SignalArchitecture.RollingReset)
            {
                if (strikeChangedThisBar)
                {
                    engine.Reset();
                    previousDiffFraction = null;
                    didReset = true;
                    resets++;
                }
            }
            else if (architecture == SignalArchitecture.RollingBackAdjusted && strikeChangedThisBar && rawPrice is { } newPrice)
            {
                // Back-adjustment: look up the OUTGOING contract's own real price at THIS SAME
                // timestamp (it is still a real, quoted instrument -- just no longer nearest-ATM).
                // ratio = outgoing/incoming, both genuinely observed at the same instant; applied as
                // a cumulative multiplier so the fed series is continuous without inventing any price.
                double? outgoingPrice = null;
                if (lastToken is not null)
                {
                    var outSeries = await GetSeriesAsync(lastToken);
                    var op = outSeries.PriceAtOrBefore(bar.EndTimestamp);
                    outgoingPrice = op is { } opv && opv > 0 ? (double)opv : null;
                }

                if (outgoingPrice is { } outp && newPrice > 0)
                {
                    adjustmentFactor *= outp / newPrice;
                }
                else
                {
                    // Fallback: outgoing contract has no valid quote at this instant (illiquid) --
                    // cannot compute a genuine ratio, so the factor is left unchanged rather than
                    // guessed. Flagged, not silently absorbed.
                    fallbackAdjustments++;
                }
            }

            if (architecture == SignalArchitecture.RollingBackAdjusted && rawPrice is { } rp)
            {
                engineInputPrice = rp * adjustmentFactor;
            }

            var step = engine.Observe(engineInputPrice, thresholdFraction);
            if (step.FastMa is not null)
            {
                warmedUpBars++;
            }

            var rawFlip = false;
            if (step.DiffFraction is { } diff)
            {
                if (previousDiffFraction is { } prevDiff && Math.Sign(prevDiff) != Math.Sign(diff) && prevDiff != 0 && diff != 0)
                {
                    rawFlip = true;
                    rawSignFlips++;
                }
                previousDiffFraction = diff;
            }

            if (step.CrossedUp)
            {
                thresholdCrossingsUp++;
            }
            if (step.CrossedDown)
            {
                thresholdCrossingsDown++;
            }

            // Trade candidate: an entry-direction crossing (CrossedUp, matching this method's own
            // "buy when fast crosses above slow" convention) that also clears the entry time window,
            // if one was supplied -- structural count only, no strike/premium-band lookup, no P&L.
            var istTime = bar.EndTimestamp.ToOffset(istOffset).TimeOfDay;
            var withinWindow = (entryWindowStart is null || istTime >= entryWindowStart)
                && (entryWindowEnd is null || istTime <= entryWindowEnd);
            if (step.CrossedUp && withinWindow)
            {
                tradeCandidates++;
            }

            trace.Add(new BarTrace(bar.BarIndex, bar.EndTimestamp, bar.ClosePrice, currentStrike, currentToken,
                rawPrice, engineInputPrice, strikeChangedThisBar, didReset, step.FastMa, step.SlowMa, step.DiffFraction,
                rawFlip, step.CrossedUp, step.CrossedDown));

            if (currentStrike is not null)
            {
                lastStrike = currentStrike;
            }
            if (currentToken is not null)
            {
                lastToken = currentToken;
            }
            if (rawPrice is not null)
            {
                lastRawPrice = rawPrice;
            }
        }

        return (trace, new SignalStats(asOfDate, side, architecture, resets, warmedUpBars, rawSignFlips, thresholdCrossingsUp, thresholdCrossingsDown, tradeCandidates, fallbackAdjustments));
    }

    static async Task<(List<VolumeBarRow> Bars, List<Domain.Entities.Instrument> Chain)> LoadDayAsync(
        NiftySignalDbContext source, VolumeBarDbContext volumeBarDb, DateOnly asOfDate, long barVolumeThreshold, CancellationToken cancellationToken)
    {
        var bars = await volumeBarDb.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(cancellationToken);
        if (bars.Count == 0)
        {
            return (bars, []);
        }

        var allOptions = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY")
            .OrderBy(i => i.StrikePrice).ThenBy(i => i.OptionType)
            .ToListAsync(cancellationToken);
        if (allOptions.Count == 0)
        {
            return (bars, []);
        }

        var nearestExpiry = allOptions.Select(o => o.ExpiryDate!.Value).Min();
        var chain = allOptions.Where(o => o.ExpiryDate == nearestExpiry).ToList();
        return (bars, chain);
    }
}
