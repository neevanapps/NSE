using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;

namespace NiftySignal.Tests.Dashboard;

/// <summary>Fixed-strike selector state machine, exact-identity row assembly, the NPT1 live quote book and the simplified paper page projection.</summary>
public class FixedStrikeAndPaperTodayTests
{
    // ------------------------------------------------------------------ selection state machine

    [Fact]
    public void AutoOn_FollowsTheCenterOfEachNewCompletedBar_AndOnlyOfANewBar()
    {
        var sel = new FixedStrikeSelection();
        Assert.True(sel.AutoFollow);

        Assert.True(sel.OnCompletedBar(100, 25100d));
        Assert.Equal(25100d, sel.SelectedStrike);

        Assert.False(sel.OnCompletedBar(100, 25150d));                         // the same bar again (a reload, a push repeat): nothing moves
        Assert.Equal(25100d, sel.SelectedStrike);
        Assert.Equal(25100d, sel.CurrentCenter);

        Assert.True(sel.OnCompletedBar(101, 25150d));
        Assert.Equal(25150d, sel.SelectedStrike);
        Assert.False(sel.OnCompletedBar(102, 25150d));                         // same center: no change, no reload needed
        Assert.False(sel.OnCompletedBar(99, 24000d));                          // an older bar never moves the selection
        Assert.Equal(25150d, sel.SelectedStrike);
    }

    [Fact]
    public void AnUnavailableCenter_DoesNotMoveTheSelection()
    {
        var sel = new FixedStrikeSelection();
        sel.OnCompletedBar(1, 25100d);
        Assert.False(sel.OnCompletedBar(2, null));
        Assert.Equal(25100d, sel.SelectedStrike);
        Assert.Equal(25100d, sel.CurrentCenter);
    }

    [Fact]
    public void AutoOff_PinsTheSelectedStrike_WhileNewCompletedBarsStillArrive()
    {
        var sel = new FixedStrikeSelection();
        sel.OnCompletedBar(100, 25100d);
        sel.SetAutoFollow(false);

        Assert.False(sel.OnCompletedBar(101, 25200d));
        Assert.Equal(25100d, sel.SelectedStrike);
        Assert.Equal(25200d, sel.CurrentCenter);                               // the center label still tracks the market
        Assert.False(sel.OnCompletedBar(102, 25300d));
        Assert.Equal(25100d, sel.SelectedStrike);
    }

    [Fact]
    public void AManualSelection_TurnsAutoOff_AndTurningAutoOnSynchronisesToTheLatestCompletedCenter()
    {
        var sel = new FixedStrikeSelection();
        sel.OnCompletedBar(100, 25100d);

        Assert.True(sel.SelectManually(25000d));
        Assert.False(sel.AutoFollow);
        Assert.False(sel.OnCompletedBar(101, 25150d));                         // the next bar does not override the manual choice
        Assert.Equal(25000d, sel.SelectedStrike);

        Assert.True(sel.SetAutoFollow(true));                                  // immediately synchronises
        Assert.Equal(25150d, sel.SelectedStrike);
        Assert.True(sel.AutoFollow);
        Assert.False(sel.SetAutoFollow(true));                                 // already in sync
    }

    [Fact]
    public void ANewSession_ForgetsAPinnedStrike()
    {
        var sel = new FixedStrikeSelection();
        sel.OnCompletedBar(100, 25100d);
        sel.SelectManually(25000d);
        sel.Reset();
        Assert.True(sel.AutoFollow);
        Assert.Null(sel.SelectedStrike);
        Assert.False(sel.EnsureInitialized());
    }

    // ------------------------------------------------------------------ exact identity

    private static readonly DateOnly Expiry = new(2026, 10, 20);

    private static FixedStrikeCatalog Catalog() => new(Expiry, new[]
    {
        new FixedStrikeContract(25100d, "CE25100", "NIFTY20OCT26C25100", "PE25100", "NIFTY20OCT26P25100"),
        new FixedStrikeContract(25150d, "CE25150", "NIFTY20OCT26C25150", "PE25150", "NIFTY20OCT26P25150"),
    });

    private static AdaptiveOptionStrikeBarRow Row(int bar, double strike, OptionType side, string token, long strictBuy = 1, DateOnly? expiry = null, string version = OptionStrikeBarMetrics.MetricsVersion)
        => new() { SessionId = 1, BarSeq = bar, MetricsVersion = version, Expiry = expiry ?? Expiry, Strike = strike, Side = side, Token = token, TradingSymbol = token, StrictBuy = strictBuy, StrictDelta = strictBuy };

    private static IReadOnlyList<(int, DateTimeOffset)> Bars(params int[] seqs) => seqs.Select(s => (s, new DateTimeOffset(2026, 10, 13, 4, 0, 0, TimeSpan.Zero).AddMinutes(s))).ToList();

    [Fact]
    public void EveryRow_IsExactlyTheSelectedExpiryStrikeAndTokens_NeverAnotherStrike()
    {
        var catalog = Catalog();
        var stored = new[]
        {
            Row(105, 25150d, OptionType.Call, "CE25150"), Row(105, 25150d, OptionType.Put, "PE25150"),
            Row(104, 25150d, OptionType.Call, "CE25150"), Row(104, 25150d, OptionType.Put, "PE25150"),
            Row(104, 25100d, OptionType.Call, "CE25100"),                                           // a different strike of the same bar: must not appear
            Row(103, 25150d, OptionType.Call, "CE25150", expiry: new DateOnly(2026, 10, 27)),        // another expiry: must not appear
            Row(103, 25150d, OptionType.Put, "PE-OTHER"),                                           // wrong token for the strike: must not appear
            Row(102, 25150d, OptionType.Call, "CE25150", version: "option-strike-v0"),               // another metrics version: must not appear
        };

        var history = FixedStrikeProjection.Assemble(catalog, catalog.Find(25150d)!, Bars(102, 103, 104, 105), stored);

        Assert.Equal(new[] { 105, 104, 103, 102 }, history.Rows.Select(r => r.BarSeq));                 // newest first
        Assert.All(history.Rows.SelectMany(r => new[] { r.Ce, r.Pe }).Where(x => x is not null), x =>
        {
            Assert.Equal(25150d, x!.Strike);
            Assert.Equal(Expiry, x.Expiry);
        });
        Assert.Equal("CE25150", history.Rows[0].Ce!.Token);
        Assert.Equal("PE25150", history.Rows[0].Pe!.Token);
        Assert.Equal(new[] { "CE25150", "PE25150" }, history.Rows[1].Ce is { } c1 && history.Rows[1].Pe is { } p1 ? new[] { c1.Token, p1.Token } : null);
        Assert.Null(history.Rows[2].Ce);                                                                // unavailable for that bar, nothing substituted
        Assert.Null(history.Rows[2].Pe);
        Assert.Null(history.Rows[3].Ce);
    }

    [Fact]
    public void ACenterChange_RebuildsTheWholeHistoryForTheNewStrike_NoMixedStrikes()
    {
        var catalog = Catalog();
        var stored = new List<AdaptiveOptionStrikeBarRow>();
        foreach (var bar in new[] { 102, 103, 104, 105 })
        {
            stored.Add(Row(bar, 25100d, OptionType.Call, "CE25100", strictBuy: bar));
            stored.Add(Row(bar, 25100d, OptionType.Put, "PE25100", strictBuy: bar));
            stored.Add(Row(bar, 25150d, OptionType.Call, "CE25150", strictBuy: 1000 + bar));
            stored.Add(Row(bar, 25150d, OptionType.Put, "PE25150", strictBuy: 1000 + bar));
        }

        var sel = new FixedStrikeSelection();
        sel.OnCompletedBar(104, 25100d);
        var before = FixedStrikeProjection.Assemble(catalog, catalog.Find(sel.SelectedStrike!.Value)!, Bars(102, 103, 104), stored);
        Assert.All(before.Rows, r => Assert.Equal(25100d, r.Ce!.Strike));

        Assert.True(sel.OnCompletedBar(105, 25150d));                                                    // the new completed bar re-centres
        var after = FixedStrikeProjection.Assemble(catalog, catalog.Find(sel.SelectedStrike!.Value)!, Bars(102, 103, 104, 105), stored);

        Assert.Equal(new[] { 105, 104, 103, 102 }, after.Rows.Select(r => r.BarSeq));
        Assert.All(after.Rows, r => { Assert.Equal(25150d, r.Ce!.Strike); Assert.Equal(25150d, r.Pe!.Strike); Assert.Equal(1000 + r.BarSeq, r.Ce.StrictBuy); });   // ALL rows are the new strike
    }

    [Fact]
    public void TheCatalog_ComesFromTheFrozenSessionUniverse_WithTheExactCeAndPeTokens()
    {
        var universe = new[]
        {
            new ObserverOptionInstrument("1", "C25100", OptionType.Call, 25100d, Expiry, 65), new ObserverOptionInstrument("2", "P25100", OptionType.Put, 25100d, Expiry, 65),
            new ObserverOptionInstrument("3", "C25150", OptionType.Call, 25150d, Expiry, 65), new ObserverOptionInstrument("4", "P25150", OptionType.Put, 25150d, Expiry, 65),
            new ObserverOptionInstrument("5", "C25200", OptionType.Call, 25200d, Expiry, 65),                       // PE missing from the frozen universe
        };
        var session = new AdaptiveSessionStateRow
        {
            TradeDate = new DateOnly(2026, 10, 13), ModelVersion = "adaptive-v1", SourceBranch = "b", SourceCommitSha = "s", BuildUtc = DateTimeOffset.UtcNow, FutureToken = "F", FutureSymbol = "F",
            FutureExpiry = Expiry, EstimatorName = "e", CreatedAtUtc = DateTimeOffset.UtcNow, OpeningWindowStartUtc = DateTimeOffset.UtcNow, OpeningWindowEndUtc = DateTimeOffset.UtcNow,
            OptionUniverseJson = System.Text.Json.JsonSerializer.Serialize(universe), WeeklyOptionExpiry = Expiry,
        };

        var catalog = FixedStrikeCatalog.FromSession(session)!;

        Assert.Equal(Expiry, catalog.Expiry);
        Assert.Equal(new[] { 25100d, 25150d, 25200d }, catalog.Strikes.Select(s => s.Strike));
        Assert.Equal(("1", "2"), (catalog.Find(25100d)!.CeToken, catalog.Find(25100d)!.PeToken));
        Assert.Null(catalog.Find(25200d)!.PeToken);
        Assert.Null(catalog.Find(25000d));
        Assert.Equal(25150d, catalog.Nearest(25140d));
    }

    // ------------------------------------------------------------------ live quotes

    private static Tick TickOf(string token, decimal ltp, decimal? bid = null, decimal? ask = null, bool depth = true, DateTimeOffset? at = null)
    {
        var t = at ?? new DateTimeOffset(2026, 10, 13, 5, 0, 0, TimeSpan.Zero);
        return new Tick
        {
            Token = token,
            Exchange = Exchange.Nfo,
            ExchangeTimestamp = t,
            ReceivedAt = t,
            LastPrice = ltp,
            Depth = depth ? new MarketDepth(bid ?? 0m, 100, 0, 0, 0, 0, 0, 0, 0, 0, ask ?? 0m, 100, 0, 0, 0, 0, 0, 0, 0, 0) : null,
        };
    }

    [Fact]
    public void OnlyTheExactHeldToken_ReceivesATick_AnUnrelatedTokenChangesNothing()
    {
        var quotes = new Npt1LiveQuotes();
        quotes.SetWatched(new[] { "PE24000" });

        Assert.False(quotes.Apply(TickOf("CE24000", 120m, 119m, 121m)));
        Assert.False(quotes.Apply(TickOf("PE24050", 120m, 119m, 121m)));
        Assert.Empty(quotes.Snapshot());

        Assert.True(quotes.Apply(TickOf("PE24000", 125m, 124m, 126m)));
        var q = Assert.Single(quotes.Snapshot()).Value;
        Assert.Equal(("PE24000", 124m, 126m, 125m), (q.Token, q.Bid, q.Ask, q.Ltp));
    }

    [Fact]
    public void ABidlessBook_GivesNoBid_AndAOneSidedUpdateClearsTheOldBid_NothingIsInvented()
    {
        var quotes = new Npt1LiveQuotes();
        quotes.SetWatched(new[] { "T" });
        quotes.Apply(TickOf("T", 100m, 99m, 101m));
        quotes.Apply(TickOf("T", 100m, 0m, 101m));                                               // the bid disappeared

        Assert.Null(quotes.Snapshot()["T"].Bid);
        Assert.Equal(101m, quotes.Snapshot()["T"].Ask);
    }

    [Fact]
    public void ATouchlineOnlyTick_KeepsTheLastBook_AndItsAge_ButRefreshesTheLtp()
    {
        var quotes = new Npt1LiveQuotes();
        quotes.SetWatched(new[] { "T" });
        var t0 = new DateTimeOffset(2026, 10, 13, 5, 0, 0, TimeSpan.Zero);
        quotes.Apply(TickOf("T", 100m, 99m, 101m, at: t0));
        quotes.Apply(TickOf("T", 103m, depth: false, at: t0.AddSeconds(5)));

        var q = quotes.Snapshot()["T"];
        Assert.Equal((99m, 101m, 103m), (q.Bid, q.Ask, q.Ltp));
        Assert.Equal(t0, q.BookAt);                                                              // the quote age keeps growing from the last real book
    }

    [Fact]
    public void AClosedPosition_LeavesNoLiveState()
    {
        var quotes = new Npt1LiveQuotes();
        quotes.SetWatched(new[] { "A", "B" });
        quotes.Apply(TickOf("A", 1m, 1m, 2m));
        quotes.Apply(TickOf("B", 1m, 1m, 2m));

        quotes.SetWatched(new[] { "B" });                                                       // A closed

        Assert.Equal(new[] { "B" }, quotes.Snapshot().Keys);
        Assert.False(quotes.Apply(TickOf("A", 5m, 5m, 6m)));
    }

    [Fact]
    public void ATradesChangedPush_RaisesTheInvalidation()
    {
        var quotes = new Npt1LiveQuotes();
        var raised = 0;
        quotes.TradesChanged += () => raised++;
        quotes.NotifyTradesChanged();
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task TicksArrivingWhileTheRendererEnumeratesASnapshot_NeverCorruptIt()
    {
        var quotes = new Npt1LiveQuotes();
        var tokens = Enumerable.Range(0, 50).Select(i => "T" + i).ToList();
        quotes.SetWatched(tokens);
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                quotes.Apply(TickOf(tokens[i % 50], 100m + i % 7, 99m + i % 5, 101m + i % 3));
                if (i % 200 == 0) quotes.SetWatched(tokens.Skip(i % 7));
                i++;
            }
        });
        SpinWait.SpinUntil(() => quotes.Snapshot().Count > 0, TimeSpan.FromSeconds(5));
        var enumerated = 0;
        for (var n = 0; n < 20000; n++)
        {
            foreach (var kv in quotes.Snapshot())                                               // would throw "collection modified" if the live dictionary were exposed
            {
                Assert.Equal(kv.Key, kv.Value.Token);
                enumerated++;
            }
        }

        await stop.CancelAsync();
        await writer;
        Assert.True(enumerated > 0);
    }

    // ------------------------------------------------------------------ paper page projection

    private static readonly DateOnly Today = new(2026, 10, 13);
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 5, 30, 0, TimeSpan.Zero);                // 11:00 IST

    private static PaperOpenPosition Open(string strategy, string token, string side = "PE", string entry = "120.00", long qty = 650, string mfe = "5.00", string mae = "-3.00", bool exitPending = false)
        => new(strategy, "MAIN", strategy + token, side == "PE" ? "Short" : "Long", side, "SYM" + token, "24000", Now.AddMinutes(-10), entry, qty, qty, "NotTriggered", null, null, null, "118.00", "-1300", mfe, mae, null, null, "Complete", exitPending, Now, token);

    private static PaperClosedTrade Closed(string strategy, string net, DateTimeOffset exitAt)
        => new(strategy, "MAIN", "p" + net + strategy, "Long", "CE", "SYM", exitAt.AddMinutes(-5), exitAt, "StrategyExit", "100.00", "110.00", 650, "6500.00", "250.00", net, "12.00", "-2.00", null, null, "Complete", 3, 5, "24000", "TKN");

    private static PaperLiveView View(DateOnly date, IReadOnlyList<PaperOpenPosition> open, IReadOnlyList<PaperClosedTrade> closed)
        => new(
            new PaperLiveRunStatus("run", date, false, "owner", 10, 10, 10, 74, Now, Now, null, null, 350000m, Array.Empty<string>()),
            open, closed, Array.Empty<PaperScopeSummary>(), Array.Empty<PaperScopeSummary>(),
            new PaperRiskPanel(350000m, 75000m, 21.4m, 3, 0m, "Healthy", Array.Empty<string>(), Array.Empty<PaperRejection>()));

    [Fact]
    public void TodayPage_ShowsOnlyTodaysTrades_AndAllThreeStrategiesSeparately()
    {
        var yesterdayExit = new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
        var todayExit = Now.AddMinutes(-20);
        var view = View(Today, new[] { Open("NPT1-MD5", "A"), Open("NPT1-PE5", "B", "CE") },
            new[] { Closed("NPT1-MD5", "5000.00", todayExit), Closed("NPT1-PE5", "-2000.00", todayExit.AddMinutes(1)), Closed("NPT1-MDX3", "100.00", yesterdayExit) });

        var page = PaperTodayProjection.Build(view, null, Today, Now);

        Assert.Equal(new[] { "NPT1-MD5", "NPT1-MDX3", "NPT1-PE5" }, page.Strategies.Select(s => s.StrategyId));                  // always the three, in a fixed order
        Assert.Equal(2, page.Completed.Count);                                                                                  // yesterday's trade is not shown
        Assert.Equal(new[] { "NPT1-PE5", "NPT1-MD5" }, page.Completed.Select(c => c.StrategyId));                               // newest first
        var md5 = page.Strategies.Single(s => s.StrategyId == "NPT1-MD5");
        var mdx3 = page.Strategies.Single(s => s.StrategyId == "NPT1-MDX3");
        var pe5 = page.Strategies.Single(s => s.StrategyId == "NPT1-PE5");
        Assert.Equal((1, 1, 0, 5000m), (md5.Trades, md5.Wins, md5.Losses, md5.ClosedNet));
        Assert.Equal((0, 0, 0, 0m), (mdx3.Trades, mdx3.Wins, mdx3.Losses, mdx3.ClosedNet));
        Assert.Equal((1, 0, 1, -2000m), (pe5.Trades, pe5.Wins, pe5.Losses, pe5.ClosedNet));
        Assert.Equal(new[] { "HOLDING", "FLAT", "HOLDING" }, page.Strategies.Select(s => s.State));
        Assert.Equal(2, page.Open.Count);                                                                                       // simultaneous positions
    }

    [Fact]
    public void AnOldRun_ShowsNothingAsToday()
    {
        var page = PaperTodayProjection.Build(View(Today.AddDays(-1), new[] { Open("NPT1-MD5", "A") }, Array.Empty<PaperClosedTrade>()), null, Today, Now);
        Assert.Empty(page.Open);
        Assert.Empty(page.Completed);
    }

    [Fact]
    public void LivePnl_UsesTheBid_NotTheLtp_ForTheExactHeldToken()
    {
        var open = new[] { Open("NPT1-MD5", "A", entry: "120.00", qty: 650), Open("NPT1-PE5", "B", entry: "100.00", qty: 650) };
        var quotes = new Dictionary<string, LiveOptionQuote>
        {
            ["A"] = new("A", 125m, 127m, 140m, Now.AddSeconds(-1)),                                // LTP far away from the bid: must not be used
            ["B"] = new("B", 90m, 92m, 100m, Now.AddSeconds(-3)),
            ["OTHER"] = new("OTHER", 999m, 1000m, 999m, Now),                                      // an unrelated contract: must not leak into any position
        };

        var running = PaperTodayProjection.Running(open, quotes, Now);

        var a = running.Single(r => r.Token == "A");
        var b = running.Single(r => r.Token == "B");
        Assert.Equal((125m - 120m) * 650, a.LivePnlAtBid);
        Assert.Equal((90m - 100m) * 650, b.LivePnlAtBid);
        Assert.Equal((125m, 127m, 140m), (a.Bid, a.Ask, a.Ltp));
        Assert.Equal(TimeSpan.FromSeconds(1), a.QuoteAge);
        Assert.Equal(TimeSpan.FromSeconds(3), b.QuoteAge);
        Assert.Equal(TimeSpan.FromMinutes(10), a.HoldingTime);
    }

    [Fact]
    public void WithoutABid_ThereIsNoPnl_AndNoQuoteMeansNoAge()
    {
        var open = new[] { Open("NPT1-MD5", "A"), Open("NPT1-PE5", "B") };
        var quotes = new Dictionary<string, LiveOptionQuote> { ["A"] = new("A", null, 127m, 140m, Now.AddSeconds(-2)) };      // ask and LTP but NO bid

        var running = PaperTodayProjection.Running(open, quotes, Now);

        Assert.Null(running.Single(r => r.Token == "A").LivePnlAtBid);
        Assert.Equal(140m, running.Single(r => r.Token == "A").Ltp);                                                           // information only
        var b = running.Single(r => r.Token == "B");
        Assert.Null(b.LivePnlAtBid);
        Assert.Null(b.QuoteAge);
        Assert.Null(b.Bid);
    }

    [Fact]
    public void DurableMaeMfe_AreNeverChangedByAPushedTick()
    {
        var open = new[] { Open("NPT1-MD5", "A", mfe: "5.00", mae: "-3.00", qty: 650) };
        var low = PaperTodayProjection.Running(open, new Dictionary<string, LiveOptionQuote> { ["A"] = new("A", 60m, 61m, 60m, Now) }, Now).Single();     // a violent tick far below entry
        var high = PaperTodayProjection.Running(open, new Dictionary<string, LiveOptionQuote> { ["A"] = new("A", 400m, 401m, 400m, Now) }, Now).Single();   // and far above

        Assert.Equal(5m * 650, low.Mfe);
        Assert.Equal(-3m * 650, low.Mae);
        Assert.Equal((low.Mfe, low.Mae), (high.Mfe, high.Mae));
        Assert.NotEqual(low.LivePnlAtBid, high.LivePnlAtBid);                                                                  // only the display P&L moves
    }

    [Fact]
    public void AClosedPosition_DisappearsFromRunning_AndAppearsAmongTodaysCompletedTrades()
    {
        var running = View(Today, new[] { Open("NPT1-MD5", "A") }, Array.Empty<PaperClosedTrade>());
        var closedView = View(Today, Array.Empty<PaperOpenPosition>(), new[] { Closed("NPT1-MD5", "650.00", Now.AddMinutes(-1)) });

        var before = PaperTodayProjection.Build(running, null, Today, Now);
        var after = PaperTodayProjection.Build(closedView, null, Today, Now);

        Assert.Single(before.Open);
        Assert.Empty(before.Completed);
        Assert.Empty(after.Open);
        Assert.Empty(PaperTodayProjection.Running(after.Open, new Dictionary<string, LiveOptionQuote>(), Now));
        var done = Assert.Single(after.Completed);
        Assert.Equal((650L, "StrategyExit", 6500m, 250m, 650m), (done.Quantity, done.ExitReason, done.Gross, done.Fees, done.Net));
        Assert.Equal(12m * 650, done.Mfe);
        Assert.Equal("FLAT", after.Strategies.Single(s => s.StrategyId == "NPT1-MD5").State);
    }

    [Fact]
    public void TheRunningRows_CarryNoTp1StopOrMaxHoldColumns()
    {
        var names = typeof(RunningTradeRow).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Tp1", StringComparison.OrdinalIgnoreCase) || n.Contains("Stop", StringComparison.OrdinalIgnoreCase) || n.Contains("MaxHold", StringComparison.OrdinalIgnoreCase));
        var page = File.ReadAllText(Path.Combine(NiftySignal.Tests.Trading.Data.TradingModelTests.RepoRoot(), "NiftySignal.Dashboard", "Components", "Pages", "PaperLive.razor"));
        var primary = page[..page.IndexOf("paper-diagnostics", StringComparison.Ordinal)];
        Assert.DoesNotContain("TP1", primary, StringComparison.Ordinal);
        Assert.DoesNotContain("Max-hold", primary, StringComparison.Ordinal);
        Assert.DoesNotContain("<th>Stop</th>", primary, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ TOTAL rows

    [Fact]
    public void RunningTotal_SumsTheLivePnlAtBid_WhenEveryPositionIsQuoted()
    {
        var open = new[] { Open("NPT1-MD5", "A", entry: "120.00", qty: 650), Open("NPT1-PE5", "B", entry: "100.00", qty: 650) };
        var quotes = new Dictionary<string, LiveOptionQuote> { ["A"] = new("A", 125m, 127m, 140m, Now), ["B"] = new("B", 90m, 92m, 100m, Now) };
        var running = PaperTodayProjection.Running(open, quotes, Now);

        var total = PaperTotals.Running(running);

        Assert.Equal(running.Sum(r => r.LivePnlAtBid!.Value), total.Total);
        Assert.Equal((125m - 120m) * 650 + (90m - 100m) * 650, total.Total);
        Assert.True(total.IsComplete);
        Assert.False(total.IsPartial);
        Assert.Equal("-3,250.00", total.Text);
    }

    [Fact]
    public void RunningTotal_IsMarkedPartial_NeverTreatingAMissingBidAsZero()
    {
        var open = new[] { Open("NPT1-MD5", "A", entry: "120.00", qty: 100), Open("NPT1-PE5", "B", entry: "100.00", qty: 100), Open("NPT1-MDX3", "C", entry: "50.00", qty: 100) };
        var quotes = new Dictionary<string, LiveOptionQuote>
        {
            ["A"] = new("A", 130m, 131m, 130m, Now),
            ["B"] = new("B", 105m, 106m, 105m, Now),
            ["C"] = new("C", null, 51m, 50m, Now),                                     // no bid: unusable
        };

        var total = PaperTotals.Running(PaperTodayProjection.Running(open, quotes, Now));

        Assert.Equal(1500m, total.Total);                                                // (10 + 5) x 100, the third position contributes NOTHING (not zero-as-a-quote)
        Assert.Equal((2, 3), (total.Quoted, total.Count));
        Assert.True(total.IsPartial);
        Assert.Equal("PARTIAL +1,500.00 · 2/3 quoted", total.Text);
    }

    [Fact]
    public void RunningTotal_IsUnavailable_WhenNoPositionHasAUsableBid_AndEmptyWhenNothingRuns()
    {
        var running = PaperTodayProjection.Running(new[] { Open("NPT1-MD5", "A"), Open("NPT1-PE5", "B") }, new Dictionary<string, LiveOptionQuote>(), Now);

        var none = PaperTotals.Running(running);

        Assert.Null(none.Total);
        Assert.True(none.IsUnavailable);
        Assert.Equal("—", none.Text);
        Assert.Equal("—", PaperTotals.Running(Array.Empty<RunningTradeRow>()).Text);
    }

    [Fact]
    public void CompletedTotals_AreTheSumsOfTheDurableLedgerValues_AndWriteNothing()
    {
        var closed = new[] { Closed("NPT1-MD5", "6250.00", Now.AddMinutes(-30)), Closed("NPT1-PE5", "-1000.50", Now.AddMinutes(-20)), Closed("NPT1-MDX3", "750.25", Now.AddMinutes(-10)) };
        var page = PaperTodayProjection.Build(View(Today, Array.Empty<PaperOpenPosition>(), closed), null, Today, Now);

        var total = PaperTotals.Completed(page.Completed);

        Assert.Equal(page.Completed.Sum(c => c.Gross), total.Gross);
        Assert.Equal(page.Completed.Sum(c => c.Fees), total.Fees);
        Assert.Equal(page.Completed.Sum(c => c.Net), total.Net);
        Assert.Equal(3 * 6500m, total.Gross);                                              // the fixture ledger has Gross 6500.00 / Fees 250.00 on every trade
        Assert.Equal(3 * 250m, total.Fees);
        Assert.Equal(6250.00m - 1000.50m + 750.25m, total.Net);
        Assert.Equal(3, total.Count);
        Assert.Equal(new CompletedTradesTotal(0m, 0m, 0m, 0), PaperTotals.Completed(Array.Empty<CompletedTradeRow>()));
        // Pure functions over already-projected rows: they hold no ledger, database or writer (the type has no state at all).
        Assert.Empty(typeof(PaperTotals).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));
    }
}
