using NiftySignal.Tests.Trading.Core;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Data;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Recovery.Codec;
using NiftySignal.Trading.Signals;

namespace NiftySignal.Tests.Trading.Data;

/// <summary>The typed ownership rows derived from real engine outputs: stored trades, closes, rejections, fills and ledger entries.</summary>
public class ProjectionMapperTests
{
    private const string RunId = "run-1";

    private static readonly VariantKey XA = new("X", "A");
    private static readonly VariantKey YA = new("Y", "A");

    private static IReadOnlyList<OptionContract> Universe() => Kit.StandardUniverse().Select(c => c with { Symbol = $"NIFTY26SEP{c.Strike:0}{(c.Side == OptionSide.Call ? "CE" : "PE")}" }).ToList();

    internal static IReadOnlyList<OutputRecord> Records(IEnumerable<EngineOutput> outputs)
        => outputs.Select((o, i) =>
        {
            var payload = OutputCodec.Encode(o);
            return new OutputRecord(i, 0, o.GetType().Name, o.StrategyId, o.VariantId, o.At, payload, OutputCodec.ContentHash(payload));
        }).ToList();

    internal static IReadOnlyList<VariantIdentity> Header(params VariantKey[] keys)
        => keys.Select(k => new VariantIdentity(k.VariantId, "hash-" + k, k.StrategyId, "v1", RunIdentity.AccountOf(k))).ToList();

    /// <summary>X/A long with TP1 and a stop, Y/A short with the same VariantId: A stops out, Y/A stays open; plus a blocked second X/A entry.</summary>
    private static PaperKit Scenario()
    {
        var exit = new ExitSpec { TradeSlPct = 0.10m, Tp1Pct = 0.10m, Tp1Proportion = 0.5m, MaxHold = TimeSpan.FromMinutes(30) };
        var config = PaperKit.Config(10_000_000m, PaperKit.Spec("X", "A", exit), PaperKit.Spec("Y", "A"));
        var kit = new PaperKit(
            config,
            Universe(),
            PaperKit.Family("X", ("A", PaperKit.Script(new[] { (1L, 1L), (3L, 3L) }))),
            new PlumbingFamily("Y", new Dictionary<string, PlumbingVariantScript> { ["A"] = new(new[] { new PlumbingWindow(1, 1, StrategyDirection.Short) }) }));
        var t = kit.Bar(10, 0, advance: 1.1);                                       // X/A long CE and Y/A short PE enter
        kit.Move(t.AddSeconds(2.2), ceBid: 140m, advance: 0.5);                     // TP1 of X/A
        kit.Move(t.AddSeconds(5.2), ceBid: 100m);                                   // stop of X/A
        kit.Bar(10, 5);                                                             // bar 2: waits
        kit.Bar(10, 10);                                                            // bar 3: X/A again: a new entry that fills
        return kit;
    }

    [Fact]
    public void AStoredTrade_IsDerivedFromTheOutputsAlone_WithEveryEntryAndCloseFact()
    {
        var kit = Scenario();
        var records = Records(kit.Kit.Engine.Outputs);

        var mapped = ProjectionMapper.Map(RunId, records, Header(XA, YA));

        var first = mapped.Positions.First(p => p.StrategyId == "X");
        Assert.Equal((RunId, "X", "A"), (first.RunId, first.StrategyId, first.VariantId));
        Assert.Equal("Long", first.Direction);
        Assert.Equal("CE", first.OptionType);
        Assert.Equal("CE23900", first.Token);
        Assert.Equal("NIFTY26SEP23900CE", first.Symbol);
        Assert.Equal("23900", first.StrikeText);
        Assert.Equal(65, first.LotSize);
        Assert.Equal(650, first.EntryQuantity);
        Assert.Equal("125", first.AvgEntryPriceText);
        Assert.Equal("session:2026-09-22", first.SourceSessionId);
        Assert.Equal(1, first.EntryBarSeq);
        Assert.Equal(DecimalText.Format(125m * (1m - 0.10m)), first.StopLevelText);
        Assert.NotNull(first.Tp1LevelText);
        Assert.NotNull(first.MaxHoldDeadlineUtcTicks);
        Assert.Equal(first.EntryFillUtcTicks + TimeSpan.FromMinutes(30).Ticks, first.MaxHoldDeadlineUtcTicks);
        Assert.True(first.EntryDecisionUtcTicks < first.EntryFillUtcTicks);
        Assert.Equal("independent-virtual-book:X/A", first.AccountId);

        var close = Assert.Single(mapped.Closures, c => c.PositionId == first.PositionId);
        var closed = kit.Kit.Engine.Outputs.OfType<PositionClosed>().Single(c => c.PositionId == first.PositionId);
        Assert.Equal("TradeSl", close.Fields.FinalExitReason);
        Assert.Equal(DecimalText.Format(closed.NetPnl), close.Fields.NetPnlText);
        Assert.Equal(DecimalText.Format(closed.GrossPnl), close.Fields.GrossPnlText);
        Assert.Equal(DecimalText.Format(closed.TotalCosts), close.Fields.TotalCostsText);
        Assert.Equal("Done", close.Fields.Tp1State);
        Assert.Equal(325, close.Fields.Tp1FilledQuantity);
        Assert.Equal(DecimalText.Format(closed.Excursion.Mfe!.Value), close.Fields.MfeText);        // the lifetime excursion is stored with the trade
        Assert.Equal(DecimalText.Format(closed.Excursion.Mae!.Value), close.Fields.MaeText);
        Assert.Equal(closed.Excursion.Coverage.ToString(), close.Fields.ExcursionCoverage);          // here "HasGaps": the scenario has 3 s of silence in the bid path
        Assert.True(close.Fields.ExitBarSeq >= 1);
    }

    [Fact]
    public void TheSameVariantIdUnderTwoStrategies_GivesSeparateRows_Accounts_AndOwnership()
    {
        var kit = Scenario();

        var mapped = ProjectionMapper.Map(RunId, Records(kit.Kit.Engine.Outputs), Header(XA, YA));

        var y = Assert.Single(mapped.Positions, p => p.StrategyId == "Y");
        Assert.Equal(("A", "Short", "PE", "independent-virtual-book:Y/A"), (y.VariantId, y.Direction, y.OptionType, y.AccountId));
        Assert.Equal(new[] { "X", "X", "Y" }, mapped.Positions.OrderBy(p => p.StrategyId, StringComparer.Ordinal).Select(p => p.StrategyId).ToArray());
        Assert.All(mapped.Fills, f => Assert.Equal($"independent-virtual-book:{f.StrategyId}/{f.VariantId}", f.AccountId));
        Assert.All(mapped.Orders, o => Assert.Contains(o.StrategyId, new[] { "X", "Y" }));
        Assert.Equal(mapped.Fills.Count * 7, mapped.Ledger.Count);                   // six cost components plus the notional per fill
        Assert.Equal(mapped.Positions.Count, mapped.Positions.Select(p => p.PositionId).Distinct().Count());
    }

    [Fact]
    public void ABlockedEntry_BecomesATypedRejectionRow_WithIdentitySourceAndReasons()
    {
        var risk = NiftySignal.Trading.Core.Risk.RiskProfile.PaperLiveV1(1_000m);
        var kit = new PaperKit(PaperKit.Config(risk, PaperKit.Spec("X", "A")), PaperKit.Family("X", ("A", PaperKit.Script(new[] { (1L, 1L) }))));
        kit.Bar(10, 0);

        var mapped = ProjectionMapper.Map(RunId, Records(kit.Kit.Engine.Outputs), Header(XA));

        var row = Assert.Single(mapped.Rejections);
        Assert.Equal(("X", "A", "GlobalCapital", "Gate", 1L), (row.StrategyId, row.VariantId, row.Reasons, row.Stage, row.BarSeq));
        Assert.Equal("session:2026-09-22", row.SourceSessionId);
        Assert.StartsWith("dec_", row.DecisionId);
        Assert.Empty(mapped.Positions);
    }

    [Fact]
    public void TheMapping_IsDeterministic_AndABatchSplitDoesNotChangeTheRows()
    {
        var records = Records(Scenario().Kit.Engine.Outputs);
        var whole = ProjectionMapper.Map(RunId, records, Header(XA, YA));
        var again = ProjectionMapper.Map(RunId, records, Header(XA, YA));

        Assert.Equal(whole.Positions.Select(p => p.PositionId), again.Positions.Select(p => p.PositionId));
        Assert.Equal(whole.Fills.Select(f => f.PriceText), again.Fills.Select(f => f.PriceText));

        // an entry order and the position it opens may be committed in different steps: the slot is then looked up by order id
        var slots = whole.Orders.Where(o => o.Kind == "Entry").ToDictionary(o => o.OrderId, o => o.SlotNo);
        var cut = records.ToList().FindIndex(r => r.Type == nameof(FillRecord));          // the entry intents are committed in an earlier step than the first fill
        var early = ProjectionMapper.Map(RunId, records.Take(cut), Header(XA, YA));
        var late = ProjectionMapper.Map(RunId, records.Skip(cut), Header(XA, YA), id => slots.GetValueOrDefault(id));
        Assert.Equal(whole.Positions.Count, early.Positions.Count + late.Positions.Count);
        Assert.Equal(whole.Fills.Count, early.Fills.Count + late.Fills.Count);

        // without the earlier entry order the slot cannot be determined: the mapper refuses to guess
        Assert.Throws<InvalidOperationException>(() => ProjectionMapper.Map(RunId, records.Skip(cut), Header(XA, YA)));
    }

    [Fact]
    public void AVariantMissingFromTheHeader_IsRefused()
    {
        var records = Records(Scenario().Kit.Engine.Outputs);

        Assert.Throws<InvalidOperationException>(() => ProjectionMapper.Map(RunId, records, Header(XA)));            // Y/A is not part of the run
    }
}
