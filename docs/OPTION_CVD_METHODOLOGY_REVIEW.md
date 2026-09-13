# Option CVD — Methodology and Findings (review draft, 2026-09-12)

This document exists for external review. It explains, precisely and completely, what "option
CVD" is in this project, exactly how every value is calculated, exactly how it was correlated
against price, and exactly what was found — including a real bug that was caught and fixed along
the way. Nothing here is summarized from memory; every formula is quoted directly from the actual
source and every result is a real query result, re-verified before writing this.

## 1. Background — what problem this is solving

NiftySignal is a Nifty 50 options directional-signal system. The underlying question this specific
piece of work answers: **does classified option order flow (who's aggressively buying vs. selling)
predict short-term price movement?**

This mirrors work already done and validated on the tracked Nifty future (`FutureCvdProxy`), which
showed real forward-predictive correlation. The question here is whether the same technique,
applied to the *option chain itself* rather than the future, shows the same kind of edge.

## 2. Why "CVD" and not just raw volume

Resting order-book depth (how many contracts are waiting on the bid vs. the ask) was tried first
and showed weak, inconsistent correlation — a book can look bid-heavy simply because sellers who
already got their move have stopped offering, not because anyone is buying. The problem: depth
measures who's *waiting*, not who's *trading*.

CVD (cumulative volume delta) instead classifies **executed volume** — every reported trade is
tagged as buyer-initiated ("buy-leaning") or seller-initiated ("sell-leaning") based on where the
price printed relative to the quote, and a running net (buy volume minus sell volume) is kept.

**Important caveat, unchanged from the future's own version**: FlatTrade's feed has no true
per-trade tape. A single reported volume-delta between two ticks can bundle many real trades that
happened in between, possibly in different directions. This is a **proxy** for CVD, not true CVD —
same honest limitation the future-level version already carries.

## 3. Data model — where every number comes from

Three tables, all in a dedicated database (`niftysignal_backtest_analysis`) built from real
historical ticks, never touched by the live trading system:

1. **`CadenceContexts`** — one row every 15 seconds, future/spot/VIX-level data (already existed).
2. **`StrikeCadenceSnapshots`** — new this session. One row per **strike × option type × expiry**,
   at the same 15-second cadence as table 1, for every strike within 3 strikes of the current ATM
   (on both the near-week and next-week option chains simultaneously).
3. **`StrikeBandCadenceSnapshots`** — new this session. Aggregates table 2 into 5-minute or
   15-minute buckets, across a named band of strikes (e.g. "the 3 nearest strikes to ATM").

## 4. Exact classification rule (the "quote rule")

Quoted directly from `CvdProxyAccumulator.ApplyTick` (`NiftySignal.BacktestData/CadencePopulator.cs`):

```csharp
public void ApplyTick(decimal lastPrice, MarketDepth depth, long volumeDelta)
{
    if (depth.Bid1Price <= 0 || depth.Ask1Price <= 0 || volumeDelta <= 0)
    {
        return; // no two-sided quote, or nothing traded this tick -- contributes nothing
    }

    var midpoint = (depth.Bid1Price + depth.Ask1Price) / 2m;
    var isBuyLeaning = lastPrice >= midpoint;

    _cadenceVolumeNet += isBuyLeaning ? volumeDelta : -volumeDelta;
    _cadenceNotionalNet += (isBuyLeaning ? 1 : -1) * volumeDelta * lastPrice;
    _cadenceContributingTicks++;
}
```

In words: for each raw tick, compute the quote midpoint `(bid1 + ask1) / 2`. If the last traded
price sits at or above that midpoint, classify the **entire volume traded since the previous tick**
as buyer-initiated (+volume); otherwise seller-initiated (-volume). A tick contributes nothing (not
a zero — genuinely excluded) if there's no two-sided quote, or if no new volume traded.

**`volumeDelta` itself** — how much volume "traded since the previous tick" — is computed one level
up, in `OptionInstrumentState.ApplyTick`:

```csharp
var volumeDelta = _previousVolume is { } previousVolume ? Math.Max(0, tick.Volume - previousVolume) : 0;
```

The feed reports *cumulative* volume for the day; this diffs against the last-seen cumulative
value for that same instrument, floored at zero to absorb any feed reset. This is the same pattern
already used and tested for the future's own version.

## 5. Two parallel measures: contract-based and notional-based

Both are computed from the exact same classified tick, at the same instant, using the same
buy/sell decision — they only differ in *what's being summed*:

- **Contract-based** (`_cadenceVolumeNet`): sums `±volumeDelta` (lots).
- **Notional-based** (`_cadenceNotionalNet`): sums `±volumeDelta × lastPrice` (rupees) — each
  classified slice valued at the price it actually traded at, not a single closing price.

Rationale for testing both: a cheap, high-lot-count strike and an expensive, low-lot-count strike
can show very different pictures depending on which basis you use. Testing both avoids assuming
one is more meaningful in advance.

These two per-strike, per-cadence values are exposed on `StrikeCadenceSnapshot` as
`CvdProxyVolumeThisCadence` and `CvdProxyNotionalThisCadence`.

## 6. Rollup to the band level (table 3)

Each `StrikeBandCadenceSnapshot` row aggregates a named band of strikes (e.g. "3 nearest to ATM")
over a 5 or 15-minute bucket, separately for calls and puts:

```csharp
CallCvdProxyVolumeNet   = SumOrNull(callRows.Select(r => r.CvdProxyVolumeThisCadence)),
PutCvdProxyVolumeNet    = SumOrNull(putRows.Select(r => r.CvdProxyVolumeThisCadence)),
CallCvdProxyNotionalNet = SumDecimalOrNull(callRows.Select(r => r.CvdProxyNotionalThisCadence)),
PutCvdProxyNotionalNet  = SumDecimalOrNull(putRows.Select(r => r.CvdProxyNotionalThisCadence)),
```

This sums across **two dimensions at once**: every strike in the band, and every 15-second cadence
inside the bucket. `SumOrNull`/`SumDecimalOrNull` return null only if *every* contributing value
was null (no classifiable tick anywhere in the band/bucket); otherwise they sum whatever real
values exist.

**The metric actually tested**: `CVD-diff = CallCvdProxyVolumeNet − PutCvdProxyVolumeNet` (and the
notional equivalent) — a single combined directional read: positive means the call side's
classified buying exceeded the put side's, which we treat as a bullish-leaning reading.

## 7. Correlation methodology — round 1 (against the future's price)

The first pass correlated CVD-diff against the tracked future's own forward price change
(`CadenceContext.FutureChangeFromLastCadence`), because at the time no per-strike price table
existed yet — the future was the only available "did the market move" series.

Method: join each `StrikeBandCadenceSnapshot` row to the `CadenceContext` row at the same
timestamp; compute the future's forward price change over the next 5 and 15 real minutes using a
`RANGE`-based SQL window (`SUM(FutureChangeFromLastCadence) OVER (... RANGE BETWEEN CURRENT ROW AND
INTERVAL '5 minutes' FOLLOWING) - <this row's own contribution>`); correlate.

**Result**: pooled correlation near zero (-0.03 to +0.09 across bands/cadences). Per-day breakdown
showed why — the sign flips across days (e.g. Strike7/5-min/fwd-5m: 08 Sep -0.14, 09 Sep +0.05,
10 Sep +0.29, 11 Sep -0.06). **No reliable relationship found.**

## 8. Correction — why round 1 used the wrong target, and what changed

The future was always a stand-in, adopted only because no option-price series existed. Once the
per-strike table existed, continuing to correlate against the future was no longer justified — the
future's price and an option's own price move together only approximately (delta-scaled, and
diverging further under IV/theta effects), so a signal's relationship with the future doesn't
necessarily match its relationship with the option's own price, which is what actually determines
P&L on an options position.

**Round 2 methodology** — correlate CVD-diff against the ATM option's *own* price, separately for
the call leg and the put leg (since CVD-diff has no single natural "option price" counterpart):

1. Take `MarkPrice` ((bid+ask)/2 — the same quote-based price IV/Greeks are solved from, chosen
   over the traded-price series specifically to avoid last-trade staleness/bounce) at the ATM
   strike (`StrikeOffsetFromAtm = 0`), separately for calls and puts, separately for the near-week
   and next-week chains.
2. **Forward-fill** through cadences where no tick arrived (a standard SQL "gaps and islands"
   technique: `COUNT(MarkPrice) OVER (ORDER BY Timestamp)` — which only increments on a non-null
   value — creates a group id that stays constant until the next real value; `FIRST_VALUE` within
   that group carries the last real price forward).
3. Compute forward price change as a **fixed row offset**, not a time-range window: `LEAD(price,
   20) - price` for +5 minutes, `LEAD(price, 60) - price` for +15 minutes. This relies on
   `StrikeCadenceSnapshot` guaranteeing exactly one row per 15-second cadence once a token starts
   ticking (20 rows = 5 minutes, 60 rows = 15 minutes) — a genuine precondition, not an assumption
   (see the bug in section 9).
4. Correlate CVD-diff against the call's own forward change and the put's own forward change,
   separately. **Coherence check**: a real directional signal should move the two in *opposite*
   directions (bullish → call price up, put price down). If both move the same direction, or the
   sign flips across days, that's evidence against a real signal, not for one.

**Result on clean data**: still weak and unstable. ThisWeek shows call and put moving the *same*
direction (incoherent — no clean story) at both cadences. NextWeek at 15-minute cadence was the one
partial exception — negative on all 4 days (fwd-15m: -0.067, -0.433, -0.153, -0.074) — but on a
much smaller sample (23-25 points/day vs. 72-75 for 5-minute buckets), so weighted as a lead, not a
finding.

**Contract vs. notional**: track each other closely in every cell — same sign, similar magnitude.
The choice between the two framings doesn't change the read for this specific metric (this differs
from what was later found for a different metric, put/call volume ratio, where the contract-based
version was a clear, consistent improvement over notional).

## 9. A real bug found and fixed mid-investigation — full disclosure

While building the corrected (option-price) correlation, the row count came back 8x larger than
expected (2,400 rows where 300 were expected for one cell). Root cause, confirmed directly by
inspection: `PopulateDayAsync`'s idempotency check only looks at `CadenceContexts` — there was no
real database foreign key between it and the two new tables, only an application-level id value.
Every time `CadenceContexts` was deleted and repopulated after a schema change (done twice before
this was caught), the *old* `StrikeCadenceSnapshots`/`StrikeBandCadenceSnapshots` rows were left
behind pointing at now-deleted ids, and the new run added a second copy on top — silent duplication,
compounding with each repopulate.

**Why this mattered here specifically but not everywhere**: correlations that joined back through
`CadenceContexts` (like the round-1 future-price check) were accidentally protected, because the
join condition (`w.Id = b.CadenceContextId`) only ever matched the live copy — the orphaned
duplicate's id pointed nowhere and was silently excluded. The round-2 option-price query computed
its own price series directly from the (still-duplicated) `StrikeCadenceSnapshots`, using
row-offset `LEAD`/`LAG` — and duplicate rows silently corrupted what "20 rows" actually spanned in
real time, breaking the row-per-15-seconds assumption the whole technique depends on.

**Fix**: added a real foreign key with `ON DELETE CASCADE` from both child tables to
`CadenceContext`. Verified directly: after the fix and a clean repopulate, the future-price
correlation reproduced its exact prior numbers (proving it was never actually affected), while the
option-price correlation's row count corrected from 2,400 to the expected 300 and its numbers
changed materially (proving it genuinely had been corrupted before the fix). All numbers reported
in section 8 above are from the post-fix, verified-clean data.

## 10. Where this leaves option CVD

**Not confirmed as a candidate signal, on either target series, on either the contract or notional
framing.** The one thing worth carrying forward rather than discarding: NextWeek at 15-minute
cadence showed a real, if thin (small sample), cross-day-consistent negative relationship with
option price specifically — flagged, not concluded.

For contrast, a different candidate tested afterward with the same rigor (put/call **volume**
ratio, not aggressor-classified CVD) showed a much stronger, more coherent result on the same data
— logged separately, not covered by this document.

## 11. Open questions for review

- Is the quote-rule classification itself (LastPrice ≥ midpoint → buy-leaning) the right choice for
  options specifically, or does the wider typical bid-ask spread on options (versus the future)
  make this cruder here than it is for the future?
- Is `MarkPrice` (quote mid) the right series to correlate against, or would traded price
  (last-traded, forward-filled the same way) tell a different story?
- Given CVD-diff showed no edge but put/call volume ratio did, is there a principled reason
  aggressor-classification would fail where raw activity-balance succeeds, on this specific
  market/instrument? (Working hypothesis, not yet tested: options market-making/writing flow may
  dominate the classified-volume signal in a way it doesn't dominate the raw-volume-ratio signal.)
- Only 4 real trading days underlie every number in this document — every finding here is a
  checkpoint, not a statistically settled result.
