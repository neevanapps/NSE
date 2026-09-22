-- 2026-09-13: manual spot-check requested by user -- pick real rows from 10 Sep (ThisWeek,
-- Itm2Atm1, 5-min) and prove by hand that (a) the metric's own window ends AT boundary T using
-- only data BEFORE T, and (b) the forward price target genuinely starts AT T and looks ahead to
-- T+15m on the SAME physical contract throughout -- no ATM-splice, no overlap.

-- PART 1: the metric as stored -- CallVolumeSum/PutVolumeSum/pcr_vol_log at each boundary T
-- (ThisWeek only -- StrikeBandCadenceSnapshots carries a NextWeek row at the same boundary too).
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate", ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeBandCadenceSnapshots") d
)
SELECT b."Timestamp" AS boundary_T, b."CallVolumeSum", b."PutVolumeSum",
    ROUND(LN(b."CallVolumeSum"::float / NULLIF(b."PutVolumeSum",0))::numeric, 4) AS pcr_vol_log
FROM "StrikeBandCadenceSnapshots" b
JOIN week_map wm ON wm."AsOfDate" = b."AsOfDate" AND wm."ExpiryDate" = b."ExpiryDate" AND wm.week_rank = 1
WHERE b."AsOfDate" = '2026-09-10' AND b."CadenceMinutes" = 5 AND b."BandDefinition" = 'Itm2Atm1'
  AND b."Timestamp" BETWEEN '2026-09-10 10:00:00+05:30' AND '2026-09-10 10:20:00+05:30'
ORDER BY b."Timestamp";

-- PART 2: independently recompute CallVolumeSum from raw 15s rows, restricted to (T-5min, T] --
-- proves the persisted aggregate is a BACKWARD window ending at T, never peeking past T.
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate", ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
boundaries AS (
    SELECT DISTINCT "Timestamp" AS boundary_T
    FROM "StrikeBandCadenceSnapshots"
    WHERE "AsOfDate" = '2026-09-10' AND "CadenceMinutes" = 5 AND "BandDefinition" = 'Itm2Atm1'
      AND "Timestamp" BETWEEN '2026-09-10 10:00:00+05:30' AND '2026-09-10 10:20:00+05:30'
)
SELECT b.boundary_T,
    MIN(s."Timestamp") AS earliest_tick_in_window, MAX(s."Timestamp") AS latest_tick_in_window,
    COUNT(*) AS ticks_contributing,
    SUM(s."VolumeDelta") AS recomputed_call_volume_sum
FROM boundaries b
JOIN "StrikeCadenceSnapshots" s
    ON s."AsOfDate" = '2026-09-10'
   AND s."Timestamp" > b.boundary_T - INTERVAL '5 minutes' AND s."Timestamp" <= b.boundary_T
JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate" AND wm.week_rank = 1
WHERE s."OptionType" = 1 AND s."StrikeOffsetFromAtm" IN (-2,-1,0)
GROUP BY b.boundary_T
ORDER BY b.boundary_T;

-- PART 3a: NAIVE version (deliberately reproducing the splicing bug for demonstration) --
-- filters StrikeOffsetFromAtm=0 at each end independently, no StrikePrice partition. Watch the
-- StrikePrice column change between T and T+15m on the rows that go haywire.
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate", ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
atm_call AS (
    SELECT s."Timestamp", s."StrikePrice", s."MarkPrice"
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate" AND wm.week_rank = 1
    WHERE s."AsOfDate" = '2026-09-10' AND s."OptionType" = 1 AND s."StrikeOffsetFromAtm" = 0
)
SELECT a."Timestamp" AS boundary_T, a."StrikePrice" AS strike_at_T, a."MarkPrice" AS price_at_T,
    b."StrikePrice" AS strike_at_Tplus15, b."MarkPrice" AS price_15m_later,
    (a."StrikePrice" <> b."StrikePrice") AS strike_swapped,
    ROUND((b."MarkPrice" - a."MarkPrice")::numeric, 2) AS naive_fwd_15m
FROM atm_call a
JOIN atm_call b ON b."Timestamp" = a."Timestamp" + INTERVAL '15 minutes'
WHERE a."Timestamp" IN ('2026-09-10 10:00:00+05:30','2026-09-10 10:05:00+05:30',
                         '2026-09-10 10:10:00+05:30','2026-09-10 10:15:00+05:30','2026-09-10 10:20:00+05:30')
ORDER BY a."Timestamp";

-- PART 3b: CORRECTED version (the actual production template) -- forward-fill and LEAD
-- partitioned by StrikePrice, anchored only where StrikeOffsetFromAtm=0 at time T. Same 5
-- boundaries as Part 3a, side by side for comparison.
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate", ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
all_strikes AS (
    SELECT s."Timestamp", s."StrikePrice", s."StrikeOffsetFromAtm", s."MarkPrice"
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate" AND wm.week_rank = 1
    WHERE s."AsOfDate" = '2026-09-10' AND s."OptionType" = 1
),
with_lookahead AS (
    SELECT *,
        LEAD("MarkPrice", 60) OVER (PARTITION BY "StrikePrice" ORDER BY "Timestamp") AS price_15m_later,
        LEAD("Timestamp", 60) OVER (PARTITION BY "StrikePrice" ORDER BY "Timestamp") AS ts_15m_later
    FROM all_strikes
)
SELECT "Timestamp" AS boundary_T, "StrikePrice" AS anchor_strike, "MarkPrice" AS price_at_T,
    ts_15m_later, price_15m_later,
    ts_15m_later - "Timestamp" AS actual_elapsed,
    ROUND((price_15m_later - "MarkPrice")::numeric, 2) AS correct_fwd_15m
FROM with_lookahead
WHERE "StrikeOffsetFromAtm" = 0
  AND "Timestamp" IN ('2026-09-10 10:00:00+05:30','2026-09-10 10:05:00+05:30',
                       '2026-09-10 10:10:00+05:30','2026-09-10 10:15:00+05:30','2026-09-10 10:20:00+05:30')
ORDER BY "Timestamp";
