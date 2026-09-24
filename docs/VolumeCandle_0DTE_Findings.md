# 0-DTE Volume-Candle Findings

**Scope of this file**: the futures-volume-event-bar research track from the user's own 52-section
spec ("0-DTE Nifty Options Volume-Candle Research & Simulation Specification"), implemented per
the approved plan "0-DTE Volume-Candle Research Framework -- Phase 1 / Experiment 1"
(2026-09-23). Kept separate from `docs/Price_Based_Findings.md` (time-based, single-strike,
PUT-only, no futures clock at all) and `docs/VOLUME_BAR_FINDINGS.md` (the much broader existing
volume-bar/metric-trial research) -- this track's own clock (futures-volume event bars, spec
section 4), its own bar universe (every 0-DTE CE+PE strike, spec section 7), and its own
execution model (real bid/ask, spec section 27-29) are all structurally different from either.

Same discipline as the other two findings docs: what was tested, the actual numbers, an explicit
conclusion (even when just "this is Phase 1, not a verdict"), and honestly-noted scope cuts.

## What was built (2026-09-23)

Per the approved plan, all in `NiftySignal.VolumeBarData`, in-memory only (no new database table):

- `FutureEventBarBuilder`/`FutureEventBar` -- 1300-contract futures event bars with true
  excess-volume carry (spec 4.1) and an explicitly-marked final partial bar (spec 5). Deliberately
  a separate type from the existing production `NiftySignal.Features.VolumeBar`, which does NOT
  carry excess volume (a different, unrelated bar-construction rule for a different purpose).
- `SynchronizedOptionBarBuilder`/`SynchronizedOptionEventBar` -- every available 0-DTE CE/PE
  strike's own bar for each futures event boundary (spec 6-8), average-LTP baseline pricing (spec
  9), `MissingData`/`IsStale` marking with no fabricated OHLC (spec 11-12).
- `EventBarStrikeSelector` -- Dynamic ATM only this pass (spec 14); Fixed Daily ATM/Fixed
  Offset/Dynamic Band are documented, not-yet-implemented extension points for Experiments 2-4.
- `Vc0DteBehaviorRecorder` -- forward returns (1/2/3/5/10 event bars), MFE/MAE and
  time-to-MFE/MAE, for every dynamic-ATM Call/Put fast=3/slow=10 SMA crossover, pinned to the
  signalling contract (spec 17-23, 40).
- `Vc0DteTradeSimulator` -- the state machine (spec 24-39) plus the user's own three explicit
  rules: entry only once the slow window is warmed (inherent to `PriceCrossoverEngine`), strike
  selection walks outward from ATM to the first live premium in [100,150], no new entries after
  15:00 IST, forced close at 15:15 IST using the last real executable tick.
- CLI: `vc0dte-populate`, `vc0dte-trace`, `vc0dte-behavior`, `vc0dte-simulate`.
- Tests: `FutureEventBarBuilderTests`, `SynchronizedOptionBarBuilderTests`,
  `Vc0DteTradeSimulatorTests` (12 tests total). `dotnet build NiftySignal.slnx`: 0 warnings/errors.
  `dotnet test NiftySignal.slnx`: 786/786 passing (774 baseline + 12 new, no regressions).

**Important, kept explicit per the plan's own note**: `Vc0DteBehaviorRecorder` follows the
dynamically-selected ATM contract itself; `Vc0DteTradeSimulator` selects and holds whichever
contract's live premium falls in [100,150] -- often a DIFFERENT contract for the same signal
event. The two are separate, non-comparable views. A behaviour finding on the ATM contract is not
automatically a claim about what the simulator traded.

## First real-data run, 2026-09-08 only -- ONE DAY, a data point, not a verdict

Per project memory (`feedback_reporting_format_and_targets`, backtesting-is-a-long-process rule):
always report trade count + net, and never treat one day as a conclusion.

- `vc0dte-populate`: 1591 future event bars (1 final-partial), 21 distinct 0-DTE strikes, 65231
  option bars, 8837 missing (13.5%).
- `vc0dte-behavior`: 272 crossover observations (both sides pooled).
- `vc0dte-simulate` (fast=3/slow=10, [100,150] band, no-entry-after-15:00, forced-close-15:15):
  **87 trades, 28.7% win rate, net -8826.25, 17 rejected signals (all `OutsideSession`, i.e. real
  crossings that fired after 15:00 IST and were correctly excluded rather than traded).**

### Observation, not yet a conclusion: bar cadence is very fast, trade frequency far above the 5-15/day target

1591 event bars in one day (a 1300-contract threshold) means bars complete roughly every 15-20
seconds on average, several times faster than this project's existing 2600-contract futures-volume
convention used elsewhere (`docs/VOLUME_BAR_FINDINGS.md`). A fast=3/slow=10 SMA on that cadence
warms up and re-crosses very quickly, producing 87 trades in a single day -- far above this
project's standing 5-15 trades/day target for a production rule. **This is explicitly the Phase 1
baseline, not a tuned result** (spec section 17/52: "these parameters are a starting research
configuration, not an optimized result"; section 45: "do not optimize for P&L initially"). No
threshold/window change is recommended from one day's numbers -- flagging the observation for
whoever runs Experiment 6 (event-frequency sweep, spec section 47) later, since 1300 contracts
producing this many bars/day is itself useful context for that later experiment.

### Scope not attempted, honestly noted

- Only one trading day (2026-09-08) has been run through the full pipeline so far -- this is a
  single data point, not a multi-day sample.
- Experiments 2-7 (fixed-daily ATM, fixed-offset strikes, dynamic ATM+/-N bands, VWAP/Close price
  comparison, event-frequency sweep, DTE expansion) are all deferred, per the approved plan and
  the spec's own section 47 sequencing.
- No cross-strike agreement/persistence/propagation analysis yet (needs the band modes from
  Experiment 4 first).
- Transaction costs currently cover brokerage + slippage only (`PaperTradeSimulator`'s existing
  model) -- STT/GST/stamp duty are not modeled (no rate was ever given; nothing fabricated, same
  discipline `PaperTradeSimulator`'s own doc comment already states).
- The forward-return/MFE-MAE numbers in the one behaviour CSV produced so far have not been
  aggregated/summarized into persistence or cross-strike-agreement findings yet -- that analysis
  is the next step, not yet done.

### Reproduction commands

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-populate 2026-09-08 2026-09-08
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-trace 2026-09-08 12
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-behavior 2026-09-08 2026-09-08 --out=vc0dte-behavior-2026-09-08.csv
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-simulate 2026-09-08 2026-09-08 --out=vc0dte-trades-2026-09-08.csv --rejected=vc0dte-rejected-2026-09-08.csv
```

## Phase 1 forensic verification, before touching methodology (2026-09-23)

**User's explicit instruction**: before tuning anything (3/10, 1300, [100,150]), prove the 87-trade
result comes from a real, fast event clock rather than a construction bug -- verify the raw
volume/excess-carry arithmetic, measure the actual event-bar time scale, separate behaviour from
trading results, and produce a full signal-to-trade funnel. New diagnostics, all read-only,
reusing the exact same code paths that produced the original result (never a re-implementation
that could silently drift):

- `FutureEventBarBuilder.BuildDayWithTraceAsync` / CLI `vc0dte-tick-trace` -- per-tick threshold
  accounting (built into the SAME accumulation loop `BuildDayAsync` already used, not a parallel
  re-implementation).
- `EventBarDurationStats` / CLI `vc0dte-duration-stats` -- event-bar duration percentiles and
  histogram.
- `Vc0DteBehaviorSummary` -- aggregates `Vc0DteBehaviorRecorder`'s own observation rows by
  (OptionType, SignalDirection), printed by `vc0dte-behavior` itself.
- `Vc0DteTradeSimulator.SignalFunnel` -- counters added inline to the existing
  `SimulateDayAsync` loop (not a duplicate simulator), printed by `vc0dte-simulate` itself.

`dotnet build`: 0 warnings/errors. `dotnet test`: 796/796 passing (786 baseline + 10 new: excess-
carry proof, duration-stats percentile/histogram coverage, behaviour-summary grouping/null-average
coverage, signal-funnel counter coverage including the "ignored while position already open" case).

### 1-2. Raw volume/excess-carry verification -- CONFIRMED, exactly as designed

`vc0dte-tick-trace 2026-09-08 25 --context=6` (real data):

```
      TimeIST PrevCumVol  CurCumVol   Delta AccumBefore Threshold AccumAfter EventId ClosesBar  Excess
     09:15:25      36660      38350    1690         455      1300        845      24         Y     845
     09:15:26      38350      39975    1625         845      1300       1170      25         Y    1170
```

Event #24's closing tick: `AccumBefore=455, Delta=1690` -> raw sum 2145, threshold 1300, **Excess
845 -- and event #25 opens with `AccumBefore=845` on its very next tick**, exactly the carried
value. The closing bar's own `Volume` is the full 2145 (later, 845+1625=2470 for #25) -- **the
whole tick is never split** (no bar ever gets a fabricated "30 of this tick, 40 of the next");
only the numeric excess carries forward as the next bar's starting accumulator balance. This is
the exact distinction the spec's own section 4.1 asks for, and it is now directly readable from
one raw-tick table, not merely asserted by the code's own doc comments. Confirmed as intended,
not a bug: **1591 bars/day is not a volume-misinterpretation artifact.**

### 3. Event-bar duration distribution -- the real answer to "what temporal scale are we studying"

`vc0dte-duration-stats 2026-09-08 2026-09-08`, 1590 bars (final partial excluded):

| Min | P1 | P5 | P10 | P25 | Median | P75 | P90 | P95 | P99 | Max |
|---|---|---|---|---|---|---|---|---|---|---|
| 0ms | 0ms | 0ms | 0ms | 2.0s | **8.0s** | 18.0s | 37.0s | 49.0s | 90.0s | 149.0s |

Histogram: <1s 15.5%, 1-2s 4.0%, 2-5s 14.8%, 5-10s 20.9%, 10-30s 30.1%, 30-60s 11.6%, 1-2min 2.9%,
2-5min 0.2%, >5min 0.0%.

**A 3-bar fast MA / 10-bar slow MA at a 1300-contract threshold spans roughly 24s-80s during the
bulk of the session (P25-P75), not minutes.** This is NOT equivalent to a conventional multi-
minute time-based 3/10 crossover -- it is a genuinely different, much shorter-horizon signal.
Exactly the user's own point: not necessarily bad (this is precisely what "event time" research is
for), but the temporal scale must be stated plainly rather than assumed, and it now is.

### 4. Behaviour by signal type, kept separate from any trading result

`vc0dte-behavior 2026-09-08 2026-09-08`, 272 observations (matches the funnel's own
`TotalCrossoverSignals=272` exactly -- a useful cross-check that both diagnostics agree):

| Signal | Count | AvgFwd1 | AvgFwd2 | AvgFwd3 | AvgFwd5 | AvgFwd10 | AvgMFE% | AvgMAE% |
|---|---|---|---|---|---|---|---|---|
| Call Bullish | 67 | -0.014 | 0.564 | -0.463 | -1.372 | -2.217 | 38.3 | 98.9 |
| Call Bearish | 69 | -0.637 | -1.015 | -0.335 | -0.398 | 0.541 | 39.7 | 98.7 |
| Put Bullish | 67 | 0.298 | 0.032 | -0.508 | -0.044 | 0.218 | 98.0 | 20.7 |
| Put Bearish | 69 | -0.366 | -0.309 | -0.385 | -0.473 | 1.092 | 101.3 | 20.8 |

**One day only -- read as a shape, not a verdict.** Forward returns are small and inconsistent in
sign across horizons for all four rows; none show the kind of clean, monotonic post-signal drift
that would constitute an established behavioural edge yet. The MFE/MAE columns are the more
striking pattern: Calls show MAE close to 100% (i.e., the average Call contract's worst
excursion after a signal is nearly a full round-trip to near-zero) while Puts show MAE around
20% -- consistent with 0-DTE Call premiums often decaying toward worthless well before session end
regardless of signal direction, a structural (theta/pin-risk) effect that a one-day sample cannot
distinguish from genuine signal-driven adverse movement. **Per the user's own framing: this is an
"engineering validation: promising" result (the pipeline correctly measures four distinct,
non-conflated behaviours), not yet a "behavioural finding" (no edge is established on one day).**

### 5. Signal funnel -- explains the 87 trades without finding a construction bug

`vc0dte-simulate 2026-09-08 2026-09-08`:

```
TotalCrossoverSignals=272 (CE up=67 down=69, PE up=67 down=69)
WhileWarmingUp=0 (structurally always 0 -- PriceCrossoverEngine cannot report a crossing before its slow window is full)
ConsideredWhileFlat=104
IgnoredPositionOpen=30
Rejected[NoStrikeInBand=0 NoExecutableTick=0 OutsideSession=17]
TradesOpened=87 (CE=41 PE=46)
ReversalExits=87, ForcedExits=0
TradesPerHour=13.92
```

The accounting closes exactly: `ConsideredWhileFlat (104) = TradesOpened (87) + Rejected (17)`,
and every one of the 17 rejections is `OutsideSession` (a real crossing after 15:00 IST, correctly
excluded per the user's own rule) -- **zero rejections for `NoStrikeInBand` or
`NoExecutableTick`**, meaning the [100,150] band and tick-availability were never the bottleneck
this day. **`ReversalExits=87, ForcedExits=0`**: every single position was closed by a genuine
opposite-direction crossing before the session ended -- none needed the 15:15 forced close, which
is itself informative (positions never sit "stuck" waiting for EOD at this cadence). `~14
trades/hour` is the direct, un-mysterious consequence of a bar completing roughly every 8 seconds
(median) and a 3/10 window that re-crosses often at that cadence -- not a sign-selection or
ATM-selection defect.

### Overall classification (adopting the user's own framing verbatim)

- **Engineering validation: promising.** The event-bar/excess-carry mechanism is proven correct at
  the raw-tick level. The signal funnel accounts for every entry-direction crossing exactly:
  `ConsideredWhileFlat (104) + IgnoredPositionOpen (30) = 134 = CeCrossUp (67) + PeCrossUp (67)`,
  with the 138 opposite-direction (`CrossedDown`) crossings only ever consulted as exit checks for
  a currently-open position of that side -- every signal's fate is traceable, nothing vanishes
  silently.
- **Behavioural finding: not yet established.** One day, small and inconsistent forward returns,
  a plausible-but-unverified theta-decay explanation for the Call/Put MAE asymmetry. Needs a
  multi-day sample before any directional claim.
- **Trading result: negative first-day baseline, inconclusive.** 28.7% win rate, net -8826.25 on
  one day is not evidence against the 3/10 crossover -- per the user's own instruction, **3/10,
  1300, and [100,150] are not being tuned off this single day.**

### Next step (per the user's own instruction, not yet started)

Repeat sections 3-5 above (duration distribution, behaviour-by-signal-type, signal funnel) across
a genuine multi-day sample before drawing any behavioural or trading conclusion -- a "Phase 1
forensic analysis" using the diagnostics built here, still without touching 3/10, 1300, or
[100,150].

### Reproduction commands

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-tick-trace 2026-09-08 25 --context=6
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-duration-stats 2026-09-08 2026-09-08
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-behavior 2026-09-08 2026-09-08 --out=vc0dte-behavior-2026-09-08.csv
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-simulate 2026-09-08 2026-09-08 --out=vc0dte-trades-2026-09-08.csv --rejected=vc0dte-rejected-2026-09-08.csv
```

## Phase 1 / Experiment 1 -- Multi-Day Behaviour Validation (2026-09-23)

**Frozen configuration, unchanged from Phase 1 (user's own explicit instruction -- no parameter
optimization in this task)**: 0-DTE only, 1300-contract futures event bars with excess-volume
carry, dynamic ATM, average LTP, fast=3/slow=10 SMA crossover, behaviour pinned to the signalling
contract, trade simulator's [100,150] band/no-entry-after-15:00/forced-close-15:15/first-tick-
after-signal/bid-ask-with-fallback rules all unchanged. The new `vc0dte-multiday-behavior` CLI
command hardcodes this configuration (no `--threshold=`/`--fast=`/`--slow=` flags exist on it at
all) specifically so it cannot be used to accidentally sweep any of these.

### Data coverage -- every date in the local `niftysignal_vm_copy` mirror, 2026-09-01 through
2026-09-23, explicitly classified

**3 complete 0-DTE days available: 2026-09-08, 2026-09-15, 2026-09-22** -- all Tuesdays (NIFTY's
weekly option expiry in this dataset is Tuesday; a 0-DTE day can only be a Tuesday). Every other
date in the range is excluded with a concrete, printed reason -- none silent:
- **Weekends** (09-05/06, 09-12/13, 09-19/20): no trading session, expected.
- **Weekdays with literally no futures tick data in this local mirror** (09-01, 09-02, 09-03,
  09-07, 09-14, 09-23): a genuine gap/boundary in `niftysignal_vm_copy`'s own coverage, not a
  0-DTE-specific exclusion -- 09-23 is today, the local mirror simply doesn't have it yet.
- **Weekdays with real futures data but NOT a 0-DTE expiry day** (09-04, 09-09, 09-10, 09-11,
  09-16, 09-17, 09-18, 09-21): futures event bars build fine, but zero option instruments have
  `ExpiryDate == AsOfDate`, so the 0-DTE filter correctly excludes them -- confirmed these are
  non-Tuesday weekdays, consistent with the weekly-Tuesday-expiry explanation.

This is a smaller independent sample than hoped (3 days), an honest limitation carried through
every conclusion below.

### Point 1 -- implementation inspection, before running anything

- **Forward return formula** (unchanged from Phase 1): `(futurePrice - priceAtSignal) / priceAtSignal * 100`, using the SAME token's own bars at `EventId + horizon` -- pinned to the signalling contract, null if that bar doesn't exist or has no real print (never fabricated).
- **MFE/MAE formula** (unchanged): scans every real tick for that same contract from the signal timestamp to session end (15:30 IST), `MFE = max(0, price - entryPrice)`, `MAE = max(0, entryPrice - price)`, both clamped at 0.
- **Signalling strike pinning** (unchanged): the ATM token at the moment of the signal is captured once; every forward-return/MFE-MAE lookup uses that SAME token's own later bars/ticks, regardless of where ATM moves afterward.
- **Missing option bar handling** (unchanged): a bar with zero real ticks in its interval has `MissingData=true` and null OHLC/AverageLtp -- every formula above already treats that as "cannot compute," never as zero.
- **Can the recorder safely run across multiple days?** Yes, confirmed structurally: `RecordAsync` is called once per day with a **fresh** `PriceCrossoverEngine` instance and `lastAtmToken=null` each call -- no state leaks between days.
- **New, additive fields required for this task** (points 4-9) did not exist before this task and were added to `ObservationRow`: `FutureCloseAtSignal`/`FutureForwardReturnPercent{1,2,3,5,10}` (point 5), `AbsoluteForwardReturn{1,2,3,5,10}`/`MaximumFavorableMoveAbs`/`MaximumAdverseMoveAbs` (point 4), `PreSignalOptionMovePercent{3,5,10}`/`PreSignalFutureMovePercent{3,5,10}` (point 6), `EventBarsToMaximumFavorable`/`EventBarsToMaximumAdverse` (point 3's "event bars to MFE/MAE" unit), `FastWindowDurationMs`/`SlowWindowDurationMs` (point 9). All are additive -- the original 17 fields' formulas are byte-identical to Phase 1.

### Point 3 -- core behavioural table (pooled, 910 observations across 3 days)

| Signal | n | Days | Fwd1 avg/med | Fwd3 avg/med | Fwd10 avg/med | MFE% avg/med | MAE% avg/med |
|---|---|---|---|---|---|---|---|
| Call Bearish | 219 | 3 | -0.738 / -0.197 | -1.367 / -0.343 | -2.113 / -1.839 | 38.95 / 21.30 | 98.16 / 99.84 |
| Call Bullish | 223 | 3 | 0.010 / 0.130 | -0.588 / 0.130 | -1.183 / 0.307 | 40.78 / 22.37 | 98.00 / 99.85 |
| Put Bearish | 238 | 3 | -0.164 / -0.041 | -0.341 / -0.122 | -0.185 / -0.353 | 168.95 / 114.89 | 30.59 / 22.97 |
| Put Bullish | 230 | 3 | 0.175 / -0.032 | 0.286 / 0.308 | 1.230 / 0.737 | 164.37 / 111.05 | 30.18 / 23.55 |

**Call Bearish is the only row where mean AND median agree in sign at every horizon (all
negative, and getting MORE negative from Fwd1 to Fwd10)** -- the one row with a directionally
consistent shape. Every other row has at least one horizon where mean and median disagree in
sign (Call Bullish at Fwd3/Fwd10, Put Bullish at Fwd1), which by itself means "the average is
being pulled by a skew/outlier, not a broad-based effect" -- not read as evidence of edge.

### Point 4 -- absolute (Rs) vs percentage option movement

| Signal | MFE% avg | MAE% avg | MFE Rs avg | MAE Rs avg |
|---|---|---|---|---|
| Call Bearish | 38.95 | 98.16 | 10.79 | 29.10 |
| Call Bullish | 40.78 | 98.00 | 11.57 | 29.85 |
| Put Bearish | 168.95 | 30.59 | 118.94 | 20.60 |
| Put Bullish | 164.37 | 30.18 | 118.34 | 20.42 |

**The asymmetry survives in absolute Rs terms, not just percent** -- this rules out "it's purely
a cheap-premium percentage artifact" (the user's own explicit concern): Calls lose ~2.6-2.7x more
in Rs on their worst excursion than they gain on their best (MAE Rs ~29-30 vs MFE Rs ~11-12); Puts
show the OPPOSITE asymmetry, gaining ~5.7-5.8x more Rs on their best excursion than they lose on
their worst (MFE Rs ~118-119 vs MAE Rs ~20-21). Both sides show a real, Rs-denominated asymmetry,
just in opposite directions. **Event-bar count to the extreme reinforces this**: Calls reach their
worst point (MAE) at ~1109-1122 event bars after the signal (late in the session) but their best
point (MFE) much earlier (~220-223 bars); Puts show the mirror pattern (MFE late at ~852-857
bars, MAE early at ~357-365 bars). Read together, this is consistent with a session-long
DIRECTIONAL DRIFT in the underlying across these 3 particular days (if the index drifted one way
across the day, calls would decay toward that day's low late and puts would rally toward that
day's high late) -- **stated as a plausible, unverified hypothesis, not a conclusion**; theta decay
alone would not explain why Calls and Puts show opposite-direction asymmetry, since theta pulls
every option toward zero regardless of side.

### Point 5 -- underlying (futures) vs option movement

Pooled `FutFwd1`/`FutFwd3` (the future's own move over the same horizons) are **tiny in every
row**: 0.000% to 0.003% (essentially flat), while the OPTION's own forward returns over the
identical horizons range from -2.1% to +1.2%. **The option premium is moving far more than the
underlying's own near-term move would explain.** This says the crossover, whatever it is
detecting, is NOT primarily forecasting near-term Nifty futures direction -- if anything is being
captured, it's specific to the option's own price path (theta/IV/microstructure), not the
underlying. No causal claim beyond that.

### Point 6 -- lead/lag (does the crossover anticipate, or just confirm?)

Pre-signal option movement (`PreOpt3`) is large in magnitude (day-level range roughly -7.5% to
+6.6%; pooled -5.2% to +4.8%) while pre-signal futures movement (`PreFut3`) stays at the same tiny
scale as the futures' own forward returns (~0.01-0.03%). **The option has already moved
substantially by the time the signal fires, while the underlying has barely moved at all -- and
the subsequent (post-signal) option forward return is small and frequently opposite in sign to
the pre-signal move** (e.g. pooled Call Bullish: PreOpt3 = +4.85%, then Fwd3 averages -0.588%).
**This pattern -- a large move already happened, the signal fires, and what follows is smaller
and inconsistent -- reads as the crossover mostly CONFIRMING a move that already occurred, not
anticipating what happens next.** Stated descriptively, per the user's own instruction, not as a
causal mechanism.

### Point 7 -- day-by-day robustness (the most important check)

Per-day average Fwd3, classified against a +/-0.5% "flat" band, across all 3 days:

| Signal | Positive days | Negative days | Flat days | Avg of daily avg Fwd3 | Median of daily avg Fwd3 |
|---|---|---|---|---|---|
| Call Bearish | 0 | 2 | 1 | -1.333% | -0.925% |
| Call Bullish | 0 | 1 | 2 | -0.561% | -0.463% |
| Put Bearish | 0 | 0 | 3 | -0.343% | -0.375% |
| Put Bullish | 1 | 1 | 1 | +0.249% | +0.166% |

**No signal type is positive on a majority of days. None is "robust" by the user's own explicit
standard** (a signal is not called robust merely because pooled observations are positive -- and
here, none of the four is even pooled-positive at Fwd3 except Put Bullish, which is a literal
coin-flip day-by-day, one up day and one down day out of three). Call Bearish leans consistently
negative (2 of 3 days, never positive). Put Bearish is remarkably FLAT every single day -- not
"no effect found" so much as "this signal's Fwd3 barely moves the needle on any of the 3 days
observed," itself a specific, repeatable (if unexciting) observation.

### Point 8 -- session-bucket analysis (descriptive only, no filters created)

The **15:00-15:30 bucket is a dramatic outlier for the Call side**: Call Bearish AvgFwd1=-7.21%,
AvgFwd3=-14.58%; Call Bullish AvgFwd1=-1.53%, AvgFwd3=-7.94% -- both roughly 5-25x larger in
magnitude than any other session bucket for either Call row (every other bucket's Call AvgFwd3 is
between -0.51% and +0.87%). This is consistent with 0-DTE Call extrinsic value collapsing hard in
the closing half hour (the classic "0DTE pin/decay into close" pattern). **Puts show the same
directional sign but far smaller magnitude** in the same bucket (Bearish -1.08%/-2.56%, Bullish
+1.54%/+0.70%) -- i.e. Puts don't collapse the same way, consistent with Section 4's Rs-asymmetry
finding above (Calls lose big late, Puts don't). Every other session bucket (09:15 through 15:00)
shows comparatively small, mixed-sign Fwd1/Fwd3 values with no obvious consistent pattern across
the day. **No session filter is being proposed here** -- purely descriptive, per the user's
explicit instruction.

### Point 9 -- event-speed dependence (quartiles of this dataset's own slow-window duration)

Q1=45,000ms (45s), Q3=162,000ms (162s) -- the pooled slow (10-bar) window's own real-time span
across all 910 observations. Bucketing each observation into Fast (<=Q1) / Normal / Slow (>=Q3):
Call Bearish Fwd3 goes -1.65% (Fast) -> -1.99% (Normal) -> **+0.22% (Slow)** -- i.e. Calls are
LEAST negative during the calmest (slowest-forming-bar) periods. Put Bullish Fwd3 goes -0.15%
(Fast) -> **+0.54% (Normal)** -> +0.10% (Slow) -- best in the middle bucket, not monotonic. **Some
event-speed dependence is visible (the Call pattern in particular is suggestive: negative during
fast/normal market activity, closer to flat when the market is calm), but with only 3 days
pooled into 3 buckets this is not read as an established, separate finding** -- worth revisiting
once more days are available.

### Point 12 -- missing/stale data audit

| Date | Bars | Missing | Missing% | Stale |
|---|---|---|---|---|
| 2026-09-08 | 65,231 | 8,837 | 13.5% | 8,059 |
| 2026-09-15 | 109,120 | 24,543 | 22.5% | 24,543 |
| 2026-09-22 | 84,920 | 17,948 | 21.1% | 17,948 |
| **TOTAL** | **259,271** | **51,328** | **19.8%** | **50,550** |

Roughly 1 in 5 synchronized option bars across the whole sample has no real tick -- expected at a
seconds-scale event-bar cadence for far-OTM/thin 0-DTE strikes, not itself a red flag. **A small,
correct discrepancy confirmed by inspection**: total Stale (50,550) is slightly less than total
Missing (51,328) -- by design (`SynchronizedOptionBarBuilder`'s own rule: a missing bar is only
marked `IsStale` if an EARLIER real print exists to fall back on; the 778-bar gap is bars missing
data before that contract had traded even once that session, which correctly get `MissingData=true,
IsStale=false` rather than a fabricated stale carry-forward). Forward-return calculations already
verified (point 1, and by the dedicated unit tests) to record `null`, never a fabricated or
carried-forward value, whenever the required future observation doesn't exist.

### Point 10 -- trading simulation (SECONDARY ONLY, per the user's explicit instruction)

| Date | Trades | Net | Total crossovers | Trades/hour |
|---|---|---|---|---|
| 2026-09-08 | 87 | -8,826.25 | 272 | 13.92 |
| 2026-09-15 | 114 | +146.00 | 329 | 18.24 |
| 2026-09-22 | 122 | -8,156.00 | 309 | 19.52 |
| **Pooled** | **323** | **-16,836.25** | | |

30.0% pooled win rate. Two of three days notably negative, one essentially flat. **This is not
used as evidence for or against the crossover's behavioural value** -- included only for
continuity, per the user's own explicit instruction that trading P&L is secondary here.

### Classification of the evidence (user's own categories, A-G)

**A. Engineering validity**: The pipeline behaves according to spec. Every date in the range was
classified with a concrete reason (point 2); the recorder runs safely across multiple days with
fresh per-day state; missing/stale data is marked, never fabricated (point 12); forward-return
nulls past the session boundary are confirmed, never carried or zeroed. No construction bug found.

**B. Behavioural evidence, per signal type**:
- **CE CrossUp (Call Bullish)**: mixed/inconsistent -- mean and median disagree in sign at 2 of
  5 horizons; day-by-day is 0 positive/1 negative/2 flat.
- **CE CrossDown (Call Bearish)**: the most consistent row -- negative at every horizon, mean and
  median agree in sign throughout, and 2 of 3 days negative (0 positive). Still only 3 days.
- **PE CrossUp (Put Bullish)**: mixed/inconsistent -- positive pooled at every horizon except a
  mean/median sign disagreement at Fwd1, but day-by-day is a literal 1-1-1 split (positive/
  negative/flat).
- **PE CrossDown (Put Bearish)**: consistently near-flat -- small magnitude at every horizon,
  0 positive/0 negative/3 flat days. Consistent, but consistent about there being little effect.

**C. Temporal persistence**: Only Call Bearish shows a shape that plausibly persists/strengthens
from 1 to 10 event bars (increasingly negative). The other three signal types don't show a clear
monotonic persistence pattern across horizons in this pooled data.

**D. Lead/lag**: The evidence (point 6) leans toward **confirmation, not anticipation** -- a
sizeable pre-signal option move with a comparatively small, often opposite-signed post-signal
move, while the underlying barely moves either before or after.

**E. Session dependence**: Yes, materially different in the 15:00-15:30 bucket specifically for
the Call side (large negative moves, consistent with 0-DTE extrinsic-value collapse into close).
Other buckets don't show an obvious pattern.

**F. Event-speed dependence**: Suggestive but not conclusive with 3 days -- Calls appear least
negative during the calmest (slowest) event-bar-formation periods.

**G. Open questions**:
- Is the Call-side MAE-heavy / Put-side MFE-heavy asymmetry (point 4) a directional-drift artifact
  specific to these 3 days, a structural 0-DTE Call-decay effect, or something else? Not
  distinguishable with 3 days from one direction of market movement.
- Does the Call Bearish signal's relatively consistent negative shape survive a larger sample, or
  is 3 days (2 negative, 1 flat) too small to say anything?
- Is the 15:00-15:30 session effect specific to Calls, or would it appear for Puts too with more
  days (Puts already show the same SIGN but far smaller magnitude in this bucket)?
- Does the event-speed pattern (point 9) hold up, strengthen, or vanish with more independent
  days?
- The single-day 2026-09-08 duration-distribution finding (median bar ~8s) combined with this
  multi-day Q1/Q3 (45s/162s for the whole 10-bar SLOW window) confirms the fast/slow window is
  genuinely a short, seconds-to-low-minutes-scale signal -- not yet cross-checked against
  09-15/09-22's own individual duration distributions.

### What this task does NOT conclude

Per the user's own explicit instruction: this is **not** a recommendation to move to Experiment 2.
The evidence across 3 independent days is, at best, mixed -- one signal type (Call Bearish) shows
a directionally consistent but still small and only 2-of-3-days-negative pattern; the other three
show no consistent day-by-day direction at all. Nothing here is optimized, tuned, or cherry-picked;
every one of the 14 prohibited actions in the user's own instruction was avoided. **The honest
summary is: engineering is sound, behavioural evidence is weak-to-mixed across the only 3
independent sessions available, and more independent 0-DTE days (this local data mirror currently
has exactly 3) are needed before this configuration's behavioural value can be judged either way.**

### Files/code changed this task

New: `OptionBarQualityAudit.cs` (point 12). Extended (additive only, existing fields/formulas
unchanged): `Vc0DteBehaviorRecorder.cs` (`ObservationRow` +21 fields, `WriteCsv` +21 columns),
`Vc0DteBehaviorSummary.cs` (added `SummarizeByDay`/`SummarizeBySessionBucket`/
`SummarizeBySpeedBucket`/median support, existing `Summarize` behavior preserved). New CLI:
`vc0dte-multiday-behavior` (frozen config, no override flags). New tests:
`Vc0DteBehaviorRecorderTests.cs`, `OptionBarQualityAuditTests.cs`, extended
`Vc0DteBehaviorSummaryTests.cs`. `dotnet build NiftySignal.slnx`: 0 warnings/errors. `dotnet test
NiftySignal.slnx`: 810/810 passing (796 baseline + 14 new).

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-multiday-behavior 2026-09-01 2026-09-23 --out=vc0dte-multiday-2026-09.csv
```

## Underlying-Spot-CE-PE Relationship Analysis (2026-09-23)

**Separate descriptive research layer -- does NOT modify or invalidate Experiment 1.** New files
only (`UnderlyingOptionRelationshipObservation.cs`, `UnderlyingOptionRelationshipRecorder.cs`,
`UnderlyingOptionRelationshipSummary.cs`, CLI `vc0dte-relationship`); `FutureEventBar(Builder)`,
`SynchronizedOptionEventBar(Builder)`, `PriceCrossoverEngine`, `Vc0DteBehaviorRecorder`, and every
Experiment 1 CSV/CLI/formula are byte-for-byte unchanged and reused as-is. No new database table.

### Point 1 -- inspection findings, before writing any code

- **Spot data availability**: NIFTY Index (`InstrumentType.Index`) ticks exist and were confirmed
  present for all 3 included 0-DTE days -- **not assumed, verified empirically** by running the
  new recorder, which reports `SpotAvailable`/`SpotMissingData` per event.
- **Spot tick resolution/fields**: same `Tick` entity as futures/options -- `LastPrice`,
  cumulative `Volume`, optional `MarketDepth`. No option-specific fields (OI, strike) apply, none
  assumed. `OptionTickSeries` (built for Phase 1's options) was reused AS-IS for spot ticks too --
  nothing in its implementation is actually option-specific.
- **Timestamp representation**: identical `DateTimeOffset` (UTC-stored) convention as futures/
  options -- synchronizing spot to the SAME futures event boundaries required no new clock or
  timestamp handling, only a per-event tick-bucketing loop (new, local to the new recorder --
  `SynchronizedOptionBarBuilder` itself was NOT reused/modified, since it is option-specific by
  design, e.g. `MissingData`+`IsStale` semantics tied to option contract identity).
- **Signalling-contract mapping**: every event maps to exactly one CE and one PE token via
  `EventBarStrikeSelector.PickDynamicAtm` -- the SAME, unmodified Experiment 1 rule.
- **Existing calculated fields**: none of futures-vs-spot comparison, CE-vs-PE relative change,
  pre-signal-window movement, or relationship categories existed before this task -- all new,
  additive types.

### Point 3/4 -- event-level relationship results (6,442 observations, 3 days, frozen config)

| Category | Count | % |
|---|---|---|
| OptionDataIncomplete | 2,079 | 32.3% |
| UnderlyingDown_CEDown_PEUp | 1,081 | 16.8% |
| UnderlyingUp_CEUp_PEDown | 913 | 14.2% |
| UnderlyingUp_CEDown_PEUp | 578 | 9.0% |
| UnderlyingDown_CEUp_PEDown | 557 | 8.6% |
| UnderlyingFlat_CEPEDivergence | 394 | 6.1% |
| ContractTransition | 389 | 6.0% |
| (12 smaller categories) | 351 | 5.4% |

**`OptionDataIncomplete` (32.3%) is meaningfully higher than Phase 1's own ~20% raw missing-bar
rate** -- expected, not a discrepancy: a single missing option bar breaks the 1-event-bar
direction calculation for BOTH that event and the NEXT one (no valid "previous reading" to diff
against), so gaps propagate into one extra event's worth of "incomplete" classification. Stated
explicitly so this isn't misread as a second, larger data-quality problem.

**The two "textbook" (delta-consistent) categories -- underlying up with CE up/PE down, and
underlying down with CE down/PE up -- together account for 31.0% of all observations, roughly
DOUBLE the two categories matching the user's own named divergence hypothesis (17.6% combined,
see below).** Descriptively, "normal" CE/PE response to the underlying's own 1-event-bar direction
is the more common pattern in this sample; the divergent pattern is real and non-trivial in
frequency, but a minority.

### Point 8/11 -- the user's hypothesis patterns, tested directly

| Pattern | n | Days | Avg Futures Δ | Avg CE Δ | Avg PE Δ | Avg CE-PE | Stale/Transition |
|---|---|---|---|---|---|---|---|
| Underlying rising, CE weakening, PE strengthening | 578 | 3 | +3.902 | -0.499 | +0.949 | -1.448 | 0 |
| Underlying falling, CE strengthening, PE weakening | 557 | 3 | -3.679 | +0.465 | -0.977 | +1.442 | 0 |

**Observed pattern**: both directions of the user's named hypothesis occur, in comparable
volume, on all 3 included days, with zero stale/transition contamination (both counts are exactly
0 -- every one of these 1,135 observations is a clean, same-contract, non-stale reading).
**Possible interpretation**: consistent with a scenario where CE and PE respond to the underlying
move with different magnitudes (PE's average move, ~0.95-0.98, is roughly double CE's average
move, ~0.47-0.50, in both directions) -- but see Section 8's own explicit caution below.
**What this cannot establish**: whether this asymmetry reflects "demand," any option Greek, or is
simply a mechanical consequence of these particular ATM CE/PE contracts' own price levels/gamma at
the moment of measurement. No Greeks were computed or normalized against, per the task's explicit
instruction.

### Point 5 -- CE-versus-PE relative behaviour: an explicit caution, not a score

CE and PE differ in price, delta, gamma, IV, moneyness, liquidity, and theta exposure at every
single event -- a raw CE-minus-PE Rs difference (reported above, e.g. -1.448/+1.442) is a
descriptive number, not a demand/imbalance measure, and no composite "demand score" was created
(per the task's own explicit prohibition). No normalization by delta/IV/Greeks was performed
(none were pre-approved).

### Point 6 -- futures-versus-spot comparison

Both series are tracked independently per event; `FuturesFastMa`/`SpotFastMa` etc. are separate,
independent `PriceCrossoverEngine` instances (never reset, since futures/spot each have one
continuous identity all day, unlike CE/PE). A dedicated futures-vs-spot divergence table was not
built as a separate report in this pass (out of scope for this task's time budget) -- both series'
own 1-event changes and crossover states ARE in the CSV (`FuturesChange1`/`SpotChange1`, etc.),
available for that comparison directly from the raw data.

### Point 7 -- crossover pre/post context (Experiment 1's own 4 crossover types, unmodified)

| Crossover (n) | Futures Pre3/Pre5/Pre10 | Futures Post1/Post3/Post5/Post10 | CE Pre3/Pre5/Pre10 | CE Post1/Post3/Post5/Post10 |
|---|---|---|---|---|
| Call Bearish (219) | -0.009/-0.007/0.000 | 0.001/-0.004/-0.003/-0.005 | -5.237/-4.546/-1.543 | -0.764/-1.517/-1.164/-1.021 |
| Call Bullish (223) | 0.011/0.009/0.001 | -0.001/0.003/0.000/0.004 | 4.846/4.100/0.771 | -0.012/-0.618/-1.347/-1.593 |
| Put Bearish (238) | 0.011/0.009/0.001 | 0.001/0.002/0.002/0.006 | 4.210/3.042/-0.269 | 0.213/0.377/-0.512/-1.512 |
| Put Bullish (230) | -0.007/-0.008/0.000 | 0.000/-0.006/-0.002/-0.007 | -5.071/-3.917/-2.747 | -0.498/-0.341/-0.061/-2.099 |

(PE pre/post columns are in the full console output and CSV; omitted here for width.) **The same
pattern already reported in the Multi-Day Behaviour Validation section above is reconfirmed with
independently-computed numbers**: futures pre/post movement stays at the ~0.00-0.01% scale in
every row, while the CE/PE pre-signal movement is 1-2 orders of magnitude larger (up to 5.2%) and
the post-signal movement is smaller and often opposite in sign to the pre-signal move -- consistent
with the crossover mostly confirming an already-happened option move rather than anticipating one,
same conclusion as before, now cross-checked via a differently-built dataset.

### Point 9 -- session-bucket breakdown (descriptive only)

`OptionDataIncomplete` is the single largest category in EVERY session bucket, ranging 22%
(11:00-12:00, 155/626) to 38% (14:00-15:00, 538/1399) -- consistent with the fast event clock
producing more/shorter bars (hence more gaps) later in the session, matching the existing 2026-09-08
duration-distribution finding. No session filter was created, per the task's explicit instruction.

### Point 11/14 -- contract-transition and data-quality audit

| Date | Events | Futures avail | Spot avail | CE avail | PE avail | CE transitions | PE transitions |
|---|---|---|---|---|---|---|---|
| 2026-09-08 | 1,591 | 1,591 | 1,379 (86.7%) | 1,402 (88.1%) | 1,402 (88.1%) | 61 | 61 |
| 2026-09-15 | 2,728 | 2,728 | 2,110 (77.4%) | 2,136 (78.3%) | 2,135 (78.3%) | 223 | 223 |
| 2026-09-22 | 2,123 | 2,123 | 1,684 (79.3%) | 1,697 (79.9%) | 1,699 (80.0%) | 105 | 105 |

Futures is always 100% available (by construction -- every futures event bar has a real close).
Spot availability (77-87%) is somewhat lower than CE/PE availability (78-88%) -- the NIFTY Index
feed has its own gaps, independent of the options feed's. **CE and PE transition counts are
identical on every day** -- expected, since both sides select ATM from the SAME futures close
price each event, so a strike roll always affects both legs simultaneously.

### What was observed

The engineering pipeline behaves as specified: spot data was verified present (not assumed),
contract transitions are detected and correctly exclude change attribution across them, missing
data is marked and never fabricated (confirmed by dedicated tests), and the user's two named
hypothesis patterns both occur in comparable, non-trivial volume with zero stale/transition
contamination. The "textbook" delta-consistent CE/PE response pattern is about twice as frequent
as the named divergence pattern in this sample. The crossover pre/post asymmetry (large pre-signal
option move, small/reversing post-signal move, near-zero underlying move throughout) is
reconfirmed independently of the original Multi-Day Behaviour Validation section.

### What remains unproven

Whether the divergence pattern (CE weakening while PE strengthens on a rising underlying, or the
mirror) has any forward-looking value, whether it's more or less common on different days/session
times beyond what's described here, whether the CE/PE magnitude asymmetry reflects a genuine
options-market effect or is an artifact of these 3 days' specific strikes/moneyness, and whether a
futures-vs-spot divergence (not separately tabulated this pass) would itself show a distinct
pattern. None of this is claimed as a trading edge, and no recommendation is made to proceed to
Experiment 2.

### Files/code changed

New: `UnderlyingOptionRelationshipObservation.cs`, `UnderlyingOptionRelationshipRecorder.cs`,
`UnderlyingOptionRelationshipSummary.cs`, CLI `vc0dte-relationship` (frozen config, `--out=`
required, no override flags). New tests: `UnderlyingOptionRelationshipRecorderTests.cs` (6 tests),
`UnderlyingOptionRelationshipSummaryTests.cs` (10 tests). `dotnet build NiftySignal.slnx`: 0
warnings/errors. `dotnet test NiftySignal.slnx`: 826/826 passing (810 baseline + 16 new).

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship 2026-09-01 2026-09-23 --out=vc0dte-relationship.csv
```

## Forward Validation of Underlying-CE-PE Relationship Patterns (2026-09-23)

**Separate command (`vc0dte-relationship-forward`), keeps `vc0dte-relationship` itself frozen.**
New file: `ForwardValidationAnalysis.cs` (pure metrics/classification/bootstrap calculators only).
Every forward change is computed via the EXISTING, unmodified
`UnderlyingOptionRelationshipSummary.ComputeFuturesChange`/`ComputeSpotChange`/`ComputeCeChange`/
`ComputePeChange` -- same-contract/missing/stale rejection was already built and tested there, not
re-implemented. No Experiment 1 or relationship-layer file, formula, or CSV column was touched.

### A. Implementation

Population A/B (state-classified, clean at T) coincide in this codebase: `IsStale` is only ever
true when `MissingData` is also true (confirmed by inspection), so requiring "not missing" (which
state-classification already requires) automatically means "not stale" too -- an honest finding,
not a forced distinction. Population C (per-horizon coverage) is enforced naturally: every
`Compute*Change` call already returns `null` when the specific horizon's same-contract/available
condition fails, so the reported `N` at each horizon IS Population C for that horizon -- no extra
filtering code needed.

### B. Data quality

6,442 total relationship observations (unchanged from the prior analysis); **3,129 (48.6%) fall
into one of the four named states** and form the forward-validation population. Per-horizon
coverage shrinks as expected (contract transitions/missing bars accumulate over a longer window) --
e.g. State2 (Pattern A) CE coverage: 493/578 (+1) -> 452/578 (+3) -> 432/578 (+5) -> 404/578 (+10).

### C. Four relationship states -- complete pooled table (horizons +1/+3/+5/+10)

| State | n | Fut med% (+1/+3/+5/+10) | Fut pos%/neg% (+10) | CE med% (+1/+3/+5/+10) | PE med% (+1/+3/+5/+10) |
|---|---|---|---|---|---|
| State1 Normal bullish confirm (UnderlyingUp_CEUp_PEDown) | 913 | 0.000/-0.003/-0.003/-0.004 | 41.6/56.8 | 0.354/0.273/-0.125/-0.378 | -0.309/-0.207/0.158/0.146 |
| State2 Bullish divergence = Pattern A (UnderlyingUp_CEDown_PEUp) | 578 | -0.004/-0.011/-0.009/-0.015 | 30.3/67.1 | -0.203/-0.784/-0.692/-1.554 | 0.141/0.623/0.496/0.868 |
| State3 Normal bearish confirm (UnderlyingDown_CEDown_PEUp) | 1081 | 0.000/0.000/0.000/-0.002 | 45.0/52.1 | -0.602/-0.585/-0.637/-1.160 | 0.456/0.417/0.373/0.836 |
| State4 Bearish divergence = Pattern B (UnderlyingDown_CEUp_PEDown) | 557 | 0.002/0.004/0.005/0.005 | 55.0/42.6 | 0.033/-0.499/-0.690/-1.294 | 0.076/0.369/0.562/0.471 |

Full per-metric detail (mean, positive/negative/zero%, mean-absolute, CE-minus-PE, same/opposite
direction%) at every horizon is in the CSV and console output; only medians shown here for width.

### D. Pattern A (Underlying Up + CE Down + PE Up) -- the most internally consistent finding

**Forward futures direction FLIPS from the contemporaneous "Up" reading to predominantly negative**:
median forward futures change is negative at every horizon (-0.004% to -0.015%, deepening with
horizon) and negative% exceeds positive% at every single horizon (59-68% negative vs 22-30%
positive). **This holds independently on all 3 included days** (Section J). **The CE/PE divergence
itself deepens going forward**: CE-minus-PE (%) goes from -0.77 (+1) to -2.98 (+10), i.e. CE keeps
falling relative to PE rather than the divergence closing. Bootstrap 95% CI for the median (+3,
seed=42): Futures [-0.0133%, -0.0084%] (does not cross zero), CE [-1.180%, -0.476%] (does not
cross zero), PE [+0.286%, +0.898%] (does not cross zero).

**Observed**: after this pattern, futures more often moves opposite to its own contemporaneous
direction, and the CE/PE divergence continues rather than closes, consistently across all 3 days.
**Possible interpretation**: consistent with (not proof of) either a short-horizon mean-reversion
tendency in the underlying at this event-bar cadence, or the divergence pattern capturing something
that continues to unfold over the following few event bars. **Unproven**: whether this holds beyond
3 days, whether it is specific to this DTE/threshold/window configuration, or whether it would
survive realistic execution.

### E. Pattern B (Underlying Down + CE Up + PE Down) -- the mirror pattern, with a key asymmetry

**Forward futures direction also flips** (from "Down" to predominantly positive): median forward
futures change is positive at every horizon (+0.002% to +0.005%) with positive% exceeding
negative% at every horizon (55-59% positive vs 31-43% negative), again holding on all 3 days.
**But the CE/PE divergence behaves DIFFERENTLY here than in Pattern A**: CE-minus-PE (%) starts
near zero at +1 (-0.03) and only becomes clearly negative by +3/+5/+10 (-0.97/-2.14/-2.02) --
i.e. CE (which rose contemporaneously) ends up giving back its relative gain against PE, closer to
a closing/reversing pattern for the divergence itself, unlike Pattern A's deepening divergence.
Bootstrap 95% CI (+3): Futures [+0.0026%, +0.0077%] (does not cross zero), CE [-0.837%, -0.165%]
(does not cross zero), PE [+0.031%, +0.595%] (lower bound very close to zero -- the weakest of the
four state/leg combinations tested).

### F. Futures vs Spot

**Same-direction agreement between futures and spot INCREASES with horizon for every state**: from
~50-57% at +1 to ~72-76% at +10 (e.g. State1: 52.3%->73.8%; State2: 55.4%->72.0%). Both series
individually show the same sign pattern as each other (futures/spot medians point the same way at
every horizon within each state) -- futures and spot tell a broadly consistent, not materially
different, story once measured over 3+ event bars; the two only disagree more often at the
shortest (+1) horizon, consistent with short-horizon noise rather than a structural futures/spot
split.

### G. Pre-event vs forward movement

Every state's contemporaneous (pre-event) direction is, by construction, the state's own name
(Up for State1/2, Down for State3/4). The forward futures direction for State2/State4 (the two
divergence patterns) moves OPPOSITE to that pre-event direction at every horizon (Section D/E);
for State1/State3 (the two confirmation patterns) forward futures stays much closer to flat
(median magnitude under 0.005% at every horizon, positive/negative split close to 50/50) --
i.e. "normal confirmation" states show comparatively little forward underlying movement in either
direction, while "divergence" states show a comparatively larger, opposite-direction subsequent
move. Neither is described as a reversal signal -- both are reported as measured, opposite- or
same-direction movement only.

### H. Absolute Rs vs percentage behaviour

The CE/PE relationship (CE weakens, PE strengthens for divergence-adjacent states, or vice versa)
holds in both measurement systems -- percentage and Rs figures never disagree in SIGN anywhere in
the pooled table, only in relative magnitude (consistent with the previously-documented Call/Put
premium-level asymmetry). No representation is treated as "more correct"; both are reported side
by side in the CSV.

### I. Session stability

Pattern A's negative forward-futures tilt is present in EVERY session bucket (positive% stays
20-31%, negative% 59-78%, across all 7 buckets) -- not dominated by one part of the day. Pattern
B's positive tilt is similarly present in every bucket (positive% 49-66%). **No bucket shows a sign
flip relative to the pooled result for either pattern** -- unlike the earlier-documented Call
extrinsic-value collapse in 15:00-15:30 (which was about MFE/MAE magnitude, not this forward-
direction question).

### J. Day-by-day stability

**Both Pattern A and Pattern B's forward-futures-direction tilt hold independently on all 3 days**,
not just pooled (see table in Section D/E and the full breakdown in the console/CSV output) --
this is the single most important robustness check in this analysis, and it passes for the two
patterns of primary interest. State1/State3 (confirmation states) show forward futures much
closer to flat on all 3 days too, consistent with the pooled result.

### K. Low-underlying-movement analysis

Terciles of pre-event |futures 1-bar %change| across all 6,439 valid readings: **Low33=0.0013%,
High67=0.0143%** (documented data-derived quantile, not chosen for a favourable result). Even in
the LOW tercile (near-zero contemporaneous underlying movement), CE and PE still moved materially
(mean-absolute Rs 0.457 and 1.097 respectively) among the state-classified subset -- confirming
0-DTE option premium movement occurs even when the underlying itself barely moved, consistent with
the earlier Multi-Day Behaviour Validation's "option movement dominates underlying movement"
finding, now directly quantified by tercile rather than inferred.

### L. What is actually observed

Both named divergence patterns (A and B) show a forward futures direction that is OPPOSITE to
their own contemporaneous direction, at every horizon tested, consistently across all 3 days and
every session bucket -- descriptively real, not a pooling artifact of one day or one time window.
The two patterns differ in how their own CE/PE divergence evolves forward: Pattern A's divergence
deepens, Pattern B's divergence partially closes. Option (CE/PE) movement is confirmed present even
in the lowest tercile of underlying movement.

### M. What remains unproven

Whether this "opposite-direction forward futures" pattern is specific to the 1300-contract/3-10-SMA
configuration, whether it would survive realistic execution costs/slippage, whether it holds beyond
these 3 specific days, and -- per the task's own explicit instruction -- **no demand/supply
interpretation is offered anywhere in this section**: only "CE premium decreased"/"PE premium
increased" (or the reverse) is stated, never "put demand" or "call supply." No trading rule, entry/
exit logic, score, or Greek/IV normalization was introduced. **This is not a recommendation to
trade on this pattern, and it does not modify any Experiment 1 conclusion.**

### N. Tests/build

New tests: `ForwardValidationAnalysisTests.cs` (10 tests: metrics, direction classification x2,
terciles, deterministic bootstrap x2), plus one no-lookahead regression test added to
`UnderlyingOptionRelationshipRecorderTests.cs`. `dotnet build NiftySignal.slnx`: 0 warnings/errors.
`dotnet test NiftySignal.slnx`: **846/846 passing** (826 baseline + 20 new).

### O. Files changed

New: `ForwardValidationAnalysis.cs`, CLI `vc0dte-relationship-forward` (frozen config, `--out=`
required, no override flags). No existing file's formulas/behavior changed.

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-forward 2026-09-01 2026-09-23 --out=vc0dte-relationship-forward.csv
```

## Forensic Validation of the Pattern A/B Forward-Futures Finding (2026-09-23)

**HARD FREEZE respected throughout**: new file `ForensicValidationAnalysis.cs` (pure calculators)
+ new CLI `vc0dte-relationship-forensic`, both reusing `UnderlyingOptionRelationshipRecorder`/
`UnderlyingOptionRelationshipSummary`/`ForwardValidationAnalysis` completely unchanged. No
threshold, classification, command, or execution-model file was touched.

### 1. Code-path trace -- no future-information leakage confirmed

`RelationshipObservation` at event T is built entirely from `futureBars[T].Close` and
`optionBars` at `EventId==T`/`T-1` (see `UnderlyingOptionRelationshipRecorder.RecordAsync`'s own
loop, unchanged) -- `FuturesChange1`/`CeChange1`/`PeChange1`/`RelationshipCategory` never read
`futureBars[T+1]` or later. T+1/T+3/T+5/T+10 are simply `futureBars`/`optionBars` entries at
`EventId == T + horizon`, looked up only AFTER T's own classification is already fixed (in a
separate function, `UnderlyingOptionRelationshipSummary.Compute*Change`, called by the CLI only
after loading T's row). Contract identity is enforced by `SameContract` (token equality at both
endpoints AND no `ContractTransition` flag anywhere strictly between them); missing/stale option
bars make the underlying `AverageLtp` null, which propagates to a null change, never a fabricated
value. **A new regression test
(`RecordAsync_ClassificationAtEventT_NeverDependsOnDataAfterT_NoLookahead`) proves this directly**:
two scenarios identical through event 1 but wildly different at event 2 (futures crashing, CE
rocketing, PE cratering) produce byte-identical classification/direction/MA state at event 1.

### 2. Raw forensic examples

9 examples per pattern (first/middle/last qualifying observation per day, deterministic, none
hand-picked for a favourable result) were printed with full T-1/T/T+1/T+3/T+5/T+10 prices --
included in the full console output and the forensic CSV. Spot-checking confirms every printed
forward return matches the raw printed prices by hand (e.g. Pattern A, 2026-09-08 event#20: CE
19.1667 -> 18.8750 at T+1 = -1.52%, matches the printed forward return exactly).

### 3. Is the effect driven by tiny underlying moves? -- NO, the opposite

| | Pattern A pre-move | Pattern B pre-move | Global (all 6,439 obs) |
|---|---|---|---|
| Median \|%change\| | 0.0126% | 0.0107% | 0.0060% |
| % below global P25 | **0.0%** | **0.0%** | -- |
| % above global P75 | 33.0% | 29.1% | -- |

**Both patterns require an ABOVE-median pre-event move to occur at all** -- zero observations of
either pattern fall below the global P25, and roughly a third exceed the global P75. This directly
answers the section-3 question: the divergence patterns are NOT concentrated in near-zero-movement
periods; if anything they need a real move to be classified in the first place (mechanically
expected, since CE/PE must ALSO show a real, nonzero directional move to qualify for the state).

### 4. Four-state comparison (no ranking)

At every horizon, **all four states show OppositeDirection% > SameDirection%** (a general
mean-reversion-like tendency at this event-bar cadence, already noted in the Multi-Day Behaviour
Validation section above) -- but the two divergence states are consistently MORE opposite-leaning
than the two confirmation states: at +1, State2/State4 (Pattern A/B) show 59.3%/54.0% opposite vs
State1/State3's 44.5%/42.2%. The divergence patterns amplify a tendency present everywhere, they
do not invent one from nothing.

### 5. Underlying-only baseline -- CE/PE adds real information beyond direction alone

| Horizon | UnderlyingUpOnly (n=2,238) neg% | Pattern A (n=578) neg% | UnderlyingDownOnly (n=2,472) pos% | Pattern B (n=557) |
|---|---|---|---|---|
| +1 | 47.2% | **59.3%** | 43.5% (implied pos%) | medFwd=+0.0021% |
| +10 | 60.6% | **67.1%** | -- | medFwd=+0.0047% |

Knowing only "underlying is up" gives a much weaker forward tilt (47-61% negative) than knowing
"underlying is up AND CE down AND PE up" (59-67% negative) -- CE/PE information changes the
forward distribution relative to direction alone.

### 6. CE/PE-only baseline -- the three-way combination is stronger than CE/PE alone

| Horizon | CEDown+PEUp, any underlying (n=1,870) medFwd | Pattern A (n=578) medFwd |
|---|---|---|
| +1 | 0.0000% | -0.0038% |
| +10 | -0.0047% | **-0.0146%** (>3x stronger) |

Requiring the underlying direction to ALSO match roughly triples the effect size at the +10
horizon relative to CE/PE divergence alone, regardless of underlying direction. Neither the
underlying-only nor the CE/PE-only baseline reproduces Pattern A/B's own magnitude -- consistent
with this being a genuinely three-way relationship, not fully explained by either piece alone.

### 7. Pattern A vs B asymmetry -- reconfirmed, hypotheses only

Pattern A's CE/PE divergence deepens forward (CE-PE% -0.77 -> -2.98 from +1 to +10); Pattern B's
partially closes (-0.03 -> -2.02, starting near zero then diverging the OTHER way). **Possible
explanations, not decided between**: 0-DTE theta decay (asymmetric if the two legs sit at
different moneyness at the moment of measurement), delta/gamma differences, IV/skew, liquidity
differences between the two legs, or market-maker repricing behavior. No Greeks or IV were
computed to distinguish between these.

### 8. ATM/contract-transition audit -- effect is NOT concentrated around transitions

Only 3.5-3.6% of Pattern A/B observations have an ATM transition at T+1. The tiny transitioned
subgroup (n=20 each) shows, if anything, a WEAKER or opposite-signed median than the
non-transitioned majority (e.g. Pattern A +1: transitioned=+0.0030% vs non-transitioned=-0.0038%)
-- the effect is carried by the non-transitioned 96%+ of observations, not concentrated in or
caused by strike rolls.

### 9. Session stability with actual counts -- no LOW SAMPLE buckets, effect present throughout

Every one of the 14 (pattern x session) cells has n >= 41 (none flagged LOW SAMPLE under the
documented n<30 threshold). OppositeDirection% exceeds 50% in 13 of 14 cells at +1 (the one
exception, Pattern B 10:00-11:00, is 50% flat) and effectively all cells by +3. Not dominated by
one part of the session.

### 10. Distribution robustness -- median agrees with mean, not a few large moves

Mean and median agree in sign at every horizon for both patterns. Top-1%-of-observations
contribution to the total ranges 4.3-10.3% (modest -- a genuinely single-outlier-driven effect
would approach 100%); top-10% contribution is 32-56% (a meaningful but not overwhelming share).
**Conclusion: this is not an artifact of a handful of extreme moves.**

### 11. Permutation sanity check (seed=42, 1,000 permutations -- NOT a significance test)

| Pattern | Horizon | Observed median | Percentile rank in 1,000 random same-sized draws |
|---|---|---|---|
| A | +1 to +10 | -0.0038% to -0.0146% | **0.0** (every horizon) |
| B | +1 to +10 | +0.0021% to +0.0047% | **100.0** (every horizon) |

The observed median falls OUTSIDE the entire permutation range at every horizon for both patterns
-- a random same-sized subset of the full 6,412-6,439-observation population essentially never
reproduces a median this extreme in either direction, in this specific 3-day dataset. **This is a
sanity check, not a significance claim**: the permutation range itself is very narrow (the global
population's own median sits very close to zero), so "outside the range" here means "detectably
different from a typical same-sized random draw," not "statistically significant" in any formal
sense with only 3 underlying days of real data.

### 13. Multi-day expansion readiness

Scanned 2026-08-01 through 2026-10-31 (cheap existence-only queries, no bar-building, no
methodology change): **exactly 3 eligible 0-DTE days exist in the local `niftysignal_vm_copy`
mirror -- the same 3 already used (2026-09-08, 2026-09-15, 2026-09-22)**. No additional
independent days are currently available in this local dataset. The existing, frozen
`vc0dte-relationship-forward` command can process any future dates directly by date range with no
methodology change, whenever more 0-DTE days become available in this local mirror (e.g. after a
refresh from the VM).

### Final conclusion (answering the 10 required questions)

1. **Pattern A reproducible at raw-event level?** Yes -- spot-checked against printed raw prices.
2. **Pattern B reproducible at raw-event level?** Yes -- same check.
3. **Visible using medians as well as means?** Yes -- sign agrees at every horizon, both patterns.
4. **Different from underlying-only baseline?** Yes -- Pattern A/B show a materially stronger
   directional tilt than "underlying direction alone" (Section 5).
5. **Different from CE/PE-only baseline?** Yes -- requiring the underlying direction too roughly
   triples the +10 effect size versus CE/PE divergence alone, regardless of underlying (Section 6).
6. **Concentrated around ATM transitions?** No -- only 3.5-3.6% of observations involve one, and
   that subgroup does not show a stronger effect (Section 8).
7. **Dominated by a small number of observations?** No -- top-1% contribution is modest (4-10%),
   median tracks the mean (Section 10).
8. **Evidence of a methodological artifact?** None found across every check attempted (no-lookahead
   proof, raw-example spot-check, tiny-move check, baseline comparisons, transition audit, session
   counts, distribution shape, permutation sanity check).
9. **Additional 0-DTE days available?** **Zero beyond the 3 already used**, in this local mirror,
   as of this scan.
10. **What remains unknown?** Whether this survives on independent days beyond these 3 (none
    currently available locally), whether it survives realistic execution costs/slippage, and the
    Pattern A vs B asymmetry's actual cause (Section 7's hypotheses are undecided).

**No trading edge is claimed. No signal is proposed. Per the user's own explicit instruction, the
only next-stage direction is: run this SAME frozen methodology on a much larger set of independent
0-DTE days once more become available** -- none are available locally right now.

### Tests/build

New: `ForensicValidationAnalysisTests.cs` (7 tests: percentiles, mean-contribution x2,
deterministic permutation x2, percentile-rank x1) + 1 no-lookahead regression test (already listed
under the Forward Validation section's own test additions). `dotnet build NiftySignal.slnx`: 0
warnings/errors. `dotnet test NiftySignal.slnx`: **853/853 passing** (846 baseline + 7 new).

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-forensic 2026-09-01 2026-09-23 --out=vc0dte-relationship-forensic.csv
```

## Episode-Level / Cluster-Level Validation (2026-09-23)

**HARD FREEZE respected throughout.** New file `EpisodeAnalysis.cs` (pure calculators) + new CLI
`vc0dte-relationship-episodes`, both reusing `UnderlyingOptionRelationshipRecorder`/
`Vc0DteBehaviorRecorder`/`UnderlyingOptionRelationshipSummary.Compute*Change`/
`ForensicValidationAnalysis.PermutationMedianDistribution` completely unchanged. An "episode" is a
maximal run of CONSECUTIVE events sharing the SAME `RelationshipCategory` -- the point being to
stop counting N consecutive event bars of one persistent condition as N independent observations.

### A. Implementation

`EpisodeAnalysis.DetectEpisodes` walks one day's chronological row sequence; continuity requires
the SAME category on the immediately-next event, any change (including to "ContractTransition"/
"OptionDataIncomplete"/another state) ends the episode, gaps are never bridged, and the same
pattern reappearing later always starts a NEW episode. **A contract transition can never occur
INSIDE an episode by construction**: the existing (unmodified) classifier already labels a
transitioned event "ContractTransition", which can never equal a pattern name -- confirmed
empirically (`AtmTransitionDuringEpisode` is False for every one of the 1,056 detected episodes).
The episode's signal point is deterministically its FIRST event (never the last, never the
"best"), per the task's own explicit requirement -- enforced directly in code, not by convention.

### B. Event-level vs episode-level counts -- the key finding of this task

| | Event-level | Episode-level | Events/episode |
|---|---|---|---|
| Pattern A | 578 | **548** | 1.1 |
| Pattern B | 557 | **508** | 1.1 |

**The clustering concern turns out to be nearly moot for this specific pattern.** Median episode
length is **1 event** for both patterns (P75 = 1 for both; max episode length is only 3 events for
Pattern A, 4 for Pattern B). The divergence patterns are overwhelmingly single-event-bar
occurrences at this cadence, not multi-bar persistent conditions -- there is very little
serial-correlation inflation to correct for in the first place.

### C. Pattern A episode results

| Horizon | Event-level median | Episode-level median | Event neg% | Episode neg% |
|---|---|---|---|---|
| +1 | -0.0038% | -0.0040% | 59.3% | 60.0% |
| +3 | -0.0105% | -0.0104% | 68.5% | 68.4% |
| +5 | -0.0094% | -0.0101% | 66.0% | 66.0% |
| +10 | -0.0146% | -0.0146% | 67.1% | 67.0% |

**Episode-level results are nearly identical to event-level results at every horizon** -- direct
confirmation that the original event-level finding was not an artifact of a few persistent
episodes being overcounted.

### D. Pattern B episode results

| Horizon | Event-level median | Episode-level median | Event pos% | Episode pos% |
|---|---|---|---|---|
| +1 | +0.0021% | +0.0017% | 54.0% | 53.5% |
| +3 | +0.0043% | +0.0042% | 59.1% | 57.9% |
| +5 | +0.0053% | +0.0051% | 57.9% | 57.8% |
| +10 | +0.0047% | +0.0046% | 55.0% | 55.0% |

Same conclusion as Pattern A: episode-level and event-level results agree closely at every
horizon.

### E. Episode duration/persistence

Pattern A: 548 episodes, median/P25/P75 event count all = 1, max = 3. Pattern B: 508 episodes,
median/P25/P75 = 1, max = 4. Duration buckets: **"1 event" episodes dominate both patterns** (520
of 548 for A, 461 of 508 for B); "2-3 events" is a small remainder (28 and 46 respectively);
"4-10 events" has exactly ONE observation total (Pattern B) and none for Pattern A. The 1-event
bucket alone reproduces the pooled directional tilt closely (Pattern A 1-event bucket: 69.4% neg;
Pattern B 1-event bucket: 58.8% pos) -- **the effect is carried by short, not long, episodes**,
opposite to what a "persistent-condition-driven" explanation would require.

### F. First-event vs later-event behaviour

Sample sizes collapse immediately past the 1st event (Pattern A: 548 first-events -> 28 second ->
only 2 third; Pattern B: 508 -> 47 -> 1), a direct, mechanical consequence of Section E's finding
that episodes are almost always 1 event long. **The data cannot meaningfully distinguish "does the
effect strengthen or weaken with persistence" beyond the 2nd event** -- n=2 and n=1 at the 3rd
event are too small to read anything into. What CAN be said: the effect is already present, at
comparable magnitude, at the very FIRST event of the episode (the same magnitude as the pooled
event-level/episode-level results in Sections C/D) -- it does not require several bars of
persistent divergence to appear.

### G. Pre-event underlying movement (late-stage-divergence context)

| | Prev-3 events median | Prev-5 median | Prev-10 median | Forward +10 median |
|---|---|---|---|---|
| Pattern A | +0.0034% | +0.0043% | +0.0021% | **-0.0146%** |
| Pattern B | -0.0042% | -0.0063% | -0.0103% | **+0.0046%** |

The cumulative movement over the 3-10 events BEFORE the episode starts is small and in the SAME
direction as the pattern's own defining condition (mildly positive before Pattern A, mildly
negative before Pattern B) -- consistent with a real, if modest, preceding move, smaller in
magnitude than the subsequent reversal. Described as a **possible late-stage divergence context**,
not a reversal signal, per the task's own explicit instruction.

### H. Day-by-day episode-level results

| Date | Pattern A episodes | +3 median | opp% | Pattern B episodes | +3 median | opp% |
|---|---|---|---|---|---|---|
| 2026-09-08 | 139 | -0.0084% | 68% | 152 | +0.0036% | 59% |
| 2026-09-15 | 219 | -0.0107% | 70% | 189 | +0.0077% | 61% |
| 2026-09-22 | 190 | -0.0130% | 67% | 167 | +0.0021% | 54% |

**All 3 days match the pooled sign at +3 for both patterns (3 of 3)** -- full day-by-day
consistency survives episode-level treatment. Not claimed as statistically significant from 3
days.

### I. Corrected episode-level permutation sanity check

Reuses the exact same `PermutationMedianDistribution` procedure as the event-level check
(unmodified), with the population changed from "one value per event" to "one value per episode"
(pooled across all 4 states, 2,572-2,577 episodes depending on horizon coverage):

| Pattern | Horizon | Observed episode median | Percentile rank |
|---|---|---|---|
| A | +1 to +10 | -0.0040% to -0.0146% | **0.0** (every horizon) |
| B | +1 to +10 | +0.0017% to +0.0046% | **100.0** (every horizon) |

Identical qualitative result to the event-level permutation check -- the observed episode-level
median falls outside the entire permutation range at every horizon for both patterns. Same
caveat as before: a sanity check against a narrow-range population, not a significance claim.

### J. What survives the clustering correction

Everything: the direction (negative for A, positive for B), the approximate magnitude at every
horizon, the day-by-day consistency (3 of 3 days both patterns), and the permutation-check
percentile ranks are all preserved almost unchanged between event-level and episode-level
treatment. This is explained by Section B/E's own finding: episodes are overwhelmingly single
events, so there was very little pseudo-replication to correct for in this specific case.

### K. What does not survive / cannot be assessed

**Nothing was found to NOT survive** -- but Section F's "does the effect strengthen with
persistence" question could not be meaningfully answered (sample collapses to n=2/n=1 beyond the
2nd event). This is a data-availability limit, not a finding of the effect weakening.

### L. What remains unproven

Whether the "possible late-stage divergence context" (Section G) is anything beyond incidental,
whether the effect would look different with more persistent (multi-event) episodes if a larger
sample ever produces them, and -- as with every prior section of this document -- generalization
beyond these 3 days.

### Final conclusion (answering the 10 required questions)

1. **How many events -> episodes?** Pattern A: 578 -> 548 (1.1 events/episode). Pattern B: 557 ->
   508 (1.1 events/episode). Very little clustering existed to begin with.
2. **Pattern A directionally consistent at episode level?** Yes -- median -0.0040% to -0.0146%
   across horizons, matching event-level closely.
3. **Pattern B directionally consistent at episode level?** Yes -- median +0.0017% to +0.0046%.
4. **Concentrated in long persistent episodes?** No -- the opposite: carried by the (overwhelming
   majority) 1-event episodes; the tiny multi-event subset is weaker/more mixed.
5. **Does the effect exist at first appearance?** Yes -- the 1st-event numbers ARE essentially the
   pooled numbers, since almost every episode IS just one event.
6. **Generally preceded by a strong underlying move?** A modest, same-direction move over the
   preceding 3-10 events, smaller in magnitude than the subsequent reversal -- described as a
   possible context, not a precondition or a reversal signal.
7. **Survives serial-clustering correction?** Yes -- because there was very little clustering to
   correct for; episode-level results are nearly identical to event-level results.
8. **Different from underlying-only condition?** Yes (reconfirmed from the prior forensic task,
   unchanged by this episode-level pass).
9. **Pattern A/B asymmetry still present?** Yes -- unaffected by this task (asymmetry was about
   CE/PE divergence evolution, not addressed again here; see the prior Forensic Validation
   section).
10. **What additional data would be required?** More independent 0-DTE days (currently exactly 3
    exist locally, per the prior forensic scan) -- both to strengthen the day-by-day case and to
    accumulate enough multi-event episodes to answer Section F's persistence question, which the
    current 3-day sample cannot.

**No trading edge is claimed. No signal, score, or rule was created. This remains a research
finding, now additionally confirmed not to be an artifact of serially-correlated event bars.**

### M. Tests/build

New: `EpisodeAnalysisTests.cs` (17 tests: start/continuation/termination/gap/contract-transition/
day-end-closure/price-capture/crossover-attachment/duration-bucket/deterministic-first-event
coverage). `dotnet build NiftySignal.slnx`: 0 warnings/errors. `dotnet test NiftySignal.slnx`:
**870/870 passing** (853 baseline + 17 new).

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-episodes 2026-09-01 2026-09-23 --out=vc0dte-relationship-episodes.csv
```

## Incremental Information / Conditional Analysis (2026-09-23)

**HARD FREEZE respected throughout.** New file `ConditionalMovementAnalysis.cs` (one new pure
calculation: tercile-bucket classification) + new CLI `vc0dte-relationship-conditional`, reusing
`UnderlyingOptionRelationshipRecorder.RecordAsync`, every `Compute*Change` function,
`ComputeForwardMetrics`/`ComputeTerciles`, and `ComputePercentiles` completely unchanged.

**Question**: does CE/PE divergence carry information about subsequent underlying direction
beyond simply knowing the underlying already moved strongly in the opposite direction (Hypothesis
2, "late-stage reversal context only") -- or does it add something beyond that (Hypothesis 1)?

**Control-group design, confirmed with the user before implementation** (this was flagged as a
genuine methodological ambiguity per the task's own "stop and ask" instruction): the control
population for Pattern A is every event sharing Pattern A's own contemporaneous Up direction,
excluding data-incomplete/contract-transition rows and Pattern A itself, tercile-matched on
`|prior-N-event movement|` using terciles computed WITHIN that same Up-direction population
(symmetric for Pattern B/Down). Holding contemporaneous direction constant was necessary because
the forensic task already showed Up-only and Down-only populations have systematically different
forward tilts on their own, independent of CE/PE.

### Population sizes

UpPop (excl. incomplete/transition) = 1,686 -> Pattern A = 578, Control A = 1,108. DownPop = 1,847
-> Pattern B = 557, Control B = 1,290.

### Experiment 1 -- prior movement and forward returns

Prior -1 is, AS EXPECTED, tautological with the pattern's own defining direction (100%/0% split)
-- reported explicitly, not treated as a finding. **Prior -3/-5/-10 show only a MODEST lean in the
pattern's own direction** (Pattern A: 53.5-56.4% positive, not overwhelming; Pattern B: 33.2-35.9%
positive, i.e. 61-64% negative) -- the patterns are not occurring almost exclusively after a large
prior move; there is real dispersion in the preceding-movement population.

### Experiments 2/4 -- the central result: tercile-stratified Pattern vs Control

Across **every one of 72 (pattern x tercile x prior-horizon x forward-horizon) cells tested**
(3 prior horizons x 3 terciles x 4 forward horizons x 2 patterns), **Pattern A's median forward
futures return is more negative than its matched Control's, and Pattern B's is more positive than
its matched Control's** -- with NO exception. Representative cells (prior -3 terciles, forward
+10):

| Tercile | Pattern A median | Control A median | Pattern B median | Control B median |
|---|---|---|---|---|
| Low | -0.0129% | -0.0021% | +0.0043% | -0.0042% |
| Mid | -0.0165% | -0.0074% | +0.0085% | +0.0000% |
| High | -0.0141% | -0.0083% | +0.0017% | -0.0004% |

**Control A/B are themselves close to flat (often exactly 0.0000% at +1/+3) while Pattern A/B
retain a clear directional tilt of comparable or larger magnitude than the ORIGINAL, unconditioned
pooled result.** This is the key finding: conditioning on comparable prior movement (holding
contemporaneous direction fixed) does NOT explain away the CE/PE-divergence-associated tilt --
the pattern group and the matched-movement control group behave differently, at every tercile,
at every horizon.

### Experiment 5 -- first-event timing

Since episode analysis already established that Pattern A/B episodes are overwhelmingly
1-event, Experiment 1's own numbers (prior -1/-3/-5/-10 alongside forward +1/+3/+5/+10, all
computed at the same signal point) already ARE the "first-event timing" view -- no separate
recomputation was needed. The pattern is consistent with "CE/PE divergence appears WHILE a modest
move is still unfolding, not only after an already-large, clearly-finished move" -- described
descriptively, not labeled a reversal signal.

### Experiment 6 -- day-by-day robustness (prior -3 terciles, +3 forward horizon)

**Pattern A**: more negative than Control on all 9 (day x tercile) cells, no exception.
**Pattern B**: more positive than Control on 8 of 9 cells; the one exception (2026-09-22, High
tercile: Pattern +0.0026% vs Control +0.0030%) is a near-tie, not a reversal of direction. **Only
3 independent trading days are available -- this is explicitly NOT treated as a statistically
significant result**, per the task's own instruction.

### Experiment 7 -- existing permutation result (context only)

The episode-level permutation check (unmodified, seed=42, 1,000 permutations) already placed
Pattern A's observed median at percentile 0.0 and Pattern B's at percentile 100.0 of the
permutation distribution at every horizon -- referenced here as context, not recomputed or
replaced.

### Does CE/PE divergence add information beyond prior underlying movement?

**Descriptively, yes -- the conditional relationship does not explain away after conditioning on
comparable prior movement.** Every tercile-matched comparison (72 cells) shows the pattern group
retaining a directional tilt the matched control group does not show to the same degree, and this
holds on all 3 days for Pattern A and 8 of 9 day/tercile cells for Pattern B. This is consistent
with Hypothesis 1 (incremental information) rather than Hypothesis 2 (the effect being purely a
symptom of an already-large, already-finished underlying move) -- **stated as what the data is
consistent with, not as a proven mechanism.**

### What remains unproven

Whether this incremental-information pattern would survive on independent days beyond the current
3, whether it survives realistic execution, and what (if anything) explains it mechanically --
theta, gamma, IV/skew, liquidity, and market-maker repricing remain unexamined hypotheses, exactly
as flagged in the earlier Forensic Validation section. **No edge is claimed, no signal is
proposed, and Pattern A/B are not labeled reversal signals.**

### Tests/build

New: `ConditionalMovementAnalysisTests.cs` (6 tests: tercile-bucket boundary classification,
independence from hidden recomputation). `dotnet build NiftySignal.slnx`: 0 warnings/errors.
`dotnet test NiftySignal.slnx`: **876/876 passing** (870 baseline + 6 new).

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-conditional 2026-09-01 2026-09-23 --out=vc0dte-relationship-conditional.csv
```

## Option Response Validation (2026-09-24)

**HARD FREEZE respected throughout. BEHAVIOUR ONLY -- no entries, exits, P&L, bid/ask execution,
stop loss, target, or threshold tuning anywhere in this experiment.** New file
`OptionResponseAnalysis.cs` (one new pure calculation: exclusion-reason classification) + new CLI
`vc0dte-relationship-option-response`, reusing `UnderlyingOptionRelationshipRecorder.RecordAsync`,
every `Compute*Change` function, `ComputeForwardMetrics`/`ComputeTerciles`/`ComputePercentiles`,
`ConditionalMovementAnalysis`'s tercile classifier, and `Vc0DteBehaviorSummary.SessionBucket`, and
the EXACT SAME tercile-matched control-group construction from the conditional-analysis task,
completely unchanged.

**Question**: does the CE/PE-divergence relationship translate into a materially different
subsequent response in the directionally-appropriate option (PE for Pattern A, CE for Pattern B)?

### 1. Sample counts and exclusions

Pattern A = 578, Control A = 1,108. Pattern B = 557, Control B = 1,290. Coverage shrinks with
horizon as expected: Pattern A's PE is `Available` for 493/578 (+1) down to 403/578 (+10);
Pattern B's CE for 490/557 (+1) down to 392/557 (+10). `ContractTransition` grows from ~3.5% (+1)
to ~17-18% (+10) for both patterns -- a real data-quality constraint on the longest horizon,
reported explicitly rather than silently forward-filled.

### 2. Signalling option price level

Pattern A's signalling PE: median Rs67.31 (P25=54.05, P75=89.75). Pattern B's signalling CE:
median Rs32.71 (P25=18.38, P75=40.81) -- **notably cheaper than Pattern A's PE.** Flagged here
(section 8's own explicit instruction) since a cheaper option is mechanically more exposed to
theta/gamma effects overwhelming a modest directional drift -- a hypothesis for the asymmetry
found below, not a proven mechanism.

### 3/4. Pattern A results -- PE (primary) and CE (opposite)

| Horizon | Futures median | PE median% (pos%/neg%) | PE median Rs | CE (opposite) median% |
|---|---|---|---|---|
| +1 | -0.0038% | +0.14% (55.4%/44.4%) | +Rs0.11 | -0.20% |
| +3 | -0.0105% | **+0.62%** (59.6%/40.4%) | +Rs0.45 | -0.78% |
| +5 | -0.0094% | +0.50% (56.0%/44.0%) | +Rs0.38 | -0.69% |
| +10 | -0.0146% | **+0.87%** (57.3%/42.7%) | +Rs0.64 | -1.55% |

**PE rises, on median, at every horizon, positive more often than not, and the opposite-side CE
falls -- directionally consistent with a PE buyer's objective.**

### 5/6. Pattern A matched-control comparison

Across every one of 36 tercile-stratified cells (3 prior-horizons x 3 terciles x 4 forward-
horizons), **Pattern A's PE median return is higher than Control A's PE median return, with no
exception** -- e.g. prior-3, Low tercile, +3: Pattern +0.66% vs Control -0.48%; +10: Pattern
+0.50% vs Control -0.70%. Control A's own PE is frequently NEGATIVE where Pattern A's is positive.
**This is the clearest, most consistent result across this entire research thread.**

### 7/8. Pattern B results -- CE (primary) and PE (opposite)

| Horizon | Futures median | CE median% (pos%/neg%) | CE median Rs | PE (opposite) median% |
|---|---|---|---|---|
| +1 | +0.0021% | +0.03% (51.4%/48.6%) | +Rs0.01 | +0.08% |
| +3 | +0.0043% | **-0.50%** (43.9%/55.9%) | -Rs0.12 | +0.37% |
| +5 | +0.0053% | **-0.69%** (40.1%/59.9%) | -Rs0.16 | +0.56% |
| +10 | +0.0047% | **-1.29%** (41.8%/58.2%) | -Rs0.29 | +0.47% |

**CE falls on median at every horizon beyond +1, and is positive LESS than half the time at every
horizon beyond +1 (40-44%)** -- the OPPOSITE of what a CE buyer following Pattern B would want,
despite the underlying itself moving modestly in the expected (positive) direction.

### 9/10. Pattern B matched-control comparison -- mixed, not uniformly favourable

Unlike Pattern A, Pattern B's CE-vs-Control-B comparison is **inconsistent**: some cells favour
the pattern (prior-5 Mid, +10: Pattern -0.36% vs Control -2.17%), others favour the control
(prior-3 Mid, +10: Pattern -0.91% vs Control -0.64%; prior-10 High, +5: Pattern -0.87% vs Control
-0.52%). **No uniform directional advantage for Pattern B's CE over its matched control.**

### 11. Session-bucket diagnostic (0-DTE decay context)

Pattern A's PE shows a positive median at EVERY session bucket (range +0.14% to +1.45%). **Pattern
B's CE shows a NEGATIVE median at 6 of 7 session buckets** (the one exception, 13:00-14:00, is a
modest +0.53%; the worst is 15:00-15:30 at -2.51%, consistent with 0-DTE Call extrinsic-value
collapse into close, already documented in the Multi-Day Behaviour Validation section above).
**This is consistent with Pattern B's CE result being significantly influenced by ordinary 0-DTE
Call decay, present throughout the session and worst late in the day.**

### 12. Day-by-day

Pattern A's PE median is positive on all 3 days at nearly every horizon (one exception: 2026-09-08
+5, -0.11%, still 49% positive -- a near-tie, not a reversal). Pattern B's CE median is NEGATIVE
on 2026-09-08 and mostly negative on 2026-09-15/2026-09-22 at every horizon beyond +1 -- consistent
across all 3 days, not a single-day artifact, but consistently in the OPPOSITE direction from what
a CE buyer would want.

### 13. Does the relationship translate into the directionally appropriate option?

**Asymmetric answer -- explicitly not forced into one case for both patterns:**

- **Pattern A: Case A (strong confirmation).** PE rises on median at every horizon, beats its
  matched control with no exception across 36 cells, holds on all 3 days and every session
  bucket. The underlying relationship translates cleanly into the option an appropriate PE buyer
  would want.
- **Pattern B: Case C, leaning toward Case D (dominated by 0-DTE decay / result weak-to-absent in
  option prices).** The underlying itself moves modestly in the expected direction, but CE falls
  on median at every horizon beyond +1, underperforms 50% positive at every such horizon, shows a
  negative median in 6 of 7 session buckets, and does not show a consistent advantage over its
  matched control. **The underlying-direction relationship for Pattern B does not appear to
  translate into a favourable CE outcome in this sample** -- most consistent with ordinary 0-DTE
  Call decay dominating a real but small favourable directional drift, though this is stated as a
  hypothesis (per the price-level observation in Section 2), not confirmed by any theta/IV
  modeling (none was built, per the task's own explicit instruction).

### What remains unproven

Whether Pattern B's CE result would look different at a different DTE (not tested, per the task's
explicit "do not expand DTE yet" instruction), whether normalizing by an actual theta/IV measure
would change the picture, and -- as with every prior section -- generalization beyond these 3
days. Pattern A's clean result is also unproven beyond 3 days and unproven under realistic
execution costs (no simulation was run, per the task's explicit prohibition).

### Tests/build

New: `OptionResponseAnalysisTests.cs` (4 tests: exclusion-reason classification, precedence of
ContractTransition over MissingOrStaleData). `dotnet build NiftySignal.slnx`: 0 warnings/errors.
`dotnet test NiftySignal.slnx`: **880/880 passing** (876 baseline + 4 new).

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-option-response 2026-09-01 2026-09-23 --out=vc0dte-option-response.csv
```

## Option Response Decomposition (2026-09-24)

**Question**: why does Pattern A's PE respond strongly while Pattern B's CE does not (per the
prior "Option Response Validation" section)? Decompose each signalling option's price into
**intrinsic** (`max(Futures-Strike,0)` Call / `max(Strike-Futures,0)` Put) and **extrinsic**
(`OptionPrice - Intrinsic`) value, using only observable price/strike/futures data -- no IV, no
Greeks, no Black-Scholes, no theta modeling (explicitly out of scope, reserved for a possible
later experiment). Diagnostic/economic decomposition only -- no trading simulation, no strategy.

### 1. Implementation summary

New pure-calculation file `OptionValueDecomposition.cs` (`ComputeIntrinsic`, `ComputeExtrinsic`,
`ComputeMoneyness`) plus a new CLI command `vc0dte-relationship-decomposition`. **Underlying
instrument for intrinsic value: the NIFTY FUTURE's own close price (`FuturesClose`), never
spot** -- this was the same instrument the relationship's own dynamic-ATM selection already uses,
per the task's own explicit instruction, so this was not an open ambiguity. The signalling
strike is frozen at the signal event (`RelationshipObservation.AtmStrike` at T) and reused
unchanged for every forward horizon -- no strike is ever re-selected. Every return/availability
calculation reuses `UnderlyingOptionRelationshipSummary.ComputeCeChange`/`ComputePeChange`
unmodified for gating (SameContract/availability); the new code only splits the *same* observed
option price into two components. The Pattern A/B populations and their tercile-matched,
direction-held-constant control groups are the exact same construction used in the Conditional
Analysis and Option Response Validation experiments -- not redefined.

### 2. Sample counts / exclusions

3 eligible days loaded (2026-09-08: 1591 obs, 2026-09-15: 2728 obs, 2026-09-22: 2123 obs; same 3
days as every prior experiment -- no new data). Pattern A (Up, category=PatternA) n=578, Control A
(Up, not PatternA, not excluded) n=1108. Pattern B (Down, category=PatternB) n=557, Control B
(Down, not PatternB, not excluded) n=1290. Per-horizon counts shrink from the signal-event n as
the existing `ComputeCeChange`/`ComputePeChange` availability gate excludes missing/stale/contract
-transition rows (same exclusion reasons as the prior experiment, not new) -- e.g. Pattern A PE
n=578 at signal, 493/451/432/403 at +1/+3/+5/+10.

### 3. Pattern A -> PE decomposition

| Horizon | n | Option Rs (mean/med) | Intrinsic Rs (mean/med) | Extrinsic Rs (mean/med) |
|---|---|---|---|---|
| +1 | 493 | 0.17 / 0.11 | 1.17 / 0.00 | -0.99 / -0.25 |
| +3 | 451 | 0.54 / 0.45 | 1.33 / 0.00 | -0.79 / -0.36 |
| +5 | 432 | 0.34 / 0.38 | 1.43 / 0.00 | -1.08 / -0.45 |
| +10 | 403 | 0.64 / 0.64 | 1.50 / 0.00 | -0.86 / -0.75 |

Reconciliation (`Option change = Intrinsic change + Extrinsic change`) verified **exact** on every
row (max discrepancy ~1e-28, floating-point noise only, as expected since it's an algebraic
identity by construction). **Note on aggregation**: means reconcile exactly
(mean(Option) = mean(Intrinsic) + mean(Extrinsic), confirmed row-by-row above), but medians do
**not** sum this way (median is not a linear operator) -- the median columns are each the
population's own median, not additive; only the mean columns are the arithmetic decomposition of
the total.

Reading the means: intrinsic-change mean is consistently positive and grows with horizon
(1.17 -> 1.50), while extrinsic-change mean is consistently negative (-0.99 to -0.86). The net
positive total is a case of a positive intrinsic component outweighing a negative extrinsic
one -- i.e., a minority of events where the underlying moved solidly enough to gain real intrinsic
value are what keep the aggregate positive, against a backdrop where the "typical" (median)
event actually loses extrinsic value at every horizon.

### 4. Pattern A -> CE decomposition (opposite side, for completeness)

n=578/493/452/432/404. Option Rs mean -0.08/-0.29/-0.31/-0.47 across +1/+3/+5/+10; intrinsic mean
consistently negative (-1.31 to -2.11, i.e. losing intrinsic value as the underlying rises away
from the CE's moneyness), extrinsic mean consistently positive (+1.23 to +1.64). Included only
for completeness (the opposite side is not the pattern's directionally appropriate option) --
not a claim about Pattern A's PE result.

### 5. Pattern B -> CE decomposition

| Horizon | n | Option Rs (mean/med) | Intrinsic Rs (mean/med) | Extrinsic Rs (mean/med) |
|---|---|---|---|---|
| +1 | 490 | 0.02 / 0.01 | 1.14 / 0.00 | -1.12 / -0.11 |
| +3 | 465 | -0.13 / -0.12 | 1.18 / 0.00 | -1.31 / -0.40 |
| +5 | 446 | -0.30 / -0.16 | 1.19 / 0.00 | -1.49 / -0.52 |
| +10 | 392 | -0.29 / -0.29 | 1.27 / 0.00 | -1.56 / -0.72 |

Same mean-additivity confirmation applies. Here the intrinsic-change mean is also consistently
positive (1.14 -> 1.27, similar magnitude to Pattern A's PE case -- reflecting a real, if modest,
favourable underlying drift, consistent with the prior experiment's finding that Pattern B's
underlying does move in the expected direction) but the extrinsic-change mean is **larger in
magnitude and grows faster** (-1.12 at +1 widening to -1.56 at +10) than the intrinsic gain,
flipping the net total from roughly flat at +1 to negative from +3 onward. This is consistent
with (not proof of) an extrinsic-value loss -- decay-like in shape, growing with horizon -- that
outpaces the modest favourable intrinsic drift. No theta/IV attribution is made; "decay-like" is
descriptive of the shape (extrinsic loss growing with horizon), not a modeled quantity.

### 6. Pattern B -> PE decomposition (opposite side, for completeness)

n=557/490/466/446/393. Option Rs mean -0.07/0.17/0.51/0.34 across horizons; intrinsic mean
negative (-0.90 to -0.66), extrinsic mean positive (0.83 to 1.05). Included for completeness only.

### 7. Price-level diagnostics

| Population | n | Median | P25 | P75 | Min | Max | % < Rs20 |
|---|---|---|---|---|---|---|---|
| Pattern A PE | 578 | 67.31 | 54.05 | 89.75 | 27.88 | 175.45 | 0.0% |
| Pattern B CE | 557 | 32.71 | 18.38 | 40.81 | 0.07 | 62.62 | 27.6% |

The previously-stated approximate medians (~₹67.31 PE, ~₹32.71 CE) are confirmed exactly against
this fresh run's own output. Pattern A's PE population is never below ₹20; Pattern B's CE
population is dominated by comparatively cheap options, with over a quarter of signalling events
priced under ₹20 (near the "close to worthless" range for an already-cheap 0-DTE Call).

### 8. Moneyness diagnostics

| Population | n | Median | P25 | P75 | Min | Max |
|---|---|---|---|---|---|---|
| Pattern A PE | 578 | -2.00 | -14.00 | 11.00 | -25.00 | 24.60 |
| Pattern B CE | 557 | -2.00 | -13.00 | 8.00 | -25.00 | 24.50 |

The two populations sit in **essentially the same signed-moneyness region** (median -2, similar
P25/P75 spread, both roughly straddling at-the-money). Moneyness itself is not the source of the
price-level asymmetry in Section 7 -- Pattern A's PE trading at roughly double Pattern B's CE
price, at similar moneyness, is consistent with the well-known Nifty put/call volatility-skew
premium (Puts trading richer than Calls at comparable moneyness), not with the two populations
being selected from different strike-distance regions.

### 9. Session diagnostics (total/intrinsic/extrinsic change, +3 horizon representative)

Pattern A -> PE: total change is positive in 6 of 7 session buckets (09:15-15:00), with the
15:00-15:30 bucket (n=31) showing the *only* nonzero median intrinsic move (+0.60) alongside a
positive extrinsic median (+0.20) and the second-highest total median (0.58) of any bucket --
consistent with the prior experiment's finding of a strong late-session 0-DTE effect, now
visible as a genuine intrinsic move (not just an extrinsic/decay artifact) in that bucket.
Pattern B -> CE: total change is negative in 6 of 7 buckets, intrinsic median is exactly 0.00 in
every single bucket (no late-session intrinsic move analogous to Pattern A's), and extrinsic
median is negative in all 7 buckets including 15:00-15:30 (-0.20) -- there is no bucket where
Pattern B's CE shows the kind of genuine intrinsic follow-through Pattern A's PE shows late in the
session.

### 10. Matched-control comparison

Pattern A -> PE vs Control A -> PE (same tercile-matched, Up-direction-held-constant control,
unchanged from prior experiments): Pattern's **total** median beats Control's at every horizon
(+1: 0.11 vs -0.22; +3: 0.45 vs -0.21; +5: 0.38 vs 0.02; +10: 0.64 vs 0.05), and Pattern's
extrinsic median decays less severely than Control's at +1/+3/+5 (-0.25/-0.36/-0.45 vs
-0.51/-0.55/-0.66), converging at +10 (-0.75 vs -0.71). Intrinsic medians are 0.00 for both
Pattern and Control at every horizon (as expected, since both stay predominantly near-the-money).
This is a relationship-specific effect, not just "any option under similar underlying
conditions" -- the control shares the same direction and matched prior-movement magnitude but
does not share Pattern A's advantage.

Pattern B -> CE vs Control B -> CE: here the comparison **sharpens** the prior experiment's
finding rather than merely confirming it. Pattern's extrinsic median is *more negative* than
Control's at every horizon (+1: -0.11 vs -0.14; +3: -0.40 vs -0.16; +5: -0.52 vs -0.19; +10:
-0.72 vs -0.30) -- Pattern B's CE decays noticeably faster than a matched, similarly-directional,
similarly-prior-moved Control's CE. Total median is only better than Control at +1 (0.01 vs -0.12)
and is worse than or similar to Control from +3 onward (-0.12 vs -0.11 at +3; -0.16 vs -0.13 at
+5; -0.29 vs -0.20 at +10). This is a materially different picture from Pattern A: Pattern B's
signalling CE is not merely "no better than a random comparable CE" -- on the extrinsic
component specifically, and increasingly on the total, it is **worse** than its own matched
control.

### 11. Interpretation of the A/B asymmetry

- **Pattern A PE's positive response is a case of a positive intrinsic-value component
  (mean) outweighing a persistently negative extrinsic-value component (mean)** -- both
  components are active, but the net direction is set by intrinsic gains from the subset of
  events where the underlying followed through enough to move the option meaningfully
  in-the-money, not by an absence of extrinsic decay (extrinsic decay is present at every
  horizon, on both mean and median).
- **Pattern B CE's negative response is consistent with an extrinsic-value component (decay-like
  in shape, growing with horizon) that outpaces a real but comparatively modest favourable
  intrinsic drift** -- the intrinsic-change mean for Pattern B CE (1.14-1.27) is actually similar
  in magnitude to Pattern A PE's (1.17-1.50); what differs is the extrinsic side, which is larger
  and grows faster for Pattern B CE.
- **The A/B asymmetry is not explained by moneyness** (Section 8: nearly identical signed
  moneyness distributions) but **is associated with a large signalling-price-level difference**
  (Section 7: ~₹67 PE vs ~₹33 CE, with over a quarter of Pattern B's CE population under ₹20).
  A cheaper option has proportionally less extrinsic cushion to begin with, so the same rupee
  amount of extrinsic decay represents a larger fraction of the option's value and pushes it
  toward near-worthless faster -- this is consistent with ordinary 0-DTE option mechanics
  (cheaper options, deeper into their decay curve, have less room to absorb extrinsic loss), not
  with a claim about theta, IV, gamma, liquidity, or demand specifically (none of which were
  measured here).
- **The matched-control comparison (Section 10) indicates the asymmetry is at least partly
  relationship-specific, not purely "normal option behaviour under similar underlying
  conditions"**: Pattern A beats its control on total price at every horizon; Pattern B's CE
  *underperforms* its control on extrinsic and (from +3 onward) total price. If Pattern B's CE
  loss were purely a generic price-level/decay artifact common to all cheap 0-DTE Calls under
  similar conditions, the matched control (same direction, same tercile-matched prior movement)
  would be expected to show a similar decay profile -- it does not; it decays less than Pattern
  B's own signalling CE.

### 12. What remains unproven

Why Pattern B's CE decays *faster* than its own matched control specifically (this experiment
identifies the extrinsic component as the locus of the asymmetry and shows it's relationship-
specific, but does not explain the mechanism -- no IV, Greeks, theta, or liquidity data was
examined, per the task's explicit exclusion). Whether the same intrinsic/extrinsic split would
look different at other DTEs (not tested, DTE=0 only, per the task's own instruction). Everything
here is still a 3-day sample -- both patterns' n (578, 557) and every one of the numbers above are
one data point in an accumulating series, not a verdict.

### 13. Recommended next research question

Given that this experiment successfully isolated the A/B asymmetry to the extrinsic-value
component and showed it is relationship-specific (not merely a control-level decay artifact), the
one remaining piece of that finding this experiment was explicitly scoped *not* to explain is
**why** Pattern B's CE decays faster in extrinsic terms than a matched control's CE. Answering
that mechanistically is exactly what Greeks/IV/theoretical pricing (option (B) in the task's
menu) would address -- an actual theta/IV comparison between Pattern B's signalling CE and its
matched control's CE could determine whether the faster decay reflects a genuinely different
implied-volatility or time-decay profile, or is still within normal sampling variation for a
557-event, 3-day sample. This is offered as a recommendation, not a decision -- continuing the
underlying-relationship research track (option C) remains a reasonable alternative if the
priority is breadth (more relationship states, more days) over depth on this specific mechanism.

### 14. Build / test

New: `OptionValueDecompositionTests.cs` (12 tests: intrinsic ITM/OTM/ATM for both Call and Put,
extrinsic never-clipped, moneyness signed/unclipped, exact reconciliation identity).
`dotnet build NiftySignal.slnx`: 0 warnings/errors. `dotnet test NiftySignal.slnx`: **892/892
passing** (880 baseline + 12 new).

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-decomposition 2026-09-01 2026-09-23 --out=vc0dte-decomposition.csv
```

## DTE Expansion of the Existing Relationship (2026-09-24)

**Question**: does Pattern A/B's underlying-direction relationship, and its option monetization,
survive outside 0-DTE, or is the earlier PE/CE asymmetry specifically a 0-DTE phenomenon? Validation
experiment only -- no trading strategy, optimizer, composite score, or threshold tuning.

### 1. Implementation summary

Added exactly two new pieces, both pure additions with no change to any frozen methodology:
`SynchronizedOptionBarBuilder.BuildDayForChainAsync` (a sibling of the frozen, unmodified
`BuildDayAsync` that accepts an explicit chain instead of querying the fixed `ExpiryDate ==
asOfDate` 0-DTE filter -- verified byte-identical behaviour for the 0-DTE case via the existing,
unmodified `SynchronizedOptionBarBuilderTests`, which still pass), and `DteBucketClassifier`
(a pure function mapping a raw DTE integer to one of 5 buckets reflecting the dataset's own
calendar structure, not fit to any result). The new `vc0dte-relationship-dte-expansion` CLI
command reuses `FutureEventBarBuilder.BuildDayAsync`, `UnderlyingOptionRelationshipRecorder.RecordAsync`,
`OptionValueDecomposition`, `EpisodeAnalysis.DetectEpisodes`, `ForwardValidationAnalysis.ComputeForwardMetrics`/
`ComputeTerciles`, and `ConditionalMovementAnalysis`'s tercile-matched control construction
completely unmodified -- the only new logic is discovering which (date, expiry) pairs exist and
feeding each one through the identical pipeline the 0-DTE commands already use.

### 2. Data-availability scan (Phase 1 -- exact dates used, no fabrication)

A cheap existence scan (`vc-dte-availability-scan`, also added this task) over 2026-08-15 to
2026-10-05 found real futures+option tick data on **12 unique calendar sessions**: 2026-09-04,
08, 09, 10, 11, 15, 16, 17, 18, 21, 22, 23 (all other days in that window have no futures
instrument at all). NIFTY's weekly expiry in this dataset is Tuesday-only, and **each of these 12
days has TWO listed option chains**: the "front" (nearest upcoming Tuesday) and "back" (following
Tuesday) expiry, exactly 7 days apart. This yields 5 real DTE buckets (`DteBucketClassifier`,
boundaries fixed by this calendar structure, not tuned to any result):

| Bucket | Raw DTE | Sessions | Dates |
|---|---|---|---|
| 0 | 0 | 3 | 09-08, 09-15, 09-22 |
| 1 | 1 | **1** | 09-21 |
| 4-6 | 4,5,6 | 8 | 09-04, 09-09, 09-10, 09-11, 09-16, 09-17, 09-18, 09-23 |
| 7-8 | 7,8 | 4 | 09-08, 09-15, 09-21, 09-22 |
| 11-13 | 11,12,13 | 8 | 09-04, 09-09, 09-10, 09-11, 09-16, 09-17, 09-18, 09-23 |

DTE 2 and 3 never occur (no Saturday/Sunday trading, so the gap from a Tuesday expiry never lands
there) -- not forced into a bucket, per the task's own instruction. **Bucket 1 is flagged
insufficient (1 session) and its numbers below are reported for completeness only, never as
generalizable evidence.**

**Critical structural caveat, stated explicitly per the task's statistical-discipline
requirement**: bucket "4-6" and bucket "11-13" are built from the exact SAME 8 calendar
sessions (front-week chain vs back-week chain on the identical days); bucket "0" and bucket "7-8"
share 3 of "7-8"'s 4 sessions likewise. There are only **12 independent underlying calendar
sessions in this entire experiment, not 24** -- every session contributes to exactly two buckets
(its front-chain view and its back-chain view, DTE differing by exactly 7). Any comparison ACROSS
buckets that doesn't respect this pairing (e.g. treating "0 vs 4-6" as two independent samples)
implicitly compares different day-sets, not just different DTEs, and that confound is
called out explicitly in Section 11 below.

### 3. Underlying-relationship validation (per bucket, futures forward return)

Pattern A (defined on a contemporaneous Up move) and Pattern B (defined on a contemporaneous Down
move) both show **the same subsequent-futures-direction pattern in every single bucket**,
essentially unchanged in sign, magnitude, and pos/neg split:

| Bucket | Pattern | n | +1 med | +3 med | +5 med | +10 med | pos%/neg% (+10) |
|---|---|---|---|---|---|---|---|
| 0 | A | 570 | -0.0038% | -0.0104% | -0.0101% | -0.0146% | 30.2/67.2 |
| 0 | B | 558 | 0.0017% | 0.0043% | 0.0051% | 0.0051% | 55.3/42.4 |
| 4-6 | A | 1182 | -0.0026% | -0.0085% | -0.0102% | -0.0093% | 38.6/59.2 |
| 4-6 | B | 1132 | 0.0029% | 0.0069% | 0.0094% | 0.0104% | 59.9/38.1 |
| 7-8 | A | 617 | -0.0038% | -0.0104% | -0.0107% | -0.0152% | 31.3/67.4 |
| 7-8 | B | 628 | 0.0034% | 0.0059% | 0.0068% | 0.0066% | 58.1/39.9 |
| 11-13 | A | 1018 | -0.0026% | -0.0080% | -0.0085% | -0.0081% | 39.0/58.4 |
| 11-13 | B | 991 | 0.0026% | 0.0077% | 0.0094% | 0.0094% | 59.1/38.9 |

(Bucket 1, n=115/130, flagged insufficient, shows the same sign pattern.) Episode counts (per
bucket, summed across that bucket's sessions, `EpisodeAnalysis.DetectEpisodes` reused unmodified)
track event counts closely (e.g. bucket 0: PatternA 570 events / 540 episodes; bucket 4-6:
PatternA 1182 events / 1086 episodes) -- consistent with the prior Episode-Level finding that
these episodes are overwhelmingly 1-2 events long, so event-level and episode-level counts are not
meaningfully different here either.

### 4. Option response decomposition (Pattern A -> PE, Pattern B -> CE; median Option Rs by horizon)

| Bucket | A->PE +1 | A->PE +3 | A->PE +5 | A->PE +10 | B->CE +1 | B->CE +3 | B->CE +5 | B->CE +10 |
|---|---|---|---|---|---|---|---|---|
| 0 | 0.0948 | 0.4295 | 0.3163 | 0.4556 | 0.0037 | -0.1185 | -0.1566 | -0.2667 |
| 4-6 | 0.0454 | 0.0845 | 0.0308 | 0.0478 | 0.0017 | 0.0039 | -0.0017 | -0.1190 |
| 7-8 | 0.0905 | 0.2298 | 0.1655 | 0.3344 | 0.0847 | 0.0961 | 0.0797 | 0.0343 |
| 11-13 | 0.0000 | 0.0132 | -0.0974 | 0.0028 | 0.0009 | -0.0488 | -0.0762 | -0.1819 |

Unlike Section 3's underlying result, the option-response magnitude is **not** stable across
buckets. Two observations, read together with Section 2's structural caveat:

- **0-DTE vs 7-8-DTE (same underlying days, viewed via front vs back-week chain)**: Pattern B's
  CE goes from consistently negative (0-DTE: -0.12 to -0.27 from +3 onward) to consistently
  **positive** (7-8-DTE: +0.08 to +0.10 through +5, still positive +0.03 at +10) on the SAME
  calendar days, just priced off the following week's contract instead of the expiring one.
  Pattern A's PE is strong-positive in both (0.43/0.32/0.46 vs 0.23/0.17/0.33) -- weaker in
  7-8-DTE but still clearly positive at every horizon.
- **4-6-DTE vs 11-13-DTE (a different set of 8 underlying days, same front/back pairing)**: this
  comparison does **not** show the same improvement. Both are weak: Pattern A's PE is
  near-zero-to-weakly-positive in both (0.05/0.08/0.03/0.05 vs 0.00/0.01/-0.10/0.00); Pattern B's
  CE is near-zero in 4-6-DTE (0.002/0.004/-0.002/-0.12) and mildly negative in 11-13-DTE
  (0.001/-0.05/-0.08/-0.18) -- if anything slightly worse further from expiry here, the opposite
  direction from the 0-vs-7-8 comparison.

**This is reported as an unresolved inconsistency, not resolved into a single conclusion**: the
same "front vs back-week, same days" comparison structure gives an opposite answer on the two
available day-sets. With only 2 such paired comparisons in the whole dataset, this cannot be
distinguished from ordinary day-to-day variation in how strongly the pattern's edge showed up on
those particular days (see Section 2's caveat -- the 12 sessions are not evenly informative, and
several cluster around the same market conditions).

### 5. Preceding vs subsequent underlying movement (descriptive reversal-hypothesis check, Section 6 of the task)

In **every bucket, at every prior/forward horizon (3/5/10 events)**, both patterns show the same
descriptive signature: the median forward move **opposes** the pattern's own defining
contemporaneous direction (Pattern A: defined on an Up move, subsequent median is negative at
every horizon in every bucket; Pattern B: defined on a Down move, subsequent median is positive at
every horizon in every bucket), and the per-event "opposing-sign rate" (prior-N move and
forward-N move have opposite signs) sits modestly above chance in every bucket -- roughly 50-58%
across all buckets/horizons/patterns, e.g. bucket 0 Pattern A +10: 51.9%; bucket 11-13 Pattern B
+10: 47.6% (only one of the 40 bucket/pattern/horizon cells dips slightly below 50%). This is
**descriptive only** -- no significance test was implemented or is claimed here, per the task's
explicit instruction -- but the consistency of direction (not magnitude) across every single DTE
bucket is itself the notable finding: it does not attenuate or reverse as DTE increases, which is
what a spurious 0-DTE-specific artifact would be expected to do.

### 6. Matched-control comparison (computed within each bucket, same tercile-matched/direction-held-constant construction)

Pattern beats its own matched control on futures-return median in every single bucket at every
horizon (e.g. bucket 0, Pattern A +10: -0.0146% vs Control -0.0055%; bucket 11-13, Pattern B +10:
0.0094% vs Control 0.0017%) -- the underlying relationship's advantage over a comparable,
similarly-directioned, similarly-prior-moved control is present at every DTE tested, not just
0-DTE.

### 7. Statistical discipline (task item 8)

12 unique calendar sessions total (not 24 -- see Section 2). Episode counts closely track event
counts in every bucket (ratios 0.90-0.95), consistent with the prior Episode-Level finding that
most episodes are 1-2 events long. Bucket 1 (n=1 session) is explicitly flagged insufficient for
generalization and its numbers are descriptive only. No significance test of any kind was
implemented or is claimed anywhere in this experiment.

### 8. Answers to the required questions

- **Q1 (Pattern A survives outside 0-DTE?)**: **Yes.** The underlying-direction relationship
  (median subsequent futures return negative, ~57-68% negative) is present with essentially
  unchanged sign and magnitude in every bucket tested (0, 1, 4-6, 7-8, 11-13 DTE).
- **Q2 (Pattern B survives outside 0-DTE?)**: **Yes**, symmetrically -- median subsequent futures
  return positive, ~53-70% positive, in every bucket.
- **Q3 (Does Pattern B's CE response improve as DTE increases?)**: **Not established.** It
  improves markedly in one paired same-day comparison (0-DTE -> 7-8-DTE) but shows no comparable
  improvement, if anything a mild worsening, in the other available paired comparison (4-6-DTE ->
  11-13-DTE). No monotonic or consistent DTE relationship is supported by this data.
- **Q4 (Is the 0-DTE CE weakness consistent with a time-to-expiry effect?)**: **Partially, and
  not conclusively.** The 0-vs-7-8-DTE result on its own is consistent with that hypothesis, but
  since the 4-6-vs-11-13-DTE result does not replicate it, a genuine, general time-to-expiry
  effect cannot be distinguished from day-specific variation with only 2 paired comparisons
  available. This should not be read as ruling the hypothesis out either -- there simply isn't
  enough independent data yet to decide either way.
- **Q5 (Is Pattern B consistent with an underlying reversal/weakening indicator, independent of
  CE profitability?)**: **Yes, this is the most consistent finding in the experiment.** Every
  bucket, every horizon, both patterns show the same reversal-like signature (subsequent movement
  opposing the pattern's own defining direction) with a modest (51-58%) opposing-sign tilt --
  unchanged across DTE, unlike the option-response result. This supports treating Pattern B (and
  symmetrically Pattern A) as a candidate underlying-behaviour indicator independent of whether it
  monetizes well through the CE/PE.
- **Q6 (Does Pattern A remain economically useful for PE outside 0-DTE?)**: **Mixed.** The
  directional tilt (positive median) is present in every generalizable bucket, but the magnitude
  is inconsistent -- strong in 0-DTE and 7-8-DTE, weak in 4-6-DTE and 11-13-DTE. Not an
  unambiguous yes; the same day-set confound from Section 2 applies here too.
- **Q7 (Which description is currently supported?)**: **The underlying directional relationship
  is DTE-independent** (Q1/Q2/Q5 -- strong, consistent evidence across every bucket). **Option
  monetization shows apparent DTE-dependence in one comparison that does not replicate in the
  other** -- most plausibly because monetization variability here is dominated by day-to-day/
  session-specific factors that are confounded with the current bucket definition (only 12 unique
  underlying sessions, each viewed twice). This is reported as **insufficient data to settle Q3/Q4
  specifically**, not forced into either "DTE-dependent" or "DTE-independent" for the option
  response.

### 9. What remains unproven

Whether the 0-vs-7-8-DTE CE improvement is a real time-to-expiry effect or a property of those
specific 3-4 calendar days; whether a larger, non-overlapping sample of DTE buckets (impossible
with the current data mirror, which only has these 12 sessions) would resolve Section 4's
inconsistency; whether the reversal-like signature in Section 5 (present at every DTE) would
survive an actual significance test (none was run, none is claimed). Everything here remains 3-12
sessions per bucket -- one data point in an accumulating series, not a verdict, same discipline as
every prior section in this document.

### 10. Build / test

New: `DteBucketClassifierTests.cs` (16 tests: known-calendar-DTE-to-bucket mapping, unrecognized
DTEs correctly excluded rather than forced into a bucket).
`SynchronizedOptionBarBuilderTests.cs` unchanged and still passing, confirming
`BuildDayForChainAsync`'s refactor left `BuildDayAsync`'s own 0-DTE behaviour byte-identical.
`dotnet build NiftySignal.slnx`: 0 warnings/errors. `dotnet test NiftySignal.slnx`: **908/908
passing** (892 baseline + 16 new).

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc-dte-availability-scan 2026-08-15 2026-10-05
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-dte-expansion 2026-09-01 2026-09-23 --out=vc0dte-dte-expansion.csv
```

## Pre-Pattern-Post Transition Analysis (2026-09-24)

**Question**: what does Pattern A/B represent in time -- an early reversal warning, a late-stage
confirmation, a temporary countertrend event, or something else? Validation research only -- no
trading strategy, composite score, optimizer, or trading simulator; no IV/Greeks.

### 1. Implementation summary and pre-implementation check

Checked before coding, per the task's own instruction: the existing `EpisodeAnalysis.DetectEpisodes`
already gives exactly the "first event of episode" signal point this experiment needs (`Episode.StartEventId`),
and the existing tercile-matched, direction-held-constant control methodology (from the Conditional
Analysis experiment) is technically capable of answering "does the pattern add information beyond
a big prior move" as-is -- no blocking ambiguity, no new methodology needed. One resolution
decision is stated explicitly rather than silently made: **Pattern occurrences are deduplicated to
one row per episode (its first event); the control population is left at its existing, unmodified
per-event granularity** (reusing it exactly as before, not deduplicated) -- this is a resolution
difference, not a capability gap, and Section 2's own finding (92.9% of episodes are exactly 1
event) shows it has little practical effect.

New code: `EpisodeTransitionAnalysis.cs` (two pure sign-orientation helpers --
`OrientToDefiningDirection`/`OrientToOpposingDirection`, which re-express a raw signed change so
"positive" consistently means "in the pattern's own defining direction" / "in the reversal
direction" regardless of which pattern produced it -- plus a CE/PE divergence classifier built
entirely from the existing `RelationshipDirectionExtensions.Classify`) and the new
`vc0dte-relationship-transition` CLI command, which otherwise reuses
`UnderlyingOptionRelationshipRecorder.RecordAsync`, `EpisodeAnalysis.DetectEpisodes`,
`OptionValueDecomposition`, `Vc0DteBehaviorSummary.SessionBucket`, and the Conditional Analysis
control construction completely unmodified.

### 2. Episode counts and length distribution

Primary (frozen 0-DTE, 3-session) dataset: **548 Pattern A episodes, 508 Pattern B episodes**.
Episode length: median 1.0 event, min 1, max 4, **92.9% exactly 1 event** -- consistent with the
prior Episode-Level Validation finding; episode-level and event-level granularity are, empirically,
almost the same thing here.

### 3. PRE-PATTERN

| Pattern | Metric | -1 | -3 | -5 | -10 |
|---|---|---|---|---|---|
| A | Futures med% | 0.0129 | 0.0034 | 0.0043 | 0.0021 |
| A | CE med% | -1.19 | -1.43 | -1.46 | -1.44 |
| A | PE med% | 0.99 | 1.10 | 0.99 | 0.84 |
| B | Futures med% | -0.0117 | -0.0042 | -0.0063 | -0.0103 |
| B | CE med% | 1.14 | 0.93 | 0.96 | 0.47 |
| B | PE med% | -0.89 | -0.75 | -0.63 | -0.24 |

Pattern A's pre-pattern **futures** movement is tiny (0.002-0.013%) -- the underlying was *not*
strongly moving up before Pattern A appears. Pattern B's pre-pattern futures movement is larger
in comparison (-0.004% to -0.012%) -- some real preceding downward movement already existed.
Both patterns' pre-pattern CE/PE changes are large and already point in the pattern's own
defining divergence direction (Pattern A: CE already falling, PE already rising, before the
labelled event; Pattern B: the mirror) -- consistent with the divergence building up over
several events rather than appearing instantaneously at the labelled bar. Pre-pattern (-3)
CE-vs-PE divergence is "Divergent" in the large majority of episodes for both patterns (A:
396/548 = 72.3%; B: 381/508 = 75.0%), "Aligned" in a small minority (A: 54, B: 48), the rest
Flat/Unavailable. Signal-event median futures event-duration is 10,000ms and median event
volume is ~1,500-1,560 contracts for both patterns (near the 1300 threshold, as expected for a
threshold-triggered bar).

### 4. PATTERN EVENT / 5. POST-PATTERN + critical comparison

| Pattern | Horizon | PreSameDir med% | PostOpposing med% (pos%) | Fwd CE med% | Fwd PE med% |
|---|---|---|---|---|---|
| A | +1 | 0.0129 | 0.0040 (60.0%) | -0.26 | 0.15 |
| A | +3 | 0.0034 | 0.0104 (68.4%) | -0.78 | 0.60 |
| A | +5 | 0.0043 | 0.0101 (66.0%) | -0.71 | 0.43 |
| A | +10 | 0.0021 | 0.0146 (67.0%) | -1.58 | 0.92 |
| B | +1 | 0.0117 | 0.0017 (53.5%) | 0.06 | 0.06 |
| B | +3 | 0.0042 | 0.0042 (57.9%) | -0.40 | 0.33 |
| B | +5 | 0.0063 | 0.0051 (57.8%) | -0.69 | 0.56 |
| B | +10 | 0.0103 | 0.0046 (55.0%) | -1.26 | 0.40 |

(PreSameDir/PostOpposing are sign-oriented per Section 1: positive PreSameDir = already moving in
the pattern's own defining direction; positive PostOpposing = genuinely reversed.)

- **Pattern A**: pre-pattern avg |move| (n=3,5,10) = 0.0033%; post-pattern avg |opposing move| =
  0.0117%. **Ratio (post/pre) = 3.58** -- the reversal is more than 3x the size of whatever
  preceded it.
- **Pattern B**: pre-pattern avg |move| = 0.0069%; post-pattern avg |opposing move| = 0.0046%.
  **Ratio (post/pre) = 0.67** -- the preceding move is actually larger than the reversal that
  follows.

**Classification (data-driven, no invented numeric threshold -- see Section 6 below for the exact
rule)**: **Pattern A: Early-warning-leaning.** **Pattern B: Late-stage-confirmation-leaning.**

Signalling price/moneyness/session confirm the previously-established baseline exactly (Pattern A
PE median ₹68.50, Pattern B CE median ₹32.76 -- consistent with the Option Response Decomposition
section's ~₹67-68/~₹33 figures; moneyness median -2.00 for both, matching the earlier finding that
moneyness itself does not distinguish the two patterns). Session distribution is broad across all
7 buckets for both patterns, no single-bucket concentration.

### 6. Path (continuation vs bounce-back, using only the frozen 1/3/5/10 horizons -- no finer
granularity was measured, a stated limitation, not a fabricated one)

- **Pattern A**: PostOpposing +1=0.0040%, +3=0.0104%, +5=0.0101%, +10=0.0146% -- rises to +10
  with only a minor dip at +5, never falls back toward zero. **Consistent with a sustained,
  continuing reversal within the tested window**, not a bounce that fades.
- **Pattern B**: PostOpposing +1=0.0017%, +3=0.0042%, +5=0.0051%, +10=0.0046% -- rises through +5
  then eases slightly by +10, but **stays positive throughout** -- it does not cross back to
  renewed downward movement within +10. This is neither a clean sustained reversal (like Pattern
  A) nor a full round-trip bounce-back to the original direction -- best described as a **modest,
  partially-sustained reversal that plateaus** rather than either accelerating or fully reverting.

### 7. Matched-control comparison (existing methodology, unmodified; per-event control vs
per-episode pattern)

Pattern beats its matched control at **every** prior-movement tercile and every horizon, for both
patterns:

- Pattern A vs Control A (Prior-10, High tercile): Pattern -0.0189% vs Control -0.0050%.
- Pattern B vs Control B (Prior-10, High tercile): Pattern +0.0072% vs Control -0.0043%.

Every one of the 9 tercile/horizon cells for each pattern shows the same separation (Pattern more
negative than Control for A, more positive than Control for B) -- the pattern's forward return is
not simply explained by "Nifty had already moved a lot in one direction."

### 8. DTE-bucket breakdown (reusing the DTE-expansion experiment's exact pair discovery/bucket
classification; the same 12-unique-session caveat from that experiment applies unchanged here)

| Bucket | Sessions | A PreSameDir(-3) | A PostOpposing(+3) | A PostOpposing(+10) | B PreSameDir(-3) | B PostOpposing(+3) | B PostOpposing(+10) |
|---|---|---|---|---|---|---|---|
| 0 | 3 | 0.0038 | 0.0103 | 0.0146 | 0.0042 | 0.0042 | 0.0047 |
| 1 | 1 (flagged) | 0.0030 | 0.0098 | 0.0087 | 0.0036 | 0.0098 | 0.0113 |
| 4-6 | 8 | 0.0030 | 0.0086 | 0.0093 | 0.0030 | 0.0068 | 0.0094 |
| 7-8 | 4 | 0.0021 | 0.0104 | 0.0152 | 0.0030 | 0.0055 | 0.0064 |
| 11-13 | 8 | 0.0017 | 0.0081 | 0.0078 | 0.0017 | 0.0078 | 0.0090 |

The core reversal-consistent signature (PostOpposing positive, of the same order of magnitude, in
every bucket for both patterns) is stable across every DTE bucket tested, now confirmed at proper
episode (deduplicated) granularity -- consistent with, and reinforcing, the DTE-Expansion
experiment's own finding that the underlying-direction relationship is DTE-independent.

### 9. Answers to the required questions

- **Q1 (Was the underlying already moving strongly in the defining direction before the pattern
  appeared?)**: **No for Pattern A** (pre-pattern futures move is tiny, ~0.002-0.013%). **Partially
  for Pattern B** (a real, moderate preceding move exists, ~0.004-0.012%, comparable to or larger
  than what follows).
- **Q2 (Is the subsequent opposing move larger than the preceding move?)**: **Yes for Pattern A**
  (ratio 3.58). **No for Pattern B** (ratio 0.67).
- **Q3 (Early enough to be actionable, or mostly late-stage confirmation?)**: **Pattern A leans
  early/actionable** -- most of the reversal is still ahead when the pattern fires. **Pattern B
  leans late-stage** -- a meaningful part of the move has already happened by the time it fires.
- **Q4 (Sustained reversal or temporary countertrend?)**: **Pattern A: sustained/continuing**
  through +10, no bounce-back observed. **Pattern B: partially sustained but plateauing** -- rises
  then eases, never fully reverts to the original direction within the tested window, but also
  doesn't continue building the way Pattern A's does.
- **Q5 (Does Pattern A contain information beyond an upward underlying move?)**: **Yes** -- beats
  its matched control at every tercile/horizon.
- **Q6 (Does Pattern B contain information beyond a downward underlying move?)**: **Yes**,
  symmetrically -- beats its matched control at every tercile/horizon.
- **Q7 (Does the evidence support investigating Pattern B as a bearish-position exit/weakening
  condition?)**: **Tentatively yes, but with modest expectations.** It shows a real, control-beating
  reversal tilt, but it is late-stage, smaller than the move that preceded it, and plateaus rather
  than accelerating -- more consistent with "the down-move may be running out of steam" than with
  a strong turning point.
- **Q8 (Does the evidence support investigating Pattern A as a bullish-position exit/weakening
  condition?)**: **Yes, and more strongly than Pattern B** -- early, sustained, control-beating at
  every cell tested. The stronger candidate of the two for this line of research.
- **Q9 (Are results stable across sessions/DTE buckets?)**: **Yes for the qualitative
  reversal-consistent finding** -- present, same sign, same order of magnitude, in every DTE
  bucket (bucket 1 flagged, n=1 session). The Pattern-A-vs-Pattern-B asymmetry (early/sustained vs
  late/plateauing) itself was only characterized in detail on the primary 3-session dataset in
  this experiment -- Section 8's bucket breakdown reports only the summary PreSameDir/PostOpposing
  medians, not the full early-vs-late classification, per bucket.
- **Q10 (Single most important unanswered question)**: Pattern A's early, sustained-reversal
  signature is the strongest finding in this experiment -- the most important next question is
  whether it holds up under genuinely independent, larger, out-of-sample data (only 3 non-overlapping
  core sessions and 548 episodes support it so far), before any exit-condition design is attempted
  on it. This is squarely a "backtesting is a long-term, multi-session process" question, not a
  green light to build anything yet.

### 10. Build / test

New: `EpisodeTransitionAnalysisTests.cs` (18 tests: sign-orientation for both patterns and both
directions, null propagation, CE/PE divergence classification reusing the existing direction
enum). `dotnet build NiftySignal.slnx`: 0 warnings/errors. `dotnet test NiftySignal.slnx`:
**926/926 passing** (908 baseline + 18 new).

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-transition 2026-09-01 2026-09-23 --out=vc0dte-transition.csv
```

## Methodology Freeze + Out-of-Sample Data Check (2026-09-24)

Following the completed Transition Analysis, the research definitions below are now **FROZEN**
pending genuinely new (never-examined) session data. No code changes were needed for this check
-- the existing `vc-dte-availability-scan` command (added during the DTE-Expansion experiment)
was sufficient, run once, read-only.

### Frozen definitions (do not change without a genuine implementation bug, not validation results)

- **Pattern A** = `RelationshipObservation.RelationshipCategory == "UnderlyingUp_CEDown_PEUp"`
  (`ForwardValidationAnalysis.State2_BullishDivergence_PatternA`).
- **Pattern B** = `RelationshipObservation.RelationshipCategory == "UnderlyingDown_CEUp_PEDown"`
  (`ForwardValidationAnalysis.State4_BearishDivergence_PatternB`).
- Event-bar construction: 1300-contract futures-volume threshold with excess-volume carry
  (`FutureEventBarBuilder`). Dynamic-ATM signalling contract (`EventBarStrikeSelector.PickDynamicAtm`,
  keyed off futures close). Episode = maximal run of consecutive events sharing the same
  `RelationshipCategory` (`EpisodeAnalysis.DetectEpisodes`); signal point = episode's
  `StartEventId` (first event). Forward/prior horizons: exactly 1/3/5/10 events
  (`UnderlyingOptionRelationshipSummary.Compute*Change`). Control: direction-held-constant,
  tercile-matched on `|prior-N-event movement|` computed within that direction population
  (`ConditionalMovementAnalysis.ClassifyTercileBucket` + `ForwardValidationAnalysis.ComputeTerciles`).
  Option response: `OptionValueDecomposition` intrinsic/extrinsic split on the signalling
  contract's own `AverageLtp`, frozen strike, futures (never spot) as the underlying for
  intrinsic value. Stale/missing/incomplete: `OptionResponseAnalysis.ClassifyExclusionReason` /
  the `"OptionDataIncomplete"`/`"ContractTransition"` category exclusions. DTE: `DteBucketClassifier`'s
  5 buckets (0, 1, 4-6, 7-8, 11-13), fixed by calendar structure, not tuned.

### Data-availability check (Q1-Q4)

Re-ran `vc-dte-availability-scan` over **2025-01-01 to 2027-06-30** (2.5 years, far wider than
any previous scan) to check for any session not already examined. Result: **zero new sessions**.
The only dates with any futures/option tick data anywhere in the local `niftysignal_vm_copy`
mirror remain the same **12 unique calendar sessions** already used throughout this research
chain: 2026-09-04, 08, 09, 10, 11, 15, 16, 17, 18, 21, 22, 23. First date 2026-09-04, last date
2026-09-23. Of these, exactly **3 are 0-DTE-eligible** (09-08, 09-15, 09-22) -- the same 3 used to
discover and characterize Pattern A/B from the start. The other 9 were already examined (front-
and back-week chains) in the DTE-Expansion and Transition experiments' own bucket breakdowns, so
they are not unseen either. DTE coverage across the 12 sessions: 0, 1, 4, 5, 6, 7, 8, 11, 12, 13
(2 and 3 never occur -- Tuesday-only expiry, no weekend trading).

**Conclusion: there is no genuinely new, never-examined data locally for an honest out-of-sample
validation.** Manufacturing an OOS split from the same 3 (or 12) sessions already used to
discover and characterize the patterns would not be a real validation -- it would just be
re-reporting in-sample results under a different label. This is reported as a hard limitation,
not worked around.

### Answers to the required questions

- **Q1 (How many genuinely new independent calendar sessions are available?)**: **Zero.**
- **Q2 (What dates are available?)**: The same 12 already-examined sessions listed above; no
  others exist in the local mirror across a 2.5-year scan window.
- **Q3 (How many are suitable for true out-of-sample validation?)**: **Zero** -- every available
  session has already been used either to discover/characterize the patterns (the 3 0-DTE days)
  or to examine them at other DTEs (the other 9, already reported in the DTE-Expansion/Transition
  sections).
- **Q4 (What DTE coverage exists in the validation period?)**: N/A -- there is no validation
  period distinct from the discovery period with the current local data.
- **Q5 (Is the existing infrastructure sufficient to run the frozen validation without changing
  methodology?)**: **Yes.** `LoadVc0DteDayAsync`/the DTE-expansion loader,
  `UnderlyingOptionRelationshipRecorder.RecordAsync`, `EpisodeAnalysis.DetectEpisodes`, the
  Conditional Analysis control construction, and `OptionValueDecomposition` already implement
  every metric the validation protocol needs. No new code is required to RUN the protocol --
  only new, genuinely unseen data is missing.
- **Q6**: See "Frozen definitions" above -- the exact frozen Pattern A/B definitions and
  validation metrics to be used the moment new data becomes available.
- **Q7 (What validation experiment should run next?)**: **None can run yet against genuinely new
  data -- this is a data-availability blocker, not a methodology or implementation gap.** The most
  plausible source of real new sessions is the live paper-trading VM (`100.105.67.79`), which has
  likely accumulated trading days since `niftysignal_vm_copy` was last mirrored -- but re-syncing
  or pulling from the live system is exactly the kind of action this project's working agreement
  says to ask about first, not do unprompted, so this is surfaced as a question for the user
  rather than acted on. Absent new data, the honest position is: Pattern A/B's characterization
  (Sections above, discovery data only) stands as reported, but remains **unvalidated
  out-of-sample**, and no further relationship research on these two patterns can responsibly
  claim more than that until new, previously-unseen sessions exist.

### Build / test

No code changes were required for this check (existing `vc-dte-availability-scan` was reused
as-is); no build/test run was needed and none is claimed.

## Cross-Index Validation -- Sensex (2026-09-24)

**Question**: does the FROZEN Nifty-derived Pattern A/B relationship also occur on Sensex, or is
it Nifty-specific? Applies the frozen definitions unchanged -- no new pattern discovery, no
Sensex-specific tuning, no trading strategy.

### 1. Pre-implementation data-availability check (task step 1)

`vc-underlying-inventory` (new, read-only) found: `SENSEX Future` rows=2, `SENSEX Option` rows=80,
both spanning **2026-09-22 to 2026-09-23 only** -- no `SENSEX Index` (spot) rows at all. Extending
`vc-dte-availability-scan` with an `--underlying=` parameter (reusing its exact existing logic,
not duplicating it) confirmed: **exactly 2 independent calendar sessions**, each with **one**
option expiry chain only (2026-09-24, DTE=2 on 09-22 and DTE=1 on 09-23) -- unlike Nifty's
front+back-week pair, there is no second (back-week) chain to compare against on either day.
Strike spacing is 100 points (73800-75900, 22 distinct strikes, 40 rows/day matching Nifty's own
per-day chain size), with dense real tick coverage (~140-155k sample ticks/day) -- not a
data-quality problem, purely a *quantity* one (2 sessions, not 3+).

### 2. Mapping the frozen Nifty methodology to Sensex -- documented differences (task step 2)

Two Nifty-specific implementation details could not be transferred literally, both because the
NIFTY string was hardcoded rather than because of an actual methodology difference:
`FutureEventBarBuilder`'s future-instrument query and `UnderlyingOptionRelationshipRecorder`'s
spot-instrument query. Both were addressed the same way every previous DTE-agnostic extension in
this research chain has been: a new, additive, underlying-parameterized SIBLING method
(`BuildDayForUnderlyingAsync`/`RecordForUnderlyingAsync`), with the original NIFTY-only method
now delegating to it with `"NIFTY"` hardcoded -- verified byte-identical NIFTY behaviour via the
existing, unmodified tests, which still pass unchanged. `SynchronizedOptionBarBuilder.BuildDayForChainAsync`
needed no change at all (it already takes the chain as an external parameter, added during the
DTE-Expansion experiment). One genuine, unavoidable market-structure difference is documented
rather than worked around: **Sensex has no Index (spot) instrument in this dataset** --
`SpotAvailable` is `false` for every Sensex observation, and every `Spot*` field is simply
unavailable throughout, exactly as the existing "never fabricate" convention already handles for
any Nifty day lacking spot data. The 1300-contract threshold, dynamic-ATM selection, episode/
first-event convention, 1/3/5/10 horizons, and the tercile-matched control construction were all
applied completely unchanged.

### 3. What actually happened when the frozen methodology was applied

- 2026-09-22: 11 future event bars, 11 relationship observations (DTE=2).
- 2026-09-23: 15 future event bars, 15 relationship observations (DTE=1).

This is far fewer event bars per day than Nifty typically produces (1200+) at the SAME frozen
1300-contract threshold -- a genuine finding about Sensex futures' own volume characteristics in
this dataset, reported as-is, not compensated for by changing the threshold (which would violate
the freeze). With only 26 total events across both days, **3 Pattern A episodes and 3 Pattern B
episodes** were detected -- the patterns clearly CAN occur on Sensex (the classification logic
transfers mechanically without any change), but this leaves at most 1-3 usable observations at
any given forward horizon once contract-transition/missing-data exclusions and the day's own
remaining-bar limits are applied.

| Pattern | Horizon | n | Underlying med% | PostOpposing med% (pos%) |
|---|---|---|---|---|
| A | +1 | 2 | 0.0602 | -0.0602 (0.0%) |
| A | +3 | 1 | 0.1605 | -0.1605 (0.0%) |
| A | +5 | 1 | 0.3010 | -0.3010 (0.0%) |
| A | +10 | 1 | 0.0939 | -0.0939 (0.0%) |
| B | +1 | 3 | 0.0367 | 0.0367 (66.7%) |
| B | +3 | 2 | 0.0161 | 0.0161 (50.0%) |
| B | +5 | 2 | 0.0140 | 0.0140 (50.0%) |
| B | +10 | 1 | 0.0401 | 0.0401 (100.0%) |

Pattern A's PostOpposing is **negative at every horizon** (0% positive) -- in this sample, the
underlying continued in its ORIGINAL (up) direction rather than reversing down, the opposite
sign from the Nifty finding. Pattern B's PostOpposing stays positive at every horizon (50-100%
positive), the SAME sign as the Nifty finding, with a Pre/Post ratio (0.52) in the same
"late-stage, smaller-than-preceding" range as Nifty's own Pattern B ratio (0.67). **Neither result
should be read as confirming or refuting the Nifty finding** -- every cell above has n=1-3, which
is not a sample size any legitimate statistical claim can be built on, in either direction.

Option response (Pattern A -> PE, Pattern B -> CE): Pattern A -> PE had **zero usable
observations at every single horizon** (the signalling PE price or its forward change was
unavailable for all 3 episodes). Pattern B -> CE had exactly **one** usable observation, at +1
only (Option change -₹20.82, Intrinsic +₹25.00, Extrinsic -₹45.82) -- a single anecdotal data
point, not a distribution. **The option-response side of this experiment cannot be evaluated at
all with the current Sensex data.**

Matched-control comparison: every cell has n=0-2 on the pattern side, n=0-2 on the control side
(one cell, Pattern B +5, has literally zero control observations, making that comparison
undefined). No control comparison in this experiment reaches a sample size where "beats the
control" would mean anything.

### 4. Answers to the required questions

- **Q1 (Is sufficient Sensex data available?)**: **No.** 2 sessions, 26 total events, no spot
  data, no second expiry chain to cross-check against. Not enough for a real relationship test.
- **Q2 (How many independent Sensex sessions?)**: **2** (2026-09-22, 2026-09-23).
- **Q3 (Does frozen Pattern A occur?)**: **Yes, mechanically** -- 3 episodes detected -- but with
  only 1-2 usable observations per horizon, nothing about its behaviour can be concluded.
- **Q4 (Does frozen Pattern B occur?)**: **Yes, mechanically**, symmetrically -- 3 episodes, same
  sample-size limitation.
- **Q5 (Does Pattern A precede downward Sensex movement?)**: **Not supported in this sample** --
  the observed post-pattern movement was in the opposite (continuing-up) direction at every
  horizon, but from n=1-2 observations, this cannot be treated as a real refutation either.
- **Q6 (Does Pattern B precede upward Sensex movement?)**: **Weakly consistent** -- positive at
  every horizon (50-100% positive), same sign as Nifty, but from n=1-3 observations.
- **Q7 (Do the relationships beat the matched controls?)**: **Cannot be meaningfully answered** --
  every control comparison cell is built from 0-2 observations on one or both sides.
- **Q8 (Does the timing structure resemble Nifty?)**: **Partially, superficially, for Pattern B**
  (ratio 0.52 vs Nifty's 0.67, same "late-stage" shape) -- **not for Pattern A** (the ratio
  arithmetic points the same way, but the actual reversal DIRECTION contradicts Nifty). Neither
  observation is trustworthy at this sample size.
- **Q9 (Does option response resemble Nifty, particularly A->PE, B->CE?)**: **Cannot be
  answered** -- A->PE has zero usable observations; B->CE has exactly one.
- **Q10 (Which parts transfer, which do not?)**: The STRUCTURAL/definitional machinery transfers
  without any modification (event bars build, dynamic ATM selects, patterns classify, episodes
  detect, all correctly, on Sensex data). Whether the underlying BEHAVIOURAL finding transfers is
  **not yet answerable** -- there isn't enough data to say it does or doesn't.
- **Q11 (Does the evidence support the broader index-option-relationship hypothesis?)**:
  **Neither confirmed nor refuted -- inconclusive due to sample size**, not a negative result.
- **Q12 (Single most important next research question)**: Whether Sensex accumulates enough
  additional independent calendar sessions (ideally with a comparable multi-day, multi-DTE
  history to what Nifty has) to let this same frozen protocol run with a real sample size --
  everything in this section is a first look, not a verdict, and should not be cited as evidence
  either for or against cross-index transfer until that data exists.

### 5. Build / test

New: `FutureEventBarBuilder.BuildDayForUnderlyingAsync`/`BuildDayWithTraceForUnderlyingAsync` and
`UnderlyingOptionRelationshipRecorder.RecordForUnderlyingAsync` (both pure additive siblings, no
new dedicated tests -- mechanical parameterizations verified via the existing, unmodified
`FutureEventBarBuilderTests`/`UnderlyingOptionRelationshipRecorderTests`, which still pass
unchanged, confirming zero NIFTY behaviour drift). `dotnet build NiftySignal.slnx`: 0
warnings/errors. `dotnet test NiftySignal.slnx`: **926/926 passing** (unchanged from the prior
section -- no new tests were needed for this mechanical parameterization).

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc-underlying-inventory --underlying=SENSEX
dotnet run --project NiftySignal.VolumeBarData -- vc-dte-availability-scan 2026-09-01 2026-09-30 --underlying=SENSEX
dotnet run --project NiftySignal.VolumeBarData -- vc-sensex-relationship-validation 2026-09-22 2026-09-23 --out=vc-sensex-validation.csv
```

## Trade Simulation -- Frozen Relationship (2026-09-24)

**Question**: if we actually traded the frozen Pattern A/B relationship using real option tick
prices, what would the resulting trades look like? First actual option P&L simulation -- no
optimization anywhere (1300 threshold, 10 lots, 15:00/15:15 cutoffs, opposite-pattern exit are all
exactly as specified, none tuned against any result).

### 1. Ambiguity resolved before implementation

The completed research never defined an "opposite-signal exit" for a position triggered by
Pattern A/B (episodes are instantaneous classifications, not a persisting state with its own
natural reversal event). Per the task's own "stop and report" instruction, this was surfaced and
resolved with the user before coding: **a position exits when the OPPOSITE pattern fires while it
is open** (Pattern-A-originated PE exits the instant Pattern B fires; Pattern-B-originated CE
exits the instant Pattern A fires), otherwise it holds to the mandatory 15:15 IST forced close.
Reversal handling mirrors the existing `Vc0DteTradeSimulator` convention: closing on the opposite
signal and opening the new side on the SAME event are recorded as two separate trade rows, never
one combined transaction.

### 2. Transaction-cost convention (documented, not silently chosen)

No existing convention in this codebase covers STT/GST (`PaperTradeSimulator`'s own doc comment:
"nothing is fabricated here" since no rate was ever given). This experiment applies, and states
plainly: **STT** = 0.0625% of premium value, India's current statutory options rate, charged only
on the SELL leg (exit only, never entry). **GST** = 18% of brokerage -- and brokerage is ₹0 per
this experiment's own "Flat Trade" assumption, so GST evaluates to ₹0 throughout. **Other**
(exchange transaction charges, SEBI turnover fees, stamp duty) is **not modeled** -- reported as
₹0 with that limitation stated, not guessed. No slippage constant was added; entry/exit use the
best ask/bid (or LTP fallback when no depth exists) with zero added slippage ticks --
`SignalOptionLtp` vs `EntryPrice`/`ExitPrice` are both recorded so real signal-to-execution tick
movement is visible on its own, not hidden inside a fabricated constant.

### 3. Implementation summary

New: `TransactionCostCalculator` (pure, documented above) and `PatternRelationshipTradeSimulator`
(the state machine) -- both additive, reusing `UnderlyingOptionRelationshipRecorder` (unmodified,
the sole signal source), `PaperTradeSimulator.FillEntry`/`FillExit`, `MaeMfeCalculator.Compute`,
and `OptionTickSeries.EntryAtOrAfter`/`EntryAtOrBefore` completely unchanged. No new signal engine
of any kind.

### 4. DISCOVERY / SEEN-DATA SIMULATION (2026-09-08, 09-15, 09-22 -- the same 3 sessions used to
discover and characterize the patterns; NOT out-of-sample)

**Signal audit** (every Pattern A/B signal, not just executed trades): Executed=606,
AlreadyInPosition=440, After3Pm=89, MissingOptionData=0, ContractTransition=0,
NoExecutableTick=0. Total signals = 1135, exactly Executed+AlreadyInPosition+After3Pm --
confirms every single signal was accounted for, none silently dropped.

**Day-wise**:

| Date | A Signals | B Signals | Trades | Wins | Losses | WinRate | GrossPnl | Costs | NetPnl | AvgTrade | MaxDD | MaxConsecLosses | AvgMAE | AvgMFE |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 09-08 | 142 | 168 | 166 | 60 | 106 | 36.1% | -19890.00 | 4078.59 | -23968.59 | -144.39 | -36907.90 | 9 | 1.75 | 1.83 |
| 09-15 | 233 | 208 | 217 | 94 | 123 | 43.3% | 39552.50 | 4901.78 | 34650.72 | 159.68 | -23634.44 | 10 | 1.94 | 2.88 |
| 09-22 | 203 | 181 | 223 | 84 | 139 | 37.7% | 19792.50 | 4002.50 | 15790.00 | 70.81 | -44313.53 | 8 | 1.90 | 2.41 |

Combined net P&L across all 3 days: **+₹26,472.13** (606 trades). Performance is **not**
concentrated in one day -- 09-08 lost money, 09-15 and 09-22 made money -- but the day-to-day
swing is large (from -₹23,969 to +₹34,651), consistent with only 3 independent sessions.

**Pattern A -> PE** (n=304, 101.33 trades/day): WinRate=43.8%, GrossPnl=90675.00,
TotalCosts=9078.73, **NetPnl=+81596.27**, AvgNetPnl/trade=+268.41, MedianNetPnl/trade=-216.51
(median negative despite positive mean -- the distribution is right-skewed, a small number of
large winners driving the total), ProfitFactor=1.34, MaxDrawdown=-29133.49,
MaxConsecutiveLosses=14, AvgMAE=₹2.31, AvgMFE=₹3.30 (MFE > MAE on average), AvgHoldingTime=1m46s,
MedianHoldingTime=1m06s.

**Pattern B -> CE** (n=302, 100.67 trades/day): WinRate=34.8%, GrossPnl=-51220.00,
TotalCosts=3904.14, **NetPnl=-55124.14**, AvgNetPnl/trade=-182.53, ProfitFactor=0.71,
MaxDrawdown=-86651.96, MaxConsecutiveLosses=10, AvgMAE=₹1.43, AvgMFE=₹1.53 (MFE barely exceeds
MAE), AvgHoldingTime=1m38s, MedianHoldingTime=57s.

**This is a clean, direct confirmation of what the earlier Option-Response Decomposition
experiment already suggested indirectly**: Pattern A's PE is genuinely profitable in raw option
P&L terms; Pattern B's CE is genuinely unprofitable, as the task's own framing anticipated
("previous research specifically showed weaker CE monetization" -- confirmed, not assumed).

**MFE-captured caveat**: the reported average MFE-captured ratio (Pattern A: -1.78, Pattern B:
-2.12) is a known statistical artifact of averaging a ratio with a small, sometimes near-zero
denominator (MFE) across many small losing trades -- a losing trade with a tiny MFE and a larger
loss produces a hugely negative outlier ratio that dominates the mean. This is reported as-is
(the task asked for the average), but should not be read literally as "captured -178%/-212% of
MFE" -- the underlying per-trade values are in the exported CSV for a more robust (e.g. median)
recomputation if needed.

**Trade frequency is far above any plausible production target** (~101-102 trades/day per side,
~202/day combined, vs. this project's own stated 5-10 trades/day design goal for an eventual
composite score) -- expected and by design for this first, deliberately unfiltered pass (every
single qualifying event is a signal, per the task's own instruction), not a bug. Converting this
raw behaviour into anything deployable would require filtering the vast majority of these
signals -- a future research question, not attempted here.

### 5. TEMPORAL OOS -- 2026-09-24 -- BLOCKED, DATA NOT AVAILABLE

**2026-09-24 Nifty data is not present in the local `niftysignal_vm_copy` mirror.**
`vc-underlying-inventory` was re-checked twice (once before implementation, once again after the
discovery simulation completed) and both times NIFTY's own date range remained 2026-09-04 to
**2026-09-23** -- unchanged. No fabricated or interpolated data was substituted. The methodology
and simulation rules above are fully frozen and ready to run the instant this session's data is
synced into the local mirror -- but **the OOS leg of this experiment could not be executed** and
none of its required questions (Q14-Q20 below) can be answered yet.

### 6. Look-ahead / data-leakage audit (task item 21)

- Signal uses only information available at signal timestamp: **Pass** -- `UnderlyingOptionRelationshipRecorder`
  (unmodified) has its own existing no-look-ahead regression test
  (`RecordAsync_ClassificationAtEventT_NeverDependsOnDataAfterT_NoLookahead`).
- ATM selection uses futures price available at signal: **Pass** -- `EventBarStrikeSelector.PickDynamicAtm`
  keyed off that event's own `FuturesClose` only.
- Option contract pinned at signal: **Pass** -- `row.AtmStrike`/`PeToken`/`CeToken` captured once
  into the `open` position state and never re-derived for the life of the trade.
- Entry uses a future tick after signal: **Pass** -- `OptionTickSeries.EntryAtOrAfter(row.EndTimestamp)`.
- Exit uses only future ticks: **Pass** -- `EntryAtOrAfter`/`EntryAtOrBefore`, both relative to a
  timestamp at or after the position's own entry.
- MAE/MFE from post-entry data only: **Pass** -- path prices filtered strictly
  `> EntryTimestamp` through `<= ExitTimestamp`.
- Today's OOS data was not used for parameter selection: **Pass, trivially** -- no parameter was
  tuned at all (fixed 1300/10 lots/15:00/15:15/opposite-pattern-exit, all specified in advance),
  and the OOS data doesn't even exist locally yet.
- No future option price influences strike selection: **Pass** -- strike selection depends only
  on the futures close at the signal bar, never an option price.
- No future underlying price influences signal classification: **Pass** -- same recorder,
  same existing regression test as the first item.

### 7. Research conclusion (descriptive classification, no "best strategy" verdict)

- **Relationship behaviour**: Confirmed to fire very frequently at the raw event level (606
  executable signals across 3 days) -- consistent with every prior finding in this research
  chain.
- **Option monetization behaviour**: Pattern A -> PE monetizes the relationship profitably in
  raw P&L; Pattern B -> CE does not, consistent with (and now directly confirming) the earlier
  Option-Response Decomposition finding.
- **Execution behaviour**: Every signal was accounted for (0 MissingOptionData/ContractTransition/
  NoExecutableTick in this dataset); entries/exits execute on real ticks within seconds to low
  minutes of the signal, never fabricated.
- **Risk/MAE behaviour**: MAE magnitudes are small in rupee terms per trade (~₹1.4-2.3 average)
  but the % terms can be large on cheap options (individual trades with MAE% >20-30% appear in
  the trade-by-trade CSV) -- no stop loss was applied in this first pass, per the task's explicit
  instruction, so these are genuine unmanaged excursions, not a designed risk profile.
- **OOS behaviour**: Not yet observed -- blocked on data availability (Section 5).

### 8. Next research questions (not implemented here)

Whether filtering the ~200-trades/day raw signal down toward a practical frequency (via a filter
not yet chosen) preserves Pattern A's edge and/or fixes Pattern B's; whether a stop loss would
help Pattern A (right-skewed distribution, median trade is a small loss) without giving back its
large winners; whether Pattern B's CE weakness is structural enough to exclude it entirely rather
than try to fix it; and, first and foremost, running the now-fully-frozen OOS protocol the moment
2026-09-24 (or any later) Nifty data is synced into the local mirror.

### 9. Build / test

New: `PatternRelationshipTradeSimulatorTests.cs` (14 tests covering all 15 required scenarios --
Pattern A->PE, Pattern B->CE, entry-after-signal, no-entry-after-3pm, forced-1515-close,
overlapping-position handling, sequential/reversal trades, 10-lot quantity, MAE, MFE,
missing-data handling, contract-transition handling, strike-pinning, transaction costs, and
day-to-day state isolation) and `TransactionCostCalculatorTests.cs` (5 tests). `dotnet build
NiftySignal.slnx`: 0 warnings/errors. `dotnet test NiftySignal.slnx`: **945/945 passing** (926
baseline + 19 new).

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-trade-simulation 2026-09-01 2026-09-23 --label=DISCOVERY --tradesOut=vc-trades-discovery.csv --auditOut=vc-audit-discovery.csv
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-trade-simulation 2026-09-24 2026-09-24 --label=OOS --tradesOut=vc-trades-oos.csv --auditOut=vc-audit-oos.csv
```

### 10. Addendum -- [100,150] band strike-selection rule (2026-09-24, same day, user follow-up)

The user asked, after seeing Section 4's results: did strike selection use the [100,150] premium
band (the rule from `Vc0DteTradeSimulator`, Experiment 1)? **No** -- Section 4 used the frozen
relationship's own pinned dynamic-ATM contract, not a price band. The user then asked for the
same [100,150] band rule to be applied here and the identical report reproduced for comparison.
Added additively: `PatternRelationshipTradeSimulator.SimulateDayAsync` gained optional
`optionBars`/`minEntryPrice`/`maxEntryPrice` parameters (default `null`, preserving Section 4's
original pinned-ATM behaviour byte-for-byte -- all 14 original tests still pass unchanged) and a
new `SelectBandStrike` path, mirroring `Vc0DteTradeSimulator.TryEnterAsync`'s existing convention
exactly: walk the chain outward from the dynamic-ATM strike, take the first contract whose OWN
live premium at the signal event falls in [100,150]. 2 new tests cover it (band pick differs from
ATM; no-strike-in-band is audited, not dropped). `dotnet build`: 0 warnings/errors. `dotnet test`:
**947/947 passing** (945 baseline + 2 new).

Same 3 discovery sessions, same 1135 signals, same 606 executed trades (`NoStrikeInBand=0` --
a band contract existed for every single signal in this dataset):

| Metric | Pinned ATM (Section 4) | [100,150] band |
|---|---|---|
| Pattern A -> PE NetPnl | +81,596.27 | **+94,375.03** |
| Pattern A -> PE WinRate / ProfitFactor | 43.8% / 1.34 | 43.4% / 1.30 |
| Pattern B -> CE NetPnl | -55,124.14 | **-163,202.47** |
| Pattern B -> CE WinRate / ProfitFactor | 34.8% / 0.71 | 33.1% / 0.62 |
| Combined NetPnl (A+B) | +26,472.13 | **-68,827.44** |
| Day-wise NetPnl (09-08 / 09-15 / 09-22) | -23,968.59 / +34,650.72 / +15,790.00 | -59,547.77 / -1,721.33 / -7,558.34 |
| AvgMAE / AvgMFE, Pattern A | ₹2.31 / ₹3.30 | ₹2.99 / ₹4.23 |
| AvgMAE / AvgMFE, Pattern B | ₹1.43 / ₹1.53 | ₹3.33 / ₹3.22 |
| TotalCosts (both patterns) | ₹12,982.87 | ₹29,762.44 |

**Pattern A -> PE improved** under the band rule (+94,375 vs +81,596) -- a cheaper-band contract
this close to the underlying's own dynamic-ATM apparently captured slightly more of the same
directional move here. **Pattern B -> CE got materially worse** (-163,202 vs -55,124, more than
2.9x the loss) -- band selection did not rescue Pattern B's already-established weak CE
monetization, it worsened it, on this dataset. **Every single day flips to net-negative under
the band rule** (all 3 days negative, vs. 2 of 3 positive under pinned ATM) -- combined P&L flips
from +26,472 (pinned ATM) to **-68,827** (band). Total transaction costs also roughly doubled
(the band rule's own instrument selection lands on materially different, evidently more
expensive-to-round-trip contracts on average).

**This is reported as a direct empirical comparison of two strike-selection conventions on the
identical signal set, not a verdict on which is "correct"** -- both are non-optimized, literal
applications of a specific rule (frozen-ATM vs. this project's own pre-existing [100,150] band
convention), and the divergence itself (especially Pattern B's much larger loss) is a new,
notable finding worth carrying into the "next research questions" list: WHY the band rule hurts
Pattern B this much more than it helps Pattern A is not yet understood and was not investigated
here (per the task's own no-optimization instruction).

### Reproduction (band addendum)

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-trade-simulation 2026-09-01 2026-09-23 --label=DISCOVERY-BAND100-150 --minEntryPrice=100 --maxEntryPrice=150 --tradesOut=vc-trades-band.csv --auditOut=vc-audit-band.csv
```

## Trade-Lifecycle Diagnostics (2026-09-24)

**Question**: understand WHY the strategy produces ~200 trades/day, quantify churn, and identify
evidence-based improvement hypotheses -- diagnostic only, no strategy change, no optimizer, no
SL/TP, no composite score. The ₹100-150 band is now treated as the intended execution convention
(not challenged or re-optimized).

### Part 1 -- implementation verification

Confirmed exactly frozen: Pattern A/B definitions, 1300 threshold, event construction, opposite-
pattern exit, 15:00/15:15 cutoffs, 10 lots, cost convention, ₹100-150 band. One factual note
(not a discrepancy): the simulator does **not** deduplicate signals to episode-first-events --
every qualifying event is its own signal attempt, naturally suppressed by "already in position."
This was the ORIGINAL design of the first trade-simulation task, not drift, and it directly
explains part of Part 2's finding below (episodes turn out to average only ~1.05-1.10 events, so
this distinction barely matters in practice).

### Part 2 -- trade-frequency diagnosis

Raw events: A=578, B=557 (1135 total, matching every prior experiment exactly). Episodes: A=548,
B=508 -- **episodes are almost 1:1 with raw events** (avg 1.05/1.10 events per episode, median 1,
max 3/4). Signal lifecycle: Executed=606, AlreadyInPosition=440, After3Pm=89 (sums to 1135
exactly). **Every single one of the 606 executed trades closed via the opposite-pattern signal --
zero reached the 15:15 EOD close.**

**Diagnosis: (B) the opposite-pattern exit mechanism is overwhelmingly the source of trade
volume, not (A) many independent episodes** -- since episodes barely differ from raw events, the
relationship itself doesn't manufacture extra signals; it's the fact that essentially every
executed trade gets reversed by the very next opposite signal that produces ~200 trades/day. No
evidence of (D) implementation behaviour (audit reconciles exactly; zero missing-data/contract-
transition/no-tick/no-strike-in-band rejections in this dataset).

### Part 3 -- churn diagnosis

A->B=301, B->A=302 (near-perfectly balanced, as the reversal mechanism requires), A->A=2 (rare
edge case), B->B=0. 3- and 4-hop chains (A->B->A=301, B->A->B=299, 4-hop~298-299) are almost as
frequent as the 2-hop transitions themselves -- the day is essentially one long alternating chain,
not isolated independent reversals. Reversal timing: only 16.8% of reversals happen within 1
event, but **49.3% happen within 1 minute and 94.1% within 5 minutes** -- churn is fast in wall-
clock time even when it takes a few events.

### Part 4 -- holding-time diagnosis

For **both** patterns, P&L improves monotonically with holding time. Pattern A: `<1 min` (n=143,
47% of A's trades) avgPnl=-128.15, totalPnl=-18,325.77; `5-10 min` (n=12) avgPnl=+2,617.23; `10-15
min` (n=4) avgPnl=+6,631.24. Pattern B: `<1 min` (n=154, 51% of B's trades) avgPnl=-920.02,
totalPnl=**-141,682.37** (this single bucket is larger in magnitude than B's entire -163,202
total); `5-10 min` (n=15) avgPnl=+2,154.16, winRate=53.3%. **The sub-1-minute bucket alone accounts
for essentially all of Pattern B's aggregate loss** and a large fraction of Pattern A's forgone
gain.

### Part 5 -- lifecycle P&L

`Entry -> EOD`: n=0 (confirmed above). Reversal exits by holding-time bucket: `<1 min` (n=297)
totalPnl=-160,008.14; `5-10 min` (n=27) totalPnl=+63,719.16; `10-15 min` (n=8) totalPnl=+29,018.25.
Chain-level: `A->B->A` chains average **+85.13**/chain (winRate 41.9%); `B->A->B` chains average
**-705.90**/chain (winRate 36.5%) -- the A/B asymmetry reproduces itself at the chain level, as
expected (this is corroborating, not independent evidence).

### Part 6/7 -- improvement dimensions and A/B asymmetry (combined, per the task's own request to
show them side by side)

- **A. Signal strength / E. movement tercile** (same metric, reported under both letters):
  non-monotonic for both patterns (A's "Mid" tercile is oddly near-zero; B is negative in all
  three terciles) -- **not a clean explanatory dimension** for either pattern's outcome.
- **B. Episode position/length**: nearly all episodes are single-event (B1 has only a "First
  event" row for both patterns), so this dimension has almost no discriminating power in this
  dataset.
- **C. Alternation count**: dominated by the "3+" bucket (298/304 A, 296/302 B) -- buckets 0/1/2
  have n=1-3 each, too small to read anything into.
- **D. Time since previous actionable signal**: **mirrors Part 4's holding-time finding** --
  `<1 min` is both the most populated (216/304 A, 201/302 B) and the worst-performing bucket for
  BOTH patterns (A: avgPnl=417.84 vs 5-10min's 3,117.16; B: avgPnl=-535.25 vs 2-5min's +708.65).
  Likely the same underlying phenomenon as Part 4/H1, viewed from the signal side rather than the
  trade side.
- **F. DTE**: constant at 0 in this dataset -- not testable here.
- **G. Session bucket**: **a real, clean asymmetry**. Pattern A is net positive in 5 of 6 session
  buckets (only 11:00-12:00 negative). Pattern B is net negative in 5 of 6 buckets (only
  14:00-15:00 positive). This is the most session-consistent distinguishing signal found.
- **H. Option execution characteristics -- the most consequential finding of this experiment**:
  Pattern A's band-selected contracts sit **66.94 points** from ATM on average; Pattern B's sit
  **146.36 points** away -- **more than double**. Average moneyness: A=65.06, B=143.79. Within
  Pattern A, the "Low" (near-ATM) strike-distance tercile dominates (252/304 trades) and is
  clearly profitable (avgPnl=496.61); the few "High"-distance A trades (n=4) are negative. Within
  Pattern B, both distance terciles are negative, but "Low" is less negative than "High" --
  consistent with the same direction of effect, just starting from a worse baseline. **This
  strongly suggests the ₹100-150 band, applied identically to both option types, forces Pattern
  B's cheaper CE surface to select strikes structurally much farther from ATM than Pattern A's PE
  surface** -- a direct, mechanistic link back to the Option-Response-Decomposition finding that
  Pattern B's CE trades at roughly half Pattern A's PE price at comparable moneyness (Nifty's own
  put/call skew).

### Part 10 -- MAE/MFE excursion behaviour

For both patterns, winners have low MAE and high MFE (A: winners MAE=1.39/MFE=7.66; B: winners
MAE=1.12/MFE=7.28) while losers have high MAE and low MFE (A: losers MAE=4.21/MFE=1.60; B: losers
MAE=4.42/MFE=1.21) -- the expected, reassuring shape. MFE-at-exit for reversal-closed trades
(A avg=4.23, B avg=3.22) sits close to each pattern's own overall average MFE, suggesting the
reversal exit is not systematically leaving large unrealized favourable excursions on the table
on average (though this masks per-trade variance). **Time-to-MFE (the temporal excursion path)
was NOT computed in this pass** -- a documented scope limitation, not a fabricated number.

### Part 8 -- improvement hypotheses (NOT implemented -- diagnostic only, awaiting review)

1. **Holding-time / signal-bunching filter.** *Observed*: sub-1-2-minute trades are the most
   numerous and the worst-performing bucket for both patterns; longer-held trades are strongly
   profitable for both. *Variable*: holding time (Part 4) / time-since-previous-signal (Part 6D)
   -- likely two views of the same phenomenon. *Support*: ~450 of 606 trades combined. *All 3
   days?*: not verified per-day in this pass. *Affects*: both A and B. *DTE/session
   consistency*: not cross-checked this pass. *Genuine or artifact?*: **plausibly genuine, but
   with a real causal-ambiguity caveat** -- the opposite-pattern exit condition itself requires
   the underlying to move again, so holding time and profitability may be mechanically linked by
   construction, not independently informative. *Future experiment*: a controlled, separately
   labelled test of whether restricting reversal-eligibility for some minimum period changes
   trade quality -- itself a rule change, so it needs its own single experiment, not folded into
   this one.
2. **Session-time weakness specific to Pattern B.** *Observed*: B negative in 5/6 session
   buckets, positive only 14:00-15:00; A positive in 5/6. *Variable*: session bucket. *Support*:
   302 B trades across 6 buckets (~30-75 each). *All 3 days?*: not verified per-day. *Affects*: B
   specifically. *Genuine or artifact?*: consistent sign across 5 buckets is suggestive, but
   per-bucket-per-day counts are modest (3 days only). *Future experiment*: session-stratified
   replication once more independent sessions exist.
3. **Strike-distance/moneyness effect from applying one price band to two structurally different
   premium surfaces.** *Observed*: B's band-selected strikes average >2x farther from ATM than
   A's; within-pattern, farther-from-ATM correlates with worse P&L for both. *Variable*: |strike
   distance from ATM| / moneyness at entry. *Support*: 302-304 trades per pattern. *Genuine or
   artifact?*: likely genuine and mechanistic -- ties directly to the already-documented Nifty
   put/call skew from the Option-Response-Decomposition experiment. *Future experiment*: an
   intrinsic/extrinsic decomposition (reusing `OptionValueDecomposition`, unmodified) applied
   specifically to the BAND-selected contracts, not the pinned-ATM ones, to see if the same
   mechanism explains the gap.
4. **Chain-level asymmetry (`B->A->B` chains average -705.90 vs `A->B->A`'s +85.13).**
   *Observed* and *Support*: ~300 chains each. *Genuine or artifact?*: most likely **not an
   independent finding** -- restates the same A/B P&L asymmetry already visible at the trade
   level (hypotheses 1-3), not a new lever.

### Part 9 -- explicit note on trade-frequency framing

No filter was chosen or implemented to move trade count toward any target. Per the task's own
instruction, the ~200-trades/day frequency is diagnosed (Part 2: overwhelmingly the exit
mechanism, not the raw relationship) rather than treated as a problem to suppress by construction.

### Recommended experiment sequence (not run)

1. Re-verify hypotheses 1 and 2 hold on a **per-day** basis (not just the 3-day aggregate) before
   trusting them further -- pure verification, no rule change.
2. If confirmed, run ONE controlled, separately-labelled experiment on hypothesis 1 (minimum
   reversal-eligibility delay) -- understanding this is itself a rule change, not a diagnostic.
3. Hypothesis 2 (session-time weakness) needs more independent sessions before a real
   session-based conclusion is possible; revisit when new data exists.
4. Hypothesis 3 (moneyness/strike-distance decomposition) can run now, reusing existing
   decomposition infrastructure unchanged, before any strike-selection discussion (which remains
   explicitly out of scope).

### Part 12 -- integrity checks

Signal counts reconcile exactly: 1135 = 606 (Executed) + 440 (AlreadyInPosition) + 89 (After3Pm).
Daily raw-event counts reconcile: 142+233+203=578 (A), 168+208+181=557 (B). A+B=1135. Trade CSV
count (606) matches the simulator's own `Executed` count. MAE/MFE values are the same, already-
tested `MaeMfeCalculator` output the first simulation used -- no new calculation was introduced
for those fields. The ₹100-150 band changes WHICH contract is selected only -- signal generation
(1135/606/440/89, identical to the pinned-ATM run) and lifecycle rules (15:00/15:15, opposite-
pattern exit) are unchanged, confirmed both by code inspection and by the reconciled counts above.
No overlapping trades / one entry-exit per trade are structural invariants of `TradeRow`, already
covered by the existing `PatternRelationshipTradeSimulatorTests`.

### Build / test

New: `TradeLifecycleDiagnostics.cs` (pure: holding-time bucketing, alternation counting, episode
lookup, n-gram churn counting) with `TradeLifecycleDiagnosticsTests.cs` (20 tests). `dotnet build
NiftySignal.slnx`: 0 warnings/errors. `dotnet test NiftySignal.slnx`: **967/967 passing** (947
baseline + 20 new).

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-trade-diagnostics 2026-09-01 2026-09-23 --diagOut=vc-diagnostics.csv
```

## Mark-to-Market Signal-Quality Diagnostics (2026-09-24)

**Question**: separate signal quality, signal invalidation, option-execution/moneyness effects,
and exit-rule effects -- is the outcome already visible before the opposite pattern exits the
trade, and is Pattern B intrinsically weak or a strike-selection execution artifact? Diagnostic
only -- no filter, cooldown, minimum holding period, SL/TP, or parameter change implemented. The
₹100-150 band remains the accepted execution convention, not re-optimized.

### Implementation

New: `MarkToMarketDiagnostics.cs` (pure: 4 holding-time groups and 6 MFE buckets, both specified
verbatim by the task; strike-step buckets using the day's own minimum strike gap, never a
hardcoded constant; excursion-timing calculation) with 28 tests, and the
`vc0dte-relationship-mtm-diagnostics` CLI command, which reuses the frozen ₹100-150-band
simulation, `OptionValueDecomposition`, and the existing tercile methodology completely
unmodified -- it only measures, never changes, the trading logic. `dotnet build`: 0
warnings/errors. `dotnet test`: **995/995 passing** (967 baseline + 28 new).

### Part 1 -- mark-to-market before the opposite pattern exits (not a simulated exit)

| Horizon | A->PE avg Rs | A->PE median Rs | B->CE avg Rs | B->CE median Rs |
|---|---|---|---|---|
| +1 event | 0.12 | 0.15 | -0.40 | -0.35 |
| +3 events | 0.38 | 0.40 | -0.69 | -0.60 |
| +5 events | 0.85 | 0.80 | -1.13 | -0.65 |
| +10 events | 1.02 | 0.75 | -0.70 | -0.85 |
| +1 min | 0.80 | 0.45 | -0.59 | -0.40 |
| +3 min | 1.83 | 1.05 | -1.98 | -1.40 |
| +5 min | 2.96 | 1.60 | -2.98 | -2.90 |
| +10 min | 4.86 | 2.60 | -4.06 | -3.80 |
| +15 min | 5.25 | 2.40 | -4.90 | -3.40 |

**Pattern A's average mark-to-market is positive at every single horizon from +1 event onward and
keeps growing through +15 minutes with no peak yet visible. Pattern B's is negative at every
horizon from +1 event onward and keeps getting WORSE through +15 minutes.** The sign of the
eventual outcome is set almost immediately for both patterns -- this is not something that only
emerges after a long hold.

### Part 2 -- signal quality before invalidation

Every trade in this dataset is reversal-exited (0 reach EOD, per the prior diagnostic), so this
section IS "before the opposite pattern." Pattern A: AvgMFE=4.23, AvgMAE=2.99, AvgTimeToMFE=56s,
AvgTimeToMAE=42s. Pattern B: AvgMFE=3.22, AvgMAE=3.33, AvgTimeToMFE=47s, AvgTimeToMAE=46s. Both
patterns reach their own MFE/MAE within under a minute on average -- excursions happen fast.

### Part 3 -- first-5-minutes groups (descriptive, not filters)

| Group | n (A/B) | AvgMAE | AvgMFE | NetPnl |
|---|---|---|---|---|
| Group1 (<1 min) | 297 (143/154) | 2.29 | 1.71 | **-160,008.14** |
| Group2 (1-2 min) | 149 (74/75) | 3.14 | 4.29 | +6,883.21 |
| Group3 (2-5 min) | 124 (70/54) | 4.50 | 5.37 | -16,550.60 |
| Group4 (>5 min) | 36 (17/19) | 5.71 | 12.36 | **+100,848.09** |

Group4 (only 36 trades, 6% of all trades) contributes more total profit than every other group
combined loses. Group1 (49% of all trades) is overwhelmingly the source of aggregate loss.

### Part 4 -- MFE-before-invalidation buckets

| MFE bucket | A n | A winRate | A realizedPnl | B n | B winRate | B realizedPnl |
|---|---|---|---|---|---|---|
| 0-1% | 101 | 6.9% | -178,989.25 | 139 | 2.9% | -278,896.61 |
| 1-2% | 53 | 26.4% | -48,381.58 | 47 | 27.7% | -77,493.43 |
| 2-5% | 84 | 59.5% | -4,166.11 | 71 | 57.7% | -19,371.65 |
| 5-10% | 40 | 87.5% | +107,196.85 | 31 | 93.5% | +87,905.83 |
| >10% | 26 | 100.0% | +218,715.12 | 14 | 92.9% | +124,653.39 |

**Win rate rises with MFE bucket almost identically for both patterns** -- when a trade DOES
develop meaningful favourable movement, it wins for A and B alike. The A/B difference is in the
*distribution*: B has proportionally more trades stuck in the worst bucket (139/302=46% vs
A's 101/304=33%) and fewer reaching the best bucket (14/302=4.6% vs A's 26/304=8.6%).

### Part 5 -- entry price / strike distance / intrinsic-extrinsic decomposition

| Metric | A->PE | B->CE |
|---|---|---|
| Median strike distance | 50.00 | 150.00 |
| Mean strike distance | 66.94 | 146.36 |
| Median moneyness | 71.50 | 137.10 |
| Median premium | 122.20 | 121.15 |
| Median intrinsic | 71.50 | 137.10 |
| Median extrinsic | **52.15** | **-9.50** |
| Intrinsic % of premium | 58.82% | 106.93% |

**Pattern B's median trade has essentially zero-to-negative extrinsic value at entry** -- the
₹100-150 band, applied to Pattern B's cheaper CE surface, lands on a deeply in-the-money contract
whose price is nearly pure intrinsic value, with almost no time-value cushion. Pattern A's median
trade retains a real ₹52.15 extrinsic cushion. A near-zero-extrinsic option behaves close to a
leveraged futures position -- essentially none of a real option's optionality remains.

### Part 6 -- strike-step buckets vs outcome

Pattern A has trades at every step distance (0 through 3+); Pattern B has **zero trades within 1
step of ATM** -- the band structurally cannot select a near-ATM CE for Pattern B in this dataset.
Within A, "1 step away" (n=109) is the best-performing bucket (avgPnl=+900.49); "ATM/closest"
(n=48) is oddly the worst (avgPnl=-572.70) -- not a clean monotonic relationship. Within B, all
three populated buckets (2/3/4+ steps) are negative, "2 steps away" least negative (-295.07),
"3 steps away" most negative (-676.80) -- also not cleanly monotonic. The STRUCTURAL fact that A
can reach near-ATM cushion and B categorically cannot is the clearer finding than any
within-pattern monotonic trend.

### Part 7 -- intrinsic vs extrinsic response

| Horizon | A Intrinsic(mean) | A Extrinsic(mean) | B Intrinsic(mean) | B Extrinsic(mean) |
|---|---|---|---|---|
| +1 event | 2.28 | -2.16 | 1.48 | -1.89 |
| +3 events | 2.68 | -2.30 | 1.95 | -2.64 |
| +5 events | 3.13 | -2.28 | 1.46 | -2.59 |
| +10 events | 3.50 | -2.47 | 1.87 | -2.57 |

**Extrinsic decay magnitude is SIMILAR between A and B** (roughly -2.2 to -2.6 for both) -- B is
not decaying dramatically faster. **Intrinsic growth is the real gap**: A's intrinsic grows to
2.28-3.50; B's only to 1.46-1.95, roughly half. This is consistent with (not a new finding beyond)
the earlier Pre-Pattern-Post Transition Analysis's own conclusion that Pattern B's underlying
reversal is later-stage and smaller than Pattern A's.

### Part 8 -- signal magnitude (existing tercile methodology)

Non-monotonic for both: A's High tercile (avgPnl=593.42) beats Low (347.76) which beats Mid
(7.91) -- an odd U-shape, not a clean "bigger move = better" relationship. B's Low tercile is
actually its WORST (-774.72), not best -- also not the expected direction. **Signal magnitude does
not cleanly explain either pattern's outcome in this dataset.**

### Part 9 -- consolidated A vs B table

| Dimension | A->PE | B->CE |
|---|---|---|
| Trade count | 304 | 302 |
| Median holding time | 01:05 | 00:57 |
| Median strike distance | 50.00 | 150.00 |
| Median moneyness | 71.50 | 137.10 |
| Median entry premium | 122.20 | 121.15 |
| Median intrinsic value | 71.50 | 137.10 |
| Median extrinsic value | 52.15 | -9.50 |
| Median +1m option return | 0.45 | -0.40 |
| Median +3m option return | 1.05 | -1.40 |
| Median +5m option return | 1.60 | -2.90 |
| Median +10m option return | 2.60 | -3.80 |
| Median MFE | 2.50 | 1.40 |
| Median MAE | 2.15 | 2.30 |
| Net P&L | 94,375.03 | -163,202.47 |

### Part 10 -- temporal robustness

| Date | A n | A NetPnl | B n | B NetPnl |
|---|---|---|---|---|
| 2026-09-08 | 83 | -12,225.42 | 83 | -47,322.35 |
| 2026-09-15 | 109 | +88,446.69 | 108 | -90,168.02 |
| 2026-09-22 | 112 | +18,153.76 | 111 | -25,712.10 |
| POOLED | 304 | +94,375.03 | 302 | -163,202.47 |

**Pattern B is net-negative on all 3 days** -- not concentrated in one session. Pattern A is
net-negative on 09-08 but positive on the other two; the pooled positive result is not purely a
one-day artifact, but 09-08's loss is a genuine, non-trivial exception worth remembering.

### Answers to the required questions

- **Q1 (Is the outcome already visible before the opposite-pattern exit?)**: **Yes.** Both
  patterns' average mark-to-market return has the SAME sign from +1 event/+1 minute onward as
  their eventual pooled result, and that sign never flips at any measured horizon.
- **Q2 (Does the opposite-pattern exit close trades before they've developed?)**: **Asymmetric.**
  For Pattern A: partially -- mark-to-market keeps improving through +15 minutes while median
  actual holding time is only ~1 minute, a real, quantifiable forgone gain. For Pattern B: **no**
  -- mark-to-market keeps deteriorating the longer it's held, so the same exit rule is
  protective for B, not premature.
- **Q3 (Is B intrinsically weaker, or is the band a meaningful execution difference?)**: **Both,
  with the execution/moneyness difference the more clearly evidenced factor.** B's band-selected
  contract has near-zero extrinsic cushion (leveraged-futures-like exposure); its intrinsic value
  also grows about half as much as A's (a real, independently-documented underlying-strength
  gap). Correlation only -- causality between the two is not established here.
- **Q4 (How much of B's result is associated with strike distance?)**: **Substantial but not
  cleanly isolated.** B never lands within 1 step of ATM (0 trades) while A does regularly; B's
  MFE distribution is shifted toward the worst bucket. The pinned-ATM run (prior experiment) was
  ALSO negative for B (-55,124 vs the band's -163,202) -- so strike distance amplifies an
  already-existing weakness, it does not single-handedly create it.
- **Q5 (Does the underlying relationship remain strong even when option P&L is poor?)**: **Yes,
  directionally, but weaker in magnitude for B.** Both patterns show positive average intrinsic
  growth at every horizon (the underlying moves the expected way for both), but B's is roughly
  half of A's -- consistent with, not new beyond, the Transition Analysis's own finding.
- **Q6**: see hypotheses below.

### Improvement hypotheses (NOT implemented -- awaiting review)

1. **The ₹100-150 band forces Pattern B into deep-ITM, near-zero-extrinsic contracts, amplifying
   an already-existing weakness.** *Confidence*: **Strong descriptive evidence** (Parts 5, 6, 7,
   9 all agree; consistent across all 3 days). *Future experiment*: none needed to confirm this
   specific point further -- already cross-validated against the pinned-ATM run's own numbers.
2. **The opposite-pattern exit rule has opposite effects on the two patterns relative to their own
   natural mark-to-market drift** -- costly for A (cuts off continuing improvement), protective
   for B (avoids continuing deterioration). *Confidence*: **Strong descriptive evidence** (Part 1,
   clean and monotonic in both directions). *Future experiment*: check whether this asymmetry
   holds when stratified by session/DTE before any exit-rule discussion (still not a rule change).
3. **B's MFE distribution is shifted toward low-conviction outcomes relative to A's.**
   *Confidence*: **Moderate descriptive evidence** (Part 4 is clean, but the underlying cause --
   execution vs. signal quality -- is not separated). *Future experiment*: test whether
   MFE-bucket membership is predictable ex-ante from existing features (session, DTE, magnitude)
   -- a classification-style diagnostic, not a filter.
4. **Signal magnitude (Part 8) does not explain either pattern's outcome.** *Confidence*: **Weak/
   descriptive only** (non-monotonic, small tercile sizes across only 3 days). *Future
   experiment*: none proposed -- this dimension appears unproductive with current data.

### Part 12 -- integrity

Trade counts reconcile exactly with the prior diagnostic and the original band simulation: 304
(A) + 302 (B) = 606 = the same `Executed` count from every prior run on this dataset. Day sums
(83+109+112=304 for A; 83+108+111=302 for B) match the per-day trade counts already reported.
Nothing in this command touches signal generation, entry/exit timing, or the ₹100-150 band --
verified both by code inspection (measurement only, no new trading decision) and by these
reconciled counts.

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-mtm-diagnostics 2026-09-01 2026-09-23 --diagOut=vc-mtm-diagnostics.csv
```

## Exit-Asymmetry Validation (2026-09-24)

**Question**: is the A-premature/B-protective exit asymmetry (from the prior diagnostic) robust
across sessions, days, and structurally distinct from a signal-quality difference -- and how
large is it economically? Diagnostic only, no exit/filter/threshold change.

### Implementation

New: `MarkToMarketDiagnostics.ComputeOpportunityCost` (pure, tested) and `ExitAsymmetryRow`, plus
the `vc0dte-relationship-exit-asymmetry-validation` CLI command, reusing the frozen ₹100-150-band
simulation, `OptionValueDecomposition`, `MaeMfeCalculator`, and `ForensicValidationAnalysis.ComputePercentiles`
unmodified. `dotnet build`: 0 warnings/errors. `dotnet test`: **998/998 passing** (995 baseline +
3 new).

### 1. Core finding, reproduced with before/after-exit excursions

| | A->PE (n=304) | B->CE (n=302) |
|---|---|---|
| Realized NetPnl | +94,375.03 | -163,202.47 |
| Median holding | 01:05 | 00:57 |
| MFE before exit (median) | 2.50 | 1.40 |
| **MFE after exit (median)** | **11.00** (n=303) | 5.35 (n=302) |
| MAE before exit (median) | 2.15 | 2.30 |
| **MAE after exit (median)** | 7.10 (n=303) | **11.65** (n=302) |

**Pattern A's median favourable excursion roughly quadruples (2.50->11.00) after the actual
exit.** **Pattern B's median adverse excursion roughly quintuples (2.30->11.65) after the actual
exit.** This is the clearest, most direct confirmation yet of the asymmetry: A's exit leaves real
upside on the table; B's exit avoids real, larger downside.

### 2. Session-time validation

Pattern A's median MFE-after-exit is large and remarkably STABLE across every session bucket
(8.15 to 13.35, no bucket materially different) -- the "premature exit" pattern is session-wide,
not concentrated in any particular period. Pattern B's median +10-minute mark-to-market is
negative in 5 of its 6 session buckets (only 12:00-13:00 is mildly positive, +0.45) -- also
broadly session-wide. *Limitation*: MAE-after-exit was not separately tabulated per session
bucket in this pass (only MFE-after was) -- the pooled Part 1 figure (11.65) is the strongest
evidence for B's protective effect; the full per-trade `MAEAfterExit` column is in the CSV for
anyone who wants to slice it further.

### 3. DTE validation

**Cannot be separated from the day question with this dataset** -- all 3 discovery sessions are
0-DTE, so DTE=0 for every single trade. Not fabricated; stated plainly as a data limitation, per
the task's own instruction not to combine structurally related sessions and call them independent.

### 4. Day-by-day validation

| Date | A n | A NetPnl | A med+10m | A MFEafter | B n | B NetPnl | B med+10m | B MFEafter |
|---|---|---|---|---|---|---|---|---|
| 09-08 | 83 | -12,225.42 | -0.90 | 8.15 | 83 | -47,322.35 | -0.80 | 5.60 |
| 09-15 | 109 | +88,446.69 | +5.10 | 13.35 | 108 | -90,168.02 | -5.35 | 5.85 |
| 09-22 | 112 | +18,153.76 | +2.25 | 11.00 | 111 | -25,712.10 | -4.30 | 5.05 |

A's median MFE-after-exit is substantial and positive on **all 3 days** (8.15/13.35/11.00), even
though its own +10-minute mark-to-market checkpoint is actually negative on 09-08 specifically --
the opportunity is real every day, but its exact shape varies. B's deterioration (median +10m) is
present on all 3 days but MUCH weaker on 09-08 (-0.80) than on 09-15/09-22 (-5.35/-4.30) -- present
everywhere, but not uniform in magnitude.

### 5. Exit opportunity cost (hypothetical gross P&L at horizon minus actual gross P&L)

| Horizon | A mean | A median | A P25 | A P75 | B mean | B median | B P25 | B P75 |
|---|---|---|---|---|---|---|---|---|
| +1 min | 161.75 | 357.50 | -1,137.50 | 1,527.50 | 110.52 | 260.00 | -780.00 | 1,300.00 |
| +3 min | 826.50 | 552.50 | -1,625.00 | 2,502.50 | -792.91 | -227.50 | -2,502.50 | 1,625.00 |
| +5 min | 1,563.96 | 812.50 | -1,820.00 | 3,737.50 | -1,444.53 | -812.50 | -3,705.00 | 1,690.00 |
| +10 min | 2,801.84 | 1,527.50 | -2,275.00 | 6,272.50 | -2,145.54 | -1,365.00 | -6,987.50 | 2,080.00 |
| +15 min | 3,052.01 | 1,625.00 | -3,185.00 | 7,930.00 | -2,691.90 | -1,852.50 | -8,027.50 | 2,177.50 |

**For A, opportunity cost is positive (favours holding longer) and growing at every horizon --
but P25 is negative throughout, meaning holding longer would have hurt roughly a quarter of A's
trades.** **For B, opportunity cost flips from mildly positive at +1 minute to increasingly
negative from +3 minutes onward** -- the protective effect specifically kicks in from ~3 minutes
past entry, not immediately. Reported gross-to-gross (STT only applies to an actually-executed
sell leg, never charged twice here) -- real net opportunity cost is somewhat smaller than shown.

### 7. Structural A vs B comparison at signal time

| Variable | A->PE | B->CE |
|---|---|---|
| Median \|prior-3-event move\| | 0.01% | 0.01% |
| Median CE change1 at signal | -0.33 | +0.34 |
| Median PE change1 at signal | +0.69 | -0.56 |
| Median entry premium | 122.20 | 121.15 |
| Median moneyness | 71.50 | 137.10 |
| Median intrinsic at entry | 71.50 | 137.10 |
| Median extrinsic at entry | 52.15 | -9.50 |
| Median \|strike distance\| | 50.00 | 150.00 |

**The signal itself is symmetric** (prior-movement magnitude identical; CE/PE change1 mirror each
other, exactly as the frozen definitions require). **The resulting TRADED CONTRACT is not** --
the same ₹100-150 band, applied to each side's own premium surface, lands on structurally
different instruments (B nearly twice as deep ITM, with no extrinsic cushion).

### 8. ₹100-150 band effect, per day

| Date | A strikeDist | A extrinsic | B strikeDist | B extrinsic |
|---|---|---|---|---|
| 09-08 | 0.00 | 101.25 | 200.00 | -73.10 |
| 09-15 | 50.00 | 55.80 | 150.00 | -10.65 |
| 09-22 | 100.00 | 26.00 | 100.00 | +2.70 |

**The DIRECTION of the asymmetry (A always less deep, always more extrinsic cushion than B)
holds on all 3 days** -- but the magnitude varies considerably (A's extrinsic ranges 26-101; B's
ranges from -73 to +2.70, even turning slightly positive on 09-22). The qualitative finding is
robust; the exact numbers are not a stable constant across just 3 days.

### Answers to the required questions

- **Q1 (Does A continue to improve after its exit?)**: **Yes, robustly** -- median MFE-after-exit
  is large and stable across every session bucket and all 3 days, though the +10-minute
  mark-to-market checkpoint specifically dipped negative on 09-08.
- **Q2 (Does B continue to deteriorate after its exit?)**: **Yes, even more cleanly** -- median
  MAE-after-exit roughly quintuples pooled; median +10m mark-to-market is negative in 5 of 6
  session buckets and on all 3 days (though much weaker on 09-08).
- **Q3 (Present on all 3 days?)**: **Yes, directionally, with real magnitude variation** -- see
  Part 4.
- **Q4 (Survives across DTE/session?)**: DTE cannot be tested (0-DTE-only dataset). Session:
  **yes, both patterns' effects are broadly session-wide**, not concentrated in one period.
- **Q5 (Size of A's post-exit opportunity cost?)**: Median +₹357.50 at +1min growing to +₹1,625
  at +15min per trade; mean growing to +₹3,052 at +15min -- economically meaningful, but P25 is
  negative at every horizon (a real minority of trades would be worse off holding longer).
- **Q6 (Size of B's post-exit protection benefit?)**: From +3 minutes onward, median opportunity
  cost is negative and grows to -₹1,852.50 at +15min (mean -₹2,691.90) -- but at +1 minute
  specifically it is still mildly POSITIVE (+₹260 median), so the protection is not immediate.
- **Q7 (Are A/B different enough to be asymmetric relationships?)**: **Yes** -- not because the
  signal itself differs (it's symmetric by construction), but because the SAME execution
  convention produces structurally different traded contracts for the two sides.
- **Q8 (Does the band create a consistent A/B selection difference?)**: **Yes in direction, on
  all 3 days** -- but not in exact magnitude, which varies meaningfully day to day.

### 10. Finding / Uncertainty / Proposed next experiment

**Finding**: Pattern A's opposite-pattern exit realizes only a fraction of its own subsequent
favourable excursion (median MFE ~4.4x larger after exit than before), consistently across
sessions and days. Pattern B's exit is genuinely protective, avoiding a subsequent adverse
excursion that grows ~5x after exit, also broadly consistent. Both patterns' signals are
symmetric by construction; the traded CONTRACTS are not, due to the band's interaction with each
side's own premium surface.

**Uncertainty**: Only 3 independent sessions exist -- day-to-day magnitude varies enough (A's
09-08 mark-to-market dipped negative; B's 09-08 deterioration was much weaker than the other two
days) that the SIZE of the effect, not just its direction, remains uncertain. The exit-timing
asymmetry and the structural contract-selection difference (Parts 7-8) are entangled -- this
experiment cannot yet separate "A's exit is premature because A's own signal is genuinely
stronger" from "A's exit merely has more room to move because its contract has more extrinsic
cushion." All figures are gross-to-gross (real net opportunity cost is somewhat smaller once STT
on an eventual real exit is accounted for). Everything here remains descriptive/correlational --
no causal test has been run.

**Proposed next controlled experiment** (not implemented): the smallest isolated test would
change **only Pattern A's exit rule**, leaving Pattern B's exit completely unchanged, and compare
against the current frozen A+B baseline -- so any P&L difference is attributable solely to the
A-side change. The exact delay/rule is deliberately not proposed here, per the task's own
instruction not to assume a specific horizon ahead of seeing further validation.

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-exit-asymmetry-validation 2026-09-01 2026-09-23 --diagOut=vc-exit-asymmetry.csv
```

## A-Side Exit-Delay Sensitivity Experiment (2026-09-24)

**Question**: is delaying only Pattern A's opposite-pattern exit (by a fixed 1/3/5/10 minutes)
a robust improvement, or an artifact of aggregation? Pattern B is completely untouched. This is a
controlled sensitivity experiment, not a proposed final rule -- no delay is recommended as
"optimal."

### Implementation

The frozen ₹100-150-band baseline (`PatternRelationshipTradeSimulator`, unmodified) runs exactly
once per day; Pattern A's actual entries/exits are never changed. Instead, `DelayedExitCounterfactual`
(new, pure, 8 tests) computes a **counterfactual** alternative exit over the SAME real option
ticks the baseline already used, capped at the existing 15:15 mandatory-close ceiling -- exactly
the "diagnostic layer instead of modifying production behaviour" approach the task itself
suggested. Pattern B is never read, modified, or recomputed anywhere in this command, so its
invariance holds by construction, not by luck. One stated simplification: this measures the price
path only -- it does not model a hypothetical third relationship signal that might have fired for
real during the delay window. `dotnet build`: 0 warnings/errors. `dotnet test`: **1006/1006
passing** (998 baseline + 8 new).

### 1. Baseline reconciliation

1135 signals (606 Executed/440 AlreadyInPosition/89 After3Pm), A=304 trades (+94,375.03), B=302
trades (-163,202.47), combined=-68,827.44 -- **matches the previously validated [100,150]
experiment exactly.** `RECONCILES: True`.

### 2. A-side sensitivity table

| Treatment | A trades | TotalPnl | Median | Mean | WinRate | PF | P25 | P75 | AvgMAE | AvgMFE |
|---|---|---|---|---|---|---|---|---|---|---|
| Immediate (baseline) | 304 | 94,375.03 | -337.96 | 310.44 | 43.4% | 1.30 | -1,511.05 | 1,310.77 | 2.99 | 4.23 |
| +1m | 304 | 175,574.30 | 56.55 | 577.55 | 51.6% | 1.44 | -2,005.72 | 2,970.64 | 4.16 | 5.85 |
| +3m | 304 | 456,880.88 | 501.62 | 1,502.90 | 55.3% | 2.04 | -1,999.97 | 4,230.48 | 5.39 | 8.51 |
| +5m | 304 | 670,597.21 | 1,087.31 | 2,205.91 | 57.6% | 2.33 | -2,326.86 | 5,544.48 | 6.34 | 10.60 |
| +10m | 304 | 925,465.30 | 1,670.34 | 3,044.29 | 58.6% | 2.42 | -3,064.12 | 7,805.04 | 8.18 | 14.34 |

**Pooled, every metric improves monotonically through +10m with no sign of flattening.** This
alone would look like a green light -- but see Parts 6 and 9 below, which is why it is not.

### 3. B invariance control

B trade count (302) and B P&L (-163,202.47) are byte-identical to baseline in every single
treatment -- confirmed trivially, since B is never touched by this command at all.

### 4. Trade-level paired analysis

| Delay | LossToWin | LossToLoss | WinToWin | WinToLoss | Mean delta | Median delta | P25 | P75 | Min | Max |
|---|---|---|---|---|---|---|---|---|---|---|
| +1m | 45 (14.8%) | 127 (41.8%) | 112 (36.8%) | 20 (6.6%) | 267.10 | 194.88 | -1,429.10 | 1,786.38 | -11,692.69 | 12,699.55 |
| +3m | 61 (20.1%) | 111 (36.5%) | 107 (35.2%) | 25 (8.2%) | 1,192.45 | 747.03 | -1,948.78 | 3,605.25 | -9,549.02 | 25,723.91 |
| +5m | 73 (24.0%) | 99 (32.6%) | 102 (33.6%) | 30 (9.9%) | 1,895.47 | 1,461.58 | -2,176.14 | 4,319.80 | -20,397.24 | 35,857.58 |
| +10m | 72 (23.7%) | 100 (32.9%) | 106 (34.9%) | 26 (8.6%) | 2,733.85 | 1,753.91 | -2,663.34 | 6,885.69 | -44,951.88 | 39,755.14 |

**WinToLoss count grows with delay (20->30 at +5m)** -- real trades that would have been winners
under baseline become losers when delayed. **The worst single-trade loss (min) grows severely
with delay** (-11,693 at +1m to -44,952 at +10m) -- the left tail grows at least as fast as the
right tail (max +39,755 at +10m).

### 5. Give-back analysis

| Delay | MaxFavorableInWait (mean/median) | MaxAdverseInWait (mean/median) |
|---|---|---|
| +1m | 4.10 / 2.65 | 2.07 / 2.45 |
| +3m | 7.43 / 5.50 | 3.88 / 3.80 |
| +5m | 9.80 / 8.00 | 5.06 / 4.45 |
| +10m | 13.85 / 10.55 | 7.04 / 6.70 |

Both favourable and adverse excursion grow steadily with delay -- there is real two-sided
movement while waiting, not a one-directional drift.

### 6. Day-by-day -- THE CRITICAL CAUTION

| Date | Baseline A | +1m A | +3m A | +5m A | +10m A |
|---|---|---|---|---|---|
| 09-08 | -12,225.42 | -5,372.18 | -3,650.79 | **+7,717.11** | **-32,200.45** |
| 09-15 | +88,446.69 | +129,403.60 | +330,940.08 | +493,306.01 | +728,166.58 |
| 09-22 | +18,153.76 | +51,542.88 | +129,591.59 | +169,574.09 | +229,499.17 |

**09-15 and 09-22 improve monotonically at every delay, matching the pooled picture. 09-08 does
NOT**: it improves through +5m (briefly turning positive, +7,717) then reverses sharply at +10m
to -32,200 -- WORSE than even doing nothing (baseline -12,225). **The pooled "still rising at
+10m" result in Part 2 is driven entirely by 2 of the 3 days and hides a real reversal on the
third.** Combined (A+B) P&L on 09-08 at +10m (-79,522.80) is also worse than baseline combined
(-59,547.77) -- delaying would have made the whole day's result worse, not just A's own slice.

### 7. Session buckets

Median paired delta is positive and growing with delay in the morning (09:15-12:00) and again in
the afternoon (14:00-15:00), but **weak, inconsistent, or negative in the 12:00-14:00 window**
(e.g. 13:00-14:00 is negative at 3 of 4 delays: -422, -487, -195, only +10m positive at +1,169).
**The benefit is broad but not uniform across the session** -- it is notably absent in the midday
window.

### 8. Trade-duration

Every single delayed exit (100%, n=304 at every horizon) found a valid tick well before 15:15 --
`ForcedEod=0`, `Unavailable=0` at every delay. Median holding grows from 01:05 (baseline) to
02:06/04:05/06:05/11:06 for +1m/+3m/+5m/+10m respectively, consistent with the delay itself.

### 9. MFE/MAE while waiting

| Delay | % better than baseline | % suffering adverse exceeding own baseline MAE |
|---|---|---|
| +1m | 52.6% | 43.8% |
| +3m | 56.6% | 57.9% |
| +5m | 62.8% | 65.5% |
| +10m | 60.2% | **72.7%** |

**By +10m, nearly 3 of 4 trades endure an adverse excursion worse than what they experienced
under the current immediate exit** -- the price of the aggregate improvement is a substantial
increase in how often trades go through real, growing drawdown while waiting.

### 10. Shape of the response

| Delay | Incremental A P&L | Median delta | P25 | P75 |
|---|---|---|---|---|
| +1m | +81,199.27 | 194.88 | -1,429.10 | 1,786.38 |
| +3m | +362,505.85 | 747.03 | -1,948.78 | 3,605.25 |
| +5m | +576,222.18 | 1,461.58 | -2,176.14 | 4,319.80 |
| +10m | +831,090.27 | 1,753.91 | -2,663.34 | 6,885.69 |

**Pooled shape**: gradual, still-rising improvement through +10m, with no flattening or reversal
point found in this tested range. **This pooled shape is misleading on its own** -- Part 6 shows
one of the three days already reverses hard within the same range.

### Answers to the required questions

- **Q1 (Does delaying consistently improve A's outcome?)**: **Pooled, yes, growing through
  +10m. Day-by-day, no** -- 09-08 improves through +5m then reverses to worse-than-baseline at
  +10m.
- **Q2 (At what horizon does the benefit flatten/reverse?)**: **Not found in the pooled data
  within 1-10 minutes** -- still rising at +10m. **But a real reversal exists on 09-08 between
  +5m and +10m**, which the pooled curve hides entirely.
- **Q3 (Broad or outlier-driven?)**: **Both.** Median delta is positive at every horizon (a
  genuine majority-level effect), but the mean-median gap widens with delay and the extreme
  min/max values (and 09-15's outsized PF of 6.10 at +10m) show meaningful outlier influence too.
- **Q4 (Exists on all 3 days?)**: **No** -- 2 of 3 days show clean, monotonic improvement; the
  third shows improvement then reversal.
- **Q5 (Exists across session buckets?)**: **Mostly, not uniformly** -- morning and late
  afternoon are consistently positive; the 12:00-14:00 window is weak or negative.
- **Q6 (Downside exposure introduced?)**: **Substantial and growing** -- WinToLoss transitions
  increase, the worst single-trade loss grows to -44,952 at +10m, and the fraction of trades
  suffering adverse excursion worse than their own baseline MAE rises to 72.7% by +10m.
- **Q7 (Does B remain unchanged?)**: **Yes, exactly, by construction.**
- **Q8 (Enough evidence to justify a specific next experiment?)**: **Yes, but not a delay
  choice** -- the data justifies investigating WHY 09-08 and the 12:00-14:00 window behave
  differently before any delay-based rule is considered.

### Recommended next experiment (not implemented)

Investigate the 09-08 day-specific reversal and the 12:00-14:00 session-specific weakness
directly -- e.g. what was different about the trades/underlying conditions on 09-08 between +5m
and +10m that caused the reversal -- before testing any specific delay value further. This is a
diagnostic follow-up, not a parameter search; no delay is recommended as a next step.

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-a-exit-delay-sensitivity 2026-09-01 2026-09-23 --diagOut=vc-a-delay-sensitivity.csv
```

## A-Continuation Structural Diagnostic (2026-09-24)

**Question**: can observable signal-time conditions distinguish Pattern A trades likely to
continue after the opposite-pattern exit from those likely to reverse? Diagnostic only -- no
model, no filter, no strategy change. Features (known at/before the signal) and outcome (the
price path strictly after the actual baseline exit) are kept in separate fields throughout.

### Implementation

Two new pure helpers in `DelayedExitCounterfactual.cs`: `ClassifyContinuationVsReversal` (a
zero-threshold sign split of the post-exit return -- no magnitude cutoff invented) and
`MinutesSinceSessionOpen`. Everything else reuses existing infrastructure
(`OptionValueDecomposition`, `EpisodeTransitionAnalysis.ClassifyCePeDivergence`,
`ForwardValidationAnalysis.ComputeTerciles`, `ConditionalMovementAnalysis.ClassifyTercileBucket`,
`Vc0DteBehaviorSummary.SessionBucket`) unmodified. `dotnet build`: 0 warnings/errors. `dotnet
test`: **1012/1012 passing** (1006 baseline + 6 new).

### A. Core outcome distribution (post-exit PE return, Rs)

| Horizon | Mean | Median | P25 | P75 | Min | Max |
|---|---|---|---|---|---|---|
| +1m | 0.58 | 0.55 | -2.00 | 3.00 | -18.05 | 19.80 |
| +3m | 2.02 | 1.45 | -2.60 | 5.50 | -14.40 | 42.15 |
| +5m | 3.10 | 2.45 | -3.15 | 6.90 | -30.65 | 55.35 |
| +10m | 4.28 | 2.80 | -3.95 | 10.85 | -69.30 | 61.25 |
| +15m | 5.39 | 2.25 | -3.80 | 14.30 | -72.75 | 67.45 |

Continuation/Reversal counts hover in the 54-63% range at every horizon (e.g. +10m:
Continuation=186, Reversal=118 = 61.2%) -- a modest overall tilt toward continuation, consistent
with the earlier exit-asymmetry finding, but far from a clean split.

### B. Feature comparison (Continuation vs Reversal, at +10m)

**Nearly every examined feature shows heavily overlapping distributions between the two
groups** -- CE change1 (-0.51 vs -0.56), PE change1 (0.88 vs 0.95), futures change1 (3.96 vs
4.17), PE entry price (121.26 vs 122.80), strike distance (67.20 vs 66.53, essentially
identical), moneyness (65.70 vs 64.13), extrinsic at entry (55.49 vs 58.64). **CE/PE divergence
has zero discriminating power** -- both groups are 100% "Divergent" by construction (Pattern A's
own definition). The only features showing any separation: **minutes since session open**
(Continuation median 132.6 vs Reversal median 178.4 -- Reversal trades occur somewhat later in
the day) and **prior-movement tercile** (Low=54.9% continuation vs Mid=65.0%/High=63.7% -- a real
but non-monotonic gap, Low is weaker, Mid and High are not cleanly ordered).

### Day-by-day validation

| Date | n | ContinuationRate | Within-day Cont vs Rev separation? |
|---|---|---|---|
| 09-08 | 83 | 53.0% | None (extrinsic 102.37 vs 98.60; moneyness 21.55 vs 26.46; strike dist 18.18 vs 24.36 -- all close) |
| 09-15 | 109 | 67.0% | None (extrinsic 55.30 vs 54.21; moneyness 63.96 vs 67.86; strike dist 64.38 vs 72.22) |
| 09-22 | 112 | 61.6% | None (extrinsic 25.80 vs 26.11; moneyness 95.69 vs 95.16; strike dist 101.45 vs 100.00) |

**No feature separates Continuation from Reversal WITHIN any single day either** -- the pooled
non-finding in Part B replicates on every day individually. 09-08 does have the lowest
continuation rate of the three, consistent with its sensitivity-experiment behaviour.

### Session analysis

| Session | n | ContinuationRate | AvgExtrinsic | AvgMoneyness |
|---|---|---|---|---|
| 09:15-10:00 | 75 | 69.3% | 62.65 | 53.73 |
| 10:00-11:00 | 45 | 68.9% | 54.69 | 65.75 |
| 11:00-12:00 | 38 | 55.3% | 54.11 | 71.34 |
| 12:00-13:00 | 30 | **46.7%** | 64.39 | 59.21 |
| 13:00-14:00 | 46 | 52.2% | 54.75 | 69.93 |
| 14:00-15:00 | 70 | 62.9% | 51.06 | 72.79 |

**This is the clearest, most consistent structural signal found in the whole diagnostic.** The
12:00-14:00 window shows the lowest continuation rates (46.7%/52.2%) against 62.9-69.3% elsewhere
-- but its AvgExtrinsic/AvgMoneyness are NOT unusual relative to the other buckets (12:00-13:00's
extrinsic, 64.39, is actually on the higher side yet has the lowest continuation rate). **The
session effect does not appear to be mediated by contract selection** -- it looks like a genuine,
separate time-of-day phenomenon.

### C. 09-08 investigation

| Variable | 09-08 | Other 2 days |
|---|---|---|
| Futures change1 at signal | mean 2.93 | mean 4.47 |
| CE change1 at signal | mean -0.22 | mean -0.64 |
| Moneyness | mean 23.86, median 20.00 | mean 80.58, median 83.50 |
| Extrinsic at entry | mean 100.60, median 101.25 | mean 40.23, median 36.00 |
| Strike distance | mean 21.08, **median 0.00** | mean 84.16, **median 100.00** |

**Yes, a large, repeatable, explainable structural difference exists.** 09-08's Pattern A trades
sit far closer to ATM (median strike distance exactly 0 vs 100 for the other two days) with far
more extrinsic cushion (2.5x) and a modestly weaker underlying-move magnitude at signal. This is
consistent with, and explained by, the SAME band-execution mechanic already documented (the
₹100-150 band interacts with wherever the underlying happens to be trading that particular
week) -- not an unexplainable one-off. It is the structural mirror image of what made 09-08's
delay-sensitivity result reverse at +10m: its trades were closer to genuine options (more
extrinsic cushion) rather than the deep-ITM, leveraged-futures-like contracts typical of the
other two days.

### D. Contract-selection analysis

| Extrinsic tercile | n | ContinuationRate |
|---|---|---|
| Low | 102 | 59.8% |
| Mid | 100 | **67.0%** |
| High | 102 | 56.9% |

**Non-monotonic -- extrinsic level does not cleanly predict continuation on its own.** Despite
09-08 (a high-extrinsic day) showing a lower continuation rate, the pooled extrinsic-tercile
breakdown does not reproduce "more extrinsic = more/less continuation" as a general rule. The
09-08 effect is more likely tied to something specific to that day/week's conditions as a whole
than to extrinsic composition acting as an independent predictor.

### E. Consistency matrix

| Feature | Pooled signal | 09-08 | 09-15 | 09-22 | Assessment |
|---|---|---|---|---|---|
| CE/PE change1 at signal | none | none | none | none | consistent (no predictive signal) |
| Futures change1 at signal | none | none | none | none | consistent (no predictive signal) |
| PE entry price | none | none | none | none | consistent (no predictive signal) |
| Extrinsic at entry | none (non-monotonic tercile) | none | none | none | consistent (no predictive signal) |
| Strike distance from ATM | none (pooled) | Rev slightly farther | Rev slightly farther | Cont slightly farther | mixed -- sign not stable |
| Moneyness | none (pooled) | Rev slightly higher | Rev slightly higher | ~equal | weak, same direction 2/3 days, tiny effect |
| Prior-movement tercile | weak (Low lower) | insufficient sample per-day | insufficient sample per-day | insufficient sample per-day | pooled-only, weak |
| Minutes since session open | mild (Reversal later) | insufficient sample per-day | insufficient sample per-day | insufficient sample per-day | pooled-only, weak |
| CE/PE divergence label | none (100% Divergent, both groups) | same | same | same | consistent (trivial, no signal by construction) |
| **Session bucket** | **real (12:00-14:00 weaker)** | present in mix | present in mix | present in mix | **consistent across days -- most promising finding** |

### Answers to the required questions

- **Q1 (Is continuation/reversal distinguishable from signal-time information?)**: **Weakly at
  best.** Almost every examined feature overlaps heavily between the two groups, both pooled and
  within every individual day.
- **Q2 (Most consistent structural variables?)**: **Session bucket** (12:00-14:00 shows a real,
  reproducible lower continuation rate) is the strongest finding. Prior-movement tercile and
  minutes-since-open show weak, pooled-only effects.
- **Q3 (Does 09-08 have a repeatable structural explanation?)**: **Yes.** A large, explainable
  difference in contract selection (much closer to ATM, much more extrinsic cushion) driven by
  the same band-execution mechanic already documented -- not an unexplainable anomaly.
- **Q4 (Does 12:00-14:00 weakness have a repeatable structural explanation?)**: **Not via
  contract selection.** Extrinsic/moneyness in that window are unremarkable; the effect appears
  to be a genuine, separate time-of-day phenomenon, unexplained by the variables examined here.
- **Q5 (Does moneyness/extrinsic explain continuation?)**: **No, not cleanly** -- non-monotonic
  pooled relationship, no within-day separation on any of the 3 days.
- **Q6 (Enough evidence for one narrow conditional experiment?)**: **Yes, cautiously** -- but the
  strongest candidate is session-time-related, not contract-selection-related.
- **Q7 (Smallest next experiment, not implemented)**: a targeted, still-descriptive investigation
  of what specifically differs about the 12:00-14:00 window beyond contract selection (e.g. event-
  bar frequency/volume characteristics in that window across the 3 days) -- before any
  session-based rule is ever considered.

### Build / test

`dotnet build NiftySignal.slnx`: 0 warnings/errors. `dotnet test NiftySignal.slnx`: **1012/1012
passing** (1006 baseline + 6 new).

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-a-continuation-diagnostic 2026-09-01 2026-09-23 --diagOut=vc-a-continuation.csv
```

## 12:00-14:00 Market-State Diagnostic (2026-09-24)

**Question**: what is different about the market state around Pattern A signals occurring in
12:00-14:00 compared with signals outside that window? Diagnostic only -- no filter, no strategy
change. Causal caution observed throughout: findings are reported as directional associations,
never as "X causes the weakness."

### Implementation

Two new pure helpers (`ComputeVolumeRate`, `ComputeCePeDivergenceMagnitude`, plus
`ClassifyPriorStateClass`) in new `MarketStateDiagnostics.cs`, all tested. **CVD-net, OFI-net,
depth imbalance, and top-of-book imbalance are reported as UNAVAILABLE** -- they exist elsewhere
in this codebase (`CvdProxySumPopulator`, `DepthImbalanceSumPopulator`) but on a structurally
different bar clock from an earlier, separate score-candidate research track; mapping them onto
the frozen relationship's own event boundaries would be an approximate cross-framework
reconstruction, which was explicitly avoided rather than attempted. Everything else reuses
existing fields (`FutureEventBar.Vwap/High/Low`, `RelationshipObservation.FuturesEventDurationMs`/
`CeChange1`/`PeChange1`, `EpisodeAnalysis`) unmodified. `dotnet build`: 0 warnings/errors. `dotnet
test`: **1020/1020 passing** (1012 baseline + 8 new).

### 1. Group1 (12:00-14:00) vs Group2 (all other periods), pooled

| Variable | Group1 (n=76) | Group2 (n=228) |
|---|---|---|
| Futures event-bar return | mean 4.80, median 4.00 | mean 3.79, median 2.50 |
| Futures range (High-Low) | mean 8.01, median 8.90 | mean 7.01, median 6.90 |
| Futures event duration (ms) | mean 22,921, median 15,000 | mean 15,421, median 10,000 |
| Volume rate (contracts/sec) | mean 262.22, median 100.00 | mean 365.87, median 170.63 |
| Close - VWAP | mean 1.15, median 0.32 | mean 0.53, median 0.24 |
| Minutes since previous A | mean 3.67, median 2.87 | mean 2.21, median 1.58 |
| Minutes since previous B | mean 1.44, median 0.73 | mean 0.79, median 0.47 |
| Minutes since previous transition | mean 0.91, median 0.68 | mean 0.52, median 0.35 |
| CE/PE divergence magnitude | mean 1.36, median 0.98 | mean 1.46, median 1.11 |
| Episode length | 1.04 | 1.09 |
| Prior state: followed PatternB | 32.9% | 31.1% |

### 2. Detailed session buckets

| Session | n | AvgRange | AvgVolumeRate | AvgEpisodeLen | AvgMinSincePrevA |
|---|---|---|---|---|---|
| 09:15-10:00 | 75 | 5.63 | 429.33 | 1.04 | 1.51 |
| 10:00-11:00 | 45 | 6.95 | 195.78 | 1.16 | 2.76 |
| 11:00-12:00 | 38 | 7.53 | 88.71 | 1.11 | 3.30 |
| 12:00-13:00 | 30 | 8.08 | 240.12 | 1.00 | 4.61 |
| 13:00-14:00 | 46 | 7.97 | 276.63 | 1.07 | 3.06 |
| 14:00-15:00 | 70 | 8.26 | 557.68 | 1.10 | 1.98 |

The two middle buckets (12:00-13:00, 13:00-14:00) are reported separately throughout, never
silently merged, per the task's own instruction.

### 3. Day-wise comparison (Group1 vs Group2)

| Day | Duration (G1 vs G2, ms) | Range (G1 vs G2) | Event-bar return (G1 vs G2) | Volume rate (G1 vs G2) |
|---|---|---|---|---|
| 09-08 | 23,000 vs 21,224 | 6.90 vs 5.49 | 3.60 vs 2.64 | 240.60 vs 225.96 (reversed direction) |
| 09-15 | 25,667 vs 12,082 | 8.42 vs 7.60 | 5.77 vs 4.16 | 307.41 vs 564.97 |
| 09-22 | 20,407 vs 14,800 | 8.67 vs 7.46 | 5.06 vs 4.21 | 242.07 vs 262.24 |

**Consistent across all 3 days** (same direction every time): longer event-bar duration, wider
range, larger event-bar return, longer gaps since the previous A/B signal and since the previous
relationship transition, and price sitting somewhat further above VWAP. **Not consistent**:
volume rate (reverses sign on 09-08) and CE/PE divergence magnitude (mixed sign across days).
Episode length and "followed PatternB vs Other" show no meaningful difference on any day.

### 6. Relationship-transition analysis (before A)

"Followed PatternB" vs "followed a neutral/other state" is essentially identical between the two
groups (32.9% vs 31.1%) -- what precedes A is not meaningfully different in 12:00-14:00. What IS
different is HOW LONG that prior regime had been running: minutes-since-previous-transition is
roughly 1.75x longer in Group1 (0.91 vs 0.52 pooled), consistent across all 3 days.

### 5/9. Outcome comparison -- and the critical caveat

| | Group1 (12-14) | Group2 (other) |
|---|---|---|
| +1m / +3m / +5m / +10m / +15m median | -0.20 / 0.30 / 1.45 / 0.15 / 0.85 | 0.75 / 1.85 / 2.70 / 3.65 / 2.85 |
| MFE after exit (mean) | 11.26 | **18.03** |
| MAE after exit (mean) | 8.90 | 9.59 |
| Continuation rate (+10m) | **50.0%** | **64.9%** |

Group1's weaker outcome is driven by **less favourable movement developing** (MFE-after-exit
much lower), not more adverse movement (MAE-after-exit is nearly identical) -- pooled, this
connects cleanly to the market-state differences above.

**But day-by-day, the connection breaks down on one of the three days**:

| Day | ContinuationRate G1 | ContinuationRate G2 | Gap |
|---|---|---|---|
| 09-08 | 40.0% | 58.6% | 18.6pp |
| **09-15** | **66.7%** | **67.1%** | **0.4pp -- essentially none** |
| 09-22 | 44.4% | 67.1% | 22.7pp |

**09-15 shows the STRONGEST market-state divergence of all three days (the biggest duration gap,
25,667ms vs 12,082ms) yet shows NO continuation-rate gap at all.** The market-state difference
and the outcome difference do not reliably travel together -- this is reported plainly as an
unresolved inconsistency, not smoothed over.

### 7. Is this simply lower activity?

**No, not in the simple sense.** Volume rate itself is inconsistent across days (reversed on
09-08), and critically, the **range is WIDER, not narrower**, during 12:00-14:00 on all 3 days --
a genuinely "quiet" market would show a narrower range, not a wider one. The more accurate
description: bars take longer to accumulate the 1300-contract threshold, but when they complete,
they show a larger price move and a wider intrabar range, alongside less frequent signals and
longer-persisting regimes -- a "slower-to-trigger but choppier" pattern, not simply reduced
activity.

### 8. Cross-day consistency table

| Variable | 09-08 | 09-15 | 09-22 | Overall |
|---|---|---|---|---|
| Event-bar duration (longer in G1) | present (small) | present (large) | present (large) | consistent |
| Futures range (wider in G1) | present | present | present | consistent |
| Event-bar return (larger in G1) | present | present | present | consistent |
| Minutes since previous A/B (longer in G1) | present | present | present | consistent |
| Minutes since previous transition (longer in G1) | present | present | present | consistent |
| Close - VWAP (higher in G1) | present | present | present | consistent |
| Volume rate (lower in G1) | absent/reversed | present | present | mixed |
| CE/PE divergence magnitude | ~equal | higher in G1 | lower in G1 | mixed |
| Episode length | ~equal | ~equal | ~equal | absent |
| Prior state class (followed B vs Other) | ~equal | ~equal | ~equal | absent |
| **Continuation-rate gap (the outcome itself)** | present (18.6pp) | **absent** (0.4pp) | present (22.7pp) | **mixed** |

### Answers to the required questions

- **Q1 (Is 12:00-14:00 associated with a materially different market state?)**: **Yes** --
  several variables differ consistently in the same direction across all 3 days.
- **Q2 (Which variables differ consistently across all 3 days?)**: Event-bar duration, futures
  range, event-bar return magnitude, minutes since previous A/B signal, minutes since previous
  relationship transition, and Close-VWAP. Volume rate and CE/PE divergence magnitude are NOT
  consistent. Episode length and prior-state class show no difference at all.
- **Q3 (Are those variables also associated with weaker A continuation?)**: **Pooled, yes**
  (lower continuation rate, much lower MFE-after-exit). **Day-by-day, not reliably** -- 09-15
  shows the strongest market-state divergence of the three days yet no continuation-rate gap.
  The market-state difference and the outcome difference are directionally associated pooled,
  but do not travel together consistently.
- **Q4 (Simply lower activity, or a different regime?)**: **Not simply lower activity** -- the
  wider range (not narrower) rules out a plain "quiet market" story. Best described as
  slower-to-trigger but choppier bars, with less frequent signals and longer-persisting regimes.
- **Q5 (Does the evidence justify one narrow conditional experiment?)**: **Yes, but the
  experiment should target the unresolved 09-15 inconsistency, not the market-state difference
  itself** (which is already reasonably well established).
- **Q6 (Smallest next experiment, not implemented)**: investigate why 09-15's 12:00-14:00 window
  did not show the continuation weakness despite having the strongest market-state signature --
  e.g. whether some other conditioning factor (day-level volatility regime, or the extrinsic/
  moneyness characteristics from the earlier continuation diagnostic) distinguishes 09-15's
  12-14 trades from 09-08's/09-22's -- before any session-based rule is ever considered.

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-a-12to14-market-state 2026-09-01 2026-09-23 --diagOut=vc-a-12to14.csv
```

## Pattern A DTE Validation (2026-09-24)

**Question**: does Pattern A's underlying reversal, PE response, and intrinsic/extrinsic
composition survive outside 0-DTE? Pure relationship-level validation -- no trade simulator, no
₹100-150 band, no exit logic. Pattern B out of scope entirely.

### Implementation

Reuses the exact (date, expiry) pair discovery from the DTE-Expansion experiment, and the
completely unmodified `UnderlyingOptionRelationshipRecorder`/`EpisodeAnalysis`/
`OptionValueDecomposition`/tercile-control infrastructure. Pattern A signal = episode first
event (per the frozen methodology, matching the earlier Transition Analysis convention); control
stays at the existing per-event granularity, unmodified. One new pure helper,
`BroadSessionClassifier` (Morning/Midday/Afternoon), tested. `dotnet build`: 0 warnings/errors.
`dotnet test`: **1026/1026 passing** (1020 baseline + 6 new).

### 4. DTE / session inventory (critical independence check)

| Bucket | Unique sessions | Sessions |
|---|---|---|
| 0 | 3 | 09-08, 09-15, 09-22 |
| 1 | 1 (insufficient) | 09-21 |
| 4-6 | 8 | 09-04, 09, 10, 11, 16, 17, 18, 23 |
| 7-8 | 4 | 09-08, 09-15, 09-21, 09-22 |
| 11-13 | 8 | same 8 as 4-6 |

**Every single session contributes to exactly 2 buckets** (front-week and back-week chain views)
-- 12 unique calendar sessions total, not 24. Explicitly re-confirmed, not glossed over.

### 5. Pattern A underlying (futures) response -- remarkably stable across DTE

| Bucket | n | +10 median | +10 %negative | vs Control +10 |
|---|---|---|---|---|
| 0 | 540 | -0.0146% | 67.0% | -0.0091pp |
| 1 | 105 | -0.0090% | 55.8% | -0.0087pp |
| 4-6 | 1086 | -0.0093% | 59.3% | -0.0093pp |
| 7-8 | 575 | -0.0153% | 67.8% | -0.0110pp |
| 11-13 | 947 | -0.0079% | 58.2% | -0.0078pp |

**The underlying reversal survives outside 0-DTE essentially unchanged** -- direction, rough
magnitude, and the control-beating margin are all remarkably stable across every DTE bucket
tested.

### 6. Pattern A -> PE option response -- a clean DTE-dependent decay

| Bucket | n | PE +3 median | PE +5 median | PE +10 median |
|---|---|---|---|---|
| 0 | 460/399/368 | 0.5803% | 0.4267% | **0.6871%** |
| 7-8 | 454/436/396 | 0.1538% | 0.1054% | 0.2430% |
| 4-6 | 851/825/752 | 0.1006% | 0.0478% | 0.0997% |
| 11-13 | 747/719/639 | 0.0236% | -0.0428% | **0.0031%** |

**Unlike the underlying, PE's own response monotonically weakens with DTE** -- from a strong
+0.69% median at 0-DTE down to essentially flat (+0.003%) at 11-13 DTE. Bucket 1 (single
session, flagged insufficient) shows the response turning outright negative at +5/+10, but
cannot be generalized from one day.

### 7. PE intrinsic/extrinsic decomposition -- explains the decay mechanistically

| Bucket | +10 Intrinsic (mean) | +10 Extrinsic (mean) | +10 Net (mean) |
|---|---|---|---|
| 0 | 1.2274 | -0.6773 | **+0.5502** |
| 7-8 | 1.4859 | -1.1990 | +0.2868 |
| 4-6 | 0.9047 | -0.9768 | **-0.0722** |
| 11-13 | 1.1505 | -1.3747 | **-0.2241** |

**Intrinsic gain stays roughly stable across DTE (0.90-1.49); extrinsic decay grows larger in
the deeper buckets (-0.68 at 0-DTE to -1.37 at 11-13)** -- this growing extrinsic decay is what
flips the net PE response from clearly positive (0-DTE) to flat/negative (4-6, 11-13). The
decomposition does NOT remain similar across DTE -- the mechanism shifts.

### 8. Matched-control comparison -- the relationship's edge over control survives everywhere

| Bucket | PE Pattern +10 median | PE Control +10 median | diff |
|---|---|---|---|
| 0 | 0.6638% | 0.0342% | +0.6297pp |
| 7-8 | 0.2305% | -0.0196% | +0.2501pp |
| 4-6 | 0.0891% | -0.1963% | +0.2854pp |
| 11-13 | 0.0031% | -0.1580% | +0.1611pp |

**Pattern A beats its matched control on PE response in every single bucket** -- even in 11-13
DTE, where the ABSOLUTE PE response is essentially flat, the RELATIVE edge over a comparable
control is still clearly positive (+0.16pp). The relationship retains real information beyond
the underlying's own move at every DTE tested, even as the absolute payoff shrinks toward zero.

### 9. Session interaction -- not a clean cross-DTE pattern

| Bucket | Morning PEPos% | Midday PEPos% | Afternoon PEPos% |
|---|---|---|---|
| 0 | 56.1% | 50.6% | 60.3% |
| 7-8 | 52.9% | 50.6% | 58.6% |
| 4-6 | 51.6% | 53.5% | 50.5% |
| 11-13 | 48.9% | 51.9% | 50.7% |

Midday is weakest in buckets 0 and 7-8 (both share the same 3 core sessions), but is NOT
noticeably weak in 4-6/11-13 -- the 12:00-14:00 weakness found earlier does not cleanly
replicate across every DTE bucket.

### 10. Day-level evidence

Bucket 0: PE+10 medians are 0.11% (09-08), 1.03% (09-15), 1.06% (09-22) -- 09-08 is consistently
the weakest day, matching every prior finding. Buckets 4-6/11-13 (8 sessions each) show REAL
day-to-day sign flips -- roughly half the days show a NEGATIVE PE+10 median even though the
bucket's own pooled median is positive or near-zero (e.g. 4-6: 09-11=-0.11%, 09-17=-0.73%,
09-23=-0.25%). The "positive PE response" finding is NOT uniform across days once DTE deepens.

### 11. DTE comparison table

| DTE bucket | Unique sessions | A observations | PE+3 median | PE+5 median | PE+10 median | Futures+10 median | Assessment |
|---|---|---|---|---|---|---|---|
| 0 | 3 | 540 | 0.5803% | 0.4267% | 0.6871% | -0.0146% | repeated across independent sessions |
| 1 | 1 | 105 | 0.3142% | -0.3025% | -0.1087% | -0.0085% | insufficient |
| 4-6 | 8 | 1086 | 0.1006% | 0.0478% | 0.0997% | -0.0093% | repeated across independent sessions |
| 7-8 | 4 | 575 | 0.1538% | 0.1054% | 0.2430% | -0.0151% | repeated across independent sessions |
| 11-13 | 8 | 947 | 0.0236% | -0.0428% | 0.0031% | -0.0077% | repeated across independent sessions |

### Answers to the required questions

- **Q1 (Does the underlying reversal survive outside 0-DTE?)**: **Yes, robustly** -- remarkably
  stable direction, magnitude, and control-beating margin across every DTE bucket.
- **Q2 (Does the PE response survive outside 0-DTE?)**: **Only partially.** Direction stays
  positive in 3 of 4 sufficient buckets but the magnitude decays cleanly with DTE, reaching
  essentially flat (+0.003%) at 11-13 DTE.
- **Q3 (Does PE remain positive relative to the matched control?)**: **Yes, in every single
  bucket** -- even where the absolute PE response is flat, the relative edge over control stays
  positive.
- **Q4 (Does the intrinsic/extrinsic decomposition remain similar across DTE?)**: **No.**
  Intrinsic gain is roughly stable; extrinsic decay grows larger with DTE, and that growth is
  what flips the net response from positive to flat/negative.
- **Q5 (Is the 0-DTE A exit asymmetry an underlying-relationship effect or an option-surface/DTE
  effect?)**: **This diagnostic cannot fully answer it** -- the exit-asymmetry work used a
  different methodology (the trade simulator/band) never re-run at other DTEs here. What CAN be
  said: the underlying relationship stays stable across DTE while the option's own monetization
  clearly varies with DTE, which makes an option-surface/DTE-specific explanation more
  plausible than a pure underlying-relationship one -- stated as a reasonable inference from
  consistent evidence, not a proven fact.
- **Q6 (Does 12:00-14:00 behaviour appear consistently across DTE?)**: **No, not cleanly** --
  present in 2 of 4 sufficient buckets, absent/reversed in the other 2.
- **Q7 (Smallest next research step, not implemented)**: a focused decomposition analysis of
  WHY extrinsic decay grows with DTE -- e.g. whether it's simply proportional to the larger
  absolute extrinsic value present at signal time (a scale effect) or a genuinely different
  decay rate -- before any DTE-aware trading rule is considered.

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-a-dte-validation 2026-09-01 2026-09-23 --out=vc-a-dte-validation.csv
```

## Pattern A + Crossover -- Incremental Edge Discovery (2026-09-24)

### Purpose

Pattern A's underlying reversal and PE response survive across DTE (previous section), but the
research goal has shifted from "does Pattern A exist" to "what observable information can
*condition* Pattern A so the resulting option-buying opportunity has a better, more survivable
distribution?" A futures price crossover is the first candidate conditioning signal tested this
way. This experiment does **not** build any trade simulator, filter, exit rule, or composite
score -- it is purely diagnostic: does knowing the crossover *state* at a Pattern A signal improve
the conditional distribution of the subsequent underlying move and PE response, versus Pattern A
alone and versus the existing tercile-matched control?

### What crossover infrastructure already exists (mandatory pre-check, per task instructions)

`PriceCrossoverEngine` (`NiftySignal.VolumeBarData/PriceCrossoverCalculator.cs`) is a pure,
generic, **event-count-based** (not time-based) rolling-window fast/slow moving-average crossover
detector: `PriceCrossoverEngine(int fastBars, int slowBars, MaType fastType = Sma, MaType
slowType = Sma)`, fed one raw price per event bar via `Observe(double? price, double
thresholdFraction) -> Step(FastMa, SlowMa, DiffFraction, CrossedUp, CrossedDown)`. Both MAs are
null until the window has `slowBars` real readings (no fabricated warm-up value). It is **already
used internally, unmodified**, inside `UnderlyingOptionRelationshipRecorder` at a FIXED
FastBars=3/SlowBars=10 for futures/spot/CE/PE, feeding `RelationshipObservation.FuturesCrossoverState`
etc. -- that internal usage is frozen and untouched by this experiment. This experiment
instantiates **separate, additional** `PriceCrossoverEngine` instances (never modifying the
recorder's own) to test other fast/slow combinations and other event-bar-size clocks. No new
crossover formula was written anywhere in this task; the only new code is
`CrossoverResolutionDiagnostics.cs` (`AlignStepAtOrBefore`, `ClassifyConfirmation`,
`IsFreshCrossDown`), a thin diagnostic layer for (a) aligning an alternate event-bar clock's own
crossover-state sequence to the frozen signal's timestamp without look-ahead, and (b) a plain
3-way classification of whether a crossover state confirms Pattern A's expected direction.

**Fresh vs. state**: this experiment classifies by crossover *state* (`DiffFraction < 0` =
bearish, "Confirms" Pattern A's expected downward reversal) rather than by a "fresh crossing in
the last N bars" definition, since the task's own Group2/Group3 split is a state split. "Fresh"
(`IsFreshCrossDown`) exists in the helper but was not the primary classification used for the
Group1/2/3 comparison, to avoid doubling an already-large combinatorial grid; it is available for
a future, narrower follow-up if the state-based result here motivates it.

### Methodology (frozen, unmodified)

Same (date, expiry) pair discovery, DTE buckets, episode-first-event Pattern A signal convention,
option-value decomposition, and tercile-matched control construction as the DTE-Validation
experiment above -- copied verbatim as the base, reusing
`UnderlyingOptionRelationshipRecorder.RecordAsync`, `EpisodeAnalysis.DetectEpisodes`,
`OptionValueDecomposition`, `DteBucketClassifier`, and `BroadSessionClassifier` unmodified.
**SignalClock stays fixed at the frozen 1300-contract futures event-bar clock for every episode's
identity and timing** -- only the *crossover-classification input* varies across the experiment's
three layers:

1. **PRIMARY** -- one representative fast/slow combo, **(5, 20)** (a middle point of the grid
   below, explicitly *not* chosen because it performed best), classified on the SAME frozen
   1300-contract clock as the signal (no cross-clock alignment needed here at all). Full
   Group1(All)/Group2(Confirms)/Group3(DoesNotConfirm) comparison: underlying and PE response at
   +1/+3/+5/+10 events (mean/median/P25/P75/%negative or %positive), intrinsic/extrinsic
   decomposition, matched-control comparison, session interaction (3 broad groups), and
   mandatory day-level breakdown, for every DTE bucket with data (0, 1, 4-6, 7-8, 11-13).
2. **GRID** -- the same classification repeated for all 9 valid fast/slow combinations
   (fast in {3,5,8}, slow in {10,20,40}) per DTE bucket, reporting n, PE+10 median, %positive, the
   incremental delta vs. Group1's own median, and a **descriptive-only** day-consistency label
   (broadly consistent / mostly consistent / mixed / insufficient sample) -- explicitly no
   ranking, scoring, or "best" declaration anywhere.
3. **EVENT-BAR-SIZE DIAGNOSTIC** -- the representative (5, 20) combo re-run with the crossover
   fed from an **alternate** futures event-bar clock (650 and 2600 contracts, vs. the frozen
   1300), built via the same unmodified `FutureEventBarBuilder.BuildDayAsync` at a different
   threshold. Since the alternate clock's bars do not line up in time with the frozen signal
   clock, each Pattern A signal's crossover state is resolved via
   `CrossoverResolutionDiagnostics.AlignStepAtOrBefore`: replay the alternate clock in
   chronological order and take its last bar whose `EndTimestamp` is at or before the signal's own
   timestamp. This never reads a bar that completes after the signal, so it introduces no
   look-ahead -- it is a principled alignment rule, not an approximation, and no case requiring
   a stop-and-report was encountered.

**Independence note (frozen convention, carried over)**: the 12 real local-mirror calendar
sessions each contribute to exactly 2 DTE buckets via front/back-week chain views. Day-level
counts below (e.g. "8 sessions" for bucket 4-6) are real distinct calendar dates, but a date
appearing in two different DTE buckets is never treated as two independent confirmations of the
same underlying-market day.

### Results

**PRIMARY, representative combo (5, 20), frozen clock -- PE+10 median by group:**

| DTE bucket | sessions | Group1 (All) | Group2 (Confirms) | Group3 (DoesNotConfirm) | Control |
|---|---|---|---|---|---|
| 0 | 3 | 0.6406% (n=368) | 0.4273% (n=179) | 0.9165% (n=189) | 0.0342% |
| 1 | 1 (insufficient) | -0.1687% (n=74) | -1.4632% (n=38) | -0.0176% (n=36) | -0.2275% |
| 4-6 | 8 | 0.0785% (n=752) | 0.0202% (n=374) | 0.2129% (n=373) | -0.1963% |
| 7-8 | 4 | 0.2181% (n=396) | 0.1824% (n=200) | 0.2181% (n=195) | -0.0196% |
| 11-13 | 8 | 0.0031% (n=639) | -0.1309% (n=325) | 0.1000% (n=309) | -0.1580% |

In **every single DTE bucket**, Group2 (crossover confirms) is flat-to-worse than Group1 (all
Pattern A), and in 4 of 5 buckets Group3 (crossover does NOT confirm) is actually *better* than
Group1. Pattern A's control-beating margin survives in Group2 in 4 of 5 buckets (stays positive,
just smaller), but shrinks materially at DTE 4-6 and especially 11-13 (0.1611pp -> 0.0271pp), and
turns negative at DTE=1 (single session, not trustworthy on its own).

**Underlying (futures) response, +10 events, mean, by group -- every bucket:**

| DTE bucket | Group1 | Group2 (Confirms) | Group3 (DoesNotConfirm) |
|---|---|---|---|
| 0 | -0.0162% | -0.0131% | -0.0193% |
| 4-6 | -0.0110% | -0.0095% | -0.0124% |
| 7-8 | -0.0162% | -0.0135% | -0.0183% |
| 11-13 | -0.0101% | -0.0090% | -0.0112% |

This is the most consistent single finding in the whole experiment: **Group2's underlying
reversal is *weaker* (smaller-magnitude) than Group1's or Group3's, in all four buckets with
enough data** -- the opposite of the naive "confirms -> stronger move" expectation. A plausible
(not proven) mechanical reading: by the time a bearish crossover *state* has already formed, a
meaningful part of the downward move that fed that state has already happened, leaving less room
for further continuation -- an exhaustion-flavored effect rather than a confirmation one.

**Intrinsic/extrinsic decomposition (mean, +10 events) -- no consistent story:** Group2 shows
slightly higher mean intrinsic gain than Group1 at DTE 0 and 4-6, but that is offset (4-6) or more
than offset (11-13, where intrinsic is also lower) by larger extrinsic decay; at DTE 7-8 the two
groups are nearly identical. There is no clean "crossover helps mainly via intrinsic" or "mainly
hurts via extrinsic" pattern -- where the net effect is negative, extrinsic decay is usually the
dominant driver, consistent with the prior DTE-Validation finding, but the size of that
contribution is not uniform.

**GRID (9 fast/slow combos x 5 DTE buckets, PE+10 incremental delta vs. Group1's own median):**

- **DTE=0**: negative in 8 of 9 cells (range -0.71pp to +0.06pp) -- a stable neighborhood, not an
  isolated cell; day-consistency label "broadly/mostly consistent" in all 9.
- **DTE=1**: single contributing session (2026-09-21) -- every cell labeled "insufficient
  sample" for day-consistency regardless of its (adequate, n=44-54) observation count; not
  treated as evidence either way.
- **DTE=4-6**: near zero in all 9 cells (-0.33pp to +0.07pp), sign flips between adjacent cells --
  genuinely "mixed"/"mostly consistent" with no reliable direction.
- **DTE=7-8**: near zero, mixed sign (-0.40pp to +0.10pp) -- the one bucket where the effect does
  not lean consistently negative.
- **DTE=11-13**: negative in all 9 of 9 cells (-0.12pp to -0.20pp) -- the most stable negative
  neighborhood in the grid.

**EVENT-BAR-SIZE DIAGNOSTIC (representative combo, alternate clocks 650/2600):** the same
flat-to-negative direction reproduces at both alternate thresholds, at every DTE bucket with
sufficient data (650: -0.42pp/-0.36pp/-0.08pp/-0.40pp at DTE 0/4-6/7-8/11-13; 2600:
-0.44pp/-0.13pp/-0.46pp/-0.13pp). The absence of a positive incremental effect is **not an
artifact of the frozen 1300-contract clock choice** -- it reproduces on two independently-built
alternate futures-bar sequences.

**Day-level (mandatory check):** DTE=0's negative direction holds on all 3 contributing calendar
sessions (2026-09-08, 09-15, 09-22) independently. DTE=4-6 leans negative on 6 of 8 sessions.
DTE=11-13 leans negative on 5 of 8 sessions. DTE=7-8 is a genuine 2-of-4/2-of-4 split with no
majority direction. No bucket shows a majority of its independent sessions favoring a *positive*
incremental effect.

### Multiple-testing discipline

55 distinct (DTE bucket x parameter) cells were examined for direction and magnitude: 45 from the
GRID (9 fast/slow combos x 5 buckets) plus 10 from the EVENT-BAR-SIZE diagnostic (1 combo x 2 alt
clocks x 5 buckets); the PRIMARY report's own 5 buckets are the (5,20)-frozen-clock subset of the
GRID's 45, not counted twice. Every cell had a substantial observation count (44 to 1086); the
caveat that matters here is **day independence, not sample size** -- DTE=1 has only 1 contributing
calendar session and is excluded from any consistency judgment; DTE=0/4-6/11-13 have 3/8/8
sessions respectively and support a real (if not perfect) consistency check; DTE=7-8 has 4
sessions and shows a genuine split. No single cell anywhere in the 55 showed an isolated large
positive effect that the rest of the grid contradicted -- the flat-to-negative finding is a
neighborhood, not a one-off, at DTE 0 and 11-13; it is a genuine toss-up (not a "we didn't test
enough" gap) at DTE 4-6 and 7-8.

### Operational note (not a research finding)

This run hit two pieces of friction worth recording for future long single-process CLI runs: (1)
an early mistake backgrounding the command with a shell `&` inside a tool call that itself
returned immediately, which silently orphaned the first attempt -- fixed by letting the harness's
own backgrounding track the `dotnet run` process directly; (2) once running correctly, the ~24-pair
Phase 2 build (each pair doing a full-day, full-chain option-bar synchronization) proceeded in
uneven bursts with several multi-minute apparent stalls that all self-resolved -- total wall time
well over an hour for a computation that a 2-session probe completed in under two minutes,
suggesting local Postgres/tick-table read latency rather than a code defect (confirmed by adding
per-day progress logging, which showed the run was always still making forward, non-repeating
progress).

### Answers to Q1-Q10

- **Q1 (Does crossover add incremental information to the underlying reversal)?** **No** --
  and if anything, mildly the opposite: Group2 (crossover confirms) shows a *weaker* subsequent
  futures move than Group1 (all Pattern A) or Group3 (does not confirm), consistently across
  every DTE bucket with data. This held whether or not the crossover's own class was already
  "expected" to be bearish.
- **Q2 (Does it improve PE response)?** **No.** Flat-to-negative in every DTE bucket at the
  representative combo; negative in the large majority of the 45-cell grid and all 10
  event-bar-size cells with sufficient day-level data. Group3 (does not confirm) was
  flat-to-better than Group1 in 4 of 5 buckets -- the opposite framing looks more promising than
  the original hypothesis, see Q10.
- **Q3 (Does the effect survive matched controls)?** Pattern A's own control-beating margin
  mostly survives inside Group2 (stays positive in 4 of 5 buckets), but crossover confirmation
  typically *shrinks* that margin rather than growing it -- the opposite of an "adds edge"
  result. At DTE=1 it turns negative, but that bucket has only one contributing session.
- **Q4 (Is it stable across fast/slow windows)?** The *absence* of a positive incremental effect
  is stable across all 9 grid combinations at DTE=0 (8/9 negative) and DTE=11-13 (9/9 negative).
  At DTE 4-6/7-8 the sign itself is not stable (hovers near zero, flips between adjacent cells) --
  so "no effect" is the stable conclusion there, not "a small negative effect."
- **Q5 (Does it depend on event-bar size)?** **No** -- the same flat-to-negative direction
  reproduces on two independently-built alternate futures-bar clocks (650 and 2600 contracts),
  at every bucket with sufficient day-level data. This rules out the frozen 1300-contract clock
  as the explanation for the null/negative result.
- **Q6 (Does it survive across DTE buckets)?** The *absence* of a positive effect survives across
  DTE 0/4-6/7-8/11-13 (DTE=1 is single-session and excluded). The *magnitude/direction* of the
  (mostly non-positive) effect does not generalize uniformly -- strongly negative at DTE 0 and
  11-13, genuinely mixed at 4-6 and 7-8.
- **Q7 (Does crossover improve intrinsic gain, reduce extrinsic decay, or both)?** **Neither,
  consistently.** Where Group2's intrinsic gain is slightly higher (DTE 0, 4-6), it is offset or
  more than offset by larger extrinsic decay; at DTE 11-13 both move in the unfavorable
  direction together. Where a net difference exists, extrinsic decay is usually the larger
  driver, consistent with the prior DTE-Validation finding, but there is no clean single
  mechanism.
- **Q8 (Is it consistent across independent sessions)?** Reasonably so at DTE=0 (3/3 sessions
  negative), leaning consistent at DTE=4-6 (6/8) and DTE=11-13 (5/8), and a genuine toss-up at
  DTE=7-8 (2/4 each way). No bucket has a session-level majority favoring a positive effect.
- **Q9 (Is a DTE-specific crossover requirement needed, or is a common definition sufficient)?**
  There is **no evidence supporting a DTE-specific crossover rule** -- no DTE bucket shows a
  positive incremental effect that a DTE-conditioned threshold would need to "unlock." The single
  common state-based definition (bearish crossover state = confirms) is sufficient to reach the
  same (non-)conclusion everywhere it was tested.
- **Q10 (Smallest next experiment, not implemented)?** The most direct, smallest next step is to
  flip the framing this experiment tested: Group3 ("crossover does NOT yet confirm," i.e. the
  fast/slow state is still bullish or neutral at the moment Pattern A fires) was flat-to-better
  than Group1 in 4 of 5 DTE buckets here -- the opposite of what this experiment set out to test.
  A dedicated, disciplined evaluation cycle (same rigor as this one: multiple fast/slow windows,
  matched control, day-level check, no trade simulator) of "Pattern A signals where the crossover
  state has *not yet* turned bearish" as its own candidate conditioning signal is the natural next
  step -- named here, not implemented.

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-a-crossover 2026-09-01 2026-09-23 --out=vc-a-crossover.csv
```

## CE/PE Relative-Price Crossover -- Design Audit and Validation (2026-09-24)

### Why this experiment exists

A user audit of the previous "Pattern A + Crossover" experiment established that it tested only a
**futures Close** fast/slow SMA crossover -- never CE, never PE, never any CE-vs-PE relationship
(traced code-path by code-path: `PriceCrossoverEngine.Observe((double)futureBars[i].Close, 0.0)`
in both the PRIMARY/GRID classifier and the event-bar-size diagnostic). The frozen relationship
recorder's own internal engines (`ceEngine`/`peEngine`) each track CE or PE against *its own* price
history, never against each other. The CE/PE relative-price crossover hypothesis was therefore
genuinely untested, not falsified, and this experiment is the smallest clean next step named in
that audit's Q7.

### Phase 1 -- design audit of candidate CE/PE relative representations

| Candidate | Verdict | Reasoning |
|---|---|---|
| **A: `CE - PE`** (raw price difference) | Rejected before seeing results | By put-call parity for a European option on a future, `C - P (approx)= (F - K)` near expiry-independent discounting -- so this series is nearly a relabeling of futures moneyness, i.e. of the futures level itself. A crossover on it would largely re-derive the already-tested-and-null futures crossover rather than add CE/PE-specific information. Also DTE-sensitive in absolute scale (extrinsic value grows with DTE on both legs). |
| **B: `CE / PE`** (price ratio) | Rejected before seeing results | Numerically unstable: as the pinned contract drifts from ATM over the holding window (or under skew), one leg's premium can shrink toward zero, sending the ratio toward 0 or infinity. A rolling MA of an occasionally-exploding ratio gets dominated by a handful of outlier bars -- not a sound crossover input. |
| **C: `Return(CE) - Return(PE)`** (return spread) | **Selected** | Return-normalized, so DTE/absolute-scale artifacts from A are largely removed. Directly and transparently tied to Pattern A's own defining one-event condition ("CE down, PE up") -- this is that same condition made continuous and smoothable rather than binary. Computable with a strict one-event lag (no look-ahead). Its main weakness (percent return on a very cheap option can be noisy) is an existing, already-accepted limitation of this project's `PercentChange` convention generally, not something newly introduced here. |
| **D: `Return(CE) / Return(PE)`** | Rejected before seeing results, per the task's own explicit permission | `Return(PE)` crosses exactly zero far more often than a raw price ever does (a bar with no percent move for that leg) -- a ratio of two returns is unstable even more often than Candidate B's price ratio. Never computed. |
| **E: existing one-event divergence** (`CE Down + PE Up`) | Rejected as not a crossover candidate | This is already Pattern A's own defining, binary, single-event condition -- there is no natural continuous series to feed a fast/slow MA without inventing an ad hoc binary-to-continuous mapping, and any such rolling "how many of the last N events also showed this divergence" measure would be substantially redundant with the episode-length bookkeeping `EpisodeAnalysis` already performs. Not implemented. |

**Only Candidate C (`Return(CE) - Return(PE)`) was implemented and run** -- matching the task's own
stated starting hypothesis, but arrived at independently through the reasoning above, not assumed.

### Phase 2/3 -- data/synchronization and strike-pinning audit (traced, unmodified)

`Return(CE)` and `Return(PE)` for this experiment are computed via the exact, unmodified
`UnderlyingOptionRelationshipSummary.ComputeCeChange`/`ComputePeChange` (horizon=1, ending at
each event) -- the SAME calculator every other forward/prior-return figure in this whole research
track already uses. This means the CE/PE relative series inherits, for free: the same
`[StartTimestamp, EndTimestamp)` event boundaries as futures (both legs are bars in the SAME
per-event sequence the frozen recorder produces); the same `AverageLtp` pricing convention; the
same same-contract/missing/stale gating (a value is null, never fabricated, across a contract
transition or a missing bar on either leg). **Strike pinning**: traced
`EventBarStrikeSelector.PickDynamicAtm` -> `AtmStrikeSelector.PickAtm`, called *independently* for
the call chain and the put chain (nearest strike to `futureBar.Close`, separately per side) -- not
a single strike looked up on both sides. In practice these are guaranteed to coincide because
Nifty's option chain has an identical strike grid for calls and puts, but this is worth documenting
exactly as it is (two independent selections that happen to agree) rather than as "one pinned
strike," per the task's explicit instruction not to assume equivalence.

### Phase 6 -- sign convention (worked out before computing results)

`PriceCrossoverEngine`'s own `DiffFraction = (fastMa - slowMa) / slowMa` is safe for a PRICE series
(always comfortably away from zero) but numerically unsafe for a zero-centered return-spread series,
whose `slowMa` sits arbitrarily close to zero much of the time -- dividing by it would produce
wild, meaningless swings. This experiment therefore added
`CrossoverResolutionDiagnostics.ClassifyConfirmationBySign` (new pure helper, tested), which
classifies purely from the **sign** of `(fastMa - slowMa)`, never dividing by `slowMa`. Sign
convention: **"Confirms" = `fastMa < slowMa`** -- i.e. the recent (fast) average of
`Return(CE)-Return(PE)` sits below its own longer-run (slow) average, meaning CE has recently been
underperforming PE *more* than its own baseline. This is the smoothed, multi-event extension of
Pattern A's own one-event "CE down, PE up" condition, and is the direction that would be
economically consistent with the divergence *intensifying* rather than fading.

### Results (methodology identical to the futures-crossover experiment: same pair discovery, same
episode-first-event convention, same tercile-matched control, same 3-broad-session/day-level
breakdown, same "no ranking" GRID discipline -- only the crossover input series and its
sign-based classification differ)

**PE +10 median by group, representative combo (5, 20):**

| DTE bucket | sessions | Group1 (All) | Group2 (Confirms) | Group3 (DoesNotConfirm) | Control |
|---|---|---|---|---|---|
| 0 | 3 | 0.6406% (n=368) | 0.1058% (n=208) | 1.2842% (n=152) | 0.0342% |
| 1 | 1 (insufficient) | -0.1687% (n=74) | 0.1080% (n=41) | -1.3655% (n=33) | -0.2275% |
| 4-6 | 8 | 0.0785% (n=752) | -0.2325% (n=433) | 0.3517% (n=304) | -0.1963% |
| 7-8 | 4 | 0.2181% (n=396) | 0.0750% (n=239) | 0.2474% (n=150) | -0.0196% |
| 11-13 | 8 | 0.0031% (n=639) | -0.2238% (n=389) | 0.2051% (n=234) | -0.1580% |

In 4 of 5 buckets (0, 4-6, 7-8, 11-13), **Group2 (Confirms) is materially WORSE than Group1, and
Group3 (DoesNotConfirm) is materially BETTER** -- the same reversed-from-expectation pattern the
futures-only crossover experiment already found, now reproduced on a genuinely different (CE/PE
relative) series. Bucket 1 (single session) shows the opposite pattern but cannot be trusted on
its own.

**Control-beating margin (PE +10, diffVsControl):**

| DTE bucket | Group1 | Group2 (Confirms) | Group3 (DoesNotConfirm) |
|---|---|---|---|
| 0 | +0.6297pp | +0.0800pp | +1.2571pp |
| 4-6 | +0.2854pp | **-0.0362pp** | +0.5715pp |
| 7-8 | +0.2501pp | +0.0946pp | +0.2753pp |
| 11-13 | +0.1611pp | **-0.0659pp** | +0.3703pp |

At DTE 4-6 and 11-13, Confirms doesn't just shrink Pattern A's control-beating margin -- it
**erases it and turns it slightly negative** (Confirms trades *underperform* the matched
control on average). DoesNotConfirm, by contrast, roughly doubles the margin in every bucket.

**Underlying (futures) response, +10 events, mean:** unlike the futures-only crossover experiment
(where Confirms was consistently the *weaker* reversal in every bucket), this series' direction is
mixed: Confirms is weaker at DTE 0/7-8 but *stronger* at DTE 4-6/11-13. No single clean underlying
mechanism generalizes across buckets for this candidate.

**Phase 13 check -- is Group2 simply a bigger one-event founding divergence?** The one-event
`Return(CE)-Return(PE)` value AT the signal itself:

| DTE bucket | Group2 (Confirms) mean/med | Group3 (DoesNotConfirm) mean/med |
|---|---|---|
| 0 | -4.23 / -2.40pp | -3.36 / -2.03pp |
| 4-6 | -1.46 / -1.13pp | -1.17 / -0.94pp |
| 7-8 | -0.98 / -0.76pp | -0.80 / -0.58pp |
| 11-13 | -0.98 / -0.80pp | -0.81 / -0.66pp |

In 4 of 5 buckets, **Group2 starts with a BIGGER one-event divergence than Group3, yet goes on to
perform WORSE** in those same buckets. This rules out the simplest "smoothed re-detection" story
(bigger founding move -> better outcome, just re-labeled) -- the relationship is closer to the
opposite of that, consistent with an exhaustion/overextension reading rather than a momentum-continuation
one (a large one-event CE/PE divergence, and a recently-intensifying one, tends to precede a
*weaker* or reversed follow-through, not a stronger one).

**GRID (9 fast/slow combos x 5 buckets):** DTE=0 -- fast=3 near-zero/mixed, fast=5/8 consistently
negative (-1.02pp to -0.25pp); DTE=1 -- single session, not trusted; DTE=4-6 -- consistently
negative-to-flat across all 9 cells (-0.32pp to +0.02pp), the most stable negative neighborhood;
DTE=7-8 -- genuinely mixed sign, small magnitude, no majority direction; DTE=11-13 -- leans
negative in 7 of 9 cells. No cell anywhere showed an isolated large positive effect contradicted by
its neighbors, and no cell was ranked or declared best.

### Multiple-testing discipline

45 grid cells (9 combos x 5 buckets) plus the PRIMARY 5-bucket detailed report at (5,20) (a subset
of the grid, not double-counted) were examined. DTE=1 has only 1 contributing calendar session and
is excluded from any consistency judgment regardless of its (adequate) observation count; DTE
0/4-6/11-13 have 3/8/8 sessions and support a real day-level check (day-level tables above show
Group2 underperforming Group1 in the clear majority of sessions in 0 and 11-13, and in most of
4-6's 8 sessions); DTE=7-8 (4 sessions) is a genuine split with no majority direction, same as it
was in the futures-only crossover experiment. No single exceptional cell drove any conclusion here.

### Answers to Q1-Q12

- **Q1 (most defensible representation)**: `Return(CE) - Return(PE)` -- return-normalized (removes
  most DTE/scale artifacts of a raw price difference), directly tied to Pattern A's own defining
  condition, and free of the ratio-based numerical instability that rules out Candidates B and D.
- **Q2 (what its crossover measures)**: whether the recent (fast-window) average of
  `Return(CE)-Return(PE)` sits below (Confirms) or above (DoesNotConfirm) its own longer-run
  (slow-window) average -- i.e. whether the CE-underperforms-PE divergence has been recently
  intensifying relative to its own baseline, classified by sign only, never by a ratio.
- **Q3 (adds incremental information beyond Pattern A)?** Yes, information -- but not edge. It
  reliably SPLITS Pattern A into a materially worse sub-group (Confirms) and a materially better
  one (DoesNotConfirm) in 4 of 5 buckets. That is real incremental information; it just points the
  opposite way from the original hypothesis.
- **Q4 (improves the underlying reversal distribution)?** No consistent direction -- weaker at
  DTE 0/7-8, stronger at DTE 4-6/11-13. Unlike the futures-crossover experiment, no single
  underlying-response story generalizes here.
- **Q5 (improves PE response)?** No -- Confirms is worse than Group1 in 4 of 5 buckets; if
  anything DoesNotConfirm is the sub-group with the better PE response.
- **Q6 (survives matched controls)?** Confirms's control-beating margin shrinks in every bucket
  and turns negative (underperforms control) at DTE 4-6 and 11-13. DoesNotConfirm's margin is
  larger than Pattern A's own in every bucket.
- **Q7 (survives across DTE groups)?** The reversed-from-hypothesis direction (Confirms worse,
  DoesNotConfirm better) holds at DTE 0, 4-6, 7-8 (mildly), and 11-13 -- four of the five real
  buckets. DTE=1 is single-session and excluded.
- **Q8 (plausible mechanism from decomposition)?** No single clean one -- intrinsic/extrinsic
  contributions to the Confirms-vs-Group1 gap vary by bucket without a consistent driver;
  decomposition does not by itself explain the reversed direction.
- **Q9 (consistent across independent sessions)?** Reasonably so at DTE 0 (day-level table:
  Confirms below Group1 in 3/3 sessions) and DTE 11-13 (Confirms below Group1 in 6/8 sessions);
  mixed at DTE 4-6 (Confirms below Group1 in 6/8, closer split) and a genuine toss-up at DTE 7-8.
- **Q10 (just a smoothed re-detection of Pattern A itself)?** **No, not in the simple sense.** The
  Phase 13 check shows Group2 (Confirms) actually starts with a BIGGER one-event founding
  divergence than Group3 in 4 of 5 buckets, yet performs WORSE afterward in those same buckets --
  if this were merely re-detecting "a stronger version of the same one-event signal," bigger
  founding divergence should predict a *better* outcome, not a worse one. The crossover is
  measuring something distinct from Pattern A's raw one-event magnitude; it is just not, on this
  evidence, information that supports the original bullish-for-PE hypothesis.
- **Q11 (smallest next experiment if positive, NOT implemented)**: N/A -- the result is not
  positive for the original hypothesis.
- **Q12 (if negative, next existing information with the strongest mechanistic reason to test,
  NOT implemented)**: The Group2-vs-Group3 reversal here (bigger founding divergence -> worse
  outcome) closely parallels the futures-only crossover experiment's own finding (Confirms ->
  weaker subsequent reversal) -- both point toward an **exhaustion/overextension** reading rather
  than a momentum-continuation one. The most mechanistically motivated next candidate is therefore
  the DoesNotConfirm/"not yet exhausted" framing already named in the futures-crossover audit's own
  Q10 -- now reinforced by an independent series (CE/PE relative return) reaching the same
  structural conclusion from a different angle. A dedicated evaluation cycle of "Pattern A signals
  where the CE/PE relative-return crossover has NOT yet flipped" as its own candidate conditioning
  signal is the natural next step -- named here, not implemented.

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-a-ce-pe-crossover 2026-09-01 2026-09-23 --out=vc-a-ce-pe-crossover.csv
```

## Pattern A Divergence-Maturity Diagnostic (2026-09-24)

### Purpose

Three observations pointed the same direction: (1) futures crossover confirmation precedes a
*weaker* Pattern A reversal; (2) CE/PE `Return(CE)-Return(PE)` crossover confirmation also
precedes a *weaker* reversal; (3) archived F58 research found an asymmetric call-over-reaction /
put-under-reaction exhaustion signature. Before testing another formula, this diagnostic asks: are
(1) and (2) just two views of the same thing -- does Pattern A's expected reversal simply fade as
its CE/PE divergence gets more mature/extended, with the "crossover" result merely detecting that
maturity? **Purely diagnostic -- no trade simulator, no filter, no entry/exit rule, nothing
implemented.**

### Methodology (frozen, unmodified where reused)

Identical pair discovery, RelSpread (`Return(CE)-Return(PE)`) construction, representative (5, 20)
combo, and `CrossoverResolutionDiagnostics.ClassifyConfirmationBySign` definition as the completed
ce-pe-crossover experiment -- copied verbatim, no new window, no new grid. Two new descriptive
measurements, computed strictly from information at/before each Pattern A signal:

- **`StateAgeEvents`** (`CrossoverResolutionDiagnostics.CountConsecutiveSameSign`, new tested
  helper): how many consecutive prior event bars, walking backward from the signal, share the
  SAME sign of `(fastMa - slowMa)` as the signal itself -- i.e. how long the current crossover
  *state* has already persisted ("maturity"), as opposed to whether a crossing just fired.
- **`Trend`**: compares `|fastMa - slowMa|` at the signal against the same quantity `dmLag = 5`
  events earlier (`dmLag` reuses the fast window itself -- not a new invented parameter) --
  "Strengthening" if the separation grew, "Weakening" if it shrank, "Stable"/"Unavailable"
  otherwise. This is Part 1's descriptive state classification.

Maturity terciles (Part 2-4) are built by tercile-splitting `StateAgeEvents` within each DTE
bucket via the project's existing `ForwardValidationAnalysis.ComputeTerciles`/
`ConditionalMovementAnalysis.ClassifyTercileBucket` (unmodified), among signals with a defined
crossover state.

### Part 5 -- what's actually available for the F58 connection

`RelationshipObservation` carries no Delta/Gamma/Theta/IV fields -- **Greeks and implied vol are
unavailable on this frozen event clock and were not approximated from another clock**, per the
task's explicit instruction. The only check performed is a crude magnitude-only proxy: at the
signal, is `|CE one-event % change|` bigger than `|PE one-event % change|` ("CE reacted more
strongly")? This is not F58's Greeks-residual, forward-looking test -- it is a same-event
magnitude comparison, reported as exploratory only.

### Results

**Part 4 -- THE MOST IMPORTANT TEST: does Confirms-vs-DoesNotConfirm survive after conditioning on
maturity? (PE+10 median gap, Confirms minus DoesNotConfirm)**

| DTE bucket | Low maturity (early) gap | High maturity (late) gap |
|---|---|---|
| 0 | -1.29pp | -1.62pp |
| 1 (1 session, not trusted) | +1.79pp | +0.17pp |
| 4-6 | -0.52pp | -0.41pp |
| 7-8 | -0.37pp | -0.05pp (nearly vanishes) |
| 11-13 | -0.21pp | -0.66pp |

**In 4 of 5 real buckets (0, 4-6, 7-8, 11-13), the gap stays negative at BOTH maturity extremes**
-- Confirms is worse than DoesNotConfirm whether the crossover state is freshly formed or already
long-persisted. If the crossover effect were merely a proxy for maturity, it should shrink toward
zero or flip sign at one extreme; instead it survives in the clear majority of buckets (only
weakening substantially at DTE=7-8's High tercile, the one bucket that was already the least
consistent in the prior experiment).

**Day-level consistency (Part 6) -- unusually strong for two of the four checkable buckets:**

- **DTE=0** (3 sessions): Confirms worse than DoesNotConfirm in **3 of 3** sessions.
- **DTE=4-6** (8 sessions): Confirms worse in **8 of 8** sessions.
- **DTE=11-13** (8 sessions): Confirms worse in **8 of 8** sessions.
- **DTE=7-8** (4 sessions): a genuine 2-vs-2 split (2026-09-08 and 2026-09-21 favor Confirms;
  2026-09-15 and 2026-09-22 favor DoesNotConfirm) -- consistent with this bucket already being the
  one exception in the completed ce-pe-crossover experiment.

**Part 1 -- does the reversal weaken as maturity increases? (Futures +10 mean, Low -> Mid ->
High maturity)**

| DTE bucket | Low | Mid | High |
|---|---|---|---|
| 0 | -0.0170% | -0.0163% | -0.0135% |
| 4-6 | -0.0100% | -0.0131% | -0.0121% |
| 7-8 | -0.0165% | n/a (0) | -0.0144% |
| 11-13 | -0.0089% | -0.0110% | -0.0110% |

Only DTE=0 shows a clean, monotonic weakening from Low to High maturity; DTE=4-6 and DTE=11-13 are
**not monotonic** (the Mid tercile shows the strongest reversal, not High) -- this is a genuinely
mixed, exploratory finding, not a clean confirmation that "more mature always means weaker."

**Part 5 -- F58 magnitude-only proxy (%CE-reacted-more-strongly-than-PE, Confirms vs
DoesNotConfirm):**

| DTE bucket | Confirms | DoesNotConfirm |
|---|---|---|
| 0 | 62.3% | 58.4% |
| 4-6 | 65.7% | 54.9% |
| 7-8 | 46.0% | 40.2% |
| 11-13 | 53.0% | 43.0% |

Confirms episodes show CE reacting more strongly than PE more often than DoesNotConfirm episodes
in **all 4 real buckets** -- directionally consistent with F58's "call over-reaction" component,
but this is a same-event magnitude comparison, not F58's actual Greeks-residual/forward-correlation
design, and does not test the PE-under-reaction half of F58's story. Reported as exploratory, not
confirmatory.

### Multiple-testing discipline

No threshold, window, or event-bar size was optimized in this diagnostic -- the representative
(5,20) combo and the `dmLag=5` trend-comparison lag are both reused, not fitted. Tercile splits use
the project's existing, unmodified tercile machinery. The clearest, most robust finding (Part 4's
maturity-conditioned survival of the Confirms/DoesNotConfirm gap, and its near-perfect day-level
consistency in 2 of 4 checkable buckets) rests on the full Pattern A sample sizes already used
throughout this research track (n=181-566 per maturity subgroup in the larger buckets). The
weakest findings (Part 1's Trend states, Part 5's F58 proxy) are exploratory and should be read
that way -- smaller effective independence (fewer distinguishing sessions per state) and no
forward-looking design.

### Answers to the eight final questions

1. **Does Pattern A's expected reversal weaken as CE/PE divergence matures?** Mixed. Clean,
   monotonic weakening only in DTE=0; DTE=4-6/11-13 are non-monotonic (Mid tercile shows the
   strongest reversal). Not a general finding -- exploratory.
2. **Is the CE/PE crossover result merely detecting divergence maturity?** **No.** The
   Confirms-worse-than-DoesNotConfirm gap survives at BOTH maturity extremes in 4 of 5 buckets. A
   pure maturity-detection story would predict the gap vanishing or flipping at one extreme.
3. **Does crossover add information beyond divergence magnitude/maturity?** **Yes** -- same
   evidence as Q2; the effect holds within maturity subgroups, not just pooled.
4. **Evidence consistent with the asymmetric F58 exhaustion idea?** Directionally, yes, but only
   as a crude magnitude proxy (not F58's actual Greeks/forward-looking design) -- Confirms shows
   CE reacting more strongly than PE more often than DoesNotConfirm, in all 4 real buckets.
   Exploratory, not confirmatory.
5. **Survives across actual DTE buckets?** Yes, in 4 of 5 (0, 4-6, 7-8 partially, 11-13); DTE=1 is
   single-session and excluded.
6. **Replicates across independent calendar sessions?** Strongly yes in DTE=0 (3/3) and DTE=4-6
   and DTE=11-13 (8/8 each) -- unusually consistent. DTE=7-8 (4 sessions) is a genuine 2-vs-2
   split, not replicating cleanly.
7. **Smallest next experiment justified by the evidence (NOT implemented):** Extend the F58
   connection from a same-event magnitude proxy to an actual forward-looking test, entirely on
   this frozen clock (no Greeks needed): does the CE-reacted-more-strongly-than-PE pattern at a
   Confirms signal continue to intensify or reverse over the NEXT few events, mirroring F58's own
   "residual vs next window" design using RelSpread's own forward change instead of a
   Greeks-implied one. Still a diagnostic, not a trading rule.
8. **What should we stop revisiting?** (a) The idea that crossover confirmation is just a maturity
   proxy -- rejected, the effect survives conditioning. (b) A clean, monotonic "more mature always
   means weaker" relationship pooled across all buckets -- rejected as a general claim, only DTE=0
   shows it cleanly. (c) Whether `Return(CE)-Return(PE)` crossover carries *some* real incremental
   information at all -- already answered yes twice now (the completed experiment and this
   diagnostic); do not re-run that specific question with yet another parameter sweep.

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-a-divergence-maturity 2026-09-01 2026-09-23 --out=vc-a-divergence-maturity.csv
```

## Pattern A/B Temporal Context Diagnostic (2026-09-24)

### Purpose

Does the local mix of Pattern A / Pattern B activity in the minutes immediately before a signal
carry information about whether that signal is the start of a genuine directional phase or just
short-term oscillation? Purely diagnostic -- no trading rule, no filter, no majority-vote rule, no
cooldown, no optimization.

### Methodology (frozen where reused, additive where new)

Same pair discovery, episode-first-event convention, DTE buckets, and forward horizons as every
prior experiment in this track. For each Pattern A and Pattern B signal, four TIME-based windows
(1/3/5/10 minutes -- not event-count windows) are built by walking backward from the signal's own
`StartTimestamp` (never including the signal's own bar -- "now" is defined as the signal's start,
so it can never be part of its own preceding context). Within each window: `ACount`/`BCount` (how
many prior events had `RelationshipCategory` equal to Pattern A/Pattern B), `NetDominance`,
`SignalRate` (fraction of all events in the window that were A or B), the underlying futures
return over the window, and distance from the window's own high/low. State classification uses
only natural, non-optimized rules: "NoPriorSignal" (no A/B events in the window), "Transition" (the
first and last A/B event in the window differ -- a flip occurred), else "A-dominant"/"B-dominant"
(whichever count exceeds the other) or "Balanced" (tie) -- no invented thresholds beyond simple
50% dominance. The representative window (5 minutes, a middle point of {1,3,5,10}, not chosen for
performance) gets the full detailed breakdown; all four windows get a compact, non-ranked
cross-window summary.

### Operational note: a real bug found and fixed mid-run

The first full run crashed with an unhandled `ArgumentException` ("An item with the same key has
already been added") in the compact cross-window summary section. Root cause: `ToDictionary` keyed
only by `Episode.StartEventId` -- but event IDs restart from 0 for every (date, expiry) pair, so
two Pattern A/B signals from *different calendar days within the same DTE bucket* can share the
same `StartEventId`, causing a genuine key collision once enough sessions accumulated (it did not
manifest on the 2-day probe run). Fixed by keying on `(Date, StartEventId)` instead. This was a
real correctness bug, not a hang -- earlier apparent "stalls" during monitoring were genuine slow
progress on the DB-bound Phase 2 rebuild, confirmed by the log advancing substantially between
checks that looked flat at short intervals.

### Results

**Mechanism check (Q1/Q2/Q3) -- representative window (5 min), PE/CE +10 median gap between
"preceded by same-direction dominance" and "preceded by opposite-direction dominance":**

| DTE bucket | Pattern A Gap (A-dominant − B-dominant) | Pattern B Gap (B-dominant − A-dominant) |
|---|---|---|
| 0 (3 sessions) | **+1.14pp** (consistent with hypothesis) | **-2.70pp** (opposite) |
| 1 (1 session, not trusted) | -1.53pp | -1.07pp |
| 4-6 (8 sessions) | +0.28pp (weak) | +0.53pp (weak) |
| 7-8 (4 sessions) | -0.39pp (opposite) | -0.27pp (opposite) |
| 11-13 (8 sessions) | +0.30pp (weak) | +0.06pp (near zero) |

Pattern A leans toward the naive "A-dominant context strengthens A" hypothesis in 3 of 4 real
buckets, but Pattern B is a genuine split (2 buckets each way), and the single largest-magnitude
result (bucket 0, Pattern B, -2.70pp) directly **contradicts** the hypothesis.

**Cross-window robustness (1/3/5/10 min, same Gap, NOT ranked/optimized):** within every single
bucket, the Gap's sign flips across at least one adjacent window for at least one of the two
patterns. The one partial exception is DTE 7-8, Pattern A, which is negative at all four windows
(-0.05, -0.41, -0.39, -0.30pp) -- a stable finding, but in the *opposite* direction from the
hypothesis, and for only one pattern in one bucket.

**Transition rate (Q4):** remarkably consistent across DTE buckets -- 47.5% (0), 44.5% (1), 42.9%
(4-6), 46.8% (7-8), 42.1% (11-13). Roughly **44-48% of all Pattern A/B signals occur amid a recent
A↔B flip within the last 5 minutes**, essentially constant regardless of DTE. This is a genuine,
stable descriptive finding about how often these signals arise from oscillation rather than a
clean run -- but the mechanism checks above show this context does not translate into a
consistent outcome difference.

**Day-level (mandatory, DTE=0's 3 sessions):** the same-vs-opposite Gap's sign flips across all 3
sessions for Pattern A (2026-09-08 negative, 09-15 positive, 09-22 negative) -- not consistent
even within the one bucket with enough sessions to check properly at the representative window.

**Q5/Q6 (does context explain the A→PE vs B→CE outcome asymmetry, or identify directional-phase
vs. oscillation):** no clean signal found. The "Transition" context's own outcomes sit between the
A-dominant and B-dominant outcomes in most buckets rather than being distinctly better or worse,
and the same-vs-opposite Gap does not consistently favor one pattern's expected option response
over the other in a way that would explain the previously-documented A/PE vs B/CE asymmetry (which
the DTE-Validation experiment already tied to extrinsic decay, not local temporal context).

### Verdict: weak/mixed -- stop this line of research

Per the task's own explicit decision rule, this is a **weak/mixed** result: the mechanism-check
sign flips across DTE buckets, across the four time windows within the same bucket, and across
independent calendar sessions within the one bucket with enough data to check. No single window,
bucket, or pattern shows a robust, direction-stable finding of the kind the divergence-maturity
and CE/PE-crossover diagnostics found. The one genuinely stable, cross-bucket-consistent finding
(the ~44-48% Transition rate) is a fact about signal *composition*, not about outcome, and does not
by itself carry tradeable information. **This specific line of research (local A/B temporal
dominance as a conditioning signal) should be closed, not extended into a controlled trade-level
experiment**, per the task's own stated criterion.

### Visual analysis -- not completed this pass

The requested mechanically-selected charts (futures price vs. time with Pattern A/B markers, CE/PE
price, crossover state, forward outcomes) were not built in this pass. They require a distinct
step -- exporting a raw per-event price time series for specific, mechanically-selected days (not
just the aggregate statistics this diagnostic produces) and building an HTML/SVG visualization from
it -- separate from the statistical diagnostic itself. Given the diagnostic's own weak/mixed
verdict (which per the task's explicit stopping rule closes this line of research before any
trade-level design step), building the charts now would only serve exploratory understanding, not
feed a next experiment. Held pending your decision on whether you still want them.

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-ab-temporal-context 2026-09-01 2026-09-23 --out=vc-ab-temporal-context.csv
```

## Pattern A Crossover-Conditioned Trade Test (2026-09-24)

**Task**: the first controlled economic test following the A/B Temporal Context Diagnostic's
weak/mixed verdict (closed above). Tests whether the already-validated CE/PE relative-return
crossover (`R_t = Return(CE) - Return(PE)`, frozen `PriceCrossoverEngine(5,20)`,
`ClassifyConfirmationBySign(confirmsWhenFastBelowSlow: true)`) improves the frozen Pattern A → BUY
PE trade simulation's real economics. Pattern A only; Pattern B kept as a descriptive/control
reference, never filtered. Range: 2026-09-01 to 2026-09-23 (12 trading sessions, 24 (date,expiry)
pairs, DTE buckets 0/1/4-6/7-8/11-13).

### Methodology

Added one additive parameter to the frozen simulator, `PatternRelationshipTradeSimulator
.SimulateDayAsync(..., Func<int,bool>? patternAEntryFilter = null)`, consulted only for Pattern A
signals, only after all existing entry gates pass (position-open check, after-3pm check, contract-
transition check), never affecting exits or Pattern B. A rejected signal is recorded with a new
`SignalOutcome.FilteredByEntryCondition` audit row rather than silently vanishing. Default `null`
preserves prior behavior byte-for-byte, verified by all 16 pre-existing
`PatternRelationshipTradeSimulatorTests` passing unchanged; 2 new tests added
(`..._SkipsFilteredSignal_ButLaterSignalStillEnters`, `..._NeverAppliesToPatternB`). No baseline
rule was modified: same pinned-ATM contract, single open position, sequential trades, opposite-
pattern exit, mandatory 15:15 exit, no entries after 15:00, fixed 10 lots, existing cost/fill
model, no SL/TP.

Two deliberately separate analyses, per the task's own warning against comparing different trade
populations without explaining the selection effect:

- **PART 1 (zero selection effect)**: baseline's own unchanged 1,783 executed Pattern A trades,
  split *post-hoc* by the crossover state recorded at each trade's signal timestamp (Confirms /
  DoesNotConfirm / Unavailable-still-warming-up). No re-simulation; this only answers "do trades
  that happened to be flagged Confirms look different from ones flagged DoesNotConfirm."
- **PART 2 (realized committed variants)**: two fresh, independently re-simulated runs — one that
  only ever enters on Confirms, one that only ever enters on DoesNotConfirm — capturing real
  cascading effects (a skipped signal can free a position slot for a later, different trade). This
  answers "what would actually have been traded under this rule," including opportunity-retention
  metrics against the baseline population.

### PART 1 results — same eligible signal set (post-hoc split, no re-simulation)

| Group | n | Net P&L | P&L/trade | Median trade | Win% | PF | Gross profit | Gross loss |
|---|---|---|---|---|---|---|---|---|
| A. Baseline (all Pattern A) | 1,783 | -405,191.95 | -227.25 | -452.95 | 38.4% | 0.79 | 1,491,277.77 | -1,896,469.72 |
| A + Confirms | 830 | -342,127.09 | -412.20 | -537.92 | 35.4% | 0.63 | 571,068.52 | -913,195.61 |
| A + DoesNotConfirm | 760 | -57,168.57 | -75.22 | -367.64 | 39.7% | 0.93 | 706,648.56 | -763,817.13 |
| A + Unavailable (warming up) | 193 | -5,896.29 | -30.55 | -242.21 | 46.1% | 0.97 | 213,560.69 | -219,456.98 |
| B. Baseline (control, never filtered) | 1,776 | -397,916.55 | -224.05 | -382.24 | 35.4% | 0.75 | 1,205,383.09 | -1,603,299.64 |

Session sign-count: **Confirms positive in 3/12 sessions** (09-15, 09-16, 09-22); **DoesNotConfirm
positive in 5/12 sessions** (09-10, 09-15, 09-17, 09-18, 09-22); baseline positive in only 2/12
(09-15, 09-22). DTE-bucket sign-count: DoesNotConfirm net-positive only in DTE 0 (+75,955.83); all
of DTE 1, 4-6, 7-8, 11-13 remain net-negative for every group including DoesNotConfirm.

**The direction is the opposite of the naive hypothesis**: Confirms (the crossover agreeing with
Pattern A's implied bearish-CE/bullish-PE direction) selects the *worse* trades (P&L/trade roughly
1.8x worse than baseline), while DoesNotConfirm selects the *better* (least-bad) trades. This is
consistent with the divergence-maturity diagnostic's earlier finding that a "confirming" reading
can reflect an already-mature/exhausted divergence rather than fresh directional information.

### PART 2 results — realized committed variants + opportunity retention

| Variant | n | Net P&L | P&L/trade | Win% | PF |
|---|---|---|---|---|---|
| Confirms-only (realized) | 1,201 | -404,702.26 | -336.97 | 36.6% | 0.69 |
| DoesNotConfirm-only (realized) | 935 | -100,908.03 | -107.92 | 40.0% | 0.89 |

**Confirms-only vs. baseline**: retained 641/1,783 baseline trades (36.0%), 162 new-in-variant-only
(cascading) trades; retains 50.9% of baseline winners, 55.8% of baseline losers, 60.2% of gross
profit, removes 31.3% of gross loss, retains 99.9% of baseline net P&L; P&L/trade delta vs.
baseline = **-109.72** (worse).

**DoesNotConfirm-only vs. baseline**: retained 573/1,783 baseline trades (32.1%), 69 new-in-variant-
only trades; retains 50.8% of baseline winners, 46.4% of baseline losers (removes more losers
proportionally than winners), 56.9% of gross profit, removes 50.0% of gross loss, retains 24.9% of
baseline net P&L; P&L/trade delta vs. baseline = **+119.33** (better — per-trade loss roughly
halved: -227.25 → -107.92).

The realized (re-simulated, cascading-effects-included) variant confirms the same direction as the
zero-selection-effect PART 1 split: DoesNotConfirm-conditioned entry is a real, non-artifactual
improvement in per-trade economics, not a re-simulation shuffling effect.

### Statistical discipline

- Confirms subset: n=830, 12 independent sessions, largest single-session share of total |P&L| =
  5.3% — not driven by one session.
- DoesNotConfirm subset: n=760, 12 independent sessions, largest single-session share of total
  |P&L| = 5.9% — not driven by one session.
- Both subsets have ample trade counts (n>700) across all 12 sessions and all 5 DTE buckets — no
  small-sample flag.
- Neither subset is ever pooled-positive; per the task's own rule, neither is called "profitable."

### Decision-gate classification: **WEAK/MIXED**

Not **CLEAR CANDIDATE**: the DoesNotConfirm-conditioned subgroup remains net-lossmaking overall
(-57,168.57 post-hoc / -100,908.03 realized) — a materially smaller loss than baseline, but not a
positive-P&L strategy. It is directionally consistent in win-rate/PF/P&L-per-trade terms and not
driven by one session, but it is net-positive in only 5 of 12 sessions and only 1 of 5 DTE buckets
(DTE 0). That falls short of "directionally consistent across multiple independent sessions" in
the sense the decision gate requires for freezing a strategy.

Not **NO EDGE**: the crossover state clearly and consistently distinguishes worse trades (Confirms)
from better trades (DoesNotConfirm) — the same direction holds in both the zero-selection-effect
post-hoc split and the independently re-simulated committed variant, the effect size is large
(P&L/trade roughly halved), and it isn't concentrated in one session or DTE bucket. This is too
real and too consistent to dismiss outright.

**Per the task's own rule for WEAK/MIXED: do not add another filter; decide whether to abandon.**
This result is reported for that decision and no further filter, exit-rule experiment, or OOS test
design has been built. Stopping here per instruction, pending your decision on whether to abandon
this line, investigate *why* Confirms trades are worse (a mechanism question, not a new filter),
or something else.

### Reproduction

```
dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-a-crossover-trade-test 2026-09-01 2026-09-23 --out=vc-a-crossover-trade-test.csv
```
