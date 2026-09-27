using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class VolumeBar6500EpisodeStateAnalysisTests
{
    static readonly DateOnly Day = new(2026, 9, 8);
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    [Theory]
    [InlineData(1.0, 100L, 1, 1, 1)]
    [InlineData(-1.0, -100L, -1, -1, -1)]
    [InlineData(1.0, -100L, 1, -1, null)]
    [InlineData(-1.0, 100L, -1, 1, null)]
    [InlineData(0.0, 100L, null, 1, null)]
    [InlineData(1.0, 0L, 1, null, null)]
    public void StateSign_ClassifiesPriceCvdAndAgreement(
        double priceMove,
        long cvd,
        int? expectedPrice,
        int? expectedCvd,
        int? expectedAgreement)
    {
        var row = Observation(0, priceMove, cvd);

        Assert.Equal(expectedPrice, VolumeBar6500EpisodeStateAnalysis.StateSign(VolumeBar6500EpisodeStateAnalysis.PriceState, row));
        Assert.Equal(expectedCvd, VolumeBar6500EpisodeStateAnalysis.StateSign(VolumeBar6500EpisodeStateAnalysis.CvdState, row));
        Assert.Equal(expectedAgreement, VolumeBar6500EpisodeStateAnalysis.StateSign(VolumeBar6500EpisodeStateAnalysis.AgreementState, row));
    }

    [Fact]
    public void BuildEpisodes_CollapsesConsecutiveSameStateIntoSingleEpisode()
    {
        var rows = new[]
        {
            Observation(0, +1, +100),
            Observation(1, +2, +200),
            Observation(2, +3, +300),
            Observation(3, -1, -100),
            Observation(4, -2, -200),
        };

        var result = VolumeBar6500EpisodeStateAnalysis.BuildEpisodes(rows);
        var price = result.Episodes.Where(x => x.Mechanism == VolumeBar6500EpisodeStateAnalysis.PriceState).ToArray();
        var agreement = result.Episodes.Where(x => x.Mechanism == VolumeBar6500EpisodeStateAnalysis.AgreementState).ToArray();

        Assert.Equal(2, price.Length);
        Assert.Equal(3, price[0].BarCount);
        Assert.Equal(2, price[1].BarCount);
        Assert.Equal(2, agreement.Length);
        Assert.Equal(3, agreement[0].BarCount);
        Assert.Equal(2, agreement[1].BarCount);
    }

    [Fact]
    public void BuildEpisodes_DisagreementTerminatesAgreementEpisode()
    {
        var rows = new[]
        {
            Observation(0, +1, +100),
            Observation(1, +1, +100),
            Observation(2, +1, -100), // disagreement ends episode
            Observation(3, +1, +100),
            Observation(4, +1, +100),
        };

        var result = VolumeBar6500EpisodeStateAnalysis.BuildEpisodes(rows);
        var agreement = result.Episodes.Where(x => x.Mechanism == VolumeBar6500EpisodeStateAnalysis.AgreementState).ToArray();

        Assert.Equal(2, agreement.Length);
        Assert.All(agreement, x => Assert.Equal(2, x.BarCount));
    }

    [Fact]
    public void BuildEpisodes_TracksExactStateAge()
    {
        var rows = new[]
        {
            Observation(0, -1, -100),
            Observation(1, -1, -100),
            Observation(2, -1, -100),
        };

        var result = VolumeBar6500EpisodeStateAnalysis.BuildEpisodes(rows);
        var agreementBars = result.StateBars
            .Where(x => x.Mechanism == VolumeBar6500EpisodeStateAnalysis.AgreementState)
            .OrderBy(x => x.StateAge)
            .ToArray();

        Assert.Equal([1, 2, 3], agreementBars.Select(x => x.StateAge).ToArray());
        Assert.All(agreementBars, x => Assert.Equal(+1, x.ExpectedDirectionSign));
        Assert.All(agreementBars, x => Assert.Equal("Call", x.OptionType));
    }

    static VolumeBar6500Revalidation.Observation Observation(int barIndex, double priceMove, long cvd)
    {
        var at = new DateTimeOffset(Day.ToDateTime(new TimeOnly(9, 15)).AddSeconds(barIndex * 20), Ist);
        var close = 25000m + (decimal)priceMove;
        return new VolumeBar6500Revalidation.Observation(
            Day,
            0,
            barIndex,
            at,
            at,
            close,
            25000m,
            Math.Max(25000m, close),
            Math.Min(25000m, close),
            priceMove,
            0,
            Math.Abs(priceMove),
            6500,
            0,
            10,
            10,
            5,
            5,
            0,
            0,
            0,
            0,
            cvd,
            null,
            1,
            2,
            3,
            6500,
            13000,
            26000,
            2,
            3,
            4,
            2,
            3,
            4);
    }
}