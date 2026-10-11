using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Trading.Data;

/// <summary>The schema, codec and engine versions this build of the code writes and understands (contract section A.19.7).</summary>
public static class TradingSchema
{
    /// <summary>Version of the database schema (the table and column layout). Bump together with a migration that changes it.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The component name of the single row this code owns in <c>schema_versions</c>.</summary>
    public const string Component = "trading-engine";

    /// <summary>The oldest code build (by engine compatibility label) that may read data written by this schema. The label is the manually maintained engine behaviour label.</summary>
    public static string MinimumCompatibleCode => CompatibilityIdentity.CurrentEngine;
}

public sealed record CompatibilityVerdict(bool Compatible, IReadOnlyList<string> Problems)
{
    public static CompatibilityVerdict Ok { get; } = new(true, Array.Empty<string>());
}

/// <summary>
/// Pure checks that a store runs before it trusts a database (never against a live connection here): the database schema version row, and each run's
/// recorded codec / state-digest / engine versions. A mismatch means: do not write, do not resume; a newer schema than the code is never guessed at.
/// This class performs no I/O and has no side effect; "no implicit migration" is enforced by the absence of any Migrate / EnsureCreated call in the
/// assembly (tested).
/// </summary>
public static class SchemaCompatibility
{
    public static CompatibilityVerdict CheckSchema(SchemaVersionEntity? row)
    {
        var problems = new List<string>();
        if (row is null)
        {
            problems.Add("schema_versions has no 'trading-engine' row: the database was not created by the trading migrations");
        }
        else
        {
            if (row.SchemaVersion > TradingSchema.CurrentVersion)
            {
                problems.Add($"database schema version {row.SchemaVersion} is newer than this code understands ({TradingSchema.CurrentVersion})");
            }
            else if (row.SchemaVersion < TradingSchema.CurrentVersion)
            {
                problems.Add($"database schema version {row.SchemaVersion} is older than this code requires ({TradingSchema.CurrentVersion}); apply the reviewed migration first");
            }

            if (!string.Equals(row.CodecVersion, OutputCodec.Version, StringComparison.Ordinal))
            {
                problems.Add($"database codec version '{row.CodecVersion}' differs from this code's '{OutputCodec.Version}'");
            }

            if (!string.Equals(row.StateDigestVersion, Core.State.StateWriter.Version, StringComparison.Ordinal))
            {
                problems.Add($"database state-digest version '{row.StateDigestVersion}' differs from this code's '{Core.State.StateWriter.Version}'");
            }
        }

        return problems.Count == 0 ? CompatibilityVerdict.Ok : new CompatibilityVerdict(false, problems);
    }

    /// <summary>A run may be resumed only when its recorded versions and compatibility identity equal the running ones (the CP6.1 rule), and its schema version is this code's.</summary>
    public static CompatibilityVerdict CheckRun(RunEntity run, CompatibilityIdentity running)
    {
        var problems = new List<string>();
        if (run.SchemaVersion != TradingSchema.CurrentVersion)
        {
            problems.Add($"run schema version {run.SchemaVersion} differs from this code's {TradingSchema.CurrentVersion}");
        }

        problems.AddRange(EntityMapper.ToRecord(run).Compatibility.DifferencesFrom(running));
        return problems.Count == 0 ? CompatibilityVerdict.Ok : new CompatibilityVerdict(false, problems);
    }

    /// <summary>The row a freshly migrated database holds (inserted by the migration itself, so a database without it is detected).</summary>
    public static SchemaVersionEntity Expected()
        => new()
        {
            Component = TradingSchema.Component,
            SchemaVersion = TradingSchema.CurrentVersion,
            MinimumCompatibleCode = TradingSchema.MinimumCompatibleCode,
            CodecVersion = OutputCodec.Version,
            StateDigestVersion = Core.State.StateWriter.Version,
        };
}
