# Certification failure: 206 publications for 228 bars (23 November, restart 14:30)

Investigated 26 November. Run: `D:\perf-scratch\out\certification-C-certify-1430-20261123.json`, tree `25e7f964...`, 21:16-21:51 IST. Verdict `NOT CERTIFIED`, one failure: `publications: 206 rows for 228 bars`.

## Cause

`AdaptiveObserverWorker` published the bars rebuilt by a start or recovery in its service loop (`ExecuteAsync`), **after** `TryStartOrRecoverAsync` returned. The certification tool calls `TryStartOrRecoverAsync` and `PollLiveAsync` directly, so the recovered bars were never published in any of its runs. Only live-polled bars were.

| Evidence | Value |
|---|---|
| Uninterrupted run (no restart, no checkpoint) | also 206 publications; table equal to the checkpoint run |
| Every other table, both comparisons | equal (228 bars) |
| Highest publication at the 14:30 restart | 196 = bars completed by then |
| Full replay (publications not compared) | publishes nothing by the same mechanism |
| The 22 missing bars | **inferred**: those completed before the live start at 09:30:05. To be confirmed by `D:\perf-scratch\unpublished-20261123.sql` |

| Candidate explanation | Verdict |
|---|---|
| Warm-up exclusion | No. Publication has no warm-up rule; warm-up is applied by the strategy |
| Initial recovery exclusion / missing backfill | **Yes, in the tool's path.** The service loop does backfill |
| Unverified historical bars | No. Unverified bars are published with `Verified=false` |
| Checkpoint restore defect | No. Same count without any restore |
| Transaction or ordering defect | **A second, real production defect** (below) |
| Wrong certification assertion | No. One publication per completed bar is the contract; the assertion is unchanged |

## Production defect found on the same path

If the recovered-bar publication failed (for example a transient observer database error), the failure was logged, never retried, and live bars were published after it. The execution service reads only `BarSeq` greater than the last bar it handled, so the earlier bars were skipped for good, even if a later recovery published them. A failed live publication was also retried only when another bar completed.

## Can an eligible opposite signal be missed?

**Yes, for a bar with no publication, or one published behind a later one.** Execution never evaluates it, so its exit signal is lost. The next frame is a gap: the previous-frame link is dropped (conditions needing it are Unknown on that bar) and entries stay blocked until 3 clean frames. Exit signals are evaluated on every later verified frame whether or not entries are blocked; stops, protective exits and the terminal liquidation do not depend on bars. A bar published **late but in order** is evaluated; it only blocks entries (processing lag). In the normal path (publication succeeding) no bar was lost.

## Change

| File | Change |
|---|---|
| `NiftySignal.ObserverRuntime/AdaptiveObserverWorker.cs` | Recovered bars are published inside `TryStartOrRecoverAsync`. On failure: flagged, retried by every poll and before any later bar; nothing later is published first; no checkpoint while outstanding. Failed live publications are also retried by every poll |
| `NiftySignal.AdaptiveObserverData/AdaptiveBarPublisher.cs` | Backfill stops before a bar still awaiting its own live publication, which keeps its own verified verdict |
| `NiftySignal.Tests/AdaptiveObserver/AdaptiveRecoveredBarPublicationTests.cs` | 3 tests on a throw-away PostgreSQL with injected publication failures. All 3 fail without the fix; the ordering test then shows bars 13-17 published while the recovered bars were missing |

Strategy semantics, warm-up rules, verification verdicts, schema and certification criteria are unchanged.

## Confirm the inference (read-only, before any new certification)

A new certification **drops and recreates** the `perf_cert_*` databases. Run first:

```powershell
foreach ($d in "perf_cert_live","perf_cert_checkpoint","perf_cert_full") { & "C:\Program Files\PostgreSQL\18\bin\psql.exe" -h 127.0.0.1 -p 54340 -U postgres -d $d -X -v ON_ERROR_STOP=1 -f D:\perf-scratch\unpublished-20261123.sql }
```

Expected: `perf_cert_live` and `perf_cert_checkpoint`: 228 completed, 206 published, 22 unpublished, first 1, last 22, last unpublished ended at or before 09:30:05, 0 unverified. `perf_cert_full`: 0 published. Any other result means this explanation is incomplete.

## Verification and identity

| Check | Result |
|---|---|
| Full Release test suite (throw-away PostgreSQL on 127.0.0.1:54341, stopped afterwards) | 3,267 passed, 0 failed, 2 skipped, 3,269 total |
| `AdaptiveLivePerformance` Release rebuild | 0 errors, 2 warnings |
| New source identity (without `docs`) | `aa906ab302ce7e770d03c1c4002aa40e5a15808e` (was `25e7f9643f206881b7fc84145e26c198f555c9a7`); base `78d9b8b`, uncommitted |
| Real-data certification of the new source | **NOT RUN** |

## To repeat

1. Rebuild the tool (the run sheet uses `--no-build`), set `ADAPTIVE_PERF_COMMIT` to the new tree.
2. Certify 14:30 again, then certify 09:45 (the first run took about 34 minutes).
3. Benchmarks B and C against the new tree; baseline A is unaffected.
4. The packages in `artifacts\release-candidate-20261127` predate this change: superseded. Build from the new source at deployment.

No earlier real-data result carries over as a certification: the recorded timings and parity belong to the old tree.
