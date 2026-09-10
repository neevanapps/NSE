# External Review Tracker

**Purpose:** a running ledger of findings from outside review (friends, third-party audits, AI
review) that were independently checked against the actual current code — not the review's
description of the code — and confirmed to be real, current, and worth fixing. Anything a review
flagged that turned out to already be fixed, or to be a claim about signal *predictive validity*
that can't be resolved without the backtester (see below), is recorded here too, but separately,
so effort doesn't get spent twice or spent on the wrong thing.

Numbering continues the existing `PENDING (audit finding FNN)` / `DEFERRED (audit finding FNN)`
source-comment convention (see `[[feedback_pending_items_in_source]]`-style discipline already
used throughout this codebase) — F46 is the next free number as of 2026-09-10. Nothing here has
been added as a source comment yet; this file is the tracking layer until each item is actually
implemented, at which point it gets the usual PENDING comment at its code site and a line here
marking it done.

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

## F32 — The backtest runner (scoped 2026-09-10, not yet built)

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

**Recommendation, not yet decided:** start with A for iteration speed while the runner itself is
being built and validated, keep B available as a periodic cross-check once the runner's output looks
trustworthy — the same "don't fully trust a new measurement tool until it's been sanity-checked
against a known-good path" discipline this project already applies everywhere else (BS-consistent
test pricing, the F19-F45 verification-by-test pattern from this week). This needs your call before
any of it gets built, not mine.

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
