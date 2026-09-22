-- 2026-09-12: MA-smoothing experiment, per user instruction -- tests a dimension never tried
-- before on Table 3 metrics. Every correlation so far used either a raw 5-min bucket or a raw
-- 15-min bucket (non-overlapping, resets each time). This tests a 3-bucket ROLLING average of the
-- 5-min bucket value (MA-3, an overlapping ~15-min lookback that updates every 5 minutes) against
-- the same raw 5-min and raw 15-min bucket values already reported, for PCR (volume), OI-diff
-- (contract), and CVD (volume + notional) -- ThisWeek only (per today's trading-logic discussion),
-- Itm2Atm1 band (PCR's own winning band, used as the consistent reference across all four metrics
-- for this specific comparison).
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
base AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."MarkPrice",
        CASE WHEN wm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate"
    WHERE s."StrikeOffsetFromAtm" = 0
),
grouped AS (
    SELECT *, COUNT("MarkPrice") OVER (PARTITION BY "AsOfDate", week_label, "OptionType" ORDER BY "Timestamp") AS grp
    FROM base
),
filled AS (
    SELECT "AsOfDate", "Timestamp", week_label, "OptionType",
        FIRST_VALUE("MarkPrice") OVER (PARTITION BY "AsOfDate", week_label, "OptionType", grp ORDER BY "Timestamp") AS mark_ffilled
    FROM grouped
),
changes AS (
    SELECT "AsOfDate", "Timestamp", week_label, "OptionType",
        LEAD(mark_ffilled, 20) OVER (PARTITION BY "AsOfDate", week_label, "OptionType" ORDER BY "Timestamp") - mark_ffilled AS fwd_5m,
        LEAD(mark_ffilled, 60) OVER (PARTITION BY "AsOfDate", week_label, "OptionType" ORDER BY "Timestamp") - mark_ffilled AS fwd_15m
    FROM filled
),
band_week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeBandCadenceSnapshots") d
),
metrics_5min AS (
    SELECT b."AsOfDate", b."Timestamp",
        CASE WHEN b."CallVolumeSum" > 0 AND b."PutVolumeSum" > 0 THEN LN(b."CallVolumeSum"::float / b."PutVolumeSum") END AS pcr_vol_log,
        (b."CallOiChangeSum" - b."PutOiChangeSum")::float AS oi_diff,
        (b."CallCvdProxyVolumeNet" - b."PutCvdProxyVolumeNet")::float AS cvd_vol_diff,
        (b."CallCvdProxyNotionalNet" - b."PutCvdProxyNotionalNet")::float AS cvd_notional_diff
    FROM "StrikeBandCadenceSnapshots" b
    JOIN band_week_map bwm ON bwm."AsOfDate" = b."AsOfDate" AND bwm."ExpiryDate" = b."ExpiryDate"
    WHERE b."BandDefinition" = 'Itm2Atm1' AND b."CadenceMinutes" = 5 AND bwm.week_rank = 1
),
smoothed AS (
    SELECT *,
        AVG(pcr_vol_log) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) AS pcr_ma3,
        AVG(oi_diff) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) AS oi_diff_ma3,
        AVG(cvd_vol_diff) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) AS cvd_vol_ma3,
        AVG(cvd_notional_diff) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp" ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) AS cvd_notional_ma3
    FROM metrics_5min
)
SELECT
    'PCR-volume' AS metric,
    ROUND(CORR(pcr_vol_log, callc.fwd_5m)::numeric,3) AS raw_call_fwd5m,
    ROUND(CORR(pcr_vol_log, putc.fwd_5m)::numeric,3) AS raw_put_fwd5m,
    ROUND(CORR(pcr_ma3, callc.fwd_5m)::numeric,3) AS ma3_call_fwd5m,
    ROUND(CORR(pcr_ma3, putc.fwd_5m)::numeric,3) AS ma3_put_fwd5m,
    ROUND(CORR(pcr_ma3, callc.fwd_15m)::numeric,3) AS ma3_call_fwd15m,
    ROUND(CORR(pcr_ma3, putc.fwd_15m)::numeric,3) AS ma3_put_fwd15m
FROM smoothed s
JOIN changes callc ON callc."AsOfDate" = s."AsOfDate" AND callc."Timestamp" = s."Timestamp" AND callc.week_label = 'ThisWeek' AND callc."OptionType" = 1
JOIN changes putc ON putc."AsOfDate" = s."AsOfDate" AND putc."Timestamp" = s."Timestamp" AND putc.week_label = 'ThisWeek' AND putc."OptionType" = 2
UNION ALL
SELECT 'OI-diff (contract)',
    ROUND(CORR(oi_diff, callc.fwd_5m)::numeric,3), ROUND(CORR(oi_diff, putc.fwd_5m)::numeric,3),
    ROUND(CORR(oi_diff_ma3, callc.fwd_5m)::numeric,3), ROUND(CORR(oi_diff_ma3, putc.fwd_5m)::numeric,3),
    ROUND(CORR(oi_diff_ma3, callc.fwd_15m)::numeric,3), ROUND(CORR(oi_diff_ma3, putc.fwd_15m)::numeric,3)
FROM smoothed s
JOIN changes callc ON callc."AsOfDate" = s."AsOfDate" AND callc."Timestamp" = s."Timestamp" AND callc.week_label = 'ThisWeek' AND callc."OptionType" = 1
JOIN changes putc ON putc."AsOfDate" = s."AsOfDate" AND putc."Timestamp" = s."Timestamp" AND putc.week_label = 'ThisWeek' AND putc."OptionType" = 2
UNION ALL
SELECT 'CVD-volume',
    ROUND(CORR(cvd_vol_diff, callc.fwd_5m)::numeric,3), ROUND(CORR(cvd_vol_diff, putc.fwd_5m)::numeric,3),
    ROUND(CORR(cvd_vol_ma3, callc.fwd_5m)::numeric,3), ROUND(CORR(cvd_vol_ma3, putc.fwd_5m)::numeric,3),
    ROUND(CORR(cvd_vol_ma3, callc.fwd_15m)::numeric,3), ROUND(CORR(cvd_vol_ma3, putc.fwd_15m)::numeric,3)
FROM smoothed s
JOIN changes callc ON callc."AsOfDate" = s."AsOfDate" AND callc."Timestamp" = s."Timestamp" AND callc.week_label = 'ThisWeek' AND callc."OptionType" = 1
JOIN changes putc ON putc."AsOfDate" = s."AsOfDate" AND putc."Timestamp" = s."Timestamp" AND putc.week_label = 'ThisWeek' AND putc."OptionType" = 2
UNION ALL
SELECT 'CVD-notional',
    ROUND(CORR(cvd_notional_diff, callc.fwd_5m)::numeric,3), ROUND(CORR(cvd_notional_diff, putc.fwd_5m)::numeric,3),
    ROUND(CORR(cvd_notional_ma3, callc.fwd_5m)::numeric,3), ROUND(CORR(cvd_notional_ma3, putc.fwd_5m)::numeric,3),
    ROUND(CORR(cvd_notional_ma3, callc.fwd_15m)::numeric,3), ROUND(CORR(cvd_notional_ma3, putc.fwd_15m)::numeric,3)
FROM smoothed s
JOIN changes callc ON callc."AsOfDate" = s."AsOfDate" AND callc."Timestamp" = s."Timestamp" AND callc.week_label = 'ThisWeek' AND callc."OptionType" = 1
JOIN changes putc ON putc."AsOfDate" = s."AsOfDate" AND putc."Timestamp" = s."Timestamp" AND putc.week_label = 'ThisWeek' AND putc."OptionType" = 2;
