using NiftySignal.Domain.Enums;
using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// Backtest-only risk-rule sweep (2026-09-21, docs/VOLUME_BAR_FINDINGS.md) -- unit coverage for
/// <see cref="RiskRuleState"/> (the TP1 partial-book gate and the daily-loss-cap entry gate, shared
/// by <see cref="TradeSimulator.SimulateDayAsync"/> and <see cref="TradeSimulator.SimulateCrossoverDayAsync"/>)
/// and for <see cref="VolumeBarTrade"/>'s own blended-P&amp;L formula once a partial has booked. Pure
/// in-memory logic, no DB -- these constructors/properties have no I/O dependency, so real
/// TradeSimulator-day integration coverage stays via the CLI regression checks documented in
/// docs/VOLUME_BAR_FINDINGS.md rather than duplicated here.
/// </summary>
public class RiskRuleStateTests
{
    [Fact]
    public void ShouldPartialBook_False_WhenTp1NotConfigured()
    {
        var state = new RiskRuleState(tp1ProfitPct: null, tp1Fraction: null, dailyLossCapPct: null);

        Assert.False(state.ShouldPartialBook(alreadyPartiallyBooked: false, entryPrice: 100m, currentPrice: 200m));
    }

    [Fact]
    public void ShouldPartialBook_True_OncePriceClearsTheProfitTrigger()
    {
        // tp1ProfitPct=0.15 (15%), tp1Fraction=0.5 (50%) -- entry 100, needs currentPrice >= 115.
        var state = new RiskRuleState(tp1ProfitPct: 0.15m, tp1Fraction: 0.5m, dailyLossCapPct: null);

        Assert.False(state.ShouldPartialBook(alreadyPartiallyBooked: false, entryPrice: 100m, currentPrice: 114.99m));
        Assert.True(state.ShouldPartialBook(alreadyPartiallyBooked: false, entryPrice: 100m, currentPrice: 115m));
    }

    [Fact]
    public void ShouldPartialBook_False_OnceAlreadyBooked()
    {
        var state = new RiskRuleState(tp1ProfitPct: 0.15m, tp1Fraction: 0.5m, dailyLossCapPct: null);

        Assert.False(state.ShouldPartialBook(alreadyPartiallyBooked: true, entryPrice: 100m, currentPrice: 200m));
    }

    [Fact]
    public void DailyLossCapHit_False_WhenNotConfigured()
    {
        var state = new RiskRuleState(tp1ProfitPct: null, tp1Fraction: null, dailyLossCapPct: null);
        state.RecordRealizedPnl(-100_000m);

        Assert.False(state.DailyLossCapHit);
    }

    [Fact]
    public void DailyLossCapHit_TripsOnceRealizedLossCrossesTheConvertedPointsThreshold()
    {
        // dailyLossCapPct=20 -> 20% of Rs 50,000 / (65 lot size * 2 lots per trade) = 10,000 / 130
        // = 76.923... points. This is RiskRuleState's own documented conversion (see its doc
        // comment) -- re-derived here explicitly rather than hardcoding the constant twice, so the
        // test breaks loudly if the conversion itself ever changes.
        const decimal expectedThreshold = 20m / 100m * 50_000m / (65 * 2);
        var state = new RiskRuleState(tp1ProfitPct: null, tp1Fraction: null, dailyLossCapPct: 20m);

        state.RecordRealizedPnl(-(expectedThreshold - 1m));
        Assert.False(state.DailyLossCapHit);

        state.RecordRealizedPnl(-2m); // crosses the threshold
        Assert.True(state.DailyLossCapHit);
    }

    [Fact]
    public void DailyLossCapHit_AccumulatesAcrossMultipleClosedTrades()
    {
        var state = new RiskRuleState(tp1ProfitPct: null, tp1Fraction: null, dailyLossCapPct: 10m);
        // Threshold = 10% * 50,000 / 130 = 38.46...
        state.RecordRealizedPnl(-20m);
        Assert.False(state.DailyLossCapHit);

        state.RecordRealizedPnl(-20m);
        Assert.True(state.DailyLossCapHit); // cumulative -40 crosses the ~38.46 threshold
    }

    [Fact]
    public void DailyLossCapHit_UnaffectedByRealizedProfit()
    {
        var state = new RiskRuleState(tp1ProfitPct: null, tp1Fraction: null, dailyLossCapPct: 10m);
        state.RecordRealizedPnl(500m); // a big win doesn't move the cap toward tripping
        state.RecordRealizedPnl(-30m);

        Assert.False(state.DailyLossCapHit);
    }

    [Fact]
    public void VolumeBarTrade_NetPnlPoints_IsPlainExitMinusEntry_WithNoPartialBook()
    {
        var now = DateTimeOffset.UtcNow;
        var trade = new VolumeBarTrade(now, EntryPrice: 100m, OptionType.Call, StrikePrice: 23000m,
            now.AddMinutes(5), ExitPrice: 120m, "ScoreInvalidated", EntryScore: 50.0);

        Assert.Equal(20m, trade.NetPnlPoints);
        Assert.Equal(20m, trade.NetPnlPercent);
    }

    [Fact]
    public void VolumeBarTrade_NetPnlPoints_IsFractionWeightedBlend_WithAPartialBook()
    {
        var now = DateTimeOffset.UtcNow;
        // Entry 100, TP1 partial (50%) booked at 115 (+15), final exit (remaining 50%) at 90 (-10).
        // Blended: 0.5*15 + 0.5*(-10) = 7.5 - 5 = 2.5
        var trade = new VolumeBarTrade(now, EntryPrice: 100m, OptionType.Call, StrikePrice: 23000m,
            now.AddMinutes(10), ExitPrice: 90m, "StopLoss", EntryScore: 50.0,
            PartialExitTime: now.AddMinutes(3), PartialExitPrice: 115m, PartialBookedFraction: 0.5m);

        Assert.Equal(2.5m, trade.NetPnlPoints);
    }

    [Fact]
    public void VolumeBarTrade_NetPnlPoints_PartialBookFullyRealizesProfitEvenIfRemainingLegLosesEverything()
    {
        var now = DateTimeOffset.UtcNow;
        // Entry 100, 50% booked at 200 (+100), remaining 50% stopped out back at entry (0 pnl on
        // that leg) -- blended should still show half the locked-in gain, not wipe it out.
        var trade = new VolumeBarTrade(now, EntryPrice: 100m, OptionType.Call, StrikePrice: 23000m,
            now.AddMinutes(10), ExitPrice: 100m, "StopLoss", EntryScore: 50.0,
            PartialExitTime: now.AddMinutes(3), PartialExitPrice: 200m, PartialBookedFraction: 0.5m);

        Assert.Equal(50m, trade.NetPnlPoints); // 0.5*100 + 0.5*0
    }
}
