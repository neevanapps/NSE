# Volume-Bar Metrics — Plain-Language Guide

This is a simple walkthrough of everything we tested on the new volume-bar system. The
[detailed technical log](VOLUME_BAR_FINDINGS.md) has every number and every caveat; this document
is the same work explained in plain words, with real numbers from our own data as examples.

**Update note:** this guide was originally written for the futures phase only. It's now rewritten
to cover the *entire* project journey — futures metrics, the options-chain phase built on top of
them, and the final combination attempt — through the point where the options phase closed and a
final config was locked. If you only read one section, read "Where things stand now" at the very
end.

**Earlier update note (kept for history):** an earlier rewrite of this guide followed the discovery
of a real gap — a trading-hours rule that was supposed to be enforced but wasn't. That fix changed
several of the futures numbers below, including which metric looked "best." See "The trading-hours
rule we forgot" section further down; it's worth reading before the per-metric results, since it's
the reason some of those numbers moved.

## The basics, explained simply

**What's a "volume bar"?** Normally we'd look at the market every 15 seconds, like a clock
ticking. Instead, here we look at it every time a fixed amount of trading happens — for example,
every time 1,300 lots of Nifty future change hands, we call that "one bar" and compute fresh
readings. When the market is busy, bars form in seconds. When it's quiet, a bar might take several
minutes to fill. The idea: we're measuring by *how much people are trading*, not by the clock.

**What's a "score"?** For each metric below, we turn its raw reading into a number from -100
(strongly bearish) to +100 (strongly bullish). We do this by comparing today's reading against
every other reading we've seen so far *today* — "is this more extreme than 95% of everything
we've seen today?" That becomes the score.

**How do we trade it?** When a metric's score crosses into extreme territory (say, more extreme
than 95% of the day's own readings), we open a paper trade — buy a Call option if the score is
bullish, buy a Put option if bearish, using the option closest to the current price. We hold it
until the score flips to the opposite extreme, or the trading day ends. **New rule, now enforced:**
no trade can open before 9:30am or after 3:00pm, and any open trade is force-closed by 3:15pm,
regardless of what the score says.

**What do "650/95" or "2600/93" mean?** The first number is the bar size (how many lots make one
bar — smaller means more, faster bars). The second is the percentile threshold — "95" means we
only trade the most extreme 5% of readings that day.

**What's "win rate"?** Percentage of trades that made money. **"Net pts"** is the total profit or
loss in option-price points across the trading days we tested. **"Concentration"** is how much of
the total profit came from just one lucky trade (or one lucky day) — a low number is good (the
profit is spread out); a high number is a warning sign (the whole result might just be one
outlier).

**What's "0-DTE"?** DTE means "days to expiry." 0-DTE is expiry day itself — options move much
more wildly on that day (small underlying moves can swing an option's price 100%+), so we always
double-check whether a metric's good result is real skill or just lucky expiry-day chaos.

**Options-specific terms, explained once here so they don't need re-explaining below:**
- **ATM ("at the money")**: the option strike price closest to where the market is trading right
  now. ATM±1 means "the ATM strike plus the one strike above and the one strike below it" — three
  strikes total, each with a Call and a Put, so six instruments. ATM±2 widens that to five strikes
  (ten instruments), and so on.
- **Notional**: the rupee value of a trade or position — quantity traded (or held) multiplied by
  price. We use it instead of raw contract counts because a deep out-of-the-money option and an
  expensive ATM option can trade the same number of contracts for very different amounts of money.
- **IV ("implied volatility")**: roughly, "how much movement the option market is currently pricing
  in." It's not directly on the data feed — we solve for it from the option's traded price using
  the same option-pricing machinery (Black-Scholes) already trusted elsewhere in this project.
- **OI ("open interest")**: the number of option contracts currently open (not yet closed out) at a
  given strike — a rough gauge of where real positions are sitting, as opposed to volume, which is
  just today's trading activity.
- **Max Pain**: the strike price at which option sellers (as a group) would owe option buyers the
  least money if the market settled there today. There's a long-standing trading folklore that price
  sometimes gets pulled toward this strike near expiry — we tested it rather than assumed it.
- **Band width / "ATM±N"**: how many strikes on each side of the ATM strike a metric aggregates
  over. This turned out to matter — see the depth-imbalance band-width finding below.

---

## The trading-hours rule we forgot to apply — and why it changed everything

Partway through this work, we were asked to confirm that every trade actually followed the
standing rule: no new trade before 9:30am, no new trade after 3:00pm, everything closed by 3:15pm.
Checking the actual code, this rule **was not being enforced anywhere** — trades had been opening
as early as 9:15am and running all the way to 3:30pm market close in plenty of cases, for the
entire duration of this project so far.

We fixed it, then re-ran everything to see what changed. The honest answer: **quite a lot.** Our
previous "best" metric (TobDepthDivergence, #10 below) turned out to owe a large share of its
apparent edge to trades opened in the first 15 minutes or held past 3pm — exactly the windows now
off-limits. Once those were excluded, its win rate fell from a strong 60.8% to a weak 45.1% (worse
than a coin flip). Meanwhile two other metrics (DepthImbalance and BarDurationUrgency) held up
fine or even *improved slightly*, because their own edges weren't sitting in those excluded
windows.

**The numbers below are all the corrected, gated ones** — what actually happens under the rule
that applies. Where a metric's story meaningfully changed because of this fix, it's called out
explicitly.

---

## PART ONE: The futures phase (locked, unchanged since 2026-09-18)

### The 10 individual metrics we tested, one by one

#### 1. TrendReversion — *does the market look like it's overextended and due to snap back?*

**How it's worked out:** Look at the net price move over the last 15 bars, divide by how much the
price wiggled around to get there (a straight line up = "1", a jagged sideways path that ends up
higher = a smaller number). Then flip the sign — a very *clean, straight* move is read as a
warning of a reversal, not a "keep going" signal.

**Result under the gated rules:** still inconsistent — some settings look decent (up to ~60% win
rate), others lose money, with no stable pattern across nearby settings.

**Status: Not confirmed.** Unchanged verdict after the gate fix.

---

#### 2. FutureCvdNet — *are more people buying or selling the future right now?*

**How it's worked out:** We don't have a real "who bought vs. who sold" feed, so we estimate it: if
a trade happens at or above the midpoint of the current bid/ask, we guess it was a buyer stepping
in; below the midpoint, a seller.

**Result under the gated rules:** still weak — win rate barely clears 50% at its best setting, and
most settings lose money.

**Status: Not confirmed / closed.** Unchanged verdict. Would need a real buyer/seller data feed to
do better, which we don't have.

---

#### 3. DepthImbalance — *is there more buying interest or selling interest resting in the order book?*

**How it's worked out:** Look at the order book — how many lots are waiting to buy (across the top
5 price levels) vs. waiting to sell. `(Bid total − Ask total) / (Bid total + Ask total)`.

**Real example**, from an actual live snapshot: 1,75,955 lots resting on the bid side vs.
2,46,545 on the ask side → `(1.76L − 2.46L) / (1.76L + 2.46L) ≈ -0.17` — a mildly "more sellers
waiting" reading.

**Result under the gated rules:** best setting shifted to 650/93 (a smaller bar size than before).
About 11 trades/day, **56.1% win rate, +151.50 points** across 7 real trading days — this includes
a simple 30%-loss stop (exit if the option loses 30% of its value), which we re-tested and found
genuinely helps here (both win rate and profit improve together when it's added, not just one or
the other).

**What we found when we checked it properly:** its edge is now **even more concentrated in the
first 30 minutes of trading** than we first thought — 78.6% win rate in that opening window, and
it actually *loses* money for the rest of the day (both midday and afternoon). Holds up
similarly well on expiry days and ordinary days.

**Status: One of the two strongest standalone futures metrics.** This is the anchor of the
session-gated switch below, and later became the "Open leg" of the futures score used through
the rest of this project.

---

#### 4. OrderFlowImbalance (OFI) — *is real buying/selling pressure building or draining from the order book?*

**How it's worked out:** Unlike DepthImbalance (a snapshot), this looks at the *change* — did the
resting buy size grow or shrink since the last tick, and did the resting sell size grow or shrink?

**Real example**, from our own test data: resting buy size grows from 50 to 80 lots at the same
price (someone adding more buy orders) while the sell size stays at 30 → that bar reads as +30,
a bullish signal.

**Result under the gated rules:** same best bar size as before (2600), but the win rate fell below
50% (44.2%), and over half the profit now comes from a single trade (53.3%). It's also heavily
tilted toward expiry days — a 60.0% win rate on expiry days vs. just 40.5% on ordinary days.

**Status: Downgraded — the most fragile metric still in consideration.** Was a "promising watch
item" before the gate fix; the gate fix made its weaknesses more visible, not less.

---

#### 5. FutureOiBuildupQuadrant — *is money flowing into new positions, or out of old ones, and which direction?*

**How it's worked out:** Combine two things — did price go up or down, and did open interest (the
count of contracts still open) go up or down. Four combinations, each with a classic trading
meaning (new buyers piling in, new sellers piling in, buyers leaving, or sellers covering).

**Result under the gated rules:** still weak and inconsistent, win rate below 50% at most settings.

**Status: Not confirmed.** Unchanged verdict.

---

#### 6. VwapDeviation — *is price trading rich or cheap compared to today's average traded price?*

**How it's worked out:** VWAP means "volume-weighted average price" — the average price of the
day, weighted by how much volume traded at each price. We check: is the current price above or
below that running average?

**Correction (2026-09-18):** we originally dismissed this one mainly as "doesn't trade often
enough" — that undersold how weak it actually is. Looking at the *actual* trades rather than just
the trade count: win rate never exceeds 28.6% at **any** bar size or percentile we tried — it loses
on roughly 7 of every 10 trades, everywhere. The only reason some settings still show a positive
total is two specific trades — one on 09-11 and one on 09-15 (the same two high-volatility days
that inflated several other metrics' headline numbers) — where it happened to buy an option right
at the open and hold it all the way to the close of the day during a huge move (+130% and +290%
respectively). Those two lucky trades outweigh the *combined losses of every other trade*. Take
them away and this metric loses money consistently.

**Status: Confirmed weak**, not just "inconclusive." It doesn't behave like a real trading signal
so much as an occasional all-day directional bet that mostly loses small and occasionally wins
big on a day it got lucky. Would need a fundamentally different design (a shorter rolling window
instead of the whole session, and exiting dynamically instead of holding to end of day) to be
worth trying again.

---

#### 7. BarDurationUrgency — *how urgently is the market trading right now?*

**How it's worked out:** Every volume bar takes some amount of time to fill. A bar that fills in
5 seconds means intense trading; one that takes 3 minutes means it's quiet. We take 1 ÷ (seconds
to fill the bar) as "urgency," and give it a direction using whichever way price moved during that
bar.

**Result under the gated rules:** same best bar size as before (2600). About 10 trades/day,
**54.9% win rate, +119.60 points** across 7 real trading days.

**What we found when we checked it properly:** the picture is now much clearer than before — it
actually **loses money in the first 30 minutes** (45.5% win rate there) and makes essentially all
of its profit in the middle of the day and the afternoon. That's the exact opposite pattern from
DepthImbalance, which is what made combining the two so appealing (see below).

**Status: One of the two strongest standalone futures metrics**, alongside DepthImbalance — the
"Mid/Close leg" of the session-gated switch below.

---

#### 8. PriceImpact (a "Kyle's lambda" style measure) — *how much does price move per unit of volume traded?*

**How it's worked out:** Price change during the bar, divided by the bar's own volume. In theory
this measures how "thin" or "liquid" the market is right now.

**Result under the gated rules:** still weak, win rate never above 50%.

**Status: Not confirmed.** Unchanged verdict — as suspected from the start, dividing by volume
barely matters when bars are built to a near-fixed volume size, so this ends up being a diluted
version of plain price movement.

---

#### 9. TopOfBookImbalance (TOB) — *who's winning the battle right at the best price, ignoring the rest of the book?*

**How it's worked out:** Same idea as DepthImbalance (#3), but instead of looking at the top 5
price levels, we only look at the very best bid and best ask.

**Real example**, same live snapshot as before: best bid had 520 lots waiting, best ask had only
65 → `(520 − 65) / (520 + 65) ≈ +0.78` — strongly "buyers winning at the top." Notice this is the
**opposite sign** from DepthImbalance's reading of the exact same moment (-0.17)! The touch (best
price) said bullish; the full book said bearish.

**Result under the gated rules:** best setting shifted to 2600/80. About 20 trades/day, **54.7%
win rate, +124.60 points** — a large jump in trade count from before, now near the upper edge of
what we consider a workable trade frequency.

**What stands out:** this is one of the **most consistent metrics of everything we've tested** —
54.9% win rate on expiry days vs. 54.7% on ordinary days (almost identical), and positive in every
part of the trading day. It doesn't have DepthImbalance's or BarDurationUrgency's raw edge size,
but it's the most reliably "not fooling itself" of the group.

**Status: Folded into the futures switch as a confirmation filter**, not a third switched leg or
a blend input — during the open only, it must agree in sign with DepthImbalance before a trade is
allowed. Costs nothing (it can only filter, never add trades).

---

#### 10. TobDepthDivergence — *when the best price and the full order book disagree, which one should we trust?*

**How it's worked out:** DepthImbalance minus TopOfBookImbalance — how much more bullish the
*full* book reads than the *touch* does, at the same moment.

**Real example, using the exact same live snapshot as before:** DepthImbalance read -0.17, TOB
read +0.78. Divergence = -0.17 − 0.78 = **-0.95**. Under the rule we adopted ("fade the thin
touch, trust the deeper book"), this specific real moment would have triggered a **Put** trade.

**This was our "strongest candidate" before the trading-hours fix — and it collapsed once we
applied it.** Before the fix: 60.8% win rate, +420.65 points, the best result we'd ever found.
After: best setting only reaches **45.1% win rate, +129.30 points**, and no combination of
settings achieves both a good win rate and a good profit at the same time. We checked the actual
trades and confirmed why: a large share of its edge was sitting in trades opened in the very
first 15 minutes or held past 3pm — exactly the windows the trading-hours rule now excludes. It
wasn't a bug in our earlier numbers; it was a real edge that simply isn't tradeable under the
rules that actually apply.

**Status: Downgraded from "strongest candidate" to weak.** A cautionary tale about how much a
single rule you forgot to apply can change a "best result" — and about always double-checking a
promising number against the rules that actually govern live trading before trusting it. A friend's
early draft of the options-phase plan initially named this metric as the futures baseline to build
on top of, based on stale pre-correction information — caught and corrected before any options work
started.

---

### What we built after finding all this — the futures score

**The composite score — a weighted blend, tried but came up short.** Once we had 5 metrics still
worth considering (DepthImbalance, BarDurationUrgency, TopOfBookImbalance, OrderFlowImbalance, and
TobDepthDivergence), the natural next step was combining them into one score, weighted by how much
we trust each one. **It didn't work as hoped.** The blended score came in at only 49.6% win rate
and +80.95 points — *worse* than simply trading DepthImbalance on its own (56.1% win rate, +151.50
points). The likely reason: DepthImbalance and BarDurationUrgency make their money at *opposite*
times of day. Averaging their scores together every moment waters both signals down instead of
combining them — like trying to average "turn left" and "turn right" into a straight line.

**The session-gated switch — the futures score that stuck.** Instead of blending, we tried simply
**switching** which metric drives the trade depending on the time of day: DepthImbalance's own
score in the first 30 minutes (its strong window), BarDurationUrgency's own score for the rest of
the day (its strong window), with TopOfBookImbalance as an open-window confirmation filter. Never
averaging — always exactly one metric or the other. **This worked.** Result: about 10 trades/day,
**57.4% win rate, +177.25 points** — the best win rate and best profit of anything tested on the
futures side, beating both individual metrics and the blended composite.

**One honest caveat kept from the original result:** almost all of that profit (97.7%) came from
a single very volatile day (2026-09-15). That's not the same problem as "one lucky trade" (the
trades themselves are spread out fine, 5 of 7 backtest days were profitable), but it meant the
result still needed to prove itself on fresh days.

**Futures phase closed 2026-09-18, locked as `SessionGatedDepthDuration` @ 2600/90.** Also tried
and explicitly rejected before closing: bigger fixed bars (3900/5200 lots — no metric improved),
and rolling/sliding-window bars (made every metric flat-to-worse, because overlapping windows
dilute the percentile-ranking gate). This futures score has been unchanged since.

**First out-of-sample reading (2026-09-18, the first genuinely unseen day):** a losing day, 41.7%
win vs. the 57.4% backtested rate. Not damning on one day — see the standing rule below about
treating any single day as a data point, not a verdict — but a reminder the futures switch, like
everything else here, still needs to keep proving itself.

---

## PART TWO: The options phase (2026-09-18 onward)

### Setting the stage: reusing the same clock

With the futures score frozen, the natural next question is: can the option chain — Calls and Puts
around the current price — tell us anything the future alone can't? Rather than building a whole
second bar-counting engine, we reused the exact same future-volume-bar clock. Every option-chain
metric below is simply "the state of the ATM±1 (or wider) options complex as of this future bar's
close" — same entry/exit machinery, same percentile-ranking gate, same 9:30–3:00/3:15 trading-hours
rule, all unchanged. This is one of the reasons the options phase could move quickly: the plumbing
was already built and trusted.

One real underlying-price subtlety mattered here: IV is solved against a **synthetic forward**
price (derived from put-call parity), not the raw future price directly — using the raw future
biases Put IV noticeably below Call IV at every strike, a bug already caught and fixed earlier in
this project. Re-confirmed clean on real data: the Call/Put IV gap averaged 0.43 percentage points
using the synthetic forward, versus several points of gap the old bug produced.

The options phase followed a 5-phase plan (Phase 0 setup → Phase 1: 5 core metrics → Phase 2: 3
depth metrics → Phase 3: redundancy/stop-loss across the survivor pool → Phase 4: build one
OptionsScore → Phase 5: try combining it with FuturesScore).

---

### Phase 1: the 5 core options metrics

#### 1. ATM IV Change — *is the market's expected volatility rising or falling?*

Two useful variants came out of this one:

- **Raw ΔIV** (this bar's ATM implied volatility minus the previous bar's): **CONFIRMED**, but only
  once 0-DTE (expiry) days are excluded — 1300/97, **80.2% win rate, +77.25 net** over 5 days. The
  strongest single win rate found anywhere in the options phase, though on a smaller trade sample
  and with real concentration (32.8%).
- **Price-signed ΔIV** (`-sign(price change) × ΔIV` — the negative sign was added after the
  un-negated version was tested and clearly rejected): **CONFIRMED, no DTE gate needed** — 1300/97,
  **56.8% win rate, +153.85 net** over 7 days, with the lowest concentration seen in the project
  up to that point (22.4%). In plain terms: a price move that *isn't* accompanied by a matching
  move in expected volatility is the informative signal — not simple co-movement.

**Verdict: both confirmed**, kept as separate candidates (later correlation checks found them
statistically independent from each other despite sharing the same underlying IV data).

---

#### 2. Notional Call-Put Volume Delta — *is more real money buying Calls or Puts?*

**How it's worked out:** Add up the rupee value (notional) of everything traded on Calls near the
current price, and everything traded on Puts, across the ATM±1 band. The original hypothesis
(`Call notional − Put notional` = bullish) was tested and rejected outright — win rate sat at or
below 50% with no clean pattern. Flipped to `Put notional − Call notional`, and the full sweep was
re-run from scratch (not inferred): a clean pattern appeared immediately.

**Result:** both 0-DTE days in the sample were 100%-loss single-trade days, so the metric is
**CONFIRMED, DTE-gated** (0-DTE excluded) — 1300/95, **70.0% win rate, +130.65 net** over 5 days,
40 trades, with the lowest concentration in the options phase at the time (20.2%).

---

#### 3. Notional Call-Put OI Delta (+ a rejected quadrant variant)

**A real bug caught before trusting any result:** the first build persisted OI-times-price as a
running level and differenced it — this conflates real open-interest change with the option's own
noisier price movement, since OI genuinely updates only about once a minute while an option's own
price moves nearly every bar. It produced 10x the trade count of every other metric on first run —
the giveaway that something was wrong — and was fixed by computing the flow directly from the raw
OI change at the moment it happens, priced at that moment, before any calibration was trusted.

- **Notional OI Delta** (`Call OI-change notional − Put OI-change notional`): clean pattern, no
  sign flip needed. **CONFIRMED, DTE-gated** — 650/80, **65.1% win rate, +152.00 net** over 5 days,
  43 trades, 20.0% concentration.
- **Buildup Quadrant** (classifying each strike/side by price direction × OI direction, the options
  equivalent of futures metric #5): weak both as built and sign-flipped (34–61% win rate, no clean
  pattern either way). **NOT CONFIRMED** — this is the second independent time this exact style of
  quadrant classification has failed (the futures side's equivalent metric also found no edge), so
  it's now treated as a closed line of investigation rather than something to keep re-trying.

---

#### 4. 25-Delta Skew Change — *is the market pricing more fear of a crash down than a rally up (or vice versa)?*

**How it's worked out:** reuses an already-trusted time-based skew calculation — find the strike on
each side (Call and Put) whose "delta" (roughly, sensitivity to price) is nearest 25%, meaningfully
out-of-the-money by construction, and compare the ratio of Put IV to Call IV there, bar to bar.

**Result:** raw change in that ratio is **CONFIRMED, no DTE gate needed** — but the first options
survivor with a real caveat. At loose thresholds it fires far too often (up to 159 trades/day,
~50% win) because the "25-delta strike" hops between adjacent strikes as the market drifts; the win
rate only becomes attractive (69–73%) once gated to the 97th percentile or tighter. Best combo
1300/97, **68.9% win rate, +112.25 net**, 103 trades — but the top 2 trades made up 55.1% of the
net, the highest concentration seen in the options phase. A later soft-stop check found a 30% stop
improves it further, making it the single strongest confirmed Phase 1 metric by win rate (71.7%
after the stop). Two other variants (price-signed, and a raw level instead of a change) were tried
and rejected — the level version barely traded at all under this project's entry/exit rule.

---

#### 5. Distance to Max Pain / Highest-OI Strike — *is price being pulled toward (or pushed away from) a magnet level?*

**How it's worked out:** compute the true Max Pain strike across the *whole* option chain (not just
the ATM band) each bar, and track how far price is from it.

**Result:** the un-negated version was a clean, strong *rejection* (win rate worsening toward 0% as
the gate tightened). Flipped, it became the cleanest pattern found anywhere in the options phase —
**win rate 60–85%, positive net in all 24 cells tested.** The catch: true Max Pain shifts are rare
events, matching the real-world "pinning" intuition, so it only fires about 2.1 trades a day even
at the loosest setting — nowhere near the 7–20 trades/day this project treats as a workable
standalone strategy. **Verdict: directionally confirmed, but not viable as a standalone traded
metric** — instead flagged as a strong candidate to use as a *confirmation filter* later, the same
role TopOfBookImbalance plays for the futures switch. (A second variant, distance to the
highest-open-interest strike rather than true Max Pain, was too sparse to judge — only about 7
trades across the whole 7-day sample.)

**Phase 1 closed:** 6 confirmed standalone sub-variants across 4 of the 5 metrics, plus this
Max Pain distance metric held for later use as a confirmation filter.

---

### Phase 2: the 3 depth metrics

These mirror the futures side's order-book metrics, but computed over the option chain's own
resting bid/ask depth.

#### 1. ATM Complex Notional Depth Imbalance — *more resting buy interest or sell interest across the option complex?*

The un-negated, "more buying = bullish" version was rejected (stuck below 50% win rate); flipped,
it produced one of the cleanest patterns in the whole project — above 50% win rate in nearly every
cell tested.

**Band width genuinely mattered here** — a real finding, not a minor detail: ATM±2 (2600/80) won
on win-rate quality (60.2% vs. 54.4% for ATM±1) and concentration (15.4%/30.5% vs. 22.8%/41.6%,
both meaningfully better), while ATM±1 (1300/90) won on raw net (+319.15 vs. +290.65) and
day-consistency (8 of 8 days positive vs. 7 of 8). **ATM±2 was adopted as primary**, following this
project's standing preference for win-rate quality over raw net, with ATM±1 kept as a credible
alternative. A later check against ATM±3 confirmed ATM±2 is a genuine peak, not an arbitrary
stopping point — net and win rate both fall going wider still (ATM±1 → ±2 → ±3 = 319/291/211 net,
54%/60%/57% win, a real hump shape). A 30% stop-loss was later adopted for this metric — the only
level where both win rate and net improved together (60.2%→60.4% win, +290.65→+299.85 net).

**CONFIRMED, sign-flipped, ATM±2 primary, 30% stop adopted.**

---

#### 2. TobDepthDivergence on the ATM Complex — *touch vs. full book, on options this time*

Same "full book minus best price" idea as the futures version, but on the option complex.
**CONFIRMED as-built, no sign flip, no DTE gate needed** — 1300/95, **55.1% win rate, +236.05 net**,
about 9.8 trades a day. Band width barely mattered for this one (ATM±2 gave essentially the same
result), so it was kept at ATM±1 for simplicity. No stop-loss level cleared the bar (nothing
improved both win rate and net together), so it's traded without one.

---

#### 3. Call vs. Put Depth Imbalance — *is the order book itself lopsided between Calls and Puts?*

Flipping the sign helped directionally (net went from all-negative to all-positive across the
grid), but trade frequency never reached the 7–20/day range this project treats as workable — at
most 4.0 trades a day at any setting tried. **NOT VIABLE AS BUILT** — same tier as a couple of the
sparse Phase 1 variants, a real but impractically rare signal.

**Phase 2 closed: 2 of 3 confirmed** (Depth Imbalance and TobDepthDivergence). Combined with Phase
1, the confirmed standalone options pool now stood at 8 metrics, plus the Max Pain confirmation-
filter candidate.

A follow-up check retested Phase 1's metrics 2 and 3 (which had only ever been built at ATM±1)
against ATM±2, since band width had just proven to matter for the Phase 2 depth metric. Result was
the *opposite* conclusion: ATM±1 held up as well as or better than ATM±2 for both — Volume Delta
was roughly a wash, and OI Delta clearly favored staying at ATM±1 (2600/75, 63.5% win, +192.20 net,
vs. ATM±2's best in-range result of only +82.65 net). **Lesson: band width is metric-specific, not
a universal rule — worth checking per metric, not assumed in either direction.** No change was made
to Phase 1's already-locked configs; this was a validation check, not a re-optimization.

---

### Phase 3: redundancy and stop-loss, across the whole 8-metric pool

Before building a combined OptionsScore, every pair of the 8 confirmed standalone metrics (28
pairs) was checked for correlation, to avoid effectively double-counting the same signal.

**Result: 26 of 28 pairs were fully independent** (correlation under 0.15) — clean, nothing forced
out of consideration. Two pairs were related-but-distinct (kept, not merged, same treatment as a
similar pair on the futures side): Volume Delta vs. OI Delta (+0.249 — both read ATM±1 options
activity, one traded flow, one building positions) and Volume Delta vs. Depth Imbalance (-0.220 —
plausibly because heavy notional volume eats into resting depth on one side). Notably, raw ΔIV and
price-signed ΔIV turned out to be essentially independent of each other (0.003) despite sharing the
same underlying IV data, confirming both were worth keeping as separate candidates.

A stop-loss sweep on the 3 metrics not yet tested found one more adopter: **ATM Depth Imbalance**
gets a 30% stop (the only level where win rate and net both improved). Raw ΔIV and TOB Divergence:
no stop level cleared the bar for either. **4 of the 8 confirmed metrics now carry an adopted
stop.**

---

### Phase 4: building OptionsScore

**The blend, tried first (required as the first step, same discipline as the futures side).** An
equal-weight average of all 7 standalone metrics (the 8th, Max Pain distance, was always intended
as a filter, not a blend input). Result: 1300/95, **62.3% win rate, +221.35 net**, 8.6 trades/day —
solidly mid-pack. It beat the two weakest individual metrics but didn't come close to the strongest
ones (Skew Change at 71.7%, gated Raw ΔIV at 80.2%). Same dilution story as the futures side's own
blend, now confirmed a second time.

**The 2-way switch, built next.** Using the session-phase pattern already found in the Phase 3
robustness checks — ATM Depth Imbalance is strongest in the Open window, Price-signed ΔIV is
strongest in Mid/Close — `OptionsScoreOpenMidSwitch` drives the score with ATM Depth Imbalance
before 10:00am and Price-signed ΔIV from 10:00am onward. Pure switch, never a blend, mirroring the
futures side's session-gated switch exactly. **Result: 2600/90, 57.1% win rate, +416.85 net, 156
trades, 19.5 trades/day, concentration 12.4%/24.8%** (among the lowest anywhere in the project) —
nearly double the blend's net at a comparable win rate, positive on 5 of 8 days, both 0-DTE days
strong (no gate needed). Beats every input metric that went into it. Same "switch recovers what a
blend dilutes" lesson as the futures side, now confirmed for a second time.

**The 3-way switch — where it landed.** As part of a longer list of follow-up experiments (next
section), a dedicated third leg was tried: instead of Price-signed ΔIV covering both Mid and Close,
give Close its own driver, Raw ΔIV. This was a clear, confirmed improvement over the 2-way switch
on every dimension tested — see below.

---

### The 17-item pre-Phase-5 experiment list

Once the 2-way switch was working, rather than moving straight to Phase 5, a long list of follow-up
questions was worked through one at a time, evidence-based, on the principle that a cheap backtest
sweep costs nothing even for a 5% chance of improvement. Most of these were narrow "does X beat Y"
checks and don't need individual write-ups here — grouped by kind:

- **Switch-timing questions**: what time should the switch trigger (confirmed 10:00am, the value
  already in use, was correct), and should the entry window start earlier at 9:15 instead of 9:30
  (inconclusive — looked like a real improvement until one single outsized trade on the same
  high-volatility day that has skewed other results was excluded, after which it was a wash; the
  standing 9:30 rule was kept).
- **Which metric drives which leg**: OI Delta was tried in place of ATM Depth Imbalance for the
  Open leg (Depth Imbalance confirmed better), and Volume Delta was tried in place of Price-signed
  ΔIV for the Mid leg (Price-signed ΔIV confirmed better — Volume Delta traded away more than half
  the net for a smaller win-rate gain).
- **The dedicated Close leg** — the 3-way switch itself: giving Close its own driver (Raw ΔIV)
  instead of letting Price-signed ΔIV cover Mid and Close both. **Confirmed improvement**, and the
  new leading candidate: `OptionsScoreThreeWaySwitch` @ 2600/90 — **60.8% win rate, +446.05 net**,
  concentration 11.6%/23.1% (even lower than the 2-way switch's), positive on 7 of 8 days. Beat the
  2-way switch on every dimension.
- **Confirmation-filter designs, mostly rejected**: an all-day Skew Change agreement filter (hurt
  both win rate and net — filtered out winners along with losers); an "early directional
  conviction" filter based on the first 70 minutes of trading (also came in lower on both
  dimensions, no cell in the sweep beat the unfiltered switch); and Max Pain distance as a
  confirmation filter, discussed on its own below because — unlike the other two — it wasn't a
  clean rejection.
- **Concentration and band-width checks**: the linear blend's own concentration was finally
  computed (14.5%/27.5%, close to the switch's own — confirming the blend's weaker net really was
  dilution, not a fluke of one outlier trade), and a wider ATM±3 test on the depth-imbalance metric
  confirmed ATM±2 is a genuine peak rather than an arbitrary stopping point (mentioned above under
  Phase 2).
- **A crossover bar-threshold sweep** on the futures-side dual-moving-average entry mechanism (a
  different way of deciding *when* to trade, tried earlier as the user's own idea — see the futures
  crossover box below) found a new leading config at 1300 bars, 8-fast/30-slow/8-point threshold:
  58.0% win, +273.20 net, beating the previously-adopted 2600-bar config on both dimensions, though
  not yet validated out-of-sample.

**Two findings from this list are worth a closer look:**

**(a) The DTE-interaction finding.** Splitting the 3-way switch's trades by 0-DTE vs. non-0-DTE
revealed something not obvious from the pooled numbers: the Mid leg (Price-signed ΔIV) is actually
*below* 50% win rate specifically on non-0-DTE days (49.1%) but strong on 0-DTE days (59.3%) — and
the Close leg (Raw ΔIV) is the exact mirror image (50.0% and net-negative on 0-DTE, 72.7% on
non-0-DTE). In plain terms: the switch's overall robustness across both kinds of days isn't because
each leg is independently solid on every day type — it's because each leg's weakness on one day
type happens to be covered by the other leg's strength there. That's a real dependency worth
re-checking as more days accumulate, not a red flag on its own (the switch is still net-positive on
both DTE buckets pooled), but it means the switch is doing more compensating than it might appear.

**(b) The Max Pain confirmation filter's adoption.** Unlike the two other confirmation-filter
designs tried, gating the 3-way switch's trades on Max Pain distance agreement wasn't a clean
rejection — it was a genuine trade-off. At the same 2600/90 combo: win rate rose to **64.3%** (up
3.5 points) and every single one of the 8 backtest days stayed above 50% win rate (versus 7 of 8
unfiltered) — a more consistent day-to-day curve. But net fell slightly, to **+426.40** (down about
4.4% from the unfiltered switch's +446.05). By the project's usual strict rule ("adopt only if both
win rate and net improve together"), this wouldn't qualify. It was adopted anyway, as a judgment
call: the DTE-interaction finding above showed the unfiltered switch's robustness comes from two
legs' weaknesses compensating for each other rather than either leg being solid on its own — the
Max Pain filter's extra day-to-day consistency lines up with filtering out exactly the kind of
trades that compensation is thinnest on. For a live paper-trading system, a smoother, more
predictable curve was judged worth a net gap that's well within the noise of an 8-day sample.

---

### Phase 5: the final combination attempt

With both a futures score (`SessionGatedDepthDuration` @ 2600/90, 55.0% win/+144.70 net) and an
options score (`OptionsScoreThreeWaySwitch` @ 2600/90, 60.8% win/+446.05 net) independently locked,
the plan's final step was to combine them into one number. **Four genuinely different ways of
combining them were tried**, all evidence-tested against both parent scores:

1. **DTE-weighted blend** — weight each parent by how close to expiry the day is: 45.1% win,
   +172.75 net. Worse than either parent on win rate.
2. **"Both must agree"** — only trade when futures and options scores agree in sign, futures as the
   primary driver: 51.6% win, +129.75 net.
3. **Options-primary, futures as a confirmation filter**: 52.3% win, +308.00 net.
4. **Session-weighted blend** — weight the two scores differently by time of day (more futures
   weight early, more options weight late): 57.4% win, +379.80 net — the closest of the four, but
   still short of the options score alone on both dimensions.

**None of the four beat the standalone options score on both win rate and net.** A follow-up sweep
around the session-weighted blend's own weights (roughly 125 combinations of Open/Mid/Close weight,
across 5 percentile settings) found one cell that nearly tied on net (+446.10 vs. +446.05) but still
fell slightly short on win rate (60.7% vs. 60.8%) and was fragile — neighboring weight combinations
collapsed to 43–47% win rate, unlike the tighter, more stable band around the original weights. Not
adopted either.

**Verdict: combining dilutes rather than helps — the same lesson as the futures-side blend and the
options-side blend, now confirmed a third time, one level up.** Phase 5's combination line is
closed; no FuturesScore + OptionsScore blend is part of the current best configuration.

A related, separate experiment was also tried and also came up short: the same dual-moving-average
crossover mechanism used on the futures score (a different way of deciding *when* to trade, instead
of a fixed percentile threshold) was generalized to run on the options score too. The best config
found (2600 bars, 12-fast/30-slow/5-point threshold) reached only 62.5% win rate and +153.95 net at
6.0 trades/day — well short of the standing percentile-threshold entry's 64.3% win/+426.40 net. The
likely reason: the 3-way switch already jumps between three different formulas across the trading
day, and smoothing that with a moving average dampens exactly the regime transitions the switch
exists to catch — unlike the futures score's single continuous metric, where the crossover idea
showed more promise. **Percentile-threshold entry stays the standard for the options score;
crossover remains a futures-only idea.**

---

## Where things stand now — ranked

**Final locked config: `OptionsScoreThreeWaySwitchMaxPainConfirmed` @ 2600 bars / 90th percentile.**

In plain words, that name means: a three-way session switch (Depth Imbalance drives trades before
10:00am, Price-signed ΔIV drives the 10:00am–1:30pm window, Raw ΔIV drives the last window before
force-close), with an added Max Pain confirmation gate — a trade only fires if the distance-to-
Max-Pain reading agrees. No blending with the futures score, no moving-average crossover — just
this one options score with this one filter, entering whenever its own reading crosses the 90th
percentile of the day's own readings so far.

**Its real numbers (8 trading days, 2600 bars/90th percentile):** **64.3% win rate, +426.40 net
points, 112 trades**, and — the headline consistency result — **every single one of the 8 backtest
days stayed above 50% win rate.**

| Rank | Strategy | Result | Status |
|---|---|---|---|
| 1 | **OptionsScoreThreeWaySwitchMaxPainConfirmed** @ 2600/90 | 64.3% win, +426.40 pts, 8/8 days ≥50% win | 🟢 **Current leading candidate for the live system** |
| 2 | OptionsScoreThreeWaySwitch (unfiltered) @ 2600/90 | 60.8% win, +446.05 pts, 7/8 days ≥50% win | 🟢 Superseded by #1 — higher net, less consistent day-to-day |
| 3 | OptionsScoreOpenMidSwitch (2-way) @ 2600/90 | 57.1% win, +416.85 pts | 🟡 Superseded by the 3-way switch |
| 4 | FuturesScore: SessionGatedDepthDuration @ 2600/90 | 57.4% win (backtest), +177.25 pts | 🟢 Locked futures baseline — not currently combined with the options score |
| 5 | OptionsScore blend (equal-weight) | 62.3% win, +221.35 pts | 🔴 Confirmed dilution vs. the switch |
| 6 | FuturesScore + OptionsScore, best combination attempt (session-weighted blend) | 57.4% win, +379.80 pts | 🔴 Closest combination try, still short of options-alone |
| 7 | Options-side crossover entry | 62.5% win, +153.95 pts (best combo found) | 🔴 Not adopted — dampens the switch's own regime transitions |

**In plain terms:** this project moved through the futures phase (10 candidates → a 2-metric
session switch with a confirmation filter), then the options phase (8 confirmed standalone metrics
across two rounds of metric-building → a blend that diluted → a 2-way switch that beat it → a 3-way
switch that beat that → a confirmation filter that traded a little net for a lot more day-to-day
consistency), and finally a combination attempt that tried four different ways of merging the
futures and options scores together — all four came up short of the options score standing alone.
**The standalone options score, `OptionsScoreThreeWaySwitchMaxPainConfirmed` @ 2600/90, is the
leading candidate for the live paper-trading system as of today.** It still needs more
out-of-sample days before it's fully trusted — 8 days of backtest data is a real, encouraging
starting point, not a verdict, per this project's own standing rule about treating backtests as
accumulating evidence rather than a one-time test.
