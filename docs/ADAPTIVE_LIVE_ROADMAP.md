# Adaptive Market Observer V1 — Live Binding Roadmap

## Base and operating principle

- Canonical implementation base: `master`.
- New work must start from a fresh feature branch based on `master`.
- The current `NiftyRatio` branch is treated as research/development history, not the production source of truth.
- The new adaptive system is observation-only first. No paper trading or real order routing is enabled as part of the initial live binding.
- The implementation must reproduce the validated NiftyResearcher semantics exactly before any live result is trusted.
- Dashboard code is a reader only. Market-state calculations belong in Host/domain services and are persisted.

## Implementation status — 2026-10-05

### Verified checkpoint and release boundary

- Feature: `feature/adaptive-market-observer-v1`, branched from `master`.
- Public gate: [37332433765](https://github.com/neevanapps/NSE/actions/runs/37332433765), SHA `64ece10ac1928d7a6bf43c14666aef70331dd0cd`: complete Release solution build, zero warnings/errors, **48 adaptive / 1,123 full tests**; PostgreSQL migrations and model drift; seven relational recovery boundaries; real Chromium selectors, blocked-reader race, page reload and legacy route; assembly SHA verification.
- Frozen historical gate: [37330577623](https://github.com/neevanapps/NiftyResearcher/actions/runs/37330577623), production-calculation SHA `a43ca3cab9ed3a335022e4d927e2c28f8de58e0b`, reference research `e768b8e5b87938fb7be9531310f1c6f786853301`. **All 13 discovery sessions pass**: 2,034 complete futures bars / 3,905,218 core fields; 235,344 independent band/rolling fields; 97,767 actual source/bootstrap persisted rolling fields; 3,641 residual rows / 40,051 fields; 125 overlapping executable H5 observations plus one unavailable selection; 87 historical engine resets. No sealed dates used.
- Historical tolerances remain declared: residual numeric error <=1e-6 points (observed maximum <5e-11); observation price/OI <=1e-9; Python serialized observation timestamps <=1 microsecond. Exact identities, counts, classifications and selected strikes must agree.
- Grid 2 is a new live-binding view: its independent oracle uses original research cleaner/classifier and separate causal selection/aggregation. It is not claimed to be an identical pre-existing research Dashboard.
- CI commit-to-three-grids plus two animation frames: 20 samples, P95 **158.8837 ms**, maximum **181.7617 ms**. This measures isolated PostgreSQL/Chromium, **not VM latency** and not feed-arrival latency.
- Final feature gate **PASS**: [37335634158](https://github.com/neevanapps/NSE/actions/runs/37335634158), SHA `22592b07fb48f2c81a27e65bf407e7f78b44d9e3`. Distinct CE/PE, contract/notional, ATM/band and unavailable-row assertions pass; both Host/Dashboard publish successfully. 48 adaptive / 1,123 total tests; seven PostgreSQL recovery boundaries; P95 189.0 ms / max 224.4 ms. Exact merged master verification is the remaining automated release gate.
- Current-build traceability F72 **PASS** at `b40851c48afddcc5b693d5ee75b15a24738e23dc`, [37354356288](https://github.com/neevanapps/NSE/actions/runs/37354356288): current Dashboard full SHA/branch independently asserted; session provenance separately labeled; 48 adaptive / 1,123 full tests, migration/restart/browser and publish pass; P95 142.0 ms / max 144.8 ms. No calculation changes.
- Final merge SHA and exact master CI result are recorded in [release PR #3](https://github.com/neevanapps/NSE/pull/3) after completion. The badge below is live workflow status, not a claim about VM deployment.
- **VM deployment is manual by the user with the existing `deploy.ps1`.** VM service/DB permissions, source-history completeness, deployed SHA, prospective observation and VM latency are not validated by CI.
- No adaptive or legacy paper executor is hosted. Legacy source/schema and `/legacy` remain for compatibility/rollback.

Status terminology: **VALIDATED** means the stated gate passed in the stated environment; **IMPLEMENTED / UNVALIDATED** means code exists without that gate; **PENDING** means not yet performed. A CI pass never implies a VM pass.

### Overall status

| Workstream | Status | Evidence / remaining boundary |
|---|---|---|
| Branch isolation | VALIDATED | Fresh branch from master. |
| Build/SHA traceability | VALIDATED in CI | F72 verifies current Dashboard full SHA/branch, separate from immutable session provenance; Host startup logs identify current build. VM SHA check remains manual. |
| Exact futures / 09:30 estimator / Strong–Weak1–Weak2 | VALIDATED | All 13 frozen discovery sessions, source/bootstrap and thresholds. |
| CE/PE ATM±2 contract/notional flow and rolling metrics | VALIDATED | Independent causal band oracle across all 13 sessions. |
| Fixed 09:30 ATM and ATM±2 residuals | VALIDATED | Complete integrated historical replay, availability and numeric parity. |
| Adaptive schema and EF migrations | VALIDATED | Eight tables, eight unique indexes, seven restrictive session FKs; PostgreSQL 17 apply/model drift pass. |
| ₹100–₹150 fixed selection, CE+PE OI, H5/MFE/MAE | VALIDATED | Independent overlapping historical observation reference. |
| Restart reconstruction | VALIDATED | 87 historical engine resets, seven actual PostgreSQL recovery boundaries, frozen-universe/after-hours unit tests, corruption rejection. This is deterministic process-state reconstruction, not a Windows-service crash experiment. |
| Dashboard three synchronized grids, selectors, partial bar | VALIDATED in CI | 36 combinations, blocked-reader race, reader reload, unavailable rows, legacy route. Distinct-value and unavailable-row assertions passed at 22592b0. |
| Header/access-token/Live Quote retention | IMPLEMENTED; source reviewed | Existing components retained. Real broker-authenticated quote behavior requires VM observation. |
| Sub-second commit-to-visible Dashboard | VALIDATED in CI | Latest feature P95 142 ms / max 145 ms; VM measurement pending. |
| Legacy runtime paper-worker removal | VALIDATED | Hosted call-site review and full regression; other legacy source retained. |
| Final diff review / publish gate | VALIDATED | Full 82-file scope reviewed; F63–F72 repaired; final publish run passed. No adaptive calculation change since historical-tested a43ca3c. |
| Merge and exact master CI | [![Master validation](https://github.com/neevanapps/NSE/actions/workflows/adaptive-observer-validation.yml/badge.svg?branch=master)](https://github.com/neevanapps/NSE/actions/workflows/adaptive-observer-validation.yml?query=branch%3Amaster) | Baseline merged in PR #2; final release SHA and exact green run recorded in PR #3. Deploy only that verified SHA. |
| VM deployment / live observation | PENDING — USER MANUAL | Follow ADAPTIVE_VM_HANDOFF.md after exact master CI passes. |
| Paper trading / orders | OUT OF V1 | Deliberately excluded. |

### Follow-on: Telegram Dashboard screenshots

Implementation is on fresh master-based `feature/adaptive-telegram-screenshots`; release evidence is recorded in [PR #4](https://github.com/neevanapps/NSE/pull/4). It adds the 09:30 ready-state image and one image every five completed adaptive bars after that boundary, without changing the validated adaptive calculations. [Screenshot configuration and morning acceptance](ADAPTIVE_TELEGRAM_SCREENSHOTS.md) covers the separate Dashboard bot settings, generated outbox migration, browser installation and pre-live test.

Screenshot implementation, migration/regression and browser/restart gates are tracked separately from manual VM deployment and real Telegram delivery. The VM gates remain **PENDING** until performed; green CI is not a substitute.

### Adaptive release / manual deployment sequence

1. **DONE:** final distinguishable Dashboard fixture and Host/Dashboard publish gate.
2. **DONE:** release diff review and final feature evidence recorded.
3. Baseline merge **DONE** (PR #2). Final traceability merge and immutable release SHA recorded in [PR #3](https://github.com/neevanapps/NSE/pull/3).
4. Final release gate: verify build, full regression, migrations, restart/browser checks and publish outputs at the exact final master SHA. The live badge and PR #3 carry completion evidence without changing release source merely to update a status.
5. User deploys that SHA with `deploy.ps1 -Target Vm -Service Both -ExpectedCommitSha <sha>`.
6. Verify VM services, logs, migration/bootstrap, build metadata and live grid behavior; measure VM latency and begin watch-only prospective observation.

Historical parity establishes implementation consistency with frozen research. It does not establish a profitable trading strategy or prospective performance.

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

The frozen V1 delivery is the operational header plus three synchronized grids and a separate partial bar. The following broader design inventory is retained for later UI work; the main chart, dedicated observation list and separate quality panels are **not delivered or claimed validated** by this cutover:

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

`deploy.ps1` requires a clean checkout, verifies optional `ExpectedCommitSha`, requires `master` for that release mode, tests/publishes an immutable tracked archive, and embeds branch/SHA/build UTC. VM metadata still requires checking after deployment.

## Implementation phases and gates

### Phase 0 — production/source-control baseline — **VALIDATED IN CI / HISTORICAL GATES**
- branch from `master`;
- add build metadata;
- establish deploy/revision visibility.

Gate: assembly source metadata verified in CI; exact deployed VM metadata remains a manual release check.

### Phase 1 — exact research semantics in NSE — **VALIDATED IN CI / HISTORICAL GATES**
Port/reuse exact tick cleaning, strict/enriched classification, exact-volume splitting and rolling-state logic.

Gate: all 13 discovery sessions match original research; actual source/bootstrap and independent band policy checks passed.

### Phase 2 — adaptive session coordinator — **VALIDATED IN CI / HISTORICAL GATES**
Implement 09:30 V1 estimation, daily immutable session row and prior-session strong threshold.

Gate: all original Python daily discovery thresholds and persisted source/bootstrap fields match.

### Phase 3 — incremental live + restart recovery — **VALIDATED IN CI / HISTORICAL GATES**
Consume new ticks incrementally while retaining deterministic replay/recovery.

Gate: 87 historical engine resets and seven PostgreSQL recovery boundaries passed; completed projection corruption fails closed. Actual Windows-service restart remains a VM check.

### Phase 4 — weak2 state lifecycle — **VALIDATED IN CI / HISTORICAL GATES**
Persist strong/weak1/weak2 transitions.

Gate: historical trigger identities, overlapping windows and persisted restart identity reconciliation passed.

### Phase 5 — option selection and CE/PE OI gate — **VALIDATED IN CI / HISTORICAL GATES**
Implement ₹100–₹150 fixed-strike selection and pair-OI expansion.

Gate: independent fixed-strike selection, OI accepted/rejected and unavailable outcomes passed all 13 discovery sessions.

### Phase 6 — H5 observation lifecycle — **VALIDATED IN CI / HISTORICAL GATES**
Persist hypothetical executable entry, H5 bid exit, MFE/MAE.

Gate: independent H5/PnL/MFE/MAE reference and persisted recovery passed.

### Phase 7 — dashboard replacement — **VALIDATED IN CI / HISTORICAL GATES**
Build the new observer UI entirely from persisted/read-model state.

Gate: persisted reader and synchronized selectors, blocked-read concurrency, page reload and latency passed in real Chromium. Broker-authenticated Live Quote and VM timing require manual observation.

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
