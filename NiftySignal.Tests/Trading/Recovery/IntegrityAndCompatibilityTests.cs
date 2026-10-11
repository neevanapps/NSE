using NiftySignal.Tests.Trading.Adapters;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Recovery;

namespace NiftySignal.Tests.Trading.Recovery;

/// <summary>Source identity, ledger corruption and version-compatibility policy: a resume must refuse, not guess (contract sections A.18.6 and A.18.7).</summary>
public class IntegrityAndCompatibilityTests
{
    private static readonly IReadOnlyList<SourceTick> Rows = RecoveryKit.ShortRows();
    private static readonly SessionFreeze Freeze = SyntheticSession.Freeze(Rows);
    private static readonly DurableRunSpec Spec = RecoveryKit.Spec(Freeze);

    /// <summary>A store holding a run that died after a late commit, so that most of the session is durable and gets replayed.</summary>
    private static InMemoryRunStore DiedLate(DurableRunSpec? spec = null, IReadOnlyList<SourceTick>? rows = null)
    {
        var store = new InMemoryRunStore();
        RecoveryKit.RunUntilDeath(store, spec ?? Spec, rows ?? Rows, new CrashAt(CrashPoint.AfterCommit, 1, (s, _) => s == 70));
        return store;
    }

    private static RecoveryHaltException Halts(InMemoryRunStore store, IReadOnlyList<SourceTick> rows, DurableRunSpec? spec = null)
        => Assert.Throws<RecoveryHaltException>(() => RecoveryKit.ResumeAndFinish(store, spec ?? Spec, rows));

    private static List<SourceTick> With(Func<List<SourceTick>, List<SourceTick>> change) => change(Rows.ToList());

    private static int OptionRow(int atLeast) => Enumerable.Range(atLeast, 5_000).First(i => Rows[i].Token != SyntheticSession.Future);

    // ------------------------------------------------------------------ source identity

    [Fact]
    public void AChangedSourceRecord_IsDetected_AtTheStepThatReleasedIt()
    {
        var i = OptionRow(2_000);
        var rows = With(r => { r[i] = r[i] with { Ask = r[i].Ask + 0.05m }; return r; });

        var halt = Halts(DiedLate(), rows);

        Assert.Equal(HaltKind.SourceMismatch, halt.Kind);
        Assert.Contains(halt.Details, d => d.Contains("source input differs", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingSourceRecord_IsDetected()
    {
        var i = OptionRow(2_000);
        var rows = With(r => { r.RemoveAt(i); return r; });

        var halt = Halts(DiedLate(), rows);

        Assert.Equal(HaltKind.SourceMismatch, halt.Kind);
    }

    [Fact]
    public void AReorderedSourceOrder_IsDetected()
    {
        var i = OptionRow(2_000);
        var j = Enumerable.Range(i + 1, 50).First(k => Rows[k].Token != SyntheticSession.Future);
        var rows = With(r =>
        {
            var a = r[i];
            var b = r[j];
            r[i] = a with { Id = b.Id };
            r[j] = b with { Id = a.Id };
            return r;
        });

        var halt = Halts(DiedLate(), rows);

        Assert.True(halt.Kind is HaltKind.SourceMismatch or HaltKind.ReplayDivergence, halt.Kind.ToString());
    }

    [Fact]
    public void ASourceThatEndsBeforeTheDurableCursor_HaltsAsMissingInput_ItIsNeverTreatedAsProgress()
    {
        var store = DiedLate();

        var halt = Halts(store, Rows.Take(3_000).ToList());

        Assert.Equal(HaltKind.MissingInput, halt.Kind);
        Assert.NotEmpty(halt.Details);
    }

    [Fact]
    public void ADifferentSourceManifest_IsACompatibilityHalt_BeforeAnyReplay()
    {
        var store = DiedLate();
        var other = RecoveryKit.Spec(Freeze, manifest: "synthetic;seed=8");

        var halt = Halts(store, Rows, other);

        Assert.Equal(HaltKind.Compatibility, halt.Kind);
        Assert.Contains(halt.Details, d => d.Contains("SourceManifest", StringComparison.Ordinal));
    }

    [Fact]
    public void ARowFieldThatNoChannelOrOutputRecords_IsStillCaughtByTheStateDigest_WhenEveryGroupIsAStepWithAStateDigest()
    {
        // OpenInterest of an option row is carried in the translator's last-row fingerprint but is part of no recorded channel and of no output. With a
        // commit and a state digest at EVERY group (the finest setting) the changed row is still the token's last row when the checkpoint is taken.
        var finest = RecoveryKit.Spec(Freeze, durable: new DurableOptions { CheckpointEverySteps = 1, MaxEngineTimeBetweenCommits = TimeSpan.FromMilliseconds(1) });
        var i = OptionRow(2_000);
        var rows = With(r => { r[i] = r[i] with { OpenInterest = (r[i].OpenInterest ?? 0) + 17 }; return r; });
        var store = new InMemoryRunStore();
        RecoveryKit.RunUntilDeath(store, finest, Rows, new CrashAt(CrashPoint.AfterCommit, 1, (s, _) => s == 8_000));

        var halt = Halts(store, rows, finest);

        Assert.Equal(HaltKind.ReplayDivergence, halt.Kind);
        Assert.Contains(halt.Details, d => d.Contains("canonical state digest differs", StringComparison.Ordinal));
    }
    // ------------------------------------------------------------------ ledger corruption

    public static IEnumerable<object[]> Corruptions()
    {
        yield return new object[] { "step-hash" };
        yield return new object[] { "step-removed-from-middle" };
        yield return new object[] { "steps-swapped" };
        yield return new object[] { "output-payload-changed" };
        yield return new object[] { "output-removed" };
        yield return new object[] { "output-not-canonical" };
        yield return new object[] { "projection-changed" };
        yield return new object[] { "projection-removed" };
        yield return new object[] { "control-hash" };
        yield return new object[] { "checkpoint-for-missing-step" };
        yield return new object[] { "header-hash" };
        yield return new object[] { "completed-without-final" };
    }

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void ACorruptOrIncompleteLedger_HaltsAsCorruptLedger_AndNothingIsReplayed(string corruption)
    {
        var store = DiedLate();
        var run = store.Unsafe("run-1");
        var firstOutputStep = run.Outputs[0].StepSeq;
        switch (corruption)
        {
            case "step-hash": run.Steps[40] = run.Steps[40] with { ContentHash = "00" }; break;
            case "step-removed-from-middle": run.Steps.RemoveAt(40); break;
            case "steps-swapped": (run.Steps[40], run.Steps[41]) = (run.Steps[41], run.Steps[40]); break;
            case "output-payload-changed": run.Outputs[3] = run.Outputs[3] with { Payload = run.Outputs[3].Payload.Replace("\"", "\"") + " " }; break;
            case "output-removed": run.Outputs.RemoveAt(5); break;
            case "output-not-canonical": run.Outputs[3] = Retied(run.Outputs[3], run.Outputs[3].Payload.Replace("{\"type\"", "{ \"type\"")); break;
            case "projection-changed": run.Projections[0] = run.Projections[0] with { Payload = "{}" }; break;
            case "projection-removed": run.Projections.RemoveAt(0); break;
            case "control-hash": run.Controls.Add(ControlCodecKill() with { ContentHash = "00" }); break;
            case "checkpoint-for-missing-step": run.Checkpoints.Add(new CheckpointRecord(999_999, DateTimeOffset.UnixEpoch, "00", null)); break;
            case "header-hash": run.Header = run.Header! with { ContentHash = "00" }; break;
            case "completed-without-final": run.Completed = true; break;
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        var halt = Halts(store, Rows);

        Assert.Equal(HaltKind.CorruptLedger, halt.Kind);
        Assert.True(firstOutputStep >= 0);
        Assert.DoesNotContain(store.Load("run-1").RecoveryLog, l => l.Outcome == "Opened" && l.Attempt > 1);
    }

    private static OutputRecord Retied(OutputRecord o, string payload) => o with { Payload = payload, ContentHash = NiftySignal.Trading.Recovery.Codec.OutputCodec.ContentHash(payload) };

    private static ControlRecord ControlCodecKill()
        => NiftySignal.Trading.Recovery.Codec.ControlCodec.ToRecord(new OperatorCommandEvent(SyntheticSession.Ist(9, 40), 5, OperatorCommand.Kill, "*", "x", "x"));

    [Fact]
    public void ACheckpointDigestThatNoLongerMatchesTheReplay_HaltsAsReplayDivergence_NamingTheStep()
    {
        var store = DiedLate();
        var run = store.Unsafe("run-1");
        var index = run.Checkpoints.FindIndex(c => c.StepSeq >= 20);
        run.Checkpoints[index] = run.Checkpoints[index] with { StateDigest = "00" + run.Checkpoints[index].StateDigest[2..] };

        var halt = Halts(store, Rows);

        Assert.Equal(HaltKind.ReplayDivergence, halt.Kind);
        Assert.Contains(halt.Details, d => d.Contains($"step {run.Checkpoints[index].StepSeq}:", StringComparison.Ordinal) && d.Contains("state digest", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOutputChangedConsistentlyWithItsHash_IsStillCaughtBecauseReplayDoesNotReproduceIt()
    {
        var store = DiedLate();
        var run = store.Unsafe("run-1");
        var o = run.Outputs[10];
        var tampered = Retied(o, o.Payload.Replace("\"", "\"") + string.Empty);
        var altered = o.Payload.Contains("\"Variant\":\"P6/A\"", StringComparison.Ordinal) ? o.Payload.Replace("\"Variant\":\"P6/A\"", "\"Variant\":\"P4/B\"") : o.Payload.Replace("\"Variant\":\"P4/B\"", "\"Variant\":\"P6/A\"");
        Assert.NotEqual(o.Payload, altered);
        run.Outputs[10] = Retied(o, altered);
        run.Projections.Clear();
        run.Projections.AddRange(ProjectionBuilder.Build(run.Outputs));

        var halt = Halts(store, Rows);

        Assert.Equal(HaltKind.ReplayDivergence, halt.Kind);
        Assert.NotNull(tampered);
    }

    // ------------------------------------------------------------------ compatibility policy

    private sealed class VersionedStrategy(string version) : IBaseStrategy
    {
        public string StrategyId => "P6";

        public string Version => version;

        public StrategyValidity OnBar(BarCompletedEvent bar) => new(false, NiftySignal.Trading.Core.Instruments.OptionSide.Call, "never");
    }

    public static IEnumerable<object[]> IncompatibleChanges()
    {
        yield return new object[] { "config", "ConfigHash" };
        yield return new object[] { "freeze", "FreezeHash" };
        yield return new object[] { "strategy-version", "StrategiesIdentity" };
        yield return new object[] { "translator-lag", "TranslatorOptionsHash" };
        yield return new object[] { "retention", "RetentionHash" };
        yield return new object[] { "engine-label", "EngineCompatibilityId" };
        yield return new object[] { "codec-version", "CodecVersion" };
        yield return new object[] { "state-digest-version", "StateDigestVersion" };
        yield return new object[] { "readiness-spec", "ReadinessSpec" };
    }

    [Theory]
    [MemberData(nameof(IncompatibleChanges))]
    public void AnyChangeToWhatDefinesBehaviour_RefusesTheResume_NamingTheDifferingField(string change, string field)
    {
        var store = DiedLate();
        DurableRunSpec spec = Spec;
        switch (change)
        {
            case "config": spec = RecoveryKit.Spec(Freeze, tweak: c => c with { ConfigVersion = c.ConfigVersion + "-changed" }); break;
            case "freeze": spec = RecoveryKit.Spec(Freeze with { StrongThreshold = 0.75 }); break;
            case "strategy-version": spec = RecoveryKit.Spec(Freeze, strategies: new Dictionary<string, IBaseStrategy> { ["P6"] = new VersionedStrategy("v2"), ["P4"] = SyntheticSession.Strategies()["P4"] }); break;
            case "translator-lag": spec = RecoveryKit.Spec(Freeze, translator: SyntheticSession.Translator(Freeze, TimeSpan.FromSeconds(3))); break;
            case "retention": spec = Spec with { Settings = Spec.Settings with { Retention = NiftySignal.Trading.Core.Trading.RetentionPolicy.Unbounded } }; break;
            case "readiness-spec": spec = RecoveryKit.Spec(Freeze with { ModelVersion = "another-model" }); break;
            case "engine-label": Rewrite(store, c => c with { EngineCompatibilityId = "trading-engine/other" }); break;
            case "codec-version": Rewrite(store, c => c with { CodecVersion = "trading-codec/0" }); break;
            case "state-digest-version": Rewrite(store, c => c with { StateDigestVersion = "trading-state/0" }); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }

        var halt = Halts(store, Rows, spec);

        Assert.Equal(HaltKind.Compatibility, halt.Kind);
        Assert.Contains(halt.Details, d => d.StartsWith(field, StringComparison.Ordinal));
        Assert.Equal(DiedLate().Load("run-1").Steps.Count, store.Load("run-1").Steps.Count);
    }

    private static void Rewrite(InMemoryRunStore store, Func<CompatibilityIdentity, CompatibilityIdentity> change)
    {
        var run = store.Unsafe("run-1");
        run.Header = RunHeader.Create(run.Header! with { Compatibility = change(run.Header.Compatibility), ContentHash = string.Empty });
    }

    [Fact]
    public void BinaryProvenance_IsRecordedButNeverComparedOnResume()
    {
        var first = Spec with { Provenance = new BinaryProvenance("aaaa111", true, "10.0.401", new Dictionary<string, string> { ["NiftySignal.Trading.Core.dll"] = "hash-1" }) };
        var store = new InMemoryRunStore();
        RecoveryKit.RunUntilDeath(store, first, Rows, new CrashAt(CrashPoint.AfterCommit, 1, (s, _) => s == 30));
        var second = Spec with { Provenance = new BinaryProvenance("bbbb222", false, "10.0.402", new Dictionary<string, string> { ["NiftySignal.Trading.Core.dll"] = "hash-2" }) };

        var resumed = RecoveryKit.ResumeAndFinish(store, second, Rows);

        Assert.True(resumed.Snapshot.Completed);
        Assert.Equal("aaaa111", resumed.Snapshot.Header!.Provenance.GitCommit);
    }

    [Fact]
    public void TheCompatibilityIdentity_IsDeterministic_AndSeparateFromTheBinary()
    {
        var a = RunIdentity.BuildCompatibility(Spec);
        var b = RunIdentity.BuildCompatibility(RecoveryKit.Spec(Freeze));

        Assert.Equal(a.CanonicalText(), b.CanonicalText());
        Assert.Equal(NiftySignal.Trading.Recovery.CompatibilityIdentity.CurrentEngine, a.EngineCompatibilityId);
        Assert.Empty(a.DifferencesFrom(b));
        Assert.DoesNotContain("dll", a.CanonicalText(), StringComparison.OrdinalIgnoreCase);
    }
}
