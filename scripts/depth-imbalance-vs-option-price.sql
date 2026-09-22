-- 2026-09-12: option-level depth imbalance correlation, first pass -- diff framing
-- (CallDepthImbalanceAvg - PutDepthImbalanceAvg), matching PCR/OI-diff's established coherence-
-- check methodology (opposite-signed call/put reaction = real signal). Uses the corrected,
-- strike-identity-safe forward window (partitioned by StrikePrice, time-guarded) established
-- during the CVD redo -- reused here since it's a general fix, not CVD-specific.
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
di AS (
    SELECT b."AsOfDate", b."Timestamp", b."CadenceMinutes",
        CASE WHEN bwm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label,
        (b."CallDepthImbalanceAvg" - b."PutDepthImbalanceAvg") AS di_diff
    FROM "StrikeBandCadenceSnapshots" b
    JOIN band_week_map bwm ON bwm."AsOfDate" = b."AsOfDate" AND bwm."ExpiryDate" = b."ExpiryDate"
    WHERE b."BandDefinition" = 'Itm2Atm1' AND b."CadenceMinutes" = 5
)
SELECT di.week_label, di."AsOfDate", COUNT(di.di_diff) AS n,
    ROUND(CORR(di.di_diff, callc.fwd_5m)::numeric,3) AS callprice_fwd5m,
    ROUND(CORR(di.di_diff, callc.fwd_15m)::numeric,3) AS callprice_fwd15m,
    ROUND(CORR(di.di_diff, putc.fwd_5m)::numeric,3) AS putprice_fwd5m,
    ROUND(CORR(di.di_diff, putc.fwd_15m)::numeric,3) AS putprice_fwd15m
FROM di
JOIN anchor_changes callc ON callc."AsOfDate" = di."AsOfDate" AND callc."Timestamp" = di."Timestamp" AND callc.week_label = di.week_label AND callc."OptionType" = 1
JOIN anchor_changes putc ON putc."AsOfDate" = di."AsOfDate" AND putc."Timestamp" = di."Timestamp" AND putc.week_label = di.week_label AND putc."OptionType" = 2
GROUP BY 1,2
ORDER BY 1,2;
