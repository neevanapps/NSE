namespace NiftySignal.Domain.ValueObjects;

/// <summary>
/// Top-5 bid/ask levels. Flattened (not a List&lt;DepthLevel&gt;) so it maps as scalar
/// columns on the owning table via EF Core's owned-type support, rather than needing a
/// join for something that's always exactly five levels a side (plan section 3.3: depth
/// is needed for both the depth-imbalance feature and realistic fill simulation).
/// </summary>
public sealed record MarketDepth(
    decimal Bid1Price, long Bid1Qty,
    decimal Bid2Price, long Bid2Qty,
    decimal Bid3Price, long Bid3Qty,
    decimal Bid4Price, long Bid4Qty,
    decimal Bid5Price, long Bid5Qty,
    decimal Ask1Price, long Ask1Qty,
    decimal Ask2Price, long Ask2Qty,
    decimal Ask3Price, long Ask3Qty,
    decimal Ask4Price, long Ask4Qty,
    decimal Ask5Price, long Ask5Qty)
{
    public long TotalBidQty => Bid1Qty + Bid2Qty + Bid3Qty + Bid4Qty + Bid5Qty;

    public long TotalAskQty => Ask1Qty + Ask2Qty + Ask3Qty + Ask4Qty + Ask5Qty;
}
