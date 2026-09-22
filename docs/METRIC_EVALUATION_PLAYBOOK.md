# Metric Evaluation Playbook

A reusable checklist of every technique used to evaluate the volume-bar-cadence future-side
metrics (see [`VOLUME_BAR_FINDINGS.md`](VOLUME_BAR_FINDINGS.md) for the full results this playbook
was extracted from, and [`VOLUME_BAR_METRICS_GUIDE.md`](VOLUME_BAR_METRICS_GUIDE.md) for the
plain-language version). Written so the same discipline isn't accidentally skipped when the
option-chain metrics phase starts — several of the techniques below were only added partway
through the futures phase, after a gap in an earlier step produced a misleading result. Follow
this roughly in order for each new candidate metric, and again for each combination step (per the
3-stage plan in memory: futures score → options score → futures-vs-options weight).

## 0. Before trusting ANY result: confirm the simulator matches the actual rules

**This is listed first because skipping it once already produced a completely wrong "best
metric."** A standing trading-hours rule (no entry before 9:30am, none after 3:00pm, force-close
by 3:15pm) existed but was not enforced in code for the entire futures phase. Every result
calibrated before that gap was found and fixed turned out to be simulating a strategy that could
never actually be traded. Fixing it changed the top-ranked metric entirely (TobDepthDivergence:
60.8% win rate → 45.1% once the excluded windows were removed).

**Before evaluating the first option-chain metric:** re-read `TradeSimulator.cs`'s actual entry/exit
logic line by line against every standing rule you believe applies (trading hours, position
sizing, risk limits, whatever else is expected to hold) — don't assume a rule is enforced just
because it's documented or was true for the futures phase. If a new option-specific rule exists
(margin limits, a different session window for options, etc.), confirm it's coded before running a
single calibration sweep.

## 1. Design the formula, and write down the sign hypothesis explicitly

For every metric, before running anything:
- State the formula precisely, using real fields from the data actually available (check the raw
  feed has what the formula needs — e.g., confirmed ~99.96%+ depth coverage before building any
  depth-based metric).
- State what direction (bullish/bearish) a positive reading is *hypothesized* to mean, and why.
- If the sign isn't obviously derivable from theory (see TobDepthDivergence below), say so
  explicitly rather than picking one and moving on — the calibration sweep is what actually
  settles it, not intuition.

**Worked example — sign chosen empirically, not assumed:** TobDepthDivergence's raw formula
(DepthImbalance minus TopOfBookImbalance) had a genuinely ambiguous sign — the market snapshot
that motivated it (thin best offer, heavier resting sell book) was explicitly described as a
"tension zone" that could resolve either direction. Built one way first, the sweep flatly rejected
it (win rate 28.6-38.2% at the tightest thresholds); flipping the sign and re-running (not
assuming from the rejected version's numbers) produced a strong result. Always re-verify by
re-running after a sign flip, never infer the flipped result from the original.

## 1b. Bar construction: fixed sequential bars, not rolling windows — tested, not just assumed

A larger bar threshold takes longer to accumulate (a 5200-volume bar can take several minutes in a
quiet market), leaving the simulator blind the whole time between readings. A rolling window
(build the big bar from several already-populated smaller sub-bars, e.g. four 650s for a 2600, and
refresh every time one new sub-bar completes instead of waiting for the full window to
reaccumulate) was built and tested as a fix (`RollingVolumeWindowBuilder`, `rollingSubBarThreshold`
parameter on `trade`/`calibrate`). **It made every metric flat-to-worse, and one metric
(BarDurationUrgency) meaningfully worse** (win rate fell from ~55% to the low-40s at every window
size tried). Root cause: consecutive rolling windows overlap heavily, so the percentile-ranking
entry gate ends up ranking each reading against a much more repetitive, less-diverse distribution
than fixed/non-overlapping bars give it. **Lesson for the options phase**: if the same "blind for
too long" concern comes up for option-chain bars, don't assume a rolling window is the fix — the
overlap cost can outweigh the responsiveness gain. Test it the same way this was tested (sanity
check on one day, then a real calibration sweep against the fixed-bar baseline) before trusting it.

## 2. Calibration sweep — never hand-pick a threshold

Sweep `barVolumeThreshold ∈ {650, 1300, 2600}` (or the option-side equivalent — may need its own
bar-sizing convention entirely, e.g. based on option notional or contract volume rather than
future lots) × `entryPercentile ∈ {80, 85, 90, 93, 95, 97, 99}`, targeting 7-20 trades/day. Every
threshold used anywhere in this project was derived from a sweep like this, never guessed.

**When picking the "best" row from a sweep, prefer win-rate quality over raw net.** Repeatedly
throughout the futures phase, the row with the single highest net points was not the row adopted —
a row with a slightly lower net but a win rate solidly above 50% and a sane trade count was
preferred, because raw net is what a single outlier trade inflates most easily (see step 4).

## 3. Full trade-level detail + concentration check

Once a combo is picked, run the actual `trade` command (not just calibrate) to get every trade,
and compute: what share of the total net profit came from the single best trade? A metric whose
"good" result is 80%+ explained by one trade is not validated, no matter how good the pooled
number looks. This single check caught more false positives than any other technique used —
TrendReversion (145% from one trade), FutureCvdNet (98%), VwapDeviation (123%, a single trade
bigger than the whole reported total), TopOfBookImbalance standalone (88% from two trades) were
all headline numbers that collapsed under this check.

## 4. DTE split (0-DTE vs. non-0-DTE)

Query the real expiry-vs-trading-date gap for every day in the test window (don't assume; query
`instruments` directly). Split every leading candidate's trades into 0-DTE and non-0-DTE buckets
and compare win rate (not just net) in each. **The recurring pattern found repeatedly**: a metric
whose pooled win rate looks fine can have a 0-DTE win rate *below* 50% while its net looks fine
purely because 0-DTE options swing 100%+ on small moves — a handful of oversized wins masking a
losing hit rate. BarDurationUrgency and TopOfBookImbalance both showed this pattern before the
gate fix; DepthImbalance and (after the fix) TobDepthDivergence did not, which was part of why
they were trusted more.

## 5. Session-phase split (time-of-day)

Bucket trades by entry time into phases that respect whatever trading-hours rule actually applies
(after the gate fix: Open 09:30-10:00, Mid 10:00-13:30, Close 13:30-15:15). This is what
ultimately mattered most for the futures phase: DepthImbalance and BarDurationUrgency turned out
to have *opposite* phase biases (one wins in the open and loses later, the other loses in the open
and wins later) — invisible from the pooled number alone, and the single most important finding
that led to the session-gated switch beating every individual metric and the linear blend both.

**Do this split on every leading candidate, not just when something looks off.** It found a real,
actionable pattern on metrics that already looked "fine" in aggregate.

## 6. Redundancy / correlation check before combining anything

Before assuming two validated metrics both deserve independent weight, cross-correlate their raw
values directly against each other (level-vs-level and delta-vs-delta), not against price — same
method already established on the time-cadence side (`scripts/pcroi-vs-oidiff-redundancy.sql`).
Interpretation scale used throughout: ±0.8+ means the same signal twice; the 0.15-0.43 range found
between several genuinely distinct metrics is "related but not redundant, keep both"; near 0 is
fully independent. Do this pairwise for every pair of candidates entering a combination step, not
just the two that seem most alike.

## 7. Stop-loss (and other risk overlay) testing — after the metric is validated, not before

Once a metric's raw signal is trusted, sweep a simple percent-of-premium stop-loss (20/30/40/50%)
against it. **Adopt a stop only when win rate AND net both improve together** at the same level —
if net improves but win rate doesn't (or vice versa), that's more likely a lucky reshuffling of
which trades got cut short than a genuine risk-reduction. A stop that hurts at tight levels but
helps at loose levels (clipping only the true tail-risk trades) is the pattern actually found
useful (DepthImbalance, both before and after the gate fix, at different specific percentages).

## 8. Combination mechanism: test blend AND switch, don't assume either works

**The single biggest lesson from this whole phase.** A linear weighted blend of validated metrics
is not guaranteed to beat the best single input — it can be *worse* (the futures composite: 49.6%
win rate / +80.95 pts, vs. DepthImbalance alone at 56.1% / +151.50). The reason: metrics with
systematic opposite biases (here, opposite time-of-day windows) partially cancel each other out
when averaged every bar. A hard switch between the two by whatever regime drives the bias (here,
time-of-day) recovered and exceeded each metric's own individual edge (57.4% / +177.25).

**When combining metrics for the options phase, test in this order:**
1. Linear weighted blend (the default hypothesis — test it, don't assume it).
2. If the blend underperforms the best single input, check for a systematic bias split (time-of-
   day is what worked for futures; for options, moneyness/strike-distance, IV regime, or days-to-
   expiry-within-the-week might be the relevant axis instead — check what the session-phase-style
   split actually reveals before assuming time-of-day transfers).
3. If a clean regime split doesn't fully explain it, consider a confirmation-filter design (one
   metric drives the trade, a second must agree in sign before entry is allowed) rather than
   either metric driving score computation directly — useful when a candidate has a real but
   narrow edge (e.g. TopOfBookImbalance's edge was concentrated in the open specifically, so it
   was folded in as an open-only confirmation gate on the session-gated switch rather than blended
   or given its own switched segment).

## 9. Out-of-sample validation log — keep a running table, don't just calibrate once

Every time new real trading days sync in, run the *already-locked* config (no re-tuning) against
them and log the result in a running table, separate from the calibration tables. Calibrating on
all available days (rather than holding one out) is sometimes the right call when explicitly
decided (more data for a "final" decision), but note plainly when that trade-off is made — it
means the very next new day becomes the first genuine out-of-sample check again.

## 10. Documentation discipline

Two documents, kept current as things change, not just written once:
- A dense technical log (`VOLUME_BAR_FINDINGS.md`-style) — every table, every number, every
  caveat, organized chronologically so a stale earlier section stays visible as "what was tried"
  even after a later section corrects it (never delete an earlier finding, add a correction
  section that references it).
- A plain-language guide (`VOLUME_BAR_METRICS_GUIDE.md`-style) — same results, explained simply
  with real data examples, rewritten (not just patched) whenever the technical log's picture
  changes enough that the old plain-language version would mislead a reader.

## Quick checklist for each new option-chain metric

1. Confirm the simulator's rules actually match what's required (step 0) — check fresh for
   anything option-specific, don't assume the futures-phase fix covers it.
2. State the formula and the sign hypothesis explicitly (step 1).
3. Calibration sweep across bar size × percentile (step 2), prefer win-rate quality when picking.
4. Trade-level detail + concentration check (step 3).
5. DTE split (step 4).
6. Session-phase split, or whatever the option-side equivalent regime axis turns out to be
   (step 5).
7. Redundancy check against every other candidate already in consideration (step 6).
8. Stop-loss / risk-overlay test, only after the above are clean (step 7).
9. When combining validated candidates, test blend first, then check for a regime split, then
   consider confirmation-filter designs (step 8).
10. Log every new real trading day against the locked config once one exists (step 9).
11. Keep both documents current (step 10).
