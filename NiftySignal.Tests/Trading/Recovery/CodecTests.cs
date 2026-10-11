using System.Globalization;
using System.Reflection;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Tests.Trading.Recovery;

public class DecimalTextTests
{
    public static IEnumerable<object[]> Values()
    {
        yield return new object[] { 0m };
        yield return new object[] { 0.0m };
        yield return new object[] { 0.000m };
        yield return new object[] { 125m };
        yield return new object[] { 125.0m };
        yield return new object[] { 125.00m };
        yield return new object[] { 125.50m };
        yield return new object[] { -125.50m };
        yield return new object[] { -0.01m };
        yield return new object[] { 0.0000000000000000000000000001m };
        yield return new object[] { -0.0000000000000000000000000001m };
        yield return new object[] { 1.2345678901234567890123456789m };
        yield return new object[] { decimal.MaxValue };
        yield return new object[] { decimal.MinValue };
        yield return new object[] { 79228162514264337593543950334m };
        yield return new object[] { 7922816251426433759354395033.5m };
        yield return new object[] { 100000000000m };
        yield return new object[] { 1.10m };
        yield return new object[] { 1.1m };
        yield return new object[] { 1.100m };
        yield return new object[] { decimal.One / 3m };
        yield return new object[] { decimal.MinusOne / 3m };
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void EveryDecimal_RoundTripsBitForBit_IncludingScaleAndTrailingZeros(decimal value)
    {
        var text = DecimalText.Format(value);
        var parsed = DecimalText.Parse(text);

        Assert.True(DecimalText.SameBits(value, parsed), $"{text}: {string.Join(",", decimal.GetBits(value))} vs {string.Join(",", decimal.GetBits(parsed))}");
        Assert.Equal(text, DecimalText.Format(parsed));
    }

    [Fact]
    public void NegativeZero_KeepsItsSign_AndEqualValuesWithDifferentScalesStayDistinct()
    {
        var negativeZero = new decimal(0, 0, 0, true, 2);
        var text = DecimalText.Format(negativeZero);

        Assert.StartsWith("-", text);
        Assert.True(DecimalText.SameBits(negativeZero, DecimalText.Parse(text)));
        Assert.NotEqual(DecimalText.Format(125m), DecimalText.Format(125.00m));
        Assert.NotEqual(DecimalText.Format(1.1m), DecimalText.Format(1.10m));
        Assert.Equal(1.1m, 1.10m);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("+1")]
    [InlineData("1e3")]
    [InlineData("1E3")]
    [InlineData("01")]
    [InlineData(".5")]
    [InlineData("1.")]
    [InlineData("1,5")]
    [InlineData("--1")]
    [InlineData("abc")]
    [InlineData("79228162514264337593543950336")]
    public void NonCanonicalOrOutOfRangeText_IsRejected(string text)
        => Assert.ThrowsAny<Exception>(() => DecimalText.Parse(text));

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public void FormatAndParse_AreCultureIndependent(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal("1234.5600", DecimalText.Format(1234.5600m));
            Assert.True(DecimalText.SameBits(1234.5600m, DecimalText.Parse("1234.5600")));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

public class OutputCodecTests
{
    private static IEnumerable<Type> OutputTypes()
        => typeof(EngineOutput).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(EngineOutput).IsAssignableFrom(t));

    [Fact]
    public void EveryConcreteOutputType_IsRegistered_SoANewOutputCannotBeForgotten()
    {
        var expected = OutputTypes().Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
        var actual = OutputCodec.SupportedTypes.Select(t => t.Name).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(expected, actual);
        Assert.True(expected.Count >= 17);
    }

    [Fact]
    public void EveryOutputType_RoundTripsBitExact_InAllVariantsOfNullabilityAndEnumValues()
    {
        var checkedCount = 0;
        foreach (var type in OutputTypes())
        {
            foreach (var variant in Enum.GetValues<SampleVariant>())
            {
                var sample = (EngineOutput)Sample.Build(type, variant);
                var payload = OutputCodec.Encode(sample);
                var decoded = OutputCodec.Decode(payload);

                Assert.Equal(type, decoded.GetType());
                Assert.Equal(payload, OutputCodec.Encode(decoded));
                Assert.Equal(sample, decoded);
                Assert.Equal(type.Name, OutputCodec.TypeNameOf(payload));
                checkedCount++;
            }
        }

        Assert.True(checkedCount >= 17 * 3);
    }

    [Fact]
    public void Encoding_IsDeterministic_AndCultureIndependent()
    {
        var fill = (EngineOutput)Sample.Build(OutputTypes().Single(t => t.Name == "FillRecord"), SampleVariant.Filled);
        var baseline = OutputCodec.Encode(fill);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(baseline, OutputCodec.Encode(fill));
            Assert.Equal(fill, OutputCodec.Decode(baseline));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.Equal(baseline, OutputCodec.Encode(fill));
    }

    [Fact]
    public void TheVariantIdentity_StrategyAndVariant_SurvivesTheCodec_AndSameVariantIdUnderTwoStrategiesStaysDistinct()
    {
        var at = new DateTimeOffset(2026, 9, 22, 4, 30, 0, TimeSpan.Zero);
        var xa = new PositionOpened(at, new VariantKey("X", "A"), "pos_1", "opp_1", "CE23900", 650, 125m);
        var ya = new PositionOpened(at, new VariantKey("Y", "A"), "pos_1", "opp_1", "CE23900", 650, 125m);

        var encodedX = OutputCodec.Encode(xa);
        var encodedY = OutputCodec.Encode(ya);

        Assert.Equal("trading-codec/2", OutputCodec.Version);
        Assert.Contains("\"Variant\":\"X/A\"", encodedX, StringComparison.Ordinal);
        Assert.NotEqual(encodedX, encodedY);
        Assert.NotEqual(OutputCodec.ContentHash(encodedX), OutputCodec.ContentHash(encodedY));
        var back = Assert.IsType<PositionOpened>(OutputCodec.Decode(encodedX));
        Assert.Equal(xa, back);
        Assert.Equal(("X", "A"), (back.StrategyId, back.VariantId));
        Assert.Equal(new VariantKey("Y", "A"), Assert.IsType<PositionOpened>(OutputCodec.Decode(encodedY)).Variant);

        // a global output (feed integrity) carries the global identity and also round-trips
        var transition = new NiftySignal.Trading.Core.Integrity.IntegrityTransition(at, NiftySignal.Trading.Core.Integrity.IntegrityState.Healthy, NiftySignal.Trading.Core.Integrity.IntegrityState.Degraded, FeedFaultReason.SocketDisconnected, "d", 3);
        Assert.True(OutputCodec.Decode(OutputCodec.Encode(transition)).Variant.IsGlobal);

        // a payload whose identity is not StrategyId/VariantId is refused, never repaired
        Assert.Throws<FormatException>(() => OutputCodec.Decode(encodedX.Replace("X/A", "A")));
    }


    [Fact]
    public void DecimalScale_SurvivesTheCodec_NotJustTheValue()
    {
        var type = OutputTypes().Single(t => t.Name == "PositionOpened");
        var a = new PositionOpenedProbe(125m);
        var b = new PositionOpenedProbe(125.00m);
        var sampleA = (EngineOutput)Sample.Build(type, SampleVariant.Filled, new Dictionary<string, object> { ["AvgEntryPrice"] = a.Price });
        var sampleB = (EngineOutput)Sample.Build(type, SampleVariant.Filled, new Dictionary<string, object> { ["AvgEntryPrice"] = b.Price });

        var payloadA = OutputCodec.Encode(sampleA);
        var payloadB = OutputCodec.Encode(sampleB);

        Assert.NotEqual(payloadA, payloadB);
        Assert.Contains("\"125\"", payloadA);
        Assert.Contains("\"125.00\"", payloadB);
        var decodedB = (NiftySignal.Trading.Core.Execution.PositionOpened)OutputCodec.Decode(payloadB);
        Assert.True(DecimalText.SameBits(125.00m, decodedB.AvgEntryPrice));
    }

    [Fact]
    public void NonUtcTimestamps_AreRejected_NotConvertedSilently()
    {
        var type = OutputTypes().Single(t => t.Name == "AuditRecord");
        var sample = (EngineOutput)Sample.Build(type, SampleVariant.Filled, new Dictionary<string, object> { ["At"] = new DateTimeOffset(2026, 9, 22, 9, 30, 0, TimeSpan.FromHours(5.5)) });

        Assert.Throws<NotSupportedException>(() => OutputCodec.Encode(sample));
    }

    [Fact]
    public void UnknownType_UndefinedEnum_AndCorruptPayloads_AreRejected()
    {
        var type = OutputTypes().Single(t => t.Name == "CancelRequest");
        var payload = OutputCodec.Encode((EngineOutput)Sample.Build(type, SampleVariant.Filled));

        Assert.Throws<NotSupportedException>(() => OutputCodec.Decode(payload.Replace("CancelRequest", "NoSuchOutput")));
        Assert.ThrowsAny<Exception>(() => OutputCodec.Decode(payload.Replace("\"Reason\":\"" + Enum.GetValues<CancelReason>()[0], "\"Reason\":\"Bogus")));
        Assert.ThrowsAny<Exception>(() => OutputCodec.Decode(payload[..^5]));
        Assert.ThrowsAny<Exception>(() => OutputCodec.Decode("{\"type\":\"CancelRequest\",\"f\":{}}"));
        Assert.ThrowsAny<Exception>(() => OutputCodec.Decode("not json"));
    }

    private sealed record PositionOpenedProbe(decimal Price);
}

public class ControlCodecTests
{
    [Fact]
    public void OperatorAndHealthEvents_RoundTrip_AndCarryTheirOwnSequenceAsIdentity()
    {
        var at = new DateTimeOffset(2026, 9, 22, 4, 20, 0, TimeSpan.Zero);
        var op = new OperatorCommandEvent(at, 7, OperatorCommand.Kill, "*", "tester", "scripted \"kill\" ü", KillMode.BlockEntriesAndLiquidate);
        var health = new FeedHealthEvent(at.AddSeconds(1), 3, FeedHealthState.Degraded, FeedFaultReason.DataGapOpen, "gap");

        var opRecord = ControlCodec.ToRecord(op);
        var healthRecord = ControlCodec.ToRecord(health);

        Assert.Equal("Operator:7", opRecord.Id);
        Assert.Equal("Health:3", healthRecord.Id);
        Assert.True(opRecord.HashIsValid);
        var back = Assert.IsType<OperatorCommandEvent>(ControlCodec.ToEvent(opRecord));
        Assert.Equal((op.AvailableAt, op.SourceId, op.Command, op.Scope, op.Actor, op.Reason, op.Mode), (back.AvailableAt, back.SourceId, back.Command, back.Scope, back.Actor, back.Reason, back.Mode));
        var healthBack = Assert.IsType<FeedHealthEvent>(ControlCodec.ToEvent(healthRecord));
        Assert.Equal((health.AvailableAt, health.SourceId, health.State, health.Reason, health.Detail), (healthBack.AvailableAt, healthBack.SourceId, healthBack.State, healthBack.Reason, healthBack.Detail));
        Assert.Equal(opRecord, ControlCodec.ToRecord(op));
    }

    [Fact]
    public void ProgressRecords_AreIdentifiedByTheirTime_AndTamperingIsDetected()
    {
        var t = new DateTimeOffset(2026, 9, 22, 4, 21, 0, TimeSpan.Zero);
        var progress = ControlCodec.ProgressRecord(ControlKind.SourceProgress, t, 42);
        var advance = ControlCodec.ProgressRecord(ControlKind.Advance, t, 42);

        Assert.NotEqual(progress.Id, advance.Id);
        Assert.Equal(t, ControlCodec.ParseTime(System.Text.Json.JsonDocument.Parse(progress.Payload).RootElement.GetProperty("time").GetString()!));
        Assert.False((progress with { Payload = progress.Payload.Replace("04:21", "04:22") }).HashIsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => ControlCodec.ProgressRecord(ControlKind.Operator, t, 0));
    }
}

public enum SampleVariant
{
    Filled = 0,
    NullsWhereAllowed = 1,
    LastEnumValues = 2,
}

/// <summary>Builds a valid instance of any output record from its constructor, so the codec tests cover every type without hand-written samples.</summary>
internal static class Sample
{
    public static object Build(Type type, SampleVariant variant, IReadOnlyDictionary<string, object>? overrides = null)
    {
        var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => !(c.GetParameters() is { Length: 1 } p && p[0].ParameterType == type))
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var counter = 0;
        var args = constructor.GetParameters().Select(p => overrides is not null && overrides.TryGetValue(p.Name!, out var o) ? o : Value(p.ParameterType, variant, p.Name!, ref counter)).ToArray();
        return constructor.Invoke(args);
    }

    private static object? Value(Type type, SampleVariant variant, string name, ref int counter)
    {
        counter++;
        var underlying = Nullable.GetUnderlyingType(type);
        var t = underlying ?? type;
        if (underlying is not null && variant == SampleVariant.NullsWhereAllowed)
        {
            return null;
        }

        if (t == typeof(VariantKey))
        {
            return new VariantKey($"S{counter}", $"V-{counter}");        // identity parts use a restricted character set; both must survive the codec
        }

        if (t == typeof(string))
        {
            return $"{name}-é\"{counter}";
        }

        if (t == typeof(bool))
        {
            return counter % 2 == 0;
        }

        if (t == typeof(int))
        {
            return -counter * 1000;
        }

        if (t == typeof(long))
        {
            return 1_000_000_000_000L + counter;
        }

        if (t == typeof(decimal))
        {
            return counter % 3 == 0 ? -125.5000m : counter % 3 == 1 ? 0.0100m : 99999999.12345678m;
        }

        if (t == typeof(DateTimeOffset))
        {
            return new DateTimeOffset(2026, 9, 22, 4, 0, 0, TimeSpan.Zero).AddTicks(counter * 1234567L);
        }

        if (t == typeof(DateOnly))
        {
            return new DateOnly(2026, 9, 22);
        }

        if (t == typeof(TimeSpan))
        {
            return TimeSpan.FromTicks(counter * 12345L);
        }

        if (t.IsEnum)
        {
            var values = Enum.GetValues(t);
            return values.GetValue(variant == SampleVariant.LastEnumValues ? values.Length - 1 : 0);
        }

        return Build(t, variant);
    }

}
