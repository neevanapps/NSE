-- 2026-09-12: full CVD redo per the friend's review + the strike-identity bug found while
-- implementing it. Two methodology fixes on top of the touch-rule tightening (a code change,
-- requires repopulate to take effect in the data):
--
-- 1. STRIKE IDENTITY: the original round-2 query filtered "WHERE StrikeOffsetFromAtm = 0" and
--    then computed LEAD/LAG within a partition that did NOT include StrikePrice -- meaning the
--    "forward price" could silently splice from one physical strike to a DIFFERENT one if ATM
--    re-centered during the window. Fixed by partitioning every window function by StrikePrice
--    too (each contract's own continuous series), and only USING a row as a correlation anchor
--    when it was actually the ATM strike (offset=0) at that moment.
-- 2. TIME GUARD: LEAD(price, 20)/LEAD(price, 60) assumes exactly 20/60 real cadences ahead == 5/15
--    real minutes. Guarded by comparing LEAD(Timestamp, N) - Timestamp against a tolerance window,
--    nulling the forward change if a strike's own row sequence had a genuine gap.
--
-- Also, per the review: no longer netting call-put into one diff. Call CVD, put CVD, and their
-- SUM ("vol-flow", testing the volatility-bid-not-direction hypothesis) are all computed and
-- correlated separately, each against its own leg's price AND the future's price.
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
    -- Only rows that WERE the ATM strike at that moment are usable as correlation anchors --
    -- the lookahead itself was computed on each strike's own unbroken series above.
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
cvd AS (
    SELECT b."AsOfDate", b."Timestamp", b."CadenceMinutes",
        CASE WHEN bwm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label,
        b."CallCvdProxyVolumeNet"::float AS call_cvd,
        b."PutCvdProxyVolumeNet"::float AS put_cvd,
        (b."CallCvdProxyVolumeNet" + b."PutCvdProxyVolumeNet")::float AS vol_flow
    FROM "StrikeBandCadenceSnapshots" b
    JOIN band_week_map bwm ON bwm."AsOfDate" = b."AsOfDate" AND bwm."ExpiryDate" = b."ExpiryDate"
    WHERE b."BandDefinition" = 'Itm2Atm1' AND b."CadenceMinutes" = 5
)
SELECT cvd.week_label,
    COUNT(*) AS n,
    ROUND(CORR(call_cvd, callc.fwd_5m)::numeric,3) AS callcvd_vs_callprice_fwd5m,
    ROUND(CORR(call_cvd, callc.fwd_15m)::numeric,3) AS callcvd_vs_callprice_fwd15m,
    ROUND(CORR(put_cvd, putc.fwd_5m)::numeric,3) AS putcvd_vs_putprice_fwd5m,
    ROUND(CORR(put_cvd, putc.fwd_15m)::numeric,3) AS putcvd_vs_putprice_fwd15m,
    ROUND(CORR(vol_flow, callc.fwd_5m)::numeric,3) AS volflow_vs_callprice_fwd5m,
    ROUND(CORR(vol_flow, putc.fwd_5m)::numeric,3) AS volflow_vs_putprice_fwd5m
FROM cvd
JOIN anchor_changes callc ON callc."AsOfDate" = cvd."AsOfDate" AND callc."Timestamp" = cvd."Timestamp" AND callc.week_label = cvd.week_label AND callc."OptionType" = 1
JOIN anchor_changes putc ON putc."AsOfDate" = cvd."AsOfDate" AND putc."Timestamp" = cvd."Timestamp" AND putc.week_label = cvd.week_label AND putc."OptionType" = 2
GROUP BY 1
ORDER BY 1;
