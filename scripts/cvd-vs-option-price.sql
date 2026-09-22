-- 2026-09-12: redo the CVD correlation against the OPTION's own price, not the future's -- the
-- future was only ever a stand-in used because no per-strike price table existed at the time.
-- Rewritten for performance: week_label resolved once via a small per-day lookup, not a
-- correlated subquery re-executed per row (the original version took minutes; this should be
-- near-instant on ~24k ATM rows).
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
cvd AS (
    SELECT b."AsOfDate", b."Timestamp", b."CadenceMinutes",
        CASE WHEN bwm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label,
        (b."CallCvdProxyVolumeNet" - b."PutCvdProxyVolumeNet") AS cvd_vol_diff,
        (b."CallCvdProxyNotionalNet" - b."PutCvdProxyNotionalNet") AS cvd_notional_diff
    FROM "StrikeBandCadenceSnapshots" b
    JOIN band_week_map bwm ON bwm."AsOfDate" = b."AsOfDate" AND bwm."ExpiryDate" = b."ExpiryDate"
    WHERE b."BandDefinition" = 'Strike7'
)
SELECT cvd.week_label, cvd."CadenceMinutes",
    COUNT(*) AS n,
    ROUND(CORR(cvd.cvd_vol_diff, callc.fwd_5m)::numeric,3) AS vol_vs_callprice_fwd5m,
    ROUND(CORR(cvd.cvd_vol_diff, callc.fwd_15m)::numeric,3) AS vol_vs_callprice_fwd15m,
    ROUND(CORR(cvd.cvd_vol_diff, putc.fwd_5m)::numeric,3) AS vol_vs_putprice_fwd5m,
    ROUND(CORR(cvd.cvd_vol_diff, putc.fwd_15m)::numeric,3) AS vol_vs_putprice_fwd15m,
    ROUND(CORR(cvd.cvd_notional_diff, callc.fwd_5m)::numeric,3) AS notional_vs_callprice_fwd5m,
    ROUND(CORR(cvd.cvd_notional_diff, callc.fwd_15m)::numeric,3) AS notional_vs_callprice_fwd15m,
    ROUND(CORR(cvd.cvd_notional_diff, putc.fwd_5m)::numeric,3) AS notional_vs_putprice_fwd5m,
    ROUND(CORR(cvd.cvd_notional_diff, putc.fwd_15m)::numeric,3) AS notional_vs_putprice_fwd15m
FROM cvd
JOIN changes callc ON callc."AsOfDate" = cvd."AsOfDate" AND callc."Timestamp" = cvd."Timestamp" AND callc.week_label = cvd.week_label AND callc."OptionType" = 1
JOIN changes putc ON putc."AsOfDate" = cvd."AsOfDate" AND putc."Timestamp" = cvd."Timestamp" AND putc.week_label = cvd.week_label AND putc."OptionType" = 2
GROUP BY 1,2
ORDER BY 1,2;
