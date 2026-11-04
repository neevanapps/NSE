# V2 Live Observability, Early Entry and Dashboard Improvements — handoff

Branch `feature/v2-observability`, branched from master `23f4ac7`. Local commits only: nothing merged, pushed or deployed. No VM, production DB, service or real Telegram contact.

## 1. SHAs
- Initial master: `23f4ac7`
- Final feature SHA: see the last line of this file (`FINAL_SHA`, filled at handoff).
- Chain: f32e4a7 (early entry) → 742176c (IV/Delta/Vega + neutral CE/PE) → 99786bb (avg prices) → cea187d (Telegram) → 29e72f9 (opportunity log) → becf53a/9ac3c73 (live marks) → docs/validation.

## 2. Projects and files changed
Core/Execution (`StrategyConfig`, parser, `StrategyRuntime`, `PaperEngine`, new `Opportunities.cs`, `LiveMarks.cs`), Alerts (`Alerts.cs`), Data (`Model.cs`, `Observability.cs`, migration `20261104130137_AddObservability`, regenerated idempotent SQL), Runtime (`LiveMarks.cs`, `PostgresLiveMarkStore.cs`, `V2Runtime`, `Options`), Host (`PaperEngineV2Bridge.cs`), Dashboard (services, 4 new components, `Home.razor`, `AdaptiveObserverPanel`, `MarketSummarySection`), Tests, `paper-engine-v2-strategies/next-0920/`. No NPT1 table or code change. The v1 strategy files are byte-identical.

## 3. Architecture
```
Flattrade feed -> ingestion -> Observer (unchanged)
                                 |-- clean tick --> QuoteBook -> frames -> journal -> PaperEngine (decisions, fills)   [unchanged]
                                 '-- clean tick --> LiveMarkBoard (separate, latest quote per option token)
PaperEngine --volatile position snapshot--> LiveMarkPublisher (1 s sampler) <-- LiveMarkBoard
LiveMarkPublisher --changed marks + heartbeat, one batched upsert--> pev2.pev2_live_marks   (HOST process)
Dashboard (separate process) --1 s timer, one read of <=200 rows--> pev2_live_marks --> Blazor circuit/SignalR --> browser
PaperEngine --decision/outcome--> IOpportunitySink --> pev2.pev2_opportunities (INSERT .. ON CONFLICT DO NOTHING)
Ledger events --> AlertingEventSink --> pev2_alert_outbox --> AlertDispatcher --> Telegram
```

## 4. Streaming / marking design
The Host feeds each clean option tick into `LiveMarkBoard` (O(1), Observer thread, no waiting). A quote replaces the stored one only if newer by (availability time, tick id), so out-of-order ticks never overwrite. Every option-universe token is tracked, so a held contract outside ATM±2 is covered with no substitution and no extra subscription. The board is separate from the `QuoteBook`, so frames, hashes and fills are untouched. The publisher is a sampler, not a queue: nothing accumulates, a slow store only delays the next sample. It writes only changed marks (status, quote tick, closed, realized) plus a heartbeat row; unchanged open marks are refreshed every 5 s. Rate: about 1 sample/s, no per-tick DB write, no ledger scan (positions come from an engine snapshot). A store failure is counted and contained; a missing table/grant disables the store after one failure. Cancellation-safe shutdown (`StopAsync` waits for the loop).

## 5. Live P&L, fees, freshness
- Gross bid-marked = (bid − entry fill) × remaining quantity.
- Exit price = max(0, bid − exit slippage); Est. net liquidation = (exit price − entry fill) × qty − entry fees incurred − estimated exit fees (`npt1-frozen-fees-v1`), so no charge is counted twice. It equals the engine's own exit net for the same price (tested).
- Realized net comes from the ledger and is displayed separately. The indicative total = realized + open estimates, shown only when every open mark is Fresh.
- Status: **Fresh** (quote ≤ 10 s old, valid) shows P&L; **Stale** keeps bid/ask but shows no P&L; **Missing** (no quote since restart) and **Invalid** (zero/crossed/non-finite) show "unavailable"; **Closed** shows the ledger net. If the Host heartbeat is older than 5 s the Dashboard shows every Fresh row as Stale and flags the publisher SILENT. Rows of another IST trade date are never shown.

## 6. Versioned strategy JSONs
`paper-engine-v2-strategies/next-0920/{TOTAL_ACTIVITY_5_10,TOTAL_VOTE_4_OF_6,TOTAL_VOTE_5_OF_6}.json`, each `version: 2`, `entryFrom 09:20:00`, `filters.minCompletedBars: 10`. Entry formulas unchanged. The parser rejects `entryFrom` < 09:30 unless `minCompletedBars >= 10`, and `entryFrom` < 09:20. The currently deployed version-1 files are untouched; a different version/hash is a new strategy identity. Swap = replace the three files in the strategy directory (owner action).

## 7. Average price display
Dashboard live view only (not in the six-grid Telegram screenshot): table `option-price-averages` with Avg5 / Avg10 of CE and PE `PriceEnd` over the last 5/10 completed bars, plus the current bid/ask (fresh ≤ 10 s). An average is shown only when ALL N consecutive bars have a finite positive price; otherwise "unavailable". Display only: no signal, filter or strategy reads it. Example: CE bars 118,120,121,119,122 → Avg5 = 120.00.

## 8. IV / Delta / Vega rendering
`DirectionDisplay` flips the sign of the displayed Direction for IV, Delta and Vega in TOTAL SUMMARY and OPTION CONTEXT, with an "(Inv)" label; the underlying scores and strategy inputs are unchanged. CE/PE background tint is removed in both fixed-strike grids (`tint: false`); the Direction-only tint stays. Eight existing tests were updated deliberately for the label/sign change; a guard test asserts `DirectionDisplay` is used only in the Dashboard.

## 9. Opportunity log
Table `pev2.pev2_opportunities` (append-only, deterministic id `strategy:session:bar:stage`). Columns: opportunity_id, strategy_id, version, config_hash, session_id, trade_date, bar_seq, stage (Decision/Outcome), resolved_bar_seq, decision_utc_ticks, direction, category, category_name, reason, detail, in_position, order_eligible, order_attempted, order_result, intent_id, token, event_ref, frame_ref, entries_allowed, coverage, recorded_utc_ticks. Nine categories (see `OpportunityCategory` in `Execution/Opportunities.cs`): signal/no-signal, entry filter rejected, warm-up incomplete, in position, outside window, quote/coverage problem, order attempted/filled, order rejected, other. Written outside the decision path; a sink failure is counted and never affects fills. The Dashboard panel degrades to a message if the table is missing.

## 10. Nov 4 rejection audit
**No Nov 4 V2 data is available locally.** `niftysignal_vm_copy` has only `public` tables (ticks 2026-09-04..2026-10-30, no `pev2` schema). No distribution is invented. Read-only SQL for the owner on the VM/production DB:
```sql
SELECT payload_json::jsonb ->> 'Reason' AS reason, count(*)
FROM pev2.pev2_events
WHERE kind = 'EntryRejected' AND session_trade_date = '2026-11-04'   -- adjust to the column holding the IST trade date
GROUP BY 1 ORDER BY 2 DESC;

SELECT strategy_id, payload_json::jsonb ->> 'Reason' AS reason, payload_json::jsonb ->> 'Detail' AS detail, count(*)
FROM pev2.pev2_events WHERE kind = 'EntryRejected' AND occurred_utc >= '2026-11-04 03:30+00' AND occurred_utc < '2026-11-04 10:00+00'
GROUP BY 1,2,3 ORDER BY 4 DESC;
```
(Verify column names against `\d pev2.pev2_events` first; the payload property names `Reason`/`Detail` are PascalCase.)

## 11. Telegram
Banner `V2 SHADOW — PAPER ONLY`; enriched ENTRY/EXIT text (contract, expiry, strike, signal id, entry/exit price and time, reason, gross, fees, net, held duration). Idempotent alert id = event id + class; Uncertain sends are never auto-resent; restart-safe context lookup from the ledger. Tested only with fake senders (39 tests: AlertTests, V2NotificationTests, RuntimeTests) — **no real Telegram message was sent**. Alerts are off by default.

## 12. Regression and replay
See section "Results" at the end (filled from the actual runs).

## 13. Browser validation
See "Results" at the end.

## 14. NPT1 and Observer parity
No NPT1 code or table touched. Tests prove: the V2 ledger and opportunity log are identical with marks on vs off under concurrent publishing; frame sequence/hashes unchanged (board is separate from the QuoteBook); the Alerts assembly reference guard still passes. Existing NPT1/Observer suites pass in the full regression (see Results).

## 15. Migration and rollback
Migration `20261104130137_AddObservability` creates only `pev2.pev2_live_marks` and `pev2.pev2_opportunities` (+2 indexes). It does **not** bump `V2Schema.CurrentVersion`, so old and new Hosts both stay compatible; with the tables absent the features switch themselves off (fail-safe) and the Dashboard says "not installed". It is never auto-applied.
Owner steps (additive, idempotent):
1. Back up: `pg_dump -Fc -n pev2 -f pev2_before_observability.dump <db>`.
2. Apply the `20261104130137_AddObservability` blocks of `NiftySignal.PaperEngineV2.Data/Sql/pev2-schema.idempotent.sql` with the owner role (the whole script is idempotent and safe to re-run).
3. Grants:
```sql
GRANT SELECT, INSERT, UPDATE ON pev2.pev2_live_marks, pev2.pev2_opportunities TO nifty_trading_host;
GRANT SELECT ON pev2.pev2_live_marks, pev2.pev2_opportunities TO <dashboard_readonly_role>;
```
4. Verify: `SELECT to_regclass('pev2.pev2_live_marks'), to_regclass('pev2.pev2_opportunities');` both non-null; `SELECT "MigrationId" FROM pev2.__pev2_migrations_history ORDER BY 1 DESC LIMIT 1;`.
Rollback: set `PaperEngineV2:LiveMarks:Enabled` false and redeploy the previous build; the new tables are harmless and may stay (or `DROP TABLE pev2.pev2_live_marks, pev2.pev2_opportunities;` — they hold only derived data). Restore the version-1 strategy files to revert early entry. **Never delete or truncate the V2 journal/ledger tables.**

## 16. Deploy commands (not run)
Changes touch both processes, so deploy Host and Dashboard. Order: (1) owner SQL + grants above; (2) Dashboard first (it degrades gracefully without data), (3) Host last, outside market hours, after swapping the strategy files.
```powershell
.\deploy.ps1 -Target Vm -Service Dashboard -ExpectedCommitSha <FINAL_MERGED_MASTER_SHA>
.\deploy.ps1 -Target Vm -Service Host      -ExpectedCommitSha <FINAL_MERGED_MASTER_SHA>
```
Use the SHA of the final merged master commit only (not the feature SHA). Verify the service definition for the Host install dir (`C:\Naveen\NiftySignal.Host`) and strategy dir (`C:\Naveen\NiftySignal.Host\paper-engine-v2-strategies`) before copying `next-0920\*.json` over it (keep a copy of the v1 files).

## 17. VM `appsettings.Local.json` additions (manual; keep the existing file)
```json
"PaperEngineV2": {
  "Alerts": { "Opened": true, "Closed": true, "ExecutionFailure": true, "OrderRejected": false, "PartialFill": false },
  "LiveMarks": { "Enabled": true, "IntervalMs": 1000 }
}
```
Merge into the existing `PaperEngineV2` section (do not duplicate it). Telegram needs the existing `Telegram` section to be configured. Live marks default to off.

## 18. Limitations and risks
- Early entry needs 10 verified bars (stricter than the 5 the windows need) — the safer reading of the directive.
- A held contract the ingestion does not subscribe shows Missing (never a substituted token).
- Average-price quotes are as of page load, not per second; marks are an estimate, never a fill.
- Marks need the new tables; without them the features are silently off (visible as "not installed").
- Feed gaps show Stale/Missing, not zero. A Dashboard disconnect only stops the display.
- Directive vs code: "show IV/Delta/Vega Direction inverted" is display-only; strategies still consume the original scores.

## Results
**Full Release solution regression** (`dotnet test NiftySignal.slnx -c Release`, scratch PostgreSQL 18 enabled for the trading, pev2 and migration tests): **Passed 2854, Failed 0, Skipped 1, Total 2855.** The one skip is `HostReplayTests.RecordedSession_FlowsThroughTheRealHostPath_...`, which by design needs `NSE_V2_REPLAY_SOURCE` (read-only local recorded session); it was run separately, below. A first full run had 1 failure: the Execution assembly vocabulary guard (no "Live" in type names) flagged my new `LiveMark*` types. The guard was NOT weakened; the Execution types were renamed `PositionMark`, `PositionMarkCalculator`, `PositionMarkJson`, and the rerun passed.

**Recorded-session replay (Sep 7, real Host path, read-only source, NPT1 parity)**, run separately with `--no-build`: Passed 1/1 with the deployed version-1 strategy files (6 m 3 s) and Passed 1/1 with `next-0920` (version 2, 09:20). Both ran after the final code.

**Browser validation** (built-in browser on a localhost Dashboard from this branch, scratch pev2 DB seeded with moving marks for the three strategies; test login created locally, nothing production touched):
- 1920 px and 1440 px: no horizontal overflow (scrollWidth = clientWidth). 1024 px: the V2 panels fit (the table scrolls inside its own `scroll-x` container), but the page overflows horizontally by about 340 px because of the **pre-existing top status bar** (status items, kill-switch toggle, logout button), unchanged by this branch. Reported, not fixed (out of scope).
- Live P&L changed between two samples 2.5 s apart (e.g. TOTAL_ACTIVITY_5_10 net +741.04 → +710.51) with the publisher "running, last update 0.4 s ago", independently of the 15 s ledger refresh.
- All three strategies visible; Closed row shows the ledger net; a position with no observed quote shows Missing with every price "unavailable"; Fresh = green badge, Closed = neutral, Missing/Stale/Invalid = warning; profit/loss colours applied.
- Not verified in the browser: the avg-price table and the neutral CE/PE grid backgrounds need Observer data that the scratch environment lacks; both are covered by unit/render tests (`OptionPriceAveragesTests`, the updated `DashboardSimplificationTests`/`AdaptiveCompactGridTests`), and the six-grid historical screenshot tests (pinned data) pass in the regression.
- Not run: a Telegram message (none sent), the VM, production DB.
