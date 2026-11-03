# Paper Engine V2: three TOTAL SUMMARY strategies

Strategy ids: `TOTAL_ACTIVITY_5_10`, `TOTAL_VOTE_4_OF_6`, `TOTAL_VOTE_5_OF_6`. Files: `paper-engine-v2-strategies/<id>.json` (the only files that belong in the V2 `StrategyDirectory`; the Host loads every `*.json` it finds). Paper only; V2 has no broker capability and the strategy format cannot express one. NPT1 and the adaptive producer are not touched.

## What they trade (source model `fixed13650-summary-v1`, TOTAL grid, raw `:raw` Dir metrics)

| Strategy | Buy CE when | Buy PE when |
|---|---|---|
| A `TOTAL_ACTIVITY_5_10` | Activity Dir (5) > 0 **and** Activity Dir (10) > 0 | both < 0 |
| B `TOTAL_VOTE_4_OF_6` | at least 4 of the six 5-bar Dir values are strictly > 0 | at least 4 strictly < 0 |
| C `TOTAL_VOTE_5_OF_6` | at least 5 of six > 0 | at least 5 of six < 0 |

The six: Price Dir, Enriched Dir, OI Dir (PE − CE, unchanged), **MicroDev Dir inverted** (`opt.total.microdev_inv.dir:raw`), OFI Dir, Activity Dir. Zero counts for neither side. Disagreement, zero or an unavailable value means no signal (A: any window disagreeing or missing; B/C: all six must be available, otherwise the vote is Unknown, never a partial count). Every tree also requires `opt.total.coverage_full == 1` for the windows it reads (FULL coverage; PARTIAL is not accepted).

Unchanged frozen V2 contract: entry/exit at the executable ask/bid of a later, strictly newer two-sided quote, CE on a positive and PE on a negative signal, premium 100–150, deterministic strike ranking inside the signal-time ATM±2 basket, expiry/strike/token frozen at the order, quote freshness, lot size, capital and risk checks, no entry before 09:30:00 or at/after 15:00:00 IST, no substitution outside the eligible universe, an opposite signal requests **Exit** (no reversal; the opposite side can open only on a later verified evaluation once flat), a neutral/unavailable signal preserves the position, terminal exit at 15:00.

## Engine extension (backward compatible)

* **`atLeast/1` count node:** `{ "atLeast": N, "onUnknown": "Unknown", "of": [ leaf, ... ] }`. 2 to 12 LEAF conditions (no nested group/count), `1 <= N <= count`, `onUnknown` mandatory and the only supported policy is `Unknown` (any Unknown listed condition makes the whole count Unknown). Leaves count toward the existing 32-leaf limit. Evaluates every item (no short-circuit) and allocates nothing. Documents without it parse and hash exactly as before.
* **`opt.total.microdev_inv.dir:raw` / `:pct`:** appended at the END of `MetricRegistry` (handles 0–283 unchanged; registry size 284 → 286). Value = `−(raw CE−PE MicroDev direction)`, ratio flips with it, zero stays zero, Unknown exactly when the raw is Unknown. The flip is applied once, in `MarketStateFrameBuilder`; `opt.total.microdev.dir` and every other MicroDev metric keep the producer value. Test: `MicroDev_IsInvertedExactlyOnce_RawAndEveryOtherMetricStayUnchanged`.
* A **Host build that does not contain this commit rejects `atLeast` and the inverted metric** ("unknown property"/"unknown metric" → V2 closes `ConfigInvalid`). Strategy A uses only existing nodes and metrics.

## Values I had to choose (owner to confirm)

Taken from existing configuration: capital 50,000, lot size 65 (from the instrument, via the runtime), 2 lots per trade, premium 100–150 (`Ruleset`); **`maxDailyTrades` 5 and `maxDailyLoss` 1,500 (owner directive for the paper session; they replace the earlier 30 / 10,000)**; `maxSpread.fraction` 0.02 (`StrikeSelection:MaxSpreadPctOfMid` 2.0); `maxQuoteAgeMs` 2000 (NPT1 execution freshness, V2 design §4); `maxProcessingLagMs` 5000 (`PaperEngineV2:MaxProcessingLagSeconds`); fee schedule `npt1-frozen-fees-v1` (the Host's configured `CostScheduleVersion`).

| Field | Value | Why / status |
|---|---|---|
| `execution.fillQuote` | `AsOfEligibleBarEnd` | The only rule the execution layer can activate (`StrategyRuntime.CheckActivation` rejects `FirstAfterDecision`). Still a strictly newer quote than the decision. |
| `slippage.entry/exit` | 0.10 | `Costs:SlippageTicks` 2 × tick 0.05. owner to confirm |
| `latencyMs` | 0 | NPT1 research has no latency model. owner to confirm |
| `entryOrderLifetimeMs` | 60000 | Must exceed latency; one bar. owner to confirm |
| `position.maxConcurrent` | 1 | NPT1 holds one position per strategy; "3 concurrent" is the sum over the three strategies. owner to confirm |
| `risk.onBreach.liquidation` | `None` | Breach blocks entries; open positions keep their exit rules. owner to confirm |
| `exit.mechanism` | `OppositeSignalOrSessionEnd` | Replaces the earlier never-true `fut.coverage_pct > 100` predicate. The validator demanded an explicit exit mechanism; a new, backward-compatible `exit.mechanism` states the real contract: leave on the OPPOSITE entry signal or at the 15:00 terminal exit, with no discretionary predicate. It is refused together with `exit.conditions`; configs without it parse and hash exactly as before (NPT1 unaffected). A Host build older than this commit rejects the key (`ConfigInvalid`). |
| `requireFreshVix` | false | Not requested. |

## Strategy A, 2026-09-07 replay: 1 entry, 0 exits (unresolved)

Root cause (engine limitation, not a data gap): frames carry quotes only for the signal-time ATM+-2 basket (10 contracts, centred on each completed bar's own option band). The held PUT (token 42636, strike 23900) drifted out of that basket while the centre sat at 23750 (bars 100-160), so no frame carried a quote for it. The terminal exit was requested at 15:00:27 but could not be priced; at the 15:25 retry cut-off the position became `Unresolved`. The recorded ticks do contain two-sided quotes for the token after 15:00 (7,155 of 7,157 ticks between 15:00 and 15:40 IST; bid 121.30 / ask 121.65 at 15:00:27). No fill was fabricated. Accounting: realised net excludes the open position; entry fees are charged and cash stays reduced; there is no unrealised P&L representation. Every V2 strategy has this limitation (the exit path needs the held token inside the basket).

Recommended fix (needs the owner's decision, changes the frame contract and so the frame schema/hash): carry the held positions' tokens in the frames. Until then a position whose contract leaves the basket and is still open at 15:00 can end `Unresolved`.

## Open owner decisions (recommended defaults are what the files contain)

1. Coverage: files require `coverage_full == 1` (recommended) rather than accepting PARTIAL.
2. Dir metrics: `:raw` (the cell value the Dashboard shows first; unit-explicit). `:pct` differs in sign only when numerator and denominator disagree (e.g. MicroDev, which is raw `Spread/2·TOB`); switching needs new versions of the files.
3. Cost schedule: `npt1-frozen-fees-v1` (the Host's current setting); the two registered schedules overlap in time.
4. Alerts: off by default; turn on `Opened`, `Closed`, `OrderRejected`, `ExecutionFailure` deliberately.

## Notes verified in the code

* The V2 frame path does **not** depend on the Dashboard fixed-strike catalog. The basket centre comes from the completed bar's own option band (`DbFrameInputSource`/`PaperEngineV2Bridge`): a missing centre builds no frame, and a centre that is not a listed strike resolves no slot (problems recorded, no contract, the entry is rejected). There is no "lowest strike" fallback in V2.
* Engine instants are expected as UTC `DateTimeOffset` (the database delivers UTC); the IST entry window adds +05:30 to the UTC time of day.

## Does the deployed build support them?

The deployed Dashboard is `62b2583` (+ the agent feed `5e5fb61`); the Host build on the VM is whatever was last deployed (V2 code is in the Host). Without this commit: **A can run** (existing nodes/metrics only; `AsOfEligibleBarEnd` is supported), **B and C cannot** (`atLeast` and `opt.total.microdev_inv` do not exist, so V2 would close `ConfigInvalid` for the whole directory: an invalid file rejects only itself, but keep B/C out of the directory on an old build). A **new Host build and a Host restart are required** for B and C; the Dashboard needs no change. This commit changes no persisted schema (frames store a metric table sized by the registry; old journal rows are not re-read across sessions).

## Activation (SHADOW, paper only), owner-run, after hours

Realistic first session: **the next session after an after-hours Host deploy**. 2026-11-03 cannot be reached (the session is over by 15:30 IST and a Host restart must not happen in market hours). Nothing below has been run on the VM.

1. Bring this branch onto `master` (owner's decision; fast-forward) and deploy only the Host, from a clean `master` checkout, outside 09:00-15:45 IST:
   ```powershell
   .\deploy.ps1 -Target Vm -Service Host -ExpectedCommitSha <40-hex SHA of the merged commit>
   ```
   `appsettings.Local.json` on the VM is never overwritten. The service restarts as part of the deploy; V2 stays disabled until step 4.
2. On the VM (read-only check first), the `pev2` schema must already be installed and at schema version 2 (`docs/PAPER_ENGINE_V2_DEPLOYMENT.md` sections 3-5; `psql` location and role names are NOT VERIFIED). If it is missing, take the documented backup and apply `NiftySignal.PaperEngineV2.Data/Sql/pev2-schema.idempotent.sql`.
3. Create the strategy directory and copy exactly the three files (nothing else; an extra `*.json` would be loaded):
   ```powershell
   $dir = 'D:\NiftySignal\paper-engine-v2-strategies'     # your choice; NOT VERIFIED on the VM
   New-Item -ItemType Directory -Force $dir | Out-Null
   Copy-Item .\paper-engine-v2-strategies\*.json $dir
   Get-ChildItem $dir | Select-Object Name, Length
   ```
4. Merge (do not replace) these keys into the Host's VM `appsettings.Local.json`:
   ```json
   "PaperEngineV2": {
     "Enabled": true,
     "ConnectionStringName": "TradingPaperLive",
     "StrategyDirectory": "D:\\NiftySignal\\paper-engine-v2-strategies",
     "CostScheduleVersion": "npt1-frozen-fees-v1",
     "Alerts": { "Opened": false, "Closed": false, "OrderRejected": false, "PartialFill": false, "ExecutionFailure": false }
   }
   ```
   Leave `Trading:PaperLive` (NPT1) as it is; there is no live/broker setting anywhere in V2. Enable alert classes later and deliberately.
5. Restart only the Host service after hours, then read the log:
   ```powershell
   Restart-Service NiftySignalHost
   Select-String -Path "<host install dir>\logs\niftysignal-*.log" -Pattern 'Paper Engine V2' | Select-Object -Last 10
   ```
   Expect "Paper Engine V2 SHADOW started" listing the three ids. Any other state (`SchemaIncompatible`, `ConfigInvalid`) means fix the stated cause; NPT1 and the observer are unaffected by design.
6. Next morning: Home page V2 section shows the three strategies; verify NPT1 initialised and recovered as before.
7. Rollback: `PaperEngineV2:Enabled=false` and restart the Host (ledger and journal stay).
