namespace NiftySignal.Tests.Notifications;

/// <summary>Minimal settable TimeProvider for tests -- avoids pulling in a fake-time-provider package for one test file.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
