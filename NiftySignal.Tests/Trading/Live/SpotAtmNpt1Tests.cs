using NiftySignal.AdaptiveObserver;
using NiftySignal.Domain.Enums;
using NiftySignal.Npt1M1;
using NiftySignal.Pricing;
using NiftySignal.Tests.Trading.Adapters;
using NiftySignal.Trading.Adapters;

namespace NiftySignal.Tests.Trading.Live;

/// <summary>npt1-m1-v2: the frozen spot token, the spot ATM contract in the M1 freeze and fingerprint, and spot rows through the translator (they feed the engine, they are not quotes).</summary>
public class SpotAtmNpt1Tests
{
    private static readonly DateOnly Day = new(2026, 10, 12);
    private static readonly DateOnly Weekly = Day.AddDays(3);
    private const string Fut = "FUT1";
    private const string SpotTok = "NIFTYSPOT";

    private static DateTimeOffset At(int h, int m, double s = 0) => M1Time.IstTime(Day, h, m).AddSeconds(s);

    private static List<ObserverOptionInstrument> Chain()
    {
        var list = new List<ObserverOptionInstrument>();
        for (var strike = 23800d; strike <= 24200d; strike += 50)
        {
            foreach (var side in new[] { OptionType.Call, OptionType.Put })
            {
                list.Add(new((side == OptionType.Call ? "C" : "P") + strike, "OPT" + strike, side, strike, Weekly, 75));
            }
        }

        return list;
    }

    private static SourceTick OptionRow(ref long id, ObserverOptionInstrument i, DateTimeOffset at)
    {
        var p = AdaptiveResearchPricing.Price(i.OptionType, 24000, i.Strike, TimeToExpiry.YearsUntilExpiry(Weekly, at), M1SessionFreeze.RiskFreeRate, .2);
        var last = Math.Round((decimal)p, 2);
        return new SourceTick(i.Token, id++, at, at, last, last - 0.05m, last + 0.05m, 100, 100, 1000, 1000);
    }

    /// <summary>A fully covered 09:15-09:30 session: a future tick every 20 s with growing volume, option quotes at 09:15:05 and 09:29:58, and (optionally) a spot tick at <paramref name="spotAt"/>.</summary>
    private static List<SourceTick> Rows(string? spotToken, DateTimeOffset? spotAt, decimal spotPrice = 24030m)
    {
        var rows = new List<SourceTick>();
        long id = 1;
        var volume = 0L;
        for (var s = 0; s <= 15 * 60 + 40; s += 20)
        {
            var at = At(9, 15, s);
            volume += 500;
            rows.Add(new SourceTick(Fut, id++, at, at, 24000m, 23999.5m, 24000.5m, 100, 100, volume, 5000));
        }

        foreach (var when in new[] { At(9, 15, 5), At(9, 29, 58) })
        {
            foreach (var i in Chain())
            {
                rows.Add(OptionRow(ref id, i, when));
            }
        }

        if (spotToken is not null && spotAt is { } sa)
        {
            rows.Add(new SourceTick(spotToken, id++, sa, sa, spotPrice, 0m, 0m, 0, 0, 0, null));
        }

        return rows.OrderBy(r => r.ReceivedAt).ThenBy(r => r.Id).ToList();
    }

    private static M1Freeze Build(IEnumerable<SourceTick> rows, string? spotToken)
    {
        var chain = Chain();
        return M1SessionFreeze.Build(Day, Weekly, new FutureInstrument(Fut, "NIFTYFUT", Day.AddDays(14), 75), chain,
            chain.ToDictionary(c => c.Token, _ => 0.05m, StringComparer.Ordinal), rows, Array.Empty<PriorSession>(), spotToken!);
    }

    // ---------------------------------------------------------------- identity / version

    [Fact]
    public void TheProducerVersion_IsV2_AndTheSessionIdCarriesIt_SoV1AndV2CanNeverBeConfused()
    {
        Assert.Equal("npt1-m1-v2", M1Identity.ModelVersion);
        Assert.Equal("npt1-m1-v2:2026-10-12", M1Identity.SessionId(Day));
        Assert.NotEqual("npt1-m1-v1:2026-10-12", M1Identity.SessionId(Day));
    }

    // ---------------------------------------------------------------- the freeze

    [Fact]
    public void TheFreeze_CarriesTheFrozenSpotToken_TheAtmRule_AndASpotCenteredAnchor()
    {
        var freeze = Build(Rows(SpotTok, At(9, 29, 59)), SpotTok);
        Assert.Equal(SpotTok, freeze.Freeze.SpotToken);
        Assert.Equal("spot-atm-v1", freeze.Freeze.AtmRuleVersion);
        Assert.Equal(M1Identity.ModelVersion, freeze.Freeze.ModelVersion);
        Assert.Equal(SpotTok, freeze.Report.SpotToken);
        Assert.Equal("spot-atm-v1", freeze.Report.AtmRuleVersion);
        Assert.Equal(24050d, freeze.Freeze.ResidualAnchor!.CenterStrike);             // spot 24030 is nearer 24050 (20) than 24000 (30); the forward (~24000) is not the reference
        Assert.Equal("24050", freeze.Report.AnchorCenterStrike);
    }

    [Fact]
    public void TheFreeze_UsesTheSameInclusiveFiveSecondFreshnessRule_AtTheAnchorTime()
    {
        Assert.NotNull(Build(Rows(SpotTok, At(9, 29, 55)), SpotTok).Freeze.ResidualAnchor);       // exactly 5 s old at 09:30:00: fresh
        Assert.Null(Build(Rows(SpotTok, At(9, 29, 54)), SpotTok).Freeze.ResidualAnchor);          // 6 s old: stale -> no anchor, no fallback to the forward
        Assert.Null(Build(Rows(SpotTok, null), SpotTok).Freeze.ResidualAnchor);                   // no spot tick at all
        Assert.Null(Build(Rows(SpotTok, At(9, 30, 2)), SpotTok).Freeze.ResidualAnchor);          // a spot AFTER 09:30:00 is never used
        Assert.Throws<ArgumentException>(() => Build(Rows(SpotTok, At(9, 29, 59)), null));        // no frozen spot identity: a v2 freeze is refused, never built band-less
        Assert.Throws<ArgumentException>(() => Build(Rows(SpotTok, At(9, 29, 59)), "  "));
    }

    [Fact]
    public void TheFingerprint_IsDeterministic_AndChangesWithTheSpotIdentityAndTheSpotCenter()
    {
        var a = Build(Rows(SpotTok, At(9, 29, 59)), SpotTok).Report.Fingerprint;
        var b = Build(Rows(SpotTok, At(9, 29, 59)), SpotTok).Report.Fingerprint;
        Assert.Equal(a, b);                                                                       // identical session inputs: identical fingerprint
        Assert.Matches("^[0-9a-f]{64}$", a);

        var otherToken = Build(Rows("OTHERSPOT", At(9, 29, 59)), "OTHERSPOT").Report.Fingerprint;
        Assert.NotEqual(a, otherToken);                                                           // a different spot token changes the fingerprint (same price, same everything else)

        var noSpot = Build(Rows(SpotTok, null), SpotTok).Report.Fingerprint;                      // the same identity but no spot tick: no anchor
        Assert.NotEqual(a, noSpot);

        var otherCenter = Build(Rows(SpotTok, At(9, 29, 59), 24010m), SpotTok).Report.Fingerprint;
        Assert.NotEqual(a, otherCenter);                                                          // 24010 -> center 24000 instead of 24050
    }

    [Fact]
    public void TheDurableRunIdentity_ChangesWithTheSpotTokenAndTheAtmRule_SoAResumeUnderAnotherContractIsRefused()
    {
        var baseline = NiftySignal.Trading.Recovery.RunIdentity.FreezeText(TranslatorFreeze(spot: true));
        Assert.Equal(baseline, NiftySignal.Trading.Recovery.RunIdentity.FreezeText(TranslatorFreeze(spot: true)));                      // deterministic
        Assert.NotEqual(baseline, NiftySignal.Trading.Recovery.RunIdentity.FreezeText(TranslatorFreeze(spot: false)));                  // legacy vs spot contract
        Assert.NotEqual(baseline, NiftySignal.Trading.Recovery.RunIdentity.FreezeText(TranslatorFreeze(spot: true) with { SpotToken = "OTHERSPOT" }));
        Assert.NotEqual(baseline, NiftySignal.Trading.Recovery.RunIdentity.FreezeText(TranslatorFreeze(spot: true) with { AtmRuleVersion = null }));
    }

    // ---------------------------------------------------------------- the translator

    private static SessionFreeze TranslatorFreeze(bool spot)
    {
        var chain = Chain();
        return new SessionFreeze(
            new AdaptiveSessionDefinition(Day, Fut, "NIFTYFUT", Day.AddDays(14), 75, 100),
            Weekly, M1SessionFreeze.RiskFreeRate, null, At(9, 30), At(9, 15), chain, null,
            chain.ToDictionary(c => c.Token, _ => 0.05m, StringComparer.Ordinal), M1Identity.ModelVersion,
            spot ? SpotTok : null, spot ? AtmCenterRules.SpotV1Version : null);
    }

    private static AdaptiveEventTranslator Translator(SessionFreeze freeze) => new(freeze, new TranslatorOptions
    {
        StableOrderingLag = TimeSpan.Zero,
        AcceptFromUtc = freeze.ObservationStartUtc,
        AcceptThroughUtc = At(15, 35),
    });

    /// <summary>Two completed 100-contract bars. Spot rows (when given) are offered BEFORE the future row of the same instant and, for the second bar, at a later instant.</summary>
    private static List<SourceTick> BarStream(double? spotFirst, double? spotSecond, out long spotRows)
    {
        var rows = new List<SourceTick>();
        long id = 1;
        long spotCount = 0;
        var t0 = At(10, 0);
        void Group(DateTimeOffset at, long volume, double? spot)
        {
            foreach (var i in Chain())
            {
                rows.Add(OptionRow(ref id, i, at));
            }

            if (spot is { } s)
            {
                rows.Add(new SourceTick(SpotTok, id++, at, at, (decimal)s, 0m, 0m, 0, 0, 0, null));
                spotCount++;
            }

            rows.Add(new SourceTick(Fut, id++, at, at, 24000m, 23999.5m, 24000.5m, 100, 100, volume, 5000));
        }

        Group(t0, 1000, spotFirst);
        Group(t0.AddSeconds(1.5), 1000, spotSecond);              // a spot move while the bar is open; the future volume does not change
        Group(t0.AddSeconds(2), 1100, null);                       // bar 1 closes; the next band is chosen here from the 0.5 s old spot
        Group(t0.AddSeconds(4), 1200, null);                       // bar 2 closes
        spotRows = spotCount;
        return rows;
    }

    [Fact]
    public void Translator_SpotRowsFeedTheEngine_ButAreNeverQuotesAndNeverFormAGroupOfTheirOwn()
    {
        var rows = BarStream(24020, 24030, out var spotRows);
        Assert.Equal(2, spotRows);
        var translator = Translator(TranslatorFreeze(spot: true));
        var groups = Tk.All(translator, rows);

        var spotIds = rows.Where(r => r.Token == SpotTok).Select(r => r.Id).ToHashSet();
        Assert.All(groups, g => Assert.DoesNotContain(g.Quotes, q => spotIds.Contains(q.Event.SourceId)));      // not an option quote, not a future quote
        Assert.Equal(rows.Select(r => r.ReceivedAt).Distinct().Count(), groups.Count);                          // one group per instant; the spot rows added none
        Assert.Equal(rows.Count - spotRows, groups.Sum(g => g.Quotes.Count));                                   // every non-spot row is still exactly one quote

        var bars = groups.SelectMany(g => g.Bars).ToList();
        Assert.Equal(2, bars.Count);
        Assert.Equal(24000d, bars[0].Package!.OptionBand.Selection!.CenterStrike);                            // spot 24020 at the bar's opening instant
        Assert.Equal(24050d, bars[1].Package!.OptionBand.Selection!.CenterStrike);                            // spot 24030 (0.5 s old) when the next band was chosen
        Assert.Equal(24000d, bars[1].Package!.OptionBand.Selection!.SyntheticUnderlying, 0);                  // the forward did not choose the center
    }

    [Fact]
    public void Translator_WithoutSpotRows_HasNoBandAndTheDeclaredReason()
    {
        var rows = BarStream(null, null, out _);
        var bars = Tk.All(Translator(TranslatorFreeze(spot: true)), rows).SelectMany(g => g.Bars).ToList();
        Assert.Equal(2, bars.Count);
        Assert.All(bars, b =>
        {
            Assert.Null(b.Package!.OptionBand.Selection);
            Assert.Equal(AtmCenterRules.SpotUnavailableReason, b.Package.OptionBand.UnavailableReason);
        });
    }

    [Fact]
    public void Translator_AFreezeWithoutASpotToken_StillRefusesSpotRows_AndKeepsTheLegacyRule()
    {
        var translator = Translator(TranslatorFreeze(spot: false));
        var spotRow = new SourceTick(SpotTok, 1, At(10, 0), At(10, 0), 24020m, 0m, 0m, 0, 0, 0, null);
        Assert.Equal(RowDisposition.Quarantined, translator.Offer(spotRow));                       // unknown token, exactly as before this change

        var rows = BarStream(null, null, out _);
        var bars = Tk.All(Translator(TranslatorFreeze(spot: false)), rows).SelectMany(g => g.Bars).ToList();
        Assert.All(bars, b => Assert.Equal(24000d, b.Package!.OptionBand.Selection!.CenterStrike)); // legacy synthetic-forward center, no spot needed
    }

    [Fact]
    public void Translator_IsDeterministic_AcrossReleaseCadences_WithSpotRows()
    {
        var rows = BarStream(24020, 24030, out _);
        string Run(int batch)
        {
            var translator = Translator(TranslatorFreeze(spot: true));
            var groups = new List<TranslatedGroup>();
            var n = 0;
            foreach (var r in rows)
            {
                translator.Offer(r);
                if (++n % batch == 0)
                {
                    translator.AdvanceSource(r.ReceivedAt);
                    groups.AddRange(translator.Release());
                }
            }

            groups.AddRange(translator.Finish());
            return string.Join("|", groups.SelectMany(g => g.Bars).Select(b => $"{b.Event.BarSeq}:{b.Package!.OptionBand.Selection?.CenterStrike}"));
        }

        Assert.Equal(Run(1000), Run(7));
        Assert.Equal("1:24000|2:24050", Run(3));
    }
}
