using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Ingestion.Upstox.Protocol;

namespace NiftySignal.Ingestion.Upstox;

/// <summary>Maps the provider's complete feed snapshots without treating last-trade time as quote time.</summary>
public sealed class UpstoxFeedMapper
{
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public IReadOnlyList<Tick> Map(FeedResponse frame, DateTimeOffset receivedAt)
    {
        if (frame.Type == Protocol.Type.MarketInfo) return [];
        if (frame.CurrentTs <= 0) throw new InvalidOperationException("Upstox frame has no message timestamp.");
        var messageTime = DateTimeOffset.FromUnixTimeMilliseconds(frame.CurrentTs);
        var result = new List<Tick>();
        foreach (var item in frame.Feeds.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var market = item.Value.FullFeed?.MarketFF;
            var index = item.Value.FullFeed?.IndexFF;
            var ltpc = market?.Ltpc ?? index?.Ltpc;
            if (ltpc is null) throw new InvalidOperationException("Expected full Upstox feed; LTPC-only/Greeks subscriptions are insufficient.");
            if (!double.IsFinite(ltpc.Ltp) || ltpc.Ltp <= 0) continue;
            if (market is not null && (market.Vtt < 0 || !double.IsFinite(market.Oi) || market.Oi < 0 || market.Oi != Math.Truncate(market.Oi)))
                throw new InvalidOperationException("Invalid Upstox volume/OI.");
            MarketDepth? depth = null;
            var q = market?.MarketLevel?.BidAskQuote;
            if (q is { Count: >= 5 } && q.Take(5).All(x => double.IsFinite(x.BidP) && double.IsFinite(x.AskP) && x.BidP >= 0 && x.AskP >= 0 && x.BidQ >= 0 && x.AskQ >= 0))
            {
                depth = new MarketDepth(
                    (decimal)q[0].BidP,q[0].BidQ,(decimal)q[1].BidP,q[1].BidQ,(decimal)q[2].BidP,q[2].BidQ,(decimal)q[3].BidP,q[3].BidQ,(decimal)q[4].BidP,q[4].BidQ,
                    (decimal)q[0].AskP,q[0].AskQ,(decimal)q[1].AskP,q[1].AskQ,(decimal)q[2].AskP,q[2].AskQ,(decimal)q[3].AskP,q[3].AskQ,(decimal)q[4].AskP,q[4].AskQ);
            }
            var exchange = item.Key.Split('|')[0] switch
            {
                "NSE_FO" => Exchange.Nfo, "BSE_FO" => Exchange.Bfo, "NSE_INDEX" => Exchange.Nse, "BSE_INDEX" => Exchange.Bse,
                _ => throw new InvalidOperationException("Unrecognized tracked Upstox segment."),
            };
            result.Add(new Tick
            {
                Provider = MarketDataProvider.Upstox, Token = UpstoxInstrumentMasterProvider.InternalToken(item.Key), Exchange = exchange,
                ExchangeTimestamp = messageTime, LastTradeTimestamp = ltpc.Ltt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ltpc.Ltt) : null,
                ReceivedAt = receivedAt, LastPrice = (decimal)ltpc.Ltp, Volume = market?.Vtt ?? 0,
                OpenInterest = market is null ? null : checked((long)market.Oi), Depth = depth,
                IsSnapshot = !_seen.Add(item.Key) ? frame.Type == Protocol.Type.InitialFeed : true,
            });
        }
        return result;
    }
}
