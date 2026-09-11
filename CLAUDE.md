# NiftySignal — Working Agreement

Nifty 50 options directional-signal and paper-trading system. .NET 10 modular monolith,
PostgreSQL, running live (paper trading) on a Windows VM reached over Tailscale. Solo long-term
project — this file exists so every session starts with the same ground rules instead of
re-deriving them.

**Read these first, don't duplicate them here:**
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — the module map, what each project is for.
- [`docs/PLAN.md`](docs/PLAN.md) — original design intent, with its own "As-Built Status"
  section tracking where the live system has since diverged.
- [`docs/REVIEW_FINDINGS.md`](docs/REVIEW_FINDINGS.md) — the external-review tracker: findings
  from any outside review (a person, a friend, an AI), checked against actual current code
  before being trusted, with fixed/open status kept current.

## The one rule everything else follows

**Verify against the running code, not memory or a prior summary — every time.** This project
has had "obviously true" claims turn out to be stale more than once (a doc comment describing a
version of the code that no longer exists, a plan document describing intent that was later
revised). Read the actual file before asserting what it does. This applies equally to reviewing
someone else's claim about the code and to trusting your own recollection from earlier in a
session.

## Source is the ledger, not just the docs

Every valid, unfixed finding — from a review, from testing, from a live incident — gets a
grep-able comment at its code site:

```csharp
// PENDING (audit finding F53) — <what's wrong, one line>. See docs/REVIEW_FINDINGS.md.
// DEFERRED (audit finding F53) — <what's wrong and why it's deliberately not fixed yet>.
```

`grep -rn "PENDING (audit finding\|DEFERRED (audit finding"` is the authoritative, current
pending-items list — more current than any plan doc, since it's updated at the exact moment code
changes, not on a separate documentation pass. Numbering is sequential (check the highest `FNN`
in use with that grep before assigning a new one) and continues across both source comments and
`docs/REVIEW_FINDINGS.md` — they're one numbering space. When a finding is fixed, update or
remove the comment in the same change, and mark it fixed in `docs/REVIEW_FINDINGS.md` if it has
an entry there.

## Quant/scoring principles

- **No look-ahead.** A metric may only use data that would genuinely have been available at the
  timestamp it's computed for. This matters most in `NiftySignal.Backtest`/`NiftySignal.ScoreReplay`
  and anything that will eventually feed them — a metric that's fine live can silently leak
  future information in a replay if it isn't threaded through the same tick-by-tick ordering
  discipline `LiveFeatureEngine` already uses.
- **Explain a formula before changing it.** For any existing scoring/metric formula: state what
  it currently computes and why, identify the specific problem, propose the replacement, state
  the expected impact on the composite/ratio score's behavior — before writing the change. This
  project's audit-finding history (see `docs/REVIEW_FINDINGS.md`) is largely a record of doing
  exactly this, and it's caught real bugs the "just fix it" approach would have missed (e.g. F51
  wasn't a smoothing bug alone, it was a smoothing bug plus a redundant second smoothing pass
  found only by working through what each layer was actually doing).
- **Don't invent thresholds or scale constants.** Every `PENDING (F40)`-style placeholder
  constant in `RatioMetricScales`, every window length in `FeatureWindowLengths`, is flagged as
  provisional until derived from real live data — that discipline should extend to any new
  constant, not just the ones already flagged. State plainly when a number is a starting guess.
- **A metric's *sign* is a hypothesis until tested, not a textbook default.** Several existing
  components (PCR, IV skew) are deliberately left with `PENDING (audit finding FNN)` comments
  saying their directional sign is unresolved. Do not "fix" a sign based on intuition — that's
  exactly the failure mode `docs/REVIEW_FINDINGS.md`'s backtester section (F32) exists to
  prevent. If a sign genuinely needs settling, it needs the backtester or a live correlation
  check, not a judgment call.
- **The ratio composite (`Ratio*` columns, `NiftySignal.Scoring/RatioScoreCalculator.cs`) is
  structurally inert to real trades** — `LiveTradingEngine.EvaluateCadenceAsync` reads only
  `ScoreSnapshot.CompositeScore`, confirmed by
  `LiveTradingEngineTests.EvaluateCadenceAsync_IgnoresRatioCompositeScore_...`. Keep it that way
  until there's a deliberate decision to wire it in — don't let a refactor accidentally add a
  read of `RatioCompositeScore` into the trading path.

## Risk

Risk/capital gates in `NiftySignal.Rules/EntryRuleEvaluator.cs` and
`NiftySignal.Host/LiveTradingEngine.cs` take priority over signal quality — a stronger score
never bypasses `MaxConcurrentPositions`, the capital-sum check (F48), `MaxDailyLossPct`, or the
kill switch. If a change to entry/exit logic could weaken any of these under some input, say so
explicitly before implementing, even if the change's main purpose is unrelated.

## Working process

- All work happens on a feature branch (currently `NiftyRatio`), never directly on `main`.
- Only commit when explicitly asked. When asked, end commit messages with:
  `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>`
- `dotnet build` and `dotnet test` clean (0 warnings, all tests passing) before considering any
  C#/F# change done — this project's test suite is fast (well under a second), there's no excuse
  to skip it.
- New behavior gets a test in the same change, following the existing style: real
  Black-Scholes-consistent option pricing for anything IV/Greeks-related (a hand-rolled linear
  price ladder doesn't round-trip through the IV solver), `EF Core InMemoryDatabase` for
  `LiveTradingEngine`/DB-backed tests (see `LiveTradingEngineTests.Fixture`), deterministic
  seeded `Random` where jitter is needed.
- Don't rush a large, correctness-sensitive refactor in one pass. Batch related, mechanical
  fixes together; give a structurally risky change (anything touching `LiveTradingEngine`'s
  entry/exit path, or a scoring formula) its own pass with its own verification.
- The deployed system is a Windows Service on a VM, reached over Tailscale
  (`100.105.67.79`), via `deploy.ps1`. Prefer verifying a deploy directly (service status,
  binary timestamps, tailing `logs/niftysignal-*.log` for errors, checking the Dashboard) over
  assuming it worked. Avoid triggering a redeploy or restart during live market hours for
  anything short of a real bug fix — a restart resets in-memory warm-up state.
- Local dev uses a separate Postgres database from the VM's — never assume they're in the same
  state. Check which one a claim is actually based on.

## What NOT to do

- Don't guess at a live value (a metric's sign, a threshold, whether a bug is "probably" fixed)
  when it's checkable — check it.
- Don't add speculative abstractions, unused configuration knobs, or "might need this later"
  code. This project's existing patterns (rolling windows, FIFO smoothing, the optional/required
  component split) already cover most new needs — reuse them before inventing a new mechanism.
- Don't silently expand the scope of a requested fix. If fixing the requested thing surfaces a
  second, related problem, say so and ask, rather than fixing both without flagging it (unless
  it's the same fix, like a stale reference to something you just renamed).
