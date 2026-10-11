using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Integrity;
using NiftySignal.Trading.Core.Trading;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Trading.Core.Reporting;

public sealed record CostComponentTotals(decimal Brokerage, decimal Stt, decimal ExchangeCharge, decimal SebiFee, decimal StampDuty, decimal Gst)
{
    public decimal Total => Brokerage + Stt + ExchangeCharge + SebiFee + StampDuty + Gst;

    public static CostComponentTotals From(IEnumerable<CostBreakdown> costs)
    {
        var list = costs.ToList();
        return new CostComponentTotals(list.Sum(c => c.Brokerage), list.Sum(c => c.Stt), list.Sum(c => c.ExchangeCharge), list.Sum(c => c.SebiFee), list.Sum(c => c.StampDuty), list.Sum(c => c.Gst));
    }

    /// <summary>Total excluding the named components (used for the "without provisional components" figure).</summary>
    public decimal TotalExcluding(IEnumerable<string> componentNames)
    {
        var excluded = new HashSet<string>(componentNames, StringComparer.Ordinal);
        return (excluded.Contains("Brokerage") ? 0m : Brokerage)
            + (excluded.Contains("Stt") ? 0m : Stt)
            + (excluded.Contains("ExchangeCharge") ? 0m : ExchangeCharge)
            + (excluded.Contains("SebiFee") ? 0m : SebiFee)
            + (excluded.Contains("StampDuty") ? 0m : StampDuty)
            + (excluded.Contains("Gst") ? 0m : Gst);
    }
}

public enum ExcursionCoverage
{
    /// <summary>No eligible bid was ever observed: MFE / MAE are unavailable.</summary>
    None = 0,

    /// <summary>Observed with no gap longer than the quote-age limit.</summary>
    Complete = 1,

    /// <summary>At least one gap longer than the quote-age limit: the true extremes may lie in an unobserved stretch.</summary>
    HasGaps = 2,
}

/// <summary>
/// Bid-based excursion of one trade. Unit: premium rupees per unit of the contract (NOT multiplied by quantity), relative to the average entry FILL
/// price. MFE = max(0, highest eligible bid − avg entry); MAE = min(0, lowest eligible bid − avg entry) (so MAE is zero or negative). The observed series
/// runs from the entry fill to the close (or session end) and is not interrupted by TP1 or other partial exits; the same values at the first exit fill
/// are reported separately (<see cref="MfeAtFirstExit"/> / <see cref="MaeAtFirstExit"/>).
/// </summary>
public sealed record TradeExcursion(
    string PositionId,
    string Token,
    string Unit,
    PositionState State,
    decimal AvgEntryPrice,
    decimal? Mfe,
    decimal? Mae,
    decimal? MfePct,
    decimal? MaePct,
    int Observations,
    int Gaps,
    ExcursionCoverage Coverage,
    decimal? MfeAtFirstExit,
    decimal? MaeAtFirstExit);

public sealed record OpenPositionValuation(string PositionId, ValuationStatus Status, long OpenQuantity, decimal? Bid, TimeSpan? QuoteAge, decimal? UnrealisedGross, decimal? LiquidationValue);

/// <summary>
/// One variant's day. Closed-trade results (realised) and open / unresolved valuation (unrealised or unavailable) are kept apart. A trade is one
/// position; partial exits are not new trades. Provisional cost components are labelled and the net P&amp;L is also given without them.
/// </summary>
public sealed record VariantDailySummary
{
    /// <summary>The logical strategy family that owns the variant (family roll-ups group by this).</summary>
    public required string StrategyId { get; init; }

    public required string VariantId { get; init; }

    public required string AccountId { get; init; }

    public required string Book { get; init; }

    public required string RunId { get; init; }

    public required string ConfigHash { get; init; }

    public required string VariantHash { get; init; }

    public required string RiskProfile { get; init; }

    public required DateOnly TradeDate { get; init; }

    public required DateTimeOffset AsOf { get; init; }

    // ---- opportunities and attempts
    public int OpportunitiesCreated { get; init; }

    public int OpportunitiesStillActive { get; init; }

    public IReadOnlyDictionary<string, int> OpportunityEnds { get; init; } = new Dictionary<string, int>();

    public int EntryAttempts { get; init; }

    public IReadOnlyDictionary<string, int> EntryOrderOutcomes { get; init; } = new Dictionary<string, int>();

    /// <summary>Every rejection / blocking reason and how often it occurred (first occurrences plus folded repeats), with NoEligibleContract and terminal opportunity ends.</summary>
    public IReadOnlyDictionary<string, int> RejectionReasons { get; init; } = new Dictionary<string, int>();

    // ---- quantities
    public long RequestedQuantity { get; init; }

    public long FilledEntryQuantity { get; init; }

    public int PartialEntries { get; init; }

    // ---- trades
    public int TradesOpened { get; init; }

    public int ClosedTrades { get; init; }

    public int Tp1Exits { get; init; }

    public IReadOnlyDictionary<string, int> ExitFillsByReason { get; init; } = new Dictionary<string, int>();

    public int BelowBandFills { get; init; }

    public int LateClosures { get; init; }

    public int UnresolvedPositions { get; init; }

    // ---- accounting: closed trades only
    public decimal ClosedGrossPnl { get; init; }

    public required CostComponentTotals ClosedTradeCosts { get; init; }

    public decimal ClosedNetPnl { get; init; }

    /// <summary>Closed net P&amp;L excluding the PROVISIONAL cost components (see <see cref="ProvisionalComponents"/>).</summary>
    public decimal ClosedNetPnlExcludingProvisionalComponents { get; init; }

    // ---- accounting: everything paid so far (including open / unresolved positions)
    public required CostComponentTotals AllFillCosts { get; init; }

    // ---- open / unresolved: kept separate from realised results
    public IReadOnlyList<OpenPositionValuation> OpenValuations { get; init; } = Array.Empty<OpenPositionValuation>();

    public decimal UnrealisedGrossMarked { get; init; }

    public int UnavailableValuations { get; init; }

    // ---- daily SL and exposure: observed, never enforced in ResearchV1
    public decimal? DailySlConfigured { get; init; }

    public bool DailySlBreachObserved { get; init; }

    public decimal? WorstDailyPnl { get; init; }

    public decimal DailyPnlAsOf { get; init; }

    public bool DailyPnlConservativeMark { get; init; }

    public decimal PeakPremiumExposure { get; init; }

    public IReadOnlyList<TradeExcursion> Excursions { get; init; } = Array.Empty<TradeExcursion>();

    // ---- cost labelling
    public required string CostScheduleVersion { get; init; }

    public required string CostSourceStatus { get; init; }

    public IReadOnlyList<string> ProvisionalComponents { get; init; } = Array.Empty<string>();
}

/// <summary>Builds the per-variant daily summaries from the engine's outputs and state. Pure reporting: it changes nothing and enforces nothing.</summary>
public static class DailySummaryBuilder
{
    public static IReadOnlyList<VariantDailySummary> Build(TradingEngine engine, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var summaries = new List<VariantDailySummary>();
        var schedule = engine.Execution.CostSchedule;
        var execution = engine.Execution;
        foreach (var variant in engine.Variants.Variants)
        {
            var id = variant.Key;
            var outputs = engine.Outputs.Where(o => o.Variant == id).ToList();
            var transitions = outputs.OfType<OpportunityTransition>().ToList();
            var created = transitions.Count(t => t.From == OpportunityState.Created);
            var ends = transitions
                .Where(t => t.To is OpportunityState.Consumed or OpportunityState.Cancelled or OpportunityState.Expired or OpportunityState.Rejected)
                .GroupBy(t => $"{t.To}:{t.Reason}")
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var intents = outputs.OfType<EntryIntent>().ToList();
            var audits = outputs.OfType<AuditRecord>().ToList();
            var orderOutcomes = audits
                .Where(a => a.Stage == "EntryOrder" && a.Outcome is "Filled" or "PartiallyFilled" or "Expired" or "Cancelled")
                .GroupBy(a => a.Outcome)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var rejections = new Dictionary<string, int>(StringComparer.Ordinal);
            void Add(string key, int n) => rejections[key] = rejections.TryGetValue(key, out var v) ? v + n : n;
            foreach (var s in outputs.OfType<EvaluationSummary>().Where(s => s.Scope is "Opportunity" or "EntryOrder"))
            {
                Add(s.Reason, s.Count);
            }

            foreach (var a in audits.Where(a => a is { Stage: "ContractSelection", Outcome: "NoEligibleContract" }))
            {
                Add("NoEligibleContract", 1);
            }

            foreach (var a in audits.Where(a => a is { Stage: "Sizing" } or { Stage: "OpportunityCreate", Outcome: "SessionNotEligible" }))
            {
                Add(a.Outcome, 1);
            }

            var positions = execution.PositionsOf(id);
            var fills = outputs.OfType<FillRecord>().ToList();
            var buyFills = fills.Where(f => f.Side == TradeSide.Buy).ToList();
            var sellFills = fills.Where(f => f.Side == TradeSide.Sell).ToList();
            var requestedByOrder = intents.ToDictionary(i => i.IntentId, i => i.RequestedQuantity, StringComparer.Ordinal);
            var closed = positions.Where(p => p.State == PositionState.Closed).ToList();
            var closedIds = closed.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            var closedCosts = CostComponentTotals.From(fills.Where(f => closedIds.Contains(f.PositionId)).Select(f => f.Costs));
            var closedGross = closed.Sum(p => p.GrossRealised);
            var provisional = schedule.ProvisionalComponents;
            var open = positions.Where(p => p.OpenQuantity > 0 && p.State is PositionState.Open or PositionState.Unresolved).ToList();
            var valuations = open.Select(p =>
            {
                var v = execution.Value(p, asOf);
                decimal? unrealised = v.Status == ValuationStatus.Marked ? v.GrossValue - (p.AvgEntryPrice * p.OpenQuantity) : null;
                return new OpenPositionValuation(p.Id, v.Status, p.OpenQuantity, v.Bid, v.QuoteAge, unrealised, v.LiquidationValue);
            }).ToList();
            var (dailyPnl, conservative) = execution.DailyPnl(id, asOf);
            var (breached, worst) = execution.DailySlOf(id);

            summaries.Add(new VariantDailySummary
            {
                StrategyId = id.StrategyId,
                VariantId = id.VariantId,
                AccountId = variant.Spec.ResolveAccountId(),
                Book = execution.BookLabel(id),
                RunId = engine.RunId,
                ConfigHash = engine.Variants.ConfigHash.Value,
                VariantHash = variant.VariantHash,
                RiskProfile = $"{engine.Config.Risk.Name}/{engine.Config.Risk.Version}",
                TradeDate = engine.TradeDate,
                AsOf = asOf,
                OpportunitiesCreated = created,
                OpportunitiesStillActive = created - ends.Values.Sum(),
                OpportunityEnds = ends,
                EntryAttempts = intents.Count,
                EntryOrderOutcomes = orderOutcomes,
                RejectionReasons = rejections,
                RequestedQuantity = intents.Sum(i => (long)i.RequestedQuantity),
                FilledEntryQuantity = buyFills.Sum(f => f.Quantity),
                PartialEntries = buyFills.Count(f => requestedByOrder.TryGetValue(f.OrderId, out var requested) && f.Quantity < requested),
                TradesOpened = positions.Count,
                ClosedTrades = closed.Count,
                Tp1Exits = sellFills.Where(f => f.Reason == ExitReason.Tp1).Select(f => f.OrderId).Distinct().Count(),
                ExitFillsByReason = sellFills.Where(f => f.Reason is not null).GroupBy(f => f.Reason!.Value.ToString()).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
                BelowBandFills = buyFills.Count(f => f.BelowBand),
                LateClosures = closed.Count(p => sellFills.Any(f => f.PositionId == p.Id && f.AfterFinalClose)),
                UnresolvedPositions = positions.Count(p => p.State == PositionState.Unresolved || (p.State == PositionState.Open && p.OpenQuantity > 0 && asOf >= engine.Variants.Schedule.FinalCloseUtc)),
                ClosedGrossPnl = closedGross,
                ClosedTradeCosts = closedCosts,
                ClosedNetPnl = closedGross - closedCosts.Total,
                ClosedNetPnlExcludingProvisionalComponents = closedGross - closedCosts.TotalExcluding(provisional),
                AllFillCosts = CostComponentTotals.From(fills.Select(f => f.Costs)),
                OpenValuations = valuations,
                UnrealisedGrossMarked = valuations.Where(v => v.UnrealisedGross is not null).Sum(v => v.UnrealisedGross!.Value),
                UnavailableValuations = valuations.Count(v => v.Status != ValuationStatus.Marked),
                DailySlConfigured = variant.Spec.DailySl,
                DailySlBreachObserved = breached,
                WorstDailyPnl = worst,
                DailyPnlAsOf = dailyPnl,
                DailyPnlConservativeMark = conservative,
                PeakPremiumExposure = execution.PeakPremiumOf(id),
                Excursions = positions.Select(Excursion).ToList(),
                CostScheduleVersion = schedule.Version,
                CostSourceStatus = schedule.SourceStatus,
                ProvisionalComponents = provisional,
            });
        }

        return summaries;
    }

    private static TradeExcursion Excursion(Position p)
    {
        const string unit = "premium rupees per unit (bid - average entry fill price), not multiplied by quantity";
        if (p.BidObservations == 0 || p.MaxBid is not { } max || p.MinBid is not { } min)
        {
            return new TradeExcursion(p.Id, p.Contract.Token, unit, p.State, p.AvgEntryPrice, null, null, null, null, 0, p.BidObservationGaps, ExcursionCoverage.None, null, null);
        }

        var avg = p.AvgEntryPrice;
        var mfe = Math.Max(0m, max - avg);
        var mae = Math.Min(0m, min - avg);
        return new TradeExcursion(
            p.Id,
            p.Contract.Token,
            unit,
            p.State,
            avg,
            mfe,
            mae,
            avg == 0m ? null : 100m * mfe / avg,
            avg == 0m ? null : 100m * mae / avg,
            p.BidObservations,
            p.BidObservationGaps,
            p.BidObservationGaps == 0 ? ExcursionCoverage.Complete : ExcursionCoverage.HasGaps,
            p.MaxBidAtFirstExit is { } fm ? Math.Max(0m, fm - avg) : null,
            p.MinBidAtFirstExit is { } fn ? Math.Min(0m, fn - avg) : null);
    }
}
