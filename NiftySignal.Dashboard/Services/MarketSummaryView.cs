using NiftySignal.MarketSummary;

namespace NiftySignal.Dashboard.Services;

/// <summary>Everything the Market Summary section renders for one pinned completed bar. <see cref="UnavailableReason"/> set = nothing to show (the section says why instead of rendering zeros).</summary>
public sealed record MarketSummaryView(
    int EndBarSeq, Basket? Basket, IReadOnlyList<FuturesWindowSummary> Futures, IReadOnlyList<GridSummary> Grids, string? UnavailableReason,
    IReadOnlyList<WindowInstant>? Instants = null, IReadOnlyList<VixWindowSummary>? Vix = null, string? VixUnavailableReason = null)
{
    public static MarketSummaryView Unavailable(int endBarSeq, string reason) => new(endBarSeq, null, [], [], reason);
}
