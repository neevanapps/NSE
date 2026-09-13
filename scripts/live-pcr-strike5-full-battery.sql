-- 2026-09-13: live "Pcr" (putOI/callOI raw ratio, ATM+-2 = Strike5) -- first direct test.
-- Mathematically the reciprocal of PCR-OI's own log(CallOi/PutOi), so this isolates two questions
-- PCR-OI's own result didn't answer: (a) does the raw (un-logged) ratio behave differently from
-- log-transformed, and (b) does the ratio's own bar-to-bar CHANGE correlate with price directly
-- (never tested for PCR-OI either -- only checked against OI-diff for redundancy).
-- Strike5 band only (matches live's ATM+-2 exactly), ThisWeek only (NextWeek dropped as a
-- standing scope per 2026-09-13 decision), 5/15/30-min horizons, both option and future targets.
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
all_strikes AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."StrikePrice", s."StrikeOffsetFromAtm", s."MarkPrice",
        CASE WHEN wm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate"
),
grouped AS (
    SELECT *, COUNT("MarkPrice") OVER (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice" ORDER BY "Timestamp") AS grp
    FROM all_strikes
),
filled AS (
    SELECT "AsOfDate", "Timestamp", week_label, "OptionType", "StrikePrice", "StrikeOffsetFromAtm",
        FIRST_VALUE("MarkPrice") OVER (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice", grp ORDER BY "Timestamp") AS mark_ffilled
    FROM grouped
),
with_lookahead AS (
    SELECT *,
        LEAD(mark_ffilled, 20) OVER w AS price_5m_later, LEAD("Timestamp", 20) OVER w AS ts_5m_later,
        LEAD(mark_ffilled, 60) OVER w AS price_15m_later, LEAD("Timestamp", 60) OVER w AS ts_15m_later,
        LEAD(mark_ffilled, 120) OVER w AS price_30m_later, LEAD("Timestamp", 120) OVER w AS ts_30m_later
    FROM filled
    WINDOW w AS (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice" ORDER BY "Timestamp")
),
option_changes AS (
    SELECT "AsOfDate", "Timestamp", week_label, "OptionType",
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
),
band_week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeBandCadenceSnapshots") d
),
pcr_raw AS (
    SELECT b."AsOfDate", b."Timestamp",
        CASE WHEN b."CallOiSum" > 0 THEN b."PutOiSum"::float / b."CallOiSum" END AS raw_level,
        CASE WHEN b."CallOiSum" > 0 AND b."PutOiSum" > 0 THEN LN(b."CallOiSum"::float / b."PutOiSum") END AS log_level
    FROM "StrikeBandCadenceSnapshots" b
    JOIN band_week_map bwm ON bwm."AsOfDate" = b."AsOfDate" AND bwm."ExpiryDate" = b."ExpiryDate" AND bwm.week_rank = 1
    WHERE b."CadenceMinutes" = 5 AND b."BandDefinition" = 'Strike5'
),
pcr_with_delta AS (
    SELECT *,
        raw_level - LAG(raw_level) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp") AS raw_delta,
        log_level - LAG(log_level) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp") AS log_delta
    FROM pcr_raw
)
SELECT p."AsOfDate",
    COUNT(p.raw_level) AS n,
    ROUND(CORR(p.raw_level, oc.opt_fwd_5m)::numeric,3) AS raw_opt5, ROUND(CORR(p.raw_level, oc.opt_fwd_15m)::numeric,3) AS raw_opt15, ROUND(CORR(p.raw_level, oc.opt_fwd_30m)::numeric,3) AS raw_opt30,
    ROUND(CORR(p.raw_level, fc.future_fwd_5m)::numeric,3) AS raw_fut5, ROUND(CORR(p.raw_level, fc.future_fwd_15m)::numeric,3) AS raw_fut15, ROUND(CORR(p.raw_level, fc.future_fwd_30m)::numeric,3) AS raw_fut30,
    ROUND(CORR(p.log_level, oc.opt_fwd_5m)::numeric,3) AS log_opt5, ROUND(CORR(p.log_level, oc.opt_fwd_15m)::numeric,3) AS log_opt15, ROUND(CORR(p.log_level, oc.opt_fwd_30m)::numeric,3) AS log_opt30,
    ROUND(CORR(p.log_level, fc.future_fwd_5m)::numeric,3) AS log_fut5, ROUND(CORR(p.log_level, fc.future_fwd_15m)::numeric,3) AS log_fut15, ROUND(CORR(p.log_level, fc.future_fwd_30m)::numeric,3) AS log_fut30,
    ROUND(CORR(p.raw_delta, oc.opt_fwd_5m)::numeric,3) AS rawD_opt5, ROUND(CORR(p.raw_delta, oc.opt_fwd_15m)::numeric,3) AS rawD_opt15, ROUND(CORR(p.raw_delta, oc.opt_fwd_30m)::numeric,3) AS rawD_opt30,
    ROUND(CORR(p.log_delta, oc.opt_fwd_5m)::numeric,3) AS logD_opt5, ROUND(CORR(p.log_delta, oc.opt_fwd_15m)::numeric,3) AS logD_opt15, ROUND(CORR(p.log_delta, oc.opt_fwd_30m)::numeric,3) AS logD_opt30
FROM pcr_with_delta p
JOIN option_changes oc ON oc."AsOfDate" = p."AsOfDate" AND oc."Timestamp" = p."Timestamp" AND oc.week_label = 'ThisWeek' AND oc."OptionType" = 1
JOIN future_changes fc ON fc."AsOfDate" = p."AsOfDate" AND fc."Timestamp" = p."Timestamp"
GROUP BY 1
ORDER BY 1;
