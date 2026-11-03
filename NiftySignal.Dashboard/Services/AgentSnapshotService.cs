using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain;
using NiftySignal.Domain.Enums;
using NiftySignal.MarketSummary;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// The read-only agent feed ("agent-v1"). It is a THIN PROJECTION of what the Dashboard grids already render: the same loaders (<see cref="AdaptiveObserverDataService"/>, day-average data, fixed-strike
/// history), the same ratio builders (<see cref="FuturesRatios"/>, <see cref="OptionDirectionRatios"/>, <see cref="AdaptiveResidualProjection"/>) and the same as-of quote and VIX rules. Nothing is recalculated here,
/// and only COMPLETED bars (up to the runtime's LastCompletedBarSeq) are ever read: the live incomplete bar has no representation in this feed.
/// Each bar uses the fixed strike that ITS OWN completed center selects (the same rule as the pinned Telegram view), so a bar's line never changes after the fact.
/// </summary>
public sealed class AgentSnapshotService(
    AdaptiveObserverDataService data,
    AdaptiveQuoteAsOfService quotes,
    VixSummaryService vix,
    IDbContextFactory<AdaptiveObserverDbContext> dbFactory)
{
    public const int MinBars = 1;
    public const int MaxBars = 300;
    public const int DefaultBars = 20;

    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    static readonly SummaryWindow OneBar = new("bar", 1);

    static readonly string[] Notes =
    [
        "Raw values only: microDevRaw is NOT inverted and delta / vega are NOT scaled x100 (the Dashboard's '(Inv)' and 'x100' are display conventions).",
        "Units: ivPoints = IV points; vegaPer1Vol = vega per 1.00 vol point; delta unit-less; ratioPct = numerator / denominator x 100 as in the Dashboard cell; times are IST (+05:30).",
        "Direction = CE - PE for every metric except oi, where it is PE - CE.",
        "Each bar uses the fixed strike selected by its own completed center (options.strike); options.ce / options.pe bid and ask are the top of book of the latest tick available at the bar's end boundary (null when that tick had no depth).",
        "A value the data contract does not have for a bar is null, never 0. dataOk is false for a bar when basis, spot, VIX, the fixed-strike rows, the futures microstructure or the residual is stale or missing.",
    ];

    /// <summary>The last <paramref name="bars"/> COMPLETED bars of today's adaptive session (oldest first).</summary>
    public async Task<AgentSnapshotResponse> SnapshotAsync(int bars, CancellationToken ct = default)
    {
        var header = await data.LoadHeaderAsync(ct);
        if (header is null)
        {
            return Empty(null, "no adaptive session today");
        }

        var last = header.Runtime.LastCompletedBarSeq;
        if (last <= 0)
        {
            return Empty(header.Session, "no completed bar yet");
        }

        var from = Math.Max(1, last - bars + 1);
        var built = await BuildBarsAsync(header.Session, last, Enumerable.Range(from, last - from + 1).ToList(), ct);
        return Response(header.Session, last, built);
    }

    /// <summary>Builds the lines of the given completed bars (all must be &lt;= <paramref name="throughBarSeq"/>, the runtime's last completed bar), oldest first. A bar that does not exist is skipped.</summary>
    public async Task<IReadOnlyList<AgentBar>> BuildBarsAsync(AdaptiveSessionStateRow session, int throughBarSeq, IReadOnlyCollection<int> barSeqs, CancellationToken ct = default)
    {
        var wanted = barSeqs.Where(s => s >= 1 && s <= throughBarSeq).ToHashSet();
        if (wanted.Count == 0)
        {
            return [];
        }

        var catalog = FixedStrikeCatalog.FromSession(session);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var anchors = await db.ResidualAnchorComponents.AsNoTracking().Where(x => x.SessionId == session.Id).OrderBy(x => x.Side).ThenBy(x => x.Strike).ToListAsync(ct);
        var day = await data.LoadDayAverageDataAsync(session.Id, throughBarSeq, catalog, null, session.RollingWindowBars, anchors, ct);
        if (day is null)
        {
            return [];
        }

        var rows = day.Futures.Where(f => wanted.Contains(f.Bar.BarSeq)).OrderBy(f => f.Bar.BarSeq).ToList();
        var seqs = rows.Select(f => f.Bar.BarSeq).ToArray();
        if (seqs.Length == 0)
        {
            return [];
        }

        // The center of each bar (Call row first, like the Dashboard) and the strike it selects: the center when the frozen universe has it, else the nearest offered strike.
        var bands = await db.OptionBandBars.AsNoTracking().Where(x => x.SessionId == session.Id && seqs.Contains(x.BarSeq)).Select(x => new { x.BarSeq, x.Side, x.CenterStrike }).ToListAsync(ct);
        var centerBySeq = bands.GroupBy(x => x.BarSeq).ToDictionary(g => g.Key,
            g => (g.FirstOrDefault(x => x.Side == OptionType.Call && x.CenterStrike > 0) ?? g.FirstOrDefault(x => x.CenterStrike > 0)) is { } r ? (double?)r.CenterStrike : null);
        double? Selected(int seq)
        {
            if (catalog is null || !centerBySeq.TryGetValue(seq, out var center) || center is not { } c)
            {
                return null;
            }

            return catalog.Strikes.Any(s => s.Strike == c) ? c : catalog.Nearest(c);
        }

        var selectedBySeq = seqs.ToDictionary(s => s, Selected);
        var endBySeq = rows.ToDictionary(f => f.Bar.BarSeq, f => f.Bar.EndAvailableAtUtc);

        // One fixed-strike history per distinct strike, restricted to the bars that selected it (a roll window is never bridged across contracts).
        var fixedBySeq = new Dictionary<int, FixedStrikeRow>();
        foreach (var group in selectedBySeq.Where(x => x.Value is not null).GroupBy(x => x.Value!.Value))
        {
            var history = await data.LoadFixedStrikeHistoryAsync(session.Id, catalog!, group.Key, group.Select(x => (x.Key, endBySeq[x.Key])).ToList(), ct, session.RollingWindowBars);
            foreach (var row in history?.Rows ?? [])
            {
                fixedBySeq[row.BarSeq] = row;
            }
        }

        var requests = rows.Select(f =>
        {
            var contract = selectedBySeq[f.Bar.BarSeq] is { } k ? catalog?.Find(k) : null;
            return new AdaptiveQuoteAsOfService.BoundaryRequest(f.Bar.BarSeq, f.Bar.EndAvailableAtUtc, contract?.CeToken, contract?.PeToken);
        }).ToList();
        var boundary = await quotes.LoadBoundaryQuotesAsync(session, requests, ct);

        var vixWindows = (await vix.LoadAsync(session.TradeDate, rows.Select(f => new WindowInstant(OneBar, f.Bar.StartAvailableAtUtc, f.Bar.EndAvailableAtUtc)).ToList(), ct)).Windows;

        var variant = ResidualVariant.AtmPlusMinus2;
        var residualRows = day.Residuals.Where(x => x.Variant == variant).GroupBy(x => x.BarSeq).ToDictionary(g => g.Key, g => g.First());
        var bases = new ResidualBases(anchors.Where(x => x.Side == OptionType.Call).Sum(x => x.Price0930), anchors.Where(x => x.Side == OptionType.Put).Sum(x => x.Price0930));
        var derived = AdaptiveResidualProjection.Derive(residualRows.Values.Select(x => new ResidualReadingPoint(
            x.BarSeq, x.IsAvailable, x.CEResidual, x.PEResidual, x.CEResidualPct, x.PEResidualPct, x.DirectionalResidualPct)), bases).ToDictionary(x => x.BarSeq);

        var result = new List<AgentBar>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var f = rows[i];
            var seq = f.Bar.BarSeq;
            var contract = selectedBySeq[seq] is { } k ? catalog?.Find(k) : null;
            result.Add(Map(
                session, f, centerBySeq.GetValueOrDefault(seq), selectedBySeq[seq], catalog, contract,
                day.FuturesSupplemental.GetValueOrDefault(seq), day.Basis.GetValueOrDefault(seq), fixedBySeq.GetValueOrDefault(seq),
                boundary.GetValueOrDefault(seq), vixWindows is not null && i < vixWindows.Count ? vixWindows[i] : null,
                residualRows.GetValueOrDefault(seq), derived.GetValueOrDefault(seq)));
        }

        return result;
    }

    /// <summary>Pure mapping of already-loaded Dashboard rows into one agent bar. Public so the contract (raw values, nulls, dataOk) is unit-testable without a database.</summary>
    public static AgentBar Map(
        AdaptiveSessionStateRow session, AdaptiveFuturesGridRow futures, double? center, double? selectedStrike, FixedStrikeCatalog? catalog, FixedStrikeContract? contract,
        AdaptiveFuturesSupplementalRow? sup, AdaptiveBasisSupplementalRow? basis, FixedStrikeRow? fixedRow,
        AdaptiveQuoteAsOfService.BoundaryQuotes? quotes, VixWindowSummary? vix, AdaptiveOptionResidualBarRow? residual, ResidualDerivedPoint? residualDerived)
    {
        var b = futures.Bar;
        var r = futures.Rolling;
        var issues = new List<string>();

        var agentFutures = new AgentFutures(
            F(b.Close), F(b.BarPriceDisplacement), F(r?.PriceDisplacement), b.Volume, b.StrictDelta, b.EnrichedDelta,
            F(sup?.MicroDevTimeWeighted), sup?.Ofi, r?.OiChange, F(basis?.DeltaBasis), F(r?.Efficiency),
            r is null ? null : r.State.ToString(), r?.StrictDominanceEvolution);
        if (sup is null)
        {
            issues.Add("futures microstructure missing");
        }

        if (basis is null)
        {
            issues.Add("basis missing");
        }
        else if (!string.Equals(basis.Status, FuturesBasisBar.StatusUsable, StringComparison.Ordinal))
        {
            issues.Add($"basis {basis.Status}");
        }

        // Options: the fixed strike of this bar. A side without a stored row stays null and is never replaced by another strike.
        var ceRow = fixedRow?.Ce;
        var peRow = fixedRow?.Pe;
        var core = OptionDirectionRatios.Core(fixedRow ?? new FixedStrikeRow(b.BarSeq, b.EndAvailableAtUtc, null, null));
        var ctx = OptionDirectionRatios.Context(ceRow, peRow);
        var dir = new AgentDir(
            Ratios(core.Price), Ratios(core.RollPrice), Ratios(core.Strict), Ratios(core.Enriched), Ratios(core.Oi), Ratios(core.MicroDev), Ratios(core.Ofi),
            Ratios(ctx.Iv), Ratios(ctx.Activity), Ratios(ctx.Delta), Ratios(ctx.Vega));
        if (catalog is null || selectedStrike is null || contract is null)
        {
            issues.Add("fixed strike unavailable");
        }
        else
        {
            if (contract.CeToken is not null && ceRow is null) issues.Add("fixed-strike CE row missing");
            if (contract.PeToken is not null && peRow is null) issues.Add("fixed-strike PE row missing");
        }

        var options = new AgentOptions(
            catalog?.Expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), selectedStrike, center,
            Side(ceRow, contract?.CeToken, contract?.CeSymbol, quotes?.Ce, b.EndAvailableAtUtc),
            Side(peRow, contract?.PeToken, contract?.PeSymbol, quotes?.Pe, b.EndAvailableAtUtc),
            dir);

        // Residual (the Dashboard's default ATM +/- 2 band): before the 09:30 anchor exists an absent row is by design; after it, it is missing input.
        var anchorExists = session.Future0930.HasValue && b.EndAvailableAtUtc >= session.OpeningWindowEndUtc;
        var agentResidual = residual is { IsAvailable: true }
            ? new AgentResidual("atmPlusMinus2", true, null, F(residual.CEResidualPct), F(residual.PEResidualPct), F(residual.DirectionalResidualPct), F(residualDerived?.StraddleResidualPct),
                residual.ResidualDirection > 0 ? "UP" : residual.ResidualDirection < 0 ? "DOWN" : "NEUTRAL", residual.Relationship, F(residual.MaxQuoteAgeSeconds))
            : new AgentResidual("atmPlusMinus2", false, residual?.UnavailableReason ?? "residual row missing", null, null, null, null, null, null, null);
        if (!agentResidual.Available && anchorExists)
        {
            issues.Add("residual unavailable");
        }

        // Spot and VIX at the bar boundary. Spot older than the basis freshness rule (5 s), or VIX older than its own rule, is reported as stale, not carried.
        double? spot = quotes?.Spot is { } sq ? (double)sq.Quote.Ltp : null;
        double? spotAge = quotes?.Spot is { } sa ? Math.Max(0d, (b.EndAvailableAtUtc - sa.AvailableAtUtc).TotalSeconds) : null;
        if (spot is null)
        {
            issues.Add("spot missing");
        }
        else if (spotAge > FuturesBasisBar.SpotFreshnessSeconds)
        {
            issues.Add("spot stale");
        }

        var vixEnd = vix?.End;
        if (vixEnd is null || vixEnd.Status == VixStatus.Missing)
        {
            issues.Add("VIX missing");
        }
        else if (vixEnd.Status == VixStatus.Stale)
        {
            issues.Add("VIX stale");
        }

        var market = new AgentMarket(spot, F(spotAge), F(vixEnd?.Value), F(vix?.Change), F(vixEnd?.AgeSeconds));

        return new AgentBar(b.BarSeq, At(b.StartAvailableAtUtc), At(b.EndAvailableAtUtc), true, F(b.DurationSeconds), issues.Count == 0, issues, agentFutures, options, agentResidual, market);
    }

    static AgentOptionSide Side(AdaptiveOptionStrikeBarRow? row, string? token, string? symbol, PinnedQuote? quote, DateTimeOffset boundary) => new(
        token, symbol, row is not null,
        F(row?.PriceEnd), F(row?.PriceStart), F(row?.PriceChange),
        quote?.Quote.Bid is { } bid ? (double)bid : null, quote?.Quote.Ask is { } ask ? (double)ask : null,
        quote is null ? null : F(Math.Max(0d, (boundary - quote.AvailableAtUtc).TotalSeconds)),
        F(row?.DeltaEnd), F(row?.DeltaChange), F(row?.VegaEnd), F(row?.VegaChange), F(row?.IvEnd), F(row?.IvChange),
        row?.StrictDelta, row?.EnrichedDelta, row?.TotalQuantity, row?.OiDelta,
        F(row?.MicroDevTimeWeighted), row?.NetOfi, row?.GrossOfi, F(row?.ActivityPerSecond));

    static AgentSideRatios Ratios(SideDirection d) => new(Ratio(d.Ce), Ratio(d.Pe), Ratio(d.Direction));

    static AgentRatio Ratio(RatioPart p) => new(F(p.Raw), F(p.Ratio));

    /// <summary>Finite values only: NaN / infinity cannot be JSON and are unavailable anyway.</summary>
    static double? F(double? v) => v is { } x && double.IsFinite(x) ? x : null;

    static string At(DateTimeOffset at) => at.ToOffset(Ist).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    AgentSnapshotResponse Empty(AdaptiveSessionStateRow? session, string reason) => new(
        MetaFor(session, null, [], [reason]), []);

    AgentSnapshotResponse Response(AdaptiveSessionStateRow session, int latestCompleted, IReadOnlyList<AgentBar> bars) =>
        new(MetaFor(session, latestCompleted, bars, bars.Count == 0 ? ["no completed bar could be built"] : bars[^1].DataIssues), bars);

    static AgentMeta MetaFor(AdaptiveSessionStateRow? session, int? latestCompleted, IReadOnlyList<AgentBar> bars, IReadOnlyList<string> issues)
    {
        var latest = bars.Count == 0 ? null : bars[^1];
        var sha = BuildIdentity.Current().CommitSha;
        return new AgentMeta(
            AgentFeedJson.Schema, sha,
            session is null ? null : $"{session.SourceBranch} / {(session.SourceCommitSha.Length > 8 ? session.SourceCommitSha[..8] : session.SourceCommitSha)}",
            session is null ? null : new AgentClock(session.ModelVersion, AdaptiveFixedClockV1.IsFixedClock(session.ModelVersion) ? "fixedVolume" : "adaptive", session.BaseBarVolume,
                session.RollingWindowBars, AdaptiveFixedClockV1.Describe(session.ModelVersion)),
            session?.TradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            latest?.Options.Expiry, latest?.Options.Strike, latest?.Options.CenterStrike,
            latestCompleted, bars.Count, latest is { DataOk: true }, issues, Notes);
    }
}
