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

    /// <summary>What the Host's replay/insert writes: one attestation per bar over the stored rows.</summary>
    static async Task AttestAsync(AdaptiveObserverDbContext o)
    {
        await o.SaveChangesAsync();
        foreach (var g in (await o.OptionStrikeBars.AsNoTracking().ToListAsync()).GroupBy(x => x.BarSeq))
            o.StrikeAttestations.Add(new AdaptiveStrikeAttestationRow { SessionId = 1, BarSeq = g.Key, MetricsVersion = OptionStrikeBarMetrics.MetricsVersion, TokenCount = g.Count(), Digests = StrikeAttestation.Format(g), VerifiedAtUtc = DateTimeOffset.UtcNow });
    }

    static async Task Seed(string db, bool markUnverified, bool attest = true)
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

        if (attest) await AttestAsync(o);
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

    [Fact]
    public async Task UnattestedStrikeRows_AreSuppressedForV2_UntilTheHostReplayAttestsThem_ThenCoverageIsFullAgain()
    {
        await Seed("unattested", false, attest: false);                              // e.g. rows from before the attestation rollout, or a Host restart that has not replayed yet
        var before = (await TotalAsync("unattested")).Rows.Single(r => r.Window.Name == "5 Bars");
        Assert.NotEqual(CoverageStatus.Full, before.Coverage.Status);
        Assert.Equal(0, before.Coverage.Included);                                    // withheld, never zero-filled

        await using (var o = Observer("unattested")) { await AttestAsync(o); await o.SaveChangesAsync(); }   // replay verified and attested every bar
        var after = (await TotalAsync("unattested")).Rows.Single(r => r.Window.Name == "5 Bars");
        Assert.Equal(CoverageStatus.Full, after.Coverage.Status);                     // a healthy restart resumes with full coverage
    }

    [Fact]
    public async Task ARowChangedAfterAttestation_IsSuppressedForV2()
    {
        await Seed("changed", false);
        await using (var o = Observer("changed"))
        {
            var row = await o.OptionStrikeBars.FirstAsync(x => x.BarSeq == 4 && x.Token == "CE26000");
            row.StrictBuy += 3;
            await o.SaveChangesAsync();
        }

        var row5 = (await TotalAsync("changed")).Rows.Single(r => r.Window.Name == "5 Bars");
        Assert.NotEqual(CoverageStatus.Full, row5.Coverage.Status);
        Assert.Contains("CE26000", row5.Coverage.MissingTokens);
    }
}
