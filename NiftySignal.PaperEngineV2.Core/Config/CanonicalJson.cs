using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NiftySignal.PaperEngineV2.Core.Config;

/// <summary>
/// Canonical form of a JSON document, the input of the configuration hash: object properties in ordinal order, no insignificant whitespace, strings escaped by the framework, numbers normalised through
/// <see cref="decimal"/> (trailing zeros removed, invariant culture) so <c>1.0</c>, <c>1.00</c> and <c>1</c> hash identically while any real change does not.
/// </summary>
public static class CanonicalJson
{
    public static string Canonicalize(JsonElement element)
    {
        var sb = new StringBuilder();
        Write(sb, element);
        return sb.ToString();
    }

    public static string Sha256Hex(string canonical) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    static void Write(StringBuilder sb, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var p in e.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(p.Name)).Append(':');
                    Write(sb, p.Value);
                }

                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in e.EnumerateArray())
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    Write(sb, item);
                }

                sb.Append(']');
                break;
            case JsonValueKind.String:
                sb.Append(JsonSerializer.Serialize(e.GetString()));
                break;
            case JsonValueKind.Number:
                sb.Append(e.TryGetDecimal(out var d) ? (d / 1.000000000000000000000000000000000m).ToString("0.#############################", CultureInfo.InvariantCulture) : e.GetDouble().ToString("R", CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            default: sb.Append("null"); break;
        }
    }
}
