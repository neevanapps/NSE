# Adaptive Market Observer — Dashboard Contract

## Scope

This document freezes the intended dashboard contract before production implementation.

The first deployed adaptive version is **watch-only**:
- no paper trading;
- no real trading;
- no adaptive entry buttons/actions;
- no strategy P&L-led dashboard design.

The dashboard exists to let the user watch synchronized futures, options-band and theoretical-residual behavior live and discover repeatable relationships.

## Existing top area — unchanged

Do not remove or redesign these existing rows as part of V1:
- application/header area;
- access-token/authentication bar and controls;
- Live Quote section;
- all current live price-tick displays.

The adaptive observer begins below the existing Live Quote area.

## Global observer controls

One global row selector controls all three grids:

```text
Rows:  [5] [10] [15]
```

Default: 10.

All grids show completed adaptive bars only and align on:
- TradeDate;
- AdaptiveModelVersion;
- BarSeq;
- exact futures adaptive-bar start/end timestamps.

The current incomplete bar is shown separately in the observer header, never mixed into completed rows.

## Observer header

Read-only session/runtime information:
- feed status;
- latest futures tick age;
- latest option quote age;
- adaptive model version;
- source branch;
- source commit SHA;
- build UTC;
- Host rebuild/recovery status;
- opening 09:15–09:30 futures volume;
- expected full-day futures volume;
- actual futures volume so far;
- selected base adaptive volume;
- rolling 10-bar volume;
- current incomplete-bar progress;
- completed adaptive bars;
- frozen strong threshold.

Normal completed-bar persistence to visible-grid target: < 1 second.

## Grid 1 — Futures Market State

Purpose:
Show the exact adaptive futures behavior from which the rolling market state and Weak2 observations are derived.

Columns:

1. Bar Seq
2. End IST
3. Duration
4. Bar Price Displacement
5. Bar Strict Delta
6. Bar Strict Delta Ratio Total
7. Bar Strict Coverage
8. Bar Enriched Delta
9. Bar Enriched Delta Ratio
10. Bar Trade Updates
11. Rolling Price Displacement
12. Rolling Strict Delta
13. Rolling Strict Delta Ratio Total
14. Rolling Quote Coverage
15. Rolling Enriched Delta
16. Rolling Enriched Delta Ratio
17. Rolling Strict Dominance Change
18. Rolling Enriched Delta Change
19. Rolling Efficiency
20. Rolling Return Bps
21. Rolling OI Change
22. Rolling OI Change %
23. Rolling Volume / Second
24. Strict Dominance Evolution
25. State — Normal / Strong / Weak1 / Weak2

Session-constant values such as BaseBarVolume, Rolling10BarVolume and StrongThreshold stay in the observer header, not repeated on every row.

Footer for displayed window:
- total strict delta;
- recomputed strict delta ratio;
- recomputed strict coverage;
- total enriched delta;
- total price displacement;
- total OI change;
- elapsed duration.

Do not sum state ratios or efficiency fields.

## Grid 2 — CE / PE Option-Band Market State

Purpose:
Treat weekly options as another market observed inside the **same exact adaptive futures-bar intervals**.

Options do not create an independent clock.

Controls:

```text
Side:         CE | PE
Measurement:  NOTIONAL | CONTRACT
```

Defaults:
- side: CE initially; user may switch freely;
- measurement: NOTIONAL.

Both contract and notional values are persisted. The dashboard selector changes displayed persisted columns only.

### Band composition

At the start of each adaptive futures bar:
1. obtain latest weekly synthetic underlying;
2. choose nearest listed ATM strike;
3. choose ATM±2 strikes for selected option side;
4. freeze those five contracts for the entire futures bar;
5. persist center strike and exact five-strike composition;
6. next adaptive bar may select a new center/band.

Persist `BandRolled` when composition differs from the previous bar.

### Trade-flow representation

Strict classification remains separate from enriched classification for options.

Do not assume option flow is always enriched.

Persist for both CONTRACT and NOTIONAL:
- strict buy;
- strict sell;
- strict unknown;
- strict delta;
- strict classified coverage;
- enriched buy/sell/delta;
- total activity.

Notional uses observed option premium × traded quantity using a precisely documented quantity unit. Before implementation, confirm FlatTrade Volume/LotSize semantics and persist raw quantity plus normalized contract/lot quantity where needed.

### Grid columns

1. Bar Seq
2. End IST
3. Duration
4. Center Strike
5. Band Rolled
6. Band Price Change
7. Bar Strict Delta
8. Bar Strict Delta Ratio Total
9. Bar Strict Coverage
10. Bar Enriched Delta
11. Bar Enriched Delta Ratio
12. Bar Trade Updates
13. Rolling Band Price Change
14. Rolling Strict Delta
15. Rolling Strict Delta Ratio Total
16. Rolling Quote Coverage
17. Rolling Enriched Delta
18. Rolling Enriched Delta Ratio
19. Rolling Strict Dominance Change
20. Rolling Enriched Delta Change
21. Rolling Efficiency
22. Rolling Return %
23. Rolling OI Change
24. Rolling OI Change %
25. Rolling Activity / Second

### Band price definition

Within one adaptive bar the five strikes are frozen.

Define:
```text
BandPremiumIndex = sum(mid price of the five selected options)
BarBandPriceChange = BandPremiumIndexClose - BandPremiumIndexOpen
```

Because composition can roll between adaptive bars:
```text
RollingBandPriceChange = sum(last 10 BarBandPriceChange values)
```

Do not calculate rolling price displacement by subtracting the current basket from a potentially different basket ten bars ago.

### OI

Primary positioning measure remains contract OI:
```text
BandOI = sum(OI of the five selected option contracts)
```

Also persist an explicitly named premium-notional OI representation for observation, but never confuse premium-notional OI movement with actual position creation/closure because option premium itself changes.

## Grid 3 — 09:30 Theoretical CE / PE Residual

Purpose:
Show what the weekly option market is doing **beyond what futures movement + convexity + time decay should explain** under a constant-09:30-IV reference.

This grid uses the same adaptive futures BarSeq/timestamps but a diagnostic composition frozen at 09:30 for the entire session.

### Persisted diagnostic variants

Persist both:
1. fixed single 09:30 ATM strike;
2. fixed 09:30 ATM±2 five-strike band.

Dashboard default: ATM±2 band.

Optional read-only selector:
```text
Residual view:  ATM | ATM±2
```

No live recalculation in Dashboard.

### 09:30 anchor

At 09:30:
- derive synthetic weekly underlying from same-expiry call/put parity;
- choose diagnostic ATM center;
- freeze ATM or ATM±2 composition for entire session;
- capture CE and PE midpoint anchors;
- solve per-contract 09:30 IV;
- persist all anchors and IVs.

Modeled weekly underlying later:
```text
SyntheticUnderlying0930 + (CurrentMonthlyFuture - MonthlyFuture0930)
```

Expected option prices:
- constant 09:30 IV;
- current calendar time-to-expiry;
- Black-Scholes repricing;
- fixed diagnostic strike(s).

### Grid columns

1. Bar Seq
2. End IST
3. Future Δ from 09:30
4. Diagnostic Center Strike
5. CE Actual Δ
6. CE Expected Δ
7. CE Residual
8. CE Residual %
9. PE Actual Δ
10. PE Expected Δ
11. PE Residual
12. PE Residual %
13. Directional Residual %
14. Residual Δ vs Previous Bar
15. Residual Direction — UP / DOWN / NEUTRAL
16. Futures Rolling Direction
17. Relationship — ALIGN / OPPOSE
18. Common Residual %
19. Max Quote Age

09:30 fixed anchor values belong in a compact panel above the residual grid, not repeated in every row:
- synthetic weekly underlying;
- monthly futures at 09:30;
- diagnostic strike/band;
- CE 09:30 basket/price;
- PE 09:30 basket/price;
- CE IV anchor(s);
- PE IV anchor(s).

Residual-grid footer is descriptive rather than additive:
- latest directional residual;
- median directional residual over displayed rows;
- max positive residual;
- max negative residual;
- ALIGN count;
- OPPOSE count.

## Current incomplete adaptive bar

Show separately above the grids:

- accumulated / target exact futures volume;
- progress %;
- bar start time;
- elapsed duration;
- current raw futures price;
- provisional data-quality indicators.

Do not place provisional calculations as the first grid row in V1.

## Synchronization behavior

A row number must mean the same adaptive futures interval everywhere.

If a dependent row is not persisted yet:
```text
Bar 187 | Pending
```

Never silently show a previous option/residual row beside a newer futures row.

## Read path and performance

Host owns all calculations and persists read-ready state.

Expected transaction/order:
1. persist completed futures bar;
2. persist rolling futures state;
3. persist CE and PE option-band rows;
4. persist single-strike and ATM±2 residual rows;
5. update Weak2/observation lifecycle if applicable;
6. commit;
7. emit lightweight SignalR state-changed notification.

Dashboard:
- receives notification;
- queries indexed last N rows only;
- renders existing cached header/price data plus the changed observer rows;
- performs no raw-tick scans;
- performs no Black-Scholes/IV work;
- performs no band aggregation;
- performs no rolling-window logic.

Target:
- persisted completed state -> visible UI normally < 1 second;
- Live Quote continues using its existing high-frequency mechanism independently.

## Watch-only V1

No adaptive paper-trade engine is included.

The observer may persist hypothetical research outcomes such as:
- Weak2 event;
- OI accepted/rejected;
- hypothetical fixed-strike ask;
- H5 bid;
- MFE/MAE.

These are observations, not executable trades.

Paper trading is a later separate phase after prospective live observation is reviewed.
