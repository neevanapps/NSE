using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Entry;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.Session;
using NiftySignal.Trading.Core.Sizing;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Core.Trading;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Tests.Trading.Core;

/// <summary>A family that answers from a script of (bar sequence -> action); everything else is Wait.</summary>
internal sealed class ScriptedBarFamily(string id, string variant, Func<long, VariantDecision?> script) : IStrategyFamily
{
    public string StrategyId { get; } = id;

    public string Version => "scripted";

    public IReadOnlyCollection<string> VariantIds { get; } = new[] { variant };

    public FamilyDecisions OnBar(BarCompletedEvent bar) => new(new[] { script(bar.BarSeq) ?? VariantDecision.Wait(variant, "Scripted") });
}

/// <summary>Drives the bar-clocked NPT1 execution policy end to end through the real <see cref="TradingEngine"/> and <see cref="EngineLoop"/>.</summary>
internal sealed class Npt1Kit
{
    public static readonly DateOnly Expiry = Clock.Day;

    public const string Strategy = "NPT1-X";
    public const string Variant = "MAIN";

    private long _id;

    public Npt1Kit(Func<long, VariantDecision?> script, IEnumerable<OptionContract>? universe = null, SessionConfig? session = null, NextCompletedBarEntrySpec? policy = null)
    {
        var spec = new VariantSpec
        {
            StrategyId = Strategy,
            VariantId = Variant,
            EntryCondition = policy ?? new NextCompletedBarEntrySpec(),
            Sizing = new FixedQuantitySpec { Units = 650 },
            Exit = new NiftySignal.Trading.Core.Exits.ExitSpec(),
        };
        var config = new TradingConfig
        {
            ConfigVersion = "npt1-kit",
            Session = session ?? SessionConfig.Npt1Live1500(),
            Costs = TradeKit.TestCosts(0m),
            Variants = new Dictionary<string, VariantSpec> { [Strategy + "/" + Variant] = spec },
        };
        var families = new Dictionary<string, IStrategyFamily> { [Strategy] = new ScriptedBarFamily(Strategy, Variant, script) };
        Engine = new TradingEngine(config, Clock.Day, (universe ?? DefaultUniverse()).ToList(), families);
        Loop = new EngineLoop(Engine, Engine.Variants.Schedule.DayStartUtc);
        Engine.Start(Loop);
    }

    public TradingEngine Engine { get; }

    public EngineLoop Loop { get; }

    public VariantKey Key => new(Strategy, Variant);

    public static OptionContract C(OptionSide side, int strike, int lot = 65)
        => new((side == OptionSide.Call ? "CE" : "PE") + strike, side, strike, Expiry, lot, 0.05m);

    public static IReadOnlyList<OptionContract> DefaultUniverse() => new[]
    {
        C(OptionSide.Call, 24000), C(OptionSide.Call, 24050), C(OptionSide.Call, 23950),
        C(OptionSide.Put, 24000), C(OptionSide.Put, 23950), C(OptionSide.Put, 24050),
    };

    public static DateTimeOffset T(int k) => Clock.Ist(10, 0).AddMinutes(k);

    public QuoteEvent Q(DateTimeOffset at, string token, decimal? bid, decimal? ask)
        => new(at, EventKind.OptionQuote, ++_id, token, bid, ask, null, 100000, 100000);

    public static AdaptiveStrategyFrame Frame(DateTimeOffset end, long seq, double? centre = 24000d, DateOnly? expiry = null, string? candidateStrikes = null)
        => new("npt1-m1-test", Clock.Day, seq, end.AddMinutes(-1), end, true, true, (int)seq, "npt1-m1-v1", "test", "test", 0.1d, 5d, 0L, centre, expiry ?? Expiry, null, candidateStrikes);

    public void Bar(DateTimeOffset end, long seq, double? centre = 24000d, DateOnly? expiry = null, params QuoteEvent[] quotes) => BarWithBand(end, seq, null, centre, expiry, quotes);

    public void BarWithBand(DateTimeOffset end, long seq, string? candidateStrikes, double? centre = 24000d, DateOnly? expiry = null, params QuoteEvent[] quotes)
    {
        var bar = new BarCompletedEvent(end, seq, "FUT", null, Frame(end, seq, centre, expiry, candidateStrikes));
        foreach (var group in quotes.Cast<EngineEvent>().Append(bar).GroupBy(e => e.AvailableAt).OrderBy(g => g.Key))
        {
            Loop.ProcessGroup(group.ToArray());
        }
    }

    public IEnumerable<T1> Out<T1>() where T1 : EngineOutput => Engine.Outputs.OfType<T1>().Where(o => o.Variant == Key);

    public IReadOnlyList<Position> Positions => Engine.Execution.PositionsOf(Key);

    public static VariantDecision Enter(StrategyDirection d) => VariantDecision.Enter(Variant, d, "Scripted");

    public static VariantDecision Exit() => VariantDecision.Exit(Variant, "Scripted");
}

public class Npt1ExecutionTests
{
    private const string Ce = "CE24000";

    private static Npt1Kit LongAt(long signalSeq, IEnumerable<OptionContract>? universe = null, SessionConfig? session = null)
        => new(seq => seq == signalSeq ? Npt1Kit.Enter(StrategyDirection.Long) : null, universe, session);

    [Fact]
    public void TheSignalBar_NeverFills_AndTheNextBarFillsTheFrozenContractAtItsAsk_Quantity650()
    {
        var kit = LongAt(3);
        kit.Bar(Npt1Kit.T(3), 3, quotes: kit.Q(Npt1Kit.T(3).AddMilliseconds(-300), Ce, 119m, 120m));
        Assert.Empty(kit.Positions);
        Assert.Empty(kit.Out<FillRecord>());

        kit.Bar(Npt1Kit.T(4), 4, quotes: kit.Q(Npt1Kit.T(4).AddMilliseconds(-500), Ce, 124m, 126m));

        var fill = Assert.Single(kit.Out<FillRecord>());
        Assert.Equal(Npt1Kit.T(4), fill.At);
        Assert.Equal(Ce, fill.Token);
        Assert.Equal(126m, fill.Price);                                      // the fill bar's ask, not the signal bar's 120
        Assert.Equal(650, fill.Quantity);
        var position = Assert.Single(kit.Positions);
        Assert.Equal(PositionState.Open, position.State);
        Assert.Single(kit.Out<PositionOpened>());
    }

    [Fact]
    public void ABadFillBar_CancelsTheEntry_AndALaterBarNeverRescuesIt()
    {
        var kit = LongAt(3);
        kit.Bar(Npt1Kit.T(3), 3, quotes: kit.Q(Npt1Kit.T(3).AddMilliseconds(-300), Ce, 119m, 120m));
        kit.Bar(Npt1Kit.T(4), 4, quotes: kit.Q(Npt1Kit.T(4).AddMilliseconds(-100), Ce, 150m, 151m));        // ask above the band at the fill bar
        kit.Bar(Npt1Kit.T(5), 5, quotes: kit.Q(Npt1Kit.T(5).AddMilliseconds(-100), Ce, 119m, 120m));        // a fine ask one bar later: too late

        Assert.Empty(kit.Out<FillRecord>());
        Assert.Empty(kit.Positions);
        Assert.Contains(kit.Out<AuditRecord>(), a => a.Outcome == "Cancelled:AskAboveBand");
    }

    [Fact]
    public void TheFrozenToken_IsNeverSubstituted_EvenWhenASiblingIsBetterOnTheFillBar()
    {
        var kit = LongAt(3);
        kit.Bar(Npt1Kit.T(3), 3, quotes: new[]
        {
            kit.Q(Npt1Kit.T(3).AddMilliseconds(-100), "CE24000", 119m, 120m),
            kit.Q(Npt1Kit.T(3).AddMilliseconds(-100), "CE24050", 124m, 125m),
        });
        kit.Bar(Npt1Kit.T(4), 4, quotes: new[]
        {
            kit.Q(Npt1Kit.T(4).AddMilliseconds(-100), "CE24000", 160m, 161m),                         // the frozen contract left the band
            kit.Q(Npt1Kit.T(4).AddMilliseconds(-100), "CE24050", 124m, 125m),
        });

        Assert.Empty(kit.Out<FillRecord>());
        Assert.Contains(kit.Out<AuditRecord>(), a => a.Stage == "NextBarEntry" && a.Detail.Contains("CE24000", StringComparison.Ordinal));
    }

    [Fact]
    public void Ranking_NearestStrikeToTheBandCentre_ThenAskNearest125_ThenLowerStrike()
    {
        // centre 24025: CE24000 and CE24050 are equally near; their asks 140 and 130 -> the 130 (distance 5) wins.
        var kit = LongAt(3);
        kit.Bar(Npt1Kit.T(3), 3, centre: 24025d, quotes: new[]
        {
            kit.Q(Npt1Kit.T(3).AddMilliseconds(-100), "CE24000", 139m, 140m),
            kit.Q(Npt1Kit.T(3).AddMilliseconds(-100), "CE24050", 129m, 130m),
        });
        kit.Bar(Npt1Kit.T(4), 4, centre: 24025d, quotes: new[]
        {
            kit.Q(Npt1Kit.T(4).AddMilliseconds(-100), "CE24000", 139m, 140m),
            kit.Q(Npt1Kit.T(4).AddMilliseconds(-100), "CE24050", 129m, 130m),
        });
        Assert.Equal("CE24050", Assert.Single(kit.Out<FillRecord>()).Token);

        // equal strike distance AND equal ask distance (120 and 130 are both 5 from 125): the LOWER strike wins.
        var tie = LongAt(3);
        tie.Bar(Npt1Kit.T(3), 3, centre: 24025d, quotes: new[]
        {
            tie.Q(Npt1Kit.T(3).AddMilliseconds(-100), "CE24000", 119m, 120m),
            tie.Q(Npt1Kit.T(3).AddMilliseconds(-100), "CE24050", 129m, 130m),
        });
        tie.Bar(Npt1Kit.T(4), 4, centre: 24025d, quotes: new[]
        {
            tie.Q(Npt1Kit.T(4).AddMilliseconds(-100), "CE24000", 119m, 120m),
            tie.Q(Npt1Kit.T(4).AddMilliseconds(-100), "CE24050", 129m, 130m),
        });
        Assert.Equal("CE24000", Assert.Single(tie.Out<FillRecord>()).Token);
    }

    [Theory]
    [InlineData(100.00, true)]
    [InlineData(150.00, true)]
    [InlineData(99.95, false)]
    [InlineData(150.05, false)]
    public void TheAskBand_IsInclusiveAt100And150(double ask, bool allowed)
    {
        var kit = LongAt(3);
        var a = (decimal)ask;
        kit.Bar(Npt1Kit.T(3), 3, quotes: kit.Q(Npt1Kit.T(3).AddMilliseconds(-100), Ce, a - 1m, a));
        kit.Bar(Npt1Kit.T(4), 4, quotes: kit.Q(Npt1Kit.T(4).AddMilliseconds(-100), Ce, a - 1m, a));
        Assert.Equal(allowed ? 1 : 0, kit.Out<FillRecord>().Count());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    public void TheFillBarQuoteAge_IsInclusiveAtTwoSeconds(int ageMs, bool allowed)
    {
        var kit = LongAt(3);
        kit.Bar(Npt1Kit.T(3), 3, quotes: kit.Q(Npt1Kit.T(3).AddMilliseconds(-100), Ce, 119m, 120m));
        kit.Bar(Npt1Kit.T(4), 4, quotes: kit.Q(Npt1Kit.T(4).AddMilliseconds(-ageMs), Ce, 119m, 120m));
        Assert.Equal(allowed ? 1 : 0, kit.Out<FillRecord>().Count());
    }

    [Fact]
    public void ASignalBarContractWithAStaleQuote_IsNotEligible_AndCreatesNoEntry()
    {
        var kit = LongAt(3);
        kit.Bar(Npt1Kit.T(3), 3, quotes: kit.Q(Npt1Kit.T(3).AddSeconds(-3), Ce, 119m, 120m));
        kit.Bar(Npt1Kit.T(4), 4, quotes: kit.Q(Npt1Kit.T(4).AddMilliseconds(-100), Ce, 119m, 120m));
        Assert.Empty(kit.Out<FillRecord>());
    }

    [Fact]
    public void ALotSizeThatDoesNotDivide650_FailsClosedAtConfiguration_NeverARoundedQuantity()
    {
        var universe = new[] { Npt1Kit.C(OptionSide.Call, 24000, lot: 75) };
        var error = Assert.Throws<ArgumentException>(() => LongAt(3, universe));
        Assert.Contains("650", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyContractsOfTheCandidateStrikes_AreCandidates_AContractOutsideThemIsNeverPicked()
    {
        // centre 24000, band strikes 24000 and 24050 only: CE23950 has the better ask distance but is not in the band
        var kit = LongAt(3);
        var quotes = new[]
        {
            kit.Q(Npt1Kit.T(3).AddMilliseconds(-100), "CE23950", 124m, 125m),
            kit.Q(Npt1Kit.T(3).AddMilliseconds(-100), "CE24000", 94m, 95m),                    // in the band but below ask 100
            kit.Q(Npt1Kit.T(3).AddMilliseconds(-100), "CE24050", 129m, 130m),
        };
        kit.BarWithBand(Npt1Kit.T(3), 3, "24000|24050", 24000d, null, quotes);
        kit.BarWithBand(Npt1Kit.T(4), 4, "24000|24050", 24000d, null,
            kit.Q(Npt1Kit.T(4).AddMilliseconds(-100), "CE23950", 124m, 125m), kit.Q(Npt1Kit.T(4).AddMilliseconds(-100), "CE24000", 94m, 95m), kit.Q(Npt1Kit.T(4).AddMilliseconds(-100), "CE24050", 129m, 130m));

        Assert.Equal("CE24050", Assert.Single(kit.Out<FillRecord>()).Token);

        // and when no band member qualifies there is no trade, however good a contract outside the band looks
        var none = LongAt(3);
        none.BarWithBand(Npt1Kit.T(3), 3, "24000", 24000d, null, none.Q(Npt1Kit.T(3).AddMilliseconds(-100), "CE23950", 124m, 125m), none.Q(Npt1Kit.T(3).AddMilliseconds(-100), "CE24000", 94m, 95m));
        none.BarWithBand(Npt1Kit.T(4), 4, "24000", 24000d, null, none.Q(Npt1Kit.T(4).AddMilliseconds(-100), "CE23950", 124m, 125m));
        Assert.Empty(none.Out<FillRecord>());
    }

    [Fact]
    public void ABandForAnotherExpiry_SelectsNothing()
    {
        var kit = LongAt(3);
        kit.Bar(Npt1Kit.T(3), 3, expiry: new DateOnly(2026, 9, 29), quotes: kit.Q(Npt1Kit.T(3).AddMilliseconds(-100), Ce, 119m, 120m));
        kit.Bar(Npt1Kit.T(4), 4, quotes: kit.Q(Npt1Kit.T(4).AddMilliseconds(-100), Ce, 119m, 120m));
        Assert.Empty(kit.Out<FillRecord>());
    }

    [Fact]
    public void NoEntryFillsAtOrAfter1500_ButJustBefore_Does()
    {
        var late = LongAt(3);
        var signal = Clock.Ist(14, 58, 59);
        var fillAt = Clock.Ist(15, 0);
        late.Bar(signal, 3, quotes: late.Q(signal.AddMilliseconds(-100), Ce, 119m, 120m));
        late.Bar(fillAt, 4, quotes: late.Q(fillAt.AddMilliseconds(-100), Ce, 119m, 120m));
        Assert.Empty(late.Out<FillRecord>());                                  // 15:00:00 is outside [09:30, 15:00)

        var ok = LongAt(3);
        var okFill = Clock.Ist(14, 59, 59);
        ok.Bar(Clock.Ist(14, 58, 59), 3, quotes: ok.Q(Clock.Ist(14, 58, 59).AddMilliseconds(-100), Ce, 119m, 120m));
        ok.Bar(okFill, 4, quotes: ok.Q(okFill.AddMilliseconds(-100), Ce, 119m, 120m));
        Assert.Single(ok.Out<FillRecord>());

        // mid-morning fills are allowed in the live profile (the 11:00 cutoff belongs to the historical-parity profile only)
        foreach (var (h, m, sec) in new[] { (10, 59, 0), (11, 1, 0), (13, 30, 0) })
        {
            var mid = LongAt(3);
            var sig = Clock.Ist(h, m, sec).AddSeconds(-30);
            var fill = Clock.Ist(h, m, sec);
            mid.Bar(sig, 3, quotes: mid.Q(sig.AddMilliseconds(-100), Ce, 119m, 120m));
            mid.Bar(fill, 4, quotes: mid.Q(fill.AddMilliseconds(-100), Ce, 119m, 120m));
            Assert.Single(mid.Out<FillRecord>());
        }

        var early = LongAt(3);
        var s = Clock.Ist(9, 29, 0);
        early.Bar(s, 3, quotes: early.Q(s.AddMilliseconds(-100), Ce, 119m, 120m));
        early.Bar(Clock.Ist(9, 29, 59), 4, quotes: early.Q(Clock.Ist(9, 29, 58), Ce, 119m, 120m));
        Assert.Empty(early.Out<FillRecord>());                                 // before 09:30:00
    }

    [Fact]
    public void AnOppositeSignal_ExitsFromTheNextBarAtTheBid_AndHasNoStopsOrTargets()
    {
        var kit = new Npt1Kit(seq => seq switch
        {
            3 => Npt1Kit.Enter(StrategyDirection.Long),
            6 => Npt1Kit.Enter(StrategyDirection.Short),
            _ => null,
        });
        kit.Bar(Npt1Kit.T(3), 3, quotes: kit.Q(Npt1Kit.T(3).AddMilliseconds(-100), Ce, 119m, 120m));
        kit.Bar(Npt1Kit.T(4), 4, quotes: kit.Q(Npt1Kit.T(4).AddMilliseconds(-100), Ce, 119m, 120m));
        // a violent drop is NOT acted on: no stop exists
        kit.Bar(Npt1Kit.T(5), 5, quotes: kit.Q(Npt1Kit.T(5).AddMilliseconds(-100), Ce, 20m, 21m));
        Assert.Equal(PositionState.Open, Assert.Single(kit.Positions).State);

        kit.Bar(Npt1Kit.T(6), 6, quotes: new[]
        {
            kit.Q(Npt1Kit.T(6).AddMilliseconds(-100), Ce, 30m, 31m),
            kit.Q(Npt1Kit.T(6).AddMilliseconds(-100), "PE24000", 119m, 120m),
        });
        Assert.Equal(PositionState.Open, kit.Positions[0].State);               // the exit starts on bar 7, not on the signal bar

        kit.Bar(Npt1Kit.T(7), 7, quotes: new[]
        {
            kit.Q(Npt1Kit.T(7).AddMilliseconds(-100), Ce, 40m, 41m),
            kit.Q(Npt1Kit.T(7).AddMilliseconds(-100), "PE24000", 119m, 120m),
        });

        var closed = Assert.Single(kit.Out<PositionClosed>());
        Assert.Equal(40m, closed.AvgExitPrice);
        Assert.Equal(ExitReason.StrategyExit, closed.FinalExitReason);
        Assert.Equal(2, kit.Out<PositionOpened>().Count());                    // the reversal entered on the same bar the exit filled
    }

    [Fact]
    public void ASignalExit_IsNotGatedOnBidAge_AndUsesTheLastTwoSidedBid()
    {
        var kit = new Npt1Kit(seq => seq switch
        {
            3 => Npt1Kit.Enter(StrategyDirection.Long),
            6 => Npt1Kit.Exit(),
            _ => null,
        });
        kit.Bar(Npt1Kit.T(3), 3, quotes: kit.Q(Npt1Kit.T(3).AddMilliseconds(-100), Ce, 119m, 120m));
        kit.Bar(Npt1Kit.T(4), 4, quotes: kit.Q(Npt1Kit.T(4).AddMilliseconds(-100), Ce, 119m, 120m));
        kit.Bar(Npt1Kit.T(6), 6);
        // bar 7: the only quote is one-sided with a zero bid; the last two-sided quote is old but is still the held contract's last usable bid
        kit.Bar(Npt1Kit.T(7), 7, quotes: kit.Q(Npt1Kit.T(7).AddMilliseconds(-100), Ce, 0m, 125m));
        Assert.Single(kit.Out<PositionClosed>());                                // the exit is not gated on age; it used the last positive bid (119) and recorded its age
    }

    [Fact]
    public void TheTerminalClose_At1500_UsesTheObservedPositiveBid_AndNeverBackdates()
    {
        var kit = LongAt(3);
        kit.Bar(Npt1Kit.T(3), 3, quotes: kit.Q(Npt1Kit.T(3).AddMilliseconds(-100), Ce, 119m, 120m));
        kit.Bar(Npt1Kit.T(4), 4, quotes: kit.Q(Npt1Kit.T(4).AddMilliseconds(-100), Ce, 119m, 120m));
        kit.Loop.ProcessGroup(new EngineEvent[] { kit.Q(Clock.Ist(14, 59, 30), Ce, 131m, 133m) });
        kit.Loop.AdvanceTo(Clock.Ist(15, 0).AddSeconds(1));

        var closed = Assert.Single(kit.Out<PositionClosed>());
        Assert.Equal(ExitReason.TerminalClose, closed.FinalExitReason);
        Assert.Equal(131m, closed.AvgExitPrice);
        Assert.Equal(Clock.Ist(15, 0), closed.At);                              // stamped at the terminal instant, not at the quote
    }
}
