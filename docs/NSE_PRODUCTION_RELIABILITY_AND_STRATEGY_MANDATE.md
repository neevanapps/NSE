# NiftySignal — Production Reliability, Strategy Enhancement and Dashboard Simplification

## 1. Role and objective

Act as a principal software architect and quantitative trading systems engineer specialising in C#, .NET, PostgreSQL, deterministic market-data processing, intraday options execution and production reliability.

Implement the approved requirements below with rigorous attention to correctness, performance, maintainability, restart recovery, execution integrity and deterministic replay.

This is an approved implementation mandate, not another speculative design exercise.

## 2. Repository and working environment

Repository: `neevanapps/NSE`

Working copy:

`D:\Projects\NSE-release-20261124`

The last known deployed Top-5 event-based implementation is:

`78d9b8bd84d0b9bb38dd91a80ae89f0dad8fd5fe`

Verify the actual latest deployed revision before proceeding.

Create a dedicated feature branch from the approved release baseline:

`feature/production-reliability-strategy-enhancement`

Do not modify the research-protected checkout at `D:\Projects\NSE` or bypass any research protection locks.

Before implementation:

- Inspect Git status and current branch.
- Review existing architecture, documentation, skills, implementation contracts and tests.
- Preserve existing approved changes.
- Inspect the deployed strategy definitions and configuration.
- Establish baseline test results.
- Create and maintain an implementation roadmap at `docs/NSE_PRODUCTION_RELIABILITY_AND_STRATEGY_PLAN.md`.
- Track each requirement, implementation status, tests, risks and acceptance criteria.

Do not commit, push, merge, deploy, restart production services or modify production databases without explicit approval.

Use scratch databases and approved historical data access mechanisms.

## 3. Non-negotiable existing contracts

Preserve:

- Fixed futures volume bars of 13,650 units.
- Existing raw tick collection and persistence.
- Existing Level-1 metric definitions.
- Recently deployed event-based Top-5 metrics and v2 persistence.
- Existing options and futures attestation protections.
- Current option universe, expiry and ATM selection rules.
- Rule 8: entry contract executable ask between ₹100 and ₹150.
- Existing strategy-level configurable position quantity.
- Existing strategy-level configurable maximum trade count.
- Existing cost model, including zero brokerage per order where configured and applicable statutory charges.
- Deterministic replay and restart recovery.
- No new entries from 15:00 IST.
- Mandatory liquidation by 15:10 IST.
- Paper-only execution for this initiative.

Do not hardcode five trades per day or 650 quantity. Preserve the existing configuration mechanisms.

Do not change the recently approved MicroDev display direction conventions.

Do not silently change strategy-consumed metric definitions.

---

# PHASE 1 — Production reliability and safety

## 4. P0 — Incident root-cause investigation

Investigate the production incident on 25 November 2026.

Reported symptoms:

- Observer/runtime stall around 07:36 UTC.
- Dashboard displayed stale runtime state.
- All Adaptive grids stopped refreshing.
- Live futures and options prices continued refreshing.
- Existing tick ingestion and legacy volume-bar persistence continued.
- Agent snapshot endpoint became unresponsive.
- Dashboard restart did not resolve the problem.
- Reported recovery duration was approximately 36 minutes.
- Host logs contained warnings about slow synchronous paper-live handoffs.

Collect and analyse available Host logs and telemetry for 07:30–08:30 UTC.

Determine:

1. Exact incident timeline.
2. Whether the Observer stopped, became blocked or continued without publishing runtime state.
3. Whether synchronous paper-live handoff contributed.
4. Database query, lock and transaction behaviour.
5. Recovery phases and the primary recovery bottleneck.
6. Whether any Adaptive data, sidecars, strategy decisions or paper-trading records were lost or duplicated.

Distinguish confirmed facts from hypotheses.

If historical evidence is insufficient, report that explicitly and prepare safe diagnostic commands and a process-dump collection procedure for recurrence.

Process dumps may contain sensitive information. Store them securely and do not upload them externally without approval.

Do not delay other approved safety improvements merely because the historical root cause cannot be conclusively established.

## 5. P0 — Independent protective exits

Make position protection independent of the Adaptive Observer.

The position-protection path must continue functioning when the Observer is:

- Stale.
- Blocked.
- Rebuilding.
- Faulted.
- Disconnected.
- Terminated.

Provide independent execution of:

- Emergency flattening.
- Mandatory 15:10 IST liquidation.
- Existing mandatory protective risk exits.
- Outstanding entry-order cancellation where applicable.

Do not depend on completed volume bars or successful Observer publication to trigger mandatory liquidation.

Separate entry eligibility from exit eligibility.

A failed entry gate must never prevent an existing position from being closed.

Prevent duplicate exits, conflicting orders, unintended position reversals and excess exit quantity.

Preserve broker reconciliation and restart safety.

Investigate actual FlatTrade broker-side/exchange-side protective-order capabilities before relying on them.

Implement and test the protection architecture using paper execution and mocked broker interfaces. Do not place real-money orders.

A background task within the Observer is not sufficient independence.

Document which failures remain survivable and which require broker-held protection or a separate failure domain.

## 6. P0 — Fail closed on stale Observer

Block new entries when:

- Observer status is not Live.
- Observer heartbeat exceeds the configured freshness limit.
- Recovery is incomplete.
- Required market data is stale or invalid.
- Position or execution state is not reconciled.

Use an initial configurable heartbeat threshold of 30 seconds.

The heartbeat must represent actual Observer processing-loop responsiveness.

Do not derive Observer liveness solely from completed-bar timestamps. Fixed-volume bars may legitimately take longer than 30 seconds.

Do not implement a blind timer heartbeat that continues advancing while the Observer processing loop is blocked.

Re-evaluate pending entries against all mandatory safety gates before execution.

On stale Observer detection, cancel or prevent outstanding entry orders as applicable.

Existing-position protection must continue through the independent risk path.

Define an explicit configurable stale-position policy. For paper validation, default to conservative protection, and document behaviour clearly.

## 7. P0 — Operational alerts

Implement configurable alerts for:

- Observer heartbeat age exceeding 30 seconds.
- Observer entering a stale or failed state.
- Rebuilding exceeding five minutes.
- Recovery timeout.
- Recovery failure.
- Independent liquidation failure.
- Position reconciliation failure.
- Observer returning to healthy Live state.

Reuse the existing notification infrastructure where practical.

Implement alert deduplication and appropriate recovery notifications.

Do not emit repetitive alerts on every polling cycle.

Critical operational alerts should remain visible on the Dashboard.

## 8. P0 — Deployment safety

Prohibit routine Host deployments and restarts during market hours.

Preserve the existing deployment guard of 09:00–15:45 IST on applicable trading days.

Verify the guard immediately before disruptive deployment operations, not only when the script initially starts.

Ensure sufficient time remains for startup, validation and rollback.

Do not automatically bypass the guard.

Emergency intervention requires explicit owner approval and assessment of open-position risk.

---

# PHASE 2 — Recovery engineering

## 9. P1 — Recovery heartbeat and progress

Publish meaningful recovery heartbeat and progress from inside `RecoverAsync`.

Expose:

- Observer status.
- Recovery start timestamp.
- Current recovery phase.
- Last recovery progress timestamp.
- Processed tick count.
- Processed bar count.
- Estimated total work, where reliably known.
- Recovery elapsed time.
- Heartbeat age.
- Latest persisted cursor.
- Remaining catch-up backlog, where measurable.

Dashboard should show meaningful information such as:

`Rebuilding — replayed bar 312 of 500 — elapsed 02:41`

Do not fabricate total counts or progress percentages when the denominator is unknown.

Distinguish active rebuilding from stalled recovery.

Recovery heartbeat must be published without falsely marking incomplete state as Live.

## 10. P1 — Recovery timeout and failure state

Implement:

- Configurable recovery time limit.
- Per-phase logging and timings.
- Cooperative cancellation.
- Explicit recovery failure state.
- Safe handling of partial recovery.
- Entry blocking until recovery is validated.
- Safe retry/recovery behaviour.

Initial end-to-end recovery target: five minutes.

If recovery exceeds its configured limit, fail closed and alert.

Do not silently skip ticks, weaken attestation or mark incomplete state as healthy to meet the target.

Ensure timed-out recovery tasks cannot continue mutating state concurrently with subsequent recovery attempts.

## 11. P1 — Bounded recovery reads

Inspect `ReadAfterIdAsync` and eliminate unbounded reads using `int.MaxValue`.

Introduce configurable, bounded batches.

Preserve:

- Deterministic tick ordering.
- Cursor continuity.
- Source-event identity.
- No missing or duplicate ticks.
- Split-volume-bar semantics.
- Live/replay equivalence.
- Correct handoff from historical replay to live catch-up.

Avoid retaining full-day tick collections in memory.

Measure query time, allocation, peak memory, throughput and total recovery time.

## 12. P1 — Correct heartbeat timestamps

Use actual time at heartbeat publication.

Do not reuse the poll-start timestamp for a heartbeat written significantly later.

Separate, where appropriate:

- Last broker tick received.
- Last Observer heartbeat.
- Last completed bar.
- Last persisted bar.
- Last runtime-state publication.
- Last recovery progress.
- Last execution heartbeat.

Use UTC internally and IST for presentation.

---

# PHASE 3 — Trading strategy configuration and gate simplification

## 13. Retain exactly four enabled research strategies

Retain the following two existing base strategies:

1. `TOTAL_ACTIVITY_5_10`
2. `TOTAL_VOTE_5_OF_6`

Introduce independent Avg5 variants:

3. `TOTAL_ACTIVITY_5_10_AVG5`
4. `TOTAL_VOTE_5_OF_6_AVG5`

Archive or disable other existing strategy definitions. Do not delete historical strategy records.

The immediate and Avg5 variants of each base strategy must share the same core signal logic and otherwise equivalent research configuration.

The intended experimental difference is entry timing.

Preserve independently configurable:

- Maximum trades per day.
- Position quantity.
- Strategy enablement.
- Entry and exit policy configuration.

Do not introduce cross-strategy position dependencies.

Each strategy owns its own:

- Current position.
- Pending entry intent.
- Consumed signal identity.
- Trade count.
- Selected option contract.
- Fill records.
- Partial-exit state.
- Realized and unrealized P&L.
- Recovery and reconciliation state.

A position in one strategy must not prevent another strategy from opening its own position.

## 14. Audit all existing trading gates

Before modifying gates, identify every gate in the deployed strategies and shared execution pipeline.

Produce an inventory containing:

- Strategy.
- Gate name.
- Exact source location.
- Condition and formula.
- Configuration source.
- Metrics consumed.
- Gate category.
- Dependencies.
- Whether it is core signal logic, an additional trading restriction or a mandatory safety gate.
- Existing rejection frequency, where evidence exists.
- Overlap with other gates.
- Approved action.

Do not infer gate behaviour from names alone.

## 15. Approved gate changes

Implement the following decisions.

| Gate | Required action |
|---|---|
| Existing core entry signal | Retain |
| Additional market-state confirmation | Remove |
| Additional metric magnitude thresholds | Remove if present |
| DTE-based entry restrictions | Remove |
| Discretionary trading cooldown | Remove |
| Duplicate or overlapping trading gates | Remove |
| Warm-up | Modify as specified below |
| Maximum trade count | Preserve existing per-strategy configuration |
| Rule 8 | Retain |
| Position and execution integrity | Retain |
| Observer health | Retain |
| Market-data freshness | Retain |
| Entry cutoff | Retain |
| Mandatory liquidation | Retain |

Important:

Do not remove a threshold that is integral to the core signal definition.

For example, the five-of-six voting condition in `TOTAL_VOTE_5_OF_6` is core strategy logic, not an unwanted secondary threshold.

Do not change the fundamental activity calculations or voting semantics.

If a gate cannot be removed without changing the core signal definition, report the ambiguity before changing it.

Removing cooldown does not mean permitting duplicate orders, overlapping entries or repeated execution of an already consumed signal.

Do not introduce an arbitrary 10–30-second trading cooldown.

A valid opposite signal may allow an opposite-direction entry once the previous position has been fully closed and mandatory gates pass.

## 16. Warm-up requirements

New entries are permitted only when BOTH conditions are satisfied:

- Current IST time is at or after 09:20.
- At least 20 completed futures volume bars are available.

Retain mathematical data-sufficiency requirements for all metrics.

After restart, correctly recovered historical bars may satisfy the warm-up requirement.

Do not impose an unnecessary additional 20-bar wait after a successful, validated recovery.

Entries must remain blocked while the Observer is rebuilding or its state is invalid.

---

# PHASE 4 — Immediate versus Avg5 entry research

## 17. Immediate-entry strategies

The two existing base strategies enter when their original directional signal triggers and all remaining mandatory entry gates pass.

Do not impose an Avg5 condition on either base strategy.

## 18. Avg5 reference metric

Reuse the existing Option Price Averages implementation.

Existing contract:

`Avg = mean of completed-bar mid (PriceEnd) of the latest 5 completed bars, exact contract, no bridging, no zero-fill.`

Do not create an alternative average calculation.

Use:

- Latest five completed bars.
- Exact reference contract identity.
- Completed-bar midpoint values.
- Existing coverage and validity rules.
- No zero-fill.
- No bridging across missing observations.

## 19. Avg5 entry condition

For a bullish signal:

`Reference CE live midpoint < Reference CE Avg5`

For a bearish signal:

`Reference PE live midpoint < Reference PE Avg5`

Equality does not qualify.

Evaluate on eligible live reference-contract quote updates against the latest available completed-bar Avg5.

Do not wait for another completed futures volume bar merely to detect that the price has crossed below Avg5.

Use a coherent, valid and fresh bid/ask midpoint.

Do not substitute LTP for the midpoint.

## 20. Reference contract versus execution contract

The reference contract is used to measure option premium behaviour.

The actual purchased contract may differ.

When a new directional signal triggers:

1. Resolve the appropriate CE or PE reference contract using the existing Option Price Averages grid's contract-selection semantics.
2. Freeze its identity for the pending entry.
3. Continue calculating its rolling Avg5 as new completed bars become available.
4. Compare its live midpoint with its own Avg5.

Do not switch reference contracts simply because ATM changes.

Do not compare one contract's price with another contract's average.

When entry becomes eligible, independently select a valid executable option contract using the existing Rule 8 selection policy.

The actual execution contract must satisfy the ₹100–₹150 ask requirement at execution time.

If the reference contract cannot be observed or its Avg5 is unavailable or stale, do not enter.

## 21. Pending entry lifecycle — approved Option A

When the underlying strategy signal triggers, create a pending directional entry intent.

The pending intent persists even when the original entry signal conditions subsequently weaken or cease to be simultaneously satisfied.

Do not require the original signal conditions to be continuously true while waiting for the premium pullback.

Continue waiting until:

- The Avg5 condition becomes valid and all mandatory entry gates pass; or
- An opposite strategy signal occurs; or
- The entry cutoff/session end is reached; or
- A mandatory safety invalidation requires cancellation.

An unavailable or stale Avg5 prevents entry but does not, by itself, cancel the pending directional intent.

Repeated same-direction signals must not create duplicate pending entries.

Preserve signal identity and pending-entry state across restart, subject to reconciliation and current safety validity.

Do not revive obsolete or previously cancelled intents.

At actual execution, revalidate:

- Observer health.
- Market-data freshness.
- Warm-up.
- Entry time.
- Remaining configured trade allowance.
- Position state.
- Reference contract identity.
- Avg5 validity.
- Executable contract eligibility.
- Rule 8.
- Duplicate-order protection.

No future data or backdated fills may be used.

Record the original signal time, entry time, reference contract, reference midpoint, Avg5, execution contract and entry delay.

Record missed opportunities and cancelled intents.

---

# PHASE 5 — Configurable DTE/VIX partial profit booking

## 22. Configuration requirement

The entire partial profit-booking policy must be configurable.

Do not hardcode the DTE/VIX matrix into strategy execution code.

Use the existing strategy configuration architecture wherever possible.

Support configurable:

- Partial-exit enablement.
- Partial-exit quantity percentage.
- VIX threshold.
- DTE buckets.
- Profit-target percentages for each volatility bucket.
- VIX freshness requirement.
- Missing-data policy, with the approved default specified below.

The approved initial configuration is:

- Partial exit enabled for all four strategies.
- Partial quantity: 50%.
- VIX threshold: 16.
- Higher-volatility bucket includes VIX exactly equal to 16.
- Target and VIX frozen at entry.
- Profit targets expressed as percentage appreciation of the option premium.

The following JSON is illustrative. Adapt it to the actual existing configuration model without unnecessarily introducing another configuration framework.

```json
{
  "PartialProfitBooking": {
    "Enabled": true,
    "ExitQuantityPercent": 50,
    "VixThreshold": 16,
    "FreezeAtEntry": true,
    "Targets": [
      {
        "Dte": [6, 5],
        "BelowVixThresholdProfitPercent": 20,
        "AtOrAboveVixThresholdProfitPercent": 25
      },
      {
        "Dte": [4, 3, 2],
        "BelowVixThresholdProfitPercent": 25,
        "AtOrAboveVixThresholdProfitPercent": 30
      },
      {
        "Dte": [1],
        "BelowVixThresholdProfitPercent": 30,
        "AtOrAboveVixThresholdProfitPercent": 40
      },
      {
        "Dte": [0],
        "BelowVixThresholdProfitPercent": 40,
        "AtOrAboveVixThresholdProfitPercent": 50
      }
    ],
    "MissingVixPolicy": "DisablePartialTargetForPosition"
  }
}
```

Validate configuration at startup.

Reject ambiguous, overlapping, invalid or contradictory target definitions.

Do not silently assign an unapproved target for an unmapped DTE value.

Configuration changes must apply to future positions without modifying targets already frozen for existing positions.

## 23. Approved target matrix

| DTE | VIX below 16 | VIX at or above 16 |
|---|---:|---:|
| 6 | 20% | 25% |
| 5 | 20% | 25% |
| 4 | 25% | 30% |
| 3 | 25% | 30% |
| 2 | 25% | 30% |
| 1 | 30% | 40% |
| 0 | 40% | 50% |

Calculate DTE from actual contract expiry and exchange-local trading date.

Do not hardcode weekday names.

These are owner-defined research parameters, not historically validated optimal targets.

## 24. Freeze partial target at entry

At position opening, capture and persist:

- Actual average entry fill price.
- Original filled quantity.
- Actual contract expiry.
- DTE at entry.
- India VIX at entry.
- VIX observation timestamp.
- Selected volatility bucket.
- Selected profit-target percentage.
- Target option price.
- Partial-exit quantity.
- Configuration version or equivalent policy identity.

Calculate:

`TargetPrice = AverageEntryFillPrice × (1 + TargetProfitPercent / 100)`

Use the actual average fill price rather than the requested entry price.

Correctly handle entry orders that fill in multiple executions.

Do not dynamically recalculate the target when VIX changes during the position.

Persist the frozen target so restart cannot change it.

## 25. Missing or stale VIX

If valid, sufficiently fresh India VIX is unavailable at entry:

- Do not fabricate a VIX value.
- Do not substitute a default volatility bucket.
- Do not fabricate a partial profit target.
- Permit entry if all other mandatory gates pass.
- Disable partial profit booking for that specific position.
- Log the reason.
- Retain opposite-signal and mandatory protective exits.

The same conservative behaviour applies when the actual DTE has no configured target.

Inspect the existing India VIX data source and determine its actual availability and freshness characteristics.

Do not claim VIX-based functionality is validated unless the real data path is verified.

## 26. Partial-exit execution

Evaluate partial profit targets on live executable bid updates for the actual held contract.

Do not depend on futures volume-bar completion.

Do not trigger against LTP, reference-contract price or theoretical option price.

When the eligible bid reaches or exceeds the frozen target:

- Initiate a one-time partial-exit intent.
- Exit the configured percentage of original filled quantity.
- Respect exchange-valid lot quantities.
- Preserve the remaining position.
- Track partial fills and outstanding quantities.
- Prevent duplicate partial-exit orders.

For quantities that cannot be divided equally, round down to the nearest valid lot quantity while retaining at least one valid lot for the remaining position.

If a valid partial exit is impossible, disable that partial target for the position and log the reason.

A partially filled profit-booking order is part of the same logical partial-exit intent.

Do not mark the entire target quantity as closed until confirmed fills occur.

Partial exits do not increment the strategy's maximum-trade counter.

## 27. Remaining position exit

The normal final exit condition is the existing opposite strategy signal.

No new reversal-detection engine is required.

Required behaviour:

- Opposite signal before partial target: close the entire position.
- Opposite signal after partial target: close all remaining quantity.
- No opposite signal after partial target: continue holding the remainder.
- Do not add an arbitrary trailing profit target.
- Do not add a time-based discretionary exit.
- Do not introduce another unapproved technical exit filter.

Mandatory protective exits and 15:10 IST liquidation override normal strategy exits.

An opposite signal must close the position even if:

- Maximum trade count is exhausted.
- The opposite-side option fails Rule 8.
- The opposite-side Avg5 condition is not satisfied.
- The opposite-side entry is otherwise unavailable.

Exit eligibility must remain independent of entry eligibility.

For Avg5 variants, close the existing position immediately on the valid opposite signal. Any subsequent opposite-direction entry follows its own pending Avg5 lifecycle.

Resolve simultaneous partial-target, opposite-signal and emergency-exit events deterministically.

Emergency/full liquidation takes precedence.

Prevent duplicate or excess exit quantities.

---

# PHASE 6 — Dashboard simplification and live P&L

## 28. Preserve required Dashboard components

Retain:

- Live futures and options quote row.
- Futures Market Summary.
- Total Summary — ITM-shifted.
- Total Summary — ATM.
- Total Summary — OTM-shifted.
- Existing 5/10/50/100/Day summary windows.
- OFI5, MicroDev5 and event-based Depth L5 metrics.
- Option Price Averages.
- Observer runtime and recovery health.
- Critical operational alerts.
- Existing Telegram summary screenshot functionality.

## 29. Remove three bar-wise grids

Remove the three existing detailed bar-wise futures/options grids from the Dashboard presentation.

Identify the exact three components from the current implementation.

Do not remove unrelated summary or operational components.

This is a presentation change only.

Preserve:

- Underlying metric calculation.
- Volume-bar generation.
- Sidecar persistence.
- Historical research data.
- Agent API contracts, subject to existing approved versioning.
- Strategy-consumed information.

Avoid unnecessary database queries and projection work for removed views where safe.

Do not accidentally disable producers used by other consumers.

## 30. Exactly two paper-trading grids

The Dashboard should contain only two paper-trading grids.

### Grid 1 — Current positions

Exactly one row per enabled strategy, including strategies that are flat or waiting for Avg5.

With the approved configuration, this means four rows.

Include appropriate columns for:

- Strategy.
- Position status.
- CE/PE direction.
- Trading symbol/contract.
- Entry time.
- Average entry price.
- Original quantity.
- Remaining quantity.
- Current executable bid.
- Frozen partial profit target.
- Partial-exit status.
- Realized P&L from partial exits.
- Unrealized P&L.
- Total position P&L.
- MFE.
- MAE.
- Holding duration.

Use a compact, readable layout.

Show a concise pending-entry status for Avg5 variants.

Do not display lengthy strategy commentary or per-event decision explanations in the Dashboard.

### Grid 2 — Today's closed-position summary

Exactly one row per enabled strategy.

Include:

- Strategy.
- Fully closed trade count.
- Winning trades.
- Losing trades.
- Breakeven trades where applicable.
- Gross realized P&L.
- Charges.
- Net realized P&L.
- Win rate.
- Other existing essential daily performance fields if appropriate.

Do not display one row per individual closed trade.

A trade that is partially exited but still open must not increment the fully closed trade count.

Keep individual trade records in the existing database and audit infrastructure.

## 31. Live mark-to-market P&L

Open-position P&L must update using live quotes, independently of completed futures volume bars.

For long options:

`UnrealizedGrossPnL = (CurrentExecutableBid - AverageEntryPrice) × RemainingQuantity`

`TotalPositionGrossPnL = RealizedGrossPnLFromPartialExits + UnrealizedGrossPnL`

Use existing accounting and cost conventions.

Distinguish gross, realized, unrealized and net values clearly.

Do not double-count partial realized P&L across current-position and daily closed-position grids.

If bid data becomes stale or unavailable:

- Mark valuation as stale/unavailable.
- Do not present the last price as a fresh executable mark.
- Preserve the last known valuation only with an explicit stale indicator.

Live P&L must not require expensive full-day database queries on each quote update.

Reuse existing live quote infrastructure and bounded update mechanisms.

Do not introduce unnecessary load on the Observer.

## 32. Structured strategy and execution logs

Move detailed paper-trading commentary to structured logs.

Include:

- Core signal generation.
- Gate rejection.
- Avg5 pending entry creation.
- Reference contract selection.
- Avg5 waiting and eligibility transitions.
- Pending entry cancellation.
- Contract selection.
- Entry execution.
- Actual fills.
- DTE/VIX target assignment.
- Missing-VIX or unmapped-DTE reason.
- Partial-exit trigger and fills.
- Opposite signal.
- Final exit.
- Independent protective exit.
- Position reconciliation.
- P&L.
- Recovery state transitions.

Include strategy identity, session identity, signal identity, position identity, contract identity and relevant timestamps.

Avoid writing identical repetitive waiting messages on every tick.

Log meaningful state transitions and use bounded diagnostics.

Retain existing database audit records.

Logs supplement database persistence; they do not replace it.

---

# PHASE 7 — Structural reliability

## 33. P2 — Durable engine-state checkpointing

Implement versioned, atomic checkpoints for the Observer and other required stateful components.

Avoid replaying the entire session from 09:15 whenever recovery is required.

First identify the complete deterministic engine state.

Checkpoint scope must include, where applicable:

- Session and instrument identity.
- Last processed source cursor.
- In-progress volume-bar state.
- Rolling metric accumulators.
- Previous quote and depth baselines.
- Top-5 event-based accumulators.
- Required options contract state.
- Relevant attestation state.
- Strategy pending-entry state.
- Consumed signal identities.
- Position state.
- Frozen DTE/VIX targets.
- Partial-exit intent and fill state.
- Trade counters.
- Configuration and schema compatibility metadata.

Do not serialize incomplete or inconsistent state merely to reduce replay time.

Implement:

- Versioning.
- Atomic persistence.
- Integrity validation.
- Corruption detection.
- Deterministic replay from checkpoint cursor.
- Compatibility checking.
- Safe full-replay fallback.

Position and order truth must still be reconciled against the authoritative execution records.

Checkpoint frequency must be configurable and performance-tested.

## 34. P2 — Observer process isolation

Separate Adaptive Observer processing from tick ingestion so that an Observer failure does not interrupt raw market-data collection.

Assess the existing architecture before selecting the smallest safe process boundary.

Preserve:

- Durable ordered tick handoff.
- Source-event identity.
- Replay continuity.
- Session lifecycle.
- Deterministic metrics.
- Database integrity.
- Execution safety.
- Dashboard runtime visibility.

Ensure the independent position-protection path is not dependent on the Observer process.

Do not introduce unnecessary distributed infrastructure.

Use existing Windows service and PostgreSQL infrastructure where appropriate.

Document service dependencies, startup order, restart behaviour, configuration and operational procedures.

---

# PHASE 8 — Deployment tooling corrections

## 35. Fix vm-deploy.ps1 backup failure

The deployment on 26 November 2026 exposed a backup-script defect.

Both installation directories were incorrectly passed to Robocopy as a single source argument, causing exit code 16.

Affected service directories:

- `C:\Naveen\NiftySignal.Host`
- `C:\Naveen\NiftySignal.Dashboard`

Correct remote PowerShell argument handling.

Verify:

- Host-only backup.
- Dashboard-only backup.
- Both-services backup.
- Separate destination paths.
- Binary backup integrity.
- Preservation of appsettings.Local.json.
- Correct handling of Robocopy exit codes.
- No deployment if backup fails.

Do not delete valid backups prematurely.

## 36. Fix post-deployment timestamp verification

The same deployment reported both service checks as failed despite both services running and the Dashboard push hub reconnecting.

Investigate the apparent UTC/IST timestamp comparison issue.

Normalize timestamps appropriately.

Prefer verification using:

- Expected commit SHA.
- Published versus installed binary hashes.
- Actual loaded process/build identity where available.
- Service running state.
- Startup health.
- Migration state.

Do not declare deployment successful based solely on Windows service status.

Do not declare failure merely because inconsistent timestamp representations were compared.

Test the deployment scripts without touching production services.

---

# PHASE 9 — Research, validation and acceptance criteria

## 37. Controlled strategy comparison

Compare each immediate-entry strategy against its corresponding Avg5 variant.

Keep all other approved trading conditions equivalent.

Capture:

- Original directional signal.
- Signal time.
- Reference contract.
- Reference midpoint and Avg5.
- Actual execution contract.
- Entry delay in seconds and completed bars.
- Missed entry opportunities.
- Cancelled pending intents.
- Filled entries.
- Partial-exit targets and outcomes.
- Final exit reason.
- Gross and net P&L.
- MAE.
- MFE.
- Holding duration.
- Position occupancy.
- Profit surrendered from MFE.
- Configured trade-limit utilisation.

Do not compare only successful entries. Include signals missed by the Avg5 variants.

Use actual executable quote assumptions.

Do not use future information, LTP-based optimistic fills or synthetic liquidity.

Do not tune the owner-defined DTE/VIX matrix during validation.

Report results by strategy, session, DTE and VIX bucket.

The objective is to understand whether waiting for a cheaper premium adds value, not to manufacture profitable historical results.

Respect existing research dataset access restrictions and holdout protections.

## 38. Mandatory strategy tests

At minimum, test:

- Both base strategies retain their core signal definitions.
- Only four approved strategies are enabled in the intended research configuration.
- Each strategy has independent position state.
- Strategy-level maximum trade configuration remains effective.
- No hardcoded maximum-trade limit is introduced.
- No discretionary cooldown.
- 09:20 time requirement.
- Minimum 20 completed bars.
- Rule 8 remains mandatory.
- Both CE and PE Avg5 conditions.
- Strict less-than comparison.
- Exact-contract average comparison.
- Different execution and reference contracts.
- Reference contract retained through ATM changes.
- Missing Avg5.
- Stale reference quote.
- Pending entry survives weakening original signal.
- Pending entry cancelled on opposite signal.
- Pending entry cancelled at cutoff.
- No duplicate pending intents.
- Revalidation of mandatory gates at actual execution.
- Restart with a pending entry.
- All configured DTE/VIX combinations.
- VIX exactly equal to 16.
- DTE 0 targets of 40% and 50%.
- DTE 2 and 3 use DTE 4 targets.
- Missing or stale VIX.
- Unmapped DTE.
- Frozen target unaffected by later VIX changes.
- Frozen target survives restart and configuration changes.
- Exchange-valid partial quantity rounding.
- One-time partial-exit intent.
- Partial fills.
- Opposite signal before partial exit.
- Opposite signal after partial exit.
- Opposite signal when trade limit is exhausted.
- Simultaneous partial target and reversal.
- Mandatory liquidation with stalled Observer.
- Duplicate-order and excess-exit prevention.
- Live P&L independently of bar completion.
- Stale bid valuation.
- Partial P&L accounting without double-counting.
- Four rows per paper-trading grid.
- Existing historical records remain readable.

## 39. Mandatory reliability tests

Include fault-injection tests for:

- Observer hangs.
- Observer process termination.
- Recovery timeout.
- Database unavailability.
- Database locking.
- Slow synchronous handoff.
- Broker connection interruption.
- Dashboard unavailability.
- Stale quotes.
- Tick-ingestion continuity during Observer failure.
- Independent protective exit.
- Mandatory 15:10 liquidation.
- Restart with open positions.
- Restart after partial exit.
- Restart with an outstanding partial-exit order.
- Checkpoint corruption.
- Checkpoint incompatibility.
- Replay cursor continuity.
- Duplicate source events.
- Split-volume-bar events.
- Full recovery while live ticks continue arriving.

Verify that safety failures block entries without blocking required exits.

## 40. Real-data performance and parity

Complete the outstanding Top-5 real-data validation.

Compare the approved deployed baseline against the new implementation under equivalent conditions.

Measure:

- Tick-read duration.
- Replay duration.
- Persistence duration.
- Attestation duration.
- Checkpoint load/write duration.
- Catch-up duration.
- Total recovery duration.
- Peak managed memory.
- Process working set.
- Database load.
- Observer processing lag.
- Paper-live handoff latency.
- Live P&L update overhead.

Verify existing Level-1 and Top-5 metric parity against uninterrupted execution.

Do not infer production-sized performance from synthetic tests alone.

If credentials or approved data access are unavailable, provide exact commands for the owner to execute and report the validation blocker.

Do not bypass credential restrictions or research protections.

## 41. Full-day recovery certification

Every production go-live must include a representative full-day recovery rehearsal.

Target: complete end-to-end recovery within five minutes.

Recovery is complete only when:

- Required persisted ticks have been processed.
- No missing or duplicate observations exist.
- Recovered metrics match uninterrupted execution.
- Runtime state is published.
- Observer reaches validated Live state.
- Strategy pending intents are reconciled.
- Positions and outstanding execution intents are reconciled.
- Frozen partial-exit state is correctly restored.
- Mandatory safety gates can safely permit entries.

Do not achieve the time limit by reducing data coverage or weakening correctness.

Run an after-close restart rehearsal before real-money certification.

Report cold-recovery and checkpoint-recovery results separately.

## 42. Real-money go-live gate

Real-money execution remains prohibited until:

- Independent protective exits are implemented and tested.
- Mandatory 15:10 liquidation works without the Observer.
- Stale Observer conditions fail closed.
- Heartbeat and recovery alerts are tested.
- Position and order reconciliation is verified.
- Full-day recovery completes within the approved limit.
- Recovery parity is verified.
- The 25 November incident root cause is resolved or residual risk is explicitly accepted in writing.
- No critical production reliability defects remain.

This mandate authorises paper-trading implementation and local validation only.

---

# PHASE 10 — Execution discipline and final deliverables

## 43. Implementation order

Implement incrementally in dependency order:

1. Incident investigation and architecture audit.
2. P0 safety improvements.
3. P1 recovery engineering.
4. Existing strategy gate audit.
5. Four-strategy configuration and gate simplification.
6. Avg5 pending-entry lifecycle.
7. Configurable DTE/VIX partial exits.
8. Dashboard simplification and live P&L.
9. Deployment tooling corrections.
10. Checkpointing and Observer isolation.
11. Integrated regression, fault injection and real-data validation.

Maintain an explicit implementation checklist.

At the completion of each phase:

- Review the diff.
- Execute relevant tests.
- Record results.
- Identify blockers.
- Update the roadmap.
- Preserve previously verified behaviour.

Proceed through approved phases without requesting repeated approval for already agreed requirements.

However, stop and report if an unresolved ambiguity affects trading behaviour, safety, metric semantics, historical data integrity or architectural correctness.

Do not implement speculative enhancements outside this mandate.

## 44. Testing discipline

Use the repository's existing test infrastructure.

Do not weaken existing assertions merely to make tests pass.

Do not repeatedly run the full expensive test suite when focused tests are sufficient.

Run a complete Release regression suite after integrated implementation.

Distinguish:

- Passed tests.
- Failed tests.
- Skipped tests.
- Tests requiring external dependencies.
- Synthetic validation.
- Real-data validation.
- Production validation.

A timing test passing in isolation does not automatically establish absence of contention or performance regressions.

## 45. Final deliverables

Provide:

1. Updated consolidated implementation roadmap.
2. Incident investigation report.
3. Existing gate inventory and removal report.
4. Architecture and execution safety design.
5. Four strategy configurations.
6. Configurable DTE/VIX policy documentation.
7. Database migration and rollback documentation.
8. Updated Dashboard.
9. Structured logging documentation.
10. Recovery and checkpoint documentation.
11. Deployment script corrections.
12. Test and fault-injection results.
13. Real-data parity and performance results.
14. Comparative strategy research results, where approved data is available.
15. Files changed and exact behavioural changes.
16. Remaining risks, blockers and limitations.
17. Explicit paper-trading release-readiness recommendation.
18. Explicit real-money GO/NO-GO recommendation.

Do not claim validation succeeded without actually executing it.

Do not commit, push, merge or deploy.

Stop after implementation, verification and final review. Await explicit owner approval for release.
