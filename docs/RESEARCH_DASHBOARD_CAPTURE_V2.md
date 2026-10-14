# Research Dashboard + capture v2 (2026-10-14)

Branch `feature/research-dashboard-capture-v2`, started from the checkpoint `f29e166951c6a8bfc9d1623a4b8e05ce476cb336` (`feature/adaptive-dashboard-direction-ratios`), so it contains both bodies of work. Display + observational work only; NOT deployed, NOT merged.

## 1. Fixed 13,650-contract futures clock — `adaptive-fixed13650-v1`

* New model identity (`AdaptiveFixedClockV1`). `adaptive-v1` is never redefined or rewritten; every reader (Host recovery, ended-session recovery, Dashboard, screenshots) accepts both. A session that already exists keeps the model it was frozen with — today's live `adaptive-v1` session is unaffected.
* **Start 09:15 IST.** The session row is frozen as soon as the open has passed and the NIFTY future + weekly options are resolved — it no longer waits for 09:30. Bars accumulate from the first eligible futures tick at/after 09:15; the 09:15–09:30 volume forms bars (nothing is discarded, no time bars). Every completed bar holds exactly 13,650 contracts (exact-volume split semantics unchanged). `BaseBarVolume = 13650`, rolling window 10 bars, no opening-volume estimator is consulted.
* **09:30 residual anchor stays separate.** Session identity is frozen at 09:15 without the anchor. The ENGINE freezes the anchor itself, causally, at the first input after 09:30 from the option quotes and the last futures tick it has already processed (same code live and in replay, so restart-safe and deterministic); it is stored ONCE in the session row + components (fill-once, a stored anchor is verified and never overwritten; on restart the stored anchor is used). Bars that end before 09:30 carry no residual rows (unavailable, never fabricated).
* **Strong/Weak policy (new, versioned with the clock).** The daily Strong threshold = the frozen Strong quantile (2/3) of `|StrictDeltaRatioTotal|` over the rolling states of PRIOR fixed-clock sessions only — never adaptive-v1 rolling states, never today or later. Prior days are rebuilt on the fixed clock from the same persisted raw futures ticks (`AdaptiveHistoricalBootstrapService.EnsurePriorFixedSessionsAsync`: most recent ≤ 30 days, idempotent; a day without complete 09:15–09:30 coverage or < 10 bars is excluded). The threshold is frozen into the session row at creation (restart reloads it). **Provisional starting guess (not data-derived): at least 10 eligible prior fixed-clock sessions are required**; with fewer the threshold stays NULL and Strong/Weak is UNCALIBRATED (no labels; the Dashboard says so and the data-quality line reports "Strong/Weak uncalibrated"). Prior sessions contributing today: determined at runtime (`StrongThresholdPriorStateCount` in the session row = number of rolling states pooled; log line `Adaptive fixed-clock session frozen ... prior fixed-session states=`).
* The history rebuild runs PRE-MARKET on a trading weekday (Host started before 09:15) so the live start never waits for it; if the Host starts later it runs once before the first session freeze (delays the session by the rebuild time). Failures never stop the observer.
* NPT1 (`npt1-m1-v1`, MD5/MDX3/PE5), PaperLive execution, trading and broker code are untouched. The adaptive paper bridge keeps receiving the same package types (no strategy family is registered there).
* Config: `Adaptive:Clock:Mode` = `Fixed13650` (default for the Host) or `OpeningVolumeV1` (legacy estimator for NEW sessions).

Fixed bars/day on the 22 recorded sessions (cumulative NIFTY-future volume ÷ 13,650): min 107, median 239, max 413.

## 2. Dashboard

* **Sticky Live Quote**: now a direct child of the dashboard shell (a sticky element only sticks inside its own parent; inside the short login grid it scrolled away). `z-index: 40` above grid headers; not sticky below 700 px. In capture: class `quote-panel-capture` (`position: static`), rendered exactly once.
* **Toasts** (`ToastHost`, UI only, no sound): commentary events (persisted lifecycle/bias/regime/agreement text), NPT1 trade open and close (from the durable ledger). Each source's first load only sets a baseline (no historical storm on load/refresh/reconnect); afterwards one toast per durable identity (commentary `EventIdentity`, `PositionId`), bounded dedup set (500), ≤ 3 visible, auto-dismiss 10 s, manual dismiss. Reuses `PushAdaptiveStateChanged` / `PushTradesChanged`; injects read-only services only.
* **Direction highlighting**: sign only — `val-pos` / `val-neg` subtle tint on Direction cells (Price, Roll Price, Strict, Enriched, OI (PE−CE), MicroDev, OFI, IV, Activity) and their AVG cells; zero and unavailable neutral; no magnitude thresholds.
* **Research header + data quality + recent events** (live and capture): date, boundary IST, bar, clock, expiry, selected strike, center, Auto Follow, completed boundary, `Data:` line, NPT1 strategy state as-of the boundary, model/source identity. Data quality is factual (failed projections, basis not usable, missing fixed-strike rows, unavailable pinned quote, degraded commentary, unavailable residual rows after the anchor, uncalibrated Strong/Weak) — never "OK" while any is present.
* **Roll ΔPx** (the only rolling metric in the primary Options grids): CE / PE / Roll Price Dir = CE−PE, computed only from the same exact session/expiry/strike/CE/PE tokens over the rolling window (10 bars); a window with any missing bar for that side is unavailable (never bridged or substituted). Ratios use rolling-start price; AVG uses SUM/SUM. The moving ATM±2 diagnostics (with their rolling metrics) stay under Diagnostics in the live view.

## 3. Screenshots

* Schedule for the fixed clock: first completed bar (`Initialization`, bar 1), then every 5th (6, 11, 16, …); completed bars only. Event captures (`EventTriggered`): commentary Confirmed / Flipped and NPT1 open / close map to the first completed bar at/after the event; skipped when that bar already has any capture, so several events on one boundary share one screenshot (carries the RECENT EVENTS strip). Event scan runs once per newly completed bar. adaptive-v1 sessions keep their original schedule exactly. `DeliveryUncertain` handling and the outbox contract are unchanged (no exactly-once claim).
* Policy of the event strip: events since the previous scheduled capture of the session up to the boundary (newest 6); with no previous capture, the latest 3 at/before the boundary — the policy is printed in the strip. Only rows timestamped ≤ the boundary are eligible.
* Strategy state as-of the boundary is reconstructed ONLY from durable ledger entry/exit times of that trade date; otherwise `strategy state unavailable` (current state is never substituted). Pending-order states are not part of the ledger and are not shown.
* Caption (≤ ~300 chars): date, bar @ boundary, clock, expiry / strike / Auto Follow, completed rows only, `Data:` line, Dashboard SHA. Captures always show Auto Follow ON = the strike the bar's own center selects.
* Renderer: the obsolete "exactly 3 tables" check is gone. Required PRIMARY grids by `data-grid` (`futures-compact`, `options-core-direction`, `options-context`, `residual-compact`) and blocks by `data-testid` (`research-header`, `research-data-quality`, `recent-events`), Live Quote present once and not sticky; a missing grid refuses the capture. Diagnostics (ATM±2) are excluded from captures. Capture-only CSS tightens table padding/font so the 23-column CORE grid fits a 1920 px image without expansion.

## 4. Database

No new migration in this task (no schema change: new data uses existing columns, `EventTriggered = 3` is a new value of an existing int column). The already-pending migration from the previous checkpoint is still required.

**Tomorrow's order:** (1) apply `20261014045007_AddStrikeBookMetricsAndFuturesGrossOfi` (`NiftySignal.AdaptiveObserverData/Sql/adaptive-strike-book-metrics.idempotent.sql`) to `niftysignal_adaptive_observer` (the Host would also migrate at start); (2) deploy Host + Dashboard from the merged commit; (3) restart Host PRE-MARKET (before 09:15) so the fixed-clock history rebuild finishes first; restart Dashboard.

## 5. Volume / performance

* Rows/day (strike v2) = 40 contracts × bars ≈ 4.3k (107 bars) / 9.6k (median 239) / 16.5k (413).
* Host: unchanged per-bar work (earlier measurement: ≈ +2 ms/bar for the per-strike projection at 40 contracts; persist median 8.5 ms/40-row batch). The only new Host work is the one-off pre-market history rebuild (≤ 30 days of futures ticks, read + bar build, no options).
* Screenshots/day: scheduled ⌈bars/5⌉ = 22 / 48 / 83 (min / median / max-volume day) + event captures (≤ one per boundary; Confirmed/Flipped commentary plus 2 per NPT1 trade) — expected roughly 60–110 on a busy day vs ~32 before. Telegram retry-after handling is unchanged; check the outbox on the first day.
* Dashboard: the research header adds 2–3 indexed reads per reload (commentary events, previous capture, ledger by date).

## 6. Validation

Release build 0 errors (3 pre-existing warnings, none new). Full suite with PostgreSQL: 2270 passed, 0 failed, 0 skipped. Visual check: the capture and live markup were rendered with the compiled CSS and inspected in the browser pane (static render of the real component output); the authenticated Dashboard could not be signed into (not attempted again), so VISUAL CHECK of the live sticky behaviour and toasts is pending.
