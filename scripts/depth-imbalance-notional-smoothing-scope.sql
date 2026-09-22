-- 2026-09-16 -- Scoping pass for the proposed DepthImbalance/NotionalVolumeRatio smoothing fix
-- (mirrors audit finding F51's mechanism, NOT its window length). Question: does giving these two
-- CoreScore terms their own rolling-time smoothing (1/3/5/10 min) before ranking improve, hurt, or
-- not change their forward-looking correlation with price, versus the current unsmoothed
-- (single 15s-cadence) raw value? Same backward/forward, per-day + pooled methodology, ThisWeek
-- only, as every other metric evaluation in this project.
--
-- DepthImbalance is smoothed as a rolling MEAN (a resting-book STATE, matching F51's own treatment
-- of state-like metrics). NotionalVolumeRatio is smoothed as rolling SUMS of call/put notional,
-- THEN one log-ratio of the sums (a FLOW metric -- matches FutureCvdNet5Min/OiChangeDiff15m's own
-- "sum over the window, not average of ratios" convention already established in this codebase).

\echo '=== DepthImbalance: raw vs 1/3/5/10-min rolling MEAN, ThisWeek, per day ==='
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate", ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
all_strikes AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."StrikePrice", s."StrikeOffsetFromAtm", s."MarkPrice", s."VolumeDelta", s."DepthImbalanceFromLastCadence",
        CASE WHEN wm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate"
    WHERE wm.week_rank = 1
),
-- Forward-filled ATM (offset=0) mark price per option type, for the price-target anchor series.
grouped AS (
    SELECT *, COUNT("MarkPrice") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice" ORDER BY "Timestamp") AS grp
    FROM all_strikes
),
filled AS (
    SELECT "AsOfDate", "Timestamp", "OptionType", "StrikePrice", "StrikeOffsetFromAtm", "VolumeDelta", "DepthImbalanceFromLastCadence",
        FIRST_VALUE("MarkPrice") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice", grp ORDER BY "Timestamp") AS mark_ffilled
    FROM grouped
),
with_lookahead AS (
    SELECT *,
        LEAD(mark_ffilled, 20) OVER w AS price_5m_later, LEAD("Timestamp", 20) OVER w AS ts_5m_later,
        LEAD(mark_ffilled, 60) OVER w AS price_15m_later, LEAD("Timestamp", 60) OVER w AS ts_15m_later
    FROM filled
    WINDOW w AS (PARTITION BY "AsOfDate", "OptionType", "StrikePrice" ORDER BY "Timestamp")
),
anchor_changes AS (
    SELECT "AsOfDate", "Timestamp", "OptionType",
        CASE WHEN ts_5m_later - "Timestamp" BETWEEN INTERVAL '4 minutes 30 seconds' AND INTERVAL '5 minutes 30 seconds'
             THEN price_5m_later - mark_ffilled END AS fwd_5m,
        CASE WHEN ts_15m_later - "Timestamp" BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN price_15m_later - mark_ffilled END AS fwd_15m
    FROM with_lookahead
    WHERE "StrikeOffsetFromAtm" = 0
),
-- Per-15s-cadence raw DepthImbalance, Itm2Atm1 band (calls -2..0, puts 0..2) -- exactly
-- ComputeCoreDepthImbalanceRaw's own selection, byte-for-byte the CoreScoreBands.InItm2Atm1 rule.
cadence_di AS (
    SELECT "AsOfDate", "Timestamp",
        AVG("DepthImbalanceFromLastCadence") FILTER (WHERE "OptionType" = 1 AND "StrikeOffsetFromAtm" BETWEEN -2 AND 0) AS call_avg,
        AVG("DepthImbalanceFromLastCadence") FILTER (WHERE "OptionType" = 2 AND "StrikeOffsetFromAtm" BETWEEN 0 AND 2) AS put_avg
    FROM all_strikes
    GROUP BY 1, 2
),
di_raw AS (
    SELECT "AsOfDate", "Timestamp",
        CASE WHEN call_avg IS NOT NULL AND put_avg IS NOT NULL THEN call_avg - put_avg END AS di
    FROM cadence_di
),
di_smoothed AS (
    SELECT "AsOfDate", "Timestamp", di AS di_raw,
        AVG(di) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '1 minute'  PRECEDING AND CURRENT ROW) AS di_1m,
        AVG(di) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '3 minutes' PRECEDING AND CURRENT ROW) AS di_3m,
        AVG(di) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '5 minutes' PRECEDING AND CURRENT ROW) AS di_5m,
        AVG(di) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '10 minutes' PRECEDING AND CURRENT ROW) AS di_10m
    FROM di_raw
),
joined AS (
    SELECT d.*, callc.fwd_5m AS call_fwd5m, callc.fwd_15m AS call_fwd15m, putc.fwd_5m AS put_fwd5m, putc.fwd_15m AS put_fwd15m
    FROM di_smoothed d
    JOIN anchor_changes callc ON callc."AsOfDate" = d."AsOfDate" AND callc."Timestamp" = d."Timestamp" AND callc."OptionType" = 1
    JOIN anchor_changes putc  ON putc."AsOfDate"  = d."AsOfDate" AND putc."Timestamp"  = d."Timestamp" AND putc."OptionType"  = 2
)
SELECT 'raw'  AS variant, "AsOfDate", COUNT(di_raw) AS n,
    ROUND(CORR(di_raw, call_fwd5m)::numeric,3) AS call_fwd5m, ROUND(CORR(di_raw, call_fwd15m)::numeric,3) AS call_fwd15m,
    ROUND(CORR(di_raw, put_fwd5m)::numeric,3)  AS put_fwd5m,  ROUND(CORR(di_raw, put_fwd15m)::numeric,3)  AS put_fwd15m
FROM joined GROUP BY "AsOfDate"
UNION ALL
SELECT '1m', "AsOfDate", COUNT(di_1m),
    ROUND(CORR(di_1m, call_fwd5m)::numeric,3), ROUND(CORR(di_1m, call_fwd15m)::numeric,3),
    ROUND(CORR(di_1m, put_fwd5m)::numeric,3),  ROUND(CORR(di_1m, put_fwd15m)::numeric,3)
FROM joined GROUP BY "AsOfDate"
UNION ALL
SELECT '3m', "AsOfDate", COUNT(di_3m),
    ROUND(CORR(di_3m, call_fwd5m)::numeric,3), ROUND(CORR(di_3m, call_fwd15m)::numeric,3),
    ROUND(CORR(di_3m, put_fwd5m)::numeric,3),  ROUND(CORR(di_3m, put_fwd15m)::numeric,3)
FROM joined GROUP BY "AsOfDate"
UNION ALL
SELECT '5m', "AsOfDate", COUNT(di_5m),
    ROUND(CORR(di_5m, call_fwd5m)::numeric,3), ROUND(CORR(di_5m, call_fwd15m)::numeric,3),
    ROUND(CORR(di_5m, put_fwd5m)::numeric,3),  ROUND(CORR(di_5m, put_fwd15m)::numeric,3)
FROM joined GROUP BY "AsOfDate"
UNION ALL
SELECT '10m', "AsOfDate", COUNT(di_10m),
    ROUND(CORR(di_10m, call_fwd5m)::numeric,3), ROUND(CORR(di_10m, call_fwd15m)::numeric,3),
    ROUND(CORR(di_10m, put_fwd5m)::numeric,3),  ROUND(CORR(di_10m, put_fwd15m)::numeric,3)
FROM joined GROUP BY "AsOfDate"
ORDER BY 1, 2;

\echo ''
\echo '=== DepthImbalance: pooled across all 5 days ==='
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate", ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
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
    SELECT d.*, callc.fwd_5m AS call_fwd5m, callc.fwd_15m AS call_fwd15m, putc.fwd_5m AS put_fwd5m, putc.fwd_15m AS put_fwd15m
    FROM di_smoothed d
    JOIN anchor_changes callc ON callc."AsOfDate" = d."AsOfDate" AND callc."Timestamp" = d."Timestamp" AND callc."OptionType" = 1
    JOIN anchor_changes putc  ON putc."AsOfDate"  = d."AsOfDate" AND putc."Timestamp"  = d."Timestamp" AND putc."OptionType"  = 2
)
SELECT 'raw' AS variant, COUNT(di_raw) AS n,
    ROUND(CORR(di_raw, call_fwd5m)::numeric,3) AS call_fwd5m, ROUND(CORR(di_raw, call_fwd15m)::numeric,3) AS call_fwd15m,
    ROUND(CORR(di_raw, put_fwd5m)::numeric,3)  AS put_fwd5m,  ROUND(CORR(di_raw, put_fwd15m)::numeric,3)  AS put_fwd15m
FROM joined
UNION ALL
SELECT '1m', COUNT(di_1m), ROUND(CORR(di_1m, call_fwd5m)::numeric,3), ROUND(CORR(di_1m, call_fwd15m)::numeric,3), ROUND(CORR(di_1m, put_fwd5m)::numeric,3), ROUND(CORR(di_1m, put_fwd15m)::numeric,3) FROM joined
UNION ALL
SELECT '3m', COUNT(di_3m), ROUND(CORR(di_3m, call_fwd5m)::numeric,3), ROUND(CORR(di_3m, call_fwd15m)::numeric,3), ROUND(CORR(di_3m, put_fwd5m)::numeric,3), ROUND(CORR(di_3m, put_fwd15m)::numeric,3) FROM joined
UNION ALL
SELECT '5m', COUNT(di_5m), ROUND(CORR(di_5m, call_fwd5m)::numeric,3), ROUND(CORR(di_5m, call_fwd15m)::numeric,3), ROUND(CORR(di_5m, put_fwd5m)::numeric,3), ROUND(CORR(di_5m, put_fwd15m)::numeric,3) FROM joined
UNION ALL
SELECT '10m', COUNT(di_10m), ROUND(CORR(di_10m, call_fwd5m)::numeric,3), ROUND(CORR(di_10m, call_fwd15m)::numeric,3), ROUND(CORR(di_10m, put_fwd5m)::numeric,3), ROUND(CORR(di_10m, put_fwd15m)::numeric,3) FROM joined
ORDER BY 1;

\echo ''
\echo '=== NotionalVolumeRatio: raw vs 1/3/5/10-min rolling-SUM-then-ratio, ThisWeek, per day ==='
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate", ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
all_strikes AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."StrikePrice", s."StrikeOffsetFromAtm", s."MarkPrice", s."VolumeDelta"
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate"
    WHERE wm.week_rank = 1
),
grouped AS (SELECT *, COUNT("MarkPrice") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice" ORDER BY "Timestamp") AS grp FROM all_strikes),
filled AS (
    SELECT "AsOfDate", "Timestamp", "OptionType", "StrikePrice", "StrikeOffsetFromAtm", "VolumeDelta",
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
-- Per-15s-cadence call/put notional, ATM+/-5, VolumeDelta*MarkPrice per strike then summed --
-- exactly ComputeCoreNotionalVolumeRatioRaw / CoreScoreOptionSimulator's own callNotional/putNotional.
cadence_notional AS (
    SELECT "AsOfDate", "Timestamp",
        SUM("VolumeDelta" * "MarkPrice") FILTER (WHERE "OptionType" = 1 AND ABS("StrikeOffsetFromAtm") <= 5 AND "VolumeDelta" > 0 AND "MarkPrice" > 0) AS call_notional,
        SUM("VolumeDelta" * "MarkPrice") FILTER (WHERE "OptionType" = 2 AND ABS("StrikeOffsetFromAtm") <= 5 AND "VolumeDelta" > 0 AND "MarkPrice" > 0) AS put_notional
    FROM all_strikes GROUP BY 1, 2
),
nvr_raw AS (
    SELECT "AsOfDate", "Timestamp", call_notional, put_notional,
        CASE WHEN call_notional > 0 AND put_notional > 0 THEN LN(put_notional::double precision / call_notional::double precision) END AS nvr
    FROM cadence_notional
),
nvr_smoothed AS (
    SELECT "AsOfDate", "Timestamp", nvr AS nvr_raw,
        CASE WHEN call_sum_1m > 0 AND put_sum_1m > 0 THEN LN(put_sum_1m / call_sum_1m) END AS nvr_1m,
        CASE WHEN call_sum_3m > 0 AND put_sum_3m > 0 THEN LN(put_sum_3m / call_sum_3m) END AS nvr_3m,
        CASE WHEN call_sum_5m > 0 AND put_sum_5m > 0 THEN LN(put_sum_5m / call_sum_5m) END AS nvr_5m,
        CASE WHEN call_sum_10m > 0 AND put_sum_10m > 0 THEN LN(put_sum_10m / call_sum_10m) END AS nvr_10m
    FROM (
        SELECT "AsOfDate", "Timestamp", nvr,
            SUM(call_notional) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '1 minute'  PRECEDING AND CURRENT ROW)::double precision AS call_sum_1m,
            SUM(put_notional)  OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '1 minute'  PRECEDING AND CURRENT ROW)::double precision AS put_sum_1m,
            SUM(call_notional) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '3 minutes' PRECEDING AND CURRENT ROW)::double precision AS call_sum_3m,
            SUM(put_notional)  OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '3 minutes' PRECEDING AND CURRENT ROW)::double precision AS put_sum_3m,
            SUM(call_notional) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '5 minutes' PRECEDING AND CURRENT ROW)::double precision AS call_sum_5m,
            SUM(put_notional)  OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '5 minutes' PRECEDING AND CURRENT ROW)::double precision AS put_sum_5m,
            SUM(call_notional) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '10 minutes' PRECEDING AND CURRENT ROW)::double precision AS call_sum_10m,
            SUM(put_notional)  OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '10 minutes' PRECEDING AND CURRENT ROW)::double precision AS put_sum_10m
        FROM nvr_raw
    ) w
),
joined AS (
    SELECT d.*, callc.fwd_5m AS call_fwd5m, callc.fwd_15m AS call_fwd15m, putc.fwd_5m AS put_fwd5m, putc.fwd_15m AS put_fwd15m
    FROM nvr_smoothed d
    JOIN anchor_changes callc ON callc."AsOfDate" = d."AsOfDate" AND callc."Timestamp" = d."Timestamp" AND callc."OptionType" = 1
    JOIN anchor_changes putc  ON putc."AsOfDate"  = d."AsOfDate" AND putc."Timestamp"  = d."Timestamp" AND putc."OptionType"  = 2
)
SELECT 'raw' AS variant, "AsOfDate", COUNT(nvr_raw) AS n,
    ROUND(CORR(nvr_raw, call_fwd5m)::numeric,3) AS call_fwd5m, ROUND(CORR(nvr_raw, call_fwd15m)::numeric,3) AS call_fwd15m,
    ROUND(CORR(nvr_raw, put_fwd5m)::numeric,3)  AS put_fwd5m,  ROUND(CORR(nvr_raw, put_fwd15m)::numeric,3)  AS put_fwd15m
FROM joined GROUP BY "AsOfDate"
UNION ALL
SELECT '1m', "AsOfDate", COUNT(nvr_1m), ROUND(CORR(nvr_1m, call_fwd5m)::numeric,3), ROUND(CORR(nvr_1m, call_fwd15m)::numeric,3), ROUND(CORR(nvr_1m, put_fwd5m)::numeric,3), ROUND(CORR(nvr_1m, put_fwd15m)::numeric,3) FROM joined GROUP BY "AsOfDate"
UNION ALL
SELECT '3m', "AsOfDate", COUNT(nvr_3m), ROUND(CORR(nvr_3m, call_fwd5m)::numeric,3), ROUND(CORR(nvr_3m, call_fwd15m)::numeric,3), ROUND(CORR(nvr_3m, put_fwd5m)::numeric,3), ROUND(CORR(nvr_3m, put_fwd15m)::numeric,3) FROM joined GROUP BY "AsOfDate"
UNION ALL
SELECT '5m', "AsOfDate", COUNT(nvr_5m), ROUND(CORR(nvr_5m, call_fwd5m)::numeric,3), ROUND(CORR(nvr_5m, call_fwd15m)::numeric,3), ROUND(CORR(nvr_5m, put_fwd5m)::numeric,3), ROUND(CORR(nvr_5m, put_fwd15m)::numeric,3) FROM joined GROUP BY "AsOfDate"
UNION ALL
SELECT '10m', "AsOfDate", COUNT(nvr_10m), ROUND(CORR(nvr_10m, call_fwd5m)::numeric,3), ROUND(CORR(nvr_10m, call_fwd15m)::numeric,3), ROUND(CORR(nvr_10m, put_fwd5m)::numeric,3), ROUND(CORR(nvr_10m, put_fwd15m)::numeric,3) FROM joined GROUP BY "AsOfDate"
ORDER BY 1, 2;

\echo ''
\echo '=== NotionalVolumeRatio: pooled across all 5 days ==='
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate", ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
all_strikes AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."StrikePrice", s."StrikeOffsetFromAtm", s."MarkPrice", s."VolumeDelta"
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate"
    WHERE wm.week_rank = 1
),
grouped AS (SELECT *, COUNT("MarkPrice") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice" ORDER BY "Timestamp") AS grp FROM all_strikes),
filled AS (
    SELECT "AsOfDate", "Timestamp", "OptionType", "StrikePrice", "StrikeOffsetFromAtm", "VolumeDelta",
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
cadence_notional AS (
    SELECT "AsOfDate", "Timestamp",
        SUM("VolumeDelta" * "MarkPrice") FILTER (WHERE "OptionType" = 1 AND ABS("StrikeOffsetFromAtm") <= 5 AND "VolumeDelta" > 0 AND "MarkPrice" > 0) AS call_notional,
        SUM("VolumeDelta" * "MarkPrice") FILTER (WHERE "OptionType" = 2 AND ABS("StrikeOffsetFromAtm") <= 5 AND "VolumeDelta" > 0 AND "MarkPrice" > 0) AS put_notional
    FROM all_strikes GROUP BY 1, 2
),
nvr_raw AS (
    SELECT "AsOfDate", "Timestamp", call_notional, put_notional,
        CASE WHEN call_notional > 0 AND put_notional > 0 THEN LN(put_notional::double precision / call_notional::double precision) END AS nvr
    FROM cadence_notional
),
nvr_smoothed AS (
    SELECT "AsOfDate", "Timestamp", nvr AS nvr_raw,
        CASE WHEN call_sum_1m > 0 AND put_sum_1m > 0 THEN LN(put_sum_1m / call_sum_1m) END AS nvr_1m,
        CASE WHEN call_sum_3m > 0 AND put_sum_3m > 0 THEN LN(put_sum_3m / call_sum_3m) END AS nvr_3m,
        CASE WHEN call_sum_5m > 0 AND put_sum_5m > 0 THEN LN(put_sum_5m / call_sum_5m) END AS nvr_5m,
        CASE WHEN call_sum_10m > 0 AND put_sum_10m > 0 THEN LN(put_sum_10m / call_sum_10m) END AS nvr_10m
    FROM (
        SELECT "AsOfDate", "Timestamp", nvr,
            SUM(call_notional) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '1 minute'  PRECEDING AND CURRENT ROW)::double precision AS call_sum_1m,
            SUM(put_notional)  OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '1 minute'  PRECEDING AND CURRENT ROW)::double precision AS put_sum_1m,
            SUM(call_notional) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '3 minutes' PRECEDING AND CURRENT ROW)::double precision AS call_sum_3m,
            SUM(put_notional)  OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '3 minutes' PRECEDING AND CURRENT ROW)::double precision AS put_sum_3m,
            SUM(call_notional) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '5 minutes' PRECEDING AND CURRENT ROW)::double precision AS call_sum_5m,
            SUM(put_notional)  OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '5 minutes' PRECEDING AND CURRENT ROW)::double precision AS put_sum_5m,
            SUM(call_notional) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '10 minutes' PRECEDING AND CURRENT ROW)::double precision AS call_sum_10m,
            SUM(put_notional)  OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '10 minutes' PRECEDING AND CURRENT ROW)::double precision AS put_sum_10m
        FROM nvr_raw
    ) w
),
joined AS (
    SELECT d.*, callc.fwd_5m AS call_fwd5m, callc.fwd_15m AS call_fwd15m, putc.fwd_5m AS put_fwd5m, putc.fwd_15m AS put_fwd15m
    FROM nvr_smoothed d
    JOIN anchor_changes callc ON callc."AsOfDate" = d."AsOfDate" AND callc."Timestamp" = d."Timestamp" AND callc."OptionType" = 1
    JOIN anchor_changes putc  ON putc."AsOfDate"  = d."AsOfDate" AND putc."Timestamp"  = d."Timestamp" AND putc."OptionType"  = 2
)
SELECT 'raw' AS variant, COUNT(nvr_raw) AS n,
    ROUND(CORR(nvr_raw, call_fwd5m)::numeric,3) AS call_fwd5m, ROUND(CORR(nvr_raw, call_fwd15m)::numeric,3) AS call_fwd15m,
    ROUND(CORR(nvr_raw, put_fwd5m)::numeric,3)  AS put_fwd5m,  ROUND(CORR(nvr_raw, put_fwd15m)::numeric,3)  AS put_fwd15m
FROM joined
UNION ALL
SELECT '1m', COUNT(nvr_1m), ROUND(CORR(nvr_1m, call_fwd5m)::numeric,3), ROUND(CORR(nvr_1m, call_fwd15m)::numeric,3), ROUND(CORR(nvr_1m, put_fwd5m)::numeric,3), ROUND(CORR(nvr_1m, put_fwd15m)::numeric,3) FROM joined
UNION ALL
SELECT '3m', COUNT(nvr_3m), ROUND(CORR(nvr_3m, call_fwd5m)::numeric,3), ROUND(CORR(nvr_3m, call_fwd15m)::numeric,3), ROUND(CORR(nvr_3m, put_fwd5m)::numeric,3), ROUND(CORR(nvr_3m, put_fwd15m)::numeric,3) FROM joined
UNION ALL
SELECT '5m', COUNT(nvr_5m), ROUND(CORR(nvr_5m, call_fwd5m)::numeric,3), ROUND(CORR(nvr_5m, call_fwd15m)::numeric,3), ROUND(CORR(nvr_5m, put_fwd5m)::numeric,3), ROUND(CORR(nvr_5m, put_fwd15m)::numeric,3) FROM joined
UNION ALL
SELECT '10m', COUNT(nvr_10m), ROUND(CORR(nvr_10m, call_fwd5m)::numeric,3), ROUND(CORR(nvr_10m, call_fwd15m)::numeric,3), ROUND(CORR(nvr_10m, put_fwd5m)::numeric,3), ROUND(CORR(nvr_10m, put_fwd15m)::numeric,3) FROM joined
ORDER BY 1;
