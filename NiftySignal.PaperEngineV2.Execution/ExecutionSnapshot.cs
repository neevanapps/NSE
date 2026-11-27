namespace NiftySignal.PaperEngineV2.Execution;

/// <summary>
/// The execution service's standing, published with its heartbeat for read-only consumers (the Dashboard). <see cref="Status"/> is the service state; <see cref="EntryBlock"/> / <see cref="Flatten"/> are the safety
/// verdict in force; <see cref="Conditions"/> lists every outstanding problem in plain words.
/// </summary>
public sealed record ExecutionSnapshot(
    DateTimeOffset At, string Status, DateOnly TradeDate, long? SessionId, int? LastBarSeq, bool Reconciled, string? EntryBlock, string? Flatten,
    IReadOnlyList<StrategyStatus> Strategies, IReadOnlyList<PositionView> Positions, IReadOnlyList<string> Conditions);
