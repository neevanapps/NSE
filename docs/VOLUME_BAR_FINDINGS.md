# Volume-Bar Findings

Research log for the **volume-bar (event-driven) cadence** track — a parallel methodology to the
time-cadence (15s) pipeline documented in [`SCORE_CANDIDATES.md`](SCORE_CANDIDATES.md) /
[`REVIEW_FINDINGS.md`](REVIEW_FINDINGS.md). This file is scoped to volume-bar-specific findings
only; it does not replace or duplicate the time-cadence log, and a metric confirmed here is not
automatically promoted there (different clock, different rule shape until proven otherwise).

## Methodology (2026-09-17)

**Why volume bars.** The time-cadence pipeline samples every 15 real-time seconds regardless of
participant activity. A volume bar instead closes the instant cumulative *future* volume crosses a
fixed threshold, so the bar clock speeds up when the market is active and slows down when it is
quiet — the premise being that participant behaviour is better indexed by volume than by the
clock. Bars never split a single tick's volume across two bars (an "event bar" convention: a tick
that pushes cumulative volume past the threshold closes the bar on that tick, with any excess
volume carried into the next bar's opening state).

**Bar sizing.** Bar thresholds are quoted in underlying-future quantity, multiples of one 10-lot
(65 qty × 10 = 650). Three sizes were carried through calibration: 650, 1300, 2600.

**Pipeline (backtest-only, deliberately live-portable).**
- `NiftySignal.Features.VolumeBarBuilder` — pure accumulator, no DB/IO, builds one `VolumeBar`
  (OHLC, volume, OI-at-close, VWAP-at-close, CVD-net, depth-imbalance-avg, OFI-net, bar duration)
  per threshold crossing.
- `NiftySignal.Features.{FutureFlowAccumulator, DepthImbalanceAccumulator, OrderFlowImbalanceAccumulator, VolumeBarTrendReversionTracker, SignedRank}`
  — shared pure classes, reused by both the volume-bar pipeline and (already) the time-cadence
  pipeline where applicable, so wiring these live later is a matter of feeding them a live tick
  stream, not rewriting logic.
- `NiftySignal.VolumeBarData` (new project, mirrors `NiftySignal.BacktestData`) — the
  backtest-only shell: `VolumeBarPopulator` walks raw ticks from `niftysignal_vm_copy` and persists
  `VolumeBarRow`s into a dedicated `niftysignal_volume_bars` DB (idempotent per day+threshold,
  future-only, 09:15–15:30 IST), so a simulation reads pre-built bars instead of re-deriving them
  from ticks every time. `OptionPriceSeries` gives tick-accurate (not cadence-rounded) option
  fills via binary search. `TradeSimulator` runs the actual paper trade against real option prices.
  **None of this touches `NiftySignal.Host`/`NiftySignal.Dashboard`.**

**Trading rule.** Same hysteresis shape as the time-cadence
`CoreScoreOptionSimulator.SimulateDay`: enter at a score extreme, hold, exit only once the score
crosses to the *opposite* extreme (or end-of-day), one position at a time, always a long option
(Call on a positive signal, Put on a negative one). ATM strike is picked by nearest
`|strike − futurePrice|` (a simplification vs. the time-cadence pipeline's `[100,150]` price-range
convention — flagged for a later pass, not an oversight). **No risk management** (no stop-loss,
no target, no time exit besides end-of-day) — deliberately removed for this round so the raw
metric signal is visible before any risk overlay is layered on.

**Entry/exit gate is dynamic, not hardcoded**: `entryPercentile` means "trade only the top
`(100 − entryPercentile)`% most extreme score readings today," self-calibrating per day and per
metric. For the three `SignedRank`-normalized metrics (FutureCvdNet, DepthImbalance,
OrderFlowImbalance) the scaled score already *is* that percentile by construction. TrendReversion
is a raw bounded ratio, so it gets its own separate `SessionRankTracker` to become
percentile-based the same way. Self-inclusion-safe (ranks before adding) throughout.

**Validation.** Before trusting any new number, the volume-bar CVD series was cross-checked
against the already-trusted time-cadence CVD: day-cumulative sums matched **exactly, 0.0%
difference**, across every day tested. This is the strongest evidence the new pipeline computes
the same underlying quantities correctly, just on a different clock.

**Performance note.** The `calibrate` sweep initially re-scanned raw ticks for the same option's
price series on every (threshold, percentile) combination — up to ~126 redundant re-reads of the
same strike's ticks per metric. Fixed 2026-09-17 by widening `TradeSimulator`'s option-price cache
from per-call to a dictionary shared across the whole sweep/run, keyed by `(Day, Token)`. Volume
bars themselves were never the bottleneck — they're pre-tabled in `niftysignal_volume_bars` and
read once per day per threshold.

**Data window.** 2026-09-04 is excluded from all volume-bar simulations going forward — it does
not contain a full trading day, and its presence in earlier runs modestly understated some
metrics' figures (e.g. OrderFlowImbalance's total, since 04-Sep contributed a small loss). All
results below use **2026-09-08 through 2026-09-16, 6 trading days** (weekends/gaps between those
dates have no ticks and contribute nothing).

## Calibration sweep methodology

For each metric, swept `barVolumeThreshold ∈ {650, 1300, 2600}` × `entryPercentile ∈ {80, 85, 90,
93, 95, 97, 99}`, targeting a **7–20 trades/day** band (the user's own stated expectation for a
usable cadence). `TradingDays` in the sweep is the count of *distinct populated days* at that
threshold, not the calendar span, so `Trades/Day` isn't artificially diluted by non-trading dates.

## TrendReversion

Bar-count-windowed port of the existing `CoreScoreTrendReversionTracker` (net price change / path
length over a window, **negated** — clean trend reads as a reversion signal, same sign convention
as the time-cadence version). Already bounded to `[-1, 1]` by construction, so no session-rank
layer is needed for the raw score itself — only its *magnitude* is percentile-ranked for the entry
gate. `trendWindowBars = 15` (the tool's default) throughout.

**Best target-zone rows** (650/1300/2600 threshold):

| Threshold | Percentile | Trades/Day | Win Rate | Net (pts) |
|---|---|---|---|---|
| 650 | 93 | 13.2 | 55.7% | +68.75 |
| 650 | 95 | 11.0 | 54.5% | +64.10 |
| 1300 | 93 | 9.2 | 54.5% | +4.00 |
| 650 | 90 | 17.0 | 50.0% | +29.85 |

**Trade-level detail, 650/93** (79 trades, 55.7% win rate, net +68.75 pts):

| Day | Trades | Win Rate | Net (pts) |
|---|---|---|---|
| 09-08 | 16 | 50.0% | +108.95 |
| 09-09 | 20 | 60.0% | +16.15 |
| 09-10 | 10 | 60.0% | -0.05 |
| 09-11 | 15 | 46.7% | -43.40 |
| 09-15 | 11 | 63.6% | -0.30 |
| 09-16 | 7 | 57.1% | -12.60 |

**Concentration**: single best trade (09-08, Put entry 84.10 → exit 183.55) contributed **+99.45
pts, 144.7% of the 6-day total**. Single best day (09-08) contributed **158.5% of the total**.
Excluding either the top trade or the top day flips the 6-day net **negative**. Only 2 of 6 days
are clearly positive; the rest are roughly flat or negative.

**Verdict: NOT CONFIRMED.** The headline "+68.75 net, 55.7% win rate" is not broadly supported —
it is entirely an artifact of one outlier trade on one outlier day. Win rate alone (>50% at most
target-zone rows) is the only mildly encouraging signal; P&L is not.

## FutureCvdNet

Mid-point-rule aggressor-volume proxy (CVD proxy, not true CVD — the feed has no trade tape),
session-rank normalized, ported unchanged from the time-cadence pipeline.

**Best target-zone rows:**

| Threshold | Percentile | Trades/Day | Win Rate | Net (pts) |
|---|---|---|---|---|
| 650 | 97 | 15.8 | 48.4% | +59.60 |
| 1300 | 95 | 16.7 | 45.0% | -69.90 |
| 2600 | 95 | 10.3 | 46.8% | +14.10 |
| 1300 | 97 | 10.0 | 40.0% | -92.15 |

Weak and inconsistent across every row — win rate never clears 50% in the target band, and net
flips sign depending on threshold/percentile with no clear pattern.

**Trade-level detail, 650/97** (95 trades, 48.4% win rate, net +59.60 pts):

| Day | Trades | Win Rate | Net (pts) |
|---|---|---|---|
| 09-08 | 15 | 26.7% | -15.50 |
| 09-09 | 15 | 73.3% | +46.00 |
| 09-10 | 16 | 56.2% | -4.30 |
| 09-11 | 16 | 43.8% | +1.95 |
| 09-15 | 19 | 68.4% | +122.65 |
| 09-16 | 14 | 14.3% | -91.20 |

**Concentration**: single best trade (09-15, Put entry 82.65 → exit 141.30) contributed **+58.65
pts, 98.4% of the 6-day total** — the entire positive result is essentially one trade. Single best
day (09-15) contributed **205.8%** of the total (more than the whole net, since 09-16 alone lost
-91.20). Day-level win rate swings wildly (14.3% to 73.3%) with no stability.

**Verdict: NOT CONFIRMED / CLOSED for this pass.** Weakest of the four metrics — matches the
time-cadence pipeline's own prior finding that raw CVD needs more work to be tradeable.

## DepthImbalance

Resting-book imbalance average (level, not a change quantity), session-rank normalized.

**Best target-zone rows:**

| Threshold | Percentile | Trades/Day | Win Rate | Net (pts) |
|---|---|---|---|---|
| 650 | 93 | 15.3 | 54.3% | +262.20 |
| 1300 | 95 | 8.3 | **58.0%** | +242.45 |
| 2600 | 93 | 7.8 | 51.1% | +216.50 |
| 1300 | 93 | 11.3 | 51.5% | +197.05 |

Every single target-zone row across all three thresholds is **net positive** — the strongest and
most broadly consistent metric of the four.

**Trade-level detail, 1300/95** (50 trades, 58.0% win rate, net +242.45 pts):

| Day | Trades | Win Rate | Net (pts) |
|---|---|---|---|
| 09-08 | 6 | 33.3% | +5.05 |
| 09-09 | 7 | 71.4% | +27.85 |
| 09-10 | 7 | 71.4% | +39.60 |
| 09-11 | 4 | 25.0% | -42.05 |
| 09-15 | 15 | 60.0% | +163.50 |
| 09-16 | 11 | 63.6% | +48.50 |

**Concentration**: single best trade (09-15, Put entry 47.10 → exit 131.10) contributed **+84.00
pts, 34.6% of the total** — notable but not dominant. Single best day (09-15) contributed **67.4%**
of the total, but unlike TrendReversion/FutureCvdNet, **5 of 6 days are net positive** even with
09-15 as an outsized contributor — the edge is not a single-trade artifact.

**Verdict: STRONGEST CANDIDATE.** Broadly supported across thresholds, percentiles, and days.
Best win rate of any row tested (58.0% at 1300/95). Recommended as the leading volume-bar metric
to carry forward.

## OrderFlowImbalance

Cont/Kukanov/Stoikov order-flow-imbalance formula — a change-in-book measure (top-of-book bid/ask
size delta between ticks), distinct from DepthImbalance's static snapshot. Session-rank
normalized, new metric introduced this track (2026-09-17), no time-cadence equivalent yet.

**Best target-zone rows:**

| Threshold | Percentile | Trades/Day | Win Rate | Net (pts) |
|---|---|---|---|---|
| 2600 | 95 | 11.7 | 50.0% | **+357.45** |
| 2600 | 93 | 14.3 | 45.3% | +316.35 |
| 1300 | 95 | 18.5 | 45.0% | +188.40 |
| 650 | 97 | 18.5 | 45.0% | +193.25 |

Highest raw net of the four metrics, but win rate never exceeds 50% in the target band —
profitability comes from win *size*, not win *frequency*.

**Trade-level detail, 2600/95** (70 trades, 50.0% win rate, net +357.45 pts):

| Day | Trades | Win Rate | Net (pts) |
|---|---|---|---|
| 09-08 | 4 | 100.0% | +61.25 |
| 09-09 | 12 | 41.7% | -12.80 |
| 09-10 | 11 | 36.4% | -31.25 |
| 09-11 | 14 | 57.1% | +107.95 |
| 09-15 | 14 | 57.1% | +227.75 |
| 09-16 | 15 | 40.0% | +4.55 |

**Concentration**: single best trade (09-15, Put entry 79.65 → exit 233.85) contributed **+154.20
pts, 43.1% of the total** — a large but not disqualifying share (consistent with the earlier
04-Sep-included pass, which found 43.5%; excluding 04-Sep barely moved this). Single best day
(09-15) contributed **63.7%** of the total. 4 of 6 days are net positive.

**Verdict: PROMISING BUT CONCENTRATED.** Highest raw net, and unlike TrendReversion/FutureCvdNet
the result survives removing the top trade (net would still be ~+203, positive) — but nearly half
the edge rides on one large move. Needs more days before being trusted at the same level as
DepthImbalance.

## 9th candidate: TopOfBookImbalance (2026-09-17)

Motivated by a live order-book snapshot walkthrough (Nifty Sep future, 12:22 IST 17-Sep-2026):
best bid 520 qty vs. best offer 65 qty (touch strongly bid-supported, TOB ratio ≈ +0.78) while the
summed 5-level book was **offer-heavy** (1.76L bid vs. 2.46L ask, ratio ≈ -0.17) at the same
instant. Since `FutureDepthImbalance` (the strongest validated candidate so far) already uses the
summed-5-level version, this tests the same underlying idea at **touch-only** granularity —
`TopOfBookImbalanceAccumulator` (new, mirrors `DepthImbalanceAccumulator` exactly but reads
`Bid1Qty`/`Ask1Qty` instead of `TotalBidQty`/`TotalAskQty`), wired into `VolumeBarBuilder` as a new
`TopOfBookImbalance` column, session-rank normalized the same way as every other flow-style metric.
Populated across the same 7 days × 3 thresholds as everything else (required a DB drop+recreate+
repopulate for the new column, same precedent as adding OFI).

**Calibration sweep, 2026-09-08..2026-09-16:**

| Threshold | Percentile | Trades/Day | Win Rate | Net (pts) |
|---|---|---|---|---|
| 650 | 95 | 17.5 | 45.7% | +28.55 |
| 650 | 97 | 11.5 | 49.3% | +126.40 |
| 1300 | 93 | 16.2 | 44.3% | -18.70 |
| 1300 | 95 | 12.5 | 48.0% | -0.60 |
| 1300 | 97 | 8.2 | 51.0% | -10.45 |
| 2600 | 90 | 15.7 | 48.9% | +118.50 |
| 2600 | 93 | 11.7 | 48.6% | +143.70 |
| 2600 | 95 | 9.5 | **52.6%** | +89.35 |

Weaker and noisier than DepthImbalance's own sweep: win rate never clears 53% at any target-zone
row (DepthImbalance cleared 58% at its best), and the 1300 threshold is net *negative* across its
entire target band. Best net is 2600/93 (+143.70); best win rate is 2600/95 (52.6%, more modest
net).

**Trade-level detail, 2600/93** (70 trades, 48.6% win rate, net +143.70 pts):

| Day | Trades | Win Rate | Net (pts) |
|---|---|---|---|
| 09-08 | 8 | 50.0% | +16.35 |
| 09-09 | 16 | 50.0% | +57.80 |
| 09-10 | 10 | 60.0% | +30.40 |
| 09-11 | 11 | 54.5% | -27.95 |
| 09-15 | 16 | 25.0% | +43.35 |
| 09-16 | 9 | 66.7% | +23.75 |

**Concentration**: the top 2 trades (both on 09-15) contributed **+126.05 pts, 87.7% of the total**
— heavier concentration than any metric tested so far except FutureCvdNet. 5 of 6 days are net
positive (better breadth than the concentration number alone suggests), but 09-15 itself has the
*worst* win rate of any day (25.0%) — few large wins overwhelming many small losses, the same
signature already seen for 0-DTE days elsewhere in this track.

**0-DTE check, applied proactively this time** (09-08 and 09-15 are both 0-DTE, per the DTE map
established earlier):

| Bucket | Trades | Win Rate | Net (pts) |
|---|---|---|---|
| 0-DTE (09-08, 09-15) | 24 | **33.3%** | +59.70 (41.6% of total) |
| Non-0-DTE (09-09/10/11/16) | 46 | **56.5%** | +84.00 (58.4% of total) |

Same pattern already seen with BarDurationUrgency: the 0-DTE win rate is **below coin-flip**
(33.3%) and the net there is carried by variance, not directional skill, while the non-0-DTE win
rate (56.5%) is genuinely solid — better than the metric's own pooled win rate (48.6%).

**Verdict: NOT CONFIRMED as a standalone, pooled metric** — weaker and more concentrated than
DepthImbalance, and its pooled numbers are inflated by 0-DTE variance the same way
BarDurationUrgency's were. But there is a real, if modest, **non-0-DTE-specific signal** (56.5%
win rate over 46 trades across 4 days) worth more data before dismissing outright. The original
motivation — that TOB and 5-level DepthImbalance can diverge sharply at the same instant — is
still true and still worth exploring, but as a **divergence signal between the two metrics**
(price making a high while TOB and Depth disagree, per the original proposal's idea #5) rather
than as a standalone replacement for DepthImbalance. Not built in this pass.

## 10th candidate: TobDepthDivergence (2026-09-17)

Follow-up to TopOfBookImbalance: rather than trade the touch-only signal standalone, test the
**divergence between it and the already-validated DepthImbalance** — your own idea #5 from the
live-snapshot proposal. No new accumulator or schema change needed; both inputs are already
persisted per bar (`TopOfBookImbalance`, `FutureDepthImbalance`), so this is a pure derived
formula inside `TradeSimulator`.

**Sign convention had to be derived empirically, not assumed.** The live-snapshot narrative itself
described a thin-touch-vs-heavy-book situation as a "tension zone" that could resolve *either*
direction — genuinely ambiguous a priori. Built first as `TopOfBookImbalance - DepthImbalance`
("trust the thin touch"), the calibration sweep flatly rejected that: at the 2600 threshold, win
rate fell to 28.6-38.2% across the whole target-zone band with strongly negative net (-131 to -217
pts), consistent enough across percentiles to read as a real, inverted signal rather than noise.
Flipped to `DepthImbalance - TopOfBookImbalance` ("fade the thin touch, trust the deeper book")
and re-ran — same empirical-sign discipline already used for TrendReversion's own negation,
re-verified by re-running rather than assumed from the rejected version's numbers.

**Calibration sweep after the flip, 2026-09-08..2026-09-16:**

| Threshold | Percentile | Trades/Day | Win Rate | Net (pts) |
|---|---|---|---|---|
| 650 | 95 | 16.7 | 55.0% | +119.95 |
| 1300 | 90 | 22.8 | 57.7% | +267.90 |
| 1300 | 93 | 16.5 | 54.5% | +227.70 |
| 2600 | 90 | 14.8 | 52.8% | +277.75 |
| 2600 | 93 | 11.0 | 56.1% | +288.75 |
| **2600** | **95** | **8.5** | **60.8%** | **+420.65** |

Nearly every target-zone row across all three thresholds is net positive with win rate ≥50% —
the broadest, most consistent calibration picture found in this entire track, and 2600/95's
+420.65 is the single best result of any metric or combination tested so far.

**Trade-level detail, 2600/95** (51 trades, 60.8% win rate, net +420.65 pts):

| Day | Trades | Win Rate | Net (pts) |
|---|---|---|---|
| 09-08 | 9 | 33.3% | +6.80 |
| 09-09 | 8 | 62.5% | +70.45 |
| 09-10 | 10 | 50.0% | +25.00 |
| 09-11 | 3 | 33.3% | -45.60 |
| 09-15 | 12 | 83.3% | +248.20 |
| 09-16 | 9 | 77.8% | +115.80 |

**Concentration**: top single trade (09-15, Put entry 92.20 → exit 159.90) contributed +67.70 pts,
just **16.1% of the total** — lower concentration than DepthImbalance's own 34.6%, the best (least
concentrated) result found so far. 5 of 6 days net positive.

**0-DTE check** (09-08, 09-15): 21 trades, **61.9%** win rate, +255.00 net (60.6% of total).
**Non-0-DTE** (09-09/10/11/16): 30 trades, **60.0%** win rate, +165.65 net. The two win rates are
nearly identical — unlike BarDurationUrgency and TopOfBookImbalance, where 0-DTE win rate
collapsed below 50% while non-0-DTE held up, TobDepthDivergence shows a genuine, consistent edge
in **both** regimes.

**Session-phase check** (Open 09:15-10:00 / Mid 10:00-13:30 / Close 13:30-15:30): Open +196.80 (25
trades), Mid +110.40 (13 trades), Close +113.45 (13 trades) — positive in all three, not
concentrated in one time-of-day segment the way DepthImbalance (Open-only) or BarDurationUrgency
(loses in Open) were.

**Redundancy check vs. DepthImbalance** (the formula literally contains DepthImbalance as one of
its two terms, so this needed checking before crediting it as independent) —
`scripts/tobdepthdivergence-vs-depthimbalance-redundancy.sql`, same level-vs-level/delta-vs-delta
method as every other redundancy check in this track:

| Date | Level-vs-level | Delta-vs-delta | n |
|---|---|---|---|
| 09-08 | 0.399 | 0.303 | 646 |
| 09-09 | 0.440 | 0.366 | 911 |
| 09-10 | 0.387 | 0.309 | 513 |
| 09-11 | 0.329 | 0.308 | 941 |
| 09-15 | 0.282 | 0.306 | 991 |
| 09-16 | 0.298 | 0.296 | 611 |
| **Pooled** | **0.362** | **0.318** | 4,613 |

Moderate, consistently-signed correlation (well below the ±0.8 "same metric twice" threshold that
this project's convention uses) — real but not redundant, confirming TobDepthDivergence carries
genuinely additional information beyond DepthImbalance alone.

**Verdict: NEW STRONGEST CANDIDATE**, ahead of DepthImbalance itself. Clears every check the
earlier candidates stumbled on: lowest single-trade concentration found so far (16.1%), broadest
day coverage (5/6 positive), robust across both DTE regimes (unlike the two prior "second-tier"
candidates), and robust across all three session-phase segments (unlike DepthImbalance itself,
which is Open-only). Same overfitting caveat as everywhere else in this track applies with extra
force here, precisely because the result looks unusually good: every choice (threshold,
percentile, and now the sign convention itself) was derived by looking at these same 6 days, and
the sign flip in particular was chosen specifically because it produced a strong positive result
on this data — a textbook setup for overfitting even though the underlying economic story ("fade a
thin touch that disagrees with the deeper book") is plausible. This needs to be checked against
new, not-yet-seen trading days before being trusted at face value.

## Second batch: the remaining 4 of 8 planned future-side metrics (2026-09-17)

The original plan named 8 future-side candidates total. The four above were the first batch
(TrendReversion, FutureCvdNet, DepthImbalance, OrderFlowImbalance were three named up front plus
OFI as the first new-candidate addition). This second batch covers the other four candidates
raised in the original planning discussion: **Future OI-buildup quadrant**, **VWAP deviation**,
**bar-duration urgency**, and **price impact per unit volume** (a Kyle's-lambda-style proxy).
Large-trade/sweep detection was explicitly ruled out at planning time (needs per-print trade size,
which this feed doesn't have) and is not part of the 8.

**Sign convention for the two that aren't naturally directional.** Bar duration and price impact
don't inherently say "up" or "down" the way CVD/DepthImbalance/OFI do — a fast bar or a
high-impact bar says nothing about direction by itself. Per an explicit decision before building
these, both are signed by **that bar's own price-change direction**, with the (always-positive)
duration/impact quantity supplying the magnitude that then gets session-rank normalized the same
way as everything else. This makes them directly comparable/tradeable through the same
`SimulateDay` harness as the first four, at the flagged risk that they may turn out to be
volume-scaled duplicates of TrendReversion/price-momentum rather than new information — a
hypothesis each metric's own results below speak to.

### FutureOiBuildupQuadrant

Reuses the existing `OiBuildupClassifier.Classify(priceChange, oiChange)` (previously only wired
for option-strike OI, never the future's own OI) applied bar-to-bar: LongBuildup (price↑, OI↑) and
ShortCovering (price↑, OI↓) read bullish; ShortBuildup (price↓, OI↑) and LongUnwinding (price↓,
OI↓) read bearish; Neutral (either leg unchanged) reads as 0. Magnitude is the OI change itself,
session-rank normalized.

**Best target-zone rows:**

| Threshold | Percentile | Trades/Day | Win Rate | Net (pts) |
|---|---|---|---|---|
| 1300 | 80 | 16.3 | 42.9% | +128.95 |
| 1300 | 85 | 12.8 | 45.5% | +133.05 |
| 650 | 93 | 7.0 | 47.6% | +57.65 |
| 2600 | 80 | 15.5 | 44.1% | +29.60 |

Noisy — net sign and magnitude swing considerably between adjacent percentiles at the same
threshold (e.g. 650: +8.75 at 90 → +57.65 at 93 → +2.50 at 95, no clean trend), and win rate never
reaches 50% anywhere in the target zone.

**Trade-level detail, 1300/85** (77 trades, 45.5% win rate, net +133.05 pts):

| Day | Trades | Win Rate | Net (pts) |
|---|---|---|---|
| 09-08 | 14 | 50.0% | +75.60 |
| 09-09 | 9 | 33.3% | -13.00 |
| 09-10 | 16 | 50.0% | -14.35 |
| 09-11 | 11 | 63.6% | +59.25 |
| 09-15 | 17 | 29.4% | -2.50 |
| 09-16 | 10 | 50.0% | +28.05 |

**Concentration**: single best trade (09-08, Put entry 90.10 → exit 172.35) contributed **+82.25
pts, 61.8% of the total**. Top day (09-08) is **56.8%** of the total. Only 3 of 6 days net
positive.

**Verdict: NOT CONFIRMED.** Weak, noisy, single-trade-dependent — the price/OI-buildup framing
that works well for option strikes doesn't obviously transfer to the future's own OI on this
clock.

### VwapDeviation

Close price minus the whole-session running VWAP (`FutureFlowAccumulator.Vwap`, already tracked
and persisted as `VwapAtClose` — no new accumulator needed), session-rank normalized.

**No row at any tested threshold/percentile reached the 7–20 trades/day target band** — even at
the most permissive percentile (80), the highest rate seen was 5.0 trades/day (650/80). This is a
structural finding, not a calibration miss: a whole-*session* cumulative VWAP anchors slowly and
barely moves once enough volume has traded, so price rarely deviates far enough from it to rank in
the extreme percentiles this pipeline's gate requires. Lower percentiles than the standard sweep
grid (80/85/.../99) would be needed to reach the target band, which is a real threshold decision
outside the grid every other metric was judged against — flagged here rather than quietly explored
off-grid.

**Trade-level detail, 650/99** (shown for reference only — 3.2 trades/day, below the 7-day floor):
19 trades, 36.8% win rate, net +312.05 pts. **Concentration**: a single trade on 09-15 (Put
entered at market open, held the *entire day* to end-of-day because the score never crossed to the
opposite extreme) contributed **+384.85 pts, 123.3% of the total** — larger than the entire 6-day
net by itself, meaning every other day combined was net negative. This is one lucky whole-day hold
on the same volatile 09-15 session flagged below, not demonstrated edge.

**Verdict: NOT VIABLE as built.** Doesn't generate enough signals to be a trading metric on this
clock at any of the tested settings, and its best-looking number is a single-trade artifact.
Revisiting would need either a shorter VWAP window (e.g. reset per N bars instead of whole-session)
or accepting a much lower percentile floor — a design change, not a threshold tweak.

### BarDurationUrgency

Signed by the bar's own price-change direction, magnitude is fill speed (`1 / DurationSeconds` —
a faster bar means more participant activity packed into the same volume, reading as a stronger
signal in whichever direction that bar moved), session-rank normalized.

**Best target-zone rows:**

| Threshold | Percentile | Trades/Day | Win Rate | Net (pts) |
|---|---|---|---|---|
| 2600 | 90 | 17.8 | **53.3%** | +178.45 |
| 1300 | 97 | 16.2 | 44.3% | +153.40 |
| 1300 | 99 | 14.2 | 44.7% | +180.95 |
| 2600 | 93 | 14.7 | 45.5% | +74.30 |

Every target-zone row across both thresholds tested is **net positive** — the only one of this
second batch with that property, matching the pattern that made DepthImbalance the standout in the
first batch. Trade volume is far higher than any other metric (this signal fires on nearly every
bar, unlike CVD/depth/OFI which can go null on quiet bars), which also means more statistical
support behind each number.

**Trade-level detail, 2600/90** (107 trades, 53.3% win rate, net +178.45 pts):

| Day | Trades | Win Rate | Net (pts) |
|---|---|---|---|
| 09-08 | 11 | 27.3% | +32.95 |
| 09-09 | 27 | 55.6% | +52.20 |
| 09-10 | 13 | 46.2% | -12.70 |
| 09-11 | 25 | 60.0% | +23.80 |
| 09-15 | 21 | 57.1% | +100.40 |
| 09-16 | 10 | 60.0% | -18.20 |

**Concentration**: single best trade (09-15, Put entry 67.25 → exit 101.30) contributed **+34.05
pts, only 19.1% of the total** — by far the *least* concentrated of any metric tested in either
batch. Top day (09-15) is 56.3% of the total, but **4 of 6 days are net positive**, and the day
that lost the most (09-16, -18.20) is a small loss relative to the winning days.

**Verdict: SECOND STRONGEST CANDIDATE**, after DepthImbalance. Broadly supported across both
thresholds that reach target, best win rate of anything tested in either batch (53.3%), and the
best single-trade diversification seen so far. Genuinely different information from TrendReversion
despite sharing a price-direction sign — high trade count and different day-by-day pattern (best
day 09-15, worst day 09-16 vs. TrendReversion's best day 09-08) suggest it isn't just a relabeled
momentum signal.

### PriceImpact (Kyle's-lambda proxy)

Signed price change per unit volume for the bar, session-rank normalized. Flagged at design time
as a likely weak differentiator versus raw signed price change, because bars are built to a
roughly *fixed* volume threshold — the denominator barely varies bar-to-bar within one threshold,
so this was expected to closely track TrendReversion rather than add new information.

**Best target-zone rows:**

| Threshold | Percentile | Trades/Day | Win Rate | Net (pts) |
|---|---|---|---|---|
| 2600 | 95 | 19.0 | 40.4% | +72.85 |
| 2600 | 97 | 12.2 | 45.2% | +80.60 |
| 650 | 99 | 12.3 | 40.5% | +50.05 |
| 1300 | 99 | 9.3 | 25.0% | -56.55 |

Weak — win rate stays below 50% at every target-zone row (one row as low as 25.0%), and the
1300/99 row is a clear loser. Confirms the structural hypothesis: this doesn't behave like a
distinct edge, it behaves like a diluted momentum signal.

**Trade-level detail, 2600/97** (73 trades, 45.2% win rate, net +80.60 pts):

| Day | Trades | Win Rate | Net (pts) |
|---|---|---|---|
| 09-08 | 9 | 55.6% | +16.05 |
| 09-09 | 15 | 33.3% | +13.45 |
| 09-10 | 3 | 33.3% | -21.55 |
| 09-11 | 22 | 59.1% | +60.40 |
| 09-15 | 17 | 47.1% | +36.30 |
| 09-16 | 7 | 14.3% | -24.05 |

**Concentration**: single best trade (09-15, Put entry 96.80 → exit 148.35) contributed **+51.55
pts, 63.9% of the total**. Top day (09-11) is 74.9% of the total. 4 of 6 days net positive, but the
edge is thin and concentrated.

**Verdict: NOT CONFIRMED.** Matches the pre-registered hypothesis — the fixed-bar-volume
denominator mutes this metric's ability to add information beyond raw price direction, and the
result doesn't clear the bar DepthImbalance/BarDurationUrgency set.

## Cross-metric caveat: 2026-09-15 dependency

**2026-09-15 produced the single best trade of the window for five of the eight metrics**
(FutureCvdNet, DepthImbalance, OrderFlowImbalance, BarDurationUrgency, PriceImpact) and was also
the best single *day* for two more (TrendReversion had its best day on 09-08 instead, but 09-15
was still a top-3 day for it; VwapDeviation's entire net result is a single 09-15 trade). This was
evidently an unusually volatile/trending session (multiple option legs moving 50–400% in the
recorded trades, including a Put that returned nearly 5x in one held-to-close position). With five
of eight independently-computed metrics all finding their best result on the same calendar day,
a meaningful share of the apparent edge across this whole track may reflect "09-15 was a good day
to hold any long option," not metric-specific signal. This needs to wash out over more trading
days before any of these numbers are treated as durable edge — consistent with the project's
standing rule that no single day/week of backtest results is definitive. BarDurationUrgency is the
one metric whose result is *least* dependent on this single day (only 19.1% of its net from one
trade), which is itself a point in its favor.

## 0-DTE vs non-0-DTE split (2026-09-17)

Before trusting DepthImbalance and BarDurationUrgency (the two survivors after the second batch)
any further, checked whether their edge is metric-specific or an artifact of expiry-day gamma.
Queried the actual DTE for each of the 6 trading days directly from `instruments` (nearest
Option-type expiry minus `AsOfDate`):

| Date | Nearest expiry | DTE |
|---|---|---|
| 09-08 | 2026-09-08 | **0** |
| 09-09 | 2026-09-15 | 6 |
| 09-10 | 2026-09-15 | 5 |
| 09-11 | 2026-09-15 | 4 |
| 09-15 | 2026-09-15 | **0** |
| 09-16 | 2026-09-22 | 6 |

**09-08 and 09-15 — the two days behind nearly every metric's best trade in this whole track — are
both 0-DTE expiry days.** 0-DTE options can swing 100–400% on a modest underlying move purely from
gamma/theta collapse near expiry, independent of which metric triggered the entry. With only 6
days total (2 of them 0-DTE, one each at 4-/5-DTE, two at 6-DTE), a fine-grained DTE bucket split
would leave 1-day buckets — anecdotal, not evidence — so this was run as a coarse **0-DTE vs
non-0-DTE** split instead, re-aggregating the already-collected day-by-day trade results (no new
simulation needed).

**DepthImbalance (1300/95):**

| Bucket | Days | Trades | Win Rate | Net (pts) | Pts/Trade |
|---|---|---|---|---|---|
| 0-DTE | 09-08, 09-15 | 21 | 52.4% | +168.55 (69.5% of total) | 8.03 |
| Non-0-DTE | 09-09/10/11/16 | 29 | 62.1% | +73.90 (30.5% of total) | 2.55 |

**BarDurationUrgency (2600/90):**

| Bucket | Days | Trades | Win Rate | Net (pts) | Pts/Trade |
|---|---|---|---|---|---|
| 0-DTE | 09-08, 09-15 | 32 | **46.9%** | +133.35 (74.7% of total) | 4.17 |
| Non-0-DTE | 09-09/10/11/16 | 75 | 56.0% | +45.10 (25.3% of total) | 0.60 |

**DepthImbalance holds up on both regimes** — win rate stays above 50% whether it's expiry day or
not. P&L is bigger on 0-DTE (bigger option swings, as expected for any correct-direction bet held
through gamma), but the metric is still picking the right direction more often than not either
way — a genuinely robust signal, not an expiry-day artifact.

**BarDurationUrgency does not hold up the same way.** Its 0-DTE win rate is **below 50%** (46.9%)
— it is wrong more often than right on expiry days — and its headline net is carried entirely by a
handful of oversized 0-DTE winners outrunning more-frequent small losses, the signature of gamma
variance rather than directional skill. Strip out the 2 expiry days and what remains is real but
modest: +45.10 over 75 trades across 4 days, 56.0% win rate, ~0.60 pts/trade average.

**Revised standing**: DepthImbalance is now more clearly the strongest candidate — it is the only
metric in this track whose edge does not appear to depend on 0-DTE variance. BarDurationUrgency is
downgraded from "second strongest candidate" to a **second-tier watch item** alongside
OrderFlowImbalance — real edge on normal trading days, but the headline number overstates it.

## Pairwise redundancy check: DepthImbalance vs. BarDurationUrgency (2026-09-17)

Before assuming both survivors would each contribute independent weight to a future composite,
cross-correlated their raw values directly against each other (not against price/PnL) — same
discipline as the time-cadence pipeline's own redundancy checks (`scripts/pcroi-vs-oidiff-redundancy.sql`,
`scripts/itm-vs-otm-skew-redundancy.sql`): level-vs-level and delta-vs-delta, per day and pooled.
Run at a single **shared threshold (1300)** so the two series are bar-aligned — DepthImbalance's
own best combo (1300/95) and BarDurationUrgency's own (2600/90) sit on different thresholds and
aren't directly comparable without this. Script: `scripts/depthimbalance-vs-barduration-redundancy.sql`.
BarDurationUrgency's raw value is reconstructed exactly as `TradeSimulator.ComputeBarDurationScore`
computes it pre-rank (`SIGN(ΔClose) / DurationSeconds`); DepthImbalance's raw value is the already
-persisted `FutureDepthImbalance` column.

| Date | Level-vs-level | Delta-vs-delta | n |
|---|---|---|---|
| 09-08 | -0.047 | -0.058 | 1,136 |
| 09-09 | -0.017 | +0.092 | 1,534 |
| 09-10 | -0.055 | -0.022 | 907 |
| 09-11 | -0.051 | +0.011 | 1,628 |
| 09-15 | -0.080 | +0.031 | 1,656 |
| 09-16 | -0.100 | -0.012 | 1,088 |
| **Pooled** | **-0.053** | **+0.022** | 7,949 |

**Essentially zero relationship at either the level or the delta**, and the delta-vs-delta sign
isn't even consistent day-to-day. For scale: this project's established interpretation treats
±0.8+ as "the same metric twice" and the PCR-OI/OI-diff case (-0.15 to -0.43, moderate but real
and consistently signed) as "related but distinct, keep both." This result is weaker than even
that — squarely in "independent, no meaningful relationship" territory.

**Verdict: not redundant.** DepthImbalance (a resting-book snapshot) and BarDurationUrgency (bar
fill speed signed by price direction) measure genuinely different things and would each add
independent information to a future composite, not double-count one signal. The practical caveat
from the DTE split still applies: BarDurationUrgency's independent contribution is real but modest
on ordinary (non-0-DTE) days (~0.60 pts/trade), so "not redundant" doesn't imply "equally weighty"
— just that combining them isn't wasted effort once weighting is actually decided.

## Session-phase split: DepthImbalance vs. BarDurationUrgency (2026-09-17)

Third experiment in the redundancy/robustness sequence (after the 0-DTE split and the pairwise
correlation check). Bucketed each survivor's already-collected trades by **entry time** into Open
(09:15–10:00), Mid (10:00–13:30), and Close (13:30–15:30) — the exact boundaries from the
"first 45 min vs. after 13:30" hypothesis that prompted this check. No new simulation needed, this
re-aggregates the trade-level output already gathered for both metrics.

**DepthImbalance (1300/95):**

| Phase | Trades | Win Rate | Net (pts) |
|---|---|---|---|
| Open (09:15–10:00) | 19 | 73.7% | +188.20 (77.6% of total) |
| Mid (10:00–13:30) | 17 | 47.1% | -14.00 (net loss) |
| Close (13:30–15:30) | 14 | 50.0% | +68.25 |

**BarDurationUrgency (2600/90):**

| Phase | Trades | Win Rate | Net (pts) |
|---|---|---|---|
| Open (09:15–10:00) | 25 | 56.0% | -44.55 (net loss) |
| Mid (10:00–13:30) | 33 | 60.6% | +112.05 |
| Close (13:30–15:30) | 49 | 46.9% | +110.95 |

**DepthImbalance's edge lives almost entirely in the opening 45 minutes** — 73.7% win rate, 77.6%
of its total net, and it is a net *loser* through the midday session. **BarDurationUrgency shows
close to the opposite profile**: it loses money in the open (a decent 56.0% win rate there, but
smaller winners than losers) and its edge sits in the mid and close sessions instead.

This reinforces the pairwise-redundancy finding with a second, independent kind of evidence: the
two metrics aren't just statistically uncorrelated, they are profitable at *different times of
day* — a cleaner and more mechanistically interpretable form of independence than a bare
correlation coefficient. It also surfaces a concrete follow-up worth flagging (not built yet): a
time-of-day-gated combination — DepthImbalance restricted to the open, BarDurationUrgency
restricted to mid/close — could plausibly outperform either metric traded unconditionally across
the full session. Not attempted in this pass; noted as an open item below.

## Risk overlay: soft stop-loss test (2026-09-17)

Fourth and final experiment in the robustness sequence. Added an optional stop-loss to
`TradeSimulator.SimulateDayAsync` (`stopLossPercent`, threaded through the `trade` command's new
optional 7th argument), checked at bar granularity like every other exit rule this simulator
already has. Expressed as a **percent of entry premium**, not a fixed point amount — real trades
in this dataset range from a ₹0.05 option to a ₹230+ one, so a fixed-point stop would be
meaningless at one end and never trigger at the other. Checked before the score-invalidation exit
each bar, since capital protection should fire regardless of whether the signal has flipped yet.
Swept 20/30/40/50% against both survivors over the same 2026-09-08–16 window, no other change.

**DepthImbalance (1300/95):**

| Stop | Trades | Win Rate | Net (pts) |
|---|---|---|---|
| None (baseline) | 50 | 58.0% | +242.45 |
| 20% | 59 | 50.8% | +152.50 |
| 30% | 56 | 55.4% | +211.20 |
| **40%** | 54 | **59.3%** | **+259.15** |
| 50% | 52 | 57.7% | +246.50 |

**BarDurationUrgency (2600/90):**

| Stop | Trades | Win Rate | Net (pts) |
|---|---|---|---|
| None (baseline) | 107 | 53.3% | +178.45 |
| 20% | 116 | 51.7% | +185.60 |
| 30% | 111 | 52.3% | +233.00 |
| 40% | 110 | 51.8% | +158.60 |
| 50% | 109 | 52.3% | +178.40 |

**DepthImbalance genuinely benefits from a loose (40%) stop** — net *and* win rate both improve
over the unprotected baseline (+259.15 vs +242.45, 59.3% vs 58.0%). Both moving together is a much
stronger signal than net alone (a single compensating trade can move net without moving win rate).
Tighter stops (20–30%) actively hurt, consistent with cutting off trades that later recovered —
the 40% level appears to clip genuine tail risk (this metric's raw trade list already showed a
-99.5% and a -42.2% single-trade loss) without interfering with ordinary drawdown-and-recovery.

**BarDurationUrgency's case is murkier — not adopted.** Net is not monotonic across stop levels
(30% best, 40% worse than no stop at all), and win rate stays *below* the unprotected baseline at
every level tested. That is the signature of a stop mostly cutting off recoveries rather than
clipping genuine tail risk, consistent with this metric's earlier-established low single-trade
concentration (only 19.1% of net from its top trade) — there is less fat tail here for a stop to
usefully protect against.

**Decision**: adopt a ~40% premium stop-loss for DepthImbalance going forward as a real, modest
improvement over the raw signal. Leave BarDurationUrgency unprotected for now — the sweep doesn't
tell a consistent enough story to justify picking a specific level.

## Time-of-day-gated combination (2026-09-17)

Follow-up to the session-phase split: tested whether restricting each survivor to its own
strongest time-of-day segment beats trading it unrestricted across the full session. Since gating
only *suppresses* entries outside the assigned window and never changes how an in-window position
behaves (exits depend only on the score series, not on later suppressed entries), this reuses the
already-collected trade lists filtered by entry time rather than a new simulation — mathematically
identical to a true gated re-simulation for a single confined window.

**First check — does the adopted 40% stop change DepthImbalance's Open segment?** No: every
stop-loss exit in the stop-adjusted run landed on a Mid/Close entry, so DepthImbalance's Open
segment is unchanged either way (19 trades, 73.7% win, +188.20).

**That reveals the real issue with the original idea**: the case for gating DepthImbalance away
from Mid rested on Mid being a net *loser* (-14.00) in the no-stop data. The 40% stop already
fixes that — Mid+Close *with* the stop nets +70.95, not a loss. **Gating DepthImbalance is now
redundant with the stop-loss fix already adopted** — running it unrestricted all day (+259.15)
beats restricting it to the open only (+188.20).

**BarDurationUrgency is different** — it has no stop-loss, and its Open segment is a genuine net
loser (-44.55) independent of any fix. Gating *it* to Mid+Close only is a clean win: net improves
from +178.45 (full day) to +223.00 (Mid+Close only, 82 trades), with fewer trades.

Three configurations were compared against the two single-metric baselines:

| Configuration | Trades | Win Rate | Net (pts) |
|---|---|---|---|
| DepthImbalance alone (full day, 40% stop) | 54 | 59.3% | +259.15 |
| BarDurationUrgency alone (full day, no stop) | 107 | 53.3% | +178.45 |
| Both unrestricted, run in parallel | 161 | 55.3% | +437.60 |
| Original idea: both gated by time-of-day | 101 | 56.4% | +411.20 |
| **Refined: DepthImbalance ungated (stop already does the work), BarDurationUrgency gated to Mid+Close** | **136** | **55.1%** | **+482.15** |

The refined, **asymmetric** version wins — more net than either running both unrestricted or the
original symmetric gating idea, with fewer trades than unrestricted (136 vs. 161), i.e. better
return per unit of market exposure. Lesson: only gate the metric that doesn't already have a fix
for its weak segment — gating a metric that has already been stop-loss-corrected just discards
good trades.

**Important caveat**: every parameter in this combined result — bar threshold, entry percentile,
stop level, and now the time-of-day windows — was selected by looking at the same 6 days. This
combined configuration has not been checked against a single day it wasn't tuned on, and the
compounding effect of that many sequential choices on the same dataset is a real overfitting risk.
+482.15 should be read as "the best fit found on known data," not a forward expectation, until
validated on new days.

## Composite score (2026-09-17)

First attempt at combining the metrics still under active consideration into a single traded
score, per the project's standing "5-8 validated metrics combined into ONE score" endgame (see
`CLAUDE.md`). Includes all 5 metrics that survived to this point (TobDepthDivergence,
DepthImbalance, OrderFlowImbalance, BarDurationUrgency, TopOfBookImbalance) — the other 5 tested
earlier in this track were already closed out (not confirmed / not viable).

**Weight derivation** — not guessed. Each metric's weight is `(net pts/trade) x (concentration
discount) x (DTE-consistency discount) x (2026-09-17 out-of-sample nudge)`:

| Metric | pts/trade | Concentration discount | DTE consistency | Today's nudge | Raw score | Weight |
|---|---|---|---|---|---|---|
| TobDepthDivergence | 8.25 | 0.839 (16.1% top-trade share) | 0.969 (61.9%/60.0%) | 0.75 (missed badly today) | 5.03 | **46.3%** |
| DepthImbalance | 4.80 | 0.654 (34.6%) | 0.844 (52.4%/62.1%) | 1.05 (close & positive today) | 2.78 | **25.6%** |
| OrderFlowImbalance | 5.11 | 0.569 (43.1%) | 0.663 (44.2%/66.7% — new, computed for this exercise) | 1.00 (matched exactly today) | 1.93 | **17.8%** |
| BarDurationUrgency | 1.67 | 0.809 (19.1%) | 0.838 (46.9%/56.0%) | 0.85 (missed today) | 0.96 | **8.8%** |
| TopOfBookImbalance | 2.05 | 0.123 (87.7%) | 0.589 (33.3%/56.5%) | 1.10 (beat expectations today) | 0.163 | **1.5%** |

TobDepthDivergence's own formula already contains DepthImbalance and TopOfBookImbalance as terms,
so this composite is not 5 independent signals — deliberately kept all 5 anyway (explicit choice)
and let the weighting itself down-weight the overlap (TopOfBookImbalance's near-zero weight is
largely this overlap resolving itself, not an arbitrary override).

**Mechanics** (`VolumeBarMetric.Composite` in `TradeSimulator.cs`): each component is session-rank
normalized with its own independent tracker (same isolation a standalone run gets), combined as a
weighted average over whichever components are non-null on a given bar (missing ones excluded and
the remaining weights renormalized, not treated as zero). The blended result isn't itself
percentile-shaped the way one SignedRank output is, so it gets its own dedicated magnitude-rank
tracker for the entry-percentile gate — same treatment TrendReversion already gets for the same
underlying reason.

**Calibration sweep, 2026-09-08..2026-09-16** (the same 6 backtest days, not guessed — same
discipline as every other metric): best win-rate row is 1300/97 (9.8 trades/day, 59.3% win,
+229.10 net); best net row is 1300/95 (47.7% win, +387.80 net, win rate below 50% — a
big-win-driven result more like OFI's shape). Chose **1300/97** to carry forward, favoring win-rate
quality over raw net, consistent with how every other metric in this track was selected.

**2026-09-17 out-of-sample result** (1300/97, locked, no re-tuning): 9 trades, 44.4% win rate,
**-60.45 pts** — a losing day, below the calibrated 59.3% expectation. 98.8% of the loss is one
trade (a Put held from 09:15 to 09:36, -59.75 pts) — the same opening trade TobDepthDivergence
itself lost on today, except the composite held it *longer* (its blended score took longer to
flip than TobDepthDivergence's own score did), making the loss worse than that single component's
own version of the trade. Since TobDepthDivergence carries the plurality of the weight (46.3%), a
rough day for it pulls the composite down with it — the diversification benefit didn't show up on
this particular day.

**Full 7-day result** (2026-09-08 through 2026-09-17, same locked 1300/97 config): **68 trades,
57.4% win rate, +168.65 net pts.** 6 of 7 days net positive; only 09-17 (today) is negative.

| Day | Trades | Win Rate | Net (pts) |
|---|---|---|---|
| 09-08 | 9 | 66.7% | +43.55 |
| 09-09 | 7 | 71.4% | +19.10 |
| 09-10 | 10 | 50.0% | +21.10 |
| 09-11 | 15 | 53.3% | -5.85 |
| 09-15 | 13 | 61.5% | +135.10 |
| 09-16 | 5 | 60.0% | +16.10 |
| 09-17 | 9 | 44.4% | -60.45 |

**Concentration**: a single trade on 09-15 (Put, ₹78.95 → ₹186.75) contributed +107.80 pts, **64%
of the 7-day total** — more concentrated than DepthImbalance's own standalone 34.6%, worth
watching as more days accumulate.

**Important framing**: this is NOT 7 independent out-of-sample days. 6 of the 7 days (08-16 Sep)
were used to choose the composite's weights and the 97th-percentile threshold in the first place —
only 09-17 is genuinely fresh evidence. The honest read is "the composite survives adding one new
day without falling apart," not "the composite is validated." Same discipline as everywhere else
in this track: needs many more days that had no influence on its construction before being
trusted at face value.

**Status: promising first pass, not yet a verdict.** Holds up reasonably (6/7 days positive,
57.4% pooled win rate) even after absorbing a genuinely bad day, but the concentration and the
in-sample-heavy nature of the "7 days" both argue for continuing to accumulate real out-of-sample
days through the same validation log above before treating this as the project's actual composite
score.

## Correction: trading-hours gate had not been applied (2026-09-17)

The standing rule — no new position before 09:30 IST, no new position after 15:00 IST, any open
position force-closed by 15:15 IST — was **not enforced anywhere in `TradeSimulator` before this
point**. Confirmed directly in the code before fixing it: entries were firing as early as 09:15
and positions were running to `EndOfData` at ~15:30 in many trades throughout this whole track.
Every result above this section was produced without these gates.

**Fixed** in `TradeSimulator.cs`: added `EntryWindowStart` (09:30), `EntryWindowEnd` (15:00, no new
entries after), and `ForceCloseAt` (15:15, any open position closed regardless of its own score,
new `"TimeCutoff"` exit reason) as always-on rules, not an optional per-run parameter like
`stopLossPercent` -- this is a standing constraint on the simulator's own trading hours. Build and
605/605 tests unaffected (no test coverage exercises exact entry/exit timestamps at this level).

**Re-ran every leading metric across all 7 available days (2026-09-08 through 2026-09-17), same
locked (threshold, percentile, stop) configs as already adopted -- no re-tuning, straight
apples-to-apples before/after comparison:**

| Metric | Before (ungated): trades / net | After (gated): trades / win% / net | Change |
|---|---|---|---|
| **TobDepthDivergence** (2600/95) | 60 / +358.10 | **38 / 39.5% / +85.90** | Collapsed -- net -76%, win rate now *below* breakeven |
| DepthImbalance (1300/95, 40% stop) | 65 / +284.85 | 40 / 57.5% / +136.15 | Trade count down ~38%, win rate held |
| OrderFlowImbalance (2600/95) | 78 / +340.10 | 52 / 44.2% / +289.40 | Per-trade quality improved |
| BarDurationUrgency (2600/90) | 117 / +131.75 | 71 / 54.9% / +119.60 | Per-trade quality improved |
| TopOfBookImbalance (2600/93) | 80 / +204.85 | 57 / 50.9% / +118.05 | Net down ~42% |
| Composite (1300/97) | 68 / +168.65 | 43 / 51.2% / +190.60 | Net actually up despite fewer trades |

**TobDepthDivergence is the story here.** Trade-level inspection confirms this is real, not a bug:
a large share of its apparent edge was concentrated in entries fired in the first 15 minutes
(09:15-09:30) or positions that ran past 15:00 into the close -- exactly the windows the gate now
excludes. Concretely, its own best day (09-15) previously included a +50.85 trade entered at
15:19:40, which can no longer open at all under the 15:00 cutoff; several of its 09-16 winners
were 09:15-09:22 entries, also now excluded. Gated, only 3 of 7 days are net positive (down from
5-6), and a single trade now accounts for **78.8%** of the little profit that remains -- a complete
reversal from "lowest concentration of anything tested" to one of the most concentrated results in
this whole track.

**Revised standing**: DepthImbalance is the clearer strongest candidate again under the rules that
actually apply -- TobDepthDivergence's headline result turns out to have been substantially an
opening-minutes/late-day effect that doesn't survive the trading-hours constraint. OFI and
BarDurationUrgency held up better than expected (their per-trade quality *improved* once the gates
removed some of their weaker edge-of-day trades) and are worth a second look as relatively more
robust second-tier candidates than previously ranked.

**Open items before this is fully settled:**
- These are still the OLD percentile thresholds, calibrated without the gates. The true optimum
  may differ now that the tradeable window is narrower -- a fresh calibration sweep per metric is
  the rigorous next step, not yet done.
- The composite's weights (46.3% on TobDepthDivergence) were derived from the now-invalidated
  ungated numbers and need to be recomputed from the gated results above before the composite
  section's conclusions can be trusted.
- Every historical section above this correction (calibration tables, concentration checks, DTE
  splits, session-phase splits) was computed WITHOUT the gate and should be read as describing an
  ungated strategy that was never actually tradeable under the standing rule -- kept in the
  document for the record of what was tried, not as current guidance.

## Gated calibration (2026-09-17) — the authoritative results going forward

Re-ran the full calibration sweep (not just the trade command with old settings) for all 5 active
metrics over the same 6 backtest days, now with the trading-hours gates always on. Several
metrics' optimal (threshold, percentile) shifted, not just their numbers — the gated results below
are what should be used going forward; everything calibrated before the "Correction" section above
was tuned for a strategy that was never actually tradeable under the standing rule.

| Metric | Old best (ungated) | **New best (gated)** |
|---|---|---|
| TobDepthDivergence | 2600/95 -> 60.8% win, +420.65 | **1300/90 -> 55.7% win, +62.30** |
| DepthImbalance | 1300/95 -> 58.0% win, +242.45 | **650/93 -> 54.8% win, +210.90** |
| OrderFlowImbalance | 2600/95 -> 50.0% win, +357.45 | **2600/95 -> 42.2% win, +273.40** (same threshold, worse win rate) |
| BarDurationUrgency | 2600/90 -> 53.3% win, +178.45 | **2600/90 -> 58.1% win, +175.45** (same threshold, win rate improved) |
| TopOfBookImbalance | 2600/93 -> 48.6% win, +143.70 | **2600/80 -> 54.2% win, +112.00** |

**Trade-level detail and concentration check on each newly-selected combo:**

| Metric (new combo) | Trades | Win Rate | Net | Days positive (of 6) | Concentration |
|---|---|---|---|---|---|
| BarDurationUrgency (2600/90) | 62 | **58.1%** | +175.45 | 4 | Low -- best single trade only ~19% of net |
| DepthImbalance (650/93) | 62 | 54.8% | **+210.90** | 5 | Moderate -- best single trade ~31% of net |
| TopOfBookImbalance (2600/80) | 118 | 54.2% | +112.00 | 5 | Low by trade, but high trade count (19.7/day, upper edge of target) |
| TobDepthDivergence (1300/90) | 97 | 55.7% | +62.30 | 3 | Very high -- 09-15 alone is 146% of the total net (every other day combined loses money) |
| OrderFlowImbalance (2600/95) | 45 | **42.2%** | +273.40 | 4 | Extreme -- 56.4% of net from a single trade (09-15, +154.20) |

**BarDurationUrgency and DepthImbalance are now the two clearly strongest candidates.** This is a
real shift, not a restatement: BarDurationUrgency was only a second-tier watch item before this
correction; under the gates it now has the best win rate of anything tested (58.1%) with the
lowest concentration. DepthImbalance remains solid with the best net among the quality picks.

**TobDepthDivergence stays weak even after re-optimizing its own threshold for the gated rules**
-- confirms its earlier "strongest candidate" ranking really was an artifact of trading windows
that are no longer allowed, not something a different threshold recovers.

**OrderFlowImbalance is now the most fragile of the five** -- win rate fell to 42.2% (below a coin
flip) and over half its net profit is a single trade. Was already flagged as concentration-risky
before the gates; the gates made this worse, not better.

**Not yet done**: DTE-split and session-phase checks on these newly-selected gated combos (the
ones run before the correction were on the old, ungated combos and no longer apply), stop-loss
overlay re-testing for DepthImbalance's new 650/93 combo, and recomputing the composite's weights
from these gated numbers before re-running it. Flagged as open items, not completed in this pass.

## Full recheck across all 8 original candidates + deep checks, all 7 available days (2026-09-17)

Two follow-ups requested after the gated calibration above: (1) re-check whether the 5 metrics
closed out before the trading-hours-gate correction existed (TrendReversion, FutureCvdNet,
FutureOiBuildupQuadrant, VwapDeviation, PriceImpact) might reverse verdict under the real rules,
same way TobDepthDivergence's ranking flipped; (2) run DTE-split, session-phase, and a fresh
stop-loss test on the gated active-5's newly-selected combos, and recompute the composite from
those results -- this time using **all 7 available days** for calibration (2026-09-08 through
2026-09-17) rather than holding one out, per explicit instruction. Trade-off flagged up front: this
means there is currently no fresh out-of-sample day left in the dataset -- the next new trading day
that syncs in will be the first genuine out-of-sample check since this recalibration.

**None of the 5 previously-closed metrics reverse.** Re-ran their calibration sweeps over all 7
gated days: TrendReversion's best target-zone rows are inconsistent (some strong, most weak, no
stable pattern); FutureCvdNet, FutureOiBuildupQuadrant, and PriceImpact never clear 50% win rate at
a reasonable trade frequency; VwapDeviation still never reaches the 7-20 trades/day target band at
any setting. No change to their "not confirmed" / "not viable" status.

**Gated active-5, recalibrated on all 7 days** -- the extra day (09-17, a losing day for most
configs) mostly held thresholds steady but pulled some nets down:

| Metric | Best combo (7-day gated) | Win rate | Net |
|---|---|---|---|
| DepthImbalance | 650/93 | 53.8% | +138.10 |
| BarDurationUrgency | 2600/90 | 54.9% | +119.60 |
| TopOfBookImbalance | 2600/80 | 54.7% | +124.60 |
| OrderFlowImbalance | 2600/95 | 44.2% | +289.40 |
| TobDepthDivergence | 2600/90 | 45.1% | +129.30 -- **no combo in the full sweep clears both >50% win rate and strong net; its old 6-day-best (1300/90) actually flips to a loss once 09-17 is included** |

**DTE-split, session-phase split, and concentration** (script-computed --
`scripts/analyze-gated-trades.awk` -- to avoid the arithmetic errors manual day-by-day summing
risks at this volume; cross-checked against one metric by hand first):

| Metric | Top-trade share | DTE consistency (0-DTE/non-0-DTE win rate) | Phase pattern |
|---|---|---|---|
| DepthImbalance | 47.4% | 0.903 (50.0%/55.4%) | Edge is now almost ENTIRELY in the 09:30-10:00 open (78.6% win, +181.05) -- loses money in both Mid and Close |
| BarDurationUrgency | 28.5% | 0.943 (52.6%/55.8%) | Mirror image of DepthImbalance -- loses in the open (45.5% win, -40.70), strong in Mid and Close |
| TopOfBookImbalance | 23.3% | 0.996 (54.7%/54.9%) | Best DTE-robustness of all five; positive in every phase |
| TobDepthDivergence | 33.9% | 0.976 | Neither phase nor DTE regime clears 50% win rate cleanly |
| OrderFlowImbalance | 53.3% | 0.675 (40.5%/60.0%) | Still heavily 0-DTE dependent, confirmed again |

DepthImbalance and BarDurationUrgency having exactly opposite time-of-day biases is the same
pattern found before the gate correction -- the earlier "time-of-day-gated combination" idea
(trade DepthImbalance only in the open, BarDurationUrgency only outside it) is still sitting there
unexplored under the gated rules and is probably worth more than the linear composite below.

**DepthImbalance stop-loss re-test** (650/93, gated, all 7 days): 20% hurts (52.3% win, +66.35);
**30% is best** -- win rate AND net both improve together (56.1% win, +151.50, vs. 53.8%/+138.10
with no stop), the same "both moving together" signal used to adopt a stop before; 40%/50% add
little. **Adopted: 650/93 with a 30% stop-loss** as DepthImbalance's config going forward.

**Composite reweighted** from these gated numbers. Since all 7 days are now used for calibration
(no held-out day for a fresh out-of-sample nudge), the weighting formula was changed to
`(win rate - 40%) x concentration discount x DTE-consistency discount` -- a win-rate-quality
measure, replacing the old out-of-sample nudge, better matching how combos have been chosen
throughout this track (prefer win-rate quality over raw net):

| Metric | Edge = win% - 40% | Concentration discount | DTE consistency | Raw score | New weight (was) |
|---|---|---|---|---|---|
| TopOfBookImbalance | 14.7 | 0.767 | 0.996 | 11.23 | **33.5%** (was 1.5%) |
| BarDurationUrgency | 14.9 | 0.715 | 0.943 | 10.04 | **29.9%** (was 8.8%) |
| DepthImbalance (30% stop) | 16.1 | 0.526 | 0.903 | 7.65 | **22.8%** (was 25.6%) |
| TobDepthDivergence | 5.1 | 0.661 | 0.976 | 3.29 | **9.8%** (was 46.3%) |
| OrderFlowImbalance | 4.2 | 0.467 | 0.675 | 1.32 | **3.9%** (was 17.8%) |

**The reweighted composite is weaker, not better.** Recalibrated over all 7 days: best target-zone
row is 2600/85 (17.0 trades/day, 49.6% win, +80.95 net) -- win rate below 50%, only 4 of 7 days
profitable, and 117% of the total net comes from a single day (09-15). This **underperforms simply
trading DepthImbalance alone** (56.1% win, +151.50 over the same 7 days).

**Read on why**: DepthImbalance and BarDurationUrgency have opposite time-of-day biases (see
phase table above). Averaging their scores together every bar likely partially cancels real signal
rather than combining it -- a linear weighted-average blend is probably the wrong combination
mechanism for metrics that disagree systematically by time of day. The time-of-day-gated ensemble
idea (trade each metric only in its own strong window, never blend their scores) remains the more
promising unexplored path if composite scoring is revisited.

**Decision: composite not adopted. DepthImbalance (650/93, 30% stop) is the strongest standalone
candidate under the real trading rules** -- 56.1% win rate, +151.50 net across all 7 available
days. BarDurationUrgency and TopOfBookImbalance remain solid secondary candidates.
TobDepthDivergence and OrderFlowImbalance remain weak/fragile and are not recommended for live
consideration as currently formulated.

## Session-gated ensemble: a switch, not a blend (2026-09-17)

Follow-up to the composite underperforming DepthImbalance alone. Since DepthImbalance's edge is
almost entirely in the 09:30-10:00 open and BarDurationUrgency's is almost entirely outside it
(see the phase table above), built a hard **switch** instead of a weighted blend:
`VolumeBarMetric.SessionGatedDepthDuration` in `TradeSimulator.cs` uses DepthImbalance's own score
for bars before 10:00 IST and BarDurationUrgency's own score for bars at or after 10:00 IST --
never an average of both on the same bar. Both components are session-rank tracked on every bar
(so each one's running distribution matches what the standalone metric would see over the whole
day), but only one drives the traded score at any given moment. Since each component's own score
is already percentile-shaped by construction (SignedRank), no separate magnitude-rank tracker is
needed here, unlike the composite.

**Calibration sweep, all 7 gated days**: best combo is **2600/90** -- 9.7 trades/day, **57.4% win
rate, +177.25 net** -- the best win rate of any strategy tested in this entire track, and better
net than the composite by more than double.

**Comparison:**

| Strategy | Trades | Win Rate | Net |
|---|---|---|---|
| **SessionGatedDepthDuration (2600/90)** | 68 | **57.4%** | **+177.25** |
| DepthImbalance alone (650/93, 30% stop) | 82 | 56.1% | +151.50 |
| BarDurationUrgency alone (2600/90) | 71 | 54.9% | +119.60 |
| Linear composite (2600/85) | 119 | 49.6% | +80.95 |

The switch beats both components individually and the linear composite -- confirms the hypothesis
that blending two metrics with opposite time-of-day biases cancels real signal, while switching
between them preserves each one's own strength in its own window.

**Concentration check, 2600/90** (68 trades): top single trade is 39.5% of net (better than
DepthImbalance's own 47.4%), 5 of 7 days net positive. **But 97.7% of the entire 7-day profit
comes from a single day** (09-15 -- the same high-volatility 0-DTE day that has dominated several
metrics throughout this track). Excluding that one day, the other 6 days net to roughly +4 points
combined, essentially flat. This is a day-level concentration risk, not a single-trade fluke, and
worth watching as more days accumulate.

**Status: current best candidate**, ahead of every standalone metric and the linear composite on
every measure except day-level concentration. Same standing caveat as the rest of this section --
all 7 available days were used to build and calibrate this, so there is no held-out day left to
validate it against; the next new trading day is the first genuine out-of-sample check for this
specific design.

## Folding TopOfBookImbalance into the session-gated switch (2026-09-17)

Per the volume-bar scoring roadmap, tried adding TopOfBookImbalance (#9) to the session-gated
switch. Its own phase data (see the gated-calibration section above) showed its edge concentrated
specifically in the open (63.6% win rate there vs. much weaker in Mid/Close) -- rather than blend
it in (already shown to risk cancellation) or give it a third switched segment (it doesn't clearly
beat DepthImbalance in the open or BarDurationUrgency in Mid/Close on this data), folded it in as
a **confirmation filter**: during the open only, a new position additionally requires TOB's own
score to agree in sign with DepthImbalance's, or the bar is skipped. `VolumeBarMetric.SessionGatedDepthDurationConfirmed` in `TradeSimulator.cs`.

**First, a sanity check on the switch itself**: split the already-adopted 2600/90 switch's own 68
trades by which sub-metric actually drove each one. Depth/open: 12 trades, 58.3% win, +57.30. Mid
+Close/duration: 56 trades, 57.1% win, +119.95. Both halves perform equally well proportionally --
the open side is smaller by trade COUNT (fewer bars complete within a 30-minute window at a 2600
bar size), not by quality. This is a good sign for the switch design overall, not a red flag.

**TOB-confirmation result**: calibrated at all 7 gated days, the adopted 2600/90 combo comes back
**exactly identical** (68 trades, 57.4% win, +177.25) with or without the confirmation filter.
Traced to why: at the 2600 bar threshold there is only about 1 bar per day inside the 09:30-10:00
open window, so the confirmation gate has at most ~12 opportunities across the whole 7-day window
to reject anything -- and apparently TOB never once disagreed with DepthImbalance's own sign in
those 12 cases. Not a failed idea, just inconclusive at this sample size; costs nothing to leave
in place since it can only filter trades, never add ones that wouldn't otherwise fire. Worth
re-checking once more days accumulate and the sample of open-window trades grows.

**Status: TOB confirmation adopted as a no-cost addition, not yet shown to add value.** The
session-gated switch's own adopted config (2600/90) is unchanged either way.

## Larger bar sizes tested: 3900 and 5200 (2026-09-18)

Before closing out the futures phase, tested whether larger volume bars (3900 = 6x the 650 base
unit, 5200 = 8x) change any metric's verdict. Populated both thresholds across all 7 days, widened
the calibration percentile grid to include 75 (previously started at 80) per explicit request, and
ran all 10 original metrics through the same calibration sweep.

**Important note on bar construction, clarified during this round**: `VolumeBarBuilder` builds
FIXED, sequential, non-overlapping bars (the standard "volume bar" convention) -- not a rolling
window. A 5200-threshold bar waits until 5200 units of volume have traded since the last bar
closed before producing a new reading; during a quiet stretch this can take several minutes, and
there is no intermediate signal in between. This sweep still uses that fixed-bar design --
a live/partial-bar evaluation alternative was discussed but not yet built, and remains an open
design decision (see the "Open items" note below).

**Predicted and confirmed**: VwapDeviation's trade-frequency problem gets *worse*, not better, at
larger bar sizes (max 3.9 trades/day at either 3900 or 5200, still nowhere near the 7-20 target) --
mechanically expected, since bigger bars means fewer bars per day.

**No other metric clearly beats its already-adopted smaller-bar config.** DepthImbalance,
BarDurationUrgency, TopOfBookImbalance, TobDepthDivergence, OrderFlowImbalance, FutureCvdNet,
FutureOiBuildupQuadrant, and PriceImpact all look roughly the same or somewhat weaker at 3900/5200
than at their established thresholds -- a useful negative result, not just an absence of finding.

**TrendReversion looked promising on the surface, but doesn't survive the same scrutiny as
everything else.** At 3900, two adjacent percentiles (75, 80) both showed an unusually stable
~61-62% win rate -- more consistent than its erratic behavior at 650/1300/2600. Checked trade-level
detail at 3900/75 (60 trades, 61.7% win, +72.40 net) before trusting it:
- A single trade is 65% of the total net profit (09-17, +47.05 of +72.40).
- Only 4 of 7 days are net positive.
- Despite the positive win rate and point-net, the average PERCENTAGE return per trade is
  negative (-0.2%) -- several losses were 20-70% of the option's own premium (09-15: -55.2% and
  -69.2%; 09-17: -30.8%) while most winners were small-to-moderate percentage gains. Good-looking
  win rate and net points hiding a poor risk/reward shape.

**Status: TrendReversion remains not confirmed.** The apparent consistency at 3900 doesn't hold up
once checked the same way every other candidate has been -- a reminder that a stable-looking
headline number still needs the full check (concentration, day-breadth, and now also point-vs-
percent-return agreement) before being trusted, not just internal consistency across percentiles.

**Open items:**
- The rolling-window / partial-bar-evaluation design question raised during this session is
  unresolved -- current results (this section and everything before it) all use the fixed
  sequential-bar design. If a live-evaluation variant is built later, this entire track's numbers
  would need re-deriving under that design, not just re-calibrating.
- Bar thresholds beyond 5200 were not tested (not requested).

## Rolling-window bars tried, not adopted (2026-09-18)

Follow-up to the 3900/5200 experiment: the user's own concern was that a large FIXED bar (e.g.
5200) can take 2-5 minutes to accumulate, leaving the simulator (and a live system) blind the
whole time. Proposed fix, clarified precisely: instead of one bar waiting for its full volume to
accumulate from empty, build it from several already-populated 650-sized sub-bars and SLIDE a
window across them -- a 2600-wide window is 4 consecutive 650 sub-bars, refreshed every time one
new sub-bar completes (every ~650 volume) rather than only once all 4 have reaccumulated from
scratch (every ~2600 volume). Built as `RollingVolumeWindowBuilder` (`NiftySignal.VolumeBarData`),
combining already-persisted 650 sub-bars' own stored fields (sum for Volume/CVD/OFI/Duration,
volume-weighted average for the two ratio metrics DepthImbalance/TopOfBookImbalance, min/max for
High/Low, oldest/newest for Open/Close) -- no new ticks re-scanned, no schema change. Wired into
`TradeSimulator.SimulateDayAsync` and the `trade`/`calibrate` CLI commands via an optional
`rollingSubBarThreshold` parameter; omitting it keeps the original fixed-bar behavior unchanged.

**Flagged before testing, confirmed after**: consecutive rolling windows overlap heavily (a
4-wide window shares 75% of its content with the next one), so readings are not independent
samples the way fixed/sequential bars are -- the percentile-ranking entry gate assumes reasonably
independent samples, and this design gives it a much more repetitive, less-diverse distribution to
rank against instead.

**Sanity check** (DepthImbalance, one day, fixed 2600 vs. rolling 2600 from 650 sub-bars):
mechanism works correctly, produces sensible, slightly different output (5 trades vs. 4, first
entry nearly identical, diverging as the finer window slides) -- no bugs, safe to run a real sweep.

**Calibration sweep, all 7 gated days, the 5 actively-considered metrics, rolling 650-wide
sub-bars at logical sizes 1300/2600/3900/5200:**

| Metric | Best rolling result | Established fixed-bar result | Verdict |
|---|---|---|---|
| DepthImbalance | 1300/93: 55.9% win, +56.95 (9.7 trades/day) | 650/93 + 30% stop: 56.1% win, +151.50 | Similar win rate, worse net |
| BarDurationUrgency | best win rate only ~46-48% at any target row | 2600/90: **54.9%** win, +119.60 | **Clearly worse** -- win rate fell meaningfully at every window size tested |
| TopOfBookImbalance | 3900/85: 51.7% win, +122.05 | 2600/80: 54.7% win, +124.60 | Roughly flat, slightly worse |
| TobDepthDivergence | best ~50% win rate | 2600/90: 45.1% win, +129.30 | Still weak either way, no reversal |
| OrderFlowImbalance | win rate stuck at 35-44% throughout | 2600/95: 44.2% win, +289.40 | Flat to slightly worse |

**BarDurationUrgency is the clearest casualty** -- its win rate dropped from a solid ~55% under
fixed bars to the low-to-mid 40s at essentially every rolling window size. Read: the extra update
frequency did not translate into a better signal; the overlapping-window redundancy plausibly
diluted its ability to distinguish a genuinely urgent moment from an ordinary one, since the
percentile gate is now ranking against a much more repetitive "today so far" distribution.

**Decision: rolling-window mode tried, not adopted.** The underlying concern (blind for minutes on
a large fixed bar) is real, but this specific fix costs more in signal quality than it gains in
responsiveness, for every metric and window width tested. The code stays available
(`rollingSubBarThreshold` on `trade`/`calibrate`) for a future attempt with a different design (a
narrower/less-overlapping slide step, for instance) if the blind-window concern needs revisiting
later. The fixed-bar session-gated switch (DepthImbalance in the open, BarDurationUrgency after,
TopOfBookImbalance as an open-window confirmation filter) remains the best futures-side result.

## Correction: VwapDeviation's real verdict (2026-09-18)

Challenged, correctly: earlier sections dismissed VwapDeviation primarily as "never reaches the
7-20 trades/day target band," which is a heuristic this project chose for statistical trust, not
a hard requirement -- the actual simulated results deserved to be shown and judged on their own
terms rather than filtered out by that band first.

**Full sweep, every setting tested, no trade-count filter, across all 7 gated days:**

| Threshold | Percentile range | Trades | Win rate range | Net range |
|---|---|---|---|---|
| 650 | 80-99 | 16-26 | 15.4-25.0% | -23.00 to +73.90 |
| 1300 | 80-99 | 15-25 | 16.0-26.7% | -18.35 to **+82.60** |
| 2600 | 80-99 | 16-24 | 16.7-25.0% | -37.90 to +43.50 |
| 3900 | 75-99 | 14-27 | 17.4-28.6% | -6.25 to **+119.45** |
| 5200 | 75-99 | 14-23 | 17.4-28.6% | -24.20 to +63.20 |

**Win rate never exceeds 28.6% at any bar size or percentile tested** -- not a sample-size
artifact, a consistent structural result. Pulled real trade-level detail on the two best-net rows
(1300/99: 15 trades, 26.7% win, +82.60; 3900/99: 14 trades, 28.6% win, +119.45) to see exactly
what produces a positive number despite that:

Both rows are carried entirely by the same two trades -- an all-day Call hold on 09-11 (+115.40,
+130%) and an all-day Put hold on 09-15 (+235.95, +290%), both entered near market open and held
to the 15:15 forced close. Those two trades alone total more than the ENTIRE reported net in both
rows -- every other trade combined is a net loser (-153.35 across the other 13 trades in the
1300/99 row; -231.75 across the other 12 in the 3900/99 row), with typical losses of -20 to -55
points (20-40% of premium).

**Structural reason, not just bad luck**: VwapDeviation enters rarely and, when it does, tends to
fire near the open and hold to the forced close rather than exiting dynamically when the score
flips the way every other metric in this track does. That makes it behave less like a trading
signal and more like an occasional all-day directional bet -- small losses often, an occasional
large win on whichever day happens to have an outsized move (09-11 and 09-15 are the same two
high-volatility days that have inflated several other metrics' headline numbers throughout this
track).

**Corrected verdict**: not "inconclusive due to low trade count" -- **confirmed weak**. It loses on
roughly 7 of every 10 trades at every setting tested, and its only positive-looking results are
two specific outsized days propping up an otherwise consistently losing strategy. This is a
stronger and more precise disqualification than the original "not enough trades" framing, not a
softer one -- the original framing undersold how weak this metric actually is.

## Out-of-sample validation log

Every result above was calibrated by looking at the same 6 days (2026-09-08 through 2026-09-16).
This section tracks each leading candidate's performance on trading days that came *after* all
calibration decisions were locked in — no re-tuning, exact same (threshold, percentile, stop)
parameters as already adopted. This is the real test of whether any of this generalizes.

### 2026-09-17 (DTE=5, first out-of-sample day)

Data validated before running: 40,454 future ticks, full 09:15-15:30 session, 99.998% depth
coverage, 80-strike option chain, nearest expiry 2026-09-22 (5 DTE, an ordinary non-expiry day).
Volume bars populated at 650/1300/2600, same as every prior day.

| Metric (locked-in config) | Today | Backtest expectation (6-day) |
|---|---|---|
| TobDepthDivergence (2600/95) | 9 trades, 33.3% win, **-62.55 pts** | 60.8% win, +420.65 |
| DepthImbalance (1300/95, 40% stop) | 11 trades, 54.5% win, **+25.70 pts** | 59.3% win, +259.15 |
| OrderFlowImbalance (2600/95) | 8 trades, 50.0% win, -17.35 pts | 50.0% win, +357.45 |
| BarDurationUrgency (2600/90) | 10 trades, 30.0% win, -46.70 pts | 53.3% win, +178.45 |
| TopOfBookImbalance (2600/93) | 10 trades, 80.0% win, +61.15 pts | 48.6% win, +143.70 |

**One day, 8-11 trades per metric — far too small a sample to confirm or reject anything on its
own.** Noted for the record, not as a verdict:

- **TobDepthDivergence had its worst day of anything tested**, well below its backtested win
  rate. This is the metric whose promotion carried the most overfitting risk (its sign convention
  was chosen specifically because it worked on these same 6 days — see its own section above), and
  this is the first real test of that risk on a day it never influenced. One losing day doesn't
  invalidate it (DepthImbalance had a losing day in the original 6-day backtest too), but it is a
  real yellow flag that argues for more out-of-sample days before trusting the "strongest
  candidate" ranking at face value.
- **DepthImbalance came in positive and roughly in line with its backtested win rate** — a small
  but real point of reassurance for the metric that didn't need a sign convention chosen after the
  fact.
- TopOfBookImbalance's unusually good day (80.0% win) is consistent with it already being the
  noisiest/least-confirmed metric of the group — a good single day doesn't change its "not
  confirmed pooled" status any more than one bad day changes TobDepthDivergence's.

**Next step**: keep running every future trading day through this same table as new ticks sync in,
without re-tuning any parameter, so the candidate ranking is eventually judged on data none of it
was fit to.

## Summary and next steps

All 8 originally planned future-side candidates have now been through the same evaluation cycle
(calibration sweep + full trade-level detail + concentration check):

| Metric | Verdict | Best combo | Net (6 days) | Win Rate | Top-trade share |
|---|---|---|---|---|---|
| TobDepthDivergence | **New strongest candidate — best net, lowest concentration, robust on every check** | 2600/95 | +420.65 | 60.8% | 16.1% |
| DepthImbalance | Strong, second — holds up 0-DTE and non-0-DTE | 1300/95 | +242.45 | 58.0% | 34.6% |
| BarDurationUrgency | Second-tier watch — headline leans on 0-DTE variance | 2600/90 | +178.45 | 53.3% | 19.1% |
| OrderFlowImbalance | Second-tier watch — promising, concentrated | 2600/95 | +357.45 | 50.0% | 43.1% |
| TrendReversion | Not confirmed | 650/93 | +68.75 | 55.7% | 144.7% |
| FutureOiBuildupQuadrant | Not confirmed | 1300/85 | +133.05 | 45.5% | 61.8% |
| PriceImpact | Not confirmed | 2600/97 | +80.60 | 45.2% | 63.9% |
| FutureCvdNet | Not confirmed / closed | 650/97 | +59.60 | 48.4% | 98.4% |
| VwapDeviation | Not viable as built | 650/99 (below target freq.) | +312.05 | 36.8% | 123.3% |
| TopOfBookImbalance | Not confirmed pooled; watch on non-0-DTE only | 2600/93 | +143.70 | 48.6% | 87.7% (top 2 trades) |

**Open items:**
- All eight metrics need more trading days before any verdict is durable — 6 days is an early
  read, not a conclusion (same discipline as the time-cadence track).
- DepthImbalance and BarDurationUrgency are the two worth prioritizing for continued data
  accumulation; OrderFlowImbalance stays a second-tier watch item (strong net, but concentrated).
  The other five need either a reformulation or more evidence before further investment, same
  evaluation-cycle discipline used on the time-cadence side.
- VwapDeviation specifically needs a design change (shorter VWAP window, e.g. reset per N bars
  instead of whole-session) before it can be re-evaluated at all — it never produced enough trade
  frequency to judge at the current whole-session-VWAP design.
- Before combining any of these into a composite, a pairwise redundancy check (same method already
  used on the time-cadence side in `SCORE_CANDIDATES.md`) is needed — BarDurationUrgency and
  PriceImpact both share TrendReversion's price-direction sign by construction, so their
  correlation with it (and with each other) should be checked before assuming they'd each
  contribute independent weight. (DepthImbalance vs. BarDurationUrgency is now done — see above,
  not redundant.)
- **Time-of-day-gated combination, flagged not built**: DepthImbalance's edge is concentrated in
  the opening 45 minutes; BarDurationUrgency's is concentrated in mid/close. A rule that trades
  DepthImbalance only in the open and BarDurationUrgency only outside it is a natural next
  experiment once/if a combined-metric trading harness exists — currently `TradeSimulator` only
  runs one metric at a time.
- Option-chain-side metrics (IV skew, PCR, OI-quadrant, etc.) on the volume-bar clock are still
  unstarted — future-side metrics were deliberately done first per the original plan.
- Risk management: a soft stop-loss overlay has now been tested on the two survivors (see above) —
  a 40% premium stop adopted for DepthImbalance, BarDurationUrgency left unprotected pending more
  data. A take-profit/target overlay has not been tested yet and remains a separate follow-up.
- Paper-trading against option price (rather than notional future points) is already how this
  pipeline is built (`OptionPriceSeries`, real ATM fills) — the same discipline the user asked for
  up front.

## Options phase kickoff (2026-09-18)

With the futures-side switch (DepthImbalance pre-10:00, BarDurationUrgency after, TopOfBookImbalance
as an open-window confirmation filter) frozen as the interim FuturesScore, the options phase began.
A 5-phase plan (drafted externally, validated and amended here) governs it: Phase 0 preparation,
Phase 1 core options metrics (5 candidates), Phase 2 depth metrics, Phase 3 survivors-only checks,
Phase 4 first OptionsScore, Phase 5 final FuturesScore + OptionsScore composite.

**Two design decisions locked before any metric work started:**
- **Clock**: no new bar engine for options. Every option-chain metric reads the ATM±1 complex's
  state "as of" each *future* bar's close — the future bar clock (already built, already validated)
  is reused as-is, not reinvented per option.
- **Data coverage**: validated before trusting any option-side number, same discipline as the
  future-side tick coverage checks earlier in this doc.

**Synthetic forward is mandatory, not optional.** IV/Greeks solving on weekly options uses
`SyntheticForward.Compute` (put-call parity: S = K·e^(-rT) + (C−P)) as the underlying, never the raw
future price directly — using the raw future was a previously-fixed bug in this codebase (biased put
IV ~7-8 vol points below call IV at every strike). Re-confirmed here: on 2026-09-15@650 the
synthetic-forward-based ATM Call/Put IV gap averaged 0.43pp, versus the multi-point gap the old bug
produced.

**Two price conventions, used deliberately for two different purposes:**
- Mid price (bid1/ask1 average, LastPrice fallback) — for IV/Greeks solving, which needs a quoted
  mid, not a stale trade print (`OptionQuoteSeries`, new).
- LastPrice-primary notional (`Qty × LastTradedPrice`) — for notional volume flow, which needs the
  actual traded price (reuses `FutureFlowAccumulator`'s cumulative-volume-diffing per option
  instrument).

**ATM±1 band definition differs by metric, deliberately:**
- Single-strike IV metrics: ATM = nearest strike to the **synthetic forward** (bias-correct).
- Band-aggregation metrics (volume/OI): ATM = nearest strike to the **future's own close price**,
  per the plan's own Phase 0 definition — not an inconsistency, a different deliberate choice for a
  different question ("what's priced in" vs. "where is flow actually happening").

**New infrastructure** (`NiftySignal.VolumeBarData`, mirrors the future-side pattern):
- `OptionAtmBarRow` / `OptionAtmPopulator` — one row per future bar: synthetic forward (5 nearest
  strikes), ATM strike, Call/Put/blended IV via `NiftySignal.Pricing.ImpliedVolatilitySolver`
  (Newton-Raphson + bisection fallback). Validated 2026-09-15@650: 2545 rows (exact match to future
  bar count), 0 nulls, IV range 0.146–1.15 (avg 0.39).
- `OptionBandFlowBarRow` / `OptionBandFlowPopulator` — one row per future bar: Call/Put notional
  volume summed over the 3 strikes (ATM±1) nearest that bar's future close, tick-level flow
  accumulation merged against the future's already-fixed bar boundaries. Validated 2026-09-15@650:
  2545 rows, only 12 both-zero bars, total call notional ~120.3B vs. put ~200.2B, no negatives.

## Phase 1, metric 1: ATM IV Change (2026-09-18)

Three variants tried with genuinely different formulations, each carried to an explicit finalized
verdict (per-day recalibration excluding 0-DTE where flagged):

| Variant | Formula | Verdict | Best combo | Win Rate | Net | Concentration |
|---|---|---|---|---|---|---|
| Raw ΔIV | `ΔIV` (this bar's ATM IV − previous bar's) | **CONFIRMED, DTE-gated** (0-DTE excluded) | 1300/97 | 80.2% | +77.25 (5 days) | 32.8% |
| Price-signed ΔIV | `-sign(ΔFuturePrice) × ΔIV` (sign flipped after empirical rejection of the un-negated version) | **CONFIRMED, no gate needed** | 1300/97 | 56.8% | +153.85 (7 days) | 22.4% (lowest seen on either side of the project) |
| IV Acceleration | `ΔIV(t) − ΔIV(t-1)` | **NOT CONFIRMED** | — | — | — | inconsistent DTE behaviour, opposite pattern from raw ΔIV, no clean win-rate/net combination at any threshold/percentile |

**Sign-flip note on price-signed ΔIV**: the un-negated formula (`sign(ΔPrice) × ΔIV`) showed win
rate never exceeding 50.2% across the full 24-cell grid with deeply negative net in most cells — the
same rejection signature seen with TobDepthDivergence on the futures side. Flipped to
`-sign(ΔPrice) × ΔIV` and re-ran the **full** sweep (not inferred from the rejected version); all 24
cells flipped to positive net, confirming the flip was correct. Economically: a price move
*without* a proportional IV move in the same direction (i.e., IV falling as price rises, or IV
rising as price falls less than expected) is the informative signal, not simple co-movement.

**Data window**: 2026-09-08 through 2026-09-17 (7 days, unlike the future-side 6-day window — one
extra day of option-chain coverage was validated in the interim).

## Phase 1, metric 2: Notional Call-Put Volume Delta (2026-09-18)

Infrastructure built and validated (see above). **CONFIRMED, DTE-gated (0-DTE excluded)**, after
two iterations with genuinely different signs and an explicit DTE check.

**Iteration 1 — original hypothesis, `CallNotional − PutNotional` (more Call flow reads bullish),
all 7 days, un-gated:** weak, no clean pattern. Win rate hovered at/below 50% in nearly every cell
(best in-target combo: 1300/80, 51.1% win, +133.10 net) — the same rejection signature seen with
TobDepthDivergence and price-signed ATM IV change before their own flips.

**Iteration 2 — sign flipped, `PutNotional − CallNotional`, same 7 days, un-gated:** clean,
monotonic pattern this time — win rate and net both climb steadily as the percentile tightens,
across all three bar sizes (e.g. 650: 52.3% win at 80th → 61.8% at 95th; net -185.70 → +81.25).
Empirical, not assumed — flipped because the un-negated version failed the sweep, same discipline
as the two prior flips in this project. No causal story assumed for *why* Put-heavier flow reads
bullish; it is reported as an empirical finding, not explained.

**DTE check on the flipped version (650/95, all 7 days):** the pattern split cleanly by expiry.

| Bucket | Days | Trades | Win Rate | Net (pts) |
|---|---|---|---|---|
| 0-DTE | 09-08, 09-15 | 2 | **0.0%** | **-49.95** |
| Non-0-DTE | 09-09/10/11/16/17 | 53 | 64.2% | +131.20 |

Both 0-DTE days were single-trade, 100%-loss days (one rode a ₹17.35 entry down to ₹2.65 by
time-cutoff, -84.7%; the other similarly). Consistent with the small-sample-size, high-variance
0-DTE behaviour already documented above for the future-side metrics — recalibrated excluding
0-DTE to find the real optimum, same as Phase 1 metric 1's raw ΔIV variant.

**Re-calibrated sweep, 0-DTE excluded (2026-09-09 through 2026-09-17, 5 days):**

| BarThreshold | Percentile | Trades/Day | Win Rate | Net (5 days) |
|---|---|---|---|---|
| 650 | 95 | 10.6 | 64.2% | +131.20 |
| 1300 | 95 | 8.0 | **70.0%** | +130.65 |
| 2600 | 95 | 7.4 | 67.6% | +163.90 |

All three bar sizes converge on percentile 95 with a clean win rate in the mid-60s to 70%.
**Best combo: 1300/95** — 40 trades, 70.0% win rate, +130.65 net, **20.2% top-trade concentration**
(top trade 26.45 / 130.65 net) — the lowest concentration of any metric in the options phase so far,
comparable to price-signed ATM IV change's 22.4%.

**Verdict: CONFIRMED, DTE-gated (0-DTE excluded).** Best combo 1300/95.

## Phase 1, metric 3: Notional Call-Put OI Delta + Buildup Quadrant (2026-09-18)

Two sub-variants from the same new `OptionOiBarRow`/`OptionOiPopulator` infrastructure (ATM±1 band,
same as metric 2), one **CONFIRMED**, one **NOT CONFIRMED**.

**Design correction caught before calibration was trusted**: the first build persisted
`CallNotionalOi`/`PutNotionalOi` as LEVELS (`OI × mid price` at each bar's close) and differenced
them in `TradeSimulator`, mirroring metric 1's ΔIV pattern. Sanity calibration immediately showed
100+ trades/day (vs. a 7-20 target) with rapid Call/Put flip-flopping within seconds — the signature
of price noise, not real OI flow. Root cause: differencing a re-priced level conflates real OI
change with the option's own (far noisier) mid-price movement between bars
(`Δ(OI·Price) = OI·ΔPrice + Price·ΔOI + ...`, dominated by the price term since mid-price moves on
nearly every bar while OI itself, per the raw feed, only updates roughly once a minute — confirmed
directly: one ATM call on 09-15 showed 375 distinct OI readings across 68,027 ticks that day).
**Fixed** by computing the flow directly from the discrete per-strike ΔOI at the moment it's
observed, weighted by the price at that moment (`Σ ΔOI × price_then`), the same shape
`BuildupNet` (below) already used correctly — never differencing a level. Migration and all 21
day/threshold combos were regenerated after the fix; nothing from the flawed version was calibrated
to a verdict.

**Sub-variant A — Notional OI Delta** (`CallOiChangeNotional − PutOiChangeNotional`, a genuine flow):
clean pattern immediately, no sign flip needed — win rate climbs from ~60% at the 75th percentile to
80%+ by the 93rd, positive net in nearly every cell, all three bar sizes.

| Bucket | Days | Trades | Win Rate | Net (pts) |
|---|---|---|---|---|
| 0-DTE | 09-08, 09-15 | 20 | 50.0% | -18.60 |
| Non-0-DTE | 09-09/10/11/16/17 | 43 | 65.1% | +152.00 |

Same 0-DTE-weaker pattern as metric 1's raw ΔIV and metric 2. Re-calibrated excluding 0-DTE (5
days): best combo **650/80 — 65.1% win, +152.00 net, 43 trades, 8.6 trades/day, 20.0% top-trade
concentration** (30.40/152.00). 1300/80 and 2600/80 show the same quality, all three thresholds
converge cleanly.

**Verdict: CONFIRMED, DTE-gated (0-DTE excluded).** Best combo 650/80.

**Sub-variant B — Buildup Quadrant** (`BuildupNet`, reusing `OiBuildupClassifier` on each option's
own bar-over-bar price/OI change, Put-side sign-flipped, same convention as the original
time-cadence `OiBuildupNet` candidate): weak as built — win rate 34-61% with no clean pattern
across any bar size. Flipped sign, re-ran the full sweep: still weak (39-56%, no clean pattern
either direction). **Both directions tested, neither confirms.**

This replicates rather than contradicts the existing time-cadence finding: `OiBuildupNet` was
**CLOSED, no edge found** there too, across four separate formulations (see `SCORE_CANDIDATES.md`).
Tried again here anyway per the "a metric closed on one clock isn't automatically excluded on
another" convention (genuinely different clock — future volume bars vs. 15s cadence — and a
narrower ATM±1 band vs. ATM±2) rather than assumed dead on arrival. Same conclusion both times.

**Verdict: NOT CONFIRMED.**

**Metric 3 overall**: one of two sub-variants confirmed (Notional OI Delta), matching the pattern
already seen in metric 1 (2 of 3 variants confirmed). Buildup-quadrant-style classification now has
two independent negative results (time-cadence and volume-bar clock) — worth treating as a closed
line of investigation unless a materially different formulation surfaces later.

## Phase 1, metric 4: 25-Delta Skew Change (2026-09-18)

New `OptionSkew25DeltaBarRow`/`OptionSkew25DeltaPopulator`, reusing the time-cadence pipeline's own
already-**CONFIRMED** `IvSkew25Delta` candidate exactly (`docs/SCORE_CANDIDATES.md`): the single
nearest-to-25-delta strike per side (using each strike's own individually-solved delta via
`BlackScholes`, no bracket interpolation), `SkewRatio = putIv / callIv`, same orientation. Band is
wider than metrics 2/3's ATM±1 (a 25-delta strike is meaningfully OTM by construction) — 21 strikes
(ATM±10) nearest the future's own close, matching that candidate's own "full chain" scope. ~42 IV
solves/bar; populated in ~20s/day at the densest threshold, no performance concern.

Three variants, mirroring metric 1's pattern:

**Sub-variant A — raw ΔSkewRatio**: loose percentiles (75-90th) show huge trade counts (up to
159/day) with near-50% win rates — the nearest-25-delta strike hops between adjacent strikes as the
forward drifts, and small hops add quantization jitter on top of genuine skew movement. Unlike the
metric 3 OI-notional bug, this isn't a structural error — it's real noise density, and win rate
climbs cleanly and monotonically as the percentile gate tightens (650: 48.7%→73.3% from 75th to
99th; same shape at 1300/2600), meaning the gate itself filters the jitter out. Best in-target
combo: **1300/97 — 68.9% win, +112.25 net, 103 trades, 14.7 trades/day**. DTE split showed no real
gate needed this time (0-DTE 70.5% win vs. non-0-DTE 67.8% — comparable, unlike every other metric
in this phase so far). **Concentration is real**: top 2 trades are 55.1% of total net (33.55 +
28.25, both from 09-16) — the highest concentration seen in the options phase, worth treating as a
caveat, not disqualifying on its own (comparable tier to futures-side OrderFlowImbalance's own
43.1%, itself only a "second-tier watch").

**Verdict: CONFIRMED, no DTE gate, but flagged for high concentration and reliance on a tight
(97th+) percentile gate to separate signal from strike-hop noise** — weaker/more fragile than
metrics 1-3's cleaner survivors, worth extra out-of-sample scrutiny before trusting at face value.

**Sub-variant B — price-signed ΔSkewRatio**: built un-negated first — weak, win rate 41-53% with no
clean percentile pattern (several cells got *worse* as the gate tightened). Flipped sign, re-ran the
full sweep: still weak (44-57%, still no clean pattern, mostly negative net at loose percentiles).
**Both directions tested, neither confirms.**

**Verdict: NOT CONFIRMED.**

**Sub-variant C — level (`log(SkewRatio)`)**: tried because the time-cadence `IvSkew25Delta`
candidate's own notes explicitly flagged the level as carrying the real signal, with its own
bar-to-bar change weaker there. On this clock it's not viable as built at all — only ~8 trades
total across the entire 7-day window at *every* percentile setting tested (~1.1/day, essentially
flat regardless of gate tightness). Root cause: `log(SkewRatio)` is a slow-moving level that sits
pegged near one session-rank extreme for most of the day, so it rarely crosses back to trigger a
`ScoreInvalidated` exit under this project's hysteresis-based entry/exit rule — the same
trading-rule mismatch `VwapDeviation` hit on the futures side (not a metric failure, a rule-shape
mismatch; a different entry/exit harness built for slow levels might do better, not attempted here).

**Verdict: NOT VIABLE AS BUILT** (same tier as VwapDeviation — needs a different trading rule
before it can be judged at all, not confirmed or rejected on the merits).

**Metric 4 overall**: one of three sub-variants confirmed (raw ΔSkewRatio), but it's the first
options-phase survivor with a real concentration flag and dependence on an unusually tight gate —
weaker than metrics 1-3's confirms. Worth more out-of-sample days before trusting it at the same
level as the others.

## Phase 1, metric 5: Distance to Max-Pain/Highest-OI Strike (2026-09-18) — last of Phase 1's 5 core metrics

New `OptionMaxPainBarRow`/`OptionMaxPainPopulator` — the first options-phase table to deliberately
span the FULL listed chain rather than a windowed ATM-relative band (both max pain and "highest OI"
are magnitude-driven concepts by construction; a deep OTM strike with outsized OI can still set
either one). Two reference strikes computed per bar: true max pain (`argmin_K` of total option-buyer
payout across every strike, standard formula) and the single highest-combined-OI strike (cheaper
proxy). Populated in ~10s/day, no performance concern (no IV solving needed here, just OI lookups).
Sanity check on 09-15@650: max pain strike drifted across 8 distinct strikes through the day,
highest-OI strike sat mostly on round numbers (23100/200/300/400/500 — a well-known real pattern),
distance-to-max-pain ranged -70 to +179 points.

**Sub-variant A — Distance to Max Pain** (`currentPrice − maxPainStrike`): built un-negated first
(hypothesis: price above max pain gets pulled down, i.e. positive distance reads bearish) — strong,
clean REJECTION: win rate 20-33%, monotonically getting *worse* as the gate tightened, down to 0%
at the 99th percentile on all three bar sizes — the clearest wrong-sign signature in this whole
track. Flipped sign, re-ran the full sweep: **win rate 60-85%, positive net in every single one of
the 24 cells tested** — the cleanest win-rate pattern found anywhere in the options phase, no
exceptions. But trade frequency never reaches the 7-20/day target at any setting — even the
loosest gate (75th percentile) produces only ~2.1 trades/day; true max-pain "pinning" shifts are
inherently rare events (the reference strike itself only moved 8 times across the whole day),
which real-world intuition supports (pinning effects are mainly an expiry-week phenomenon, not a
continuous one).

**Verdict: directionally CONFIRMED (real, strong, exceptionally clean signal) but NOT VIABLE AS A
STANDALONE TRADED METRIC** under this project's 7-20/day harness — same "real edge, wrong
frequency" category as `VwapDeviation` and metric 4's Skew25DeltaLevel, but with much stronger
evidence of a genuine underlying signal (unlike those two, this one showed clear percentile
sensitivity and a spotless win-rate table, not just a flat trade count). **Flagged as a strong
candidate for a Phase 4 CONFIRMATION FILTER** (rare-but-high-conviction gate on other signals,
the same role TopOfBookImbalance already plays on the futures side) rather than a primary driver.

**Sub-variant B — Distance to Highest-OI Strike**: same design, cheaper reference strike. Built
un-negated first — weak (14-43% win, degrading to 0% at the tightest gates). Flipped: modestly
better (50-80% win, mostly small positive net) but only ~7 trades total across the ENTIRE 7-day
window at every setting (~1/day, barely changing with percentile) — too sparse to judge on the
merits either way.

**Verdict: NOT VIABLE AS BUILT** (same "too sparse to judge" tier as Skew25DeltaLevel) — the
cheaper highest-OI proxy does not show the same strength as true max pain; the extra payout-
minimization computation appears to matter, not just be a refinement.

**Metric 5 overall, and Phase 1 close-out**: the last of the plan's 5 core options metrics is done.
No standalone-tradeable survivor from metric 5 (both sub-variants too sparse to trade directly),
but max pain distance is flagged as a promising confirmation-filter candidate for Phase 4, and its
clean win-rate pattern is itself informative — pinning is real in this data, just not continuously
tradeable at this cadence.

### Phase 1 summary — all 5 core metrics, final status

| Metric | Confirmed sub-variant(s) | Not confirmed / not viable |
|---|---|---|
| 1. ATM IV Change | Raw ΔIV (DTE-gated); Price-signed ΔIV (no gate) | IV Acceleration |
| 2. Notional Call-Put Volume Delta | Sign-flipped Put−Call (DTE-gated) | — (single formulation, confirmed) |
| 3. Notional Call-Put OI Delta + Buildup Quadrant | Notional OI Delta (DTE-gated) | Buildup Quadrant (both signs) |
| 4. 25-Delta Skew Change | Raw ΔSkewRatio (concentration-flagged) | Price-signed ΔSkewRatio; Level |
| 5. Distance to Max-Pain/Highest-OI Strike | — (not standalone-tradeable) | Both, but Max Pain flagged as a Phase 4 confirmation-filter candidate |

**6 confirmed standalone sub-variants** across 4 of 5 metrics, plus one strong confirmation-filter
candidate (max-pain distance) held in reserve for Phase 4. Every confirmed sub-variant needed at
least one empirical sign-flip except metric 2's OI-delta cousin; three formulations were rejected
outright after testing both signs (IV Acceleration, Buildup Quadrant, price-signed Skew Ratio); two
were structurally not viable under this project's hysteresis-based trading rule regardless of sign
(Skew25DeltaLevel, Distance to Highest-OI Strike). One real design bug was caught and fixed before
it reached a verdict (metric 3's OI-notional level-differencing confound).

**Next**: per the locked plan, Phase 2 (option-chain depth metrics: ATM Complex Notional Depth
Imbalance, TobDepthDivergence on the ATM Complex, Call vs Put Depth Imbalance) — only after this
Phase 1 summary is reviewed, per the plan's own "only after Phase 1 reviewed" gate.

## Playbook-compliance review (2026-09-18) — checked Phase 1 against `METRIC_EVALUATION_PLAYBOOK.md`

Before starting Phase 2, audited the 5 completed options metrics against the reusable checklist
written after the futures-phase trading-hours-gate mistake (`docs/METRIC_EVALUATION_PLAYBOOK.md`,
steps 0-10). Result: mostly followed, two real gaps found, both left open deliberately (user
decided Phase 2 and this follow-up work both start in a later session, not immediately).

**Step 0 (confirm the simulator's rules actually match) — VERIFIED, not a gap.** Re-read
`TradeSimulator.cs`'s actual entry/exit code rather than assuming the futures-phase trading-hours
fix (`EntryWindowStart`/`EntryWindowEnd`/`ForceCloseAt`, `TradeSimulator.cs:342-344`) carried over
to the new options-metric dispatch branches. It does: the entry-window check at
`TradeSimulator.cs:698-699` and the force-close check at `TradeSimulator.cs:686` both live in the
shared trade-management loop, downstream of every metric's `score` computation — every options
dispatch branch (`isAtmIvMetric`, `NotionalCallPutVolumeDelta`, `isOiMetric`, `isSkew25DeltaMetric`,
`isMaxPainMetric`) only ever sets `score`; none of them can bypass the shared gate. Confirmed in
code, not assumed — matches the playbook's own explicit instruction for this step.

**Step 5 (session-phase / time-of-day split) — GAP, not yet done for any options metric.** This is
the check the playbook itself flags as having mattered most on the futures side (it's how
DepthImbalance's and BarDurationUrgency's opposite time-of-day biases were found, which is what
made the session-gated switch work). Every options verdict so far (all 5 metrics, all sub-variants)
rests on pooled and DTE-split numbers only — no candidate has had its trades bucketed by entry-time
phase (Open 09:30-10:00 / Mid 10:00-13:30 / Close 13:30-15:15) the way every futures survivor was.
**Deliberately left undone for now** — queued as the first thing to run when this work resumes,
before Phase 2 starts, on the 6 confirmed sub-variants (1a, 1b, 2, 3a, 4a) plus the max-pain
confirmation-filter candidate (5a).

**Step 10 (documentation currency) — GAP.** `VOLUME_BAR_METRICS_GUIDE.md` (the plain-language
companion to this log) is still 10-metrics-futures-only; it has not been extended to cover any of
the options-phase work despite Phase 1 now being fully closed out with 6 confirmed sub-variants.
**Deliberately left undone for now** — the technical log above (this file) is current and
authoritative in the meantime; the plain-language rewrite is queued for when this work resumes.

**Confirmed NOT gaps, correctly deferred by the plan's own phase structure**: redundancy/
correlation checks (step 6) and stop-loss/risk-overlay testing (step 7) are explicitly Phase 3
work ("survivors only," per the locked plan); combination-mechanism testing — blend vs. switch vs.
confirmation-filter (step 8) — is Phase 4. Bar-construction experiments (larger thresholds, rolling
windows, step 1b) don't need re-testing on the options side since every options metric rides the
same shared future-bar clock already tested there — that finding transfers directly, not a new
question per option metric.

## Robustness checks, run out of order at user request (2026-09-18) — steps 5, 6, 7 on the 4 confirmed metrics

The user asked for correlation (step 6), session-phase split (step 5), and a soft-stop sweep (step
7) on the 4 confirmed standalone survivors before Phase 2/3 formally start — same checks used on
the futures side, applied here ahead of their normal place in the phase sequence. The 4 metrics:
`AtmIvChangePriceSigned` (1300/97), `NotionalCallPutVolumeDelta` (1300/95), `NotionalOiDelta`
(650/80), `Skew25DeltaChangeRaw` (1300/97) — metric 1's raw ΔIV variant is represented by its
cleaner, no-gate price-signed cousin per the user's own framing of "four confirmed metrics."

### Data-completeness catch, found and fixed before trusting any of this

Building the correlation tool surfaced a real gap: `OptionBandFlowBars` (metric 2's underlying
table) had never been populated for **2026-09-15 at the 1300 and 2600 thresholds** — only the
original 650-threshold sanity-check day had it. The initial "populate remaining 6 days x 3
thresholds" background job (see metric 2's own section above) silently treated 09-15 as already
done because it existed at 650, and never filled in the other two thresholds for that specific day.
This meant every prior 1300/2600-threshold result for metric 2 was silently missing one full
trading day's contribution. **Fixed**: re-ran every populate command for all 5 options tables x 3
thresholds x 7 days (idempotent, safe to re-run) — confirmed exactly one other gap (`OptionBandFlowBars`
09-15@2600, 991 rows), backfilled both, then re-verified a second full pass showed zero remaining
gaps anywhere.

**Impact, checked directly**: only metric 2's *pooled all-7-days* auxiliary numbers move (41→42
trades, net 115.75→78.75 at 1300/95 pooled) — its **finalized DTE-gated verdict is unaffected**,
since 09-15 is one of the two 0-DTE days already excluded from that number (70.0% win, +130.65 net,
40 trades stands as reported). The other 3 metrics' totals were cross-checked against this fix and
came back byte-for-byte identical to their previously reported numbers (their underlying tables
were never missing 09-15 data at any threshold) — only metric 2 was affected. Lesson: an idempotent
populate command reporting "AlreadyPopulated" checks existence at the SPECIFIC (date, threshold)
key — always verify a background population job actually iterated every combination it claimed to,
not just that it exited 0, before trusting a sweep built on top of it.

### Correlation (step 6) — level-vs-level and per-day, same method as PCR-OI/OI-diff on the futures side

Computed each metric's own raw (pre-`SignedRank`) per-bar value at a common 1300 threshold (the one
3 of 4 metrics already use), pooled across all 8,899 bars where all four had a value, plus a
per-day breakdown to check for pooled-correlation dilution (the same check that caught PCR-OI's own
misleading pooled number on the time-cadence side).

| Pair | Pooled r | Per-day range | Interpretation |
|---|---|---|---|
| Price-signed IV Δ vs. Volume Delta | -0.099 | -0.37 to +0.11 | Near-independent, sign inconsistent day to day |
| Price-signed IV Δ vs. OI Delta | +0.017 | -0.03 to +0.07 | Fully independent |
| Price-signed IV Δ vs. Skew Δ | -0.096 | -0.22 to +0.05 | Near-independent |
| **Volume Delta vs. OI Delta** | **+0.239** | **+0.14 to +0.33, positive every single day** | **Related but not redundant** — same tier as the futures-side PCR-OI/OI-diff pair |
| Volume Delta vs. Skew Δ | -0.009 | -0.16 to +0.08 | Fully independent |
| OI Delta vs. Skew Δ | -0.026 | -0.09 to +0.02 | Fully independent |

Using the same interpretation scale established on the futures side (±0.8+ = same signal twice;
0.15-0.43 = related but distinct, keep both; near 0 = independent): **5 of 6 pairs are fully
independent** — good news for a future weighted combination, no double-counting risk. **Volume
Delta and OI Delta are the one related pair** (+0.239 pooled, positive on all 7 days with no sign
flips — a cleaner, more consistent relationship than PCR-OI/OI-diff's own 0.15-0.43 range was).
Makes economic sense: both are notional-flow reads off the same ATM±1 options activity (traded
volume vs. building open interest), so some overlap is expected. Not redundant enough to drop
either outright, but worth remembering when weighting a combination later (Phase 4) so this pair
isn't implicitly double-counted the way a naive equal-weight blend would.

### Session-phase split (step 5) — Open 09:30-10:00 / Mid 10:00-13:30 / Close 13:30-15:15

| Metric | Open | Mid | Close |
|---|---|---|---|
| Price-signed IV Δ | 7 trades, 42.9% win, **-31.85 net** | 32 trades, 65.6% win, **+160.75 net** | 42 trades, 52.4% win, +24.95 net |
| Volume Delta | 10 trades, 50.0% win, **-19.15 net** | 26 trades, 76.9% win, **+120.70 net** | 6 trades, 50.0% win, -22.80 net |
| OI Delta | **16 trades, 81.2% win, +84.35 net** | 35 trades, 54.3% win, +35.00 net | 12 trades, 50.0% win, +14.05 net |
| Skew Change (raw) | 11 trades, 72.7% win, +13.45 net | 59 trades, 66.1% win, +92.40 net | 33 trades, 72.7% win, +6.40 net |

**A real, actionable pattern, same shape as the futures-side DepthImbalance/BarDurationUrgency
split**: Price-signed IV Δ and Volume Delta both **lose money in the Open** and make virtually all
their profit in the Mid window. **OI Delta is the mirror image** — its best window by far is the
Open (81.2% win, 5.27 pts/trade — the single best per-trade figure of any bucket in this table),
and it fades through Mid/Close. Skew Change is the outlier: reasonably consistent across all three
phases (66-73% win throughout), closer to the futures side's TopOfBookImbalance in character (not
the biggest edge, but the most session-independent one).

**Implication for Phase 4**: OI Delta looks like a strong candidate to drive (or gate) an
Open-window options score, with one of the Mid-strong metrics (Price-signed IV Δ or Volume Delta)
taking over afterward — the same session-gated-switch shape that worked on the futures side,
not yet built here, flagged for Phase 4.

### Soft-stop sweep (step 7) — 20/30/40/50%, adopt only when win rate AND net both improve together

| Metric | Baseline | 20% | 30% | 40% | 50% | Verdict |
|---|---|---|---|---|---|---|
| Price-signed IV Δ | 56.8% / 153.85 | 56.3% / 194.30 | 56.0% / 190.70 | 56.6% / 195.00 | **57.3% / 188.95** | **Adopt 50%** — only level where both improve together |
| Volume Delta | 66.7% / 78.75 | 50.0% / 19.90 | 52.7% / -16.50 | 58.0% / 14.85 | 60.4% / 20.35 | **No stop** — every level makes both worse; baseline wins outright |
| OI Delta | 60.3% / 133.40 | 54.2% / 118.40 | 57.5% / 110.15 | 58.8% / 95.35 | 59.1% / 124.65 | **No stop** — every level makes both worse; baseline wins outright |
| Skew Change (raw) | 68.9% / 112.25 | **71.4% / 132.40** | **71.7% / 160.70** | 70.5% / 114.60 | 70.5% / 110.40 | **Adopt 30%** — best win rate AND best net of any level tested |

**2 of 4 genuinely benefit from a stop, 2 don't — and the ones that don't are hurt badly by one.**
Volume Delta and OI Delta both get *meaningfully worse* on both dimensions at every stop level
tested (Volume Delta's net even goes negative at 30%) — their large losing trades are apparently
not true tail risk but drawdowns that go on to recover, so cutting them early removes profit
without reducing loss frequency. Price-signed IV Δ (50%) and Skew Change (30%) both show the
"loose stop clips only real tail risk" pattern the futures side found useful for DepthImbalance.
**Skew Change + 30% stop is now the strongest single confirmed options metric by both win rate and
net** of everything tested in Phase 1.
