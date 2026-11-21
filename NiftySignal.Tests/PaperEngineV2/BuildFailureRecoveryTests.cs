using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Pipeline;
using static NiftySignal.Tests.PaperEngineV2.FrameFixtures;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>
/// Incident 2026-11-05: every frame of the session was "EntriesBlocked (BuildFailure)". A producer-side BuildFailure/Overflow signal is only acknowledged (TakeSignals) when the consumer accepts a frame AFTER a detected
/// sequence gap. A failure that happens before the consumer's first frame of a session (the consumer then sees SessionStart, not AcceptAfterGap) was never acknowledged, so the flag stayed raised for the whole session
/// even though every later frame was built, contiguous and verified. These tests pin the recovery contract.
/// </summary>
public class BuildFailureRecoveryTests
{
    static readonly HandoffOptions Options = new(capacity: 16, maxProcessingLag: TimeSpan.FromSeconds(10), framesToClearGap: 3);

    sealed class Rig
    {
        public FrameHandoff Handoff { get; } = new(Options);
        public InMemoryFrameJournal Journal { get; } = new();
        public ManualClock Clock { get; } = new(T0);
        public StrategyDigestProcessor Processor { get; } = new(StrategyDigestProcessor.Thirty());
        public FrameConsumer Consumer { get; }

        public Rig() => Consumer = new FrameConsumer(Handoff, Options, Journal, Processor, Clock);

        public async Task Feed(MarketStateFrame f)
        {
            Clock.Now = f.Identity.BarEndAvailableAt.AddSeconds(0.5);
            await Consumer.HandleAsync(f, CancellationToken.None);
        }

        public void Fail(int? bar) => Handoff.OfferBuilt(() => throw new InvalidOperationException("no centre strike for this bar"), bar);
    }

    static IntegrityFlags FlagsOf(Rig r, int bar) => r.Journal.ReadSessionAsync(1, default).Result.Dispositions.Single(d => d.BarSeq == bar).BlockReasons;

    static bool Allowed(Rig r, int bar) => r.Processor.Log.Single(l => l.Bar == bar).Allowed;

    [Fact]
    public async Task AFailureBeforeTheFirstFrameOfTheSession_DoesNotBlockEntriesForever()
    {
        var r = new Rig();
        r.Fail(1);                                                      // bar 1 could not be built (e.g. no centre strike yet)
        for (var i = 2; i <= 12; i++) await r.Feed(Light(1, i));        // bars 2.. are built, contiguous and verified
        Assert.False(Allowed(r, 2));                                     // continuity is uncertain at the start: blocked
        Assert.False(Allowed(r, 3));
        Assert.True(Allowed(r, 4), "after FramesToClearGap(3) clean contiguous frames the block must lift");
        Assert.Equal(IntegrityFlags.None, FlagsOf(r, 12));
        Assert.Equal(IntegrityFlags.None, r.Handoff.PeekSignals());
    }

    [Fact]
    public async Task AFailureBetweenFrames_IsAcknowledgedByTheGap_AndRecoversAfterThreeCleanFrames()
    {
        var r = new Rig();
        for (var i = 1; i <= 3; i++) await r.Feed(Light(1, i));
        r.Fail(4);                                                       // bar 4 fails
        for (var i = 5; i <= 12; i++) await r.Feed(Light(1, i));
        Assert.True(Allowed(r, 3));
        Assert.All(new[] { 5, 6 }, b => Assert.False(Allowed(r, b)));
        Assert.Equal(1, r.Consumer.Status.GapsDetected);
        Assert.True(Allowed(r, 7));                                      // post-gap run 1,2,3 -> entries resume at the third contiguous frame
        Assert.Equal(IntegrityFlags.None, FlagsOf(r, 12));
    }

    [Fact]
    public async Task ConsecutiveFailuresBeforeTheFirstFrame_RecoverTheSameWay()
    {
        var r = new Rig();
        r.Fail(1); r.Fail(2); r.Fail(3);
        for (var i = 4; i <= 10; i++) await r.Feed(Light(1, i));
        Assert.False(Allowed(r, 4)); Assert.False(Allowed(r, 5));
        Assert.True(Allowed(r, 6));
    }

    [Fact]
    public async Task AFailureThatIsStillOutstanding_KeepsEntriesBlocked_UntilTheNextFrameShowsTheGap()
    {
        var r = new Rig();
        for (var i = 1; i <= 5; i++) await r.Feed(Light(1, i));
        r.Fail(6);                                                       // bar 6 failed; the NEXT frame to arrive is bar 7 (a gap): no recovery may be claimed before that is observed
        Assert.Equal(IntegrityFlags.BuildFailure, r.Handoff.PeekSignals());
        await r.Feed(Light(1, 7));
        Assert.False(Allowed(r, 7));
        Assert.Equal(1, r.Consumer.Status.GapsDetected);
    }

    [Fact]
    public async Task AnUnverifiedFrame_StillBreaksTheRun_AfterAStartupFailure()
    {
        var r = new Rig();
        r.Fail(1);
        await r.Feed(Light(1, 2)); await r.Feed(Light(1, 3)); await r.Feed(Light(1, 4));
        Assert.True(Allowed(r, 4));
        await r.Feed(Light(1, 5, verified: false));
        Assert.False(Allowed(r, 5));
        Assert.True((FlagsOf(r, 5) & IntegrityFlags.UnverifiedFrame) != 0);
    }

    [Fact]
    public async Task AFailureOfALaterBar_RaisedWhileEarlierFramesAreStillQueued_IsNotAcknowledgedEarly()
    {
        var r = new Rig();
        r.Fail(8);                                                       // the producer is ahead of the consumer: bar 8 already failed while the consumer is still on bars 1-3
        for (var i = 1; i <= 3; i++) await r.Feed(Light(1, i));
        Assert.All(new[] { 1, 2, 3 }, b => { Assert.False(Allowed(r, b)); Assert.True((FlagsOf(r, b) & IntegrityFlags.BuildFailure) != 0); });
        await r.Feed(Light(1, 9));                                       // the gap at bar 8 is now observed and accounts for the failure
        Assert.Equal(1, r.Consumer.Status.GapsDetected);
        Assert.Equal(IntegrityFlags.None, r.Handoff.PeekSignals());
        for (var i = 10; i <= 12; i++) await r.Feed(Light(1, i));
        Assert.True(Allowed(r, 11));
    }

    [Fact]
    public async Task ADuplicateNoticeFailure_WithNoGap_IsAcknowledged_AndBlocksOnlyTheFrameThatAcknowledgesIt()
    {
        var r = new Rig();
        for (var i = 1; i <= 5; i++) await r.Feed(Light(1, i));
        Assert.True(Allowed(r, 5));
        r.Fail(3);                                                       // a second notice for bar 3 failed; bar 3 was already built and accepted, so no gap can ever follow
        await r.Feed(Light(1, 6));
        Assert.False(Allowed(r, 6)); Assert.True((FlagsOf(r, 6) & IntegrityFlags.BuildFailure) != 0);
        await r.Feed(Light(1, 7));
        Assert.True(Allowed(r, 7));
        Assert.Equal(0, r.Consumer.Status.GapsDetected);
    }

    [Fact]
    public async Task AnUnattributedFailure_IsNeverAcknowledgedWithoutAGap()
    {
        var r = new Rig();
        r.Fail(null);                                                    // the bar is unknown: no frame may claim to cover it
        for (var i = 2; i <= 12; i++) await r.Feed(Light(1, i));
        Assert.All(Enumerable.Range(2, 11), b => Assert.False(Allowed(r, b)));
        Assert.Equal(IntegrityFlags.BuildFailure, r.Handoff.PeekSignals());
    }

    [Fact]
    public async Task OverflowBeforeTheFirstFrame_StillBlocksUntilTheGapItCausesIsSeen()
    {
        var o = new HandoffOptions(capacity: 2, maxProcessingLag: TimeSpan.FromSeconds(10), framesToClearGap: 3);
        var h = new FrameHandoff(o);
        for (var i = 1; i <= 5; i++) h.Offer(Light(1, i));              // bars 3-5 are rejected: Overflow is raised before the consumer has seen bar 1
        Assert.True((h.PeekSignals() & IntegrityFlags.Overflow) != 0);
        var acknowledged = h.AcknowledgeBuildFailuresCoveredBy(1);
        Assert.Equal(IntegrityFlags.None, acknowledged);
        Assert.True((h.PeekSignals() & IntegrityFlags.Overflow) != 0);   // untouched
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ASlowConsumer_ProcessingLag_IsReportedSeparately_FromBuildFailure()
    {
        var r = new Rig();
        for (var i = 1; i <= 4; i++) await r.Feed(Light(1, i));
        r.Clock.Now = Light(1, 5).Identity.BarEndAvailableAt.AddSeconds(60);
        await r.Consumer.HandleAsync(Light(1, 5), CancellationToken.None);
        var f = FlagsOf(r, 5);
        Assert.True((f & IntegrityFlags.ProcessingLag) != 0);
        Assert.Equal(IntegrityFlags.None, f & IntegrityFlags.BuildFailure);
    }
}
