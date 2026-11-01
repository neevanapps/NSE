# Paper Engine V2 + Market Summary — deployment readiness and migration runbook

**Status: documentation only. Nothing in this file has been executed on the VM or any production database.** It is for the owner / operator. Every command and path below was read from this repository; where something is *not* in the repository it is marked **NOT VERIFIED — fill in on the VM** and no value is invented. Secrets (connection strings, tokens, password hashes) never appear here.

Feature branch: `feature/market-summary-paper-engine-v2`. Progress and evidence: [PAPER_ENGINE_V2_PROGRESS.md](PAPER_ENGINE_V2_PROGRESS.md). Design: [PAPER_ENGINE_V2_DESIGN.md](PAPER_ENGINE_V2_DESIGN.md). Companion (NPT1 paper ledger, same database role pattern): [PAPER_LIVE_DEPLOYMENT.md](PAPER_LIVE_DEPLOYMENT.md).

---

## 1. Git baseline, required commits, merge prerequisites

* Base: `origin/master` = `bfa9a8679ac90e3d96422451f609469433163c1b` ("Add vm-deploy.ps1: guarded VM release wrapper around deploy.ps1") at the time of writing; the branch's merge-base with it is that same commit, so the branch is a pure fast-forwardable descendant (several dozen commits ahead; count with `git rev-list --count origin/master..HEAD`). **Re-check before use:** `git fetch` is the owner's step; if `origin/master` has moved, inspect the divergence and re-validate, do not guess a merge resolution.
* The release is what is on the branch tip when the owner approves it. The last validated code state is `44a4ce7` (full Release suite, PostgreSQL included, replay enabled: **2612 passed, 0 failed, 0 skipped**); later commits are documentation only.
* Key commits (oldest first): Market Summary library `18d88ea`, Dashboard grids `79c420e`, India VIX + quantity progress `65c25da`, commentary removal `3521e2a`, V2 design `4d72ca4`…`141d03c`, V2 core/frames/execution/persistence/alerts `9288138`…`906b649`, Home V2 section `84bb15e`, Host integration `b4dbb9c`/`1a5fcf7`, replay validation `c73bfa7`, Futures colour coding `214c127`, hardening `44a4ce7`.
* `deploy.ps1` (repo root) refuses to publish unless: the worktree is clean (including untracked files); `HEAD` equals `-ExpectedCommitSha`; and, when `-ExpectedCommitSha` is given, the branch is **`master`**. Therefore the owner must first bring the reviewed branch onto `master` (a fast-forward is possible) and check `master` out clean. This runbook performs no merge and no push.
* Merge prerequisites checklist: (a) `git status` clean on the release checkout; (b) `git rev-parse HEAD` recorded as the release SHA; (c) the full suite passes on exactly that tree (see §8; `deploy.ps1` also runs the tests itself unless `-SkipTests`).

## 2. Production configuration changes

Both services read `appsettings.json` (shipped, safe defaults) plus the VM's own `appsettings.Local.json`. **`deploy.ps1` strips `appsettings.Local.json` from the staged output and never overwrites the VM's copy**, so VM secrets and connection strings are preserved by the deploy itself.

### Host (`NiftySignalHost`, `appsettings.json` shipped with the release)
New section, shipped **disabled** (verified in `NiftySignal.Host/appsettings.json`):

```json
"PaperEngineV2": {
  "Enabled": false,
  "ConnectionStringName": "TradingPaperLive",
  "StrategyDirectory": "",
  "QueueCapacity": 64,
  "MaxProcessingLagSeconds": 5,
  "FramesToClearGap": 3,
  "TerminalRetryUntil": "15:25",
  "BrokeragePerOrder": 0,
  "CostScheduleVersion": "npt1-frozen-fees-v1",
  "Alerts": { "Opened": false, "Closed": false, "OrderRejected": false, "PartialFill": false, "ExecutionFailure": false }
}
```

Additional optional keys read by the bridge (`PaperEngineV2Bridge.Bind`): `NoticeCapacity` (default 256), `AlertPollIntervalMs` (default 2000). **Owner decisions required before activation (no code default is assumed):** `QueueCapacity`/`MaxProcessingLagSeconds` (candidates 64 and 5 s, evidence in the progress doc), `FramesToClearGap`, `TerminalRetryUntil` (must be later than 15:00), `BrokeragePerOrder` (the shipped 0 is a placeholder, costs must not be assumed zero in activation), `CostScheduleVersion` (exactly one of `npt1-frozen-fees-v1` or `nse-index-options-2026-04-01-provisional` — the two overlap in time, so strategies' `fees.schedule` must name the chosen one), and `StrategyDirectory` (a folder of `pev2-strategy/1` JSON files on the VM; **NOT VERIFIED — path to be chosen on the VM**).

* `ConnectionStrings:TradingPaperLive` must exist in the Host's `appsettings.Local.json` for V2 (same key and the same database as the NPT1 paper ledger; see `docs/PAPER_LIVE_DEPLOYMENT.md` §2–3). The V2 tables live in their own schema `pev2` inside that database.
* Alerts are per class and **all off by default**. Telegram uses the existing `Telegram:BotToken` / `Telegram:ChatId` (already in the Host's local settings for existing alerts); the sender is registered only if some `PaperEngineV2:Alerts:*` is `true`.

### Dashboard (`NiftySignalDashboard`)
* No new setting is required: the Home V2 section reads `ConnectionStrings:TradingPaperLive` (read-only session, 5 s statement timeout; use a role with **SELECT only**, as for `/paper-live`). Without that connection string the section shows "the V2 database is not configured".
* Screenshots keep their existing configuration (`AdaptiveScreenshots:Enabled/BaseUrl/OutputDirectory`, `Telegram:*`, see `docs/ADAPTIVE_TELEGRAM_SCREENSHOTS.md`); nothing new. `deploy.ps1` runs `NiftySignal.Dashboard.dll --install-screenshot-browser` on the target before starting the Dashboard.
* Behaviour changes visible without any configuration: Market Summary grids, India VIX in the Futures Summary, sticky bar-quantity progress, Futures directional tint, commentary UI/notifications removed (history retained), V2 section on Home (shows "not running" while V2 is off/not installed).

## 3. Existing database prerequisites

* The adaptive observer database is migrated by the **Host at start** (EF `Database.Migrate()`); this release adds only the additive adaptive migrations already described in `docs/SPOT_ATM_CONTRACT.md` (idempotent SQL kept in `NiftySignal.AdaptiveObserverData/Sql/`: `adaptive-spot-atm-and-strike-greeks.idempotent.sql`, `adaptive-option-strike-bars.idempotent.sql`, `adaptive-strike-book-metrics.idempotent.sql`). Which of them are already applied on the VM is **NOT VERIFIED** — inspect `__EFMigrationsHistory` of the observer database read-only before the deploy.
* The V2 schema is **not** applied by the application: `V2SchemaCheck` only reads `pev2.pev2_schema_versions`. It must find component row with schema version equal to the code's (`2`) and journal schema `pev2-journal/1`; a missing schema, older, newer or foreign version keeps V2 closed (the Host logs "Paper Engine V2 SHADOW is closed … The observer and NPT1 are unaffected").
* Prerequisite: the NPT1 trading database named by `TradingPaperLive` must already exist (it is the one created per `PAPER_LIVE_DEPLOYMENT.md` §2). The V2 script creates only the `pev2` schema in it and references no `trading.*` object.
* Role privileges for `pev2`: the Host role needs INSERT/SELECT/UPDATE on schema `pev2` (the journal and ledger are idempotent inserts; the schema-version row is read); the Dashboard role needs SELECT only. **Concrete role names are NOT VERIFIED — fill in on the VM**; the repository does not define them.

## 4. The owner-run `pev2_` migration script

* Location (repository): `NiftySignal.PaperEngineV2.Data/Sql/pev2-schema.idempotent.sql` (8.5 KB). Generated from migration `20261031084515_InitialPaperEngineV2Schema` (+ a version-2 update); purely additive — `CREATE ... IF NOT EXISTS`, history table `pev2.__pev2_migrations_history`, tables `pev2_schema_versions`, `pev2_frames`, `pev2_dispositions`, `pev2_gaps`, `pev2_events`, `pev2_strategy_configs`, `pev2_alert_outbox`. No ALTER/DROP/TRUNCATE of anything existing; verified by test (script ≡ migration objects, runs twice with no change, trading tables untouched).
* Safe execution order (operator, on the VM, as a PostgreSQL superuser or the database owner; `psql` availability on the VM is **NOT VERIFIED**):
  1. Take the backup in §5 first.
  2. Read the script.
  3. Apply to the trading database named by `TradingPaperLive` — the same pattern the repository documents for the NPT1 schema: `psql -d <trading database> -f NiftySignal.PaperEngineV2.Data/Sql/pev2-schema.idempotent.sql` (copy the file from the verified release checkout; do not edit it).
  4. Verify (§5) before any application change that depends on it. Running it twice must change nothing.
* Rehearse first on a disposable database. The test suite already does this against `niftysignal_trading_test` (`PostgresPersistenceTests`); a manual rehearsal is: create a scratch database, apply the script twice, `SELECT * FROM pev2.pev2_schema_versions;`, drop the scratch database.
* The Host never runs this script, never calls `Migrate`/`EnsureCreated` for `pev2` (enforced by a test).

## 5. Backup, verification and rollback (database)

* Backup before any change: `pg_dump` of the trading database (custom format) and of the adaptive observer database to a destination with verified free space. Exact `pg_dump` location/credentials on the VM are **NOT VERIFIED**. The VM's boot disk has been tight in the past: check free space first (read-only: the free-space line of `vm-deploy.ps1 -PreflightOnly`'s VM snapshot, or `Get-PSDrive`), size the dump from `pg_database_size`, and verify the dump (`pg_restore --list <file>`) before proceeding.
* Verification after the script: `SELECT component, journal_schema, schema_version FROM pev2.pev2_schema_versions;` (expect schema version 2, journal schema `pev2-journal/1`); `SELECT count(*) FROM information_schema.tables WHERE table_schema = 'pev2';` (expect 8 including the `__pev2_migrations_history` table); existing `trading.*` row counts unchanged versus before.
* Rollback of the V2 schema: the script is additive and V2 is disabled by default, so the first rollback is simply **leave `PaperEngineV2:Enabled=false`** (the schema is inert). If removal is ever demanded, `DROP SCHEMA pev2 CASCADE;` is the only operation and it touches nothing outside `pev2` (the script creates nothing elsewhere) — it destroys V2 history, so only after the owner has decided so and a dump exists. No such command is automated anywhere.
* Restore of a whole database from the backup is a separate, owner-decided operation and is not needed for a V2 rollback.

## 6. Windows Service deployment order and startup checks

* Services (verified in `deploy.ps1`): **`NiftySignalHost`** and **`NiftySignalDashboard`**. Install directories are discovered from the service registration (`Win32_Service PathName`), never assumed. The VM Tailscale address default in the script is `100.105.67.79`; remoting is WinRM (`New-PSSession`).
* Order enforced by `deploy.ps1`: build Release from an archive of the verified SHA → run tests (unless `-SkipTests`) → publish Host and Dashboard → strip `appsettings.Local.json` → **Host is deployed and started before the Dashboard** (hash-delta copy, stop → copy → start with 30 s waits) → Dashboard (installs the screenshot Chromium before start).
* Guard: the **desktop** `NiftySignalHost` must be stopped (only one machine may hold the FlatTrade session) or the script exits unless `-Force`.
* Command shape (from the script's own help/parameters): `.\deploy.ps1 -Target Vm -Service Both -ExpectedCommitSha <40-hex release SHA>` on a clean `master` checkout; it prompts for the VM credentials. `vm-deploy.ps1` (committed on master, guarded wrapper with pre-flight, read-only VM snapshot, binary/config backup and post-deploy checks, also calls `deploy.ps1`) is optional; `.\vm-deploy.ps1 -PreflightOnly` deploys nothing. Do **not** deploy during market hours (weekdays 09:00–15:45 IST; a Host restart resets the observer warm-up).
* Startup checks (read the Host log, `logs/niftysignal-*.log` per `appsettings.json` Serilog configuration): the adaptive migration applied without error; adaptive observer initialises (fixed-13,650 session recovers/creates); with V2 disabled there is **no** "Paper Engine V2" line other than the inert state; the Dashboard is Running and its build identity matches the Host's release SHA (shown on the Adaptive Observer panel as "Dashboard build").

## 7. V2 disabled by default and the separate activation checklist

Deploy with V2 **disabled** and verify the existing system first (§8). Activation is a separate, later, reversible step:

1. Schema installed and verified (§4–5) on the trading database named by `TradingPaperLive`.
2. Choose and write down: `StrategyDirectory` (folder created on the VM), `CostScheduleVersion`, `QueueCapacity`, `MaxProcessingLagSeconds`, `FramesToClearGap`, `TerminalRetryUntil`, `BrokeragePerOrder`.
3. Put **only reviewed, intentionally enabled** `pev2-strategy/1` files in the directory (up to 30; disabled/invalid files are recorded or rejected visibly, an invalid set closes V2). Start with one or a few, not 30 by default. Strategy file format: see `docs/PAPER_ENGINE_V2_DESIGN.md` and `NiftySignal.Tests/PaperEngineV2/ConfigTests.cs`.
4. In the Host's `appsettings.Local.json` (VM-local, not committed) set `PaperEngineV2:Enabled=true` plus the values above; enable alert classes deliberately (`Opened`, `Closed`, `OrderRejected`, `ExecutionFailure`; `PartialFill` never occurs — V2 v1 is all-or-none); restart only `NiftySignalHost` outside market hours.
5. Check the log for "Paper Engine V2 SHADOW started" (it lists the active strategies); any strategy that did not activate is logged with its reason; "closed (SchemaIncompatible|ConfigInvalid)" means fix the stated cause — NPT1 and the observer are unaffected by design.
6. Deactivate by setting `Enabled=false` and restarting the Host; the ledger and journal remain.

## 8. NPT1 regression and isolation verification after deployment

* Before enabling V2 and again after: NPT1 paper engine initialises and recovers as before (its own log lines and `/paper-live`), historical adaptive data readable, observer digest behaviour unchanged. Evidence already collected offline: persisted observer output identical with V2 on vs off over the recorded session (digest equality in `HostReplayTests`), no NPT1 production file changed, structural isolation tests (`IsolationReleaseBlockerTests`, `PipelineIsolationTests`): V2's assembly closure reaches no broker/ingestion/live/NPT1 assembly and the Host references V2 only via the Runtime project and the authorised hook sites.
* Tests to run on the release commit (offline, local): `dotnet test NiftySignal.Tests -c Release` with `NSE_TRADING_TEST_DB` (fixed database `niftysignal_trading_test`, loopback) and, for the recorded-data replay, `NSE_V2_REPLAY_SOURCE` (read-only tick database). Without the replay variable one test is reported skipped by design (it is gated, not ignored).

## 9. Health checks (V2 worker, queue, persistence, gaps, positions, outbox)

Read-only; the Home page V2 section shows most of these (trade date, last frame bar/time, frames today, entries-blocked frames, gaps today, alert outbox pending/uncertain/failed, positions with signal status, trades, rejections, failures). SQL (schema `pev2`, run against the trading database as a read-only role):

* Frames flowing: `SELECT count(*), max(bar_seq) FROM pev2.pev2_frames;` and `SELECT count(*) FROM pev2.pev2_dispositions WHERE entries_allowed;` (frames = dispositions).
* Lag / queue: `SELECT max(processing_lag_ms) FROM pev2.pev2_dispositions;` (limit is `MaxProcessingLagSeconds`); entries are blocked, not skipped, above it.
* Gaps: `SELECT * FROM pev2.pev2_gaps ORDER BY id;` (cause 0 Overflow, 1 BuildFailure, 2 MissingFrame).
* Ledger: `SELECT kind, count(*) FROM pev2.pev2_events GROUP BY kind;` (EntryOrderPlaced / EntryFilled / EntryRejected / ExitRequested / ExitFilled / …; unresolved exits and strategy faults are events too).
* Outbox: `SELECT alert_class, status, count(*) FROM pev2.pev2_alert_outbox GROUP BY 1,2;` (status 0 pending, 1 sent, 2 uncertain — never auto-resent, 4 expired, 5 claimed/in flight).
* Worker/log: errors from `V2 consumer faulted`, `V2 could not process the bar notice`, `V2 alert dispatch failed`. Counters (`NoticesDropped`, `BuildFailures`, `ConsumerRestarts`, `LedgerFailures/Conflicts`, `AlertFailures`) are in the runtime snapshot (in-process; surfaced through logs/tests, not yet an HTTP endpoint).

## 10. Dashboard / Market Summary / colour / screenshot / alert smoke tests

* Dashboard Home loads with no exception: Adaptive Observer panel with Market Summary (Futures Summary five windows + ATM/ITM/OTM/TOTAL core and context grids), detailed diagnostic grids retained, India VIX columns (dashes when unavailable/stale — never zero), sticky Live Quote with bar-quantity progress, **no commentary** section or toast, V2 section ("not running" while disabled).
* Futures colour coding: green/red sign tint on Price Δ, Strict Δ, Enriched Δ, MicroDev, OFI in the Futures Summary (all five windows) and the detailed Futures grid; OI Δ, volume, activity, VIX untinted; zero/unavailable neutral.
* Historical screenshot: the Dashboard's `/adaptive-screenshots` page "Send pre-live test" (existing, documented in `ADAPTIVE_TELEGRAM_SCREENSHOTS.md`) sends **one real Telegram message** — owner decision. Offline evidence exists (real Chromium, fake Bot API, five pinned jobs, grids and tint inspected; see progress doc Phase 9).
* Paper alerts: enable classes only after the V2 smoke; one controlled paper trade needs a live session. Alert state transitions (pending → claimed → sent / uncertain / expired) verified offline with a fake transport.

## 11. Go / no-go criteria before the Monday session

**GO only if all are true:** release SHA recorded and equal on Host and Dashboard; full suite green on that SHA with zero unexplained skips; database backups taken and verified, free disk adequate; V2 schema script applied (or deliberately not, with V2 disabled) and verified; both services Running, Host log free of migration/startup errors; adaptive observer initialises with the fixed-13,650 clock; NPT1 initialises and recovers; Dashboard grids render and the screenshot path works; **no live-order configuration touched and none enabled**; V2 either disabled or enabled only with the §7 checklist complete.
**NO-GO / roll back if any:** a service fails to start; migration error; NPT1 or observer behaviour differs from before; Dashboard exceptions; disk nearly full; any sign that V2 affects the observer or NPT1 (disable V2 first; if the existing system is affected, redeploy the previous build from the backup/previous release SHA).
**Cannot be verified on a closed-market day (Monday live checks):** 09:15 fixed-13,650 bar collection; futures/option token selection; India VIX freshness; live quote and quantity progress; V2 frame construction and lag on live data; paper signals/fills; real Telegram screenshot and alert delivery; no live broker orders.

## 12. Known limitations, unresolved decisions, recovery

* One recorded full session exists locally (2026-09-07); the Host live scheduler loop was exercised through the same worker methods, not the BackgroundService scheduler; GC counts and per-stage query timings are not instrumented in the Host path; real Telegram never exercised; Auto-Follow live view not exercised offline.
* Owner decisions open: cost schedule, queue capacity / lag / gap-clear / terminal-retry values, strategy set and directory, Market Summary option-grid Direction tint (only Futures requested), whether V2 trade events should trigger screenshots (today only the NPT1 paper ledger does), backup/roles specifics on the VM.
* Recovery: V2 crash/consumer fault → rebuilt from the durable journal automatically; Host restart → recovery by journal replay, no duplicate orders/fills/alerts (tested); persistence loss → entries blocked, nothing claimed durable; disable V2 by config at any time. Application rollback: redeploy the previous release SHA with `deploy.ps1` on its master checkout, or restore the backed-up binaries per `vm-deploy.ps1`'s documented rollback (copy the backup folder over the install folder with the service stopped). The VM's `appsettings.Local.json` is never touched by `deploy.ps1`.
* Safety: V2 is paper/shadow only; no broker, order or live type is reachable from it; the future "at most two explicitly authorised live strategies" restriction is architectural only and not implemented.
