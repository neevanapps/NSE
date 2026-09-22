# NiftySignal — Core Score Live Wiring: Two Strategies for Next-Week Paper-Trade Watch (2026-09-13)

## Context

Four days of backtesting (`NiftySignal.MetricTrials/CoreScoreOptionSimulator.cs`) converged on two
tradeable strategies built on the SAME 8-metric Core score (DepthImbalance 0.25 / ItmSkew 0.065 /
FutureCvdNet5Min 0.12 / NotionalVolumeRatio 0.14 / GammaExposure 0.06 / TrendReversion15m 0.10 /
BasisChange 0.08 / OiChangeDiff15m 0.10, `K=1.0`):

1. **Hysteresis threshold** — enter at `|score| >= 30`, exit only once the score crosses to the
   OPPOSITE threshold (a long call exits when score `< -30`, not merely `< +30`). No SL/TP, no
   time-based exit. Backtest result: 47 trades / 83.0% win rate / net +200.73 pts (+163.0%).
2. **Fast/slow crossover** — smooth the same score over a 10-min and a 30-min real-time trailing
   window; a position flips (closes + immediately reopens opposite) on every crossover of fast
   vs. slow. Always positioned once the first cross fires. No SL/TP either. Backtest result: 81
   trades / 55.6% win rate / net +124.13 pts (+103.8%), best of five window combos tried.

The user has decided to run BOTH live, in parallel, starting next week, purely to watch real
behavior (still paper trading, no real capital) — NOT because either is considered "done."
Further backtesting with more data continues separately; this plan is scoped to wiring the two
*already-tested* strategies into the live pipeline exactly as validated, changing nothing about
their logic in the process.

**This is the first time this session's Core-score work touches `NiftySignal.Host`/
`NiftySignal.Rules`/`NiftySignal.Execution`/`NiftySignal.Dashboard` — every prior artifact
(`CoreScoreOptionSimulator.cs`, `SessionRankTracker`, the weight cuts, the hysteresis/crossover
designs) lived entirely in the backtest-only `NiftySignal.MetricTrials`/`NiftySignal.BacktestData`
projects, deliberately, per this project's own standing rule not to touch live code pre-edge. The
user's own explicit instruction here is what lifts that rule for this specific, now-decided pair
of strategies** — nothing else about the "no Host/Dashboard changes pre-edge" convention changes.

**Confirmed via three parallel research passes before writing this plan** (live scoring engine,
live trading/risk engine, dashboard) that this is a substantially bigger lift than "wire in two
already-built formulas":

- Of the 8 Core-score terms, only 1 (`NotionalVolumeRatio`) is close to reusable from existing
  live code with a small formula adjustment. 3 more (`DepthImbalance`, `ItmSkew`, `OiChangeDiff15m`)
  have the right raw ingredients live but in a **different band or aggregation** that must not be
  reused as-is — reusing them unmodified would silently trade a different metric than the one
  actually backtested. **4 of the 8 (`FutureCvdNet5Min`, `TrendReversion15m`, `BasisChange`,
  `GammaExposure`) need building fresh, not adapting** — the first three have no live equivalent at
  all; `GammaExposure` looked reusable at first pass but a closer check (`docs/SCORE_CANDIDATES.md`)
  found the backtest used each strike's own individually-solved-IV Gamma, while live's existing
  `ComputeGammaExposure` uses a single shared ATM vol for every strike — a materially different
  formula, caught only by cross-checking the actual backtest evidence rather than trusting a
  same-sounding method name.
- Live has **no `SessionRankTracker`/percentile-rank normalization anywhere** — it exclusively
  uses fixed-duration `WelfordRollingWindow` z-scores. The Core score's whole "no hardcoded
  magnitude, rank against session-so-far" design point is absent from live and must be ported,
  including its own restart-safety story (which doesn't yet exist for a rank tracker specifically
  — `SeedHistory`'s existing replay-from-persisted-snapshots pattern is the template, not an
  existing solution).
- Live has **zero notion of "more than one strategy" anywhere** — `LiveTradingEngine`,
  `EntryRuleEvaluator`/`ExitRuleEvaluator`, `RulesetConfig`, and `PaperTrade` are all, structurally
  and by convention, hardcoded to one account-wide strategy. Every open-position/risk-counter query
  is an unscoped DB read (no `StrategyId` filter exists because no such column exists).
- `EntryRuleEvaluator`/`ExitRuleEvaluator`'s existing rule shapes (`MinAbsScore`+sustain-duration
  entry; `StopLoss`/`PartialBook`/plain-zero-crossing-`ScoreFlip`/`TimeStop` exit) do **not** match
  either backtested strategy's actual tested logic (hysteresis-band-vs-opposite-threshold; discrete
  crossover-event with no magnitude check at all) and must not be force-fit via config tricks (e.g.
  "set StopLossPct absurdly high to neutralize it") — that risks silent behavioral drift from what
  was actually validated. New, purpose-built decision logic is the safer path (see A7 below).

## Decisions already made (for the record)

- **Crossover window: 10min fast / 30min slow** — the best-performing, lowest-trade-count combo
  tested (backtest: 81 trades, +124.13 pts / +103.8%).
- **Old 14-component composite score: retired from live trading**, not deleted — `LiveFeatureEngine`
  keeps computing and persisting `CompositeScore` (harmless, already-tested diagnostic value;
  nothing downstream currently depends on removing it), but `LiveTradingEngine`'s existing
  `EvaluateCadenceAsync` call is **removed from the live cadence loop** so it can no longer open new
  positions. `LiveTradingEngine.cs` itself is left in place, untouched, in case it's ever wanted
  again — this is a wiring change (stop calling it), not a deletion.
- **Risk limits: per-strategy independent** — each of the two new strategies gets its own daily
  loss cap, consecutive-loss breaker, and daily trade-count limit; one strategy tripping its limit
  does not stop the other.
- **Ratio composite: retired entirely**, not just its dashboard panel — `RatioScoreCalculator`,
  `RatioComponentInputs`, `RatioMetricMath`/`RatioMetricScales` usage, and every `Ratio*` computation
  currently inside `LiveFeatureEngine.ComputeCadence` are removed from the live pipeline. The
  already-persisted `ScoreSnapshot.Ratio*` **columns are kept, just permanently null going
  forward** (no destructive migration dropping historical ratio data) — cheaper and lower-risk than
  a drop-column migration, and the historical data stays queryable if ever wanted.

## Assumptions I'm making explicit (flag if any of these are wrong)

1. **Capital pool is shared, not split.** The user asked for per-strategy *risk-limit* independence
   (loss caps, consecutive losses, trade counts) but didn't address capital allocation specifically.
   I'm assuming `Capital.Total`/`LotSize`/`LotsPerTrade` stay one shared config (both strategies draw
   from the same paper-capital pool, same lot sizing), since this is still paper trading and nothing
   in the backtest work implied different position sizing per strategy. If you want each strategy to
   have its own capital ceiling, say so and I'll split `Capital` into two configs too.
   **Consequence worth flagging (raised on review): with a shared pool, the two strategies CAN
   block each other** — if Hysteresis's open position already commits enough capital, Crossover's
   entry can be rejected by the committed-capital check (A8) even though Crossover's OWN risk limits
   are fine. This means next week's combined live P&L is not simply comparable to "backtest A's
   +200.73 plus backtest B's +124.13" — the two backtests ran with full, uncontested capital each;
   live, they compete for the same pool. Don't read a lower combined live number as "the strategies
   underperformed the backtest" without checking whether capital contention actually blocked entries
   first.
2. **`ScorePanel.razor` (the "Universal Directional Score" panel — there's no file literally named
   `CompositeScorePanel`; that's what you mean by it) gets fully repurposed** to show the 8
   Core-score components (replacing the old 14) plus both strategies' current position state
   (flat/long-call/long-put, entry price, current score). The old 14-component composite's
   diagnostic values, though still computed, would no longer be visible on the dashboard unless a
   second panel is kept for them. If you want the old panel kept around too (as a second,
   still-visible diagnostic), say so — it's an easy addition, just wasn't the plan's default given
   "Composite score panel will have these metrics we have chosen" (singular, replacing content).
3. **No new SL/TP or other safety net gets added** for going live, beyond what was already
   backtested (no SL/TP, ForceClose/SquareOff at session end only). Both strategies deliberately
   go live with the exact same discipline they were tested under; the only exit conditions are:
   hysteresis-threshold-cross / crossover-flip / session close.
   **On review, a question was raised about next week's Tue 15 Sep being a 0-DTE expiry day, asking
   whether "renormalize and hold with no SL into expiry" needs a fresh decision since the 4-day
   backtest supposedly doesn't cover it — checked directly against the DB and this is factually
   incorrect: 08 Sep IS the 0-DTE expiry day (`CadenceContexts.NearestExpiryDate = AsOfDate`,
   confirmed), and every single backtest run this session included it, trading normally with
   `ItmSkew` nulled and renormalized (Hysteresis threshold=30: "2026-09-08: 10 trades, 70.0% win
   rate, net 31.05 pts"). This exact scenario is already real, tested data, not an open gap — no new
   decision needed for 15 Sep specifically. **Caveat (raised on second review, fair): this is n=1
   expiry day, not "the same Tuesday" or a generally-validated 0-DTE regime — it's one real data
   point, not a robust sample. Doesn't justify a new rule or gate, but also isn't grounds to treat
   0-DTE days as fully de-risked going forward; just the honest weight of the evidence.**
4. **A shadow-mode dry run** (see Batch 6 below) runs for at least one live session before either
   strategy is allowed to actually write `PaperTrade` rows — purely computing and logging what each
   strategy *would* do, with zero trading side effects. Given "please be extra careful," I'm
   treating this as required, not optional, even though it wasn't explicitly asked for.

## Architecture

### A1 — New `SessionRankTracker` location + live restart-safety

`SessionRankTracker` currently exists only in `NiftySignal.Backtest` and (duplicated)
`NiftySignal.MetricTrials` — neither is referenced by `NiftySignal.Host`. Move a copy into
`NiftySignal.Features` (already referenced by `NiftySignal.Host`), unchanged, matching this
project's own established precedent of a deliberate, documented duplication when sharing would
over-couple unrelated projects (see `NiftySignal.MetricTrials`'s own copy's doc comment). On Host
restart, replay today's persisted `CoreScoreSnapshot` raw values (queried by
`ComputedAt >= todayIstMidnightUtc`, same query shape `MarketDataIngestionWorker.SeedEngineHistoryAsync`
already uses for `SeedHistory`) into **SEVEN** fresh `SessionRankTracker` instances, chronologically
— **not eight** (caught on second review, same class of bug as the earlier "all 8 feed RankSigned"
mistake): `DepthImbalance`, `ItmSkew`, `FutureCvdNet5Min`, `NotionalVolumeRatio`, `GammaExposure`,
`OiChangeDiff15m`, and `BasisChange` each get one; `TrendReversion15m` gets none, since it's never
ranked (see A3). Same pattern as every other restart-seedable structure in `LiveFeatureEngine`,
applied to a mechanism that doesn't have precedent live yet.

### A2 — New `CoreScoreSnapshot` entity (own table, not bolted onto `ScoreSnapshot`)

Per `ScoreSnapshot.cs`'s own doc comment ("a scoped-down v1... splitting them apart later is
cheap"), a new table is the established convention here, not another dozen nullable columns on an
already-322-line entity. Columns: `Id`, `ComputedAt`; 8 raw values (`DepthImbalanceRaw`,
`ItmSkewRaw`, `FutureCvdNet5MinRaw`, `NotionalVolumeRatioRaw`, `GammaExposureRaw`,
`TrendReversion15mRaw`, `BasisChangeRaw`, `OiChangeDiff15mRaw`, all `double?`); 8 signed
(rank-transformed) values (`*Signed`, all `double?`, diagnostic — lets a dashboard or a future
investigation see the same rank-and-sign breakdown this session's SQL investigations relied on
without re-deriving it); `CoreScoreRawInstant`/`CoreScoreRaw`/`CoreScore` (mirroring
`CompositeScoreRawInstant`/`Raw`/final-score naming already established), `IsWarmedUp`,
`WeightSetVersion`; `CoreScoreFast`/`CoreScoreSlow` (the two crossover moving averages, needed both
for the crossover engine's own decision AND as a dashboard diagnostic). New EF migration under
`NiftySignal.Persistence/Migrations`.

### A3 — The 8 metrics in `LiveFeatureEngine.cs` — reuse vs. adapt vs. build fresh

Each gets its OWN new method (not sharing the old composite's existing `Compute*` methods, even
where the underlying idea overlaps) — this is deliberate: the old composite's methods stay
untouched so its still-computed diagnostic value never silently changes shape, and the Core-score
methods are free to use the exact band/formula the backtest actually validated without threading a
band parameter through code whose other caller needs a different band.

- **DepthImbalance** (build fresh, Itm2Atm1 band) — reuses the per-strike `DepthImbalanceFromLastCadence`-style
  accumulator concept already proven in `ComputeDepthImbalance`, restricted to the Itm2Atm1 band
  (calls {-2,-1,0}, puts {0,+1,+2} by strike offset) instead of live's existing nearest-2-strikes
  band, call-avg minus put-avg (not the existing sum-then-ratio construction).
- **ItmSkew** (build fresh) — **exact formula, unambiguous**: within the Itm2Atm1 band, take the
  PLAIN arithmetic mean of every call strike's IV in that band, separately the plain mean of every
  put strike's IV, then `putAvgIv - callAvgIv` (`CoreScoreOptionSimulator.cs:280-289`) — a
  band-average of ALL Itm2Atm1 strikes' IV on each side, NOT a two-single-strike difference and NOT
  OI-weighted. Nulled outright on a 0-DTE day (check `featureEngine.NearestExpiry == today`, same
  check `LiveTradingEngine.cs:121` already does for `isExpiryDay`). Neither existing live IV-skew
  method matches this shape — `ComputeIvSkew` solves exactly ONE call strike + ONE put strike (a
  dynamic ~1-sigma OTM pair, not a band average); `ComputeIvSkewRatio25Delta` is a 25-delta RATIO,
  not a difference. Reuse only the underlying "solve this strike's IV" primitive each of those
  already calls, not their strike-selection or aggregation logic — the actual aggregation (loop
  over the Itm2Atm1 band, average each side, subtract) is new. **Use the SAME underlying-price
  convention every other live Greek/IV solve in this class already uses (the synthetic-forward
  convention, per `BlackScholes.cs`'s own doc comment) — not a different one** (optional flag from
  review: if Batch 3's diff shows `ItmSkewRaw` mismatching while everything else matches, this
  specific convention choice is the first place to check, since IV is more sensitive to the
  underlying-price input than most of the other 7 terms). **Deliberately duplicating the
  0-DTE-day check as a literal condition here rather than sharing a helper with
  `LiveTradingEngine`** — matches this codebase's own stated convention (see
  `EntryRuleEvaluator.MinIvRankSessionsForGate`'s own doc comment on why duplicated literals beat
  cross-project coupling for a single boolean).
- **FutureCvdNet5Min** (build fresh) — the single largest new-build item. **The simulator never
  computes this itself — it just reads a pre-computed value off `CadenceContext.FutureCvdProxyNet5Min`
  (`CoreScoreOptionSimulator.cs`: `futureCvdRaw = ctx?.FutureCvdProxyNet5Min`), which was populated
  offline by `NiftySignal.BacktestData/CadencePopulator.cs`'s `FutureCvdProxyAccumulator` class
  (`CadencePopulator.cs:1012-1046`).** That class, not any live method with a similar-sounding name,
  is the actual byte-for-byte source of truth: `ApplyTick(lastPrice, depth, volumeDelta)` skips the
  tick entirely if `Bid1Price<=0 || Ask1Price<=0 || volumeDelta<=0`, otherwise computes
  `midpoint=(Bid1Price+Ask1Price)/2` and signs the WHOLE tick's `volumeDelta` as `+` if
  `lastPrice >= midpoint` (note: `>=`, ties go buy-leaning) or `-` otherwise, accumulated into a
  per-cadence net. Port this exact class's logic for the FUTURE token specifically (it does happen
  to match the same quote-rule `ComputeVolumePcrAndCvdProxy`'s option-chain version already uses,
  per that class's own doc comment — but copy `FutureCvdProxyAccumulator`'s own code, don't
  re-derive the rule from the option-chain method's name and risk a subtly different boundary
  condition slipping in). Accumulate per-tick in `OnTick` alongside the existing future-VWAP
  accumulation (`OnTick` lines 420-432 are the direct template — same per-tick, future-token-only
  trigger point), plus a new trailing-5-real-minute rolling-SUM `Queue<(DateTimeOffset, long)>`
  evaluated in `ComputeCadence` (mirroring `NiftySignal.BacktestData`'s own
  `RollingNetSumWindow`/the backtest simulator's `oiDiffWindow`/`trendWindow` real-time-eviction
  pattern) — this rolling-sum-of-per-cadence-nets is a SEPARATE step from `FutureCvdProxyAccumulator`
  itself, which only produces the per-cadence value.
- **NotionalVolumeRatio** (adapt) — `ComputeRatioNotionalVolumeRaw`'s ATM±5 notional-by-side
  construction is exactly right; only the output transform changes (backtest wants
  `log(putNotional/callNotional)` directly, not the raw ratio `ComputeRatioNotionalVolumeRaw`
  currently returns for the now-retired ratio composite to log-clip separately). Since the ratio
  composite is being removed entirely (per the decision above), this method can be **moved**
  (renamed, band unchanged) into the new Core-score code rather than kept duplicated.
- **GammaExposure** (build fresh — NOT an adapt of `ComputeGammaExposure`). **Confirmed via
  `docs/SCORE_CANDIDATES.md:697-698`: the backtest tested "version A" — each strike's OWN
  individually-solved-IV Gamma — explicitly NOT live's existing `ComputeGammaExposure`, which uses
  a single shared ATM reference vol for every strike ("version B", the docs' own words, "not yet
  built" as of that note).** Reusing `ComputeGammaExposure` with just an ATM±10 filter added would
  silently trade version B, a formula never actually backtested — the sign/behavior could differ in
  ways nothing here has validated. Build a new per-strike loop that solves each strike's own IV
  (reuse `BlackScholes.Calculate`'s existing per-strike solve, same primitive `ComputeIvSkew`/
  `ComputeGammaExposure` both already call, just not sharing their strike-selection or shared-vol
  assumption), reads that strike's own Gamma, signs by side (`+` call, `-` put), sums across ATM±10,
  matching `CoreScoreOptionSimulator.cs:291-294` exactly.
- **TrendReversion15m** (build fresh) — needs a genuinely new live concept: a per-cadence "future's
  own close-to-close change," which doesn't exist today (only continuous tick state does). Track
  the future's last-seen price at each `ComputeCadence` call (a new `_previousFuturePrice` field,
  same shape as every other `_previousXxx` tracker already in this class), diff it each cadence,
  push into a trailing-15-real-minute `Queue<(DateTimeOffset, double)>`, compute
  `-1 * (net / pathLength)` exactly as the backtest does — already bounded [-1,1]. **NOT ranked —
  fed into the weighted sum directly, bypassing `SessionRankTracker` entirely** (confirmed against
  `CoreScoreOptionSimulator.cs:372`: `trendSigned = trendReversionSigned;`, no `RankSigned` call).
  This is the one term of the eight that skips ranking; get this exception right, don't
  generalize "everything gets ranked" from the other seven.
- **BasisChange** (build fresh) — reuses `TrendReversion15m`'s new per-cadence future-price-change
  tracker, adds a parallel per-cadence spot-price-change tracker (`_previousSpotPrice`, same
  shape), computes `futureChange - spotChange` per cadence. **Unlike TrendReversion15m, this ONE
  IS ranked** — negate the raw value first, then rank via `RankSigned` (`CoreScoreOptionSimulator.cs:370`:
  `RankSigned(basisChangeRaw is { } b ? -b : null, basisRank)`) — negate-then-rank, not
  rank-then-negate; get the order right. **Not** the same as the existing `FuturesBasisRaw` (a
  basis *level*, `futureMid - spotMid`) — a structurally different quantity despite the similar
  name; must not be confused with or substituted for it.
- **OiChangeDiff15m** (build fresh raw ingredient; reuse only the band/loop shape) — **live's only
  existing OI-delta machinery (`ComputeOiBuildupNet`) is unsuitable as the per-cadence increment**:
  it reads OI against `_oiLookback`'s ~4-minute-old snapshot (`OiComparisonWindow`), specifically to
  dodge OI's slow real-world refresh rate. Feeding that 4-minute-lookback delta into a 15-minute
  ROLLING SUM would overlap successive readings and badly over-count flow — not what the backtest
  does (its own `OpenInterestDelta` is a genuine previous-real-cadence-to-this-cadence diff, mostly
  zero with periodic jumps as the OI feed itself refreshes). Build a NEW, simple per-strike tracker
  (`_previousOiByStrike`, same `_previousXxx` shape as everything else in this class) comparing
  each cadence's OI against the immediately-prior cadence's OI — NOT `_oiLookback`'s 4-minute
  comparison — restricted to ATM±2 (`PersistedStrikeBand`, matching `ComputeOiBuildupNet`'s band
  only, not its lookback mechanism), summed naively as `CallOiDelta - PutOiDelta` (no
  classification/weighting), then fed into the new trailing-15-real-minute rolling-SUM queue —
  same `Queue<(...)>` pattern as `FutureCvdNet5Min`/`TrendReversion15m` above.

The other 6 terms (`DepthImbalance`, `ItmSkew`, `FutureCvdNet5Min`, `NotionalVolumeRatio`,
`GammaExposure`, `OiChangeDiff15m`) feed a new `RankSigned`-equivalent helper (port directly from
`CoreScoreOptionSimulator.RankSigned`, unchanged) using 6 of the 8 new `SessionRankTracker`
instances from A1 (`BasisChange` uses a 7th; `TrendReversion15m` uses none, per above), read-before-add
ordering preserved exactly (self-inclusion-safe, no same-bar leakage — the one correctness rule
this whole session has been strictest about).

### A4 — New `CoreScoreCalculator` in `NiftySignal.Scoring`

Modeled on `RatioScoreCalculator`, not `CompositeScoreCalculator` — the Core score's
renormalize-by-present-weight policy (skip absent terms, scale by present weight) matches
`RatioScoreCalculator`'s optional-component handling exactly, not the main composite's
all-required policy. Inputs: `CoreScoreComponentInputs` (8 nullable `double?` signed values, from
A3). Weights: `CoreScoreWeights` record ported directly from `CoreScoreOptionSimulator.cs`'s own
record (same 8 named weights, same values, same `K=1.0`) — copy the values exactly, do not
re-derive them, **and copy the renormalize-by-present-weight formula exactly too (divide by the
SUM OF PRESENT weights, not by 1.0)** — the 8 weights sum to 0.915, not 1.0, by design; the
backtest never rescales them to 1.0 and neither should this port (see `CoreScoreOptionSimulator.cs`'s
own `Total` doc comment: "left as-is rather than silently rescaled"). Output: reuses
`ScoreComponentBreakdown` (already used by both existing calculators) for the per-term diagnostic
rows the dashboard will want.

**`CoreScoreCalculator` is called exactly ONCE per cadence, no `rawOverride`.** Unlike
`CompositeScoreCalculator`/`RatioScoreCalculator` (which both smooth their raw value BEFORE tanh,
then re-invoke `Calculate` with that smoothed raw as an override), the Core score's crossover
smoothing happens AFTER tanh, on the already-final `CoreScore` — it is a plain arithmetic mean of
already-computed scores, not a second pass through the calculator (confirmed against
`CoreScoreOptionSimulator.cs:558-562`: `fastWindow.Enqueue((timestamp, scoreValue))` enqueues
`cadence.Score`, the tanh'd value, not a raw). See A5 for exactly where that averaging happens —
it does not touch `CoreScoreCalculator` at all.

### A5 — Crossover's fast/slow smoothing, live

Two new FIFO/real-time-trailing structures inside `LiveFeatureEngine` (or wherever `ComputeCadence`
assembles the final Core score) — a 10-min and a 30-min trailing average of the per-cadence
`CoreScore` itself (the already-tanh'd final value, `cadence.Score` in the backtest — NOT the raw
pre-tanh value, and NOT routed back through `CoreScoreCalculator` — see A4). Persisted as
`CoreScoreFast`/`CoreScoreSlow` on `CoreScoreSnapshot` (A2).

**Restart behavior: MUST replay on restart, NOT the ratio composite's accepted-gap treatment.**
The ratio composite's fast FIFO (`RatioCompositeScoreFast`) can tolerate a cold restart because it
feeds a threshold-gated composite that isn't the sole driver of an already-open position. The
crossover strategy is different: it's **always positioned**, so an already-open position depends
entirely on a future crossover event to ever exit (besides SquareOff). If both windows reset empty
on restart, `SimulateDayCrossover`'s own warm-up guard (`fastWindow.Count == 0 || slowWindow.Count
== 0 → continue`) means NO crossover can be detected for up to 30 minutes post-restart — an
already-open position would sit with no working exit path (beyond end-of-day SquareOff) for that
whole window. Since `CoreScoreSnapshot.CoreScore` is already being persisted every cadence (A2),
replaying it costs nothing extra to build: on Host restart, query today's persisted `CoreScore`
values (`ComputedAt >= todayIstMidnightUtc`, same query shape `SeedHistory` already uses) and
replay them chronologically into fresh `CoreScoreFast`/`CoreScoreSlow` queues before the cadence
loop resumes — same pattern as every other restart-seeded structure in this class, just not yet
applied to this one. Verify explicitly during Batch 6 (see Verification) by deliberately restarting
Host mid-shadow-session and confirming the crossover engine's own log shows sane fast/slow values
immediately, not a fresh 30-minute blind spot.

**Replaying the queues alone is not enough — `CoreScoreCrossoverRules`' own `previousDiffSign` must
be seeded from the SAME replay, not left null (caught on second review, and it's a real one).** The
simulator's own logic (`CoreScoreOptionSimulator.cs`) treats `previousDiffSign is null` as "first
warmed-up reading, establish baseline, not a cross" — it does NOT immediately fire a flip. That's
correct for a fresh day with no open position. But after a mid-day restart with a position ALREADY
open, if `previousDiffSign` resets to null, the next real cadence just silently re-baselines to
whatever the CURRENT sign happens to be — even if that current sign already disagrees with the
open position's side (the market could easily have moved during the downtime). The position would
then sit open, already on the wrong side of the live signal, with the code no longer aware anything
changed — it would wait for yet another fresh crossover FROM that new, silently-adopted baseline,
which could be a long, unbounded wait. Fix: replay the fast/slow queues (above), then run the SAME
sign-comparison logic across that replayed history to derive what `previousDiffSign` would actually
be at the most recent replayed cadence, and seed the live engine's rules instance with that value —
this restores exact continuity, as if the process had never gone down, and any crossover that
genuinely happened during the downtime is already reflected correctly.

### A6 — `PaperTrade.StrategyId` + migration

New required field, `StrategyId` enum (`NiftySignal.Domain.Enums`): `LegacyComposite`,
`CoreScoreHysteresis`, `CoreScoreCrossover`. Every existing (currently unscoped) query inside
`LiveTradingEngine.EvaluateEntryAsync`/`EvaluateCadenceAsync` that reads `db.PaperTrades` gets a
`.Where(p => p.StrategyId == ...)` filter added — `openPositions` (line 52), `tradesToday` (83),
`lastEntrySameDirection` (86-90), `closedTodayPnls` (94-98). New EF migration; existing historical
`PaperTrade` rows backfilled to `StrategyId = LegacyComposite` in the migration's `Up()` (the only
strategy that has ever traded so far).

### A7 — New, purpose-built entry/exit decision logic per strategy (NOT `EntryRuleEvaluator`/`ExitRuleEvaluator`)

Confirmed by reading both evaluators in full: neither's existing rule shape matches what was
actually backtested. `EntryRuleEvaluator` requires a `MinAbsScore` + sustain-duration model with no
concept of a hysteresis band or a discrete crossover event; `ExitRuleEvaluator`'s priority chain
(SquareOff > StopLoss > PartialBook > **plain zero-crossing** ScoreFlip > ScoreDecay > TimeStop)
would reintroduce exactly the SL/TP/time-gate mechanisms both backtested strategies deliberately
have none of, and its `ScoreFlip` is a symmetric zero-cross, not the wider hysteresis band actually
validated. Forcing either strategy through this machinery via config tricks (absurdly wide
`StopLossPct`, huge `MaxHoldMinutes`) would risk silent drift from the tested behavior and is
explicitly rejected here in favor of new, minimal, purpose-built pure functions:

- `CoreScoreHysteresisRules.Evaluate(...)` — reads the **instant `CoreScore`** (this cadence's own
  `100*tanh(...)` value) directly, NOT `CoreScoreFast`/`CoreScoreSlow` — the fast/slow smoothed
  series exists only for the crossover strategy; making this unmissable here since both strategies
  now read from the same `CoreScoreSnapshot` row and it would be an easy field-mixup otherwise.
  Entry: `|score| >= EntryScoreThreshold` (30, from the backtest). Exit: hysteresis band exactly as
  coded in `CoreScoreOptionSimulator.cs`'s `scoreInvalidated` check (a held call exits only once
  `score < -threshold`; a held put only once `score > +threshold`), plus SquareOff at session
  close (the one shared, non-negotiable safety exit — reuses `config.Session.SquareOffTime`,
  the same field `ExitRuleEvaluator` already reads).
- `CoreScoreCrossoverRules.Evaluate(...)` — entry/flip: a genuine crossover of `CoreScoreFast` vs.
  `CoreScoreSlow` (sign of the difference changes from the previous cadence — this needs one bit of
  new per-engine state, `previousDiffSign`). **Restart handling differs from `ScoreSustainTracker`'s
  own accepted reset — see A5's own correction**: this specific piece of state MUST be re-derived
  from the same `CoreScoreFast`/`CoreScoreSlow` history replay on restart, not left null, since an
  already-open crossover position depends on it to detect a crossover that may have already happened
  during the downtime. Exit: only the next opposite crossover, or SquareOff.

Both still reuse: `IStrikeSelector`/`StrikeSelector` (premium-band/spread/OI strike candidate
filtering — unchanged, strategy-agnostic), `PaperTradeSimulator.FillEntry/FillExit` (unchanged,
pure fill math), the kill-switch and data-gap checks (shared infrastructure, not strategy logic),
and the IV-rank gate if desired (optional — flag if you want it applied to these two strategies;
the backtest never gated on it, so the default here is **not** to add it, matching "change nothing
about the tested logic").

### A8 — Two new trading-engine instances, per-strategy risk scoping

Rather than generalizing `LiveTradingEngine` into an N-strategy loop (larger, riskier surface for a
first cut), add two new sibling classes — `CoreScoreHysteresisTradingEngine`,
`CoreScoreCrossoverTradingEngine` — each a thin adaptation of `LiveTradingEngine`'s own
`EvaluateCadenceAsync` shape: fetch open positions filtered to its own `StrategyId`, evaluate exits
via its own rules class (A7), then entry the same way, with its own independent
`tradesToday`/`closedTodayPnls`/`consecutiveLossesToday`/`dailyLossLimitBreached` queries (same
query shapes as today, `StrategyId`-filtered) — this is what makes risk limits genuinely per-strategy
as decided. `MaxConcurrentPositions` is fixed at 1 for each (not read from shared config — hardcoded
per the user's explicit "max concurrent trades should be 1 per strategy" instruction, not a
tunable). Capital check (committed-capital-vs-`Capital.Total`) stays a shared, unscoped sum across
BOTH strategies' open positions plus the legacy engine's (now permanently empty) positions — per
Assumption 1 (shared capital pool).

### A9 — Config shape

New `NiftySignal.Rules` records: `CoreScoreHysteresisConfig(EntryScoreThreshold, RiskLimits)`,
`CoreScoreCrossoverConfig(FastWindowMinutes, SlowWindowMinutes, RiskLimits)`, each with their own
`RiskLimitsConfig`-shaped nested record (independent `MaxDailyLossPct`/`MaxConsecutiveLosses`/
`MaxTradesPerDay`, per the per-strategy decision) — both continue reading the EXISTING shared
`Capital`/`Session`/`StrikeSelection`/`Costs`/`KillSwitch` sections unchanged (Assumption 1). New
`appsettings.json` sections (`"CoreScoreHysteresis"`, `"CoreScoreCrossover"`), bound the same
validated-hot-reload way `RulesetConfigOptions`/`ValidatedOptionsMonitor` already work for the
existing config, with their own `RulesetConfigValidator`-equivalent structural checks
(`EntryScoreThreshold > 0`, `SlowWindowMinutes > FastWindowMinutes > 0`, etc.).

### A10 — Wiring into the cadence loop

`MarketDataIngestionWorker.RunScoreCadenceLoopAsync` — after step 1 (`ComputeCadence`, now also
producing the new `CoreScoreSnapshot`) and step 3 (persist, now persisting both snapshot types):
the existing single `tradingEngine.EvaluateCadenceAsync(snapshot, ...)` call (step 4) is **removed**
(per the "retire from trading" decision) and **replaced with two new calls**:
`hysteresisEngine.EvaluateCadenceAsync(coreScoreSnapshot, ...)` and
`crossoverEngine.EvaluateCadenceAsync(coreScoreSnapshot, ...)`. (Net effect: one call becomes two,
not three — a wording slip in an earlier draft of this plan said "three calls," which was wrong.)
Both new engines registered as singletons in `Program.cs`, same lifetime/DI shape as the existing
`LiveTradingEngine` registration.

**Cutover safety: check for an open `LegacyComposite` position before removing the legacy call.**
If a `PaperTrade` row with `StrategyId = LegacyComposite` is still open (`ExitTime == null`) at the
moment this call is removed, nothing will ever evaluate it for exit again — no engine will query
it, so it would sit open indefinitely (MFE/MAE tracking stops, no SquareOff ever fires, the
dashboard would show a permanently stuck position). Before this deploy: confirm no `LegacyComposite`
position is open (check `db.PaperTrades.Any(p => p.StrategyId == StrategyId.LegacyComposite &&
p.ExitTime == null)`), and if one is open, either wait for it to close naturally (it will, via the
still-functioning legacy engine, before end of that trading day) or keep a minimal
legacy-exits-only code path active (still call `EvaluateExitAsync` for `LegacyComposite` positions,
just skip `EvaluateEntryAsync`) until it's flat, THEN remove the call entirely.

### A11 — Dashboard

- **Delete** `RatioScorePanel.razor`; remove its `<RatioScorePanel />` reference from `Home.razor`;
  remove `CurrentRatioScore`/`RatioScoreHistory`/`RatioScoreComponents`/`RatioComponentsPresent` and
  `BuildRatioComponentRows`/`BuildDefaultRatioComponentRows` from `LiveDataService.cs`; remove
  `RatioComponentRow` from `DashboardModels.cs` if nothing else references it.
- **Repurpose** `ScorePanel.razor` (per Assumption 2): its component loop renders the 8 Core-score
  metrics (reading the new `CoreScoreSnapshot` table via `LiveDataService`'s existing 5s-poll
  pattern — no SignalR/hub change needed, confirmed by the dashboard research: `ScoreSnapshot`
  reaches the dashboard exclusively via direct DB poll today, not a push, so a new table polls the
  same way). Add a small new section showing both strategies' current state (flat / long-call /
  long-put, entry price/time, current score) — reads `PaperTrades` filtered by the two new
  `StrategyId` values, mirroring however the dashboard already surfaces the legacy engine's open
  position today (needs a quick look at `LiveDataService`'s existing open-position query at
  implementation time — not yet traced in this research pass).
- `LiveFeatureEngine.ComputeCadence`'s ratio-composite computation block is removed entirely (per
  the "retired, not just hidden" decision) — this is a live-scoring-pipeline change, listed here
  too since it's the other half of "remove ratio scoring."

## Sequencing (batches, watch-one-before-the-next — this project's own established discipline)

1. **Batch 1 — pure/fast, no live wiring yet.** `SessionRankTracker` port to `NiftySignal.Features`
   (A1, minus restart-replay), `CoreScoreCalculator` (A4), `CoreScoreComponentInputs`/`CoreScoreWeights`,
   `CoreScoreHysteresisRules`/`CoreScoreCrossoverRules` (A7) as pure static classes. Unit tests
   mirroring `RatioScoreCalculatorTests.cs`'s own categories (manual-computation match, warm-up
   gate, antisymmetry, saturation bound) plus hand-computed hysteresis-band and crossover-flip
   boundary tests (exactly-at-threshold and one-below, matching this project's own established
   boundary-testing convention). `dotnet test` green before touching `LiveFeatureEngine` at all.
2. **Batch 2 — the 8 metrics in `LiveFeatureEngine` + `CoreScoreSnapshot` + migration** (A2, A3,
   A5). New tests in `LiveFeatureEngineTests.cs` per metric: null-before-data, hand-computed match
   against a synthetic scenario, band-boundary tests (proving Itm2Atm1/ATM±5/ATM±10/ATM±2 actually
   exclude what they should), a 0-DTE-day test for `ItmSkew`'s null-out, restart-replay test for
   the `SessionRankTracker`s. `dotnet test` green.
3. **Batch 3 — historical replay validation, MANDATORY before any live-trading-capable code is
   written (added after external review).** Shadow mode alone only proves "looks plausible live" —
   it can't prove the live Core score actually IS the backtested one, not "a cousin of it." Reuse
   `NiftySignal.ScoreReplay`'s own established pattern (already replays `BacktestTickSource` ticks
   through `LiveFeatureEngine.OnTick/Sample/ComputeCadence` on a tick-timestamp clock, not
   wall-clock) to drive the new Batch 2 code across the same 4 backtested days (08-11 Sep), and diff
   EVERY intermediate value — the 8 raw values, the 8 signed values, `CoreScore`, `CoreScoreFast`,
   `CoreScoreSlow`, and each strategy's own would-enter/would-exit decision — against
   `CoreScoreOptionSimulator`'s own output at matching timestamps. **First confirm the cadence
   actually lines up** (both are 15s-cadence-driven per their own designs, but verify this
   explicitly rather than assuming — a mismatch would silently invalidate every downstream diff).
   Do not proceed to Batch 5 (trading engines) until this diff is tight (exact match, allowing only
   genuine floating-point rounding) across all 4 days — a persistent mismatch means STOP and find
   which of the A3 formulas still doesn't match, not "close enough, ship it."
4. **Batch 4 — remove the ratio composite entirely** (the decision above) — delete
   `RatioScoreCalculator`/`RatioComponentInputs` usage from `ComputeCadence`, delete
   `RatioScorePanel` and its Dashboard-side code (A11 first half). This is independent of Batches
   1-3 and could ship before, after, or alongside them; sequenced here mainly because it's
   lowest-risk and good to get out of the way early. `dotnet test` green; `ScoreSnapshot.Ratio*`
   columns stay in the schema, just never written again.
5. **Batch 5 — `PaperTrade.StrategyId` + the two new trading engines + config + cadence-loop
   wiring** (A6, A8, A9, A10) — the actual live-trading-capable code, only started once Batch 3's
   replay diff is tight. New tests mirroring `LiveTradingEngineTests.cs`'s own shape: per-strategy
   risk-gate isolation (a trading-safety parity test proving strategy A hitting its daily loss cap
   does NOT block strategy B, and vice versa — the direct test of the "per-strategy independent"
   decision, not left as a code-reading claim), `MaxConcurrentPositions=1`-per-strategy enforcement,
   and a repeat of the existing `EvaluateCadenceAsync_IgnoresRatioCompositeScore_...`-style parity
   test confirming the two new engines read ONLY `CoreScoreSnapshot` fields and never
   `ScoreSnapshot.CompositeScore`.
6. **Batch 6 — shadow-mode dry run, mandatory before real order-writes are enabled** (Assumption
   4). A config flag (`ShadowMode: true` on each new engine's config, or one shared flag) that runs
   the full entry/exit decision logic and logs what each strategy *would* do (`logger.LogInformation`,
   matching the existing "Entry NOT taken despite qualifying score" pattern) but skips the
   `db.PaperTrades.Add`/`SaveChangesAsync` step entirely. Deploy Batches 1-5 with `ShadowMode: true`
   for at least one full live session (09:15-15:30 IST), read the logs, confirm both engines are
   scoring sensibly and would have made sane decisions. **Deliberately restart the Host process
   once during this session** and confirm the crossover engine's log shows sane
   `CoreScoreFast`/`CoreScoreSlow` values immediately after restart (proves A5's restart-replay fix
   actually works, not just that it compiles), THEN flip `ShadowMode: false` for the next session.
7. **Batch 7 — Dashboard repurposing** (A11 second half — the `ScorePanel.razor` rebuild). Lowest
   risk (display-only, zero trading impact), sequenced last mainly because it depends on Batch 2's
   `CoreScoreSnapshot` table existing with real data to display, and Batch 5's `StrategyId` existing
   for the open-position-by-strategy section.

## Small things flagged so they don't get missed (the user's own stated worry)

- **Self-inclusion-safe rank ordering**: every `SessionRankTracker.Rank(...)` call must happen
  BEFORE the corresponding `.Add(...)`, exactly as `RankSigned` already does in the backtest — this
  is the single most-repeated correctness bug class this whole session has found and fixed
  elsewhere (DynamicHybrid, Vanna/Charm, PCR). Get it right the first time here.
- **Strike-identity safety**: `PaperTrade` already tracks one `InstrumentToken` per position for
  its whole lifecycle (confirmed — `EvaluateExitAsync` always re-quotes `position.InstrumentToken`,
  never "whatever's ATM now"), so this specific bug class is already structurally prevented for
  both new engines without extra work — just don't introduce a fresh ATM re-lookup anywhere in the
  new rules classes.
- **0-DTE exclusion for `ItmSkew`** must use the SAME expiry-day definition `LiveTradingEngine.cs:121`
  already computes (`featureEngine.NearestExpiry == today`), not a re-derived one — a second,
  slightly different expiry-day check would be a subtle, hard-to-notice divergence from the
  backtest's own definition.
- **IST vs. UTC**: every new per-cadence timestamp comparison (SquareOffTime, expiry-day check,
  today-midnight for risk queries) must use the existing `.ToIst()`/`IstOffset` conventions already
  fixed once for `EntryRuleEvaluator`/`ExitRuleEvaluator` (both evaluators' own doc comments
  explicitly warn this exact bug already bit them once) — do not re-derive a naive `DateTime.Date`
  comparison anywhere in the new code.
- **Restart replay is REQUIRED for `CoreScoreFast`/`CoreScoreSlow`** (A5, corrected after review) —
  unlike the ratio composite's own fast FIFO, this one must be seeded from persisted `CoreScore`
  history on restart, since the crossover strategy is always-positioned and an empty window blocks
  its only non-SquareOff exit path for up to 30 minutes. The two engines' own `previousDiffSign`/
  sustain-adjacent in-memory state (not persisted anywhere) stays restart-reset, same as
  `ScoreSustainTracker` today — that gap is fine since it just means "wait for the next real
  crossover signal to re-establish direction," not "can't exit an open position."
- **`StrategyId` backfill on the migration** — every existing historical `PaperTrade` row must
  become `StrategyId = LegacyComposite` in the same migration that adds the column, not left null
  (the column is required, matching every other required field on this entity).
- **Telegram/notification messages** should include which strategy fired (`CoreScoreHysteresis`/
  `CoreScoreCrossover`) — today's single-strategy messages have no such label; a bare "NiftySignal
  ENTRY: ..." message would be ambiguous with two strategies live simultaneously.
- **`MaxConcurrentPositions=1` is hardcoded per engine, not read from `RulesetConfigOptions`'s
  shared `Capital` section** — reusing the existing shared `Capital.MaxConcurrentPositions` (today
  set to 3, for the now-retired legacy engine) for either new engine would be wrong; each new
  engine's "1" is a fixed architectural fact of this plan, not a config value someone could
  accidentally bump.
- **Do not let the legacy engine's removal orphan its own risk state** — `LiveTradingEngine.cs`
  stays fully intact and functional (just uncalled), so if it's ever re-enabled later, its own
  (still-unscoped, `LegacyComposite`-filtered-after-A6) risk queries continue to work correctly
  against historical data without any further change.

## Verification

`dotnet build` + `dotnet test` clean after every batch (per this project's own standing rule, plus
the Stop hook that already enforces it). Batch-specific checks as listed inline above.

**The binding check is Batch 3's historical replay diff, not shadow mode** — shadow mode (Batch 6)
can only show "this looks sane," it cannot show "this matches the backtest." Do not treat Batch 6
as sufficient proof of replication on its own; Batch 3's diff against `CoreScoreOptionSimulator`'s
own output across all 4 known days is what actually proves the live pipeline computes the SAME
score, not a formula that merely resembles it (the exact failure mode this review's core objection
was about).

Final, whole-pipeline check before calling this done (Batch 6, shadow mode): run one real trading
session with `ShadowMode: true` and confirm in the logs that (a) all 8 Core-score metrics are
populating with plausible signs against what the option chain/future visibly show at that moment
(the same "validate against real numbers" discipline this whole project has applied to every prior
live formula — a second, live-data sanity check, on top of Batch 3's exact-match backtest diff,
not a replacement for it), (b) `CoreScoreFast`/`CoreScoreSlow` both warm up within their own windows
(10/30 min) and diverge sensibly rather than tracking identically, (c) both engines' shadow-logged
decisions look sane end-to-end (right side for the score's sign, plausible strike selection, exits
firing on the correct hysteresis/crossover condition), (d) a deliberate mid-session Host restart
shows the crossover engine's fast/slow values recovering immediately from the A5 replay fix, and
`previousDiffSign` recovering with it (not resetting to null), not a fresh 30-minute blind spot —
before flipping `ShadowMode: false`.

**What Batch 3's diff does and doesn't prove (flagged on second review, worth being explicit
about):** it proves the SCORE computation replicates exactly. It does NOT prove live P&L will match
the backtest's +200.73/+124.13 — real `StrikeSelector` candidate availability, live bid/ask spreads,
and actual fill prices can all differ from the backtest's own simplified strike-in-[100,150]-range
pricing, and that's an accepted, separate source of divergence, not a bug to chase. Judge next
week's live run on whether the two engines make the SAME DECISIONS the score would predict — enter/
exit at the right times, right side, right strike ballpark — not on whether the P&L number lands at
+200.73 or +124.13.
