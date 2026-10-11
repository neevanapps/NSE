using System.Globalization;

namespace NiftySignal.Trading.Recovery.Codec;

/// <summary>
/// The canonical, lossless text of a .NET <see cref="decimal"/> (contract section A.18.2): the invariant-culture representation WITH its scale
/// (125, 125.0 and 125.00 are three different texts and three different bit patterns), the sign of a negative zero kept, no exponent, no leading plus,
/// no spaces. <see cref="Parse"/> accepts only text that <see cref="Format"/> would itself produce, so a value round-trips bit for bit
/// (sign, scale and 96-bit mantissa) or fails loudly.
/// <para>
/// Supported domain: every finite <see cref="decimal"/>. A future database column must preserve exactly this domain: PostgreSQL <c>numeric</c>
/// without a declared precision and scale keeps any value and its display scale; a constrained <c>numeric(p,s)</c> would round or reject values with
/// more fractional digits than <c>s</c> and is therefore NOT chosen here. Until a store round-trip test proves otherwise, the canonical text is the
/// authoritative form and any native column is a convenience copy.
/// </para>
/// </summary>
public static class DecimalText
{
    public static string Format(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return value == 0m && IsNegative(value) && !text.StartsWith('-') ? "-" + text : text;
    }

    public static decimal Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"'{text}' is not a canonical decimal.");
        }

        if (value == 0m && text.StartsWith('-') && !IsNegative(value))
        {
            var bits = decimal.GetBits(value);
            value = new decimal(bits[0], bits[1], bits[2], true, (byte)((bits[3] >> 16) & 0xFF));
        }

        if (Format(value) != text)
        {
            throw new FormatException($"'{text}' is not the canonical text of the decimal it parses to ('{Format(value)}').");
        }

        return value;
    }

    public static bool SameBits(decimal a, decimal b) => decimal.GetBits(a).AsSpan().SequenceEqual(decimal.GetBits(b));

    private static bool IsNegative(decimal value) => (decimal.GetBits(value)[3] & int.MinValue) != 0;
}
