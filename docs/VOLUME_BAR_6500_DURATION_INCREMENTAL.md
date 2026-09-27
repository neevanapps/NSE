# 6500 Volume-Bar Duration Incremental Test

## Purpose

The first-pass metric analysis found a repeatable short-horizon reversal relationship for
'BarDurationUrgency', but that metric is also strongly correlated with the current signal bar's own
price move.

This test isolates one narrower question:

> For two minimum-6500 observed-volume bars that moved roughly the same distance in the same
> direction, does the faster bar reverse more strongly than the slower bar?

If yes, duration/speed contains incremental information beyond simple current-bar mean reversion.
If no, 'BarDurationUrgency' is mainly a transformation of the already-observable current bar move.

This remains an underlying NIFTY Futures diagnostic only. It does not define option entries,
position sizing, stop-losses, composites, thresholds or production rules.

## Frozen sample

The test uses the same nine audited primary sessions:

- 2026-09-08
- 2026-09-09
- 2026-09-10
- 2026-09-11
- 2026-09-15
- 2026-09-16
- 2026-09-17
- 2026-09-18
- 2026-09-21

The bar clock remains the audited whole-feed-update/no-carry minimum-6500 observed-volume clock.

## Eligible signal bars

A signal bar is eligible when:

- 'SignalBarChangePoints != 0'
- 'ExchangeDurationSeconds > 0'
- both values are finite

No future outcome is used to decide signal eligibility.

## Direction and move-magnitude matching

UP and DOWN signal bars are handled separately.

For each direction, absolute signal-bar move:

    abs(Close - Open)

is divided into five quintiles using only the full eligible signal-bar population.

That creates ten fixed cells:

    DOWN Q1 ... DOWN Q5
    UP   Q1 ... UP   Q5

The magnitude cell definitions are frozen before looking at +1/+2/+4 outcomes.

Within each direction x move-magnitude cell, the median exchange duration is computed from the full
eligible signal-bar population.

The speed split is then fixed:

    Fast = ExchangeDurationSeconds <= cell median
    Slow = ExchangeDurationSeconds >  cell median

The duration median is not re-optimized by horizon, session or robustness scope.

## Reversal-aligned outcome

For every eligible observation:

    ReversalAlignedFuture =
        -sign(SignalBarChangePoints) * ForwardPoints

Therefore:

- after an UP signal bar, subsequent Futures DOWN movement is positive reversal;
- after a DOWN signal bar, subsequent Futures UP movement is positive reversal.

Positive always means movement in the reversal direction.

The same +1 / +2 / +4 later completed event-bar horizons from the first-pass analysis are used.

## Cell-level outputs

For every direction x move-magnitude quintile x horizon:

- N
- Fast N
- Slow N
- Fast mean reversal points
- Slow mean reversal points
- Fast minus Slow mean reversal points
- Fast median reversal points
- Slow median reversal points
- Fast reversal hit rate
- Slow reversal hit rate
- Spearman(Duration, ReversalAlignedFuture)

Interpretation:

- positive 'Fast - Slow' means faster bars reversed more;
- negative duration/reversal Spearman means shorter duration is associated with stronger reversal.

No individual magnitude bucket is promoted into a trading threshold from this historical sample.

## Matched aggregate

The headline aggregate is not a raw pooled Fast-vs-Slow difference.

For each of the ten direction x magnitude cells, compute:

    CellDifference =
        mean(Fast reversal) - mean(Slow reversal)

Then summarize those cell differences with:

- equal-weight mean cell difference
- median cell difference

This keeps the question explicitly move-magnitude matched rather than letting a large or common
cell dominate the result.

## Session robustness

For each trading session, the same frozen global cell definitions and speed medians are used.

Within that session, Fast-Slow is calculated separately for every cell containing both groups.
The session row reports:

- N
- valid matched-cell count
- mean cell Fast-Slow reversal difference
- median cell Fast-Slow reversal difference

The summary then reports:

- median session Fast-Slow difference
- positive-session count
- negative-session count
- zero-session count

Sessions remain the primary robustness unit.

## Leave-one-session-out

For the primary 'AllValid' scope, remove one full trading session at a time.

The direction/magnitude definitions and duration medians remain frozen; they are **not recomputed**
after removing the session.

Each leave-one-session-out run reports the same equal-weight matched-cell Fast-Slow difference.

This is a concentration check only. It is not independent out-of-sample evidence.

## Sampled-feed sensitivity

The same predeclared sensitivity from the metric analysis is retained:

    SignalBarVolumeLt13000

This removes signal bars whose observed bar volume is >= 13000.

The magnitude-cell boundaries and duration medians remain those frozen from the full eligible
primary population. The sensitivity therefore changes only the included observations, not the
matching rule.

No alternative volume cutoff is searched.

## Generated files

Running the existing command:

    dotnet run --project NiftySignal.VolumeBarRevalidation -- ^
      --root=research-ticks-v2 ^
      --out=research-6500-revalidation

now also writes:

- 'duration-incremental-summary.csv'
- 'duration-incremental-cells.csv'
- 'duration-incremental-sessions.csv'
- 'duration-incremental-leave-one-session-out.csv'

The console prints the primary 'AllValid' +1/+2/+4 matched summary.

Generated output remains ignored by git.

## Interpretation

The intended decision is narrow:

**Duration adds incremental information**

when faster bars show a positive matched Fast-Slow reversal difference across many direction/move
cells, session-level differences are mostly positive, duration/reversal correlations are generally
negative, and leave-one-session-out results retain the effect.

**Current-bar move explains the result**

when Fast and Slow bars behave similarly after direction and move-magnitude matching.

**Historical concentration**

when the result is driven by one magnitude cell, one direction or a small number of sessions.

No option simulation follows automatically from a positive result. A surviving relationship would
first become an underlying-mechanism candidate for later translation research.
