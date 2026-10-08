# Futures Grid Live-Observation Plan — 08 October 2026

**Status:** DESIGN FINALIZED FOR APPROVED SLICES · IMPLEMENTATION AUTHORIZED IN CONTROLLED SLICES · SPECIFIC DEFERRED ITEMS REMAIN BLOCKED UNTIL APPROVAL (section 0.4). Individual grid/commentary sections are marked DESIGN LOCKED where their design is final. No deployment is authorized by this document.

**Scope:** Adaptive Market Observer → **Futures — exact adaptive bars**, **Options** and **CE / PE theoretical residual** grids; the pinned Telegram screenshot; and the Running Market Commentary layer.

**Intent:** Make the grids easier to read live by showing only fields that answer distinct market-behaviour questions. Existing calculations and hidden diagnostic fields must remain available; this plan hides columns from the primary live grids rather than deleting functionality.

**Non-goals:** No trading rule, composite score, entry/exit automation, threshold optimization, or change to existing Strict / Enriched / rolling-state semantics. No change to live trading, scoring, entry/exit, risk or execution. Nothing in this plan (including the commentary layer) is wired into any trading path.

# 0. Controlling decisions, authorization scope and blockers

This section is the authoritative summary. Where an older paragraph elsewhere in this file appears to disagree, this section and the sections it references win, and the older paragraph is a defect to be fixed in the plan (not a licence to improvise in code).

## 0.1 Authorization scope (CLAUDE.md exception)

CLAUDE.md contains the general rule "don't modify `NiftySignal.Host` or `NiftySignal.Dashboard` until backtesting has found a real edge". The project owner's explicit, scoped instruction for this task authorizes Host and Dashboard changes **required specifically for** the finalized Adaptive Observer grids, supplemental metrics, pinned screenshot and commentary system. That scoped authorization overrides the older general restriction **for this work only**.

It does **not** authorize any change to live trading, entry/exit logic, scoring, risk/capital gates, kill switch or execution. CLAUDE.md itself is not rewritten to make the exception permanent.

## 0.2 Controlling design decisions

1. **Sidecar persistence.** New observational metrics are stored in separate, versioned sidecar tables keyed by `SessionId + BarSeq (+ MetricsVersion)`. They are never added as columns to the parity-protected rows `AdaptiveFutureBarRow`, `AdaptiveRollingStateRow`, `AdaptiveOptionBandBarRow`, `AdaptiveOptionResidualBarRow`. `AdaptiveObserverPersistence.VerifyRow` is not weakened or exempted. Details: section 71.
2. **Residual deltas.** The persisted `AdaptiveOptionResidualBarRow.ResidualDelta` keeps its legacy gap-bridging behaviour unchanged (renamed "Legacy Bridged Directional Δ" in diagnostic presentation). All NEW residual deltas and all commentary use adjacent-bar semantics (sections 34, 35). Residual supplemental values are derived by one shared pure projection, not a sidecar table (section 32.4).
3. **Day Vol PCR.** Nothing is labelled or displayed as full-chain Day Vol PCR until full-chain coverage is proven. A diagnostic "Subscribed-Universe Day Vol PCR" may be computed and persisted (sections 25.3–25.5).
4. **Option freshness.** New Position / ΔIV / IV Skew metrics reuse the existing 5-second option quote freshness (section 24.5). No second threshold.
5. **Basis / spot.** Spot enters the observer as an isolated causal input that must not change any existing adaptive output. ΔBasis stays hidden/unavailable until the spot freshness rule has been measured and approved by the project owner (section 9.6).
6. **Rolling-input availability.** Commentary primary events never fire when their mandatory rolling futures inputs are unavailable; missing external evidence is neither support nor contradiction (sections 48, 53).
7. **Pinned screenshot.** The Telegram screenshot uses one shared `LiveQuotePanel` component with a deterministic as-of data source pinned to `TargetBarSeq` (section 18).
8. **Commentary.** `NoMaterialEvent`, exact regime/lifecycle rules, no time-based regime expiry, `EvidenceAgreement` (never "Confidence"), conservative Telegram policy (sections 44–69).
9. **Urgency** is derived in the Dashboard/read projection (`Volume / DurationSeconds`) and is not persisted (section 5).
10. **Directional signs are hypotheses**, not validated edge (section 0.5).

## 0.3 FINAL TARGET grids versus what each slice actually displays

The grids described in sections 2/15 (Futures), 20 (Options) and 32 (Residual) are the **FINAL TARGET** layouts. A column is displayed only once the slice that implements it has landed. No permanent placeholder "—" columns are added to mimic the final layout early.

| Slice | Futures grid adds | Options grid adds | Residual grid adds |
|---|---|---|---|
| 1 | Seq, End IST, Dur s, Urgency (derived), Bar ΔPx, Roll ΔPx, Strict Δ, Enriched Δ, Roll Strict, Roll Enriched, \|Strict\| Δ, Roll OI Δ (renamed), Roll Efficiency, Evolution, State | Seq, End IST, Center, Roll?, CE/PE ΔPx, CE/PE Roll ΔPx, CE/PE contract Strict Δ, CE/PE contract Enriched Δ, CE/PE contract Roll Strict | Seq, End IST, Future Δ09:30, CE Res %, PE Res %, Directional Res %, Direction, Relation, Quote Age |
| 2A | MicroDev, OFI | — | — |
| 2B | ΔBasis — only after the freshness rule is approved | — | — |
| 3 | — | center CE/PE OI Δ, CE/PE Position, CE/PE ΔIV, IV Skew, Vol PCR, Roll Vol PCR | CE Res Δ%, PE Res Δ%, Adjacent Directional Res Δ, Straddle Res % |
| 4A–4C | Commentary domain, persistence, Dashboard panel, Telegram policy | | |

The Dashboard keeps the full pre-existing wide tables available behind an explicit **Diagnostic view** toggle (default: compact). Telegram captures use the compact layout only. Hiding a column never removes a calculation or a persisted value.

## 0.4 IMPLEMENTATION BLOCKERS / DEFERRED ITEMS

1. **ΔBasis display** — blocked until (a) spot is plumbed with proof that no existing adaptive output changes, (b) the spot age/inter-arrival distribution (p50/p90/p95/p99/max during market hours) is reported, and (c) the project owner approves one explicit freshness rule. Until then ΔBasis is hidden/unavailable and the Basis evidence family counts as neither support nor contradiction.
2. **Full-chain Day Vol PCR** — blocked until full nearest-expiry option-chain cumulative-volume coverage is proven. Only the explicitly labelled Subscribed-Universe diagnostic may exist meanwhile.
3. **Deployment** — nothing in this plan is deployed; no VM change is authorized. The migration/deploy ordering contract is in section 71.
4. **Telegram commentary beyond the V1 conservative set** (ordinary New / Strengthening / Weakening / Absorption) — deferred until the notification-noise replay report (section 73) has been reviewed.
5. **Commentary reliance on Options Position / ΔIV / IV Skew** — those metrics are implemented under the 5-second rule, but their measured availability must be reported (section 73) before they are treated as a meaningful evidence source. The 5-second rule is not loosened to improve availability.

## 0.5 Directional signs are hypotheses; EvidenceAgreement is not confidence

CLAUDE.md states that a metric's sign is a hypothesis until tested. The direction assigned in this plan to **OFI, ΔBasis, Options Position regimes, IV/skew interpretation, PCR context and residual direction** is a deterministic **market-interpretation hypothesis**. It is not a validated predictive relationship. The commentary engine's first purpose is to describe and classify what the measurements jointly imply *under those hypotheses*. Later forward outcomes may validate or reject them; outcome data is never used to rewrite historical commentary.

`EvidenceAgreement` (LOW / MEDIUM / HIGH) means only: *how many independent evidence families agree with the deterministic event interpretation under the current hypotheses*. It is **not** a probability, win rate, validated predictive confidence, trading confidence or "safe trade" indicator. The word "Confidence" is not used in the commentary domain, database or UI.

## 1. Live-reading model

The compact futures grid should answer, left to right:

1. When and how fast is activity arriving?
2. What did price do?
3. Who is executing aggressively now?
4. What has persistent aggressive flow been doing?
5. Is that dominance strengthening or weakening?
6. What is the passive top-of-book doing?
7. Are futures positions expanding or contracting?
8. Are futures leading or lagging spot?
9. Is price movement clean/efficient or choppy?
10. How does the existing evolution/state engine summarize the condition?

The grid is for observation. Interpret flow, OI and price response together rather than treating any one column as an independent signal.

## 2. FINAL TARGET futures-grid columns

This is the final target layout. The grid displays only the columns whose slice has landed (section 0.3).

| # | Display column | Source/type | Purpose |
|---:|---|---|---|
| 1 | **Seq** | Existing | Completed adaptive-bar sequence |
| 2 | **End IST** | Existing | Bar completion time |
| 3 | **Dur s** | Existing | Elapsed time required to complete the exact-volume bar |
| 4 | **Urgency** | New (Dashboard-derived, not persisted) | Rate at which the bar's traded volume arrived |
| 5 | **Bar ΔPx** | Existing | Immediate price response in this adaptive bar |
| 6 | **Roll ΔPx** | Existing | Price response across the existing rolling window |
| 7 | **Strict Δ** | Existing | High-confidence aggressive buy minus sell volume in the current bar |
| 8 | **Enriched Δ** | Existing | Broader aggressive buy minus sell volume in the current bar |
| 9 | **Roll Strict** | Existing | Strict aggressive-volume delta across the rolling window |
| 10 | **Roll Enriched** | Existing | Enriched aggressive-volume delta across the rolling window |
| 11 | **|Strict| Δ** | Existing | Change in magnitude of rolling Strict dominance |
| 12 | **MicroDev** | New (Slice 2A) | Time-weighted microprice deviation from midpoint inside the bar |
| 13 | **OFI** | New (Slice 2A) | Raw top-of-book order-flow imbalance accumulated inside the bar |
| 14 | **Roll OI Δ** | Existing, rename label | Net futures OI change across the rolling window |
| 15 | **ΔBasis** | New (Slice 2B; hidden until freshness rule approved) | Change in futures-minus-spot basis during the bar |
| 16 | **Roll Efficiency** | Existing | How efficiently the rolling path converts movement into net displacement |
| 17 | **Evolution** | Existing | Existing Strict-dominance evolution label |
| 18 | **State** | Existing | Existing adaptive state label |

### Important naming correction

The current Dashboard's displayed **OI Δ** comes from the rolling state, not from the single current bar. The compact grid should therefore label it **Roll OI Δ** so live interpretation cannot accidentally treat it as a one-bar OI change.

No underlying OI calculation changes are proposed by this rename.

## 3. Common timing and causality rules

All new calculations must follow the same causal live semantics as the adaptive observer.

### 3.1 Timestamp

Use the feed/persisted system's **available-at timestamp** for live sequencing. Do not match a future observation to a later spot or book observation merely because exchange/event timestamps are close.

### 3.2 Same-timestamp ordering

When multiple source observations have the same available-at timestamp, use the existing deterministic source ordering / persisted source identifier. Do not invent an ordering from price.

### 3.3 No look-ahead synchronization

For any as-of join:

SourceValue(t) = latest value actually available at or before t.

Never use the nearest observation if that observation occurs after t.

### 3.4 Duplicate/carry-forward rows

Repeated snapshots with no relevant state change must not be counted as independent book evidence.

For TOB / microprice / OFI, process **unique book states**, not the raw number of duplicate feed rows.

### 3.5 Invalid book

A top-of-book state is invalid for book-derived calculations when any required value is unusable, for example:
- bid <= 0;
- ask <= 0;
- ask < bid;
- bid quantity < 0;
- ask quantity < 0;
- denominator required by a formula is zero.

Invalid intervals must not silently reuse a later valid quote. Quality/coverage must remain auditable internally.

# 4. Existing visible fields — exact definition

## 4.1 Seq

Completed exact-volume adaptive futures bar sequence number. Only completed bars belong in the futures grid.

## 4.2 End IST

The bar's causal completion/availability timestamp converted to IST:

EndIST = EndAvailableAtUtc + 05:30.

Display remains HH:mm:ss unless later changed.

## 4.3 Dur s

Elapsed causal time of the completed adaptive bar:

DurationSeconds = EndAvailableAt - StartAvailableAt.

Every complete adaptive bar contains the session's configured exact volume threshold, so duration shows how quickly that fixed quantity of trading arrived.

Very short duration = high participation speed. Long duration = low participation speed. Duration has no directional meaning by itself.

## 4.4 Bar ΔPx

Immediate futures price displacement of the current completed adaptive bar:

Bar ΔPx = Close - Open.

Positive = higher close than open. Negative = lower close than open.

This is required because Strict/Enriched flow is only useful when compared with the price response it produced.

Examples:
- seller flow + negative Bar ΔPx → sellers are succeeding;
- seller flow + flat/positive Bar ΔPx → possible seller absorption;
- buyer flow + positive Bar ΔPx → buyers are succeeding;
- buyer flow + flat/negative Bar ΔPx → possible buyer absorption.

## 4.5 Roll ΔPx

Existing rolling-window futures price displacement:

Roll ΔPx = RollingEndPrice - RollingStartPrice.

The current adaptive observer uses its existing rolling adaptive-state window. At present this is the established **10 completed adaptive bars** / 10 × BaseBarVolume rolling state.

Do not change the rolling-window definition as part of this grid work.

## 4.6 Strict Δ

Current adaptive bar's high-confidence aggressor delta:

Strict Δ = StrictBuyVolume - StrictSellVolume.

Positive = more strictly classified buyer-aggressive traded volume.

Negative = more strictly classified seller-aggressive traded volume.

Zero = balanced/no net classified dominance.

This is executed/aggressor behaviour, not resting-book behaviour.

The compact view displays the **raw delta only** for now. Existing ratio and coverage calculations remain preserved but hidden.

## 4.7 Enriched Δ

Current adaptive bar's enriched aggressor delta:

Enriched Δ = EnrichedBuyVolume - EnrichedSellVolume.

It uses the existing enriched classification path, which broadens classified volume beyond Strict without replacing Strict.

Interpretation:
- Strict and Enriched same sign → broader agreement;
- Strict strong but Enriched weak → high-confidence flow exists but broader participation is less one-sided;
- opposite signs → flow conflict; do not force a directional interpretation.

The compact view displays the raw delta only.

## 4.8 Roll Strict

Existing rolling-window Strict delta:

Roll Strict = sum of Strict Δ over the existing rolling window.

This answers: who has been the persistent aggressive side?

No new rolling window is introduced here.

## 4.9 Roll Enriched

Existing rolling-window Enriched delta:

Roll Enriched = sum of Enriched Δ over the existing rolling window.

This gives broader-flow confirmation of Roll Strict.

## 4.10 |Strict| Δ

Existing rolling Strict dominance-magnitude change:

|Strict| Δ = |RollStrict_t| - |RollStrict_(t-1)|.

Interpretation:
- positive → rolling Strict dominance magnitude increased;
- negative → rolling Strict dominance magnitude decreased;
- near zero → little change in dominance magnitude.

This field is deliberately unsigned with respect to buyer/seller direction. Direction must be read from Roll Strict.

A sign flip in rolling Strict must be read together with Evolution; |Strict| Δ alone must not be interpreted as direction.

## 4.11 Roll OI Δ

Existing rolling futures open-interest change:

Roll OI Δ = OI_rolling_end - OI_rolling_start.

Positive means the number of outstanding futures contracts increased across the rolling window.

Negative means outstanding contracts contracted across the rolling window.

**Important:** OI alone does not identify longs versus shorts. Every newly opened futures contract has both a long and a short.

Interpret it with aggressive flow and price response:

| Roll OI | Aggressive flow | Price response | Behavioural interpretation |
|---|---|---|---|
| ↑ | Buy | ↑ | Buyer-initiated expansion accepted |
| ↑ | Sell | ↓ | Seller-initiated expansion accepted |
| ↑ | Buy | flat/↓ | Buyers being absorbed |
| ↑ | Sell | flat/↑ | Sellers being absorbed |
| ↓ | Buy | ↑ | Covering-compatible rally / contraction |
| ↓ | Sell | ↓ | Liquidation-compatible decline / contraction |

These are interpretations, not proof of exact participant open/close intent.

## 4.12 Roll Efficiency

Use the existing rolling efficiency definition.

For rolling bars 1..N:

PathLength = |Close_1 - Open_1| + sum from i=2..N of |Close_i - Close_(i-1)|.

Roll Efficiency = |RollingEndPrice - RollingStartPrice| / PathLength.

Range is approximately 0..1.

Interpretation:
- near 1 → movement was efficient/directional;
- near 0 → substantial back-and-forth movement produced little net displacement.

Examples:
- strong seller flow + OI↑ + negative Roll ΔPx + high efficiency → clean seller expansion;
- strong seller flow + OI↑ + small Roll ΔPx + low efficiency → churn/absorption is more plausible.

## 4.13 Evolution

Keep the existing StrictDominanceEvolution calculation and labels unchanged.

This column is a readable summary of how rolling Strict dominance is changing, including the existing Strengthening, Weakening, FlipToBuyer, FlipToSeller and other lifecycle labels already supported by the observer.

Evolution is a summary. It must not replace reading Roll Strict, |Strict| Δ, price and OI.

## 4.14 State

Keep the existing adaptive-state implementation unchanged: Strong, Weak1, Weak2, Normal and other states exactly as currently defined.

No proposal in this document changes state thresholds or state logic.

# 5. New field: Urgency

## 5.1 Goal

Measure **how quickly the exact adaptive-bar volume arrived**.

Because complete bars have the session's exact configured volume threshold, this turns bar completion speed into a simple participation-rate measure.

## 5.2 Formula

Urgency = BarVolume / DurationSeconds.

Units: reported futures volume units / second.

Use the bar's actual exact completed volume rather than assuming a hard-coded threshold.

Urgency is computed in the Dashboard/read projection from the persisted bar `Volume` and `DurationSeconds`. It is **not** persisted in any table (the source fields are already persisted; a second stored value adds no information). Commentary computes the identical value from the persisted bar when it needs it.

## 5.3 Edge case: zero-duration split bar

The exact-volume builder can produce more than one bar boundary from one source update. A derived bar can therefore have zero/near-zero duration.

Do not produce infinity.

If DurationSeconds <= 0, then Urgency = unavailable (—).

The raw bar remains valid; only the rate is undefined.

## 5.4 Interpretation

Urgency has no direction. It modifies the meaning of the other fields.

Example — convincing seller expansion:
- Urgency high;
- Strict negative;
- Enriched negative;
- Roll OI Δ positive;
- Bar/Roll ΔPx negative.

Example — possible seller absorption:
- Urgency high;
- Strict negative;
- Enriched negative;
- Roll OI Δ positive;
- Bar ΔPx flat/positive.

The second case can be more informative because large urgent selling is failing to move price lower.

## 5.5 No normalization in V1

Do not add percentile/z-score/session normalization to the display yet. Show the raw value. Any later normalization must be researched separately.

# 6. TOB imbalance — compute internally, do not display initially

We want TOB because it is useful for deriving/validating MicroDev and for later diagnostics, but **TOB and MicroDev are mathematically related at Level 1**. To avoid redundant live columns, V1 displays MicroDev and keeps TOB internally.

## 6.1 Per-book-state formula

For every unique valid futures best-book state:

TOB_t = (BidQty_t - AskQty_t) / (BidQty_t + AskQty_t).

Range: -1 <= TOB_t <= +1.

Interpretation:
- positive → displayed Level-1 book is bid-heavy;
- negative → ask-heavy;
- zero → balanced.

TOB is passive/resting-book context. It must not be treated as proof of actual executed buying/selling because displayed liquidity can be added, cancelled or spoofed.

## 6.2 Calculate on every unique book change

Do not use only the final quote of the bar.

For each unique valid book state inside the bar, retain the state from its available-at time until the next book state or the bar end.

Do not give extra weight to repeated identical snapshots.

## 6.3 Bar aggregation — time weighted

Primary internal bar TOB:

TOB_TW = sum(TOB_i × interval_i) / sum(interval_i),

where each interval is clipped to the adaptive bar's time boundaries.

This measures how the book was positioned through time, rather than how many feed messages represented each state.

## 6.4 Retain internally

Retain enough information to inspect later:
- TOB start;
- TOB time-weighted average;
- TOB end;
- TOB change;
- min/max if inexpensive;
- valid-book coverage duration.

Only the time-weighted average would be a candidate if TOB is later exposed.

# 7. New visible field: MicroDev

## 7.1 Goal

Express Level-1 displayed-book pressure in **price units**, incorporating both best prices and best quantities.

It is preferable to showing TOB simultaneously because it avoids two highly overlapping live columns.

## 7.2 Midpoint

For every unique valid Level-1 book state:

Mid_t = (Bid_t + Ask_t) / 2.

## 7.3 Microprice

Use cross-weighted best-book quantities:

MicroPrice_t = (Ask_t × BidQty_t + Bid_t × AskQty_t) / (BidQty_t + AskQty_t).

The cross weighting is intentional.

If bid quantity is much larger, microprice moves toward the ask; if ask quantity is larger, it moves toward the bid.

## 7.4 Microprice deviation

MicroDev_t = MicroPrice_t - Mid_t.

Interpretation:
- positive → upward Level-1 book pressure;
- negative → downward Level-1 book pressure;
- near zero → balanced book.

For V1, display **raw futures price points**, not ticks, percentile or z-score.

## 7.5 Bar aggregation

Do not use only the final microprice.

Primary visible bar metric:

MicroDev_TW = sum(MicroDev_i × interval_i) / sum(interval_i),

over valid unique book states inside the adaptive bar.

Intervals are clipped to the bar boundaries.

## 7.6 TOB relationship

At one level of book:

MicroPrice - Mid = (Spread / 2) × TOB.

Therefore TOB and MicroDev are related rather than independent.

V1 decision:
- **display MicroDev**;
- **retain TOB internally**.

# 8. New visible field: OFI

## 8.1 Goal

Strict/Enriched answer: Who executed aggressively?

OFI answers a different question: How did displayed best-bid/best-ask liquidity change?

This is useful for spotting absorption.

Examples:
- seller-aggressive trades + positive OFI + flat price → passive bid replenishment / seller absorption candidate;
- buyer-aggressive trades + negative OFI + struggling price → passive ask replenishment / buyer absorption candidate.

## 8.2 Input

Use every **unique valid best-book update** in deterministic causal order.

Let previous best book be:
- bid price P^B_(n-1), bid quantity q^B_(n-1);
- ask price P^A_(n-1), ask quantity q^A_(n-1).

Let current best book be:
- bid price P^B_n, bid quantity q^B_n;
- ask price P^A_n, ask quantity q^A_n.

## 8.3 Per-update OFI contribution

Use the standard Level-1 event contribution:

e_n =
I(P^B_n >= P^B_(n-1)) q^B_n
- I(P^B_n <= P^B_(n-1)) q^B_(n-1)
- I(P^A_n <= P^A_(n-1)) q^A_n
+ I(P^A_n >= P^A_(n-1)) q^A_(n-1).

Then:

OFI_bar = sum of e_n for valid transitions in the bar.

Display the **raw OFI quantity** in V1.

Interpretation:
- positive → net best-book evolution supports upward pressure;
- negative → net best-book evolution supports downward pressure.

## 8.4 Boundary handling

The first book change inside a bar needs a previous state. Use the last causally available valid book state at/before the bar start as the reference when available.

If there is no valid prior state, the first contribution is unavailable and OFI begins with the first pair of valid states. Do not fabricate a prior book.

## 8.5 Invalid states

Do not calculate an OFI transition across an invalid/crossed book as though the invalid observation did not exist.

Preferred conservative behaviour for implementation review:
- mark the book invalid;
- do not generate OFI while invalid;
- when a valid book returns, re-establish the baseline rather than creating a large synthetic OFI jump from stale pre-gap quantities.

This policy must be unit-tested.

## 8.6 Implementation contract — `futures-micro-v1` (Slice 2A, implemented)

This records the exact rules the code applies (`FuturesMicrostructureTracker`), so the contract is auditable without reading the code. Any change to a rule below requires a new `MetricsVersion`.

- **Input.** Every futures tick's top of book `(Bid, Ask, BidQty, AskQty)` stamped with the tick's `AvailableAt`, in the engine's causal processing order. A state identical to the previous unique state is collapsed (repeated snapshots are not independent evidence).
- **Valid book.** `Bid > 0`, `Ask > 0`, `Ask >= Bid` (locked is valid, crossed is not), `BidQty >= 0`, `AskQty >= 0`, all finite. TOB and MicroDev additionally need `BidQty + AskQty > 0`; a valid book with zero total quantity counts as valid time but contributes to neither TOB nor MicroDev.
- **Bar boundary.** For a completed bar `(start, end]` the reference state is the last unique state with `At <= start`; the bar's own book changes are the states with `start < At <= end`. A state is effective from its `At` to the next unique state, clipped to the bar. Consecutive bars therefore partition book transitions with none lost or double counted (tested by additivity of OFI).
- **TOB / MicroDev.** Start = reference state, End = state effective at `end`, Change = End − Start, Min/Max over the reference state and every in-bar state, and the time-weighted average over the seconds where the metric is defined (null when that is zero seconds, e.g. a zero-duration split bar).
- **OFI.** Sum of the Level-1 event contribution over consecutive valid unique states inside the bar, using the reference state as the first baseline when it is valid. An invalid/crossed state clears the baseline and counts as an invalid-book event; the first valid state afterwards only re-establishes the baseline (no transition across the gap). `Ofi` is null only when no valid baseline exists at any point in the bar; a bar with a valid baseline but no book change has `Ofi = 0` and `OfiTransitions = 0`.
- **Coverage.** `ValidBookSeconds` and `InvalidBookSeconds` (including time before any book was seen) are stored per bar.
- **Persistence.** Table `adaptive_futures_supplemental_bars`, unique on `(SessionId, BarSeq, MetricsVersion)`. Recovery inserts missing rows and verifies existing rows (tolerance 1e-9 on doubles). A mismatch or a write failure is logged and counted (`AdaptiveSupplementalPersistence.MismatchCount` / `FailureCount`), never overwrites a stored row, and never throws into core adaptive processing. A persisted per-session sidecar health flag is not implemented; the counters and error logs are the current signal.
- **Display.** The compact futures grid shows MicroDev (time-weighted, raw price points, 3 decimals) and OFI (raw quantity) from the current `MetricsVersion` row; a missing row, a missing table, or an unavailable value renders as unavailable, never zero.

# 9. Spot–futures basis — calculate continuously, display ΔBasis

## 9.1 Goal

Determine whether futures are strengthening/weakening relative to the NIFTY spot index.

Absolute basis contains carry and other structural effects. For intraday observation, **change in basis** is the primary visible metric.

## 9.2 Instantaneous basis

Maintain the latest causally available:
- NIFTY futures last price;
- NIFTY spot/index last price.

At any causal state time t:

Basis_t = FuturePrice_t - SpotPrice_t.

No future/nearest-neighbour matching is allowed.

## 9.3 Event stream

Basis should not be sampled only when a futures trade tick arrives.

Maintain a joined live state and update basis whenever either relevant futures or spot state changes, provided both current prices are available.

This produces a causal basis-state series inside the futures adaptive-bar interval.

## 9.4 Bar start/end basis

At the adaptive bar boundaries, use the latest basis state actually known at or before that boundary:

BasisStart = Basis at bar start.

BasisEnd = Basis at bar end.

Visible field:

ΔBasis = BasisEnd - BasisStart.

Interpretation:
- positive ΔBasis → futures strengthened relative to spot during the bar;
- negative ΔBasis → futures weakened relative to spot.

Examples:

Bullish confirmation:
- Bar ΔPx positive;
- Strict/Enriched positive;
- Roll OI Δ positive;
- ΔBasis positive.

Bearish confirmation:
- Bar ΔPx negative;
- Strict/Enriched negative;
- Roll OI Δ positive;
- ΔBasis negative.

Divergence can be more interesting:
- futures price rising but ΔBasis falling → futures are underperforming spot;
- futures price falling but ΔBasis rising → futures are relatively stronger than spot.

## 9.5 Internal time-weighted basis average

Also calculate internally:

Basis_TW = sum(Basis_i × interval_i) / sum(interval_i).

Do not display it in V1.

Retain:
- Basis start;
- Basis time-weighted average;
- Basis end;
- ΔBasis;
- spot age/freshness diagnostics.

## 9.6 Spot freshness — BLOCKER (section 0.4 item 1)

Basis quality depends on spot freshness. The plan records, per basis state:

    SpotAge_t = BasisStateTime_t - LastSpotAvailableAt_t

**No freshness threshold is frozen.** Implementation must not invent one. The required sequence is:

1. Identify the correct persisted NIFTY spot/index instrument and freeze it per session in the supplemental session table (section 71.3). If a unique valid spot instrument cannot be resolved, Basis is unavailable for that session; the instrument is never silently switched mid-session.
2. Add spot to the causal observer projection **without letting it influence futures exact-volume bar construction**, and prove with tests/replay that bar boundaries, `BarSeq`, OHLC, volume, Strict/Enriched, rolling state and option-bar boundaries are value-equivalent before and after spot is added.
3. Report the actual persisted spot age / inter-arrival distribution (p50, p90, p95, p99, max) during market hours from the available sessions, and recommend a freshness rule based on that evidence.
4. **Stop and obtain the project owner's approval of the threshold.** Only then may ΔBasis be shown and enabled as a commentary evidence family.

Until approved, Basis/ΔBasis is unavailable and the Basis family is neither support nor contradiction in commentary.

# 10. Why MicroDev, OFI and ΔBasis are not duplicates

These fields answer different questions:

- **Strict / Enriched** → executed aggressive flow.
- **MicroDev** → current Level-1 resting-liquidity pressure, summarized through time.
- **OFI** → how that Level-1 liquidity is being added, removed or repriced.
- **Roll OI Δ** → whether outstanding futures positioning expanded/contracted.
- **ΔBasis** → whether futures strengthened/weakened relative to spot.
- **Bar / Roll ΔPx** → whether the pressure actually moved price.
- **Urgency** → how quickly participation arrived.

This distinction is intentional.

# 11. Live interpretation examples (hypotheses, section 0.5)

## 11.1 Clean seller expansion

Possible live picture:
- Dur falling / Urgency high;
- Bar ΔPx negative;
- Roll ΔPx negative;
- Strict negative;
- Enriched negative;
- Roll Strict negative;
- Roll Enriched negative;
- |Strict| Δ positive;
- MicroDev negative;
- OFI negative;
- Roll OI Δ positive;
- ΔBasis negative;
- Roll Efficiency high;
- Evolution Strengthening.

Interpretation:

**Aggressive sellers + expanding positioning + bearish book evolution + futures weakening versus spot + successful price response.**

## 11.2 Seller absorption / possible bearish exhaustion

Possible live picture:
- Urgency high;
- Strict/Enriched negative;
- Roll Strict/Enriched still negative;
- Roll OI Δ positive;
- but Bar ΔPx flat/positive;
- Roll ΔPx stops falling;
- |Strict| Δ negative / Evolution Weakening;
- MicroDev turns positive;
- OFI turns positive;
- ΔBasis improves.

Interpretation:

**Sellers are still attacking and positions may still be expanding, but they are no longer receiving the expected downward price response. Passive buyers/book behaviour and futures-vs-spot are beginning to move against them.**

This is a lifecycle/absorption observation, not automatically a CE entry.

## 11.3 Clean buyer expansion

Mirror of seller expansion:
- Urgency high;
- Bar/Roll ΔPx positive;
- Strict/Enriched positive;
- rolling flow positive;
- |Strict| Δ positive;
- MicroDev positive;
- OFI positive;
- Roll OI Δ positive;
- ΔBasis positive;
- Roll Efficiency high.

Interpretation:

**Buyer-initiated expansion accepted by price.**

## 11.4 Buyer absorption

- buyer-aggressive Strict/Enriched;
- Roll OI Δ positive;
- price fails to rise;
- MicroDev/OFI turn negative;
- ΔBasis weakens;
- Evolution weakens.

Interpretation:

**Aggressive buying is meeting effective passive selling.**

# 12. Existing fields to hide from primary futures grid, not remove

The following existing fields remain available in data/model/diagnostics but are hidden from the compact live grid initially:

- Strict ratio;
- Strict coverage;
- Enriched ratio;
- Trade Updates;
- Roll Strict ratio;
- Roll quote coverage;
- Roll Enriched ratio;
- Enriched Δ change;
- Return bps;
- Roll OI Δ%;
- existing Vol/sec;
- any other currently persisted/read fields not in the compact list.

They remain useful for debugging, research, screenshot forensics, data-quality review, later dashboard expansion and future validation.

No persistence/model calculation should be deleted merely because its column is hidden.

# 13. New fields to retain internally even when not displayed

Persistence: every metric retained in this section lives in versioned sidecar tables (section 71), never as columns on the existing parity-protected adaptive rows.

If implementation cost is reasonable, retain:

### Book
- TOB start;
- TOB time-weighted average;
- TOB end;
- TOB Δ;
- MicroDev start;
- MicroDev time-weighted average;
- MicroDev end;
- MicroDev Δ;
- valid-book coverage duration;
- invalid/crossed-book count or duration.

### OFI
- raw current-bar OFI;
- number of valid book transitions contributing to OFI.

Do **not** add a rolling OFI column to V1. If useful later it should be a separate research decision.

### Basis
- Basis start;
- Basis time-weighted average;
- Basis end;
- ΔBasis;
- spot age at start/end;
- maximum/average spot age if useful.

### Urgency
- Dashboard-derived `Volume / DurationSeconds`; not retained in any table (section 5.2).

No percentiles/z-scores/threshold labels are part of V1.

# 14. Data-quality requirements for implementation

When implementation eventually starts, it must prove:

1. Existing adaptive bar boundaries and current fields are unchanged.
2. New metrics use only data causally available by each bar boundary.
3. Duplicate snapshot rows cannot bias TOB or MicroDev averages.
4. Time weighting is interval based, not feed-row-count based.
5. OFI does not fabricate a jump across invalid-book gaps.
6. Basis never pairs a futures observation with a future spot observation.
7. Basis exposes/records spot staleness.
8. Zero-duration split bars do not create infinite urgency.
9. Restart/replay reproduces identical metric values from persisted raw state.
10. New metrics do not change trading/scoring/execution behaviour.
11. Dashboard hiding does not remove old data fields or calculations.
12. Screenshot/export representation matches the live grid.
13. A sidecar row, when present, is recomputed and verified on recovery; when missing it is inserted deterministically (section 71.2). The existing `VerifyRow` is not weakened.
14. Adding spot input leaves every pre-existing adaptive output value-equivalent (section 9.6).
15. The legacy persisted `ResidualDelta` semantics are unchanged (section 35).

# 15. FINAL TARGET compact futures-grid layout

Displayed only as slices land (section 0.3). ΔBasis is omitted until its freshness rule is approved.

Recommended visible order:

    Seq | End IST | Dur s | Urgency
    | Bar ΔPx | Roll ΔPx
    | Strict Δ | Enriched Δ
    | Roll Strict | Roll Enriched | |Strict| Δ
    | MicroDev | OFI
    | Roll OI Δ
    | ΔBasis | Roll Efficiency
    | Evolution | State

Read it as:

    Time / speed
    → price response
    → immediate executed aggression
    → persistent executed aggression
    → dominance change
    → passive book / book evolution
    → position expansion/contraction
    → futures-vs-spot confirmation
    → path quality
    → existing interpretation

# 16. Decisions frozen so far

As of **08 October 2026**:

- Keep the futures grid compact.
- Hide unused columns; do not delete existing functionality.
- Keep raw visible values for now.
- Keep Seq, End IST, Dur s.
- Keep both immediate and rolling price response.
- Keep current Strict/Enriched and rolling Strict/Enriched.
- Keep |Strict| Δ.
- Rename displayed OI Δ to **Roll OI Δ** to reflect its actual rolling semantics.
- Keep Evolution and State.
- Show **Urgency** (Dashboard-derived, not persisted).
- Calculate TOB on every unique valid book state, time-weighted per bar, but do not display it initially.
- Show **MicroDev** instead of TOB to avoid redundant live columns.
- Show raw **OFI** from Level-1 book changes.
- Calculate spot–futures basis causally through the bar; show **ΔBasis** only after its spot freshness rule is measured and approved (section 9.6); retain absolute/time-weighted basis internally.
- Show **Roll Efficiency**.
- Do not add RSI/MACD/Bollinger/ADX or similar technical-indicator columns.
- Do not add a composite score.
- Implementation proceeds only in the controlled slices of section 72; deferred items in section 0.4 stay blocked.

# 17. Former open items — resolutions

The open items listed here on 08 Oct have been resolved as follows:

1. **Spot freshness rule** — not frozen; measured-then-approved process in section 9.6 (blocker 0.4.1).
2. **MicroDev display precision** — raw futures price points only in V1.
3. **Persistence of new metrics** — versioned sidecar tables with insert-if-missing / verify-if-present recovery (section 71). Urgency is not persisted; residual supplemental values come from a shared pure projection (section 32.4).
4. **Dashboard UX** — compact grid by default plus an explicit Diagnostic view toggle that restores the existing wide tables (section 0.3).
5. **Telegram composition** — compact grids only, plus the pinned Live Quote panel (section 18).

# 18. Telegram screenshot — include the NIFTY Live Quote panel, pinned to the target bar

**Status:** DESIGN LOCKED. Implemented as a separate Slice 1 sub-step (it is not merely CSS/grid compaction).

Current Telegram adaptive screenshots do not include the NIFTY **Live Quote** panel. The existing `LiveQuotePanel` / `LiveDataService` show *live/latest* state; the Telegram screenshot is *pinned to `TargetBarSeq`* and must be reproducible. The screenshot therefore cannot simply reuse the latest live values.

## 18.1 One component, two data sources

- There is exactly **one** `LiveQuotePanel` component. No second, visually separate quote component is created.
- **Normal Dashboard mode:** behaviour is unchanged (live/latest data, interactive CE/PE strike selection).
- **Capture mode:** the same component receives deterministic **as-of data** resolved causally at the pinned target bar's end boundary (`EndAvailableAtUtc` of the completed `TargetBarSeq` bar — the same boundary the grids use).

This is a shared component with a different data source in capture mode; the browser and Telegram views cannot diverge visually.

## 18.2 Capture-mode data resolution

At the target bar end boundary `B`:

- **NIFTY spot** = latest valid spot value available at or before `B`.
- **NIFTY future** = latest valid value of the session's selected (frozen) future available at or before `B`.
- **VIX** = latest valid value available at or before `B`, when available.
- Spot is resolved from the trade date's instrument master (the single NIFTY `Index` instrument; if the master does not contain exactly one, spot is rendered unavailable). Until Slice 2B freezes a supplemental session spot identity, this read-time resolution from the immutable instrument master is the capture-mode rule.
- **CE / PE** = the **center CE and center PE** of the target completed option-band row (`CenterStrike`), resolved from the session's frozen option universe and weekly expiry. The exact strike labels are displayed.
- **LTP / bid / ask / change** come only from ticks available at or before `B`. Day change uses the same day-open baseline definition as live mode.
- Each quote shows the available-at time of the tick it came from, so staleness is visible without inventing a freshness threshold. If no tick exists at or before `B`, the value is rendered unavailable (`—`); an older unrelated quote is never substituted and a later quote is never used.
- The wall-clock-based "Session metadata stale — check login" badge is not rendered in capture mode.
- **Gamma Flip** is derived from the current live option chain (`LiveDataService.GammaFlipLevel`) and cannot be reproduced as of a past bar, so the pinned capture omits it (amendment found during Slice 1 implementation). The live Dashboard still shows it.
- The quote box is **never** re-centered using the wall-clock time at which Chromium ran.

## 18.3 Difference between browser and pinned capture (documented behaviour)

| | Browser Dashboard | Pinned Telegram capture |
|---|---|---|
| Data | live/latest | as-of `TargetBarSeq` end boundary |
| CE / PE strike | interactive, user-selected (stable across re-centering) | center CE / center PE of the target bar's option-band row |
| Reproducible later | no | yes (same persisted inputs ⇒ same image data) |

## 18.4 Capture layout

The ordinary Telegram capture contains, in order:

1. **NIFTY Live Quote panel** (pinned, as above);
2. adaptive session/header information required for context;
3. current incomplete adaptive-bar progress only if it remains part of the normal capture (completed-snapshot captures hide it, as today);
4. compact Futures grid;
5. compact Options grid;
6. compact Residual grid (the residual section remains part of the normal capture).

The capture must not omit the Live Quote panel because of viewport clipping or because the screenshot target starts at the adaptive-grid DOM element, and must not clip the compact grids horizontally.

## 18.5 Reliability requirements

Implementation validation must prove that:

- the live quote panel is visibly present in the generated PNG;
- CE and PE strike labels, bid and ask values are visible;
- values come from data at or before the pinned boundary (a later tick injected in a test does not appear);
- a missing quote is rendered unavailable rather than replaced by an older unrelated quote;
- the image remains readable at the Telegram image dimensions;
- adding the quote panel does not truncate the compact grids;
- normal (non-capture) Dashboard behaviour of `LiveQuotePanel` is unchanged.

# 19. Options Grid Live-Observation Plan

**Status:** DESIGN LOCKED. Implemented in Slice 1 (existing fields) and Slice 3 (new metrics), see section 0.3. Full-chain Day Vol PCR is deferred (section 0.4).

**Scope:** Adaptive Market Observer → Options grid synchronized to the completed futures adaptive bars.

The options grid does **not** create its own clock. Every options row belongs to exactly one completed futures adaptive bar interval.

The primary compact view will be the CE+PE **BOTH** view. Existing CE-only / PE-only details, ratios, coverage, notional variants and diagnostics remain available internally or in an expanded diagnostic view; they are not deleted.

## 19.1 Option-grid reading model

Read the compact options row from left to right as:

1. Which futures adaptive bar and option band are being observed?
2. How did CE and PE premiums respond?
3. Which side saw aggressive executions?
4. Is that aggressive flow persistent across the rolling window?
5. What happened to option OI in the center contracts?
6. Is the center contract behaving like writing, long build, short covering or long unwind?
7. Is volatility itself expanding/contracting?
8. Is downside-vs-upside IV skew changing?
9. Is current and rolling option participation put-heavy or call-heavy?
10. How does the current bar compare with recent rolling participation (Roll Vol PCR)?

No individual options column is a trade signal by itself.

# 20. FINAL TARGET compact Options grid

Displayed only as slices land (section 0.3). The compact grid uses raw contract quantities; the BOTH/CE/PE and NOTIONAL/CONTRACT selectors remain in the Diagnostic view.

Visible order:

    Seq | End IST | Center | Roll?
    | CE ΔPx | PE ΔPx
    | CE Roll ΔPx | PE Roll ΔPx
    | CE Strict Δ | PE Strict Δ
    | CE Enriched Δ | PE Enriched Δ
    | CE Roll Strict | PE Roll Strict
    | CE OI Δ | PE OI Δ
    | CE Position | PE Position
    | CE ΔIV | PE ΔIV | IV Skew
    | Vol PCR | Roll Vol PCR

Above the grid, **no Day Vol PCR is shown in V1** because full-chain coverage is not proven (sections 25.3–25.4). Once Slice 3 persists the diagnostic, the header may show a clearly named line:

    Subscribed-Universe Day Vol PCR: x.xx   (CE vol x | PE vol x | N tokens | Since HH:mm:ss IST)

Row-level Vol PCR and Roll Vol PCR are **ATM±2 band** metrics and are labelled as such.

## 20.1 Existing context columns

### Seq

Same completed futures adaptive-bar sequence as the Futures grid.

### End IST

Same causal completed-bar end timestamp as the Futures grid.

### Center

The synthetic weekly-option center strike selected causally at the start of the futures adaptive bar by the existing option-band selection logic.

### Roll?

Visible warning that the ATM±2 band composition changed relative to the previous futures bar.

A band roll is important because rolling values then represent a sequence of per-bar frozen ATM±2 bands, not one unchanged set of option contracts.

The band remains frozen **within each individual futures adaptive bar**.

# 21. Premium-response columns

## 21.1 CE ΔPx / PE ΔPx

Keep the existing ATM±2 band premium-index price change for the current futures adaptive bar.

For each side:

    BarBandPriceChange = PremiumIndexClose - PremiumIndexOpen

The existing premium index is the sum of causally available component mids for the five-strike side.

Interpretation:
- CE ΔPx positive → call-band premium increased;
- PE ΔPx positive → put-band premium increased.

Always interpret CE and PE together.

Examples:
- CE up, PE down → directional bullish transfer is plausible;
- CE down, PE up → directional bearish transfer is plausible;
- CE and PE both up → common volatility/premium expansion may be important;
- CE and PE both down → common volatility/theta/premium contraction may be important.

## 21.2 CE Roll ΔPx / PE Roll ΔPx

Keep the existing rolling band-price change over the existing options rolling window.

Current rolling semantics remain the established adaptive rolling-window length, presently 10 completed futures adaptive bars.

Do not introduce another rolling length as part of this plan.

Because the ATM±2 band can roll, the rolling price metric is a sum of causally frozen per-bar band changes, not a literal same-contract start-to-end premium comparison.

# 22. Options aggressive-flow columns

## 22.1 CE Strict Δ / PE Strict Δ

For the compact grid, display **raw contract-quantity Strict delta**, not the ratio:

    ContractStrictDelta = StrictBuyQuantity - StrictSellQuantity

Use the existing ATM±2 side aggregation for the current futures adaptive bar.

Positive = buyer-aggressive option volume dominates.

Negative = seller-aggressive option volume dominates.

Existing notional Strict values, ratios and coverage remain retained but hidden from the compact grid.

## 22.2 CE Enriched Δ / PE Enriched Δ

Display raw contract-quantity Enriched delta:

    ContractEnrichedDelta = EnrichedBuyQuantity - EnrichedSellQuantity

Use the same current-bar ATM±2 band.

Interpret Strict and Enriched together:
- same sign → broader agreement;
- different sign → flow conflict / mixed participation.

## 22.3 CE Roll Strict / PE Roll Strict

Display raw rolling contract Strict delta:

    RollStrictSide = sum of current-bar ContractStrictDelta across the existing rolling option window

This is persistent CE or PE aggressive execution behaviour.

Do not replace it with the current ratio display in the compact grid.

Existing rolling ratios/coverage remain available diagnostically.

## 22.4 Roll Enriched

Do not expose CE/PE Roll Enriched in V1 compact view because width is limited and current Strict + current Enriched + rolling Strict already provide the primary flow view.

Retain the existing rolling Enriched values internally/diagnostically.

# 23. Center-contract OI and Position labels

The compact grid's visible CE OI Δ / PE OI Δ must refer to the **center-strike CE and PE contracts**, not the five-strike band aggregate.

Reason: the Position labels below must describe the same unchanged contract across the bar. A five-strike aggregate can hide different opening/closing behaviour across strikes.

Retain the existing ATM±2 band Bar OI Δ and rolling OI metrics internally.

## 23.1 CE OI Δ / PE OI Δ

For the center CE and center PE separately:

    CenterOiDelta = OI_end - OI_start

where start and end are causal snapshots of the same center-strike contract within the completed futures adaptive bar.

Positive = outstanding contracts increased.

Negative = outstanding contracts decreased.

OI does not identify actor identity by itself.

## 23.2 Center premium change used for position classification

Use valid two-sided quote midpoint, not LTP:

    Mid = (Bid + Ask) / 2

    CenterMidDelta = Mid_end - Mid_start

The center contract is frozen for the bar.

Do not classify Position if:
- start or end midpoint is unavailable or older than the 5-second option freshness rule (section 24.5);
- start or end OI is unavailable;
- the center contract changed inside the bar, which should not occur under the frozen-band contract;
- a defined zero-change policy below yields Neutral.

## 23.3 CE Position

Classify the center CE using OI change + midpoint change:

| CE OI | CE midpoint | Label |
|---|---|---|
| ↑ | ↓ | CallWriting |
| ↑ | ↑ | CallLongBuild |
| ↓ | ↑ | CallShortCover |
| ↓ | ↓ | CallLongUnwind |
| 0 / unavailable | any | Neutral / — |
| any | 0 / unavailable | Neutral / — |

These are behaviour-compatible labels, not proof of the initiating participant.

## 23.4 PE Position

Classify the center PE:

| PE OI | PE midpoint | Label |
|---|---|---|
| ↑ | ↓ | PutWriting |
| ↑ | ↑ | PutLongBuild |
| ↓ | ↑ | PutShortCover |
| ↓ | ↓ | PutLongUnwind |
| 0 / unavailable | any | Neutral / — |
| any | 0 / unavailable | Neutral / — |

Recent research makes this row-level interpretation especially useful, but **no Position label becomes an entry gate in this implementation**.

# 24. IV metrics

The options compact grid will expose:

    CE ΔIV | PE ΔIV | IV Skew

Use the existing pricing conventions, risk-free-rate source and time-to-expiry convention already used by the adaptive observer/pricing project. Do not create a second independent volatility model for the Dashboard.

All IV calculations must be causal and based on quote midpoint rather than LTP.

## 24.1 Center CE ΔIV / PE ΔIV

For the frozen center CE and PE contract:

    CE ΔIV = CE_IV_end - CE_IV_start
    PE ΔIV = PE_IV_end - PE_IV_start

Use percentage-point display convention consistently. Example: an IV move from 18.2% to 21.0% is +2.8 IV points.

Purpose:
- separate underlying-direction premium movement from volatility repricing;
- identify volatility expansion/crush;
- make 0-DTE premium behaviour easier to interpret.

Examples:
- PE premium ↑ + PE ΔIV strongly positive → downside option demand/volatility repricing accompanies the move;
- PE premium ↑ + PE ΔIV flat/negative → more of the premium move may be explained by underlying/delta rather than volatility expansion.

## 24.2 IV solver quality

Near expiry, especially 0-DTE, IV inversion can become unstable.

Therefore:
- require a valid two-sided quote midpoint;
- use the correct strike, expiry, current underlying input and session risk-free rate;
- reuse the existing time-to-expiry convention;
- if no stable finite IV solution exists, display —;
- never carry a later IV backward;
- retain quote age/solver availability diagnostically.

Do not fabricate/clamp an IV merely to keep the grid populated.

## 24.3 IV Skew

Do **not** define skew as ATM PE IV minus ATM CE IV alone.

Use symmetric OTM wings from the frozen ATM±2 band.

Let:
- PE -1 = one strike below Center;
- PE -2 = two strikes below Center;
- CE +1 = one strike above Center;
- CE +2 = two strikes above Center.

Primary visible skew:

    IVSkew =
      [(IV_PE_-1 - IV_CE_+1)
       + (IV_PE_-2 - IV_CE_+2)] / 2

Positive:
- downside OTM puts carry higher IV than equivalent upside OTM calls.

Negative:
- upside OTM calls carry higher IV than equivalent downside puts.

This is a relative volatility-shape metric, not direction by itself.

All four wing IVs must be valid and fresh enough under the final option quote-freshness policy. Otherwise IV Skew is unavailable.

## 24.4 ΔSkew retained internally

Also calculate:

    ΔSkew = IVSkew_end - IVSkew_start

Do not display ΔSkew in V1.

Retain it for later research because change in skew may eventually be more informative than absolute skew.

## 24.5 Option quote freshness — FINAL

Center Position, CE/PE ΔIV and IV Skew reuse the **existing adaptive option quote freshness convention: 5 seconds** (`AdaptiveOptionBandCalculator` `maxQuoteAgeSeconds = 5`, and `ResidualQuoteMaxAgeSeconds = 5` in the session coordinator). No second threshold is introduced.

- If a required quote is older than 5 seconds at the relevant boundary, the metric is unavailable.
- A later quote is never carried backward.
- IV is never fabricated or clamped to keep a cell populated.
- The rule is not loosened to improve availability; the measured availability is reported separately (section 73) and simply means the Options family cannot confirm that bar.

# 25. Volume PCR metrics

PCR in this plan is a **participation ratio**, not a bullish/bearish signal by itself.

The compact grid exposes (both ATM±2):

    Vol PCR | Roll Vol PCR

Full-chain Day Vol PCR is **not displayed** in V1 (sections 25.3–25.4).

## 25.1 Current-bar Vol PCR — ATM±2

Use contract traded quantity across the same frozen ATM±2 bands represented by the row:

    VolPCR_bar =
      PE_ATM±2_TradedQuantity_bar
      /
      CE_ATM±2_TradedQuantity_bar

Use raw contract quantity, not number of feed messages.

If CE volume is zero, display — rather than infinity.

Do not use notional volume for the V1 displayed PCR. Retain notional PCR internally if inexpensive.

Interpretation examples:

A high PCR can mean very different things:

Case A:
- Vol PCR high;
- PE Position = PutLongBuild;
- PE Strict positive;
- PE ΔIV positive;
- IV Skew increasing.

This is compatible with active downside-protection / bearish demand.

Case B:
- Vol PCR high;
- PE Position = PutWriting;
- PE Strict negative;
- PE ΔIV falling;
- IV Skew falling.

This is compatible with heavy put supply/writing.

Therefore PCR must always be read with price, flow, OI/Position and IV.

## 25.2 Roll Vol PCR — ATM±2

Do **not** average the last N bar PCR values.

Correct formula over the existing option rolling window:

    RollVolPCR =
      sum(PE_ATM±2_TradedQuantity for last N bars)
      /
      sum(CE_ATM±2_TradedQuantity for last N bars)

where N is the existing rolling option-window size, presently 10 completed futures adaptive bars.

This remains valid when the ATM±2 band rolls because each bar contributes its own causally frozen current-band traded quantity.

Interpretation:
- current Vol PCR far above Roll Vol PCR → sudden put-heavy participation relative to recent regime;
- current Vol PCR near Roll Vol PCR → current participation resembles the rolling regime.

## 25.3 Full-chain Day Vol PCR — DEFERRED / HIDDEN

Target definition (not implemented, not displayed):

    DayVolPCR(t) = cumulative PE traded quantity / cumulative CE traded quantity
                   over the FULL nearest-expiry NIFTY option chain since the session observation start

Nothing may be displayed or labelled "Full Chain Day Vol PCR" unless full nearest-expiry option-chain cumulative-volume coverage has been **proven**. The adaptive observer consumes whatever nearest-expiry option universe the source database holds; that does not prove the live subscription covers the full chain (the collector subscribes a limited strike window).

## 25.4 Full-chain coverage requirement

Before any full-chain label can be used, prove that cumulative volume is available for the full intended nearest-expiry universe, with a persisted, auditable coverage diagnostic. Otherwise the figure stays hidden. This is blocker 0.4.2.

## 25.5 Subscribed-Universe Day Vol PCR — diagnostic

An internal diagnostic may be computed from the option tokens actually present in the session's frozen option universe:

    SubscribedUniverseDayVolPCR(t) = cumulative PE volume / cumulative CE volume over the subscribed universe

- **Baseline:** the session `ObservationStart` (frozen). For a normal complete session this is the frozen session start (09:15); for a late-start/fallback session it is the first valid observed point used by that session. It never implies activity before `ObservationStart`.
- **Label (mandatory):** `Subscribed-Universe Day Vol PCR` with `Since: HH:mm:ss IST`.
- **Persisted per bar (options sidecar):** cumulative CE volume, cumulative PE volume, option token/strike universe count, observation start, coverage status.
- If CE cumulative volume is zero the ratio is unavailable (never infinity).
- It is never presented as, or silently substituted for, a full-chain figure.

# 26. Additional new option metrics to calculate but hide initially

The following are useful but should not widen the first compact grid.

## 26.1 Center CE/PE MicroDev

For center CE and PE independently, use the same Level-1 definition approved for futures:

    Mid = (Bid + Ask) / 2

    MicroPrice =
      (Ask × BidQty + Bid × AskQty)
      /
      (BidQty + AskQty)

    MicroDev = MicroPrice - Mid

Calculate on every unique valid book state inside the futures adaptive bar and time-weight across the bar.

Retain:
- CE MicroDev time-weighted average;
- PE MicroDev time-weighted average;
- start/end/change if inexpensive.

Do not aggregate microprice naïvely across five strikes.

## 26.2 Center CE/PE OFI

Calculate Level-1 OFI independently for the center CE and PE using the same causal book-transition formula approved for futures.

Purpose:
- distinguish aggressive option trade flow from passive liquidity replenishment/removal;
- inspect cases such as PutShortCover + positive PE OFI.

Do not show in V1 compact view initially.

## 26.3 Option Activity/s

Calculate separately for CE and PE ATM±2 bands:

    CE Activity/s = CE traded contract quantity / futures-bar duration
    PE Activity/s = PE traded contract quantity / futures-bar duration

Option activity does not create the bar clock; it measures how intensely options participated during the futures event.

Retain contract and notional variants internally.

## 26.4 Center straddle change

Using frozen center CE and PE midpoint:

    Straddle = CenterCE_Mid + CenterPE_Mid

    Straddle Δ = Straddle_end - Straddle_start

Purpose:
- distinguish pure directional premium transfer from common premium/volatility expansion or contraction.

Examples:
- CE +15, PE -14 → little straddle change; mostly directional transfer;
- CE +12, PE +10 → large positive straddle change; common premium/volatility expansion.

Retain in V1 diagnostics; do not expose in compact grid yet.

## 26.5 Implementation contract — `options-supp-v1` (Slice 3, implemented)

Computed by `OptionsSupplementalCalculator` once per completed bar and stored in `adaptive_options_supplemental_bars` (unique on `SessionId, BarSeq, MetricsVersion`; insert-if-missing / verify-if-present; never overwritten). The shared residual projection (section 32.4) needs no table.

- **Center contract.** The center CE and PE of the ATM±2 band selected at the bar's start, fixed for the whole bar. Wings are the band's strikes one and two below (PE) and above (CE) the center. Without a selection every contract-level value is unavailable (the row still records the subscribed-universe volumes).
- **Freshness.** A boundary quote is usable only if a two-sided book (`Bid > 0`, `Ask >= Bid`), available at or before the boundary and at most **5 seconds** old (exactly 5 s is fresh). This is the existing adaptive convention; a guard test pins it to `AdaptiveOptionBandCalculator`'s default.
- **Mid / OI.** Midpoint start/end/Δ per center contract (never LTP); OI start/end/Δ per center contract from the last known open interest at each boundary (OI is not subject to the 5 s rule and updates sparsely, which is why zero OI change, and therefore `Neutral`, is expected to be common).
- **Position.** `OI Δ` and midpoint Δ must both exist; if either is zero the label is `Neutral` (no inference); otherwise OI↑ + mid↓ = Writing, OI↑ + mid↑ = LongBuild, OI↓ + mid↑ = ShortCover, OI↓ + mid↓ = LongUnwind, prefixed `Call` / `Put`. Missing input = unavailable (null). Labels are behaviour-compatible descriptions, not actor identity, and not entry gates.
- **IV.** `AdaptiveResearchPricing.SolveIv` (the frozen research solver; returns null when the solution is unreliable, never clamped) on the fresh mid, the contract's own strike/expiry, the existing time-to-expiry convention and the session risk-free rate. The underlying is the synthetic weekly underlying of the band selection at the same instant (start: the bar's selection; end: the next selection formed at the bar end). Stored in IV points (×100). ΔIV = end − start.
- **IV Skew.** `[(IV_PE-1 − IV_CE+1) + (IV_PE-2 − IV_CE+2)] / 2`; all four wing IVs are required. The visible value is the **end-of-bar** skew; start and ΔSkew are retained internally.
- **PCR.** Vol PCR = ATM±2 PE contract quantity / CE contract quantity of the bar (null when CE is 0). Roll Vol PCR = Σ PE quantity / Σ CE quantity over the existing rolling window and requires every bar of the window to have a band (one gap blanks it), never an average of ratios.
- **Internal.** Center CE/PE MicroDev (time-weighted) and OFI from each option's own Level-1 book (same tracker as the futures sidecar); CE/PE ATM±2 Activity/s (contract quantity / bar seconds); center straddle mid start/end/Δ.
- **Subscribed-Universe Day Vol PCR (diagnostic only).** Σ traded quantity of the session's frozen option universe split CE/PE since the session observation start (stored with the universe token count and how many tokens had data). It is shown only in the Diagnostic view under that exact name, never in the compact grid, and never described as full chain.
- **Residual metrics.** CE Res Δ%, PE Res Δ%, Adjacent Directional Res Δ and Straddle Res % are shown in the compact residual grid from the shared pure projection over persisted rows plus the frozen anchor baselines (adjacent-bar semantics). The legacy persisted bridged `ResidualDelta` is untouched and shown only in the Diagnostic view.

# 27. Options fields to hide, not remove

Keep existing functionality/data for:
- Strict ratios;
- Enriched ratios;
- coverage;
- CE−PE relative Strict/Enriched ratios;
- rolling Enriched;
- rolling Enriched change;
- |Strict| change variants;
- band OI Δ and OI %;
- rolling band OI and OI %;
- activity/s;
- rolling efficiency;
- notional-flow variants;
- premium-notional OI;
- quote age;
- all existing CE-only / PE-only diagnostic fields.

No existing calculation is to be deleted simply because the compact BOTH grid hides it.

# 28. Options-grid data-quality and causality requirements

Implementation must prove:

1. Options rows use exactly the corresponding futures adaptive-bar time boundaries.
2. Band selection is causal and frozen within each bar.
3. Roll? is accurate whenever composition changes.
4. No future option quote is used for premium, OI, IV, MicroDev or OFI.
5. Center OI Δ and Position use the same center contract for bar start/end.
6. Position uses midpoint + OI, not LTP + OI.
7. Position becomes Neutral/unavailable on zero/missing required changes rather than inferring actor identity.
8. IV uses midpoint and the existing pricing/time-to-expiry conventions.
9. Invalid/unreliable 0-DTE IV results remain unavailable rather than clamped/fabricated.
10. IV Skew compares symmetric OTM wings and requires all wing inputs.
11. Current Vol PCR uses ATM±2 contract traded quantity.
12. Roll Vol PCR is ratio-of-summed-volumes, never average-of-PCRs.
13. Day Vol PCR is never presented as full-chain unless coverage is proven; the subscribed-universe diagnostic is always labelled with its universe and "Since" time.
14. Band-roll semantics are explicit; rolling metrics never pretend the basket stayed physically unchanged.
15. Restart/replay reproduces the same metrics from persisted causal data via sidecar insert-if-missing / verify-if-present semantics (section 71.2).
16. New metrics do not change trading/scoring/execution behaviour.
17. Compact-grid hiding preserves the existing detailed diagnostics.
18. Telegram screenshot remains readable after the compact Options grid is introduced.

# 29. Options-grid decisions frozen so far

As of **08 October 2026**:

- Primary live options view = CE+PE BOTH compact grid.
- Options continue to use the futures adaptive bars as their clock.
- Keep Seq, End IST, Center and Roll?.
- Keep CE/PE current and rolling premium change.
- Show raw **contract-quantity** Strict and Enriched delta for the current bar.
- Show raw rolling Strict for CE and PE.
- Keep rolling Enriched internally rather than displaying it in V1.
- Show center-contract CE/PE OI Δ.
- Add center-contract CE/PE Position classification using midpoint + OI change.
- Add center CE ΔIV and PE ΔIV.
- Add symmetric OTM-wing IV Skew.
- Retain ΔSkew internally.
- Add ATM±2 current-bar Vol PCR.
- Add ATM±2 Roll Vol PCR as ratio of summed volumes.
- Full-chain Day Vol PCR is deferred/hidden until coverage is proven; only the labelled Subscribed-Universe diagnostic may exist.
- Do not interpret PCR alone as bullish/bearish.
- Calculate center CE/PE MicroDev and OFI internally, not V1 visible.
- Calculate option Activity/s and center Straddle Δ internally, not V1 visible.
- Keep existing notional/ratio/coverage/OI diagnostic calculations; hide rather than remove.
- No composite score and no trading rule are introduced.

# 30. Former open items — resolutions

1. **Futures basis spot freshness** — measured-then-approved process (section 9.6); blocker 0.4.1.
2. **Option quote freshness** — 5 seconds, reused (section 24.5).
3. **Day PCR coverage** — full-chain deferred; Subscribed-Universe diagnostic only (sections 25.3–25.5).
4. **Persistence vs recomputation** — versioned sidecar tables; residual supplemental values via a shared pure projection (sections 32.4, 71).
5. **Compact vs diagnostic UX** — compact default plus Diagnostic view toggle (section 0.3).
6. **Telegram composition** — section 18.4.
7. **Screenshot selection state** — deterministic target-bar center CE/PE (section 18.2).

# 31. Residual Grid Live-Observation Plan — DESIGN LOCKED

**Status:** DESIGN LOCKED as of 08 October 2026, amended the same day: adjacent-bar deltas for all new residual deltas, the legacy bridged persisted Residual Δ retained for diagnostics only, and a shared pure projection instead of a sidecar table (sections 32.4, 34, 35, 43).

**Scope:** Adaptive Market Observer → **CE / PE theoretical residual — fixed 09:30 diagnostic**.

The residual grid has one narrow purpose:

> **Show whether CE/PE premiums are richer or cheaper than the fixed 09:30 theoretical expectation, identify which side is driving the dislocation, and show whether that dislocation is strengthening or weakening.**

Do not duplicate Futures-grid or Options-grid metrics here.

Specifically, do **not** add OFI, MicroDev, PCR, OI, IV change or IV skew to the residual grid. Those belong in the Futures/Options grids.

The residual model remains a fixed-09:30 diagnostic, not a trading score or entry/exit rule.

## 31.1 Existing residual model semantics remain unchanged

The existing residual framework remains:

- diagnostic anchor frozen at 09:30;
- fixed diagnostic strike composition for the entire session;
- selectable ATM or ATM±2 residual variant;
- actual option price from causal valid bid/ask midpoint;
- expected option price produced from the existing pricing model;
- anchor IV frozen at 09:30;
- current time-to-expiry allowed to decay naturally;
- modeled underlying moved from the 09:30 synthetic anchor by the observed futures change;
- no future quote may be used;
- stale/missing required quotes make the residual unavailable.

For a side:

    ActualChange = ActualNow - BasePrice09:30

    ExpectedChange = ExpectedNow - BasePrice09:30

    Residual = ActualNow - ExpectedNow

    ResidualPct = 100 × Residual / BasePrice09:30

Current directional residual remains:

    DirectionalResidualPct =
      CEResidualPct - PEResidualPct

Positive directional residual means CE is relatively richer and/or PE relatively cheaper than the frozen theoretical expectation.

Negative directional residual means PE is relatively richer and/or CE relatively cheaper.

This definition is preserved for continuity with existing research/history.

# 32. FINAL TARGET compact Residual grid

Displayed only as slices land (section 0.3). Slice 1 shows the fields that already exist (Seq, End IST, Future Δ09:30, CE Res %, PE Res %, Directional Res %, Direction, Relation, Quote Age). Slice 3 adds CE Res Δ%, PE Res Δ%, Adjacent Directional Res Δ and Straddle Res %.

Final target visible order:

    Seq | End IST | Future Δ09:30
    | CE Res % | CE Res Δ%
    | PE Res % | PE Res Δ%
    | Directional Res % | Adjacent Directional Res Δ
    | Straddle Res %
    | Direction | Relation
    | Quote Age

This becomes the primary compact live residual grid. The legacy persisted bridged Residual Δ is not part of it (section 35.3).

The purpose of the compact row is:

    underlying displacement
    → CE dislocation and its change
    → PE dislocation and its change
    → relative directional dislocation and its change
    → common CE+PE richness/cheapness
    → direction/relationship summary
    → quote quality

## 32.1 Seq

Same completed futures adaptive-bar sequence used by the Futures and Options grids.

## 32.2 End IST

Same completed futures adaptive-bar causal end timestamp.

## 32.3 Future Δ09:30

Keep the existing value:

    FutureDelta0930 =
      FutureNow - Future09:30

This provides the underlying move against which the fixed residual model is being evaluated.

## 32.4 Shared pure residual projection

CE Res Δ%, PE Res Δ%, Adjacent Directional Res Δ, Straddle Res % and the straddle-normalized directional residual (section 39) are deterministic functions of immutable persisted data: the persisted `AdaptiveOptionResidualBarRow`s (per variant, ordered by `BarSeq`) and the frozen `AdaptiveResidualAnchorComponentRow`s (`Price0930`, `IsCenterStrike`). They are produced by **one pure component** (no database access and no wall-clock state inside the formula) shared by the Dashboard, the commentary frame builder and tests/replay.

`CEBase09:30` and `PEBase09:30` are the same sums the residual model uses (the center component for ATM; all components of that side for ATM±2). No residual sidecar table is created unless a need appears that cannot be reconstructed deterministically.

# 33. CE Res % and PE Res %

Keep the existing side residual percentages:

    CEResidualPct =
      100 × (CEActual - CEExpected) / CEBase09:30

    PEResidualPct =
      100 × (PEActual - PEExpected) / PEBase09:30

For ATM, CE/PE refer to the fixed center contracts.

For ATM±2, CE/PE values are the corresponding fixed five-contract side aggregates established by the 09:30 anchor.

Interpretation:

- positive CE Res % → calls are richer than frozen-model expectation;
- negative CE Res % → calls are cheaper than expectation;
- positive PE Res % → puts are richer than expectation;
- negative PE Res % → puts are cheaper than expectation.

Residual percentages are relative to their own 09:30 side baseline and must not be treated as equivalent absolute rupee dislocations.

# 34. New visible metrics: CE Res Δ% and PE Res Δ%

The existing grid shows the level of each residual but not how each leg changed from the previous completed adaptive bar.

Add, with **adjacent-bar semantics**:

    CEResidualDeltaPct_t = CEResidualPct_t - CEResidualPct_(t-1)
    PEResidualDeltaPct_t = PEResidualPct_t - PEResidualPct_(t-1)

only when bar t-1 — the **immediately preceding completed bar** — had a valid residual reading for the **same residual variant** (ATM compared with ATM; ATM±2 compared with ATM±2). If bar t-1 or bar t is unavailable the delta is unavailable. A reading is never compared with an earlier valid reading across a gap.

These values come from the shared pure residual projection (section 32.4). The legacy persisted `ResidualDelta` is not used.

Interpretation:

Example A:

    CE Res %      +4.0
    PE Res %      -3.0
    CE Res Δ%     +0.2
    PE Res Δ%     -2.5

The directional dislocation is strengthening mainly because PE is becoming increasingly cheap relative to expectation.

Example B:

    CE Res %      +4.0
    PE Res %      -3.0
    CE Res Δ%     +2.8
    PE Res Δ%     -0.1

The same directional residual level is now being driven mainly by calls becoming richer.

This distinction is the primary reason to add the two leg-specific residual-delta columns.

# 35. Directional Res % and Adjacent Directional Res Δ

## 35.1 Directional Res %

Keep the existing definition:

    DirectionalResidualPct = CEResidualPct - PEResidualPct

Do not replace it in V1 because existing research/history already uses this exact value.

## 35.2 Adjacent Directional Res Δ

    AdjacentDirectionalResidualDelta_t = DirectionalResidualPct_t - DirectionalResidualPct_(t-1)

only when the immediately preceding completed bar had a valid residual for the same variant; otherwise unavailable. Whenever CE Res Δ%, PE Res Δ% and this value are all available:

    AdjacentDirectionalResidualDelta = CEResidualDeltaPct - PEResidualDeltaPct

(guaranteed, because all three use the same adjacent pair of readings).

Interpretation:
- positive → CE-vs-PE relative dislocation is becoming more positive;
- negative → it is becoming more negative;
- near zero → little change in the directional residual.

Commentary uses this adjacent value only, never the legacy bridged one.

## 35.3 Legacy Bridged Directional Δ — retained, not in the compact grid

The persisted `AdaptiveOptionResidualBarRow.ResidualDelta` is produced by the engine's `_previousResidual`, which is updated only on valid readings and therefore **bridges across unavailable bars**. That behaviour is preserved unchanged for restart/historical compatibility (`VerifyRow` reconciles it). Its calculation and persistence are not modified.

It appears only in the Diagnostic view / internal presentation, labelled **Legacy Bridged Directional Δ**. It is not shown beside the adjacent CE/PE deltas in the compact grid, so the compact row never mixes two delta meanings.

# 36. New visible metric: Straddle Res %

The current Common residual % is an equal-weight average of CE and PE residual percentages:

    CommonResidualPct =
      (CEResidualPct + PEResidualPct) / 2

Retain that existing value internally for backward compatibility, but the compact grid should instead expose a more economically interpretable **Straddle Res %**.

## 36.1 Formula

Let:

    StraddleActual =
      CEActual + PEActual

    StraddleExpected =
      CEExpected + PEExpected

    StraddleResidual =
      StraddleActual - StraddleExpected
      = CEResidual + PEResidual

Use the common 09:30 premium denominator:

    StraddleBase09:30 =
      CEBase09:30 + PEBase09:30

Then:

    StraddleResidualPct =
      100 × StraddleResidual / StraddleBase09:30

For the ATM±2 variant, CE/PE values are the corresponding fixed side aggregates, so the formula applies to the aggregate fixed diagnostic basket.

## 36.2 Interpretation

Positive Straddle Res %:

> Combined CE+PE premium is richer than the frozen-09:30-IV theoretical model expects.

Negative Straddle Res %:

> Combined CE+PE premium is cheaper than the frozen model expects.

This separates **common premium/volatility richness** from **relative CE-vs-PE directional dislocation**.

Example:

    Directional Res %    +6.0
    Straddle Res %       +0.3

Interpretation:
- primarily a relative/directional CE-vs-PE repricing;
- little common CE+PE richness.

Versus:

    Directional Res %    +1.0
    Straddle Res %       +7.0

Interpretation:
- little relative directional dislocation;
- both option sides are collectively much richer than the frozen model.

This complements the live IV metrics in the Options grid without duplicating them.

# 37. Direction and Relation

## 37.1 Direction

Keep the existing residual direction derived from the sign of DirectionalResidualPct:

- positive → UP;
- negative → DOWN;
- zero → NEUTRAL.

This is a residual-direction label, not a trade recommendation.

## 37.2 Relation

Keep the existing comparison against the current futures rolling price direction:

- same non-zero sign → ALIGN;
- opposite non-zero sign → OPPOSE;
- either side neutral → NEUTRAL.

This remains a compact descriptive comparison between residual direction and futures rolling direction.

The separate Future roll dir column is no longer required in the compact grid because Relation already conveys the comparison and the Futures grid shows the underlying rolling state directly.

Retain Future roll direction internally/diagnostically.

# 38. Quote Age

Keep maximum quote age visible:

    QuoteAge =
      max age of all option quotes required by the selected residual variant

Purpose:
- immediately distinguish a genuine residual reading from one produced near the permitted freshness boundary;
- make data quality visible without opening diagnostics.

If any required quote breaches the final approved option freshness rule, the residual reading itself should become unavailable rather than merely showing a large age.

# 39. Straddle-normalized directional residual — calculate internally, do not display initially

The existing directional residual subtracts two percentages that use different denominators:

    CEResidualPct - PEResidualPct

On 0-DTE one side can become very cheap, causing a small absolute residual on that side to become a large percentage.

Therefore calculate a second diagnostic with a common denominator:

    DirectionalResidualStraddleNormPct =
      100 × (CEResidual - PEResidual)
      / (CEBase09:30 + PEBase09:30)

Do **not** replace the existing DirectionalResidualPct in V1.

Retain both:
- existing directional residual for research continuity;
- straddle-normalized directional residual for future comparison/validation.

No threshold or trading rule is attached to the new normalized metric.

# 40. Residual anchor context above the grid

Keep a compact anchor/context area above the residual rows.

Visible context should include:

- **Residual variant:** ATM or ATM±2 selector;
- **Expiry / DTE**;
- **Future @09:30**;
- **Synthetic weekly @09:30**;
- **Fixed diagnostic center**;
- **Components:** 1 CE + 1 PE for ATM, or 5 CE + 5 PE for ATM±2.

Continue to state clearly that:

> **Residual strike composition is frozen at 09:30 for the entire session.**

The residual grid must never silently roll its diagnostic strike composition with intraday ATM.

# 41. Residual fields to hide, not remove

Hide from the primary compact residual grid:

- CE Actual Δ;
- CE Expected Δ;
- CE Residual points;
- PE Actual Δ;
- PE Expected Δ;
- PE Residual points;
- Future roll direction;
- Legacy Bridged Directional Δ (the persisted `ResidualDelta`);
- existing Common Residual %;
- modeled underlying;
- raw CE/PE actual prices;
- raw CE/PE expected prices;
- any existing anchor/component diagnostics not required by the compact view.

Retain all of these in the model/persistence/diagnostic path.

They remain important for:
- debugging;
- theoretical-model validation;
- explaining an abnormal residual;
- historical research;
- regression testing.

No residual functionality is removed by hiding a column.

# 42. Residual-grid data-quality and causality requirements

Implementation must prove:

1. The 09:30 residual anchor and its strike composition remain immutable throughout the session.
2. ATM and ATM±2 histories remain separate.
3. No option quote later than the bar's causal diagnostic boundary is used.
4. Required quote freshness is enforced consistently.
5. CE/PE residual Δ% use the immediately preceding completed bar only and are unavailable if that bar's residual for the same variant was unavailable.
6. Adjacent Directional Res Δ follows the same adjacency rule and equals CE Res Δ% − PE Res Δ% whenever all three are available; the legacy persisted `ResidualDelta` is unchanged and not used by the compact grid or commentary.
7. Straddle Res % uses the common 09:30 CE+PE denominator.
8. The existing Directional Res % formula remains unchanged.
9. The new straddle-normalized directional residual is additive/diagnostic only and cannot silently replace existing research fields.
10. 0-DTE time decay continues to use the existing time-to-expiry convention.
11. Restart/replay reproduces identical residual rows and deltas.
12. Compact-grid hiding does not delete existing actual/expected/residual-point diagnostics.
13. No residual metric is used to alter scoring, entries, exits or execution.
14. Telegram/browser rendering shows the same residual values for the same persisted completed bar.

# 43. Residual-grid decisions frozen

As of **08 October 2026**, the Residual grid design is DESIGN LOCKED as:

    Seq | End IST | Future Δ09:30
    | CE Res % | CE Res Δ%
    | PE Res % | PE Res Δ%
    | Directional Res % | Adjacent Directional Res Δ
    | Straddle Res %
    | Direction | Relation
    | Quote Age

Also frozen:

- Keep fixed 09:30 anchor semantics.
- Keep ATM and ATM±2 selector.
- Keep current Directional Residual % definition for continuity.
- Add CE and PE residual change independently, with adjacent-bar semantics.
- Add Adjacent Directional Res Δ; keep the legacy persisted bridged `ResidualDelta` untouched and diagnostic-only.
- Add Straddle Residual % using a common 09:30 CE+PE premium denominator.
- Calculate straddle-normalized directional residual internally only.
- Derive all new residual values with one shared pure projection (section 32.4); no residual sidecar table.
- Keep Quote Age visible.
- Keep Direction and Relation.
- Hide rather than remove actual/expected/intermediate diagnostic columns.
- Do not duplicate Options-grid IV/OI/PCR/MicroDev/OFI in this grid.
- No composite score or trading rule is introduced.

# 44. Running Market Commentary — IMPLEMENTATION PLAN

**Status:** DESIGN LOCKED for Slices 4A–4C. Telegram behaviour beyond the V1 conservative set is deferred (section 0.4). Commentary is observational only (section 65).

**Purpose:** Add a deterministic, event-driven commentary layer on top of the finalized Futures, Options and Residual observations. The commentary exists to reduce the amount of manual interpretation required while watching live. It must explain meaningful market-state changes without generating a message after every adaptive bar.

The commentary engine is observational. It does not place orders, modify scoring, or change any existing trading/execution path. The directions it assigns are interpretation hypotheses (section 0.5).

The core output of every commentary event is:

    Event
    + Event Bias
    + Market Regime
    + Lifecycle
    + EvidenceAgreement
    + Primary Evidence
    + Confirmations
    + Contradictions

Event Bias is explicitly:

    LONG | SHORT | NEUTRAL

This means the direction the event currently supports for NIFTY under the plan's interpretation hypotheses. It is **not** an automatic instruction to place a trade.

# 45. Commentary architecture

The V1 processing flow is:

    completed futures adaptive bar
            |
            v
    finalized Futures metrics
    finalized Options metrics
    finalized Residual metrics
            |
            v
    CommentaryFrame
            |
            v
    Deterministic Event Detector
            |
            v
    Lifecycle / Regime Engine
            |
            v
    PostgreSQL Event Store
            |
            +----> Dashboard commentary panel
            |
            +----> Notification policy
                        |
                        v
                 PostgreSQL outbox
                        |
                        v
                     Telegram

The event detector must evaluate only completed adaptive bars.

The commentary engine must not read a later live quote after the completed bar boundary in order to reinterpret that bar. All values in the CommentaryFrame must already obey the causality/freshness rules defined elsewhere in this document.

# 46. CommentaryFrame — deterministic input contract

Create one immutable CommentaryFrame per completed adaptive bar after the three observation layers are finalized.

The frame should contain references/values required for commentary and no wall-clock-dependent calculations.

At minimum it must contain:

### Identity / timing
- SessionId
- TradeDate
- BarSeq
- BarEndAvailableAtUtc

### Futures
- BarPriceDisplacement
- RollingPriceDisplacement
- StrictDelta
- EnrichedDelta
- RollingStrictDelta
- RollingEnrichedDelta
- RollingStrictAbsDeltaChange
- RollOiDelta
- Urgency
- MicroDev
- OFI
- DeltaBasis
- RollingEfficiency
- Evolution
- State
- ReadinessMet (`AdaptiveReadinessPolicy`: ten consecutive valid completed bars)
- availability flags for the mandatory rolling inputs (RollingStrictDelta, RollingEnrichedDelta, RollingPriceDisplacement, RollOiDelta)
- other data-quality availability flags required by those metrics

### Options
- CenterStrike
- BandRolled
- CE/PE BarPriceChange
- CE/PE RollingPriceChange
- CE/PE StrictDelta
- CE/PE EnrichedDelta
- CE/PE RollingStrictDelta
- center CE/PE OiDelta
- CEPosition
- PEPosition
- CE/PE DeltaIV
- IVSkew
- current VolPCR
- RollingVolPCR
- DayVolPCR and its coverage status
- option quote/data-quality flags

### Residual
Use the selected primary residual view configured for commentary. V1 should default to ATM±2 unless a later explicit decision changes it.

Include:
- CEResidualPct
- CEResidualDeltaPct
- PEResidualPct
- PEResidualDeltaPct
- DirectionalResidualPct
- AdjacentDirectionalResidualDelta (unavailable unless the immediately preceding completed bar had a valid residual for the same variant)
- StraddleResidualPct
- ResidualDirection
- Relation
- QuoteAge
- availability flag

The CommentaryFrame must be serializable for deterministic tests/replay, but it does not have to be persisted as another full duplicate of all market data if the source metrics are already durably stored.

# 47. Directional concepts

The commentary system deliberately keeps three separate concepts.

## 47.1 Event Bias

Values:

    Long
    Short
    Neutral

Meaning:

- Long: this event supports upward NIFTY direction.
- Short: this event supports downward NIFTY direction.
- Neutral: the event is important but does not yet justify directional interpretation.

Event Bias must not be labeled Buy/Sell because the engine is describing market behaviour, not sending an order instruction.

## 47.2 Market Regime

Values:

    Bullish
    Bearish
    Transition
    Conflict
    Neutral

Meaning:

- Bullish: established broader evidence remains predominantly upward.
- Bearish: established broader evidence remains predominantly downward.
- Transition: the established regime has been challenged by an explicitly listed transition event (section 54).
- Conflict: a FamilyConflict classification (section 54.1): available independent families point in opposite directions and no primary event fires.
- Neutral: no established directional regime.

The Market Regime can differ from Event Bias.

Example:

    Event        SellerAbsorption
    Event Bias   Neutral
    Regime       Bearish

This means the broader market remains bearish, but the newest event no longer supports adding directional weight to the short side.

## 47.3 Bias transition

Persist on every commentary event row:

    PreviousBias   bias state at the previous checkpoint (EventBias of the then-active event; NEUTRAL when no event was active)
    CurrentBias    EventBias of the event persisted by this row (NEUTRAL for a Resolved row, because the event is cleared)
    BiasChanged    PreviousBias != CurrentBias

`BiasChanged` is a factual column. Which bias changes cause a Telegram message is decided only by the narrower rule in section 60.1.

Transitions that can occur:

    Short -> Neutral
    Neutral -> Long
    Long -> Neutral
    Neutral -> Short
    Long -> Short   (always a Flipped event, section 55.6)
    Short -> Long   (always a Flipped event, section 55.6)

A directional-to-opposite or directional-to-neutral transition produced by a persisted event is more important than another bar retaining the same bias.

# 48. V1 futures event classifier

The primary event classifier should begin from futures behaviour because the adaptive clock and principal directional state originate there.

Define current aligned flow:

    BuyAligned =
      StrictDelta > 0
      AND EnrichedDelta > 0
      AND RollingStrictDelta > 0
      AND RollingEnrichedDelta > 0

    SellAligned =
      StrictDelta < 0
      AND EnrichedDelta < 0
      AND RollingStrictDelta < 0
      AND RollingEnrichedDelta < 0

Define flow conflict:

    FlowConflict =
      sign(StrictDelta) != sign(EnrichedDelta)
      OR sign(RollingStrictDelta) != sign(RollingEnrichedDelta)

Zero values are not forced into Buy or Sell alignment.

**Evaluation gates and mandatory-input availability.**

1. Events are produced only for a bar whose frame has `ReadinessMet == true` (the shared ten-valid-contiguous-completed-bar gate, `AdaptiveReadinessPolicy`). During warm-up the classification is `NoMaterialEvent`.
2. Every primary event requires its mandatory inputs to be available for that bar. BuyerExpansion, SellerExpansion, ShortCovering and LongLiquidation require **Roll Strict, Roll Enriched, Roll ΔPx and Roll OI Δ** (plus the current-bar Strict, Enriched and Bar ΔPx they test). The absorption events require Roll Strict, Roll Enriched and Roll OI Δ. FlowConflict requires the signs it compares. If any mandatory input is unavailable, the classification is `NoMaterialEvent` for that bar — an unavailable rolling window (for example after a gap in option data) silences these events until the inputs return.
3. Missing **external** confirmation (Book, Basis, Options, Residual) is different: it is neither confirmation nor contradiction (sections 52–53) and never blocks a futures-core event.

V1 primary events:

## 48.1 BuyerExpansion

Requirements:

    BuyAligned
    AND RollOiDelta > 0
    AND BarPriceDisplacement > 0
    AND RollingPriceDisplacement > 0

Event Bias:

    LONG

Meaning:

Fresh/expanding futures positioning is being initiated with buyer-aggressive flow and price is accepting it upward.

## 48.2 SellerExpansion

Requirements:

    SellAligned
    AND RollOiDelta > 0
    AND BarPriceDisplacement < 0
    AND RollingPriceDisplacement < 0

Event Bias:

    SHORT

## 48.3 ShortCovering

Requirements:

    BuyAligned
    AND RollOiDelta < 0
    AND BarPriceDisplacement > 0
    AND RollingPriceDisplacement > 0

Event Bias:

    LONG

This is contraction/covering-compatible behaviour and must remain distinguishable from BuyerExpansion.

## 48.4 LongLiquidation

Requirements:

    SellAligned
    AND RollOiDelta < 0
    AND BarPriceDisplacement < 0
    AND RollingPriceDisplacement < 0

Event Bias:

    SHORT

## 48.5 BuyerAbsorption

Requirements:

    BuyAligned
    AND RollOiDelta >= 0
    AND BarPriceDisplacement <= 0

Initial Event Bias:

    NEUTRAL

The important observation is aggressive buying without expected upward price acceptance.

Do not immediately label this SHORT.

## 48.6 SellerAbsorption

Requirements:

    SellAligned
    AND RollOiDelta >= 0
    AND BarPriceDisplacement >= 0

Initial Event Bias:

    NEUTRAL

Do not immediately label this LONG.

## 48.7 FlowConflict

Requirements:

    FlowConflict == true

Event Bias:

    NEUTRAL

This is a Neutral classification; no same-bar directional interpretation is made.

If no explicitly defined primary event is satisfied, the classification is **`NoMaterialEvent`**. `NoMaterialEvent` is an internal outcome: it is **not persisted as an event row**, produces **no Telegram notification**, and **still advances the runtime checkpoint**. Silence is intentional: combinations such as buy-aligned flow with falling OI and non-positive price, or buy-aligned flow with positive bar price but non-positive rolling price, are deliberately not given invented event types. If a previously active event exists, section 55.7 governs its resolution.

# 49. Absorption-to-reversal confirmation

Absorption is deliberately two-stage.

## 49.1 SellerRejectionConfirmed

This can occur only if the active event at the previous checkpoint is SellerAbsorption (section 55.7 gives rejection precedence over the base classification).

Mandatory evidence:

    BarPriceDisplacement > 0

and one of:

    Evolution == Weakening
    OR Evolution == FlipToBuyer
    OR RollingStrictAbsDeltaChange < 0

Then require confirmation from at least **two independent evidence families** below:

### Book family

Book family direction is Long (section 52.1).

The book family counts once even if both OFI and MicroDev agree.

### Inter-market family

    DeltaBasis > 0

### Options family

Strong LONG-supporting option evidence as defined in section 50.

### Residual family

    DirectionalResidualPct > 0

If mandatory conditions and at least two independent confirming families are present:

    EventType    SellerRejectionConfirmed
    EventBias    LONG
    Regime       per section 54 (Transition when previously Bullish/Bearish)
    Lifecycle    Confirmed

A later BuyerExpansion can move the regime from Transition to Bullish.

## 49.2 BuyerRejectionConfirmed

Mirror rule after BuyerAbsorption.

Mandatory:

    BarPriceDisplacement < 0

and one of:

    Evolution == Weakening
    OR Evolution == FlipToSeller
    OR RollingStrictAbsDeltaChange < 0

Require at least two independent confirmations:

### Book

Book family direction is Short (section 52.1).

### Inter-market

    DeltaBasis < 0

### Options

Strong SHORT-supporting option evidence.

### Residual

    DirectionalResidualPct < 0

Result:

    EventType    BuyerRejectionConfirmed
    EventBias    SHORT
    Regime       per section 54 (Transition when previously Bullish/Bearish)
    Lifecycle    Confirmed

This two-stage rule prevents an early absorption observation from being mislabeled as an immediate reversal.

# 50. Options directional-support rules for commentary

PCR alone must never create Long or Short bias.

IV alone must never create Long or Short bias.

For V1, classify **strong Options support** using Position + aggressive flow.

## 50.1 Strong LONG options support

At least one of:

### Call-long build

    CEPosition == CallLongBuild
    AND CE StrictDelta > 0

### Put writing

    PEPosition == PutWriting
    AND PE StrictDelta < 0

These are the primary fresh LONG-supporting option patterns.

The following may be recorded as supporting/mature evidence but do not create strong LONG support by themselves:

    CEPosition == CallShortCover

because covering can occur late in an existing move.

## 50.2 Strong SHORT options support

At least one of:

### Put-long build

    PEPosition == PutLongBuild
    AND PE StrictDelta > 0

### Call writing

    CEPosition == CallWriting
    AND CE StrictDelta < 0

The following is supporting/mature evidence but not sufficient by itself:

    PEPosition == PutShortCover

because it may represent a later-stage move.

## 50.3 IV, skew and PCR as confirmation/context

Examples of LONG-compatible confirmation include:

- CE DeltaIV positive while PE DeltaIV is flat/falling;
- IV skew falling;
- Vol PCR high because PE activity is PutWriting rather than PutLongBuild;
- current PCR moving below its rolling/session context while call-long activity expands.

Examples of SHORT-compatible confirmation include:

- PE DeltaIV positive while CE DeltaIV is flat/falling;
- IV skew increasing;
- high Vol PCR accompanied by PutLongBuild and positive PE aggressive flow.

These fields strengthen/explain an options conclusion but must not be converted into standalone direction without the Position/flow context.

# 51. Residual directional support

Residual direction is defined by the finalized residual model:

    DirectionalResidualPct > 0  => LONG-supporting residual
    DirectionalResidualPct < 0  => SHORT-supporting residual
    DirectionalResidualPct == 0 => Neutral

Residual acceleration adds context:

    AdjacentDirectionalResidualDelta > 0
        => residual is becoming more LONG-oriented

    AdjacentDirectionalResidualDelta < 0
        => residual is becoming more SHORT-oriented

The adjacent delta is usable only when the immediately preceding completed bar had a valid residual for the same variant; the legacy bridged persisted `ResidualDelta` is never used.

Leg-specific residual deltas should be used in commentary text to explain which side is driving the change.

StraddleResidualPct remains common-richness/common-cheapness context and does not create Long/Short direction by itself.

# 52. Independent evidence families

Never calculate EvidenceAgreement by counting every raw column because many metrics are correlated.

For commentary, evidence is grouped into independent families:

1. **Futures core** — aggressive flow + OI + price response.
2. **Book** — MicroDev / OFI.
3. **Inter-market** — DeltaBasis.
4. **Options** — Position + option aggressive flow; IV/PCR/skew as context.
5. **Residual** — directional theoretical dislocation.

Within a family, multiple agreeing metrics improve the explanation but the family still counts only once when calculating EvidenceAgreement.

## 52.1 Family direction definitions (used for Conflict and EvidenceAgreement)

Each family has a direction in {Long, Short, Neutral} or is Unavailable. Definitions use signs only; no magnitude threshold is introduced. These directions are interpretation hypotheses (section 0.5).

- **FuturesCore** — Long if `BuyAligned`; Short if `SellAligned`; otherwise Neutral. Price response and OI are part of the primary-event definitions, not of the family direction. Unavailable if an alignment input is unavailable.
- **Book** — Long if (OFI > 0 or MicroDev > 0) and neither is < 0; Short if (OFI < 0 or MicroDev < 0) and neither is > 0; Neutral if they disagree or both are zero; Unavailable if both are unavailable. OFI and MicroDev together count as one family.
- **InterMarketBasis** — Long if ΔBasis > 0; Short if < 0; Neutral if 0; Unavailable while ΔBasis is unavailable (including until the spot freshness rule is approved, section 9.6).
- **Options** — Long if strong LONG options support (section 50.1) holds and strong SHORT support (50.2) does not; Short if 50.2 holds and 50.1 does not; Neutral if both hold, or neither holds while Position/flow inputs are available; Unavailable if no Position/flow inputs are available for either side.
- **Residual** — Long if `DirectionalResidualPct` > 0; Short if < 0; Neutral if 0; Unavailable if the residual reading is unavailable. The adjacent delta is explanatory context and does not change the family direction.

For an event with bias X, a family **supports** it when its direction equals X and **contradicts** it when its direction is the opposite of X. Neutral and Unavailable are neither.

# 53. EvidenceAgreement — V1 deterministic bands

V1 uses discrete `EvidenceAgreement` rather than a false-precision numeric score.

Values:

    LOW
    MEDIUM
    HIGH

It means how many independent evidence families agree with the deterministic event interpretation. It is **not** a calibrated probability, historical win rate, validated predictive confidence or trading confidence (section 0.5).

The four **external** families are Book, InterMarketBasis, Options and Residual; FuturesCore is the primary event itself. Support/contradiction follows section 52.1. **Unavailable evidence is neither support nor contradiction and carries no extra penalty**; it simply limits how many families can support.

For a directional primary event (BuyerExpansion, SellerExpansion, ShortCovering, LongLiquidation) and for the confirmed rejection events:

### HIGH

- at least three of the four external families support the event bias;
- no external family contradicts it.

### MEDIUM

- at least one external family supports it;
- no more than one external family contradicts it;
- and it is not HIGH.

### LOW

- anything else.

For the neutral absorption events the interpretation is that the aggressive side is failing to move price: for **SellerAbsorption**, "supporting" families are those whose direction is Long and "contradicting" are Short; for **BuyerAbsorption** the reverse. The same HIGH/MEDIUM/LOW bands apply.

For **FlowConflict** and **FamilyConflict** (no agreement exists by definition), `EvidenceAgreement` is LOW.

# 54. Market Regime state machine

V1 maintains one current Market Regime per session. Initial state: `Neutral`.

**There is no time-based regime expiry in V1.** Bullish/Bearish never decay to Neutral because bars elapsed. The regime persists until an explicitly listed transition changes it or the session ends; the next session starts Neutral.

"Directional primary event" means BuyerExpansion, ShortCovering (both LONG) and SellerExpansion, LongLiquidation (both SHORT). Each bar has exactly one classification; a transition not listed below leaves the regime unchanged.

### Neutral -> Bullish
A LONG directional primary event with EvidenceAgreement MEDIUM or HIGH.

### Neutral -> Bearish
A SHORT directional primary event with EvidenceAgreement MEDIUM or HIGH.

### Bullish -> Transition
BuyerAbsorption, OR BuyerRejectionConfirmed, OR any SHORT directional primary event (any EvidenceAgreement).

### Bearish -> Transition
SellerAbsorption, OR SellerRejectionConfirmed, OR any LONG directional primary event (any EvidenceAgreement).

### Transition -> Bullish
BuyerExpansion with EvidenceAgreement MEDIUM or HIGH.

### Transition -> Bearish
SellerExpansion with EvidenceAgreement MEDIUM or HIGH.

### Any regime -> Conflict
A `FamilyConflict` classification (section 54.1).

### Conflict -> Bullish / Bearish
The next LONG / SHORT directional primary event with EvidenceAgreement MEDIUM or HIGH.

### Session end / reset
The next session starts Neutral.

## 54.1 FamilyConflict (executable definition)

`FamilyConflict` (EventBias NEUTRAL, EvidenceAgreement LOW) is the classification when **all** hold:

- no explicit primary futures event fires on the bar (the classification would otherwise be `NoMaterialEvent`; FlowConflict counts as a primary event);
- at least one available independent family (FuturesCore, Book, InterMarketBasis, Options, Residual; section 52.1) has direction Long;
- at least one available independent family has direction Short.

Unavailable families do not participate. One unavailable family is never itself a conflict.

# 55. Event lifecycle

Lifecycle values:

    New
    Strengthening
    Weakening
    Confirmed
    Resolved
    Flipped

Do not persist an event row for every ordinary continuation bar.

## 55.1 New

An event classification appears while no event is active, or a different EventType replaces the active event without an opposite directional bias (section 55.7). The same event reappearing after it was Resolved is New.

## 55.2 Strengthening

The same event remains in force (its type conditions still hold) and at least one of these explicit changes occurs:

- Evolution changes toward Strengthening;
- RollingStrictAbsDeltaChange > 0;
- a previously absent independent confirmation family becomes supportive;
- EvidenceAgreement improves LOW -> MEDIUM or MEDIUM -> HIGH.

Do not generate Strengthening repeatedly only because raw magnitudes drift.

## 55.3 Weakening

The same event remains in force (its type conditions still hold) and at least one of these explicit deteriorations occurs:

- Evolution becomes Weakening;
- RollingStrictAbsDeltaChange < 0;
- a previously supporting independent family becomes Neutral or opposing;
- EvidenceAgreement falls (HIGH -> MEDIUM or MEDIUM -> LOW).

No magnitude threshold is introduced. Strengthening/Weakening are not generated merely because raw magnitudes drift.

## 55.4 Confirmed

The lifecycle value of SellerRejectionConfirmed / BuyerRejectionConfirmed events (section 49).

## 55.5 Resolved

Persisted when an active event is followed by `NoMaterialEvent` (including because mandatory inputs became unavailable, section 55.8). One Resolved row is written for the previous event (EventBias NEUTRAL, PreviousBias = the old bias) and the active event is cleared.

## 55.6 Flipped

Persisted when a different event with the **opposite directional EventBias** replaces the active directional event. Examples:

    SellerExpansion -> BuyerExpansion
    LongLiquidation -> BuyerExpansion
    SellerExpansion -> ShortCovering

The new event row carries Lifecycle = Flipped. No separate Resolved row is written for the replaced event; the replacement itself closes the prior lifecycle.

## 55.7 Continuation, replacement and precedence

Evaluated once per completed bar, in strict BarSeq order:

1. **Rejection precedence.** If the previous checkpoint's active event is SellerAbsorption (BuyerAbsorption) and the SellerRejectionConfirmed (BuyerRejectionConfirmed) prerequisites of section 49 hold, that rejection event is the bar's classification. Otherwise the base classification of section 48 / 54.1 applies.
2. **Same event, no explicit change** (no Strengthening or Weakening condition): *unchanged continuation* — no event row, no Telegram; the runtime checkpoint advances.
3. **Same event, explicit Strengthening/Weakening condition:** one row with that lifecycle.
4. **No event active, a classification appears:** New (Confirmed for the rejection events).
5. **Different event replaces the active event:** if the two have opposite directional EventBias → Flipped; otherwise (neutral-bias event, or a same-direction different type such as BuyerExpansion -> ShortCovering) → New (Confirmed for the rejection events). No Resolved row is written for the replaced event.
6. **Active event followed by NoMaterialEvent:** one Resolved row (section 55.5).
7. **No event active and NoMaterialEvent:** nothing is persisted.

## 55.8 Data-gap behaviour

If a mandatory rolling input becomes unavailable the bar classifies as NoMaterialEvent and any active event is Resolved. The unavailability reason is recorded in `DataQualityJson` of the Resolved row. When the inputs return, the next classification is New (not a continuation).

# 56. Commentary event persistence — PostgreSQL is authoritative

Files must **not** be the primary store.

Create a PostgreSQL event table conceptually named:

    adaptive_commentary_events

Recommended fields:

    Id                         bigint primary key
    SessionId                  bigint not null
    TradeDate                  date not null
    BarSeq                     int not null
    OccurredAtUtc              timestamptz not null

    EventType                  enum/string not null
    EventBias                  enum/string not null
    MarketRegime               enum/string not null
    Lifecycle                  enum/string not null
    EvidenceAgreement          enum/string not null
    Severity                   enum/string not null
    CommentaryVersion          text not null

    PreviousEventId            bigint null
    PreviousBias               enum/string null
    BiasChanged                bool not null

    PrimaryEvidenceJson        jsonb not null
    ConfirmationEvidenceJson   jsonb not null
    ContradictionEvidenceJson  jsonb not null
    DataQualityJson            jsonb not null

    RenderedCommentary         text not null

    ShouldNotifyTelegram       bool not null
    NotificationReason        text null

    CreatedAtUtc               timestamptz not null

Foreign-key SessionId to the adaptive session where practical.

Rows exist only for lifecycle events (New, Strengthening, Weakening, Confirmed, Resolved, Flipped). `NoMaterialEvent` and unchanged continuation bars are never persisted as event rows. `CommentaryVersion` identifies the rule set that produced the row so history can be reproduced after any later rule change.

Recommended indexes:

    (SessionId, BarSeq)
    (TradeDate, OccurredAtUtc)
    (SessionId, EventBias, OccurredAtUtc)
    (SessionId, EventType, OccurredAtUtc)

Idempotency requirement:

Create a deterministic EventIdentity from:

    SessionId
    + BarSeq
    + EventType
    + Lifecycle
    + EventBias

and enforce uniqueness either through a stored identity column or an equivalent unique composite index.

Restart/replay of the same completed bar must not produce a duplicate event.

## 56.1 Evidence JSON content

Do not store only prose.

PrimaryEvidenceJson must contain the exact structured facts that created the event.

Example shape:

    {
      "futures": {
        "strictDelta": -18400,
        "enrichedDelta": -22600,
        "rollStrict": -121000,
        "rollOiDelta": 12450,
        "barPriceDelta": -7.5,
        "rollPriceDelta": -31.5
      }
    }

ConfirmationEvidenceJson can contain independent confirmation families such as:

    {
      "book": {
        "ofi": -6200,
        "microDev": -0.31
      },
      "basis": {
        "deltaBasis": -4.8
      },
      "options": {
        "pePosition": "PutLongBuild",
        "peStrictDelta": 9200,
        "peDeltaIv": 2.4
      },
      "residual": {
        "directionalResidualPct": -5.2,
        "directionalResidualDelta": -1.4
      }
    }

ContradictionEvidenceJson contains only meaningful opposing facts, not every unavailable metric.

DataQualityJson must record important unavailable/stale/degraded inputs used when determining EvidenceAgreement.

# 57. Commentary runtime checkpoint

Because many completed bars will legitimately produce **no commentary event**, event rows alone cannot indicate how far evaluation progressed.

Create a small operational checkpoint table conceptually named:

    adaptive_commentary_runtime

One row per SessionId.

Recommended fields:

    SessionId                  bigint primary key
    LastEvaluatedBarSeq        int not null
    CurrentEventId             bigint null
    CurrentEventType           enum/string null
    CurrentBias                enum/string not null
    CurrentRegime              enum/string not null
    CurrentLifecycle           enum/string null
    LastTelegramBarSeq         int null
    CommentaryVersion          text not null
    UpdatedAtUtc               timestamptz not null

This row is operational state, **not** the authoritative market history.

It must be rebuildable by replaying persisted completed bars and commentary events.

On restart:

1. Load session commentary runtime.
2. Load/reconstruct the current active event/regime.
3. Evaluate only completed bars after LastEvaluatedBarSeq.
4. Persist event(s) and checkpoint atomically for each processed bar where practical.
5. Never announce an old event as New solely because the process restarted.

# 58. Dashboard commentary panel

Add a compact **Live Market Commentary** panel to the Dashboard.

This panel should show the latest meaningful events, newest first.

Recommended visible content per event:

    HH:mm:ss IST
    Event name
    Bias: LONG / SHORT / NEUTRAL
    Regime: BULLISH / BEARISH / TRANSITION / CONFLICT / NEUTRAL
    Lifecycle
    EvidenceAgreement
    short deterministic explanation

Suggested maximum visible history:

    latest 5 meaningful events

Older history remains queryable from PostgreSQL; the live panel does not need to render the entire day.

Example:

    11:18:43  Seller Expansion
    Bias SHORT | Regime BEARISH | Strengthening | HIGH
    Aggressive selling remains dominant with expanding OI and accepted lower prices.
    OFI, basis and PE PutLongBuild confirm. Residual is aligned.

Example absorption:

    11:31:02  Seller Absorption
    Bias NEUTRAL | Regime BEARISH | New | MEDIUM
    Selling remains aggressive but price no longer moves lower.
    OFI and MicroDev have turned positive; basis is recovering.

Do not display raw JSON in the normal Dashboard panel.

The panel carries a standing one-line note: "EvidenceAgreement = how many independent evidence families agree under deterministic interpretation hypotheses; it is not a probability or a validated signal." (section 0.5).

# 59. Deterministic commentary rendering

V1 must **not** use an LLM to decide:
- EventType;
- Bias;
- MarketRegime;
- Lifecycle;
- EvidenceAgreement;
- whether Telegram should be notified.

Use deterministic templates.

A renderer should receive the structured event and produce concise text.

Recommended structure:

    [Event title]
    Bias / Regime / Lifecycle / EvidenceAgreement
    Primary evidence sentence.
    Confirmation sentence if present.
    Contradiction sentence if present.

Example:

    Seller expansion strengthening.
    Bias SHORT | Regime BEARISH | EvidenceAgreement HIGH.
    Seller-aggressive flow remains dominant with rising OI and accepted lower prices.
    OFI and basis weakened; PE PutLongBuild and bearish residual confirm.

The renderer must never invent evidence that is not present in the event JSON.

If a family is unavailable, either omit it or explicitly say it is unavailable when data quality itself matters.

An LLM can be considered later for non-critical end-of-day summaries, but it is out of V1 live event detection and notification.

# 60. Telegram notification policy — V1 conservative

The Dashboard shows every persisted lifecycle event. Telegram is stricter and, in V1, **only** the events below are automatically eligible. `Severity` (Info / Medium / High) is persisted for Dashboard presentation; Telegram eligibility is decided by these explicit rules, not by severity.

## 60.1 V1 Telegram-eligible events

1. `Lifecycle == Confirmed` (SellerRejectionConfirmed / BuyerRejectionConfirmed).
2. `Lifecycle == Flipped`.
3. An **actual bias change**: `PreviousBias` is directional (LONG or SHORT) and `CurrentBias` differs, produced by a persisted non-Resolved event — that is LONG <-> SHORT (always Flipped) or directional -> NEUTRAL through a persisted NEUTRAL-bias event (absorption, FlowConflict, FamilyConflict).

A Neutral -> directional change caused by an ordinary New expansion/covering/liquidation is **not** a Telegram-eligible bias change. A Resolved row is never Telegram-eligible by itself.

## 60.2 Not sent to Telegram in V1

- ordinary New BuyerExpansion / SellerExpansion / ShortCovering / LongLiquidation;
- Strengthening and Weakening;
- absorption alone (unless it caused an eligible bias change under 60.1.3);
- Resolved rows;
- critical data-quality events (persisted and shown on the Dashboard only);
- everything else.

Expanding the Telegram set is deferred until the notification-noise replay report (section 73) has been reviewed.

# 61. Telegram deduplication and cooldown

Never send Telegram directly from the detector.

Use a durable notification outbox following the same reliability principles already used by Adaptive screenshot delivery. Create a conceptually separate table:

    adaptive_commentary_notification_jobs

Recommended fields:

    Id
    EventId
    Status
    CreatedAtUtc
    NextAttemptUtc
    SentAtUtc
    TelegramMessageId
    Attempts
    LastError

Status semantics mirror the existing safe outbox pattern:

    Pending
    Sending
    Sent
    DeliveryUncertain

Telegram acknowledgement and database commit are not atomic; do not claim exactly-once delivery.

Deduplication:

- One event can create at most one automatic Telegram job.
- Sent jobs never automatically resend.
- DeliveryUncertain is not blindly retried.
- EventId must be unique in the notification outbox.

Cooldown:

    MinimumBarsBetweenSameEventTelegram = 5   (completed bars)

It applies to repeated notifications for the same EventType + Bias. The following bypass the cooldown because they are materially new: BiasChanged, Flipped, Confirmed, newly HIGH EvidenceAgreement caused by a new independent confirmation family, and a critical data-quality event.

In V1 every Telegram-eligible class (Confirmed, Flipped, actual bias change) is in the bypass list, so the cooldown currently has no practical effect. It is implemented as configuration so that enabling additional eligible classes later cannot flood Telegram. It is an operational notification throttle, not a trading parameter.

# 62. Commentary examples

## 62.1 Seller expansion begins

Possible frame:

    Strict / Enriched       SELL
    Roll Strict / Enriched  SELL
    Roll OI                 positive
    Bar / Roll price        negative
    OFI                     negative
    MicroDev                negative
    DeltaBasis              negative
    PE Position             PutLongBuild
    Residual                negative

Persist:

    EventType     SellerExpansion
    Bias          SHORT
    Regime        BEARISH
    Lifecycle     New
    EvidenceAgreement    HIGH

Rendered commentary:

    Seller expansion started.
    Bias SHORT | Regime BEARISH | EvidenceAgreement HIGH.
    Seller-aggressive flow is persistent, OI is expanding and price is accepting the selling.
    OFI, basis, PE PutLongBuild and the residual confirm downside pressure.

## 62.2 Same move continues

Next bar still satisfies SellerExpansion but nothing materially changes.

Result:

    no new event row
    no Telegram
    runtime/checkpoint advances

This silence is intentional.

## 62.3 Seller expansion strengthens

Later:
- RollingStrictAbsDeltaChange > 0 (existing Evolution logic);
- EvidenceAgreement moves MEDIUM -> HIGH because Options becomes supportive.

Persist:

    EventType     SellerExpansion
    Bias          SHORT
    Lifecycle     Strengthening
    EvidenceAgreement    HIGH

Not Telegram-eligible in V1 (Strengthening is Dashboard-only).

## 62.4 Seller absorption appears

Current selling stays aligned, OI remains non-negative, but BarPriceDisplacement becomes >= 0.

Persist:

    EventType     SellerAbsorption
    Bias          NEUTRAL
    Regime        BEARISH
    Lifecycle     New

Commentary:

    Seller absorption appeared.
    Bias NEUTRAL | Regime BEARISH.
    Aggressive selling persists, but price is no longer accepting it lower.
    Book pressure is improving against sellers.

Do not call LONG yet.

## 62.5 Seller rejection becomes confirmed

After SellerAbsorption:
- price turns positive;
- selling dominance weakens;
- at least two independent confirmation families turn LONG-compatible.

Persist:

    EventType     SellerRejectionConfirmed
    Bias          LONG
    Regime        TRANSITION
    Lifecycle     Confirmed

This is Telegram-eligible in V1 because its lifecycle is Confirmed.

## 62.6 Buyer expansion follows

Later:

    EventType     BuyerExpansion
    Bias          LONG
    Regime        BULLISH
    Lifecycle     New or Flipped depending on prior active directional event

The commentary timeline now describes the complete transition rather than treating each bar independently.

# 63. Commentary history and research use

PostgreSQL commentary events should later support research without changing V1 live decisions.

Do not modify historical event classification after seeing future prices.

Outcomes may be attached only **after** their horizons become available.

Recommended later outcome fields/table:

    EventId
    FutureMoveH1
    FutureMoveH3
    FutureMoveH5
    MfePoints
    MaePoints
    EventDurationBars
    NextMajorEventType
    OutcomeCompletedAtUtc

Prefer a separate table such as:

    adaptive_commentary_event_outcomes

so the immutable event-at-detection record remains unchanged.

These outcomes are research diagnostics only.

They can later answer questions such as:

- What happened after HIGH-EvidenceAgreement SellerExpansion?
- How often did SellerAbsorption lead to SellerRejectionConfirmed?
- What happened after Short -> Neutral bias transitions?
- Did three-family/four-family confirmation outperform a futures-only event?
- How long did expansion regimes normally persist?

Future outcome analysis must not retroactively rewrite the original EventType, Bias, EvidenceAgreement or evidence.

# 64. Optional file export

PostgreSQL is the authoritative source.

Files are export only.

A later end-of-day exporter may produce CSV/JSON/text such as:

    09:51  SHORT    SellerExpansion       New
    10:07  SHORT    SellerExpansion       Strengthening
    10:24  NEUTRAL  SellerAbsorption      New
    10:31  LONG     SellerRejection       Confirmed
    10:38  LONG     BuyerExpansion        New

Deleting an export file must never lose commentary history.

Restart recovery must never depend on an export file.

# 65. Commentary implementation boundaries

The initial implementation must **not**:

- create orders;
- modify paper/live entry or exit logic;
- feed commentary back into Futures/Options/Residual calculations;
- optimize event definitions against historical P&L;
- create a -100..+100 composite score;
- use an LLM in the live decision path;
- send Telegram after every completed bar;
- infer Long/Short from PCR alone;
- infer a reversal immediately from absorption;
- rewrite past events after future outcomes are known.

The commentary engine is a deterministic observation/read-model layer.

# 66. Commentary testing and acceptance requirements

Implementation is not complete until automated/replay tests prove:

1. Same CommentaryFrame always produces the same event classification.
2. Future data cannot affect a historical event.
3. BuyerExpansion/SellerExpansion/ShortCovering/LongLiquidation classify correctly, and bars matching no explicit event classify as NoMaterialEvent (no row, no Telegram, checkpoint advances).
4. Primary events do not fire when a mandatory rolling input is unavailable or ReadinessMet is false.
5. Absorption initially produces Neutral bias.
6. Rejection confirmation requires the previous checkpoint's active absorption plus the mandatory price/dominance condition and the required independent families.
7. Option support cannot be created from PCR alone.
8. CallShortCover and PutShortCover are not sufficient fresh directional confirmation by themselves.
9. Evidence-family counting does not double-count MicroDev+OFI as two families.
10. Unavailable evidence is neither support nor contradiction and carries no extra penalty.
11. EvidenceAgreement bands follow section 53 exactly.
12. FamilyConflict follows section 54.1 exactly; one unavailable family is never a conflict.
13. Regime transitions follow section 54 exactly; there is no time-based decay.
14. Lifecycle rules (New/Strengthening/Weakening/Confirmed/Resolved/Flipped, continuation, replacement, precedence) follow section 55 exactly; no `Active` lifecycle exists.
15. BiasChanged is persisted correctly and the Telegram bias-change rule (60.1.3) is applied exactly.
16. Adjacent residual delta is used; the legacy bridged delta is never used.
17. Same bar replay cannot duplicate an event (identity: SessionId + BarSeq + EventType + Lifecycle + Bias).
18. Bars with no persisted event still advance the commentary runtime checkpoint.
19. Service restart does not turn an existing event into a false New event.
20. Telegram eligibility is limited to the V1 set; same-event cooldown works; bypasses work.
21. Sent Telegram jobs are not automatically resent after restart; DeliveryUncertain follows the existing conservative pattern.
22. Dashboard text is rendered only from the persisted structured event; the renderer never mentions evidence absent from the event data.
23. Historical replay and live processing produce identical commentary events for identical persisted inputs.
24. Event outcome enrichment cannot mutate the original event record.
25. No UI, database or domain identifier uses the word "EvidenceAgreement" for commentary.

# 67. Commentary implementation order

Commentary is built in the controlled slices of section 72.

### Phase C1 — domain contract (Slice 4A)
- CommentaryFrame, enums (EventType, EventBias, MarketRegime, Lifecycle, EvidenceAgreement, Severity).
- pure deterministic classifier incl. NoMaterialEvent, FamilyConflict, rejection precedence.
- pure deterministic lifecycle/regime transition logic and renderer.
- extensive unit tests with hand-built frames. No database in this slice.

### Phase C2 + C3 — persistence and projection integration (Slice 4B)
- adaptive_commentary_events / adaptive_commentary_runtime migration and models, idempotent event identity.
- repository/service for atomic event + checkpoint advancement.
- CommentaryFrame built only after Futures/Options/Residual metrics for a completed bar exist; strict BarSeq order; no wall-clock or later-live-state leakage.
- replay/restart tests.

### Phase C4 + C5 — Dashboard and Telegram (Slice 4C)
- Live Market Commentary panel (latest five meaningful events, Event/Bias/Regime/Lifecycle/EvidenceAgreement, deterministic explanation).
- adaptive_commentary_notification_jobs outbox, V1 conservative eligibility, 5-bar cooldown with bypasses, safe retry / DeliveryUncertain.

### Phase C6 — replay validation (after 4C)
- run existing sessions through the same completed-bar projection; restart mid-session; compare event identities, order, lifecycle, bias and rendered text; verify no duplicate notifications are queued; produce the notification-noise report (section 73).

### Phase C7 — research outcomes (later)
- event outcome table/export; H1/H3/H5, MFE/MAE, durations. Observational only; must not modify V1 live classification.

# 68. Commentary decisions locked for implementation

As of 08 October 2026:

- PostgreSQL is the authoritative commentary store; file output is export-only.
- Commentary is event-driven, not one message per bar, and evaluates completed futures adaptive bars only.
- Every persisted event carries LONG / SHORT / NEUTRAL Event Bias; Event Bias describes a directional implication under the plan's hypotheses, not an order instruction.
- Market Regime is separate from Event Bias, persists until an explicit transition (no time-based expiry).
- Unmatched combinations are `NoMaterialEvent` (not persisted, no Telegram, checkpoint advances).
- Primary events require ReadinessMet and their mandatory rolling inputs.
- Absorption starts NEUTRAL; reversal direction requires a later confirmation event.
- Futures core behaviour creates the primary event; Book, Basis, Options and Residual are independent confirmation families.
- PCR/IV/skew are context; PCR alone never defines direction.
- `EvidenceAgreement` LOW/MEDIUM/HIGH counts supporting families; unavailable evidence is neutral; no composite numeric score. It is not confidence.
- Do not double-count correlated metrics from the same evidence family.
- Persist structured evidence and contradictions, not only prose; persist lifecycle events, not ordinary continuation bars.
- A per-session runtime checkpoint keeps silent bars restart-safe.
- New commentary residual logic uses adjacent-bar deltas only.
- Dashboard shows recent commentary; Telegram is limited to Confirmed, Flipped and actual bias changes in V1, through a durable outbox with conservative DeliveryUncertain semantics.
- Live event detection/rendering is deterministic and does not use an LLM.
- Historical outcomes may be attached later but cannot rewrite the original event.
- Commentary does not modify any trading/execution behaviour.

# 69. Commentary-specific items that remain configurable, not research-tuned

The following are implementation configuration rather than market-edge parameters:

    MinimumBarsBetweenSameEventTelegram = 5
    DashboardCommentaryRows = 5
    CommentaryResidualVariant = ATM±2

Defaults above are part of the V1 operational design.

They must not be optimized against P&L during implementation.

Any later change should be explicit and versioned so historical commentary can be reproduced.

# 70. Plan self-sufficiency rule

This document is the implementation contract for the 08-Oct Dashboard/observer enhancement.

A new engineer must be able to understand from this file alone:

- what appears in each compact grid;
- how every new visible metric is calculated;
- what remains internal/diagnostic;
- timestamp/causality/freshness requirements;
- how Telegram screenshots should be composed;
- how the Running Market Commentary classifies events;
- how LONG/SHORT/NEUTRAL bias differs from Market Regime;
- how events transition through lifecycle;
- how commentary is persisted/recovered;
- when Telegram is and is not notified;
- which behaviors are explicitly forbidden.

If implementation encounters an ambiguity that would alter a metric definition, event direction, persistence semantics, or notification behavior, **stop and update/finalize this plan before inventing behavior in code**.

No undocumented fallback or silently different calculation is acceptable.


# 71. Sidecar persistence and deployment contract

**Status:** DESIGN LOCKED.

## 71.1 Why sidecars

`AdaptiveObserverPersistence.VerifyRow` reflects over every scalar property (except `Id`) of the persisted adaptive rows on restart and throws on any difference. New columns on `AdaptiveFutureBarRow`, `AdaptiveRollingStateRow`, `AdaptiveOptionBandBarRow` or `AdaptiveOptionResidualBarRow` would be null for rows persisted earlier in a session while replay would compute values, so the first restart after a deployment would fail parity. Those rows are the protected deterministic ledger: their semantics are unchanged, `VerifyRow` is not weakened, no blanket "ignore new properties" exemption is added, and historical rows are never mutated.

## 71.2 Sidecar tables and recovery semantics

Conceptual tables (final column lists are recorded in the migrations and in each slice report):

- `adaptive_session_supplemental` — one row per session (section 71.3).
- `adaptive_futures_supplemental_bars` — Slice 2A: TOB start / time-weighted / end / change / min / max, MicroDev start / time-weighted / end / change, raw OFI, OFI transition count, valid-book coverage duration, invalid/crossed-book count and duration. Slice 2B adds basis start / time-weighted / end, ΔBasis, spot ages and a basis status.
- `adaptive_options_supplemental_bars` — Slice 3: center OI start/end/Δ and midpoints per side, Position per side, IV start/end/Δ per side, IV skew start/end/ΔSkew, Vol PCR and its rolling components, center CE/PE MicroDev and OFI, ATM±2 CE/PE Activity/s, center Straddle Δ, and the Subscribed-Universe Day Vol PCR fields (cumulative CE volume, cumulative PE volume, token/strike universe count, observation start, coverage status).
- Commentary tables (Slice 4B) are separate (sections 56–57, 61).
- No residual sidecar (section 32.4); no Urgency column (section 5.2).

Every sidecar row carries `SessionId`, `BarSeq` and `MetricsVersion` with a unique key on that triple. `MetricsVersion` identifies the calculation contract; a row produced by a different `MetricsVersion` is never silently overwritten (a new version is inserted alongside, and readers use the current version).

On recovery / replay, for every completed bar:

- if the sidecar row exists → recompute the expected supplemental metrics and **verify** the stored row against them;
- if it does not exist → **insert** the deterministic missing row.

A mid-session deployment therefore backfills earlier bars from persisted raw data. Sidecar calculation is a deterministic function of the same causal inputs the core replay uses. A sidecar verification failure is logged and flagged on the sidecar status, never overwrites a stored row, and does not block or alter core adaptive recovery (the sidecar is observational; the core ledger remains the restart authority).

## 71.3 Supplemental session identity (spot token freezing)

`adaptive_session_supplemental` (key `SessionId`) holds at least `MetricsVersion`, `SpotToken`, `SpotSymbol`, `CreatedAtUtc` and resolution provenance. The correct NIFTY spot/index instrument is resolved once for that trade date/session and frozen; recovery always reuses the frozen identity and never re-resolves a different token. If a unique valid spot instrument cannot be resolved, Basis stays unavailable. For a session already running at deployment, the identity is created once and is immutable from then on. The existing frozen `AdaptiveSessionStateRow` is not modified.

## 71.4 Migration and deployment ordering (contract; nothing is being deployed)

1. The Host (which runs `Database.Migrate()` at startup) is deployed **first**, so the new schema exists.
2. The Dashboard is deployed only after the schema exists. Dashboard reads of sidecar tables treat a missing relation as "unavailable" rather than failing the whole panel.
3. Deploy between sessions; a mid-session deployment is safe by design (backfill, section 71.2) but still involves a Host restart and a full replay.
4. Both services are deployed from the same reviewed master SHA with `deploy.ps1 -ExpectedCommitSha`, per ADAPTIVE_VM_HANDOFF.md.

# 72. Implementation slices, branches and per-slice verification

Work is split into independently shippable, stacked branches (names may vary; each must have a clean commit history). Nothing is merged to master and nothing is deployed without separate authorization.

    adaptive-08oct-plan          this plan (plan-only commit)
      adaptive-08oct-s1          Slice 1 — safe Dashboard compaction + pinned Live Quote
        adaptive-08oct-s2a       Slice 2A — futures microstructure sidecar (TOB, MicroDev, OFI)
          adaptive-08oct-s2b-basis   Slice 2B — spot plumbing / audit (STOP before the freshness threshold)
          adaptive-08oct-s3      Slice 3 — options sidecar + shared pure residual projection (not blocked by 2B)
            adaptive-08oct-s4a   Slice 4A — commentary pure domain
              adaptive-08oct-s4b   Slice 4B — commentary persistence + projection integration
                adaptive-08oct-s4c Slice 4C — commentary Dashboard + Telegram outbox

Slice contents:

- **Slice 1:** compact Futures/Options/Residual grids from existing persisted fields only; `OI Δ` renamed `Roll OI Δ`; Dashboard-derived Urgency (`DurationSeconds <= 0` ⇒ unavailable); Diagnostic view toggle; pinned/as-of `LiveQuotePanel` capture support (separate sub-step, section 18); Telegram screenshot tests; restart parity unchanged.
- **Slice 2A:** futures sidecar with versioned replay/backfill verification (sections 6–8, 71).
- **Slice 2B:** frozen supplemental spot identity, causal spot plumbing, proof that no existing adaptive output changes, spot-age distribution report; **stop for threshold approval**.
- **Slice 3:** options sidecar, Subscribed-Universe PCR diagnostic, shared pure residual projection, availability report.
- **Slices 4A–4C:** section 67.

**Stop conditions.** Stop and ask only if (1) current code proves a decision here cannot be implemented safely as specified; (2) a new market threshold/constant not already approved is needed; (3) the ΔBasis spot-freshness decision is reached; (4) an implementation would alter existing adaptive bar or restart semantics; (5) an implementation would touch trading/scoring/execution/risk. When stopping, give the exact code location, the conflict, alternatives and a recommendation.

**Per-slice report** must state: branch, commit SHA, files changed, migrations/schema added, exact behaviour added, tests added, full build/test result, restart/replay parity result, old/new row parity where applicable, screenshot validation where applicable, availability statistics where applicable, deferred items, and assumptions verified against code.

# 73. Availability and noise reports

- **Options availability (Slice 3):** for the available historical/live replay sessions report CE Position, PE Position, CE ΔIV, PE ΔIV and IV Skew availability %, broken down by DTE where practical. The 5-second rule is not loosened.
- **Spot freshness (Slice 2B):** p50 / p90 / p95 / p99 / max spot age during market hours and a recommended rule; owner approval required (section 9.6).
- **Commentary notification noise (after Slice 4C):** replay existing sessions and report events/day, lifecycle counts/day, BiasChanged/day and Telegram-eligible events/day. This validates notification volume only; it is not a profitability test, and event directions/signs are never changed because of how outcomes turned out.

