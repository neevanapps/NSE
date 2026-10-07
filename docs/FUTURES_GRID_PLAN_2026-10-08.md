# Futures Grid Live-Observation Plan — 08 October 2026

**Status:** DRAFT / DESIGN ONLY — do not implement until this document is explicitly finalized.

**Scope:** Adaptive Market Observer → **Futures — exact adaptive bars** grid only.

**Intent:** Make the futures grid easier to read live by showing only fields that answer distinct market-behaviour questions. Existing calculations and hidden diagnostic fields must remain available; this plan hides columns from the primary live grid rather than deleting functionality.

**Non-goals:** No trading rule, composite score, entry/exit automation, threshold optimization, or change to existing Strict / Enriched / rolling-state semantics. Options and residual grids are out of scope for now.

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

## 2. Proposed visible futures-grid columns

| # | Display column | Source/type | Purpose |
|---:|---|---|---|
| 1 | **Seq** | Existing | Completed adaptive-bar sequence |
| 2 | **End IST** | Existing | Bar completion time |
| 3 | **Dur s** | Existing | Elapsed time required to complete the exact-volume bar |
| 4 | **Urgency** | New | Rate at which the bar's traded volume arrived |
| 5 | **Bar ΔPx** | Existing | Immediate price response in this adaptive bar |
| 6 | **Roll ΔPx** | Existing | Price response across the existing rolling window |
| 7 | **Strict Δ** | Existing | High-confidence aggressive buy minus sell volume in the current bar |
| 8 | **Enriched Δ** | Existing | Broader aggressive buy minus sell volume in the current bar |
| 9 | **Roll Strict** | Existing | Strict aggressive-volume delta across the rolling window |
| 10 | **Roll Enriched** | Existing | Enriched aggressive-volume delta across the rolling window |
| 11 | **|Strict| Δ** | Existing | Change in magnitude of rolling Strict dominance |
| 12 | **MicroDev** | New | Time-weighted microprice deviation from midpoint inside the bar |
| 13 | **OFI** | New | Raw top-of-book order-flow imbalance accumulated inside the bar |
| 14 | **Roll OI Δ** | Existing, rename label | Net futures OI change across the rolling window |
| 15 | **ΔBasis** | New | Change in futures-minus-spot basis during the bar |
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

## 9.6 Spot freshness — OPEN ITEM BEFORE IMPLEMENTATION

Basis quality depends on spot freshness.

Record at minimum:

SpotAge_t = BasisStateTime_t - LastSpotAvailableAt_t.

A fixed live freshness/suppression rule has **not yet been frozen** in this planning discussion. Do not invent a threshold during implementation.

Before implementation begins, this document must be updated with one explicit rule for when a stale spot state makes Basis/ΔBasis unavailable.

Until that is finalized, basis design is approved conceptually but implementation is blocked on this detail.

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

# 11. Proposed live interpretation examples

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
- raw volume/sec as defined above.

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

# 15. Proposed compact futures-grid layout

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
- Show **Urgency**.
- Calculate TOB on every unique valid book state, time-weighted per bar, but do not display it initially.
- Show **MicroDev** instead of TOB to avoid redundant live columns.
- Show raw **OFI** from Level-1 book changes.
- Calculate spot–futures basis causally through the bar; show **ΔBasis**, retain absolute/time-weighted basis internally.
- Show **Roll Efficiency**.
- Do not add RSI/MACD/Bollinger/ADX or similar technical-indicator columns.
- Do not add a composite score.
- Do not begin implementation until this plan is explicitly finalized.

# 17. Open items before implementation

Keep adding decisions to this file. At minimum the following must be frozen before coding:

1. **Spot freshness rule for basis:** exact age at which Basis/ΔBasis becomes unavailable.
2. Whether MicroDev display precision should be price points only or also expose ticks in hover/details. V1 direction is **raw price points**.
3. Whether new internal metrics should be persisted in adaptive-session tables or deterministically recomputed by the Dashboard read model. Decide with restart/replay parity and screenshot reliability in mind.
4. Exact Dashboard hidden-column UX: fixed compact layout only, or compact/default plus an expandable diagnostic view.
5. Whether Telegram screenshots use exactly the compact futures columns or support a separate wider diagnostic capture. Current preference is **compact columns for normal screenshots**.

No implementation is authorized by this document yet.
