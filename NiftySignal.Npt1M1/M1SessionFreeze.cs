using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Trading.Adapters;

namespace NiftySignal.Npt1M1;

/// <summary>Facts about the M1 session freeze, recorded with the session and compared on every recovery.</summary>
public sealed record M1FreezeReport(
    string TradeDate,
    string WeeklyExpiry,
    int OpeningCoveredMinutes,
    long OpeningVolume,
    long BaseBarVolume,
    string SignalStartUtc,
    string ObservationStartUtc,
    int OptionContracts,
    string? AnchorCenterStrike,
    string Fingerprint);

/// <summary>The frozen M1 session, its report and the complete provenance of the base bar volume decision.</summary>
public sealed record M1Freeze(SessionFreeze Freeze, M1FreezeReport Report, BarSizeDecision Decision);

/// <summary>
/// PORT of research/bar-size-v2 e402baf <c>FreezeBuilder.Build</c> with the M1 sizer of <c>tools/DayCsvExport/BarSizing.cs</c> wired in: the frozen adaptive session is rebuilt from the
/// stored rows exactly as the research export did (rows up to 09:30:05, the same normaliser / enricher / coverage, the 09:30 residual anchor, no strong threshold, signal start 09:30,
/// observation start 09:15), and the base bar volume is <see cref="OpeningVolumeProjectionV2.Resolve"/> with beta 0.5 over the eligible PRIOR sessions only
/// (<c>covered == 15</c> for a fully covered opening). Pure: it reads no database and no clock.
/// </summary>
public static class M1SessionFreeze
{
    public const double RiskFreeRate = 0.065;                    // Pricing:RiskFreeRate of the Host configuration (the research harness uses the same constant)

    /// <param name="priors">Prior sessions eligible for the baseline (see <see cref="FuturesVolumeHistory.Eligible"/>). Sessions on or after <paramref name="day"/> are ignored by the rule.</param>
    public static M1Freeze Build(
        DateOnly day,
        DateOnly weeklyExpiry,
        FutureInstrument future,
        IReadOnlyList<ObserverOptionInstrument> descriptors,
        IReadOnlyDictionary<string, decimal> tickSizes,
        IEnumerable<SourceTick> rows,
        IReadOnlyList<PriorSession> priors)
    {
        var open = M1Time.IstTime(day, 9, 15);
        var cutoff = M1Time.IstTime(day, 9, 30);
        var through = cutoff.AddSeconds(5);

        var normalizer = new AdaptiveIncrementalTickNormalizer();
        var preClean = new List<(string Token, CleanObserverTick Tick)>();
        foreach (var row in rows)
        {
            if (row.ExchangeTimestamp > through || row.ReceivedAt > through)
            {
                continue;                                         // the research harness reads Stream(cutoff + 5 s): rows are in id order, later ids may still be early rows
            }

            if (normalizer.Process(row.Token, row.ToRaw()) is { } tick && tick.AvailableAt >= open && tick.AvailableAt <= through)
            {
                preClean.Add((row.Token, tick));
            }
        }

        preClean.Sort((a, b) => a.Tick.AvailableAt != b.Tick.AvailableAt ? a.Tick.AvailableAt.CompareTo(b.Tick.AvailableAt) : a.Tick.Id.CompareTo(b.Tick.Id));
        var futureTicks = preClean.Where(x => x.Token == future.Token).Select(x => x.Tick).ToList();
        if (!futureTicks.Any(x => x.AvailableAt >= cutoff))
        {
            throw new InvalidOperationException("The stored futures stream does not cross 09:30 for this day.");
        }

        var enricher = new AdaptiveTradeFlowEnricher();
        long openingVolume = 0;
        foreach (var tick in futureTicks)
        {
            var e = enricher.Process(tick);
            if (tick.AvailableAt < cutoff)
            {
                openingVolume += e.TradeVolume;
            }
        }

        var covered = AdaptiveOpeningCoverage.CoveredMinutes(futureTicks, open, cutoff);
        if (covered != 15 || openingVolume <= 0)
        {
            throw new InvalidOperationException($"Late-start / partial-opening session (covered minutes {covered}, opening volume {openingVolume}): the M1 freeze cannot be reconstructed from this data.");
        }

        var decision = OpeningVolumeProjectionV2.Resolve(day, openingVolume, covered == 15, priors, OpeningVolumeProjectionV2.BetaM1, future.LotSize);
        var baseVolume = decision.BaseBar;
        OptionResidualAnchor? anchor = null;
        var future0930 = futureTicks.Where(x => x.AvailableAt <= cutoff).OrderByDescending(x => x.AvailableAt).ThenByDescending(x => x.Id).Cast<CleanObserverTick?>().FirstOrDefault();
        if (future0930 is { } f0 && (cutoff - f0.AvailableAt).TotalSeconds <= 5d)
        {
            var wanted = descriptors.Select(d => d.Token).ToHashSet(StringComparer.Ordinal);
            var latest = new Dictionary<string, OptionQuoteSnapshot>(StringComparer.Ordinal);
            foreach (var (token, t) in preClean)
            {
                if (wanted.Contains(token) && t.AvailableAt <= cutoff)
                {
                    latest[token] = new OptionQuoteSnapshot(token, t.AvailableAt, t.Last, t.Bid, t.Ask, t.OpenInterest);
                }
            }

            anchor = OptionResidualModel.BuildAnchor(cutoff, weeklyExpiry, f0.Last, descriptors, latest, RiskFreeRate, 5d);
        }

        var freeze = new SessionFreeze(
            new AdaptiveSessionDefinition(day, future.Token, future.TradingSymbol, future.Expiry, future.LotSize, baseVolume),
            weeklyExpiry,
            RiskFreeRate,
            null,
            cutoff,
            open,
            descriptors,
            anchor,
            tickSizes,
            M1Identity.ModelVersion);

        var fingerprintText = new StringBuilder();
        fingerprintText.Append(CultureInfo.InvariantCulture, $"{day:yyyy-MM-dd}|{weeklyExpiry:yyyy-MM-dd}|{future.Token}|{future.LotSize}|{baseVolume}|{openingVolume}|{covered}|{cutoff:O}|{open:O}|{freeze.ModelVersion}\n");
        foreach (var o in descriptors.OrderBy(d => d.Token, StringComparer.Ordinal))
        {
            fingerprintText.Append(CultureInfo.InvariantCulture, $"{o.Token}|{o.OptionType}|{o.Strike:R}|{o.ExpiryDate:yyyy-MM-dd}|{o.LotSize}|{tickSizes[o.Token]}\n");
        }

        if (anchor is not null)
        {
            fingerprintText.Append(CultureInfo.InvariantCulture, $"anchor|{anchor.CenterStrike:R}|{anchor.Future0930:R}|{anchor.SyntheticUnderlying0930:R}\n");
            foreach (var c in anchor.Calls.Concat(anchor.Puts).OrderBy(c => c.Instrument.Token, StringComparer.Ordinal))
            {
                fingerprintText.Append(CultureInfo.InvariantCulture, $"{c.Instrument.Token}|{c.Price0930:R}|{c.ImpliedVolatility0930:R}\n");
            }
        }

        var report = new M1FreezeReport(
            day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            weeklyExpiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            covered,
            openingVolume,
            baseVolume,
            cutoff.ToString("O", CultureInfo.InvariantCulture),
            open.ToString("O", CultureInfo.InvariantCulture),
            descriptors.Count,
            anchor?.CenterStrike.ToString("R", CultureInfo.InvariantCulture),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintText.ToString()))).ToLowerInvariant());
        return new M1Freeze(freeze, report, decision);
    }
}
