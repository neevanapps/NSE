# NiftySignal services: owner-run deployment, activation, health checks and rollback

Paper only. **Nothing here has been executed on the VM or a production database.** Paper release readiness is NOT READY (see the plan document); real money is NO-GO. All commands are for the owner, in the owner's own PowerShell, outside market hours (weekdays 09:00-15:45 IST are blocked by the scripts).

## 1. Prerequisites

| Item | Check |
|---|---|
| Release identity | Reviewed commit on a clean `master`; record the 40-character SHA |
| Backups | `pg_dump` of the trading database and the observer database, verified with `pg_restore --list`; free disk checked first |
| Observer migrations (applied at start by the process that runs the Observer: `NiftySignalObserver`, or the Host when `InHost`) | `20261126043514_AddObserverRecoveryProgress`, `20261126053253_AddBarPublications`, `20261126080333_AddObserverCheckpoints` (scripts in `NiftySignal.AdaptiveObserverData/Sql/`: `adaptive-observer-recovery-progress`, `adaptive-bar-publications`, `adaptive-observer-checkpoints`) |
| Paper ledger schema (owner-run, **not** applied by any service) | `NiftySignal.PaperEngineV2.Data/Sql/pev2-schema.idempotent.sql` up to `20261126053557_AddExecutionService` |
| Database role | The execution service writes schema `pev2` (SELECT, INSERT, UPDATE, including the new tables) and reads the trading and observer databases. Once `pev2_execution_state` exists, **the Host role needs the same SELECT, INSERT, UPDATE on it** if the in-Host paper engine is ever enabled (fallback): without them that engine stays closed. Role names are not defined in the repository: **not verified** |

Apply the ledger script to the database named by `ConnectionStrings:TradingPaperLive`, then verify:

```sql
SELECT "MigrationId" FROM pev2.__pev2_migrations_history ORDER BY 1;
SELECT component, journal_schema, schema_version FROM pev2.pev2_schema_versions;
```

Expect `20261126053557_AddExecutionService` in the first result and version 2, `pev2-journal/1` in the second. The observer history is checked the same way on the observer database: `SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1 DESC LIMIT 5;`.

## 2. Install (once)

| Service | Role | Writes |
|---|---|---|
| `NiftySignalHost` | broker session and tick ingestion | tick database |
| `NiftySignalObserver` | bars, metrics, checkpoints | observer database |
| `NiftySignalExecution` | paper entries and protective exits | paper ledger (`pev2`) |
| `NiftySignalDashboard` | display, health alerts | none of the above data |

Only the Host holds the FlatTrade session. On the VM, elevated PowerShell, from a copy of the install scripts. Use the same service account as `NiftySignalHost` (confirm which it is). Folder paths are examples: follow the layout used on the VM.

```powershell
.\Install-ObserverService.ps1 -InstallDir C:\Naveen\NiftySignal.Observer
```

```powershell
.\Install-ExecutionService.ps1 -InstallDir C:\Naveen\NiftySignal.ExecutionService
```

Both are registered with manual start, restart-on-failure actions (including a stop with an error exit code; not verified on a real service) and are not started. No service-manager dependency on PostgreSQL is declared: a service that starts first fails and relies on the restart actions.

Each `appsettings.Local.json` lives only on the machine and is never shipped by the deployment.

**Observer** (required before the first deployment). Write only these keys; do **not** copy the Host file, which holds broker credentials.

| Key | Value |
|---|---|
| `ConnectionStrings:NiftySignalDb` | tick database (read); the observer database name is derived from it. The role must also be able to migrate and write the observer database |
| `Adaptive:*`, `Pricing:*`, `DashboardPush:*` | every value the Host's local file overrides. The start is refused if `Adaptive:Top5Microstructure:Enabled` / `OptionsEnabled`, `Pricing:RiskFreeRate` or `DashboardPush:HubUrl` differ from the Host. Other overrides (for example `Adaptive:Clock:Mode`, `Adaptive:Recovery:*`) are **not** compared: copy them by hand |

Observer settings shipped: `Adaptive:Recovery` `TimeLimitSeconds` 300, `TimeoutRetryBackoffSeconds` 30, `ReadWindowSeconds` 120, `CheckpointsEnabled` true, `CheckpointIntervalSeconds` 60, `CheckpointRetention` 3 (1-20). `CheckpointsEnabled=false` returns to full replay on every restart.

Checkpoint retention (rows of `adaptive_checkpoints` only; no tick, bar, metric or execution record is ever removed):

| Rows of one session | Kept |
|---|---|
| This build and configuration | newest `CheckpointRetention` |
| Another build or configuration (after a deployment, or after going back) | newest `CheckpointRetention`, counted separately |
| While a replay started from a checkpoint is not yet verified | additionally that checkpoint and all older ones |
| Damaged (hash, content or session wrong) | removed when a recovery meets it |
| Completed sessions | stay as they were at the close; nothing deletes across sessions, so the table grows by at most `2 x CheckpointRetention` rows per session. Payload size on real data is unmeasured (reported by the certification run) |

Cleanup runs after a save and after a successful recovery, removes at most 20 rows at a time, oldest first, and never stops the Observer if it fails.

**Execution:**

| Key | Value |
|---|---|
| `ConnectionStrings:NiftySignalDb` | tick database; the observer database name is derived from it |
| `ConnectionStrings:TradingPaperLive` | paper ledger database |
| `Telegram:BotToken`, `Telegram:ChatId` | for the service's own failure alerts |
| `Execution:Enabled` | `false` until section 4 |

## 3. Deploy

```powershell
.\vm-deploy.ps1 -PreflightOnly
```

```powershell
.\vm-deploy.ps1
```

**Before deploying (separate Observer, the default):** the installed Host local file must not enable `PaperEngineV2:Enabled` or `Trading:PaperLive:Enabled` (the new Host would not start). Set them to `false` first, or choose `InHost`. A deployment that includes the Host is refused otherwise.

On the first deployment the two new services have no binaries yet: `vm-deploy.ps1` says so and takes no backup of them (their rollback is: stopped and disabled). Host and Dashboard are always backed up. The ordered checklist for 27 November is `NSE_OWNER_DEPLOYMENT_CHECKLIST_2026-11-27.md`.

Top-5 is shipped **off**: set `Adaptive:Top5Microstructure:Enabled` and `OptionsEnabled` to `true` in both the Host and the Observer local files.

With the default `-Service Both`:

1. Nothing is deployed if the new Host would run no observer: `NiftySignalObserver` not registered and the Host not set to `Adaptive:Observer:Process=InHost`.
2. Stop `NiftySignalExecution`, then `NiftySignalObserver` (each if running).
3. Install Host, Observer, Execution, Dashboard in that order (each: backup, copy, verification).
4. Start the Observer, then Execution, each only after its check passes.

A deployment limited to Dashboard or Execution leaves the Observer running. A disabled service is left stopped.

- **Observer check:** the Host build knows the setting, both processes are `Separate`, the Observer has a connection string, and the compared settings match. Otherwise it **stays stopped and no observer runs**: fix and re-run.
- **Execution check:** reads only `PaperEngineV2:Enabled` (Host), `Execution:Enabled` and `ExecutionHealth:Required` from installed settings files; environment-variable overrides are not seen. Refused if both writers are configured or a setting cannot be read.
- A deployment that fails part-way leaves the stopped services stopped: fix the cause and re-run.

**Ownership.** The running Observer holds a PostgreSQL advisory lock on the observer database. A second Observer (separate or in-Host) logs that another process holds it and does not write.

The paper ledger has one writer, whether that is the execution service or the in-Host engine of this release. Taking ownership means holding the ledger's advisory lock on a dedicated connection and claiming the next epoch in `pev2_execution_state`. Every ledger write (events, alert outbox rows, journals, opportunity rows, live marks, state) is a transaction on that connection which locks the state row and requires that epoch. A writer that lost the connection or was superseded cannot commit anything, including bars it had already queued or decisions it had already evaluated; the in-Host engine discards them and either rebuilds under a new ownership or closes. The alert dispatcher's delivery-status updates are not fenced (they change no position). The state row may show an owner that has published no snapshot when the in-Host engine owns the ledger; the Dashboard then says positions are not reported.

**Binaries from before this release take neither lock nor epoch: nothing in the database stops them. Stop and disable them before starting the new services, and never run them side by side.**

**InHost option.** `Adaptive:Observer:Process=InHost` in the Host local file runs the Observer inside the Host as before (no process isolation); disable `NiftySignalObserver`. It is required if the in-Host paper engine is enabled: the Host refuses to start with it while `Separate`.

`Execution:JournalReplayBatchSize` (default 200, 1-5000): journal rows read at a time when the execution state is rebuilt after a restart. It bounds memory only; the rebuilt state does not depend on it. The journal itself is never trimmed.

Strategy files ship with the execution service (`paper-engine-v2-strategies/execution-service/`) and are overwritten by each deployment: change them in the repository, not on the VM.

## 4. Activate (ownership transfer)

Do this on a day with no open paper position, outside market hours. A same-day transfer leaves the new service unreconciled (entries blocked) for the rest of that day.

1. Host `appsettings.Local.json`: `PaperEngineV2:Enabled=false`. Paper-live: `TradingPaperLive`-based NPT1 paper-live stays disabled (decision A4); confirm its `Enabled` is false and every NPT1 strategy is disabled.
2. Execution `appsettings.Local.json`: `Execution:Enabled=true`. Review `BrokeragePerOrder` (shipped 0) and `CostScheduleVersion`.
3. Dashboard `appsettings.Local.json`: `ExecutionHealth:Required=true` (alerts even if the service never reports).
4. Restart in this order: Host, `NiftySignalObserver`, `NiftySignalExecution`, Dashboard.
5. When verified: set `NiftySignalObserver` and `NiftySignalExecution` to start type Automatic (`Set-Service <name> -StartupType Automatic`).

## 5. Health checks

| Check | Expected |
|---|---|
| Observer log `logs\niftysignal-observer-*.log` | `Build identity` with the release SHA; no ownership refusal; recovery reaches Live; after a restart, "continues from checkpoint" once checkpoints exist |
| Host log | no adaptive observer start when `Separate` |
| Execution log `logs\niftysignal-execution-*.log` | `Build identity` with the release SHA; five strategy lines (four `ENABLED`, `TOTAL_VOTE_4_OF_6 disabled`), warm-up 20 bars; `Execution ownership taken with epoch N` |
| `SELECT component, owner, epoch, status, to_timestamp((heartbeat_utc_ticks - 621355968000000000) / 10000000.0) FROM pev2.pev2_execution_state;` | one row, heartbeat seconds old in a session |
| Host log | no Paper Engine V2 start line; no paper-live processing |
| Dashboard | exactly two paper grids with four rows each; no execution banner |
| Alerts | Telegram test is an owner decision; delivery on the VM is not verified |

## 6. Rollback

1. Stop and disable the new services **first**. Binaries older than this release run the observer inside the Host and take no writer lock.

```powershell
Stop-Service NiftySignalExecution, NiftySignalObserver
```

```powershell
Set-Service NiftySignalExecution -StartupType Disabled
```

```powershell
Set-Service NiftySignalObserver -StartupType Disabled
```

2. Check open paper positions in the Dashboard or the ledger. They are not carried to the old engine: record them as unresolved; no exit fill is invented.
3. Restore the previous Host and Dashboard from `<install parent>\_backups\<service>-<stamp>` (service stopped), or deploy the previous release SHA.
4. Re-enable the in-Host engine only if it was enabled before, and only outside market hours. On the same trade date its journal replay would meet events written by the service: leave it off until the next trade date.
5. Leave the added columns and tables in place (additive; older code ignores them). Never run two writers.

To fall back only on process isolation, keep this release and use the `InHost` option (section 3) with `NiftySignalObserver` stopped and disabled.

## 7. Limits that stay after deployment

- If the VM, its disk, network or PostgreSQL is down, open positions are unprotected and no component on this VM can act or necessarily alert.
- If the Host is down there are no quotes: exits are requested and alerted but cannot fill.
- If the execution service is down nothing protects positions until it restarts; the Dashboard banner and alert are the signal.
- If the Observer is down or recovering there are no new bars: new entries are blocked; protective exits do not depend on it.

## 8. Real-data validation still required

Follow `docs/NSE_OWNER_BENCHMARK_PROCEDURE.md` (scratch databases only; includes pass conditions, commands, disk estimate and cleanup). The Observer processes only the current date, so a recorded day is replayed through the tools there, not by starting the service on it. Dashboard and Telegram capture against real data ("Send pre-live test" is a real Telegram message) remain an owner step: five capture grids in order, two paper grids.
