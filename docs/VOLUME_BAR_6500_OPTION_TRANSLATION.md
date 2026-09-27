# 6500 Futures-to-Weekly-Option Translation

## Purpose

This phase starts only after the Futures-side mechanism work.

Two candidates are translated independently:

1. 'CurrentBarReversal'
2. 'FutureCvdProxyExhaustion'

No composite is created.

This is **not a sequential trading strategy**. Observations may overlap. The report never sums
their P&L, calculates strategy profit factor, drawdown, or optimizes a trade count. It asks a
narrower question:

> When the Futures candidate points in one direction, does the exact weekly CE/PE contract that
> could actually have been bought at that time appreciate over the next +1 / +2 / +4 Futures
> event bars?

## Frozen directional mapping

### CurrentBarReversal

Driver:

    SignalBarChange = Close - Open

Direction:

    SignalBarChange > 0 -> expect Futures DOWN -> buy PE
    SignalBarChange < 0 -> expect Futures UP   -> buy CE
    SignalBarChange = 0 -> no observation

### FutureCvdProxyExhaustion

Driver:

    FutureCvdProxyNet

Direction:

    CVD > 0 -> expect Futures DOWN -> buy PE
    CVD < 0 -> expect Futures UP   -> buy CE
    CVD = 0 -> no observation

The CVD sign is the historical relationship already established by the clean 6500 analysis. It is
not re-selected from option returns.

The CVD value remains a FlatTrade sampled-feed proxy, not true exchange aggressor CVD.

## Event horizons

Outcomes use the same completed Futures event-bar clock:

- +1 bar
- +2 bars
- +4 bars

The target decision time is the target Futures observation's 'AvailableAt', not merely its
exchange timestamp.

This preserves receipt-time causality.

## Option universe

For each research date:

- NIFTY options only
- nearest listed expiry on or after the trading date
- CE or PE determined only by the Futures candidate
- exact token must have an exported V2 tick file
- one instrument-master lot for liquidity checks and modeled fees

No DTE is excluded.

## Decision-time contract selection

The signal becomes available at the Futures bar's 'AvailableAt'.

Every candidate option token on the required side is inspected using only a quote whose effective
availability is:

    max(ExchangeTimestamp, ReceivedAt) <= signal AvailableAt

The quote must:

- be no more than 15 seconds old by effective availability;
- have positive bid and ask;
- be non-crossed;
- display at least one instrument-master lot on both bid and ask;
- have decision-time ask between Rs100 and Rs150 inclusive.

Among valid contracts, choose:

1. smallest relative spread '(Ask - Bid) / Ask';
2. then nearest strike to Futures price;
3. then token lexicographically for deterministic tie-breaking.

No future option price is used to select the token.

If none qualifies, record 'NoEligibleContract'. Do not fall back to ATM.

## Entry execution

After the token is pinned:

- wait at least one second after the signal decision;
- take the first valid quote;
- entry quote must arrive within 15 seconds of the signal decision;
- displayed bid/ask quantity must still cover one lot;
- the actual entry ask must still be Rs100-Rs150;
- otherwise record 'NoTimelyEntryQuote' or 'EntryMovedOutOfBand';
- buy fill = Ask + one instrument tick.

If the selected token moves out of the premium band, no different strike is substituted using
future information.

The exact token remains pinned for every horizon.

## Exit execution

For each +1/+2/+4 target:

- target decision = target Futures bar 'AvailableAt';
- if target occurs before the entry fill, record 'HorizonBeforeEntry';
- wait at least one second after target decision;
- use the first later valid one-lot quote on the same token;
- sell fill = Bid - one instrument tick, floored at zero.

The exit order is not backdated. The report stores the delay from target decision to actual exit
quote.

This follows the corrected research convention that an exit stays pending until a valid quote
exists rather than fabricating a close.

## Receipt-time and quote limitation

Option rows are ordered for execution by:

    max(ExchangeTimestamp, ReceivedAt), then Id

This prevents using a snapshot before it was received.

However, FlatTrade fields can be carried between updates and the export does not contain separate
field-refresh timestamps for depth. A row carrying bid/ask does not prove the exchange book
actually refreshed at that instant.

Therefore all executable returns remain **modeled quote execution**, not proof of historical fill
availability.

## Costs

The observation uses one instrument-master lot only for the modeled fee calculation.

Execution assumptions:

- one tick adverse slippage on entry;
- one tick adverse slippage on exit;
- zero brokerage;
- sell STT 0.15%;
- exchange charge 0.03503% both sides;
- SEBI fee 0.0001% both sides;
- buy stamp duty 0.003%;
- GST 18% on exchange + SEBI charges.

The calculation reuses the corrected 'ReversalResearch.Fees' implementation rather than the
legacy 'TransactionCostCalculator' STT assumption.

The report contains percentage returns, not aggregate strategy P&L.

## LTP response

For each target, the same pinned token also records the most recent LTP known at or before target
'AvailableAt'.

Report:

    LtpReturnPct =
        (TargetLtp - EntryTickLtp) / EntryTickLtp

The target LTP age is stored explicitly.

This is diagnostic response, not an executable fill.

## MFE / MAE

From actual entry availability through target bar availability, use the pinned token's LTP path
known by receipt time.

For the long option:

    MFE = max(LTP) - EntryFill
    MAE = EntryFill - min(LTP)

Both are floored at zero.

Also record:

- MFE %
- MAE %
- time from entry to first occurrence of maximum favorable LTP

These are diagnostics only.

## Four-way translation outcome

For each executable observation:

    Futures correct + option profitable
    Futures correct + option loses
    Futures wrong   + option profitable
    Futures wrong   + option loses

'Option profitable' means the one-lot modeled executable return is positive after the fixed fee
model.

A zero Futures move is kept separate as 'UnderlyingFlat'.

This directly identifies whether failure is in:

- the Futures directional relationship; or
- option translation despite correct Futures direction.

## CVD magnitude diagnostics

No CVD magnitude threshold is used.

The table stores:

- raw CVD proxy
- expanding within-session absolute CVD percentile

The expanding percentile uses only current and earlier bars in the same session. It is descriptive
and causal; no full-day future distribution is used.

The same expanding absolute percentile is also stored for each candidate's own driver.

No threshold is selected from these historical sessions.

## Output

Running the existing 6500 revalidation command also creates:

- 'option-translation-signal-audit.csv'
- 'option-translation-observations.csv'
- 'option-translation-summary.csv'
- 'option-translation-sessions.csv'
- 'option-translation-dte.csv'

The console prints only the high-level translation summary.

## Interpretation discipline

This phase can establish:

- whether the Futures direction translates to weekly-option direction;
- how often a correct Futures call still loses after spread/slippage/fees;
- whether translation changes by DTE;
- whether MFE exists but is given back by the fixed horizon;
- whether one candidate translates more cleanly than the other.

It cannot establish a production strategy because:

- observations overlap;
- every non-zero candidate observation is included;
- no entry magnitude threshold is frozen yet;
- no sequential position constraint is applied;
- no exit rule other than fixed diagnostic horizons is being optimized;
- all sessions are historical research sessions.

Only after the translation relationship is understood should a separate rule be frozen for
forward validation.

## Magnitude dose-response follow-up

The unconditional translation result is followed by the frozen descriptive intensity test in
'docs/VOLUME_BAR_6500_MAGNITUDE_DOSE_RESPONSE.md'. It uses only the already-recorded causal
expanding absolute percentile for each candidate, fixes Q1..Q5 at 20% intervals, and tests whether
Futures direction/magnitude and option economics improve monotonically with signal intensity.
No quintile becomes an entry threshold in that run.
