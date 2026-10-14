# Dashboard: fixed-strike options history + simplified NPT1 paper page (2026-10-13)

Branch `feature/dashboard-fixed-strike-paper-simplify` (from master `50c0b9e5a3219e25d42dc738617a1c389c4468a5`). Display + observational projection only. NOT deployed, NOT merged.

## What changed

1. **Options grid = fixed strike history.** Header: Expiry, Auto follow center ON/OFF, Selected strike dropdown, Current center. Auto ON: on a NEW COMPLETED adaptive bar only, the selection moves to that bar's authoritative `CenterStrike` and the whole grid is re-read for that strike. Auto OFF pins the strike (new bars add rows). A manual dropdown choice turns Auto OFF; turning Auto ON syncs immediately. Every row is one expiry + strike + CE token + PE token; a missing bar shows `—`, never another strike. The legacy ATM±2 grid is unchanged and now sits under *Diagnostics* (and is rendered open/identical in Telegram capture mode).
2. **Per-strike sidecar** `adaptive_option_strike_bars` (metrics version `option-strike-v1`): one row per SessionId/BarSeq/contract (expiry+strike+side), computed from the SAME session-long per-token flow snapshots and bar boundaries as the existing band metrics (no new aggressor classifier). Missing = NULL, never 0. Only the frozen weekly option universe. Insert-if-missing / verify-if-present; a sidecar failure never changes the core persistence/verification result.
3. **/paper-live simplified**: compact header, RUNNING TRADES, TODAY'S COMPLETED TRADES, TODAY'S STRATEGY SUMMARY (three strategies, never combined), existing detail kept under collapsed *Diagnostics*.
4. **Live P&L @ bid**: (current bid − entry price) × remaining quantity for exactly the held token, from the EXISTING Host→Dashboard SignalR tick push into an in-memory book (`Npt1LiveQuotes`); no polling, no DB read per tick; display only, never persisted; no bid ⇒ `—`. Durable MFE/MAE untouched. Host now also sends the existing `PushTradesChanged` when an NPT1 position opens/closes so the Dashboard re-reads open positions immediately.

## Database

* Migration required: **YES** (adaptive observer database only).
* Migration name: `20261013140636_AddOptionStrikeBars` (`NiftySignal.AdaptiveObserverData/Migrations`).
* Idempotent SQL: `NiftySignal.AdaptiveObserverData/Sql/adaptive-option-strike-bars.idempotent.sql` (CREATE TABLE + unique index (SessionId, BarSeq, Token, MetricsVersion) + lookup index (SessionId, Expiry, Strike, Side, BarSeq) + `__EFMigrationsHistory` row; transactional, safe to re-run).
* Target: the adaptive observer database (`niftysignal_adaptive_observer` on the VM), table `adaptive_option_strike_bars`. No other table is touched.
* Grants: no new grants expected (same owner/roles as the other adaptive tables). Verify the Host and Dashboard roles can INSERT/SELECT on the new table (default privileges normally cover it).
* The Host runs `AdaptiveObserverDbContext.Database.Migrate()` at start-up, so applying the script first (or letting the Host migrate) both work; the script is the recommended, reviewable route. The Dashboard tolerates the table being absent (fixed-strike section shows `—`).

## Performance (estimates unless marked measured)

* Rows/day ≈ contracts × completed bars ≈ ~80–120 contracts × ~160–250 bars ≈ **13–30k rows/day** (~3–5 MB/day incl. indexes); only the frozen weekly universe, not the whole chain.
* Fixed-strike read: indexed on (SessionId, Expiry, Strike, Side, BarSeq) → two narrow range scans returning ≤ ~250 rows each; sub-second expected (not measured against the VM database).
* Host extra work per bar: O(universe) arithmetic over existing snapshots (+2 IV solves per contract) and one batched insert of ~100 rows. Measured on the synthetic 10-contract session (504 bars, 216k ticks): 346–420 ms with projection vs 400–432 ms without — within noise.
* Dashboard live path: in-memory only (tick push → `Npt1LiveQuotes` for held tokens → coalesced 250 ms render); no DB access per tick.

## Safety (git diff)

Unchanged: NPT1 strategy logic, NPT1 M1 clock/producer/execution/contract selection/accounting, adaptive-v1 bar clock and existing calculations (engine change is an additive, default-off `projectStrikes` flag; a test proves enabling it changes no existing package field), Commentary, Telegram, PaperLive risk/capital, broker integration. Durable MAE/MFE semantics unchanged. No real-order work.

## Deployment (owner)

1. Back up / snapshot the adaptive DB (optional, additive table).
2. Apply `adaptive-option-strike-bars.idempotent.sql` to the adaptive observer DB (or let the Host migrate on start).
3. Build + deploy Host and Dashboard from the feature branch (or after merge).
4. **Host restart required** (strike sidecar + trade-change push). **Dashboard restart required.** Prefer outside market hours (restart resets warm-up state; adaptive recovery replays and back-fills the sidecar deterministically).
5. Keep `Command Timeout=300` on `ConnectionStrings:NiftySignalDb` through the first full NPT1 session.
6. Post-deploy checks: Host log has no `strike sidecar` mismatch/failure warnings; `SELECT count(*), max("BarSeq") FROM adaptive_option_strike_bars WHERE "SessionId" = <today>` grows by ~(universe size) per completed bar; Options page shows the fixed-strike header and a full-history grid for the selected strike (Auto ON follows the new completed-bar center, dropdown change flips Auto OFF); ATM±2 grid present under Diagnostics; /paper-live shows the compact header, Running (bid-driven Live P&L, quote age), Completed (today only), Summary, Diagnostics collapsed; open/close of a paper trade refreshes the Running grid immediately.

## Validation notes

Release build 0 errors (3 pre-existing warnings, none new). Focused tests: `AdaptiveStrikeProjectionTests` (6), `FixedStrikeAndPaperTodayTests` (21). **Visual browser verification was not completed**: the local Dashboard started but the sandboxed preview could not sign in (login env overrides were not honoured), so rendering is covered by the compiled Razor + logic/projection tests and existing capture-mode render tests, not by screenshots. Please eyeball both pages once after deploy.
