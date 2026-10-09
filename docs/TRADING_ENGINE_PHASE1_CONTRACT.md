# Trading Engine — Phase 1 Implementation Contract

Baseline: `origin/master` = `1ef645be330d1d864ba50b6eb2dc614c744f9674` (fetched 2026-10-09; local `master` was identical, tree clean).
Branch: `trading-engine-phase1-contract` (worktree `D:\Projects\NSE-phase1`). Roadmap: [`TRADING_ENGINE_ROADMAP.md`](TRADING_ENGINE_ROADMAP.md).

This document is a contract and an audit. **Nothing here is implemented.** Every constant marked `PLACEHOLDER` or `PAPER-DEFAULT (unapproved)` is a starting guess, not a decision (project rule: no invented thresholds).

## 0. How the evidence was gathered

- **V (verified by me)**: I opened the file/ran the query in this session.
- **A (audit pass)**: three read-only audit passes read the cited files in full and reported file:line; I spot-checked a sample (listed below) but did not re-read every claim. Treat A-items as "read by an auditor, to be re-confirmed by the Phase 2 implementer before relying on them".
- **U**: unclear/assumed; stated as such.

Spot-checks I performed (all matched the audit text): `AdaptiveObserverWorker.cs:32` `StableOrderingLag = 2 s` and use at `:159,:217,:309`; `PaperTradeSimulator.cs:16,25` ask+slippage / bid−slippage and `:20,29` brokerage; `Host/Program.cs:145,163,200` are the only `AddHostedService` calls; `LiveTradingEngine.cs:103-105` daily-loss formula over closed-trade P&L; `AdaptiveTickCleaner.cs:35-36` `AvailableAt` from `ReceivedAt`/`ExchangeTimestamp`; `OpeningVolumeProjectionV1.cs` 13 discovery dates; all data-coverage numbers in §11 come from my own read-only SQL.

## 1. Existing system — what exists, verified behaviour, reuse/extend/retire

### 1.1 Trading is not live today
Only `MarketDataIngestionWorker`, `LiveVolumeBarWriter`, `AdaptiveObserverWorker` are hosted in Host (V: `Host/Program.cs:145,163,200`). No paper executor is hosted (matches `ADAPTIVE_LIVE_ROADMAP.md` "Paper trading / orders: OUT OF V1"). The adaptive observer is watch-only.

### 1.2 Component inventory

| Component (path :: type) | What it does | Hosted? | Disposition | Ev. |
|---|---|---|---|---|
| `NiftySignal.Ingestion/FlatTrade/FlatTradeTickSource` + `FlatTradeFeedState` | WebSocket → `Tick` (top-5 depth when complete, 1-s `ft` exchange time, local `ReceivedAt`). Single receive loop, wire order, unbounded channel. | yes | **Reuse** (no change in Phase 2) | A |
| `Host/MarketDataIngestionWorker` (`RunTickLoopAsync`) | Single consumer: engine tick → dashboard → 200-tick/1-s DB batch. Skips (and loses) a tick if the per-tick body throws (`:347-356`). No unique key / idempotent insert (`:913`). | yes | **Extend** (loss path, tick sequence) — later phase, not Phase 2 | A |
| `Domain/Entities/Tick`, `Persistence/Configurations/TickConfiguration` | `ticks`: PK `Id`, indexes `(Token, ExchangeTimestamp)`, `(ReceivedAt)`; bid1..5/ask1..5 price+qty columns. No broker sequence number. | yes | **Reuse** | A; V (schema via `information_schema`/`pg_indexes` on `niftysignal_vm_copy`) |
| `AdaptiveObserver/AdaptiveIncrementalTickNormalizer`, `AdaptiveTickCleaner`, `Host/AdaptiveSourceTickReader` | Per-token dedupe; `AvailableAt = max(ReceivedAt, ExchangeTimestamp)`; read by `Id`, order by `(AvailableAt, Id)`. Shared by live, recovery, weak2 service. | yes | **Reuse as the canonical event-ordering definition** | V (cleaner), A |
| `AdaptiveObserver/AdaptiveObserverEngine.ProcessAvailabilityGroup` | Pure per-availability-group engine: spot, then options (by Id), then future last; throws on a late group (`:126-127`). Builds completed exact-volume bars, rolling state, weak labels, band flows, residuals, sidecars. No wall clock inside the engine. | yes (via worker) | **Reuse as the feature/signal producer** (no calc change) | A |
| `AdaptiveObserver/ExactAdaptiveFuturesBarBuilder`, `AdaptiveRollingStateTracker`, `AdaptiveWeak2Classifier`, `AdaptiveReadinessPolicy`, `OptionResidualModel`, `FuturesBasis`, `FuturesMicrostructure`, `OptionsSupplemental` | Pure calculators; frozen/parity-validated | yes | **Reuse unchanged** | A |
| `Host/AdaptiveObserverWorker` | 250 ms poll; releases groups with `AvailableAt <= UtcNow − 2 s` (V `:217`); session start/close by wall clock (`:87`, close 15:35). | yes | **Extend**: wall-clock decisions must move behind an injected clock/adapter; keep worker for observation | V, A |
| `Host/AdaptiveStateRecoveryService`, `AdaptiveObserverPersistence.PersistOrVerifyAsync` | Recovery = replay whole session's raw ticks through a fresh engine; persisted rows are *verified* field-by-field (mismatch throws). Resume key = raw `Id` cursor + `(LastProcessedAvailableAt, LastProcessedTickId)`. | yes | **Reuse the pattern** ("replay + verify, never overwrite") for the trading engine | A |
| `Host/AdaptiveSessionCoordinator` | 09:30 session freeze (opening volume → bar volume, strong threshold, ATM anchor), median fallback for late start. Immutable session row. | yes | **Reuse** | A |
| `Host/AdaptiveWeak2ObservationService` | Watch-only. Picks reversal-side weekly option whose *first post-trigger quote* ask ∈ [₹100,₹150] closest to ₹125 (constants hard-coded `:16-18`), entry=ask, exit=bid at trigger+5 bars, MFE/MAE. **Zero latency, no quote-age cap, no spread/size check, no costs, selection uses quotes of every candidate strike.** | yes | **Retire as an execution model; keep as a diagnostic.** Replace with engine-executed paper fills in Phase 3. | A |
| `Execution/PaperTradeSimulator` | Entry = ask + ticks×slippage; exit = max(0, bid − ticks×slippage); brokerage ₹20/order. | via unhosted engines/backtest | **Extend** (needs missing-quote policy, quote age, latency, partials, full cost schedule, per-fill cost fields) | V |
| `Execution/StrikeSelector` + `LiveFeatureEngine.BuildStrikeCandidates` | Nearest expiry; filters mid∈[100,150], bid/ask>0, spread≤2 % mid, OI≥100 000, IV non-null; ranks preferred-delta flag (hard-coded |Δ|∈[0.30,0.60]) → OI → volume. Candidate mid falls back to LTP when depth is missing; **no quote age**. | via unhosted engines | **Extend/replace** by the contract in §6; delta band is an unapproved constant | A |
| `Rules/EntryRuleEvaluator`, `ExitRuleEvaluator`, `ScoreSustainTracker` | Legacy composite rules: SL −10 %, partial +15 %, score flip/decay, time stop 120 min, SquareOff 15:15, entry window 09:30–15:00 (14:00 on expiry day), `MaxTradesPerDay` 30. | no | **Do not reuse as defaults** (discretionary SL/TP conflict with "no risk mgmt before edge"; legacy composite is retired). Keep tests as reference. | A |
| `Host/LiveTradingEngine` | Legacy composite entry/exit with DB-backed risk gates (kill switch, daily loss, consecutive loss, F48 capital, IV-rank, data-gap). Registered but **never called live**; used by `Backtest/BacktestRunner`. | registered, not called | **Retire from live; retain as the regression oracle for risk-gate semantics** | A |
| `Host/CoreScoreHysteresisTradingEngine`, `CoreScoreCrossoverTradingEngine` | Retired core-score engines; ~150 lines duplicated risk-gate code each; crossover can leave state desynced if the exit quote is missing (`:289-297`). | registered, not called | **Retire** | A |
| `Host/LiveOptionsScoreEngine`, `LiveFuturesCrossoverEngine`, `VolumeBarData/LivePaperTradeExecutor` | Dormant volume-bar paper path. LTP-only fills at decision time `UtcNow`, no costs, exits only on bar close; unique index `(AsOfDate, BarVolumeThreshold, Strategy, EntryBarIndex)` is a good idempotency pattern. | not registered | **Retire**; copy only the idempotency idea | A |
| `Backtest/BacktestRunner` | Drives real `LiveFeatureEngine` + `LiveTradingEngine` with historical ticks (3 s sample / 15 s cadence). Legacy engine only; fresh engine per day (no IV-rank history). | replay | **Retire after Phase 2** (superseded) | A |
| `MetricTrials/CoreScoreOptionSimulator`, `FutureDirectSimulator`; `VolumeBarData/TradeSimulator` etc. | Research simulators; mark fills, no slippage/spread, **exit falls back to entry price when no quote**, duplicated copies of rule logic. | replay | **Keep as research history; results are not live-equivalent** | A |
| `Backtest/PerformanceReportBuilder` | Per-trade closed-trade metrics; profit factor returns 0 for all-win; trades/day uses calendar span; no per-day/exit-reason/gross-vs-net/unrealised. | replay | **Extend** | A |
| `Domain` `PaperTrade`, `KillSwitchState`; `Persistence` `paper_trades` | One row = whole trade lifecycle; partial leg overwrites fields; no orders/fills tables; indexes on `EntryTime`, `ExitTime` only; **no unique/idempotency key, no `StrategyId` index**. Kill switch is a DB row toggled by Dashboard only; **nothing auto-trips it**; `Ruleset.KillSwitch` config is read by nothing. | partly | **Replace** with new trading schema (§8) | A |
| `VolumeBarData/TransactionCostCalculator` | Only code with STT/GST (STT 0.0625 % of exit premium, GST 18 % of brokerage per tests). Rates not independently verified. | research | **Reuse as a starting point only**; schedule must be verified (§14 D-7) | A |

### 1.3 Wall-clock and time handling (V/A)
- No injected clock in ingestion/trading/adaptive paths; `TimeProvider.System` is registered (`Program.cs:84`) but only notifiers use it (A).
- Decision-relevant wall-clock uses: `FlatTradeTickSource.cs:218` (`ReceivedAt = UtcNow`, which feeds `AvailableAt`); `AdaptiveObserverWorker` `nowUtc` (`:81,:111,:217,:309`); legacy cadence `MarketDataIngestionWorker.cs:401` (`ComputeCadence(now)` every 15 s) and `:536` (`Sample(UtcNow)` every 3 s) (A).
- IST offset is re-declared in several files; `Domain/IstTime` exists but is not used everywhere (A). Market hours hard-coded; holidays not modelled (A).
- `FlatTradeFeedState.cs:97`: if a message never carried `ft`, `ExchangeTimestamp` falls back to `receivedAt`; `ft` is 1-second resolution. In the recorded data the first `ExchangeTimestamp` of 2026-09-04 is `00:00:00` and others start ~06:07 IST (V: my query of `ticks`), so non-trading-hours/zero-time rows exist and must be filtered by the session window.

## 2. Backtest ↔ live gaps, ranked by severity

| # | Gap | Severity | Evidence |
|---|---|---|---|
| G1 | **There is no shared strategy/risk/execution implementation.** The adaptive path (the only live-validated signal source) has no trading logic; the trading engines consume a different (legacy) score, are unhosted, and simulators re-implement rules. Identical inputs do not yield identical decisions between MetricTrials/BacktestRunner/live engines. | Critical | §1.1, §1.2 |
| G2 | **Optimistic research fills.** Weak2/H5 observation: zero latency, no quote-age/spread/size limits, strike chosen from the first post-trigger quote of every candidate strike (requires information that arrives *after* the decision), no costs. Any rule derived from it overstates executable edge. | Critical | `AdaptiveWeak2ObservationService` (A) |
| G3 | **Wall-clock coupling.** Live adaptive processing is delayed 2 s by wall clock; legacy cadence and sampling run on wall-clock timers; session start/close by `UtcNow`. Replay uses event time. Same events can yield different timing live vs. replay. | High | V `:32,:217` |
| G4 | **Exits are cadence/bar driven.** Legacy: evaluated every 15 s, skipped when `CompositeScore == null` or when the held token has never ticked (SquareOff skipped). Dormant volume-bar path: 15:15 cutoff fires only on a bar close, else at 15:35 flush. A quiet market can delay a required exit. | High | A (`LiveTradingEngine.cs:39-42,234`; `LiveFuturesCrossoverEngine.cs:60-64`) |
| G5 | **LTP/entry-price substitution.** Legacy mark = bid ?? LTP; `MidPrice` falls back to LTP; `LivePaperTradeExecutor` is LTP-only; `CoreScoreOptionSimulator` exits at entry price when no quote. | High | A |
| G6 | **Incomplete execution & cost model.** No latency, partial fills, rejections, expiry, quote age (legacy `InstrumentState` has no timestamp). Only brokerage ₹20/order + 2-tick slippage in Host; no STT/exchange/GST/stamp (only in a research calculator). | High | V (`PaperTradeSimulator`), A |
| G7 | **Risk gaps.** Daily loss is realised-only and gates new entries only (V `LiveTradingEngine.cs:103-105`); kill switch never auto-trips and `Ruleset.KillSwitch` config is dead; no pending-order exposure reservation; no DB uniqueness for open positions; F48 capital sum over-reserves after a partial; legacy `MaxTradesPerDay` default 30 (project target is 5–10/day; requested cap now 5). | High | A |
| G8 | **Ingestion integrity.** No broker sequence; per-tick exception path drops a tick silently; no unique tick key; no inbound watchdog (stalled-but-connected socket undetected); `DataGap` end time is auth-ack not first tick; adaptive path does not consume `DataGap`; several readers order by `ExchangeTimestamp` only (not `(AvailableAt, Id)`); `BacktestTickSource` orders by `(ExchangeTimestamp, Id)` – a different order from the adaptive path. | Medium-High | A |
| G9 | **Recovery model.** Replay-whole-session only (no checkpoint): measured 7.6–15.5 s and ~1.9 GB working set for a full day (`ADAPTIVE_LIVE_PERFORMANCE_2026-10-08.md`, existing doc); multi-table persistence is not atomic (relies on recovery to back-fill); a late row with lower `Id` than the cursor is either never seen or loops the degrade/rebuild cycle. | Medium | A |
| G10 | **Constants & thresholds not derived.** ₹100–150 ask band, ₹125 target, delta 0.30–0.60, OI ≥ 100 000, 2 % spread, opening-volume regression are fixed numbers; conflicts with BACKTEST/CLAUDE "dynamic or DTE-based" rule if reused as decision thresholds. | Medium | A |
| G11 | **Repo hygiene.** `dotnet build` of the solution at the baseline has 3 warnings (`CLAUDE.md` requires 0); `ADAPTIVE_LIVE_ROADMAP.md:39` says "eight tables" but `AdaptiveObserverDbContext` has 17 (A). | Low | §16 |

## 3. Proposed architecture

Principle: **one deterministic core that is pure** (no I/O, no wall clock, no randomness, no static mutable state), driven by an ordered stream of events, emitting an ordered stream of outputs. Adapters feed events in and carry outputs out. The existing adaptive engine stays the feature/signal producer and is *wrapped*, not modified.

```
                     ┌───────────────────────── NiftySignal.Trading.Core (pure) ───────────────────────┐
 Event source  ───►  │ EngineEvent stream ─► Sequencer ─► SignalAdapter(IStrategy) ─► RiskEngine ─►     │
 (Historical or      │                         │ timers          │ decisions              │ intents     │
  LivePaper)         │                         ▼                 ▼                        ▼             │
                     │                    DeadlineQueue     DecisionAudit           OrderManager        │
                     │                                                                   │              │
                     │                          ExecutionSimulator (IExecutionModel) ◄───┘              │
                     │                                   │ execution updates                            │
                     │                                   ▼                                              │
                     │                          PositionBook + RiskCounters ─► EngineOutput stream      │
                     └──────────────────────────────────────────────────────────────────────────────────┘
        outputs ─► Persistence adapter (EF, replay-and-verify)     future: IExecutionGateway → broker
```

### 3.1 Proposed projects (names are proposals)

| Project | Responsibility | Dependencies |
|---|---|---|
| `NiftySignal.Trading.Core` | Event/output types, `EngineClock`/`DeadlineQueue`, `TradingEngine` (single entry point `Handle(EngineEvent) → IReadOnlyList<EngineOutput>`), `IStrategy`, `RiskEngine`, `OrderManager`, `PositionBook`, `IExecutionModel` + `SimulatedExecutionModel`, `ContractSelector`, `CostModel`, `DecisionAudit`, versioned `TradingConfig`. **No** EF, no HTTP, no `DateTime.*Now`, no `Random`. Enforce with an analyzer/banned-API test. | `Domain` only |
| `NiftySignal.Trading.Signals` | `IStrategy` implementations + the Phase 2 *plumbing strategy*; later the standalone signal families. Consumes `BarCompletedEvent` etc. | `Trading.Core`, `AdaptiveObserver` (read-only types) |
| `NiftySignal.Trading.Adapters.Historical` | Reads recorded ticks via the **same** `AdaptiveSourceTickReader` + `AdaptiveIncrementalTickNormalizer` semantics, runs `AdaptiveObserverEngine`, converts its outputs to `EngineEvent`s, drives `TradingEngine`. Virtual clock = event time. | `Trading.Core`, `AdaptiveObserver`, `Persistence` |
| `NiftySignal.Trading.Adapters.LivePaper` | Same event conversion over the live-polled stream; supplies *journaled* heartbeats; hosts the engine in a `BackgroundService`; simulated execution identical to historical. | `Trading.Core`, `AdaptiveObserver`, `Persistence`; wired in `Host` only after an edge is established |
| `NiftySignal.Trading.Data` | EF Core context/entities/migrations for the trading database (§8). | `Trading.Core` types |
| *(future)* `NiftySignal.Trading.Adapters.Broker` | `IExecutionGateway` implementation. Not Phase 2. | |

A single shared **event conversion** component (`AdaptiveEventTranslator`) is used by *both* Historical and LivePaper adapters, so feature and bar events cannot diverge between them. The only legitimate differences between the two adapters are: tick reader cursor (full vs. incremental), where the heartbeat comes from, and persistence flush cadence.

Rejected alternative: embedding trading logic inside `AdaptiveObserverEngine`/`AdaptiveObserverWorker`. It would put order logic into a parity-frozen calculation path and break the "watch-only, calculations unchanged" guarantee.

## 4. Events, ordering and the injected clock

### 4.1 Four times (never conflated)
| Name | Meaning | Source |
|---|---|---|
| `EventTime` | When the exchange says it happened | `Tick.ExchangeTimestamp` (1-s resolution) |
| `AvailableAt` | When the system could first know it: `max(ReceivedAt, ExchangeTimestamp)` | existing normalizer definition (V) |
| `DecisionTime` | `AvailableAt` of the event that triggered the decision (or the timer deadline); **never** wall-clock processing time | engine |
| `ExecutionTime` | `DecisionTime + latency` (§9); when the order reaches the simulated exchange | execution model |
Wall-clock processing time (`ProcessedAtWall`) is recorded for diagnostics/latency reconciliation only; it is never an input to a decision.

### 4.2 Event types (all immutable, carry `Seq`, `AvailableAt`)
`QuoteEvent` (per instrument top-of-book + depth-1 qty + LTP/volume/OI, `SourceTickId`), `BarCompletedEvent` (adaptive futures bar + rolling state + residual/band features, `BarSeq`, `EndAvailableAt`), `ExecutionUpdateEvent` (ack/fill/partial/reject/expire/cancel from the execution model or gateway), `TimerEvent` (deadline fired), `SessionEvent` (SessionStart, WarmupComplete, EntryCutoff, CompulsoryCloseStart, SessionEnd), `FeedHealthEvent` (stale/disconnected/recovered, derived from `DataGap` and quote age), `ConfigChangeEvent` (rejected mid-session; see §7).

### 4.3 Total order
The engine's input order is **`(AvailableAt, GroupRank, SourceTickId)`**, where an *availability group* is all ticks with equal `AvailableAt` (as in the existing engine, V/A) and inside a group non-future instruments precede the future, each by `Id` (existing `AdaptiveObserverEngine` rule, A `:131-133`). The engine reuses these rules rather than redefining them. Consequences, which are the contract:
- Equal timestamps → one group, processed atomically; the `BarCompletedEvent` produced by a group is emitted *after* the group's quote events and carries `EndAvailableAt == group AvailableAt`.
- A group with `AvailableAt <=` last processed → **rejected as late** (engine raises `LateEventFault`, enters `Degraded`, §6.5). It is never merged retroactively.
- Duplicate ticks (same payload as the previous raw tick of that token) are dropped by the existing per-token normalizer *before* the engine; a re-delivered event with an already-seen `Seq` is ignored (idempotent).
- Out-of-order across the live `Id` cursor is detected by the same mechanism as today (A: `AdaptiveObserverWorker` degrade-and-rebuild); the trading engine must treat that as a fault, never silently reorder.

### 4.4 Injected clock and timers
- `ITradingClock` exposes only `Now` = *engine time* = `AvailableAt` of the last processed event or fired deadline. Core has no other time source.
- Timers are **deadline-stamped**: `DeadlineQueue` holds `(deadline, kind, id)`. Before handling any event with `AvailableAt = T`, the engine fires, in `(deadline, id)` order, every deadline `< T` (and `== T` after the event for session boundaries, §6.3), with `DecisionTime = deadline` (not `T`). The outcome therefore does not depend on *when* it is processed, only on the event stream and deadlines.
- Live heartbeats: the LivePaper adapter injects `HeartbeatEvent(AvailableAt = wallNow − stableLag)` once per poll (even when no ticks arrive). A heartbeat can only cause due deadlines to be processed *earlier in wall time*; because decisions use the deadline as `DecisionTime`, replay without heartbeats produces identical outputs. Heartbeats are journaled (`engine_inputs`) for audit but are not required for replay equality. This is how a quiet market cannot block a required exit: session-close and risk deadlines fire on heartbeats in live, and on the next event or an explicit end-of-stream `Flush(sessionEnd)` in replay.
- The stable-ordering lag (existing 2 s) is a property of the adapter's *watermark* (`watermark = wallNow − stableLag`); the engine sees only watermark-ordered events. It is configuration (`StableOrderingLagSeconds`), versioned, and the historical adapter emulates it by processing groups strictly in `AvailableAt` order (equal to the live outcome whenever no late tick is injected).
- Banned in Core: `DateTime(Offset).Now/UtcNow`, `Stopwatch`, `TimeProvider.System`, `Task.Delay`, `Random`, `Guid.NewGuid` (ids are deterministic hashes, §8.3). Enforced by a test scanning the assembly's IL/source.

### 4.5 When a completed-bar signal becomes available
The bar completes inside the availability group whose tick fills the target volume (A: `ExactAdaptiveFuturesBarBuilder.Add :80-83`). The signal's `AvailableAt = bar.EndAvailableAtUtc`. Only that bar's closing-group data is in the feature vector (existing no-look-ahead: option quotes with `AvailableAt <= bar end`, 5 s freshness, A). Earliest allowed order: `ExecutionTime = EndAvailableAt + latency`. The strategy may read **only** `BarCompletedEvent` fields and engine-held quote state with `AvailableAt <= DecisionTime`.

## 5. Strategy → order → position state machines

### 5.1 Interfaces
```
interface IStrategy {                       // pure; same instance type in all runs
  string StrategyId; string Version;
  StrategyDecision OnBar(BarCompletedEvent bar, EngineView view);   // signal families
  StrategyDecision OnQuote(QuoteEvent q, EngineView view);          // optional, e.g. exit signals
  StrategyDecision OnTimer(TimerEvent t, EngineView view);
}
record StrategyDecision(DecisionKind Kind /*None|Enter|Exit*/, Side, ContractRequest? Contract, ReasonCodes Reasons, IReadOnlyDictionary<string,decimal> Inputs);
```
`EngineView` is read-only: positions, pending orders, risk counters, latest quotes with ages. Strategies **cannot** place orders; they emit decisions. Risk and execution are separate stages and cannot be bypassed.

### 5.2 Decision pipeline (every decision audited, accepted or rejected)
`StrategyDecision` → `ContractSelector` (entries only; §6) → `RiskEngine.PreTrade` (§10) → `OrderIntent` → `OrderManager` → `IExecutionModel` → `ExecutionUpdate` → `PositionBook`/`RiskCounters` update. Each hop writes a `DecisionAudit` row with stage, outcome (`Accepted`/`Rejected`/`Deferred`), reason code(s) and the exact input values (quote ages, spreads, counters) that determined the outcome.

### 5.3 Order states (append-only `order_events`)
`Created → Submitted → {Working → PartiallyFilled}* → {Filled | Cancelled | Expired | Rejected}`; `CancelRequested` is an intermediate. Illegal transitions throw (fault). An order's identity is deterministic (§8.3). Order types in the paper model: **marketable limit** only (limit = opposite-side top-of-book at ExecutionTime ± configured collar); no market orders, so a missing/wide book cannot create unbounded fills.

### 5.4 Position states
`Flat → EntryPending → (Open | PartiallyOpen) → ExitPending → Flat`; plus `ExitFailed` (see §9.5) which is **not** `Flat`. One logical position at a time: the state machine admits no second `EntryPending` unless `Flat`. A partially filled entry is `PartiallyOpen`: the unfilled remainder is cancelled at the entry-expiry deadline (default policy: cancel remainder, manage the filled part as the position; alternative: all-or-none, §14 D-5). An exit may itself be partial: the position stays `ExitPending` with remaining quantity until flat.

## 6. Contract selection, session phases, faults

### 6.1 Fixed contract at entry, preserved through exit
`ContractSelector` runs once, at `DecisionTime`, using only quotes with `AvailableAt <= DecisionTime` and age ≤ `MaxQuoteAgeMs`. Output `SelectedContract {Token, Strike, OptionType, Expiry, LotSize, SelectionPolicyVersion, CandidateSnapshot[]}` is written to the `DecisionAudit` and copied onto the `OrderIntent` and `Position`. The exit intent uses `Position.Token` and the engine **rejects** any exit for a different token. No roll, no re-selection. Candidate snapshot (every candidate with its quote, age, spread, reason for exclusion) is stored so selection can be audited.
Selection constants (premium window, preferred delta, OI/spread minimums) are **not** approved parameters of this contract: the contract only fixes the *mechanism* (pure function of quotes at DecisionTime, versioned policy, candidate snapshot). Values must be dynamic/DTE-derived per `BACKTEST_RULES.md` or explicitly approved (§14 D-4). The existing Weak2 service's "first post-trigger quote of every strike" is explicitly not an allowed selection input.

### 6.2 Rolling ATM baskets
Rolling ATM±k baskets (band flows, residual anchors) are **diagnostic features only**. They are never a held position, never an `OrderIntent` target. Only a fixed single contract can be held.

### 6.3 Session phases (engine-time, from versioned `SessionConfig`)
| Phase | Rule |
|---|---|
| Pre-open / Warm-up | Events are processed (features advance); decisions are `Rejected(reason=Warmup)`. Warm-up complete when `AdaptiveReadinessPolicy` (10 consecutive valid bars, A) **and** the adaptive session is frozen (09:30 or the persisted late-start fallback, A) |
| Trading | Entries allowed while `EngineTime < EntryCutoff` |
| Entry cutoff | `EntryCutoff` (PLACEHOLDER, unapproved; legacy 15:00 / 14:00 on expiry day) – after it all entry decisions are `Rejected(EntryCutoff)`; pending entry orders are cancelled at the cutoff deadline |
| Compulsory close | At `CloseStart` (PLACEHOLDER; legacy 15:15) a `TimerEvent` forces `Exit(reason=SessionClose)` for any position or pending entry; retried every `ExitRetryMs` until flat or `CloseDeadline` |
| Session end | `SessionEnd` (≤ adaptive hard close 15:35, A). Anything not flat is `ExitFailed` → fault alert; **never** marked flat without a fill |
The deadlines are `TimerEvent`s created at session start from config, so they fire in a quiet market (§4.4).

### 6.4 Restart recovery
Recovery is *replay-and-verify*, the pattern already proven for the adaptive observer (A: `AdaptiveStateRecoveryService`):
1. Load the immutable `trading_session` row (config hash, engine version, adaptive session id). If config hash/engine version differ from the running binary → **refuse to trade** (read-only/flatten-only mode, alert).
2. Rebuild adaptive state exactly as today; replay all `EngineEvent`s from session start through the watermark into a fresh `TradingEngine` with persistence in *verify* mode: each output (decision, order, order event, fill, position, counter) is compared with the persisted row by deterministic id; mismatch → fault.
3. Outputs that exist persisted but are not re-derived, or derived but not persisted *and older than the watermark*, are mismatches. Outputs not yet persisted at crash time are inserted (idempotent unique keys).
4. Simulated execution is deterministic, so a pending paper order is re-derived, not re-sent. With a future broker gateway the client order id = `OrderId` and the gateway must `QueryOrder` first; "unknown" ⇒ halt, not resend.
5. Open position / pending order after recovery is exactly the replayed state. No second entry can be created because the decision id (§8.3) already exists.
Replay memory/time precedent: 7.6–15.5 s and up to ≈1.9 GB working set per full session (existing measurement). A checkpoint (§8.2) is an optimisation to add only if measured need arises; correctness must not depend on it.

### 6.5 Fault policy
`Degraded` (late event, parity mismatch, persistence failure, stale data beyond `HaltStaleSeconds`): **entries disabled**; exit management stays active using the best executable quote available, and compulsory close timers still run. Faults are persisted and alerted. Leaving `Degraded` requires successful recovery verification; the engine never self-clears a parity mismatch.

## 7. Versioned configuration and the audit trail

- `TradingConfig` is a single immutable record: `{ConfigVersion, Session, Risk, Execution, Costs, ContractSelection, StrategyParams, StableOrderingLag, ...}`. Its canonical JSON → SHA-256 `ConfigHash`. Stored once in `trading_config_versions`; every session, decision, order and position carries `ConfigHash` + `EngineVersion` (assembly informational version incl. git SHA).
- Mid-session config change: forbidden. A `ConfigChangeEvent` is rejected and logged; changes apply from the next session. (The legacy `ValidatedOptionsMonitor` hot-reload is deliberately not used for engine parameters.)
- Every strategy `Enter`/`Exit` decision, accepted **or rejected**, produces a `decision_audit` row (§8) with: `DecisionId`, stage, outcome, ordered reason codes, `DecisionTime`, trigger event ref (`BarSeq`/`SourceTickId`), inputs snapshot (counters, quote ages, spreads, exposure computation), `ConfigHash`. A human must be able to answer "why did/didn't we trade at 11:42:07?" from this table alone.

## 8. Persistence (new database `niftysignal_trading`, separate like `niftysignal_adaptive_observer`)

### 8.1 Entities, keys, indexes (proposal)
All tables: `bigint Id` surrogate PK; natural unique keys below carry idempotency. Money `numeric(18,4)`; UTC `timestamptz`; `RunMode` ∈ {`Historical`,`LivePaper`}; `RunId` identifies one execution of a session (so historical and live outputs can coexist and be diffed).

| Table | Unique key | Other indexes / notes |
|---|---|---|
| `trading_config_versions` | `ConfigHash` | canonical JSON, created-at |
| `trading_sessions` | `(TradeDate, RunMode, RunId)` | FK adaptive session id (logical, other DB), `ConfigHash`, `EngineVersion`, status, started/ended |
| `engine_checkpoints` *(optional, later)* | `(SessionId, Seq)` | source cursor `(AvailableAt, TickId)`, state hash; never required for correctness |
| `decision_audit` | `(SessionId, DecisionId, Stage)` | `(SessionId, DecisionTime)`, `(SessionId, Outcome)`; reason codes array; inputs JSONB |
| `orders` | `(SessionId, OrderId)` | `(SessionId, State)`; DecisionId FK; side, qty, limit, expiry, contract token |
| `order_events` (append-only) | `(OrderId, EventSeq)` | state transition, qty, price, `ExecutionTime`, quote ref (`SourceTickId`, age, bid, ask, sizes) |
| `fills` | `(OrderId, FillSeq)` | qty, price, per-fill cost breakdown (brokerage, STT, exchange, GST, stamp, slippage), quote ref |
| `positions` | `(SessionId, PositionId)` **plus partial unique index `(SessionId) WHERE State <> 'Flat'`** (one open position) | entry/exit decision ids, contract, qty, avg prices, realised/unrealised, state |
| `position_events` (append-only) | `(PositionId, EventSeq)` | transitions |
| `risk_counters` | `(SessionId, Seq)` | trades-today, reserved exposure, realised net, unrealised liquidation value, daily-loss state; snapshot after each state-changing output |
| `engine_faults` | `(SessionId, FaultSeq)` | kind, details |
| `engine_inputs` (optional) | `(SessionId, Seq)` | journaled heartbeats / config-change attempts (raw ticks stay in `ticks`) |

### 8.2 Checkpoints
Not required. If added: a checkpoint is a *cache* (state blob + `StateHash` + source cursor) and recovery must still be able to verify against a full replay in a test.

### 8.3 Deterministic ids / idempotency
`DecisionId = H(ConfigHash, StrategyId, TradeDate, TriggerKind, TriggerSeq(BarSeq|timerId), Ordinal)`; `OrderId = H(DecisionId, Leg, Attempt)`; `FillSeq` increments per order from the execution model; `PositionId = H(SessionId, EntryDecisionId)`. No GUIDs, no identity values in business keys. Inserts are `INSERT … ON CONFLICT DO NOTHING` + verify equality (existing `PersistOrVerifyAsync` pattern, A). Multi-row outputs of one engine step are written in **one transaction** (fixing the non-atomic multi-table weakness, G9).

## 9. Execution assumptions

### 9.1 Rules (apply to historical and live-paper identically)
1. **No retrospective fills.** An order created at `DecisionTime` cannot fill at any quote with `AvailableAt <= DecisionTime` unless that quote is also the latest at `ExecutionTime` (it is evaluated at `ExecutionTime`, not "at the price that generated the signal"). Rejecting a stale quote is not "using" it.
2. **Buy against ask, sell against bid.** Entry fills at the ask side, exit at the bid side, level-1 prices/sizes only for Phase 2 (the `ticks` table stores 5 levels but the adaptive projection keeps level 1; level-2+ walking is a later extension, flagged).
3. **Executable quote at `ExecutionTime`** = the latest quote of that token with `AvailableAt <= ExecutionTime`, two-sided (`Bid>0`, `Ask>0`, `Ask>=Bid`), `ExecutionTime − AvailableAt <= MaxQuoteAgeMs`. If none: the order stays `Working` until `OrderExpiryMs`; then `Expired` (entry) or retried/escalated (exit, §9.5).
4. **Never substitute LTP, last mid, or the entry price** for a missing executable quote — in selection, marking, fills, stops, or liquidation. If an unrealised value cannot be marked, it is recorded as `Unmarkable` and the risk engine uses the conservative rule in §10.4.
5. **Latency** `ExecutionLatencyMs` (PLACEHOLDER): applied from `DecisionTime`. Phase 3 calibrates it from recorded data (`ReceivedAt − ExchangeTimestamp` distribution and measured live decision lag). It must be ≥ the live adapter's measured processing lag or live paper is *more optimistic than real*; the live adapter reports `WallLagMs = ProcessedAtWall − DecisionTimeWall` and a counter of orders where it exceeded the modelled latency.
6. **Spread / liquidity checks (pre-trade, on the quote at `ExecutionTime`)**: `Spread <= MaxSpread` (absolute ₹ and % of mid, both PLACEHOLDER), `AskQty >= requested qty` (or partial per §9.2), bid ≥ `MinBid`.
7. **Partial fills**: fill quantity at each quote = `min(remaining, displayed level-1 qty × FillFractionOfDisplayed)`; the remainder rests until expiry; subsequent fills use later quotes. Quantities are lot multiples. (`FillFractionOfDisplayed` PLACEHOLDER; D-5.)
8. **Order expiry** `OrderExpiryMs` (entry) and `ExitRetryMs` (exit) PLACEHOLDER; entry never outlives the entry cutoff.
9. **Costs** from a versioned `CostSchedule`: brokerage per order (existing ₹20, V), STT, exchange transaction charge, SEBI, stamp duty, GST, and slippage in ticks. Existing Host path lacks all but brokerage+slippage (G6). The schedule values are **unresolved (D-7)**; the research calculator's STT 0.0625 % is *not* adopted until verified. Costs are computed per fill and stored per fill.
10. Fill price per fill = quote price ± `SlippageTicks × tickSize` (existing mechanism, V; slippage default PLACEHOLDER).

### 9.2 Pending entries / partial positions / cancellation / rejected exits
- Pending entry holds a risk reservation (§10.3) until `Filled/Cancelled/Expired/Rejected`; reservation is released by the **actual** unfilled remainder.
- Partial entry at expiry: cancel remainder; position quantity = filled quantity; strategy exit rules apply to it.
- A signal to exit while the entry is still `Working`: cancel the entry first; if any part filled, exit that part.
- Exit rejected/expired: the position stays `ExitPending`→`ExitFailed`; the engine retries every `ExitRetryMs` with the then-executable bid, escalates alerts at N failures, keeps `Degraded`. **A position is never closed in the books without a fill** and P&L is never "assumed" at session end; an unfilled position at `SessionEnd` is reported as `UnliquidatedAtClose` with last executable bid/time and excluded from realised P&L but included in the daily summary as a risk event.

### 9.3 Stale data, disconnect
- `QuoteAge > MaxQuoteAgeMs` for the held token ⇒ no fills and no marks from it; position risk uses §10.4.
- Feed disconnect (`DataGap` open, or no events for `HaltStaleSeconds` – inbound watchdog does not exist today, G8, so the engine derives staleness from event time vs. heartbeat time): entries disabled; exits attempt against the last *fresh* executable quote only; compulsory-close timers keep running; on recovery, normal operation resumes only after a verified `FeedHealthEvent(Recovered)` and a fresh two-sided quote.

### 9.4 What must match exactly vs. what needs reconciliation
| Output | Historical vs. live-paper path on identical recorded events | Notes |
|---|---|---|
| Features, bars, rolling state, signals | **Exactly equal** (field-equal; numerics within the existing declared tolerances) | already proven for the adaptive observer |
| Decisions, rejects, reason codes, order intents, order ids | **Exactly equal** | |
| Simulated fills, positions, counters, P&L | **Exactly equal** (same `IExecutionModel`, same quote stream) | |
| Persisted `DecisionTime`/`ExecutionTime` | **Exactly equal** (event-time) | |
| `ProcessedAtWall`, wall lag, poll cadence | Not comparable by design | reported only |
| Live paper vs. a *real broker* (future) | **Reconcile**, don't expect equality: latency, queue position, partial-fill behaviour, rejections, exchange charges, quote-snapshot vs. true book | per-order slippage-vs-model report; drift alarms |
| Live paper vs. historical **when the live feed dropped/reordered ticks** | Reconcile: ticks lost on the live path (G8) or arrival-order differences change `AvailableAt` | compare persisted ticks used by the historical run vs. the live run's consumed ticks (tick-id sets + hash) |

## 10. Risk controls

### 10.1 Configuration (all in `TradingConfig.Risk`; **no approved values exist** — see §14)
`LotsPerTrade`/sizing rule, `PaperCapital`, `MaxPremiumExposure`, `MaxLossPerTrade`, `MaxDailyLoss`, `MaxTradesPerDay = 5` (requested), `MaxOpenPositions = 1` (requested), `EntryCutoff`, `CompulsoryCloseStart`, `KillSwitch` source.

### 10.2 Hard rules (not configurable below these)
- Maximum 5 *entry orders that reach a fill* per trading day; one position at a time (including pending, partial, exit-pending states). No minimum trade count is ever enforced.
- A stronger signal never bypasses a risk gate. Gates are evaluated in a fixed order and **all** failing reasons are recorded.

### 10.3 Reservation / duplicate prevention
At entry intent creation the engine atomically reserves: `reservedTrades += 1`, `reservedPremium = qty × limitPrice + estimatedEntryCosts`. Reservation counts against `MaxTradesPerDay`, exposure and "one position". It is released (or reduced to the filled amount) on `Cancelled/Expired/Rejected` or partial fill remainder. A new entry decision while any of `{EntryPending, PartiallyOpen, Open, ExitPending, ExitFailed}` exists is rejected `Rejected(PositionActive)`. A decision whose `DecisionId` already exists is a no-op (duplicate). DB-level partial unique index (§8.1) is the last line of defence.

### 10.4 Daily-loss definition (recommendation, D-3)
`DailyPnL = RealisedNet + UnrealisedLiquidationNet`
- `RealisedNet` = Σ closed-fill proceeds − cost of closed quantity − **all costs** (brokerage, taxes, slippage already in fill prices).
- `UnrealisedLiquidationNet` = open qty × (**current executable bid** − avg entry fill price) − **estimated exit costs** (using the cost schedule), i.e. what liquidation would net now, not mid/LTP.
- If the open contract has no fresh executable bid (`Unmarkable`): conservatively mark at **zero bid value** (full premium lost) for limit evaluation, until a fresh bid arrives. This is a risk-side conservatism only; it is not written to realised P&L.
- Breach (`DailyPnL <= −MaxDailyLoss`): entries disabled for the day **and** the open position is force-exited (not just "no new entries" as in the existing realised-only gate, G7). Evaluated on every quote event for the held token and at timers (between bars).
- Per-trade loss control (`MaxLossPerTrade`) uses the same liquidation-value definition and triggers a force exit; it is a *safety control*, not a research stop-loss (research SL/TP stay out of discovery runs per project rule; the control's value is a documented safety parameter and its trigger is reported as exit reason `RiskPerTrade`).
- Kill switch: engine-owned flag, set by (a) operator via Dashboard writes to a `kill_switch` row read at timers/heartbeats, (b) automatic trip on daily-loss breach or `Degraded`. Effect: entries off, exits and compulsory close remain on. (Existing DB row + dead config are not reused as-is.)

### 10.5 Capital/exposure
`CommittedPremium = Σ open (avg entry × remaining qty) + reservations ≤ MaxPremiumExposure ≤ PaperCapital`; partial exits reduce commitment (fixing the over-reservation noted in G7). Costs included in the check.

### 10.6 Unresolved risk values and labelled **paper-only** proposals
| Item | Status | Paper-only proposal (UNAPPROVED, derived only for plumbing tests) |
|---|---|---|
| Paper capital | unresolved | ₹50,000 *(legacy default, V in `CapitalConfigOptions` per audit)* |
| Lot size | verified from instruments table | 65 (V: `instruments.LotSize` for NIFTY on all 18 `AsOfDate`s) |
| Lots per trade | unresolved | 1 lot |
| Max premium exposure | unresolved | ₹16,250 (= 2 lots × ₹125 × 65; sized for a ~₹125 premium and the legacy 130-unit size) – recompute with D-2 |
| Max loss per trade | unresolved | — leave **disabled** (null) in discovery; enable only after a signal has edge |
| Max daily loss | unresolved | ₹5,000 (10 % of paper capital; legacy was 20 %/₹10,000) |
| Max trades/day | requested | 5 |
| Entry cutoff / close start | unresolved | 15:00 (14:00 on weekly-expiry days) / 15:15, from legacy rules (placeholders) |
| Latency, quote age, spread, fill fraction, expiries | unresolved | latency 1000 ms, quote age 2000 ms (< the 5 s adaptive freshness rule), spread ≤ 3 % of mid and ≤ ₹2, fill fraction 1.0 with level-1 cap, entry expiry 5 s, exit retry 1 s — all **PLACEHOLDER** pending Phase 3 calibration |
| Costs | unresolved | brokerage ₹20/order (existing); STT/exchange/GST/stamp schedule must be supplied/verified (D-7) |

## 11. Research contract (Phase 4; fixed now so Phase 2/3 do not leak into it)

### 11.1 Verified session and data availability (V; read-only SQL on local `niftysignal_vm_copy`, 110,517,168 estimated rows)
The "18 sessions" exist: **2026-09-04 … 2026-10-01**, 18 distinct trading dates in `ticks` (IST). `instruments` has an `AsOfDate` snapshot for each (≈80 NIFTY option contracts, 2 expiries, strikes ≈ ±475–500 around spot, lot size 65). Weekly expiries are Tuesdays: **09-08, 09-15, 09-22, 09-29 are expiry days (4 of 18)**; 09-04 uses expiry 09-08 (DTE 2).

| Session | Ticks (all tokens) | Tokens | Level-1 bid/ask present | Received first–last (IST) | Role in prior work |
|---|---:|---:|---:|---|---|
| 09-04 | 3,861,744 | 83 | 93.3 % | 09:25:36–16:58:33 (late start; trailing non-session ticks; first exchange time `00:00:00`) | in the 18-session diagnostics tables; not an adaptive discovery date; any strategy-level use not confirmed |
| 09-08 | 4,313,612 | 84 | 93.8 % | 08:45:57–15:35:08 | **adaptive discovery** |
| 09-09 | 3,998,088 | 84 | 93.1 % | 08:45:32–15:35:12 | adaptive discovery |
| 09-10 | 3,952,562 | 83 | 93.0 % | 08:45:57–15:35:05 | adaptive discovery |
| 09-11 | 4,305,336 | 83 | 93.5 % | 08:46:02–15:35:06 | adaptive discovery |
| 09-15 | 4,860,981 | 83 | 89.8 % | 08:45:52–15:35:19 | adaptive discovery |
| 09-16 | 4,297,937 | 83 | 88.7 % | 08:45:29–15:35:33 | adaptive discovery |
| 09-17 | 4,412,082 | 83 | 89.0 % | 08:45:09–15:35:13 | adaptive discovery |
| 09-18 | 4,368,784 | 83 | 89.3 % | 08:45:22–15:35:25 | adaptive discovery (also volume-bar holdout) |
| 09-21 | 4,458,263 | 83 | 89.5 % | 08:45:50–15:35:23 | adaptive discovery (also volume-bar OOS day) |
| 09-22 | 8,961,800 | 165 | 94.8 % | 08:45:30–15:35:46 | adaptive discovery |
| 09-23 | 8,407,183 | 165 | 94.5 % | 08:45:39–15:35:44 | adaptive discovery |
| 09-24 | 8,203,740 | 165 | 94.1 % | 08:45:56–15:35:17 | adaptive discovery |
| 09-25 | 8,930,138 | 167 | 92.0 % | **08:57:43**–15:35:46 | adaptive discovery |
| 09-28 | 9,814,018 | 167 | 92.6 % | 08:45:40–15:35:45 | *not* adaptive discovery |
| 09-29 | 11,072,823 | 167 | 93.1 % | 08:45:20–15:35:24 | *not* adaptive discovery |
| 09-30 | 8,238,670 | 167 | 91.3 % | 08:45:56–15:35:00 | *not* adaptive discovery |
| 10-01 | 8,303,530 | 167 | 91.2 % | 08:46:10–**15:29:48** (ends before 15:30) | *not* adaptive discovery |

Notes: token counts ≈ double from 09-22, consistent with SENSEX/BANKNIFTY collection (U – see `SENSEX_BANKNIFTY_TICK_COLLECTION.md`; not verified here). "Level-1 bid/ask present" is the share of all-token rows with a populated `bid1/ask1` (full-depth requirement, §1.3); it is **not** per-contract executable-quote coverage, which must be measured per contract/time bucket in Phase 3 before any fill-model claim. The query used `ReceivedAt` for the date; `ExchangeTimestamp` has quirks (`00:00:00`, ~06:07 starts). 09-04 and 10-01 have atypical session edges; 09-25 starts late. **Not verified:** the VM database (sessions after 10-01, e.g. the 10-05…10-08 live watch-only sessions, are not in the local copy; I have no VM access in this phase); contents of `niftysignal_backtest_analysis`/`niftysignal` local DB (7.9 M ticks, 09-04…09-07 only, not the research set).

### 11.2 Examined data — do not label as untouched
- The 13 dates **09-08 … 09-25** are the adaptive observer's *discovery* sessions (V: `OpeningVolumeProjectionV1.DiscoveryOutOfFold`).
- 09-04 and **09-28, 09-29, 09-30, 10-01** were not adaptive discovery dates and the roadmap states "no sealed dates used" for the historical gate (A, doc), **but** all 18 sessions have been viewed by diagnostics: live-loop performance validation used 09-21, 09-23, 10-01; the tick-rate table and notification-volume replay used all 18 (`ADAPTIVE_LIVE_PERFORMANCE_2026-10-08.md`, `FUTURES_GRID_PLAN_2026-10-08.md`); volume-bar research used 09-08…09-21 for tuning and 09-18/09-21 as held-out days (`VOLUME_BAR_FINDINGS.md`). Neither those documents nor git history gives a complete per-experiment ledger of which dates were inspected for *strategy* questions.
- **Consequence (contract):** no session is "untouched" for the purposes of rule discovery. The five non-discovery adaptive dates are only *less contaminated*, and shall be labelled "examined for diagnostics, not used for adaptive rule discovery" in every report. True out-of-sample evidence can only come from sessions recorded after the rule freeze (forward validation).

### 11.3 Chronological splits (proposal; D-6)
Discovery = 09-04…09-25 minus any exclusion for data quality (13 adaptive-discovery dates + 09-04 if usable); Validation = 09-28…10-01 (4 sessions: *within-sample for the project but not adaptive-discovery*; labelled as "validation-1, contaminated by diagnostics"); **Forward** = every session recorded after the rules are frozen (sessions 10-05+ on the VM; their availability to the researcher is U). Rules: no look-ahead; rolling/expanding normalisation only from prior sessions; splits are chronological only; one hypothesis per experiment; expiry days analysed as a stratum (4 of 18, DTE=0); hold-out consumption is permanent (`BACKTEST_RULES.md` rule 15).

### 11.4 Research constraints
- Standalone signal families with explicit gates; **no composite score**; no forced daily trade count (the 5-trade cap is a ceiling). Every threshold dynamic/DTE-based or labelled as an approved structural constant.
- Rolling ATM baskets are diagnostics only (§6.2).
- Every tested variant is registered *before* running (id, hypothesis, parameters, split, date) in an append-only `research_variants` ledger (Phase 4 table/CSV, with user approval for new datasets per CLAUDE.md) and reported including negatives, each result a single data point in an accumulating series.
- Results are produced by the shared engine only (replay path), with costs and executable-quote fills; no point-P&L-only or mark-fill results count as evidence.
- A rule that passes discovery+validation is only a candidate; it requires forward paper validation with replay reconciliation.

## 12. Acceptance tests (Phase 2 gate)

Use a **plumbing strategy** `PlumbingEveryNthBarStrategy` (enter on every k-th completed bar alternating CE/PE, exit after m bars) — deterministic, no edge claim, documented as such in code and test names.

| ID | Test | Pass criterion |
|---|---|---|
| T1 | **Replay/live-path equivalence.** One recorded session fixture (real ticks, ≥1 full day slice and a synthetic edge-case file) fed through Historical adapter and through LivePaper adapter (fake poll source delivering the same ticks in arbitrary batch sizes with injected `FakeClock`) | Byte/field-equal features, bars, decisions (incl. rejects), order intents, order events, fills, positions, risk-counter snapshots, P&L; diff tool reports 0 differences |
| T2 | Batch-boundary independence: same events split into random poll batches (property test, seeded) | Output equal to T1 for every split |
| T3 | Duplicate ticks / duplicate event `Seq` | No duplicate decisions/orders/fills; counters unchanged |
| T4 | Equal timestamps (large availability group, mixed instruments) | Processed as one group in contract order; bar emitted after group quotes |
| T5 | Out-of-order / late tick (older than watermark) | `LateEventFault`, entries disabled, exits managed; recovery replay reproduces; no silent reorder |
| T6 | Late quote (arrives after `ExecutionTime` for an order) | Not used for that fill; order stays `Working`/expires |
| T7 | Stale quote (age > limit) at execution; no two-sided quote | No fill, no LTP substitution (assert LTP never read), order expires with reason |
| T8 | Partial fills (displayed qty < requested) | Per-quote partials, remainder cancelled at expiry, reservation reduced to filled amount, position qty = filled |
| T9 | Rejection (spread/size/min-bid) | Order `Rejected`, reservation released, trade counter not consumed, audit row with exact inputs |
| T10 | Exit rejection / no bid | Position `ExitFailed`, retries at `ExitRetryMs`, never flat without fill, alert fault |
| T11 | Disconnect: events stop for the held token | Entries off; compulsory-close timer fires at deadline in a quiet market (no events) using heartbeat in live, `Flush` in replay; outputs equal |
| T12 | Session close | Entry cutoff cancels pending entries; close-start forces exit; `SessionEnd` with unfilled position records `UnliquidatedAtClose`, never fabricates a fill |
| T13 | Crash/restart with **pending entry order** | Recovery replays to identical pending order; no second order; persisted order events verified; ids unchanged |
| T14 | Crash/restart with **open position** (and with `ExitPending`) | State identical to uninterrupted run; exit still occurs once; no duplicate fills |
| T15 | Restart at many arbitrary tick/bar boundaries (adaptive "restart-parity" style) | Final persisted tables equal the uninterrupted run field-for-field |
| T16 | Risk: 5-trade cap, one position, duplicate-entry attempt, pending reservation counts, cutoff, daily-loss incl. unrealised liquidation and costs, per-trade loss, kill switch | Each gate rejects with correct reason; ordering of reasons deterministic |
| T17 | Determinism guard: banned APIs absent from Core; same run twice in-process and in separate processes → identical outputs; culture/timezone changed → identical | Pass |
| T18 | Config: hash stable; mid-session change rejected; restart with different hash refuses to trade | Pass |
| T19 | Contract fixation: exit for a different token rejected; selection uses only quotes ≤ DecisionTime (inject a better future quote → must not be chosen) | Pass |
| T20 | DB: unique keys and partial unique "one open position" index reject duplicates; transaction atomicity (kill between rows) | Pass |
| T21 | Regression oracle: engine adaptive feature outputs unchanged vs. current master on the existing frozen fixtures | Existing adaptive tests (1324 total pass at baseline) stay green |

## 13. Risks

1. **Hidden wall-clock coupling** in any reused class (A: only `Adaptive*` workers/services use `UtcNow`; pure calculators none). Mitigation: Core wrapper tests T17; adapter-only clock.
2. **Late/reordered live rows** triggering the degrade-and-rebuild loop (G9). Mitigation: fault policy §6.5; Phase 3 measure frequency from `ReceivedAt`/`Id` anomalies; do not auto-retry forever.
3. **Quote coverage** for executable fills is untested per contract (11.1 note) – the fill model may reject a large share of intended trades; that is information, not a bug.
4. **Overfitting / contamination** (§11.2).
5. **Memory** of full-session replay (≈1.9 GB WS) when the trading engine is added on top in the same process.
6. **Tick loss on the live path** (G8) breaks historical-vs-live equality for reasons outside the engine; reconciliation must separate the two (§9.4).
7. **Scope creep** into Host/Dashboard before edge is found; Phase 2 adds no registration in `Host/Program.cs`.
8. **Audit-pass claims** marked A may contain errors; Phase 2 starts by re-verifying the A-items it depends on.
9. **Costs schedule** uncertainty can flip conclusions for low-premium weekly options (₹100–150 premium, brokerage ₹40 round trip + taxes ≈ material fraction of a 1-lot P&L).

## 14. Decisions requiring Naveen's input (with recommendations)

| ID | Decision | Recommendation |
|---|---|---|
| D-1 | Approve architecture: new `Trading.Core` pure library + adapters, wrapping (not modifying) `AdaptiveObserverEngine`; legacy engines retired from live path | Approve |
| D-2 | Paper capital, lots per trade, max premium exposure | Start 1 lot, ₹50k capital as paper-only; confirm or supply real values |
| D-3 | Daily-loss definition: realised + unrealised liquidation value (executable bid, after estimated exit costs) + costs; breach also force-exits | Approve (stricter than today) |
| D-4 | Contract-selection policy values (premium window, delta preference, spread/OI minima): dynamic/DTE-based vs. approved fixed structural constants | Derive from data in Phase 3; use no premium band hard-coding; for Phase 2 plumbing use a clearly labelled placeholder |
| D-5 | Partial-fill policy: keep filled part & cancel remainder (default) vs. all-or-none | Keep-and-cancel |
| D-6 | Research split labels (§11.3) and whether post-10-01 VM sessions will be made available as forward data | Approve; provide 10-05+ session data access/export when ready |
| D-7 | Cost schedule: supply or authorise verification of current STT/exchange/SEBI/stamp/GST rates and broker brokerage | Required before Phase 3; research STT figure in code not adopted |
| D-8 | Latency/quote-age/spread/expiry placeholders (§10.6) – calibrate in Phase 3 from recorded data | Approve calibration plan |
| D-9 | New database `niftysignal_trading` (schema §8) – CLAUDE.md requires approval for new datasets/tables | Approve creation (local dev DB only in Phase 2; no VM migration) |
| D-10 | Entry cutoff / compulsory close times and weekly-expiry-day treatment | Keep 15:00/14:00 and 15:15 as placeholders until research says otherwise |
| D-11 | Kill switch: engine-owned with Dashboard write, auto-trip on daily-loss/Degraded | Approve; Dashboard change deferred until an edge exists (project rule) |

## 15. Phase 2 bounded checklist (implementation, still no edge claims, no Host registration, no deploy)

1. Re-verify the A-items this plan depends on (list in PR description).
2. Create `NiftySignal.Trading.Core` (+ tests): events, `Sequencer`, `DeadlineQueue`, `ITradingClock`, `TradingEngine.Handle`, ids (deterministic hashes), `TradingConfig` + hash.
3. `SimulatedExecutionModel` + `CostModel` + order/position state machines + `RiskEngine` + `ContractSelector` (mechanism only; plumbing selection policy labelled placeholder) + `DecisionAudit`.
4. `NiftySignal.Trading.Signals` with `PlumbingEveryNthBarStrategy` only.
5. `AdaptiveEventTranslator` and the Historical adapter; LivePaper adapter library (class library, **not** hosted) with a fake poll source for tests.
6. `NiftySignal.Trading.Data` EF model + migration for `niftysignal_trading` (local DB; approval D-9), replay-and-verify persistence, partial unique index.
7. Acceptance tests T1–T21; a fixture builder from the recorded `niftysignal_vm_copy` ticks (read-only extraction of a bounded slice into test resources or a read-only DB test; no new dataset beyond approved fixtures).
8. Differ tool (`TradingRunDiff`) comparing two runs table-by-table (used by T1/T15).
9. `dotnet build` 0 warnings (also fix the 3 baseline warnings only if touching those files; otherwise record) and `dotnet test` green; adaptive tests unchanged.
10. Docs: update `ARCHITECTURE.md` module map; contract changes recorded here; `PENDING/DEFERRED (audit finding FNN)` comments for any newly found defect.
Out of Phase 2: broker adapter, Host registration/deploy, Dashboard changes, strategy sweeps, calibration of placeholders, any signal-family evaluation.

## 16. Checks run (baseline, worktree `D:\Projects\NSE-phase1`, Windows, .NET SDK 10.0.401)

| Command | Result |
|---|---|
| `git fetch origin` | ok; `origin/master` = `1ef645be330d1d864ba50b6eb2dc614c744f9674`, local `master` identical (0 ahead/0 behind), working tree clean, no stashes touched |
| `git worktree add -b trading-engine-phase1-contract ../NSE-phase1 origin/master` | ok; HEAD `1ef645b` (a pre-existing detached worktree `D:/Projects/NSE-OOS-Export` @ `29552c1` was left untouched) |
| `dotnet build NiftySignal.slnx -c Release -nologo -v q` | **succeeded, 0 errors, 3 warnings** (`tools/AdaptiveLivePerformance/Program.cs(205,8)` CS0219; `OptionsSupplementalTests.cs(23,9)` and `AdaptiveCommentaryPersistenceTests.cs(100,9)` xUnit2000); 3 min 39 s. Warnings pre-exist at baseline and violate the "0 warnings" working rule — recorded, not fixed (Phase 1 is docs-only) |
| `dotnet test NiftySignal.Tests -c Release --no-build -nologo` | **Passed 1324 / Failed 0 / Skipped 0**, 6 s |
| Read-only SQL (psql 18, `default_transaction_read_only=on`) on local `niftysignal_vm_copy`: `pg_database`, `information_schema.columns/pg_indexes` for `ticks`, per-date `ticks` aggregates, `instruments` per `AsOfDate` | results in §1.2, §11.1 (per-date aggregate query took ≈5 min) |
| Not run (by instruction): relational/Chromium/CI gates, VM checks, any replay/sweep | — |
