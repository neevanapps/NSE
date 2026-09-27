# 6500 Volume-Bar Second Metric Batch

## Purpose

This batch continues the clean 6500-bar revalidation after the first four metrics and the
current-move-vs-duration incremental test.

The simple current-bar reversal relationship remains the benchmark:

    SignalBarChange = Close - Open

Every candidate is first tested as a raw Futures relationship and then tested again after
controlling for the signal bar's direction and absolute move magnitude.

No option P&L, entry percentile, bar-size sweep, time-of-day gate, stop-loss, composite score or
production rule is part of this phase.

## Data and clock

The batch uses the same nine audited primary sessions and the same whole-feed-update/no-carry
minimum-6500 observed-volume bars.

FlatTrade feed rows remain sampled feed updates, not individual exchange trades. All activity
metrics that use 'FeedUpdateCount' inherit that limitation.

The same predeclared sampled-feed sensitivity is retained:

    SignalBarVolumeLt13000

No other overshoot cutoff is searched.

## Simple reversal benchmark

For every +1 / +2 / +4 completed-event-bar horizon, the report re-computes:

    Spearman(SignalBarChange, ForwardFuturesPoints)

both pooled and session-by-session.

This benchmark is printed before the second-batch metrics so a new metric is never judged without
seeing whether it adds anything beyond the simpler current-bar reversal behavior.

## Locked metric definitions

### TopOfBookImbalance

Existing received-snapshot-weighted touch imbalance:

    per received depth update =
        (Bid1Qty - Ask1Qty) / (Bid1Qty + Ask1Qty)

    bar value = average of valid received-update ratios

Positive means the observed touch is bid-heavy.

### FutureCvdProxyNet

Existing midpoint-classified cumulative-volume proxy.

For a positive cumulative-volume delta with a two-sided quote:

    LastPrice >= midpoint -> classify entire observed delta as buy
    LastPrice <  midpoint -> classify entire observed delta as sell

Bar value:

    buy-classified observed volume - sell-classified observed volume

This is explicitly **not true exchange-trade CVD**. A FlatTrade update can bundle many hidden
trades.

### PriceImpact

The existing volume-bar formulation is preserved:

    (CurrentBarClose - PreviousBarClose) / CurrentBarObservedVolume

The previous close is the immediately preceding completed 6500 bar in the same session.

Because the denominator is approximately fixed by the event clock, this metric is expected to be
highly related to current price movement; the revalidation measures that rather than assuming it.

### FutureOiBuildupSignedMagnitude

The existing 'OiBuildupClassifier' is preserved using bar-to-bar close and OI changes:

    price up,   OI up   -> LongBuildup
    price down, OI up   -> ShortBuildup
    price down, OI down -> LongUnwinding
    price up,   OI down -> ShortCovering
    either unchanged    -> Neutral

Signed raw magnitude:

    LongBuildup / ShortCovering -> +abs(OI change)
    ShortBuildup / LongUnwinding -> -abs(OI change)
    Neutral -> 0

The categorical state is also retained and reported independently rather than judging only the
collapsed signed number.

### TrendReversion15

The existing 'VolumeBarTrendReversionTracker(15)' formulation is preserved.

Input per completed bar:

    CurrentClose - PreviousClose

Over the trailing queue of up to 15 available close-to-close changes:

    persistence = net change / path length
    TrendReversion15 = -persistence

The current implementation does not require a full 15-change warm-up; this batch reproduces that
existing behavior rather than silently redefining the metric.

The un-negated value is exported separately as:

    TrendPersistence15Raw = -TrendReversion15

'TrendPersistence15Raw' is a **diagnostic only**, not a second candidate. Its purpose is to show
whether the historically selected negation is actually supported by the clean 6500 relationship.

### TickDensity

    FeedUpdateCount / ObservedVolume

'FeedUpdateCount' means received broker feed rows, not exchange trades.

### TickVelocity

    FeedUpdateCount / ExchangeDurationSeconds

Again this is feed-update velocity, not trade velocity.

### PriceEfficiency

Existing activity-feature definition:

    abs(Close - Open) / FeedUpdateCount

### Churn

Existing activity-feature definition:

    max(High - Low, epsilon) / max(abs(Close - Open), epsilon)

with the same epsilon handling from 'TickActivityFeatures'.

### VwapDeviation

    Close - whole-session cumulative Futures VWAP

The revalidation builder now independently reconstructs session VWAP from the same positive
cumulative-volume deltas:

    cumulative(price * observed volume delta)
    ------------------------------------------
           cumulative observed volume

VWAP is added to the mandatory parity audit. The run aborts if independently reconstructed
'VwapAtClose' does not match the existing 'VolumeBarBuilder(6500)' value.

## Forward horizons

Every metric is tested at:

- +1 later completed event bar
- +2 later completed event bars
- +4 later completed event bars

Actual forward observed volume remains exported because sampled-feed bars can overshoot 6500.

## Raw analysis

For each metric/horizon/scope:

- valid N
- pooled Spearman(metric, future points)
- Spearman(metric, current signal-bar move)
- Spearman(current signal-bar move, future points)
- per-session Spearman
- median session Spearman
- positive / negative session counts
- fixed raw-value quintiles Q1-Q5
- mean / median future points by quintile
- future-positive rate
- mean MaxUp / MaxDown
- DTE breakdown
- raw leave-one-session-out correlation

Quintile cut points depend only on the metric's own historical raw distribution. Future outcomes
are never used to choose them.

They are descriptive buckets, not entry thresholds.

## Move-controlled incremental analysis

The exact frozen direction x move-magnitude cells from the duration incremental study are reused:

    DOWN Q1 ... DOWN Q5
    UP   Q1 ... UP   Q5

The cells are based on:

    direction = sign(Close - Open)
    magnitude = abs(Close - Open)

No new matching grid is created for the second-batch metrics.

### Directional metrics

For:

- TopOfBookImbalance
- FutureCvdProxyNet
- PriceImpact
- FutureOiBuildupSignedMagnitude
- TrendReversion15
- TrendPersistence15Raw diagnostic
- VwapDeviation

the within-cell outcome remains raw future Futures points:

    Spearman(metric, ForwardPoints)

This asks whether the metric differentiates subsequent direction among bars that already moved
roughly the same distance in the same direction.

### Unsigned activity / efficiency metrics

For:

- TickDensity
- TickVelocity
- PriceEfficiency
- Churn

the within-cell outcome is reversal-aligned:

    ReversalAlignedFuture =
        -sign(SignalBarChange) * ForwardPoints

Positive means the future moved opposite the signal bar.

This asks, for example:

> Among two similarly sized UP bars, does higher tick velocity imply a stronger subsequent
> reversal?

Each cell reports its own Spearman. The summary uses the median cell Spearman and sign counts.

## Session robustness and leave-one-session-out

For the move-controlled analysis, every session uses the same frozen global direction/magnitude
cells.

Within each session the analyzer computes all available cell correlations and reports their
median.

For 'AllValid', one full session is removed at a time. Both are recomputed:

- raw pooled metric/future Spearman
- median move-controlled cell Spearman

The cell definitions are never recomputed after excluding a session.

This is a concentration diagnostic, not independent forward evidence.

## OI-state table

In addition to 'FutureOiBuildupSignedMagnitude', every categorical OI state is summarized
separately at +1/+2/+4:

- N
- mean future points
- median future points
- future-positive rate
- implied direction
- mean direction-aligned points
- direction-aligned hit rate

This prevents a collapsed signed magnitude from hiding materially different behavior among Long
Buildup, Short Buildup, Long Unwinding and Short Covering.

## Generated files

The existing revalidation command now also writes:

- 'second-metric-baseline.csv'
- 'second-metric-summary.csv'
- 'second-metric-sessions.csv'
- 'second-metric-quintiles.csv'
- 'second-metric-dte.csv'
- 'second-metric-move-controlled-cells.csv'
- 'second-metric-move-controlled-sessions.csv'
- 'second-metric-leave-one-session-out.csv'
- 'second-metric-oi-states.csv'

All generated CSVs remain ignored by git.

## Interpretation discipline

Possible outcomes include:

- raw relation present
- incremental relation survives current-move control
- contemporaneous / redundant with current move
- non-monotonic
- session-concentrated
- sensitive to sampled-volume overshoot
- no repeatable relation

No candidate proceeds to option translation merely because it has a non-zero pooled correlation.

The main question remains:

> Does the metric contain repeatable forward Futures information that is not already explained by
> the simple current 6500-bar reversal mechanism?
