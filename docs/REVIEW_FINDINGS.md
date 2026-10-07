# External Review Tracker

**Purpose:** a running ledger of findings from outside review (friends, third-party audits, AI
review) that were independently checked against the actual current code — not the review's
description of the code — and confirmed to be real, current, and worth fixing. Anything a review
flagged that turned out to already be fixed, or to be a claim about signal *predictive validity*
that can't be resolved without the backtester (see below), is recorded here too, but separately,
so effort doesn't get spent twice or spent on the wrong thing.

Numbering continues the existing `PENDING (audit finding FNN)` / `DEFERRED (audit finding FNN)`
source-comment convention (see `[[feedback_pending_items_in_source]]`-style discipline already
used throughout this codebase) — F61 is the next free number as of 2026-09-11 (F46-F60 used
below). Findings that are already fixed get their `audit finding FNN` marker inline in source at
the point of the fix, same as every other batch this project has done; this file stays the
durable index of what F-number maps to what, and why.

---

## 2026-09-10 — Friend's review (the "not going to implement until we get value out of it" pass)

A friend reviewed a static archive of this repo (no `.NET SDK`, no live DB — code-level reading
only) and produced a 60-point review. It's sharp in the places that matter most (the review's own
framing — correlated features combined into one opaque score, then treated as a probability — is
correct and is the real headline issue), but roughly a third of its "critical" findings turned out
to be either already fixed in this repo (the archive predates recent work) or unverifiable without
the backtester the review itself says is missing. Each item below was checked directly against
current source before being kept or dropped.

### Fixed (2026-09-10)

- **F46 — Z-score computed against a window that already includes the current observation.**
  Fixed: every `_xWindow.ComputeZScore(raw)` call in `LiveFeatureEngine.ComputeCadence` now runs
  *before* the corresponding `_xWindow.Add(now, raw)`, at all ~14 call sites (the z is captured
  into a local, `Add` happens after, same pattern the class already used for `ivSkewRaw` around
  `ResetSamples()`). Regression test:
  `LiveFeatureEngineTests.ComputeCadence_FuturesBasisZ_ExcludesCurrentObservationFromItsOwnWindow`
  — replays the same basis sequence into an independent `WelfordRollingWindow` and proves the
  engine's returned `FuturesBasisZ` matches compute-then-add (correct) and diverges from
  add-then-compute (the bug), for a moderate, unclamped outlier chosen specifically so the two
  orderings don't just both saturate the ±3 clamp identically. One existing test
  (`ComputeCadence_SmoothsCompositeRaw_AcrossTheLastSeveralCadences`) had its strict `<` loosened
  to `<=`: fixing self-inclusion removed a pre-existing dampening bias from the instant reading,
  which moved that specific deterministic scenario's smoothed/instant ratio to exactly the old
  boundary rather than under it — still the same 2x damping factor, not a weakened assertion.

- **F47 — Zero-weight components still gate composite warm-up.**
  Fixed: `CompositeScoreCalculator.TryComputeRaw` now treats a component as optional if its
  weight is `0.0`, in addition to the existing name-based `VixChange` exception (VIX has a real
  0.05 weight but its own separate can-be-absent-for-the-day reason, so it still needs a
  name-based exception; everything else that was previously name-listed — GammaExposure,
  VolumePcr, SpreadRatio, VannaExposure, CharmExposure, CvdProxy, StraddleRichness — is weight
  0.0 today and is now covered by the weight rule instead). **Correction to this file's earlier
  note:** F47 does **not** resolve F14 as a side effect — that was wrong when first written here.
  F14 is about `DepthImbalance`, which carries a real, nonzero weight (0.1625); a weight-driven
  rule leaves it exactly as required as before. F14 remains open, a separate and larger
  risk-behavior decision (should a missing depth book silently contribute 0 like VixChange does,
  or keep blocking the composite?), not something this fix touches. Regression test:
  `CompositeScoreCalculatorTests.Calculate_StillWarmsUp_WhenPriceMomentumZIsNull_BecauseItsWeightIsZero`.
  **Follow-on bug caught live** while validating this same fix's VM deploy (Host log,
  10-Sep): `MarketDataIngestionWorker`'s own warm-up-blocked diagnostic log
  (`"Composite score not warmed up -- blocked by: ..."`, audit finding F10) had its own
  hardcoded, independently-maintained six-name list — exactly the kind of duplication this
  file's F48 entry already warns about elsewhere — and kept naming `PriceMomentumZ` as a
  blocker after this fix made it optional, actively misleading anyone reading the log about
  why warm-up was actually stalled. Fixed by adding
  `CompositeScoreCalculator.DescribeMissingRequiredComponents(inputs, weights)` (single source
  of truth, reused by both the composite's own gating and this diagnostic) and having the
  worker's log call it instead of duplicating the rule. Regression tests:
  `CompositeScoreCalculatorTests.DescribeMissingRequiredComponents_ExcludesPriceMomentum_EvenWhenItIsNull`,
  `..._IsEmpty_WhenFullyWarm`.

- **F48 — Capital gate is a position count, not a capital sum.**
  Fixed, using the "after strike selection" design (your call): `LiveTradingEngine.EvaluateEntryAsync`
  now computes `committedCapital = Σ(open positions' EntryPrice × Quantity) + (this candidate's
  real ask+slippage fill price × quantity)` after `StrikeSelector` has picked a real candidate and
  `PaperTradeSimulator.FillEntry` has produced a real fill price, and rejects the entry if that
  total would exceed `Capital.Total` — `MaxConcurrentPositions` stays as an additional, unchanged
  gate, not replaced. `EvaluateCadenceAsync` now passes the post-exit still-open `PaperTrade` list
  into `EvaluateEntryAsync` (filtered in memory from the exit loop's already-tracked entities, no
  extra DB round trip) instead of a bare count, so the entry side can sum real committed premium.
  Regression test:
  `LiveTradingEngineTests.EvaluateCadenceAsync_NoEntry_WhenCommittedCapitalWouldExceedTotal_EvenUnderMaxConcurrentPositions`
  — two open positions sized so `MaxConcurrentPositions` (3) is deliberately *not* tripped (only
  2 of 3 slots used) but a third entry's real capital sum would exceed `Capital.Total`, isolating
  the new capital-sum gate as the one actually doing the rejecting.

- **F49 — `MarketDataIngestionWorker.ExecuteAsync` had no daily restart loop.** Fixed:
  `ExecuteAsync` now wraps a `while (!stoppingToken.IsCancellationRequested)` day loop around
  `WaitForMarketHoursAsync` + the extracted `RunTradingSessionAsync` (everything the old
  `ExecuteAsync` used to do inline, from FlatTrade session validation through market close).
  `RunTradingSessionAsync` returns `false` only for the one hard-stop condition (no valid
  FlatTrade session — deliberately still ends the service rather than silently retrying next day,
  per plan 4.3) and `true` otherwise, so the day loop keeps going after every normal session.
  Two things that must **not** be per-day were moved out, both start-once-in-`ExecuteAsync`:
  `dashboardPush.StartAsync` (its SignalR connection persists across the overnight gap via
  `WithAutomaticReconnect`; calling `StartAsync` again on an already-connected `HubConnection`
  throws) and `RunPaperTradeSummaryLoopAsync` (it deliberately runs on the full-lifetime token so
  it keeps sending summaries after a day's other loops wind down at market close — **catching this
  during implementation mattered**: the original single-day code already awaited this loop last,
  which never actually completes during normal operation, so naively awaiting it *inside* the new
  per-day method would have silently blocked the day loop from ever reaching day 2, reintroducing
  the exact bug this fix exists to remove). `RunPaperTradeSummaryLoopAsync` also gained the same
  per-iteration try/catch every other loop already had (audit finding F34) — it now runs unattended
  for the whole process lifetime rather than being awaited within minutes of starting, so an
  uncaught fault there needed the same "log and continue" resilience, not silence until final
  shutdown. `_warmUpBlockedLogged` is now reset at the start of each session so a prior day's
  leftover `true` can't suppress today's first genuine warm-up-blocked log.
  **Not covered by an automated test** — this class has no existing test harness (it's
  WebSocket/timer/live-DI-coupled with nothing faked in this codebase today) and building one
  proportionate to this fix alone was judged out of scope; verified instead by careful manual
  trace-through of every code path (documented above) and a clean build. **Recommend watching the
  actual day-1-to-day-2 transition on the VM once it happens naturally**, to confirm the real
  Windows Service picks up day 2 without a manual restart.

### Already fixed — do not re-do

- **Risk-free rate duplication** (review's #52) — fixed 2026-09-09 (F21/F19 work). Both
  `LiveFeatureEngine` and `LiveDataService` now read one `PricingOptions.RiskFreeRate` via
  `IOptionsMonitor`, configured once in each service's `appsettings.json`. Verified: no remaining
  hardcoded `0.065` constant feeding either engine independently.
- **Rules not hot-reloadable** (review's #51) — also stale. `LiveTradingEngine._config` is a
  property reading `rulesetOptions.Current` (an `IValidatedOptions<RulesetConfig>` backed by
  `IOptionsMonitor` + validation) fresh on every access, not a captured `LiveRulesetConfig.Default()`
  snapshot. The review's description matches an earlier state of this file that no longer exists.
- **GEX terminology** (review's #12) — already tracked as exactly this concern under audit finding
  F29 (2026-09-08), with the same conclusion the review reaches independently: it's a call-put
  gamma tilt, not dealer GEX, zero live weight, rename before ever giving it a nonzero weight. No
  new action needed beyond what F29 already says.

### Documentation drift — fixed (2026-09-10)

- **`ARCHITECTURE.md` line 19** claimed `NiftySignal.Rules` was "Rule engine (NCalc), ruleset
  config, hot-reload + validation." Confirmed zero references to NCalc anywhere in the codebase
  before fixing — the rule engine is hand-written C# (`EntryRuleEvaluator`/`ExitRuleEvaluator`),
  never NCalc. Corrected to describe what's actually there and point at this file.
- **`ARCHITECTURE.md` line 24** (`NiftySignal.Backtest`) had the same problem, found while fixing
  the line above: it claimed the backtest project was "running the same pipeline as live," which
  is exactly what F32 (below) doesn't exist yet. Corrected to describe what's actually built
  (`BacktestTickSource`, `PerformanceReportBuilder`, both tested) and point at F32's scope.

### Security — action item, not a code fix

Git history itself is clean: `git log --all --full-history` across every branch for
`appsettings.Local.json` (Host, Dashboard, and the `deploy`/`deploy-vm`/`artifacts` staging copies)
returns nothing — these files were never committed. But that doesn't clear the review's concern:
if the archive handed to the reviewer was a plain folder/zip copy rather than a git-clean export,
the untracked `Local.json` files (which hold the live FlatTrade key/secret, DB password, and
dashboard auth hash) would have been included regardless of git being clean. **Needs a direct
answer, not a code check: how was that archive produced?** If there's any chance it included the
untracked files, rotate the FlatTrade API key/secret and DB password — cheap insurance, independent
of everything else in this file.

### Correctly flagged as unresolved — deferred to the backtester, not fixed by guessing

Everything about *sign and predictive validity* — PCR direction, IV skew sign, depth imbalance's
directional meaning, futures basis usefulness, dealer-GEX assumptions, OI-buildup's premium-vs-spot
definition — the review is right that none of these should be asserted from textbook intuition, and
several are already open `PENDING` items in source saying exactly that. The review's own suggested
method (correlate each raw metric against forward returns before trusting a sign) is correct, but
it requires the tick-to-P&L backtester below. Resist the temptation to hand-fix any of these signs
in the meantime — see the backtester scope for why guessing now just means re-deriving later against
real data anyway.

---

## 2026-09-10 — Live-caught: OI compared against the wrong window

While looking at why the Dashboard's Sized OI Flow Ratio tile kept flipping from "warmed up" to
"pending" on consecutive refreshes, the user correctly identified the root cause from first
principles (independent of anything in the friend's review): NSE/the broker only refresh OI
every ~3 minutes, not every 15 seconds.

### Fixed (2026-09-10)

- **F50 — OI compared against the previous 15s cadence instead of a window long enough to span
  a real update.** Confirmed by reading `FlatTradeFeedState.ApplyDelta` ([FlatTradeFeedState.cs:64](../NiftySignal.Ingestion/FlatTrade/FlatTradeFeedState.cs:64)):
  `if (msg.OpenInterest is not null) OpenInterest = ParseLong(msg.OpenInterest);` — between real
  broker OI prints, the same value is carried forward unchanged on every tick, so
  `ComputeOiBuildupNet`/`ComputeRatioSizedOiFlowRaw` comparing current OI against
  `_previousCadence` (15s ago) were mostly comparing an unchanged value against itself, with the
  full ~3 minutes' worth of change landing in one lumpy spike on whichever cadence a real print
  happened to fall in. **This is the exact same root cause already found and fixed once in this
  codebase**, for a different (display-only) case — `LiveDataService.cs:648`'s own comment on
  the Dashboard's "OI Change %" panel: *"comparing against the immediately-prior poll (5s ago)
  was structurally almost always a no-op."* Worst here for `ComputeOiBuildupNet`, whose weight
  (0.3125) is the single largest in the whole composite.

  Fixed with a new `OiLookbackWindow` class (`NiftySignal.Features`) — per-token, time-windowed
  "what was OI approximately `FeatureWindowLengths.OiComparisonWindow` (4 minutes, a provisional
  constant comfortably longer than the confirmed ~3-minute refresh) ago," used by both methods in
  place of `_previousCadence` for the OI-specific comparison only (spot price comparison for
  classification direction is untouched — see below). Recorded once per cadence, for every
  tracked option, right where `_previousCadence` itself gets updated at the end of
  `ComputeCadence`, so both methods' `Lookback` calls earlier in the same cadence see state
  strictly before that cadence's own recording.

  **Known, documented, not-yet-fixed follow-on gaps** (deliberately left out of this fix's scope,
  each flagged in source rather than silently left):
  - `spotPriceChange` (used to classify each strike's OI change as buildup/unwinding) still
    compares against the previous 15s cadence, not the same ~4-minute window `oiChange` now
    uses — spot ticks continuously so a 15s-old direction is usually still representative,
    unlike OI's genuinely lumpy refresh, but the two are no longer measuring the same span.
  - `_oiLookback` is not restart-seeded (unlike most other rolling state in `LiveFeatureEngine`)
    — every Host restart costs up to `OiComparisonWindow` of `ComputeOiBuildupNet` reading
    exactly 0 rather than a real value. Raw per-token OI history isn't in `ScoreSnapshot` to
    replay from; would need new seeding plumbing off `StrikeSnapshot`'s own persisted per-strike
    OI if this turns out to matter in practice.
  - `BuildStrikeSnapshots`' own per-strike `_previousOpenInterestByToken` (feeding the
    diagnostic, unweighted `StrikeSnapshot.OiChangeDelta`/`OiBuildup` columns) has the identical
    15s-comparison structure and was **not** touched by this fix — display/diagnostic-only, not
    fed into any score, lower priority than the two weighted metrics above.

  Regression tests: `NiftySignal.Tests/Features/OiLookbackWindowTests.cs` (new, 7 tests covering
  the class in isolation) plus updates to
  `LiveFeatureEngineTests.ComputeCadence_OiBuildupNet_WeighsEachStrikeByOiChangeMagnitude_NotAFlatVote`
  and `..._PopulatesAllFiveRatioMetrics_AndWarmsUp_OnceThereIsPriorStateToDeltaAgainst`, both of
  which now tick every 15s across the full comparison window (matching real cadence spacing --
  a single big jump trips `ComputeOiBuildupNet`'s own `MaxCadenceGapForOiBuildup` feed-outage
  guard) instead of one 15s-cadence jump.

---

## 2026-09-10 — Live-caught: dashboard bar charts and ratio-panel sign confusion

User reported three things while looking at the live Ratio Composite Score panel: (1) most
individual metrics showed positive `s`, yet the overall score was negative; (2) bar length
didn't visually track a component's magnitude, in any panel; (3) the panel was meant to average
each metric over 10-15 minutes so it wouldn't swing sign every cadence, and evidently didn't.
All three were real, and (1)/(3) turned out to be the same root cause.

### Fixed (2026-09-10)

- **F52 — Bar length not proportional to magnitude, worst/invisible for negative values.**
  `ScorePanel.razor.css` and `RatioScorePanel.razor.css`'s `.component-bar.bear` rule carried a
  redundant `transform: translateX(-100%);` on top of the inline `right:50%; width:X%` the
  Razor code already emits. `right:50%; width:X%` alone already positions a box correctly —
  right edge pinned at the center, extending left by `X%` — so the box already renders a
  correctly-proportional, correctly-anchored bar with no transform needed at all. Adding
  `translateX(-100%)` shifts that already-correct box an *additional* 100% of its own width
  further left: for a half-scale bar (`X=50`) this pushes it half a track-width past the
  container's left edge; for a near-full-scale bar (`X≈100`) the entire box lands off-screen,
  clipped invisible by the track's `overflow:hidden`. Bull bars (`left:50%`, no transform) were
  never affected — which is exactly why the bug read as "negative values don't show a bar" in
  particular, not "bars are broken." Fix: delete the `transform` line from both files; the
  existing `right:50%` positioning was already correct on its own. Verified visually against a
  seeded snapshot with strongly negative z/s values in both panels — bars now render fully
  visible and length-proportional in both directions.

- **F51 — Ratio panel's individual metrics were single-cadence instant reads shown next to an
  already-smoothed score.** `RatioCompositeScore` (the number at the top of the panel) is the
  combined value averaged over `RatioCompositeSmoothingCadences` (~12 min) — but the five
  component rows below it (`Ratio*Raw`, hence the displayed `s=` values and their bars) were
  each a *single 15-second cadence's* raw read, re-clipped fresh every poll. A metric could
  genuinely flip sign from one 15s cadence to the next (order flow is lumpy at that granularity
  — see F50 above for OI specifically), while the headline score, built from many cadences'
  worth of history, stayed comparatively stable — reading as contradictory even though both
  numbers were individually correct for what they represented. Matches what the user recalled
  deciding during the original build: each metric should itself be smoothed over ~10-15 minutes,
  not just the combined total.

  Fixed with the same FIFO-average mechanism the combined score already uses
  (`RatioCompositeSmoothingCadences`, ~12 min), applied one level earlier — each of the five raw
  metrics now gets its own smoothing FIFO, fed every cadence, averaged before being clipped into
  `RatioComponentInputs` and before being persisted to `ScoreSnapshot.Ratio*Raw`. A quiet bar
  (below a metric's own liquidity floor) is skipped, not zero-filled, so it doesn't drag the
  average down artificially. `SeedHistory` replays each persisted (now-smoothed) `Ratio*Raw`
  value back into its own FIFO on restart (a known, documented approximation, same spirit as
  `_previousCadence`'s own accepted restart limitations — see F50).

  **Follow-up, same day, on direct user instruction:** the first version of this fix also kept
  the pre-existing combined-level FIFO on top (smoothing the already-smoothed combination a
  second time) as a deliberately conservative choice. The user pointed out that once every input
  is already a ~12-minute average, smoothing their combination again adds lag for no benefit —
  correct, and simpler besides. Retired the combined-level FIFO (`_ratioCompositeRawHistory`)
  entirely: `RatioCompositeScoreRaw` is now set identically to `RatioCompositeScoreRawInstant`
  every cadence, with no separate history of its own. Both columns are kept (removing one would
  need a migration and touch every reader) rather than collapsed into one.

  Regression tests:
  `LiveFeatureEngineTests.RatioNotionalVolumeRaw_IsTheSmoothedAverage_NotJustThisCadencesInstantValue`
  (two cadences with deliberately different instant ratios, 20 then 0, prove the persisted value
  is their average, 10, not either instant alone) and
  `SeedHistory_ReplaysEachRatioMetricsOwnFifo_SoARestartDoesNotResetItsSmoothing` (replaces the
  old combined-FIFO seed test, which no longer tests anything real now that FIFO is gone —
  proves the *per-metric* FIFOs are what actually gets restored on restart).

---

## F32 — The backtest runner (scoped 2026-09-10, built 2026-09-11)

**Why this is the one thing that unblocks everything else in the review:** almost every
metric-level question above (is PCR's sign right? is IV skew's sign right? does depth imbalance
actually predict anything?) can only be answered by running the strategy against history and
measuring forward returns — not by reading the code more carefully. `BacktestTickSource` and
`PerformanceReportBuilder` were built and tested for exactly this, months ago, and nothing has ever
wired them together. This section is a scope, not a build — no code changes were made producing it.

### What already exists and is reusable as-is (verified by reading each one, not assumed)

- **`BacktestTickSource`** (`NiftySignal.Backtest`) — replays `Ticks` rows in timestamp order for a
  date range, already implements `ITickSource` (the same interface `FlatTradeTickSource` implements
  live), already tested. Nothing to build here.
- **The tick-driven clock pattern already proven in `NiftySignal.ScoreReplay/Program.cs`** — this
  one-off tool (built 2026-09-09 to derive Batch 4's thresholds) already does the hard part: replay
  ticks up to a boundary, call `engine.OnTick` for everything at or before it, then call
  `engine.Sample`/`engine.ComputeCadence` at 3s/15s intervals keyed off *tick timestamps*, not a
  live `PeriodicTimer`. This is precisely the mechanism a full backtest needs and it already exists,
  already runs against real recorded ticks, already produces real `ScoreSnapshot`-shaped output.
  The backtester doesn't need to invent this; it needs to extend it.
- **`LiveFeatureEngine`, `LiveTradingEngine`, `EntryRuleEvaluator`, `ExitRuleEvaluator`,
  `StrikeSelector`, `PaperTradeSimulator`** — checked every one of these for a hidden
  `DateTimeOffset.UtcNow` call. Found **zero**. `LiveTradingEngine.EvaluateCadenceAsync` takes `now`
  from `snapshot.ComputedAt`, not the wall clock; `StrikeSelector.SelectBestCandidate` and
  `PaperTradeSimulator.FillEntry/FillExit` are pure functions with no clock or DB dependency at all.
  **The entire decision path is already clock-agnostic.** The only wall-clock-coupled class in the
  whole live path is `MarketDataIngestionWorker` itself — which a backtest was never going to reuse
  anyway (it's real-feed/real-timer orchestration, not decision logic).
- **`PaperTradeSimulator`** already does the right thing by default — entry fills at ask + slippage,
  exit at bid − slippage, never at LTP. This directly satisfies the review's #47 concern about
  unrealistic fills; it just isn't being called from anywhere outside its own unit tests yet.
- **`PerformanceReportBuilder`** already computes win rate, profit factor, max drawdown (both cash
  and % of equity peak, with the % explicitly documented as peak-relative not capital-relative —
  matches the review's #43 concern; the fix there is adding a capital-relative % alongside the
  existing one, not correcting a bug), and an honestly-named `SharpeRatio` that the doc comment
  already calls "not a finance-textbook Sharpe." Reusable unchanged.
- **`LiveTradingEngine`'s own test suite already runs it against EF Core's `UseInMemoryDatabase`**,
  not real Postgres (`LiveTradingEngineTests.cs`). This proves the whole entry/exit/DB-backed path
  has no Postgres-specific dependency — which matters a lot for the design decision below.

### What's actually missing

**One coordinator class** — a `BacktestRunner` that, for a given date range and instrument universe:

1. Resolves the day's instrument set the same way `MarketDataIngestionWorker.ResolveInstrumentsAsync`
   does live, but from the `Instruments` table's `AsOfDate` rows instead of a fresh FlatTrade session
   call (the data's already there — `ScoreReplay` already does this exact lookup).
2. Constructs one `LiveFeatureEngine` per day (same as live, same as `ScoreReplay`).
3. Drives ticks through it using `ScoreReplay`'s already-proven boundary/Sample/ComputeCadence loop.
4. **The new part `ScoreReplay` doesn't do:** on every non-null `ScoreSnapshot`, calls
   `LiveTradingEngine.EvaluateCadenceAsync` — the exact same method the live system calls — against
   a **fresh, isolated `DbContext`** for this run.
5. At the end of the date range, runs `PerformanceReportBuilder.Build` over whatever `PaperTrade`
   rows landed in that isolated context, and returns/prints the report.

### The one real design decision this needs before any code gets written

**Where does the backtest's isolated `DbContext` point?** Two options, both viable, with different
tradeoffs:

- **A — EF Core `UseInMemoryDatabase`, one fresh instance per run.** Already proven safe for this
  exact code path (`LiveTradingEngineTests` already runs the real engine against it). Zero risk of
  ever touching the live `PaperTrades` table — a backtest literally cannot corrupt live risk state
  because it's a different provider entirely, not just a different connection string. Fast, no
  Postgres schema management, trivial to parallelize (one InMemory instance per backtested day or
  per parameter sweep run). Downside: InMemory's LINQ provider is more permissive than Postgres's —
  a query that works against InMemory in a backtest but relies on something Npgsql-specific would
  pass silently and only break live. Low risk here specifically, since `LiveTradingEngineTests`
  already exercises the same queries this way, but worth having the actual production Postgres
  provider run the full suite once as a sanity check before trusting backtest output at scale.
- **B — a real, separate Postgres database** (matches `BacktestTickSource`'s own doc comment quoting
  plan section 1.5: *"backtest mode feeds it from the database replaying stored ticks"*, and how
  `NiftySignal.ScoreReplay` already points at `niftysignal_vm_copy` for its own read-only replay).
  Higher fidelity (byte-for-byte the same provider as live), but needs the schema migrated and reset
  between runs, and is slower per run.

**Decided 2026-09-11: option A.** User confirmed EF `InMemoryDatabase` for the runner's own trial
`ScoreSnapshot`/`PaperTrade` writes. Real ticks/instruments are still read from real Postgres
(read-only) — the two were never meant to be the same DbContext, and aren't. Option B (a real,
separate Postgres schema) stays available as a future periodic cross-check per the recommendation
above, not built now.

**Built 2026-09-11** — `BacktestRunner` (`NiftySignal.Backtest/BacktestRunner.cs`) implements
exactly the 5 steps above, one `LiveFeatureEngine` per trading day (fresh instrument resolution,
matching live's own daily reset) driving `LiveTradingEngine.EvaluateCadenceAsync` per cadence
against the isolated InMemory store, with `PerformanceReportBuilder.Build` over the resulting
trades. `NiftySignal.Backtest` is now a runnable console tool
(`NiftySignal.Backtest/Program.cs`) that loads the **real, currently-live** `RulesetConfig` and
`ScoreWeights` from `NiftySignal.Host`'s own appsettings (not test defaults), so a run validates
the rules actually in production, not a fixture. Tested in
`NiftySignal.Tests/Backtest/BacktestRunnerTests.cs` (day-skip on missing instruments, isolation of
trial writes from the real read-only DB, and an end-to-end run producing a real `PaperTrade` from
a genuinely warmed-up composite score). All 359 tests green.

Also corrected `BacktestTickSource.cs`'s 2026-09-08 doc comment, which claimed
`LiveTradingEngine` hardcoded `DateTimeOffset.UtcNow` — false; this section's own "what already
exists" analysis above had already found zero `UtcNow` calls there, and a full re-read confirms
`EvaluateCadenceAsync` derives `now` entirely from `snapshot.ComputedAt`. That earlier comment
should not have been trusted at face value; this section's own analysis was right the first time.

**Not yet done, worth doing before trusting output at scale** (per this section's own "Rough
effort shape" above): the backtest-vs-live parity/sanity check — comparing backtest-computed
`ScoreSnapshot`s against the real ones already persisted live for the same historical days. Real
4-day tick data now exists (≥98% coverage) to actually run this comparison.

### Explicitly out of scope for a first version (matches the review's own restraint)

- **Execution latency modeling** (review's #47's "defined execution latency model"). A first version
  fills at the exact tick-timestamp the signal fires, using the same quote data the signal itself
  was computed from — this is *not* lookahead (no future information is used), but it is an
  optimistic zero-latency fill assumption. Worth adding as a configurable delay (e.g., fill using
  the first quote ≥ signal time + N seconds) once the zero-latency version's results are understood,
  not before — a latency model tuned before the base case is validated is a parameter chosen with no
  data behind it, exactly the failure mode this whole effort exists to avoid.
- **Multi-parameter sweeps / walk-forward validation** — valuable eventually (this is what actually
  answers the review's sign/weight questions systematically), but it's an outer loop around a single
  `BacktestRunner` call, not part of scoping the runner itself.
- **Re-deriving PCR/IV-skew/OI-buildup signs** — explicitly deferred until the runner exists and has
  been run, per the whole point of this section.

### Rough effort shape (not a calendar commitment)

The coordinator itself (`BacktestRunner`, wiring steps 1-5 above) is genuinely the smallest piece of
this — every dependency it calls already exists and is tested. The real cost is elsewhere:
validating that backtest-mode output actually matches what live would have produced for the same
historical ticks (a sanity/parity check, ideally by comparing backtest-computed `ScoreSnapshot`s
against the real ones already persisted for the same historical days — those already exist in the
DB and were computed live, giving a free ground truth to diff against), and building whatever
reporting layer turns a `PerformanceReport` plus per-metric forward-return correlation into
something a human can actually read and trust before touching a single sign or weight.

---

## 2026-09-11 — Live-observed ideas and questions (candidate hypotheses, none implemented yet)

User watched today's live session and brought a batch of observations, questions, and strategy
ideas. Per this project's own "long-term process" framing (this isn't a one-day/one-week
exercise) and the "explain a formula before changing it" principle, **none of these are
implemented** — they're recorded here as candidate hypotheses to test once `BacktestRunner` (F32,
now built) has real multi-session data behind it, not acted on from live-watching intuition alone.
Two direct questions below were answerable from the existing code right now and are marked
resolved rather than deferred.

**Resolved while researching this batch — call-side vs put-side average IV converging on the
Dashboard's Option Chain "Total" row is by design, not a bug.** `OptionChainPanel.razor`'s total
row shows the *mean* IV across the visible strikes per side (`FormatAverageIv`), not a sum — the
component's own comment explains why summing percentages is meaningless. Each leg's IV is solved
independently from its own market price
(`LiveDataService.BuildOptionChainAsync`: `ImpliedVolatilitySolver.Solve(instrument.OptionType,
tick.LastPrice, underlying, strike, ...)`), but `underlying` is the **same put-call-parity
synthetic forward for both sides** (`BuildSyntheticForwardByExpiry`) — this was a deliberate fix
(2026-09-07) for a real prior bug where an understated underlying (spot, with no cost-of-carry
adjustment) biased call IV up and put IV down by ~7-8 vol points, systematically, all day. Solving
both legs against the same parity-consistent forward is specifically what makes call and put IV at
a given strike converge to consistent values — averaged across a handful of near-ATM strikes,
that convergence is expected to look like "always about the same." If the two totals are ever
*exactly* bit-identical rather than just close, that would be worth a second look, but "close" is
the fix working as intended, not a defect.

- **F53 — Apply per-metric smoothing to the original (Z-score) composite's 14 components too,
  mirroring the ratio score's own fix.** User's live impression: the ratio score (per-metric
  FIFO-smoothed, audit finding F51) is behaving more reliably than the original composite, which
  still only smooths at the *combined* level (`CompositeSmoothingCadences = 12`, i.e. 3 minutes —
  see `LiveFeatureEngine.cs`). Candidate fix mirrors F51's actual mechanism: smooth each of the 14
  raw values individually before z-scoring/combining, not just the final combined raw. Needs a
  real explain-first pass before implementing (per CLAUDE.md) — several of the 14 already have
  very different natural update cadences (OI-driven ones update far less often than quote-driven
  ones; see `FeatureWindowLengths.cs`'s own per-metric window-length reasoning), so a uniform
  per-metric FIFO length may not transfer directly from the ratio score's five metrics. Backtest
  both versions against the same real days once F32 has enough data and compare.

- **F54 — `ComputeRatioSizedOiFlowRaw` (metric 2) weights by contract-count `|ΔOI|`, not
  notional value; metric 1 already solves this for volume.** User's question, checked against the
  actual code: is call/put weighting distorted when one side's strikes trade at meaningfully
  different absolute premium than the other's? For **metric 1** (`ComputeRatioNotionalVolumeRaw`),
  the answer is already "no" — it sums *notional* (price × volume) per side by construction,
  exactly the fix the user described. For **metric 2**, the answer is "yes, potentially" — it sums
  raw `|ΔOI|` in contracts per side (spot-classified via `OiBuildupClassifier`), so a call-side OI
  change at ₹200 premium and a put-side OI change of the same contract count at ₹80 premium are
  currently weighted identically even though very different rupee amounts of exposure actually
  changed hands. A notional-weighted variant (`|ΔOI| × premium` per side) is a real, testable
  alternative — not obviously correct either, since OI is a *position* change, not a *trade*, so
  "notional OI change" is a different economic quantity than notional volume (metric 1), not just
  metric 1's fix applied a second time. Needs its own explain-first pass and an A/B backtest
  against the current contract-weighted version, not a swap on intuition.
  See `NiftySignal.Host/LiveFeatureEngine.cs`'s `ComputeRatioSizedOiFlowRaw`.

- **F55 — Ratio score direction-switching is slow; consider MA/EMA crossover or a rolling-median
  long/short rule instead of raw-value sign.** User's live impression: the ratio score reads well
  overall but is sluggish to flip direction. Two candidate mechanisms, both unimplemented: (a) a
  moving-average crossover (fast MA/EMA of the ratio score crossing a slower one) instead of
  reading the smoothed value's raw sign; (b) a rolling median (session-so-far or a fixed lookback)
  as the long/short threshold instead of zero. Both are real alternatives to the current
  `tanh(raw_smoothed/k)` sign read and need to be tried against real data, not decided from feel —
  a crossover or median-relative rule changes entry *timing* in ways that only a backtest can
  actually evaluate (faster direction changes trade off against more whipsaws).

- **F56 — If a crossover-style signal is adopted (F55), gate entries on a fair-price check (VWAP
  of the target strike) before firing.** User's idea: don't enter blindly on the crossover signal
  alone — compute the target strike's VWAP and only enter when the current price is below it
  (better-than-average fill), with score-strength-based override rules for cases where waiting for
  a VWAP-favorable price would miss the move entirely. Depends on F55 being decided first (there's
  no "crossover moment" to gate without it) — sequencing matters here the same way the original
  Batch 1-4 plan's sequencing did.

- **F57 — Does the ratio score's behavior actually depend on days-to-expiry? Needs real analysis,
  not assumption, before acting on it.** User explicitly asked for this to be researched/simulated
  against real data rather than answered from intuition — correctly, per this project's own "sign
  is a hypothesis until tested" principle. Now that real multi-day tick data exists
  (`niftysignal_vm_copy`, F32), this is answerable: bucket already-computed `RatioXxxRaw`/
  `RatioCompositeScore` history (or values `BacktestRunner` reproduces) by trading-days-to-expiry
  and compare distributions/volatility/mean-reversion characteristics across buckets — the same
  kind of retrospective, evidence-gathering analysis `docs/REVERSAL_ANALYSIS.md` and
  `scripts/put-call-residual-analysis.sql` already established a pattern for in this project. Not
  run yet — needs its own pass, ideally once several more days of real data exist so DTE buckets
  aren't each built from a single session.

- **F58 — "Demand vs. supply exhaustion" idea: compare spot price change over a window against
  whether call/put prices reacted proportionally.** User's own idea, explicitly framed as
  something to backtest before trusting: track spot (or future) price plus a chosen call and put
  price over e.g. a 15-minute window, and check whether the options' price reaction was
  proportional to the underlying's move (via Delta/Gamma, i.e. the same
  Delta+Gamma+Theta-predicted-vs-actual residual decomposition `scripts/put-call-residual-analysis.sql`
  already uses) — a disproportionately weak or strong reaction might signal the move is running out
  of steam. Directly related to, but distinct from, the residual-autocorrelation thread that
  closed negative earlier this session (see the "Research thread" section of the backtest plan) —
  that closed thread tested same-bar residual mean-reversion; this idea tests whether the
  *magnitude* of the reaction (not its reversion) carries information about trend exhaustion. Worth
  its own entry in `docs/REVERSAL_ANALYSIS.md` once a real reversal is studied with this lens, not
  assumed from the idea alone.

- **F59 — Should the ratio score's smoothing window (currently a fixed 12 minutes,
  `RatioCompositeSmoothingCadences = 48`) adapt to market conditions — DTE, VIX level — instead of
  staying constant?** User's point: a 12-minute smoothing window sized for a normal session may be
  wrong on expiry day (faster-moving, more mean-reverting) or when VIX is elevated (>18) versus
  calm (~12) — a fixed window can't be right for both regimes. Real, well-reasoned point, and
  exactly the kind of thing that needs a backtest showing the window's effect at different
  DTE/VIX conditions before changing it — same discipline as F57. Not designed or implemented; a
  regime-conditional smoothing length is a bigger structural change than it sounds (the window
  currently has no notion of "today's regime" at all) and deserves its own pass once F57's DTE
  analysis exists to build on.

- **F60 — Configurable trailing-stop/partial-book ratchet, to compare against current exit
  rules in the backtest.** User's specific mechanic: on hitting the first partial-book target
  (`PartialBookAtProfitPct`), close half the position and move the stop-loss to entry (breakeven);
  after that, ratchet the stop up by a fixed step (user's example: every further 5% price increase
  moves the stop up 5%, e.g. entry ₹100 → partial-book + SL→₹100 at ₹115 → SL→₹105 at ₹120, and so
  on). This is a genuinely different exit shape than the current `ExitRuleEvaluator`
  (`TrailAfterPartialBook` exists today but its exact ratchet behavior should be checked against
  this proposal before assuming they match). Explicitly requested as **configurable in the
  backtest**, not a live change — the point is to A/B it against the current exit rules on real
  data, per this session's own "backtesting is long-term, don't conclude from one run" framing.

**Standing goal, not a finding:** target is roughly 5-10 trades/day — now recorded in `CLAUDE.md`
so future threshold/frequency tuning (F53-F60 included) is judged against that, not an arbitrary
qualification rate.

---

## 2026-09-11 — Ratio-score-driven backtest of 11 Sep: root-caused, not a bug

First real test of F32's `--ratio-score` opt-in (see `BacktestRunner`'s own doc comment) against
11 Sep 2026 real data: **5 trades, 0% win rate, net -9,872, every exit a hard StopLoss.** User's
live-watching mental model expected the day to be strongly profitable instead, and pushed back
correctly rather than accepting the number — investigated rather than assumed either "the ratio
score is bad" or "there's a bug."

**Verified NOT a bug — the ratio score's direction was correct all day.** Real spot rose ~190
points (23,246 low at 09:50 -> 23,436 high at 14:40), a genuine sustained uptrend. The
*live-recorded* `RatioCompositeScore` (queried directly from `score_snapshots`, independent of
the backtest's own re-derivation) tracked this correctly: climbed into the 40s-70s by ~10:10am and
stayed there almost continuously the rest of the session. The *original* composite, over the same
day, whipsawed between -77 and +84, sign-flipping every 15-30 minutes — exactly the F28
"fades toward zero the moment it stops changing" problem the ratio score exists to fix. The user's
live impression was right.

**Two calibration gaps found, one ruled out empirically:**
- `MinAbsScore=64` sits at only the ratio score's own **~87th percentile** (real 4-day
  distribution: p50=48.8, p75=57.0, p90=65.1, p95=68.3), nowhere near the "top 5-10% of cadences"
  it was actually derived for on the *original* composite (which needs 83-95 to reach p90-p95) —
  the two scores' distributions are not comparable, so reusing the same absolute threshold
  silently changed selectivity.
- `ExitOnScoreBelowAbs=30` almost never fires for the ratio score, since its own median (48.8) sits
  well above 30 — every losing trade rode a blunt `StopLossPct` instead of a score-based exit that
  could have cut losses earlier.
- **Tested empirically, not just reasoned about**: re-ran the same day with `MinAbsScore` raised to
  68 (near the day's realized ceiling) -> 0 trades (never sustained 45s above it); raised to 65
  (~p90) with `ExitOnScoreBelowAbs=45` -> still 6 trades, still **0% win rate**, still all
  StopLoss, net **-12,187 (worse)**. Threshold recalibration alone does not fix this.

**Real root cause, found by pulling tick-level option price data (not spot alone) for the losing
trades**: entries cluster at **local price extremes**, not before them. `NIFTY15SEP26C23400`
around the 13:50-14:00 window: LTP ~86 at 13:55:00, spiking past 93+ within 30 seconds as spot
made its sharp ~90-point move (23,349 -> 23,436, 13:50-14:00). The backtest's ratio-score entries
for this exact strike landed at 14:04-14:08 with entry prices 126-142 — **after** the spike, then
rode the retracement down to a 10% stop as premium fell back toward 110-126. This is consistent
across both loss clusters (11:25 and 14:04-14:08): `RatioCompositeSmoothingCadences`'s 12-minute
window plus the 45s/3-cadence sustain requirement structurally means the score only confirms a
move well after it's already happened — a threshold-and-sustain entry mechanism chases moves that
have already run, rather than catching them.

**This directly validates F55 (crossover/momentum-based entry) over further threshold tuning on
the current mechanism** — the problem isn't *where* the bar is set, it's *that waiting for
sustained confirmation is structurally too slow* for how fast Nifty options can move. F56 (VWAP
fair-price gate) becomes more clearly relevant too: an entry mechanism that reliably lands after a
spike needs exactly this kind of "don't buy above where it's been trading" guard. Not implemented
yet — this is real, evidence-backed motivation for building F55 next, not a decision to build it
made from here.

Diagnostic scripts kept for reuse: `scripts/diagnose-sep11-ratio-score.sql` (score-vs-price
5-min-bucketed comparison), `scripts/ratio-score-percentiles.sql` (distribution comparison
between the two scores), `scripts/diagnose-c23400-price-path.sql` (tick-level single-strike price
path template).

---

## 2026-09-11 — F55 built: fast/slow ratio-composite momentum, tested against real 11 Sep data

Designed and built per the "root-caused, not a bug" section above. `LiveFeatureEngine` now computes
a second copy of the ratio composite (`RatioCompositeScoreFast`, `RatioFastSmoothingCadences = 8`
~2 min) alongside the existing slow one (~12 min), and `RatioMomentum = Fast - Slow`. Kept the
existing 45s/3-cadence sustain requirement unchanged (user's explicit call), reusing
`EntryRuleEvaluator`/`ExitRuleEvaluator`/`ScoreSustainTracker` completely unmodified —
`RatioMomentum` is substituted for `CompositeScore` in `BacktestRunner` the exact same way
`RatioCompositeScore` already was (now a 3-way `BacktestScoreSource` enum:
`OriginalComposite`/`RatioLevel`/`RatioMomentum`), so the live-path guarantee stays intact. Two new
`score_snapshots` columns (`RatioCompositeScoreFast`, `RatioMomentum`), migration `AddRatioMomentum`.
Fast FIFOs deliberately not restart-replayed (see `SeedHistory`'s own comment — a 2-minute window
self-heals quickly enough that the replay gap that mattered for the 12-minute slow window doesn't
apply here). Two new tests in `LiveFeatureEngineTests.cs` prove momentum is null before both
composites warm up and goes positive when a real bullish shift makes the fast read pull ahead of
the (still-diluted-by-older-samples) slow read. 361/361 tests green.

**Threshold derived from real data, not guessed** (per the plan's own verification step): ran the
new `--print-percentiles` capability (`BacktestResult.Snapshots` exposed for exactly this) across
all 4 real days with entries disabled — `|RatioMomentum|` distribution: p50=6.83, p75=11.57,
p90=16.92, p92.5=18.76, p95=20.82. Picked `MinAbsScore=17` (~p90) and `ExitOnScoreBelowAbs=8`
(~median, genuinely reachable — unlike `ExitOnScoreBelowAbs=30` against the ratio *level*, which
almost never fires since that level's own median sits at 48.8).

**Result on 11 Sep with these thresholds: 5 trades, still 0% win rate, but net -1,701.50 — an 83%
reduction in losses versus the initial ratio-level run (-9,872) and the recalibrated ratio-level
run (-12,187).** The meaningful change: **every exit is now `ScoreDecay`, not `StopLoss`** — the
exit mechanism is genuinely cutting losses early via the score-based signal for the first time in
this session's testing, confirming momentum's much smaller, more mean-reverting scale (median 6.83
vs the ratio level's 48.8) makes `ExitOnScoreBelowAbs` actually functional.

**Not fully resolved — reported honestly, not oversold:** this run's 5 entries clustered around
10:28-10:57 IST, during a choppy, non-trending stretch (23,276-23,305 range per the earlier 5-min
price data) — **not** around the 13:50-14:00 spike that originally motivated F55. So far this
session's testing shows the exit-side fix working, but the entry-side "catch the move earlier"
goal — the original motivation — is not yet confirmed.

**Follow-up: removing the sustain requirement entirely did NOT fix it (12 trades, worse net P&L
-3,314, still no spike-window entries) — ruled out sustain as the blocker.** Dumped
`RatioCompositeScoreFast`/slow/`RatioMomentum` at tick resolution around 13:45-14:15 (new
`--dump-scores` capability, since this data lives only in the backtest's throwaway InMemory store,
never anywhere queryable after the process exits) and found the real reason: the slow score was
*already* at 54-58 by 13:57 (the broader uptrend had been running most of the session), so fast
pulling further ahead during the spike only produced a momentum peak of ~13 — never near the
17-point threshold. **Momentum (fast minus slow) is structurally built to catch fresh inflections
against a flat baseline, not to add value during continuation of an already-running trend** — Sep
11 was mostly the latter. The dump also showed momentum going negative (-11 to -13) right as the
option premium was cratering at 14:06 — the useful signal was there, just not in the role built for
it (sole entry trigger); it's a better *filter*.

---

## 2026-09-11 — F55 extended: dynamic hybrid (session-rank level + momentum-sign filter)

User's explicit requirement for this mode: **no hardcoded metric or value for entry or exit —
every entry and exit must be dynamic.** Built `SessionRankTracker` (`NiftySignal.Backtest`) —
ranks the ratio level against every value seen so far *this session only* (reset per day, growing
from empty at market open) and, inversely, resolves what value sits at a given percentile of that
same growing distribution. `Entry.MinAbsScore`/`Exit.ExitOnScoreBelowAbs` are recomputed from this
every cadence via a new `MutableRulesetOptions` (exploits `LiveTradingEngine`'s own existing
hot-reload read, `_config => rulesetOptions.Current` — zero changes to `LiveTradingEngine` itself),
so the threshold is always relative to *today's own realized range*, never a fixed magnitude tuned
to one historical snapshot. `RatioMomentum`'s **sign** (not magnitude — zero is a structural
boundary, not a tuned constant) gates whether the level score is used unchanged or forced to
exactly 0 that cadence, which fails `MinAbsScore` for a fresh entry and trips
`ExitOnScoreBelowAbs` for an open one. The one remaining parameter is a rank *percentage* (default
85th for entry, 50th for exit) — deliberately a different kind of constant than a raw score
magnitude: portable across regimes/scores by construction, and directly justified by the project's
own 5-10 trades/day goal rather than a curve-fit. `BacktestScoreSource.DynamicHybrid`, same
substitution-only pattern as every other F55 mode — `EntryRuleEvaluator`/`ExitRuleEvaluator`/
`ScoreSustainTracker`/`LiveTradingEngine` all remain completely unmodified. New pure unit tests for
`SessionRankTracker` (6, all passing). 367/367 tests green overall.

**Result on 11 Sep: 30 trades, 43.3% win rate, profit factor 3.01, net P&L +7,343.25 — the first
genuinely positive result across every ratio-based approach tried this session.** Critically, it
directly caught the originally-missed spike: entry at 14:01:00 (`NIFTY15SEP26C23400` @ 108.80),
exit at 14:05:45 @ 139.05 (netPnl +2,962.50), riding almost the entire 13:50-14:00 move rather than
buying its tail. All exits `ScoreDecay` (momentum-based), none rode to a hard stop.

**New problem, not yet solved: 30 trades is 3-6x over the 5-10/day target.** The rank-relative
threshold gets progressively less selective as the session's own distribution widens with more
"normal" readings accumulating near the current baseline, and combined with the existing 2-minute
`ReEntryGapSameDirectionMinutes`, produced far more qualifying moments than intended. Real,
measured progress (first positive P&L), but not a finished result — needs its own pass on trade
frequency (a higher/adaptive rank cutoff, or reconsidering how `MaxTradesPerDay`/re-entry gap
interact with a rank-relative gate) before this is ready to be treated as validated. Not committed
yet, per ongoing user instruction to keep discussing before committing.

---

## 2026-09-11 — F55 redesigned: price (VWAP deviation) leads entry, ratio score confirms and exits

User's structural observation, made explicit after the result above: **every ratio metric is
built from option chain data that *reacts* to price — none of it can lead.** No amount of
smoothing-window or rank-threshold tuning on option-derived inputs changes that; it explains the
exact pattern seen all session — every exit mechanism worked (confirming an already-happened move
is what a reactive signal is good at), every entry mechanism struggled (entries need something
that moves *with* price). Confirmed prerequisite before designing further: the tracked future
(`NIFTY29SEP26F`) carries real, growing traded volume across 11 Sep (0 -> 3.46M); the spot/index
instrument carries none (never trades, it's calculated) — so VWAP has to be built on the future.

**Built, same "no hardcoded value" rule as before:** `LiveFeatureEngine` now tracks the future's
own cumulative VWAP (`OnTick` accumulates volume-delta-weighted price, same
`Math.Max(0, volume - previous)` diffing pattern `_previousVolumeByToken` already uses elsewhere)
and z-scores `LastPrice - VWAP` against a new 30-minute rolling window
(`FeatureWindowLengths.FuturesVwapDeviation`, reusing `WelfordRollingWindow` unchanged, same
self-inclusion-safe compute-then-add ordering audit finding F46 already established). Three new
`ScoreSnapshot` columns (`FuturesVwap`, `FuturesVwapDeviationRaw`, `FuturesVwapDeviationZ`),
migration `AddFuturesVwapDeviation`, `SeedHistory` replay for the deviation window (VWAP's own
cumulative sums are *not* replayed — an accepted restart gap, same spirit as `_previousCadence`'s
own documented limitations). Two new tests, including one that caught a genuine, worth-understanding
behavior: checking a full sustained climb's *end* gave a **negative** z-score at first, because
VWAP is cumulative and keeps catching up across a long move, so deviation itself decays over a
sustained climb — the z-score legitimately reads "still rising vs. already settling," not a bug.
Fixed the test to check right after a fresh jump instead, which is also the economically correct
moment for this signal to matter.

`BacktestRunner.ApplyDynamicHybrid` redesigned in place (not a new enum case): `FuturesVwapDeviationZ`
now ranked via the existing `SessionRankTracker` (same mechanism, different series) and drives
entry direction/magnitude; qualifies only when the ratio *level*'s sign agrees (user's explicit
confirmation-gate requirement) **and** `RatioMomentum`'s sign also agrees (preserves the exact
exit mechanism already proven working — either one flipping against the trade zeros the effective
score, tripping `ExitOnScoreBelowAbs`). 369/369 tests green.

**Result on 11 Sep: 13 trades, 46.2% win rate, profit factor 3.95, net P&L +11,958.00 — better
than the ratio-level-primary version on every dimension** (previous: 30 trades, profit factor
3.01, net +7,343.25). Trade count also moved much closer to the 5-10/day target (13 vs. 30).
Directly confirms the structural fix: **three** trades caught the 13:50-14:00+ spike this time,
starting at 13:56:15 (vs. 14:01:00 before) — entering earlier and catching more of the move
(+4,665.50, +3,957.00, +2,962.50 on that cluster alone) — plus a second real rally at 11:13-11:19
(+2,689.50, +935.00). All 13 exits still `ScoreDecay`, none rode to a hard stop.

Not committed yet, per ongoing user instruction to keep discussing before committing. Trade count
(13) is still slightly above the 5-10 target — the next natural question, not yet investigated.

---

## 2026-09-11 — Price-led dynamic hybrid run across all 4 real days: mixed per-day, positive aggregate

Same run (`--score-mode=dynamic-hybrid`, same code, no changes) across the full 08-11 Sep range
instead of just 11 Sep, per this session's own "backtesting is long-term, don't conclude from one
run" principle — Sep 11 alone looked strong; worth checking whether that held up or was a
favorable day.

**Aggregate: 35 trades (8.75/day simple average — within the 5-10/day target across the full
set), 40.0% win rate, profit factor 2.08, net +14,001.50.** All 35 exits still `ScoreDecay` or
`StopLoss` (one `StopLoss` this time, 08 Sep 10:15 — the only hard stop across all 4 days and 35
trades).

**Per-day, the picture is genuinely mixed, not a clean win — reported honestly:**

| Day | Trades | Net P&L | Wins/Losses |
|---|---|---|---|
| 08 Sep | 9 | -4,812.50 | 2W / 7L |
| 09 Sep | 9 | +9,707.00 | 6W / 3L |
| 10 Sep | 4 | -2,851.00 | **0W / 4L** |
| 11 Sep | 13 | +11,958.00 | 6W / 7L (already reviewed above) |

Two of four days lost money. 10 Sep is a clean 0-for-4 and stands out — not yet investigated why
(a natural next step, mirroring the tick-level diagnosis already done for 11 Sep's original
problem, using the same `--dump-scores`/tick-path tooling). The aggregate is positive only because
09 Sep and 11 Sep were strong enough to cover 08 Sep and 10 Sep's losses — exactly the kind of
result this session's own "don't conclude from one run" principle exists to catch: a single
favorable day (11 Sep) would have overstated how settled this mechanism actually is.

Not committed. Genuinely promising (positive net across 4 real days, structurally sound design,
the spike-catching behavior holds up), but "promising" is the accurate word — not "validated." A
larger, multi-week dataset and an understanding of what made 10 Sep fail cleanly are the real next
steps before this is ready for anything past continued backtesting.

---

## 2026-09-11 — Price-led dynamic hybrid re-run with MaxConcurrentPositions=1 (single trade at a time)

User's live-trading design going forward is one trade at a time per strategy (multi-strategy
parallelism is a future goal, not now), so `MaxConcurrentPositions=3` in the run above doesn't
match how this will actually trade. Added a `--max-concurrent-positions=N` CLI override to
`NiftySignal.Backtest/Program.cs` (applied via `with` on the real loaded `RulesetConfig.Capital`,
same pattern as every other override) and re-ran the identical 08-11 Sep price-led DynamicHybrid
backtest with `MaxConcurrentPositions=1`. No scoring/entry/exit logic changed — this isolates the
effect of concurrency alone.

**Aggregate: 22 trades (6.9/day — still inside the 5-10/day target), 31.8% win rate, profit
factor 1.43, net +4,152.25.** Both win rate and profit factor dropped materially from the
`MaxConcurrentPositions=3` run (40.0% / 2.08 / +14,001.50) — expected, since capping concurrency
to 1 blocks every pyramiding-style stacked entry the 3-slot version took on top of an
already-open, already-working position (visible in the `=3` run's per-trade log: e.g. 09-11
11:13/11:15/11:18 were three overlapping entries around the same rally). Those stacked entries
were disproportionately the winners — losing them thins out the winning side more than the
losing side.

**Per-day, same mixed pattern as before, all figures now lower:**

| Day | Trades | Net P&L | Wins/Losses |
|---|---|---|---|
| 08 Sep | 6 | -2,716.50 | 2W / 4L |
| 09 Sep | 4 | +4,691.25 | 2W / 2L |
| 10 Sep | 4 | -3,228.00 | **0W / 4L** |
| 11 Sep | 8 | +5,405.50 | 4W / 4L |

10 Sep stays a clean 0-for-4 under both concurrency settings — this run adds evidence it's a
real, day-specific failure mode (not an artifact of overlapping entries), still not investigated.
Net P&L stayed positive across all 4 days at `MaxConcurrentPositions=1` (+4,152.25), same
direction as the `=3` run, just a smaller number — the mechanism's sign held, its size didn't.

Not committed. This is the more realistic number for how the system will actually trade going
forward (single position at a time), and it's the honest baseline to compare any future change
against — not the `=3` run's larger, partly concurrency-inflated figures.

---

## 2026-09-12 — New backtest dataset: `CadenceContexts` (parent table) built, populated, validated

Started a ground-up rebuild of the backtest data pipeline, separate from everything above. Per
user instruction: a brand-new, dedicated Postgres database (`niftysignal_backtest_analysis`),
built purely from raw historical ticks, leakage-safe by construction (no field ever reads
information that wouldn't exist yet at its own timestamp — a "close for day" field was proposed,
caught as look-ahead, and removed before anything was built). New project
`NiftySignal.BacktestData`; the parent table (`CadenceContext`, 49 columns) is built, migrated,
and populated for all 4 real trading days (08-11 Sep 2026, 1,500 rows/day, 6,000 total). Full
design discussion, every field's rationale, and the schema itself live in this session's history;
the entity's own doc comments in `NiftySignal.BacktestData/CadenceContext.cs` are the durable
reference. The child (strike/option-type) table is designed (full OHLC per leg, for realistic
paper-trade fills) but deliberately deferred — user is treating this as a long-running project,
parent table first.

**Validated against real data before trusting it**: row counts exactly match the theoretical
22,500s/15s per day; every warm-up-driven null count landed *exactly* on the theoretical window
(64 nulls for `FutureOiChangeFromLastKnown` = 4min/15s x 4 days, 80 for the 5-min depth mean = 4
days x 20 cadences, 240 for the 15-min mean = 4 days x 60 cadences); zero negative
volumes/OI-changes, zero out-of-range depth imbalances, zero duplicate timestamps; ATM strikes
and VWAP at the previously hand-verified 11-Sep-14:00 cadence matched the real option-chain-derived
numbers from two days earlier exactly. One genuine, non-bug finding: 08 Sep's `Instruments`
snapshot contained a second option series expiring that same day (confirmed via real tick volume,
not just stale metadata) — `CadencePopulator` correctly picked it as nearest-expiry, exactly
matching `LiveFeatureEngine`'s own convention, meaning 08 Sep is structurally an expiry-day regime
(0-6.25h DTE all session) and should not be treated as "just another mid-week day" when designing
DTE-based rules later.

**Bug found and fixed during first run**: Npgsql refuses to write a `DateTimeOffset` with a
non-zero offset to `timestamp with time zone` (insists on UTC). Fixed by converting `dayStart`/
`dayEnd` (and everything derived from them, including every persisted `Timestamp`) to UTC right
after construction — the same instant, just a different displayed offset, so nothing about the
actual time arithmetic changed. Caught and fixed a related latent bug in the same change:
`TradingHoursRemaining` extracted the calendar date via `now.DateTime`, which returns wall-clock
time in whatever offset `now` carries — correct only by coincidence that a 09:15-15:30 IST session
never crosses a UTC calendar-day boundary. Fixed to explicitly convert to IST first.

---

## 2026-09-12 — Future depth imbalance vs. future price: weak, sign-inconsistent correlation

First correlation check on the new dataset, per user request, to inform whether/how heavily to
weight `FutureDepthImbalanceMean5Min`/`Mean15Min` in a future unified score. Backward-vs-forward
methodology, same as every other correlation check in this project's history (PriceMomentum,
SpreadRatio): correlate each metric against the future's own price change over the *prior*
1/5/15 minutes (does the metric coincide with what already happened) and the *next* 1/5/15
minutes (does it predict what's coming), day-scoped (no lead/lag bleeding across a day boundary),
pooled across all 4 real days and broken out per day. SQL: `scripts/depth-imbalance-correlation.sql`.

**Pooled (n=5,639-5,799):**

| Metric | vs past 1m | vs past 5m | vs past 15m | vs next 1m | vs next 5m | vs next 15m |
|---|---|---|---|---|---|---|
| 5-min mean | -0.027 | -0.079 | -0.125 | -0.020 | -0.059 | -0.032 |
| 15-min mean | -0.022 | -0.077 | -0.141 | -0.014 | -0.020 | +0.049 |

**Per day (5-min horizon):** 08 Sep -0.092/-0.151, 09 Sep -0.093/-0.092, 10 Sep -0.004/+0.034, 11
Sep -0.125/-0.026 (backward/forward for the 5-min mean). The *forward* correlation is not
consistently signed across days (10 Sep flips positive) -- not something to weight a real decision
on. The *backward* correlation is more consistently negative: a bid-heavy book tends to follow a
price *decline*, not a rise -- plausibly bargain-hunters resting fresh bids after a drop while
sellers who already got their move stop offering, both mechanically pushing the ratio bid-heavy
*after* the fact. Reads as the book documenting a move that already happened, not anticipating one.

**Structural point raised by the user, more important than the correlation number itself**: depth
imbalance measures resting (passive) order-book quantity -- who's *waiting* -- not executed trade
flow -- who's actually *trading*. A real directional move is driven by aggressive orders hitting
the ask (or bid), which depletes resting depth sharply and briefly at the moment of the trade;
market makers often replenish it fast at the new price, so a 5-15 minute average of resting
quantity can wash out the exact moment real buying/selling pressure was highest, or even catch the
book on the wrong side of it afterward. The correct measure for "aggressive orders eating the ask"
is a trade-aggressor / signed-volume metric, not a resting-book metric -- this already exists in
the live composite score for the option chain (`CvdProxy`, quote-rule classification: LTP at/above
the bid-ask midpoint counts buy-leaning) but has no equivalent for the future yet, even though
`FutureVolumeDeltaThisCadence` (unsigned) is already captured in the new parent table.

**Conclusion for weighting**: do not assign `FutureDepthImbalance` a real weight yet. Four days is
too small a sample, the forward correlation isn't consistently signed, and the metric is more
likely missing its complement (a future-specific aggressor/CVD proxy, structurally the same
addition as the existing option-chain `CvdProxy`, applied to the future's own volume) than simply
wrong on its own terms. Depth imbalance (resting liquidity) and an aggressor-volume metric (traded
liquidity) answer different questions and are not substitutes for each other -- a real weighting
decision likely needs both. Not yet built -- user is deliberately staying on the parent table for
now; noted here as the natural next column to add to it, not a child-table concern.

---

## 2026-09-12 — `FutureCvdProxy` built and correlated: positive-signed, meaningfully better than depth imbalance, but the day-long cumulative version has a real flaw

Built the aggressor-volume proxy flagged above: `FutureCvdProxyThisCadence`/`CumulativeDay`, same
"quote rule" as production's existing option-chain `CvdProxy` (a tick's entire volume delta since
the previous tick counts buy-leaning if LastPrice sat at/above the bid-ask midpoint, sell-leaning
otherwise), applied to the future's own ticks. User's own caution motivated the design: FlatTrade's
feed has no per-trade tape -- confirmed directly against real ticks (11 Sep 13:55-13:56) that a
single reported volume delta routinely bundles thousands of lots that plausibly traded across many
individual prints, some potentially in the opposite direction. Documented as a proxy, not true CVD,
with the same honesty as the metric it mirrors. `dotnet ef migrations add AddFutureCvdProxy`, two
nullable `bigint` columns, nothing else touched. Repopulated all 4 days after a full delete (per
user instruction, idempotency is per-day-existence so a schema-changing repopulation needs an
explicit clear first). Internal-consistency check: cumulative day-over-day increments matched the
per-cadence net exactly, zero mismatches across all 6,000 rows.

**Correlation (same backward/forward methodology as the depth-imbalance check), pooled across all
4 days:**

| Metric | n | bwd 1m | bwd 5m | bwd 15m | fwd 1m | fwd 5m | fwd 15m |
|---|---|---|---|---|---|---|---|
| Per-cadence net | 5,667 | +0.115 | +0.121 | +0.087 | -0.002 | +0.038 | +0.041 |
| Cumulative-day | 5,876 | +0.041 | +0.110 | +0.212 | +0.032 | +0.068 | +0.081 |

Every correlation here is **positive** -- buy-leaning classified volume genuinely associates with
price having gone up, and weakly with price continuing up. That's the intuitive sign
`FutureDepthImbalance` failed to show (its correlations were mostly negative -- see the section
above). The cumulative-day version's 15-minute backward correlation (+0.212) is the strongest
relationship found on this dataset so far.

**But the per-day breakdown surfaced a real flaw in the cumulative-day version specifically:**

| Day | per-cadence fwd 5m | cumulative-day fwd 5m |
|---|---|---|
| 08 Sep | +0.041 | -0.074 |
| 09 Sep | +0.070 | +0.122 |
| 10 Sep | -0.013 | +0.007 |
| 11 Sep | +0.019 | -0.105 |

The per-cadence net's forward sign is fairly consistent (small positive most days). The
**cumulative-day version's forward correlation flips sign across days, including going negative on
11 Sep -- the day with the documented 13:50-14:00 rally.** Root cause, consistent with the user's
own instinct when asking for this: a whole-day running total drags along everything that happened
hours earlier, so a strong morning trend can still be dominating the number in the afternoon even
after the market has genuinely turned -- exactly the same "sluggish, dominated by stale activity"
failure mode already known from the original composite's own PENDING F28 finding (level metrics
z-scored/accumulated over too long a horizon fade to look normal, or in this case stay stuck,
long after conditions changed).

**Fix, built the same session**: added `FutureCvdProxyNet5Min`/`Net15Min` -- a rolling **sum**
(not mean, since this is a flow quantity naturally read as a total over a window) of the
per-cadence net, evicting anything older than 5/15 real minutes (new `RollingNetSumWindow` class,
same eviction shape as `WelfordRollingWindow`/`OiLookbackWindow` but tracking a plain sum since no
variance is needed). Not yet correlated -- needs the same delete+repopulate+validate cycle before
trusting it. `dotnet ef migrations add AddFutureCvdProxyRollingNet`, two more nullable `bigint`
columns.

**Conclusion for weighting so far**: `FutureCvdProxyThisCadence` (the per-cadence net) is the most
promising single metric found on this dataset yet -- positive-signed, directionally consistent
across days, real (if still modest) forward correlation. `FutureCvdProxyCumulativeDay` should
likely not be used directly for a forward-looking rule without the rolling-window version once
that's validated -- its day-long memory appears to work against it. Still only 4 days of data;
same "don't conclude from one run" discipline applies here as everywhere else.

---

## 2026-09-12 — Standing process established: per-metric evaluate-then-decide cycle

User's explicit instruction, applies to every candidate metric on the ranked list going forward,
not just this one: try multiple formulations of a metric, correlate each against real price
(backward and forward, every time), and reach an **explicit yes/no conclusion** on edge before
anything gets added to a real scoring weight. Record every iteration here, positive or negative --
a metric that fails this cycle is itself a useful finding, not a dead end to discard quietly.
Durable copy in `CLAUDE.md`'s "Metric-by-metric evaluation process" section.

---

## 2026-09-12 — `FutureCvdProxyNet5Min`/`Net15Min`: strongest candidate found yet, one recurring anomalous day

Rolling-window fix for `FutureCvdProxyCumulativeDay`'s day-long-memory problem (previous section):
`FutureCvdProxyNet5Min`/`Net15Min`, a rolling **sum** (not mean) of the per-cadence net over the
trailing 5/15 real minutes, new `RollingNetSumWindow` class (same eviction shape as
`WelfordRollingWindow`, tracking a plain sum since no variance is needed for a flow quantity).
Migration `AddFutureCvdProxyRollingNet`, two nullable `bigint` columns. Repopulated all 4 days
after a full delete. Null rates landed exactly on the theoretical warm-up windows again (80/240
nulls for 5min/15min, matching 20/60 cadences x 4 days).

**Correlation, pooled across all 4 days -- all four CVD variants together:**

| Metric | n | bwd 1m | bwd 5m | bwd 15m | fwd 1m | fwd 5m | fwd 15m |
|---|---|---|---|---|---|---|---|
| Per-cadence net | 5,667 | +0.115 | +0.121 | +0.087 | -0.002 | +0.038 | +0.041 |
| Cumulative-day | 5,876 | +0.041 | +0.110 | +0.212 | +0.032 | +0.068 | +0.081 |
| Rolling 5-min net | 5,799 | +0.093 | +0.260 | +0.275 | +0.042 | **+0.142** | +0.116 |
| Rolling 15-min net | 5,639 | +0.088 | +0.256 | **+0.413** | +0.069 | +0.123 | +0.033 |

Both rolling versions meaningfully beat the raw per-cadence net and the day-long cumulative. The
15-min net's backward correlation (+0.413) is the strongest relationship found anywhere in this
project's metric-testing so far; the 5-min net's forward-5-minute correlation (+0.142) is the best
*forward-looking* number found yet -- roughly 3.7x the depth-imbalance metric's best forward result.

**Per-day (5-min horizon):**

| Day | 5-min net -> fwd 5m | 15-min net -> fwd 5m |
|---|---|---|
| 08 Sep | +0.099 | -0.077 |
| 09 Sep | +0.234 | +0.170 |
| 10 Sep | **-0.065** | **-0.050** |
| 11 Sep | +0.144 | +0.172 |

5-min net positive on 3 of 4 days -- better consistency than depth imbalance ever showed, but not
clean. **10 Sep is the outlier for both windows, and this is the third independent analysis where
10 Sep has broken a pattern that held on the other three days**: it was also the only day
depth-imbalance's forward correlation flipped positive (see above), and separately, the only day
the price-led DynamicHybrid backtest went a clean 0-for-4 on trades (2026-09-11 section, earlier).
Not proof of anything specific yet, but three independent analyses landing on the same day is a
strong enough recurring signal that "what actually happened on 10 Sep" deserves its own
investigation at some point.

**Verdict per the evaluation cycle above: not a final yes yet** -- one day out of four still
disagrees, and concluding "edge confirmed" off that would be exactly the premature call the
process exists to prevent. But `FutureCvdProxyNet5Min` is, as of this test, the strongest single
candidate found across everything tried on this dataset so far: better forward correlation, better
day-to-day consistency, and a coherent, intuitive sign (unlike depth imbalance's counter-intuitive
negative one). Next steps whenever pursued: more days once available (4 is still thin), or
investigating what made 10 Sep different.

## 2026-09-12 -- `FutureCvdProxy` direct-future trade simulation, six variants: real directional
tendency confirmed, but no variant makes it a viable standalone trading rule

Six no-restriction trade-simulation variants (`NiftySignal.MetricTrials`, thumb-of-rule: entries
only after 09:30, force-close 15:15, no new entries after 15:00, no risk gates) run against all 4
real days, per the standing "no new metric until we conclude on this one" instruction. Two
fundamentally different paradigms tried: threshold-based (enter only on extreme session-rank
readings of `FutureCvdProxyNet5Min`, exit on decay/flip) and always-positioned (continuously long
or short, EMA-smoothed sign flips on `FutureCvdProxyNet15Min`, no thresholds at all).

**Aggregate results, all 4 days:**

| Variant | Trades/legs | Win rate | Net P&L | Time in market |
|---|---|---|---|---|
| 1. Ungated, fast exit (baseline) | 28 | 53.6% | +14.55 | 13.3% |
| 2. Trend-efficiency gated entry, fast exit | 23 | 52.2% | +14.35 | 12.0% |
| 3. Ungated, slow exit (15-min net) | 48 | 47.9% | +95.55 | 21.7% |
| 4. Ungated, fast exit, looser exit-rank | 27 | 51.9% | +23.50 | 15.5% |
| 5. Always positioned (EMA span 20, flip on sign) | 52 | 40.4% | +75.65 | 99.9% |
| 6. Always positioned + trend-efficiency flip gate | 46 | 41.3% | +18.10 | 99.3% |

**Threshold-based variants (1-4) structurally cannot reach the user's 60-75% time-in-market target**
-- they only hold a position while the signal is extreme by design, capping time-in-market at
12-22% regardless of exit stickiness. This motivated variant 5.

**Variant 5 (always positioned) hits the time-in-market target by construction (99.9%), but its
P&L is a single-day artifact, not a robust result**: 09 Sep alone contributed +186.30 against a
+75.65 total, meaning the other 3 days net -110.65 combined (08 Sep -59.25, 10 Sep -72.80, 11 Sep
+21.40). Win rate 40.4% -- P&L comes entirely from trend-following asymmetry (few large wins, many
small losses), not from being right more often than wrong.

**Variant 6 (the trend-efficiency flip gate, built specifically to fix variant 5's concentration
problem) did not fix it -- it made the aggregate worse and relocated the concentration rather than
resolving it:**

| Day | Variant 5 net | Variant 6 net | Delta |
|---|---|---|---|
| 08 Sep | -59.25 | -28.55 | +30.70 (improved) |
| 09 Sep | +186.30 | +166.30 | -20.00 (slightly worse) |
| 10 Sep | -72.80 | -2.90 | **+69.90 (the intended fix -- worked as designed)** |
| 11 Sep | +21.40 | -116.75 | **-138.15 (badly worse)** |
| Total | +75.65 | +18.10 | -57.55 |

The gate worked exactly as designed on 10 Sep (the choppiest day by trend efficiency, 0.022 vs.
11 Sep's 0.060 -- see the earlier depth-imbalance section) -- it cut that day's loss by ~96%.
But it did real damage on 11 Sep, previously the second-best day. Tracing the actual trades: up to
11:11 the two variants are identical (same losing short flip, -49.35 in both). After that, variant
6's gate delayed a flip back to long by ~5.5 minutes (worse re-entry price), and later blocked a
short-to-long flip during 13:44-13:54 that turned out to be a genuine, sizeable up-move rather than
chop -- holding the wrong-side (short) position through it produced a single -35.95 leg that
doesn't exist in variant 5 at all. **The mechanism is the actual finding here**: delaying a flip
only helps when the flip attempt was itself premature (noise); when the underlying signal has
correctly identified a real reversal, delaying it just holds the wrong side longer and compounds
the loss instead of preventing a whipsaw. Trend efficiency (computed backward-looking over a
trailing window) can't distinguish "this move is chop" from "this move just started and hasn't
accumulated enough of a trail yet" -- exactly the two cases that most need to be told apart for a
flip-delay gate to help rather than hurt.

**Verdict on `FutureCvdProxy` under the standing per-metric evaluation cycle:** real, positive,
regime-dependent directional tendency confirmed yet again (this is the 4th independent analysis
landing on the same read: correlation, threshold-simulation, always-positioned-simulation, and now
gated-always-positioned-simulation all agree the signal works on trending days and struggles on
choppy ones). But **no trading-rule variant tried across six attempts turns that into a standalone
edge that's both profitable in aggregate and not concentrated in one or two trending days** --
every fix tried for the concentration/regime problem so far (threshold gating, slow exit, looser
exit, always-positioned, gated always-positioned) has either left time-in-market too low or
introduced a new failure mode as bad as the one it fixed. This matches the project's own stated
composite-score philosophy: `FutureCvdProxy` is not being carried forward as a metric strong enough
to trade alone, but as one candidate input (with a demonstrated, real, if regime-dependent,
directional signal) for the eventual 5-8-metric composite score, where a trend/regime read from a
*different* metric can do the job of telling chop from trend apart, rather than asking one metric's
own trailing-window derivative to referee itself.

## 2026-09-12 -- DTE and VIX checked as explanations for the recurring 10-Sep anomaly: real findings, neither one resolves it

Before treating the six-variant `FutureCvdProxy` trade-simulation conclusion above as settled, user
pushed on two specific, checkable hypotheses rather than accepting "regime-dependent" at face
value. Both checked directly against real data, not assumed.

**DTE.** Queried `instruments` in `niftysignal_vm_copy` for the nearest weekly option expiry
tracked on each of the 4 days (Nifty's weekly expiry is Tuesday):

| Date | Day | Nearest weekly expiry | DTE |
|---|---|---|---|
| 08 Sep | Tue | 08 Sep (same day) | **0 -- expiry day itself** |
| 09 Sep | Wed | 15 Sep | 6 |
| 10 Sep | Thu | 15 Sep | 5 |
| 11 Sep | Fri | 15 Sep | 4 |

Real, previously-unexamined structural fact: 08 Sep is categorically different (the expiry day
itself), and 09-11 Sep each sit at a different, unrepeated DTE. **With exactly one day per DTE
value, "chop vs. trend," "day-of-week," "DTE," and "plain noise" are observationally
indistinguishable explanations with this sample** -- none can be isolated from the others yet.
10 Sep (DTE=5) sits in the middle of the 09-11 DTE range, not at an extreme, so DTE alone doesn't
cleanly single it out either.

**VIX.** Two separate checks against `CadenceContexts`' existing `Vix*` columns (India VIX was
already being captured for the live composite, just not yet checked against this dataset):

1. `corr(VixChangeFromLastCadence, FutureCvdProxyNet5Min)` per day: 08 Sep -0.016, 09 Sep -0.005,
   10 Sep +0.035, 11 Sep -0.018 -- all near zero, 10 Sep is not an outlier here. **VIX does not
   explain why the CVD signal itself behaves differently on 10 Sep.**
2. `corr(VixChangeFromLastCadence, FutureChangeFromLastCadence)` per day (the classic VIX/price
   leverage effect -- VIX up, price down): 08 Sep +0.057, 09 Sep -0.253, 10 Sep -0.235, 11 Sep
   -0.184. The negative relationship holds cleanly on 3 of 4 days; **08 Sep (the expiry day) is
   the one that breaks it**, plausibly pinning/gamma-unwind flows overriding the normal
   fear-selloff relationship. 10 Sep is unremarkable here too (-0.235, between 09 Sep's -0.253 and
   11 Sep's -0.184). Separately, day-level: 10 Sep was the only day of the 4 where VIX *fell*
   intraday (-1.01%, vs. +2.01%/+4.17%/+0.91% the other three) -- a real, distinct fact about that
   day, just not one that shows up as a per-cadence correlation with the CVD signal.

**Net effect**: neither hypothesis resolves the 10-Sep anomaly -- it remains unexplained. But
this wasn't a wasted check: it surfaced a real, VIX-independent-of-CVD candidate signal (the
VIX/price leverage effect, with a genuine expiry-day caveat) worth carrying forward on its own
merits. Added to `docs/SCORE_CANDIDATES.md` as a proposed, not-yet-evaluated candidate. The
`FutureCvdProxy` conclusion from the six-variant section above stands as a checkpoint, not a final
verdict, per the project's own "backtesting is long-term" rule -- more real days (ideally spanning
another expiry day and another mid-cycle day) are what's actually needed to separate DTE,
day-of-week, and regime from each other, not another rule variant on the same 4 days.

## 2026-09-12 -- Trend efficiency reframed as a reversion signal (not continuation), VIX confirmed contemporaneous-only after forward testing

Two follow-up tests, `scripts/trend-efficiency-and-vix-forward-correlation.sql`, per user request
to (a) resolve whether trend efficiency is a directional or volatility metric before scoring it,
and (b) push VIX past the contemporaneous-only correlation found in the prior section before
promoting it to a scored candidate.

**Trend efficiency -- signed (net/path over trailing 15 min, naturally bounded [-1,1]) vs. unsigned
magnitude, correlated against forward price change:**

| Framing | 08 Sep fwd5m/15m | 09 Sep fwd5m/15m | 10 Sep fwd5m/15m | 11 Sep fwd5m/15m | Pooled fwd5m/15m |
|---|---|---|---|---|---|
| Signed (continuation) | -0.159/-0.152 | +0.080/-0.064 | -0.223/**-0.455** | -0.075/-0.202 | -0.045/**-0.146** |
| \|Magnitude\| (volatility) | +0.061/-0.022 | -0.009/-0.103 | +0.033/+0.112 | +0.168/-0.073 | +0.075/-0.014 |

The volatility framing is weak and sign-inconsistent across days -- no real signal. The signed
framing shows a real negative correlation, strongest at 15 minutes and on 10 Sep -- **a clean
recent trend tends to partially reverse, not continue.** This is the opposite of the assumption
behind the trend-efficiency flip-gate built earlier this session (which trusted a flip *more*
during high trend efficiency, on a continuation read) -- retroactively explains why that gate hurt
11 Sep: a high-efficiency moment is when reversal risk is higher, not lower. Renamed "trend
reversion" in `docs/SCORE_CANDIDATES.md` to avoid the misleading "efficiency = continuation"
connotation. Formula: `score = -100 * (net_15min / path_15min)` -- the minus sign carries the
finding.

**VIX -- forward correlation across three formulations (raw per-cadence change, rolling 5-min sum,
rolling 15-min sum, the last two mirroring the fix that worked for `FutureCvdProxy`):**

| Formulation | Pooled vs fwd 5m | Pooled vs fwd 15m |
|---|---|---|
| Raw per-cadence | -0.006 | +0.002 |
| Rolling 5-min sum | -0.015 | -0.007 |
| Rolling 15-min sum | -0.010 | -0.016 |

All three are essentially zero pooled and sign-inconsistent per day (15-min-sum per-day: 08
+0.030, 09 -0.245, 10 +0.045, 11 -0.019). **Conclusion: VIX change's real, confirmed contemporaneous
relationship with price (prior section) does not extend forward in any formulation tested --
same "documents what already happened" failure mode as `FutureDepthImbalance`. Not promoted to a
scored candidate.** Genuine negative finding, kept on record rather than discarded quietly, per the
standing evaluation-cycle rule.

## 2026-09-12 -- Spot/future basis change: a small but unusually consistent reversion signal; a striking outlier traced to a benign data artifact

Per user request, before moving to the child (strike-level) table: does the spot/future basis, or
its change over time, correlate with the future's forward price move? `scripts/spot-future-basis-correlation.sql`, same backward/forward, per-day + pooled methodology as every
other candidate. Basis = future - spot (future premium, matching the live composite's own
`FuturesBasisRaw` sign convention).

**Basis LEVEL is dominated by mechanical cost-of-carry decay, not a live signal.** Average basis
shrinks day to day as DTE counts down toward the next weekly expiry: 107.42 (09 Sep, DTE=6) ->
93.07 (10 Sep, DTE=5) -> 64.23 (11 Sep, DTE=4). Level correlations were accordingly noisy and
sign-inconsistent (10 Sep even flips positive against the other days' negative). This is the same
"differencing beats level" lesson already learned from `FutureCvdProxyCumulativeDay`.

**Basis CHANGE (per-cadence), pooled and per day:**

| Day | vs fwd 5m | vs fwd 15m |
|---|---|---|
| 08 Sep | -0.045 | -0.041 |
| 09 Sep | -0.043 | -0.031 |
| 10 Sep | -0.058 | -0.034 |
| 11 Sep | -0.050 | -0.038 |
| Pooled | -0.050 | -0.034 |

Modest in magnitude, but **every one of the 4 days agrees in sign** -- the most cross-day-consistent
correlation found in this project's metric-testing so far, even more consistent than
`FutureCvdProxy`'s own numbers. Reads as: basis widening (future outpacing spot) precedes a small
pullback, not continuation -- the same reversion flavor as the trend-reversion finding above (two
independent, structurally unrelated metrics both saying "short-term overextension -> pullback").

**Rolling-15-min-sum version is stronger on 3 of 4 days (-0.125 to -0.172 vs fwd 15m) but flips to
+0.192 on 08 Sep, the expiry day** -- plausibly because basis is mechanically collapsing toward
zero all session on expiry day (settlement approaching), swamping whatever flow signal exists over
a 15-minute window on that specific day type. Not yet known if this replicates on a future expiry
day or is a one-off.

**A striking outlier checked before being trusted, per this project's own standing discipline**:
10 Sep showed a momentary basis of 14.75 (vs. a normal ~90-115 range) -- initially looked like it
might finally explain the recurring 10-Sep anomaly from earlier analyses. Traced directly: it
occurs at 15:29:15-15:30:00 IST, the last 1-2 cadences of the session, where `SpotCloseFromLastCadence`
is frozen at 23477.80 across multiple cadences while the future kept ticking up (23492 -> 23499 ->
23503). Spot is a calculated index with no continuous two-sided quote (already documented in
`CadenceContext.cs`) -- this is a benign stale-tick artifact right at close, not a real market
event, and **it does not explain the still-open 10-Sep anomaly** from the correlation/simulation
work earlier this session. Falls outside the trading window anyway (force-close is 15:15 per the
standing simulation rules), so no practical trading impact, but worth documenting as a real,
if usually small, noise source for any basis-change computation given Spot's known staleness.

**Conclusion**: basis change provisionally promoted to `docs/SCORE_CANDIDATES.md` -- real,
unusually consistent signal, modest magnitude, per-cadence change preferred over level or the
rolling-window version until the expiry-day exception is understood. 10-Sep's root cause remains
unresolved after three independent checks now (DTE, VIX, and this basis investigation) -- still
just 4 confounded days; more real data is the only thing that actually resolves it.

## 2026-09-12 -- Child tables implemented: StrikeCadenceSnapshot, StrikeBandCadenceSnapshot, option-side CVD proxy

Full implementation of the schema in `docs/CHILD_TABLE_SCHEMA.md`, built the same day it was
finalized:

- **`StrikeCadenceSnapshot`** -- one row per strike/option-type/expiry at the true 15s cadence
  (1:1 with `CadenceContext`), ATM+/-3 band, both tracked expiries. Full traded-price OHLC
  (`OpenFromLastCadence`.../`CloseFromLastCadence`, distinct from the quote-based `MarkPrice`),
  Greeks/IV (`ImpliedVolatilitySolver`/`BlackScholes`, underlying = the tracked future per this
  project's existing convention), `OiBuildupClassifier` reused directly, and the new option-side
  CVD proxy.
- **`StrikeBandCadenceSnapshot`** -- configurable 5/15-min buckets, two-dimensional rollup of the
  above (across strikes in a named band AND across the 15s cadences inside the bucket). Four
  bands: `Strike3`/`Strike5`/`Strike7` (symmetric ATM+/-1/2/3) and `Itm2Atm1` (asymmetric,
  per-option-type-mirrored -- ATM + 2 strikes into ITM territory only, motivated by the user's own
  observation that real positioning skews toward buying ITM and writing OTM).
- **Option-side CVD proxy, both volume and notional** -- `CvdProxyAccumulator`, a generalized,
  reusable version of `FutureCvdProxyAccumulator`'s classification logic (kept as a separate class
  so the future's own already-tested behavior is never put at risk), extended with a notional net
  alongside the volume net. The notional field answers a real methodological question raised
  before building it: a raw call-vs-put contract-count ratio doesn't account for the two sides
  trading at different price levels (skew, moneyness) -- notional (price x volume, paired per
  strike before summing) fixes this more precisely than a band-wide price-ratio correction would,
  since it weights each strike's own contribution individually rather than applying one blanket
  factor. Residual caveat (not solved by notional itself, doesn't need to be): a structural
  price-level difference between calls and puts could still bias a *raw* CallNotionalNet-vs-
  PutNotionalNet comparison -- the fix is the same session-rank normalization already adopted for
  `FutureCvdProxy` and basis-change, applied at scoring time, not a new mechanism.

**A real bug caught by the new tests, not just a passing suite**: `OptionInstrumentState`'s first
implementation checked `HasVolumeBaseline` against a live-updating field that got set mid-cadence
during `ApplyTick`, so it flipped `true` partway through the very first cadence a token was ever
seen in, instead of only from the *next* cadence onward. Fixed to snapshot the baseline state only
in `ResetCadence()` (mirroring `CadenceHasOiBaseline`'s already-correct pattern), matching
production's own `StrikeSnapshot.VolumeDelta` convention ("null on the first cadence after
startup"). 26 new tests total across `CvdProxyAccumulatorTests`, `OptionInstrumentStateTests`,
`OffsetInBandTests` -- full suite green (462/462), solution builds with 0 warnings.

**Operational note -- repopulation needed for the 4 real days already in the database.**
`PopulateDayAsync`'s idempotency check only looks at whether `CadenceContexts` already has rows
for a date; 08-11 Sep already do (from earlier in this session), so simply re-running the
populator will skip those days entirely and never build the new strike/band tables for them. Per
the project's own established pattern for a schema-changing repopulation (same as the
`FutureCvdProxy` columns earlier this session), the existing rows need deleting first:

```
DELETE FROM "CadenceContexts" WHERE "AsOfDate" BETWEEN '2026-09-08' AND '2026-09-11';
```

(No FK constraint exists between `CadenceContexts` and the new tables at the database level --
only an application-level `CadenceContextId` reference -- so this delete alone is sufficient; there
are no `StrikeCadenceSnapshot`/`StrikeBandCadenceSnapshot` rows to separately clean up, since they
don't exist yet for these days.) Then re-run the populator as before; the migration applies
automatically on first connect.

## 2026-09-12 -- Table 2 field-by-field calculation review: NotionalDelta added, OI and Greeks confirmed

Before populating anything, user asked to explicitly settle *how* each raw metric on
`StrikeCadenceSnapshot` (table 2, the 15s per-strike table) is calculated -- not just agree that a
metric exists. Three areas reviewed:

- **Volume**: user's own instinct -- contracts AND notional, both. Contracts (`VolumeDelta`) was
  already built; **notional was only present for the CVD-classified version
  (`CvdProxyNotionalThisCadence`), missing for the blind total** -- a real gap. Added
  `NotionalDelta` (decimal?) to `StrikeCadenceSnapshot` and `_cadenceNotionalDelta` to
  `OptionInstrumentState`: sum of each tick's own `LastPrice x that tick's own volume delta`, same
  per-tick-priced technique CVD notional already uses, not `VolumeDelta x one closing MarkPrice`
  (which would misvalue a cadence where price moved across several ticks). Same null-on-first-
  cadence semantics as `VolumeDelta`, since it shares the same baseline. 3 new tests
  (`NotionalDelta_*`), including one confirming it doesn't require a depth quote the way CvdProxy
  does (only needs LastPrice + volume, both always present on a real tick).
- **OI**: confirmed contract-only, matching production's own `StrikeSnapshot` convention -- OI is a
  stock/level, not a flow, so "OI notional" (value of open positions) is a different question than
  volume's contract-vs-notional split, not something the same decision automatically extends to.
  Flagged as an available future addition, not built (not asked for).
- **Greeks**: confirmed already correct against production's own precedent -- each strike solves
  its own IV from its own MarkPrice (not a shared reference vol), matching `StrikeSnapshot.
  ImpliedVolatility`'s convention exactly (the shared-reference-vol path in production is only used
  for the theoretical-price/richness diagnostic, already excluded from this table since that
  research thread closed negative). One real, flagged simplification: the single tracked monthly
  future is used as the Black-Scholes underlying for **both** near-week and next-week option
  chains, since only one synthetic forward (near-week only, for ATM selection) exists in this
  codebase -- an expiry-specific forward per chain would be more correct but is new scope beyond
  today's ask. Left as-is pending the user's sign-off; easy to revisit if IV numbers look wrong
  once real data is populated.

Migration regenerated (not layered as a second one) since the original `AddStrikeCadenceAndBandTables`
had not yet been applied to any real database -- cleaner history for a schema still being finalized
rather than deployed. 465/465 tests green, solution builds with 0 warnings.

## 2026-09-12 -- OiNotional added: OI valued at a 3-minute averaged price, not the instantaneous one

Following the volume/OI/Greeks review above, user confirmed: yes to OI notional, with one specific
requirement -- price must be averaged over a trailing 3 minutes for the valuation, since OI on this
feed only genuinely refreshes about every 3 minutes (the same "OI refreshes slower than price"
characteristic already known for the future's own OI comparison window). Valuing OI at the raw
instantaneous MarkPrice would let ordinary 15s-level price noise dominate a figure meant to track
real OI-value change, since OI itself would just be sitting on its last known reading most of the
time regardless.

Implemented via a new `WelfordRollingWindow(3 min)` per option instrument (`OptionInstrumentState.
_oiValuationPriceWindow`), fed once per cadence with that cadence's own MarkPrice -- same "feed
before ResetCadence clears the per-cadence value, read inclusive of this cadence's own
contribution" pattern the parent CadencePopulator loop already uses for depth imbalance and CVD's
rolling windows. `OiNotional = OpenInterest x windowMean`, null until the window has 3 real minutes
of history or OI itself is unknown. 3 new tests, including one specifically proving a same-cadence
price spike doesn't leak into OiNotional (averaged 46.00 used, not the spiked instantaneous 47.00).
Migration regenerated again (still not yet applied to any real database) rather than layered.
468/468 tests green, 0 warnings.

Greeks review deferred at user's request -- volume, OI, and their calculation methods are now
fully settled for table 2 (`StrikeCadenceSnapshot`); Greeks and, after that, table 3 are next.

## 2026-09-12 -- Greeks fixed to use each expiry's own synthetic underlying, not the tracked monthly future

A friend's review (relayed by the user) argued that using the tracked monthly future as the
Black-Scholes underlying for weekly options' Greeks is a real bug, not just a simplification:
Nifty CE/PE are European, cash-settled on spot; the liquid future is monthly; those two clocks
aren't the same, and plugging the monthly future's mid into BS for a 2-9 day weekly embeds that
contract's own month-end cost-of-carry into the IV solve. Proposed fix: Black-76 on a synthetic
forward built per-expiry via put-call parity from that week's own chain.

**Checked against this exact codebase before accepting it, not taken on faith**: `NiftySignal.
Pricing/SyntheticForward.cs` already exists, and its own doc comment describes **exactly this bug,
already found and fixed once, live, on 2026-09-07** -- using the tracked future (or raw spot) as
the underlying produced put IV running 7-8 vol points below call IV at the same strike, all day, a
signature of a too-low underlying (real skew shows up across strikes, not as a same-strike
call/put split). `SyntheticForward.Compute`'s S-based parity solve (`S = K*e^(-rT) + (C-P)`), fed
into this codebase's own `BlackScholes.Calculate` convention (`dividendYield=0`), is mathematically
equivalent to Black-76 priced off that same expiry's forward (`F = S*e^(rT)`) -- confirmed by
direct derivation, not assumed. **No second pricing formula was needed** -- the existing
`BlackScholes.Calculate`/`ImpliedVolatilitySolver.Solve` pipeline already amounts to Black-76 once
the correct underlying reaches it. The friend's proposal also correctly distinguished this question
(which underlying to feed Greeks) from the separate, already-settled F11 finding (don't treat
`syntheticForward - spot` as a real cost-of-carry basis measurement) -- agreed with F11 rather than
contradicting it.

**Real oversight found and corrected**: `StrikeCadenceSnapshot`'s Greeks were built earlier today
using the monthly future, following `BlackScholes.cs`'s generic doc comment ("underlying is
expected to be the futures price") without noticing this project already has a more specific,
already-validated fix for exactly this per-expiry-weekly-options case. Not a fresh simplification
worth debating -- a reintroduction of an already-fixed bug.

**Fix**: new `CadencePopulator.ComputeSyntheticUnderlyingForChain`, generalizing the existing
near-week-only `ComputeSyntheticForward` (used for `AtmStrikeBySyntheticForward` on the parent
table, left untouched) to run once per tracked expiry via the same `SyntheticForward.Compute`
primitive. `StrikeCadenceSnapshot`'s IV/Greeks now solve against that expiry's own synthetic
underlying; null (never a fallback to the future) when the parity solve can't run yet for that
expiry that cadence. No schema change -- this only changes which price feeds an already-existing
calculation. 468/468 tests still green, 0 warnings; no new unit test added for the new wrapper
itself, consistent with the existing `ComputeSyntheticForward`'s own precedent (its strike-selection
logic isn't unit-tested either -- `SyntheticForward.Compute`'s actual math already is, thoroughly,
in `NiftySignal.Tests/Pricing/SyntheticForwardTests.cs`, and the wrapper's correctness is validated
the same way `ComputeSyntheticForward`'s always has been: against real data once populated).

## 2026-09-12 -- Table 2 populated and validated against real data; OiNotional recalibrated to 1 minute

First real population run of `StrikeCadenceSnapshot`/`StrikeBandCadenceSnapshot` (08-11 Sep,
42,000 strike rows/day, 800 band rows/day -- both match expected counts exactly given the
28-rows/cadence x 1,500-cadences and 100-bucket-instances x 2-expiries x 4-bands math). Validated
before moving on, not just trusted:

- **Coverage**: 9-11 distinct strikes touched per expiry across a whole day (not the nominal 7) is
  correct, not a bug -- ATM drifts intraday, widening the set of strikes that were ever in-band
  over the full session. Same reasoning explains `CallStrikeCount` occasionally reading above a
  band's nominal width within one Table 3 bucket.
- **Null rates**: ordered exactly as expected -- `NotionalDelta` (0.1%) < `MarkPrice`/`IV`/`Delta`
  (0-3.6%, 08 Sep expiry-day highest) < `CvdProxyVolumeThisCadence` (3.6-13%), since CVD needs a
  real nonzero volume delta *and* a two-sided quote, a stricter bar than a mark price alone.
- **Greeks -- real cross-validation, not just plausibility**: ATM IV for 09-11 Sep landed at
  9.8%-11.4%, matching the independently-measured India VIX for those same days (~10.3-12.4%,
  pulled earlier this session) -- two unrelated computations agreeing. Delta is cleanly monotonic
  across the strike ladder both directions (calls 0.720 at offset -3 down to 0.295 at offset +3;
  puts -0.280 to -0.704), IV shows a sane mild smile rather than a flat/degenerate line, and
  **next-week converges exactly as well as near-week** (no elevated null rate on the thinner
  chain) -- the main risk flagged when building the per-expiry synthetic-forward fix didn't
  materialize. 08 Sep's own expiry-day chain shows much higher, noisier IV (16.6%-76.7%), expected
  given near-zero remaining time value on expiry day.
- **OiNotional's 3-minute window was wrong** -- measured directly rather than re-assumed: gaps
  between real `OpenInterest` changes cluster tightly around 60 seconds (median 60s, p90 60s, max
  75s), identical for a liquid near-ATM strike and a thin far strike, pointing to a fixed ~60s
  broker-side refresh cycle rather than something tied to trading activity. User's call: OI is
  truth, value it as soon as real data supports the average rather than padding past what's
  needed. Recalibrated `_oiValuationPriceWindow` from 3 minutes to 1 minute; 3 tests updated to
  match (renamed, timestamps changed from 3-minute to 1-minute spacing). 468/468 green, 0 warnings.

**Next**: re-populate 08-11 Sep (same delete-and-rerun as every schema/logic change this session)
to pick up the 1-minute window, then run correlation on Table 3's already-built aggregate columns
(CVD volume/notional net, OI change, avg IV, per band and expiry) against forward price -- using
what's already there to decide what further Table 3 metrics (e.g. the volume/OI turnover ratio
discussed earlier) are actually worth prioritizing, rather than speculatively designing more
columns before knowing whether the current ones show anything.

## 2026-09-12 -- Table 3 first correlation pass: option-side CVD weak, OI-change diff the strongest cross-day finding yet

First correlation check on StrikeBandCadenceSnapshot's already-built columns (near-week expiry,
all 4 bands, both cadence granularities), against the tracked future's own forward/backward price
change -- same target series and backward/forward methodology as every other correlation check
this project has run, joined via CadenceContextId. `scripts/table3-correlation.sql`.

**Option-side CVD (CallCvdProxyVolumeNet - PutCvdProxyVolumeNet, and the notional equivalent):
weak and sign-inconsistent.** Pooled forward correlation near zero (-0.03 to +0.09 across bands/
granularities); per-day breakdown (Strike7, 5-min buckets) shows why -- 08 Sep -0.14, 09 Sep
+0.05, 10 Sep +0.29, 11 Sep -0.06 at fwd-5m. Sign flips across days, the same signature of "not a
reliable relationship yet" already seen for `FutureDepthImbalance`. Not concluding edge on this
formulation.

**OI-change diff (CallOiChangeSum - PutOiChangeSum): strong, and the most cross-day-consistent
correlation found in this project so far.** At the 15-minute forward horizon:

| Band | 08 Sep | 09 Sep | 10 Sep | 11 Sep |
|---|---|---|---|---|
| Itm2Atm1 | 0.216 | 0.063 | 0.405 | 0.184 |
| Strike3 | 0.225 | 0.115 | 0.424 | 0.159 |
| Strike5 | 0.238 | 0.099 | 0.425 | 0.154 |
| Strike7 | 0.247 | 0.094 | 0.425 | 0.126 |

16/16 band-day combinations positive -- nothing else tested this session has matched this level of
sign consistency (basis-change was the previous best, 4/4 days but ~10x smaller magnitude).
Two notable structural observations: (1) band choice barely matters here -- all four bands give
nearly identical readings on the same day, suggesting the signal concentrates at/near ATM rather
than needing width; (2) **10 Sep is the best day for this metric** (0.40-0.43), a real contrast to
every future-side metric tested earlier this session (CVD, trend efficiency, depth imbalance),
where 10 Sep was consistently the worst or most anomalous day. If this holds up, it's a genuinely
complementary signal for the composite (real information on a day the future's own metrics went
quiet), not a redundant one.

Same standing caveat as everywhere else -- 4 days is a checkpoint, not a verdict. Next steps not
yet run: 5-minute forward horizon, next-week expiry, individual call/put OI-change levels (is the
diff doing real work or would either side alone show the same thing), and eventually a scored
formula once the evaluation cycle is further along.

## 2026-09-12 -- Table 3 CVD deep dive: contract vs notional near-identical, both unstable except one thin cell

Per user instruction (one metric at a time, contract vs notional, both cadences, both expiry
weeks): `scripts/cvd-contract-vs-notional.sql` (pooled, all 4 bands) then a per-day breakdown
(Strike7, both weeks/cadences).

**Contract vs notional CVD diff track each other closely in every cell tested** -- same sign,
similar magnitude throughout (e.g. ThisWeek/15min/08 Sep: contract +0.334, notional +0.614). The
choice between the two framings doesn't change the read; not a decision point worth spending more
time on for this metric.

**Pooled numbers were masking real day-to-day sign flips, not smoothing over a stable weak
signal** -- per-day (Strike7, fwd15m):

| | 08 Sep | 09 Sep | 10 Sep | 11 Sep |
|---|---|---|---|---|
| ThisWeek, 5-min | -0.09 | +0.08 | +0.34 | -0.01 |
| ThisWeek, 15-min | +0.17 | +0.03 | +0.28 | -0.39 |
| NextWeek, 5-min | -0.05 | -0.22 | -0.21 | +0.03 |
| NextWeek, 15-min | -0.07 | -0.43 | -0.15 | -0.07 |

Three of four combinations flip sign across days -- same "not a reliable relationship yet"
signature already established for `FutureDepthImbalance` and the earlier raw option CVD check.
**Verdict: ThisWeek CVD-diff, either cadence, either framing, does not clear the bar for edge.**

**One thin, not-yet-confirmed exception**: NextWeek at 15-minute cadence is negative on all 4 days
(-0.067, -0.433, -0.153, -0.074) -- the only cross-day-consistent cell in this whole CVD check, a
contrarian read matching the reversion flavor already found for trend-reversion and basis-change.
Weighted lower than the OI-change-diff finding: much thinner sample (23-25 points/day vs. 72-75),
smaller and more variable magnitude. Not concluded either way -- would need more days before
trusting it, flagged rather than dropped.

Per the standing evaluation cycle: CVD (as formulated so far -- call-minus-put net, either
contract or notional, either band) is **not** being carried forward as a confirmed Table 3
candidate on this check. Moving to the next Table 3 metric per user's explicit one-at-a-time
instruction.

## 2026-09-12 -- Duplication bug confirmed fixed; CVD-vs-option-price redone on clean data

Re-populated 08-11 Sep after the cascade-FK fix. Verified directly: 24,000 ATM rows (matching
expectation exactly, was 48,000 before). Re-ran `cvd-contract-vs-notional.sql` (the future-price
version) on the clean data -- **numbers matched the pre-fix report exactly, to three decimals**.
Root cause confirmed: those queries join back through `CadenceContexts` via `CadenceContextId`,
and orphaned duplicate rows pointed at deleted IDs that no longer match anything in the live
table, so the join itself silently excluded them. Not every query was so lucky.

**The CVD-vs-option-price query (previous section) WAS corrupted by the duplication** -- it
computed the option's own price series directly from `StrikeCadenceSnapshots` using row-offset
`LEAD`/`LAG` (20 rows = 5 min, assuming uniform 15s spacing), with no join back to
`CadenceContexts` to filter orphans. Duplicate rows silently halved what "20 rows" actually
spanned in real time. Confirmed by the n mismatch: 2400 -> 300 after cleanup, exactly 8x (2
duplicate copies each of the CVD row, the call-price row, and the put-price row fanning out
against each other in the join) -- not the 2x a merely-duplicated-but-otherwise-correct query
would show.

**Clean re-run, CVD-diff vs. the option's own price** (call/put price via forward-filled MarkPrice,
same row-offset technique, now correct since the underlying data has no duplicates):

| | ThisWeek 5m | ThisWeek 15m | NextWeek 5m | NextWeek 15m |
|---|---|---|---|---|
| CVD-vol vs call price (fwd5m/fwd15m) | -0.02/+0.02 | -0.06/+0.00 | +0.01/+0.06 | +0.27/+0.09 |
| CVD-vol vs put price (fwd5m/fwd15m) | +0.18/+0.07 | +0.17/+0.12 | -0.02/-0.05 | -0.26/-0.04 |

A real directional signal should move call and put price in *opposite* directions. **ThisWeek
fails this check** -- call and put move the same direction, incoherent, no story (consistent with
already ruling ThisWeek out against future price). **NextWeek/15-min/fwd-5m passes it cleanly**:
call +0.268, put -0.260, near-mirror-image -- and this is the same cell that already stood out as
the one cross-day-consistent finding against the *future's* price. Two different target variables
agreeing is a real convergence, not a coincidence. Still the thinnest sample of anything tested
(n=100, 25/day) -- flagged as the one live thread from the CVD investigation, not confirmed.

**Process note**: going forward, `DELETE FROM "CadenceContexts" ...` alone is sufficient before a
repopulate -- the cascade FK now cleans up dependent StrikeCadenceSnapshot/StrikeBandCadenceSnapshot
rows automatically. No more three-table manual deletes needed.

## 2026-09-12 -- PCR (put/call volume ratio): the strongest, most coherent finding this session

Per user instruction, first Table 3 metric after CVD: log(CallVolumeSum/PutVolumeSum) -- blind
(unclassified) activity ratio, a genuinely different question from CVD (which is aggressor-
classified net flow within each side; PCR just compares raw busyness between sides). Log-ratio,
not raw ratio, matching this project's established preference for symmetric-around-zero ratios.
Correlated against the option's own price (per the 2026-09-12 methodology correction), separately
against ATM call price and ATM put price -- a real signal should move them in *opposite*
directions. `scripts/pcr-vs-option-price.sql`.

**Pooled, all 4 bands, both weeks, both cadences: 15 of 16 cells show the coherent sign pattern**
(call price correlation positive, put price correlation negative), the cleanest result of anything
tested this session -- CVD never got close to this level of directional coherence.

**Per-day breakdown (Itm2Atm1 band, fwd-15m, call/put correlation)**:

| | 08 Sep | 09 Sep | 10 Sep | 11 Sep | Coherent? |
|---|---|---|---|---|---|
| ThisWeek, 5-min | +0.177/-0.239 | +0.205/-0.186 | +0.304/-0.297 | +0.277/-0.256 | 4/4 |
| NextWeek, 5-min | +0.264/-0.137 | +0.088/-0.091 | +0.181/-0.167 | +0.248/-0.230 | 4/4 |
| NextWeek, 15-min | +0.035/-0.092 | +0.247/-0.226 | +0.166/-0.157 | +0.120/-0.088 | 4/4 |
| ThisWeek, 15-min | -0.131/+0.021 | +0.368/-0.348 | +0.431/-0.434 | +0.097/+0.017 | 2/4 |

Three of four week/cadence combinations perfectly coherent on every day, with larger magnitudes
(0.09-0.43) than anything else tested this session (CVD, OI-change-diff included) -- ThisWeek/
5-min is the standout, every day in the +0.18 to +0.30 / -0.19 to -0.30 range. **The best candidate
found in this entire investigation so far.**

**One caveat, reported rather than smoothed over**: ThisWeek at 15-minute cadence breaks coherence
on 2 of 4 days (08 Sep flips sign entirely, 11 Sep goes weak/same-direction on the put leg) --
notably, the *same week's* 5-minute version is rock-solid, suggesting the 15-minute bucket's ATM
band composition may shift too much within the window for this specific metric. Not yet checked:
notional-based PCR's per-day picture (pooled numbers tracked the volume version closely, matching
the same "contract vs notional don't differ much" pattern already found for CVD, but not verified
per-day yet), and whether Strike3/5/7's weaker pooled correlations (vs. Itm2Atm1's strongest) hold
the same day-level coherence.

## 2026-09-12 -- PCR notional version checked: same pattern, consistently weaker than volume

Per-day notional PCR (Itm2Atm1 band, log(CallNotionalSum/PutNotionalSum) vs option price)
reproduces the exact same coherent/incoherent pattern as the volume version day-for-day --
ThisWeek/15-min still breaks on 08 Sep and 11 Sep, every other week/cadence combination still 4/4
coherent -- but runs systematically weaker in magnitude across nearly every cell (e.g. ThisWeek/
5-min call-price correlation: volume 0.177-0.304 vs notional 0.102-0.231, roughly 30-40% smaller).

**Verdict: volume (contract-based) PCR is the version to carry forward, not notional.** Different
conclusion from CVD (where contract and notional were near-interchangeable, no clear winner) --
here there's an actual winner, and no reason to prefer notional's weaker read. `docs/
SCORE_CANDIDATES.md` should record PCR-volume as the confirmed formulation once this line of
investigation is fully closed out (still pending: understanding the ThisWeek/15-min breakdown,
and checking whether Strike3/5/7's weaker pooled correlations hold the same day-level pattern as
Itm2Atm1).

## 2026-09-12 -- PCR band comparison: Itm2Atm1 clearly outperforms the wider symmetric bands

Checked whether Strike3/5/7 hold the same day-level coherence Itm2Atm1 showed for PCR-volume.
They don't -- Itm2Atm1 is a real, meaningful improvement, not noise.

**Coherence count (days where call price and put price moved opposite directions, fwd-15m):**

| | ThisWeek/5m | NextWeek/5m | NextWeek/15m | ThisWeek/15m |
|---|---|---|---|---|
| Itm2Atm1 | 4/4 | 4/4 | 4/4 | 2/4 |
| Strike3 | 3/4 | 2/4 | 3/4 | 2/4 |
| Strike5 | 3/4 | 3/4 | 4/4 | 2/4 |
| Strike7 | 2/4 | 3/4 | 4/4 | 2/4 |

Itm2Atm1 strictly beats or ties every wider band in every combination. The wider bands consistently
lose coherence specifically on **09 Sep at 5-minute cadence, both weeks** (e.g. NextWeek/5m/09 Sep:
Strike3 -0.047/+0.038, Strike5 -0.064/+0.042, Strike7 -0.033/+0.015, all wrong-signed, while
Itm2Atm1 stayed coherent at +0.088/-0.091 that same day).

**Plausible real mechanism, not just a statistical artifact**: Itm2Atm1 is the only band that
excludes OTM strikes entirely. Diluting with OTM activity (present in Strike3/5/7) appears to
genuinely weaken the PCR signal -- consistent with the user's own earlier instinct that real
positioning skews toward buying ITM and writing/selling OTM, meaning OTM activity likely reflects
different, less directionally-informative dynamics (premium collection, low-conviction
speculative flow) that a PCR-style ratio picks up as noise once included.

**Conclusion: Itm2Atm1 is the preferred band for PCR going forward.** This resolves the
band-choice question but does not resolve the separate ThisWeek/15-min issue -- even Itm2Atm1
struggles there (2/4) -- which still needs its own investigation.

## 2026-09-12 -- OI-change-diff redone against option price: direction reverses, ThisWeek fairly consistent, NextWeek unstable

Per user instruction, OI-change-diff (`CallOiChangeSum - PutOiChangeSum`) redone against the
option's own price -- it was only ever tested against the future's price before (16/16 band-day
combinations positive, the strongest finding prior to PCR). `scripts/oi-diff-vs-option-price.sql`.

**Per-day breakdown (Itm2Atm1, fwd-15m, call/put correlation):**

| | 08 Sep | 09 Sep | 10 Sep | 11 Sep |
|---|---|---|---|---|
| ThisWeek, 5-min | -0.001/+0.047 | -0.049/+0.050 | -0.164/+0.168 | -0.097/+0.076 |
| ThisWeek, 15-min | +0.051/-0.047 | -0.110/+0.121 | -0.524/+0.574 | -0.205/+0.106 |
| NextWeek, 5-min | +0.111/-0.092 | +0.111/-0.058 | +0.040/-0.039 | -0.232/+0.263 |
| NextWeek, 15-min | -0.208/-0.005 | +0.033/+0.061 | -0.224/+0.263 | -0.347/+0.292 |

**Two findings, neither matching the earlier future-price story:**

1. **Direction reverses.** Against the future, positive OI-diff correlated with the future rising
   (read as bullish). Against the option's own price, positive OI-diff mostly correlates with the
   *call falling and the put rising* -- the opposite. Plausible mechanism: OI change doesn't
   distinguish buyer from seller -- rising call OI can reflect call *writing* (bearish/neutral,
   selling premium) as easily as call buying, which the future-price-only check couldn't
   distinguish. The option-price version is arguably the more decision-relevant one if the
   eventual strategy trades options directly, since that's what determines P&L on an options
   position, not the future's own move.
2. **ThisWeek is fairly consistent (7 of 8 combinations share the same call-down/put-up
   direction); NextWeek is not** -- it flips within cadences (5-min: three days one way, 11 Sep
   opposite; 15-min: two days incoherent, two days matching ThisWeek's direction).

**Verdict: not ready to confirm against option price.** Coherent enough to reveal a real,
substantively interesting reinterpretation (the sign itself may need to flip from the
future-price-based reading), but weaker and less stable than PCR's clean result, and NextWeek's
instability is a real, unresolved problem, not noise to wave away. Recorded as a genuine
re-finding, not a dead end -- the underlying signal may still be real, just needs more work
(more days, understanding why NextWeek differs from ThisWeek) before promotion.

## 2026-09-12 -- MA smoothing tested across PCR, OI-diff, CVD (volume+notional): weakens every one

Per user challenge (this dimension was never tested despite the project already using rolling-sum
smoothing elsewhere, e.g. `FutureCvdProxyNet5Min`/`Net15Min`) -- a 3-bucket rolling average (MA-3,
an overlapping ~15-min lookback on 5-min buckets, updating every 5 minutes) tested against the raw
bucket value for all four Table 3 candidates so far, ThisWeek only, Itm2Atm1 band.
`scripts/ma-smoothing-experiment.sql`.

| Metric | Raw (fwd5m) call/put | MA-3 (fwd5m) call/put | MA-3 (fwd15m) call/put |
|---|---|---|---|
| PCR-volume | +0.175/-0.190 | +0.116/-0.118 | +0.155/-0.139 |
| OI-diff (contract) | -0.105/-0.032 | -0.030/-0.037 | -0.033/+0.037 |
| CVD-volume | -0.031/-0.056 | -0.003/-0.050 | +0.012/+0.020 |
| CVD-notional | -0.089/+0.124 | -0.050/+0.059 | -0.031/+0.049 |

**Every metric weakened under smoothing, none improved.** PCR (the strongest signal) loses roughly
a third of its magnitude. Plausible mechanism: these signals may capture something transient (a
short-lived imbalance right before a move) rather than a persistent state -- averaging in two
older buckets dilutes exactly the recent information that mattered, rather than filtering noise.

**Conclusion: MA smoothing does not help any Table 3 metric tested so far -- the raw, single most
recent bucket is the better formulation.** Not pursuing EMA as a follow-up: the trend (less
smoothing performs better, down to zero smoothing) makes it unlikely a different decay shape would
reverse this, though revisit if there's a specific reason to expect otherwise.

## 2026-09-12 -- OiChangeNotional added (Table 2 + Table 3), migration ready, repopulate needed

Per user request, built the notional counterpart to OI change (mirrors exactly how NotionalDelta
mirrors VolumeDelta): `StrikeCadenceSnapshot.OiChangeNotional = OpenInterestDelta x` the same
1-minute-averaged price `OiNotional` already uses (refactored `RecordCadenceAndComputeOiNotional`
to return both values from one shared average, avoiding feeding the window twice). Aggregated to
`StrikeBandCadenceSnapshot.CallOiChangeNotionalSum`/`PutOiChangeNotionalSum`, same SUM-across-
strikes-and-cadences pattern as the existing contract version. 4 new tests (470/470 total green),
migration `AddOiChangeNotional` (3 columns, inspected clean). Not yet correlated -- needs the
standard delete-`CadenceContexts`-and-repopulate cycle first (cascade FK now handles child-table
cleanup automatically, so this is back to the simple one-table delete).

**Also decided (per user instruction, real-trading reasoning)**: OI-based metrics restricted to
**ThisWeek expiry only** going forward -- near-week OI reflects live positioning for imminent
price action (high gamma, deep liquidity); next-week OI more plausibly reflects calendar-spread or
longer-horizon hedging activity, which isn't a reactive signal for the next 5-15 minutes. This is
also the likely explanation for why NextWeek's OI-change-diff was unstable while ThisWeek's was
comparatively more consistent (see the prior OI-vs-option-price section).

**Sign convention confirmed with real economic grounding**: the user's own trading experience
("market movers are mostly sellers, occasionally buyers") directly explains why OI-diff's
relationship with option price came back reversed from the future-price version -- rising call OI
more often reflects call *writing* (bearish/neutral) than buying, since OI doesn't distinguish
which side of a new position was the aggressor.

## 2026-09-12 -- OI-notional-diff checked, ThisWeek only: same story as contract, not a clear upgrade

`CallOiChangeNotionalSum - PutOiChangeNotionalSum`, ThisWeek only (per today's restriction),
Itm2Atm1 band, against option price:

| Cadence | 08 Sep | 09 Sep | 10 Sep | 11 Sep |
|---|---|---|---|---|
| 5-min (fwd15m) | -0.002/+0.094 | -0.054/+0.042 | -0.146/+0.145 | -0.093/+0.080 |
| 15-min (fwd5m) | -0.530/+0.548 | +0.241/-0.249 | -0.300/+0.315 | -0.289/+0.222 |
| 15-min (fwd15m) | +0.079/+0.001 | -0.131/+0.138 | -0.457/+0.503 | -0.215/+0.120 |

Same call-down/put-up direction as the contract version on 3 of 4 days at every cadence/horizon,
with 09 Sep the same recurring outlier (near-zero at 5-min, flips entirely at 15-min/fwd5m).
Magnitudes track the contract version closely (15-min/10 Sep: -0.457/+0.503 here vs. contract's
-0.524/+0.574) -- notional slightly weaker, same instability.

**Verdict: not a clear upgrade over the contract version.** Same pattern CVD showed (contract and
notional interchangeable) rather than PCR's pattern (a real winner). Default to OI-contract
(simpler, no extra price-averaging dependency) unless a specific reason favors notional.

**OI-diff status overall**: still not confirmed as a candidate -- ThisWeek shows a real, if
imperfect (09 Sep breaks it), directional pattern in both contract and notional framings; MA
smoothing already ruled out as a fix (see prior section). Remaining open question: what's
different about 09 Sep specifically that breaks this metric on that one day, which hasn't been
investigated yet.

## 2026-09-12 -- Option CVD closed: five formulations, tightened classification, corrected methodology, same instability

Full redo per the friend's review: touch-rule classification (code change, `CvdProxyAccumulator`
now requires a print to reach or cross the actual bid/ask, not just cross the midpoint -- a wide
option spread makes a mid-based guess low signal-to-noise), plus two methodology fixes found while
implementing it -- strike-identity-safe forward windows (partition every LEAD/LAG by StrikePrice,
not just option type/week, so a forward price never silently splices across an ATM re-centering
event) and a time guard (null the forward change if the real elapsed time to the looked-up row
falls outside a 30-second tolerance of the intended 5/15-minute horizon).

**Five formulations tested, ThisWeek only, Itm2Atm1 band, all against the strike-identity-corrected
target:**

| Check | 08 Sep | 09 Sep | 10 Sep | 11 Sep |
|---|---|---|---|---|
| Call CVD vs. call price (MarkPrice) | -0.084 | +0.090 | -0.047 | +0.050 |
| Put CVD vs. put price (MarkPrice) | -0.199 | +0.059 | +0.152 | +0.000 |
| Vol-flow (call+put) vs. call price | +0.050 | -0.061 | -0.170 | +0.032 |
| Vol-flow (call+put) vs. put price | -0.220 | -0.009 | +0.150 | -0.045 |
| Call CVD vs. call price (LTP target) | -0.109 | +0.091 | -0.039 | +0.042 |
| Put CVD vs. put price (LTP target) | -0.210 | +0.051 | +0.143 | +0.002 |

Every formulation shows the same signature: sign flips day to day, no stable direction. The
LTP-vs-MarkPrice check rules out target choice as the cause -- both give essentially the same
(unstable) answer, confirming the instability is intrinsic to the classified-flow signal itself on
this instrument, not an artifact of the price series it's measured against.

**Verdict: option CVD is closed as a candidate**, per the friend's own stated exit criterion (five
clean formulations coming back unstable is enough to conclude the metric is the wrong resolution
for this market, not under-tested). The touch-rule and strike-identity fixes are real, worthwhile
improvements independent of this outcome -- kept in the codebase (`CvdProxyAccumulator` stays
tightened; the strike-identity-safe correlation pattern is reused for any future option-price
check, not just CVD). The future's own `FutureCvdProxy` (already validated, unrelated accumulator
class) is untouched by any of this and remains a confirmed candidate.

Closed the same way the put/call price-tracking residual thread was closed earlier in this
project's history: a genuine negative result from a rigorous, multi-angle test, not a dead end to
discard quietly.

## 2026-09-12 -- OI-diff promoted to watch candidate; option-level depth imbalance built

**OI-diff**: per explicit instruction, promoted from "flagged, not yet confirmed" to a full watch
candidate in `docs/SCORE_CANDIDATES.md` -- real signal, real economic grounding (market movers as
net option sellers), not yet as proven as PCR, kept on the list to keep accumulating evidence
rather than re-litigated from scratch each time.

**Option-level depth imbalance**: the last remaining "flagged but not built" item from the original
schema design. Discovered `FutureDepthAccumulator` was already fully generic in implementation
(nothing future-specific in its logic, only its name) -- renamed to `DepthImbalanceAccumulator`
rather than duplicating identical code, and reused directly for options via a new instance on
`OptionInstrumentState`. Wired into the same tick-processing block CvdProxy already uses, but fed
unconditionally on every two-sided-quote tick (not gated by volume -- resting liquidity is sampled
regardless of whether anything traded, matching the future's own convention exactly).

New fields: `StrikeCadenceSnapshot.TotalBidQty`/`TotalAskQty`/`DepthImbalanceFromLastCadence`
(per-strike, this cadence, averaged over every real tick -- same "averaging a ratio differs from a
ratio of averages" reasoning as the future's version); `StrikeBandCadenceSnapshot.
CallDepthImbalanceAvg`/`PutDepthImbalanceAvg` (plain average across strikes-in-band and
cadences-in-bucket, unweighted -- no established reason yet to OI-weight this the way `CallAvgIv`
is). Migration `AddOptionDepthImbalance` (5 columns, inspected clean). 2 new tests confirming the
wiring (fed regardless of volume, cleared on reset). 475/475 total tests green, 0 warnings.

Not yet correlated -- needs the standard repopulate first.

## 2026-09-12 -- Depth imbalance diff: the strongest, most broadly coherent finding this session

First correlation check on the newly-built option depth imbalance, per user instruction.
`CallDepthImbalanceAvg - PutDepthImbalanceAvg`, against option price, using the corrected
strike-identity-safe/time-guarded methodology established during the CVD redo.
`scripts/depth-imbalance-vs-option-price.sql`.

**ThisWeek, fwd-15m, Itm2Atm1: 4/4 days coherent, largest magnitudes found this session:**

| | 08 Sep | 09 Sep | 10 Sep | 11 Sep |
|---|---|---|---|---|
| Call price | +0.263 | +0.293 | +0.466 | +0.145 |
| Put price | -0.277 | -0.260 | -0.414 | -0.338 |

**Band comparison -- unlike PCR, band choice barely matters here, and every band is strongly
coherent:**

| Band | 08 Sep | 09 Sep | 10 Sep | 11 Sep |
|---|---|---|---|---|
| Itm2Atm1 | +0.263/-0.277 | +0.293/-0.260 | +0.466/-0.414 | +0.145/-0.338 |
| Strike3 | +0.221/-0.242 | +0.323/-0.296 | +0.415/-0.353 | +0.122/-0.273 |
| Strike5 | +0.235/-0.228 | +0.272/-0.244 | +0.366/-0.313 | +0.113/-0.290 |
| Strike7 | +0.270/-0.204 | +0.261/-0.232 | +0.332/-0.282 | +0.103/-0.291 |

16/16 band-day combinations coherent -- a different pattern from PCR (which needed the ITM-only
band specifically to work) and from OI-diff (band-agnostic but weaker/less stable). Depth
imbalance's signal is broad across the whole ATM+/-3 range, not concentrated near ATM.

At fwd-5m, 3 of 4 days coherent (11 Sep breaks, both legs negative that day) -- the 15-minute
horizon is cleanly the more reliable one, consistent with the pattern already seen for other
metrics this session.

**NextWeek is internally coherent on all 4 days too, but the direction splits**: 08 Sep matches
ThisWeek's sign (call+/put-), while 09/10/11 Sep consistently run the opposite way (call-/put+).
A real, separate pattern -- not investigated further yet, does not block confirming ThisWeek.

**Verdict: strongest candidate found in this entire investigation** -- larger magnitudes than PCR,
broader band-robustness than PCR, real economic intuition (resting bids under an option reflect
waiting buyers). Confirmed on ThisWeek. NextWeek's sign-split is an open question for later.

## 2026-09-12 -- OI level sum added (Table 3), unlocking PCR-OI and volume/OI turnover ratio

Per discussion clarifying that "PCR" (as built and confirmed) is volume-based, not OI-based --
the classic Indian-markets PCR (Put OI / Call OI, a position-level ratio) and the earlier-discussed
volume/OI turnover ratio are both distinct, untested candidates that share the same missing
ingredient: Table 3 only had OI *change* summed (`CallOiChangeSum`), never OI *level* summed.

Added `StrikeBandCadenceSnapshot.CallOiSum`/`PutOiSum` -- total open interest across the band's
strikes, at the bucket's own boundary cadence (a level quantity, not summed across the bucket's
cadences, same convention `CallAvgIv` already uses). Migration `AddOiLevelSum` (2 columns,
inspected clean). 475/475 tests still green (no new accumulator logic needed -- this reuses the
existing per-strike `OpenInterest` field and the already-existing boundary-row selection).

Sequencing (user's own call, confirmed): build once, test PCR-OI first, then discuss the volume/OI
turnover ratio with real data already available rather than purely theoretically.

Not yet correlated -- needs the standard repopulate first.

## 2026-09-12 -- PCR-OI (level-based) tested: real per-day signal, pooled number misleading

`log(CallOiSum/PutOiSum)` against option price, same methodology as everything else.
`scripts/pcr-oi-vs-option-price.sql`.

**Pooled correlation looked weak** (-0.02 to -0.15 for call price, +0.01 to +0.17 for put,
across bands/weeks) -- coherent in sign everywhere (8/8), but small.

**Per-day tells a very different story:**

| | 08 Sep | 09 Sep | 10 Sep | 11 Sep |
|---|---|---|---|---|
| NextWeek (Itm2Atm1): call/put | -0.345/+0.303 | -0.190/+0.203 | -0.298/+0.292 | -0.098/+0.134 |
| ThisWeek (Itm2Atm1): call/put | -0.182/+0.137 | +0.016/+0.003 | -0.390/+0.404 | -0.095/+0.137 |

7 of 8 combinations coherent, real magnitude (0.10-0.40) -- comparable to OI-diff's own strength,
same reversed direction (call OI-heavy -> call price down, put price up), consistent with the same
underlying mechanism (market makers as net option sellers/writers).

**Root cause of the pooled/per-day mismatch, checked directly rather than assumed**: the raw
log-ratio's own range varies enormously day to day (08 Sep: -1.43 to -0.43; 09 Sep: -2.42 to
+0.05) -- OI accumulates over a contract's entire life, so each day sits at a different baseline
level unrelated to that day's own price dynamics. Pooling across days with different baselines
dilutes a real within-day relationship -- the same "level vs. change" trap already hit with basis
level (dominated by DTE decay) and CVD's cumulative-day version (dominated by stale morning
activity). Consistent, recurring lesson: a raw level, pooled across sessions with different
baselines, systematically understates a real per-day relationship.

**Practical consequence for scoring**: PCR-OI cannot use the raw log-ratio pooled across days --
needs within-day normalization (session-rank based, matching the plan already noted for other
level-type candidates) before it's usable across multiple sessions.

**Status**: real signal found, comparable to OI-diff in coherence and strength, but not yet a
clean "confirmed" the way volume-PCR and depth imbalance are -- the level/baseline issue needs
the same normalization treatment before real use, and this is a genuinely new methodological point
(the first per-day/pooled mismatch driven specifically by cross-day OI-level baseline drift, not
day-to-day sign flips like every other case so far).

---

## 2026-09-12 -- PCR-OI/OI-diff redundancy check, finalized (delegated decision)

User explicitly delegated this: "I will let you decide pcr-oi/oi and finalise it first." This was
the exact open question flagged in both candidates' entries -- how much of PCR-OI's information is
redundant with OI-diff. Ran `scripts/pcroi-vs-oidiff-redundancy.sql`: cross-correlated the two
metrics directly against **each other** (not against option price), ThisWeek, Itm2Atm1, 5-min, all
4 days.

**Level-vs-level** (`pcr_oi_log` vs. raw `OiDiff`):

| Date | n | corr |
|---|---|---|
| 08 Sep | 75 | -0.152 |
| 09 Sep | 75 | -0.429 |
| 10 Sep | 75 | -0.312 |
| 11 Sep | 75 | -0.333 |

Every day negative, moderate magnitude -- a real, coherent relationship, but far short of the
0.7-0.8+ that would mean "these are the same metric wearing two names."

**Delta-vs-delta** (PCR-OI's own bar-to-bar change vs. `OiDiff`) -- the decisive check. This is the
theoretically apt comparison: `OiDiff` *is* the flow that mechanically moves PCR-OI's underlying OI
stock each cadence, so if PCR-OI were just OI-diff re-expressed, its own change should track
OI-diff almost mechanically.

| Date | n | corr |
|---|---|---|
| 08 Sep | 75 | +0.154 |
| 09 Sep | 75 | -0.059 |
| 10 Sep | 75 | +0.006 |
| 11 Sep | 75 | -0.093 |

No consistent sign, magnitudes near zero -- indistinguishable from noise. The mechanical link does
**not** show up at the bar-to-bar level.

**Root cause, understood not just observed**: PCR-OI's delta is a *log ratio of levels* --
`Δlog(CallOi/PutOi)` is approximately each side's own *relative* (%) OI change, normalized by that
side's own OI base. `OiDiff` is a *raw, unnormalized* absolute difference. When the call-side and
put-side OI bases differ in size (routine, since strikes accumulate OI unevenly over their whole
life), the same raw `OiDiff` produces very different `pcr_oi_delta` values depending on which side
is bigger -- the normalization structurally decouples the two at the per-cadence level, even though
they're built from the same underlying OI numbers.

**Verdict: PCR-OI and OI-diff are genuinely distinct, not redundant. Finalized as two separate
watch candidates, neither subsumed into the other.** PCR-OI is a base-normalized *level/stock*
reading; OI-diff is an unnormalized *flow* reading. Their moderate, coherent level-vs-level
correlation reflects a real but partial relationship (today's fresh OI flow and the day's overall
OI-stock skew are related, as expected), not duplication. Both `docs/SCORE_CANDIDATES.md` entries
updated to close this open question; PCR-OI's remaining open item (does session-rank normalization
recover the pooled signal cleanly) stays open on its own.

---

## 2026-09-12 -- Volume/OI turnover ratio tested: CLOSED, no edge (end-of-day artifact)

Fourth axis, distinct from PCR-volume (cross-side activity), OI-diff (cross-side flow change), and
PCR-OI (cross-side level) -- **same-side** activity intensity: `CallVolumeSum/CallOiSum` and
`PutVolumeSum/PutOiSum`, each side against its own resting OI, not a call-vs-put comparison at all.

**Design note, established before testing:** a "directional" cross-side version
(`log(CallTurnover/PutTurnover)`) is algebraically identical to `PcrVolumeLog − PcrOiLog` --
already-tracked metrics recombined, not a new ingredient. Not built. Turnover also has no
inherent sign (volume and OI are both ≥0), so it was tested against the **magnitude** of each
side's own forward price move (`ABS(fwd_5m)`, `ABS(fwd_15m)`), not signed change -- the question
being asked is "does high turnover coincide with bigger moves," not "which direction."

**Evidence (`scripts/turnover-ratio-vs-option-price.sql`, 4 bands x 2 weeks x 2 cadences x 2
forward windows, 08-11 Sep)**: pooled numbers looked promising at first glance -- ThisWeek/5-min
cadence/5-min-forward showed **put-side ~0.50-0.56 across all four bands**, call-side ~0.14-0.16.
Band-flat (as expected -- bands mostly share the same near-ATM strikes). NextWeek was
uniformly near-zero to slightly negative across every combination (clean negative from the start,
no follow-up needed there). The 15-min forward window and the 15-min cadence bucket were both weak
(~0.0-0.13) even within ThisWeek/5-min.

**Per-day breakdown of the promising ThisWeek/5-min/5-min-forward cell (the standard "never trust
a pooled number" check) immediately exposed the problem**:

| Date | call_turnover_vs\|fwd5m\| | put_turnover_vs\|fwd5m\| |
|---|---|---|
| 08 Sep | 0.257 | **0.851** |
| 09 Sep | 0.010 | -0.049 |
| 10 Sep | 0.160 | 0.144 |
| 11 Sep | 0.240 | 0.052 |

08 Sep's 0.851 alone was inflating the pooled 0.499-0.555 across an otherwise weak, inconsistent
set of days. Pulled the raw top rows for 08 Sep directly: the extreme turnover values (5.25, 4.29,
3.10, 2.89) all cluster at **15:15-15:30** -- the last 15-20 minutes of the session, when positions
get squared off before close (naturally elevated volume relative to resting OI, plus naturally
choppier closing prices). Re-ran 08 Sep excluding `Timestamp::time >= 15:10` and the correlation
**collapsed from 0.851 to 0.243** -- right in line with the other three days' noise-level range.

**Verdict: CLOSED, no edge.** With the end-of-day artifact excluded, per-day correlations for both
sides sit in a noise band with no consistent sign or magnitude (put: -0.04 to +0.24; call: +0.02 to
+0.25, all 4 days). The apparent pooled signal was a single-day, single-mechanism (pre-close
unwind) artifact, not a general intraday relationship between turnover and price-move magnitude.
Not promoted to any candidate status -- same closure standard as CVD (5 formulations tested, all
unstable). If end-of-day-specific dynamics are ever worth their own investigation (a distinct
question from this one -- "does elevated closing-window turnover predict something about the next
session," not "does turnover predict intraday moves"), that would be a fresh, separately-scoped
hypothesis, not a resurrection of this one.

---

## 2026-09-12 -- F58 (demand/supply exhaustion via cross-leg reaction proportionality): CLOSED

User's own idea, tested per the "try it, correlate it, reach a verdict" discipline. Design: does
the option chain's Strike7-band (+-3 strikes) average call/put price react proportionally to a
15-minute Nifty (future) move, and does a WEAK/anomalous reaction predict a REVERSAL in the next
15-minute window? Distinct from the already-closed residual-autocorrelation research thread (that
tested one leg's own Delta+Gamma+Theta residual mean-reversion; this tests cross-leg proportionality
of reaction as a forward-looking signal).

**First pass (raw per-row ratio, `ChangeInOption / ChangeInFuture`, averaged) produced nonsensical
numbers** -- average ratios flipping sign across days (-0.14 to +0.16), nowhere near the expected
~0.5. Root cause: averaging a ratio row-by-row is statistically unstable near a small denominator,
even with a floor (>=15 points) -- a classic ratio-of-noisy-quantities pitfall. Redone with
`REGR_SLOPE`/`CORR` (the standard way to measure "rupees moved per point of underlying move"),
which avoids that instability entirely.

**Even the basic sanity check failed, before ever testing the reversal claim.** ATM-only (the
cleanest possible case for a delta-driven relationship), 5-minute window:

| Date | Week | Call beta | Call corr | Put beta | Put corr |
|---|---|---|---|---|---|
| 08 Sep | ThisWeek | -0.089 | -0.070 | -0.005 | -0.004 |
| 08 Sep | NextWeek | -0.231 | -0.187 | 0.071 | 0.082 |
| 09 Sep | ThisWeek | 0.013 | 0.015 | -0.018 | -0.030 |
| 09 Sep | NextWeek | 0.010 | 0.011 | -0.031 | -0.052 |
| 10 Sep | ThisWeek | -0.120 | -0.106 | 0.038 | 0.048 |
| 10 Sep | NextWeek | -0.136 | -0.118 | 0.012 | 0.015 |
| 11 Sep | ThisWeek | 0.154 | 0.220 | -0.031 | -0.059 |
| 11 Sep | NextWeek | 0.116 | 0.164 | -0.074 | -0.149 |

An ATM call's real delta (~0.5) predicts a consistently positive beta around 0.4-0.6, ATM put
consistently negative. Instead: **sign flips day to day** (negative 08/10 Sep, near-zero 09 Sep,
positive 11 Sep), magnitudes well under 0.5, put side essentially noise. The +-3-strike band
average at 15 minutes (the original spec) showed the same pattern, if anything weaker -- neither
widening/narrowing the window nor the band fixed it.

**Initial verdict (WRONG, later corrected below): CLOSED -- premise doesn't hold.** This was based
on a real bug, not real evidence -- see the correction immediately following.

---

## 2026-09-12 -- F58 REOPENED: the "premise doesn't hold" verdict was a bug, not a finding

User asked to chase one more thing before finalizing: is `FutureCloseFromLastCadence` itself a
noisy/basis-drifting reference at 15s granularity, which would explain why even the ATM-only
sanity check above showed a weak, sign-flipping relationship? Investigating that question found
something much more important than expected.

**The future is not noisy.** `spot_move5m` vs `future_move5m` correlation across all 4 days:
0.842-0.937 -- spot and future track each other closely, similar volatility (stddev ~8-13 points
both). Future does have far fewer ticks per 15s cadence than spot (27-29 vs 162-171) and a
handful of genuinely dead cadences (51 on 08 Sep, tapering to 0 by 11 Sep), but this is nowhere
near enough to explain the earlier near-zero, sign-flipping ATM-vs-future correlations.

**The real cause, found by comparing against spot instead of future (same weak, sign-flipping
result, if anything slightly worse) and then re-reading my own query**: the ATM-only sanity check
(and the original `demand-supply-exhaustion.sql`'s underlying window computation) filtered
`WHERE StrikeOffsetFromAtm = 0` and then computed `LEAD`/forward-fill **without partitioning by
StrikePrice** -- exactly the strike-identity splicing bug already found and fixed once this
session for CVD (`cvd-redo-touch-rule.sql`'s whole reason for existing). Over a 5-15 minute window
with the future/spot moving ~8-13 points per 5 minutes against ~50-point-wide strikes, ATM
re-centers often enough that "the ATM price 15 minutes later" was frequently a genuinely different
physical contract than "the ATM price now" -- the forward "change" was partly measuring a jump
between two unrelated strikes' price levels, not a real price move on one contract.

**Re-ran the ATM-only sanity check with the correct partitioning** (forward-fill and `LEAD`
partitioned by `StrikePrice`, anchored only on rows that were genuinely ATM at time T -- the exact
template `cvd-redo-touch-rule.sql` already established):

| Date | Week | Call beta (future) | Call corr (future) | Put beta (future) | Put corr (future) |
|---|---|---|---|---|---|
| 08 Sep | ThisWeek | 0.528 | 0.735 | -0.497 | -0.490 |
| 08 Sep | NextWeek | 0.413 | 0.898 | -0.385 | -0.904 |
| 09 Sep | ThisWeek | 0.548 | 0.947 | -0.375 | -0.944 |
| 09 Sep | NextWeek | 0.540 | 0.950 | -0.389 | -0.950 |
| 10 Sep | ThisWeek | 0.528 | 0.930 | -0.402 | -0.931 |
| 10 Sep | NextWeek | 0.515 | 0.939 | -0.425 | -0.945 |
| 11 Sep | ThisWeek | 0.594 | 0.947 | -0.356 | -0.933 |
| 11 Sep | NextWeek | 0.564 | 0.956 | -0.384 | -0.951 |

Textbook-clean once fixed: beta consistently 0.41-0.59 (call) / -0.36 to -0.50 (put), matching a
real ATM delta; correlation 0.72-0.96, every single day. Spot gave near-identical numbers (not
tabulated) -- confirming the underlying-reference choice was never the issue.

**Corrected reversal/exhaustion retest** (`scripts/f58-corrected-reversal-test.sql` -- restricted
to `|future_move1| >= 15` points, a real trending move; residual = actual 15-min reaction minus
the naive "half the move" expectation (`beta=0.5`), correlated against the *next* 15-minute
window's move):

| Date | Week | n | Call reaction corr | Put reaction corr | Call residual vs next move | Put residual vs next move |
|---|---|---|---|---|---|---|
| 08 Sep | ThisWeek | 301 | 0.842 | -0.867 | -0.336 | -0.418 |
| 08 Sep | NextWeek | 301 | 0.976 | -0.985 | +0.531 | -0.433 |
| 09 Sep | ThisWeek | 625 | 0.988 | -0.987 | -0.111 | -0.236 |
| 09 Sep | NextWeek | 625 | 0.988 | -0.989 | -0.067 | -0.238 |
| 10 Sep | ThisWeek | 497 | 0.979 | -0.980 | -0.271 | -0.453 |
| 10 Sep | NextWeek | 497 | 0.980 | -0.986 | -0.167 | -0.388 |
| 11 Sep | ThisWeek | 533 | 0.975 | -0.964 | -0.343 | -0.440 |
| 11 Sep | NextWeek | 533 | 0.985 | -0.983 | -0.313 | -0.422 |

**Put-side residual is coherent across all 8 rows (-0.24 to -0.45)**: a put that under-reacts
(weaker fall than its own delta implies -- the user's original "anomaly") is followed by the trend
continuing against itself in the next window. This matches the original hypothesis directly.

**Call-side residual points the other way (7 of 8 rows negative, one outlier: +0.531 on 08 Sep
NextWeek)**: it's a call that OVER-reacts (moves more than the naive half-move expectation, not
less) that precedes reversal, not a weak call reaction. Put together, the coherent story across
both legs is: **call over-confirmation (chasing/FOMO flow pushing the call past what delta
justifies) combined with put under-confirmation (writers/sellers stepping in expecting mean
reversion) together precede a reversal** -- a real, economically sensible "euphoria/blow-off"
signature, but a refined version of the user's original symmetric "either leg weak = anomaly"
framing, not a literal confirmation of it.

**Status: REOPENED as a watch candidate, not confirmed.** Real, moderate, mostly-coherent signal
(put side especially) on only 4 days with heavily overlapping 15s-cadence windows (same
effective-sample-size caveat noted elsewhere this session) and one real outlier on the call side.
Worth tracking with more days before promoting further. `docs/SCORE_CANDIDATES.md` updated to
reflect reopened/watch status, correcting the earlier premature closure.

---

## 2026-09-12 -- Strike-identity bug also found in PCR and OI-diff's original scripts: both INVALIDATED

User's direct question after the F58 correction: does finding this bug (again) mean other
already-decided candidates should be retested? Answered by actually checking, not guessing --
listed every saved script's timestamp and grepped each option-price query for the
`PARTITION BY ... "StrikePrice"` + time-guard pattern that marks the corrected template.

**Timestamps told the story before a single query ran.** `cvd-redo-touch-rule.sql` (the file that
established the fix) was written 16:45. Two scripts predate it:
`pcr-vs-option-price.sql` (15:46) and `oi-diff-vs-option-price.sql` (16:00) -- both written before
the fix existed. Two others postdate it: `depth-imbalance-vs-option-price.sql` (17:05) and this
session's own `turnover-ratio-vs-option-price.sql` (18:20).

**Read each file directly to confirm, rather than trusting the timestamp alone**:
- `pcr-vs-option-price.sql` and `oi-diff-vs-option-price.sql`: both filter
  `WHERE "StrikeOffsetFromAtm" = 0` and then compute `LEAD` partitioned only by
  `"AsOfDate", week_label, "OptionType"` -- **no `StrikePrice`, no time-guard**. Confirmed bug in
  both, identical to F58's.
- `depth-imbalance-vs-option-price.sql` and `turnover-ratio-vs-option-price.sql`: both partition
  every `FIRST_VALUE`/`LEAD` by `"StrikePrice"` and apply the `StrikeOffsetFromAtm = 0` filter
  *after* the lookahead is computed, with the `BETWEEN INTERVAL '...' AND INTERVAL '...'`
  time-guard present. Confirmed safe -- their CONFIRMED and CLOSED verdicts stand unaffected.
- CVD's own final closure used the corrected template throughout (that's the whole reason
  `cvd-redo-touch-rule.sql` exists) -- unaffected, its original pre-fix numbers were already
  superseded before being trusted.

**PCR retested** (`scripts/pcr-vs-option-price-corrected.sql`, full band x cadence sweep):

| Week | Cadence | Band | callprice fwd5m | callprice fwd15m | putprice fwd5m | putprice fwd15m |
|---|---|---|---|---|---|---|
| NextWeek | 5 | Itm2Atm1 | 0.008 | -0.017 | 0.015 | 0.058 |
| NextWeek | 5 | Strike3 | -0.037 | -0.022 | 0.062 | 0.056 |
| NextWeek | 5 | Strike5 | 0.008 | 0.026 | 0.017 | 0.008 |
| NextWeek | 5 | Strike7 | 0.047 | 0.066 | -0.022 | -0.026 |
| ThisWeek | 5 | Itm2Atm1 | -0.020 | -0.071 | -0.015 | 0.102 |
| ThisWeek | 5 | Strike3 | -0.014 | -0.093 | -0.008 | 0.120 |
| ThisWeek | 5 | Strike5 | -0.016 | -0.098 | 0.014 | 0.122 |
| ThisWeek | 5 | Strike7 | -0.022 | -0.116 | 0.029 | 0.133 |
| (15-min cadence rows: same pattern, all in -0.07 to +0.12 range) | | | | | | |

Pooled across every band and both cadences: **noise, -0.12 to +0.13, nowhere near the reported
0.09-0.43.** Per-day breakdown (Itm2Atm1/5-min) tells a more nuanced story:

| Date | Week | callprice fwd5m | putprice fwd5m |
|---|---|---|---|
| 08 Sep | NextWeek | -0.395 | +0.408 |
| 08 Sep | ThisWeek | -0.306 | -0.069 |
| 09 Sep | NextWeek | -0.082 | +0.073 |
| 09 Sep | ThisWeek | +0.009 | +0.040 |
| 10 Sep | NextWeek | -0.048 | +0.050 |
| 10 Sep | ThisWeek | -0.250 | +0.237 |
| 11 Sep | NextWeek | +0.131 | -0.098 |
| 11 Sep | ThisWeek | -0.012 | +0.087 |

08 Sep and 10 Sep show real, moderate, coherent signal -- but in the **reversed** direction (call
side busier correlates with call price *falling*), the same convention already established for
OI-diff and PCR-OI, not the "bullish" direction originally reported and confirmed. 09 Sep is
near-zero both weeks; 11 Sep NextWeek goes the other way entirely.

**OI-diff retested** (`scripts/oi-diff-vs-option-price-corrected.sql`, ThisWeek/Itm2Atm1/5-min):

| Date | callprice fwd5m | putprice fwd5m |
|---|---|---|
| 08 Sep | +0.113 | -0.411 |
| 09 Sep | -0.069 | +0.051 |
| 10 Sep | +0.322 | -0.351 |
| 11 Sep | -0.044 | -0.047 |

08 Sep and 10 Sep again carry the real magnitude, but this time in the **naive "bullish"**
direction (call OI building faster correlates with call price *rising*) -- the exact opposite of
the originally-reported "reversed" direction that the whole "market makers are net sellers"
narrative was built around. 09/11 Sep are near-zero/incoherent.

**Verdict: PCR's CONFIRMED status and OI-diff's reversed-sign explanation are both INVALIDATED.**
Neither was a data-quality problem with the underlying reference (already ruled out during the
F58 investigation) -- both were the exact same strike-identity splicing bug, present in scripts
written in the ~1-hour window before the fix was established. `docs/SCORE_CANDIDATES.md`'s PCR and
OI-diff entries are marked invalidated/under-retest, with their original (wrong) evidence kept
below the marker for the historical record, not deleted. **Not yet re-decided** -- the corrected
per-day numbers hint at something real on 08/10 Sep for both metrics, but in each case a different
sign than what was reported, and 09/11 Sep don't cooperate either way. A full corrected battery
(every band, both cadences, notional vs. volume, and for OI-diff a genuine test of which sign
direction is real rather than assuming either) is needed before either metric gets a new status --
not done in this pass, flagged as the clear next step.

**Process lesson, stated plainly**: this session's own "verify against the actual file, don't trust
memory or a prior verbal summary" discipline caught this -- the fix was invented, used going
forward, and never back-applied to the two scripts written just before it existed. The check that
found this (comparing file timestamps against the fix's own introduction, then reading each file
directly rather than assuming safety from its later confirmed status) is worth repeating any time a
methodology fix lands mid-session: check every already-written query against the new template, not
just the ones written afterward.

---

## 2026-09-12 -- PCR and OI-diff: full corrected battery, new verdicts

User asked for the complete corrected re-run (all bands, both cadences, notional, per-day, proper
sign determination) rather than a single spot-check. `scripts/pcr-full-battery-corrected.sql` and
`scripts/oi-diff-full-battery-corrected.sql` -- both bands x cadences x weeks x volume-or-notional
(PCR) / contract-or-notional (OI-diff), per day (64 rows each, full output kept in the script
files/git history, summarized here).

### PCR -- real signal, but on 2 of 4 days, not 4 of 4

Across every band, both cadences, both volume and notional versions:
- **08 Sep and 10 Sep**: strongly, consistently **reversed-direction** (call busier -> call price
  falls, put price rises) on literally every band/cadence/framing tested that day -- e.g. 10 Sep
  ThisWeek/5-min: Itm2Atm1 -0.250/+0.237, Strike3 -0.270/+0.272, Strike5 -0.271/+0.274, Strike7
  -0.282/+0.287 (volume); near-identical notional numbers. 08 Sep NextWeek/5-min: -0.395/+0.408
  down to -0.215/+0.234 across bands, same direction throughout.
- **09 Sep**: noise. Small, inconsistent, sometimes wrong-signed depending on cadence (e.g.
  ThisWeek/5-min shows call *positive* +0.009 to +0.072, the opposite of 08/10 Sep's direction;
  ThisWeek/15-min-bucket shows the reversed direction again, weakly). Does not cooperate either way.
- **11 Sep**: tends the **opposite** (naive bullish) direction, most clearly on NextWeek (call
  +0.10 to +0.13, put -0.05 to -0.10 across bands) and ThisWeek/15-min-bucket (call +0.08 to +0.20).
- **Volume vs. notional**: now essentially interchangeable -- the original "volume beats notional by
  30-40%" finding does not replicate; matches CVD's own established contract-vs-notional pattern.
- **Band comparison**: no consistent winner. 08 Sep NextWeek has Itm2Atm1 clearly strongest
  (-0.395 vs. Strike7's -0.215); 10 Sep ThisWeek has the *wider* bands slightly beating Itm2Atm1
  (Strike7 -0.282 vs. Itm2Atm1's -0.250) -- the original "Itm2Atm1 uniquely wins" claim doesn't
  hold up; band preference is day-dependent, not a stable property of the metric.

**New verdict: PCR is not a clean 4/4-coherent CONFIRMED candidate.** Real, substantial,
internally-consistent signal exists on a majority-but-not-unanimous basis (2 of 4 days strongly
reversed-direction across every framing tested that day), materially weaker and less clean than
depth imbalance's actual 16/16 band-day coherence. **Downgraded to watch candidate**, same tier as
OI-diff and PCR-OI, sign = reversed (net evidence favors reversed over naive: 2 strongly-reversed
days outweigh 1 day leaning naive), with 09 Sep flagged as an unexplained break.

### OI-diff -- the original "market makers as sellers" narrative does not survive

ThisWeek (the scoped-in week):
- **08 Sep and 10 Sep** (both bucket cadences): consistently **naive bullish** direction (call OI
  rising -> call price *rising*, put price falling) -- e.g. 10 Sep ThisWeek/5-min-bucket: contract
  +0.322/-0.351, notional +0.301/-0.334; 08 Sep 15-min-bucket: contract +0.36 to +0.46 (call),
  -0.22 to -0.30 (put), across all 4 bands.
- **09 Sep**: noise at the 5-min bucket (call/put both near-zero, both *negative* -- not a
  coherent directional signal either way), but the *15-min* bucket shows the same naive-bullish
  direction as 08/10 Sep, moderately (contract +0.11 to +0.13 call, -0.12 to -0.16 put).
- **11 Sep**: the *only* day showing the originally-reported **reversed** direction cleanly, and
  only at the 15-min bucket (contract -0.25 to -0.26 call, +0.21 to +0.25 put); at the 5-min
  bucket it's closer to noise (both sides negative, incoherent).
- **Net**: 3 of 4 days lean naive-bullish (08, 10 clearly; 09 weakly at 15-min bucket only), 1 of 4
  (11 Sep) leans reversed. This is the **opposite** of the original "7 of 8 combinations reversed"
  claim -- that claim was built entirely on the buggy script.
- **NextWeek**: genuinely mixed either direction, no clean story -- this part of the original
  reasoning (ThisWeek reflects reactive positioning, NextWeek doesn't) holds up independent of the
  bug; the scope restriction to ThisWeek stays justified.
- **Contract vs. notional**: interchangeable, same as PCR and CVD -- no clear winner.

**New verdict: the "confirmed, inverted from the naive reading" sign claim and its
"market-makers-as-sellers" explanation are retracted.** The corrected evidence now favors the
**naive bullish** reading more often than not (3 of 4 days), the opposite of what was reported and
explained. **Remains a watch candidate** (real, moderate signal on most days, ThisWeek-only scope
still sound) but the sign is genuinely unresolved/day-dependent, not confidently reversed --
needs more days before either direction is trusted, and the economic narrative needs rebuilding
from scratch once the sign is actually settled.

### Cross-metric observation, not yet investigated

**09 Sep is now flagged as the anomalous day by two independently-corrected metrics** -- it was
already OI-diff's own "recurring break, not root-caused" day before today, and corrected PCR
singles it out the same way (weak/wrong-signed specifically on 09 Sep, at the finer cadence).
Not proof of anything on its own, but worth remembering rather than dismissing as coincidence --
if a third metric independently flags 09 Sep, that would be worth a dedicated look at what was
structurally different about that session (range-bound vs. trending, realized vol, expiry
proximity) rather than treating each metric's 09 Sep weakness as an unrelated one-off.

Both `docs/SCORE_CANDIDATES.md` entries updated with these new verdicts, replacing the
"invalidated, under retest" placeholder.

---

## 2026-09-13 -- Depth imbalance vs future price: closes the external-review gap, strengthens the finding

External review (2026-09-12) correctly flagged that option depth imbalance was the one confirmed
candidate never checked against the future's price -- every other Table 3 candidate (CVD, PCR,
OI-diff) got both targets tested at some point. Ran `scripts/depth-imbalance-vs-future-price.sql`:
`CallDepthImbalanceAvg - PutDepthImbalanceAvg` against the tracked future's own forward price
change, same time-guarded forward-fill methodology used throughout (no strike-identity exposure
here -- depth imbalance is a band-level aggregate, not a per-strike LEAD computation).

**ThisWeek, fwd-15m (the already-established reliable horizon), all 4 bands:**

| Date | Itm2Atm1 | Strike3 | Strike5 | Strike7 |
|---|---|---|---|---|
| 08 Sep | 0.292 | 0.245 | 0.258 | 0.289 |
| 09 Sep | 0.275 | 0.304 | 0.253 | 0.245 |
| 10 Sep | 0.457 | 0.409 | 0.361 | 0.339 |
| 11 Sep | 0.226 | 0.195 | 0.199 | 0.195 |

**Positive across every band, every one of the 4 days** -- 16/16 coherent, same direction as the
option-price confirmation (positive depth-imbalance-diff correlates with the future rising, same
as it correlates with the ATM call rising / put falling). fwd-5m is weaker and 11 Sep flips slightly
negative there, exactly matching the already-documented "fwd-15m reliable, fwd-5m coherent on 3 of
4 days" pattern from the option-price test -- an independent replication of that same pattern
against a different target, not a new inconsistency.

NextWeek stays weak and directionally inconsistent against the future too (mostly small magnitude,
sign flips across days) -- consistent with its already-documented flipped-sign-vs-ThisWeek behavior
in the option-price test. Reinforces that NextWeek doesn't carry a reliable signal here regardless
of which target it's checked against.

**Verdict: the gap is closed, and it closes in depth imbalance's favor.** Unlike PCR and OI-diff,
which fell apart or reversed sign once properly retested, depth imbalance's signal replicates
cleanly against a second, independent target with the same sign and comparable magnitude. This is
the strongest evidence yet that depth imbalance's CONFIRMED status is real, not an artifact of
which price series it happened to be checked against.

---

## 2026-09-13 -- External review's remaining findings: two fixed, two logged PENDING

Second external review (2026-09-12) covered Greeks methodology, F32's design, and the metric lab's
documentation hygiene. Every specific code claim was verified against actual current source (not
taken on the review's word) via a dedicated read-only pass before any action -- all six checked out
as accurate. Actions taken:

**Fixed and verified (`dotnet build` + `dotnet test`, 475/475 green after each):**

- **`BlackScholes.cs`'s stale class comment** ([BlackScholes.cs:8-13](../NiftySignal.Pricing/BlackScholes.cs:8)) said "underlying is expected to be the futures price" -- both current call sites (`CadencePopulator`, `LiveFeatureEngine.ComputeUnderlyingPrice`) feed the put-call-parity synthetic S instead, and have since 2026-09-07. Corrected to name the actual convention and cross-reference `SyntheticForward.Compute`. Comment-only, zero behavior change.

- **`CadencePopulator.BuildStrikeRow`'s `OiBuildupClassifier` input** ([CadencePopulator.cs:479-486](../NiftySignal.BacktestData/CadencePopulator.cs:479)) was classifying each strike's OI buildup against that strike's own `MarkPriceDelta`, not the underlying's move -- the exact F17 bug already fixed in `LiveFeatureEngine` (which correctly uses `spotPriceChange`), left un-mirrored in the backtest-analysis populator. Fixed by threading a new per-cadence `spotPriceChange` (current cadence's spot minus the previous cadence's, one shared value per cadence since spot has no per-strike identity -- mirrors `PriorKnownMarkPrice`'s "value at the end of last cadence" pattern but at the day-loop level, not per-instrument) through `BuildStrikeRows` -> `BuildStrikeRow`, and classifying against that instead. `MarkPriceDelta` stays as its own stored diagnostic column (still useful, just no longer feeds this classification) -- its doc comment and `OiBuildup`'s own doc comment both updated to describe the corrected input. This only affects `StrikeCadenceSnapshot.OiBuildup`, a diagnostic column in the offline correlation lab -- not fed into any score or live trading decision, and not the same thing as `CallOiChangeSum`/`PutOiChangeSum` (OI-diff's own source columns, entirely unaffected by this fix). **Requires a repopulate** to see corrected `OiBuildup` values on rebuilt data, same as every other schema/logic change this session -- the user's own call on when to run it.

- **`BacktestRunner.ApplyDynamicHybrid`'s same-bar rank leakage** ([BacktestRunner.cs:262-269](../NiftySignal.Backtest/BacktestRunner.cs:262)) called `sessionRankTracker.Add(...)` before reading `ValueAtPercentile(...)` from the same tracker, so the current bar's own value was already included in the distribution used to threshold itself. Reordered: read first, add after -- the honest "was this rank clearable before this bar arrived" semantics a live session-rank gate would actually see. Mild in practice (one bar barely moves a growing session distribution) but the correct behavior regardless. Lab-only code (`NiftySignal.Backtest`), no live-path exposure, and `DynamicHybrid` itself remains uncommitted per standing instruction.

**Logged as `PENDING`, not fixed -- deliberately deferred, not overlooked:**

- **`LiveFeatureEngine.ComputeUnderlyingPrice`'s spot-fallback** ([LiveFeatureEngine.cs:647-663](../NiftySignal.Host/LiveFeatureEngine.cs:647)) falls back to raw spot (reintroducing the documented too-low-S bias) instead of nulling out like `CadencePopulator`'s equivalent does. Not fixed: the honest version of this fix changes the method's return type to nullable and requires reviewing null-propagation across all 7 live call sites (basis, GEX, every Greek) -- a real refactor of live scoring code, not a comment or a single classifier-input swap. Practical exposure judged low today (fires only transiently, early-session; everything it feeds through GEX/Vanna/Charm is already zero-weighted in the live composite) -- revisit deliberately, with its own review of every call site, not folded into an unrelated cleanup pass.

- **ATM offset (`StrikeOffsetFromAtm`) is spot-based while Greeks price against synthetic S** ([CadencePopulator.cs:425-441](../NiftySignal.BacktestData/CadencePopulator.cs:425)) -- a real mismatch when basis is nonzero (occasionally puts "ATM" one strike off from the strike nearest the S actually being priced against). Not fixed: this is a deliberate, documented, codebase-wide strike-selection convention (also used live), not a narrow bug -- fixing it means deciding whether to move strike selection everywhere to an S-based ATM, or leave live trading on spot-based ATM and only rebase this analysis-only bucketing. A real design decision, flagged for a dedicated pass rather than decided inside a fix-up round.

**One review claim independently found to be incorrect, not acted on**: the claim that the
PCR-OI/OI-diff redundancy check needed re-running post-splice-fix. Checked
`pcroi-vs-oidiff-redundancy.sql` directly -- it correlates `CallOiSum/PutOiSum`- and
`CallOiChangeSum/PutOiChangeSum`-derived series against each other, never touches option
`MarkPrice` or the forward-window computation that had the strike-identity bug. That verdict
("distinct, not redundant") stands unchanged.

---

## 2026-09-13 -- Timing methodology spot-check (user-requested, prompted by a friend's review)

A second friend's review raised a real, important, and extremely common trap in this kind of
research: correlating a metric against the price *already printed* at the same instant (or the
move inside the same window that built the metric) instead of against the price move *after* the
metric is known. Their prescription (forward, non-overlapping, time-guarded, per-day, no ATM
splice) was correct as a principle. Before accepting or rejecting the claim that this project had
been making that mistake, verified it directly against the actual saved SQL rather than trusting
either the review or memory.

**Grepped all 24 saved scripts containing a `CORR()` call for the forward-window pattern.** Every
one that correlates against a price series uses either `LEAD(...)` (row-offset) or an equivalent
`RANGE ... N FOLLOWING` window frame to build the target, always paired with a `BETWEEN INTERVAL`
time-guard, always joined on the metric's own timestamp so the target window starts exactly where
the metric's own window ends. The two scripts without `LEAD` (`pcroi-vs-oidiff-redundancy.sql`,
correlating two OI-derived series against each other) don't need one -- neither side is a price
target. `trend-efficiency-and-vix-forward-correlation.sql` is worth naming specifically: it was
built to test exactly this same-time-vs-forward distinction on VIX change, computing both a
forward and a backward/contemporaneous version side by side -- the result (VIX correlates
contemporaneously, forward ~= 0) is already recorded in `SCORE_CANDIDATES.md` as "concluded,
contemporaneous only, not promoted." That's the review's exact failure mode, already caught, on a
different metric, before this review was ever written.

**User asked for a hand-checkable spot-check on one real metric (PCR, 10 Sep, ThisWeek, Itm2Atm1,
5-min) rather than accepting the script-level argument alone.** Built it in three parts:

1. **Metric window is genuinely backward.** Recomputed `CallVolumeSum` from raw 15s `VolumeDelta`
   rows restricted to `(T-5min, T]` and it matched the persisted `StrikeBandCadenceSnapshot` value
   exactly at all 5 boundaries checked (4,415,905 / 3,215,485 / 3,200,015 / 1,710,540 / 6,558,435)
   -- `earliest_tick_in_window` to `latest_tick_in_window` never crosses past `T`.
2. **Caught a real bug building the check itself, which doubled as proof of why the fix matters.**
   A naive attempt at "the ATM call's price at T and T+15m" (filtering `StrikeOffsetFromAtm = 0`
   independently at each end, no `StrikePrice` partition) showed the "ATM" strike genuinely
   flipping between 23450 and 23400 in 4 of 5 boundaries checked -- spot crossed the strike midpoint
   between 10:00:45 and 10:01:00, causing a ~126-to-154 "price change" that was really just two
   different contracts' prices, not a real move.
3. **Redone with the actual production template** (`LEAD` partitioned by `StrikePrice`, anchored
   only where `StrikeOffsetFromAtm=0` at `T`) -- every boundary now tracks one continuous contract,
   exactly 15 minutes apart (`actual_elapsed = 00:15:00` every time), producing materially
   different, more plausible numbers (-6.90, +5.13, +6.48, +5.90, -1.83) than the naive version.

**Verdict: the review's principle is real and worth being paranoid about, but it does not describe
a defect in this project.** Every correlation this session has run already uses the forward,
non-overlapping, time-guarded, strike-identity-safe, per-day methodology the review recommends --
confirmed by grep across every saved script and by hand against raw numbers on one real metric, not
just asserted. No re-test needed. Scripts kept: `spot-check-pcr-timing.sql` (the 4-part hand-check,
including the deliberately-reproduced naive bug for comparison).

---

## 2026-09-13 -- IV skew tested: CONFIRMED among non-expiry ThisWeek days

First test of `PutAvgIv - CallAvgIv` (Table 3), a genuinely different kind of candidate from
everything else tested this session -- a pricing-surface read, not an activity/position read.
Ran `scripts/iv-skew-vs-price.sql`: all 4 bands, both cadences, both targets (option price and
future), level and cadence-to-cadence delta, per day.

**Band sensitivity was immediately structural, not incidental.** Only `Itm2Atm1` (pairing ITM
calls against ITM puts -- genuinely different moneyness each side) showed any coherent signal.
The symmetric bands (Strike3/5/7, same strikes both sides) were noise throughout -- expected,
since same-strike call/put IV is already parity-consistent (the 2026-09-07 fix) and carries little
independent skew information once correctly matched.

**First look at ThisWeek/Itm2Atm1 showed 08 Sep as an outlier at 5-min cadence** (near-zero/weak
against both targets, while 09-11 Sep were clearly positive). Initially called this a "watch
candidate" pending explanation, matching the caution applied to OI-diff's own unexplained 09 Sep
break. **User immediately identified the real cause: 08 Sep is expiry day.** Verified directly --
`CadenceContexts.NearestExpiryDate` for 08 Sep equals `AsOfDate` itself (0 DTE), while 09-11 Sep
all show `NearestExpiryDate = 2026-09-15` (a fresh weekly cycle). This is a real, identified,
principled regime split (0-DTE gamma/theta/pin-risk dynamics genuinely differ from a normal week),
not an unexplained mystery -- excluding it from scope is the same kind of deliberate decision
already applied to OI-diff's NextWeek exclusion, not cherry-picking convenient data.

**Re-read with 08 Sep excluded as its own regime: 3 of 3 clean days, unanimous, every cadence,
every target, every horizon:**

| Date | 5-min vs option (fwd5/15) | 5-min vs future (fwd5/15) | 15-min vs option (fwd5/15) | 15-min vs future (fwd5/15) |
|---|---|---|---|---|
| 09 Sep | 0.273 / 0.461 | 0.231 / 0.404 | 0.450 / 0.566 | 0.356 / 0.530 |
| 10 Sep | 0.235 / 0.239 | 0.181 / 0.185 | 0.291 / 0.444 | 0.090 / 0.376 |
| 11 Sep | 0.238 / 0.313 | 0.245 / 0.310 | 0.224 / 0.327 | 0.306 / 0.319 |

15-min-vs-option is the standout cell (0.45-0.57), comparable to depth imbalance's strongest
readings and larger than anything OI-diff or PCR-OI ever produced. Level carries the signal, not
the cadence-to-cadence delta (delta correlations mostly under 0.15, no stable sign) -- same
pattern as OI-diff/PCR-OI. Sign is reversed from classic skew theory (rising put-side IV
correlates with price *rising*, not falling) -- the fourth metric this session where the textbook
direction didn't hold (after OI-diff, PCR-OI, PCR), reported as tested, not explained away.
NextWeek stays messier (only 09/11 Sep coherent), consistent with the same ThisWeek-vs-NextWeek
dynamics difference already established for OI-diff.

**Verdict: CONFIRMED among non-expiry ThisWeek days**, with the honest caveat that this rests on
only 3 days (fewer than depth imbalance's 4-day/16-combination base) -- an early confirmation to
keep testing against, not a fully settled result. Expiry-day (0 DTE) IV skew behavior is flagged
as its own separate, not-yet-tested question, deliberately kept out of this metric's scope rather
than folded in as a weak day.

---

## 2026-09-13 -- Live-vs-lab metric inventory

User asked to compare every metric actually used live against everything tested in the Table 2/3
lab, find what's untested, and check for duplicates. Read the actual current source (not memory):
`NiftySignal.Scoring/ScoreComponentInputs.cs`, `ScoreWeights.cs`, `RatioComponentInputs.cs`,
`RatioScoreWeights.cs`, and every `Compute*` method in `LiveFeatureEngine.cs` (dispatched to an
Explore agent for the exact-formula extraction, then cross-checked by hand against the lab's own
candidate list).

**Two findings stood out immediately:**

1. **`OiBuildupNet` -- the single largest live weight (0.3125, ~31% of everything nonzero) -- has
   never been tested in the lab in any form.** It is not the same metric as OI-diff: OiBuildupNet
   nets every strike's OI change into one sign-weighted vote based on spot direction
   (`Σ(sign(spotPriceChange)·|ΔOI|)`, `OiBuildupClassifier.Classify` per strike, ATM±2, both option
   types), while OI-diff is a blind call-side-vs-put-side difference with no per-strike
   classification at all. OI-diff's watch-candidate status says nothing about this metric.
2. **Live `Pcr` (weight 0.19, second-largest) and the lab's "PCR" are not the same metric despite
   the identical name.** Live `Pcr` = `putOI/callOI`, a raw (non-log) **OI-based** ratio on ATM±2
   -- conceptually much closer to PCR-OI (level-based, log-ratio of OI, already found to need
   within-day normalization before its raw form means anything) than to the lab's PCR (which is
   volume-based). The second-heaviest live weight is scoring an un-normalized level ratio of
   exactly the shape PCR-OI already showed is misleading when read raw/pooled.

**Full formula extraction (Explore agent, verified against `LiveFeatureEngine.cs` line-by-line)**:

| Live component | Weight | Formula | Band/scope | Lab equivalent |
|---|---|---|---|---|
| `OiBuildupNet` | 0.3125 | `Σ(sign(spotChange)·\|ΔOI\|)` | ATM±2 (`PersistedStrikeBand`) | None |
| `Pcr` | 0.19 | `putOI/callOI` (raw) | ATM±2 | Closest to PCR-OI, not lab's PCR |
| `FuturesBasis` | 0.1425 | `futureMid − spot` (raw level) | n/a | Lab tested the *change* version only |
| `IvSkew` | 0.1425 | `putIv − callIv` | Dynamic OTM, ±1 expected-move strike | Lab-confirmed version uses ITM Itm2Atm1 -- different strikes |
| `PriceMomentum` | 0.0 (cut) | Raw future momentum | n/a | Already resolved live-side (lagging) |
| `DepthImbalance` | 0.1625 | `callImbalance − putImbalance`, each `(bid−ask)/(bid+ask)` | NTM (2 nearest strikes) | Option depth imbalance -- CONFIRMED, band-insensitive (16/16) -- likely covered |
| `VixChange` | 0.05 | Negated 30-min VIX delta | n/a | Tested, contemporaneous only, **not promoted** -- mismatch: zero-edge metric still carries live weight |
| `GammaExposure` | 0.0 | `Σ(±gamma×OI)` | Full chain | None |
| `VolumePcr` | 0.0 | `putNotional/callNotional` (raw) | Full chain | Related to lab's PCR, different scope/transform |
| `SpreadRatio` | 0.0 | `putSpread%/callSpread%` (unweighted mean) | Full chain | None |
| `VannaExposure` / `CharmExposure` | 0.0 each | Structurally identical to GammaExposure, different Greek | Full chain | None |
| `CvdProxy` | 0.0 | Midpoint-rule quote classification | Full chain | Option CVD -- CLOSED, but tested with the *touch-rule* fix; live still uses the pre-fix midpoint rule (harmless today at weight 0, but never patched) |
| `StraddleRichness` | 0.0 | ATM straddle, actual move minus 1st-order (Δ+Θ) predicted | ATM only | Related to F58 and `ResidualDifference`, distinct formula from both |

Ratio-composite (all equal-weighted 0.2, none evidence-derived): `NotionalVolumeRatio` (ATM±5,
third PCR-volume variant), `SizedOiFlowRatio` (ATM±5, fourth OI-comparison variant),
`ResidualDifference` (per-leg 2nd-order Taylor, related to F58's simpler version),
`IvSkew25Delta` (true 25-delta interpolated strike, ratio not difference -- third IV-skew variant),
`SpreadRatioAtm` (ATM±2, OI-weighted -- second spread-ratio variant).

**Structural finding: four economic ideas have 3-4 independently-coded variants each**, scattered
across the original 14-component composite, the 5-metric ratio composite, and the lab, never
reconciled -- IV skew (3 variants: live diff/OTM, ratio-composite ratio/25-delta, lab diff/ITM),
call/put volume balance (3: live full-chain, ratio-composite ATM±5, lab band-restricted),
OI-based call/put comparison (4: live `Pcr` raw-ratio, ratio-composite constructive-flow-ratio, lab
OI-diff raw-difference, lab PCR-OI log-ratio), and price-vs-Greeks residual (3: live
`StraddleRichness` 1st-order combined, ratio-composite `ResidualDifference` 2nd-order per-leg, lab
F58 simplified per-leg). Sharper than the already-open F9 pending item (components assumed
independent) -- some of these aren't just correlated, they're near-literal re-implementations of
the same idea built at different times without consolidation.

**Scoring convention confirmed for whatever composite comes out of this lab work**: no rolling
z-scores -- reuse the ratio-composite's own clip-to-`[-1,1]` -> weighted-sum -> `100·tanh(raw/k)`
pattern, not the original composite's z-score machinery.

**Level-vs-change methodology, clarified after a genuine back-and-forth**: user's instinct that
"we should test the change over time, not the day value" is correct for flow-type metrics (volume,
OI change, CVD -- already tested that way throughout) and caught a real gap (PCR-OI's own
bar-to-bar delta was checked against OI-diff for redundancy but never against price directly).
But it is not a universal rule -- IV skew's own delta was tested directly against price and lost to
its level (0.09-0.57 coherent vs. mostly under 0.15, no stable sign), and depth imbalance (the
strongest confirmed candidate) is tested as a resting-book-state level, not a change, and that's
exactly what made it 16/16 coherent. User's own follow-up correctly reframed this: match the
treatment to the metric's own nature (volume keeps accruing -> test its rate/change; depth shifts
every tick -> test its current state/level) and test both where a metric plausibly has either,
rather than assuming either wins going in.

---

## 2026-09-13 -- OiBuildupNet tested: CLOSED, no edge (four formulations, all negative)

First test of the single largest live-weighted component (0.3125). Replicated
`LiveFeatureEngine.ComputeOiBuildupNet`'s exact classifier and sign table
(`OiBuildupClassifier.Classify` + the call/put-flipped lookup at LiveFeatureEngine.cs:1832-1843,
verified directly against source, not the earlier paraphrase) on bucket-level spot/OI changes
rather than live's raw per-cadence values, deliberately avoiding the "OI refresh is lumpy, a
single 15s delta is usually zero" trap already found and fixed live once (F50).

**Pass 1 (`scripts/oi-buildup-net-vs-price.sql`)** -- 5-min and 15-min bucket windows, matching
spot and OI change windows, ATM+-2 (Strike5's own offset range), both option types, both weeks:

| Date | Week | 15m-window vs option (fwd5/15) | 15m-window vs future (fwd5/15) |
|---|---|---|---|
| 08 Sep | ThisWeek | -0.116 / -0.075 | -0.059 / -0.040 |
| 09 Sep | ThisWeek | +0.059 / -0.041 | +0.040 / -0.062 |
| 10 Sep | ThisWeek | +0.102 / +0.177 | +0.105 / +0.197 |
| 11 Sep | ThisWeek | +0.050 / -0.058 | +0.033 / -0.062 |
| 08 Sep | NextWeek | -0.017 / -0.305 | -0.055 / -0.332 |
| 09 Sep | NextWeek | +0.011 / -0.024 | +0.020 / -0.052 |
| 10 Sep | NextWeek | -0.150 / -0.251 | -0.159 / -0.259 |
| 11 Sep | NextWeek | +0.051 / +0.050 | +0.056 / +0.050 |

No consistent sign anywhere; 10 Sep ThisWeek is the one real-looking cell, not replicated on any
other day.

**Pass 2 (`scripts/oi-buildup-net-4min-lookback.sql`)**, per user's request to get closer to
live's real ~4-minute `OiLookbackWindow` -- tested two variants side by side: (A) the *exact*
live pairing (4-min OI change + previous-single-15s-cadence spot change -- replicating live's own
documented F50 mismatch verbatim, not fixing it) and (B) a self-consistent 4-min/4-min pairing:

| Date | Week | A vs option/future (fwd5/15) | B vs option/future (fwd5/15) |
|---|---|---|---|
| 08 Sep | ThisWeek | 0.003/0.002, 0.010/0.009 | -0.099/-0.069, -0.085/-0.138 |
| 09 Sep | ThisWeek | 0.030/0.011, 0.020/0.002 | 0.080/-0.020, 0.051/-0.032 |
| 10 Sep | ThisWeek | -0.029/-0.041, -0.052/-0.040 | -0.004/0.070, -0.042/0.060 |
| 11 Sep | ThisWeek | -0.019/0.007, -0.015/0.004 | 0.022/0.012, 0.020/0.011 |

Variant A (what live actually runs today) is essentially zero everywhere -- weaker than every
other formulation tested, including the deliberately-mismatched bucket versions. Variant B is a
little larger but still sign-inconsistent across days.

**Separate, standing finding, independent of whether OiBuildupNet has any edge at all**: Variant
A's near-total flatness compared to Variant B's modest (if inconsistent) readings suggests the
documented F50 spot/OI window mismatch isn't just untidy -- it appears to actively degrade
whatever signal the self-consistent version carries, by pairing a multi-minute OI move against a
single 15-second-old, largely noise-dominated spot tick. If this component is ever revisited, F50
needs fixing *before* re-evaluation -- testing today's self-inconsistent formula and concluding "no
edge" would risk judging the wrong thing.

**Verdict: CLOSED, no edge found.** Four honest formulations (5-min bucket, 15-min bucket, exact
live 4-min mismatched, self-consistent 4-min), all negative, same bar as CVD's closure. The single
largest weight in the live composite has now been tested for the first time and found
unsupported by any of the four ways it could reasonably be interpreted.

---

## 2026-09-13 -- Two standing testing-convention decisions

**NextWeek dropped as a testing scope going forward.** Every metric checked against both weeks
this session -- OI-diff, PCR, PCR-OI, depth imbalance, IV skew, OiBuildupNet -- showed NextWeek
weaker, messier, or incoherent relative to ThisWeek, with zero exceptions and not one finding
sourced from NextWeek data. User's own call: stop testing it as a matter of course; only revisit
if there's a specific reason to. `docs/SCORE_CANDIDATES.md` updated with this as a standing rule.

**Multiple time horizons per metric, not a fixed 5/15-min default.** Different metrics plausibly
resolve on different timeframes (a resting-state metric might behave differently at 10 or 30
minutes than at 5) -- test what makes sense for the specific metric, not just the two horizons
convenient for the first candidates tried.

---

## 2026-09-13 -- Live `Pcr` tested: CLOSED, apparent signal was a single-day trend artifact

Second item on the live-weight priority list (0.19, second-largest). Mathematically the reciprocal
of PCR-OI's own `log(CallOi/PutOi)` (same OI data, un-logged, inverted), so this isolates two
questions PCR-OI's own result left open: does the raw-vs-log transform matter, and does the
ratio's own bar-to-bar delta correlate with price directly (never tested for PCR-OI either --
only checked against OI-diff for redundancy). Ran `scripts/live-pcr-strike5-full-battery.sql`:
raw level, log level, and each one's delta, at 5/15/30-min horizons, Strike5 (ATM+-2, matching
live exactly), ThisWeek only, per day.

**Result**: 09 Sep alone produced a large, horizon-strengthening correlation --

| Date | raw vs option (5/15/30m) | raw vs future (5/15/30m) | log vs option (5/15/30m) |
|---|---|---|---|
| 08 Sep | 0.037 / 0.020 / -0.013 | -0.014 / -0.001 / 0.021 | -0.013 / -0.003 / 0.031 |
| 09 Sep | -0.268 / -0.416 / -0.569 | -0.268 / -0.397 / -0.537 | 0.301 / 0.465 / 0.604 |
| 10 Sep | -0.023 / 0.110 / -0.027 | -0.047 / 0.146 / -0.004 | 0.020 / -0.129 / 0.005 |
| 11 Sep | -0.041 / -0.014 / -0.008 | -0.032 / -0.019 / 0.007 | 0.008 / -0.014 / -0.020 |

08, 10, 11 Sep: nothing, every cell under 0.15. 09 Sep: a striking, monotonically-strengthening
result at every horizon, both targets, both transforms (mirror-signed, as expected from the
monotonic raw/log relationship).

**Checked whether this was real before trusting it -- three separate tests, all pointing the same
way:**

1. **Daily price action**: 08 Sep -64.6 (mild down), **09 Sep -85.6, opened near the day's high and
   closed exactly at the day's low** (a clean, uninterrupted one-directional grind), 10 Sep +10.4
   (flat), **11 Sep +156.6, by far the largest net move of the 4 days**. If this were a real
   leading relationship, 11 Sep -- nearly double 09 Sep's move -- should show it at least as
   strongly. It shows essentially zero everywhere.
2. **Horizon shape**: the correlation *strengthens* from 5 to 15 to 30 minutes -- the signature of
   two series co-drifting over a persistently one-directional session, not a decaying predictive
   lead (a real forecast typically loses information the further out you look, not gains it).
3. **The metric's own delta**: shows nothing special on 09 Sep either (-0.008/-0.044/-0.167 raw,
   +0.001/+0.064/+0.198 log) -- if the level carried real, updating information, the bar-to-bar
   increments should reflect it too. They don't, consistent with the level simply tracking 09
   Sep's own cumulative decline rather than predicting anything ahead of it.

**Verdict: CLOSED.** The raw-vs-log transform question this test was partly designed to answer
turned out to be moot -- both transforms reproduced the identical single-day artifact, because the
underlying effect isn't real. Exactly the failure mode this project's "per-day, not pooled,
multiple days" discipline exists to catch, from the opposite direction of PCR-OI's own lesson: PCR-OI
showed pooling can hide a real per-day signal; this shows a single dramatic day can masquerade as
one. Band-comparison sweep not run given how conclusive this already is -- available on request if
further confirmation is wanted.

---

## 2026-09-13 -- Live `IvSkew` blocked, `FuturesBasis` tested: CLOSED, same artifact pattern a third time

**`IvSkew` as actually implemented could not be tested -- a real data-availability blocker, not a
finding.** Live's `ComputeIvSkew` targets strikes at `spot +- expectedMove` (`expectedMove =
spot*atmVol*sqrt(t)`), a genuine 1-sigma weekly move -- estimated using our own real IV/DTE data
for ThisWeek: ~310 points (~6 strikes) on 09 Sep, ~270 (~5 strikes) on 10 Sep, ~260 (~5 strikes) on
11 Sep, all far beyond `CadencePopulator`'s persisted `PersistedStrikeOffsetBand = 3`. Confirmed
directly: `MIN(StrikeOffsetFromAtm), MAX(...)` across the whole table returns exactly (-3, 3). The
strikes live's `IvSkew` actually reads are simply not in our data. Three options presented to
user: widen the persisted band and repopulate (correct fix, costs a pipeline run), approximate
using the widest already-persisted band (`Strike7`) as a rough OTM proxy, or defer. **User chose
to defer and move to the next item** -- not closed, not tested, genuinely blocked pending a
repopulate decision.

**`FuturesBasis` as actually implemented (the raw level feeding the composite's own rolling
z-score) tested: CLOSED, same artifact pattern as `OiBuildupNet` and `Pcr`.** Re-ran the existing
`scripts/spot-future-basis-correlation.sql` (no option strikes involved at all -- pure
future-vs-spot, so the strike-splicing bug was never a risk here; its `RANGE BETWEEN ... FOLLOWING`
windows self-guard by real elapsed time, no additional guard needed).

| Date | Level vs fwd5m | Level vs fwd15m | Day's own trend |
|---|---|---|---|
| 08 Sep | -0.090 | -0.031 | mild down |
| 09 Sep | -0.192 | -0.238 | persistent grind down, closed at daily low |
| 10 Sep | +0.088 | +0.111 | flat |
| 11 Sep | -0.030 | +0.012 | biggest trend of the 4 days (+156.6) |

Pooled (-0.135/-0.212) looks real but is driven almost entirely by 09 Sep, does not replicate on
11 Sep (the actual biggest trend day), and strengthens rather than decays from 5 to 15 minutes --
the identical three-flag pattern that closed live `Pcr` the same day. The per-cadence change
version is different and more coherent (consistently negative on all 4 days) but far too small in
magnitude (under 0.06 everywhere) to call a real finding; rolling-sum change versions are
inconsistent (08 Sep flips positive) and untrustworthy.

**Verdict: CLOSED** for the raw level as actually implemented live -- the pre-existing "Spot/future
basis change" watch candidate (a different, first-differenced quantity) is unaffected and stays as
it was. **Pattern worth naming plainly: three live-weighted components tested in a row --
`OiBuildupNet`, `Pcr`, `FuturesBasis` -- have each turned out to be a single-trending-day artifact
once properly checked (fails to replicate on the day with the actually largest trend, correlation
strengthens rather than decays with horizon, and in two of the three cases the metric's own change
shows nothing special on the same day).** Not proof the whole original 14-component composite is
unsupported, but a real, recurring signature worth taking seriously before trusting any of its
other untested weights (`IvSkew` once unblocked, and every 0.0-weighted diagnostic component).

---

## 2026-09-13 -- GammaExposure/VannaExposure/CharmExposure also blocked; schema widened to unblock all four

User asked to test the three Greek-exposure components together (structurally identical --
confirmed by direct read of `ComputeGammaExposure`, LiveFeatureEngine.cs:2229-2252: `Σ(call:
+gamma*OI, put: -gamma*OI)` over `_nearestExpiryOptions`, i.e. the **entire** nearest-expiry
chain, not a persisted band, using one shared ATM-reference vol for every strike -- Vanna/Charm
are the identical loop with a different Greek).

**Two separate blockers found, worse than `IvSkew`'s**:
1. Same root cause as `IvSkew` -- our lab only ever persists `StrikeOffsetFromAtm` within +-3
   (`PersistedStrikeOffsetBand`), confirmed via `MIN/MAX` across the whole table. Gamma exposure
   genuinely needs the whole chain (real dealer-GEX calculations span far wider than +-3 strikes
   precisely because large OI concentrations often sit well outside a narrow ATM band) -- an
   ATM+-3 approximation risks not just incompleteness but the wrong **sign**.
2. **`Vanna`/`CharmPerDay` aren't persisted at all** -- checked `StrikeCadenceSnapshot.cs` directly,
   only `Gamma` exists as a column. A schema gap, not a row-scope gap -- widening the band alone
   would not have been enough for these two.

**User chose to fix the root cause rather than defer a third time**: widen the schema and
repopulate to unblock all four components (`IvSkew`, `GammaExposure`, `VannaExposure`,
`CharmExposure`) in one pass.

**Confirmed the right band width before picking one, not guessed**: queried
`niftysignal_vm_copy.instruments` (the actual tracked/subscribed instrument universe, a different
DB from the analysis output this project queries all day) for 08-11 Sep -- 20-21 distinct strikes
per expiry, i.e. roughly ATM+-10 at 50-point spacing. Confirmed `OptionGreeks` (NiftySignal.
Pricing/OptionGreeks.cs:16) already returns `Vanna` and `CharmPerDay` as part of the same struct
`BlackScholes.Calculate` already produces for Delta/Gamma/Theta/Vega -- zero extra computation
cost, just two previously-unread fields.

**Changes made** (`NiftySignal.BacktestData`, analysis pipeline only -- no Host/live code
touched):
- `CadencePopulator.cs`: `PersistedStrikeOffsetBand` widened from `3` to `10`. Purely additive --
  every existing band definition tops out at offset 3, none are affected.
- `StrikeCadenceSnapshot.cs`: added `Vanna`, `CharmPerDay` (both `double?`, same null-when-parity-
  cant-solve convention as every other Greek).
- `CadencePopulator.BuildStrikeRow`: reads `greeks.Vanna`/`greeks.CharmPerDay` from the
  already-existing `BlackScholes.Calculate` call and stores them.
- Migration `WidenStrikeBandAndAddVannaCharm` -- inspected before applying, exactly 2
  `AddColumn` calls against `StrikeCadenceSnapshots`, nothing else touched.
- `dotnet build` + `dotnet test`: clean, 475/475, both before and after.
- `docs/CHILD_TABLE_SCHEMA.md` updated with the new columns and the widened-band rationale.

**Requires a repopulate** (delete `CadenceContexts` for the affected days, re-run) before the
wider band or the two new Greeks show up in queryable data -- not done yet, the user's own call on
timing per this project's standing convention (the user always runs the populator, never this
session). `IvSkew`, `GammaExposure`, `VannaExposure`, `CharmExposure` all remain untested until
that repopulate happens.

---

## 2026-09-13 -- Repopulate verified, GammaExposure/VannaExposure/CharmExposure tested (version A)

**Repopulate verification** (same discipline as every prior schema change): `MIN/MAX
StrikeOffsetFromAtm` now -10/10 (was -3/3), `Vanna`/`CharmPerDay` non-null at 444,231/449,167 --
identical rate to `Gamma`, exactly as expected since all three come from the same conditional
block. No duplication: ATM (`offset=0`) rows are 6,000/day on 3 of 4 days, matching the expected
1500 cadences x 2 expiries x 2 option types exactly (11 Sep shows slightly more distinct strikes
at offset 0 due to more ATM re-centering that day, not a duplication artifact).

**Design decision confirmed before testing**: live's `ComputeGammaExposure`/Vanna/Charm use one
shared ATM-reference vol for every strike's Black-Scholes calc, not each strike's own solved IV.
What's now persisted (`Gamma`/`Vanna`/`CharmPerDay` per strike) is the own-IV version -- more
theoretically correct (respects the real smile) but not byte-identical to live's simplification.
Tested version A (sum of persisted per-strike Greeks) first, per user's choice; version B (exact
shared-vol replication) deferred as a follow-up if version A looks worth it.

Ran `scripts/gex-vanna-charm-vs-price.sql`: `Σ(call:+Greek·OI, put:-Greek·OI)` across the full
ATM+-10 chain, level and bucket-level (5-min) delta, 5/15/30-min horizons, both option and future
targets, ThisWeek only, per day.

**GammaExposure -- distinct from every metric closed today, passes checks they failed:**

| Date | vs option (5/15/30m) | vs future (5/15/30m) | Delta vs future (5/15m) |
|---|---|---|---|
| 08 Sep | 0.131 / 0.154 / 0.135 | 0.093 / 0.100 / 0.056 | 0.091 / 0.068 |
| 09 Sep | 0.361 / 0.521 / 0.599 | 0.347 / 0.494 / 0.572 | 0.025 / 0.162 |
| 10 Sep | 0.167 / 0.214 / 0.357 | 0.180 / 0.206 / 0.395 | 0.201 / 0.254 |
| 11 Sep | 0.048 / 0.076 / 0.100 | 0.061 / 0.088 / 0.123 | 0.087 / 0.106 |

Same positive sign on all 4 days; option and future agree with each other every day (never true
for `OiBuildupNet`/`Pcr`/`FuturesBasis`); present, weaker but not absent, on 11 Sep (the biggest
trend day, +156.6); delta shows a real same-signed pattern rather than nothing. **Open, not
resolved**: correlation strengthens with horizon on 09/10 Sep, the same shape that flagged trouble
before -- but here a real competing mechanism exists (dealer gamma-hedging pressure accumulating
over a session, not an instant reaction), so this isn't automatically the same artifact. Verdict:
**watch candidate**, meaningfully better-supported than the day's three closures, not yet at
depth-imbalance/IV-skew tier.

**VannaExposure -- fails the option/future coherence check GEX passes:**

| Date | vs option (5/15/30m) | vs future (5/15/30m) | Delta vs future (5/15m) |
|---|---|---|---|
| 08 Sep | 0.225 / 0.329 / 0.243 | -0.024 / -0.025 / -0.136 | -0.149 / 0.024 |
| 09 Sep | -0.110 / -0.197 / -0.218 | -0.090 / -0.191 / -0.232 | 0.095 / 0.051 |
| 10 Sep | 0.015 / -0.054 / -0.209 | -0.003 / -0.080 / -0.274 | -0.061 / -0.165 |
| 11 Sep | -0.006 / -0.033 / -0.056 | -0.050 / -0.075 / -0.104 | 0.126 / 0.106 |

08 Sep: option (+0.33) and future (-0.03) flatly disagree on the same day -- the same
coherence-check logic used throughout this project to reject a signal. 10 Sep sign-flips within
itself across horizons against the same target. Delta has no stable sign on any day. **Verdict:
CLOSED, no edge.**

**CharmExposure -- same coherence failure as Vanna:**

| Date | vs option (5/15/30m) | vs future (5/15/30m) | Delta vs future (5/15m) |
|---|---|---|---|
| 08 Sep | 0.349 / 0.493 / 0.326 | 0.008 / 0.030 / -0.091 | 0.097 / 0.071 |
| 09 Sep | 0.038 / 0.074 / 0.070 | 0.017 / 0.070 / 0.087 | -0.052 / -0.071 |
| 10 Sep | -0.074 / -0.010 / 0.147 | -0.062 / 0.015 / 0.205 | -0.074 / -0.066 |
| 11 Sep | 0.038 / 0.084 / 0.121 | 0.078 / 0.121 / 0.164 | -0.149 / -0.045 |

08 Sep: a strong option-only reading (+0.49 at 15m) against essentially nothing on future (+0.03)
-- the identical one-day target disagreement that closed Vanna. 10 Sep sign-flips within itself.
09/11 Sep are small but target-consistent; delta is consistently negative on 3 of 4 days but too
small (under 0.15) to rescue it. **Verdict: CLOSED, no edge.**

`docs/SCORE_CANDIDATES.md` updated with all three verdicts and full tables.

---

## 2026-09-13 -- Live `IvSkew` (dynamic OTM strike) tested: watch candidate, sign reversed vs. the ITM-band finding

Now unblocked by the schema widening. Replicated `expectedMove = spot*atmVol*sqrt(t)`, target
offset = `ROUND(expectedMove/50)` (confirmed 50-point strike spacing from real data), call at
`+offset`, put at `-offset`. `atmVol` proxied as the average of ATM (`offset=0`) call+put IV per
cadence -- an approximation of live's actual shared reference-vol solve, not independently
replicated, flagged explicitly rather than presented as exact.

**08 Sep is a genuine data-availability casualty, not a weak finding**: expiry-day IV distortion
(avg IV 0.271 vs ~0.10 normally) inflates the implied target offset up to **19 strikes out** --
beyond even the widened +-10 band. `n_resolved=1245` vs 1500 on the other days confirms real gaps.
Same underlying cause (08 Sep's expiry-day regime) as the ITM-band version's own exclusion, via a
different mechanism (data availability here, vs. regime-difference reasoning there).

**09-11 Sep -- target offsets 5-7, comfortably inside the persisted band, confirming the earlier
estimate was accurate:**

| Date | Level vs option (5/15/30m) | Level vs future (5/15/30m) | Day's trend |
|---|---|---|---|
| 09 Sep | -0.090 / -0.122 / -0.298 | -0.073 / -0.112 / -0.278 | down |
| 10 Sep | -0.048 / +0.056 / +0.162 | -0.037 / +0.051 / +0.244 | flat |
| 11 Sep | -0.157 / -0.243 / -0.340 | -0.163 / -0.248 / -0.366 | biggest trend (+156.6) |

09 and 11 Sep (the two real trend days) show a coherent, option/future-agreeing,
strengthens-with-horizon relationship -- strongest on 11 Sep, the day with the largest actual
trend. This is the **opposite** pattern from the same day's three closures (`OiBuildupNet`, `Pcr`,
`FuturesBasis`), where the biggest trend day showed nothing -- here it shows the most, which is
reassuring rather than a red flag (a real signal having more to explain on a bigger-move day is
expected, not suspicious). 10 Sep (the flat day, only +10.4 net) flips sign across horizons,
plausibly just a low-information day. Delta shows nothing special anywhere (mostly under 0.05).

**The standout finding: the sign here is the classic textbook direction, reversed from the
already-confirmed ITM-band (Itm2Atm1) result.** Negative correlation = rising put-skew (relative
to call) predicts price falling -- the standard "hedging demand is bearish" reading. The ITM-band
version showed the opposite (put-skew-up correlates with price *rising*). Read as evidence that
"IV skew" is not one robust concept with a single sign -- the economic meaning depends on which
strikes are actually being compared (a real expected-move OTM pair here, vs. an ITM band there),
not a property of skew in the abstract.

**Verdict: watch candidate.** Real, coherent evidence on 2 of 3 usable days (09, 11 Sep),
comparable 30-min magnitude to the ITM-band confirmation, but a smaller base (2/3 vs. that
version's 3/3) and 08 Sep is a genuine data gap rather than a weak day. `docs/SCORE_CANDIDATES.md`
updated with the full tables and the sign-reversal finding.

---

## 2026-09-13 -- Live `VolumePcr` tested: watch candidate, meaningfully more robust than live `Pcr`

Checked the exact formula directly before reusing anything: `ComputeVolumePcrAndCvdProxy` computes
`notional = VolumeDelta x MarkPrice` **per cadence** (one mid price times that cadence's own
volume delta) -- confirmed this is genuinely different from the already-persisted `NotionalDelta`
column (a per-tick-weighted sum, documented as "sum of each tick's own LastPrice x that tick's own
volume delta"). Built the per-cadence version directly from persisted `VolumeDelta`/`MarkPrice`
rather than assuming either existing column was reusable. Full chain (ATM+-10, matching live's own
"entire nearest-expiry chain" scope), ThisWeek only.

Ran `scripts/live-volumepcr-fullchain.sql`: raw and log transforms, level and delta, 5/15/30-min
horizons, both targets, per day.

| Date | Raw vs option (5/15/30m) | Raw vs future (5/15/30m) | Day's trend |
|---|---|---|---|
| 08 Sep | 0.127 / 0.257 / 0.209 | 0.114 / 0.322 / 0.330 | down |
| 09 Sep | -0.045 / 0.144 / 0.224 | -0.048 / 0.116 / 0.185 | down |
| 10 Sep | 0.156 / 0.259 / 0.179 | 0.105 / 0.240 / 0.109 | flat |
| 11 Sep | 0.002 / 0.111 / 0.171 | 0.034 / 0.146 / 0.200 | biggest trend (+156.6) |

**Passes the checks that closed `OiBuildupNet`/`Pcr`/`FuturesBasis` earlier the same day**:
consistent sign across all 4 days, present (not vanished) on 11 Sep, the biggest actual trend day.
**Horizon shape is the most reassuring seen today**: 08 and 10 Sep peak at 15 minutes and decay at
30 -- the normal shape for a real, decaying forecast, not the suspicious ever-climbing pattern that
flagged the artifacts. 09 and 11 Sep still rise at 30-min but only to 0.20-0.22, far below the
0.57-0.60 the actual artifacts reached. Log transform mirrors raw almost exactly (opposite sign,
similar magnitude) -- the raw-vs-log question that mattered for live `Pcr` barely matters here.
Delta is weak but consistently negative (raw) on 3 of 4 days.

**Worth stating plainly**: this is meaningfully more robust than live's OI-based `Pcr`
(`putOI/callOI`), closed the same day as a pure single-day (09 Sep) trend artifact. Same "PCR"
family by name, opposite outcome once actually tested -- the notional/volume-based data held up
under scrutiny, the OI-level-based data didn't.

**Verdict: watch candidate**, same tier as `GammaExposure` and the dynamic-strike `IvSkew`.
`docs/SCORE_CANDIDATES.md` updated with the full table.

---

## 2026-09-13 -- Live `StraddleRichness` tested: CLOSED, fails two separate checks

Read the exact source before building anything (`ComputeStraddleRichness`, LiveFeatureEngine.cs:
2400-2449): `richness = actualChange - (prevNetDelta*(S_now-S_prev) + prevThetaPerDay*elapsedDays)`,
ATM straddle (call Delta+Theta plus put Delta+Theta, combined), computed cadence-to-cadence,
guarded by `_previousStraddleStrike == atmStrike` -- only valid when the ATM strike hasn't rolled
between the two ticks, replicated exactly here via a `StrikePrice` LAG comparison (the same
strike-identity discipline used everywhere else this session). Spot substituted for synthetic S in
the delta term -- not persisted per-cadence as its own column, and multiplied by a near-zero
straddle net delta (call delta ~+0.5, put delta ~-0.5, nearly cancelling), so the approximation's
impact should be small, flagged rather than hidden.

Per user's explicit sequencing: tested the **windowed sum** (trailing 5/15-min) first, per-tick
version deferred as a follow-up. Ran `scripts/live-straddlerichness-windowed.sql`.

| Date | Sum5m vs option (5/15/30m) | Sum5m vs future (5/15/30m) |
|---|---|---|
| 08 Sep | 0.044 / -0.485 / -0.272 | 0.147 / -0.059 / 0.074 |
| 09 Sep | -0.293 / -0.209 / -0.085 | -0.244 / -0.171 / -0.051 |
| 10 Sep | 0.120 / 0.171 / 0.182 | 0.158 / 0.182 / 0.208 |
| 11 Sep | -0.003 / -0.139 / -0.082 | 0.004 / -0.151 / -0.058 |

**Two separate failure modes, not just weak magnitude:**
1. **08 Sep fails option/future coherence** -- strong against option (-0.485 at 15m), essentially
   nothing against future (-0.059) at the same horizon -- the identical target-disagreement red
   flag that closed `VannaExposure`/`CharmExposure` earlier the same day. Plausibly an expiry-day
   distortion (08 Sep is 0 DTE, and this metric's theta term is especially sensitive to
   time-to-expiry), consistent with 08 Sep already being excluded from two other candidates today.
2. **10 Sep clearly disagrees in sign with 09 and 11 Sep even after allowing for the expiry-day
   exclusion** -- 09 and 11 Sep both come out negative (both targets self-consistent within each
   day); 10 Sep is positive throughout, increasing with horizon. 10 Sep is not an expiry day, so
   this 2-vs-1 cross-day split has no regime explanation available -- a genuine, unresolved
   inconsistency, not rescued by the horizon-shape reasoning that saved `GammaExposure`/
   `VolumePcr` earlier today (those had consistent sign across every single day; this doesn't).

**Verdict: CLOSED, no edge.** Fails both the target-coherence check and cross-day sign
consistency. The raw per-tick (unwindowed) version was not built, per the user's own sequencing --
worth a direct decision on whether it's still worth chasing given how clearly the windowed version
closed, rather than building it by default.

---

## 2026-09-13 -- Live `SpreadRatio` tested: CLOSED, same cross-day sign failure, different outlier day

Confirmed exact formula from `ComputeSpreadRatio` (LiveFeatureEngine.cs:880-912) before building
anything: per-token average spread%-of-mid, meaned across every call/put token equally
(mean-of-means, not liquidity-weighted), full chain, `return putMean/callMean`. Used persisted
per-cadence `SpreadPctOfMid` (forward-filled per strike) as the closest available proxy for live's
own rolling per-token average -- a minor, flagged approximation. Full chain (ATM+-10), ThisWeek
only. Ran `scripts/live-spreadratio-fullchain.sql`.

| Date | Raw vs option (5/15/30m) | Raw vs future (5/15/30m) |
|---|---|---|
| 08 Sep | -0.208 / -0.390 / -0.091 | -0.008 / -0.025 / 0.088 |
| 09 Sep | 0.093 / 0.159 / 0.174 | 0.096 / 0.142 / 0.141 |
| 10 Sep | 0.034 / 0.095 / 0.092 | 0.015 / 0.080 / 0.085 |
| 11 Sep | -0.029 / -0.074 / -0.136 | -0.018 / -0.071 / -0.136 |

08 Sep repeats the same expiry-day option/future target-disagreement seen across multiple
candidates today (option -0.390 vs future -0.025 at 15m). Excluding it: option and future agree
well within each remaining day, but **09/10 Sep are positive while 11 Sep is clearly negative** at
comparable magnitude -- the identical cross-day sign failure that closed `StraddleRichness` earlier
today, just with a different day as the outlier this time (there it was 10 Sep vs 09/11; here it's
11 Sep vs 09/10). Magnitudes are small throughout (mostly under 0.20); delta shows nothing (under
0.05 everywhere).

**Verdict: CLOSED, no edge.** Second candidate in a row to fail on cross-day sign consistency
rather than magnitude -- worth watching whether this becomes a recognizable pattern for
full-chain, unweighted, mean-of-means-style metrics specifically (both `StraddleRichness` and
`SpreadRatio` share that shape), separate from the single-trending-day-artifact pattern found
earlier for `OiBuildupNet`/`Pcr`/`FuturesBasis`.

---

## 2026-09-13 -- Ratio-composite `NotionalVolumeRatio` tested: watch candidate, strongest of the group, cross-validates `VolumePcr`

Confirmed exact formula from `ComputeRatioNotionalVolumeRaw` (LiveFeatureEngine.cs:2704-2752)
before building anything: same per-cadence notional calc as `VolumePcr` (`volumeDelta x mid`), but
`RatioWideStrikeBand=5` (ATM+-5, confirmed by grep, not assumed), its own independent volume
baseline (`_previousVolumeByTokenRatio`, separate from `VolumePcr`'s full-chain one), a
`MinNotionalForVolumeRatio=5000` floor (confirmed from `RatioMetricScales.cs`), and **inverted
orientation** -- `callNotional/putNotional`, not `put/call` like `VolumePcr`. Ran
`scripts/ratio-notionalvolumeratio.sql`, ThisWeek only.

| Date | Raw vs option (5/15/30m) | Raw vs future (5/15/30m) |
|---|---|---|
| 08 Sep | -0.204 / -0.315 / -0.212 | -0.172 / -0.354 / -0.333 |
| 09 Sep | 0.061 / -0.102 / -0.193 | 0.060 / -0.081 / -0.145 |
| 10 Sep | -0.151 / -0.267 / -0.167 | -0.091 / -0.236 / -0.085 |
| 11 Sep | 0.018 / -0.125 / -0.199 | -0.002 / -0.149 / -0.218 |

**At 30 minutes, all 4 days land in a remarkably tight band** (-0.212, -0.193, -0.167, -0.199) --
tighter clustering, both in sign and magnitude, than anything else tested today including
`GammaExposure`. Option and future agree within every single day. 5-minute horizon is noisier (two
days flip slightly positive) but the 15-30 minute picture is unanimous. Delta shows a modest but
mostly-consistent positive pattern (3 of 4 days at 5-min).

**Cross-validation with `VolumePcr` is the standout point**: `VolumePcr` (put/call, full chain, own
baseline, tested earlier today) showed *positive* correlation strengthening with horizon on all 4
days. This metric -- call/put (inverted), ATM+-5 (narrower band), independent volume baseline and
floor guard -- shows *negative* correlation on all 4 days. Once the inverted orientation is
accounted for, these are the *same* underlying relationship, found twice, independently, under two
different scopes and implementations. That's meaningfully stronger evidence than either result
alone -- it's not "one query found a pattern," it's "the pattern replicates under a different band
and a different floor."

**Verdict: watch candidate, flagged as the strongest of today's watch tier** -- the 30-minute
tightness plus the cross-validation puts this a notch above `GammaExposure`/`IvSkew`/`VolumePcr`
themselves, though not promoted to confirmed pending the same further scrutiny (more days, an
explicit check for 08 Sep expiry-day distortion) already applied to every other candidate today.

---

## 2026-09-13 -- Ratio-composite `SizedOiFlowRatio` tested: CLOSED, no edge

Confirmed exact formula from `ComputeRatioSizedOiFlowRaw` (LiveFeatureEngine.cs:1875-1936):
`(callConstructive+1)/(putConstructive+1)`, sum of `|ΔOI|` for classifier-confirmed constructive
strikes only (call: LongBuildup/ShortCovering; put: ShortBuildup/LongUnwinding -- same table as
`OiBuildupNet`), ATM+-5 band, `MinContractsForOiFlow=50` floor (confirmed from
`RatioMetricScales.cs`). Shares the identical spot/OI window mismatch as `OiBuildupNet` --
confirmed via the source's own comment explicitly cross-referencing it ("same known, deliberate
gap as ComputeOiBuildupNet's own copy of this line"). Tested both the exact live pairing (A:
~4-min OI change + previous-single-15s-cadence spot) and a self-consistent pairing (B: ~4-min/
~4-min) again, same methodology as `OiBuildupNet`. Ran `scripts/ratio-sizedoiflowratio.sql`.

| Date | A vs option/future (15m/15m/30m) | B vs option/future (15m/15m/30m) |
|---|---|---|
| 08 Sep | -0.301 / -0.042 / 0.029 | -0.375 / -0.054 / 0.012 |
| 09 Sep | -0.081 / -0.064 / -0.153 | -0.055 / -0.050 / -0.168 |
| 10 Sep | 0.012 / 0.019 / -0.003 | -0.038 / -0.043 / -0.038 |
| 11 Sep | 0.002 / 0.004 / -0.015 | -0.016 / -0.004 / -0.041 |

08 Sep fails option/future coherence badly in both variants -- the same expiry-day pattern seen
repeatedly today. Excluding it, only 09 Sep carries real magnitude; 10 and 11 Sep are essentially
flat in both variants. **Unlike `OiBuildupNet`, the exact-vs-self-consistent pairing choice barely
matters here** -- A and B tell nearly the same story, meaning the weakness isn't primarily a
pairing artifact this time; the underlying constructive-flow-ratio concept itself just doesn't
carry much signal at this band/floor.

**Verdict: CLOSED, no edge.**

---

## 2026-09-13 -- Ratio-composite `ResidualDifference` tested: CLOSED, cross-day sign split

Confirmed exact formula from `ComputeResidualDifference` (LiveFeatureEngine.cs:2470-2538):
`predictedChange = Delta*dS + 0.5*Gamma*dS^2 + Theta*elapsedDays + Vega*dVol` per leg (full
2nd-order Taylor -- richer than `StraddleRichness`'s 1st-order combined version), `residualDifference
= callResidual - putResidual`. Directly related to F58's already-confirmed finding (same
call-minus-put-residual concept, F58 uses a simpler "half the naive move" formula) -- this is the
more theoretically rigorous version, worth checking whether it holds up as well or better.
Cadence-to-cadence, `_previousResidualStrike == atmStrike` guard replicated exactly (same
strike-identity discipline as `StraddleRichness`). `vol` (shared ATM reference) proxied as
avg(ATM call IV, ATM put IV), same proxy used for `IvSkew`/`StraddleRichness`. Summed over
trailing 5/15-min windows, same sequencing as `StraddleRichness`. Ran
`scripts/ratio-residualdifference.sql`.

| Date | Sum5m vs option/future (15m/15m) | Sum15m vs option/future (15m/15m/30m) |
|---|---|---|
| 08 Sep | 0.058 / 0.046 | 0.131 / 0.216 / 0.103 |
| 09 Sep | -0.185 / -0.176 | -0.249 / -0.262 / -0.219 |
| 10 Sep | 0.085 / 0.118 | 0.209 / 0.170 / 0.180 |
| 11 Sep | -0.032 / -0.029 | -0.088 / -0.063 / 0.016 |

Notably, **option and future agree well within every single day here** -- no target-disagreement
failure this time, unlike most of today's other closures. But **08 and 10 Sep are clearly positive
while 09 and 11 Sep are clearly negative** -- a clean 2-vs-2 split. Checked whether this tracked
the day's own trend direction (a plausible confound): it doesn't -- 08 and 09 Sep are both down
days yet show opposite signs for this metric, ruling that out. No regime explanation available.
Same cross-day sign-inconsistency failure class as `StraddleRichness` and `SpreadRatio` earlier
today.

**Verdict: CLOSED, no edge.** Per-tick (unwindowed) version not built, same reasoning as
`StraddleRichness` -- a cross-day sign split is a structural, day-level issue that a windowing
choice is unlikely to resolve.

---

## 2026-09-13 -- Ratio-composite `IvSkew25Delta` tested: CONFIRMED among non-expiry ThisWeek days

Last of the five ratio-composite metrics. Live's `ComputeIvSkewRatio25Delta`/
`InterpolateIvAtDelta25` (LiveFeatureEngine.cs:2071-2161) does true 25-delta smile interpolation --
rank every strike by a cheap shared-vol delta estimate, solve real IV for the 6 closest candidates,
recompute delta from the solved IV, linear-interpolate between whichever adjacent pair brackets
|delta|=0.25. Per user's explicit choice, approximated as the **single nearest-to-25-delta strike
per side** using each strike's own persisted, individually-solved `Delta` -- no bracket
interpolation built. `putIv/callIv`, matching live's exact orientation, fresh per-cadence
selection, full chain (ATM+-10). Ran `scripts/ratio-ivskew25delta.sql`.

| Date | Raw vs option (5/15/30m) | Raw vs future (5/15/30m) |
|---|---|---|
| 08 Sep | -0.095 / 0.018 / -0.073 | 0.005 / 0.158 / 0.067 |
| 09 Sep | -0.312 / -0.472 / -0.480 | -0.273 / -0.437 / -0.456 |
| 10 Sep | -0.219 / -0.270 / -0.214 | -0.210 / -0.245 / -0.132 |
| 11 Sep | -0.125 / -0.221 / -0.319 | -0.140 / -0.224 / -0.347 |

**The strongest result of the entire ratio-composite batch.** 08 Sep is weak and target-
inconsistent -- the now-familiar expiry-day pattern seen across every IV-based metric this
session. 09, 10, 11 Sep are unanimous: same sign, option and future agree closely within every
day, magnitude reaching -0.48 (larger than either other IV-skew variant tested today), and it
replicates on 11 Sep (the biggest trend day) rather than vanishing -- the reassuring pattern, not
the artifact one. Delta shows nothing special, consistent with "level carries the signal" holding
for every IV-skew formulation tried this session.

**Third independent confirmation of the classic textbook sign at an OTM strike selection.** Matches
the dynamic expected-move `IvSkew` finding exactly (rising put-skew predicts price falling) and
sits opposite to the ITM-band (Itm2Atm1) confirmation's sign. Two genuinely different OTM-selection
methods (expected-move-based, nearest-25-delta) now agree with each other and disagree with the
ITM-band method -- a clean, real pattern: **strike selection (OTM vs. ITM) determines the sign,
not "IV skew" as a single concept.**

**Verdict: CONFIRMED among non-expiry ThisWeek days** -- same tier and 08-Sep exclusion logic as
the original ITM-band IV skew finding, unanimous across all 3 usable days. Nearest-strike
approximation (not true bracket interpolation) flagged explicitly as a simplification, not a
byte-exact replication.

---

## 2026-09-13 -- ITM-vs-OTM skew redundancy check: substantially the same signal, ITM chosen for Core

User proposed a live Core/Watch/Blend scoring design for next week (external second-opinion input,
same "verify before acting" treatment as every other external review this session). Before
freezing a weight table that would have put both ITM-band and OTM-25-delta skew into Core at
independent weights, flagged that the two show *opposite* signs against price and had never been
checked against each other directly -- same open question already answered once this session for
PCR-OI vs. OI-diff.

Ran `scripts/itm-vs-otm-skew-redundancy.sql`: cross-correlated `PutAvgIv-CallAvgIv` (Itm2Atm1,
5-min boundary) against the nearest-25-delta `putIv/callIv` approximation, directly against each
other, ThisWeek only, all 4 days.

| Date | Level-vs-level | Delta-vs-delta |
|---|---|---|
| 08 Sep | 0.084 | -0.733 |
| 09 Sep | -0.657 | -0.356 |
| 10 Sep | -0.462 | -0.257 |
| 11 Sep | -0.894 | -0.005 |

**On all 3 non-expiry days -- the exact days both individual skew findings were confirmed on --
the level correlation is strongly negative (-0.46 to -0.89).** 11 Sep's -0.894 is close to a
near-perfect inverse relationship. This is exactly what two metrics reading the *same* underlying
smile tilt from opposite ends would look like, not what independent information looks like. Delta
is weaker and less consistent (same "level carries the information" pattern as every IV-skew
variant tested this session), and 11 Sep's delta relationship nearly vanishes (-0.005) despite
having the session's strongest level relationship -- worth remembering: levels are highly
redundant here, bar-to-bar innovations meaningfully less so.

**Verdict: substantially redundant at the level -- scoring both as independent Core inputs would
double-count one signal.** User's decision: **ITM (`Itm2Atm1`) is the Core skew representative**
going forward (tested with the actual production band, no approximation involved, vs. OTM's
nearest-strike approximation). OTM 25-delta moves to the Watch panel as a reference series, not
separately scored -- worth watching next week whether the redundancy holds up out-of-sample or
the two decouple on new days. The weight the second skew term would have taken in the proposed
Core design is now open for reallocation (e.g. toward depth imbalance, whose evidence base -- 4
days at 16/16 coherence -- is meaningfully stronger than either skew's 3 days).

---

## 2026-09-13 -- Full-day summary: every live-weighted component and every ratio-composite metric now tested

This closes out the systematic pass through every real and potential live weight prompted by the
external review's "OiBuildupNet has never been tested" finding. Complete tally for the day: 3
confirmed/strong candidates, 3 watch candidates, 9 closed. See `docs/SCORE_CANDIDATES.md` for the
full list with evidence; the user-facing summary was delivered directly in conversation rather than
duplicated here in full.

---

## 2026-09-22 — F61: `Instruments` table Underlying-ambiguity, found while scoping the Sensex/Bank Nifty tick-collection task — fixed same session

**Found while investigating, not reported by an outside review.** The task was to start persisting
raw Sensex/Bank Nifty ticks into the same `NiftySignalDbContext.Instruments`/`Ticks` tables NIFTY's
own live pipeline already uses (for future backtesting only — no scoring/trading on the new
indices). Before writing any resolver code, checked every consumer of `Instruments` for a hidden
single-underlying assumption, since that table has only ever held NIFTY rows in production.

**Confirmed real:** several call sites picked "the" future/spot/nearest-expiry-option-chain via a
bare `FirstOrDefaultAsync`/`First`/`Min` over `Instruments` filtered only by `AsOfDate` (or nothing
at all), with no `Underlying` filter and, for the `FirstOrDefault` sites, no deterministic
`OrderBy` either:

- `NiftySignal.Host/LiveFeatureEngine.cs` constructor — `_spot`/`_future`/`_nearestExpiry`/
  `_nearestExpiryOptions`, the engine that computes every live (paper) trade decision.
- `NiftySignal.VolumeBarData/LiveVolumeBarPopulator.cs` (`WriteNewBarsAsync`'s future lookup) and
  `NiftySignal.VolumeBarData/ReplayLiveCommand.cs` (same pattern, the `replay-live` parity harness).
- `NiftySignal.VolumeBarData/LiveOptionAtmPopulator.cs`, `LiveOptionMaxPainPopulator.cs`,
  `LiveOptionDepthPopulator.cs` — nearest-expiry option chain selection.
- `NiftySignal.Backtest/BacktestRunner.cs`, `NiftySignal.ScoreReplay/Program.cs`,
  `NiftySignal.CoreScoreReplayDiff/Program.cs` (two call sites) — all hand their loaded
  `Instruments` rows straight into `new LiveFeatureEngine(...)`.

Once Sensex/Bank Nifty rows exist in `Instruments` for the same `AsOfDate`, any of these would have
silently resolved to the wrong underlying's future/spot/option chain — no exception, no log line,
just quietly wrong scores/bars. Checked and ruled out as *not* needing a fix: `NiftySignal.DataSync/
Program.cs`'s Instruments max-id watermark query (`OrderByDescending(i => i.Id).FirstOrDefaultAsync()`)
— deterministic and deliberately underlying-agnostic, since it's a generic full-table replicator, not
a single-instrument selector.

**Fix:** added an explicit `Underlying == "NIFTY"` (`LiveFeatureEngine.NiftyUnderlying`) filter at
every site above, plus explicit `OrderBy(ExpiryDate)` wherever a `FirstOrDefault` had no ordering.
`LiveFeatureEngine`'s own constructor now filters to NIFTY once, internally, and stores only the
filtered list — so every downstream consumer that hands it a (possibly mixed) instrument list is
protected regardless of whether that caller also filters. The Backtest/ScoreReplay/CoreScoreReplayDiff
call sites additionally filter at intake, belt-and-suspenders.

**Verification (Stage 1 of the Sensex/Bank Nifty task, before any new-index code was written):**
- Behaviorally a no-op today, proven: full `dotnet test` suite (666 pre-existing tests) passes
  byte-identical after the fix, since the `Instruments` table holds only NIFTY rows in every
  existing fixture and in production as of this date.
- Two new regression tests added specifically to catch the bug this fix closes (would have failed
  against the pre-fix code): `LiveFeatureEngineTests.Constructor_WithMixedUnderlyingInstruments_*`
  (two tests) and `LiveVolumeBarPopulatorUnderlyingFilterTests.WriteNewBarsAsync_WithTwoUnderlyingsInInstrumentsTable_UsesNiftyFutureOnly`
  — each builds a fixture with a second underlying's rows deliberately inserted first and with an
  earlier expiry (the exact ordering an unfiltered query is most likely to get wrong), and asserts
  the NIFTY-only result.
- Full suite after the fix + new tests: 669/669 passing, 0 warnings.
- No live/VM verification performed or required — this is a local-code fix with no behavior change
  to the currently-deployed system (Instruments table on the VM also holds NIFTY rows only, so the
  filter is a no-op there too until Stage 2 ships).

**Numbering note:** this file's own text above (2026-09-11) said F61 was next-free; that note is now
stale as of this entry — F62 was next-free going forward, and is now used by the entry immediately
below.

## 2026-09-22 — F62: `Instruments` table Underlying-ambiguity, offline-populator follow-up to F61 — fixed same session

**Found while scoping this same follow-up task**, not by an outside review. F61 (immediately above)
explicitly scoped its fix to the live-path consumers only (`LiveFeatureEngine.cs` and the
`Live*Populator.cs`/`ReplayLiveCommand.cs`/`Backtest`/`ScoreReplay`/`CoreScoreReplayDiff` call
sites) — the *offline* backtest populators in `NiftySignal.VolumeBarData` weren't in scope at the
time, since no multi-underlying data existed yet to motivate checking them. Confirmed via a
`grep -rn "\.Instruments\b" NiftySignal.VolumeBarData/*.cs` sweep of every hit (not just the ones a
first pass guessed) that the same unfiltered/unordered pattern was present in every offline
populator/simulator/CLI helper that resolves a future or an option chain:

- `VolumeBarPopulator.cs` (`PopulateDayAsync`'s future lookup) — bare `FirstOrDefaultAsync`, no
  `Underlying` filter, no ordering. Same shape F61 fixed in `LiveVolumeBarPopulator.cs`, missed here.
- `OptionAtmPopulator.cs`, `OptionMaxPainPopulator.cs`, `OptionDepthPopulator.cs`,
  `OptionBandFlowPopulator.cs`, `OptionOiPopulator.cs`, `OptionSkew25DeltaPopulator.cs`,
  `CvdProxySumPopulator.cs`, `DepthImbalanceSumPopulator.cs` — each pulls the day's whole option
  chain via `Where(... && ExpiryDate != null)` with no `Underlying` filter, then picks the nearest
  expiry via a bare `Min()`.
- `TradeSimulator.cs` — the same `allOptions`/`Min()` pattern at both `SimulateDayAsync` and
  `SimulateCrossoverDayAsync`.
- `LivePaperTradeExecutor.cs` — despite the "Live" prefix, this file was **not** in F61's fix list
  (confirmed: F61's entry above names `LiveFeatureEngine`, `LiveVolumeBarPopulator`,
  `LiveOptionAtmPopulator`, `LiveOptionMaxPainPopulator`, `LiveOptionDepthPopulator`, and
  `ReplayLiveCommand` only) — same unfiltered chain lookup as the others.
- `CvdProxyTrialSimulator.cs`, `DepthImbalanceTrialSimulator.cs`, `DepthSpreadTrialSimulator.cs` —
  each has a lazily-loaded `nearestExpiry`/`chain` pair (`MinAsync` + `Where(... ExpiryDate ==
  nearestExpiry)`), unfiltered.
- `Program.cs` — three ad hoc CLI-diagnostic call sites (`perf-check`'s option-chain load,
  `sanity-oi-raw`'s single-instrument lookup, the MAE/MFE analysis command's per-day chain cache).

**Fix:** identical template to F61 — added `i.Underlying == "NIFTY"` to every `Where` clause above
(reusing the same string literal F61's own live-side fixes use; `NiftySignal.Host` isn't reachable
from `NiftySignal.VolumeBarData` per the project reference graph, `Host` depends on `VolumeBarData`
and not the reverse, so a shared constant isn't available without introducing a new cross-project
dependency — same reasoning F61's own `LiveOptionAtmPopulator.cs`/`LiveOptionMaxPainPopulator.cs`/
`LiveOptionDepthPopulator.cs`/`LiveVolumeBarPopulator.cs` already used, kept consistent rather than
reinvented), plus `OrderBy(i => i.ExpiryDate)` on `VolumeBarPopulator.cs`'s bare
`FirstOrDefaultAsync`. Every site carries a grep-able `// Audit finding F62 (2026-09-22)` comment.

**Current status, confirmed at fix time:** local `niftysignal_vm_copy` still has zero Sensex/Bank
Nifty rows — behaviorally a no-op today. This closes the gap before the next
`sync-vm-data-incremental.ps1` pull brings down a day with all three underlyings mixed in
`Instruments`/`Ticks` (the VM's live Host has collected all three since this morning).

**Verification:**
- Two new regression tests, same "insert a second underlying's rows first, with an earlier expiry,
  and prove the query still resolves to NIFTY" technique F61's own tests used:
  `NiftySignal.Tests/VolumeBarData/VolumeBarPopulatorUnderlyingFilterTests.cs`
  (`PopulateDayAsync_WithTwoUnderlyingsInInstrumentsTable_UsesNiftyFutureOnly`) and
  `NiftySignal.Tests/VolumeBarData/OptionMaxPainPopulatorUnderlyingFilterTests.cs`
  (`PopulateDayAsync_WithTwoUnderlyingsInInstrumentsTable_ResolvesNiftyChainOnly`). Both were
  confirmed to FAIL against the pre-fix code (reverted locally, re-ran, restored) before being
  left in place passing — real regression coverage, not defensive filtering added on faith.
- Full suite: 706/706 passing (704 pre-existing + 2 new), 0 warnings.
- Locked-baseline reproduction re-run after the fix, both byte-identical to their prior locked
  values: `trade 2026-09-08 2026-09-19 OptionsScoreThreeWaySwitchMaxPainConfirmed 90 15 2600
  --band=5` → 112 trades, 64.3% win rate, net +426.40 pts (unchanged). `crossover 2026-09-08
  2026-09-19 8 40 5 2600` → 80 trades, 55.0% win rate, net +277.20 pts (checked fresh, not compared
  against a stale prior number — this project's own history notes this figure moved once already
  after the strike-search feature was added, so it's recorded here as the new current baseline).
- No live/VM changes — this fix is entirely inside `NiftySignal.VolumeBarData`, does not touch
  `NiftySignal.Host`/`NiftySignal.Dashboard`, and nothing was deployed.

**Numbering note:** F63 is next-free going forward.


## F63 — adaptive diagnostics use a different clock precision from frozen Python research — FIXED

The independent historical residual gate observed an expected-price difference of 1.1206466e-6 points at discovery 2026-09-08 BarSeq 20 after correcting the Gaussian/IV solver. The C# exact bar retains sub-millisecond receive precision; frozen Python residual/H5 scripts consume `FlowEvolutionCsv` timestamps truncated to milliseconds. The formula harness now supplies identical serialized clock inputs, without relaxing its 1e-6 numeric limit. This validates formulas only; it does not establish integrated engine clock/quote selection or observation latency parity. **Release blocker:** resolve this clock policy explicitly and pass full pipeline residual/H5 parity. Fix now implemented: diagnostic timestamps floor to the frozen research millisecond clock, prior boundary quotes exclude later sub-millisecond updates, and stable availability groups include all same-boundary option marks while exact futures timestamps remain unchanged. Clock and availability-group regression tests plus complete engine replay comparisons are added; Full complete-engine residual/H5 integration passed all 13 discovery sessions at e91d1ca, private workflow 37324830737.

## F64 — adaptive restart option universe is not immutable — FIXED

`AdaptiveSessionCoordinator.LoadExistingAsync` reloads today's instruments from the source table. The session freezes residual components and rate, but does not persist the complete option descriptor universe used by adaptive bands and Weak2 selection. An instrument refresh or corrected strike/lot size can change restart outputs or future observation selection. **Release blocker:** persist and reuse the original universe; add a mutation/restart test. Fix: session serializes the complete option descriptors and rate; restart deserializes and validates those descriptors without querying mutable source instruments. Source mutation/rate-change regression passed at `9663879`, workflow `37320556273`. The follow-up generated migration adds a non-null text column; older unsnapshotted adaptive rows fail closed instead of inventing an original universe.

## F65 — persisted pending observations are not recovered after an outside-hours restart — FIXED

The worker enters its outside-market delay with `_live == null`; session-close finalization only runs for an existing in-memory live state. A crash near close followed by an after-hours restart can leave persisted pending H5 observations unresolved. Draining the buffer before a normal in-process close is fixed, but this startup path remains uncovered. **Release blocker:** recover/finalize persisted ended sessions and test restart across market close. Fix: dedicated ended-session recovery replays/verifies persisted source, finalizes pending observations and closes runtime both outside hours and before starting the next session. Same-day and next-day idempotent restart tests passed at `9663879`, workflow `37320556273`. Missing source history beyond reconstructed bars also fails closed.

## F66 — adaptive strong threshold bypassed frozen CSV decision precision — FIXED

Independent original-Python threshold JSON exposed discovery 2026-09-11 threshold `0.201714` versus live `0.20171428571428573`. Frozen research classifiers consume six-decimal CSV ratios. Threshold samples and classifier comparisons now use that exact six-decimal decision representation; full-precision persisted/displayed rolling metrics remain unchanged. A regression protects this separation. Prior checkpoint library parity did not establish this serialized Python gate; The complete historical suite passed at e91d1ca (private workflow 37324830737), including original Python thresholds for all 13 discovery sessions.

## F67 — completed Weak2 execution projections skipped restart verification — FIXED

Recovery replay previously verified futures, rolling, bands and residuals but selected only pending H5 observations. Completed selection/OI/H5/MFE/MAE fields could remain corrupted without stopping recovery. Recovery now recalculates completed execution projections from frozen descriptors and source ticks, compares every scalar except surrogate ID, and fails without overwriting mismatches. Trigger identity/diagnostics are compared to each reconstructed package, and recovery rejects extra persisted trigger rows. Restart fixtures now verify completed results and deliberately corrupt PnL to prove rejection.

## F68 — watch-only Host still starts legacy paper executors — FIXED

`LiveOptionsScoreEngine` and `LiveFuturesCrossoverEngine` are hosted workers calling `LivePaperTradeExecutor.OpenAsync/CloseAsync`. Their registration contradicted the roadmap's deployment gate that all order/paper actions are disabled. Both registrations are removed for the adaptive watch-only cutover; code, tables and existing records remain for rollback. Ingestion and legacy volume-bar persistence continue. This deliberately stops new legacy paper observations too. Full Host regression and release call-site review must pass before deployment.

## F69 — full solution omitted four root projects — FIXED

`NiftySignal.slnx` omitted BacktestData, DataSync, MetricTrials and VolumeBarData. Some were built only transitively; a standalone harness relying on a completed Release solution build could not find their Release reference assemblies. All root projects and the Dashboard validation console are now explicitly included. The browser gate uses this single complete solution build, removing the reference-skip shortcut. Full Release build must pass for these formerly omitted projects too.

## F70 — missing discovery history silently changes strong threshold — FIXED

Historical bootstrap skipped incomplete discovery dates and returned successfully even with no source. The coordinator could freeze a threshold from only the available subset; a fresh start more than 45 days later also excluded mandatory older dates. Bootstrap now queries all prior source dates and refuses readiness until every required prior discovery session has been seeded. Cancellation propagates. A missing-source regression proves no daily configuration can be based on a silently partial discovery history; the real-source 13-session gate must still pass after this change. Production must have the frozen discovery raw source available before the observer starts.

## F71 — Dashboard selector refresh can be lost during prerender or an active read — FIXED

The first browser selector gate found five requested rows still displaying ten. Controls could be changed before the Blazor circuit became interactive; additionally ReloadAsync's nonblocking semaphore skipped any selector/push arriving during another read, while header polling saw unchanged sequence and never repaired the requested row count. Controls now wait for interactive rendering and refresh requests queue behind an active read using the latest selector. Disposal cancels/drains readers. The browser test explicitly blocks a PostgreSQL future-bar read, changes rows again, proves the server received that selection, then unlocks and requires all three grids to show the latest count. Latency samples also wait two animation frames to include browser painting.

**Validation checkpoint, 2026-10-05:** F67 completed-projection corruption rejection passed seven real PostgreSQL recovery boundaries; F68 hosted call-site review confirms only ingestion, volume-bar writer and adaptive observer are started, and full regression passed; F69 complete Release solution builds with zero warnings/errors; F70 missing-history unit regression and actual-source 13-session historical gate passed; F71 actual blocked PostgreSQL reader/selector race and 36 browser combinations passed. Public run 37332433765 at 64ece10: 48 adaptive / 1,123 total tests, P95 painted three-grid latency 158.8837 ms, max 181.7617 ms. Private frozen historical run 37330577623 at a43ca3c passed all 13 discovery sessions, source/bootstrap, independent band/rolling, residual/H5 and 87 historical engine resets. This is CI/historical validation; VM service/restart/broker checks remain manual. The final fixture strengthens distinguishable CE/PE, residual and unavailable-row values without changing production calculations.

## F72 — Dashboard shows immutable session provenance as though it were runtime build — FIXED

The session source/build card describes the process that originally froze the daily configuration. After deployment or recovery it can retain an older SHA; no current Dashboard assembly identity was exposed. A separate always-visible full Dashboard branch/SHA/build UTC is now rendered from its own assembly, while existing cards are explicitly labeled session provenance. A real browser assertion requires the current Dashboard SHA/branch to match the checked-out CI build, independently of the synthetic session branch. Host current provenance remains in its startup log. This affects traceability only; no adaptive calculation or session is rewritten.

Validation passed at b40851c48afddcc5b693d5ee75b15a24738e23dc, run 37354356288: real Chromium verified full current Dashboard SHA/branch independently of the session branch; zero compiler warnings/errors, 48 adaptive / 1,123 total tests, migrations/restart/selectors and both publish outputs pass. P95 142.0 ms / max 144.8 ms in isolated CI.

**Numbering note:** F73 is next-free going forward.


## F73 — screenshot sender fallback hides exception identity — FIXED, validation pending

VM pre-live jobs 1–4 on 2026-10-06 reached DeliveryUncertain with only "Sender interrupted". Manual upload of the captured 38,226-byte PNG with Dashboard-local bot/chat succeeded (HTTP 200, acknowledged message ID 133632), but this does not establish the application sender's root cause. The processor fallback now records exception and inner-exception type names only, preserving ambiguous-delivery/no-resend behavior and excluding messages/stack traces which can reveal bot tokens. A regression injects a nested sender exception containing a secret and checks both sanitized diagnostics and no retry. This fixes diagnostic visibility, not the unresolved VM delivery failure. Automated VM delivery remains pending.


## F74 — real Telegram token colon is interpreted as URI scheme — FIXED, validation pending

The document sender used `new Uri(baseUri, "bot" + token + "/sendDocument")`. Real numeric-prefix bot tokens contain a colon, making that second argument a syntactically absolute URI with scheme `bot123456`, which HttpClient rejects before upload. Tests and the Chromium upload emulator used colon-free tokens and missed this production-only path. The endpoint now starts with `/bot`, retaining the configured HTTP(S) origin; unit tests assert the entire HTTPS endpoint with a representative colon-bearing fake token, and the full browser harness uses the same token format. The outer sender diagnostic also exposes safe exception type names (F73). No strategy, Host or database model change. Previous uncertain jobs remain untouched; VM acceptance requires a new acknowledged pre-live delivery after Dashboard-only deployment.


## F75 — late start silently freezes zero/partial opening volume — FIXED, validation pending

Require observed futures coverage in every minute of the opening window and positive opening volume. Otherwise freeze the median prior validated opening volume with explicit source/input/sample/start metadata, excluding fallback/current/future sessions. Missing offline ticks are not fabricated; warm-up begins from a real cumulative-volume baseline. Missing residual quotes stay unavailable. Existing sessions never silently refreeze. Synthetic late/partial/normal/restart tests added; median fallback trading performance remains unvalidated.

## F76 — decision readiness not explicit and replay can prematurely mark Live — FIXED, validation pending

Shared ten-valid-contiguous-completed-bar gate protects actionable Weak2 and Dashboard readiness. Verified replay bars count after restart. Keep Rebuilding status while replay inserts rows; only complete reconciliation restores Live. Incomplete/invalid/missing rows do not satisfy the gate. Future paper/order consumers must use this gate plus their own risk/data/execution checks. No paper or real orders enabled.

## F77 — deployment rebuilds twice and performs per-file WinRM copies — FIXED, validation pending

One Release build with exact provenance is reused by full unit tests and publish. Only changed files are packaged into one ZIP, transferred before remote service stop, extracted retaining VM-local settings. Reuse version-specific installed Chromium. Print step timing. Tests retain source/SHA guards and verify delta ZIP nesting, secret retention and traversal rejection. Actual Windows/WinRM speed measurement remains pending.

## F80 — Live Quote mixes underlying/expiry slots and can disappear or freeze zero selection — FIXED, verification in progress

Dashboard queries admitted NIFTY, BANKNIFTY and SENSEX into one spot/future/option quote map. Nearest expiry was chosen across all three underlyings; a nearer SENSEX expiry could exclude NIFTY options entirely, while other index ticks overwrote spot/future and recentered CE/PE controls. Initial empty option data also froze selected strike at zero. The row was hidden whenever session metadata was stale, concealing useful data/status.

The Dashboard now scopes its quote universe, option-chain spot anchor and on-demand subscription anchor to NIFTY. Roles/baselines/persisted quotes publish atomically, and a slower poll cannot overwrite a newer push. Current-day persisted LTP/depth initializes the panel after restart. CE/PE selection waits for a positive known strike and remains in the choices when spot moves. The panel remains visible with an explicit session-status badge. Deterministic rendered-component and real PostgreSQL/Chromium regressions cover mixed underlyings/expiries, prices/depth, day rollover, stale poll, row visibility and selector retention. This changes Dashboard display only; Host, FlatTrade feed, schema and strategy calculations remain untouched. F78/F79 belong to the separately saved, unmerged Upstox integration branch.
