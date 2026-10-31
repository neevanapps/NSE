# Market Summary – metric aggregation contract

Branch `feature/market-summary-paper-engine-v2`. Implementation: `NiftySignal.MarketSummary` (pure, no I/O). Tests: `NiftySignal.Tests/MarketSummary`. Not deployed.

## 1. Scope and invariants

* Five rows per grid: **5 Bars, 10 Bars, 50 Bars, 100 Bars, Day**. Completed bars only. For a target completed bar `N` a window of `k` bars is bars `N-k+1 .. N`; **Day = bars 1 .. N** (session start through the pinned boundary). Nothing after `N` is read.
* Fewer than `k` completed bars ⇒ the row is **INSUFFICIENT DATA** (`— – INSUFFICIENT DATA`), never zero and never a shorter window.
* **One fixed basket** for all five windows: one centre `K`, one expiry, strikes `K-2Δ..K+2Δ` (Δ = gap to the next listed strike above `K`, the one below if none), exact CE and PE tokens. History is never re-classified by each bar's own centre.
  * Auto Follow ON: when a new completed bar changes the centre the basket is re-resolved once and every window is recomputed. Manual pin: the basket stays fixed. A historical (pinned) capture uses the centre of the target bar `N`, never a later one.
* Classification: CE – ITM at `K-2Δ, K-Δ`; ATM at `K`; OTM at `K+Δ, K+2Δ`. PE – OTM at `K-2Δ, K-Δ`; ATM at `K`; ITM at `K+Δ, K+2Δ`.
  * ATM grid = centre CE+PE (**2**); ITM = lower two CE + upper two PE (**4**); OTM = upper two CE + lower two PE (**4**); TOTAL = all **10**.
* **Contract inclusion:** a contract counts in a window only when a stored row exists for **every** bar of the window (no bridging, no substitution of another strike / expiry / token). Coverage = included / expected: `10/10 – FULL`, `8/10 – PARTIAL`, `— – INSUFFICIENT DATA`. Unresolved basket slots (strike not listed / side missing) are reported in the coverage's missing list.
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
