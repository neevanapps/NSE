using System.Security.Cryptography;
using System.Text;
using NiftySignal.Trading.Core.Config;

namespace NiftySignal.Trading.Core.Ids;

/// <summary>
/// Deterministic identifiers (contract section 8.3): SHA-256 over the canonical serialisation of (scheme tag, kind, parts),
/// truncated to 128 bits. No GUIDs, no counters from a database, no clock. Parts must be primitives supported by
/// <see cref="CanonicalJson"/> (strings, integers, decimals, DateOnly, DateTimeOffset, enums, bool, null); type is significant
/// (the integer 1 and the string "1" produce different ids), and the structure removes concatenation ambiguity.
/// </summary>
public static class DeterministicId
{
    public const string Scheme = "tid1";

    public static string Create(string prefix, string kind, params object?[] parts)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(parts);
        var payload = new object?[parts.Length + 2];
        payload[0] = Scheme;
        payload[1] = kind;
        Array.Copy(parts, 0, payload, 2, parts.Length);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(payload)));
        return prefix + "_" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }
}

/// <summary>
/// Typed id constructors for the contract's entities. Every id includes the variant (where one exists) so variants never share an
/// id namespace, and none includes a run id, so a historical and a live-paper run of the same session produce comparable ids.
/// </summary>
public static class EngineIds
{
    public static string Decision(ConfigHash configHash, string variantId, string strategyId, DateOnly tradeDate, string triggerKind, long triggerSeq, int ordinal)
        => DeterministicId.Create("dec", "decision", configHash.Value, variantId, strategyId, tradeDate, triggerKind, triggerSeq, ordinal);

    public static string Order(string decisionId, string leg, int attempt)
        => DeterministicId.Create("ord", "order", decisionId, leg, attempt);

    public static string Opportunity(string variantId, ConfigHash variantConfigHash, DateOnly tradeDate, string episodeId)
        => DeterministicId.Create("opp", "opportunity", variantId, variantConfigHash.Value, tradeDate, episodeId);

    public static string Position(ConfigHash configHash, string variantId, DateOnly tradeDate, string entryDecisionId)
        => DeterministicId.Create("pos", "position", configHash.Value, variantId, tradeDate, entryDecisionId);
}
