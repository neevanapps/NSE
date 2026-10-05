namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// Session-long per-token cumulative flow state. Every subscribed option token is processed even
/// when it is outside the current ATM±2 band, so later band entry never starts from a false
/// cumulative-volume/quote baseline.
/// </summary>
public sealed class AdaptiveInstrumentFlowAccumulator
{
    sealed class State
    {
        public AdaptiveTradeFlowEnricher Enricher { get; } = new();
        public DateTimeOffset? LastAvailableAt;
        public double? LastPrice;
        public double? Bid;
        public double? Ask;
        public long? OpenInterest;
        public long StrictBuy;
        public long StrictSell;
        public long StrictUnknown;
        public long EnrichedBuy;
        public long EnrichedSell;
        public long EnrichedUnknown;
        public double StrictBuyNotional;
        public double StrictSellNotional;
        public double StrictUnknownNotional;
        public double EnrichedBuyNotional;
        public double EnrichedSellNotional;
        public double EnrichedUnknownNotional;
        public long TradeUpdates;
    }

    readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

    public EnrichedObserverTick Process(string token, CleanObserverTick tick)
    {
        if (!_states.TryGetValue(token, out var state))
        {
            state = new State();
            _states[token] = state;
        }

        var e = state.Enricher.Process(tick);
        state.LastAvailableAt = tick.AvailableAt;
        state.LastPrice = tick.Last;
        state.Bid = tick.HasTwoSidedQuote ? tick.Bid : null;
        state.Ask = tick.HasTwoSidedQuote ? tick.Ask : null;
        state.OpenInterest = tick.OpenInterest;

        if (e.TradeVolume > 0)
        {
            state.TradeUpdates++;
            var notional = tick.Last * e.TradeVolume;
            Add(e.StrictSide, e.TradeVolume, notional,
                ref state.StrictBuy, ref state.StrictSell, ref state.StrictUnknown,
                ref state.StrictBuyNotional, ref state.StrictSellNotional, ref state.StrictUnknownNotional);
            Add(e.EnrichedSide, e.TradeVolume, notional,
                ref state.EnrichedBuy, ref state.EnrichedSell, ref state.EnrichedUnknown,
                ref state.EnrichedBuyNotional, ref state.EnrichedSellNotional, ref state.EnrichedUnknownNotional);
        }

        return e;
    }

    public InstrumentFlowSnapshot Snapshot(string token)
    {
        if (!_states.TryGetValue(token, out var s))
        {
            return new InstrumentFlowSnapshot(token, null, null, null, null, null,
                0,0,0,0,0,0,0d,0d,0d,0d,0d,0d,0);
        }

        return new InstrumentFlowSnapshot(
            token, s.LastAvailableAt, s.LastPrice, s.Bid, s.Ask, s.OpenInterest,
            s.StrictBuy, s.StrictSell, s.StrictUnknown,
            s.EnrichedBuy, s.EnrichedSell, s.EnrichedUnknown,
            s.StrictBuyNotional, s.StrictSellNotional, s.StrictUnknownNotional,
            s.EnrichedBuyNotional, s.EnrichedSellNotional, s.EnrichedUnknownNotional,
            s.TradeUpdates);
    }

    static void Add(
        ObserverTradeSide side,
        long quantity,
        double notional,
        ref long buyQty,
        ref long sellQty,
        ref long unknownQty,
        ref double buyNotional,
        ref double sellNotional,
        ref double unknownNotional)
    {
        switch (side)
        {
            case ObserverTradeSide.Buy:
                buyQty += quantity;
                buyNotional += notional;
                break;
            case ObserverTradeSide.Sell:
                sellQty += quantity;
                sellNotional += notional;
                break;
            default:
                unknownQty += quantity;
                unknownNotional += notional;
                break;
        }
    }
}
