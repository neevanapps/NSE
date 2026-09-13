-- 2026-09-12: PCR (put/call ratio) check, per user instruction -- first new Table 3 metric after
-- CVD. Uses the blind (unclassified) volume/notional totals, a genuinely different question from
-- CVD's aggressor-classified net: PCR asks "which side is busier," not "which side is being
-- bought vs sold within itself." Log-ratio (log(Call/Put)), not a raw ratio, matching this
-- project's own established preference (symmetric around 0, no single side dominating from scale
-- alone -- same reasoning as the ratio-composite's five metrics). Sign convention kept consistent
-- with CVD-diff/OI-diff: positive = call side busier = presumptively bullish-leaning. Target is
-- the option's own price (per 2026-09-12 correction -- future was only ever a stand-in used before
-- a per-strike price table existed), correlated separately against ATM call price and ATM put
-- price, since PCR (like CVD-diff) is one combined metric with no single natural "option price"
-- counterpart. A real signal should move call and put price in OPPOSITE directions.
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
SELECT pcr.week_label, pcr."CadenceMinutes", pcr."BandDefinition",
    COUNT(pcr.pcr_vol_log) AS n,
    ROUND(CORR(pcr.pcr_vol_log, callc.fwd_5m)::numeric,3) AS vol_vs_callprice_fwd5m,
    ROUND(CORR(pcr.pcr_vol_log, callc.fwd_15m)::numeric,3) AS vol_vs_callprice_fwd15m,
    ROUND(CORR(pcr.pcr_vol_log, putc.fwd_5m)::numeric,3) AS vol_vs_putprice_fwd5m,
    ROUND(CORR(pcr.pcr_vol_log, putc.fwd_15m)::numeric,3) AS vol_vs_putprice_fwd15m,
    ROUND(CORR(pcr.pcr_notional_log, callc.fwd_5m)::numeric,3) AS notional_vs_callprice_fwd5m,
    ROUND(CORR(pcr.pcr_notional_log, callc.fwd_15m)::numeric,3) AS notional_vs_callprice_fwd15m,
    ROUND(CORR(pcr.pcr_notional_log, putc.fwd_5m)::numeric,3) AS notional_vs_putprice_fwd5m,
    ROUND(CORR(pcr.pcr_notional_log, putc.fwd_15m)::numeric,3) AS notional_vs_putprice_fwd15m
FROM pcr
JOIN changes callc ON callc."AsOfDate" = pcr."AsOfDate" AND callc."Timestamp" = pcr."Timestamp" AND callc.week_label = pcr.week_label AND callc."OptionType" = 1
JOIN changes putc ON putc."AsOfDate" = pcr."AsOfDate" AND putc."Timestamp" = pcr."Timestamp" AND putc.week_label = pcr.week_label AND putc."OptionType" = 2
GROUP BY 1,2,3
ORDER BY 1,2,3;
