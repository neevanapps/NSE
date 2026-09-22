-- 2026-09-13: live StraddleRichness -- exact formula confirmed from source (LiveFeatureEngine.cs:
-- 2400-2449): richness = actualChange - (prevNetDelta*(S_now-S_prev) + prevThetaPerDay*elapsedDays),
-- ATM straddle (call+put), computed cadence-to-cadence, ONLY when the ATM strike hasn't rolled
-- between the two ticks (_previousStraddleStrike == atmStrike guard, replicated exactly via a
-- StrikePrice LAG comparison here). Spot used as a proxy for synthetic S in the delta term (not
-- persisted per-cadence as its own column; multiplied by a near-zero straddle net delta, so the
-- approximation's impact should be small -- flagged, not hidden). Per-strike own-IV Delta/Theta
-- (version A, consistent with today's GEX/Vanna/Charm approach). Summed over trailing 5/15-min
-- windows first, per user's request, then correlated against forward price.
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
atm_legs AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."StrikePrice",
        s."MarkPrice", s."Delta", s."ThetaPerDay"
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate" AND wm.week_rank = 1
    WHERE s."StrikeOffsetFromAtm" = 0
),
grouped AS (
    SELECT *, COUNT("MarkPrice") OVER (PARTITION BY "AsOfDate", "OptionType" ORDER BY "Timestamp") AS mgrp,
        COUNT("Delta") OVER (PARTITION BY "AsOfDate", "OptionType" ORDER BY "Timestamp") AS ggrp
    FROM atm_legs
),
filled AS (
    SELECT "AsOfDate", "Timestamp", "OptionType", "StrikePrice",
        FIRST_VALUE("MarkPrice") OVER (PARTITION BY "AsOfDate", "OptionType", mgrp ORDER BY "Timestamp") AS mark_f,
        FIRST_VALUE("Delta") OVER (PARTITION BY "AsOfDate", "OptionType", ggrp ORDER BY "Timestamp") AS delta_f,
        FIRST_VALUE("ThetaPerDay") OVER (PARTITION BY "AsOfDate", "OptionType", ggrp ORDER BY "Timestamp") AS theta_f
    FROM grouped
),
straddle AS (
    SELECT c."AsOfDate", c."Timestamp", c."StrikePrice" AS call_strike, p."StrikePrice" AS put_strike,
        (c.mark_f + p.mark_f) AS straddle_mid,
        (c.delta_f + p.delta_f) AS net_delta,
        (c.theta_f + p.theta_f) AS theta_per_day
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
straddle_with_spot AS (
    SELECT st.*, sf.spot_ffilled
    FROM straddle st
    JOIN spot_filled sf ON sf."AsOfDate" = st."AsOfDate" AND sf."Timestamp" = st."Timestamp"
),
with_prev AS (
    SELECT *,
        LAG(call_strike) OVER w AS prev_call_strike,
        LAG(straddle_mid) OVER w AS prev_mid,
        LAG(net_delta) OVER w AS prev_delta,
        LAG(theta_per_day) OVER w AS prev_theta,
        LAG(spot_ffilled) OVER w AS prev_spot,
        LAG("Timestamp") OVER w AS prev_ts
    FROM straddle_with_spot
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
),
richness_tick AS (
    SELECT "AsOfDate", "Timestamp",
        CASE WHEN call_strike = prev_call_strike AND prev_mid IS NOT NULL AND prev_delta IS NOT NULL AND prev_theta IS NOT NULL AND prev_spot IS NOT NULL
             THEN (straddle_mid - prev_mid) - (prev_delta * (spot_ffilled - prev_spot) + prev_theta * (EXTRACT(EPOCH FROM ("Timestamp" - prev_ts)) / 86400.0))
        END AS richness
    FROM with_prev
),
richness_windowed AS (
    SELECT "AsOfDate", "Timestamp",
        SUM(richness) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '5 minutes' PRECEDING AND CURRENT ROW) AS richness_sum_5m,
        SUM(richness) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '15 minutes' PRECEDING AND CURRENT ROW) AS richness_sum_15m
    FROM richness_tick
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
    COUNT(r.richness_sum_5m) AS n,
    ROUND(CORR(r.richness_sum_5m, oc.opt_fwd_5m)::numeric,3) AS sum5_opt5, ROUND(CORR(r.richness_sum_5m, oc.opt_fwd_15m)::numeric,3) AS sum5_opt15, ROUND(CORR(r.richness_sum_5m, oc.opt_fwd_30m)::numeric,3) AS sum5_opt30,
    ROUND(CORR(r.richness_sum_5m, fc.future_fwd_5m)::numeric,3) AS sum5_fut5, ROUND(CORR(r.richness_sum_5m, fc.future_fwd_15m)::numeric,3) AS sum5_fut15, ROUND(CORR(r.richness_sum_5m, fc.future_fwd_30m)::numeric,3) AS sum5_fut30,
    ROUND(CORR(r.richness_sum_15m, oc.opt_fwd_15m)::numeric,3) AS sum15_opt15, ROUND(CORR(r.richness_sum_15m, oc.opt_fwd_30m)::numeric,3) AS sum15_opt30,
    ROUND(CORR(r.richness_sum_15m, fc.future_fwd_15m)::numeric,3) AS sum15_fut15, ROUND(CORR(r.richness_sum_15m, fc.future_fwd_30m)::numeric,3) AS sum15_fut30
FROM richness_windowed r
JOIN option_changes oc ON oc."AsOfDate" = r."AsOfDate" AND oc."Timestamp" = r."Timestamp" AND oc."OptionType" = 1
JOIN future_changes fc ON fc."AsOfDate" = r."AsOfDate" AND fc."Timestamp" = r."Timestamp"
GROUP BY 1
ORDER BY 1;
