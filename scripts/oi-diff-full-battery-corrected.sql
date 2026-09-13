-- 2026-09-12: OI-diff full corrected battery -- all 4 bands, both cadences, both weeks (checking
-- whether the ThisWeek-only scope restriction still looks justified once the bug is fixed),
-- contract AND notional, per-day. Strike-identity-safe/time-guarded template throughout.
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
oi AS (
    SELECT b."AsOfDate", b."Timestamp", b."CadenceMinutes", b."BandDefinition",
        CASE WHEN bwm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label,
        (b."CallOiChangeSum" - b."PutOiChangeSum")::float AS oi_diff_contract,
        (b."CallOiChangeNotionalSum" - b."PutOiChangeNotionalSum")::float AS oi_diff_notional
    FROM "StrikeBandCadenceSnapshots" b
    JOIN band_week_map bwm ON bwm."AsOfDate" = b."AsOfDate" AND bwm."ExpiryDate" = b."ExpiryDate"
)
SELECT oi."AsOfDate", oi.week_label, oi."CadenceMinutes", oi."BandDefinition",
    COUNT(oi.oi_diff_contract) AS n,
    ROUND(CORR(oi.oi_diff_contract, callc.fwd_5m)::numeric,3) AS contract_call_5m,
    ROUND(CORR(oi.oi_diff_contract, putc.fwd_5m)::numeric,3) AS contract_put_5m,
    ROUND(CORR(oi.oi_diff_contract, callc.fwd_15m)::numeric,3) AS contract_call_15m,
    ROUND(CORR(oi.oi_diff_contract, putc.fwd_15m)::numeric,3) AS contract_put_15m,
    ROUND(CORR(oi.oi_diff_notional, callc.fwd_5m)::numeric,3) AS notional_call_5m,
    ROUND(CORR(oi.oi_diff_notional, putc.fwd_5m)::numeric,3) AS notional_put_5m
FROM oi
JOIN changes callc ON callc."AsOfDate" = oi."AsOfDate" AND callc."Timestamp" = oi."Timestamp" AND callc.week_label = oi.week_label AND callc."OptionType" = 1
JOIN changes putc ON putc."AsOfDate" = oi."AsOfDate" AND putc."Timestamp" = oi."Timestamp" AND putc.week_label = oi.week_label AND putc."OptionType" = 2
GROUP BY 1,2,3,4
ORDER BY 2,3,4,1;
