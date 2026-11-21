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
    readonly Dictionary<int, HashSet<string>?> _byBar;

    UnverifiedStrikeBars(Dictionary<int, HashSet<string>?> byBar, bool tablePending) { _byBar = byBar; TablePending = tablePending; }

    public static UnverifiedStrikeBars None { get; } = new([], false);

    /// <summary>The marker table could not be read (migration pending): the readers cannot vouch for verification and say so instead of failing.</summary>
    public bool TablePending { get; }

    public bool Any => _byBar.Count > 0;

    public IReadOnlyCollection<int> Bars => _byBar.Keys;

    public bool IsUnverified(int barSeq, string token) => _byBar.TryGetValue(barSeq, out var t) && (t is null || t.Contains(token));

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
                .Select(x => new { x.BarSeq, x.Detail }).ToListAsync(ct);
            var map = new Dictionary<int, HashSet<string>?>();
            foreach (var r in rows)
            {
                var tokens = ParseTokens(r.Detail);
                if (!map.TryGetValue(r.BarSeq, out var existing)) map[r.BarSeq] = tokens;
                else if (existing is not null) { if (tokens is null) map[r.BarSeq] = null; else existing.UnionWith(tokens); }
            }

            return new(map, false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new([], true);
        }
    }

    public IEnumerable<AdaptiveOptionStrikeBarRow> Exclude(IEnumerable<AdaptiveOptionStrikeBarRow> rows) => Any ? rows.Where(r => !IsUnverified(r.BarSeq, r.Token)) : rows;
}
