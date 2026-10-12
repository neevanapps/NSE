# NPT1 paper three — live PAPER implementation (`feature/npt1-paper-three-live-v1`)

Specification: `Nifty_Paper_Three_V1_20261012.md` (the user's handoff) plus the user's amendments of 2026-10-12: live entry fills 09:30:00 <= t < 15:00:00 IST, the 15:00 terminal protocol, independent strategies,
and the decision to run the strategies on a NEW parallel M1 producer (`docs/NPT1_M1_PRODUCER_PLAN.md`) while the production Adaptive Observer (`adaptive-v1`) stays untouched.

**PAPER ONLY.** No broker, order, HTTP or messaging type exists anywhere in this path (release-blocker tests: `BrokerIsolationTests`). All three strategies ship **OFF**.
Nothing was merged, deployed or enabled, the VM was not touched, and no sealed (after 2026-10-01) data was opened.

## 1. What runs

```
raw ticks (shared table, read-only) ──► Npt1M1Worker (own hosted worker, own reader, own schema npt1m1)
      M1SessionBuilder: instruments + prior volumes ──► M1 freeze (OpeningVolumeProjectionV2, beta 0.5)
      M1BarProducer = the UNCHANGED AdaptiveEventTranslator / AdaptiveObserverEngine over the M1 base bar
      ──► immutable AdaptiveStrategyFrame per completed M1 bar (stored in npt1m1.frames, hash-protected)
      Npt1M1StreamProcessor ──► PaperLiveCoordinator ──► durable LiveEngineRun (trading schema, codec 5)
            NPT1-MD5 / NPT1-MDX3 / NPT1-PE5  (families, one variant MAIN each)  ──►  bar-clocked execution policy
```

* The strategy layer never reads ticks or the database: it sees one frame per completed M1 bar.
* The producer shares **no table, key, runtime state, session id or cursor** with adaptive-v1 (`npt1-m1-v1`, `npt1m1.*`). adaptive-v1 files are byte-identical to master (`git diff 4a0c9ff -- NiftySignal.AdaptiveObserver NiftySignal.AdaptiveObserverData NiftySignal.Host/Adaptive*.cs` is empty).
* Frame metrics (all read from the package of the bar itself): `m = Microstructure.MicroDevTimeWeighted` (`fmicro_microdev_time_weighted`), `w = Microstructure.MicroDevUsableSeconds` (`fmicro_microdev_usable_s`),
  `P = OptionBand.Put.ContractStrictDelta` (`band_pe_contract_strict_delta`), band centre/expiry = `OptionBand.Selection` (`band_center_strike`), `flag_ready = BarReadiness.Build(...)`. A missing metric is `null` and never zero.

## 2. Strategy rules as implemented (`NiftySignal.Trading.Signals/Npt1Families.cs`)

| | rule |
|---|---|
| shared | `R[t] = Σ(m·w)/Σ(w)` over the last 10 bars; all 10 `m` and `w` present and `Σw > 0`, else unavailable. Windows include completed pre-09:30 bars. A new trade date or any bar-number gap resets the windows (no fabricated history). A decision needs `flag_ready`. |
| NPT1-MD5 | `A5` = mean of the last 5 `R` (all present). `A5>0` → target long (CE), `<0` → short (PE), exactly 0 → retain. Unavailable `A5` skips the decision (no retry). A retained target is re-emitted every bar, which is the retry of a cancelled entry; same-side signals never add. First decision at bar 14. |
| NPT1-MDX3 | `F`=mean last 3 `R`, `S`=mean last 10 `R`, `d=F−S`. CE: `d[t−1]<=0 ∧ d[t]>0 ∧ F>0`; PE: `d[t−1]>=0 ∧ d[t]<0 ∧ F<0`. An unqualified opposite crossover holds. Cancelled entries are not retried. Earliest event: bar 20. |
| NPT1-PE5 | `x=−P`, `F`=mean last 5 `x`, `S`=mean last 10 `x`, `d=F−S`, `margin=0.20·|S|` (0 when `S=0`). CE: `d[t−1]<=margin[t−1] ∧ d[t]>margin[t]`; PE: `d[t−1]>=−margin[t−1] ∧ d[t]<−margin[t]`; each previous comparison uses the PREVIOUS bar's margin; no F-sign gate; emits CE and PE. Earliest event: bar 11. |

Interpretation notes (the specification is silent): the target of MD5 is updated only on bars with `flag_ready` (a decision needs readiness); the previous-`d` state of MDX3/PE5 is updated on every bar
(it is data, not a decision).

## 3. Execution (bar-clocked policy `NextCompletedBarEntrySpec`, label `npt1-live1500-v1`)

Per completed bar, in this order: pending exit → pending entry → position marks → the new signal. A signal on bar *t* freezes ONE contract: side, exactly the band expiry, **among the frame's candidate strikes**,
ask in [100, 150] inclusive, two-sided quote age in [0, 2 s] inclusive, ranked nearest strike to the band centre, then |ask−125|, then the lower strike, then the token. It may fill **only on bar *t+1*, at that
token's ask**; otherwise the entry is cancelled (visibly, with a reason) and never delayed or substituted. Quantity is exactly 650 (a contract whose lot does not divide 650 fails closed at configuration, never rounded).
Opposite signal → exit starting *t+1* at the last two-sided bid (age not gated, only flagged), then the opposite entry. One position per strategy; the three may hold simultaneously. No stop, TP, TP1, trailing,
max hold, trade cap or cooldown exists for these variants (the configuration validator refuses them).

* **Window.** Live fills are allowed for 09:30:00 <= t < 15:00:00 IST. 11:00 exists only in the historical-parity profile (`npt1-hist1100-v1`, verification only, never live).
* **Terminal protocol at 15:00:00.** Every open position is closed at the held token's latest observed positive bid at the terminal instant (stamped 15:00:00, bid time and age recorded, nothing backdated). With no
  bid at all it stays visibly pending (`MandatoryExitPending`) and the first later bid closes it as `DelayedTerminalClose`; at the hard end 15:35 it is reported `UnresolvedAtClose`. No 15:10 semantics.
* **Candidate strikes.** The frozen research evidence could only choose among the contracts of its per-bar contract table: the bar's changing ATM±2 band **united with** the session's frozen 09:30 anchor basket.
  The frame carries exactly that union (`CandidateStrikes`) and the selection honours it. (Without it the engine would take contracts the evidence never could; found by the parity run, see §6.)
* **Marks (MAE/MFE).** Gross, `650·(bar-end bid − entry ask)`, clipped at 0, observed at completed-bar ends; a stale (>2 s) bid is counted as a gap and not marked.

## 4. Restart and recovery

The frame is journaled inside the `BarCompleted` input (codec `trading-codec/5`); strategy state is part of the state digest; a replay rebuilds it. A restart rebuilds the M1 producer by re-reading the day's ticks
(deterministic, hash-verified against `npt1m1.frames`). Tests (`Npt1EndToEndTests`): A flat/warm, B open position, C entry pending, D exit pending, E three positions, F downtime gap (no trade fabricated, windows
re-warm), G idempotent 15:00 close, plus a fault-injected rebuild. Bars that completed while nobody observed (the stream processor's catch-up rule) are never given to the engine as decisions.
Found and fixed on the way: a rebuilt run must use NEW instances of stateful families (`PaperLiveStrategy.Fresh`), otherwise a rebuild after a store fault diverged on replay.

## 5. Configuration and installation (nothing applied by this work)

```
Trading:PaperLive:Enabled                   false   (source default)
Trading:PaperLive:Strategies:NPT1-MD5:Enabled   false   (also NPT1-MDX3, NPT1-PE5)
Trading:PaperLive:Capital                   required, never defaulted (see §7)
Trading:PaperLive:BrokeragePerOrder         must be 0 for NPT1 (frozen fee model)
Trading:PaperLive:OptionTickSize            required
```

Refused at start: NPT1 together with the plumbing family, an unknown strategy key, non-zero brokerage. Order of installation when the user decides to deploy: (1) `NiftySignal.Trading.Data/Sql/trading-schema.idempotent.sql`
(adds migration `Npt1CodecV5`: codec 5, engine label `trading-engine/npt1-v1`); (2) `NiftySignal.Npt1M1.Data/Sql/npt1m1-schema.idempotent.sql`; (3) set the switches. Both scripts are idempotent
and are never applied implicitly by the application.

## 6. Verification evidence (all on authorized development data, dates <= 2026-10-01)

**M1 producer parity** (`tools/Npt1M1Parity`, local copy of the tick database, research exports `bar-size-v2/M1`): 17 of 17 development sessions (2026-09-08 .. 2026-10-01) identical bar by bar: bar count,
start/end instants, `flag_ready`, `m`, `w`, `P`, band centre; base-bar volume and opening volume of the freeze equal the export's session state (including the `insufficient_history` fallback sessions of 09-08..09-15);
the ported prior-session volume history reproduces `volume_history_dev.csv` on all 10 recomputed sessions.

**Historical 11:00 parity** (historical-parity profile, the 15 frozen dates, no tuning; reference = `All_151_Simulations_Entry_Cutoff_1100.xlsx`, Trades sheet):

| strategy | reference trades / wins / gross / fees / net | this implementation | trade by trade |
|---|---|---|---|
| NPT1-MD5 | 44 / 25 / 145,210.00 / 8,365.05 / 136,844.95 | 44 / 25 / 145,210.00 / 8,365.11 / 136,844.89 | 44 of 44 identical (times, strike, ask, bid, gross, MAE, MFE) |
| NPT1-MDX3 | 12 / 8 / 198,997.50 / 2,566.00 / 196,431.50 | 12 / 8 / 198,997.50 / 2,565.98 / 196,431.52 | 12 of 12 identical |
| NPT1-PE5 | 106 / 45 / 89,732.50 / 19,889.41 / 69,843.09 | 106 / 45 / 89,732.50 / 19,889.51 / 69,842.99 | 105 of 106 identical; 1 trade differs in MAE/MFE only |

* The fee differences (a few paise) are the reference's unrounded fees against this ledger's per-component paise rounding. P&L before fees is identical.
* The one excursion difference (2026-09-22 CE23250, entry 10:58:02): the research table has no row for the contract on four intermediate bars (it left the ATM±2 band and the 09:30 anchor basket), so the research recorded
  no marks there; this implementation marks the real bar-end bids (MAE −6,467.50 / MFE +390 instead of −4,615 / 0). Entry, exit and P&L are identical. This is a data-availability artifact of the evidence, not a rule.
* One real divergence was found by this comparison and fixed before it matched: the contract candidate set (§3); every other trade matched on the first complete run.

**Tests** (`dotnet test`, local PostgreSQL test database): 2,178 passed, 0 failed, 0 skipped. Build: 0 errors; 3 warnings that exist on master in files this work does not touch (2× xUnit2000 in
`NiftySignal.Tests/AdaptiveObserver`, 1× CS0219 in `tools/AdaptiveLivePerformance`). New: execution-policy tests, strategy tests against an independent reference model on tie-heavy random data, 11 end-to-end
and restart tests, Postgres schema/dashboard/reversal tests, configuration tests, broker-isolation additions.

**Performance** (synthetic full day, 223k source rows, local PostgreSQL, `Npt1PerformanceTests` and the unchanged adaptive-v1 `PaperLivePerformanceTests` for comparison):

| | NPT1 stream (M1 producer + 3 strategies) | adaptive-v1 paper bridge (existing) |
|---|---|---|
| commit ms p95 / p99 / max | 62.6 / 68.2 / 220.0 | 137.9 / 169.4 / 261.1 |
| per-group handoff ms p95 / p99 / max | 0.03 / 0.12 / 195.2 | 0.04 / 20.6 / 193.1 |
| journal growth per day | 214,478 rows, 110.9 MiB | 222,973 rows, 116.1 MiB |
| M1 producer ms per poll p50 / p95 / p99 / max | 2.3 / 5.1 / 8.4 / 31.7 | - |

Real-data replay of the busiest development day (2026-09-22, 2,376,347 rows): database read 93-95 s, M1 producer 10 s CPU for the whole day (about 0.1 ms per 250 ms poll), trading engine 2.6 s, peak working
set 282 MiB (producer only). The M1 stream reads the same rows as adaptive-v1 with its own reader: tick reads are 2x, nothing more; it never runs inside the Observer's poll loop. Bar completion latency in live
operation is bounded by the 2 s ordering lag plus the poll interval (same as adaptive-v1); it cannot be measured without live data.

Found by the Postgres measurement: a same-step reversal (exit and opposite entry on one bar) violated `ux_positions_open_slot`, rebuilt the run (a ~3 s stall) and could halt it; fixed in `PostgresRunStore.Commit` with a regression test.

## 7. Capital

Three simultaneous 650-unit positions at the top of the ₹100–₹150 band need at most 3 × 650 × 150 = **₹292,500** of committed premium (₹97,500 per strategy), plus the premium of an entry that is pending
on the same bar. `Trading:PaperLive:Capital` is the single global gate (open positions at entry price plus outstanding intents); this work does not choose it.

## 8. Fees

`NseIndexOptionsCostSchedules.From20260401` (the global schedule) uses an exchange rate of 0.03553%; the frozen NPT1 model uses 0.03503%. Everything else is identical (STT 0.15% sell, SEBI 0.000001, stamp 0.003% buy,
GST 18% of exchange+SEBI, brokerage 0). A separate versioned schedule `npt1-frozen-fees-v1` is used for the NPT1 run; the global schedule is unchanged. Costs are rounded to paise per component per fill
(the research sheet is unrounded: a sum difference of a few paise over dozens of trades).

## 9. Limits, interpretations and what is NOT verified

* Not deployed, not merged, schemas not applied anywhere, the VM untouched. Never run against a live feed: the Host worker (`Npt1M1Worker`) is covered by its building blocks (producer determinism, stream
  processor policy, EF frame store on PostgreSQL, durable run, restart) but its live polling loop has not run against a real market day.
* The `/paper-live` NPT1 section builds and its data service is tested against PostgreSQL; the page itself was not rendered in a browser here.
* Candidate set (§3) is derived from the frozen evidence, not stated in the specification. A fill bar whose frozen contract left the candidate set is NOT cancelled (the quote exists in a live feed); the
  evidence has no case of it. If the owner wants the stricter reading, it is a one-line rule in `ProcessPendingEntry`.
* Quote age = bar end minus the availability of the contract's latest TWO-SIDED quote (bid > 0, ask >= bid), kept across later one-sided updates.
* After a restart the strategies re-warm from live bars (14 / 20 / 11 consecutive bars); bars completed while nobody observed are not replayed as decisions (the specification forbids fabricated downtime trades).
* A contract whose lot size does not divide 650 stops the NPT1 run at configuration time (fail closed) instead of cancelling one entry.
* The 15:00 protocol closes at the held token's latest positive bid even if it is old (the age is recorded). A position that never had a two-sided quote cannot exist (entry needs one), so the "no bid" branch is a defensive path.
* Sealed data: no date after 2026-10-01 was opened; the parity tool refuses such dates.
