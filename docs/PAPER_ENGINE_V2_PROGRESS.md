# Paper Engine V2 – development progress log

Branch `feature/market-summary-paper-engine-v2` (base `bfa9a86`, the pushed `origin/master`). Design: `docs/PAPER_ENGINE_V2_DESIGN.md`. Never deployed; no VM / production database access; nothing pushed or merged; no broker connectivity. Updated after every completed phase.

## Earlier work on this branch (context)
| Commit | Content |
|---|---|
| `18d88ea`, `79c420e` | Market Summary: pure aggregation library + Dashboard grids, missing-row semantics, screenshot contract |
| `65c25da`, `9593a91` | India VIX in the Futures Summary; sticky bar-quantity progress |
| `3521e2a` | Commentary removal (history kept; NPT1 verified-bar gate preserved exactly) |
| `d25ef5a`, `141d03c` | V2 design rev 2 / rev 3 (fixed decisions + frozen execution defaults) |

## Phase 6a – schema, registry, evaluator — `9288138`
Pure `NiftySignal.PaperEngineV2.Core`: strict `pev2-strategy/1` parser/validator (buy-only vocabulary, no live keys, frozen defaults), 284-metric typed registry, Kleene three-valued evaluator, canonical-JSON SHA-256 identity, 30-strategy set rules. Full suite **2423 passed, 0 failed, 0 skipped** (PostgreSQL included, no disposable databases left). 30-strategy evaluation p95 6.3 µs, 0 allocation (synthetic).

## Phase 6b – frames, handoff, journal contract — `e1b2e58`
`MarketStateFrame` (immutable, canonical lazy payload + hash, decision key, same-instant ordinals, availability-checked quotes/VIX), `FrameHandoff` (O(1) non-blocking offer, observable overflow), `FrameSequenceGuard`, `FrameConsumer` (journal-before-process, entry blocking, restart from journal), `pev2-journal/1` + in-memory journal (`IsDurable=false`), deterministic replay, `MarketStateFrameBuilder` (Frames project). Full suite **2486 passed, 0 failed, 0 skipped**; PostgreSQL included; no disposable databases left; tree clean.
Performance (in-memory, synthetic): observer-thread frame construction p50 83 µs / p95 202 µs / p99 1.2 ms (~361 KB allocated incl. the lazy payload); first-use payload+hash on the consumer p50 344 µs; `Offer` p99 0.2 µs (accepted) / 1.1 µs (full queue); consumer HandleAsync with 30 strategies p50 16.5 µs / p95 25.8 µs; end-to-end offer → processed p50 134 µs / p95 330 µs.
Limitations: nothing is durable; restart is modelled from the in-memory journal only.

## Source-dependent investigations (before Phase 6c) — evidence, local tick copy only
| Item | Evidence | Consequence |
|---|---|---|
| Option-quote cadence vs the 2 s freshness limit | 10 sessions (2026-09-04…10-09), NIFTY nearest-expiry options with ask Rs 100–150, 09:30–15:00 IST, two-sided quotes, 7–19 contracts/session: median gap 0.02 s, p95 1.0 s, p99 1.2 s; share of time with the latest quote older than 2 s: 0.00–0.28 % in nine sessions, **6.06 % on 2026-09-04** (one 203 s gap, early collection); older than 5 s: 0.00–0.20 % (5.73 % on 09-04). | 2 s is supported by the data for the liquid ATM-band contracts; keep 2 s as an explicit per-strategy value; report the evidence, do not relax. Feed gaps of 12–24 s do occur on a few sessions (2026-10-07, 10-01), so stale-quote handling is exercised in reality. |
| Level-1 size reliability | Same population (≈271 k quotes on 2026-10-08): bid1/ask1 quantity is a multiple of the lot size 65 in **100 %** of quotes; min 65, median 650, max ~128 k; zero top quantity 0 %; level-2 always present. | Units = contract quantity in lot multiples (verified). Displayed size is NOT proof of executable liquidity; V2 stays **all-or-none, filling the whole order at the quote like NPT1**, records the displayed size at the fill for later analysis, and implements no partial fills. |
| Instrument-master lot size | NIFTY options: lot size 65 on all 30 as-of dates (2026-09-04…10-30), never null/non-positive. SENSEX (20) and BANKNIFTY (30) options also appear from 2026-09-22 — V2 must always filter on `Underlying = 'NIFTY'`. | Lot size comes from the instrument master per trade date through an explicit provider; a missing value rejects the order (`LotSizeViolation`). |
| Terminal exit retries after 15:00 | No data decides the horizon; NPT1 uses 15:35 as its hard end. | Retry cut-off is a REQUIRED engine option (no default); the simulation keeps the exit pending and retries until it, then reports the position UNRESOLVED. Value to be set by the owner. |
| Queue capacity / max lag / overload recovery | No production measurement exists. | Required, validated parameters (6b); to be benchmarked in 6g. |

## Phase 6c – simulated execution — `9cc94bb`
New pure project `NiftySignal.PaperEngineV2.Execution` (references only Core + Trading.Core cost types): `StrategyRuntime` (Flat → EntryPending → Open → ExitPending → Closed | Unresolved, per strategy, fault-contained), `PaperEngine` (`IFrameProcessor`, ascending strategy id, idempotent per bar, optional `IPaperEventSink`), typed events with deterministic ids (`EntryOrderPlaced/Filled`, `EntryRejected`, `ExitRequested/Filled`, `PositionUnresolved`, `RiskBreached`, `StrategyFaulted`), closed `RejectionReason` taxonomy.
* **Causality:** a fill needs a quote available strictly after the decision (+ latency), fresh (≤ maxQuoteAgeMs), and within the order lifetime; same-instant sibling bars and the decision frame can never fill. Entry fills also need `EntriesAllowed` (blocked → order cancelled, fail closed); exits never need it.
* **Fills:** all-or-none, ask + slippage (entry), bid − slippage ≥ 0 (exit); displayed size recorded only; fees via the existing `CostModel` (schedule matched by `fees.schedule` version; no silent zero cost). Capital = virtual capital − committed premium + proceeds; lot size via `ILotSizeSource` (missing → `LotSizeViolation`).
* **Selection:** premium window inclusive; rank by proximity to the midpoint, centre distance, strike, token.
* **Exits:** discretionary, opposite signal (exit only, no reversal), stop/target/trailing (Points|Percent on bid marks; stale marks never trigger), max-hold bars, terminal at 15:00 IST with retries until the REQUIRED `ExecutionOptions.TerminalRetryUntil` (> 15:00; no default), then `PositionUnresolved`; day change closes the books (open → Unresolved); daily-loss breach blocks entries and optionally liquidates.
* **Fail closed at activation:** `FirstAfterDecision` (unsupported: frames hold one quote per token per bar) and unknown cost schedule → typed `ActivationFailure`, strategy never runs.
* **Tests:** 58 xUnit cases in `ExecutionTests` (causality incl. sibling bars/latency, staleness, selection ranking, capital/lot/risk, gating, exits, terminal/unresolved, 30-strategy isolation + permutation determinism, journal-replay ledger equality, release blockers: assembly references and banned vocabulary).
* **Full suite on the committed tree: 2525 passed, 0 failed, 0 skipped** (PostgreSQL included). No disposable databases of mine remain (only the pre-existing `niftysignal_trading_test` fixture and older volume-bar databases).
* **Limits:** in-memory only; `AsOfEligibleBarEnd` only; spread fraction uses the mid; realized-loss risk check excludes unrealized P&L; Unresolved positions are not auto-liquidated.
* No existing test was modified in this phase.
