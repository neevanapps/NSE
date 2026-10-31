using System.Diagnostics;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Pipeline;
using static NiftySignal.Tests.PaperEngineV2.FrameFixtures;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>Phase 6b: the bounded non-blocking handoff (observer side) and the process-local sequence / causality guard (consumer side).</summary>
public class HandoffAndGuardTests
{
    static HandoffOptions Opt(int capacity = 8, int k = 2, double lagSeconds = 10) => new(capacity, TimeSpan.FromSeconds(lagSeconds), k);

    // ---- options: bounded, validated, no defaults ----

    [Fact]
    public void Options_AreBoundedAndValidated_WithNoDefaults()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HandoffOptions(0, TimeSpan.FromSeconds(1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HandoffOptions(HandoffOptions.MaxCapacity + 1, TimeSpan.FromSeconds(1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HandoffOptions(1, TimeSpan.Zero, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HandoffOptions(1, TimeSpan.FromSeconds(-1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HandoffOptions(1, TimeSpan.FromSeconds(1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HandoffOptions(1, TimeSpan.FromSeconds(1), 1, 0));
        Assert.DoesNotContain(typeof(HandoffOptions).GetConstructors().Single().GetParameters(), p => p.Name is "capacity" or "maxProcessingLag" or "framesToClearGap" && p.HasDefaultValue);     // nothing to invent
        var ok = Opt(5, 3, 2);
        Assert.Equal((5, TimeSpan.FromSeconds(2), 3), (ok.Capacity, ok.MaxProcessingLag, ok.FramesToClearGap));
    }

    // ---- handoff ----

    [Fact]
    public async Task OrderedDelivery_PreservesTheOfferOrder_AndCountsEverything()
    {
        var h = new FrameHandoff(Opt(8));
        for (var i = 1; i <= 5; i++) Assert.True(h.Offer(Light(1, i)).Accepted);
        h.Complete();
        var seen = new List<int>();
        await foreach (var f in h.Reader.ReadAllAsync()) seen.Add(f.Identity.BarSeq);
        Assert.Equal([1, 2, 3, 4, 5], seen);
        var st = h.Stats();
        Assert.Equal((5L, 5L, 0L), (st.Offered, st.Accepted, st.RejectedFull));
        Assert.Equal(IntegrityFlags.None, st.ProducerSignals);
        Assert.Equal(OfferOutcome.Closed, h.Offer(Light(1, 6)).Outcome);                // after Complete: refused, never thrown
    }

    [Fact]
    public void Overflow_NeverDropsSilently_RejectsObservablyAndRaisesTheProducerSignal()
    {
        var h = new FrameHandoff(Opt(3));
        var results = Enumerable.Range(1, 8).Select(i => h.Offer(Light(1, i))).ToList();
        Assert.Equal([OfferOutcome.Accepted, OfferOutcome.Accepted, OfferOutcome.Accepted, OfferOutcome.RejectedFull, OfferOutcome.RejectedFull, OfferOutcome.RejectedFull, OfferOutcome.RejectedFull, OfferOutcome.RejectedFull], results.Select(r => r.Outcome));
        var st = h.Stats();
        Assert.Equal((8L, 3L, 5L), (st.Offered, st.Accepted, st.RejectedFull));
        Assert.Equal(st.Offered, st.Accepted + st.RejectedFull);                       // every frame is accounted for
        Assert.Equal((3, 3), (st.QueueDepth, st.Capacity));
        Assert.Equal((4, 8), (st.FirstDroppedBarSeq!.Value, st.LastDroppedBarSeq!.Value));
        Assert.True((st.ProducerSignals & IntegrityFlags.Overflow) != 0);
        var ledger = h.OverflowLedger();
        Assert.Equal([4, 5, 6, 7, 8], ledger.Select(l => l.BarSeq));
        Assert.All(ledger, l => Assert.Equal(End(l.BarSeq), l.DecisionKey.AvailableAt));            // identity and decision key, never a payload serialised on the observer thread
        Assert.Equal(IntegrityFlags.Overflow, h.TakeSignals());                         // the consumer accounts for and clears it
        Assert.Equal(IntegrityFlags.None, h.PeekSignals());
    }

    [Fact]
    public void OverflowLedger_IsBounded_ButTheCountersNeverLoseADrop()
    {
        var h = new FrameHandoff(new HandoffOptions(1, TimeSpan.FromSeconds(1), 1, overflowLedgerCapacity: 4));
        h.Offer(Light(1, 1));
        for (var i = 2; i <= 50; i++) h.Offer(Light(1, i));
        Assert.Equal(49, h.Stats().RejectedFull);
        Assert.Equal([47, 48, 49, 50], h.OverflowLedger().Select(l => l.BarSeq));       // only the newest identities are kept ...
        Assert.Equal((2, 50), (h.Stats().FirstDroppedBarSeq!.Value, h.Stats().LastDroppedBarSeq!.Value));   // ... the range and the totals are exact
    }

    [Fact]
    public void Offer_NeverWaits_EvenWithNoConsumerAndAFullQueue()
    {
        var h = new FrameHandoff(Opt(4));
        var frames = Enumerable.Range(1, 2_000).Select(i => Light(1, i)).ToList();
        var sw = Stopwatch.StartNew();
        foreach (var f in frames) h.Offer(f);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"2,000 offers on a full queue took {sw.Elapsed}");     // it returns immediately instead of waiting for room
        Assert.Equal(1_996, h.Stats().RejectedFull);
    }

    [Fact]
    public async Task ConcurrentOffers_AreAllAccountedFor()
    {
        var h = new FrameHandoff(Opt(500));
        var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() => { for (var i = 1; i <= 250; i++) h.Offer(Light(t + 1, i)); })).ToArray();
        await Task.WhenAll(tasks);
        var st = h.Stats();
        Assert.Equal(2_000, st.Offered);
        Assert.Equal(st.Offered, st.Accepted + st.RejectedFull);
        Assert.Equal(500, st.Accepted); Assert.Equal(1_500, st.RejectedFull);
    }

    [Fact]
    public void OfferBuilt_ContainsBuilderFaults_NeverThrows_AndSignalsTheGap()
    {
        var h = new FrameHandoff(Opt(4));
        var r = h.OfferBuilt(() => throw new InvalidOperationException("builder blew up"));
        Assert.Equal(OfferOutcome.BuildFailed, r.Outcome);
        Assert.Equal(1, h.Stats().BuildFailures);
        Assert.True((h.PeekSignals() & IntegrityFlags.BuildFailure) != 0);
        Assert.Equal(OfferOutcome.Accepted, h.OfferBuilt(() => Light(1, 2)).Outcome);
        h.Complete();
        Assert.Equal(OfferOutcome.Closed, h.OfferBuilt(() => Light(1, 3)).Outcome);
    }

    [Fact]
    public void TheQueueHoldsOnlyImmutableFrames()
    {
        var f = Light(1, 1);
        var h = new FrameHandoff(Opt(2));
        h.Offer(f);
        Assert.True(h.Reader.TryRead(out var got));
        Assert.Same(f, got);                                                             // handed over by reference because nothing about a frame can change
        Assert.Equal(f.Hash, got!.Hash);
    }

    // ---- guard ----

    [Fact]
    public void Guard_AcceptsOrderedFrames_AndSessionStart()
    {
        var g = new FrameSequenceGuard();
        Assert.Equal(GuardVerdict.SessionStart, g.Evaluate(Light(1, 1)).Verdict);
        Assert.Equal(GuardVerdict.Accept, g.Evaluate(Light(1, 2)).Verdict);
        var third = g.Evaluate(Light(1, 3));
        Assert.True(third.Accepted); Assert.True(third.PreviousUsable);
        Assert.Equal(3, g.LastBarSeq);
        Assert.Equal(GuardVerdict.SessionStart, g.Evaluate(Light(2, 1)).Verdict);        // a new session resets the state
        Assert.Equal(GuardVerdict.SessionStart, g.Evaluate(Light(3, 5)).Verdict);        // a late (mid-session) start is a start, not a gap
    }

    [Fact]
    public void Guard_DuplicatesAreIgnored_ConflictsAreRejected()
    {
        var g = new FrameSequenceGuard();
        g.Evaluate(Light(1, 1)); g.Evaluate(Light(1, 2));
        Assert.Equal(GuardVerdict.Duplicate, g.Evaluate(Light(1, 2)).Verdict);            // same content: ignored
        Assert.Equal(GuardVerdict.Duplicate, g.Evaluate(Light(1, 1)).Verdict);            // an old duplicate too
        var conflict = g.Evaluate(Light(1, 2, strict: 99));
        Assert.Equal((GuardVerdict.RejectConflict, IntegrityFlags.DuplicateConflict), (conflict.Verdict, conflict.Flags));
        Assert.False(conflict.Accepted);
        Assert.Equal(2, g.LastBarSeq);                                                    // state unchanged by a rejection
    }

    [Fact]
    public void Guard_OutOfOrderAndMissingSequences()
    {
        var g = new FrameSequenceGuard();
        g.Evaluate(Light(1, 1)); g.Evaluate(Light(1, 2));
        var gap = g.Evaluate(Light(1, 5));
        Assert.Equal((GuardVerdict.AcceptAfterGap, 3, 4, false), (gap.Verdict, gap.GapFrom, gap.GapTo, gap.PreviousUsable));
        Assert.True((gap.Flags & IntegrityFlags.SequenceGap) != 0);
        var late = g.Evaluate(Light(1, 3));                                              // bar 3 shows up after bar 5: too late
        Assert.Equal((GuardVerdict.RejectOutOfOrder, IntegrityFlags.OutOfOrder), (late.Verdict, late.Flags));
        Assert.Equal(5, g.LastBarSeq);
        Assert.Equal(GuardVerdict.Accept, g.Evaluate(Light(1, 6)).Verdict);
    }

    [Fact]
    public void Guard_CausalityDecisionKeysNeverGoBackwards_AndSameInstantOrdinalsAreConsistent()
    {
        var g = new FrameSequenceGuard();
        g.Evaluate(Light(1, 1)); g.Evaluate(Light(1, 2));
        var back = g.Evaluate(Light(1, 3, end: End(2).AddSeconds(-1), tick: 5));           // contiguous bar whose availability instant precedes its predecessor
        Assert.Equal((GuardVerdict.RejectCausality, IntegrityFlags.CausalityViolation), (back.Verdict, back.Flags));
        var backTick = g.Evaluate(Light(1, 3, end: End(2), tick: 1));                      // same instant, smaller source tick id
        Assert.Equal(GuardVerdict.RejectCausality, backTick.Verdict);
        // the zero-duration siblings of one market update: same instant + same tick, ordinals 1, 2 ...
        var t = End(2);
        Assert.Equal(GuardVerdict.Accept, g.Evaluate(Light(1, 3, end: t, tick: 1_002, ordinal: 1, duration: 0)).Verdict);
        Assert.Equal(GuardVerdict.Accept, g.Evaluate(Light(1, 4, end: t, tick: 1_002, ordinal: 2, duration: 0)).Verdict);
        Assert.Equal(GuardVerdict.RejectCausality, g.Evaluate(Light(1, 5, end: t, tick: 1_002, ordinal: 0)).Verdict);        // a sibling cannot restart the numbering
        Assert.Equal(GuardVerdict.RejectCausality, g.Evaluate(Light(1, 5, end: t.AddSeconds(5), tick: 1_100, ordinal: 1)).Verdict);   // a later instant must restart at 0
        Assert.Equal(GuardVerdict.Accept, g.Evaluate(Light(1, 5, end: t.AddSeconds(5), tick: 1_100, ordinal: 0)).Verdict);
    }

    [Fact]
    public void Guard_AnUnverifiedPredecessorIsNotAUsablePreviousFrame_AndRestoreKeepsDetectingDuplicates()
    {
        var g = new FrameSequenceGuard();
        g.Evaluate(Light(1, 1)); g.Evaluate(Light(1, 2, verified: false));
        Assert.False(g.Evaluate(Light(1, 3)).PreviousUsable);
        var restored = new FrameSequenceGuard();
        restored.Restore([Light(1, 1), Light(1, 2), Light(1, 3)]);
        Assert.Equal(3, restored.LastBarSeq);
        Assert.Equal(GuardVerdict.Duplicate, restored.Evaluate(Light(1, 2)).Verdict);
        Assert.Equal(GuardVerdict.Accept, restored.Evaluate(Light(1, 4)).Verdict);
    }
}
