# Top-5 event-based depth: development checkpoint (2026-11-25)

Status: **development checkpoint, NOT a release approval.** Base `bd0b08f` (Top-5 time-weighted implementation). No data, credentials or raw market ticks are included here; only aggregate benchmark numbers.

## What changed
Event-based Top-5 depth (`futures-depth5-v2` / `options-depth5-v2`): each distinct coherent five-level snapshot counts once, no time weights. OFI5 and MicroDev5 in all four top Market Summary grids; MicroDev Direction reverted to raw CE-PE (standalone Futures/CE/PE MicroDev cells keep inversion; strategy metric `opt.total.microdev_inv.dir` unchanged). Additive migration `20261125074852_AddDepth5EventBasedColumns`; v1 and v2 rows coexist (`MetricsVersion` is in the unique index). Contracts: `MARKET_SUMMARY_METRICS.md`, design: `TOP5_MICROSTRUCTURE_DESIGN.md` Appendix F, feed: `AGENT_FEED.md`.

## Real-data benchmark (session 2026-10-29, one run per cell)
Source read-only via `research_ro` on `niftysignal_vm_copy`; all writes to a throw-away scratch cluster. 45,240 future ticks, 2,158,360 option ticks, 170 bars, 40 contracts. Harness: `OptionsDepth5HostBenchmark` (env-gated; `NS_DEPTH5_OPT=off|futures|both`).

| Mode | Insert s (base→new) | Verify s | Alloc MB | Peak WS MB | Peak managed MB |
|---|---|---|---|---|---|
| off | 117.1→113.8 | 97.1→93.3 | 17717→17718 | 1426→1434 | 1080→1079 |
| futures | 127.4→130.6 | 106.8→104.8 | 20317→20354 | 1436→1439 | 1090→1090 |
| both | 168.8→171.3 | 127.1→136.1 | 39557→41266 | 1349→1379 | 1144→1157 |

- **Parity:** `core_hash=95E131FEF571910A` in all six runs (old/new x off/futures/both); restart-stable; 0 mismatches, 0 failures; last id equal. Row counts identical (6,800 strike bars, 170 attestations; futures depth 170; options depth 6,800 + 170 attestations).
- Options pass (both): 206k→198k rows/s; per-bar complete p99 0.643→0.747 ms (one 16 ms max outlier, p99 unchanged); coverage identical (89.95% usable; the 10% unobserved are two dead far-OTM strikes).
- Depth costs ~+45% recovery on baseline and new alike; event-based adds ~+1.5% insert, +7% verify, +4% alloc, +2% working set in "both". Single runs: differences of a few percent are within noise.

## Test results
Focused regression set (Depth5, Top5, AgentFeed, DashboardSimplification, AdaptiveCompactGrid, MarketSummary, AdaptiveMigrationPostgres, TotalStrategies): **438/438 pass**, incl. Postgres migration tests on a scratch cluster. Full suite not re-run for the checkpoint. PaperEngineV2 `ExecutionPerformanceTests` latency failure: not a code regression (isolated and CPU-contended runs equal on base and new, p99 ~161-176 us, 85.6 KB/frame allocation on both); it appears only under the parallel full-suite pool. PaperEngineV2 untouched.

## Edge-case status
- **Event dedup: verified.** AAA=1, ABA=3, ABBA=3, no event across a volume-bar boundary, L1-superseded refreshes, sibling split bars cannot multiply an event. A snapshot at exactly the first bar's start is not counted (shared `(start,end]` convention).
- **Previous Host binary compatibility: verified** on a scratch DB (7-step matrix): old Host on the new schema; new Host flags off / futures only / both; rollback never destroys v1 or v2. Old Host ignores unknown migration history entries. The additive migration's Down drops columns; do not run Down on a DB holding v2 data you need.
- **Agent API schema:** `DepthUsableSeconds` removed from `top5`; replaced by `ValidDepthEvents` (new meaning, new name); `DepthMetricsVersion` added to identify v2 semantics. No existing field changes meaning. Schema version not bumped (additive plus one removal of a time-weighted diagnostic); consumers listed in `AGENT_FEED.md`. A consumer reading `DepthUsableSeconds` must be updated.

## Outstanding release blockers
- **B1 (VM retention / newest sessions): OPEN.** Needs an owner-run read-only min/max `ReceivedAt` and per-day tick counts on the VM, and whether `purge-vm-raw-ticks.ps1` has run. Local copy has no ticks after 2026-10-30, so sessions after that cannot be replayed locally.
- **B2 (VM recovery/memory budget): OPEN.** Local one-day numbers above are not the VM. Target remains recovery increase <= +10 s on the VM. Needs VM RAM and Host peak working set, then a real-data recovery measurement at production scale (context: the 2026-11-25 Observer stall, ~36 min recovery).
- **B4 (raw message semantics): OPEN.** `tf`/`df` field semantics are inferred from stored rows, not raw WebSocket messages; mixed-generation rows possible; simultaneity not claimed. Needs a dev capture of field-presence counts or broker documentation.
- Also open before release: run a previous Host *release* binary against the new schema (done with the harness build of bd0b08f, not the deployed VM binary); VM shadow soak with flags on.

## Verdict
Commit as a development checkpoint: GO. Release: NO-GO until B1, B2, B4 are closed.
