-- Put/Call price-tracking residual analysis (discussed 2026-09-07, run 2026-09-09 evening --
-- see fix plan dazzling-orbiting-whistle.md's "Research thread" section for the full background).
--
-- Question: does an option's actual cadence-to-cadence price change deviate from what its own
-- Delta/Gamma/Theta predict, in a way that's a real, repeating pattern (e.g. price-impact-and-
-- reversion from order flow) rather than one day's noise? The original one-day check (07 Sep, per
-- the discussion -- though that date isn't actually present in this copy's score_snapshots; the
-- earliest available session here is 04 Sep) found a lag-1 residual autocorrelation of -0.34.
-- This extends the same method across every full session in the local copy (04/08/09 Sep) to see
-- if that holds up as a stable pattern.
--
-- Method: predicted_change = Delta*dSpot + 0.5*Gamma*dSpot^2 + ThetaPerDay*dt_days (a proper
-- second-order Taylor expansion, not Delta+Theta alone -- Gamma is included deliberately: for a
-- non-infinitesimal spot move, leaving it out would show a "residual" that's just convexity
-- error, not signal). residual = actual MarkPriceDelta - predicted_change. Vega/IV changes are
-- deliberately NOT included in the prediction, so they end up inside the residual along with any
-- genuine order-flow effect -- this analysis can't yet tell those two apart (a real next step:
-- check whether the residual correlates with the strike's own ImpliedVolatility having moved).
--
-- Restricted to the persisted ATM+/-2 band (strike_snapshots' own scope already), and to normal
-- 5-60s cadence gaps only (excludes reconnect/gap cadences, where a stale price comparison would
-- masquerade as a huge, meaningless "residual"). Partitioned by (Token, ist_date) throughout so
-- no lag/window calculation ever crosses a session boundary -- the same overnight-gap discipline
-- the codebase's own WelfordRollingWindow should have (see audit finding F22) applied here too.

WITH cadence AS (
    SELECT
        ss."ComputedAt", ss."Token", ss."OptionType", ss."StrikePrice",
        date(ss."ComputedAt" AT TIME ZONE 'Asia/Kolkata') AS ist_date,
        ss."MarkPriceDelta", ss."Delta", ss."Gamma", ss."ThetaPerDay",
        sc."SpotPrice"
    FROM strike_snapshots ss
    JOIN score_snapshots sc ON sc."ComputedAt" = ss."ComputedAt"
    WHERE ss."Delta" IS NOT NULL AND ss."Gamma" IS NOT NULL AND ss."ThetaPerDay" IS NOT NULL
      AND ss."MarkPriceDelta" IS NOT NULL AND sc."SpotPrice" IS NOT NULL
),
with_lag AS (
    SELECT *,
        LAG("ComputedAt") OVER w AS prev_at,
        "SpotPrice" - LAG("SpotPrice") OVER w AS spot_change
    FROM cadence
    WINDOW w AS (PARTITION BY "Token", ist_date ORDER BY "ComputedAt")
),
residuals AS (
    SELECT *,
        EXTRACT(EPOCH FROM ("ComputedAt" - prev_at)) AS gap_seconds,
        "MarkPriceDelta" - (
            "Delta" * spot_change
            + 0.5 * "Gamma" * spot_change * spot_change
            + "ThetaPerDay" * (EXTRACT(EPOCH FROM ("ComputedAt" - prev_at)) / 86400.0)
        ) AS residual
    FROM with_lag
    WHERE prev_at IS NOT NULL
),
clean AS (
    SELECT * FROM residuals WHERE gap_seconds BETWEEN 5 AND 60
),
with_residual_lag AS (
    SELECT *,
        LAG(residual) OVER (PARTITION BY "Token", ist_date ORDER BY "ComputedAt") AS prev_residual
    FROM clean
)
SELECT
    ist_date::text AS session,
    count(*) AS n,
    round(stddev(residual)::numeric, 4) AS residual_stddev,
    -- Sanity check: should be near 0 if Delta+Gamma genuinely captured the spot relationship --
    -- a strongly nonzero value here would mean the decomposition itself is off, not that
    -- something interesting about order flow was found.
    round(corr(residual, spot_change)::numeric, 4) AS residual_vs_spot_sanity,
    round(corr(residual, prev_residual)::numeric, 4) AS lag1_autocorrelation
FROM with_residual_lag
GROUP BY ist_date
UNION ALL
SELECT
    'ALL SESSIONS POOLED',
    count(*),
    round(stddev(residual)::numeric, 4),
    round(corr(residual, spot_change)::numeric, 4),
    round(corr(residual, prev_residual)::numeric, 4)
FROM with_residual_lag
ORDER BY 1;
