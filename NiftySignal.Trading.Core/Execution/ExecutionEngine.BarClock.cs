using System.Globalization;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Entry;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Ids;
using NiftySignal.Trading.Core.Quotes;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Trading.Core.Execution;

/// <summary>
/// Bar-clocked execution (the NPT1 contract): fills happen AT completed-bar ends and at the terminal instant, at the held / frozen contract's as-of two-sided quote, with no latency, queue or
/// displayed-size model (the research fills the whole quantity at the quote). Entry fills, signal exits, excursion marks and the terminal close all run through the same books, fills, costs
/// and position records as every other variant, so the ledger, risk figures, persistence and Dashboard see no difference.
/// </summary>
public sealed partial class ExecutionEngine : IBarClockExecution
{
    private NextCompletedBarEntrySpec PolicyOf(VariantKey variant) => (NextCompletedBarEntrySpec)_variants.GetVariant(variant).Spec.EntryCondition;

    private bool IsBarClocked(VariantKey variant) => _variants.GetVariant(variant).Spec.EntryCondition is NextCompletedBarEntrySpec;

    public Position? OpenPositionOf(VariantKey variant) => BookOf(variant).Positions.FirstOrDefault(p => p.State == PositionState.Open);

    public bool RequestSignalExit(VariantKey variant, long barSeq, DateTimeOffset at)
    {
        var position = OpenPositionOf(variant);
        if (position is null || position.BarClockExitRequestedBar is not null || position.TerminalRequestedAt is not null)
        {
            return false;
        }

        position.BarClockExitRequestedBar = barSeq;
        position.BarClockExitRequestedAt = at;
        return true;
    }

    public void ObserveBarEnd(VariantKey variant, DateTimeOffset now)
    {
        foreach (var position in BookOf(variant).Positions.Where(p => p.State == PositionState.Open && p.BarClocked))
        {
            if (position.LastBidObservationAt == now || _variants.TwoSided.LastTwoSided(position.Contract.Token) is not { } quote)
            {
                continue;
            }

            ObserveBarEndBid(position, quote, now);
        }
    }

    /// <summary>
    /// Records the held contract's bid at a completed-bar end. The bid is the latest two-sided quote at or before the bar end; a quote older than the policy's age limit is still the mark (the research
    /// marks bar-end bids) but the observation is flagged as a GAP so a stale mark is always visible.
    /// </summary>
    private void ObserveBarEndBid(Position position, QuoteSnapshot quote, DateTimeOffset now)
    {
        var bid = quote.Bid!.Value;
        if (now - quote.AvailableAt > PolicyOf(position.Variant).MaxQuoteAge)
        {
            position.BidObservationGaps++;
        }

        position.BidObservations++;
        position.MaxBid = position.MaxBid is { } max ? Math.Max(max, bid) : bid;
        position.MinBid = position.MinBid is { } min ? Math.Min(min, bid) : bid;
        position.FirstBidObservationAt ??= now;
        position.LastBidObservationAt = now;
        position.LastBarEndBid = bid;
        position.LastBarEndBidAt = now;
        position.LastBarEndBidSourceId = quote.SourceId;
    }

    public void ProcessPendingExit(VariantKey variant, BarCompletedEvent bar, IEngineScheduler s)
    {
        if (_ended || OpenPositionOf(variant) is not { } position || position.BarClockExitRequestedBar is not { } requested || bar.BarSeq <= requested || position.TerminalRequestedAt is not null)
        {
            return;
        }

        var now = bar.AvailableAt;
        var quote = _variants.TwoSided.LastTwoSided(position.Contract.Token);
        if (quote is null)
        {
            _out.Add(new AuditRecord(now, variant, position.OpportunityId, "SignalExit", "Pending:NoBid", FormattableString.Invariant($"bar={bar.BarSeq} requestedBar={requested} no two-sided quote of {position.Contract.Token} yet; the exit stays pending")));
            return;
        }

        ObserveBarEndBid(position, quote, now);
        var age = now - quote.AvailableAt;
        var stale = age > PolicyOf(variant).MaxQuoteAge;
        _out.Add(new AuditRecord(now, variant, position.OpportunityId, "SignalExit", "Filling", FormattableString.Invariant($"bar={bar.BarSeq} requestedBar={requested} token={position.Contract.Token} bid={quote.Bid} bidQuoteAt={quote.AvailableAt:O} bidAge={age.TotalMilliseconds:0}ms stale={(stale ? "true" : "false")} (exit age is not gated, only flagged)")));
        FillBarClockExit(BookOf(variant), position, ExitReason.StrategyExit, quote, now, s);
    }

    public void ExecuteBarClockEntry(EntryIntent intent, QuoteSnapshot quote, DateTimeOffset at, IEngineScheduler s)
    {
        var order = new EntryOrder(intent, at, at);
        _entryOrders.Add(order);
        _entryById.Add(order.Id, order);
        var book = BookOf(intent.Variant);
        OpenPosition(book, order, intent.RequestedQuantity, quote.Ask!.Value + _x.EntrySlippage, false, quote, at, s);
        Resolve(order, EntryOutcomeKind.Filled, "Filled", s);
        Touch(book);
    }

    /// <summary>Creates the exit order and fills it at once at the given bid / instant (a fill at a bar end or at the terminal instant: no latency, no retry).</summary>
    private void FillBarClockExit(Book book, Position position, ExitReason reason, QuoteSnapshot quote, DateTimeOffset execTime, IEngineScheduler s)
    {
        var quantity = position.OpenQuantity - position.ReservedExitQuantity;
        position.ExitSeq++;
        var variant = _variants.GetVariant(position.Variant);
        var decisionId = EngineIds.Decision(new ConfigHash(variant.VariantHash), position.Variant, _tradeDate, "Exit" + reason + "@" + position.Id, quote.SourceId, position.ExitSeq);
        var orderId = EngineIds.Order(decisionId, "exit", 1);
        var order = new ExitOrder(orderId, position, reason, quantity, null, execTime);
        position.ExitOrders.Add(order);
        position.ReservedExitQuantity += quantity;
        _exitById.Add(orderId, order);
        _out.Add(new ExitOrderCreated(execTime, position.Variant, orderId, position.Id, reason, quantity, execTime, null));
        position.BarClockExitRequestedBar = null;
        position.BarClockExitRequestedAt = null;
        FillExit(book, order, quantity, quote.Bid!.Value, quote, execTime, s);
    }

    // ------------------------------------------------------------------ terminal close

    /// <summary>
    /// The terminal instant of a bar-clocked position (15:00 live). LIVE mode: close now at the held token's latest observed POSITIVE bid, record its time and age, never backdate; with no bid at all the
    /// position stays visibly pending (<see cref="MandatoryExitPending"/>) and the first later bid closes it as <see cref="ExitReason.DelayedTerminalClose"/>. HISTORICAL-PARITY mode: close at the
    /// bid of the last completed bar end seen so far, stamped at that bar end (replay verification only).
    /// </summary>
    private void TerminalClose(Book book, Position position, IEngineScheduler s)
    {
        var policy = PolicyOf(position.Variant);
        var now = s.Now;
        position.BarClockExitRequestedBar = null;                    // a pending signal exit is taken over by the terminal protocol
        position.BarClockExitRequestedAt = null;
        if (policy.TerminalMode == NextBarTerminalMode.HistoricalLastBarEnd)
        {
            if (position.LastBarEndBid is { } barBid && position.LastBarEndBidAt is { } barAt)
            {
                var barQuote = new QuoteSnapshot(position.Contract.Token, barBid, null, barAt, position.LastBarEndBidSourceId ?? 0);
                _out.Add(new AuditRecord(now, position.Variant, position.OpportunityId, "TerminalClose", "HistoricalLastBarEnd", FormattableString.Invariant($"bid={barBid} atBarEnd={barAt:O} requestedAt={now:O} (replay verification mode; not reproducible online)")));
                FillBarClockExit(book, position, ExitReason.TerminalClose, barQuote, barAt, s);
                return;
            }
        }
        else if (_variants.TwoSided.LastPositiveBid(position.Contract.Token) is { } quote)
        {
            var age = now - quote.AvailableAt;
            _out.Add(new AuditRecord(now, position.Variant, position.OpportunityId, "TerminalClose", "LiveObservedBid", FormattableString.Invariant($"bid={quote.Bid} quoteAt={quote.AvailableAt:O} quoteAge={age.TotalMilliseconds:0}ms stale={(age > policy.MaxQuoteAge ? "true" : "false")} requestedAt={now:O} fillAt={now:O} policy={policy.PolicyLabel}")));
            FillBarClockExit(book, position, ExitReason.TerminalClose, quote, now, s);
            return;
        }

        position.TerminalRequestedAt = now;
        _out.Add(new AuditRecord(now, position.Variant, position.OpportunityId, "TerminalClose", "Pending:NoUsableBid", FormattableString.Invariant($"requestedAt={now:O} no positive bid of {position.Contract.Token} has ever been observed; nothing is invented and nothing is backdated")));
        _out.Add(new MandatoryExitPending(now, position.Variant, position.Id, "terminal:" + position.Id, ExitReason.TerminalClose, position.OpenQuantity, 0, "NoUsableBid"));
    }

    /// <summary>A position whose terminal close found no bid is closed by the first positive bid that appears afterwards, recorded as a delayed terminal close with both instants.</summary>
    private void TryDelayedTerminalClose(Book book, Position position, QuoteSnapshot quote, IEngineScheduler s)
    {
        if (position.TerminalRequestedAt is not { } requested || quote.Bid is not > 0m || position.State != PositionState.Open)
        {
            return;
        }

        var now = s.Now;
        _out.Add(new AuditRecord(now, position.Variant, position.OpportunityId, "TerminalClose", "Delayed", FormattableString.Invariant($"bid={quote.Bid} quoteAt={quote.AvailableAt:O} requestedAt={requested:O} fillAt={now:O} (recorded distinctly from a terminal close at the terminal instant)")));
        FillBarClockExit(book, position, ExitReason.DelayedTerminalClose, quote, now, s);
        position.TerminalRequestedAt = null;
    }
}
