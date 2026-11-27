# "runtime not updated for Ns - last known state": read-only investigation

27 November 2026. Code reading only. No source change, no database or VM access, no new test run. Candidate source tree `aa906ab302ce7e770d03c1c4002aa40e5a15808e` is unchanged. Baseline = `78d9b8bd` (local `master`), read from the scratch worktree `D:\perf-scratch\nse-78d9b8b`. That the VM runs this build is assumed, not verified.

## Verdict

**Expected to be reduced, not guaranteed to be resolved.** The release removes the clearest cause in the code of a long frozen heartbeat (recovery that does not write it). The cause of the daily occurrences on the VM is **not established**: that needs the VM logs.

## 1. Where the warning comes from

- `NiftySignal.Dashboard/Services/IncompleteBarView.cs:58` builds the text. State is `Stale` when `now - heartbeat > 10 s` (`StaleAfter`, line 31) and the status is not `Closed` (lines 50-51).
- Timestamp: column `LastHeartbeatUtc` of the runtime row (`adaptive_observer_runtime`, entity `AdaptiveObserverRuntimeRow`).
- Read path: `AdaptiveBarProgressService` polls once a second while a page is open; `LiveQuotePanel.razor:221` re-renders every second.
- The warning text, service and panel are **unchanged** between baseline and candidate (`git diff 78d9b8b` empty for them).
- Writer: only the Observer (inside Host in the baseline; its own service in the candidate).

## 2. Who writes the heartbeat, and how often

| Situation | Baseline | Candidate |
|---|---|---|
| Live polling, bars or no bars | end of a poll, at most once per second (poll every 250 ms) | same cadence |
| Value written | the instant the poll **started** (`AdaptiveObserverWorker.cs:98,407`) | the instant of the write (`AdaptiveObserverWorker.cs:378`) |
| Long catch-up inside one poll | nothing until the poll ends | about once per second between groups (lines 468-472) |
| Completed bar | with the bar insert | with the bar insert |
| Recovery start | status `Rebuilding`, heartbeat **not** written (`AdaptiveStateRecoveryService.cs:38-42`) | heartbeat written (lines 81-92) |
| Recovery replay | only when a bar is **inserted**; verifying a persisted bar writes nothing (`AdaptiveObserverPersistence.cs:25-31` against 49-57) | at least once per second from the replay loop (`AdaptiveRecovery.cs:77-92`) |
| Recovery failure | row can stay `Rebuilding` with an old heartbeat while retries repeat (no live state to mark) | `RecoveryFailed` written; 30 s back-off after a timeout |
| Outside 09:15-15:35 | none (session closed) | none |

It does not depend on a volume bar completing: the time-based flush (candidate lines 370-383, baseline 401-413) runs after every poll, including polls that read no rows. So low activity alone should not raise the warning. It does depend on the poll loop returning: any database operation, bar persistence, sidecar write, Dashboard push or checkpoint that blocks the loop also blocks the heartbeat. In the baseline the paper bridges are called inside the loop; the V2 bridge only enqueues, so I found no evidence that it blocks.

## 3. Likely baseline causes of an age like 129 s

In order of how directly the code supports them. None is confirmed from VM evidence.

1. **Recovery after a restart or a live-processing exception.** Any exception sets `Degraded`, drops the state and replays the day (`AdaptiveObserverWorker.cs:151-157`). Verifying persisted bars writes no heartbeat, so its age grows for the whole replay. Today's full-table benchmark needed minutes on this development machine.
2. **A stalled poll** (slow or blocked database or push call). The age also starts from the poll's start.
3. **Observer or Host stopped**, which the warning reports correctly.
4. **Dashboard display only (both versions):** `AdaptiveBarProgressService.Current` keeps the value from when the last page unsubscribed. A newly opened or prerendered page shows it until the first refresh (`LiveQuotePanel.razor:212-218`), so an old age can flash for a moment. If the number vanishes within a few seconds of opening, this is it; if it climbs while you watch, the writer is not writing.

## 4. What the release changes, and what remains

Addressed: heartbeat during recovery; write-time stamping; heartbeat during live catch-up; Observer in its own process, so Host load or restarts no longer force an Observer replay; checkpoints, so restarts replay less; a visible `RecoveryFailed` state and Observer health alerts (`ObserverHealth.cs`).

Can still raise the warning in the candidate:

- Observer start-up before recovery (ownership, history bootstrap, session lookup): no heartbeat written.
- The 30 s back-off after a timed-out attempt (threshold is 10 s).
- One long blocking operation in the live loop (there is deliberately no timer heartbeat), including the single final verification call of a recovery.
- Observer stopped, or ownership not held.
- The cached Dashboard value (item 3.4).
- Whatever makes the Observer fail or restart on the VM: the release does not identify or fix it.

## 5. Stale metrics and Execution

Heartbeat freshness proves the loop moves, **not that the metrics are current**.

- Low activity: `Live` with a fresh heartbeat while no bar completes is correct; the panel shows the last processed tick time separately.
- **Gap (candidate):** during a live backlog the status stays `Live` and the heartbeat stays fresh while processing lags. The Dashboard does not compare `LastProcessedSourceAvailableAtUtc` with the clock, and Execution reads only status and heartbeat (`DbExecutionFeed.cs:40`).
- Execution's other guards: a late bar (processing lag above 5 s) blocks entries; market data older than 10 s blocks entries, but that is Execution's own tick read, not the Observer's progress. **Between bars**, entry permission comes from the last bar processed (`StrategyRuntime.cs:185-187`), so a waiting 5-bar-average intent can place while the Observer lags. **Reproduced later the same day**; details and a proposed fix in `NSE_OBSERVER_FRESHNESS_GAP_2026-11-27.md`.
- Recovery: `Rebuilding` or `RecoveryFailed` blocks entries and invalidates intents whatever the heartbeat age (`ProtectionPolicy.cs:39-50`); `RecoveryFailed`, `Degraded` or a heartbeat older than 30 s requests a flatten. During `Rebuilding` with a moving heartbeat, positions are not flattened by default.
- **Bounded window:** if the Observer dies, the row still says `Live` until the heartbeat is 30 s old or a new recovery writes `Rebuilding`. For up to 30 s Execution treats it as healthy.

So item 9 is met for recovery, and **not fully** for prolonged live processing.

## 6. Smallest next steps (none taken)

1. Establish the VM cause from logs (owner-run, read-only, on the VM). Match the times of the warning with these lines:

```powershell
$p=(Get-CimInstance Win32_Service -Filter "Name='NiftySignalHost'").PathName; $exe=if($p.StartsWith('"')){$p.Split('"')[1]}else{$p.Split(' ')[0]}; Get-ChildItem "$(Split-Path $exe)\logs\niftysignal-*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 5 | Select-String -SimpleMatch 'Adaptive observer live processing failed','Adaptive observer recovery complete','Could not persist adaptive observer','Application started','Application is shutting down' | ForEach-Object { "$($_.Filename): $($_.Line)" }
```

2. Decide on the processing-lag gap (section 5). A possible fix is to make Execution (and the Dashboard badge) treat an old `LastProcessedSourceAvailableAtUtc` during active market data as not current. That is a source change: it invalidates the certification and the benchmarks, and needs approval. Not recommended before the log evidence and an explicit decision.
