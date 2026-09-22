using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.Host;

namespace NiftySignal.Tests.Host;

public class ValidatedOptionsMonitorTests
{
    /// <summary>Fully-controllable stand-in for the real file-backed IOptionsMonitor -- Change() simulates a config file edit landing.</summary>
    sealed class FakeOptionsMonitor<T>(T initial) : IOptionsMonitor<T>
    {
        Action<T, string?>? _listener;

        public T CurrentValue { get; private set; } = initial;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            _listener = listener;
            return null;
        }

        public void Change(T newValue)
        {
            CurrentValue = newValue;
            _listener?.Invoke(newValue, null);
        }
    }

    sealed class TestOptions
    {
        public int Value { get; set; }
    }

    static IReadOnlyList<string> ValidateNonNegative(int value) => value < 0 ? ["Value must not be negative"] : [];

    static ValidatedOptionsMonitor<TestOptions, int> Sut(FakeOptionsMonitor<TestOptions> monitor) =>
        new(monitor, o => o.Value, ValidateNonNegative, NullLogger<ValidatedOptionsMonitor<TestOptions, int>>.Instance);

    [Fact]
    public void Constructor_MapsAndExposesTheInitialValue_WhenValid()
    {
        var sut = Sut(new FakeOptionsMonitor<TestOptions>(new TestOptions { Value = 5 }));

        Assert.Equal(5, sut.Current);
    }

    [Fact]
    public void Constructor_Throws_WhenTheInitialValueIsInvalid()
    {
        // No last-known-good exists yet at startup -- an invalid config here must fail fast,
        // not silently run on an unvalidated fallback.
        var monitor = new FakeOptionsMonitor<TestOptions>(new TestOptions { Value = -1 });

        Assert.Throws<InvalidOperationException>(() => Sut(monitor));
    }

    [Fact]
    public void OnChange_AppliesAValidReload()
    {
        var monitor = new FakeOptionsMonitor<TestOptions>(new TestOptions { Value = 5 });
        var sut = Sut(monitor);

        monitor.Change(new TestOptions { Value = 10 });

        Assert.Equal(10, sut.Current);
    }

    [Fact]
    public void OnChange_RejectsAnInvalidReload_AndKeepsServingTheLastKnownGoodValue()
    {
        var monitor = new FakeOptionsMonitor<TestOptions>(new TestOptions { Value = 5 });
        var sut = Sut(monitor);

        monitor.Change(new TestOptions { Value = -1 });

        Assert.Equal(5, sut.Current);
    }

    [Fact]
    public void OnChange_RejectsAReloadWhoseMappingThrows_AndKeepsServingTheLastKnownGoodValue()
    {
        var monitor = new FakeOptionsMonitor<TestOptions>(new TestOptions { Value = 5 });
        var sut = new ValidatedOptionsMonitor<TestOptions, int>(
            monitor,
            o => o.Value == 999 ? throw new FormatException("simulated bad mapping") : o.Value,
            ValidateNonNegative,
            NullLogger<ValidatedOptionsMonitor<TestOptions, int>>.Instance);

        monitor.Change(new TestOptions { Value = 999 });

        Assert.Equal(5, sut.Current);
    }

    [Fact]
    public void OnChange_RecoversOnTheNextValidReload_AfterARejection()
    {
        var monitor = new FakeOptionsMonitor<TestOptions>(new TestOptions { Value = 5 });
        var sut = Sut(monitor);

        monitor.Change(new TestOptions { Value = -1 }); // rejected
        monitor.Change(new TestOptions { Value = 20 }); // accepted

        Assert.Equal(20, sut.Current);
    }
}
