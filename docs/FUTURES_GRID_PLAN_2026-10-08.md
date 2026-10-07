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


# 18. Telegram screenshot — include existing NIFTY Live Quote panel

Current Telegram adaptive screenshots do not include the existing Dashboard **NIFTY Live Quote** panel. This must be corrected when implementation begins.

## 18.1 Requirement

Normal Telegram screenshots must include, above the adaptive observer grids, the same existing live-quote panel rendered by the Dashboard.

Do **not** build a second quote calculation path for Telegram. The screenshot must capture the existing Dashboard component/state so browser and Telegram views cannot diverge.

The captured quote panel should therefore include whatever the existing live panel shows at capture time, including:
- NIFTY spot live quote/change;
- NIFTY futures live quote/change;
- VIX live quote/change when available;
- selected CE strike and its LTP / bid / ask / change;
- selected PE strike and its LTP / bid / ask / change;
- the existing stale-session warning/status when applicable.

The exact fields above follow the existing `LiveQuotePanel`; this planning item does not redefine its pricing/subscription logic.

## 18.2 Selected CE/PE must be preserved

The screenshot must use the currently selected CE and PE strikes from the Dashboard state. It must not silently recenter the quote box to a different strike only for Telegram capture.

If capture runs in an isolated browser/session where interactive selection cannot be inherited, that behavior must be made explicit and deterministic before implementation is finalized rather than silently choosing a different contract.

## 18.3 Capture layout

The ordinary Telegram capture should contain, in order:

1. **NIFTY Live Quote panel**;
2. adaptive session/header information required for context;
3. current incomplete adaptive-bar progress if still part of the normal Dashboard view;
4. compact Futures grid;
5. compact Options grid once finalized;
6. residual diagnostic section only if it remains part of the normal screenshot specification after this planning phase.

The capture must not omit the Live Quote panel because of viewport clipping or because the screenshot target starts at the adaptive-grid DOM element.

## 18.4 Reliability requirements

Implementation validation must prove that:
- the live quote panel is visibly present in the generated PNG;
- quote values shown in the screenshot come from the same Dashboard state as the browser panel;
- CE and PE strike labels are visible;
- bid and ask values are visible;
- a missing/stale quote is rendered as unavailable/stale rather than replaced by an older unrelated quote;
- the screenshot remains readable at the Telegram image dimensions;
- adding the quote panel does not truncate the compact Futures/Options grids.

This requirement is part of the eventual Dashboard/Telegram implementation but **no screenshot code is authorized to change until the overall 08-Oct plan is finalized**.


# 19. Options Grid Live-Observation Plan

**Status:** DRAFT / DESIGN ONLY — agreed direction, no implementation until the complete 08-Oct plan is explicitly finalized.

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
10. How does the current bar compare with the full-session volume PCR context?

No individual options column is a trade signal by itself.

# 20. Proposed visible compact Options grid

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

Above the grid, show session context:

    Day Vol PCR: x.xx

The Day Vol PCR label must explicitly indicate that it is **Full nearest-expiry chain**, while row-level Vol PCR and Roll Vol PCR are **ATM±2 band** metrics.

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
- start or end midpoint is unavailable/stale;
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

# 25. Volume PCR metrics

PCR in this plan is a **participation ratio**, not a bullish/bearish signal by itself.

The compact grid exposes:

    Vol PCR | Roll Vol PCR

and the options header exposes:

    Day Vol PCR

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

## 25.3 Day Vol PCR — full nearest-expiry chain

Display above the options grid:

    Day Vol PCR — Full nearest-expiry NIFTY chain

Formula at time t:

    DayVolPCR(t) =
      cumulative PE traded quantity from 09:15 IST through t
      /
      cumulative CE traded quantity from 09:15 IST through t

Universe:
- NIFTY options only;
- nearest weekly expiry used by the live adaptive observer;
- full available strike chain for that expiry;
- both CE and PE.

This is intentionally a different universe from row-level ATM±2 PCR.

The label must make that distinction visible.

## 25.4 Full-chain coverage requirement

Day Vol PCR must not silently become a partial-chain PCR.

Before implementation is considered correct, prove that cumulative volume is available for the full intended nearest-expiry option universe.

If the live collection/subscription architecture does not provide complete full-chain traded-volume coverage, Day Vol PCR must show unavailable/degraded rather than silently using only subscribed ATM strikes.

Persist/retain a coverage diagnostic so this can be audited.

## 25.5 Day header context

If screen width permits, the header may show:

    Day Vol PCR x.xx | PE Vol x | CE Vol x

The ratio remains the primary label. Underlying cumulative PE/CE volume is useful context and should be retained even if not displayed in V1.

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
13. Day Vol PCR uses the full nearest-expiry NIFTY chain or explicitly reports unavailable/degraded coverage.
14. Band-roll semantics are explicit; rolling metrics never pretend the basket stayed physically unchanged.
15. Restart/replay reproduces the same metrics from persisted causal data.
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
- Add full-chain nearest-expiry Day Vol PCR above the grid.
- Do not interpret PCR alone as bullish/bearish.
- Calculate center CE/PE MicroDev and OFI internally, not V1 visible.
- Calculate option Activity/s and center Straddle Δ internally, not V1 visible.
- Keep existing notional/ratio/coverage/OI diagnostic calculations; hide rather than remove.
- No composite score and no trading rule are introduced.

# 30. Remaining open items before implementation

The complete 08-Oct plan is close, but implementation should not start until these details are explicitly frozen:

1. **Futures basis spot freshness:** exact stale-age rule for Basis/ΔBasis.
2. **Option quote freshness for new IV/Position/Skew metrics:** reuse the current 5-second band-selection freshness everywhere, or define another explicit causal threshold. Prefer one consistent rule unless evidence justifies otherwise.
3. **Day PCR data coverage:** confirm the live collector has full nearest-expiry-chain cumulative volume coverage; otherwise define how the UI reports degraded/unavailable.
4. **Persistence vs deterministic recomputation:** choose where the new futures and options microstructure metrics live so restart/replay and Telegram screenshot parity are guaranteed.
5. **Compact vs diagnostic Dashboard UX:** fixed compact grid only versus an expandable detailed view.
6. **Telegram screenshot final composition:** live quotes + session header + incomplete bar + compact futures + compact options; decide whether residual diagnostics remain in the normal Telegram image or move to a secondary/diagnostic capture.
7. **Screenshot selection state:** define deterministic CE/PE selected-strike behavior for isolated Telegram browser capture so it cannot silently differ from the intended live selection.

No production implementation is authorized until these open items are resolved or explicitly deferred with deterministic behavior documented.


# 31. Residual Grid Live-Observation Plan — LOCKED

**Status:** LOCKED FOR IMPLEMENTATION DESIGN as of 08 October 2026.

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

# 32. Proposed compact Residual grid

Visible order:

    Seq | End IST | Future Δ09:30
    | CE Res % | CE Res Δ%
    | PE Res % | PE Res Δ%
    | Directional Res % | Directional Res Δ
    | Straddle Res %
    | Direction | Relation
    | Quote Age

This becomes the primary compact live residual grid.

The purpose of the compact row is:

    underlying displacement
    → CE dislocation and its change
    → PE dislocation and its change
    → relative directional dislocation
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

Add:

    CEResidualDeltaPct_t =
      CEResidualPct_t - CEResidualPct_(t-1)

    PEResidualDeltaPct_t =
      PEResidualPct_t - PEResidualPct_(t-1)

Use the previous available residual reading for the **same residual variant** (ATM compared with prior ATM; ATM±2 compared with prior ATM±2).

If the previous residual reading for that variant was unavailable, the delta is unavailable rather than bridging across the gap.

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

# 35. Directional Res % and Directional Res Δ

## 35.1 Directional Res %

Keep the existing definition:

    DirectionalResidualPct =
      CEResidualPct - PEResidualPct

Do not replace it in V1 because existing research/history already uses this exact value.

## 35.2 Directional Res Δ

Keep the existing Residual Δ calculation but rename/display it clearly as:

    Directional Res Δ

Formula:

    DirectionalResidualDelta_t =
      DirectionalResidualPct_t
      - DirectionalResidualPct_(t-1)

Again, do not bridge across an unavailable previous residual reading.

Interpretation:
- positive → CE-vs-PE relative dislocation is becoming more positive;
- negative → it is becoming more negative;
- near zero → little change in the directional residual.

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
5. CE/PE residual Δ does not bridge across unavailable prior readings.
6. Directional Res Δ does not bridge across unavailable prior readings.
7. Straddle Res % uses the common 09:30 CE+PE denominator.
8. The existing Directional Res % formula remains unchanged.
9. The new straddle-normalized directional residual is additive/diagnostic only and cannot silently replace existing research fields.
10. 0-DTE time decay continues to use the existing time-to-expiry convention.
11. Restart/replay reproduces identical residual rows and deltas.
12. Compact-grid hiding does not delete existing actual/expected/residual-point diagnostics.
13. No residual metric is used to alter scoring, entries, exits or execution.
14. Telegram/browser rendering shows the same residual values for the same persisted completed bar.

# 43. Residual-grid decisions frozen

As of **08 October 2026**, the Residual grid design is locked as:

    Seq | End IST | Future Δ09:30
    | CE Res % | CE Res Δ%
    | PE Res % | PE Res Δ%
    | Directional Res % | Directional Res Δ
    | Straddle Res %
    | Direction | Relation
    | Quote Age

Also frozen:

- Keep fixed 09:30 anchor semantics.
- Keep ATM and ATM±2 selector.
- Keep current Directional Residual % definition for continuity.
- Add CE and PE residual change independently.
- Add Straddle Residual % using a common 09:30 CE+PE premium denominator.
- Calculate straddle-normalized directional residual internally only.
- Keep Quote Age visible.
- Keep Direction and Relation.
- Hide rather than remove actual/expected/intermediate diagnostic columns.
- Do not duplicate Options-grid IV/OI/PCR/MicroDev/OFI in this grid.
- No composite score or trading rule is introduced.

The **Residual-grid metric design itself is finalized**. The broader implementation remains blocked only by the cross-cutting open items already listed in section 30, unless those are explicitly resolved/deferred before coding.


# 44. Running Market Commentary — IMPLEMENTATION PLAN

**Status:** APPROVED FOR THE 08 OCTOBER IMPLEMENTATION PLAN.

**Purpose:** Add a deterministic, event-driven commentary layer on top of the finalized Futures, Options and Residual observations. The commentary exists to reduce the amount of manual interpretation required while watching live. It must explain meaningful market-state changes without generating a message after every adaptive bar.

The commentary engine is observational. It does not place orders, modify scoring, or change any existing trading/execution path.

The core output of every commentary event is:

    Event
    + Event Bias
    + Market Regime
    + Lifecycle
    + Confidence
    + Primary Evidence
    + Confirmations
    + Contradictions

Event Bias is explicitly:

    LONG | SHORT | NEUTRAL

This means the direction the event currently supports for NIFTY. It is **not** an automatic instruction to place a trade.

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
- data-quality availability flags required by those metrics

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
- DirectionalResidualDelta
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
- Transition: established regime is being challenged by a material opposing event.
- Conflict: independent evidence families materially disagree.
- Neutral: no established directional regime.

The Market Regime can differ from Event Bias.

Example:

    Event        SellerAbsorption
    Event Bias   Neutral
    Regime       Bearish

This means the broader market remains bearish, but the newest event no longer supports adding directional confidence to the short side.

## 47.3 Bias transition

Persist:

    PreviousBias
    CurrentBias
    BiasChanged

Important transitions include:

    Short -> Neutral
    Neutral -> Long
    Long -> Neutral
    Neutral -> Short
    Long -> Short
    Short -> Long

A bias transition is more important than another bar retaining the same bias and should receive notification priority.

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

This should normally reduce confidence in any same-bar directional interpretation.

If none of the above primary events exists, the engine can remain silent for that bar unless a previously active event changes lifecycle materially.

# 49. Absorption-to-reversal confirmation

Absorption is deliberately two-stage.

## 49.1 SellerRejectionConfirmed

This can occur only if the immediately active relevant lifecycle previously contained SellerAbsorption.

Mandatory evidence:

    BarPriceDisplacement > 0

and one of:

    Evolution == Weakening
    OR Evolution == FlipToBuyer
    OR RollingStrictAbsDeltaChange < 0

Then require confirmation from at least **two independent evidence families** below:

### Book family
At least one:

    OFI > 0
    OR MicroDev > 0

The book family counts once even if both agree.

### Inter-market family

    DeltaBasis > 0

### Options family

Strong LONG-supporting option evidence as defined in section 50.

### Residual family

    DirectionalResidualPct > 0

If mandatory conditions and at least two independent confirming families are present:

    EventType    SellerRejectionConfirmed
    EventBias    LONG
    Regime       Transition initially
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

    OFI < 0
    OR MicroDev < 0

### Inter-market

    DeltaBasis < 0

### Options

Strong SHORT-supporting option evidence.

### Residual

    DirectionalResidualPct < 0

Result:

    EventType    BuyerRejectionConfirmed
    EventBias    SHORT
    Regime       Transition initially
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

    DirectionalResidualDelta > 0
        => residual is becoming more LONG-oriented

    DirectionalResidualDelta < 0
        => residual is becoming more SHORT-oriented

Leg-specific residual deltas should be used in commentary text to explain which side is driving the change.

StraddleResidualPct remains common-richness/common-cheapness context and does not create Long/Short direction by itself.

# 52. Independent evidence families

Never calculate confidence by counting every raw column because many metrics are correlated.

For commentary, evidence is grouped into independent families:

1. **Futures core** — aggressive flow + OI + price response.
2. **Book** — MicroDev / OFI.
3. **Inter-market** — DeltaBasis.
4. **Options** — Position + option aggressive flow; IV/PCR/skew as context.
5. **Residual** — directional theoretical dislocation.

Within a family, multiple agreeing metrics improve the explanation but the family still counts only once when calculating confidence.

# 53. Confidence — V1 deterministic bands

V1 uses discrete confidence rather than a false-precision numeric score.

Values:

    LOW
    MEDIUM
    HIGH

For a directional primary event:

### HIGH

- Futures core event exists;
- at least three of the four independent external families (Book, Inter-market, Options, Residual) support the same Event Bias;
- no independent family gives a clear opposite-direction contradiction.

### MEDIUM

- Futures core event exists;
- at least one external family supports the same bias;
- no more than one external family clearly contradicts it.

### LOW

- Futures core event exists but confirmation is weak;
- or two or more independent families materially disagree;
- or important inputs required for confirmation are unavailable.

For Neutral events such as absorption/conflict, Confidence means confidence that the **event condition exists**, not confidence in Long/Short direction.

Data unavailability must not be counted as agreement or contradiction.

# 54. Market Regime state machine

V1 should maintain one current Market Regime per session.

Initial state:

    Neutral

Transitions:

### Neutral -> Bullish
When a LONG BuyerExpansion or ShortCovering event is created with MEDIUM/HIGH confidence.

### Neutral -> Bearish
When a SHORT SellerExpansion or LongLiquidation event is created with MEDIUM/HIGH confidence.

### Bullish -> Transition
When BuyerAbsorption, BuyerRejectionConfirmed, or a material SHORT event challenges the existing bullish regime.

### Bearish -> Transition
When SellerAbsorption, SellerRejectionConfirmed, or a material LONG event challenges the existing bearish regime.

### Transition -> Bullish
When BuyerExpansion occurs after a LONG-confirmed transition.

### Transition -> Bearish
When SellerExpansion occurs after a SHORT-confirmed transition.

### Any directional regime -> Conflict
When material independent-family disagreement persists and no directional event can be confirmed.

### Conflict -> Bullish/Bearish
When aligned directional evidence returns with MEDIUM/HIGH confidence.

### Any -> Neutral
Only at session initialization/reset or when no active directional lifecycle remains under the finalized regime-expiry policy.

V1 should avoid aggressively resetting to Neutral between ordinary bars. Regime is intentionally more persistent than an individual event.

# 55. Event lifecycle

Lifecycle values:

    New
    Strengthening
    Weakening
    Confirmed
    Resolved
    Flipped

Do not persist an event row for every ordinary Active bar.

## 55.1 New

A materially different EventType appears compared with the current active event, or the same event reappears after being resolved.

## 55.2 Strengthening

Same directional event remains active and at least one meaningful change occurs, such as:

- Evolution changes toward Strengthening;
- RollingStrictAbsDeltaChange increases in the event direction;
- a previously absent independent confirmation family becomes supportive;
- Confidence improves LOW -> MEDIUM or MEDIUM -> HIGH.

Do not generate Strengthening repeatedly only because raw magnitudes drift.

## 55.3 Weakening

Same event remains relevant but at least one material deterioration occurs:

- Evolution becomes Weakening;
- RollingStrict dominance magnitude contracts;
- expected price acceptance disappears;
- a supporting independent family becomes Neutral/opposing;
- Confidence falls.

## 55.4 Confirmed

Used when a previously tentative transition becomes explicitly confirmed, especially SellerRejectionConfirmed / BuyerRejectionConfirmed.

## 55.5 Resolved

The event condition no longer exists and no immediate opposite-direction event replaced it.

## 55.6 Flipped

A directly opposing directional event replaces the previous directional event.

Examples:

    SellerExpansion -> BuyerExpansion
    LongLiquidation -> BuyerExpansion

A Flipped lifecycle is always notification-worthy.

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
    Confidence                 enum/string not null
    Severity                   enum/string not null

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

DataQualityJson must record important unavailable/stale/degraded inputs used when determining Confidence.

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
    Confidence
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

# 59. Deterministic commentary rendering

V1 must **not** use an LLM to decide:
- EventType;
- Bias;
- MarketRegime;
- Lifecycle;
- Confidence;
- whether Telegram should be notified.

Use deterministic templates.

A renderer should receive the structured event and produce concise text.

Recommended structure:

    [Event title]
    Bias / Regime / Lifecycle / Confidence
    Primary evidence sentence.
    Confirmation sentence if present.
    Contradiction sentence if present.

Example:

    Seller expansion strengthening.
    Bias SHORT | Regime BEARISH | Confidence HIGH.
    Seller-aggressive flow remains dominant with rising OI and accepted lower prices.
    OFI and basis weakened; PE PutLongBuild and bearish residual confirm.

The renderer must never invent evidence that is not present in the event JSON.

If a family is unavailable, either omit it or explicitly say it is unavailable when data quality itself matters.

An LLM can be considered later for non-critical end-of-day summaries, but it is out of V1 live event detection and notification.

# 60. Telegram notification policy

Dashboard commentary can show all persisted meaningful events.

Telegram must be stricter.

Severity values:

    Info
    Medium
    High

## 60.1 High severity — Telegram eligible

Examples:

- Event Bias changes Long <-> Short.
- Event Bias changes directional -> Neutral because a significant absorption/conflict event appeared.
- SellerRejectionConfirmed / BuyerRejectionConfirmed.
- New BuyerExpansion / SellerExpansion with HIGH confidence.
- A directional event Flipped.
- Confidence becomes HIGH because a new independent family creates broad cross-market agreement.
- material data-quality failure invalidates previously reliable commentary during the live session.

## 60.2 Medium severity

Normally Dashboard-only unless later configured otherwise.

Examples:

- New absorption.
- directional event Strengthening or Weakening at MEDIUM confidence.
- material Basis divergence.
- Residual dislocation materially opposes an established regime.
- Options support appears/disappears without a bias change.

## 60.3 Info

Persisted/displayed only.

Examples:

- ordinary continuation;
- minor context change;
- one isolated confirming metric with no lifecycle effect.

# 61. Telegram deduplication and cooldown

Never send Telegram directly from the detector.

Use a durable notification outbox following the same reliability principles already used by Adaptive screenshot delivery.

Create a conceptually separate table:

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

Status semantics should mirror the existing safe outbox pattern:

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

Default V1 policy:

    MinimumBarsBetweenSameEventTelegram = 5

This applies to repeated notifications for the same EventType + Bias.

The following bypass cooldown because they are materially new:

- BiasChanged;
- Flipped;
- Confirmed rejection/reversal;
- transition to HIGH confidence caused by new independent-family agreement;
- critical data-quality warning.

This is an operational notification throttle, not a trading parameter.

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
    Confidence    HIGH

Rendered commentary:

    Seller expansion started.
    Bias SHORT | Regime BEARISH | Confidence HIGH.
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
- rolling Strict magnitude increases materially through existing Evolution logic;
- Confidence moves MEDIUM -> HIGH because Options becomes supportive.

Persist:

    EventType     SellerExpansion
    Bias          SHORT
    Lifecycle     Strengthening
    Confidence    HIGH

Potential Telegram only if notification policy and cooldown allow it.

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

This is Telegram-eligible even during cooldown because BiasChanged.

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

- What happened after HIGH-confidence SellerExpansion?
- How often did SellerAbsorption lead to SellerRejectionConfirmed?
- What happened after Short -> Neutral bias transitions?
- Did three-family/four-family confirmation outperform a futures-only event?
- How long did expansion regimes normally persist?

Future outcome analysis must not retroactively rewrite the original EventType, Bias, Confidence or evidence.

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
3. BuyerExpansion/SellerExpansion/covering/liquidation rules classify correctly.
4. Absorption initially produces Neutral bias.
5. Rejection confirmation requires the prior absorption lifecycle plus the required independent confirmations.
6. Option support cannot be created from PCR alone.
7. CallShortCover and PutShortCover are not treated as sufficient fresh directional confirmation by themselves.
8. Evidence-family counting does not double-count MicroDev+OFI as two families.
9. Missing data is not treated as contradiction or confirmation.
10. Confidence bands follow section 53 exactly.
11. BiasChanged is persisted correctly.
12. Same bar replay cannot duplicate an event.
13. Bars with no material event still advance the commentary runtime checkpoint.
14. Service restart does not turn an existing event into a false New event.
15. Same-event Telegram cooldown works.
16. Flipped/BiasChanged/Confirmed events can bypass cooldown.
17. Sent Telegram jobs are not automatically resent after restart.
18. DeliveryUncertain follows the existing conservative notification pattern.
19. Dashboard text is rendered only from the persisted structured event.
20. Rendered commentary never mentions evidence absent from structured event data.
21. Historical replay and live processing produce identical commentary events for identical persisted inputs.
22. Event outcome enrichment cannot mutate the original event record.

# 67. Commentary implementation order

A new engineer should implement commentary in this order:

### Phase C1 — domain contract
- CommentaryFrame.
- enums for EventType, EventBias, MarketRegime, Lifecycle, Confidence, Severity.
- pure deterministic classifier.
- pure deterministic lifecycle/regime transition logic.
- pure deterministic renderer.
- unit tests with hand-built frames.

### Phase C2 — persistence
- adaptive_commentary_events migration/model.
- adaptive_commentary_runtime migration/model.
- idempotent event identity/indexes.
- repository/service for atomic event + checkpoint advancement.
- replay/restart tests.

### Phase C3 — projection integration
- create CommentaryFrame only after Futures/Options/Residual metrics for a completed bar are available.
- process bars in strict BarSeq order.
- prove no wall-clock/live-later state leaks into evaluation.

### Phase C4 — Dashboard
- Live Market Commentary panel.
- latest five meaningful events.
- explicit Bias, Regime, Lifecycle, Confidence.
- deterministic prose from persisted event.

### Phase C5 — Telegram
- adaptive_commentary_notification_jobs outbox.
- severity policy.
- 5-bar same-event cooldown.
- BiasChanged/Flipped/Confirmed bypasses.
- safe retry / DeliveryUncertain behavior.
- Telegram message formatting.

### Phase C6 — replay validation
- run a historical session through the same completed-bar projection.
- restart mid-session.
- compare event identities, order, lifecycle, bias and rendered text.
- verify no duplicate notifications are queued.

### Phase C7 — research outcomes later
- event outcome table/export.
- H1/H3/H5, MFE/MAE and event-duration analysis.
- this phase remains observational and must not modify V1 live classification.

# 68. Commentary decisions locked for implementation

As of 08 October 2026:

- PostgreSQL is the authoritative commentary store.
- File output is export-only.
- Commentary is event-driven, not one message per bar.
- Commentary evaluates completed futures adaptive bars only.
- Every meaningful event carries LONG / SHORT / NEUTRAL Event Bias.
- Event Bias describes directional implication, not an order instruction.
- Market Regime is separate from Event Bias.
- Bias transitions are first-class events.
- Absorption starts NEUTRAL.
- Reversal direction requires a later confirmation event.
- Futures core behaviour creates the primary event.
- Book, Basis, Options and Residual are independent confirmation families.
- Option PCR/IV/skew are contextual; PCR alone never defines direction.
- Confidence uses LOW/MEDIUM/HIGH family agreement, not a composite numeric score.
- Do not double-count correlated metrics from the same evidence family.
- Persist structured evidence and contradictions, not only prose.
- Persist meaningful lifecycle transitions, not ordinary continuation bars.
- Keep an operational per-session checkpoint so silent bars are restart-safe.
- Dashboard shows recent commentary.
- Telegram is reserved for material events under explicit severity/cooldown rules.
- Telegram delivery uses a durable PostgreSQL outbox and conservative DeliveryUncertain semantics.
- Live event detection/rendering is deterministic and does not use an LLM.
- Historical outcomes may be attached later but cannot rewrite the original event.
- Commentary does not modify any trading/execution behaviour in V1.

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
