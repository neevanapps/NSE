# 6500 Volume-Bar Raw-Tick Revalidation

## Why this revalidation exists

The earlier volume-bar metric research used 'VolumeBarPopulator -> VolumeBarBuilder'. That builder
assigns the whole threshold-crossing tick to the open bar and then resets the cadence-volume
counter to zero. The later Pattern A/B event-bar research uses 'FutureEventBarBuilder', which also
assigns the whole crossing tick to exactly one bar but **carries the threshold excess** into the
next bar's accounting balance.

Those are different event-clock semantics. That difference does not prove the older metric
findings were wrong, but it is large enough that the old 650/1300/2600 headline rankings should
not be treated as final evidence without a clean rebuild.

This track therefore labels the historical metric conclusions as **UNVERIFIED_OLD_RESULT** until
they are rechecked from raw V2 ticks.

## Predeclared first pass

Fixed event threshold: **6500 Futures contracts**. This is a structural research choice, not a
parameter sweep. Do not rerun neighboring bar sizes after seeing the result.

Primary sessions only:

- 2026-09-04
- 2026-09-08
- 2026-09-09
- 2026-09-10
- 2026-09-11
- 2026-09-15
- 2026-09-16
- 2026-09-17
- 2026-09-18
- 2026-09-21

'2026-09-22/23' remain discovery-only and are not loaded. '2026-09-24' is consumed OOS and is not
loaded. '2026-09-25' remains excluded from this first pass.

The first four metrics are:

1. 'TobDepthDivergence'
2. 'DepthImbalance'
3. 'OrderFlowImbalance'
4. 'BarDurationUrgency'

No composite score, no option trade simulation, no threshold search and no gating are part of this
phase.

## Raw-tick source

The harness reads the NIFTY Futures token directly from each
'research-ticks-v2/<date>/instruments.json' and loads that token's '.ndjson' file through
'OptionTickReaderV2'.

The export schema supplies:

- deterministic 'ExchangeTimestamp, Id' ordering,
- 'ReceivedAt',
- LTP,
- cumulative Futures volume,
- Futures OI,
- full top-5 bid/ask price and quantity.

Negative cumulative-volume deltas are fatal. Ordering violations are fatal. They are never clamped
or silently repaired.

## 6500 event-bar construction

Each raw tick belongs to exactly one bar.

When a tick takes the threshold accumulator above 6500:

    balance_before + current_tick_delta >= 6500
        -> close the current bar on that whole tick
        -> carry_out = balance_at_close - 6500
        -> the next bar starts with carry_out in its threshold-accounting balance

The closing tick itself is **not duplicated** into the next bar. Carry is accounting only; it does
not fabricate a second price/depth observation.

The bar output keeps both:

- 'RealAssignedVolume': positive cumulative-volume deltas from real ticks actually assigned to the
  bar;
- 'ThresholdCarryIn/ThresholdBalanceAtClose/ThresholdCarryOut': the separate 6500 threshold
  accounting.

This makes the convention directly auditable.

The final incomplete bar is explicitly marked 'IsFinalPartialBar=true' and is excluded from
forward-response observations.

## Metric definitions

All metrics are calculated from the same raw ticks and the same 6500 bars.

'DepthImbalance':

    per tick = (sum BidQty L1..L5 - sum AskQty L1..L5)
               / (sum BidQty L1..L5 + sum AskQty L1..L5)

    bar value = average of valid per-tick ratios

'TopOfBookImbalance' is the same shape using only Bid1Qty/Ask1Qty.

'TobDepthDivergence' is exported in the raw, untuned frame:

    DepthImbalance - TopOfBookImbalance

Its directional sign is **not assumed** in this revalidation.

'OrderFlowImbalance' reuses the existing Cont/Kukanov/Stoikov top-of-book change formula. Previous
quote state persists across bar boundaries; only the bar-local OFI sum resets.

'BarDurationUrgency' reproduces the historical formulation:

    sign(Close - Open) / exchange-duration-seconds

The report also keeps receipt duration separately; it does not silently substitute one for the
other.

## Forward outcomes

For a bar closing at T, response is measured only from later completed 6500 bars:

- '+1': next 6500-contract bar close,
- '+2': 13,000 contracts later,
- '+4': 26,000 contracts later.

The CSV also records maximum favorable up/down Futures excursion over each horizon. These are
diagnostics only and are never fed back into the signal.

## Evaluation

The first export deliberately stops before statistical selection. 'observations-6500.csv' contains
the raw metric values plus +1/+2/+4 Futures outcomes needed for the next analysis pass:

- pooled and per-session Spearman correlation,
- raw-value quintile response,
- DTE breakdown,
- MFE/MAE-style up/down excursion diagnostics.

That analysis is performed after the bar-construction audit is accepted, not mixed into the
builder implementation itself.

A result will not be promoted because one percentile, session, DTE or trade P&L looks attractive.
The first question is only: **does the raw metric carry repeatable forward Futures information?**

## Running

The implementation is isolated from live trading and the existing volume-bar database.

    dotnet run --project NiftySignal.VolumeBarRevalidation -- ^
      --root=research-ticks-v2 ^
      --out=research-6500-revalidation

Running the command creates a new, regenerable research output directory containing:

- 'session-audit.csv'
- 'bars-6500.csv'
- 'observations-6500.csv'

Do not commit those generated outputs by default. They are research artifacts, not source data.

## Status labels

Until this pass is reviewed:

- older volume-bar result: 'UNVERIFIED_OLD_RESULT'
- metric currently being rebuilt: 'REVALIDATING_6500'
- survives raw Futures tests: 'UNDERLYING_CANDIDATE'
- later survives realistic option execution: 'TRADEABLE_CANDIDATE'
- clean revalidation fails: 'FAILED_6500_REVALIDATION'
- frozen unchanged on genuinely new sessions: 'FROZEN_FORWARD_TEST'
