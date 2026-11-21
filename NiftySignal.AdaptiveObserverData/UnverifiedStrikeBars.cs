using NiftySignal.AdaptiveObserver;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace NiftySignal.AdaptiveObserverData;

/// <summary>
/// A4: per-strike option bars that recovery found DISAGREEING with the reconstructed values are marked UNVERIFIED in the existing durable
/// <see cref="AdaptiveProjectionHealthRow"/> ledger (component <see cref="AdaptiveProjectionComponents.OptionStrikeBars"/>, one row per session/version/bar - the unique index
/// allows only one per bar, so the affected tokens are listed in Detail as <c>tokens=T1,T2; first difference</c>; <c>tokens=*</c> = every strike of the bar). No schema change.
/// Readers must treat a (bar, token) listed here as an unavailable observation, never as a normal value and never zero-filled.
/// </summary>
public sealed class UnverifiedStrikeBars
{
    public const string AllMarker = "*";
    const int MaxDetail = 512;
    /// <summary>bar -> version -> tokens (null = every strike of the bar for that metrics version).</summary>
    readonly Dictionary<int, Dictionary<string, HashSet<string>?>> _byBar;

    /// <summary>bar -> raw attested digest list (current metrics version only); parsed per bar on first use, because a read only ever looks at the bars it serves.</summary>
    readonly Dictionary<int, string> _attestedRaw;
    readonly Dictionary<int, Dictionary<string, string>> _attested = [];

    UnverifiedStrikeBars(Dictionary<int, Dictionary<string, HashSet<string>?>> byBar, Dictionary<int, string> attestedRaw, bool tablePending) { _byBar = byBar; _attestedRaw = attestedRaw; TablePending = tablePending; }

    /// <summary>Nothing attested, nothing marked: FAIL CLOSED (a current-version row is unverified). Only a placeholder before a real load.</summary>
    public static UnverifiedStrikeBars None { get; } = new([], [], false);

    /// <summary>The marker or attestation table could not be read: verification cannot be vouched for, so FAIL CLOSED - every current-version per-strike row is reported unverified.</summary>
    public bool TablePending { get; }

    public IReadOnlyCollection<int> Bars => _byBar.Keys;

    /// <summary>Union of two token lists for a marker; null (whole bar) absorbs.</summary>
    public static HashSet<string>? Union(HashSet<string>? a, IReadOnlyCollection<string>? b)
    {
        if (a is null || b is null || b.Count == 0) return null;
        var u = new HashSet<string>(a, StringComparer.Ordinal);
        u.UnionWith(b);
        return u;
    }

    /// <summary>Marker-only, version-aware: a marker for strike-metrics version V withholds only rows of version V (<paramref name="version"/> null = any version, conservative).</summary>
    public bool IsMarked(int barSeq, string token, string? version = null)
    {
        if (TablePending) return true;
        if (!_byBar.TryGetValue(barSeq, out var byVersion)) return false;
        foreach (var (v, tokens) in byVersion)
        {
            if (version is not null && !string.Equals(v, version, StringComparison.Ordinal)) continue;
            if (tokens is null || tokens.Contains(token)) return true;
        }

        return false;
    }

    /// <summary>Why a row is withheld, or null when it may be served: marked UNVERIFIED, no attestation covering its token, or its values changed since it was attested. Legacy versions have no attestation (marker-only).</summary>
    public string? WithheldReason(AdaptiveOptionStrikeBarRow row)
    {
        if (TablePending) return "verification tables unreadable";
        if (IsMarked(row.BarSeq, row.Token, row.MetricsVersion)) return "marked unverified";
        if (!string.Equals(row.MetricsVersion, OptionStrikeBarMetrics.MetricsVersion, StringComparison.Ordinal)) return null;
        if (!_attestedRaw.TryGetValue(row.BarSeq, out var raw)) return "no verification attestation";
        if (!_attested.TryGetValue(row.BarSeq, out var tokens)) _attested[row.BarSeq] = tokens = StrikeAttestation.Parse(raw);
        if (!tokens.TryGetValue(row.Token, out var digest)) return "no verification attestation";
        return string.Equals(digest, StrikeAttestation.Digest(row), StringComparison.Ordinal) ? null : "changed since it was attested";
    }

    public bool IsUnverified(AdaptiveOptionStrikeBarRow row) => WithheldReason(row) is not null;

    public static string FormatDetail(IReadOnlyCollection<string> tokens, string difference)
    {
        var list = tokens.Count == 0 ? AllMarker : string.Join(',', tokens.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
        var head = $"tokens={list}";
        if (head.Length > MaxDetail - 40) head = $"tokens={AllMarker}";   // cannot list them all: the whole bar is unverified (conservative)
        var detail = $"{head}; {difference}";
        return detail.Length > MaxDetail ? detail[..MaxDetail] : detail;
    }

    /// <summary>Null = every strike of the bar.</summary>
    public static HashSet<string>? ParseTokens(string detail)
    {
        const string prefix = "tokens=";
        if (!detail.StartsWith(prefix, StringComparison.Ordinal)) return null;       // unreadable detail: conservative, whole bar
        var end = detail.IndexOf(';');
        var list = (end < 0 ? detail[prefix.Length..] : detail[prefix.Length..end]).Trim();
        if (list.Length == 0 || list == AllMarker) return null;
        return list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Markers and attestations of a session for bars up to <paramref name="throughBarSeq"/>. A missing table yields <see cref="TablePending"/>.</summary>
    public static async Task<UnverifiedStrikeBars> LoadAsync(AdaptiveObserverDbContext db, long sessionId, int throughBarSeq, CancellationToken ct)
    {
        try
        {
            var component = AdaptiveProjectionComponents.OptionStrikeBars;
            var rows = await db.ProjectionHealth.AsNoTracking()
                .Where(x => x.SessionId == sessionId && x.Component == component && x.BarSeq <= throughBarSeq)
                .Select(x => new { x.BarSeq, x.Version, x.Detail }).ToListAsync(ct);
            var map = new Dictionary<int, Dictionary<string, HashSet<string>?>>();
            foreach (var r in rows)
            {
                var tokens = ParseTokens(r.Detail);
                if (!map.TryGetValue(r.BarSeq, out var byVersion)) map[r.BarSeq] = byVersion = new(StringComparer.Ordinal);
                if (!byVersion.TryGetValue(r.Version, out var existing)) byVersion[r.Version] = tokens;
                else if (existing is not null) { if (tokens is null) byVersion[r.Version] = null; else existing.UnionWith(tokens); }
            }

            var version = OptionStrikeBarMetrics.MetricsVersion;
            var att = await db.StrikeAttestations.AsNoTracking()
                .Where(x => x.SessionId == sessionId && x.MetricsVersion == version && x.BarSeq <= throughBarSeq)
                .Select(x => new { x.BarSeq, x.Digests }).ToListAsync(ct);
            var attested = att.ToDictionary(x => x.BarSeq, x => x.Digests);
            return new(map, attested, false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new([], [], true);
        }
    }

    public IEnumerable<AdaptiveOptionStrikeBarRow> Exclude(IEnumerable<AdaptiveOptionStrikeBarRow> rows) => rows.Where(r => !IsUnverified(r));
}
