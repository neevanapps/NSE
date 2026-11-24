using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace NiftySignal.AdaptiveObserverData;

/// <summary>Digest and (de)serialisation of the per-contract hashes stored in <see cref="AdaptiveOptionsDepth5AttestationRow.Digests"/>. Same construction as <see cref="StrikeAttestation"/>, over its own row type.</summary>
public static class OptionsDepth5Attestation
{
    static readonly PropertyInfo[] Columns = typeof(AdaptiveOptionsDepth5Row).GetProperties()
        .Where(p => p.Name != nameof(AdaptiveOptionsDepth5Row.Id))
        .OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();

    /// <summary>First 16 hex chars of SHA-256 over every value column of the row (Id excluded; doubles in round-trip format).</summary>
    public static string Digest(AdaptiveOptionsDepth5Row row)
    {
        var sb = new StringBuilder();
        foreach (var c in Columns)
        {
            var v = c.GetValue(row);
            sb.Append(c.Name).Append('=').Append(v switch
            {
                null => "~",
                double d => d.ToString("R", CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => v.ToString(),
            }).Append('|');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    public static string Format(IEnumerable<AdaptiveOptionsDepth5Row> rows) =>
        string.Join(';', rows.Select(r => $"{r.Token}={Digest(r)}").OrderBy(x => x, StringComparer.Ordinal));

    public static Dictionary<string, string> Parse(string digests) => StrikeAttestation.Parse(digests);
}

/// <summary>
/// Which options Top-5 rows a consumer may use. A row is served only when (a) its bar is not marked UNVERIFIED, (b) the bar has a durable attestation of the current metrics version covering its token and (c) its values
/// still hash to the attested digest. Anything else - no row, no attestation, a marker, changed values, an unreadable table - is UNAVAILABLE (never zero, never substituted). Fails closed.
/// </summary>
public sealed class VerifiedOptionsDepth5
{
    readonly HashSet<int> _marked;
    readonly Dictionary<int, string> _attestedRaw;
    readonly Dictionary<int, Dictionary<string, string>> _attested = [];

    VerifiedOptionsDepth5(HashSet<int> marked, Dictionary<int, string> attestedRaw, bool tablePending) { _marked = marked; _attestedRaw = attestedRaw; TablePending = tablePending; }

    /// <summary>Nothing attested: FAIL CLOSED. Only a placeholder before a real load.</summary>
    public static VerifiedOptionsDepth5 None { get; } = new([], [], false);

    public bool TablePending { get; }

    public string? WithheldReason(AdaptiveOptionsDepth5Row row)
    {
        if (TablePending) return "verification tables unreadable";
        if (_marked.Contains(row.BarSeq)) return "marked unverified";
        if (!string.Equals(row.MetricsVersion, NiftySignal.AdaptiveObserver.OptionDepth5Bar.MetricsVersion, StringComparison.Ordinal)) return "unknown metrics version";
        if (!_attestedRaw.TryGetValue(row.BarSeq, out var raw)) return "no verification attestation";
        if (!_attested.TryGetValue(row.BarSeq, out var tokens)) _attested[row.BarSeq] = tokens = OptionsDepth5Attestation.Parse(raw);
        if (!tokens.TryGetValue(row.Token, out var digest)) return "no verification attestation";
        return string.Equals(digest, OptionsDepth5Attestation.Digest(row), StringComparison.Ordinal) ? null : "changed since it was attested";
    }

    public bool IsUnverified(AdaptiveOptionsDepth5Row row) => WithheldReason(row) is not null;

    public static async Task<VerifiedOptionsDepth5> LoadAsync(AdaptiveObserverDbContext db, long sessionId, int throughBarSeq, CancellationToken ct)
    {
        try
        {
            var version = NiftySignal.AdaptiveObserver.OptionDepth5Bar.MetricsVersion;
            var component = AdaptiveProjectionComponents.OptionsDepth5;
            var marked = (await db.ProjectionHealth.AsNoTracking()
                .Where(x => x.SessionId == sessionId && x.Component == component && x.Version == version && x.BarSeq <= throughBarSeq)
                .Select(x => x.BarSeq).ToListAsync(ct)).ToHashSet();
            var att = await db.OptionsDepth5Attestations.AsNoTracking()
                .Where(x => x.SessionId == sessionId && x.MetricsVersion == version && x.BarSeq <= throughBarSeq)
                .Select(x => new { x.BarSeq, x.Digests }).ToListAsync(ct);
            return new(marked, att.ToDictionary(x => x.BarSeq, x => x.Digests), false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new([], [], true);
        }
    }

    /// <summary>The verified rows of a bar range, in (bar, token) order; every other contract/bar is simply absent (unavailable).</summary>
    public static async Task<IReadOnlyList<AdaptiveOptionsDepth5Row>> LoadServedRowsAsync(AdaptiveObserverDbContext db, long sessionId, int fromBarSeq, int throughBarSeq, CancellationToken ct, IReadOnlyCollection<string>? tokens = null)
    {
        var tokenArray = tokens?.ToArray();
        var verified = await LoadAsync(db, sessionId, throughBarSeq, ct);
        if (verified.TablePending) return [];
        var version = NiftySignal.AdaptiveObserver.OptionDepth5Bar.MetricsVersion;
        List<AdaptiveOptionsDepth5Row> rows;
        try
        {
            rows = await db.OptionsDepth5.AsNoTracking()
            .Where(x => x.SessionId == sessionId && x.MetricsVersion == version && x.BarSeq >= fromBarSeq && x.BarSeq <= throughBarSeq && (tokenArray == null || tokenArray.Contains(x.Token)))
            .OrderBy(x => x.BarSeq).ThenBy(x => x.Token).ToListAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return [];
        }

        return rows.Where(r => !verified.IsUnverified(r)).ToList();
    }
}
