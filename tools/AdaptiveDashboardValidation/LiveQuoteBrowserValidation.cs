using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using System.Text.RegularExpressions;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Persistence;
using static Microsoft.Playwright.Assertions;

static class LiveQuoteBrowserValidation
{
    public static async Task CheckAsync(IPage page, HubConnection push, string connection, string evidence)
    {
        var now = DateTimeOffset.UtcNow;
        var day = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        await using var db = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(connection).Options);
        db.Instruments.AddRange(
            Contract("QUOTE-SPOT", "NIFTY", InstrumentType.Index), Contract("QUOTE-FUT", "NIFTY", InstrumentType.Future),
            Contract("QUOTE-CE", "NIFTY", InstrumentType.Option, day.AddDays(6), 25000, OptionType.Call),
            Contract("QUOTE-PE", "NIFTY", InstrumentType.Option, day.AddDays(6), 25000, OptionType.Put),
            Contract("QUOTE-NEXT", "NIFTY", InstrumentType.Option, day.AddDays(13), 25000, OptionType.Call),
            Contract("QUOTE-BANK", "BANKNIFTY", InstrumentType.Index), Contract("QUOTE-SENSEX", "SENSEX", InstrumentType.Index),
            Contract("QUOTE-SENSEX-CE", "SENSEX", InstrumentType.Option, day.AddDays(1), 82000, OptionType.Call));
        db.Ticks.AddRange(Quote("QUOTE-SPOT",25000),Quote("QUOTE-FUT",25040),Quote("QUOTE-CE",100),Quote("QUOTE-PE",110));
        await db.SaveChangesAsync();
        var panel = page.GetByTestId("live-quote");
        await Expect(panel).ToBeVisibleAsync(); // No broker session row: quote row must still exist.
        await Expect(panel).ToContainTextAsync("99.00 / 101.00");
        await Expect(panel).ToContainTextAsync("109.00 / 111.00");
        var call = page.GetByLabel("Nifty call strike", new() { Exact=true });
        var put = page.GetByLabel("Nifty put strike", new() { Exact=true });
        var selectedStrike = new Regex(@"^25000(?:\.0+)?$"); // PostgreSQL preserves decimal scale.
        await Expect(call).ToHaveValueAsync(selectedStrike); await Expect(put).ToHaveValueAsync(selectedStrike);
        foreach(var token in new[] { "QUOTE-BANK", "QUOTE-SENSEX", "QUOTE-SENSEX-CE", "QUOTE-NEXT" })
            await push.InvokeAsync("PushTick", Quote(token, 85000));
        // Follow with an accepted NIFTY tick to flush any circuit render triggered by those pushes.
        await push.InvokeAsync("PushTick", Quote("QUOTE-CE", 102));
        await Expect(panel).ToContainTextAsync("101.00 / 103.00");
        await Expect(panel).Not.ToContainTextAsync("85000");
        await call.SelectOptionAsync(new SelectOptionValue { Label="25000" });
        await put.SelectOptionAsync(new SelectOptionValue { Label="25000" });
        await push.InvokeAsync("PushTick", Quote("QUOTE-SPOT", 27000));
        await Expect(panel).ToContainTextAsync("27000.00");
        await Expect(call).ToHaveValueAsync(selectedStrike); await Expect(put).ToHaveValueAsync(selectedStrike);
        await Expect(panel).ToContainTextAsync("101.00 / 103.00");
        await page.ScreenshotAsync(new() { Path=Path.Combine(evidence,"nifty-live-quote.png"),FullPage=true });
        Console.WriteLine("NIFTY LIVE QUOTE BROWSER PASS: visible without session metadata, persisted LTP/bid/ask, mixed-index/expiry push isolation, CE/PE selection preserved after spot move.");

        Instrument Contract(string token,string underlying,InstrumentType type,DateOnly? expiry=null,decimal? strike=null,OptionType side=OptionType.None) =>
            new() { Token=token,TradingSymbol=token,Underlying=underlying,Exchange=Exchange.Nfo,AsOfDate=day,
                InstrumentType=type,ExpiryDate=expiry ?? (type==InstrumentType.Future ? day.AddDays(20) : null),StrikePrice=strike,OptionType=side,LotSize=65,TickSize=.05m };
        Tick Quote(string token,decimal price) => new() { Token=token,Exchange=Exchange.Nfo,ExchangeTimestamp=DateTimeOffset.UtcNow,ReceivedAt=DateTimeOffset.UtcNow,LastPrice=price,
            Depth=new MarketDepth(price-1,65,0,0,0,0,0,0,0,0,price+1,65,0,0,0,0,0,0,0,0) };
    }
}
