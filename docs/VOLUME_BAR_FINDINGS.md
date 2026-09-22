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

### 2026-09-18 (DTE=4, second out-of-sample day — first day that includes the options-phase metrics)

Data validated before running: nearest expiry 2026-09-22 (4 DTE, ordinary non-expiry day, same
expiry week as 09-16/09-17). Volume bars populated fresh at 650/1300/2600 (757/1249/431 bars
respectively), all 5 options tables populated clean at all 3 thresholds, no gaps. This is the first
out-of-sample test of the LOCKED futures score (the session-gated switch, not its individual
components) and the first out-of-sample test of any options-phase metric at all — every number
below is on data none of Phase 1's calibration ever saw.

| Metric (locked-in config) | Today | Backtest expectation |
|---|---|---|
| Futures: session-gated switch, TOB-confirmed (2600/90) | 12 trades, 41.7% win, **-32.30 pts** | 57.4% win, +177.25 (7-day pooled) |
| Options 1a: Raw ΔIV (1300/97, DTE-gated) | 4 trades, 75.0% win, **-19.00 pts** | 80.2% win, +77.25 (5-day gated) |
| Options 1b: Price-signed ΔIV (1300/97, +50% stop) | 2 trades, 50.0% win, +20.65 pts | 57.3% win, +188.95 (7-day, w/ stop) |
| Options 2: Notional Volume Delta (1300/95, DTE-gated) | 5 trades, **80.0% win**, +13.75 pts | 70.0% win, +130.65 (5-day gated) |
| Options 3: Notional OI Delta (650/80, DTE-gated) | 7 trades, **42.9% win**, +14.25 pts | 65.1% win, +152.00 (5-day gated) |
| Options 4: 25-Delta Skew Δ (1300/97, +30% stop) | 5 trades, **80.0% win**, +13.55 pts | 71.7% win, +160.70 (7-day, w/ stop) |

**2-12 trades per metric on a single day — same "too small a sample to confirm or reject anything
on its own" caveat as 09-17.** Noted for the record, not a verdict:

- **The futures session-gated switch had a losing day** — 41.7% win, well below its 57.4%
  backtested rate, net negative. Its first real out-of-sample test (09-17) wasn't run against the
  switch itself (that config was locked afterward), so this is genuinely the switch's FIRST
  out-of-sample reading, and it's a soft one. Worth more days before reading into it — DepthImbalance
  itself had a losing day inside the original backtest window too.
- **Options 2 (Volume Delta) and Options 4 (Skew Δ) both came in with win rates that meet or
  *exceed* their backtested rate** (80.0% both, vs. 70.0% and 71.7% backtested respectively) — a
  reassuring first read for the two metrics that also passed their soft-stop test.
- **Options 3 (OI Delta) is the one real yellow flag**: 42.9% win vs. 65.1% backtested — well below
  its own expectation, and the only metric here whose win rate came in on the wrong side of 50%.
  Net still landed positive purely because 2 of the 3 losing trades were small and 2 winners were
  disproportionately large (+11.45, +12.50) — the kind of "net masks a weak hit rate" pattern the
  DTE-split check was specifically built to catch on the calibration data, now showing up in a
  single out-of-sample day. Not enough to reject on one day (same discipline as TobDepthDivergence's
  first out-of-sample day, which also looked bad early and needed more days to assess), but this is
  the metric most worth watching as more days arrive — and given Options 3 and Options 2 were found
  to be the one correlated pair (+0.239) in the robustness check above, a bad day for OI Delta
  specifically (not Volume Delta) is a useful data point that they're carrying at least partly
  different information, not just noisier/cleaner copies of the same signal.
- **Options 1a (Raw ΔIV) had a win rate close to its backtested rate (75.0% vs. 80.2%) but a net
  loss** — one large loser (-24.30 on the day's last trade, held from 13:14 to 14:59) outweighed
  three small winners. With only 4 trades this is a concentration observation, not a rejection —
  worth remembering that this metric has no stop-loss adopted, unlike its price-signed sibling.
- **Options 1b (Price-signed ΔIV) only fired twice** — too few trades to read anything into today,
  noted for completeness only.

**Next step**: same as the futures-only log above — keep running every new trading day through both
the futures and options locked configs, without re-tuning, so both rankings are eventually judged
on data none of them were fit to. OI Delta in particular should be watched closely on the next
few days given today's below-50% win rate.

## Crossover experiment: dual-MA crossover on the futures composite score (2026-09-18, user's idea)

A genuinely different trading-rule shape, tried at the user's request: instead of the
percentile-threshold entry every other metric in this project uses, a classic dual-window
**moving-average crossover** applied directly to the LOCKED futures composite score
(`SessionGatedDepthDurationConfirmed`'s own per-bar scaled score, including its TOB open-window
confirmation gate — the real validated FuturesScore, not a new composite). Fast/slow are simple
moving averages of the last N per-bar scores on the SAME bar sequence (not two different bar
thresholds, despite the initial framing) — e.g. "4 fast / 12 slow" means the last 4 bars' average
vs. the last 12 bars' average. A crossing only fires as a signal once the fast/slow gap at that bar
is at least a configurable `thresholdPoints` (on the same -100..+100 scale everything else in this
project uses) — a noise filter, not a percentile (this rule has no percentile concept at all).
Entry/exit mechanics otherwise match every other metric: real ATM option fills, the standing
09:30-15:00 entry window / 15:15 force-close, one position at a time, exit on the opposite
qualifying crossover or end-of-day. New code: `TradeSimulator.SimulateCrossoverDayAsync`, CLI
commands `crossover` and `crossover-calibrate`.

**First pass, the user's own suggested starting point (4 fast / 12 slow / 2-point threshold, 2600
bar threshold — matching the locked switch's own bar size)**: badly over-firing. 356 trades across
8 days (44.5/day, vs. every other metric's 7-20/day target), 51.4% win rate — barely above a coin
flip. Root cause: at 2600-threshold bars, a 4-bar/12-bar window is short in wall-clock time, so the
fast/slow gap crosses zero constantly on noise, and a 2-point gap threshold (2% of the full
-100..+100 range) is far too loose a filter at this bar cadence.

**Calibration sweep** (fast ∈ {3,4,6}, slow ∈ {10,12,16,20}, threshold ∈ {1,2,3,5}, 2600 bars, 48
combos, all 7 backtest days): **nothing in this grid cleanly clears both the 7-20 trades/day target
AND a solid win rate at the same time** — trade frequency ranges 16.4-65.9/day across the whole
grid, mostly still above target, and win rate mostly sits in the low-to-high 40s. The one standout:
**6 fast / 12 slow / 5-point threshold — 57.9% win rate (the best in the entire 48-row sweep),
20.7 trades/day (right at the edge of target), +163.50 net over 145 trades, positive on 6 of 7
days** (09-15 the only real dip, 44.7%). Concentration: top trade 43.5% of net, top 2 = 58.5% —
higher than every confirmed options survivor, worth flagging as a real weakness even before the
out-of-sample check below.

**Out-of-sample check on the held-out 09-18 day**: 16 trades, **43.8% win, -15.55 net** — below the
backtested 57.9% win rate, and the day's trades cluster heavily in the same post-13:30 afternoon
window that already hurt BarDurationUrgency that day (unsurprising, since the composite score being
averaged switches to BarDurationUrgency's own formula after 10:00 IST).

**Verdict on the first sweep (fast 3-6, slow 10-20, threshold 1-5): NOT CONFIRMED.** No combo
satisfies both frequency and quality simultaneously, the one standout (6/12/5) had worse
concentration than every confirmed options survivor, and it underperformed on the held-out 09-18
day. Superseded by the wider sweep below.

### Second sweep (2026-09-18), targeting a tighter 5-12 trades/day band per explicit request

Widened the grid to fast ∈ {6,8,10,12}, slow ∈ {20,25,30,40}, threshold ∈ {5,8,10,15,20}, still at
2600 bars — 80 combos, still all 7 backtest days. **23 combos land in the requested 5-12/day
band.** Ranked by win-rate quality (project convention: prefer win rate over raw net when picking
off a sweep), one clear family stands out: **every "8 fast, 5-point threshold" combo, across all
four slow windows tried, clears 50-53% win rate** — a combo being stable across a whole dimension
of the grid (here, the slow window) rather than one lucky cell is exactly the kind of robustness
this project looks for before trusting a pick.

| Fast | Slow | Threshold | Trades/Day | Win Rate | Net (7-day) |
|---|---|---|---|---|---|
| 8 | 20 | 5 | 11.7 | 52.4% | +160.05 |
| 8 | 25 | 5 | 11.7 | 51.2% | +149.95 |
| 8 | 30 | 5 | 11.6 | 50.6% | +145.90 |
| **8** | **40** | **5** | **10.6** | **52.7%** | **+241.45** |

**8 fast / 40 slow / 5-point threshold is the best of the family** — highest win rate AND highest
net of the four, right in the middle of the requested band (10.6 trades/day, 74 trades over 7
days). Positive on 6 of 7 backtest days (only 09-09 negative, -13.15), win rate above 50% on 6 of
7 days too. Concentration: top trade 25.9% of net, top 2 = 46.4% — meaningfully better than the
first sweep's 6/12/5 pick (43.5%/58.5%), though still moderate, comparable to the weaker end of
the confirmed futures/options survivors, not the cleanest seen in this project.

**Out-of-sample check on 09-18 (held out, same as every other check in this log)** — all 4 members
of the "8-fast" family tested:

| Combo | Today | Backtest expectation |
|---|---|---|
| 8/20/5 | 10 trades, 60.0% win, +13.40 pts | 52.4% win, +160.05 |
| 8/25/5 | 8 trades, 37.5% win, +17.70 pts | 51.2% win, +149.95 |
| 8/30/5 | 6 trades, 50.0% win, +7.80 pts | 50.6% win, +145.90 |
| **8/40/5** | **6 trades, 83.3% win, +35.75 pts** | 52.7% win, +241.45 |

**All 4 came in net-positive on the held-out day, and 8/40/5 (the backtest's best combo) is also
the out-of-sample standout — win rate well ABOVE its own backtested rate.** This is a genuinely
encouraging cross-validation: the same combo that led the 7-day sweep on win-rate quality also led
the one real out-of-sample test, rather than a different cell winning each time (which would be a
classic overfitting red flag).

**Verdict: PROMISING, still not fully confirmed** — one out-of-sample day (6 trades) is nowhere
near enough to call this validated by the same standard applied to every other metric in this
project (DTE split, session-phase split, more out-of-sample days all still needed), but this is a
meaningfully stronger result than the first sweep produced, and the "8-fast, 5-point threshold"
region's stability across both the slow-window dimension AND the single out-of-sample day is worth
taking seriously as a candidate going forward. **Adopted for continued tracking: 8 fast / 40 slow /
5-point threshold, 2600 bars.** Next new trading day should be run through this combo (and,
for comparison, the rest of the 8-fast family) the same way every other locked config now gets
logged in this out-of-sample section.

**Follow-up, same day: the 3 confirmed standalone futures metrics (DepthImbalance, BarDurationUrgency,
TopOfBookImbalance) run individually against 09-18**, to see whether the switch's bad day was
uniform or came from one leg specifically:

| Metric (locked config) | Today | Backtest expectation (7-day) |
|---|---|---|
| DepthImbalance (650/93, +30% stop) | 19 trades, 63.2% win, +12.60 pts | 56.1% win, +151.50 |
| **BarDurationUrgency (2600/90)** | 11 trades, **36.4% win, -43.20 pts** | 54.9% win, +119.60 |
| TopOfBookImbalance (2600/80) | 18 trades, 61.1% win, +36.85 pts | 54.7% win, +124.60 |

**It was not uniform — it was one leg.** Verified directly (trade-by-trade): every one of the
session-gated switch's post-10:00 trades today is byte-for-byte identical to BarDurationUrgency's
own standalone trades (same entry/exit times, same prices, same P&L) — the switch's post-10:00 leg
IS BarDurationUrgency's own score, and today BarDurationUrgency had a genuinely bad day (36.4% win,
the only one of the three below 50%, and the worst reading of anything in this whole validation
round). Meanwhile DepthImbalance (the switch's pre-10:00 leg) and TopOfBookImbalance (the
confirmation filter) both held up well — DepthImbalance's win rate actually exceeded its own
backtest (63.2% vs 56.1%), and TopOfBookImbalance did too (61.1% vs 54.7%) with the best net of the
three. **The switch's -32.30 pts today is a BarDurationUrgency problem specifically, not a
breakdown of the whole session-gated design** — worth remembering before reading too much into the
switch's own single-day number above. Still one day, still not a verdict on BarDurationUrgency
either (its time-of-day edge and DTE-robustness were both established on 7 real backtest days,
not zero), but this is exactly the kind of decomposition the switch's own construction makes
possible, and it should be repeated on the next few out-of-sample days before trusting either the
switch's headline number or BarDurationUrgency's bad day too heavily.

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

## Phase 2 metric 1: ATM Complex Notional Depth Imbalance (2026-09-19/20)

`(ΣBidQty − ΣAskQty)/(ΣBidQty+ΣAskQty)`, top-5 book, Call+Put combined across the ATM band. New
`OptionDepthBarRow`/`OptionDepthPopulator`, tick-accumulated per bar via the same
`DepthImbalanceAccumulator` the futures side's own `DepthImbalance` already uses.

**Built un-negated first** (more resting buy than sell interest reads bullish, mirroring the
futures reading) — win rate stuck below 50% at every loose-to-mid percentile, both ATM±1 and
ATM±2, the same rejection signature seen repeatedly elsewhere in this project. **Flipped, re-ran
the full sweep**: win rate above 50% in nearly every cell, both bands, all three thresholds —
one of the cleanest patterns found in this whole project.

### Band-width comparison, ATM±1 vs ATM±2 (2026-09-20, user's own question)

Every narrow-band options metric so far (metrics 2/3 in Phase 1, and this one until now) used
ATM±1 because that's what the Phase 0 plan specified, never because it was tested against a wider
band. `OptionDepthBarRow.BandWidth` is now a real parameter (not hardcoded) specifically so this
could be checked. Best combo per band, same 8-day window:

| Band | Combo | Trades/Day | Win Rate | Net | Concentration | Days positive |
|---|---|---|---|---|---|---|
| ATM±1 (3) | 1300/90 | 14.2 | 54.4% | +319.15 | 22.8% top / 41.6% top-2 | 8 of 8 |
| **ATM±2 (5)** | **2600/80** | **13.5** | **60.2%** | +290.65 | **15.4% top / 30.5% top-2** | 7 of 8 |

**Band width genuinely matters here — not a blowout, but a real, measurable difference.** ATM±2
wins on win-rate quality and concentration (both meaningfully better); ATM±1 wins on raw net and
day-consistency (positive every single day vs. one losing day for ATM±2, 09-17). Per this
project's own "prefer win-rate quality over raw net" convention, **ATM±2 (2600/80) is the primary
pick** — 60.2% win, +290.65 net, 108 trades, positive 7 of 8 days, DTE-robust (both 0-DTE days in
the window, 09-08 and 09-15, are among the strongest days, no gate needed). ATM±1 (1300/90) stays
a credible alternative, not discarded.

**Verdict: CONFIRMED, sign-flipped, ATM±2 preferred.** This directly answers the user's question:
band width was never tested before, and testing it here found a real (if modest) effect —
worth doing for metrics 2/3 in Phase 1 too as a follow-up, though those stay locked for now per
the "don't re-litigate confirmed Phase 1 results pre-emptively" decision from when this parameter
was first added.

**Still pending for this metric**: concentration/DTE/session-phase checks are done; redundancy
against the other 2 Phase-2 depth metrics and a stop-loss sweep are Phase 3 work per the plan.

## Phase 2 metric 2: TobDepthDivergence on the ATM Complex (2026-09-20)

Full-book ATM-complex imbalance minus touch-only ATM-complex imbalance — direct options analog of
the futures side's own `TobDepthDivergence` (which collapsed once the trading-hours gate was
correctly enforced there, but per the "one clock's verdict doesn't transfer automatically"
convention, tried fresh here rather than assumed dead on arrival).

**Built as-is (no flip needed)** — promising immediately: best combo **1300/95, 55.1% win,
+236.05 net, 78 trades, 9.8 trades/day**, positive on 6 of 8 days. Concentration: top trade 25.9%
of net, top 2 = 38.6% — moderate, not alarming. DTE split: 0-DTE 54.5% win (+61.05 net) vs.
non-0-DTE 55.4% win (+175.00) — no meaningful gap, **no DTE gate needed**.

**Band-width check**: ATM±2 gives an almost identical result at the same combo (1300/95: 78
trades — exactly the same count — 53.8% win, +256.70 net) — band width doesn't move this metric
much, unlike metric 1. **Kept at ATM±1** for simplicity since the wider band buys nothing here.

**Verdict: CONFIRMED, no gate, no flip needed.** Best combo 1300/95, ATM±1.

## Phase 2 metric 3: Call vs Put Depth Imbalance (2026-09-20)

`(CallDepth − PutDepth)/(CallDepth+PutDepth)` — no futures analog, compares total resting
liquidity on the Call side vs. Put side of the ATM±1 band.

**Built un-negated first** (more resting depth on the Call side reads bullish) — weak, negative
net almost everywhere, and trade frequency never cleared 4.0/day at any setting (the band only
has 6 instruments, and this ratio evidently sits in extreme territory rarely). **Flipped**:
win rate improved to the upper-40s/50s and net turned consistently positive at every setting
tested — the flip is directionally correct — but **trade frequency still never reaches the 7-20/day
target at any combo** (max 4.0/day at 650/75, most settings 1-3/day).

**Verdict: NOT VIABLE AS BUILT** — same "real signal, wrong frequency" tier as Skew25DeltaLevel and
Distance to Highest-OI Strike from Phase 1. The 6-instrument band may simply be too narrow a
sample for this specific ratio to move often enough; a wider band (already known configurable via
`BandWidth`) or a different formulation (e.g. a flow/change version instead of a level) would be
the natural next thing to try if revisited, not attempted here.

## Phase 2 summary

| Metric | Verdict | Best combo | Win Rate | Net |
|---|---|---|---|---|
| 1. ATM Complex Notional Depth Imbalance | CONFIRMED, sign-flipped, ATM±2 preferred | 2600/80, ATM±2 | 60.2% | +290.65 |
| 2. TobDepthDivergence on ATM Complex | CONFIRMED, no flip, no gate | 1300/95, ATM±1 | 55.1% | +236.05 |
| 3. Call vs Put Depth Imbalance | NOT VIABLE (frequency) | — | — | — |

**2 of 3 Phase 2 metrics confirmed** — both without needing a DTE gate, unlike most of Phase 1's
survivors. Combined with Phase 1's 6 confirmed sub-variants (plus 1 confirmation-filter candidate)
and the crossover experiment (promising, not yet fully confirmed), the options-side candidate
pool now stands at 8 confirmed standalone scores. Redundancy checks (these 2 against each other,
and against Phase 1's survivors) and stop-loss sweeps are Phase 3 work, not yet done.

## Band-width retrofit check on Phase 1 metrics 2/3 (2026-09-20, user's own follow-up question)

Having found ATM±2 genuinely better for Phase 2 metric 1, checked whether the same holds for
Phase 1's own narrow-band metrics (Notional Call-Put Volume Delta, Notional OI Delta) — both used
a hardcoded ATM±1, never tested wider, same as Phase 2 was before this session. Retrofitted
`BandWidth` onto `OptionBandFlowBarRow`/`OptionOiBarRow` the same way (real parameter, both widths
coexist, migration correctly defaulted existing Phase 1 rows to BandWidth=3 rather than EF's own
auto-generated 0 — checked before applying, would have silently orphaned every existing confirmed
row otherwise). Re-ran both metrics' full sweeps at ATM±2, same 8-day window (2026-09-08 through
2026-09-19) as ATM±1 for a fair comparison.

**Result: the opposite conclusion from Phase 2's metric 1 — ATM±1 holds up as well or better for
both.**

| Metric | Band | Best combo | Win Rate | Net |
|---|---|---|---|---|
| Notional Volume Delta | ATM±1 | 650/95 | 63.3% | +84.20 |
| Notional Volume Delta | ATM±2 | 650/93 | 63.6% | +57.25 |
| **Notional OI Delta** | **ATM±1** | **2600/75** | **63.5%** | **+192.20** |
| Notional OI Delta | ATM±2 | 1300/80 (best in-target) | 63.9% | +82.65 |

Volume Delta is essentially a wash between bands (ATM±1 marginally ahead on net). **OI Delta
clearly favors ATM±1** — nothing in ATM±2's in-target set comes close to ATM±1's 2600/75 combo.

**Conclusion: band width is metric-specific, not a universal "wider is better" (or worse) rule.**
Phase 2 metric 1 genuinely benefited from ATM±2; Phase 1 metrics 2/3 don't. The original ATM±1
choice for metrics 2/3 wasn't just an untested assumption that happened to be wrong — tested now,
it holds up as the right call. **No change to Phase 1's locked configs** (650/95 for Volume Delta,
650/80 gated for OI Delta remain as documented) — this was a check, not a re-optimization; picking
2600/75 for OI Delta now would be re-tuning on data that includes 09-18/09-19, which the original
lock didn't have, and isn't how this project treats already-confirmed results.

## Phase 3: redundancy check across the full confirmed pool (2026-09-20)

Extended the earlier 4-metric correlation tool (`correlate-options`) to all 8 confirmed
standalone/filter candidates, each computed with its own exact locked formula (raw ΔIV,
price-signed ΔIV, Volume Delta, OI Delta, Skew Change, Depth Imbalance at its own adopted ATM±2,
TOB Divergence, Max Pain distance), pooled across all 8 available days, 9,651 bars with all 8
present.

| | IvRaw | IvPriceSigned | VolDelta | OiDelta | SkewChange | DepthImbalance | TobDivergence | MaxPainDist |
|---|---|---|---|---|---|---|---|---|
| **IvRaw** | 1.000 | 0.003 | -0.127 | 0.012 | 0.019 | -0.005 | 0.004 | 0.016 |
| **IvPriceSigned** | | 1.000 | -0.095 | 0.017 | -0.095 | 0.054 | -0.019 | 0.014 |
| **VolDelta** | | | 1.000 | **0.249** | -0.008 | **-0.220** | 0.069 | 0.109 |
| **OiDelta** | | | | 1.000 | -0.027 | -0.060 | 0.013 | 0.101 |
| **SkewChange** | | | | | 1.000 | -0.037 | 0.015 | -0.005 |
| **DepthImbalance** | | | | | | 1.000 | -0.086 | 0.114 |
| **TobDivergence** | | | | | | | 1.000 | -0.038 |

**26 of 28 pairs are fully independent** (|r| < 0.15) — very low double-counting risk for a Phase 4
combination. Notably, **raw ΔIV and price-signed ΔIV are themselves independent (0.003)** despite
sharing the same underlying ΔIV data — the price-signing transformation genuinely decorrelates
them, confirming they're worth keeping as separate candidates, not near-duplicates of each other.
Phase 2's two depth metrics (DepthImbalance, TobDivergence) are also independent of each other
(-0.086) despite being derived from the same `OptionDepthBarRow` table — the full-book-vs-touch
divergence really does carry different information than the level itself.

**Two pairs land in the "related but not redundant, keep both" tier** (same 0.15-0.43
interpretation scale used throughout this project):
- **Volume Delta vs. OI Delta: +0.249** — already found in the earlier 4-metric robustness check,
  reproduced here on the extended 8-day window. Makes sense (both read ATM±1 options activity).
- **Volume Delta vs. Depth Imbalance: -0.220** (new finding) — moderate negative relationship.
  Economically plausible: heavy notional volume trading through the book plausibly consumes
  resting depth on one side, pushing the (flipped-sign) depth imbalance the opposite way. Neither
  pair is strong enough to drop one metric outright, but both are worth remembering when weighting
  a Phase 4 combination so they aren't implicitly double-counted.

**Verdict: redundancy check clean.** No pair needs to be dropped; Phase 4 combination work can
proceed without a forced pruning step.

## Phase 3: stop-loss sweep on the 3 not-yet-tested confirmed metrics (2026-09-20)

Volume Delta and OI Delta were already stop-tested in the earlier 4-metric robustness check (both
rejected every level — see that section above). Price-signed ΔIV and Skew Change already have
adopted stops (50% and 30% respectively). Remaining untested: raw ΔIV, and both Phase 2 depth
metrics. Same rule throughout: adopt only when win rate AND net both improve together.

| Metric | Baseline | 20% | 30% | 40% | 50% | Verdict |
|---|---|---|---|---|---|---|
| Raw ΔIV | 67.5% / -30.90 | 66.2% / -12.05 | 65.6% / -39.20 | 66.4% / -52.25 | 66.9% / -27.55 | **No stop** — every level makes win rate worse, none turns net positive |
| ATM Depth Imbalance (ATM±2) | 60.2% / +290.65 | 57.4% / +273.05 | **60.4% / +299.85** | 60.2% / +290.65 (unchanged, never triggers) | 60.2% / +290.65 (unchanged) | **Adopt 30%** — only level where both improve, modest but real |
| TOB Divergence | 55.1% / +236.05 | 50.6% / +182.55 | 53.1% / +232.15 | 55.0% / +239.35 | 54.4% / +235.45 | **No stop** — closest candidate (40%) has net up but win rate flat-to-down, doesn't clear the bar |

Note: raw ΔIV's baseline here (67.5% win, -30.90 net, 8-day pooled) looks worse than its own
locked DTE-gated verdict (80.2% win, +77.25 net, 5-day gated) — expected, since this sweep runs
the full un-gated 8-day pool including both 0-DTE days and the new 09-18 data, not the DTE-gated
subset the actual locked config trades on. The stop-loss question itself is unaffected either way:
no level helps on either framing.

## Phase 3 summary

- **Redundancy**: clean, no pair strong enough to drop (see "Phase 3: redundancy check" above).
- **Stop-loss**: 2 of 8 confirmed metrics now carry an adopted stop from this round (ATM Depth
  Imbalance 30%), on top of the 2 already adopted in Phase 1 (Price-signed ΔIV 50%, Skew Change
  30%) — 4 of 8 total now have a stop; the other 4 (raw ΔIV, Volume Delta, OI Delta, TOB
  Divergence) trade unprotected by design, tested and rejected at every level.
- **Phase 3 CLOSED.** Full confirmed pool, locked configs going into Phase 4:

| # | Metric | Combo | Stop | Win Rate | Net (backtest) |
|---|---|---|---|---|---|
| 1a | Raw ΔIV | 1300/97, DTE-gated | none | 80.2% | +77.25 (5d) |
| 1b | Price-signed ΔIV | 1300/97 | 50% | 57.3% | +188.95 |
| 2 | Volume Delta | 1300/95, DTE-gated | none | 70.0% | +130.65 (5d gated) |
| 3 | OI Delta | 650/80, DTE-gated | none | 65.1% | +152.00 (5d gated) |
| 4 | Skew Change (raw) | 1300/97 | 30% | 71.7% | +160.70 |
| 5* | Max Pain distance | 1300/75 | — | 71.4% | +224.45 (filter candidate, not standalone) |
| 6 | ATM Depth Imbalance | 2600/80, ATM±2 | 30% | 60.4% | +299.85 |
| 7 | TOB Divergence | 1300/95 | none | 55.1% | +236.05 |

**Next**: Phase 4 (build OptionsScore) — per the plan's own amendment, check the 7 standalone
survivors for a regime-bias split (time-of-day, DTE, or option-specific axis) BEFORE defaulting to
an equal-weight blend, same blend-vs-switch-vs-confirm lesson from the futures side. The session-
phase splits already done (metrics 1b/2/3/4 in the earlier robustness check) found a real pattern
worth building on: OI Delta's edge concentrates in the Open, the others in Mid — not yet checked
for metrics 1a, 6, 7. Max Pain distance is the natural confirmation-filter candidate, same role
TopOfBookImbalance plays on the futures side.

## Phase 4 prep: session-phase split, remaining 3 metrics (2026-09-20)

Completed the session-phase picture for the full 7-metric confirmed pool (4 already done in the
earlier robustness check, 3 more here: raw ΔIV, ATM Depth Imbalance, TOB Divergence). Added
`--stop=`/`--band=` support to the `session-phase` command so these could run against each
metric's ACTUAL locked config (previously it silently used ATM±1/no-stop regardless).

| Metric | Open (09:30-10:00) | Mid (10:00-13:30) | Close (13:30-15:15) | Pattern |
|---|---|---|---|---|
| Raw ΔIV | 7 trades, 57.1% win, +32.05 | 42 trades, 81.0% win, -7.25 | 36 trades, 83.3% win, +34.55 | High win rate everywhere, Mid net flat despite it |
| Price-signed ΔIV | 7 trades, 42.9% win, -31.85 | 32 trades, 65.6% win, +160.75 | 42 trades, 52.4% win, +24.95 | **Mid-strong only** |
| Volume Delta | 10 trades, 50.0% win, -19.15 | 26 trades, 76.9% win, +120.70 | 6 trades, 50.0% win, -22.80 | **Mid-strong only**, negative elsewhere |
| OI Delta | 16 trades, 81.2% win, +84.35 | 35 trades, 54.3% win, +35.00 | 12 trades, 50.0% win, +14.05 | **Open-strong**, fades |
| Skew Change | 11 trades, 72.7% win, +13.45 | 59 trades, 66.1% win, +92.40 | 33 trades, 72.7% win, +6.40 | Consistent everywhere |
| ATM Depth Imbalance | 21 trades, 71.4% win, +153.30 | 53 trades, 54.7% win, +75.55 | 37 trades, 62.2% win, +71.00 | **Open-strong**, positive everywhere |
| TOB Divergence | 18 trades, 61.1% win, +75.30 | 37 trades, 54.1% win, +144.50 | 23 trades, 52.2% win, +16.25 | Open/Mid similar, fades in Close |

**A real, actionable pattern, same shape as the futures-side DepthImbalance/BarDurationUrgency
split that beat a naive blend there**: OI Delta and ATM Depth Imbalance both peak in the Open;
Price-signed ΔIV and Volume Delta are Mid-only (negative in Open and Close). Skew Change is the
session-consistent one. Raw ΔIV is the outlier — strong win rate in every phase but Mid net is
essentially flat despite an 81% win rate there, worth a closer look (a few outsized Mid losers
likely offsetting many small wins) before trusting it in a combined design.

**Next**: test a linear blend first (the plan's own required first step, per
`docs/METRIC_EVALUATION_PLAYBOOK.md` step 8), then check whether an Open/Mid-gated switch (mirroring
this session-phase split) recovers more than the blend, before deciding Phase 4's combination
design.

## Phase 4: linear blend, tested and found wanting (2026-09-20)

Built `OptionsScoreBlend` — equal-weight average of all 7 confirmed standalone metrics' own
SignedRank scores (own independent tracker per component, missing components excluded and the
average taken over whichever are present, own dedicated magnitude-rank tracker since an average
of percentile-like values isn't itself percentile-shaped — same pattern as the futures side's own
`Composite`).

**Caught and fixed a real dispatch bug before trusting any result**: first calibration run showed
ZERO trades at every single cell. Root cause: the boolean flags gating which tables to load
(`isAtmIvMetric`, `isOiMetric`, etc.) were extended with `|| isOptionsScoreBlend` so the blend's
own data would load — but those SAME flags are also used for per-bar dispatch ROUTING, and the
blend's dispatch branch was placed AFTER them in the if/else-if chain, so `isAtmIvMetric` (now
true for the blend too) intercepted every blend bar first, fell through its own `switch` to
`_ => null` (since `OptionsScoreBlend` isn't one of ITS cases), and the blend never got a chance to
compute anything. Fixed by moving the blend's dispatch check to be the second branch overall
(right after `Composite`), before any of the shared flags. Re-verified against an already-locked
metric (`NotionalOiDelta`) to confirm the fix didn't disturb anything else.

**Result — best combo 1300/95: 62.3% win, +221.35 net, 69 trades, 8.6 trades/day.** Solidly
mid-pack: beats TOB Divergence (55.1%) and ATM Depth Imbalance (60.4%) individually, but doesn't
approach the strongest single inputs (Skew Change 71.7% with stop, Raw ΔIV's own DTE-gated 80.2%).
**Same lesson as the futures side's own composite**: averaging metrics with different regime
biases (the session-phase split already found OI Delta/ATM Depth Imbalance peak in the Open while
Price-signed ΔIV/Volume Delta are Mid-only) dilutes the best individual edge rather than combining
it. Confirms the blend alone is not the answer — per the plan's own step 8, next is checking
whether a session-phase-gated switch (mirroring the futures side's session-gated switch, using
this project's own session-phase findings) recovers more than either the blend or the best single
metric.

## Phase 4: session-gated switch — beats the blend (2026-09-20)

Built `OptionsScoreOpenMidSwitch` — a hard switch, not a blend, mirroring the futures side's own
`SessionGatedDepthDuration` exactly: ATM Depth Imbalance's own score drives bars before 10:00 IST
(its strongest window, 7.30 pts/trade), Price-signed ΔIV's own score drives everything from 10:00
onward (Mid-strong, and unlike Volume Delta stays positive through the Close too). Never an
average of both on the same bar. Same dispatch-order care taken as the blend (checked before the
shared `isAtmIvMetric` flag in the if/else-if chain).

**Result — best combo 2600/90: 57.1% win, +416.85 net, 156 trades, 19.5 trades/day.** Nearly
double the blend's net (+221.35) at a comparable win rate, and the highest net of anything found
in the entire options phase so far. Concentration: top trade 12.4% of net, top 2 = 24.8% — among
the lowest concentration of any metric in this whole project (comparable to metric 2's 20.0% and
price-signed ΔIV's 22.4% from Phase 1). Positive on 5 of 8 days; both 0-DTE days (09-08, 09-15)
are strong, no DTE gate needed.

| | Win Rate | Net | Trades/Day | Concentration |
|---|---|---|---|---|
| Blend (equal-weight, 7 metrics) | 62.3% | +221.35 | 8.6 | not checked |
| **Switch (Depth Imbalance / Price-signed ΔIV)** | 57.1% | **+416.85** | 19.5 | **12.4% / 24.8%** |
| ATM Depth Imbalance alone | 60.4% | +299.85 | 13.5 | 15.4% / 30.5% |
| Price-signed ΔIV alone | 57.3% | +188.95 | ~10.6 | ~20% (Phase 1) |

**The switch beats every input that went into it** — higher net than either leg alone, without
diluting either one's edge the way the blend did. Same lesson as the futures side, confirmed a
second time on a genuinely different clock/instrument mix: a hard regime switch recovers real
signal that a naive average cancels out.

**Verdict: OptionsScoreOpenMidSwitch (2600/90) is the leading OptionsScore candidate.** Not yet
fully locked — same caveats as every "just built" result in this log: needs more out-of-sample
days, and the plan's own Phase 5 (weighting FuturesScore vs. OptionsScore) is still ahead. But this
is now the strongest, cleanest composite found in the options phase, and a reasonable point to
adopt as the working OptionsScore going into Phase 5.

## Phase 5 prep: experiment list, working through in order (2026-09-20)

Before Phase 5, user asked for a complete list of every untested-but-valid technique to check by
evidence rather than skip by assumption (backtesting is cheap; nothing gets ruled out on a hunch).
17-item list, working top to bottom. `OptionsScoreOpenMidSwitch`'s switch time is now a real
parameter (`--switchtime=HH:mm` on `trade`/`calibrate`), separate from the futures side's own
locked `SessionGateSwitchTime` constant -- sweeping this never touches the already-locked futures
switch.

### 1. Switch-time sweep — CONFIRMED 10:00 is best

Swept 10:00/10:15/10:30/10:45 at 2600 (the switch's own locked threshold), same 8-day window.
Best combo each time was 2600/90:

| Switch time | Win Rate | Net |
|---|---|---|
| **10:00 (locked)** | **57.1%** | **+416.85** |
| 10:15 | 55.9% | +356.05 |
| 10:30 | 56.0% | +383.30 |
| 10:45 | 52.1% | +307.90 |

Monotonically worse as the boundary moves later (small non-monotonic blip at 10:30, not enough to
change the conclusion) — later boundaries hand more bars to Depth Imbalance's weaker Mid-window
performance instead of Price-signed ΔIV's stronger one. **No change — 10:00 stays the switch
point**, now backed by an actual sweep instead of inherited from the futures side's own boundary.

### 2. Entry window start (09:15 vs. 09:30) — inconclusive, not adopted

Made `EntryWindowStart` a real parameter too (`--entrystart=HH:mm`, default unchanged at 09:30,
shared code path so every metric could use it, tested here on the current best candidate). At
2600/90 with `--entrystart=09:15`: 57.3% win, +550.70 net, 164 trades — looks like a ~32%
improvement over baseline's 416.85.

**Checked before trusting it**: the top trade (124.75 pts) is a Put entered at 09:15:04 on 09-15
(exit 10:11, +128.9%) — the SAME known high-volatility day already flagged multiple times in this
project for inflating other metrics' headline numbers (VwapDeviation's own "propped up by 2 lucky
trades" closure, the crossover experiment's best day, several others). That one trade accounts for
**93% of the entire improvement** (124.75 of the 133.85-point gain). Excluding it: net is 425.95 —
essentially a wash against the 09:30 baseline's 416.85. Concentration also gets worse with the
09:15 window (22.7% top-trade vs. baseline's 12.4%).

**Verdict: inconclusive, NOT adopted.** The headline number looked like a real improvement but
doesn't survive the same single-trade concentration check that's caught false positives
repeatedly in this project. Real answer requires more days with activity in the 09:15-09:30
window before this can be judged either way — keeping the standing 09:30 rule for now, not
because of the original practical rationale (execution quality in the first 15 minutes), but
because the backtest itself doesn't show a robust edge once the outlier is set aside.

### 3. 3-way switch (dedicated Close leg) — CONFIRMED improvement, new leading candidate

Built `OptionsScoreThreeWaySwitch`: Open (before 10:00) = ATM Depth Imbalance (unchanged from the
2-way switch), Mid (10:00-13:30) = Price-signed ΔIV (unchanged), **Close (13:30-15:15) = Raw ΔIV**
(new dedicated leg, using the session-phase split's own finding that Raw ΔIV was the single best
Close performer, 83.3% win, of anything tested — previously left unused since the 2-way switch let
Price-signed ΔIV cover Mid+Close together).

**Result — best combo 2600/90: 60.8% win, +446.05 net, 148 trades, 18.5 trades/day.** Beats the
2-way switch on every dimension checked:

| | Win Rate | Net | Trades/Day | Concentration | Days positive |
|---|---|---|---|---|---|
| 2-way switch (Depth Imbalance / Price-signed ΔIV) | 57.1% | +416.85 | 19.5 | 12.4% / 24.8% | 5 of 8 |
| **3-way switch (+ Raw ΔIV Close leg)** | **60.8%** | **+446.05** | 18.5 | **11.6% / 23.1%** | **7 of 8** |

Positive on 7 of 8 days (only 09-18 negative), win rate above 50% on every day except that one.
Both 0-DTE days (09-08: 64.7% win, 09-15: 54.5% win) stay solidly positive — no DTE gate needed,
consistent with both legs' own individual no-gate findings.

**Verdict: CONFIRMED improvement — OptionsScoreThreeWaySwitch (2600/90) is the new leading
OptionsScore candidate**, replacing the 2-way switch. Giving Raw ΔIV its own dedicated window
recovered real signal the 2-way design was leaving on the table, same "don't leave a confirmed
metric unused just because the initial design didn't have a slot for it" lesson as everything
else found by testing rather than assuming in this pass.

### 4. OI Delta vs. ATM Depth Imbalance as the Open leg — CONFIRMED Depth Imbalance is better

Built `OptionsScoreThreeWaySwitchOiOpen`: identical to the 3-way switch (item 3) except OI Delta
drives the Open leg instead of ATM Depth Imbalance. Direct head-to-head at the same combo (2600/90):

| | Win Rate | Net |
|---|---|---|
| **Depth Imbalance Open (adopted)** | **60.8%** | **+446.05** |
| OI Delta Open | 59.7% | +385.10 |

Depth Imbalance wins on both dimensions at the winning combo, and across the broader sweep (best
in-target OI-open cell tops out at 61.6% win but only +167.90 net at 1300/97 — never both high
win rate and strong net together the way Depth Imbalance's own 2600/90 does).

**Verdict: CONFIRMED — ATM Depth Imbalance stays the Open-leg driver.** The original pick (made by
per-trade-net comparison in isolation) holds up under a real in-switch, apples-to-apples test —
not just an assumption that happened to survive unchallenged.

### 5. Volume Delta vs. Price-signed ΔIV as the Mid leg — CONFIRMED Price-signed ΔIV is better

Built `OptionsScoreThreeWaySwitchVolMid`: identical to the 3-way switch except Volume Delta drives
Mid instead of Price-signed ΔIV. At the same combo (2600/90): 64.3% win but only **+172.70 net**
— less than half the adopted design's +446.05. Best cell anywhere in the sweep (650/95: 64.1% win,
+306.85 net) still falls well short of the adopted design's net despite a higher win rate.

**Verdict: CONFIRMED — Price-signed ΔIV stays the Mid-leg driver.** A real trade-off exists (Volume
Delta trades a meaningfully higher win rate for a much lower net), but the net gap is too large to
call this a win-rate-quality improvement — the adopted design's net is more than double.

### 6. Skew Change as an all-day confirmation filter — NOT adopted, hurts the switch

Built `OptionsScoreThreeWaySwitchConfirmed`: the 3-way switch's own scoring unchanged, plus a gate
requiring Skew Change's own score to agree in sign before a new position opens (applied all day,
no window carve-out, unlike the futures side's open-only TOB gate — Skew Change didn't show a
narrow window where it's uniquely strong the way TOB does on the futures side).

**Result at the same reference combo (2600/90): 58.2% win, +303.45 net, 110 trades** — both WORSE
than the unconfirmed 3-way switch (60.8% win, +446.05 net, 148 trades). The gate filtered out 38
trades, but the remaining ones performed worse on both dimensions, not better — the opposite of
what a useful confirmation filter should do (remove losers, keep or improve win rate on what's
left).

**Verdict: NOT adopted.** All-day Skew Change confirmation doesn't help this design. Doesn't rule
out a confirmation filter entirely — a window-restricted version (matching the futures side's
own open-only carve-out) or a different confirming metric (TOB Divergence, or Max Pain distance
per item 8 below) might behave differently, but the specific design tried here is confirmed worse,
not just untested.

### 7. Early directional conviction (09:15-10:30) as a confirmation filter — NOT adopted

Built `OptionsScoreThreeWaySwitchEarlyConviction`: a genuinely new kind of gate, not another
options metric. `EarlyConviction = sign(future close at the first bar ending at/after 10:30 IST −
the day's own opening price)`, computed ONCE per day (a fixed historical observation, not a
running score). Applied only to the Mid/Close legs (10:00 IST onward) — the Open leg trades
ungated since the 10:30 window hasn't closed yet during Open trading, so gating it would be
forward-looking.

**Result at the same reference combo (2600/90): 59.8% win, +370.95 net, 117 trades** — both worse
than the unfiltered 3-way switch (60.8% win, +446.05 net, 148 trades). Best win rate anywhere in
the sweep (2600/97: 65.4%) still comes with meaningfully lower net (+179.75) than the unfiltered
baseline. Same shape as item 6's confirmation filter: trades net for a modest win-rate bump,
never both together.

**Verdict: NOT adopted.** Two confirmation-filter designs tried now (Skew Change all-day, early
directional conviction on Mid/Close) — neither beat the unfiltered 3-way switch. Worth noting as a
real, if modest, pattern: this particular switch design doesn't seem to have "bad trades" that a
simple sign-agreement filter can cleanly remove — the trades it fires are already reasonably
well-selected by the percentile gate alone.

## Phase 5 prep progress: 7 of 17 items done, leading candidate unchanged

`OptionsScoreThreeWaySwitch` @ 2600/90 (Depth Imbalance Open / Price-signed ΔIV Mid / Raw ΔIV
Close, no confirmation filter) remains the leading OptionsScore candidate — it has now survived
direct head-to-head challenges from 6 different alternatives (switch-time sweep, entry-window
test, OI-Delta-open variant, Volume-Delta-mid variant, Skew-Change confirmation, early-conviction
confirmation) without a single one beating it. Items 8-17 remain: Max Pain as confirmation filter,
DTE-interaction on session-phase, blend concentration check, wider bands, crossover follow-ups,
then Phase 5's own combination menu (equal weight / DTE weight / both-must-agree / primary-filter,
FuturesScore + OptionsScore).

### 8. Max Pain distance as a confirmation filter — real trade-off, not a clean answer either way

Built `OptionsScoreThreeWaySwitchMaxPainConfirmed`: same all-day-gate design as item 6, using
Max Pain distance instead of Skew Change — the metric Phase 1 flagged as the natural confirmation
candidate specifically because it was too infrequent to drive a standalone score.

**Result at the same reference combo (2600/90): 64.3% win, +426.40 net, 112 trades, 14.0/day** —
genuinely different shape from items 6/7 (which lost on both dimensions):

| | Win Rate | Net | Concentration | Days ≥50% win |
|---|---|---|---|---|
| Unfiltered 3-way switch | 60.8% | **+446.05** | 11.6% / 23.1% | 7 of 8 |
| **Max Pain confirmed** | **64.3%** | +426.40 (-4.4%) | 12.1% / 24.2% (essentially unchanged) | **8 of 8** |

Win rate up 3.5pp, concentration essentially unchanged, and **every single day stays above 50% win
rate** (63.6/60.0/63.6/54.2/64.3/69.2/91.7/57.1%) — more consistent than the unfiltered switch,
which had at least one weaker day. Cost: net is 4.4% lower.

**Verdict: ADOPTED (2026-09-20), overriding the strict "both must improve" rule by explicit
judgment call.** Reasoning: item 9's DTE-interaction finding (below) shows the unfiltered switch's
day-to-day robustness is *compensating* rather than uniform — the Mid leg sits below 50% win on
non-0-DTE days, the Close leg sits at exactly 50%/net-negative on 0-DTE days, masked only by
pooling. The Max Pain filter's effect lines up directly with that: it is filtering out trades in
exactly those known-weak legs, which is why it turns 7/8 days into 8/8 and lifts win rate 3.5pp
rather than being a random trade-off. For a system now running live paper trading, a smoother
day-to-day equity curve is worth more than a 4.4% net gap on an 8-day sample — that gap is well
within noise at this sample size, while the 8/8-vs-7/8 consistency is a more durable signal tied
to a real structural weakness rather than luck.

**`OptionsScoreThreeWaySwitchMaxPainConfirmed` @ 2600/90 is now the leading OptionsScore
candidate**, replacing the unfiltered `OptionsScoreThreeWaySwitch`. Superseded verdict kept above
for the record. Next: validate on new out-of-sample days as they accumulate, per the standing
"backtesting is long-term" discipline — this is not a final word on 8 days of data.

### 9. DTE-interaction on the session-phase split — robustness is compensating, not uniform

Adapted from the original framing (OI Delta's own DTE interaction) since the adopted switch uses
ATM Depth Imbalance for Open, not OI Delta — tested the actual leading candidate's session-phase
behavior split by DTE instead, the more relevant question now. Split `OptionsScoreThreeWaySwitch`
@ 2600/90's trades by 0-DTE (09-08, 09-15) vs. non-0-DTE (the other 6 days), within each session
phase:

| Phase | 0-DTE | Non-0-DTE |
|---|---|---|
| Open (Depth Imbalance) | 2 trades, 100.0% win, +31.03 pts/trade | 12 trades, 75.0% win, +9.20 pts/trade |
| Mid (Price-signed ΔIV) | 27 trades, 59.3% win, +2.66 pts/trade | **53 trades, 49.1% win** (below 50%), +2.55 pts/trade |
| Close (Raw ΔIV) | **10 trades, 50.0% win** (net -11.20), -1.12 pts/trade | 44 trades, 72.7% win, +78.10 pts, +1.78 pts/trade |

**The switch's overall DTE-robustness (both buckets net-positive pooled: 0-DTE +122.60, non-0-DTE
+323.45) turns out to be two legs' OPPOSITE DTE weaknesses compensating for each other, not
either leg being independently robust.** Mid is weaker on non-0-DTE (win rate actually below 50%
there) but strong on 0-DTE; Close is weak on 0-DTE (exactly 50%, net negative) but strong on
non-0-DTE. Pooled, the switch looks fine on both DTE buckets — but that's because whichever leg
is weak on a given DTE regime is offset by another leg being strong there, not because any single
leg is uniformly good. Open's own split (2 vs. 12 trades) is too small to read into either way.

**Not a rejection — the switch still performs fine pooled, on both DTE buckets, and this is
exactly the kind of dependency that's cheap to know about now** rather than discover later if one
leg's pattern drifts on new data. Worth re-checking this specific breakdown as more out-of-sample
days accumulate, since the current sample (39 and 109 trades split three ways each) is thin for a
DTE x session-phase cross-tabulation.

### 10. Blend concentration check — clean, confirms dilution was the real story

`OptionsScoreBlend` @ 1300/95 (its own best combo): top trade 32.00/221.35 = **14.5%**, top 2 =
60.90/221.35 = **27.5%**. Reasonable, close to the leading switch's own 11.6%/23.1% — not a
concentration red flag. Confirms the blend's weaker net (item 3's comparison) was genuine dilution
from averaging opposite-biased metrics, not an artifact of a lucky/unlucky trade distribution.

### 11. Wider band test (ATM±3) — confirms ATM±2 is a genuine peak, not an arbitrary stop

Tested `AtmComplexDepthImbalance` at ATM±3 (BandWidth=7), the metric where band width mattered
most. Best combo 2600/85: 57.1% win, +210.70 net — worse than ATM±2's own best (2600/80: 60.2%
win, +290.65) on both dimensions, and worse than ATM±1's own best (1300/90: 54.4% win, +319.15)
on net.

Pattern across all three widths now tested: ATM±1 (54.4%/+319.15) → **ATM±2 (60.2%/+290.65, best
win rate)** → ATM±3 (57.1%/+210.70, declining again). **Confirms ATM±2 is a genuine local peak for
this metric, not just where testing happened to stop** — going wider doesn't keep helping.

### 12. Crossover bar-threshold sweep — CONFIRMED 1300 beats locked 2600 config
Tested the dual-MA crossover (originally tuned only at 2600) against 650 and 1300 bar thresholds, same parameter grid (fast 6-12 / slow 20-40 / threshold 5-20 pts).

| BarThreshold | Fast | Slow | Thresh | Trades | Trades/Day | Win% | Net |
|---|---|---|---|---|---|---|---|
| 2600 (locked) | 8 | 40 | 5 | - | - | 52.7% | +241.45 |
| **1300 (new best)** | **8** | **30** | **8** | 69 | 8.6 | **58.0%** | **+273.20** |
| 650 | 8 | 30 | 8 | 69 | 8.6 | 58.0% | +273.20 (dup row, see raw sweep) |

1300/8/30/8 beats the locked config on BOTH win rate and net — passes the adopt-only-if-both-improve rule. Trades/day (8.6) sits comfortably in the 5-12 target band. Not yet adopted pending out-of-sample validation (only 1 held-out day available so far, same constraint as the original 2600 config).

## Phase 5 prep progress: 12 of 17 items done (2026-09-20)
Items 1-12 complete. Note on numbering: items 9-11 as executed correspond to original list items 14-16 (DTE-interaction, blend concentration, wider band) — the equal-weight blend (original item 9) was already done in Phase 4 before this list existed, so items 9-13 proper (DTE-dependent weight, both-must-agree, futures-primary/options-filter, session-weighted multi-metric blend — i.e. the actual FuturesScore+OptionsScore combination methods) have NOT yet been executed and remain open. Flagged to user for confirmation before proceeding.

## Phase 5: FuturesScore + OptionsScore combination (2026-09-20)

The original Phase 5 plan's items 10-13 — the first-ever combination of the two independently-locked
parent scores, `SessionGatedDepthDuration` (FuturesScore) and `OptionsScoreThreeWaySwitch`
(OptionsScore, BandWidth=5/ATM±2) — computed on the same bar. Four new `VolumeBarMetric` values
added to `TradeSimulator.cs`: `FinalScoreDteWeighted`, `FinalScoreBothMustAgree`,
`FinalScoreFuturesPrimaryOptionsFilter`/`FinalScoreOptionsPrimaryFuturesFilter`, and
`FinalScoreSessionWeighted`. Neither parent's own internals or dispatch branch was touched — each
combination re-derives both legs from the parents' own exact locked formulas via dedicated trackers.

**Smoke-tested first** (single-day run before trusting any sweep), then both parents re-verified
unchanged over the same 8-day range used below, confirming no collateral damage from the new
dispatch branch (inserted before `isAtmIvMetric`, following the same dispatch-order rule every prior
combination metric in this file has had to respect):

| Parent | Config | Trades | Win Rate | Net |
|---|---|---|---|---|
| FuturesScore (`SessionGatedDepthDuration`) | 2600/90 | 80 | 55.0% | +144.70 |
| **OptionsScore (`OptionsScoreThreeWaySwitch`)** | **2600/90** | **148** | **60.8%** | **+446.05** |

(These are the 8-day 2026-09-08→2026-09-19 numbers, the same range the 4 new metrics are swept over
below — not the earlier 7-day locked numbers quoted elsewhere in this file, which cover a shorter
range. OptionsScore is the stronger of the two parents on this range by both dimensions, so it is
the bar every new combo must clear on BOTH win rate and net to be adopted, per the project's standing
discipline.)

Full sweep, thresholds 650/1300/2600, band=5, same 8-day range:

### 10. `FinalScoreDteWeighted` — light DTE-dependent weighted average — NOT adopted

0-DTE days weight OptionsScore 0.6/FuturesScore 0.4; non-0-DTE days flip to FuturesScore
0.6/OptionsScore 0.4 (0-DTE determined dynamically per day from the day's own nearest-expiry chain,
not a hardcoded date list). Best in-target cell: **2600/90 — 113 trades, 14.1 trades/day, 45.1% win,
+172.75 net.**

| | Win Rate | Net |
|---|---|---|
| FuturesScore alone (2600/90) | 55.0% | +144.70 |
| **OptionsScore alone (2600/90)** | **60.8%** | **+446.05** |
| FinalScoreDteWeighted (2600/90) | 45.1% | +172.75 |

**Verdict: NOT adopted.** Worse than both parents on win rate, and far short of OptionsScore's net —
averaging the two legs dilutes OptionsScore's own strong signal rather than adding anything, the
same "opposite/uneven-strength legs don't blend well" lesson `Composite`/`OptionsScoreBlend` already
established on their own sides before switches replaced them.

### 11. `FinalScoreBothMustAgree` — futures traded, options-sign confirmation gate — NOT adopted

Traded score is FuturesScore, gated all-day by requiring OptionsScore to agree in sign. Best
in-target cell: **2600/90 — 62 trades, 7.8 trades/day, 51.6% win, +129.75 net.**

| | Win Rate | Net |
|---|---|---|
| FuturesScore alone (2600/90) | 55.0% | +144.70 |
| FinalScoreBothMustAgree (2600/90) | 51.6% | +129.75 |

**Verdict: NOT adopted.** The gate does not even improve on FuturesScore alone (both win rate and
net went DOWN after filtering), the opposite of what a useful confirmation filter should do — same
failure signature as item 6's all-day Skew Change gate on the options side.
`FinalScoreFuturesPrimaryOptionsFilter` shares this exact dispatch/confirmation logic (see its own
doc comment) and was not swept separately since the result is identical by construction.

### 12. `FinalScoreOptionsPrimaryFuturesFilter` — options traded, futures-sign confirmation gate — NOT adopted

Reverse direction: traded score is OptionsScore, gated all-day by requiring FuturesScore to agree in
sign. Best in-target cell: **2600/85 — 130 trades, 16.2 trades/day, 52.3% win, +308.00 net.**

| | Win Rate | Net |
|---|---|---|
| **OptionsScore alone (2600/90)** | **60.8%** | **+446.05** |
| FinalScoreOptionsPrimaryFuturesFilter (2600/85) | 52.3% | +308.00 |

**Verdict: NOT adopted.** Gating OptionsScore's own strong trades on FuturesScore agreement removes
more good trades than bad ones — both win rate and net fall well short of trading OptionsScore
ungated. Combined with item 11, neither direction of "one parent primary, the other as a sign-gate"
beats trading the stronger parent on its own.

### 13. `FinalScoreSessionWeighted` — session-phase-weighted blend — NOT adopted (closest of the four)

Open (<10:00 IST) 0.7 FuturesScore/0.3 OptionsScore, Mid (10:00-13:30) 0.5/0.5, Close (≥13:30) 0.3
FuturesScore/0.7 OptionsScore, per-phase weights tied to each phase's strongest documented leg (see
enum doc comment). Best in-target cell: **2600/93 — 68 trades, 8.5 trades/day, 57.4% win, +379.80
net.**

| | Win Rate | Net |
|---|---|---|
| FuturesScore alone (2600/90) | 55.0% | +144.70 |
| **OptionsScore alone (2600/90)** | **60.8%** | **+446.05** |
| FinalScoreSessionWeighted (2600/93) | 57.4% | +379.80 |

**Verdict: NOT adopted, but the closest of the 4 new combos and a genuine open trade-off worth
revisiting.** It beats FuturesScore alone on both dimensions, and sits between the two parents rather
than diluting below both the way item 10's DTE-weighted blend did — but per the adopt-only-if-both-
improve-on-the-BETTER-parent rule, it falls short of OptionsScore alone on both win rate (57.4% vs.
60.8%) and net (+379.80 vs. +446.05). Unlike items 10-12, this design's weights were never tuned
(first-pass values straight from the doc-comment derivation) — a real weight sweep (finer per-phase
grid, or swapping which phase gets the 0.7/0.3 tilt) is the natural next step before writing this
design off, rather than treating one untuned first-pass result as final.

#### 13a. Follow-up: phase-weight sweep — NOT adopted, kept original 0.7/0.5/0.3

Per item 13's own "worth revisiting" note, the 3 phase weights (Open/Mid/Close weight-on-
FuturesScore, OptionsScore always 1 minus it) were swept rather than left at their untuned
first-pass values. `FinalScoreSessionWeighted` gained temporary-turned-permanent `--wopen=`/
`--wmid=`/`--wclose=` calibrate flags (default 0.7/0.5/0.3, matching the original derivation) so
the grid could run without recompiling. Swept each phase weight in {0.0, 0.3, 0.5, 0.7, 1.0} (125
combos), BarThreshold=2600, entry percentiles 85-97 (the standing 7-20 trades/day target band),
same 2026-09-08→2026-09-19 8-day range.

| Combo (Open/Mid/Close wt-on-Futures) | Percentile | Trades | Win Rate | Net |
|---|---|---|---|---|
| Original 0.7/0.5/0.3 | 93 | 68 | 57.4% | +379.80 |
| Best in-target found: 0.7/0.3/0.3 | 95 | 56 | 60.7% | **+446.10** |
| **OptionsScore alone (locked)** | 90 | 148 | **60.8%** | +446.05 |

**Verdict: NOT adopted, original 0.7/0.5/0.3 weights kept as the default.** No combo in the grid
beat OptionsScore alone on both win rate and net within the target trades/day band. The closest,
0.7/0.3/0.3 at percentile 95, is a near-exact tie on net (+446.10 vs. +446.05) but still loses on
win rate (60.7% vs. 60.8%) — doesn't clear the adopt-only-if-both-improve bar. It's also a fragile
single-cell peak, not a robust plateau: the *same* 0.7/0.3/0.3 weights at the neighboring 90 and 93
percentiles collapse to 46.9% and 43.7% win respectively, while the original 0.7/0.5/0.3 stays in a
tighter 54.0-64.7% band across percentiles 90-97 in the same sweep. Chasing the single best cell
here would repeat the mistake item 11 explicitly checked for and ruled out elsewhere (an arbitrary
stop mistaken for a genuine peak) — so the more stable original weights are kept as the shipped
default rather than the higher-but-fragile candidate. The `--wopen=`/`--wmid=`/`--wclose=` calibrate
flags are kept as a permanent addition (documented in `calibrate`'s own usage text) so a future,
finer or differently-bounded re-sweep doesn't require recompiling again.

**Phase 5 items 10-13 summary: none of the 4 FuturesScore+OptionsScore combinations beat
`OptionsScoreThreeWaySwitch` alone.** OptionsScore remains the strongest single leading candidate on
this 8-day range; no combination design tried here recovers additional edge from FuturesScore beyond
what OptionsScore already captures on its own. Session-weighted blending (item 13) is the one design
that came reasonably close and is flagged as worth a weight-sweep follow-up; the other three
(DTE-weighted blend, both-must-agree in either direction) are confirmed weaker, not just untested.

## Options-side crossover experiment (2026-09-20)

The dual-MA crossover mechanism (originally built only for the futures side's own
`SessionGatedDepthDurationConfirmed` score — see "Crossover experiment" section above) has been
generalized to run on `OptionsScoreThreeWaySwitchMaxPainConfirmed`'s own per-bar score too.
`SimulateCrossoverDayAsync` now takes an optional `VolumeBarMetric scoreMetric` parameter (default:
`SessionGatedDepthDurationConfirmed`, unchanged) plus `bandWidth`/`optionsSwitchTime`; the `crossover`
and `crossover-calibrate` CLI commands expose this as a new `--metric=` named flag (plus `--band=`),
following the standing `--name=value` convention — no new required positional args. The options-score
path reuses `OptionsScoreThreeWaySwitchMaxPainConfirmed`'s exact scoring logic: the Open/Mid/Close
3-way switch computation was factored out of `SimulateDayAsync` into a new shared static method,
`ComputeOptionsThreeWayScore`, called from both the standard percentile-threshold path and the new
crossover path, so the two can never drift out of sync. Entry is gated by Max Pain sign agreement
(same confirmation `OptionsScoreThreeWaySwitchMaxPainConfirmed` already requires), in place of the
futures crossover's own TOB open-window gate.

**Sanity check (no regression):** re-ran `crossover-calibrate 2026-09-08 2026-09-19 2600 8 40 5` (the
locked futures config, `--metric` omitted so it still defaults to `SessionGatedDepthDurationConfirmed`)
— 80 trades, 55.0% win, +277.20 net. This matches the 8-day-range FuturesScore number already recorded
elsewhere in this file (see "Phase 5: FuturesScore + OptionsScore combination", which explicitly notes
the 52.7%/+241.45 figure quoted for this same config was from an earlier, shorter 7-day range — the
8-day range used throughout Phase 5 is the correct comparison base here, and it is unchanged by this
change since the futures code path itself was not touched, only gated behind an `if`).

**Options-crossover sweep**, same 8-day range, `--metric=OptionsScoreThreeWaySwitchMaxPainConfirmed
--band=5`, grid fast∈{6,8,10,12} / slow∈{20,30,40} / threshold∈{5,10,15,20} pts, target 5-20 trades/day:

| BarThreshold | Fast | Slow | Thresh | Trades | Trades/Day | Win% | Net |
|---|---|---|---|---|---|---|---|
| 650 | 8 | 40 | 10 | 88 | 11.0 | 62.5% | +152.20 |
| 1300 | 10 | 30 | 5 | 95 | 11.9 | 60.0% | +136.75 |
| **2600 (best overall)** | **12** | **30** | **5** | 48 | 6.0 | **62.5%** | **+153.95** |
| Standing percentile-threshold (2600/90, for comparison) | — | — | — | 112 | 14.0 | **64.3%** | **+426.40** |

**Verdict: NOT ADOPTED.** No options-crossover combo in the swept grid beats the standing
`OptionsScoreThreeWaySwitchMaxPainConfirmed` percentile-threshold result (64.3% win, +426.40 net) on
BOTH win rate and net — the best in-target combo found (2600/12/30/5) is close on win rate (62.5% vs
64.3%) but far behind on net (+153.95 vs +426.40, on roughly half the trade count), so it fails the
adopt-only-if-both-improve rule outright. Unlike the futures side (where the crossover mechanism found
a real, still-unconfirmed edge over its own percentile-threshold baseline), the crossover reshaping
does not help the options score — plausibly because the 3-way switch's Open/Mid/Close legs already
change formula shape (not just which underlying reading) at each boundary, so a moving-average smooth
across a leg switch is smoothing over a structural break rather than genuine trend noise the way it
does for the futures score's single depth/duration switch. Not swept further given this gap; no
out-of-sample follow-up planned unless a materially different parameter region is proposed.

## Phase 5 combination methods closed out (2026-09-20)
Items 10-13 (DTE-weighted, both-must-agree, futures/options-primary-filter, session-weighted blend) plus the session-weighted blend's follow-up phase-weight sweep (item 13a) are all complete — none beat the standalone `OptionsScoreThreeWaySwitchMaxPainConfirmed` on both win rate and net. **Combination is closed as a line of investigation for now.** Leading candidate remains the standalone options score with the Max Pain confirmation gate.

## Whipsaw-reduction experiment (2026-09-21)

**Backtest-only. Nothing in this section is live.** The locked, live-trading config
(`OptionsScoreThreeWaySwitchMaxPainConfirmed`, 2600/90) is unchanged by this section — no deploy,
no config change, nothing here should be treated as adopted without a separate explicit decision.

**Motivation:** user-reported live symptom — the score swings between extremes and the traded
position flips direction frequently, with some trades holding only 1-2 minutes before
`ScoreInvalidated` fires. First checked the exit-trigger logic itself
(`TradeSimulator.cs` ~line 1478): it already requires the OPPOSITE-direction score to cross the
FULL entry percentile (not a naive zero-cross), the same wide-hysteresis shape the retired
`CoreScoreHysteresisRules` used. So the exit gate is not a narrow/missing hysteresis band — the
whipsaw has to be coming from the RAW per-bar leg values themselves (ATM depth imbalance /
price-signed ΔIV / raw ΔIV) swinging enough, combined with same-day percentile ranking on a
small/early sample, to repeatedly cross both ±90th-percentile extremes within a session. Two
candidate designs were built and evidence-tested against this hypothesis, both strictly
backtest-only (`NiftySignal.VolumeBarData/TradeSimulator.cs`), both reusing
`OptionsScoreThreeWaySwitchMaxPainConfirmed`'s exact scoring/confirmation gate otherwise, so the
comparison isolates just the one change each makes.

**Locked baseline reference** (2026-09-08 to 2026-09-19, 8 trading days, BarThreshold=2600,
EntryPercentile=90, band=5): **112 trades, 64.3% win rate, +426.40 net points, 14.0 trades/day,
8/8 days ≥50% win rate, average hold 11.67 minutes** (hold duration newly computed here from the
same trade log this section's candidates are compared against — not previously recorded).

### Candidate A: smooth the raw leg value before ranking (`...MaxPainConfirmedSmoothed`)

Each leg's raw pre-rank value (depth-imbalance ratio / price-signed ΔIV / raw ΔIV — the exact
quantities `OptionsThreeWayScoreCalculator.ComputeScore` feeds into `SignedRank.Compute`) is run
through a plain N-bar simple moving average (`BarCountRollingMean`, new, bar-count windowed not
time-windowed, since volume bars vary in wall-clock duration) before ranking. SMA chosen over EMA
for a directly interpretable "N bars" parameter matching this file's own bar-count sweep
convention (`trendWindowBars`); not a claim EMA is worse, just untested. Window length swept via
the new `--smoothbars=` flag, same 8-day/2600/90/band=5 config:

| SmoothBars | Trades | Trades/Day | Win% | Net | Days ≥50% win | Avg hold (min) |
|---|---|---|---|---|---|---|
| Baseline (unsmoothed) | 112 | 14.0 | 64.3% | +426.40 | 8/8 | 11.67 |
| 2 | 77 | 9.6 | 64.9% | +266.25 | 6/8 | 18.12 |
| 3 | 66 | 8.25 | 62.1% | +290.45 | 5/8 | 21.99 |
| 5 | 50 | 6.25 | 66.0% | +251.30 | 6/8 | 30.86 |

**Verdict: NOT ADOPTED.** Smoothing clearly reduces trade frequency (14.0/day down to 6.25-9.6/day)
and clearly lengthens average hold (11.67 min up to 18-31 min) — whipsaw is genuinely damped by
this mechanism. But net drops sharply at every window (426 down to 251-290, a 32-41% cut) and
day-consistency degrades from 8/8 to 5-6/8 days ≥50% win — one day (09-15) goes as low as 14.3%
win at SmoothBars=3. Win rate alone stays roughly flat to slightly better (62.1-66.0% vs 64.3%),
but that's not enough on its own: this fails the "adopt only if both win rate AND net improve" bar
badly on net, and also fails on the day-consistency character of the locked baseline. Read as: the
smoothed input is cutting into some of the genuinely fast, genuinely good trades along with the
noise (the baseline's own quick trades are not uniformly bad — several exit at 1-2 minutes with a
solid gain, e.g. the 14:37:45 Put on 09-18 nets +1.5% in 13 seconds), so a blanket N-bar smooth on
the score INPUT is too blunt an instrument here.

### Candidate B: minimum dwell time before an invalidation exit (`...MaxPainConfirmedMinHold`)

Score/confirmation/entry unchanged; once a position opens, a `ScoreInvalidated` exit is blocked
until at least N minutes have elapsed since entry (minutes, not bars, chosen to match this file's
own time-based exit conventions — `ForceCloseAt`/`SessionGateSwitchTime` — since a bar's real
duration varies). The stop-loss and 15:15 force-close are unconditional safety exits, unaffected.
Swept via the new `--minhold=` flag, same 8-day/2600/90/band=5 config:

| MinHold (min) | Trades | Trades/Day | Win% | Net | Days ≥50% win | Avg hold (min) |
|---|---|---|---|---|---|---|
| Baseline (no dwell) | 112 | 14.0 | 64.3% | +426.40 | 8/8 | 11.67 |
| 2 | 92 | 11.5 | 64.1% | +457.45 | 7/8 | 15.92 |
| 3 | 85 | 10.6 | 64.7% | +420.65 | **8/8** | 17.50 |
| 5 | 81 | 10.1 | 63.0% | +409.55 | 7/8 | 19.33 |
| 10 | 72 | 9.0 | 63.9% | +367.30 | 7/8 | 23.09 |

Collateral-damage check (dispatch-order regression guard, since a new metric's confirmation-gate
flag was OR'd into the shared `isOptionsScoreMaxPainConfirmed` condition): re-ran
`SessionGatedDepthDuration` @ 2600/90 after this change — **80 trades, 55.0% win, +144.70 net**,
byte-identical to its previously recorded locked number. No regression.

**Verdict: OPEN TRADE-OFF, leaning toward adoption of MinHold=3 as the next candidate to carry
forward for further (multi-session) evidence, not yet formally adopted.** MinHold=3 is the
standout cell: win rate 64.7% (fractionally *above* the 64.3% baseline), net +420.65 (99.7% of
baseline net, a 1.3% give-up), **8/8 days ≥50% win — matches the locked baseline's own
day-consistency exactly** — while cutting trade frequency from 14.0 to 10.6/day (-24%) and
extending average hold from 11.67 to 17.50 minutes (+50%). That is a real, measurable whipsaw
reduction (fewer trades, longer holds, same day-consistency) at a cost close to noise-level on the
two headline metrics. MinHold=2 is arguably even stronger on net alone (+457.45, +7.3% over
baseline, win rate 64.1% essentially tied) but gives up one day's consistency (09-10 drops to
44.4%), so it does not cleanly clear the strict "adopt only if both improve, matching baseline
day-consistency" bar the way MinHold=3 does. Every dwell value tested reduces trade frequency and
extends hold time monotonically as dwell increases, confirming the mechanism works as intended;
net and day-consistency both erode gradually past MinHold=3 (5 and 10 both drop a day of
consistency and give back more net), suggesting 3 minutes is close to a genuine local sweet spot
on this 8-day sample rather than the edge of a wider plateau — worth re-testing as more days
accumulate per this project's own "one run is a data point, not a verdict" rule, not adopted into
the live config off this single 8-day pass.

### Plain-language summary

Both candidates measurably reduce whipsaw (fewer trades, longer average hold) — the underlying
hypothesis (single-bar noise pushing the same-day percentile rank to false extremes) held up.
**Candidate A (smoothing the score input) reduces whipsaw the most but at a real, consistent cost
to net P&L and day-consistency** — not worth it as tested; a blanket N-bar average on the leg
value throws away some of the genuinely fast, genuinely good trades along with the noise.
**Candidate B (minimum dwell before an invalidation exit) reduces whipsaw with close to zero cost
at MinHold=3** (net -1.3%, win rate +0.4pp, day-consistency unchanged at 8/8) — the more promising
design of the two, and the better mechanism in general for this specific whipsaw symptom, since it
targets the SYMPTOM (a position closing too fast) directly rather than reshaping the score itself.
**Recommendation: do not adopt Candidate A. Treat Candidate B (MinHold=3, and MinHold=2 as a
higher-net/lower-consistency alternative) as a promising open candidate for continued
multi-session evidence-gathering before any live decision** — consistent with this project's
"backtesting is a long-term process, one run is a data point" rule. No change to the currently
deployed live config from this work.

## Risk-rule sweep: SL/TP1/daily-loss for both live strategies (2026-09-21)

Evaluates the retired (pre-volume-bar) live engine's risk-management mechanics
(`NiftySignal.Rules/ExitRuleEvaluator.cs`, `NiftySignal.Rules/RulesetConfigOptions.cs` — per-trade
stop-loss, TP1 profit-trigger partial-booking with trail-to-breakeven, daily-loss-cap) against the
2 strategies now actually live for the first time. These defaults were tuned for the old
Core/legacy composite score and had never been tested against either
`OptionsScoreThreeWaySwitchMaxPainConfirmed` or the futures crossover. **Everything in this section
is backtest-only** — `NiftySignal.VolumeBarData`/CLI-flag additions, nothing wired into
`NiftySignal.Host`/`NiftySignal.Dashboard`.

**Trade-model change**: `VolumeBarTrade` gained 3 nullable fields (`PartialExitTime`/
`PartialExitPrice`/`PartialBookedFraction`) rather than a new wrapping type — a TP1 partial-booked
trade still reports through the same `NetPnlPoints`/`NetPnlPercent` every existing consumer already
reads, now internally a fraction-weighted blend of the partial leg and the final leg
(`(partialPrice-entry)*fraction + (exitPrice-entry)*(1-fraction)`) — the exact "blended realized
P&L" definition the old engine's own TrailAfterPartialBook comment describes, not a new invented
one. Priority order replicated from `ExitRuleEvaluator.Evaluate`: the absolute session-end exits
(`TimeCutoff`/`EndOfData`, this simulator's SquareOff-equivalents) outrank everything; then
StopLoss; then PartialBook; then the metric's own discretionary exit (`ScoreInvalidated`/
`CrossoverReversed`). After a partial books, the stop tightens to breakeven (0%) for the remaining
leg, replicating `TrailAfterPartialBook` exactly. New shared `RiskRuleState` class (per-day,
constructed fresh inside each `SimulateDayAsync`/`SimulateCrossoverDayAsync` call, both of which
already operate one day at a time) holds the TP1 gate and the running daily-realized-P&L check.

**Daily-loss-cap "% of what"**: this simulator trades raw option-premium points with no lot-size or
rupee-capital concept of its own. Rather than invent a denominator, `--dailyloss=N` is read as N%
of `NiftySignal.Rules.CapitalConfigOptions`'s own real live defaults (Total=Rs 50,000, LotSize=65,
LotsPerTrade=2), converted to a points threshold via `(N/100 * 50,000) / (65*2)` — e.g.
`--dailyloss=20` is a ~76.9-point cap on a day's realized (closed trades + booked partial legs) P&L.
This is a one-way, backtest-only unit conversion (reads a real number from `NiftySignal.Rules`,
never writes to it) — documented explicitly since this project's own sweep discipline requires
saying what the denominator is, not just the percent.

**New CLI flags** (all off by default, named per the standing `--name=value` convention):
`--tp1pct=N` (TP1 profit trigger, percent of entry premium), `--tp1frac=N` (fraction of quantity
booked at TP1, percent), `--dailyloss=N` (daily-loss-cap, percent per the conversion above). `--stop=`
already existed on `trade`; it was previously **missing entirely** from `crossover`/
`crossover-calibrate` (no stop-loss parameter, no stopped-out check) — added there now following
the identical pattern.

**Mandatory regression check (all new flags OFF)**: `trade 2026-09-08 2026-09-19
OptionsScoreThreeWaySwitchMaxPainConfirmed 90 15 2600 --band=5` reproduced **112 trades, 64.3% win,
+426.40 net** exactly. `crossover 2026-09-08 2026-09-19 8 40 5 2600` reproduced **80 trades, 55.0%
win, +277.20 net** exactly (re-verified fresh against the current 100-150-strike-search-merged
`TradeSimulator.cs`, not assumed) — both byte-identical to the locked baselines, confirming no
dispatch-order regression from threading `RiskRuleState` through both methods' entry/exit chains.
`dotnet test`: **661/661 passing** (651 pre-existing + 10 new `RiskRuleStateTests`), 0 warnings,
`dotnet build` clean.

Both strategies actually only produced trades on **8 trading days** in the 2026-09-08→2026-09-19
range (08, 09, 10, 11, 15, 16, 17, 18 — 09-12/09-13 weekend, and the range's own 09-19 tail had no
qualifying entries for either strategy that day). 0-DTE days per the existing project convention:
**08, 15**; non-0-DTE: **09, 10, 11, 16, 17, 18** (6 days).

### Options (`OptionsScoreThreeWaySwitchMaxPainConfirmed` @ 2600/90, band=5) — stop-loss sweep

| Stop | Trades | Win% | Net |
|---|---|---|---|
| none (baseline) | 112 | 64.3% | +426.40 |
| 5% | 130 | 54.6% | +261.45 |
| 10% | 119 | 62.2% | +364.10 |
| 15% | 114 | 64.0% | +426.15 |
| **20%** | 113 | **64.6%** | **+429.15** |
| 30% | 112 | 64.3% | +426.40 (never binds — identical to baseline) |

A tight stop (5-10%) clearly hurts both metrics — this strategy's real winning trades routinely
draw down more than 10% intraday before recovering (see the 90-97% avg-hold win rates elsewhere in
this file), so a tight stop cuts them off early and converts them to losses, also adding MORE
trades (more StopLoss-triggered re-entries) at a WORSE win rate. 20% is the only value in the grid
that clears the strict "adopt only if both win rate and net improve" bar, but only marginally
(+0.3pp win rate, +0.65% net) — well within one-8-day-sample noise, not a confident edge.

### Options — TP1 sweep (profit trigger × booked fraction)

| Trigger\Fraction | 25% | 50% | 75% |
|---|---|---|---|
| 10% | 114tr/63.2%/+354.58 | 114tr/64.0%/+316.35 | 114tr/64.0%/+278.13 |
| 15% | 113tr/**65.5%**/+385.53 | 113tr/65.5%/+354.75 | 113tr/65.5%/+323.98 |
| 20% | 112tr/64.3%/+398.58 | 112tr/64.3%/+370.75 | 112tr/64.3%/+342.93 |
| 25% | 112tr/64.3%/+398.16 | 112tr/64.3%/+384.53 | 112tr/64.3%/+370.89 |

**No TP1 setting beats the no-rules baseline on net** (best net is +398.58 at 20%/25%, still below
+426.40) — TP1 consistently trades away upside (locking in a partial early caps the trade's best
outcome) for a small, consistent win-rate bump (up to +1.2pp at 15%/any fraction). A clean,
consistent trade-off, not noise: within every trigger row, net strictly decreases as the booked
fraction rises from 25%→50%→75% (locking in more early = giving up more upside), and win rate is
flat within a trigger row (the fraction doesn't change whether a trade "wins," only how much).
Fails the strict adopt bar at every cell tested.

### Options — daily-loss-cap sweep

| Cap | Trades | Win% | Net |
|---|---|---|---|
| 1% (~3.8 pts) | 77 | 63.6% | +355.70 |
| 2% (~7.7 pts) | 85 | 64.7% | +378.80 |
| 3% (~11.5 pts) | 106 | 64.2% | +413.75 |
| 5% (~19.2 pts) | 110 | 63.6% | +404.45 |
| 7% (~26.9 pts) | 112 | 64.3% | +426.40 (baseline) |
| 10/15/20/30% | 112 | 64.3% | +426.40 (never binds) |

The cap only ever binds below ~7% of the documented reference capital (~27 points/day) on this
8-day sample — this strategy's worst single-day realized drawdown never got worse than that. Where
it DOES bind (1-5%), it always cuts both trades and net; only the 2% cell shows a win-rate bump
(+0.4pp) and even there net drops by $47.60. No cell clears the strict adopt bar; a daily-loss-cap
in the double-digit-percent range documented here is pure headroom for this strategy on this
sample, not a real behavioral change — worth re-testing as single-day tail risk shows up in more
data, per this project's "one run is a data point" rule.

### Options — combos tried (stop + TP1)

Tried combining the single best-in-target lever (stop=20%) with the best-win-rate TP1 cell
(15%/25%) and a couple of neighbors, since TP1 alone never wins on net but does lift win rate —
hypothesis was that layering it on top of the one stop value that already clears the bar might
still hold net roughly flat while adding win-rate. Rejected: every combo tried gives back MORE net
than stop=20 keeps by adding win rate, same TP1 upside-capping trade-off as the standalone TP1
sweep, just starting from a slightly higher base:

| Combo | Trades | Win% | Net |
|---|---|---|---|
| stop=20 alone (best single lever) | 113 | 64.6% | **+429.15** |
| stop=20 + tp1=15%/25% | 114 | 65.8% | +388.28 |
| stop=20 + tp1=25%/25% | 113 | 64.6% | +400.91 |
| stop=15 + tp1=15%/25% | 114 | 65.8% | +394.03 |

**No combo beats stop=20 alone.** Not pursued further (per the "use judgment, don't
combinatorially explode" instruction) — TP1's upside-capping cost dominates every combo tried.

### Options — DTE-conditioning check (stop=20%, the one lever that cleared the bar)

| Bucket | Baseline (no stop) trades/win%/net | stop=20% trades/win%/net |
|---|---|---|
| 0-DTE (08, 15) | 25 / 64.0% / +157.30 | 25 / 64.0% / +161.45 |
| Non-0-DTE (09,10,11,16,17,18) | 87 / ~64.4% / +269.10 | 88 / ~64.8% / +267.70 |

**No real DTE-conditioned effect.** stop=20% moves both buckets in roughly the same small
direction (net essentially flat, win rate up a hair in both) — the tiny overall improvement is not
concentrated in one day-type, it's spread evenly. A single shared 20% stop performs the same as a
DTE-conditioned pair would here; splitting it by DTE is not worth the added complexity on this
evidence.

### Futures crossover (`SessionGatedDepthDurationConfirmed`, 8/40/5pt @ 2600) — stop-loss sweep

| Stop | Trades | Win% | Net |
|---|---|---|---|
| none (baseline) | 80 | 55.0% | +277.20 |
| 5% | 111 | 39.6% | +114.35 |
| 10% | 93 | 49.5% | +240.55 |
| 15% | 88 | 52.3% | +275.10 |
| 20% | 85 | 52.9% | **+285.60** |
| 30% | 81 | 55.6% | +281.70 |

Same shape as the options side (tight stops clearly hurt), but **no value clears the strict
adopt-only-if-both-improve bar** here — 20% gives the best net (+3.0% over baseline) but at a real
win-rate cost (52.9% vs 55.0%, -2.1pp), and 30% recovers win rate (55.6%, +0.6pp) but at lower net
than baseline-adjacent 15%. This is a genuine trade-off worth recording even though it doesn't
clear the bar, per this project's "report trade-offs honestly" convention: a tighter stop here
trades win-rate consistency for a small net edge, unlike the options side where 20% cleanly won on
both.

### Futures crossover — TP1 sweep

| Trigger\Fraction | 25% | 50% | 75% |
|---|---|---|---|
| 10% | 85tr/57.6%/+196.80 | 85tr/57.6%/+174.85 | 85tr/57.6%/+152.90 |
| 15% | 84tr/57.1%/+213.19 | 84tr/57.1%/+201.58 | 84tr/57.1%/+189.96 |
| 20% | 83tr/56.6%/+231.65 | 83tr/56.6%/+227.80 | 83tr/56.6%/+223.95 |
| 25% | 81tr/55.6%/+263.88 | 81tr/55.6%/+263.88 | 81tr/55.6%/+239.43 |

Same pattern as options: TP1 lifts win rate (up to +2.6pp at trigger=10%) but never recovers enough
net to beat baseline (+277.20) at any cell — the closer the trigger gets to a level the strategy
rarely reaches (25%), the closer it converges back toward baseline (fewer partials actually fire),
consistent with the mechanism working as intended rather than a bug.

### Futures crossover — daily-loss-cap sweep

| Cap | Trades | Win% | Net |
|---|---|---|---|
| 1% | 41 | 46.3% | +45.35 |
| 2% | 59 | 52.5% | +203.05 |
| 3% | 59 | 52.5% | +203.05 |
| 5% | 77 | 54.5% | +244.05 |
| 10%+ | 80 | 55.0% | +277.20 (never binds) |

Strictly worse the tighter it's set — this strategy's realized-P&L path apparently has more
single-day drawdown texture than the options side (the cap starts biting at a looser threshold, 5%
vs options' ~7%, and every bind cuts both win rate and net, no exception). No adopt case here at
any setting tested.

### Futures crossover — DTE-conditioning check (stop=20%)

| Bucket | Baseline (no stop) trades/win%/net | stop=20% trades/win%/net |
|---|---|---|
| 0-DTE (08, 15) | 20 / 55.0% / +83.35 | 24 / 50.0% / +87.20 |
| Non-0-DTE (09,10,11,16,17,18) | 60 / 55.0% / +193.85 | 61 / ~54.1% / +198.40 |

**No real DTE-conditioned effect here either** — the win-rate cost of stop=20% shows up in BOTH
buckets (0-DTE -5.0pp, non-0-DTE -0.9pp — a bit more pronounced on 0-DTE days, but not a clean
split where one bucket improves and the other doesn't), and net moves up modestly in both. A
DTE-conditioned rule is not indicated by this evidence.

### Overall verdict and recommendation

**Do not adopt TP1 partial-booking or the daily-loss-cap for either strategy off this evidence** —
neither ever beats its strategy's own no-rules baseline on net, at any setting tried, on either
strategy; TP1's cost (capping upside on the exact large winning trades that currently drive most of
each strategy's net) is a structural, consistent trade-off, not noise, and the daily-loss-cap is
pure unused headroom in the percent ranges normally considered reasonable (see F42's own note below
on why this project's `RiskLimitsConfigOptions` defaults are already unusually wide).

**Stop-loss is a mild, genuine candidate for the options strategy specifically** (20%: 64.3%→64.6%
win, +426.40→+429.15 net — clears the strict bar, but only just, on one 8-day sample) and a
**flagged, NOT-adopted trade-off for the crossover strategy** (20%: net improves +3.0% but win rate
drops 2.1pp — a real trade-off, not a win). DTE-conditioning was checked explicitly for stop-loss
(the only lever showing any real effect) on both strategies and found **no differential effect** —
the effect, where it exists, is roughly uniform across 0-DTE and non-0-DTE days, so a single shared
threshold is not worse than a DTE-split pair here.

**Recommendation: adopt nothing yet.** The options-side 20% stop is the single most promising
number in this sweep, but a 0.3-point win-rate/0.65% net edge on one 8-day sample is well inside
this project's own "one run is a data point, not a verdict" standard — worth carrying forward into
the next several backtest sessions as more days accumulate, not worth a live config change today.
No change to either live strategy's config from this work; all of it stays behind the new,
off-by-default `--stop=`/`--tp1pct=`/`--tp1frac=`/`--dailyloss=` flags in `NiftySignal.VolumeBarData`.

**Re: audit finding F42** (`NiftySignal.Rules/RulesetConfigOptions.cs`'s own `RiskLimitsConfigOptions`
comment) — the old engine's `MaxDailyLossPct=20%`/`MaxDailyProfitPct=30%` were flagged as
deliberately widened for paper-trading observation, "must be tightened before real money." This
sweep's own finding is consistent with that flag: at the old engine's literal 20% daily-loss value,
the cap never binds at all for either strategy on this sample (pure headroom), reinforcing that F42's
concern (these numbers are not a real risk box as configured) transfers cleanly to the new
strategies too, not just the old composite score this default was originally tuned for.

## MAE/MFE analysis, all 4 strategies (2026-09-22)

New CLI command, purely additive analytics on top of already-existing trade output (no change to
any strategy's entry/exit/dispatch logic):

```
dotnet run --project NiftySignal.VolumeBarData -- mae-mfe trade <fromDate> <toDate> <metric> [entryPercentile] [trendWindowBars] [barVolumeThreshold] [--band=] [--minprice=] [--maxprice=] ...
dotnet run --project NiftySignal.VolumeBarData -- mae-mfe crossover <fromDate> <toDate> [fastBars] [slowBars] [thresholdPoints] [barVolumeThreshold] [--metric=] [--band=] [--minprice=] [--maxprice=] ...
```

Both accept the exact same parameters as the existing `trade`/`crossover` commands, reuse
`RunRangeAsync`/`TradeSimulator.SimulateCrossoverDayAsync` verbatim for trade generation, then for
each closed trade reconstructs the traded instrument's `Token` (not stored on `VolumeBarTrade`) by
re-querying the day's option chain on `AsOfDate + StrikePrice + OptionType` (unique, since this
codebase's convention is one expiry in play per day), loads that instrument's real tick price path
for exactly the `EntryTime..ExitTime` window (`OptionPriceSeries`, extended with a new
`AllPrices` property alongside its existing point-lookup `PriceAtOrBefore`), and computes MAE/MFE
via the new, separately-unit-tested `MaeMfeCalculator.Compute` (5 tests in
`NiftySignal.Tests/VolumeBarData/MaeMfeCalculatorTests.cs`, `dotnet test`: 666 passing, 0 warnings,
up from the 661 baseline). MAE/MFE are both LONG-premium points/percent relative to `EntryPrice`
(clamped at 0 — a trade that never moved adversely has MAE 0, not negative), per
`VolumeBarTrade.NetPnlPoints`'s own doc comment confirming every trade here is a LONG position
regardless of Call/Put.

All 8 requested trade sets were reproduced first and matched their known-good reference numbers
exactly before their MAE/MFE numbers were trusted:

| # | Set | Trades | Win% | Net | Reference matched? |
|---|---|---|---|---|---|
| 1 | OptionsScore backtest, no strike-search | 112 | 64.3% | +426.40 | Yes |
| 2 | OptionsScore backtest, --minprice=100 --maxprice=150 | 112 | 64.3% | +452.45 | Yes |
| 3 | OptionsScore out-of-sample (09-21), no strike-search | 7 | 71.4% | -5.10 | Yes |
| 4 | OptionsScore out-of-sample (09-21), strike-search | 7 | 71.4% | -5.75 | Yes |
| 5 | Futures crossover backtest, no strike-search | 80 | 55.0% | +277.20 | Yes (current ground truth, no fixed reference given — reproduced against the plain `crossover` command's own current output) |
| 6 | Futures crossover backtest, strike-search | 80 | 55.0% | +320.85 | Yes |
| 7 | Futures crossover out-of-sample (09-21), no strike-search | 10 | 30.0% | -12.50 | Yes |
| 8 | Futures crossover out-of-sample (09-21), strike-search | 10 | 40.0% | -8.55 | Yes |

### MAE/MFE summary statistics

| # | Avg MAE% | Avg MFE% | Median MAE% | Median MFE% | Worst MAE% | Best MFE% | Recovered¹ | Gave back² |
|---|---|---|---|---|---|---|---|---|
| 1 | 4.67% | 7.50% | 3.56% | 3.22% | 31.01% | 71.50% | 105/112 | 101/112 |
| 2 | 4.26% | 6.58% | 3.54% | 3.19% | 21.87% | 54.71% | 105/112 | 101/112 |
| 3 | 7.57% | 5.84% | 6.17% | 7.10% | 21.47% | 10.26% | 7/7 | 7/7 |
| 4 | 5.91% | 5.14% | 5.03% | 6.11% | 16.40% | 8.64% | 7/7 | 7/7 |
| 5 | 6.82% | 11.21% | 3.76% | 5.54% | 44.39% | 66.69% | 75/80 | 79/80 |
| 6 | 5.70% | 9.15% | 3.58% | 4.54% | 26.47% | 53.11% | 76/80 | 77/80 |
| 7 | 7.45% | 8.69% | 4.75% | 2.90% | 34.04% | 58.71% | 10/10 | 9/10 |
| 8 | 6.19% | 6.38% | 3.94% | 2.85% | 31.11% | 40.50% | 10/10 | 8/10 |

¹ Trades where MAE exceeded the trade's own final loss magnitude (touched a worse drawdown than it
closed at — includes winners that dipped before recovering). ² Trades where MFE exceeded the
trade's own final gain (gave back some open profit before exit).

### Notable patterns

- **Almost every trade in every set touches a worse point than its close.** The "recovered"
  fraction is 90%+ in all 8 sets (lowest is set 5's 75/80 = 93.75%) — essentially every trade,
  winner or loser, dips below its own final P&L at some point during the hold. This is expected
  for a strategy holding through noise rather than trailing a tight stop, but it means MAE is a
  poor predictor of a trade's eventual outcome on its own; it fires on almost everything.
- **The crossover strategy runs both bigger MFE and bigger MAE than the options-score strategy on
  the same 8-day backtest window** (set 5: avg MFE% 11.21% vs set 1's 7.50%; avg MAE% 6.82% vs
  4.67%). Consistent with the crossover strategy's generally longer average hold and larger
  average move — the single-metric `NiftySignal.MetricTrials` track's own 1-8 minute average trade
  durations (a lone-metric, no-composite simulation, not this multi-bar crossover approach) sit at
  the opposite extreme, underlining that a longer hold gives the underlying more time to wander
  before exit, both ways.
- **The crossover out-of-sample day (set 7, no strike-search) has the widest MAE spread relative
  to its win rate of any set**: only 30% win rate, yet every single trade (10/10) touched a worse
  drawdown than its close, and the worst MAE (34.04%, on a Rs 89.00 Put that fell to Rs 61.35,
  entry 10:33:49) came within striking distance of being the day's biggest loser outright. This is
  the "losing trades had deeper MAE than the backtest's typical losing trade" pattern flagged
  up-front — the same pattern doesn't show up nearly as sharply in the 8-day backtest sets (1, 2,
  5, 6), where the worst single-trade MAE% (31–44%) is driven by a handful of outliers, not a
  day-wide pattern.
- **Two illustrative individual trades** (both from set 1, the plain OptionsScore backtest):
  - 09:38:49, Long Put@23500, entry 85.60 → exit 137.20, net **+51.60** (a big winner) — but it
    first drew down **13.00 pts (15.19%)** before rallying to a peak MFE of **61.20 pts (71.50%)**,
    the best MFE in the whole set. A trader watching the open position mid-trade would have seen a
    15% loss before it became the day's best winner.
  - 14:47:09, Long Call@23200, entry 40.95 → exit 28.60, net **-12.35** — its MAE (12.70 pts) is
    **31.01% of entry**, the worst MAE% in the set, on a cheap (~Rs 41) option that fell almost
    monotonically (MFE only 3.55 pts / 8.67%, so it barely bounced at all). Cheap premiums produce
    large percentage swings on comparatively small point moves — a reminder that "avg MAE%"
    numbers above are pulled around by low-premium trades more than point-based MAE would be.
- **Strike-search (`--minprice=100 --maxprice=150`) trades toward cheaper, further-OTM strikes
  having somewhat lower percentage MAE/MFE than the plain-ATM sets** in 3 of 4 matched pairs
  (sets 1→2, 5→6, 7→8 all show avg MAE%/MFE% decrease with strike-search on), likely because the
  band search is selecting strikes whose premiums move less violently in percentage terms for the
  same underlying move — consistent with, though not proof of, the strike-search mechanism
  behaving as designed (picking a specific premium range rather than pure ATM). Set 3→4 is the one
  exception (MAE% drops but so does the day's already-small sample of 7 trades' variance
  generally) — one out-of-sample day is not enough to generalize this observation past "worth
  watching across more days."

Full per-trade tables for all 8 sets are reproducible on demand via the commands above (not
reproduced row-by-row here); the exact commands are listed in the reproduction table.

## Part A: score smoothing on the locked OptionsScore (2026-09-22)

**Research only, provisional, not adopted.** Everything in this section is backtest-only
(`NiftySignal.VolumeBarData`), off by default, additive to `TradeSimulator.cs`. The locked
live-trading config (`OptionsScoreThreeWaySwitchMaxPainConfirmed` @ 2600/90, band=5) and its own
score computation (`ComputeOptionsThreeWayScore`/`OptionsThreeWayScoreCalculator`, the Max Pain
gate) are byte-for-byte unchanged -- reverified below. No `NiftySignal.Host`/`NiftySignal.Dashboard`
file was touched, and nothing was deployed or redeployed.

### Design decision: what gets smoothed, and how entry is gated

Distinct from the already-rejected 2026-09-21 Candidate A
(`OptionsScoreThreeWaySwitchMaxPainConfirmedSmoothed`), which smoothed each leg's RAW PRE-RANK
value (the quantity fed into `SignedRank.Compute`) -- this task smooths the ALREADY-COMPUTED,
ALREADY-PERCENTILE-SHAPED scaled score (`100 * ComputeOptionsThreeWayScore(...)`, the exact value
`SimulateDayAsync` derives every bar and currently gates entry on directly). Two new
`VolumeBarMetric` values were added, both reusing `ComputeOptionsThreeWayScore`/the Max Pain
confirmation gate completely unchanged:

- `OptionsScoreThreeWaySwitchMaxPainConfirmedScoreSma` -- the scaled score run through
  `BarCountRollingMean` (reused verbatim from the 2026-09-21 Candidate A helper), a plain N-bar
  simple moving average.
- `OptionsScoreThreeWaySwitchMaxPainConfirmedScoreEma` -- the scaled score run through the new
  `ExponentialMean` (`NiftySignal.VolumeBarData/TradeSimulator.cs`), a standard EMA with smoothing
  constant `alpha = 2 / (N + 1)` (the conventional "N-period EMA" formula), first observation
  seeding the EMA directly (no synthetic warm-up).

**Why re-rank the smoothed value instead of thresholding it directly.** A `SignedRank` output is
percentile-shaped by construction, but an AVERAGE of percentile-shaped values does not itself
preserve that shape -- the same reasoning this file's own `Composite`/`OptionsScoreBlend`/
`TrendReversion` metrics already establish for their own dedicated magnitude-rank trackers (see
each enum value's own doc comment). So the smoothed value is re-ranked through its own dedicated
`SessionRankTracker` (`scoreSmoothMagnitudeRank`), and entry/exit still gate on
`Percentile(|Smoothed(scaledScore, N)|) >= entryPercentile` -- the SAME dynamic, self-calibrating
percentile-threshold mechanism every other metric in this file uses, per CLAUDE.md's "no hardcoded
thresholds" rule, rather than inventing a fixed-magnitude gate.

**Implementation mechanism.** `TradeSimulator.SimulateDayAsync` overwrites the local `scaledScore`
variable with its smoothed value immediately after computing it (right after
`var scaledScore = score is { } s ? 100.0 * s : null;`), before anything else in that bar's
iteration reads it. Every downstream consumer -- the percentile calc, the exit's opposite-extreme
`ScoreInvalidated` check, the entry side/sign (Call vs. Put), the Max Pain confirmation gate's
sign-agreement check, `onBarEvaluated`, and `VolumeBarTrade.EntryScore` -- therefore automatically
operates on the smoothed series, not the raw single-bar value, satisfying the task's own
instruction that "entry rules must use the smoothed value... not the raw single-bar percentile"
without threading a second variable through the rest of the method. Both new metrics reuse the
EXISTING `--smoothbars=N` CLI flag (already wired through `trade`/`mae-mfe trade`/`calibrate`) for
the window/EMA-length parameter, rather than adding a second flag -- the three smoothing metrics
(leg-level `Smoothed`, `ScoreSma`, `ScoreEma`) are mutually exclusive per call and all want "an
N-bar window" in the same units. Defaults to 3 bars when omitted (same default as the 2026-09-21
candidate).

**Variant 3 (dual-MA crossover): already built, reused directly, no new code.** Checked per the
task's own instruction -- `SimulateCrossoverDayAsync`'s existing `scoreMetric` parameter already
supports `OptionsScoreThreeWaySwitchMaxPainConfirmed` (added 2026-09-20 for a different purpose,
Max-Pain-sign-gated futures/options comparability), which runs fast/slow simple moving averages of
this exact scaled-score series with a `thresholdPoints` crossing-gap filter -- this IS variant 3
as specified. No new code was written for it; only the sweep below is new.

Mandatory regression check (all new metrics unused, baseline unchanged): `trade 2026-09-08
2026-09-19 OptionsScoreThreeWaySwitchMaxPainConfirmed 90 15 2600 --band=5` reproduced **112 trades,
64.3% win, +426.40 net** exactly. `trade ... OptionsScoreThreeWaySwitchMaxPainConfirmedMinHold ...
--minhold=3` reproduced **85 trades, 64.7% win, +420.65 net** exactly. `trade ...
SessionGatedDepthDuration ...` (collateral-damage check, unrelated dispatch branch) reproduced
**80 trades, 55.0% win, +144.70 net** exactly. `dotnet test`: **680/680 passing** (673 pre-existing
+ 7 new `ExponentialMeanTests`, which also cover `BarCountRollingMean`'s null-handling
convention), 0 warnings, `dotnet build` clean.

### Variant 3 (crossover) threshold note

`SimulateCrossoverDayAsync` requires a `thresholdPoints` crossing-gap filter with no percentile
concept at all (unlike variants 1-2). A quick calibration at the default fast=4/slow=12 combo found
thresholds below ~15 fire 16-39 trades/day (far above CLAUDE.md's 5-10/day target band) and
thresholds at/above ~25 collapse to under 2 trades/day; **thresholdPoints=15** was fixed for the
entire fast/slow grid below (lands the default fast=4/slow=12 combo at 8.75 trades/day, inside the
target band) -- the task's own combo grid is fast x slow only, so threshold itself was not swept
further.

### Full sweep: variant 1 (SMA) and variant 2 (EMA), N in {3,4,5,6}

8-day backtest (2026-09-08 to 2026-09-19) and out-of-sample day (2026-09-21), both @ 2600/90,
band=5:

| Variant | N | Backtest trades | Backtest win% | Backtest net | Days >=50% win | OOS trades | OOS win% | OOS net |
|---|---|---|---|---|---|---|---|---|
| Baseline (unsmoothed) | - | 112 | 64.3% | +426.40 | 8/8 | 7 | 71.4% | -5.10 |
| SMA | 3 | 42 | 61.9% | +295.40 | 6/8 | 7 | 42.9% | +12.85 |
| SMA | 4 | 33 | 63.6% | +280.80 | 6/8 | 5 | 60.0% | +3.60 |
| SMA | 5 | 27 | 59.3% | +328.55 | 7/8 | 2 | 50.0% | -12.95 |
| SMA | 6 | 19 | 68.4% | +221.85 | 7/8 | 3 | 66.7% | -3.80 |
| EMA | 3 | 35 | 68.6% | +274.55 | **8/8** | 6 | 50.0% | +9.10 |
| EMA | 4 | 29 | 65.5% | +202.30 | 6/8 | 6 | 50.0% | +8.90 |
| EMA | 5 | 26 | 61.5% | +191.40 | 5/8 | 5 | 60.0% | +11.85 |
| EMA | 6 | 23 | 65.2% | +206.05 | 6/8 | 4 | 75.0% | +16.15 |

Days>=50% win, per SMA/EMA cell (derived from the per-day breakdown, not re-tabulated row by row):
SMA3 fails on 09-11/09-15; SMA4 fails on 09-11/09-18; SMA5/SMA6 fail only on 09-18; EMA3 fails on
none (matches baseline's own 8/8); EMA4 fails on 09-11/09-18; EMA5 fails on 09-11/09-15/09-18;
EMA6 fails on 09-11/09-18.

**No SMA or EMA cell clears the strict "adopt only if both win rate and net improve" bar** -- every
cell trades far fewer signals than baseline (19-42 vs. 112, since smoothing suppresses many
borderline single-bar percentile crossings that used to qualify), and net drops 23-55% at every
cell even where win rate improves. This mirrors the 2026-09-21 leg-level Candidate A's own
rejection shape (net down 32-41% there) -- smoothing at the score level is not meaningfully less
costly than smoothing at the leg level on this sample.

**EMA N=3 is the standout candidate among the 8 SMA/EMA cells**, on day-consistency specifically:
it is the ONLY cell matching baseline's 8/8 days >=50% win rate, and it has the best win-rate lift
of any SMA/EMA cell on the OOS-adjacent metric (68.6% vs. baseline 64.3%, +4.3pp) at real net cost
(+274.55 vs +426.40, -35.6%). SMA N=5 has the best raw net among the smoothed cells (+328.55, only
-23.0% vs baseline) but gives up a day of consistency (09-18 drops below 50%) and trades far less
often (3.4/day, well below the 5-10/day band). Neither is a clean win; both are real trade-offs
recorded honestly, per this project's own "report trade-offs, don't force a winner" convention.

### Full sweep: variant 3 (dual-MA crossover), fast in {3,4,5} x slow in {8,9,10,11,12}, threshold=15

8-day backtest:

| Fast | Slow | Trades | Win% | Net |
|---|---|---|---|---|
| 3 | 8 | 131 | 53.4% | +177.00 |
| 3 | 9 | 132 | 50.0% | +3.50 |
| 3 | 10 | 138 | 48.6% | -52.50 |
| 3 | 11 | 145 | 51.0% | +69.75 |
| 3 | 12 | 135 | 46.7% | +8.30 |
| 4 | 8 | 70 | 47.1% | +49.05 |
| 4 | 9 | 74 | 60.8% | +137.20 |
| 4 | 10 | 71 | 46.5% | +8.25 |
| 4 | 11 | 74 | 51.4% | +94.10 |
| 4 | 12 | 70 | 45.7% | +69.95 |
| 5 | 8 | 54 | **61.1%** | **+200.20** |
| 5 | 9 | 44 | 56.8% | +83.05 |
| 5 | 10 | 46 | 50.0% | -28.05 |
| 5 | 11 | 35 | 48.6% | +16.80 |
| 5 | 12 | 41 | 41.5% | -30.80 |

Out-of-sample day (2026-09-21), same 15 combos:

| Fast | Slow | Trades | Win% | Net |
|---|---|---|---|---|
| 3 | 8 | 11 | 36.4% | -28.80 |
| 3 | 9 | 11 | 72.7% | -7.80 |
| 3 | 10 | 13 | 30.8% | -22.20 |
| 3 | 11 | 11 | 63.6% | -10.90 |
| 3 | 12 | 15 | 40.0% | -37.85 |
| 4 | 8 | 8 | 12.5% | -32.65 |
| 4 | 9 | 7 | 28.6% | -36.60 |
| 4 | 10 | 8 | 37.5% | -26.05 |
| 4 | 11 | 8 | 50.0% | +0.95 |
| 4 | 12 | 7 | 71.4% | -6.95 |
| 5 | 8 | 2 | 50.0% | -19.90 |
| 5 | 9 | 4 | 25.0% | -9.90 |
| 5 | 10 | 4 | 0.0% | -46.90 |
| 5 | 11 | 4 | 25.0% | -13.00 |
| 5 | 12 | 2 | 0.0% | -9.90 |

**Verdict: crossover (variant 3) is clearly the weakest of the three variants.** No backtest cell
gets remotely close to baseline's net (+426.40) or win rate (64.3%) -- the best cell (fast=5/
slow=8: 54 trades, 61.1% win, +200.20, -53.0% net vs baseline) is still a large give-up. Every
single one of the 15 combos is NET NEGATIVE on the out-of-sample day, with several posting
catastrophic single-day losses (-46.90 on 5/10) -- a much worse out-of-sample picture than either
SMA/EMA (which stayed positive on OOS in most cells) or MinHold=3 (-9.25, small). This is
consistent with the dual-MA mechanism requiring several consecutive bars of sustained directional
score movement to cross, which this metric's already-fast-moving percentile-threshold entries
don't naturally provide -- the crossover shape fits the futures side's own `SessionGatedDepthDuration`
score (its original design target) better than a metric already built around single-bar percentile
extremes. Not carried forward.

### MinHold=3 comparison (re-run for apples-to-apples MAE/MFE, DTE, and session-phase splits)

The 2026-09-21 whipsaw-reduction finding (85 trades, 64.7% win, +420.65 net, 8/8 days) is
reconfirmed byte-identical (see regression check above). New splits computed here for the first
time:

| Split | Trades | Win% | Net |
|---|---|---|---|
| 0-DTE (09-08, 09-15) | 22 | 63.6% | +158.45 |
| Non-0-DTE (09-09/10/11/16/17/18) | 63 | 65.1% | +262.20 |
| Session: Open (<10:00) | 10 | 60.0% | +121.95 |
| Session: Mid (10:00-13:30) | 45 | 64.4% | +275.05 |
| Session: Close (>=13:30) | 30 | 66.7% | +23.65 |

Robust across both DTE regimes and all three session phases (positive everywhere, no single-segment
dependency) -- matches the baseline's own broad-based character, not a surprise since MinHold only
gates the exit side.

### MAE/MFE comparison: baseline vs. MinHold=3 vs. best smoothing candidate (EMA N=3)

Reused the existing `mae-mfe trade` command unchanged:

| Set | Trades | Avg MAE% | Avg MFE% | Median MAE% | Median MFE% |
|---|---|---|---|---|---|
| Baseline (unsmoothed) | 112 | 4.67% | 7.50% | 3.56% | 3.22% |
| MinHold=3 | 85 | 6.32% | 10.07% | 4.85% | 6.05% |
| ScoreEma N=3 | 35 | 9.88% | 13.57% | 5.98% | 11.94% |
| ScoreEma3 + MinHold=3 (combo, see below) | 33 | 10.36% | 14.25% | 6.12% | 12.21% |

**Finding, stated plainly: smoothing/dwell do NOT make trades calmer -- they make the SURVIVING
trades' own excursions LARGER, not smaller.** This is the opposite of what "reduce whipsaw" might
suggest about individual-trade volatility, and worth reporting exactly because it's a real,
measured result rather than the hoped-for outcome. The mechanism is straightforward once measured:
both smoothing and dwell suppress the FAST, LOW-EXCURSION trades that used to exit within a few
minutes of entry (the baseline's own quick, small-MAE/MFE trades) while leaving the SLOWER, HIGHER-
EXCURSION trades intact and, on average, held even longer -- so the surviving trade population's
average intra-trade wander goes UP, not down, even though trade FREQUENCY goes down. Trade-count
reduction and average-hold-time extension are real (already established 2026-09-21 for MinHold);
per-trade calmness is not.

### Day-by-day stability (>=50% win rate, matching the "8/8" bar)

| Config | Days >=50% win |
|---|---|
| Baseline | 8/8 |
| MinHold=3 | 8/8 |
| SMA N=3/4 | 6/8 |
| SMA N=5/6 | 7/8 |
| EMA N=3 | **8/8** |
| EMA N=4/6 | 6/8 |
| EMA N=5 | 5/8 |
| Crossover (best cell, 5/8) | not tracked per-day (single aggregate; day-level detail not pulled for the weakest variant, per "use judgment on what's worth the detail" instruction) |
| ScoreEma3 + MinHold=3 (combo) | **8/8** |

### Concentration (top trade / top 2 trades as % of net)

| Config | Net | Top 1 trade | Top 1 % | Top 2 sum | Top 2 % |
|---|---|---|---|---|---|
| Baseline | +426.40 | +51.60 | 12.1% | +103.20 | 24.2% |
| MinHold=3 | +420.65 | +51.60 | 12.3% | +103.20 | 24.5% |
| SMA N=5 | +328.55 | +68.40 | 20.8% | +111.95 | 34.1% |
| EMA N=3 | +274.55 | +48.30 | 17.6% | +81.30 | 29.6% |
| ScoreEma3 + MinHold=3 (combo) | +276.45 | +48.30 | 17.5% | +82.45 | 29.8% |

MinHold=3's top 2 trades are the EXACT SAME two trades as the baseline's (same +103.20 sum) --
dwell-gating never touched either of the two biggest winners. The smoothing candidates run
somewhat MORE concentrated than baseline (17.5-20.8% vs 12.1-12.3% for the single top trade), the
mirror image of trading away small quick winners: fewer total trades means each surviving trade,
including the big ones, is a larger share of a smaller pie.

### Best-smoother + MinHold=3 combination (`OptionsScoreThreeWaySwitchMaxPainConfirmedScoreEmaMinHold`)

Per the task's own instruction, ran exactly one additional combination: the single best smoother
found (ScoreEma N=3, the only SMA/EMA cell matching baseline's day-consistency) stacked with the
already-promising MinHold=3 exit-side dwell. New metric
`OptionsScoreThreeWaySwitchMaxPainConfirmedScoreEmaMinHold` (both mechanisms always on together,
not independently toggleable -- it exists to answer this one question, not to sweep a 2D grid),
reusing the same `--smoothbars=3 --minhold=3` flags:

| Metric | Trades | Win% | Net | Days >=50% |
|---|---|---|---|---|
| ScoreEma N=3 alone | 35 | 68.6% | +274.55 | 8/8 |
| MinHold=3 alone | 85 | 64.7% | +420.65 | 8/8 |
| **ScoreEma3 + MinHold=3 (combo)** | 33 | **72.7%** | +276.45 | 8/8 |
| Combo, out-of-sample (09-21) | 5 | 40.0% | +6.75 | n/a (1 day) |

**Verdict: the two mechanisms barely compound, and mostly don't interfere either -- MinHold=3 adds
almost nothing on top of ScoreEma3 alone** (33 trades vs. 35, net +276.45 vs. +274.55, essentially
flat) but DOES lift win rate further (72.7% vs. 68.6%, +4.1pp over ScoreEma3 alone and +8.4pp over
the unsmoothed baseline -- the best win rate of anything tested in this whole Part A task). The
reason the combination barely changes trade count: ScoreEma smoothing already suppresses most of
the fast, early `ScoreInvalidated` exits MinHold=3 would otherwise have blocked, so by the time
MinHold=3's 3-minute dwell is checked, there's little left for it to gate -- the two mechanisms
target an overlapping subset of the same whipsaw symptom rather than compounding on independent
dimensions. On the out-of-sample day, the combo stays net POSITIVE (+6.75) where ScoreEma3 alone
was also positive (+9.10) and MinHold=3 alone was slightly negative (-9.25) -- a small, single-day
data point, not a pattern to lean on.

### Frozen smoothing spec for Part B

**Nothing beat the baseline cleanly on the strict "adopt only if both win rate and net improve"
bar** -- consistent with the 2026-09-21 leg-level smoothing finding, and an honest result, not
forced. If Part B needs a starting point for further score-smoothing exploration (more days,
different thresholds, or as an ingredient in a future multi-metric composite rather than a
standalone entry gate), freeze on:

- **Method: EMA (not SMA)** -- `ExponentialMean`, `alpha = 2 / (N + 1)`.
- **Length: N = 3** (same units as `--smoothbars=3`).
- **Metric name: `OptionsScoreThreeWaySwitchMaxPainConfirmedScoreEma`** (standalone) or
  `OptionsScoreThreeWaySwitchMaxPainConfirmedScoreEmaMinHold` (stacked with MinHold=3) if the exit-
  side dwell mechanism is also wanted -- the combo's +8.4pp win-rate lift over baseline, at 8/8
  day-consistency and roughly flat net vs. ScoreEma3 alone, is the single most win-rate-favorable
  result in this entire task.
- **Why EMA over SMA**: EMA N=3 was the only SMA/EMA cell matching baseline's own 8/8
  day-consistency bar; SMA's best net cell (N=5) gave up a day of consistency and traded at barely
  a third of the target 5-10/day band.
- **Why NOT the crossover (variant 3)**: clearly weakest of the three variants -- large net
  give-up in-sample and net-negative on EVERY combo tested out-of-sample.
- **Real, measured cost of adopting this spec**: net drops 35.6% (ScoreEma3 alone) to 35.2%
  (combo) versus the unsmoothed baseline, trade frequency drops from 14.0/day to ~4.1-4.4/day (well
  below CLAUDE.md's 5-10/day target band -- a real, explicit flag per that document's own
  instruction to surface frequency drift, not tune past it silently), and MAE/MFE INCREASE rather
  than decrease (the calming hypothesis did not hold). This is not a call to adopt -- it is the
  exact, reproducible starting point Part B would need if score-smoothing is revisited with more
  data, so that work doesn't have to re-derive the SMA-vs-EMA/N-value/re-rank-vs-threshold design
  decisions from scratch.

### Reproduction commands

```
# Baseline (unchanged)
dotnet run --project NiftySignal.VolumeBarData -- trade 2026-09-08 2026-09-19 OptionsScoreThreeWaySwitchMaxPainConfirmed 90 15 2600 --band=5
dotnet run --project NiftySignal.VolumeBarData -- trade 2026-09-21 2026-09-21 OptionsScoreThreeWaySwitchMaxPainConfirmed 90 15 2600 --band=5

# Variant 1 (SMA) / Variant 2 (EMA), N in {3,4,5,6}
dotnet run --project NiftySignal.VolumeBarData -- trade 2026-09-08 2026-09-19 OptionsScoreThreeWaySwitchMaxPainConfirmedScoreSma 90 15 2600 --band=5 --smoothbars=<N>
dotnet run --project NiftySignal.VolumeBarData -- trade 2026-09-08 2026-09-19 OptionsScoreThreeWaySwitchMaxPainConfirmedScoreEma 90 15 2600 --band=5 --smoothbars=<N>
dotnet run --project NiftySignal.VolumeBarData -- trade 2026-09-21 2026-09-21 OptionsScoreThreeWaySwitchMaxPainConfirmedScoreSma 90 15 2600 --band=5 --smoothbars=<N>
dotnet run --project NiftySignal.VolumeBarData -- trade 2026-09-21 2026-09-21 OptionsScoreThreeWaySwitchMaxPainConfirmedScoreEma 90 15 2600 --band=5 --smoothbars=<N>

# Variant 3 (crossover), already-existing mechanism, fast/slow grid at threshold=15
dotnet run --project NiftySignal.VolumeBarData -- crossover 2026-09-08 2026-09-19 <fast> <slow> 15 2600 --metric=OptionsScoreThreeWaySwitchMaxPainConfirmed --band=5
dotnet run --project NiftySignal.VolumeBarData -- crossover 2026-09-21 2026-09-21 <fast> <slow> 15 2600 --metric=OptionsScoreThreeWaySwitchMaxPainConfirmed --band=5

# MinHold=3 re-run
dotnet run --project NiftySignal.VolumeBarData -- trade 2026-09-08 2026-09-19 OptionsScoreThreeWaySwitchMaxPainConfirmedMinHold 90 15 2600 --band=5 --minhold=3
dotnet run --project NiftySignal.VolumeBarData -- mae-mfe trade 2026-09-08 2026-09-19 OptionsScoreThreeWaySwitchMaxPainConfirmedMinHold 90 15 2600 --band=5 --minhold=3

# MAE/MFE for baseline and ScoreEma N=3
dotnet run --project NiftySignal.VolumeBarData -- mae-mfe trade 2026-09-08 2026-09-19 OptionsScoreThreeWaySwitchMaxPainConfirmed 90 15 2600 --band=5
dotnet run --project NiftySignal.VolumeBarData -- mae-mfe trade 2026-09-08 2026-09-19 OptionsScoreThreeWaySwitchMaxPainConfirmedScoreEma 90 15 2600 --band=5 --smoothbars=3

# Best-smoother + MinHold=3 combination
dotnet run --project NiftySignal.VolumeBarData -- trade 2026-09-08 2026-09-19 OptionsScoreThreeWaySwitchMaxPainConfirmedScoreEmaMinHold 90 15 2600 --band=5 --smoothbars=3 --minhold=3
dotnet run --project NiftySignal.VolumeBarData -- trade 2026-09-21 2026-09-21 OptionsScoreThreeWaySwitchMaxPainConfirmedScoreEmaMinHold 90 15 2600 --band=5 --smoothbars=3 --minhold=3
dotnet run --project NiftySignal.VolumeBarData -- mae-mfe trade 2026-09-08 2026-09-19 OptionsScoreThreeWaySwitchMaxPainConfirmedScoreEmaMinHold 90 15 2600 --band=5 --smoothbars=3 --minhold=3
```
