-- 2026-09-12: PCR retest with the strike-identity-safe/time-guarded template (originally
-- established for CVD's redo, cvd-redo-touch-rule.sql) -- the ORIGINAL pcr-vs-option-price.sql
-- (written 15:46, before the fix was established at 16:45) filtered StrikeOffsetFromAtm=0 and
-- computed LEAD without partitioning by StrikePrice, and without a time-guard on elapsed time --
-- exactly the splicing bug found and fixed (again) for F58. Retesting PCR's CONFIRMED status
-- against the corrected methodology before trusting it any further. Full band/cadence sweep,
-- not just the single previously-strongest cell.
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
             THEN LN(b."CallVolumeSum"::float / b."PutVolumeSum") END AS pcr_vol_log
    FROM "StrikeBandCadenceSnapshots" b
    JOIN band_week_map bwm ON bwm."AsOfDate" = b."AsOfDate" AND bwm."ExpiryDate" = b."ExpiryDate"
)
SELECT pcr.week_label, pcr."CadenceMinutes", pcr."BandDefinition",
    COUNT(pcr.pcr_vol_log) AS n,
    ROUND(CORR(pcr.pcr_vol_log, callc.fwd_5m)::numeric,3) AS vol_vs_callprice_fwd5m,
    ROUND(CORR(pcr.pcr_vol_log, callc.fwd_15m)::numeric,3) AS vol_vs_callprice_fwd15m,
    ROUND(CORR(pcr.pcr_vol_log, putc.fwd_5m)::numeric,3) AS vol_vs_putprice_fwd5m,
    ROUND(CORR(pcr.pcr_vol_log, putc.fwd_15m)::numeric,3) AS vol_vs_putprice_fwd15m
FROM pcr
JOIN changes callc ON callc."AsOfDate" = pcr."AsOfDate" AND callc."Timestamp" = pcr."Timestamp" AND callc.week_label = pcr.week_label AND callc."OptionType" = 1
JOIN changes putc ON putc."AsOfDate" = pcr."AsOfDate" AND putc."Timestamp" = pcr."Timestamp" AND putc.week_label = pcr.week_label AND putc."OptionType" = 2
GROUP BY 1,2,3
ORDER BY 1,2,3;
