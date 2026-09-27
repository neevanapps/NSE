# 6500 Volume-Bar Metric Analysis — Predeclared First Pass

## Purpose

This analysis starts only after the corrected 6500 event-clock audit passes on all primary sessions.

It asks one question:

> Does each raw Futures metric carry repeatable information about subsequent NIFTY Futures movement
> under the audited FlatTrade feed-update clock?

This phase does **not** test option P&L, entry thresholds, stop-losses, time-of-day filters,
composites, percentile gates, adaptive weights or other strategy logic.

## Fixed candidate set

Only these four metrics are analyzed in this first pass:

1. 'TobDepthDivergence'
2. 'DepthImbalance'
3. 'OrderFlowImbalance'
4. 'BarDurationUrgency'

No metric is added or removed after seeing the output.

## Fixed forward horizons

The same three horizons are used for every metric:

- +1 later completed minimum-6500 observed-volume bar
- +2 later completed bars
- +4 later completed bars

Because the FlatTrade feed is sampled and completed bars can overshoot 6500, these are event-bar
horizons rather than exact 6500/13000/26000-contract horizons.

The analyzer therefore also records the exact cumulative 'ForwardObservedVolume' for each horizon.

## Signal-bar price controls

Every observation now records the signal bar's own:

- open,
- high,
- low,
- close,
- close-minus-open change in points,
- return,
- high-low range.

For each metric/horizon the report includes:

- Spearman(metric, future response)
- Spearman(metric, signal-bar change)
- Spearman(signal-bar change, future response)

This is a redundancy diagnostic. It is especially important for 'BarDurationUrgency', whose sign
already contains the current bar's price direction.

These correlations do not by themselves establish incremental causality; they show whether an
apparently predictive metric is closely tied to the current price move that is already observable.

## Primary statistical outputs

For each metric and +1/+2/+4 horizon:

### Pooled raw relationship

- valid observation count
- tie-aware Spearman correlation between raw metric and future Futures points
- median and P95 actual forward observed-volume horizon

Pooled bars are not treated as thousands of independent trading days. Session-level evidence below
is primary for robustness.

### Session-level relationship

For every trading session:

- N
- Spearman(metric, future response)
- Q1 count
- Q5 count
- Q5 minus Q1 mean future-point response

The summary reports:

- total session count
- sessions with a computable Spearman
- median session Spearman
- positive / negative / zero session-Spearman counts

A pooled relationship that is driven by a small number of sessions is not considered robust.

### Raw-value quintiles

Quintile cut points are computed from the metric's own raw-value distribution only. Future outcomes
are never used to choose cut points.

The same cut points are then used across horizons and robustness scopes.

For Q1 through Q5 the analyzer reports:

- N
- minimum / maximum / mean raw metric value
- mean future points
- median future points
- fraction of future outcomes greater than zero
- mean maximum-up excursion
- mean maximum-down excursion

The summary also reports Q5 minus Q1 mean future points.

Quintiles are descriptive. They are not entry thresholds and must not be converted directly into a
trading rule from this historical sample.

### DTE breakdown

For each available DTE:

- N
- Spearman correlation
- mean future points
- median future points
- fraction future-positive

DTE rows are descriptive robustness checks, not permission to choose the best historical DTE.

### Leave-one-session-out

For each metric/horizon, remove one entire trading session and recompute pooled Spearman.

The summary reports:

- minimum leave-one-session-out Spearman
- maximum leave-one-session-out Spearman
- number of leave-one-session-out runs retaining the pooled correlation's non-zero sign

This is a concentration diagnostic. It is not independent out-of-sample evidence because every
remaining session is still historical research data.

## Predeclared sampled-feed sensitivity

The primary scope is:

    AllValid

The one predeclared sensitivity scope is:

    SignalBarVolumeLt13000

The sensitivity removes only signal bars whose actual observed bar volume is >= 13000.

This rule was fixed before metric outcomes were inspected. In the audited 6500 run, only a small
minority of bars occupy this large-overshoot tail. If an apparent metric relationship disappears
when these sampled-volume jumps are removed, that is important evidence about feed sensitivity.

No other volume cutoff will be searched after seeing results.

## Spearman implementation

The analyzer uses average ranks for tied values and Pearson correlation of those ranks.

It returns no correlation when:

- fewer than three valid pairs exist, or
- either ranked series is constant.

No p-values are used as a substitute for session robustness.

## Generated files

Running:

    dotnet run --project NiftySignal.VolumeBarRevalidation -- ^
      --root=research-ticks-v2 ^
      --out=research-6500-revalidation

now regenerates the bar audit plus:

- 'metric-summary.csv'
- 'metric-sessions.csv'
- 'metric-quintiles.csv'
- 'metric-dte.csv'
- 'metric-leave-one-session-out.csv'

The console prints only the primary 'AllValid' summary.

Generated CSVs remain ignored by git.

## Interpretation discipline

After the run, a metric may be described using evidence-oriented labels such as:

- raw relation present
- weak / inconsistent
- redundant with current price move
- session-concentrated
- sensitive to sampled-volume overshoot
- no repeatable relation

No option-buying or tradeable-candidate status is assigned in this phase.

A metric should proceed to option translation only if its raw Futures relationship is coherent
across horizons, not dominated by one session, not obviously explained only by current-bar price
movement, and not dependent on the predeclared extreme-volume tail.
