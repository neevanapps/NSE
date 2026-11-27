# NiftySignal — A1–A8 decisions and continuation mandate

I have reviewed the following documents:

- NSE_PRODUCTION_RELIABILITY_AND_STRATEGY_PLAN.md
- NSE_ENTRY_GATE_INVENTORY.md
- NSE_INCIDENT_2026-11-25_OBSERVER_STALL.md

Continue implementation on the existing branch `feature/production-reliability-strategy-enhancement` in `D:\Projects\NSE-release-20261124`.

The original 45-section mandate remains authoritative, subject to the following clarifications and architectural decisions.

Preserve all completed work. Do not restart implementation from scratch.

## A1 — Separate execution service: approved

Implement a separate Windows service, provisionally named `NiftySignalExecution`, responsible for live paper execution and position protection.

Reuse existing PaperEngineV2 core, execution, configuration, journal and persistence components wherever practical. Do not create an entirely new trading engine or duplicate working accounting logic.

The execution service shall be the single authoritative writer of position and execution state.

Responsibilities:

- Consume durable directional signals and required verified metric snapshots.
- Consume fresh raw option quotes independently of completed futures bars.
- Manage all four approved strategies.
- Manage pending Avg5 entry intents.
- Select executable contracts using Rule 8.
- Manage entry and exit execution.
- Maintain independent strategy trade counters.
- Evaluate frozen partial-profit targets.
- Maintain realized and unrealized P&L.
- Perform independent protective exits.
- Perform mandatory session liquidation.
- Reconcile and recover execution state.

Do not establish a second FlatTrade login session.

The existing Host remains responsible for broker market-data connectivity and tick ingestion.

The Observer remains responsible for bar construction, market metrics and publishing the information required for directional signals.

The Dashboard remains a read-only presentation consumer.

Disable the legacy in-process position-writing path when execution ownership transfers to the new service. Never allow both engines to write the same ledger concurrently.

Implement an explicit single-writer ownership mechanism and test duplicate service startup.

Design a durable, ordered, restart-safe Observer-to-execution handoff that does not synchronously block the Observer on execution database writes.

Do not introduce Kafka, Redis or other distributed infrastructure unless demonstrated necessary. Prefer existing PostgreSQL and .NET infrastructure.

Avoid unbounded polling, whole-day reloads and expensive repeated queries.

Define event ordering and availability semantics so live execution and replay do not use future information. A delayed Observer signal must not result in a backdated fill using an earlier quote.

Journal relevant signals, quotes, timer events, safety events and execution intents as needed for deterministic replay.

Separate timer-based protective actions from market-data arrival. The mandatory liquidation timer must continue operating even when no new bars or quotes arrive.

Explicitly document behaviour during Observer failure, Host failure, execution service failure, PostgreSQL failure and total VM failure.

If fresh executable quotes are unavailable, record the pending protective action and alert. Do not fabricate a successful paper fill.

Document the architecture and review it against the approved requirements before coding. Proceed without another owner approval unless a genuine unresolved correctness or safety issue remains.

## A2 — Entry cutoff and mandatory liquidation

Do not retain the existing unconditional 15:00 forced exit.

Approved behaviour:

- New entries only from 09:20 IST and after at least 20 completed verified futures volume bars.
- No new entries at or after 15:00 IST.
- Cancel all pending entries at 15:00.
- Existing positions can remain open after 15:00.
- Continue partial-profit management and opposite-signal exit processing.
- Mandatory independent liquidation by 15:10 IST.

Initiate mandatory liquidation with sufficient time to complete before the deadline where executable quotes and liquidity permit.

Retain emergency and protective exits.

If liquidation cannot be completed, expose the failure explicitly and continue safe reconciliation/protection. Do not report a position as closed without confirmed execution.

Entry and exit eligibility must remain independent.

## A3 — Existing risk and integrity gates

Retain all four identified mechanisms:

1. Configurable daily realized loss limit.
2. Configurable maximum entry spread.
3. Virtual capital validation.
4. Three clean frames following an integrity failure.

Do not hardcode the daily loss limit, spread limit, virtual capital, maximum trades or quantity.

Preserve the existing configuration mechanisms.

The current inventory shows ₹50,000 virtual capital and a ₹1,500 daily realized loss limit. Verify the actual loaded strategy configurations.

Specifically identify whether configured capital can support the configured quantity within the ₹100–₹150 entry premium range, including charges.

For example, 650 quantity requires ₹65,000–₹97,500 before charges. A ₹50,000 capital configuration cannot support this.

Do not silently change capital, quantity, loss limits or capital accounting semantics. Report any contradictory configuration and provide the exact recommended correction.

For paired research, immediate and Avg5 variants must have identical risk, quantity and capital configuration except for the Avg5 entry requirement.

The daily loss limit must remain an entry restriction as currently defined. It must not prevent exits.

The maximum entry spread restriction must not block required protective, reversal or mandatory liquidation exits.

The three-clean-frames mechanism is a data-integrity recovery condition, not a discretionary cooldown. Preserve its existing configurable behaviour.

The existing gate inventory confirms that there is no additional market-state confirmation, magnitude filter, DTE entry restriction or timed cooldown in the two selected strategies.

Therefore, do not rewrite their core signal definitions or remove data-sufficiency checks.

Retain:

- TOTAL_ACTIVITY_5_10 core 5/10-bar activity logic.
- TOTAL_VOTE_5_OF_6 core five-of-six voting logic.
- Full metric coverage.
- Verified-bar and sidecar integrity.
- Quote freshness.
- Rule 8.
- Configurable maximum trade count.
- Position and execution integrity.

Retain the existing inverted MicroDev metric used internally by the voting strategy. The earlier Dashboard direction-display change must not alter the strategy's signal semantics.

Change the minimum completed-bar requirement from 10 to 20. Correctly recovered verified session bars count towards warm-up.

## A4 — Enabled strategies

Exactly four research strategies should be enabled in the intended new configuration:

- TOTAL_ACTIVITY_5_10
- TOTAL_ACTIVITY_5_10_AVG5
- TOTAL_VOTE_5_OF_6
- TOTAL_VOTE_5_OF_6_AVG5

Disable TOTAL_VOTE_4_OF_6 and all NPT1 paper-live strategies through configuration.

Preserve historical strategy definitions, records and research data.

Remove unnecessary synchronous paper-live handoff work from the Observer path when those execution paths are disabled or migrated.

Do not delete unrelated source projects.

Verify that disabled engines do not continue performing unnecessary background processing.

## A5 — Same-signal reversal and entry sequencing

Permit an opposite-direction entry using the same directional signal that triggered closure of the previous position, once the previous position is confirmed fully closed.

Do not require an additional completed futures bar.

Do not introduce a discretionary cooldown.

Require a new eligible executable quote after the previous exit is confirmed.

Prevent duplicate entries, overlapping positions and execution against already consumed entry intents.

For immediate variants, enter when mandatory conditions pass.

For Avg5 variants, create an independent pending opposite-direction entry and apply the approved Avg5 rule.

An independent protective flatten consumes the existing entry intent. Do not reopen using an already consumed signal simply because the Observer becomes healthy again.

Retain pending Avg5 Option A semantics:

- Freeze the exact reference contract when the eligible signal triggers.
- Compare the live coherent midpoint strictly below that contract's latest completed Avg5.
- Keep rolling Avg5 current.
- Continue waiting when the original directional conditions weaken.
- Cancel on an opposite signal, entry cutoff, session end or mandatory safety invalidation.
- Missing/stale Avg5 blocks entry without fabricating an average.
- The actual execution contract may differ from the reference contract.
- Rule 8 remains mandatory at execution.

Distinguish the pending directional intent lifetime from the lifetime of an actual execution order. Do not accidentally apply the existing 60-second order lifetime to the entire Avg5 waiting period.

## A6 — Stale Observer and independent position protection

Implement explicit entry blocking whenever:

- Observer status is not Live.
- Observer heartbeat is older than the configurable threshold.
- Required market data is invalid or stale.
- Recovery is incomplete.
- Position or execution state has not been reconciled.

Initial heartbeat threshold: 30 seconds.

Implement a configurable protective flatten policy for existing positions.

Initial additional stale-heartbeat grace period: 0 seconds.

A stale heartbeat beyond the threshold, or an explicit terminal Observer failure, must trigger the configured independent protection policy.

Do not reopen a protectively closed position using an already consumed entry intent.

Do not depend on Observer completion, Dashboard availability or completed bars to initiate independent protection.

Add independent execution heartbeat and alerts for:

- Execution service unavailable or stale.
- Protective liquidation failure.
- Mandatory session liquidation failure.
- Position reconciliation failure.
- Outstanding protective actions.
- Recovery to healthy state.

Preserve alert deduplication.

Explicitly report that services on the same VM cannot protect against complete VM failure. Do not claim real-money readiness on that basis.

## A7 — Calendar-day DTE and configurable VIX targets

DTE means calendar days between exchange-local trading date and the actual selected execution contract expiry date.

Do not use trading-day counting or hardcoded weekdays.

Retain the approved fully configurable partial-profit matrix:

| DTE | VIX below 16 | VIX at or above 16 |
|---|---:|---:|
| 6, 5 | 20% | 25% |
| 4, 3, 2 | 25% | 30% |
| 1 | 30% | 40% |
| 0 | 40% | 50% |

Partial-exit quantity: 50%, configurable.

VIX threshold: 16, configurable.

Validate DTE buckets and target configuration.

Freeze VIX, DTE, target percentage, target price and partial-exit quantity at entry. Persist the frozen policy with the position.

Evaluate against the live executable bid of the actual held contract.

Missing/stale VIX or unmapped DTE must disable partial profit booking for that position. Log the reason. Retain reversal and protective exits.

Do not recalculate the target during the trade.

Partial exits must be one logical intent, with correct handling of multiple fills and valid lot rounding.

The remaining quantity closes on the existing opposite signal or mandatory protective exit.

Preserve correct partial-fill accounting and trade-count semantics.

## A8 — Dashboard and Telegram capture

Confirmed removal of the following three bar-wise presentation grids:

- Futures Bar Grid.
- Options Core Direction Grid.
- Option Context Grid.

Retain Option Price Averages and all four market-summary grids.

Preserve underlying producers, persistence, agent API contracts, attestation and research data.

Update Telegram screenshot capture, grid-count expectations, diagnostics and tests to match the new Dashboard structure.

Do not remove data producers simply because their presentation grids are removed.

The paper-trading section must contain exactly two grids:

1. Current positions — one row per enabled strategy.
2. Today's fully closed-position summary — one row per enabled strategy.

Both must show four rows under the intended configuration.

Current positions must include live executable-bid-based P&L, remaining quantity, realized partial profit, total position P&L, status and other previously approved fields.

Update live P&L independently of completed futures bars.

Clearly distinguish stale valuations.

Ensure partially realized profits are not double-counted between the current-position grid and the fully closed-position summary.

Move detailed paper-trading commentary into structured logs.

Keep critical operational health and protection alerts visible.

Verify the Dashboard and Telegram capture in an actual browser using synthetic/test data before claiming acceptance.

## Additional findings from the recovery implementation

The implemented recovery changes are a useful improvement but are not sufficient to certify recovery performance.

Address the following:

1. The current configurable 300-second timeout can repeatedly abort recovery before it reaches Live.
2. The current retry backoff does not eliminate repeated expensive full-day recovery attempts.
3. Live reads are bounded per query, but the complete poll backlog may still accumulate in memory.
4. Initial no-cursor reads remain unbounded.
5. Session creation and historical bootstrap still load full-day data into memory.
6. The new time-sliced reads fail on per-token disorder exceeding a slice. Verify the actual production data's ordering characteristics before considering this equivalent to the previous whole-day normalization.
7. The query plan and cost of approximately 190 Level-1 recovery slice queries per session remain unverified.
8. Real-data Top-5 performance and parity remain outstanding.

Implement genuinely bounded end-to-end processing, not merely bounded individual database queries.

Preserve ordering, continuity and deterministic replay.

Do not silently drop, skip or reinterpret out-of-order source data.

If a changed ordering assumption is required, document it and provide evidence that actual recorded data satisfies it.

Prioritise durable checkpointing and incremental catch-up after the execution ownership architecture is established.

Implement safe retry behaviour with backoff and explicit repeated-failure reporting.

Keep the five-minute recovery certification target.

The configured runtime timeout must remain adjustable and must not be treated as proof of recovery performance.

Perform representative real-data recovery and query-plan validation against an authorised scratch database. Do not bypass access restrictions.

If access is blocked, produce an exact owner-run validation procedure. Continue independent development work where possible.

## Deployment tooling follow-up

Preserve the existing deployment fixes.

Additionally verify:

- Deployment cannot cross the approved disruptive-operation cutoff without explicit authorised intervention.
- Backups are independently verified.
- Exact deployed build identity is verified.
- Required migration identifiers are explicitly verified, not inferred solely from absence of startup errors.
- The execution service is included in installation, backup, health verification and rollback procedures.
- The Observer service is included once process isolation is implemented.
- Startup ordering and ownership transfer cannot create concurrent execution writers.
- Rollback cannot cause duplicated positions or outstanding order execution.

Do not deploy or change production services.

The currently running VM revision is believed to be 78d9b8b, but exact deployed binary identity remains to be verified. Preserve the distinction between local staging evidence and actual VM verification.

## Incident investigation

Keep the 25 November incident classified as root cause unresolved.

The synchronous paper-live handoff, slow database operations and recovery workload remain hypotheses until supported by evidence.

Do not claim that the new heartbeat and progress reporting fix the original stall.

Prepare a focused evidence collection procedure for the owner to execute against the relevant historical logs.

Do not access production directly or collect sensitive process dumps without separate approval.

## Continue implementation

All eight architectural decisions are now resolved by this addendum.

Proceed with the remaining approved requirements in dependency order:

1. Execution ownership and durable handoff architecture.
2. Independent protective exits and explicit fail-closed entry gate.
3. Four strategy configurations and warm-up changes.
4. Avg5 pending lifecycle.
5. Configurable DTE/VIX partial-profit execution.
6. Live P&L and Dashboard simplification.
7. Checkpointing and Observer process isolation.
8. Remaining recovery performance work.
9. Deployment lifecycle integration.
10. Integrated regression, fault injection and authorised real-data validation.

Reuse the existing roadmap. Record these decisions in the plan and mark their resolution.

Do not repeatedly ask for approval of requirements already agreed.

Raise only genuine new blockers that affect correctness, safety, data integrity, configuration semantics or execution behaviour.

Preserve the current passing baseline of 3138 tests, 0 failures and 1 skipped, subject to intentional and documented test changes required by the approved behavioural modifications.

Do not weaken unrelated assertions.

Perform focused tests during development and a full Release regression after integrated implementation.

Paper trading only.

Do not commit, push, merge, deploy, restart production services or modify production databases without explicit owner approval.

Final report must distinguish implemented, tested, blocked, unverified and not started requirements.

Do not declare paper release readiness until the required execution, recovery and strategy validation gates are satisfied.

Real money remains NO-GO.
