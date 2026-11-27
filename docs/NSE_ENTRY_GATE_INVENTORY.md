# Entry-gate inventory (mandate section 14)

Source revision `78d9b8b`. **The table below describes that revision (the first-pass inventory); the section "Resolution (A1-A8)" states what the execution service does now.** Rejection frequencies: none available (no access to the production ledger); the owner query at the end returns them.
Strategies covered: `TOTAL_ACTIVITY_5_10` (A) and `TOTAL_VOTE_5_OF_6` (V), files under `paper-engine-v2-strategies/next-0920/` (version 2). Which directory the VM actually loads is owner configuration (`PaperEngineV2:StrategyDirectory`) and is **not verified**.

`SR` = `NiftySignal.PaperEngineV2.Execution/StrategyRuntime.cs`; `CF` = the strategy JSON.

| # | Gate | Strategy | Source | Condition | Config | Metrics | Category | Overlap / dependency | Action per mandate |
|---|---|---|---|---|---|---|---|---|---|
| 1 | Activity signal | A | CF `entry` | `opt.total.activity.dir:raw` windows 5 AND 10 both >0 (CE) / <0 (PE) | CF | activity | Core signal | none | Retain |
| 2 | 5-of-6 vote | V | CF `entry` | at least 5 of price, enriched, OI, inverted microdev, OFI, activity (window 5) >0 / <0; unknown stays unknown | CF | six | Core signal | none | Retain |
| 3 | Full coverage | A (5 and 10), V (5) | CF `entry` | `opt.total.coverage_full == 1` | CF | coverage | Data sufficiency | gate 6 | Retain (data sufficiency; written inside the signal expression) |
| 4 | Verified bar | both | SR:147 | signals only on a verified bar (bar and sidecars verified, no earlier unverified bar) | code | - | Integrity | gate 6 | Retain |
| 5 | Conflicting signals | both | SR:166 | buyCall and buyPut both true | code | - | Integrity | cannot occur with strict > / < | Retain |
| 6 | Entries allowed (engine disposition) | both | SR:311, SR:394; `Core/Pipeline/Consumer.cs:160-199` | no sequence gap, overflow, build failure, unverified frame or journal fault; run length >= `FramesToClearGap` (3); processing lag <= `MaxProcessingLagSeconds` (5) | Host `PaperEngineV2` | - | Integrity / freshness / Observer health | also cancels a pending order | Retain. **Ambiguity A3**: confirm 3 frames is integrity, not a cooldown |
| 7 | Entry window | both | SR:395; CF `filters` | 09:20:00 <= bar end IST < 15:00:00 | CF | - | Cutoff + warm-up time | gate 8 | Retain |
| 8 | Warm-up | both | SR:396 | verified frames evaluated >= `minCompletedBars` (10) AND BarSeq >= it | CF | - | Warm-up | gate 7 | **Change to 20.** The "frames evaluated" count excludes bars recovered while the engine was not receiving frames, which contradicts section 16; the count must come from recovered, verified bars |
| 9 | Daily loss limit | both | SR:325, SR:398, SR:349 | realized today <= -1500 blocks entries (no liquidation) | CF `risk` | P&L | Risk restriction | - | **Ambiguity A3** (not listed in the mandate) |
| 10 | Max daily trades | both | SR:399 | fills today >= 5 | CF `risk` | - | Configured limit | - | Retain |
| 11 | One position / one pending | both | SR:401; CF `position` | open + 1 > 1, or an order pending | CF | - | Integrity | - | Retain |
| 12 | Opposite signal = exit only | both | SR:165 | no entry on the bar that exits the opposite position | code | - | Sequencing | acts like a one-bar delay | **Ambiguity A5** |
| 13 | Basket contract | both | SR:403-411 | ATM±2 contract of the side resolved and quoted | CF `instrument` | quotes | Integrity | - | Retain |
| 14 | Quote freshness | both | SR:244, 319, 414 | quote age <= 2000 ms at bar end | CF `execution` | quote time | Market-data freshness | - | Retain |
| 15 | Rule 8 premium | both | SR:415 | 100 <= ask <= 150 | CF `instrument.premium` | ask | Rule 8 | - | Retain |
| 16 | Maximum spread | both | SR:324, 417 | spread <= 2 % of mid, at selection and at fill | CF `execution.maxSpread` | bid, ask | Execution restriction | - | **Ambiguity A3** |
| 17 | Lot size known | both | SR:436 | lot size for token and date | instrument master | - | Integrity | - | Retain |
| 18 | Capital | both | SR:329, 440 | cost incl. fees <= remaining virtual capital (50,000; not replenished) | CF `capital` | cash | Execution restriction | - | **Ambiguity A3** |
| 19 | Order lifetime | both | SR:338 | a qualifying quote, strictly newer than the decision, within 60 s | CF `execution` | quote time | Execution integrity | replaced for Avg5 variants | Retain for immediate; Avg5 needs the new lifecycle |
| 20 | Terminal time | both | SR:129, 140; `ExecutionTypes.cs:35` | from 15:00 IST pending entries are rejected and open positions exited | code | - | Mandatory liquidation | gate 7 | **Ambiguity A2** (mandate: liquidate by 15:10) |

## Resolution (A1-A8, implemented in `NiftySignalExecution`)

Strategy files: `paper-engine-v2-strategies/execution-service/`. Enabled: `TOTAL_ACTIVITY_5_10`, `TOTAL_ACTIVITY_5_10_AVG5`, `TOTAL_VOTE_5_OF_6`, `TOTAL_VOTE_5_OF_6_AVG5`. `TOTAL_VOTE_4_OF_6` is present with `enabled: false`.

| Gate | Now |
|---|---|
| 1-5, 10, 11, 13-15, 17 | Retained |
| 6 entries allowed | Retained, 3 clean frames (A3); lag limit 5 s measured at processing time |
| 7 entry window | 09:20 to 15:00 (A2) |
| 8 warm-up | 20 verified completed bars, recovered bars included (A3). The parser still accepts 10 for other files |
| 9 daily loss limit, 16 spread (entries only, never exits), 18 capital | Retained, values unchanged (A3). Capital check against a 100-150 premium: 130 quantity needs about 13,000-19,500 of the 50,000; the lot size on the VM is not verified. A 1,500 loss limit is about 11.5 points on 130 quantity |
| 12 opposite signal | Exit, then the opposite entry from the same signal once flat (A5) |
| 19 order lifetime | Immediate variants: 60 s. Avg5 variants: the intent waits; each placed order lives 60 s and returns to waiting if it fails (A5) |
| 20 terminal time | No forced 15:00 exit; liquidation from 15:08, deadline 15:10 (A2) |
| New: Observer health gate | Observer `Live`, heartbeat within 30 s, recovery complete, execution state reconciled (A6) |
| Delayed immediate entry (Q1) | Contract from the latest verified frame, fill on a fresh live quote, gates 9-19 and the health gate rechecked, 60 s lifetime |
| Effect of a block on pending entries (Q2) | Temporary (market data, bar problem, quote, premium, spread): the order cannot fill; an Avg5 intent is kept, an immediate signal is consumed. Terminal (opposite signal, Observer health, reconciliation, trade or loss limit, cutoff, session end, protective flatten): both cancelled. Table: architecture section 5 |

## Approved removals

In these two definitions there is **no** additional market-state confirmation, no extra magnitude threshold (signs compare with 0; coverage is data sufficiency), no DTE restriction (`requireFreshVix` is false) and no timed cooldown. So the removal list changes nothing for A and V beyond the warm-up change, unless gates 9, 12, 16 or 18 are judged removable (A3, A5).

## Other strategy paths (to be disabled, not itemised gate by gate)

- `TOTAL_VOTE_4_OF_6` (same engine): disable under section 13.
- NPT1 paper-live (`NPT1-MD5`, `NPT1-MDX3`, `NPT1-PE5`, `Trading:PaperLive`; entries 09:30-15:00, liquidation 15:10; limits `MaxTradesPerDay`, `MaxDailyLoss`, `MaxConsecutiveLosses`; `Trading.Core` integrity blocking and expiry selection `MinCalendarDaysToExpiry`). Disabled in source defaults; the VM setting is unverified. **Ambiguity A4.** These share the Observer thread (see the incident report).
- Legacy score engines are not hosted or not called.

## Owner query for rejection frequency (paper-live database, read-only)

```sql
select table_name, string_agg(column_name, ', ' order by ordinal_position)
from information_schema.columns where table_schema = 'pev2' group by table_name order by table_name;
```
The opportunity table listed there carries a reason per strategy and bar; group by strategy and reason for the days of interest.
