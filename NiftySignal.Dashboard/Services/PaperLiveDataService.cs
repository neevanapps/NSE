using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NiftySignal.Trading.Core.Integrity;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Data;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Dashboard.Services;

public sealed record PaperLiveRunStatus(
    string RunId,
    DateOnly TradeDate,
    bool Completed,
    string? OwnerId,
    long Steps,
    long Inputs,
    long Outputs,
    long? LastBarSeq,
    DateTimeOffset? LastStepEngineTime,
    DateTimeOffset? LastGroupAt,
    string? LastRecoveryOutcome,
    string? LastRecoveryDetail,
    decimal? GlobalCapital,
    IReadOnlyList<string> Problems);

public sealed record PaperOpenPosition(
    string StrategyId, string VariantId, string PositionId, string Direction, string OptionType, string Symbol, string Strike, DateTimeOffset EntryAt,
    string AvgEntryPrice, long EntryQuantity, long RemainingQuantity, string Tp1State, string? StopLevel, string? Tp1Level, DateTimeOffset? MaxHoldDeadline,
    string? LastBid, string? UnrealizedPnl, string? Mfe, string? Mae, string? MfePct, string? MaePct, string? Coverage, bool ExitPending, DateTimeOffset AsOf);

public sealed record PaperClosedTrade(
    string StrategyId, string VariantId, string PositionId, string Direction, string OptionType, string Symbol, DateTimeOffset EntryAt, DateTimeOffset ExitAt, string ExitReason,
    string AvgEntryPrice, string AvgExitPrice, long Quantity, string Gross, string Costs, string Net, string? Mfe, string? Mae, string? MfePct, string? MaePct, string? Coverage, long EntryBarSeq, long ExitBarSeq);

public sealed record PaperScopeSummary(string Scope, int Trades, int Open, int Closed, int Wins, int Losses, decimal ClosedNet, int ConsecutiveLosses, decimal? AvgMfe, decimal? AvgMae);

public sealed record PaperRejection(DateTimeOffset At, string StrategyId, string VariantId, string Stage, string Reasons, long BarSeq, string DecisionId);

public sealed record PaperRiskPanel(
    decimal? GlobalCapital,
    decimal OpenExposure,
    decimal? CapitalInUsePct,
    int GlobalTrades,
    decimal GlobalRealizedNet,
    string IntegrityState,
    IReadOnlyList<string> KillSwitches,
    IReadOnlyList<PaperRejection> Rejections);

public sealed record PaperLiveView(
    PaperLiveRunStatus Status,
    IReadOnlyList<PaperOpenPosition> Open,
    IReadOnlyList<PaperClosedTrade> Closed,
    IReadOnlyList<PaperScopeSummary> Families,
    IReadOnlyList<PaperScopeSummary> Variants,
    PaperRiskPanel Risk);

/// <summary>
/// READ-ONLY view of the live paper trading path (contract section B.7). It reads the <c>trading</c> schema only — runs, positions, the position snapshot cache, the risk-ledger cache,
/// rejections and a few decoded outputs — through a context whose connection is opened read-only. It cannot place, change or cancel anything: there is no write method, no broker, no
/// command of any kind. The figures shown come from the durable ledger (outputs and the rows derived from them), never from the Host's memory.
/// </summary>
public sealed partial class PaperLiveDataService(IDbContextFactory<TradingDbContext>? factory, Func<DateOnly>? today = null)
{
    public bool Configured => factory is not null;

    /// <summary>The latest run (by trade date, then run id), or null when nothing was recorded yet.</summary>
    public async Task<PaperLiveView?> LoadLatestAsync(DateOnly? tradeDate = null, CancellationToken ct = default)
    {
        if (factory is null)
        {
            return null;
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var runs = db.Runs.AsQueryable();
        if (tradeDate is { } day)
        {
            runs = runs.Where(r => r.TradeDate == day);
        }

        var run = await runs.OrderByDescending(r => r.TradeDate).ThenByDescending(r => r.RunId).FirstOrDefaultAsync(ct);
        if (run is null)
        {
            return null;
        }

        var id = run.RunId;
        var problems = new List<string>();
        var owner = await db.RunOwners.Where(o => o.RunId == id).Select(o => o.OwnerId).FirstOrDefaultAsync(ct);
        var steps = await db.Steps.CountAsync(s => s.RunId == id, ct);
        var inputs = await db.Inputs.CountAsync(i => i.RunId == id, ct);
        var outputs = await db.Outputs.CountAsync(o => o.RunId == id, ct);
        var lastBar = await db.Inputs.Where(i => i.RunId == id && i.Kind == "BarCompleted").MaxAsync(i => (long?)i.SourceId, ct);
        var lastStep = await db.Steps.Where(s => s.RunId == id).MaxAsync(s => (long?)s.EngineTimeUtcTicks, ct);
        var lastGroup = await db.Inputs.Where(i => i.RunId == id && i.Kind != "Advance" && i.Kind != "Finish").MaxAsync(i => (long?)i.AtUtcTicks, ct);
        var recovery = await db.RecoveryLog.Where(l => l.RunId == id).OrderByDescending(l => l.LogSeq).FirstOrDefaultAsync(ct);
        var capital = GlobalCapitalOf(run.ConfigJson);
        var istToday = today?.Invoke() ?? DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).Date);
        if (!run.Completed && run.TradeDate < istToday)
        {
            problems.Add("This run never reached its final close (the Host was not running at the session end): positions that were still open were NOT liquidated in the paper ledger and stay unresolved.");
        }

        if (recovery is not null && recovery.Outcome.StartsWith("Halted", StringComparison.Ordinal))
        {
            problems.Add($"HALTED: {recovery.Outcome} - {recovery.Detail}");
        }

        var positions = await db.Positions.Where(p => p.RunId == id).OrderBy(p => p.OpenedOutputSeq).ToListAsync(ct);
        var snapshots = (await db.PositionSnapshots.Where(p => p.RunId == id).ToListAsync(ct)).ToDictionary(p => p.PositionId, StringComparer.Ordinal);
        var risk = await db.RiskCheckpoints.Where(r => r.RunId == id).OrderByDescending(r => r.StepSeq).FirstOrDefaultAsync(ct);
        var ledger = risk is null ? new RiskLedger() : RiskLedger.FromText(risk.CountersText);
        if (risk is not null && risk.CountersHash != OutputCodec.ContentHash(risk.CountersText))
        {
            problems.Add("The risk-ledger cache does not match its own hash; the figures below are not trustworthy.");
        }

        var open = positions.Where(p => p.ClosedOutputSeq is null).Select(p => OpenOf(p, snapshots.GetValueOrDefault(p.PositionId))).ToList();
        var closed = positions.Where(p => p.ClosedOutputSeq is not null).OrderByDescending(p => p.ClosedOutputSeq).Select(ClosedOf).ToList();

        var variants = positions.GroupBy(p => (p.StrategyId, p.VariantId)).OrderBy(g => g.Key.StrategyId, StringComparer.Ordinal).ThenBy(g => g.Key.VariantId, StringComparer.Ordinal)
            .Select(g => Summarize($"{g.Key.StrategyId}/{g.Key.VariantId}", g, ledger.ConsecutiveLossesOf(new NiftySignal.Trading.Core.Strategies.VariantKey(g.Key.StrategyId, g.Key.VariantId)))).ToList();
        var families = positions.GroupBy(p => p.StrategyId).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => Summarize(g.Key, g, ledger.FamilyConsecutiveLosses(g.Key))).ToList();

        var rejections = (await db.Rejections.Where(r => r.RunId == id).OrderByDescending(r => r.OutputSeq).Take(25).ToListAsync(ct))
            .Select(r => new PaperRejection(Instant(r.AtUtcTicks), r.StrategyId, r.VariantId, r.Stage, r.Reasons, r.BarSeq, r.DecisionId)).ToList();
        var (integrity, kills) = await StateOfAsync(db, id, ct);
        var exposure = ledger.OpenExposure();
        var panel = new PaperRiskPanel(capital, exposure, capital is > 0m ? Math.Round(100m * exposure / capital.Value, 2) : null, ledger.GlobalTrades(), ledger.GlobalRealizedNet(), integrity, kills, rejections);
        var status = new PaperLiveRunStatus(id, run.TradeDate, run.Completed, owner, steps, inputs, outputs, lastBar, lastStep is { } s ? Instant(s) : null, lastGroup is { } g ? Instant(g) : null, recovery?.Outcome, recovery?.Detail, capital, problems);
        return new PaperLiveView(status, open, closed, families, variants, panel);
    }

    /// <summary>The integrity state and the active kill scopes, from the latest decoded transition outputs (the outputs are the ledger of record).</summary>
    private static async Task<(string Integrity, IReadOnlyList<string> Kills)> StateOfAsync(TradingDbContext db, string runId, CancellationToken ct)
    {
        var transition = await db.Outputs.Where(o => o.RunId == runId && o.Type == nameof(IntegrityTransition)).OrderByDescending(o => o.Seq).Select(o => o.Payload).FirstOrDefaultAsync(ct);
        var integrity = transition is null ? nameof(IntegrityState.Healthy) : ((IntegrityTransition)OutputCodec.Decode(transition)).To.ToString();
        var changes = await db.Outputs.Where(o => o.RunId == runId && o.Type == nameof(KillSwitchChanged)).OrderBy(o => o.Seq).Select(o => o.Payload).ToListAsync(ct);
        var active = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var payload in changes)
        {
            var change = (KillSwitchChanged)OutputCodec.Decode(payload);
            if (change.Effect.StartsWith("Rejected", StringComparison.Ordinal))
            {
                continue;
            }

            if (change.Command == NiftySignal.Trading.Core.Events.OperatorCommand.Kill)
            {
                active.Add(change.Scope);
            }
            else if (change.Effect == "Reset")
            {
                active.Remove(change.Scope);          // a reset clears exactly the scope it names
            }
        }

        return (integrity, active.ToList());
    }

    private static PaperOpenPosition OpenOf(PositionEntity p, PositionSnapshotEntity? s)
        => new(
            p.StrategyId, p.VariantId, p.PositionId, p.Direction, p.OptionType, p.Symbol, p.StrikeText, Instant(p.EntryFillUtcTicks), p.AvgEntryPriceText, p.EntryQuantity,
            s?.RemainingQuantity ?? p.EntryQuantity, s?.Tp1State ?? "NotTriggered", p.StopLevelText, p.Tp1LevelText, p.MaxHoldDeadlineUtcTicks is { } d ? Instant(d) : null,
            s?.LastBidText, s?.UnrealizedPnlText, s?.MfeText, s?.MaeText, s?.MfePctText, s?.MaePctText, s?.ExcursionCoverage, s?.ExitPending ?? false, s is null ? Instant(p.EntryFillUtcTicks) : Instant(s.AsOfUtcTicks));

    private static PaperClosedTrade ClosedOf(PositionEntity p)
        => new(
            p.StrategyId, p.VariantId, p.PositionId, p.Direction, p.OptionType, p.Symbol, Instant(p.EntryFillUtcTicks), Instant(p.ExitUtcTicks!.Value), p.FinalExitReason!, p.AvgEntryPriceText, p.AvgExitPriceText!,
            p.EntryQuantity, p.GrossPnlText!, p.TotalCostsText!, p.NetPnlText!, p.MfeText, p.MaeText, p.MfePctText, p.MaePctText, p.ExcursionCoverage, p.EntryBarSeq, p.ExitBarSeq!.Value);

    private static PaperScopeSummary Summarize(string scope, IEnumerable<PositionEntity> positions, int consecutiveLosses)
    {
        var list = positions.ToList();
        var closed = list.Where(p => p.ClosedOutputSeq is not null).ToList();
        var nets = closed.Select(p => decimal.Parse(p.NetPnlText!, CultureInfo.InvariantCulture)).ToList();
        var mfes = closed.Where(p => p.MfeText is not null).Select(p => decimal.Parse(p.MfeText!, CultureInfo.InvariantCulture)).ToList();
        var maes = closed.Where(p => p.MaeText is not null).Select(p => decimal.Parse(p.MaeText!, CultureInfo.InvariantCulture)).ToList();
        return new PaperScopeSummary(scope, list.Count, list.Count - closed.Count, closed.Count, nets.Count(n => n > 0m), nets.Count(n => n < 0m), nets.Sum(), consecutiveLosses, mfes.Count == 0 ? null : mfes.Average(), maes.Count == 0 ? null : maes.Average());
    }

    private static DateTimeOffset Instant(long ticks) => TickTime.FromTicks(ticks);

    internal static decimal? GlobalCapitalOf(string configJson)
        => CapitalPattern().Match(configJson) is { Success: true } m && decimal.TryParse(m.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var capital) ? capital : null;

    [GeneratedRegex("\"GlobalCapital\"\\s*:\\s*\"?([0-9]+(?:\\.[0-9]+)?)")]
    private static partial Regex CapitalPattern();
}
