# NiftySignal — Architecture

Full project rationale, locked decisions, and domain design live in [`docs/PLAN.md`](docs/PLAN.md). This file is the map of the code: what each project is for, and how they're allowed to depend on each other.

## Why a modular monolith

Solo developer, one machine, running 24/7. Splitting ingestion/features/scoring/execution into separate services buys nothing here and costs deployment complexity, inter-process serialization, and debugging pain. Instead: one solution, one host process, multiple class libraries with enforced one-directional dependencies — so the system *could* be split later if it ever needed to, without paying distributed-systems tax now. See plan section 1.4.

**The rule that keeps this true:** every cross-module dependency goes through an interface defined in the *consuming* project (or `Domain`), never a concrete type reached by referencing the implementing project directly. `ITickSource` is the canonical example — live ingestion and backtest replay both implement it, and the feature/scoring/rule code never knows or cares which one is running (plan section 1.5, "backtest/live parity"). If a module ever needs to reach directly into another module's implementation type, that's a sign the interface boundary is missing, not a reason to add a project reference.

## Module map

| Project | Responsibility |
|---|---|
| `NiftySignal.Domain` | Entities, value objects, enums, shared config POCOs (e.g. `KillSwitchOptions`). Zero dependencies — every other project can depend on this one. |
| `NiftySignal.Pricing` | Black-Scholes, IV solver, Greeks. |
| `NiftySignal.Features` | Rolling stats (Welford z-scores), OI buildup classification, warm-up tracking. |
| `NiftySignal.Scoring` | Composite universal directional score. |
| `NiftySignal.Rules` | Rule engine (NCalc), ruleset config, hot-reload + validation. |
| `NiftySignal.Ingestion` | FlatTrade REST/WebSocket client, instrument master, tick demux. All FlatTrade-specific mapping is isolated here — nothing outside this project should see a FlatTrade type. |
| `NiftySignal.Persistence` | EF Core `NiftySignalDbContext`, `IEntityTypeConfiguration<T>` per entity, migrations, repositories. |
| `NiftySignal.Notifications` | Telegram alerts, with per-category rate limiting. |
| `NiftySignal.Execution` | Strike selection, paper trade simulator, position tracking. |
| `NiftySignal.Backtest` | Replay harness — implements `ITickSource` over stored ticks, running the same pipeline as live. |
| `NiftySignal.Host` | Worker Service composition root. Wires DI, hosts `BackgroundService` workers, runs as a Windows Service. |
| `NiftySignal.Dashboard` | Blazor Server UI — kill switch, live score, option chain, trade history, config editor. |
| `NiftySignal.Tests` | xUnit. Mirrors the module layout (`Tests/<Module>/...`), naming convention `MethodName_Scenario_ExpectedResult`. |

## Project-reference graph

```
Domain
  ├─ Pricing
  │    └─ Features
  │         └─ Scoring
  │              └─ Rules
  │                   └─ Execution ◄── Pricing
  ├─ Ingestion
  ├─ Persistence
  └─ Notifications

Backtest    → Domain, Persistence, Features, Scoring, Rules, Execution, Ingestion
Host        → Domain, Ingestion, Persistence, Notifications, Pricing, Features, Scoring, Rules, Execution
Dashboard   → Domain, Persistence, Execution, Rules
Tests       → everything
```

`Persistence` never appears on the left side of that graph except from `Host`/`Dashboard`/`Backtest`/`Tests` — no domain-logic project (`Pricing`, `Features`, `Scoring`, `Rules`, `Execution`) references it directly. When those modules need to read or write data, they depend on a repository interface (defined alongside them, or in `Domain`), and `Persistence` supplies the implementation, wired up in `Host`'s composition root. This keeps `Persistence` swappable and keeps EF Core out of every project that doesn't need it.

## Build-time conventions

- **Central package management** (`Directory.Packages.props`) — every NuGet version is declared once at the solution root; individual `.csproj` files reference packages by name only.
- **Shared build settings** (`Directory.Build.props`) — `net10.0`, nullable reference types on, nullable warnings promoted to errors. A `null` in this codebase is usually meaningful (non-convergent IV, a degraded feature window, missing depth) rather than an oversight, so the compiler is set to never let one pass silently.
- **`.editorconfig`** — naming conventions (`_camelCase` private fields, `Async`-suffixed async methods), file-scoped namespaces, brace style.
- Secrets never live in `appsettings.json` — that file holds the config *shape* only (empty values), and real values go in `appsettings.Local.json`, which is gitignored. See plan section 1.1.

## Where things run

`NiftySignal.Host` is the only long-running process — a Windows Service (`AddWindowsService()`), auto-restarting on failure, self-provisioning its database schema on startup (`context.Database.Migrate()`) so a fresh machine needs no manual setup step. `NiftySignal.Dashboard` is a separate ASP.NET Core process (Blazor Server) that reads/writes the same database and config files; it does not run the trading pipeline itself.
