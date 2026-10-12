using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NiftySignal.Npt1M1;
using NiftySignal.Npt1M1.Data;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Data;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Dashboard.Services;

/// <summary>One audited step of a strategy: what it saw and decided (inputs, targets, readiness), contract ranking, fills, cancellations, exits, terminal protocol.</summary>
public sealed record Npt1Decision(DateTimeOffset At, string Stage, string Outcome, string Detail);

/// <summary>One NPT1 strategy, shown on its own: its position, its ledger (never summed with another strategy's) and its most recent audited steps.</summary>
public sealed record Npt1StrategyPanel(
    string StrategyId,
    string State,
    PaperOpenPosition? Open,
    IReadOnlyList<PaperClosedTrade> Closed,
    int Trades,
    int Wins,
    decimal Gross,
    decimal Fees,
    decimal Net,
    decimal? OpenCommitted,
    IReadOnlyList<Npt1Decision> Decisions);

/// <summary>Health of the NPT1 M1 producer (its own schema npt1m1): last heartbeat and bar, frozen base bar, throughput and any divergent frame.</summary>
public sealed record Npt1StreamHealth(
    string SessionId,
    string Status,
    DateTimeOffset LastHeartbeat,
    int LastBarSeq,
    DateTimeOffset? LastBarEnd,
    long? BaseBarVolume,
    string? BarSizeStatus,
    long? OpeningVolume,
    long Polls,
    double LastPollMillis,
    double MaxPollMillis,
    long RowsRead,
    long DivergentFrames,
    string? Detail);

public sealed record Npt1View(string RunId, DateOnly TradeDate, IReadOnlyList<Npt1StrategyPanel> Strategies, Npt1StreamHealth? Stream, string Note);

public sealed partial class PaperLiveDataService
{
    public const string Npt1Prefix = "NPT1-";

    private static readonly string[] AuditedStages = { "StrategyDecision", "ContractSelection", "SignalEntry", "NextBarEntry", "SignalExit", "TerminalClose", "Sizing" };

    /// <summary>
    /// The NPT1 strategies of the loaded run, each on its own, plus the M1 producer's health. Null when the run holds no NPT1 strategy. Read-only: the audit rows are decoded from the durable
    /// outputs (the ledger of record) and the stream health from <c>npt1m1.runtime</c>.
    /// </summary>
    public async Task<Npt1View?> LoadNpt1Async(PaperLiveView view, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        var ids = view.Variants.Select(v => v.Scope.Split('/')[0]).Concat(view.Open.Select(p => p.StrategyId)).Concat(view.Closed.Select(c => c.StrategyId))
            .Where(s => s.StartsWith(Npt1Prefix, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (factory is null || ids.Count == 0)
        {
            return null;
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var runId = view.Status.RunId;
        var panels = new List<Npt1StrategyPanel>();
        foreach (var id in ids)
        {
            var rows = await db.Outputs.Where(o => o.RunId == runId && o.Type == nameof(AuditRecord) && o.StrategyId == id).OrderByDescending(o => o.Seq).Take(60).Select(o => o.Payload).ToListAsync(ct);
            var decisions = rows.Select(p => (AuditRecord)OutputCodec.Decode(p)).Where(a => AuditedStages.Contains(a.Stage, StringComparer.Ordinal))
                .Take(16).Select(a => new Npt1Decision(a.At, a.Stage, a.Outcome, a.Detail)).ToList();
            var open = view.Open.FirstOrDefault(p => p.StrategyId == id);
            var closed = view.Closed.Where(c => c.StrategyId == id).ToList();
            var gross = closed.Sum(c => decimal.Parse(c.Gross, CultureInfo.InvariantCulture));
            var fees = closed.Sum(c => decimal.Parse(c.Costs, CultureInfo.InvariantCulture));
            var net = closed.Sum(c => decimal.Parse(c.Net, CultureInfo.InvariantCulture));
            var committed = open is null ? (decimal?)null : decimal.Parse(open.AvgEntryPrice, CultureInfo.InvariantCulture) * open.RemainingQuantity;
            panels.Add(new Npt1StrategyPanel(id, StateOf(open, decisions), open, closed, closed.Count, closed.Count(c => decimal.Parse(c.Net, CultureInfo.InvariantCulture) > 0m), gross, fees, net, committed, decisions));
        }

        return new Npt1View(runId, view.Status.TradeDate, panels, await StreamAsync(view.Status.TradeDate, ct),
            "Each strategy has its own position and ledger; their profits are NOT added (alternative strategies sharing one capital pool, not a portfolio result).");
    }

    private static string StateOf(PaperOpenPosition? open, IReadOnlyList<Npt1Decision> latestFirst)
    {
        if (open is not null)
        {
            var exitRequested = latestFirst.FirstOrDefault(d => d.Stage is "SignalExit" or "TerminalClose");
            var pendingExit = open.ExitPending || exitRequested is { Outcome: "Requested" or "Pending:NoBid" or "Pending:NoUsableBid" };
            return $"HOLDING {open.OptionType} {open.Symbol} x{open.RemainingQuantity}" + (pendingExit ? " - EXIT PENDING" : string.Empty);
        }

        var next = latestFirst.FirstOrDefault(d => d.Stage is "ContractSelection" or "NextBarEntry" or "SignalEntry");
        return next switch
        {
            { Stage: "ContractSelection", Outcome: "Selected" } => "ENTRY PENDING (fills only on the next completed bar)",
            { Stage: "NextBarEntry" } n when n.Outcome.StartsWith("Cancelled", StringComparison.Ordinal) => "FLAT (last entry cancelled: " + n.Outcome + ")",
            _ => "FLAT",
        };
    }

    private async Task<Npt1StreamHealth?> StreamAsync(DateOnly tradeDate, CancellationToken ct)
    {
        if (npt1 is null)
        {
            return null;
        }

        try
        {
            await using var db = await npt1.CreateDbContextAsync(ct);
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            var sessionId = M1Identity.SessionId(tradeDate);
            var runtime = await db.Runtime.FirstOrDefaultAsync(r => r.SessionId == sessionId, ct);
            if (runtime is null)
            {
                return null;
            }

            var session = await db.Sessions.FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);
            return new Npt1StreamHealth(
                sessionId, runtime.Status, runtime.LastHeartbeatUtc, runtime.LastBarSeq, runtime.LastBarEndUtc, session?.BaseBarVolume, session?.BarSizeStatus, session?.OpeningVolume,
                runtime.Polls, runtime.LastPollMillis, runtime.MaxPollMillis, runtime.RowsRead, runtime.DivergentFrames, runtime.Detail);
        }
        catch (Exception)
        {
            return null;                                         // the stream panel is informational: an unreadable npt1m1 schema must never hide the ledger
        }
    }
}
