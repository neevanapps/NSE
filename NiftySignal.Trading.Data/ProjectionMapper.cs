using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Trading.Data;

/// <summary>The columns of a stored trade that are filled once, by the output of its final exit.</summary>
public sealed record ClosedPositionFields(
    long ExitUtcTicks,
    string ExitSessionId,
    long ExitBarSeq,
    string FinalExitReason,
    string AvgExitPriceText,
    string? ExitQuoteBidText,
    string Tp1State,
    long Tp1FilledQuantity,
    string GrossPnlText,
    string TotalCostsText,
    string NetPnlText,
    string? MfeText,
    string? MaeText,
    string? MfePctText,
    string? MaePctText,
    string? HighestBidText,
    string? LowestBidText,
    int ExcursionObservations,
    int ExcursionGaps,
    string ExcursionCoverage,
    string? MfeAtFirstExitText,
    string? MaeAtFirstExitText);

public sealed record PositionClosure(string PositionId, long ClosedOutputSeq, ClosedPositionFields Fields);

/// <summary>Typed rows derived from the outputs of one commit batch.</summary>
public sealed record MappedProjections(
    IReadOnlyList<OrderEntity> Orders,
    IReadOnlyList<PositionEntity> Positions,
    IReadOnlyList<PositionClosure> Closures,
    IReadOnlyList<FillEntity> Fills,
    IReadOnlyList<LedgerEntryEntity> Ledger,
    IReadOnlyList<AuditEntryEntity> Audit,
    IReadOnlyList<RejectionEntity> Rejections);

/// <summary>
/// Derives the typed ownership rows (orders, positions, fills, ledger entries, audit entries, rejections) from committed outputs. They are VIEWS of the
/// outputs: each carries the sequence of the output it came from (a foreign key), duplicates no payload, and can always be rebuilt from the outputs, which stay the
/// authoritative ledger. Ownership is explicit and keyed by (StrategyId, VariantId): run → account (one independent virtual book per variant) → variant → position →
/// order → fill → ledger entry. The mapper is pure; the store persists its result in the same transaction as the step.
/// </summary>
public static class ProjectionMapper
{
    /// <param name="runId">The run the outputs belong to.</param>
    /// <param name="outputs">Outputs of one batch, in sequence order.</param>
    /// <param name="variants">The variants of the run (header), which define the account of each variant.</param>
    /// <param name="slotOfEntryOrder">Slot reserved by an entry order created in an earlier batch (an entry order and the position it opens may be committed in different steps); null when unknown.</param>
    public static MappedProjections Map(string runId, IEnumerable<OutputRecord> outputs, IReadOnlyList<VariantIdentity> variants, Func<string, int?>? slotOfEntryOrder = null)
    {
        var accounts = variants.ToDictionary(v => new VariantKey(v.StrategyId, v.VariantId), v => v.AccountId);
        var ordered = outputs.OrderBy(o => o.Seq).ToList();
        var decoded = ordered.Select(o => (Record: o, Output: OutputCodec.Decode(o.Payload))).ToList();

        var orders = new List<OrderEntity>();
        var slotOfOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (record, output) in decoded)
        {
            switch (output)
            {
                case EntryIntent intent:
                    slotOfOrder[intent.IntentId] = intent.SlotNo;
                    orders.Add(new OrderEntity
                    {
                        RunId = runId,
                        OrderId = intent.IntentId,
                        StrategyId = intent.StrategyId,
                        VariantId = intent.VariantId,
                        Kind = "Entry",
                        OpportunityId = intent.OpportunityId,
                        SlotNo = intent.SlotNo,
                        PositionId = null,
                        CreatedOutputSeq = record.Seq,
                    });
                    break;
                case ExitOrderCreated exit:
                    orders.Add(new OrderEntity { RunId = runId, OrderId = exit.OrderId, StrategyId = exit.StrategyId, VariantId = exit.VariantId, Kind = "Exit", PositionId = exit.PositionId, CreatedOutputSeq = record.Seq });
                    break;
            }
        }

        var fills = new List<FillEntity>();
        var ledger = new List<LedgerEntryEntity>();
        var firstBuyOrderOfPosition = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (record, output) in decoded)
        {
            if (output is not FillRecord fill)
            {
                continue;
            }

            if (fill.Side == TradeSide.Buy)
            {
                firstBuyOrderOfPosition.TryAdd(fill.PositionId, fill.OrderId);
            }

            fills.Add(new FillEntity
            {
                RunId = runId,
                FillId = fill.FillId,
                OrderId = fill.OrderId,
                PositionId = fill.PositionId,
                StrategyId = fill.StrategyId,
                VariantId = fill.VariantId,
                AccountId = AccountOf(accounts, fill.Variant),
                OutputSeq = record.Seq,
                Side = fill.Side.ToString(),
                Quantity = fill.Quantity,
                PriceText = DecimalText.Format(fill.Price),
                NotionalText = DecimalText.Format(fill.Notional),
            });
            foreach (var (component, amount) in ComponentsOf(fill))
            {
                ledger.Add(new LedgerEntryEntity { RunId = runId, FillId = fill.FillId, Component = component, AmountText = DecimalText.Format(amount) });
            }
        }

        var positions = new List<PositionEntity>();
        var closures = new List<PositionClosure>();
        var audit = new List<AuditEntryEntity>();
        var rejections = new List<RejectionEntity>();
        foreach (var (record, output) in decoded)
        {
            switch (output)
            {
                case PositionOpened opened:
                {
                    var slot = firstBuyOrderOfPosition.TryGetValue(opened.PositionId, out var orderId)
                        ? (slotOfOrder.TryGetValue(orderId, out var s) ? s : slotOfEntryOrder?.Invoke(orderId))
                        : null;
                    positions.Add(new PositionEntity
                    {
                        RunId = runId,
                        PositionId = opened.PositionId,
                        StrategyId = opened.StrategyId,
                        VariantId = opened.VariantId,
                        AccountId = AccountOf(accounts, opened.Variant),
                        SlotNo = slot ?? throw new InvalidOperationException($"The slot of position '{opened.PositionId}' cannot be determined: no entry fill and entry order of this batch or earlier steps names it."),
                        OpportunityId = opened.OpportunityId,
                        OpenedOutputSeq = record.Seq,
                        Direction = opened.Direction.ToString(),
                        OptionType = opened.Side == OptionSide.Call ? "CE" : "PE",
                        Token = opened.Token,
                        Symbol = opened.Symbol,
                        StrikeText = DecimalText.Format(opened.Strike),
                        ExpiryDate = opened.Expiry,
                        LotSize = opened.LotSize,
                        EntryQuantity = opened.Quantity,
                        AvgEntryPriceText = DecimalText.Format(opened.AvgEntryPrice),
                        SourceSessionId = opened.SourceSessionId,
                        EntryBarSeq = opened.EntryBarSeq,
                        EntryDecisionUtcTicks = TickTime.ToTicks(opened.EntryDecisionAt),
                        EntryFillUtcTicks = TickTime.ToTicks(opened.At),
                        EntryQuoteBidText = Text(opened.EntryQuoteBid),
                        EntryQuoteAskText = Text(opened.EntryQuoteAsk),
                        EntryQuoteUtcTicks = TickTime.ToTicks(opened.EntryQuoteAt),
                        StopLevelText = Text(opened.StopLevel),
                        Tp1LevelText = Text(opened.Tp1Level),
                        MaxHoldDeadlineUtcTicks = opened.MaxHoldDeadline is { } deadline ? TickTime.ToTicks(deadline) : null,
                    });
                    break;
                }

                case PositionClosed closed:
                {
                    var e = closed.Excursion;
                    closures.Add(new PositionClosure(
                        closed.PositionId,
                        record.Seq,
                        new ClosedPositionFields(
                            TickTime.ToTicks(closed.At),
                            closed.ExitSourceSessionId,
                            closed.ExitBarSeq,
                            closed.FinalExitReason.ToString(),
                            DecimalText.Format(closed.AvgExitPrice),
                            Text(closed.ExitQuoteBid),
                            closed.Tp1.ToString(),
                            closed.Tp1FilledQuantity,
                            DecimalText.Format(closed.GrossPnl),
                            DecimalText.Format(closed.TotalCosts),
                            DecimalText.Format(closed.NetPnl),
                            Text(e.Mfe),
                            Text(e.Mae),
                            Text(e.MfePct),
                            Text(e.MaePct),
                            Text(e.MaxBid),
                            Text(e.MinBid),
                            e.Observations,
                            e.Gaps,
                            e.Coverage.ToString(),
                            Text(e.MfeAtFirstExit),
                            Text(e.MaeAtFirstExit))));
                    break;
                }

                case AuditRecord a:
                    audit.Add(new AuditEntryEntity { RunId = runId, OutputSeq = record.Seq, StrategyId = a.StrategyId, VariantId = a.VariantId, OpportunityId = a.OpportunityId, Stage = a.Stage, Outcome = a.Outcome, Detail = a.Detail });
                    break;
                case EntryRejected r:
                    rejections.Add(new RejectionEntity
                    {
                        RunId = runId,
                        OutputSeq = record.Seq,
                        StrategyId = r.StrategyId,
                        VariantId = r.VariantId,
                        OpportunityId = r.OpportunityId,
                        DecisionId = r.DecisionId,
                        SourceSessionId = r.SourceSessionId,
                        BarSeq = r.BarSeq,
                        Stage = r.Stage,
                        Reasons = r.Reasons,
                        AtUtcTicks = TickTime.ToTicks(r.At),
                    });
                    break;
            }
        }

        return new MappedProjections(orders, positions, closures, fills, ledger, audit, rejections);
    }

    /// <summary>
    /// Writes the close columns of a stored trade. They are write-once: a position that is already closed is never changed (the store applies exactly this
    /// update with a <c>closed_output_seq IS NULL</c> guard, and a repeated identical commit finds the row already closed and does nothing).
    /// </summary>
    public static void ApplyClosure(PositionEntity position, PositionClosure closure)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(closure);
        if (position.PositionId != closure.PositionId)
        {
            throw new InvalidOperationException($"Closure of '{closure.PositionId}' applied to '{position.PositionId}'.");
        }

        if (position.ClosedOutputSeq is not null)
        {
            throw new InvalidOperationException($"Position '{position.PositionId}' is already closed (at output {position.ClosedOutputSeq}).");
        }

        var f = closure.Fields;
        position.ClosedOutputSeq = closure.ClosedOutputSeq;
        position.ExitUtcTicks = f.ExitUtcTicks;
        position.ExitSessionId = f.ExitSessionId;
        position.ExitBarSeq = f.ExitBarSeq;
        position.FinalExitReason = f.FinalExitReason;
        position.AvgExitPriceText = f.AvgExitPriceText;
        position.ExitQuoteBidText = f.ExitQuoteBidText;
        position.Tp1State = f.Tp1State;
        position.Tp1FilledQuantity = f.Tp1FilledQuantity;
        position.GrossPnlText = f.GrossPnlText;
        position.TotalCostsText = f.TotalCostsText;
        position.NetPnlText = f.NetPnlText;
        position.MfeText = f.MfeText;
        position.MaeText = f.MaeText;
        position.MfePctText = f.MfePctText;
        position.MaePctText = f.MaePctText;
        position.HighestBidText = f.HighestBidText;
        position.LowestBidText = f.LowestBidText;
        position.ExcursionObservations = f.ExcursionObservations;
        position.ExcursionGaps = f.ExcursionGaps;
        position.ExcursionCoverage = f.ExcursionCoverage;
        position.MfeAtFirstExitText = f.MfeAtFirstExitText;
        position.MaeAtFirstExitText = f.MaeAtFirstExitText;
    }

    public static IEnumerable<(string Component, decimal Amount)> ComponentsOf(FillRecord fill)
    {
        yield return ("Brokerage", fill.Costs.Brokerage);
        yield return ("Stt", fill.Costs.Stt);
        yield return ("ExchangeCharge", fill.Costs.ExchangeCharge);
        yield return ("SebiFee", fill.Costs.SebiFee);
        yield return ("StampDuty", fill.Costs.StampDuty);
        yield return ("Gst", fill.Costs.Gst);
        yield return ("Notional", fill.Notional);
    }

    private static string? Text(decimal? value) => value is { } v ? DecimalText.Format(v) : null;

    private static string AccountOf(Dictionary<VariantKey, string> accounts, VariantKey variant)
        => accounts.TryGetValue(variant, out var account) ? account : throw new InvalidOperationException($"Variant '{variant}' is not part of the run header.");
}
