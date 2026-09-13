using NiftySignal.Host;
using NiftySignal.Notifications;
using NiftySignal.Rules;

namespace NiftySignal.Backtest;

/// <summary>
/// A one-shot batch tool has no config file to hot-reload -- this is the same trivial
/// IValidatedOptions wrapper LiveTradingEngineTests already defines privately for its own
/// fixture, made public here since BacktestRunner's console entry point needs the same shape
/// for RulesetConfig/ScoreWeights loaded once from real appsettings at startup.
/// </summary>
public sealed class FixedOptions<T>(T value) : IValidatedOptions<T>
{
    public T Current { get; } = value;
}

/// <summary>
/// Audit finding F55's dynamic-hybrid mode (2026-09-11): unlike FixedOptions, this can be
/// updated between cadences. LiveTradingEngine's own `_config => rulesetOptions.Current` reads
/// this property fresh on every access (see its own doc comment) -- exactly the hot-reload
/// behavior this was originally built for, repurposed here so BacktestRunner can hand it a
/// freshly-derived RulesetConfig (session-rank-relative Entry.MinAbsScore/Exit.ExitOnScoreBelowAbs)
/// before each EvaluateCadenceAsync call, with zero changes to LiveTradingEngine itself.
/// </summary>
public sealed class MutableRulesetOptions(RulesetConfig initial) : IValidatedOptions<RulesetConfig>
{
    public RulesetConfig Current { get; set; } = initial;
}

/// <summary>
/// A backtest must never actually notify anyone -- it's replaying historical data, not a live
/// session. Mirrors LiveTradingEngineTests' own FakeTelegramNotifier (no network, just a place
/// for LiveTradingEngine's SendAsync calls to land safely).
/// </summary>
public sealed class NoOpTelegramNotifier : ITelegramNotifier
{
    public Task SendAsync(NotificationCategory category, string message, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
