# Incident report: Adaptive Observer stall, 25 November 2026

Status: **root cause not established.** Source analysis plus the database output the owner pasted during the incident. No Host, Windows or PostgreSQL logs for 07:30-08:30 UTC were available, and no VM access.
The analysis was made against `bd0b08f`; line numbers below refer to that revision. This report consolidates `docs/NSE_PRODUCTION_RELIABILITY_PLAN.md` (uncommitted, worktree `D:\Projects\NSE-production-reliability`).

## 1. Timeline (UTC; IST = +5:30)

| Time | Event | Status |
|---|---|---|
| 05:27:38 | Previous recovery of session 77 completed | Confirmed (runtime row) |
| 07:36:53 / 07:36:56 | Last processed source tick / last heartbeat, 431 bars | Confirmed (runtime row) |
| 07:36:56 - 07:40:50 | No heartbeat | Gap confirmed, cause unknown |
| about 07:40:50 | `RecoverAsync` started; a Host trading connection dates from about 07:40 | Start confirmed; a Host process (re)start at that time is an inference |
| 08:16 | Still `Rebuilding`, no error, same start timestamp | Confirmed |
| 08:22:08 | Owner restarted the Host; new recovery began | Confirmed |
| 08:23:39 | That recovery 90 s old and reading the databases | Confirmed |
| later | Outcome of the second recovery | Not reported |

The mandate also reports: grids stopped refreshing while live prices and ingestion continued, the agent snapshot endpoint stopped responding, and a Dashboard restart did not help. The first two follow from the Observer publishing nothing (prices and ingestion use other paths). The unresponsive agent snapshot endpoint (a Dashboard endpoint) is **not explained** by the source analysis.

## 2. Answers to the mandate's questions

| Question | Answer |
|---|---|
| Stopped, blocked, or running without publishing? | Unknown for 07:36:56-07:40:50. After 07:40:50 a recovery ran and, by design of that build, wrote no heartbeat or progress, so "alive" and "dead" looked the same. |
| Did the synchronous paper-live handoff contribute? | Possible, unproven. It runs on the Observer thread with a 30 s store timeout, and any failure rebuilds the whole run (load, verify, replay) on that thread. The logged slow commit (2.96 s) is far short of 4 minutes, and its logger category is needed (NPT1 uses the same class on another thread). |
| Database behaviour | A 2.96 s commit shows the trading database can stall for seconds. A depth window query has a measured ~24 s per page under a bad plan. No lock or statement logs for the window. |
| Recovery phases / bottleneck | Whole-day read, normalize, sort, depth streams, replay with about 22-24 SQL commands per bar, verify. Not timed in that build. Repo measurements: 7.6-15.5 s full day without Top-5; 2-3 min reported with options depth. **Never measured with Top-5 on at full-day volume.** |
| Data lost or duplicated? | **Not verified.** Raw ticks continued to be stored; recovery verifies persisted bars instead of overwriting them. Whether any paper decision or ledger record was missed or duplicated needs the owner queries in section 4. While the Observer was down no strategy was evaluated and the 15:10 / terminal exit logic could not have advanced. |

## 3. Hypotheses (none confirmed)

Stall: S1 crash or restart of the Host; S2 paper-live fault and in-thread rebuild; S3 database or storage stall; S4 memory/GC pressure; S5 thread-pool starvation from sync-over-async; S6 unhandled exception.
Long recovery: R1 bad query plan on depth windows; R2 inherently slow whole-day replay with Top-5 on; R3 memory/GC; R4 hung query or call; R5 earlier failed attempt hidden by the "invisible failure" defect (less likely: one unchanged start timestamp).
Unexplained: session 75 was still `Rebuilding` from 24 Nov 12:04 UTC; session 76 shows `Closed` with an odd heartbeat time.

## 4. Owner-run evidence collection (read-only)

Run on the VM in your own PowerShell. Nothing here changes state. Inspect before sharing.

```powershell
Get-TimeZone | Select-Object Id, BaseUtcOffset
```
```powershell
Get-CimInstance Win32_Service | Where-Object { $_.PathName -like '*NiftySignal*' } | Select-Object Name, State, StartMode, ProcessId, PathName
```
```powershell
Get-Process | Where-Object { $_.ProcessName -like 'NiftySignal*' } | Select-Object Id, ProcessName, StartTime, @{n='WS_MB';e={[int]($_.WorkingSet64/1MB)}}, @{n='Threads';e={$_.Threads.Count}}
```
Service restarts and crashes, 07:00-09:00 UTC:
```powershell
$from = ([datetime]'2026-11-25T07:00:00Z').ToLocalTime(); $to = ([datetime]'2026-11-25T09:00:00Z').ToLocalTime()
Get-WinEvent -FilterHashtable @{LogName='System','Application'; StartTime=$from; EndTime=$to} |
  Where-Object { $_.Message -match 'NiftySignal|postgres' -or $_.Id -in 1000,1001,1026 } |
  Select-Object TimeCreated, Id, ProviderName, @{n='Msg';e={$_.Message.Split("`n")[0]}}
```
Host log extract (adjust the install folder), secrets redacted:
```powershell
$log = Get-ChildItem C:\Naveen\NiftySignal.Host\logs -Filter '*20261125*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$from = [datetimeoffset]'2026-11-25T07:30:00Z'; $to = [datetimeoffset]'2026-11-25T08:30:00Z'
New-Item -ItemType Directory -Force C:\Temp | Out-Null
Get-Content $log.FullName | ForEach-Object {
  if ($_ -match '^(?<ts>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d(\.\d+)?( [+-]\d\d:\d\d)?)') { $t = [datetimeoffset]::MinValue; if ([datetimeoffset]::TryParse($matches.ts, [ref]$t) -and $t -ge $from -and $t -le $to) { $_ } } } |
  ForEach-Object { $_ -replace '(?i)(password|pwd|secret|token|apikey|api_key)\s*[=:]\s*[^;\s,"]+', '$1=<redacted>' } |
  Set-Content C:\Temp\host-0730-0830-redacted.txt
Select-String C:\Temp\host-0730-0830-redacted.txt -Pattern 'Starting NiftySignal Host|Build identity|Adaptive observer|recover|Paper-live|PAPER-LIVE|depth5|timed out|Timeout|HALTED|Unhandled|OutOfMemory' | Select-Object -First 80
```
Also record the timestamp of every `Starting NiftySignal Host` line that day.

PostgreSQL (read-only):
```sql
-- ticks database
select relname, n_live_tup, n_mod_since_analyze, last_analyze, last_autoanalyze, last_autovacuum from pg_stat_user_tables where relname ilike '%tick%';
select indexrelname, idx_scan from pg_stat_user_indexes where relname ilike '%tick%';
select name, setting from pg_settings where name in ('log_min_duration_statement','log_lock_waits','log_checkpoints','logging_collector','log_directory','checkpoint_timeout','max_wal_size','shared_buffers','work_mem');
select * from pg_stat_checkpointer;
-- niftysignal_adaptive_observer
select "Id","TradeDate","ModelVersion" from adaptive_sessions order by "Id" desc limit 5;
select "SessionId","RuntimeStatus","LastRecoveryStartedUtc","LastRecoveryCompletedUtc","LastHeartbeatUtc","LastCompletedBarSeq",left("LastError",200) from adaptive_observer_runtime order by "SessionId" desc limit 3;
select count(*), count(distinct "BarSeq"), min("BarSeq"), max("BarSeq") from adaptive_future_bars where "SessionId" = 77;
```
Check the PostgreSQL server log for the same hour (checkpoint, lock wait, cancel and slow-statement lines).

## 5. Process dump procedure for a recurrence

A dump holds process memory: **connection strings, tokens and market data may be inside.** Keep it on the VM or an encrypted drive you control, never upload it, delete it after analysis. Staging the tools or enabling Windows crash dumps changes production configuration and needs your approval; do it before market open.

- Trigger: the Dashboard/Telegram critical alert for a stale heartbeat or an over-long rebuild.
- Budget 5 minutes; with open positions, restore service when it is spent.
  1. `dotnet-counters monitor --process-id <HostPid> System.Runtime` for 30 s (thread-pool queue, GC pause, working set); snapshot `pg_stat_activity` and locks.
  2. `dotnet-stack report --process-id <HostPid>` (short pause; usually shows where the Observer is waiting).
  3. Only if still unexplained: `procdump -accepteula -ma -r <HostPid> C:\Dumps\host` (clone-based, pauses briefly). Do not use Task Manager or a plain full dump during market hours.
- Analyse offline: `dotnet-dump analyze` (`clrthreads`, `clrstack`, `syncblk`, `threadpool`, `dumpheap -stat`).

## 6. What changed since, and what residual risk remains

Changed on this branch (see the plan document): recovery now publishes phase and progress, has a time limit, records failures, reads in bounded slices; heartbeats carry the write time; the Dashboard process alerts on a stale or failed Observer.
Changed in the continuation: the Host registers neither paper bridge when disabled, so no synchronous paper work runs on the Observer thread under the intended configuration; paper execution runs in the separate `NiftySignalExecution` service with its own protective exits. Not changed: no durable recovery checkpoint (finding F73); the Observer itself stays in the Host process; the live poll and session creation still hold whole backlogs in memory (F74, F75). **The cause of the 25 November stall is unresolved and could recur.** Mandate section 42 requires it to be resolved or accepted in writing before real money.
