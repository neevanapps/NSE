using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
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
    public const string SelectionPolicyV1 = "first-post-trigger-ask-100-150-nearest-125-strike-ascending-v1";

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
                await TryFinalizeAsync(source, observer, context, observation, lastBarSeq, ct, sessionClosing: true);
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
            TriggerTimestampUtc = AdaptiveResearchClock.DiagnosticTime(package.FutureBar.EndAvailableAtUtc),
            OptionSide = reversal > 0 ? OptionType.Call : OptionType.Put,
            AtmResidualDirectionalPct = residualAtm,
            BandResidualDirectionalPct = residualBand,
            ResidualSupportsReversalDiagnostic = residualBand.HasValue
                ? Math.Sign(residualBand.Value) == reversal
                : null,
            H5TargetBarSeq = package.FutureBar.BarSeq + 5,
            Status = AdaptiveObservationStatus.PendingH5,
            SelectionPolicy = SelectionPolicyV1,
        });
        await db.SaveChangesAsync(ct);
    }

    long _cachedSessionId;
    DateOnly _cachedDay;
    long _cachedRawId;
    AdaptiveIncrementalTickNormalizer _cacheNormalizer = new();
    readonly Dictionary<string,List<CleanObserverTick>> _cachedTicks = new(StringComparer.Ordinal);

    async Task<IReadOnlyDictionary<string,List<CleanObserverTick>>> ReadCachedSourceAsync(
        NiftySignalDbContext source,AdaptiveObserverSessionContext context,CancellationToken ct)
    {
        if (_cachedSessionId!=context.Session.Id || _cachedDay!=context.Session.TradeDate)
        {
            _cachedSessionId=context.Session.Id;_cachedDay=context.Session.TradeDate;_cachedRawId=0;
            _cacheNormalizer=new();_cachedTicks.Clear();
        }
        var reader=new AdaptiveSourceTickReader();
        var tokens=context.Options.Select(x=>x.Token).Distinct(StringComparer.Ordinal).ToArray();
        var dirty=new HashSet<string>(StringComparer.Ordinal);
        while(true)
        {
            var batch=await reader.ReadRawAfterIdAsync(source,tokens,_cachedRawId,ct,context.Session.TradeDate,batchSize:10000);
            foreach(var item in batch)
            {
                _cachedRawId=Math.Max(_cachedRawId,item.Tick.Id);
                if(_cacheNormalizer.Process(item.Token,item.Tick) is not { } clean)continue;
                if(!_cachedTicks.TryGetValue(item.Token,out var series))_cachedTicks[item.Token]=series=[];
                series.Add(clean);dirty.Add(item.Token);
            }
            if(batch.Count<10000)break;
        }
        foreach(var token in dirty)_cachedTicks[token].Sort(static (a,b)=> {
            var time=a.AvailableAt.CompareTo(b.AvailableAt);return time!=0?time:a.Id.CompareTo(b.Id); });
        return _cachedTicks;
    }

    async Task TryFinalizeAsync(NiftySignalDbContext source,AdaptiveObserverDbContext observer,
        AdaptiveObserverSessionContext context,AdaptiveWeak2ObservationRow observation,int currentBarSeq,
        CancellationToken ct,bool sessionClosing=false)
    {
        var target=await observer.FutureBars.AsNoTracking().SingleOrDefaultAsync(x=>x.SessionId==context.Session.Id && x.BarSeq==observation.H5TargetBarSeq,ct);
        if(target is null)return;
        var baseBar=await observer.FutureBars.AsNoTracking().SingleAsync(x=>x.SessionId==context.Session.Id && x.BarSeq==observation.StrongBaseBarSeq,ct);
        var trigger=await observer.FutureBars.AsNoTracking().SingleAsync(x=>x.SessionId==context.Session.Id && x.BarSeq==observation.TriggerBarSeq,ct);
        var ticks=await ReadCachedSourceAsync(source,context,ct);
        FinalizeFromCleanTicks(context,observation,baseBar.EndAvailableAtUtc,target.EndAvailableAtUtc,target.Close-trigger.Close,ticks,sessionClosing);
        await observer.SaveChangesAsync(ct);
        if(observation.Status==AdaptiveObservationStatus.Completed)
            logger.LogInformation("Adaptive Weak2 observation finalized: session={SessionId}, trigger={Trigger}, OI={OiGate}, H5PnL={Pnl:F2}",
                observation.SessionId,observation.TriggerBarSeq,observation.OiGatePassed,observation.PnlPoints);
    }

    /// <summary>Pure watch-only execution/OI/H5 calculation used by live persistence and independent research parity.</summary>
    public static void FinalizeFromCleanTicks(AdaptiveObserverSessionContext context,AdaptiveWeak2ObservationRow observation,
        DateTimeOffset baseEndUtc,DateTimeOffset targetEndUtc,double futuresH5Move,
        IReadOnlyDictionary<string,List<CleanObserverTick>> ticks,bool sessionClosing)
    {
        baseEndUtc=AdaptiveResearchClock.DiagnosticTime(baseEndUtc);
        targetEndUtc=AdaptiveResearchClock.DiagnosticTime(targetEndUtc);
        observation.TriggerTimestampUtc=AdaptiveResearchClock.DiagnosticTime(observation.TriggerTimestampUtc);
        var sideInstruments=context.Options.Where(x=>x.OptionType==observation.OptionSide).OrderBy(x=>x.Strike).ToArray();
        if(sideInstruments.Length==0) { MarkUnavailable(observation,"No weekly option instruments available for reversal side.");return; }
        var latest=ticks.Values.Where(x=>x.Count>0).Select(x=>x[^1].AvailableAt).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        if(latest<targetEndUtc)return;
        var firstByToken=new Dictionary<string,QuotePoint>(StringComparer.Ordinal);
        foreach(var instrument in sideInstruments)
        {
            if(!ticks.TryGetValue(instrument.Token,out var series))continue;
            var lo=0;var hi=series.Count;
            while(lo<hi) { var mid=(lo+hi)/2;if(series[mid].AvailableAt<observation.TriggerTimestampUtc)lo=mid+1;else hi=mid; }
            for(var index=lo;index<series.Count;index++)
            {
                if(ToQuote(instrument.Token,series[index]) is not { } quote)continue;
                firstByToken[instrument.Token]=quote.Point;break;
            }
        }
        // No unquoted candidate may silently change the eventual nearest-125 selection.
        if(!sessionClosing && firstByToken.Count<sideInstruments.Length)return;
        var selected=sideInstruments.Where(i=>firstByToken.TryGetValue(i.Token,out var q) && q.Ask>=PremiumMin && q.Ask<=PremiumMax)
            .Select(i=>new EntryCandidate(i,firstByToken[i.Token])).OrderBy(x=>Math.Abs(x.Quote.Ask-PremiumTarget)).ThenBy(x=>x.Instrument.Strike).FirstOrDefault();
        if(selected is null) { MarkUnavailable(observation,"No reversal-side option had first post-trigger executable Ask in ₹100–₹150.");return; }
        var call=context.Options.SingleOrDefault(x=>x.Strike==selected.Instrument.Strike && x.OptionType==OptionType.Call);
        var put=context.Options.SingleOrDefault(x=>x.Strike==selected.Instrument.Strike && x.OptionType==OptionType.Put);
        if(call is null || put is null) { MarkUnavailable(observation,"Selected strike did not have a complete CE/PE pair.");return; }
        var callClean=ticks.GetValueOrDefault(call.Token)??[];
        var putClean=ticks.GetValueOrDefault(put.Token)??[];
        var ceBase=OiAtOrBefore(callClean,baseEndUtc);var peBase=OiAtOrBefore(putClean,baseEndUtc);
        var ceTrigger=OiAtOrBefore(callClean,observation.TriggerTimestampUtc);var peTrigger=OiAtOrBefore(putClean,observation.TriggerTimestampUtc);
        var selectedClean=selected.Instrument.OptionType==OptionType.Call?callClean:putClean;
        var selectedQuotes=selectedClean.Select(x=>ToQuote(selected.Instrument.Token,x)).Where(x=>x is not null).Select(x=>x!.Value.Point).ToArray();
        var exit=selectedQuotes.FirstOrDefault(x=>x.AvailableAt>=targetEndUtc && x.Bid>0);
        if(exit is null)return;
        var bidPath=selectedQuotes.Where(x=>x.AvailableAt>=selected.Quote.AvailableAt && x.AvailableAt<=exit.AvailableAt && x.Bid>0).Select(x=>x.Bid).ToArray();
        if(bidPath.Length==0)return;
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
        observation.ExitLatencySeconds = (exit.AvailableAt - targetEndUtc).TotalSeconds;
        observation.HoldingSeconds = (exit.AvailableAt - selected.Quote.AvailableAt).TotalSeconds;
        observation.PnlPoints = exit.Bid - selected.Quote.Ask;
        observation.ReturnPct = selected.Quote.Ask != 0d
            ? 100d * observation.PnlPoints.Value / selected.Quote.Ask
            : null;
        observation.MfePointsExecutableBid = bidPath.Max() - selected.Quote.Ask;
        observation.MaePointsExecutableBid = bidPath.Min() - selected.Quote.Ask;
        observation.FuturesH5Move = futuresH5Move;
        observation.Status = AdaptiveObservationStatus.Completed;

    }

    static void MarkUnavailable(AdaptiveWeak2ObservationRow row, string reason)
    {
        row.Status = AdaptiveObservationStatus.Unavailable;
        row.UnavailableReason = reason;
    }

    static (string Token, QuotePoint Point)? ToQuote(ObserverTokenTick item) =>
        ToQuote(item.Token, item.Tick);

    static (string Token, QuotePoint Point)? ToQuote(string token, CleanObserverTick tick)
    {
        // Execution research accepts locked quotes; strict aggressor classification does not.
        if (!(tick.Bid > 0d && tick.Ask >= tick.Bid))
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

    static long? OiAtOrBefore(IReadOnlyList<CleanObserverTick> ticks, DateTimeOffset at)
    {
        var lo=0;var hi=ticks.Count;
        while(lo<hi) { var mid=(lo+hi)/2;if(ticks[mid].AvailableAt<=at)lo=mid+1;else hi=mid; }
        for(var i=lo-1;i>=0;i--)if(ticks[i].OpenInterest is { } oi)return oi;
        return null;
    }
}
