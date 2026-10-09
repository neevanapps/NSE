using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Trading.Data;

public sealed record PositionClosure(string PositionId, long ClosedOutputSeq);

/// <summary>Typed rows derived from the outputs of one commit batch.</summary>
public sealed record MappedProjections(
    IReadOnlyList<OrderEntity> Orders,
    IReadOnlyList<PositionEntity> Positions,
    IReadOnlyList<PositionClosure> Closures,
    IReadOnlyList<FillEntity> Fills,
    IReadOnlyList<LedgerEntryEntity> Ledger,
    IReadOnlyList<AuditEntryEntity> Audit);

/// <summary>
/// Derives the typed ownership rows (orders, positions, fills, ledger entries, audit entries) from committed outputs. They are VIEWS of the outputs: each
/// carries the sequence of the output it came from (a foreign key), duplicates no payload, and can always be rebuilt from the outputs, which stay the
/// authoritative ledger. Ownership is explicit: run → account (one independent virtual book per variant) → variant → position → order → fill → ledger
/// entry. The mapper is pure; the store (CP6.3) persists its result in the same transaction as the step.
/// </summary>
public static class ProjectionMapper
{
    /// <param name="runId">The run the outputs belong to.</param>
    /// <param name="outputs">Outputs of one batch, in sequence order.</param>
    /// <param name="variants">The variants of the run (header), which define the account of each variant.</param>
    /// <param name="slotOfEntryOrder">Slot reserved by an entry order created in an earlier batch (an entry order and the position it opens may be committed in different steps); null when unknown.</param>
    public static MappedProjections Map(string runId, IEnumerable<OutputRecord> outputs, IReadOnlyList<VariantIdentity> variants, Func<string, int?>? slotOfEntryOrder = null)
    {
        var accounts = variants.ToDictionary(v => v.VariantId, v => v.AccountId, StringComparer.Ordinal);
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
                    orders.Add(new OrderEntity { RunId = runId, OrderId = intent.IntentId, VariantId = intent.VariantId, Kind = "Entry", OpportunityId = intent.OpportunityId, SlotNo = intent.SlotNo, PositionId = null, CreatedOutputSeq = record.Seq });
                    break;
                case ExitOrderCreated exit:
                    orders.Add(new OrderEntity { RunId = runId, OrderId = exit.OrderId, VariantId = exit.VariantId, Kind = "Exit", PositionId = exit.PositionId, CreatedOutputSeq = record.Seq });
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
                VariantId = fill.VariantId,
                AccountId = AccountOf(accounts, fill.VariantId),
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
                        VariantId = opened.VariantId,
                        AccountId = AccountOf(accounts, opened.VariantId),
                        SlotNo = slot ?? throw new InvalidOperationException($"The slot of position '{opened.PositionId}' cannot be determined: no entry fill and entry order of this batch or earlier steps explains it."),
                        OpportunityId = opened.OpportunityId,
                        Token = opened.Token,
                        OpenedOutputSeq = record.Seq,
                    });
                    break;
                }

                case PositionClosed closed:
                    closures.Add(new PositionClosure(closed.PositionId, record.Seq));
                    break;
                case AuditRecord a:
                    audit.Add(new AuditEntryEntity { RunId = runId, OutputSeq = record.Seq, VariantId = a.VariantId, OpportunityId = a.OpportunityId, Stage = a.Stage, Outcome = a.Outcome, Detail = a.Detail });
                    break;
            }
        }

        return new MappedProjections(orders, positions, closures, fills, ledger, audit);
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

    private static string AccountOf(Dictionary<string, string> accounts, string variantId)
        => accounts.TryGetValue(variantId, out var account) ? account : throw new InvalidOperationException($"Variant '{variantId}' is not part of the run header.");
}
