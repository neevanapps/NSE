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
}

public enum ExitReason { Discretionary, OppositeSignal, StopLoss, Target, Trailing, MaxHold, Terminal, RiskLiquidation }

public enum PositionState { Open, ExitPending, Closed, Unresolved }

/// <summary>Instrument-master lot size per trade date and token (null = unknown, which rejects the order: never assumed).</summary>
public interface ILotSizeSource
{
    int? LotSize(DateOnly tradeDate, string token);
}

/// <summary>
/// Engine-level parameters that are REQUIRED (no defaults): the retry cut-off for a pending terminal exit is an owner decision, the cost schedules are the existing date-effective NSE schedules, and the lot sizes
/// come from the instrument master.
/// </summary>
public sealed record ExecutionOptions
{
    public static readonly TimeOnly TerminalTime = new(15, 0, 0);

    public ExecutionOptions(TimeOnly terminalRetryUntil, IReadOnlyList<CostScheduleEntry> costSchedules, ILotSizeSource lotSizes)
    {
        if (terminalRetryUntil <= TerminalTime) throw new ArgumentOutOfRangeException(nameof(terminalRetryUntil), "the terminal retry cut-off must be later than the 15:00:00 terminal time");
        if (costSchedules.Count == 0) throw new ArgumentException("at least one cost schedule is required: costs are never assumed to be zero", nameof(costSchedules));
        var errors = new CostsConfig { Schedules = costSchedules }.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(costSchedules));
        TerminalRetryUntil = terminalRetryUntil; CostSchedules = costSchedules; LotSizes = lotSizes ?? throw new ArgumentNullException(nameof(lotSizes));
    }

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

/// <summary>A snapshot of one position for reporting (the live engine state is private to the strategy runtime).</summary>
public sealed record PositionView(
    string PositionId, string StrategyId, OptionRight Right, string Token, double Strike, DateOnly Expiry, int Quantity, PositionState State, decimal EntryPrice, decimal EntryFees,
    decimal? ExitPrice, decimal? ExitFees, decimal? GrossPnl, decimal? NetPnl, ExitReason? ExitReason, decimal Mfe, decimal Mae, int MarkGaps, int BarsHeld);

public enum ActivationFailureKind { UnsupportedFillQuoteRule, MissingCostSchedule }

/// <summary>A strategy that could not be activated and therefore never opens an entry (fail closed), with a typed reason.</summary>
public sealed record ActivationFailure(string StrategyId, ActivationFailureKind Kind, string Detail);
