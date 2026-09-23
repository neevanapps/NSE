# Price-Based Findings

**Scope of this file**: everything from the 2026-09-23-onward "this session is completely based on
Price" work -- the option-price fast/slow crossover engine (`PriceCrossoverEngine`/
`TradeSimulator.SimulatePriceCrossoverDayAsync`, CLI: `price-crossover`/`price-crossover-calibrate`/
`mae-mfe price-crossover`) and whatever new price-only strategies get tried from here on. This is
the intended SOURCE OF RECORD for the real experiments in this track, kept separate from
`docs/VOLUME_BAR_FINDINGS.md` (which covers this project's much broader volume-bar/metric-trial
research and is getting large) so this session's price-based work stays easy to find and read start
to finish.

Same discipline as `docs/VOLUME_BAR_FINDINGS.md`: every entry states what was tested, the actual
numbers, an explicit conclusion (even when negative), and honestly-noted scope cuts. Every
"established winner" referenced anywhere in this file traces back to the original SMA grid work in
`docs/VOLUME_BAR_FINDINGS.md`'s "Standalone Call/Put-price crossover" and "650-bar sweep" sections
(2026-09-22), moved forward from there.

**Standing rules for this track** (see project memory for the full reasoning):
- **No single fixed rule** -- design for regime-conditioned/switchable rules and adaptive window
  sizing, not one globally-locked fast/slow/threshold triple. 0-DTE and non-0-DTE are the one axis
  already conditioned on; more may follow.
- **No risk management (SL/TP1) while still establishing edge** -- `SimulatePriceCrossoverDayAsync`
  has no stop-loss/take-profit logic by design; leave it that way until a signal is confirmed to
  have real edge, then design risk deliberately as a separate step.
- **Entry strike premium band [100, 150], FIXED (locked 2026-09-23)** -- `--minprice=100
  --maxprice=150` on every CLI command in this track, no longer a variable to sweep. This was a gap
  until 2026-09-23 (see below); anything dated before that in this file, or carried over from
  `docs/VOLUME_BAR_FINDINGS.md`, was ATM-only unless stated otherwise.
- **Signal-price band composition FINALIZED as ATM-only (locked 2026-09-23)** -- no `--band=`/
  `--bandmode=` flag on any command in this track. Symmetric (ATM+/-N) and one-sided (ItmSide/
  OtmSide) band averaging were both swept against the two established winners and neither beat
  plain single-ATM signal pricing once win rate was weighed against sample size and MAE/MFE
  together (see "Locking [100,150] entry band; finalizing SIGNAL-price band composition" below).
- **No fixed bar size or window across regimes/sessions (reaffirmed 2026-09-23)** -- explicitly not
  looking for one global winner; find what works best per DTE regime AND per session-of-day, drill
  down further whenever a regime split suggests it.
- **Trades/day target: 5-15, the standing ideal for both simulation and live (set 2026-09-23)** --
  replaces the earlier 5-20/5-10 targets used at various points above.
- **Every sweep reports top-5-by-metric (Win rate, Net, MFE%, MAE%), split by DTE regime** -- not a
  single pre-chosen "winner." `price-crossover-calibrate` now does this automatically (see below).

> **IMPORTANT NOTICE (2026-09-23), read before trusting any number above this line**: every SMA/EMA/
> band-composition result recorded ABOVE this notice (the "Standalone Call/Put-price crossover"
> section carried over from `docs/VOLUME_BAR_FINDINGS.md`, the EMA-vs-SMA comparison, the
> [100,150]-band re-verification, the band-composition sweep) was measured using a signal that could
> silently mix two different option contracts' premiums within one fast/slow window (the rolling-ATM
> splicing issue -- see "Correctness review" and "Investigating the zero-trade result" below). Direct
> measurement (not inference) shows the overwhelming majority of the threshold-clearing "crossings"
> driving those results were an artifact of that splicing, not genuine single-contract momentum.
> **These results are PRESERVED BELOW for audit/history and must NOT be cited as evidence of edge
> going forward.** Nothing has been deleted or edited -- per instruction, they stay exactly as
> originally recorded. The corrected-signal investigation and current recommendation are in
> "Investigating the zero-trade result" near the end of this file.

---

## EMA vs SMA on the TRADEABLE option-price crossover engine (2026-09-23)

**User's explicit request**: every price-crossover backtest run so far (the two sections above)
used SMA only -- `MaSpreadEngine`'s EMA support was wired only into the PREDICTIVE-only
correlation-research path (`ma-spread-research`/`momentum-relationship`), never into the actual
tradeable P&L simulator. This task adds real EMA support to the trading path itself and reports a
genuine win-rate/net/MAE-MFE comparison, not another correlation number.

**New code**: `PriceCrossoverEngine` (`PriceCrossoverCalculator.cs`) now takes optional
`MaType fastType = Sma, MaType slowType = Sma` constructor params (reusing the same `MaType` enum
`MaSpreadEngine` already defines) -- default SMA/SMA reproduces the class's original behavior
bit-for-bit (confirmed both by a new unit test comparing the 2-arg and explicit-Sma constructors
bar-for-bar, and by re-running the established Put 2/10/5%@2600 baseline below, which reproduced
exactly: 54 trades, 72.2% win, +254.85 net). EMA seeding/recursion is copy-identical to
`MaSpreadEngine.Observe`'s own convention. `PriceCrossoverEngine` was previously kept SMA-only
deliberately, per an EARLIER task's own rule that it stay unchanged while `MaSpreadEngine` was
built alongside it for correlation research only -- that constraint doesn't apply here, since this
task explicitly wants a tradeable EMA backtest, and extending the existing crossing-detection class
avoids duplicating the whole entry/exit/P&L simulator a second time around `MaSpreadEngine`.
`TradeSimulator.SimulatePriceCrossoverDayAsync` gained the same two params (applied uniformly to
both the Call and Put engines -- no separate per-side type override, a documented scope decision,
not yet a tested need). CLI: `price-crossover`/`price-crossover-calibrate`/`mae-mfe price-crossover`
all gained `--fasttype=Sma|Ema`/`--slowtype=Sma|Ema` (default Sma/Sma). `dotnet build
NiftySignal.slnx`: 0 warnings/0 errors. `dotnet test NiftySignal.slnx`: 763/763 passing (758
baseline + 5 new EMA-specific tests in `PriceCrossoverEngineTests.cs`, no regressions). No
production file (`NiftySignal.Host`, `NiftySignal.Dashboard`, `NiftySignal.Rules`,
`LiveTradingEngine`) touched.

**Hypothesis stated before running, per this project's own "explain before changing" discipline**:
0-DTE option premium moves fast and decays fast (extreme gamma, brutal theta in the final hours),
so a MORE responsive signal (EMA, which weights the newest print more heavily than an SMA of the
same N) should have more payoff there than on calmer non-0-DTE days, where a steadier signal that
doesn't whipsaw is more valuable than raw speed. Tested directly below, not assumed.

### Put side, bar=2600 (the established non-0-DTE venue), EMA/EMA, same 8-day range as the recorded SMA grid

At the exact SMA-winning window (fast=2/slow=10/thr=5%), EMA is worse on every axis: 48 trades,
60.4% win, **+183.85 net**, MAE 7.45%/MFE 8.77% (1.18x favorable) -- versus SMA's own 54 trades,
72.2% win, +254.85 net, 1.30x ratio at the identical window. EMA's own best real-sample cell within
its full grid sweep (fast 2-10 x slow 10-50) is this SAME window -- no EMA cell anywhere in the
grid beats the SMA winner on win rate AND MAE/MFE together.

**Non-0-DTE specifically, uniform and clear-cut**: every real-sample EMA cell at bar=2600 nets
NEGATIVE for non-0-DTE days (best -9.60 to -56.05, worst -137.80), against SMA's non-0-DTE winner
(2/10: 63.6% win, +75.35 net). This is not a close call anywhere in the grid.

**0-DTE specifically**: EMA's best real-sample cell (2/10, n=26) gives 73.1% win, +193.45 net --
lower win rate than SMA's own 0-DTE winner at this bar size (2/40: 89.3% win, +147.65 net) but a
higher net. Mixed, not a clean win either way.

### Put side, bar=650 (the established 0-DTE venue), EMA/EMA

Best real-sample 0-DTE cell: **fast=4/slow=10/thr=5%** -- 16 trades, 75.0% win, +181.20 net, MAE
7.28%/MFE 15.80% (2.17x favorable). Compared to the established SMA 0-DTE winner at the same bar
size (650, 4/40/5%: 21 trades, 76.2% win, +159.10 net, 2.5x ratio): **essentially tied** -- EMA
fires fewer times for a similar win rate, a modestly higher net-per-trade (11.33 vs 7.58), on a
slightly weaker but still favorable MAE/MFE ratio. Neither dominates the other here. Non-0-DTE at
bar=650 with EMA stays weak/negative across the grid, same pattern the SMA baseline already showed
(650 was never the recommended venue for non-0-DTE trades).

### Call side, bar=2600, EMA/EMA (spot-check + full grid, confirms weak -- not re-swept at 650)

At the SMA-winning window (2/15/5%): EMA gives 67 trades, 44.8% win, -13.00 net -- a lower win rate
than SMA's own 62.1% at the identical window (both net-negative). Across the full grid, every
real-sample cell stays sub-50% win rate pooled and on 0-DTE days; a handful of non-0-DTE cells at
wide slow windows (fast=2, slow=45-50) show small positive net (+30 to +78) but at ~36-39% win
rate, which fails the project's win-rate-first priority outright. **Call-price crossover does not
clear the bar with EMA either -- if anything EMA is uniformly worse than SMA on this side.**

### Verdict

Per the metric-by-metric evaluation process (2026-09-12), recording an explicit conclusion:

**EMA does NOT beat the already-established SMA winners for the tradeable option-price crossover,
on either side, at either bar size tested.** On the non-0-DTE side (bar=2600), EMA is strictly
worse at the identical SMA-winning window -- lower win rate, lower net, weaker MAE/MFE ratio, and
no cell in EMA's own grid recovers the gap. On the 0-DTE side (bar=650), EMA is roughly TIED with
SMA -- not better, not worse in any way that would justify switching a live rule.

**This tests, and does not confirm, my own stated hypothesis.** The DIRECTION is weakly right --
EMA's relative standing against SMA is better on 0-DTE (tied) than on non-0-DTE (clearly worse),
consistent with "responsiveness pays off more when the underlying decays fast." But EMA never
actually surpasses SMA anywhere in this sweep; the honest reading is that EMA's extra
responsiveness on 0-DTE at best cancels out its extra noise exposure, without creating a net edge
large enough to act on. This is consistent with, not new evidence against, the earlier PREDICTIVE-
only SMA-vs-EMA correlation finding (`docs/VOLUME_BAR_FINDINGS.md`'s "Tick activity + option
premium SMA/EMA research" section, part (5)): EMA showed 1.4-1.8x larger correlation magnitude
there too, but was explicitly flagged as possibly an artifact of EMA behaving like a shorter
effective window rather than a genuine edge -- this tradeable P&L result is consistent with that
caution, not a contradiction of it.

**No change recommended to the DTE-conditioned SMA recommendation already on record** (0-DTE: Put
fast=4/slow=40/thr=5% @650; non-0-DTE: Put fast=2/slow=10/thr=5% @2600). EMA is not promoted to
replace it anywhere.

### Scope not attempted, honestly noted

- Only Put fast=2..10/slow=10..50 (step 5) was swept as a full grid at both bar sizes (650/2600);
  Call's full grid was only run at bar=2600, not 650 (a spot-check, not a full sweep, per the same
  judgment call the original SMA Call-side work made -- Call was already weak, a second full grid
  wasn't judged worth the run time).
- `DualAgreement`/`DualAgreementBothExit` sides were not re-tested with EMA.
- Per-leg type MIXING (EMA fast + SMA slow, or the reverse) was not tested here -- only pure
  EMA/EMA was compared against pure SMA/SMA, even though the new plumbing supports independent
  fast/slow types. The earlier correlation-only research already found EMA-fast/SMA-slow "lands
  between the two pure forms" -- worth a dedicated follow-up rather than assumed to transfer here.
- Threshold was fixed at 5% throughout (matching the established SMA convention), not re-swept for
  EMA specifically -- EMA's spread reacts faster, so its own optimal threshold could plausibly
  differ; not checked.
- Only the original 8-day range (09-08..09-19) was used, for direct comparability with the recorded
  SMA numbers -- the additional days populated since then (09-04, 09-10, 09-11, 09-16 through
  09-22) were not included in this EMA sweep.
- Band-width variants (band=3/5) were not re-tested with EMA.
- **All results in this section are ATM-only (no entry-premium band) -- see the addendum below.**

### Reproduction commands

```
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-calibrate 2026-09-08 2026-09-19 Put 2,3,4,5,6,7,8,9,10 10,15,20,25,30,35,40,45,50 5 2600 --fasttype=Ema --slowtype=Ema
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-calibrate 2026-09-08 2026-09-19 Put 2,3,4,5,6,7,8,9,10 10,15,20,25,30,35,40,45,50 5 650 --fasttype=Ema --slowtype=Ema
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-calibrate 2026-09-08 2026-09-19 Call 2,3,4,5,6,7,8,9,10 10,15,20,25,30,35,40,45,50 5 2600 --fasttype=Ema --slowtype=Ema
dotnet run --project NiftySignal.VolumeBarData -- mae-mfe price-crossover 2026-09-08 2026-09-19 Put 2 10 5 2600 --fasttype=Ema --slowtype=Ema
dotnet run --project NiftySignal.VolumeBarData -- mae-mfe price-crossover 2026-09-08 2026-09-19 Put 4 10 5 650 --fasttype=Ema --slowtype=Ema

# Baseline re-verification (must stay 54 trades, 72.2% win, +254.85 net)
dotnet run --project NiftySignal.VolumeBarData -- price-crossover 2026-09-08 2026-09-19 Put 2 10 5 2600
```

## Addendum, same session (2026-09-23): closed an entry-premium-band gap in `SimulatePriceCrossoverDayAsync`

Discovered while explaining this method's design to the user: unlike `SimulateDayAsync`/
`SimulateCrossoverDayAsync` (which gained a `[minEntryPrice, maxEntryPrice]` entry-strike-selection
band on 2026-09-21, walking the chain outward from ATM and taking the first strike whose live
premium falls in the band), `SimulatePriceCrossoverDayAsync` had NO such filter -- every trade in
both sections above entered the pure ATM strike regardless of that strike's own premium level. The
user believed the [100,150] premium band already applied here ("no change required there"); it did
not. Closed the gap by adding the identical `minEntryPrice`/`maxEntryPrice` params and
`PickStrikeInBandAsync` local function (same convention, duplicated per the existing project
pattern for closures that capture per-method local state) to `SimulatePriceCrossoverDayAsync`, and
threaded `--minprice=`/`--maxprice=` through `price-crossover`/`price-crossover-calibrate`/
`mae-mfe price-crossover`. Default (both null) is byte-identical to the method's prior behavior --
confirmed by re-running the Put 2/10/5%@2600 baseline unchanged (54 trades, 72.2% win, +254.85 net).
Every result recorded in the two sections above this addendum was produced WITHOUT this band (plain
ATM) -- they should be treated as ATM-strike results, not [100,150]-banded results, until re-run
with `--minprice=100 --maxprice=150`. `dotnet build`: 0 warnings/0 errors. `dotnet test`: 763/763
passing, no regressions. No new automated test was added for the band behavior itself (this method
has no existing EF-InMemoryDatabase integration-test harness to extend, unlike the pure
`PriceCrossoverEngine` calculator, which already has one) -- verified instead by the same
CLI-reproduction-run convention this method's own results have always used throughout this
document (baseline-unchanged-by-default, then a spot-check run with the band active showing
different entries/strikes, confirming the filter is live).

## Re-verifying the established Put winners under the real [100, 150] entry-premium band (2026-09-23)

The user's own explicit instruction: run the established winners with `--minprice=100
--maxprice=150` before trusting them further, since the addendum above showed every prior number in
this document was ATM-only. Same 8-day range (09-08..09-19) throughout, so directly comparable to
the recorded ATM-only numbers. Also fixed a small display-only gap while running this:
`price-crossover-calibrate`'s own console header still printed "single ATM strike" even when
`--minprice`/`--maxprice` were set (the simulation itself was already correct -- confirmed by the
numbers differing from the ATM-only run -- only the header text was misleading). `dotnet build`: 0
warnings/0 errors. `dotnet test`: 763/763 passing, no regressions.

### Non-0-DTE winner: Put fast=2/slow=10/thr=5% @2600

| | Pooled (8 days) | 0-DTE (2 days) | Non-0-DTE (6 days) |
|---|---|---|---|
| ATM-only (established) | 54 trades, 72.2% win, +254.85 net | 32, 78.1%, +179.50 | 22, 63.6%, +75.35 |
| **[100,150]-banded** | 54 trades, 70.4% win, **+262.80 net** | 32, 75.0%, +198.20 | 22, 63.6%, **+64.60** |

MAE/MFE (pooled, banded): MAE avg 6.00% / MFE avg 7.43% -- **1.24x favorable** (vs the ATM-only
1.30x). **Essentially unchanged overall** -- same trade count (the band never excludes a trade
here, it can only swap which nearby strike gets picked, and evidently the ATM strike was already
usually in-band or close to it), non-0-DTE win rate identical (63.6%=63.6%), non-0-DTE net modestly
lower (75.35 -> 64.60), pooled net modestly higher (254.85 -> 262.80), MAE/MFE ratio modestly
weaker but still clearly favorable. **Conclusion: the band does not change this cell's standing --
it remains the recommended non-0-DTE pick, now verified under the real constraint rather than
ATM-only.**

### 0-DTE winner: Put fast=4/slow=40/thr=5% @650

| | Pooled (8 days) | 0-DTE (2 days) | Non-0-DTE (6 days) |
|---|---|---|---|
| ATM-only (established) | 21 trades, 76.2% win, +159.10 net | 20, 80.0%, +167.55 | 1, 0.0%, -8.45 |
| **[100,150]-banded** | 21 trades, 85.7% win, **+173.55 net** | 20, **90.0%**, **+181.65** | 1, 0.0%, -8.10 |

MAE/MFE (pooled, banded): MAE avg 4.81% / MFE avg 11.30% -- **2.35x favorable** (vs the ATM-only
2.5x, a small step down but still strongly favorable). **This one genuinely IMPROVES under the
band**: 0-DTE win rate 80.0% -> 90.0%, 0-DTE net +167.55 -> +181.65, same trade count. The band
appears to be filtering OUT a worse-quality ATM fill on at least one of the 0-DTE trades in favor of
a strike whose premium is more solidly inside the liquid, capital-consistent [100,150] range --
consistent with the project's own stated reason for the band (keeping per-lot capital consistent),
not a lucky artifact given the improvement shows up on both win rate and net together, not just one.
**Conclusion: the band strengthens this cell's standing; it remains the recommended 0-DTE pick, now
measurably better under the real constraint than it looked ATM-only.**

### Call side: fast=2/slow=15/thr=5% @2600 (spot-check, already the weak side)

ATM-only: 58 trades, 62.1% win, -31.30 net, MAE/MFE unfavorable (1.19x, i.e. MAE > MFE). Banded: 58
trades, 63.8% win, **-70.60 net**, MAE 7.83%/MFE 6.48% -- **0.83x, more unfavorable than ATM-only.**
Win rate ticks up slightly but net gets meaningfully worse and the MAE/MFE ratio degrades further.
**Conclusion unchanged, if anything reinforced: Call-price crossover does not clear the bar under
the real premium band either.**

### Overall verdict

**Applying the real [100,150] entry-premium constraint does not overturn any standing conclusion in
this file.** The non-0-DTE Put winner is essentially unchanged (a wash). The 0-DTE Put winner
genuinely improves. The Call side, already rejected, gets modestly worse. **No change to the
recommendation**: 0-DTE trades Put fast=4/slow=40/thr=5% @650 (now confirmed stronger under the
band); non-0-DTE trades Put fast=2/slow=10/thr=5% @2600 (confirmed essentially equivalent under the
band). **From this point forward, every reproduction/new-cell run in this file should pass
`--minprice=100 --maxprice=150`** -- the ATM-only numbers above (and everything carried over from
`docs/VOLUME_BAR_FINDINGS.md`) are superseded as the "current" numbers, though they remain useful as
a reference for how much the band does or doesn't matter.

### Scope not attempted, honestly noted

- Only the two established Put winners and one Call spot-check were re-run under the band -- the
  full SMA/EMA grids (the exhaustive fast/slow sweeps) were NOT re-swept under the band. It's
  possible a different window pair is optimal once entries are premium-constrained; not checked.
- `DualAgreement`/`DualAgreementBothExit` were not re-tested under the band.
- The EMA results recorded in the section above this one remain ATM-only and have not been re-run
  banded.

### Reproduction commands

```
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-calibrate 2026-09-08 2026-09-19 Put 2 10 5 2600 --minprice=100 --maxprice=150
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-calibrate 2026-09-08 2026-09-19 Put 4 40 5 650 --minprice=100 --maxprice=150
dotnet run --project NiftySignal.VolumeBarData -- mae-mfe price-crossover 2026-09-08 2026-09-19 Put 2 10 5 2600 --minprice=100 --maxprice=150
dotnet run --project NiftySignal.VolumeBarData -- mae-mfe price-crossover 2026-09-08 2026-09-19 Put 4 40 5 650 --minprice=100 --maxprice=150
dotnet run --project NiftySignal.VolumeBarData -- mae-mfe price-crossover 2026-09-08 2026-09-19 Call 2 15 5 2600 --minprice=100 --maxprice=150
```

## Locking [100,150] entry band; finalizing SIGNAL-price band composition (2026-09-23)

**Two fixed rules from here on, per the user's explicit instruction:**
1. **Every trade's strike must be selected from live premium in [100, 150]** -- `--minprice=100
   --maxprice=150` on every command in this track from now on, no longer swept as a variable.
2. **The SIGNAL-price band composition** (which strikes the crossover engine's own fast/slow MA is
   computed FROM -- separate from #1, which strike gets TRADED) needed to be decided before further
   work: single ATM, a symmetric band (ATM+N ITM+N OTM), or an asymmetric one-sided band (ATM+N ITM
   only, or ATM+N OTM only). This section decides it.

**New code**: `PriceBandComposition` enum (`Symmetric`/`ItmSide`/`OtmSide`) added to
`TradeSimulator.SimulatePriceCrossoverDayAsync`'s `GetPriceAsync` closure. `Symmetric` (default) is
the ORIGINAL, unchanged ATM+/-N behavior (re-verified: `--band=3` still reproduces the exact
pre-existing number, 56 trades/71.4%/+254.15 ATM-only). `ItmSide`/`OtmSide` take all
`bandWidth`-1 non-ATM strikes from one side only. **ITM/OTM direction is handled explicitly per
option side, not assumed symmetric**: for a Call, ITM = lower strikes (lower index in the ascending
sorted-strike list); for a Put, ITM = HIGHER strikes (higher index) -- getting this backwards for
either side would silently swap ITM and OTM for that side. CLI: `--bandmode=Symmetric|ItmSide|OtmSide`
on `price-crossover`/`price-crossover-calibrate`/`mae-mfe price-crossover`. `dotnet build`: 0
warnings/0 errors. `dotnet test`: 763/763 passing, no regressions.

**Five configurations swept, both established Put winners, entry band [100,150] fixed throughout**:
ATM-only (band=null, the current pick), Symmetric band=3 (ATM+1ITM+1OTM), Symmetric band=5
(ATM+2ITM+2OTM), ItmSide band=2 (ATM+1ITM only), OtmSide band=2 (ATM+1OTM only) -- matching the
user's own named examples exactly (the "5 strikes"/"3 strikes" symmetric cases, and the "only ITM
and ATM"/"only ATM and OTM" cases read as the minimal 2-strike version of each, ATM plus exactly one
strike from that side).

### Non-0-DTE winner: Put fast=2/slow=10/thr=5% @2600 (non-0-DTE column is the one that matters here)

| Signal band | Non-0-DTE trades | Win% | Net |
|---|---|---|---|
| **ATM-only (current)** | 22 | **63.6%** | **+64.60** |
| Symmetric band=3 | 23 | 60.9% | +61.70 |
| Symmetric band=5 | 21 | 57.1% | +58.65 |
| ItmSide band=2 | 20 | 60.0% | +31.05 |
| OtmSide band=2 | 23 | 60.9% | +26.20 |

**ATM-only wins outright** -- every band variant has a lower non-0-DTE win rate, and three of four
have a materially lower net. No further check needed; this isn't close.

### 0-DTE winner: Put fast=4/slow=40/thr=5% @650 (0-DTE column is the one that matters here)

| Signal band | 0-DTE trades | Win% | Net | Pooled MAE%/MFE% (ratio) |
|---|---|---|---|---|
| **ATM-only (current)** | 20 | 90.0% | +181.65 | 4.81% / 11.30% (**2.35x**) |
| Symmetric band=3 | 15 | 100.0% | +191.20 | 7.61% / 12.63% (1.66x) |
| Symmetric band=5 | 13 | 100.0% | +181.05 | 7.17% / 13.59% (1.90x) |
| ItmSide band=2 | 13 | 100.0% | +181.50 | 7.20% / 14.74% (2.05x) |
| OtmSide band=2 | 21 | 85.7% | +181.60 | 8.80% / 10.84% (1.23x) |

**Closer call here, but ATM-only still wins once sample size and MAE/MFE are weighed, not just the
headline win rate.** Three of four band variants show 100.0% win rate -- but on 13-15 trades, down
from ATM's 20, and this project's own guardrail treats an unusually high win rate as a red flag to
interrogate, not a result to celebrate, especially at a SMALLER n than the baseline it's being
compared against. The likely mechanism (not independently verified further, flagged as a hypothesis
not a fact): averaging across more strikes smooths/lags the signal, so fewer crossings fire at all --
the ones that still do are probably concentrated in the largest, cleanest moves, which naturally win
more often on a shrunk, survivorship-flavored sample. Every band variant's MAE/MFE ratio is WORSE
than ATM-only's 2.35x (best band variant reaches 2.05x, ItmSide) -- band averaging does not improve
trade quality by the MAE/MFE measure at this window, it only reduces the sample while coincidentally
preserving a high win rate on what's left. OtmSide is the only variant that doesn't shrink the
sample (21 vs 20) but it's worse on every other axis (lower win rate, weakest MAE/MFE ratio of the
four).

### Verdict: band composition is finalized as **ATM-only (no signal-price averaging)**

**Neither symmetric nor one-sided band averaging improves on the plain single-ATM-strike signal
price for either established winner, once win rate is checked against sample size and MAE/MFE is
weighed alongside it, not just the raw win% number.** The non-0-DTE case is a clean, unambiguous
ATM-only win. The 0-DTE case is closer on paper (100% win rate cells exist) but doesn't survive the
combination of (a) smaller sample than the baseline it's compared to and (b) a worse MAE/MFE ratio
across every single variant tested -- exactly the kind of result this project's evaluation
discipline exists to catch rather than be impressed by.

**Going forward, every command in this file uses**: `--minprice=100 --maxprice=150` (fixed, rule
#1) and no `--band=`/`--bandmode=` flag at all (ATM-only, the finalized signal-price choice, rule
#2). Both are now locked; this dimension will not be re-swept without a specific new reason to
revisit it.

### Scope not attempted, honestly noted

- Only the two established Put winner windows were swept across band compositions -- Call side and
  other fast/slow windows were not re-checked (Call was already clearly weaker under every prior
  test; unlikely band averaging changes that conclusion, but not verified here).
- Band widths beyond 2/3/5 (e.g. ItmSide/OtmSide with 3+ strikes, matching the symmetric band=5
  scale one-sided) were not tested -- the user's named examples covered widths 2 (one-sided) and 3/5
  (symmetric) specifically; a one-sided width-3 (ATM+2ITM or ATM+2OTM) variant was not requested and
  not run.
- The "100% win rate on a shrunk sample" mechanism above is stated as a plausible hypothesis, not
  independently verified by inspecting the excluded trades directly.

### Reproduction commands

```
# Non-0-DTE winner sweep
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-calibrate 2026-09-08 2026-09-19 Put 2 10 5 2600 --band=3 --bandmode=Symmetric --minprice=100 --maxprice=150
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-calibrate 2026-09-08 2026-09-19 Put 2 10 5 2600 --band=5 --bandmode=Symmetric --minprice=100 --maxprice=150
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-calibrate 2026-09-08 2026-09-19 Put 2 10 5 2600 --band=2 --bandmode=ItmSide --minprice=100 --maxprice=150
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-calibrate 2026-09-08 2026-09-19 Put 2 10 5 2600 --band=2 --bandmode=OtmSide --minprice=100 --maxprice=150

# 0-DTE winner sweep (same band flags, window 4/40/5%@650) + MAE/MFE
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-calibrate 2026-09-08 2026-09-19 Put 4 40 5 650 --band=3 --bandmode=Symmetric --minprice=100 --maxprice=150
dotnet run --project NiftySignal.VolumeBarData -- mae-mfe price-crossover 2026-09-08 2026-09-19 Put 4 40 5 650 --band=2 --bandmode=ItmSide --minprice=100 --maxprice=150

# Regression check (must stay 56 trades, 71.4% win, +254.15 net -- ATM-only, no entry-price band)
dotnet run --project NiftySignal.VolumeBarData -- price-crossover 2026-09-08 2026-09-19 Put 2 10 5 2600 --band=3
```

## Correctness review: does the crossover SIGNAL track one instrument, or splice across a rolling ATM strike? (2026-09-23)

**User's explicit request**: verify the simulation doesn't do something like "buy 24500CE but track the
price of 24400CE" -- i.e., does entry/exit P&L stay on the traded instrument, and does the crossover
signal that decides WHEN to enter actually represent one option's own price momentum?

**Two separate questions, two separate answers -- important not to conflate them:**

### (1) Entry/exit P&L tracking -- CONFIRMED CORRECT, no bug

Once a position opens, `position.Token` is captured at entry and `GetSeriesAsync(position.Token)`
is used for every subsequent bar's mark-to-market and the eventual exit fill, all the way to
`ExitReason`/`ExitPrice`. The strike is never re-picked mid-trade. Verified by reading the full
entry->hold->exit path in `SimulatePriceCrossoverDayAsync` line by line. **The literal scenario the
user described (buy 24500CE, track 24400CE) does not happen anywhere in this simulator.**

### (2) The crossover SIGNAL itself -- a REAL, significant issue, confirmed empirically, not just in theory

`GetPriceAsync` re-picks the ATM strike FRESH on every single bar, from that bar's own future
close price (`PickAtm(optSide, bar.ClosePrice)`), then feeds whatever that bar's ATM premium
happens to be into the `PriceCrossoverEngine`'s rolling fast/slow-MA window. If the ATM strike
changes between bars (NIFTY future crosses a strike midpoint), the "price" the crossover engine is
averaging silently switches from one option contract's premium to a DIFFERENT contract's premium,
mid-window -- the fast/slow MA is not necessarily tracking one instrument's own price history.

**This exact mechanism was already identified once before, in a wholly different, PREDICTIVE-ONLY
tool** (`MomentumRelationshipAnalyzer`'s `StrikeMode.Rolling` vs `StrikeMode.SignalFixed`,
`docs/VOLUME_BAR_FINDINGS.md`'s "Step 4: signal-strike vs rolling-strike control" section,
2026-09-22) but was never applied to, or even measured against, the actual TRADEABLE
`SimulatePriceCrossoverDayAsync` path this whole `Price_Based_Findings.md` track is built on. That
prior work found fixing the strike for the day collapsed a correlation from -0.48/-0.50/-0.52 down
to -0.05/-0.08/-0.05 (~7-10x), i.e. "a meaningful share of the measured relationship is a
rolling-strike artifact, not pure premium autocorrelation" -- but it stopped at the correlation
level and explicitly left the trading-rule implication as a caveat on INTERPRETATION, not something
requiring a code fix, reasoning that "a real trading rule... also necessarily trades on the rolling
ATM strike, since that's the liquid/tradeable contract." This session's review goes further: it
measures how often this ACTUALLY happens for the specific trades our two established winners fire,
not just as an abstract correlation effect.

**New diagnostic**: `atm-drift-check` CLI command (read-only, opens no trades, doesn't touch
`TradeSimulator.cs`) -- runs the real `SimulatePriceCrossoverDayAsync` to get actual trade entries,
independently recomputes the Call/Put ATM strike per bar using the exact same
`AtmStrikeSelector.PickAtm` call the simulator itself uses, and checks whether the ATM strike was
constant across the trailing `slowBars`-wide window ending at each trade's entry bar.

**Results, both established winners, [100,150] entry band applied throughout:**

| Winner | Trades checked | Stable signal window | ATM rolled mid-window |
|---|---|---|---|
| Non-0-DTE: Put 2/10/5%@2600 | 54 | 3 | **51 (94.4%)** |
| 0-DTE: Put 4/40/5%@650 | 21 | 2 | **19 (90.5%)** |

**This is pervasive, not an edge case.** Over 9 in 10 of every trade fired by either established
winner had its entry decision built from a fast/slow MA that mixed two different option contracts'
premiums within the same window (almost always exactly two adjacent strikes, e.g. "23750 -> 23800" --
not a wild multi-strike scramble, but a real, frequent discontinuity all the same). Given how often
this happens, a meaningful share of every recorded "crossing" in this file could be triggered by the
premium-LEVEL step when ATM re-centers (a fresh ATM contract typically sits at a different point on
its own theta/vega curve than the contract it replaced) rather than by genuine price momentum on one
contract -- exactly the mechanism the prior correlation-only research already described, now shown to
affect the overwhelming majority of actual trades, not a minority.

### What this does and does not call into question

- **Does NOT** call into question any recorded trade's realism (real strike, real premium, real
  fill) or the entry-premium-band/band-composition conclusions already locked in -- those hold
  regardless of what's driving the crossover signal.
- **DOES** call into question the MECHANISM behind the measured edge: "fast/slow crossover on one
  option's own premium" was the strategy as originally conceived (user's own words: "2 cadence
  average call price as fast ma... buy call if 2ma cross over 10ma"), but what's actually been
  backtested so far is closer to "fast/slow crossover on whichever strike happens to be nearest ATM
  at each moment," which is a different, noisier signal that the results recorded in this file have
  been silently measuring without anyone (including me) flagging it until this review.

### Fix options, not yet implemented -- decision needed before more experiments build on this signal

1. **Reset the signal window on ATM roll** (my recommendation): when the ATM strike changes bar to
   bar, clear `PriceCrossoverEngine`'s rolling window and re-warm from scratch on the new strike, so
   the fast/slow MA NEVER mixes two contracts' prices, ever. Tradeoff: given rolls happen this often,
   the strategy would go quiet for `slowBars` bars after every roll while re-warming -- likely a real
   cut to trade frequency, magnitude unmeasured yet. Most faithful to the original "track one
   contract's own momentum" concept.
2. **SignalFixed (pin ATM for the whole day)**, mirroring `MomentumRelationshipAnalyzer`'s existing
   mode: simple, already has precedent in this codebase. Tradeoff: by afternoon on a trending day the
   pinned contract may no longer be near-the-money at all, so its price behavior stops representing
   "the tradeable ATM option" -- a different, arguably worse mismatch with what's actually entered
   (entry strike selection is still the CURRENT ATM at trade time, so the signal and the traded
   instrument could end up meaningfully different contracts).
3. **Leave rolling as-is, but treat this as a known, accepted property** (the prior correlation-only
   research's own stance) on the reasoning that any REAL strategy must also trade the current liquid
   ATM contract, so a live system would face the same discontinuity -- the fix, in this view, isn't
   in the simulator, it's in accepting that "crossover on the current tradeable contract" is a
   structurally different (and possibly still useful) signal from "crossover on one fixed contract,"
   and re-labeling the strategy's own self-description accordingly rather than changing the code.

**Not implemented pending direction** -- this is a correctness-sensitive change that would require
re-running every experiment recorded in this file once decided, per this project's own "give a
structurally risky change its own pass with its own verification" discipline. `dotnet build`: 0
warnings/0 errors. `dotnet test`: 763/763 passing (the new `atm-drift-check` command has no unit
test of its own -- it's a read-only diagnostic tool, same convention as `depth-report`/`perf-check`
elsewhere in this project, verified by running it and reading its output rather than asserting on
it).

### Reproduction commands

```
dotnet run --project NiftySignal.VolumeBarData -- atm-drift-check 2026-09-08 2026-09-19 Put 2 10 5 2600 --minprice=100 --maxprice=150
dotnet run --project NiftySignal.VolumeBarData -- atm-drift-check 2026-09-08 2026-09-19 Put 4 40 5 650 --minprice=100 --maxprice=150
```

## Implementing Option 1 (reset the signal window on ATM roll) -- and a major, non-obvious consequence (2026-09-23)

**User's decision**: go with Option 1 from the three proposed above -- reset `PriceCrossoverEngine`'s
rolling window/EMA state whenever the ATM strike it's tracking changes bar to bar, so the fast/slow
MA can never mix two option contracts' premiums.

**New code**: `PriceCrossoverEngine.Reset()` -- clears `_window`/`_fastEma`/`_slowEma`/
`_previousDiffFraction` back to fresh-construction state; does not touch the engine's own
fastBars/slowBars/MaType configuration. `GetPriceAsync` in `SimulatePriceCrossoverDayAsync` now
returns `(double? Price, decimal? AtmStrike)` instead of just the price, so the caller can detect a
roll (works identically for the single-ATM and band-averaged cases -- for a band, the anchor ATM
strike is returned even though it isn't necessarily one of the averaged strikes, since the whole
band composition shifts whenever the anchor does). The main per-bar loop tracks `lastCallAtm`/
`lastPutAtm` and calls `Reset()` on the relevant engine immediately before `Observe()` whenever that
side's anchor strike differs from the previous bar's. Two new unit tests confirm `Reset()` fully
erases state (a reused, reset engine observing a price series from scratch produces byte-identical
output to a brand-new engine observing the same series). `dotnet build`: 0 warnings/0 errors.
`dotnet test`: 765/765 passing (763 baseline + 2 new, no regressions).

**Invariant re-verified empirically, not just by code inspection**: re-ran `atm-drift-check` against
both established winners post-fix -- **0 trades checked, 0% rolled** for both (down from
54/21 trades checked and 94.4%/90.5% rolled pre-fix). The fix works exactly as intended: it is now
structurally impossible for a fired trade's signal window to span more than one ATM strike.

**But "0 trades checked" is because ZERO trades fire at all, for either established winner, over the
whole 8-day range, once the fix is applied.** Confirmed directly (`price-crossover`, not just the
diagnostic): both `Put 2/10/5%@2600` and `Put 4/40/5%@650` return "No trades fired across the
requested range."

### Why -- a genuinely surprising, important structural finding, not a bug

Added a day-level report (`atm-drift-check`'s new second section) showing the raw ATM-strike-change
pattern per day, independent of any trade logic: 33-263 strike changes per day, but only 2-9
*distinct* strikes revisited -- i.e. the strike **oscillates back and forth**, not a clean one-way
drift, and it does so across mostly SHORT runs (many well under the 10/40 bars the slow leg needs to
warm up) punctuated by one dominant long run per day (55-659 bars, comfortably long enough to warm
up in isolation).

**The reason zero trades still fire even with those long stable runs available**: a strike ROLLS
*because* the underlying is moving through a strike boundary -- rolling is, almost by definition, a
symptom of real price movement. A long, stable, no-roll run is close to the opposite: a period where
the underlying stayed within one strike's claim on "nearest to ATM," i.e. relatively range-bound.
**Resetting on every roll doesn't just avoid mixing instruments -- it specifically discards the
signal history at the exact moment real momentum starts (when the roll happens), and only allows a
crossover to complete during the calmer, lower-momentum periods where the strike happens to sit
still.** Those calmer periods, it turns out, don't produce a 5%-threshold-clearing premium swing
anywhere in this 8-day sample at the 10-bar/40-bar slow-window lengths that were tuned for the OLD,
rolling/splicing signal. This is not a contradiction of anything found so far -- it's a direct,
mechanical consequence of fixing the splicing issue, and arguably the clearest evidence yet that a
real chunk of the previously-recorded "edge" was entangled with roll-driven level-jumps rather than
pure single-contract momentum, exactly as the earlier correlation-only research warned.

### What this means going forward

**Every fast/slow/threshold parameter recorded anywhere in this file (both SMA and EMA sections) was
tuned against the OLD rolling/splicing signal and is very likely no longer usable as-is under the
fixed, reset-on-roll signal.** The established "winners" are dead (0 trades) under the fix as
currently configured. A full re-sweep is needed to find whether ANY fast/slow/threshold combination
produces a real trade count under the corrected signal -- very likely needing much SHORTER slow
windows (to have a chance of completing warm-up within the typical short runs, not just the one long
run per day) and/or a lower threshold (since a completed window inside a calm, no-roll period is
less likely to clear 5%). This has not been done yet -- flagging for direction before spending
compute on a grid whose right search range isn't yet known.

### Scope not attempted, honestly noted

- No re-sweep of fast/slow/threshold has been run yet under the fixed signal -- the two numbers
  above (0 trades at both established windows) are the only data points so far.
- Call side and `DualAgreement`/`DualAgreementBothExit` were not re-checked under the fix.
- The band-composition conclusion (ATM-only, locked in above) was reached BEFORE this fix and has
  not been re-verified under the reset-on-roll signal -- band averaging's own strike anchor also
  rolls, so this may need revisiting too, not assumed to still hold.

### Reproduction commands

```
# Confirm zero trades at both established windows, post-fix
dotnet run --project NiftySignal.VolumeBarData -- price-crossover 2026-09-08 2026-09-19 Put 2 10 5 2600 --minprice=100 --maxprice=150
dotnet run --project NiftySignal.VolumeBarData -- price-crossover 2026-09-08 2026-09-19 Put 4 40 5 650 --minprice=100 --maxprice=150

# Confirm the invariant (0% rolled) and see the day-level strike-stability report
dotnet run --project NiftySignal.VolumeBarData -- atm-drift-check 2026-09-08 2026-09-19 Put 2 10 5 2600 --minprice=100 --maxprice=150
dotnet run --project NiftySignal.VolumeBarData -- atm-drift-check 2026-09-08 2026-09-19 Put 4 40 5 650 --minprice=100 --maxprice=150
```

## Investigating the zero-trade result -- research/correctness problem, not a parameter sweep (2026-09-23)

**User's explicit instruction**: before any further parameter sweeping, prove (not assume) why the
reset-on-roll fix produces zero trades, rule out an implementation bug, compare candidate signal
architectures on structural terms only (no profitability optimization), and recommend a signal
architecture on correctness/live-behavior grounds before touching trading logic again.

### 1. Signal instrument vs. trade instrument -- confirmed separate, and (1) is unconditionally correct

**Trade instrument (the exact option token bought and tracked for P&L) is immutable from entry to
exit, independent of anything this investigation changes.** Verified by code reading AND by a new
integration test (`PriceCrossoverSignalArchitectureTests.
SimulatePriceCrossoverDayAsync_TradeStaysOnEntryToken_EvenWhenAtmRollsToADifferentContractBeforeExit`):
a position is opened on strike A (23500), the ATM strike is then forced to roll to a DIFFERENT
strike B (23400) on the very last bar while the position is still open, and B's price is set to an
absurd, unmistakable value (5.00) that must never leak into the trade's result. The trade's recorded
`ExitPrice` is asserted to equal A's own real continuing price (600.00), not B's. Passes.

**Signal instrument (which option's price feeds the fast/slow crossover that decides WHEN to enter)
is the part under investigation** -- see the "Correctness review" section above for how the original
implementation let this drift across contracts, and everything below.

### 2. Day-level, per-side market structure and crossing counts -- the evidence

**New code**: `NiftySignal.VolumeBarData/SignalArchitectureAudit.cs` -- a wholly separate, read-only
analysis module (same "never touches TradeSimulator's dispatch" discipline `MomentumRelationshipAnalyzer`
already established), plus two CLI commands: `signal-audit` (aggregate structural stats per day/
architecture) and `signal-trace` (bar-by-bar dump for one day, see section 4). Neither opens a trade
or optimizes anything -- they report counts only: bars, ATM-strike run lengths, raw sign flips
(threshold=0, i.e. "any crossing at all"), threshold-gated crossings (split Up/Down), and "trade
candidates" (an entry-direction crossing that also clears the entry time window -- still not a
simulated trade, no position-already-open gating, no strike/premium lookup).

**Market structure** (architecture-independent -- a property of the raw ATM sequence, same regardless
of how the signal handles it), Put side, bar=2600, 8-day range:

| Date | Bars | Distinct strikes | Changes | Max stable run | Runs >=10 | >=20 | >=40 | >=60 |
|---|---|---|---|---|---|---|---|---|
| 2026-09-08 | 646 | 3 | 33 | 153 | 13 | 8 | 5 | 3 |
| 2026-09-09 | 911 | 3 | 40 | 181 | 14 | 10 | 7 | 6 |
| 2026-09-10 | 513 | 3 | 50 | 95 | 14 | 6 | 4 | 2 |
| 2026-09-11 | 941 | 5 | 71 | 144 | 19 | 14 | 5 | 4 |
| 2026-09-15 | 991 | 9 | 139 | 131 | 25 | 12 | 3 | 3 |
| 2026-09-16 | 611 | 4 | 86 | 55 | 17 | 11 | 4 | 0 |
| 2026-09-17 | 546 | 4 | 48 | 83 | 16 | 8 | 3 | 2 |
| 2026-09-18 | 431 | 3 | 36 | 132 | 9 | 5 | 4 | 1 |

Plenty of long stable runs exist (max run 55-181 bars, well past the 10-bar or 40-bar slow-window
requirement) -- warm-up is structurally POSSIBLE many times a day. (bar=650/Put/Call and Call/2600
market-structure tables reproducible via the commands below; omitted here for length, same
qualitative shape.)

**Signal generation, four architectures, Put side, bar=2600, fast=2/slow=10/thr=5%, totals across
all 8 days:**

| Architecture | Resets | Warmed-up bars | Raw sign flips | Threshold crossings (Up+Down) | Trade candidates |
|---|---|---|---|---|---|
| **RollingSplice** (the ORIGINAL, pre-fix behavior -- reference only) | 0 | 5518 | 1020 | **93+106=199** | **69** |
| RollingReset (A, current production) | 503 | 3522 | 508 | 3 | 0 |
| FixedAtm (B) | 0 | 5518 | 776 | 2 | 0 |
| RollingBackAdjusted (C) | 0 | 5518 | 774 | 5 | 0 |

Same shape at bar=650/Put/fast=4/slow=40 (RollingSplice: 39+41=80 crossings, 29 candidates; the
three corrected architectures: 0-1 crossings, 0 candidates) and at bar=2600/Call/fast=2/slow=15
(RollingSplice: 111+110=221 crossings, 86 candidates; corrected architectures: 3-9 crossings, 1-2
candidates). `dotnet build`: 0 warnings/0 errors. `dotnet test`: 770/770 passing (765 baseline + 5
new invariant tests, see section 8).

### 3. Root cause -- ruled out B, confirmed A, by direct control comparison (not assumed)

**Is it (A) a genuine absence of qualifying signals, (B) `Reset()` destroying warm-up, or (C) an
implementation bug?** The decisive control is **FixedAtm**: it never resets (0 resets, by
construction it can't -- the strike is picked once and held), and it is warmed up for virtually the
entire session (5518 of 5518 possible bars, i.e. warm from bar ~10 onward). If Reset() were the
cause of the sparse signal, FixedAtm -- which has no Reset() at all -- should show a real trade
count. **It does not: 2 threshold crossings across the whole 8-day range, same order of magnitude as
RollingReset's 3, not RollingSplice's 199.** This rules out (B). No architecture-specific
implementation bug was found either (C) -- `signal-audit`'s numbers for RollingReset/FixedAtm/
RollingBackAdjusted are mutually consistent (same order of magnitude, same near-total absence of
5%-threshold-clearing moves) despite three structurally different implementations, which is what
you'd expect if the true cause is a property of the DATA once contract-mixing is removed, not a bug
specific to any one implementation. **Conclusion: (A) -- once the signal genuinely represents one
real contract's own price, a 5% threshold essentially never clears in this 8-day sample, at these
particular fast/slow windows.** This is a real, structural finding, not an artifact of the fix.

### 4. Visual replay -- what the algorithm is actually doing, bar by bar

`signal-trace` dump, 2026-09-15 (the busiest roll day, 139 strike changes), Put, fast=2/slow=10/
thr=5%, bar=2600, **RollingSplice** (original behavior):

```
 Bar  TimeIST  FuturePx AtmStrike    RawPx EnginePx Chg Rst   FastMa   SlowMa   Diff% Flip Up Dn
  20 09:15:54  23520.00     23500    61.60    61.60   Y        75.72    82.15   -7.82    Y     Y
  23 09:16:06  23529.30     23550    82.00    82.00   Y        69.17    77.28  -10.48
  24 09:16:07  23530.00     23550    81.25    81.25            81.62    76.79    6.30    Y  Y
```

At bar 20 the ATM strike is 23500 (RawPx ~61.60); by bar 23 it has rolled to 23550 (RawPx jumps to
~82.00 -- a genuine ~33% level difference between two DIFFERENT real contracts, not one contract
moving); bar 24 then fires **CrossedUp** (Diff%=6.30, clears the 5% threshold) purely because the
fast MA is still digesting that level jump -- **this specific "crossing" is a strike-switch artifact,
not price momentum on either contract.**

Same day, same window, **RollingReset** (the fix): at the identical bars 20/23, `Rst=Y` fires and
`FastMa/SlowMa` show `--` (window cleared, re-warming) -- **the bar-24 spurious CrossedUp is gone
entirely**, replaced by a clean re-warm-up on the new contract. **FixedAtm** on the same day never
shows a strike change at all (`Chg` column empty throughout) -- one real contract (23600, picked at
day's open) with its own genuinely continuous price trajectory (117 -> 156+ as the day progresses).

Full traces (every bar, or every day) reproducible via the commands below.

### 5. Structural comparison of the three candidate architectures (no profitability optimization)

Already tabulated in section 2. Restated as the plain answer to "which construction, on structural
grounds alone": **RollingReset and FixedAtm both achieve the primary goal (signal never mixes two
contracts) and land on the same order of magnitude of crossings (2-3 vs FixedAtm's 2) -- the choice
between them is NOT about which finds more signal, since neither finds much at this threshold.**
RollingBackAdjusted sits in the same range (5) with added complexity (a back-adjustment factor
computed from two contracts' real prices at each roll) and a fallback path (unused here --
0 fallback adjustments across every run) for when the outgoing contract has no quote at the
transition instant.

### 6. Which architecture matches the stated live requirement

**"The signal must not create artificial momentum by switching between option contracts, and once a
trade is opened, the exact selected option token must be tracked until exit."**

The second half is already satisfied identically by all three candidates AND was true before this
investigation started (section 1). The first half is where they differ in spirit, not just in the
numbers above:

- **RollingReset (A)**: always tracks the CURRENTLY tradeable, genuinely-nearest-to-ATM contract --
  exactly the contract a live system would actually be looking at and would actually buy if it
  decided to enter right now. The cost is a re-warm-up gap after every roll.
- **FixedAtm (B)**: simpler, but the tracked contract silently stops being "the ATM option" as spot
  drifts over the day -- by afternoon it can be a strike that's no longer anywhere near the money,
  meaning the SIGNAL and the contract you'd actually want to trade at that moment can diverge. This
  is arguably a WORSE mismatch with live reality than RollingReset's warm-up gap, not a better one.
- **RollingBackAdjusted (C)**: stays anchored to the current ATM like RollingReset, avoids the
  re-warm-up gap, but introduces a synthetic (real-data-grounded, not fabricated, but still
  constructed) price level that no longer equals any single contract's own actual traded price after
  a roll -- a live system could never actually see or trade this exact number.

**Recommendation, on correctness/live-behavior grounds, before any profitability consideration:
RollingReset (A) remains the best match.** It is the only one of the three whose fed price at every
single bar is a real, currently-tradeable contract's own actual premium -- never a stale contract
(FixedAtm's problem) and never a synthetic level (RollingBackAdjusted's tradeoff). Its cost (fewer
warmed-up bars right after a roll) is an honest reflection of live reality: a live system genuinely
cannot know "the new contract's own momentum" until it has watched that contract for a while either.

### 7-8. Scope discipline and tests

Per instruction: no trading logic was changed in this investigation (the reset-on-roll fix already
implemented and merged in the prior section stands as-is; this investigation only added read-only
analysis code). No parameter sweep was run; no threshold was adjusted to manufacture trades. Five new
tests added, `NiftySignal.Tests/VolumeBarData/PriceCrossoverSignalArchitectureTests.cs`:
`ShouldResetOnStrikeChange` exhaustive truth table (same strike/different strike/no-prior-strike/
no-current-strike -- 4 tests), plus the end-to-end token-immutability integration test from section 1
(the `Reset()` determinism tests already exist in `PriceCrossoverEngineTests.cs` from the prior
section). `dotnet build`: 0 warnings/0 errors. `dotnet test`: 770/770 passing.

### 9. Plain-language summary

**What exactly was wrong before?** The crossover engine's fast/slow moving average was computed from
"whichever option happens to be nearest-the-money right now," re-evaluated fresh every bar. When the
underlying moved enough to make a different strike the nearest one, the moving average would silently
start averaging in that DIFFERENT contract's price, right in the middle of its own rolling window --
mixing two contracts' price histories together without anyone (including the code) noticing.

**Why did the reset fix eliminate almost all trades?** Because, once measured directly (not assumed),
the overwhelming majority of what used to clear the 5% entry threshold turned out to BE that mixing
artifact -- a fresh contract typically starts at a meaningfully different price level than the one it
replaced, and that level jump alone was often bigger than 5%. Take the mixing away and, at this
particular threshold and these particular fast/slow window sizes, there's almost nothing left that
clears it -- confirmed by three independently-built, structurally different corrected signals (reset,
fixed, back-adjusted) all landing in the same near-zero range, and by watching the actual bar-by-bar
trace show the exact artifact bar by bar.

**Does that mean the strategy has no edge, or only that this signal construction is unsuitable?**
Neither claim is supported yet, and it's important not to overclaim either way. What IS now
demonstrated: the specific fast=2/slow=10/thr=5% (and fast=4/slow=40/thr=5%) configuration, tuned
against the OLD contaminated signal, does not transfer to the corrected one -- that threshold was
implicitly calibrated to catch contract-switch jumps, not real premium moves. Whether a DIFFERENT
threshold or window size finds genuine edge in the corrected, honest signal is a real open question
this investigation deliberately did not answer (no sweep was run, per instruction). The prior
sections' positive-looking numbers should be read as: "a signal that mixed real momentum with
contract-switch noise, evaluated at a threshold that happened to fire on the switches" -- not
evidence the underlying idea (option premium momentum) is worthless, and not evidence it works
either. Genuinely unresolved until a proper re-sweep is run on the corrected signal.

**What should the live implementation actually do?** If this strategy is ever wired live: use
RollingReset's own construction (always track the current, genuinely tradeable ATM contract; never
silently splice across a roll) -- it's the only one of the three that never shows a live system a
number it couldn't actually have traded at that exact price. A live version would also need its OWN
re-tuned fast/slow/threshold parameters, since the ones on record were tuned against the bug.

### Reproduction commands

```
dotnet run --project NiftySignal.VolumeBarData -- signal-audit 2026-09-08 2026-09-19 Put 2 10 5 2600 --architecture=All --entrystart=09:30 --entryend=15:00
dotnet run --project NiftySignal.VolumeBarData -- signal-audit 2026-09-08 2026-09-19 Put 4 40 5 650 --architecture=All --entrystart=09:30 --entryend=15:00
dotnet run --project NiftySignal.VolumeBarData -- signal-audit 2026-09-08 2026-09-19 Call 2 15 5 2600 --architecture=All --entrystart=09:30 --entryend=15:00

# Bar-by-bar visual trace, any architecture
dotnet run --project NiftySignal.VolumeBarData -- signal-trace 2026-09-15 Put 2 10 5 2600 --architecture=RollingSplice
dotnet run --project NiftySignal.VolumeBarData -- signal-trace 2026-09-15 Put 2 10 5 2600 --architecture=RollingReset
dotnet run --project NiftySignal.VolumeBarData -- signal-trace 2026-09-15 Put 2 10 5 2600 --architecture=FixedAtm
dotnet run --project NiftySignal.VolumeBarData -- signal-trace 2026-09-15 Put 2 10 5 2600 --architecture=RollingBackAdjusted
```

### Scope not attempted, honestly noted

- Only Put@2600 (2/10), Put@650 (4/40), and Call@2600 (2/15) were audited across all four
  architectures -- not every window/bar-size combination in this file.
- No parameter re-sweep was run under any corrected architecture (explicitly out of scope for this
  investigation, per instruction).
- `DualAgreement`/`DualAgreementBothExit` were not audited.
- `RollingBackAdjusted`'s fallback path (outgoing contract has no quote at the transition instant)
  was never exercised in this 8-day sample (0 fallbacks in every run) -- its behavior there is
  implemented (factor left unchanged) but not empirically tested against a real occurrence.

## Re-sweep under the corrected RollingReset signal (2026-09-23)

**User's explicit instruction, after the investigation above**: run a fresh parameter sweep now that
the signal is provably free of contract-mixing. `price-crossover-calibrate` already calls the
CURRENT (fixed) `SimulatePriceCrossoverDayAsync` -- no flag needed to select RollingReset, it IS the
production behavior. `[100,150]` entry band and ATM-only signal price stayed locked per the standing
rules above throughout.

**Bug found and fixed en route**: `price-crossover-calibrate`'s console output formatted the
threshold column as `{threshold,5:F0}` -- rounding to the nearest WHOLE number for display only (the
actual simulation always used the exact double value). A threshold list of `0.5,1,1.5,2,2.5,3` could
print two different real cells under the same misleading label (e.g. 0.5 and 1.5 both risk rounding
toward "1" depending on the value). Caught it because a re-run of a promising-looking "thr=0" row
with the literal value 0 produced 489 trades instead of the table's 25 -- a dead giveaway. Fixed to
`{threshold,6:F2}` (exact value, two decimal places). `dotnet build`: 0 warnings/0 errors. `dotnet
test`: 770/770 passing (display-only change, no simulation logic touched, no regression risk).

**Grid**: fast in {1,2,3,4,5,6,8,10}, slow in {3,5,8,10,15,20,30,40}, threshold in
{0.5,1,1.5,2,2.5,3}%, Put and Call, bar in {650, 2600}, `--minprice=100 --maxprice=150` throughout,
same 8-day range (2026-09-08..2026-09-19) as every prior grid in this file for direct comparability.

### Non-0-DTE (bar=2600, the established venue): no viable candidate

Best non-0-DTE win rate at any real-sample cell (n>=15): **45.0%** (fast=4/slow=10/thr=0.5%, n=20).
Every other real-sample cell falls in the 32-43% range. **No cell clears even a 50% win rate on a
real sample.** Clean, unambiguous negative result -- the corrected signal shows no usable edge for
Put-price crossover at this bar size in this 8-day window.

### Call side (both bar sizes): no viable candidate

Best Call win rate at any real-sample cell: 44.4% (0-DTE, bar=650) and 51.5% (non-0-DTE, bar=2600,
but on a pooled win rate of only 37.8%, i.e. inconsistent across the DTE split). **Confirms Call
remains the weaker side, as it already was before this whole investigation** -- not promoted.

### 0-DTE (bar=650): one real, positive, but substantially WEAKER candidate than before

Best real-sample cell by the win-rate-then-MAE/MFE priority: **Put fast=5/slow=10/thr=0.5%**:

| | Trades | Win% | Net | MAE% avg | MFE% avg | Ratio |
|---|---|---|---|---|---|---|
| Pooled (8 days) | 37 | 54.1% | +226.75 | 6.45% | 13.41% | **2.08x favorable** |
| 0-DTE (2 days) | 17 | **64.7%** | +179.25 | -- | -- | -- |
| Non-0-DTE (6 days) | 20 | 45.0% | +47.50 | -- | -- | -- |

Two other real-sample cells nearby: fast=6/slow=10/thr=0.5% (0-DTE n=16, 68.8% win, +114.20 net,
1.28x ratio -- higher win rate, weaker MAE/MFE and net) and fast=1/slow=15/thr=2.0% (0-DTE n=14,
71.4% win, +138.20 net, 1.77x ratio, but non-0-DTE net -41.45 at 33.3% win -- worse on the side that
isn't its intended venue). **5/10/0.5% is the pick**: best MAE/MFE ratio of the three, largest
0-DTE sample, positive net on every split.

**Honest comparison to the pre-fix number this replaces**: the OLD (contaminated) 0-DTE winner was
650/4/40/5%: 20 trades, **80.0%** win, +167.55 net, 2.5x ratio. The corrected candidate is real and
positive but clearly weaker: 64.7% vs 80.0% win rate, similar trade count, a comparable ratio (2.08x
vs 2.5x). **This is the expected, honest shape of the result** -- most of the old edge was the
splicing artifact (per the investigation above); what's left, once that's removed, is smaller but not
zero.

### Out-of-sample check, 2026-09-21/22 (already-populated days, never used in the sweep above)

| Day | DTE | Trades | Win% | Net |
|---|---|---|---|---|
| 2026-09-22 | 0-DTE | 18 | 50.0% | +116.75 |
| 2026-09-21 | non-0-DTE | 2 | 50.0% | -44.85 |

0-DTE OOS win rate drops from 64.7% (in-sample) to 50.0% -- a real, honest degradation, though net
stays positive on a real sample (18 trades). Non-0-DTE OOS is uninformative (n=2). **Per this
project's own "backtesting is a long-term process" rule: this is one data point, not a verdict** --
the in-sample number was already the best-supported real-sample cell in an 80-cell grid (some
overfitting to the 8-day window is expected structurally), and the OOS win rate, while lower, still
clears 50% with a positive net on a non-trivial sample. Neither confirms nor kills the candidate.

### Verdict

**A real, positive, but substantially smaller edge survives the correctness fix, confined to 0-DTE
Put-price crossover at bar=650 (fast=5/slow=10/thr=0.5%).** Non-0-DTE (bar=2600) and Call (both bar
sizes) show no viable candidate anywhere in this grid -- clean negative results, not "not swept
enough." This directly answers the open question from the investigation section: the underlying idea
(option premium momentum) is not dead, but it is much narrower than the pre-fix numbers suggested,
and what's left needs more out-of-sample validation before being treated as a settled finding, per
this project's own standing discipline.

### Scope not attempted, honestly noted

- Fast > 10 and slow > 40 were not swept -- the grid's own shape (win rate declining as windows
  widen past the best cells found) suggests this is unlikely to help, but not verified.
- Thresholds below 0.5% or above 3% were not swept.
- `DualAgreement`/`DualAgreementBothExit` were not re-swept under the corrected signal.
- Only one out-of-sample day per DTE regime was checked (both already-populated, no new data
  collected) -- a genuinely fresh, larger OOS set would be needed before this candidate could be
  called validated.
- EMA was not re-tried against the corrected signal in this sweep (the EMA-vs-SMA section earlier in
  this file used the OLD, contaminated signal throughout).

### Reproduction commands

```
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-calibrate 2026-09-08 2026-09-19 Put 1,2,3,4,5,6,8,10 3,5,8,10,15,20,30,40 0.5,1,1.5,2,2.5,3 650 --minprice=100 --maxprice=150 --targetmin=5 --targetmax=20
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-calibrate 2026-09-08 2026-09-19 Put 1,2,3,4,5,6,8,10 3,5,8,10,15,20,30,40 0.5,1,1.5,2,2.5,3 2600 --minprice=100 --maxprice=150 --targetmin=5 --targetmax=20
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-calibrate 2026-09-08 2026-09-19 Call 1,2,3,4,5,6,8,10 3,5,8,10,15,20,30,40 0.5,1,1.5,2,2.5,3 650 --minprice=100 --maxprice=150 --targetmin=5 --targetmax=20
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-calibrate 2026-09-08 2026-09-19 Call 1,2,3,4,5,6,8,10 3,5,8,10,15,20,30,40 0.5,1,1.5,2,2.5,3 2600 --minprice=100 --maxprice=150 --targetmin=5 --targetmax=20

# Top candidate detail + MAE/MFE
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-calibrate 2026-09-08 2026-09-19 Put 5 10 0.5 650 --minprice=100 --maxprice=150 --targetmin=5 --targetmax=20
dotnet run --project NiftySignal.VolumeBarData -- mae-mfe price-crossover 2026-09-08 2026-09-19 Put 5 10 0.5 650 --minprice=100 --maxprice=150

# Out-of-sample
dotnet run --project NiftySignal.VolumeBarData -- price-crossover 2026-09-22 2026-09-22 Put 5 10 0.5 650 --minprice=100 --maxprice=150
dotnet run --project NiftySignal.VolumeBarData -- price-crossover 2026-09-21 2026-09-21 Put 5 10 0.5 650 --minprice=100 --maxprice=150
```

## Joint bar-size x window sweep -- the re-sweep above was incomplete (2026-09-23)

**User's challenge, correct and acted on**: the re-sweep above only varied fast/slow/threshold at
TWO fixed bar sizes (650, 2600) carried over from the old, contaminated winners. Since a fast/slow
bar COUNT means a completely different volume horizon at a different bar size (`fast=5/slow=10` at
650-qty bars is nowhere near the same thing as `5/10` at 2600-qty bars), restricting to those two
sizes could easily miss the bar size the CORRECTED signal actually suits -- exactly the
bar-size/window-length confound this project's own earlier research already named (`docs/
VOLUME_BAR_FINDINGS.md`'s "Effective Volume Horizon" section). Confirmed all six already-populated
bar sizes (325, 650, 1300, 2600, 3900, 5200) exist for the full 8-day range before sweeping --
no new dataset created.

**Grid**: same fast/slow/threshold ranges as the re-sweep above, now crossed with bar in
{325, 650, 1300, 2600, 3900, 5200} -- 6x the prior grid's cell count (roughly 2,304 cells total
across both sides). `[100,150]` entry band, ATM-only signal, unchanged.

### Non-0-DTE: still no viable candidate, now confirmed across all 6 bar sizes

Best non-0-DTE win rate at any real-sample cell (n>=15), any bar size: **54.5%** (bar=5200,
fast=4/slow=10/thr=0.5%, n=17) -- still marginal, and no bar size produces a cell that clears ~55%
with real conviction. **This strengthens, not weakens, the earlier non-0-DTE negative conclusion**:
it isn't that 650/2600 were the wrong bar sizes, it's that non-0-DTE Put-price crossover doesn't show
a real edge in this 8-day sample at ANY bar size tested.

### Call: still no viable candidate, now confirmed across all 6 bar sizes

Best 0-DTE win rate at any bar size: 53.8% (n=13). Best non-0-DTE: 60.0% (bar=5200, n=25, but pooled
win rate only 45.0% -- inconsistent). **Call remains not promoted, now on a much broader search.**

### 0-DTE: the bar-size dimension mattered -- a materially stronger candidate exists at bar=1300

**bar=1300, fast=1/slow=8/thr=2.0%**: pooled 31 trades, 58.1% win, +185.85 net; **0-DTE 15 trades,
80.0% win, +111.10 net**; non-0-DTE 16 trades, 37.5%, +74.75. MAE 5.79%/MFE 14.28% -- **2.47x
favorable**, essentially matching the OLD (contaminated) winner's 2.5x ratio. A tighter-threshold
neighbor, fast=1/slow=8/thr=2.5%, pushes 0-DTE win rate to **90.9%** but on a smaller sample (n=11),
2.59x ratio. **This is a real, materially stronger candidate than anything found restricting to
650/2600** -- the user's instinct was correct, and the earlier "much weaker" verdict undersold what's
actually recoverable once bar size is treated as a real dimension, not assumed fixed.

### Out-of-sample check on the NEW bar=1300 candidate -- a bigger warning sign than the bar=650 one

| Candidate | In-sample 0-DTE win% (n) | OOS 2026-09-22 win% (n) | Drop |
|---|---|---|---|
| bar=650, fast=5/slow=10/thr=0.5% | 64.7% (17) | 50.0% (18) | -14.7pt |
| **bar=1300, fast=1/slow=8/thr=2.0%** | **80.0% (15)** | **53.3% (15)** | **-26.7pt** |

The bar=1300 candidate's headline number is more impressive in-sample but degrades MORE out-of-sample
than the bar=650 one, not less. **This is an important honesty check, not a footnote**: extending the
sweep from 384 cells (one bar size) to ~2,304 cells (six bar sizes) mechanically increases the odds
that SOME cell looks excellent purely by chance, even with no real underlying edge -- the same
"multiple-testing" caution this project's own findings have flagged before for smaller grids. An
80-90% win-rate cell surfacing out of 2,304 candidates is measurably less surprising under pure noise
than the same number surfacing out of 384. The steeper OOS degradation here is consistent with (not
proof of) that concern.

### Revised verdict

**The user's request was reasonable and changed the result**: bar size is a real, load-bearing
dimension, and restricting to 650/2600 alone (carried over from the old, contaminated winners)
understated what the corrected signal can find. A genuinely stronger 0-DTE candidate exists at
bar=1300. **But neither the bar=650 nor the bar=1300 0-DTE candidate should be treated as validated
yet** -- both degrade out-of-sample on the one available OOS day, the bar=1300 one more severely, and
the sheer size of the combined search space (2,304+ cells) makes an isolated strong in-sample cell
inherently less trustworthy than it looks. **The honest summary**: 0-DTE Put-price crossover shows a
real, recurring hint of edge across MULTIPLE bar sizes and window pairs (650/5/10, 1300/1/8 both
positive in-sample AND out-of-sample, just weaker OOS) -- that consistency across independently-found
cells is itself mildly encouraging -- but the exact best configuration is not yet pinned down and
both leading candidates need more out-of-sample days before being trusted as a settled recommendation.
Non-0-DTE and Call remain cleanly rejected across all six bar sizes.

### Scope not attempted, honestly noted

- Only Put was swept across all 6 bar sizes with the full fast/slow/threshold grid; Call was swept
  across all 6 bar sizes too but not cross-checked with MAE/MFE on its own best cells (already weak
  enough on win rate alone to not warrant it).
- No bar size beyond the 6 already populated (e.g. a genuinely fine 100-200 qty bar) was tried --
  would require populating new data, which needs approval per this project's standing rule.
- Only one OOS day per DTE regime was available to check both new candidates against.

### Reproduction commands

```
# Joint sweep, one bar size at a time (loop over 325,650,1300,2600,3900,5200)
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-calibrate 2026-09-08 2026-09-19 Put 1,2,3,4,5,6,8,10 3,5,8,10,15,20,30,40 0.5,1,1.5,2,2.5,3 1300 --minprice=100 --maxprice=150 --targetmin=5 --targetmax=20

# Best bar=1300 candidate detail + MAE/MFE
dotnet run --project NiftySignal.VolumeBarData -- mae-mfe price-crossover 2026-09-08 2026-09-19 Put 1 8 2.0 1300 --minprice=100 --maxprice=150

# Out-of-sample
dotnet run --project NiftySignal.VolumeBarData -- price-crossover 2026-09-22 2026-09-22 Put 1 8 2.0 1300 --minprice=100 --maxprice=150
dotnet run --project NiftySignal.VolumeBarData -- price-crossover 2026-09-21 2026-09-21 Put 1 8 2.0 1300 --minprice=100 --maxprice=150
```

## New tooling + full bar-size top-5 sweep + session/tick-quality first pass (2026-09-23)

**User's requests, four parts**: (1) always show trade count/net; (2) every sweep must report
top-5-by-metric (win rate, MFE, MAE, net), split by regime; (3) target 5-15 trades/day as the
standing ideal; (4) investigate whether the volume bar's own TICK COUNT and time DURATION
(independent of its qty threshold) matter, toward eventually sizing bars dynamically off those
fields. Standing rules updated at the top of this file; the reasoning is also saved to project
memory (`feedback_reporting_format_and_targets.md`, `feedback_dynamic_rules_not_fixed.md`).

### New tooling

- **`price-crossover-calibrate` now auto-prints a top-5-by-metric summary** after the grid, split
  0-DTE / non-0-DTE. Win rate and Net come straight from the already-computed grid (free). MAE/MFE
  are NOT computed for every cell (would require per-trade tick loading across potentially
  thousands of cells -- too expensive) -- instead, only the small UNION of cells that make a
  top-5-by-win-rate-or-net list gets re-run (cheap, cached price series) to compute REGIME-SPECIFIC
  average MAE%/MFE% (a real improvement over this session's earlier manual process, which had
  sometimes reported POOLED MAE/MFE for a candidate chosen by its 0-DTE-specific win rate --
  a mismatch). New flags: `--minsample=N` (default 10, the top-5 eligibility floor), `--notop5`
  (skip the summary for scripting), `--entrystart=`/`--entryend=` (session-of-day restriction,
  see below). Default `--targetmax` changed from 10 to 15 (new 5-15 standing target).
- **`SimulatePriceCrossoverDayAsync` gained `entryWindowStartOverride`/`entryWindowEndOverride`**
  (same convention `SimulateDayAsync` already used) -- lets any command in this track restrict
  entries to a session-of-day window (Open/Mid/Close) instead of the full 09:30-15:00. Threaded
  through `price-crossover`/`price-crossover-calibrate`/`mae-mfe price-crossover` as
  `--entrystart=`/`--entryend=`. Default (both null) unchanged from every prior result in this file.
- **New diagnostic `entry-bar-quality`**: re-runs the real simulator, joins each trade back to its
  own entry bar's `TickCount`/`DurationSeconds` (already stored on `VolumeBarRow`, never used for
  anything before), and reports win rate by tercile of each. Read-only, no trading logic touched.

`dotnet build`: 0 warnings/0 errors. `dotnet test`: 770/770 passing (no new tests for the reporting/
diagnostic additions themselves -- consistent with this file's existing convention that CLI-only
reporting tools are verified by running them and reading the output, not asserted on; the
underlying `SimulatePriceCrossoverDayAsync`/`PriceCrossoverEngine` changes already carry their own
test coverage from prior sections).

### Full bar-size sweep, all 6 populated sizes (325/650/1300/2600/3900/5200), Put side, top win-rate cell per bar size

**0-DTE** (min sample n>=10, 2 days):

| Bar | Fast/Slow/Thr% | Trades | Win% | Net | MAE% | MFE% | Ratio |
|---|---|---|---|---|---|---|---|
| **1300** | **1/8/2.5%** | 11 | **90.9%** | +111.75 | 5.46% | 18.75% | **3.43x** |
| 1300 | 1/8/2.0% | 15 | 80.0% | +111.10 | 5.65% | 15.09% | 2.67x |
| 2600 | 3/5/1.0% | 15 | 73.3% | +185.10 | 7.16% | 19.36% | 2.70x |
| 325 | 1/10/2.5% | 11 | 72.7% | +148.80 | 7.00% | 20.71% | 2.96x |
| 650 | 1/15/2.0% | 14 | 71.4% | +138.20 | 7.02% | 17.95% | 2.56x |
| 3900 | 1/3/2.5% | 14 | 71.4% | +223.95 | 6.98% | 21.78% | 3.12x |
| 5200 | 1/3/2.0% | 15 | 66.7% | +194.10 | 4.59% | 19.79% | **4.31x** |

**Non-0-DTE** (min sample n>=10, 6 days) -- best win rate at ANY bar size, for reference:

| Bar | Fast/Slow/Thr% | Trades | Win% | Net | MAE% | MFE% |
|---|---|---|---|---|---|---|
| 5200 | 1/10/2.0% or 2/8/1.0% | 11 | 54.5% | +28.00 / +123.40 | 9.81% / 6.48% | 21.49% / 17.24% |
| 325 | 1/10/1.0% | 66 | 54.5% | +57.60 | 5.04% | 6.52% |
| 650 | 5/8/0.5% | 12 | 58.3% | +42.95 | 6.98% | 11.76% |

**Non-0-DTE conclusion unchanged and now more robust**: no bar size among all six produces a
real-sample cell clearing ~55-58% win rate. This was checked, not assumed, across the full
combinatorial space -- non-0-DTE Put-price crossover does not show a usable edge at ANY bar size
tested in this 8-day window.

**0-DTE conclusion, revised**: bar=1300's fast=1/slow=8 window family is the strongest candidate
found so far by win rate (80-91%) AND has a favorable-to-very-favorable MAE/MFE ratio, but bar=5200's
fast=1/slow=3/thr=2.0% has the single BEST MAE/MFE ratio (4.31x) at a still-strong 66.7% win rate.
**Per the user's own "don't force one fixed answer" instruction, these are presented side by side,
not collapsed into one pick** -- the user's call which tradeoff (higher win rate vs. better
excursion ratio) matters more for the eventual live rule.

**Multiple-testing caution restated**: this sweep now covers 6 bar sizes x ~336 fast/slow/threshold
cells x 2 sides = over 4,000 cells total. The bar=1300 candidate's 80-90% win rate should be read
with the same skepticism the earlier OOS check already surfaced (bar=1300's own OOS run dropped to
53.3% -- see the section above) -- a wider search makes an isolated high number LESS trustworthy on
its own, not more.

### Session-of-day drill-down, first pass (bar=1300, fast=1/slow=8/thr=2.0%, 0-DTE days only)

| Session (IST) | 2026-09-08 | 2026-09-15 |
|---|---|---|
| Open (09:30-10:30) | 1 trade, 100% win, +6.20 | 3 trades, 100% win, +34.50 |
| Mid (10:30-13:30) | 1 trade, 100% win, +19.10 | 3 trades, 100% win, +26.05 |
| Close (13:30-15:00) | 4 trades, 25% win, -13.25 | 4 trades, 75% win, +37.85 |

**Directionally suggestive, not conclusive on 2 days**: Open/Mid sessions are clean (100% win on
every trade fired) on both days; Close is visibly weaker and where the one bad day's losses are
concentrated. Consistent with 0-DTE theta/gamma intuition (the final 90 minutes is the most
decay-pressured, highest-gamma stretch) but this is 2 days of data -- needs more days before being
treated as a rule, not a hint.

### Tick-count / duration first pass (does bar "quality" matter, independent of its qty size?)

Two candidates checked, all 8 days pooled:

| Candidate | Winners avg TickCount/Dur | Losers avg TickCount/Dur | Low tercile win% | Mid tercile win% | High tercile win% |
|---|---|---|---|---|---|
| bar=1300, 1/8/2.0% | 60.5 / 37.4s | 71.2 / 43.5s | **80.0%** | 45.5% | 50.0% |
| bar=650, 5/10/0.5% | 30.8 / 17.7s | 30.2 / 17.6s | **66.7%** | 38.5% | 50.0% |

**A consistent, real pattern across both independently-found candidates**: the LOWEST tercile of
entry-bar tick-count/duration (i.e., bars that filled FASTEST, meaning the highest tick velocity at
that moment) has the best win rate in both cases, meaningfully ahead of the middle and high
terciles. Not perfectly monotonic (HIGH beats MID in both cases), but the LOW-tercile edge is
consistent and non-trivial (14-28 points of win rate). **This is directionally consistent with this
project's own earlier tick-activity research** (`docs/VOLUME_BAR_FINDINGS.md`'s TickVelocity work),
which is reassuring rather than a coincidence -- a fast-filling bar likely reflects a genuine burst
of directional activity, exactly the kind of bar a momentum entry should want to catch.

**This is a first pass, explicitly scoped, not the full "detailed experiment" the user asked for.**
What it establishes: the signal is worth pursuing further. What it does NOT yet establish: an actual
dynamic bar-sizing rule (e.g. "use a smaller qty threshold when recent tick velocity is high") --
that requires a dedicated follow-up task building and testing such a rule, not just observing the
correlation on trades an already-fixed-size bar happened to produce.

### Scope not attempted, honestly noted

- The bar-size table above shows only the SINGLE best win-rate cell per bar size -- each bar size's
  own full top-5-by-metric output (already generated) has more candidates than shown here; ask for
  any specific bar size's full breakdown.
- Session-of-day drill-down was only run for one candidate, on 0-DTE days only, with a fixed
  Open/Mid/Close split (09:30-10:30/10:30-13:30/13:30-15:00) -- not yet run for non-0-DTE days, not
  yet run for other candidates, and the session boundaries themselves were not tuned.
  Non-0-DTE was not drilled down by session at all yet.
- Tick-count/duration was checked for only 2 candidates, pooled across all 8 days (not split by
  DTE regime or session) -- a real "detailed experiment" would check this per regime, check
  interaction with session-of-day, and attempt an actual dynamic-threshold rule, not just observe
  the tercile pattern on a fixed-size bar's own trades.
- No dynamic/adaptive bar-sizing rule has been built or tested yet -- this section only establishes
  that tick velocity looks like a promising input for one.

### Reproduction commands

```
# Full per-bar-size sweep (loop over 325,650,1300,2600,3900,5200)
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-calibrate 2026-09-08 2026-09-19 Put 1,2,3,4,5,6,8,10 3,5,8,10,15,20,30,40 0.5,1,1.5,2,2.5,3 1300 --minprice=100 --maxprice=150 --targetmin=5 --targetmax=15 --minsample=10

# Session-of-day drill-down
dotnet run --project NiftySignal.VolumeBarData -- price-crossover 2026-09-08 2026-09-08 Put 1 8 2.0 1300 --minprice=100 --maxprice=150 --entrystart=09:30 --entryend=10:30
dotnet run --project NiftySignal.VolumeBarData -- price-crossover 2026-09-08 2026-09-08 Put 1 8 2.0 1300 --minprice=100 --maxprice=150 --entrystart=13:30 --entryend=15:00

# Tick-count/duration check
dotnet run --project NiftySignal.VolumeBarData -- entry-bar-quality 2026-09-08 2026-09-19 Put 1 8 2.0 1300 --minprice=100 --maxprice=150
```

## Dynamic tick-velocity bar-sizing: built and tested (2026-09-23)

**User's explicit request**: build the dynamic tick-velocity bar-sizing rule the prior section's
first pass motivated, and test it. Also: prior sweep output didn't make clear whether a "Trades"
count was per-day or pooled across the whole range -- fixed throughout (see below).

### Reporting-clarity fix (applied everywhere in this track going forward)

Every trade-count/net figure in this file's tooling now explicitly states whether it is POOLED
(summed across every day in a group) or PER-DAY, never left ambiguous. `price-crossover-calibrate`'s
grid header and its top-5 tables now show both `TradesTotal` (pooled) and `TradesPerDay`
(`TradesTotal / <day count in that regime>`) as separate columns, with the day count for each group
stated explicitly in the section header. `price-crossover-dynamic` (below) prints each day's own
numbers labeled "THIS DAY" as it runs, then a separate POOLED summary at the end, also explicitly
labeled. **Any trade count anywhere in this document should now be read as: check the label; if in
doubt, it's pooled unless "per day" or "THIS DAY" is stated.**

### The rule, built

**New code**: `NiftySignal.Features.VolumeBarBuilder` gained an opt-in `dynamicThresholdProvider`
constructor parameter (`Func<long?>`, default null) -- consulted ONCE at the exact moment each new
bar starts (never mid-bar, so a bar's own threshold can never move while it's still forming), falls
back to the original fixed threshold if the provider returns null. Default (no provider) is
byte-identical to the original class -- 4 new unit tests confirm this plus the new behavior's own
determinism (`VolumeBarBuilderTests.cs`). New `NiftySignal.VolumeBarData.DynamicTickVelocityBarBuilder`
builds one day's future volume bars entirely IN-MEMORY (never persisted -- no new dataset) using
this capability: at each bar start, it compares RECENT tick velocity (ticks/sec over the trailing 60
seconds) against a ROLLING BASELINE (ticks/sec over the trailing 20 minutes, or less early in the
session), both computed causally from only already-observed ticks. `recent/baseline >= 1.3` ->
busy -> use the smaller/finer threshold (650 default); `<= 0.7` -> quiet -> use the larger/coarser
threshold (2600 default); otherwise the middle threshold (1300 default). These three values reuse
this project's own three most-common bar sizes rather than inventing new numbers. **Explicitly a
first, simple rule** -- the 60s/20min windows and 1.3/0.7 ratio cutoffs are stated plainly as
starting choices, not derived/tuned.

`TradeSimulator.SimulatePriceCrossoverDayAsync` gained an optional `prebuiltBars` parameter (null by
default, unchanged for every existing caller) so a caller can supply an already-built in-memory bar
sequence instead of querying `VolumeBarDbContext`'s fixed-threshold rows -- this is what lets the
dynamic bars feed straight into the real, unmodified trading/exit logic, not a parallel
reimplementation of it. New CLI `price-crossover-dynamic`. `dotnet build`: 0 warnings/0 errors.
`dotnet test`: 774/774 passing (770 baseline + 4 new `VolumeBarBuilder` dynamic-threshold tests).

### Test results -- an honest negative result against both leading static candidates

Tested the two strongest 0-DTE candidates found in the prior bar-size sweep, feeding the SAME
fast/slow/threshold window through dynamic bars instead of the static bar size that produced each
one's original result:

| Candidate window | Static (fixed bar size) result | Dynamic (tick-velocity-sized) result |
|---|---|---|
| Put 1/8/2.0% | bar=1300: **15 trades, 80.0% win**, +111.10 net (pooled, 2 0-DTE days) | **17 trades, 64.7% win**, +101.40 net (pooled, 2 0-DTE days) |
| Put 5/10/0.5% | bar=650: **17 trades, 64.7% win**, +179.25 net (pooled, 2 0-DTE days) | **17 trades, 52.9% win**, +85.05 net (pooled, 2 0-DTE days) |

**The dynamic rule, as built, underperforms the static baseline on both tests -- reported
honestly rather than reframed as a partial win.** Win rate dropped 15.3pt and 11.8pt respectively;
net dropped too, though stayed positive in both cases.

### Why, honestly assessed rather than left unexplained

The regime-count logs (e.g. `low=0 med=1528 high=137` for 2026-09-15) show the LOW-velocity regime
almost never fires -- the 0.7x ratio cutoff against a 20-minute rolling baseline is rarely crossed
in practice, so the "coarse when quiet" half of the rule is nearly inert; most of the day runs on the
medium (1300) threshold with occasional excursions into the high (650) regime. More importantly:
**the crossover engine's own fast=N/slow=M window is a BAR-COUNT window, not a volume-horizon
window.** When bar size changes bar-to-bar, an "8-bar slow window" no longer represents a consistent
amount of traded volume the way it does under a fixed bar size -- exactly the bar-size/window-length
confound this project's own earlier research already flagged (the "Effective Volume Horizon" section
in `docs/VOLUME_BAR_FINDINGS.md`), now showing up as a real cost rather than just a measurement
caveat. Varying the bar size while holding the window's BAR COUNT fixed likely undermines the very
consistency that made the static-bar-size candidates comparable in the first place.

### Verdict

**Built and tested, as asked -- the first version does not beat static sizing and should not be
adopted as-is.** This is a genuine, useful negative result, not a wasted exercise: it demonstrates
directly (not just via the `entry-bar-quality` correlation) that naively swapping a fixed threshold
for a velocity-adaptive one, while leaving everything else (bar-count window) unchanged, is not
sufficient. A follow-up that wants to pursue this further would need to address the
volume-horizon-consistency issue directly -- e.g. sizing the fast/slow window in trailing VOLUME
terms rather than bar COUNT, so an "8-bar" window and a "so-many-contracts-traded" window mean the
same thing regardless of which threshold produced any given bar -- rather than just re-tuning the
60s/20min/1.3/0.7 constants in the current rule.

### Scope not attempted, honestly noted

- Only 2 candidate windows were tested against dynamic bars, both already known-strong under static
  sizing -- no fresh grid search was run UNDER the dynamic bars themselves (a window tuned FOR the
  dynamic bars' own characteristics might behave differently than one tuned for a fixed bar size).
- The ratio cutoffs (1.3/0.7) and window lengths (60s/20min) were not tuned or swept at all --
  first-guess values only, explicitly flagged as such above.
- The volume-horizon-consistency fix suggested above (sizing the crossover window in volume terms,
  not bar count) was not attempted -- flagged as the most promising next step, not yet built.
- Call side and non-0-DTE were not tested against dynamic bars (non-0-DTE already showed weaker
  regime activity in the two dumps above -- e.g. "low=0" on several non-0-DTE days -- consistent
  with, not yet separately confirmed as, the same pattern).

### Reproduction commands

```
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-dynamic 2026-09-08 2026-09-19 Put 1 8 2.0
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-dynamic 2026-09-08 2026-09-19 Put 5 10 0.5
dotnet run --project NiftySignal.VolumeBarData -- price-crossover-dynamic 2026-09-08 2026-09-19 Put 1 8 2.0 --low=5200 --medium=2600 --high=1300
```
