using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.Host;

/// <summary>
/// Persists watch-only Weak2 observations and retrospectively finalizes their hypothetical H5
/// execution using the exact historical simulation policy. No method in this service places,
/// routes, modifies or cancels an order.
/// </summary>
public sealed class AdaptiveWeak2ObservationService(ILogger<AdaptiveWeak2ObservationService> logger)
{
    const double PremiumMin = 100d;
    const double PremiumMax = 150d;
    const double PremiumTarget = 125d;

    sealed record QuotePoint(DateTimeOffset AvailableAt, long Id, double Bid, double Ask, double Last, long? Oi);
    sealed record EntryCandidate(ObserverOptionInstrument Instrument, QuotePoint Quote);

    public async Task ProcessPackageAsync(
        NiftySignalDbContext source,
        AdaptiveObserverDbContext observer,
        AdaptiveObserverSessionContext context,
        AdaptiveCompletedBarPackage package,
        CancellationToken ct)
    {
        if (package.IsActionableWeak2)
        {
            await EnsureObservationAsync(observer, context, package, ct);
        }

        var due = await observer.Weak2Observations
            .Where(x => x.SessionId == context.Session.Id
                && x.Status == AdaptiveObservationStatus.PendingH5
                && x.H5TargetBarSeq <= package.FutureBar.BarSeq)
            .OrderBy(x => x.TriggerBarSeq)
            .ToListAsync(ct);

        foreach (var observation in due)
        {
            await TryFinalizeAsync(source, observer, context, observation, package.FutureBar.BarSeq, ct);
        }
    }

    public async Task FinalizeSessionAsync(
        NiftySignalDbContext source,
        AdaptiveObserverDbContext observer,
        AdaptiveObserverSessionContext context,
        CancellationToken ct)
    {
        var pending = await observer.Weak2Observations
            .Where(x => x.SessionId == context.Session.Id && x.Status == AdaptiveObservationStatus.PendingH5)
            .OrderBy(x => x.TriggerBarSeq)
            .ToListAsync(ct);

        var lastBarSeq = await observer.FutureBars.AsNoTracking()
            .Where(x => x.SessionId == context.Session.Id)
            .Select(x => (int?)x.BarSeq)
            .MaxAsync(ct) ?? 0;

        foreach (var observation in pending)
        {
            if (observation.H5TargetBarSeq <= lastBarSeq)
            {
                await TryFinalizeAsync(source, observer, context, observation, lastBarSeq, ct);
            }

            if (observation.Status == AdaptiveObservationStatus.PendingH5)
            {
                MarkUnavailable(
                    observation,
                    observation.H5TargetBarSeq > lastBarSeq
                        ? "H5 did not complete before session close."
                        : "No executable H5 option exit was available before session close.");
            }
        }

        if (pending.Count > 0)
        {
            await observer.SaveChangesAsync(ct);
        }
    }

    async Task EnsureObservationAsync(
        AdaptiveObserverDbContext db,
        AdaptiveObserverSessionContext context,
        AdaptiveCompletedBarPackage package,
        CancellationToken ct)
    {
        var flow = package.FlowState;
        if (flow.StrongBaseBarSeq is not { } strongSeq || flow.Weak1BarSeq is not { } weak1Seq || flow.Rolling is null)
        {
            throw new InvalidOperationException($"Weak2 BarSeq {package.FutureBar.BarSeq} has no strong-base/weak1 identity.");
        }

        if (await db.Weak2Observations.AnyAsync(
            x => x.SessionId == context.Session.Id && x.TriggerBarSeq == package.FutureBar.BarSeq, ct))
        {
            return;
        }

        var bars = await db.FutureBars.AsNoTracking()
            .Where(x => x.SessionId == context.Session.Id
                && (x.BarSeq == strongSeq || x.BarSeq == weak1Seq || x.BarSeq == package.FutureBar.BarSeq))
            .ToListAsync(ct);
        var baseBar = bars.Single(x => x.BarSeq == strongSeq);
        var weak1Bar = bars.Single(x => x.BarSeq == weak1Seq);
        var triggerBar = bars.Single(x => x.BarSeq == package.FutureBar.BarSeq);

        var oldDirection = flow.Rolling.PriceDirection;
        var strictNoTurn =
            oldDirection * (weak1Bar.Close - baseBar.Close) >= 0d
            && oldDirection * (triggerBar.Close - weak1Bar.Close) >= 0d;

        var residualAtm = package.Residuals.FirstOrDefault(x => x.Variant == ResidualVariant.Atm)?.Reading?.DirectionalResidualPct;
        var residualBand = package.Residuals.FirstOrDefault(x => x.Variant == ResidualVariant.AtmPlusMinus2)?.Reading?.DirectionalResidualPct;
        var reversal = -oldDirection;

        db.Weak2Observations.Add(new AdaptiveWeak2ObservationRow
        {
            SessionId = context.Session.Id,
            StrongBaseBarSeq = strongSeq,
            Weak1BarSeq = weak1Seq,
            TriggerBarSeq = package.FutureBar.BarSeq,
            OldTrendDirection = oldDirection,
            ReversalDirection = reversal,
            StrictNoTurnDiagnostic = strictNoTurn,
            TriggerTimestampUtc = package.FutureBar.EndAvailableAtUtc,
            OptionSide = reversal > 0 ? OptionType.Call : OptionType.Put,
            AtmResidualDirectionalPct = residualAtm,
            BandResidualDirectionalPct = residualBand,
            ResidualSupportsReversalDiagnostic = residualBand.HasValue
                ? Math.Sign(residualBand.Value) == reversal
                : null,
            H5TargetBarSeq = package.FutureBar.BarSeq + 5,
            Status = AdaptiveObservationStatus.PendingH5,
        });
        await db.SaveChangesAsync(ct);
    }

    async Task TryFinalizeAsync(
        NiftySignalDbContext source,
        AdaptiveObserverDbContext observer,
        AdaptiveObserverSessionContext context,
        AdaptiveWeak2ObservationRow observation,
        int currentBarSeq,
        CancellationToken ct)
    {
        var targetBar = await observer.FutureBars.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SessionId == context.Session.Id && x.BarSeq == observation.H5TargetBarSeq, ct);
        if (targetBar is null)
        {
            return;
        }

        var baseBar = await observer.FutureBars.AsNoTracking()
            .SingleAsync(x => x.SessionId == context.Session.Id && x.BarSeq == observation.StrongBaseBarSeq, ct);

        var sideInstruments = context.Options
            .Where(x => x.OptionType == observation.OptionSide)
            .OrderBy(x => x.Strike)
            .ToArray();
        if (sideInstruments.Length == 0)
        {
            MarkUnavailable(observation, "No weekly option instruments available for reversal side.");
            await observer.SaveChangesAsync(ct);
            return;
        }

        // Pull only the required trigger->current window for candidate selection. We deliberately
        // wait until every subscribed candidate has produced its first valid post-trigger quote;
        // because this is watch-only there is no need to invent a timeout that would differ from
        // the historical simulator's first-valid-quote policy.
        var latestKnown = await LatestSourceAvailableAtAsync(source, sideInstruments.Select(x => x.Token).ToArray(), ct);
        if (!latestKnown.HasValue || latestKnown.Value < targetBar.EndAvailableAtUtc)
        {
            return;
        }

        var candidateTicks = Normalize(await LoadTicksAsync(
            source,
            sideInstruments.Select(x => x.Token).ToArray(),
            observation.TriggerTimestampUtc.AddMinutes(-1),
            targetBar.EndAvailableAtUtc.AddMinutes(1),
            ct));

        var firstByToken = candidateTicks
            .Select(ToQuote)
            .Where(x => x is not null
                && x.Value.Point.AvailableAt >= observation.TriggerTimestampUtc
                && x.Value.Point.AvailableAt <= targetBar.EndAvailableAtUtc)
            .Select(x => x!.Value)
            .GroupBy(x => x.Token, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(x => x.Point.AvailableAt).ThenBy(x => x.Point.Id).First().Point,
                StringComparer.Ordinal);

        var eligible = sideInstruments
            .Where(i => firstByToken.TryGetValue(i.Token, out var q) && q.Ask >= PremiumMin && q.Ask <= PremiumMax)
            .Select(i => new EntryCandidate(i, firstByToken[i.Token]))
            .OrderBy(x => Math.Abs(x.Quote.Ask - PremiumTarget))
            .ThenBy(x => x.Instrument.Strike)
            .ToArray();

        if (eligible.Length == 0)
        {
            MarkUnavailable(observation, "No reversal-side option had first post-trigger executable Ask in ₹100–₹150.");
            await observer.SaveChangesAsync(ct);
            return;
        }

        var selected = eligible[0];
        var pairCall = context.Options.SingleOrDefault(x => x.Strike == selected.Instrument.Strike && x.OptionType == OptionType.Call);
        var pairPut = context.Options.SingleOrDefault(x => x.Strike == selected.Instrument.Strike && x.OptionType == OptionType.Put);
        if (pairCall is null || pairPut is null)
        {
            MarkUnavailable(observation, "Selected strike did not have a complete CE/PE pair.");
            await observer.SaveChangesAsync(ct);
            return;
        }

        var pairTokens = new[] { pairCall.Token, pairPut.Token };
        var pairTicks = Normalize(await LoadTicksAsync(
            source,
            pairTokens,
            baseBar.EndAvailableAtUtc.AddMinutes(-5),
            latestKnown.Value,
            ct));

        var callClean = pairTicks.Where(x => x.Token == pairCall.Token)
            .Select(x => x.Tick).OrderBy(x => x.AvailableAt).ThenBy(x => x.Id).ToArray();
        var putClean = pairTicks.Where(x => x.Token == pairPut.Token)
            .Select(x => x.Tick).OrderBy(x => x.AvailableAt).ThenBy(x => x.Id).ToArray();

        // OI parity: use every clean tick carrying OI, not only ticks with a valid two-sided quote.
        var ceBase = OiAtOrBefore(callClean, baseBar.EndAvailableAtUtc);
        var peBase = OiAtOrBefore(putClean, baseBar.EndAvailableAtUtc);
        var ceTrigger = OiAtOrBefore(callClean, observation.TriggerTimestampUtc);
        var peTrigger = OiAtOrBefore(putClean, observation.TriggerTimestampUtc);

        var callQuotes = callClean.Select(x => ToQuote(pairCall.Token, x)).Where(x => x is not null).Select(x => x!.Value.Point).ToArray();
        var putQuotes = putClean.Select(x => ToQuote(pairPut.Token, x)).Where(x => x is not null).Select(x => x!.Value.Point).ToArray();
        var selectedQuotes = selected.Instrument.OptionType == OptionType.Call ? callQuotes : putQuotes;
        var exit = selectedQuotes.FirstOrDefault(x => x.AvailableAt >= targetBar.EndAvailableAtUtc && x.Bid > 0d);
        if (exit is null)
        {
            // H5 has occurred but its first executable bid has not reached persisted source data yet.
            return;
        }

        var bidPath = selectedQuotes
            .Where(x => x.AvailableAt >= selected.Quote.AvailableAt && x.AvailableAt <= exit.AvailableAt && x.Bid > 0d)
            .Select(x => x.Bid)
            .ToArray();
        if (bidPath.Length == 0)
        {
            return;
        }

        observation.ExpiryDate = selected.Instrument.ExpiryDate;
        observation.Strike = selected.Instrument.Strike;
        observation.Token = selected.Instrument.Token;
        observation.TradingSymbol = selected.Instrument.TradingSymbol;
        observation.EntryAsk = selected.Quote.Ask;
        observation.EntryBid = selected.Quote.Bid;
        observation.EntryQuoteTimestampUtc = selected.Quote.AvailableAt;
        observation.EntryQuoteLatencySeconds = (selected.Quote.AvailableAt - observation.TriggerTimestampUtc).TotalSeconds;

        observation.CEOiStrongBase = ceBase;
        observation.PEOiStrongBase = peBase;
        observation.CEOiTrigger = ceTrigger;
        observation.PEOiTrigger = peTrigger;

        if (ceBase.HasValue && peBase.HasValue && ceTrigger.HasValue && peTrigger.HasValue)
        {
            observation.PairOiStrongBase = ceBase.Value + peBase.Value;
            observation.PairOiTrigger = ceTrigger.Value + peTrigger.Value;
            observation.PairOiChange = observation.PairOiTrigger.Value - observation.PairOiStrongBase.Value;
            observation.PairOiChangePct = observation.PairOiStrongBase.Value != 0
                ? (double)observation.PairOiChange.Value / observation.PairOiStrongBase.Value
                : null;
            observation.OiGatePassed = observation.PairOiTrigger.Value > observation.PairOiStrongBase.Value;
        }

        observation.ExitBid = exit.Bid;
        observation.ExitAsk = exit.Ask;
        observation.ExitTimestampUtc = exit.AvailableAt;
        observation.ExitLatencySeconds = (exit.AvailableAt - targetBar.EndAvailableAtUtc).TotalSeconds;
        observation.HoldingSeconds = (exit.AvailableAt - selected.Quote.AvailableAt).TotalSeconds;
        observation.PnlPoints = exit.Bid - selected.Quote.Ask;
        observation.ReturnPct = selected.Quote.Ask != 0d
            ? 100d * observation.PnlPoints.Value / selected.Quote.Ask
            : null;
        observation.MfePointsExecutableBid = bidPath.Max() - selected.Quote.Ask;
        observation.MaePointsExecutableBid = bidPath.Min() - selected.Quote.Ask;
        observation.FuturesH5Move = targetBar.Close - (await observer.FutureBars.AsNoTracking()
            .SingleAsync(x => x.SessionId == context.Session.Id && x.BarSeq == observation.TriggerBarSeq, ct)).Close;
        observation.Status = AdaptiveObservationStatus.Completed;

        await observer.SaveChangesAsync(ct);
        logger.LogInformation(
            "Adaptive Weak2 observation finalized: session={SessionId}, trigger={Trigger}, {Side} {Strike}, OI={OiGate}, H5PnL={Pnl:F2}",
            observation.SessionId, observation.TriggerBarSeq, observation.OptionSide, observation.Strike, observation.OiGatePassed, observation.PnlPoints);
    }

    static void MarkUnavailable(AdaptiveWeak2ObservationRow row, string reason)
    {
        row.Status = AdaptiveObservationStatus.Unavailable;
        row.UnavailableReason = reason;
    }

    static async Task<DateTimeOffset?> LatestSourceAvailableAtAsync(
        NiftySignalDbContext db,
        string[] tokens,
        CancellationToken ct)
    {
        var latest = await db.Ticks.AsNoTracking()
            .Where(x => tokens.Contains(x.Token))
            .OrderByDescending(x => x.Id)
            .Select(x => new { x.ExchangeTimestamp, x.ReceivedAt })
            .FirstOrDefaultAsync(ct);
        if (latest is null) return null;
        return latest.ReceivedAt < latest.ExchangeTimestamp ? latest.ExchangeTimestamp : latest.ReceivedAt;
    }

    static async Task<List<Tick>> LoadTicksAsync(
        NiftySignalDbContext db,
        string[] tokens,
        DateTimeOffset from,
        DateTimeOffset through,
        CancellationToken ct)
    {
        var queryFrom = from.AddMinutes(-1);
        var queryThrough = through.AddMinutes(1);
        return await db.Ticks.AsNoTracking()
            .Where(x => tokens.Contains(x.Token)
                && x.ExchangeTimestamp >= queryFrom
                && x.ExchangeTimestamp <= queryThrough)
            .OrderBy(x => x.Id)
            .ToListAsync(ct);
    }

    static IReadOnlyList<ObserverTokenTick> Normalize(IReadOnlyList<Tick> rows)
    {
        var normalizer = new AdaptiveIncrementalTickNormalizer();
        var clean = new List<ObserverTokenTick>(rows.Count);
        foreach (var row in rows.OrderBy(x => x.Id))
        {
            var tick = normalizer.Process(row.Token, AdaptiveSourceTickReader.ToRaw(row));
            if (tick is { } value)
            {
                clean.Add(new ObserverTokenTick(row.Token, value));
            }
        }

        clean.Sort(static (a, b) =>
        {
            var byTime = a.Tick.AvailableAt.CompareTo(b.Tick.AvailableAt);
            return byTime != 0 ? byTime : a.Tick.Id.CompareTo(b.Tick.Id);
        });
        return clean;
    }

    static (string Token, QuotePoint Point)? ToQuote(ObserverTokenTick item) =>
        ToQuote(item.Token, item.Tick);

    static (string Token, QuotePoint Point)? ToQuote(string token, CleanObserverTick tick)
    {
        if (!tick.HasTwoSidedQuote)
        {
            return null;
        }

        return (token, new QuotePoint(
            tick.AvailableAt,
            tick.Id,
            tick.Bid,
            tick.Ask,
            tick.Last,
            tick.OpenInterest));
    }

    static long? OiAtOrBefore(IReadOnlyList<CleanObserverTick> ticks, DateTimeOffset at) =>
        ticks.Where(x => x.AvailableAt <= at && x.OpenInterest.HasValue)
            .OrderByDescending(x => x.AvailableAt)
            .ThenByDescending(x => x.Id)
            .Select(x => x.OpenInterest)
            .FirstOrDefault();
}
