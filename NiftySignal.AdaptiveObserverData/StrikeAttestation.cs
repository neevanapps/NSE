using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace NiftySignal.AdaptiveObserverData;

/// <summary>Digest and (de)serialisation of the per-contract hashes stored in <see cref="AdaptiveStrikeAttestationRow.Digests"/>.</summary>
public static class StrikeAttestation
{
    static readonly PropertyInfo[] Columns = typeof(AdaptiveOptionStrikeBarRow).GetProperties()
        .Where(p => p.Name is not (nameof(AdaptiveOptionStrikeBarRow.Id) or nameof(AdaptiveOptionStrikeBarRow.EndAvailableAtUtc)))
        .OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();

    /// <summary>First 16 hex chars of SHA-256 over every value column of the row (Id and the instant column excluded; doubles in round-trip format).</summary>
    public static string Digest(AdaptiveOptionStrikeBarRow row)
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

    public static string Format(IEnumerable<AdaptiveOptionStrikeBarRow> rows) =>
        string.Join(';', rows.Select(r => $"{r.Token}={Digest(r)}").OrderBy(x => x, StringComparer.Ordinal));

    public static Dictionary<string, string> Parse(string digests)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in digests.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.LastIndexOf('=');
            if (i > 0) map[part[..i]] = part[(i + 1)..];
        }

        return map;
    }
}
