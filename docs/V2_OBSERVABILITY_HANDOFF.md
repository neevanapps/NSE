# V2 Live Observability, Early Entry and Dashboard Improvements — handoff

Branch `feature/v2-observability`, branched from master `23f4ac7`. Local commits only: nothing merged, pushed or deployed. No VM, production DB, service or real Telegram contact.

## 1. SHAs
- Initial master: `23f4ac7`
- Release SHA: this document deliberately does not name its own final commit (a document cannot contain the hash of the commit that changes it). After the merge, obtain it on the master checkout with `git rev-parse HEAD` and use that full 40-character value for `-ExpectedCommitSha`.
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

## Final checks (owner request, 2026-11-04)

**1. Visual fixture with representative Observer data** (static render of `AdaptiveObserverPanel` live view + `MarketSummarySection` from the same in-memory Observer fixtures the test suite uses, with the real Dashboard CSS, opened in the built-in browser; the local DB has no adaptive tables, so a fixture was the only way to get Observer-shaped data):
- Option price averages: CE25000 Avg5 108.30 / Avg10 107.40, PE25000 Avg5 90.00 / Avg10 87.00 — equal to hand-computed means of the stored `PriceEnd` of bars 8–12 / 3–12. Bid/Ask/Quote age show "—" because the fixture has no tick source (never zero).
- OPTION CONTEXT inversion, asymmetric fixture (CE ΔIV 1, PE ΔIV 3; ΔDelta 0.02 / 0.05; ΔVega 0.4 / 0.9): raw CE−PE would be −2.00 / −0.03 / −0.50; displayed **IV Dir (Inv) +2.00, Delta Dir (Inv) +0.03, Vega Dir (Inv) +0.50**, Direction cells tinted green; headers carry "(Inv)".
- TOTAL SUMMARY: headers "IV Dir (Inv)", "Delta Dir (Inv)", "Vega Dir (Inv)"; Delta/Vega Direction cells tinted by the inverted sign (IV is "—" in that fixture because it has no IV data).
- CE/PE colouring: 0 of the CE-/PE-labelled cells in OPTION CONTEXT and CORE DIRECTION have a background colour; only Direction cells are tinted.
- No calculation logic was changed. The temporary fixture generator was deleted and is not in the repo.

**2. Migration review** (`20261104130137_AddObservability`, run only on a scratch PostgreSQL, never the VM):
- Applying the master (23f4ac7) `pev2-schema.idempotent.sql` and then the new script: tables 8 → 10 (only `pev2_live_marks`, `pev2_opportunities`, plus 2 indexes and one history row); `pev2_schema_versions.schema_version` stays **2** (no bump), so an old Host (exact-version check) and the new Host both accept the schema; running the new script a second time succeeds with no change; a fresh install straight to the new script also yields 10 tables. The script contains no DROP/ALTER/TRUNCATE/DELETE; its only UPDATE is the pre-existing AddAlertOutbox version stamp.
- **Operator note:** both the old and the new script file start with a UTF-8 BOM that makes `psql -f` fail on the first statement (the test harness reads it with `File.ReadAllText`, which strips it). Strip it first: `sed '1s/^\xEF\xBB\xBF//' pev2-schema.idempotent.sql > pev2-schema.nobom.sql` (Git Bash) or run it through your usual tool that strips the BOM.
- Permissions: the Host role needs SELECT, INSERT, UPDATE on the two new tables; the Dashboard's read-only role needs SELECT on them (both roles already need USAGE on schema `pev2`). With the tables or grants missing, both processes degrade to "not installed / no access" and never affect fills.

**3. Release SHA:** see section 1; use `git rev-parse HEAD` after the merge.

**4. Strategy files:** the three `next-0920/*.json` differ from the deployed version-1 files ONLY in `version` 1→2, `entryFrom` 09:30:00→09:20:00 and the added `filters.minCompletedBars: 10` (verified by diff); all entry rules are identical. The version-1 files at `paper-engine-v2-strategies/*.json` are unchanged since master 23f4ac7 (empty `git diff`; git blob ids 92511a9a… TOTAL_ACTIVITY_5_10, feb9d164… TOTAL_VOTE_4_OF_6, 3ca547d1… TOTAL_VOTE_5_OF_6). The Host loads `*.json` from the strategy directory non-recursively, so the `next-0920/` subfolder is never loaded by itself; activation is an explicit copy (below).

**5. Scope:** all seven requested features are in; no NPT1 code or table changed; no known release blocker.

## Manual deployment procedure (Host + Dashboard) — operator runs this, outside market hours
0. Prerequisites: master contains this branch; on the master checkout run `git rev-parse HEAD` → `<RELEASE_SHA>` (40 chars). Take a DB backup: `pg_dump -Fc -n pev2 -f pev2_before_observability.dump <db>`. Copy the current strategy directory aside (keeps the version-1 files).
1. Migration (owner, additive): apply the BOM-stripped `pev2-schema.idempotent.sql` with the owner role; then grants for `nifty_trading_host` (SELECT, INSERT, UPDATE) and the Dashboard read-only role (SELECT) on `pev2.pev2_live_marks` and `pev2.pev2_opportunities`; verify with `SELECT to_regclass('pev2.pev2_live_marks'), to_regclass('pev2.pev2_opportunities');` and `SELECT schema_version FROM pev2.pev2_schema_versions;` (must still be 2).
2. VM `appsettings.Local.json`: merge the `PaperEngineV2` additions from section 17 (Alerts Opened/Closed/ExecutionFailure true; `LiveMarks:Enabled` true). Telegram needs the already-configured `Telegram` section.
3. Strategy files: in `C:\Naveen\NiftySignal.Host\paper-engine-v2-strategies` (verify against the service definition) replace the three version-1 `.json` files with the three from `next-0920\`. Do not leave both versions in the folder.
4. Deploy: `.\deploy.ps1 -Target Vm -Service Dashboard -ExpectedCommitSha <RELEASE_SHA>` then `.\deploy.ps1 -Target Vm -Service Host -ExpectedCommitSha <RELEASE_SHA>`.
5. Activation verification: Host log shows the three strategies loaded at version 2 and live marks enabled (no "not installed" message); Dashboard "V2 LIVE P&L" shows "Publisher running" with updates about once a second; "V2 OPPORTUNITIES" panel loads; the first simulated entry/exit produces a Telegram ENTRY/EXIT alert (banner `V2 SHADOW — PAPER ONLY`) and a row in `pev2.pev2_alert_outbox`; `SELECT count(*) FROM pev2.pev2_opportunities WHERE trade_date = '<today>'` grows after 09:20 IST.
6. Rollback: set `LiveMarks:Enabled` false and the Alerts flags false in `appsettings.Local.json`; restore the saved version-1 strategy files; redeploy the previous release SHA for both services. The two new tables may stay (derived data only). Never delete or truncate the V2 journal/ledger tables.
