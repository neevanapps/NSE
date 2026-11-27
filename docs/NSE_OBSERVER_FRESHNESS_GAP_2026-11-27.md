# Observer processing lag and paper entries: investigation, reproduction, proposed fix

27 November 2026. Investigated and reproduced on tree `aa906ab302ce7e770d03c1c4002aa40e5a15808e`.

**Update, same day: the owner approved the fix; it is implemented on tree `45008e381918ec091c37b1b9c861ea9a1fa03630`.** Results, changed files and limits: readiness report section 13. The investigation below describes the earlier tree. Differences from the proposal: the session future's ticks are included; a completed but unevaluated bar also blocks (after 5 s); the position must not be later than the clock or older than the ticks Execution has read. The reproduction tests were replaced by `NiftySignal.Tests/PaperEngineV2/ObserverFreshnessTests.cs`; the original file is kept as evidence.

## Verdict

**The gap is confirmed.** While the Observer is `Live` with a fresh heartbeat and Execution's own quotes are fresh, Execution does not know how far the Observer has processed, and can open a paper entry on a bar that is minutes old.

## Reproduction

- Test file (not in the release tree): `docs/overnight/freshness/ObserverLagReproductionTests.cs`.
- Run on a scratch copy of the certified tree plus that file, `D:\perf-scratch\freshness\aa906ab`; log `D:\perf-scratch\freshness\test.log`. **8 passed, 0 failed.** A pass means the behaviour exists.
- Uses the real `ProtectionPolicy`, the real `StrategyRuntime` and the four shipped strategy files, with only their entry rules replaced by the test fixture's rules. In memory, no database.
- **Not exercised:** `ExecutionCoordinator` with `DbExecutionFeed` against a database. That path was read in code only (`ExecutionCoordinator.cs:109-119`: same gate, averages of the last processed bar, applied every cycle).

| Test | Strategies | Result |
|---|---|---|
| Waiting 5-bar-average intent, no bar for 10 minutes, then pullback | both AVG5 | order placed and filled |
| Opposite signal, held contract executable only 5 minutes later, no further bar | both immediate | exit filled, opposite order placed and filled |
| Position open, no further bar | all four | partial target fills; 15:08 liquidation fills; a fall from about 126 to 50 requests no exit |

## The five questions

**1. Can a pending Avg5 entry open while processing is far behind?** Yes (reproduced). It places against the contract average ending at the last processed bar.

**2. Do existing values give protection?**
- `MaxProcessingLag` (5 s) is checked only when a frame arrives (`Consumer.cs:206-210`). No frame, no check.
- `LastProcessedSourceAvailableAtUtc` is stored by the Observer, normally within about a second, but Execution reads only status and heartbeat (`DbExecutionFeed.cs:40`).
- No source tick watermark is compared anywhere in Execution.
- The latest raw tick is already read by Execution, but only feeds the "market data is stale" check (10 s), which says nothing about the Observer.
- So the values needed exist; none is connected.

**3. Do all four strategies check market-state freshness just before entry?** No. All use one path: the last frame's permission (`StrategyRuntime.cs:185-187, 367`) plus the safety gate, which has no processing-lag input. The pending-order fill checks quote age, price, spread, capital and risk only. Immediate strategies are exposed through the waiting reversal intent (reproduced) and, by code reading, through an order already pending (60 s lifetime).

**4. Do exits continue?**

| Exit | During lag |
|---|---|
| Pending exit retries, partial profit target | continue (quotes only) |
| 15:08 terminal liquidation | continues (clock and quotes) |
| Protective flatten | works, but lag does not trigger it: it needs a heartbeat older than 30 s, a failed or missing Observer |
| Opposite-signal exit | **stops**: needs a new bar |
| Stop-loss, trailing stop, maximum hold | **none configured** in the four strategies |
| Daily loss limit | realized losses only; blocks entries, liquidates nothing |

A position held during a backlog therefore has no loss-based exit until bars resume or 15:08.

**5. Quiet market or backlog?** Completed-bar age cannot tell them apart. Unprocessed ticks can:
- Quiet: no tick newer than the Observer's watermark, or only very recent ones.
- Backlog: a tick Execution has already read, newer than the watermark, older than 5 s.

"Now minus watermark" alone is not enough: it would block after any quiet spell. "Latest tick minus watermark" would block on the first tick after a quiet spell.

## Proposed smallest fix (as proposed; implemented with the differences above)

Rule: **block new entries while the oldest option tick not yet processed by the Observer is older than the existing `MaxProcessingLag` (5 s).**

1. `ProtectionPolicy.ObserverState`: add the watermark; `DbExecutionFeed.cs:40` fills it from `LastProcessedSourceAvailableAtUtc`.
2. `ExecutionCoordinator.Observe`: keep availability times of option ticks (not VIX) newer than the watermark; drop them as it advances; pass the oldest into `ProtectionContext`.
3. `ProtectionPolicy`: one more `EntryBlock` reason, "observer processing is behind". Not `Invalidate`, not `Flatten`.

No new service, table, column or setting. The block is part of the journaled safety state, so replay stays deterministic. A missing watermark with unprocessed ticks counts as behind.

Effects to accept before approving:
- A waiting Avg5 intent survives the block; an outstanding order is cancelled.
- An immediate strategy's waiting reversal is **consumed** by the block (existing rule for temporary blocks).
- Open positions are not flattened. Adding a lag-triggered flatten or a stop-loss would change strategy behaviour and is not proposed.
- Normal margin: stable cutoff 2 s, poll 0.25 s, row write up to 1 s, so about 3.3 s against 5 s. **Not measured on the VM**; a slower Observer would cause blocked entries, visibly.

To verify during implementation: that every tick Execution accepts also advances the Observer's watermark (same normalizer type, but separate instances and start points); otherwise a trailing tick could hold a brief false block.

Tests needed: turn the two entry reproductions into "does not open" through the coordinator; quiet market stays unblocked; null watermark blocks; replay after restart matches; exits unaffected.

## Cost of approving

The source tree changes, so the certification run and the recovery benchmarks recorded for `aa906ab` no longer certify the new tree. Builds, the full test suite and certification must be repeated before packaging. The change is in ExecutionService only; recovery code is untouched, which the owner may weigh when deciding whether to repeat the benchmarks.

## If not approved for 30 November

Paper only, no money at risk. The exposure is that a paper entry during an Observer backlog is recorded as if it were valid, which pollutes the forward-test evidence. A manual check: treat any entry whose decision bar is much older than the order time as suspect.
