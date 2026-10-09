# Trading Engine Roadmap (shared backtest / live paper engine)

Status: **Phase 1 (audit + contract), amended 2026-10-09 with Naveen's confirmed requirements — documents only.** No production code, config, schema, database, migration, deployment, merge or order path was changed.
Baseline: `origin/master` `1ef645be330d1d864ba50b6eb2dc614c744f9674` (fetched 2026-10-09), branch `trading-engine-phase1-contract`.
Companion: [`TRADING_ENGINE_PHASE1_CONTRACT.md`](TRADING_ENGINE_PHASE1_CONTRACT.md) (the implementation contract; **§A "Amendment 1" is authoritative**).

## Goal

One deterministic strategy + risk + simulated-execution engine that is executed **unchanged** by (a) historical replay and (b) live paper trading, hosting **several strategies and entry variants concurrently**, so the same recorded events produce the same features, decisions, order intents, fills, positions, risk counters and P&L. The execution adapter is chosen by configuration; a future broker adapter replaces only execution. Using the 18 recorded **development** sessions we later discover explicit, standalone-signal-family rules for NIFTY weekly-option buying and test them forward. No composite score. No forced trade count.

## Confirmed requirements now in the contract (Amendment 1)

- **Session (Asia/Kolkata):** entries `[09:30:00, 15:00:00)`; at 15:00 waiting opportunities expire and unfilled entry quantity is cancelled; every position targeted flat by 15:10 with an earlier liquidation start (placeholder 15:05), retry/escalation, explicit `UnresolvedAtClose` reporting, never "closed" without a fill; stops, targets and deadlines independent of bars (§A.1).
- **Timers:** replay advances through every scheduled deadline before a later market event, deterministic tie order, no reliance on end-of-stream flush (§A.2).
- **Variants:** concurrent variants with their own opportunities, orders, positions, accounting and config; one position per variant in V1 including pending reservations; ids and keys ready for several positions; uniqueness scoped to run/account/variant (no database-wide constraint); separate virtual accounts for comparative research (§A.3).
- **Entry variants:** base-strategy validity separate from extra entry conditions; A enters immediately, A2 waits for the executable ask to fall below a moving average of the *selected option's own* completed candles (N configurable); contract fixed throughout; explicit invalidation, re-arm and re-entry rules; limit-versus-recheck flagged as decision D-12 (§A.4).
- **Sizing & exits:** default requested quantity 650, validated against the date-specific lot size (65 on all 18 sessions), never silently altered; fixed-quantity and premium-budget modes; TP1 with lot rounding, average-entry reference, remainder rules and restart-safe idempotency; no hidden capital limit (§A.5–A.6).
- **Research risk policy (`ResearchV1`):** no max-trades limit, no daily-loss lockout or liquidation; per-variant daily SL recorded only; per-trade SL a separate exit rule; enforcement and a real-order profile preserved as separate, disabled configuration (§A.7).
- **Costs:** current Indian option-cost components researched; applicable to all 18 sessions; **provisional** because the NSE primary documents could not be opened (§A.9).
- **Research:** all 18 sessions are `Development`; later sessions are forward-eligible only via a session ledger; per-contract quote coverage measured before feasibility claims; execution placeholders tested for sensitivity (§A.10).

## Non-goals of every phase until stated otherwise

- No real orders; `Broker` adapter and the `RealOrder` profile cannot start. No change to `NiftySignal.AdaptiveObserver` calculations.
- No VM deployment, no live-hours restart, no merge to `master`, no `Host`/`Dashboard` registration before an edge exists.
- No strategy sweeps before the replay/live-path equivalence tests pass (Phase 2 exit gate).

## Phases

| Phase | Scope | Exit gate |
|---|---|---|
| **1 — Audit & contract** (this; amended) | Code audit, contract, research-data inventory, acceptance tests, bounded Phase 2 checkpoints. Docs only. | Naveen resolves or accepts the open decisions in contract §14 (notably D-1, D-12, D-13, D-9 before CP6). |
| **2 — Engine skeleton + plumbing proof** | Checkpoints **CP0–CP7** (contract §15): scaffold + determinism guard; time/events/timers; variants/opportunities/entry conditions; execution/costs/sizing/exits; risk profiles & audit; adapters & equivalence; persistence & recovery (needs D-9); close-out. Plumbing strategy only (no edge claim). | Acceptance tests T1–T48 green; zero warnings in new projects; adaptive tests unchanged. Each checkpoint stops for review. |
| **3 — Signal bridge & execution calibration** | Wire adaptive bar/Weak2 events as signal sources; measure per-contract quote coverage and feed-delay distributions; calibrate/sensitivity-grid execution placeholders; confirm the cost schedule from primary documents and the actual brokerage plan; replace the zero-latency Weak2/H5 observation with engine-executed paper fills for research. | Coverage and execution-feasibility report approved; cost schedule confirmed. |
| **4 — Research** | Standalone signal families + explicit gates on `Development` data, every variant registered before running, negatives recorded, sensitivity grid mandatory, `BACKTEST_RULES.md` applies. | Candidate rules documented; forward validation plan using ledger-eligible sessions. |
| **5 — Hosted live paper** | Host the engine (paper only) in the Windows service; weeks of forward paper vs replay reconciliation. | Reconciliation inside tolerance; VM verification. |
| **6 — Broker adapter (future, separate approval)** | `IExecutionGateway` implementation, broker reconciliation, `RealOrder` profile with approved limits. | Explicit written approval. |

## Principal risks (detail in contract §13)

1. Wall-clock coupling between live and replay (mitigated by deadline-stamped timers, §A.2).
2. Optimistic fills — the existing Weak2/H5 observation is zero-latency with no quote-age cap; waiting entries add a trigger-to-fill miss risk (D-12).
3. Uncapped research losses by design in `ResearchV1` (daily SL recorded only); the real-order profile must be separately approved.
4. Contamination: all 18 sessions are development data; forward evidence requires post-freeze, ledger-eligible sessions.
5. Cost sensitivity (≈ ₹246 per 650-unit round trip in the §A.9 illustration) and provisional cost rates.
6. Independent virtual accounts overstate what one real account could fill.
7. Scope creep into `Host`/`Dashboard` before an edge is found (project rule).

## Working rules carried forward

`CLAUDE.md` / `AGENTS.md` / `docs/BACKTEST_RULES.md` apply unchanged: verify against code, one hypothesis at a time, no look-ahead, no invented thresholds (every new constant labelled PLACEHOLDER until derived or approved), no risk management bolted on before an edge is confirmed (the exit rules in §A.6 are per-variant research parameters; ResearchV1 enforces only integrity, session and duplicate-prevention rules), `PENDING/DEFERRED (audit finding FNN)` comments for unfixed findings.
