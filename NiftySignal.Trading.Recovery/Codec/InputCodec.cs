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

                AdaptiveStrategyFrame? frame = null;
                if (p.TryGetProperty("frame", out var f) && f.ValueKind == JsonValueKind.Object)
                {
                    frame = new AdaptiveStrategyFrame(
                        f.GetProperty("session").GetString()!,
                        DateOnly.ParseExact(f.GetProperty("date").GetString()!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                        r.SourceId,
                        new DateTimeOffset(f.GetProperty("startTicks").GetInt64(), TimeSpan.Zero),
                        r.At,
                        f.GetProperty("ready").GetBoolean(),
                        f.GetProperty("isValid").GetBoolean(),
                        f.GetProperty("consecutiveValidBars").GetInt32(),
                        f.GetProperty("model").GetString()!,
                        f.GetProperty("readinessSpec").GetString()!,
                        f.GetProperty("microVersion").GetString()!,
                        OptDouble(f, "microDevTw"),
                        OptDouble(f, "microDevUsableS"),
                        f.GetProperty("peStrictDelta") is { ValueKind: JsonValueKind.Number } pe ? pe.GetInt64() : null,
                        OptDouble(f, "bandCenter"),
                        f.GetProperty("bandExpiry") is { ValueKind: JsonValueKind.String } be ? DateOnly.ParseExact(be.GetString()!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null,
                        f.GetProperty("bandUnavailable") is { ValueKind: JsonValueKind.String } bu ? bu.GetString() : null,
                        f.TryGetProperty("candidateStrikes", out var bs) && bs.ValueKind == JsonValueKind.String ? bs.GetString() : null);
                }

                return new BarCompletedEvent(r.At, r.SourceId, r.Token, info, frame);
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

            if (b.Frame is { } f)
            {
                // written only when present: a bar without a frame keeps its pre-frame payload text, so existing journals still verify
                w.WritePropertyName("frame");
                w.WriteStartObject();
                w.WriteString("session", f.AdaptiveSessionId);
                w.WriteString("date", f.TradeDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                w.WriteNumber("startTicks", f.StartAvailableAt.UtcTicks);
                w.WriteBoolean("ready", f.Ready);
                w.WriteBoolean("isValid", f.IsValid);
                w.WriteNumber("consecutiveValidBars", f.ConsecutiveValidBars);
                w.WriteString("model", f.ModelVersion);
                w.WriteString("readinessSpec", f.ReadinessSpec);
                w.WriteString("microVersion", f.MicrostructureVersion);
                WriteDouble(w, "microDevTw", f.MicroDevTimeWeighted);
                WriteDouble(w, "microDevUsableS", f.MicroDevUsableSeconds);
                WriteLong(w, "peStrictDelta", f.PeBandContractStrictDelta);
                WriteDouble(w, "bandCenter", f.BandCenterStrike);
                if (f.BandExpiry is { } expiry)
                {
                    w.WriteString("bandExpiry", expiry.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    w.WriteNull("bandExpiry");
                }

                if (f.BandUnavailableReason is { } reason)
                {
                    w.WriteString("bandUnavailable", reason);
                }
                else
                {
                    w.WriteNull("bandUnavailable");
                }

                if (f.CandidateStrikes is { } strikes)
                {
                    w.WriteString("candidateStrikes", strikes);
                }
                else
                {
                    w.WriteNull("candidateStrikes");
                }

                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteDouble(Utf8JsonWriter w, string name, double? value)
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

    private static double? OptDouble(JsonElement p, string name) => p.GetProperty(name) is { ValueKind: JsonValueKind.Number } e ? e.GetDouble() : null;

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
