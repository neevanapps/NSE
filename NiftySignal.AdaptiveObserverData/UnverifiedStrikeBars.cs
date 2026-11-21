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

    UnverifiedStrikeBars(Dictionary<int, Dictionary<string, HashSet<string>?>> byBar, bool tablePending) { _byBar = byBar; TablePending = tablePending; }

    public static UnverifiedStrikeBars None { get; } = new([], false);

    /// <summary>The marker table could not be read: verification cannot be vouched for, so FAIL CLOSED - every per-strike row is reported unverified (withheld), never shown as a normal observation.</summary>
    public bool TablePending { get; }

    public bool Any => TablePending || _byBar.Count > 0;

    public IReadOnlyCollection<int> Bars => _byBar.Keys;

    /// <summary>Union of two token lists for a marker; null (whole bar) absorbs.</summary>
    public static HashSet<string>? Union(HashSet<string>? a, IReadOnlyCollection<string>? b)
    {
        if (a is null || b is null || b.Count == 0) return null;
        var u = new HashSet<string>(a, StringComparer.Ordinal);
        u.UnionWith(b);
        return u;
    }

    /// <summary>Version-aware: a marker for strike-metrics version V withholds only rows of version V (<paramref name="version"/> null = any version, conservative).</summary>
    public bool IsUnverified(int barSeq, string token, string? version = null)
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

    /// <summary>Markers of a session for bars up to <paramref name="throughBarSeq"/>. A missing table yields <see cref="TablePending"/>.</summary>
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

            return new(map, false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new([], true);
        }
    }

    public IEnumerable<AdaptiveOptionStrikeBarRow> Exclude(IEnumerable<AdaptiveOptionStrikeBarRow> rows) => Any ? rows.Where(r => !IsUnverified(r.BarSeq, r.Token, r.MetricsVersion)) : rows;
}
