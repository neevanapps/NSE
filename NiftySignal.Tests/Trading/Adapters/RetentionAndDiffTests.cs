using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Trading;

namespace NiftySignal.Tests.Trading.Adapters;

public class RetentionTests
{
    private static readonly IReadOnlyList<SourceTick> Rows = SyntheticSession.Rows();
    private static readonly SessionFreeze Freeze = SyntheticSession.Freeze(Rows);

    private static readonly EngineEvent[] Faults =
    {
        new FeedHealthEvent(SyntheticSession.Ist(9, 40, 10), 1, FeedHealthState.Degraded, FeedFaultReason.DataGapOpen, "scripted"),
        new FeedHealthEvent(SyntheticSession.Ist(9, 41, 0), 2, FeedHealthState.Healthy, FeedFaultReason.None, "scripted"),
    };

    [Fact]
    public void PruningLoopMemoryQuoteHistoryAndRememberedIds_DoesNotChangeAnyChannel()
    {
        RunResult Run(RetentionPolicy retention, TimeSpan seenIds)
        {
            var translator = SyntheticSession.Translator(Freeze) with { SeenIdRetention = seenIds };
            var run = new TradingRun(Freeze, translator, SyntheticSession.Config(), SyntheticSession.Strategies(), new RunSettings { Retention = retention }, new RecorderOptions(), Faults);
            return HistoricalAdapter.Run(run, Rows);
        }

        var generous = Run(RetentionPolicy.Unbounded, TimeSpan.FromDays(2));
        var defaults = Run(RetentionPolicy.Default, TimeSpan.FromMinutes(10));
        var aggressive = Run(new RetentionPolicy { LoopSeenRetention = TimeSpan.FromSeconds(5), QuoteHistoryRetention = TimeSpan.FromSeconds(3) }, TimeSpan.FromSeconds(5));

        Assert.All(RunRecorders.Names.All, n =>
        {
            Assert.Equal(generous.Channels[n].Hash, defaults.Channels[n].Hash);
            Assert.Equal(generous.Channels[n].Hash, aggressive.Channels[n].Hash);
        });
        Assert.True(generous.Outputs > 100 && generous.Fills > 5);

        // and the memory really is bounded
        var delivered = generous.Loop.EventsDelivered;
        Assert.Equal(delivered, aggressive.Loop.EventsDelivered);
        Assert.Equal((int)delivered, generous.SeenInLoop);                                                       // unbounded: every delivered event is remembered
        Assert.InRange(defaults.SeenInLoop, 1, (int)delivered / 5);
        Assert.InRange(aggressive.SeenInLoop, 1, (int)delivered / 50);
        Assert.True(generous.Translator.SeenIdCount > aggressive.Translator.SeenIdCount * 20);
    }

    [Fact]
    public void AReDeliveryOlderThanTheRetention_IsClassifiedByTheDocumentedFallback_AndNeverReachesTheEngine()
    {
        var translator = Tk.Translator(options: new TranslatorOptions { StableOrderingLag = TimeSpan.Zero, SeenIdRetention = TimeSpan.FromSeconds(10), AcceptFromUtc = SyntheticSession.Ist(9, 15) });
        var first = Tk.Row("CE23900", 1, Tk.T, 100m, 99m, 101m);
        translator.Offer(first);
        for (var i = 2; i < 40; i++)
        {
            translator.Offer(Tk.Row("CE23900", i, Tk.T.AddSeconds(i), 100m + (i % 5), 99m, 101m));
        }

        var disposition = translator.Offer(first);                                                               // exact duplicate, but 38 s old: beyond the 10 s memory

        Assert.Equal(RowDisposition.Quarantined, disposition);
        Assert.Equal(RowRejection.IdOutOfOrder, translator.Quarantines.Single().Reason);
        Assert.True(translator.Quarantines.Single().Transport);
        Assert.Single(translator.Finish().SelectMany(g => g.Quotes), q => q.Event.SourceId == 1);                // the original is still translated exactly once
    }
}

public class RunDiffBehaviourTests
{
    private static readonly IReadOnlyList<SourceTick> Rows = SyntheticSession.Rows(new SyntheticSession.Options { Windows = new[] { (SyntheticSession.Ist(9, 15), SyntheticSession.Ist(9, 40)) } });
    private static readonly SessionFreeze Freeze = SyntheticSession.Freeze(Rows);

    [Fact]
    public void IdenticalRuns_AreEquivalent_AndTheReportListsTheExclusions()
    {
        var report = RunDiff.Compare(Runs.Hist(Rows, Freeze), Runs.Hist(Rows, Freeze, page: 17));

        Assert.True(report.Equivalent);
        Assert.Empty(report.Divergences);
        Assert.Null(report.First);
        Assert.All(report.Channels, c => Assert.True(c.Equal));
        Assert.Contains(RunDiff.Exclusions, e => e.Contains("Transport diagnostics", StringComparison.Ordinal));
        Assert.Contains(RunDiff.Exclusions, e => e.Contains("RunId and RunMode", StringComparison.Ordinal));
        Assert.StartsWith("EQUIVALENT", RunDiff.Render(report), StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingRow_IsLocatedByTheTwoPassSearch_WithIdentifiersAndTimestamps_ExactlyAsAFullCaptureFindsIt()
    {
        var missing = Rows[Rows.Count * 2 / 3];
        var damaged = Rows.Where(r => r.Id != missing.Id).ToList();

        var twoPass = RunDiff.Compare(Runs.Hist(Rows, Freeze), Runs.Hist(damaged, Freeze), new DiffOptions { CheckpointEvery = 100 });
        var full = RunDiff.Compare(Runs.Hist(Rows, Freeze), Runs.Hist(damaged, Freeze), new DiffOptions { FullCapture = true });

        Assert.False(twoPass.Equivalent);
        Assert.Equal(full.First!.Channel, twoPass.First!.Channel);
        Assert.Equal(full.First.LineIndex, twoPass.First.LineIndex);
        Assert.Equal(full.First.KeyA, twoPass.First.KeyA);
        Assert.Equal(RunRecorders.Names.Quotes, twoPass.First.Channel);
        Assert.EndsWith($":{missing.Id}", twoPass.First.KeyA, StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, twoPass.First.TimeA);
        Assert.Contains(twoPass.Divergences, d => d.Channel == RunRecorders.Names.Candles || d.Channel == RunRecorders.Names.Bars || d.Channel == RunRecorders.Names.Outputs || d.Channel == RunRecorders.Names.Quotes);
        Assert.Equal(twoPass.Divergences.Select(d => d.Channel).Order().ToArray(), full.Divergences.Select(d => d.Channel).Order().ToArray());
    }

    [Fact]
    public void ATruncatedRun_ReportsWhereTheShorterStreamEnds()
    {
        var shorter = Rows.Take(Rows.Count - 60).ToList();

        var report = RunDiff.Compare(Runs.Hist(Rows, Freeze), Runs.Hist(shorter, Freeze), new DiffOptions { CheckpointEvery = 50 });

        Assert.False(report.Equivalent);
        var quotes = report.Divergences.Single(d => d.Channel == RunRecorders.Names.Quotes);
        Assert.Contains("stream B ends", quotes.Note, StringComparison.Ordinal);
        Assert.Equal("-", quotes.KeyB);
        Assert.Equal(report.B.Channels[RunRecorders.Names.Quotes].Count, quotes.LineIndex);
    }
}

public class WatchdogAndHarnessTests
{
    private static readonly DateTimeOffset From = SyntheticSession.Ist(9, 15);
    private static readonly DateTimeOffset Through = SyntheticSession.Ist(15, 30);

    [Fact]
    public void WatchdogThresholds_AreExplicit_Labelled_AndValidated()
    {
        var testOnly = WatchdogOptions.TestOnly(From, Through);
        Assert.StartsWith("TEST-ONLY", testOnly.Provenance, StringComparison.Ordinal);
        Assert.Empty(testOnly.Validate());
        Assert.NotEmpty((testOnly with { NoMessageTimeout = TimeSpan.Zero }).Validate());
        Assert.NotEmpty((testOnly with { FailedPollsBeforeDegraded = 0 }).Validate());
        Assert.NotEmpty((testOnly with { MonitorThroughUtc = From }).Validate());
        Assert.NotEmpty((testOnly with { Provenance = " " }).Validate());
        Assert.Throws<ArgumentException>(() => new FeedWatchdog(testOnly with { NoMessageTimeout = TimeSpan.FromSeconds(-1) }));
        // there is no default: every threshold is a required member, so a caller cannot omit one by accident
        Assert.All(
            new[] { "NoMessageTimeout", "FailedPollsBeforeDegraded", "MonitorFromUtc", "MonitorThroughUtc", "Provenance" },
            name => Assert.Contains(typeof(WatchdogOptions).GetProperties(), p => p.Name == name && p.GetCustomAttributes(typeof(System.Runtime.CompilerServices.RequiredMemberAttribute), false).Length == 1));
    }

    [Fact]
    public void Watchdog_StateMachine_DegradesOnSilenceInsideTheWindowOnly_AndRecoversByReason()
    {
        var w = new FeedWatchdog(WatchdogOptions.TestOnly(From, Through, noMessage: TimeSpan.FromSeconds(30), failedPolls: 2));
        var t = From;

        Assert.Null(w.Observe(t.AddSeconds(10), PollStatus.Ok, 5, t.AddSeconds(9)));                                  // traffic
        Assert.Null(w.Observe(t.AddSeconds(39), PollStatus.Ok, 0, null));                                             // 30 s of silence: not yet "beyond"
        var degraded = w.Observe(t.AddSeconds(41), PollStatus.Ok, 0, null);
        Assert.Equal((FeedHealthState.Degraded, FeedFaultReason.NoMessageWatchdog), (degraded!.State, degraded.Reason));
        Assert.Null(w.Observe(t.AddSeconds(60), PollStatus.Ok, 0, null));                                             // already degraded: no repeat
        var healthy = w.Observe(t.AddSeconds(70), PollStatus.Ok, 3, t.AddSeconds(69));
        Assert.Equal(FeedHealthState.Healthy, healthy!.State);
        Assert.Equal(new long[] { 1, 2 }, new[] { degraded.SourceId, healthy.SourceId });                            // strictly increasing health sequence

        Assert.Null(w.Observe(t.AddSeconds(75), PollStatus.Disconnected, 0, null));                                   // one failed poll is below the threshold
        Assert.Null(w.Observe(t.AddSeconds(76), PollStatus.Ok, 1, t.AddSeconds(76)));                                 // and a good poll resets the streak
        Assert.Null(w.Observe(t.AddSeconds(77), PollStatus.Disconnected, 0, null));
        var disconnected = w.Observe(t.AddSeconds(78), PollStatus.Disconnected, 0, null);
        Assert.Equal(FeedFaultReason.SocketDisconnected, disconnected!.Reason);
        var back = w.Observe(t.AddSeconds(79), PollStatus.Ok, 0, null);                                               // a successful poll ends a disconnect even without rows
        Assert.Equal(FeedHealthState.Healthy, back!.State);

        var outside = new FeedWatchdog(WatchdogOptions.TestOnly(From, Through));
        Assert.Null(outside.Observe(Through.AddHours(1), PollStatus.Ok, 0, null));                                    // silence after the monitoring window is normal (closed market)
        Assert.Null(outside.Observe(From.AddSeconds(-5), PollStatus.Ok, 0, null));                                    // and before it
    }

    [Fact]
    public void HarnessSettings_AreLabelled_KeepTheConfirmedContract_AndAllowACloseAttemptBeforeFinalClose()
    {
        var config = HarnessSettings.Config("labels", SyntheticSession.Variants());

        Assert.StartsWith("HARNESS-TEST-ONLY:", config.ConfigVersion, StringComparison.Ordinal);
        Assert.Contains(HarnessSettings.Provenance, p => p.StartsWith("Brokerage: TEST-ONLY", StringComparison.Ordinal));
        Assert.Contains(HarnessSettings.Provenance, p => p.StartsWith("Liquidation start: TEST-ONLY", StringComparison.Ordinal));
        Assert.Contains(HarnessSettings.Provenance, p => p.StartsWith("Watchdog thresholds: TEST-ONLY", StringComparison.Ordinal));
        // confirmed contract values are untouched
        Assert.Equal(new TimeOnly(9, 30), config.Session.EntryOpen);
        Assert.Equal(new TimeOnly(15, 0), config.Session.EntryClose);
        Assert.Equal(new TimeOnly(15, 10), config.Session.FinalClose);
        Assert.Equal(100m, config.Selection.MinimumEntryPremium);
        Assert.Equal(150m, config.Selection.MaximumEntryPremium);
        Assert.Equal("ResearchV1", config.Risk.Name);
        Assert.Null(config.Risk.MaxTradesPerDay);
        Assert.Equal(NiftySignal.Trading.Core.Risk.DailySlEnforcement.None, config.Risk.DailySlEnforcement);
        Assert.Equal(650, Assert.IsType<NiftySignal.Trading.Core.Sizing.FixedQuantitySpec>(config.Variants["P6/A"].Sizing).Units);
        var brokerage = Assert.IsType<FlatBrokerage>(config.Costs.Schedules.Single().Brokerage);
        Assert.Equal((20m, BrokerageUnit.PerOrder), (brokerage.Amount, brokerage.Unit));
        Assert.Empty(config.Validate());
        // a first close attempt (liquidation start + latency) executes before 15:10 and the engine accepts it; a later one is refused
        Assert.True(HarnessSettings.LiquidationStart.ToTimeSpan() + config.Execution.Latency < config.Session.FinalClose.ToTimeSpan());
        _ = new TradingEngine(config, SyntheticSession.Day, SyntheticSession.Freeze(Array.Empty<SourceTick>(), anchor: false).ToContracts(), SyntheticSession.Strategies());
        var tooLate = config with { Session = config.Session with { LiquidationStart = new TimeOnly(15, 9, 59, 500) } };
        Assert.Throws<ArgumentException>(() => new TradingEngine(tooLate, SyntheticSession.Day, SyntheticSession.Freeze(Array.Empty<SourceTick>(), anchor: false).ToContracts(), SyntheticSession.Strategies()));
    }

    [Fact]
    public void ResearchV1_RunsUnrestricted_ManyTradesInADay_NoCapNoLockout()
    {
        var rows = SyntheticSession.Rows();
        var freeze = SyntheticSession.Freeze(rows);

        var result = Runs.Hist(rows, freeze, tweak: c => c with { Variants = c.Variants.ToDictionary(v => v.Key, v => v.Value with { DailySl = 1m }) })(new RecorderOptions());

        Assert.True(result.Positions >= 5);                                                                     // several trades in the day, none blocked by a count or by the observed daily SL
        Assert.Contains("DailySlBreached", string.Join("\n", Runs.Hist(rows, freeze, tweak: c => c with { Variants = c.Variants.ToDictionary(v => v.Key, v => v.Value with { DailySl = 1m }) })(new RecorderOptions { CaptureAll = true }).Captured[RunRecorders.Names.Audit].Select(l => l.Line)), StringComparison.Ordinal);
    }
}
