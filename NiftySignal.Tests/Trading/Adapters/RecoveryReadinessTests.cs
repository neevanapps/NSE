using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Integrity;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Tests.Trading.Adapters;

/// <summary>
/// CP5 correction of the CP4 recovery rule, exercised with REAL translator bars: recovery needs explicit readiness evidence (a futures quote and a
/// ready bar that started after the recovery instant), never "any option quote".
/// </summary>
public class RecoveryReadinessTests
{
    private static (TradingRun Run, IReadOnlyList<IntegrityTransition> Transitions) Run(SyntheticSession.Options options, params EngineEvent[] controls)
    {
        var rows = SyntheticSession.Rows(options);
        var freeze = SyntheticSession.Freeze(rows);
        var run = new TradingRun(freeze, SyntheticSession.Translator(freeze), SyntheticSession.Config(), SyntheticSession.Strategies(), new RunSettings(), new RecorderOptions(), controls);
        HistoricalAdapter.Run(run, rows);
        return (run, run.Engine.Outputs.OfType<IntegrityTransition>().ToList());
    }

    private static FeedHealthEvent Degraded(int h, int m, int s, long seq) => new(SyntheticSession.Ist(h, m, s), seq, FeedHealthState.Degraded, FeedFaultReason.DataGapOpen, "scripted");

    private static FeedHealthEvent Healthy(int h, int m, int s, long seq) => new(SyntheticSession.Ist(h, m, s), seq, FeedHealthState.Healthy, FeedFaultReason.None, "scripted");

    private static SyntheticSession.Options FirstWindow(params (DateTimeOffset, DateTimeOffset)[] futureSilences) => new()
    {
        Windows = new[] { (SyntheticSession.Ist(9, 15), SyntheticSession.Ist(10, 5)) },
        FutureSilences = futureSilences,
    };

    [Fact]
    public void StaleFutures_AnOptionOnlyStream_NeverCompletesTheRecovery()
    {
        // the futures stream goes silent from 09:59 to the end of the window; the recovery is reported at 10:01 while option quotes keep flowing
        var (run, transitions) = Run(FirstWindow((SyntheticSession.Ist(9, 59), SyntheticSession.Ist(10, 5))), Degraded(9, 59, 30, 1), Healthy(10, 1, 0, 2));

        Assert.Equal(new[] { IntegrityState.Degraded, IntegrityState.Recovering }, transitions.Select(t => t.To).ToArray());
        Assert.Equal(IntegrityState.Recovering, run.Engine.Integrity.State);
        var optionQuotesWhileRecovering = run.Recorders[RunRecorders.Names.Quotes].Count;                    // all quotes of the run, for scale
        Assert.True(optionQuotesWhileRecovering > 1_000);
        Assert.DoesNotContain(run.Engine.Outputs.OfType<EntryIntent>(), i => i.At >= SyntheticSession.Ist(9, 59, 30));        // no entry while the evidence is missing
    }

    [Fact]
    public void FuturesResumeButTheFirstBarSpansTheOutage_RecoveryWaitsForABarThatStartedAfterTheRecovery()
    {
        var (run, transitions) = Run(FirstWindow((SyntheticSession.Ist(10, 0), SyntheticSession.Ist(10, 3))), Degraded(9, 59, 50, 1), Healthy(10, 1, 0, 2));

        var healthy = Assert.Single(transitions, t => t.To == IntegrityState.Healthy);
        Assert.True(healthy.At > SyntheticSession.Ist(10, 3), $"recovered at {healthy.At:O}, before the futures stream came back");
        var evidenceBar = int.Parse(System.Text.RegularExpressions.Regex.Match(healthy.Detail, "readyBar=b(\\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var bars = run.Translator.FutureBars;
        var evidence = bars.Single(b => b.BarSeq == evidenceBar);
        Assert.True(evidence.StartAvailableAtUtc >= SyntheticSession.Ist(10, 1), "the evidence bar must have started at or after the recovery instant");
        Assert.True(evidenceBar > 1);
        // the bar that was open during the outage (it started before 10:01) completed earlier and was not accepted as evidence
        var spanning = bars.First(b => b.StartAvailableAtUtc < SyntheticSession.Ist(10, 1) && b.EndAvailableAtUtc > SyntheticSession.Ist(10, 3));
        Assert.True(spanning.BarSeq < evidenceBar);
        Assert.True(spanning.EndAvailableAtUtc < healthy.At);
    }

    [Fact]
    public void IncompleteWarmUp_TheRecoveryWaitsForTheFirstReadyBar_AfterTheSignalStart()
    {
        var (run, transitions) = Run(FirstWindow(), Degraded(9, 16, 0, 1), Healthy(9, 17, 0, 2));

        var healthy = Assert.Single(transitions, t => t.To == IntegrityState.Healthy);
        Assert.True(healthy.At >= SyntheticSession.Ist(9, 30), $"recovered at {healthy.At:O}, before the signal start");
        var bars = run.Translator.FutureBars;
        var notReadyAfterRecovery = bars.Count(b => b.StartAvailableAtUtc >= SyntheticSession.Ist(9, 17) && b.EndAvailableAtUtc < healthy.At);
        Assert.True(notReadyAfterRecovery >= 10, "many complete bars started after the recovery instant but were not ready (warm-up / before the signal start)");
        Assert.Contains("readyBar=b", healthy.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulRecovery_ReleasesEntriesAgain_OnlyAfterTheEvidence()
    {
        var (run, transitions) = Run(FirstWindow(), Degraded(9, 40, 10, 1), Healthy(9, 41, 0, 2));

        var healthy = Assert.Single(transitions, t => t.To == IntegrityState.Healthy);
        Assert.InRange((healthy.At - SyntheticSession.Ist(9, 41)).TotalSeconds, 0, 130);                         // within about two bars of normal flow
        Assert.Contains("futureQuote=yes", healthy.Detail, StringComparison.Ordinal);
        var intents = run.Engine.Outputs.OfType<EntryIntent>().ToList();
        Assert.DoesNotContain(intents, i => i.At > SyntheticSession.Ist(9, 40, 10) && i.At < healthy.At);       // blocked from the fault until the evidence is complete
        Assert.Contains(intents, i => i.At > healthy.At);                                                        // and entries resume afterwards
        Assert.Contains(run.Engine.Outputs.OfType<AuditRecord>(), r => r.Outcome == "Blocked:IntegrityDegraded" || r.Outcome == "Blocked:IntegrityRecovering");
    }

    [Fact]
    public void BothAdaptersReachTheSameRecovery_WithTheSameHealthEvents()
    {
        var rows = SyntheticSession.Rows(FirstWindow((SyntheticSession.Ist(10, 0), SyntheticSession.Ist(10, 3))));
        var freeze = SyntheticSession.Freeze(rows);
        var controls = new EngineEvent[] { Degraded(9, 59, 50, 1), Healthy(10, 1, 0, 2) };

        var report = RunDiff.Compare(
            Runs.Hist(rows, freeze, controls: controls),
            Runs.Live(rows, freeze, new[] { new PollStep(TimeSpan.FromMilliseconds(500), 40), new PollStep(TimeSpan.FromSeconds(2), 100_000) }, controls: controls));

        Runs.AssertEquivalent(report);
    }
}
