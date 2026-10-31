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
