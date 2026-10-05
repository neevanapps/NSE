# Adaptive Market Observer — Host Implementation Contract

## Status

Frozen design contract before production implementation.

Base branch: `master`.

First live release: **watch-only**.

No adaptive paper-trade engine or order-routing logic is included in this contract.

The implementation must reproduce the validated `NiftyResearcher` adaptive-V1 research semantics before it is allowed to run live.

---

# 1. Project boundaries

Create two new projects.

## 1.1 `NiftySignal.AdaptiveObserver`

Purpose: deterministic calculation library only.

Allowed dependencies:
- `NiftySignal.Domain`
- `NiftySignal.Pricing`

Must NOT depend on:
- EF Core
- Host
- Dashboard
- SignalR
- trading/execution projects
- legacy score projects

Primary components:

### `AdaptiveTickCleaner`
Ports the research TickCleaner semantics exactly:
- de-duplicate only consecutive rows whose market payload is identical;
- duplicate comparison excludes database Id and ReceivedAt;
- `AvailableAt = max(ReceivedAt, ExchangeTimestamp)`;
- deterministic ordering is `AvailableAt, Tick.Id`.

For observer calculations only top-of-book values are required:
- LastPrice
- Bid1Price / Ask1Price
- Bid1Qty / Ask1Qty
- cumulative Volume
- OpenInterest.

### `AdaptiveTradeFlowEnricher`
Ports `Phase1TickEnricher` exactly.

Per instrument:
- first cumulative-volume observation establishes the baseline;
- traded quantity exists only when cumulative volume exceeds the previous session maximum;
- cumulative-volume decreases do not create negative volume;
- strict aggressor classification:
  - previous two-sided quote if available;
  - otherwise current two-sided quote;
  - Last >= Ask => Buy;
  - Last <= Bid => Sell;
  - otherwise Unknown;
- enriched classification:
  - strict result first;
  - tick-direction fallback only when strict is Unknown;
- preserve strict Unknown separately.

### `ExactAdaptiveFuturesBarBuilder`
Ports `ExactVolumeBarBuilder` semantics exactly.

Rules:
- crossing traded-volume updates are split;
- every persisted completed futures bar has exactly `BaseBarVolume`;
- partial end-of-day bar is never part of the rolling market-state window;
- one source trade update may contribute chunks to more than one exact bar;
- OI is point-in-time and must remain split-volume safe;
- source tick Id range is persisted.

### `AdaptiveRollingStateTracker`
10 completed adaptive futures bars exactly.

Ports `MarketStateWindowBuilder` semantics:
- WindowBars = 10;
- WindowVolume = BaseBarVolume × 10;
- PriceDisplacement = last close - first open;
- ReturnBps;
- PathLength;
- Efficiency = abs(displacement) / path length, capped at 1;
- strict/enriched buy, sell, unknown, delta and ratios;
- strict quote coverage;
- fallback share;
- OI start/end/change/change%;
- elapsed seconds;
- volume/second;
- trade updates;
- price / strict / enriched direction;
- agreement flags.

### `AdaptiveFlowEvolutionTracker`
Ports `FlowEvolutionBuilder` semantics:
- RollingStrictDeltaChange;
- RollingStrictAbsDeltaChange;
- RollingEnrichedDeltaChange;
- RollingOiChangePctChange;
- StrictDominanceEvolution:
  - BuyerDominanceStarted
  - SellerDominanceStarted
  - DominanceNeutralized
  - FlipToBuyer
  - FlipToSeller
  - Strengthening
  - Weakening
  - Unchanged.

### `AdaptiveStrongStateEvaluator`
Strong state definition:
- session threshold = causal prior-session 66.7th percentile of absolute rolling strict delta ratio;
- threshold is calculated once for the day and frozen;
- current session is never included in its own threshold;
- Strong requires:
  - abs(RollingStrictDeltaRatioTotal) >= frozen threshold;
  - RollingPriceDirection == RollingStrictDeltaDirection.

### `Weak2StateMachine`
Frozen Weak2 definition:
1. strong, direction-aligned state;
2. next rolling state = Weakening;
3. same strict-delta direction and same rolling price direction;
4. following rolling state = Weakening;
5. same strict-delta direction and same rolling price direction;
6. trigger is the second weakening bar.

Persist all strong-base, weak1 and weak2 bar identities.

`StrictNoTurn` may be retained as a diagnostic field only. It is not a V1 gate.

### `WeeklySyntheticUnderlyingCalculator`
Uses same-expiry CE/PE parity and existing `NiftySignal.Pricing.SyntheticForward` behavior.

Used for:
- Grid 2 causal ATM-band center at each adaptive-bar start;
- Grid 3 fixed 09:30 diagnostic center.

### `AdaptiveOptionBandSelector`
Grid 2 semantics.

At each adaptive futures-bar START:
- calculate latest valid weekly synthetic underlying;
- select nearest listed ATM;
- select ATM±2;
- freeze the five strikes for the whole futures-bar interval;
- CE and PE use the same center and strike band;
- next futures bar may roll.

If a valid synthetic level cannot be calculated from sufficient fresh pairs, the option-band row for that interval is marked unavailable. Do not silently use a stale previous center.

Persist:
- center strike;
- all five strikes;
- whether band changed from prior adaptive bar.

### `AdaptiveOptionFlowAccumulator`
Treat every option contract as an instrument with the same cumulative-volume + strict/enriched classification semantics as futures.

The option clock is ALWAYS the adaptive futures bar's exact StartAvailableAt / EndAvailableAt.

Maintain per-token state all session so cumulative-volume baseline, previous quote and tick direction remain correct when a token enters/leaves the current ATM±2 band.

For each CE and PE side separately, aggregate five selected strikes.

Persist BOTH representations:

#### Contract/quantity representation
- strict buy quantity
- strict sell quantity
- strict unknown quantity
- strict delta
- strict ratio total
- strict coverage
- enriched buy/sell/unknown
- enriched delta
- enriched ratio
- trade updates.

#### Premium-notional representation
For each classified traded quantity chunk:
```text
premium_notional = observed_option_trade_price × traded_quantity
```

Persist strict/enriched buy/sell/unknown notional, delta, ratios and activity.

FlatTrade `Volume` is treated as cumulative traded quantity, not "lots". Preserve:
- raw traded quantity;
- instrument LotSize;
- optional derived lots for display/audit.

Do not multiply by LotSize again when raw Volume is already exchange quantity.

### `AdaptiveOptionBandRollingTracker`
Rolling window uses the last 10 FUTURES adaptive intervals.

Because Grid 2 composition can change between bars:
- `RollingBandPriceChange = sum(last 10 BarBandPriceChange)`;
- never subtract a current five-strike basket from a different basket ten bars earlier.

Aggregate:
- contract strict/enriched flow;
- notional strict/enriched flow;
- quote coverage;
- OI;
- duration;
- activity/second;
- trade updates;
- price-change path/efficiency using per-bar band changes.

### `OptionResidualAnchorBuilder`
At 09:30:
- use latest valid quotes at or before 09:30;
- quote max age for research parity: 5 seconds;
- derive synthetic weekly underlying using parity across the five nearest candidate strikes;
- select nearest ATM center;
- freeze ATM±2 for the whole day;
- capture each CE/PE component midpoint;
- solve each component's 09:30 IV;
- persist component anchors.

The fixed center/band is never rolled intraday.

### `OptionResidualCalculator`
For every completed adaptive futures bar:

```text
ModeledWeeklyUnderlying
= SyntheticWeeklyUnderlying0930
+ (CurrentMonthlyFuture - MonthlyFuture0930)
```

For every fixed diagnostic option component:
- 09:30 IV remains constant;
- time-to-expiry updates continuously;
- use existing Black-Scholes implementation;
- Expected = constant-IV theoretical price;
- Residual = actual midpoint - expected.

Persist both diagnostic variants:
- ATM single-strike;
- ATM±2 basket.

Basket residual:
- CE actual/expected = sum of five CE components;
- PE actual/expected = sum of five PE components;
- normalize each side by its own 09:30 basket premium;
- `DirectionalResidualPct = CEResidualPct - PEResidualPct`;
- `ResidualDelta = current directional residual - previous bar directional residual`;
- relationship to rolling futures direction = ALIGN / OPPOSE / NEUTRAL.

No residual value is a V1 trade gate.

### `Weak2ObservationTracker`
Watch-only observation lifecycle.

At each weak2:
- old bullish dominance => reversal side PE;
- old bearish dominance => reversal side CE;
- find nearest weekly expiry;
- scan reversal-side contracts whose executable Ask at/after trigger is ₹100–₹150;
- deterministic selection = Ask closest to ₹125, tie lower strike;
- freeze selected strike for observation;
- evaluate CE and PE OI at the SAME strike:
  `PairOITrigger > PairOIStrongBase`;
- persist ACCEPTED / REJECTED;
- capture current residual diagnostics but do not gate on them;
- H5 target = fifth subsequent adaptive futures bar;
- hypothetical entry = executable ask;
- hypothetical exit = executable H5 bid;
- MFE/MAE = executable bid path during observation.

This remains an observation, not a paper trade.

---

# 2. New persistence project/database

Create:

`NiftySignal.AdaptiveObserverData`

with:
- EF Core entities;
- `AdaptiveObserverDbContext`;
- migrations;
- no strategy calculation logic.

Physical PostgreSQL database:

`niftysignal_adaptive_observer`

Reason:
- raw ticks remain cleanly owned by `NiftySignalDb`;
- legacy 2,600-volume research/live tables remain isolated in the existing volume-bar database;
- adaptive V1 can be maintained and eventually become the clean production observer schema without carrying retired score/trade tables.

Host writes this database.
Dashboard reads it with `IDbContextFactory<AdaptiveObserverDbContext>`.

---

# 3. Database entities and identities

All strategy/state tables carry:
- `ModelVersion`, initially `adaptive-v1`;
- deterministic session identity;
- timestamps in UTC;
- no browser-generated values.

## 3.1 `AdaptiveSessionStateRow`

One frozen row per:
`(TradeDate, ModelVersion)`.

Unique index:
`TradeDate, ModelVersion`.

Fields:
- Id
- TradeDate
- ModelVersion
- SourceBranch
- SourceCommitSha
- BuildUtc
- IsHistoricalSeed
- FutureToken
- FutureSymbol
- FutureExpiry
- LotSize
- OpeningWindowStartUtc
- OpeningWindowEndUtc
- OpeningVolume
- EstimatorName
- EstimatorIntercept
- EstimatorSlope
- TargetBarsPerDay
- RoundingLots
- EstimatedFullDayVolume
- BaseBarVolume
- RollingWindowBars (=10)
- RollingWindowVolume
- StrongQuantile (=2/3)
- StrongThreshold
- StrongThresholdPriorStateCount
- WeeklyOptionExpiry
- Future0930
- SyntheticWeeklyUnderlying0930
- ResidualCenterStrike
- ResidualAnchorCompletedAtUtc
- CreatedAtUtc.

Once the 09:30 session row is committed, its calculation-defining fields are immutable for the trading day.

## 3.2 `AdaptiveFutureBarRow`

One row per completed exact adaptive futures bar.

Unique:
`SessionId, BarSeq`.

Indexes:
- `SessionId, BarSeq DESC`
- `SessionId, EndAvailableAtUtc`.

Fields:
- Id
- SessionId
- BarSeq
- StartAvailableAtUtc
- EndAvailableAtUtc
- FirstSourceTickId
- LastSourceTickId
- DurationSeconds
- Open/High/Low/Close
- BarPriceDisplacement
- Vwap
- Volume (must equal session BaseBarVolume)
- TradeUpdates
- StrictBuyVolume
- StrictSellVolume
- StrictUnknownVolume
- StrictDelta
- StrictDeltaRatioTotal
- StrictCoverage
- EnrichedBuyVolume
- EnrichedSellVolume
- EnrichedUnknownVolume
- EnrichedDelta
- EnrichedDeltaRatio
- OiOpen
- OiClose
- OiChange
- Bid/Ask
- BidQty/AskQty
- Spread
- BookImbalance
- Microprice.

Do not persist an incomplete bar in this table.

## 3.3 `AdaptiveRollingStateRow`

One row for every completed future bar starting at BarSeq 10.

Unique:
`SessionId, EndBarSeq`.

Index:
`SessionId, EndBarSeq DESC`.

Fields:
- Id
- SessionId
- StartBarSeq
- EndBarSeq
- StartAvailableAtUtc
- EndAvailableAtUtc
- WindowBars
- WindowVolume
- ElapsedSeconds
- StartPrice
- EndPrice
- High
- Low
- PriceDisplacement
- ReturnBps
- PathLength
- Efficiency
- SignedEfficiency
- StrictBuyVolume
- StrictSellVolume
- StrictUnknownVolume
- StrictDelta
- StrictQuoteCoverage
- StrictDeltaRatioTotal
- StrictDeltaRatioClassified
- EnrichedBuyVolume
- EnrichedSellVolume
- EnrichedUnknownVolume
- EnrichedDelta
- EnrichedDeltaRatio
- FallbackShare
- OiStart
- OiEnd
- OiChange
- OiChangePct
- VolumePerSecond
- TradeUpdates
- WindowRange
- PriceDirection
- StrictDeltaDirection
- EnrichedDeltaDirection
- PriceStrictDeltaAgree
- PriceEnrichedDeltaAgree
- RollingStrictDeltaChange
- RollingStrictAbsDeltaChange
- RollingEnrichedDeltaChange
- RollingOiChangePctChange
- StrictDominanceEvolution
- IsStrong
- WeakeningSequence (0/1/2)
- State (Normal/Strong/Weak1/Weak2).

## 3.4 `AdaptiveOptionBandBarRow`

One row per:
`SessionId, BarSeq, Side`.

Side = CE or PE.

Unique:
`SessionId, BarSeq, Side`.

Index:
`SessionId, Side, BarSeq DESC`.

Band fields:
- CenterStrike
- StrikeMinus2
- StrikeMinus1
- StrikeAtm
- StrikePlus1
- StrikePlus2
- BandRolled
- BandAvailable
- UnavailableReason
- StartAvailableAtUtc
- EndAvailableAtUtc
- DurationSeconds.

Price:
- BandPremiumIndexOpen
- BandPremiumIndexClose
- BarBandPriceChange
- BarBandReturnPct
- RollingBandPriceChange
- RollingEfficiency.

Contract/quantity flow:
- ContractTotalQuantity
- ContractStrictBuy
- ContractStrictSell
- ContractStrictUnknown
- ContractStrictDelta
- ContractStrictDeltaRatioTotal
- ContractStrictCoverage
- ContractEnrichedBuy
- ContractEnrichedSell
- ContractEnrichedUnknown
- ContractEnrichedDelta
- ContractEnrichedDeltaRatio
- ContractRollingStrictDelta
- ContractRollingStrictDeltaRatioTotal
- ContractRollingEnrichedDelta
- ContractRollingEnrichedDeltaRatio.

Premium-notional flow:
- NotionalTotal
- NotionalStrictBuy
- NotionalStrictSell
- NotionalStrictUnknown
- NotionalStrictDelta
- NotionalStrictDeltaRatioTotal
- NotionalStrictCoverage
- NotionalEnrichedBuy
- NotionalEnrichedSell
- NotionalEnrichedUnknown
- NotionalEnrichedDelta
- NotionalEnrichedDeltaRatio
- NotionalRollingStrictDelta
- NotionalRollingStrictDeltaRatioTotal
- NotionalRollingEnrichedDelta
- NotionalRollingEnrichedDeltaRatio.

Quality/activity:
- BarTradeUpdates
- RollingTradeUpdates
- RollingQuoteCoverage
- RollingActivityPerSecond.

OI:
- BandOiOpen
- BandOiClose
- BarOiChange
- RollingOiChange
- RollingOiChangePct
- PremiumNotionalOiAtClose
- PremiumNotionalOiChange.

## 3.5 `AdaptiveResidualAnchorComponentRow`

One component row per:
`SessionId, Side, Strike`.

The five ATM±2 components are stored once. The center component also serves the single-ATM diagnostic.

Unique:
`SessionId, Side, Strike`.

Fields:
- Id
- SessionId
- Side
- Strike
- Token
- TradingSymbol
- IsCenterStrike
- Price0930
- ImpliedVolatility0930
- QuoteTimestampUtc
- QuoteAgeSeconds.

## 3.6 `AdaptiveOptionResidualBarRow`

One row per:
`SessionId, BarSeq, Variant`.

Variant:
- ATM
- ATM_PLUS_MINUS_2.

Unique:
`SessionId, BarSeq, Variant`.

Index:
`SessionId, Variant, BarSeq DESC`.

Fields:
- Id
- SessionId
- BarSeq
- Variant
- EndAvailableAtUtc
- CenterStrike
- FutureChangeFrom0930
- ModeledWeeklyUnderlying
- CEActual
- CEExpected
- CEActualChange
- CEExpectedChange
- CEResidual
- CEResidualPct
- PEActual
- PEExpected
- PEActualChange
- PEExpectedChange
- PEResidual
- PEResidualPct
- DirectionalResidualPct
- ResidualDelta
- ResidualDirection
- FuturesRollingDirection
- Relationship
- CommonResidualPct
- MaxQuoteAgeSeconds
- IsAvailable
- UnavailableReason.

## 3.7 `AdaptiveWeak2ObservationRow`

Unique:
`SessionId, TriggerBarSeq`.

Indexes:
- `SessionId, TriggerBarSeq DESC`
- `SessionId, Status`.

Fields:
- Id
- SessionId
- StrongBaseBarSeq
- Weak1BarSeq
- TriggerBarSeq
- OldTrendDirection
- ReversalDirection
- StrictNoTurnDiagnostic
- TriggerTimestampUtc
- OptionSide
- ExpiryDate
- Strike
- Token
- TradingSymbol
- EntryAsk
- EntryBid
- EntryQuoteTimestampUtc
- EntryQuoteLatencySeconds
- CEOiStrongBase
- PEOiStrongBase
- CEOiTrigger
- PEOiTrigger
- PairOiStrongBase
- PairOiTrigger
- PairOiChange
- PairOiChangePct
- OiGatePassed
- ATMResidualDirectionalPct
- BandResidualDirectionalPct
- ResidualSupportsReversalDiagnostic
- H5TargetBarSeq
- Status:
  - PendingH5
  - Completed
  - Unavailable
- ExitBid
- ExitAsk
- ExitTimestampUtc
- ExitLatencySeconds
- HoldingSeconds
- PnlPoints
- ReturnPct
- MfePointsExecutableBid
- MaePointsExecutableBid
- FuturesH5Move.

No field in this row triggers execution in V1.

## 3.8 `AdaptiveObserverRuntimeRow`

One mutable row per live session, used for health/recovery display only.

Unique:
`SessionId`.

Fields:
- SessionId
- RuntimeStatus:
  - Calibrating
  - Rebuilding
  - Live
  - Degraded
  - Closed
- LastHeartbeatUtc
- LastProcessedSourceAvailableAtUtc
- LastProcessedSourceTickId
- LastCompletedBarSeq
- CurrentPartialBarVolume
- CurrentPartialBarStartedAtUtc
- LastRecoveryStartedUtc
- LastRecoveryCompletedUtc
- LastRecoveryReconciledBars
- LastError.

This is NOT used as the authoritative calculation state after restart. Restart always reconstructs deterministic state from source data + frozen session configuration.

---

# 4. Grid 2 BOTH-mode contract

Persist CE and PE separately in `AdaptiveOptionBandBarRow`.

Dashboard modes:

```text
Side: BOTH | CE | PE
Measurement: NOTIONAL | CONTRACT
```

Defaults:
- BOTH
- NOTIONAL.

## BOTH view

BOTH is a directional comparison view, not a composite score.

Show:
- BarSeq
- End IST
- Duration
- CenterStrike
- BandRolled
- CE BandPriceChange / Return
- PE BandPriceChange / Return
- CE StrictDeltaRatioTotal
- PE StrictDeltaRatioTotal
- **RelativeStrictFlow = CE ratio - PE ratio**
- CE EnrichedDeltaRatio
- PE EnrichedDeltaRatio
- **RelativeEnrichedFlow = CE ratio - PE ratio**
- CE StrictCoverage
- PE StrictCoverage
- CE RollingStrictDeltaRatioTotal
- PE RollingStrictDeltaRatioTotal
- **RelativeRollingStrictFlow = CE rolling ratio - PE rolling ratio**
- CE RollingEnrichedDeltaRatio
- PE RollingEnrichedDeltaRatio
- CE RollingOiChangePct
- PE RollingOiChangePct
- **RelativeOiChangePct = CE OI change % - PE OI change %**
- CE RollingActivityPerSecond
- PE RollingActivityPerSecond.

Interpretation:
- positive relative flow = CE side stronger relative to PE;
- negative = PE side stronger;
- these are standalone pair differences, never combined into a weighted score.

CE-only / PE-only modes expose the full side-specific Grid 2 column set.

The Dashboard may compute simple presentation-only subtraction from already persisted CE/PE rows, but the preferred implementation is a read-model projection in `AdaptiveObserverDataService`; no market-data or strategy recomputation occurs in Razor.

---

# 5. Host orchestration classes

Host gets orchestration only.

## `AdaptiveObserverWorker : BackgroundService`

Single owner of live adaptive observer progression.

Responsibilities:
- market-hours/day rollover;
- call bootstrap/recovery;
- read newly persisted raw ticks;
- pass normalized ticks to deterministic observer engine;
- persist completed state packages transactionally;
- issue Dashboard refresh notification after commit;
- finalize session after close.

It does not contain market formulas.

## `AdaptiveSessionCoordinator`

Responsibilities:
- before 09:30: report Calibrating;
- wait until raw future persistence has advanced beyond the 09:30 cutoff;
- calculate opening 09:15–09:30 volume;
- select adaptive threshold using frozen V1 estimator;
- calculate causal prior-session strong threshold;
- build/persist 09:30 residual anchors;
- commit immutable session state;
- return a complete deterministic daily configuration object.

If today's session row already exists, it is loaded exactly; today's thresholds/anchors are never recalculated from changed code or changed history.

## `AdaptiveSourceTickReader`

Read-only access to `NiftySignalDbContext`.

Research parity ordering:
```text
AvailableAt = max(ReceivedAt, ExchangeTimestamp)
ORDER BY AvailableAt, Tick.Id
```

Queries are bounded to today's resolved observer instruments.

Historical/restart replay begins from 09:15 IST.

Incremental reads use the persisted runtime watermark only as an optimization; correctness does not depend on that watermark surviving restart.

## `AdaptiveObserverEngine`

In-memory deterministic composition of the pure project components.

Owns:
- per-token tick cleaner/enricher state;
- futures exact partial bar;
- completed rolling-state tracker;
- option per-token states;
- option band selection/aggregation;
- residual model;
- weak2/H5 observation lifecycle.

One instance per live session.

## `AdaptiveObserverPersistence`

Writes one completed-bar package in one adaptive-database transaction where possible:
1. FutureBar
2. RollingState if available
3. CE OptionBandBar
4. PE OptionBandBar
5. ATM ResidualBar
6. ATM±2 ResidualBar
7. Weak2/H5 lifecycle changes
8. Runtime row.

Idempotent unique keys prevent duplicate writes.

## `AdaptiveStateRecoveryService`

On Host start/restart:
1. identify today's session;
2. load immutable session config if already selected;
3. set RuntimeStatus=Rebuilding;
4. replay today's persisted raw ticks from 09:15 in research-parity order;
5. reconstruct exact futures bars, rolling state, option states, residual state and observations;
6. compare regenerated completed rows with persisted rows;
7. fail/degrade on any material mismatch rather than silently overwriting history;
8. insert any source-derived missing rows idempotently;
9. restore current partial bar and pending H5 observations in memory;
10. set RuntimeStatus=Live;
11. continue incremental processing.

A restart never changes historical completed output.

## `AdaptiveDashboardNotifier`

After a successful adaptive DB commit:
- send a lightweight Host -> Dashboard SignalR notification containing SessionId / latest BarSeq;
- no calculated payload is trusted as source-of-truth;
- Dashboard reads committed state from adaptive DB.

---

# 6. 09:30 lifecycle

## 08:45–09:15
Existing production instrument resolution/auth/feed startup remains.

Current universe already resolves ATM±10 strikes for the nearest and next weekly expiries. That is sufficient for ATM±2 observation under normal moves.

## 09:15–09:30
- raw ticks continue existing ingestion/persistence;
- observer status = Calibrating;
- no actionable weak2;
- no adaptive completed-bar grid shown as live signal output yet, though a progress/header can show opening-volume accumulation.

## 09:30 readiness
Do not select using wall clock alone.

Coordinator waits until the persisted future stream proves that data has advanced through the cutoff.

Opening volume uses:
`[09:15:00, 09:30:00)`
with research cumulative-volume semantics.

Then freeze:
- BaseBarVolume;
- RollingWindowVolume;
- StrongThreshold;
- weekly expiry;
- residual anchor composition and IVs;
- model/build identity.

## Post-selection replay
Replay persisted 09:15→current raw ticks through the exact adaptive engine.

Bars ending before 09:30 initialize rolling state but cannot produce actionable weak2 observations.

Signals/observations require trigger EndAvailableAt >= 09:30.

## Live
Continue from source DB persistence.

The observer works from persisted source ticks for restart correctness. Existing Live Quote continues to use its direct push path independently.

---

# 7. Performance contract

Dashboard requirement:
- committed completed adaptive bar -> all three grids normally visible in <1 second.

To support that:
- adaptive database rows are read-ready;
- no raw tick scans in Dashboard;
- indexed Last-N queries only;
- global row count <=15;
- SignalR notification immediately after Host commit;
- existing Live Quote push path unchanged.

Host source persistence currently flushes raw ticks at batch size 200 or <=1 second. The observer's <1s UI contract is measured from adaptive-state commit to visible UI. End-to-end raw tick -> derived row may additionally include the source persistence flush latency.

Do not sacrifice restart correctness by deriving authoritative persisted adaptive state from a tick that has not yet reached raw-tick persistence.

---

# 8. Restart parity acceptance test

Historical test day is replayed in these forms:

A. uninterrupted;

B. forced restart:
- during a partial future bar;
- immediately before a bar closes;
- immediately after a completed bar is persisted;
- during Strong;
- during Weak1;
- immediately after Weak2;
- while an H5 observation is pending;
- after Grid 2 band rolled;
- after 09:30 residual anchor creation.

Final database output must match A exactly for all deterministic fields:
- session config;
- future bars;
- rolling states;
- CE/PE band composition;
- contract/notional flow;
- residual rows;
- weak2 identities;
- OI gate;
- H5 result.

No tolerance-based "close enough" for integer/state fields.
Floating-point pricing fields use explicit test tolerance only where unavoidable.

---

# 9. Historical bootstrap before first live use

Before first production watch session:
- replay/import the validated discovery sessions into the new adaptive database;
- verify exact parity against `NiftyResearcher`;
- this seeds prior-session rolling-state history required for the first live StrongThreshold calculation.

Live strong threshold then naturally incorporates completed prospective sessions on subsequent days.

The 09:30 day-volume estimator coefficients remain frozen V1; they are not automatically refit from live sessions.

---

# 10. Existing runtime retained

Keep:
- FlatTrade authentication/session handling;
- FlatTrade WebSocket ingestion;
- raw `Tick` persistence;
- `Instrument` daily universe;
- NIFTY spot/future/VIX collection;
- data-gap recording;
- Sensex/BankNifty archive collection if still desired;
- Dashboard authentication;
- existing Header / access-token controls;
- existing Live Quote tick push;
- `NiftySignal.Pricing` Black-Scholes / IV / synthetic-forward primitives;
- Telegram/ops notification infrastructure;
- EF migration history already applied.

---

# 11. Legacy runtime targeted for retirement after adaptive cutover

Do NOT remove these before:
1. adaptive historical parity;
2. restart parity;
3. successful watch-only live stability period.

Then retire from production registration first:

### Old cadence/composite runtime
- `LiveFeatureEngine` score-cadence calculations;
- ScoreSnapshot/CoreScoreSnapshot/StrikeSnapshot live persistence loop;
- `LiveTradingEngine`;
- `CoreScoreHysteresisTradingEngine`;
- `CoreScoreCrossoverTradingEngine`;
- Ruleset/ScoreWeights/CoreScore live runtime registrations no longer consumed after cutover.

### Old 2,600-volume live strategy runtime
- `LiveVolumeBarWriter`;
- `LiveVolumeBarBuilderCache`;
- `LiveOptionSeriesCache` when no remaining consumer;
- `LiveOptionsScoreEngine`;
- `LiveFuturesCrossoverEngine`;
- `LivePaperTradeExecutor`;
- old live entry-signal / live paper-trade / crossover score runtime paths;
- old kill-switch path used solely by those retired strategies.

### Old Dashboard strategy UI
After new observer is stable:
- legacy composite/core score panels;
- live-options-score strategy panels;
- futures crossover strategy panels;
- old paper-trade/performance panels if no longer required;
- their polling/read-model branches in `LiveDataService`.

Keep old pages under `/legacy` during the initial watch period, then delete only after explicit approval.

### Code deletion rule
"Retired from production" does not automatically mean "delete project".

After registrations/pages are removed:
- run a repository reference audit;
- delete genuinely unreachable strategy code/tests/config only in a separate cleanup change;
- preserve useful offline research/replay code if still referenced;
- never delete applied EF migration history simply because a table/runtime is retired.

---

# 12. Deployment/source traceability

Before adaptive watch deployment:
- embed SourceBranch;
- embed CommitSHA;
- embed BuildUtc;
- embed ModelVersion.

Expose in:
- Host startup log;
- adaptive session row;
- Dashboard observer header.

Deployment must no longer leave ambiguity about whether running binaries came from `master`, `NiftyRatio`, or another branch.

---

# 13. Implementation gates

Phase 0 — fresh implementation branch from `master`.
Gate: build identity visible.

Phase 1 — pure adaptive calculation library.
Gate: NiftyResearcher historical parity.

Phase 2 — adaptive database/entities/migrations.
Gate: deterministic idempotent persistence tests.

Phase 3 — historical bootstrap/import.
Gate: research output parity and initial strong-history seed.

Phase 4 — session coordinator + 09:30 selection.
Gate: threshold/anchor parity.

Phase 5 — live/replay observer worker.
Gate: restart parity.

Phase 6 — Grid 2 CE/PE contract+notional band engine.
Gate: independent historical recomputation parity.

Phase 7 — Grid 3 ATM and ATM±2 residual engine.
Gate: match existing residual research rows.

Phase 8 — Weak2/OI/H5 observation lifecycle.
Gate: match adaptive trade-research observations without executing trades.

Phase 9 — Dashboard read model and three-grid UI.
Gate: existing Header/access-token/Live Quote unchanged; completed state visible normally <1 second after commit.

Phase 10 — watch-only VM deployment.
Gate: no adaptive paper trade/order path exists.

Phase 11 — legacy runtime removal.
Gate: prospective observer stable and rollback no longer needed.

Phase 12 — separate future paper-trading design/approval.
