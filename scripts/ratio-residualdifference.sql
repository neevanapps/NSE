-- 2026-09-13: ratio-composite metric 3, ResidualDifference -- confirmed exact formula from
-- ComputeResidualDifference (LiveFeatureEngine.cs:2470-2538): per leg, predictedChange =
-- Delta*dS + 0.5*Gamma*dS^2 + Theta*elapsedDays + Vega*dVol (full 2nd-order Taylor); residual =
-- actualChange - predictedChange; residualDifference = callResidual - putResidual. ATM strike
-- only, cadence-to-cadence, guarded by strike-identity (no roll between ticks). "vol" (the shared
-- ATM reference vol) proxied as avg(ATM call IV, ATM put IV) per cadence, same proxy used for
-- IvSkew/StraddleRichness. Spot proxied for underlying S, same approximation as StraddleRichness.
-- Summed over trailing 5/15-min windows (same sequencing as StraddleRichness), ThisWeek only.
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
atm_legs AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."StrikePrice",
        s."MarkPrice", s."Delta", s."Gamma", s."ThetaPerDay", s."Vega", s."ImpliedVolatility"
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate" AND wm.week_rank = 1
    WHERE s."StrikeOffsetFromAtm" = 0
),
grouped AS (
    SELECT *, COUNT("MarkPrice") OVER (PARTITION BY "AsOfDate", "OptionType" ORDER BY "Timestamp") AS grp
    FROM atm_legs
),
filled AS (
    SELECT "AsOfDate", "Timestamp", "OptionType", "StrikePrice",
        FIRST_VALUE("MarkPrice") OVER (PARTITION BY "AsOfDate", "OptionType", grp ORDER BY "Timestamp") AS mark_f,
        FIRST_VALUE("Delta") OVER (PARTITION BY "AsOfDate", "OptionType", grp ORDER BY "Timestamp") AS delta_f,
        FIRST_VALUE("Gamma") OVER (PARTITION BY "AsOfDate", "OptionType", grp ORDER BY "Timestamp") AS gamma_f,
        FIRST_VALUE("ThetaPerDay") OVER (PARTITION BY "AsOfDate", "OptionType", grp ORDER BY "Timestamp") AS theta_f,
        FIRST_VALUE("Vega") OVER (PARTITION BY "AsOfDate", "OptionType", grp ORDER BY "Timestamp") AS vega_f,
        FIRST_VALUE("ImpliedVolatility") OVER (PARTITION BY "AsOfDate", "OptionType", grp ORDER BY "Timestamp") AS iv_f
    FROM grouped
),
legs AS (
    SELECT c."AsOfDate", c."Timestamp", c."StrikePrice" AS call_strike, p."StrikePrice" AS put_strike,
        c.mark_f AS call_mid, p.mark_f AS put_mid,
        c.delta_f AS call_delta, p.delta_f AS put_delta,
        c.gamma_f AS call_gamma, p.gamma_f AS put_gamma,
        c.theta_f AS call_theta, p.theta_f AS put_theta,
        c.vega_f AS call_vega, p.vega_f AS put_vega,
        (c.iv_f + p.iv_f) / 2.0 AS atm_vol
    FROM filled c
    JOIN filled p ON p."AsOfDate" = c."AsOfDate" AND p."Timestamp" = c."Timestamp" AND p."OptionType" = 2
    WHERE c."OptionType" = 1
),
spot_grouped AS (
    SELECT "AsOfDate", "Timestamp",
        COUNT("SpotCloseFromLastCadence") OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp") AS spot_grp,
        "SpotCloseFromLastCadence"
    FROM "CadenceContexts"
),
spot_filled AS (
    SELECT "AsOfDate", "Timestamp",
        FIRST_VALUE("SpotCloseFromLastCadence") OVER (PARTITION BY "AsOfDate", spot_grp ORDER BY "Timestamp") AS spot_ffilled
    FROM spot_grouped
),
legs_with_spot AS (
    SELECT l.*, sf.spot_ffilled
    FROM legs l
    JOIN spot_filled sf ON sf."AsOfDate" = l."AsOfDate" AND sf."Timestamp" = l."Timestamp"
),
with_prev AS (
    SELECT *,
        LAG(call_strike) OVER w AS prev_call_strike,
        LAG(call_mid) OVER w AS prev_call_mid, LAG(put_mid) OVER w AS prev_put_mid,
        LAG(call_delta) OVER w AS prev_call_delta, LAG(put_delta) OVER w AS prev_put_delta,
        LAG(call_gamma) OVER w AS prev_call_gamma, LAG(put_gamma) OVER w AS prev_put_gamma,
        LAG(call_theta) OVER w AS prev_call_theta, LAG(put_theta) OVER w AS prev_put_theta,
        LAG(call_vega) OVER w AS prev_call_vega, LAG(put_vega) OVER w AS prev_put_vega,
        LAG(atm_vol) OVER w AS prev_vol,
        LAG(spot_ffilled) OVER w AS prev_spot,
        LAG("Timestamp") OVER w AS prev_ts
    FROM legs_with_spot
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
),
residual_tick AS (
    SELECT "AsOfDate", "Timestamp",
        CASE WHEN call_strike = prev_call_strike AND prev_call_mid IS NOT NULL AND prev_put_mid IS NOT NULL
                  AND prev_call_delta IS NOT NULL AND prev_put_delta IS NOT NULL
                  AND prev_call_gamma IS NOT NULL AND prev_put_gamma IS NOT NULL
                  AND prev_call_theta IS NOT NULL AND prev_put_theta IS NOT NULL
                  AND prev_call_vega IS NOT NULL AND prev_put_vega IS NOT NULL
                  AND prev_vol IS NOT NULL AND prev_spot IS NOT NULL
             THEN
                ((call_mid - prev_call_mid) - (prev_call_delta*(spot_ffilled-prev_spot) + 0.5*prev_call_gamma*(spot_ffilled-prev_spot)*(spot_ffilled-prev_spot) + prev_call_theta*(EXTRACT(EPOCH FROM ("Timestamp"-prev_ts))/86400.0) + prev_call_vega*(atm_vol-prev_vol)))
                -
                ((put_mid - prev_put_mid) - (prev_put_delta*(spot_ffilled-prev_spot) + 0.5*prev_put_gamma*(spot_ffilled-prev_spot)*(spot_ffilled-prev_spot) + prev_put_theta*(EXTRACT(EPOCH FROM ("Timestamp"-prev_ts))/86400.0) + prev_put_vega*(atm_vol-prev_vol)))
        END AS residual_diff
    FROM with_prev
),
residual_windowed AS (
    SELECT "AsOfDate", "Timestamp",
        SUM(residual_diff) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '5 minutes' PRECEDING AND CURRENT ROW) AS sum_5m,
        SUM(residual_diff) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '15 minutes' PRECEDING AND CURRENT ROW) AS sum_15m
    FROM residual_tick
),
all_strikes2 AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."StrikePrice", s."StrikeOffsetFromAtm", s."MarkPrice"
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate" AND wm.week_rank = 1
),
grouped2 AS (
    SELECT *, COUNT("MarkPrice") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice" ORDER BY "Timestamp") AS grp
    FROM all_strikes2
),
filled2 AS (
    SELECT "AsOfDate", "Timestamp", "OptionType", "StrikePrice", "StrikeOffsetFromAtm",
        FIRST_VALUE("MarkPrice") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice", grp ORDER BY "Timestamp") AS mark_ffilled
    FROM grouped2
),
with_lookahead AS (
    SELECT *,
        LEAD(mark_ffilled, 20) OVER w AS price_5m_later, LEAD("Timestamp", 20) OVER w AS ts_5m_later,
        LEAD(mark_ffilled, 60) OVER w AS price_15m_later, LEAD("Timestamp", 60) OVER w AS ts_15m_later,
        LEAD(mark_ffilled, 120) OVER w AS price_30m_later, LEAD("Timestamp", 120) OVER w AS ts_30m_later
    FROM filled2
    WINDOW w AS (PARTITION BY "AsOfDate", "OptionType", "StrikePrice" ORDER BY "Timestamp")
),
option_changes AS (
    SELECT "AsOfDate", "Timestamp", "OptionType",
        CASE WHEN ts_5m_later - "Timestamp" BETWEEN INTERVAL '4 minutes 30 seconds' AND INTERVAL '5 minutes 30 seconds'
             THEN price_5m_later - mark_ffilled END AS opt_fwd_5m,
        CASE WHEN ts_15m_later - "Timestamp" BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN price_15m_later - mark_ffilled END AS opt_fwd_15m,
        CASE WHEN ts_30m_later - "Timestamp" BETWEEN INTERVAL '29 minutes 30 seconds' AND INTERVAL '30 minutes 30 seconds'
             THEN price_30m_later - mark_ffilled END AS opt_fwd_30m
    FROM with_lookahead
    WHERE "StrikeOffsetFromAtm" = 0
),
future_grouped AS (
    SELECT "AsOfDate", "Timestamp",
        COUNT("FutureCloseFromLastCadence") OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp") AS future_grp,
        "FutureCloseFromLastCadence"
    FROM "CadenceContexts"
),
future_filled AS (
    SELECT "AsOfDate", "Timestamp",
        FIRST_VALUE("FutureCloseFromLastCadence") OVER (PARTITION BY "AsOfDate", future_grp ORDER BY "Timestamp") AS future_ffilled
    FROM future_grouped
),
future_lead AS (
    SELECT *,
        LEAD(future_ffilled, 20) OVER w AS future_5m, LEAD("Timestamp", 20) OVER w AS ts_5m,
        LEAD(future_ffilled, 60) OVER w AS future_15m, LEAD("Timestamp", 60) OVER w AS ts_15m,
        LEAD(future_ffilled, 120) OVER w AS future_30m, LEAD("Timestamp", 120) OVER w AS ts_30m
    FROM future_filled
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
),
future_changes AS (
    SELECT "AsOfDate", "Timestamp",
        CASE WHEN ts_5m - "Timestamp" BETWEEN INTERVAL '4 minutes 30 seconds' AND INTERVAL '5 minutes 30 seconds'
             THEN future_5m - future_ffilled END AS future_fwd_5m,
        CASE WHEN ts_15m - "Timestamp" BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN future_15m - future_ffilled END AS future_fwd_15m,
        CASE WHEN ts_30m - "Timestamp" BETWEEN INTERVAL '29 minutes 30 seconds' AND INTERVAL '30 minutes 30 seconds'
             THEN future_30m - future_ffilled END AS future_fwd_30m
    FROM future_lead
)
SELECT r."AsOfDate",
    COUNT(r.sum_5m) AS n,
    ROUND(CORR(r.sum_5m, oc.opt_fwd_5m)::numeric,3) AS sum5_opt5, ROUND(CORR(r.sum_5m, oc.opt_fwd_15m)::numeric,3) AS sum5_opt15, ROUND(CORR(r.sum_5m, oc.opt_fwd_30m)::numeric,3) AS sum5_opt30,
    ROUND(CORR(r.sum_5m, fc.future_fwd_5m)::numeric,3) AS sum5_fut5, ROUND(CORR(r.sum_5m, fc.future_fwd_15m)::numeric,3) AS sum5_fut15, ROUND(CORR(r.sum_5m, fc.future_fwd_30m)::numeric,3) AS sum5_fut30,
    ROUND(CORR(r.sum_15m, oc.opt_fwd_15m)::numeric,3) AS sum15_opt15, ROUND(CORR(r.sum_15m, fc.future_fwd_15m)::numeric,3) AS sum15_fut15, ROUND(CORR(r.sum_15m, fc.future_fwd_30m)::numeric,3) AS sum15_fut30
FROM residual_windowed r
JOIN option_changes oc ON oc."AsOfDate" = r."AsOfDate" AND oc."Timestamp" = r."Timestamp" AND oc."OptionType" = 1
JOIN future_changes fc ON fc."AsOfDate" = r."AsOfDate" AND fc."Timestamp" = r."Timestamp"
GROUP BY 1
ORDER BY 1;
