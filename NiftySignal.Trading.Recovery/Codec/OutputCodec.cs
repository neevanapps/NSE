using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Trading.Recovery.Codec;

/// <summary>
/// Versioned, deterministic, lossless codec for the engine's output records (contract section A.18.2). Every concrete <see cref="EngineOutput"/> is
/// found by reflection and its primary constructor defines the wire shape (parameter order); a type with an unsupported member type fails when the
/// registry is built, and a test requires every output type to be registered, so a new output cannot be forgotten.
/// <para>
/// Wire format (<see cref="Version"/>): <c>{"type":"&lt;RecordName&gt;","f":{...}}</c>. Values: strings as JSON strings; <c>int</c>/<c>long</c>/<c>bool</c> as
/// JSON numbers / booleans; <b>decimals as canonical strings</b> (<see cref="DecimalText"/>: scale and negative zero preserved); times as
/// <c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c> UTC (a non-zero offset is rejected, nothing is converted silently); dates as <c>yyyy-MM-dd</c>; time spans in the
/// constant format; enums by name (an undefined value is rejected); null as JSON null; nested records as objects. No floating-point member is
/// supported (the engine emits none). Property order is fixed, so equal values always encode to the same bytes, in any culture.
/// </para>
/// </summary>
public static class OutputCodec
{
    public const string Version = "trading-codec/1";

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private const string DateFormat = "yyyy-MM-dd";

    private sealed record Shape(Type Type, ConstructorInfo Constructor, ParameterInfo[] Parameters, PropertyInfo[] Properties);

    private static readonly Lazy<IReadOnlyDictionary<string, Shape>> Registry = new(BuildRegistry);
    private static readonly ConcurrentDictionary<Type, Shape> Shapes = new();

    /// <summary>The output record types the codec can read and write (all concrete engine outputs).</summary>
    public static IReadOnlyList<Type> SupportedTypes => Registry.Value.Values.Select(s => s.Type).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

    public static string Encode(EngineOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var type = output.GetType();
        if (!Registry.Value.TryGetValue(type.Name, out var shape) || shape.Type != type)
        {
            throw new NotSupportedException($"Output type '{type.FullName}' is not registered with the codec.");
        }

        var sb = new StringBuilder(256);
        sb.Append("{\"type\":").Append(Quote(type.Name)).Append(",\"f\":");
        WriteObject(sb, output, shape);
        sb.Append('}');
        return sb.ToString();
    }

    public static EngineOutput Decode(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var name = root.GetProperty("type").GetString() ?? throw new FormatException("Missing output type.");
        if (!Registry.Value.TryGetValue(name, out var shape))
        {
            throw new NotSupportedException($"Unknown output type '{name}' (codec {Version}).");
        }

        return (EngineOutput)ReadObject(root.GetProperty("f"), shape);
    }

    public static string TypeNameOf(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("type").GetString() ?? string.Empty;
    }

    public static string ContentHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ------------------------------------------------------------------ registry

    private static IReadOnlyDictionary<string, Shape> BuildRegistry()
    {
        var map = new Dictionary<string, Shape>(StringComparer.Ordinal);
        foreach (var type in typeof(EngineOutput).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(EngineOutput).IsAssignableFrom(t)).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (!map.TryAdd(type.Name, ShapeOf(type)))
            {
                throw new InvalidOperationException($"Two output types share the name '{type.Name}'.");
            }
        }

        return map;
    }

    private static Shape ShapeOf(Type type)
        => Shapes.GetOrAdd(type, t =>
        {
            var constructors = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Where(c => !(c.GetParameters() is { Length: 1 } p && p[0].ParameterType == t))
                .OrderByDescending(c => c.GetParameters().Length)
                .ToArray();
            if (constructors.Length == 0)
            {
                throw new NotSupportedException($"'{t.FullName}' has no public constructor the codec can use.");
            }

            var constructor = constructors[0];
            var parameters = constructor.GetParameters();
            var properties = parameters
                .Select(p => t.GetProperty(p.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                    ?? throw new NotSupportedException($"'{t.FullName}': constructor parameter '{p.Name}' has no matching property."))
                .ToArray();
            foreach (var p in parameters)
            {
                EnsureSupported(p.ParameterType, t);
            }

            return new Shape(t, constructor, parameters, properties);
        });

    private static void EnsureSupported(Type type, Type owner)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string) || t == typeof(bool) || t == typeof(int) || t == typeof(long) || t == typeof(decimal) || t == typeof(DateTimeOffset)
            || t == typeof(DateOnly) || t == typeof(TimeSpan) || t.IsEnum)
        {
            return;
        }

        if (t.IsClass && !t.IsAbstract && t != typeof(object))
        {
            _ = ShapeOf(t);
            return;
        }

        throw new NotSupportedException($"'{owner.FullName}' has a member of unsupported type '{type.FullName}'.");
    }

    // ------------------------------------------------------------------ writing

    private static void WriteObject(StringBuilder sb, object value, Shape shape)
    {
        sb.Append('{');
        for (var i = 0; i < shape.Parameters.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(Quote(shape.Properties[i].Name)).Append(':');
            WriteValue(sb, shape.Properties[i].GetValue(value), shape.Parameters[i].ParameterType);
        }

        sb.Append('}');
    }

    private static void WriteValue(StringBuilder sb, object? value, Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (value is null)
        {
            if (underlying is null && type.IsValueType)
            {
                throw new InvalidOperationException($"A non-nullable {type.Name} is null.");
            }

            sb.Append("null");
            return;
        }

        var t = underlying ?? type;
        switch (value)
        {
            case string s:
                sb.Append(Quote(s));
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                break;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                break;
            case decimal d:
                sb.Append(Quote(DecimalText.Format(d)));
                break;
            case DateTimeOffset at:
                if (at.Offset != TimeSpan.Zero)
                {
                    throw new NotSupportedException($"Only UTC timestamps are supported; got offset {at.Offset}.");
                }

                sb.Append(Quote(at.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture)));
                break;
            case DateOnly date:
                sb.Append(Quote(date.ToString(DateFormat, CultureInfo.InvariantCulture)));
                break;
            case TimeSpan span:
                sb.Append(Quote(span.ToString("c", CultureInfo.InvariantCulture)));
                break;
            default:
                if (t.IsEnum)
                {
                    if (!Enum.IsDefined(t, value))
                    {
                        throw new NotSupportedException($"Undefined {t.Name} value {value}.");
                    }

                    sb.Append(Quote(value.ToString()!));
                }
                else
                {
                    WriteObject(sb, value, ShapeOf(value.GetType()));
                }

                break;
        }
    }

    private static string Quote(string text)
        => "\"" + JsonEncodedText.Encode(text, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString() + "\"";

    // ------------------------------------------------------------------ reading

    private static object ReadObject(JsonElement element, Shape shape)
    {
        var args = new object?[shape.Parameters.Length];
        var index = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (index >= shape.Parameters.Length || property.Name != shape.Properties[index].Name)
            {
                throw new FormatException($"'{shape.Type.Name}': unexpected or out-of-order member '{property.Name}'.");
            }

            args[index] = ReadValue(property.Value, shape.Parameters[index].ParameterType);
            index++;
        }

        if (index != shape.Parameters.Length)
        {
            throw new FormatException($"'{shape.Type.Name}': {shape.Parameters.Length - index} member(s) missing.");
        }

        return shape.Constructor.Invoke(args);
    }

    private static object? ReadValue(JsonElement element, Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        var t = underlying ?? type;
        if (element.ValueKind == JsonValueKind.Null)
        {
            if (underlying is null && t.IsValueType)
            {
                throw new FormatException($"A non-nullable {t.Name} is null.");
            }

            return null;
        }

        if (t == typeof(string))
        {
            return element.GetString();
        }

        if (t == typeof(bool))
        {
            return element.GetBoolean();
        }

        if (t == typeof(int))
        {
            return element.GetInt32();
        }

        if (t == typeof(long))
        {
            return element.GetInt64();
        }

        if (t == typeof(decimal))
        {
            return DecimalText.Parse(element.GetString() ?? throw new FormatException("A decimal must be a string."));
        }

        if (t == typeof(DateTimeOffset))
        {
            return new DateTimeOffset(DateTime.ParseExact(element.GetString()!, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal), TimeSpan.Zero);
        }

        if (t == typeof(DateOnly))
        {
            return DateOnly.ParseExact(element.GetString()!, DateFormat, CultureInfo.InvariantCulture);
        }

        if (t == typeof(TimeSpan))
        {
            return TimeSpan.ParseExact(element.GetString()!, "c", CultureInfo.InvariantCulture);
        }

        if (t.IsEnum)
        {
            var name = element.GetString()!;
            if (!Enum.TryParse(t, name, ignoreCase: false, out var parsed) || !Enum.IsDefined(t, parsed!))
            {
                throw new FormatException($"'{name}' is not a defined {t.Name}.");
            }

            return parsed;
        }

        return ReadObject(element, ShapeOf(t));
    }
}
