-- 2026-09-12: PCR-OI (Call OI / Put OI, the classic level-based put/call ratio, distinct from the
-- volume-based PCR already confirmed and from OI-diff's change-based framing) -- same log-ratio
-- convention, same strike-identity-safe/time-guarded forward window established during the CVD
-- redo, same coherence check (call price up, put price down for a real signal).
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
        LEAD(mark_ffilled, 60) OVER w AS price_15m_later,
        LEAD("Timestamp", 60) OVER w AS ts_15m_later
    FROM filled
    WINDOW w AS (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice" ORDER BY "Timestamp")
),
anchor_changes AS (
    SELECT "AsOfDate", "Timestamp", week_label, "OptionType",
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
pcr_oi AS (
    SELECT b."AsOfDate", b."Timestamp", b."BandDefinition",
        CASE WHEN bwm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label,
        CASE WHEN b."CallOiSum" > 0 AND b."PutOiSum" > 0 THEN LN(b."CallOiSum"::float / b."PutOiSum") END AS pcr_oi_log
    FROM "StrikeBandCadenceSnapshots" b
    JOIN band_week_map bwm ON bwm."AsOfDate" = b."AsOfDate" AND bwm."ExpiryDate" = b."ExpiryDate"
    WHERE b."CadenceMinutes" = 5
)
SELECT pcr_oi.week_label, pcr_oi."BandDefinition",
    COUNT(pcr_oi.pcr_oi_log) AS n,
    ROUND(CORR(pcr_oi.pcr_oi_log, callc.fwd_15m)::numeric,3) AS callprice_fwd15m,
    ROUND(CORR(pcr_oi.pcr_oi_log, putc.fwd_15m)::numeric,3) AS putprice_fwd15m
FROM pcr_oi
JOIN anchor_changes callc ON callc."AsOfDate" = pcr_oi."AsOfDate" AND callc."Timestamp" = pcr_oi."Timestamp" AND callc.week_label = pcr_oi.week_label AND callc."OptionType" = 1
JOIN anchor_changes putc ON putc."AsOfDate" = pcr_oi."AsOfDate" AND putc."Timestamp" = pcr_oi."Timestamp" AND putc.week_label = pcr_oi.week_label AND putc."OptionType" = 2
GROUP BY 1,2
ORDER BY 1,2;
