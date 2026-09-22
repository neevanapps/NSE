# Sensex / Bank Nifty raw tick collection (2026-09-22)

**Purpose:** start persisting raw Sensex/Bank Nifty ticks into the same tables Nifty's own live
pipeline already uses, for **future backtesting only**. Pure write-only/archive addition -- no
scoring, no volume bars, no rank trackers, no paper trading for these two indices, ever. This is a
two-stage change; see `docs/REVIEW_FINDINGS.md`'s 2026-09-22 F61 entry for Stage 1 (the
`Instruments` table Underlying-ambiguity fix that made Stage 2 safe to build). This document covers
both, briefly, with Stage 2 as the main subject.

## What was built

1. **`NiftySignal.Ingestion/FlatTrade/SensexBankNiftyInstrumentMasterProvider.cs`** -- a sibling of
   `FlatTradeInstrumentMasterProvider`, not a modification of it. Downloads the same public NFO
   index-derivatives CSV Nifty's own provider already downloads (Bank Nifty lives in that same
   segment) plus a new public BFO index-derivatives CSV (Sensex), and filters each with
   `FlatTradeInstrumentMasterProvider.ParseNiftyInstruments` **unchanged** -- that method was
   already fully parameterized on `underlying` despite its name, so reusing it needed zero edits
   to the file Nifty's own provider lives in.
2. **`NiftySignal.Ingestion/FlatTrade/SensexBankNiftyInstrumentUniverseResolver.cs`** -- a sibling
   of `InstrumentUniverseResolver`, not a generalization of it and not called by it. Resolves, per
   index, the single CURRENT expiry's option chain (ATM+/-10, matching Nifty's own band width) plus
   the nearest future, using the future's own quote (not a spot-index quote -- see below) as the
   option chain's ATM-centering price.
3. **`NiftySignal.Host/Program.cs`** -- two new DI registrations (`SensexBankNiftyInstrumentMasterProvider`,
   `SensexBankNiftyInstrumentUniverseResolver`), added as their own concrete types, not bound to the
   shared `IInstrumentMasterProvider` interface Nifty's own resolver uses -- no existing
   registration line was touched.
4. **`NiftySignal.Host/MarketDataIngestionWorker.cs`**:
   - `ResolveInstrumentsAsync` now resolves/reuses NIFTY and SENSEX/BANKNIFTY independently (see
     "Nifty-regression evidence" below for why NIFTY's own outcome is unchanged). A SENSEX/BANKNIFTY
     resolution failure is caught and logged; it never blocks or fails NIFTY's own resolution.
   - `RunTradingSessionAsync`'s existing subscription list (`instruments.Select(i => (i.Exchange, i.Token))`)
     and the existing `FlatTradeTickSource` construction are **untouched** -- the combined
     instrument list (now including Sensex/Bank Nifty) flows into the exact same subscribe/flush
     code that already existed, additively.
   - New `RunMonitoringLoopAsync` (see "Monitoring" below).

### Design choice: parameterize vs. sibling

Sibling, for both the resolver and the master provider. `InstrumentUniverseResolver` and
`FlatTradeInstrumentMasterProvider` are **not edited at all** in this change (`git diff` on those
two files for Stage 2 is empty) -- the only way to make Nifty's own resolution "provably
byte-identical" was to never touch the code path it runs through. The cost is some duplicated
shape (the option-chain-around-a-future-quote pattern), accepted deliberately over the risk of a
shared parameterized method's edge case leaking between the two call sites.

### Design choice: no spot-index subscription

The task's own spec says "options + future if available" for these two indices, not spot. Verified
live 2026-09-22 that FlatTrade's public NFO/BFO index-derivatives CSVs don't carry spot-index rows,
and the equity/index CSV filenames that might (guessed from `InstrumentUniverseResolver`'s own
`NiftySpotToken` doc comment, e.g. `NSE_Equity.csv`, `BSE_Equity.csv`) returned S3 `AccessDenied`
when fetched live the same day -- likely gated behind auth this task has no reason to acquire just
for this. Each index's own nearest future's own quote (`GetQuotes`) is used as the ATM-anchor price
for `GetOptionChainAsync` instead of a spot quote -- economically close (small, well-understood
basis) and already data this class needs to fetch for the future leg regardless.

## Expiry-scoping proof

Verified against **real, live FlatTrade scrip master data**, fetched directly on 2026-09-22:

- `https://flattrade.s3.ap-south-1.amazonaws.com/scripmaster/Nfo_Index_Derivatives.csv` (public, no
  auth) -- confirmed BANKNIFTY rows present (same file Nifty's own provider already downloads).
- `https://flattrade.s3.ap-south-1.amazonaws.com/scripmaster/Bfo_Index_Derivatives.csv` (public, no
  auth, newly used by this task) -- confirmed SENSEX rows present.

**Sensex (BFO) distinct option expiries found, in order:** 24-SEP-2026, 01-OCT-2026, 08-OCT-2026,
15-OCT-2026, 22-OCT-2026, 29-OCT-2026, 05-NOV-2026, 26-NOV-2026, 31-DEC-2026, then quarterly/longer-
dated rows out to 2031. **Weekly-shaped**, as expected. Current expiry as of 2026-09-22:
**24-SEP-2026**. The resolver takes `expiries[0]` after `OrderBy(d => d)` and never touches
`expiries[1..]` -- proven in `SensexBankNiftyInstrumentUniverseResolverTests.ResolveAsync_ResolvesOnlySensexCurrentWeeklyExpiry_NeverNextWeek`,
which asserts the next expiry never appears in the resolved instruments *and* that
`GetOptionChainAsync` is never even called for it (only one chain request is made per index).

**Bank Nifty (NFO) distinct option expiries found:** 29-SEP-2026, 27-OCT-2026, 23-NOV-2026,
29-DEC-2026, 30-MAR-2027, 29-JUN-2027. **No weekly rows exist at all** in the current live master --
every gap between consecutive expiries is 4+ weeks. This directly answers the task's own question
("verify whether Bank Nifty still has monthly expiries at all, and report clearly if it doesn't
rather than substituting nearest-weekly silently"): **yes, Bank Nifty still has monthly expiries,
and as of 2026-09-22 it has *only* monthly expiries** -- there is no weekly-vs-monthly ambiguity to
resolve today, since taking "the nearest expiry" and taking "the current monthly expiry" are
identical operations right now. Current expiry as of 2026-09-22: **29-SEP-2026**. Proven the same
way as Sensex in `ResolveAsync_ResolvesOnlyBankNiftyCurrentMonthlyExpiry_NeverNextMonth`.

**Tripwire, not a gate:** `SensexBankNiftyInstrumentUniverseResolver.LogExpirySpacing` logs a
`WARNING` if Bank Nifty's nearest two expiries are ever found less than 20 days apart (weekly rows
returning) -- diagnostic only, never changes which expiry gets resolved (always the nearest one).
This is a deliberate exception to "no hardcoded thresholds for backtesting rules": it gates nothing
and decides nothing, it only tells a human to re-check the "monthly-only today" assumption above
before trusting it in the future.

## Same-WebSocket proof

`grep -rn "new FlatTradeTickSource" --include=*.cs .` (excluding tests) returns exactly one match,
unchanged from before this task: `MarketDataIngestionWorker.cs`'s existing `RunTradingSessionAsync`.
Neither new file (`SensexBankNiftyInstrumentMasterProvider`, `SensexBankNiftyInstrumentUniverseResolver`)
references `FlatTradeTickSource` at all -- both only do plain HTTP (scrip master CSV downloads,
`GetQuotes`, `GetOptionChain`), never WebSocket. Sensex/Bank Nifty tokens reach the feed purely by
being added to the same `subscriptions` list `RunTradingSessionAsync` already builds from the
combined `instruments` result and hands to the single `FlatTradeTickSource` constructor call.

## No-downstream-leakage proof

Re-checked with Stage 2's actual changes in place (not just Stage 1's fix in isolation), per the
coordinator's explicit request to confirm the Underlying filter is *sufficient on its own*:

- **`LiveFeatureEngine`**: constructor filters `instruments` to `NiftyUnderlying` internally
  (F61, Stage 1) *before* computing `_spot`/`_future`/`_nearestExpiry`/`_nearestExpiryOptions` --
  sufficient regardless of what `MarketDataIngestionWorker` now passes in (the full combined list,
  post Stage 2). `OnTick` receives every tick from the shared feed unconditionally (`_latest[tick.Token] = ...`
  for all tokens, harmless dictionary growth) but its dispatch chain (`tick.Token == _future.Token`
  / `== _spot.Token` / `_coreDepthImbalanceByToken.TryGetValue`) only ever matches NIFTY-filtered
  tokens, so Sensex/Bank Nifty ticks fall through untouched. Proven with real Stage-2 code active:
  `LiveFeatureEngineTests.Constructor_WithMixedUnderlyingInstruments_ComputesCadenceIdenticallyToNiftyOnlyUniverse`
  passes, and a live replay of 2026-09-16's real ticks through `NiftySignal.ScoreReplay` produced
  the exact same composite-score distribution as Stage 1's baseline (see below).
- **`LiveVolumeBarWriter`** (-> `LiveVolumeBarPopulator`, `LiveOptionAtmPopulator`,
  `LiveOptionMaxPainPopulator`, `LiveOptionDepthPopulator`): each queries `NiftySignalDbContext.Instruments`
  directly (not through `LiveFeatureEngine`), so the Underlying filter added in each one (F61,
  Stage 1) *is* the thing keeping Sensex/Bank Nifty out here -- confirmed sufficient by
  `LiveVolumeBarPopulatorUnderlyingFilterTests` and by re-running `replay-live-papertrade 2026-09-16`
  with Stage 2's code active: identical `PARITY: PASS`, all 13 trades matching byte-for-byte the
  same entry/exit bars, strikes, and fill prices as Stage 1's baseline run.
- **`LiveOptionsScoreEngine`** / **`LiveFuturesCrossoverEngine`**: `grep -n "db\.Instruments"` over
  both files returns no matches -- neither queries `Instruments` at all. They read exclusively from
  `VolumeBarDbContext` (`VolumeBars`, `OptionAtmBars`, `OptionDepthBars`, `OptionMaxPainBars`), which
  can only ever be populated by the four populators above -- already proven NIFTY-only. Safety here
  is transitive, not direct.
- **`CoreScoreHysteresisTradingEngine`** / **`CoreScoreCrossoverTradingEngine`**: touch instruments
  only via `featureEngine.FindInstrument(token)` / `featureEngine.TryGetLatestQuote(token)`, both of
  which read `LiveFeatureEngine`'s own NIFTY-filtered `_instruments`/`_latest` -- `FindInstrument`
  returns `null` for any Sensex/Bank Nifty token (proven directly in
  `Constructor_WithMixedUnderlyingInstruments_OnlyResolvesNiftyOwnInstruments`), so neither engine
  can ever act on one.

**Conclusion: the Underlying filter added in Stage 1 is sufficient on its own** -- no further
downstream change was needed for Stage 2. This was re-verified with Stage 2's actual resolver/
worker changes in the tree, not assumed to still hold from Stage 1 alone.

## Nifty-regression evidence (Stage 2)

- `dotnet build`: 0 warnings, 0 errors.
- `dotnet test`: **673/673 passing** (669 after Stage 1 + 4 new expiry-scoping tests for the Stage 2
  resolver).
- Real local data (`niftysignal` database, 2026-09-16, 4,297,937 real ticks) re-run with Stage 2's
  code in place:
  - `NiftySignal.ScoreReplay -- 2026-09-16`: 1641 cadences, 1502 scored, `|CompositeScore|`
    distribution `p50=25.0 p75=39.0 p90=54.0 p92.5=58.2 p95=61.2 max=81.8` -- identical to Stage 1's
    pre/post-fix baseline.
  - `NiftySignal.VolumeBarData -- replay-live-papertrade 2026-09-16`: `PARITY (2026-09-16): PASS`,
    13/13 trades matched (entry bar, strike, direction, exit reason, exit bar, fill prices) --
    identical to Stage 1's baseline.
- **Honest limits**: these local runs used a local `niftysignal` database whose `Instruments` table
  does not yet contain any real Sensex/Bank Nifty rows (`MarketDataIngestionWorker` has never run
  against it with this code) -- so this specifically proves "the code path is unaffected when only
  NIFTY rows exist" and "the resolver/populator logic correctly ignores a second underlying" (via
  the InMemory-database unit tests, which *do* construct exactly that two-underlying scenario).
  It does **not** prove behavior against a live day where the real ingestion worker has actually
  written Sensex/Bank Nifty rows into the same live Postgres table Nifty depends on. **Recommended
  before/soon after this deploys**: run `verify-parity`/`verify-parity-selftest` against a live
  trading day *after* Stage 2 has run at least once and written real Sensex/Bank Nifty rows into
  `Instruments`/`Ticks`, and confirm it still passes -- this is the one proof that genuinely can't
  be done locally without live FlatTrade market hours and a real session token.
- No deployment was performed. All verification above is local build/test/replay only.

## Monitoring

Log lines only, per the spec ("not a new dashboard panel or database table"), emitted once a
minute by `MarketDataIngestionWorker.RunMonitoringLoopAsync`:

```
Ingestion monitoring (2026-09-22): ticks/sec [NIFTY=42.3/s, BANKNIFTY=18.1/s, SENSEX=11.4/s],
flushes=58 avgFlushMs=6.2, Ticks table size=812.4MB (+3.10MB since session start)
```

- **Ticks/sec per index**: a token-to-Underlying snapshot is built once per trading session from
  the day's resolved instrument list (NIFTY + SENSEX + BANKNIFTY together) and used only to
  attribute tick counts for this log line -- never for filtering or routing. A token not in the
  snapshot (e.g. a Dashboard on-demand watch subscribed mid-day) is counted under `UNKNOWN` rather
  than causing an error.
- **Flush duration**: `FlushAsync`'s existing `SaveChangesAsync` call is timed with a `Stopwatch`;
  count and total duration accumulate between reports and reset each cycle, so the log shows an
  average, not a running total.
- **Disk growth per day**: `pg_total_relation_size('"Ticks"')` queried once per monitoring cycle;
  the first successful read each session becomes the baseline, and every later line reports the
  delta against it.
- All of this is best-effort and wrapped the same way every other loop in `MarketDataIngestionWorker`
  already is (audit finding F34's discipline) -- a monitoring-cycle failure is logged and skipped,
  never allowed to affect ingestion itself.

## Zero-scoring proof

`grep -rln "SENSEX\|BANKNIFTY" --include=*.cs NiftySignal.Scoring NiftySignal.Rules` returns no
matches -- neither underlying's name appears anywhere in the scoring or rules projects. The only
code that knows about Sensex/Bank Nifty at all is the resolver/master-provider pair above and the
monitoring log line in `MarketDataIngestionWorker`; everything else treats their ticks as inert rows
in the shared `Ticks` table.
