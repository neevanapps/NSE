using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.Trading.Core.Costs;

namespace NiftySignal.PaperEngineV2.Execution;

/// <summary>V2 v1 only BUYS options: a bullish view buys a call, a bearish view buys a put. There is no sell / write side anywhere in this assembly.</summary>
public enum OptionRight { Call, Put }

/// <summary>The closed, durable taxonomy of simulated rejections. A rejection is a NORMAL ledger outcome of the simulation; only <see cref="InfrastructureFailure"/> is a malfunction class.</summary>
public enum RejectionReason
{
    InvalidInstrument, MissingQuote, StaleQuote, InsufficientCapital, LotSizeViolation, RiskBreach, ExpiredOpportunity,
    UnavailableLiquidity,       // reserved: V2 v1 is all-or-none and never infers liquidity from Level-1 sizes
    DuplicateOrder, InfrastructureFailure,
    NoQualifyingContract, SpreadTooWide, OutsideEntryWindow, PositionLimit, EntriesBlocked, ConflictingSignals,
    WarmupIncomplete,           // appended: fewer than filters.minCompletedBars verified bars of the session have been evaluated
    Avg5NotMet,                 // appended: the Avg5 condition no longer held at the fill; the intent keeps waiting
    OppositeSignal,             // appended: an opposite signal arrived before the pending order filled
}

/// <summary><see cref="Terminal"/> is the mandatory session liquidation; <see cref="Protective"/> the independent safety flatten.</summary>
public enum ExitReason { Discretionary, OppositeSignal, StopLoss, Target, Trailing, MaxHold, Terminal, RiskLiquidation, Protective }

public enum PositionState { Open, ExitPending, Closed, Unresolved }

/// <summary>Instrument-master lot size per trade date and token (null = unknown, which rejects the order: never assumed).</summary>
public interface ILotSizeSource
{
    int? LotSize(DateOnly tradeDate, string token);
}

/// <summary>
/// The IST session timers. No new entry at or after <see cref="EntryCutoff"/> (pending ones are cancelled) while open positions are still managed; every position still open at <see cref="LiquidationStart"/> is
/// requested to exit, and anything not confirmed closed at <see cref="LiquidationDeadline"/> is a reported liquidation failure. The 15:08 start is a starting value.
/// </summary>
public sealed record SessionTimes(TimeOnly EntryCutoff, TimeOnly LiquidationStart, TimeOnly LiquidationDeadline)
{
    public static readonly SessionTimes Default = new(new(15, 0), new(15, 8), new(15, 10));

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (EntryCutoff > new TimeOnly(15, 0)) errors.Add("the entry cutoff cannot be later than 15:00 IST");
        if (LiquidationStart < EntryCutoff) errors.Add("liquidation cannot start before the entry cutoff");
        if (LiquidationDeadline <= LiquidationStart) errors.Add("the liquidation deadline must be later than its start");
        if (LiquidationDeadline > new TimeOnly(15, 10)) errors.Add("the liquidation deadline cannot be later than 15:10 IST");
        return errors;
    }
}

/// <summary>
/// Engine-level parameters: the retry cut-off for an unfilled liquidation is an owner decision, the cost schedules are the existing date-effective NSE schedules, the lot sizes come from the instrument master.
/// </summary>
public sealed record ExecutionOptions
{
    public ExecutionOptions(TimeOnly terminalRetryUntil, IReadOnlyList<CostScheduleEntry> costSchedules, ILotSizeSource lotSizes, SessionTimes? times = null)
    {
        Times = times ?? SessionTimes.Default;
        if (Times.Validate() is { Count: > 0 } timeErrors) throw new ArgumentException(string.Join(" ", timeErrors), nameof(times));
        if (terminalRetryUntil <= Times.LiquidationDeadline) throw new ArgumentOutOfRangeException(nameof(terminalRetryUntil), "the terminal retry cut-off must be later than the liquidation deadline");
        if (costSchedules.Count == 0) throw new ArgumentException("at least one cost schedule is required: costs are never assumed to be zero", nameof(costSchedules));
        var errors = new CostsConfig { Schedules = costSchedules }.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(costSchedules));
        TerminalRetryUntil = terminalRetryUntil; CostSchedules = costSchedules; LotSizes = lotSizes ?? throw new ArgumentNullException(nameof(lotSizes));
    }

    public SessionTimes Times { get; }

    public TimeOnly TerminalRetryUntil { get; }

    public IReadOnlyList<CostScheduleEntry> CostSchedules { get; }

    public ILotSizeSource LotSizes { get; }
}

/// <summary>Base of every durable ledger event. <see cref="EventId"/> is deterministic (strategy, config hash prefix, kind, intent / position key) so a replay reproduces identical ids and a consumer can de-duplicate.</summary>
public abstract record PaperEvent(string EventId, string StrategyId, string ConfigHash, int BarSeq, DateTimeOffset At)
{
    public abstract string Kind { get; }
}

public sealed record EntryRejected(string EventId, string StrategyId, string ConfigHash, int BarSeq, DateTimeOffset At, OptionRight? Right, string? IntentId, RejectionReason Reason, string Detail) : PaperEvent(EventId, StrategyId, ConfigHash, BarSeq, At)
{
    public override string Kind => "EntryRejected";
}

public sealed record EntryOrderPlaced(string EventId, string StrategyId, string ConfigHash, int BarSeq, DateTimeOffset At, string IntentId, OptionRight Right, string Token, double Strike, DateOnly Expiry,
    int LotSize, int Quantity, decimal SelectedAsk, DecisionKey Decision, DateTimeOffset ExpiresAt) : PaperEvent(EventId, StrategyId, ConfigHash, BarSeq, At)
{
    public override string Kind => "EntryOrderPlaced";
}

public sealed record EntryFilled(string EventId, string StrategyId, string ConfigHash, int BarSeq, DateTimeOffset At, string IntentId, string PositionId, OptionRight Right, string Token, int Quantity,
    decimal FillPrice, decimal Fees, DateTimeOffset QuoteAvailableAt, long QuoteTickId, long DisplayedQuantity) : PaperEvent(EventId, StrategyId, ConfigHash, BarSeq, At)
{
    public override string Kind => "EntryFilled";
}

public sealed record ExitRequested(string EventId, string StrategyId, string ConfigHash, int BarSeq, DateTimeOffset At, string PositionId, ExitReason Reason, DecisionKey Decision) : PaperEvent(EventId, StrategyId, ConfigHash, BarSeq, At)
{
    public override string Kind => "ExitRequested";
}

public sealed record ExitFilled(string EventId, string StrategyId, string ConfigHash, int BarSeq, DateTimeOffset At, string PositionId, ExitReason Reason, int Quantity, decimal FillPrice, decimal Fees,
    decimal GrossPnl, decimal NetPnl, DateTimeOffset QuoteAvailableAt, long QuoteTickId, long DisplayedQuantity, decimal Mfe, decimal Mae) : PaperEvent(EventId, StrategyId, ConfigHash, BarSeq, At)
{
    public override string Kind => "ExitFilled";
}

public sealed record PositionUnresolved(string EventId, string StrategyId, string ConfigHash, int BarSeq, DateTimeOffset At, string PositionId, string Detail) : PaperEvent(EventId, StrategyId, ConfigHash, BarSeq, At)
{
    public override string Kind => "PositionUnresolved";
}

public sealed record RiskBreached(string EventId, string StrategyId, string ConfigHash, int BarSeq, DateTimeOffset At, decimal RealizedNetToday, decimal MaxDailyLoss) : PaperEvent(EventId, StrategyId, ConfigHash, BarSeq, At)
{
    public override string Kind => "RiskBreached";
}

/// <summary>A malfunction inside ONE strategy's processing (never a simulated rejection). The strategy stops opening entries; every other strategy and NPT1 are unaffected.</summary>
public sealed record StrategyFaulted(string EventId, string StrategyId, string ConfigHash, int BarSeq, DateTimeOffset At, string Message) : PaperEvent(EventId, StrategyId, ConfigHash, BarSeq, At)
{
    public override string Kind => "StrategyFaulted";
}

/// <summary>A directional entry intent: created by a signal, consumed by exactly one entry fill or cancelled. <see cref="ReferenceToken"/> is the frozen Avg5 reference contract (null for immediate entry).</summary>
public sealed record EntryIntentCreated(string EventId, string StrategyId, string ConfigHash, int BarSeq, DateTimeOffset At, string IntentId, OptionRight Right, string? ReferenceToken, bool AwaitingFlat) : PaperEvent(EventId, StrategyId, ConfigHash, BarSeq, At)
{
    public override string Kind => "EntryIntentCreated";
}

public sealed record EntryIntentCancelled(string EventId, string StrategyId, string ConfigHash, int BarSeq, DateTimeOffset At, string IntentId, OptionRight Right, string Reason) : PaperEvent(EventId, StrategyId, ConfigHash, BarSeq, At)
{
    public override string Kind => "EntryIntentCancelled";
}

/// <summary>The evidence of an Avg5 entry: the signal instant, the reference contract, its live mid and its average at the fill.</summary>
public sealed record Avg5EntryEvidence(string EventId, string StrategyId, string ConfigHash, int BarSeq, DateTimeOffset At, string PositionId, DateTimeOffset SignalAt, string ReferenceToken, double ReferenceMid, double Average, int AverageBarSeq, string ExecutionToken, double EntryDelaySeconds) : PaperEvent(EventId, StrategyId, ConfigHash, BarSeq, At)
{
    public override string Kind => "Avg5EntryEvidence";
}

/// <summary>The partial-profit policy frozen at the entry fill. <see cref="Quantity"/> 0 with a <see cref="DisabledReason"/> = partial booking is off for this position.</summary>
public sealed record PartialTargetAssigned(string EventId, string StrategyId, string ConfigHash, int BarSeq, DateTimeOffset At, string PositionId, int Dte, double? Vix, DateTimeOffset? VixObservedAt, string? VixBucket,
    double? TargetPercent, decimal? TargetPrice, int Quantity, decimal EntryPrice, int OriginalQuantity, DateOnly Expiry, string? DisabledReason) : PaperEvent(EventId, StrategyId, ConfigHash, BarSeq, At)
{
    public override string Kind => "PartialTargetAssigned";
}

public sealed record PartialExitRequested(string EventId, string StrategyId, string ConfigHash, int BarSeq, DateTimeOffset At, string PositionId, int Quantity, decimal TriggerBid, decimal TargetPrice, DecisionKey Decision) : PaperEvent(EventId, StrategyId, ConfigHash, BarSeq, At)
{
    public override string Kind => "PartialExitRequested";
}

/// <summary>A confirmed partial fill of a still-open position. The trade is counted closed only by its final <see cref="ExitFilled"/>, whose P&amp;L includes this one.</summary>
public sealed record PartialExitFilled(string EventId, string StrategyId, string ConfigHash, int BarSeq, DateTimeOffset At, string PositionId, int Quantity, int RemainingQuantity, decimal FillPrice, decimal Fees,
    decimal GrossPnl, decimal NetPnl, DateTimeOffset QuoteAvailableAt, long QuoteTickId) : PaperEvent(EventId, StrategyId, ConfigHash, BarSeq, At)
{
    public override string Kind => "PartialExitFilled";
}

/// <summary>A position not confirmed closed at the liquidation deadline. It stays open and keeps being retried: never reported closed without a fill.</summary>
public sealed record LiquidationFailed(string EventId, string StrategyId, string ConfigHash, int BarSeq, DateTimeOffset At, string PositionId, int RemainingQuantity, string Detail) : PaperEvent(EventId, StrategyId, ConfigHash, BarSeq, At)
{
    public override string Kind => "LiquidationFailed";
}

/// <summary>A snapshot of one position for reporting (the live engine state is private to the strategy runtime).</summary>
public sealed record PositionView(
    string PositionId, string StrategyId, OptionRight Right, string Token, double Strike, DateOnly Expiry, int Quantity, PositionState State, decimal EntryPrice, decimal EntryFees,
    decimal? ExitPrice, decimal? ExitFees, decimal? GrossPnl, decimal? NetPnl, ExitReason? ExitReason, decimal Mfe, decimal Mae, int MarkGaps, int BarsHeld,
    int RemainingQuantity = 0, decimal PartialGrossPnl = 0m, decimal PartialNetPnl = 0m, decimal? PartialTargetPrice = null, string? PartialStatus = null, DateTimeOffset? EntryAt = null);

public enum ActivationFailureKind { UnsupportedFillQuoteRule, MissingCostSchedule }

/// <summary>A strategy that could not be activated and therefore never opens an entry (fail closed), with a typed reason.</summary>
public sealed record ActivationFailure(string StrategyId, ActivationFailureKind Kind, string Detail);
