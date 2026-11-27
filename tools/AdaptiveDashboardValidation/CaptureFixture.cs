using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;
using static Microsoft.Playwright.Assertions;

/// <summary>
/// The option chain, per-strike rows and close ATM of the hand-built capture session, and the values the Market Summary must show for them.
/// Rows are written by the Observer's own sidecar writers (so attestation is the production one). A row of bar b depends only on b and its strike: nothing later is used.
/// The expected cells below are closed forms of the fixture numbers, derived from docs/MARKET_SUMMARY_METRICS.md without calling the production calculator.
/// </summary>
static class CaptureFixture
{
    public const double Center = 23000;
    static readonly int[] Ladder = [-3, -2, -1, 0, 1, 2, 3];                       // strikes 22850..23150: all three five-strike baskets resolve
    static readonly AdaptiveSupplementalPersistence Writer = new(NullLogger<AdaptiveSupplementalPersistence>.Instance);

    static string Token(int k, bool call) => k == 0 ? (call ? "PIN-CE" : "PIN-PE") : $"PIN-{(call ? "CE" : "PE")}-{Center + 50 * k:0}";

    public static ObserverOptionInstrument[] Universe(DateOnly expiry) => Ladder.SelectMany(k => new[] { true, false }
        .Select(call => new ObserverOptionInstrument(Token(k, call), Token(k, call), call ? OptionType.Call : OptionType.Put, Center + 50 * k, expiry, 65))).ToArray();

    // CE mid rises 4+k per bar, PE mid falls 1 per bar.
    static OptionStrikeBarMetrics Strike(int b, int k, bool call, DateOnly expiry)
    {
        double slope = call ? 4 + k : -1, priceBase = call ? 100 - 10 * k : 400 + 10 * k;
        double start = priceBase + slope * (b - 1), end = priceBase + slope * b;
        long quantity = call ? 200 : 80, strict = call ? 10 * (4 + k) : -10, enriched = 2 * strict;
        long oiStep = call ? 10 : 30, oiStart = 10_000 + oiStep * (b - 1);
        double ivStart = call ? 12 + .1 * (b - 1) : 14, ivEnd = call ? 12 + .1 * b : 14;
        double deltaStart = call ? .40 + .001 * (b - 1) : -.40, deltaEnd = call ? .40 + .001 * b : -.40;
        double vegaStart = call ? 10 + .01 * (b - 1) : 8, vegaEnd = call ? 10 + .01 * b : 8;
        return new OptionStrikeBarMetrics(Token(k, call), Token(k, call), expiry, Center + 50 * k, call ? OptionType.Call : OptionType.Put,
            start, end, end - start, quantity, (quantity + strict) / 2, (quantity - strict) / 2, 0, strict,
            (quantity + enriched) / 2, (quantity - enriched) / 2, 0, enriched, 1, oiStart, oiStart + oiStep, oiStep,
            ivStart, ivEnd, ivEnd - ivStart, quantity / 60d, 0,
            call ? .006 : .002, call ? .30 : .10, 60, call ? 20 : -10, call ? 100 : 50, 3,
            deltaStart, deltaEnd, deltaEnd - deltaStart, vegaStart, vegaEnd, vegaEnd - vegaStart);
    }

    static AdaptiveCompletedBarPackage Package(int barSeq, DateTimeOffset end, DateOnly expiry) => new(
        new ExactAdaptiveBar
        {
            TradeDate = DateOnly.FromDateTime(end.UtcDateTime), BarSeq = barSeq, Symbol = "NIFTYFUT", IsComplete = true, StartAvailableAtUtc = end.AddMinutes(-1), EndAvailableAtUtc = end,
            Open = Center, High = Center + 1, Low = Center, Close = Center + 1, Vwap = Center, Volume = 3250, TradeUpdates = 1, FirstSourceTickId = 0, LastSourceTickId = 0,
            StrictBuyVolume = 0, StrictSellVolume = 0, StrictUnknownVolume = 3250, EnrichedBuyVolume = 0, EnrichedSellVolume = 0, EnrichedUnknownVolume = 3250,
            OiOpen = null, OiClose = null, Bid = null, Ask = null, BidQty = null, AskQty = null, Spread = null, BookImbalance = null, Microprice = null,
        },
        null!, null!, [], false)       // the two writers used here read only the bar identity, the strikes and the close ATM
    {
        OptionStrikes = Ladder.SelectMany(k => new[] { true, false }.Select(call => Strike(barSeq, k, call, expiry))).ToList(),
        CloseAtm = new BarCloseAtm(Center, Center, null),
    };

    /// <summary>The sidecars the Observer stores for a completed bar. <paramref name="closeAtm"/> false models its close-ATM write not having succeeded yet.</summary>
    public static async Task PersistAsync(AdaptiveObserverDbContext db, long sessionId, int barSeq, DateTimeOffset end, DateOnly expiry, bool closeAtm = true)
    {
        var package = Package(barSeq, end, expiry);
        Require(await Writer.PersistOrVerifyStrikesAsync(db, sessionId, package, default), "strike rows", barSeq);
        if (closeAtm) await PersistCloseAtmAsync(db, sessionId, barSeq, end, expiry);
    }

    public static async Task PersistCloseAtmAsync(AdaptiveObserverDbContext db, long sessionId, int barSeq, DateTimeOffset end, DateOnly expiry) =>
        Require(await Writer.PersistOrVerifyCloseAtmAsync(db, sessionId, Package(barSeq, end, expiry), default), "close ATM", barSeq);

    static void Require(SupplementalOutcome outcome, string what, int barSeq)
    {
        if (outcome != SupplementalOutcome.Inserted) throw new Exception($"Fixture {what} of bar {barSeq} were not stored by the Observer writer: {outcome}.");
    }

    static string N(double v, string format = "0.00") => v.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>The eleven Level-1 Direction cells of one TOTAL row: window of <paramref name="n"/> bars ending at <paramref name="bar"/>, basket shifted <paramref name="m"/> strikes.</summary>
    static string[] TotalCells(int bar, int n, int m)
    {
        var f = bar - n + 1;
        double price = n * (5 + m), priceDen = 2500 + 5d * (f - 1) * (3 + m);
        var observations = Enumerable.Range(Math.Max(f, 10), Math.Max(0, bar - Math.Max(f, 10) + 1)).ToList();
        double r = observations.Count, offsets = observations.Sum(b => b - 10);
        var roll = r == 0 ? "—" : $"{N(10 * (5 + m))} ({N(100 * 50 * r * (5 + m) / (2500 * r + 5 * (3 + m) * offsets))})";
        double strict = 50d * n * (5 + m), flowDen = 1400d * n;
        double ivDen = 65 + .25 * (f - 1), deltaDen = 2 + .0025 * (f - 1), vegaDen = 45 + .025 * (f - 1);
        return
        [
            $"{N(price)} ({N(100 * 5 * price / priceDen)})",
            roll,
            $"{N(strict, "0")} ({N(100 * strict / flowDen)})",
            $"{N(2 * strict, "0")} ({N(100 * 2 * strict / flowDen)})",
            $"{N(100 * n, "0")} ({N(100d * 100 * n / (100_000 + 200d * (f - 1)))})",         // PE - CE
            "0.20 (0.20)",
            $"{N(150 * n, "0")} (20.00)",
            $"{N(-.1 * n)} ({N(-100 * .5 * n / ivDen)})",                                     // IV shown inverted
            $"{N(.1 * n)} ({N(100 * .005 * n / deltaDen)})",                                  // x100
            $"{N(-1.0 * n)} ({N(-100 * .05 * n / vegaDen)})",                                 // x100, shown inverted
            "10.0 (42.86)",
        ];
    }

    static readonly string[] Subtitle = ["Pinned completed boundary · Futures Summary, TOTAL summaries (ITM-shifted, ATM, OTM-shifted) and CE / PE theoretical residual · live incomplete bar excluded"];
    static readonly string[] CaptureGrids = ["summary-futures", "summary-total-itm", "summary-total", "summary-total-otm", "residual-compact"];

    /// <summary>Hand-worked anchors, so a mistake in the closed forms cannot pass unnoticed: bar 43, 5 Bars, ATM basket.</summary>
    static void CheckAnchors()
    {
        var cells = TotalCells(43, 5, 0);
        if (cells[0] != "25.00 (4.07)" || cells[2] != "1250 (17.86)" || cells[4] != "500 (0.46)" || cells[7] != "-0.50 (-3.36)")       // IV: -(2.5 change) / ((79 CE + 70 PE start) / 2)
            throw new Exception("Fixture expected values differ from the hand-worked anchors: " + string.Join(" | ", cells));
    }

    /// <summary>The capture page of one job: identity, subtitle, exactly the five grids, and every summary value for its completed bar.</summary>
    public static async Task CheckCapturePageAsync(IPage page, string url, long jobId, int bar)
    {
        CheckAnchors();
        await page.GotoAsync(url + $"/adaptive-screenshot/{jobId}");
        await Expect(page.Locator(".adaptive-observer[data-capture-ready='true']")).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Expect(page.Locator(".adaptive-capture")).ToHaveAttributeAsync("data-job-id", jobId.ToString());
        await Expect(page.Locator(".adaptive-capture h1")).ToContainTextAsync($"completed bar {bar}");
        await Expect(page.GetByTestId("capture-subtitle")).ToHaveTextAsync($"{Subtitle[0]} · job {jobId}");
        await Expect(page.GetByTestId("market-summary-basket")).ToHaveAttributeAsync("data-end-bar", bar.ToString());
        await Expect(page.GetByTestId("summary-close-atm-strike")).ToHaveTextAsync("23000");
        await Expect(page.GetByTestId("summary-close-atm")).Not.ToContainTextAsync("next bar");
        await Expect(page.GetByTestId("market-summary-problems")).ToHaveCountAsync(0);
        var grids = await page.EvaluateAsync<string[]>("() => Array.from(document.querySelectorAll('table')).map(t => t.getAttribute('data-grid') || '(unnamed)')");
        if (!grids.SequenceEqual(CaptureGrids)) throw new Exception($"Capture of bar {bar} has other tables than the five grids: {string.Join(", ", grids)}");
        var text = await page.Locator(".adaptive-capture").InnerTextAsync();
        foreach (var removed in new[] { "CORE DIRECTION", "OPTION CONTEXT", "FIXED STRIKE HISTORY", "Every row below" })
            if (text.Contains(removed, StringComparison.OrdinalIgnoreCase)) throw new Exception($"Capture of bar {bar} still says '{removed}'.");
        await Expect(page.Locator("table[data-grid='residual-compact'] tbody tr").First.Locator("td").First).ToHaveTextAsync(bar.ToString());

        // Futures: volume and strict delta of the Day row identify the boundary (bars 1..bar only).
        var day = page.Locator("table[data-grid='summary-futures'] tbody tr").Last.Locator("td");
        await Expect(day.Nth(1)).ToContainTextAsync($"{bar}/{bar}");
        var volume = (await day.Nth(3).InnerTextAsync()).Replace(",", "");
        var strictDay = Enumerable.Range(1, bar).Sum(b => b <= 20 ? -100 : b == 21 ? 100 : b == 22 ? -100 : 0);
        if (volume != (3250L * bar).ToString(CultureInfo.InvariantCulture)) throw new Exception($"Day volume of bar {bar}: {volume}");
        await Expect(day.Nth(4)).ToContainTextAsync(strictDay.ToString(CultureInfo.InvariantCulture));

        foreach (var (grid, shift, centre) in new[] { ("summary-total-itm", -1, 22950), ("summary-total", 0, 23000), ("summary-total-otm", 1, 23050) })
        {
            var rows = page.Locator($"table[data-grid='{grid}'] tbody tr");
            await Expect(rows).ToHaveCountAsync(5);
            foreach (var (window, size) in new[] { ("5 Bars", 5), ("10 Bars", 10), ("50 Bars", 50), ("100 Bars", 100), ("Day", bar) })
            {
                var cells = page.Locator($"table[data-grid='{grid}'] tbody tr[data-window='{window}'] td");
                var actual = (await cells.AllInnerTextsAsync()).Select(x => x.Trim()).ToArray();
                var expected = size > bar
                    ? new[] { window, "— – INSUFFICIENT DATA" }.Concat(Enumerable.Repeat("—", 14)).ToArray()
                    : new[] { window, "10/10 – FULL" }.Concat(TotalCells(bar, size, shift)).Concat(Enumerable.Repeat("—", 3)).ToArray();     // Top-5 depth is not recorded here
                if (!actual.SequenceEqual(expected))
                    throw new Exception($"Bar {bar}, {grid} (centre {centre}), {window}: expected [{string.Join(" | ", expected)}] but shows [{string.Join(" | ", actual)}]");
            }
        }
    }
}
