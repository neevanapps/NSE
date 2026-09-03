using System.Text.Json.Serialization;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;

namespace NiftySignal.Ingestion.FlatTrade;

/// <summary>
/// One raw feed message. Every field is nullable and string-typed to match the wire
/// format (Noren-family brokers send numbers as JSON strings) and, just as importantly,
/// so a field genuinely absent from an incremental "tf"/"df" update deserializes to null
/// rather than to a misleading default -- that null is what tells
/// <see cref="FlatTradeFeedState.ApplyDelta"/> to leave the existing value alone instead
/// of overwriting it with zero.
/// </summary>
public sealed record RawFeedMessage(
    [property: JsonPropertyName("t")] string? Type,
    [property: JsonPropertyName("e")] string? Exchange,
    [property: JsonPropertyName("tk")] string? Token,
    [property: JsonPropertyName("lp")] string? LastPrice,
    [property: JsonPropertyName("v")] string? Volume,
    [property: JsonPropertyName("oi")] string? OpenInterest,
    [property: JsonPropertyName("ft")] string? FeedTimeEpochSeconds,
    [property: JsonPropertyName("bp1")] string? Bid1Price, [property: JsonPropertyName("bq1")] string? Bid1Qty,
    [property: JsonPropertyName("bp2")] string? Bid2Price, [property: JsonPropertyName("bq2")] string? Bid2Qty,
    [property: JsonPropertyName("bp3")] string? Bid3Price, [property: JsonPropertyName("bq3")] string? Bid3Qty,
    [property: JsonPropertyName("bp4")] string? Bid4Price, [property: JsonPropertyName("bq4")] string? Bid4Qty,
    [property: JsonPropertyName("bp5")] string? Bid5Price, [property: JsonPropertyName("bq5")] string? Bid5Qty,
    [property: JsonPropertyName("sp1")] string? Ask1Price, [property: JsonPropertyName("sq1")] string? Ask1Qty,
    [property: JsonPropertyName("sp2")] string? Ask2Price, [property: JsonPropertyName("sq2")] string? Ask2Qty,
    [property: JsonPropertyName("sp3")] string? Ask3Price, [property: JsonPropertyName("sq3")] string? Ask3Qty,
    [property: JsonPropertyName("sp4")] string? Ask4Price, [property: JsonPropertyName("sq4")] string? Ask4Qty,
    [property: JsonPropertyName("sp5")] string? Ask5Price, [property: JsonPropertyName("sq5")] string? Ask5Qty);

/// <summary>
/// Per-token running state, merged field-by-field from a sequence of raw feed messages.
/// Needed because "tf"/"df" updates only carry the fields that changed (plan section 4.3:
/// "an acknowledgement stage and an update-only stage") -- a naive one-message-per-Tick
/// mapping would silently zero out every field the update didn't mention.
/// </summary>
public sealed class FlatTradeFeedState(Exchange exchange, string token)
{
    public Exchange Exchange { get; } = exchange;

    public string Token { get; } = token;

    public decimal? LastPrice { get; private set; }

    public long Volume { get; private set; }

    public long? OpenInterest { get; private set; }

    public DateTimeOffset? ExchangeTimestamp { get; private set; }

    decimal?[] _bidPrices = new decimal?[5];
    long?[] _bidQtys = new long?[5];
    decimal?[] _askPrices = new decimal?[5];
    long?[] _askQtys = new long?[5];

    public void ApplyDelta(RawFeedMessage msg)
    {
        if (msg.LastPrice is not null) LastPrice = ParseDecimal(msg.LastPrice);
        if (msg.Volume is not null) Volume = ParseLong(msg.Volume) ?? Volume;
        if (msg.OpenInterest is not null) OpenInterest = ParseLong(msg.OpenInterest);
        if (msg.FeedTimeEpochSeconds is not null && long.TryParse(msg.FeedTimeEpochSeconds, out var epoch))
        {
            ExchangeTimestamp = DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        ApplyLevel(0, msg.Bid1Price, msg.Bid1Qty, msg.Ask1Price, msg.Ask1Qty);
        ApplyLevel(1, msg.Bid2Price, msg.Bid2Qty, msg.Ask2Price, msg.Ask2Qty);
        ApplyLevel(2, msg.Bid3Price, msg.Bid3Qty, msg.Ask3Price, msg.Ask3Qty);
        ApplyLevel(3, msg.Bid4Price, msg.Bid4Qty, msg.Ask4Price, msg.Ask4Qty);
        ApplyLevel(4, msg.Bid5Price, msg.Bid5Qty, msg.Ask5Price, msg.Ask5Qty);
    }

    void ApplyLevel(int i, string? bidPrice, string? bidQty, string? askPrice, string? askQty)
    {
        if (bidPrice is not null) _bidPrices[i] = ParseDecimal(bidPrice);
        if (bidQty is not null) _bidQtys[i] = ParseLong(bidQty);
        if (askPrice is not null) _askPrices[i] = ParseDecimal(askPrice);
        if (askQty is not null) _askQtys[i] = ParseLong(askQty);
    }

    /// <summary>Null until at least a last price has been seen -- an empty shell isn't a tick yet.</summary>
    public Tick? ToTick(DateTimeOffset receivedAt)
    {
        if (LastPrice is null)
        {
            return null;
        }

        return new Tick
        {
            Token = Token,
            Exchange = Exchange,
            ExchangeTimestamp = ExchangeTimestamp ?? receivedAt,
            ReceivedAt = receivedAt,
            LastPrice = LastPrice.Value,
            Volume = Volume,
            OpenInterest = OpenInterest,
            Depth = HasFullDepth() ? BuildDepth() : null,
        };
    }

    bool HasFullDepth() =>
        Array.TrueForAll(_bidPrices, v => v.HasValue) && Array.TrueForAll(_askPrices, v => v.HasValue);

    MarketDepth BuildDepth() => new(
        _bidPrices[0]!.Value, _bidQtys[0] ?? 0,
        _bidPrices[1]!.Value, _bidQtys[1] ?? 0,
        _bidPrices[2]!.Value, _bidQtys[2] ?? 0,
        _bidPrices[3]!.Value, _bidQtys[3] ?? 0,
        _bidPrices[4]!.Value, _bidQtys[4] ?? 0,
        _askPrices[0]!.Value, _askQtys[0] ?? 0,
        _askPrices[1]!.Value, _askQtys[1] ?? 0,
        _askPrices[2]!.Value, _askQtys[2] ?? 0,
        _askPrices[3]!.Value, _askQtys[3] ?? 0,
        _askPrices[4]!.Value, _askQtys[4] ?? 0);

    static decimal? ParseDecimal(string s) => decimal.TryParse(s, out var v) ? v : null;

    static long? ParseLong(string s) => long.TryParse(s, out var v) ? v : decimal.TryParse(s, out var d) ? (long)d : null;
}
