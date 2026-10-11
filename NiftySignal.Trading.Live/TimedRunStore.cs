using System.Diagnostics;
using NiftySignal.Trading.Recovery;

namespace NiftySignal.Trading.Live;

/// <summary>
/// Measures the commits of a run store (duration, batch size) without changing them. It lives here, not in the deterministic recovery assembly, because it reads a clock. Measurement only:
/// nothing it records is ever part of a digest or a decision.
/// </summary>
public sealed class TimedRunStore(IRunStore inner) : IRunStore
{
    private readonly List<double> _commitMillis = new();
    private readonly object _gate = new();

    public IReadOnlyList<double> CommitMillis
    {
        get
        {
            lock (_gate)
            {
                return _commitMillis.ToList();
            }
        }
    }

    public int PeakStepInputs { get; private set; }

    public int PeakStepOutputs { get; private set; }

    public OwnershipToken Acquire(string runId, string ownerId) => inner.Acquire(runId, ownerId);

    public void Release(OwnershipToken token) => inner.Release(token);

    public RunSnapshot Load(string runId) => inner.Load(runId);

    public AppendResult CreateRun(OwnershipToken token, RunHeader header) => inner.CreateRun(token, header);

    public AppendResult AppendControl(OwnershipToken token, ControlRecord record) => inner.AppendControl(token, record);

    public void AppendRecoveryLog(OwnershipToken token, RecoveryLogEntry entry) => inner.AppendRecoveryLog(token, entry);

    public CommitResult Commit(OwnershipToken token, CommitBatch batch)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return inner.Commit(token, batch);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            lock (_gate)
            {
                _commitMillis.Add(elapsed);
                PeakStepInputs = Math.Max(PeakStepInputs, batch.Inputs.Count);
                PeakStepOutputs = Math.Max(PeakStepOutputs, batch.Outputs.Count);
            }
        }
    }
}
