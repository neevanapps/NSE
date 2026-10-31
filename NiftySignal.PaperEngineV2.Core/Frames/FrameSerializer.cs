using System.Globalization;
using System.Text;
using System.Text.Json;
using NiftySignal.PaperEngineV2.Core.Metrics;

namespace NiftySignal.PaperEngineV2.Core.Frames;

/// <summary>
/// Canonical frame payload (<c>pev2-frame/1</c>): a fixed field order, instants as UTC round-trip strings, numbers in the shortest round-trip form, metrics as sorted <c>[handle, window, value]</c> triples
/// (absent metrics are omitted, so "unavailable" survives the round trip as unavailable). <see cref="Read"/> rebuilds through <see cref="MarketStateFrame.Create"/>, so a payload is re-validated, never trusted.
/// </summary>
public static class FrameSerializer
{
    static string Instant(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static string Write(FrameIdentity id, BasketIdentity basket, double[] values, bool[] present, QuoteSnapshot[] quotes, QuoteIssue[] issues, VixAvailability vix)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("schema", id.Schema); w.WriteString("sourceModel", id.SourceModel); w.WriteString("adaptiveModelVersion", id.AdaptiveModelVersion);
            if (id.AtmRuleVersion is null) w.WriteNull("atmRuleVersion"); else w.WriteString("atmRuleVersion", id.AtmRuleVersion);
            w.WriteNumber("sessionId", id.SessionId); w.WriteString("tradeDate", id.TradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); w.WriteNumber("barSeq", id.BarSeq);
            w.WriteString("barStart", Instant(id.BarStartAvailableAt)); w.WriteString("barEnd", Instant(id.BarEndAvailableAt)); w.WriteNumber("lastTickId", id.LastSourceTickId);
            w.WriteNumber("sameInstantOrdinal", id.SameInstantOrdinal); w.WriteNumber("durationSeconds", id.DurationSeconds); w.WriteBoolean("verified", id.Verified);
            if (id.UnverifiedReason is null) w.WriteNull("unverifiedReason"); else w.WriteString("unverifiedReason", id.UnverifiedReason);

            w.WriteStartObject("basket");
            w.WriteString("expiry", basket.Expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); w.WriteNumber("centre", basket.Centre);
            if (basket.Step is { } st) w.WriteNumber("step", st); else w.WriteNull("step");
            w.WriteStartArray("slots");
            foreach (var s in basket.Slots)
            {
                w.WriteStartObject(); w.WriteNumber("offset", s.Offset);
                if (s.Strike is { } k) w.WriteNumber("strike", k); else w.WriteNull("strike");
                w.WriteBoolean("isCall", s.IsCall);
                if (s.Token is null) w.WriteNull("token"); else w.WriteString("token", s.Token);
                w.WriteNumber("moneyness", (int)s.Moneyness); w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("problems"); foreach (var p in basket.Problems) w.WriteStringValue(p); w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartObject("vix");
            if (vix.ObservedAt is { } o) w.WriteString("observedAt", Instant(o)); else w.WriteNull("observedAt");
            if (vix.AgeSeconds is { } a) w.WriteNumber("ageSeconds", a); else w.WriteNull("ageSeconds");
            w.WriteString("status", vix.Status); w.WriteEndObject();

            w.WriteStartArray("quotes");
            foreach (var q in quotes)
            {
                w.WriteStartObject(); w.WriteString("token", q.Token); w.WriteNumber("bid", q.Bid); w.WriteNumber("ask", q.Ask); w.WriteNumber("bidQty", q.BidQty); w.WriteNumber("askQty", q.AskQty);
                w.WriteString("availableAt", Instant(q.AvailableAt)); w.WriteNumber("tickId", q.SourceTickId); w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("quoteIssues");
            foreach (var i in issues) { w.WriteStartObject(); w.WriteString("token", i.Token); w.WriteString("reason", i.Reason); w.WriteEndObject(); }
            w.WriteEndArray();

            w.WriteStartArray("metrics");
            for (var i = 0; i < values.Length; i++)
            {
                if (!present[i]) continue;
                w.WriteStartArray(); w.WriteNumberValue(i / SummaryWindows.Count); w.WriteNumberValue(i % SummaryWindows.Count); w.WriteNumberValue(values[i]); w.WriteEndArray();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string Write(MarketStateFrame frame) => frame.PayloadJson;

    /// <summary>Rebuilds a frame from a payload. Throws <see cref="FrameValidationException"/> for anything malformed or inconsistent; the returned frame's <see cref="MarketStateFrame.PayloadJson"/> equals the input when it was canonical.</summary>
    public static MarketStateFrame Read(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var r = doc.RootElement;
            string Str(JsonElement e, string n) => e.GetProperty(n).GetString() ?? throw new FrameValidationException($"{n} is null");
            string? StrN(JsonElement e, string n) => e.GetProperty(n).ValueKind == JsonValueKind.Null ? null : e.GetProperty(n).GetString();
            DateTimeOffset T(JsonElement e, string n) => DateTimeOffset.Parse(Str(e, n), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var id = new FrameIdentity(Str(r, "schema"), Str(r, "sourceModel"), Str(r, "adaptiveModelVersion"), StrN(r, "atmRuleVersion"), r.GetProperty("sessionId").GetInt64(),
                DateOnly.ParseExact(Str(r, "tradeDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture), r.GetProperty("barSeq").GetInt32(), T(r, "barStart"), T(r, "barEnd"), r.GetProperty("lastTickId").GetInt64(),
                r.GetProperty("sameInstantOrdinal").GetInt32(), r.GetProperty("durationSeconds").GetDouble(), r.GetProperty("verified").GetBoolean(), StrN(r, "unverifiedReason"));
            var b = r.GetProperty("basket");
            var slots = b.GetProperty("slots").EnumerateArray().Select(s => new BasketSlot(s.GetProperty("offset").GetInt32(),
                s.GetProperty("strike").ValueKind == JsonValueKind.Null ? null : s.GetProperty("strike").GetDouble(), s.GetProperty("isCall").GetBoolean(), StrN(s, "token"), (Moneyness)s.GetProperty("moneyness").GetInt32())).ToList();
            var basket = new BasketIdentity(DateOnly.ParseExact(Str(b, "expiry"), "yyyy-MM-dd", CultureInfo.InvariantCulture), b.GetProperty("centre").GetDouble(),
                b.GetProperty("step").ValueKind == JsonValueKind.Null ? null : b.GetProperty("step").GetDouble(), slots, b.GetProperty("problems").EnumerateArray().Select(p => p.GetString()!).ToList());
            var v = r.GetProperty("vix");
            var vix = new VixAvailability(v.GetProperty("observedAt").ValueKind == JsonValueKind.Null ? null : T(v, "observedAt"),
                v.GetProperty("ageSeconds").ValueKind == JsonValueKind.Null ? null : v.GetProperty("ageSeconds").GetDouble(), Str(v, "status"));
            var quotes = r.GetProperty("quotes").EnumerateArray().Select(q => new QuoteSnapshot(Str(q, "token"), q.GetProperty("bid").GetDouble(), q.GetProperty("ask").GetDouble(),
                q.GetProperty("bidQty").GetInt64(), q.GetProperty("askQty").GetInt64(), T(q, "availableAt"), q.GetProperty("tickId").GetInt64())).ToList();
            var issues = r.GetProperty("quoteIssues").EnumerateArray().Select(i => new QuoteIssue(Str(i, "token"), Str(i, "reason"))).ToList();
            var metrics = r.GetProperty("metrics").EnumerateArray().Select(m => new MetricValue(m[0].GetInt32(), (SummaryWindowId)m[1].GetInt32(), m[2].GetDouble())).ToList();
            return MarketStateFrame.Create(id, basket, metrics, quotes, issues, vix);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new FrameValidationException($"malformed frame payload: {ex.Message}");
        }
    }
}
