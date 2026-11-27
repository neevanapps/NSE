using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Pipeline;

namespace NiftySignal.PaperEngineV2.Execution;

/// <summary>India VIX as last observed, with the instant it became available.</summary>
public sealed record VixReading(double Value, DateTimeOffset ObservedAt);

/// <summary>The completed-bar average price of one exact contract ending at <see cref="BarSeq"/> (null = unavailable: no bridging, no zero-fill).</summary>
public sealed record ContractAverage(string Token, double? Average, int BarSeq);

/// <summary>
/// The safety verdict evaluated outside the strategy: <see cref="EntryBlock"/> (non-null) forbids new entries and fills and cancels an outstanding order; on its own it is TEMPORARY and a waiting Avg5 intent
/// survives it. <see cref="Invalidate"/> (non-null) means pending directional intents are no longer safe and are consumed. <see cref="Flatten"/> (non-null) additionally requests the exit of every open position
/// and always invalidates. Nothing here can ever prevent an exit.
/// </summary>
public sealed record SafetyState(string? EntryBlock = null, string? Flatten = null, string? Invalidate = null)
{
    public static readonly SafetyState Healthy = new();
}

/// <summary>
/// Everything one execution step may look at. A strategy decides from this input and its own state ONLY, so a journal of the inputs that changed state replays to the identical ledger.
/// <see cref="Now"/> is the decision instant: every fill needs a quote that became available strictly after the decision it fills.
/// </summary>
public sealed record ExecutionInput(
    DateTimeOffset Now, DateOnly TradeDate, long SessionId, IReadOnlyList<QuoteSnapshot> Quotes, VixReading? Vix, IReadOnlyList<ContractAverage> Averages, SafetyState Safety,
    MarketStateFrame? Frame = null, MarketStateFrame? Previous = null, FrameDisposition? Disposition = null)
{
    /// <summary>The bar-driven form (replay and tests): the instant is the bar's end and the only quotes are the frame's own.</summary>
    public static ExecutionInput FromFrame(MarketStateFrame frame, MarketStateFrame? previous, FrameDisposition disposition) =>
        new(frame.Identity.BarEndAvailableAt, frame.Identity.TradeDate, frame.Identity.SessionId,
            frame.HeldQuotes.Count == 0 ? frame.Quotes : [.. frame.Quotes, .. frame.HeldQuotes], null, [], SafetyState.Healthy, frame, previous, disposition);
}
