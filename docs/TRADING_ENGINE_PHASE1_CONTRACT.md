# Trading Engine — Phase 1 Implementation Contract

Baseline: `origin/master` = `1ef645be330d1d864ba50b6eb2dc614c744f9674` (fetched 2026-10-09; local `master` was identical, tree clean).
Branch: `trading-engine-phase1-contract` (worktree `D:\Projects\NSE-phase1`). Roadmap: [`TRADING_ENGINE_ROADMAP.md`](TRADING_ENGINE_ROADMAP.md).

**Amendment 1 (2026-10-09) incorporates Naveen's confirmed requirements** (session times, concurrent variants, waiting entries, sizing/TP1, research risk policy, execution adapters, research labelling, cost verification). §A is authoritative and supersedes any conflicting older text; revised decisions are in §14. Implementation status: Phase 2 checkpoints CP0, CP1, CP2 and CP3 are implemented (§A.11, §A.12, §A.13); everything else is still contract only. No database created, no migration, no deploy, no merge.

This document is a contract and an audit. **Nothing here is implemented.** Every constant marked `PLACEHOLDER` or `PAPER-DEFAULT (unapproved)` is a starting guess, not a decision (project rule: no invented thresholds).

## Amendment 1 — confirmed requirements (2026-10-09). **Authoritative; supersedes any conflicting text elsewhere in this document.**

Contract update only. No implementation, no database created, no migration, no deploy, no merge. Section numbers below (§A.x) are new; older sections (§1–§16) were patched where they conflicted, and each patched place says "Amendment 1".

### A.0 Requirement → contract mapping

| Req. | Confirmed requirement (short) | Where specified |
|---|---|---|
| R1 | Asia/Kolkata; entries 09:30:00 ≤ t < 15:00:00; warm-up earlier; at 15:00 invalidate waiting opportunities + cancel unfilled entry qty; all positions closed by 15:10 target; earlier liquidation-start setting with retry/escalation; never claim closed without a fill; stops/targets/deadlines independent of bars | §A.1, §6.3, §9.2, T22–T26 |
| R2 | Concurrent strategies/variants; each owns opportunities, orders, positions, accounting, config; 1 position per variant in V1 incl. pending reservations; ids support many positions later; uniqueness scoped to run/account/variant (no DB-wide constraint); independent comparative research = separate virtual accounting | §A.3, §8.1, §A.7, T27–T29 |
| R3 | Base-strategy validity separate from extra entry condition (A immediate; A2 ask < MA of selected option's completed candles, N configurable, recalculated); same contract throughout; evaluated on eligible quote updates; invalidation cancels; re-arm/re-entry explicit; never compare to ATM basket; limit-vs-recheck flagged | §A.4, T30–T35, D-12 |
| R4 | Default requested quantity 650; validate against date-specific lot size, report invalid, never alter silently; fixed-quantity and premium-budget modes; budget valuation/rounding/insufficiency; TP1 % + proportion with lot rounding, avg-entry reference, remainder SL/final exit, partial fills, restart-safe idempotency; no guessed ₹50k limit | §A.5, §A.6, T36–T41 |
| R5 | V1 research risk policy: no max-trades, no daily-loss lockout or liquidation; configurable daily SL per variant recorded only; per-trade SL separate; optional enforcement + separate real-order profile later; integrity/duplicate/session rules still apply; no invented monetary limits | §A.7, §10, T42–T44 |
| R6 | Shared strategy/risk across replay/live paper/future broker; adapter by config; real orders disabled; replay advances clock through every deadline before a later event; deterministic tie order; quote freshness at execution time; no LTP/entry-price substitution | §A.8, §A.2, §4.4, §9.1, T45–T48 |
| R7 | All 18 sessions = development data; post-10-01 not automatically out-of-sample; measure per-contract quote coverage first; costs verified from official sources with dates; execution placeholders + sensitivity; historical quotes don't measure broker latency | §A.9, §A.10, §11, §14 |

### A.1 Trading session and deadlines (Asia/Kolkata; R1)

All session times are wall times in `Asia/Kolkata` (UTC+05:30, no DST), converted once at the adapter boundary to the UTC `AvailableAt` axis; Core compares instants only. Times are `SessionConfig` values (versioned, in the config hash).

| Setting | Value | Status |
|---|---|---|
| `WarmupStart` | engine consumes events from the adaptive observer's collection start (09:15 today; may be earlier) | existing behaviour; decisions are rejected `Warmup` until ready |
| `EntryOpen` | **09:30:00.000 inclusive** | confirmed |
| `EntryClose` | **15:00:00.000 exclusive** (a decision with `DecisionTime == 15:00:00.000` is rejected `EntryClosed`) | confirmed |
| `TargetFlatBy` | **15:10:00.000** (goal: every position closed by then) | confirmed (goal, not a guarantee) |
| `LiquidationStart` | **15:05:00.000** | PLACEHOLDER (proposal): earlier than `TargetFlatBy` to leave retry room |
| `ExitRetryMs`, `ExitEscalation*` | see below | PLACEHOLDER |
| `SessionEnd` | the adaptive hard close / feed stop (15:35 today) | existing |
The legacy expiry-day 14:00 entry cutoff is **dropped**; one cutoff applies on all days unless a later amendment adds a per-DTE setting.

Behaviour at `EntryClose` (a deadline, class `SessionBoundary`, §A.2):
1. Every `Opportunity` in `Waiting` or `Triggered` becomes `Expired(EntryClose)`; no new opportunity can be created or triggered.
2. Every entry order that is `Submitted`/`Working` is cancelled for its **unfilled quantity** (`CancelRequested → Cancelled`); quantity already filled remains a position and is managed normally. Because simulated cancellation is evaluated at `ExecutionTime`, a fill that was already matured at an `ExecutionTime < 15:00:00` is honoured; one that would mature at `>= 15:00:00` is not.
3. Reservations are released to the filled amount. Exits, stops and targets are unaffected by `EntryClose`.

Liquidation (`LiquidationStart` → `TargetFlatBy`): at `LiquidationStart` every non-flat position (including `PartiallyOpen`, `ExitPending`) receives a forced exit decision `SessionLiquidation` for its remaining quantity unless an exit order for it is already working (then that order is re-priced per escalation). Exit attempt `k` (k = 0,1,…) is a marketable limit at the executable bid minus `collar_k`, where `collar_k = min(k × ExitCollarStepTicks, MaxExitCollarTicks) × tickSize`, repeated every `ExitRetryMs` (all PLACEHOLDER; tick size from the instrument snapshot, 0.05 on all 18 sessions). An attempt with no executable bid (missing/stale/one-sided quote) is **not** executed and **not** substituted; it is recorded `NoExecutableBid` and retried. Escalation: at `TargetFlatBy` with the position not flat → `Fault(LiquidationOverdue)`, alert, retries continue at the same cadence until flat or `SessionEnd`. At `SessionEnd` any non-flat position is reported `UnresolvedAtClose` (last executable bid and its age, remaining quantity, attempts) in the session summary and the audit; it is **not** closed in the books, **not** given a realised P&L, and is carried as an explicit open item in the report. A position is `Flat` only after fills sum to its quantity.

Stops, targets and deadlines are independent of completed bars (R1): stop-loss, TP1, final target and the trailing/remainder rule are evaluated on every eligible quote event of the held token and on timers (§A.2, §A.6); bars are never needed for exits. If no new quote arrives, there is no new price information and no trigger; the position becomes `Unmarkable` when its last quote ages beyond `MaxQuoteAgeMs`, an alert is raised, and the session deadlines still run.

### A.2 Deterministic timers in replay (R6; replaces the "flush" wording)

1. **Replay advances through every deadline before a later event.** For each next input event with `AvailableAt = T`, the engine repeatedly pops the earliest queued deadline `D` with `D <= T` and executes it (with `DecisionTime = D`) **before** the event is handled. The loop continues because a deadline handler may enqueue a new deadline (order maturity, expiry, retry) that is itself `<= T`. A deadline equal to `T` runs before the event at `T`. This makes the exclusive `EntryClose` correct (a quote at exactly 15:00:00 cannot create an entry) and removes the earlier "equal-to-T after the event" rule.
2. **No dependence on end-of-stream flush.** The end-of-stream `AdvanceTo(SessionEnd)` call is still made so trailing deadlines fire, but a gap of any length between two events is processed by (1); a deadline in the middle of a quiet gap is never deferred to the next market event or to the final flush. (Test T26/T47 fails if a deadline is observed with `DecisionTime` later than its deadline, or if outputs differ between a stream with a synthetic heartbeat in the gap and one without.)
3. **Deterministic tie order** for deadlines with equal time: `(DeadlineTime, ClassRank, VariantOrdinal, OrderOrOpportunityId, CreationSeq)` with `ClassRank`: 0 = execution maturities (order reaches `ExecutionTime`, expiry, cancel evaluation), 1 = session boundaries (`EntryClose`, `LiquidationStart`, `TargetFlatBy`, `SessionEnd`), 2 = risk/exit retries, 3 = strategy/opportunity timers. `VariantOrdinal` is the variant's position in the sorted `VariantId` list of the config. `CreationSeq` is the engine's monotonically increasing counter (deterministic because event handling is).
4. **Live parity.** The live adapter's heartbeats (§4.4) only cause (1) to run earlier in wall time; they cannot change `DecisionTime`, ordering or outputs.
5. **Executions are evaluated at execution time.** An order's `ExecutionTime` is a class-0 deadline. At that deadline the model reads the latest quote with `AvailableAt <= ExecutionTime` (never a later one), checks freshness (`ExecutionTime − AvailableAt <= MaxQuoteAgeMs`), two-sidedness, spread and size. A quote that arrives after `ExecutionTime` cannot be used even if it is processed before the deadline in wall time. No LTP, last mid, trigger price or entry price may be substituted for a missing bid/ask.

### A.3 Strategies, variants, accounts and positions (R2)

- **Entities.** `Strategy` (a signal family, version) → `Variant` (`VariantId` = stable string, e.g. `A`, `A2-ma10`; owns `EntryConditionSpec`, `SizingSpec`, `ExitSpec`, `RiskProfileRef`, `AccountRef`, its own config hash). The engine hosts **N variants over the same ordered event stream**; each event is offered to variants in `VariantOrdinal` order. A variant's `Opportunity`, `Order`, `Position`, `RiskCounters` and ledger are never read or written by another variant.
- **Accounting.** Each variant has its own `VirtualAccount` (default `AccountId == VariantId`): cash/exposure/P&L ledger, daily counters, daily SL record. Independent comparative research always uses separate virtual accounts, so one variant's fills never reduce another's capacity or quote availability. (Consequence stated plainly: with independent accounts every variant can fill against the *same displayed size*, so summing variants overstates what one real account could get; sums across variants are therefore never reported as achievable P&L.)
- **Shared-account policy** (capital/exposure constraints across variants, a single broker account) is a *separate execution policy layer* (`AccountPolicy`) selected by config; it is out of Phase 2 beyond the interface and a pass-through "Independent" implementation.
- **V1 capacity.** `MaxPositionsPerVariant = 1`. Capacity counts positions in any non-flat state **and** reservations for pending entry orders (and a `Triggered` opportunity whose order is in flight), so a second entry cannot be created while an order is pending.
- **Identity for the future.** `PositionId = H(ConfigHash, VariantId, TradeDate, EntryDecisionId)`; each position carries `VariantId` and `SlotNo` (always 0 in V1). Uniqueness constraints (§8.1) are scoped `(RunId, AccountId, VariantId, SlotNo)`; raising capacity later means allowing `SlotNo` 0..N−1, not changing keys. **There is no database-wide single-position constraint.**
- **Per-variant config.** Each variant's full spec is part of the config hash; two variants with different parameters have different `VariantConfigHash` and may not share an `OpportunityId`/`OrderId` namespace (ids include `VariantId`).

### A.4 Entry variants, waiting entries and quote-triggered conditions (R3)

**Separation.** `BaseStrategy` answers one question — *is the setup valid, and for which contract?* — by emitting `StrategyValidity` changes on completed-bar (and optionally timer) events. `EntryCondition` answers *given a valid opportunity, may we place the entry now?* It is a pluggable pure function with a declared input set. Variants are `(BaseStrategy, EntryCondition, Sizing, Exit, Risk)` tuples: A = `(S, Immediate)`, A2 = `(S, AskBelowOptionCandleMA(N))`; both can run concurrently.

**Opportunity lifecycle** (one `OpportunityId = H(VariantConfigHash, TradeDate, EpisodeId)`, where an *episode* starts when the base strategy goes from not-valid to valid):

`Created → Waiting → Triggered → OrderPending → {Filled | Waiting (re-armed) | Expired | Cancelled}`

1. **Creation.** At the validity-making `DecisionTime`, if `EntryOpen <= DecisionTime < EntryClose` and warm-up complete, the **contract is selected once** (§6.1) and stored on the opportunity. The same token is used for the condition, the order, the position and every exit. It is never re-selected during the opportunity, the trade, or a re-arm.
2. **Immediate condition (A).** Evaluated once at creation. If capacity is busy → `Rejected(CapacityBusy)` and the opportunity ends (`OnCapacityBlocked = Reject`, per-variant, default for Immediate). Otherwise an entry order is created.
3. **Waiting condition (A2).** The opportunity enters `Waiting`. The condition is evaluated on **every eligible quote update of the selected token** using the latest causally available state: the quote's own executable ask and the moving average as of that event. *Eligible* means all of: opportunity `Waiting`; base strategy still valid; `EntryOpen <= t < EntryClose`; feed healthy; the quote is fresh, two-sided and passes spread/size checks; capacity free (if capacity is busy the opportunity keeps waiting, `OnCapacityBlocked = Wait`, default for waiting conditions). No evaluation happens on quotes of other tokens.
4. **A2 definition.** `AskBelowOptionCandleMA(N, priceField, maType)`: condition true iff `executableAsk(token, t) < MA_N(token, t)` (strict), where `MA_N` is the arithmetic mean (`maType = Simple`; others are separate named conditions) of the `priceField` of the **last N completed candles of the selected option token only**. `N` is configuration (10 is only an example; no default is approved). The MA is recalculated whenever a candle completes; a candle is available to the condition only for events ordered **after** the candle-completion output in the total order (§4.3), i.e. a quote in the same availability group as the candle's closing tick does not see that candle. If fewer than N valid completed candles exist for the token, the condition is `NotEvaluable(InsufficientCandles)` (waiting continues, audited), never filled with LTP, zeros or other tokens' data. A rolling-ATM-basket average, a futures average, or any series not built from the selected token's own quotes is **rejected by construction**: the condition declares its input token, and the engine asserts `inputToken == opportunity.Token` (test T31). A2 always compares the **executable ask** with the average of the **configured candle price series of the selected option token**; the comparison side never implies which candle price is averaged.
5. **Candle definition (unresolved, D-13).** Proposed: option candles are built per token from fresh two-sided quotes on the **same completed-bar boundaries** as the adaptive futures bars (so the candle clock is the strategy's own bar clock), `priceField` is an **explicit, configurable** setting with no implied default (candidates: `AskClose` — last fresh ask in the bar, a **proposed variant** on the same side as the comparison; `MidClose`; `BidClose`; `TypicalPrice`; open/high/low forms), each a separately named and separately logged variant — ask-close is one interpretation of "average price", not the only one; tick-time ordering by `(AvailableAt, Id)`. A bar with no fresh two-sided quote for the token yields an invalid candle (counts toward neither N nor the MA; the MA window then spans earlier candles—flagged by `CandleGapCount` in the audit). Sensitivity variants (mid close; time-based candles) are separate named conditions, not parameters.
6. **Invalidation.** When the base strategy becomes not-valid (a bar or timer says so) the opportunity is `Cancelled(StrategyInvalidated)`; an entry order created from it and not yet filled is cancelled for its unfilled quantity; a filled part remains a position governed by its exit rules. `EntryClose`, feed `Degraded`, or contract unquotability beyond `HaltStaleSeconds` also end/suspend it (`Expired(EntryClose)`, suspended-not-cancelled for data validity, cancelled for `Degraded`).
7. **What a satisfied condition creates (unresolved, D-12).** Observing a qualifying ask does not guarantee a later fill at that price. Options: (a) condition creates a **marketable limit order at the observed qualifying ask** (plus `EntryLimitTolerance` ticks, default 0); it fills only if the executable ask at `ExecutionTime` is `<= limit` (fill at the actual ask — price improvement allowed); (b) the condition is **re-checked at `ExecutionTime`** and the order fills at the then-current ask if it still holds (no price bound); (c) fill at whatever the ask is at `ExecutionTime` (rejected: not a condition). **Recommendation: (a), adopted as the recommended initial policy; no overlapping retry orders (at most one entry order outstanding per opportunity — the previous order must be terminal before any re-arm or new order)**, no MA re-check, because it bounds slippage to the price that satisfied the rule, is how a real limit order would behave, and makes "trigger → fill" a measurable miss rate. Under (a), if the order expires unfilled while the opportunity is still valid and in session, the opportunity is **re-armed** to `Waiting` (below). Phase 2 implements both (a) and (b) behind `EntryOrderPolicy` so the sensitivity can be measured; the default is whichever Naveen approves.
8. **Re-arming and re-entry (explicit).**
   - *Unfilled expiry, no fill at all:* re-arm to `Waiting` **only after the prior order is terminal** (no overlapping retry orders) and only if still valid/in session/capacity free, counting `AttemptNo`; `MaxAttemptsPerOpportunity` is configurable, **default unlimited** (attempts are not trades; there is no trade-count cap in V1). Minimum spacing between attempts is the order expiry itself because only one order may be outstanding.
   - *Partial fill then expiry:* remainder cancelled; opportunity is **consumed** (no re-arm to top-up) — the position is managed on what filled.
   - *Filled:* opportunity consumed. One entry per opportunity.
   - *After the trade exits:* no re-entry on the same opportunity/episode (`ReentryPolicy = NewEpisodeRequired`, recommended default). A new opportunity requires the base strategy to go not-valid → valid again. Alternative `SameEpisodeAfterExit` exists as a named policy, not default, and would create a new `OpportunityId` with `EpisodeId` suffix `r<k>` and a fresh contract selection (never reusing the old contract silently).
   - *Valid → invalid → valid within the same bar processing:* treated as two episodes in event order.
9. **Audit.** Every evaluation outcome that changes state (and every N-th `NotEvaluable` per opportunity, bounded) writes `decision_audit` with ask, MA, N, candle count, quote age, spread, capacity, the rule that blocked it; per-quote "ask ≥ MA, still waiting" evaluations are summarised (counters + first/last timestamps + min ask-minus-MA), not one row per quote, to bound volume (~1.1 M ticks/session, existing measurement).

### A.5 Sizing and quantity validation (R4)

- **Modes (per variant):** `FixedQuantity { Units }` — **default `Units = 650`** (= 10 lots of 65); `PremiumBudget { Rupees, ValuationPrice, MaxLots? }`. One mode per variant; the mode and its parameters are in the variant config hash.
- **Lot size validation.** Lot size is read per trading date from the instrument snapshot for the selected token (`instruments.LotSize` as of the session's `AsOfDate`). *Verified:* all NIFTY option instruments on all 18 `AsOfDate` snapshots have `LotSize = 65` and `TickSize = 0.05`, so 650 = 10 whole lots on every development session. A run-level **pre-run validator** checks every session/variant before trading and reports a table `(date, variant, requested units, lot size, valid?)`; an invalid combination (`units % lotSize != 0`, `units <= 0`, missing lot size) marks that variant-day `InvalidQuantity`, the entry decision is `Rejected(InvalidQuantity)` with the numbers, and the run report lists it. **Quantities are never silently rounded or altered** in `FixedQuantity` mode.
- **Premium budget.** Valuation: `lotCost = lotSize × ValuationPrice + estimatedEntryCostsPerLot`, where `ValuationPrice` is the **order limit price** at order creation (the observed qualifying ask for waiting entries; the executable ask at decision time for immediate entries) — not the later fill price — and entry costs come from the `CostSchedule` (§A.9). `lots = floor(Rupees / lotCost)`, capped by `MaxLots` if set. **Round down only.** If `lots < 1` → `Rejected(InsufficientBudget)` with `required = lotCost`, `budget = Rupees`; the entry is not attempted at a smaller size by rounding up and not silently skipped (the rejection is audited). Because a fill can only be at or below the limit, the budget is not exceeded by price (costs excepted, being estimated).
- **Capital.** No approved capital exists. The earlier ₹50,000 paper-capital / ₹16,250 exposure proposals are **withdrawn**. `AccountCapital` is nullable; `null` (the research default) means *not enforced*. Exposure is **recorded** (peak premium committed per variant/day, never used to reject) so research is not silently restricted. Enforcement is available only when a capital value is configured, and then only in a profile that enables it (§A.7).
- Illustration (my arithmetic, not a decision): 650 units × a ₹100–150 premium = ₹65,000–₹97,500 of premium per entry, i.e. larger than the withdrawn ₹50k guess; that is why capital must not be a hidden default.

### A.6 Exits: per-trade SL, TP1 partial exit, remainder, final exit (R4/R5)

All prices below are **premium (option) prices**; percentage triggers compare the **executable bid** (what liquidation would receive) with the **average entry fill price** (volume-weighted over actual entry fills, price only, excluding costs; `EntryReference = AvgEntryFillPrice`). Triggers are evaluated on every eligible quote event of the held token and on timers (independent of bars). Costs enter P&L, not trigger prices (configurable `TriggerBasis = Price | NetOfCosts` is a named option, default `Price`).

- **Per-trade SL** (`TradeSLPct`, nullable): trigger when `bid <= AvgEntry × (1 − TradeSLPct)`. It is an **exit rule**, separate from the daily SL (§A.7), applies to all open quantity (and to the remainder after TP1 per `RemainderSL`), and creates a marketable-limit exit (§A.1 escalation applies). The fill is whatever is executable at `ExecutionTime`, so it can be worse than the trigger (gap risk) — recorded as `ExitSlippageVsTrigger`.
- **TP1** (`Tp1Pct`, `Tp1Proportion` p with 0 < p < 1; both nullable = disabled): armed on the first entry fill. Trigger when `bid >= AvgEntry × (1 + Tp1Pct)`.
  - `Tp1TargetQty = floorToLot(p × OpenQtyAtTrigger)` computed **once** at the first trigger and stored. If it rounds to 0 lots → TP1 state `Skipped(RoundsToZeroLots)`, audited, and the position continues under SL/final/session rules (no silent alteration to 1 lot). Because p < 1 and fills are whole lots, `Tp1TargetQty < OpenQty` always, leaving a remainder.
  - Lot rounding: all order and fill quantities are whole multiples of lot size (partial fills are rounded **down** to whole lots; less than one lot of displayed size ⇒ no fill).
  - TP1 order = marketable limit at the bid (collar 0 for TP1, `Tp1CollarTicks` configurable). It may fill partially; the order rests until `OrderExpiryMs`. If it expires with `TargetQty − Filled > 0` the state returns to `Armed(remaining)` and may re-trigger when the condition holds again for **only the remaining TP1 quantity** — total TP1 quantity never exceeds `Tp1TargetQty`.
  - If the entry order is still working when TP1 triggers, the unfilled entry quantity is cancelled first (no accumulation after an exit began).
  - **Restart-safe idempotency.** `Tp1State ∈ {NotArmed, Armed, TriggeredOrderWorking, PartiallyFilled, Done, Skipped}` is part of position state and is *derived deterministically by replay*; the TP1 order id is `OrderId = H(PositionId, "TP1", AttemptNo)` and `Tp1TargetQty` is stored on the first trigger event. Replay re-derives the same ids; inserts are `ON CONFLICT DO NOTHING` + verify; a recovery that finds a persisted TP1 order does not create a second one (T39–T40).
- **Remainder after TP1:** `RemainderSL ∈ {Unchanged, MoveToBreakeven (AvgEntry), FixedPct x}` — default `Unchanged` (research-neutral); `FinalTPPct` (nullable) is the final target; the remainder is also subject to session liquidation (§A.1). The average-entry reference does **not** change after TP1 (it is the entry average, not a re-averaged cost basis).
- **Exit priority when several fire on one event** (deterministic): `SessionLiquidation > TradeSL > FinalTP > TP1`. An exit order in flight blocks creating a second exit order for the same quantity (duplicate-order prevention).
- No discretionary exit parameter has an approved value; every `*Pct`/`*Ticks`/`*Ms` is a research parameter or PLACEHOLDER, recorded with its variant.

### A.7 Risk policy: `ResearchV1` and a separate real-order profile (R5)

Two named profiles. The profile is part of every variant's config hash.

| Control | `ResearchV1` (the only profile in Phase 2) | `RealOrder` (future; separately configured; **disabled**) |
|---|---|---|
| Max trades per day | **none** (the earlier cap of 5 is withdrawn) | configurable, nullable |
| Concurrent positions | per-variant capacity (V1 = 1, incl. pending reservations) | per-variant and per-account |
| Daily SL | **configurable per variant; recorded only** — see below | configurable enforcement: `None | BlockEntries | BlockEntriesAndLiquidate` |
| Per-trade SL | exit rule (§A.6), independent of daily SL | same |
| Capital/exposure limit | not enforced; peak exposure recorded | configurable, requires approved values |
| Kill switch | manual operator flag and integrity faults only (no daily-loss auto-trip) | adds configurable auto-trip |
| Quantity/lot validation, duplicate-order prevention, session restrictions, data-integrity gates (stale/disconnect/degraded, late event), one outstanding order per quantity | **always on** | always on |
| Approved monetary limits | **none exist**; any value shown is PLACEHOLDER and unapproved | to be approved explicitly |

**Daily SL in `ResearchV1` (non-enforcing).** `DailySL` (monetary amount, nullable, per variant) is evaluated on every state change and quote event of the variant's open position as `DailyPnL_v = RealisedNet_v + UnrealisedLiquidationNet_v` (definitions in §10.4: net of all costs; unrealised at the executable bid less estimated exit costs; `Unmarkable` handled as in §10.4 *for the record only*). When `DailyPnL_v <= −DailySL` the engine writes a `DailySlBreached` event (first breach time, `DailyPnL` at breach, worst `DailyPnL` of the day, still-breached flag, subsequent entries/exits count). It **does not** block entries, cancel opportunities, liquidate, or trip the kill switch. The report prints breaches next to results and a counterfactual summary "trades/P&L after first breach" so enforcement can be evaluated later without having constrained the run. `EnforcementMode` exists in config with default `None`; enabling it is a `RealOrder`/later-version decision.

### A.8 Execution adapter selection (R6)

- Strategy, opportunity, sizing, exit, risk and order-management logic run in `Trading.Core` and are identical for replay, live paper and a future broker.
- `Execution.Adapter` (config, in the hash): `Simulated` (replay **and** live paper — same `SimulatedExecutionModel`, §9) or `Broker` (future). `RealOrdersEnabled` is a separate constant-false guard: selecting `Broker` while it is false, or running any profile other than an explicitly approved `RealOrder` profile, makes engine start-up **refuse**. Phase 2 contains no broker code beyond the `IExecutionGateway` interface and a "not enabled" stub that throws.
- The broker adapter would receive `OrderIntent`s with deterministic client order ids and return `ExecutionUpdateEvent`s on the same event stream; it is never allowed to touch strategy or risk state.

### A.9 Transaction costs — verification of current rates (R7; **PROVISIONAL**)

Authorised check performed 2026-10-09. **Honest status:** my attempts to open the NSE primary documents directly (`FA73061.pdf`, `FATAX73524.pdf`, the NSE STT and levies pages) **timed out three times**, so I could not read the primary text. The rates below come from search-engine extracts of those NSE pages plus broker/aggregator pages. They are therefore *provisional* until the primary PDFs are read (action item, D-7).

All 18 development sessions (2026-09-04 … 2026-10-01) fall **after** every effective date below, so the *current* schedule is applicable to all of them; no earlier rate applies to this dataset. Do not apply this schedule to sessions before the stated effective dates.

| Charge (index option, intraday round trip) | Rate | Base / payer | Effective | Applies to 18 sessions | Source (status) |
|---|---|---|---|---|---|
| STT on sale of an option | **0.15 %** (was 0.10 % up to 31 Mar 2026) | sell-side premium | 2026-04-01 (Finance Act 2026; assent 2026-03-30) | yes | NSE STT page and NSE circular `NSE/FATAX/73524` (31 Mar 2026), as returned by search extracts; secondary: taxguru, Arihant (**primary not read**) |
| NSE exchange transaction charge, index options | ≈ **₹3,553 per crore** of premium (≈ 0.03553 %) each side, i.e. ₹3,503 + ₹50 IPFT per crore before 2026-03-01; the 2026-02-27 circular rolls the IPFT contribution back and "no impact in overall outflow" | premium turnover, both sides | 2024-10-01 (₹35.03/lakh); revision effective 2026-03-01 | yes | NSE circulars `NSE/FA/73061` (27 Feb 2026) and the Oct-2024 transaction-charge circular via search extract — **the per-crore split was garbled in the extract; the total is provisional** |
| SEBI turnover fee | ₹10 per crore (0.0001 %) | turnover, both sides | long-standing | yes | broker/aggregator tables, NSE levies page (extract) — primary not read |
| GST | 18 % | on brokerage + exchange charges + SEBI fee (not on STT or stamp duty) | long-standing | yes | broker tables; BSE notice on SEBI-fee GST — primary not read |
| Stamp duty | **0.003 %** (₹300/crore) | buy-side premium only | 2020-07-01 | yes | NSE levies page (extract), Zerodha bulletin — gazette not read |
| Brokerage | **unknown** (existing config assumes ₹20/order; current broker plan not verified) | per order | — | — | **needs Naveen's actual plan (D-7)** |

Consequences: (i) the research calculator's **STT 0.0625 % is stale by a factor of 2.4** for these dates and must not be reused; (ii) the schedule must be a **versioned `CostSchedule` with effective dates**, selected per trade date, never hard-coded; (iii) each fill stores its cost components; (iv) results are reported **with and without** the provisional components so the dependence is visible.
Illustrative magnitude (my arithmetic, 650 units, buy at ₹125.00 and sell at ₹130.00, ₹20/order): STT ≈ ₹126.75, exchange ≈ ₹58.89, brokerage ₹40, GST ≈ ₹17.83, stamp ≈ ₹2.44, SEBI ≈ ₹0.17 → **≈ ₹246 round trip ≈ 0.38 index points per unit**, comparable to the whole 5-point gain in that example and larger than the two-tick (0.10) slippage per side modelled today. Costs therefore materially affect any short-hold premium strategy.

### A.10 Research data labelling and execution sensitivity (R7)

- **All 18 sessions (2026-09-04 … 2026-10-01) are labelled `Development`.** The earlier "discovery / validation-1" split of these dates is withdrawn: 09-28 … 10-01 are *not* a clean validation set (they were examined by diagnostics, §11.2), so no pass/fail claim may use them as out-of-sample.
- **Sessions after 10-01 are not automatically out-of-sample.** A `session_ledger` (append-only, one row per session per examination: date, purpose `diagnostic | discovery | rule-selection | validation | forward`, experiment id, date of first viewing, who/what) decides eligibility. A post-10-01 session is `ForwardEligible` only if the ledger shows it was neither examined for, nor influenced, rule selection before the rule freeze; the live watch-only sessions of 10-05 … 10-08 are `Unknown` until the ledger is reconstructed (they were observed live and referenced in recent performance work).
- **Per-contract quote coverage is measured before any feasibility claim.** Phase 3 deliverable, per session and contract: share of time with a fresh two-sided quote at the 5 s and `MaxQuoteAgeMs` thresholds, spread distribution (₹ and % of mid), touch sizes, quote-update intensity, and the same conditioned on the moments strategies would act (bar completions, ask-below-MA events). The existing "bid/ask present" percentages (89–95 %) are row-level, all-token, and **not** evidence of executable coverage.
- **Execution placeholders are explicit and tested for sensitivity.** `ExecutionLatencyMs`, `MaxQuoteAgeMs`, spread/size limits, `EntryOrderPolicy`, collars and fill fraction are PLACEHOLDERs. Every research result is reported at a **low / base / high** placeholder set (grid proposed in Phase 3, e.g. latency {250, 1000, 3000} ms, quote age {1, 2, 5} s) and a conclusion counts only if it survives the grid. Recorded quotes show *feed* timing (`ReceivedAt − ExchangeTimestamp`, 1-second exchange resolution) and book state, **not** broker order round-trip latency, acknowledgement delay, queue position or rejection behaviour; those can only be measured from real broker paper/live interaction later. Reports must say so.


### A.11 CP0/CP1 implementation status and the exact ordering contract (implemented 2026-10-09)

Implemented in `NiftySignal.Trading.Core` (references `NiftySignal.Domain` only, for `IstTime.Offset`) with tests in `NiftySignal.Tests/Trading/Core` (75 tests). **Nothing else of the contract is implemented**: no strategy, variants, opportunities, entry conditions, execution simulator, costs, sizing, exits, risk profiles, persistence, adapters, `Host` registration, database or migration. (Status at CP1; CP2 followed, see §A.12.)

**Classes and responsibilities**

| Class (namespace `NiftySignal.Trading.Core.*`) | Responsibility |
|---|---|
| `Time.EngineClock`, `ITradingClock` | Monotonic engine time, advanced only by the loop; UTC instants only (offset zero enforced) |
| `Events.EngineEvent`, `QuoteEvent`, `BarCompletedEvent`, `EventKind`, `EventIdentity` | Immutable input envelope: `AvailableAt` (UTC), kind, source id, token |
| `Events.EventOrdering` | The total order of events (below) |
| `Scheduling.ScheduledDeadline`, `DeadlineClass`, `DeadlineOrdering`, `DeadlineQueue` | Deadline record, tie-break order, priority queue with cancellation |
| `Scheduling.EngineLoop` (`IEngineScheduler`, `IEngineHandler`) | Single-threaded deterministic driver: group delivery, deadline progression, watermark, finish |
| `Scheduling.ReplayDriver` | Replays an ordered stream with a **required explicit horizon** |
| `Session.SessionConfig`, `SessionSchedule`, `SessionBoundaryKind`, `SessionPhase` | Session policy (hashed config) and its binding to a trade date: UTC instants, entry eligibility, phases, boundary deadlines |
| `Config.TradingConfig`, `CanonicalJson`, `ConfigHash`, `CanonicalIgnoreAttribute` | Versioned config and canonical serialisation/hash |
| `Ids.DeterministicId`, `EngineIds` | Stable ids for decisions, orders, opportunities, positions |

**Event order (exact).** Events are ordered by `(AvailableAt UTC ticks, EventKind rank, SourceId, Token ordinal)`. Kind rank: `SpotQuote 0 < OptionQuote 1 < FutureQuote 2 < BarCompleted 3`. Events with equal `AvailableAt` form one atomic availability group, delivered in that order regardless of argument/arrival order; a completed-bar event is therefore always after every quote of the group that completed it. Identity for re-delivery is `(Kind, SourceId)`; an exact repeat is ignored (and counted), a repeat with different content is `ConflictingDuplicateEventException`.

**Deadline order (exact).** `(Time, DeadlineClass rank, VariantOrdinal, Key ordinal, CreationSeq)`. Class rank: `ExecutionMaturity 0 < SessionBoundary 1 < RiskRetry 2 < StrategyTimer 3`. `VariantOrdinal` is `-1` for engine-wide deadlines (sorts first). `Key` comparison is ordinal; `CreationSeq` is the loop's own counter, so the order is strict and culture-independent.

**Interleaving (exact).**
1. Before delivering a group at time `T`, every queued deadline with `Time <= T` fires in deadline order, **including deadlines scheduled by earlier deadline callbacks** that are themselves `<= T`. Each callback runs with `Now == deadline.Time`. A deadline equal to `T` fires **before** the events at `T`.
2. Then `Now = T`, and the group's events are delivered in event order with `Now == T`.
3. After the group, deadlines with `Time <= T` created by its handlers drain before `ProcessGroup` returns. A deadline created at `Now == T` during the group therefore runs after the whole group, not between two events of it.
4. Future data is never visible earlier: a group is held back (not given to any handler) until all deadlines at or before its time have fired. Scheduling a deadline before `Now` throws `DeadlineInPastException`.
5. **Live watermark** `AdvanceTo(w)` means "no undelivered event has `AvailableAt < w`": it fires deadlines with `Time < w` (strict), sets `Now = w`, and is a no-op when `w <= Now`. Events at exactly `w` are still accepted and are preceded by deadlines at `w`; events before `w` afterwards are late. Watermarks change only *when in wall time* a deadline is processed, never its time, order or effect (tested by 100 randomised heartbeat insertions).
6. **Finish / horizon.** `Finish(h)` fires every remaining deadline with `Time <= h` (chained ones too), sets `Now = h`, and ends the run. `ReplayDriver.Run` requires `h`: events with `AvailableAt > h` are not delivered (`StoppedAtHorizon = true`); deadlines after `h` stay pending and are reported. The natural horizon is `SessionSchedule.SessionEndUtc`, but the caller chooses it; there is no default. Because `Finish` is always called with the horizon, a final market event before 15:00/15:10/15:35 cannot prevent those deadlines from running, and a gap of any length is handled by rule 1, not by the final call.
7. **Late events.** A group at or before the last processed group time, or before `Now`, throws `LateEventException`. Replay input must be non-decreasing in `AvailableAt` (checked up to the horizon).

**Session policy (implemented).** `SessionConfig` defaults: Asia/Kolkata (only value accepted; fixed UTC+05:30 from `IstTime.Offset`, no time-zone database), `EntryOpen 09:30:00`, `EntryClose 15:00:00`, `FinalClose 15:10:00`, `SessionEnd 15:35:00`, and **`LiquidationStart = null`** (a separate optional setting; the 15:05:00 proposal is deliberately not defaulted and is not an approved trading rule). Entry eligibility is `09:30:00.0000000 <= t < 15:00:00.0000000`. `SessionSchedule.ScheduleBoundaries` registers global `SessionBoundary` deadlines `session:EntryClose` (the waiting-entry invalidation and pending-entry cancellation instant), `session:LiquidationStart` (only if configured), `session:FinalClose` (the final-close deadline) and `session:SessionEnd`. CP1 only schedules them; what the engine does at each (cancelling orders, liquidation, `UnresolvedAtClose`) is CP2/CP3 work.

**Config hash / ids (implemented).** Canonical rules are in the `CanonicalJson` summary: ordinal-sorted properties/dictionary keys, order-insensitive sets, order-preserving lists, normalised invariant decimals, UTC timestamps, ASCII-escaped strings, explicit nulls; double/float/DateTime/Guid and undefined enums are rejected; `[CanonicalIgnore]` excludes non-config properties. The session date is not part of the config hash. Ids are `prefix_` + first 128 bits of SHA-256 over the canonical array `["tid1", kind, parts...]`. Golden values are pinned in tests and were cross-checked with an independent SHA-256 computation (Python) of the same canonical text.

**Banned-API guard.** A metadata scan of the `Trading.Core` assembly (member and type references) fails on wall-clock, random, GUID generation, machine-state, culture-state, thread/timer/stopwatch and `Task.Delay/Run` references, and a second test proves the scanner detects each family in a fixture. It supplements the behavioural tests (repeated, parallel, 8-culture, permutation and heartbeat tests). **Limitations:** it sees only direct references in Core's own IL; it cannot see calls reached through other assemblies, reflection or delegates; it cannot distinguish culture-sensitive overloads that share a name with safe ones (for example `ToLower()` and `ToLowerInvariant()`); and it cannot detect non-determinism that needs no banned API (hash-set iteration order, floating-point order, shared mutable state).

**Known limitations and ambiguities (CP1)**
- `EngineLoop` keeps every delivered event (identity → event) for duplicate detection: ~1.1–1.2 M ticks per session once real quote streams are routed through it. To be revisited in CP5 (store identity + content hash, or only events for tokens the engine actually consumes).
- Single-threaded by contract; re-entrant use throws. The loop's initial time must be at or before the earliest deadline.
- Adapters must normalise timestamps to UTC (offset zero is enforced) and handle `ExchangeTimestamp` quirks (`00:00:00`, ~06:07 starts) before the engine; Core does no such cleaning.
- No holiday/special-session calendar; the session is one `SessionConfig` per trade date.
- Config values must be `decimal` (not `double`) — a later numeric config needing floating point must use decimal.
- A deadline created at `Now == T` during a group runs after the group (rule 3); a design that needs it between two events of the same instant would need an explicit event type.
- `LiquidationStart` unset means *no* liquidation boundary exists; whether a forced-liquidation phase is wanted at all, and when, remains decision D-14.
- A harness finding worth recording: the first parallel multi-culture run failed because my *test logger* formatted `-1` with the Arabic culture's negative sign, not because of the engine; Core itself uses only invariant formatting. The test logger now uses `FormattableString.Invariant`.

**Contract points carried forward from the review (binding for CP2+)**
- The A2 candle price source (`priceField`) is **explicit and configurable**; ask-close is one *proposed variant*, not the only interpretation of "average price" (§A.4 item 5).
- A2 always compares the **executable ask** with the average of the **configured completed-candle price series of the selected option token** (§A.4 item 4).
- Marketable limit at the qualifying ask is the **recommended initial entry policy** (D-12). **No overlapping retry orders**: the previous order must be terminal before an opportunity is re-armed or another order is placed (§A.4 items 7–8).
- Costs remain **provisional** until authoritative sources and effective dates are verified (§A.9, D-7).
- ResearchV1 has **no daily-loss enforcement and no trade-count cap** (§A.7).
- **One position per variant in V1**, with identifiers and unique-key scope ready for several positions per variant (`SlotNo`, §A.3).

### A.12 CP2 implementation: entry engine, premium band, candles, opportunities (implemented 2026-10-10)

**Accepted decisions (Naveen, CP2 go-ahead):** the shared pure-engine architecture is accepted (D-1). The initial entry policy is a **marketable-limit entry intent at the observed qualifying ask** (D-12 initial policy; actual execution is CP3). A2 compares the **current eligible ask** with a moving average of **N completed candles of the same selected contract**. The candle **price source and N are explicit configuration with no default** (both are `required` members of the spec). V1 option candles use the **adaptive futures-bar boundaries**. No strategy edge claims, no parameter sweeps.

**New confirmed requirement — entry premium band.** Every variant selects a CE or PE (per its required side) whose **fresh executable ask is between ₹100 and ₹150 inclusive**; `MinimumEntryPremium = 100` and `MaximumEntryPremium = 150` are in the versioned configuration (`Selection`) and its canonical hash.

**Components (`NiftySignal.Trading.Core.*`, plus `NiftySignal.Trading.Signals`)**

| Class | Responsibility |
|---|---|
| `Instruments.OptionContract`, `OptionSide` | Contract metadata at selection time: token (stable id), side, strike, expiry, lot, tick |
| `Quotes.QuoteSnapshot`, `QuoteBook` | Latest top-of-book per token; executable = bid > 0, ask > 0, ask >= bid; a non-executable update clears (does not carry) the previous quote |
| `Selection.ContractSelectionConfig`, `ContractSelector` | Band, expiry policy, freshness, ranking; **pure** selection over candidates with a per-candidate rejection reason |
| `Candles.CandleConfig`, `CandleStore`, `CandleHistoryView`, `Candle` | Per-token completed candles on the adaptive bar clock; token-bound read-only histories |
| `Entry.EntryConditionSpec` (`ImmediateEntrySpec`, `AskBelowCandleMaSpec`), `EntryConditions` | Entry conditions and their guarded evaluation |
| `Variants.VariantSpec`, `IBaseStrategy`, `VariantState`, `Opportunity`, `CapacitySlot` | Variant identity/config, pure base strategy seam, isolated per-variant state, opportunity lifecycle, capacity slot |
| `Variants.VariantEngine` | The CP2 handler: events in, opportunities/intents/cancel requests/transitions/audit out |
| `Variants.EntryIntent`, `CancelRequest`, `OpportunityTransition`, `AuditRecord` | Plain-data outputs (no I/O) |
| `Config.TradingConfig` (schema **2**) | Adds `Selection`, `Candles`, `Variants` (dictionary keyed by VariantId) to the session policy; `ComputeVariantHash(id)` |
| `Signals.PlumbingEveryNthBarStrategy` | **Plumbing only, no edge claim:** valid for `validBars` bars from every `period`-th bar, side alternating Call/Put per episode |

**CP1 clock review (done before building on it).** `Schedule` rejects times before `Now`; `ProcessGroup` fires every deadline `<= T` before moving the clock to `T`; `AdvanceTo(w)` fires every deadline `< w` before moving the clock to `w`; `Finish(h)` fires every deadline `<= h`; `EngineClock` throws on any backward move. Hence **no deadline can be left behind the clock, and a heartbeat cannot cause a later dispatch that moves time backward**. A deadline callback always sees `Now == ScheduledDeadline.Time` (scheduled time), while the loop's own clock (`EngineLoop.Now`, the processing time) may already be the watermark; the two are different and both are tested. A heartbeat exactly at a deadline leaves it pending at the clock (it fires on the next, later heartbeat or before the equal-time group). A group delivered late but not behind the watermark still sees the equal-time deadline first. **No defect was found and no CP1 code was changed**; six invariant tests were added (`ClockInvariantTests`), including a 40-scenario randomised check of "pending deadlines never precede the clock".

**Premium band and contract selection (exact semantics)**
1. Selection is a pure function of: the contract universe with the latest quote of each contract, the required side, the trade date, the decision instant and `ContractSelectionConfig`. Nothing else is read; a quote with `AvailableAt` after the decision instant is rejected (`QuoteNotCausal`).
2. Candidate filters, in this order, each recorded as the rejection reason: wrong side; wrong expiry; no quote; not causal; stale (`age > MaxQuoteAge`, equality is fresh); not executable (two-sided, ask >= bid); `ask < MinimumEntryPremium` (`AskBelowBand`); `ask > MaximumEntryPremium` (`AskAboveBand`). Band ends are inclusive: 100.00 and 150.00 qualify, 99.95 and 150.05 do not.
3. **Expiry policy** `NearestExpiryOnOrAfterTradeDate`: the single nearest expiry in the universe that is at least `MinCalendarDaysToExpiry` (default 0, so the expiry day is admitted) after the trade date. There is no fall-through to a later expiry when the nearest has no qualifying contract.
4. **Ranking** (configurable list, default recommended policy): smallest `|ask − 125|` (band midpoint), then smallest bid/ask spread, then the stable contract identifier (ordinal token compare, always the final tie-break). Alternatives: `LowestAsk`. Output and evaluation list are independent of candidate order.
5. `NoEligibleContract` is recorded (with per-reason counts) and **no intent is emitted**. A still-valid opportunity in the entry window retries selection **on each later completed bar only** (never on a quote), until it selects, the strategy invalidates, or the window closes.
6. Once an opportunity selects a contract it is **fixed** for the opportunity, every re-arm and the resulting trade (`ContractFixation.Require`, `RequireFixedContract`). A contract leaving the band is **not** replaced: while the band condition fails the opportunity keeps its contract, emits no intent (audit `Blocked:AskBelowBand` / `Blocked:AskAboveBand`) and qualifies again when the same contract is back inside the band and everything else holds. A better-centred strike appearing later does not switch the contract.
7. **Recheck before every entry intent**, in this order: session eligibility; strategy validity (same episode); quote age; quote executable; premium band; entry condition; attempt limit; capacity. Immediate entries and every waiting-quote evaluation use the same path.
8. **Premium restrictions apply to entries only.** The engine has no exit logic yet; the band is not consulted by anything that is not an entry decision, and CP3 must keep it that way.
9. The `EntryIntent` carries the **qualifying ask**, the **limit price (= qualifying ask)**, `MinimumEntryPremium` and `MaximumEntryPremium`, the requested quantity (default **650**, not yet lot-validated: sizing execution is CP3), the contract, slot number, quote id/time, and the condition snapshot (kind, average, N, price source).
10. **Handoff to CP3 (open decision D-18).** A normal buy limit caps the price but cannot guarantee a minimum fill price: if the book's ask falls below ₹100 between observation and `ExecutionTime`, a real order at the observed ask would fill at the lower ask. CP3 must define simulator behaviour for fills below `MinimumEntryPremium` and a broker-reconciliation rule for the future, and must **never fabricate a fill inside the band** (no clamping a fill price up to 100). Recommended: fill at the actual executable ask (as the exchange would), flag the fill `BelowBand`, report their share separately, and offer a sensitivity mode that instead cancels such orders.

**Candle semantics (exact)**
1. A candle of token X for bar k covers the option quotes of X that are **ordered after `BarCompleted(k−1)` and before `BarCompleted(k)`** in the engine's total order. A quote in the same availability group as the closing tick precedes the bar event (kind rank) and belongs to candle k. A bar that completes in the same group as another bar (multiple chunks) leaves the later bar with an empty window, i.e. an invalid candle.
2. A candle becomes visible only when its `BarCompleted` event is processed (`PublishBar` runs first in the bar handler). Therefore **a quote in the same group as a bar never sees that bar's candle**; the next group's quotes do. An entry cannot use an unpublished candle (tests `QuoteInTheSameGroupAsABar…` and `QuoteInTheNextGroup…`).
3. Only executable quotes of the same token are observations. One-sided, zero-bid or crossed quotes are ignored and never carried forward.
4. A candle is **valid** iff it has at least `MinObservationsPerCandle` observations (default 1) and, when `MaxCloseAge` is set, its last observation is within that age of the bar end. A token with no valid observation in a window gets an **invalid candle** (reasons `NoObservations`, `TooFewObservations`, `StaleClose`); **there is no forward-fill**. A skipped `BarSeq` leaves absent candles (= invalid). Bar sequence must strictly increase.
5. `CandlePriceSource` (explicit choice, no default): `AskClose` (ask of the last executable quote), `BidClose`, `MidClose`, `TypicalPrice` = (highest mid + lowest mid + MidClose) / 3. `AskClose` is one proposed variant, not the only interpretation of "average price".
6. **N valid observations must be CONSECUTIVE** (V1): the last N published bars ending at the latest published bar, each valid for that token. A missing or invalid candle anywhere in the window, or fewer than N bars of history for that token, makes the condition **`NotEvaluable`** (reasons `NoBarsPublished`, `InsufficientHistory`, `InvalidCandleInWindow`); nothing is skipped or filled. A token first observed late has `InsufficientHistory` for earlier bars (not a gap).
7. Histories are kept **for every option token from its first quote, before any opportunity exists** (`HistoryDepth` bars, default 64, validated to be >= every N). The average of A2 is the arithmetic mean of the chosen price over the window of the **selected token only**: `CandleHistoryView` is bound to one token and cannot express a basket or strike splice; `EntryConditions` throws `ProhibitedConditionInputException` if the quote, history and opportunity are not the same contract.
8. A2 is evaluated **on every eligible quote update of the selected token** (not on bar events) with the latest published history: `ask < average` strictly. Ties are not satisfied.

**Opportunity and capacity semantics (exact)**
- **Episode:** an uninterrupted run of base-strategy validity on one side; it starts at the first valid bar (`EpisodeId = b<BarSeq>`). A side change starts a new episode (and invalidates the old opportunity). The opportunity is created at the first completed bar **inside the entry window** during the episode, so an episode that began before 09:30:00 gets its opportunity at the first eligible bar; none is created at or after 15:00:00.
- **States:** `AwaitingContract → Waiting → OrderPending → {Consumed | back to Waiting (re-arm)}`, plus terminals `Cancelled`, `Expired`, `Rejected`; every transition is an `OpportunityTransition` output.
- **Immediate (A):** evaluated at the moment a contract is selected (first successful selection, possibly on a later bar), then, if it is still `Waiting` (re-armed, or delayed by an occupied slot under `Wait`), on later eligible quote updates. **Waiting (A2):** evaluated on selected-token quotes.
- **Invalidation:** a bar on which the strategy is not valid cancels `AwaitingContract`/`Waiting` immediately; for `OrderPending` it emits one `CancelRequest` and **retains the reservation** until the outcome is reported; a fill that wins the race still consumes the opportunity.
- **No overlapping attempts / duplicate reservations:** an intent needs a free slot and a `Waiting` opportunity; the slot becomes `Reserved` at issue; `OrderPending` ignores further quotes.
- **Re-arm:** after a no-fill outcome (`Cancelled`, `Expired`, `Rejected`) the slot is freed and the opportunity returns to `Waiting` **only if** the previous attempt is terminal, the strategy is still valid for the episode, the instant is inside the entry window, `MaxAttemptsPerOpportunity` (default unlimited) is not exhausted and no cancel was requested; otherwise it ends (`Cancelled` / `Expired`). A re-armed opportunity issues nothing until the next eligible quote update.
- **Partial fill consumes the opportunity** (no top-up). The slot stays `Occupied` until `ReportPositionClosed`.
- **After a closed trade:** `ReentryPolicy.NewEpisodeRequired` — the base strategy must become not-valid and then valid again before another opportunity exists, even though the first position has closed. `Rejected(CapacityBusy)` (Immediate default) and any other terminal state also spend the episode.
- **Capacity:** `OnCapacityBlocked` (Reject for Immediate, Wait for waiting conditions unless configured). One slot per variant (`SlotNo = 0`; the slot list and ids already allow more). Independent variants never share a slot, account or id namespace (decision ids use the **variant hash**, so adding or removing another variant does not re-identify this one's decisions).
- **15:00 (`session:EntryClose`):** waiting/awaiting opportunities become `Expired(EntryClose)`; each `OrderPending` gets a `CancelRequest(EntryClose)` and keeps its reservation until the outcome is reported (then `Expired(EntryClose)` if unfilled, `Consumed` if filled); nothing is created or issued afterwards; a quote exactly at 15:00:00 is already too late.
- **Audit:** state changes and the first occurrence of each evaluation result (`Blocked:*`, `NotEvaluable:*`, `NotSatisfied:*`) per opportunity; per-quote repeats are counted, not recorded.
- **Feedback seam for CP3:** `VariantEngine.ReportEntryOutcome(EntryOutcome, scheduler)` and `ReportPositionClosed(...)` must be called from inside the loop (a deadline or event callback) so the resolution time is `scheduler.Now`. CP2 tests call them from scheduled test deadlines.

**Acceptance coverage (T-ids → tests)**
T19 contract fixation: `SelectedContract_StaysFixed…`, `ContractSelectorTests` causal/stale cases. T27 concurrent variants: `EachVariant_BehavesIdentically…`, `DeclarationOrder_NeverChanges…`, `Ids_DoNotDepend…`, repeated/parallel/culture/heartbeat runs. T28 capacity: `Capacity_IsPerVariant…`, `OccupiedSlot_Rejects…`. T29 accounts: `Capacity_IsPerVariant_AndAccountsAreSeparateByDefault`. T30 `Immediate_EntersAtOnce…`. T31 `ProhibitedInputs_AreRefused`. T32 `QuoteInTheSameGroup…`, `WaitingEvaluation_HappensOnlyOnQuotes…`, `HistoryExistingBeforeTheOpportunity…`, `InsufficientHistory…`, `AMissingCandle…`. T33 invalidation tests. T34 intent fields, band tests. T35 `ReArm_…`, `MaxAttempts…`, `PartialFill_Consumes…`. Premium: `PremiumBand_IsInclusiveAtBothEnds`, `EntryPremiumBand_AppliesAtSelection…`, `NoQualifyingContract…`, `Quotes_MustBeFresh…`, shuffled-candidate and tie tests. Session: `EntryWindow_Opens…`, `AtFifteenHundred_*`, `QuoteAtExactlyFifteenHundred…`, `ABarAtFifteenHundred…`.

**Known limitations (CP2).** Requested quantity is carried, not lot-validated or sized (CP3). The contract universe is a static list supplied at construction. Strategies are pure functions of the bar only (no feature inputs yet). `FinalClose`, `LiquidationStart` and `SessionEnd` deadlines are ignored by `VariantEngine` until CP3 (no positions are managed). The engine does not yet consult the adaptive readiness policy. Audit records for quote evaluations are first-occurrence only. `TradingConfig` schema is now 2 (all earlier hashes are superseded).

**Corrections to earlier contract text.** Decision ids use the **variant hash** rather than the whole-config hash (so they do not change when another variant is added); schema version bumped to 2; the ₹100–150 premium window and the "ask nearest the midpoint, then spread, then identifier" ranking are now confirmed/recommended and replace the "values unresolved" wording for D-4.

### A.13 CP3 implementation: simulated execution, sizing, positions, exits, costs (implemented 2026-10-10)

No strategy research or profitability claim is made anywhere in this section. Real-order mode is **unavailable**: `ExecutionConfig.Adapter` accepts only `Simulated`, configuration validation rejects `Broker`, and the `TradingEngine` constructor refuses to start with it. No database, persistence, adapter, `Host` registration or broker call exists.

**A.13.1 Clock clarification (scheduled deadline time vs engine clock vs watermark)**

| Name | Definition | Who sees it |
|---|---|---|
| **Scheduled deadline time `D`** | `ScheduledDeadline.Time`: the instant an order matures (`decision + Latency`), expires (`decision + EntryOrderLifetime`), an exit attempt is due, or a session boundary falls | The deadline callback: `scheduler.Now == D` exactly |
| **Engine clock** `EngineLoop.Now` | The time of the last processed event group, fired deadline or watermark. Monotonic | Event callbacks (`Now == event AvailableAt`) and anything reading the loop |
| **Watermark `W`** | The argument of `AdvanceTo(W)` (live heartbeat): "no undelivered event has `AvailableAt < W`". The loop fires deadlines `< W`, then the clock becomes `W` | Only the loop; **never** used for execution |
| **Execution time** | For a matured/retried order: `D`. For a resting entry order evaluated on a new quote: that quote's `AvailableAt` | `ExecutionEngine`: quote lookup, quote age, session/expiry checks |

Worked example (decision `T0`, latency 1 s, quote A at `T0+0.5` ask 124, heartbeat `W = T0+1.2`, quote B at `T0+1.5` ask 118): the maturity deadline has `D = T0+1.0`; it fires during the heartbeat, its callback sees `Now = T0+1.0`, execution time is `T0+1.0`, the quote used is the latest delivered quote with `AvailableAt <= T0+1.0` (A), its age is `D − A = 0.5 s` (not `W − A = 0.7 s`), the fill is recorded at `T0+1.0` at 124, and only afterwards does the loop clock read `T0+1.2`. Quote B has not been delivered at that point and is never visible to the earlier deadline. Three independent protections: (1) the loop delivers a group only after all deadlines `<= its time` fired; (2) `QuoteHistory` holds only delivered quotes and answers "latest at or before `D`"; (3) quote age is computed against `D`. Equal-time rule: a quote with `AvailableAt == D` is delivered **after** the deadline (deadline-before-event), so the deadline cannot see it; the quote then evaluates the still-resting order as a fresh quote at time `D`. At one instant the deadline order is cancel < expire < mature (ordinal key prefixes `exec-cancel:` < `exec-expire:` < `exec-mature:`): a cancellation wins over an execution, an expiry wins over a fill, and a quote at the expiry instant cannot fill. Regression tests: `HeartbeatBeyondMaturity_…`, `QuoteAfterTheDeadline_…`, `QuoteFreshness_IsMeasuredAtTheExecutionTime_…`, `QuoteAtTheMaturityInstant_…`, `OrderLifetime_ExpiresExactlyAtItsBoundary_…`. Honest note: because delivery already precedes visibility, replacing the time-indexed lookup by "newest delivered quote" is an *equivalent* change in every reachable scenario (the lookup is defence in depth); the discriminating test is the freshness-vs-watermark one.

**A.13.2 Entry execution (marketable buy limit at the qualifying ask)**
- An `EntryIntent` creates an order with limit = qualifying ask. It reaches the simulated exchange at `decision + Latency` (positive; zero is rejected). **Correction (CP4, §A.14.0): positive latency does not by itself prevent the quote that triggered the decision from filling the order** — a still-fresh signal quote (age `<= MaxQuoteAge` at the maturity instant) may fill it under the default `SignalQuoteReusePolicy.AllowFreshSignalQuote`; `RequireQuoteAfterDecision` is the separately named stricter policy and lives until `decision + EntryOrderLifetime`; fills are possible in `[maturity, expiry)`.
- **Default policy `ResearchEnforceBand`:** fill only if the execution-time ask is `<= limit` **and** inside `[100, 150]` inclusive; otherwise no fill and the order keeps working until expiry (it may fill on a later quote). Price improvement is real: the fill price is the actual ask (e.g. limit 125, ask 118 → 118).
- **`BrokerRealisticBelowBand` (separately named):** an ask below ₹100 (but `<= limit`) fills at that actual price, flagged `BelowBand`, counted in `SessionEndReport.BelowBandFills`; the price is **never clamped** into the band. **A broker buy limit order can cap the price but cannot enforce a minimum fill price**; the default mode is a research rule of the simulator, not something a broker would do.
- **Revalidation when the order matures and on every later evaluation:** (1) session: execution time must be `< EntryClose` (15:00:00) else the order is cancelled (opportunity `Expired(EntryClose)`); (2) contract identity: the order's contract must be the opportunity's fixed contract; (3) strategy validity (same episode) else cancelled; then quote quality (keeps the order working if it fails): a quote exists, is causal, age `<= Execution.MaxQuoteAge` **at the execution time**, executable (two-sided, positive prices, not crossed), spread `<= MaxSpreadAbsolute` and `<= MaxSpreadFraction x mid` (entries only), displayed ask quantity usable.
- **Liquidity (level 1 only):** usable = unconsumed displayed ask size x `FillFractionOfDisplayed`; the fill is `min(requested lots, usable / lotSize)` whole lots; unknown or zero displayed size means no fill unless `RequireDisplayedQuantity = false`. **Partial-entry policy `KeepFilledCancelRemainder`:** a fill happens once; the filled whole lots become the position, the rest of the order is terminal (cancelled), and a partial fill consumes the opportunity. **Repeated consumption:** each virtual book remembers, per (contract, side), the displayed `(price, size)` it has already taken from and how much; an identical displayed state is *the same liquidity* (remaining = displayed − consumed), a different displayed state is treated as fresh liquidity (limitation: a real refill to exactly the same size cannot be told apart).
- **Cancellation:** `StrategyInvalidated` cancels take effect at `request + CancelLatency`, but revalidation cancels the order at the first evaluation after the invalidating bar, whichever comes first; an order that fills at an instant before the invalidating bar (or in the same group but ordered before it, or at a deadline instant equal to the bar's time) is not cancelled. **The 15:00 `EntryClose` cancel is immediate** (a session rule of the simulated exchange): at 15:00:00 no entry fill is possible, all outstanding entry quantity is cancelled, reservations are held until that terminal resolution and then released.
- **No overlapping orders:** one order per opportunity at a time; the slot stays `Reserved` until the order is terminal; re-arm only after a terminal no-fill outcome (CP2 rules). **Attempt limits are disabled by default** (`MaxAttemptsPerOpportunity = null`; tested with six consecutive attempts).
- **Independent virtual books:** every variant has its own execution book (liquidity ledger, cash, positions), labelled `independent-virtual-book:<variant>` on every fill. Two variants can each take the same displayed 650 units; their combined fills are **not** feasible in one account and must never be presented as such.
- **Level-1 limitations (all simulations):** only the best bid/ask and their displayed sizes; no depth beyond level 1, queue priority, hidden liquidity, other participants, market impact, order rejection, exchange price bands or freeze quantities; the displayed size is assumed available to us in full (times the fill fraction) once; latency, quote age, spread limits, lifetime and cancel latency are placeholders; recorded quotes show feed timing, not broker round-trip time.

**A.13.3 Sizing**
- `FixedQuantitySpec` (default **650**): must be a positive whole number of lots of the selected contract's lot size from the instrument snapshot of the trade date (`OptionContract.LotSize`; there is no hard-coded 65 in the engine). A violation **fails validation with a clear reason** — the engine constructor rejects it for every contract in the universe (`Variant 'A': fixed quantity 650 is not a whole number of lots for CE23900 (lot size 75)`), and `Sizer` returns `InvalidQuantity`; nothing is rounded. No capital limit applies to fixed-quantity research.
- `PremiumBudgetSpec { Rupees, IncludeEntryCosts = true, MaxLots }`: the largest whole number of lots with `lots x lotSize x orderLimit` (plus the estimated buy-side costs on that turnover when included) `<= Rupees`; **valued at the order limit (the qualifying ask), never at a later fill price**; rounds down only; below one lot the entry is **skipped** (`Blocked:InsufficientBudget`, no reservation, the opportunity keeps waiting and can qualify at a lower ask). Independently recomputed examples (limit 125, lot 65): budget 50,000 → 6 lots (48,750 + 45.56 costs); 48,760 → 5 lots with costs, 6 without; `MaxLots = 3` → 3; budget 8,000 → skipped at 125, 1 lot at 120.

**A.13.4 Positions and exits**
- A position is created only from an actual entry fill: quantity, average entry **fill** price and entry costs come from the fill. The contract is fixed for the whole position. The premium band is not consulted for exits (a stop may fill at ₹95).
- Exit rules per variant (`ExitSpec`, each optional): `TradeSlPct`, `Tp1Pct` + `Tp1Proportion`, `FinalTpPct`, `RemainderSl` (`Unchanged` default | `MoveToBreakeven`, which only has an effect when `TradeSlPct` is set: after TP1 completes the stop level becomes the average entry fill price). Thresholds are fractions of the **average entry fill price** (price only). Triggers are evaluated on **executable bid** quotes (two-sided, non-crossed, fresh) of the held contract on every quote event; bars are never needed.
- **Gap behaviour:** a trigger creates a sell order that executes after `Latency` at the **eligible bid at that execution time**, not at the threshold: a stop at 112.5 hit by a bid of 95 fills at 95 (`FillRecord.TriggerThreshold` keeps 112.5 for analysis).
- **Priority** when several triggers qualify on one event (lowest = highest priority): `SessionLiquidation > TradeSl > StrategyExit > FinalTarget > Tp1`; exactly one order is created per evaluation.
- **TP1 quantity** = `floor(Tp1Proportion x openLots)` lots at the *first* trigger (stored once, `Tp1TargetQuantity`): 10 lots @ 0.5 → 5; 5 lots @ 0.5 → 2; 3 lots @ 0.4 → 1; **1 lot → 0 → TP1 skipped** (`Tp1Resolved(Skipped)`, the position continues under its other rules; never rounded up to a lot); 10 lots @ 0.99 → 9. TP1 triggers once; a partially filled TP1 keeps `Tp1 = Triggered` and the same order keeps retrying (`ExitRetryInterval`) until its target quantity is filled (`Done`).
- **No overselling:** each live exit order reserves its remaining quantity on the position; a new exit may use only `open − reserved` (TP1 pending + a stop → the stop covers the other half; a liquidation covers only the unreserved rest). Exit order ids are deterministic and include the position id (a test found that omitting it collided across a variant's positions; fixed).
- **Idempotency across reconstructed in-memory state:** the TP1 decision is a pure function of the event stream and position state; replaying the same events into a fresh engine yields identical orders, ids, fills and TP1 state (tested by building the state twice). Database recovery is CP6.
- **Strategy-exit interface:** `ExecutionEngine.RequestStrategyExit(variantId, positionId, quantity?, scheduler)` creates an exit order through the same path (whole lots, never more than unreserved).
- Exit orders are market-style sells at the eligible bid (no limit, no collar); they rest and retry every `ExitRetryInterval` until filled or the session ends. A protective price limit for exits is open decision D-23.

**A.13.5 Session liquidation, final close and session end**
- A runnable end-to-end `TradingEngine` **requires an explicitly configured `LiquidationStart` at or after `EntryClose`**; no 15:05 is assumed anywhere in the engine (15:05 appears only in test configurations). Without it, with a missing cost schedule, or with a broker adapter, the constructor throws.
- At `LiquidationStart` every open position gets a `SessionLiquidation` order for its **unreserved** quantity, first attempt after `Latency`, retries every `ExitRetryInterval` (explicit retry policy). Fills need execution time `< SessionEnd`.
- **At `FinalClose` (15:10:00 by default)** every position with open quantity is reported `UnresolvedAtClose` (open and reserved quantity, live orders, the last eligible bid **and its age**; the bid is a report, never a fill price). Liquidation orders **continue** to retry after 15:10; any fill after it is real but flagged `AfterFinalClose` and counted in `SessionEndReport.FillsAfterFinalClose`.
- **At `SessionEnd`** any position still open is reported `UnresolvedAtSessionEnd`, set to state `Unresolved` (it is *not* closed in the books, has no manufactured exit and no realised P&L beyond real fills), exit orders are cancelled, further execution is disabled, and a `SessionEndReport` per variant is emitted. A fillable quote 0.5 s before the end can still be too late (the boundary precedes the retry at the same instant).

**A.13.6 Costs and accounting**
- Costs are an **effective-dated, versioned** list of `CostScheduleEntry` (no overlaps; a trade date without a schedule is an error; there is no default schedule and no silent zero). Brokerage is a required, configurable spec. **Correction (CP4, §A.14.0): the CP3 spec named "FlatPerOrder" charged per fill, which was mislabelled.** It is now `FlatBrokerage` / `PercentOfTurnoverBrokerage` with an explicit required `BrokerageUnit { PerOrder, PerFill }`, so turnover-based charges and the brokerage charging unit are separate concerns (statutory charges are always per fill on that fill's turnover); this project does not know Naveen's plan and invents none (tests use a flat ₹20 **TEST** value).
- **Per fill, on premium turnover (qty x price):** brokerage; STT (sell only); exchange transaction charge incl. IPFT (both sides); SEBI fee (both); stamp duty (buy only); GST = `GstRate x (brokerage + exchange + SEBI fee)`. **Rounding:** each of brokerage, STT, exchange, SEBI, stamp is rounded to 0.01 half-away-from-zero; GST is computed on the *rounded* brokerage + exchange + SEBI and rounded the same way; the fill total is the sum of the six rounded components; nothing is netted across fills before rounding; every `FillRecord` stores the components.
- **Realised/net P&L** of a position = `(exit proceeds − average entry price x exited quantity) − all entry costs − all exit costs incurred so far` (the full entry costs are charged as soon as the position exists, partial-exit costs as they occur). Cash of a virtual book = `Σ −(buy notional + costs) + Σ (sell notional − costs)`; flat, it equals the sum of positions' net P&L (tested).
- **Unrealised liquidation value** = `openQty x eligible bid − estimated sell costs`, from the latest quote at or before the valuation time that is fresh (`<= MaxQuoteAge`) and executable; otherwise the valuation is flagged `Stale`, `Missing` or `NotExecutable` and carries no value (never another price).
- **Daily SL (ResearchV1) is record-only:** when a variant's `DailySl` is set, the first time `DailyPnl <= −DailySl` (realised net + liquidation value of open positions, an unmarkable open position conservatively valued at zero and flagged) a single `DailySlBreached` is emitted and the worst daily P&L is kept for the session report. It blocks no entry, forces no exit and caps no trades (tested over seven consecutive trades with a breach after the first). Broader profile/kill-switch work stays in CP4.

**A.13.7 Cost-source status (verification performed 2026-10-10)**

| Component | Rate in the engine | Status |
|---|---|---|
| Exchange transaction charge incl. IPFT, index options | ₹3,553 per crore of premium each side (0.03553 %) = ₹3,552.99 + ₹0.01 IPFT, w.e.f. 2026-03-01 | **Read from the primary document**: NSE circular `NSE/FA/73061`, 2026-02-27 (PDF text extracted; previous ₹3,503 + ₹50 = ₹3,553, i.e. unchanged total) |
| STT on sale of an option | 0.15 % of sale premium, w.e.f. 2026-04-01 (0.10 % up to 2026-03-31) | **Read from the primary document**: NSE circular `NSE/FATAX/73524`, 2026-03-31 (Finance Act 2026, assent 2026-03-30) |
| SEBI turnover fee | ₹10 per crore (0.0001 %) | Provisional: NSE investor-page extract; the SEBI/NSE circular text was not read |
| Stamp duty, buyer, options | 0.003 % of premium | Provisional: NSE stamp-duty page extract; Indian Stamp Act schedule text was not read |
| GST | 18 % on brokerage + exchange charge + SEBI fee (not on STT or stamp duty) | Provisional: broker/aggregator tables and a BSE notice about the SEBI fee; no primary tax document read |
| Brokerage | not supplied | **Unknown — required input** |
The whole schedule keeps the label `PROVISIONAL` (`NseIndexOptionsCostSchedules.StatusText`), is effective from 2026-04-01 and therefore covers all 18 development sessions (2026-09-04 … 2026-10-01); no earlier date is covered. Reports must show results with and without the provisional components.

**A.13.8 Candle price sources (precise definitions; no LTP source exists)**
All four sources use **only executable two-sided quotes of the same token observed inside the candle window** (between the previous and the current `BarCompleted` in the engine's total order). The last traded price carried by `QuoteEvent.Last` is never read by the candle store, and a quote with a last price but no executable bid/ask creates no observation (test). `AskClose` = ask of the last such quote in the window. `BidClose` = its bid. `MidClose` = (bid + ask) / 2 of that same last quote. `TypicalPrice` = (highest mid + lowest mid + MidClose) / 3, where "highest/lowest mid" range over the mids `(bid + ask) / 2` of **every** executable quote in the window and `MidClose` is the mid of the last one; with a single observation all three terms are the same mid. There is no LTP-based, volume-weighted or trade-based source.

**A.13.9 Independent golden trade ledger (variant A, fixed 650, TP1 10 % / 50 %, final target 20 %, test brokerage ₹20 per fill)**
Timeline: decision at 10:00:00 (ask 125.00), entry fills 10:00:01; quote bid 138.00 at 10:02:00 → TP1 fills 10:02:01; quote bid 151.00 at 10:03:00 (gap through the 150.00 final level) → final fills 10:03:01.

| Fill | Qty | Price | Notional | Brokerage | STT | Exchange | SEBI | Stamp | GST | Total costs |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Buy (entry) | 650 | 125.00 | 81,250.00 | 20.00 | 0.00 | 28.87 | 0.08 | 2.44 | 8.81 | **60.20** |
| Sell TP1 | 325 | 138.00 | 44,850.00 | 20.00 | 67.28 | 15.94 | 0.04 | 0.00 | 6.48 | **109.74** |
| Sell final | 325 | 151.00 | 49,075.00 | 20.00 | 73.61 | 17.44 | 0.05 | 0.00 | 6.75 | **117.85** |

Gross P&L = (138 − 125) x 325 + (151 − 125) x 325 = 4,225.00 + 8,450.00 = **12,675.00**; total costs = 60.20 + 109.74 + 117.85 = **287.79**; net = **12,387.21**; book cash = −81,310.20 + 44,740.26 + 48,957.15 = **12,387.21**. The figures were first computed by hand and then recomputed by a separate Python script (`decimal`, half-up) that shares no code with the engine; the engine's ledger matched component by component. The test pins these numbers.

**A.13.10 Changes to earlier contract text**
- `TradingConfig` schema **3**: adds `Execution` and `Costs`; `VariantSpec.RequestedQuantity` is replaced by `Sizing` (default `FixedQuantitySpec { Units = 650 }`) plus `Exit` and `DailySl`; `EntryIntent` now carries `Lots` and `LotSize`; `QuoteEvent`/`QuoteSnapshot` carry optional displayed `BidQty`/`AskQty`. All earlier hashes are superseded (new goldens recomputed independently).
- A.6/A.1: exit orders are market-style sells at the eligible bid; the "collar escalation" and marketable-limit exits are **not implemented** (D-23). A.1's 15:05 stays a PLACEHOLDER proposal that exists only in test configuration.
- A.9: cost status above supersedes the earlier "primary documents could not be opened" remark for the exchange charge and STT.

**A.13.11 Limitations (CP3)** — level-1 simulation (above); single-fill entry; no order rejections; fills inside one availability group are processed in the engine's total order; exit orders ignore spreads (they only need an executable bid) so a very wide bid/ask can produce a poor exit; daily-SL valuation of an unmarkable position is the conservative zero; costs provisional (A.13.7); the pre-run quantity validator fails the whole run if any universe contract is incompatible even if never selected (conservative); no persistence — recovery across restarts is CP6; `ExecutionEngine` keeps all quote history within a retention window only.

### A.14 CP4 implementation: CP3 corrections, risk profiles, integrity, audit, daily summaries (implemented 2026-10-10)

No strategy research, edge claim, database, adapter, `Host` registration, broker call or deployment exists in this section. `TradingConfig` schema is now **4** (adds `Risk`; brokerage units and `SignalQuoteReuse` change the hashed text); all earlier hashes are superseded and the schema-4 goldens were recomputed independently.

**A.14.0 CP3 corrections found by the CP4 review**

| # | CP3 statement / behaviour | Finding | Correction and evidence |
|---|---|---|---|
| C1 | A.13.2 said a positive latency means "the signal quote can never fill" | **Wrong.** The execution quote is the latest *delivered* quote at or before the maturity instant; the quote that triggered the intent (age 1 s at maturity, limit 2 s) was eligible | Documented accurately; `ExecutionConfig.SignalQuoteReuse`: `AllowFreshSignalQuote` (default, the actual CP3 behaviour, now explicit) or `RequireQuoteAfterDecision` (execution quote must have `AvailableAt` strictly after the decision; until then the order keeps working and a note `AwaitingNewerQuote` is audited). Tests `SignalQuoteReuseTests`: fresh signal quote fills; older than `MaxQuoteAge` does not; unavailable (none delivered) does not; a quote after the decision is used under the strict policy; displayed size consumed by an earlier fill of the same book is not reused (fresh quote with a different displayed state is) |
| C2 | A.13.6 "FlatPerOrder" brokerage was charged on **every fill** | **Mislabelled/incorrect for multi-fill orders** (a TP1 order filled in two parts paid brokerage twice) | `BrokerageSpec` now has a required `Unit { PerOrder, PerFill }`; `FlatBrokerage { Amount }`, `PercentOfTurnoverBrokerage { Rate, Cap }`; there is no default unit and **no brokerage plan is assumed** (D-7). Per-order accounting uses an `OrderChargeState` (fills so far, turnover so far, brokerage charged) so multiple fills of one order never multiply the per-order amount; a percentage per-order cap applies to the order's cumulative turnover. Statutory components (STT, exchange, SEBI, stamp) stay per fill on that fill's turnover; GST stays on the rounded brokerage + exchange + SEBI of that fill. Independent Python recomputation (no shared code) of one exit order filled 130 then 195 @ 138.00: flat 20 per order → 20.00 + 0.00 (fill costs 58.05 + 51.69 = 109.74); flat 20 per fill → 20 + 20 (58.05 + 75.29 = 133.34); 0.05 % cap 20 per order → 8.97 + 11.03 (45.03 + 64.70 = 109.73); per fill → 8.97 + 13.46 (45.03 + 67.57 = 112.60) |
| C3 | Golden ledger label "test brokerage ₹20 per fill" | Still correct (the TEST value is now declared `PerFill`, unchanged numbers: 60.20 / 109.74 / 117.85) | Unit made explicit in the test helper |

Everything else in A.13 stands.

**A.14.1 Risk profiles (`NiftySignal.Trading.Core/Risk`)**
- `RiskProfile { Name, Version, MaxTradesPerDay?, DailySlEnforcement, MaxPeakPremiumExposure? }` is part of the hashed `TradingConfig` (canonical text `"Risk":{"DailySlEnforcement":"None","MaxPeakPremiumExposure":null,"MaxTradesPerDay":null,"Name":"ResearchV1","Version":"1"}`), so a profile change changes the configuration and variant hashes.
- **`ResearchV1` (default, the only startable profile):** no maximum trade count; the daily SL is **observed and reported only** (`DailySlBreached` output, audit row, summary fields; no entry lockout, no automatic liquidation); peak committed premium is recorded but not limited; the per-trade SL remains an **independent** exit rule of the variant (tested: trade-SL only → exit, no daily observation; daily-SL only → observation, no exit; both → each acts by its own rule). Validation rejects any enforcement setting on `ResearchV1`.
- **`RealOrder`:** carries the optional future settings `MaxTradesPerDay`, `DailySlEnforcement { None, BlockEntries, BlockEntriesAndLiquidate }`, `MaxPeakPremiumExposure`. They are configurable and unit-tested through the pure `RiskPolicy.CheckEntry` / `ShouldLiquidate` (reason codes `RiskMaxTrades`, `RiskDailySlBreached`, `RiskExposureLimit`), but the profile is **unstartable**: `StartBlockers()` reports it and the `TradingEngine` constructor throws (`... not startable ...`); no broker execution exists. No monetary value is approved.
- Always-on rules (every profile, not options): lot/quantity validation, one position per variant incl. pending reservations, duplicate prevention, the session window, and the integrity gates below. `ResearchV1` always returns `Allow` from `RiskPolicy` whatever the state (tested with extreme values).

**A.14.2 Integrity (global feed faults vs single-contract staleness)**
- **Scope.** *Global*: feed health (`IntegrityManager`) and kill-all. *Variant*: kill of one variant. *Contract*: a stale, missing or one-sided quote of one contract is **not** an integrity state; it is ordinary quote quality (reason codes `QuoteStale`, `NoQuote`, `QuoteNotExecutable`) that blocks entries and fills on that contract only and never cancels other contracts' orders (tested). The engine never infers a global fault from a quiet contract.
- **States.** `Healthy → Degraded` on a `FeedHealthEvent(Degraded, reason)` supplied by the adapter; `Degraded → Recovering` on `FeedHealthEvent(Healthy)`; `Recovering → Healthy` on the **first accepted option quote at or after the recovery instant**; a Degraded event while Recovering returns to Degraded; Degraded-while-Degraded and Healthy-while-Healthy change nothing (tested). Each change is an `IntegrityTransition` output.
- **Degraded / Recovering effects.** New entries are blocked for every variant (gate reason `IntegrityDegraded` / `IntegrityRecovering`); opportunities already waiting stay waiting (no consumption, and they re-arm for evaluation only after recovery); every pending entry order gets a `CancelRequest(IntegrityDegraded)` for the unfilled quantity, and the execution engine **revalidates the gate on every evaluation** (a Degraded order cannot fill even before the cancel takes effect); the position **reservation is kept until the cancellation or a fill resolves** (never released early, so no double position). **Exits, session liquidation and TP1/SL continue** on eligible quotes — they never wait for health and never manufacture a fill when no eligible quote exists (the position stays open and is reported unresolved if it never fills).
- **Event admission (`EventAdmission`, used by `TradingEngine`).** Quarantined with an `EventQuarantined` record (reason `Late`, `ConflictingDuplicate`, `Inconsistent:NegativeValue`, `Inconsistent:BarSequence`) and **no effect on state**: an event whose `AvailableAt` is not after the last processed group/engine clock; the same identity re-delivered with different content; a quote with a negative price/size; a bar whose sequence does not increase. An exact duplicate is ignored idempotently, not quarantined.
- **Ordering.** `EventKind` ranks `Control (−2) < FeedHealth (−1) <` data events at the same instant, so a fault/kill at instant T is seen before quotes of instant T, identically in replay and live paper (tested).
- **CP5 adapter interface (interface only).** `IFeedHealthSource.Drain(DateTimeOffset watermark)` returns not-yet-returned `FeedHealthEvent`s at or before the watermark in strictly increasing sequence order; the adapter merges them into the stream. Live: derived from the ingestion `DataGap`/receive watchdog; replay: from recorded gaps. Nothing in Core polls a feed, reads a wall clock or monitors live data.

**A.14.3 Manual kill switch (`OperatorCommandEvent`: `Kill` / `Reset`; scope `*` or a variant id)**
- An ordered, replayable input event (`Control` rank). Real operator input (Dashboard) is not wired in CP4; this is the engine-side contract.
- **Kill.** Marks the scope killed (idempotent: a repeat reports `AlreadyKilled`); blocks new entries of the scope (`KillSwitch`); cancels the scope's **waiting opportunities** (terminal reason `KillSwitch`; they do not resume after a reset); requests cancellation of the scope's **pending entry quantities** (`CancelRequest(KillSwitch)`, reservations kept until resolution); open positions keep their normal exit rules. Mode `BlockEntries` (default) stops there; mode `BlockEntriesAndLiquidate` additionally creates `ManualKill` exit orders for the scope's **unreserved** open quantity through the normal exit path (eligible bid, retries, never a manufactured fill; priority `SessionLiquidation > ManualKill > TradeSl > StrategyExit > FinalTarget > Tp1`).
- **Reset.** `Reset("*")` clears all kills; `Reset(variant)` clears that variant, **ignored while a global kill is active** (`ResetIgnored:GlobalKillActive`); nothing to reset reports `NothingToReset`. A reset only re-allows *new* opportunities/entries; it never resurrects a killed opportunity, a cancelled order or a liquidation. An unknown variant scope is rejected (`Rejected:UnknownVariant`). Each command is a `KillSwitchChanged` output with actor, reason, effect and the counts cancelled/liquidated. No daily-loss auto-trip exists in `ResearchV1`.

**A.14.4 Structured audit (`AuditTrail`, derived from the outputs)**
- `TradingEngine.AuditTrailEntries()` maps **every** output to exactly one `AuditEntry` (`Seq`, `At`, `RunId`, `RunMode`, `AccountId`, `VariantId`, `VariantHash`, `OpportunityId`, `OrderId`, `PositionId`, `ConfigHash`, `Category`, `Reason` (stable `ReasonCode` enum), `Event`, `Detail`). Categories: Opportunity, Selection, Evaluation, Sizing, EntryIntent, EntryOrder, Fill, Cancellation, Exit, Position, Integrity, KillSwitch, DailySl, Quarantine, Session, Summary. Accepted and rejected opportunities, intents, orders, fills, cancellations, exits, integrity transitions, kill commands, quarantines and daily-SL observations are all covered. Free text only accompanies the reason code. `RunId`/`RunMode` come from the engine constructor (`run-unspecified` / `Historical` by default).
- **Bounded repetition.** Repeated identical per-quote rejections (same variant, opportunity, stage, reason) produce the **first** `Evaluation` row and one `EvaluationSummary` (count, first and last time) instead of one row per quote; **state transitions and accounting events are never folded** (tested: 50 identical rejections → 1 row + 1 summary `x50`, daily-summary count 50, opportunity transitions unchanged).
- Persistence of the trail is CP6; CP4 provides the in-memory derivation and the field set.

**A.14.5 Per-variant daily summary (`DailySummary`, `TradingEngine.DailySummaries(asOf)`)**
- Counts: opportunities created / still active / ended by reason, entry attempts, entry-order outcomes, **rejection reasons** (per stage and code), requested vs filled entry quantity, partial entries, trades opened, closed trades (a **partial exit is not a new trade**), TP1 exits (distinct TP1 orders), exit fills by reason, below-band fills, late closures (fills after 15:10), unresolved positions.
- Money, all per independent virtual book (never summed across variants): **closed-trade** gross P&L, each cost component (brokerage, STT, exchange charge, SEBI, stamp, GST, total), net P&L, and net P&L **excluding the provisional components** (`ProvisionalComponents`, `CostSourceStatus`, `CostScheduleVersion` are printed with every summary). **Open/unresolved positions are reported separately** (`OpenValuations` with status Marked/Stale/Missing/NotExecutable; `UnrealisedGrossMarked` counts only marked positions; `UnavailableValuations`) and are never folded into closed-trade results; unavailable valuations carry no value. `DailyPnlAsOf` (realised net + liquidation value, an unmarkable position valued at zero and flagged `DailyPnlConservativeMark`) is the daily-SL observation basis.
- Daily-SL: configured value, breach observed, worst daily P&L — observation only. **Peak premium exposure** = maximum committed premium (open positions at average entry + working entry orders at limit), recorded, not enforced.
- **Bid-based MFE / MAE (`TradeExcursion`).** Unit: premium rupees **per unit** of the contract (not multiplied by quantity) relative to the average entry **fill** price, plus percent of it. Observed series: every eligible (fresh, executable) bid of the held contract from the entry fill to the close/session end. `MFE = max(0, highest bid − entry)`, `MAE = min(0, lowest bid − entry)`. **Post-TP1 treatment:** the series is *not* interrupted or restarted by TP1 or any partial exit — one MFE/MAE per position; `MfeAtFirstExit`/`MaeAtFirstExit` give the same values as of the first exit fill. Coverage: `None` (no eligible bid, values unavailable), `Complete`, `HasGaps` (some gap between observations exceeded the quote-age limit, so the true extremes may lie in an unobserved stretch); `Observations` and `Gaps` are reported. These are bid-based liquidation-oriented excursions, not last-trade excursions, and they do not include costs.
- Golden day (the A.13.9 ledger): gross 12,675.00; costs 287.79 (brokerage 60.00, STT 140.89, exchange 62.25, SEBI 0.17, stamp 2.44, GST 22.04); net 12,387.21; net excluding provisional components 12,471.86; peak exposure 81,250; MFE 26.00 / MAE −1.00 per unit (3 observations, 2 gaps > 2 s → `HasGaps`); MFE at first exit 13.00. Pinned by tests.

**A.14.6 Preserved trading contract (re-asserted, unchanged by CP4):** entry window [09:30:00, 15:00:00) Asia/Kolkata; outstanding entry quantity cancelled at 15:00; targets closed by 15:10 with unresolved fills reported honestly; `LiquidationStart` explicit and required (no 15:05 in the engine); multiple independent variants; one position per variant including pending reservations; default requested quantity 650 validated against the effective lot size; fixed contract through exit; entry premium ₹100–₹150 inclusive on the executable ask; strict-band default and the separately named `BrokerRealisticBelowBand`; exits never restricted by the band; A2 compares the ask with a configurable moving average of completed candles of the same contract; no daily-loss blocking and no trade-count cap in `ResearchV1`.

**A.14.7 Limitations (CP4):** the audit trail and summaries are in-memory (no persistence); integrity inputs exist only as events (no live monitoring; the watchdog thresholds are CP5 adapter concerns); `Recovering → Healthy` requires an *option* quote (a quiet market after recovery keeps entries blocked, deliberately conservative); a kill's liquidation uses the normal exit path and can stay unresolved if no eligible bid exists; MFE/MAE are bid-sampled at quote events only; provisional cost components remain provisional; brokerage remains unknown.

### A.15 Agreed future research plan (documented only — nothing in this section is implemented or run)

Roadmap view: `docs/TRADING_ENGINE_ROADMAP.md` ("Agreed research plan"). This is the contract-level record.

**A.15.1 Approach.** Metric-first. **No composite score** and no composite-score tuning. Five initial families, each evaluated on its own:
1. directional residual % (call residual % − put residual %);
2. straddle residual % (call + put residual over call + put base);
3. futures rolling strict ratio;
4. relative option rolling strict ratio;
5. futures rolling return in bps (a **price-only benchmark**, not an order-flow metric).

**A.15.2 Pre-research verification (blocking).** Before any research run, verify and report for each family the exact source fields, formulas, units, denominators, basket composition and causal availability (the `AvailableAt` at which each value is first knowable); **report gaps rather than invent equivalents**; never interchange straddle, common and directional residuals. Inventory read from the adaptive-observer source at the CP4 baseline (to be re-verified by the research implementer):

| Family | Source found | Status / gap |
|---|---|---|
| Directional residual % | `AdaptiveOptionResidualBarRow.DirectionalResidualPct = CEResidualPct − PEResidualPct` (per completed adaptive bar) | Present; basket composition and per-side denominators to be confirmed in the verification step |
| Straddle residual % | Derived `100 × (CeResidual + PeResidual) / (CeBase + PeBase)` in `AdaptiveResidualProjection` | **Derived, not persisted**; `CommonResidualPct = (CE% + PE%) / 2` is a **different** quantity and must not stand in for it |
| Futures rolling strict ratio | `AdaptiveRollingStateRow.StrictDeltaRatioTotal` (10-bar rolling) | Present; window length is the existing structural 10 bars |
| Relative option rolling strict ratio | Option-band rows carry `ContractRolling` / `NotionalRolling` strict ratios **per CE/PE side** | **Gap:** no persisted "relative" (CE vs PE) rolling strict ratio field was found; its exact definition must be agreed before use, not improvised |
| Futures rolling return (bps) | `AdaptiveRollingStateRow.ReturnBps` (10-bar rolling) | Present; price-only benchmark |

**A.15.3 Comparison forms (per family).** (a) absolute level against zero; (b) current value minus the *prior* running average since 09:30; (c) current value minus the *prior* average of the last N completed bars, **N = 10** with **N = 15** a predefined sensitivity. The current observation is excluded from its own comparison average. Averages are equal weight per completed bar (labelled the *bar-based baseline*), require a valid warm-up, and require consistent contract/basket composition across the averaged bars (otherwise unavailable, never mixed).

**A.15.4 Hypotheses.** Directional residual: *continuation* and *reversal* tested separately (neither is assumed). Straddle residual: test movement **magnitude** and **direction** separately; a higher straddle residual is **not** assumed to mean buy CE. Volume per second and efficiency are recorded as context only. Strict flow first; enriched flow later as separate variants.

**A.15.5 Sequence.** *A — outcome study first:* fixed-contract CE/PE executable returns at 15, 30 and 60 minutes, MFE/MAE, quote coverage, breakdowns by session and DTE; horizons running beyond 15:10 are marked **unavailable, never silently shortened**; same-session / time-of-day / prior-movement controls; overlapping observations handled explicitly. *B — standalone trading:* baseline crossings, continuation and reversal variants, opposite-cross exit, compulsory liquidation; the **initial signal-isolation runs have TP1 and per-trade SL disabled** and that is recorded; SL, TP1 and extra entry conditions come later as separately named experiments; identical execution and contract-selection assumptions across every comparison.

**A.15.6 Trade frequency.** No quota and no cap. Report opportunities, attempts, fills, median and range of daily trades, zero-trade days, holding time and daily costs **per variant**; never sum independent variants into a feasible shared-account claim.

**A.15.7 Data discipline.** All 18 sessions are **development data**; a new session qualifies for forward validation only after the rules are frozen and the exposure history is checked (D-17); every tested variation is recorded, positive or negative.

## 0. How the evidence was gathered

- **V (verified by me)**: I opened the file/ran the query in this session.
- **A (audit pass)**: three read-only audit passes read the cited files in full and reported file:line; I spot-checked a sample (listed below) but did not re-read every claim. Treat A-items as "read by an auditor, to be re-confirmed by the Phase 2 implementer before relying on them".
- **U**: unclear/assumed; stated as such.

Spot-checks I performed (all matched the audit text): `AdaptiveObserverWorker.cs:32` `StableOrderingLag = 2 s` and use at `:159,:217,:309`; `PaperTradeSimulator.cs:16,25` ask+slippage / bid−slippage and `:20,29` brokerage; `Host/Program.cs:145,163,200` are the only `AddHostedService` calls; `LiveTradingEngine.cs:103-105` daily-loss formula over closed-trade P&L; `AdaptiveTickCleaner.cs:35-36` `AvailableAt` from `ReceivedAt`/`ExchangeTimestamp`; `OpeningVolumeProjectionV1.cs` 13 discovery dates; all data-coverage numbers in §11 come from my own read-only SQL.

## 1. Existing system — what exists, verified behaviour, reuse/extend/retire

### 1.1 Trading is not live today
Only `MarketDataIngestionWorker`, `LiveVolumeBarWriter`, `AdaptiveObserverWorker` are hosted in Host (V: `Host/Program.cs:145,163,200`). No paper executor is hosted (matches `ADAPTIVE_LIVE_ROADMAP.md` "Paper trading / orders: OUT OF V1"). The adaptive observer is watch-only.

### 1.2 Component inventory

| Component (path :: type) | What it does | Hosted? | Disposition | Ev. |
|---|---|---|---|---|
| `NiftySignal.Ingestion/FlatTrade/FlatTradeTickSource` + `FlatTradeFeedState` | WebSocket → `Tick` (top-5 depth when complete, 1-s `ft` exchange time, local `ReceivedAt`). Single receive loop, wire order, unbounded channel. | yes | **Reuse** (no change in Phase 2) | A |
| `Host/MarketDataIngestionWorker` (`RunTickLoopAsync`) | Single consumer: engine tick → dashboard → 200-tick/1-s DB batch. Skips (and loses) a tick if the per-tick body throws (`:347-356`). No unique key / idempotent insert (`:913`). | yes | **Extend** (loss path, tick sequence) — later phase, not Phase 2 | A |
| `Domain/Entities/Tick`, `Persistence/Configurations/TickConfiguration` | `ticks`: PK `Id`, indexes `(Token, ExchangeTimestamp)`, `(ReceivedAt)`; bid1..5/ask1..5 price+qty columns. No broker sequence number. | yes | **Reuse** | A; V (schema via `information_schema`/`pg_indexes` on `niftysignal_vm_copy`) |
| `AdaptiveObserver/AdaptiveIncrementalTickNormalizer`, `AdaptiveTickCleaner`, `Host/AdaptiveSourceTickReader` | Per-token dedupe; `AvailableAt = max(ReceivedAt, ExchangeTimestamp)`; read by `Id`, order by `(AvailableAt, Id)`. Shared by live, recovery, weak2 service. | yes | **Reuse as the canonical event-ordering definition** | V (cleaner), A |
| `AdaptiveObserver/AdaptiveObserverEngine.ProcessAvailabilityGroup` | Pure per-availability-group engine: spot, then options (by Id), then future last; throws on a late group (`:126-127`). Builds completed exact-volume bars, rolling state, weak labels, band flows, residuals, sidecars. No wall clock inside the engine. | yes (via worker) | **Reuse as the feature/signal producer** (no calc change) | A |
| `AdaptiveObserver/ExactAdaptiveFuturesBarBuilder`, `AdaptiveRollingStateTracker`, `AdaptiveWeak2Classifier`, `AdaptiveReadinessPolicy`, `OptionResidualModel`, `FuturesBasis`, `FuturesMicrostructure`, `OptionsSupplemental` | Pure calculators; frozen/parity-validated | yes | **Reuse unchanged** | A |
| `Host/AdaptiveObserverWorker` | 250 ms poll; releases groups with `AvailableAt <= UtcNow − 2 s` (V `:217`); session start/close by wall clock (`:87`, close 15:35). | yes | **Extend**: wall-clock decisions must move behind an injected clock/adapter; keep worker for observation | V, A |
| `Host/AdaptiveStateRecoveryService`, `AdaptiveObserverPersistence.PersistOrVerifyAsync` | Recovery = replay whole session's raw ticks through a fresh engine; persisted rows are *verified* field-by-field (mismatch throws). Resume key = raw `Id` cursor + `(LastProcessedAvailableAt, LastProcessedTickId)`. | yes | **Reuse the pattern** ("replay + verify, never overwrite") for the trading engine | A |
| `Host/AdaptiveSessionCoordinator` | 09:30 session freeze (opening volume → bar volume, strong threshold, ATM anchor), median fallback for late start. Immutable session row. | yes | **Reuse** | A |
| `Host/AdaptiveWeak2ObservationService` | Watch-only. Picks reversal-side weekly option whose *first post-trigger quote* ask ∈ [₹100,₹150] closest to ₹125 (constants hard-coded `:16-18`), entry=ask, exit=bid at trigger+5 bars, MFE/MAE. **Zero latency, no quote-age cap, no spread/size check, no costs, selection uses quotes of every candidate strike.** | yes | **Retire as an execution model; keep as a diagnostic.** Replace with engine-executed paper fills in Phase 3. | A |
| `Execution/PaperTradeSimulator` | Entry = ask + ticks×slippage; exit = max(0, bid − ticks×slippage); brokerage ₹20/order. | via unhosted engines/backtest | **Extend** (needs missing-quote policy, quote age, latency, partials, full cost schedule, per-fill cost fields) | V |
| `Execution/StrikeSelector` + `LiveFeatureEngine.BuildStrikeCandidates` | Nearest expiry; filters mid∈[100,150], bid/ask>0, spread≤2 % mid, OI≥100 000, IV non-null; ranks preferred-delta flag (hard-coded |Δ|∈[0.30,0.60]) → OI → volume. Candidate mid falls back to LTP when depth is missing; **no quote age**. | via unhosted engines | **Extend/replace** by the contract in §6; delta band is an unapproved constant | A |
| `Rules/EntryRuleEvaluator`, `ExitRuleEvaluator`, `ScoreSustainTracker` | Legacy composite rules: SL −10 %, partial +15 %, score flip/decay, time stop 120 min, SquareOff 15:15, entry window 09:30–15:00 (14:00 on expiry day), `MaxTradesPerDay` 30. | no | **Do not reuse as defaults** (discretionary SL/TP conflict with "no risk mgmt before edge"; legacy composite is retired). Keep tests as reference. | A |
| `Host/LiveTradingEngine` | Legacy composite entry/exit with DB-backed risk gates (kill switch, daily loss, consecutive loss, F48 capital, IV-rank, data-gap). Registered but **never called live**; used by `Backtest/BacktestRunner`. | registered, not called | **Retire from live; retain as the regression oracle for risk-gate semantics** | A |
| `Host/CoreScoreHysteresisTradingEngine`, `CoreScoreCrossoverTradingEngine` | Retired core-score engines; ~150 lines duplicated risk-gate code each; crossover can leave state desynced if the exit quote is missing (`:289-297`). | registered, not called | **Retire** | A |
| `Host/LiveOptionsScoreEngine`, `LiveFuturesCrossoverEngine`, `VolumeBarData/LivePaperTradeExecutor` | Dormant volume-bar paper path. LTP-only fills at decision time `UtcNow`, no costs, exits only on bar close; unique index `(AsOfDate, BarVolumeThreshold, Strategy, EntryBarIndex)` is a good idempotency pattern. | not registered | **Retire**; copy only the idempotency idea | A |
| `Backtest/BacktestRunner` | Drives real `LiveFeatureEngine` + `LiveTradingEngine` with historical ticks (3 s sample / 15 s cadence). Legacy engine only; fresh engine per day (no IV-rank history). | replay | **Retire after Phase 2** (superseded) | A |
| `MetricTrials/CoreScoreOptionSimulator`, `FutureDirectSimulator`; `VolumeBarData/TradeSimulator` etc. | Research simulators; mark fills, no slippage/spread, **exit falls back to entry price when no quote**, duplicated copies of rule logic. | replay | **Keep as research history; results are not live-equivalent** | A |
| `Backtest/PerformanceReportBuilder` | Per-trade closed-trade metrics; profit factor returns 0 for all-win; trades/day uses calendar span; no per-day/exit-reason/gross-vs-net/unrealised. | replay | **Extend** | A |
| `Domain` `PaperTrade`, `KillSwitchState`; `Persistence` `paper_trades` | One row = whole trade lifecycle; partial leg overwrites fields; no orders/fills tables; indexes on `EntryTime`, `ExitTime` only; **no unique/idempotency key, no `StrategyId` index**. Kill switch is a DB row toggled by Dashboard only; **nothing auto-trips it**; `Ruleset.KillSwitch` config is read by nothing. | partly | **Replace** with new trading schema (§8) | A |
| `VolumeBarData/TransactionCostCalculator` | Only code with STT/GST (STT 0.0625 % of exit premium, GST 18 % of brokerage per tests). Rates not independently verified. | research | **Do not reuse its rates** (its STT 0.0625 % is stale; 0.15 % applies to all 18 sessions, §A.9); at most reuse its structure | A |

### 1.3 Wall-clock and time handling (V/A)
- No injected clock in ingestion/trading/adaptive paths; `TimeProvider.System` is registered (`Program.cs:84`) but only notifiers use it (A).
- Decision-relevant wall-clock uses: `FlatTradeTickSource.cs:218` (`ReceivedAt = UtcNow`, which feeds `AvailableAt`); `AdaptiveObserverWorker` `nowUtc` (`:81,:111,:217,:309`); legacy cadence `MarketDataIngestionWorker.cs:401` (`ComputeCadence(now)` every 15 s) and `:536` (`Sample(UtcNow)` every 3 s) (A).
- IST offset is re-declared in several files; `Domain/IstTime` exists but is not used everywhere (A). Market hours hard-coded; holidays not modelled (A).
- `FlatTradeFeedState.cs:97`: if a message never carried `ft`, `ExchangeTimestamp` falls back to `receivedAt`; `ft` is 1-second resolution. In the recorded data the first `ExchangeTimestamp` of 2026-09-04 is `00:00:00` and others start ~06:07 IST (V: my query of `ticks`), so non-trading-hours/zero-time rows exist and must be filtered by the session window.

## 2. Backtest ↔ live gaps, ranked by severity

| # | Gap | Severity | Evidence |
|---|---|---|---|
| G1 | **There is no shared strategy/risk/execution implementation.** The adaptive path (the only live-validated signal source) has no trading logic; the trading engines consume a different (legacy) score, are unhosted, and simulators re-implement rules. Identical inputs do not yield identical decisions between MetricTrials/BacktestRunner/live engines. | Critical | §1.1, §1.2 |
| G2 | **Optimistic research fills.** Weak2/H5 observation: zero latency, no quote-age/spread/size limits, strike chosen from the first post-trigger quote of every candidate strike (requires information that arrives *after* the decision), no costs. Any rule derived from it overstates executable edge. | Critical | `AdaptiveWeak2ObservationService` (A) |
| G3 | **Wall-clock coupling.** Live adaptive processing is delayed 2 s by wall clock; legacy cadence and sampling run on wall-clock timers; session start/close by `UtcNow`. Replay uses event time. Same events can yield different timing live vs. replay. | High | V `:32,:217` |
| G4 | **Exits are cadence/bar driven.** Legacy: evaluated every 15 s, skipped when `CompositeScore == null` or when the held token has never ticked (SquareOff skipped). Dormant volume-bar path: 15:15 cutoff fires only on a bar close, else at 15:35 flush. A quiet market can delay a required exit. | High | A (`LiveTradingEngine.cs:39-42,234`; `LiveFuturesCrossoverEngine.cs:60-64`) |
| G5 | **LTP/entry-price substitution.** Legacy mark = bid ?? LTP; `MidPrice` falls back to LTP; `LivePaperTradeExecutor` is LTP-only; `CoreScoreOptionSimulator` exits at entry price when no quote. | High | A |
| G6 | **Incomplete execution & cost model.** No latency, partial fills, rejections, expiry, quote age (legacy `InstrumentState` has no timestamp). Only brokerage ₹20/order + 2-tick slippage in Host; no STT/exchange/GST/stamp (only in a research calculator). | High | V (`PaperTradeSimulator`), A |
| G7 | **Risk gaps.** Daily loss is realised-only and gates new entries only (V `LiveTradingEngine.cs:103-105`); kill switch never auto-trips and `Ruleset.KillSwitch` config is dead; no pending-order exposure reservation; no DB uniqueness for open positions; F48 capital sum over-reserves after a partial; legacy `MaxTradesPerDay` default 30 (project target is 5–10/day; the 5-trade cap was withdrawn in Amendment 1). | High | A |
| G8 | **Ingestion integrity.** No broker sequence; per-tick exception path drops a tick silently; no unique tick key; no inbound watchdog (stalled-but-connected socket undetected); `DataGap` end time is auth-ack not first tick; adaptive path does not consume `DataGap`; several readers order by `ExchangeTimestamp` only (not `(AvailableAt, Id)`); `BacktestTickSource` orders by `(ExchangeTimestamp, Id)` – a different order from the adaptive path. | Medium-High | A |
| G9 | **Recovery model.** Replay-whole-session only (no checkpoint): measured 7.6–15.5 s and ~1.9 GB working set for a full day (`ADAPTIVE_LIVE_PERFORMANCE_2026-10-08.md`, existing doc); multi-table persistence is not atomic (relies on recovery to back-fill); a late row with lower `Id` than the cursor is either never seen or loops the degrade/rebuild cycle. | Medium | A |
| G10 | **Constants & thresholds not derived.** ₹100–150 ask band, ₹125 target, delta 0.30–0.60, OI ≥ 100 000, 2 % spread, opening-volume regression are fixed numbers; conflicts with BACKTEST/CLAUDE "dynamic or DTE-based" rule if reused as decision thresholds. | Medium | A |
| G11 | **Repo hygiene.** `dotnet build` of the solution at the baseline has 3 warnings (`CLAUDE.md` requires 0); `ADAPTIVE_LIVE_ROADMAP.md:39` says "eight tables" but `AdaptiveObserverDbContext` has 17 (A). | Low | §16 |

## 3. Proposed architecture

Principle: **one deterministic core that is pure** (no I/O, no wall clock, no randomness, no static mutable state), driven by an ordered stream of events, emitting an ordered stream of outputs. Adapters feed events in and carry outputs out. The existing adaptive engine stays the feature/signal producer and is *wrapped*, not modified.

```
                     ┌───────────────────────── NiftySignal.Trading.Core (pure) ───────────────────────┐
 Event source  ───►  │ EngineEvent stream ─► Sequencer ─► SignalAdapter(IStrategy) ─► RiskEngine ─►     │
 (Historical or      │                         │ timers          │ decisions              │ intents     │
  LivePaper)         │                         ▼                 ▼                        ▼             │
                     │                    DeadlineQueue     DecisionAudit           OrderManager        │
                     │                                                                   │              │
                     │                          ExecutionSimulator (IExecutionModel) ◄───┘              │
                     │                                   │ execution updates                            │
                     │                                   ▼                                              │
                     │                          PositionBook + RiskCounters ─► EngineOutput stream      │
                     └──────────────────────────────────────────────────────────────────────────────────┘
        outputs ─► Persistence adapter (EF, replay-and-verify)     future: IExecutionGateway → broker
```

### 3.1 Proposed projects (names are proposals)

| Project | Responsibility | Dependencies |
|---|---|---|
| `NiftySignal.Trading.Core` | Event/output types, `EngineClock`/`DeadlineQueue`, `TradingEngine` (single entry point `Handle(EngineEvent) → IReadOnlyList<EngineOutput>`), `IStrategy`, `RiskEngine`, `OrderManager`, `PositionBook`, `IExecutionModel` + `SimulatedExecutionModel`, `ContractSelector`, `CostModel`, `DecisionAudit`, versioned `TradingConfig`. **No** EF, no HTTP, no `DateTime.*Now`, no `Random`. Enforce with an analyzer/banned-API test. | `Domain` only |
| `NiftySignal.Trading.Signals` | `IStrategy` implementations + the Phase 2 *plumbing strategy*; later the standalone signal families. Consumes `BarCompletedEvent` etc. | `Trading.Core`, `AdaptiveObserver` (read-only types) |
| `NiftySignal.Trading.Adapters.Historical` | Reads recorded ticks via the **same** `AdaptiveSourceTickReader` + `AdaptiveIncrementalTickNormalizer` semantics, runs `AdaptiveObserverEngine`, converts its outputs to `EngineEvent`s, drives `TradingEngine`. Virtual clock = event time. | `Trading.Core`, `AdaptiveObserver`, `Persistence` |
| `NiftySignal.Trading.Adapters.LivePaper` | Same event conversion over the live-polled stream; supplies *journaled* heartbeats; hosts the engine in a `BackgroundService`; simulated execution identical to historical. | `Trading.Core`, `AdaptiveObserver`, `Persistence`; wired in `Host` only after an edge is established |
| `NiftySignal.Trading.Data` | EF Core context/entities/migrations for the trading database (§8). | `Trading.Core` types |
| *(future)* `NiftySignal.Trading.Adapters.Broker` | `IExecutionGateway` implementation. Not Phase 2. | |

A single shared **event conversion** component (`AdaptiveEventTranslator`) is used by *both* Historical and LivePaper adapters, so feature and bar events cannot diverge between them. The only legitimate differences between the two adapters are: tick reader cursor (full vs. incremental), where the heartbeat comes from, and persistence flush cadence.

Rejected alternative: embedding trading logic inside `AdaptiveObserverEngine`/`AdaptiveObserverWorker`. It would put order logic into a parity-frozen calculation path and break the "watch-only, calculations unchanged" guarantee.

## 4. Events, ordering and the injected clock

### 4.1 Four times (never conflated)
| Name | Meaning | Source |
|---|---|---|
| `EventTime` | When the exchange says it happened | `Tick.ExchangeTimestamp` (1-s resolution) |
| `AvailableAt` | When the system could first know it: `max(ReceivedAt, ExchangeTimestamp)` | existing normalizer definition (V) |
| `DecisionTime` | `AvailableAt` of the event that triggered the decision (or the timer deadline); **never** wall-clock processing time | engine |
| `ExecutionTime` | `DecisionTime + latency` (§9); when the order reaches the simulated exchange | execution model |
Wall-clock processing time (`ProcessedAtWall`) is recorded for diagnostics/latency reconciliation only; it is never an input to a decision.

### 4.2 Event types (all immutable, carry `Seq`, `AvailableAt`)
`QuoteEvent` (per instrument top-of-book + depth-1 qty + LTP/volume/OI, `SourceTickId`), `BarCompletedEvent` (adaptive futures bar + rolling state + residual/band features, `BarSeq`, `EndAvailableAt`), `ExecutionUpdateEvent` (ack/fill/partial/reject/expire/cancel from the execution model or gateway), `TimerEvent` (deadline fired), `SessionEvent` (SessionStart, WarmupComplete, EntryCutoff, CompulsoryCloseStart, SessionEnd), `FeedHealthEvent` (stale/disconnected/recovered, derived from `DataGap` and quote age), `ConfigChangeEvent` (rejected mid-session; see §7).

### 4.3 Total order
The engine's input order is **`(AvailableAt, GroupRank, SourceTickId)`**, where an *availability group* is all ticks with equal `AvailableAt` (as in the existing engine, V/A) and inside a group non-future instruments precede the future, each by `Id` (existing `AdaptiveObserverEngine` rule, A `:131-133`). The engine reuses these rules rather than redefining them. Consequences, which are the contract:
- Equal timestamps → one group, processed atomically; the `BarCompletedEvent` produced by a group is emitted *after* the group's quote events and carries `EndAvailableAt == group AvailableAt`.
- A group with `AvailableAt <=` last processed → **rejected as late** (engine raises `LateEventFault`, enters `Degraded`, §6.5). It is never merged retroactively.
- Duplicate ticks (same payload as the previous raw tick of that token) are dropped by the existing per-token normalizer *before* the engine; a re-delivered event with an already-seen `Seq` is ignored (idempotent).
- Out-of-order across the live `Id` cursor is detected by the same mechanism as today (A: `AdaptiveObserverWorker` degrade-and-rebuild); the trading engine must treat that as a fault, never silently reorder.

### 4.4 Injected clock and timers
- `ITradingClock` exposes only `Now` = *engine time* = `AvailableAt` of the last processed event or fired deadline. Core has no other time source.
- Timers are **deadline-stamped**: `DeadlineQueue` holds `(deadline, kind, id)`. Before handling any event with `AvailableAt = T`, the engine fires, in `(deadline, id)` order, every deadline `<= T` (a deadline equal to `T` also fires **before** the event; tie order, chaining and gap handling in §A.2), with `DecisionTime = deadline` (not `T`). The outcome therefore does not depend on *when* it is processed, only on the event stream and deadlines.
- Live heartbeats: the LivePaper adapter injects `HeartbeatEvent(AvailableAt = wallNow − stableLag)` once per poll (even when no ticks arrive). A heartbeat can only cause due deadlines to be processed *earlier in wall time*; because decisions use the deadline as `DecisionTime`, replay without heartbeats produces identical outputs. Heartbeats are journaled (`engine_inputs`) for audit but are not required for replay equality. This is how a quiet market cannot block a required exit: session-close and risk deadlines fire on heartbeats in live, and on the next event or an explicit end-of-stream `Flush(sessionEnd)` in replay.
- The stable-ordering lag (existing 2 s) is a property of the adapter's *watermark* (`watermark = wallNow − stableLag`); the engine sees only watermark-ordered events. It is configuration (`StableOrderingLagSeconds`), versioned, and the historical adapter emulates it by processing groups strictly in `AvailableAt` order (equal to the live outcome whenever no late tick is injected).
- Banned in Core: `DateTime(Offset).Now/UtcNow`, `Stopwatch`, `TimeProvider.System`, `Task.Delay`, `Random`, `Guid.NewGuid` (ids are deterministic hashes, §8.3). Enforced by a test scanning the assembly's IL/source.

### 4.5 When a completed-bar signal becomes available
The bar completes inside the availability group whose tick fills the target volume (A: `ExactAdaptiveFuturesBarBuilder.Add :80-83`). The signal's `AvailableAt = bar.EndAvailableAtUtc`. Only that bar's closing-group data is in the feature vector (existing no-look-ahead: option quotes with `AvailableAt <= bar end`, 5 s freshness, A). Earliest allowed order: `ExecutionTime = EndAvailableAt + latency`. The strategy may read **only** `BarCompletedEvent` fields and engine-held quote state with `AvailableAt <= DecisionTime`.

## 5. Strategy → order → position state machines

### 5.1 Interfaces
```
interface IStrategy {                       // pure; same instance type in all runs
  string StrategyId; string Version;
  StrategyDecision OnBar(BarCompletedEvent bar, EngineView view);   // signal families
  StrategyDecision OnQuote(QuoteEvent q, EngineView view);          // optional, e.g. exit signals
  StrategyDecision OnTimer(TimerEvent t, EngineView view);
}
record StrategyDecision(DecisionKind Kind /*None|Enter|Exit*/, Side, ContractRequest? Contract, ReasonCodes Reasons, IReadOnlyDictionary<string,decimal> Inputs);
```
`EngineView` is read-only: positions, pending orders, risk counters, latest quotes with ages. Strategies **cannot** place orders; they emit decisions. Risk and execution are separate stages and cannot be bypassed.

**Amendment 1 (§A.4):** `IStrategy` above is the *base strategy* (validity + contract); the extra entry rule is a separate pure `IEntryCondition` (declares its input token; evaluated on eligible quote events of that token). Exits (SL, TP1, final, session liquidation) are `IExitRule`s evaluated on quotes and timers, never on bars only (§A.6).

### 5.2 Decision pipeline (every decision audited, accepted or rejected)
`StrategyDecision` → `ContractSelector` (entries only; §6) → `RiskEngine.PreTrade` (§10) → `OrderIntent` → `OrderManager` → `IExecutionModel` → `ExecutionUpdate` → `PositionBook`/`RiskCounters` update. Each hop writes a `DecisionAudit` row with stage, outcome (`Accepted`/`Rejected`/`Deferred`), reason code(s) and the exact input values (quote ages, spreads, counters) that determined the outcome.

### 5.3 Order states (append-only `order_events`)
`Created → Submitted → {Working → PartiallyFilled}* → {Filled | Cancelled | Expired | Rejected}`; `CancelRequested` is an intermediate. Illegal transitions throw (fault). An order's identity is deterministic (§8.3). Order types in the paper model: **marketable limit** only (limit = opposite-side top-of-book at ExecutionTime ± configured collar); no market orders, so a missing/wide book cannot create unbounded fills.

### 5.4 Position states
`Flat → EntryPending → (Open | PartiallyOpen) → ExitPending → Flat`; plus `ExitFailed` (see §9.5) which is **not** `Flat`. One logical position at a time **per variant** (Amendment 1 §A.3): the state machine admits no second `EntryPending` unless `Flat`. A partially filled entry is `PartiallyOpen`: the unfilled remainder is cancelled at the entry-expiry deadline (default policy: cancel remainder, manage the filled part as the position; alternative: all-or-none, §14 D-5). An exit may itself be partial: the position stays `ExitPending` with remaining quantity until flat.

## 6. Contract selection, session phases, faults

### 6.1 Fixed contract at entry, preserved through exit
`ContractSelector` runs once, at `DecisionTime`, using only quotes with `AvailableAt <= DecisionTime` and age ≤ `MaxQuoteAgeMs`. Output `SelectedContract {Token, Strike, OptionType, Expiry, LotSize, SelectionPolicyVersion, CandidateSnapshot[]}` is written to the `DecisionAudit` and copied onto the `OrderIntent` and `Position`. The exit intent uses `Position.Token` and the engine **rejects** any exit for a different token. No roll, no re-selection. Candidate snapshot (every candidate with its quote, age, spread, reason for exclusion) is stored so selection can be audited.
The entry premium window is now confirmed (₹100–₹150 inclusive, §A.12); preferred delta and OI/spread minimums remain **not** approved parameters: the contract fixes the *mechanism* (pure function of quotes at DecisionTime, versioned policy, candidate snapshot). Values must be dynamic/DTE-derived per `BACKTEST_RULES.md` or explicitly approved (§14 D-4). The existing Weak2 service's "first post-trigger quote of every strike" is explicitly not an allowed selection input.

### 6.2 Rolling ATM baskets
Rolling ATM±k baskets (band flows, residual anchors) are **diagnostic features only**. They are never a held position, never an `OrderIntent` target. Only a fixed single contract can be held.

### 6.3 Session phases (engine-time, from versioned `SessionConfig`; Amendment 1 / §A.1)
| Phase | Rule |
|---|---|
| Warm-up / data collection | Events are processed from `WarmupStart` (features advance); entry decisions are `Rejected(Warmup)` until `AdaptiveReadinessPolicy` (10 consecutive valid bars, A) **and** the adaptive session is frozen (09:30 or the persisted late-start fallback, A) |
| Trading | `09:30:00.000 <= DecisionTime < 15:00:00.000` Asia/Kolkata (inclusive/exclusive as stated) |
| Entry close (15:00:00) | Waiting/triggered opportunities become `Expired(EntryClose)`; unfilled entry quantity cancelled; filled quantity stays a position; exits, stops, targets unaffected |
| Liquidation start (15:05:00, PLACEHOLDER) | Forced `SessionLiquidation` exits with retry/escalation; target is every position flat by **15:10:00** |
| Session end (adaptive hard close, 15:35 today) | Anything not flat is reported `UnresolvedAtClose` and is **never** marked closed without fills |
All boundaries are `SessionBoundary` deadlines (§A.2) created at session start, so they fire in a quiet market and are not tied to completed bars. The expiry-day 14:00 cutoff is dropped.

### 6.4 Restart recovery
Recovery is *replay-and-verify*, the pattern already proven for the adaptive observer (A: `AdaptiveStateRecoveryService`):
1. Load the immutable `trading_session` row (config hash, engine version, adaptive session id). If config hash/engine version differ from the running binary → **refuse to trade** (read-only/flatten-only mode, alert).
2. Rebuild adaptive state exactly as today; replay all `EngineEvent`s from session start through the watermark into a fresh `TradingEngine` with persistence in *verify* mode: each output (decision, order, order event, fill, position, counter) is compared with the persisted row by deterministic id; mismatch → fault.
3. Outputs that exist persisted but are not re-derived, or derived but not persisted *and older than the watermark*, are mismatches. Outputs not yet persisted at crash time are inserted (idempotent unique keys).
4. Simulated execution is deterministic, so a pending paper order is re-derived, not re-sent. With a future broker gateway the client order id = `OrderId` and the gateway must `QueryOrder` first; "unknown" ⇒ halt, not resend.
5. Open position / pending order after recovery is exactly the replayed state. No second entry can be created because the decision id (§8.3) already exists.
Replay memory/time precedent: 7.6–15.5 s and up to ≈1.9 GB working set per full session (existing measurement). A checkpoint (§8.2) is an optimisation to add only if measured need arises; correctness must not depend on it.

### 6.5 Fault policy
`Degraded` (late event, parity mismatch, persistence failure, stale data beyond `HaltStaleSeconds`): **entries disabled**; exit management stays active using the best executable quote available, and compulsory close timers still run. Faults are persisted and alerted. Leaving `Degraded` requires successful recovery verification; the engine never self-clears a parity mismatch.

## 7. Versioned configuration and the audit trail

- `TradingConfig` is a single immutable record: `{ConfigVersion, Session, Risk, Execution, Costs, ContractSelection, StrategyParams, StableOrderingLag, ...}`. Its canonical JSON → SHA-256 `ConfigHash`. Stored once in `trading_config_versions`; every session, decision, order and position carries `ConfigHash` + `EngineVersion` (assembly informational version incl. git SHA).
- Mid-session config change: forbidden. A `ConfigChangeEvent` is rejected and logged; changes apply from the next session. (The legacy `ValidatedOptionsMonitor` hot-reload is deliberately not used for engine parameters.)
- Every strategy `Enter`/`Exit` decision, accepted **or rejected**, produces a `decision_audit` row (§8) with: `DecisionId`, stage, outcome, ordered reason codes, `DecisionTime`, trigger event ref (`BarSeq`/`SourceTickId`), inputs snapshot (counters, quote ages, spreads, exposure computation), `ConfigHash`. A human must be able to answer "why did/didn't we trade at 11:42:07?" from this table alone.

## 8. Persistence (new database `niftysignal_trading`, separate like `niftysignal_adaptive_observer`)

### 8.1 Entities, keys, indexes (proposal)
All tables: `bigint Id` surrogate PK; natural unique keys below carry idempotency. Money `numeric(18,4)`; UTC `timestamptz`; `RunMode` ∈ {`Historical`,`LivePaper`}; `RunId` identifies one execution of a session (so historical and live outputs can coexist and be diffed).

| Table | Unique key | Other indexes / notes |
|---|---|---|
| `trading_config_versions` | `ConfigHash` | canonical JSON, created-at |
| `variants` | `(RunId, VariantId)` | variant spec JSON, `VariantConfigHash`, risk profile, sizing mode, entry-condition spec |
| `virtual_accounts` | `(RunId, AccountId)` | one per variant by default; ledger totals; `AccountPolicy` ref |
| `opportunities` | `(RunId, VariantId, OpportunityId)` | state, contract token (fixed), episode id, attempt count, expiry reason |
| `session_ledger` | `(SessionDate, Purpose, ExperimentId)` | examination history for development/forward eligibility (§A.10) |
| `trading_sessions` | `(TradeDate, RunMode, RunId)` | FK adaptive session id (logical, other DB), `ConfigHash`, `EngineVersion`, status, started/ended |
| `engine_checkpoints` *(optional, later)* | `(SessionId, Seq)` | source cursor `(AvailableAt, TickId)`, state hash; never required for correctness |
| `decision_audit` | `(SessionId, DecisionId, Stage)` | `(SessionId, DecisionTime)`, `(SessionId, Outcome)`; reason codes array; inputs JSONB |
| `orders` | `(SessionId, OrderId)` | `(SessionId, State)`; DecisionId FK; side, qty, limit, expiry, contract token |
| `order_events` (append-only) | `(OrderId, EventSeq)` | state transition, qty, price, `ExecutionTime`, quote ref (`SourceTickId`, age, bid, ask, sizes) |
| `fills` | `(OrderId, FillSeq)` | qty, price, per-fill cost breakdown (brokerage, STT, exchange, GST, stamp, slippage), quote ref |
| `positions` | `(RunId, PositionId)` **plus partial unique index `(RunId, AccountId, VariantId, SlotNo) WHERE State <> 'Flat'`** (V1: `SlotNo = 0`, so one non-flat position per variant; many variants may be open at once; **no database-wide single-position constraint**) | `VariantId`, entry/exit decision ids, contract, qty, avg prices, `Tp1State`/`Tp1TargetQty`, realised/unrealised, state |
| `position_events` (append-only) | `(PositionId, EventSeq)` | transitions |
| `risk_counters` | `(RunId, VariantId, Seq)` | trades-today (recorded, not capped in ResearchV1), reserved exposure, realised net, unrealised liquidation value, daily-loss state; snapshot after each state-changing output |
| `engine_faults` | `(SessionId, FaultSeq)` | kind, details |
| `engine_inputs` (optional) | `(SessionId, Seq)` | journaled heartbeats / config-change attempts (raw ticks stay in `ticks`) |

### 8.2 Checkpoints
Not required. If added: a checkpoint is a *cache* (state blob + `StateHash` + source cursor) and recovery must still be able to verify against a full replay in a test.

### 8.3 Deterministic ids / idempotency
`DecisionId = H(VariantConfigHash, VariantId, StrategyId, TradeDate, TriggerKind, TriggerSeq(BarSeq|quote id|timerId), Ordinal)` (CP2: the variant hash, not the whole-config hash, so adding or removing another variant never re-identifies this one's decisions); `OrderId = H(DecisionId, Leg, Attempt)`; `FillSeq` increments per order from the execution model; `PositionId = H(SessionId, EntryDecisionId)`. No GUIDs, no identity values in business keys. Inserts are `INSERT … ON CONFLICT DO NOTHING` + verify equality (existing `PersistOrVerifyAsync` pattern, A). Multi-row outputs of one engine step are written in **one transaction** (fixing the non-atomic multi-table weakness, G9).

## 9. Execution assumptions

### 9.1 Rules (apply to historical and live-paper identically)
1. **No retrospective fills.** An order created at `DecisionTime` cannot fill at any quote with `AvailableAt <= DecisionTime` unless that quote is also the latest at `ExecutionTime` (it is evaluated at `ExecutionTime`, not "at the price that generated the signal"). Rejecting a stale quote is not "using" it.
2. **Buy against ask, sell against bid.** Entry fills at the ask side, exit at the bid side, level-1 prices/sizes only for Phase 2 (the `ticks` table stores 5 levels but the adaptive projection keeps level 1; level-2+ walking is a later extension, flagged).
3. **Executable quote at `ExecutionTime`** = the latest quote of that token with `AvailableAt <= ExecutionTime`, two-sided (`Bid>0`, `Ask>0`, `Ask>=Bid`), `ExecutionTime − AvailableAt <= MaxQuoteAgeMs`. If none: the order stays `Working` until `OrderExpiryMs`; then `Expired` (entry) or retried/escalated (exit, §9.5).
4. **Never substitute LTP, last mid, or the entry price** for a missing executable quote — in selection, marking, fills, stops, or liquidation. If an unrealised value cannot be marked, it is recorded as `Unmarkable` and the risk engine uses the conservative rule in §10.4.
5. **Latency** `ExecutionLatencyMs` (PLACEHOLDER): applied from `DecisionTime`. Phase 3 calibrates it from recorded data (`ReceivedAt − ExchangeTimestamp` distribution and measured live decision lag). It must be ≥ the live adapter's measured processing lag or live paper is *more optimistic than real*; the live adapter reports `WallLagMs = ProcessedAtWall − DecisionTimeWall` and a counter of orders where it exceeded the modelled latency.
6. **Spread / liquidity checks (pre-trade, on the quote at `ExecutionTime`)**: `Spread <= MaxSpread` (absolute ₹ and % of mid, both PLACEHOLDER), `AskQty >= requested qty` (or partial per §9.2), bid ≥ `MinBid`.
7. **Partial fills**: fill quantity at each quote = `min(remaining, displayed level-1 qty × FillFractionOfDisplayed)`; the remainder rests until expiry; subsequent fills use later quotes. Quantities are lot multiples. (`FillFractionOfDisplayed` PLACEHOLDER; D-5.)
8. **Order expiry** `OrderExpiryMs` (entry) and `ExitRetryMs` (exit) PLACEHOLDER; entry never outlives the entry cutoff.
9. **Costs** from a versioned, dated `CostSchedule` (§A.9, PROVISIONAL): brokerage per order, STT (0.15 % of sell-side premium for all 18 sessions), exchange transaction charge, SEBI fee, stamp duty (buy side), GST, plus slippage in ticks. Computed and stored per fill; reports show results with and without the provisional components. Never the stale 0.0625 % STT.
10. Fill price per fill = quote price ± `SlippageTicks × tickSize` (existing mechanism, V; slippage default PLACEHOLDER).

### 9.2 Pending entries / partial positions / cancellation / rejected exits
- Pending entry holds a risk reservation (§10.3) until `Filled/Cancelled/Expired/Rejected`; reservation is released by the **actual** unfilled remainder.
- Partial entry at expiry: cancel remainder; position quantity = filled quantity; strategy exit rules apply to it.
- A signal to exit while the entry is still `Working`: cancel the entry first; if any part filled, exit that part.
- Exit rejected/expired: the position stays `ExitPending`→`ExitFailed`; the engine retries every `ExitRetryMs` with the then-executable bid, escalates alerts at N failures, keeps `Degraded`. **A position is never closed in the books without a fill** and P&L is never "assumed" at session end; an unfilled position at `SessionEnd` is reported as `UnresolvedAtClose` with last executable bid/time and excluded from realised P&L but included in the daily summary as a risk event.

### 9.3 Stale data, disconnect
- `QuoteAge > MaxQuoteAgeMs` for the held token ⇒ no fills and no marks from it; position risk uses §10.4.
- Feed disconnect (`DataGap` open, or no events for `HaltStaleSeconds` – inbound watchdog does not exist today, G8, so the engine derives staleness from event time vs. heartbeat time): entries disabled; exits attempt against the last *fresh* executable quote only; compulsory-close timers keep running; on recovery, normal operation resumes only after a verified `FeedHealthEvent(Recovered)` and a fresh two-sided quote.

### 9.4 What must match exactly vs. what needs reconciliation
| Output | Historical vs. live-paper path on identical recorded events | Notes |
|---|---|---|
| Features, bars, rolling state, signals | **Exactly equal** (field-equal; numerics within the existing declared tolerances) | already proven for the adaptive observer |
| Decisions, rejects, reason codes, order intents, order ids | **Exactly equal** | |
| Simulated fills, positions, counters, P&L | **Exactly equal** (same `IExecutionModel`, same quote stream) | |
| Persisted `DecisionTime`/`ExecutionTime` | **Exactly equal** (event-time) | |
| `ProcessedAtWall`, wall lag, poll cadence | Not comparable by design | reported only |
| Live paper vs. a *real broker* (future) | **Reconcile**, don't expect equality: latency, queue position, partial-fill behaviour, rejections, exchange charges, quote-snapshot vs. true book | per-order slippage-vs-model report; drift alarms |
| Live paper vs. historical **when the live feed dropped/reordered ticks** | Reconcile: ticks lost on the live path (G8) or arrival-order differences change `AvailableAt` | compare persisted ticks used by the historical run vs. the live run's consumed ticks (tick-id sets + hash) |

## 10. Risk controls (revised by Amendment 1 — R5)

The earlier 5-trade cap, ₹50k capital, ₹5,000 daily-loss, daily-loss force-exit and daily-loss kill-switch proposals are **withdrawn**. See §A.7 for the profile table; this section keeps the definitions that the profiles use.

### 10.1 Configuration (`TradingConfig.Variants[*].Risk`; **no approved monetary values exist**)
`RiskProfile` (`ResearchV1` | `RealOrder`), `DailySL` (₹, nullable), `DailySlEnforcement` (`None` default | `BlockEntries` | `BlockEntriesAndLiquidate`), `TradeSLPct` (exit rule, §A.6), `MaxTradesPerDay` (nullable; **null in ResearchV1**), `MaxPositionsPerVariant` (1 in V1), `AccountCapital` (nullable; null = not enforced), `MaxPremiumExposure` (nullable; not enforced unless set in an enforcing profile), `KillSwitchSource`. Session times and liquidation settings: §A.1.

### 10.2 Always-on rules (every profile)
- Quantity/lot validation, no silent quantity alteration (§A.5).
- Duplicate-order and duplicate-entry prevention: deterministic ids; one outstanding order per quantity; capacity counts reservations (§10.3).
- Session restrictions: entries only in `[EntryOpen, EntryClose)`; waiting opportunities and unfilled entry quantity die at `EntryClose` (§A.1).
- Data-integrity gates: stale/disconnected feed, late event, parity mismatch, persistence failure ⇒ `Degraded` (entries disabled; exits, stops, targets and liquidation continue; §6.5, §9.3).
- No LTP/entry-price substitution anywhere (§A.2 item 5, §9.1).
- A stronger signal never bypasses any enabled gate; all failing reasons are recorded in a fixed order.

### 10.3 Reservation / duplicate prevention (per variant)
At entry-order creation the variant atomically reserves a position slot (`SlotNo 0`) and `reservedPremium = qty × limitPrice + estimatedEntryCosts` (recorded, not enforced unless a capital limit is configured). The reservation lives while the opportunity is `Triggered/OrderPending` or the position is in any non-flat state (`EntryPending, PartiallyOpen, Open, ExitPending, ExitFailed`), and is reduced to the filled amount on cancel/expiry/reject. A new entry decision for a variant whose slot is occupied is `Rejected(CapacityBusy)` (immediate conditions) or keeps waiting (waiting conditions), per §A.4. A decision whose `DecisionId` already exists is a no-op. The DB backstop is a partial unique index scoped to `(RunId, AccountId, VariantId, SlotNo) WHERE State <> 'Flat'` (§8.1) — **never** database-wide, so different variants (and, later, more slots) coexist.

### 10.4 Daily P&L definition (recorded in ResearchV1; enforced only if a later profile enables it)
`DailyPnL_v = RealisedNet_v + UnrealisedLiquidationNet_v`
- `RealisedNet_v` = Σ closed-fill proceeds − cost of closed quantity − all costs (§A.9; slippage is already in fill prices).
- `UnrealisedLiquidationNet_v` = open qty × (**current executable bid** − average entry fill price) − estimated exit costs (cost schedule), i.e. what liquidation would net now. Not mid, not LTP.
- `Unmarkable` (no fresh executable bid): for the daily-SL record the value is marked conservatively at **zero bid value** and flagged `MarkedConservatively`; this never alters realised P&L or triggers anything in ResearchV1.
- Daily SL is separate from per-trade SL (§A.6). Breach handling in ResearchV1 is record-only (§A.7).

### 10.5 Capital/exposure
Not enforced by default. `PeakCommittedPremium_v` and `PeakCommittedPremium_run` are recorded per day. If `AccountCapital` is configured **and** the profile enables enforcement, `CommittedPremium = Σ open (avg entry × remaining qty) + reservations (+ estimated costs) ≤ AccountCapital`, with partial exits reducing the commitment. Independent virtual accounts never share capital; shared-account constraints belong to the separate `AccountPolicy` (§A.3).

### 10.6 Unresolved risk values (no proposals of monetary limits)
| Item | Status |
|---|---|
| Account capital | unresolved — not enforced; **no default** |
| Daily SL per variant | unresolved — null until Naveen supplies; recorded only |
| Per-trade SL %, TP1 %, TP1 proportion, final TP %, remainder SL | research parameters per variant; none approved |
| Max premium exposure, max trades/day (RealOrder profile) | unresolved |
| Lot size / tick size | verified: 65 / ₹0.05 on all 18 sessions |
| Default quantity | **650 units (10 lots)**, confirmed requested simulation default |
| Latency, quote age, spread/size limits, expiries, collars, liquidation start, retry cadence | PLACEHOLDERs with a sensitivity grid (§A.10); proposed starting values only for plumbing tests: latency 1000 ms, quote age 2000 ms (below the 5 s adaptive freshness), spread ≤ 3 % of mid and ≤ ₹2, fill fraction 1.0 of displayed level-1 size, entry expiry 5 s, exit retry 1 s, `LiquidationStart` 15:05:00, `ExitCollarStepTicks` 1, `MaxExitCollarTicks` 10 |
| Costs | schedule provisional (§A.9); brokerage plan unknown |

## 11. Research contract (Phase 4; fixed now so Phase 2/3 do not leak into it)

### 11.1 Verified session and data availability (V; read-only SQL on local `niftysignal_vm_copy`, 110,517,168 estimated rows)
The "18 sessions" exist: **2026-09-04 … 2026-10-01**, 18 distinct trading dates in `ticks` (IST). `instruments` has an `AsOfDate` snapshot for each (≈80 NIFTY option contracts, 2 expiries, strikes ≈ ±475–500 around spot, lot size 65). Weekly expiries are Tuesdays: **09-08, 09-15, 09-22, 09-29 are expiry days (4 of 18)**; 09-04 uses expiry 09-08 (DTE 2).

| Session | Ticks (all tokens) | Tokens | Level-1 bid/ask present | Received first–last (IST) | Role in prior work |
|---|---:|---:|---:|---|---|
| 09-04 | 3,861,744 | 83 | 93.3 % | 09:25:36–16:58:33 (late start; trailing non-session ticks; first exchange time `00:00:00`) | in the 18-session diagnostics tables; not an adaptive discovery date; any strategy-level use not confirmed |
| 09-08 | 4,313,612 | 84 | 93.8 % | 08:45:57–15:35:08 | **adaptive discovery** |
| 09-09 | 3,998,088 | 84 | 93.1 % | 08:45:32–15:35:12 | adaptive discovery |
| 09-10 | 3,952,562 | 83 | 93.0 % | 08:45:57–15:35:05 | adaptive discovery |
| 09-11 | 4,305,336 | 83 | 93.5 % | 08:46:02–15:35:06 | adaptive discovery |
| 09-15 | 4,860,981 | 83 | 89.8 % | 08:45:52–15:35:19 | adaptive discovery |
| 09-16 | 4,297,937 | 83 | 88.7 % | 08:45:29–15:35:33 | adaptive discovery |
| 09-17 | 4,412,082 | 83 | 89.0 % | 08:45:09–15:35:13 | adaptive discovery |
| 09-18 | 4,368,784 | 83 | 89.3 % | 08:45:22–15:35:25 | adaptive discovery (also volume-bar holdout) |
| 09-21 | 4,458,263 | 83 | 89.5 % | 08:45:50–15:35:23 | adaptive discovery (also volume-bar OOS day) |
| 09-22 | 8,961,800 | 165 | 94.8 % | 08:45:30–15:35:46 | adaptive discovery |
| 09-23 | 8,407,183 | 165 | 94.5 % | 08:45:39–15:35:44 | adaptive discovery |
| 09-24 | 8,203,740 | 165 | 94.1 % | 08:45:56–15:35:17 | adaptive discovery |
| 09-25 | 8,930,138 | 167 | 92.0 % | **08:57:43**–15:35:46 | adaptive discovery |
| 09-28 | 9,814,018 | 167 | 92.6 % | 08:45:40–15:35:45 | *not* adaptive discovery |
| 09-29 | 11,072,823 | 167 | 93.1 % | 08:45:20–15:35:24 | *not* adaptive discovery |
| 09-30 | 8,238,670 | 167 | 91.3 % | 08:45:56–15:35:00 | *not* adaptive discovery |
| 10-01 | 8,303,530 | 167 | 91.2 % | 08:46:10–**15:29:48** (ends before 15:30) | *not* adaptive discovery |

Notes: token counts ≈ double from 09-22, consistent with SENSEX/BANKNIFTY collection (U – see `SENSEX_BANKNIFTY_TICK_COLLECTION.md`; not verified here). "Level-1 bid/ask present" is the share of all-token rows with a populated `bid1/ask1` (full-depth requirement, §1.3); it is **not** per-contract executable-quote coverage, which must be measured per contract/time bucket in Phase 3 before any fill-model claim. The query used `ReceivedAt` for the date; `ExchangeTimestamp` has quirks (`00:00:00`, ~06:07 starts). 09-04 and 10-01 have atypical session edges; 09-25 starts late. **Not verified:** the VM database (sessions after 10-01, e.g. the 10-05…10-08 live watch-only sessions, are not in the local copy; I have no VM access in this phase); contents of `niftysignal_backtest_analysis`/`niftysignal` local DB (7.9 M ticks, 09-04…09-07 only, not the research set).

### 11.2 Examined data — do not label as untouched
- The 13 dates **09-08 … 09-25** are the adaptive observer's *discovery* sessions (V: `OpeningVolumeProjectionV1.DiscoveryOutOfFold`).
- 09-04 and **09-28, 09-29, 09-30, 10-01** were not adaptive discovery dates and the roadmap states "no sealed dates used" for the historical gate (A, doc), **but** all 18 sessions have been viewed by diagnostics: live-loop performance validation used 09-21, 09-23, 10-01; the tick-rate table and notification-volume replay used all 18 (`ADAPTIVE_LIVE_PERFORMANCE_2026-10-08.md`, `FUTURES_GRID_PLAN_2026-10-08.md`); volume-bar research used 09-08…09-21 for tuning and 09-18/09-21 as held-out days (`VOLUME_BAR_FINDINGS.md`). Neither those documents nor git history gives a complete per-experiment ledger of which dates were inspected for *strategy* questions.
- **Consequence (contract):** no session is "untouched" for the purposes of rule discovery. The five non-discovery adaptive dates are only *less contaminated*, and shall be labelled "examined for diagnostics, not used for adaptive rule discovery" in every report. True out-of-sample evidence can only come from sessions recorded after the rule freeze (forward validation).

### 11.3 Labels and splits (revised by Amendment 1 / §A.10; D-6)
All 18 sessions (2026-09-04 … 2026-10-01) are **`Development`** data. The earlier discovery/validation partition of these dates is withdrawn: they cannot provide out-of-sample evidence for any rule. Out-of-sample evidence comes only from sessions recorded after a rule freeze **and** shown by the `session_ledger` not to have been examined for, or to have influenced, rule selection; sessions after 10-01 are `Unknown` until that ledger is reconstructed. Within development data, any internal chronological split used for model development must be declared before the run and is a development split, not a validation claim. Other rules unchanged: no look-ahead; rolling/expanding normalisation only from prior sessions; one hypothesis per experiment; expiry days (09-08, 09-15, 09-22, 09-29) analysed as a stratum; consumed hold-out data is permanently consumed (`BACKTEST_RULES.md` rule 15).

### 11.4 Research constraints
- Standalone signal families with explicit gates; **no composite score**; no forced daily trade count (ResearchV1 has no trade-count cap). Every threshold dynamic/DTE-based or labelled as an approved structural constant.
- Rolling ATM baskets are diagnostics only (§6.2); an entry condition may never compare an option ask with a basket average (§A.4).
- Concurrent variants are compared on separate virtual accounts; cross-variant sums are not achievable P&L (§A.3).
- Every tested variant is registered *before* running (id, hypothesis, parameters, split, date) in an append-only `research_variants` ledger (Phase 4 table/CSV, with user approval for new datasets per CLAUDE.md) and reported including negatives, each result a single data point in an accumulating series.
- Results are produced by the shared engine only (replay path), with costs and executable-quote fills; no point-P&L-only or mark-fill results count as evidence.
- A rule that survives development-data analysis and the execution-sensitivity grid (§A.10) is only a candidate; it requires forward paper validation (ledger-eligible sessions) with replay reconciliation.

## 12. Acceptance tests (Phase 2 gate)

Use a **plumbing strategy** `PlumbingEveryNthBarStrategy` (enter on every k-th completed bar alternating CE/PE, exit after m bars) — deterministic, no edge claim, documented as such in code and test names.

| ID | Test | Pass criterion |
|---|---|---|
| T1 | **Replay/live-path equivalence.** One recorded session fixture (real ticks, ≥1 full day slice and a synthetic edge-case file) fed through Historical adapter and through LivePaper adapter (fake poll source delivering the same ticks in arbitrary batch sizes with injected `FakeClock`) | Byte/field-equal features, bars, decisions (incl. rejects), order intents, order events, fills, positions, risk-counter snapshots, P&L; diff tool reports 0 differences |
| T2 | Batch-boundary independence: same events split into random poll batches (property test, seeded) | Output equal to T1 for every split |
| T3 | Duplicate ticks / duplicate event `Seq` | No duplicate decisions/orders/fills; counters unchanged |
| T4 | Equal timestamps (large availability group, mixed instruments) | Processed as one group in contract order; bar emitted after group quotes |
| T5 | Out-of-order / late tick (older than watermark) | `LateEventFault`, entries disabled, exits managed; recovery replay reproduces; no silent reorder |
| T6 | Late quote (arrives after `ExecutionTime` for an order) | Not used for that fill; order stays `Working`/expires |
| T7 | Stale quote (age > limit) at execution; no two-sided quote | No fill, no LTP substitution (assert LTP never read), order expires with reason |
| T8 | Partial fills (displayed qty < requested) | Per-quote partials, remainder cancelled at expiry, reservation reduced to filled amount, position qty = filled |
| T9 | Rejection (spread/size/min-bid) | Order `Rejected`, reservation released, trade counter not consumed, audit row with exact inputs |
| T10 | Exit rejection / no bid | Position `ExitFailed`, retries at `ExitRetryMs`, never flat without fill, alert fault |
| T11 | Disconnect: events stop for the held token | Entries off; compulsory-close timer fires at deadline in a quiet market (no events) using heartbeat in live, `Flush` in replay; outputs equal |
| T12 | Session close | Entry cutoff cancels pending entries; close-start forces exit; `SessionEnd` with unfilled position records `UnresolvedAtClose`, never fabricates a fill |
| T13 | Crash/restart with **pending entry order** | Recovery replays to identical pending order; no second order; persisted order events verified; ids unchanged |
| T14 | Crash/restart with **open position** (and with `ExitPending`) | State identical to uninterrupted run; exit still occurs once; no duplicate fills |
| T15 | Restart at many arbitrary tick/bar boundaries (adaptive "restart-parity" style) | Final persisted tables equal the uninterrupted run field-for-field |
| T16 | Risk gates that remain in ResearchV1: capacity per variant incl. pending reservation, duplicate-entry attempt, entry-window boundaries, quantity validation, integrity gates (no 5-trade cap, no daily-loss lockout) | Each gate rejects with the correct ordered reasons; absent gates do not reject |
| T17 | Determinism guard: banned APIs absent from Core; same run twice in-process and in separate processes → identical outputs; culture/timezone changed → identical | Pass |
| T18 | Config: hash stable; mid-session change rejected; restart with different hash refuses to trade | Pass |
| T19 | Contract fixation: exit for a different token rejected; selection uses only quotes ≤ DecisionTime (inject a better future quote → must not be chosen) | Pass |
| T20 | DB: unique keys and partial unique "one open position" index reject duplicates; transaction atomicity (kill between rows) | Pass |
| T21 | Regression oracle: engine adaptive feature outputs unchanged vs. current master on the existing frozen fixtures | Existing adaptive tests (1324 total pass at baseline) stay green |
| T22 | Entry-open boundary: decisions at 09:29:59.999 and 09:30:00.000 IST (Asia/Kolkata → UTC conversion, on a fixture date from the data) | 09:29:59.999 rejected (`Warmup`/`EntryClosed`); 09:30:00.000 accepted when ready |
| T23 | Entry-close boundary: 14:59:59.999 accepted; 15:00:00.000 rejected; waiting opportunity expired at 15:00; unfilled entry quantity cancelled, partially filled quantity kept; an order whose `ExecutionTime >= 15:00:00` does not fill | Exact outcomes; exits/stops still operate after 15:00 |
| T24 | Liquidation: `LiquidationStart` forces exits; retry cadence and collar escalation; no executable bid ⇒ `NoExecutableBid` and retry (no substitution); `TargetFlatBy` overdue ⇒ fault; `SessionEnd` unresolved ⇒ `UnresolvedAtClose` | Never flat without fills; report lists unresolved positions explicitly |
| T25 | Stops/targets independent of bars: no bar completes for a long period; SL, TP1 and final target trigger on quote events; session deadlines fire | Triggers occur on quotes/timers; identical to a run with bars |
| T26 | **Timer gap:** quiet gap spanning several deadlines (entry close, liquidation start, retries) before the next market event; chained deadlines (maturity → expiry → retry) | Every deadline runs before the later event with `DecisionTime == deadline`; output identical with/without synthetic heartbeats and independent of the end-of-stream flush |
| T27 | Concurrent variants A and A2 (and a third with different sizing) on one stream | Each variant has its own opportunities/orders/positions/ledger/config hash; removing or reordering the declaration of one variant does not change another's outputs |
| T28 | Capacity: second entry in the same variant while an order is pending/position open; two different variants open at the same time; DB unique index scoped `(RunId, AccountId, VariantId, SlotNo)` | Same-variant duplicate rejected (logic and DB); cross-variant coexistence allowed; no database-wide constraint |
| T29 | Independent virtual accounts vs shared-account policy interface | Independent default isolates P&L/exposure; pass-through shared policy changes nothing in Phase 2 |
| T30 | Immediate (A) vs waiting (A2) entries from the same base-strategy validity | A orders at creation; A2 waits; `OnCapacityBlocked` Reject vs Wait behave as specified |
| T31 | Condition input guard: attempt to feed a rolling-ATM-basket (or another token's) average to `AskBelowOptionCandleMA` | Rejected at configuration/assertion time; audit shows `inputToken == opportunity.Token` otherwise |
| T32 | Waiting entry evaluation: only quotes of the selected token; MA recomputed when a candle completes; a candle is invisible to quotes ordered before its completion; `N` configurable (e.g. 3 and 10); insufficient candles ⇒ `NotEvaluable` | Exact trigger instants; no use of other tokens, LTP or future candles |
| T33 | Invalidation: base strategy invalid ⇒ waiting opportunity cancelled and unfilled pending order cancelled; filled part remains under exit rules; feed `Degraded` ⇒ cancelled; stale selected-token quotes ⇒ suspended | As specified in §A.4 item 6 |
| T34 | Quote-triggered entry (D-12): trigger ask X; at `ExecutionTime` ask > X (policy a: no fill, re-arm), ask ≤ X (fill at actual ask), policy b (recheck); better future quote injected after `ExecutionTime` | Never filled at the observed price retrospectively; future quote unused; miss rate counted |
| T35 | Re-arm / re-entry: unfilled expiry ⇒ re-arm with `AttemptNo`; partial fill ⇒ opportunity consumed; after exit no re-entry until a new episode (`NewEpisodeRequired`); alternative policy gets a fresh contract selection | Exact opportunity ids/states |
| T36 | Quantity validation: 650 with lot 65 valid on all 18 dates; 600 or 645 invalid ⇒ `Rejected(InvalidQuantity)` with numbers, never altered; pre-run validator report lists variant-days | No silent rounding |
| T37 | Premium budget: valuation at order limit price + estimated entry costs; round down to whole lots; `MaxLots`; budget below one lot ⇒ `Rejected(InsufficientBudget)` | Exact lot counts; no round-up |
| T38 | No hidden capital: `AccountCapital = null`, 650 units at ₹150 (₹97,500) not restricted; peak exposure recorded; with capital configured in an enforcing profile ⇒ rejected | Both behaviours |
| T39 | TP1: average entry fill over multiple fills; `Tp1TargetQty = floorToLot(p × open)` stored once; rounds-to-zero ⇒ `Skipped`; remainder under `RemainderSL` (Unchanged / MoveToBreakeven / FixedPct) and final TP; priority `SessionLiquidation > TradeSL > FinalTP > TP1` | Exact quantities/prices; remainder is whole lots |
| T40 | TP1 partial fill, expiry and re-trigger for the remaining TP1 quantity only; entry still working when TP1 triggers ⇒ remaining entry quantity cancelled | Total TP1 never exceeds target quantity |
| T41 | **TP1 restart idempotency:** crash before/after TP1 trigger, mid-order, after a partial fill; replay | Same `OrderId`/`Tp1TargetQty`, no second TP1 order, no duplicate fill |
| T42 | Non-enforcing daily SL: breach recorded (first time, worst, still-breached) while entries continue, nothing liquidated or cancelled, kill switch untouched; counterfactual summary produced | Behaviour identical to the same run with daily SL unset, except the record |
| T43 | Per-trade SL independent of daily SL: trade SL fires with daily SL null; daily SL breach alone causes no exit | As specified |
| T44 | No trade-count cap: more than 5 entries per day permitted in ResearchV1; `RealOrder` enforcement flags unit-tested but that profile cannot start | Pass |
| T45 | Adapter by configuration: `Simulated` for replay and live paper yields identical outputs; `Broker` or a non-approved profile refuses start-up; `RealOrdersEnabled=false` | Pass |
| T46 | Execution-time quote rules: a quote arriving after `ExecutionTime` is unused even if processed earlier in wall time; stale quote; one-sided/missing bid or ask; no LTP/entry-price/trigger-price substitution anywhere | No fill / `NoExecutableBid`; static scan finds no LTP read in execution/exit code |
| T47 | Tie ordering: equal-time deadlines across classes and variants; a deadline equal to the event time runs first; result independent of variant declaration order and of run-to-run | Identical across 100 randomised declaration orders |
| T48 | Cost schedule: effective-date selection by trade date; per-fill components stored; STT 0.15 % sell-side for all 18 dates; schedule version is in the config hash; with/without-taxes report | Exact rupee amounts for a hand-computed fixture |
| T49 | Premium band boundaries (selector and engine): 99.95 / 100.00 / 150.00 / 150.05; `MinimumEntryPremium`/`MaximumEntryPremium` in the config hash | Inclusive at both ends; hash changes with either bound |
| T50 | Selection ranking: nearest to the midpoint, then smallest spread, then stable id; shuffled input (100 permutations) | Same selection and same evaluation list |
| T51 | Stale / future / one-sided / crossed / missing quotes; no qualifying contract; expiry policy without fall-through | `NoEligibleContract`, no intent, per-reason counts |
| T52 | Fixed contract while waiting: leaves the band, re-enters, a better strike appears, strategy invalidated | No switch; no intent outside the band; intent when back inside; cancel on invalidation |
| T53 | `NoEligibleContract` at creation: retry on later bars only | Selection attempts counted per bar, never per quote |
| T54 | Equal-time quote/bar: a quote in the bar's own group never sees that bar's candle; the next group does | Exact averages (113.33 vs 106.67 construction) |
| T55 | Candle validity: no observation, one-sided quotes, minimum observations, close age, skipped bars, late-first-seen token, depth limit | Invalid candle recorded, never forward-filled; `NotEvaluable` reasons exact |
| T56 | Prohibited inputs: another strike's history, a basket pseudo-token, another contract's quote | `ProhibitedConditionInputException` |
| T57 | CP1 clock invariant: heartbeat after / exactly at a deadline, delayed equal-time group, randomised operations | Pending deadlines never precede the clock; no backward dispatch |
| T58 | CP2 canonical hash and variant-hash goldens (independently computed); declaration order and other variants' presence | Pinned values; variant hash independent of siblings |
| T59 | Clock and execution timestamp: heartbeat beyond maturity with quotes on both sides of the deadline; freshness measured at the execution time; equal-time quote after the deadline; expiry boundary | Fill at the deadline time with the latest pre-deadline quote; no future quote used |
| T60 | Below-band modes and boundaries at execution: 99.95 / 100.00 / 150.00 / 150.05, price improvement, `BrokerRealisticBelowBand` flag and no clamping | Exact fills / no-fills |
| T61 | Liquidity: whole-lot partial entry, unusable quantities, fill fraction, unchanged displayed liquidity consumed once, independent virtual books | Exact quantities; labelled books |
| T62 | Cancellation / execution ordering at exact boundaries: quote vs invalidating bar in one group, deadline vs bar at one instant, invalidation before maturity, 15:00:00 maturity, 14:59:59 maturity | As specified in §A.13.2 |
| T63 | Exits: TP1 rounding table, bid-only triggers, gap through the stop, priority, reservation / no oversell, partial TP1 idempotency, remainder stop modes, strategy exit | Exact orders / fills |
| T64 | Golden trade ledger (entry, TP1, final exit, costs) against the independent recomputation | Component-exact |
| T65 | Costs: effective dating, rounding ties (67.275 and 67.305), brokerage configurable / not invented, provisional label, valuation flags | Exact |
| T66 | Conservation (quantity and cash), daily SL record-only over 7 trades, whole-engine determinism under repeats / heartbeats / parallel / cultures | Identical outputs |

## 13. Risks

1. **Hidden wall-clock coupling** in any reused class (A: only `Adaptive*` workers/services use `UtcNow`; pure calculators none). Mitigation: Core wrapper tests T17; adapter-only clock.
2. **Late/reordered live rows** triggering the degrade-and-rebuild loop (G9). Mitigation: fault policy §6.5; Phase 3 measure frequency from `ReceivedAt`/`Id` anomalies; do not auto-retry forever.
3. **Quote coverage** for executable fills is untested per contract (11.1 note) – the fill model may reject a large share of intended trades; that is information, not a bug.
4. **Overfitting / contamination** (§11.2).
5. **Memory** of full-session replay (≈1.9 GB WS) when the trading engine is added on top in the same process.
6. **Tick loss on the live path** (G8) breaks historical-vs-live equality for reasons outside the engine; reconciliation must separate the two (§9.4).
7. **Scope creep** into Host/Dashboard before edge is found; Phase 2 adds no registration in `Host/Program.cs`.
8. **Audit-pass claims** marked A may contain errors; Phase 2 starts by re-verifying the A-items it depends on.
9. **Costs schedule** uncertainty can flip conclusions for low-premium weekly options (₹100–150 premium, brokerage ₹40 round trip + taxes ≈ material fraction of a 1-lot P&L).

10. **Waiting-entry optimism:** a quote-triggered entry only counts if the order policy (D-12) fills; the miss rate between "condition observed" and "filled" can dominate A2 results and must be reported.
11. **Candle-definition sensitivity (D-13):** A2 outcomes depend on candle clock, price field and N; each is a named variant, tested at several N, and logged in the variant ledger.
12. **Independent-account overstatement:** concurrent variants all see the same displayed size; cross-variant sums are not achievable.
13. **Unenforced daily SL** in ResearchV1 can produce large uncapped losses in research; it is recorded, and a real-order profile must be a separate approved configuration.
14. **Provisional costs:** until the NSE primary documents are read, cost components are provisional; low-premium weekly options are cost-sensitive (§A.9 illustration).
15. **Unresolved-at-close positions** can exist when no executable bid is available in the liquidation window; they are reported, not hidden, and distort daily P&L comparisons unless flagged.

## 14. Decisions (revised by Amendment 1)

Status: **Confirmed** = given by Naveen (2026-10-09 amendment); **Revised** = earlier recommendation changed; **Open** = still needs Naveen, with recommendation.

| ID | Decision | Status | Resolution / recommendation |
|---|---|---|---|
| D-1 | Architecture: pure `Trading.Core` + adapters wrapping (not modifying) `AdaptiveObserverEngine`; legacy engines retired from live path | **Accepted (CP2 go-ahead)** | Implemented through CP2: one pure core hosts N variants; the execution adapter is chosen by configuration from CP3 on. |
| D-2 | Capital, lots, exposure | **Revised** | Default requested quantity **650 units** (10 × 65) confirmed. Capital **not approved**: the ₹50k default and ₹16,250 exposure proposal are withdrawn; capital is nullable/unenforced. Still open: whether any capital limit applies to the later real-order profile. |
| D-3 | Daily-loss definition and enforcement | **Revised** | Definition retained for *recording* (realised net + unrealised liquidation at executable bid, net of costs). Enforcement in ResearchV1 = **none** (no lockout, no liquidation); per-variant daily SL value is configurable and currently unset. |
| D-4 | Contract-selection policy values | **Partly resolved** | Entry premium band **₹100–₹150 inclusive** confirmed (entries only; in the hashed configuration). Mechanism implemented (§A.12): nearest expiry on/after the trade date (`MinCalendarDaysToExpiry` default 0, no fall-through), ranking ask-nearest-midpoint then smallest spread then stable id (configurable), `MaxQuoteAge` PLACEHOLDER 2 s. Still open: admitting the expiry day by default (4 of 18 sessions are expiry days), spread/OI minima, and whether research should test DTE-dependent bands (CLAUDE.md prefers dynamic/DTE-based rules) — recommend keeping 100–150 as the confirmed structural entry policy and testing any DTE-dependent band only as a named research variant. |
| D-5 | Partial-entry policy | Open | Keep the filled part and cancel the remainder (default); all-or-none remains a named alternative for sensitivity. |
| D-6 | Research split labels | **Revised** | All 18 sessions = `Development`; no discovery/validation split among them; forward eligibility via the `session_ledger` (§A.10). |
| D-7 | Cost schedule | **Partly resolved (CP3)** | Exchange transaction charge incl. IPFT (₹3,553/crore, `NSE/FA/73061`) and STT on option sales (0.15 %, `NSE/FATAX/73524`) are now **read from the NSE circulars themselves**. SEBI fee, stamp duty and the GST base are still extract-based/provisional, and **brokerage is unknown** (required input). The schedule is effective-dated and stays labelled PROVISIONAL; reports show results with and without the provisional components. Open: read the remaining primary texts and supply the brokerage plan. |
| D-8 | Execution placeholders and sensitivity | Open | Implemented as explicit configuration defaults (latency 1 s, cancel latency 1 s, entry lifetime 5 s, execution quote age 2 s, entry spread limits ₹2.00 and 3 % of mid, fill fraction 1.0, exit retry 1 s), all PLACEHOLDERS. Recommend the Phase 3 calibration plus a low/base/high grid; recorded quotes cannot measure broker latency. |
| D-9 | New database `niftysignal_trading` | Open — **not created** | Approve only when Phase 2 checkpoint CP6 starts; local dev DB only; no migration applied until then. |
| D-10 | Cutoffs | **Confirmed / Revised** | Entry `[09:30:00, 15:00:00)`, target flat by 15:10:00 confirmed; expiry-day 14:00 cutoff dropped. Open only: `LiquidationStart` (proposal 15:05:00). |
| D-11 | Kill switch | **Revised** | Manual operator flag + data-integrity faults only; **no** daily-loss auto-trip in ResearchV1. Dashboard wiring still deferred. |
| D-12 | What a satisfied waiting condition creates | **Initial policy accepted** | Marketable limit at the observed qualifying ask (implemented as the `EntryIntent` limit price); no overlapping retry orders; re-arm only after a terminal outcome and strategy re-validation. Execution is CP3; recheck-at-execution stays a CP3 sensitivity option. |
| D-13 | Option-candle definition for A2 | **Partly resolved** | Adaptive-bar clock (V1); price source and N are explicit with no default; window must be N consecutive valid candles; invalid candles never forward-filled; sources AskClose/BidClose/MidClose/TypicalPrice implemented. Still open: which source(s) and N values research uses (each a named variant) and whether `MinObservationsPerCandle` (1) and `MaxCloseAge` (not enforced) stay permissive — recommend keeping them and testing stricter values as sensitivity. |
| D-14 | Liquidation / escalation settings | **Resolved for CP3** | A runnable engine requires an explicit `LiquidationStart` (≥ `EntryClose`); no 15:05 is assumed in the engine. Retry cadence = `ExitRetryInterval` (placeholder 1 s); no collar escalation (see D-23). Open only: which real `LiquidationStart` value to run research with. |
| D-15 | Exit defaults: `RemainderSL` default, `TriggerBasis`, reference price | Open | `RemainderSL = Unchanged`, `TriggerBasis = Price`, reference = average entry fill price (price only). |
| D-16 | Per-variant opportunity defaults | Open | `OnCapacityBlocked`: Reject (Immediate) / Wait (waiting); `MaxAttemptsPerOpportunity` unlimited; `ReentryPolicy = NewEpisodeRequired`; partial fill consumes the opportunity. |
| D-17 | Session ledger for post-10-01 sessions | Open | Reconstruct which sessions were viewed/used (incl. 10-05…10-08 live watch-only) before any is called forward data; until then `Unknown`. Provide VM data export when ready. |
| D-18 | Fills below `MinimumEntryPremium` | **Resolved (CP3)** | Implemented as the confirmed policy: default `ResearchEnforceBand` (no fill outside [100,150] or above the limit) and the separately named `BrokerRealisticBelowBand` (fill at the actual ask, flagged `BelowBand`, reported separately, never clamped). A broker buy limit cannot enforce a minimum fill price. |
| D-19 | Where the adaptive readiness policy gates opportunity creation | Open — CP5 | Recommend: the adapter withholds `BarCompleted`-driven validity until `AdaptiveReadinessPolicy` is ready (10 consecutive valid bars, session frozen), so the core stays free of adaptive internals. |
| D-20 | Level-1 liquidity treatment | Open | The simulator takes at most the displayed size x `FillFractionOfDisplayed` once per distinct displayed state and treats a changed displayed state as fresh. Recommend keeping this and testing `FillFractionOfDisplayed` 0.25 / 0.5 / 1.0 as sensitivity; depth, queue position and market impact stay unmodelled. |
| D-23 | Price protection on exits | Open | Exit orders sell at the eligible bid with no limit or collar (a gap fills at the available bid). Recommend keeping it for ResearchV1 and revisiting a protective limit only for the real-order profile. |

## 15. Phase 2 bounded scope — implementation checkpoints

Still: no edge claims, no `Host` registration, no deployment, no merge, no strategy sweeps, no real orders, no broker code beyond an interface and a throwing stub. **Prerequisite:** Naveen resolves D-1, D-12, D-13 (or accepts the recommendations) and approves D-9 before CP6. Each checkpoint ends with `dotnet build` (0 warnings in touched projects) and `dotnet test` green, and a short status note appended to this document; work stops at the checkpoint gate for review.

| CP | Content | Gate (must pass before the next) |
|---|---|---|
| **CP0 — Re-verify & scaffold** *(DONE 2026-10-09, §A.11)* | Re-verify the audit-pass (A) items the plan depends on; create empty `Trading.Core` / `Trading.Signals` / tests projects with the banned-API determinism test (no `DateTime*.Now/UtcNow`, `Stopwatch`, `TimeProvider.System`, `Task.Delay`, `Random`, `Guid.NewGuid`) | build/test green; banned-API test green; no change to existing projects |
| **CP1 — Time, events, timers** *(DONE 2026-10-09, §A.11)* | Event types, `Sequencer`, `DeadlineQueue` with class-rank tie order, `AdvanceTo`, replay loop that runs every deadline `<= T` before an event, deterministic ids/hash, `TradingConfig` + hash, session phases incl. `EntryOpen/EntryClose/LiquidationStart/TargetFlatBy/SessionEnd` (Asia/Kolkata → UTC at the boundary) | T17, T18, T22, T23 (timer parts), T26, T47 |
| **CP2 — Variants, accounts, opportunities** *(DONE 2026-10-10, §A.12)* | Variant/account model, per-variant isolation, capacity + reservations + `SlotNo`, base validity vs entry condition (Immediate, `AskBelowCandleMA`), per-contract candle histories, entry premium band + pure contract selection, opportunity lifecycle, re-arm/re-entry, plumbing strategy, contract fixation | T19, T27–T35 and the premium/candle/session/clock tests below |
| **CP3 — Execution, costs, sizing, exits** *(DONE 2026-10-10, §A.13)* | Simulated execution (latency, execution-time quotes, freshness, spreads, level-1 liquidity, partial whole-lot fills, expiry, cancellation, both below-band modes), lot-validated sizing (fixed 650 / premium budget), positions from actual fills, per-trade SL / TP1 / final target, strategy-exit API, session liquidation with `UnresolvedAtClose` / `UnresolvedAtSessionEnd`, effective-dated costs, record-only daily SL | T59–T66 and the CP3 test classes |
| **CP4 — Risk profiles, integrity, audit, summaries** *(DONE 2026-10-10, §A.14)* — original scope: `ResearchV1` profile (non-enforcing daily SL, no max trades), always-on rules, `RealOrder` profile as unit-tested-but-unstartable config, kill switch (manual/integrity), `DecisionAudit` with bounded per-quote summaries | T16, T42–T45 |
| **CP5 — Adapters & equivalence** | `AdaptiveEventTranslator`, Historical adapter, LivePaper adapter library with fake poll source and journaled heartbeats, adapter selection by config, run-diff tool | T1, T2, T4, T5, T11, T21 |
| **CP6 — Persistence & recovery** *(needs D-9 approval)* | `Trading.Data` EF model, scoped unique indexes, atomic per-step writes, replay-and-verify, crash/restart cases; migrations applied to a local dev database only | T13–T15, T20 |
| **CP7 — Close-out** | Full T1–T48 run, `ARCHITECTURE.md` module-map update, contract status notes, findings as `PENDING/DEFERRED (audit finding FNN)` comments, final report | all tests green; no warnings in new projects; adaptive tests unchanged |

**Proposed CP3 scope (bounded; for review).** (1) `SimulatedExecutionModel`: consume `EntryIntent` / `CancelRequest`, latency as an `ExecutionMaturity` deadline, execution-time quote lookup (latest quote `<= ExecutionTime`, freshness, two-sidedness, spread/size), limit semantics (fill at the actual ask only if `<= limit`), whole-lot partial fills, expiry and cancel, and D-18 below-band handling; resolutions delivered through `ReportEntryOutcome` from inside the loop. (2) Sizing: lot validation of the requested 650 against the per-date lot size (65), pre-run validator report, `FixedQuantity` and `PremiumBudget` modes. (3) Positions and exits: position state per slot, per-trade SL, TP1 with remainder and idempotent TP1, final target, `LiquidationStart` (if configured) / `FinalClose` behaviour with retry/escalation and `UnresolvedAtClose`, exits on the fixed contract and independent of bars and of the premium band. (4) `CostSchedule` with effective dates and per-fill components (provisional until D-7). (5) `ResearchV1` risk profile: daily SL recorded only, no trade cap, always-on integrity rules. (6) Tests T3, T6–T10, T12, T24, T25, T36–T41, T46, T48 plus the full CP0–CP2 regression. Not in CP3: persistence, adapters, `Host`, database, broker.

**Proposed CP4 scope (bounded; for review).** (1) Risk profiles: `ResearchV1` as a named, hashed profile (record-only daily SL, no trade cap, quantity/lot validation, duplicate prevention, session restrictions) and a `RealOrder` profile that is unit-tested but cannot start (enforcement modes `None | BlockEntries | BlockEntriesAndLiquidate`, optional trade cap and exposure limit, all unapproved and disabled). (2) Integrity faults: late event / stale feed / persistence-style fault → `Degraded` (entries disabled, exits and liquidation continue), manual engine-owned kill switch (entries only), no auto-trip on daily loss in ResearchV1. (3) Audit: replace free-text reasons by reason-code enums on every accepted / rejected step, put the config and variant hash on every record, bounded per-quote evaluation counters. (4) A per-variant daily summary object (trades, net P&L with and without provisional components, below-band fills, late fills, unresolved, daily-SL record, peak committed premium recorded but not enforced). (5) Tests T5, T16, T42–T45 plus CP0–CP3 regression. Not in CP4: persistence (CP6), adapters (CP5), `Host`, database, broker.

**Proposed CP5 scope (bounded; for review; not started).** (1) `AdaptiveEventTranslator`: adaptive-observer outputs (quotes, completed bars, session freeze/readiness) → `EngineEvent`s, shared by both adapters (D-19 readiness gating in the adapter). (2) Historical adapter over recorded ticks using the existing normalizer/ordering semantics, driving `TradingEngine` with virtual time; admission/quarantine wired in front of the loop; **no strategy sweeps**. (3) LivePaper adapter *library* only (fake poll source, journaled heartbeats, `IFeedHealthSource` implementation for the DataGap/receive-watchdog inputs with explicit thresholds as configuration), adapter selection by configuration, no `Host` registration. (4) A run-diff tool proving historical vs live-paper event/output equivalence on the same recorded stream (T1, T2, T4, T5, T11, T21), including the audit trail and daily summaries. (5) Decisions to settle first: D-19, D-9 timing, `LiquidationStart` value, brokerage plan (D-7), watchdog thresholds. Not in CP5: persistence (CP6), database, `Host`, broker, deployment, research runs, the research plan of §A.15.

Out of Phase 2: broker adapter, Host registration/deploy, Dashboard changes, calibration of placeholders, per-contract coverage study, signal-family evaluation, any research run, shared-account policy beyond the pass-through interface.

## 16. Checks run (baseline, worktree `D:\Projects\NSE-phase1`, Windows, .NET SDK 10.0.401)

| Command | Result |
|---|---|
| `git fetch origin` | ok; `origin/master` = `1ef645be330d1d864ba50b6eb2dc614c744f9674`, local `master` identical (0 ahead/0 behind), working tree clean, no stashes touched |
| `git worktree add -b trading-engine-phase1-contract ../NSE-phase1 origin/master` | ok; HEAD `1ef645b` (a pre-existing detached worktree `D:/Projects/NSE-OOS-Export` @ `29552c1` was left untouched) |
| `dotnet build NiftySignal.slnx -c Release -nologo -v q` | **succeeded, 0 errors, 3 warnings** (`tools/AdaptiveLivePerformance/Program.cs(205,8)` CS0219; `OptionsSupplementalTests.cs(23,9)` and `AdaptiveCommentaryPersistenceTests.cs(100,9)` xUnit2000); 3 min 39 s. Warnings pre-exist at baseline and violate the "0 warnings" working rule — recorded, not fixed (Phase 1 is docs-only) |
| `dotnet test NiftySignal.Tests -c Release --no-build -nologo` | **Passed 1324 / Failed 0 / Skipped 0**, 6 s |
| Read-only SQL (psql 18, `default_transaction_read_only=on`) on local `niftysignal_vm_copy`: `pg_database`, `information_schema.columns/pg_indexes` for `ticks`, per-date `ticks` aggregates, `instruments` per `AsOfDate` | results in §1.2, §11.1 (per-date aggregate query took ≈5 min) |
| Not run (by instruction): relational/Chromium/CI gates, VM checks, any replay/sweep | — |

### 16.1 Amendment 1 verification log (2026-10-09; documents only)

| Action | Result |
|---|---|
| Read-only SQL: `instruments` for NIFTY options across all `AsOfDate`s (`count(distinct AsOfDate)`, `min/max LotSize`, `TickSize`) | 18 snapshots; `LotSize = 65` and `TickSize = 0.05` for every NIFTY option instrument on every date |
| Web search: NSE equity-derivatives transaction charges (circular `NSE/FA/73061`, 27 Feb 2026, effective 2026-03-01; earlier Oct-2024 circular) | Total ≈ ₹3,553/crore options premium each side (provisional; the per-crore split was garbled in the extract) |
| Web search: STT (Finance Act 2026, NSE STT page, circular `NSE/FATAX/73524`) | Option sale 0.15 % from 2026-04-01 (0.10 % before) |
| Web search: SEBI turnover fee, GST base, stamp duty | ₹10/crore; GST 18 % on brokerage + exchange + SEBI fee; stamp 0.003 % buy side since 2020-07-01 (secondary sources + NSE-page extracts) |
| `WebFetch` of `FA73061.pdf` (twice), `FATAX73524.pdf`, NSE STT page, NSE levies page | **All timed out (60 s); primary documents not read** |
| Build/test | Not re-run (documents only; no code changed). Baseline results in §16 stand |
| Database / migrations / deploy / merge | None performed |

### 16.2 CP0/CP1 verification log (2026-10-09)

**CP0 — audited facts re-read in source before relying on them (all confirmed at baseline `1ef645b` + docs commits):**
- `AdaptiveIncrementalTickNormalizer.Process` and `AdaptiveTickCleaner.Clean`: `AvailableAt = ReceivedAt < ExchangeTimestamp ? ExchangeTimestamp : ReceivedAt`; per-token duplicate = same payload as the immediately previous raw tick.
- `AdaptiveObserverEngine.ProcessAvailabilityGroup`: spot first, then non-future tokens by `Id`, then the future by `Id`; bar packages are returned after the group's quotes; a group at or before `_lastAvailableAt` throws.
- `AdaptiveObserverWorker`: `StableOrderingLag = 2 s`, `MarketHardClose = 15:35`, wall-clock `UtcNow` used for polling and the stable cutoff.
- `Domain/IstTime.Offset = +05:30`. NIFTY option `LotSize = 65` and `TickSize = 0.05` on all 18 session snapshots (SQL, previous step).

**Commands and results (worktree `D:\Projects\NSE-phase1`, Release, .NET SDK 10.0.401):**

| Command | Result |
|---|---|
| `dotnet build NiftySignal.Trading.Core -c Release` | 0 warnings, 0 errors |
| `dotnet build NiftySignal.slnx -c Release -nologo -v q` | succeeded, 0 errors, **3 warnings, all pre-existing** (identical to the baseline list in §16) |
| `dotnet test NiftySignal.Tests -c Release --no-build --filter FullyQualifiedName~NiftySignal.Tests.Trading` | 75/75 passed (run 4 times, identical) |
| `dotnet test NiftySignal.Tests -c Release --no-build` | **1399/1399 passed** (baseline 1324 + 75 new) |
| Mutation checks (temporary, reverted): deadlines at the event instant fired after the event; bar rank before future quotes; entry close made inclusive | each made 2–4 new tests fail, then the originals were restored |

### 16.3 CP2 verification log (2026-10-10)

**Branch state verified first:** `trading-engine-phase1-contract` at `3f842b849ceb957dd560dda7d343e11e97730d70`, working tree clean, `D:\Projects\NSE` untouched on `master`.

| Command | Result |
|---|---|
| `dotnet build NiftySignal.Trading.Core -c Release`, `dotnet build NiftySignal.Trading.Signals -c Release` | 0 warnings, 0 errors each |
| `dotnet build NiftySignal.slnx -c Release -nologo -v q` | succeeded, 0 errors, **3 warnings, all pre-existing** (identical to the §16 baseline list); **0 new warnings** (one new xUnit2031 in my own test was fixed before the final build) |
| `dotnet test NiftySignal.Tests -c Release --no-build --filter FullyQualifiedName~NiftySignal.Tests.Trading` | **169/169 passed** (run 3 times, identical; 75 at CP1 + 94 new) |
| `dotnet test NiftySignal.Tests -c Release --no-build` | **1493/1493 passed** (baseline 1324 + 75 CP1 + 94 CP2) |
| Mutation checks (temporary, reverted; file restored byte-identical) | band recheck removed → 1 test fails; spent episode may re-enter → 2 fail; EntryClose ignored → 2 fail; decision id uses whole-config hash → 2 fail; slot not reserved → 9 fail; **candles published after the variant loop → 0 fail (equivalent mutant: nothing reads candles during bar handling in CP2; it becomes observable only if a candle-reading condition is ever evaluated on a bar event)** |
| Independent check of golden hashes | the CP2 canonical text, config hash and variant hash were computed in Python (hashlib) from hand-written canonical JSON and then pinned; the C# values matched on first run |
| Harness defects found and fixed during the run (test code, not engine) | record `ToString()` is culture-sensitive, so test signatures are rendered under the invariant culture; a CP1 validation test that used `SchemaVersion = 2` as "unsupported" now uses 1 |
| Database / migrations / Host / deploy / merge / execution simulator / costs | none |

### 16.4 CP3 verification log (2026-10-10)

**Branch state verified first:** `trading-engine-phase1-contract` at `38831e9969dae574927d8cf4ffb9aaf10eb8daf3`, working tree clean, `D:\Projects\NSE` untouched on `master`.

| Command / action | Result |
|---|---|
| `dotnet build NiftySignal.Trading.Core -c Release` | 0 warnings, 0 errors |
| `dotnet build NiftySignal.slnx -c Release -nologo -v q` | succeeded, 0 errors, **3 warnings, all pre-existing** (identical to the §16 baseline list); **0 new warnings** |
| `dotnet test NiftySignal.Tests -c Release --no-build --filter FullyQualifiedName~NiftySignal.Tests.Trading` | **259/259 passed** (run 3 times, identical): 169 earlier + 90 new CP3 tests |
| `dotnet test NiftySignal.Tests -c Release --no-build` | **1583/1583 passed** (baseline 1324 + 75 CP1 + 94 CP2 + 90 CP3) |
| CP0–CP2 regression | all 169 earlier Trading tests unchanged and green; the only edits were the schema-3 golden values (recomputed independently and matched on first run) and replacing `RequestedQuantity` by `Sizing` in two test expressions |
| Defect found by a new test and fixed | exit order ids collided between two positions of one variant (decision id lacked the position); fixed by including the position id; covered by the 7-trade daily-SL test |
| Mutation checks (temporary, reverted; files restored byte-identical) | 12 of 12 killed: band not enforced (2 failing tests), clamp into band (2), liquidity not consumed (2), exit quantity not reserved (5), stop filled at threshold (8), session check removed (1), TP1 rounded instead of floored (3), triggers on ask (1), banker's rounding (1), strategy revalidation removed (2), late fills unflagged (1), unresolved position closed at session end (2). A first rounding test (67.275) was not discriminating — banker's and half-away agree there — so a 67.305 tie case was added before the run |
| Independent recomputation | `ledger.py` (Python `decimal`, half-up, no shared code) reproduced the hand-calculated golden ledger and the budget-sizing examples; the engine matched component by component |
| Cost sources | NSE circulars `NSE/FA/73061` (exchange charge) and `NSE/FATAX/73524` (STT) downloaded and text-extracted; SEBI fee / stamp duty / GST remain page-extract based |
| Database / migrations / Host / adapters / broker / deployment / merge | none |

### 16.5 CP4 verification log (2026-10-10)

**Branch state verified first:** `trading-engine-phase1-contract` at `67bc6ac02f12ce206b71a8f15a8e47e6976d831a` (CP3), working tree clean, `D:\Projects\NSE` untouched on `master`.

| Command / action | Result |
|---|---|
| `dotnet build NiftySignal.slnx -c Release -nologo -v q` | succeeded, 0 errors, **3 warnings, all pre-existing** (CS0219 `tools/AdaptiveLivePerformance/Program.cs(205)`; xUnit2000 `OptionsSupplementalTests.cs(23)`, `AdaptiveCommentaryPersistenceTests.cs(100)`); one new CS0105 (duplicate `using` in `ConfigCp2HashTests.cs`) was introduced and fixed before the final build → **0 new warnings** |
| `dotnet test NiftySignal.Tests -c Release --no-build --filter FullyQualifiedName~NiftySignal.Tests.Trading` | **307/307 passed** (259 earlier + 48 new CP4 tests) |
| `dotnet test NiftySignal.slnx -c Release --no-build` | **1631/1631 passed** (baseline 1324 + 75 + 94 + 90 + 48) |
| Regression changes | only the schema-4 golden hashes (recomputed independently), the test cost helper (explicit `BrokerageUnit`) and renamed brokerage types |
| Independent recomputation | `brokerage.py` (Python `decimal`, half-up, shares no code with the engine) produced the four brokerage-unit scenarios in A.14.0 (C2); the engine's component values matched; the A.13.9 ledger and the daily-summary golden values reuse the CP3 independent ledger |
| Defects found by the run and fixed | audit row for the entry-intent decision was categorised `Cancellation` (found in the sample output; now `EntryIntent`); a test needed its second same-instant quote inside the bar group (test code); a missing `using` in a test file |
| Database / migrations / Host / adapters / broker / deployment / merge / research runs | none |
