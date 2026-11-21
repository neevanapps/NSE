using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;
using NiftySignal.MarketSummary;
using NiftySignal.PaperEngineV2.Frames;
using NiftySignal.PaperEngineV2.Runtime;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>A4 (awaiting owner confirmation): a per-strike bar durably marked UNVERIFIED is a missing observation in the V2 frame inputs - coverage shrinks, nothing is zero-filled.</summary>
public sealed class UnverifiedStrikeFrameTests
{
    static AdaptiveObserverDbContext Observer(string name) => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(name).Options);

    static AdaptiveOptionStrikeBarRow Row(int seq, double strike, bool call) => new()
    {
        SessionId = 1, BarSeq = seq, MetricsVersion = OptionStrikeBarMetrics.MetricsVersion, EndAvailableAtUtc = FrameFixtures.End(seq), Expiry = FrameFixtures.Expiry, Strike = strike,
        Side = call ? OptionType.Call : OptionType.Put, Token = $"{(call ? "CE" : "PE")}{strike:0}", TradingSymbol = "S", TotalQuantity = 100, StrictDelta = 10,
    };

    static async Task<GridSummary> TotalAsync(string db)
    {
        var session = new V2SessionInfo(1, FrameFixtures.Day, "adaptive-fixed13650-v1", "spot-atm-v1", FrameFixtures.Expiry, FrameFixtures.Listed(), new Dictionary<string, int>());
        var bar = new BarInput(1, FrameFixtures.Day, "adaptive-fixed13650-v1", "spot-atm-v1", 5, FrameFixtures.End(4), FrameFixtures.End(5), 1005, 60, true, null);
        var source = new DbFrameInputSource(() => Observer(db), () => new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase("src-" + db).Options));
        var (inputs, problem) = await source.LoadAsync(new V2BarNotice(session, bar, 26_000, []), default);
        Assert.Null(problem);
        return inputs!.Grids.Single(g => g.Kind == GridKind.Total);
    }

    static async Task Seed(string db, bool markUnverified)
    {
        await using var o = Observer(db);
        for (var seq = 1; seq <= 5; seq++)
        {
            o.FutureBars.Add(new AdaptiveFutureBarRow { SessionId = 1, BarSeq = seq, StartAvailableAtUtc = FrameFixtures.End(seq - 1), EndAvailableAtUtc = FrameFixtures.End(seq) });
            foreach (var strike in new[] { 25_900d, 25_950d, 26_000d, 26_050d, 26_100d })
            {
                o.OptionStrikeBars.Add(Row(seq, strike, true));
                o.OptionStrikeBars.Add(Row(seq, strike, false));
            }
        }

        if (markUnverified)
        {
            o.ProjectionHealth.Add(new AdaptiveProjectionHealthRow
            {
                SessionId = 1, Component = AdaptiveProjectionComponents.OptionStrikeBars, Version = OptionStrikeBarMetrics.MetricsVersion, BarSeq = 4,
                Detail = UnverifiedStrikeBars.FormatDetail(["CE26000"], "test"), DetectedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        await o.SaveChangesAsync();
    }

    [Fact]
    public async Task UnverifiedStrikeBar_IsAMissingObservation_CoverageReduced_NeverZeroFilled()
    {
        await Seed("clean", false);
        var clean = (await TotalAsync("clean")).Rows.Single(r => r.Window.Name == "5 Bars");
        Assert.Equal(CoverageStatus.Full, clean.Coverage.Status);

        await Seed("marked", true);
        var marked = (await TotalAsync("marked")).Rows.Single(r => r.Window.Name == "5 Bars");
        Assert.NotEqual(CoverageStatus.Full, marked.Coverage.Status);              // CE26000 bar 4 is missing: the full-coverage requirement is not met
        Assert.Contains("CE26000", marked.Coverage.MissingTokens);
        Assert.True(marked.Coverage.Included < marked.Coverage.Expected || marked.Coverage.MissingTokens.Count > 0);
    }
}
