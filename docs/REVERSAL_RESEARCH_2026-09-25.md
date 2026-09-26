# Reversal research — 2026-09-25

Branch: `codex/reversal-contract-research`. Research only; no live changes, orders, or commits.
All new calculations reconstruct raw ticks in memory. Output files are experiment reports and
decision traces, not reusable backtest datasets. Frozen strategy/forward artifacts are untouched.

## Pre-run audit and experiment register

The existing `PerStrikeCadenceSimulator` uses independent token histories, but breaks its
strike loop on entry, skipping other engines that cadence (F63). It defaults to 2/10 readings,
not 8/40, fills at pre-signal LTP, and carries windows across missing intervals. Its P&L is
diagnostic, not executable. `PutCadenceExporter` uses (start,end] intervals and 8/40 readings;
missing intervals are skipped, so the actual elapsed windows can exceed 2/10 minutes.
`PriceCrossoverEngine` applies a threshold on the crossing reading only; later gap growth is
not confirmation. Rolling ATM resets fix splicing but do not implement independent contracts.
Historical SMA/EMA grids, band/window/bar-size sweeps and dynamic-velocity trials are research
selection, not independent evidence. Their earlier mixed-contract winners are inadmissible.

Pattern A remains futures rising, same-token CE falling and PE rising; B is the opposite.
Reuse the existing 13K futures builder and causal 180-second context, preserve state-entry
and full-surface definitions for a reference. Reproduce signals separately from trading.
The frozen execution uses 10 lots, zero added slippage, LTP fallback and incomplete costs.
`TransactionCostCalculator` still uses 0.0625% STT; NSE states 0.15% applies from April 2026.
It is preserved for frozen reproducibility, not adopted for the new experiment.
`FlatTradeFeedState.ApplyDelta` carries prices/depth forward; individual last-trade and depth
refresh timestamps are not persisted. Tick age is NOT guaranteed quote age. Even a strictly
later bid/ask model cannot prove fills actually obtainable. All new P&L is conditional modeled
execution, not independently verified executable profit.

Research dates already inspected: September 4, 8, 9, 10, 11, 15, 16, 17, 18, 21, 22, 23.
September 24 is consumed OOS and excluded from every new test. September 25 is excluded from
strategy tests; only metadata completeness/stability checks are allowed. No future evidence
is claimed. Date/session, not individual trade, is the independence unit.

## Predeclared experiments (no optimization grid)

R0 correctness/inventory: enumerate source dates and per-token cadence coverage; independently
recalculate sampled averages from raw ticks. Trace all decisions and several full tick paths.
Abort confidence in aggregates on contract, cadence, causality or fill-order failure.

C0 option-only baseline: each nearest-expiry NIFTY token gets (start,end] 15-second mean stored
LTP, SMA 8/40; an empty interval resets history. Buy its own upward crossing; exit its own
downward crossing. No futures signal or ATM selection. Eligible ask premium Rs100–150;
choose smallest relative spread then token at decision time. One position per experiment,
Calls and Puts simulated separately. Warm-up is 40 consecutive intervals plus previous gap.
Entry before 15:00; scheduled liquidation 15:15. No forced trade count.

C1 adaptive crossing gap: same C0 except require current positive fast-minus-slow price gap
to cover the current spread, two ticks of assumed round-trip slippage, and modeled round-trip
fees per unit. Hypothesis: crossing weak relative to immediate friction is not worth buying.
Expected fewer trades, better net/entry and fewer losing sessions; fail if suppression mainly
removes profitable trades or performance depends on best day. No fitted gap constant.

C2 cross then confirm: same adaptive hurdle, but arm at an upward crossing and allow the first
later positive-gap observation clearing the CURRENT hurdle; cancel on gap <=0, missing
interval, or after one fast-window (8 intervals). One signal per bullish episode. Compare
C1 first (only confirmation timing changes), then C0. Hypothesis: natural gradual crossings
need time to overcome costs. Fail if delayed entries degrade exact-token forward returns,
net/session or drawdown; report lateness, do not tune the timeout after results.

A0/B0: original 13K/180s full-surface state entries, original opposite ATM-pattern state-entry
exit; trade eligible Rs100–150 contract selected on same-time quotes, separately report A->PE
and B->CE. This is a research execution variant, NOT the frozen ATM/10-lot strategy.
A1/B1: identical signals/exits/selection, but suppress entry when the selected token's current
LTP-minus-trailing-40-cadence mean exceeds the largest comparable extension in its previous
40 complete observations. Hypothesis: a new local extreme can mean entry is already late.
No future data; no gate until the full reference history exists. Fail if suppression removes
the few payoff-driving trends or does not improve session robustness. Report same-entry
suppressed-trade counterfactuals as well as sequential outcomes.

Execution assumptions fixed before outcomes: one instrument-master lot; decision-time valid
positive non-crossed bid/ask with displayed quantity >= one lot and tick age <= one cadence;
first valid tick at least one second after decision, within the following cadence. No LTP
fallback; add one instrument tick against each fill. Exit orders stay pending until a valid
quote; do not reopen before exit fill. Unresolved positions are explicit failures, never
zero-price closes. One-second latency/one-tick slippage/one-cadence quote-age are uncalibrated
assumptions, not discovered edge. Store true fill times and mark carried-field age unknown.
Fees: sell STT .15%; exchange .03503% both sides (assumption pending source confirmation),
SEBI .0001% both sides, buy stamp .003%, GST 18% on exchange+SEBI; zero brokerage.

Measure forward underlying direction for A/B (1/2/4 bars with direction/magnitude controls),
and exact-token LTP forward returns at 1/2/5 minutes for every selected opportunity before
trade outcomes. LTP MFE/MAE are diagnostics. Report session and DTE by side, net, PF, drawdown,
win%, duration, best trade/day concentration, zero trades, and unresolved outcomes.

Sources: [NSE STT](https://www.nseindia.com/static/products-services/equity-derivatives-securities-transaction-tax),
[Flattrade stamp duty](https://flattrade.in/support/knowledge-base/what-stamp-duty-calculation-on-trades-are-there-on-flattrade/),
[Groww tariff](https://groww.in/pricing/futures-and-options).

## Run log (append only)

1. Inventory: database is `niftysignal_vm_copy`, 14 instrument dates (12 research dates,
   consumed September 24 and provisional September 25). Read-only DB session enforced.
2. `research-reversal-run01`, September 4 only: all five predeclared variants executed as
   an initial correctness audit. 2,103,824 option ticks, 40 nearest-expiry tokens. Sampled
   means matched raw (start,end] snapshots. **INVALID FOR EDGE: receipt-time leakage found**:
   thousands of snapshots per token arrived after their nominal bucket boundary; max delays
   exceed 20 seconds. Keep the report as failed evidence, not a candidate result.
   C0 CE 13/+288.78, C1 CE 2/+346.83, C2 CE 11/+356.07; C0 PE 12/+947.84,
   C1 PE 0/0, C2 PE 8/+1647.61; P0 2/+2716.94; P1 1/+990.61 (N/rupees).
   These numbers are explicitly superseded for correctness, not for being unattractive.
3. Receipt-time correction, before any multi-session run: cadence averages include only
   snapshots received by the bucket close. Late snapshots are excluded, not retroactively
   inserted into an emitted cadence. As-of selection also checks receipt time. Future-bar
   decision availability is the maximum receipt time of all ticks used so far. Fill times
   are no earlier than both exchange and receipt timestamps. Regression tests cover late
   data and the difference between same-observation and delayed gap confirmation.
   Stored LTP can itself be carried by feed updates: these means are **snapshot-weighted
   LTP means**, not exchange trade-print VWAP or a count of independent option transactions.
4. `research-reversal-run02`: interrupted after September 4/8 when a stale compilation was
   detected (source edited while the prior compiler was running). This intermediate binary
   had receipt checks but not the complete freshness/reference/trace audit additions. Reports
   remain on disk, excluded from inference. An attempted `research-stability-first` command
   encountered the same stale binary, started loading September 4 and was interrupted; it
   did not evaluate September 25. No hyperparameter changed.
5. Forced rebuild with `-t:Rebuild`: zero warnings/errors. Full test suite: 1082/1082 pass.
   Run03 is the complete audited implementation. A/B reference states retain exchange-time
   legacy as-of lookup strictly for comparison; actual research states require a snapshot
   within one cadence at BOTH context endpoints and known receipt time. Future-bar signals
   wait until every contributing future tick has arrived. These corrections can change
   episode boundaries. P0/P1 are therefore explicitly research variants, not new frozen
   forward-validation results. September 25 receives metadata checks only.

## Complete results and verdicts (run03)

The complete session/DTE tables, exact-token forward response, trade statistics, controls,
suppression counterfactuals and raw decision traces are in
`research-reversal-run03/REPORT.md`. The source reports are the twelve dated JSON files in that
directory. `scripts/summarize-reversal-research.ps1` regenerates and audits the report.

Pattern A produced 70 corrected FullSurface state entries. Expected-direction futures hit
rates were 64.3%/61.4%/68.6% at +1/+2/+4 bars and mean +4 reversal was 5.81 points. Pattern B
produced 63 entries: 69.8%/68.3%/61.9%, mean +3.91 points. Day tables show important failures
and control reversals, especially September 9/17/18/23; this is suggestive relationship evidence,
not universal behavior.

The purchasable option weakens the case. A-selected PE mean return was -0.15%/-0.13%/+0.70%
at +1/+2/+5 minutes; B-selected CE was +0.21%/-0.37%/+0.78%. At the executable lifecycle,
A had 31 trades, Rs+1,976, PF 1.15, 45.2% wins and 6/12 positive days; without its best day,
Rs-6,079. B had 25 trades, Rs+546, PF 1.06, 28.0% wins and 3/12 positive days; without its
best day, Rs-5,435. The overextension filter did not rescue this: combined P1 Rs+995/PF 1.04,
and it suppressed a Rs+4,463 winner. **Verdict for A and B as option-buying methods: rejected
at present.** Their underlying reversal relationship is **promising but unproven**, and neither
has independent forward support under the corrected receipt/freshness/execution methodology.

Option-only Calls: baseline C0 lost Rs9,548 on 268 trades (22.3/day, PF .82); delayed C2 lost
Rs6,072 on 216 (18/day, PF .87). The friction-clearing C1 reduced this to 17 trades and showed
Rs+1,969/PF 1.48, but the exact-token candidate response was negative at 1/2/5 minutes and the
result becomes Rs-1,925 without its best day. **All Call variants rejected.**

Option-only Puts: baseline C0 was essentially flat after modeled costs (Rs-98, 263 trades,
PF 1.00), while delayed C2 was Rs+6,182/PF 1.17 on 211 trades but becomes Rs-2,820 without
its best day and still averages 17.6 trades/day. **C0 and C2 rejected.** C1 (gap must cover
current spread, two ticks and fees on the actual crossing observation) produced 17 trades,
Rs+4,814, PF 3.30, 47.1% wins, six positive days, one no-trade day, max closed-trade drawdown
Rs636 and Rs+1,315 after removing its best day. Mean exact-token return was +1.81%/+2.35%/
+0.65% at +1/+2/+5 minutes, but only 46.4% were positive at +5: a few right-tail moves drive
the mean. DTE is inconsistent (positive for DTE 0/5/6, negative DTE 1/4), sample sizes are tiny,
and every date was already research data. **C1 Put verdict: promising but unproven.** It is the
only hypothesis worth freezing unchanged for future settled sessions; no historical tuning,
production change or independent-evidence claim is justified.

September 25 metadata was sampled twice 162 seconds apart: both snapshots were byte-equivalent
by token metadata (83 tokens, 4,361,772 ticks, 09:15-15:30 coverage). It now looks stable at that
metadata level, but remains excluded exactly as predeclared and is not independent validation.
All four frozen artifact hashes remained unchanged.

Reproduction:

```powershell
dotnet build NiftySignal.slnx --no-restore -v minimal
dotnet test NiftySignal.slnx --no-restore -v minimal
dotnet NiftySignal.VolumeBarData/bin/Debug/net10.0/NiftySignal.VolumeBarData.dll reversal-research research-reversal-run03
./scripts/summarize-reversal-research.ps1 -RunDirectory research-reversal-run03
dotnet NiftySignal.VolumeBarData/bin/Debug/net10.0/NiftySignal.VolumeBarData.dll reversal-research research-stability-check --snapshot25
```
