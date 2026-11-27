# Certification run sheet, recorded day 23 November 2026 (owner's PowerShell)

**Revised 26 November, 22:15 IST** after the failed first certify run (`NSE_CERTIFICATION_FAILURE_PUBLICATIONS_2026-11-26.md`). Before certifying again: (1) run the read-only query in that report, because certify drops the `perf_cert_*` databases; (2) rebuild the tool; (3) set the variables of the environment step again. Labels are now `C2-` so the first run's evidence is kept.

Directive: `NSE_AUTONOMOUS_REAL_DATA_CERTIFICATION.md`. Details and pass conditions: `NSE_OWNER_BENCHMARK_PROCEDURE.md`. **Nothing in this sheet has been executed against the recorded database.**

Already prepared on 26 November: `D:\perf-scratch\out`; scratch cluster `D:\perf-scratch\pg` (initialised, verified, then stopped); baseline worktree `D:\perf-scratch\nse-78d9b8b` (clean, `78d9b8bd84d0b9bb38dd91a80ae89f0dad8fd5fe`); both tool builds in Release (0 errors). Candidate identity: source tree without `docs` `aa906ab302ce7e770d03c1c4002aa40e5a15808e` (if any source file changes, rebuild and re-identify).

Run from `D:\Projects\NSE-release-20261124`, one command at a time, nothing else heavy running. Passwords stay in `%APPDATA%\postgresql\pgpass.conf`. Run steps 1-4 first: they hold the mandatory certification.

## 1. Cluster and instrument coverage

```powershell
& "C:\Program Files\PostgreSQL\18\bin\pg_ctl.exe" -D D:\perf-scratch\pg -o "-h 127.0.0.1 -p 54340" -l D:\perf-scratch\pg.log -w start
```

Expected: three values `54340`, `D:/perf-scratch/pg`, `127.0.0.1`.

```powershell
& "C:\Program Files\PostgreSQL\18\bin\psql.exe" -X -h 127.0.0.1 -p 54340 -U postgres -At -c "show port" -c "show data_directory" -c "show listen_addresses"
```

```powershell
$env:PGOPTIONS="-c default_transaction_read_only=on -c statement_timeout=600000"; & "C:\Program Files\PostgreSQL\18\bin\psql.exe" -X -w -h localhost -p 5432 -U research_ro -d niftysignal_vm_copy -f D:\perf-scratch\coverage-20261123.sql -o D:\perf-scratch\out\coverage-20261123.txt; Remove-Item Env:\PGOPTIONS
```

Expected: one future, weekly calls and puts, all with ticks, five-level depth present for each kind, 09:15 to about 15:35 IST. Failure: stop. Note the future token and weekly expiry for step 6.

## 2. Settings (once per window)

```powershell
$env:ADAPTIVE_PERF_SOURCE="Host=localhost;Port=5432;Database=niftysignal_vm_copy;Username=research_ro"; $env:ADAPTIVE_PERF_ADMIN="Host=127.0.0.1;Port=54340;Database=postgres;Username=postgres"; $env:ADAPTIVE_PERF_OUT="D:\perf-scratch\out"; $env:ADAPTIVE_PERF_COMMIT="tree-aa906ab302ce7e770d03c1c4002aa40e5a15808e"
```

## 3. Certify, late restart (mandatory)

```powershell
$env:ADAPTIVE_PERF_LABEL="C2-certify-1430"; $env:ADAPTIVE_PERF_STAGES="certify"; $env:ADAPTIVE_PERF_CERTIFY_DAY="2026-11-23"; $env:ADAPTIVE_PERF_CERTIFY_RESTART_IST="14:30:00"; $env:ADAPTIVE_PERF_CERTIFY_DOWNTIME_SECONDS="60"; dotnet run -c Release --no-build --project tools\AdaptiveLivePerformance 2>&1 | Tee-Object D:\perf-scratch\out\C2-certify-1430.log
```

Expected: `CRITERIA MET on the supplied day`, exit 0. `NOT CERTIFIED` (1) or an error: stop and keep the log; this stage has never run, so the fault may be in the tool.

## 4. Certify, early restart (mandatory)

```powershell
$env:ADAPTIVE_PERF_LABEL="C2-certify-0945"; $env:ADAPTIVE_PERF_CERTIFY_RESTART_IST="09:45:00"; dotnet run -c Release --no-build --project tools\AdaptiveLivePerformance 2>&1 | Tee-Object D:\perf-scratch\out\C2-certify-0945.log
```

## 5. Builds A, B, C (run each twice, with `-2` added to the label the second time)

The day settings differ from the procedure's example: only 23 November gets input statistics, to avoid scanning every session.

```powershell
$env:ADAPTIVE_PERF_DAYS="2026-11-23"; $env:ADAPTIVE_PERF_LIVE_DAYS="2026-11-23"; $env:ADAPTIVE_PERF_COLD_DAYS="2026-11-23"; $env:ADAPTIVE_PERF_RESTART_DAYS="2026-11-23"; $env:ADAPTIVE_PERF_STRESS="1"; $env:ADAPTIVE_PERF_RESTART_MODE="full"
```

```powershell
$env:ADAPTIVE_PERF_LABEL="A-full"; $env:ADAPTIVE_PERF_COMMIT="78d9b8b"; $env:ADAPTIVE_PERF_CHECKPOINTS="0"; $env:ADAPTIVE_PERF_STAGES="inputs,live,recovery,dashboard,plans,stress,digest"; dotnet run -c Release --no-build --project D:\perf-scratch\nse-78d9b8b\tools\AdaptiveLivePerformance 2>&1 | Tee-Object D:\perf-scratch\out\A-full.log
```

```powershell
$env:ADAPTIVE_PERF_LABEL="A-restart"; $env:ADAPTIVE_PERF_STAGES="restart"; dotnet run -c Release --no-build --project D:\perf-scratch\nse-78d9b8b\tools\AdaptiveLivePerformance 2>&1 | Tee-Object D:\perf-scratch\out\A-restart.log
```

```powershell
$env:ADAPTIVE_PERF_LABEL="B-full"; $env:ADAPTIVE_PERF_COMMIT="tree-aa906ab302ce7e770d03c1c4002aa40e5a15808e"; $env:ADAPTIVE_PERF_CHECKPOINTS="0"; $env:ADAPTIVE_PERF_STAGES="inputs,live,recovery,dashboard,plans,stress,digest"; dotnet run -c Release --no-build --project tools\AdaptiveLivePerformance 2>&1 | Tee-Object D:\perf-scratch\out\B-full.log
```

```powershell
$env:ADAPTIVE_PERF_LABEL="B-restart"; $env:ADAPTIVE_PERF_STAGES="restart"; dotnet run -c Release --no-build --project tools\AdaptiveLivePerformance 2>&1 | Tee-Object D:\perf-scratch\out\B-restart.log
```

```powershell
$env:ADAPTIVE_PERF_LABEL="C-full"; $env:ADAPTIVE_PERF_CHECKPOINTS="1"; $env:ADAPTIVE_PERF_STAGES="inputs,live,recovery,dashboard,plans,stress,digest"; dotnet run -c Release --no-build --project tools\AdaptiveLivePerformance 2>&1 | Tee-Object D:\perf-scratch\out\C-full.log
```

```powershell
$env:ADAPTIVE_PERF_LABEL="C-restart"; $env:ADAPTIVE_PERF_STAGES="restart"; dotnet run -c Release --no-build --project tools\AdaptiveLivePerformance 2>&1 | Tee-Object D:\perf-scratch\out\C-restart.log
```

```powershell
Get-FileHash D:\perf-scratch\out\dump-A-restart-20261123.json, D:\perf-scratch\out\dump-B-restart-20261123.json, D:\perf-scratch\out\dump-C-restart-20261123.json
```

If a run reports that priors or opening coverage are missing, see procedure section 2.

## 6. Full-table Level-1 / Top-5 parity

Replace the two placeholders with the values from step 1.

```powershell
$env:NSE_ADAPTIVE_MIGRATION_TEST_DB="Host=127.0.0.1;Port=54340;Database=postgres;Username=postgres"; $env:NS_DEPTH5_CONN="Host=localhost;Port=5432;Database=niftysignal_vm_copy;Username=research_ro"; $env:NS_DEPTH5_DAY="2026-11-23"; $env:NS_DEPTH5_FUT="<future token>"; $env:NS_DEPTH5_WEEKLY="<yyyy-MM-dd>"; $env:NS_DEPTH5_BARVOL="13650"; $env:NS_DEPTH5_OUT="D:\perf-scratch\out\depth5-20261123.txt"
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

Expected: identical `core_hash` on three lines, `mismatches=0`, `core_same_on_restart=True`.

## 7. Stop the cluster

```powershell
& "C:\Program Files\PostgreSQL\18\bin\pg_ctl.exe" -D D:\perf-scratch\pg stop
```

Keep `D:\perf-scratch\out`. Recorded-session execution replay has no run line here: the Host replay test needs its own recorded-session settings and covers the in-Host engine, not the execution service (see the readiness report).
