# Release candidate readiness, 27 November 2026

Prepared 26 November 2026 (evening IST) on the developer machine. Directive: `NSE_AUTONOMOUS_RELEASE_CERTIFICATION.md`. Classes: **PASS**, **FAIL**, **BLOCKED** (an access or permission is missing), **NOT RUN**, **REQUIRES OWNER**.

## Recommendation

| Question | Answer |
|---|---|
| Paper release of the new version for Friday 27 November | **NOT READY.** Local engineering checks pass; nothing has been shown on real recorded data, on the VM or against real service control |
| What would change it | Owner actions O1-O5 in section 15 pass before **07:30 IST** Friday (deployment must start before the 08:30 guard). Their duration is unmeasured |
| If they cannot be completed in time | Keep the deployed release for Friday and deploy after certification (after 15:45 IST or at the weekend). Deploying with execution disabled is not a safe shortcut: it still replaces the Host and moves the Observer to a new service whose real-data recovery is uncertified. The deployed release also still carries the unresolved 25 November cause |
| Real money | **NO-GO** |

**Update, later on 26 November** (`NSE_REAL_DATA_CERTIFICATION_REPORT_2026-11-26.md`): the owner authorised real-data certification on 23 November and reported the machine and database figures (clearing the capacity part of B3 on their evidence). The read-only login was refused again by the tool permission control (B2 stands), so nothing was certified: **NO-GO**. B1 is untested under the new authorisation. The scratch cluster, baseline worktree and builds are prepared; owner actions O1-O4 are replaced by `NSE_CERTIFICATION_RUNSHEET_2026-11-23.md`. 25 November is not in the recorded copy.

**Update, 26 November, 22:00 IST** (`NSE_CERTIFICATION_FAILURE_PUBLICATIONS_2026-11-26.md`): the owner ran the 14:30 certification on 23 November: `NOT CERTIFIED` (206 publications for 228 bars). Cause: recovered bars were published only by the service loop, which the tool bypasses; investigating it found a real production defect (a failed publication of recovered bars let later bars overtake them, so the execution service could miss an exit signal). Corrected with three regression tests. **The source identity below is superseded**, as are the packages; section 2-4 results belong to the old tree. New tree `aa906ab302ce7e770d03c1c4002aa40e5a15808e`: full suite 3,267 passed, 0 failed, 2 skipped. Both certify runs must pass on it: **NOT READY**.

## 1. Implementation status

| Item | Status |
|---|---|
| Branch / base | `feature/production-reliability-strategy-enhancement` on `78d9b8bd84d0b9bb38dd91a80ae89f0dad8fd5fe`; **all work uncommitted** |
| Source identity tested | working-tree hash without `docs`: `25e7f9643f206881b7fc84145e26c198f555c9a7` (git tree object, includes untracked files, excludes ignored files). Recompute: checklist A1 |
| Scope | frozen. One correction in this pass: first-deployment backup abort in `vm-deploy.ps1` (section 17). No strategy, parameter, schema or .NET source change |
| Recorded incident of 25 November | cause unresolved |

## 2-4. Build, tests, browser (final code)

| Check | Class | Result |
|---|---|---|
| Release build | PASS | 0 errors, 12 warnings (unused code and xUnit analyzer types) |
| Full Release test suite | PASS | 3,264 passed, 0 failed, 2 skipped, 3,266 total. Ran before the PowerShell-only correction; no .NET source changed since, so it was not repeated |
| Skipped: `HostReplayTests` recorded-session replay | BLOCKED | needs recorded data (B2) |
| Skipped: journal recovery benchmark | NOT RUN in the suite | run separately earlier (section 7) |
| Browser harness (capture, paper grids, health, recovery banners) | PASS | exit 0, none reported NOT RUN; synthetic data, fresh loopback scratch cluster |
| Telegram | PASS (emulator) / REQUIRES OWNER (real delivery) | no real message was sent |
| CI run of the harness | NOT RUN | needs a push (B6) |
| `git diff --check` | PASS | line-ending notices only |

## 5-7. Fencing, retention, journal recovery (synthetic, scratch PostgreSQL)

| Requirement | Class | Evidence |
|---|---|---|
| Writer fencing at the persistence boundary; ownership moved between evaluation and persistence (service and in-Host engine) | PASS | concurrency tests in the full run. Limits: older binaries take no lock or epoch; alert delivery-status updates are unfenced |
| Configurable checkpoint retention (3), fallback, damaged / incompatible rows, concurrent removal | PASS | checkpoint tests in the full run |
| Paged journal recovery, identical state at page sizes 1, 2, 7, 5000 | PASS | parity tests |
| Journal recovery measurements | PASS (synthetic only) | 600 bars: 1.62 s / 125.2 MiB peak before, 1.03 s / 27.5 MiB after. 3,000 bars: 3.51 s / 647.0 MiB before, 2.35 s / 27.2 MiB after |
| In-Host fallback journal still read whole | open | F81 |
| Top-5 checkpoint certification stage | NOT RUN | built, never executed (B1, B2) |

## 8-10. Real-data certification

Prerequisites (directive section 5):

| Prerequisite | Class |
|---|---|
| `niftysignal_vm_copy` availability, `research_ro` access, session dates, futures and options Top-5 presence, dataset boundaries | BLOCKED (B2) |
| Free disk, memory, PostgreSQL services, scratch ports, environment settings | BLOCKED (B3) |
| Scratch performance databases | BLOCKED (B1) |
| Benchmark tooling | PASS: builds in the Release solution; procedure written |

| Measurement (directive section 7) | Class |
|---|---|
| Benchmarks A, B, C | BLOCKED (B1, B2) |
| Full and checkpoint-assisted recovery duration; save / restore duration; checkpoint use versus fallback | BLOCKED |
| Level-1, futures Top-5, options Top-5, bar / sidecar, attestation, publication parity | BLOCKED on real data. Synthetic parity tests pass; they do not certify |
| Source cursor continuity, missing / duplicate events, memory, working set, query duration, live catch-up backlog | BLOCKED |
| Execution journal and strategy state recovery on real data | BLOCKED |
| **Recovery under 300 s** | **NOT CERTIFIED** |

## Execution and strategy certification (directive section 8)

| Requirement | Class | Evidence (tests in the full run unless stated) |
|---|---|---|
| Exactly four enabled, 4-of-6 disabled | PASS | `ExactlyFourStrategiesAreEnabled...`; the five files inside the execution package |
| Per-strategy configuration: 2 lots, capital 50,000, 5 trades, daily loss 1,500, premium 100-150 (Rule 8), 09:20, 20 bars, entry before 15:00, 50 % partial, DTE / VIX targets | PASS | files read in this pass; `EveryEnabledStrategy_KeepsTheSignalDefinition...` |
| Immediate and Avg5 differ only in identity and timing | PASS | `ImmediateAndAvg5Variants_DifferOnly...` |
| Independent ownership, configurable limits | PASS | `ThirtyStrategies_AreIsolated...`, `MaxConcurrentAndDailyTrades...`, `LotSizeAndCapital...` |
| Persistent Avg5 intent, reference contract, fresh executable quotes | PASS | `ExecutionIntentLifecycleTests`, `Avg5_*`, `StaleOrMissingQuote...` |
| Frozen target, partial quantity and accounting | PASS | `PartialTarget_*`, `TheFrozenPartialTarget_Survives...` |
| Opposite-signal and protective exits | PASS | `OppositeSignal_*`, `ProtectiveFlatten_*`, `StaleObserver_Flattens...` |
| No entry from 15:00; liquidation from 15:08, deadline 15:10 | PASS | `PendingEntry_IsCancelledAtTheEntryCutoff...`, `NoTimeExitAt1500_LiquidationAt1508...` |
| Restart reconciliation, no duplicates, no look-ahead or backdated fill | PASS | paged recovery, `Restart_*`, `SameFrame_NeverFills...`, `LiveRunAndJournalReplay...` |
| Recorded-session execution replay | BLOCKED (B2) | |
| Shadow run on live data (latency, staleness rejections) | REQUIRES OWNER | needs an approved deployment |

## 11. Migrations and database package

| Requirement | Class | Note |
|---|---|---|
| Migrations identified | PASS | observer: `AddObserverRecoveryProgress`, `AddBarPublications`, `AddObserverCheckpoints` (automatic at Observer start). Ledger: `AddExecutionService`, which carries the fencing state table (**owner-run**). None new in the final pass; no Host database migration |
| Scripts equal to fresh EF output | PASS | four scripts |
| Additive only; ledger schema version stays 2 | PASS | new tables, new columns that are nullable or defaulted |
| Observer upgrade from the older schema keeps history readable; new indexes | PASS (scratch) | `HistoricalDatabase_UpgradedToLatest...`, `EmptyDatabase_UpgradedToLatest...` |
| Ledger script runs twice and matches the migration | PASS (scratch) | `OwnerScript_CreatesTheSchema_CanRunTwice...` |
| Three new observer scripts applied twice | NOT RUN | text equals EF's guarded output; the Observer applies them through EF |
| Upgrade of a copy of the production databases | BLOCKED (B2, B4) | |
| Old binaries ignore the new schema | NOT RUN | by inspection (additive; EF ignores unknown applied migrations); old binaries were not run against it |
| Service and Dashboard permissions | REQUIRES OWNER | role names unknown; grants and verification queries in checklist B7, D1 |

## 12. Security and dependencies

PASS: no vulnerable package in 45 projects (transitive); no vulnerability warning in the publish logs. Packages hold no `appsettings.Local.json`, logs, dumps or key files, and no non-empty secret-like setting or machine path in their settings files (name-pattern scan, not a full secret audit). `appsettings.Development.json` ships in the Host and Dashboard packages as before.

## 13. Package manifest (local validation only, not deployment artifacts)

`artifacts\release-candidate-20261127\` (ignored by git), built 2026-11-26T15:03:16Z from the tree above, identity `uncommitted-candidate`.

| Service | Files | Bytes | Zip bytes | Zip SHA-256 | Main DLL SHA-256 |
|---|---|---|---|---|---|
| NiftySignalHost | 125 | 21,404,290 | 7,149,140 | `cd0764c72b5253a0cc53286c28db8aa8353894493c246b547e7c10ea9ca2b7b0` | `69d380057a8e683a708897252ddddf24ea2aba494bac0dde68d3d441526d0c17` |
| NiftySignalObserver | 73 | 13,247,577 | 4,376,269 | `9ddcf10726300c65c0e64e66c248aae130bbe9fc6b9211f9e2e474a8c7ff4928` | `69f0d6c3ea10299e667fde49eb12a72a9e4d0334e73c7e8a85a806148e9e6e12` |
| NiftySignalExecution | 87 | 14,171,545 | 4,731,057 | `89321e4d14a53a67fbb3ba1226cfaeb373a64b6474115a6f89d0eae2296b5e30` | `794433e57fec446fe6edf182f9ffb82e33a8e2a155dce4bd5b3ba69be4b0a051` |
| NiftySignalDashboard | 429 | 116,459,525 | 41,912,458 | `8278a900e4a263df2c9fbd067cc420b97efdd93669eb94e62f64ee7b42a1f8ae` | `c435daa234d91a5530ef4a1a6d167d6a5d9cacdac93d7ae9885d7e242a6cc63b` |

Verified per package (PASS): executable, main DLL, deps and runtimeconfig files, `appsettings.json`, build identity in the main DLL; execution package holds the five strategy files; Dashboard holds `wwwroot` and the Playwright driver (win x64). Runtimes: .NET 10 for all, plus ASP.NET Core 10 for the Dashboard (framework-dependent). Chromium is not packaged: the deployment installs it into `.playwright-browsers` on the VM (needs internet). Starting each package as a Windows service: NOT RUN.

## 14. Deployment prerequisites and rehearsal

| Requirement | Class |
|---|---|
| Local deployment safety (47) and delta packaging tests | PASS |
| Sequence: identity, runtimes, capacity, permissions, open positions, backups, migrations, registration, legacy engine off, deploy, start order, ownership, Dashboard / alerts, four strategies | prepared in the checklist; REQUIRES OWNER; not rehearsed on real services |
| Two writers / two Observers prevented | PASS locally (ownership checks, locks, fencing tests); older binaries are not prevented by the database |
| Market-hours guard and rollback margin | PASS (tests); unchanged |
| VM configuration templates | prepared (checklist D3). Note: Top-5 ships **off** and must be enabled in both Host and Observer |
| VM state, installed revision, accounts, disk, memory | REQUIRES OWNER (VM off) |

## 15. Blocker register and owner actions

| ID | Affected | Missing | Why needed | Blocks |
|---|---|---|---|---|
| B1 | A/B/C, certify | creating a scratch performance database was denied | every benchmark stage creates databases | certification |
| B2 | data prerequisites, all real-data parity and replay | the read-only query on `niftysignal_vm_copy` as `research_ro` was denied | representative recorded data | certification |
| B3 | capacity prerequisites | the local probe (disk, memory, services, ports, environment names) was denied | sizing the benchmark | certification |
| B4 | deployment, upgrade rehearsal | VM off; access not authorised | production state | deployment |
| B5 | deployment | commit and merge approval | scripts deploy only a clean `master` | deployment |
| B6 | CI browser run | push approval | CI evidence | neither (gate 3) |
| B7 | alert delivery | real Telegram test is the owner's decision | delivery proof | deployment verification |
| B8 | permissions | database role names | grants | deployment |
| B9 | 25 November cause | production evidence collection | incident | neither; risk remains |

None of the denied operations was retried in another form. Independent work continued in every case.

Ordered owner actions:

| # | Action | Where / command |
|---|---|---|
| O1 | Check capacity: `Get-PSDrive`, RAM, PostgreSQL 18 service, free scratch port (clears B3) | desktop |
| O2 | Confirm read access and Top-5 dates on `niftysignal_vm_copy` | procedure section 2 |
| O3 | Run builds A, B, C | procedure sections 3-4 |
| O4 | Run certify (`CRITERIA MET`, recovery under 300 s) and Top-5 / L1 parity | procedure 5.1, 5.2 |
| O5 | Review the diff; approve commit and merge | checklist A1, A3 |
| O6 | Start the VM; checks and backups | checklist B, C |
| O7 | Ledger script, grants, service registration, settings | checklist D1-D3 |
| O8 | Preflight, deploy with execution disabled | checklist D4 |
| O9 | Activate, verify ownership, four strategies, Dashboard | checklist E |
| O10 | Decide on the real Telegram test; push for CI | owner |

If you want a developer session to run O1-O4, you have to grant those operations yourself; they stay blocked otherwise.

## 16. Remaining risks

- Plan section 7 applies in full: unresolved 25 November cause; real-data recovery time and execution latency unmeasured; certify tool never executed; first restart after a deployment is a full replay; same-VM failures leave positions unprotected; no cross-session checkpoint cleanup (payload size on real data unknown); F81.
- First start of three new or changed processes on a live day, with no shadow session behind them.
- `vm-deploy.ps1` has never run against four services; the first-install correction is covered only by a unit test of its decision.

## 17. Rollback readiness

| Item | Class |
|---|---|
| Procedure (stop and disable new services first; restore verified binary backups; additive schema stays; no second writer) | prepared |
| Verified backup logic for Host and Dashboard | PASS (local tests) |
| First installation of Observer and execution: no backup exists; rollback is stopped and disabled | by design |
| Rehearsal on real services; database restore from dump | NOT RUN |

Correction in this pass: `vm-deploy.ps1` backed up every registered service; the newly registered empty Observer and execution folders would have failed the "holds no binaries" check and aborted the first deployment. `Test-FirstInstall` now skips only those two while their main DLL is absent.

## 18-19. Final recommendation

- Paper: **NOT READY** until recovery is certified on representative real Top-5 data and the checklist is executed and verified.
- Real money: **NO-GO**.

Not done in this session, by instruction: no commit, push, merge or deployment; no VM, production database or service access; `D:\Projects\NSE` not accessed; no strategy semantics or trading parameter changed.
