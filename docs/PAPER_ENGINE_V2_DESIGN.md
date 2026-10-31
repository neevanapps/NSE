# Paper Engine V2 – implementation design (Phase 6, DESIGN ONLY)

Branch `feature/market-summary-paper-engine-v2`. Nothing in this document is implemented. It plans the approved requirements, separates what the repo already settles from what it does not, and lists the execution semantics that must be decided by the owner instead of being invented. Safety envelope unchanged: paper only, shadow only, disabled by default, no broker path, no change to NPT1 strategies / codecs / ledger / bar clock, nothing deployed or applied to any production database.

## 0. What already exists and constrains the design (verified in source)

| Existing piece | Fact | Consequence for V2 |
|---|---|---|
| NPT1 engine (`Trading.Core`, `Trading.Live`, `Trading.Recovery`, `Trading.Data`) | Deterministic durable `LiveEngineRun`; input journal + state digest; `trading-codec/5`; frame type `AdaptiveStrategyFrame` is narrow (MicroDev, PE strict delta, band centre/expiry, candidate strikes); schema `trading` with `runs, steps, engine_inputs/outputs, positions, orders, fills, ledger_entries, final_records…` | V2 must not extend `AdaptiveStrategyFrame`, the codec or any NPT1 table (hashed, versioned, "no change without explicit versioning"). |
| `ExecutionConfig`, `CostsConfig`/`CostModel`, `RiskProfile` | Pure, validated config records already exist: latency, order lifetime, max quote age (2 s), spread limits, FillFractionOfDisplayed, slippage, NSE index-option cost schedules, risk scopes (capital, daily SL, trade count, concurrent positions). `Adapter` must be `Simulated`. | Reuse as LIBRARIES (cost model, quote validity rules, risk checks) rather than re-deriving fees or risk. |
| `PaperLiveCoordinator` hand-off | Synchronous inside the observer poll (single thread), never throws into the observer, only verified bars (`IsBarVerifiedForPaper`) | V2 hangs off the same hand-off point but as a second, isolated sink: its failure/latency must not delay or fail NPT1 (bounded time budget + own try/catch; see §8). |
| Fixed-13,650 clock, NPT1 M1 clock | Independent; NPT1 strategies use M1 | A V2 strategy names its source model; the two clocks are never mixed. |
| Market Summary (this branch) | Pure `NiftySignal.MarketSummary`: five windows, fixed ATM±2 basket, coverage, VIX as-of | The natural metric source for V2's first source model. |
| Notifications | `TelegramTextSender` (acknowledged delivery), screenshot outbox with Sending-before-send / DeliveryUncertain semantics | Template for the V2 alert outbox. |

## 1. Architecture decision (needs owner confirmation)

**Option A – plug a `ConfiguredFamily : IStrategyFamily` into the existing engine.** Maximal reuse of fills/orders/ledger, but needs a new frame type in the engine input and a codec/digest version bump (`trading-codec/6`) touching NPT1 code paths, and the engine's variants-per-family model is built for 3 hand-written families, not 30 data-driven ones.

**Option B (recommended) – a separate `NiftySignal.PaperEngineV2` core + `pev2_` persistence, reusing the pure libraries.** New projects: `PaperEngineV2.Core` (pure: config, registry, evaluator, state machines, simulated execution, ledger records), `PaperEngineV2.Data` (EF context + SQL in the existing trading database, `pev2_` prefix), a thin Host adapter. Reuses `CostModel`, the quote-validity rules and `RiskProfile` types by reference; does NOT reference `Trading.Live` run/codec. Reason: zero risk to NPT1 invariants, 30 data-driven strategies on one shared immutable frame, free to version its own codec.

Everything below assumes Option B. If A is chosen the persistence and codec sections change.

## 2. Source frames

* `MarketStateFrame` (immutable, record, versioned `fixed13650-summary-v1`): built ONCE per completed fixed-13,650 bar by the Host after the bar's sidecars are verified (same `IsBarVerifiedForPaper` gate; an unverified bar is delivered as such and entries are blocked).
* Contents: identity (session, BarSeq, boundary instants, model version, `AtmRuleVersion`), basket (centre, expiry, ten tokens), the Market Summary rows (futures + ATM/ITM/OTM/TOTAL × five windows incl. coverage per family), India VIX points, per-token top-of-book quotes (bid, ask, sizes, availability instant) for the contracts a strategy may trade, and previous-frame references via a bounded ring (see §4 rolling refs).
* Computed once per bar and shared by all strategies (no per-strategy recomputation). Persisted as a content hash + the journaled compact form so replay does not re-read ticks.
* A second source model `npt1-m1-v2` (NPT1's frame) may be added later as its own registry namespace; a strategy declares exactly one `sourceModel`.

## 3. Versioned JSON strategy configuration (v1 schema)

One file per strategy (directory `Trading:PaperEngineV2:ConfigDirectory`), strictly typed, unknown properties rejected, no expressions/scripts/SQL:

```
{ "schema": "pev2-strategy/1", "strategyId": "S01", "version": 3, "enabled": true,
  "sourceModel": "fixed13650-summary-v1",
  "entry": { "long": <ConditionGroup>, "short": <ConditionGroup> },     // CE / PE direction targets
  "exit":  { "conditions": <ConditionGroup>, "opposite": "Exit|ExitAndReverse|Ignore" },
  "filters": { "sessionStart": "09:30:00", "entryCutoff": "15:00:00", "requireFreshVix": false, ... },
  "instrument": { "expiry": "BasketExpiry", "selection": { "rank": [...], "premium": { "min": 100, "max": 150 } } },
  "quantity": { "mode": "FixedLots|FixedUnits", "value": 10 },
  "position": { "maxConcurrent": 1 },
  "risk": { ...see §6... }, "execution": { ...see §6... }, "fees": { "schedule": "NseIndexOptions-<id>" } }
```
* `ConditionGroup = { "op": "AND|OR", "items": [ Condition | ConditionGroup ] }` (depth ≤ 4, ≤ 32 leaves).
* `Condition = { "metric": "<registry id>", "window": "5|10|50|100|Day|Bar", "cmp": ">|<|==|>=|<=", "value": <number> }`, or `{ "cross": "Up|Down", "metric": …, "level": <number|metric> }` (previous-frame vs current-frame), or `{ "metric": A, "cmp": …, "metricB": B }` for metric-vs-metric of the SAME unit.
* `==` on doubles is rejected unless the metric is declared discrete (sign, state enums, counts); otherwise `Between` with explicit tolerance is the only equality form (avoids silent float equality).
* Validation = schema + registry check (unit/type compatibility, window allowed for the metric, direction semantics) + policy completeness (§6). **Incomplete policy ⇒ the strategy cannot activate** and the reason is stored/visible. More than 30 enabled ⇒ the whole file set is rejected (no partial activation).
* Immutability: the canonical-JSON SHA-256 is the config identity. Configs are read at session start only; a changed file creates a new `(strategyId, version, hash)` row, takes effect from the NEXT session; an open position keeps the hash it was opened under. Same-version-different-hash is refused (like NPT1's run-identity check).

## 4. Typed metric registry

A fixed C# registry, not data: each entry = `{ id, sourceModel(+version), unit, direction convention, supported windows, aggregation (links to docs/MARKET_SUMMARY_METRICS.md), availability rule, accessor }`. Initial entries: futures (price Δ, strict/enriched ratio, MicroDev, OFI, OI Δ, activity), option grids (ATM/ITM/OTM/TOTAL × price, roll, strict, enriched, OI, MicroDev, OFI, IV, Δ-Greeks, activity, each CE / PE / Direction), coverage ratios, VIX level/Δ/%.
* Unavailable (null / stale / insufficient coverage) ⇒ the leaf evaluates to **Unknown**, never false/zero. Group semantics under three-valued logic are explicit: AND with any False = False; AND with Unknown and no False = Unknown; OR symmetric. An Unknown entry condition = no signal (and a counter `signalsSuppressedUnknown`); an Unknown exit condition = no exit signal (the mandatory exits in §6 still apply).
* Delta/Vega: compared in the REGISTRY unit (raw); the ×100 display scaling never reaches a threshold.
* Rolling refs: only the five summary windows plus a bounded prior-frame ring (K ≤ 3 frames, needed by crossovers/previous-vs-current). Longer path-dependent features (rolling means of per-bar values) are NOT in v1 (they need a per-bar series store and an approved definition).

## 5. Evaluation model (30 strategies)

* One deterministic pass per completed bar, on the observer's hand-off thread, strategies in ascending `strategyId`; conditions pre-compiled at activation into a flat array of delegates over frame accessors (no reflection, no LINQ, no per-bar allocation in the loop); shared features read from the frame. Time budget guard (p95 < 5 ms / p99 < 10 ms for 30 simple strategies, measured in Phase 8); overrun ⇒ metric + skip-with-record, never a throw into the observer.
* Concurrency only if profiling shows a need; the contract is order-independent determinism (strategies cannot see each other).
* Per-strategy isolation: own state object, own ledger rows, own risk counters; an exception in one strategy faults only that strategy (state `Faulted`, alert), never another or NPT1.

## 6. Per-strategy state machine and the explicit policy contracts

`Flat → (signal on bar t) → EntryPending → (fill on bar t+1 per policy) → Open → ExitSignal → ExitPending → Flat`, plus `Cancelled/Rejected` terminals. Intent identity `(runId, strategyId, configHash, barSeq, intent)` is unique ⇒ duplicate-signal prevention is a key constraint, not a check.

Policy fields that MUST be present and valid, else the strategy cannot activate: instrument selection rule + tie-breakers, premium range, quote freshness, spread limits, entry timing, buy/sell reference prices, slippage, quantity/lot validation, partial-fill handling, order lifetime and cancel/retry behaviour, position limits, capital allocation, fee schedule, session windows, unavailable/stale quote behaviour for entry AND exit, risk limits, terminal liquidation.

### 6.1 UNRESOLVED execution semantics (owner decisions; no rule is invented here)
Where NPT1 already fixes a value it is shown only as a reference, not as a V2 default.

1. **Entry timing:** fill on the NEXT completed bar's quote (NPT1) vs a latency-offset instant inside the next bar? NPT1's bar clock is M1; a 13,650-volume bar has irregular duration (≈ 1–2 min) – is "next completed bar" acceptable latency for V2, and may the wait span the 15:00 cutoff?
2. **Instrument choice:** which strike? (nearest to basket centre, premium-band 100–150 ranking like NPT1, fixed offset from centre?) Is the candidate set the ten basket contracts only or the NPT1-style union with the 09:30 anchor basket? Behaviour when no contract qualifies (cancel with reason vs wait).
3. **Reference prices:** entry at ask / exit at bid (NPT1) vs mid vs last; slippage model; whether the stale-bid exit rule (NPT1: age not gated) applies.
4. **Quote freshness for V2 entry/exit:** 2 s (NPT1) is a bar-clock policy value; what is the V2 limit, and what happens on a stale quote (skip, wait N bars, reject)?
5. **Partial fills:** `FillFractionOfDisplayed` semantics for entry and for EXIT (an unfilled exit remainder: retry every bar? at what price?). NPT1 allows `KeepFilledCancelRemainder` only on entry.
6. **Opposite signal:** `Ignore` / `Exit` / `ExitAndReverse`; for ExitAndReverse, the second leg's timing relative to the exit fill.
7. **Capital and sizing:** fixed lots vs % of per-strategy capital; per-strategy independent virtual capital vs a shared pool; rejection reason when premium exceeds capital; lot-size source (instrument master per day).
8. **Risk limits:** daily loss per strategy / per engine, max trades per day, cooldown after exit or loss, max open positions per strategy and globally; whether breach liquidates open positions (NPT1 `ShouldLiquidate`) or only blocks entries; interaction with the legacy kill switch (V2 must NOT read or toggle it – confirm).
9. **Stops / targets / trailing / max-hold:** NPT1 deliberately has none. May V2 configs contain them? If yes, evaluated on completed bars only (bar-clocked) or on quote ticks (needs a tick path = new feed; recommended: completed bars only).
10. **Session rules:** entry window (NPT1 09:30:00–15:00:00), terminal close at 15:00 at the latest bid, behaviour with no bid (`MandatoryExitPending`), what "stale feed / unverified bar" does to open positions.
11. **Fees:** which `CostScheduleEntry` and its effective-date selection; fee rounding.
12. **Rejection taxonomy:** the closed list of paper-order rejection / cancellation reasons that alerts and the ledger use (needed for rejection alerts); whether the engine itself can "fail execution" (infrastructure error) as a distinct class.
13. **Shadow semantics:** "shadow-mode capable" = full simulated lifecycle with its own ledger (assumed). Is there any variant where V2 only LOGS signals without simulated fills? (a `signalsOnly` flag is cheap but changes the accounting surface).
14. **Per-strategy vs combined reporting:** per-strategy accounting only (as required); is any aggregate "engine" panel allowed on Home, labelled as a sum of independent books?
15. **Config location and promotion:** file directory on the VM vs a table edited from the Dashboard (the brief says JSON; a Dashboard editor is out of scope unless requested). Who may change a version, and is a change mid-session ever allowed (proposed: never).

## 7. Persistence (new `pev2_` tables, existing trading database, additive only)

EF migration in the existing trading context + idempotent SQL (same pattern as `trading-schema.idempotent.sql`); no ALTER/DROP/TRUNCATE of any existing object; Host role gets INSERT/UPDATE/SELECT on `pev2_*` only. Proposed tables:
`pev2_schema_versions`, `pev2_config_versions` (strategy id, version, canonical JSON, SHA-256, validation result, enabled), `pev2_runs` (one per session: session id, frame model, code/commit identity, status, owner epoch), `pev2_frames` (run, bar seq, hash, compact payload, availability instant; unique `(run, bar_seq)`), `pev2_strategy_state` (checkpoint JSON + digest per strategy per bar), `pev2_signals`, `pev2_orders`, `pev2_fills`, `pev2_positions`, `pev2_ledger_entries` (gross/fees/net components), `pev2_risk_counters`, `pev2_events` + `pev2_notification_outbox` (durable event id, type, status, attempts, telegram id, uncertain flag), `pev2_recovery_log`.
Every row carries `run_id`, `strategy_id`, `config_hash`. Unique keys encode idempotency (`(run_id, strategy_id, config_hash, bar_seq, intent)` for signals/orders; `(order_id, fill_seq)` for fills). Nothing references an NPT1 table by FK.

## 8. Restart / replay determinism and isolation

* The frame journal (`pev2_frames`) + the config hash set fully determine every V2 decision: replaying the journaled frames through freshly built strategy state must reproduce signals, orders, fills, positions and ledger bit-for-bit (digest per bar compared to `pev2_strategy_state`); any mismatch ⇒ the run halts (like NPT1: never silently continue) and alerts.
* Recovery: rebuild from journal, resume at the first un-journaled bar; bars missed while the Host was down are never evaluated retroactively (NPT1's catch-up rule) and a gap is declared as a feed-health degradation that blocks entries until verified bars flow.
* Idempotent hand-off: a frame offered twice (retry after a store fault) is ignored by the unique key.
* Isolation from NPT1: separate sink with its own time budget and exception boundary; a V2 store fault never delays the NPT1 commit. Open decision: run V2 AFTER NPT1's `OnGroup` in the same poll (simplest, deterministic) vs on a bounded queue drained by its own worker (isolates latency, adds a persistence lag). Recommended: same thread, after NPT1, with the budget guard, revisit if Phase 8 shows pressure.

## 9. Notifications (alerts) for paper trades

Events: trade opened, trade closed, order rejected, partial fill, execution failure. Durable identity `pev2:<runId>:<strategyId>:<eventKind>:<intentOrFillId>`; outbox row created in the SAME transaction as the ledger/event row; a worker sends via `TelegramTextSender` with the screenshot-job state machine (Pending → Sending (durable before send) → Sent / DeliveryUncertain (never auto-resent) / Rejected-with-retry-after). Reconnect/refresh never replays (Dashboard toast uses the existing per-source baseline). Messages are labelled `[V2 SHADOW]` with strategy id/version/side; Telegram delivery is separately opt-in and OFF by default; no alert depends on any other subsystem. Screenshot-trigger integration for V2 events is an open question (NPT1 trades already trigger event captures: should V2 trades?).

## 10. Disabled-by-default, shadow-only, no broker

* `Trading:PaperEngineV2:Enabled=false` (config) ⇒ nothing is constructed, no table touched. Enabling also requires `ShadowOnly=true` (the only accepted value) and a valid config set.
* Assembly-level release-blocker tests (modelled on NPT1's `BrokerIsolationTests`): `PaperEngineV2.*` references no Ingestion/FlatTrade/Execution/HTTP/broker type; no code path creates an order object of the live system.
* A test asserts V2 SQL touches only `pev2_*` and that NPT1 ledger/position tables are unchanged (hash of NPT1 tables before/after a full V2 session).

## 11. Dashboard (Phase 7 hook, not designed in detail here)

Read-only service over `pev2_*`: per-strategy rows (identity incl. version/hash, side, entry/exit, gross/fees/net, MAE/MFE where available), clearly separated from NPT1 (`V2 SHADOW` identity), no combined portfolio result; Home compact summary; `/paper-live` untouched.

## 12. Implementation phases (after approval)

6a config schema + validator + registry + tests (pure) · 6b frame builder in the Host adapter + journal + determinism harness · 6c evaluator + state machine + simulated execution using `CostModel` · 6d `pev2_` schema + EF migration + idempotent SQL + disposable-PostgreSQL recovery tests · 6e alerts outbox · 6f isolation/broker/NPT1-unchanged release-blockers · 6g benchmark 1/10/30 strategies. Each step ends with the full suite green.

## 13. Test plan (maps to the brief)
1, several and 30 strategies; per-strategy isolation; no cross-strategy contamination; duplicate-signal prevention; same-frame determinism (permuted evaluation order); restart recovery and replay equality on disposable PostgreSQL; config version change (new version next session, open position keeps old hash); missing metric (Unknown propagation); stale quote; concurrent signal conditions on one frame; capital/risk rejection; invalid configs (>30 enabled, unknown metric, unit mismatch, incomplete policy, `==` on a float); NPT1 parity suites unchanged; no broker API reachable; V2 cannot alter the NPT1 ledger.

## 14. Risks
Execution-semantic gaps in §6.1 (largest); frame size/journal growth (bounded, ~220 bars/day × a few KB); the hand-off thread budget shared with NPT1; metric registry growth vs correctness (every metric needs its own documented contract); the 13,650-volume bar clock has irregular wall-time so wall-clock conditions (cutoffs) must be defined against the bar's end instant.
