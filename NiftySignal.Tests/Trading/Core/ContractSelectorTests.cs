using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Quotes;
using NiftySignal.Trading.Core.Selection;

namespace NiftySignal.Tests.Trading.Core;

public class ContractSelectorTests
{
    private static readonly DateOnly Trade = new(2026, 9, 22);
    private static readonly DateTimeOffset Now = Clock.Ist(11, 0);
    private static readonly ContractSelectionConfig Config = new();

    private static ContractCandidate Cand(string token, decimal? bid, decimal? ask, OptionSide side = OptionSide.Call, DateOnly? expiry = null, TimeSpan? age = null, int strike = 23900)
        => new(
            new OptionContract(token, side, strike, expiry ?? Trade, 65, 0.05m),
            bid is null && ask is null ? null : new QuoteSnapshot(token, bid, ask, Now - (age ?? TimeSpan.Zero), 1));

    private static ContractSelectionResult Select(IReadOnlyList<ContractCandidate> candidates, OptionSide side = OptionSide.Call, ContractSelectionConfig? config = null, DateOnly? trade = null)
        => ContractSelector.Select(candidates, side, trade ?? Trade, Now, config ?? Config);

    [Theory]
    [InlineData("99.95", false)]
    [InlineData("99.99", false)]
    [InlineData("100.00", true)]
    [InlineData("100.05", true)]
    [InlineData("149.95", true)]
    [InlineData("150.00", true)]
    [InlineData("150.05", false)]
    [InlineData("0.05", false)]
    public void PremiumBand_IsInclusiveAtBothEnds(string askText, bool eligible)
    {
        var ask = decimal.Parse(askText, System.Globalization.CultureInfo.InvariantCulture);

        var result = Select(new[] { Cand("C1", Math.Max(ask - 0.05m, 0.01m), ask) });

        Assert.Equal(eligible ? ContractSelectionOutcome.Selected : ContractSelectionOutcome.NoEligibleContract, result.Outcome);
        var reason = result.Evaluations.Single().Reason;
        Assert.Equal(eligible ? CandidateRejection.None : (ask < 100m ? CandidateRejection.AskBelowBand : CandidateRejection.AskAboveBand), reason);
    }

    [Fact]
    public void DefaultBand_IsOneHundredToOneHundredFifty_AndPartOfTheHash()
    {
        Assert.Equal(100m, Config.MinimumEntryPremium);
        Assert.Equal(150m, Config.MaximumEntryPremium);
        Assert.Equal(125m, Config.BandMidpoint());
    }

    [Fact]
    public void Ranking_PrefersAskNearestTheMidpoint()
    {
        var result = Select(new[]
        {
            Cand("A", 109, 110), Cand("B", 130, 131), Cand("C", 119, 120), Cand("D", 149, 150),
        });

        Assert.Equal("C", result.Selected!.Token);   // |120-125| = 5 beats 110 (15), 131 (6), 150 (25)
    }

    [Fact]
    public void Ranking_TiesOnDistanceGoToTheSmallerSpread_ThenTheStableContractId()
    {
        var spreadWins = Select(new[] { Cand("A", 123m, 124m), Cand("B", 125.5m, 126m) });   // both 1 from 125; spread 1 vs 0.5
        Assert.Equal("B", spreadWins.Selected!.Token);

        var idWins = Select(new[] { Cand("Z", 123m, 124m), Cand("M", 125m, 126m), Cand("K", 125m, 126m) });   // all spread 1 / distance 1
        Assert.Equal("K", idWins.Selected!.Token);
    }

    [Fact]
    public void Selection_IsIndependentOfCandidateInputOrder()
    {
        var rng = new Random(2026);
        var candidates = new List<ContractCandidate>();
        for (var i = 0; i < 30; i++)
        {
            var ask = 90m + (i * 3m);                                // spans below, inside and above the band
            candidates.Add(Cand("T" + (i % 7).ToString("00") + "_" + i, ask - 1m, ask));
        }

        candidates.Add(Cand("X1", 124m, 125m));
        candidates.Add(Cand("X0", 124m, 125m));                      // exact tie with X1
        candidates.Add(Cand("NOQ", null, null));
        var baseline = Select(candidates);
        var baselineEvaluations = baseline.Evaluations.Select(e => (e.Contract.Token, e.Reason)).ToList();

        for (var run = 0; run < 100; run++)
        {
            var shuffled = candidates.OrderBy(_ => rng.Next()).ToList();
            var result = Select(shuffled);
            Assert.Equal(baseline.Selected, result.Selected);
            Assert.Equal(baselineEvaluations, result.Evaluations.Select(e => (e.Contract.Token, e.Reason)).ToList());
        }

        Assert.Equal("X0", baseline.Selected!.Token);
    }

    [Fact]
    public void Quotes_MustBeFreshCausalAndExecutable()
    {
        var max = Config.MaxQuoteAge;
        var cases = new (ContractCandidate Candidate, CandidateRejection Expected)[]
        {
            (Cand("fresh-boundary", 124, 125, age: max), CandidateRejection.None),
            (Cand("stale", 124, 125, age: max + TimeSpan.FromTicks(1)), CandidateRejection.QuoteStale),
            (Cand("future", 124, 125, age: TimeSpan.FromTicks(-1)), CandidateRejection.QuoteNotCausal),
            (Cand("no-quote", null, null), CandidateRejection.NoQuote),
            (Cand("no-bid", null, 125), CandidateRejection.QuoteNotExecutable),
            (Cand("no-ask", 124, null), CandidateRejection.QuoteNotExecutable),
            (Cand("zero-bid", 0, 125), CandidateRejection.QuoteNotExecutable),
            (Cand("crossed", 126, 125), CandidateRejection.QuoteNotExecutable),
            (Cand("locked", 125, 125), CandidateRejection.None),
        };

        foreach (var (candidate, expected) in cases)
        {
            var result = Select(new[] { candidate });
            Assert.Equal(expected, result.Evaluations.Single().Reason);
        }
    }

    [Fact]
    public void NoQualifyingContract_IsReported_NotInvented()
    {
        var result = Select(new[] { Cand("LOW", 4, 5), Cand("HIGH", 199, 200), Cand("STALE", 124, 125, age: TimeSpan.FromMinutes(1)) });

        Assert.Equal(ContractSelectionOutcome.NoEligibleContract, result.Outcome);
        Assert.Null(result.Selected);
        Assert.Null(result.SelectedQuote);
        Assert.Equal(
            new[] { CandidateRejection.AskAboveBand, CandidateRejection.AskBelowBand, CandidateRejection.QuoteStale },
            result.Evaluations.Select(e => e.Reason).ToArray());
    }

    [Fact]
    public void SideFilter_ExcludesTheOtherSide()
    {
        var result = Select(new[] { Cand("CE1", 124, 125), Cand("PE1", 124, 125, OptionSide.Put) }, OptionSide.Put);

        Assert.Equal("PE1", result.Selected!.Token);
        Assert.Equal(CandidateRejection.WrongSide, result.Evaluations.Single(e => e.Contract.Token == "CE1").Reason);
    }

    [Fact]
    public void ExpiryPolicy_UsesOnlyTheNearestExpiry_WithNoFallThrough()
    {
        var next = new DateOnly(2026, 9, 29);
        var candidates = new[]
        {
            Cand("NEAR-OUT", 4, 5),                                       // nearest expiry, outside the band
            Cand("FAR-IN", 124, 125, expiry: next),                       // later expiry, perfectly eligible
        };

        var sameDay = Select(candidates);
        Assert.Equal(ContractSelectionOutcome.NoEligibleContract, sameDay.Outcome);
        Assert.Equal(Trade, sameDay.Expiry);
        Assert.Equal(CandidateRejection.WrongExpiry, sameDay.Evaluations.Single(e => e.Contract.Token == "FAR-IN").Reason);

        var skipExpiryDay = Select(candidates, config: Config with { MinCalendarDaysToExpiry = 1 });
        Assert.Equal("FAR-IN", skipExpiryDay.Selected!.Token);
        Assert.Equal(next, skipExpiryDay.Expiry);

        var afterAll = Select(candidates, trade: new DateOnly(2026, 10, 30));
        Assert.Equal(ContractSelectionOutcome.NoEligibleContract, afterAll.Outcome);
        Assert.Null(afterAll.Expiry);
    }

    [Fact]
    public void RankingIsConfigurable_AndIdentifierAlwaysBreaksTheLastTie()
    {
        var candidates = new[] { Cand("A", 129, 130), Cand("B", 109, 110), Cand("C", 119, 125) };

        var lowest = Select(candidates, config: Config with { Ranking = new[] { SelectionRanking.LowestAsk } });
        var spreadFirst = Select(candidates, config: Config with { Ranking = new[] { SelectionRanking.SmallestSpread, SelectionRanking.LowestAsk } });

        Assert.Equal("B", lowest.Selected!.Token);
        Assert.Equal("B", spreadFirst.Selected!.Token);   // spreads 1,1,6 -> A and B tie, lowest ask B
    }

    [Fact]
    public void Result_IsIdenticalUnderAnotherCulture()
    {
        var candidates = new[] { Cand("I", 123, 124), Cand("i", 123, 124) };

        var invariant = Select(candidates).Selected!.Token;
        var turkish = Culture.Under("tr-TR", () => Select(candidates).Selected!.Token);

        Assert.Equal(invariant, turkish);
    }

    [Fact]
    public void ConfigValidation_RejectsNonsense()
    {
        Assert.Empty(Config.Validate());
        Assert.NotEmpty((Config with { MinimumEntryPremium = 0m }).Validate());
        Assert.NotEmpty((Config with { MaximumEntryPremium = 99m }).Validate());
        Assert.NotEmpty((Config with { MaxQuoteAge = TimeSpan.Zero }).Validate());
        Assert.NotEmpty((Config with { MinCalendarDaysToExpiry = -1 }).Validate());
        Assert.NotEmpty((Config with { Ranking = Array.Empty<SelectionRanking>() }).Validate());
        Assert.NotEmpty((Config with { Ranking = new[] { SelectionRanking.LowestAsk, SelectionRanking.LowestAsk } }).Validate());
        Assert.Throws<ArgumentException>(() => ContractSelector.Select(Array.Empty<ContractCandidate>(), OptionSide.Call, Trade, Now, Config with { MaximumEntryPremium = 1m }));
    }
}
