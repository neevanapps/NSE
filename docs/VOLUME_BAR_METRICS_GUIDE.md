# Volume-Bar Metrics — Plain-Language Guide

This is a simple walkthrough of everything we tested on the new volume-bar system. The
[detailed technical log](VOLUME_BAR_FINDINGS.md) has every number and every caveat; this document
is the same work explained in plain words, with real numbers from our own data as examples.

**Update note:** this guide was rewritten after we discovered and fixed a real gap — a
trading-hours rule that was supposed to be enforced but wasn't. That fix changed several of the
numbers below, including which metric looked "best." See the section on it further down; it's
worth reading before the per-metric results, since it's the reason some of these numbers moved.

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

## The 10 individual metrics we tested, one by one

### 1. TrendReversion — *does the market look like it's overextended and due to snap back?*

**How it's worked out:** Look at the net price move over the last 15 bars, divide by how much the
price wiggled around to get there (a straight line up = "1", a jagged sideways path that ends up
higher = a smaller number). Then flip the sign — a very *clean, straight* move is read as a
warning of a reversal, not a "keep going" signal.

**Result under the gated rules:** still inconsistent — some settings look decent (up to ~60% win
rate), others lose money, with no stable pattern across nearby settings.

**Status: Not confirmed.** Unchanged verdict after the gate fix.

---

### 2. FutureCvdNet — *are more people buying or selling the future right now?*

**How it's worked out:** We don't have a real "who bought vs. who sold" feed, so we estimate it: if
a trade happens at or above the midpoint of the current bid/ask, we guess it was a buyer stepping
in; below the midpoint, a seller.

**Result under the gated rules:** still weak — win rate barely clears 50% at its best setting, and
most settings lose money.

**Status: Not confirmed / closed.** Unchanged verdict. Would need a real buyer/seller data feed to
do better, which we don't have.

---

### 3. DepthImbalance — *is there more buying interest or selling interest resting in the order book?*

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

**Status: One of the two strongest standalone metrics.** This is now the anchor of the best
combined strategy we've built (see below).

---

### 4. OrderFlowImbalance (OFI) — *is real buying/selling pressure building or draining from the order book?*

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

### 5. FutureOiBuildupQuadrant — *is money flowing into new positions, or out of old ones, and which direction?*

**How it's worked out:** Combine two things — did price go up or down, and did open interest (the
count of contracts still open) go up or down. Four combinations, each with a classic trading
meaning (new buyers piling in, new sellers piling in, buyers leaving, or sellers covering).

**Result under the gated rules:** still weak and inconsistent, win rate below 50% at most settings.

**Status: Not confirmed.** Unchanged verdict.

---

### 6. VwapDeviation — *is price trading rich or cheap compared to today's average traded price?*

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

### 7. BarDurationUrgency — *how urgently is the market trading right now?*

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

**Status: One of the two strongest standalone metrics**, alongside DepthImbalance — now the
second half of the best combined strategy we've built.

---

### 8. PriceImpact (a "Kyle's lambda" style measure) — *how much does price move per unit of volume traded?*

**How it's worked out:** Price change during the bar, divided by the bar's own volume. In theory
this measures how "thin" or "liquid" the market is right now.

**Result under the gated rules:** still weak, win rate never above 50%.

**Status: Not confirmed.** Unchanged verdict — as suspected from the start, dividing by volume
barely matters when bars are built to a near-fixed volume size, so this ends up being a diluted
version of plain price movement.

---

### 9. TopOfBookImbalance (TOB) — *who's winning the battle right at the best price, ignoring the rest of the book?*

**How it's worked out:** Same idea as DepthImbalance (#3), but instead of looking at the top 5
price levels, we only look at the very best bid and best ask.

**Real example**, same live snapshot as before: best bid had 520 lots waiting, best ask had only
65 → `(520 − 65) / (520 + 65) ≈ +0.78` — strongly "buyers winning at the top." Notice this is the
**opposite sign** from DepthImbalance's reading of the exact same moment (-0.17)! The touch (best
price) said bullish; the full book said bearish.

**Result under the gated rules:** best setting shifted to 2600/80. About 20 trades/day, **54.7%
win rate, +124.60 points** — a large jump in trade count from before, now near the upper edge of
what we consider a workable trade frequency.

**What stands out:** this is now the **most consistent metric of everything we've tested** —
54.9% win rate on expiry days vs. 54.7% on ordinary days (almost identical), and positive in every
part of the trading day. It doesn't have DepthImbalance's or BarDurationUrgency's raw edge size,
but it's the most reliably "not fooling itself" of the group.

**Status: Solid secondary candidate.** Not the headline metric, but the one we'd trust most if we
needed a single number that behaves the same way no matter the day or the hour.

---

### 10. TobDepthDivergence — *when the best price and the full order book disagree, which one should we trust?*

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
single rule you forgot to apply can change a "best result" — and why we now double-check every
promising number against the rules that actually govern live trading before trusting it.

---

## What we built after finding all this

### The composite score — a weighted blend, tried but came up short

Once we had 5 metrics still worth considering (DepthImbalance, BarDurationUrgency,
TopOfBookImbalance, OrderFlowImbalance, and TobDepthDivergence), the natural next step was
combining them into one score, weighted by how much we trust each one. We built exactly that —
each metric contributes a share based on its own edge size, how spread-out its profit is, and how
consistently it performs across expiry vs. ordinary days.

**It didn't work as hoped.** The blended score came in at only 49.6% win rate and +80.95 points —
*worse* than simply trading DepthImbalance on its own (56.1% win rate, +151.50 points). The likely
reason: DepthImbalance and BarDurationUrgency make their money at *opposite* times of day (see
#3 and #7 above). Averaging their scores together every moment waters both signals down instead of
combining them — like trying to average "turn left" and "turn right" into a straight line.

### The session-gated switch — our current best idea

Instead of blending, we tried simply **switching** which metric drives the trade depending on the
time of day: DepthImbalance's own score in the first 30 minutes (9:30–10:00am, its strong window),
BarDurationUrgency's own score for the rest of the day (its strong window). Never averaging them —
always exactly one or the other.

**This worked.** Result: about 10 trades/day, **57.4% win rate, +177.25 points** — the best win
rate and best profit of anything we've tested, beating both individual metrics and the blended
composite.

**One honest caveat:** almost all of that profit (97.7%) came from a single very volatile day
(2026-09-15). Take away that one day, and the rest are close to flat. That's not the same problem
as "one lucky trade" (the trades themselves are spread out fine, 5 of 7 days were profitable), but
it does mean this result still needs to prove itself on more real days before we fully trust the
size of the edge.

---

## Where things stand now — ranked

| Rank | Strategy | Result (gated, real rules) | Status |
|---|---|---|---|
| 1 | **Session-gated switch** (DepthImbalance in the open, BarDurationUrgency after) | 57.4% win, +177.25 pts | 🟢 **Current best — combination of #3 and #7** |
| 2 | **DepthImbalance** (#3) | 56.1% win, +151.50 pts | 🟢 Strongest standalone metric |
| 3 | **BarDurationUrgency** (#7) | 54.9% win, +119.60 pts | 🟢 Second-strongest standalone, opposite time-of-day pattern to #3 |
| 4 | **TopOfBookImbalance** (#9) | 54.7% win, +124.60 pts | 🟡 Most consistent metric, modest edge size |
| 5 | TobDepthDivergence (#10) | 45.1% win, +129.30 pts | 🔴 Was "strongest," collapsed once trading hours were enforced correctly |
| 6 | OrderFlowImbalance (#4) | 44.2% win, +289.40 pts | 🔴 Highest raw profit, but most fragile — below-50% win rate, expiry-day dependent |
| 7 | TrendReversion (#1) | inconsistent | 🔴 Not confirmed |
| 8 | FutureOiBuildupQuadrant (#5) | inconsistent | 🔴 Not confirmed |
| 9 | PriceImpact (#8) | weak | 🔴 Not confirmed |
| 10 | FutureCvdNet (#2) | weak | 🔴 Not confirmed |
| 11 | VwapDeviation (#6) | win rate never above 28.6% anywhere | 🔴 Confirmed weak, propped up by 2 lucky trades |

**In plain terms:** out of 10 individual metrics, 2 (DepthImbalance and BarDurationUrgency) hold up
as genuinely strong, and combining them the *right* way — switching by time of day instead of
averaging — beats either one alone. Our earlier "winner," TobDepthDivergence, turned out to be an
artifact of a trading-hours rule we weren't actually enforcing, which is exactly the kind of
mistake this whole checking process (expiry-day splits, time-of-day splits, concentration checks,
and — this time — confirming the rules we're simulating actually match the rules we're required to
follow) is designed to catch before it costs anything real. The next real test for the session-gated
switch is simple: does it keep working on trading days we haven't looked at yet.
