-- 2026-09-12: Volume/OI turnover ratio (same-side activity intensity, NOT a call-vs-put comparison
-- -- see docs/SCORE_CANDIDATES.md discussion). Per side: CallVolumeSum/CallOiSum,
-- PutVolumeSum/PutOiSum, correlated against the MAGNITUDE (abs) of that same side's own forward
-- price change, not the signed change -- turnover has no inherent sign, so the question being
-- tested is "does high turnover coincide with bigger moves," not "does it predict direction."
-- Same strike-identity-safe/time-guarded forward-window template as every prior option-price
-- correlation this session (cvd-redo-touch-rule.sql origin), reused unmodified for the price side.
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
        LEAD(mark_ffilled, 20) OVER w AS price_5m_later,
        LEAD("Timestamp", 20) OVER w AS ts_5m_later,
        LEAD(mark_ffilled, 60) OVER w AS price_15m_later,
        LEAD("Timestamp", 60) OVER w AS ts_15m_later
    FROM filled
    WINDOW w AS (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice" ORDER BY "Timestamp")
),
anchor_changes AS (
    SELECT "AsOfDate", "Timestamp", week_label, "OptionType",
        CASE WHEN ts_5m_later - "Timestamp" BETWEEN INTERVAL '4 minutes 30 seconds' AND INTERVAL '5 minutes 30 seconds'
             THEN ABS(price_5m_later - mark_ffilled) END AS abs_fwd_5m,
        CASE WHEN ts_15m_later - "Timestamp" BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN ABS(price_15m_later - mark_ffilled) END AS abs_fwd_15m
    FROM with_lookahead
    WHERE "StrikeOffsetFromAtm" = 0
),
band_week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeBandCadenceSnapshots") d
),
turnover AS (
    SELECT b."AsOfDate", b."Timestamp", b."BandDefinition", b."CadenceMinutes",
        CASE WHEN bwm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label,
        CASE WHEN b."CallOiSum" > 0 THEN b."CallVolumeSum"::float / b."CallOiSum" END AS call_turnover,
        CASE WHEN b."PutOiSum" > 0 THEN b."PutVolumeSum"::float / b."PutOiSum" END AS put_turnover
    FROM "StrikeBandCadenceSnapshots" b
    JOIN band_week_map bwm ON bwm."AsOfDate" = b."AsOfDate" AND bwm."ExpiryDate" = b."ExpiryDate"
)
SELECT t."CadenceMinutes", t.week_label, t."BandDefinition",
    COUNT(t.call_turnover) AS n,
    ROUND(CORR(t.call_turnover, callc.abs_fwd_5m)::numeric,3) AS call_turnover_vs_absfwd5m,
    ROUND(CORR(t.call_turnover, callc.abs_fwd_15m)::numeric,3) AS call_turnover_vs_absfwd15m,
    ROUND(CORR(t.put_turnover, putc.abs_fwd_5m)::numeric,3) AS put_turnover_vs_absfwd5m,
    ROUND(CORR(t.put_turnover, putc.abs_fwd_15m)::numeric,3) AS put_turnover_vs_absfwd15m
FROM turnover t
JOIN anchor_changes callc ON callc."AsOfDate" = t."AsOfDate" AND callc."Timestamp" = t."Timestamp" AND callc.week_label = t.week_label AND callc."OptionType" = 1
JOIN anchor_changes putc ON putc."AsOfDate" = t."AsOfDate" AND putc."Timestamp" = t."Timestamp" AND putc.week_label = t.week_label AND putc."OptionType" = 2
GROUP BY 1,2,3
ORDER BY 1,2,3;
