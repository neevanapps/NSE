# Child (strike-level) table schema — finalized for review

Two new tables (making three total alongside the existing `CadenceContext`) in
`NiftySignal.BacktestData` (same project/DB, `niftysignal_backtest_analysis`), designed
2026-09-12 and **implemented the same day** (migration `AddStrikeCadenceAndBandTables`) once the
band definitions, OHLC fields, and option-side CVD proxy were finalized. `CadencePopulator.cs`
now builds all three tables in one pass; `NiftySignal.MetricTrials`/analysis SQL can query
`StrikeCadenceSnapshots`/`StrikeBandCadenceSnapshots` once the 4 real days are repopulated (see
`docs/REVIEW_FINDINGS.md`'s 2026-09-12 implementation section for the repopulation note).

## Three-table structure, three different cadences

1. **`CadenceContext`** (existing parent) — future/spot/VIX, one row every **15 seconds**.
2. **`StrikeCadenceSnapshot`** (new) — raw, one row per strike per option type, at the **same 15
   seconds** as `CadenceContext` (1:1 pairing via FK, same live cadence production's own
   `StrikeSnapshot` already samples at). Broad enough band (ATM±3) to cover every requested view
   for both tracked expiries. Full fidelity, nothing derived or thrown away — any coarser view is
   built from this later, not computed on the fly and discarded.
3. **`StrikeBandCadenceSnapshot`** (new) — aggregated, one row per (**5 or 15-minute** bucket,
   expiry, band definition). Rolls up table 2's stored 15-second rows two ways at once: across
   every strike in the named band, and across every 15-second cadence inside the bucket window.
   Precomputed so testing "does the 5-strike band correlate better than the 3-strike band" doesn't
   require re-aggregating raw rows every time.

Getting this right matters specifically because of `CvdProxy`: classifying each raw tick's volume
delta (quote-rule, same technique as `FutureCvdProxy`) only works honestly at the cadence the ticks
actually arrive at. Storing table 2 straight at 5/15-minute granularity would have meant
classifying ticks "under the hood" between snapshots without ever persisting that history —
recomputing the same problem the future's own CVD proxy was built to avoid. Storing it at the true
15-second cadence keeps `CvdProxyVolumeThisCadence` a direct, honest analog of
`FutureCvdProxyThisCadence` on `CadenceContext`, and table 3's rollup becomes real aggregation over
stored data rather than a guess at what happened in between.

Rough volume check: ATM±3 (7 strikes) x 2 option types x 2 expiries = 28 rows/cadence, ~1,500
cadences/trading day -> ~42,000 rows/day for table 2 across 4 days ~ 168,000 rows so far.
Comfortable for Postgres — same reasoning production already used to justify a narrow band.

## Configurable cadence — table 3 only

`CadenceMinutes` lives only on `StrikeBandCadenceSnapshot`, not on `StrikeCadenceSnapshot` (which
is always 15 seconds, implicit via its `CadenceContextId` FK — no column needed). The populator
takes `CadenceMinutes` as a parameter for table 3 (e.g. populate both 5 and 15 at once), and rows
for different granularities coexist in the same table, the same way `FutureCvdProxyNet5Min`/
`Net15Min` already sit side by side on the parent table rather than requiring a rebuild to compare.
Both 5 and 15 minutes are exact multiples of the 15-second base, so every bucket boundary lines up
with a real `CadenceContext`/`StrikeCadenceSnapshot` row.

## Decisions defaulted by me, pending your review

1. **Join to the parent table: real FK (`CadenceContextId`), not a timestamp-match.** Production's
   own `StrikeSnapshot` joins to `ScoreSnapshot` by matching timestamp rather than a foreign key —
   I chose the FK here instead: table 2 is 1:1 with `CadenceContext` by construction (same 15s), and
   every table 3 bucket boundary is guaranteed to land on an existing `CadenceContext` row too
   (5/15 min are exact multiples of 15s). A real FK gives direct EF navigation and a guaranteed
   non-null join. Easy to switch to timestamp-matching instead if you'd rather match the existing
   convention exactly.
2. **IV/Greeks aggregation in the band table: OI-weighted average, not a plain average.** A thin,
   far-from-ATM strike with 200 contracts of OI shouldn't count the same as a strike carrying
   500,000 — matches how IV skew is conventionally read (weighted by where real positioning sits).
   A strike with null OI that cadence is excluded from the weighted average, not treated as
   zero-weight (which would silently distort rather than just omit it).
3. **Notional basis: `MarkPrice`, not raw traded price.** Matches the ratio-composite's own
   `RatioNotionalVolumeRaw` convention already established elsewhere in this project — consistent
   rather than inventing a second convention.

## Table 2 — `StrikeCadenceSnapshot` (15-second, 1:1 with `CadenceContext`)

One row per (15-second cadence, strike, option type, expiry) — full fidelity, no aggregation.

```csharp
public sealed class StrikeCadenceSnapshot
{
    public long Id { get; set; }

    public required long CadenceContextId { get; set; }        // FK to the parent CadenceContext row at this exact 15s timestamp

    public required DateOnly AsOfDate { get; set; }
    public required DateTimeOffset Timestamp { get; set; }     // == CadenceContext.Timestamp for this row; stored UTC, same Npgsql requirement

    public required string Token { get; set; }                 // bare instrument token -- direct traceability back to Instruments/Ticks, matches production's StrikeSnapshot convention

    public required DateOnly ExpiryDate { get; set; }           // this week's or next week's -- both populated
    public required int DaysToExpiry { get; set; }              // persisted directly, feeds straight into the DTE-confound work from today's earlier session

    public required decimal StrikePrice { get; set; }
    public required OptionType OptionType { get; set; }
    public required int StrikeOffsetFromAtm { get; set; }       // signed: 0=ATM, negative=below spot, positive=above spot -- persisted at snapshot time since ATM itself drifts intraday

    public decimal? MarkPrice { get; set; }                     // (bid+ask)/2, LTP fallback -- same convention as production
    public decimal? BidPrice { get; set; }
    public decimal? AskPrice { get; set; }
    public long? VolumeDelta { get; set; }                      // contracts, this 15s cadence only -- null on the very first cadence a token is ever seen in (no baseline), a real confirmed number after that
    public decimal? NotionalDelta { get; set; }                 // blind rupee notional (2026-09-12) -- sum of each tick's own LastPrice x that tick's own volume delta, NOT VolumeDelta x one closing price; distinct from CvdProxyNotionalThisCadence below (same idea, signed buy-minus-sell)
    public long? OpenInterest { get; set; }
    public long? OpenInterestDelta { get; set; }
    public decimal? OiNotional { get; set; }                    // (2026-09-12, recalibrated same day) OpenInterest x MarkPrice averaged over a trailing 1 min, NOT the instantaneous price -- originally 3 min on an assumed refresh rate, corrected after measuring the real OI-change gap on populated data (~60s median, both liquid and thin strikes)
    public decimal? MarkPriceDelta { get; set; }                 // needed to classify OiBuildup below
    public OiBuildupClassification? OiBuildup { get; set; }       // NiftySignal.Features.OiBuildupClassifier.Classify, reused directly -- BacktestData already references Features, so no need for Domain's OiBuildupQuadrant mirror-enum (that exists only because Host+Dashboard import Domain.Enums and Features together)
    public decimal? SpreadAbs { get; set; }
    public decimal? SpreadPctOfMid { get; set; }

    // (2026-09-12) Resting order-book depth imbalance, this strike's own analog of CadenceContext's
    // FutureDepthImbalanceFromLastCadence -- averaged over every real tick this cadence (not a
    // single snapshot), using DepthImbalanceAccumulator (renamed from FutureDepthAccumulator,
    // which was already fully generic).
    public double? TotalBidQty { get; set; }
    public double? TotalAskQty { get; set; }
    public double? DepthImbalanceFromLastCadence { get; set; }

    // Full traded-price OHLC for this 15s cadence -- distinct from MarkPrice (bid/ask mid, used
    // for IV/Greeks) above. Needed for realistic paper-trade fill simulation: a resting or stop
    // order fills against traded price, not mid. Matches CadenceContext's own Open/High/Low/Close
    // naming convention exactly.
    public decimal? OpenFromLastCadence { get; set; }
    public decimal? HighFromLastCadence { get; set; }
    public decimal? LowFromLastCadence { get; set; }
    public decimal? CloseFromLastCadence { get; set; }

    // Same quote-rule technique as FutureCvdProxy (CadencePopulator.cs), applied per strike, at the
    // same 15s cadence -- a direct, honest analog of FutureCvdProxyThisCadence, not a rollup. Any
    // 5/15-minute window is built from these stored rows in table 3, never computed on the fly and
    // discarded. Expect more nulls than the future's version on thin, far-from-ATM strikes without
    // a live two-sided quote.
    public long? CvdProxyVolumeThisCadence { get; set; }         // signed net classified contract volume, this 15s cadence
    public decimal? CvdProxyNotionalThisCadence { get; set; }    // signed net classified notional (+/- volume x MarkPrice), this 15s cadence

    public double? ImpliedVolatility { get; set; }
    public double? Delta { get; set; }
    public double? Gamma { get; set; }
    public double? ThetaPerDay { get; set; }
    public double? Vega { get; set; }
    public double? Vanna { get; set; }        // (2026-09-13) already returned by BlackScholes.Calculate, just not read here before
    public double? CharmPerDay { get; set; }  // (2026-09-13) same source, same null convention as every Greek above
}
```

**Persisted strike band widened 3 -> 10 (2026-09-13)**: `PersistedStrikeOffsetBand` (in
`CadencePopulator.cs`) was `ATM+/-3`, enough for every band tested so far (`Strike7`'s own
definition tops out at offset 3) but not enough to test three untested live components:
`GammaExposure`/`VannaExposure`/`CharmExposure` sum over the **entire** nearest-expiry chain, and
`IvSkew` targets a real expected-move OTM strike pair that lands ~5-6 strikes out on a normal week.
Widened to `ATM+/-10`, confirmed against `niftysignal_vm_copy.instruments` to match the actually
tracked/subscribed chain width (~20-21 distinct strikes per expiry, i.e. ~ATM+/-10 at 50-point
spacing) — not an arbitrary round number. Purely additive: every existing band definition
(`Itm2Atm1`/`Strike3`/`Strike5`/`Strike7`, all offset <= 3) is unaffected, more data is simply now
available beyond what they already used. **Requires a repopulate** to see the wider band and the
new Vanna/CharmPerDay columns on rebuilt data.

**Greeks/IV underlying (finalized 2026-09-12)**: each strike's IV is solved from its own `MarkPrice`
against **that expiry's own put-call-parity synthetic underlying** (`CadencePopulator.
ComputeSyntheticUnderlyingForChain`, reusing `NiftySignal.Pricing.SyntheticForward.Compute`) — NOT
the tracked monthly future. This isn't a stylistic preference: this exact codebase already found
and fixed this precise bug once, live, on 2026-09-07 (see `SyntheticForward.cs`'s own doc comment)
— using the wrong underlying for a short-dated weekly option produced put IV running 7-8 vol points
below call IV at the same strike, all day, the signature of a too-low underlying, not real skew.
`SyntheticForward.Compute`'s parity-implied "S", fed into this codebase's existing
`BlackScholes.Calculate` (`dividendYield=0`) convention, is mathematically equivalent to Black-76
priced off that same expiry's own forward — no second pricing formula needed, only the correct
underlying reaching the one already in use. Null (never a fallback to the future) when the parity
solve can't run yet that cadence for that expiry, matching this codebase's "never fabricate"
convention everywhere else. The near-week-only synthetic forward already computed for
`AtmStrikeBySyntheticForward` on the parent `CadenceContext` table is untouched — this is a
separate, additive computation, run once per tracked expiry (near-week AND next-week), specific to
this table's own Greeks.

Deliberately dropped from production's field list: `Rho` (never read anywhere in live scoring),
`TheoreticalPrice`/`PriceVsTheoretical` (that richness diagnostic was investigated and closed as
not replicating out-of-sample — see `REVIEW_FINDINGS.md`'s research thread). Trivial to add back
if a future metric needs them.

## Table 3 — `StrikeBandCadenceSnapshot` (5 or 15-minute, configurable)

One row per (bucket timestamp, `CadenceMinutes`, expiry, band). Rolls up table 2's 15-second rows
across two dimensions at once -- every strike in the named band, and every 15-second cadence inside
the bucket window. Call-side and put-side columns sit side by side in the same row, since almost
every interesting metric here is a call-vs-put comparison.

```csharp
public sealed class StrikeBandCadenceSnapshot
{
    public long Id { get; set; }

    public required long CadenceContextId { get; set; }        // FK to the CadenceContext row at this bucket's boundary timestamp

    public required DateOnly AsOfDate { get; set; }
    public required DateTimeOffset Timestamp { get; set; }     // bucket boundary (e.g. every 5th or 15th minute)
    public required int CadenceMinutes { get; set; }           // 5 or 15 today, not hardcoded -- the only table carrying this column

    public required DateOnly ExpiryDate { get; set; }
    public required int DaysToExpiry { get; set; }

    public required string BandDefinition { get; set; }        // "Strike3" / "Strike5" / "Strike7" / "Itm2Atm1" -- see band table below
    public required int CallStrikeCount { get; set; }          // strikes actually contributing this cadence (chain gaps happen near open/close)
    public required int PutStrikeCount { get; set; }

    // Starter metric set only -- deliberately minimal, more columns land metric-by-metric once
    // this structure is agreed, per the standing evaluation process.
    public long? CallVolumeSum { get; set; }
    public long? PutVolumeSum { get; set; }
    public decimal? CallNotionalSum { get; set; }               // Sum(MarkPrice x VolumeDelta)
    public decimal? PutNotionalSum { get; set; }
    public long? CallOiChangeSum { get; set; }
    public long? PutOiChangeSum { get; set; }
    public long? CallOiSum { get; set; }           // (2026-09-12) total OI level (not change), band boundary cadence only -- for PCR-OI (Call OI / Put OI) and volume/OI turnover ratio candidates
    public long? PutOiSum { get; set; }
    public double? CallAvgIv { get; set; }                      // OI-weighted, see decision #2 above
    public double? PutAvgIv { get; set; }

    // Two-dimensional rollup of StrikeCadenceSnapshot's own CVD proxy fields: summed across every
    // strike in the band AND across every 15s cadence inside this bucket -- net DIRECTION, distinct
    // from CallNotionalSum/PutNotionalSum above which are blind totals (how much traded, either
    // way). Both are kept: one answers "how much activity," the other "which way it leaned."
    public long? CallCvdProxyVolumeNet { get; set; }
    public long? PutCvdProxyVolumeNet { get; set; }
    public decimal? CallCvdProxyNotionalNet { get; set; }
    public decimal? PutCvdProxyNotionalNet { get; set; }

    // (2026-09-12) Plain average of DepthImbalanceFromLastCadence across strikes-in-band and
    // cadences-in-bucket -- resting liquidity, not executed flow. Unweighted, unlike CallAvgIv's
    // OI-weighting (no established reason yet to weight this by OI specifically).
    public double? CallDepthImbalanceAvg { get; set; }
    public double? PutDepthImbalanceAvg { get; set; }
}
```

## Band definitions

| `BandDefinition` | Calls: `StrikeOffsetFromAtm` in | Puts: `StrikeOffsetFromAtm` in | Strike count |
|---|---|---|---|
| `Strike3` | {-1, 0, +1} | {-1, 0, +1} | 3 |
| `Strike5` | {-2, -1, 0, +1, +2} | {-2, -1, 0, +1, +2} | 5 |
| `Strike7` | {-3, -2, -1, 0, +1, +2, +3} | {-3, -2, -1, 0, +1, +2, +3} | 7 |
| `Itm2Atm1` | {-2, -1, 0} (ITM is below ATM for calls) | {0, +1, +2} (ITM is above ATM for puts) | 3 |

`Itm2Atm1` is the only asymmetric, per-option-type-mirrored band — its call-side and put-side rows
are built from genuinely different strike ranges, unlike the other three which are symmetric and
identical for both sides. Motivation (your own): real positioning skews toward buying ITM and
selling/writing OTM, so a band that reflects that skew is worth having as its own view rather than
forcing everything into ATM±N.

## What's intentionally not decided here

Per your own instruction earlier this session ("we will keep building columns metric by metric
once complete schema is finalized"), table 3's metric columns above are a *starter set*, not an
exhaustive one — volume, notional, OI change, CVD net, and average IV per side, enough to get the
tables populated and validated. Every further metric (skew ratios, spread aggregates, Greeks
rollups, etc.) goes through the same evaluate-then-decide cycle as every parent-table metric before
it gets its own column, rather than being guessed wholesale now.
