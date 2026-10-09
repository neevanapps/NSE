# Trading Engine Roadmap (shared backtest / live paper engine)

Status: **Phase 1 (audit + contract) — documents only.** No production code, config, schema, deployment or order path was changed.
Baseline: `origin/master` `1ef645be330d1d864ba50b6eb2dc614c744f9674` (fetched 2026-10-09), branch `trading-engine-phase1-contract`.
Companion: [`TRADING_ENGINE_PHASE1_CONTRACT.md`](TRADING_ENGINE_PHASE1_CONTRACT.md) (the implementation contract and the evidence).

## Goal

One deterministic strategy + risk + simulated-execution engine that is executed **unchanged** by (a) historical replay and (b) live paper trading, so that the same recorded events produce the same features, decisions, order intents, fills, positions, risk counters and P&L. A future broker adapter must replace only the execution adapter. Using the 18 recorded sessions we later *discover and validate* explicit, standalone-signal-family rules for NIFTY weekly-option buying. No composite score. No forced trade count.

## Non-goals of every phase until stated otherwise

- No real orders. No broker adapter is enabled. Paper only.
- No change to `NiftySignal.AdaptiveObserver` calculations (they are parity-validated against frozen research; see `ADAPTIVE_LIVE_ROADMAP.md`).
- No VM deployment, no live-hours restart, no merge to `master`.
- No strategy sweeps before the engine's replay/live equivalence tests pass (Phase 2 exit gate).

## Phases

| Phase | Scope | Exit gate |
|---|---|---|
| **1 — Audit & contract** (this) | Verify code, write contract, research-data inventory, acceptance tests, Phase 2 checklist. Docs only. | Naveen reviews the report and resolves the "decisions required" list in the contract §14. |
| **2 — Engine skeleton + plumbing proof** | New pure core library (events, injected clock, order/position state machines, risk engine, simulated execution, audit trail), a trivial deterministic *plumbing* strategy (no edge claim), historical and live-paper adapters, new trading database, acceptance tests of contract §12. Bounded checklist in contract §15. | All §12 tests green: identical output historical-vs-live-path on recorded events; crash/restart without duplicate execution. Zero warnings, full test suite green. |
| **3 — Observation-to-intent bridge & execution calibration** | Wire adaptive completed-bar/Weak2 events into the engine as a *signal source* (still no edge claim). Calibrate latency / quote-age / spread parameters from the recorded `ReceivedAt − ExchangeTimestamp` and book data. Costs schedule verified against the current exchange/broker schedule. Replace the Weak2 H5 zero-latency "observation" semantics with engine-executed paper fills for research. | Execution-model parameters approved by Naveen; reconciliation report (contract §9.3) produced for the 18 sessions. |
| **4 — Research (discovery / validation)** | Standalone signal families + explicit gates, chronological splits, every tested variant logged, forward validation. Follows `docs/BACKTEST_RULES.md` and the research contract (§11). | A rule family clears validation *and* forward paper observation; otherwise negative results are recorded. |
| **5 — Hosted live paper** | Host the engine in the Windows service, paper only, with the kill switch and session-close guarantees; VM verification per `vm-deploy-check`. | Weeks of forward paper vs. replay reconciliation inside tolerance. |
| **6 — Broker adapter (future, separate approval)** | `IExecutionGateway` implementation for the broker; reconciliation against the broker order book; idempotent client order ids. | Explicit written approval by Naveen; out of scope until then. |

## Principal risks (detail in contract §13)

1. Silent divergence between backtest and live from wall-clock coupling (the live adaptive loop holds ticks back 2 s on wall-clock; legacy cadence is a 15 s wall-clock timer).
2. Optimistic fills: the existing Weak2/H5 observation takes the first post-trigger quote at zero latency, with no quote-age cap, choosing the strike from quotes of *all* candidates; any research built on it overstates executable edge.
3. Quiet-market exits: existing exit logic is cadence/bar driven and can be skipped when no score or no quote exists.
4. Overfitting 18 sessions, 13 of which are discovery data for the adaptive observer, and all of which have been seen by some earlier experiment.
5. Scope creep into `Host`/`Dashboard` before an edge is established (project rule).

## Working rules carried forward

`CLAUDE.md` / `AGENTS.md` / `docs/BACKTEST_RULES.md` apply unchanged: verify against code, one hypothesis at a time, no look-ahead, no invented thresholds (every new constant is labelled PLACEHOLDER until derived), no risk management bolted on before an edge is confirmed (the engine implements risk *controls* for safety; discretionary SL/TP exits are research parameters, not defaults), `PENDING/DEFERRED (audit finding FNN)` comments for any unfixed finding discovered.
