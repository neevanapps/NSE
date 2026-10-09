using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Trading.Data;

/// <summary>
/// Lossless conversion between the recovery records and the entities (contract section A.19). Every conversion is exact in both directions: payload
/// text, hashes and tick counts are copied, never recomputed or reformatted, so a record that is stored and loaded again is equal to the original
/// (and its own content hash still validates). The store (CP6.3) adds nothing but I/O around these functions.
/// </summary>
public static class EntityMapper
{
    public static RunEntity ToEntity(RunHeader h, bool completed = false)
    {
        var c = h.Compatibility;
        return new RunEntity
        {
            RunId = h.RunId,
            TradeDate = h.TradeDate,
            SchemaVersion = TradingSchema.CurrentVersion,
            CodecVersion = c.CodecVersion,
            StateDigestVersion = c.StateDigestVersion,
            EngineCompatibilityId = c.EngineCompatibilityId,
            ConfigSchemaVersion = c.ConfigSchemaVersion,
            ConfigHash = c.ConfigHash,
            FreezeHash = c.FreezeHash,
            TranslatorOptionsHash = c.TranslatorOptionsHash,
            ReadinessSpec = c.ReadinessSpec,
            RetentionHash = c.RetentionHash,
            StrategiesIdentity = c.StrategiesIdentity,
            DurableOptionsHash = c.DurableOptionsHash,
            SourceManifestHash = c.SourceManifestHash,
            ProvenanceGitCommit = h.Provenance.GitCommit,
            ProvenanceTreeClean = h.Provenance.TreeClean,
            ProvenanceSdk = h.Provenance.Sdk,
            ProvenanceAssemblyHashes = string.Join("\n", h.Provenance.AssemblyHashes.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Key + "=" + a.Value)),
            ConfigJson = h.ConfigJson,
            FreezeSnapshot = h.FreezeSnapshot,
            SourceManifest = h.SourceManifest,
            HorizonUtcTicks = TickTime.ToTicks(h.Horizon),
            ContentHash = h.ContentHash,
            Completed = completed,
            Variants = h.Variants.Select(v => ToEntity(h.RunId, v)).ToList(),
        };
    }

    public static AccountEntity AccountOf(string runId, VariantIdentity v) => new() { RunId = runId, AccountId = v.AccountId };

    public static VariantEntity ToEntity(string runId, VariantIdentity v)
        => new() { RunId = runId, VariantId = v.VariantId, VariantHash = v.VariantHash, StrategyId = v.StrategyId, StrategyVersion = v.StrategyVersion, AccountId = v.AccountId };

    public static RunHeader ToRecord(RunEntity e)
    {
        var provenance = new BinaryProvenance(
            e.ProvenanceGitCommit,
            e.ProvenanceTreeClean,
            e.ProvenanceSdk,
            e.ProvenanceAssemblyHashes.Length == 0
                ? new Dictionary<string, string>()
                : e.ProvenanceAssemblyHashes.Split('\n').Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal));
        return new RunHeader(
            e.RunId,
            e.TradeDate,
            new CompatibilityIdentity(
                e.CodecVersion,
                e.StateDigestVersion,
                e.EngineCompatibilityId,
                e.ConfigSchemaVersion,
                e.ConfigHash,
                e.FreezeHash,
                e.TranslatorOptionsHash,
                e.ReadinessSpec,
                e.RetentionHash,
                e.StrategiesIdentity,
                e.DurableOptionsHash,
                e.SourceManifestHash),
            provenance,
            e.ConfigJson,
            e.FreezeSnapshot,
            e.SourceManifest,
            TickTime.FromTicks(e.HorizonUtcTicks),
            e.Variants.OrderBy(v => v.VariantId, StringComparer.Ordinal).Select(v => new VariantIdentity(v.VariantId, v.VariantHash, v.StrategyId, v.StrategyVersion, v.AccountId)).ToList(),
            e.ContentHash);
    }

    public static ControlJournalEntity ToEntity(string runId, ControlRecord r, long journalSeq)
        => new()
        {
            RunId = runId,
            ControlId = r.Id,
            JournalSeq = journalSeq,
            Kind = r.Kind.ToString(),
            ControlKey = r.Key,
            AvailableAtUtcTicks = TickTime.ToTicks(r.AvailableAt),
            AvailableAtUtc = TickTime.Convenience(TickTime.ToTicks(r.AvailableAt)),
            AfterOfferedRows = r.AfterOfferedRows,
            Payload = r.Payload,
            ContentHash = r.ContentHash,
            CodecVersion = OutputCodec.Version,
        };

    public static ControlRecord ToRecord(ControlJournalEntity e)
        => new(Enum.Parse<ControlKind>(e.Kind), e.ControlKey, TickTime.FromTicks(e.AvailableAtUtcTicks), e.AfterOfferedRows, e.Payload, e.ContentHash);

    public static StepEntity ToEntity(string runId, StepRecord s)
        => new()
        {
            RunId = runId,
            StepSeq = s.StepSeq,
            Kind = s.Kind.ToString(),
            Units = s.Units,
            EngineTimeUtcTicks = TickTime.ToTicks(s.EngineTime),
            OfferedRows = s.OfferedRows,
            FirstGroupOrdinal = s.FirstGroupOrdinal,
            LastGroupOrdinal = s.LastGroupOrdinal,
            LastGroupAtUtcTicks = s.LastGroupAt is { } at ? TickTime.ToTicks(at) : null,
            LastSourceId = s.LastSourceId,
            OutputFrom = s.OutputFrom,
            OutputToExclusive = s.OutputToExclusive,
            ContentHash = s.ContentHash,
            CodecVersion = OutputCodec.Version,
            Digests = s.Digests.Select(d => new StepDigestEntity { RunId = runId, StepSeq = s.StepSeq, Channel = d.Channel, LineCount = d.Count, Hash = d.Hash }).ToList(),
        };

    public static IReadOnlyList<ControlApplicationEntity> ApplicationsOf(string runId, StepRecord s)
        => s.AppliedControls.Select((id, i) => new ControlApplicationEntity { RunId = runId, ControlId = id, StepSeq = s.StepSeq, Ordinal = i }).ToList();

    /// <summary>Rebuilds a step. The digests are returned in channel order, which is the order the step hash uses; the applied controls come back in their stored order.</summary>
    public static StepRecord ToRecord(StepEntity e, IEnumerable<ControlApplicationEntity> applications)
        => new(
            e.StepSeq,
            Enum.Parse<UnitKind>(e.Kind),
            TickTime.FromTicks(e.EngineTimeUtcTicks),
            e.OfferedRows,
            e.Units,
            e.FirstGroupOrdinal,
            e.LastGroupOrdinal,
            e.LastGroupAtUtcTicks is { } at ? TickTime.FromTicks(at) : null,
            e.LastSourceId,
            e.OutputFrom,
            e.OutputToExclusive,
            applications.OrderBy(a => a.Ordinal).Select(a => a.ControlId).ToList(),
            e.Digests.OrderBy(d => d.Channel, StringComparer.Ordinal).Select(d => new ChannelDigestRecord(d.Channel, d.LineCount, d.Hash)).ToList(),
            e.ContentHash);

    public static OutputEntity ToEntity(string runId, OutputRecord o)
        => new()
        {
            RunId = runId,
            Seq = o.Seq,
            StepSeq = o.StepSeq,
            Type = o.Type,
            VariantId = o.VariantId,
            AtUtcTicks = TickTime.ToTicks(o.At),
            AtUtc = TickTime.Convenience(TickTime.ToTicks(o.At)),
            Payload = o.Payload,
            ContentHash = o.ContentHash,
            CodecVersion = OutputCodec.Version,
        };

    public static OutputRecord ToRecord(OutputEntity e)
        => new(e.Seq, e.StepSeq, e.Type, e.VariantId, TickTime.FromTicks(e.AtUtcTicks), e.Payload, e.ContentHash);

    public static CheckpointEntity ToEntity(string runId, CheckpointRecord c)
        => new() { RunId = runId, StepSeq = c.StepSeq, EngineTimeUtcTicks = TickTime.ToTicks(c.EngineTime), StateDigest = c.StateDigest, StateDigestVersion = Core.State.StateWriter.Version, StateText = c.StateText };

    public static CheckpointRecord ToRecord(CheckpointEntity e) => new(e.StepSeq, TickTime.FromTicks(e.EngineTimeUtcTicks), e.StateDigest, e.StateText);

    public static (FinalRecordEntity Record, IReadOnlyList<FinalLineEntity> Lines) ToEntity(string runId, FinalRecord f)
    {
        var lines = f.AuditLines.Select((t, i) => new FinalLineEntity { RunId = runId, Channel = "audit", LineNo = i, Text = t })
            .Concat(f.SummaryLines.Select((t, i) => new FinalLineEntity { RunId = runId, Channel = "summaries", LineNo = i, Text = t }))
            .ToList();
        return (new FinalRecordEntity { RunId = runId, ContentHash = f.ContentHash, CodecVersion = OutputCodec.Version }, lines);
    }

    public static FinalRecord ToRecord(FinalRecordEntity e, IEnumerable<FinalLineEntity> lines)
    {
        var all = lines.ToList();
        return new FinalRecord(
            all.Where(l => l.Channel == "audit").OrderBy(l => l.LineNo).Select(l => l.Text).ToList(),
            all.Where(l => l.Channel == "summaries").OrderBy(l => l.LineNo).Select(l => l.Text).ToList(),
            e.ContentHash);
    }

    public static RecoveryLogEntity ToEntity(string runId, RecoveryLogEntry r, long logSeq, DateTimeOffset? recordedAtUtc = null)
        => new() { RunId = runId, LogSeq = logSeq, Attempt = r.Attempt, OwnerEpoch = r.OwnerEpoch, DurableSteps = r.DurableSteps, DurableOutputs = r.DurableOutputs, DurableControls = r.DurableControls, Outcome = r.Outcome, Detail = r.Detail, RecordedAtUtc = recordedAtUtc };

    public static RecoveryLogEntry ToRecord(RecoveryLogEntity e) => new(e.Attempt, e.OwnerEpoch, e.DurableSteps, e.DurableOutputs, e.DurableControls, e.Outcome, e.Detail);
}
