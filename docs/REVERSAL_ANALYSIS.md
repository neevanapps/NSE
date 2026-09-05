# Reversal Analysis Log

Running log of retrospective analysis on intraday Nifty reversals, looking for leading
clues in the tracked metrics (OI buildup, PCR, futures basis, IV skew, depth imbalance,
price momentum, VIX change). Purpose: accumulate enough instances to tell a real,
repeatable pattern from a one-off coincidence. One entry per reversal studied — do not
overwrite prior entries.

**How to use this**: when a new reversal is worth studying, add a new dated section below
in the same shape as the template. After a few entries exist, look across them for any
finding that shows up consistently (not just once) before promoting it from "candidate
signal" to something worth acting on or building into the ruleset.

---

## Template for new entries

```
## YYYY-MM-DD HH:MM–HH:MM IST — <up-reversal / down-reversal>

**Price context**: <spot path, e.g. "23960 -> 24005 (top) -> 23982">

**Days to expiry**: <calendar days> calendar / <trading sessions remaining, i.e. excluding
weekends/holidays, counting expiry day itself> trading -- track both; NIFTY weeklies always
straddle a weekend so the two numbers diverge, and theta decay tracks trading days, not
calendar days. Not used to *infer* buying vs. writing (see below) -- it's a conditioning
variable, for slicing findings across entries to see whether the buying/writing mix
actually shifts as expiry approaches, once enough entries exist to check.

**Data quality caveats**: <which metrics, if any, were running pre-fix/buggy formulas
at this point in time and should be discounted>

**Buying vs. writing (direct, per-strike)**: pull OI + own-premium change per strike across
the window for the side(s) that moved -- this is the primary read, not net OI change alone.
OI up + premium up = buying (long buildup). OI up + premium down = writing (short buildup).
Note which strikes it concentrated at (near-ATM vs. far OTM) and whether the same pattern
held on both calls and puts or diverged between them.

**Findings**:
- <metric>: <what it did, with rough timing relative to the top/bottom>
- ...

**Candidate signal(s)**: <the single clearest, most defensible tell, if any>

**Cross-check against prior entries**: <does this confirm, contradict, or say nothing
about a candidate signal from an earlier entry? include a DTE-bucket comparison once >=2
entries exist -- e.g. "both entries at 1 trading day to expiry showed writing-dominant
puts" is the kind of statement this section should eventually be able to make>
```

---

## 2026-09-04 11:15–11:55 IST — up-reversal (topped ~11:39–11:40)

**Price context**: Nifty spot rose steadily from ~23963 (11:15) to a top of ~24005
(11:39–11:40, tick high 24004.85 at 11:39), then reversed down to ~23982 by 11:44–11:45,
chopping in the 23982–23989 range through 11:55.

**Days to expiry**: 4 calendar / 2 trading (Fri 4-Sep -> Tue 8-Sep expiry, with Sat/Sun in
between; only Mon 7-Sep and expiry day itself remained as trading sessions). Added
retroactively when this field was introduced -- worth noting since the buying/writing
finding below (writing-dominant) came in at what's actually a fairly *close* trading-DTE,
not the "far from expiry" the raw 4-calendar-day figure would suggest at a glance.

**Data quality caveats**: This window predates same-day fixes to `DepthImbalance` (was a
raw bid/ask ratio, always positive — no directional information) and `IvSkew` (was priced
against the mismatched current-month future instead of spot, biasing both call and put IV).
Both are discounted below and should not be compared against post-fix data from later
reversals without re-deriving them from raw ticks the same way this entry did, or waiting
for a reversal that occurs entirely after the fix (deployed 2026-09-04 afternoon). The
composite score itself also predates the dynamic-k and within-cadence-smoothing fixes, so
its raw values were saturating (swinging roughly -70..+94) and were not usable as a
standalone signal at the time — that's a separate, already-fixed noise problem, not a
finding about this specific reversal.

**Buying vs. writing (direct, per-strike)**: near-ATM/slightly-OTM puts (23950, 24000,
24050 — the strikes spot was approaching/crossing) all showed OI up + premium down over
11:15→11:40, e.g. 24000 put: OI +3.25M, premium −14.0%. That's writing (short buildup), not
buying, concentrated right where spot was headed. Calls weren't a buildup story at all —
aggregate call OI simply eroded (see below), consistent with existing longs closing out
rather than fresh positioning either way.

**Findings**:
- **PCR (put/call OI ratio)**: rose steadily and almost monotonically from 1.279 (11:15) to
  ~1.56 (11:45+). Its z-score hit the +3.00 clip repeatedly starting **~11:23** — roughly
  15+ minutes before the actual top — and stayed pinned near that ceiling into and past
  11:40. The clearest, most persistent statistical anomaly in the window.
- **Put OI, per-strike**: see "Buying vs. writing" above — writing-dominant, not buying, by
  this project's own OI-classification convention that reads *bullish* (sellers confident
  enough to underwrite a floor there), not bearish.
- **Call OI (aggregate, near-week)**: eroded almost every single minute for the full ~25
  minutes into the top — 120.9M (11:15) -> 109.2M (11:39) -> 107.0M (11:44), about -11.5%,
  while price was still rising the whole time. Rising price + falling OI = "long
  unwinding": existing call holders steadily closing/booking profits rather than adding.
  Bearish by the same convention. Not a one-off spike — sustained the entire way up, which
  is what makes it notable.
- **PCR's rise, reconciled**: mechanically driven by *both* sides (calls unwinding + puts
  being written), but the two sides carry opposite directional reads individually. The
  put-writing was confidence-flavored (bullish); the steady call unwind was the more
  genuinely bearish, persistent tell. Treat "PCR spiked" as a headline number whose
  underlying mechanism is mixed, not a clean one-directional signal.
- **Price momentum (15-min lookback delta on futures)**: peaked essentially coincident with
  price (~25.10 at 11:39:34, versus the price top at 11:39–11:40), then decayed fast,
  crossing negative by ~11:53. Expected to be coincident/lagging by construction (it's a
  lookback delta) — not a leading indicator on its own, but the *speed* of the post-top
  decay was notably fast once it turned.
- **Futures basis**: no clean pre-top pattern — mostly negative-vs-recent-mean (-1 to -2 z)
  through 11:15-11:32, crossed positive and widened (+1 to +2.9 z) mostly *after* the top,
  during the decline. Inconclusive as a leading signal here.
- **OI buildup net (aggregate, 15s cadence)**: noisy, oscillating both signs throughout,
  no visible pre-top trend at that resolution. The clean call/put OI stories above only
  showed up when aggregated over the full ~25-40 minute window, not at the 15s cadence the
  live composite score consumes — worth remembering when judging whether this class of
  signal could realistically feed the live score without further smoothing/aggregation
  changes.
- **Depth imbalance / IV skew**: discounted, see caveats above.

**Candidate signal(s)**:
1. Persistent call-OI erosion (steady "long unwinding") sustained for 20+ minutes *while
   price is still making new highs* — the more behaviorally specific and repeatable-sounding
   candidate.
2. PCR z-score pinned at its +3.00 clip for a sustained stretch (not just a brief spike) —
   easier to monitor live since it's already a tracked metric, but per the mechanism above
   it's a mixed signal, not a clean directional one.

**Cross-check against prior entries**: none yet — this is the first entry. Both candidate
signals above are unvalidated with n=1 and should not be treated as a real edge until they
either show up again on a subsequent reversal, or fail to.
