# 6500 Volume-Bar Feed-Update Revalidation

## Why this revalidation exists

The earlier volume-bar metric research used 'VolumeBarPopulator -> VolumeBarBuilder'. A later
Pattern A/B research path used a different threshold-excess carry convention. The first audit run
showed that carrying threshold excess can create artificial follow-on bars when a single recorded
feed update contains more than one 6500-contract threshold of newly observed cumulative volume.

That matters because the FlatTrade stream used by this project is **not an exchange trade tape**.
Operationally it is usually only about 2-5 feed updates per second. One row can therefore summarize
many underlying trades that occurred between two received updates. We know the cumulative traded
volume changed, but we do not know the hidden sequence of trade prices/book states inside that
jump.

The first carry-aware audit made the problem visible: on 2026-09-09 a large volume jump produced a
sequence of apparent carry values at 14:35:42-14:35:44 (51350, 44850, 38350, 31850, ...). Those
were not independently observed 6500-volume market events. They were one sampled jump being
propagated across later bars.

The primary revalidation therefore uses the same whole-update/no-carry convention as the existing
'VolumeBarBuilder'. The old metric conclusions remain **UNVERIFIED_OLD_RESULT** until they are
rechecked from V2 feed updates under this audited clock.

## Locked primary bar convention

Fixed threshold: **6500 newly observed Futures contracts minimum per completed bar**.

For each received feed update:

    delta = current cumulative Futures volume - previous cumulative Futures volume
    current_bar_volume += delta

    if current_bar_volume >= 6500:
        assign the whole current feed update to this bar
        close exactly one bar
        next bar starts at volume = 0

There is:

- no fractional splitting of a feed update,
- no duplicate price/depth observation,
- no threshold-excess carry into the next bar,
- no fabricated intermediate bar for hidden exchange trades we did not receive.

A completed bar can therefore contain 6500, 7200, 10000, 30000 or more observed contracts. The
correct description is **minimum-6500 observed-volume event bar**, not an exact-6500 trade bar.

'OvershootVolume = ObservedVolume - 6500' is recorded for every completed bar so the approximation
can be audited directly.

The final incomplete bar is explicit and excluded from signal/outcome observations.

## Predeclared primary sessions

Primary sessions:

- 2026-09-08
- 2026-09-09
- 2026-09-10
- 2026-09-11
- 2026-09-15
- 2026-09-16
- 2026-09-17
- 2026-09-18
- 2026-09-21

'2026-09-04' is excluded **before metric inspection** because its first recorded NIFTY Futures
update is 09:25:35 IST rather than the 09:15 market open.

'2026-09-22/23' remain discovery-only. '2026-09-24' is consumed OOS. '2026-09-25' remains excluded
from this historical revalidation.

The first four metrics remain:

1. 'TobDepthDivergence'
2. 'DepthImbalance'
3. 'OrderFlowImbalance'
4. 'BarDurationUrgency'

No composite score, option P&L, threshold search, gate or parameter tuning belongs to this phase.

## Raw source and terminology

The harness reads the NIFTY Futures token directly from each
'research-ticks-v2/<date>/instruments.json' and loads that token's '.ndjson' file through
'OptionTickReaderV2'.

The export contains:

- deterministic 'ExchangeTimestamp, Id' ordering,
- 'ReceivedAt',
- latest observed LTP,
- exchange cumulative Futures volume,
- Futures OI,
- full top-5 bid/ask snapshot.

Research output deliberately uses **FeedUpdateCount** rather than "trade count" or "tick count".
The feed rows are broker-delivered snapshots/updates, not individual exchange trades.

Negative cumulative-volume deltas are fatal. Ordering violations are fatal. They are never clamped,
re-sorted or silently repaired.

## Mandatory parity audit

Before any metric CSV is accepted, the same V2 updates are replayed through:

    A. the new audited WholeFeedUpdateNoCarryBuilder(6500)
    B. the existing NiftySignal.Features.VolumeBarBuilder(6500)

Every exposed common field must match:

- start/end exchange timestamp,
- OHLC,
- observed bar volume,
- feed-update count,
- latest OI,
- whole-session VWAP-at-close,
- CVD proxy,
- depth imbalance,
- order-flow imbalance,
- top-of-book imbalance.

The command fails immediately on the first session with a mismatch. A valid run therefore requires
'LegacyParityMismatchCount = 0' for every primary session.

This does not make the feed complete. It proves that the old whole-update/no-carry bar convention
is reproducible from the committed V2 raw feed updates rather than depending on an unexplained
persisted dataset.

## Overshoot audit

For each session the audit reports:

- median overshoot,
- P90 / P95 / P99 overshoot (nearest-rank),
- maximum overshoot,
- maximum observed bar volume,
- count of bars with volume >= 7500,
- count >= 10000,
- count >= 13000.

These fields quantify how closely the sampled-feed event clock approximates a nominal 6500-volume
clock.

## Metric semantics

All metrics use the same bar boundaries.

'DepthImbalance' is an **observed-snapshot-weighted** average:

    per received depth update =
        (sum BidQty L1..L5 - sum AskQty L1..L5)
        / (sum BidQty L1..L5 + sum AskQty L1..L5)

    bar value = average of valid received-depth-update ratios

It is not exchange-event-weighted and not trade-weighted.

'TopOfBookImbalance' has the same shape using Bid1Qty/Ask1Qty only.

'TobDepthDivergence':

    DepthImbalance - TopOfBookImbalance

Its directional sign is not assumed.

'OrderFlowImbalance' uses the existing Cont/Kukanov/Stoikov top-of-book change formula between
**received book snapshots**. Book events that occurred between FlatTrade updates are unavailable.
Previous observed quote state persists across bar boundaries; only the bar-local sum resets.

'FutureCvdProxyNet' is explicitly a proxy. A cumulative-volume jump can contain many hidden
transactions, but the current implementation classifies that entire observed volume delta from the
received update's price/book state. It must never be described as exchange-trade CVD.

'BarDurationUrgency':

    sign(Close - Open) / exchange-duration-seconds

The timing resolution is limited by the feed update cadence. Receipt duration is exported
separately.

## Forward outcomes

'+1', '+2' and '+4' mean the close of one, two and four later **completed event bars**.

They do **not** mean exactly 6500 / 13000 / 26000 contracts because each completed bar can overshoot.
For that reason the CSV also exports:

- 'Forward1ObservedVolume'
- 'Forward2ObservedVolume'
- 'Forward4ObservedVolume'

so every observation states the actual sampled cumulative-volume horizon used.

Maximum up/down Futures excursions over each horizon are diagnostics only and are never fed into a
signal.

## Evaluation

The predeclared first-pass metric analyzer is implemented separately in
'VolumeBar6500MetricAnalysis.cs' and documented in
'docs/VOLUME_BAR_6500_METRIC_ANALYSIS.md'.

It examines the four locked raw metrics with pooled and session-level Spearman correlation,
raw-value quintiles, DTE breakdown, leave-one-session-out concentration checks, current-bar price
redundancy diagnostics, MFE/MAE-style Futures excursions and the predeclared
'SignalBarVolumeLt13000' sampled-feed sensitivity.

No metric is promoted because one percentile, day, DTE or P&L happens to look attractive.

The first question remains:

> Does the raw metric carry repeatable forward Futures information under the audited sampled-feed
> event clock?

## Running

    dotnet run --project NiftySignal.VolumeBarRevalidation -- ^
      --root=research-ticks-v2 ^
      --out=research-6500-revalidation

The output directory contains:

- 'session-audit.csv'
- 'bars-6500.csv'
- 'observations-6500.csv'
- 'metric-summary.csv'
- 'metric-sessions.csv'
- 'metric-quintiles.csv'
- 'metric-dte.csv'
- 'metric-leave-one-session-out.csv'
- 'duration-incremental-summary.csv'
- 'duration-incremental-cells.csv'
- 'duration-incremental-sessions.csv'
- 'duration-incremental-leave-one-session-out.csv'
- 'second-metric-baseline.csv'
- 'second-metric-summary.csv'
- 'second-metric-sessions.csv'
- 'second-metric-quintiles.csv'
- 'second-metric-dte.csv'
- 'second-metric-move-controlled-cells.csv'
- 'second-metric-move-controlled-sessions.csv'
- 'second-metric-leave-one-session-out.csv'
- 'second-metric-oi-states.csv'
- 'cvd-control-diagnostics.csv'
- 'cvd-control-summary.csv'
- 'cvd-control-cells.csv'
- 'cvd-control-sessions.csv'
- 'cvd-control-leave-one-session-out.csv'
- 'option-translation-signal-audit.csv'
- 'option-translation-observations.csv'
- 'option-translation-summary.csv'
- 'option-translation-sessions.csv'
- 'option-translation-dte.csv'
- 'magnitude-dose-response-summary.csv'
- 'magnitude-dose-response-sessions.csv'
- 'magnitude-dose-response-dte.csv'
- 'magnitude-dose-response-monotonicity.csv'
- 'episode-state-episodes.csv'
- 'episode-state-entry-observations.csv'
- 'episode-state-structure.csv'
- 'episode-state-entry-summary.csv'
- 'episode-state-entry-sessions.csv'
- 'episode-state-entry-dte.csv'
- 'episode-state-age-summary.csv'

The directory is regenerable and is ignored by git. Do not commit these CSVs as source.

## Status labels

Until this pass is reviewed:

- older volume-bar result: 'UNVERIFIED_OLD_RESULT'
- metric currently being rebuilt: 'REVALIDATING_6500'
- survives raw Futures tests: 'UNDERLYING_CANDIDATE'
- later survives realistic option execution: 'TRADEABLE_CANDIDATE'
- clean revalidation fails: 'FAILED_6500_REVALIDATION'
- frozen unchanged on genuinely new sessions: 'FROZEN_FORWARD_TEST'

## Second metric batch

The next locked raw-Futures revalidation batch is documented in
'docs/VOLUME_BAR_6500_SECOND_METRIC_BATCH.md'. It evaluates TOB, CVD proxy, price impact,
Futures OI buildup, TrendReversion, tick activity/efficiency and VWAP deviation while preserving
the simple current-bar reversal relationship as the benchmark and reusing the frozen direction x
move-magnitude cells for incremental-information checks.

## CVD control follow-up

The clean second-batch result for 'FutureCvdProxyNet' is followed by the locked mechanism test in
'docs/VOLUME_BAR_6500_CVD_CONTROL.md'. It reuses the same frozen direction x move-magnitude cells
and asks whether the inverse CVD/future relationship survives contemporaneous CloseLocation,
TopOfBookImbalance and DepthImbalance controls.

## Option translation follow-up

After the CVD mechanism controls, the fixed Futures-to-weekly-option diagnostic is documented in
'docs/VOLUME_BAR_6500_OPTION_TRANSLATION.md'. It translates 'CurrentBarReversal' and
'FutureCvdProxyExhaustion' independently into nearest-expiry Rs100-Rs150 CE/PE observations with
receipt-time-causal contract selection, same-token pinning, bid/ask fills, fixed one-second latency,
one-tick adverse slippage and the corrected fee model. Observations may overlap and are not summed
as strategy P&L.

## Magnitude dose-response follow-up

After unconditional option translation, the next frozen diagnostic is documented in
'docs/VOLUME_BAR_6500_MAGNITUDE_DOSE_RESPONSE.md'. It keeps 'CurrentBarReversal' and
'FutureCvdProxyExhaustion' separate and uses fixed causal expanding-percentile quintiles to test
whether stronger magnitude produces a monotonic improvement in Futures reversal and weekly-option
response. It does not choose an entry threshold or create sequential strategy P&L.

## Episode/state-entry follow-up

After the magnitude dose-response diagnostic, the next frozen test is documented in
'docs/VOLUME_BAR_6500_EPISODE_STATE.md'. It collapses maximal consecutive same-sign price,
CVD and unweighted price+CVD agreement states into episodes, evaluates only the first bar for the
primary state-entry comparison, and exports exact continuation-state age separately. No magnitude
threshold, DTE filter, weighted composite, stop, target or confirmation-age rule is selected.
