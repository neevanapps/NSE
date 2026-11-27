NiftySignal — Autonomous real-data certification
Proceed with the complete local real-data certification using the existing release candidate.
Our target is to deploy the new release for the Friday, 27 November 2026 paper-trading session, provided all mandatory certification requirements pass.
Do not wait for repeated owner instructions. Execute the certification steps yourself, in dependency order.
1. Verified environment
The owner has independently verified the following:
Repository

* Working directory: `D:\Projects\NSE-release-20261124`
* Branch: `feature/production-reliability-strategy-enhancement`
* Original baseline: `78d9b8bd84d0b9bb38dd91a80ae89f0dad8fd5fe`
* Latest regression: 3264 passed, 0 failed, 2 skipped.

Desktop

* C drive free space: 340.49 GB.
* D drive free space: 167.45 GB.
* Total RAM: 31.78 GB.
* Available RAM: 15.87 GB.
* PostgreSQL 18 service: running.
* Scratch port 54340: available.
* `D:\perf-scratch\pg` does not exist.
* PostgreSQL `initdb.exe` and `pg_ctl.exe` are available under `C:\Program Files\PostgreSQL\18\bin\`.

Source database

* Host: localhost.
* Port: 5432.
* Database: `niftysignal_vm_copy`.
* Read-only role: `research_ro`.
* Role authentication successfully verified by the owner using psql.
* Role is not a superuser.
* SELECT permissions on ticks and instruments: verified.
* Tick write permissions: absent.
* Database size: 87 GB.
* Estimated tick count: 290,626,048.
* Estimated storage per tick: 322.77 bytes.
* Recorded coverage: 4 September through 23 November 2026.

Initial certification date: 23 November 2026

* Total ticks: 3,965,994.
* First tick: 09:15:00 IST.
* Last tick: 15:35:35 IST.
* Ticks with positive fifth-level bid and ask prices and quantities: 3,390,839.
* Overall fifth-level coverage: 85.50%.
* Estimated scratch tick storage: 3.58 GiB, excluding additional databases and WAL.
* All 20 Level-1 through Level-5 depth columns exist.

The 25 November incident session is not available in this database. Do not attempt to retrieve it from the VM.
The production VM is powered off and must remain untouched.
2. Authorisation
I explicitly authorise the following local operations for this certification:

* Read-only SQL queries against `niftysignal_vm_copy` using `research_ro`.
* Local disk, memory and process inspection.
* Creating `D:\perf-scratch` and its benchmark output directories.
* Initialising an isolated PostgreSQL scratch cluster.
* Starting and stopping that scratch cluster on port 54340.
* Creating and dropping benchmark-owned `perf_*` databases exclusively within that scratch cluster.
* Creating an isolated Git worktree for the original baseline.
* Building and executing the benchmark tools.
* Running recorded-session replay and certification tests.
* Writing benchmark reports and updating documentation.

This owner authorisation does not override tool-level permission controls.
If an operation requires interactive permission approval, request it through the normal permission mechanism.
If denied, do not bypass or retry it through an alternative execution mechanism. Record the blocker and continue independent tasks where possible.
Never request or print database passwords in the conversation.
3. Authentication
Check whether `%APPDATA%\postgresql\pgpass.conf` already permits non-interactive authentication for:
`localhost:5432:niftysignal_vm_copy:research_ro`
Verify authentication with psql without prompting for a password.
Do not display the password file or its contents.
If authentication fails because the password is unavailable, ask the owner to configure the password file locally.
Do not change the research_ro password or database permissions.
4. Verify instrument-specific Top-5 coverage
Use the actual `instruments` schema and existing instrument-type definitions from the repository.
Available instrument columns include:

* Token
* Exchange
* TradingSymbol
* InstrumentType
* OptionType
* StrikePrice
* ExpiryDate
* Underlying
* LotSize
* TickSize
* AsOfDate
* Subscribed

Verify that 23 November contains representative data for:

* The selected NIFTY futures contract.
* NIFTY weekly CE contracts.
* NIFTY weekly PE contracts.
* Futures Top-5 depth.
* Options Top-5 depth.
* Sufficient session coverage.

Use the indexed ReceivedAt range and Token/ExchangeTimestamp index where appropriate.
Do not repeatedly scan the entire 87 GB table unnecessarily.
Identify the busiest and a typical eligible recorded session for subsequent certification.
Respect existing research and dataset access restrictions.
5. Initialise isolated scratch PostgreSQL
Create:

* `D:\perf-scratch`
* `D:\perf-scratch\out`

Initialise the scratch cluster:

```
& "C:\Program Files\PostgreSQL\18\bin\initdb.exe" -D D:\perf-scratch\pg -U postgres -A trust -E UTF8
```

Start it with explicit loopback binding:

```
& "C:\Program Files\PostgreSQL\18\bin\pg_ctl.exe" -D D:\perf-scratch\pg -o "-h 127.0.0.1 -p 54340" -l D:\perf-scratch\pg.log start
```

Before any benchmark database creation, verify:

* Server port is 54340.
* Data directory is `D:\perf-scratch\pg`.
* Listen address is 127.0.0.1.
* The original PostgreSQL server on port 5432 is unaffected.

All benchmark writes, database creation and deletion must target the isolated scratch cluster.
The recorded source database must remain read-only.
6. Freeze benchmark source identity
Follow the latest `docs/NSE_OWNER_BENCHMARK_PROCEDURE.md`.
Record a reproducible source identity for the uncommitted release candidate using the documented temporary Git index mechanism.
Do not modify the normal staging area.
Verify against the previously reported release-candidate source identity, accounting for the documented difference between hashes that include or exclude documentation.
Create the original baseline in an isolated detached worktree under `D:\perf-scratch`.
Do not access or modify `D:\Projects\NSE`.
Build the original baseline and current release candidate separately.
Do not commit or merge.
7. Prioritise Top-5 checkpoint certification
Execute section 5.1 of the latest owner benchmark procedure first.
Use:

* Certification date: 2026-11-23.
* Restart time: 14:30 IST.
* Simulated downtime: 60 seconds.
* Futures Top-5 enabled.
* Options Top-5 enabled.

Use the documented `certify` stage of `tools\AdaptiveLivePerformance`.
Verify:

* Full-session recovery under 300 seconds.
* Actual checkpoint restoration.
* Checkpoint-assisted recovery under 300 seconds.
* No full-replay fallback masquerading as checkpoint recovery.
* Source cursor continuity.
* No missing or duplicated ticks.
* Partial volume-bar parity.
* Completed-bar parity.
* Level-1 metric parity.
* Futures Top-5 parity.
* Options Top-5 parity.
* Sidecar parity.
* Attestation parity.
* Durable publication parity.
* Peak managed memory.
* Peak working set.
* Checkpoint size, save duration and restore duration.

The certification tool must report `CRITERIA MET`.
Then repeat for the early-session restart at 09:45 IST.
Use distinct output labels.
The certification stage has never been executed on real data, so distinguish genuine production defects from certification-tool defects.
If the tool fails, investigate the cause. Correct a genuine benchmark defect if necessary, but never weaken certification criteria or production attestation.
8. Complete remaining real-data benchmarks
Execute the remaining mandatory sections of the benchmark procedure.
Compare:
A. Original deployed revision 78d9b8b.
B. Current implementation with checkpoints disabled.
C. Current implementation with checkpoints enabled.
Include:

* Full recovery.
* Cold recovery.
* Warm recovery with verification.
* Restart parity.
* Live catch-up.
* Stress testing.
* Database query timings.
* Memory consumption.
* Full-table Level-1 and Top-5 parity.

Run the full-table OptionsDepth5HostBenchmark modes:

* off
* futures
* both

Resolve the correct futures token and weekly expiry from the source data.
Use the fixed 13,650-unit futures volume bar configuration.
Verify the resulting hashes and mismatch counts.
Complete recorded-session execution replay where the available tooling and authorised data support it.
Use representative sessions and equivalent configurations.
Do not unnecessarily run every historical session when representative certification is sufficient. Retain the documented repeat-run requirements.
9. Failure handling and efficiency
The deployment deadline is important, but do not bypass correctness requirements.
Prioritise the most consequential release gates.
Avoid:

* Redundant full-table scans.
* Repeated full solution builds without code changes.
* Repeated complete regression suites without a relevant change.
* Running competing heavy benchmarks simultaneously.
* Speculative architecture enhancements.
* Unrelated strategy changes.

If any benchmark fails:

1. Preserve the evidence and logs.
2. Identify the exact failing criterion.
3. Determine whether the failure is a production defect, fixture defect, benchmark defect or environment limitation.
4. Fix only genuine in-scope defects.
5. Run focused tests.
6. Repeat affected certification steps.

If a code correction changes the release-candidate identity, record the new identity and rerun affected validation.
Do not silently reduce the dataset or change acceptance thresholds.
10. Final certification report
Create a consolidated report containing:

* Actual machine and database environment.
* Selected session dates.
* Futures and options depth coverage.
* Exact tested source identities.
* Benchmark A/B/C results.
* Cold and warm recovery durations.
* Early and late checkpoint recovery durations.
* L1 and Top-5 parity.
* Attestation and publication parity.
* Tick accounting.
* Query timings.
* Peak memory.
* Checkpoint save and restoration performance.
* Recorded-session strategy replay results.
* All failures, blockers and limitations.
* Exact output paths.
* Total elapsed certification time.
* Paper-release GO/NO-GO recommendation.

Update:

* `docs/NSE_RELEASE_CANDIDATE_READINESS_2026-11-27.md`
* `docs/NSE_OWNER_BENCHMARK_PROCEDURE.md`
* `docs/NSE_PRODUCTION_RELIABILITY_AND_STRATEGY_PLAN.md`

Preserve all benchmark JSON outputs and logs.
Stop the scratch cluster when certification is complete or no further authorised work remains. Do not delete benchmark evidence.
Distinguish local certification from VM-specific validation. Live shadow latency and actual Windows service operation cannot be certified while the VM is off.
11. Stop conditions
Continue autonomously through every feasible local certification step.
Stop for owner input only when:

* A required permission cannot be obtained.
* Database authentication needs owner intervention.
* Authorised data is unavailable.
* A genuine correctness failure requires an owner decision.
* The certification is complete and release approval is required.

Do not stop merely to provide another intermediate checklist.
Do not start the VM, commit, push, merge, deploy, modify production configurations or place real-money orders.
Real-money trading remains NO-GO.
Begin with authentication verification and instrument-specific Top-5 coverage, then initialise the scratch cluster and execute the certification.
