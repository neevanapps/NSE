-- 2026-09-12: PCR full corrected battery -- all 4 bands, both cadences, both weeks, volume AND
-- notional, per-day (not pooled, since pooling already proved misleading once this session).
-- Strike-identity-safe/time-guarded template throughout (partitioned by StrikePrice, anchored
-- only on genuinely-ATM rows, time-guard on elapsed forward-window duration).
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
        LEAD(mark_ffilled, 60) OVER w AS price_15m_later, LEAD("Timestamp", 60) OVER w AS ts_15m_later
    FROM filled
    WINDOW w AS (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice" ORDER BY "Timestamp")
),
changes AS (
    SELECT "AsOfDate", "Timestamp", week_label, "OptionType",
        CASE WHEN ts_5m_later - "Timestamp" BETWEEN INTERVAL '4 minutes 30 seconds' AND INTERVAL '5 minutes 30 seconds'
             THEN price_5m_later - mark_ffilled END AS fwd_5m,
        CASE WHEN ts_15m_later - "Timestamp" BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN price_15m_later - mark_ffilled END AS fwd_15m
    FROM with_lookahead
    WHERE "StrikeOffsetFromAtm" = 0
),
band_week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeBandCadenceSnapshots") d
),
pcr AS (
    SELECT b."AsOfDate", b."Timestamp", b."CadenceMinutes", b."BandDefinition",
        CASE WHEN bwm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label,
        CASE WHEN b."CallVolumeSum" > 0 AND b."PutVolumeSum" > 0
             THEN LN(b."CallVolumeSum"::float / b."PutVolumeSum") END AS pcr_vol_log,
        CASE WHEN b."CallNotionalSum" > 0 AND b."PutNotionalSum" > 0
             THEN LN(b."CallNotionalSum"::float / b."PutNotionalSum"::float) END AS pcr_notional_log
    FROM "StrikeBandCadenceSnapshots" b
    JOIN band_week_map bwm ON bwm."AsOfDate" = b."AsOfDate" AND bwm."ExpiryDate" = b."ExpiryDate"
)
SELECT pcr."AsOfDate", pcr.week_label, pcr."CadenceMinutes", pcr."BandDefinition",
    COUNT(pcr.pcr_vol_log) AS n,
    ROUND(CORR(pcr.pcr_vol_log, callc.fwd_5m)::numeric,3) AS vol_call_5m,
    ROUND(CORR(pcr.pcr_vol_log, putc.fwd_5m)::numeric,3) AS vol_put_5m,
    ROUND(CORR(pcr.pcr_vol_log, callc.fwd_15m)::numeric,3) AS vol_call_15m,
    ROUND(CORR(pcr.pcr_vol_log, putc.fwd_15m)::numeric,3) AS vol_put_15m,
    ROUND(CORR(pcr.pcr_notional_log, callc.fwd_5m)::numeric,3) AS notional_call_5m,
    ROUND(CORR(pcr.pcr_notional_log, putc.fwd_5m)::numeric,3) AS notional_put_5m,
    ROUND(CORR(pcr.pcr_notional_log, callc.fwd_15m)::numeric,3) AS notional_call_15m,
    ROUND(CORR(pcr.pcr_notional_log, putc.fwd_15m)::numeric,3) AS notional_put_15m
FROM pcr
JOIN changes callc ON callc."AsOfDate" = pcr."AsOfDate" AND callc."Timestamp" = pcr."Timestamp" AND callc.week_label = pcr.week_label AND callc."OptionType" = 1
JOIN changes putc ON putc."AsOfDate" = pcr."AsOfDate" AND putc."Timestamp" = pcr."Timestamp" AND putc.week_label = pcr.week_label AND putc."OptionType" = 2
GROUP BY 1,2,3,4
ORDER BY 2,3,4,1;
