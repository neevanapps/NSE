# Top-5 Microstructure (OFI5, MicroDev5Proxy, Depth L5) + Greek Delta display revert — DESIGN ONLY (revision 2)

Status: **design for owner approval; nothing implemented.** Baseline `b8485ff5e3739dea53b257e838dbc39fe9e1f27b` (worktree `D:\Projects\NSE-top5-design`, branch `design/top5-microstructure`, uncommitted).
Evidence: direct source reading (file:line at that SHA) + Phase 0 read-only counts from the local sealed copy `niftysignal_vm_copy` (section 5). No VM, no production data, no secrets, no writes.
Revision 2 incorporates the owner review: Delta display revert plan (section 1), corrected placement (2), MicroDev5 formula comparison (3), accepted architecture (4), Phase 0 measurements (5).

---
## 0. Headline changes since revision 1

1. **Phase 0 overturned the "validity = reject non-monotone books" idea.** Rows in `ticks` come from two interleaved update streams: *touchline-originated* rows change only L1 and *depth-originated* rows change only L2-5 (never both in the same row: 0 of ~66k transitions for most tokens, <=8 per token-day otherwise). L1-only rows carry **stale** deeper levels (11-39 % of option rows violate L1-vs-L2 monotonicity, always for exactly one row, always followed by a consistent depth row). **A Top-5 observation must therefore be built only from depth-originated rows**, not from every tick and not from L1-only rows (section 6).
2. **Both feeds are ~1.6 s sampled streams** (depth-changing rows: p50 gap 1.61 s, p99 1.82 s; L1-changing rows per token-day ~ the same count, ~13.7 k). OFI5 is therefore an OFI over ~1.6 s snapshots, like the existing L1 OFI, not an event-level OFI.
3. **The current L1 normalizer drops ~13.7 k depth-only rows per token-day** (20-44 % of option rows): confirmed quantitatively.
4. **A hard 5 s depth freshness limit is supported by the data** for options (max observed gap 2.6-4.8 s on every sampled option token-day, 0 gaps > 5 s) and only bites on futures (32-52 gaps > 5 s per day, 5-7 > 30 s, max ~320 s). Recommendation reversed from rev. 1: adopt the 5 s limit for Top-5 depth (section 8).
5. **Performance assumptions corrected** (section 13): ~41 sidecar rows per bar (not "2-4 inserts"), ~560 k depth states per session (same as L1, not a larger factor), recovery read +53 % local read time for the extra columns.

Still true from rev. 1: depth never reaches the observer today; the L1 normalizer drops deeper-only changes; **never add columns to `adaptive_option_strike_bars`** (the attestation digest reflects over all properties); L1 trackers have no stale limit.

---
## 1. Greek Delta Direction display revert (PLAN ONLY — no Dashboard edit yet)

**Directive:** remove the display inversion **only** for the options Greek Delta Direction. Displayed Delta Direction = raw `CE ΔDelta − PE ΔDelta` (change-based rows use the existing CE/PE Delta *changes*; no levels substituted), with the existing ×100 scaling in Market Summary. IV, Vega, MicroDev directions stay inverted; Strict/Enriched trade-flow deltas unchanged; OI Direction stays PE − CE; stored values, calculators, agent feed and PaperEngineV2 unchanged.
(Note: CLAUDE.md "no Dashboard changes before an edge is found" is superseded here by the explicit owner request; implementation still waits for approval of this plan.)

### 1.1 Whole-repo search (all `*.cs, *.razor, *.md, *.js, *.json`, incl. `tools/`, excluding `obj/bin`)
Delta-direction inversion exists in exactly these production paths; there are **no** others (no JS/CSS, no screenshot-specific formatter, no Telegram formatter, no agent-feed inversion — `AgentFeedModels.cs:7` and `docs/AGENT_FEED.md:25` state raw values, and `DirectionDisplay` is asserted by `DashboardSimplificationTests.cs:233-241` to live only in the Dashboard). Screenshots/pinned captures render the same Razor components, so they follow automatically.

| # | File:line | Today | Planned change |
|---|---|---|---|
| P1 | `Services/DirectionDisplay.cs:13` | `DeltaLabel = "Delta Dir (Inv)"` | `DeltaLabel = "Delta Dir"`; keep `IvLabel`/`VegaLabel` |
| P2 | `Services/DirectionDisplay.cs:7-9` (summary) and `:15` `Legend` | "IV / Delta / Vega ... INVERTED" | rewrite: IV / Vega inverted, Delta raw; keep `Invert`/`InvertDirectionOnly` (`:18,:21`) untouched (IV, Vega use them) |
| P3 | `Components/Dashboard/MarketSummarySection.razor:124` | `@Dir(DirectionDisplay.Invert(d.Delta), 2, scale100: true)` | `@Dir(d.Delta, 2, scale100: true)` (shared `TotalGrid` fragment => ATM, ITM-shifted, OTM-shifted all change at once) |
| P4 | `MarketSummarySection.razor:100` | Delta header `title="displayed inverted: displayed = −raw (CE − PE); ×100 display scaling"` | `title="CE − PE (raw); ×100 display scaling"`; IV/Vega titles unchanged |
| P5 | `Components/Dashboard/AdaptiveObserverPanel.razor:377` (bar rows), `:389` (AVG, `ctxAvg`), `:401` (DAY AVG, `dx.Context`) | `DirectionDisplay.InvertDirectionOnly(<x>.Delta)` | `<x>.Delta` raw; CE/PE Delta cells unchanged (never inverted) |
| P6 | `AdaptiveObserverPanel.razor:363` | Delta header tooltip "displayed inverted..." | "CE − PE (raw)" |
| P7 | `AdaptiveObserverPanel.razor:355` | subtitle appends `@DirectionDisplay.Legend` | follows P2 automatically |
| P8 | docs: `docs/V2_OBSERVABILITY_HANDOFF.md:41,108,126,127`, `docs/MARKET_SUMMARY_METRICS.md:93,88+` (section 7 presentation note) | describe IV/Delta/Vega inversion and "(Inv)" headers | amend: Delta now raw; historical handoff text annotated, not rewritten |

Unchanged on purpose: `MarketSummarySection.razor:123,125` (IV, Vega), `AdaptiveObserverPanel.razor:376,378,388,390,400,402`, all `MicroDevDisplay` sites (`:135,157,178,324,336,348`), `docs/AGENT_FEED.md`.

### 1.2 Existing tests that encode the old convention (must be updated, not deleted)
- `Tests/MarketSummary/DashboardSimplificationTests.cs:67` header list (`"Delta Dir (Inv)"` -> `"Delta Dir"`); `:181-188` `TotalSummary_DeltaAndVegaAreScaledOnce_NotTwice` (`StartsWith("-2.00 (")` for Delta -> `"2.00 ("`; Vega stays `"-30.00 ("`); `:225-230` label assertion (DeltaLabel); `:251-256` tint test (Delta column index 10 moves from the inverted group to the same sign group as Price/Strict: positive set `{2,3,4,5,8,10,12}` for mode 1).
- `Tests/MarketSummary/MarketSummarySectionRenderTests.cs:118` (`"Delta Dir (Inv)"`), `:124-129` (comment and loose `Contains("2.00 (")`, which currently cannot distinguish `2.00` from `-2.00`; make it a strict cell assertion).
- `Tests/Dashboard/AdaptiveCompactGridTests.cs:630` header list; `:713-725` (bar 12 Delta Dir `"-0.0050 (-1.00)"` -> `"0.0050 (1.00)"`, AVG cell the same; confirm exact sign formatting of the existing `RatioFormat` before pinning); `:759-785` DAY AVG test (add Delta Dir assertion; today it asserts Greeks unavailable for DAY AVG fixtures, keep that and add a v3-seeded variant).
- Unchanged and still valid: `Tests/Dashboard/AdaptiveRatioGridTests.cs:324-351` and `Tests/AdaptiveObserver/AdaptiveMigrationPostgresTests.cs:373-377` (raw calculators), `AgentFeedTests`.

### 1.3 New regression tests (prove ONLY Greek Delta direction display changes)
1. `DirectionDisplay_Labels`: `DeltaLabel == "Delta Dir"`, `IvLabel`/`VegaLabel` still `(Inv)`, `Legend` mentions IV and Vega but not Delta as inverted.
2. Market Summary, **parameterized over `summary-total-itm`, `summary-total`, `summary-total-otm`** and the five windows: Delta Dir cell == formatted `raw x100` for raw **positive, negative, zero** (renders `0.00`, never `-0.00`), **missing** (`—`); tint class/`data-sign` follows the raw sign.
3. Same fixture, same render: IV Dir == `-raw`, Vega Dir == `-raw x100`, MicroDev Dir inverted, OI Dir == PE − CE, Strict/Enriched Dir == CE − PE (proves nothing else moved); header text order unchanged.
4. Fixed-strike OPTION CONTEXT: bar rows, **AVG**, **DAY AVG** each: Delta Dir == raw `CE ΔDelta − PE ΔDelta` (positive/negative/zero/missing/mixed), CE and PE Delta cells unchanged, IV/Vega Dir still inverted; bar without Greeks (v2) shows six `—`.
5. Source guard: no `Invert(`/`InvertDirectionOnly(` call whose argument ends in `.Delta` in `NiftySignal.Dashboard/**/*.razor|cs` (Iv/Vega/MicroDev calls allowed); existing `DirectionDisplay_LivesOnlyInTheDashboard_...` stays.
6. Non-display invariants: calculator Delta direction (`OptionSummaryCalculator` `Dir(ce.Delta, pe.Delta, 1d, 2d)`, `OptionSummary.cs:231`), agent-feed Delta direction JSON, V2 frame metrics: byte-identical before/after (golden pinned from the unmodified baseline).

---
## 2. Corrected Dashboard placement (owner-approved)

| Metric | Futures grid / Summary | Fixed-strike Options grid | TOTAL options summary (ATM, ITM-shifted, OTM-shifted) |
|---|---|---|---|
| **OFI5** | yes (Futures grid) | yes | **no** |
| **MicroDev5Proxy** | yes (Futures grid) | yes | **no** |
| **Notional Depth Imbalance L5** | yes (Futures Summary) | **no** | **yes**, CE−PE direction in all three grids |

Consequences: (a) no OFI5/MicroDev5 aggregator is added to `OptionSummaryCalculator`/`Direction`; (b) fixed-strike views get OFI5 and MicroDev5 columns only (bar rows, AVG, DAY AVG, following the existing conventions: ratio-of-sums, no zero-fill); (c) the TOTAL grids need per-contract notional depth, so the per-strike sidecar still **stores** depth-imbalance inputs for every contract, but the fixed-strike view never renders them. Existing labels, coverage rules, windows (5/10/50/100/Day), completed-bars-only and MicroDev "(Inv)" conventions are untouched; new coverage families `Depth5` (TOTAL grids, `TotalFamilies` `MarketSummarySection.razor:168`) only.

---
## 3. MicroDev5Proxy — formula decision

### 3.1 Candidates
Notation: `Qb = Σ bid qty L1-5`, `Qa = Σ ask qty L1-5`, `Tob5 = (Qb−Qa)/(Qb+Qa)`, `HS1 = (Ask1−Bid1)/2`, `Vb/Va` = quantity-weighted average price of the five bid/ask levels.
- **A** = `HS1 × Tob5` (best bid/ask half-spread × aggregate Top-5 quantity imbalance; the imbalance is "weighted" by quantity itself — every share counts equally. A rank-decay weighting such as 1/level would be an invented constant and is rejected under the CLAUDE.md no-invented-constants rule).
- **B** = `(Va−Vb)/2 × Tob5` (depth-VWAP width x the same imbalance; rev. 1 formula; equals `Micro5 − Mid5`).
- Reference: existing L1 `MicroDev = HS1 × TOB1` (`FuturesMicrostructure.cs:19-25`).

### 3.2 Worked examples (synthetic books, pure arithmetic; INR points; computed with a throwaway script, not data)
| Case | L1 MicroDev | Tob5 | A | B | B/A | Reading |
|---|---|---|---|---|---|---|
| O3 wide touch (spread 1.0, tight depth, asks 3x bids) | −0.2500 | −0.500 | −0.2500 | −0.3500 | 1.4 | similar magnitude |
| O4 tight touch (spread 0.1), bids spaced 1.0 apart, asks 0.1 apart, qty 120 vs 100 | +0.0045 | +0.091 | +0.0045 | +0.1045 | **23** | B dominated by deep price spread, not by the touch; same sign |
| O2 L1 bid-heavy (500 vs 100) but deep asks heavy (400 each) | **+0.0167** | −0.491 | −0.0123 | −0.0454 | 3.7 | **sign differs from L1 MicroDev** for both A and B (aggregate Top-5 disagrees with L1) |
| O6 L1 1000 vs 100, deep asks 400 each | +0.0205 | −0.223 | −0.0056 | −0.0197 | 3.5 | A with aggregate weights negative; a rank-decay-weighted variant would be **+0.0063** (sign flips with the weighting choice — another reason not to invent weights) |

Signs of A and B never differ from each other for the same book (same `Tob5`, positive widths); they differ in **magnitude** (B/A 1.4 to 23, driven by how far apart deep levels are). Signs can differ from the L1 MicroDev (O2, O6) — that is the information deeper levels add.

### 3.3 Recommendation (needs owner approval — decision D8; not a contract until approved)
**Adopt A, equal quantity weighting: `MicroDev5Proxy = HS1 × (Qb − Qa)/(Qb + Qa)`.**
Reasons: (1) it reduces **exactly** to the existing MicroDev when only level 1 is used, so it is the same quantity extended in depth and shares units/scale with the stored L1 MicroDev; (2) magnitude is anchored to the quoted spread (what the proxy is meant to scale by), whereas B is dominated by deep-level price dispersion (23x in O4) which is a different concept (book slope) and noisy for sparse option books; (3) no invented constants.
Relationship to notional Depth L5: `DI5 = (BidN−AskN)/(BidN+AskN)` uses the same five levels but weights each level by price x qty; for futures the five prices differ by tenths of a point so `DI5 ≈ Tob5` and MicroDev5 ≈ `HS1 × DI5`; for options premium differences across levels are larger. **Honest limitation: MicroDev5Proxy is Tob5 rescaled by the touch half-spread and is largely redundant information next to Depth L5/Tob5**; it is retained because the owner placed it on the futures and fixed-strike grids as the MicroDev continuation. Ratio for direction/aggregation = `Tob5 x 100` time-weighted (as `MicroDir` does for L1, `OptionSummary.cs:241-246`).
Neither formula is claimed to predict price direction; sign is a hypothesis (display follows the D5 convention below). Storage is raw; the `(Inv)` display, if approved, is applied once at render exactly like `MicroDevDisplay`.

---
## 4. Provisionally accepted architecture (owner) — safeguards recorded
Independent Top-5 observation channel; Level-1 clean-tick stream and dedup untouched; separate versioned supplemental tables; existing strike attestation digests unchanged (pinned by a test); no PaperEngineV2/strategy change; notional normalized Depth L5; price-aligned OFI5 subject to validation (section 7.1); identical causal ordering live/replay; no zero-fill for unavailable depth; freshness/coverage reporting; no silent change to any existing metric version. Safeguard added by Phase 0: the channel emits observations **only from depth-originated rows** (section 6).

---
## 5. Phase 0 measurements (local sealed copy, read-only, counts only)

Method: role `research_ro`, `default_transaction_read_only=on`, `statement_timeout`, per-token `(Token, ExchangeTimestamp)` bounded window (09:00-16:00 IST of one session) with `LAG` partitions; no prices/OFI/MicroDev values were read or recorded. SQL in Appendix A.
Scope: full day 2026-10-29 (NIFTY future `61471` + all 40 nearest-weekly option tokens); spot checks 2026-10-14 (4 tokens) and 2026-09-17 (4 tokens); day inventory for all dates.

| Check | Result |
|---|---|
| Historical availability | **32 sessions** with ticks locally, 2026-09-04 .. 2026-10-30; depth columns populated on all 32. **No ticks after 2026-10-30** in the copy although `instruments` runs to 2026-11-23 => the newest 17+ sessions cannot be replayed locally. VM retention state unknown (blocker B1). |
| Rows | 10-29: 2,332,466 option rows (40 tokens, 30.9k-70.3k each) + 46,106 futures = **2.38 M**; heap tuple ~248 B/row (sampled), table 71 GB / 148.8 M rows. |
| Depth completeness (option strikes) | `Depth` null: 1-6 rows per token-day (<= 0.02 %). Among non-null rows: **100 % have 5 non-empty levels both sides**; padded/empty levels 0; price>0 with qty=0: 0; one-side empty: 0; gaps (empty before non-empty): 0; crossed/locked: 0. Far strikes of the frozen universe included. |
| Depth completeness (futures) | null 1; **840 rows (1.8 %)** with ask5 padded (price 0); gaps 0. |
| Depth-originated vs L1-originated rows | options: depth-changing rows 13,652-13,825 per token-day, L1-only rows 13.6-13.8 k, **both-in-one-row 0** (spot days: <= 8). Futures: depth 13.6-13.7 k, L1-only 10.9-11.9 k. |
| Deeper-only rows dropped by the current L1 normalizer | options 13.65-13.83 k per token-day (20-44 % of raw rows); futures 13.7 k (on 10-29: 9,451 of 46,106 satisfy the strict SamePayload match incl. timestamp/last/volume/OI). |
| Update cadence of depth | p50 **1.61 s**, p90 1.80 s, p99 1.81-1.87 s (options), max gap 2.6-4.8 s, **gaps > 5 s: 0** on every option token-day; futures p99 up to 3.2 s, **32-52 gaps > 5 s/day, 5-7 > 30 s, max ~312-324 s**. |
| Non-monotone books | Rows where L1 conflicts with L2 (bid2 >= bid1 or ask2 <= ask1): options 11.4 k of 65.8 k (17 %) and 11.9 k of 30.9 k (39 %) for two tokens; futures 5.3 k of 46.1 k (11.5 %). In 100 % of these the row changed **only L1**, the stale deeper levels did not change, and the **next row was consistent** (bad-after-bad = 0, recovered = bad count). Inside depth-originated rows: **0 violations for options**, 38-43 per day for futures (all crossed L1). |
| Crossed L1 (futures) | 863 rows on 10-29 (1.9 %): 823 in L1-only rows, 40 in depth rows. Options: 0. |
| Memory cost of extra columns | In-process benchmark not run (needs the new reader; blocker B3). Estimate from measured rows: 2.38 M rows; projecting 16 more fields inline (+~128 B) would roughly double the ~112 B `ObserverRawTick` list that already drives a ~1.9 GB recovery peak => **do not widen `ObserverRawTick`**; use the streaming depth channel (section 13): ~560 k retained depth states x ~176 B = **~100 MB**, less with pruning. |
| Recovery read cost | Local server-side scan of the 41 tokens for one day: L1 columns **13.5 s**; all depth columns **20.7 s (+53 %)** (second run, partially cached; local disk, not the VM). Existing recovery today 8-15 s end to end (`docs/ADAPTIVE_LIVE_PERFORMANCE_2026-10-08.md`). Projected: a second streaming depth pass adds roughly the extra-column read time (~+5-10 s locally); **VM figure unknown (B2)**. |

Interpretation: the feed is a pair of ~1.6 s snapshot streams; touchline updates L1, depth updates L2-5. This is inferred from stored rows, **not** from raw WebSocket messages (none are retained) — see blocker B4.

### Remaining Phase 0 blockers (each with the exact read-only check)
- **B1 VM retention / newest sessions:** cannot touch the VM. Needed: owner-run read-only `SELECT min("ReceivedAt"), max("ReceivedAt")` and per-day counts on the VM `ticks`, plus whether `purge-vm-raw-ticks.ps1` has run (branch `ops/vm-tick-retention`, not on master). Local copy lacks 2026-11-02..11-23.
- **B2 VM recovery/memory budget:** needs VM RAM and Host peak working set (`Get-Process` / existing recovery log lines).
- **B3 In-process memory/time benchmark:** needs the proposed streaming depth reader (code) run against one local session; no DB writes. Cannot be done before approval of implementation.
- **B4 Raw message semantics:** confirm `tf` carries only `bp1/sp1/bq1/sq1` and `df` only L2-5 (or that `df` L1 equals the touchline L1) by logging a few seconds of raw WebSocket JSON counts (field-presence only) from a dev run, or from FlatTrade documentation.
- **B5 Wider sampling:** only 1 full day + 2 partial days sampled; repeat the section-5 SQL on 4-5 more dates (Appendix A, parameterised) before final contract sign-off, including a high-volatility day and a very illiquid expiry day.

---
## 6. Input path and the depth channel (revised by Phase 0)

Existing facts (rev. 1, unchanged): reader projects L1 only (`AdaptiveSourceTickReader.cs:105-113`, `ToRaw :115-129`); `SamePayload` compares L1 + timestamp/last/volume/OI (`AdaptiveTickCleaner.cs:62-70`) and the normalizer returns null for such rows (`AdaptiveIncrementalTickNormalizer.cs:14-19`); live poll `AdaptiveObserverWorker.PollLiveAsync` and recovery `AdaptiveStateRecoveryService.RecoverAsync :45-153` read by Id then order `(AvailableAt, Id)` with `StableOrderingLag` 2 s (`AdaptiveObserverWorker.cs:32,273`); engine ordering rules `EnsureOrder` (`AdaptiveObserverEngine.cs:452-467`).

**Channel design (revised):**
- New streaming reader `ReadDepthAfterId/ReadDepthSession` projecting `(Token, Id, ExchangeTimestamp, ReceivedAt, L1-5 prices+qty)` **separately** from the existing L1 projection; the L1 path, `ObserverRawTick`, `SamePayload` and the clean-tick list stay **byte-identical** (guarded by a golden test). Streaming (Id order, per token) so recovery never materializes the whole depth set.
- New per-token `AdaptiveDepthNormalizer`: emits a `DepthObservation` **only when any of L2-5 (16 fields) changed vs the previous row of that token** (the depth-originated rows) and always for the first complete row; L1 inside the observation is the row's L1 (equal to the latest touchline value at that time). L1-only rows are **not** observations (their deeper levels are stale). `AvailableAt = max(ReceivedAt, ExchangeTimestamp)` as today.
- Engine hook `ObserveDepth(token, observation)` invoked inside `ProcessAvailabilityGroup` before the core items of the same group, bypassing `_lastAvailableAt/_lastId`, `_optionFlow`, `_latestOptionQuotes`, `_optionBooks` (same pattern as the spot bypass). Depth-only groups must not move the core runtime cursors (`LastProcessedSourceTickId`).
- Observation validity (checked at the observation, not per tick): `Depth` non-null; L1 valid (`bid1>0, ask1>0, ask1>=bid1`); non-empty levels contiguous from level 1 (padded price-0 levels allowed only as a tail: futures 1.8 %); bid prices strictly decreasing, ask strictly increasing; qty >= 0. Phase 0: options 100 % pass except null rows; futures ~0.1 % crossed => **invalid**, not bridged, not zero-filled, OFI baseline reset (as `FuturesMicrostructure.cs:121-125`).
- Because observations are depth-originated only, no "skip transient L1-only rows" rule is needed; the earlier fear (frequent invalidation) is removed. The fallback "Option A" of rev. 1 (observe depth on L1-surviving ticks) is **rejected**: it would sample the stale-deep L1-only rows.
- Trading.Adapters (`AdaptiveEventTranslator`, `SourceModels.ToRaw`) and PaperEngineV2 are not touched.

## 7. Final formulas and sign conventions

### 7.1 OFI5 (price-aligned; subject to validation)
Notation: snapshot = bids/asks `(p,q)` levels 1..5; empty level = `p<=0` or `q<=0`; consecutive valid observations `P` -> `N` of one token.
- Bid floor `L = max(lowB(P), lowB(N))`, `lowB(S)` = price of bid level 5 if five non-empty levels else 0; ask ceiling `U = min(highA(P), highA(N))`, `highA(S)` = price of ask level 5 if five non-empty levels else +inf. `qB_S(p)/qA_S(p)` = quantity at price `p` (0 if unlisted).
- `e5 = Σ_{p>=L}(qB_N(p) − qB_P(p)) − Σ_{p<=U}(qA_N(p) − qA_P(p))`.
- Bar: `Ofi5 = Σ e5` over valid transitions with `next.At ∈ (start,end]` (reference = last observation `At <= start`); `GrossOfi5 = Σ|e5|`; `Ofi5Transitions`; null (not 0) when no valid transition. Window ratio = `Σ net / Σ gross` (existing convention).
- Relation to L1 (`Contribution`, `FuturesMicrostructure.cs:196-205`): restricting the window to level 1 reproduces it exactly (validated by a 10^5-pair property test); rank-based multi-level OFI is rejected (it books a shifted level as new flow). Level shifts: compared at the same price; prices entering the window at level 5 are unobservable in `P` and ignored.
- **Cadence caveat (Phase 0):** transitions are between ~1.6 s depth snapshots; intra-interval churn is invisible, as for the L1 OFI built on L1 rows. Not comparable to event-level OFI literature magnitudes.
- Sign: positive = net bid adds / ask removals (buy pressure), stored raw; displayed non-inverted (consistent with the existing OFI columns). Additivity: additive across bars as stored nets (baseline carries across the bar boundary), not across a re-baseline; summing across contracts mixes unit quantities (not done for OFI5 in TOTAL — placement section 2).
- `Ofi5` **contains** L1 flow; never add it to `Ofi`.

### 7.2 MicroDev5Proxy
`HS1 × Tob5` (section 3, recommended A; pending D8). Raw positive = bid-heavy; usable iff `Qb+Qa>0`; time-weighted by usable seconds; ratio = `Tob5 x 100`. Display convention pending D5 (recommended: inverted once at render, label `MicroDev5 (Inv)`, like `MicroDevDisplay`).

### 7.3 Notional Depth Imbalance L5
Per valid observation `BidN = Σ p_i q_i` (bids), `AskN` likewise, `DI5 = (BidN−AskN)/(BidN+AskN)` in [−1,1], usable iff `BidN+AskN>0`. Prices INR points, quantities contract units: lot size cancels; option notional is premium x qty (ITM dominates pooled sums).
Time weighting: observation effective from its `At` until the next observation, clipped to the bar `(start,end]`, **and to at most 5 s** (section 8); stored per bar: `DepthImb5Tw`, `BidNotionalTw`, `AskNotionalTw`, `Depth5UsableSeconds`, `Depth5InvalidSeconds`, `Depth5StaleSeconds` (valid state older than 5 s: excluded), `Depth5StateChanges`.
- Futures Summary: `Metric(Raw = mean DI5, Num = Σ DepthImb5Tw·w, Den = Σ w)`, `w = Depth5UsableSeconds`; ratio = time-weighted mean x 100 (TOB convention); sign raw, not inverted.
- TOTAL grids (ATM, ITM-shifted, OTM-shifted; same basket construction as every TOTAL grid): per side pooled over included contracts and bars `DI_side = Σ(BidNotionalTw−AskNotionalTw)·w / Σ(BidNotionalTw+AskNotionalTw)·w`; **Direction = DI_CE − DI_PE** (display ratio `(DI_CE−DI_PE)/2 x 100`, mirroring `MicroDir`); non-inverted. Alternative per-contract equal weighting stays a display-time decision (D4).
- Not shown in fixed-strike views (section 2).

### 7.4 Item 5 — Price-Flow Agreement and Fallback Share
Unchanged from rev. 1 (section 11 of the old text): persisted only as 10-bar rolling values on `adaptive_rolling_states` (`PriceStrictDeltaAgree/PriceEnrichedDeltaAgree` `AdaptiveObserverRows.cs:137-138`, `FallbackShare :125`, computed `AdaptiveRollingStateTracker.cs:125,141-144`), read by nothing in the Dashboard. Recommendation: derive per window from rows already loaded (sign of Price Δ vs Strict/Enriched Δ; `Σ(StrictUnknown−EnrichedUnknown)/ΣVolume` from `adaptive_future_bars`); display-only; no new persistence. Placement not specified by the owner: **decision D6 stays open** and nothing is implemented for it until placed.

### 7.5 Existing conventions preserved
Level-1 OFI/MicroDev code, `futures-micro-v3`, `option-strike-v3`, `adaptive-fixed13650-v1`, `close-atm-v1`, `options-supp-v2` versions untouched; windows 5/10/50/100/Day; completed bars only; coverage rules (flow metrics need a row for every bar of the window); OI Direction PE − CE; Strict/Enriched unchanged.

---
## 8. Freshness rule (decision D3, now evidence-based)
Depth observations are ~1.6 s apart for options (max 2.6-4.8 s sampled) and futures have long quiet gaps. **Recommended: hard 5 s freshness limit for Top-5 depth** (user-specified): seconds beyond 5 s after an observation's `At` are `Depth5StaleSeconds` and excluded from usable seconds and from OFI continuity (an observation arriving after a >5 s gap re-baselines OFI5: no transition across a stale gap). This deliberately differs from L1 TOB/MicroDev (unlimited carry-forward, `FuturesMicrostructure.cs:92-130`) and is labelled in the coverage legend. Versioned as `*-depth5-v1`; a different limit is a new version.

## 9. Persistence, migration, attestation (revised for placement)
Additive only; no change to any existing table, column, version string or attestation.
1. `adaptive_futures_depth5_bars`, unique `(SessionId, BarSeq, MetricsVersion)`, version `futures-depth5-v1`: `Ofi5`, `GrossOfi5`, `Ofi5Transitions`, `Tob5Tw`, `MicroDev5Tw`, `DepthImb5Tw`, `BidNotionalTw`, `AskNotionalTw`, `Depth5UsableSeconds`, `Depth5InvalidSeconds`, `Depth5StaleSeconds`, `Depth5StateChanges`, `Depth5InvalidEvents`.
2. `adaptive_option_strike_depth5_bars`, unique `(SessionId, BarSeq, Token, MetricsVersion)` + index `(SessionId, Expiry, Strike, Side, BarSeq)`, version `option-depth5-v1`: the same measures (needed: OFI5/MicroDev5 for fixed-strike, notional depth for TOTAL grids) + `Expiry, Strike, Side, Token`.
3. `adaptive_depth5_attestation`, unique `(SessionId, BarSeq, MetricsVersion)`, `Digests` (`token=hash;...`), `TokenCount`, `VerifiedAtUtc`: a **generic** copy of the A4 mechanism whose digest reflects over the depth5 row type. `StrikeAttestation.Digest` (`StrikeAttestation.cs:11-30`) and `adaptive_strike_attestation` are **not modified**; a unit test pins the digest of a fixed existing row to a constant so any accidental property addition fails. Futures depth5 rows follow the trust model of `adaptive_futures_supplemental_bars` (one row per bar, insert-if-missing/verify-if-present within 1e-9).
Writers: `PersistOrVerify*Async` contract of `AdaptiveSupplementalPersistence.cs` (insert-if-missing, verify-if-present, mismatch marked in `adaptive_projection_health` with new component names, never overwritten); depth5 outcomes never alter the paper verification result (`:616-632` carve-out). Option rows + attestation commit in one `SaveChanges`.
Migration: `Migrations/<ts>_AddTop5MicrostructureSidecars` + Designer + snapshot; `Sql/adaptive-top5-microstructure.idempotent.sql` in the style of `adaptive-bar-close-atm.idempotent.sql`; Host applies at start (`Program.cs:243`); every Dashboard loader wraps `UndefinedTable` (`AdaptiveObserverDataService.cs:273,455` pattern) so absent tables/rows = coverage gap.

## 10. Market Summary aggregation and coverage
Windows 5/10/50/100/Day, completed bars, same pinned end bar; futures missing bar => PARTIAL; option flow metrics need a row for **every** bar of the window per contract (`OptionSummary.cs:116-146`). New calculators and records are separate (`Depth5Summary`, `FuturesDepthBar`, `ContractDepthBar`), not extra parameters on `FuturesBar`/`ContractBar` (constructed at ~30 sites). Windows crossing the deploy boundary are PARTIAL until replay backfills (needs raw ticks, B1). Baskets (ATM/ITM-shifted `−1`/OTM-shifted `+1` around the pinned bar's close ATM; `Basket.cs:44-80`, `AdaptiveObserverDataService.cs:302-312`) are view-time constructs applied to all bars of a window — fine for display, **not** a per-bar causal series (needed if ever backtested).

## 11. Dashboard rendering
Futures table (`MarketSummarySection.razor` ~l.40-60): append `Depth5 Imb`, `OFI5`, `MicroDev5 (Inv)` and update the insufficient-coverage placeholder loop (`@for i<8`). TOTAL fragment (`:~92-135`): append **only** `Depth5 Dir`, update placeholder loop (11). Fixed-strike (`FixedStrikeHistory.cs`, `AdaptiveObserverPanel.razor`): append `OFI5`, `MicroDev5` for bar rows, AVG, DAY AVG. Loader: one extra scoped query per table inside the `UndefinedTable` catch; attested-only for strike depth rows. Agent feed: additive fields only. `PaperEngineV2.Core/Metrics/MetricRegistry.cs` untouched. All new signs are hypotheses (legend states so).

## 12. Live / restart deterministic parity
Same engine replays the whole session (`AdaptiveStateRecoveryService.cs:100-153`). Requirements: depth channel derived from the same rows by one shared helper in live and recovery; dedup key = the 16 deeper fields; arithmetic in fixed level/time order, no parallelism, no wall clock, tokens iterated sorted; pruning of depth states is a pure function of completed bars. Compared exactly: all depth5 measures (1e-9, integers exact), seconds counters, attestation `Digests`. Volatile/excluded: `Id`, `VerifiedAtUtc`, `DetectedAtUtc`, runtime heartbeat/cursors, bar-end instant (1 ms tolerance). Hazards: depth-only groups must not move core cursors; mid-session deploy => replay inserts missing rows; flag toggling => backfill by replay only. Tools/tests: `tools/AdaptiveDashboardValidation/RelationalRestartParity.cs`, the 30-minute scratch replay approach (separate milestone), fixture must include depth-only changes, level shifts, padded levels, crossed observation, a >5 s gap, and null depth rows.

## 13. Performance, memory and deployment (corrected)
- Rates (`docs/ADAPTIVE_LIVE_PERFORMANCE_2026-10-08.md`): mean 51 clean ticks/s, max 1,217/s; Phase 0: 2.38 M raw rows/session. Depth observations: ~13.7 k per token-day x 41 ≈ **560 k per session** (the same count as the existing L1 option-book states, "540-560 k, ~30 MB"; rev. 1's "larger factor" assumption is withdrawn).
- Per-observation cost: one 16-field compare + ~176 B append (vs 0.0046 ms/tick fitted): negligible. Per bar: `Complete` for ~41 trackers.
- **Sidecar writes per bar (correction):** options sidecar = **one row per universe contract (40 for the nearest weekly in the sampled day) + 1 attestation row + 1 futures row = ~42 rows**, in 2 transactions (options+attestation atomic; futures separate), EF batches rows into a few commands: ~2-4 SQL commands/bar for writes, plus on replay one existence read per sidecar per bar (`PersistOrVerify*`): ~2 extra reads/bar. At ~200 bars/day that is ~8.4 k rows/day (~2 MB), not "2-4 inserts per bar".
- Memory: keep `ObserverRawTick` unchanged; stream depth in Id order into per-token dedup; retain compact observations (~100 MB unpruned for a full session; prune each tracker to the last observation `<= bar end` after `Complete`, deterministic). Do not materialize 2.38 M rows x 20 depth fields (would add roughly +300 MB for 2.38 M x 128 B inline plus copies in raw/clean/sorted lists, on top of today's ~1.9 GB working-set peak).
- Recovery: extra streaming pass; local read +53 % for the added columns (13.5 s -> 20.7 s); VM unknown (B2); target: recovery wall-time increase <= +10 s on the VM, otherwise chunk/optimise before enabling.
- Deploy gates: (G1) migration applied (Host `Migrate()` or idempotent SQL), schema verified; (G2) Host deployed with `Adaptive:Top5Microstructure:Enabled=false`, no behaviour change verified (core golden, V2 untouched); (G3) enable flag on one restart, replay backfills today, depth5 rows verified equal to a second restart replay; (G4) Dashboard deployed after Host; absent tables/rows tolerated.
- Rollback: old Host/Dashboard run unchanged on the new schema (unreferenced tables; `Migrate()` with applied-but-unknown migration to be **proven** in `AdaptiveMigrationPostgresTests`); Host flag off (default) with no schema revert; Dashboard flag hides columns; separate attestation table ensures rollback never invalidates existing strike attestations. The Delta display change (section 1) is Dashboard-only and independently revertible.

## 14. Implementation sequence (after approval)
S0 Phase 0 completion (B1-B5, owner-run/data checks). S1 **Delta display revert** (section 1; Dashboard + tests; independent of Top-5, smallest, can ship alone). S2 Domain/AdaptiveObserver pure code: `Depth5Book`, validity, OFI5/Tob5/MicroDev5/DI5 trackers + property tests (OFI5 clamped to L1 == `Contribution`). S3 Streaming depth reader + normalizer + engine hook + `AdaptiveCompletedBarPackage` member; golden proof core output unchanged. S4 Persistence: entities, migration, SQL, writers, generic attestation, health markers; PG tests. S5 Recovery/parity: replay, fixtures, parity tool. S6 MarketSummary calculators (Depth5 for futures and TOTAL; OFI5/MicroDev5 futures + fixed-strike). S7 Dashboard rendering, legends, loaders, agent-feed additive fields. S8 Performance harness (B3), then VM shadow soak with flag on for several sessions. Each step ends at a review gate; no step touches strategies/V2.

## 15. Tests (extend existing files)
- Ingestion: `Tests/Ingestion/FlatTradeFeedStateTests.cs` (touchline/depth interleaving, junk price nulling, qty default 0).
- Pure metrics: `AdaptiveObserver/FuturesMicrostructureTests.cs`, `AdaptiveBookMetricsTests.cs`, new `Depth5MetricsTests` (OFI5 == L1 when clamped, level shift, window exhaustion, padded tail, crossed, 5 s stale re-baseline, boundary `(start,end]`, null-not-zero, MicroDev5 identity `HS1×Tob5`, DI5 bounds), `AdaptiveStrikeProjectionTests.cs`.
- Channel: normalizer tests (depth-originated rows only; L1-only rows ignored; first row), `AdaptiveRecoveryBufferTests.cs`, `AdaptiveSessionRecoveryTests.cs`, ordering/stable-lag tests.
- Persistence/PG: `AdaptiveSupplementalPersistenceTests.cs`, `AdaptiveMigrationPostgresTests.cs` (up, idempotent twice, old-model tolerance), `StrikeAttestSeed.cs`; digest-pin test; `PaperEngineV2/UnverifiedStrikeFrameTests.cs` unchanged-green.
- Parity: `AdaptiveRestartParityTests.cs`, `AdaptiveCoreParityTests.cs` (core JSON identical before/after), `RelationalRestartParity`.
- Summary/Dashboard: `MarketSummary/MarketSummaryTests.cs`, `MarketSummarySemanticsTests.cs`, `MarketSummaryDataServiceTests.cs`, `LowerUpperGridTests.cs`, `MarketSummarySectionRenderTests.cs` (placeholder counts, Delta Dir tests from section 1.3), `DashboardSimplificationTests.cs`, `Dashboard/AdaptiveCompactGridTests.cs`, `AgentFeedTests.cs`, `MarketSummaryPerformanceTests.cs`.
- Placement guards: no OFI5/MicroDev5 column in TOTAL grids; no Depth5 column in fixed-strike; grid/column order tests updated.

---
## 16. Decisions for the owner

**Owner response ("Approve all"): D2-D9 approved as recommended (D2 depth-originated rows only; D3 hard 5 s limit; D4 premium-notional pooled; D5 OFI5/Depth5 raw, MicroDev5 `(Inv)`; D6 derive Agreement/Fallback per window, placement still to be specified before any work on it; D7 names; D8 MicroDev5 formula A; D9 Delta revert plan, independent). Approval covers the design contract only: implementation sequence S1-S8 still needs an explicit go for each step, and B1-B5 remain open. D1 (finish Phase 0) was not separately confirmed.**

Original list:
D1 Approve completing Phase 0 (B5 sampling now; B1-B4 need your data/VM or a dev capture). D2 Depth channel from depth-originated rows only (recommended; rev. 1 alternative rejected). D3 5 s hard freshness limit for depth (recommended). D4 TOTAL aggregation: premium-notional pooled (recommended, as specified) vs per-contract equal weight. D5 Display convention: OFI5/Depth5 raw; MicroDev5 `(Inv)` like MicroDev (recommended) — confirm. D6 Item 5 (Agreement/Fallback): placement not given; derive per window (recommended) — specify grid or defer. D7 Table/column/version names (section 9). **D8 MicroDev5 formula A (recommended) vs B vs defer (no contract until you choose).** D9 Approve Delta display revert plan (section 1) and its test updates; may ship first and independently.

---
## Appendix A — Phase 0 SQL (read-only; parameters `:toks` comma list, window literals)
Run as `psql -h localhost -U research_ro -d niftysignal_vm_copy` with `PGOPTIONS="-c default_transaction_read_only=on -c statement_timeout=300000"`; `toks` from `instruments` by `AsOfDate`/`Underlying='NIFTY'`/`InstrumentType`.
```sql
-- A1 row-type split, monotonicity inside depth rows (counts only)
with t as (select "Token" tk,"Id" id, (bid1_price,bid1_qty,ask1_price,ask1_qty) l1,
 (bid2_price,bid2_qty,bid3_price,bid3_qty,bid4_price,bid4_qty,bid5_price,bid5_qty,ask2_price,ask2_qty,ask3_price,ask3_qty,ask4_price,ask4_qty,ask5_price,ask5_qty) dp,
 bid1_price b1,bid2_price b2,bid3_price b3,bid4_price b4,bid5_price b5,ask1_price a1,ask2_price a2,ask3_price a3,ask4_price a4,ask5_price a5
 from ticks where "Token"=any(string_to_array(:'toks',',')) and "ExchangeTimestamp">=:'d0'::timestamptz and "ExchangeTimestamp"<:'d1'::timestamptz
  and "ReceivedAt">=:'d0'::timestamptz and "ReceivedAt"<:'d1'::timestamptz),
l as (select *, lag(l1) over w pl1, lag(dp) over w pdp from t window w as (partition by tk order by id)),
c as (select tk, (pl1 is not null) hp, (l1 is distinct from pl1) l1c, (dp is distinct from pdp) dpc,
 (b2>=b1 or b3>=b2 or b4>=b3 or b5>=b4 or a2<=a1 or a3<=a2 or a4<=a3 or a5<=a4) nm, (a1<b1) cr from l)
select tk, count(*) filter (where hp and l1c and not dpc) l1_only, count(*) filter (where hp and dpc and not l1c) deep_only,
 count(*) filter (where hp and dpc and l1c) both, count(*) filter (where dpc and nm) nonmono_in_depth_rows,
 count(*) filter (where dpc and cr) crossed_in_depth_rows from c group by tk order by tk;
-- A2 depth-observation cadence: same CTE, then
--   d as (select tk, ra, lag(ra) over (partition by tk order by id) pra from l2 where dp is distinct from pdp and pdp is not null)
--   select tk, count(*), percentile_cont(.5/.9/.99) within group (order by extract(epoch from ra-pra)), max(...), count(*) filter (where gap>5), count(*) filter (where gap>30)
-- A3 completeness: counts of depth-null rows, levels with price>0 and qty>0 per side (<5), price>0 & qty=0, gaps (empty level before non-empty), crossed/locked L1.
-- A4 day inventory: exists(select 1 from ticks where "ReceivedAt">=d::timestamptz+interval '10 hours' and "ReceivedAt"<d::timestamptz+interval '14 hours' [and bid5_price is not null] limit 1)
-- A5 read cost: select count(*), sum(<L1 qty cols>) vs sum(<all depth cols>) over the same token/window predicate (timed).
```

---

## Appendix B - S3 input-event and freshness contracts (rev. 3)

Phase 0 evidence (2026-10-29, three tokens, counts only): across ~186k consecutive-row pairs, a row that changed levels 2-5 NEVER also changed Level 1 (touchline and depth arrive as separate messages); every depth-only row had a monotone, uncrossed, non-empty-top ladder (0 of 41,222 violations); but 84% of Level-1 PRICE-only rows (31,010 of 37,101) pair the new touchline with stale depth into a non-monotone ladder, and Level-1 quantity-only rows (2,300) were monotone. Stored rows between depth updates are therefore not verified books.

Event contract (`AdaptiveDepthNormalizer`, per token, source Id order, compared with the previous row):
- `Snapshot`: levels 2-5 changed, first complete book, or depth re-appeared. The only way a book becomes usable. May itself be invalid (crossed, non-monotone, empty top) -> unavailable with the `Depth5Validity` reason.
- `L1Superseded`: depth identical but Level 1 (price OR quantity) changed while a valid snapshot is held. The book is unavailable until the next `Snapshot`. No "new L1 + old depth" book is ever produced; nothing is carried over. Emitted once per episode; no marker when no valid snapshot is held.
- `NoDepth`: depth disappears. Repeats and unchanged rows emit nothing, so they never refresh freshness.
- Level-1 input stream, `AdaptiveIncrementalTickNormalizer`, cleaner and trade classification are unchanged.

Freshness: a valid snapshot is usable for at most 5 s after its `AvailableAt` (inclusive) and only until the next event of that token. `Depth5Coverage.Segments` returns the explicit available/gap partition of any range with reasons `NotYetObserved`, `Stale`, `L1Superseded`, `InvalidBook` (+ validity), `NoDepth`.

OFI5: transitions only between consecutive valid snapshots; identical books are not transitions; invalid snapshots and `NoDepth` break the baseline. DECISION D10 (open): an `L1Superseded` marker does NOT break the baseline by default (both endpoints are coherent snapshots, the Level-1 moves in between are part of their difference, exactly as for consecutive Level-1 snapshots); `Depth5Flow.Ofi5(..., breakOnL1Superseded: true)` implements the strict alternative.

## Appendix C - S4 Futures-only integration contracts (D10 closed)

**Scope.** Futures token only; flag `Adaptive:Top5Microstructure:Enabled` (default **false**, shipped in `appsettings.json`). Flag off reads no depth rows, builds no tracker, and packages carry `Depth5 = null`. Core observer output is byte-identical with the flag on or off (JSON-equal in `T10`; SHA-256 of the 170 completed bars identical on the 2026-10-29 data in the Host benchmark).

**Delivery.** `FuturesDepth5Feed` (normalizer + pending queue) is shared by live polling and recovery. Before each availability group at instant T, every depth event with AvailableAt <= T is delivered (`DrainThrough`), identically in both paths; depth events bypass the core ordering state (like spot). Late events (older than the last delivered) are dropped and counted; replay includes them, so sidecar verification reports a mismatch instead of hiding it.

**D10 bridging (implemented in `FuturesDepth5Tracker`).**
1. Both endpoints must be valid Depth5 snapshots; 2. elapsed between endpoints <= 5 s (inclusive); 3. an `L1Superseded` marker makes time-weighted coverage unavailable (`Depth5SupersededSeconds`) until the next valid snapshot; 4. nothing is interpolated or carried across that interval; 5. the transition is computed only at the new snapshot's AvailableAt; 6. an invalid/crossed/NoDepth observation breaks the baseline; 7. FeedGap, or >5 s, breaks it too (`Ofi5BaselineResets`); 8. a repeated identical snapshot is not a transition and never renews freshness or the baseline clock (after a marker an identical snapshot only restores coverage from its own instant); 9. `Ofi5DirectTransitions` / `Ofi5BridgedTransitions` / `Ofi5BridgedNet` / `Ofi5BridgedGapSeconds` are recorded separately; 10. bridged OFI5 is the **sampled net displayed-book change** between two coherent snapshots, not proof of every intermediate event.
Limitation: a stored row merges touchline (tf) and depth (df) deltas, so a structurally valid snapshot may mix generations; simultaneity at the broker is not claimed (blocker B4).

**Time accounting.** Every second of a bar is exactly one of Usable / Stale / Invalid / Superseded / NoDepth / Unobserved. Time-weighted values (Tob5, MicroDev5Proxy, DepthImb5, notionals) are weighted by Usable seconds only; zero-duration split bars have all-zero seconds and null values and receive no transitions.

**Persistence.** Table `adaptive_futures_depth5_bars`, unique `(SessionId, BarSeq, MetricsVersion="futures-depth5-v1")`; additive migration `20261124060445_AddFuturesDepth5Sidecar` with idempotent script. Insert-if-missing / verify-if-present (1e-9), never overwritten; mismatch recorded in `adaptive_projection_health` (component `futures-depth5`); failures use their own retry queue and never change the paper-verification result. A live depth read failure withholds the sidecar for the process (fail-closed) without affecting the core.

**Rollback.** The migration only adds a table; an older Host ignores it and unknown history ids.

## Appendix D - S5 options-side contracts (options-depth5-v1)

**Scope.** Every contract of the frozen session universe (`context.Options`; ~40) gets its own `FuturesDepth5Tracker` inside `OptionsDepth5Tracker`: independent ordering (late-event detection per token), freshness clock, OFI baseline, invalid/gap state and D10 bridging. Same formulas, 5 s rule and D10 rules as Appendix C; nothing is fabricated from L1-only events. A contract that never produced an event is wholly "unobserved" (null metrics, never zero). Identity (token, trading symbol, expiry, strike, side) travels with every row. Flag `Adaptive:Top5Microstructure:OptionsEnabled` (default false, independent of `Enabled`).

**Persistence.** `adaptive_options_depth5_bars` (unique `(SessionId, BarSeq, Token, MetricsVersion)`) and its own `adaptive_options_depth5_attestation` (unique `(SessionId, BarSeq, MetricsVersion)`, per-token SHA-256 digests of every value column), via additive migration `20261124064802_AddOptionsDepth5Sidecar` + idempotent script. option-strike-v3 and `adaptive_strike_attestation` are untouched. Insert-if-missing writes rows + attestation in one SaveChanges; verify-if-present compares every column, then (re)issues the attestation of exactly the stored rows; a mismatch never overwrites, writes a whole-bar `options-depth5` marker and revokes the attestation in one SaveChanges. `VerifiedOptionsDepth5.LoadServedRowsAsync` serves only attested, unmarked, digest-matching rows (fail closed; no row, no attestation, marker, edited value or unreadable table = unavailable). Own retry queue; outcome never affects the paper verification result. A mismatch marker is not healed by a later honest replay (the bar stays withheld, by design).

**Recovery / live.** Recovery reads option depth rows lazily in 2-minute RECEIVED-time windows (`OptionsDepth5Source`), ingesting only rows that can still yield an event at or before the next group (plus a 5 s margin), so pending/retained state stays small and the delivered events equal those of a whole-day ingest (test T10b). An Id-keyset LIMIT paging variant was rejected: PostgreSQL walked the primary key (~24 s per page). Live polls `Id > cursor` for the option tokens; a read failure withholds only the options sidecar for the session.

**Limitations.** Same as futures (B4: tf/df mixed-generation rows; simultaneity not claimed). The `OptionsEnabled` flag adds a second option-token read per live poll. Rollback to an older Host needs only that Host to ignore the extra tables (unknown history ids are harmless); an actual previous Host binary must still be run against the new schema before production.

## Appendix E - Dashboard / Market Summary integration (S6-S8 consolidated)

Contracts implemented (all additive; flags default OFF; with no sidecar rows every new cell is "—", never zero):

- **Futures compact grid** (`data-grid="futures-compact"`), appended: `OFI5`, `MicroDev5Proxy (Inv)` after OFI; `Price-Flow Agree`, `Fallback Share %` after Roll Efficiency. Per bar, AVG and DAY AVG rows. OFI5 = net/gross, AVG = Σnet/Σgross; MicroDev5Proxy usable-seconds weighted and displayed inverted by `MicroDevDisplay` (same convention as Level-1 MicroDev; stored/raw values never inverted); Price-Flow Agreement = the existing `PriceStrictDeltaAgree` / `PriceEnrichedDeltaAgree` 10-bar rolling flags (S/E, check/cross/dash), AVG = share of defined bars that agree; Fallback Share % = existing `FallbackShare` x 100, AVG = mean. OFI5 cell tooltip separates direct from bridged transitions.
- **Fixed-strike grid** (`options-core-direction`), appended: CE/PE/Dir OFI5 and CE/PE/Dir MicroDev5Proxy (Inv), rows, AVG and DAY AVG. Direction keeps the Level-1 conventions (combined gross denominator; (CE Tob5 - PE Tob5)/2 x usable overlap). Only exact-token, attested `options-depth5-v1` rows are served. All Level-1 columns unchanged.
- **Futures Summary**: `Depth Imb L5` per window = (Σbid - Σask)/(Σbid + Σask) of the bars' time-weighted notionals, weights = usable seconds; bars without usable depth are skipped and reported as `n/m partial`; no usable bar = "—".
- **Total Options Summary (main, ITM-shifted, OTM-shifted)**: `Depth L5 Dir` = CE ratio - PE ratio, each side normalized by ITS OWN pooled premium notional (Σ over included contracts and bars of notional x usable seconds). A contract is included only when EVERY bar of the window has an attested row with usable depth (the all-bars discipline of the other flow families: a hole is never bridged, zero-filled or substituted); the cell shows `included/expected`; Direction needs at least one included contract on both sides. OFI5 / MicroDev5 are NOT in the Total grids (not approved).
- **Trust enforcement**: option depth reaches the Dashboard only through `VerifiedOptionsDepth5` (no attestation, a marker, changed values, unreadable table = unavailable, fail closed). Futures depth follows the futures trust model (per-bar marker withholds the bar). `UndefinedTable` tolerated everywhere. Strike-v3 rows and attestations are untouched.
- **Agent feed**: additive `top5` object per bar (raw, not inverted; direct/bridged counts); null when absent.
- **Recovery paging hardening**: `OptionsDepth5Source` keeps per-token Id-ordered buffers, reads one full window beyond the limit, and ingests a token's rows in Id order while `ReceivedAt <= limit`; `OutOfOrderRows` must be 0. Test `T10c` shuffles ReceivedAt (+-30 s, +-100 s) and asserts every stored row is read exactly once and the per-token event stream equals a whole-day Id-order ingest. Assumption (documented limit): a row's ReceivedAt may deviate from its Id order by less than the 2-minute window; beyond that `OutOfOrderRows` would be non-zero and the sidecar verification would report a mismatch rather than hide it.
- All new signs are hypotheses, not established directional rules (stated in the column tooltips).

### Appendix E validation results (2026-11-24, local, uncommitted)
- Full Release suite with the scratch PostgreSQL enabled (migrations, idempotence, attestation, old-Host rollback): 2979 passed, 101 skipped (env-gated), 1 failed: `MarketSummaryPerformanceTests.DatabaseLoad_PostgreSql_FullSessionSidecar_Timings` timing guard under whole-suite parallel load (p95 662 ms vs 500 ms guard); standalone it passes (bar 220 p50 90 ms / p95 137 ms; bar 30 p50 12.6 ms). Not a functional failure.
- Existing layout-pinned tests were updated for the approved new columns (futures +4, fixed-strike core +6, Total grids +1); no behavioural assertion was relaxed.
- Final BOTH benchmark (2026-10-29, 40 contracts, 170 bars, real scratch PG): insert recovery 188.8 s, verify restart 130.7 s, peak WS 1360 MB, mismatches 0, failures 0, core hash identical to OFF, 6800 option-depth rows + 170 futures-depth rows + 170 attestations. S5 reference: OFF 125-169 s / 1.43 GB; BOTH 183-201 s / 1.35-1.49 GB. GC pause 10.2 s total (S5: 5.4-5.9 s) - noisier, deferred: no memory or deadline threat.
- Real-data disorder check (research_ro, 2026-10-29, 12 option tokens, 534,596 rows): 0 per-token ReceivedAt-vs-Id inversions.
