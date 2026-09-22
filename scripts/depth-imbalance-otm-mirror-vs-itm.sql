-- 2026-09-16 -- Re-test of audit finding F-A: does DepthImbalance's Itm2Atm1 band carry the same
-- geometric artifact that just broke ItmSkew (comparing ITM calls, below spot, against ITM puts,
-- above spot -- different points on the strike axis)?
--
-- Two checks, both against the existing corrected strike-identity-safe/time-guarded methodology
-- from scripts/depth-imbalance-vs-option-price.sql:
--   (1) Re-run Itm2Atm1 vs the already-persisted, genuinely symmetric Strike5 band (ATM+/-2 on
--       BOTH call and put sides), independently, across all 5 days now available (adds 15 Sep,
--       not in the original 4-day finding).
--   (2) Build the true OTM-mirror band -- calls from offset 0..2 (OTM), puts from offset -2..0
--       (OTM) -- the EXACT geometric opposite of Itm2Atm1's construction (calls -2..0, puts 0..2).
--       This isolates the same ITM-vs-OTM flip that changed ItmSkew's sign, applied to
--       DepthImbalance instead. No persisted band exists for this shape, so it's built directly
--       from StrikeCadenceSnapshots, reusing Itm2Atm1's own persisted 5-min bucket timestamps as
--       the bucket grid (so results join cleanly against the same anchor prices) rather than
--       re-deriving the bucketing scheme from scratch.

\echo '=== (1) Itm2Atm1 vs Strike5 (symmetric), all 5 days, ThisWeek, fwd 5m/15m ==='
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
    SELECT b."AsOfDate", b."Timestamp", b."BandDefinition",
        CASE WHEN bwm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label,
        (b."CallDepthImbalanceAvg" - b."PutDepthImbalanceAvg") AS di_diff
    FROM "StrikeBandCadenceSnapshots" b
    JOIN band_week_map bwm ON bwm."AsOfDate" = b."AsOfDate" AND bwm."ExpiryDate" = b."ExpiryDate"
    WHERE b."BandDefinition" IN ('Itm2Atm1', 'Strike5') AND b."CadenceMinutes" = 5
)
SELECT di."BandDefinition", di.week_label, di."AsOfDate", COUNT(di.di_diff) AS n,
    ROUND(CORR(di.di_diff, callc.fwd_5m)::numeric,3)  AS callprice_fwd5m,
    ROUND(CORR(di.di_diff, callc.fwd_15m)::numeric,3) AS callprice_fwd15m,
    ROUND(CORR(di.di_diff, putc.fwd_5m)::numeric,3)   AS putprice_fwd5m,
    ROUND(CORR(di.di_diff, putc.fwd_15m)::numeric,3)  AS putprice_fwd15m
FROM di
JOIN anchor_changes callc ON callc."AsOfDate" = di."AsOfDate" AND callc."Timestamp" = di."Timestamp" AND callc.week_label = di.week_label AND callc."OptionType" = 1
JOIN anchor_changes putc  ON putc."AsOfDate"  = di."AsOfDate" AND putc."Timestamp"  = di."Timestamp" AND putc.week_label  = di.week_label AND putc."OptionType"  = 2
WHERE di.week_label = 'ThisWeek'
GROUP BY 1,2,3
ORDER BY 1,2,3;

\echo ''
\echo '=== (2) True OTM-mirror band (calls 0..2, puts -2..0) vs Itm2Atm1 -- exact geometric flip ==='
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
anchor_changes AS (
    SELECT "AsOfDate", "Timestamp", week_label, "OptionType",
        CASE WHEN ts_5m_later - "Timestamp" BETWEEN INTERVAL '4 minutes 30 seconds' AND INTERVAL '5 minutes 30 seconds'
             THEN price_5m_later - mark_ffilled END AS fwd_5m,
        CASE WHEN ts_15m_later - "Timestamp" BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN price_15m_later - mark_ffilled END AS fwd_15m
    FROM with_lookahead
    WHERE "StrikeOffsetFromAtm" = 0
),
-- Bucket grid borrowed from Itm2Atm1's own persisted 5-min Timestamps (ThisWeek only) so the
-- OTM-mirror band's bucket boundaries line up exactly with the anchor prices above -- not a
-- re-derivation of the bucketing scheme, just reuse of an existing, already-correct grid.
band_week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeBandCadenceSnapshots") d
),
bucket_grid AS (
    SELECT DISTINCT b."AsOfDate", b."Timestamp"
    FROM "StrikeBandCadenceSnapshots" b
    JOIN band_week_map bwm ON bwm."AsOfDate" = b."AsOfDate" AND bwm."ExpiryDate" = b."ExpiryDate"
    WHERE b."BandDefinition" = 'Itm2Atm1' AND b."CadenceMinutes" = 5 AND bwm.week_rank = 1
),
otm_strikes AS (
    -- Same ThisWeek-only, 15s raw rows, restricted to the OTM-mirror offsets.
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."DepthImbalanceFromLastCadence"
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate" AND wm.week_rank = 1
    WHERE (s."OptionType" = 1 AND s."StrikeOffsetFromAtm" BETWEEN 0 AND 2)   -- calls: OTM (mirrors Itm2Atm1's ITM -2..0)
       OR (s."OptionType" = 2 AND s."StrikeOffsetFromAtm" BETWEEN -2 AND 0) -- puts: OTM (mirrors Itm2Atm1's ITM 0..2)
),
bucketed AS (
    SELECT g."AsOfDate", g."Timestamp" AS bucket_ts,
        AVG(o."DepthImbalanceFromLastCadence") FILTER (WHERE o."OptionType" = 1) AS call_avg,
        AVG(o."DepthImbalanceFromLastCadence") FILTER (WHERE o."OptionType" = 2) AS put_avg
    FROM bucket_grid g
    JOIN otm_strikes o ON o."AsOfDate" = g."AsOfDate"
        AND o."Timestamp" > g."Timestamp" - INTERVAL '5 minutes' AND o."Timestamp" <= g."Timestamp"
    GROUP BY 1,2
)
SELECT 'OtmMirror' AS "BandDefinition", 'ThisWeek' AS week_label, di."AsOfDate", COUNT(*) AS n,
    ROUND(CORR(di.call_avg - di.put_avg, callc.fwd_5m)::numeric,3)  AS callprice_fwd5m,
    ROUND(CORR(di.call_avg - di.put_avg, callc.fwd_15m)::numeric,3) AS callprice_fwd15m,
    ROUND(CORR(di.call_avg - di.put_avg, putc.fwd_5m)::numeric,3)   AS putprice_fwd5m,
    ROUND(CORR(di.call_avg - di.put_avg, putc.fwd_15m)::numeric,3)  AS putprice_fwd15m
FROM bucketed di
JOIN anchor_changes callc ON callc."AsOfDate" = di."AsOfDate" AND callc."Timestamp" = di.bucket_ts AND callc.week_label = 'ThisWeek' AND callc."OptionType" = 1
JOIN anchor_changes putc  ON putc."AsOfDate"  = di."AsOfDate" AND putc."Timestamp"  = di.bucket_ts AND putc.week_label  = 'ThisWeek' AND putc."OptionType"  = 2
WHERE di.call_avg IS NOT NULL AND di.put_avg IS NOT NULL
GROUP BY di."AsOfDate"
ORDER BY di."AsOfDate";
