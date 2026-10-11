using System.Globalization;
using System.Text;
using System.Text.Json;
using NiftySignal.Trading.Core.Events;

namespace NiftySignal.Trading.Recovery.Codec;

/// <summary>
/// Encodes the non-derivable inputs for the write-ahead journal and decodes them back into engine events (contract section A.18.2). The identity of an
/// operator or health event is the sequence number it already carries, so a duplicate delivery is recognisable without any generated id.
/// </summary>
public static class ControlCodec
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static ControlRecord ToRecord(EngineEvent control)
    {
        ArgumentNullException.ThrowIfNull(control);
        switch (control)
        {
            case OperatorCommandEvent op:
                return ControlRecord.Create(
                    ControlKind.Operator,
                    op.SourceId,
                    op.AvailableAt,
                    0,
                    $"{{\"command\":{Q(op.Command.ToString())},\"scope\":{Q(op.Scope)},\"actor\":{Q(op.Actor)},\"reason\":{Q(op.Reason)},\"mode\":{Q(op.Mode.ToString())}}}");
            case FeedHealthEvent health:
                return ControlRecord.Create(
                    ControlKind.Health,
                    health.SourceId,
                    health.AvailableAt,
                    0,
                    $"{{\"state\":{Q(health.State.ToString())},\"reason\":{Q(health.Reason.ToString())},\"detail\":{Q(health.Detail)}}}");
            default:
                throw new NotSupportedException($"{control.GetType().Name} is not a journalable control event.");
        }
    }

    public static ControlRecord ProgressRecord(ControlKind kind, DateTimeOffset time, long afterOfferedRows)
    {
        if (kind is not (ControlKind.SourceProgress or ControlKind.Advance))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return ControlRecord.Create(kind, time.UtcTicks, time, afterOfferedRows, $"{{\"time\":{Q(Format(time))}}}");
    }

    public static EngineEvent ToEvent(ControlRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var doc = JsonDocument.Parse(record.Payload);
        var p = doc.RootElement;
        switch (record.Kind)
        {
            case ControlKind.Operator:
                return new OperatorCommandEvent(
                    record.AvailableAt,
                    record.Key,
                    Enum.Parse<OperatorCommand>(p.GetProperty("command").GetString()!),
                    p.GetProperty("scope").GetString()!,
                    p.GetProperty("actor").GetString()!,
                    p.GetProperty("reason").GetString()!,
                    Enum.Parse<KillMode>(p.GetProperty("mode").GetString()!));
            case ControlKind.Health:
                return new FeedHealthEvent(
                    record.AvailableAt,
                    record.Key,
                    Enum.Parse<FeedHealthState>(p.GetProperty("state").GetString()!),
                    Enum.Parse<FeedFaultReason>(p.GetProperty("reason").GetString()!),
                    p.GetProperty("detail").GetString()!);
            default:
                throw new NotSupportedException($"{record.Kind} is not an engine event.");
        }
    }

    public static DateTimeOffset ParseTime(string text)
        => new(DateTime.ParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal), TimeSpan.Zero);

    private static string Format(DateTimeOffset at)
        => at.Offset == TimeSpan.Zero ? at.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture) : throw new NotSupportedException("Only UTC timestamps are supported.");

    private static string Q(string text)
    {
        var sb = new StringBuilder("\"");
        sb.Append(System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping.Encode(text));
        return sb.Append('"').ToString();
    }
}
