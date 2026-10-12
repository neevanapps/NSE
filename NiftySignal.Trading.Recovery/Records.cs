using System.Globalization;
using System.Text;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Trading.Recovery;

/// <summary>The kinds of non-derivable input that are journaled before they are applied (write-ahead).</summary>
public enum ControlKind
{
    /// <summary>An operator command (kill / reset); key = the command's own sequence number.</summary>
    Operator = 0,

    /// <summary>A feed-health transition; key = the health sequence number.</summary>
    Health = 1,

    /// <summary>A declaration "the source has delivered everything before this time" that releases buffered groups; key = the time in UTC ticks.</summary>
    SourceProgress = 2,

    /// <summary>An effective clock advance (it fires at least one deadline); key = the watermark in UTC ticks.</summary>
    Advance = 3,
}

/// <summary>
/// One journaled input. <see cref="Id"/> is its deterministic identity (kind + the key the command itself carries), so a duplicate delivery is
/// recognised, never applied twice, and a different payload under the same identity is a conflict. <see cref="AfterOfferedRows"/> is the number of
/// source rows that had been offered when a progress / advance was applied (their position in the unit sequence); operator and health events are
/// positioned by their availability time and carry 0.
/// </summary>
public sealed record ControlRecord(ControlKind Kind, long Key, DateTimeOffset AvailableAt, long AfterOfferedRows, string Payload, string ContentHash)
{
    public string Id => IdOf(Kind, Key);

    public static string IdOf(ControlKind kind, long key) => string.Create(CultureInfo.InvariantCulture, $"{kind}:{key}");

    public static ControlRecord Create(ControlKind kind, long key, DateTimeOffset availableAt, long afterOfferedRows, string payload)
        => new(kind, key, availableAt, afterOfferedRows, payload, OutputCodec.ContentHash(CanonicalText(kind, key, availableAt, afterOfferedRows, payload)));

    public bool HashIsValid => ContentHash == OutputCodec.ContentHash(CanonicalText(Kind, Key, AvailableAt, AfterOfferedRows, Payload));

    private static string CanonicalText(ControlKind kind, long key, DateTimeOffset at, long after, string payload)
        => string.Create(CultureInfo.InvariantCulture, $"{kind}|{key}|{at.UtcTicks}|{after}|{payload}");
}

/// <summary>One engine output in the durable ledger: global sequence, the step that committed it, the canonical payload and its hash.</summary>
public sealed record OutputRecord(long Seq, long StepSeq, string Type, string StrategyId, string VariantId, DateTimeOffset At, string Payload, string ContentHash)
{
    public bool HashIsValid => ContentHash == OutputCodec.ContentHash(Payload);
}

/// <summary>
/// One canonical input of a live run, journaled in the same commit as the step that processed it (contract section B.5). Inputs are what the live engine was
/// GIVEN (a quote, a completed Adaptive bar, a feed-health or operator event, a clock advance, the finish), never what it decided: replaying them in order through the
/// deterministic core reproduces every output. <see cref="GroupOrdinal"/> numbers the units (one availability group, one advance or the finish); records with the same
/// ordinal were delivered together. <see cref="Kind"/> is an <c>EventKind</c> name, <c>Advance</c> or <c>Finish</c>.
/// </summary>
public sealed record InputRecord(long Seq, long StepSeq, long GroupOrdinal, string Kind, long SourceId, string Token, DateTimeOffset At, string Payload, string ContentHash)
{
    public const string AdvanceKind = "Advance";
    public const string FinishKind = "Finish";

    public bool HashIsValid => ContentHash == HashOf(GroupOrdinal, Kind, SourceId, Token, At, Payload);

    public static InputRecord Create(long seq, long stepSeq, long groupOrdinal, string kind, long sourceId, string token, DateTimeOffset at, string payload)
        => new(seq, stepSeq, groupOrdinal, kind, sourceId, token, at, payload, HashOf(groupOrdinal, kind, sourceId, token, at, payload));

    public static string HashOf(long groupOrdinal, string kind, long sourceId, string token, DateTimeOffset at, string payload)
        => OutputCodec.ContentHash(string.Create(CultureInfo.InvariantCulture, $"{groupOrdinal}|{kind}|{sourceId}|{token}|{at.UtcTicks}|{payload}"));
}

/// <summary>A cumulative recorder digest at a step boundary (one of the nine CP5 channels).</summary>
public sealed record ChannelDigestRecord(string Channel, long Count, string Hash);

/// <summary>
/// A committed step: a batch of consecutive units of work ended by a complete unit (a delivered availability group, a control-only delivery, an
/// effective advance or the finish). The cursor of the run is the highest committed step, and it moves only inside the commit that makes the step's
/// outputs durable. <see cref="Digests"/> are the cumulative channel digests at the step end; the <c>quotes</c> digest doubles as the rolling
/// fingerprint of the effective source input (every released quote with its identifiers, values, exchange and received times, in order).
/// </summary>
public sealed record StepRecord(
    long StepSeq,
    UnitKind Kind,
    DateTimeOffset EngineTime,
    long OfferedRows,
    int Units,
    long? FirstGroupOrdinal,
    long? LastGroupOrdinal,
    DateTimeOffset? LastGroupAt,
    long? LastSourceId,
    long OutputFrom,
    long OutputToExclusive,
    IReadOnlyList<string> AppliedControls,
    IReadOnlyList<ChannelDigestRecord> Digests,
    string ContentHash)
{
    public ChannelDigestRecord Digest(string channel) => Digests.Single(d => d.Channel == channel);

    public bool HashIsValid => ContentHash == ComputeHash(this);

    public static string ComputeHash(StepRecord s)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{s.StepSeq}|{s.Kind}|{s.EngineTime.UtcTicks}|{s.OfferedRows}|{s.Units}|{s.FirstGroupOrdinal}|{s.LastGroupOrdinal}|{s.LastGroupAt?.UtcTicks}|{s.LastSourceId}|{s.OutputFrom}|{s.OutputToExclusive}|");
        sb.Append(string.Join(",", s.AppliedControls)).Append('|');
        foreach (var d in s.Digests.OrderBy(x => x.Channel, StringComparer.Ordinal))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{d.Channel}:{d.Count}:{d.Hash};");
        }

        return OutputCodec.ContentHash(sb.ToString());
    }

    public static StepRecord Create(StepRecord withoutHash) => withoutHash with { ContentHash = ComputeHash(withoutHash) };
}

/// <summary>A verification checkpoint: the canonical state digest at a step end (the text only when the run is configured to keep it, for diagnostics).</summary>
public sealed record CheckpointRecord(long StepSeq, DateTimeOffset EngineTime, string StateDigest, string? StateText);

/// <summary>A row of an append-only projection table (fills, ledger, order events, position events) derived deterministically from the outputs.</summary>
public sealed record ProjectionRow(string Table, string Key, string Payload, string ContentHash)
{
    public bool HashIsValid => ContentHash == OutputCodec.ContentHash(Payload);

    public static ProjectionRow Create(string table, string key, string payload) => new(table, key, payload, OutputCodec.ContentHash(payload));
}

/// <summary>Written by the final commit: the audit and summary lines (the two channels that are only complete at the end).</summary>
public sealed record FinalRecord(IReadOnlyList<string> AuditLines, IReadOnlyList<string> SummaryLines, string ContentHash)
{
    public static FinalRecord Create(IReadOnlyList<string> audit, IReadOnlyList<string> summaries)
        => new(audit, summaries, OutputCodec.ContentHash(string.Join("\n", audit) + "\u0001" + string.Join("\n", summaries)));

    public bool HashIsValid => ContentHash == OutputCodec.ContentHash(string.Join("\n", AuditLines) + "\u0001" + string.Join("\n", SummaryLines));
}

/// <summary>
/// Everything a resume must find identical (contract section A.18.7). It is an explicit compatibility identity — NOT a binary hash: builds of this
/// repository are not byte-reproducible, so assembly hashes are provenance only. <see cref="EngineCompatibilityId"/> is a manually maintained label
/// that must be bumped whenever a change alters outputs or state for identical inputs.
/// </summary>
public sealed record CompatibilityIdentity(
    string CodecVersion,
    string StateDigestVersion,
    string EngineCompatibilityId,
    int ConfigSchemaVersion,
    string ConfigHash,
    string FreezeHash,
    string TranslatorOptionsHash,
    string ReadinessSpec,
    string RetentionHash,
    string StrategiesIdentity,
    string DurableOptionsHash,
    string SourceManifestHash)
{
    /// <summary>The engine behaviour label of this checkpoint of the code base. Bump it with any behaviour or state-layout change.</summary>
    public const string CurrentEngine = "trading-engine/npt1-v1";

    public string CanonicalText()
        => string.Create(CultureInfo.InvariantCulture, $"{CodecVersion}|{StateDigestVersion}|{EngineCompatibilityId}|{ConfigSchemaVersion}|{ConfigHash}|{FreezeHash}|{TranslatorOptionsHash}|{ReadinessSpec}|{RetentionHash}|{StrategiesIdentity}|{DurableOptionsHash}|{SourceManifestHash}");

    public IReadOnlyList<string> DifferencesFrom(CompatibilityIdentity other)
    {
        var diffs = new List<string>();
        void Check(string name, string? a, string? b)
        {
            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                diffs.Add($"{name}: durable '{a}' vs running '{b}'");
            }
        }

        Check(nameof(CodecVersion), CodecVersion, other.CodecVersion);
        Check(nameof(StateDigestVersion), StateDigestVersion, other.StateDigestVersion);
        Check(nameof(EngineCompatibilityId), EngineCompatibilityId, other.EngineCompatibilityId);
        Check(nameof(ConfigSchemaVersion), ConfigSchemaVersion.ToString(CultureInfo.InvariantCulture), other.ConfigSchemaVersion.ToString(CultureInfo.InvariantCulture));
        Check(nameof(ConfigHash), ConfigHash, other.ConfigHash);
        Check(nameof(FreezeHash), FreezeHash, other.FreezeHash);
        Check(nameof(TranslatorOptionsHash), TranslatorOptionsHash, other.TranslatorOptionsHash);
        Check(nameof(ReadinessSpec), ReadinessSpec, other.ReadinessSpec);
        Check(nameof(RetentionHash), RetentionHash, other.RetentionHash);
        Check(nameof(StrategiesIdentity), StrategiesIdentity, other.StrategiesIdentity);
        Check(nameof(DurableOptionsHash), DurableOptionsHash, other.DurableOptionsHash);
        Check(nameof(SourceManifestHash), SourceManifestHash, other.SourceManifestHash);
        return diffs;
    }
}

/// <summary>Where the code came from. Recorded for audit and never compared on resume (a different commit or binary with the same compatibility identity may resume a run).</summary>
public sealed record BinaryProvenance(string? GitCommit, bool? TreeClean, string Sdk, IReadOnlyDictionary<string, string> AssemblyHashes)
{
    public static BinaryProvenance None { get; } = new(null, null, "unknown", new Dictionary<string, string>());
}

/// <summary>
/// One variant of a run with the virtual account (book) it trades in. Recorded in the header so that a store can create the ownership rows
/// (run, account, variant) without parsing the configuration. <see cref="VariantHash"/> is the hash that determines the variant's behaviour.
/// </summary>
public sealed record VariantIdentity(string VariantId, string VariantHash, string StrategyId, string StrategyVersion, string AccountId);

/// <summary>The immutable header of a run: identity plus the snapshots it was started from.</summary>
public sealed record RunHeader(
    string RunId,
    DateOnly TradeDate,
    CompatibilityIdentity Compatibility,
    BinaryProvenance Provenance,
    string ConfigJson,
    string FreezeSnapshot,
    string SourceManifest,
    DateTimeOffset Horizon,
    IReadOnlyList<VariantIdentity> Variants,
    string ContentHash)
{
    public bool HashIsValid => ContentHash == ComputeHash(this);

    public static string ComputeHash(RunHeader h)
        => OutputCodec.ContentHash(string.Create(CultureInfo.InvariantCulture, $"{h.RunId}|{h.TradeDate:yyyy-MM-dd}|{h.Compatibility.CanonicalText()}|{h.ConfigJson}|{h.FreezeSnapshot}|{h.SourceManifest}|{h.Horizon.UtcTicks}|{string.Join(";", h.Variants.Select(v => $"{v.VariantId},{v.VariantHash},{v.StrategyId},{v.StrategyVersion},{v.AccountId}"))}"));

    public static RunHeader Create(RunHeader withoutHash) => withoutHash with { ContentHash = ComputeHash(withoutHash) };
}

/// <summary>Diagnostics of one recovery attempt. Deterministic fields only (the library reads no clock); the future database layer adds wall-clock columns. Never part of any economic digest.</summary>
public sealed record RecoveryLogEntry(int Attempt, long OwnerEpoch, long DurableSteps, long DurableOutputs, long DurableControls, string Outcome, string Detail);

/// <summary>A consistent, read-only copy of everything durable for one run.</summary>
public sealed record RunSnapshot(
    RunHeader? Header,
    IReadOnlyList<ControlRecord> Controls,
    IReadOnlyList<StepRecord> Steps,
    IReadOnlyList<OutputRecord> Outputs,
    IReadOnlyList<CheckpointRecord> Checkpoints,
    IReadOnlyList<ProjectionRow> Projections,
    FinalRecord? Final,
    bool Completed,
    IReadOnlyList<RecoveryLogEntry> RecoveryLog)
{
    /// <summary>The journaled canonical inputs of a live run, in sequence order (empty for a source-driven run).</summary>
    public IReadOnlyList<InputRecord> Inputs { get; init; } = Array.Empty<InputRecord>();
}
