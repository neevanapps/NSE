namespace NiftySignal.Trading.Data;

// Entities of the trading database (contract section A.19). Conventions:
//  * Every text identifier is compared ordinally (the whole model uses the "C" collation).
//  * Every instant that takes part in ordering or identity is stored as UTC ticks (bigint, 100 ns, exact). A "..._utc" timestamptz column, where present, is
//    a CONVENIENCE copy truncated to microseconds by PostgreSQL; it is never compared, hashed or used to rebuild a record.
//  * Every canonical payload is stored as text (authoritative) with its SHA-256 (64 upper-case hex characters) and the codec version that produced it.
//  * Decimals appear only inside canonical payload text or as canonical decimal text columns (..._text); there is no numeric column.

public sealed class SchemaVersionEntity
{
    public string Component { get; set; } = string.Empty;

    public int SchemaVersion { get; set; }

    public string MinimumCompatibleCode { get; set; } = string.Empty;

    public string CodecVersion { get; set; } = string.Empty;

    public string StateDigestVersion { get; set; } = string.Empty;
}

public sealed class RunEntity
{
    public string RunId { get; set; } = string.Empty;

    public DateOnly TradeDate { get; set; }

    public int SchemaVersion { get; set; }

    // CompatibilityIdentity (explicit, never a binary hash)
    public string CodecVersion { get; set; } = string.Empty;

    public string StateDigestVersion { get; set; } = string.Empty;

    public string EngineCompatibilityId { get; set; } = string.Empty;

    public int ConfigSchemaVersion { get; set; }

    public string ConfigHash { get; set; } = string.Empty;

    public string FreezeHash { get; set; } = string.Empty;

    public string TranslatorOptionsHash { get; set; } = string.Empty;

    public string ReadinessSpec { get; set; } = string.Empty;

    public string RetentionHash { get; set; } = string.Empty;

    public string StrategiesIdentity { get; set; } = string.Empty;

    public string DurableOptionsHash { get; set; } = string.Empty;

    public string SourceManifestHash { get; set; } = string.Empty;

    // Provenance (recorded, never compared on resume)
    public string? ProvenanceGitCommit { get; set; }

    public bool? ProvenanceTreeClean { get; set; }

    public string ProvenanceSdk { get; set; } = string.Empty;

    public string ProvenanceAssemblyHashes { get; set; } = string.Empty;

    // Snapshots the run was started from
    public string ConfigJson { get; set; } = string.Empty;

    public string FreezeSnapshot { get; set; } = string.Empty;

    public string SourceManifest { get; set; } = string.Empty;

    public long HorizonUtcTicks { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Written once by the commit of the finish step.</summary>
    public bool Completed { get; set; }

    public List<VariantEntity> Variants { get; set; } = new();
}

/// <summary>Single-writer ownership. <see cref="Epoch"/> only ever increases; every write is fenced against it. The advisory lock itself lives in the database session, not in this row.</summary>
public sealed class RunOwnerEntity
{
    public string RunId { get; set; } = string.Empty;

    public long Epoch { get; set; }

    public string OwnerId { get; set; } = string.Empty;

    /// <summary>Diagnostic only.</summary>
    public DateTimeOffset? AcquiredAtUtc { get; set; }

    /// <summary>Diagnostic only.</summary>
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
}

/// <summary>A virtual account (book). One independent book per variant by default; the combined fills of two variants are never presented as one account.</summary>
public sealed class AccountEntity
{
    public string RunId { get; set; } = string.Empty;

    public string AccountId { get; set; } = string.Empty;
}

public sealed class VariantEntity
{
    public string RunId { get; set; } = string.Empty;

    public string VariantId { get; set; } = string.Empty;

    public string VariantHash { get; set; } = string.Empty;

    public string StrategyId { get; set; } = string.Empty;

    public string StrategyVersion { get; set; } = string.Empty;

    public string AccountId { get; set; } = string.Empty;
}

/// <summary>A journaled non-derivable input. A row here means DURABLE; whether it was APPLIED is a separate fact (<see cref="ControlApplicationEntity"/>).</summary>
public sealed class ControlJournalEntity
{
    public string RunId { get; set; } = string.Empty;

    public string ControlId { get; set; } = string.Empty;

    /// <summary>Append order within the run (1-based, contiguous). Replay applies same-position records in this order.</summary>
    public long JournalSeq { get; set; }

    public string Kind { get; set; } = string.Empty;

    public long ControlKey { get; set; }

    public long AvailableAtUtcTicks { get; set; }

    public DateTimeOffset AvailableAtUtc { get; set; }

    public long AfterOfferedRows { get; set; }

    public string Payload { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public string CodecVersion { get; set; } = string.Empty;
}

/// <summary>Written in the same transaction as the step that applied the control: applied progress is committed progress.</summary>
public sealed class ControlApplicationEntity
{
    public string RunId { get; set; } = string.Empty;

    public string ControlId { get; set; } = string.Empty;

    public long StepSeq { get; set; }

    /// <summary>Position in <c>StepRecord.AppliedControls</c> (the list order is part of the step hash).</summary>
    public int Ordinal { get; set; }
}

public sealed class StepEntity
{
    public string RunId { get; set; } = string.Empty;

    public long StepSeq { get; set; }

    public string Kind { get; set; } = string.Empty;

    public int Units { get; set; }

    public long EngineTimeUtcTicks { get; set; }

    public long OfferedRows { get; set; }

    public long? FirstGroupOrdinal { get; set; }

    public long? LastGroupOrdinal { get; set; }

    public long? LastGroupAtUtcTicks { get; set; }

    public long? LastSourceId { get; set; }

    public long OutputFrom { get; set; }

    public long OutputToExclusive { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    public string CodecVersion { get; set; } = string.Empty;

    public List<StepDigestEntity> Digests { get; set; } = new();
}

/// <summary>One of the nine cumulative channel digests at a step end. All nine are retained for every committed step.</summary>
public sealed class StepDigestEntity
{
    public string RunId { get; set; } = string.Empty;

    public long StepSeq { get; set; }

    public string Channel { get; set; } = string.Empty;

    public long LineCount { get; set; }

    public string Hash { get; set; } = string.Empty;
}

public sealed class CheckpointEntity
{
    public string RunId { get; set; } = string.Empty;

    public long StepSeq { get; set; }

    public long EngineTimeUtcTicks { get; set; }

    public string StateDigest { get; set; } = string.Empty;

    public string StateDigestVersion { get; set; } = string.Empty;

    public string? StateText { get; set; }
}

public sealed class OutputEntity
{
    public string RunId { get; set; } = string.Empty;

    public long Seq { get; set; }

    public long StepSeq { get; set; }

    public string Type { get; set; } = string.Empty;

    /// <summary>The output's variant, or a scope label such as <c>*</c> for global outputs; deliberately not a foreign key.</summary>
    public string VariantId { get; set; } = string.Empty;

    public long AtUtcTicks { get; set; }

    public DateTimeOffset AtUtc { get; set; }

    public string Payload { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public string CodecVersion { get; set; } = string.Empty;
}

/// <summary>Write-once identity of a position. <see cref="ClosedOutputSeq"/> is set once when the position closes; an open position occupies its slot.</summary>
public sealed class PositionEntity
{
    public string RunId { get; set; } = string.Empty;

    public string PositionId { get; set; } = string.Empty;

    public string VariantId { get; set; } = string.Empty;

    public string AccountId { get; set; } = string.Empty;

    public int SlotNo { get; set; }

    public string OpportunityId { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public long OpenedOutputSeq { get; set; }

    public long? ClosedOutputSeq { get; set; }
}

public sealed class OrderEntity
{
    public string RunId { get; set; } = string.Empty;

    public string OrderId { get; set; } = string.Empty;

    public string VariantId { get; set; } = string.Empty;

    /// <summary><c>Entry</c> or <c>Exit</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    public string? OpportunityId { get; set; }

    /// <summary>Slot of the variant that an entry order reserves (null for exit orders).</summary>
    public int? SlotNo { get; set; }

    /// <summary>Exit orders belong to a position from creation; an entry order's position exists only after its first fill.</summary>
    public string? PositionId { get; set; }

    public long CreatedOutputSeq { get; set; }
}

public sealed class FillEntity
{
    public string RunId { get; set; } = string.Empty;

    public string FillId { get; set; } = string.Empty;

    public string OrderId { get; set; } = string.Empty;

    public string PositionId { get; set; } = string.Empty;

    public string VariantId { get; set; } = string.Empty;

    public string AccountId { get; set; } = string.Empty;

    public long OutputSeq { get; set; }

    public string Side { get; set; } = string.Empty;

    public long Quantity { get; set; }

    public string PriceText { get; set; } = string.Empty;

    public string NotionalText { get; set; } = string.Empty;
}

public sealed class LedgerEntryEntity
{
    public string RunId { get; set; } = string.Empty;

    public string FillId { get; set; } = string.Empty;

    public string Component { get; set; } = string.Empty;

    public string AmountText { get; set; } = string.Empty;
}

public sealed class AuditEntryEntity
{
    public string RunId { get; set; } = string.Empty;

    public long OutputSeq { get; set; }

    public string VariantId { get; set; } = string.Empty;

    public string? OpportunityId { get; set; }

    public string Stage { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;
}

public sealed class FinalRecordEntity
{
    public string RunId { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public string CodecVersion { get; set; } = string.Empty;
}

/// <summary>The audit and summary lines of the finish step, one row per line so that nothing is joined or split.</summary>
public sealed class FinalLineEntity
{
    public string RunId { get; set; } = string.Empty;

    public string Channel { get; set; } = string.Empty;

    public int LineNo { get; set; }

    public string Text { get; set; } = string.Empty;
}

/// <summary>Diagnostics of recovery attempts. No economic table references it, and it is not an input of any economic digest.</summary>
public sealed class RecoveryLogEntity
{
    public string RunId { get; set; } = string.Empty;

    /// <summary>Assigned by the store (1-based, contiguous per run). <see cref="Attempt"/> is the attempt number reported by the recovery code and is not unique.</summary>
    public long LogSeq { get; set; }

    public int Attempt { get; set; }

    public long OwnerEpoch { get; set; }

    public long DurableSteps { get; set; }

    public long DurableOutputs { get; set; }

    public long DurableControls { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    /// <summary>Wall-clock time of the write; diagnostic only (the library itself reads no clock).</summary>
    public DateTimeOffset? RecordedAtUtc { get; set; }
}
