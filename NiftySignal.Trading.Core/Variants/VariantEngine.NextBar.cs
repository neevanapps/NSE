using System.Globalization;
using NiftySignal.Trading.Core.Entry;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Ids;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Quotes;
using NiftySignal.Trading.Core.Risk;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.Sizing;
using NiftySignal.Trading.Core.Strategies;

namespace NiftySignal.Trading.Core.Variants;

/// <summary>What the bar-clocked entry policy needs from the execution model (implemented by <see cref="ExecutionEngine"/>; set by the trading engine).</summary>
public interface IBarClockExecution
{
    /// <summary>The variant's open (not closed, not unresolved) position, or null.</summary>
    Position? OpenPositionOf(VariantKey variant);

    /// <summary>Exits first: fills a pending signal exit of the variant on this completed bar if a usable bid exists (the request must be from an EARLIER bar).</summary>
    void ProcessPendingExit(VariantKey variant, BarCompletedEvent bar, IEngineScheduler scheduler);

    /// <summary>Requests the exit of the variant's open position beginning on the NEXT completed bar. False when there is nothing to exit or an exit / terminal close is already pending.</summary>
    bool RequestSignalExit(VariantKey variant, long barSeq, DateTimeOffset at);

    /// <summary>Marks every open position of the variant at this completed bar end (excursion), except those already observed at this instant.</summary>
    void ObserveBarEnd(VariantKey variant, DateTimeOffset now);

    /// <summary>Fills the entry intent at once, at the given executable quote and instant (no latency, no queue); reports the outcome back to the variant engine.</summary>
    void ExecuteBarClockEntry(EntryIntent intent, QuoteSnapshot quote, DateTimeOffset at, IEngineScheduler scheduler);
}

public sealed partial class VariantEngine
{
    /// <summary>As-of quotes of the bar-clocked policy: the latest two-sided quote and the latest positive bid per token (see <see cref="TwoSidedQuotes"/>).</summary>
    public TwoSidedQuotes TwoSided { get; } = new();

    /// <summary>Execution services of the bar-clocked policy; set by the trading engine. Null in entry-only harnesses (a bar-clocked variant then cannot trade).</summary>
    public IBarClockExecution? BarClock { get; set; }

    internal static bool IsBarClocked(VariantState v) => v.Spec.EntryCondition is NextCompletedBarEntrySpec;

    /// <summary>
    /// One completed bar for a bar-clocked variant, in the specified order: (1) pending exit, (2) pending entry from the previous bar, (3) position marks, (4) the new signal. The strategy is a pure
    /// function of the frame; THIS method maps its decision to actions: <c>Enter</c> is the DESIRED direction (an event for a crossover, a retained target for a raw strategy), never an instruction to add.
    /// </summary>
    private void OnBarBarClocked(VariantState v, BarCompletedEvent bar, VariantDecision decision, NextCompletedBarEntrySpec policy, IEngineScheduler scheduler)
    {
        var now = bar.AvailableAt;
        var attributed = new AttributedDecision(v.Key, decision.Action, decision.Direction, decision.ReasonCode, decision.Detail, _sourceSessionId, bar.BarSeq, bar.AvailableAt);
        v.LastDecision = attributed;
        Audit(now, v, null, "StrategyDecision", $"{decision.Action}:{decision.Direction}", $"bar={bar.BarSeq} reason={decision.ReasonCode} {decision.Detail}");

        BarClock?.ProcessPendingExit(v.Key, bar, scheduler);
        if (v.Active is { State: OpportunityState.Waiting, NextBarSignalSeq: { } signalSeq } pending)
        {
            ProcessPendingEntry(v, pending, signalSeq, bar, policy, scheduler);
        }

        BarClock?.ObserveBarEnd(v.Key, now);

        if (decision.Action is not (StrategyAction.Enter or StrategyAction.Exit))
        {
            return;
        }

        var held = BarClock?.OpenPositionOf(v.Key);
        if (decision.Action == StrategyAction.Exit)
        {
            if (held is not null && BarClock!.RequestSignalExit(v.Key, bar.BarSeq, now))
            {
                Audit(now, v, null, "SignalExit", "Requested", $"bar={bar.BarSeq} position={held.Id} fills on a later completed bar at the first usable bid");
            }

            return;
        }

        var wanted = decision.Direction == StrategyDirection.Short ? OptionSide.Put : OptionSide.Call;
        if (held is not null)
        {
            if (held.Contract.Side == wanted)
            {
                Audit(now, v, null, "SignalEntry", "SameSideNoAdd", $"bar={bar.BarSeq} the strategy already holds a {held.Contract.Side} position {held.Id}");
                return;
            }

            if (BarClock!.RequestSignalExit(v.Key, bar.BarSeq, now))
            {
                Audit(now, v, null, "SignalExit", "Requested", $"bar={bar.BarSeq} opposite signal; position={held.Id} fills on a later completed bar at the first usable bid");
            }
        }

        CreatePendingEntry(v, bar, wanted, attributed, policy);
    }

    private void CreatePendingEntry(VariantState v, BarCompletedEvent bar, OptionSide side, AttributedDecision attributed, NextCompletedBarEntrySpec policy)
    {
        var now = bar.AvailableAt;
        if (_entryClosed || !Schedule.IsEntryEligible(now))
        {
            Audit(now, v, null, "SignalEntry", "WindowClosed", $"bar={bar.BarSeq} no entry can fill after the signal bar {bar.BarSeq} (entry window closed at the signal)");
            Reject(v, null, now, new[] { WindowReason(now) }, "Window", "Bar", bar.BarSeq);
            return;
        }

        if (bar.Frame is not { } frame)
        {
            Audit(now, v, null, "SignalEntry", "NoFrame", $"bar={bar.BarSeq} the bar carries no strategy frame");
            return;
        }

        var gateReasons = EntryGate?.Evaluate(v.Key, now, 0m, null) ?? Array.Empty<RejectionReason>();
        if (gateReasons.Count > 0)
        {
            Audit(now, v, null, "SignalEntry", "Blocked:" + RejectionReasons.NoteName(gateReasons[0]), $"bar={bar.BarSeq} new entries are blocked for this scope");
            Reject(v, null, now, gateReasons, "Gate", "Bar", bar.BarSeq);
            return;
        }

        var episode = FormattableString.Invariant($"b{bar.BarSeq}");
        var id = EngineIds.Opportunity(v.Key, new ConfigHash(v.VariantHash), _tradeDate, episode);
        v.Valid = true;
        v.EpisodeSide = side;
        v.EpisodeId = episode;
        v.EpisodeStartBarSeq = bar.BarSeq;
        v.EpisodeSpent = false;
        v.EpisodeBlockNoted = false;
        v.EpisodeRejectionKey = null;
        v.EpisodeDecision = attributed;
        var opportunity = new Opportunity(id, v.Key, episode, bar.BarSeq, side, now, attributed) { NextBarSignalSeq = bar.BarSeq };
        v.Active = opportunity;
        _allOpportunities.Add(opportunity);
        Transition(v, opportunity, OpportunityState.AwaitingContract, now, "SignalBar");

        opportunity.SelectionAttempts++;
        var (selected, ranking) = SelectFrameContract(frame, side, now, policy);
        if (selected is null)
        {
            Audit(now, v, opportunity.Id, "ContractSelection", "NoEligibleContract", $"bar={bar.BarSeq} band centre={frame.BandCenterStrike?.ToString("R", CultureInfo.InvariantCulture) ?? "~"} expiry={frame.BandExpiry:yyyy-MM-dd} {ranking}");
            Reject(v, opportunity, now, new[] { RejectionReason.NoEligibleContract }, "Contract", "Bar", bar.BarSeq);
            Terminal(v, opportunity, OpportunityState.Rejected, now, "NoEligibleContract");
            return;
        }

        opportunity.Contract = selected;
        Audit(now, v, opportunity.Id, "ContractSelection", "Selected", $"bar={bar.BarSeq} token={selected.Token} symbol={selected.Symbol} strike={selected.Strike} expiry={selected.Expiry:yyyy-MM-dd} side={selected.Side} centre={frame.BandCenterStrike?.ToString("R", CultureInfo.InvariantCulture)} ranking=[{ranking}]");
        Transition(v, opportunity, OpportunityState.Waiting, now, "ContractFrozenForNextBar");
    }

    /// <summary>
    /// Signal-bar contract selection (the NPT1 rule): matching side and exactly the band expiry of the finalized frame; signal-bar ask inside [AskMin, AskMax] (inclusive) and quote age inside [0, MaxQuoteAge]
    /// (inclusive) on the contract's latest TWO-SIDED quote; ranked by nearest strike to the band centre, then |ask - AskTarget|, then the lower strike, then the token. Returns the winner and the full ranking text.
    /// </summary>
    private (OptionContract? Selected, string Ranking) SelectFrameContract(AdaptiveStrategyFrame frame, OptionSide side, DateTimeOffset now, NextCompletedBarEntrySpec policy)
    {
        if (frame.BandCenterStrike is not { } centerValue || frame.BandExpiry is not { } expiry)
        {
            return (null, "band unavailable: " + (frame.BandUnavailableReason ?? "no band centre or expiry"));
        }

        var center = Convert.ToDecimal(centerValue, CultureInfo.InvariantCulture);
        var band = frame.CandidateStrikes is { Length: > 0 } strikeText
            ? strikeText.Split('|').Select(x => decimal.Parse(x, NumberStyles.Float, CultureInfo.InvariantCulture)).ToHashSet()
            : null;
        var rows = new List<(OptionContract Contract, decimal Ask, TimeSpan Age, string Verdict)>();
        foreach (var contract in _universe.Where(c => c.Side == side && c.Expiry == expiry && (band is null || band.Contains(c.Strike))))
        {
            var quote = TwoSided.LastTwoSided(contract.Token);
            if (quote is null)
            {
                rows.Add((contract, 0m, TimeSpan.Zero, "NoQuote"));
                continue;
            }

            var age = now - quote.AvailableAt;
            var ask = quote.Ask!.Value;
            var verdict = age < TimeSpan.Zero || age > policy.MaxQuoteAge ? "QuoteAge" : ask < policy.AskMin || ask > policy.AskMax ? "AskOutsideBand" : "Eligible";
            rows.Add((contract, ask, age, verdict));
        }

        var eligible = rows.Where(r => r.Verdict == "Eligible")
            .OrderBy(r => Math.Abs(r.Contract.Strike - center))
            .ThenBy(r => Math.Abs(r.Ask - policy.AskTarget))
            .ThenBy(r => r.Contract.Strike)
            .ThenBy(r => r.Contract.Token, StringComparer.Ordinal)
            .ToList();
        var text = string.Join(
            ";",
            eligible.Select((r, i) => FormattableString.Invariant($"#{i + 1} {r.Contract.Token}|{r.Contract.Strike}|ask={r.Ask}|age={r.Age.TotalMilliseconds:0}ms"))
                .Concat(rows.Where(r => r.Verdict != "Eligible").OrderBy(r => r.Contract.Token, StringComparer.Ordinal).Select(r => FormattableString.Invariant($"x {r.Contract.Token}|{r.Contract.Strike}|{r.Verdict}|ask={r.Ask}|age={r.Age.TotalMilliseconds:0}ms"))));
        return (eligible.Count == 0 ? null : eligible[0].Contract, text);
    }

    private void ProcessPendingEntry(VariantState v, Opportunity opportunity, long signalSeq, BarCompletedEvent bar, NextCompletedBarEntrySpec policy, IEngineScheduler scheduler)
    {
        var now = bar.AvailableAt;
        var contract = opportunity.Contract!;
        void Cancel(OpportunityState terminal, string reason, string detail)
        {
            Audit(now, v, opportunity.Id, "NextBarEntry", "Cancelled:" + reason, $"signalBar={signalSeq} fillBar={bar.BarSeq} token={contract.Token} {detail}");
            Terminal(v, opportunity, terminal, now, reason);
        }

        if (bar.BarSeq != signalSeq + 1)
        {
            Cancel(OpportunityState.Cancelled, "BarSequenceGap", $"the completed bar after {signalSeq} was {bar.BarSeq}");
            return;
        }

        if (_entryClosed || !Schedule.IsEntryEligible(now))
        {
            Reject(v, opportunity, now, new[] { WindowReason(now) }, "Window", "Bar", bar.BarSeq);
            Cancel(OpportunityState.Expired, "OutsideEntryWindow", $"fill instant {now:O} is outside the entry window");
            return;
        }

        var quote = TwoSided.LastTwoSided(contract.Token);
        if (quote is null)
        {
            Reject(v, opportunity, now, new[] { RejectionReason.NoExecutableAsk }, "Quote", "Bar", bar.BarSeq);
            Cancel(OpportunityState.Cancelled, "NoQuote", "no two-sided quote");
            return;
        }

        var age = now - quote.AvailableAt;
        if (age < TimeSpan.Zero || age > policy.MaxQuoteAge)
        {
            Reject(v, opportunity, now, new[] { RejectionReason.NoExecutableAsk }, "Quote", "Bar", bar.BarSeq);
            Cancel(OpportunityState.Cancelled, "QuoteStale", FormattableString.Invariant($"ask={quote.Ask} age={age.TotalMilliseconds:0}ms limit={policy.MaxQuoteAge.TotalMilliseconds:0}ms"));
            return;
        }

        var ask = quote.Ask!.Value;
        if (ask < policy.AskMin || ask > policy.AskMax)
        {
            Reject(v, opportunity, now, new[] { RejectionReason.PremiumOutsideBand }, "Band", "Bar", bar.BarSeq);
            Cancel(OpportunityState.Cancelled, ask < policy.AskMin ? "AskBelowBand" : "AskAboveBand", FormattableString.Invariant($"ask={ask} band=[{policy.AskMin},{policy.AskMax}]"));
            return;
        }

        var sizing = Sizer.Size(v.Spec.Sizing, contract.LotSize, ask, _costModel);
        if (sizing.Status != SizingStatus.Ok)
        {
            Audit(now, v, opportunity.Id, "Sizing", "InvalidQuantity", sizing.Detail);
            Cancel(OpportunityState.Rejected, "InvalidQuantity", sizing.Detail);
            return;
        }

        var slot = v.Slots.FirstOrDefault(s => s.State == SlotState.Free);
        if (slot is null || BarClock?.OpenPositionOf(v.Key) is not null)
        {
            Reject(v, opportunity, now, new[] { RejectionReason.VariantCapacity }, "Capacity", "Bar", bar.BarSeq);
            Cancel(OpportunityState.Cancelled, "OldPositionStillOpen", "the strategy's own position has not exited yet");
            return;
        }

        var premium = (ask + _config.Execution.EntrySlippage) * sizing.Quantity;
        var gateReasons = EntryGate?.Evaluate(v.Key, now, premium, opportunity.Id) ?? Array.Empty<RejectionReason>();
        if (gateReasons.Count > 0)
        {
            Reject(v, opportunity, now, gateReasons, "Gate", "Bar", bar.BarSeq);
            Cancel(OpportunityState.Cancelled, "Gate:" + RejectionReasons.NoteName(gateReasons[0]), "new entries are blocked for this scope");
            return;
        }

        IssueIntent(v, opportunity, slot, quote, ConditionResult.Satisfied("NextCompletedBar", ask), sizing, now, "NextBar", bar.BarSeq);
        opportunity.IntentDecision = opportunity.EntryDecision;                       // the position records the SIGNAL bar, not the fill bar
        var intent = _outputs.OfType<EntryIntent>().Last(i => i.OpportunityId == opportunity.Id);
        Audit(now, v, opportunity.Id, "NextBarEntry", "Filling", FormattableString.Invariant($"signalBar={signalSeq} fillBar={bar.BarSeq} token={contract.Token} ask={ask} askAge={age.TotalMilliseconds:0}ms qty={sizing.Quantity}"));
        BarClock!.ExecuteBarClockEntry(intent, quote, now, scheduler);
    }
}
