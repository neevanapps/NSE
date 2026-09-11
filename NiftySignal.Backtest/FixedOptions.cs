using NiftySignal.Host;
using NiftySignal.Notifications;

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
/// A backtest must never actually notify anyone -- it's replaying historical data, not a live
/// session. Mirrors LiveTradingEngineTests' own FakeTelegramNotifier (no network, just a place
/// for LiveTradingEngine's SendAsync calls to land safely).
/// </summary>
public sealed class NoOpTelegramNotifier : ITelegramNotifier
{
    public Task SendAsync(NotificationCategory category, string message, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
