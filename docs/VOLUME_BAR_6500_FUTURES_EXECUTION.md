# 6500 Futures-Execution Analysis

## Why this phase exists

Every option-translation phase so far — weekly-option translation, magnitude dose-response,
episode/state-entry — showed a negative mean net option return on every candidate, every
magnitude quintile and every mechanism, even where the underlying directional hit rate was well
above 50% (e.g. 'PriceCvdAgreementState' at 58% underlying hit rate still produced
meanOptionNetReturnPct of -0.14% to -0.36%). Because that result was unconditionally negative
everywhere, it could not distinguish "the underlying signal has no edge" from "the underlying
signal has an edge that weekly-option decay/spread/timing destroys."

This phase removes options entirely and prices a direct NIFTY Futures round trip instead, so the
underlying edge can be judged on its own economics.

## What is measured

For each of the three episode/state mechanisms ('PriceReversalState', 'CvdExhaustionState',
'PriceCvdAgreementState') and each horizon (+1/+2/+4 bars), reuse the already-frozen
'AlignedForwardPoints' from the episode/state-entry comparison (one observation per episode, first
bar only, direction-aligned) as the gross points of a single one-lot Futures round trip: enter at
the episode's first bar, exit at the forward horizon.

No stop, target, position sizing beyond one lot, pyramiding or sequential compounding is
introduced. Each state-entry observation remains an independent single round trip, exactly as in
the frozen episode/state-entry comparison — this phase only changes what the round trip is priced
against (Futures cost, not option execution).

Net points = gross aligned points - round-trip cost, swept over a fixed cost grid in NIFTY Futures
index points:

    0.0, 0.5, 1.0, 1.5, 2.0, 3.0

0 is the frictionless upper bound. The remaining values span realistic-to-pessimistic NIFTY
Futures round-trip cost (exchange/broker charges, bid/ask spread, market-order slippage on size).

'BreakevenCostPoints' is reported directly as the mean gross aligned points per mechanism x
horizon — the round-trip cost at which mean net points crosses zero.

## Gate

A mechanism x horizon passes only if, at a fixed primary cost assumption of 1.0 point round trip:

- mean net points > 0, and
- the same sign holds in at least 7 of 9 leave-one-session-out folds (excluding one session's
  trades at a time and recomputing the mean).

1.0 point round trip is a deliberately conservative middle-of-the-grid assumption, not a claim
about actual achievable NIFTY Futures execution cost. The full cost grid is exported so the
reader can re-check the gate against whatever cost assumption they trust.

## Output

The existing 6500 revalidation command additionally writes:

- `futures-execution-summary.csv`
- `futures-execution-breakeven.csv`
- `futures-execution-sessions.csv`
- `futures-execution-leave-one-session-out.csv`

## Forward-test session 2026-09-24 (authorized re-use, 2026-09-29)

At the time this phase ran, `research-ticks-v2` had no genuinely unspent session beyond the
predeclared 9: 2026-09-22/23 were already inspected across other research threads (discovery-only),
2026-09-24 was already consumed as the OOS check for an earlier candidate batch, and 2026-09-25 is
restricted to metadata/stability checks only (see the predeclared-sessions section above and
`docs/REVERSAL_RESEARCH_2026-09-25.md`).

Given that, the user explicitly authorized re-spending 2026-09-24 on 2026-09-29 for an immediate
directional read, accepting the weaker leave-one-out guarantee that comes from reusing an
already-consumed day rather than a genuinely new one. This is a one-off compromise, not a policy
change: a fresh, never-inspected session is still required before this candidate earns anything
beyond `UNDERLYING_CANDIDATE`.

Command (bypasses weekly-option translation entirely; the futures-execution gate only needs
direction-aligned forward Futures points, which do not depend on any option chain):

    dotnet run --project NiftySignal.VolumeBarRevalidation -- ^
      --root=research-ticks-v2 ^
      --forward-test-date=2026-09-24

Result — forward-test single-session mean net points at the 1.0pt gate cost, against the primary-9
per-session range at the same cost:

| Mechanism | Horizon | Primary-9 range (median) | 2026-09-24 | Sign |
|---|---|---|---|---|
| PriceReversalState | +1 | [-0.94, 0.68] (-0.19) | -0.26 | net- |
| PriceReversalState | +2 | [-0.83, 1.08] (-0.34) | -0.26 | net- |
| PriceReversalState | +4 | [-0.77, 1.00] (0.30) | -0.41 | net- |
| CvdExhaustionState | +1 | [-1.09, 0.89] (-0.10) | -0.47 | net- |
| CvdExhaustionState | +2 | [-1.31, 1.10] (0.25) | -0.28 | net- |
| CvdExhaustionState | +4 | [-1.48, 2.13] (0.65) | -0.81 | net- |
| PriceCvdAgreementState | +1 | [-0.22, 1.90] (0.48) | +0.26 | net+ |
| PriceCvdAgreementState | +2 | [-0.15, 2.33] (0.82) | +0.20 | net+ |
| PriceCvdAgreementState | +4 | [-1.57, 2.97] (1.20) | -0.01 | ~flat |

`PriceCvdAgreementState` is the only mechanism that stays net-positive on this held-out session at
+1 and +2, though the margin shrinks from the primary-9 median (gross aligned points ~1.0-1.3 vs.
1.6-2.35 in-sample) — consistent with genuine but smaller-than-in-sample edge, not with either a
clean pass-through or a sign flip. +4 comes in essentially flat rather than the strongest horizon it
was in-sample. `PriceReversalState` and `CvdExhaustionState` are net-negative on this day at every
horizon, reinforcing that the CVD-agreement combination specifically — not either mechanism alone —
is carrying the result.

One session, and a contaminated one, is not confirmation. This is a directional read only: the
result did not collapse to a clear failure, which is enough to keep `PriceCvdAgreementState` open
pending a genuinely new session, but it is not enough to raise its status.

## Interpretation discipline

This is still a revalidation diagnostic, not a strategy. It does not select a cost assumption,
does not size a position beyond one lot, does not choose a stop/target, and does not compound
observations sequentially into equity-curve P&L. A mechanism x horizon that passes this gate is a
status upgrade to 'UNDERLYING_CANDIDATE' at most — the next step before any live consideration is
re-running the identical pipeline on a materially larger session count, per the project's rule
that a single 9-session batch is never treated as a definitive result.
