# Adaptive Market Observer V1 — Live Binding Roadmap

## Base and operating principle

- Canonical implementation base: `master`.
- New work must start from a fresh feature branch based on `master`.
- The current `NiftyRatio` branch is treated as research/development history, not the production source of truth.
- The new adaptive system is observation-only first. No paper trading or real order routing is enabled as part of the initial live binding.
- The implementation must reproduce the validated NiftyResearcher semantics exactly before any live result is trusted.
- Dashboard code is a reader only. Market-state calculations belong in Host/domain services and are persisted.

## Implementation status — 2026-10-05

### Current checkpoint

- Implementation branch: `feature/adaptive-market-observer-v1`
- Branch base: fresh branch created directly from `master`
- Last fully green build/test checkpoint: `e91d1ca85c8d5c705f5d02952bf15a29af516cdf`.
- Validation workflow: [37324823845](https://github.com/neevanapps/NSE/actions/runs/37324823845).
- **Build: PASS — zero warnings/errors; adaptive tests: 47/47; full regression: 1122/1122.**
- Initial and frozen-universe EF migrations committed, applied to isolated PostgreSQL 17, and model-drift check passed. Eight isolated tables, eight unique indexes, seven restrictive session foreign keys, frozen rate, required selection policy and frozen option universe.
- Frozen historical gate **PASS** at `e91d1ca`, reference research SHA `e768b8e5b87938fb7be9531310f1c6f786853301`, private workflow `37324830737`: all 13 discovery sessions, 2034 complete futures bars and 3,905,218 futures/threshold field checks, 3,641 residual rows (40,051 fields), and 125 executable overlapping H5 observations plus an unavailable selection. Includes original Python daily thresholds, complete-engine ATM/ATM±2 residual availability and values, and independent overlapping option selection/OI/H5/MFE/MAE checks. No sealed dates used. Numeric price/OI comparisons retain declared strict tolerances; observation timing allows 1 microsecond for Python timestamp serialization.
- This pass does not establish the new Grid 2 band/rolling policy, production source/bootstrap path, completed-observation restart verification, browser behavior or VM behavior. Separate independent gates are being added/run for these scopes.
- F63 diagnostic clock and F66 six-decimal decision precision passed the full historical gate. F64/F65 frozen option universe and after-hours recovery passed their earlier gates. F67 completed observation/identity reconciliation and F68 disabling legacy paper workers are now implemented and require a green final regression/release review.
- New source-reader/bootstrap and independent band-boundary/rolling oracle additions need their own full 13-session pass. The original research has no identical new Dashboard Grid 2 implementation; its cleaner/classifier are the independent reference for grid flow, with separate aggregation/selection policy validation.
- No browser/VM synchronization or commit-to-visible latency measurement has passed.
- Merge to `master`: **blocked until every remaining gate passes**.
- VM deployment and prospective observation: **not performed**.

Status terminology used below:

- **VALIDATED** — implemented and passed the required automated/historical gate.
- **IMPLEMENTED / UNVALIDATED** — code exists on the implementation branch but the relevant gate has not yet passed.
- **IN PROGRESS** — partially implemented or currently being corrected.
- **PENDING** — not implemented or not yet attempted.

### Overall status

| Workstream | Status | Notes |
|---|---|---|
| Source-control isolation | **VALIDATED** | Work is on a fresh branch from `master`. |
| Build/source traceability | **IMPLEMENTED / UNVALIDATED** | Branch/SHA/build-UTC metadata added to build/deploy/runtime/session path. Full solution build passed at the checkpoint above; exact running-build verification remains pending. |
| Exact futures adaptive calculation library | **IMPLEMENTED / UNVALIDATED** | Tick cleaning, aggressor classification, exact split bars, rolling state, evolution, adaptive opening-volume estimator and Weak2 logic are ported with focused unit tests. Futures/threshold/Weak2 historical gate passed at the checkpoint above; production-source bootstrap remains pending. |
| Adaptive option-band calculations | **IMPLEMENTED / UNVALIDATED** | Futures-clock ATM±2 selection, CE/PE independent persistence model, contract/notional flow, rolling option metrics and strength-change fields exist. Historical parity gate is still pending. |
| 09:30 option residual calculations | **IMPLEMENTED / UNVALIDATED** | Both fixed ATM and fixed ATM±2 variants are implemented using the research constant-IV repricing method. Focused deterministic tests exist; constant-IV/richness fixtures passed; complete engine clock/quote integration passed against the frozen Python rows in all 13 discovery sessions. |
| New adaptive persistence schema | **IMPLEMENTED / UNVALIDATED** | Isolated `niftysignal_adaptive_observer` EF model/entities/indexes exist. Both reviewed migrations are committed, applied in isolated PostgreSQL and model-drift checked. |
| Immutable 09:30 session coordinator | **IMPLEMENTED / UNVALIDATED** | Waits for persisted data to advance through 09:30, freezes daily threshold/anchors/model identity and reuses the persisted row on restart. |
| Historical strong-threshold bootstrap | **IMPLEMENTED / UNVALIDATED** | Replays prior production sessions. Discovery dates carry frozen OOF adaptive thresholds so the bootstrap does not retrospectively substitute the final live estimator. Direct futures-library parity passed; production source/bootstrap parity remains pending. |
| Incremental live observer worker | **IMPLEMENTED / UNVALIDATED** | Watch-only worker reads persisted raw ticks, keeps a causal ordering lag and commits completed-bar packages. |
| Restart/recovery path | **IMPLEMENTED / UNVALIDATED** | Full-day persisted-tick replay + persisted-row verification is implemented; a deterministic artificial-restart test was added. Artificial prefix restarts, complete persisted-field corruption checks, and the raw-ID cutoff buffer test passed; frozen universe and outside-hours close passed; completed execution/identity reconciliation is now being revalidated (F67). |
| Weak2/OI/H5 observation lifecycle | **IN PROGRESS** | ₹100–₹150 fixed-strike selection, pair OI gate, H5 bid, MFE/MAE and independent overlapping observation outcomes passed the frozen historical gate. Completed-observation restart checks are being strengthened. |
| Dashboard adaptive read service | **IMPLEMENTED / UNVALIDATED** | Dedicated adaptive DB read path; no raw-tick/strategy calculation in Razor. |
| Dashboard three-grid UI | **IMPLEMENTED / UNVALIDATED** | Existing StatusBar/access-token/Live Quote retained; adaptive Futures, Options and Residual grids added; incomplete bar is outside grids; old panels moved to `/legacy`. Needs successful build and UI/runtime validation. |
| Dashboard row selector | **IMPLEMENTED / UNVALIDATED** | Global `5 / 10 / 15`, default 10. |
| Grid 2 mode | **IMPLEMENTED / UNVALIDATED** | `BOTH / CE / PE`, default BOTH; `NOTIONAL / CONTRACT`, default NOTIONAL. BOTH shows CE and PE plus explicit pair differences, never a composite score. |
| Grid 3 mode | **IMPLEMENTED / UNVALIDATED** | `ATM±2 / ATM`, default ATM±2, with fixed 09:30 composition. |
| Current incomplete bar | **IMPLEMENTED / UNVALIDATED** | Shown separately from immutable completed rows. |
| Sub-second Dashboard target | **PENDING VALIDATION** | Architecture is designed for indexed last-N reads + SignalR invalidation; latency has not yet been measured. |
| Legacy runtime cleanup | **PENDING** | Legacy source/tables are retained for rollback. F68 removes the two active paper-worker registrations to satisfy watch-only deployment. Broader cleanup remains deferred. |
| Watch-only production deployment | **PENDING** | Must not occur until build/tests/parity/migration/runtime gates pass. |
| Paper trading | **PENDING / OUT OF V1** | Explicitly excluded from the first live observer deployment. |

### Completed implementation details

The following code paths now exist on the feature branch, although the items marked above remain unvalidated until CI/parity gates pass:

- new pure calculation project `NiftySignal.AdaptiveObserver`;
- new persistence project `NiftySignal.AdaptiveObserverData`;
- consecutive-payload tick deduplication using causal `AvailableAt = max(ExchangeTimestamp, ReceivedAt)`;
- strict quote-derived aggressor classification and enriched tick-rule fallback kept separately;
- exact futures volume-bar splitting;
- 10-complete-bar rolling market state;
- Duration, BarTradeUpdates and RollingVolume/Second;
- rolling strict/enriched evolution fields;
- frozen V1 09:30 full-day volume estimator;
- frozen discovery OOF adaptive thresholds for the original discovery sessions;
- direct three-row Weak2 classification that permits overlapping research windows;
- session-long per-option cumulative-volume/quote state;
- Grid 2 ATM±2 selection on the futures adaptive clock, frozen per adaptive bar;
- both raw quantity/contract and premium-notional option flow;
- option rolling band price/return, flow, activity and OI metrics;
- fixed 09:30 ATM and ATM±2 theoretical-residual models;
- immutable adaptive daily session row;
- build branch/SHA/build-UTC traceability;
- full-day restart reconstruction and persisted-row reconciliation;
- watch-only Weak2 observation persistence including OI gate/H5/MFE/MAE design;
- session-close finalization of unresolved observations;
- Dashboard-specific adaptive read model;
- synchronized three-grid UI;
- existing Header/StatusBar, access-token controls and Live Quote retained;
- legacy Dashboard retained at `/legacy`;
- no adaptive paper or real order path.

### Immediate next gates

1. **Restore green Build — PASSED at checkpoint above**
   - selection policy and subsequent compile defects fixed;
   - rerun after each subsequent code/schema change;
   - do not proceed to deployment on an earlier green SHA.

2. **Run focused adaptive tests**
   - core tick/enricher/exact-bar tests;
   - Weak2 overlap semantics;
   - constant-IV residual tests;
   - restart replay test.

3. **Generate and inspect the initial EF migration**
   - migration must be generated from the actual model;
   - inspect keys, unique constraints, enum storage, nullable fields and indexes;
   - commit migration only after review.

4. **Historical parity against NiftyResearcher**
   - exact adaptive base threshold per discovery day;
   - complete bar count;
   - bar timestamps/OHLC/volume;
   - strict/enriched flow;
   - rolling 10-bar states;
   - Strong/Weak1/Weak2 identities;
   - option-band rows;
   - ATM and ATM±2 residual rows;
   - Weak2 ₹100–₹150 selection, pair-OI gate, H5 P&L, MFE/MAE.
   - This gate is mandatory before the live observer is trusted.

5. **Restart-parity validation**
   - uninterrupted replay vs forced restarts inside partial bar, around bar close, Strong/Weak1/Weak2, band roll and pending H5;
   - persisted deterministic fields must match.

6. **Dashboard runtime validation**
   - all three grids must show the same completed `BarSeq`;
   - explicit unavailable/pending rows instead of stale-row substitution;
   - existing Live Quote remains unaffected;
   - verify BOTH/CE/PE and NOTIONAL/CONTRACT selectors;
   - verify ATM±2/ATM residual selector;
   - measure adaptive commit -> visible UI latency; target normally <1 second.

7. **Full solution regression**
   - existing non-adaptive tests/build must stay healthy;
   - legacy rollback page must remain functional during the watch-only period.

8. **Merge/deploy review**
   - only after all gates above are green;
   - review complete diff;
   - merge feature branch to `master`;
   - build from merged master;
   - deploy that exact SHA to VM;
   - confirm Dashboard shows the same branch/SHA/build UTC.

### What is deliberately not complete yet

- No merge to `master`.
- No VM deployment.
- No paper-trading engine.
- No real-order integration.
- No deletion of legacy runtime engines/tables/migrations.
- No claim that live adaptive behavior matches the research until historical parity has passed.
- No claim of <1-second end-to-end behavior until measured on the running Host/Dashboard.
- No new residual/OI/flow filter is being promoted into a trading rule from this implementation work.


## Non-negotiable restart safety

Every live component must be able to restart mid-session and reconstruct the exact same state it would have held without the restart.

A restart must not:
- change today's selected adaptive bar threshold;
- change the strong-state threshold;
- lose a partially formed exact-volume bar;
- duplicate or skip a source tick;
- duplicate a completed bar;
- duplicate a weak1/weak2 observation;
- change fixed-strike selection for an existing observation;
- lose H5 progress or generate H5 twice;
- reinterpret historical events using a newer configuration/model version.

Restart strategy:

1. Persist one immutable daily session configuration row as soon as the 09:30 selection is made:
   - trade date;
   - opening 09:15–09:30 futures volume;
   - estimator/model version;
   - estimated full-day volume;
   - exchange lot size;
   - selected exact base-bar volume;
   - rolling 10-bar volume;
   - strong threshold;
   - build commit SHA / strategy version;
   - selection timestamp.

2. On process restart:
   - if today's daily configuration row exists, load and reuse it exactly;
   - never recompute today's 09:30 threshold from a revised model or changed historical set;
   - rebuild deterministic market state from persisted raw ticks / completed exact bars;
   - resume from an explicit source-tick boundary `(ExchangeTimestamp, TickId)`;
   - reconcile persisted completed bars before accepting any new tick.

3. Persist enough identity for idempotency:
   - unique `(TradeDate, AdaptiveModelVersion, BarSeq)` for exact bars;
   - unique `(TradeDate, AdaptiveModelVersion, EndBarSeq)` for rolling states;
   - unique trigger identity for weak2 observations;
   - observation lifecycle state persisted separately from in-memory state.

4. Add a mandatory restart-parity test:
   - uninterrupted replay of a historical day;
   - replay with several artificial process restarts at arbitrary tick/bar boundaries;
   - resulting daily session row, exact bars, rolling states, weak2 triggers, option selections, OI-gate decisions and H5 outcomes must be byte/field equivalent.

## Validated V1 market clock

At 09:30 IST, after observing NIFTY futures from 09:15 to 09:30:

```text
EstimatedDayVolume
= 1,228,063.4608804993
+ 4.699614102486805 * OpeningVolume0930
```

Then:

```text
RawBaseBarVolume = EstimatedDayVolume / 160
BaseBarVolume = round to nearest 50 exchange lots
RollingStateVolume = BaseBarVolume * 10
```

V1 freezes `BaseBarVolume` for the entire session.

The 09:15–09:30 ticks are replayed using the selected threshold to initialize state, but no adaptive weak2 trigger before 09:30 is actionable.

## Exact market-state semantics

Do not reuse the existing production `VolumeBarBuilder` for this observer.

The research definition requires:
- each complete base bar has exactly the configured traded volume;
- crossing cumulative-volume updates are split across exact bars;
- strict buyer/seller aggressor flow is quote-derived;
- enriched tick-rule fallback remains a separate diagnostic;
- OI is point-in-time and split-volume safe;
- rolling state is the latest 10 complete exact adaptive bars;
- incomplete end-of-day bars are never silently treated as complete state bars.

Strong-state threshold:
- calculated only from completed prior sessions;
- frozen for the current day;
- current session cannot influence its own threshold.

Weak2:
- strong direction-aligned rolling state;
- then two same-sign `Weakening` transitions;
- all event fields persisted, including rejected events.

## Option observation semantics

At each weak2 trigger:
- old bullish dominance -> reversal side PE;
- old bearish dominance -> reversal side CE;
- nearest weekly expiry;
- executable ask must be in ₹100–₹150;
- choose ask closest to ₹125, deterministic tie-break;
- selected strike is fixed for the observation;
- evaluate CE and PE OI at the same strike;
- OI gate:
  `CE_OI(trigger) + PE_OI(trigger) > CE_OI(strong-base) + PE_OI(strong-base)`;
- persist accepted and rejected observations;
- record hypothetical ask entry and H5 executable bid outcome, MFE and MAE;
- observation only until a separate paper-trading decision.

## Persistence / read model

Keep the adaptive observer separate from the legacy 2,600-volume pipeline.

Suggested concepts:
- `AdaptiveSessionState`
- `AdaptiveExactFlowBar`
- `AdaptiveRollingMarketState`
- `AdaptiveWeak2Observation`

All rows carry:
- strategy/model version;
- build Git SHA where applicable;
- timestamps;
- deterministic event identity.

The database is the source of truth. The dashboard never reconstructs strategy logic.

## Dashboard rewrite

The existing top-of-page operational area remains intact:
- existing application/header row;
- access-token/authentication bar and controls;
- existing Live Quote / live price-tick section;
- no existing live price-tick display is removed as part of the observer redesign.

The new adaptive observer content is inserted below those existing rows.

The initial deployment is **watch-only**. No adaptive paper-trade engine, order-entry action, strategy kill-switch, or execution control is introduced until a later explicit paper-trading phase.

The new observer body uses three synchronized grids, all keyed to the same completed adaptive futures `BarSeq` and futures bar timestamps:
1. Futures market-state grid;
2. CE/PE ATM±2 option-band grid;
3. 09:30 theoretical CE/PE residual grid.

One global row-count selector controls all three grids:
`5 | 10 | 15`, default `10`.

The dashboard target is sub-second perceived/update latency after persisted state becomes available. Dashboard rendering must not perform strategy calculations or scan raw tick history.

Primary sections:

1. Runtime/build status
   - feed status and tick age;
   - Git branch, commit SHA, build UTC;
   - observer model version;
   - restart/rebuild status.

2. Adaptive session clock
   - opening 15-minute volume;
   - expected day volume;
   - actual volume so far;
   - base-bar volume;
   - rolling 10-bar volume;
   - completed bars;
   - current in-progress bar percentage;
   - strong threshold.

3. Futures market-state grid
   - last 5/10/15 completed adaptive futures bars;
   - BarSeq, End IST, duration, price displacement;
   - strict/enriched aggressor flow;
   - rolling 10-bar market state;
   - OI, efficiency, return, activity and quality metrics;
   - explicit Normal / Strong / Weak1 / Weak2 state.

4. CE/PE option-band grid
   - uses the exact same futures adaptive bar start/end timestamps; options never create their own clock;
   - side selector: BOTH / CE / PE, default BOTH;
   - measurement selector: NOTIONAL / CONTRACT, default NOTIONAL;
   - ATM±2 weekly-option strikes selected causally at the start of each adaptive futures bar and frozen for that bar;
   - both contract and premium-notional quantities persisted, never browser-recomputed;
   - strict and enriched trade classification retained separately with strict coverage exposed;
   - center strike and band-roll indicator persisted.

5. 09:30 theoretical residual grid
   - fixed diagnostic composition for the whole session, selected once at 09:30;
   - persist both the original fixed single-ATM diagnostic and the fixed ATM±2 diagnostic basket for research continuity;
   - dashboard defaults to the fixed ATM±2 basket because it is less dependent on one option quote while remaining directly comparable with the single-strike research;
   - optional view selector may expose `ATM | ATM±2` without recalculation;
   - actual CE/PE basket price change, constant-09:30-IV expected change, residuals, directional residual, residual change, rolling-futures relationship and quote age;
   - no residual-derived entry rule in watch-only V1.

6. Main futures/state chart
   - futures price;
   - exact adaptive bar boundaries;
   - rolling strict delta ratio;
   - positive/negative strong thresholds;
   - strong, weak1 and weak2 markers;
   - OI-accepted and OI-rejected trigger markers.

7. Current market-state panel
   - price direction;
   - strict delta and ratio;
   - strict quote coverage;
   - dominance/evolution;
   - weakening count;
   - rolling duration and activity.

8. Current reversal observation
   - reversal direction;
   - fixed CE/PE strike;
   - bid/ask/spread;
   - CE/PE OI at strong-base and trigger;
   - pair-OI change and accepted/rejected status;
   - H5 progress/outcome.

9. Today's observations
   - all accepted and rejected weak2 events;
   - H5 outcome;
   - hypothetical P&L;
   - MFE/MAE;
   - data-quality fields.

10. Data quality
   - futures tick lag;
   - option quote/OI age;
   - strict classification coverage;
   - unknown-flow share;
   - duplicate/reset counters;
   - source-tick checkpoint.

Legacy operational functionality remains under `/legacy` during the watch-only cutover.

## Deployment traceability

Before this observer is deployed, add immutable build metadata:
- source branch;
- commit SHA;
- build UTC;
- model/strategy version.

Expose it:
- in application startup logs;
- in the dashboard header;
- optionally in a small persisted/runtime health record.

`deploy.ps1` currently publishes whichever checkout invoked it and does not prove which Git revision is running. This ambiguity must be eliminated before the adaptive observer becomes the production baseline.

## Implementation phases and gates

### Phase 0 — production/source-control baseline — **IMPLEMENTED / UNVALIDATED**
- branch from `master`;
- add build metadata;
- establish deploy/revision visibility.

Gate: running revision is unambiguous. Build metadata and the full solution build pass; verification of the actual deployed SHA is pending.

### Phase 1 — exact research semantics in NSE — **IMPLEMENTED / PARITY PENDING**
Port/reuse exact tick cleaning, strict/enriched classification, exact-volume splitting and rolling-state logic.

Gate: golden historical replay matches NiftyResearcher bar-for-bar and state-for-state. Direct independent library comparisons passed; integration gates remain pending.

### Phase 2 — adaptive session coordinator — **IMPLEMENTED / PARITY PENDING**
Implement 09:30 V1 estimation, daily immutable session row and prior-session strong threshold.

Gate: historical session thresholds match NiftyResearcher exactly. Discovery OOF thresholds are frozen; exact replay parity remains pending.

### Phase 3 — incremental live + restart recovery — **IMPLEMENTED / TEST PENDING**
Consume new ticks incrementally while retaining deterministic replay/recovery.

Gate: restart-parity tests at arbitrary points produce identical results to uninterrupted replay. Automated artificial restart and buffer/persistence tests passed; full Host restart across mutable universe/session close remains pending.

### Phase 4 — weak2 state lifecycle — **IMPLEMENTED / PARITY PENDING**
Persist strong/weak1/weak2 transitions.

Gate: historical trigger identities/times match research exactly. Direct overlapping three-row semantics are implemented; direct-library historical identities passed; persisted Host observation identities remain under validation.

### Phase 5 — option selection and CE/PE OI gate — **IN PROGRESS**
Implement ₹100–₹150 fixed-strike selection and pair-OI expansion.

Gate: historical accepted/rejected observations match frozen research. Independent overlapping observations passed all 13 discovery sessions at e91d1ca; final strengthened restart/release gates remain pending.

### Phase 6 — H5 observation lifecycle — **IN PROGRESS**
Persist hypothetical executable entry, H5 bid exit, MFE/MAE.

Gate: historical simulation outcomes reproduce. Historical H5/PnL/MFE/MAE comparisons passed all 13 discovery sessions at e91d1ca. Final strengthened restart gate remains pending.

### Phase 7 — dashboard replacement — **IMPLEMENTED / RUNTIME VALIDATION PENDING**
Build the new observer UI entirely from persisted/read-model state.

Gate: dashboard performs no trading calculations and survives dashboard/Host restart independently. Three grids/read service are implemented; build/runtime/performance validation remains pending.

### Phase 8 — live observation — **PENDING**
Deploy with all order/paper-trade actions disabled.

Gate: collect live sessions and compare accepted versus rejected behavior prospectively. No VM deployment has occurred.

### Phase 9 — paper-trading decision — **PENDING / OUT OF CURRENT SCOPE**
Only after prospective observation is accepted.

## Legacy cleanup

Maintenance cleanup is part of the migration, but should happen after the adaptive observer has historical parity and initial live stability.

Do not delete old code before it has served as a rollback/reference path.

Cleanup sequence:
1. inventory legacy Host engines, score models, tables, Razor panels, config sections, migrations and tests;
2. classify each as:
   - still operational infrastructure;
   - required historical/schema compatibility;
   - genuinely unused/retired;
3. remove retired runtime registrations first;
4. remove retired Dashboard panels/routes/services;
5. remove dead configuration/options;
6. remove unreachable score/trading code and its tests;
7. keep migrations already applied to production history even when their model is retired unless a deliberate schema-cleanup migration is made;
8. simplify deployment and documentation;
9. run full build/test + historical adaptive parity + restart parity after each cleanup batch.

Likely legacy candidates must be verified by current references before deletion; no class is removed merely because its name belongs to an older strategy.

## Change discipline

- No composite score is introduced into the adaptive weak2 research.
- No new historical filter is added merely to improve backtest P&L.
- Any new metric discovered during observation is persisted/visualized first and tested separately.
- Production behavior changes only after deterministic historical parity and restart parity.
