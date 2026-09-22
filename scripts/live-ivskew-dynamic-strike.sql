-- 2026-09-13: live IvSkew as actually implemented -- putIv - callIv at a DYNAMIC strike pair
-- (spot +- expectedMove, expectedMove = spot*atmVol*sqrt(t)), now testable after the schema
-- widening to ATM+-10. atmVol proxied as the average of ATM (offset=0) call+put IV per cadence
-- (live solves one shared ATM reference vol used across GEX/Vanna/Charm/IvSkew; this average is
-- the closest available proxy, not a byte-identical replication -- flagged explicitly).
-- Strike spacing = 50 points (confirmed from real data). ThisWeek only, per day.
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
all_strikes AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."StrikeOffsetFromAtm",
        s."ImpliedVolatility", s."DaysToExpiry"
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate" AND wm.week_rank = 1
),
grouped AS (
    SELECT *, COUNT("ImpliedVolatility") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikeOffsetFromAtm" ORDER BY "Timestamp") AS grp
    FROM all_strikes
),
filled AS (
    SELECT "AsOfDate", "Timestamp", "OptionType", "StrikeOffsetFromAtm", "DaysToExpiry",
        FIRST_VALUE("ImpliedVolatility") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikeOffsetFromAtm", grp ORDER BY "Timestamp") AS iv_f
    FROM grouped
),
atm_vol AS (
    SELECT "AsOfDate", "Timestamp", AVG(iv_f) AS atm_vol, MAX("DaysToExpiry") AS dte
    FROM filled
    WHERE "StrikeOffsetFromAtm" = 0
    GROUP BY 1,2
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
target AS (
    SELECT av."AsOfDate", av."Timestamp",
        ROUND((sf.spot_ffilled::float * av.atm_vol * SQRT(GREATEST(av.dte,1)::float / 365.0)) / 50.0) AS target_offset
    FROM atm_vol av
    JOIN spot_filled sf ON sf."AsOfDate" = av."AsOfDate" AND sf."Timestamp" = av."Timestamp"
    WHERE av.atm_vol IS NOT NULL AND sf.spot_ffilled IS NOT NULL
),
skew_dynamic AS (
    SELECT t."AsOfDate", t."Timestamp", t.target_offset,
        pc.iv_f IS NOT NULL AS has_call, pp.iv_f IS NOT NULL AS has_put,
        (pp.iv_f - pc.iv_f) AS skew_level
    FROM target t
    LEFT JOIN filled pc ON pc."AsOfDate" = t."AsOfDate" AND pc."Timestamp" = t."Timestamp" AND pc."OptionType" = 1 AND pc."StrikeOffsetFromAtm" = t.target_offset
    LEFT JOIN filled pp ON pp."AsOfDate" = t."AsOfDate" AND pp."Timestamp" = t."Timestamp" AND pp."OptionType" = 2 AND pp."StrikeOffsetFromAtm" = -t.target_offset
),
skew_with_delta AS (
    SELECT *, skew_level - LAG(skew_level, 20) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp") AS skew_delta_5m
    FROM skew_dynamic
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
SELECT sd."AsOfDate",
    COUNT(*) FILTER (WHERE sd.skew_level IS NOT NULL) AS n_resolved,
    ROUND(AVG(sd.target_offset)::numeric,2) AS avg_target_offset,
    ROUND(MAX(ABS(sd.target_offset))::numeric,0) AS max_abs_offset,
    ROUND(CORR(sd.skew_level, oc.opt_fwd_5m)::numeric,3) AS lvl_opt5, ROUND(CORR(sd.skew_level, oc.opt_fwd_15m)::numeric,3) AS lvl_opt15, ROUND(CORR(sd.skew_level, oc.opt_fwd_30m)::numeric,3) AS lvl_opt30,
    ROUND(CORR(sd.skew_level, fc.future_fwd_5m)::numeric,3) AS lvl_fut5, ROUND(CORR(sd.skew_level, fc.future_fwd_15m)::numeric,3) AS lvl_fut15, ROUND(CORR(sd.skew_level, fc.future_fwd_30m)::numeric,3) AS lvl_fut30,
    ROUND(CORR(sd.skew_delta_5m, fc.future_fwd_5m)::numeric,3) AS d_fut5, ROUND(CORR(sd.skew_delta_5m, fc.future_fwd_15m)::numeric,3) AS d_fut15
FROM skew_with_delta sd
JOIN option_changes oc ON oc."AsOfDate" = sd."AsOfDate" AND oc."Timestamp" = sd."Timestamp" AND oc."OptionType" = 1
JOIN future_changes fc ON fc."AsOfDate" = sd."AsOfDate" AND fc."Timestamp" = sd."Timestamp"
GROUP BY 1
ORDER BY 1;
