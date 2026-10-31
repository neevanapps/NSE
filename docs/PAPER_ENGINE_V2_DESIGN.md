# Paper Engine V2 – architecture and design (revision 3: fixed decisions recorded; implementation proceeds phase by phase)

Branch `feature/market-summary-paper-engine-v2`. **Nothing here is implemented.** Revision 2 records the owner's approval of **Option B** (separate `PaperEngineV2.Core` + `PaperEngineV2.Data`, new `pev2_` tables in the existing trading database) and refines the design: independent persistence, observer isolation, time causality, buy-only direction terminology, the future live-selection boundary, and the execution decision matrix. Every default below that is not an existing repo fact is a **PROPOSAL awaiting owner approval** and is flagged `OWNER`. Nothing may be implemented, enabled, migrated or deployed from this document alone.

**Revision 3 (after owner acceptance of revision 2 at `d25ef5a`)** records the owner's FIXED architecture decisions and FROZEN execution defaults in §0 and separates them from the assumptions that still need source verification (§0.3). Where §0 and a later section disagree, **§0 wins**; sections §2.1, §3, §5 and §9 are annotated accordingly. Implementation proceeds one sub-phase at a time (6a first) with a stop at each checkpoint. A passing synthetic simulation never proves real-world execution quality.

Safety envelope (unchanged, applies to every phase): paper only; V2 disabled by default; **no broker interface, router, order submission or live-enabling control exists in Phases 6a–6g**; no change to NPT1 strategies, codec (`trading-codec/5`), engine, bar clock, positions, orders, fills or ledger; no VM / production-database access; no merge to master.

## 0. FIXED decisions, FROZEN defaults, and what still needs verification

### 0.1 FIXED architecture decisions (owner-approved)
1. **Option B** stays: separate `PaperEngineV2.Core`, separate `PaperEngineV2DbContext`, new `pev2_` tables in the existing trading database; every NPT1 isolation guarantee stays.
2. **Bounded asynchronous V2 worker.** The observer must NEVER wait on V2 database or evaluation work. Frame sequence and causal timestamps are preserved end to end.
3. **Fail closed for entries.** Every overflow, missing journal frame or failed persistence operation blocks NEW entries. Sequence gaps are detected and recorded once persistence recovers; the system never claims durable recording while storage itself is unavailable. A missed entry signal is **never executed retroactively**.
4. **V2 migrations are applied ONLY by an explicit owner-run script** (`pev2-schema.idempotent.sql`). Host start-up only CHECKS schema compatibility (version row + expected tables/columns) and fails V2 closed (V2 inactive, alert) when the schema is missing or incompatible. V2 never auto-migrates. (Supersedes the §2.1 option list.)
5. **V2 event screenshots** for paper-trade open/close are allowed and configurable; Telegram delivery has a **per-event-class opt-in, OFF by default**.
6. **`requireFreshVix`** is an allowed optional entry filter, OFF by default.
7. **Frame-journal retention >= 90 days initially; no automatic pruning in Phase 6.** A configurable retention/archival policy is a documented future item.
8. **Live boundary (mandatory, design only):** up to 30 simultaneous paper strategies, at most **two** explicitly authorized live strategy IDs ever; authorization is independent of strategy JSON, default OFF and checked at the execution boundary; a strategy can never gain live permission by editing its own config. No broker execution, live router or live enablement is authorized in Phase 6.

### 0.2 FROZEN execution defaults (the V2 contract)
| # | Frozen default |
|---|---|
| 1 | Entry and exit signals are evaluated only on **verified completed bars**. A simulated fill requires a genuinely NEWER market observation than the decision (strictly later availability instant), including across zero-duration split bars (§4). |
| 2 | **Buy CE or buy PE only.** Select from the signal-time exact ATM±2 basket within the **Rs 100–150 ASK-premium range**. Expiry, strike and token are frozen for the trade. Tie-breakers, in order: **proximity of the ask to Rs 125**, then distance of the strike from the signal-time centre, then stable strike / token ordering. |
| 3 | Buy at the executable ask, close at the executable bid; configurable conservative slippage; the validated fee model. |
| 4 | Maximum quote age **2 s initially** for normal fills, subject to the cadence audit in §0.3: if 2 s makes the simulation unreliable or leaves too few valid quotes, **report the evidence; do not silently relax freshness**. |
| 5 | **All-or-none** simulated orders by default. Partial-fill semantics are implemented only where size units, reliability and lot-multiple constraints are VERIFIED; real exchange fills are never inferred from Level-1 quotes. |
| 6 | Opposite signal = **Exit only**; no automatic reversal. |
| 7 | Fixed lots using the actual instrument-master lot size; independent virtual capital per strategy; capital is explicit configuration, never an engine default. |
| 8 | **Explicit daily-loss, max-trade and max-position controls are mandatory.** Protective entry blocking on breach, plus a separately defined liquidation behaviour. |
| 9 | Optional JSON stops, targets, trailing exits and max hold, **disabled by default**, evaluated on completed bars; a threshold price is never assumed executable. |
| 10 | Entries begin 09:30 and cease 15:00 IST. Terminal liquidation is attempted at 15:00; with no valid bid a **PENDING exit** is kept and retried on later eligible quotes; no fictional fill is ever recorded; unresolved positions are reported. |
| 11 | Reuse the existing date-effective NSE cost schedules and deterministic rounding. |
| 12 | Typed rejection / cancellation / failure taxonomy with durable event identities. |
| 13 | Shadow mode = the complete simulated lifecycle with independently journaled signals, orders, fills and ledger entries. |
| 14 | Independent per-strategy results; any combined total is labelled hypothetical, not a shared funded portfolio. |
| 15 | Versioned, validated JSON configs loaded at session initialization; changes take effect the FOLLOWING session only; no Dashboard JSON editor; no mid-session rule changes. |

### 0.3 Assumptions that STILL require source verification (not facts)
| Open item | Why it is not yet fact | How it will be verified |
|---|---|---|
| **Real option-quote cadence** vs the 2 s age limit | The branch has measured India VIX cadence (`docs/MARKET_SUMMARY_METRICS.md` §6.1) but **not** option top-of-book cadence; NPT1's 2 s is a research policy and the observer sidecars use 5 s. | Study on the local tick copy: per-token gap distribution for ATM±2 contracts in the Rs 100–150 premium band; share of decision instants with a valid <= 2 s two-sided quote strictly after the decision, per session. Report the evidence; do not change 2 s silently. |
| **Size-unit reliability** (units vs lots for top-of-book quantities; reliability for liquidity) | Level-1 bid/ask quantities exist in the tick table but their unit and reliability relative to lot size were never verified here. | Audit against the instrument-master lot size and feed documentation before any partial-fill logic; until then all-or-none. |
| **Lot-size source per trade date** | NPT1 reads lot size from the instrument master; V2 must prove the same field is complete for the signal-time basket. | Instrument-master check in 6c. |
| **Terminal / pending-exit retry horizon after 15:00** | NPT1 closes a pending terminal exit at the first later bid (`DelayedTerminalClose`) and reports `UnresolvedAtClose` at 15:35; V2 keeps the exit PENDING and retries, with the retry cut-off undefined. | Owner decision (proposal: retry until the 15:35 hard end, then report UNRESOLVED). |
| **Channel capacity / `MaxProcessingLag` values** | No measurement exists. | Phase 6g benchmarks. |
| **Stop / target / trailing reference prices** | The executable price at which a stop triggers is not defined by completed-bar data. | Defined in 6c; until then only bar-end bid evaluation with explicit documentation. |

## 1. Source evidence this design rests on (verified in the repo)

| Fact | Where | Consequence |
|---|---|---|
| NPT1 is a deterministic durable engine with a hashed, versioned codec and a narrow frame (`AdaptiveStrategyFrame`) | `Trading.Core/Events`, `Trading.Recovery` | V2 must not touch it → Option B. |
| NPT1's bar-clocked fills happen **at completed-bar ends** at the held/frozen contract's as-of two-sided quote, **"no latency, queue or displayed-size model (the research fills the whole quantity at the quote)"** | `ExecutionEngine.BarClock.cs` header | Source disagrees with matrix items 1, 3-5 (see §9): V2 must define its own causal fill rule and its own liquidity model; it can reuse NPT1 only as a reference. |
| The fixed-13,650 builder **splits one source update into several exact bars with the same timestamp** (`MaxChunksPerUpdate`), follow-on bars have duration 0, and the engine starts each next interval with a fresh band context at that instant | `ExactAdaptiveFuturesBarBuilder`, `AdaptiveObserverEngine` (comment "zero-duration follow-on bars") | "Next BarSeq" is not "later in time" → §4. |
| Observer processes whole availability groups ordered by (AvailableAt, tick Id), holding back a 2 s stable-ordering lag; late groups throw and force deterministic recovery | `AdaptiveObserverWorker`, `ProcessAvailabilityGroup` | Decision instants are availability instants; V2 inherits the same ordering key. |
| The paper hand-off (`PaperLiveCoordinator`) is **synchronous on the observer poll thread**; documented placeholder slow-handoff threshold = the 250 ms poll interval; it never throws into the observer | `Trading.Live/PaperLiveCoordinator.cs` | A second synchronous sink would add its DB commit to the NPT1 critical path → §3. |
| Quote freshness differs by consumer: observer sidecars / spot use **5 s** (`OptionsSupplementalBar.QuoteFreshnessSeconds`, spot rule), NPT1 execution uses **2 s** (`ExecutionConfig.MaxQuoteAge`) | `OptionsSupplemental.cs`, `ExecutionConfig.cs` | Source disagrees with a blanket "2 s": the limit is an execution policy and must be explicit per strategy (§9 item 4). |
| `CostsConfig` schedules are **effective-dated**; the engine refuses to run without a schedule covering the trade date ("no silent zero costs") | `Costs/CostModel.cs` | Agrees with item 11. |
| `RiskProfile.PaperLiveV1(globalCapital, …)`: only shared capital is mandatory, all other limits optional | `Risk/RiskProfile.cs` | Agrees with item 8 (no invented monetary defaults). |
| NPT1 quantity is exactly 650 and a contract whose lot size does not divide it **fails closed** | `NPT1_PAPER_THREE_LIVE_V1.md` §3 | Agrees with item 7 (lot validation against the instrument master). |
| NPT1 forbids stop / TP / trailing / max-hold; its terminal protocol closes at the latest observed bid at 15:00:00, keeps `MandatoryExitPending` with no bid and closes later as `DelayedTerminalClose`, `UnresolvedAtClose` at 15:35 | `NPT1_PAPER_THREE_LIVE_V1.md` | Items 9 and 10 are NEW behaviour for V2 / differ from NPT1 (§9). |
| Broker isolation is a release-blocker test | `Tests/Trading/Live/BrokerIsolationTests.cs` | Retained and extended for V2 (§12). |
| Market Summary library, VIX as-of, fixed basket, coverage per family exist on this branch | `NiftySignal.MarketSummary`, `docs/MARKET_SUMMARY_METRICS.md` | The V2 frame's metric source. |

## 2. Approved architecture (Option B)

```
Host observer thread (UNCHANGED, NPT1 path first)
  └─ PaperEngineV2Bridge.Offer(frame)              // O(1): enqueue into a bounded in-memory channel; never blocks, never throws
PaperEngineV2 worker (own hosted service, own thread, own DbContext/connection)
  ├─ FrameJournalWriter  → pev2_frames (durable, batched)
  ├─ Evaluator           → pure core: config set × frame → intents (deterministic)
  ├─ SimulatedExecution  → quotes from the frame / as-of reader → orders, fills (CostModel)
  └─ LedgerWriter + Outbox → pev2_* (one transaction per frame step)
```
Projects: `NiftySignal.PaperEngineV2.Core` (pure: config schema/validator, metric registry, three-valued evaluator, state machines, simulated execution, ledger records; references only `Domain`, `MarketSummary` and the PURE parts of `Trading.Core` that are cost / quote-validity / risk **types**, never `Trading.Live`, `Trading.Recovery`, the run/codec or `Ingestion`) and `NiftySignal.PaperEngineV2.Data` (EF context + SQL). The Host gets a thin adapter. A release-blocker test asserts the dependency list.

### 2.1 Independent persistence
* A **separate `PaperEngineV2DbContext`** with its **own migration history table** (`__pev2_migrations_history`, via `MigrationsHistoryTable`) in the existing trading database. Its migrations are additive (`CREATE TABLE pev2_…`, indexes) and never ALTER/DROP/TRUNCATE any existing object; no foreign key references an NPT1/`trading` table. The existing `TradingDbContext` and its idempotent SQL/model snapshot are not touched.
* Idempotent SQL script `pev2-schema.idempotent.sql` for owner-applied deployment, same pattern as `trading-schema.idempotent.sql`.
* **Migration application policy (FIXED, §0.1-4):** migrations are applied ONLY by the explicit owner-run idempotent script; the Host performs a read-only compatibility check at start and keeps V2 closed when the schema is missing or incompatible. The V2 context never calls `Migrate()`.
* DB roles: the Host role needs INSERT/UPDATE/SELECT on `pev2_*` only for V2; the Dashboard role SELECT only. Verified in the disposable-database tests.

## 3. Observer isolation, journal and gap handling

Synchronous V2 evaluation + persistence on the observer thread is **rejected**: it puts a second database commit (and 30 strategy evaluations) inside the NPT1 hand-off whose own slow-handoff threshold is already the poll interval.

**Chosen design: bounded asynchronous consumer + durable frame journal.**
1. The observer thread only builds/offers the immutable frame (the frame content is computed once per verified bar from the already-persisted core and sidecar rows; cost target < 1 ms) into a bounded channel (`Capacity` configurable, OWNER-sized from Phase 8 measurements; no invented default). `Offer` never blocks and never throws; if the channel is full the frame is **not dropped silently**: the run enters `Gapped` (counter, durable `pev2_recovery_log` row written by the worker's catch-up, alert) and entries are blocked until the gap is closed by catch-up.
2. The worker journals each frame (`pev2_frames`, unique `(run_id, bar_seq)`, content hash + compact payload + availability instants) **before** evaluating it. A frame evaluation step (signals, orders, fills, ledger, state digest, outbox rows) commits in ONE transaction keyed by the frame; a retry is a no-op by the unique keys (idempotent).
3. **The journal is a durable cache, not the only source.** After a restart the worker rebuilds missing frames deterministically from the persisted adaptive rows (core + verified sidecars) and, for executable quotes, from the persisted `ticks` through the same as-of rule used by the Dashboard (`AdaptiveQuoteAsOfService`); a frame whose inputs cannot be rebuilt (unverified sidecar, missing quote history) is recorded as an explicit `GapFrame` — never fabricated, never skipped silently.
4. **Gap detection:** per run, `BarSeq` must be contiguous; a missing seq, an unverified bar, a channel overflow, a rebuild failure or a hash mismatch against a journaled frame creates a durable gap record. While a gap is open: no new entries, open positions are managed only by rules that need no missing data, and exits that need a quote keep waiting (never a fictional fill).
5. **Backlog monitoring:** gauges for queue depth, oldest-frame age, processing lag per frame (`processedAt − frame decision instant`), journal write latency, evaluation time per strategy count (p50/p95/p99/max), reported on the Dashboard health strip and logged.
6. **No delayed signal is executed as if real-time.** Every frame step records `processingLagMs` in the journal before acting. A new entry is allowed only when `lag ≤ MaxProcessingLag` (OWNER; a per-strategy config value, mandatory); otherwise the intent is recorded as `Rejected(ExpiredOpportunity)`. Because the lag is journaled, replay reproduces the same outcome. Exits are never expired by lag (they are state-driven and use the same quote rules).
7. Determinism contract: the outcome of a frame is a pure function of (frame, journaled evaluation context, prior strategy state, config hash set) – never of the wall clock at processing time.
8. Failure containment: V2 worker exceptions never reach the observer; a faulted strategy is isolated (`Faulted`, alert) without affecting the others; a V2 store failure cannot delay or fail an NPT1 commit (separate connection, separate thread).

## 4. Time causality and executable quotes

**Instants and keys.** Every observation has an availability instant `A = max(exchange, receive)` and a source tick id; the total order is `(A, tickId)` (same as the observer). A decision is taken on completed bar `D` with decision key `Dk = (D.EndAvailableAt, lastTickIdOfGroup)`.

**Rules**
1. **No same-event fill.** A fill quote `q` must satisfy `q.A > D.EndAvailableAt` (strictly later *instant*; a later tick id at the same instant is the same event) and, with a configured simulated latency `ℓ ≥ 0` (OWNER), `q.A ≥ D.EndAvailableAt + ℓ`.
2. **Eligible bar = a genuinely later bar.** The next eligible verified bar for a decision on `D` is the first verified bar `B` with `B.EndAvailableAt > D.EndAvailableAt`. Sibling bars produced by the same market update (same `EndAvailableAt`, including zero-duration bars) are **never** eligible for fills; they can still carry signals, each signal being its own decision with its own `Dk` (a strategy may not act twice on the same instant: one entry intent per `(strategy, instant)`).
3. **Fill quote selection (proposal, OWNER):** the earliest executable two-sided quote `q` of the frozen contract with `q.A` in `(D.EndAvailableAt + ℓ, B.EndAvailableAt]` that passes freshness/spread/liquidity checks — i.e. an actual quote that appeared after the decision — rather than NPT1's "as-of the later bar's end", which can equal a quote that existed before the decision when bars share instants. The as-of-bar-end rule is kept as an alternative (`fillQuote: "FirstAfterDecision" | "AsOfEligibleBarEnd"`, both strictly after `D`).
4. **Freshness** is measured at the fill/evaluation instant `t`: `t − q.A ≤ maxQuoteAge`. A quote that arrives after `B.EndAvailableAt` is not usable for that entry.
5. **Missed entry:** no qualifying quote by the end of the eligible window (`EntryOrderLifetime`, OWNER) ⇒ `Rejected(MissingOrStaleQuote | UnavailableLiquidity | ExpiredOpportunity)`, durable, never retried by price drift.
6. **Delayed processing:** the evaluator works on journaled instants, never wall time; lag only matters through rule §3.6.
7. **Repeated timestamps / zero-duration bars:** unit-tested explicitly: N bars at one instant ⇒ at most one decision key advance per fill; activity-per-second metrics are already unavailable for zero duration (null, not infinity) and V2's registry treats them as Unknown.
8. **Exit causality** is identical: an exit decided on `D` fills at the first valid bid strictly after `D.EndAvailableAt`; with no valid bid the exit stays `ExitPending` (§5) — no fictional price.

## 5. Strategy configuration (JSON `pev2-strategy/1`) – buy-only terminology

V2 v1 **only BUYS calls and BUYS puts**. The JSON vocabulary makes selling/writing unrepresentable:
* Entry groups are `buyCall` and `buyPut` (never "long/short"). A bearish view is expressed as *buy a put*; a position is `{ right: "CE"|"PE", action: "BUY" }`, an exit is always `SELL_TO_CLOSE`. The validator **rejects** the words/keys `sell`, `short`, `write`, `writeCall`, `writePut`, `shortCall`, `shortPut`, any `side`/`action` other than BUY on entry, and any negative quantity.
* Ledger and journal columns use `option_right` (CE/PE) and `entry_action = 'BUY'`; there is no `direction = -1` anywhere.

```
{ "schema": "pev2-strategy/1", "strategyId": "S01", "version": 3, "enabled": true,
  "sourceModel": "fixed13650-summary-v1",
  "entry": { "buyCall": <ConditionGroup>, "buyPut": <ConditionGroup> },
  "exit":  { "conditions": <ConditionGroup>, "onOppositeEntrySignal": "Exit" },         // v1: only "Exit"
  "filters": { "entryFrom": "09:30:00", "entryBefore": "15:00:00", "requireFreshVix": false },    // §0.2-10 bounds; §0.1-6
  "instrument": { "basket": "SignalTimeAtmPm2", "premium": { "min": <number>, "max": <number> }, "rank": "PremiumProximityThenCentreDistanceThenStrikeThenToken" },   // frozen §0.2-2: ask nearest Rs 125 (range midpoint), then centre distance, then strike, then token
  "execution": { "fillQuote": "FirstAfterDecision", "entryPrice": "Ask", "exitPrice": "Bid", "slippage": {"entry": <number>, "exit": <number>}, "latencyMs": <int>,
                 "maxQuoteAgeMs": <int>, "maxSpread": {...}, "entryOrderLifetimeMs": <int>, "maxProcessingLagMs": <int>, "fillMode": "AllOrNone" },          // frozen §0.2-5; "Partial" is rejected until size units are verified
  "quantity": { "lots": <int> },
  "capital": { "virtualCapital": <number> },
  "position": { "maxConcurrent": <int> },
  "risk": { "maxDailyLoss": <number>, "maxDailyTrades": <int>, "onBreach": { "blockEntries": true, "liquidation": "None|ExitOpenPositions" } },   // all mandatory (frozen §0.2-8)
  "exits": { "stopLoss": {...}?, "target": {...}?, "trailing": {...}?, "maxHoldBars": <int>? },       // verified completed bars only in v1
  "fees": { "schedule": "<cost schedule id>" } }
```
`premium`, all `execution` timing/age values, `capital`, `quantity` and the fee schedule are **mandatory with no defaults**; an incomplete or invalid policy ⇒ the strategy cannot activate (reason stored). `ConditionGroup`, `Condition`, crossovers, metric-vs-metric and the float-equality rule are as in revision 1 (AND/OR trees, depth ≤ 4, ≤ 32 leaves; `==` only for discrete metrics; no expressions/scripts/SQL). Exactly one `sourceModel` per strategy; the fixed-13,650 frame and NPT1 M1 values are never mixed. More than 30 enabled strategies ⇒ the whole set is rejected.

**Live authorization is NOT part of this file** (§7). Config files are read, validated and hashed (canonical JSON SHA-256) **once at session start**; a change is a new `(strategyId, version, hash)` effective from the NEXT session; an open position keeps its original hash; a same-version/different-hash file is refused; no mid-session policy change; **no Dashboard JSON editor in the first release**.

## 6. Registry, evaluation and state machine

* **Typed metric registry** (C# fixed table): id, source model/version, unit, direction convention, allowed windows (5/10/50/100/Day or `Bar`), availability rule, accessor; initial entries from the Market Summary (futures, ATM/ITM/OTM/TOTAL option families CE/PE/Direction, coverage ratios, VIX level/Δ/%). Delta/Vega compare in raw units (×100 is display only).
* **Three-valued logic:** an unavailable/stale/insufficient-coverage input makes the leaf **Unknown**, never false/zero; AND/OR follow Kleene logic; Unknown entry = no signal (counted), Unknown exit = no discretionary exit (mandatory exits still apply). Rolling references are limited to the five summary windows plus a bounded previous-frame ring (K ≤ 3); longer path-dependent features are out of v1.
* **Evaluation:** one deterministic pass per frame, strategies in ascending id, conditions pre-compiled at activation into arrays of delegates (no reflection/LINQ/allocation in the loop), shared features read from the frame. Targets: 30 simple strategies p95 < 5 ms / p99 < 10 ms, measured in 6g; concurrency only if profiling demands it and determinism is kept. Strategies cannot observe each other.
* **State machine per strategy:** `Flat → EntryPending → (PartiallyFilled →) Open → ExitPending (partial exits) → Flat`, terminal side states `Rejected`, `Cancelled`, `Unresolved`, `Faulted`. Intent identity `(runId, strategyId, configHash, decisionKey, intent)` is a **unique key** (duplicate-signal prevention is structural). The contract token is frozen at selection and never switched during an open position.

## 7. Future live-selection boundary (design only; NOTHING live is implemented)

* **Paper vs live population:** all ≤ 30 strategies run paper evaluation forever. A **separate, explicitly configured allowlist** of **at most two** live-authorized strategy IDs may be introduced in a *future* phase.
* **No automatic promotion:** nothing (performance, schedule, a flag in a strategy file) promotes paper→live; changing the allowlist is a deliberate owner action.
* **Authorization is not embedded in strategy JSON.** It lives in a separate file/table `pev2-live-allowlist` (future) containing `(strategyId, configHash)` pairs, ≤ 2 entries, validated at session start; because it is hash-pinned, any strategy config change invalidates its authorization until re-approved. A strategy file containing any live/broker key is rejected by the schema.
* **Execution boundary:** one port `IExecutionGateway` (`Submit/Cancel/Replace/Query`). The ONLY implementation in Phases 6a–6g is `SimulatedGateway`. A future `LiveAuthorizationGuard` sits in front of any broker gateway and denies, with a durable rejection, every order from a strategy not in the allowlist, when the master switch is off, when account-level risk is breached or when the config hash differs. The guard and the port contract are specified now; **the broker gateway, router, order submission and any enabling control are not built**.
* **Default-off live master switch:** `PaperEngineV2:Live:MasterEnabled=false` (future key). It cannot be flipped by the Dashboard in the first live release without a separate decision.
* **Independent ledgers:** paper ledger tables `pev2_*` (this design) and, in a future phase, separate `pev2_live_*` tables; no table mixes paper and live rows; every ledger row carries `ledger_kind`.
* **Allocation and risk (future):** per-strategy allocation + shared **account-level** risk controls (daily loss, exposure, order/rate limits, kill-switch integration) for live; paper strategies keep independent virtual capital and the summed paper books are *never* an executable combined portfolio.
* **Broker-order safety (future):** deterministic client order ids derived from the intent identity (idempotent), submit-once state machine with a durable `Submitting` marker before the call, `Uncertain` state requiring reconciliation (never blind resubmit), periodic reconciliation against broker order/position books, restart protection (on restart no unacknowledged order is resubmitted), and a reconciliation report.
* **Phase 6 deliverables for this boundary:** the port interface, the guard specification and tests proving the Phase-6 assemblies cannot reach a broker (retained `BrokerIsolationTests`-style release blockers); no broker types.

## 8. Persistence (separate context, `pev2_` tables)

`pev2_schema_versions`, `pev2_config_versions` (strategy, version, canonical JSON, SHA-256, validation outcome), `pev2_runs`, `pev2_frames` (unique `(run_id, bar_seq)`; hash; payload; availability keys; `processing_lag_ms`), `pev2_gaps` / `pev2_recovery_log`, `pev2_strategy_state` (checkpoint + digest per strategy per frame), `pev2_signals`, `pev2_orders`, `pev2_fills` (every partial fill is its own row, `(order_id, fill_seq)` unique), `pev2_positions`, `pev2_ledger_entries` (gross, each fee component, net; `ledger_kind`), `pev2_risk_counters`, `pev2_rejections` (typed reason, §9.12), `pev2_events` + `pev2_notification_outbox`. All rows carry `run_id`, `strategy_id`, `config_hash`; idempotency is enforced by unique keys, not by application checks. No FK into `trading`.

## 9. Execution decision matrix (revision 2 proposals; the owner has since FROZEN the defaults in §0.2, and where they differ §0.2 governs; the table is kept for its source-evidence column and the remaining open points)

Legend: **Src** = what the repository evidence says; `AGREES` / `DISAGREES` / `NEW`. **OWNER** = needs explicit approval before implementation.

| # | Decision | Proposed default | Source evidence | Open owner point |
|---|---|---|---|---|
| 1 | Entry timing | Signal on a verified completed bar `D`; entry attempted on the next eligible verified bar (`EndAvailableAt > D.EndAvailableAt`) using a **genuinely newer** executable quote (§4); no same-event fills; no entry at/after 15:00:00 IST. | **DISAGREES in part:** NPT1 fills at bar *t+1*'s as-of end quote with "no latency"; with split / same-instant bars that quote can pre-date the decision. 15:00 cutoff AGREES (NPT1 live window 09:30:00 ≤ t < 15:00:00). | OWNER: latency `ℓ`, order lifetime, `FirstAfterDecision` vs `AsOfEligibleBarEnd`. |
| 2 | Instrument selection | Buy CE for a bullish entry group, PE for a bearish one, from the **signal-time exact ATM±2 basket** (five strikes, the fixed-basket resolver), ask inside the configured premium range (Rs 100–150 proposed); rank: nearest strike to the signal-time centre → smallest \|ask − range midpoint\| → lower strike → token ordinal; contract token frozen for the trade, never switched while open. | **PARTLY AGREES:** NPT1 ranks nearest-centre → \|ask−125\| → lower strike → token (same shape) but its candidate set is the ATM±2 band **united with the 09:30 anchor basket** and the Rs 100–150 range is a research parameter. | OWNER: confirm basket-only candidate set, the premium range as a V2 value (config, mandatory), behaviour when none qualifies (proposed: reject with reason, no waiting). |
| 3 | Prices | Entry at **ask**, exit at **bid**, configurable conservative slippage (≥ 0, mandatory explicit value); no mid / LTP fills. | AGREES with NPT1 (entry ask, exit last two-sided bid). | OWNER: slippage values (none invented). |
| 4 | Quote freshness | Proposed 2 s for entry and normal exit; a missed entry is rejected; an exit stays pending when no executable quote exists; never a fabricated price. | **DISAGREES in source:** NPT1 execution = 2 s but its terminal/signal exits are **not age-gated** (age only flagged); observer sidecars and spot use **5 s**. Measured option-quote cadence for the real feed has not been analysed on this branch. | OWNER: per-strategy `maxQuoteAgeMs` (mandatory); whether normal exits are age-gated (NPT1: no); run a quote-cadence study before fixing 2 s. |
| 5 | Partial fills | Valid exchange-lot multiples and displayed-liquidity limit; entry keeps the valid filled portion and cancels the remainder; exit keeps the outstanding quantity and retries on later valid quotes; each partial fill journaled independently. | **NEW / DISAGREES:** NPT1's bar-clocked path has **no displayed-size model and fills the whole quantity**; `FillFractionOfDisplayed` / `KeepFilledCancelRemainder` exist only for tick-clocked variants. Available data is **Level-1 only** (best bid/ask and sizes). | OWNER: liquidity model definition (Level-1 size × fraction? per-quote consumption across retries?), minimum-fill rule, retry price rule for exits. |
| 6 | Opposite signal | Default and only v1 option **Exit**; a new entry needs a separately evaluated signal after the strategy is flat (never in the same step). | NPT1 exits on the opposite signal then enters the opposite side (reverse-like). V2 default differs by design. | OWNER: confirm; whether "ExitAndReverse" is ever allowed later. |
| 7 | Capital and sizing | Fixed lots validated against the instrument-master lot size for the trade date; independent virtual capital per strategy; summed paper books never presented as an executable portfolio. | AGREES (NPT1 fails closed on lot mismatch; `RiskProfile` shared capital). | OWNER: lots per strategy; behaviour if premium × qty > virtual capital (proposed: `Rejected(InsufficientCapital)`). |
| 8 | Risk limits | Explicit per-strategy capital, loss limit, max daily trades, max concurrent positions; optional ceilings with **no invented monetary defaults**; a breach blocks new entries; liquidation only when the explicit policy says so; NPT1 risk state untouched. | AGREES (`PaperLiveV1`: only capital mandatory; `ShouldLiquidate` exists). V2 gets its own counters (`pev2_risk_counters`); the legacy kill switch is neither read nor written. | OWNER: which limits are mandatory vs optional; liquidation policy values. |
| 9 | Stops/targets/hold | Optional stop loss, target, trailing exit and max-hold in JSON; evaluated on **verified completed bars only** in v1; no tick-based execution path. | **NEW** (NPT1 forbids them). | OWNER: allow at all in v1? definitions (reference price: bid at bar end? stale-bid behaviour); trailing definition. |
| 10 | Session | Entry from 09:30:00; no entry at/after 15:00:00; terminal exit handling at 15:00; no valid exit quote ⇒ keep the position explicitly **UNRESOLVED**, alert and report; never a fictional fill. | **DISAGREES in part:** NPT1 keeps `MandatoryExitPending` and closes at the first later bid as `DelayedTerminalClose`, reports `UnresolvedAtClose` at 15:35. | OWNER: may a bid after 15:00 still close the position (NPT1: yes) or is it UNRESOLVED immediately; hard-end time. |
| 11 | Fees | Reuse the validated NSE option `CostModel`; effective-dated schedules; deterministic per-fill rounding; refuse to run without a covering schedule. | AGREES. | OWNER: schedule id per strategy; per-fill vs per-order brokerage rounding. |
| 12 | Rejection taxonomy | Closed, durable enum: `InvalidInstrument, MissingQuote, StaleQuote, InsufficientCapital, LotSizeViolation, RiskBreach, ExpiredOpportunity, UnavailableLiquidity, DuplicateOrder, InfrastructureFailure`; a simulated rejection is a normal ledger outcome, `InfrastructureFailure` is the only malfunction class and raises the "execution failure" alert. | NEW (NPT1 records reason codes per cancelled entry; no closed enum shared). | OWNER: confirm the list; which reasons alert. |
| 13 | Shadow semantics | Full simulated order and position lifecycle with an independent durable ledger; **no signals-only variant** in the first release. | NEW. | OWNER: none unless a signals-only mode is wanted. |
| 14 | Reporting | Each strategy independent on Home (trades, positions, gross P&L, charges, net P&L, MAE/MFE, rejection counts); any combined figure labelled hypothetical; never shared funded capital. | AGREES with NPT1 per-strategy books. | OWNER: whether a hypothetical-sum panel exists at all. |
| 15 | Configuration | Versioned JSON files validated and hashed at session start; changes effective from the NEXT session; no mid-session change; no Dashboard JSON editor in the first release. | AGREES with the run-identity check pattern of NPT1. | OWNER: file location on the VM and who may change it. |

### 9.1 Further owner decisions raised by this revision
A. Observer sink: bounded asynchronous consumer + journal (recommended) — confirm; channel capacity and `MaxProcessingLag` values to be set from Phase 8 measurements.
B. Migration application policy (§2.1 a/b/c).
C. Whether V2 events may trigger event screenshots like NPT1 trades do.
D. Telegram alerts: separate opt-in switch per event class; default off.
E. Whether the futures VIX / basket coverage thresholds may appear as entry filters (e.g. "require fresh VIX").
F. Retention of `pev2_frames` payloads (bounded days) and the journal's compact format.

## 10. Notifications (paper-trade alerts)

Events: **trade opened, trade closed, order rejected, partial fill, execution failure** (plus `UNRESOLVED` terminal position). Durable identity `pev2:<runId>:<strategyId>:<eventKind>:<intentOrFillId>`; the outbox row is created in the SAME transaction as the ledger/event row; a worker sends through `TelegramTextSender` with the screenshot-job state machine (`Pending → Sending` (durable before the call) `→ Sent | DeliveryUncertain` (never auto-resent) `| Rejected(retry-after)`). Delivery is idempotent across restarts, reconnects and refreshes (Dashboard toasts keep the per-source baseline so a reload replays nothing). Messages are labelled `[V2 SHADOW]` with strategy id/version/option right/BUY. Telegram is OFF by default and independent of commentary (removed) and of NPT1 alerts.

## 11. Dashboard (Phase 7 hook)
Read-only service over `pev2_*`; per-strategy rows with `V2 SHADOW` identity, config version/hash, option right, entry/exit, gross/fees/net, MAE/MFE where available, rejection counts; Home shows a compact summary; `/paper-live` (NPT1) untouched; any combined view labelled hypothetical.

## 12. Validation requirements (gates)

Deterministic, offline, on synthetic frames and a disposable PostgreSQL database:
* **Causality:** zero-duration split bars; many bars at one instant; repeated timestamps and tick-id ordering; a quote at exactly the decision instant (rejected) vs one microsecond later (accepted); delayed processing (lag beyond `MaxProcessingLag` ⇒ `ExpiredOpportunity`, identical on replay); missing and stale quotes at entry and exit; exit pending with no bid across the 15:00 cutoff ⇒ UNRESOLVED.
* **Fills:** partial entry (valid remainder cancelled), partial exit recovery across a restart, every partial journaled once.
* **Engine:** 1 / several / 30 strategies; 30-strategy isolation and no cross-contamination; shared-frame immutability (frame hash unchanged after evaluation, strategies cannot mutate it); same-frame determinism under permuted evaluation order; duplicate signal/frame processing (idempotent); config version change (next session; open position keeps old hash); missing metric (Unknown propagation); capital/risk rejection; invalid configs (>30 enabled, unknown metric, unit mismatch, incomplete policy, sell/short/write vocabulary, live/broker keys, float `==`).
* **Journal:** replay equality (frames → identical signals, orders, fills, ledger, digests), channel overflow ⇒ explicit gap, gap closed by catch-up from persisted rows/ticks, hash mismatch halts, backlog gauges.
* **Isolation:** NPT1 non-interference (hash of NPT1 trading/`npt1m1` tables and a full NPT1 end-to-end run identical with V2 off, on, faulted, and backlogged); a V2 store failure never delays an NPT1 commit (timing test with an injected slow store); V2 SQL touches only `pev2_*` and the V2 migration history.
* **Broker isolation (release blockers):** the existing `BrokerIsolationTests` stay green; new tests assert `PaperEngineV2.*` reference no Ingestion/FlatTrade/Execution/HTTP/broker assembly or type, there is no order-submission, router or enabling type anywhere, and a strategy file with live/broker keys is rejected.
* **Alerts:** each of the five event classes exactly once under crash/restart at every step of the outbox state machine; uncertain delivery never resent; rejection and execution-failure alerts distinguish simulated rejection from malfunction.
* **Regression:** full existing suite incl. the PostgreSQL tests green after every sub-phase; the disposable databases are removed.

## 13. Implementation sequence (each step ends with the full suite green and a report)

| Phase | Scope | Validation gate |
|---|---|---|
| **6a** | `PaperEngineV2.Core`: JSON schema, canonical hashing, validator (buy-only vocabulary, ≤ 30, mandatory policy, no live keys), metric registry, three-valued evaluator, condition compiler. Pure, no I/O. | Config/validator/registry/evaluator unit + property tests (Unknown propagation, depth/leaf limits, vocabulary rejection); assembly-reference blocker; no change to any existing project. |
| **6b** | `MarketStateFrame` builder (Host adapter) + bounded channel + journal format + gap detection, WITHOUT any strategy. | Frame immutability/hash tests; overflow ⇒ gap; contiguity; no-throw/no-block `Offer` timing test; NPT1 non-interference with the bridge on. |
| **6c** | State machine + `SimulatedGateway` + causal fill rules + quote/liquidity model + `CostModel` + rejection taxonomy + risk counters. | The causality/fill/partial/terminal tests of §12 incl. zero-duration bars; fee determinism; capital/lot/risk rejections. |
| **6d** | `PaperEngineV2DbContext`, own migration history, `pev2_` schema, idempotent SQL, journal/ledger persistence, worker transaction per frame. | EF migration + idempotent-script tests on a disposable PostgreSQL (empty → latest, twice, ordering, no destructive DDL, roles); crash/replay equality; duplicate-frame idempotency; Host/Dashboard role checks. |
| **6e** | Alert outbox + worker + Dashboard toast hook. | Five event classes, exactly-once under crash points, uncertain handling, baseline no-replay. |
| **6f** | Release blockers: broker isolation, NPT1 non-interference, V2-off no-op (nothing constructed, no table touched), live-boundary port + guard specification tests. | Full NPT1 suites byte-identical; hash of NPT1 tables unchanged across V2 sessions; blockers green. |
| **6g** | Benchmarks (1/10/30 strategies; frame build; journal write; queue/lag) and the soak/full-session simulation; Dashboard read model (Phase 7 hand-off). | p95 < 5 ms / p99 < 10 ms for 30 simple strategies; memory stable over a full-session simulation; actuals reported incl. baseline-above-target explanations. |

No live/broker work is scheduled in 6a–6g. A later, separately approved phase would add the `pev2_live_*` ledger, the allowlist, the broker gateway behind the guard, reconciliation and the master switch.

## 14. Risks
Execution-semantic gaps in §9 (largest; each OWNER flag blocks its sub-phase); quote-cadence evidence missing for the 2 s freshness proposal; Level-1-only liquidity cannot support a true depth model; irregular wall-time of volume bars (all session cut-offs are evaluated against bar end instants); journal growth (bounded, ~220 bars/day); worker catch-up correctness depends on ticks retention; scope creep toward live must be refused structurally (§7).

## 15. Phase 6b as built (frames, handoff, journal contract) — in-memory only

Code: `NiftySignal.PaperEngineV2.Core` (frame, handoff, guard, consumer, journal contract, in-memory journal, replayer) and `NiftySignal.PaperEngineV2.Frames` (the pure builder from the Market Summary). Nothing is wired into the Host; no database, migration, order, fill or broker code exists.

### 15.1 Frame identity (`pev2-frame/1`)
`MarketStateFrame` is immutable (private copies, read-only wrappers, no setters; the shared frame has a read-only interface `IMarketStateFrame.TryRead`). Identity: schema; `SourceModel = fixed13650-summary-v1`; `AdaptiveModelVersion` (must be `adaptive-fixed13650-v1`, the NPT1 M1 clock, `adaptive-v1` and anything else are refused); `AtmRuleVersion`; `SessionId`; `TradeDate`; `BarSeq`; `BarStartAvailableAt` / `BarEndAvailableAt`; `LastSourceTickId`; `SameInstantOrdinal`; `DurationSeconds`; `Verified` + `UnverifiedReason`.
* **Describes vs available:** `[BarStart, BarEnd]` is the market activity the bar DESCRIBES; `DecisionKey = (BarEndAvailableAt, LastSourceTickId)` is when it BECAME AVAILABLE. Every quote and the VIX observation carry their own availability instants and must be `<= BarEndAvailableAt`; anything later refuses the frame (look-ahead) — at construction and again on replay.
* **Split bars:** bars produced by one market update share `BarEndAvailableAt` and tick id; `SameInstantOrdinal` numbers them 0,1,2…; zero-duration siblings are flagged (`IsZeroDuration`) and are never eligible fill bars for a decision on a sibling (`IsEligibleFillBarFor`: a fill bar must END strictly later than the decision's availability instant).
* **Basket:** exact `BasketIdentity` (expiry, centre, step, ten CE/PE slots with offset, strike, token, moneyness; unresolved slots stay unresolved with the problem text).
* **Quotes:** two-sided top-of-book snapshots of basket contracts only, as available at the boundary; missing / ambiguous / not-two-sided contracts become `QuoteIssue`s (`Missing`, `Ambiguous`, `NotTwoSided`), never substituted.
* **Metrics:** 284 registry metrics × 5 windows in a dense table; an unavailable, insufficient or non-finite value is OMITTED (Unknown), never zero; `vix.fresh` is 0 when the VIX is missing or stale; Delta/Vega are raw.
* **Canonical payload and hash:** canonical JSON (fixed field order, UTC instants, shortest round-trip numbers, sorted `[handle, window, value]` triples) and its SHA-256, computed lazily on first use so the observer thread does not pay for them.

### 15.2 Observer-to-V2 handoff (`FrameHandoff`)
`Offer` / `OfferBuilt` are O(1), lock-free, never wait, never throw and touch no database, journal or strategy type (a test inspects the type's surface). Capacity, `MaxProcessingLag` and `FramesToClearGap` are required, validated parameters with NO defaults (production values come from Phase 6g). A full queue REJECTS the frame observably: counters, a bounded identity ledger (decision key + offer sequence), the first/last dropped bar and the producer-side `Overflow` signal; a builder exception raises `BuildFailure`. Nothing is dropped silently.

### 15.3 Consumer, sequence guard and entry blocking (`FrameConsumer`, `FrameSequenceGuard`)
The consumer runs on its own task. The guard classifies each frame: accept; `SessionStart`; duplicate (same hash, ignored); conflict (same bar, different hash, rejected, never replaces the journaled frame); out-of-order (late bar); missing sequence (accepted after a recorded GAP `from..to`); causality violation (decision key going backwards, inconsistent same-instant ordinal). Entries are blocked (journaled `IntegrityFlags`) while any of: unacknowledged overflow / build-failure signals, a sequence gap, an unverified frame, processing lag above the configured limit, a journal fault, a consumer restart, or fewer than `FramesToClearGap` consecutive clean frames since the last such event. A clean start at bar 1 needs no warm-up; a mid-session start counts as a post-gap run. After a gap or an unverified frame the previous-frame link is dropped, so crossovers become Unknown: a missed entry signal is never executed retroactively.

### 15.4 Journal contract (`pev2-journal/1`) and replay
`IFrameJournal` (async, idempotent per key, `IsDurable` tells the truth): `FrameRecord` (session, bar, hash, canonical payload, decision key, verified; unique (session, bar)), `DispositionRecord` (entries allowed, reason flags, processing lag ms, clean-run length, previous-frame usable, processed-without-journal; unique (session, bar); written BEFORE the frame is processed), `GapRecord` (from/to, cause Overflow / BuildFailure / MissingFrame, producer signals, `Durable`). `FrameReplayer` re-hashes and re-validates every payload (tamper/corruption throws `ReplayIntegrityException`), feeds frames strictly in journal order and uses the JOURNALED dispositions, never the wall clock — replay of a live run (including gaps, lag, unverified frames, duplicates) is bit-for-bit equal in the tests. A frame journaled without a disposition replays with entries blocked.

### 15.5 What 6b does NOT provide
* **No durability.** `InMemoryFrameJournal.IsDurable == false`; gap records are process-local (`Durable = false`, forced). Nothing may claim crash-safe recording or recovery until Phase 6d persistence exists and is tested. A journal fault is flagged, blocks entries and is never reported as durable (`ClaimsDurableRecording`); a frame processed without a journal write makes replay impossible for that run and is marked.
* Restart is modelled as "fresh processor rebuilt from the in-memory journal, run length reset"; a real process crash loses the in-memory journal and the in-memory queue.
* No strategy execution, orders, fills, ledger, alerts, Host wiring or live path.

### 15.6 Proposed Phase 6c boundary
Execution simulation only, consuming `FrameDisposition` + `IMarketStateFrame` + the frame's quotes: instrument selection from the frame's basket and quotes, the state machine, causal fill rule (strictly newer quote than the decision, using `IsEligibleFillBarFor` and `DecisionKey`), quote freshness / spread checks, all-or-none fills, fees via the existing cost model, risk counters, rejection taxonomy and the pending-exit rules; still pure and in-memory, with the same release blockers. Open inputs: the quote-cadence study (2 s), size-unit verification, lot-size source, retry horizon of a pending exit.
