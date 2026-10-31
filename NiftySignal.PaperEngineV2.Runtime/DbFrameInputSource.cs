using Microsoft.EntityFrameworkCore;
using Npgsql;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;
using NiftySignal.MarketSummary;
using NiftySignal.Persistence;

namespace NiftySignal.PaperEngineV2.Runtime;

/// <summary>
/// Assembles a bar's Market Summary inputs from the rows the Observer has ALREADY persisted and verified (futures bars, futures sidecar, per-strike option bars, the India VIX ticks) - the same sources and rules as
/// the Dashboard's Market Summary (v3/v2/v1 per-strike precedence, invalid-sidecar exclusion, as-of VIX with the 60 s freshness limit), pinned at the notice's bar so nothing after it can be read. Read-only.
/// A missing table or row leaves a coverage gap in the summary; it is never filled from another bar, strike or fabricated value.
/// </summary>
public sealed class DbFrameInputSource(Func<AdaptiveObserverDbContext> observerDb, Func<NiftySignalDbContext> sourceDb) : IV2FrameInputSource
{
    public async Task<(V2FrameInputs? Inputs, string? Problem)> LoadAsync(V2BarNotice notice, CancellationToken ct)
    {
        var session = notice.Session;
        if (notice.CenterStrike is not { } centre) return (null, "no centre strike for this bar");
        if (session.Strikes.Count == 0) return (null, "no frozen option universe for this session");

        var basket = BasketResolver.Resolve(session.Expiry, centre, session.Strikes);
        var tokens = basket.Contracts.Where(c => c.Token is not null).Select(c => c.Token!).ToHashSet(StringComparer.Ordinal);
        var endBar = notice.Bar.BarSeq;
        List<AdaptiveFutureBarRow> bars;
        Dictionary<int, AdaptiveFuturesSupplementalRow> sup;
        List<AdaptiveOptionStrikeBarRow> stored = [];
        await using (var db = observerDb())
        {
            bars = await db.FutureBars.AsNoTracking().Where(x => x.SessionId == session.SessionId && x.BarSeq <= endBar).OrderBy(x => x.BarSeq).ToListAsync(ct);
            var seqs = bars.Select(x => x.BarSeq).ToArray();
            if (!bars.Any(b => b.BarSeq == endBar)) return (null, $"bar {endBar} is not persisted yet");
            var invalid = await InvalidBarsAsync(db, session.SessionId, AdaptiveProjectionComponents.FuturesSupplemental, FuturesMicrostructureBar.MetricsVersion, seqs, ct);
            sup = (await FuturesSupplementalAsync(db, session.SessionId, seqs, ct)).Where(x => !invalid.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
            if (tokens.Count > 0)
            {
                var strikeArray = basket.Contracts.Where(c => c.Strike is not null).Select(c => c.Strike!.Value).Distinct().ToArray();
                var expiry = session.Expiry;
                try
                {
                    stored = await db.OptionStrikeBars.AsNoTracking()
                        .Where(x => x.SessionId == session.SessionId && x.Expiry == expiry && strikeArray.Contains(x.Strike) && x.BarSeq <= endBar)
                        .ToListAsync(ct);
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { }
            }
        }

        var futuresBars = bars.ToDictionary(b => b.BarSeq, b => FuturesBarOf(b, sup.GetValueOrDefault(b.BarSeq)));
        var history = new ContractHistory(ContractBars(stored, tokens, session.Expiry, endBar), bars.ToDictionary(b => b.BarSeq, b => b.DurationSeconds));
        var startOf = bars.ToDictionary(b => b.BarSeq, b => b.StartAvailableAtUtc);
        var endOf = bars.ToDictionary(b => b.BarSeq, b => b.EndAvailableAtUtc);
        var instants = SummaryWindow.All.Select(w =>
        {
            var from = w.Bars is { } n ? endBar - n + 1 : 1;
            return new WindowInstant(w, from >= 1 && startOf.TryGetValue(from, out var s) ? s : null, endOf.TryGetValue(endBar, out var e) ? e : null);
        }).ToList();

        var futures = FuturesSummaryCalculator.Compute(futuresBars, endBar);
        var grids = OptionSummaryCalculator.ComputeAll(basket, history, endBar);
        var vix = await VixAsync(session.TradeDate, instants, ct);
        return (new V2FrameInputs(basket, futures, grids, vix), null);
    }

    async Task<IReadOnlyList<VixWindowSummary>?> VixAsync(DateOnly tradeDate, IReadOnlyList<WindowInstant> instants, CancellationToken ct)
    {
        var starts = instants.Where(i => i.Start is not null).Select(i => i.Start!.Value).ToList();
        var ends = instants.Where(i => i.End is not null).Select(i => i.End!.Value).ToList();
        if (starts.Count == 0 || ends.Count == 0) return null;
        try
        {
            await using var db = sourceDb();
            var tokens = await db.Instruments.AsNoTracking().Where(i => i.AsOfDate == tradeDate && i.InstrumentType == InstrumentType.Vix).Select(i => i.Token).Distinct().ToListAsync(ct);
            if (tokens.Count != 1) return null;                       // no unique India VIX: the frame says "Unavailable" (never a guessed instrument)
            var token = tokens[0];
            var lo = starts.Min() - VixSummaryCalculator.FreshnessLimit;
            var hi = ends.Max();
            var rows = await db.Ticks.AsNoTracking()
                .Where(t => t.Token == token && t.LastPrice > 0m && t.ExchangeTimestamp >= lo && t.ExchangeTimestamp <= hi && t.ReceivedAt <= hi)
                .Select(t => new { t.ExchangeTimestamp, t.ReceivedAt, t.LastPrice, t.Id }).ToListAsync(ct);
            var observations = rows
                .Select(t => (At: t.ReceivedAt < t.ExchangeTimestamp ? t.ExchangeTimestamp : t.ReceivedAt, t.Id, Value: (double)t.LastPrice))
                .OrderBy(x => x.At).ThenBy(x => x.Id)
                .Select(x => new VixObservation(x.At, x.Value)).ToList();
            return VixSummaryCalculator.Compute(observations, instants, VixSummaryCalculator.FreshnessLimit);
        }
        catch (PostgresException) { return null; }
    }

    static int VersionRank(string version) =>
        version == OptionStrikeBarMetrics.MetricsVersion ? 3 : version == OptionStrikeBarMetrics.PreviousMetricsVersion ? 2 : version == OptionStrikeBarMetrics.LegacyMetricsVersion ? 1 : 0;

    internal static IReadOnlyList<ContractBar> ContractBars(IEnumerable<AdaptiveOptionStrikeBarRow> rows, IReadOnlySet<string> tokens, DateOnly expiry, int throughBarSeq) =>
        rows.Where(r => r.BarSeq >= 1 && r.BarSeq <= throughBarSeq && r.Expiry == expiry && tokens.Contains(r.Token) && VersionRank(r.MetricsVersion) > 0)
            .GroupBy(r => (r.BarSeq, r.Token))
            .Select(g => g.OrderByDescending(r => VersionRank(r.MetricsVersion)).First())
            .Select(r => new ContractBar(
                r.BarSeq, r.Token, r.Side == OptionType.Call,
                r.PriceStart, r.PriceEnd,
                r.TotalQuantity, r.StrictDelta, r.EnrichedBuy, r.EnrichedSell, r.EnrichedUnknown, r.EnrichedDelta,
                r.OiStart, r.OiEnd, r.IvStart, r.IvEnd,
                r.DeltaStart, r.DeltaEnd, r.VegaStart, r.VegaEnd,
                r.TobTimeWeighted, r.MicroDevTimeWeighted, r.TobUsableSeconds,
                r.NetOfi, r.GrossOfi))
            .ToList();

    internal static FuturesBar FuturesBarOf(AdaptiveFutureBarRow bar, AdaptiveFuturesSupplementalRow? s) => new(
        bar.BarSeq, bar.DurationSeconds, bar.Open, bar.Close, bar.Volume, bar.StrictDelta,
        bar.EnrichedBuyVolume, bar.EnrichedSellVolume, bar.EnrichedUnknownVolume, bar.EnrichedDelta, bar.OiOpen, bar.OiClose,
        s?.TobTimeWeighted, s?.MicroDevTimeWeighted, s?.TobUsableSeconds, s?.MicroDevUsableSeconds, s?.Ofi, s?.GrossOfi);

    static async Task<IReadOnlySet<int>> InvalidBarsAsync(AdaptiveObserverDbContext db, long sessionId, string component, string version, int[] seqs, CancellationToken ct)
    {
        if (seqs.Length == 0) return new HashSet<int>();
        try
        {
            var bars = await db.ProjectionHealth.AsNoTracking()
                .Where(x => x.SessionId == sessionId && x.Component == component && x.Version == version && seqs.Contains(x.BarSeq))
                .Select(x => x.BarSeq).ToListAsync(ct);
            return bars.ToHashSet();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { return new HashSet<int>(); }
    }

    static async Task<IReadOnlyDictionary<int, AdaptiveFuturesSupplementalRow>> FuturesSupplementalAsync(AdaptiveObserverDbContext db, long sessionId, int[] seqs, CancellationToken ct)
    {
        if (seqs.Length == 0) return new Dictionary<int, AdaptiveFuturesSupplementalRow>();
        try
        {
            var version = FuturesMicrostructureBar.MetricsVersion;
            var legacy = FuturesMicrostructureBar.LegacyMetricsVersion;
            var rows = await db.FuturesSupplemental.AsNoTracking()
                .Where(x => x.SessionId == sessionId && (x.MetricsVersion == version || x.MetricsVersion == legacy) && seqs.Contains(x.BarSeq))
                .ToListAsync(ct);
            return rows.GroupBy(x => x.BarSeq).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.MetricsVersion == version).First());
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { return new Dictionary<int, AdaptiveFuturesSupplementalRow>(); }
    }
}
