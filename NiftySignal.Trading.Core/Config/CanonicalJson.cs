using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace NiftySignal.Trading.Core.Config;

/// <summary>Excludes a public property from <see cref="CanonicalJson"/> (and therefore from configuration hashes). Use only for values that are not configuration (for example a human description).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class CanonicalIgnoreAttribute : Attribute
{
}

/// <summary>SHA-256 (lower-case hex) of the canonical serialisation of a configuration object.</summary>
public readonly record struct ConfigHash(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// Canonical serialisation for configuration hashing and deterministic ids. The output depends only on the logical value:
/// <list type="bullet">
/// <item>Object properties are sorted by ordinal name; dictionary entries by ordinal key; sets by their canonical element text. Lists and arrays keep their order (order is meaning).</item>
/// <item>Numbers use the invariant culture; decimals are normalised (1.50 and 1.5 are equal, no exponent); double/float are rejected (use decimal).</item>
/// <item>DateTimeOffset is written as UTC with fixed precision; DateOnly, TimeOnly and TimeSpan with fixed invariant formats; DateTime and Guid are rejected (kind ambiguity / runtime-generated).</item>
/// <item>Enums are written by name and must be defined; strings are escaped and all non-ASCII is written as \uXXXX so the text is plain ASCII; no Unicode normalisation is applied.</item>
/// <item>Null properties are written explicitly as null, so "unset" is part of the value.</item>
/// </list>
/// Type names are not part of the output: only property names and values are. A change of meaning must therefore bump a SchemaVersion property.
/// </summary>
public static class CanonicalJson
{
    private const int MaxDepth = 32;

    public static string Serialize(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value, 0);
        return sb.ToString();
    }

    public static ConfigHash Hash(object? value)
        => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(value)))).ToLowerInvariant());

    private static void Write(StringBuilder sb, object? value, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new NotSupportedException("Canonical serialisation depth limit exceeded (cyclic or too deep).");
        }

        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case string s:
                WriteString(sb, s);
                return;
            case char:
                throw new NotSupportedException("char is not supported; use string.");
            case Enum e:
                if (!Enum.IsDefined(e.GetType(), e))
                {
                    throw new NotSupportedException($"Undefined enum value {e} of {e.GetType().Name}.");
                }

                WriteString(sb, e.ToString());
                return;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            case decimal d:
                sb.Append(FormatDecimal(d));
                return;
            case double or float:
                throw new NotSupportedException("double/float are not supported in canonical configuration; use decimal.");
            case DateTimeOffset dto:
                WriteString(sb, dto.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
                return;
            case DateOnly date:
                WriteString(sb, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return;
            case TimeOnly time:
                WriteString(sb, time.ToString("HH':'mm':'ss'.'fffffff", CultureInfo.InvariantCulture));
                return;
            case TimeSpan span:
                WriteString(sb, span.ToString("c", CultureInfo.InvariantCulture));
                return;
            case DateTime:
                throw new NotSupportedException("DateTime is not supported (ambiguous kind); use DateTimeOffset.");
            case Guid:
                throw new NotSupportedException("Guid is not supported (runtime-generated values must not be hashed).");
        }

        var type = value.GetType();
        if (IsDictionary(type))
        {
            WriteDictionary(sb, (IEnumerable)value, depth);
            return;
        }

        if (value is IEnumerable sequence)
        {
            if (IsSet(type))
            {
                var items = new List<string>();
                foreach (var item in sequence)
                {
                    var inner = new StringBuilder();
                    Write(inner, item, depth + 1);
                    items.Add(inner.ToString());
                }

                items.Sort(StringComparer.Ordinal);
                sb.Append('[').AppendJoin(',', items).Append(']');
                return;
            }

            sb.Append('[');
            var first = true;
            foreach (var item in sequence)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                Write(sb, item, depth + 1);
            }

            sb.Append(']');
            return;
        }

        WriteObject(sb, value, type, depth);
    }

    private static void WriteObject(StringBuilder sb, object value, Type type, int depth)
    {
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is { IsPublic: true } && p.GetCustomAttribute<CanonicalIgnoreAttribute>() is null)
            .Where(p => p.Name != "EqualityContract")
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        if (properties.Count == 0)
        {
            throw new NotSupportedException($"Type {type.FullName} has no serialisable public properties.");
        }

        sb.Append('{');
        for (var i = 0; i < properties.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            WriteString(sb, properties[i].Name);
            sb.Append(':');
            Write(sb, properties[i].GetValue(value), depth + 1);
        }

        sb.Append('}');
    }

    private static void WriteDictionary(StringBuilder sb, IEnumerable entries, int depth)
    {
        var items = new List<(string Key, object? Value)>();
        foreach (var entry in entries)
        {
            object? key;
            object? val;
            if (entry is DictionaryEntry de)
            {
                key = de.Key;
                val = de.Value;
            }
            else
            {
                var entryType = entry.GetType();
                key = entryType.GetProperty("Key")!.GetValue(entry);
                val = entryType.GetProperty("Value")!.GetValue(entry);
            }

            if (key is not string stringKey)
            {
                throw new NotSupportedException("Dictionary keys must be strings.");
            }

            items.Add((stringKey, val));
        }

        items.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        sb.Append('{');
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            WriteString(sb, items[i].Key);
            sb.Append(':');
            Write(sb, items[i].Value, depth + 1);
        }

        sb.Append('}');
    }

    private static bool IsDictionary(Type type)
        => typeof(IDictionary).IsAssignableFrom(type)
           || type.GetInterfaces().Append(type).Any(i => i.IsGenericType
               && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

    private static bool IsSet(Type type)
        => type.GetInterfaces().Append(type).Any(i => i.IsGenericType
            && (i.GetGenericTypeDefinition() == typeof(ISet<>) || i.GetGenericTypeDefinition() == typeof(IReadOnlySet<>)));

    private static string FormatDecimal(decimal d)
    {
        if (d == 0m)
        {
            return "0";
        }

        // Dividing by 1.000...0m strips trailing zeros without changing the value.
        var normalised = d / 1.0000000000000000000000000000m;
        return normalised.ToString("0.############################", CultureInfo.InvariantCulture);
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20 || c > 0x7E)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}
