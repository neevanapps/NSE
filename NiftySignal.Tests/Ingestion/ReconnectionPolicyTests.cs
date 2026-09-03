using NiftySignal.Ingestion;

namespace NiftySignal.Tests.Ingestion;

public class ReconnectionPolicyTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(50, 30)]
    public void BaseDelayFor_FollowsExponentialScheduleCappedAt30Seconds(int consecutiveFailures, int expectedSeconds)
    {
        var delay = ReconnectionPolicy.BaseDelayFor(consecutiveFailures);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void ShouldAlert_IsFalseBelowFiveConsecutiveFailures()
    {
        var policy = new ReconnectionPolicy();

        for (var i = 0; i < ReconnectionPolicy.AlertThreshold - 1; i++)
        {
            policy.RecordFailure();
        }

        Assert.False(policy.ShouldAlert);
    }

    [Fact]
    public void ShouldAlert_IsTrueAtFiveConsecutiveFailures()
    {
        var policy = new ReconnectionPolicy();

        for (var i = 0; i < ReconnectionPolicy.AlertThreshold; i++)
        {
            policy.RecordFailure();
        }

        Assert.True(policy.ShouldAlert);
    }

    [Fact]
    public void RecordSuccess_ResetsConsecutiveFailuresToZero()
    {
        var policy = new ReconnectionPolicy();
        policy.RecordFailure();
        policy.RecordFailure();
        policy.RecordFailure();

        policy.RecordSuccess();

        Assert.Equal(0, policy.ConsecutiveFailures);
        Assert.False(policy.ShouldAlert);
    }

    [Fact]
    public void NextDelay_StaysWithinPlusOrMinus20PercentJitterOfBaseDelay()
    {
        var policy = new ReconnectionPolicy(new Random(42));
        policy.RecordFailure();
        policy.RecordFailure();
        policy.RecordFailure();
        var baseDelay = ReconnectionPolicy.BaseDelayFor(policy.ConsecutiveFailures);

        for (var i = 0; i < 100; i++)
        {
            var actual = policy.NextDelay();
            Assert.InRange(actual.TotalMilliseconds, baseDelay.TotalMilliseconds * 0.8, baseDelay.TotalMilliseconds * 1.2);
        }
    }
}
