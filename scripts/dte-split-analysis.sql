-- 2026-09-16 -- DTE split of the two analyses just run (depth-imbalance-otm-mirror-vs-itm.sql,
-- depth-imbalance-notional-smoothing-scope.sql), per user request: does DepthImbalance's band
-- coherence and its smoothing benefit separate cleanly by days-to-expiry (0-DTE expiry days vs
-- 4-6 DTE mid-week days), rather than pooling all 5 days together? Confirmed DTE per day directly
-- against StrikeCadenceSnapshots before writing this: 08 Sep=0, 15 Sep=0, 11 Sep=4, 10 Sep=5,
-- 09 Sep=6 -- 2 expiry days, 3 mid-week days.

\echo '=== (1) DepthImbalance band comparison, split by DTE bucket (0-DTE vs 4-6 DTE) ==='
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate", ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
dte_bucket AS (
    SELECT "AsOfDate", "ExpiryDate",
        CASE WHEN "ExpiryDate" - "AsOfDate" = 0 THEN '0-DTE' ELSE '4-6-DTE' END AS bucket
    FROM week_map WHERE week_rank = 1
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
        LEAD(mark_ffilled, 60) OVER w AS price_15m_later, LEAD("Timestamp", 60) OVER w AS ts_15m_later
    FROM filled
    WINDOW w AS (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice" ORDER BY "Timestamp")
),
anchor_changes AS (
    SELECT "AsOfDate", "Timestamp", week_label, "OptionType",
        CASE WHEN ts_5m_later - "Timestamp" BETWEEN INTERVAL '4 minutes 30 seconds' AND INTERVAL '5 minutes 30 seconds'
             THEN price_5m_later - mark_ffilled END AS fwd_5m,
        CASE WHEN ts_15m_later - "Timestamp" BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN price_15m_later - mark_ffilled END AS fwd_15m
    FROM with_lookahead
    WHERE "StrikeOffsetFromAtm" = 0
),
band_week_map AS (
    SELECT "AsOfDate", "ExpiryDate", ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeBandCadenceSnapshots") d
),
di AS (
    SELECT b."AsOfDate", b."Timestamp", b."BandDefinition",
        CASE WHEN bwm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label,
        (b."CallDepthImbalanceAvg" - b."PutDepthImbalanceAvg") AS di_diff
    FROM "StrikeBandCadenceSnapshots" b
    JOIN band_week_map bwm ON bwm."AsOfDate" = b."AsOfDate" AND bwm."ExpiryDate" = b."ExpiryDate"
    WHERE b."BandDefinition" IN ('Itm2Atm1', 'Strike5') AND b."CadenceMinutes" = 5
)
SELECT di."BandDefinition", db.bucket, COUNT(di.di_diff) AS n,
    ROUND(CORR(di.di_diff, callc.fwd_5m)::numeric,3)  AS callprice_fwd5m,
    ROUND(CORR(di.di_diff, callc.fwd_15m)::numeric,3) AS callprice_fwd15m,
    ROUND(CORR(di.di_diff, putc.fwd_5m)::numeric,3)   AS putprice_fwd5m,
    ROUND(CORR(di.di_diff, putc.fwd_15m)::numeric,3)  AS putprice_fwd15m
FROM di
JOIN dte_bucket db ON db."AsOfDate" = di."AsOfDate"
JOIN anchor_changes callc ON callc."AsOfDate" = di."AsOfDate" AND callc."Timestamp" = di."Timestamp" AND callc.week_label = di.week_label AND callc."OptionType" = 1
JOIN anchor_changes putc  ON putc."AsOfDate"  = di."AsOfDate" AND putc."Timestamp"  = di."Timestamp" AND putc.week_label  = di.week_label AND putc."OptionType"  = 2
WHERE di.week_label = 'ThisWeek'
GROUP BY 1,2
ORDER BY 1,2;

\echo ''
\echo '=== (2) DepthImbalance smoothing sweep (raw/1m/3m/5m/10m), split by DTE bucket ==='
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate", ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
dte_bucket AS (
    SELECT "AsOfDate", "ExpiryDate",
        CASE WHEN "ExpiryDate" - "AsOfDate" = 0 THEN '0-DTE' ELSE '4-6-DTE' END AS bucket
    FROM week_map WHERE week_rank = 1
),
all_strikes AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."StrikePrice", s."StrikeOffsetFromAtm", s."MarkPrice", s."DepthImbalanceFromLastCadence"
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate"
    WHERE wm.week_rank = 1
),
grouped AS (SELECT *, COUNT("MarkPrice") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice" ORDER BY "Timestamp") AS grp FROM all_strikes),
filled AS (
    SELECT "AsOfDate", "Timestamp", "OptionType", "StrikePrice", "StrikeOffsetFromAtm", "DepthImbalanceFromLastCadence",
        FIRST_VALUE("MarkPrice") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice", grp ORDER BY "Timestamp") AS mark_ffilled
    FROM grouped
),
with_lookahead AS (
    SELECT *, LEAD(mark_ffilled, 20) OVER w AS price_5m_later, LEAD("Timestamp", 20) OVER w AS ts_5m_later,
        LEAD(mark_ffilled, 60) OVER w AS price_15m_later, LEAD("Timestamp", 60) OVER w AS ts_15m_later
    FROM filled WINDOW w AS (PARTITION BY "AsOfDate", "OptionType", "StrikePrice" ORDER BY "Timestamp")
),
anchor_changes AS (
    SELECT "AsOfDate", "Timestamp", "OptionType",
        CASE WHEN ts_5m_later - "Timestamp" BETWEEN INTERVAL '4 minutes 30 seconds' AND INTERVAL '5 minutes 30 seconds' THEN price_5m_later - mark_ffilled END AS fwd_5m,
        CASE WHEN ts_15m_later - "Timestamp" BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds' THEN price_15m_later - mark_ffilled END AS fwd_15m
    FROM with_lookahead WHERE "StrikeOffsetFromAtm" = 0
),
cadence_di AS (
    SELECT "AsOfDate", "Timestamp",
        AVG("DepthImbalanceFromLastCadence") FILTER (WHERE "OptionType" = 1 AND "StrikeOffsetFromAtm" BETWEEN -2 AND 0) AS call_avg,
        AVG("DepthImbalanceFromLastCadence") FILTER (WHERE "OptionType" = 2 AND "StrikeOffsetFromAtm" BETWEEN 0 AND 2) AS put_avg
    FROM all_strikes GROUP BY 1, 2
),
di_raw AS (SELECT "AsOfDate", "Timestamp", CASE WHEN call_avg IS NOT NULL AND put_avg IS NOT NULL THEN call_avg - put_avg END AS di FROM cadence_di),
di_smoothed AS (
    SELECT "AsOfDate", "Timestamp", di AS di_raw,
        AVG(di) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '1 minute'  PRECEDING AND CURRENT ROW) AS di_1m,
        AVG(di) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '3 minutes' PRECEDING AND CURRENT ROW) AS di_3m,
        AVG(di) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '5 minutes' PRECEDING AND CURRENT ROW) AS di_5m,
        AVG(di) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '10 minutes' PRECEDING AND CURRENT ROW) AS di_10m
    FROM di_raw
),
joined AS (
    SELECT d.*, db.bucket, callc.fwd_5m AS call_fwd5m, callc.fwd_15m AS call_fwd15m, putc.fwd_5m AS put_fwd5m, putc.fwd_15m AS put_fwd15m
    FROM di_smoothed d
    JOIN dte_bucket db ON db."AsOfDate" = d."AsOfDate"
    JOIN anchor_changes callc ON callc."AsOfDate" = d."AsOfDate" AND callc."Timestamp" = d."Timestamp" AND callc."OptionType" = 1
    JOIN anchor_changes putc  ON putc."AsOfDate"  = d."AsOfDate" AND putc."Timestamp"  = d."Timestamp" AND putc."OptionType"  = 2
)
SELECT 'raw' AS variant, bucket, COUNT(di_raw) AS n,
    ROUND(CORR(di_raw, call_fwd5m)::numeric,3) AS call_fwd5m, ROUND(CORR(di_raw, call_fwd15m)::numeric,3) AS call_fwd15m,
    ROUND(CORR(di_raw, put_fwd5m)::numeric,3)  AS put_fwd5m,  ROUND(CORR(di_raw, put_fwd15m)::numeric,3)  AS put_fwd15m
FROM joined GROUP BY bucket
UNION ALL
SELECT '1m', bucket, COUNT(di_1m), ROUND(CORR(di_1m, call_fwd5m)::numeric,3), ROUND(CORR(di_1m, call_fwd15m)::numeric,3), ROUND(CORR(di_1m, put_fwd5m)::numeric,3), ROUND(CORR(di_1m, put_fwd15m)::numeric,3) FROM joined GROUP BY bucket
UNION ALL
SELECT '3m', bucket, COUNT(di_3m), ROUND(CORR(di_3m, call_fwd5m)::numeric,3), ROUND(CORR(di_3m, call_fwd15m)::numeric,3), ROUND(CORR(di_3m, put_fwd5m)::numeric,3), ROUND(CORR(di_3m, put_fwd15m)::numeric,3) FROM joined GROUP BY bucket
UNION ALL
SELECT '5m', bucket, COUNT(di_5m), ROUND(CORR(di_5m, call_fwd5m)::numeric,3), ROUND(CORR(di_5m, call_fwd15m)::numeric,3), ROUND(CORR(di_5m, put_fwd5m)::numeric,3), ROUND(CORR(di_5m, put_fwd15m)::numeric,3) FROM joined GROUP BY bucket
UNION ALL
SELECT '10m', bucket, COUNT(di_10m), ROUND(CORR(di_10m, call_fwd5m)::numeric,3), ROUND(CORR(di_10m, call_fwd15m)::numeric,3), ROUND(CORR(di_10m, put_fwd5m)::numeric,3), ROUND(CORR(di_10m, put_fwd15m)::numeric,3) FROM joined GROUP BY bucket
ORDER BY 1,2;
