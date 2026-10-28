# Spot ATM contract (`spot-atm-v1`) and per-strike Greeks (`option-strike-v3`)

Branch `feature/atm-spot-center-greeks`. Not deployed. Written 2026-10-28.

## 1. The rule

The ATM center of the weekly option band is **the listed strike nearest the fresh NIFTY spot**; an exact midpoint chooses the **lower** strike (strike interval 50: spot 26,524 → 26,500; 26,526 → 26,550; 26,525 → 26,500).

* **Fresh** = the one existing spot freshness rule of `FuturesBasisTracker`: `0 <= (instant − last spot AvailableAt) <= 5 s`, inclusive, using the latest spot with `AvailableAt <= instant` (never a later one). `FuturesBasisTracker.FreshSpotAt(instant)` keeps every accepted spot observation so the answer is causal at any past instant; late (older than one already seen) and non-positive ticks are ignored as before.
* **No fallback.** Without a fresh spot (no frozen spot token, no spot tick, a stale spot) the band is **unavailable** with the deterministic reason `AtmCenterRules.SpotUnavailableReason`. The futures price, the synthetic forward and a cached stale spot are never substituted.
* The **synthetic forward is unchanged and still prices IV / Greeks / the residual anchor**; it no longer chooses the center under this rule.
* Both `SelectBandContext` call sites of `AdaptiveObserverEngine` (the band opened with the first bar, and the band opened at each completed-bar boundary) ask for the spot in force **at that instant**. The 09:30 residual anchor (`OptionResidualModel.BuildAnchor`, fixed-clock self-anchor) uses the spot at the anchor time, same rule, no spot ⇒ no anchor.

## 2. Versioning (why a separate stamp)

`ModelVersion` (`adaptive-v1`, `adaptive-fixed13650-v1`) is the session *model identity*: ~20 places key on it (the unique `(TradeDate, ModelVersion)` index, opening-volume and Strong-threshold history, screenshot schedules, Dashboard). A new model string would reset those histories. The ATM contract is therefore a separate, nullable column **`adaptive_sessions."AtmRuleVersion"`**:

| value | meaning |
|---|---|
| `NULL` | session created before this change: **legacy synthetic-forward center**, replayed exactly as computed (never relabelled) |
| `spot-atm-v1` | this contract |
| anything else | `AtmCenterRules.FromVersion` throws — a session of a newer contract is never replayed under another rule |

* Every NEW session of EITHER clock is stamped `spot-atm-v1`: fixed-clock sessions (`TryCreateFixedAsync`, the default `Adaptive:Clock:Mode`; the engine self-anchors at 09:30 from the fresh spot) and opening-volume (`adaptive-v1`) sessions (`TryGetOrCreateAsync`; the coordinator resolves the spot instrument with the shared `NiftySpotInstrument.ResolveAsync`, reads its ticks with the future and options, and builds the 09:30 anchor with `AtmCenterRules.FreshSpot` at the cutoff — no unique spot or no fresh spot = no anchor, still stamped, never the synthetic-forward center). A NULL stamp therefore means exactly "created before this change".
* Historical **seed** sessions (`IsHistoricalSeed`, `AdaptiveHistoricalBootstrapService`) run no option engine and have no band; they keep a NULL stamp (no center was ever computed for them).
* One freshness rule, one spot-identity rule: `FuturesBasisTracker.FreshSpotAt` (live engine) and `AtmCenterRules.FreshSpot` (session freezes from stored ticks: opening-volume anchor, NPT1 freeze) are asserted equal on randomized streams; the supplemental freeze, the opening-volume creation and the NPT1 builder all resolve the spot through `NiftySpotInstrument.ResolveAsync`.
* `AdaptiveStateRecoveryService` reads the stamp from the session row and passes the rule to the engine, so restart/replay reproduce the original center. The frozen spot token behaviour (`AdaptiveSessionSupplementalService.GetOrFreezeSpotTokenAsync`) is unchanged: resolved once per session, reused on every restart, an "unresolved" outcome is also frozen. A spot-rule session with no resolvable spot logs a warning and has no bands.
* NPT1: `npt1-m1-v2` (see `docs/NPT1_PAPER_THREE_LIVE_V1.md`).

## 3. `option-strike-v3` (Delta / Vega)

`AdaptiveStrikeProjection` computes, next to IV and from the **same** inputs (fresh mid, band synthetic underlying at the bar start / end, time to expiry, rate) and the **same solved volatility**, the Black-Scholes Delta and Vega via `NiftySignal.Pricing.BlackScholes` (no second implementation). Stored on `adaptive_option_strike_bars`: `DeltaStart/End/Change`, `VegaStart/End/Change` (nullable `double precision`), `change = end − start`.

* **Units are the Pricing contract, unscaled:** Delta is the raw option delta; **Vega is dPrice/dσ for a full 1.00 volatility move** (divide by 100 for "per vol point"). Nothing is multiplied by 100 in storage.
* Null whenever IV is null (no fresh mid, no band underlying — which now includes "no fresh spot" under `spot-atm-v1` — or no solution); never zero.
* `v2` / `v1` rows are never rewritten or relabelled; a `v3` row is a separate row (the unique key includes `MetricsVersion`). The Dashboard reads v3, then v2, then v1 per bar and side and never borrows a value across bars, sides or versions.

## 3b. Late spot ticks (replay determinism)

Under `spot-atm-v1` the spot CHOOSES persisted band centers, and core band rows are verified strictly on restart. A spot observation at or before an instant the engine has already processed is therefore **refused** (`InvalidOperationException`, "…deterministic recovery required") exactly like a late option/future group, so the worker discards its state and replays the ticks in availability order. Without this guard the live engine and a replay could choose different centers. The legacy rule keeps the old behaviour (the spot only feeds the basis sidecar; an out-of-order spot is counted in `LateSpotTicks` and ignored). A late spot that arrives after a persisted bar it would have changed still ends in a strict parity mismatch on that restart — the same, accepted failure mode as for late option quotes; the 2 s ordering lag is the protection.

## 4. Migration

`20261028130033_AddSpotAtmRuleAndStrikeGreeks` — 7 additive nullable `ADD COLUMN`s (no ALTER/DROP/TRUNCATE). Idempotent SQL: `NiftySignal.AdaptiveObserverData/Sql/adaptive-spot-atm-and-strike-greeks.idempotent.sql`. Target database `niftysignal_adaptive_observer`. The Host also applies it at start-up. **Deploy order: Host first (or apply the script), then the Dashboard** — the Dashboard's session/strike queries now select the new columns and fail on an unmigrated database (the fixed-strike query tolerates a missing table, not a missing column). **Validated on a real PostgreSQL 18 server** (2026-10-28, local development server, disposable databases `niftysignal_adaptive_mig_test_*`, all dropped): `AdaptiveMigrationPostgresTests` — empty database → latest (exact columns, types, nullability, indexes, no pending model changes); previous schema with historical v1/v2 rows and an unstamped session → latest (every row and value kept, nothing relabelled); v3 writes/reads (exact double round trip, unique key still rejects a duplicate, `(TradeDate, ModelVersion)` still unique); the project's own `AdaptiveSupplementalPersistence` insert/verify; the Dashboard's real fixed-strike query on mixed v1/v2/v3 data; the idempotent script applied twice, on an up-to-date database, and equal to the EF result; rollback to the previous migration (new columns dropped, historical rows kept); recovery and restart on PostgreSQL. To run them: set `NSE_ADAPTIVE_MIGRATION_TEST_DB` to a loopback connection to the maintenance database `postgres` (the tests create and drop their own database; any other host or database name is refused). Not run against any production or VM database.

## 5. Dashboard Grid 2 (OPTION CONTEXT)

The three **Roll ΔPx** columns (CE, PE, Roll Price Dir) are replaced by **ΔDelta** (CE, PE, Delta Dir) and **ΔVega** (CE, PE, Vega Dir), laid out like IV: raw change, ratio = change / |own start| as a percentage, Direction = CE change − PE change over the average |start| (|start| because a put delta is negative). Unavailable ⇒ `—`. Delta cells show 4 decimals and Vega 2 (display precision only; **no ×100 multiplier was applied** — the column semantics were confirmed as "change", the multiplier was not requested for these columns).

The research provenance line (captions / screenshots) now shows the contract: `… | ATM spot-atm-v1 | …` or `… | ATM synthetic-forward (legacy) | …`.

## 6. Known limits and decisions

* Sessions created before this change keep the legacy center (NULL stamp): their data is not comparable bar-for-bar with `spot-atm-v1` sessions.
* A day whose spot instrument cannot be resolved has no bands (observer) or is refused (NPT1).
* **IV, Delta and Vega depend on band availability, by existing design.** The strike projection takes its pricing underlying from the band selection (`Selection.SyntheticUnderlying`); under `spot-atm-v1` a missing/stale spot makes the band unavailable, so IV and the Greeks of every strike are null for that bar (the per-contract flow rows are still persisted). Decoupling the pricing underlying from band selection would be a separate decision; it was deliberately not changed.
* `tools/Npt1M1Parity` now resolves the spot and builds a v2 freeze; its comparison against legacy-rule research packages is expected to differ in band/anchor centers (the checks report it). Keeping bar-for-bar research parity would need an explicit legacy build path for that tool only — not added.
* `RunIdentity.FreezeText` serializes the whole `SessionFreeze`, so the spot token and ATM rule are part of the durable-run compatibility check (a resume under another contract is refused).
