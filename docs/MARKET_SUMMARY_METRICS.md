# Market Summary – metric aggregation contract

Branch `feature/market-summary-paper-engine-v2`. Implementation: `NiftySignal.MarketSummary` (pure, no I/O). Tests: `NiftySignal.Tests/MarketSummary`. Not deployed.

## 1. Scope and invariants

* Five rows per grid: **5 Bars, 10 Bars, 50 Bars, 100 Bars, Day**. Completed bars only. For a target completed bar `N` a window of `k` bars is bars `N-k+1 .. N`; **Day = bars 1 .. N** (session start through the pinned boundary). Nothing after `N` is read.
* Fewer than `k` completed bars ⇒ the row is **INSUFFICIENT DATA** (`— – INSUFFICIENT DATA`), never zero and never a shorter window.
* **One fixed basket** for all five windows: one centre `K`, one expiry, strikes `K-2Δ..K+2Δ` (Δ = gap to the next listed strike above `K`, the one below if none), exact CE and PE tokens. History is never re-classified by each bar's own centre.
  * Auto Follow ON: when a new completed bar changes the centre the basket is re-resolved once and every window is recomputed. Manual pin: the basket stays fixed. A historical (pinned) capture uses the centre of the target bar `N`, never a later one.
* Classification: CE – ITM at `K-2Δ, K-Δ`; ATM at `K`; OTM at `K+Δ, K+2Δ`. PE – OTM at `K-2Δ, K-Δ`; ATM at `K`; ITM at `K+Δ, K+2Δ`.
  * ATM grid = centre CE+PE (**2**); ITM = lower two CE + upper two PE (**4**); OTM = upper two CE + lower two PE (**4**); TOTAL = all **10**.
* **Contract inclusion (per metric family, see section 5):** FLOW metrics (volume, strict, enriched, OFI, MicroDev, activity) include a contract only when a stored row exists for **every** bar of the window; LEVEL metrics (price, IV, Delta, Vega, OI, i.e. end minus start) need only the window's **first and last** bar. No bridging, no zero-fill, no substitution of another strike / expiry / token. Coverage shows both: `10/10 – FULL`, `9/10 – PARTIAL (levels 10/10)`, `— – INSUFFICIENT DATA`. Unresolved basket slots (strike not listed / side missing) appear in the coverage's missing list.
* A metric a stored row version does not carry (OFI / MicroDev on v1; Delta / Vega on v1/v2) is **unavailable (null / "—"), never zero**. The per-bar version choice (v3 > v2 > v1, one row per bar+contract) is made by the mapper.
* Direction = **CE − PE** for Price, Roll, Strict, Enriched, MicroDev, OFI, IV, Delta, Vega, Activity; **PE − CE for OI**. Direction ratios use the **combined numerators / denominators**, never a subtraction of displayed percentages.

## 2. Catalogue (per contract over a window → side aggregate over included contracts)

| Metric | Source field(s) (`adaptive_option_strike_bars`) | Raw unit | Window aggregation (per contract) | Cross-contract (CE side / PE side) | Ratio (`Num/Den×100`) | Direction | Missing data |
|---|---|---|---|---|---|---|---|
| Volume | `TotalQuantity` | contracts qty | Σ | Σ | – | – | – |
| Strict | `StrictDelta`, `TotalQuantity` | qty | Σ delta | Σ | `ΣStrictDelta / ΣTotalQuantity` (pooled) | CE−PE; ratio = (ΣCE−ΣPE)/(ΣDenCE+ΣDenPE) | den 0 ⇒ ratio n/a |
| Enriched | `EnrichedDelta`, `EnrichedBuy+Sell+Unknown` | qty | Σ | Σ | `ΣDelta / Σ(B+S+U)` | as Strict | den 0 ⇒ n/a |
| OFI | `NetOfi`, `GrossOfi` (v2+) | qty | Σ over bars with both present | Σ | `ΣNet / ΣGross` (Gross = Σ\|valid contributions\|, never \|net\|) | as Strict | no valid bar ⇒ n/a; invalid-book boundaries already break/rebaseline upstream |
| MicroDev | `MicroDevTimeWeighted`, `TobTimeWeighted`, `TobUsableSeconds` (v2+) | price units | usable-seconds weighted | usable-seconds weighted over all contract-bars | `Σ(TOB·w)/Σw` (= TOB·100; MicroDev = Spread/2·TOB) | raw CE−PE; ratio (TOBce−TOBpe)/2·100 | `w ≤ 0` bars skipped |
| Price | `PriceStart` (first bar), `PriceEnd` (last bar) (mid) | price | `end − start` | arithmetic mean of valid contracts | `ΣChange / ΣStart` (start > 0) | CE−PE; ratio (ΣCE−ΣPE)/(ΣStartCE+ΣStartPE) | start ≤ 0 or null ⇒ contract invalid for Price |
| Roll ΔPx | `PriceEnd(b) − PriceStart(b−9)` per bar b (exact-strike ten-bar roll) | price | mean of valid per-bar observations in the window (valid only if all ten bars b−9..b exist, start > 0; history before the window is read; **no bridging**) | mean of contracts' means | `ΣRoll / ΣRollStart` over all observations | as Price | no valid observation ⇒ n/a |
| IV | `IvStart`, `IvEnd` (vol points) | IV pts | level = end IV; change = `end − start` | level and change: mean of valid contracts | `ΣChange / ΣStartIV` | CE−PE; ratio over the **average** of the two combined starts (existing convention) | start ≤ 0 ⇒ unavailable |
| OI | `OiStart`, `OiEnd` | qty | `OiEnd(last) − OiStart(first)` | Σ of valid changes | `ΣChange / ΣStartOI` | **PE − CE**; combined valid start OI | start ≤ 0 / null ⇒ contract invalid |
| Delta | `DeltaStart/End` (v3, raw) | raw delta | level = end, change = `end − start` | mean of valid contracts | `ΣChange / Σ\|start\|` | CE−PE over average \|start\| | null ⇒ unavailable |
| Vega | `VegaStart/End` (v3, per 1.00 vol) | raw vega | as Delta | as Delta | as Delta | as Delta | as Delta |
| Activity | `TotalQuantity` ÷ bar duration | qty / s | `Σqty / Σ valid elapsed seconds` (bars with duration > 0); **never a sum of per-bar rates over time** | Σ of contracts' rates | – | (CE−PE) / (CE+PE) | no valid elapsed time ⇒ n/a |

**TOTAL** (all ten): additive and flow metrics are summed, ratios are pooled (ratio of sums), MicroDev is time-weighted, price / IV / Delta / Vega are **averaged across valid contracts (never summed)**, activity is summed.

**Delta / Vega display:** stored and calculated raw. The Dashboard shows `Delta ×100` / `Vega ×100` (0.42 → 42, 0.018 → 1.8) through `DisplayScale.Times100` – display only; thresholds, storage and broker payloads are untouched. (Vega is per 1.00 vol, so "×100" of the stored value is the display convention requested; it is not "per vol point".)

## 3. Futures summary (single contract, `adaptive_future_bars` + `adaptive_futures_supplemental_bars`)

Same windows / completeness rules; a missing stored bar ⇒ PARTIAL (values from the bars present). Price = `Close(last) − Open(first)` (ratio over first open); Volume Σ; Strict `ΣStrictDelta/ΣVolume`; Enriched `ΣDelta/Σ(B+S+U)`; OI = `OiClose(last) − OiOpen(first)` over first OI; MicroDev usable-seconds weighted; OFI `ΣNet/ΣGross` (v3 sidecar only); Activity = `ΣVolume / Σduration`. India VIX (Phase 3) is added to this grid separately.

## 4. Known limits

* A contract with a hole anywhere in the window drops out of that window entirely (conservative); coverage says so.
* Direction for multi-contract groups assumes the same contract count on both sides; with unequal counts (partial coverage) the numerator/denominator ratio is still exact (ratio of sums) while the raw direction compares means – coverage marks that case PARTIAL.
* Day on a session that started late (`ObservationStartUtc` after 09:15) covers the stored bars from bar 1.

## 5. What a missing contract bar means (audited from source, 2026-10-31)

Evidence: `AdaptiveStrikeProjection.Build`, `AdaptiveObserverEngine` (strike projection), `AdaptiveSupplementalPersistence.PersistOrVerifyStrikesAsync`, `AdaptiveStateRecoveryService`, `AdaptiveObserverWorker`.

| Possible meaning | Established by source? |
|---|---|
| (b) "no trade / no activity, row omitted" | **No.** `Build` iterates the WHOLE frozen universe every completed bar and emits a row for every contract, including zero-activity ones (`TotalQuantity = 0`, deltas 0, price/IV/OI null when there is no evidence). A silent contract has a row; a row's absence is not "no trades". |
| (c) "contract not eligible this interval" | **No.** The universe (`OptionUniverseJson`: every option of the nearest expiry) is frozen once per session; eligibility never varies per bar. A contract outside the universe makes the basket slot unresolved (reported, never substituted). |
| (a) "expected bar not captured / persisted" | **Yes - this is the only way a row is absent.** Rows of one bar are inserted with one `AddRange` + one `SaveChanges` (atomic per bar), so a partial set within a bar is not produced by the code; a whole bar is absent when its sidecar write failed (failures are swallowed after logging because the strike table is a display sidecar; the worker retries unverified bars while it runs), when the bar predates the strike sidecar (v1 introduced 2026-10-13) or the session ran without strike projection (historical seed), or after manual damage. A restart replays the bars and inserts any missing rows (mid-session backfill), so gaps self-heal on the next recovery. A stored-vs-recomputed mismatch leaves the stored rows untouched. |
| (d) other | Version mix: one bar can carry v2 and v3 rows after a replay backfill; the reader takes v3 > v2 > v1 per bar. Column nulls inside a row (no fresh quote, no book, no IV) mean "no evidence in that bar", which is different from a missing row. |

Consequences, implemented and tested:

1. **A missing row is a data-integrity gap, never a zero.** Within existing rows, `TotalQuantity = 0` etc. are real zeros (cumulative-counter differences with a complete universe), and are summed as zeros.
2. **Flow metrics are additive over bars, so a hole makes the window sum unknowable:** the contract is excluded from the flow metrics of that window (not partially summed, not zero-filled); coverage `Included` falls.
3. **Level metrics telescope:** the end of bar `k` equals the start of bar `k+1` (same cumulative snapshot), so `end(last) − start(first)` is exact across an interior hole. They are computed from the first and last bar and need no interior row. A hole at the first or last bar removes the contract from the window entirely. Roll ΔPx requires all ten bars of each observation (no bridging).
4. Coverage reports expected vs observed for both families (`LevelIncluded`), so a hole is visible instead of silently averaged away. When a window has no contract with the required rows the row is `— – INSUFFICIENT DATA` / the metric is unavailable.
5. This section refines an earlier draft ("a contract with any hole drops out of the whole window"): that was correct for flow metrics but needlessly discarded level information. The aggregation formulas themselves (sections 2-3) are unchanged.

## 6. India VIX in the Futures Summary

* **Source:** the raw `ticks` table, instrument-master row of type `Vix` (token `26017`, `InstrumentUniverseResolver.IndiaVixToken`; the Dashboard resolves it from the master for the session date and refuses a missing or non-unique row). Already ingested and subscribed; nothing is added to the subscription path, nothing is persisted, and no futures order-flow calculation is involved. The observer does not read it.
* **Alignment (causal):** an observation's availability is `max(ExchangeTimestamp, ReceivedAt)`. The point at an instant is the latest valid (finite, > 0) observation with availability `<=` that instant - never a later one. The read is one scoped range query that excludes anything exchanged or received after the boundary.
* **Instants:** end = `EndAvailableAtUtc` of the target completed bar; start = `StartAvailableAtUtc` of the first bar of the window (Day: bar 1 = the session start). Level = value at the end; Change = end - start; Change % = change / start x 100 (start > 0).
* **Freshness limit: 60 s, PROVISIONAL.** Derived from the real cadence (local copy, 23 sessions 2026-09-04..2026-10-09, 09:15-15:30 IST, distinct exchange timestamps): median gap 2-3 s, p95 7-22 s, p99 13-46 s, largest gap 35-336 s. 60 s exceeds every sampled session's p99; it is deliberately not the 5 s spot rule. The limit is inclusive. To be re-derived as sessions accumulate.
* **Missing data:** an observation older than the limit gives a Stale point (value withheld, age shown), none at all gives Missing; Change and % need BOTH points usable. Nothing is backfilled, interpolated, carried across a gap, or guessed; no fabricated history. A failed or unconfigured VIX read leaves the rest of the summary untouched and says why in the cell title.
* **Pinned screenshots:** the same rule at the target bar's boundaries, so no future observation can appear.

### 6.1 Freshness evidence (validation, local copy, 22 sessions 2026-09-04..2026-10-09)

Instrument mapping confirmed: `instruments` rows for token `26017` = "India VIX", `InstrumentType.Vix`, underlying NIFTY, present on all 32 as-of dates of the local copy (exactly one per date). Share of market time (09:00-15:30 IST) that would be reported **stale** for a given limit, i.e. time spent more than the limit past the last distinct exchange timestamp:

| limit | worst session | median session | sessions since 2026-09-29 |
|---|---|---|---|
| 30 s | 9.7 % (09-04) | ~1.7 % | 0.03-0.8 % |
| **60 s (policy)** | 3.5 % (09-04), 2.7 % (09-21) | ~0.2 % | 0.00-0.03 % |
| 120 s | 1.1 % (09-21) | 0 % | 0.00 % |

The feed was visibly sparser in September (p99 gap 25-46 s) than in the October sessions (p99 13-20 s), so 60 s is justified by the recent cadence and exceeds every session's p99 gap, but an early-September-style feed would show the VIX unavailable for a few percent of the day. **Recommendation (not applied):** keep 60 s; if a dense-feed assumption is judged too tight, 120 s would cut the worst-session stale share to about 1 % at the cost of calling a two-minute-old VIX current. The policy is a constant (`VixSummaryCalculator.FreshnessLimit`) and was not changed. Note a gap can also mean the value genuinely did not change (no tick is a possible quiet market), which a feed with no heartbeat cannot distinguish; stale therefore means "not confirmed within the limit", shown as such.
