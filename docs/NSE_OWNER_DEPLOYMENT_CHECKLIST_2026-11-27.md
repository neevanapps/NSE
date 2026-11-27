# Owner deployment checklist, paper session of Monday 30 November 2026

**Status: prepared, not executed. Nothing below has been run against the VM or a production database.** Read the readiness report first (`NSE_OVERNIGHT_RELEASE_READINESS_2026-11-27.md`) and continue only if it says GO. Paper only; real money NO-GO.

**Time limit.** The scripts refuse to stop a service on weekdays 09:00-15:45 IST, and refuse to start when less than 30 minutes remain before 09:00. Preferred: deploy on Saturday 28 or Sunday 29 November. If it slips to Monday: begin by 07:30 IST, deployment must start before **08:30 IST**; if that is missed, deploy after 15:45. Do not use `-AllowMarketHours`.

Conventions: `<...>` is a value only you know. Passwords are typed at the prompts, never into a command. "Approval" = needs your explicit decision.

## A. Desktop (working directory `D:\Projects\NSE-release-20261124`)

| # | Step | Changes state | Approval |
|---|---|---|---|
| A1 | Confirm the reviewed source | no | no |
| A2 | Deployment-script validation (PowerShell 5.1) | temporary files only | no |
| A3 | Commit, merge to `master`, record SHA | yes (git) | **yes** |
| A4 | Stop the desktop Host if running | yes | yes |

**A1.** Expected: `4e4d39876aca868c64c1f2f71f7c13a9b84089c5` (source tree without `docs`). It is the certified application tree `45008e3...` plus the deployment-script fix in two files under `tools/` (readiness report, section 14). A different value: stop.

```powershell
$env:GIT_INDEX_FILE="$env:TEMP\nse-fingerprint.index"; git read-tree HEAD; git add -A; git rm -r -q --cached docs; git write-tree; Remove-Item Env:GIT_INDEX_FILE
```

**A2.** The validation below passed on 27 November 13:56 (56 of 56 and the packaging test); repeat if the A1 tree differs. Certifications done 27 November 13:03 on tree `45008e3...`, both MET; not repeated for the `tools/`-only fix (readiness report 14.5). Their script refuses the new tree. Instead run the deployment-script validation under Windows PowerShell 5.1 (local only, about a minute). Expected last lines: `VERDICT: PASS` with the A1 tree. Failure: do not deploy.

```powershell
powershell -NoProfile -File D:\Projects\NSE-release-20261124\docs\overnight\Run-DeploymentCompatibility.ps1
```

**A3.** `master` with the certified release (`17ce4c79...`) and its documentation commit (`0520591...`) was pushed on 27 November. The deployment-script fix (`Fix Windows PowerShell 5.1 deployment configuration parsing and safety validation`) follows it, approved for commit and push on 27 November. Before deploying, expected: `git rev-parse HEAD origin/master` prints the same SHA twice, `git status --porcelain` prints nothing on `master`, and A1 prints the expected tree. Record the SHA. Failure: the deploy scripts refuse a dirty or non-master checkout.

```powershell
git rev-parse HEAD
```

**A4.** Only one machine may hold the FlatTrade session.

```powershell
Get-Service NiftySignalHost -ErrorAction SilentlyContinue
```

```powershell
Stop-Service NiftySignalHost
```

## B. VM checks before any change (elevated PowerShell on the VM; start the VM yourself)

None of these change state.

**B1. Runtimes.** Expected: `Microsoft.NETCore.App 10.x` and `Microsoft.AspNetCore.App 10.x`. Failure: install the .NET 10 ASP.NET Core runtime first.

```powershell
dotnet --list-runtimes
```

**B2. Services, accounts, folders, start types.** Record the output: the folders are needed below and the account of `NiftySignalHost` is used for the new services.

```powershell
Get-CimInstance Win32_Service -Filter "Name LIKE 'NiftySignal%' OR Name LIKE 'postgresql%'" | Format-List Name,State,StartMode,StartName,PathName
```

**B3. Installed release.** Expected: contains `78d9b8bd` (assumed deployed revision). Anything else: stop and compare before deploying.

```powershell
(Get-Item "<Host folder>\NiftySignal.Host.dll").VersionInfo.ProductVersion
```

**B4. Disk and memory.** The deploy refuses under 2 GB free. Backups, the observer database growth and Chromium need more; free space well above the size of both database dumps is required.

```powershell
Get-PSDrive -PSProvider FileSystem
```

```powershell
Get-CimInstance Win32_OperatingSystem | Format-List TotalVisibleMemorySize,FreePhysicalMemory
```

**B5. Environment overrides.** The deploy checks cannot see these. Expected: no names. Any name listed overrides the settings files: resolve it first.

```powershell
[Environment]::GetEnvironmentVariables('Machine').Keys | Where-Object { $_ -match '^(Adaptive|Execution|PaperEngineV2|Trading|ExecutionHealth|ConnectionStrings|DashboardPush|Pricing)(__|:)' }
```

```powershell
Get-ChildItem HKLM:\SYSTEM\CurrentControlSet\Services\NiftySignal* | ForEach-Object { $_.PSChildName; (Get-ItemProperty $_.PSPath).Environment | ForEach-Object { ($_ -split '=')[0] } }
```

**B6. Open paper positions** (ledger database). Expected: no rows (`pev2` not installed: the command reports the missing table, which also means no V2 position). Any row: stop; close or record it before ownership changes. Also confirm in the Dashboard that no NPT1 paper position is open.

```powershell
psql -U <db role> -d <ledger db> -c "SELECT e.strategy_id, e.payload_json::jsonb->>'PositionId' AS position, e.kind FROM pev2.pev2_events e WHERE e.kind='EntryFilled' AND NOT EXISTS (SELECT 1 FROM pev2.pev2_events x WHERE x.kind='ExitFilled' AND x.payload_json::jsonb->>'PositionId' = e.payload_json::jsonb->>'PositionId')"
```

**B7. Database roles in use.** Role names are not in the repository. Record which role each installed service connects as (open each `appsettings.Local.json` yourself; do not paste it anywhere).

```powershell
psql -U <db role> -d niftysignal_adaptive_observer -c "SELECT tablename, tableowner FROM pg_tables WHERE schemaname='public' ORDER BY 1"
```

Expected: the role the Observer will connect as owns these tables (it applies migrations, including `ALTER TABLE`, at start). Failure: use that owner role for the Observer.

**B8. Observer migrations** (observer database; changes nothing). Run now, keep the output, and run the second command again after the first Observer start (E5).

```powershell
@'
SELECT "MigrationId" FROM "__EFMigrationsHistory" WHERE "MigrationId" >= '20261124' ORDER BY 1;
SELECT table_name, count(*) AS event_columns FROM information_schema.columns
 WHERE table_name IN ('adaptive_futures_depth5_bars', 'adaptive_options_depth5_bars')
   AND column_name IN ('AskNotionalEventSum','BidNotionalEventSum','FeedGapEvents','MicroDev5EventSum','NoDepthEvents','RepeatSnapshotsIgnored','Tob5EventSum','ValidDepthEvents','ValidMicroDev5Events')
 GROUP BY table_name ORDER BY table_name;
'@ | Set-Content C:\Temp\observer-migrations.sql -Encoding ascii
```

```powershell
psql -U <db role> -d niftysignal_adaptive_observer -v ON_ERROR_STOP=1 -f C:\Temp\observer-migrations.sql
```

Expected **after** the Observer start: these six rows, and `event_columns` = 9 for both tables.

| Migration | Before deployment |
|---|---|
| `20261124060445_AddFuturesDepth5Sidecar` | normally present; if missing, the Observer applies it too: note it |
| `20261124064802_AddOptionsDepth5Sidecar` | same |
| `20261125074852_AddDepth5EventBasedColumns` | present only if the installed release already includes `78d9b8bd` (B3); otherwise pending |
| `20261126043514_AddObserverRecoveryProgress` | pending |
| `20261126053253_AddBarPublications` | pending |
| `20261126080333_AddObserverCheckpoints` | pending |

Failure after the start (a row missing or a count other than 9): do not activate execution (E1); read the Observer log.

## C. VM backups (changes: writes files only)

**C1.** One per database: tick database, `niftysignal_adaptive_observer`, ledger database. Expected: exit code 0. Failure: do not continue.

```powershell
pg_dump -U <db role> -Fc -f "<backup folder>\<db name>-20261130.dump" <db name>
```

**C2.** Expected: a table of contents, exit code 0.

```powershell
pg_restore --list "<backup folder>\<db name>-20261130.dump"
```

Service folder backups are taken and verified by `vm-deploy.ps1` itself (D4).

## D. VM preparation and deployment

| # | Step | Where | Changes state | Approval |
|---|---|---|---|---|
| D1 | Ledger schema and grants | VM | database | **yes** |
| D2 | Register two services | VM | services | **yes** |
| D3 | Local settings | VM | configuration | **yes** |
| D4 | Preflight, then deploy | desktop | binaries, services | **yes** |

**D1. Migrations.** Observer database (`niftysignal_adaptive_observer`): the Observer applies every pending migration at start. This release needs `20261125074852` (Top-5 event-based columns) and the three new Observer migrations `20261126043514`, `20261126053253`, `20261126080333`; verify with B8 before and after. Execution service: its one migration, `20261126053557_AddExecutionService`, is in the ledger database and is **owner-run** (below). Host tick/volume-bar migrations: none new in this release. **Owner-run only:** the ledger script. Copy `NiftySignal.PaperEngineV2.Data\Sql\pev2-schema.idempotent.sql` to the VM; remove the byte-order mark; apply with the schema owner role. It is idempotent.

```powershell
[IO.File]::WriteAllText("C:\Temp\pev2.sql", [IO.File]::ReadAllText("C:\Temp\pev2-schema.idempotent.sql"), (New-Object Text.UTF8Encoding $false))
```

```powershell
psql -U <schema owner role> -d <ledger db> -v ON_ERROR_STOP=1 -f C:\Temp\pev2.sql
```

Expected: `20261126053557_AddExecutionService` listed, and `pev2` version 2, `pev2-journal/1`. Failure: stop; the script's transactions roll back the failing step.

```powershell
psql -U <schema owner role> -d <ledger db> -c "SELECT * FROM pev2.__pev2_migrations_history ORDER BY 1" -c "SELECT component, journal_schema, schema_version FROM pev2.pev2_schema_versions"
```

Grants, only for roles that differ from the owner (save as `C:\Temp\grants.sql`, replace the role names, apply with the owner role):

```sql
GRANT USAGE ON SCHEMA pev2 TO <execution role>, <dashboard role>, <host role>;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA pev2 TO <execution role>;
GRANT SELECT ON ALL TABLES IN SCHEMA pev2 TO <dashboard role>;
GRANT SELECT, INSERT, UPDATE ON pev2.pev2_execution_state TO <host role>;   -- only used by the in-Host fallback engine
```

```powershell
psql -U <schema owner role> -d <ledger db> -v ON_ERROR_STOP=1 -f C:\Temp\grants.sql
```

Verify. Expected: `t` in every column.

```powershell
psql -U <db role> -d <ledger db> -c "SELECT has_table_privilege('<execution role>','pev2.pev2_execution_journal','INSERT') AS exec_journal, has_table_privilege('<execution role>','pev2.pev2_execution_state','UPDATE') AS exec_state, has_table_privilege('<dashboard role>','pev2.pev2_execution_state','SELECT') AS dash_state, has_table_privilege('<dashboard role>','pev2.pev2_events','SELECT') AS dash_events"
```

After the first Observer start (D4), if the execution or Dashboard role differs from the Observer role, grant read on the observer database, including the new tables:

```sql
GRANT SELECT ON ALL TABLES IN SCHEMA public TO <execution role>, <dashboard role>;
```

The execution role also needs read on the tick database. Keep the Dashboard role read-only.

**D2. Services** (copy `tools\Install-ObserverService.ps1` and `tools\Install-ExecutionService.ps1` to the VM). Folders are examples: follow the layout found in B2. Use the `NiftySignalHost` account (add `-ServiceCredential (Get-Credential)` unless it is LocalSystem). Expected: `registered (manual start, not started)`. Failure: "already registered" changes nothing.

```powershell
.\Install-ObserverService.ps1 -InstallDir C:\Naveen\NiftySignal.Observer
```

```powershell
.\Install-ExecutionService.ps1 -InstallDir C:\Naveen\NiftySignal.ExecutionService
```

**D3. Local settings.** Edit each `appsettings.Local.json` on the VM. Add keys to the existing Host and Dashboard files; never replace them. Do not copy the Host file anywhere else (it holds broker credentials).

Host (add; keep everything else):

```json
{
  "Adaptive": { "Observer": { "Process": "Separate" }, "Top5Microstructure": { "Enabled": true, "OptionsEnabled": true } },
  "PaperEngineV2": { "Enabled": false },
  "Trading": { "PaperLive": { "Enabled": false } }
}
```

Observer (new file). Also copy every other `Adaptive:*`, `Pricing:*` and `DashboardPush:*` value the Host file overrides; do not set `Adaptive:Clock:Mode` (the default is the fixed 13,650 clock):

```json
{
  "ConnectionStrings": { "NiftySignalDb": "<tick database connection, observer role>" },
  "Adaptive": {
    "Top5Microstructure": { "Enabled": true, "OptionsEnabled": true },
    "Recovery": { "TimeLimitSeconds": 300, "TimeoutRetryBackoffSeconds": 30, "ReadWindowSeconds": 120, "CheckpointsEnabled": true, "CheckpointIntervalSeconds": 60, "CheckpointRetention": 3 }
  },
  "Pricing": { "RiskFreeRate": "<Host value>" },
  "DashboardPush": { "HubUrl": "<Host value>" }
}
```

Execution (new file), **disabled** for the first deployment:

```json
{
  "ConnectionStrings": { "NiftySignalDb": "<tick database connection, execution role>", "TradingPaperLive": "<ledger database connection, execution role>" },
  "Telegram": { "BotToken": "<existing bot token>", "ChatId": "<existing chat id>" },
  "Execution": { "Enabled": false, "BrokeragePerOrder": 0, "Alerts": { "OrderRejected": true, "PartialFill": true, "ExecutionFailure": true, "Opened": true, "Closed": true } }
}
```

The alert switches are your choice (shipped: all off). Strategy files ship with the service; do not edit them on the VM.

Dashboard (add): `ConnectionStrings:TradingPaperLive` (read-only role), and keep your current `AdaptiveScreenshots` and `Telegram` values. `ExecutionHealth:Required` is set at activation (E1).

**D3a. Observer connection pooling** (VM, changes nothing, prints no secret). The recovery time measured on the desktop depends on connection pooling. Expected: `False`. `True`: remove the `Pooling` setting from that connection string before activation. B5 must also have shown no connection-string override.

```powershell
(Get-Content "C:\Naveen\NiftySignal.Observer\appsettings.Local.json" -Raw) -match '(?i)Pooling\s*=\s*(false|no|0)'
```

Actual VM recovery performance is **unmeasured**. On the first mid-session recovery, read the duration from the Observer log line `Adaptive observer recovery complete`.

**D4. Deploy** (desktop, on `master`).

```powershell
.\vm-deploy.ps1 -PreflightOnly
```

Expected: guard allows, Tailscale online, snapshot lists four services, free space. Failure: fix; nothing changed.

```powershell
.\vm-deploy.ps1
```

Expected: tests pass; verified backups of Host and Dashboard ("nothing to back up" for the two new services); every service verified by file hash and build identity; `Done: master @ <sha>`. Failure: stopped services stay stopped. Fix and re-run, or roll back (F). A failed Observer check leaves **no observer running**. The Dashboard step downloads Chromium for screenshots into its own `.playwright-browsers` folder and needs internet access on the VM.

## E. Activation and verification

**E1** (VM, approval **yes**, changes configuration): set `Execution:Enabled=true` in the execution file and `ExecutionHealth:Required=true` in the Dashboard file. Do this only if A2 passed, B6 returned nothing and D4 verified.

**E2** (VM, changes services), in this order; expected `Running` after each:

```powershell
Restart-Service NiftySignalHost
```

```powershell
Restart-Service NiftySignalObserver
```

```powershell
Restart-Service NiftySignalExecution
```

```powershell
Restart-Service NiftySignalDashboard
```

**E3. Ownership.** Expected: exactly one row, a heartbeat seconds old, owner = the execution service.

```powershell
psql -U <db role> -d <ledger db> -c "SELECT component, owner, epoch, status, to_timestamp((heartbeat_utc_ticks - 621355968000000000) / 10000000.0) FROM pev2.pev2_execution_state"
```

**E4. Four strategies.** Expected: build identity with the release SHA, four `ENABLED` lines, `TOTAL_VOTE_4_OF_6` disabled, warm-up 20 bars, `Execution ownership taken with epoch N`.

```powershell
Get-Content (Get-ChildItem C:\Naveen\NiftySignal.ExecutionService\logs\*.log | Sort-Object LastWriteTime | Select-Object -Last 1) -Tail 60
```

**E5.** Run the second B8 command: six migration rows, 9 and 9. Observer log: build identity, no ownership refusal, recovery reaches Live. Host log: no adaptive observer start, no Paper Engine V2 start. After 09:20: Dashboard shows four market-summary grids, Option Price Averages, exactly two paper grids with four rows each, no execution banner. Telegram "Send pre-live test" sends a real message: your decision.

**E6.** When verified, set both new services to start automatically (approval yes).

```powershell
Set-Service NiftySignalObserver -StartupType Automatic
```

```powershell
Set-Service NiftySignalExecution -StartupType Automatic
```

**E7. Processing-freshness blocks** (first session, changes nothing). After 09:20 the paper grids show no lasting `Entries blocked: observer processing ...`. List the warnings (at most one per minute):

```powershell
Select-String -Path C:\Naveen\NiftySignal.ExecutionService\logs\*.log -SimpleMatch 'Paper entries are blocked: observer processing' | Select-Object -Last 40 | ForEach-Object { $_.Line }
```

A warning at start-up is expected. Repeated warnings during market hours mean the Observer does not keep within 5 seconds on the VM: entries are being refused, which is safe, and the limit must not be raised without a decision.

Failure in E: set `Execution:Enabled=false`, restart the execution service, and use section F if the data pipeline is affected.

## F. Rollback (VM, approval yes)

Follow `NSE_EXECUTION_SERVICE_RUNBOOK.md` section 6. Order: stop and disable `NiftySignalExecution` and `NiftySignalObserver` **first**; record open positions; restore Host and Dashboard from `<install parent>\_backups\<service>-<stamp>` with the service stopped (or deploy the previous SHA); leave the added tables and columns; never run two writers. Restore a database dump only if data is damaged; the schema changes are additive.

```powershell
Stop-Service NiftySignalExecution, NiftySignalObserver
```

```powershell
Set-Service NiftySignalExecution -StartupType Disabled
```

```powershell
Set-Service NiftySignalObserver -StartupType Disabled
```

Restore one service (example: Host). Expected: robocopy exit code below 8, then the old build identity (B3).

```powershell
Stop-Service NiftySignalHost
```

```powershell
robocopy "<install parent>\_backups\NiftySignalHost-<stamp>" "<Host folder>" /E /XF .backup-verified
```

```powershell
Start-Service NiftySignalHost
```

## G. Regenerating release packages from the committed revision (desktop)

`deploy.ps1` builds, tests and publishes from an archive of the committed `HEAD`, so the deployed binaries are always regenerated from the reviewed SHA. Packages under `artifacts\` were built from the uncommitted tree for local validation and are **not** deployment artifacts.
