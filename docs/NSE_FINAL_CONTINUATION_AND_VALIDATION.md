NiftySignal — Final implementation continuation and validation
Continue on the existing branch `feature/production-reliability-strategy-enhancement`.
Preserve all completed implementation work.
The original mandate and A1–A8 decisions remain authoritative, subject to the clarifications below.
1. Resolve the four outstanding questions
Q1 — Delayed immediate-entry contract selection
Approved.
Select the eligible contract using the latest verified market frame, but evaluate execution against a fresh executable live quote.
Revalidate Rule 8, spread, quote freshness, capital and all mandatory entry gates at execution.
Retain the configured immediate-entry order lifetime.
Do not transform the immediate-entry strategies into indefinitely waiting strategies.
Do not introduce look-ahead or backdated fills.
Q2 — Entry blocks and pending intents
Not approved as a blanket cancellation rule.
Distinguish between:

* Pending directional entry intent.
* Actual executable order intent.
* Temporary entry eligibility failure.
* Permanent or safety-driven signal invalidation.

For Avg5 strategies, retain the pending directional intent when:

* Current midpoint remains above Avg5.
* Avg5 is temporarily unavailable.
* Reference quotes are temporarily stale or unavailable.
* No eligible Rule 8 contract is currently available.
* Entry spread temporarily exceeds its configured limit.
* Original directional conditions weaken.

These conditions must prevent execution but must not automatically cancel the underlying directional intent.
Cancel the pending directional intent when:

* An opposite strategy signal occurs.
* Observer health becomes invalid or stale.
* Execution state becomes unreconciled.
* The configured maximum daily trade allowance is exhausted.
* The daily loss limit blocks further session entries.
* The entry cutoff or session end is reached.
* A mandatory protective action invalidates the signal.

Outstanding actual orders must be cancelled or reconciled when mandatory execution gates fail.
Do not permit fills while those gates are invalid.
Review the implemented logic and correct any blanket cancellation behaviour that violates the approved Option A lifecycle.
Add explicit tests for temporary versus terminal entry blocks.
Q3 — Failed or expired Avg5 orders
Approved with reconciliation requirements.
A failed or expired order with zero fills may return to the existing Avg5 pending-entry state if the directional intent remains valid.
Before retrying:

* Confirm the previous order is terminal.
* Reconcile all fills.
* Revalidate mandatory gates.
* Require a fresh eligible quote or meaningful eligibility transition.

Do not retry repeatedly against the same quote.
If an order has partially filled, correctly manage the resulting actual position.
Do not treat it as an unfilled entry.
Preserve logical trade identity and avoid duplicate trade counting.
Q4 — Execution health monitoring
Approved.
Set `ExecutionHealth:Required=true` when the independent execution service is activated.
Detect:

* Execution service never started.
* No initial heartbeat.
* Stale heartbeat.
* Recovery or reconciliation failure.
* Outstanding protective actions.
* Execution service failure.

Preserve configuration for environments where the execution service is intentionally disabled.
2. Complete F73 — Durable Observer checkpointing
Do not postpone checkpoint implementation pending real-data benchmarking.
Whole-session recovery is already an established structural risk.
First inspect the complete existing Observer state and deterministic replay architecture.
Implement a versioned, atomic, durable checkpoint capable of restoring all state required for equivalent continuation.
Include, where applicable:

* Session and configuration identity.
* Last processed source cursor.
* In-progress volume-bar state.
* Rolling metric accumulators.
* Options and futures quote baselines.
* Level-1 and Top-5 accumulators.
* Previous coherent depth observations.
* Cross-bar event-deduplication state.
* Split-volume-bar state.
* Required option-contract history.
* Avg5 history and validity state.
* Attestation state.
* Durable publication cursor and identities.

Keep execution position state under the independent execution service's authoritative ownership.
Do not duplicate position ownership inside Observer checkpoints.
Ensure checkpoint state and source cursor refer to the same consistent processing boundary.
Provide configurable checkpoint frequency and retention.
Suggested initial settings:

* Checkpoint interval: 60 seconds.
* Retain three valid checkpoints per session.
* Integrity verification enabled.
* Version compatibility verification enabled.

These are engineering starting values. Benchmark checkpoint overhead and document any necessary adjustment.
Recovery sequence:

1. Locate the newest compatible valid checkpoint.
2. Validate its integrity and session identity.
3. Restore complete deterministic state.
4. Replay only source events after its cursor.
5. Reconcile persisted bars, sidecars and publications.
6. Catch up to the live stream.
7. Publish validated Live status.

Support fallback to older valid checkpoints.
Support full-session replay when no usable checkpoint exists.
Never skip required data or weaken attestation to meet a time limit.
Test checkpoint recovery at multiple intraday points, including an incomplete volume bar, a split-volume event, a changing ATM contract and active Top-5 accumulation.
Prove restored execution produces equivalent bars, metrics and publications compared with uninterrupted processing.
3. Complete F74 and F75 — Bounded memory processing
The current implementation bounds individual reads but still accumulates large collections in memory.
Complete end-to-end bounded processing.
Address:

* Live polling backlog accumulation.
* Initial no-cursor reads.
* Session initialization.
* Historical bootstrap.
* Recovery read and normalization buffers.
* Futures and options depth streams.
* Required rolling metric retention.

Preserve deterministic source ordering and existing metric semantics.
Do not introduce arbitrary event dropping.
Do not silently change handling of out-of-order observations.
Use bounded batches, incremental processing and appropriate cursor-based continuation.
Release processed batches promptly.
Measure allocation and peak working set using representative synthetic workloads.
Record actual limits and remaining unavoidable memory retention.
4. Complete Observer process isolation
The latest report does not establish whether this requirement is complete.
Audit the current process boundaries.
The target architecture is:

* NiftySignalHost: broker connectivity and durable raw tick ingestion.
* NiftySignalObserver: Adaptive market-state processing, bars, metrics, recovery, checkpoints and durable signal publication.
* NiftySignalExecution: authoritative paper execution, position management and independent protection.
* NiftySignalDashboard: presentation and operational monitoring.

If Observer processing still executes inside the Host, complete the approved isolation requirement.
Observer failure or restart must not interrupt durable tick ingestion.
Execution protection must not depend on Observer process responsiveness.
Reuse existing infrastructure.
Do not establish an additional FlatTrade login session.
Avoid introducing unnecessary distributed infrastructure.
Verify service startup ordering, durable handoff, restart continuity and ownership boundaries.
5. Verify execution and strategy correctness
Review all four strategy configurations against the approved specification.
Retain:

* TOTAL_ACTIVITY_5_10
* TOTAL_ACTIVITY_5_10_AVG5
* TOTAL_VOTE_5_OF_6
* TOTAL_VOTE_5_OF_6_AVG5

Verify paired strategies differ only in the intended Avg5 entry-timing condition.
Verify core signals, configured quantities, maximum trades, capital, daily loss restrictions and execution cost policies.
Review actual tick and signal ordering.
Specifically verify that:

* Quotes preceding signal availability cannot cause backdated fills.
* Pending Avg5 state survives valid restart recovery.
* Observer health invalidation cancels unsafe pending intents.
* Partial exits cannot duplicate.
* Frozen targets remain unchanged after restart.
* Opposite signals close positions regardless of entry restrictions.
* Protective exits take precedence.
* 15:10 liquidation is independent of completed bars.
* Existing positions remain protected if Observer publication stops.

Report the behaviour when Host quotes, PostgreSQL or the entire VM are unavailable.
Do not claim independent service isolation protects against complete VM failure.
6. Dashboard validation
Update `tools/AdaptiveDashboardValidation` to match the approved Dashboard structure.
Do not leave the existing browser harness incompatible with the new layout.
Verify:

* Four market-summary grids.
* Option Price Averages.
* Exactly two paper-trading grids.
* Exactly one row per enabled strategy in each paper grid.
* Live executable-bid-based P&L.
* Partial realized and remaining unrealized P&L.
* Fully closed-trade accounting.
* Pending Avg5 status.
* Stale quote indication.
* Observer recovery information.
* Execution service health alerts.
* Telegram screenshot capture.

Use a running test Dashboard and synthetic data where possible.
Test the actual screenshot capture pipeline without sending unsolicited Telegram messages to production recipients.
Review every removed, rewritten or trimmed Dashboard test.
Preserve meaningful data-integrity, metric correctness, historical coverage and no-look-ahead assertions against the underlying producers.
Do not weaken unrelated tests merely because their original presentation component was removed.
Provide a test-change inventory showing what coverage was retained, relocated or intentionally removed.
7. Real-data benchmarking
Real-data recovery and execution performance validation remain mandatory.
Do not bypass previously denied database permissions.
Prepare a precise owner-run benchmark procedure that does not require access to production write paths.
The procedure must identify:

* Required recorded-session dataset.
* Required database permissions.
* Scratch database requirements.
* Exact commands.
* Estimated disk-space requirements.
* Expected output locations.
* Cleanup procedure.

Benchmark:

1. Existing approved baseline.
2. Current full-session recovery.
3. Checkpoint-assisted recovery.
4. Live catch-up while ticks continue arriving.
5. Peak working set and managed memory.
6. Database query timings.
7. Signal publication latency.
8. Raw quote-to-execution evaluation latency.
9. Live P&L update latency.
10. Top-5 and Level-1 deterministic parity.

Use actual recorded market data when authorised.
Include representative high-volume and near-end-of-session workloads.
The five-minute recovery certification target remains mandatory.
Do not claim certification based solely on synthetic data.
Continue implementation independently of this external validation blocker.
8. Recovery timeout and retry behaviour
Prevent repeated uncontrolled full-session replay attempts when recovery cannot complete within 300 seconds.
Maintain configurable recovery timeout and backoff.
Ensure:

* Timed-out recovery tasks terminate safely.
* Partial recovery state cannot be published as Live.
* Retry attempts cannot concurrently mutate Observer state.
* Repeated failures are visible through alerts.
* New entries remain blocked.
* Independent protective exits remain operational.

Checkpoint fallback and full-replay fallback must have explicitly documented failure behaviour.
9. Deployment tooling
Complete the deployment lifecycle for all approved services.
Verify:

* Installation.
* Configuration preservation.
* Service ownership and activation.
* Backup integrity.
* Build identity.
* Migration application.
* Service health.
* Startup dependencies.
* Safe rollback.
* Prevention of concurrent execution writers.

Do not perform a production deployment.
Use local or explicitly approved non-production validation.
10. Final reporting
Continue implementation without repeatedly requesting approval for requirements already agreed.
Update the roadmap and report:

* F73, F74 and F75 implementation status.
* Observer process-isolation status.
* Pending-entry lifecycle corrections.
* Checkpoint architecture and tests.
* Recovery and memory measurements.
* Browser harness validation.
* Execution fault-injection results.
* Final full Release regression results.
* Remaining real-data blockers.
* Exact owner-run benchmark procedure.
* Paper-release readiness.
* Real-money readiness.

Do not commit, push, merge, deploy, restart production services or modify production databases without explicit owner approval.
The 25 November incident root cause remains unresolved unless new evidence establishes it.
Paper release remains NOT READY until the outstanding engineering and validation requirements are completed.
Real money remains NO-GO.