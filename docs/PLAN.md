# Nifty 50 Options — Directional Signal & Paper Trading System

## As-Built Status (2026-09-09)

**This document is the original pre-build plan, finalized 2026-09-03. It is kept as the
historical record of intent — it is not current, and a growing amount of what follows no
longer matches what actually runs.** Anyone auditing this project against this file alone will
be auditing against a plan the live system has since moved past in real, documented ways. The
authoritative sources for current behavior are the source code itself (every material
deviation from this plan is doc-commented at its point of change, cross-referenced to an audit
finding number) and [`ARCHITECTURE.md`](../ARCHITECTURE.md) for the actual module map. This
section exists so that cross-reference doesn't have to be reverse-engineered from scratch.

**Material deviations from this plan's locked decisions, as of 2026-09-09:**

- **§1.2 risk sizing** — planned `MaxTradesPerDay: 7`; live is **30** (raised 2026-09-08 after
  the 7-cap was spent by 10:48 one session, before a later well-sustained move could even be
  evaluated — deliberately loose to observe unconstrained behavior, not a considered steady
  state). Planned `MaxConcurrentPositions: 2-3`; live is **3**, matches.
- **§6 Universal Score** — planned as 6 fixed-weight components (25/20/15/15/15/10) with a
  *dynamic* `k`. Live is materially different on both axes: `k` is now a **fixed constant**
  (the dynamic version was found to be self-defeating — see `CompositeScoreCalculator.
  DefaultK`'s doc comment, audit finding F1), there are now **14** tracked components (6
  directional + VixChange + 7 diagnostic-only components carrying weight 0, added and
  evaluated incrementally against live data rather than designed up front — see
  `ScoreWeights.cs`'s own revision history), and the weights themselves have been revised
  multiple times against live findings (`PriceMomentum` cut from 0.15 to 0 entirely, its
  weight redistributed to `OiBuildupNet`/`DepthImbalance`; see F4).
- **§4.1 time-to-expiry** — planned as calendar-days/365 floored at 1 hour; live now floors at
  **2 minutes** (F20, tightened after finding the 1-hour floor kept Greeks/IV frozen through
  the entire final hour before expiry — the highest-gamma stretch). A same-day detour tried
  trading-day (weekday) counting instead of calendar days (F6) and was reverted after external
  review — index options price on calendar time; that detour is gone, but is a real example of
  why this file can't be trusted as a live description.
- **§5.3 IV skew** — planned as a fixed delta or fixed strike offset; live anchors the compared
  strikes to the expiry's own expected move (`spot × σ × √t`, F8) instead, so the same
  *relative* strikes are compared regardless of vol regime. Window shortened from the planned
  60 minutes to 15 (2026-09-04, so it warms up same-session).
- **§7.1/7.2 rule engine — partially closed, largest remaining gap is the rest.** Planned as
  JSON-configured, NCalc-evaluated expressions, hot-reloaded via `IOptionsMonitor` + file
  watcher with a validate-before-swap gate. Entry/exit *logic* is still hardcoded C#
  (`EntryRuleEvaluator`, `ExitRuleEvaluator`) — no NCalc, no JSON rule expressions, no dashboard
  rules editor (§11); that rewrite is a separate, much larger undertaking and remains
  undone. But the config *values* half of this gap closed 2026-09-09: `RulesetConfig`/
  `ScoreWeights` now bind from appsettings (`RulesetConfigOptions`/`ScoreWeightsOptions`, see
  their own doc comments for why they're separate bindable DTOs rather than binding the domain
  records directly) via `IOptionsMonitor`, hot-reload on file change, and validate before swap
  (`RulesetConfigValidator`/`ScoreWeightsValidator` plus `ValidatedOptionsMonitor<TOptions,
  TDomain>` — a bad reload is rejected and logged, the running system keeps serving the last
  value that passed, and a bad value at startup fails fast rather than running on an unvalidated
  fallback). §7.2's example numbers themselves have drifted too:
  `MinAbsScore` 55 → **64** (F13, derived from a live-data replay, not guessed), premium band
  ₹150-200 → **₹100-150** (2026-09-07), `MaxDailyLossPct` 3.0% → **20.0%** (deliberately widened
  for paper-trading observation — see the risk-limits note below), and `RiskLimitsConfig`
  gained a `MaxDailyProfitPct` field the plan never specified.
- **§10 Backtesting** — planned as "same code path as live, only the tick source differs."
  `BacktestTickSource`/`PerformanceReportBuilder` exist and are tested, but nothing wired them
  together end-to-end (audit finding F32, still deferred — needs `LiveTradingEngine`/
  `PaperTradeSimulator` in the loop for real P&L replay, not just scoring). A narrower tool,
  `NiftySignal.ScoreReplay`, drives ticks through the scoring path alone (not entry/exit/P&L)
  to validate raw-value distributions against live data without waiting for a fresh session —
  see its own doc comment for the exact scope line against F32.
- **Risk limits are observation-mode, not the plan's risk box — flagged explicitly, not fixed
  here.** `MaxDailyLossPct: 20.0` / `MaxTradesPerDay: 30` (`LiveRulesetConfig.cs`) were
  deliberately widened for paper-trading exploration, on the explicit reasoning that no real
  capital is at risk while observing a fuller range of daily outcomes. **These must be tightened
  back toward this plan's own risk box (§1.2/§7.2: single-digit `MaxTradesPerDay`, low-single-
  digit `MaxDailyLossPct`) before this system is ever pointed at real money.** Left wide
  deliberately for now — do not read this note as permission to tighten mid-observation without
  cause, and do not read it as permission to skip tightening before real capital either.
- **Diagnostic (weight-0) components** — `GammaExposure`/`VolumePcr`/`SpreadRatio`/
  `VannaExposure`/`CharmExposure`/`CvdProxy`/`StraddleRichness` all launched at weight 0,
  explicitly pending correlation evidence (`scripts/batch5-component-correlation.sql`, audit
  finding F9/Batch 5). **None should move off weight 0 until that script has run against
  several clean (non-VM-restart-interrupted) live sessions on the current, corrected formulas**
  — one session's correlation is not evidence, and running it against still-buggy components
  (which several of these were, earlier) would just encode those bugs more precisely. This is a
  standing constraint, not a one-time check.

---

## Project Plan v1.1 (Finalized)

**Scope:** Paper trading only. Directional (option buying) strategy only. Option selling deferred to a later phase.

**Locked decisions (final):**

- Broker/data API: **FlatTrade** (free API, zero brokerage) — replaces Kite Connect referenced in earlier drafts
- Database: **PostgreSQL**, already installed locally — **no Docker**, no TimescaleDB
- Data access: **EF Core, code-first**, migrations-driven schema
- Architecture: modular monolith, single .NET solution, multiple class libraries
- Hosting: **Windows Service**, 24/7 on personal machine, auto-restart on failure
- Logging: Serilog
- Notifications: Telegram (entry/exit/partial-book/kill-switch/connection-failure/token-expiry/daily summary)
- Secrets: `appsettings.Local.json` (gitignored) or User Secrets — never committed `appsettings.json`
- Kill switch: dashboard toggle, disables new entries only (ingestion keeps running)
- Config: hot-reload required (`IOptionsMonitor`, file watcher)
- Runtime: **.NET 10** (LTS) — updated from the .NET 8 referenced in earlier drafts; .NET 8 support ends November 2026

---

## 1. Reassessment — Issues Found Before Development Starts

Before the plan itself, here are the things flagged before build started — the "we'd regret it later" items.

### 1.1 Secrets in appsettings.json — change this

Putting the broker API key/secret and Telegram bot token in `appsettings.json` is the single most likely thing to bite you. `appsettings.json` is committed to source control by default in every .NET template.

**Recommended instead:**

- Keep structure/shape in `appsettings.json` with empty values (self-documenting).
- Put real values in `appsettings.Local.json`, added to `.gitignore`, loaded via `optional: true` in the config builder.
- Or use .NET User Secrets for local dev (`dotnet user-secrets`), which stores outside the repo entirely.
- The broker's access token is **regenerated daily** and is not a static secret — it needs its own storage path (see 4.3), not the same place as the API key.

This costs 20 minutes now and prevents leaking live broker credentials.

### 1.2 Capital vs lot size — a real constraint

With ₹50,000 capital and a target premium of ₹150–200 per unit, one lot costs roughly `premium × lot_size`. At current NSE lot sizes this puts a single lot in the rough range of ₹11,000–15,000.

**Implication:** roughly **3 concurrent positions maximum**, not 7. The "3–7 trades per day" target is therefore *sequential* trades, not concurrent. This is an explicit rule:

- `MaxConcurrentPositions` (config, suggest 2–3)
- `MaxTradesPerDay` (config, suggest 7)
- Reject new entries when capital is committed, and **log the rejection** — the number of good signals skipped matters, otherwise the backtest overstates achievable performance.

Verify the current Nifty lot size before setting the default; it has changed multiple times and should be config-driven anyway.

### 1.3 Black-Scholes breaks down on expiry day

As time-to-expiry approaches zero, the IV solver becomes numerically unstable (division by near-zero vega; Newton-Raphson fails to converge). On expiry afternoon IV values become garbage or NaN without a guard.

**Mitigation:**

- Floor time-to-expiry at a small positive value (e.g., 1/365/24 — one hour).
- Add a convergence guard: max iterations, and if not converged, mark IV as `null` rather than returning a wrong number.
- Any feature depending on IV must handle `null` explicitly (skip the component and renormalize weights, don't treat null as zero — zero is a meaningful value and would corrupt the score).
- This is *why* expiry day needs its own configurable mode, beyond just gamma risk.

### 1.4 Architecture — modular monolith, not microservices

Solo developer, one machine, 24/7. Splitting ingestion/features/scoring/execution into separate services buys nothing here and costs deployment complexity, inter-process serialization, and debugging pain.

**Approach:** one .NET solution, multiple class library projects, one host process running multiple `BackgroundService` workers on an in-process channel/queue. Clean module boundaries (so it *could* split later) without paying distributed-systems tax now.

### 1.5 Backtest/live parity is the #1 correctness risk

If the backtest uses different code than live, results are meaningless and that won't be discovered for months.

**Non-negotiable design rule:** the feature engine, scorer, and rule engine take an abstract `ITickSource`. Live mode feeds it from the FlatTrade WebSocket; backtest mode feeds it from the database replaying stored ticks in timestamp order. **Zero conditional logic** (`if (isBacktest)`) inside the feature/scoring/rule code. If that branch shows up, the abstraction is wrong.

### 1.6 Windows 24/7 hosting — Windows Update will reboot you

A desktop running 24/7 will restart unattended. Plan for it:

- Run as a **Windows Service** (`UseWindowsService()` / `AddWindowsService()`) with automatic-restart-on-failure configured, or use NSSM.
- Set Windows Update active hours to exclude market hours (09:00–16:00 IST).
- On startup, the service must detect whether the market is currently open and resume cleanly mid-session — not assume it starts at 9:15.
- **State recovery:** open paper positions must survive a restart. Persist positions to the database, not just memory.

### 1.7 Database — plain PostgreSQL, local install

Using the PostgreSQL instance already installed locally, accessed via EF Core code-first — no Docker, no TimescaleDB. Plain Postgres with well-indexed tables is adequate at this data volume; TimescaleDB's hypertables would help at much larger scale but aren't necessary here.

**Volume estimate:** ~84 instruments × ~1 tick/sec × 6.25 hours ≈ **1.9M ticks/day**, roughly 100–200 MB/day raw. Plan a retention policy (keep raw ticks 90 days, keep 1-min bars forever). Index `ticks` on `(InstrumentToken, ExchangeTimestamp)` and consider a monthly partition scheme if query performance degrades — plain Postgres table partitioning (declarative, built in since PG 10) covers this without the Timescale extension.

**EF Core specifics:**

- Code-first: entity classes in `NiftySignal.Domain`, `IEntityTypeConfiguration<T>` per entity in `NiftySignal.Persistence` (not data annotations)
- `dotnet ef migrations add` for every schema change, applied on service startup (`context.Database.Migrate()`) so a fresh machine self-provisions
- `Npgsql.EntityFrameworkCore.PostgreSQL` provider
- For the high-frequency `ticks` table, consider bypassing EF's change tracking for inserts (raw `Npgsql` bulk copy or `EF Core bulk extensions`) — EF's per-row overhead is noticeable at ~1.9M rows/day; reserve plain EF Core for the lower-frequency tables (trades, signals, config)
- **Migration caution once `ticks` is large**: an EF migration that touches that table (even adding a nullable column) can lock it for a long time. Policy: schema changes to `ticks` are additive-only, or done manually off-hours.

### 1.8 Don't skip the warm-up guard

Z-scores computed on a partially-filled rolling window are unreliable and will fire bad signals. Add an explicit `IsWarmedUp` flag per metric — no trades until every contributing metric has a full window. On a fresh install this means no trading for the first several days while history accumulates. That is correct behaviour, not a bug.

### 1.9 Additional gaps closed before Phase 0

Identified in a pre-Phase-0 review pass, folded in as locked decisions rather than left as open items:

- **Automated daily-loss circuit breaker** — `MaxDailyLossPct` (config) auto-disables new entries for the rest of the day once breached, independent of the manual dashboard kill switch, with a Telegram alert.
- **NSE trading-holiday calendar** — required for "is the market open" / resume-mid-session logic (1.6), and to gate the daily instrument-master job from running (and alerting on failure) on a non-trading day.
- **Bad-tick / outlier sanity filter** — reject a tick if price/volume are inconsistent (e.g. LTP jumps >X% with no supporting volume) before it reaches the feature engine. Distinct from `data_gaps` handling (3.3), which only covers disconnects, not garbage received while connected.
- **Intraday ATM re-centering** — the daily 08:45 job computes ATM once from previous close (3.1); if Nifty moves far enough intraday, the tracked ATM±10 band stops bracketing the live ATM and PCR/skew/depth-imbalance features degrade near the edge. Needs a rule for re-centering the subscription list intraday.
- **Instrument-master job failure handling** — retry, then alert, then refuse to trade on stale mapping rather than silently continuing.
- **Config hot-reload validation gate** — validate before swap-in; reject a malformed config (typo'd weight, negative number) with an alert and keep the last-known-good config in effect, rather than applying garbage or crashing.
- **DB backup strategy** — nightly `pg_dump` (or WAL archiving) to a second physical location. The tick/bar/feature history is the entire value of the project and nothing currently protects it from a disk failure.
- **Exclude the pre-open auction window (09:00–09:15)** from feature computation, not just from entries — `NoEntryBeforeMinutes: 15` stops entries during this window but ticks from it would otherwise still feed the rolling stats, and auction-period price discovery is a different regime.
- **No overnight positions is an explicit invariant**, not just an implication of `SquareOffTime: 15:15` — promoted to a stated design constraint (no gap-risk modeling needed, no overnight margin considerations).

Deferred (worth a line here, not designed yet): tick de-duplication on reconnect, least-privilege service account / Postgres bound to localhost only, consecutive-loss circuit breaker, host health heartbeat (disk space, DB growth, service uptime), event-day calendar (RBI/budget/Fed days) as a rule-layer filter, documenting that concurrent positions are typically same-direction (not diversified) as an intentional property, shadow-mode ruleset testing, conviction-scaled position sizing.

---

## 2. System Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  Host Process (.NET 10 Worker Service, Windows Service)      │
│                                                              │
│  ┌────────────┐   ┌────────────┐   ┌────────────┐          │
│  │ Ingestion  │──▶│  Feature   │──▶│  Scoring   │          │
│  │  Worker    │   │  Engine    │   │  Engine    │          │
│  └─────┬──────┘   └─────┬──────┘   └─────┬──────┘          │
│        │                │                │                  │
│        ▼                ▼                ▼                  │
│  ┌──────────────────────────────────────────────┐          │
│  │                  PostgreSQL                    │          │
│  └──────────────────────────────────────────────┘          │
│                                 │                            │
│        ┌────────────────────────┴──────────┐                │
│        ▼                                   ▼                │
│  ┌────────────┐   ┌──────────────┐   ┌────────────┐        │
│  │   Rule     │──▶│    Strike    │──▶│   Paper    │        │
│  │  Engine    │   │  Selection   │   │ Simulator  │        │
│  └────────────┘   └──────────────┘   └─────┬──────┘        │
│                                             │               │
│                    ┌────────────────────────┤               │
│                    ▼                        ▼               │
│            ┌──────────────┐        ┌──────────────┐        │
│            │  Telegram    │        │   Serilog    │        │
│            │  Notifier    │        │  Audit Log   │        │
│            └──────────────┘        └──────────────┘        │
└─────────────────────────────────────────────────────────────┘
                          │
                          ▼
              ┌───────────────────────┐
              │  Blazor Dashboard      │
              │  (kill switch, config, │
              │   live scores, P&L)    │
              └───────────────────────┘
```

See [`ARCHITECTURE.md`](../ARCHITECTURE.md) at the repo root for the actual project/module map and the project-reference graph as built.

---

## 3. Data Layer

### 3.1 Instrument universe

- Underlying: Nifty 50 spot index + Nifty current-month futures
- Options: ATM ± 10 strikes, nearest weekly + next weekly expiry
- ~84 option instruments + 2 underlying
- **Daily 08:45 job:** download instrument master, resolve tokens, compute ATM from previous close, build subscription list, persist mapping. Retry-then-alert on failure (1.9); re-center intraday if the index moves far enough that ATM±10 stops bracketing the live ATM (1.9).

### 3.2 Tick storage schema (conceptual)

| Table | Purpose | Retention |
|---|---|---|
| `instruments` | Token ↔ strike/expiry/type mapping, per day | Forever |
| `ticks` | Raw tick: token, exch_timestamp, ltp, volume, oi, bid/ask top 5 | 90 days |
| `bars_1min` | OHLCV + OI close + avg depth imbalance per instrument | Forever |
| `features` | Computed feature vector snapshot per cadence | Forever |
| `scores` | Universal score + component breakdown | Forever |
| `signals` | Rule evaluation results, incl. rejections and reasons | Forever |
| `paper_trades` | Entry/exit/partial-exit records, P&L | Forever |
| `data_gaps` | Disconnect windows — explicitly marked | Forever |

### 3.3 Critical data rules

- Use **exchange timestamp**, never local receive time, for all time-sensitive computation.
- On reconnect, write a `data_gaps` row. Any rolling window spanning a gap must be flagged `degraded` and **must not** produce trade signals.
- Never interpolate across gaps.
- Store bid/ask depth — needed for both the depth-imbalance feature and realistic fill simulation.
- Reject a tick that fails the sanity filter (1.9) before it reaches the feature engine — a different failure mode from a gap.

### 3.4 Reconnection policy

- Exponential backoff: 1s, 2s, 4s, 8s, 16s, 30s (cap), with jitter
- After 5 consecutive failures → Telegram alert
- On reconnect: re-subscribe full instrument list, mark gap, resume
- Heartbeat check: if no tick received in 30s during market hours, force reconnect

---

## 4. Pricing Engine

### 4.1 Model

Black-Scholes-Merton, European style (correct for cash-settled Nifty index options).

**Conventions (locked):**

- Time to expiry: **calendar days / 365**, computed to expiry at 15:30 IST, floored at 1 hour
- Risk-free rate: static config value (91-day T-bill proxy), reviewed weekly, not fetched per-tick
- Dividend yield: **0** (documented simplification for index options; revisit if IV values look systematically skewed)
- Underlying for pricing: **futures price** (not spot) — this implicitly handles the dividend/carry adjustment and is the more correct input for index options

### 4.2 IV solver

- Newton-Raphson, seeded with Brenner-Subrahmanyam approximation
- Fallback to bisection if Newton fails to converge
- Max 50 iterations, tolerance 1e-6
- Return `null` on non-convergence — never a fabricated value
- Input price: **mid of bid/ask** when spread is reasonable; fall back to LTP if depth unavailable. Skip the strike entirely if spread exceeds a configured percentage of mid (illiquid → garbage IV)

### 4.3 FlatTrade session token lifecycle

FlatTrade (Pi API) sessions expire and require a login flow, same operational shape as any broker API:

- Onboard on **API v2** directly (v1/v2 session tokens invalidate each other if mixed, and v2 is the forward path)
- Store the current session token in the database with its issue timestamp
- On startup / on auth failure, surface a **Telegram alert with the login URL**
- Dashboard page to complete the FlatTrade login exchange and store the resulting token
- Do not silently retry — a stale token means no data, and that needs to be known immediately
- No official .NET SDK exists for FlatTrade (their library is Python-first) — the REST + WebSocket integration is hand-built against their docs (`pi.flattrade.in/docs`); budget extra time for this in Phase 1
- FlatTrade's WebSocket multiplexes market data and order updates on the **same connection**, with touchline (`t`) and depth (`d`) tick types, each with an acknowledgement stage and an update-only stage — the ingestion worker needs to demux by message type
- Keep all FlatTrade-specific field/message mapping isolated inside `NiftySignal.Ingestion`; the rest of the system should only ever see the broker-agnostic `Tick`/`Instrument` entities

**Confirmed 2026-09-03** against the live docs at `pi.flattrade.in/docs` (JS-rendered — needs a real browser, not a plain fetch, to read): REST base is `https://piconnect.flattrade.in/PiConnectAPI/`, WebSocket is `wss://piconnect.flattrade.in/PiConnectWSAPI/`. The docs' own changelog notes a breaking change from an older API generation still circulating in reference code online — connect task `"c"` → `"a"`, auth field `"susertoken"` → `"accesstoken"`, connect acknowledgement `"ck"` → `"ak"` — so anything built against an older third-party FlatTrade client should be checked against the current docs, not trusted as-is. Also confirmed: heartbeat interval is 30s, and FlatTrade publishes the scrip master as public, no-auth-required per-segment CSVs at `https://flattrade.s3.ap-south-1.amazonaws.com/scripmaster/<Segment>.csv` (e.g. `Nfo_Index_Derivatives.csv` for Nifty/BankNifty options+futures) — this resolves the "instrument-master format not yet confirmed" gap from section 1.9. Current Nifty lot size (used in the 7.2 example) is **65**, confirmed from that file.

---

## 5. Feature Engine

### 5.1 Per-strike features

- Last price, volume, OI, OI change (absolute and %)
- Implied volatility, IV Rank, IV percentile
- Greeks: Delta, Gamma, Theta, Vega
- Depth imbalance: `sum(top5 bid qty) / sum(top5 ask qty)`
- Bid-ask spread as % of mid
- OI buildup classification (see 5.2)

### 5.2 OI buildup classification

| Price | OI | Classification | Directional meaning |
|---|---|---|---|
| Up | Up | Long Buildup | Bullish on that option |
| Down | Up | Short Buildup | Bearish on that option |
| Down | Down | Long Unwinding | Bullish sentiment fading |
| Up | Down | Short Covering | Bearish sentiment fading |

Applied per strike per side. Aggregate CE buildup vs PE buildup gives a net directional read.

### 5.3 Index-level features

- Futures basis (futures − spot) and basis trend
- Spot VWAP deviation
- Short/long EMA relationship on 1-min bars
- India VIX level and change
- Aggregate PCR (OI-weighted and volume-weighted, over the tracked strike range)
- IV skew: `IV(OTM put) − IV(OTM call)` at a fixed delta or fixed strike offset, and its rate of change

### 5.4 Z-score normalization

```
z = (current − rolling_mean) / rolling_stddev
```

- Rolling window carried **across sessions** (continuous), not reset daily
- Implemented with Welford's online algorithm (numerically stable, O(1) update, no array storage)
- Clip to ±3 before use
- Per-metric window lengths:

| Metric | Window | Rationale |
|---|---|---|
| Depth imbalance | 5 min | Fast-moving, noisy |
| Price momentum | 15 min | Intraday responsiveness |
| PCR | 30 min | Slower-moving structural signal |
| OI buildup net | 30 min | OI updates are not tick-frequency |
| IV skew | 60 min | Slow structural signal |
| Futures basis | 30 min | Moderate |

**Decision: pooled continuous window, not same-weekday filtering.** Weekday filtering cuts effective sample size ~5x, making mean/stddev estimates unstable. Day-of-week and expiry effects are handled as explicit rule-layer filters instead — visible and tunable, and where the event-day calendar from 1.9 will also live.

**Warm-up:** each metric exposes `IsWarmedUp`. Composite score is not emitted until all contributing metrics are warm.

---

## 6. Universal Directional Score

Single score, −100 (strongly bearish) to +100 (strongly bullish), for Nifty as a whole.

| Component | Weight | Source |
|---|---|---|
| Net OI buildup bias | 25% | 5.2 aggregated |
| Aggregate PCR (z) | 20% | 5.3 |
| Futures basis (z) | 15% | 5.3 |
| IV skew shift (z) | 15% | 5.3 |
| Price momentum (z) | 15% | 5.3 |
| Depth imbalance, NTM strikes (z) | 10% | 5.1 aggregated |

```
raw    = Σ (weight_i × clipped_z_i)
score  = 100 × tanh(raw / k)          // k tuned so typical range spans usefully
```

- **Cadence: every 15 seconds.**
- Persist the **component breakdown**, not just the final number — without it, diagnosing why a signal fired is impossible.
- Weights live in config and are versioned. Every persisted score records the weight-set version that produced it.

---

## 7. Rule Engine

### 7.1 Design

- Rules stored as JSON, hot-reloaded via `IOptionsMonitor` + file watcher, with a validate-before-swap gate (1.9) — an invalid config is rejected with an alert, not applied
- Expressions evaluated with NCalc against the current feature/score vector
- Every ruleset has a **version id**; every signal and trade records it
- Three categories: **filters** (block), **entry**, **exit**

### 7.2 Configuration surface

```json
{
  "RulesetVersion": "2026-09-01.1",
  "Capital": { "Total": 50000, "LotSize": 65, "MaxConcurrentPositions": 3 },
  "Session": {
    "NoEntryBeforeMinutes": 15,
    "NoEntryAfterTime": "15:00",
    "SquareOffTime": "15:15",
    "ExpiryDayEnabled": true,
    "ExpiryDayNoEntryAfterTime": "14:00"
  },
  "Entry": {
    "MinAbsScore": 55,
    "MinScoreSustainedSeconds": 45,
    "ReEntryGapSameDirectionMinutes": 2,
    "MaxTradesPerDay": 7,
    "MaxIvRankForEntry": 70
  },
  "StrikeSelection": {
    "MinPremium": 150, "MaxPremium": 200,
    "MaxSpreadPctOfMid": 2.0,
    "MinOpenInterest": 100000
  },
  "Exit": {
    "PartialBookAtProfitPct": 30, "PartialBookFraction": 0.5,
    "StopLossPct": 25,
    "TrailAfterPartialBook": true,
    "ExitOnScoreFlip": true,
    "ExitOnScoreBelowAbs": 30,
    "MaxHoldMinutes": 120
  },
  "Costs": { "BrokeragePerOrder": 20, "SlippageTicks": 2 },
  "RiskLimits": {
    "MaxDailyLossPct": 3.0,
    "MaxConsecutiveLosses": 4
  },
  "KillSwitch": { "EntriesEnabled": true }
}
```

### 7.3 Entry logic

1. All filters pass (session window, kill switch, daily-loss circuit breaker, warm-up, no data gap, trade count, concurrent positions, capital available)
2. `|score| >= MinAbsScore`
3. Score has held the same sign above threshold for `MinScoreSustainedSeconds` — prevents entering on a single noisy spike
4. `>= 2 minutes` since last entry in the same direction
5. Strike selection returns a valid candidate
6. → Enter

Every failed evaluation is logged with the failing condition. The rejection data matters as much as the acceptance data.

### 7.4 Exit logic (evaluated every cadence)

- Partial book: close 50% at configured profit %
- Stop loss: configured % of premium paid
- Score flip: score crosses to opposite sign → full exit
- Score decay: `|score|` falls below `ExitOnScoreBelowAbs` → full exit
- Time stop: `MaxHoldMinutes`
- Hard square-off at `SquareOffTime`
- After partial booking, optionally trail the stop to breakeven

"Stay in trade as long as signal is on" is implemented as: **exit when the score no longer supports the position**, via score flip or score decay. This is the primary exit; stop loss is the safety net.

---

## 8. Strike Selection Module

Separate class behind an interface (`IStrikeSelector`).

**v1 logic:**

1. Direction from score → CE for bullish, PE for bearish
2. Filter tracked strikes for that side to those with premium in ₹150–200
3. Exclude: spread > configured % of mid, OI below floor, IV unavailable (null), no valid bid/ask quote at all
4. Rank remaining by a composite of liquidity (OI + volume) and delta, prefer delta in a sensible directional range
5. Return best candidate, or `null` (no trade — log the reason)

**Expiry choice:** default to nearest weekly, but switch to next weekly when nearest has under a configured number of days remaining.

---

## 9. Paper Trade Simulator

- Entry fill: **ask** price + configured slippage ticks (not LTP)
- Exit fill: **bid** price − slippage
- Costs applied per leg: brokerage, and an STT/charges approximation
- Positions persisted to database — must survive process restart
- Records: entry time/price/reason/score, partial exits, final exit, gross and net P&L, ruleset version, score-weights version

**Performance metrics tracked continuously:**

- Rolling win rate (20-trade and 50-trade)
- Average win / average loss, profit factor
- Max drawdown (rupees and %)
- Rolling Sharpe
- Trades per day, and **signals rejected per day with reasons**
- Performance bucketed by India VIX regime (low/mid/high tercile)

---

## 10. Backtesting

- Replays stored ticks through the identical feature → score → rule → simulator pipeline
- Same code path as live; only the tick source differs
- Reserve a hold-out period never used during tuning
- Report per-regime results, not just aggregate

**Guardrails against fooling yourself:**

- Win rate above ~65–70% on directional option buying is a red flag for overfitting, not a success
- Every tuning run must be recorded with its ruleset version and result
- Because data collection starts fresh, meaningful backtesting is realistically **8–12 weeks away**

---

## 11. Dashboard (Blazor Server)

- Live universal score with component breakdown
- Option chain view: strikes, premium, OI, IV, depth imbalance, buildup classification
- Active paper positions with live P&L
- Trade history and performance metrics
- **Kill switch toggle** (disables new entries; does not stop ingestion)
- Rules config editor with validation and version bump
- Data health panel: connection status, last tick age, gap log, warm-up status per metric
- FlatTrade token refresh flow

---

## 12. Notifications (Telegram)

Send on: trade entry, partial book, exit, kill-switch toggle, daily-loss circuit breaker trip, connection failure after 5 retries, token expiry, daily summary at 15:30.

**Do not** send on every score change. Add a per-category rate limit — an alert storm during a volatile session is how you learn to ignore alerts.

---

## 13. Testing Strategy

Priority order — these are the things that fail silently:

1. **Pricing:** BS prices and Greeks validated against a known reference calculator; IV solver round-trip test (price → IV → price)
2. **Z-score:** Welford implementation vs naive computation on the same data; clipping; warm-up behaviour
3. **OI classification:** table-driven tests for all four quadrants
4. **Rule engine:** each rule evaluated against constructed feature vectors, including boundary conditions
5. **Simulator:** fill logic, partial exits, cost application, P&L arithmetic
6. **Integration:** replay a recorded session end to end, assert deterministic identical output on repeat runs
7. **Recovery:** simulate a WebSocket disconnect mid-session, a DB outage, and a process crash with open positions — assert state recovers correctly per 1.6

---

## 14. Build Sequence

| Phase | Deliverable | Est. |
|---|---|---|
| 0 | Solution scaffold, config, Serilog, DB, Windows Service host | 1 week |
| 1 | FlatTrade auth + instrument master + WebSocket ingestion + persistence + reconnect | 2 weeks |
| 2 | Pricing engine (BS, IV, Greeks) + tests | 1 week |
| 3 | Feature engine + rolling z-scores + warm-up | 2 weeks |
| 4 | Universal scoring engine | 1 week |
| 5 | Rule engine + strike selection + paper simulator | 2 weeks |
| 6 | Dashboard + kill switch + Telegram | 2 weeks |
| 7 | Backtest harness | 1 week |
| 8 | Data accumulation, tuning, evaluation | ongoing, 8+ weeks |

**Phase 0 complete.** Phase 1 starts with the FlatTrade spike (15.5) before building the full ingestion pipeline.

---

## 15. Open Items to Revisit After 4 Weeks of Live Data

- Are score component weights sensible, or is one dominating?
- Are z-score window lengths right, or too noisy/too laggy?
- Is the ₹150–200 premium band producing enough liquid candidates on all days?
- Is 15-second cadence right?
- How often is `MaxConcurrentPositions` blocking good signals?
- Does depth imbalance add signal, or is it noise? (Be willing to drop a component.)
- Event-day calendar, shadow-mode ruleset testing, and conviction-scaled position sizing (1.9, deferred) — worth building once a baseline ruleset is running.

### 15.5 Pre-Phase-1 Spike (recommended, treat as a hard gate)

Before building the full ingestion pipeline: authenticate against FlatTrade, open the WebSocket, log 5 minutes of raw ticks for one Nifty option to a file. Validates the token flow and message format hands-on before the pipeline is built around assumptions from the docs. If the WebSocket message format or token flow surprises you, better to find out here than two weeks into the ingestion worker.

### 15.6 Regulatory Note (for later, not now)

SEBI's retail algo-trading framework has been tightening around API-based order placement. Since this system is paper-trading only, this doesn't block anything today. If it ever moves toward live order placement through the API, revisit SEBI's current rules at that point.

---

## 16. Honest Expectations

- The first ruleset almost certainly will not be profitable. That is normal and not a reason to abandon it.
- Directional option buying has structurally low win rates; profitability comes from payoff asymmetry, not hit rate.
- Any edge found will be regime-dependent and will decay. This system needs ongoing maintenance, not one-time construction.
- Paper trading systematically overstates real performance even with modelled costs, because it cannot capture execution reality, emotional deviation, or liquidity under stress.
- Phases 0–7 alone are roughly 12 weeks of build, then 8+ weeks of data/tuning before backtesting means anything — treat the output as research evidence, not as a signal to act on with real capital without much longer validation.
