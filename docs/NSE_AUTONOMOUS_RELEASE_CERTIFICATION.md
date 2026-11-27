NiftySignal — Autonomous release preparation and certification
1. Objective
Our target is to have the new NiftySignal release running in paper-trading mode for the market session on Friday, 27 November 2026.
Complete as much of the remaining implementation, certification and release preparation as possible autonomously.
The production VM is currently powered off. Do not attempt to start it, connect to it or deploy to it.
Continue on:

* Working directory: `D:\Projects\NSE-release-20261124`
* Branch: `feature/production-reliability-strategy-enhancement`
* Original deployed baseline: `78d9b8bd84d0b9bb38dd91a80ae89f0dad8fd5fe`

All previous mandates, approved architectural decisions and trading requirements remain authoritative.
The functional scope is frozen. Do not introduce additional features.
2. Autonomous working instructions
You are authorised to autonomously:

* Inspect the repository and existing documentation.
* Complete the currently running regression and browser tests.
* Diagnose and fix genuine implementation defects.
* Execute local builds and tests.
* Create and use isolated local scratch databases where permitted.
* Read the authorised local recorded market-data database.
* Create isolated benchmark worktrees.
* Execute local real-data benchmarks.
* Validate database migrations against scratch databases.
* Build and package all four Windows services.
* Validate deployment scripts using local or simulated environments.
* Update documentation and prepare owner-run commands.
* Perform local security and dependency checks.

Do not pause for confirmation of routine implementation decisions already covered by the approved mandates.
When a particular operation is blocked, document it and proceed with other independent work.
Do not stop the entire initiative because one validation activity is unavailable.
Do not bypass permission denials, research protections, credential restrictions or security controls. Do not retry a denied operation using a different command, tool or execution mechanism.
3. Actions that require explicit owner approval
Do not:

* Start or access the production VM.
* Modify production databases.
* Modify production services or configurations.
* Commit, push or merge changes.
* Deploy binaries.
* Disable existing production protection.
* Place real-money orders.
* Access unauthorised market-data datasets.
* Change approved trading parameters or strategy semantics.

Prepare these actions for the owner wherever possible, but do not execute them.
4. Finish the current engineering validation
Complete the currently running full Release regression and the final Playwright browser harness.
Verify the recently implemented:

* Persistence-level writer fencing.
* Ownership-transfer concurrency protection.
* Configurable checkpoint retention.
* Paged execution journal recovery.
* Top-5 checkpoint certification stage.
* Migration script completeness.
* Four-service packaging.
* Deployment safety checks.

Record exact test counts and failures.
If tests fail, investigate and correct the underlying defect.
Do not weaken assertions or remove meaningful test coverage to obtain a passing result.
Avoid repeatedly running the entire test suite when focused regression tests are sufficient.
Run a final integrated regression after all code corrections are complete.
5. Inspect local benchmark prerequisites
Determine whether the existing local recorded-data environment can support the required benchmarks.
The expected local source database is:
`niftysignal_vm_copy`
The expected PostgreSQL installation is PostgreSQL 18.
Verify rather than assume:

* Actual database availability.
* Authorised read-only role availability.
* Approved dataset boundaries.
* Recorded session dates.
* Availability of futures and options Top-5 depth.
* Available disk space.
* Available memory.
* Scratch PostgreSQL port availability.
* Required benchmark tooling.

Use existing approved credential mechanisms.
Do not print passwords, tokens or connection-string secrets.
Do not access the research-protected working copy at `D:\Projects\NSE`.
If a benchmark prerequisite is unavailable or access is denied, record the exact blocker and prepare the owner action required to resolve it.
Continue other independent work.
6. Execute real-data certification autonomously
Where authorised local access is available, execute the corrected owner benchmark procedure yourself.
Compare three distinct implementations or operating modes:
Benchmark A — Original deployed baseline
Use the actual original source revision:
`78d9b8bd84d0b9bb38dd91a80ae89f0dad8fd5fe`
Build it in an isolated worktree without disturbing the current implementation.
Benchmark B — New implementation, checkpoints disabled
Use the current release candidate with checkpointing disabled.
Benchmark C — New implementation, checkpoints enabled
Use the current release candidate with checkpointing enabled.
Record the actual tested source identity.
Since the feature branch remains uncommitted, do not use HEAD alone to identify the modified implementation. Record an appropriate reproducible source manifest or working-tree fingerprint.
Use equivalent datasets and compatible configurations across comparisons.
Prioritise:

1. A representative recorded day containing valid futures and options Top-5 data.
2. A high-volume recorded day.
3. A typical recorded day.
4. The 25 November incident session, if available and authorised.

Avoid an unnecessarily broad sweep of all historical sessions when representative certification is sufficient.
Do not silently reduce the workload, remove Top-5 processing or weaken attestation to obtain a faster result.
7. Mandatory certification measurements
Measure and report:

* Full-session recovery duration.
* Checkpoint-assisted recovery duration.
* Checkpoint save and restore duration.
* Actual checkpoint usage versus full-replay fallback.
* Level-1 metric parity.
* Futures Top-5 metric parity.
* Options Top-5 metric parity.
* Persisted bar and sidecar parity.
* Attestation parity.
* Durable publication parity.
* Source cursor continuity.
* Missing or duplicate source events.
* Peak managed memory.
* Peak process working set.
* Database query duration.
* Live catch-up backlog.
* Execution journal recovery.
* Strategy state recovery.

The recovery certification target remains under 300 seconds.
A successful synthetic replay alone does not satisfy real-data certification.
If a required measurement fails, investigate whether the cause is an implementation defect, benchmark defect or environment limitation.
Correct genuine defects within the existing scope.
Do not change the approved recovery target simply to obtain a passing result.
8. Execution and strategy certification
Verify that the intended deployment enables exactly:

* `TOTAL_ACTIVITY_5_10`
* `TOTAL_ACTIVITY_5_10_AVG5`
* `TOTAL_VOTE_5_OF_6`
* `TOTAL_VOTE_5_OF_6_AVG5`

Preserve the approved per-strategy configurations.
Verify:

* Immediate versus Avg5 entry differences.
* Independent position ownership.
* Configurable maximum trades.
* Configurable quantities and capital.
* 09:20 IST and minimum 20 completed bars.
* Rule 8.
* Persistent Avg5 pending-entry behaviour.
* Reference contract identity.
* Fresh executable quotes.
* Configurable DTE/VIX matrix.
* Frozen partial-profit targets.
* Partial exit quantity and accounting.
* Opposite-signal final exits.
* Independent protective exits.
* No new entries from 15:00 IST.
* Mandatory liquidation by 15:10 IST.
* Correct restart reconciliation.
* No duplicate or excessive execution.
* No look-ahead or backdated fills.

Use recorded-session execution replay where existing tools and authorised data permit.
Do not optimise strategy thresholds or attempt to improve profitability.
The objective is engineering correctness and paper-trading readiness, not establishing a profitable trading edge tonight.
9. Prepare the database deployment package
Identify every migration and permission required by the new release.
Include:

* Observer recovery progress.
* Durable bar publications.
* Observer checkpoints.
* Execution service schema.
* Ownership and fencing changes.
* Any additional migrations introduced during the final engineering pass.

Verify the idempotent SQL scripts against freshly generated EF migrations.
Test schema upgrades against a suitable scratch database where permitted.
Verify:

* Existing database schema upgrades correctly.
* Historical records remain readable.
* Required indexes exist.
* Migration execution is idempotent.
* Old binaries can ignore additive schema changes where rollback compatibility is claimed.
* New services have the required database permissions.
* Dashboard read-only roles retain appropriate access.

Prepare exact owner-run SQL scripts and permission-verification queries.
Do not assume database role names or passwords.
Clearly distinguish migrations that apply automatically at startup from migrations that require owner execution.
10. Prepare all four service packages
Prepare and validate Release publish packages for:

1. `NiftySignalHost`
2. `NiftySignalObserver`
3. `NiftySignalExecution`
4. `NiftySignalDashboard`

Verify each package contains:

* Required binaries.
* Required dependencies.
* Correct service entry point.
* Required configuration defaults.
* Required strategy files.
* Required static assets.
* Build identity.
* No unintended secrets.
* No machine-specific local configuration.

Verify the required .NET and ASP.NET Core runtime versions.
Record package size and SHA256 hashes.
Do not include `appsettings.Local.json`, credentials, logs, scratch databases, benchmark data or unnecessary Playwright installation duplicates in deployment packages.
Preserve any browser runtime genuinely required by production screenshot generation and document how it is provisioned.
These are locally validated packages, not authorised production deployment artifacts.
Prepare the final release build procedure so packages can be regenerated from the reviewed committed revision once the owner approves it.
11. Prepare VM activation configuration
The VM is powered off. Prepare configuration templates and exact instructions without attempting to connect to it.
Expected services:

* `NiftySignalHost`
* `NiftySignalObserver`
* `NiftySignalExecution`
* `NiftySignalDashboard`

Expected installation directories must be verified against the runbook.
Prepare a configuration checklist for:
Host

* Broker connectivity remains unchanged.
* Tick ingestion remains enabled.
* Separate Observer mode.
* In-Host PaperEngineV2 disabled.
* NPT1 paper-live disabled.

Observer

* Required database connections.
* Fixed 13,650-unit futures volume bars.
* Futures Top-5 enabled.
* Options Top-5 enabled.
* Existing metric definitions preserved.
* Checkpointing enabled.
* Configurable checkpoint interval.
* Configurable checkpoint retention.
* Recovery timeout and retry settings.
* Dashboard publication settings.

Execution

* Independent execution service enabled at authorised activation.
* Paper-only mode.
* Four approved strategies.
* Per-strategy configuration preserved.
* Zero brokerage where configured.
* Observer health gate.
* Independent protective exits.
* Mandatory liquidation.
* Execution alerts.

Dashboard

* ExecutionHealth:Required enabled at activation.
* Four market-summary grids.
* Option Price Averages.
* Exactly two paper-trading grids.
* Telegram screenshot settings.
* Operational health alerts.

Do not fabricate actual VM connection strings or credentials.
Prepare a safe owner-run approach that preserves existing local secrets without copying unnecessary broker credentials into other services.
Verify effective configuration, including relevant environment overrides, wherever possible through deployment tooling.
12. Deployment and rollback rehearsal
Execute all feasible local deployment safety and packaging checks.
Validate the planned sequence:

1. Verify release identity.
2. Verify VM runtime prerequisites.
3. Verify disk and memory capacity.
4. Verify database permissions.
5. Verify no unresolved paper positions.
6. Create and verify database backups.
7. Create and verify service backups.
8. Apply required owner-run migrations.
9. Register the new services.
10. Disable legacy in-Host execution.
11. Deploy the approved binaries.
12. Start and verify all four services in dependency order.
13. Verify ownership.
14. Verify Dashboard and alert configuration.
15. Verify four-strategy paper activation.

Ensure that a partial deployment failure has an explicit recovery or rollback procedure.
Never allow legacy and independent execution writers to run simultaneously.
Never allow two Observers to write concurrently.
Preserve the deployment time guard and required rollback margin.
Do not bypass the market-hours restriction to meet the target date.
13. Prepare owner-run command sequences
Create a concise deployment checklist specifically for the owner.
Each step must state:

* Whether the command runs on the desktop or VM.
* Required working directory.
* Exact command.
* Expected result.
* Failure handling.
* Whether it changes state.
* Whether it requires explicit approval.

Provide PowerShell commands individually, not as a large compound script that requires copying everything at once.
Prepare the necessary commands for:

* Runtime verification.
* Installed release identity.
* Disk and memory checks.
* Windows service accounts.
* Database backup.
* Database migration.
* Database permissions.
* Service installation.
* Local configuration.
* Deployment preflight.
* Actual deployment.
* Post-deployment verification.
* Rollback.

Do not execute the production commands.
14. Handle external blockers efficiently
If a task requires owner input, do not immediately stop all work.
Maintain a blocker register containing:

* Blocker ID.
* Affected requirement.
* Exact missing input or permission.
* Why it is needed.
* Exact command or owner action.
* Whether it blocks local certification or only production deployment.
* Whether independent tasks can continue.

Complete all other available tasks before requesting assistance.
Do not ask the owner to perform tasks that you can safely and legitimately complete yourself.
Consolidate owner-dependent actions into a single ordered list at the end.
15. Final release readiness report
Update the existing documents:

* NSE_PRODUCTION_RELIABILITY_AND_STRATEGY_PLAN.md
* NSE_OWNER_BENCHMARK_PROCEDURE.md
* NSE_EXECUTION_SERVICE_RUNBOOK.md

Create:
`docs/NSE_RELEASE_CANDIDATE_READINESS_2026-11-27.md`
Include:

1. Exact implementation status.
2. Final Release build results.
3. Full regression test results.
4. Browser and Telegram emulator results.
5. Writer-fencing validation.
6. Checkpoint retention validation.
7. Journal recovery measurements.
8. Real-data benchmark A/B/C results.
9. Level-1 and Top-5 parity results.
10. Recovery certification result.
11. Migration verification.
12. Security dependency status.
13. Package manifest.
14. Deployment prerequisites.
15. Exact owner-dependent actions.
16. Remaining risks.
17. Rollback readiness.
18. Paper-release recommendation.
19. Real-money recommendation.

Clearly classify every requirement as:

* PASS
* FAIL
* BLOCKED
* NOT RUN
* REQUIRES OWNER

Do not describe an unexecuted validation as successful.
16. Stop condition
Continue autonomously until:

* All feasible local work is complete; or
* Remaining activities genuinely require owner input, approval or access.

Do not initiate another general architecture or feature-development cycle.
Do not repeatedly request confirmation for approved local work.
If everything locally verifiable passes, prepare the release candidate for owner review, commit approval and VM activation.
Do not commit, push, merge, deploy or start the production VM.
The target is tomorrow's paper-trading session, but correctness takes precedence over the deadline.
Real-money execution remains NO-GO.
Begin by completing the regression and browser checks already in progress. Then proceed through this directive without waiting for further instructions.
