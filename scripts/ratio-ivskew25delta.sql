-- 2026-09-13: ratio-composite metric 4, IvSkew25Delta -- confirmed exact formula from
-- ComputeIvSkewRatio25Delta/InterpolateIvAtDelta25 (LiveFeatureEngine.cs:2071-2161): true
-- 25-delta smile interpolation between a bracketing pair. Approximated here as the SINGLE
-- nearest-to-25-delta strike per side (using each strike's own persisted, individually-solved
-- Delta -- not live's cheap shared-vol first-pass estimate), no bracket interpolation -- a real
-- simplification, per user's own choice. putIv/callIv (ratio, matching live's exact orientation).
-- Full chain (ATM+-10), fresh per-cadence selection (no continuity guard needed -- this is a
-- level, not a residual). ThisWeek only.
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
all_strikes AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."StrikePrice", s."Delta", s."ImpliedVolatility"
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate" AND wm.week_rank = 1
    WHERE s."StrikeOffsetFromAtm" BETWEEN -10 AND 10
),
grouped AS (
    SELECT *, COUNT("Delta") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice" ORDER BY "Timestamp") AS grp
    FROM all_strikes
),
filled AS (
    SELECT "AsOfDate", "Timestamp", "OptionType", "StrikePrice",
        FIRST_VALUE("Delta") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice", grp ORDER BY "Timestamp") AS delta_f,
        FIRST_VALUE("ImpliedVolatility") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice", grp ORDER BY "Timestamp") AS iv_f
    FROM grouped
),
ranked AS (
    SELECT *,
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate", "Timestamp", "OptionType" ORDER BY ABS(ABS(delta_f) - 0.25)) AS rn
    FROM filled
    WHERE delta_f IS NOT NULL AND iv_f IS NOT NULL
),
nearest_25d AS (
    SELECT "AsOfDate", "Timestamp", "OptionType", iv_f
    FROM ranked WHERE rn = 1
),
skew AS (
    SELECT c."AsOfDate", c."Timestamp",
        CASE WHEN c.iv_f > 0 THEN p.iv_f / c.iv_f END AS raw_level
    FROM nearest_25d c
    JOIN nearest_25d p ON p."AsOfDate" = c."AsOfDate" AND p."Timestamp" = c."Timestamp" AND p."OptionType" = 2
    WHERE c."OptionType" = 1
),
skew_with_delta AS (
    SELECT *, raw_level - LAG(raw_level, 20) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp") AS raw_delta_5m
    FROM skew
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
SELECT s."AsOfDate",
    COUNT(s.raw_level) AS n,
    ROUND(CORR(s.raw_level, oc.opt_fwd_5m)::numeric,3) AS raw_opt5, ROUND(CORR(s.raw_level, oc.opt_fwd_15m)::numeric,3) AS raw_opt15, ROUND(CORR(s.raw_level, oc.opt_fwd_30m)::numeric,3) AS raw_opt30,
    ROUND(CORR(s.raw_level, fc.future_fwd_5m)::numeric,3) AS raw_fut5, ROUND(CORR(s.raw_level, fc.future_fwd_15m)::numeric,3) AS raw_fut15, ROUND(CORR(s.raw_level, fc.future_fwd_30m)::numeric,3) AS raw_fut30,
    ROUND(CORR(s.raw_delta_5m, fc.future_fwd_5m)::numeric,3) AS rawD_fut5, ROUND(CORR(s.raw_delta_5m, fc.future_fwd_15m)::numeric,3) AS rawD_fut15
FROM skew_with_delta s
JOIN option_changes oc ON oc."AsOfDate" = s."AsOfDate" AND oc."Timestamp" = s."Timestamp" AND oc."OptionType" = 1
JOIN future_changes fc ON fc."AsOfDate" = s."AsOfDate" AND fc."Timestamp" = s."Timestamp"
GROUP BY 1
ORDER BY 1;
