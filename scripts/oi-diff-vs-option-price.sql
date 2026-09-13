-- 2026-09-12: redo OI-change-diff against the option's OWN price -- it was only ever tested
-- against the future's price (16/16 band-day combinations positive, the strongest finding before
-- PCR), predating the methodology correction to use option price directly. Same structure as the
-- CVD and PCR option-price checks: correlate against ATM call price and ATM put price separately,
-- requiring opposite signs as the coherence check for a real directional signal.
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
oi AS (
    SELECT b."AsOfDate", b."Timestamp", b."CadenceMinutes", b."BandDefinition",
        CASE WHEN bwm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label,
        (b."CallOiChangeSum" - b."PutOiChangeSum") AS oi_change_diff
    FROM "StrikeBandCadenceSnapshots" b
    JOIN band_week_map bwm ON bwm."AsOfDate" = b."AsOfDate" AND bwm."ExpiryDate" = b."ExpiryDate"
)
SELECT oi.week_label, oi."CadenceMinutes", oi."BandDefinition",
    COUNT(oi.oi_change_diff) AS n,
    ROUND(CORR(oi.oi_change_diff, callc.fwd_5m)::numeric,3) AS callprice_fwd5m,
    ROUND(CORR(oi.oi_change_diff, callc.fwd_15m)::numeric,3) AS callprice_fwd15m,
    ROUND(CORR(oi.oi_change_diff, putc.fwd_5m)::numeric,3) AS putprice_fwd5m,
    ROUND(CORR(oi.oi_change_diff, putc.fwd_15m)::numeric,3) AS putprice_fwd15m
FROM oi
JOIN changes callc ON callc."AsOfDate" = oi."AsOfDate" AND callc."Timestamp" = oi."Timestamp" AND callc.week_label = oi.week_label AND callc."OptionType" = 1
JOIN changes putc ON putc."AsOfDate" = oi."AsOfDate" AND putc."Timestamp" = oi."Timestamp" AND putc.week_label = oi.week_label AND putc."OptionType" = 2
GROUP BY 1,2,3
ORDER BY 1,2,3;
