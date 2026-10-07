using Microsoft.Extensions.Logging;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Ingestion.Upstox;

public sealed class UpstoxMarketDataProvider(
    UpstoxInstrumentMasterProvider master, UpstoxRestClient rest, ILoggerFactory loggers) : IMarketDataProvider
{
    public MarketDataProvider Provider => MarketDataProvider.Upstox;

    public async Task<IReadOnlyList<Instrument>> ResolveAsync(MarketDataCredential credential, DateOnly day, CancellationToken ct, IReadOnlySet<string>? existingUnderlyings = null)
    {
        var all = await master.GetAsync(day, ct);
        var selected = new List<Instrument>();
        foreach (var underlying in new[] { "NIFTY", "SENSEX", "BANKNIFTY" })
        {
            if (existingUnderlyings?.Contains(underlying) == true) continue;
            var spot = all.SingleOrDefault(x => x.Underlying == underlying && x.InstrumentType == InstrumentType.Index);
            var future = all.Where(x => x.Underlying == underlying && x.InstrumentType == InstrumentType.Future).OrderBy(x => x.ExpiryDate).FirstOrDefault();
            if (spot is null || future is null)
            {
                if (underlying == "NIFTY") throw new InvalidOperationException("Upstox master is missing NIFTY spot/future.");
                loggers.CreateLogger<UpstoxMarketDataProvider>().LogWarning("Upstox master missing {Underlying} spot/future; secondary collection skipped", underlying);
                continue;
            }
            selected.Add(spot); selected.Add(future);
            var options = all.Where(x => x.Underlying == underlying && x.InstrumentType == InstrumentType.Option).ToList();
            var expiries = options.Select(x => x.ExpiryDate!.Value).Distinct().OrderBy(x => x).Take(underlying == "NIFTY" ? 2 : 1).ToArray();
            if (underlying == "NIFTY" && expiries.Length != 2) throw new InvalidOperationException("Upstox master needs two NIFTY option expiries.");
            var reference = await rest.PreviousCloseOrLastAsync(credential.Token,
                (underlying == "NIFTY" ? spot : future).NativeInstrumentKey!, ct);
            selected.AddRange(SelectOptionBand(options, expiries, reference));
        }
        if (existingUnderlyings?.Contains("NIFTY") != true) selected.Add(all.Single(x => x.InstrumentType == InstrumentType.Vix));
        return selected;
    }

    // Explicit contract selection: fixed descriptors; no rolling ATM token substitution.
    public static IReadOnlyList<Instrument> SelectOptionBand(IReadOnlyList<Instrument> options, IReadOnlyList<DateOnly> expiries, decimal reference)
    {
        var result = new List<Instrument>();
        foreach (var expiry in expiries)
        {
            var chain = options.Where(x => x.ExpiryDate == expiry).ToList();
            var strikes = chain.Select(x => x.StrikePrice!.Value).Distinct().OrderBy(x => x).ToArray();
            if (strikes.Length == 0) throw new InvalidOperationException("Empty Upstox option chain.");
            var atm = strikes.OrderBy(x => Math.Abs(x - reference)).ThenBy(x => x).First();
            var at = Array.IndexOf(strikes, atm);
            var band = strikes.Skip(Math.Max(0, at - 10)).Take(Math.Min(strikes.Length, at + 11) - Math.Max(0, at - 10)).ToHashSet();
            foreach (var strike in band)
            {
                if (!chain.Any(x => x.StrikePrice == strike && x.OptionType == OptionType.Call)
                    || !chain.Any(x => x.StrikePrice == strike && x.OptionType == OptionType.Put))
                    throw new InvalidOperationException("Upstox band is missing a CE/PE contract.");
            }
            result.AddRange(chain.Where(x => band.Contains(x.StrikePrice!.Value)));
        }
        return result;
    }

    public async Task<Instrument?> ResolveOptionAsync(MarketDataCredential credential, DateOnly day, DateOnly expiry, decimal strike, OptionType side, CancellationToken ct) =>
        (await master.GetAsync(day, ct)).SingleOrDefault(x => x.Underlying == "NIFTY" && x.ExpiryDate == expiry && x.StrikePrice == strike && x.OptionType == side);

    public ILiveTickSource CreateFeed(MarketDataCredential credential, IReadOnlyList<Instrument> instruments, IDataGapRecorder gaps) =>
        new UpstoxTickSource(rest, credential.Token, instruments, gaps, loggers.CreateLogger<UpstoxTickSource>());
}
