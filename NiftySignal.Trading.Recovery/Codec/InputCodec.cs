using System.Text;
using System.Text.Json;
using NiftySignal.Trading.Core.Events;

namespace NiftySignal.Trading.Recovery.Codec;

/// <summary>
/// The canonical text of the inputs a live run journals (contract section B.5): quotes, completed bars (with their readiness evidence), feed-health and
/// operator events, clock advances and the finish. Decimals use <see cref="DecimalText"/>, instants are UTC ticks, so every value round-trips bit for bit and the
/// payload of an equal event is always the same text.
/// </summary>
public static class InputCodec
{
    public static InputRecord ToRecord(long seq, long stepSeq, long groupOrdinal, EngineEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var payload = e switch
        {
            QuoteEvent q => QuotePayload(q),
            BarCompletedEvent b => BarPayload(b),
            OperatorCommandEvent or FeedHealthEvent => ControlCodec.ToRecord(e).Payload,
            _ => throw new NotSupportedException($"{e.GetType().Name} is not a journalable input."),
        };
        return InputRecord.Create(seq, stepSeq, groupOrdinal, e.Kind.ToString(), e.SourceId, e.Token, e.AvailableAt, payload);
    }

    public static InputRecord AdvanceRecord(long seq, long stepSeq, long groupOrdinal, DateTimeOffset watermark)
        => InputRecord.Create(seq, stepSeq, groupOrdinal, InputRecord.AdvanceKind, 0, string.Empty, watermark, "{}");

    public static InputRecord FinishRecord(long seq, long stepSeq, long groupOrdinal, DateTimeOffset horizon)
        => InputRecord.Create(seq, stepSeq, groupOrdinal, InputRecord.FinishKind, 0, string.Empty, horizon, "{}");

    public static bool IsEvent(InputRecord r) => r.Kind != InputRecord.AdvanceKind && r.Kind != InputRecord.FinishKind;

    public static EngineEvent ToEvent(InputRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (!Enum.TryParse<EventKind>(r.Kind, out var kind) || !Enum.IsDefined(kind))
        {
            throw new NotSupportedException($"'{r.Kind}' is not an engine event kind.");
        }

        using var doc = JsonDocument.Parse(r.Payload);
        var p = doc.RootElement;
        switch (kind)
        {
            case EventKind.SpotQuote or EventKind.OptionQuote or EventKind.FutureQuote:
                return new QuoteEvent(r.At, kind, r.SourceId, r.Token, Dec(p, "bid"), Dec(p, "ask"), Dec(p, "last"), Long(p, "bidQty"), Long(p, "askQty"));
            case EventKind.BarCompleted:
            {
                BarInfo? info = null;
                if (p.GetProperty("info") is { ValueKind: JsonValueKind.Object } i)
                {
                    info = new BarInfo(
                        new DateTimeOffset(i.GetProperty("startTicks").GetInt64(), TimeSpan.Zero),
                        new DateTimeOffset(i.GetProperty("endTicks").GetInt64(), TimeSpan.Zero),
                        i.GetProperty("volume").GetInt64(),
                        i.GetProperty("tradeUpdates").GetInt32(),
                        i.GetProperty("isValid").GetBoolean(),
                        i.GetProperty("consecutiveValidBars").GetInt32(),
                        i.GetProperty("requiredValidBars").GetInt32(),
                        i.GetProperty("signalStarted").GetBoolean(),
                        i.GetProperty("ready").GetBoolean(),
                        i.GetProperty("readinessSpec").GetString()!);
                }

                return new BarCompletedEvent(r.At, r.SourceId, r.Token, info);
            }

            case EventKind.Control:
                return ControlCodec.ToEvent(ControlRecord.Create(ControlKind.Operator, r.SourceId, r.At, 0, r.Payload));
            case EventKind.FeedHealth:
                return ControlCodec.ToEvent(ControlRecord.Create(ControlKind.Health, r.SourceId, r.At, 0, r.Payload));
            default:
                throw new NotSupportedException($"'{r.Kind}' is not an engine event kind.");
        }
    }

    private static string QuotePayload(QuoteEvent q)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            WriteDecimal(w, "bid", q.Bid);
            WriteDecimal(w, "ask", q.Ask);
            WriteDecimal(w, "last", q.Last);
            WriteLong(w, "bidQty", q.BidQty);
            WriteLong(w, "askQty", q.AskQty);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BarPayload(BarCompletedEvent b)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WritePropertyName("info");
            if (b.Info is { } i)
            {
                w.WriteStartObject();
                w.WriteNumber("startTicks", i.StartAvailableAt.UtcTicks);
                w.WriteNumber("endTicks", i.EndAvailableAt.UtcTicks);
                w.WriteNumber("volume", i.Volume);
                w.WriteNumber("tradeUpdates", i.TradeUpdates);
                w.WriteBoolean("isValid", i.IsValid);
                w.WriteNumber("consecutiveValidBars", i.ConsecutiveValidBars);
                w.WriteNumber("requiredValidBars", i.RequiredValidBars);
                w.WriteBoolean("signalStarted", i.SignalStarted);
                w.WriteBoolean("ready", i.Ready);
                w.WriteString("readinessSpec", i.ReadinessSpec);
                w.WriteEndObject();
            }
            else
            {
                w.WriteNullValue();
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteDecimal(Utf8JsonWriter w, string name, decimal? value)
    {
        if (value is { } v)
        {
            w.WriteString(name, DecimalText.Format(v));
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static void WriteLong(Utf8JsonWriter w, string name, long? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static decimal? Dec(JsonElement p, string name) => p.GetProperty(name) is { ValueKind: JsonValueKind.String } e ? DecimalText.Parse(e.GetString()!) : null;

    private static long? Long(JsonElement p, string name) => p.GetProperty(name) is { ValueKind: JsonValueKind.Number } e ? e.GetInt64() : null;
}
