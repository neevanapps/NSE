# Score Candidates

Registry of every metric under consideration for the eventual multi-metric composite score
(see [`CLAUDE.md`](../CLAUDE.md), "The endgame is a multi-metric composite score" and
[`REVIEW_FINDINGS.md`](REVIEW_FINDINGS.md) for the full evidence trail behind each entry). This
file is the standing summary; `REVIEW_FINDINGS.md` stays the detailed, dated lab notebook. When a
candidate's status changes, update its entry here and log the reasoning there, same split of
responsibility the project already uses between `CLAUDE.md` and `REVIEW_FINDINGS.md`.

**How to read this file**: each candidate is diagnosed independently (per the metric-by-metric
evaluation cycle) before it has any real weight. Nothing here is a trading rule. Gating,
regime-filters, and risk controls are explicitly **not** decided at this stage — see "Normalization
and gating" below and `CLAUDE.md`'s "No gating during single-metric evaluation" rule.

## Live-vs-lab metric inventory (2026-09-13)

Cross-referenced every metric actually computed in the live scoring code (`ScoreComponentInputs`/
`ScoreWeights`, `RatioComponentInputs`/`RatioScoreWeights`, and every `Compute*` method in
`LiveFeatureEngine.cs`) against everything tested below, since several live and lab metrics share
names or concepts without being the same formula. Full source-level detail in
`REVIEW_FINDINGS.md`'s 2026-09-13 "Live-vs-lab metric inventory" section.

**Tested in the lab, with current status:**
- Depth imbalance (option) — **CONFIRMED**. Level-based (resting book state, not a change).
- IV skew (`PutAvgIv − CallAvgIv`, Itm2Atm1) — **CONFIRMED among non-expiry ThisWeek days**.
  Level-based; its own cadence-to-cadence delta was tested too and was weaker/inconsistent.
- PCR (volume) — **watch candidate** (downgraded from a false CONFIRMED). Already flow-based
  (volume is a change quantity by construction).
- OI-diff (`CallOiChangeSum − PutOiChangeSum`) — **watch candidate, sign unresolved**. Already
  change-based.
- PCR-OI (`CallOiSum/PutOiSum`) — **watch candidate**. Level-based; **its own bar-to-bar change has
  never been correlated against price directly** (only checked against OI-diff for redundancy) —
  open item.
- Demand/supply exhaustion (F58) — **reopened watch candidate**. Change-based (15-min reaction
  residual vs. the next window).
- Volume/OI turnover ratio — **CLOSED, no edge**. Mixed (volume=flow, OI=level).
- Option CVD (touch-rule) — **CLOSED, no edge** after 5 formulations. Flow-based.
- Future CVD (Net5Min) — earlier finding, promoted with caveats. Flow-based; an early
  cumulative-day-level formulation was tried and was *not* the one that held up.
- Future depth imbalance — concluded weak.
- VIX change — concluded, contemporaneous only, forward ≈ 0, **not promoted**. Already
  change-based, still failed — change alone doesn't guarantee an edge.
- Trend reversion / spot-future basis — both raw level and first-differenced change tested side by
  side; basis change provisionally promoted (watch), weak/stale.

**Live-weighted, never tested in the lab (by weight, highest priority):**
- `OiBuildupNet` (**0.3125**, the single largest live weight) — sign-weighted `Σ(sign·|ΔOI|)`,
  ATM±2, both option types. Already change-based (ΔOI) by construction — untested regardless.
- `Pcr` as actually implemented (**0.19**) — raw `putOI/callOI`, ATM±2, a **level**. Not the same
  metric as lab's PCR (which is volume-based) — closer in concept to PCR-OI, whose own lesson
  (raw level needs normalization) likely applies directly.
- `IvSkew` as actually implemented (**0.1425**) — difference at a dynamic OTM expected-move strike
  pair, a **level**. The just-confirmed ITM-band (Itm2Atm1) result does not transfer automatically
  — different strikes entirely.
- `FuturesBasis` (**0.1425**) — raw level; only the change version has real lab evidence.

**Live-weighted at 0.0 (diagnostic only), never tested:**
- `GammaExposure`, `VannaExposure`, `CharmExposure` — full-chain, structurally identical to each
  other (same loop, different Greek), level-type.
- `SpreadRatio` — full-chain, unweighted mean, level.
- `StraddleRichness` — combined-straddle 1st-order residual, related to but distinct from F58.
- `VolumePcr` — full-chain notional ratio, related to but distinct from lab's PCR.

**Ratio-composite metrics (equal-weighted 0.2 each, none evidence-derived, none tested):**
- `NotionalVolumeRatio` (ATM±5) — third variant of the PCR-volume idea.
- `SizedOiFlowRatio` (ATM±5) — fourth variant of the OI-comparison idea.
- `ResidualDifference` — related to F58 (simpler formula), not literally tested itself.
- `IvSkew25Delta` — third variant of IV skew (true 25-delta strike, ratio not difference).
- `SpreadRatioAtm` (ATM±2, OI-weighted) — second variant of spread ratio.

`FuturesVwapDeviationZ` sits outside both composites, validated via DynamicHybrid's backtest P&L
(a different, outcome-based route), not lab correlation.

**Structural duplication flagged, not yet consolidated**: four economic ideas each have 3-4
independently-coded variants scattered across the two live composites and the lab (IV skew,
call/put volume balance, OI-based call/put comparison, price-vs-Greeks residual) — see
`REVIEW_FINDINGS.md` for the full variant-by-variant breakdown. Related to, and sharper than, the
already-open F9 pending item (components assumed independent, likely aren't).

**Scoring convention going forward**: no rolling z-scores. Each metric's raw value (level or
change, whichever the data supports) gets clipped to `[-1,1]` via a log-ratio or scaled-difference
transform, combined in a weighted sum, then squashed via `100·tanh(raw/k)` — the pattern the
ratio-composite already established, not the original 14-component composite's z-score machinery.

**Level vs. change is decided per metric, not by a blanket rule** (2026-09-13, user's own
correction after an initial overcorrection): a flow-type quantity (volume, OI change, CVD) is
naturally a change and should be tested that way; a state-type quantity (resting depth imbalance,
IV skew) is naturally a level and testing its own change is an *additional* check, not a
replacement — IV skew's own delta was tested and lost to its level. Test both where a metric
plausibly has both; let the correlation decide, don't assume either wins going in.

**NextWeek expiry is dropped as a standing testing scope going forward (2026-09-13).** Every
metric tested against both weeks this session — OI-diff, PCR, PCR-OI, depth imbalance, IV skew,
OiBuildupNet — showed NextWeek as weaker, messier, or outright incoherent versus ThisWeek, with
zero exceptions and zero cases where NextWeek carried a signal ThisWeek didn't. Not a single
finding this session has come from NextWeek data. Future candidates test ThisWeek only unless
there's a specific reason to check NextWeek again.

**Multiple time horizons per metric, not a fixed 5/15-min default (2026-09-13).** Different
metrics plausibly resolve on different timeframes — a metric built to average out noise (like
depth imbalance's resting-state read) may behave differently at 10 or 30 minutes than at 5. Test
horizons that make sense for the specific metric's own nature, not just the two that happened to
be convenient for the first few candidates.

## Backtest-vs-live parity (CoreScoreReplayDiff) — 2026-09-17 findings

Distinct from the metric-quality findings below — this section is about whether `LiveFeatureEngine`'s
real-time CoreScore computation actually matches `CoreScoreOptionSimulator`'s backtest math, not
about whether either formula has a real edge. Investigated after the user asked why a live-observed
"score reads bullish while Nifty falls" pattern didn't seem fully explained by the metric-level
findings alone. Ran `NiftySignal.CoreScoreReplayDiff` (full tick-by-tick replay vs backtest, all
19 published fields, 3-5 days) to check directly rather than infer from code reading alone.

**Found and FIXED**:
- **`BasisChangeSigned` — was wrong on 54% of all matched cadences (4016/7385), now 0/7385.** Root
  cause: `LiveFeatureEngine.ComputeCoreTrendAndBasis` combined `futureChange`/`spotChange` in DOUBLE
  arithmetic after each was independently cast from decimal, instead of subtracting in DECIMAL
  arithmetic first (matching `CoreScoreOptionSimulator`'s own `(double)(fChg - sChg)` order) and
  casting once. The resulting noise was too small to trip `BasisChangeRaw`'s own comparison
  (0 raw mismatches reported, always under 1e-6) — but `SessionRankTracker.Rank()` compares every
  new value against the *entire* accumulated session history via strict `<=`, and `BasisChange`'s
  distribution (derived from tick-granular decimal prices) is dense with near-exact ties, so a
  few-ULP difference in even one historical entry cascaded into disagreeing on the majority of the
  day's rank lookups. Fixed by recombining from the original decimal opens/closes, one cast at the
  end, matching the backtest exactly.
- **`OiChangeDiff15m` band selection** — `ComputeCoreOiChangeDiffRaw` was still selecting "nearest
  N strikes by raw price distance from spot," the same pattern already found and fixed for
  `GammaExposureRaw` (an earlier fix elsewhere on this file, referenced in that method's own doc
  comment) but never migrated here. Switched to the shared index-based `strikeOffsets` (from
  `ComputeStrikeOffsets`) every other Core-score band method already uses. Empirically inert on the
  5 days checked (0 value mismatches even before the fix, only 5 null-mismatches out of 7500) — a
  latent correctness issue, not one currently biting, but worth having fixed before it does.
- **`CoreScoreReplayDiff` itself was comparing against stale weights.** It called
  `CoreScoreOptionSimulator.BuildDiagnostics` without specifying weights, silently falling back to
  the backtest tool's own defaults (the original, pre-2026-09-16 values) — stale ever since the live
  composite's weights were revised (`ItmSkew`/`GammaExposure` zeroed, rest rescaled to sum to 1.0).
  That alone made `CoreScore`/`CoreScoreFast`/`CoreScoreSlow` disagree on 100% of cadences,
  masquerading as a parity bug on top of the real one. Fixed by mapping
  `NiftySignal.Scoring.CoreScoreWeights.Default` across before building the backtest side. With both
  real fixes in place and honest matching weights, `CoreScore` mismatches dropped to 37 of 7500
  cadences (0.5%).

**Investigated, NOT fixed — revisit once more days of data exist (per user's own instruction,
2026-09-17)**:
- **`ItmSkewSigned`**: 328 of 4500 mismatches, despite `ItmSkewRaw` itself differing on only 2
  cadences (max 0.000374) — same cascade-via-rank-tracker amplification as `BasisChange` had, but
  the raw noise here (0.000374) is ~374x larger than the IV solver's own 1e-6 convergence tolerance,
  meaning it's likely NOT pure solver noise but a small, real input difference (mid-price or
  underlying timing) at those 2 specific cadences — same family of issue as `GammaExposureRaw`
  below, just far rarer (`ItmSkew`'s Itm2Atm1 band is 5 strikes vs Gamma's 21).
- **`GammaExposureRaw`**: 26 of 7500 mismatches, max diff 2471.27 (a real, large divergence, not
  floating-point noise) — e.g. 09-08 04:26:45 UTC (09:56:45 IST): backtest=62227.38, live=59756.11.
  This method has already had two prior fix attempts (a "Gamma-freeze fix" and an "OI-holdover fix,"
  both referenced in its own doc comment) that didn't close this specific gap — consistent with the
  remaining cause being live's real-time instrument resolution/subscription momentarily disagreeing
  with backtest's historical tracked-chain set at the wide ±10 band's edge, not a code-logic bug
  fixable by editing the scoring formula itself.
- Deliberately did NOT add an epsilon-tolerant comparison to `SessionRankTracker` (shared, 3x-
  duplicated, used by every ranked term in both pipelines) as a blanket fix for either of the above
  — no data-driven justification yet for a specific tolerance value, and doing so risks quietly
  masking a real future issue the same way the original bug hid for a while. Bounded next step, not
  yet done: dump the exact 2 `ItmSkewRaw` mismatch instances and trace which specific input
  (mid-price/underlying/T) diverged, to determine whether it's a timing artifact or something else.

**Deployment status (2026-09-17): NONE of the above is live yet.** The user has not run `deploy.ps1`
since these fixes (or since the `ItmSkew`/`GammaExposure` weight revision, or the `ShadowMode=false`
change) landed. Production is still running the ORIGINAL composite weights (`ItmSkewWeight=0.065`,
`GammaExposureWeight=0.06`, no rolling-change terms) AND the buggy pre-fix `BasisChangeSigned`/
`OiChangeDiff15m` implementations. Any live-observed "score behaves strangely" pattern right now is
seeing the un-fixed, un-revised system, layered on top of whatever genuine metric-level miscalibration
already exists in the formula itself (see 09-15's own correlation findings elsewhere in this file) --
the two are compounding, not the same problem.

## Known confounds in the current sample (update as more days are added)

Every metric evaluated so far uses the same 4 real trading days (08–11 Sep 2026). These days are
**not 4 independent draws of "a normal day"** — each sits at a different, unrepeated point in the
weekly-expiry cycle, so day-of-week, DTE, and market regime are currently confounded with each
other and cannot be separated. Any per-day pattern found in a candidate below should be read
against this table before being trusted as "regime-dependence" rather than "one of these other
things."

| Date | Day of week | Nearest weekly expiry | DTE | India VIX (day open→close) | Notes |
|---|---|---|---|---|---|
| 2026-09-08 | Tue | 08 Sep (same day) | **0 — expiry day itself** | 10.94 → 11.16 (+2.01%) | Pinning/gamma-unwind regime, not a normal trading day |
| 2026-09-09 | Wed | 15 Sep | 6 | 11.50 → 11.98 (+4.17%) | |
| 2026-09-10 | Thu | 15 Sep | 5 | 11.86 → 11.74 (−1.01%) | Only day VIX fell intraday; recurring anomaly across multiple metrics (see below), cause still unresolved |
| 2026-09-11 | Fri | 15 Sep | 4 | 12.13 → 12.24 (+0.91%) | |

## Normalization and gating — general approach, not yet finalized per-candidate

- Target range for every candidate once scored: **-100 (strong bearish) to +100 (strong
  bullish)**, 0 = neutral/unknown. Matches the existing production composite and ratio-composite
  conventions.
- Preferred squashing method, per this project's own F1 finding (`CLAUDE.md`): a **fixed**
  constant, never a value recalibrated against a short recent window (dynamic recalibration erases
  genuine extremity — that was the original composite's own bug). The constant itself must be
  *derived from real data* before being fixed, not guessed — same `PENDING (F40)`-style discipline
  already used for `RatioMetricScales`. Two acceptable shapes, pick per-candidate based on what the
  raw value naturally looks like:
  - **Session-rank based**: percentile-rank the raw value against its own session-so-far
    distribution (`SessionRankTracker`, already built and tested), then map rank 0–100 to score
    -100–+100 by sign × rank. Self-scaling, no fixed constant needed at all — usually the safer
    default while a metric's real distribution is still unknown.
  - **Bounded-transform based**: clip/log-transform the raw value into a bounded range (e.g.
    `[-1,1]`), then `100 * tanh(bounded / k)` with `k` fixed from real observed distributions
    (mirrors `RatioScoreCalculator`). Prefer this only once a candidate's natural raw range is
    reasonably well understood.
- **Gating is not decided here.** Whether/how a candidate's score gets suppressed or modulated
  under some regime is a composite-score-stage or production-rule-stage decision — see
  `CLAUDE.md`'s "No gating during single-metric evaluation" rule. A regime-detector candidate
  (trend efficiency, VIX, etc.) is itself just another row in this table with its own sign/edge
  verdict, not a filter bolted onto a different row.
- **Sign is a hypothesis until tested** — same rule as the live composite's PCR/IV-skew columns.
  Every "Sign" cell below is either "confirmed" (backed by real correlation on this project's own
  data) or "hypothesis" (plausible but not yet checked) — never assumed from intuition alone.

## Candidates

### `FutureDepthImbalanceMean5Min` / `Mean15Min`

- **What it measures**: resting order-book quantity imbalance (bid qty − ask qty, or similar) at
  the top of the future's own book, averaged over a trailing 5/15-minute window.
- **Source**: `NiftySignal.BacktestData/CadencePopulator.cs`; persisted on `CadenceContext`.
- **Sign**: hypothesis was bid-heavy → bullish; **observed correlation is mostly negative and
  inconsistent** — not confirmed, and likely the wrong sign for a naive reading.
- **Evidence**: pooled backward corr −0.03 to −0.14 across windows; forward corr not consistently
  signed (10 Sep flips positive while the other 3 days are negative). See `REVIEW_FINDINGS.md`,
  "Future depth imbalance vs. future price" (2026-09-12).
- **Status**: **Concluded — insufficient/inconsistent evidence, no weight assigned yet.** Likely
  missing its complement (a trade-aggressor metric, i.e. `FutureCvdProxy` below) rather than simply
  wrong — depth imbalance (who's waiting) and CVD (who's trading) answer different questions.
- **Days validated**: 08–11 Sep 2026 (see confounds table above).
- **Overlaps with**: conceptually related to `FutureCvdProxy` (both option-book/flow-derived), but
  measures a structurally different thing (resting vs. traded liquidity) — not redundant by
  construction, though real correlation between the two hasn't been checked yet.
- **Open questions**: worth re-checking once more days exist; not worth further formulation work
  on its own until then.

### `FutureCvdProxyThisCadence` / `CumulativeDay` / `Net5Min` / `Net15Min`

- **What it measures**: aggressor-classified (quote-rule: buy-leaning if `LastPrice >=
  midpoint`) volume delta on the tracked future's own ticks — a CVD-style proxy, not true
  per-trade CVD (FlatTrade's feed has no per-trade tape; a single reported volume delta can bundle
  many real trades). `ThisCadence`/`CumulativeDay` are raw/running-total; `Net5Min`/`Net15Min` are
  rolling sums over a trailing real-time window (fixes the running-total's stale-memory problem).
- **Source**: `NiftySignal.BacktestData/CadencePopulator.cs` (`FutureCvdProxyAccumulator`,
  `RollingNetSumWindow`).
- **Sign**: **confirmed** positive (buy-leaning volume associates with price having risen and,
  weakly, continuing to rise) — the intuitive sign depth imbalance failed to show.
- **Evidence**: pooled backward corr up to +0.41 (15-min net), pooled forward corr up to +0.14
  (5-min net, forward 5-minute) — the best forward-looking number found on this dataset so far.
  Positive on 3 of 4 days; 10 Sep is the recurring exception (see confounds table). Six trade-
  simulation designs tried (threshold-based and always-positioned, gated and ungated) — see
  `REVIEW_FINDINGS.md`'s "six variants" section (2026-09-12).
- **Status**: **PROMOTED — real, forward-validated edge; scored candidate for the composite.**
  Not standalone-tradeable (every trading-rule variant tried either capped time-in-market too low
  or hit a new failure mode as bad as the one it fixed — see the "six variants" section in
  `REVIEW_FINDINGS.md`), but that was never the bar for this file; the bar is edge, which is
  confirmed. This is a checkpoint on 4 confounded days, not a final verdict.
- **Days validated**: 08–11 Sep 2026.
- **Overlaps with**: none confirmed yet; the natural comparison is `FutureDepthImbalance` (both
  future-order-flow-derived) — not yet cross-correlated against each other.
- **Score formula (2026-09-12)**: session-rank based, not a fixed-constant transform — the raw
  value's magnitude scales with the day's own traded volume, which isn't stable day to day.
  `score = (percentile_rank(FutureCvdProxyNet5Min, session-so-far distribution) - 50) * 2`. Rank
  the raw *signed* value (not its magnitude) against a `SessionRankTracker` fed from that day's own
  cadences so far — a low percentile (very negative print) scores near -100, a high percentile
  near +100, typical near 0. `Net5Min` chosen over `Net15Min`/`ThisCadence`/`CumulativeDay` for the
  scored series specifically because it has the best forward correlation (predictive validity
  matters more than backward fit for a score meant to inform what happens next). Needs a cold-start
  floor (a handful of minutes of same-day history) before the rank is trustworthy — same reasoning
  as the IV-rank gate's 5-session floor elsewhere in this project; before that, score should read
  null/neutral, not a falsely confident extreme.
- **Open questions**: what specifically makes 10 Sep different (checked and ruled out as the sole
  explanation: DTE, VIX — both real findings, neither conclusive); needs more real days, ideally
  spanning another expiry day and another mid-cycle day, before either the edge-sign or the
  regime-dependence read is treated as settled.

- **2026-09-17 red flag, as `FutureCvdNet5Min` inside the separate CoreScore 8-metric composite
  (`ctx?.FutureCvdProxyNet5Min`, read directly, "no inversion" per that class's own doc comment)**:
  user asked whether all 6 currently-live-weighted terms (besides the two already revised) are
  correctly behaving, prompting the same per-term correlation pass already used for `ItmSkew`/
  `GammaExposure`, checked against BOTH targets and BOTH the 5-min and 15-min horizon:

  | Day | vs future, 5m | vs future, 15m | vs option, 5m | vs option, 15m |
  |---|---|---|---|---|
  | 09-08 | 46.0% | 43.8% | 43.9% | 45.5% |
  | 09-09 | 58.2% | 54.6% | 57.0% | 53.8% |
  | 09-10 | 50.3% | 42.9% | 47.7% | 43.0% |
  | 09-11 | 49.1% | 42.0% | 50.8% | 44.0% |
  | 09-15 | 44.7% | 42.8% | 44.5% | 43.4% |

  Below 50% (worse than a coin flip) on **4 of 5 days, both targets, both horizons** — only 09-09
  works, and it works cleanly at every target/horizon combination. Inverting the sign would flip
  every cell's hit rate to its complement, turning 4 of 5 days positive at the cost of breaking the
  one day that currently works. **But the day pattern itself doesn't match this file's own original
  finding above** ("positive on 3 of 4 days; 10 Sep is the recurring exception" — i.e. 08/09/11 Sep
  good, 10 Sep the outlier). Here it's nearly the opposite: 09 Sep is the one good day, 08/10/11 (and
  15) are the weak ones. That mismatch is the real finding — it suggests something differs between
  the original SQL-based validation and this composite's own pipeline (different aggregation,
  different day alignment, or a genuine discrepancy in what `FutureCvdProxyNet5Min` holds by the
  time `LiveFeatureEngine`/`CoreScoreOptionSimulator` reads it), not a simple "the sign was always
  backwards."
- **Root-cause investigation (2026-09-17), two candidate explanations checked and both ruled out**:
  1. **The percentile-rank transform (`RankSigned`/`SessionRankTracker`) changing which days look
     good** — ruled out. Correlated the RAW, untransformed `CadenceContext.FutureCvdProxyNet5Min`
     value directly (bypassing `BuildScoreCadences`/`RankSigned` entirely): same day pattern, same
     magnitudes (e.g. 09-08 raw: -0.059 corr/43.9% hit at 15m vs. the signed version's -0.087/43.8%
     — indistinguishable). The transform isn't manufacturing this.
  2. **Row-count-based forward window (the original `scripts/validate-and-correlate-cvd-rolling.sql`
     uses `LEAD(20 rows)` on `FutureCloseFromLastCadence`, assuming no gaps in the cadence sequence)
     vs. this session's time-based lookup on cumulative `FutureChangeForDay`** — ruled out.
     Reproduced the SQL script's exact method (fixed `LEAD(20)`/`LEAD(60)` row-count offset) directly
     in the backtest tool and got numbers matching the time-based version almost exactly (09-08:
     0.099/46.0% row-count vs. 0.104/45.9% time-based). A direct gap check confirms why: **0 of 1480
     windows per day deviate from 5 real minutes by more than 15 seconds** — this dataset has zero
     cadence gaps, so the two methods are mathematically equivalent here.
  - **Conclusion: neither checkable methodology difference explains it — the most likely remaining
    explanation is that the underlying data itself changed since the original 2026-09-12
    investigation.** This project has an established, documented pattern of exactly this: several
    other candidates above (`Pcr`, `OiChangeDiff15m`) were originally analyzed with a buggy
    script, found wrong, and re-run under a `*-corrected.sql` script with a different verdict.
    `CadencePopulator`'s own doc comment confirms days get deliberately deleted and repopulated
    "after a real calculation mistake has been found and fixed." Something upstream of
    `FutureCvdProxyNet5Min` or `FutureCloseFromLastCadence` plausibly got fixed and the affected
    days repopulated at some point after 09-12, and the day-by-day characterization written down
    then is now stale relative to the current data. This wasn't independently re-provable here
    (the pre-fix data no longer exists to diff against), but it's the only explanation consistent
    with everything checked.
  - **Practical implication**: whatever caused it, the CURRENT data — which is what any live
    decision actually runs against — shows this term below a coin flip on 4 of 5 days, at every
    target and horizon checked. The original "positive on 3 of 4 days" line above should be read as
    superseded/stale, not as a live contradiction to resolve. **Status: real finding, root cause
    understood (stale documentation vs. re-validated current data), sign decision (keep/invert)
    deliberately not made here** — that's a separate decision needing the same "test the actual
    consequence, don't just flip on hit-rate arithmetic" discipline used everywhere else in this
    file, not something to rush off the back of a root-cause investigation alone.
  - **General lesson for this file**: a candidate's verdict is only as fresh as the data it was
    checked against. Given how often this project's own data pipeline gets corrected, any
    "confirmed" or "closed" verdict here could in principle go stale the same way — worth
    periodically spot-re-checking older verdicts against current data, not just trusting the
    written status indefinitely.
- **Backtested three options directly (2026-09-17), CoreScore composite, ThisWeek, crossover 15/30,
  levels of ItmSkew/GammaExposure already zeroed as the baseline** — keep as-is, invert the sign, or
  zero the weight entirely (same treatment `ItmSkew`/`GammaExposure` already got):

  | Strategy (5-day total) | Keep as-is | Invert | Drop entirely |
  |---|---|---|---|
  | Hysteresis | **+119.60** | +37.15 | +41.45 |
  | Crossover 15/30 | +254.50 | **+396.33** | +290.28 |
  | Combined (either) | +163.78 | +200.30 | **+243.33** |
  | Combined (both) | +231.08 | +190.95 | **+319.83** |

  **No universal winner** — each of the three options wins at least one strategy outright, keep-as-is
  wins Hysteresis specifically. Inverting rescues 09-15 dramatically (the day that started this whole
  investigation: e.g. Crossover -52.08 -> +197.65) but badly damages 09-11, one of the genuinely good
  days in the original dataset (Crossover +211.85 -> +60.50; Combined-both +211.93 -> +28.30) —
  essentially betting the *other* days are the "true" sign and 09-11 is now the exception, a
  different arbitrary bet, not a resolved answer. Dropping degrades 09-11 far more mildly and never
  bets on a direction at all.
- **Checked whether a longer window rescues it, the same way it rescued `ItmSkew` (which was flat
  noise at a tick-to-tick delta but real and consistent at a 15-min rolling change) — it does not.**
  `CadencePopulator` already computes `FutureCvdProxyNet15Min` alongside `Net5Min` (and the original
  `validate-and-correlate-cvd-rolling.sql` tested both), but the composite only ever reads `Net5Min`.
  Checked the 15-min version raw, both forward horizons, all 5 days:

  | Day | vs 5-min forward | vs 15-min forward |
  |---|---|---|
  | 09-08 | 46.1% (-0.044) | 44.3% (-0.111) |
  | 09-09 | 55.8% (+0.176) | 52.9% (-0.002) |
  | 09-10 | 50.3% (-0.050) | 51.7% (-0.044) |
  | 09-11 | 48.8% (+0.172) | 44.3% (+0.070) |
  | 09-15 | 49.9% (+0.055) | 50.8% (+0.170) |

  Every hit rate sits within a few points of a coin flip, correlation sign flips across days with no
  pattern — **flatter and noisier than the already-inconsistent 5-min version**, the opposite of what
  happened for `ItmSkew`. A genuine 10-min version was not built (no precomputed column exists; would
  require re-deriving the CVD accumulator from raw ticks rather than reading an existing one) —
  flagged as untried rather than ruled out, but given both bracketing windows (5m, 15m) already show
  the same "no stable direction" character, a 10-min version sitting between them is unlikely to
  differ qualitatively.
- **Status: real, multi-angle-confirmed finding (wrong-more-than-right on the current data, at every
  target/horizon/window tried), but deliberately NOT resolved into a keep/invert/drop decision here**
  — none of the three backtested options is a clean win, and picking whichever number looks best
  across three options on 5 days is exactly the curve-fitting trap this file's own discipline exists
  to avoid. Needs more days before any of the three should be adopted.
- **The other 5 live-weighted CoreScore terms checked the same way (`DepthImbalance`,
  `NotionalVolumeRatio`, `TrendReversion15m`, `BasisChange`, `OiChangeDiff15m`) all look genuinely
  healthy** — correct sign, hit rate meaningfully above 50% at the 15-min horizon on 4 of 5 days,
  with 09-15 (today, the day that started this whole investigation) as the one common exception
  across literally every term checked, consistent with 09-15 being a broadly anomalous session
  rather than any one term being individually mis-specified. `OiChangeDiff15m` in particular looks
  better here (57-59% hit rate, 4 of 5 days) than its own "sign unresolved, day-dependent" label
  above suggests — worth a dedicated re-look given how much data now exists.

### VIX change vs. future price change (leverage effect) — *concluded, contemporaneous only, not promoted*

- **What it measures**: per-cadence India VIX change vs. the tracked future's own per-cadence price
  change — the classic "leverage effect" (rising fear, falling price).
- **Source**: `CadenceContext.VixChangeFromLastCadence` (already captured for the live composite,
  reused here).
- **Sign**: **confirmed** negative contemporaneously (VIX up ⇒ price falling at the same moment).
- **Evidence**: same-cadence corr(VIX change, price change) is −0.25/−0.24/−0.18 on 09/10/11 Sep,
  but **+0.06 on 08 Sep, the expiry day** (pinning/gamma-unwind flows plausibly override the normal
  relationship). **Forward correlation tested 2026-09-12 across three formulations — raw per-cadence
  change, rolling 5-min sum, rolling 15-min sum (mirroring the fix that worked for `FutureCvdProxy`)
  — all come back essentially zero pooled** (-0.006 to +0.002 raw; -0.007 to -0.016 rolling) **and
  sign-inconsistent per day.** See `REVIEW_FINDINGS.md`, "DTE and VIX checked" and "trend efficiency
  reframed" sections (2026-09-12).
- **Status**: **Concluded — real contemporaneous relationship, no forward edge found in any
  formulation tried. NOT promoted to a scored candidate.** Same failure mode as
  `FutureDepthImbalance`: it documents a move that already happened rather than anticipating one. A
  genuine negative finding, not a dead end to hide — the underlying VIX/price relationship is real
  and confirms the intuitive model, it just isn't usable as a *predictive* score input in the forms
  tested so far.
- **Days validated**: 08–11 Sep 2026.
- **Overlaps with**: would have been attractive precisely because it's independent of the option
  chain/future tick flow every other candidate derives from — moot until a forward-looking
  formulation is found, if ever.
- **Open questions**: untested ideas if revisited later — a longer forward horizon (VIX may be a
  slower-moving series than 5-15 min price action), or VIX *level*/regime rather than *change* (a
  different question: does today's absolute VIX level say something about the day's character,
  as opposed to per-cadence changes predicting per-cadence price moves).

### Trend reversion (signed net price change / path length over a trailing window) — *renamed from "trend efficiency"*

- **What it measures**: how clean a recent trend is, *signed* by direction: `net_change /
  path_length` over a trailing 15-minute window, naturally bounded to [-1, +1] by construction
  (net ≤ path always, since path is the sum of absolute per-cadence moves and net is their signed
  sum). Originally named "trend efficiency" and used procedurally as a gate on `FutureCvdProxy`'s
  flip decisions (see `CLAUDE.md`'s "No gating during single-metric evaluation" — that use has been
  stepped back from). Renamed here because the confirmed correlation sign contradicts what
  "efficiency" implies (see below).
- **Sign/read**: **tested and confirmed directional, 2026-09-12 — and the sign is inverted from the
  naive "clean trend continues" assumption the gate was built on.** A clean, efficient trend over
  the past 15 minutes is followed by partial **reversal**, not continuation:

  | Framing | Pooled vs fwd 5m | Pooled vs fwd 15m |
  |---|---|---|
  | Signed (continuation hypothesis) | -0.045 | **-0.146** |
  | \|Magnitude\| (volatility hypothesis) | +0.075 | -0.014 |

  Directional/reversion framing shows a real (if noisy) negative correlation, strongest at the
  15-minute horizon and on 10 Sep specifically (-0.455) — a short-term overextension/exhaustion
  effect, not a volatility/magnitude predictor (that framing is near-zero and sign-inconsistent).
  This retroactively explains why the flip-gate hurt 11 Sep: it trusted flips *more* during high
  trend efficiency, on a continuation assumption the data doesn't support — a high-efficiency
  moment is when a reversal is more, not less, likely.
- **Status**: **Provisionally promoted as a directional (reversion) score candidate — sign
  confirmed, but low confidence: only 4 days, and one day (10 Sep) dominates the pooled
  correlation.** Should be re-checked once more days exist before trusting it at real weight.
- **Score formula (2026-09-12)**: already naturally bounded, no rank/tanh transform needed —
  `score = -100 * (trailing_15min_net_change / trailing_15min_path_length)`. The **minus sign is
  the whole point**: raw ratio positive (clean uptrend) → score negative (mild bearish/reversion
  read); raw ratio negative (clean downtrend) → score positive.
- **Days validated**: 08–11 Sep 2026.
- **Overlaps with**: economically distinct from `FutureCvdProxy` (order-flow-derived) and VIX
  (volatility-derived) — this is a pure price-path statistic, a third independent lens.
- **Open questions**: confirm the reversion read holds once more days exist rather than being
  driven by 10 Sep alone; the flip-gate finding it retroactively explains was itself only tested on
  4 days too.

### Spot/future basis change (future − spot, first-differenced)

- **What it measures**: per-cadence change in the future's premium over spot
  (`FutureChangeFromLastCadence - SpotChangeFromLastCadence`). The raw **level** is dominated by a
  mechanical cost-of-carry decay toward the next expiry (average basis 107→93→64 points across
  09-11 Sep as DTE counts down) and is not itself a live signal — first-differencing removes that
  slow drift, same lesson already learned from `FutureCvdProxy`'s cumulative-day version.
- **Source**: `CadenceContext.FutureCloseFromLastCadence` / `SpotCloseFromLastCadence` (both
  already captured); no new column needed, just a difference-of-differences.
- **Sign**: **confirmed** negative — basis widening (future outpacing spot) tends to be followed by
  a small pullback, not continuation. Same reversion flavor as trend-reversion below; two
  independent metrics now agree "short-term overextension → pullback."
- **Evidence**: per-cadence change vs. forward price is small but **the most cross-day-consistent
  sign found in this investigation** — negative on all 4 days at both 5m and 15m horizons (pooled
  -0.050 / -0.034). The rolling-15-min-sum version is stronger on 3 of 4 days (-0.125 to -0.172)
  but **flips to +0.192 on 08 Sep, the expiry day** — plausibly because basis is mechanically
  collapsing toward zero all session on expiry day, swamping the flow signal over a 15-minute
  window. See `REVIEW_FINDINGS.md`, "spot/future basis" (2026-09-12).
- **Status**: **Provisionally promoted — real, unusually consistent signal, modest magnitude.**
  Use the per-cadence change, not level or the rolling-window version, until the expiry-day
  exception is understood (needs another expiry day of data to know if it's systematic or a
  fluke). Caveat: `SpotCloseFromLastCadence` is a calculated-index LTP with no continuous quote
  (documented in `CadenceContext.cs`) — a stale-tick artifact was confirmed in this same data (a
  spurious basis collapse to 14.75 at 15:29-15:30 on 10 Sep, traced to spot freezing while the
  future kept ticking) — a real, if usually small, noise source for any basis-change computation,
  not just at day's end.
- **Score formula (2026-09-12)**: session-rank based (same reasoning as `FutureCvdProxy` — natural
  scale not yet well characterized). `score = -(percentile_rank(basis_chg_per_cadence,
  session-so-far) - 50) * 2` — negated because the correlation itself is negative (basis widening
  ⇒ bearish read, not bullish).
- **Days validated**: 08–11 Sep 2026.
- **Overlaps with**: same reversion direction as trend-reversion (both say "recent overextension
  predicts pullback") but a structurally different input (spot/future spread vs. price path shape)
  — worth watching for redundancy once real correlation between the two is checked, per the
  composite's own "don't double-count overlapping inputs" concern (F9).
- **Open questions**: does the expiry-day sign-flip on the rolling version replicate on the next
  real expiry day; is per-cadence basis change materially cleaner if computed only from real spot
  ticks (excluding cadences where `SpotChangeFromLastCadence` is a stale carry-forward) rather than
  every cadence regardless.

### Option depth imbalance — Table 3, option chain — *CONFIRMED, strongest candidate so far*

- **What it measures**: resting order-book quantity imbalance (bid qty vs. ask qty), not executed
  flow — a genuinely different question from CVD (aggression), PCR (raw activity balance), and
  OI-diff (position count). Reuses the exact same averaging technique already validated for the
  tracked future's own depth imbalance (`DepthImbalanceAccumulator`, renamed from
  `FutureDepthAccumulator` since the implementation was already fully generic — reused directly,
  not duplicated).
- **Source**: `StrikeCadenceSnapshot.DepthImbalanceFromLastCadence` (per strike, per 15s cadence,
  averaged over every real tick), rolled up to `StrikeBandCadenceSnapshot.CallDepthImbalanceAvg`/
  `PutDepthImbalanceAvg` (plain average across strikes-in-band and cadences-in-bucket).
- **Sign**: **confirmed** — `CallDepthImbalanceAvg − PutDepthImbalanceAvg` positive correlates with
  the ATM call's own price rising and the ATM put's own price falling (ThisWeek expiry). Verified
  against the option's own price using the strike-identity-safe, time-guarded methodology built
  during the CVD investigation.
- **Evidence**: the strongest finding across the entire Table 2/3 investigation, by two measures —
  **magnitude** (0.10–0.47, larger than PCR's or OI-diff's best readings) and **band-robustness**
  (16 of 16 band-day combinations coherent across all four bands — Itm2Atm1, Strike3, Strike5,
  Strike7 — unlike PCR, which needed the ITM-only band specifically). fwd-15m is the reliable
  horizon; fwd-5m is coherent on 3 of 4 days. See `REVIEW_FINDINGS.md`'s 2026-09-12 depth-imbalance
  section for the full per-day, per-band tables.
- **Confirmed against the future's price too (2026-09-13)** — the one gap external review
  correctly flagged (every other candidate got both targets checked; this one had only been run
  against the option's own price). `scripts/depth-imbalance-vs-future-price.sql`: ThisWeek/fwd-15m
  is **positive across all 4 bands on every one of the 4 days** (0.11–0.51), same direction as the
  option-price confirmation, same fwd-15m-more-reliable/fwd-5m-weaker-with-11-Sep-as-outlier
  pattern already documented. NextWeek stays weak and inconsistent against the future too, matching
  its existing flipped-sign-vs-ThisWeek behavior. This strengthens confidence rather than
  undermining it — unlike PCR and OI-diff, this candidate holds up under the cross-check.
- **Status**: **CONFIRMED**, ThisWeek expiry — now checked against both targets.
- **Score formula**: not yet finalized — natural fit is session-rank based (matching every other
  ratio/diff-shaped candidate here), sign preserved directly.
- **Days validated**: 08–11 Sep 2026.
- **Open questions**: NextWeek is internally coherent on all 4 days too, but the *direction* splits
  from ThisWeek's on 3 of the 4 days (08 Sep matches, 09–11 Sep run opposite) — a real, distinct
  pattern, not yet investigated; whether the same OI-diff-style ThisWeek-only restriction applies
  here, or whether NextWeek's flipped sign is itself a usable, separate signal.
- **Overlaps with**: conceptually closest to PCR (both option-chain, both call/put balance reads)
  but measures resting interest rather than traded activity — not yet cross-correlated against PCR
  or OI-diff to check for redundancy.

### Option CVD (aggressor-volume proxy) — Table 3, option chain — *CLOSED, concluded no edge*

- **What it measures**: same quote-rule aggressor classification as `FutureCvdProxy`, applied to
  the option chain instead of the future — a strike's own classified buy-minus-sell volume/notional.
- **Source**: `StrikeCadenceSnapshot.CvdProxyVolumeThisCadence`/`CvdProxyNotionalThisCadence`,
  rolled up to `StrikeBandCadenceSnapshot.CallCvdProxyVolumeNet`/`PutCvdProxyVolumeNet` (and the
  notional equivalent).
- **Status**: **Closed, 2026-09-12, after a rigorous multi-round investigation** — not a
  quick dismissal. Tested as: the call-minus-put diff (contract and notional), against both the
  future's price and the option's own price; then, after a friend's review identified the
  classification rule as too crude for a wide option spread (a mid-point rule is reasonable on the
  future's tick-wide spread, low signal-to-noise on an option's multi-rupee one), rebuilt with a
  tightened touch-rule classification (`CvdProxyAccumulator` now requires a print to reach or cross
  the actual bid/ask, not just cross the midpoint) and two further methodology corrections found
  while implementing it — a strike-identity-safe forward window (partitioned by `StrikePrice`, so a
  forward price change never silently splices across an ATM re-centering event) and a time guard
  (nulls the forward change if the real elapsed time falls outside tolerance of the intended
  horizon). Re-tested as call-alone, put-alone, and their sum ("vol-flow", testing a
  volatility-bid-not-direction hypothesis), against both `MarkPrice` and traded-price (LTP) targets
  — **five formulations, same result each time: sign flips day to day, no stable direction.**
  Concluded the metric is the wrong resolution for this instrument's microstructure, not
  under-tested. See `REVIEW_FINDINGS.md`'s 2026-09-12 closure section for the full per-day numbers
  across every formulation, and `docs/OPTION_CVD_METHODOLOGY_REVIEW.md` for the complete
  methodology write-up (produced for, and improved by, external review).
- **What's kept**: the touch-rule classification and the strike-identity-safe correlation pattern
  are real, general improvements, reused for any future option-price correlation work — not
  specific to CVD. `FutureCvdProxy` (a separate accumulator class, already confirmed) is completely
  unaffected by this closure.
- **Days validated**: 08–11 Sep 2026.

### PCR-OI — Call OI / Put OI (level-based), Table 3 — *watch candidate*

- **What it measures**: the classic Indian-markets "PCR" — total open interest balance between
  calls and puts, a *position level* (stock), distinct from both volume-PCR (activity, a flow) and
  OI-diff (position *change*, a flow). Not the same metric as either despite the name overlap.
- **Source**: `StrikeBandCadenceSnapshot.CallOiSum`/`PutOiSum` (new 2026-09-12), boundary-cadence
  total OI across the band's strikes.
- **Sign**: **confirmed, same reversed direction as OI-diff** — `log(CallOiSum/PutOiSum)` positive
  correlates with call price falling and put price rising. Same plausible mechanism (market makers
  as net option sellers/writers).
- **Evidence**: **pooled correlation is misleadingly weak** (-0.02 to -0.15) — per-day correlation
  is real and comparable to OI-diff's own strength (7 of 8 day/week combinations coherent,
  magnitude 0.10-0.40). Root cause of the mismatch, checked directly: the raw log-ratio's own range
  varies enormously day to day (OI accumulates over a contract's whole life, so each day sits at a
  different baseline unrelated to that day's price dynamics) — pooling across days with different
  baselines dilutes a real within-day relationship. Same "level vs. change" trap already hit with
  basis level and CVD's cumulative-day version.
- **Status**: **Watch candidate** — real per-day signal confirmed, but not usable as a raw
  pooled-across-days number the way volume-PCR is. Needs within-day normalization (session-rank
  based) before real use — this is a scoring-mechanism requirement, not an open question about
  whether the signal is real.
- **Days validated**: 08–11 Sep 2026.
- **Overlaps with OI-diff — FINALIZED (2026-09-12): distinct, not redundant, keep both.**
  Cross-correlated `pcr_oi_log` directly against `OiDiff` (`CallOiChangeSum − PutOiChangeSum`),
  ThisWeek/Itm2Atm1/5-min, all 4 days: level-vs-level runs **-0.15 to -0.43**, same sign every
  day (real, coherent, but moderate — nowhere near the ±0.8+ that would mean "same metric twice").
  The decisive check was **bar-to-bar delta vs. delta**: if PCR-OI were just OI-diff re-expressed,
  PCR-OI's own cadence-to-cadence change would track OI-diff's flow almost mechanically, since
  OI-diff *is* the flow that moves PCR-OI's underlying OI stock. It doesn't — **+0.154, -0.059,
  +0.006, -0.093** across the 4 days, no consistent sign, indistinguishable from noise. Root cause:
  PCR-OI's delta is a *log ratio of levels* (`Δlog(Call/Put)` ≈ relative % change on each side,
  normalized by that side's own OI base) while OI-diff is a *raw unnormalized difference* — the
  normalization decouples them whenever the call-side and put-side OI bases differ in size, which
  they routinely do. **Verdict: keep both as separate watch candidates.** PCR-OI (level, stock,
  base-normalized) and OI-diff (flow, unnormalized count) measure genuinely different things that
  happen to share a modest, real, same-signed relationship at the level — not duplicate signals,
  and neither subsumes the other. See `REVIEW_FINDINGS.md`'s 2026-09-12 "PCR-OI/OI-diff redundancy
  check" section for the full query and per-day numbers.
- **Open questions**: does session-rank normalization recover the pooled signal cleanly (the
  remaining open item — redundancy is now closed).

### PCR — put/call volume ratio, Table 3 (`niftysignal_backtest_analysis`, option chain) — *watch candidate (revised 2026-09-12)*

**Original "CONFIRMED, 4/4 coherent" finding was invalidated by a strike-identity bug and fully
retested — this entry reflects the corrected verdict.** `scripts/pcr-vs-option-price.sql` (written
before the strike-identity-safe fix existed) filtered `StrikeOffsetFromAtm = 0` without
partitioning the forward window by `StrikePrice` — the same splicing bug found while investigating
F58. Full corrected battery (`scripts/pcr-full-battery-corrected.sql`, all 4 bands, both cadences,
volume and notional, per-day):
- **08 Sep and 10 Sep**: strongly, consistently **reversed-direction** (call busier → call price
  falls, put price rises) across every band/cadence/framing tested that day.
- **09 Sep**: noise — small, inconsistent, sometimes wrong-signed depending on cadence.
- **11 Sep**: tends the *opposite* (naive bullish) direction, most clearly on NextWeek.
- **Volume vs. notional**: now essentially interchangeable — the original "volume beats notional"
  finding does not replicate.
- **Band comparison**: no consistent winner across days — the original "Itm2Atm1 uniquely wins"
  claim does not hold; band preference is day-dependent.
- **Status**: **downgraded to watch candidate** (same tier as OI-diff/PCR-OI) — real, substantial
  signal on a majority-but-not-unanimous basis (2 of 4 days strongly reversed across every framing
  that day), materially weaker/less clean than depth imbalance's actual 16/16 coherence. Sign:
  **reversed** (net evidence favors it, 2 strong days vs. 1 leaning naive). 09 Sep flagged as an
  unexplained break — also now flagged independently by corrected OI-diff (see below), worth
  remembering as a cross-metric pattern, not yet investigated.
- See `REVIEW_FINDINGS.md`'s 2026-09-12 "PCR and OI-diff: full corrected battery, new verdicts"
  section for the complete per-day tables.
- **Score formula**: not yet finalized — deferred until the sign/status above is settled with more
  days; the session-rank approach used elsewhere remains the natural fit once it is.
- **Open questions**: whether 08/10 Sep's reversed signal is the real one and 09/11 Sep are the
  anomalies, or vice versa; needs more days before trusting either direction at real weight.

### OI-change-diff — Table 3, option chain — *watch candidate, sign unresolved (revised 2026-09-12)*

**Original "confirmed, inverted from naive reading" finding and its "market-makers-as-sellers"
explanation were invalidated by the same strike-identity bug as PCR, and fully retested.**
`scripts/oi-diff-vs-option-price.sql` had the identical splicing bug. Full corrected battery
(`scripts/oi-diff-full-battery-corrected.sql`, all 4 bands, both cadences, contract and notional,
both weeks, per-day):
- **ThisWeek, 08 Sep and 10 Sep**: consistently **naive bullish** direction (call OI rising → call
  price *rising*, put falling) — the opposite of what was originally reported and explained.
- **ThisWeek, 09 Sep**: noise at the finer cadence, but leans naive-bullish (same direction as
  08/10 Sep) at the coarser 15-min bucket.
- **ThisWeek, 11 Sep**: the *only* day showing the originally-reported reversed direction cleanly,
  and only at the 15-min bucket.
- **Net**: 3 of 4 ThisWeek days lean naive-bullish, 1 of 4 leans reversed — the opposite balance
  from the original "7 of 8 reversed" claim, which was built entirely on the buggy script.
- **NextWeek**: still genuinely mixed either direction — this part of the original reasoning
  (ThisWeek reflects reactive positioning, NextWeek doesn't) holds up independent of the bug; the
  ThisWeek-only scope restriction stays justified.
- **Contract vs. notional**: interchangeable, no clear winner (same pattern as PCR and CVD).
- **Status**: **remains a watch candidate**, but the sign is genuinely unresolved/day-dependent,
  not confidently reversed. The "market makers as sellers" economic narrative is retracted pending
  more days settling which direction actually dominates. 09 Sep independently flagged as anomalous
  by corrected PCR too (see above) — a cross-metric pattern worth remembering, not yet investigated.
- See `REVIEW_FINDINGS.md`'s 2026-09-12 "PCR and OI-diff: full corrected battery, new verdicts"
  section for the complete per-day tables.
- **Overlaps with PCR-OI — still finalized, unaffected by this correction.** The redundancy check
  (`scripts/pcroi-vs-oidiff-redundancy.sql`) correlates `CallOiSum/PutOiSum`-derived and
  `CallOiChangeSum/PutOiChangeSum`-derived series directly against each other — it never touches
  option `MarkPrice` or the forward-window computation that had the strike-identity bug, so it
  does not need re-running. PCR-OI and OI-diff remain distinct, not redundant (level-vs-level
  -0.15 to -0.43, coherent but moderate; delta-vs-delta ~0, no relationship) — see PCR-OI's own
  entry above.
- **Open questions**: which direction (naive or reversed) is actually real; what's different about
  09 Sep; needs more days before trusting either sign at real weight.

### Volume/OI turnover ratio — Table 3, option chain — *CLOSED, concluded no edge*

- **What it measures**: same-side activity intensity — `CallVolumeSum/CallOiSum`,
  `PutVolumeSum/PutOiSum` — how much of the resting position on each side is actively turning over,
  distinct from every other candidate here (all of which compare call vs. put; this one never
  crosses sides). A directional cross-side version would be algebraically identical to
  `PcrVolumeLog − PcrOiLog` (already-tracked metrics recombined) — not built, for that reason.
- **Sign**: none by construction (volume and OI are both ≥0) — tested against the *magnitude* of
  each side's own forward price move (`ABS(fwd_5m)`, `ABS(fwd_15m)`), not signed change.
- **Evidence**: pooled numbers looked promising at first (ThisWeek/5-min cadence/5-min-forward,
  put-side ~0.50–0.56 across all 4 bands) but the per-day breakdown immediately exposed it as a
  single-day artifact — 08 Sep alone hit 0.851 while 09/10/11 Sep sat at -0.05/0.14/0.05. Traced
  the 08 Sep extreme values to the last 15-20 minutes of the session (pre-close position
  square-off); excluding that window collapsed 08 Sep's correlation from 0.851 to 0.243, in line
  with the other days' noise. NextWeek was uniformly near-zero from the start. 15-min forward and
  15-min cadence were both weak throughout.
- **Status**: **CLOSED, no edge** (2026-09-12) — real per-day correlations, both sides, land in a
  noise band with no consistent sign or magnitude once the end-of-day artifact is excluded. Same
  closure standard as CVD. See `REVIEW_FINDINGS.md`'s 2026-09-12 section for full per-day numbers.
- **Days validated**: 08–11 Sep 2026.

- **Spot/future basis, OI change over 15 min** — raised by the user (2026-09-12) as a candidate
  gate/metric worth trying; not yet built for this dataset.
- **Put/call price-tracking residual** — investigated 2026-09-07/09, found not to replicate
  out-of-sample (see `REVIEW_FINDINGS.md`'s "Research thread" section). Closed, not a candidate.

### Demand/supply exhaustion (cross-leg reaction proportionality) — *REOPENED, watch candidate*

- **What it measures**: user's own idea — does the ATM option's call/put price react
  proportionally to a 15-min Nifty (future) move (e.g., call should move ≥half the future's point
  move, put opposite), and does a weak/anomalous reaction predict a reversal in the *next* window.
  Distinct from the closed residual-autocorrelation thread above (that tested one leg's own
  Delta+Gamma+Theta mean-reversion; this tests cross-leg proportionality as an exhaustion signal).
- **First pass was closed in error (2026-09-12), then reopened same day**: the initial test found
  a weak, sign-flipping relationship even for the basic sanity check (does ATM option price track
  the future at all) — traced to a real bug, not a real finding. The query filtered
  `StrikeOffsetFromAtm = 0` and computed the forward window without partitioning by `StrikePrice`
  — the same strike-identity splicing bug already found and fixed once for CVD. Re-run with the
  correct partitioning: ATM option price tracks the future beautifully (correlation 0.72-0.96,
  beta 0.41-0.59 call / -0.36 to -0.50 put, every single day) — the premise holds fine; it was
  never a data-quality issue with the future either (spot gave near-identical numbers).
- **Corrected reversal test** (restricted to `|future_move1| >= 15` points, residual = actual
  15-min reaction minus the naive "half the move" expectation, correlated against the *next*
  15-min window's move): **put-side residual is coherent across all 8 day/week combinations
  (-0.24 to -0.45)** — a put that under-reacts (the user's original "anomaly") is followed by the
  trend continuing against itself. **Call-side points the other way** (7 of 8 negative, one
  outlier at +0.53) — it's the call *over*-reacting past the naive expectation, not under-reacting,
  that precedes reversal. Combined story: call over-confirmation + put under-confirmation together
  (a "euphoria/blow-off" signature) precede reversal — a real, sensible refinement of the original
  symmetric hypothesis, not a literal confirmation of it.
- **Status**: **REOPENED as a watch candidate** (2026-09-12) — real, moderate, mostly-coherent
  signal (put side especially), but only 4 days of heavily-overlapping-window data and one real
  call-side outlier. Not confirmed; worth tracking with more days. See `REVIEW_FINDINGS.md`'s
  2026-09-12 "F58 REOPENED" section for the full bug writeup and both correlation tables.
- **Days validated**: 08–11 Sep 2026.
- **Open questions**: does the call-side pattern (over-confirmation, not under-reaction) hold up
  over more days or is 08 Sep NextWeek's outlier a warning sign; whether a composition-stable
  ±3-strike band average (avoiding the ATM-only choice's single-contract noise) changes the
  picture — not yet tried with the corrected partitioning.

### IV skew (`PutAvgIv − CallAvgIv`), Table 3, option chain — *REVISED 2026-09-16: level closed (structurally one-sided); REOPENED same day as a 15-min rolling-change watch candidate*

- **What it measures**: a genuinely different character from every other Table 3 candidate — a
  pricing-surface (implied vol) read, not an activity/position-flow read. `CallAvgIv`/`PutAvgIv`
  are OI-weighted average IV per side, captured at each bucket's boundary cadence (a level, like
  `CallOiSum`). Because both legs are already solved against the same parity-consistent underlying
  (the 2026-09-07 fix), the pooled level sits close to zero by construction on symmetric bands —
  any real signal has to come from the sign/movement of the thin spread, not a big fixed level.
- **Band sensitivity is structural, not incidental**: only **Itm2Atm1** (which pairs ITM calls
  against ITM puts — genuinely different moneyness on each side, comparing the smile's shape) shows
  a coherent signal. The symmetric bands (Strike3/5/7, same strikes both sides) are noise, as
  expected — same-strike call/put IV is parity-consistent and carries little independent
  information once matched correctly.
- **Expiry day is a real, identified regime split, not an unexplained break** (2026-09-13, user's
  own catch): 08 Sep is confirmed via `CadenceContexts.NearestExpiryDate` to be the actual ThisWeek
  expiry date (0 DTE) — a genuinely different market-microstructure regime (gamma/theta/pin-risk
  dominate), not a mystery like OI-diff's unexplained 09 Sep break. Excluded from this metric's
  scope on that principled basis, the same way OI-diff excludes NextWeek.
- **Evidence, ThisWeek/Itm2Atm1, non-expiry days (09–11 Sep) — unanimous, not just majority**:

  | Date | 5-min vs option (fwd5/15) | 5-min vs future (fwd5/15) | 15-min vs option (fwd5/15) | 15-min vs future (fwd5/15) |
  |---|---|---|---|---|
  | 09 Sep | 0.273 / 0.461 | 0.231 / 0.404 | 0.450 / 0.566 | 0.356 / 0.530 |
  | 10 Sep | 0.235 / 0.239 | 0.181 / 0.185 | 0.291 / 0.444 | 0.090 / 0.376 |
  | 11 Sep | 0.238 / 0.313 | 0.245 / 0.310 | 0.224 / 0.327 | 0.306 / 0.319 |

  3 of 3 days, every cadence, every target, every horizon — same sign throughout. 15-min-vs-option
  is the standout cell (0.45–0.57), comparable to depth imbalance's own strongest readings.
- **Level carries the signal, not the delta** — cadence-to-cadence change correlates weakly and
  inconsistently (mostly under 0.15, no stable sign), same "level is where the information lives"
  pattern already seen for OI-diff/PCR-OI.
- **Sign is reversed from classic theory** — textbook skew reading says rising put IV relative to
  call IV reflects downside-hedging demand (bearish); the data shows the opposite (put skew rising
  correlates with price *rising*). Fourth metric this session where the naive textbook direction
  didn't hold (after OI-diff, PCR-OI, and PCR) — not explained away, just reported as tested.
  NextWeek is messier (only 09/11 Sep coherent), consistent with the same ThisWeek-vs-NextWeek
  dynamics-difference already established for OI-diff.
- **Status (superseded below)**: was **CONFIRMED among non-expiry ThisWeek days** (2026-09-13) —
  unanimous across the 3 clean days tested, both targets, both cadences, flagged even then as an
  early confirmation on a thin (3-day) base.
- **Score formula**: not yet finalized — level-based, so session-rank normalization is the natural
  fit (same reasoning as every other level-shaped candidate here).
- **Days validated (original 2026-09-13 finding)**: 08–11 Sep 2026 (08 Sep excluded from scope as
  the ThisWeek expiry day).

- **REVISED 2026-09-16 (as `ItmSkew` in the separate CoreScore 8-metric composite, same formula,
  same band, reused directly per this file's own 2026-09-13 confirmation)**: user's own live
  observation ("score reads bullish while Nifty is falling") led to correlating each of that
  composite's 8 terms against forward price independently. `ItmSkew`'s signed value (percentile
  rank of magnitude, sign reattached from the raw `PutAvgIv − CallAvgIv`) was checked across every
  day/chain combination where the 0-DTE exclusion doesn't apply — **ThisWeek non-expiry days
  (09/10/11 Sep) and NextWeek on all 5 days, 8 combinations total** — and came back **negative
  (bearish) on literally 100% of ~1,436 cadences in every single one, zero exceptions**:

  | Day | Chain | Bullish reads | Bearish reads |
  |---|---|---|---|
  | 09-09 | ThisWeek | 0 | 1436 |
  | 09-10 | ThisWeek | 0 | 1436 |
  | 09-11 | ThisWeek | 0 | 1432 |
  | 09-08 | NextWeek | 0 | 1436 |
  | 09-09 | NextWeek | 0 | 1436 |
  | 09-10 | NextWeek | 0 | 1434 |
  | 09-11 | NextWeek | 0 | 1439 |
  | 09-15 | NextWeek | 0 | 1436 |

  This is not "mostly negative" (which the original 3-day evidence already showed and correctly
  read as a real, if reversed-from-textbook, sign) — it is **never once positive**, across a much
  larger sample than the original confirmation. A metric that can structurally never read bullish
  isn't measuring a fluctuating market view; it's a near-constant offset.
- **Root cause identified — the band, not the IV solver**: `Itm2Atm1` compares ITM calls (strikes
  *below* spot, `InItm2Atm1` offsets -2..0) against ITM puts (strikes *above* spot, offsets 0..+2)
  — genuinely different points on the strike axis, not the same strike read through both option
  types. Equity indices (Nifty included) run a near-permanent downward-sloping skew (IV rises as
  strike falls), so "low strikes via calls" minus "high strikes via puts" reproduces that
  structural skew shape by construction, independent of any actual day's sentiment. Reviewed the
  IV pipeline itself (`ImpliedVolatilitySolver.cs`, `SyntheticForward.cs`) looking for a residual
  version of the 2026-09-07 systematic-bias bug this file already documents fixing (understated
  underlying $\to$ call IV up / put IV down) — the solver (Newton-Raphson + bisection fallback,
  gated on a minimum-reliable-Vega check) and the put-call-parity synthetic forward both look sound;
  no defect found there. The always-negative sign is consistent with **real, structurally-driven
  index skew being measured correctly**, not a miscalculation — it's the metric's *design* (an
  across-strike, cross-option-type comparison) that can't distinguish "normal daily skew" from
  "today's sentiment," not the arithmetic behind it.
- **Status**: **REVISED 2026-09-16 — one-sided by construction, not usable as a directional score
  input in its current form.** Downgraded from CONFIRMED. Not necessarily dead: the *level* of the
  skew (how steep, not its sign) or its *day-to-day change* might still carry real information —
  neither has been tested this way. As a signed contributor to a composite the way `ItmSkew` is
  used today, it should be treated as a near-constant bearish offset, functionally equivalent to
  zero-weighting it (backtested directly: see `GammaExposure`'s revision below for the joint
  drop-both result).
- **Days validated**: 08–11 Sep 2026 (original), 08–11 and 15 Sep 2026 (2026-09-16 revision, both
  weeks).
- **Open questions**:
  - **Its own cadence-to-cadence (~15s) delta — closed, but see the corrected re-test below before
    treating "change" as settled**: checked across ThisWeek 09-09/10/11 and NextWeek all 5 days.
    Correlation is essentially zero everywhere (\|corr\| <= 0.016 in all 8 combinations), hit rate
    within a point or two of 50% in every one. This specific granularity is noise. **But this is the
    WRONG formulation of "change over time"** for a demand/supply-shift read, by this project's own
    established convention — every other successfully-tested change metric here
    (`FutureCvdProxyNet5Min`, `BasisChange`, `OiChangeDiff15m`) is a multi-minute ROLLING WINDOW, not
    a tick-to-tick delta, precisely because a real shift plays out over minutes, not one 15-second
    print to the next. User caught this inconsistency directly (2026-09-16) — re-tested properly
    below.
  - **15-minute ROLLING change (current level minus the level 15 real minutes ago), ThisWeek,
    2026-09-16 — genuinely promising, not closed**:

    | Date | Correlation | Hit rate |
    |---|---|---|
    | 09-09 | +0.127 | 59.0% |
    | 09-10 | +0.194 | 58.2% |
    | 09-11 | +0.119 | 52.9% |

    Same sign on all 3 non-expiry ThisWeek days, moderate magnitude, two of three with a real
    hit-rate lift over a coin flip — categorically different from both the dead level and the
    tick-to-tick delta above. The 5-minute window is weaker and less consistent (-0.003 to +0.094)
    on the same 3 days, so 15 minutes specifically looks like the right horizon, not just "any
    window works." **Status: REOPENED as a watch candidate on the 15-min-rolling-change
    formulation specifically** — same 3-day base as the original (now-superseded) level
    confirmation, so treat with the identical caution (early signal, needs more days), not as
    settled. NextWeek is weak/sign-inconsistent on the same test (-0.045 to +0.098), consistent
    with NextWeek's already-established pattern of being messier than ThisWeek for nearly every
    candidate in this file — not a red flag, an expected confirmation of that pattern.
  - **Not yet tried**: this rolling-change formulation against the option's own price (only future
    price checked so far, via `CadenceContext.FutureChangeForDay`) and against a 5/15/30-min
    horizon sweep beyond the two tested; whether it holds up once 08 Sep (excluded here as the
    ThisWeek expiry day) or more non-expiry days are added.
  - **Same-strike Put-Call IV skew** (forward-filled per `(OptionType, StrikePrice)`, averaged over
    every strike quoted on both sides that cadence — removes the Itm2Atm1 band's cross-strike
    confound entirely): checked the same 8 combinations. Sign flips across days and chains with no
    pattern (positive on 08/09 Sep ThisWeek and 08 Sep NextWeek, negative on 11 Sep both chains and
    09/10/15 Sep NextWeek), magnitudes mostly under 0.15 with two exceptions that don't agree with
    each other in sign (NextWeek 11 Sep: -0.221 at 15m; ThisWeek 15 Sep: -0.133 at 15m). Removing
    the structural artifact did not uncover a hidden same-strike mispricing signal. **Closed, no
    edge found this way either.** Both `ItmSkew` framings tried (level, delta) and both `ItmSkew`
    band designs tried (cross-strike Itm2Atm1, same-strike) are now closed; no remaining untried
    formulation of this idea is apparent.

### OiBuildupNet — Table 2/3, option chain — *CLOSED, no edge found*

- **What it measures**: the single largest live-weighted component (0.3125, ~31% of everything
  nonzero in the original 14-component composite) — a sign-weighted vote across every strike in
  ATM±2 (both option types), `Σ(sign·|ΔOI|)`, where sign comes from `OiBuildupClassifier.Classify`
  (price-vs-OI-change quadrant) with a call/put-flipped sign lookup. Distinct from OI-diff (a blind
  call-side-vs-put-side difference, no per-strike classification at all) — OI-diff's watch status
  says nothing about this metric.
- **Tested across four formulations, all consistent with each other in finding nothing**:
  - Bucket-level (5-min OI change + matching 5-min spot change): no coherent sign across 8
    day/week combinations, magnitudes mostly under 0.15.
  - Bucket-level (15-min OI change + matching 15-min spot change): same picture, one isolated
    strong cell (10 Sep ThisWeek, +0.10 to +0.20) not replicated anywhere else.
  - **Exact live pairing** (~4-min OI change + previous-single-15s-cadence spot change, replicating
    live's own documented F50 mismatch verbatim): essentially zero everywhere, every cell under
    0.05 — the weakest of all four formulations.
  - Self-consistent pairing (~4-min OI change + matching ~4-min spot change): still weak and
    sign-inconsistent across days (mostly under 0.10), no better than the bucket versions.
- **A real, separate finding along the way**: the exact-live-pairing variant (A) was measurably
  weaker than the self-consistent variant (B) — pairing a multi-minute OI change with a
  single-15-second-old spot tick doesn't just look untidy, it appears to actively wash out
  whatever weak signal the self-consistent version carries. **If this component is ever revisited,
  the documented F50 spot/OI window mismatch needs fixing before re-evaluation, not after** —
  testing today's self-inconsistent live formula and concluding "no edge" would be judging the
  wrong thing.
- **Status**: **CLOSED, no edge found** (2026-09-13) — four honest formulations, all negative, same
  evidentiary bar as CVD's closure. This is the single largest weight in the live composite,
  tested here for the first time and found unsupported.
- **Days validated**: 08–11 Sep 2026, both weeks (NextWeek included here since this was tested
  before the NextWeek-drop decision above; also showed no signal).

### Live `Pcr` (`putOI/callOI`, raw ratio, ATM±2) — *CLOSED, apparent signal was a single-day trend artifact*

- **What it measures**: the live composite's second-largest weight (0.19). Mathematically the
  reciprocal of PCR-OI's own `log(CallOi/PutOi)` — same underlying OI data, un-logged and inverted.
- **Tested**: raw level, log level, and each one's own bar-to-bar delta (the gap PCR-OI's own
  redundancy check never closed — its delta was checked against OI-diff, never against price
  directly), at 5/15/30-min horizons, Strike5 band (= ATM±2), ThisWeek only.
- **Evidence — one day drove the entire apparent result, and it doesn't survive scrutiny**: 09 Sep
  showed a large, horizon-strengthening correlation (raw: -0.268/-0.416/-0.569 at 5/15/30-min vs.
  both option and future price; log: mirror-signed, +0.301/+0.465/+0.604). 08, 10, 11 Sep all
  showed nothing (every cell under 0.15). Three separate checks confirm this is a co-trending
  artifact, not a real signal: (1) **it does not replicate on 11 Sep, the day with by far the
  largest actual net move** (+156.6 points vs. 09 Sep's -85.6) — a real leading relationship should
  show up at least as strongly on the bigger trend day; instead 11 Sep shows ~0 everywhere.
  (2) **the correlation strengthens monotonically with horizon** (5→15→30 min), the textbook
  signature of two series co-drifting over a persistently one-directional session (09 Sep opened
  near its high and closed exactly at its daily low — a clean, uninterrupted grind down), not a
  decaying predictive lead. (3) **the metric's own delta shows nothing special on 09 Sep either**
  (-0.01/-0.04/-0.17) — real, updating information should show up in the increments too; it
  doesn't, consistent with the level merely tracking the day's own cumulative drift.
- **Status**: **CLOSED** (2026-09-13) — the raw-vs-log transform question is moot since the
  apparent effect isn't real to begin with. Same "per-day, not pooled" discipline that caught
  PCR-OI's dilution problem here caught the opposite failure mode: a single dramatic day
  masquerading as a real result.
- **Days validated**: 08–11 Sep 2026, ThisWeek only.

### Live `FuturesBasis` (raw level, `futureMid − spot`) — *CLOSED, same single-day artifact pattern*

- **What it measures**: the live composite's third-largest weight (0.1425, tied with `IvSkew`).
  The **raw level** feeding the composite's own rolling z-score — a genuinely different quantity
  from the already-tracked "Spot/future basis change" watch candidate (first-differenced), which
  this finding does not affect or reopen.
- **Re-ran the existing `scripts/spot-future-basis-correlation.sql`** (no strike identity involved
  at all — pure future-vs-spot, so the strike-splicing bug was never a risk here; the script's
  `RANGE BETWEEN ... FOLLOWING` windows self-guard by real elapsed time).
- **Evidence — the same artifact signature as `OiBuildupNet` and `Pcr`, found independently**:

  | Date | Level vs fwd5m | Level vs fwd15m | Day's own trend |
  |---|---|---|---|
  | 08 Sep | -0.090 | -0.031 | mild down |
  | 09 Sep | -0.192 | -0.238 | persistent grind down, closed at daily low |
  | 10 Sep | +0.088 | +0.111 | flat |
  | 11 Sep | -0.030 | +0.012 | biggest trend of the 4 days (+156.6) |

  Pooled level looks real (-0.135/-0.212) but is driven almost entirely by 09 Sep, **does not
  replicate on 11 Sep** (the actual biggest trend day, ~0 correlation), and strengthens rather than
  decays from 5 to 15 minutes — the same three red flags that closed live `Pcr`.
- **The per-cadence change version is different and more coherent, if too weak to matter**:
  -0.041 to -0.058, consistently negative on **all 4 days** — the only formulation here that's
  actually cross-day coherent, just too small in magnitude (under 0.06 everywhere) to call a real
  finding. Rolling-sum change versions are inconsistent (08 Sep flips positive) and untrustworthy.
- **Status**: **CLOSED** (2026-09-13) for the raw level as actually implemented live. Third live
  weight in a row (`OiBuildupNet`, `Pcr`, `FuturesBasis`) found to be a single-trending-day
  artifact once tested properly — not proof the whole original 14-component composite is
  unsupported, but a real, recurring pattern worth taking seriously before trusting its other
  untested weights.
- **Days validated**: 08–11 Sep 2026.

### Live `GammaExposure` (sum of per-strike Gamma×OI, ATM±10 chain) — *REVISED 2026-09-16: downgraded, one-sided in the CoreScore composite's own usage*

- **What it measures**: `Σ(call: +Gamma·OI, put: −Gamma·OI)` across the full nearest-expiry chain
  (now ATM±10, matching the actually-subscribed universe, unblocked by the 2026-09-13 schema
  widening). Tested using **version A**: each strike's own individually-solved-IV Gamma (not
  live's exact shared-ATM-vol simplification — version B not yet built).
- **Evidence — distinct from the three closures above, passes checks they failed**:

  | Date | vs option (5/15/30m) | vs future (5/15/30m) | Delta vs future (5/15m) |
  |---|---|---|---|
  | 08 Sep | 0.131 / 0.154 / 0.135 | 0.093 / 0.100 / 0.056 | 0.091 / 0.068 |
  | 09 Sep | 0.361 / 0.521 / 0.599 | 0.347 / 0.494 / 0.572 | 0.025 / 0.162 |
  | 10 Sep | 0.167 / 0.214 / 0.357 | 0.180 / 0.206 / 0.395 | 0.201 / 0.254 |
  | 11 Sep | 0.048 / 0.076 / 0.100 | 0.061 / 0.088 / 0.123 | 0.087 / 0.106 |

  Same positive sign on all 4 days; option and future targets **agree with each other on every
  single day** (unlike Vanna/Charm below); present, even if weaker, on 11 Sep (the biggest trend
  day) rather than vanishing; the delta also shows a real, same-signed pattern rather than nothing.
  None of that held for `OiBuildupNet`/`Pcr`/`FuturesBasis`, all closed as single-trend-day
  artifacts on the same day.
- **Honest open question, not yet resolved**: correlation strengthens with horizon on the two
  strongest days (09, 10 Sep) — the same shape flagged as suspicious for the closures above. Unlike
  those, there's a real competing economic explanation: dealer gamma-hedging pressure is a feedback
  mechanism that plausibly accumulates over a session rather than acting instantly, so a longer
  horizon mattering more is at least as plausible as a spurious co-trend. Not resolved either way.
- **Status (original 2026-09-13 finding, on the rigorous 4-day/dual-target battery above)**: was
  **watch candidate** — meaningfully better-supported than the three closures the same day, not yet
  at depth-imbalance/IV-skew confirmation tier.
- **Days validated (original)**: 08–11 Sep 2026, ThisWeek only.

- **REVISED 2026-09-16 (as `GammaExposure` in the separate CoreScore 8-metric composite — same
  economic idea, ATM±10, but reading `StrikeCadenceSnapshot.Gamma` directly rather than this
  entry's own per-strike-own-IV recomputation, and correlated only against forward future price,
  not both targets)**: same user-prompted per-term correlation pass as `ItmSkew` above. Checked its
  signed value against forward 15-min price on every day, **both chains this time (ThisWeek and
  NextWeek, 10 combinations)**:

  | Day | Chain | Bullish reads | Bearish reads | Hit rate |
  |---|---|---|---|---|
  | 09-08 | ThisWeek | 626 | 3 | 43.9% |
  | 09-08 | NextWeek | 626 | 0 | 43.6% |
  | 09-09 | ThisWeek | 639 | 786 | 58.2% |
  | 09-09 | NextWeek | 831 | 516 | 50.9% |
  | 09-10 | ThisWeek | 964 | 456 | 61.4% |
  | 09-10 | NextWeek | 0 | 1392 | 53.0% |
  | 09-11 | ThisWeek | 100 | 1323 | 51.1% |
  | 09-11 | NextWeek | 1210 | 143 | 50.0% |
  | 09-15 | ThisWeek | 1428 | 1 | 31.1% |
  | 09-15 | NextWeek | 445 | 974 | 31.4% |

  Genuinely two-sided on 2 of 10 (09-09 both chains, 09-10 ThisWeek) — real evidence the term isn't
  *always* degenerate, consistent with the original 2026-09-13 finding on those same days. But
  stuck heavily one-sided on the other 8, and the two clearest cases (09-10, 09-11) **flip which
  direction it's stuck in depending purely on which chain computes it, same day, same underlying
  market**. That rules out a market-wide directional explanation for the stuck cases — whatever's
  driving the sign when it does get stuck is chain-specific.
- **Root cause is very unlikely to be IV, mechanically**: unlike `ItmSkew` (a direct function of
  `PutAvgIv − CallAvgIv`), Black-Scholes Gamma is **non-negative for both calls and puts, always** —
  IV only scales Gamma's *magnitude*, never its sign. This term's sign comes entirely from
  `Σ(+Gamma·OI for calls) − Σ(Gamma·OI for puts)`, i.e. purely from which side carries more
  Gamma-weighted open interest that chain, that day. The ThisWeek/NextWeek sign-flip on the same
  day is exactly what you'd expect from call/put OI being distributed differently across two
  separate expiry chains — not from a shared IV computation feeding both. A residual IV bug could
  still be nudging magnitudes (and therefore borderline sign calls when Gamma-weighted OI is close
  between sides), but it isn't the primary driver of the multi-hundred-cadence one-sided stretches
  observed here.
- **Status**: **REVISED 2026-09-16 — downgraded from watch candidate.** The original 4-day,
  dual-target battery (above) still stands as real, separately-gathered evidence of a positive
  correlation using a more careful per-strike-IV Gamma recomputation; this newer check, using the
  simpler pipeline the live CoreScore composite actually reads, shows the term is unreliable in
  practice (one-sided, OI-driven, chain-dependent) more often than it's a clean two-sided signal.
  Backtested directly, zeroing this term's weight alongside `ItmSkew`'s (both derive from the
  option chain's Greeks/IV surface, both showed one-sidedness) **improved every one of the
  Hysteresis/Crossover/Combined strategy variants' 5-day totals** in `CoreScoreOptionSimulator`
  (e.g. Hysteresis +75.03 → +119.60 pts; Crossover 15/30 +248.15 → +254.50 pts) — though it did not
  fix the one day (09-15) that prompted this whole check, since that day's miscalibration was
  spread across nearly all 8 terms, not concentrated in these two.
- **Days validated**: 08–11 Sep 2026, ThisWeek only (original); 08–11 and 15 Sep 2026, both weeks
  (2026-09-16 revision).
- **Magnitude-gating follow-up, closed (2026-09-16)**: hypothesis was that GammaExposure might be a
  real signal specifically when call/put Gamma-weighted OI is decisively imbalanced (large \|raw\|)
  and pure noise near balance (small \|raw\|) — worth separating rather than judging the term as one
  blend of both. Median-split each day/chain by \|GammaExposureRaw\| and correlated each half
  against forward 15-min move independently (10 day/chain combinations, 20 buckets total). **Not
  supported — if anything, mildly the opposite**: on NextWeek, the LOW-magnitude (near-balanced)
  half had a *higher* hit rate than the HIGH-magnitude half on 4 of 5 days (e.g. 10 Sep: 63.5% low
  vs 43.0% high; 11 Sep: 56.0% low vs 42.7% high). ThisWeek showed no consistent pattern either way
  (sometimes low wins, sometimes roughly tied, never a clean high-magnitude win). **Closed — gating
  by the term's own decisiveness doesn't rescue it**, and there's a hint (not yet explained) that
  extreme readings might be *less* trustworthy than moderate ones, worth remembering if this term is
  revisited.
- **Rolling-change follow-up (2026-09-16, same correction as `ItmSkew` above — a real demand/supply
  shift plays out over minutes, not one 15-second tick)**: is dealer positioning building up or
  unwinding, rather than its raw level — 5-min and 15-min rolling change (current minus N-minutes-
  ago) vs forward 15-min move:

  | Date | Chain | 5-min window | 15-min window |
  |---|---|---|---|
  | 09-08 | ThisWeek | +0.068 / 53.2% | +0.115 / 54.2% |
  | 09-09 | ThisWeek | +0.162 / 52.9% | +0.282 / 55.0% |
  | 09-10 | ThisWeek | +0.254 / 60.7% | +0.045 / 55.0% |
  | 09-11 | ThisWeek | +0.106 / 54.6% | +0.071 / 45.7% |
  | 09-15 | ThisWeek | -0.115 / 47.8% | -0.261 / 44.3% |
  | 09-08 | NextWeek | -0.109 / 51.5% | -0.131 / 41.6% |
  | 09-09 | NextWeek | +0.177 / 56.0% | +0.169 / 51.7% |
  | 09-10 | NextWeek | -0.069 / 51.0% | -0.314 / 39.3% |
  | 09-11 | NextWeek | +0.007 / 47.2% | -0.130 / 44.9% |
  | 09-15 | NextWeek | -0.062 / 45.3% | -0.222 / 35.9% |

  ThisWeek leans positive on 4 of 5 days (0.05–0.28) with today (09-15, the day that prompted this
  whole check) the clear exception, flipping negative on both windows — consistent with today being
  a broadly anomalous session rather than this reformulation being wrong. But unlike `ItmSkew`'s
  15-min result, the two windows don't agree on which is stronger day to day (09-10: 5-min far
  beats 15-min; 09-09: the reverse), and NextWeek shows no consistent sign at all. **Status: open,
  not closed and not confirmed** — real enough to keep as a lead (worth re-checking as more days
  accumulate, ideally settling the 5-vs-15-min question first), not clean enough to reopen as a
  watch candidate the way `ItmSkew`'s 15-min change was.
- **Open questions**: does the original per-strike-own-IV recomputation (version A above) show the
  same chain-dependent sign-flip if re-run on NextWeek — would isolate whether the live pipeline's
  simpler Gamma source is itself adding noise beyond what version A already has; how much of the
  magnitude difference between the two pipelines traces to `ItmSkew`'s own identified band-driven
  skew bias flowing into each strike's own solved IV; why extreme readings underperform moderate
  ones in the magnitude-gating check above, if that pattern replicates on more days; which rolling
  window (5 or 15 min) is actually right for the change formulation, since the two disagree day to
  day above.

### Live `VannaExposure` (sum of per-strike Vanna×OI, ATM±10 chain) — *CLOSED, no edge*

- **What it measures**: identical structure to `GammaExposure`, `Greeks.Vanna` instead of `Gamma`.
- **Evidence — fails the option/future coherence check GEX passes**:

  | Date | vs option (5/15/30m) | vs future (5/15/30m) | Delta vs future (5/15m) |
  |---|---|---|---|
  | 08 Sep | 0.225 / 0.329 / 0.243 | -0.024 / -0.025 / -0.136 | -0.149 / 0.024 |
  | 09 Sep | -0.110 / -0.197 / -0.218 | -0.090 / -0.191 / -0.232 | 0.095 / 0.051 |
  | 10 Sep | 0.015 / -0.054 / -0.209 | -0.003 / -0.080 / -0.274 | -0.061 / -0.165 |
  | 11 Sep | -0.006 / -0.033 / -0.056 | -0.050 / -0.075 / -0.104 | 0.126 / 0.106 |

  08 Sep: option (+0.33) and future (-0.03) flatly disagree on the same day. 10 Sep sign-flips
  within itself across horizons against the same target. Delta has no stable sign across any day.
- **Status**: **CLOSED, no edge** (2026-09-13).
- **Days validated**: 08–11 Sep 2026, ThisWeek only.

### Live `CharmExposure` (sum of per-strike CharmPerDay×OI, ATM±10 chain) — *CLOSED, no edge*

- **What it measures**: identical structure to `GammaExposure`, `Greeks.CharmPerDay` instead of
  `Gamma`.
- **Evidence — same coherence failure as Vanna**:

  | Date | vs option (5/15/30m) | vs future (5/15/30m) | Delta vs future (5/15m) |
  |---|---|---|---|
  | 08 Sep | 0.349 / 0.493 / 0.326 | 0.008 / 0.030 / -0.091 | 0.097 / 0.071 |
  | 09 Sep | 0.038 / 0.074 / 0.070 | 0.017 / 0.070 / 0.087 | -0.052 / -0.071 |
  | 10 Sep | -0.074 / -0.010 / 0.147 | -0.062 / 0.015 / 0.205 | -0.074 / -0.066 |
  | 11 Sep | 0.038 / 0.084 / 0.121 | 0.078 / 0.121 / 0.164 | -0.149 / -0.045 |

  08 Sep: a strong option-only reading (+0.49 at 15m) against essentially nothing on future
  (+0.03) — the same one-day target disagreement that closed Vanna. 10 Sep sign-flips within
  itself. 09/11 Sep are small but at least target-consistent; delta is consistently negative on 3
  of 4 days but too small (under 0.15) to rescue it.
- **Status**: **CLOSED, no edge** (2026-09-13).
- **Days validated**: 08–11 Sep 2026, ThisWeek only.

### Live `IvSkew` (dynamic OTM strike pair, `putIv − callIv` at `spot ± expectedMove`) — *watch candidate*

- **What it measures**: the live composite's third-largest weight (0.1425). Targets a real
  expected-move OTM strike pair (`expectedMove = spot·atmVol·√t`), not the already-confirmed
  ITM-band (Itm2Atm1) version — a genuinely different strike selection under the same metric name.
  Unblocked by the 2026-09-13 schema widening (ATM±10). `atmVol` proxied as the average of ATM
  (offset=0) call+put IV per cadence — an approximation of live's actual shared reference-vol
  solve, not byte-identical, since that solve isn't independently replicated here.
- **08 Sep is unusable, a genuine data limit, not a finding**: expiry-day IV distortion (avg IV
  0.271 vs. ~0.10 normally) inflates the target offset up to 19 strikes out — beyond even the
  widened ±10 band. `n_resolved=1245` vs. 1500 on other days confirms real data gaps, not a weak
  signal. Consistent with 08 Sep already being excluded from the confirmed ITM-band finding.
- **Evidence, 09–11 Sep (target offsets 5-7, comfortably within the persisted band)**:

  | Date | Level vs option (5/15/30m) | Level vs future (5/15/30m) | Day's trend |
  |---|---|---|---|
  | 09 Sep | -0.090 / -0.122 / -0.298 | -0.073 / -0.112 / -0.278 | down |
  | 10 Sep | -0.048 / +0.056 / +0.162 | -0.037 / +0.051 / +0.244 | flat |
  | 11 Sep | -0.157 / -0.243 / -0.340 | -0.163 / -0.248 / -0.366 | biggest trend (+156.6) |

  09 and 11 Sep (both real trend days) show a coherent, strengthening-with-horizon relationship,
  strongest on 11 Sep — the *opposite* pattern from the closures earlier the same day, where the
  biggest trend day showed nothing. 10 Sep (the flat day) flips sign across horizons, plausibly a
  low-information day rather than a contradiction. Delta shows nothing special on any day.
- **The standout finding: this sign is the classic textbook direction, the reverse of the
  already-confirmed ITM-band (Itm2Atm1) result.** Negative correlation here means rising put-skew
  (relative to call) predicts price *falling* — standard "hedging demand is bearish." The
  ITM-band version showed the opposite (put-skew-up → price *rising*). Not a contradiction to
  resolve — evidence that "IV skew" isn't one robust concept; its economic sign depends on which
  strikes are being compared (OTM-at-the-expected-move vs. ITM-within-a-fixed-band).
- **Status**: **watch candidate** (2026-09-13) — real, coherent on 2 of 3 usable days, comparable
  magnitude to the ITM-band version at 30-min, but a smaller evidence base (2/3 vs. that version's
  3/3) and 08 Sep couldn't be tested at all rather than being weak.
- **Days validated**: 09–11 Sep 2026, ThisWeek only (08 Sep excluded — data limit, not regime
  choice, distinct reason from the ITM-band version's expiry-day exclusion though the same day).
- **Open questions**: does the sign-reversal-vs-ITM-band pattern hold with more days; whether a
  wider persisted band (beyond ±10) would recover 08 Sep; how the `atmVol` proxy compares to
  live's actual shared reference-vol solve if that's ever independently replicated.

### Live `VolumePcr` (`putNotional/callNotional`, full chain) — *watch candidate*

- **What it measures**: `notional = VolumeDelta × MarkPrice` per cadence (confirmed directly from
  `ComputeVolumePcrAndCvdProxy` — a per-cadence single-mid-price calc, **not** the persisted
  per-tick-weighted `NotionalDelta` column, checked before assuming either was reusable), summed
  per side across the full chain (ATM±10, matching live's own "entire nearest-expiry chain" scope
  now that the schema is widened).
- **Evidence — passes the same checks that closed `OiBuildupNet`/`Pcr`/`FuturesBasis`**:

  | Date | Raw vs option (5/15/30m) | Raw vs future (5/15/30m) | Day's trend |
  |---|---|---|---|
  | 08 Sep | 0.127 / 0.257 / 0.209 | 0.114 / 0.322 / 0.330 | down |
  | 09 Sep | -0.045 / 0.144 / 0.224 | -0.048 / 0.116 / 0.185 | down |
  | 10 Sep | 0.156 / 0.259 / 0.179 | 0.105 / 0.240 / 0.109 | flat |
  | 11 Sep | 0.002 / 0.111 / 0.171 | 0.034 / 0.146 / 0.200 | biggest trend (+156.6) |

  Consistent sign on all 4 days (both targets); present, not vanished, on 11 Sep (the biggest
  trend day) — the same pattern that distinguished `GammaExposure`. **Horizon shape is actually
  more reassuring than anything tested today**: 08 and 10 Sep *peak at 15 minutes and decay at 30*
  (the normal shape for a real, decaying forecast), 09 and 11 Sep still rise at 30-min but at far
  smaller magnitude (0.20–0.22) than the artifacts ever showed (up to 0.57–0.60). Log transform
  mirrors raw almost exactly (opposite sign, similar magnitude) — unlike `OiBuildupNet`'s A/B
  pairing test, the transform choice barely matters here. Delta is weak but consistently negative
  (raw) on 3 of 4 days.
- **Worth naming directly**: this is meaningfully more robust than live's OI-based `Pcr` (raw
  `putOI/callOI`), which was closed the same day as a pure single-day artifact. Same "PCR" family
  by name, very different outcome once actually tested — notional/volume-based data held up,
  OI-level-based data didn't.
- **Status**: **watch candidate** (2026-09-13) — same tier as `GammaExposure` and the dynamic-
  strike `IvSkew`, cross-day consistent, not yet at confirmed tier.
- **Days validated**: 08–11 Sep 2026, ThisWeek only.

### Live `StraddleRichness` (ATM straddle actual-vs-Delta+Theta-predicted residual, windowed sum) — *CLOSED, no edge*

- **What it measures**: `richness = actualChange − (prevNetDelta·(S_now−S_prev) + prevTheta·elapsedDays)`,
  computed cadence-to-cadence on the ATM straddle (call+put combined), only when the ATM strike
  hasn't rolled between ticks (`_previousStraddleStrike == atmStrike`, replicated exactly via a
  `StrikePrice` LAG check). Spot proxied for synthetic S in the delta term (not persisted
  per-cadence; multiplied by a near-zero straddle net delta, so impact should be small). Summed
  over trailing 5/15-min windows per user's request, tested first before the raw per-tick version.
- **Evidence — fails two separate checks, not just weak on its own**:

  | Date | Sum5m vs option (5/15/30m) | Sum5m vs future (5/15/30m) |
  |---|---|---|
  | 08 Sep | 0.044 / -0.485 / -0.272 | 0.147 / -0.059 / 0.074 |
  | 09 Sep | -0.293 / -0.209 / -0.085 | -0.244 / -0.171 / -0.051 |
  | 10 Sep | 0.120 / 0.171 / 0.182 | 0.158 / 0.182 / 0.208 |
  | 11 Sep | -0.003 / -0.139 / -0.082 | 0.004 / -0.151 / -0.058 |

  **08 Sep fails the option/future coherence check** — strong against option (-0.485 at 15m),
  essentially nothing against future (-0.059) — the same target-disagreement red flag that closed
  `VannaExposure`/`CharmExposure`. Plausibly an expiry-day theta-sensitivity distortion (08 Sep is
  0 DTE), but even excluding it: **10 Sep clearly disagrees in sign with 09 and 11 Sep** (positive
  vs. negative, both targets, increasing with horizon), and 10 Sep is not an expiry day — a real,
  unresolved 2-vs-1 cross-day split with no regime explanation available.
- **Status**: **CLOSED, no edge** (2026-09-13) — fails both the target-coherence check and
  cross-day sign consistency, neither of which was rescued by the horizon-shape reasoning that
  saved `GammaExposure`/`VolumePcr`.
- **Days validated**: 08–11 Sep 2026, ThisWeek only.
- **Note**: the raw per-tick (unwindowed) version was not tested — user's own sequencing was
  windowed-sum first, then per-tick as a follow-up; given the windowed version's clear closure,
  worth deciding whether the per-tick version is still worth chasing before building it.

### Live `SpreadRatio` (`putMean/callMean` spread%-of-mid, full chain) — *CLOSED, no edge*

- **What it measures**: confirmed exact formula from `ComputeSpreadRatio`
  (LiveFeatureEngine.cs:880-912) — per-token average spread%-of-mid, meaned across every call/put
  token equally (mean-of-means, not liquidity-weighted), full chain. Persisted per-cadence
  `SpreadPctOfMid` (forward-filled per strike) used as the closest available proxy for live's
  rolling per-token average.
- **Evidence — same cross-day sign inconsistency that closed `StraddleRichness`, different outlier day**:

  | Date | Raw vs option (5/15/30m) | Raw vs future (5/15/30m) |
  |---|---|---|
  | 08 Sep | -0.208 / -0.390 / -0.091 | -0.008 / -0.025 / 0.088 |
  | 09 Sep | 0.093 / 0.159 / 0.174 | 0.096 / 0.142 / 0.141 |
  | 10 Sep | 0.034 / 0.095 / 0.092 | 0.015 / 0.080 / 0.085 |
  | 11 Sep | -0.029 / -0.074 / -0.136 | -0.018 / -0.071 / -0.136 |

  08 Sep repeats the expiry-day target-disagreement pattern (option -0.390 vs. future -0.025 at
  15m) seen across multiple candidates today. Excluding it: within each remaining day, option and
  future agree well, but **09/10 Sep are positive while 11 Sep is clearly negative**, comparable
  magnitude — the same unresolved cross-day split that closed `StraddleRichness`, just a different
  day as the outlier. Magnitudes throughout are small (mostly under 0.20); delta shows nothing
  (under 0.05 everywhere).
- **Status**: **CLOSED, no edge** (2026-09-13).
- **Days validated**: 08–11 Sep 2026, ThisWeek only.

### Ratio-composite `NotionalVolumeRatio` (`callNotional/putNotional`, ATM±5) — *watch candidate, strongest of the watch tier*

- **What it measures**: confirmed exact formula from `ComputeRatioNotionalVolumeRaw`
  (LiveFeatureEngine.cs:2704-2752) — same per-cadence notional calc as `VolumePcr`
  (`volumeDelta × mid`), but ATM±5 band (`RatioWideStrikeBand`), its own independent volume
  baseline, a ₹5,000 minimum-notional floor (`MinNotionalForVolumeRatio`), and **inverted
  orientation** (`callNotional/putNotional`, not `put/call` like `VolumePcr`).
- **Evidence — remarkably tight cross-day consistency at 30 minutes**:

  | Date | Raw vs option (5/15/30m) | Raw vs future (5/15/30m) |
  |---|---|---|
  | 08 Sep | -0.204 / -0.315 / -0.212 | -0.172 / -0.354 / -0.333 |
  | 09 Sep | 0.061 / -0.102 / -0.193 | 0.060 / -0.081 / -0.145 |
  | 10 Sep | -0.151 / -0.267 / -0.167 | -0.091 / -0.236 / -0.085 |
  | 11 Sep | 0.018 / -0.125 / -0.199 | -0.002 / -0.149 / -0.218 |

  At 30 minutes, all 4 days land in a tight band (-0.212, -0.193, -0.167, -0.199) — tighter
  clustering than anything else tested today, including `GammaExposure`. Option and future agree
  within every day. 5-minute horizon is noisier (two days flip slightly positive) but by 15-30
  minutes the picture is unanimous. Delta shows a modest, mostly-consistent positive pattern (3 of
  4 days at 5-min).
- **Cross-validates against `VolumePcr`**: `VolumePcr` (put/call, full chain, own baseline) showed
  *positive* correlation, strengthening with horizon, on all 4 days. This metric (call/put,
  ATM±5, independent baseline and floor) shows *negative* correlation on all 4 days — the same
  underlying relationship read through an inverted ratio, not a contradiction. Two differently
  scoped, independently implemented notional-ratio metrics agreeing once orientation is accounted
  for is stronger evidence than either alone.
- **Status**: **watch candidate, strongest of today's watch tier** (2026-09-13) — the 30-min
  tightness and cross-validation with `VolumePcr` put this a step above `GammaExposure`/`IvSkew`/
  `VolumePcr` themselves, though not yet promoted to confirmed pending the same scrutiny (more
  days, checking 08 Sep for expiry-day distortion) already applied elsewhere.
- **Days validated**: 08–11 Sep 2026, ThisWeek only.

### Ratio-composite `SizedOiFlowRatio` (constructive-only `|ΔOI|` ratio, ATM±5) — *CLOSED, no edge*

- **What it measures**: confirmed exact formula from `ComputeRatioSizedOiFlowRaw`
  (LiveFeatureEngine.cs:1875-1936) — `(callConstructive+1)/(putConstructive+1)`, sum of `|ΔOI|`
  for classifier-confirmed constructive strikes only (call: LongBuildup/ShortCovering; put:
  ShortBuildup/LongUnwinding), ATM±5 band, floor of 50 contracts (`MinContractsForOiFlow`,
  confirmed from source). Shares the identical spot/OI window mismatch as `OiBuildupNet`
  (confirmed via the source's own comment cross-referencing it) — tested both the exact live
  pairing (A) and a self-consistent pairing (B) again.
- **Evidence — weak in both variants, unlike `OiBuildupNet` where the pairing mattered a lot**:

  | Date | A vs option/future (15m/15m/30m) | B vs option/future (15m/15m/30m) |
  |---|---|---|
  | 08 Sep | -0.301 / -0.042 / 0.029 | -0.375 / -0.054 / 0.012 |
  | 09 Sep | -0.081 / -0.064 / -0.153 | -0.055 / -0.050 / -0.168 |
  | 10 Sep | 0.012 / 0.019 / -0.003 | -0.038 / -0.043 / -0.038 |
  | 11 Sep | 0.002 / 0.004 / -0.015 | -0.016 / -0.004 / -0.041 |

  08 Sep fails target coherence badly in both variants (option -0.30 to -0.38 vs. future
  essentially zero) — the same expiry-day pattern seen repeatedly today. Excluding it: 09 Sep has
  some real magnitude, but 10 and 11 Sep are essentially flat in both variants. Unlike
  `OiBuildupNet`, the pairing choice (A vs. B) doesn't meaningfully change the picture here — the
  weakness isn't a pairing artifact, the underlying metric itself carries little signal.
- **Status**: **CLOSED, no edge** (2026-09-13).
- **Days validated**: 08–11 Sep 2026, ThisWeek only.

### Ratio-composite `ResidualDifference` (per-leg 2nd-order Taylor residual, call − put) — *CLOSED, no edge*

- **What it measures**: confirmed exact formula from `ComputeResidualDifference`
  (LiveFeatureEngine.cs:2470-2538) — `predictedChange = Δ·dS + 0.5·Γ·dS² + Θ·elapsedDays + Vega·dVol`
  per leg (full 2nd-order Taylor, richer than `StraddleRichness`'s 1st-order combined version),
  `residualDifference = callResidual − putResidual`. Related to F58's already-confirmed finding
  (same call-minus-put-residual idea, simpler formula there) — this is the more rigorous version.
  Cadence-to-cadence, ATM-strike-roll guarded. Summed over trailing 5/15-min windows, same
  sequencing as `StraddleRichness`.
- **Evidence — unlike the other windowed-sum closures, option/future actually agree within every day**:

  | Date | Sum5m vs option/future (15m/15m) | Sum15m vs option/future (15m/15m/30m) |
  |---|---|---|
  | 08 Sep | 0.058 / 0.046 | 0.131 / 0.216 / 0.103 |
  | 09 Sep | -0.185 / -0.176 | -0.249 / -0.262 / -0.219 |
  | 10 Sep | 0.085 / 0.118 | 0.209 / 0.170 / 0.180 |
  | 11 Sep | -0.032 / -0.029 | -0.088 / -0.063 / 0.016 |

  No target-disagreement failure this time — but **08 and 10 Sep are clearly positive while 09
  and 11 Sep are clearly negative**, a clean 2-vs-2 split with no regime explanation available (08
  and 09 Sep are both down days yet show opposite signs, ruling out a simple "matches today's own
  trend" story too). Same cross-day sign-inconsistency failure class as `StraddleRichness` and
  `SpreadRatio`.
- **Status**: **CLOSED, no edge** (2026-09-13).
- **Days validated**: 08–11 Sep 2026, ThisWeek only.
- **Note**: per-tick (unwindowed) version not tested, same reasoning as `StraddleRichness` — the
  cross-day sign split is a structural, day-level issue that windowing choice is unlikely to fix.

### Ratio-composite `IvSkew25Delta` (nearest-25-delta strike, `putIv/callIv`) — *CONFIRMED among non-expiry ThisWeek days*

- **What it measures**: live's `ComputeIvSkewRatio25Delta`/`InterpolateIvAtDelta25`
  (LiveFeatureEngine.cs:2071-2161) does true 25-delta smile interpolation (rank every strike by
  estimated delta, solve IV for the 6 closest, linear-interpolate between the bracketing pair).
  Approximated here, per explicit choice, as the **single nearest-to-25-delta strike per side**
  (using each strike's own persisted, individually-solved `Delta` — not live's cheap shared-vol
  first-pass estimate), no bracket interpolation. `putIv/callIv` (ratio, matching live's exact
  orientation). Fresh per-cadence selection, full chain (ATM±10).
- **Evidence — the strongest result of the entire ratio-composite batch**:

  | Date | Raw vs option (5/15/30m) | Raw vs future (5/15/30m) |
  |---|---|---|
  | 08 Sep | -0.095 / 0.018 / -0.073 | 0.005 / 0.158 / 0.067 |
  | 09 Sep | -0.312 / -0.472 / -0.480 | -0.273 / -0.437 / -0.456 |
  | 10 Sep | -0.219 / -0.270 / -0.214 | -0.210 / -0.245 / -0.132 |
  | 11 Sep | -0.125 / -0.221 / -0.319 | -0.140 / -0.224 / -0.347 |

  08 Sep is weak and target-inconsistent — the now-familiar expiry-day pattern. **09, 10, 11 Sep
  are unanimous**: same sign, option and future agree closely within every day, magnitude up to
  -0.48 (larger than either other IV-skew variant tested today), and it replicates on 11 Sep (the
  biggest trend day) rather than vanishing. Delta shows nothing special, same "level carries the
  signal" pattern as every IV-skew variant this session.
- **Third independent confirmation of the classic textbook sign at an OTM strike selection** —
  matches the dynamic expected-move `IvSkew` finding exactly (rising put-skew predicts price
  falling), and is the opposite sign from the ITM-band (Itm2Atm1) confirmation. Two genuinely
  different OTM-selection methods (expected-move-based, nearest-25-delta) now agree with each
  other and disagree with the ITM-band method — a clean, real pattern: strike selection (OTM vs.
  ITM), not "IV skew" as one concept, determines the sign.
- **Status**: **CONFIRMED among non-expiry ThisWeek days** (2026-09-13) — same tier and same
  08-Sep exclusion logic as the original ITM-band IV skew finding, unanimous across all 3 usable
  days. Nearest-strike approximation (not true bracket interpolation) flagged explicitly.
- **Days validated**: 09–11 Sep 2026, ThisWeek only (08 Sep excluded — expiry-day distortion).
- **Open questions**: does true bracket interpolation change the picture materially; does the
  sign hold up over more non-expiry days.

### ITM-band vs. OTM-25-delta skew: FINALIZED redundancy check — substantially the same signal

- **Question**: the ITM-band (`Itm2Atm1`) and OTM (25-delta) IV skew confirmations show *opposite*
  signs against price — before scoring both, checked whether they're independent information or
  the same underlying smile tilt read from different strikes (same discipline as the PCR-OI/
  OI-diff redundancy check).
- **Evidence — cross-correlated the two raw values directly against each other, not against price**:

  | Date | Level-vs-level | Delta-vs-delta |
  |---|---|---|
  | 08 Sep | 0.084 | -0.733 |
  | 09 Sep | **-0.657** | -0.356 |
  | 10 Sep | **-0.462** | -0.257 |
  | 11 Sep | **-0.894** | -0.005 |

  On all 3 non-expiry days (the exact days both individual findings were confirmed on), the level
  correlation is strongly negative (-0.46 to -0.89) — consistent with the two metrics reading the
  same underlying smile tilt from opposite sides, not independent information. 11 Sep's -0.894 is
  close to a near-perfect inverse relationship. Delta is weaker and less consistent (level carries
  the information here, same pattern as every IV-skew variant tested this session), and 11 Sep's
  delta relationship nearly vanishes (-0.005) despite having the strongest level relationship — an
  interesting wrinkle: levels are highly redundant, bar-to-bar innovations less so.
- **Verdict: substantially redundant at the level.** Scoring both ITM and OTM skew as independent
  Core inputs at full weight would double-count one underlying signal, not combine two. **User's
  decision: ITM (`Itm2Atm1`) is the Core skew representative** (tested with the actual production
  band, no approximation, vs. OTM's nearest-strike approximation). OTM 25-delta stays on the Watch
  panel as a reference series, not separately scored, to see whether the redundancy holds up
  out-of-sample.
- **Days validated**: 08–11 Sep 2026, ThisWeek only.
