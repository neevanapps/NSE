namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-24, first frozen-relationship trade simulation. No existing convention in this
/// codebase covers STT/GST -- <c>PaperTradeSimulator</c>'s own doc comment explicitly says "the
/// plan mentions an STT/charges approximation but never gives a rate to apply, so nothing is
/// fabricated here." This file documents and applies the ASSUMED treatment used for this
/// experiment (stated here, not silently chosen):
/// <list type="bullet">
/// <item><b>STT</b>: India's current statutory rate for options (post-2023 Finance Act
/// amendment) -- 0.0625% of premium value, charged ONLY on the SELL leg. For a long option
/// (buy to enter, sell to exit), that means STT applies on the EXIT fill's gross value only,
/// never on entry.</item>
/// <item><b>GST</b>: 18%, applied to brokerage (and any other chargeable fee modeled) -- never
/// to premium or STT itself. This experiment's own brokerage assumption is ₹0 (Flat Trade), so
/// GST evaluates to ₹0 here unless a nonzero brokerage/other-fee is supplied.</item>
/// <item><b>Other</b>: exchange transaction charges, SEBI turnover fees, and stamp duty are
/// explicitly NOT modeled in this first simulation -- a documented limitation, not a fabricated
/// number. Always ₹0.</item>
/// </list>
/// </summary>
public static class TransactionCostCalculator
{
    public const decimal SttRateOnSellPremium = 0.000625m;
    public const decimal GstRate = 0.18m;

    public readonly record struct CostBreakdown(decimal Stt, decimal Gst, decimal Other, decimal Total);

    /// <param name="exitGrossValue">Quantity * exit fill price -- the sell leg's own premium value; STT applies only here.</param>
    /// <param name="brokerage">Total brokerage already charged across both legs (₹0 under this experiment's own assumption).</param>
    public static CostBreakdown Compute(decimal exitGrossValue, decimal brokerage)
    {
        var stt = Math.Round(Math.Abs(exitGrossValue) * SttRateOnSellPremium, 2);
        var gst = Math.Round(brokerage * GstRate, 2);
        const decimal other = 0m;
        return new CostBreakdown(stt, gst, other, stt + gst + other);
    }
}
