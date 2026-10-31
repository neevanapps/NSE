using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Pipeline;
using static NiftySignal.Tests.PaperEngineV2.FrameFixtures;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>Phase 6b: the independent consumer - ordering, duplicates, gaps, overflow, lag, unverified frames, faults and restart, journal faults and deterministic replay (in-memory journal only).</summary>
public class ConsumerTests
{
    static readonly HandoffOptions Options = new(capacity: 4, maxProcessingLag: TimeSpan.FromSeconds(10), framesToClearGap: 2);

    sealed class Rig
    {
        public Rig(HandoffOptions? options = null)
        {
            Options = options ?? ConsumerTests.Options;
            Handoff = new FrameHandoff(Options);
            Processor = new StrategyDigestProcessor(StrategyDigestProcessor.Thirty());
            Consumer = new FrameConsumer(Handoff, Options, Journal, Processor, Clock);
        }

        public HandoffOptions Options { get; }
        public FrameHandoff Handoff { get; }
        public InMemoryFrameJournal Journal { get; } = new();
        public ManualClock Clock { get; } = new(T0);
        public StrategyDigestProcessor Processor { get; }
        public FrameConsumer Consumer { get; }

        /// <summary>Handles a frame directly (deterministic, no tasks), with the clock set to the frame's end + a small lag.</summary>
        public async Task Feed(MarketStateFrame f, double lagSeconds = 0.5)
        {
            Clock.Now = f.Identity.BarEndAvailableAt.AddSeconds(lagSeconds);
            await Consumer.HandleAsync(f, CancellationToken.None);
        }
    }

    static async Task<DispositionRecord> Disposition(Rig r, int bar) => (await r.Journal.ReadSessionAsync(1, default)).Dispositions.Single(d => d.BarSeq == bar);

    // ---- ordered delivery ----

    [Fact]
    public async Task OrderedDelivery_ProcessesEveryFrameInOrder_WithEntriesAllowedFromACleanStart()
    {
        var r = new Rig();
        for (var i = 1; i <= 6; i++) await r.Feed(Light(1, i));
        Assert.Equal([1, 2, 3, 4, 5, 6], r.Processor.Log.Select(l => l.Bar));
        Assert.All(r.Processor.Log, l => Assert.True(l.Allowed));                              // a clean start at bar 1 needs no warm-up
        Assert.Equal([null, 1, 2, 3, 4, 5], r.Processor.Log.Select(l => l.PreviousBar));        // crossovers get the real previous frame
        var st = r.Consumer.Status;
        Assert.Equal((6L, 0L, 0L, 0L), (st.Processed, st.Duplicates, st.Rejected, st.GapsDetected));
        var j = await r.Journal.ReadSessionAsync(1, default);
        Assert.Equal(6, j.Frames.Count); Assert.Equal(6, j.Dispositions.Count); Assert.Empty(j.Gaps);
        Assert.Equal(j.Frames.Select(f => f.JournalSeq).OrderBy(x => x), j.Frames.Select(f => f.JournalSeq));    // journal order = arrival order
    }

    [Fact]
    public async Task EndToEnd_ThroughTheBoundedHandoff_OnTheConsumersOwnTask()
    {
        var r = new Rig(new HandoffOptions(16, TimeSpan.FromHours(1), 1));
        r.Clock.Now = T0.AddMinutes(10);
        var run = Task.Run(() => r.Consumer.RunAsync(CancellationToken.None));
        for (var i = 1; i <= 12; i++) Assert.True(r.Handoff.Offer(Light(1, i)).Accepted);
        r.Handoff.Complete();
        Assert.Equal(ConsumerExit.Completed, await run);
        Assert.Equal(Enumerable.Range(1, 12), r.Processor.Log.Select(l => l.Bar));
        Assert.Equal(12, r.Consumer.Status.Processed);
    }

    // ---- duplicates, conflicts, out of order, gaps ----

    [Fact]
    public async Task DuplicateFrames_AreProcessedOnce()
    {
        var r = new Rig();
        await r.Feed(Light(1, 1)); await r.Feed(Light(1, 2)); await r.Feed(Light(1, 2)); await r.Feed(Light(1, 1)); await r.Feed(Light(1, 3));
        Assert.Equal([1, 2, 3], r.Processor.Log.Select(l => l.Bar));
        Assert.Equal(2, r.Consumer.Status.Duplicates);
        Assert.Equal(3, (await r.Journal.ReadSessionAsync(1, default)).Frames.Count);
    }

    [Fact]
    public async Task ConflictingFrame_IsRejected_NeverReplacesTheJournaledOne_AndBlocksEntriesUntilClean()
    {
        var r = new Rig();
        await r.Feed(Light(1, 1)); await r.Feed(Light(1, 2));
        await r.Feed(Light(1, 2, strict: 99));
        Assert.Equal(1, r.Consumer.Status.Rejected);
        Assert.Equal([1, 2], r.Processor.Log.Select(l => l.Bar));
        Assert.Equal(Light(1, 2).Hash, (await r.Journal.ReadSessionAsync(1, default)).Frames.Single(f => f.BarSeq == 2).FrameHash);
        await r.Feed(Light(1, 3)); await r.Feed(Light(1, 4));
        Assert.False(r.Processor.Log[2].Allowed);                                               // run was broken: 1 clean frame < 2
        Assert.True(r.Processor.Log[3].Allowed);                                                // 2 clean frames
    }

    [Fact]
    public async Task MissingSequence_IsDetectedAsAGap_EntriesBlockedUntilCleanAndNoRetroactiveSignal()
    {
        var r = new Rig();
        await r.Feed(Light(1, 1)); await r.Feed(Light(1, 2));
        await r.Feed(Light(1, 5));                                                              // 3 and 4 never arrived
        await r.Feed(Light(1, 6)); await r.Feed(Light(1, 7));
        var l = r.Processor.Log;
        Assert.Equal([1, 2, 5, 6, 7], l.Select(x => x.Bar));
        Assert.Equal([true, true, false, true, true], l.Select(x => x.Allowed));                   // bar 5: gap and a run of 1 (< 2); bar 6: run of 2 -> entries allowed again
        Assert.Equal([null, 1, null, 5, 6], l.Select(x => x.PreviousBar));                       // the link across the gap is dropped: crossovers are Unknown, nothing is bridged
        Assert.True((l[2].Reasons & IntegrityFlags.SequenceGap) != 0);
        var gaps = r.Consumer.GapLog;
        Assert.Single(gaps);
        Assert.Equal((3, 4, GapCause.MissingFrame, false), (gaps[0].FromBarSeq, gaps[0].ToBarSeq, gaps[0].Cause, gaps[0].Durable));      // process-local: NOT durable
        var j = await r.Journal.ReadSessionAsync(1, default);
        Assert.Single(j.Gaps); Assert.False(j.Gaps[0].Durable);
        Assert.Equal(1, r.Consumer.Status.GapsDetected);
        // none of the strategies' entry signals of the missing bars exist anywhere: they are simply absent
        Assert.DoesNotContain(l, x => x.Bar is 3 or 4);
    }

    [Fact]
    public async Task OutOfOrderFrame_IsRejected_AfterTheGapItCausedWasAlreadyRecorded()
    {
        var r = new Rig();
        await r.Feed(Light(1, 1)); await r.Feed(Light(1, 2)); await r.Feed(Light(1, 4)); await r.Feed(Light(1, 3)); await r.Feed(Light(1, 5));
        Assert.Equal([1, 2, 4, 5], r.Processor.Log.Select(l => l.Bar));
        Assert.Equal(1, r.Consumer.Status.Rejected); Assert.Equal(1, r.Consumer.Status.GapsDetected);
        Assert.Equal((3, 3), (r.Consumer.GapLog[0].FromBarSeq, r.Consumer.GapLog[0].ToBarSeq));
        Assert.False(r.Processor.Log[3].Allowed);                                               // not clean yet after the gap and the rejected late frame
    }

    // ---- overflow ----

    [Fact]
    public async Task QueueOverflow_BlocksEntries_ThenBecomesAnObservedGap_AndCleansAfterTheConfiguredRun()
    {
        var r = new Rig(new HandoffOptions(3, TimeSpan.FromHours(1), 2));
        for (var i = 1; i <= 8; i++) r.Handoff.Offer(Light(1, i));                              // no consumer running: 1..3 queued, 4..8 REJECTED visibly
        Assert.Equal(5, r.Handoff.Stats().RejectedFull);
        r.Clock.Now = T0.AddMinutes(20);
        while (r.Handoff.Reader.TryRead(out var f)) await r.Consumer.HandleAsync(f, default);   // the consumer drains 1..3
        Assert.Equal([1, 2, 3], r.Processor.Log.Select(l => l.Bar));
        Assert.All(r.Processor.Log, l => { Assert.False(l.Allowed); Assert.True((l.Reasons & IntegrityFlags.Overflow) != 0); });   // an unacknowledged overflow already blocks entries
        // the producer carries on once there is room: the consumer sees the discontinuity and accounts for the signal
        r.Handoff.Offer(Light(1, 9)); r.Handoff.Offer(Light(1, 10)); r.Handoff.Offer(Light(1, 11));
        while (r.Handoff.Reader.TryRead(out var f)) await r.Consumer.HandleAsync(f, default);
        var gap = Assert.Single(r.Consumer.GapLog);
        Assert.Equal((4, 8, GapCause.Overflow), (gap.FromBarSeq, gap.ToBarSeq, gap.Cause));
        Assert.True((gap.ProducerSignals & IntegrityFlags.Overflow) != 0); Assert.False(gap.Durable);
        Assert.Equal(IntegrityFlags.None, r.Handoff.PeekSignals());                              // acknowledged
        Assert.Equal([false, true, true], r.Processor.Log.Skip(3).Select(l => l.Allowed));         // 9: gap + run 1 (blocked); 10: run of 2 -> allowed; 11: allowed
        var d = r.Processor.Log.Skip(3).ToList();
        Assert.Equal([null, 9, 10], d.Select(x => x.PreviousBar));
    }

    [Fact]
    public async Task FullQueue_NeverBlocksTheProducer_EvenWhileTheConsumerIsStuckInAProcessor()
    {
        var r = new Rig(new HandoffOptions(2, TimeSpan.FromHours(1), 1));
        var gate = new TaskCompletionSource();
        r.Processor.OnProcess = _ => gate.Task.GetAwaiter().GetResult();                         // the processor hangs (a slow strategy / a stuck store)
        var run = Task.Run(() => r.Consumer.RunAsync(CancellationToken.None));
        r.Clock.Now = T0.AddMinutes(30);
        r.Handoff.Offer(Light(1, 1));
        SpinWait.SpinUntil(() => r.Handoff.Stats().QueueDepth == 0, TimeSpan.FromSeconds(5));    // frame 1 is now inside the stuck processor
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 2; i <= 50; i++) r.Handoff.Offer(Light(1, i));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), "the observer-side offer must never wait for V2");
        Assert.Equal(47, r.Handoff.Stats().RejectedFull);                                         // frame 1 is inside the processor, 2 queued, 47 rejected - visibly
        gate.SetResult(); r.Handoff.Complete();
        Assert.Equal(ConsumerExit.Completed, await run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // ---- lag, unverified, sibling bars, stale / unknown metrics ----

    [Fact]
    public async Task ProcessingLagBeyondTheLimit_BlocksEntries_IsJournaled_AndReplayAgrees()
    {
        var r = new Rig();
        await r.Feed(Light(1, 1)); await r.Feed(Light(1, 2), lagSeconds: 11); await r.Feed(Light(1, 3), lagSeconds: 10);
        Assert.Equal([true, false, true], r.Processor.Log.Select(l => l.Allowed));                // 11 s > 10 s limit; exactly 10 s is allowed
        Assert.True((r.Processor.Log[1].Reasons & IntegrityFlags.ProcessingLag) != 0);
        var d = await Disposition(r, 2);
        Assert.Equal(11_000, d.ProcessingLagMs); Assert.False(d.EntriesAllowed);
        var fresh = new StrategyDigestProcessor(StrategyDigestProcessor.Thirty());
        await FrameReplayer.ReplayAsync(r.Journal, 1, fresh, default);
        Assert.Equal(r.Processor.Digest, fresh.Digest);                                           // replay uses the journaled lag, not a clock
    }

    [Fact]
    public async Task UnverifiedFrame_BlocksEntries_DropsThePreviousLink_AndNeedsACleanRun()
    {
        var r = new Rig();
        await r.Feed(Light(1, 1)); await r.Feed(Light(1, 2, verified: false)); await r.Feed(Light(1, 3)); await r.Feed(Light(1, 4)); await r.Feed(Light(1, 5));
        var l = r.Processor.Log;
        Assert.Equal([true, false, false, true, true], l.Select(x => x.Allowed));                    // bar 3: run of 1; bar 4: run of 2 -> allowed
        Assert.True((l[1].Reasons & IntegrityFlags.UnverifiedFrame) != 0);
        Assert.Equal([null, 1, null, 3, 4], l.Select(x => x.PreviousBar));                       // an unverified frame is no crossover predecessor
        Assert.Equal(0, r.Consumer.Status.GapsDetected);                                         // not a sequence gap
    }

    [Fact]
    public async Task SiblingBarsOfOneMarketEvent_AreAllProcessedInOrder_EachAsItsOwnDecision()
    {
        var r = new Rig();
        var t = End(5);
        await r.Feed(Light(1, 1)); await r.Feed(Light(1, 2)); await r.Feed(Light(1, 3)); await r.Feed(Light(1, 4));
        var a = Light(1, 5, end: t, tick: 77, ordinal: 0);
        var b = Light(1, 6, end: t, tick: 77, ordinal: 1, duration: 0);
        var c = Light(1, 7, end: t, tick: 77, ordinal: 2, duration: 0);
        var later = Light(1, 8, end: t.AddSeconds(30), tick: 90, duration: 30);
        foreach (var f in new[] { a, b, c, later }) await r.Feed(f);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], r.Processor.Log.Select(x => x.Bar));
        Assert.Equal(0, r.Consumer.Status.Rejected);
        Assert.False(b.IsEligibleFillBarFor(a)); Assert.False(c.IsEligibleFillBarFor(a)); Assert.True(later.IsEligibleFillBarFor(c));
        // a sibling with a wrong ordinal is a causality violation, not processed
        await r.Feed(Light(1, 9, end: t.AddSeconds(30), tick: 90, ordinal: 5, duration: 0));
        Assert.Equal(1, r.Consumer.Status.Rejected); Assert.Equal(8, r.Processor.Log.Count);
    }

    [Fact]
    public async Task MissingOrStaleMetrics_YieldUnknown_SoNoStrategyEntersOnThem()
    {
        var r = new Rig();
        await r.Feed(Light(1, 1)); await r.Feed(Light(1, 2));
        await r.Feed(Light(1, 3, omitStrict: true));                                              // the options metric is unavailable on this bar
        var withValue = r.Processor.Log[1].Signals; var without = r.Processor.Log[2].Signals;
        Assert.NotEqual(withValue, without);
        // every call/put signal is False/Unknown ('0' or '2') for a missing metric - never True ('1')
        Assert.All(Enumerable.Range(0, 30), i => { Assert.NotEqual('1', without[(i * 3) + 1]); });  // buyPut (strict < x) cannot be True without the metric
        Assert.All(Enumerable.Range(0, 30), i => Assert.Equal('2', without[(i * 3) + 2]));         // the exit cross is Unknown without the metric
    }

    // ---- consumer failure and restart ----

    [Fact]
    public async Task ConsumerFault_StopsTheConsumer_FramesQueueAndRestartReplaysTheJournalIntoAFreshProcessor()
    {
        var r = new Rig(new HandoffOptions(16, TimeSpan.FromHours(1), 2));
        r.Clock.Now = T0.AddMinutes(30);
        r.Processor.OnProcess = f => { if (f.Identity.BarSeq == 4) throw new InvalidOperationException("strategy bug"); };
        for (var i = 1; i <= 9; i++) r.Handoff.Offer(Light(1, i));
        Assert.Equal(ConsumerExit.Faulted, await r.Consumer.RunAsync(CancellationToken.None));
        var st = r.Consumer.Status;
        Assert.True(st.Faulted); Assert.Contains("strategy bug", st.FaultMessage);
        Assert.Equal([1, 2, 3], r.Processor.Log.Select(l => l.Bar));
        Assert.Equal(5, r.Handoff.Stats().QueueDepth);                                           // 5..9 are still waiting - nothing was lost, nothing was reprocessed

        // restart: a fresh processor is rebuilt from the journal (frame 4 was journaled BEFORE it faulted), then the queue continues
        var fresh = new StrategyDigestProcessor(StrategyDigestProcessor.Thirty());
        var restarted = new FrameConsumer(r.Handoff, r.Options, r.Journal, fresh, r.Clock);
        await restarted.RecoverFromJournalAsync(1, default);
        Assert.Equal([1, 2, 3, 4], fresh.Log.Select(l => l.Bar));                                 // replayed with the journaled dispositions
        Assert.All(fresh.Log, l => Assert.True(l.Allowed));
        r.Handoff.Complete();
        Assert.Equal(ConsumerExit.Completed, await restarted.RunAsync(CancellationToken.None));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], fresh.Log.Select(l => l.Bar));
        Assert.Equal([false, true, true, true, true], fresh.Log.Skip(4).Select(l => l.Allowed));   // restart: entries blocked until 2 clean frames (5 -> run 1 blocked, 6 -> run 2 allowed ...)
        Assert.Equal(0, restarted.Status.GapsDetected);                                           // the restart itself is not a sequence gap
        Assert.False(restarted.Status.Faulted);
    }

    [Fact]
    public async Task ReplayAfterAFault_EqualsTheStateAnUninterruptedRunWouldHaveHad()
    {
        var frames = Enumerable.Range(1, 12).Select(i => Light(1, i)).ToList();
        var reference = new Rig(); foreach (var f in frames) await reference.Feed(f);
        var faulty = new Rig();
        faulty.Processor.OnProcess = f => { if (f.Identity.BarSeq == 7) throw new InvalidOperationException("boom"); };
        foreach (var f in frames) await faulty.Feed(f);
        Assert.True(faulty.Consumer.Status.Faulted);
        Assert.Equal(6, faulty.Processor.Log.Count);
        var fresh = new StrategyDigestProcessor(StrategyDigestProcessor.Thirty());
        await FrameReplayer.ReplayAsync(faulty.Journal, 1, fresh, default);
        var upTo7 = new Rig(); foreach (var f in frames.Take(7)) await upTo7.Feed(f);
        Assert.Equal(upTo7.Processor.Digest, fresh.Digest);                                        // replay reproduces exactly bars 1..7 as an uninterrupted run would
        Assert.NotEqual(reference.Processor.Digest, fresh.Digest);                                  // (and is not pretending to know bars 8..12)
    }

    // ---- journal faults and durability honesty ----

    [Fact]
    public async Task JournalFault_BlocksEntries_IsFlagged_AndNeverClaimsDurableRecording()
    {
        var r = new Rig();
        await r.Feed(Light(1, 1)); await r.Feed(Light(1, 2));
        r.Journal.BeforeAppend = kind => { if (kind == "frame") throw new IOException("store down"); };
        await r.Feed(Light(1, 3));
        Assert.True(r.Consumer.Status.JournalFaulted);
        Assert.False(r.Consumer.Status.ClaimsDurableRecording);
        var l = r.Processor.Log;
        Assert.False(l[2].Allowed); Assert.True((l[2].Reasons & IntegrityFlags.JournalFault) != 0);
        Assert.Equal(3, l.Count);                                                                   // processed (exits must still be manageable) but with entries blocked
        r.Journal.BeforeAppend = null;
        await r.Feed(Light(1, 4)); await r.Feed(Light(1, 5)); await r.Feed(Light(1, 6));
        Assert.Equal([true, true, false, false, false, true], r.Processor.Log.Select(x => x.Allowed));   // 3: journal down; 4: journal still marked faulted (run reset); 5: run 1; 6: run 2 -> allowed
        var j = await r.Journal.ReadSessionAsync(1, default);
        Assert.DoesNotContain(j.Frames, f => f.BarSeq == 3);                                        // bar 3 is NOT in the journal: replay can never reproduce this run
        Assert.False(new InMemoryFrameJournal().IsDurable);                                         // the 6b journal says so itself
    }

    [Fact]
    public async Task InMemoryJournal_IsIdempotent_KeyUnique_AndOrdered()
    {
        var j = new InMemoryFrameJournal();
        var f = Light(1, 1);
        Assert.Equal(AppendOutcome.Appended, await j.AppendFrameAsync(FrameRecord.From(f), default));
        Assert.Equal(AppendOutcome.DuplicateSame, await j.AppendFrameAsync(FrameRecord.From(f), default));
        Assert.Equal(AppendOutcome.ConflictDifferent, await j.AppendFrameAsync(FrameRecord.From(Light(1, 1, strict: 5)), default));
        var d = new DispositionRecord(FrameRecord.CurrentSchema, 1, 1, true, IntegrityFlags.None, 5, 2, false, false);
        Assert.Equal(AppendOutcome.Appended, await j.AppendDispositionAsync(d, default));
        Assert.Equal(AppendOutcome.DuplicateSame, await j.AppendDispositionAsync(d, default));
        Assert.Equal(AppendOutcome.ConflictDifferent, await j.AppendDispositionAsync(d with { EntriesAllowed = false }, default));
        await j.AppendGapAsync(new GapRecord(FrameRecord.CurrentSchema, 1, 2, 3, GapCause.Overflow, IntegrityFlags.Overflow, Durable: true), default);
        var s = await j.ReadSessionAsync(1, default);
        Assert.Single(s.Frames); Assert.Single(s.Dispositions); Assert.Single(s.Gaps);
        Assert.False(s.Gaps[0].Durable);                                                            // an in-memory store can never record durably, whatever the caller says
        Assert.Equal(["pev2-journal/1"], new[] { s.Frames[0].Schema });
        Assert.Empty((await j.ReadSessionAsync(99, default)).Frames);
    }

    // ---- deterministic replay ----

    [Fact]
    public async Task Replay_EqualsTheLiveRun_IncludingGapsLagUnverifiedAndDuplicates()
    {
        var r = new Rig();
        var seq = new List<(MarketStateFrame F, double Lag)>
        {
            (Light(1, 1), .5), (Light(1, 2), .5), (Light(1, 2), .5), (Light(1, 3), .5), (Light(1, 6), .5), (Light(1, 7), 30), (Light(1, 8, verified: false), .5), (Light(1, 9), .5), (Light(1, 10), .5), (Light(1, 11), .5),
            (Light(1, 11, strict: 3), .5), (Light(1, 12), .5),
        };
        foreach (var (f, lag) in seq) await r.Feed(f, lag);
        var fresh = new StrategyDigestProcessor(StrategyDigestProcessor.Thirty());
        var replay = await FrameReplayer.ReplayAsync(r.Journal, 1, fresh, default);
        Assert.Equal(r.Processor.Log.Count, replay.FramesReplayed);
        Assert.Equal(r.Processor.Digest, fresh.Digest);                                             // bit-for-bit
        Assert.Equal(r.Processor.Log.Select(l => (l.Bar, l.Allowed, l.PreviousBar, l.Signals)), fresh.Log.Select(l => (l.Bar, l.Allowed, l.PreviousBar, l.Signals)));
        // and replay twice gives the same again
        var again = new StrategyDigestProcessor(StrategyDigestProcessor.Thirty());
        await FrameReplayer.ReplayAsync(r.Journal, 1, again, default);
        Assert.Equal(fresh.Digest, again.Digest);
    }

    sealed class TamperingJournal(IFrameJournal inner, Func<FrameRecord, FrameRecord> tamper) : IFrameJournal
    {
        public bool IsDurable => inner.IsDurable;
        public ValueTask<AppendOutcome> AppendFrameAsync(FrameRecord r, CancellationToken ct) => inner.AppendFrameAsync(r, ct);
        public ValueTask<AppendOutcome> AppendDispositionAsync(DispositionRecord r, CancellationToken ct) => inner.AppendDispositionAsync(r, ct);
        public ValueTask AppendGapAsync(GapRecord r, CancellationToken ct) => inner.AppendGapAsync(r, ct);
        public async ValueTask<JournalSession> ReadSessionAsync(long sessionId, CancellationToken ct)
        {
            var s = await inner.ReadSessionAsync(sessionId, ct);
            return s with { Frames = s.Frames.Select(tamper).ToList() };
        }
    }

    [Fact]
    public async Task Replay_RefusesATamperedOrCorruptJournalPayload()
    {
        var r = new Rig();
        for (var i = 1; i <= 4; i++) await r.Feed(Light(1, i));
        var processor = new StrategyDigestProcessor(StrategyDigestProcessor.Thirty());
        var changedPayload = new TamperingJournal(r.Journal, f => f.BarSeq == 3 ? f with { PayloadJson = f.PayloadJson.Replace("\"barSeq\":3", "\"barSeq\":3 ") } : f);
        await Assert.ThrowsAsync<ReplayIntegrityException>(() => FrameReplayer.ReplayAsync(changedPayload, 1, processor, default));
        var wrongHash = new TamperingJournal(r.Journal, f => f.BarSeq == 2 ? f with { FrameHash = new string('0', 64) } : f);
        await Assert.ThrowsAsync<ReplayIntegrityException>(() => FrameReplayer.ReplayAsync(wrongHash, 1, new StrategyDigestProcessor(StrategyDigestProcessor.Thirty()), default));
        var garbage = new TamperingJournal(r.Journal, f => f.BarSeq == 4 ? f with { PayloadJson = "{}", FrameHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("{}"u8.ToArray())).ToLowerInvariant() } : f);
        await Assert.ThrowsAsync<ReplayIntegrityException>(() => FrameReplayer.ReplayAsync(garbage, 1, new StrategyDigestProcessor(StrategyDigestProcessor.Thirty()), default));
    }

    [Fact]
    public async Task Replay_NeverLooksAhead_FramesAreFedStrictlyInJournalOrder()
    {
        var r = new Rig();
        for (var i = 1; i <= 8; i++) await r.Feed(Light(1, i));
        var seen = new List<int>();
        var p = new StrategyDigestProcessor(StrategyDigestProcessor.Thirty()) { OnProcess = f => seen.Add(f.Identity.BarSeq) };
        var result = await FrameReplayer.ReplayAsync(r.Journal, 1, p, default);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], seen);
        Assert.Equal(0, result.FramesWithoutDisposition);
        Assert.All(p.Log, l => Assert.True(l.PreviousBar is null || l.PreviousBar < l.Bar));
    }

    [Fact]
    public async Task AFrameJournaledWithoutADisposition_ReplaysWithEntriesBlocked()
    {
        var j = new InMemoryFrameJournal();
        await j.AppendFrameAsync(FrameRecord.From(Light(1, 1)), default);                          // crash between the two appends
        var p = new StrategyDigestProcessor(StrategyDigestProcessor.Thirty());
        var res = await FrameReplayer.ReplayAsync(j, 1, p, default);
        Assert.Equal(1, res.FramesWithoutDisposition);
        Assert.False(p.Log[0].Allowed);
        Assert.True((p.Log[0].Reasons & IntegrityFlags.ConsumerFault) != 0);
    }
}
