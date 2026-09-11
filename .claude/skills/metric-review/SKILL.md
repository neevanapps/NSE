---
name: metric-review
description: Systematic review of one quantitative scoring metric in the NiftySignal codebase (composite score or ratio composite) — its formula, inputs, normalization, look-ahead risk, duplication with other components, edge-case handling, and whether its directional sign is actually established or just assumed. Use this whenever the user asks to "review", "check", "audit", or "look into" a specific metric or component by name (e.g. "review the momentum score", "check RatioSizedOiFlowRaw", "is OiBuildupNet still broken", "audit the IV skew calculation"), even if they don't use the word "review" explicitly — a request to look critically at one named signal should trigger this rather than an ad-hoc read of the file.
---

# Metric review

A repeatable methodology for reviewing one metric, matching the discipline that already found
real bugs in this codebase (F46 z-score self-inclusion, F50 the OI refresh-cadence mismatch, F51
the ratio panel's missing per-metric smoothing — see `docs/REVIEW_FINDINGS.md`). The point of
having a fixed checklist is that a metric review shouldn't depend on which issues happen to be
top of mind that day — read every item below against the real code, every time.

This is a **review skill, not a fix skill**. Report findings; don't implement a fix unless the
user has separately asked for one. See `CLAUDE.md` for why (formula changes need explanation and
approval before implementation in this project) and for the standing rules this checklist exists
to enforce — read it before starting if you haven't already this session.

## Before you start

Identify which pipeline the metric belongs to — the two are independent and differently weighted:
- **Original composite** (`NiftySignal.Scoring/CompositeScoreCalculator.cs`,
  `ScoreWeights.Default`, `NiftySignal.Features/FeatureWindowLengths.cs`) — the one that's
  actually wired to `LiveTradingEngine`.
- **Ratio composite** (`NiftySignal.Scoring/RatioScoreCalculator.cs`,
  `RatioScoreWeights.Default`, `NiftySignal.Features/RatioMetricScales.cs`) — structurally inert
  to trading (see `CLAUDE.md`), an experimental sidecar.

Find the metric's raw-value computation in `NiftySignal.Host/LiveFeatureEngine.cs` (search for
its name — most are a `ComputeXxx` method) before doing anything else. Everything below is
checked against that method and its call site, not against a description of it.

## The checklist

Work through all eight. Skipping one because it "obviously doesn't apply" is exactly how a real
issue gets missed — say briefly why it doesn't apply instead of omitting it.

1. **Formula and inputs.** Read the actual computation. List every input it touches: raw ticks,
   OI, an IV solve, another already-computed metric, a persisted `_previousXxx` baseline. Quote
   the real code (`file:line`), don't paraphrase from memory.

2. **Role and weight.** What is it supposed to measure, in plain language? Is it currently
   required or optional (`CompositeScoreCalculator.IsOptional` / the ratio composite's
   `MinRequiredComponents` renormalization)? What's its weight in `ScoreWeights.Default` /
   `RatioScoreWeights.Default` right now — a diagnostic-only 0.0 weight changes what a bug in it
   actually costs.

3. **Look-ahead.** Does any input reflect information that wouldn't genuinely have existed yet at
   the cadence's own timestamp? This matters most for anything that will eventually feed
   `NiftySignal.Backtest`/`NiftySignal.ScoreReplay` — a metric that's fine live (wall-clock `now`
   always equals "the present") can silently leak future information once it's replayed against
   historical ticks out of real-time order. Check whether it depends on `DateTimeOffset.UtcNow`
   directly versus the `now`/tick-timestamp values already threaded through the method.

4. **Normalization and scale.** How does the raw value get turned into something comparable
   across metrics — clipped log-ratio, z-score against a rolling window, a scaled difference?
   Is the window length (`FeatureWindowLengths.*` or `RatioMetricScales.*`) something derived
   from real behavior, or an unvalidated placeholder (check for a `PENDING (audit finding FNN)`
   comment nearby — if there isn't one and the constant looks like a guess, that's itself a
   finding)?

5. **Duplication.** Does this metric read materially the same underlying signal as another
   already-weighted component? The known cluster to check against: several components derive
   from OI (`OiBuildupNet`, `GammaExposure`, `VannaExposure`, `CharmExposure`), and a couple
   derive from the futures/synthetic-forward price (`FuturesBasis`, `PriceMomentum`). A new or
   changed metric that overlaps one of these isn't automatically wrong, but the overlap should be
   named, not silently ignored.

6. **Edge cases.** Walk through: a quiet bar (no real signal this cadence — does it correctly
   return `null`, or fabricate a `0`?), a feed gap or reconnect, a Host restart (is there a
   `SeedHistory` replay path for it, or does it silently reset warm-up?), and — for anything
   ATM-strike-relative — a strike roll. "Returns null" is the correct answer for "no trustworthy
   data this cadence"; a fabricated value that looks plausible is the dangerous failure mode,
   harder to notice than an exception.

7. **Sign.** Does a higher raw value mean bullish or bearish, and is that empirically
   established or just assumed from how the metric "should" behave? If it's assumed, say so
   plainly and do **not** propose flipping it based on intuition — per `CLAUDE.md`, a sign change
   needs the backtester (`docs/REVIEW_FINDINGS.md`'s F32) or a real live correlation check, not a
   judgment call. Several components already carry a `PENDING (audit finding FNN)` comment
   admitting this; check for one before assuming this metric is any more settled than those.

8. **Report.** For each issue found, give: current behavior (with `file:line`), the problem, why
   it matters (what it actually does to the score, not just "this looks off"), a proposed fix,
   and your confidence. Match `docs/REVIEW_FINDINGS.md`'s existing style — it's the ledger this
   review should be able to slot into directly if the user decides to track a finding. If nothing
   is wrong, say so plainly; a clean review is a valid and useful outcome, not a failure to find
   something.
