using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Reporting;
using NiftySignal.Trading.Core.Strategies;

namespace NiftySignal.Tests.Trading.Core;

/// <summary>Builders for engine outputs in tests that need a record but not a whole engine run.</summary>
internal static class OutputFactory
{
    public static PositionOpened Opened(DateTimeOffset at, VariantKey key, string positionId = "pos_1", string opportunityId = "opp_1", string token = "CE23900", OptionSide side = OptionSide.Call)
        => new(at, key, positionId, opportunityId, token, 650, 125m, side, side == OptionSide.Call ? StrategyDirection.Long : StrategyDirection.Short, "NIFTY26SEP23900CE", 23900m, new DateOnly(2026, 9, 29), 65,
            "session:2026-09-22", 7, at.AddSeconds(-1), 124m, 125m, at.AddSeconds(-1), 112.5m, 137.5m, at.AddMinutes(5));

    public static PositionClosed Closed(DateTimeOffset at, VariantKey key, PositionExcursion excursion, string positionId = "pos_1", string token = "CE23900")
        => new(at, key, positionId, token, 650, 125m, 4_875.00m, 310.25m, 4_564.75m, excursion, at.AddMinutes(-3), ExitReason.StrategyExit, 132.50m, 132m, Tp1State.Done, 325, "session:2026-09-22", 31);

    public static PositionExcursion Excursion() => new(15.00m, -3.50m, 12.00m, -2.80m, 140m, 121.5m, 42, 1, ExcursionCoverage.HasGaps, null, null, 10m, -1m);
}
