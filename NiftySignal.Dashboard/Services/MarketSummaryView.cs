using NiftySignal.MarketSummary;

namespace NiftySignal.Dashboard.Services;

/// <summary>Everything the Market Summary section renders for one pinned completed bar. <see cref="UnavailableReason"/> set = nothing to show (the section says why instead of rendering zeros).</summary>
public sealed record MarketSummaryView(
    int EndBarSeq, Basket? Basket, IReadOnlyList<FuturesWindowSummary> Futures, IReadOnlyList<GridSummary> Grids, string? UnavailableReason,
    IReadOnlyList<WindowInstant>? Instants = null, IReadOnlyList<VixWindowSummary>? Vix = null, string? VixUnavailableReason = null)
{
    /// <summary>ITM-shifted (Lower: close ATM - 1 listed step) and OTM-shifted (Upper: + 1 step) TOTAL baskets and grids around the SAME close ATM as the main TOTAL; null on views built without them (capture, V2, older callers).</summary>
    public Basket? LowerBasket { get; init; }

    public GridSummary? Lower { get; init; }

    public Basket? UpperBasket { get; init; }

    public GridSummary? Upper { get; init; }

    /// <summary>The ATM at the pinned completed bar's CLOSE that all three baskets are built around, and where it came from (see AdaptiveObserverDataService.CloseAtmResult.Source).</summary>
    public double? CloseAtmStrike { get; init; }

    public string? CloseAtmSource { get; init; }

    /// <summary>A4: per-strike bars withheld because recovery found them disagreeing with the reconstruction ("symbol bar N"), or a pending-migration note. Rendered as a visible UNVERIFIED marker.</summary>
    public IReadOnlyList<string> Unverified { get; init; } = [];

    /// <summary>Top-5 depth (flag-gated sidecars): Futures Notional Depth Imbalance L5 per window, and the Notional Depth L5 Direction of the three Total Options grids (main, Lower, Upper). Null when no depth data was loaded at all.</summary>
    public IReadOnlyList<FuturesDepthWindow>? FuturesDepth { get; init; }

    public IReadOnlyList<DepthDirectionWindow>? TotalDepth { get; init; }

    public IReadOnlyList<DepthDirectionWindow>? LowerDepth { get; init; }

    public IReadOnlyList<DepthDirectionWindow>? UpperDepth { get; init; }

    /// <summary>Why option depth is unavailable / partial (no attestation yet, table pending, marker): shown next to the Depth columns.</summary>
    public string? DepthNote { get; init; }

    public static MarketSummaryView Unavailable(int endBarSeq, string reason) => new(endBarSeq, null, [], [], reason);
}
