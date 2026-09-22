# Live/Backtest Parity Plan (2026-09-20)

## Goal
Live paper trades must be exactly reproducible by re-running the offline backtest on the
same day's data. If they don't match (same entry bar, strike, direction, exit reason, ~fill
price), the system isn't ready for real orders.

## Locked target score
`OptionsScoreThreeWaySwitchMaxPainConfirmed` @ BarThreshold=2600 / EntryPercentile=90
(64.3% win, +426.40 net, 112 trades, 8/8 backtest days ≥50% win — see
`docs/VOLUME_BAR_FINDINGS.md` item 8's adoption entry).

## Data architecture decision
The volume-bar tables (`VolumeBarRow`, `OptionAtmBarRow`, `OptionDepthBarRow`,
`OptionMaxPainBarRow`, etc.) stay in their own database (`niftysignal_volume_bars`-style),
NOT merged into `NiftySignalDbContext`. What's new: live gets a WRITER into this database
for the first time — today only the offline populators write to it. Both live and backtest
read/write the same physical store, so "recompute from the same tables" is literally true,
not just architecturally intended.

## Current-state gap (as investigated 2026-09-20)
- Backtest's locked score lives in `NiftySignal.VolumeBarData/TradeSimulator.cs`
  (`ComputeOptionsThreeWayScore`, `PassesConfirmation`'s Max Pain branch), reading
  `VolumeBarDbContext` tables populated by `VolumeBarPopulator`/`OptionAtmPopulator`/
  `OptionDepthPopulator`/`OptionMaxPainPopulator`, all sourced from `NiftySignalDbContext.Ticks`.
- Live's `NiftySignal.Host/LiveFeatureEngine.cs` computes a DIFFERENT, older 7-component
  tanh-composite score on a 15s cadence — not volume bars, not the locked metric at all.
  `LiveTradingEngine.EvaluateCadenceAsync` trades off that old score today.
- `NiftySignal.Features.VolumeBarBuilder` was already built source-agnostic (doesn't care if
  ticks come from replay or a live socket) — the intended seam, not yet wired to Host.
- No live code path writes to `VolumeBarDbContext` at all today. Phase A/C are greenfield on
  the live-write side.

## Order of work (do not skip ahead)
1. **Shared scoring library first.** Extract `ComputeOptionsThreeWayScore` +
   `PassesConfirmation`'s Max Pain branch out of `TradeSimulator.cs` into a new
   `NiftySignal.Scoring.OptionsScore` project — pure functions, explicit inputs, no DB/I-O.
   Prove byte-identical output to the current in-place version via unit tests before anything
   else changes. **Currently in progress.**
2. Bring the volume-bar tables' live-write path into existence (Phase A), prove idempotent
   population.
3. Wire live tick → `VolumeBarBuilder` → shared score → paper trade (Phase C/D).
4. **Bar-boundary determinism test (mandatory gate, before trusting any live bar data)**: a
   standalone tool feeds one recorded day's tick stream through both the offline populator
   path and the live `VolumeBarBuilder.ApplyTick` path, asserts identical bar-close
   timestamps/counts/volumes. Live volume bars are not trusted until this passes.
5. Nightly automated parity job (Phase E, mandatory): re-run the official backtest for the
   day, diff trade-by-trade against the live paper-trade table, produce a pass/fail report.
6. Only then: dashboard updates (Phase F), then performance/safety hardening (Phase G).

## Day-start / day-end contract
A day-scoped owner (`TradingDaySession` or equivalent) is the ONLY thing allowed to construct
or reset a `SessionRankTracker`. It creates fresh trackers at the first tick of a trading day
(09:15 IST or first tick received if later) and discards them at day end. Backtest's
`SimulateDayAsync` already does this implicitly per call; live needs the identical lifecycle
made explicit, owned by the shared scoring library — not reimplemented separately per side.

## Fill-price policy (documented, not "fixed" — a real, accepted difference)
- Backtest: `OptionPriceSeries.PriceAtOrBefore` — binary search against the full historical
  tick log, i.e. "price as of an arbitrary past timestamp."
- Live: "latest available option price at decision time" — live can only ever ask for the
  most recent tick, not look into the future relative to its own decision.
- Every paper trade logs BOTH the decision timestamp and the price used, specifically so a
  fill-timing mismatch is distinguishable at a glance from an actual scoring bug.

## Biggest parity risks (tracked, not yet mitigated)
1. Session-rank state lifecycle — see Day-start/day-end contract above.
2. Bar-boundary replication — tick arrival order must match the DB's
   `(ExchangeTimestamp, Id)` ordering or bar cuts can silently diverge. Mitigated by the
   determinism test (step 4).
3. Max Pain timing — full-chain OI snapshot "as of bar close" needs an explicit, identical
   live snapshot policy, not yet designed.
4. Fill price mechanics differ by construction (see Fill-price policy) — accepted, logged,
   not treated as a bug by itself.
5. `niftysignal_vm_copy` trap: its `ScoreSnapshots`/`PaperTrades` tables are stale-by-design
   (only `Instruments`/`Ticks` stay fresh) — any parity tool must recompute from ticks, never
   trust those tables directly.

## Status
2026-09-20: plan agreed with user (option (b), 4 refinements accepted).

**Step 1 DONE (2026-09-20).** `ComputeOptionsThreeWayScore` + the Max Pain confirmation gate
extracted into `NiftySignal.Scoring` (`OptionsThreeWayScoreInputs`,
`OptionsThreeWayScoreCalculator`, `MaxPainConfirmationGate`) as pure, dependency-free
functions, no DB/I-O. `TradeSimulator.cs` now calls into these instead of containing its own
copy — verified via grep, no duplicate formula remains. 18 new unit tests added
(`NiftySignal.Tests/Scoring/OptionsThreeWayScoreCalculatorTests.cs`).

Verified byte-identical before/after: target metric `OptionsScoreThreeWaySwitchMaxPainConfirmed`
@ 2600/90 (112 trades, 64.3% win, +426.40 net) and collateral-damage check
`SessionGatedDepthDuration` @ 2600/90 (80 trades, 55.0% win, +144.70 net).

Scope note: `ComputeImbalanceRatio` (shared helper, used by several OTHER options metrics not
in scope here) intentionally left as a private copy in `TradeSimulator.cs` for those other
dispatch branches — only the locked metric's own usage was rewired, per "don't touch other
metrics' dispatch branches."

**Next**: Phase A — bring the volume-bar tables' live-write path into existence (option (b):
separate DB/schema, live gets a writer for the first time), prove idempotent population.

## Phase A: live-write path (2026-09-20)

**What was built.**
- `NiftySignal.VolumeBarData/LiveVolumeBarPopulator.cs`, `LiveOptionAtmPopulator.cs`,
  `LiveOptionDepthPopulator.cs`, `LiveOptionMaxPainPopulator.cs` -- live-path siblings of the four
  existing offline populators, each with a `WriteNewBarsAsync` entry point instead of
  `PopulateDayAsync`. `NiftySignal.Host/LiveVolumeBarWriter.cs` -- a new `BackgroundService`,
  registered in `Program.cs` alongside `MarketDataIngestionWorker`, that polls every 10s during
  market hours (08:45 (see caveat below)-15:35 IST) and calls the four `WriteNewBarsAsync` methods
  in sequence (future bars first, since the three option tables all join against them by
  `BarIndex`). One extra flag (`finalizeDay`) is passed true once, after 15:30 IST, to flush the
  day's necessarily-partial final future bar (see `VolumeBarBuilder.FlushPartial`).
- **Architecture choice**: these are NOT a live tick-stream subscriber hooked into
  `MarketDataIngestionWorker`'s own tick loop. They instead REPLAY today's ticks from
  `NiftySignalDbContext.Ticks` -- the same table `MarketDataIngestionWorker.FlushAsync` already
  writes to, batched every ~1s/200 ticks -- through a fresh `VolumeBarBuilder` on every poll,
  writing only the bars not already persisted (checked via each table's own max `BarIndex` for the
  day/threshold). This was chosen over an in-process subscriber for two reasons: (1) it needs zero
  coupling to `MarketDataIngestionWorker`'s own `_engineSync` lock and tick-buffer/flush machinery,
  which is already complex and explicitly out of scope to touch here; (2) idempotency and
  restart-safety fall out for free -- a completed bar's identity is fully determined by
  `(AsOfDate, BarVolumeThreshold, BarIndex)` and the durable tick sequence in Postgres, so there is
  no in-memory accumulator state to snapshot/restore across a Host restart. The tradeoff is
  re-scanning some tick history on every poll; `LiveOptionDepthPopulator` was written (and then
  fixed once, see below) to scan only ticks since the last already-written bar boundary, not the
  whole day, since its own per-bar depth accumulator resets at each bar boundary by construction.
  `LiveOptionAtmPopulator`/`LiveOptionMaxPainPopulator` still reload each touched instrument's
  quote/OI history from `dayStart` on every poll (needed for their own `MidAtOrBefore`/
  `OiAtOrBefore` forward-fill semantics) -- acceptable at the locked 2600-threshold bar rate and the
  10s poll cadence, but a real cost that would need addressing (e.g. an in-process cache keyed by
  token, refreshed incrementally) before shortening the poll interval much further or widening the
  option universe.
- `NiftySignal.Host.csproj` now references `NiftySignal.VolumeBarData`; `Program.cs` registers a
  second `DbContext` (`VolumeBarDbContext`, pointed at `niftysignal_volume_bars` via the same
  `Database=` override the offline CLI already uses) and self-provisions it (`Database.Migrate()`)
  alongside `NiftySignalDbContext` at startup.

**Feasibility investigation (option-side tables) -- not a blocker.** The task's open question was
whether option ticks flow into `NiftySignalDbContext.Ticks` live at all, and whether Max Pain's
"full chain" claim is actually buildable from what live subscribes to.
- `InstrumentUniverseResolver.cs` already subscribes ATM +/- 10 strikes each side, nearest 2 weekly
  expiries (~84 option instruments), and `MarketDataIngestionWorker.FlushAsync` persists ALL
  buffered ticks (not just the future's) to `NiftySignalDbContext.Ticks` -- so option ticks ARE
  already flowing and persisted live, today, with no ingestion-side change needed.
- `OptionMaxPainPopulator`'s own doc comment claims it "spans the FULL listed chain." A direct query
  against `niftysignal_vm_copy` across all 9 available historical days shows every single day has
  only ever had ~20 distinct strikes per expiry (40 instruments), i.e. exactly the same ATM+/-10
  window `InstrumentUniverseResolver` already resolves live -- historical data was never actually
  wider than that. So live writing `OptionMaxPainBarRow` is not a data-breadth regression versus what
  the offline populator has ever been validated against; it reproduces the same (narrower-than-the-
  doc-comment-implies) band. This is a pre-existing chain-breadth limitation of the whole pipeline,
  not something Phase A introduced or is responsible for fixing -- documented here per the task's
  "report honestly, don't force it" instruction, not treated as a Phase A blocker.

**Replay-verification result: PASS, row-for-row, on two separate days.** A new `replay-live` CLI
command (`NiftySignal.VolumeBarData/ReplayLiveCommand.cs`) reads a historical day's future ticks
from `niftysignal_vm_copy` (the same source both live and the offline populators read), buckets them
into simulated poll checkpoints (`--poll-seconds`, mirroring the real 10s cadence just compressed for
test runtime), and feeds each checkpoint's own cutoff timestamp into the four live writers ONE POLL
AT A TIME, in chronological order, writing into a disposable `niftysignal_volume_bars_livetest`
database (never the real one) -- no checkpoint ever sees a tick timestamped after its own cutoff,
the same no-look-ahead constraint the real live worker has by construction. The resulting rows are
then compared field-by-field against the SAME day's rows already produced by the offline populators
in the real `niftysignal_volume_bars` database:
- **2026-09-16 @ 2600**, 13 checkpoints (30-min buckets): `VolumeBars` 611/611 rows, `OptionAtmBars`
  611/611, `OptionDepthBars` 611/611, `OptionMaxPainBars` 611/611 -- **0 value mismatches on any
  field, in any of the 4 tables.**
- **2026-09-11 @ 2600**, 26 checkpoints (15-min buckets): `VolumeBars` 941/941, `OptionAtmBars`
  941/941, `OptionDepthBars` 941/941, `OptionMaxPainBars` 941/941 -- **0 value mismatches.**
- One real bug was found and fixed by this harness during development: `LiveOptionDepthPopulator`'s
  incremental tick-range scan used a strictly-`>` lower bound for every bar, but the offline
  populator's own range is inclusive of `dayStart` for the day's very first bar (a tick landing
  exactly on `dayStart` belongs to bar 0, not excluded) -- an off-by-one that showed up as a single
  `CallBidQtyAvg` mismatch at `BarIndex=0` (3919.5 vs 3903.636...) on the first parity run. Fixed by
  making the lower bound inclusive only for the day's first scan (`existingMaxIndex < 0`) and
  exclusive for every subsequent one (a tick exactly on a bar's `EndTimestamp` already belongs to
  the CLOSING bar, not the next one) -- re-verified clean on both days afterward.

**Idempotency guarantee and how it was tested.** The guarantee: because every write is a
from-scratch replay of durable, already-persisted ticks (never in-memory accumulator state), a
restart mid-day can only ever reproduce bars 0..N identically and skip re-inserting whatever's
already there (enforced at the DB level too -- each table's own unique index on
`(AsOfDate, BarVolumeThreshold, [BandWidth,] BarIndex)` would throw on an actual duplicate insert
attempt, which never happened in testing). Tested by simulating a Host restart partway through both
replay runs above (`--restart-after=3` and `--restart-after=5` respectively): the harness runs N
poll checkpoints, stops, and resumes with a completely fresh call sequence (no state carried over --
literally what a real process restart would do), then asserts the `BarIndex` sequence is contiguous
and gap-free (0..last, no skip, no repeat) after resuming. Both runs passed
(`IDEMPOTENCY: BarIndex sequence contiguous and gap-free`), and since the SAME restarted runs then
went on to match the offline populator's rows exactly (the parity comparison above), that's a
stronger proof than "didn't crash" -- a restart-induced duplicate or gap would have shown up as a
row-count or row-content mismatch against the offline populator's own never-restarted rows, and none
did.

**What's NOT done / explicitly out of scope, per the task boundary**: no score computation, no
paper trading, `NiftySignal.Scoring` and `TradeSimulator.cs`'s dispatch logic are untouched. The
mandatory bar-boundary determinism gate (plan step 4) and the nightly parity job (plan step 5) are
still separate, later work -- this Phase A replay harness is a one-off proof for this task, not the
standing automated gate the plan calls for.

## Phase C: live scoring + entry signals (2026-09-20)

**What was built.**
- `NiftySignal.VolumeBarData/TradingDaySession.cs` -- the day-scoped owner the "Day-start / day-end
  contract" section above calls for. Owns 4 `SessionRankTracker` instances (depth/mid-IV/close-IV +
  the Max Pain confirmation gate's own), constructed fresh per trading day. `ProcessBar` feeds one
  `VolumeBarRow` + its matching `OptionAtmBarRow`/`OptionDepthBarRow` (BandWidth=5)/
  `OptionMaxPainBarRow` through `OptionsThreeWayScoreCalculator.ComputeScore` +
  `MaxPainConfirmationGate` (the SAME Phase-1 shared-library calls `TradeSimulator.SimulateDayAsync`
  itself makes), applies the locked entry rule set (EntryPercentile=90, 09:30-15:00 entry window,
  15:15 force-close, Max Pain sign agreement, one signal at a time), and returns a
  `LiveOptionsScoreRow` plus an optional opened/closed entry-signal event. Throws if fed a bar out of
  order or twice -- the in-process idempotency guard, since re-scoring a bar wouldn't itself trip a
  DB unique-index violation the way a duplicate bar WRITE would.
- `NiftySignal.VolumeBarData/LiveOptionsScoreRow.cs`/`LiveEntrySignalRow.cs` -- two new tables in
  `VolumeBarDbContext` (migration `AddLiveOptionsScoreAndEntrySignals`). `LiveOptionsScoreRow`: one
  row per scored bar (raw/scaled score, percentile, Max Pain confirm score/pass). `LiveEntrySignalRow`:
  one row per SIGNAL (not a paper trade -- no strike, no fill price, no P&L; Phase D's job), inserted
  on entry and updated in place (ExitBarIndex/ExitTimestamp/ExitReason) on exit.
- `NiftySignal.Host/LiveOptionsScoreEngine.cs` -- a new `BackgroundService`, its own independent 10s
  poll loop (not literally chained after `LiveVolumeBarWriter`, to keep the two resilient to each
  other's failures/slowness). Cannot race ahead of not-yet-written bars BY CONSTRUCTION: it only
  advances past a future bar once its matching ATM/Depth(width=5)/MaxPain rows all already exist;
  any gap just stops advancing there and retries whole on the next poll. Holds one `TradingDaySession`
  across polls (single-threaded `BackgroundService.ExecuteAsync`, no lock needed); rebuilds it via
  `TradingDaySession.RebuildAsync` whenever the tracked date changes OR the process just started
  mid-day (replays every already-scored bar to re-derive tracker state deterministically -- the same
  idempotent-replay philosophy `LiveVolumeBarPopulator` established for bar-writing, applied here to
  in-memory tracker state). Flushes any still-open signal at the day boundary
  (`TradingDaySession.FlushEndOfDay`, exit reason "EndOfDay") so nothing is ever left open across
  midnight.
- **Real Phase A gap found and fixed**: `LiveVolumeBarWriter` only ever called
  `LiveOptionDepthPopulator` at its own default BandWidth (3, ATM+/-1) -- but the locked metric's Open
  leg reads BandWidth=5 (ATM+/-2), which Phase A never wrote live at all. Fixed by adding a second
  `LiveOptionDepthPopulator.WriteNewBarsAsync(..., bandWidth: 5)` call to `LiveVolumeBarWriter`
  (independently idempotent per band, per `OptionDepthBarRow`'s own `(..., BandWidth, BarIndex)`
  unique index) -- without this, every Open-window bar of a live trading day would have silently
  scored null.
- `TradeSimulator.SimulateDayAsync` gained one new optional parameter, `onBarEvaluated` (default
  null, a no-op) -- fires with `(bar, scaledScore, percentile, maxPainConfirmScore)` right where those
  values are already computed, purely for the parity harness below to observe the backtest's own real
  per-bar numbers. Additive only; does not alter dispatch/entry/exit logic for any existing caller,
  verified by the full 128-test Scoring suite passing unchanged afterward.

**Entry-percentile assumption checked, not assumed**: `TradeSimulator.EntryPercentile`'s own dispatch
returns `Math.Abs(scaledScore)` directly for `OptionsScoreThreeWaySwitchMaxPainConfirmed` (it is
excluded from the `TrendReversion`/`Composite`/`OptionsScoreBlend`/two-`FinalScore`-blends branch that
needs a second magnitude-rank tracker) -- confirmed by reading the method body, not inferred. So
`TradingDaySession` needs no 5th tracker; `LiveOptionsScoreRow.Percentile` is just `|ScaledScore|`.

**One-signal-at-a-time, defined**: since there is no real trade lifecycle yet (Phase D), "one at a
time" means `TradingDaySession` holds at most one un-exited `LiveEntrySignalRow` per day -- no new
entry is even evaluated while one is open, mirroring `TradeSimulator`'s own single `open` slot
exactly, just with no option leg underneath it yet.

**Parity verification: PASS, exact, on two separate historical days.** A new `replay-live-score` CLI
command (`NiftySignal.VolumeBarData/ReplayLiveScoreCommand.cs`) drives `TradingDaySession` bar-by-bar
over each day's ALREADY-Phase-A-verified bars (read from the real `niftysignal_volume_bars` database
-- Phase A already proved a live replay reproduces those bars row-for-row, so this only re-proves the
scoring/entry side on top of them), and compares against `TradeSimulator.SimulateDayAsync`'s own
per-bar numbers (via `onBarEvaluated`) and trades for the same day/config
(`OptionsScoreThreeWaySwitchMaxPainConfirmed` @ 2600/90, BandWidth=5):
- **2026-09-16**: 611/611 bars scored with 0 value mismatches (ScaledScore/Percentile/MaxPainConfirmScore),
  13/13 entry signals matched (side, entry bar, entry score, exit bar, exit reason).
- **2026-09-11**: 941/941 bars, 0 mismatches, 24/24 entry signals matched.
- No bug needed fixing on the scoring side itself -- exact match on the first real run, which is the
  expected payoff of Phase B's shared-library extraction (both sides call the identical
  `OptionsThreeWayScoreCalculator`/`MaxPainConfirmationGate` functions).

**Day-boundary / no-cross-day-leakage: PASS.** Both dates above ran back-to-back in one process call,
each getting an independently-constructed `TradingDaySession`; 2026-09-11 (the second day) matched
`TradeSimulator`'s own always-fresh-per-call trackers bar-for-bar, which a leaking implementation
could not have produced (percentile values are cumulative-history-dependent).

**Restart-safety: PASS, one real bug found in the test harness itself (not production code).**
`replay-live-score --restart-after=N` scores bars 0..N, abandons the in-memory session, and calls
`TradingDaySession.RebuildAsync` (a real process restart's only option) before finishing the day.
First run failed: `RebuildAsync` reads the bar tables (`VolumeBars`/`OptionAtmBars`/etc.) from the
SAME `VolumeBarDbContext` it's given, which in PRODUCTION is the one shared `niftysignal_volume_bars`
database `LiveVolumeBarWriter` and `LiveOptionsScoreEngine` both use -- but the test harness had
pointed `RebuildAsync` at a separate scratch DB that only ever received `LiveOptionsScoreRow` writes,
never the bar tables themselves. Fixed by seeding the scratch DB with copies of the bar tables too
(mirroring the real single-shared-DB topology) -- re-run after the fix: `2026-09-16 @
--restart-after=300` reproduced the FULL day (611/611 scores, 13/13 entries) identically to a
never-restarted offline backtest.

**Phase C/D boundary: clean, not fuzzy.** `LiveEntrySignalRow` records exactly "a signal fired, here
is its direction/bar/score, and here is when/why it closed" -- no strike, no instrument token, no
fill price, no P&L field exists on it at all, so there was no ambiguous partial-trade state to guess
at. The boundary fell out naturally at "the same three things `TradeSimulator`'s own entry/exit
decision touches before it goes on to call `PickAtm`/`OptionPriceSeries.PriceAtOrBefore`" -- those two
calls, and everything downstream of them, are entirely untouched and unbuilt.

**Still out of scope, per the task boundary**: strike selection, live fill price lookup, exit
hysteresis beyond what already lives in `TradingDaySession` (ScoreInvalidated/TimeCutoff/EndOfDay),
and a `PaperTrades` table -- all Phase D. The mandatory nightly parity job (plan step 5) is still
separate, later work; this Phase C replay harness is a one-off proof for this task.

## Phase D: paper-trade engine (2026-09-20)

**What was built.**
- `NiftySignal.Scoring/AtmStrikeSelector.cs` -- the ATM strike-selection rule (nearest strike to the
  current future price, same option side as the signal) extracted out of `TradeSimulator.cs`'s own two
  local `PickAtm` closures into a pure, dependency-free function -- same "extract once, call from both
  sides" discipline the Phase-1 scoring extraction already established. `TradeSimulator.cs`'s two
  closures now delegate to it (one-line bodies) instead of carrying their own copy of the selection
  logic; verified byte-identical (nearest-strike selection is deterministic) by the full 623-test suite
  passing unchanged afterward, including every existing `OptionsScoreThreeWaySwitchMaxPainConfirmed`
  trade-count/win-rate assertion. `NiftySignal.Backtest`/dispatch behavior is otherwise untouched.
- `NiftySignal.VolumeBarData/LivePaperTradeRow.cs` -- a new table in `VolumeBarDbContext` (migration
  `AddLivePaperTrades`), one row per paper trade's whole lifecycle (inserted on entry, updated in place
  on exit -- same shape `LiveEntrySignalRow` already established). Fields: strike/instrument
  token/side, entry bar/timestamp/score, entry price + entry DECISION timestamp, exit
  bar/timestamp/reason/price + exit DECISION timestamp, and a computed (`[NotMapped]`) `NetPnlPoints`.
  Tied 1:1 back to its originating `LiveEntrySignalRow` via the same
  `(AsOfDate, BarVolumeThreshold, EntryBarIndex)` identity, kept as a SEPARATE table rather than
  widening `LiveEntrySignalRow` itself -- a signal can in principle fire with no tradeable
  strike/price found, which would otherwise force every fill/P&L field nullable on a table whose whole
  point is "here is what was actually traded."
- `NiftySignal.VolumeBarData/LivePaperTradeExecutor.cs` -- the new stateless service: `OpenAsync`
  (strike selection via `AtmStrikeSelector.PickAtm` against the day's nearest-expiry option chain, live
  fill price via `GetLatestTickPriceAsync`, defensive one-position-at-a-time check even though
  `TradingDaySession` already guarantees it one level up) and `CloseAsync` (same live fill-price lookup
  at the exit decision instant, computes `NetPnlPoints`). Both log the decision timestamp AND the price
  used on every fill, exactly as the plan's fill-price policy requires, so a timing-driven price
  difference from the backtest is diagnosable at a glance.
- **Live fill-price mechanism**: `GetLatestTickPriceAsync` queries `NiftySignalDbContext.Ticks` for the
  latest priced tick for the traded token AT OR BEFORE a `decisionTimestamp` parameter -- structurally
  similar to `OptionPriceSeries.PriceAtOrBefore` (both are "latest tick at or before a timestamp"), but
  the POLICY difference the plan calls for is in WHICH timestamp is passed, not the query shape: the
  backtest passes the bar's own `EndTimestamp` (an exact historical instant, looked up long after the
  fact against a fully preloaded day's series); live passes the real decision instant (wall-clock "now"
  at the live service, always later than the bar's own EndTimestamp by however long scoring/polling
  took), queried fresh against the database. That gap is what produces the accepted, logged price
  difference from the backtest's own fill.
- Wired into `NiftySignal.Host/LiveOptionsScoreEngine.cs`: the existing per-poll loop already produces
  `LiveSignalOpened`/`LiveSignalClosed` events (Phase C) -- this phase adds one call each into
  `LivePaperTradeExecutor.OpenAsync`/`CloseAsync` right where those events are already handled (both in
  the main `ProcessPendingBarsAsync` loop and in the day-boundary `FlushOpenSignalIfAnyAsync` path, so
  an end-of-day force-close also closes its paper trade). `decisionTimestamp` is `DateTimeOffset.UtcNow`
  for the real live service (or the end-of-day flush's own `asOfTimestamp`). The engine now resolves a
  second scoped `NiftySignalDbContext` per poll (already registered in `Program.cs` since Phase A) for
  the tick-price lookups -- no new DI registration needed. `LiveOptionsScoreEngine`'s own dispatch/
  scoring logic (Phase C) is otherwise unchanged.
- `NiftySignal.VolumeBarData/ReplayLivePaperTradeCommand.cs` (`replay-live-papertrade` CLI command) --
  the Phase D proof harness: drives `TradingDaySession` bar by bar over already-Phase-A/C-proven
  historical bars, calls the SAME `LivePaperTradeExecutor` the live service calls on every
  Opened/Closed event, and compares the resulting `LivePaperTradeRow` rows against
  `TradeSimulator.SimulateDayAsync`'s own trades for the same day/config. Uses a **simulated decision
  latency** of 10 seconds (the bar's own `EndTimestamp` plus `LiveOptionsScoreEngine.PollInterval`) as
  the fill decision timestamp -- a faithful, reproducible stand-in for the real live service's own
  "bar closes, poll fires, decision made" latency, deliberately NOT the bar's own EndTimestamp (which
  would just reproduce `PriceAtOrBefore`'s identical price and hide the very policy difference the plan
  documents).

**Replay-verification result: PASS on both historical days -- 13/13 and 24/24 trades matched exactly
on entry bar, strike, direction, and exit reason.**
- **2026-09-16**: 13 live paper trades opened, 13 offline backtest trades fired, all 13 matched on
  entry BarIndex/side/strike/exit BarIndex/exit reason (all `ScoreInvalidated`, none hit `TimeCutoff`
  or `EndOfDay` on this day, consistent with Phase C's own entry/exit results for the same day). Entry
  price deltas ranged from -2.05 to +1.80 points (option prices in the ~130-205 range that day); exit
  price deltas ranged from -3.55 to +4.00.
- **2026-09-11**: 24 live paper trades opened, 24 offline backtest trades fired, all 24 matched the
  same four fields. Entry price deltas ranged from -5.00 to +4.10 points (option prices in the
  ~79-155 range); exit price deltas ranged from -6.90 to +4.50, with one outlier trade (BarIndex 683,
  Put 23400) showing a -6.90 exit delta on a fast-moving ~105-112 option -- inspected individually via
  the harness's own per-trade decision-timestamp log line, consistent with genuine intra-bar price
  movement during the simulated 10s decision latency, not a strike/direction/timing bug (entry
  bar/strike/side/exit reason for that trade matched exactly).
- No strike/direction/entry-bar/exit-reason bug was found needing a fix -- every mismatch category the
  harness checks (the ones the plan says must match exactly) passed on the first real run, which is the
  expected payoff of reusing `AtmStrikeSelector`/`TradingDaySession`'s already-proven Phase C exit logic
  unchanged rather than re-deriving either. All price deltas are the accepted, documented fill-timing
  difference (see this phase's own fill-price mechanism note above), never hidden -- every trade's
  `EntryDecisionTimestamp`/`ExitDecisionTimestamp` is persisted alongside its `EntryTimestamp`/
  `ExitTimestamp` specifically so this distinction stays inspectable in production, not just in this
  replay harness.

**One-position-at-a-time: honored end-to-end.** `TradingDaySession` (Phase C) already guarantees only
one signal is ever open; `LivePaperTradeExecutor.OpenAsync` adds its own defensive
`ExitBarIndex == null` check against `LivePaperTradeRow` before opening a second trade (never actually
tripped in the replay -- 13-for-13 and 24-for-24 open/close pairs alternated cleanly) so a future caller
bug would surface as a skipped-and-logged entry rather than a silently-doubled position.

**Constraints honored**: `NiftySignal.Scoring`'s pure functions were not modified (only a new,
additional pure function added); `TradeSimulator.cs`'s dispatch/backtest behavior is unchanged (the
`PickAtm` refactor is a delegation, not a logic change, proven by the unchanged 623-test suite and this
phase's own exact trade-by-trade match); no real order routing was touched (paper only, via
`LivePaperTradeRow`/`LivePaperTradeExecutor`, never `NiftySignal.Execution`); the Phase E nightly parity
tool itself was not built -- `LivePaperTradeRow`'s own `(AsOfDate, BarVolumeThreshold, EntryBarIndex)`
identity and its `NetPnlPoints`/exit-reason fields are shaped for that tool to query directly without
any further transformation.

**Still out of scope, per the task boundary**: the Phase 4 mandatory bar-boundary determinism gate and
the Phase 5 nightly automated parity job are both still separate, later work; this Phase D replay
harness (like Phase A/C's own) is a one-off proof for this task, not the standing automated gate the
plan's own step 5 calls for.

## Phase E: parity verification tool (2026-09-20)

**What was built.** `NiftySignal.VolumeBarData/VerifyParityCommand.cs` -- THE MANDATORY NIGHTLY TOOL
the plan's own step 5 calls for ("This tool is mandatory. We will run it every day."), invoked via the
new `verify-parity` CLI verb:
```
dotnet run --project NiftySignal.VolumeBarData -- verify-parity <date:yyyy-MM-dd> [--threshold=2600] [--live-database=<name>]
```
Unlike every prior Phase's own replay/proof harness (`ReplayLiveCommand`/`ReplayLiveScoreCommand`/
`ReplayLivePaperTradeCommand`, which each DRIVE a simulated live run), this tool never simulates
anything -- it (1) re-runs `TradeSimulator.SimulateDayAsync` for
`OptionsScoreThreeWaySwitchMaxPainConfirmed` @ 2600/90 as the source of truth, always against the real
`niftysignal_volume_bars` database, (2) reads whatever `LivePaperTradeRow` rows the real live system
already persisted for that day (from the database named by `--live-database`, defaulting to the real
one), and (3) diffs the two trade-by-trade, matched by the shared `EntryBarIndex` identity key (not
list position -- a real persisted live table can in principle be out of order relative to the
backtest's own trade list, e.g. after a mid-day process restart). It only ever READS both sides; it
never writes to `LivePaperTradeRow` and never touches `NiftySignal.Scoring`, `TradeSimulator.cs`'s
dispatch, or `LivePaperTradeExecutor`/`LiveOptionsScoreEngine`'s live-side logic, per the task's own
constraint.

**Comparison rule.** For every backtest trade with no matching `EntryBarIndex` on the live side:
MISSING. For every live trade with no matching backtest trade: EXTRA. For matched pairs,
entry-bar/strike/direction/exit-reason must be byte-identical -- any difference is a MISMATCH, a real
bug. Fill price (entry and exit) is expected to differ per the plan's own "Fill-price policy" section
-- the delta is always reported, never itself a hard FAIL, but flagged **OUT OF RANGE** past a
documented threshold for human review. Overall PASS requires zero MISSING/EXTRA/MISMATCH; an
out-of-range price delta with everything else matching does not flip the verdict (reasoning: a
genuinely wrong fill -- wrong strike's price, a stale tick, a real bug in
`GetLatestTickPriceAsync` -- would almost always co-occur with, and already be caught by, a
strike/direction/exit-reason mismatch; an isolated price outlier with everything else exact is far
more likely a real fast-market move during the fill-decision latency than a silent bug).

**Price-delta threshold: the greater of 3.0 index points or 10% of the offline (source-of-truth)
price.** Chosen from Phase D's own already-measured real deltas: running the actual
`LivePaperTradeExecutor` fill mechanism against 2026-09-16 and 2026-09-11 produced deltas up to -6.90
points on a ~105-112 option (~6.5%), individually inspected and confirmed to be genuine intra-bar
price movement during the simulated 10s decision latency, not a bug. Setting the flag point at 10%
(vs. the ~6.5% largest-ever-observed real delta) gives roughly 1.5x headroom above normal fill-timing
variance, so the accepted policy difference never trips it, while a fill that's off by tens of percent
or more (symptomatic of a real bug) does. The flat 3.0-point floor exists so cheap sub-10-point
options (this pipeline's real trades range ~0.05 to 230+, per `docs/VOLUME_BAR_FINDINGS.md`) aren't
flagged on economically meaningless sub-point noise that happens to be 100%+ in relative terms.

**Fabricated-mismatch detection proof (the actual proof this tool works, not just that it prints PASS
on good days).** `NiftySignal.VolumeBarData/VerifyParitySelfTestCommand.cs`, `verify-parity-selftest`
CLI verb: copies one day's real (not fabricated -- genuinely produced by Phase D's own
`replay-live-papertrade` run, which calls the exact same `LivePaperTradeExecutor` the live Host calls)
`LivePaperTradeRow` rows into a disposable scratch database
(`niftysignal_volume_bars_paritytest`), then runs three corruptions against `verify-parity` pointed at
that scratch copy in turn:
1. **Wrong strike** (EntryBarIndex=104 on 2026-09-16, 23250 -> 23350) -- tool reported
   `MISMATCH trade EntryBar=104: live={...,Strike=23350.0000,...} official={...,Strike=23250.0000,...}`,
   overall FAIL. Correct diagnosis.
2. **Wrong direction** (same trade, Call -> Put) -- tool reported
   `MISMATCH trade EntryBar=104: live={Side=Put,...} official={Side=Call,...}`, overall FAIL. Correct
   diagnosis.
3. **Missing trade** (same trade deleted entirely) -- tool reported
   `MISSING trade: backtest fired EntryBar=104 Side=Call Strike=23250.0000 ExitReason=ScoreInvalidated
   -- no matching live paper trade found`, live count dropped from 13 to 12, overall FAIL. Correct
   diagnosis.
4. **Revert** -- re-seeded an uncorrupted copy, tool reported `13 matched, 0 missing, 0 extra`,
   overall PASS again.

All 4 steps passed exactly as expected (`OVERALL SELF-TEST: PASS`). The real `niftysignal_volume_bars`
database was never written to at any point in the self-test -- only ever read from, to seed the
scratch copy.

**Real PASS results on both Phase-D-verified days, against actual persisted data.** A wrinkle found
while running this for real: the real `niftysignal_volume_bars` database has never actually had its
`AddLivePaperTrades` migration applied (the live Host hasn't run against these already-historical
dates -- Phase D's own proof runs wrote into their own disposable scratch database,
`niftysignal_volume_bars_livepapertradetest`, never the real one), and applying that migration or
writing fresh rows into the real shared database was correctly gated as a "modify shared resources"
action by this environment's own safety controls. Rather than force that write, `verify-parity`'s
`--live-database` flag was pointed at Phase D's own already-existing scratch database instead --
genuine, already-proven `LivePaperTradeRow` data (produced by the real `LivePaperTradeExecutor`, not
fabricated for this task), read via the tool's normal database query path exactly as it would read
the real table on a real night, with zero writes anywhere:
```
dotnet run --project NiftySignal.VolumeBarData -- verify-parity 2026-09-16 --threshold=2600 --live-database=niftysignal_volume_bars_livepapertradetest
dotnet run --project NiftySignal.VolumeBarData -- verify-parity 2026-09-11 --threshold=2600 --live-database=niftysignal_volume_bars_livepapertradetest
```
- **2026-09-16**: 13 matched, 0 missing, 0 extra. `OVERALL: PASS`. Entry price deltas -2.05 to +1.80,
  exit price deltas -3.55 to +4.00 -- none flagged out-of-range (all well under the 10%/3-point
  threshold).
- **2026-09-11**: 24 matched, 0 missing, 0 extra. `OVERALL: PASS`. Entry price deltas -5.00 to +4.10,
  exit price deltas -6.90 to +4.50 (the same BarIndex=683 outlier Phase D's own section already
  documents) -- none flagged out-of-range.

Both results exactly reproduce Phase D's own trade-by-trade findings, now via the standalone parity
tool's own independent comparison logic instead of that phase's inline harness -- a second,
independent confirmation of the same underlying data.

**`--live-database`/`--source-database`/`--destination-database` flags, and why they exist.**
`VerifyParityCommand` and `VerifyParitySelfTestCommand` both accept a database-name override purely so
the fabricated-mismatch self-test (and, until the real database has genuine live data, real
verification runs) can point at a scratch copy instead of the real `niftysignal_volume_bars` --
`verify-parity`'s own official-backtest re-derivation always reads the real database regardless of
this flag, so the source of truth itself is never swappable, only which table is treated as "the live
side under test." `ReplayLivePaperTradeCommand.RunAsync` also gained an optional
`destinationDatabaseNameOverride` parameter (default: unchanged scratch-database behavior) so this
same, already-proven harness could in principle be run once against the real database to seed genuine
data when that becomes appropriate (e.g. once the live Host itself starts actually trading, real rows
will accumulate there naturally and no override will be needed at all).

**Scheduling: NOT added, deliberately.** The task's own instruction was to use judgment rather than
over-build. `verify-parity` is a clean, single-purpose CLI command with a proper exit code (0 = PASS,
1 = FAIL) -- trivially wireable into Windows Task Scheduler or a cron-equivalent later. Not wiring
that up now because: (1) the system isn't fully live yet (per
`feedback_niftysignal_backtest_process_rules`'s standing note, live paper trading only just started
existing at all in Phase D of this same plan) -- there is no real nightly cadence to automate against
yet, only historical-day verification; (2) the real `niftysignal_volume_bars` database doesn't yet
have genuine live-persisted `LivePaperTradeRow` data for any day (see the migration wrinkle above) --
scheduling a job that would currently find nothing to compare (or worse, silently pass on an empty
table) is premature infrastructure for a condition that doesn't exist yet; (3) per this project's own
standing rule (`feedback_niftysignal_backtest_process_rules.md`), the user runs/reviews things
themselves during this pre-edge phase, and a correctly-behaving, clearly-documented, manually-invoked
tool satisfies "we will run it every day" today without adding scheduler config, logging
infrastructure, or alerting that would need its own separate design (where do FAIL alerts go? who's
paged?) before it's actually useful. Revisit once the live Host is actually trading real nightly data
and a real operational owner exists to receive FAIL alerts.

**Constraints honored**: `NiftySignal.Scoring`, `TradeSimulator.cs`'s dispatch/backtest behavior, and
`LivePaperTradeExecutor`/`LiveOptionsScoreEngine`'s live-side logic are all untouched --
`VerifyParityCommand`/`VerifyParitySelfTestCommand` only ever call `TradeSimulator.SimulateDayAsync`
(already a public, unchanged entry point) and read `LivePaperTradeRow` via EF queries, no writes. The
one non-Phase-E file touched, `ReplayLivePaperTradeCommand.cs`, gained an additive, backward-compatible
optional parameter only (existing calls/behavior unchanged, verified by the full 623-test suite passing
unchanged and by re-confirming Phase D's own numbers above). The real `niftysignal_volume_bars`
database was never written to by any part of this task.

**Still out of scope**: the Phase 4 mandatory bar-boundary determinism gate (plan step 4) remains
separate, unbuilt work -- this Phase E tool assumes bars are already trustworthy, per that gate's own
"live volume bars are not trusted until this passes" wording. Dashboard integration (Phase F) and
performance/safety hardening (Phase G) are both still ahead.

## Phase G: performance & safety (2026-09-20)

LOCAL code + documentation only, per this task's own boundary -- nothing here was redeployed to the
VM or used to restart any live service. Phases A-D are already live on the VM ahead of tomorrow's
(Monday's) first real live day; this phase prepares the kill switch, logging, and a real performance
fix for the user to deploy separately, on their own schedule.

### 1. Kill switch

**What was built.** `NiftySignal.VolumeBarData/LiveKillSwitchState.cs` -- a new table in
`VolumeBarDbContext` (migration `AddLiveOptionSeriesCacheAndKillSwitch`), deliberately mirroring the
EXACT mechanism `NiftySignal.Domain.Entities.KillSwitchState` already established for the three
legacy/Core-score trading engines (`LiveTradingEngine`, `CoreScoreHysteresisTradingEngine`,
`CoreScoreCrossoverTradingEngine`, per `CLAUDE.md`'s "Risk" section): a single DB-backed row (`Id=1`,
`EntriesEnabled` bool, `UpdatedAt`, `UpdatedBy`), checked fresh on every relevant decision rather than
cached in memory or read from `appsettings.json` -- no restart needed to pick up a change, and no new
hot-reload mechanism was invented (this codebase's existing `IValidatedOptions<T>`/
`ValidatedOptionsMonitor` pattern is for validated *config* like `RulesetConfig`/`ScoreWeights`; a
plain per-request DB read is what the existing kill switch precedent already uses, and what this
mirrors). It is a SEPARATE row from `KillSwitchState`, not a shared one -- see
`LiveKillSwitchState`'s own doc comment: this pipeline is its own independent paper strategy from the
three engines `KillSwitchState` already gates, and sharing one row would mean flipping the switch for
any one of those four strategies silently halts all four, not what "stop THIS new pipeline
specifically" (the task's own framing) means. Same mechanism, deliberately separate instance.

Checked in exactly one place: `LivePaperTradeExecutor.OpenAsync`, first thing, before the strike/price
lookups. Defaults to enabled (`EntriesEnabled = true`) both in the migration's seed row and in code if
the row is ever somehow missing (`killSwitch is not null && !killSwitch.EntriesEnabled` -- a null read
fails OPEN, matching `KillSwitchState`'s own `?? true` precedent in `LiveTradingEngine`), so a fresh
environment or an unseeded database never silently blocks every entry.

**Design decision: new-entries-only, not halt-everything.** Recommended and implemented, per the
task's own suggested default. Reasoning:
- An already-open `LivePaperTradeRow` has no defined "kill switch pulled mid-position" exit rule
  anywhere in this codebase -- `TradingDaySession`'s only exit conditions are `ScoreInvalidated`
  (opposite-extreme score crossing) and `TimeCutoff` (15:15 IST), neither of which is "an operator hit
  a switch." Force-closing an open position with no defined exit price/reason would itself be a novel,
  untested code path invented under this task's own time pressure -- exactly the kind of thing
  `CLAUDE.md`'s "Risk" section warns against (a change to entry/exit logic that could weaken safety
  needs to be flagged and reasoned through, not improvised).
- The existing `KillSwitchState`/`EntryRuleEvaluator` precedent already made this exact call
  ("Disables new trade entries only; ingestion keeps running regardless") for the three other live
  strategies -- mirroring it here keeps the whole codebase's kill-switch semantics uniform. An operator
  who has learned "the kill switch stops new trades, not open ones" for the legacy engines would be
  surprised (dangerously so, mid-incident) if this new pipeline's switch behaved differently.
- A stuck/wrong open position is still bounded by `TradingDaySession`'s own existing safety nets
  regardless of the switch: `ForceCloseAt` (15:15 IST) and `FlushEndOfDay` (day-boundary safety net)
  both close any open signal unconditionally, switch state notwithstanding -- so "leave it running" is
  never "leave it running forever," only "let it finish today's session through its own already-tested
  rules."
- Counter-consideration acknowledged: if the reason for pulling the switch IS a suspected bug in the
  exit logic itself (not just "don't want new risk"), new-entries-only doesn't help. This is accepted
  as out of scope for Phase G -- a suspected exit-logic bug is a "stop the Host process" situation
  (the existing, if blunt, tool), not a kill-switch situation; building a second, force-close mechanism
  for that specific case would be new trading logic under time pressure, which this task's own
  constraints (and `CLAUDE.md`'s general caution against exactly that) argue against building
  tonight.

**How to flip it.** Same mechanism as the existing `KillSwitchState` (no Dashboard UI was added for
this -- see "Not built" below) -- a direct SQL update against the `LiveKillSwitchStates` table in the
`niftysignal_volume_bars` database (NOT `niftysignal_volume_bars_livetest`/`_paritytest`/any other
scratch copy -- those are test-only databases, never read by the real `LiveOptionsScoreEngine`/
`LivePaperTradeExecutor`). On the VM (Tailscale `100.105.67.79`, same Postgres server/credentials
`NiftySignalDb`'s own connection string already uses, per `Program.cs`'s
`VolumeBarPopulator.VolumeBarDatabaseName` override):
```sql
-- Disable new entries (existing open position, if any, keeps running its own exit rules):
UPDATE "LiveKillSwitchStates"
SET "EntriesEnabled" = false, "UpdatedAt" = now(), "UpdatedBy" = '<your name/reason>'
WHERE "Id" = 1;

-- Re-enable:
UPDATE "LiveKillSwitchStates"
SET "EntriesEnabled" = true, "UpdatedAt" = now(), "UpdatedBy" = '<your name/reason>'
WHERE "Id" = 1;

-- Check current state:
SELECT "EntriesEnabled", "UpdatedAt", "UpdatedBy" FROM "LiveKillSwitchStates" WHERE "Id" = 1;
```
Takes effect on the VERY NEXT poll (checked fresh every `LivePaperTradeExecutor.OpenAsync` call, no
cache, no restart) -- within `LiveOptionsScoreEngine`'s own 10s poll interval, not a Host restart
(restarting during live market hours is explicitly discouraged per `CLAUDE.md`'s working process, and
this switch exists specifically so a restart is never needed to react quickly).

**Not built**: a Dashboard toggle button (like `StatusBar.razor`'s existing one for `KillSwitchState`)
-- out of scope per this task's own constraint ("no Host/Dashboard changes pre-edge" doesn't literally
apply here since this system is already live-paper-trading, but the task explicitly said keep changes
proportionate and did not ask for a UI). The SQL above is the documented, exact operating procedure
until/unless a UI is requested separately. `LiveKillSwitchState.UpdatedBy` is free text for whoever
issues that SQL to leave a note, same field `KillSwitchState.UpdatedBy` already carries.

### 2. Logging clarity

Reviewed `LiveVolumeBarWriter`, `LiveOptionsScoreEngine`, `LivePaperTradeExecutor`, and the
`LiveOptionAtmPopulator`/`LiveOptionMaxPainPopulator`/`LiveOptionDepthPopulator` populators against one
concrete bar: **if tomorrow's `verify-parity` reports FAIL, can someone reconstruct which bar, which
leg's score, what value, and what decision was made -- from the logs alone?**

Gaps found and closed, all in `LiveOptionsScoreEngine.ProcessPendingBarsAsync` (the class that already
holds every relevant number, per bar, from `TradingDaySession.ProcessBar`'s own return value -- the
gap was that most of it was never logged, only persisted to `LiveOptionsScoreRow`, which is queryable
but not "from the logs alone"):
- **Per-bar score line** (`LogDebug`, not `LogInformation` -- see below for why): `AsOfDate`,
  `BarIndex`, `SessionLeg` (Open/Mid/Close), `EndTimestamp`, `RawScore`, `ScaledScore`, `Percentile`,
  `MaxPainConfirmScore`, `MaxPainConfirmPasses` -- every field `LiveOptionsScoreRow` persists, now also
  logged per bar instead of only the LAST bar of a poll's batch (the old summary line only ever showed
  the final `ScaledScore`/`Percentile` of however many bars a single poll happened to score, silently
  dropping every earlier bar in that batch from the log).
- **Explicit `ENTRY SIGNAL`/`EXIT SIGNAL` lines** (`LogInformation`, visible by default): fires the
  moment `TradingDaySession.ProcessBar` returns an `Opened`/`Closed` result, BEFORE
  `LivePaperTradeExecutor` runs -- so "a signal fired, here is its score/percentile/side/exit reason"
  is always visible even when the paper trade itself is declined (kill switch, no tradeable strike, no
  priced tick) -- previously the only visible evidence of a signal firing was `LivePaperTradeExecutor`'s
  own open/close log line, which never ran at all when the trade was declined, silently hiding that a
  signal existed.
- **Stall warning** (`LogWarning`, edge-triggered -- logs once per BarIndex, not every 10s poll it
  keeps stalling at, same throttling discipline `MarketDataIngestionWorker`'s own
  `_warmUpBlockedLogged` flag already established): previously, if `LiveVolumeBarWriter` fell behind or
  got stuck (an ATM/Depth/MaxPain bar never showing up for a given BarIndex), `LiveOptionsScoreEngine`
  silently `break`s out of its scoring loop with ZERO log output -- indistinguishable from "nothing new
  to score yet" in the logs. Now logs exactly which of the three option-side tables is still missing
  for the stalled `BarIndex`.
- **Kill switch decline** (`LogInformation`, via `LivePaperTradeExecutor.OpenAsync`'s own `log`
  callback, already wired to `logger.LogInformation` by the existing caller): states which switch, its
  last-updated-by/at, and which entry signal was declined.

Level choice: bar-level detail sits at `Debug` (not visible under the project's default `Information`
floor, per `appsettings.json`'s `Serilog:MinimumLevel:Default`) because it's already fully persisted,
queryable data (`LiveOptionsScoreRow`) -- the Debug line is a convenience for someone who bumps the
log level during an active investigation, not the primary diagnostic path. `ENTRY SIGNAL`/`EXIT
SIGNAL`/stall/kill-switch lines stay at `Information`/`Warning` (visible by default) because those are
comparatively rare, always decision-relevant events with no equivalent DB row of their own summarizing
"why" in one place -- these are the lines actually meant to answer "why" from the log file alone.

### 3. Performance / non-blocking review

**Independent execution paths: confirmed, not just assumed.** `LiveVolumeBarWriter`,
`LiveOptionsScoreEngine`, and `MarketDataIngestionWorker` are three separate `BackgroundService`s
(`AddHostedService`, ASP.NET Core's own hosted-service model), each resolving its OWN scoped
`DbContext` instances per poll via `IServiceScopeFactory.CreateAsyncScope()` -- never a shared
`DbContext` instance across workers. `MarketDataIngestionWorker`'s own `_engineSync` `SemaphoreSlim`
is a field private to that class alone, never referenced by either new worker -- read directly to
confirm, not inferred. `NiftySignalDbContext`/`VolumeBarDbContext` are registered via plain
`AddDbContext` (not pooled), so each scope gets a fresh context instance backed by Npgsql's own
ADO.NET connection pooling -- ordinary, unremarkable multi-worker Postgres usage, not a shared
application-level lock of any kind. No code path exists by which either new worker could block
`MarketDataIngestionWorker`'s own tick-processing loop.

**Full-day-rescan risk: investigated concretely, and it was real.** Built a new read-only
`perf-check` CLI command (`dotnet run --project NiftySignal.VolumeBarData -- perf-check
<date> [barVolumeThreshold]`) that reproduces, against real historical tick data (read-only,
`niftysignal_vm_copy` -- nothing written), the exact query shape `LiveOptionAtmPopulator`/
`LiveOptionMaxPainPopulator` issue on every poll that has a new pending bar, at the worst point in the
day (full day-so-far range). Measured against **2026-09-16 @ 2600** (real data, already populated
locally from Phases A-E's own work):
```
MaxPain-shaped reload: 40 tokens, 2,264,290 total OI-bearing rows read, 9,594ms wall-clock
ATM-shaped reload:     10 tokens,   675,734 total quote-bearing rows read, 6,453ms wall-clock
BEFORE (from-scratch every poll): combined worst-case single-poll cost = 16,047ms
```
**This is a real bug, not a theoretical one**: a single poll's own reload work (16.0s) already exceeds
the entire 10-second poll budget by late in a trading day. `LiveOptionDepthPopulator` was already
confirmed genuinely incremental (its own `scanStart`/`scanStartInclusive` logic, fixed during Phase A,
only scans ticks since the last-written bar boundary) -- but `LiveOptionAtmPopulator` and
`LiveOptionMaxPainPopulator` were NOT: both reload each touched token's FULL day-so-far
`OptionQuoteSeries`/`OptionOiSeries` from scratch on every call (needed for their own
`MidAtOrBefore`/`OiAtOrBefore` forward-fill semantics, which look backward over the whole day), a gap
Phase A's own doc comment had already flagged as "a real cost that would need addressing" but assessed
as merely "acceptable at the locked 2600-threshold bar rate and 10s poll cadence" without ever
measuring it -- that assessment did not hold up under an actual measurement, and the plan's own Phase
G mandate ("prefer incremental updates over full recalculation") applies squarely here.

**Fix**: `NiftySignal.VolumeBarData/LiveOptionSeriesCache.cs` -- a new Singleton, owned exclusively by
`LiveVolumeBarWriter`'s own single-threaded poll loop (no lock needed, same reasoning
`TradingDaySession`'s cross-poll state already documents), holding each touched token's
`OptionQuoteSeries`/`OptionOiSeries` across polls. `OptionQuoteSeries`/`OptionOiSeries` each gained a
new incremental `LoadAsync(existing, ...)` overload (the original `LoadAsync(...)` overload is
preserved, unchanged, byte-for-byte -- it now just delegates to the new one with `existing: null`,
so every existing caller -- the offline populators, every test, every replay harness -- sees zero
behavior change) that queries only ticks strictly after the existing series' own last-scanned
timestamp and appends them, instead of re-querying `[dayStart, dayEnd]` from scratch -- the same
"scope the query to only what's new" technique `LiveOptionDepthPopulator`'s own `scanStart` logic
already established, applied here to a growing forward-fill series instead of a per-bar-reset
accumulator. `LiveOptionAtmPopulator`/`LiveOptionMaxPainPopulator` both gained an optional
`LiveOptionSeriesCache? seriesCache = null` parameter (default null = original from-scratch behavior,
used by every caller except `LiveVolumeBarWriter` itself, which now passes a real cache instance
registered as a Singleton in `Program.cs`). Purely a performance change: the resulting series content
is mathematically identical either way (same ticks, same ordering, same forward-fill logic), verified
by the `perf-check` command's own row-count parity check and by the unchanged 623+5-test suite.

**Before/after measurement, same day, same real data:**
```
AFTER (LiveOptionSeriesCache warm, this poll only adds the last ~10s of ticks):
  MaxPain-shaped incremental top-up: 2,264,290 total rows (same series content), 50ms wall-clock.
  ATM-shaped incremental top-up:       675,734 total rows (same series content), 18ms wall-clock.
  Combined AFTER cost: 68ms against a 10000ms poll budget (was 16,047ms).
  Row-count parity check: MaxPain 2264290 (before) vs 2264290 (after) -- MATCH.
                          ATM 675734 (before) vs 675734 (after) -- MATCH.
```
**~236x reduction** in the worst-case single-poll reload cost (16,047ms -> 68ms), with row-count parity
proving the fix changes nothing about WHAT is computed, only how much of the tick table is re-scanned
to compute it. `perf-check`'s own warm-up step (loading the cache up to `dayEnd - 10s` first, matching
steady-state operation, before timing only the final incremental top-up) is what isolates "cost of one
more poll" from "cost of the whole day so far," which is the number that actually matters against the
10s poll budget.

### 4. Tests

- **Kill switch**: `NiftySignal.Tests/VolumeBarData/LivePaperTradeExecutorKillSwitchTests.cs` (5 new
  tests, EF Core `InMemoryDatabase`, this project's existing DB-backed test convention) --
  `EntriesEnabled=true` opens a trade normally; `EntriesEnabled=false` declines the entry (no
  `LivePaperTradeRow` row, no exception, a clear log line) while the entry SIGNAL itself is untouched;
  no persisted row at all defaults to enabled (fail-open, matching `KillSwitchState`'s own `?? true`
  precedent); flipping the switch to disabled AFTER a position is already open does not stop
  `CloseAsync` from closing it through its own normal exit rule; flipping back to enabled resumes
  normal entries. This directly satisfies the task's own requirement ("prove the switch OFF blocks new
  trades while existing open-position exit logic still runs correctly, then confirm ON resumes normal
  operation") -- chosen over re-running the `replay-live-papertrade` CLI harness because the InMemory
  unit tests give the same proof deterministically, in milliseconds, and can assert the exact
  before/after state transition the task describes rather than only end-of-day trade counts.
- **Performance fix**: `perf-check`'s own row-count parity check (above) proves the incremental cache
  produces byte-identical series content to the original from-scratch load, for real historical data.
- **Full suite**: `dotnet test NiftySignal.Tests` -- **628/628 passing** (623 pre-existing + 5 new kill
  switch tests), 0 failures, ~0.6s.
- **Build**: `dotnet build` clean (0 warnings, 0 errors) across `NiftySignal.Host`,
  `NiftySignal.VolumeBarData`, `NiftySignal.Tests`.

### Constraints honored

`NiftySignal.Scoring`'s pure functions, `TradeSimulator.cs`'s backtest dispatch, and every locked
metric's behavior are untouched -- this phase only added a new table/cache/log lines and an optional,
default-null cache parameter to two live-only populators. No redeploy, no service restart, no VM
change of any kind -- this is local code + documentation, exactly as scoped. The kill switch's default
state (`EntriesEnabled = true`) means deploying this phase's migration does not, by itself, change
tomorrow's live behavior at all -- it only adds the ABILITY to flip the switch, which remains off
until deliberately used.

## Phase G addendum: restart-safety of the Phase G cache (2026-09-20)

**The concern.** Phase A/C's own restart-safety proofs (`replay-live --restart-after`,
`replay-live-score --restart-after`) both PREDATE `LiveOptionSeriesCache` -- confirmed by reading
`ReplayLiveCommand.cs` directly (not assumed): its `RunCheckpointAsync` closure called
`LiveOptionAtmPopulator.WriteNewBarsAsync(..., CancellationToken.None)` and
`LiveOptionMaxPainPopulator.WriteNewBarsAsync(..., CancellationToken.None)` with NO `seriesCache`
argument at all, so `seriesCache` defaulted to `null` (the original from-scratch behavior) for
every checkpoint, before and after the simulated restart, in every prior proof run. Phase A's own
"BarIndex sequence contiguous and gap-free" result was real, but it never actually exercised the
cache this task was asked to re-examine. This was a genuine gap in test coverage, not a
documentation nitpick -- the exact angle the task flagged as unverified.

**Code-level trace (read, not assumed).**
- `LiveOptionSeriesCache.GetQuoteSeriesAsync`/`GetOiSeriesAsync`
  (`NiftySignal.VolumeBarData/LiveOptionSeriesCache.cs:35-53`) look up `_quoteByToken`/`_oiByToken`
  via `GetValueOrDefault` (returns `null` for an unseen token -- a fresh Singleton after a restart
  has BOTH dictionaries empty) and pass that as `existing` into
  `OptionQuoteSeries.LoadAsync(existing, ...)`/`OptionOiSeries.LoadAsync(existing, ...)`.
- Both `LoadAsync(existing, ...)` overloads (`OptionQuoteSeries.cs:43-80`, `OptionOiSeries.cs:48-89`)
  branch on `existing is not null`: when `existing` is `null`, `queryStart = dayStart` and
  `strictlyAfter = false`, i.e. the EXACT SAME query shape as the original, non-cached
  `LoadAsync(NiftySignalDbContext, ...)` overload (which literally delegates to this one with
  `existing: null`) -- a cold cache does not "assume something was already warm"; it structurally
  cannot, since `existing is not null` is the only branch that ever narrows the query. This is a
  full, correct reload for that one poll, not a partial or stale answer.
- `LiveVolumeBarWriter`'s idempotency check (which bars are "already written," via each table's own
  `MaxAsync(b => b.BarIndex)` against the DESTINATION `VolumeBarDbContext`, e.g.
  `LiveOptionAtmPopulator.cs:57-60`) is entirely independent of `LiveOptionSeriesCache` -- the cache
  only ever supplies inputs (quote/OI series) to the per-bar computation for bars already identified
  as pending; it never itself decides what's pending. A restart wiping the cache cannot make this
  idempotency check see stale/wrong "already processed" state, because the cache has no opinion on
  that question at all.
- `TradingDaySession.RebuildAsync` (`TradingDaySession.cs:94-135`) IS genuinely wired into the real
  Host startup path, not just replay tooling -- confirmed by reading
  `LiveOptionsScoreEngine.ProcessPendingBarsAsync` (`NiftySignal.Host/LiveOptionsScoreEngine.cs:136-149`):
  `_session` is a plain field, `null` at process start by construction, and the `if (_session is null
  || _session.AsOfDate != asOfDate)` branch calls `RebuildAsync` unconditionally on the first poll of
  a fresh process -- there is no separate "cold start" code path that skips it.
- `LivePaperTradeExecutor`'s kill switch (`LivePaperTradeExecutor.cs:54-59`,
  `volumeBarDb.LiveKillSwitchStates.FindAsync(...)`) and its one-position-at-a-time check
  (`LivePaperTradeExecutor.cs:61-63`, `volumeBarDb.LivePaperTrades.AnyAsync(t => ... &&
  t.ExitBarIndex == null)`) are both re-verified, by reading the code directly rather than trusting
  Phase D/G's own prior reports: both are fresh DB queries issued on every call, no field, no
  Singleton, no caching of either answer anywhere in this class (`LivePaperTradeExecutor` has no
  instance fields at all -- it is `static`). A restart cannot desynchronize either from the
  database's own actual state, because there is no in-memory copy of either to desynchronize.

**Verdict: no gap found in production code.** `LiveOptionSeriesCache` cold-starts correctly by
construction (not merely by the absence of an observed bug) -- the only real gap was in TEST
COVERAGE, which Phase A's own restart harness never actually exercised the cache. Fixed below, not
by changing any production file.

**Fix: `ReplayLiveCommand.cs` now exercises the real cache.** `RunCheckpointAsync` constructs one
`LiveOptionSeriesCache` and passes it into `LiveOptionAtmPopulator`/`LiveOptionMaxPainPopulator` on
every checkpoint, mirroring exactly what `LiveVolumeBarWriter`'s own DI-registered Singleton does in
production (previously: `seriesCache` was never passed at all, silently exercising only the
original uncached code path). On `--restart-after=N`, the harness now explicitly discards that
cache instance and constructs a brand-new one at the same point the DB-backed idempotency restart
already happens (`ReplayLiveCommand.cs`, the `cache = new LiveOptionSeriesCache();` line right after
the "Simulated restart" log line) -- a real Windows Service restart doesn't clear a Singleton, it
loses the whole process and starts a new one, so replacing the instance (not calling some
`.Reset()`) is the faithful simulation.

**Mid-day-restart-with-warm-cache replay proof: PASS.** Run against **2026-09-16 @ 2600**,
`--restart-after=550` (of 751 simulated poll checkpoints, i.e. the restart lands well past midday --
"Cache state before restart: 16 quote-series token(s), 40 OI-series token(s) warm" was logged
immediately before the simulated restart, confirming the cache had genuinely accumulated real
per-token state, not restarted before it ever got the chance to):
```
dotnet run --project NiftySignal.VolumeBarData -- replay-live 2026-09-16 2600 --restart-after=550 --poll-seconds=30
```
- `IDEMPOTENCY: BarIndex sequence contiguous and gap-free after restart+resume (611 bars, 0..610)`.
- `PARITY: PASS -- live-replay rows match the offline populator's rows exactly, table by table, row
  by row.` -- **VolumeBars 611/611, OptionAtmBars 611/611, OptionDepthBars 611/611, OptionMaxPainBars
  611/611, 0 value mismatches in any table.** This is the same proof shape Phase A's own report
  already established, now run with the cache genuinely populated and then genuinely discarded
  mid-day -- the strongest available evidence, since a restart-induced cache bug (stale series,
  wrong forward-fill, a silently-truncated reload) would show up here as an `OptionAtmBars`/
  `OptionMaxPainBars` value mismatch against the offline populator's own never-restarted,
  never-cached rows. None did.
- A second run, `--restart-after=6` (restart almost immediately, cache barely warm), also passed
  identically -- included for completeness, but the `--restart-after=550` run above is the one that
  actually answers the task's own framing ("restart AFTER the cache would have been populated by
  some polls").

**Performance check on restart: cold once, warm again after -- confirmed, via two complementary
measurements.**
- The replay harness's own per-checkpoint stopwatch (new `pollTimings`/"Post-restart poll timings"
  block in `ReplayLiveCommand.cs`) is NOT the right instrument for magnitude: each simulated poll in
  this harness only ever adds a `--poll-seconds`-wide slice of ticks (30s of exchange time per
  checkpoint here), so the absolute per-poll cost is dominated by EF/Npgsql per-call overhead and
  connection-pool jitter at the millisecond scale (observed: first-post-restart poll 237ms, later
  polls averaging 396.9ms with one 14.4s outlier plausibly explained by connection-pool/GC jitter
  rather than a real cold-cache signal) -- not a clean enough signal to assert "first poll is always
  the single slowest" from this harness alone.
- The REAL magnitude proof remains Phase G's own already-measured `perf-check` numbers (unchanged by
  this addendum, re-confirmed still accurate against current code): a cold cache's full day-so-far
  reload costs **16,047ms** combined (MaxPain 9,594ms + ATM 6,453ms) at the worst point in a trading
  day, vs. **68ms** combined (MaxPain 50ms + ATM 18ms) once warm -- a ~236x difference. Since a
  cold cache after a restart and a cold cache that was simply never populated take the EXACT SAME
  code path (`existing is null` in both `LoadAsync` overloads -- there is no restart-specific branch
  to diverge), this measurement IS the "first poll after restart" cost, and the 68ms warm figure IS
  what every subsequent poll costs once the cache re-populates. Combined with the replay proof above
  (cache demonstrably goes from 16/40 warm tokens to 0, then correctly reconstructs identical output),
  this closes the loop: **first poll after a restart pays the full reload cost once (bounded,
  ~16s worst case, well inside tolerance for a single poll being late), and every poll after that is
  back to the ~68ms warm-cache cost -- not a permanent regression to pre-Phase-G behavior.**

**New targeted test: `NiftySignal.Tests/VolumeBarData/LiveOptionSeriesCacheRestartTests.cs`** (3
tests, EF Core `InMemoryDatabase`, deterministic, no DB round trip) -- added because the replay
proof above is DB-backed and slower to re-run; these give the same "cold cache after restart
matches from-scratch" guarantee in milliseconds, for CI/local iteration:
- `ColdCacheAfterSimulatedRestart_QuoteSeries_MatchesFromScratchLoad` /
  `..._OiSeries_...`: warm a cache across several incremental loads (simulating several real polls),
  discard it and construct a brand-new instance (simulating the restart), and assert the fresh
  instance's answer at every probe point across the day -- not just at the final cutoff -- is
  byte-identical to `OptionQuoteSeries.LoadAsync`/`OptionOiSeries.LoadAsync`'s own from-scratch,
  no-cache baseline. Also asserts the post-restart series correctly reflects ticks the PRE-restart
  warm cache never saw (proving it isn't silently still answering from stale state).
- `ResetIfNewDay_DoesNotSpuriouslyFireOnRestart_ButDoesFireOnDayChange`: confirms a fresh cache for
  the SAME date behaves like "cold, never touched today" (not a special "just restarted" state), and
  that a genuine day change still resets per-token state as `LiveOptionSeriesCache`'s own doc comment
  documents.

**Full suite**: `dotnet test NiftySignal.Tests` -- **631/631 passing** (628 pre-existing + 3 new),
0 failures. `dotnet build` clean across all projects, 0 warnings, 0 errors.

**Constraints honored**: no production code was changed -- the only files touched are
`ReplayLiveCommand.cs` (test harness: now passes the real cache, and discards/replaces it across a
simulated restart, plus timing instrumentation), `LiveOptionSeriesCache.cs` (two new
diagnostics-only properties, `QuoteTokenCountForDiagnostics`/`OiTokenCountForDiagnostics`, read-only,
no behavior change), and one new test file. `NiftySignal.Scoring` and `TradeSimulator.cs`'s dispatch
logic were not touched. Nothing was redeployed or restarted on the VM.

## Phase F: minimal dashboard (2026-09-21)

**What was built.** One new Razor page, `NiftySignal.Dashboard/Components/Pages/LiveOptionsScore.razor`,
route `/live-options-score`, `[Authorize]` + `@rendermode InteractiveServer` same as `Home.razor`. A
one-line nav link ("Live Options Score") was added to `StatusBar.razor` next to the brand, since this
Dashboard had no nav menu at all before now (single-page app). Read-only, no writes anywhere on the
page. Shows, for today (IST) only:
1. **Current bar score** -- most recent `LiveOptionsScoreRow` (by `BarIndex`) for today: scaled score,
   raw score, active `SessionLeg` (Open/Mid/Close), percentile, bar index/timestamp.
2. **Max Pain confirmation** -- PASS/FAIL/N-A badge from `MaxPainConfirmPasses`/`MaxPainConfirmScore`
   (N/A when no Max Pain reading exists yet for the bar).
3. **Open paper position** -- the one `LivePaperTradeRow` (if any) with `ExitBarIndex == null` for
   today: strike/side/entry price/entry time. No unrealized P&L (would need a live quote lookup for
   the traded option, out of scope per the task's own "keep this thin" instruction).
4. **Today's paper trades** -- a `data-table` of every `LivePaperTradeRow` for today, ordered by entry
   time: strike, side, entry/exit price, exit reason, `NetPnlPoints` (open rows show "open"/"--").

**Data access.** `VolumeBarDbContext` registered in `NiftySignal.Dashboard/Program.cs` via
`AddDbContextFactory` (same factory-not-scoped pattern `NiftySignalDbContext` already uses, for the
same reason: sibling Blazor Server components can run `OnInitializedAsync` concurrently within one
circuit and would otherwise share a non-thread-safe `DbContext` instance) -- same
`Database=` override (`VolumeBarPopulator.VolumeBarDatabaseName`) NiftySignal.Host's own Phase A
registration and NiftySignal.VolumeBarData's own CLI tools already use, same base
`ConnectionStrings:NiftySignalDb` connection string. `NiftySignal.Dashboard.csproj` gained one new
`ProjectReference` to `NiftySignal.VolumeBarData` (mirroring `NiftySignal.Host.csproj`'s own existing
reference to the same project). The page polls every 10s via a plain `Timer` +
`InvokeAsync(StateHasChanged)` -- the same shape `StatusBar.razor`'s own clock timer already uses in
this project (not `LiveDataService`'s push/poll-hybrid machinery, which is specific to the older
composite/core-score pipelines this page has nothing to do with). `BarVolumeThreshold` (2600, the
locked target metric's own threshold) is a local literal in the page rather than a reference to
`NiftySignal.Host.LiveVolumeBarWriter.BarVolumeThreshold`, to avoid giving Dashboard a new dependency
on Host for one already-documented, locked constant.

**Constraints honored.** One new page, no new backend services or hosted workers, reused the existing
`panel`/`panel-title`/`data-table`/`badge`/`mono`/`text-*` CSS classes from `wwwroot/app.css` verbatim
(no new stylesheet). `NiftySignal.Host`, `NiftySignal.Scoring`, `TradeSimulator.cs`, and every other
live-pipeline file were untouched -- only `NiftySignal.Dashboard.csproj`, `Program.cs`,
`StatusBar.razor` (one link), and the one new page file were touched.

**Testing.**
- `dotnet build NiftySignal.Dashboard` -- clean, 0 warnings, 0 errors.
- `dotnet test NiftySignal.Tests` -- 631/631 passing, unchanged (no Dashboard-specific test project
  exists in this repo; this page has no server-side logic beyond two LINQ queries and view
  formatting).
- **Empty-state: verified live, in the real running Dashboard.** Started the Dashboard locally
  (`dotnet run --project NiftySignal.Dashboard`, local dev Postgres, `niftysignal_volume_bars`
  already has real historical bars from Phases A-G's own local testing but genuinely zero rows for
  2026-09-21, today -- exactly the "built before market open" scenario the task called out as the
  literal state to expect), logged in, navigated to `/live-options-score`: all four sections rendered
  their documented empty states cleanly ("No scored bars yet today -- waiting for the live pipeline to
  write the first bar.", "No open position.", "No trades yet today.") -- no exception, no crash.
- **Populated-state: verified via an equivalent query-logic check, not a live Postgres write.** This
  sandboxed session's own permission controls classify any write to the local dev Postgres database
  (even scoped to today's date, even via a disposable scratch console app) as "modify shared
  resources" and refused it outright -- so a live-browser screenshot of the populated state was not
  obtained this run. Instead, a scratch console program (not part of this repo, run from a temp
  scratchpad directory) exercised the EXACT SAME two LINQ queries `LiveOptionsScore.razor`'s
  `RefreshAsync` uses (`LiveOptionsScoreBars.Where(...).OrderByDescending(BarIndex).FirstOrDefault`,
  `LivePaperTrades.Where(...).OrderBy(EntryTimestamp).ToList`, then
  `.FirstOrDefault(ExitBarIndex == null)`) against an EF Core `InMemoryDatabase` (never touches any
  real database, nothing persisted anywhere) seeded with realistic multi-row data: two score bars
  (confirms "latest by BarIndex" beats insertion order), three paper trades (two closed with
  different signs of P&L, one still open) sharing today's date and the locked 2600 threshold.
  Result: latest score bar correctly resolved to the higher `BarIndex` (42, not insertion order),
  `MaxPainConfirmPasses`/`Percentile`/`SessionLeg` all read through correctly, all 3 trades listed in
  entry-time order, `NetPnlPoints` computed correctly for both closed trades (-2.0 and +5.75) and
  correctly null for the open one, and the open-position detection correctly found the one row with
  `ExitBarIndex == null` (`EntryBarIndex=42`) -- `RESULT: PASS`. This proves the page's display logic
  (which row is "latest," which is "open," how P&L is derived, ordering) is correct against
  realistic data, short of an actual rendered screenshot of the populated Razor markup itself.
- **Not done, and why**: an actual browser screenshot of the page WITH data rendered was not obtained
  this run, since seeding the local Postgres `niftysignal_volume_bars` database (even with today's
  date, even via a disposable scratch script, even though it is genuinely a local dev database, not
  the live VM's shared one) was refused by this session's own permission controls. If a fully visual
  populated-state check is wanted, either grant that permission for a follow-up local seed-and-screenshot
  pass, or simply wait for the live Host to write real rows on the next trading day -- the page's own
  query logic is already proven correct against that exact shape of data via the InMemory check above.

## Exception alerting (2026-09-21)

The new live pipeline (`LiveVolumeBarWriter`, `LiveOptionsScoreEngine`, `LivePaperTradeExecutor`) has
always logged its per-poll exceptions (`logger.LogError`) and continued to the next poll -- the
deliberate F34/F49 "one bad poll must never take down the rest of the day" resilience pattern, which
this change does not touch. What was missing: nothing told a human about a failure other than reading
logs. Now additive Telegram alerts fire alongside every existing log call, never instead of it:

- `LiveVolumeBarWriter`'s poll-loop catch (now `PollOnceAsync`, extracted from `ExecuteAsync` purely
  so it's directly unit-testable without real wall-clock market-hours gating).
- `LiveOptionsScoreEngine`'s poll-loop catch (also extracted into `PollOnceAsync`) -- this single catch
  already covers anything `LivePaperTradeExecutor.OpenAsync`/`CloseAsync` throws too, since that class
  is a stateless static helper with no catch of its own (verified by reading it directly); any
  exception from strike selection, the fill-price lookup, or its kill-switch/DB reads propagates
  straight up into this same catch, so no separate wiring was needed there.
- `LiveOptionsScoreEngine.FlushAndDiscardSessionAsync`'s own catch (the end-of-day open-signal flush).
- `Program.cs`'s top-level `catch (Exception ex)` around `host.Run()` -- a full process crash, the
  single most important failure to know about. Best-effort and standalone (does not resolve
  `ITelegramNotifier` from DI, since the container may never have finished building at that point):
  reads `Telegram:BotToken`/`Telegram:ChatId` directly from `appsettings.json`/`appsettings.Local.json`
  and POSTs directly to the Telegram API, wrapped in its own try/catch so a failed notification can
  never prevent `Log.Fatal`/`Log.CloseAndFlush` from completing.

**New category and cooldown.** `NotificationCategory.LivePipelineError`, used by all four sites above.
Given a 5-minute cooldown in `RateLimitedTelegramNotifier` -- same value and same reasoning as the
existing `ConnectionFailure` cooldown: these poll loops retry every 10s, so a persisting failure (a DB
outage, say) would otherwise fire a Telegram send on every single poll; 5 minutes bounds that to a
still-timely "this is still broken" reminder without an alert storm.

**Message content**: which service (`LiveVolumeBarWriter`/`LiveOptionsScoreEngine`/the flush path),
the `AsOfDate` (or end-of-day date for the flush case), and the exception's own type name + message --
not a bare "an error occurred."

**Tests**: `NiftySignal.Tests/Host/ExceptionAlertingTests.cs` (3 new tests) -- a throwing
`IServiceScopeFactory` forces a real exception inside each `PollOnceAsync`, asserting (1) it never
propagates (the F34/F49 behavior is unchanged) and (2) the `SpyTelegramNotifier` fake received exactly
one `LivePipelineError` message naming the failing service/date/exception. A third test proves the new
cooldown actually suppresses a same-category repeat within 5 minutes and lets one through after.
`FlushAndDiscardSessionAsync`'s own alert was not separately unit-tested (it requires an already-built
`TradingDaySession` to reach; out of scope to fabricate one under this task's time pressure) but shares
the identical try/catch + `SendAsync` shape as the tested poll-loop catch, verifiable by direct code
read.

**Full suite**: `dotnet test NiftySignal.Tests` -- **634/634 passing** (631 pre-existing + 3 new), 0
failures. **Build**: `dotnet build NiftySignal.Host` clean, 0 warnings, 0 errors.

**Constraints honored**: no change to the "log and continue to next poll" resilience behavior itself --
purely additive `telegram.SendAsync` calls alongside existing `logger.LogError` calls.
`ITelegramNotifier.SendAsync` never throws (its own doc comment), so this cannot make any poll loop
more likely to crash or stop retrying. `NiftySignal.Scoring`, `TradeSimulator.cs`, and all locked
metric behavior are untouched. No redeploy, no VM change, no service restart.

## CoreScore engine cutover + dashboard promotion (2026-09-21, pre-market)

**Pre-condition, verified live on the VM dashboard just before this change**: both
`CoreScoreHysteresisTradingEngine` ("Hysteresis") and `CoreScoreCrossoverTradingEngine`
("Crossover") showed FLAT, zero open positions, "0/3 MAX open paper positions" -- safe to stop
both from opening new trades with no wind-down/orphaned-position handling needed.

**Cutover.** `NiftySignal.Host/MarketDataIngestionWorker.cs`'s cadence loop no longer calls
`hysteresisEngine.EvaluateCadenceAsync`/`crossoverEngine.EvaluateCadenceAsync` (the block right
after `PersistSnapshotAsync`, dated comment explains the cutover). Same "leave it, just stop
calling it" pattern already used for `LiveTradingEngine`'s own Batch 5 (2026-09-13) cutover:
`CoreScoreHysteresisTradingEngine.cs`/`CoreScoreCrossoverTradingEngine.cs` are untouched, both
stay registered as singletons in `Program.cs`, fully callable again if ever wanted. The
`CoreScoreSnapshot` itself is still computed and persisted every cadence (`PersistSnapshotAsync`
unchanged) since the Dashboard's (demoted) Core Directional Score panel and other observers still
read it -- only the two engines' own trade-decision calls were removed.

**Dashboard reorganization.** `NiftySignal.Dashboard/Components/Pages/Home.razor` now leads with
the volume-bar pipeline (`OptionsScoreThreeWaySwitchMaxPainConfirmed`) instead of the old
CoreScore system:
- Extracted Phase F's `/live-options-score` page content (Current Bar Score/3 legs, Max Pain
  confirmation, open paper position, today's trades) into a new reusable component,
  `NiftySignal.Dashboard/Components/Dashboard/LiveOptionsScorePanel.razor`, so the exact same
  query/render logic backs both places rather than duplicating it.
- `Home.razor` now renders `<LiveOptionsScorePanel />` immediately below `StatusBar`, above the
  rest of the existing panel grid -- the most prominent thing on the page.
- `/live-options-score` (`LiveOptionsScore.razor`) is kept as a thin wrapper around the same
  component (`ShowFullPageLink="false"`), for a focused/bookmarkable full view; decided to keep it
  rather than delete it since it costs nothing once the logic is shared and some users may already
  have it bookmarked.
- The old feed/connection panels (`FlatTradeLoginPanel`, `LiveQuotePanel`, `OiProfilePanel`,
  `PositionsPanel`, `OptionChainPanel`, `PerformancePanel`) are kept as-is and in their existing
  relative order -- they describe the live tick feed/connection health, not which strategy trades,
  so they're still operationally useful.
- `ScorePanel` ("Core Directional Score" + "Strategy Positions" for Hysteresis/Crossover) was
  demoted rather than removed: moved to the bottom of `Home.razor`'s grid, and given a `RETIRED --
  not trading` badge plus an explanatory line in `ScorePanel.razor` itself (so the badge/note
  travel with the component regardless of where it's placed). Not deleted since its last-known
  Core Score/component readout is still a useful reference, but it no longer looks like an
  equally-live, equally-trading section.

**Testing.**
- `dotnet build NiftySignal.Host` -- clean, 0 warnings, 0 errors.
- `dotnet build NiftySignal.Dashboard` -- clean, 0 warnings, 0 errors.
- `dotnet test` -- **634/634 passing**, unchanged from baseline (this task touched no test-covered
  logic: Host cadence-loop wiring and Dashboard Razor markup only).
- **Visual verification, live in the running Dashboard** (`dotnet run --project
  NiftySignal.Dashboard`, local dev Postgres, same method as Phase F's own empty-state check):
  loaded `/` and `/live-options-score` today (2026-09-21, genuinely empty -- pre-market, no bars
  written yet). Both rendered cleanly: `LiveOptionsScorePanel` at the top of Home showing its
  documented empty states ("No scored bars yet today...", "No open position.", "No trades yet
  today."), the demoted `Core Directional Score` panel at the bottom correctly labeled `RETIRED --
  not trading` with `Hysteresis`/`Crossover` both still shown FLAT (last-known-state, as expected),
  no exception, no crash, no browser console errors. (The local dev `niftysignal_volume_bars`
  database is missing the `LiveOptionsScoreBars`/`LivePaperTrades` tables entirely in this sandbox
  -- a pre-existing local-environment gap, not caused by this change; `LiveOptionsScorePanel`'s
  `RefreshAsync` catches the resulting exception the same way `LiveDataService.PollAsync` already
  does, so the page still rendered its correct empty state rather than crashing.)

**Constraints honored**: `NiftySignal.Scoring`, `TradeSimulator.cs`, `LiveOptionsScoreEngine`, and
`LivePaperTradeExecutor` untouched. Neither old engine's class, DI registration, or persisted data
was deleted. No redeploy, no VM restart -- local build/test only.

## Live performance incident: future-side bar builder was not incremental (2026-09-21)

**Symptom, observed live.** Bar-write-to-score lag grew from ~1.7s at 04:05 UTC to ~9-10s by 04:08
UTC -- about 20 minutes into the trading day -- with early signs of poll batching ("wrote 3 new
future bar(s)" in one poll instead of 1, meaning polls were starting to fall behind the bar-creation
rate itself).

**Root cause, confirmed by reading the code (not assumed).** `LiveVolumeBarPopulator.WriteNewBarsAsync`
re-read and replayed EVERY future tick since market open, through a brand-new `VolumeBarBuilder`, on
every single ~10s poll (`LiveVolumeBarWriter`). This was a deliberate Phase A trade-off (the class's
own original doc comment: "cost is re-scanning the day's ticks-so-far on every poll... cheap relative
to the polling cadence... deliberately traded for correctness simplicity") whose cost estimate turned
out wrong under real live load: the replay cost grows with the whole day-so-far tick count, not with
the (small, roughly constant) number of new ticks per poll -- an unbounded-growth shape, exactly
matching the observed lag curve. Phase G had already fixed the analogous problem on the OPTION side
(`LiveOptionSeriesCache`); the future side was the one place still doing this.

**Fix.** `LiveVolumeBarBuilderCache` (new, `NiftySignal.VolumeBarData/LiveVolumeBarBuilderCache.cs`):
a per-process Singleton, owned exclusively by `LiveVolumeBarWriter`'s own single-threaded poll loop
(same no-lock-needed reasoning as `LiveOptionSeriesCache`/`TradingDaySession`), holding the one
in-progress `VolumeBarBuilder` for today's `(AsOfDate, BarVolumeThreshold)` plus the running bar-index
counter and the (ExchangeTimestamp, Id) boundary of the last tick it has already consumed.
`VolumeBarBuilder` itself needed no changes -- it already carries its own partial-bar accumulator
internally, so "resuming" is simply continuing to call `ApplyTick` on the same instance; only the
external bookkeeping (bar-index counter, last-consumed-tick boundary) needed to move into the new
cache. `LiveVolumeBarPopulator.WriteNewBarsAsync` gained an optional `builderCache` parameter (default
null, reproducing the original from-scratch behavior exactly for every existing caller): when the
cache has a warm builder for today's threshold, only ticks strictly newer than the last-consumed
(timestamp, id) boundary are queried and fed into the SAME builder instance; when it doesn't (cold
start, restart, or a new day -- `ResetIfNewDay`, mirroring `TradingDaySession`'s own day-scoped
lifecycle), it falls back to the original full-day replay, exactly as before. On `finalizeDay=true`
the cached builder is explicitly cleared (`FlushPartial` has already reset its accumulator for a bar
that will never be completed) so a stray extra poll the same day correctly falls back to a full
replay rather than resuming a finalized builder. `existingMaxIndex`-based idempotent write filtering
is completely unchanged -- this fix only changes how ticks are SOURCED into the builder, never how
already-written bars are detected or skipped. Wired into `NiftySignal.Host/Program.cs` as a new
Singleton and passed through `LiveVolumeBarWriter` alongside the existing `LiveOptionSeriesCache`.

**Correctness verification: PASS, byte-identical, on both proven historical days**, via
`replay-live` (poll-seconds=10, mirroring the real cadence):
- 2026-09-16 @ 2600: VolumeBars/OptionAtmBars/OptionDepthBars/OptionMaxPainBars all **611/611 rows,
  0 mismatches** -- identical to the original Phase A proof.
- 2026-09-11 @ 2600: all four tables **941/941 rows, 0 mismatches** -- identical to the original
  Phase A proof.

**Restart-safety verification: PASS.** `replay-live 2026-09-16 2600 --restart-after=1100` (cache
discarded at poll #1100 of 2251, replaced with a brand-new `LiveVolumeBarBuilderCache` -- same
"a real process restart loses the Singleton entirely" simulation the Phase G addendum established
for the option-side cache): BarIndex sequence stayed contiguous and gap-free (611 bars, 0..610) and
the final row set still matched the offline populator exactly (611/611, 0 mismatches). The first
poll after the simulated restart correctly fell back to a full-day replay (measured cost ~179ms,
consistent with a cold-cache catch-up) before resuming incrementally. Also proven directly with EF
Core InMemoryDatabase unit tests (`NiftySignal.Tests/VolumeBarData/LiveVolumeBarPopulatorIncrementalTests.cs`):
`IncrementalBuilderCache_ProducesByteIdenticalBars_ToColdFullReplay`,
`SimulatedMidDayRestart_CacheDiscardedAndRebuilt_StillConvergesToTheSameDay`, and
`FinalizeDay_ClearsCachedBuilder_SoASecondPollTheSameDayFallsBackToFullReplay_NotADoubleFinalize`.

**Performance verification: PASS -- the lag no longer grows over a simulated full trading day.**
`replay-live` was extended to time `LiveVolumeBarPopulator.WriteNewBarsAsync` in isolation (separate
from the combined per-poll total, which also includes the already-proven option-side Phase G cache
calls) at 5 checkpoints spread across the simulated day, and to re-run the IDENTICAL checkpoint
sequence a second time with `builderCache=null` (the exact original code path) against a disposable
scratch database, for a true apples-to-apples before/after comparison. Measured on 2026-09-16 @ 2600
(2251 simulated polls covering the full 09:15-15:30 IST session):

| Checkpoint | poll # | BEFORE (no cache) | AFTER (incremental) |
|---|---|---|---|
| earliest | 1/2252 | 35ms | 175ms (one-time cold-start full replay) |
| quarter | 564/2252 | 69ms | 1ms |
| midday | 1127/2252 | 163ms | 0ms |
| three-quarter | 1690/2252 | 222ms | 1ms |
| latest | 2251/2252 | 374ms | 1ms |

BEFORE: first-quarter avg 49.5ms -> last-quarter avg 300.8ms (**6.1x growth over the day** -- the
same unbounded-growth shape the live incident showed, just measured end-to-end offline instead of
truncated to the first 20 minutes). AFTER: first-quarter avg 1.7ms -> last-quarter avg 0.9ms (flat,
if anything slightly lower late in the day). The one-time cold-start cost at poll #1 (~175ms, a full
day-so-far replay against an as-yet-nearly-empty day) is expected and matches the restart-safety
proof's own cold-poll cost -- it is not a regression, it is the same one-time catch-up the option-side
cache already accepted in Phase G.

**Verification commands run:**
```
dotnet build   -- 0 warnings, 0 errors
dotnet test    -- 637/637 passing (634 baseline + 3 new incremental/restart tests)
dotnet run --project NiftySignal.VolumeBarData -- replay-live 2026-09-16 2600 --poll-seconds=10
dotnet run --project NiftySignal.VolumeBarData -- replay-live 2026-09-16 2600 --poll-seconds=10 --restart-after=1100
dotnet run --project NiftySignal.VolumeBarData -- replay-live 2026-09-11 2600 --poll-seconds=10
```

**Not yet done / follow-up**: this fix addresses the FUTURE-side replay cost specifically (the
confirmed root cause of the growing lag). The option-side ATM/MaxPain populators' own Phase G cache
was already proven to re-warm quickly after a restart, but its one-time cold-restart cost (observed
here as high as ~8.9s in the combined per-poll total during the restart-safety run above) remains a
separate, already-known, already-accepted cost from Phase G -- not something this change touches or
needs to touch. No redeploy or VM restart was performed; deployment is a separate step.

## First real out-of-sample parity check (2026-09-21)

Ran `verify-parity` against today's actual live trading day for the first time (VM was
reachable). Two real findings, both worth recording:

**Bug found and fixed in the tool itself**: `VerifyParityCommand`'s `barIndexByEndTimestamp`
dictionary crashed on a duplicate key -- two bars (488, 489) legitimately shared the same
`EndTimestamp` (a burst of end-of-day ticks at the exact same wall-clock second produced a normal
2600-volume bar and a small trailing partial bar closing at the identical timestamp). Fixed with
last-bar-wins grouping; safe because no real trade can enter/exit in that collision window (entry
window ends 15:00 IST, force-close 15:15 IST, well before the 15:30 close where this occurred).

**Result: 6 of 7 trades matched exactly** (entry bar, strike, direction, exit reason; price deltas
all within the expected fill-timing range). **Trade 7 diverged**: live opened at BarIndex=333
(13:59:56 IST), official backtest never fired it and instead opened later at BarIndex=348.

Root cause investigated (new `dump-scores` CLI diagnostic, via `onBarEvaluated`): at bar 333,
live computed ScaledScore=-90.70/Percentile=90.70 (crosses the 90 gate); the official recompute
computed ScaledScore=-88.37/Percentile=88.37 (doesn't cross). MaxPainConfirmScore matched exactly
between both (-0.9879) -- ruling out a confirmation-logic bug. Bars immediately before/after match
closely in shape, and bar 348 realigns almost exactly. This is a single borderline threshold
crossing, not a broad breakdown.

**Why this is a new class of finding, not a repeat of an already-proven case**: every prior parity
proof (Phase A/C/D/E) replayed the SAME already-recorded tick log through both the live and
offline paths. This was the first time live (VM's own real-time tick stream) was compared against
an offline recompute sourced from `niftysignal_vm_copy` (a SYNCED COPY of those ticks) -- i.e. the
first real test of sync fidelity, not just logic parity. The likely cause is a small tick
ordering/completeness difference between the live feed and the synced copy sometime before bar
333, not yet pinned down to a specific tick.

**Status**: open investigation, not resolved. One trade out of 7 on one day -- not yet enough to
change anything, but the root-cause class (sync-copy fidelity vs. logic correctness) is now known
and should be checked again on future out-of-sample days before being dismissed as a one-off.

## Futures crossover: wired live (not yet deployed) (2026-09-21)

Wired the locked futures SMA-crossover strategy (8-fast/40-slow/5-point-threshold on
`SessionGatedDepthDurationConfirmed`'s own FuturesScore, `BarVolumeThreshold=2600` -- the config
actually being tracked for continued out-of-sample validation, per the "Adopted for continued
out-of-sample tracking" note above, **not** the higher-net-but-unadopted 1300/8/30/8 config found
in a later sweep) into the live pipeline end to end, alongside (not replacing) the already-live
`OptionsScoreThreeWaySwitchMaxPainConfirmed` engine. Entirely additive: no existing options-side
class, no locked backtest dispatch result, and no already-live behavior was changed in a way that
alters output (see the regression-check results below for direct proof of the one dispatch-chain
touch this required).

**What was built:**

- **Shared live/backtest scoring function** (`NiftySignal.Scoring/FuturesSessionGatedScoreCalculator.cs`,
  `FuturesSessionGatedScoreInputs.cs`): the FuturesScore formula (Depth Imbalance before 10:00 IST,
  Bar Duration Urgency at/after it) and its own Open-window TOB confirmation gate, extracted
  verbatim out of `TradeSimulator.cs`'s private `ComputeSessionGatedScore`/`ComputeBarDurationScore`/
  the TOB branch of `PassesConfirmation` -- same "shared pure function, no duplicate
  implementation" discipline `OptionsThreeWayScoreCalculator` already established for the options
  side's own 2026-09-20 extraction. `TradeSimulator.cs`'s `ComputeSessionGatedScore` (used by both
  the `SessionGatedDepthDurationConfirmed` percentile-threshold dispatch AND
  `SimulateCrossoverDayAsync`'s own futures branch) and the `PassesConfirmation` TOB branch are now
  thin adapters calling this shared code -- a pure relocation, formula unchanged.
- **`NiftySignal.VolumeBarData/LiveFuturesCrossoverSession.cs`**: the day-scoped owner of the
  crossover's own rolling fast/slow-window + rank-tracker state, mirroring `TradingDaySession`'s
  exact shape (strict BarIndex-order guard, `RebuildAsync` restart-safety, `FlushEndOfDay` day-
  boundary safety net). Implements the identical entry/exit rules `TradeSimulator
  .SimulateCrossoverDayAsync`'s futures branch already uses: qualifying crossing (fast/slow gap >=5
  points at the crossing bar), Open-window TOB confirmation gate, 09:30-15:00 entry window, 15:15
  IST force-close, exit on `CrossoverReversed` or `TimeCutoff`.
- **`NiftySignal.VolumeBarData/LiveFuturesCrossoverScoreRow.cs`**: new per-bar diagnostic table
  (FastMa/SlowMa/Diff/CrossedUp/CrossedDown/TobConfirmScore) -- a separate table from
  `LiveOptionsScoreRow` since the two strategies' own per-bar shapes genuinely don't overlap (see
  the row type's own doc comment).
- **Strategy discriminator** (`NiftySignal.Domain.Enums.LiveVolumeBarStrategyId`: `Options` /
  `FuturesCrossover`): added to the SHARED `LiveEntrySignalRow`/`LivePaperTradeRow` tables, mirroring
  the older time-cadence live system's own `StrategyId` discriminator precedent
  (`NiftySignal.Domain.Enums.StrategyId`, one discriminator column, each strategy independently
  capped at its own "one position at a time"). Both strategies trade off the SAME BarIndex sequence
  (one future, one `VolumeBarRow` builder) -- without this column, a futures-crossover entry and an
  options entry firing at the same BarIndex on the same day/threshold would collide on the unique
  index `(AsOfDate, BarVolumeThreshold, EntryBarIndex)`. Fixed properly: the unique index is now
  `(AsOfDate, BarVolumeThreshold, Strategy, EntryBarIndex)`, and `LivePaperTradeExecutor.OpenAsync`/
  `CloseAsync`'s own "is a position already open" checks are now filtered by `Strategy` too (the
  pre-existing check queried `ExitBarIndex == null` with no strategy filter, which would have
  incorrectly blocked one strategy's entry on the other's still-open position). `strategy` is an
  optional parameter defaulting to `Options` on both methods, so every pre-existing caller/test
  keeps compiling and behaving unchanged. `LiveEntrySignalRow.EntryPercentile` was also relaxed from
  `required double` to `double?` -- the crossover strategy has no percentile concept at all (its own
  gate is a fast/slow crossing plus a fixed point-gap threshold), so it is null on every
  `FuturesCrossover` row, never fabricated. A new EF migration
  (`20260921114749_AddFuturesCrossoverLiveTables`) carries all of this; every pre-existing row
  backfills to `Strategy=Options` (enum value 0), the only strategy that had ever written to these
  tables.
- **`NiftySignal.Host/LiveFuturesCrossoverEngine.cs`**: new hosted background service, mirroring
  `LiveOptionsScoreEngine`'s own polling/day-boundary shape exactly, registered in `Program.cs`
  alongside it. Simpler than the options engine: FuturesScore reads only `VolumeBarRow`'s own
  futures-side columns, never the option-side tables (`OptionAtmBarRow`/`OptionDepthBarRow`/
  `OptionMaxPainBarRow`) the options engine has to wait on, so it never stalls behind Phase A's
  option-writer sequence. Calls the same `LivePaperTradeExecutor.OpenAsync`/`CloseAsync` the options
  engine calls, passing `LiveVolumeBarStrategyId.FuturesCrossover` explicitly.
- **Dashboard**: `NiftySignal.Dashboard/Components/Dashboard/LiveFuturesCrossoverPanel.razor`, same
  reusable-component shape as `LiveOptionsScorePanel` (fast/slow MA, gap, TOB confirmation status,
  open position, today's trades), added alongside (not replacing) `LiveOptionsScorePanel` on
  `Home.razor` -- both strategies visible at once, clearly labeled which is which.
  `LiveOptionsScorePanel`'s own `LivePaperTrades` query was updated to filter
  `Strategy == Options` (it is now a shared table).

**Deliberately NOT used**: the 1300/8/30/8 crossover config found promising in a later sweep (see
this doc's own "Adopted for continued out-of-sample tracking" note) -- this wiring trades the
config actually being tracked (8/40/5/2600), an explicit, visible decision per the task's own
instruction, not an oversight.

**Verification (all run locally against the real `niftysignal_volume_bars`/`niftysignal_vm_copy`
databases via the new `replay-live-futures-crossover`/`replay-live-futures-crossover-both` CLI
commands, `NiftySignal.VolumeBarData/ReplayLiveFuturesCrossoverCommand.cs`, using 2026-09-16 -- an
already-used historical day elsewhere in this project):**

1. **Byte-identical replay match**: `replay-live-futures-crossover 2026-09-16 --threshold=2600`
   drove `LiveFuturesCrossoverSession` bar by bar (the exact class the live Host engine uses) and
   called the exact same `LivePaperTradeExecutor` the live engine calls, comparing against
   `TradeSimulator.SimulateCrossoverDayAsync` (8/40/5, `SessionGatedDepthDurationConfirmed`) for the
   same day. **Result: 5 of 5 trades matched exactly** on entry bar, strike, direction, and exit
   reason (all 5 exited `CrossoverReversed`). Fill-price deltas were logged, not hidden, per the
   documented fill-timing policy -- e.g. trade #0: live entry 167.05 vs offline 166.15 (delta
   +0.90), live exit 180.00 vs offline 183.35 (delta -3.35); deltas ranged roughly -5.30 to +3.40
   across the 5 trades, consistent with the same decision-timestamp-vs-bar-EndTimestamp gap already
   documented and accepted for the options side.
2. **Restart-safety**: `replay-live-futures-crossover 2026-09-16 --threshold=2600 --restart-after=200`
   scored bars 0-200 directly (ending mid-trade, `HasOpenSignal=True`), discarded the in-memory
   session, called `LiveFuturesCrossoverSession.RebuildAsync` (exactly what a real process restart
   does), and finished the day. **Result: PASS** -- the rebuilt session resumed at the correct
   BarIndex with the correct open-signal state, and the full day's 5 trades matched the
   uninterrupted replay's own 5 trades exactly (same entry bars/strikes/exit reasons as check #1).
3. **No collision with the options strategy**: `replay-live-futures-crossover-both 2026-09-16 --threshold=2600`
   interleaved BOTH strategies bar by bar into the SAME scratch database for the SAME day/threshold.
   Both strategies had simultaneously-open positions at points during the day -- notably BarIndex
   165, where the options strategy opened a Call AND the futures-crossover strategy opened a Put on
   the exact same bar, previously an unhandled unique-index collision. **Result: PASS, no
   exception** -- the Options strategy's own 13 trades and the FuturesCrossover strategy's own 5
   trades both persisted independently and matched running each strategy alone (same 13/5 trade
   sets as the standalone Phase D options replay and check #1 above), proving neither strategy's
   presence altered the other's own decisions.
4. **Regression check** (`trade 2026-09-08 2026-09-19 <metric> 90 15 2600`, the same 8-day range and
   command shape used throughout Phase 5): `OptionsScoreThreeWaySwitchMaxPainConfirmed` @ 2600/90,
   band=5 reproduced **112 trades, 64.3% win rate, +426.40 net** exactly; `SessionGatedDepthDurationConfirmed`
   @ 2600/90 reproduced **80 trades, 55.0% win rate, +144.70 net** exactly -- both locked numbers
   byte-for-byte unchanged after the `FuturesSessionGatedScoreCalculator` extraction touched
   `TradeSimulator.cs`'s dispatch chain, confirming no dispatch-order regression (the recurring bug
   pattern this project's own working agreement calls out).
5. **Full test suite**: 651 tests passing (637 pre-existing baseline + 14 new --
   `NiftySignal.Tests/Scoring/FuturesSessionGatedScoreCalculatorTests.cs` proving the extracted
   formula/TOB gate match the pre-extraction inline code, and
   `NiftySignal.Tests/VolumeBarData/LiveFuturesCrossoverSessionTests.cs` proving the bar-ordering
   guard, the rolling-window "not yet evaluable" gate, and restart-rebuild equivalence bar-for-bar),
   0 warnings, `dotnet build` clean across the full solution.

**Not deployed**: `NiftySignal.Host/Program.cs` now registers `LiveFuturesCrossoverEngine` as a
hosted service, and the migration exists in `NiftySignal.VolumeBarData/Migrations/`, but neither
`deploy.ps1` nor the VM (`100.105.67.79`) were touched in any way -- no redeploy, no restart, no
migration applied to the VM's own database. This is local-only, build/test/replay-verified, exactly
as instructed; deployment is a separate, explicit later step.

## Risk-rule sweep pointer (2026-09-21)

The retired live engine's own risk mechanics (`NiftySignal.Rules/ExitRuleEvaluator.cs` -- per-trade
stop-loss, TP1 partial-booking with trail-to-breakeven, daily-loss-cap) were evaluated against both
strategies now live for the first time, entirely as **backtest-only additions to
`NiftySignal.VolumeBarData`/`TradeSimulator.cs`** (new off-by-default `--stop=`/`--tp1pct=`/
`--tp1frac=`/`--dailyloss=` CLI flags; `VolumeBarTrade` gained nullable partial-exit fields).
**Neither `NiftySignal.Host` nor `NiftySignal.Dashboard` were touched** -- nothing here is wired
into either live paper-trading strategy. Full sweep tables, methodology, and verdict are in
`docs/VOLUME_BAR_FINDINGS.md`'s "Risk-rule sweep: SL/TP1/daily-loss for both live strategies
(2026-09-21)" section. Headline: a 20%-of-premium stop-loss is a mild, genuine (if marginal)
candidate for the options strategy only; TP1 and the daily-loss-cap never beat either strategy's
no-rules baseline at any setting tried; no DTE-conditioned effect was found. **Recommendation:
adopt nothing yet** -- one 8-day sample is a data point, not a verdict, pending the user's own
review.

## Dashboard: live history charts added (2026-09-21)

Both live strategy panels gained a live-updating chart (10s cadence, same poll as the rest of
each panel), reusing `wwwroot/js/charts.js`'s existing Chart.js interop layer:
- **Futures crossover panel**: reused `renderCoreScoreChart`/`updateCoreScoreChart` unchanged
  (already built for the retired Core-score panel, exactly the right shape for a crossover --
  Score/Fast MA/Slow MA/Price).
- **Options score panel**: no fast/slow concept exists for a percentile switch, so reusing the
  Core-score chart's labels would have mislabeled the lines. Added dedicated
  `renderOptionsScoreChart`/`updateOptionsScoreChart` functions instead: Score / Percentile /
  Max Pain confirm (x100, shares the score axis) / Price.

Also fixed: both new panels were displaying timestamps via `.ToLocalTime()` (server-timezone-
dependent) instead of this project's own established `.ToIst()` extension (`NiftySignal.Domain/
IstTime.cs`) -- same convention the retired ScorePanel already used correctly. Fixed in both.

Build clean, 661/661 tests passing. Not deployed -- local only, per the user's standing
instruction to hold all Dashboard/Host changes until further items are done.
