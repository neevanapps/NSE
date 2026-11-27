# Release readiness, paper session of Monday 30 November 2026

**Status (27 November, after 13:00): tree `45008e3...` has a passing full test suite, both real-data recovery certifications MET (section 13.6) and four packages built and validated locally (section 11). The owner approved the commit on 27 November (this report is part of it); nothing is merged, pushed or deployed. The 300-second recovery finding is closed by the owner.**

**Recommendation: local validation complete; GO to proceed to owner approval, commit and merge for paper trading on Monday 30 November.** Deployment still depends on the VM preflight in the owner checklist. VM performance (recovery time, freshness false-block rate) is unmeasured. Real money: NO-GO. Paper only.

Nothing has been merged, pushed or deployed. The VM was not contacted. No broker session was started and no order was placed.

## 1. Source identity

| Item | Value |
|---|---|
| Working directory | `D:\Projects\NSE-release-20261124` |
| Branch | `feature/production-reliability-strategy-enhancement` |
| HEAD / baseline | `78d9b8bd84d0b9bb38dd91a80ae89f0dad8fd5fe` (all release work uncommitted) |
| **Candidate source tree (without `docs`)** | `45008e381918ec091c37b1b9c861ea9a1fa03630` (section 13) |
| Previous tree | `aa906ab302ce7e770d03c1c4002aa40e5a15808e`: sections 3-7 and 12 were measured on it; section 13 states what still applies |
| Superseded tree | `25e7f964...` (before the publication fix; its packages and certification are invalid) |

## 2. Tasks

| # | Task | State |
|---|---|---|
| 1 | 09:45 recovery certification | passed |
| 2-5 | Benchmarks A, B, C and repeats | One full and one restart run passed for each of A, B and C; second round waived by the owner (section 5c) |
| 6 | Top-5 full-table parity, off / futures / both | passed on correctness; durations: section 5 |
| 7 | Performance, memory, hashes, database operations | measured once per configuration (section 5b); full-table comparison in section 12.7 |
| 8 | No metric / strategy / execution-safety regression | tests, parity, live-vs-replay and A/B/C hashes passed; full-table recovery timing open (section 12) |
| 9 | No retrospective or stale-price entry after recovery | reviewed: section 6 |
| 10 | Two skipped tests and gaps | section 7 |
| 11-12 | Packages and their validation | built and validated locally (section 11) |
| 13-14 | Checklist, runbook, deployment commands | sections 9-10; checklist updated at the end |

## 3. Measured durations

Source: `D:\perf-scratch\out\overnight\durations.csv`. All on tree `aa906ab...`. Developer machine, not the VM.

| Step | Start (IST, 26-27 Nov) | Seconds | Exit |
|---|---|---|---|
| build baseline | 22:49:17 | 199 | 0 |
| build candidate | 22:53:51 | 140 | 0 |
| build tests | 22:56:11 | 135 | 0 |
| certification 09:45 | 22:58:26 | 1181 | 0 |
| parity off | 23:18:07 | 519 | 0 |
| parity futures | 23:26:46 | 578 | 0 |
| parity both | 23:36:24 | 739 | 0 |
| A full | 23:48:44 | 604 | 1 |
| A restart | 23:58:48 | 1008 | 0 |
| B full | 00:15:36 | 601 | 1 |
| A full (end 15:35:30) | 05:47:38 | 1089 | 1 |
| B full (end 15:35:30) | 06:05:47 | 608 | 1 |
| B full (end 15:36) | 07:40:11 | 660 | 0 |
| C full (end 15:36) | 07:51:11 | 591 | 0 |
| B restart | 08:01:03 | 1010 | 0 |
| C restart | 08:17:53 | 1091 | 0 |
| A full (end 15:36) | 08:37:02 | 1239 | 0 |

An A full attempt started at 07:39:49 was stopped before it finished (no duration row, no report).

## 4. Recovery certification (real recorded data, 23 November, checkpoints on)

| Measure | 14:30 restart | 09:45 restart |
|---|---|---|
| Result | CRITERIA MET | CRITERIA MET |
| Full-session replay | 90.28 s | 89.95 s |
| Replay peak working set / managed heap | 536.3 / 373 MB | 537.4 / 366.6 MB |
| Checkpoint saves (p50 / max) | 299 (47 / 191 ms) | 14 (25 / 54 ms) |
| Newest checkpoint size | 804,839 bytes | 208,599 bytes |
| Bars published before the restart | 196 | 44 |
| Restart from checkpoint | 1 s (restore 95.5 ms) | 0.78 s (restore 38 ms) |
| Ticks replayed after restore | 8,832 of 3,329,352 | 10,300 of 370,493 |
| Working set after checkpoint restart | 672.4 MB | 711.2 MB |
| Restart without checkpoint | 21.47 s (1,583,081 ticks, 831.8 MB) | 2.96 s (173,605 ticks, 775.1 MB) |
| Table parity (two comparisons) | 16/16 and 17/17 | 16/16 and 17/17 |
| Completed bars / publications | 228 / 228 | 228 / 228 |

Reports: `D:\perf-scratch\out\certification-C2-certify-1430-20261123.json` and `certification-C2-certify-0945-20261123.json`. (`certification-C-certify-1430-20261123.json` is the earlier failed run on the superseded tree, kept as evidence.)

## 5. Top-5 full-table parity

Day 2026-11-23, universe 40, anchor 25670, 46,503 raw future ticks, 2,550,075 option ticks, 380 bars. This test uses the opening-volume projection model (380 bars), not the fixed 13,650 clock (228 bars), and uses no checkpoints.

| Measure | off | futures | both |
|---|---|---|---|
| Core hash | `3B38839FDDD97932` | same | same |
| Same result after restart | yes | yes | yes |
| Mismatches / failures / projection health | 0 / 0 / 0 | 0 / 0 / 0 | 0 / 0 / 0 |
| Cold build (insert) | 280.17 s | **332.51 s** | **428.52 s** |
| Verified rebuild | 231.07 s | 241.88 s | 296.80 s |
| Allocated | 60,377 MB | 72,063 MB | 153,491 MB |
| Peak working set / managed | 312 / 162 MB | 313 / 167 MB | 389 / 232 MB |
| GC pause | 4,228 ms | 4,674 ms | 10,137 ms |

Rows with Top-5 on: strike bars 15,200, strike attestations 380, futures depth-5 380, option depth-5 15,200 and 380 attestations. Option pass (both): 2,550,075 rows, 1,087,763 events, 10.13 s, completion p99 0.385 ms, max 7.636 ms, usable 99.31 %.

### Open finding: full-table cold recovery above 300 s

- `NSE_OWNER_BENCHMARK_PROCEDURE.md` section 1 lists full-session recovery under 300 s as mandatory; section 5.2 says this parity test does not apply that limit. The Observer's configured recovery time limit is 300 s.
- The certification replay of the staged one-day source took 90 s. The procedure calls staged timings optimistic. This test, against the full tables, took 332.5 s (futures) and 428.5 s (both).
- A checkpoint restart took about 1 s. The slow path applies only when no usable checkpoint exists (first start on a day mid-session, rejected checkpoint, version change).
- Whether this is slower than the baseline is **not known**. An earlier run on a different, smaller day (`TOP5_EVENT_BASED_CHECKPOINT_2026-11-25.md`: 170 bars) gave base to new: off 117.1 to 113.8 s, futures 127.4 to 130.6 s, both 168.8 to 171.3 s. Not comparable with today's day.
- A same-day baseline run in `D:\perf-scratch\nse-78d9b8b` would answer it.
- *Update: section 12.10 shows the time above 300 s comes from connection pooling being off in the benchmark.*
- **Owner decision required**: accept the risk for a paper session (start the Observer before the market opens so that recovery stays short), or hold the release. *The owner chose HOLD (section 5c). The same-day baseline run has since been made: section 12.7 supersedes the "not known" above.*

## 5a. Failed check: live versus replay in the full benchmark (A and B)

`EQUIVALENCE live-vs-restart-replay 2026-11-23: DIFFERENT` in A full and in B full. The script stopped at B full.

Cause (established from the outputs, not yet confirmed by a rerun):

- The live simulation ends at 15:35:00 by default and held 227 bars (22 from the start recovery plus 205 live).
- The replay at 15:40 verified 227 persisted bars (`verified=227`) and created one more (`completedBars=228`).
- The certification data show the following partial bar starting at 15:35:05, so bar 228 completed just after 15:35:00.
- A restart, whose horizon is 15:35:30, ran 228 bars; all four restart cases matched at the horizon and after the close.

First correction, ineffective: `ADAPTIVE_PERF_SIM_END_IST=15:35:30`. The tool uses only the hour and minute of the value, so the reruns (A full 05:47, 1,089 s; B full 06:05, 608 s) again ended at 15:35:00 with 205 live bars and `DIFFERENT`, and the script stopped at B full again.

Second correction: the script sets `15:36`. No source or assertion changed; the source tree is unchanged. **Confirmed** by B full at 07:40: live future bars 228 (digest n=228), `live-vs-restart-replay` IDENTICAL, `stress-vs-unthrottled-replay` IDENTICAL, exit 0. Live digests of this run differ from the 15:35:00 runs because they include bar 228; A full has to be rerun with 15:36 before A, B and C digests are compared.

To watch in the repeats: this run's live poll p99 was 5.82 ms (06:05 run: 1.65 ms), max 75.5 ms, warm recovery 17.0 s (13.3 s). One run cannot separate machine load from a real difference. C full, run next, had p99 1.60 ms.

### B against C, first full runs with end 15:36

| Measure | B (checkpoints off) | C (checkpoints on) |
|---|---|---|
| Live digests (seven tables, 228 bars) and projection health | | identical to B |
| Live-vs-replay and stress-vs-replay | IDENTICAL | IDENTICAL |
| Live poll p50 / p99 / max | 0.649 / 5.818 / 75.5 ms | 0.617 / 1.599 / 76.4 ms |
| Bar poll p50 / p99 | 14.66 / 64.72 ms | 14.37 / 19.25 ms |
| SQL per bar poll | 17 | 17 |
| Observer SQL total / writes (live) | 46,980 / 23,386 | 47,044 / 23,411 |
| Warm recovery | 17.01 s | 5.50 s (from a checkpoint holding 160 bars) |
| Recovery observer SQL (writes) / source | 2557 (21) / 195 | 799 (13) / 86 |
| Recovery peak working set / managed heap | 1081 / 842 MB | 1107 / 839 MB |
| Stress poll p99 / max | 15.14 / 17.79 ms | 14.96 / 16.78 ms |

### Restart runs: baseline A against candidate B (checkpoints off)

All four cases in both: equal at the horizon, equal after the close, live state equal, 228 bars, projection health 0; negative control detected. Across A and B the dumped tables differ in one field only, `BuildUtc` in the frozen session definition (so that table's hash differs); every other table hash is identical.

| Case | Recovery A / B (ms) | Peak working set A / B (MB) | Peak managed heap A / B (MB) | Observer SQL A / B | Source SQL A / B |
|---|---|---|---|---|---|
| A incomplete bar | 5525 / 5193 | 2010 / 1030 | 1897 / 858 | 1354 / 1372 | 131 / 115 |
| B after a bar | 8024 / 6487 | 2207 / 1068 | 2093 / 844 | 1673 / 1693 | 187 / 146 |
| C1 before fast bar | 8249 / 6771 | 2109 / 1049 | 1975 / 856 | 1698 / 1718 | 191 / 148 |
| C2 after fast bar | 8170 / 6538 | 2258 / 1080 | 2130 / 875 | 1695 / 1715 | 191 / 148 |

### Restart runs: C (checkpoints on)

All four cases equal at the horizon, after the close and in live state; 228 bars; projection health 0; negative control detected. The B and C dump files are byte-identical (SHA-256 `1E9B0BAE...F2A`), and A, B and C dumps are equal when `BuildUtc` is ignored.

| Case | Recovery B / C (ms) | Peak working set B / C (MB) | Observer SQL B / C | Source SQL B / C |
|---|---|---|---|---|
| A incomplete bar | 5193 / 4912 | 1030 / 1014 | 1372 / 1376 | 115 / 115 |
| B after a bar | 6487 / 6387 | 1068 / 1050 | 1693 / 1697 | 146 / 146 |
| C1 before fast bar | 6771 / 7037 | 1049 / 1071 | 1718 / 1724 | 148 / 148 |
| C2 after fast bar | 6538 / 2214 | 1080 / 1108 | 1715 / 223 | 148 / 63 |

Only case C2 resumed from a checkpoint (136 bars restored). The other three replayed from the start, so they show no checkpoint speed-up.

Evidence kept in `D:\perf-scratch\out\evidence-simend-1535\`.

## 5b. Baseline against candidate, full benchmark (first runs, 15:35:00 end, one run each)

The seven live table digests over 227 bars are **identical** between A and B (future bars, rolling states, option bands, residuals, futures and options supplemental, Weak2), with 0 projection-health rows.

| Measure | A (baseline) | B (candidate, checkpoints off) |
|---|---|---|
| Live poll p50 / p99 / max ms | 0.601 / 1.562 / 47.5 | 0.619 / 1.625 / 59.7 |
| Poll with completed bar p50 / p99 ms | 14.51 / 23.79 | 14.21 / 21.13 |
| Live peak working set / managed heap MB | 1253 / 977 | 1169 / 886 |
| Retained option book states, live | 806,301 | 5,046 |
| Observer SQL per bar poll | 16 | 17 |
| Observer SQL total / writes | 46,690 / 23,139 | 46,891 / 23,342 |
| Warm full-day recovery ms | 13,999 | 16,160 (+15 %) |
| of which sidecars ms | 467 | 2,080 |
| Recovery peak working set / heap MB | 2582 / 2444 | 1070 / 860 |
| Recovery observer SQL (writes) / source SQL | 2522 (8) / 2 | 2560 (27) / 195 |
| Stress 5x poll p99 / max ms | 14.53 / 15.83 | 14.70 / 15.32 |
| Stress equals unthrottled replay | identical | identical |

Single runs: differences of a few percent are not established. C and the repeats are missing.

### Final A / B / C verdicts (corrected 15:36 end)

Source tree rechecked after the runs: `aa906ab302ce7e770d03c1c4002aa40e5a15808e`.

| Check | A (baseline) | B (checkpoints off) | C (checkpoints on) |
|---|---|---|---|
| Live-vs-replay, stress-vs-replay | IDENTICAL, IDENTICAL | IDENTICAL, IDENTICAL | IDENTICAL, IDENTICAL |
| Live metric hashes, 228 bars (7 tables), projection health 0 | reference | all equal to A | all equal to A |
| Restart cases (4) | passed | passed | passed |
| Live poll p50 / p99 / max (ms) | 1.059 / 8.81 / 95.6 | 0.649 / 5.82 / 75.5 | 0.617 / 1.60 / 76.4 |
| Bar poll p50 / p99 (ms) | 27.09 / 84.19 | 14.66 / 64.72 | 14.37 / 19.25 |
| Live peak working set / managed (MB) | 1075 / 886 | 1110 / 840 | 1072 / 874 |
| SQL per bar poll; observer total / writes | 16; 46,668 / 23,127 | 17; 46,980 / 23,386 | 17; 47,044 / 23,411 |
| Warm recovery (s) | 20.35 | 17.01 | 5.50 |
| Recovery peak working set / managed (MB) | 2599 / 2444 | 1081 / 842 | 1107 / 839 |
| Recovery observer SQL (writes) / source | 2519 (2) / 2 | 2557 (21) / 195 | 799 (13) / 86 |
| Recovery allocated (MB) | 7441 | 7351 | 3114 |
| Stress poll p99 / max (ms) | 27.22 / 55.70 | 15.14 / 17.79 | 14.96 / 16.78 |

**Correctness: passed for A, B and C.** **Performance: no regression observed, but not established.** Each figure is one run, and A alone ranged from 14.0 s to 20.3 s warm recovery and 604 to 1,239 s for the full step across last night and today, so timing differences between builds are within unexplained variation. The memory reduction in recovery (about 2.5 GB to 1.1 GB) and the extra source queries (2 to 195) are large and consistent with the bounded-memory design. These workloads use the staged one-day source and Top-5 as configured by the benchmark tool; they say nothing about the full-table finding in section 12.

## 5c. Owner waiver: repeated benchmark runs

On 27 November at about 08:39 IST the owner chose to skip the second benchmark round (task 5). Each configuration is therefore measured once with the corrected end time (A restart once, from the first night). Consequence: timing stability is not established, and run-to-run variation seen so far is unexplained (A full 604 s and 1,089 s; B full live poll p99 1.65 ms and 5.82 ms). Correctness checks are not waived. The script's closing source-tree recheck and `ALL-STEPS-COMPLETED` marker do not run when it is stopped; the tree is rechecked by hand before packaging.

**Owner decision, 27 November about 08:41 IST: HOLD.** The exception above 300 s is not accepted; the timeout and the certification criteria stay unchanged; today's deployment window is given up and the target is Monday 30 November. Investigation: section 12.

## 6. Recovered publications cannot cause retrospective entries

- A bar published late has a processing lag above 5 s, so entries are not allowed for it. The signal gates block new entries and reversals, and pending orders and temporary waiting intents are cancelled.
- When the Observer is not Live, entries are cancelled.
- A fill needs a quote at most 2,000 ms old and strictly later than decision plus latency.
- Exits are evaluated on every verified frame. A late exit fills at the current price, by design.
- Covering tests passed: `ConsumerTests` (sequence gap, processing lag, unverified frame), `ExecutionIntentLifecycleTests`, `ExecutionLifecycleTests`, `EarlyEntryTests`.
- Gap: no coordinator-level test feeds a whole late backlog.
- Minor observation, independent of publications: after an execution-service restart, a journaled 5-bar-average waiting intent can place on the first cycle when no newer bar has arrived. It uses fresh quotes and the latest bar's average.

## 7. Tests and validation gaps

Full suite on tree `aa906ab...` (`dotnet test NiftySignal.slnx -c Release`, disposable PostgreSQL): **3,267 passed, 0 failed, 2 skipped**.

| Skipped | Needs | Gap left |
|---|---|---|
| `HostReplayTests.RecordedSession_..._WithNpt1Parity` | recorded source database and trading test database | in-Host path against a recorded session not exercised; does not cover the execution service |
| `ExecutionJournalRecoveryBenchmark.RebuildFromTheJournal_TimeAndManagedMemory` | `NSE_MEMORY_BENCH=1`, local PostgreSQL | journal rebuild time and memory unmeasured (synthetic data) |

The second skipped test was then run on its own, same tree, disposable loopback PostgreSQL: **passed** (synthetic journal, not a certification): 600 bars rebuilt in 1.09 s (22.0 MiB journal, peak live managed 21.4 MiB, retained 0.3 MiB); 3,000 bars in 2.41 s (110.0 MiB, 25.7 MiB, 1.5 MiB). Its gap is closed apart from having no baseline comparison. The `HostReplayTests` gap remains.

Other gaps:

- `OptionsDepth5HostBenchmark`, `FuturesDepth5HostBenchmark` and `Depth5ChannelBenchmark` return early and count as passed when not configured. Only the parity runs of section 5 exercised the first.
- One recorded day. Developer machine, not the VM. No live broker feed. Telegram only against the local emulator.

## 8. Defects

- Fixed: completed bars recovered at start could stay unpublished (`NSE_CERTIFICATION_FAILURE_PUBLICATIONS_2026-11-26.md`). Three new tests fail without the fix and pass with it; both certifications show 228 of 228.
- Closed by the owner, 27 November: section 12 (full-table recovery above 300 s) as a benchmark artefact. The 300 s timeout is unchanged.
- **Fixed (approved 27 November), tree `45008e3...`:** Execution did not check how far Observer processing lags while it is `Live`. Details: `NSE_OBSERVER_FRESHNESS_GAP_2026-11-27.md`. The daily "runtime not updated" warning is separate; its VM cause is not established (`NSE_RUNTIME_HEARTBEAT_WARNING_INVESTIGATION_2026-11-27.md`).

## 9. Owner approvals required before deployment

1. A GO after section 13 is complete.
2. Commit and merge to `master` (the deployment scripts refuse any other state).
3. Ledger schema script and grants, service registration, local settings, deployment, activation (checklist sections D and E).

VM preflight: checklist section B (runtimes, services and folders, installed build `78d9b8bd`, disk and memory, environment overrides, no open paper positions, database roles), then backups (section C).

## 10. Deployment and rollback commands

Target: paper session of Monday 30 November. Preferred window: Saturday 28 or Sunday 29 November. If it slips to Monday: begin by 07:30 IST, deployment must start before 08:30 IST. The owner checklist is updated for 30 November (its file name keeps the date it was written, 27 November); package paths are confirmed when the packages are built.

**Only after this report says GO.** Review first:

```powershell
cd D:\Projects\NSE-release-20261124
```

```powershell
git status --short
```

Commit on the feature branch and fast-forward `master` (local `master` is at the baseline and one commit ahead of `origin/master`; nothing is pushed):

```powershell
git add -A
```

```powershell
git commit -m "Implement independent Observer and Execution services with checkpoint recovery and paper strategy enhancements"
```

The source tree without `docs` must still be `45008e381918ec091c37b1b9c861ea9a1fa03630`:

```powershell
$env:GIT_INDEX_FILE="$env:TEMP\nse-fingerprint.index"; git read-tree HEAD; git rm -r -q --cached docs; git write-tree; Remove-Item Env:GIT_INDEX_FILE
```

```powershell
git switch master
```

```powershell
git merge --ff-only feature/production-reliability-strategy-enhancement
```

```powershell
git status --porcelain
```

Then checklist A4, B, C, D1-D3, and:

```powershell
.\vm-deploy.ps1 -PreflightOnly
```

```powershell
.\vm-deploy.ps1
```

Activation: checklist section E. Rollback: checklist section F and `NSE_EXECUTION_SERVICE_RUNBOOK.md` section 6; first step:

```powershell
Stop-Service NiftySignalExecution, NiftySignalObserver
```

## 11. Packages

`artifacts\release-candidate-20261130-45008e3\` (ignored by git), built 27 November from `git archive` of tree `45008e381918ec091c37b1b9c861ea9a1fa03630`, build commit field = that tree. Local validation only: the deployment rebuilds from the committed HEAD. Earlier packages are superseded and must not be used.

| Package | Files | Bytes | Zip bytes | Zip SHA-256 | Main DLL SHA-256 |
|---|---:|---:|---:|---|---|
| NiftySignal.Host | 125 | 21,368,842 | 7,118,998 | `1f28c658a2a55cf5f1a64cbf0340d59a45b604bf23223805b5d55bd4d4f86cc4` | `40c160776778950c255a265db65bcc906e09c40eff85b6e9f06e0044a84d7f5f` |
| NiftySignal.Observer | 73 | 13,239,989 | 4,369,467 | `224e704d37546070a81e45dc2abe8227982be8801dc454778354891f364a50f4` | `d2a953371f78dbe82fdfd3653bc937c5f3f68465953f40465c591f9be5512359` |
| NiftySignal.ExecutionService | 87 | 14,161,035 | 4,717,901 | `ea54e2277666792f2aae565146299e1c6fd3547ff5a03849e53467e08094811e` | `f4fbde865e90c5c42d03bc159a97bb5395be7011ccde0de578d52a32427af69e` |
| NiftySignal.Dashboard | 429 | 116,428,093 | 41,887,198 | `9bdc6cb7e685aba06018f08fe3b07496994b1149b71aaabad19030a0d943be74` | `022c5178092e903cf5ceb6ecf53cb1a25f7b06203d3ebd03d60706d628287c9e` |

| Check | Result |
|---|---|
| Release build of the exported tree | 0 errors; 12 warning lines from 6 compiler warnings in 5 files (unused field, parameter or constant) |
| Executable, main DLL, deps, runtimeconfig, `appsettings.json`; build identity in the main DLL | present in all four |
| Zip content equals the published folder; manifest hashes recomputed | equal |
| `appsettings.Local.json`, logs, dumps, key files; non-empty secret-like settings; machine paths in settings | none found (name-pattern scan, not a full secret audit) |
| Runtimes | .NET 10; Dashboard also ASP.NET Core 10 (framework-dependent) |
| Execution package | five strategy files equal to the source tree, four enabled, `TOTAL_VOTE_4_OF_6` disabled; `Execution:Enabled=false` as shipped; `MaxProcessingLagSeconds` 5; freshness gate present in the DLL |
| Dashboard package | `wwwroot` and Playwright win x64 driver; Chromium is installed by the deployment |
| Against the 26 November packages | same file names; settings and strategy files differ in line endings only (one generated Dashboard endpoints file also differs) |
| Vulnerable-package listing | **not re-run**; no project file changed since the 26 November result (no vulnerable package in 45 projects) |
| Starting each package as a Windows service | **not run** |

## 12. Full-table recovery above 300 s: investigation

Sections 12.1-12.6 come from code reading on tree `aa906ab...` and existing reports. Sections 12.7-12.9 report the owner-run measurements of 27 November, 09:17-10:43 IST, with pooling off; section 12.10 (11:24-11:35 IST, pooling on) supersedes their conclusions.

### 12.1 The 90 s and 429 s figures are different workloads

| Factor | Certification, 90.28 s | Parity, "both", 428.52 s |
|---|---|---|
| Tick source | the day copied into a small scratch database (3,953,586 rows, Ids renumbered) | `niftysignal_vm_copy` on 5432 (last Id about 290.7 million) |
| Bar clock | fixed 13,650, 228 bars | opening-volume projection model, base volume 13,650, 380 bars |
| Tokens | weekly chain, 60 option contracts, future, spot | 40 contracts and the future, no spot, no stored anchor |
| Session | production coordinator and worker | built by the test |
| Persistence | cold insert into an empty observer database | cold insert into an empty observer database |
| Top-5 | both | both |
| Checkpoints | on | none |
| Observer database connections | pooled (Npgsql default) | pooling off (test helper `TempAdaptiveDatabase`) |

Both call the same `AdaptiveStateRecoveryService.RecoverAsync`, but data, bar model, universe and connection handling all differ, so the times are not comparable. With Top-5 off the parity test still needed 280.17 s, so Top-5 alone does not explain it. Source table size does not explain it either: the candidate on a small staged copy of the same rows was no faster (section 12.7). Pooling was the leading suspect; section 12.10 confirms it on the full table.

### 12.2 What the 300 s limit does

- `AdaptiveRecoveryOptions.TimeLimitSeconds` (300, Observer and Host settings) cancels the attempt's token (`AdaptiveStateRecoveryService.RecoverAsync`).
- The worker records `RecoveryFailed`, increments the failure count, waits 30 s (`TimeoutRetryBackoffSeconds`) and retries. There is **no maximum number of attempts**; retries stop when the 15:35 market window ends. Other failures retry after 1 s.
- Bars and sidecars already persisted are kept and verified, not overwritten, by the next attempt.
- During replay a checkpoint is saved every 60 s, between batches, if checkpoints are on, no replayed bar has unverified sidecars and no sidecar retry is pending. The next attempt continues from the newest usable one.

### 12.3 Can it cause a loop, unavailability, stale state or unsafe execution?

| Question | Finding |
|---|---|
| Repeated attempts | Yes, by design |
| Permanent unavailability | Possible when attempts make no durable progress: checkpoints off or failing to save, or one unverified sidecar early in the replay (that blocks every later save of the attempt). Each attempt then restarts. A verify pass over already persisted rows took 296.80 s in "both", 3 s under the limit |
| Progress with checkpoints | Synthetic test (`ACancelledAttempt_IsNotLive_AndTheNextOneContinuesFromItsCheckpoint`) and, on the full table, section 12.8: completed on the third attempt |
| Possible code risk (unverified) | After a restore, any exception other than a cancellation exception discards that checkpoint and newer ones. If a timeout ever surfaced as another exception type, progress would be lost |
| Stale market state | No publication happens before recovery completes; the status is `Rebuilding` or `RecoveryFailed` until then |

### 12.4 Checkpoint cases

| Case | Behaviour | Test |
|---|---|---|
| None | full replay of the day | `UnusableCheckpoints_FallBackToAnOlderOne_ThenToAFullReplay` |
| Another build or configuration (every new deployment) | skipped, kept; older tried; else full replay | same, and the retention test |
| Corrupt payload or undeserialisable | removed; older tried | `ADamagedCheckpointIsRemoved_OnesThatMayBeUsableElsewhereAreKept` |
| Bars differ from persisted history, rows missing, read error | passed over, not removed | `ACheckpointWhoseBarsNoLongerMatchPersistence_...`, `ACheckpointWhoseSidecarRowsHaveGone_...` |
| Replay or verification fails after a restore | that one and newer removed | `AReplayThatFailsAfterARestore_...`, `AVerificationThatFailsAfterARestore_...` |

These tests are part of the passing full suite.

### 12.5 Execution during a long recovery

- Entries are blocked whenever the Observer status is not `Live`, its heartbeat is older than 30 s, or its state is unreadable; pending intents are invalidated (`ProtectionPolicy`).
- Open positions are flattened on `RecoveryFailed`, `Degraded`, a heartbeat older than the stale limit plus grace, or an early close.
- **During `Rebuilding` with a moving heartbeat, open positions are not flattened**: `FlattenWhenNotLiveAfterSeconds` is 0 (off) in the shipped settings. New bars do not arrive. By code reading (`ExecutionCoordinator.CycleAsync`, `StrategyRuntime.StepCore`), every cycle without a bar still reads ticks from the trading database and runs pending-exit fills, partial exits, the breach check, optional exit checks, partial targets, protective flatten and terminal liquidation; signal-driven exits (discretionary, opposite signal) need a verified frame and pause, and the bars-held count does not advance. Entry orders are suspended and waiting intents cannot place while entries are blocked. A first timeout changes the status to `RecoveryFailed`, which requests the flatten.
- Covering tests passed: `NotLive_AlwaysBlocksEntries_AndFlattensOnlyForTerminalStates`, `OptionalNotLiveLimit_FlattensARebuildThatNeverEnds`, `StaleHeartbeat_BlocksAndFlattens_OnlyBeyondThirtySeconds`, `MissingOrUnreadableObserver_FailsClosed`.
- Historical publications after recovery: section 6 (no entry when processing lag exceeds 5 s; `ProcessingLagBeyondTheLimit_BlocksEntries_IsJournaled_AndReplayAgrees`). The coordinator-level late-backlog test is still missing.

### 12.6 Exposure on Monday

Checklist D3 turns Top-5 **on** (both) on the VM, although the shipped default is off. A normal start before 09:15 has no history to replay. The slow path applies to a mid-session start with no usable checkpoint, including a mid-session redeployment (new build identity). VM speed on this path is unmeasured.

### 12.7 Same-day full-table comparison, baseline 78d9b8b against the candidate

Owner-run `docs\overnight\Run-RecoveryComparison.ps1`, completed 10:43 IST, every step exit 0, source tree `aa906ab...` unchanged at the end. Day 23 November, universe 40, base volume 13,650, 380 bars, Top-5 both, no checkpoints, same test session in both builds, observer database pooling **off**. Evidence: `D:\perf-scratch\out\compare\`. Executed bench files (SHA-256): `RecoveryComparisonBenchmark.cs` `70C26F23...C5BC16`, `RecoveryTimeoutBenchmark.cs` `903A87F9...8576`.

All runs: core hash `3B38839FDDD97932`, 380 bars, last raw Id 290,736,051, 0 mismatches, 0 failures, 0 unverified sidecar bars, 0 projection-health rows, 15,200 strike and option depth-5 rows.

| First recovery (cold insert) | A run 1 | A run 2 | B | B staged copy |
|---|---|---|---|---|
| Wall (s) | 542.6 | 535.8 | 576.8 | 698.5 |
| CPU (s, client) | 234.4 | 226.3 | 226.0 | 290.3 |
| Allocated (MB) | 140,810 | 140,812 | 148,269 | 148,702 |
| Peak working set (MB) | 1478 | 1437 | 390 | 556 |
| GC pause (s) | 11.8 | 12.8 | 11.9 | 11.9 |
| Stage: sidecars (s) | 414.4 | 418.3 | 400.4 | 443.8 |
| Stage: core / observations (s) | 75.7 / 22.0 | 71.1 / 19.0 | 69.4 / 19.5 | 81.2 / 19.9 |
| Stage: read / engine (s) | 10.2 / 2.4 | 8.0 / 2.2 | 6.2 / 3.0 | 15.9 / 2.8 |
| Time outside the measured stages (s, approx.) | 16 | 16 | 78 | 135 |
| Observer commands (writes) | 6087 (2662) | 6087 (2662) | 6784 (3010) | 6814 (3025) |
| Observer command execution (s) | 21.8 | 21.9 | 23.4 | 24.0 |
| Source commands; execution + read (s) | 445; 19.4 | 445; 17.3 | 1081; 15.6 | 1081; 16.0 |

| Repeat over persisted rows (verify) | A run 1 | A run 2 | B | B staged copy |
|---|---|---|---|---|
| Wall (s) | 303.6 | 297.5 | 319.0 | 357.9 |
| CPU (s, client) | 31.6 | 31.2 | 31.2 | 31.7 |
| Stage: sidecars / core / observations (s) | 171.2 / 80.9 / 20.8 | 162.5 / 83.4 / 21.9 | 159.8 / 88.4 / 20.7 | 156.3 / 77.8 / 18.9 |
| Time outside the measured stages (s, approx.) | 18 | 17 | 41 | 83 |
| Observer commands (writes) | 4947 (2) | 4947 (2) | 5382 (219) | 5424 (240) |
| Observer command execution (s) | 11.8 | 11.8 | 12.7 | 13.2 |

- Candidate against the baseline average: first recovery +7.0 % (576.8 against 539.2 s), verify +6.2 % (319.0 against 300.5 s). The baseline's two runs differ by about 1-2 %. The candidate was run **once** on the full table.
- The extra time falls outside the instrumented stages, while the candidate also issues 697 (insert) and 435 (verify) more observer commands. The cause is **not attributed** to a specific operation.
- Publication (candidate only), called after recovery: 158 ms for 380 bars, 20 ms when nothing was missing. Attestation work is inside the sidecar and core stages and was not timed separately.
- Not measured: server-side CPU. The same candidate code took 428.5 s in the parity run of the previous night and 576.8 s here; that variation is unexplained.
- The staged copy was slower, one run, reason not established. It shows only that source table size is not what makes the full-table run slow.

### 12.8 The 300 s limit with checkpoints, candidate, full table

Same data, `TimeLimitSeconds` 300, checkpoints on, 30 s back-off, observer pooling off. The test calls the recovery service directly, not the worker loop.

| Attempt | Started from | Outcome | Bars persisted at end | Stored checkpoints |
|---|---|---|---|---|
| 1 | nothing | timed out, 300.0 s | 233 | 3 |
| 2 | checkpoint at 201 bars | timed out, 300.0 s | 287 | 6 |
| 3 | checkpoint at 277 bars | **completed**, 258.5 s | 380 | 3 |

- Final core hash `3B38839FDDD97932`, identical to the uninterrupted runs; 0 mismatches, 0 failures.
- About 15.3 minutes from first start to completion (two 30 s back-offs included). The Observer would not be `Live` for that period.
- No endless loop here. Each later attempt first re-processes the bars between its checkpoint and the last persisted bar, so net progress per attempt is smaller than the first.
- Without usable checkpoints: the verify pass alone took 319.0 s, above the limit, so by **inference** (not run under the limit) this configuration would never complete with checkpoints off or failing to save.

### 12.9 Classification before the pooled run (superseded by 12.10)

| Question | Finding |
|---|---|
| Existing baseline limitation? | **Yes** for exceeding 300 s in this workload: baseline 535.8-542.6 s |
| Regression from our changes? | Not the cause of exceeding the limit. The candidate was 6-7 % slower in one run, with 74 % lower peak memory |
| Benchmark artefact? | **Possibly in large part, not yet measured.** In the verify pass about 300 s of wall time holds about 31 s client CPU and under 30 s of command time. The observer database runs with pooling off in this test; connection opens are not counted by the command timings. No pooling setting appears in the repository appsettings (the VM's local connection string was not seen) |
| Performance defect (loop, unavailability, unsafe execution)? | None observed with checkpoints on. Prolonged unavailability remains possible if checkpoints cannot be saved |

### 12.10 Pooled run and final classification

Owner-run `Run-RecoveryComparison.ps1 -Pooled`, completed 11:34:47 IST, every step exit 0, source tree `aa906ab...` unchanged. Same data and settings as 12.7 except observer database connection pooling **on** (the Npgsql default). One run per row. All: 380 bars, core hash `3B38839FDDD97932`, 0 mismatches, 0 failures, 0 unverified sidecar bars.

| | A pooled | B pooled | A unpooled (average) | B unpooled |
|---|---|---|---|---|
| First recovery (s) | 257.9 | **193.2** | 539.2 | 576.8 |
| Verify over persisted rows (s) | 35.7 | **20.3** | 300.5 | 319.0 |
| Peak working set (MB) | 1511 | 398 | 1437-1478 | 390 |
| Client CPU, first recovery (s) | 241 | 181 | 226-234 | 226 |
| Stage sidecars / core, first recovery (s) | 192.3 / 35.5 | 131.1 / 25.3 | 416 / 73 | 400.4 / 69.4 |
| Observer connection opens, first recovery | 5707 in 0.17 s | 6014 in 0.20 s | not counted | not counted |

Limit test, candidate, 300 s, checkpoints on, pooled: **completed on the first attempt in 97.5 s**, 380 bars, same hash, 0 mismatches. It ran faster than the 193.2 s comparison run of the same code; the difference is not explained (the comparison harness carries more instrumentation, not isolated as the cause).

| Question | Finding |
|---|---|
| Benchmark artefact? | **Yes.** Turning pooling on, with nothing else changed, removed 281 s (A) and 384 s (B) from the first recovery |
| Baseline limitation? | Not in this configuration: 257.9 s, under the limit with 42 s to spare |
| Regression? | None with pooling: candidate 25 % faster on first recovery, 43 % faster on verify, 74 % less peak memory (one run each) |
| Performance defect? | None observed |

Limits: developer machine, one day, VM speed unmeasured. The result depends on pooling being on: the repository settings do not disable it for recovery (observer ownership uses one unpooled connection by design); the VM's local connection string must be checked not to set `Pooling=false`.

**Recommended:** close section 12 as a benchmark artefact, keep the 300 s limit unchanged, and add the connection-string check to the VM preflight. Awaiting owner approval.

**Open blockers:** owner closure of section 12; freshness fix decision (section 8); packages not built; commit and merge not approved; VM preflight and VM recovery speed unmeasured; no recorded-session in-Host test (section 7) and no coordinator-level late-backlog test (section 6).

**Owner decision, 27 November:** section 12 is closed as a benchmark artefact; the 300 s timeout is retained; the VM preflight checks that the Observer connection string does not disable pooling (checklist D3a); actual VM recovery performance remains **unmeasured**; no further general recovery optimisation. The "open blockers" line above is superseded by section 13.

## 13. Observer freshness entry gate: new candidate

### 13.1 Source identity

Tree `45008e381918ec091c37b1b9c861ea9a1fa03630` (without `docs`), HEAD still `78d9b8bd`, nothing committed. Against `aa906ab...`, six files:

| File | Change |
|---|---|
| `NiftySignal.ExecutionService/ProtectionPolicy.cs` | processed position and last completed bar in `ObserverState`; the processing entry block |
| `NiftySignal.ExecutionService/ExecutionCoordinator.cs` | tracks ticks not yet processed and unevaluated bars; one warning per minute at most |
| `NiftySignal.ExecutionService/DbExecutionFeed.cs` | reads `LastProcessedSourceAvailableAtUtc`, `LastCompletedBarSeq` and the session future's ticks |
| `NiftySignal.ExecutionService/IExecutionFeed.cs` | progress-only tick flag |
| `NiftySignal.Tests/PaperEngineV2/ExecutionServiceTests.cs` | helpers state a current Observer |
| `NiftySignal.Tests/PaperEngineV2/ObserverFreshnessTests.cs` | new regression and database tests |

No new service, table, migration, setting or strategy change. Observer, Host, Dashboard, persistence and recovery code are unchanged.

### 13.2 Validation on the new tree (developer machine, disposable loopback PostgreSQL on port 54341)

| Check | Result |
|---|---|
| Full suite, `dotnet test NiftySignal.slnx -c Release` | **3,314 passed, 0 failed, 2 skipped** (previous tree 3,267; 47 new tests) |
| Same 47 tests with only the gate call removed (scratch copy) | 31 fail, 16 pass (quiet-market, current-state and unchanged-behaviour cases): the tests depend on the fix |
| Skipped `ExecutionJournalRecoveryBenchmark`, run on its own | passed; 600 bars 1.10 s, 3,000 bars 2.44 s (previous 1.09 / 2.41 s), peak live managed 19.5 / 26.9 MiB |
| Skipped `HostReplayTests` | still not exercised (as before) |

Covered through the coordinator with a real ledger database and the **four shipped strategy files unmodified** (frames carry synthetic values of the metrics their rules read): backlog on new signals, waiting 5-bar-average intents, reversals, unfilled orders and retries; opposite signal on a catch-up bar; quiet market; future-only backlog; missing and invalid position; completed bar not evaluated, including after a restart; partial target and 15:08 liquidation during a block; restart reconciliation. One test uses the real `DbExecutionFeed` with a real Observer database (source ticks in memory).

### 13.3 What earlier evidence still applies

| Evidence (tree `aa906ab...` or earlier) | Applies to `45008e3...`? |
|---|---|
| Recovery certifications 14:30 and 09:45 (section 4) | **Repeated on `45008e3...`, both MET** (section 13.6) |
| Top-5 parity, A/B/C benchmarks, live-versus-replay, recovery comparison (sections 5, 12) | Applicable: they exercise Observer, Host and persistence code, which is unchanged. Not repeated |
| Journal rebuild benchmark (section 7) | Repeated above (ExecutionService changed) |
| Publication fix evidence (section 6, 8) | Applicable |
| Packages of any earlier tree | Invalid |

### 13.4 Behaviour and limits

- Entries are blocked when the processed position is missing, later than the clock or older than the ticks Execution has read; when an option or future tick has waited more than 5 s; or when a completed bar stays unevaluated more than 5 s (no allowance after a start or rebuild until caught up). Reasons start "observer processing" and show in the Dashboard's "Entries blocked".
- A waiting 5-bar-average intent survives; its outstanding order is cancelled. An immediate strategy's waiting reversal or unfilled order is discarded. Exits are unaffected.
- **Not measured: the false-block rate in real time.** By design the Observer is about 3.3 s behind at most (2 s stable lag, row written each second), plus the time to persist a completed bar, against the 5 s limit. A slower VM would refuse entries, including immediate entries at bar completion. Checklist E7 watches this. The limit is not to be raised without a decision.
- Execution just restarted, after a silent market longer than 60 s, with the Observer stalled: older unprocessed ticks are outside its read window; the "predates" rule blocks until the Observer processes something at or after that window.
- The gate is in the execution service only. The in-Host paper paths stay disabled (checklist D3).
- Real-data certification does not run the execution service.

### 13.5 Certification attempt 1 (27 November, 12:16): did not run to a verdict

`D-certify-1430` stopped after 128 s with PostgreSQL error `57014: canceling statement due to statement timeout`. No certification report was produced; this is neither a pass nor a failed criterion. Evidence: `D:\perf-scratch\out\freshness\D-certify-1430.log`.

- Where: `tools\AdaptiveLivePerformance\CheckpointCertification.cs:117`, the first read of the day's lowest tick id from the source copy, before any recovery code runs. The tool is identical in `aa906ab...`, where both certifications completed.
- Cause: a server-side `statement_timeout` on the source connection (the tool's own command timeout is 900 s and the run stopped after 128 s in total). Why the query was slow this time is inferred only: a lowest-id query filtered by token and time can walk the id index from the oldest row, so its duration depends on the cache. The configured value, its origin and the query plan have not been checked.
- The owner approved a 10-minute session timeout in the script. **It was not applied:** `tools\AdaptiveLivePerformance\Program.cs:63` replaces the connection string's `Options` with `-c default_transaction_read_only=on`, so a script setting has no effect. The trial script edit was reverted. A timeout would need a one-line tool change, which gives a new source tree. Source sessions are already read-only.

### 13.6 Certification attempt 2 (owner-run, unchanged script and source): both MET

Tree `45008e381918ec091c37b1b9c861ea9a1fa03630`, day 2026-11-23, downtime 60 s, limit 300 s. Reports `D:\perf-scratch\out\certification-D-certify-{1430,0945}-20261123.json`; marker `D:\perf-scratch\out\freshness\CERTIFICATION-COMPLETED.txt` (13:03:43, the script rechecked the tree at the end). Developer machine.

| | Restart 14:30 | Restart 09:45 |
|---|---:|---:|
| Verdict, failures | CRITERIA MET, none | CRITERIA MET, none |
| Run duration | 1,155 s | 1,203 s |
| Full-day replay (228 bars) | 89.19 s | 90.11 s |
| Restart from checkpoint, to Live | 1.10 s | 0.79 s |
| Ticks replayed / visible | 8,832 / 3,329,352 | 10,300 / 370,493 |
| Restart without checkpoints | 21.75 s | 3.11 s |
| Versus full replay | 16 tables equal | 16 tables equal |
| Versus uninterrupted run | 17 tables equal | 17 tables equal |
| End tick accounting | equal | equal |

Inputs and all parity table hashes are identical to the `aa906ab...` certifications (`C2-certify-*`). These measure the checkpoint recovery path with Top-5, not full-table recovery (section 12) and not the execution service. Attempt 1's timeout did not recur; its explanation stays inferred.

### 13.7 Remaining steps

1. Commit approved and made with the tree without `docs` at `45008e381918ec091c37b1b9c861ea9a1fa03630`. Merge and push need separate approval.
   - Left out of the commit as temporary benchmark harnesses: `docs/overnight/compare/RecoveryComparisonBenchmark.cs` and `RecoveryTimeoutBenchmark.cs`. They remain in the release folder; `Run-RecoveryComparison.ps1` needs them and section 12 records their SHA-256.
   - `git diff --check` reports one finding type: a blank line at the end of three `adaptive-*.idempotent.sql` files. Not changed, because it would change the certified tree.
2. Owner VM preflight, backups and deployment (checklist), preferably 28 or 29 November.
3. First session: watch the freshness blocks (checklist E7) and the first recovery duration (D3a).
