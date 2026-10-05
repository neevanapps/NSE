namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// Frozen Python residual/execution research consumes FlowEvolutionCsv's millisecond timestamps.
/// Keep exact futures availability unchanged; use this diagnostic clock only for that research contract.
/// </summary>
public static class AdaptiveResearchClock
{
    public static DateTimeOffset DiagnosticTime(DateTimeOffset exactAvailableAt)
    {
        var utcTicks=exactAvailableAt.UtcDateTime.Ticks;
        return new DateTimeOffset(utcTicks-utcTicks%TimeSpan.TicksPerMillisecond,TimeSpan.Zero);
    }
}
