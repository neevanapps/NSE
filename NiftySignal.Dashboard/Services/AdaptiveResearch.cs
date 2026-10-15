using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserver.Commentary;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain;
using Npgsql;

namespace NiftySignal.Dashboard.Services;

/// <summary>One durable event shown in the RECENT EVENTS strip (a persisted commentary event or a ledger open / close).</summary>
public sealed record ResearchEvent(string Id, DateTimeOffset AtUtc, string Kind, string Text);

/// <summary>Everything the research identity header, the data-quality line and the event strip show. Every field is as-of the displayed completed boundary.</summary>
public sealed record ResearchModel(
    DateOnly TradeDate,
    int BarSeq,
    DateTimeOffset? BoundaryUtc,
    string ModelVersion,
    string ClockText,
    DateOnly? Expiry,
    double? SelectedStrike,
    double? Center,
    bool AutoFollow,
    string DataQuality,
    IReadOnlyList<string> DataQualityIssues,
    string StrategyState,
    IReadOnlyList<ResearchEvent> Events,
    string EventPolicy,
    string BuildText,
    bool Pinned);

/// <summary>The pinned view of one completed bar: the whole snapshot, the fixed-strike history of the strike the bar's own center selects, and the research model describing it.</summary>
public sealed record PinnedResearchView(AdaptiveObserverSnapshot Snapshot, FixedStrikeCatalog? Catalog, FixedStrikeHistory? Fixed, ResearchModel Model);

public static class ResearchFormat
{
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    public static string IstTime(DateTimeOffset at) => at.ToOffset(Ist).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Strike(double? strike) => strike is { } s ? s.ToString("0", CultureInfo.InvariantCulture) : "—";

    public static string ShortStrategy(string strategyId) => strategyId.StartsWith("NPT1-", StringComparison.Ordinal) ? strategyId[5..] : strategyId;
}

/// <summary>
/// Factual data-quality line. It lists what is actually missing or known-invalid in the DISPLAYED range; it never computes a composite health score and never says "OK" while a known-invalid
/// projection, a missing fixed-strike row or an unavailable pinned quote exists in range.
/// </summary>
public static class ResearchDataQuality
{
    public static IReadOnlyList<string> Evaluate(AdaptiveObserverSnapshot snapshot, FixedStrikeHistory? fixedHistory, bool quoteUnavailable, bool commentaryDegraded)
    {
        var issues = new List<string>();
        var invalid = snapshot.InvalidSupplementalBarCount;
        if (invalid > 0)
        {
            issues.Add($"{invalid} projection(s) failed verification");
        }

        var staleBasis = (snapshot.BasisSupplemental ?? new Dictionary<int, AdaptiveBasisSupplementalRow>()).Values
            .Count(b => !string.Equals(b.Status, FuturesBasisBar.StatusUsable, StringComparison.Ordinal));
        if (staleBasis > 0)
        {
            issues.Add($"Basis stale/unavailable in {staleBasis} bar(s)");
        }

        if (fixedHistory is not null)
        {
            var missing = fixedHistory.Rows.Sum(r => (r.Ce is null && fixedHistory.Contract.CeToken is not null ? 1 : 0) + (r.Pe is null && fixedHistory.Contract.PeToken is not null ? 1 : 0));
            if (missing > 0)
            {
                issues.Add($"{missing} unavailable fixed-strike option row(s)");
            }
        }

        // After the 09:30 anchor exists, an unavailable residual row is missing input (before it, absence is by design and is not reported here).
        var residualGap = snapshot.Residuals.Count(r => !r.IsAvailable && snapshot.Session.Future0930.HasValue && r.EndAvailableAtUtc >= snapshot.Session.OpeningWindowEndUtc);
        if (residualGap > 0)
        {
            issues.Add($"{residualGap} residual row(s) unavailable");
        }

        if (quoteUnavailable)
        {
            issues.Add("pinned quote unavailable");
        }

        if (commentaryDegraded)
        {
            issues.Add("commentary degraded");
        }

        if (AdaptiveFixedClockV1.IsFixedClock(snapshot.Session.ModelVersion) && snapshot.Session.StrongThreshold is null)
        {
            issues.Add("Strong/Weak uncalibrated");
        }

        return issues;
    }

    public static string Text(IReadOnlyList<string> issues) => issues.Count == 0 ? "Data: OK" : "Data: " + string.Join(" | ", issues);
}

/// <summary>Durable events and the as-of strategy state. Only rows whose own timestamp is at or before the boundary are ever eligible.</summary>
public static class ResearchEvents
{
    public const int LatestCount = 3;
    public const int SincePreviousCap = 6;

    public static ResearchEvent FromCommentary(AdaptiveCommentaryEventRow e) => new(
        $"c:{e.Id}", e.OccurredAtUtc, "Commentary",
        $"Commentary {e.Lifecycle.ToString().ToUpperInvariant()} {CommentaryRenderer.Title(e.EventType)} | Bias {e.EventBias.ToString().ToUpperInvariant()} | {e.MarketRegime.ToString().ToUpperInvariant()} | {e.EvidenceAgreement.ToString().ToUpperInvariant()}");

    /// <summary>Durable NPT1 open / close events of the ledger (the three paper strategies only).</summary>
    public static IEnumerable<ResearchEvent> FromLedger(PaperLiveView? view)
    {
        if (view is null)
        {
            yield break;
        }

        foreach (var p in view.Open.Where(p => p.StrategyId.StartsWith("NPT1-", StringComparison.Ordinal)))
        {
            yield return new ResearchEvent($"o:{p.PositionId}", p.EntryAt, "Trade", $"{ResearchFormat.ShortStrategy(p.StrategyId)} OPEN {p.OptionType} {Strike(p.Strike)} @ {p.AvgEntryPrice}");
        }

        foreach (var c in view.Closed.Where(c => c.StrategyId.StartsWith("NPT1-", StringComparison.Ordinal)))
        {
            yield return new ResearchEvent($"o:{c.PositionId}", c.EntryAt, "Trade", $"{ResearchFormat.ShortStrategy(c.StrategyId)} OPEN {c.OptionType} {Strike(c.Strike)} @ {c.AvgEntryPrice}");
            yield return new ResearchEvent($"x:{c.PositionId}", c.ExitAt, "Trade", $"{ResearchFormat.ShortStrategy(c.StrategyId)} CLOSE {c.OptionType} {Strike(c.Strike)} | Net {SignedRupees(c.Net)} | {c.ExitReason}");
        }
    }

    static string Strike(string? s) => string.IsNullOrEmpty(s) ? "—" : s;

    static string SignedRupees(string net) => decimal.TryParse(net, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)
        ? (v >= 0 ? "+" : "-") + "₹" + Math.Abs(v).ToString("N0", CultureInfo.InvariantCulture) : net;

    /// <summary>
    /// Events strictly after <paramref name="sinceUtc"/> (the previous scheduled capture's boundary) up to and INCLUDING the boundary, newest <see cref="SincePreviousCap"/>;
    /// without a known previous capture, the latest <see cref="LatestCount"/> at or before the boundary. No event after the boundary can ever be returned.
    /// </summary>
    public static (IReadOnlyList<ResearchEvent> Events, string Policy) Select(IEnumerable<ResearchEvent> all, DateTimeOffset boundaryUtc, DateTimeOffset? sinceUtc)
    {
        var eligible = all.Where(e => e.AtUtc <= boundaryUtc).GroupBy(e => e.Id).Select(g => g.First());
        if (sinceUtc is { } since)
        {
            var window = eligible.Where(e => e.AtUtc > since).OrderByDescending(e => e.AtUtc).ThenBy(e => e.Id).Take(SincePreviousCap).OrderBy(e => e.AtUtc).ThenBy(e => e.Id).ToList();
            return (window, $"events since the previous scheduled capture ({ResearchFormat.IstTime(since)} IST) up to this boundary, newest {SincePreviousCap} at most");
        }

        var latest = eligible.OrderByDescending(e => e.AtUtc).ThenBy(e => e.Id).Take(LatestCount).OrderBy(e => e.AtUtc).ThenBy(e => e.Id).ToList();
        return (latest, $"latest {LatestCount} events at or before this boundary");
    }

    /// <summary>
    /// State of the three NPT1 paper strategies at the boundary, reconstructed ONLY from durable ledger rows of that trade date: a position is held at the boundary when it was entered at or before it
    /// and was still open (or exited after it). A date without a recorded run, or no readable ledger, gives "strategy state unavailable": the CURRENT state is never substituted.
    /// </summary>
    public static string StrategyStateAsOf(PaperLiveView? view, DateOnly day, DateTimeOffset boundaryUtc)
    {
        if (view is null || view.Status.TradeDate != day)
        {
            return "strategy state unavailable";
        }

        string State(string id)
        {
            var held = view.Open.Where(p => p.StrategyId == id && p.EntryAt <= boundaryUtc).Select(p => p.OptionType)
                .Concat(view.Closed.Where(c => c.StrategyId == id && c.EntryAt <= boundaryUtc && c.ExitAt > boundaryUtc).Select(c => c.OptionType)).FirstOrDefault();
            return string.IsNullOrEmpty(held) ? "FLAT" : held;
        }

        return string.Join(" | ", PaperTodayProjection.Strategies.Select(id => $"{ResearchFormat.ShortStrategy(id)}: {State(id)}"));
    }
}

public static class ResearchCaption
{
    /// <summary>Compact, secret-free Telegram caption that describes the research view (fixed-strike CORE DIRECTION / OPTION CONTEXT), not the legacy BOTH / NOTIONAL / ATM±2 selector.</summary>
    public static string Build(AdaptiveScreenshotJobRow job, ResearchModel? m, string dashboardSha)
    {
        var ist = TimeSpan.FromHours(5.5);
        var head = $"NIFTY Research | {job.TradeDate.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture)} | {job.Kind}";
        if (m is null)
        {
            return $"{head}\nBar {job.TargetBarSeq} | research context unavailable\ncompleted rows only\nDashboard SHA {dashboardSha}";
        }

        var boundary = m.BoundaryUtc is { } b ? $"{b.ToOffset(ist).ToString("HH:mm:ss", CultureInfo.InvariantCulture)} IST" : "—";
        var expiry = m.Expiry is { } e ? e.ToString("dd-MMM", CultureInfo.InvariantCulture) : "—";
        return $"{head}\nBar {m.BarSeq} @ {boundary}\n{m.ClockText}\nExpiry {expiry} | Strike {ResearchFormat.Strike(m.SelectedStrike)} | Auto Follow {(m.AutoFollow ? "ON" : "OFF")}\ncompleted rows only\n{m.DataQuality}\nDashboard SHA {dashboardSha}";
    }
}

/// <summary>
/// Builds the research model of a snapshot. Reads durable sources only (commentary tables, the trading ledger, the screenshot outbox) and is pinned: with <c>pinned</c> every value comes from rows at or
/// before the target bar's boundary.
/// </summary>
public sealed class AdaptiveResearchService(
    IDbContextFactory<AdaptiveObserverDbContext> dbFactory,
    AdaptiveObserverDataService data,
    PaperLiveDataService? paper = null,
    AdaptiveQuoteAsOfService? quotes = null)
{
    public async Task<PinnedResearchView?> LoadPinnedAsync(long sessionId, int barSeq, CancellationToken ct = default)
    {
        var snapshot = await data.LoadSnapshotAsync(10, ct, captureSessionId: sessionId, throughBarSeq: barSeq > 0 ? barSeq : null);
        if (snapshot is null)
        {
            return null;
        }

        var catalog = FixedStrikeCatalog.FromSession(snapshot.Session);
        var latest = snapshot.Options.FirstOrDefault();
        var center = latest?.Call?.CenterStrike ?? latest?.Put?.CenterStrike;
        // A screenshot is rendered by the server, not by a person: it always shows Auto follow ON, i.e. the strike that THIS bar's own center selects.
        var selected = catalog is null || center is null ? (double?)null : catalog.Strikes.Any(s => s.Strike == center) ? center : catalog.Nearest(center.Value);
        FixedStrikeHistory? fixedHistory = null;
        if (catalog is not null && selected is { } strike)
        {
            var bars = snapshot.Futures.Select(x => (x.Bar.BarSeq, x.Bar.EndAvailableAtUtc)).ToList();
            fixedHistory = await data.LoadFixedStrikeHistoryAsync(snapshot.Session.Id, catalog, strike, bars, ct, snapshot.Session.RollingWindowBars);
        }

        var quoteUnavailable = false;
        if (quotes is not null)
        {
            try
            {
                var pinned = await quotes.LoadAsync(sessionId, barSeq, ct);
                quoteUnavailable = pinned.UnavailableReason is not null || pinned.Spot is null || pinned.Future is null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                quoteUnavailable = true;
            }
        }

        var model = await BuildAsync(snapshot, fixedHistory, selected, center, autoFollow: true, pinned: true, quoteUnavailable, ct);
        return new PinnedResearchView(snapshot, catalog, fixedHistory, model);
    }

    public async Task<ResearchModel> BuildAsync(
        AdaptiveObserverSnapshot snapshot, FixedStrikeHistory? fixedHistory, double? selectedStrike, double? center, bool autoFollow, bool pinned,
        bool quoteUnavailable = false, CancellationToken ct = default)
    {
        var session = snapshot.Session;
        var newest = snapshot.Futures.FirstOrDefault()?.Bar;
        var barSeq = newest?.BarSeq ?? 0;
        var boundary = newest?.EndAvailableAtUtc;
        var (events, policy, degraded) = boundary is { } b ? await LoadEventsAsync(session, barSeq, b, pinned, ct) : ((IReadOnlyList<ResearchEvent>)[], "no completed bar yet", false);
        var ledger = paper is { Configured: true } ? await SafeLedgerAsync(session.TradeDate, ct) : null;
        var ledgerEvents = ResearchEvents.FromLedger(ledger);
        var all = events.Concat(ledgerEvents);
        var sinceUtc = pinned && boundary is { } bb ? await PreviousCaptureBoundaryAsync(session.Id, barSeq, ct) : null;
        var (shown, selectionPolicy) = boundary is { } bd ? ResearchEvents.Select(all, bd, sinceUtc) : ((IReadOnlyList<ResearchEvent>)[], policy);
        var issues = ResearchDataQuality.Evaluate(snapshot, fixedHistory, quoteUnavailable, degraded);
        var build = BuildIdentity.Current();
        return new ResearchModel(
            session.TradeDate, barSeq, boundary, session.ModelVersion, AdaptiveFixedClockV1.Describe(session.ModelVersion),
            session.WeeklyOptionExpiry, selectedStrike, center, autoFollow,
            ResearchDataQuality.Text(issues), issues,
            boundary is { } sb ? ResearchEvents.StrategyStateAsOf(ledger, session.TradeDate, sb) : "strategy state unavailable",
            shown, selectionPolicy,
            $"{session.ModelVersion} | session source {ShortSha(session.SourceCommitSha)} | Dashboard {ShortSha(build.CommitSha)}",
            pinned);
    }

    static string ShortSha(string? sha) => string.IsNullOrEmpty(sha) ? "—" : sha.Length > 8 ? sha[..8] : sha;

    async Task<PaperLiveView?> SafeLedgerAsync(DateOnly day, CancellationToken ct)
    {
        try
        {
            return await paper!.LoadLatestAsync(day, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The boundary of the previous screenshot job of the same session (an earlier target bar), when one exists.</summary>
    async Task<DateTimeOffset?> PreviousCaptureBoundaryAsync(long sessionId, int barSeq, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var previous = await db.ScreenshotJobs.AsNoTracking()
                .Where(j => j.SessionId == sessionId && j.Kind != AdaptiveScreenshotKind.PreLiveTest && j.TargetBarSeq > 0 && j.TargetBarSeq < barSeq)
                .OrderByDescending(j => j.TargetBarSeq).Select(j => (int?)j.TargetBarSeq).FirstOrDefaultAsync(ct);
            if (previous is null)
            {
                return null;
            }

            return await db.FutureBars.AsNoTracking().Where(b => b.SessionId == sessionId && b.BarSeq == previous).Select(b => (DateTimeOffset?)b.EndAvailableAtUtc).SingleOrDefaultAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return null;
        }
    }

    async Task<(IReadOnlyList<ResearchEvent> Events, string Policy, bool Degraded)> LoadEventsAsync(AdaptiveSessionStateRow session, int barSeq, DateTimeOffset boundary, bool pinned, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var version = CommentaryEvaluator.Version;
            var health = await db.ProjectionHealth.AsNoTracking()
                .Where(x => x.SessionId == session.Id && x.Component == AdaptiveProjectionComponents.Commentary && x.Version == version).AnyAsync(ct);
            var runtime = await db.CommentaryRuntime.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == session.Id && x.CommentaryVersion == version, ct);
            var verifiedThrough = runtime?.LastEvaluatedBarSeq ?? 0;
            var query = db.CommentaryEvents.AsNoTracking().Where(x => x.SessionId == session.Id && x.CommentaryVersion == version && x.BarSeq <= barSeq && x.OccurredAtUtc <= boundary);
            if (health)
            {
                query = query.Where(x => x.BarSeq <= verifiedThrough);          // quarantined rows are never shown as current
            }

            var rows = await query.OrderByDescending(x => x.BarSeq).ThenByDescending(x => x.Id).Take(40).ToListAsync(ct);
            return (rows.Select(ResearchEvents.FromCommentary).ToList(), "durable commentary events", health);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return ([], "commentary tables unavailable", false);
        }
    }
}
