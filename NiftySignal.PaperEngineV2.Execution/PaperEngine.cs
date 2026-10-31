using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Pipeline;

namespace NiftySignal.PaperEngineV2.Execution;

/// <summary>Receives every ledger event in order (Phase 6d persists them, Phase 6e turns them into alerts). A sink failure is contained and counted: it never reaches a strategy or the consumer.</summary>
public interface IPaperEventSink
{
    void Append(PaperEvent paperEvent);
}

/// <summary>
/// The paper execution engine: a frame processor that runs every ENABLED, activatable strategy in ascending id order over the shared immutable frames. Strategies are isolated (own state, fault containment); the
/// engine has no broker, order-submission or network dependency. Idempotent per decision key.
/// </summary>
public sealed class PaperEngine : IFrameProcessor
{
    readonly List<StrategyRuntime> _runtimes = [];
    readonly List<ActivationFailure> _failures = [];
    readonly List<PaperEvent> _events = [];
    readonly IPaperEventSink? _sink;
    readonly Dictionary<string, CompiledStrategy> _compiled = new(StringComparer.Ordinal);

    public PaperEngine(IEnumerable<CompiledStrategy> strategies, ExecutionOptions options, IPaperEventSink? sink = null)
    {
        _sink = sink;
        foreach (var s in strategies.Where(s => s.Config.Enabled).OrderBy(s => s.Config.Identity.StrategyId, StringComparer.Ordinal))
        {
            if (StrategyRuntime.CheckActivation(s.Config, options) is { } failure) { _failures.Add(failure); continue; }
            _runtimes.Add(new StrategyRuntime(s, options));
            _compiled[s.Config.Identity.StrategyId] = s;
        }
    }

    public IReadOnlyList<PaperEvent> Events => _events;

    public IReadOnlyList<ActivationFailure> ActivationFailures => _failures;

    public IReadOnlyList<StrategyRuntime> Runtimes => _runtimes;

    public long SinkFailures { get; private set; }

    public ValueTask ProcessAsync(MarketStateFrame frame, MarketStateFrame? previous, FrameDisposition disposition, CancellationToken ct)
    {
        var batch = new List<PaperEvent>();
        foreach (var r in _runtimes) r.Process(frame, previous, disposition, batch);
        foreach (var e in batch)
        {
            _events.Add(e);
            if (_sink is null) continue;
            try { _sink.Append(e); }
            catch (Exception) { SinkFailures++; }
        }

        return ValueTask.CompletedTask;
    }
}
