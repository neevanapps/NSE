# 6500 CVD Proxy Control Test

## Purpose

The clean second metric batch found a strong inverse relationship between
'FutureCvdProxyNet' and subsequent NIFTY Futures movement.

That relationship survived the already-frozen direction + absolute move-magnitude matching, so
the next question is narrower:

> Is CVD still informative after removing contemporaneous bar-state information that might be
> mechanically embedded in the sampled midpoint-classification proxy?

This remains an underlying-Futures diagnostic only. It does not define an option trade, entry
threshold, stop, target, time filter, composite score or production rule.

## Data and event clock

The test uses the same nine audited primary sessions and the same whole-feed-update/no-carry
minimum-6500 observed-volume event bars.

The same sensitivity is retained:

    SignalBarVolumeLt13000

No alternative bar size or overshoot cutoff is searched.

## CVD semantics

'FutureCvdProxyNet' is the existing FlatTrade sampled-feed proxy:

- take a positive cumulative-volume delta between received updates;
- require a two-sided received quote;
- classify the **entire observed delta** as buy-side when LastPrice >= current midpoint;
- otherwise classify it as sell-side;
- sum buy-classified minus sell-classified observed volume inside the bar.

This is not exchange-trade aggressor CVD. One received update can summarize many hidden exchange
trades. This test asks whether the proxy still contains forward information after controlling for
other same-bar observables.

## Existing move control remains frozen

No new price-movement matching scheme is introduced.

The exact ten direction x absolute-move cells from the duration incremental study are reused:

    DOWN Q1 ... DOWN Q5
    UP   Q1 ... UP   Q5

They are based only on:

    direction = sign(Close - Open)
    magnitude = abs(Close - Open)

All CVD/control correlations below are computed **inside those cells**.

## Additional same-bar controls

Three controls are predeclared.

### CloseLocation

    (Close - Low) / (High - Low)

for a non-zero signal-bar range.

This is known at the signal-bar close and measures where the bar finished inside its own observed
range.

It is included because the midpoint-rule CVD proxy may partly encode whether the bar finished near
its high or low rather than independent order-flow information.

### TopOfBookImbalance

The already-audited received-snapshot-weighted Bid1/Ask1 imbalance for the signal bar.

### DepthImbalance

The already-audited received-snapshot-weighted five-level bid/ask imbalance for the signal bar.

No future value enters any control.

## Fixed analysis stages

Six stages are reported. Their definitions are frozen before inspecting this test's result.

1. 'BenchmarkAllCvd'
   - all CVD-valid observations
   - no additional control
   - reproduces the existing move-matched CVD relationship

2. 'CompleteCaseNoControls'
   - only rows where CVD, CloseLocation, TOB and Depth are all available
   - no extra statistical control
   - establishes the same-sample benchmark for stages 3-6

3. 'ControlCloseLocation'
   - same complete-case rows
   - controls CloseLocation

4. 'ControlTopOfBook'
   - same complete-case rows
   - controls TopOfBookImbalance

5. 'ControlDepth'
   - same complete-case rows
   - controls DepthImbalance

6. 'ControlAllThree'
   - same complete-case rows
   - controls CloseLocation + TopOfBookImbalance + DepthImbalance simultaneously

The complete-case stages deliberately use identical observations so a correlation change is not
silently caused by a changing missing-data sample.

## Partial Spearman method

The controls are not discretized and no threshold is searched.

For each direction x move-magnitude cell:

1. tie-aware average-rank CVD;
2. tie-aware average-rank forward Futures points;
3. tie-aware average-rank each selected control;
4. center all ranked vectors;
5. build the control-vector span with modified Gram-Schmidt;
6. remove that same ranked control span from ranked CVD and ranked future outcome;
7. Pearson-correlate the two residual vectors.

With zero controls, this is ordinary tie-aware Spearman.

This is therefore a partial Spearman-style rank correlation, not a predictive regression model.

A cell needs at least 'number of controls + 3' observations and non-constant residual variation.
Otherwise its result is null rather than fabricated.

## Horizons

The existing:

- +1 completed event bar
- +2 completed event bars
- +4 completed event bars

are retained.

## Diagnostics

For the common complete-case population the analysis also reports:

- Spearman(CVD, CloseLocation)
- Spearman(CVD, TopOfBookImbalance)
- Spearman(CVD, DepthImbalance)
- Spearman(TopOfBookImbalance, DepthImbalance)

These are mechanism diagnostics only.

## Robustness

For each stage/horizon/scope:

- median cell partial Spearman across the ten frozen move cells
- positive / negative cell counts
- per-session median cell partial Spearman
- positive / negative session counts

For 'AllValid', the test also removes one full session at a time.

The frozen move-cell definitions and fixed controls are not recomputed or reselected after a
session is removed.

Leave-one-session-out is a concentration check, not independent out-of-sample evidence.

## Generated files

The existing revalidation command now also writes:

- 'cvd-control-diagnostics.csv'
- 'cvd-control-summary.csv'
- 'cvd-control-cells.csv'
- 'cvd-control-sessions.csv'
- 'cvd-control-leave-one-session-out.csv'

Generated outputs remain ignored by git.

## Interpretation

Evidence that CVD contains incremental information would look like:

- the complete-case no-control result remains close to the all-CVD benchmark;
- the negative relationship remains after CloseLocation control;
- it remains after TOB control;
- it remains after Depth control;
- and importantly, it remains after all three simultaneously;
- cell signs, session signs and leave-one-session-out remain broadly negative.

If the relationship collapses after CloseLocation, the CVD proxy is probably encoding signal-bar
price geometry more than independent flow information.

If it collapses after book controls, the apparent CVD edge is likely another representation of
contemporaneous received book state.

If it survives all three, 'CVD-proxy exhaustion/reversal' becomes materially stronger as an
underlying candidate, while still retaining the explicit sampled-feed-proxy caveat.

## Translation phase

If the CVD relationship survives the frozen price/book controls and sampled-volume sensitivity,
the next phase is not another historical CVD threshold search. The fixed translation diagnostic is
'docs/VOLUME_BAR_6500_OPTION_TRANSLATION.md', where CVD exhaustion and simple current-bar reversal
are translated independently into exact-token weekly-option responses.
