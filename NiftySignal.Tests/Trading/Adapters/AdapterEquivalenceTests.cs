using NiftySignal.Tests.Trading.Core;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Entry;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Integrity;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Tests.Trading.Adapters;

/// <summary>Valid on every bar, always on the call side: a plumbing strategy for scenarios that need one opportunity to survive.</summary>
internal sealed class AlwaysCallStrategy : IBaseStrategy
{
    public string StrategyId => "ALWAYS";

    public string Version => "plumbing-always-v1";

    public StrategyValidity OnBar(BarCompletedEvent bar) => new(true, OptionSide.Call, "always");
}

internal static class Runs
{
    public static DateTimeOffset PollStart => SyntheticSession.Ist(9, 14, 59);

    public static Func<RecorderOptions, RunResult> Hist(
        IReadOnlyList<SourceTick> rows,
        SessionFreeze freeze,
        int page = 5_000,
        IEnumerable<EngineEvent>? controls = null,
        Func<TradingConfig, TradingConfig>? tweak = null,
        RunSettings? settings = null,
        TimeSpan? lag = null,
        IReadOnlyDictionary<string, IBaseStrategy>? strategies = null)
        => rec => HistoricalAdapter.Run(
            new TradingRun(freeze, SyntheticSession.Translator(freeze, lag), SyntheticSession.Config(tweak), strategies ?? SyntheticSession.Strategies(), settings ?? new RunSettings { RunMode = "Historical" }, rec, controls),
            rows,
            new HistoricalOptions { PageSize = page });

    public static Func<RecorderOptions, LivePaperResult> LiveFull(
        IPollSource source,
        SessionFreeze freeze,
        IEnumerable<EngineEvent>? controls = null,
        WatchdogOptions? watchdog = null,
        Func<TradingConfig, TradingConfig>? tweak = null,
        TimeSpan? lag = null,
        IReadOnlyDictionary<string, IBaseStrategy>? strategies = null,
        RunSettings? settings = null)
        => rec => LivePaperAdapter.Run(
            new TradingRun(freeze, SyntheticSession.Translator(freeze, lag), SyntheticSession.Config(tweak), strategies ?? SyntheticSession.Strategies(), settings ?? new RunSettings { RunMode = "LivePaper" }, rec, controls),
            source,
            new LivePaperOptions { Watchdog = watchdog });

    public static Func<RecorderOptions, RunResult> Live(
        IReadOnlyList<SourceTick> rows,
        SessionFreeze freeze,
        IReadOnlyList<PollStep> steps,
        IEnumerable<EngineEvent>? controls = null,
        Func<TradingConfig, TradingConfig>? tweak = null,
        TimeSpan? lag = null,
        IReadOnlyDictionary<string, IBaseStrategy>? strategies = null)
        => rec => LiveFull(RecordedPollSource.Cycle(rows, steps, PollStart), freeze, controls, null, tweak, lag, strategies)(rec).Run;

    public static void AssertEquivalent(DiffReport report) => Assert.True(report.Equivalent, RunDiff.Render(report));

    public static IReadOnlyDictionary<string, IBaseStrategy> StrategiesWithAlways()
        => new Dictionary<string, IBaseStrategy>(SyntheticSession.Strategies()) { ["ALWAYS"] = new AlwaysCallStrategy() };
}

public class AdapterEquivalenceTests
{
    private static readonly IReadOnlyList<SourceTick> Rows = SyntheticSession.Rows();
    private static readonly SessionFreeze Freeze = SyntheticSession.Freeze(Rows);

    [Theory]
    [InlineData(1)]
    [InlineData(13)]
    [InlineData(997)]
    [InlineData(1_000_000)]
    public void Historical_ResultsDoNotDependOnThePageSize(int page)
    {
        var report = RunDiff.Compare(Runs.Hist(Rows, Freeze), Runs.Hist(Rows, Freeze, page));

        Runs.AssertEquivalent(report);
        Assert.True(report.A.Outputs > 100);
        Assert.True(report.A.Fills > 5);                                                   // the scenario actually trades (plumbing strategies, no edge claim)
        Assert.True(report.A.Translator.GroupsReleased > 20_000);
    }

    private static PollStep[] Script(string name) => name switch
    {
        "coarse" => new[] { new PollStep(TimeSpan.FromMilliseconds(250), 100_000) },
        "tiny batches" => new[] { new PollStep(TimeSpan.FromMilliseconds(200), 3) },
        "mixed" => new[]
        {
            new PollStep(TimeSpan.FromMilliseconds(100), 50),
            new PollStep(TimeSpan.FromSeconds(1), 7),
            new PollStep(TimeSpan.FromSeconds(3), 10_000),
            new PollStep(TimeSpan.FromMilliseconds(250), 1),
            new PollStep(TimeSpan.FromSeconds(5), 10_000),
        },
        "overlapping polls" => new[] { new PollStep(TimeSpan.FromMilliseconds(300), 100_000, Overlap: 5) },
        "delayed batches" => new[] { new PollStep(TimeSpan.FromSeconds(45), 100_000) },
        "failed polls without a watchdog" => new[]
        {
            new PollStep(TimeSpan.FromMilliseconds(250), 100_000),
            new PollStep(TimeSpan.FromSeconds(1), 100, 0, true),
            new PollStep(TimeSpan.FromMilliseconds(250), 100_000),
        },
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [InlineData("coarse")]
    [InlineData("tiny batches")]
    [InlineData("mixed")]
    [InlineData("overlapping polls")]
    [InlineData("delayed batches")]
    [InlineData("failed polls without a watchdog")]
    public void LivePaper_MatchesHistorical_WhateverThePollingPattern(string name)
    {
        var steps = Script(name);
        var report = RunDiff.Compare(Runs.Hist(Rows, Freeze), Runs.Live(Rows, Freeze, steps));

        Runs.AssertEquivalent(report);
        Assert.True(report.B.Transport.Polls > 10, name);
        Assert.Equal(report.A.Translator.Translated, report.B.Translator.Translated);
        Assert.Equal(0, report.A.Translator.Quarantined);
        if (name.StartsWith("overlapping", StringComparison.Ordinal))
        {
            Assert.True(report.B.Translator.Redelivered > 100, "overlapping polls must actually redeliver rows (transport-only, excluded from the comparison)");
            Assert.Equal(0, report.A.Translator.Redelivered);
            // After the long midday gap the overlap window can contain a row older than the remembered-id retention: it can no longer be told
            // from a stray id and is refused as IdOutOfOrder (transport). It never reaches the quotes, so nothing differs.
            Assert.All(report.B.Translator.QuarantinedByReason.Keys, k => Assert.Equal("IdOutOfOrder", k));
            Assert.InRange(report.B.Translator.Quarantined, 0, 5);
        }
        else
        {
            Assert.Equal(0, report.B.Translator.Quarantined);
        }

        if (name.StartsWith("failed", StringComparison.Ordinal))
        {
            Assert.True(report.B.Transport.FailedPolls > 10);
        }
    }

    [Fact]
    public void RepeatedRuns_AreIdenticalInEveryChannel_AndIndependentOfTheMachineCulture()
    {
        var steps = new[] { new PollStep(TimeSpan.FromMilliseconds(400), 25), new PollStep(TimeSpan.FromSeconds(2), 100_000) };
        var baseline = Runs.Live(Rows, Freeze, steps)(new RecorderOptions());

        foreach (var culture in new[] { "tr-TR", "de-DE", "ar-SA", "th-TH" })
        {
            var other = Culture.Under(culture, () => Runs.Live(Rows, Freeze, steps)(new RecorderOptions()));
            Assert.All(RunRecorders.Names.All, n => Assert.Equal(baseline.Channels[n].Hash, other.Channels[n].Hash));
        }

        for (var i = 0; i < 2; i++)
        {
            var again = Runs.Hist(Rows, Freeze)(new RecorderOptions());
            Assert.All(RunRecorders.Names.All, n => Assert.Equal(baseline.Channels[n].Hash, again.Channels[n].Hash));
        }
    }

    [Fact]
    public void EqualTimeGroups_BarsAndDeadlines_AreExercised()
    {
        var result = Runs.Hist(Rows, Freeze)(new RecorderOptions { CaptureAll = true });

        Assert.True(result.Translator.GroupsReleased < result.Translator.Translated);        // many rows share an availability instant
        var barTimes = result.Captured[RunRecorders.Names.Bars].Select(l => l.Line[..28]).ToList();
        var quoteTimes = result.Captured[RunRecorders.Names.Quotes].Select(l => l.Line[..28]).ToHashSet(StringComparer.Ordinal);
        Assert.All(barTimes, t => Assert.Contains(t, quoteTimes));                           // every bar closes at an instant that also carries quotes
        Assert.True(result.Loop.DeadlinesFired > 20);                                         // order maturities, expiries, session boundaries
        Assert.Equal(Runs.Hist(Rows, Freeze)(new RecorderOptions()).Channels[RunRecorders.Names.Outputs].Hash, result.Channels[RunRecorders.Names.Outputs].Hash);
    }

    // ------------------------------------------------------------------ fault scenarios: the same health / control events reach both paths

    private static IReadOnlyList<EngineEvent> Faults() => new EngineEvent[]
    {
        new FeedHealthEvent(SyntheticSession.Ist(9, 40, 10), 1, FeedHealthState.Degraded, FeedFaultReason.DataGapOpen, "scripted"),
        new FeedHealthEvent(SyntheticSession.Ist(9, 41, 0), 2, FeedHealthState.Healthy, FeedFaultReason.None, "scripted"),
        new OperatorCommandEvent(SyntheticSession.Ist(9, 50, 0), 1, OperatorCommand.Kill, "*", "tester", "scripted kill"),
        new OperatorCommandEvent(SyntheticSession.Ist(9, 52, 0), 2, OperatorCommand.Reset, "*", "tester", "scripted reset"),
    };

    [Fact]
    public void ScriptedHealthAndKillEvents_ProduceIdenticalRuns_AndTheirEffectsAreVisible()
    {
        var steps = new[] { new PollStep(TimeSpan.FromMilliseconds(250), 100_000), new PollStep(TimeSpan.FromSeconds(1), 5) };

        var report = RunDiff.Compare(Runs.Hist(Rows, Freeze, controls: Faults()), Runs.Live(Rows, Freeze, steps, controls: Faults()));

        Runs.AssertEquivalent(report);
        var audit = Runs.Hist(Rows, Freeze, controls: Faults())(new RecorderOptions { CaptureAll = true }).Captured[RunRecorders.Names.Audit].Select(l => l.Line).ToList();
        Assert.Contains(audit, l => l.Contains("|Integrity|", StringComparison.Ordinal) && l.Contains("Healthy->Degraded", StringComparison.Ordinal));
        Assert.Contains(audit, l => l.Contains("Degraded->Recovering", StringComparison.Ordinal));
        Assert.Contains(audit, l => l.Contains("Recovering->Healthy", StringComparison.Ordinal) && l.Contains("readyBar=b", StringComparison.Ordinal));
        Assert.Contains(audit, l => l.Contains("Kill:Killed", StringComparison.Ordinal));
        Assert.Contains(audit, l => l.Contains("Reset:Reset", StringComparison.Ordinal));
        Assert.Equal(4, report.A.Transport.ControlEventsDelivered);
        Assert.Equal(4, report.B.Transport.ControlEventsDelivered);
    }

    [Fact]
    public void WatchdogDerivedHealthEvents_AreJournaled_ReplayableFromTheJournal_AndEquivalentWhenHandedToTheHistoricalPath()
    {
        var rows = SyntheticSession.Rows(new SyntheticSession.Options
        {
            Windows = new[] { (SyntheticSession.Ist(9, 15), SyntheticSession.Ist(10, 5)) },
            Gaps = new[] { (SyntheticSession.Ist(9, 45), SyntheticSession.Ist(9, 47)) },
        });
        var freeze = SyntheticSession.Freeze(rows);
        var watchdog = WatchdogOptions.TestOnly(SyntheticSession.Ist(9, 15), SyntheticSession.Ist(15, 30), noMessage: TimeSpan.FromSeconds(30), failedPolls: 2);
        // a disconnect of exactly two polls at 09:35:00 (poll k happens 0.5 s x (k + 1) after 09:14:59)
        PollStep Script(int k) => k is 2400 or 2401 ? new PollStep(TimeSpan.FromMilliseconds(500), 100_000, 0, true) : new PollStep(TimeSpan.FromMilliseconds(500), 100_000);

        var live = Runs.LiveFull(new RecordedPollSource(rows, Script, Runs.PollStart), freeze, null, watchdog)(new RecorderOptions());

        var derived = live.Journal.Where(j => j.Health is not null).Select(j => j.Health!).ToList();
        Assert.Equal(
            new[] { (FeedHealthState.Degraded, FeedFaultReason.SocketDisconnected), (FeedHealthState.Healthy, FeedFaultReason.None), (FeedHealthState.Degraded, FeedFaultReason.NoMessageWatchdog), (FeedHealthState.Healthy, FeedFaultReason.None) },
            derived.Select(e => (e.State, e.Reason)).ToArray());
        Assert.InRange((derived[2].AvailableAt - SyntheticSession.Ist(9, 45)).TotalSeconds, 29, 33);                // detected at the first poll after 30 s of silence
        Assert.Equal(SyntheticSession.Ist(9, 35), derived[0].AvailableAt);                                          // second consecutive failed poll (the polls at 09:34:59.5 and 09:35:00.0)
        Assert.Equal(Canonical.Json(derived), Canonical.Json(FeedWatchdog.Replay(watchdog, live.Journal)));        // the journal alone reproduces the decisions

        var historical = RunDiff.Compare(Runs.Hist(rows, freeze, controls: derived), _ => live.Run);
        Runs.AssertEquivalent(historical);
        Assert.Equal(4, live.Run.Transport.ControlEventsDelivered);
        Assert.Equal(2, live.Run.Transport.FailedPolls);
    }

    // ------------------------------------------------------------------ concurrent variants, fixed contracts

    [Fact]
    public void IndependentVariants_AreUnaffectedByTheirNeighbours_AndKeepOneContractFromEntryToExit()
    {
        var both = Runs.Hist(Rows, Freeze)(new RecorderOptions { CaptureAll = true });
        var onlyA = Runs.Hist(Rows, Freeze, tweak: c => c with { Variants = c.Variants.Where(v => v.Key == "A").ToDictionary(v => v.Key, v => v.Value) })(new RecorderOptions { CaptureAll = true });

        List<string> Lines(RunResult r, string variant) => r.Captured[RunRecorders.Names.Outputs]
            .Where(l => l.Key.EndsWith(":" + variant, StringComparison.Ordinal))
            .Select(l => l.Line)
            .ToList();

        Assert.True(Lines(both, "A").Count > 30);
        Assert.True(Lines(both, "B").Count > 30);
        Assert.Equal(Lines(onlyA, "A"), Lines(both, "A"));                                                     // variant A behaves identically with or without B beside it

        var tokensByOpportunity = both.Captured[RunRecorders.Names.Outputs]
            .Where(l => l.Line.Contains("EntryIntent:", StringComparison.Ordinal))
            .Select(l => System.Text.RegularExpressions.Regex.Match(l.Line, "\"OpportunityId\":\"([^\"]+)\".*?\"Token\":\"([^\"]+)\""))
            .GroupBy(m => m.Groups[1].Value, m => m.Groups[2].Value)
            .ToList();
        Assert.NotEmpty(tokensByOpportunity);
        Assert.All(tokensByOpportunity, g => Assert.Single(g.Distinct()));                                      // every attempt of an opportunity names the same contract
        Assert.Contains(both.Captured[RunRecorders.Names.Outputs], l => l.Line.Contains("independent-virtual-book:A", StringComparison.Ordinal));
        Assert.Contains(both.Captured[RunRecorders.Names.Outputs], l => l.Line.Contains("independent-virtual-book:B", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ quiet market close

    [Fact]
    public void QuietMarketClose_ExecutesTheLiquidationAndFinalCloseDeadlines_AndNeverInventsAFill()
    {
        var rows = SyntheticSession.Rows(new SyntheticSession.Options { Windows = new[] { (SyntheticSession.Ist(14, 40), SyntheticSession.Ist(14, 55)) } });
        var freeze = SyntheticSession.Freeze(rows, anchor: false);
        TradingConfig OneVariant(TradingConfig c) => c with
        {
            Variants = new Dictionary<string, VariantSpec> { ["Q"] = new() { VariantId = "Q", StrategyId = "ALWAYS", EntryCondition = new ImmediateEntrySpec() } },
        };

        var report = RunDiff.Compare(
            Runs.Hist(rows, freeze, tweak: OneVariant, strategies: Runs.StrategiesWithAlways()),
            Runs.Live(rows, freeze, new[] { new PollStep(TimeSpan.FromSeconds(1), 100_000), new PollStep(TimeSpan.FromSeconds(10), 100_000) }, tweak: OneVariant, strategies: Runs.StrategiesWithAlways()));

        Runs.AssertEquivalent(report);
        var captured = Runs.Hist(rows, freeze, tweak: OneVariant, strategies: Runs.StrategiesWithAlways())(new RecorderOptions { CaptureAll = true });
        var outputs = captured.Captured[RunRecorders.Names.Outputs].Select(l => l.Line).ToList();
        Assert.Contains(outputs, l => l.Contains("\"Side\":\"Buy\"", StringComparison.Ordinal) && l.Contains("FillRecord", StringComparison.Ordinal));            // a position was opened before the data stopped
        Assert.DoesNotContain(outputs, l => l.Contains("\"Side\":\"Sell\"", StringComparison.Ordinal) && l.Contains("FillRecord", StringComparison.Ordinal));      // no exit quote after 14:55: nothing is manufactured
        Assert.Contains(outputs, l => l.Contains("UnresolvedAtClose:", StringComparison.Ordinal));
        Assert.Contains(outputs, l => l.Contains("UnresolvedAtSessionEnd:", StringComparison.Ordinal));
        Assert.Equal(1, captured.UnresolvedPositions);
        Assert.Equal(1, report.B.UnresolvedPositions);
        Assert.Contains(outputs, l => l.StartsWith("2026-09-22T09:40:00.0000000Z", StringComparison.Ordinal) && l.Contains("UnresolvedAtClose", StringComparison.Ordinal));   // 15:10:00 IST
    }

    // ------------------------------------------------------------------ conflicts, duplicates, delay

    [Fact]
    public void AConflictingDuplicate_IsQuarantinedIdentically_InBothPaths_AndChangesNothing()
    {
        var rows = SyntheticSession.Rows(new SyntheticSession.Options { Windows = new[] { (SyntheticSession.Ist(9, 15), SyntheticSession.Ist(9, 45)) } });
        var freeze = SyntheticSession.Freeze(rows);
        var victim = rows[rows.Count / 2];
        var withConflict = rows.Take(rows.Count / 2 + 1).Append(victim with { Bid = victim.Bid + 5m }).Concat(rows.Skip(rows.Count / 2 + 1)).ToList();

        var report = RunDiff.Compare(
            Runs.Hist(withConflict, freeze),
            rec => LivePaperAdapter.Run(SyntheticSession.NewRun(freeze, rec, "LivePaper"), PollPlan.FromChunks(withConflict, new[] { 40, 3, 500 })).Run);

        Runs.AssertEquivalent(report);
        Assert.Equal(1L, report.A.Translator.QuarantinedByReason.GetValueOrDefault("ConflictingDuplicate"));
        Assert.Equal(1L, report.B.Translator.QuarantinedByReason.GetValueOrDefault("ConflictingDuplicate"));
        var clean = Runs.Hist(rows, freeze)(new RecorderOptions());
        Assert.Equal(clean.Channels[RunRecorders.Names.Quotes].Hash, report.A.Channels[RunRecorders.Names.Quotes].Hash);   // the refused row left no trace in the quotes
        Assert.Equal(clean.Channels[RunRecorders.Names.Outputs].Hash, report.A.Channels[RunRecorders.Names.Outputs].Hash);
        Assert.Equal(1, report.A.Channels[RunRecorders.Names.RowQuarantine].Count);
    }

    [Fact]
    public void AnAvailabilityGroupSplitAcrossTwoPollBatches_IsPublishedOnceAndWhole_BecauseACutShortBatchClaimsNoProgress()
    {
        var rows = SyntheticSession.Rows(new SyntheticSession.Options { Windows = new[] { (SyntheticSession.Ist(9, 15), SyntheticSession.Ist(9, 35)) }, ClampFraction = 0 });
        var freeze = SyntheticSession.Freeze(rows);
        var instant = rows.GroupBy(r => r.ReceivedAt).Where(g => g.Count() >= 3 && g.Key > SyntheticSession.Ist(9, 20)).OrderBy(g => g.Key).First();
        var firstId = instant.Min(r => r.Id);
        var cut = rows.Select((r, i) => (r, i)).First(x => x.r.Id == firstId).i + 1;                       // the batch ends inside the group, after its first row
        var batch1 = rows.Take(cut).ToList();
        var batch2 = rows.Skip(cut).ToList();

        IPollSource Source() => new ScriptedPollSource(new[]
        {
            new PollResult(instant.Key.AddSeconds(10), PollStatus.Ok, batch1, true, false),                    // PolledAt is far ahead, but the batch was cut short: no progress may be claimed
            new PollResult(rows[^1].ReceivedAt.AddSeconds(1), PollStatus.Ok, batch2, false, true),
        });

        var report = RunDiff.Compare(Runs.Hist(rows, freeze), rec => LivePaperAdapter.Run(SyntheticSession.NewRun(freeze, rec, "LivePaper"), Source()).Run, new DiffOptions { FullCapture = true });

        Runs.AssertEquivalent(report);
        Assert.Equal(0, report.B.Translator.Quarantined);
        Assert.True(instant.Count() >= 3);
        Assert.True(report.B.Translator.Balanced);
    }

    [Fact]
    public void AHeartbeatThatClaimsTooMuchProgress_TurnsALaterRowIntoALateRow_AndTheRunDiffNamesTheFirstDivergence()
    {
        var rows = SyntheticSession.Rows(new SyntheticSession.Options { Windows = new[] { (SyntheticSession.Ist(9, 15), SyntheticSession.Ist(9, 35)) }, ClampFraction = 0 });
        var freeze = SyntheticSession.Freeze(rows);
        var m = rows.Count / 2;
        var prefix = rows.Take(m).ToList();
        var t0 = prefix.Max(r => r.ReceivedAt);
        var skewed = new SourceTick("CE23900", prefix[^1].Id + 1, t0.AddSeconds(-1), t0.AddSeconds(-1), 131m, 130m, 131m, 2_000, 2_000, 999_999, 5_001);   // received 1 s BEFORE the newest prefix row, id above it
        var suffix = rows.Skip(m).Select(r => r with { Id = r.Id + 1 }).ToList();
        var all = prefix.Append(skewed).Concat(suffix).ToList();

        IPollSource Source() => new ScriptedPollSource(new[]
        {
            new PollResult(t0.AddSeconds(1.5), PollStatus.Ok, prefix, false, false),                                 // claims: nothing is missing before t0 + 1.5 s - 2 s lag = t0 - 0.5 s
            new PollResult(suffix[^1].ReceivedAt.AddSeconds(1), PollStatus.Ok, new[] { skewed }.Concat(suffix).ToList(), false, true),
        });

        var report = RunDiff.Compare(
            Runs.Hist(all, freeze),
            rec => LivePaperAdapter.Run(SyntheticSession.NewRun(freeze, rec, "LivePaper"), Source()).Run,
            new DiffOptions { CheckpointEvery = 200 });

        Assert.False(report.Equivalent);
        Assert.Equal(1L, report.B.Translator.QuarantinedByReason.GetValueOrDefault("LateAvailability"));
        Assert.Equal(0L, report.A.Translator.Quarantined);
        var first = report.First!;
        Assert.Equal(RunRecorders.Names.Quotes, first.Channel);
        Assert.Equal($"OptionQuote:{skewed.Id}", first.KeyA);                                                         // the first line that exists only in the historical run
        Assert.NotEqual(first.KeyA, first.KeyB);
        Assert.Equal(Canonical.Time(skewed.ReceivedAt), first.TimeA);
        Assert.Contains("131", first.LineA, StringComparison.Ordinal);
        var rendered = RunDiff.Render(report);
        Assert.Contains("DIVERGENT", rendered, StringComparison.Ordinal);
        Assert.Contains("Excluded from the comparison", rendered, StringComparison.Ordinal);

        var full = RunDiff.Compare(Runs.Hist(all, freeze), rec => LivePaperAdapter.Run(SyntheticSession.NewRun(freeze, rec, "LivePaper"), Source()).Run, new DiffOptions { FullCapture = true });
        Assert.Equal(first.KeyA, full.First!.KeyA);                                                                   // the two-pass search finds exactly what a full capture finds
        Assert.Equal(first.LineIndex, full.First.LineIndex);
    }
}

/// <summary>Builds scripted polls from chunk sizes (cycled): each poll is stamped with the newest ReceivedAt it carries and says HasMore when the next row already existed.</summary>
internal static class PollPlan
{
    public static IPollSource FromChunks(IReadOnlyList<SourceTick> rows, IReadOnlyList<int> chunkSizes)
    {
        var polls = new List<PollResult>();
        var i = 0;
        var k = 0;
        while (i < rows.Count)
        {
            var take = Math.Min(chunkSizes[k++ % chunkSizes.Count], rows.Count - i);
            var chunk = rows.Skip(i).Take(take).ToList();
            i += take;
            var polledAt = chunk.Max(r => r.ReceivedAt);
            var hasMore = i < rows.Count && rows[i].ReceivedAt <= polledAt;
            polls.Add(new PollResult(polledAt, PollStatus.Ok, chunk, hasMore, i >= rows.Count));
        }

        return new ScriptedPollSource(polls);
    }
}
