# 6500 Episode / State-Entry Analysis

## Purpose

The raw price-reversal and CVD-exhaustion candidates can remain in the same sign for several
consecutive 6500 observed-volume bars. Treating every such bar as a fresh trade opportunity counts
one persistent market state many times.

This phase asks a narrower question:

> Does evaluating only the first bar of a newly-entered state produce a cleaner Futures and weekly-option response, and how does response change with exact state age?

No magnitude threshold, weighted composite, DTE filter, stop, target or confirmation-age rule is selected.

## Three separate mechanisms

### PriceReversalState

State sign:

    sign(Close - Open)

Expected reversal direction:

    positive state -> DOWN -> PE
    negative state -> UP   -> CE

### CvdExhaustionState

State sign:

    sign(FutureCvdProxyNet)

Expected reversal direction:

    positive state -> DOWN -> PE
    negative state -> UP   -> CE

### PriceCvdAgreementState

Exists only when price-move sign and CVD-proxy sign are both non-zero and equal.

    Price UP   + CVD positive -> expected DOWN -> PE
    Price DOWN + CVD negative -> expected UP   -> CE

This is boolean agreement, not a score and not a weighted composite.

## Episode definition

For each mechanism and session, an episode is a maximal consecutive run of the same non-zero state
sign.

The episode ends when:

- state becomes zero/invalid;
- sign flips;
- or, for agreement, price and CVD stop agreeing.

If the opposite valid state begins immediately, that bar starts a new episode.

No minimum episode length is required.

## Primary state-entry observation

Only the first bar of each episode is used for the primary state-entry comparison.

Compare separately:

1. PriceReversalState entries
2. CvdExhaustionState entries
3. PriceCvdAgreementState entries

For +1/+2/+4 completed Futures event bars report:

- episode count and episodes/session;
- Futures expected-direction hit rate;
- mean/median direction-aligned Futures points;
- eligible/executable option counts;
- option net-positive rate;
- mean/median executable net return;
- MFE/MAE and time-to-MFE;
- four-way Futures/option outcome counts.

The underlying outcome does not depend on option availability.

## Option translation

Reuse the already-frozen option translation without changing any execution assumption:

- nearest expiry;
- Rs100-Rs150 decision-time ask;
- smallest relative spread;
- exact token pinned;
- receipt-time causality;
- >=1 second entry latency and <=15 second entry window;
- one-lot displayed liquidity;
- one adverse tick each side;
- corrected fee model;
- +1/+2/+4 target horizons.

For an agreement bar, CurrentBarReversal and FutureCvdProxyExhaustion must produce identical option
selection/execution because they point to the same side at the same time. The analysis aborts if
their audit/outcome records differ. The CVD-labelled translation row is then used only to avoid
double-counting the identical option observation.

## Episode structure

Report for each mechanism:

- total state bars;
- episode count;
- episodes/session;
- episode-count / state-bar-count compression ratio;
- mean/median/P90 bars per episode;
- mean/median/P90 elapsed seconds;
- expected-UP vs expected-DOWN episode counts.

This quantifies how much bar-by-bar signal frequency is repeated state persistence.

## Exact state-age diagnostic

Every continuation bar is also retained with exact age:

    age 1 = first state bar
    age 2 = second consecutive state bar
    age 3 = third consecutive state bar
    ...

For each mechanism x horizon x exact age, report the same Futures and option response metrics.

No ages are grouped and no confirmation age is selected in this run.

The purpose is to distinguish patterns such as:

- strongest response at age 1, later bars already mature;
- noisy age 1, stronger age 2 confirmation;
- or no stable age relationship.

A favorable historical age is not automatically a future entry rule.

## Session and DTE views

Primary state-entry outcomes are also exported by session and DTE.

DTE remains descriptive only. The session remains the independence unit.

## Output

The existing 6500 revalidation command will additionally write:

- `episode-state-episodes.csv`
- `episode-state-entry-observations.csv`
- `episode-state-structure.csv`
- `episode-state-entry-summary.csv`
- `episode-state-entry-sessions.csv`
- `episode-state-entry-dte.csv`
- `episode-state-age-summary.csv`

## Interpretation discipline

The useful comparison is whether agreement state **entries** improve the Futures and option
response relative to price-only and CVD-only state entries while sharply reducing repeated signals.

This run does not create a sequential trading strategy. It does not choose an episode-age entry,
magnitude cutoff, DTE, stop, target, or exit rule.