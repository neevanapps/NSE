# Adaptive live performance validation - 2026-10-08

Branch `adaptive-08oct-s4c`; baseline = `0c5817c`, after = the hardening commit that contains this file. Nothing here was run against production, Telegram or the VM.

## Method and environment

- **Harness:** `tools/AdaptiveLivePerformance` (repeatable: see its header comment for the environment variables; connection strings are never printed). It drives the REAL production code - `AdaptiveObserverWorker.PollLiveAsync` / `TryStartOrRecoverAsync` / `CloseCurrentSessionAsync` and `AdaptiveStateRecoveryService` - against an **isolated PostgreSQL 18 cluster** (separate host:port; the tool refuses to run otherwise). The historical VM-copy tick database is opened read-only (`default_transaction_read_only=on`) and only read. Telegram is disabled in the harness and the Dashboard push client is never connected (its call returns immediately when not connected, so the SignalR send itself is not measured).
- **Live simulation:** each session's selected-token ticks (NIFTY future + weekly option chain, 41 tokens, ~2.3 M raw rows, ~1.2 M after normalization) are staged into the isolated database and made visible strictly in source-Id order by ReceivedAt, exactly as live ingestion would. Each 250 ms cycle the real `PollLiveAsync` runs (real SQL, real engine, real persistence, real commentary); the simulated clock then advances by the MEASURED cycle cost plus the 250 ms delay, so poll-cadence drift is included. The whole session 09:30 -> 15:35 is replayed (about 87,300 polls) followed by the production session-close path.
- **Stress:** the same code with actual tick order and values, arrival accelerated by a time-compression factor chosen so a sustained 60-real-second window carries at least 2x the maximum observed 1-second rate; the 2-second ordering holdback is scaled so it stays 2 REAL seconds.
- **SQL counting:** an EF `DbCommandInterceptor` in the harness only (nothing is logged or counted in production). **Stage timing:** `ActivitySource "NiftySignal.AdaptiveLive"` hooks that cost one null check without a listener.
- **Machine:** Intel Core i5-14600K (14 cores / 20 threads), 31.8 GB RAM, NVMe SSD, Windows 11, .NET 10.0.401, workstation concurrent GC. The harness process itself holds one session's ~2.3 M staged rows (~350-400 MB managed heap) in addition to what the observer uses, so absolute working-set numbers overstate the Host; compare "heap before / peak / after" for recovery increments.
- **Limitations:** the isolated observer database starts empty for every run (no prior sessions), so the Weak2 strong threshold is null and no Weak2 observations occur (their H5 work is therefore not in these timings; the code path is unchanged). Idle (zero-tick) polls are rare in this dataset (about 0.4 %), so "idle" statistics are thin. Wall clocks are Windows timers on a developer machine, not the VM.

## Headline results

| Requirement | Result |
|---|---|
| Does the observer keep up at normal load? | Yes. PollLive p50 0.55 ms, p99 1.5 ms, max 48.6 ms against a 250 ms cadence; the cycle period p99 is 251.5 ms; 0 to 0.001 % of 87,300 cycles exceeded 275 ms. Pending ordering buffer peaks at 171-197 ticks (about the 2-second holdback). |
| Historical maximum burst | One 250 ms cycle carried 1,790 ticks (2026-10-01); the poll still finished in under 50 ms; the pending buffer peaked at 1,193 and drained normally. |
| Sustained 2x the maximum observed 1-second rate | 3,031 ticks/s input (target >= 2,268) for 60 real seconds: poll p99 21 ms, max 30 ms, cycle period max 280 ms, pending buffer peak 4,799, drained to 0 in 2.27 s after ingestion stopped, no data lost, no degraded status, results identical to a deterministic unthrottled replay. Fitted cost 1.2 ms/poll + 0.0046 ms/tick => capacity about 215,000 ticks/s (about 70x the stress rate, about 4,000x the mean rate). |
| Runtime-row write rate | 4.01 writes/s (87.7-88.5 k per session) -> 1.02-1.06 writes/s (22.4-23.2 k); observer SQL commands per second 8.1 -> 2.1-2.3; the idle/ticks-only poll issues 0 observer SQL in most cycles (was 2 every cycle). |
| Dashboard header refresh (every 500 ms per client) | 3 queries -> 1 indexed runtime read (0.17-0.23 ms p50); no session lookup or ten-bar readiness read unless a completed bar changed. |
| Completed-bar pipeline | p50 7.5 ms, p99 10-15 ms, max 19-22 ms (about 22-24 SQL commands, 112-288 bars/session). Far below the 250 ms cadence, so no batching architecture was added. |
| Market outputs unchanged | Every persisted market table (future bars, rolling states, option bands, residuals, futures/options sidecars, commentary events, Weak2 observations) hashes identically between `0c5817c` and the final code for all three sessions, and live output equals restart-replay output. |
| Recovery | Full-day recovery 7.6-8.5 s when verifying an existing session and 12.9-15.5 s when creating one from scratch; the source read (EF projection of ~2.3 M rows) is 40-75 % of it. Memory is the one number worth watching: see "Remaining bottleneck". |

## Decisions (measured, not assumed)

- **B5 poll cadence:** cycle processing is 0.55 ms median / 1.5 ms p99, so the real period is 250.6 ms; a fixed-rate scheduler would change nothing. Left unchanged.
- **B6 per-bar round trips:** about 177 bars/day, pipeline p99 under 16 ms. No batching added.
- **B7 recovery memory:** I tried streaming the source read into the final list, dropping the raw list before the engine replay and replacing the `GroupBy` lookup with an index scan. Measured: streaming made the peak WORSE (working set 2.4 GB vs 1.7 GB) and the other two changed nothing measurable, so all three were reverted. Recovery code differs from `0c5817c` only by telemetry hooks and unknown-sidecar tracking.
- **B8 indexes:** none added. Live read 0.12-0.23 ms; pinned-quote probes 0.07-0.29 ms. The one slow plan is the ReceivedAt probe on a SPARSE option token with a 30-minute floor (36.7 ms, scanning the non-token-prefixed ReceivedAt index); it only occurs for a delayed-tick check on a pinned capture, which resolves five quotes per capture (4.5 ms p50 for the whole call). A `(Token, ReceivedAt)` index on the multi-million-row tick table is not justified by this.
- **Option-book pruning:** not added (about 540-560 k retained states, ~30 MB).

## Remaining bottleneck

Full-day recovery peaks at about 1.1-1.3 GB of additional managed heap (1.5-1.6 GB total in the harness) because the whole session's raw rows, the normalized list and the sorted list coexist; the dominant stage is the PostgreSQL read/materialization. It is a one-off restart cost, not a per-tick cost, and a mid-session restart is proportionally smaller. If the VM has little memory, the next step would be a chunked read that preserves exact Id order for the normalizer and a global AvailableAt order for the engine; that needs its own equivalence proof and was not attempted here.

## Measurements

### Input tick-rate distribution (selected tokens: NIFTY future + weekly option chain, 18 sessions, 09:15-15:30 IST, clean ticks by AvailableAt)

| Session | clean ticks | mean/s | p50/s | p90/s | p95/s | p99/s | max 1 s | max 250 ms (raw, ReceivedAt) | max one availability group |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 2026-09-04 | 1,093,219 | 48.59 | 52 | 62 | 66 | 84 | 1217 | 2346 | 43 |
| 2026-09-08 | 1,101,030 | 48.93 | 50 | 59 | 64 | 80 | 135 | 212 | 34 |
| 2026-09-09 | 1,159,869 | 51.55 | 51 | 62 | 67 | 85 | 212 | 268 | 78 |
| 2026-09-10 | 1,125,506 | 50.02 | 49 | 60 | 64 | 83 | 226 | 216 | 13 |
| 2026-09-11 | 1,190,996 | 52.93 | 52 | 62 | 66 | 85 | 739 | 1165 | 19 |
| 2026-09-15 | 1,183,626 | 52.61 | 52 | 62 | 67 | 81 | 123 | 134 | 14 |
| 2026-09-16 | 1,147,608 | 51 | 51 | 62 | 65 | 78 | 126 | 120 | 15 |
| 2026-09-17 | 1,155,242 | 51.34 | 51 | 61 | 65 | 79 | 136 | 141 | 15 |
| 2026-09-18 | 1,140,785 | 50.7 | 50 | 61 | 65 | 77 | 130 | 144 | 21 |
| 2026-09-21 | 1,114,277 | 49.52 | 49 | 59 | 63 | 75 | 123 | 152 | 18 |
| 2026-09-22 | 1,140,609 | 50.69 | 50 | 60 | 63 | 74 | 197 | 318 | 21 |
| 2026-09-23 | 1,112,657 | 49.45 | 49 | 60 | 64 | 73 | 143 | 136 | 25 |
| 2026-09-24 | 1,117,649 | 49.67 | 49 | 58 | 62 | 71 | 389 | 728 | 24 |
| 2026-09-25 | 1,171,489 | 52.07 | 52 | 63 | 66 | 75 | 204 | 396 | 19 |
| 2026-09-28 | 1,154,193 | 51.3 | 51 | 60 | 63 | 71 | 987 | 722 | 18 |
| 2026-09-29 | 1,198,885 | 53.28 | 53 | 63 | 66 | 74 | 157 | 195 | 14 |
| 2026-09-30 | 1,193,999 | 53.07 | 52 | 64 | 68 | 89 | 391 | 743 | 55 |
| 2026-10-01 | 1,213,071 | 53.91 | 53 | 64 | 68 | 89 | 1134 | 1800 | 50 |

Across sessions: mean 51.15 ticks/s; maximum 1-second burst **1217**; maximum 250-ms bucket **2346** raw rows; largest availability group **78** ticks.

### Live loop - BASELINE (0c5817c): PollLive milliseconds

| Session | cycles | bars | poll p50 | p95 | p99 | max | period p99 | period max | cycles >275 ms | obs SQL idle/ticks-only p50 (mean) | obs SQL / bar poll | writes/s | commands/s | max pending | WS peak MB | heap peak MB | gen0/1/2 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|
| 2026-09-21 | 87,215 | 112 | 0.9886 | 1.3798 | 1.6569 | 25.2855 | 251.6569 | 275.2855 | 0.001% | 2 (2) | 23.4821 | 4.008 | 8.077 | 170 | 742.1 | 484 | 1722/12/1 |
| 2026-09-23 | 87,217 | 118 | 0.9824 | 1.3744 | 1.6383 | 39.6435 | 251.6383 | 289.6435 | 0.002% | 2 (2) | 24.0169 | 4.01 | 8.086 | 196 | 686.4 | 484.4 | 1721/12/1 |
| 2026-10-01 | 87,211 | 260 | 0.9869 | 1.3741 | 1.6624 | 48.2963 | 251.6624 | 298.2963 | 0.002% | 2 (2) | 24.4692 | 4.043 | 8.233 | 1193 | 729 | 526.1 | 1753/15/1 |

### Live loop - AFTER (this commit): PollLive milliseconds

| Session | cycles | bars | poll p50 | p95 | p99 | max | period p99 | period max | cycles >275 ms | obs SQL idle/ticks-only p50 (mean) | obs SQL / bar poll | writes/s | commands/s | max pending | WS peak MB | heap peak MB | gen0/1/2 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|
| 2026-09-21 | 87,336 | 112 | 0.5541 | 1.1438 | 1.5096 | 18.8476 | 251.5096 | 268.8476 | 0% | 0 (0.4994) | 23.4821 | 1.024 | 2.109 | 171 | 1002.9 | 776.7 | 1465/9/1 |
| 2026-09-23 | 87,338 | 118 | 0.5537 | 1.134 | 1.4674 | 23.1853 | 251.4674 | 273.1853 | 0% | 0 (0.4991) | 24 | 1.025 | 2.117 | 197 | 996.4 | 668.4 | 1462/9/0 |
| 2026-10-01 | 87,331 | 260 | 0.5582 | 1.1626 | 1.5362 | 48.5818 | 251.5362 | 298.5818 | 0.001% | 0 (0.4974) | 24.4385 | 1.061 | 2.269 | 1193 | 878.3 | 677.4 | 1499/11/0 |

### Operational runtime-row writes per live session (87,2xx polls)

| Session | baseline writes | after writes | baseline writes/s | after writes/s | baseline SQL commands | after SQL commands |
|---|---:|---:|---:|---:|---:|---:|
| 2026-09-21 | 87,748 | 22,412 | 4.008 | 1.024 | 176,842 | 46,170 |
| 2026-09-23 | 87,791 | 22,448 | 4.01 | 1.025 | 177,038 | 46,350 |
| 2026-10-01 | 88,516 | 23,228 | 4.043 | 1.061 | 180,270 | 49,686 |

### Stage timings per call (AFTER, milliseconds; SQL commands per call in brackets)

| Stage | 2026-09-21 p50 / p99 / max | 2026-10-01 p50 / p99 / max |
|---|---|---|
| Source read (ReadRawAfterIdAsync) | 0.459 / 0.9094 / 8.4569 [0] | 0.4614 / 0.8951 / 3.1837 [0] |
| Normalize appended raw ticks | 0.0024 / 0.0052 / 11.4588 [0] | 0.0024 / 0.005 / 0.818 [0] |
| Engine, one availability group | 0.0007 / 0.0029 / 1.2735 [0] | 0.0006 / 0.003 / 0.7381 [0] |
| Completed bar: core PersistOrVerify | 2.0215 / 3.3271 / 6.5557 [3] | 2.0263 / 3.5681 / 7.548 [3] |
| Completed bar: futures + options sidecars | 1.3891 / 2.8397 / 3.2645 [4] | 1.3651 / 2.225 / 3.6554 [4] |
| Completed bar: commentary (frame load + evaluate + persist) | 3.5924 / 4.8047 / 10.0685 [14] | 3.6598 / 5.0382 / 8.6754 [14] |
| Completed bar: Dashboard push call (no hub connected here) | 0.0013 / 0.0078 / 0.4688 [0] | 0.0012 / 0.0022 / 0.0027 [0] |
| Completed bar: Weak2 observations | 0.2591 / 0.3892 / 0.4202 [1] | 0.2625 / 0.4486 / 0.7444 [1] |
| Runtime row write (throttled) | 0.4839 / 0.9557 / 16.1964 [2] | 0.4824 / 0.9504 / 16.3941 [2] |
| Completed-bar pipeline total | 7.5036 / 13.6374 / 17.3914 [22] | 7.5435 / 9.4949 / 20.3609 [22] |

### Commentary stage by outcome (AFTER)

| Session | outcome | bars | ms p50 / p99 / max | SQL commands p50 / max |
|---|---|---:|---|---|
| 2026-09-21 | noMaterialEvent | 29 | 2.9739 / 3.9336 / 4.0854 | 12 / 12 |
| 2026-09-21 | lifecycleEvent | 83 | 3.7594 / 5.7633 / 10.0685 | 14 / 14 |
| 2026-09-23 | noMaterialEvent | 33 | 3.0913 / 3.5721 / 3.5856 | 12 / 12 |
| 2026-09-23 | lifecycleEvent | 87 | 3.8257 / 5.2117 / 6.5097 | 14 / 14 |
| 2026-09-23 | telegramEligibleEvent | 1 | 4.6561 / 4.6561 / 4.6561 | 14 / 14 |
| 2026-10-01 | noMaterialEvent | 42 | 2.899 / 3.9352 / 3.9542 | 12 / 12 |
| 2026-10-01 | lifecycleEvent | 223 | 3.726 / 5.0066 / 5.3146 | 14 / 14 |
| 2026-10-01 | telegramEligibleEvent | 4 | 4.1476 / 8.5456 / 8.6754 | 14 / 14 |

### Full-day recovery - BASELINE (0c5817c)

| Session | kind | total wall ms | read | normalize | sort | engine | core persist/verify | sidecars | observations | WS peak MB | heap before / peak / after MB | allocated MB | gen0/1/2 | observer SQL (writes) | retained option-book states |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|---|---|---:|
| 2026-09-08 | cold-create-full-day | 15,098 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 1566 | 357.4 / 1413.6 / 1404 | 6,450 | 482/131/6 | 2,538 (616) | 538,645 |
| 2026-09-18 | cold-create-full-day | 14,240 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 2247.7 | 377.7 / 2083.4 / 1444.3 | 6,203 | 458/128/4 | 2,194 (529) | 551,507 |
| 2026-09-21 | warm-verify-full-day | 7,684 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 1586.7 | 375.2 / 1430.9 / 1431 | 3,534 | 262/68/3 | 866 (2) | 557,746 |
| 2026-09-23 | warm-verify-full-day | 7,602 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 1575.5 | 372.8 / 1423.4 / 1419.3 | 3,518 | 261/68/3 | 964 (2) | 557,723 |
| 2026-09-28 | cold-create-full-day | 15,256 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 1853.5 | 384.1 / 1663.2 / 1457 | 8,460 | 646/129/5 | 3,842 (938) | 544,920 |
| 2026-10-01 | warm-verify-full-day | 8,566 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 1659 | 395.1 / 1502.6 / 1494.5 | 3,790 | 283/72/3 | 2,028 (2) | 541,920 |

### Full-day recovery - AFTER (this commit)

| Session | kind | total wall ms | read | normalize | sort | engine | core persist/verify | sidecars | observations | WS peak MB | heap before / peak / after MB | allocated MB | gen0/1/2 | observer SQL (writes) | retained option-book states |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|---|---|---:|
| 2026-09-08 | cold-create-full-day | 15,134 | 3,332 | 161 | 187 | 1,156 | 740 | 787 | 51 | 1616.8 | 357.4 / 1494.6 / 1487 | 7,270 | 544/131/5 | 2,539 (616) | 538,645 |
| 2026-09-18 | cold-create-full-day | 12,850 | 3,303 | 208 | 76 | 634 | 396 | 436 | 36 | 1902.4 | 377.7 / 1616.2 / 1515.5 | 7,052 | 524/133/5 | 2,195 (529) | 551,507 |
| 2026-09-21 | warm-verify-full-day | 8,084 | 6,207 | 321 | 73 | 616 | 165 | 66 | 30 | 1663.9 | 375.5 / 1493.8 / 1489.4 | 4,370 | 327/71/3 | 867 (2) | 557,746 |
| 2026-09-23 | warm-verify-full-day | 7,699 | 5,812 | 171 | 62 | 628 | 178 | 73 | 34 | 1652.7 | 373 / 1487.8 / 1485 | 4,349 | 325/71/3 | 965 (2) | 557,723 |
| 2026-09-28 | cold-create-full-day | 15,456 | 3,430 | 170 | 67 | 678 | 851 | 1,130 | 58 | 1816.2 | 384 / 1545.5 / 1544.9 | 9,317 | 713/133/5 | 3,843 (938) | 544,920 |
| 2026-10-01 | warm-verify-full-day | 8,481 | 6,222 | 183 | 74 | 683 | 315 | 137 | 65 | 1735.1 | 395.1 / 1566.7 / 1563.7 | 4,676 | 351/75/3 | 2,029 (2) | 541,920 |

### Dashboard and pinned-capture reads (ms; SQL commands per call)

| Operation | baseline p50 / p95 / max (SQL) | after p50 / p95 / max (SQL) |
|---|---|---|
| LoadRuntimeAsync (steady-state header refresh, after) | n/a (did not exist) | 0.232 / 0.356 / 9.72 (1) |
| LoadSnapshotAsync(10) | 3.055 / 3.773 / 57.68 (13) | 2.653 / 3.445 / 51.59 (13) |
| Commentary LoadLatestAsync(5) | 0.287 / 0.365 / 5.58 (1) | 0.215 / 0.355 / 5.66 (1) |
| Legacy header refresh (before: session + runtime + ten-bar readiness) | 0.664 / 0.882 / 9.18 (3) | 0.612 / 0.873 / 8.55 (3) |
| AdaptiveQuoteAsOfService.LoadAsync (pinned capture quote resolution) | 4.677 / 6.217 / 61.77 (16) | 4.458 / 5.268 / 65.64 (16) |

### Sustained stress (>= 2x the maximum observed 1-second rate)

Session 2026-10-01; observed max 1-second rate 1134 ticks/s => target >= 2268 ticks/s sustained for 60 real seconds. Actual ticks keep their order and values; their arrival is accelerated 40x (window 12:42:44-13:22:44 IST) with the 2-second ordering holdback scaled so it stays 2 REAL seconds.

| Metric | Value |
|---|---:|
| input ticks / real second (mean over the window) | 3030.6 |
| ticks per 250 ms cycle p50 / max | 1132 / 1315 |
| poll ms p50 / p99 / max | 3.4468 / 20.9752 / 30.1659 |
| poll ms with a completed bar p50 / max | 12.2913 / 30.1659 |
| cycle period p99 / max (budget 250 ms + processing) | 270.9752 / 280.1659 |
| cycles over 275 ms | 0.565% |
| fitted cost model | 1.198 ms/poll + 0.00464 ms/tick |
| processing capacity (1 / per-tick cost) | 215,312 ticks/s |
| max pending ordering buffer | 4,799 |
| pending at end / drain after ingestion stopped | 0 / 2.27 s |
| completed bars in the run | 64 |
| completed-bar pipeline p50 / max | 8.0611 / 17.8029 ms |
| working-set / managed-heap peak MB | 1252.3 / 1115.1 |

Equivalence (stress run vs deterministic unthrottled replay over the same horizon): [{"check": "stress-vs-unthrottled-replay 2026-10-01", "equal": true, "recoveryMs": 4836}]

### Value equivalence: baseline vs after (digest of every persisted market table)

| Session | table | baseline (rows, hash) | after (rows, hash) | equal |
|---|---|---|---|---|
| 20260921 | futureBars | 122, 448BA720BE2BF51A | 122, 448BA720BE2BF51A | yes |
| 20260921 | rollingStates | 113, CFFFB86FDEA880C1 | 113, CFFFB86FDEA880C1 | yes |
| 20260921 | optionBands | 244, 4BB653FC5C101564 | 244, 4BB653FC5C101564 | yes |
| 20260921 | residuals | 224, 8BCCFDA4C52C22DD | 224, 8BCCFDA4C52C22DD | yes |
| 20260921 | futuresSupplemental | 122, 8313107660A3819B | 122, 8313107660A3819B | yes |
| 20260921 | optionsSupplemental | 122, 9C11DD6AFDE329CD | 122, 9C11DD6AFDE329CD | yes |
| 20260921 | commentaryEvents | 84, 97D97044B49E4472 | 84, 97D97044B49E4472 | yes |
| 20260921 | weak2Observations | 0, E3B0C44298FC1C14 | 0, E3B0C44298FC1C14 | yes |
| 20260923 | futureBars | 136, 316C1A909DF107E3 | 136, 316C1A909DF107E3 | yes |
| 20260923 | rollingStates | 127, 167E157CC3B7C745 | 127, 167E157CC3B7C745 | yes |
| 20260923 | optionBands | 272, 35AE0B424C1CC9A2 | 272, 35AE0B424C1CC9A2 | yes |
| 20260923 | residuals | 242, C432DD9125441586 | 242, C432DD9125441586 | yes |
| 20260923 | futuresSupplemental | 136, 0DAC93EED595B90C | 136, 0DAC93EED595B90C | yes |
| 20260923 | optionsSupplemental | 136, 4A2098F1DEA6685C | 136, 4A2098F1DEA6685C | yes |
| 20260923 | commentaryEvents | 91, 811AB921C178BFDE | 91, 811AB921C178BFDE | yes |
| 20260923 | weak2Observations | 0, E3B0C44298FC1C14 | 0, E3B0C44298FC1C14 | yes |
| 20261001 | futureBars | 288, 9F838C07240A499A | 288, 9F838C07240A499A | yes |
| 20261001 | rollingStates | 279, 9604AD58BB146596 | 279, 9604AD58BB146596 | yes |
| 20261001 | optionBands | 576, DF80D1A4A8E65354 | 576, DF80D1A4A8E65354 | yes |
| 20261001 | residuals | 538, 07C91A5C044F985A | 538, 07C91A5C044F985A | yes |
| 20261001 | futuresSupplemental | 288, 93583F73745D17D9 | 288, 93583F73745D17D9 | yes |
| 20261001 | optionsSupplemental | 288, 760172BE36EDFAE6 | 288, 760172BE36EDFAE6 | yes |
| 20261001 | commentaryEvents | 230, A124CEA8CABCBB68 | 230, A124CEA8CABCBB68 | yes |
| 20261001 | weak2Observations | 0, E3B0C44298FC1C14 | 0, E3B0C44298FC1C14 | yes |

Live vs restart-replay (same code, same session): live-vs-restart-replay 2026-09-21: IDENTICAL; live-vs-restart-replay 2026-09-23: IDENTICAL; live-vs-restart-replay 2026-10-01: IDENTICAL

### Source query plans (EXPLAIN ANALYZE, BUFFERS, read-only against the VM copy)

```
### Live read: ReadRawAfterIdAsync (Id > cursor near the end of the day, session tokens)
Index Scan using "PK_ticks" on ticks t  (cost=0.77..13.81 rows=1 width=52) (actual time=0.020..0.106 rows=194.00 loops=1)

  Index Cond: ("Id" > '114760969'::bigint)

  Rows Removed by Filter: 206

  Index Searches: 1

  Buffers: shared hit=17 read=2

  Buffers: shared hit=63 read=9

Planning Time: 0.481 ms

Execution Time: 0.120 ms
```
```
### Pinned quote probe A: greatest eligible ExchangeTimestamp
Limit  (cost=4.87..8.89 rows=1 width=24) (actual time=0.121..0.121 rows=1.00 loops=1)

  Buffers: shared hit=9 read=10

  ->  Incremental Sort  (cost=4.87..19621.67 rows=4883 width=24) (actual time=0.121..0.121 rows=1.00 loops=1)

        Sort Key: "ExchangeTimestamp" DESC, "Id" DESC

        Full-sort Groups: 1  Sort Method: top-N heapsort  Average Memory: 25kB  Peak Memory: 25kB

        Buffers: shared hit=9 read=10

        ->  Index Scan Backward using "IX_ticks_Token_ExchangeTimestamp" on ticks t  (cost=0.57..19414.53 rows=4883 width=24) (actual time=0.089..0.105 rows=5.00 loops=1)

              Index Cond: ((("Token")::text = '40704'::text) AND ("ExchangeTimestamp" >= '2026-09-30 18:30:00+05:30'::timestamp with time zone) AND ("ExchangeTimestamp" <= '2026-10-01 13:00:00+05:30'::timestamp with time zone))

              Rows Removed by Filter: 5

              Index Searches: 1

              Buffers: shared read=10

  Buffers: shared hit=19 read=1

Planning Time: 0.080 ms

```
```
### Pinned quote probe B: greatest eligible ReceivedAt within [availability of probe A, boundary]
Limit  (cost=23.49..23.51 rows=1 width=24) (actual time=0.108..0.108 rows=1.00 loops=1)

  Buffers: shared hit=3 read=9

  ->  Incremental Sort  (cost=23.49..23.53 rows=2 width=24) (actual time=0.108..0.108 rows=1.00 loops=1)

        Sort Key: "ReceivedAt" DESC, "Id" DESC

        Full-sort Groups: 1  Sort Method: quicksort  Average Memory: 25kB  Peak Memory: 25kB

        Buffers: shared hit=3 read=9

        ->  Index Scan Backward using "IX_ticks_ReceivedAt" on ticks t  (cost=0.57..23.48 rows=1 width=24) (actual time=0.106..0.106 rows=2.00 loops=1)

              Index Cond: (("ReceivedAt" <= '2026-10-01 13:00:00+05:30'::timestamp with time zone) AND ("ReceivedAt" >= '2026-10-01 12:59:57+05:30'::timestamp with time zone))

              Rows Removed by Filter: 224

              Index Searches: 1

              Buffers: shared hit=3 read=9

  Buffers: shared hit=10

Planning Time: 0.074 ms

```
```
### Pinned quote day-open baseline (first tick of the day available by the boundary)
Limit  (cost=4.87..8.88 rows=1 width=22) (actual time=0.067..0.068 rows=1.00 loops=1)

  Buffers: shared hit=1 read=4

  ->  Incremental Sort  (cost=4.87..19609.43 rows=4884 width=22) (actual time=0.067..0.067 rows=1.00 loops=1)

        Sort Key: "ExchangeTimestamp", "Id"

        Full-sort Groups: 1  Sort Method: quicksort  Average Memory: 25kB  Peak Memory: 25kB

        Buffers: shared hit=1 read=4

        ->  Index Scan using "IX_ticks_Token_ExchangeTimestamp" on ticks t  (cost=0.57..19402.25 rows=4884 width=22) (actual time=0.065..0.065 rows=2.00 loops=1)

              Index Cond: ((("Token")::text = '48704'::text) AND ("ExchangeTimestamp" >= '2026-09-30 18:30:00+05:30'::timestamp with time zone) AND ("ExchangeTimestamp" <= '2026-10-01 13:00:00+05:30'::timestamp with time zone))

              Index Searches: 1

              Buffers: shared hit=1 read=4

  Buffers: shared hit=5

Planning Time: 0.046 ms

Execution Time: 0.074 ms
```
```
### Pinned quote probe B on a sparse token with a wide floor (worst case: no recent tick)
Limit  (cost=3189.43..3189.43 rows=1 width=16) (actual time=36.654..36.655 rows=1.00 loops=1)

  Buffers: shared hit=9 read=4844

  ->  Sort  (cost=3189.43..3189.45 rows=6 width=16) (actual time=36.653..36.654 rows=1.00 loops=1)

        Sort Key: "ReceivedAt" DESC, "Id" DESC

        Sort Method: top-N heapsort  Memory: 25kB

        Buffers: shared hit=9 read=4844

        ->  Bitmap Heap Scan on ticks t  (cost=3165.28..3189.40 rows=6 width=16) (actual time=28.310..36.312 rows=5445.00 loops=1)

              Buffers: shared hit=9 read=4844

                    Buffers: shared hit=6 read=2075

                    ->  Bitmap Index Scan on "IX_ticks_Token_ExchangeTimestamp"  (cost=0.00..138.00 rows=4915 width=0) (actual time=3.558..3.558 rows=40992.00 loops=1)

                          Index Cond: ((("Token")::text = '40704'::text) AND ("ExchangeTimestamp" >= '2026-09-30 18:30:00+05:30'::timestamp with time zone) AND ("ExchangeTimestamp" <= '2026-10-01 13:00:00+05:30'::timestamp with time zone))

                          Index Searches: 1

                          Buffers: shared hit=3 read=159

```
