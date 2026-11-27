# Owner-run benchmark procedure (recorded sessions, scratch databases only)

**Status: not run.** No figure in this procedure has been produced on real data by this work. Synthetic figures are not certification. Paper release stays NOT READY until sections 4–6 pass, including the certification of 5.1 on representative Top-5 data; real money is NO-GO.

Nothing here needs write access to a production database. All commands are for the owner, in the owner's own PowerShell, from the repository root, outside market hours.

## 1. Pass conditions (starting values)

| Measure | Pass |
|---|---|
| Full-session recovery (cold, and warm with verification) | **under 300 s** (mandatory), no timed-out attempt |
| Checkpoint recovery (mid-session restart, futures and options Top-5) | section 5.1 verdict `CRITERIA MET`: under 300 s, a real checkpoint restore (not a full-replay fallback), state identical to both references |
| Restart and replay parity | every `equalAfterClose` and `equal` field true; negative control detected |
| Live catch-up | pending ticks return to zero after the 2x peak stress; no growing backlog at 1x |
| Quote age at fill | at most the strategy's `maxQuoteAgeMs` (2000 ms shipped) |
| Bar-to-decision lag | under `maxProcessingLagMs` |
| Top-5 / L1 replay parity (5.2) | identical `core_hash` in all three modes, `mismatches=0`, `core_same_on_restart=True` |
| Memory | recorded; **no limit has been agreed**. Compare with the VM's free memory with all four services and PostgreSQL running |

A result certifies only the machine, build and data it ran on. A developer machine is not the VM. Do not run any of this on the VM during market hours.

**Status on 26 November 2026:** no stage of this procedure has been run by the developer sessions. Creating a scratch performance database and the read-only query on `niftysignal_vm_copy` were both refused by the session permission controls and were not retried. It is an owner-run step. Later that day the owner authorised the work; the read-only login check was refused again by the tool permission control and was not retried. The scratch cluster, baseline worktree and both builds are prepared (`NSE_REAL_DATA_CERTIFICATION_REPORT_2026-11-26.md`); for 23 November follow `NSE_CERTIFICATION_RUNSHEET_2026-11-23.md` (skip the `initdb` of 3.3 and the `worktree add` of 3.4).

## 2. Dataset

- A **copy** of the tick database (`instruments`, `ticks`) holding recorded NIFTY sessions: the busiest day available, a typical day, and 25 November 2026 if it was recorded (the incident day; its root cause is unresolved).
- Strong-threshold calibration and the restart stage need earlier sessions in the same copy. The first sessions of a dataset can lack priors: list them in `ADAPTIVE_PERF_EXCLUDE_DAYS`.
- Top-5 measurements need a day recorded with depth levels 2–5.
- Take the copy by `pg_dump` / `pg_restore` (a read of the source) or use an existing local copy. Never point any command below at the production server.

## 3. Setup

### 3.1 Permissions

| Role | Rights |
|---|---|
| Source (the copy) | `CONNECT`, `USAGE` on the schema, `SELECT` on `instruments` and `ticks`. No write right. The performance tool also sets `default_transaction_read_only=on` |
| Scratch cluster | A **separate** PostgreSQL cluster (different host:port; the tool refuses otherwise) with a superuser. The tool **drops and creates** databases named `perf_*` there |

Keep passwords in `%APPDATA%\postgresql\pgpass.conf`, not in the commands.

### 3.2 Disk

Per-row size has not been measured on real data. Measure it on the copy:

```sql
SELECT pg_total_relation_size('ticks') / NULLIF(count(*), 0) AS bytes_per_row FROM ticks;
SELECT count(*) FROM ticks WHERE "ReceivedAt" >= '<day> 03:45:00+00' AND "ReceivedAt" < '<day> 10:10:00+00';
```

The scratch cluster holds the selected tokens of **one** day twice (`perf_source`) plus one small observer database per run. Reserve at least `3 × bytes_per_row × rows_of_largest_day`, plus `max_wal_size`, plus 5 GB. This is an estimate, not a measurement. The restart stage keeps up to six observer databases per day.

### 3.3 Scratch cluster

```powershell
& "C:\Program Files\PostgreSQL\18\bin\initdb.exe" -D D:\perf-scratch\pg -U postgres -A trust -E UTF8
```

```powershell
& "C:\Program Files\PostgreSQL\18\bin\pg_ctl.exe" -D D:\perf-scratch\pg -o "-h 127.0.0.1 -p 54340" -l D:\perf-scratch\pg.log start
```

Trust authentication is acceptable only for this throw-away loopback cluster. Check first that port 54340 is free.

### 3.4 The three builds under test

| Id | Source | Checkpoints | Built from |
|---|---|---|---|
| **A** | original deployed revision `78d9b8b` | do not exist in it | its own isolated worktree |
| **B** | new implementation | disabled | the release working tree |
| **C** | new implementation | enabled | the **same build** as B |

B is **not** a baseline for A: it contains every change of this release. Never label a B result "original".

Create the baseline worktree once. It adds a detached checkout; it changes no branch and no file of the release tree.

```powershell
git -C D:\Projects\NSE-release-20261124 worktree add --detach D:\perf-scratch\nse-78d9b8b 78d9b8bd84d0b9bb38dd91a80ae89f0dad8fd5fe
```

```powershell
dotnet build D:\perf-scratch\nse-78d9b8b\tools\AdaptiveLivePerformance\AdaptiveLivePerformance.csproj -c Release
```

```powershell
dotnet build D:\Projects\NSE-release-20261124\tools\AdaptiveLivePerformance\AdaptiveLivePerformance.csproj -c Release
```

Identify each source tree. A has a commit. While the release work is uncommitted, identify B / C by the Git tree hash of the working tree, written through a throw-away index (no branch, commit or staging area of the repository changes):

```powershell
git -C D:\perf-scratch\nse-78d9b8b rev-parse HEAD
```

```powershell
git -C D:\perf-scratch\nse-78d9b8b status --short
```

```powershell
$env:GIT_INDEX_FILE="D:\perf-scratch\release.index"; git -C D:\Projects\NSE-release-20261124 add -A; $releaseTree = git -C D:\Projects\NSE-release-20261124 write-tree; Remove-Item Env:\GIT_INDEX_FILE; $releaseTree
```

Record the printed hash. If any file changes afterwards, rebuild, take a new hash and repeat the B and C runs.

### 3.5 Common settings

Adjust the host, port, database and role of the source line.

```powershell
$env:ADAPTIVE_PERF_SOURCE = "Host=localhost;Port=5432;Database=niftysignal_vm_copy;Username=research_ro"
```

```powershell
$env:ADAPTIVE_PERF_ADMIN = "Host=127.0.0.1;Port=54340;Database=postgres;Username=postgres"
```

```powershell
$env:ADAPTIVE_PERF_OUT = "D:\perf-scratch\out"
```

The days are fixed explicitly so that all three builds process the same data. Replace the examples with the busiest, a typical and the incident day.

```powershell
$env:ADAPTIVE_PERF_DAYS="all"; $env:ADAPTIVE_PERF_LIVE_DAYS="2026-11-25"; $env:ADAPTIVE_PERF_COLD_DAYS="2026-11-25"; $env:ADAPTIVE_PERF_RESTART_DAYS="2026-11-25"; $env:ADAPTIVE_PERF_STRESS="1"
```

Environment variables persist in the PowerShell window: set every one listed for each run. Use the same machine, the same scratch cluster and an otherwise idle system; run each build at least twice and report both.

## 4. Observer runs, per build (`tools\AdaptiveLivePerformance`)

The tool drives the production live and recovery code. B and C apply the default `Adaptive:Recovery` limits, including the 300 s limit: a recovery that exceeds it ends the run with a timeout error, and **that is a FAIL**. A has no such limit: compare its printed times with 300 s yourself. The tool's other code is the same in A and in B / C apart from the checkpoint switch.

Each run line below sets its label, stages and checkpoint switch, then runs one build.

### Run A: original revision 78d9b8b

```powershell
$env:ADAPTIVE_PERF_LABEL="A-full"; $env:ADAPTIVE_PERF_COMMIT="78d9b8b"; $env:ADAPTIVE_PERF_CHECKPOINTS="0"; $env:ADAPTIVE_PERF_STAGES="inputs,live,recovery,dashboard,plans,stress,digest"; dotnet run -c Release --no-build --project D:\perf-scratch\nse-78d9b8b\tools\AdaptiveLivePerformance
```

```powershell
$env:ADAPTIVE_PERF_LABEL="A-restart"; $env:ADAPTIVE_PERF_COMMIT="78d9b8b"; $env:ADAPTIVE_PERF_CHECKPOINTS="0"; $env:ADAPTIVE_PERF_STAGES="restart"; $env:ADAPTIVE_PERF_RESTART_MODE="full"; dotnet run -c Release --no-build --project D:\perf-scratch\nse-78d9b8b\tools\AdaptiveLivePerformance
```

### Run B: new implementation, checkpoints disabled

```powershell
$env:ADAPTIVE_PERF_LABEL="B-full"; $env:ADAPTIVE_PERF_COMMIT="tree-$releaseTree"; $env:ADAPTIVE_PERF_CHECKPOINTS="0"; $env:ADAPTIVE_PERF_STAGES="inputs,live,recovery,dashboard,plans,stress,digest"; dotnet run -c Release --no-build --project D:\Projects\NSE-release-20261124\tools\AdaptiveLivePerformance
```

```powershell
$env:ADAPTIVE_PERF_LABEL="B-restart"; $env:ADAPTIVE_PERF_COMMIT="tree-$releaseTree"; $env:ADAPTIVE_PERF_CHECKPOINTS="0"; $env:ADAPTIVE_PERF_STAGES="restart"; $env:ADAPTIVE_PERF_RESTART_MODE="full"; dotnet run -c Release --no-build --project D:\Projects\NSE-release-20261124\tools\AdaptiveLivePerformance
```

### Run C: new implementation, checkpoints enabled

```powershell
$env:ADAPTIVE_PERF_LABEL="C-full"; $env:ADAPTIVE_PERF_COMMIT="tree-$releaseTree"; $env:ADAPTIVE_PERF_CHECKPOINTS="1"; $env:ADAPTIVE_PERF_STAGES="inputs,live,recovery,dashboard,plans,stress,digest"; dotnet run -c Release --no-build --project D:\Projects\NSE-release-20261124\tools\AdaptiveLivePerformance
```

```powershell
$env:ADAPTIVE_PERF_LABEL="C-restart"; $env:ADAPTIVE_PERF_COMMIT="tree-$releaseTree"; $env:ADAPTIVE_PERF_CHECKPOINTS="1"; $env:ADAPTIVE_PERF_STAGES="restart"; $env:ADAPTIVE_PERF_RESTART_MODE="full"; dotnet run -c Release --no-build --project D:\Projects\NSE-release-20261124\tools\AdaptiveLivePerformance
```

- The difference B to C in the `recovery` stage is the cost of saving checkpoints during a replay. A cold observer database has no prior-session history, so Strong/Weak is uncalibrated in that stage.
- In C-restart a recovery counts as checkpoint recovery **only** if the console shows "continues from checkpoint" for it. Without that line it was a full replay. In these stages checkpoints are saved every 60 s of **wall** time while the simulation runs faster than the market, so C-restart is **not** a certification of checkpoint recovery: use section 5.
- Exit code 1 means a parity difference.

### Comparing the builds

Compare times and memory only between runs on the same days. For values, compare the uninterrupted run's dump of each build (equal hashes mean equal persisted values; a difference must be explained column by column before it is accepted):

```powershell
Get-FileHash D:\perf-scratch\out\dump-A-restart-20261125.json, D:\perf-scratch\out\dump-B-restart-20261125.json, D:\perf-scratch\out\dump-C-restart-20261125.json
```

### Outputs

`D:\perf-scratch\out\summary-<label>.json` and, for restart runs, `dump-<label>-<day>.json`.

| Report field | Meaning |
|---|---|
| `commit`, `machine` | which build, which machine |
| `inputs`, `inputsAggregate` | tick rates per second and per 250 ms |
| `recovery[].totalWallMs`, `stageMsSummed`, `workingSetPeakMb`, `managedHeapPeakMb`, `allocatedMb` | recovery time and memory |
| `restartContinuation[].recovery.wallMs` and memory fields, `equalAfterClose` | mid-session recovery and parity |
| `live[].pollMsAll` / `pollMsWithBar`, `maxPendingTicks`, `memory` | live processing |
| `stress[]` (`maxPendingTicks`, `drainSecondsAfterIngestEnd`) | catch-up at twice the observed peak |
| `dashboard[]` | Dashboard query times |
| `queryPlans`, `equivalence` | plans on the source copy; replay parity |

### What the section 4 stages do **not** measure

- `perf_source` holds only the selected tokens of one day: database query times are **optimistic** compared with the full tick table. Cover this with 5.2 and 6.1.
- Only level-1 depth is staged: **no Top-5 load and no Top-5 recovery.** Section 5 covers that.
- The Observer process only processes the current date, so a recorded day cannot be replayed by starting the Windows service against it.
- One process, no Host ingestion, Dashboard or execution service competing for the machine.

## 5. Top-5: checkpoint recovery certification and replay parity

### 5.1 Checkpoint recovery with futures and options Top-5 (build C)

Stage `certify` (`CheckpointCertification.cs`). **This stage has been built but never executed: the owner's first run is its first execution.** The Observer code it drives is covered by automated tests on synthetic data (`AdaptiveCheckpointTests`).

It copies one recorded day **with its stored depth** into the loopback scratch cluster and runs the production services four times: full replay (reference and full-session recovery duration), uninterrupted live processing, live processing that is dropped mid-session and restarted from a checkpoint, and the same restart without checkpoints. During accelerated live processing the checkpoint store is given the simulated session clock, so checkpoints are saved every 60 s of **market** time; during the recovery after the restart it uses the wall clock, as in production.

It refuses a scratch server that is not loopback. It drops and creates databases named `perf_cert_*`. Reserve disk for two copies of the day's ticks with depth and four observer databases (not measured).

```powershell
$env:ADAPTIVE_PERF_LABEL="C-certify"; $env:ADAPTIVE_PERF_COMMIT="tree-$releaseTree"; $env:ADAPTIVE_PERF_STAGES="certify"; $env:ADAPTIVE_PERF_CERTIFY_DAY="2026-11-25"; $env:ADAPTIVE_PERF_CERTIFY_RESTART_IST="14:30:00"; $env:ADAPTIVE_PERF_CERTIFY_DOWNTIME_SECONDS="60"; dotnet run -c Release --no-build --project D:\Projects\NSE-release-20261124\tools\AdaptiveLivePerformance
```

Run it at least for a late restart (14:30, the most history) and an early one (`ADAPTIVE_PERF_CERTIFY_RESTART_IST="09:45:00"`), with different labels.

| Exit | Console | Meaning |
|---|---|---|
| 0 | `CRITERIA MET on the supplied day` | every criterion below held for this day, machine and build |
| 1 | `NOT CERTIFIED: <reasons>` | at least one did not |
| 2 | message | settings refused or no data |

Criteria, all required: five-level depth present for the future and the options; ticks cover 09:20 to 15:25 IST; the restart **continued from a checkpoint** (console line, and fewer ticks replayed than were visible: a full-replay fallback fails); back to `Live` within 300 s; tick accounting at the end equal to the uninterrupted run (read and processed cursors, pending ticks, partial bar, bar count); every market table (bars, rolling, bands, residuals, futures / basis / options sidecars, per-strike rows and attestations, close ATM, futures and options depth-5 rows and attestations, observations, anchors, health) equal to both references; one publication per bar, where the only accepted difference from the uninterrupted run is a bar published after the restart arriving as unverified.

Report `certification-<label>-<day>.json`: `inputs` (depth coverage), `fullReplay`, `checkpointRestart` (`checkpointSaveMs`, `newestCheckpointBytes`, `restart.wallSeconds`, `restart.checkpointRestoreMs`, `restart.ticksReplayed`, `peakWorkingSetMb`, `peakManagedHeapMb`), `restartWithoutCheckpoints`, `parity`, `failures`, `verdict`.

Limits of what it shows: only the day's selected tokens are in the scratch source; prior-session history is built from the copy; no other service competes for the machine. A checkpoint restore also depends on how recent the newest checkpoint is, which on the VM depends on live load. Whether the day is representative is the owner's decision. **Recovery is not certified until this verdict has been obtained on representative real data and the report has been reviewed.**

### 5.2 Top-5 / L1 replay parity on the full tick table (no checkpoints)

This test runs the production full replay **against the full source copy** (read only) and writes to a temporary database on the scratch cluster. It does **not** exercise checkpoints or apply the 300 s limit. Set the day, the future token, the weekly expiry and the bar volume of the chosen session (from `adaptive_sessions` of a copy of the observer database, or from `instruments`). Run the three modes one at a time, from the release tree, each in its own process.

```powershell
$env:NSE_ADAPTIVE_MIGRATION_TEST_DB="Host=127.0.0.1;Port=54340;Database=postgres;Username=postgres"; $env:NS_DEPTH5_CONN="Host=localhost;Port=5432;Database=niftysignal_vm_copy;Username=research_ro"; $env:NS_DEPTH5_DAY="2026-11-25"; $env:NS_DEPTH5_FUT="<future token>"; $env:NS_DEPTH5_WEEKLY="<yyyy-MM-dd>"; $env:NS_DEPTH5_BARVOL="<bar volume>"; $env:NS_DEPTH5_OUT="D:\perf-scratch\out\depth5.txt"
```

```powershell
$env:NS_DEPTH5_OPT="off"; dotnet test NiftySignal.Tests\NiftySignal.Tests.csproj -c Release --filter FullyQualifiedName~OptionsDepth5HostBenchmark
```

```powershell
$env:NS_DEPTH5_OPT="futures"; dotnet test NiftySignal.Tests\NiftySignal.Tests.csproj -c Release --filter FullyQualifiedName~OptionsDepth5HostBenchmark
```

```powershell
$env:NS_DEPTH5_OPT="both"; dotnet test NiftySignal.Tests\NiftySignal.Tests.csproj -c Release --filter FullyQualifiedName~OptionsDepth5HostBenchmark
```

A test that "passes" in under a few seconds and writes no line was skipped: check the variables. Compare `core_hash`, `recovery_insert_s`, `recovery_verify_s`, `peak_ws_mb` across the three lines. The universe is the 20 strikes nearest the open, which is close to but not necessarily the production universe.

## 6. Shadow-session measurements (needs a deployment the owner has approved separately)

These cannot be produced from recorded data with the present tools. All queries are read-only. They have not been run against real data.

Run them on the database machine. Sections 6.2–6.4 read the databases of the running shadow deployment (production data, read only), with a role limited to `SELECT`. Save each query to a file and run it read-only; `psql` asks for the password itself:

```powershell
$env:PGOPTIONS = "-c default_transaction_read_only=on"
```

```powershell
& "C:\Program Files\PostgreSQL\18\bin\psql.exe" -X -h localhost -p 5432 -U <read-only role> -d <database> -f D:\perf-scratch\<query>.sql
```

| Query | Database | `SELECT` on |
|---|---|---|
| 6.1 | the tick copy | `ticks` |
| 6.2 | the observer database (`niftysignal_adaptive_observer`) | `adaptive_bar_publications`, `adaptive_future_bars` |
| 6.3 | the paper ledger (`ConnectionStrings:TradingPaperLive`) | `pev2.pev2_events`, `pev2.pev2_execution_journal` |
| 6.4 | the paper ledger | `pev2.pev2_live_marks` |

### 6.1 Recovery slice plan on the full tick table (can be run on the copy now)

```sql
EXPLAIN (ANALYZE, BUFFERS)
SELECT "Token", "Id", "ExchangeTimestamp", "ReceivedAt", "LastPrice", "Volume", "OpenInterest" FROM ticks
WHERE "Token" = ANY('{<session tokens>}') AND "ReceivedAt" >= '<day> 07:30:00+00' AND "ReceivedAt" < '<day> 07:32:00+00'
  AND "ExchangeTimestamp" >= '<day> 03:44:00+00' AND "ExchangeTimestamp" < '<day> 18:30:00+00'
ORDER BY "Id";
```

Expect an index range scan. A full session is about 190 such slices: the product must be well inside 300 s.

### 6.2 Publication latency (observer database)

```sql
SELECT percentile_cont(ARRAY[0.5, 0.95, 0.99]) WITHIN GROUP (ORDER BY extract(epoch FROM p."PublishedAtUtc" - b."EndAvailableAtUtc")) AS seconds, count(*)
FROM adaptive_bar_publications p JOIN adaptive_future_bars b ON b."SessionId" = p."SessionId" AND b."BarSeq" = p."BarSeq"
WHERE b."EndAvailableAtUtc" >= '<start of an uninterrupted live period>' AND b."EndAvailableAtUtc" < '<its end>';
```

Exclude recovery periods: replayed bars are published long after they ended.

### 6.3 Quote-to-execution latency (paper ledger)

```sql
SELECT kind, percentile_cont(ARRAY[0.5, 0.95, 0.99]) WITHIN GROUP (ORDER BY extract(epoch FROM (payload_json::jsonb ->> 'At')::timestamptz - (payload_json::jsonb ->> 'QuoteAvailableAt')::timestamptz)) AS seconds, count(*)
FROM pev2.pev2_events WHERE kind IN ('EntryFilled', 'ExitFilled', 'PartialExitFilled') GROUP BY kind;

-- bar-to-decision lag of the execution service (its journal; pev2_dispositions belongs to the in-Host engine and stays empty here)
SELECT percentile_cont(ARRAY[0.5, 0.95, 0.99]) WITHIN GROUP (ORDER BY (payload_json::jsonb -> 'Disposition' ->> 'ProcessingLagMs')::int) AS ms,
       max((payload_json::jsonb -> 'Disposition' ->> 'ProcessingLagMs')::int), count(*)
FROM pev2.pev2_execution_journal
WHERE trade_date = '<day>' AND payload_json::jsonb -> 'Disposition' ->> 'ProcessingLagMs' IS NOT NULL;
```

The first is the age of the quote when the paper fill was made; the second is the bar-to-decision lag. Few fills give weak percentiles: report the count.

### 6.4 Live P&L latency

While positions are open (otherwise there is nothing to sample). One read-only `psql` session samples `pev2.pev2_live_marks` every 200 ms; `psql` asks for the password. Needs the owner-run `AddObservability` tables and PostgreSQL 17 or later `psql`.

```powershell
.\tools\Measure-LivePnlLatency.ps1 -Database <ledger database> -User <read-only role> -Seconds 600 -OutputDirectory D:\perf-scratch\out
```

Output: p50 / p95 / max of **quote -> published** and **quote -> readable** over each quote's first appearance, and the raw sample CSV. Exit code 2 (no sample) is not a pass. Re-summarise with `-FromCsv <file>`. Each reading is late by up to one sampling interval.

Not included: the Dashboard's refresh (a 1 s timer, two reads, render), which adds up to about 1 s plus the read and render time to reach the screen; the browser leg is not measured on real data. No pass limit has been agreed for these figures.

The query is covered by an automated test and the summary was checked on a made-up sample file; the `psql` collection step has not been run.

### 6.5 Memory of the running services

```powershell
Get-Process NiftySignal* | Select-Object ProcessName, @{n='WorkingSetMB';e={[int]($_.WorkingSet64/1MB)}}, @{n='PeakMB';e={[int]($_.PeakWorkingSet64/1MB)}}, StartTime
```

Sample at the open, mid-session and after the close, and after an Observer restart.

## 7. Cleanup

```powershell
& "C:\Program Files\PostgreSQL\18\bin\pg_ctl.exe" -D D:\perf-scratch\pg stop
```

Keep `D:\perf-scratch\out`. Delete `D:\perf-scratch\pg` only after confirming it is the scratch cluster created in 3.3. Close the PowerShell window to drop the environment variables. If the source copy was made only for this, remove it by your usual procedure.

## 8. What to send back

The `summary-*.json` and `certification-*.json` files, `depth5.txt`, console output of runs C and 5.1, query results from section 6, the baseline commit SHA, the release tree hash, the dump file hashes, and the machine (CPU, memory, disk type, PostgreSQL version and `shared_buffers`).
