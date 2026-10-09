using NiftySignal.AdaptiveObserver;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Events;

namespace NiftySignal.Tests.Trading.Adapters;

internal static class Tk
{
    public static readonly DateTimeOffset T = SyntheticSession.Ist(10, 0);

    public static SourceTick Row(string token, long id, DateTimeOffset at, decimal last, decimal bid, decimal ask, long volume = 0, long bidQty = 100, long askQty = 100, DateTimeOffset? received = null, DateTimeOffset? exchange = null, long? oi = 5_000)
        => new(token, id, exchange ?? at, received ?? at, last, bid, ask, bidQty, askQty, volume, oi);

    public static AdaptiveEventTranslator Translator(TimeSpan? lag = null, SessionFreeze? freeze = null, TranslatorOptions? options = null)
    {
        var f = freeze ?? SyntheticSession.Freeze(Array.Empty<SourceTick>(), anchor: false);
        return new AdaptiveEventTranslator(f, options ?? new TranslatorOptions
        {
            StableOrderingLag = lag ?? TimeSpan.Zero,
            AcceptFromUtc = SyntheticSession.Ist(9, 15),
            AcceptThroughUtc = SyntheticSession.Ist(15, 35),
        });
    }

    public static List<TranslatedGroup> All(AdaptiveEventTranslator translator, IEnumerable<SourceTick> rows)
    {
        var groups = new List<TranslatedGroup>();
        foreach (var row in rows)
        {
            translator.Offer(row);
            groups.AddRange(translator.Release());
        }

        groups.AddRange(translator.Finish());
        return groups;
    }

    public static string Dump(IEnumerable<TranslatedGroup> groups)
        => string.Join("\n", groups.Select(g => Canonical.Time(g.AvailableAt) + "|" + string.Join(",", g.Quotes.Select(q => $"{q.Event.Kind}{q.Event.SourceId}")) + "|" + string.Join(",", g.Bars.Select(b => "b" + b.Event.BarSeq))));
}

public class TranslatorTests
{
    private static readonly SourceTick[] Mini =
    {
        Tk.Row("FUT", 1, Tk.T, 23900m, 23899.5m, 23900.5m, volume: 10_000),
        Tk.Row("CE23900", 2, Tk.T.AddSeconds(1), 125m, 124m, 125m),
        Tk.Row("FUT", 3, Tk.T.AddSeconds(2), 23901m, 23900.5m, 23901.5m, volume: 12_300),
        Tk.Row("CE23900", 4, Tk.T.AddSeconds(2), 126m, 125m, 126m),
        Tk.Row("PE23900", 5, Tk.T.AddSeconds(2), 120m, 119m, 120m),
    };

    [Fact]
    public void EqualTimeGroup_PutsOptionsBeforeTheFuture_ThenTheBars_AndEveryBarIsAtTheGroupTime()
    {
        var groups = Tk.All(Tk.Translator(), Mini);

        var group = Assert.Single(groups, g => g.Bars.Count > 0);
        Assert.Equal(Tk.T.AddSeconds(2), group.AvailableAt);
        Assert.Equal(new[] { "OptionQuote4", "OptionQuote5", "FutureQuote3" }, group.Quotes.Select(q => $"{q.Event.Kind}{q.Event.SourceId}").ToArray());   // future has the LOWEST id but ranks after the options
        Assert.Equal(new long[] { 1, 2 }, group.Bars.Select(b => b.Event.BarSeq).ToArray());                                                     // one crossing update closes two exact bars at one instant
        Assert.All(group.Bars, b => Assert.Equal(group.AvailableAt, b.Event.AvailableAt));
        Assert.Equal(group.Events().Select(e => e.Kind).ToArray(), group.Events().OrderBy(e => e, EventOrdering.Instance).Select(e => e.Kind).ToArray());   // already in the core's total order
    }

    [Fact]
    public void CandlePublication_SeesTheSameInstantOptionQuotes_InTheFirstBarOnly()
    {
        var rows = Mini;
        var freeze = SyntheticSession.Freeze(Array.Empty<SourceTick>(), anchor: false);
        var run = SyntheticSession.NewRun(freeze, new RecorderOptions { CaptureAll = true }, lag: TimeSpan.Zero);
        var result = HistoricalAdapter.Run(run, rows);

        var candles = result.Captured[RunRecorders.Names.Candles];
        Assert.Equal(2, candles.Count);
        var first = candles[0].Line;
        var second = candles[1].Line;
        Assert.Contains("\"Token\":\"CE23900\"", first, StringComparison.Ordinal);
        Assert.Contains("\"Observations\":2", first, StringComparison.Ordinal);                    // the id-2 quote (1 s) and the id-4 quote (2 s, same instant as the closing future tick)
        Assert.Contains("\"LastAsk\":126", first, StringComparison.Ordinal);
        Assert.Contains("\"Observations\":0", second, StringComparison.Ordinal);                  // the zero-duration follow-on bar sees no activity
    }

    [Fact]
    public void ReleaseNeedsStrictProgress_SoABatchBoundaryCanNeverPublishAGroupEarly()
    {
        var translator = Tk.Translator(lag: TimeSpan.Zero);
        translator.Offer(Tk.Row("CE23900", 1, Tk.T, 125m, 124m, 125m));
        Assert.Empty(translator.Release());                                                        // boundary == group time: another row of the same instant may still arrive
        translator.Offer(Tk.Row("PE23900", 2, Tk.T, 120m, 119m, 120m));
        Assert.Empty(translator.Release());

        translator.Offer(Tk.Row("CE23900", 3, Tk.T.AddSeconds(1), 126m, 125m, 126m));
        var released = Assert.Single(translator.Release());
        Assert.Equal(new long[] { 1, 2 }, released.Quotes.Select(q => q.Event.SourceId).ToArray());
    }

    [Fact]
    public void SourceProgressProof_RaisesTheBoundary_MinusTheOrderingLag()
    {
        var translator = Tk.Translator(lag: TimeSpan.FromSeconds(2));
        translator.Offer(Tk.Row("CE23900", 1, Tk.T, 125m, 124m, 125m));
        translator.AdvanceSource(Tk.T.AddSeconds(1));
        Assert.Empty(translator.Release());
        translator.AdvanceSource(Tk.T.AddSeconds(2));                                              // boundary == T: strict, still not released
        Assert.Empty(translator.Release());
        translator.AdvanceSource(Tk.T.AddMilliseconds(2001));
        Assert.Single(translator.Release());
        Assert.Equal(Tk.T.AddMilliseconds(1), translator.Boundary);
    }

    [Fact]
    public void ALateRow_IsQuarantined_AndChangesNeitherTheQuotesNorTheAdaptiveState()
    {
        var baseline = new[]
        {
            Tk.Row("FUT", 1, Tk.T, 23900m, 23899.5m, 23900.5m, volume: 10_000),
            Tk.Row("CE23900", 2, Tk.T.AddSeconds(1), 125m, 124m, 125m),
            Tk.Row("FUT", 3, Tk.T.AddSeconds(20), 23901m, 23900.5m, 23901.5m, volume: 12_300),
            Tk.Row("CE23900", 4, Tk.T.AddSeconds(21), 126m, 125m, 126m),
        };
        var late = Tk.Row("FUT", 5, Tk.T.AddSeconds(3), 23950m, 23949.5m, 23950.5m, volume: 11_000);   // id fits (above the highest accepted one), availability does not

        var clean = Tk.Translator(lag: TimeSpan.FromSeconds(2));
        var cleanGroups = Tk.All(clean, baseline);

        var disturbed = Tk.Translator(lag: TimeSpan.FromSeconds(2));
        var groups = new List<TranslatedGroup>();
        foreach (var row in baseline.Take(3).Append(late).Concat(baseline.Skip(3)))
        {
            var disposition = disturbed.Offer(row);
            Assert.Equal(row == late ? RowDisposition.Quarantined : RowDisposition.Accepted, disposition);
            groups.AddRange(disturbed.Release());
        }

        groups.AddRange(disturbed.Finish());

        Assert.Equal(Tk.Dump(cleanGroups), Tk.Dump(groups));
        Assert.Equal(Canonical.Json(cleanGroups.SelectMany(g => g.Bars).Select(b => b.Metrics).ToArray()), Canonical.Json(groups.SelectMany(g => g.Bars).Select(b => b.Metrics).ToArray()));
        var q = Assert.Single(disturbed.Quarantines);
        Assert.Equal(RowRejection.LateAvailability, q.Reason);
        Assert.True(q.Transport);
        Assert.Equal(5, q.Id);
        Assert.True(disturbed.Counters.Balanced);
    }

    [Fact]
    public void AdmissionRules_RefuseTheRow_WithoutTouchingAnyState()
    {
        var translator = Tk.Translator(lag: TimeSpan.Zero);
        var ok = Tk.Row("CE23900", 10, Tk.T, 125m, 124m, 125m);
        Assert.Equal(RowDisposition.Accepted, translator.Offer(ok));

        Assert.Equal(RowDisposition.Redelivered, translator.Offer(ok));                                                 // exact re-delivery: ignored, not an event
        Assert.Equal(RowDisposition.Quarantined, translator.Offer(ok with { Bid = 123m }));                           // same id, other content
        Assert.Equal(RowDisposition.Quarantined, translator.Offer(Tk.Row("ZZ1", 11, Tk.T, 1m, 1m, 2m)));               // unknown token
        Assert.Equal(RowDisposition.Quarantined, translator.Offer(ok with { Id = 12, ExchangeTimestamp = default }));   // missing timestamp: not repaired
        Assert.Equal(RowDisposition.Quarantined, translator.Offer(ok with { Id = 13, ReceivedAt = Tk.T.ToOffset(TimeSpan.FromHours(5.5)) }));   // non-UTC offset: not converted
        Assert.Equal(RowDisposition.Quarantined, translator.Offer(ok with { Id = 14, Ask = -1m }));                    // negative price
        Assert.Equal(RowDisposition.Quarantined, translator.Offer(ok with { Id = 9 }));                                // id below the highest accepted one and not remembered

        var reasons = translator.Quarantines.Select(q => q.Reason).ToArray();
        Assert.Equal(
            new[] { RowRejection.ConflictingDuplicate, RowRejection.UnknownToken, RowRejection.MissingTimestamp, RowRejection.NonUtcTimestamp, RowRejection.NegativeValue, RowRejection.IdOutOfOrder },
            reasons);
        Assert.Equal(new[] { false, false, false, false, false, true }, translator.Quarantines.Select(q => q.Transport).ToArray());
        var counters = translator.Counters;
        Assert.Equal((8L, 1L, 1L, 6L), (counters.Input, counters.Translated, counters.Redelivered, counters.Quarantined));
        Assert.True(counters.Balanced);

        var groups = translator.Finish();
        Assert.Equal(new long[] { 10 }, groups.SelectMany(g => g.Quotes).Select(q => q.Event.SourceId).ToArray());   // exactly one quote came out
    }

    [Fact]
    public void ConsecutiveIdenticalPayloads_AreDroppedByDesign_ExactlyLikeTheExistingNormaliser()
    {
        var translator = Tk.Translator(lag: TimeSpan.Zero);
        translator.Offer(Tk.Row("CE23900", 1, Tk.T, 125m, 124m, 125m));
        Assert.Equal(RowDisposition.PayloadDuplicate, translator.Offer(Tk.Row("CE23900", 2, Tk.T.AddSeconds(1), 125m, 124m, 125m, exchange: Tk.T)));
        Assert.Equal(RowDisposition.Accepted, translator.Offer(Tk.Row("PE23900", 3, Tk.T.AddSeconds(1), 125m, 124m, 125m)));   // another token with the same payload is not a duplicate
        Assert.Equal(1, translator.Counters.PayloadDuplicates);
        Assert.Equal(0, translator.Counters.Quarantined);
        Assert.True(translator.Counters.Balanced);
    }

    [Fact]
    public void AvailabilityProvenance_KeepsBothTimestamps_AndNamesTheBasis_NothingIsRepaired()
    {
        var received = Tk.T;
        var exchange = Tk.T.AddSeconds(1);                                                                              // the exchange stamped it after we say we received it
        var rows = new[]
        {
            Tk.Row("CE23900", 1, received, 125m, 124m, 125m, received: received, exchange: exchange),
            Tk.Row("PE23900", 2, Tk.T.AddSeconds(5), 120m, 119m, 120m),
        };

        var groups = Tk.All(Tk.Translator(), rows);

        var clamped = groups[0].Quotes.Single();
        Assert.Equal(exchange, clamped.Event.AvailableAt);
        Assert.Equal(AvailabilityBasis.ExchangeTimestampClamp, clamped.Provenance.Basis);
        Assert.Equal(received, clamped.Provenance.ReceivedAt);
        Assert.Equal(exchange, clamped.Provenance.ExchangeTimestamp);
        Assert.Equal(AvailabilityBasis.ReceivedAt, groups[1].Quotes.Single().Provenance.Basis);
        Assert.Equal(AvailabilityFallbackPolicy.MaxOfReceivedAndExchange, new TranslatorOptions().AvailabilityFallback);
    }

    [Fact]
    public void Quotes_NeverInferMissingSides_AndZeroSizesAreNotInvented()
    {
        var groups = Tk.All(Tk.Translator(), new[] { Tk.Row("CE23900", 1, Tk.T, 100m, 0m, 101m, bidQty: 0, askQty: 50) });

        var q = groups.Single().Quotes.Single().Event;
        Assert.Null(q.Bid);
        Assert.Null(q.BidQty);
        Assert.Equal(101m, q.Ask);
        Assert.Equal(50, q.AskQty);
        Assert.Equal(100m, q.Last);
    }

    [Fact]
    public void RowsOutsideTheWindow_AreCounted_NotTranslated_AndStillFeedTheNormaliser()
    {
        var translator = Tk.Translator(lag: TimeSpan.Zero);
        var early = SyntheticSession.Ist(9, 14);
        Assert.Equal(RowDisposition.OutsideWindow, translator.Offer(Tk.Row("CE23900", 1, early, 125m, 124m, 125m)));
        Assert.Equal(RowDisposition.PayloadDuplicate, translator.Offer(Tk.Row("CE23900", 2, Tk.T, 125m, 124m, 125m, exchange: early)));     // state from the early row was kept, as in the existing reader
        Assert.Equal(RowDisposition.OutsideWindow, translator.Offer(Tk.Row("CE23900", 3, SyntheticSession.Ist(15, 36), 130m, 129m, 130m)));
        Assert.Equal(2, translator.Counters.OutsideWindow);
        Assert.True(translator.Counters.Balanced);
    }

    [Fact]
    public void RememberedIds_AreBounded_AndAnAncientIdIsNoLongerRecognised()
    {
        var translator = Tk.Translator(options: new TranslatorOptions { StableOrderingLag = TimeSpan.Zero, SeenIdRetention = TimeSpan.FromSeconds(30), AcceptFromUtc = SyntheticSession.Ist(9, 15) });
        for (var i = 1; i <= 600; i++)
        {
            translator.Offer(Tk.Row("CE23900", i, Tk.T.AddSeconds(i), 100m + (i % 7), 99m, 101m));
        }

        Assert.InRange(translator.Counters.SeenIdCount, 28, 33);                                                           // ~30 s of rows, not 600
        var ancient = Tk.Row("CE23900", 5, Tk.T.AddSeconds(5), 105m, 99m, 101m);
        Assert.Equal(RowDisposition.Quarantined, translator.Offer(ancient));
        Assert.Equal(RowRejection.IdOutOfOrder, translator.Quarantines.Last().Reason);
        var recent = Tk.Row("CE23900", 600, Tk.T.AddSeconds(600), 100m + (600 % 7), 99m, 101m);
        Assert.Equal(RowDisposition.Redelivered, translator.Offer(recent));
    }

    [Fact]
    public void ContractUniverse_UsesEffectiveDateTickSizes_AndNeverDefaultsAMissingOne()
    {
        var freeze = SyntheticSession.Freeze(Array.Empty<SourceTick>(), anchor: false);

        var contracts = freeze.ToContracts();
        Assert.Equal(10, contracts.Count);
        Assert.All(contracts, c => Assert.Equal(0.05m, c.TickSize));
        Assert.Equal(contracts.Select(c => c.Token).Order(StringComparer.Ordinal), contracts.Select(c => c.Token));

        var missing = freeze with { TickSizes = freeze.TickSizes.Where(x => x.Key != "CE23900").ToDictionary(x => x.Key, x => x.Value) };
        Assert.Contains("CE23900", Assert.Throws<InvalidOperationException>(() => missing.ToContracts()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTranslatorRefusesInputAfterFinish_AndInvalidOptions()
    {
        var translator = Tk.Translator();
        translator.Finish();
        Assert.Throws<InvalidOperationException>(() => translator.Offer(Tk.Row("CE23900", 1, Tk.T, 1m, 1m, 2m)));
        Assert.Throws<ArgumentException>(() => Tk.Translator(options: new TranslatorOptions { StableOrderingLag = TimeSpan.FromSeconds(-1) }));
    }
}
