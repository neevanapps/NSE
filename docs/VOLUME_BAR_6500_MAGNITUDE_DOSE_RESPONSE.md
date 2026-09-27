# 6500 Magnitude Dose-Response

## Purpose

This phase follows the clean Futures-to-option translation diagnostic.

The previous run showed that applying either candidate on every non-zero event bar is too frequent
and loses after option friction. The next question is deliberately narrower:

> Does increasing causal signal magnitude produce progressively stronger Futures reversal and
> cleaner weekly-option response?

This phase does **not** choose an entry threshold.

Two candidates remain separate:

1. 'CurrentBarReversal'
2. 'FutureCvdProxyExhaustion'

No composite score is created.

## Magnitude definition

### CurrentBarReversal

Direction:

    sign(Close - Open)

Expected reversal:

    positive move -> expect DOWN -> PE
    negative move -> expect UP   -> CE

Magnitude:

    abs(Close - Open)

### FutureCvdProxyExhaustion

Direction:

    sign(FutureCvdProxyNet)

Expected reversal:

    positive CVD -> expect DOWN -> PE
    negative CVD -> expect UP   -> CE

Magnitude:

    abs(FutureCvdProxyNet)

CVD remains the existing FlatTrade sampled-feed proxy, not exchange aggressor CVD.

## Causal expanding percentile

Magnitude is not normalized with the full day's future distribution.

For each session and candidate, the percentile at bar 't' is computed only from:

    current bar + earlier bars from the same session

using the existing 'ExpandingAbsolutePercentiles' function from the option translation phase.

Therefore the magnitude rank is causal.

No minimum-history filter is introduced in this phase. Early-session ranks are naturally less
stable and remain part of the descriptive result rather than being removed after seeing outcomes.

## Frozen quintiles

The only magnitude buckets are fixed twenty-percent bins:

    Q1: 0.00 < percentile <= 0.20
    Q2: 0.20 < percentile <= 0.40
    Q3: 0.40 < percentile <= 0.60
    Q4: 0.60 < percentile <= 0.80
    Q5: 0.80 < percentile <= 1.00

The mathematical implementation also accepts percentile 0 into Q1, although the current expanding
rank construction normally produces values above zero.

The boundaries are not moved after seeing results.

## Horizons

The same completed Futures event-bar horizons are retained:

- +1
- +2
- +4

No time horizon is optimized in this phase.

## Futures dose-response

For every candidate / horizon / quintile:

- signal count
- sessions represented
- signals per represented session
- valid Futures outcome count
- expected-direction hit rate
- mean expected-direction-aligned Futures points
- median expected-direction-aligned Futures points

Aligned points are:

    ExpectedDirectionSign * ForwardFuturesPoints

so positive always means the candidate's predicted reversal direction occurred.

The underlying Futures calculation is performed from all valid candidate observations, independent
of whether an option contract was available. This avoids making the Futures dose-response depend on
option selection.

## Option dose-response

The option side reuses the already-frozen translation output unchanged:

- nearest expiry
- Rs100-Rs150 decision-time ask
- smallest relative spread
- exact token pinned
- receipt-time causality
- entry at least one second later and within 15 seconds
- one-lot displayed liquidity requirement
- one adverse tick at entry and exit
- corrected fee model
- +1/+2/+4 event-bar target exits

For every magnitude quintile report:

- eligible-entry count
- executable-outcome count
- option net-positive rate
- mean executable net return
- median executable net return
- mean diagnostic LTP return
- mean MFE %
- mean MAE %
- mean MFE-minus-MAE %
- median time-to-MFE
- four-way Futures/option outcome counts

The observations may overlap. Returns are never summed into strategy P&L.

## Four-way translation counts

Retain:

    Futures correct + option profitable
    Futures correct + option loses
    Futures wrong   + option profitable
    Futures wrong   + option loses

These show whether stronger magnitude primarily improves:

- Futures direction;
- option translation conditional on direction;
- or both.

## Monotonicity test

The purpose is not to find a single historically profitable quintile.

For each candidate and horizon, the five bucket values are tested for increasing dose-response using
Spearman correlation between:

    quintile index 1..5

and each of:

- Futures hit rate
- mean aligned Futures points
- option net-positive rate
- mean executable option net return
- median executable option net return
- mean MFE-minus-MAE percentage

Also report the number of adjacent improvements:

    Q2 > Q1
    Q3 > Q2
    Q4 > Q3
    Q5 > Q4

Only adjacent pairs with both values available are compared.

A clean dose-response would normally show positive bucket Spearman and several adjacent
improvements, not one isolated attractive bucket.

## Session robustness

The same metrics are calculated separately for every trading session and magnitude quintile.

For each monotonic metric, the report then calculates the within-session Spearman between quintile
number and that metric whenever at least three quintiles are available.

Report:

- number of sessions with a valid monotonicity statistic
- median session quintile Spearman
- positive-session count
- negative-session count

The session is still the independence unit.

## DTE breakdown

Every candidate / horizon / DTE / magnitude quintile is also reported descriptively.

No DTE is selected from this historical dataset.

A favorable DTE cell is not an entry filter unless a later, separately frozen hypothesis is
validated on new data.

## Output files

The existing 6500 revalidation command now also creates:

- 'magnitude-dose-response-summary.csv'
- 'magnitude-dose-response-sessions.csv'
- 'magnitude-dose-response-dte.csv'
- 'magnitude-dose-response-monotonicity.csv'

## Interpretation discipline

Evidence in favor of magnitude selectivity would be a broad pattern such as:

    Q1 weak
    Q2 weak/modest
    Q3 stronger
    Q4 stronger
    Q5 strongest

across Futures direction, aligned move magnitude and option economics, with similar direction across
sessions.

Evidence against magnitude selectivity would include:

- random bucket ordering;
- only one isolated profitable bucket;
- opposite ordering across sessions;
- option improvement without underlying improvement;
- or dependence on one DTE/session.

This run does not promote Q4, Q5, or any other percentile as a trading threshold.

If a stable dose-response exists, a later step may freeze a threshold or state-transition rule and
test it sequentially. That later rule must be specified before using new forward sessions.
