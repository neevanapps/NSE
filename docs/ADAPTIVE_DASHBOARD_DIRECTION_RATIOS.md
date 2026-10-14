# Adaptive Dashboard: direction + ratio grids (2026-10-14)

Branch `feature/adaptive-dashboard-direction-ratios` (from master `c5386f5d94899c5938f78f5d23a184144a5357a6`). Display + additive observational sidecar only. NOT deployed, NOT merged.

## Cell convention

`raw (ratio)` where ratio = Numerator / Denominator × 100, only when the denominator is valid (> 0). Unavailable raw → `—`; unavailable ratio → `raw (—)`. Nothing is ever shown as NaN/Infinity or a placeholder 0. Code: `NiftySignal.Dashboard/Services/AdaptiveRatioGrid.cs` (pure, unit-tested).

## Futures grid (default view)

Removed from the default view (still in Diagnostics): Roll Strict, Roll Enriched, |Strict| Δ.

| Column | Raw | Ratio |
|---|---|---|
| Strict Δ | StrictDelta | StrictDelta / Volume (= existing `StrictDeltaRatioTotal`) |
| Enriched Δ | EnrichedDelta | EnrichedDelta / (EnrichedBuy + EnrichedSell + EnrichedUnknown) |
| MicroDev | time-weighted MicroDev | time-weighted TOB × 100 (MicroDev = Spread/2 × TOB) |
| OFI | NetOFI | NetOFI / GrossOFI, GrossOFI = SUM(\|e_i\|) over the same valid transitions; GrossOFI = 0 or no transition → unavailable |

AVG (visible rows, last row): continuous columns = arithmetic mean of available values; Strict/Enriched ratio = SUM(Δ)/SUM(denominator); MicroDev raw and ratio = weighted by `MicroDevUsableSeconds` / `TobUsableSeconds`; OFI raw = mean of NetOFI, OFI ratio = SUM(net)/SUM(gross); State/Evolution = `—`. Missing values are excluded, never zero.

## Fixed-strike Options (identity unchanged: one expiry, one strike, exact CE + PE token, Auto follow / manual pin as before)

CORE DIRECTION (every cell `raw (ratio)`; Direction = CE − PE except OI = PE − CE; every direction ratio uses the COMBINED denominator, never CE ratio − PE ratio):

| Metric | CE / PE ratio | Direction raw | Direction ratio |
|---|---|---|---|
| Price | ΔPrice / PriceStart (start > 0) | CE Δ − PE Δ | (CEΔ − PEΔ) / (CE start + PE start) |
| Strict | StrictDelta / TotalQuantity | CE − PE | (CE − PE) / (CE total + PE total) |
| Enriched | EnrichedDelta / (buy+sell+unknown) | CE − PE | (CE − PE) / (CE den + PE den) |
| OI | OiDelta / OiStart (start > 0) | **PE − CE** | (PEΔ − CEΔ) / (PE start + CE start) |
| MicroDev | TOB × 100 | CE MicroDev − PE MicroDev | (CE TOB − PE TOB) / 2 × 100 |
| OFI | NetOFI / GrossOFI | CE net − PE net | (CE net − PE net) / (CE gross + PE gross) |

OPTION CONTEXT (same strike/tokens/bars): IV (ΔIV / IV start; direction (CEΔ − PEΔ) / ((CE IV0 + PE IV0)/2), start IV must be > 0), Activity (each side's share of CE+PE; direction (CE − PE)/(CE + PE), total ≤ 0 → unavailable).

AVG (visible rows) on both grids: raw = arithmetic mean; ratio = SUM(numerator)/SUM(denominator) of the visible rows that have both (never an average of percentages). MicroDev uses TOB-usable-seconds weighting; MicroDev Direction uses only bars where both sides exist, weighted by the usable overlap = min(CE usable s, PE usable s) (deterministic, documented choice). Bars with no stored row are excluded from each metric (the label shows the visible row count).

Residual grid: AVG row only (arithmetic mean of available numeric columns; Direction / Relation = `—`). Residual identity and formulas untouched.

Dashboard prefers the latest supported sidecar version and falls back (per bar) to the previous one: futures `futures-micro-v3` → `v2` (v2 has no GrossOFI → OFI ratio `—`), strike `option-strike-v2` → `v1` (v1 has no book metrics → MicroDev/OFI `—`).

## Persistence

* `futures-micro-v3` (was v2): adds `GrossOfi` to `adaptive_futures_supplemental_bars`; every other value identical to v2. v2 rows are never rewritten (unique index includes MetricsVersion, so v2 and v3 rows coexist). The commentary projection reads `FuturesMicrostructureBar.MetricsVersion`, so it now reads v3 rows (same values + GrossOfi); on a Host restart recovery writes v3 rows for today's bars before they are consumed.
* `option-strike-v2` (was v1): same table `adaptive_option_strike_bars`, new nullable columns `TobTimeWeighted`, `MicroDevTimeWeighted`, `TobUsableSeconds`, `NetOfi`, `GrossOfi`, `OfiTransitions`, taken from the SAME per-token `FuturesMicrostructureTracker` the observer already feeds (`_optionBooks`) over the same bar boundaries — no second tick pipeline, no DB read per option. v1 rows stay untouched.
* Migration `20261014045007_AddStrikeBookMetricsAndFuturesGrossOfi` (additive: 7 × `ADD COLUMN`, nullable, no ALTER/DROP/TRUNCATE). Idempotent SQL: `NiftySignal.AdaptiveObserverData/Sql/adaptive-strike-book-metrics.idempotent.sql`. Target database `niftysignal_adaptive_observer`. Requires `20261013140636_AddOptionStrikeBars` (already applied on the VM). Host `Migrate()` also applies it at start-up.

## Performance (local PostgreSQL, synthetic full session: 504 bars, 216k ticks)

* Rows/bar = universe size (40 on the VM today); rows/day ≈ 40 × ~163 bars ≈ 6.5k (v2).
* Engine overhead of the per-strike projection incl. books: ≈ +0.55 ms/bar at 10 contracts (≈ +2 ms/bar at 40) — negligible vs bar spacing.
* Persist (40 rows/bar batch insert, local PG): median 8.5 ms, p95 21 ms.
* Fixed-strike query (15 bars, ~20k rows in table): median 2.1 ms (first-call connection warm-up ~200 ms).

## Tomorrow's deployment (owner)

1. Merge + deploy Host and Dashboard (restart required for both: Host writes v3/v2 sidecars, Dashboard has new grids).
2. Apply `adaptive-strike-book-metrics.idempotent.sql` to `niftysignal_adaptive_observer` BEFORE the Host restart (or let the Host migrate at start-up).
3. After restart check: Host log has no strike/futures sidecar mismatch or failure warnings; `SELECT "MetricsVersion", count(*), count("GrossOfi") FROM adaptive_futures_supplemental_bars WHERE "SessionId" = <today> GROUP BY 1` shows v3 rows with GrossOfi; `SELECT count(*), count("NetOfi"), count("TobTimeWeighted") FROM adaptive_option_strike_bars WHERE "SessionId" = <today> AND "MetricsVersion" = 'option-strike-v2'` grows by ~40/bar; Dashboard shows the compact Futures grid + AVG row, CORE DIRECTION + OPTION CONTEXT, Residual AVG; ATM±2 grid still under Diagnostics.
