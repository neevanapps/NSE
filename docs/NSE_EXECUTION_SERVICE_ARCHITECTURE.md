# NiftySignalExecution: architecture and self-review

Decisions: `NSE_A1_A8_DECISIONS_AND_CONTINUATION.md`. Paper only. No broker, order or FlatTrade type is referenced by the new service.

## 1. Processes and data flow

```
NiftySignalHost (one FlatTrade session)
  tick ingestion ----------> ticks (trading db) ------------------------+
                                  |                                     |
                                  v                                     v
NiftySignalObserver          bars, sidecars, checkpoints        NiftySignalExecution
  Adaptive Observer -------> observer db                          tick tail (Id cursor, bounded)
    bar publication -------> adaptive_bar_publications ---------> frame (existing DbFrameInputSource)
    runtime row -----------> adaptive_observer_runtime ---------> health gate (status, heartbeat)
                                                                  StrategyRuntime x4
                                                                   | one fenced transaction per state change
                                                                   v
NiftySignalDashboard <---- read only ----- pev2: execution_journal, events, alert_outbox, live_marks, execution_state
```

- **Handoff.** The Observer already persists every bar and its sidecars. It adds one row per bar, `adaptive_bar_publications (SessionId, BarSeq, Verified, UnverifiedReason, PublishedAtUtc)`, insert-if-missing and immutable, after the sidecar verdict is known. Recovered bars are published after recovery. It writes only to its own database, never waits for the execution service, and a failed publication is contained and retried; a bar that is never published is a visible sequence gap (entries blocked).
- **Quotes.** The service tails the raw tick table by Id for the session's option tokens and India VIX (one writer, the Host ingestion worker, which flushes about once a second). On start it reads the latest quote per watched token once. No whole-day reads.
- **Bars.** Published bars are read in sequence from the last journaled one. Frames are built by the existing frame builder from persisted sidecars, without quotes: execution never prices from a bar-end quote.
- **Health.** Observer runtime row, polled every cycle.

## 2. Time, ordering and causality

- One loop, one thread of decisions. Each cycle: health, new ticks, new publications (in bar order), then the timer.
- The **decision instant** of a signal is when the service processes it (its own clock), not the bar's end. A fill needs a quote whose `AvailableAt` (Host receive time) is strictly later than the decision (plus configured latency) and no older than `maxQuoteAgeMs` at the processing instant. A delayed signal therefore can never fill from an earlier quote. Host and service run on one machine, so one clock.
- A frame processed more than `MaxProcessingLagSeconds` after its bar ended blocks entries (existing rule). Backlog bars after a restart count toward warm-up and drive exits, but never enter.
- The Observer holds 2 s for causal ordering before it processes ticks, so normal signal lag is a little over 2 s against a 5 s limit.

## 3. Engine input, journal and recovery

The strategy runtime takes one input type: instant, current quotes of the watched contracts, VIX reading, contract averages, health gate, and optionally one frame with its integrity disposition. **Decisions use nothing but the input and the runtime's own state**, so an input that changes no state need not be stored.

For every input that changed state or produced events, one database transaction writes: the owner fence check, the journal row, the ledger events and alert outbox rows. In-memory state is never acted on after a failed commit: the engine is discarded and rebuilt from the journal.

Restart: take ownership, replay today's journal into a fresh engine, compare regenerated events with the stored ledger. A difference or a gap marks the state **unreconciled**: entries blocked, alert raised, exits and protection continue. Then resume from the cursors.

## 4. Single writer

1. A PostgreSQL session advisory lock held on a dedicated connection for the life of the service. A second instance (or the legacy in-Host runtime, which now takes the same lock) refuses to start writing.
2. A fencing epoch in `pev2_execution_state`, incremented on every takeover and checked `FOR UPDATE` inside every write transaction. A writer that lost the lock without noticing cannot commit.

Transfer from the in-Host engine: set `PaperEngineV2:Enabled=false` for the Host and enable the service. Binaries older than this branch do not know the lock: **a rollback must stop the execution service first** (enforced by the deployment script).

## 5. Timers and rules

| IST (configurable) | Action |
|---|---|
| 09:20 | Earliest entry, also needs 20 verified completed bars |
| 15:00 | Entry cutoff: no new entries, pending intents and orders cancelled. Positions stay open; partial booking and opposite-signal exits continue |
| 15:08 (starting value) | Mandatory liquidation requested for every open position |
| 15:10 | Deadline: anything still open raises a liquidation-failure event and alert; exits keep retrying |
| 15:25 | Retry cutoff: still open is reported `Unresolved`, never as closed |

The timer runs every cycle, with or without bars or quotes.

**Entry gate (all required, rechecked at fill):** Observer `Live` and heartbeat no older than 30 s; recovery complete; execution state reconciled; integrity disposition of the latest frame (including the 3 clean frames); time window; warm-up; daily loss limit; trade count; one position; fresh quote; premium 100-150; spread; capital. **Exits** need only a fresh executable quote newer than the exit decision.

**Protective flatten.** Heartbeat older than 30 s plus grace (0 s), or Observer status `RecoveryFailed`, `Degraded`, missing session after entries exist, or `Closed` before 15:30: cancel pending intents (consumed) and exit every open position. A `Rebuilding` Observer with a fresh heartbeat blocks entries only; `Protection:FlattenWhenNotLiveAfterSeconds` can add a limit (0 = off).

**Opposite signal.** Exit the whole remaining position and create the opposite intent from the same signal. Immediate variants: once the exit is confirmed, select the contract and require a still newer quote. Avg5 variants: an independent pending intent.

**Avg5 intent.** Freezes the reference contract; waits without a time limit until live mid < that contract's latest completed-bar Avg5; then places an ordinary order (60 s lifetime) after all gates pass, rechecking the condition at fill. Avg5 values are derived by this service from the published bars.

**Temporary versus terminal blocks (continuation Q2).**

| Condition | Order not yet filled | Avg5 intent | Immediate entry or reversal signal |
|---|---|---|---|
| Mid not below Avg5, Avg5 or reference quote unavailable or stale, no Rule 8 contract, spread over the limit, entry signal no longer true | none placed, or fails | kept | consumed (never waits without limit) |
| Stale market data, bar problem (gap, unverified, lag, clean-frame run), single failed Observer read with a fresh heartbeat | cancelled; nothing fills | kept | consumed |
| Opposite signal; Observer session missing, not `Live` or heartbeat stale; execution state unreconciled; daily trade or loss limit; entry cutoff or session end; protective flatten | cancelled | cancelled | cancelled |

**Failed or expired order (Q3).** With nothing filled, an Avg5 order returns to waiting only after it is terminal, and retries only on quotes later than that order's (reference and contract both). Fills are all-or-none in paper, so a partially filled entry order cannot arise.

**Delayed immediate entry (Q1).** The contract is selected from the latest verified frame's basket and priced on a fresh live quote; premium, spread, freshness, capital and all gates are applied again at the fill, within the order lifetime.

**Partial exit.** Policy frozen at the entry fill (calendar DTE, VIX, target, quantity rounded down to lots, at least one lot remaining). Triggered by a bid at or above target; filled on a later quote; full exits take precedence and cancel it.

## 6. Failure behaviour

| Failure | Entries | Open positions | Alert source |
|---|---|---|---|
| Observer stalled, failed or dead (Host alive) | Blocked | Protective flatten on fresh quotes | Service and Dashboard |
| Observer rebuilding, heartbeating | Blocked | Held; partial target and timer exits work; no reversal exits | Dashboard |
| Host dead | Blocked (stale heartbeat, no ticks) | Flatten requested, **cannot fill: no quotes**. Stays pending, alerted | Service and Dashboard |
| Execution service dead | None | **Unprotected** until restart; journal restores state; liquidation runs on restart if still before the retry cutoff | Dashboard (stale execution heartbeat) |
| PostgreSQL down | Blocked | Nothing can be read or recorded; no paper fill is invented. Resumes from journal | Service sends Telegram directly |
| VM, disk or network down | None | **Unprotected.** Nothing on this VM can help; broker-held protection or a second failure domain would be needed. Not investigated | None from this VM |

## 6a. As built (`NiftySignal.ExecutionService`)

| Piece | File | Notes |
|---|---|---|
| Cycle, ownership, journal, replay, reconciliation | `ExecutionCoordinator.cs` | Every input carrying a bar is journaled, plus any input that changed state |
| Entry block / protective flatten | `ProtectionPolicy.cs` | Pure; table-tested |
| Reads of other processes' data | `DbExecutionFeed.cs` | Tick tail by identity; start-up look-back 60 s |
| Direct failure alerts | `ServiceAlerts.cs` | Deduplicated, 15 min reminder, recovery message; independent of the ledger outbox opt-ins |
| Startup checks | `ExecutionWorker.cs` | Disabled by default; an invalid setting or any rejected strategy file stops the service; logs every loaded strategy and its hash |

Points that differ from or sharpen the design above:

- A restart while the Observer database is unreadable cannot rebuild the day (the frozen lot sizes come from it): heartbeat `Recovering`, alert, **no entries or exits** until it is readable.
- A ledger holding same-day events written by the in-Host engine (a mid-day ownership transfer) reconciles as different: entries stay blocked for that day. Transfer ownership outside market hours.
- A published bar that cannot be assembled is retried 20 cycles, then skipped as a gap.
- Positions left open or `Unresolved` by an earlier trade date are alerted at start-up from the last heartbeat snapshot. They are not carried into today's engine: the owner resolves them.
- The service's Telegram alerts need `Telegram:BotToken` / `ChatId` in its own `appsettings.Local.json` (owner-supplied).
- Tested with a scripted feed on a local scratch PostgreSQL. The database feed's tick tail was tested on the EF in-memory provider only; frame assembly, averages and end-to-end latency are **not yet verified on real data**.

## 6b. Observer process, recovery and checkpoints

- **Isolation.** `NiftySignalObserver` (library `NiftySignal.ObserverRuntime`) is the default; `Adaptive:Observer:Process=InHost` keeps it in the Host and is required for the in-Host paper engine. One writer by a PostgreSQL advisory lock on the observer database; losing the lock's connection discards live state. The Observer only reads the tick database, so its failure or restart does not touch ingestion; it opens no broker session. A restart continues from the newest checkpoint.
- **Checkpoint.** One row in `adaptive_checkpoints`: gzip of the complete processing state at a batch boundary (engine, volume bar in progress, rolling and option accumulators, Level-1 and Top-5 baselines, duplicate memories, unprocessed ticks, per-stream Id positions). No order or position state. Saved at most every 60 s of wall time (also for empty pre-market slices), during replay and live processing, never while a bar's sidecars or publication are unsettled; three kept per session.
- **Accepted only if** the identity (session, frozen configuration, format, build module versions), payload hash, deserialization, persisted bars and persisted market-row counts all check. Otherwise the next older one, otherwise a full replay. A checkpoint from another build is never used, so the first restart after a deployment is a full replay.
- **Failure after a restore** (replay or the closing verification): that checkpoint and newer ones are deleted, the attempt fails (`RecoveryFailed`), the next uses an older one. A cancelled or timed-out attempt keeps its checkpoints and the next continues from them.
- **Full replay failure.** Runtime `RecoveryFailed`; retry after 30 s for a timeout, 1 s otherwise, one attempt at a time; never published as `Live`; persisted history is never overwritten. A persisted-versus-source mismatch repeats until someone investigates: entries stay blocked, alerts repeat, protective exits keep working.
- **Time limit.** 300 s per live attempt (ended-session repair has none). With checkpoints, successive attempts make progress; with `CheckpointsEnabled=false`, or if no checkpoint can be saved, a day that cannot replay in 300 s never becomes `Live`. Recovery time on real data is unmeasured.
- **Limits.** If a replay after a restore saves new checkpoints and then fails, older ones may already be evicted by retention; the next attempt then replays the whole day. Checkpoint rows of past sessions are never deleted (about 1.1 MiB a day on synthetic data without depth).

## 6c. Memory retention that remains (F74, F75)

Reads are bounded and processed batches released. Still retained: live pending ticks (at most one read window plus one batch); the recovery stream (about two read windows); the options observation cache from its floor (a side token that never quotes keeps the floor low); the engine's completed bars for the day. Historical bootstrap holds one futures instrument-day; Strong-threshold queries load prior sessions' ratios; verifying completed observations reads the source once more. A source disordered by more than one read window fails the stream; nothing is dropped.

The in-Host paper runtime checks the same writer lock before each completed bar and closes if another writer has taken it.

## 7. Self-review

| Requirement | Met by | Residual risk |
|---|---|---|
| Single authoritative writer | Lock plus fence (section 4) | Old binaries ignore the lock; procedural control on rollback |
| No synchronous Observer blocking | One own-database insert per bar | That insert can be slow if the observer database is slow (as every bar write already is) |
| No backdated fills | Decision instant is processing time | Clock step on the VM would distort quote ages |
| Timers independent of data | Evaluated every cycle | A hung database call delays the cycle: all commands carry a timeout |
| Deterministic replay | Inputs-only decisions, journal of state-changing inputs | Determinism depends on every state mutation marking the input; covered by a replay test, not provable |
| Bounded work | Tick batches, per-bar reads | Frame assembly reads the session's bars up to the current one (existing behaviour, grows over the day) |
| Quote latency | About 1 s ingestion flush against a 2 s maximum age | Under load, quotes may be judged stale and block fills. **Measure on real data** |
| Reuse | Same engine, config, cost model, ledger, alerts, marks | The engine's decision instant changes from bar end to processing time for the service |
| Paper multi-fill handling | All-or-none paper fills | Real partial fills are not simulated; a live broker adapter would need order-level accumulation |

No unresolved correctness or safety issue requires a new owner decision before implementation. Items stated as limitations above remain open for release.
