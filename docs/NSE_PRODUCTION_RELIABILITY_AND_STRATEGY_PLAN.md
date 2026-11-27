# Production reliability and strategy enhancement: roadmap and status

Mandate: `NSE_PRODUCTION_RELIABILITY_AND_STRATEGY_MANDATE.md` (authoritative), `NSE_A1_A8_DECISIONS_AND_CONTINUATION.md` and `NSE_FINAL_CONTINUATION_AND_VALIDATION.md`. Branch `feature/production-reliability-strategy-enhancement` from `78d9b8b`. Nothing committed, pushed, merged or deployed; no production or VM access. **Current status: sections 2 and 6b; sections 3-6a are the earlier passes.**

**Recommendations: paper-trading release of this mandate: NOT READY (gates in section 7a). Real money: NO-GO.** Current requirement-by-requirement status: `NSE_RELEASE_CANDIDATE_READINESS_2026-11-27.md`.

## 1. Deployed revision

Local evidence only: the staging build of 26 Nov 08:38 IST (`artifacts/deploy-staging`) carries `1.0.0+78d9b8bd...` in Host and Dashboard. **Not verified on the VM.** `docs/TOP5_EVENT_BASED_CHECKPOINT_2026-11-25.md` records NO-GO (B1, B2, B4 open) for that revision, which apparently was deployed anyway; please confirm. Owner commands:

```powershell
(Get-Item C:\Naveen\NiftySignal.Host\NiftySignal.Host.dll).VersionInfo.ProductVersion
```
```powershell
(Get-Content C:\Naveen\NiftySignal.Host\appsettings.Local.json -Raw) -replace '(?i)("(password|secret|apikey|apisecret|bottoken|chatid|token|userid|passwordsha256)"\s*:\s*")[^"]*"', '$1<redacted>"' -replace '(?i)(Password=)[^;"]+', '$1<redacted>'
```
Check the redacted output before sharing; it shows which paper engines, strategy directory and Top-5 flags are enabled.

## 2. Status by phase (section 43 order)

"Tested" means automated tests on synthetic data and a local scratch PostgreSQL. Nothing is verified on real market data or on the VM.

| # | Phase | Status |
|---|---|---|
| 1 | Incident and architecture audit | Done as far as evidence allows; **root cause unresolved** |
| 2 | P0 safety (independent exits, fail-closed gate, alerts, deploy guard) | Implemented and tested (execution service) |
| 3 | P1 recovery | Bounded replay and live processing, durable checkpoints, time limit and back-off implemented and tested (F73-F75). Real-data timing unmeasured; the 5-minute target is not certified |
| 4 | Gate audit | Done; A1-A8 resolutions recorded in `NSE_ENTRY_GATE_INVENTORY.md` |
| 5 | Four strategies, gates, 20-bar warm-up | Implemented and tested |
| 6 | Avg5 lifecycle | Implemented and tested |
| 7 | DTE/VIX partial exits | Implemented and tested |
| 8 | Dashboard simplification, live paper P&L | Implemented and tested; browser harness updated and passing locally on synthetic data, capture checks included. Real Telegram capture not verified |
| 9 | Deployment tooling and integration | Four-service lifecycle implemented; pure logic tested locally. **Service stop/start paths never run against real services** |
| 10 | Checkpointing, process isolation | Checkpoints implemented and tested; Observer (default) and execution each in their own service |
| 11 | Fault injection, real data, 5-minute certification | Fault cases covered by unit/service tests. **Real-data validation not run**; procedure in `NSE_OWNER_BENCHMARK_PROCEDURE.md` |

## 3. Behaviour changes made (first pass; the continuation is section 6a)

### Recovery (§9-§12)
- Runtime row: new status `RecoveryFailed` (5) and columns `RecoveryPhase`, `LastRecoveryProgressUtc`, `RecoveryTicksProcessed`, `RecoveryBarsReplayed`, `RecoveryPersistedBarsAtStart`, `RecoveryReplayedThroughUtc`, `RecoveryFailureCount`. Written only from the recovery loop (no timer). No total or percentage is estimated.
- `LastError` is no longer cleared when a recovery starts, only when it succeeds. A failure or timeout during recovery is now recorded (before: left `Rebuilding` with no error).
- Live starts are cancelled cooperatively after `TimeLimitSeconds`; a timed-out attempt retries after a back-off, other failures after 1 s as before. Ended-session repair has no limit.
- Level-1 and futures-depth recovery reads are received-time slices (options depth already was). Ordering is unchanged; a per-token disorder larger than one slice now fails the recovery instead of being silently reordered.
- Live raw and depth reads are bounded per query and drained within the same poll. With no cursor yet the query stays unbounded (a LIMIT from Id 0 would walk the primary key). A poll still holds its whole backlog in memory.
- Heartbeats carry the time they are written, and a long catch-up poll writes one per second from inside its loop.
- Still whole-day in memory: session creation and historical bootstrap (`ReadCleanSessionAsync`). Replayed bars are held by the engine as before.

| Setting `Adaptive:Recovery:` | Default (starting value) | Range |
|---|---|---|
| `TimeLimitSeconds` | 300 | 30-3600 |
| `TimeoutRetryBackoffSeconds` | 30 | 1-600 |
| `ReadWindowSeconds` | 120 | 10-900 |
| `ProgressIntervalMilliseconds` | 1000 | 100-10000 |
| `LiveReadBatchSize` | 20000 | 1000-200000 |

Phase timings are logged on success, failure and timeout. Slice reads are inside the "replaying" phase (separately visible only in the existing telemetry activities).

### Alerts (§7)
`ObserverHealthMonitor` runs in the **Dashboard** process and reads the runtime row every 5 s on weekdays 09:16-15:30 IST: no session by 09:20, unreadable database (over 30 s), heartbeat older than 30 s, `Degraded`, `RecoveryFailed` (covers timeouts), rebuilding over 300 s, closed during market hours. One Telegram message per condition per episode, a reminder every 15 min, one recovery notice; a red banner on the Dashboard. Settings `ObserverHealth:` in the Dashboard (`Telegram` false in Development).
Limits: it cannot report the Dashboard or VM being down (needs an external check); exchange holidays produce a "no session" alert; liquidation-failure and reconciliation-failure alerts do not exist because those paths do not exist yet; Telegram needs the token in the **Dashboard's** local settings (not verified on the VM).

### Fail closed (§6): what already holds, and what does not
Paper Engine V2 takes entries only from Observer frames; a frame processed more than 5 s late, after a gap, or unverified blocks entries and cancels the pending order, and 3 clean frames are required afterwards. So a stalled or rebuilding Observer produces no V2 fill. **Missing:** an explicit configurable 30 s heartbeat gate, the same assurance for NPT1, any gate for position reconciliation, and a stale-position policy. Open V2 positions get **no** exit while the Observer is down, including the terminal exit.

### Deployment (§8, §35, §36)
- `tools/DeploymentSafety.ps1`: weekdays blocked 09:00-15:45 IST and any start less than 30 min before 09:00; checked at start and immediately before each service stop. Override only by typing a phrase interactively. Holidays count as trading days.
- Backup root cause: `vm-deploy.ps1` passed both install folders to robocopy as one source (exit 16). Now one verified backup per service (hash check, verified marker, never overwrites; pruning keeps the 3 newest verified and never removes unverified ones); a failed backup stops the deployment.
- False verification failure root cause (inferred from the code): the delta ZIP stores desktop IST file times without a zone, so extracted files on the VM look about 5.5 h newer than the process. Verification now compares file hashes, build identity, service process start (UTC) and the startup log. Migration state is checked only through log errors.

## 4. Migration and rollback
- Migration `20261126043514_AddObserverRecoveryProgress` (script `NiftySignal.AdaptiveObserverData/Sql/adaptive-observer-recovery-progress.idempotent.sql`): seven additive columns; the non-null ones default to 0. The Host applies it at start.
- **Deploy the Host before the Dashboard** (the new Dashboard reads the new columns).
- Rollback: restore the previous binaries and leave the columns; older code ignores them and its inserts use the defaults. Older Dashboards would show status `5` as a number. Dropping the columns (EF `Down`) is not needed.

## 5. Verification

| Check | Result |
|---|---|
| Baseline `78d9b8b`, full Release suite, scratch PostgreSQL | 3109 passed, 0 failed, 1 skipped |
| Final code, full Release suite | see §5b |
| Build warnings versus baseline | compared during implementation; see §5a and §5b |
| `tools/Test-DeploymentSafety.ps1` (local folders only) | 31 passed at the first pass; see 5b |
| New recovery tests (synthetic, in-memory) | slice stream equals whole-day read/normalize/sort (4 seeds), disorder fails closed, progress, timeout vs caller cancel, failure recorded by the worker, batched live and depth reads, heartbeat time |
| New alert tests (synthetic) | classification, clock window, dedup/reminder/recovery, send retry, unreadable database, banner rendering |

The skipped test is the recorded-session Host replay (needs a local recorded database).
**Not verified:** real-data recovery parity and timing; the PostgreSQL plan and cost of the Level-1 slice query (about 190 queries per full day replace one; a bad plan per slice would be slow, as documented for depth: measure before any release; the permission to build a synthetic scratch benchmark was declined); the Dashboard in a browser; the interactive deploy override; anything on the VM. The deploy test script was run without an execution-policy bypass (a bypass attempt was blocked), so the confirmation parameter that existed only for automation was removed and the override path has no automated test.

### 5a. First-pass run
26 November 2026, scratch PostgreSQL on 127.0.0.1: 3138 passed, 0 failed, 1 skipped (3139 total).

### 5b. Final run (continuation, final code)
26 November 2026, same scratch PostgreSQL, clean (non-incremental) Release build then `dotnet test`: **3200 passed, 0 failed, 1 skipped** (3201 total, 4 min 48 s). Build: 0 errors, 15 warnings (first pass: 16). The one skip is the recorded-session Host replay.

| Other check | Result |
|---|---|
| `tools/Test-DeploymentSafety.ps1` (local folders; service cmdlets faked) | 39 passed, 0 failed |
| `tools/TestDeploymentPackaging.ps1` | passed |
| Browser, static pages rendered from synthetic data | Capture grids exactly the five in order; two paper grids with four rows; stale and silent states shown |

An intermediate full run failed 3 tests caused by this work (a Home page comment containing a guarded word, a source check for the removed grid's IV/Vega code, a test finding the copied strategy folder in its output); all were fixed before the final run.

## 6. Decisions: resolved on 26 November 2026

The owner's answers are in `NSE_A1_A8_DECISIONS_AND_CONTINUATION.md` (authoritative; they supersede the recommendations below where they differ). Sections 1-5a above describe the first pass; **section 6a and later describe the continuation.**

| Id | Resolution | Where |
|---|---|---|
| A1 | Separate `NiftySignalExecution` service, single writer (advisory lock and fencing epoch), durable bar publication handoff | `NSE_EXECUTION_SERVICE_ARCHITECTURE.md`, `NiftySignal.ExecutionService` |
| A2 | No forced 15:00 exit. Entry cutoff 15:00, liquidation from 15:08 (starting value), deadline 15:10 | `SessionTimes`, engine |
| A3 | Loss limit, spread limit, capital check and 3 clean frames retained; warm-up 20 verified bars | `paper-engine-v2-strategies/execution-service/` |
| A4 | Four strategies enabled; `TOTAL_VOTE_4_OF_6` disabled; NPT1 paper-live off by configuration | same folder, Host settings |
| A5 | Opposite entry from the same signal once flat; Avg5 waits on a frozen reference contract | engine |
| A6 | Entry gate and protective flatten, heartbeat 30 s, grace 0 | `ProtectionPolicy` |
| A7 | Calendar-day DTE, frozen VIX-based partial target | engine |
| A8 | Three grids removed, two paper grids | see section 6a |

## 6a. Continuation: what was built after the decisions

Design: `NSE_EXECUTION_SERVICE_ARCHITECTURE.md` (section 6a "As built"). Owner procedure: `NSE_EXECUTION_SERVICE_RUNBOOK.md`.

| Area | Change |
|---|---|
| Handoff | `adaptive_bar_publications` (migration `20261126053253_AddBarPublications`), one row per bar after its sidecars |
| Writer | `NiftySignalExecution`, advisory lock and fencing epoch, journal and replay (`20261126053557_AddExecutionService`, owner-run script) |
| Strategies | Four enabled, 20 verified bars, 09:20-15:00 entries, liquidation 15:08/15:10, unresolved after 15:25, Avg5 intents, DTE/VIX partial exits, same-signal reversal |
| Protection | Entry block and flatten on a stale or failed Observer (30 s, grace 0); execution heartbeat and alerts |
| Dashboard | Removed Futures, Options Core Direction and Option Context grids; two paper grids (current, closed today); execution banner; commentary to structured logs |
| Telegram capture | Five grids: `summary-futures`, `summary-total-itm`, `summary-total`, `summary-total-otm`, `residual-compact`; pinned by `data-end-bar`. Option Price Averages is excluded because it quotes the live market |
| Disabled paths | Host registers `PaperLiveBridge` and `PaperEngineV2Bridge` only when enabled |
| Deployment | Order Host, execution, Dashboard; execution stopped first and started last after an ownership check; included in backup and verification; `tools/Install-ExecutionService.ps1` |
| Defects found and fixed during integration | Strategy files were not published with the service; the service logged no build identity; `vm-deploy.ps1` (changed in the first pass) read a removed variable during post-deploy verification |

Intentional test changes: Dashboard grid-count and grid-content tests were rewritten for the removed grids (data and causality assertions moved to the services; assertions that only described removed markup were dropped); the isolation guard allows `NiftySignal.ExecutionService` to reference V2.

Interpretations of this pass, since decided (continuation Q1-Q4, section 6b): delayed immediate selection from the latest verified frame (approved); blanket consumption of intents by any entry block (rejected and corrected); failed or expired Avg5 order returns to waiting (approved with reconciliation); `ExecutionHealth:Required=true` when the service is activated (approved).

Questions as originally asked:

| Id | Question | Recommendation |
|---|---|---|
| A1 | Independent exits, Avg5 on live quotes and partial exits need a tick-level execution path; V2 is a bar-frame engine fed by the Observer. Where does it run and who owns the position ledger? | A separate protection/execution service (own Windows service), consuming raw ticks, as the single writer of position state, with its inputs journaled for replay. Design review before code |
| A2 | V2 exits at 15:00; mandate says no entries from 15:00 and liquidation by 15:10 with no new time exits. Keep 15:00? | Keep the existing 15:00 exit; 15:10 is the independent backstop |
| A3 | Are the daily loss limit (1500), 2 % spread limit, capital check and 3-clean-frames rule mandatory safety gates or removable? | Retain all four |
| A4 | Does "disable other strategies" include NPT1 paper-live as well as `TOTAL_VOTE_4_OF_6`? | Yes, disable by configuration; keep records |
| A5 | May the opposite side be entered on the same signal bar once flat (today: later bar only)? Does a protective flatten consume the signal? | Later fill once flat, same signal allowed for the opposite side only; a flatten consumes the signal |
| A6 | Stale-Observer policy for open positions | Flatten after the 30 s threshold plus a configurable grace |
| A7 | Is DTE calendar days or trading days? | Calendar days (the 6/5 bucket implies it) |
| A8 | The "three bar-wise grids": futures bar grid, options core direction, option context (keeping Option Price Averages)? Diagnostics and the Telegram capture (requires 8 grids) depend on them | Confirm, and update the capture contract |

## 6b. Final continuation (26 November 2026)

| Item | State | Detail |
|---|---|---|
| Q1 delayed immediate entry | Implemented, tested | architecture section 5 |
| Q2 temporary versus terminal blocks | Corrected, tested (`ExecutionIntentLifecycleTests`) | an Avg5 intent now survives temporary blocks; terminal blocks consume it |
| Q3 failed or expired Avg5 order | Implemented, tested | retry only on later quotes, survives replay |
| Q4 execution health | Implemented, tested | `ExecutionHealth:Required=true` is set at activation (runbook section 4) |
| F73 checkpoints | Implemented, tested on synthetic data | architecture section 6b |
| F74, F75 bounded memory | Implemented, tested on synthetic data | remaining retention: architecture section 6c |
| Observer isolation | Implemented, tested; never run as a Windows service | `NiftySignalObserver` |
| Dashboard browser harness | Updated, partial local run | `NSE_DASHBOARD_TEST_CHANGE_INVENTORY.md`; local run passes (F77, F78 closed), CI not run |
| Deployment lifecycle | Scripts and runbook for four services; fake-service tests only | `NSE_EXECUTION_SERVICE_RUNBOOK.md` |
| Real-data benchmark | **Not run**; procedure written | `NSE_OWNER_BENCHMARK_PROCEDURE.md` |

Defect found while documenting and fixed: a failure in the verification that follows a checkpoint restore did not discard the checkpoint, so each retry could restore it again.

### Measurements (developer machine, synthetic data; not certification)

| Measure | Value |
|---|---|
| Bounded replay, 1,845,000 ticks, 1,071 bars, no depth | 1.9 s; retained 1.7 MiB (82.6 MiB before pruning); allocated 1,389 MiB; peak working set 129 MiB |
| Checkpoint | 1.26 MiB, 0.36 MiB compressed; serialize 26-35 ms, compress 2-3 ms, restore 16-31 ms; about 13 MiB allocated per save; database write not included |
| Dashboard commit to grids (browser harness) | p95 about 0.17-0.19 s |

### Execution fault injection (synthetic feed, local scratch PostgreSQL)

All passed in the final run (`ExecutionServiceTests`, `ExecutionLifecycleTests`, `ExecutionIntentLifecycleTests`, `ExecutionOrderingTests`).

| Fault | Behaviour tested |
|---|---|
| Stale, missing or unreadable Observer state | entries blocked; flatten beyond 30 s without needing a bar, on a quote newer than the decision |
| No executable quote during a protective exit | position stays open and is alerted; no fill invented |
| Second writer; superseded writer | does nothing; cannot commit |
| Failed commit | memory discarded, rebuilt state fills exactly once |
| Restart | journal replay reproduces ledger and positions; pending Avg5 intent survives |
| Ledger differs from journal | entries blocked, exits continue |
| Observer unreadable at restart | waits visibly, then recovers |

Host quotes unavailable: exits are requested and alerted but cannot fill. PostgreSQL unavailable: nothing can be read or recorded and no fill is invented; the service alerts directly. VM, disk or network down: **positions are unprotected**, and nothing on the VM can act or necessarily alert. These three statements follow from the design and the tests above; no real outage was injected.

### Verification (final code)

| Check | Result |
|---|---|
| Clean Release build of the solution | 0 errors, 11 warnings, no `NU` warning (final release preparation, non-incremental) |
| Full test suite, scratch PostgreSQL | **3250 passed, 0 failed, 1 skipped** (recorded-session Host replay); vulnerable-package scan: none reported |
| `tools/Test-DeploymentSafety.ps1` (local folders, faked service cmdlets) | 46 passed, 0 failed; packaging check passed |
| `tools/TestDeploymentPackaging.ps1` | passed |
| Browser harness, installed Edge | passed, **partial**: screenshot delivery, restart without resends, pre-live test and both capture pages not run |

The first full run of this pass had one failure (the harness project referenced the V2 Runtime directly, against the isolation guard); the reference was unnecessary and was removed. Database queries against scratch databases were refused by the permission classifier twice during this work (activity view; observer database) and once creation of a scratch benchmark database; none was worked around.

**Readiness: paper release NOT READY (gates in section 7a). Real money NO-GO.**

## 6c. Final release preparation (26 November 2026)

**Dependency advisory (NU1903).** GHSA-334f-vvp4-7g6p / CVE-2026-70159, High: denial of service through SignalR stateful-reconnect backpressure. Chain: Host and ObserverRuntime → `Microsoft.AspNetCore.SignalR.Client` 10.0.0 → `SignalR.Common` 10.0.0. Affected 10.0.0–10.0.12. Fix: the one central version, Client 10.0.0 → **10.0.13**; no other package changed. The Dashboard hub (server side) comes from the ASP.NET Core runtime installed on the machine: **the VM needs ASP.NET Core runtime 10.0.13 or later** (`dotnet --list-runtimes`), unless the publish is self-contained (the deployment script publishes framework-dependent builds). Not checked on the VM.

**F76 fixed.** The live Option Price Averages grid re-reads the quotes at most once a second between bars, so the age is real elapsed time; a failed read leaves the last quotes to age to stale and no longer ends the page's refresh loop. Averages and reference selection are unchanged. Execution does not use this display: it reads its own tick feed and applies the strategy's quote-age limit.

**Diff audit against 78d9b8b.** Two correctness defects found and fixed:

| Finding | Fix |
|---|---|
| The in-Host paper runtime took the writer lock once and never checked it: after a dropped connection the execution service could take the lock while the Host kept writing (needs both engines enabled) | The lock is checked before each bar; if another writer holds it the runtime closes. Test added |
| The documented order deployed a `Separate` Host before disabling in-Host paper settings; with either enabled the new Host refuses to start, stopping ingestion | Deployment is refused before anything stops (`Get-ObserverInstallProblems`); runbook states the step. Test added |

Smaller corrections: the missing `adaptive-observer-checkpoints.idempotent.sql` was generated; the benchmark procedure's bar-to-decision query read a table the execution service never writes (now its journal); the disk-size query was made reliable; a live P&L latency sampler was added (`tools/Measure-LivePnlLatency.ps1`).

Confirmed: both EF contexts have no pending model change; four enabled strategies (`TOTAL_VOTE_4_OF_6` disabled); each 2 lots, 5 trades a day, capital 50,000, daily loss limit 1,500 (unchanged), entry from 09:20 with 20 completed bars; Avg5 variants differ only in id, version and timing; DTE/VIX matrix equals the approved table, VIX 16 in the upper bucket, frozen at entry; base and Avg5 variants exit and then reverse on an opposite signal. Test counts per changed test file did not decrease; removed assertions are those of the removed grids (`NSE_DASHBOARD_TEST_CHANGE_INVENTORY.md`).

Noted, not changed: the execution journal is kept for all days and a rebuild loads the current day into memory; old checkpoint rows are never deleted (F74/F75 and the architecture document, section 6c).

### Capture and Telegram-screenshot checks (developer machine)

**Result (26-Nov-2026, IST Thursday evening, fresh scratch cluster, synthetic data, Chromium 140.0.7339.16, owner-approved download, loopback Telegram emulator): all harness checks PASS.** The first run that day failed at bar 43 (F77); two intermediate runs stopped on errors in the new checks themselves (a wrong hand-worked IV anchor, an ambiguous heading selector).

| Check | Result |
|---|---|
| Seven deliveries (bars 13, 18, 23, 28, 33, 38, 43), each captioned for its bar, once, on the first attempt | PASS |
| Newest completed bar (43) captured with no later bar stored | PASS |
| Each job page: own completed bar, job id, exactly the five grids in order, no removed grid, corrected subtitle | PASS |
| ITM-shifted, ATM, OTM-shifted TOTAL values (all windows, 11 value columns) and futures Day coverage / volume / Strict against fixture closed forms | PASS |
| Option Price Averages: exact Avg5 / Avg10 and ask gaps, CE and PE (live page; not part of a capture) | PASS |
| Worker restart without resends | PASS |
| Pre-live delivery test and its page | PASS |
| Pinned live-quote capture page | PASS |
| Fixed-clock capture and its page | PASS (grids present with values; exact values not asserted) |
| Unavailable summary: refused, queued, delivered on attempt 2, no duplicate | PASS |
| Restart parity, late start, sidecar degradation, live quote, selectors, readiness, market summary, paper grids, health banners | PASS |
| Residual grid values | NOT RUN: presence and boundary row only; the fixtures hold placeholder or unavailable residuals |
| Option Depth-5 columns | NOT RUN: no Depth-5 rows in these fixtures (asserted shown as unavailable) |
| Real Telegram, real data, CI | NOT RUN |

**F77 root cause: the test fixture, not the renderer.** The hand-built session stored no `adaptive_bar_close_atm` rows. The Dashboard then falls back to the next bar's band centre, which bars 13-38 had and bar 43 did not. The Observer writes that row for every completed bar from the state at that bar's close, so an eligible bar does not need a later one. The fallback value is the same close-time selection (no look-ahead in the value; only its availability depends on the next bar). The fixture now writes strikes, attestation and close ATM through the Observer's writers before it announces the bar complete. No renderer or persistence code changed for F77.

Limits of that statement (read from source, unchanged): the row is a separate write after the core bar commit, so a capture can start before it exists or after it failed; it is then refused and retried every 60 s without a cap. A bar whose close ATM is stored as unavailable never gets a picture.

**F78 fixed.** The subtitle names the five current grids; a pre-live test without a session says that it shows no market grid and no market bar. The empty "fixed strike history" heading and its row note were removed from captures; the live page keeps the section.

Without `pwsh`, the bundled command line installs the harness copy: `.playwright\node\win32_x64\node.exe .playwright\package\cli.js install chromium` from the harness build folder.

The two install commands download Playwright's Chromium from Microsoft's CDN (harness and Dashboard folder). Uses only a throw-away cluster (the benchmark procedure's section 3.3, port 54340, trust authentication): the harness **writes** to databases `niftysignal` and `niftysignal_adaptive_observer` on the port given, so never omit the port. Telegram goes to a loopback emulator; no real recipient. Ports 5089 and 5091 must be free. `pwsh` is PowerShell 7. Run from the repository root, on an IST weekday after 09:40 (otherwise the fixed-clock capture is `NOT RUN`).

```powershell
dotnet build tools\AdaptiveDashboardValidation\AdaptiveDashboardValidation.csproj -c Release "-p:CommitSha=$(git rev-parse HEAD)" "-p:SourceBranch=uncommitted-validation"
```

```powershell
pwsh tools\AdaptiveDashboardValidation\bin\Release\net10.0\playwright.ps1 install chromium
```

```powershell
dotnet NiftySignal.Dashboard\bin\Release\net10.0\NiftySignal.Dashboard.dll --install-screenshot-browser
```

```powershell
$env:ADAPTIVE_VALIDATION_PGPORT = "54340"; Remove-Item Env:ADAPTIVE_VALIDATION_BROWSER_CHANNEL -ErrorAction SilentlyContinue
```

```powershell
dotnet tools\AdaptiveDashboardValidation\bin\Release\net10.0\AdaptiveDashboardValidation.dll "$PWD" (git rev-parse HEAD)
```

Pass: exit 0, `CAPTURE PAGE PASS`, `CAPTURE RETRY PASS`, `Telegram screenshot PASS`, `PINNED LIVE QUOTE BROWSER PASS`, `Dashboard PASS` without `PARTIAL`, no `NOT RUN` line; look at the PNGs under `artifacts\adaptive-dashboard-validation`. Cleanup: stop the scratch cluster, close the window.

## 6d. Final engineering pass before owner-run certification (26 November 2026)

No new trading feature, strategy condition, Dashboard change or schema change. No migration was added.

### Writer fencing at the persistence boundary

Gaps found: in-Host writes used pooled connections with no fence; the service checked the epoch, but a legacy writer taking over claimed none, so a superseded service could still commit.

| Change | Detail |
|---|---|
| One fence | `ExecutionOwnership` owns the advisory-lock connection and the epoch it claimed. Every ledger write is a transaction **on that connection**, which locks the state row (`FOR SHARE`) and requires the epoch. A lost lock means no commit; a newer epoch waits for a write in progress and refuses everything after it |
| Both writers | Event store, frame / disposition / gap journal, alert outbox insert, opportunity log, live marks, execution journal commit and heartbeat |
| In-Host loss | The pipeline built under the lost ownership and its queued frames are discarded; a new ownership rebuilds from the journal, or the runtime closes if another writer holds the ledger |
| Opportunity rows (service) | Held until the input they describe is committed (a replay writes them again idempotently) |
| Not fenced | Alert delivery-status updates (no position data). Without `pev2_execution_state` there is no epoch; the lock-connection transaction alone applies |
| Residual limit | Binaries older than this release take neither lock nor epoch: the runbook requires them stopped |

Concurrency tests (scratch PostgreSQL): ownership moved between decision evaluation and persistence, by a newer epoch and by terminating the owner's session, for the service (no journal, event or alert row changes; the next owner reconciles); the same race in the in-Host engine with later frames already queued (ledger digest unchanged); a stale instance stays refused after the process owns again; a new epoch waits for a write in progress.

### Checkpoint retention

`Adaptive:Recovery:CheckpointRetention` (3) was already bound and applied, with two weaknesses: rows of another build or damaged rows occupied the slots, and saves made during an unverified restore could delete the fallback. Now: newest N per usable identity and separately N of others; the restored row and older ones are held until the recovery verifies; damaged rows are removed, rows that merely disagree with persisted history are kept; a row removed while a restore is listing is passed over; cleanup is at most 20 rows, oldest first, contained. Completed sessions keep their rows (runbook table). Only `adaptive_checkpoints` rows are deleted. Tests cover the configured count (bound through the Host's registration), both limits, the fallback during a failing replay, bounded cleanup, damaged / incompatible rows and concurrent removal.

### Execution journal recovery

The rebuild loaded the whole day and kept every replayed frame, in the rebuild and in live processing. It now reads pages (`Execution:JournalReplayBatchSize`, default 200) in identity order and keeps only bar number, hash and the newest frame. The journal and its schema are unchanged.

Synthetic, developer machine, full-metric frames without trades (peak is live managed memory sampled with forced collections; not certification):

| Journaled bars (size) | Before: rebuild / peak / retained | After: rebuild / peak / retained |
|---|---|---|
| 600 (22 MiB) | 1.62 s / 125.2 MiB / 40.4 MiB | 1.03 s / 27.5 MiB / 0.4 MiB |
| 3,000 (110 MiB) | 3.51 s / 647.0 MiB / 202.1 MiB | 2.35 s / 27.2 MiB / 1.3 MiB |

Parity test: restarts with an order outstanding plus a pending Avg5 intent, after the fill (frozen 20 % target at DTE 6, VIX 14), with a partial exit ordered, after the partial fill and after the Avg5 fill; at page sizes 1, 2, 7 and 5,000 the recovered strategies, positions, counters, P&L, events, bar cursor and the whole subsequent journal are identical, and the paging cursor returns every row once. Engine events and opportunity records of the day are still held in memory (they grow with trades, not bars). The in-Host fallback engine's own journal still loads a whole session (`PENDING (audit finding F81)`).

### Checkpoint certification path and benchmark procedure

- New stage `certify` in `tools/AdaptiveLivePerformance`: one recorded day with stored five-level depth, full replay, uninterrupted live, checkpoint restart and restart without checkpoints; simulated session clock for live checkpoint intervals; a full-replay fallback fails the verdict; refuses a non-loopback scratch server. **Built, never executed** (no authorised data, and creating the scratch performance databases was not permitted here).
- `NSE_OWNER_BENCHMARK_PROCEDURE.md`: A = `78d9b8b` built in its own worktree, B = new build with checkpoints off, C = the same build with checkpoints on; certification in 5.1.
- Added trace span `checkpoint.restore` for the restore duration.

### Verification (final code)

| Check (26 November 2026, developer machine) | Result |
|---|---|
| Release build, full solution | 0 errors, 12 warnings (unused-variable / parameter and xUnit analyzer types; no NuGet vulnerability warning) |
| Full Release test suite, both scratch PostgreSQL settings | 3,264 passed, 0 failed, 2 skipped (recorded-session Host replay; journal recovery benchmark), 3,266 total |
| Vulnerable packages, 45 projects, transitive | none |
| Pending EF model changes, 7 contexts | none |
| SQL scripts regenerated by EF (ledger script; three new observer scripts) | identical |
| Deployment safety tests | 47 passed, 0 failed (after the correction below) |
| Deployment delta ZIP test; `git diff --check` | pass |
| Browser harness incl. Telegram emulator and capture checks, fresh loopback scratch cluster | exit 0, nothing reported NOT RUN |

Correction found during release preparation: on the first VM deployment `vm-deploy.ps1` would have tried to back up the newly registered, still empty Observer and execution folders, which the backup refuses ("holds no binaries"), aborting the deployment. Those two services are now skipped until their main DLL exists; Host and Dashboard always need a verified backup. Found by reading the code; covered by a unit test of the decision, not run against a VM. No .NET source changed after the full test run.

**Real-data certification attempt (26 November, evening):** blocked at the first database read; see `NSE_REAL_DATA_CERTIFICATION_REPORT_2026-11-26.md`.

## 7. Remaining risks
- The 25 November cause is unresolved and could recur.
- Recovery time on real data is unmeasured. If checkpoints are disabled or cannot be saved and a full replay exceeds `TimeLimitSeconds`, the Observer never goes live.
- A restart after a deployment is a full replay (checkpoints are bound to the build).
- The `certify` stage has never been executed; its first owner run may expose a tool fault.
- Execution service on real data is unproven: tick tail, frame assembly (reads the session's bars up to the current one), quote latency against the 2 s age limit, signal lag against 5 s.
- Same-VM limitation: VM, disk, network or database failure leaves positions unprotected and may raise no alert. A dead Host means exits cannot fill. Service isolation does not protect against these.
- Positions left open from an earlier day are alerted, not carried over.
- Paper fills are all-or-none; real partial fills are not simulated.
- Checkpoint rows of completed sessions stay (bounded per session, no cross-session cleanup).
- Binaries older than this release take no ledger lock or epoch; running one beside the new services is not prevented by the database.
- In-Host fallback engine: its session journal is read whole on a rebuild (F81).
- Top-5 items B1 (tick retention), B2 and B4 (feed semantics) remain open.

### 7a. Gates before a paper release
1. Owner benchmark on real sessions (`NSE_OWNER_BENCHMARK_PROCEDURE.md`): builds A, B, C; recovery under 300 s; section 5.1 `CRITERIA MET` on representative Top-5 data; Top-5/L1 replay parity.
2. Execution service shadow run on real data: latency, staleness rejections, replay after restart.
3. Browser capture checks pass locally on synthetic data; still open: the CI run, then the running Dashboard and Telegram capture against a scratch copy.
4. Deployment and rollback rehearsed with the four services (local script tests pass; no rehearsal on real services).
5. Owner confirmation of the deployed revision and VM paper settings.

## 8. Not delivered or not verified (section 45)
Real-data parity, performance and recovery certification; CI run of the browser harness; live Dashboard and Telegram evidence; VM deployment and service-control evidence; incident root cause (owner-run evidence collection, incident document section 4).

## 9. Files changed
Use `git status` on the branch. New projects/areas: `NiftySignal.ExecutionService/`, `NiftySignal.Observer/`, `NiftySignal.ObserverRuntime/`, `paper-engine-v2-strategies/execution-service/`, deployment and install scripts in `tools/`, observer and ledger migrations, Dashboard components, harness changes, tests and `docs/NSE_*`.
