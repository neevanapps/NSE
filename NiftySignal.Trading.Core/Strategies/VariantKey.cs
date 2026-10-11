using System.Globalization;
using System.Text.RegularExpressions;

namespace NiftySignal.Trading.Core.Strategies;

/// <summary>
/// Canonical identity of one tradable case: <c>(StrategyId, VariantId)</c>. StrategyId names a logical strategy family; VariantId names a case
/// inside that family and is unique only INSIDE its family, so <c>X/A</c> and <c>Y/A</c> are different variants. Ordinal ordering is by
/// StrategyId then VariantId and is the engine's deterministic processing order. The text form is <c>StrategyId/VariantId</c>; both parts
/// are restricted to a character set that cannot contain the separator (see <see cref="IsValidPart"/>), so the text form is unambiguous.
/// </summary>
public readonly record struct VariantKey : IComparable<VariantKey>
{
    private static readonly Regex Part = new("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Sentinel for outputs that belong to the whole engine rather than one variant (feed integrity transitions).</summary>
    public static VariantKey Global { get; } = new("*", "*", validate: false);

    public VariantKey(string strategyId, string variantId)
        : this(strategyId, variantId, validate: true)
    {
    }

    private VariantKey(string strategyId, string variantId, bool validate)
    {
        if (validate)
        {
            Require(strategyId, nameof(strategyId));
            Require(variantId, nameof(variantId));
        }

        StrategyId = strategyId;
        VariantId = variantId;
    }

    public string StrategyId { get; }

    public string VariantId { get; }

    public bool IsGlobal => StrategyId == "*" && VariantId == "*";

    /// <summary>Allowed characters: letters, digits, underscore, dot and hyphen; must start with a letter or digit. No slash (the separator), no whitespace.</summary>
    public static bool IsValidPart(string? part) => part is not null && Part.IsMatch(part);

    public static bool TryParse(string? text, out VariantKey key)
    {
        key = default;
        if (text is null)
        {
            return false;
        }

        if (text == "*/*")
        {
            key = Global;
            return true;
        }

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash != text.LastIndexOf('/') || !IsValidPart(text[..slash]) || !IsValidPart(text[(slash + 1)..]))
        {
            return false;
        }

        key = new VariantKey(text[..slash], text[(slash + 1)..], validate: false);
        return true;
    }

    public int CompareTo(VariantKey other)
    {
        var c = string.CompareOrdinal(StrategyId, other.StrategyId);
        return c != 0 ? c : string.CompareOrdinal(VariantId, other.VariantId);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{StrategyId}/{VariantId}");

    private static void Require(string value, string name)
    {
        if (!IsValidPart(value))
        {
            throw new ArgumentException($"'{value}' is not a valid {name}: use letters, digits, underscore, dot or hyphen, starting with a letter or digit (no slash or whitespace).", name);
        }
    }
}
