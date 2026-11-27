# Dashboard test and browser-harness change inventory

Branch `feature/production-reliability-strategy-enhancement` against `78d9b8b`. All changes are uncommitted. Synthetic data only.

## 1. Cause of the changes

Decision A8 removed three bar-wise grids from the Dashboard (`futures-compact`, `options-core-direction`, `options-context`) and replaced the paper panels on Home with two grids. The calculations and persisted data behind the removed grids are unchanged.

Rule applied: an assertion about a **figure** moved to the service or projection that produces it; an assertion about **markup of a removed grid** was dropped.

## 2. `Dashboard/AdaptiveCompactGridTests.cs`

| Test | Change | Where the producer assertion is now |
|---|---|---|
| default compact grid headers | 3 tables → 2; removed grids asserted absent | headers of the two retained grids unchanged |
| `CompactFuturesGrid_…` → `RemovedFuturesGrid_ItsDataStillLoads_…` | rewritten | urgency (zero duration unavailable, 20.0), rolling OI change, sidecar OFI, version filtering: from the snapshot |
| options / residual grid cells | table index only | unchanged |
| invalid-supplemental withholding | futures cells → snapshot | sidecar withheld, rolling value untouched |
| fixed-strike view | removed core / context grids | Strict, Enriched, OI (PE − CE), OFI, MicroDev, Price, IV, Activity, Delta, visible-rows AVG, same-strike equality: from `OptionDirectionRatios` |
| Delta / Vega version handling | HTML → history | v3 values, v2 unavailable (never zero), AVG excludes rows without Greeks |
| Delta direction theory and mixed-availability cases | HTML → history / day data | raw CE − PE, sign, no negative zero, AVG and DAY AVG exclusion rules |
| DAY AVG tests (live, context, pinned capture) | HTML → day data | aggregate numerator / denominator, bar count, other strikes excluded, nothing after the target bar |
| capture grid set and order | 8 grids → 5 | market summary ×4 and residual; `data-end-bar` equals the job's bar |
| research / quote-panel tests | sentinel grid changed to a retained one | unchanged intent |

Dropped as markup of removed grids: their header lists and column order, AVG / DAY AVG row position and "10 rows" label, `futures-avg` / `options-*-avg` test ids, per-cell sign tint and `data-direction-sign` counts, and the **display-only inversion** of IV / Vega / MicroDev in those grids (the market-summary grids keep their inversion and its tests; raw values are now asserted here). The `data-strike` attribute check became `data-end-bar` / the fixed-strike header text.

Restored during this review (they had been lost in the first rewrite): MicroDev, Price, IV raw values for the seeded bar and the two-bars-same-strike equality. 46 of 46 tests in this class pass.

## 3. Other changed Dashboard and related tests

| File | Change | Reason |
|---|---|---|
| `Dashboard/ResearchDashboardTests.cs` | renderer requires 5 grids, the removed 3 are forbidden, target-bar check added; the "residual may start before target" flag assertion went with the removed field | A8 |
| `MarketSummary/DashboardSimplificationTests.cs` | six-named-grids test → five | A8 |
| `MarketSummary/MarketSummarySectionRenderTests.cs` | dropped the source check for the removed grid's own inversion calls; market-summary inversion check kept | A8 |
| `PaperEngineV2/HomeV2Tests.cs` | Home must contain the two-grid panel and none of the earlier V2 panels; required grids 8 → 5 | A8 |
| `PaperEngineV2/AlertTests.cs` | "no partial event type exists" replaced by assertions on the partial-exit and failed-liquidation alerts | partial exits and 15:10 liquidation are approved behaviour |
| `PaperEngineV2/ExecutionTests.cs`, `TotalStrategiesTests.cs` | no exit at 15:00 (entries stop); liquidation 15:08, failure report at 15:10 | approved time rules |
| `PaperEngineV2/IsolationReleaseBlockerTests.cs` | ExecutionService may reference the V2 Runtime project, as the Host may | A1 |
| `PaperEngineV2/HostReplayTests.cs` | registers the live sink interface | Observer isolation |
| `AdaptiveObserver/AdaptiveMigrationPostgresTests.cs`, `OptionsDepth5HostBenchmark.cs` | database name prefixes and one shared collection | leftover-database check raced with other classes |

## 4. Browser harness `tools/AdaptiveDashboardValidation`

It no longer matched the Dashboard. Some mismatches predate this branch (the `.adaptive-current-bar` element and the residual grid's average rows; judged from source, the base harness was not run). The restart-parity check built its uninterrupted engine without `projectStrikes`, which the recovery service has always set; it now configures both alike and prints the first differing value on failure.

Changed:
- Removed assertions on the removed futures grid (headers, urgency, MicroDev / OFI / ΔBasis cells, its diagnostic header). Table counts 3 → 2 (options ATM±2 inside the collapsed diagnostics, and residual). Average rows are excluded from row counts. The capture page also expects the four summary grids and no live-only tables.
- Current bar progress is read from the quote panel (`incomplete-bar-qty`).
- **Weakened:** the selector race no longer queries server activity views to prove a read was blocked; it holds the table lock for a fixed delay. The end state (latest selection wins) is still asserted.
- Added (`CurrentDashboardValidation.cs`, `SyntheticSession.cs`, `PaperFixture.cs`): four summary grids with values; price averages (Avg5 / Avg10, stale and fresh quote); exactly two paper grids with one row per strategy; live bid / unrealized / partial / total; pending Avg5; stale quote without a profit figure; closed accounting without double counting; execution condition, silent-service and recovery banners; Observer rebuilding, failed and live text; fixed-clock capture page.
- Refuses to start beside an `appsettings.Local.json`; health-monitor Telegram messages are off; uploads go to the loopback emulator only.
- The added part runs only on an IST weekday after 09:40, otherwise it is printed as `NOT RUN` (a CI run outside that window does not cover it).
- The paper part is fed by the production stores, not by a running execution service.

### Local result (26-Nov-2026, scratch PostgreSQL 18, installed Edge, headless)

PASS: PostgreSQL restart parity, late start, sidecar degradation, live quote, 36 selector combinations, commit-to-grids p95 about 0.17–0.19 s (developer machine, not a VM figure), readiness, market summary, price averages, paper grids, health banners, recovery text.

That run used Edge, so screenshot delivery and both capture pages were `NOT RUN`. A first Chromium run the same day failed at the newest bar (F77). The result after the corrections is in section 6.

## 5. Finding raised

**F76** (fixed in the final release preparation): the price-averages table read LTP / bid / ask / age only when the grid reloaded, so the age text froze between completed bars and could read "fresh" when it was not. Display only; present before this branch. The live page now re-reads the quotes at most once a second without a new bar.

- Added `OptionPriceAverages_OnTheLivePage_TheQuoteAgesAndANewQuoteAppears_WithoutANewBar` (`AdaptiveCompactGridTests.cs`).
- The harness no longer reloads the page to pick up the new quote.

## 6. Capture correctness (F77, F78) and full capture run (26-Nov-2026)

Tests (`Dashboard/AdaptiveCompactGridTests.cs`):

| Test | Change |
|---|---|
| `Capture_ContainsEveryRequiredPrimaryGridAndBlock_ExactlyOnce` | the capture must now **not** contain the "fixed strike history" heading or its row note (was: heading present). The live-page test still requires the section |
| `Capture_LatestCompletedBar_RendersAllRequiredGridsFromItsStoredCloseAtm_WithoutAnyLaterRow` | added |
| `CapturePage_SubtitleNamesTheCurrentGrids_AndNoRemovedOne` | added |
| `CapturePage_PreLiveTestWithoutASession_ClaimsNoGridAndNoMarketBar` | added |

Harness:
- `CaptureFixture.cs` (new): strikes CE / PE 22850-23150, per-bar rows and close ATM written by the Observer's writers; expected TOTAL cells as closed forms of the fixture numbers (derived from `MARKET_SUMMARY_METRICS.md`, not from the calculator), with hand-worked anchors.
- A bar's sidecars are stored before it is announced complete. Fixture bars never end after the wall clock.
- Added: first-attempt and caption check per delivery, newest bar without a later one, all job pages after later bars exist, pre-live page, Option Price Averages values, unavailable-then-retried capture without duplicate; fixed-clock page checks the job's bar, subtitle, five grids and values.
- Corrected stale expectation: the pinned capture page has one non-summary table (residual), not two.
- No check was removed or relaxed to make the run pass.

Result with Playwright's Chromium (loopback emulator, scratch PostgreSQL, synthetic data): exit 0, no `NOT RUN` line, `screenshotCaptureRun=true`, `currentDashboardChecksRun=true`. Focused tests (`Tests.Dashboard`, `MarketSummary`): 354 passed, 0 failed, 1 skipped. The full Release suite was not rerun after these changes. CI not run.

Not covered by these fixtures: residual values, option Depth-5 columns; the fixed-clock fixture leaves Day IV / Delta / Vega blank (5 of 10 contracts valid for Price and OI, none for the Greeks).
