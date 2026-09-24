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
