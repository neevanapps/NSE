-- 2026-09-12: option depth imbalance vs FUTURE price -- closes the gap flagged by external review
-- (depth imbalance was only ever tested against the option's own price; every other Table 3
-- candidate got both targets checked). Future price forward-filled from CadenceContexts, same
-- time-guarded methodology used throughout. Per-day, all 4 bands, both cadences, both weeks.
WITH future_grouped AS (
    SELECT "AsOfDate", "Timestamp",
        COUNT("FutureCloseFromLastCadence") OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp") AS future_grp,
        "FutureCloseFromLastCadence"
    FROM "CadenceContexts"
),
future_filled AS (
    SELECT "AsOfDate", "Timestamp",
        FIRST_VALUE("FutureCloseFromLastCadence") OVER (PARTITION BY "AsOfDate", future_grp ORDER BY "Timestamp") AS future_ffilled
    FROM future_grouped
),
future_lead AS (
    SELECT *,
        LEAD(future_ffilled, 20) OVER w AS future_5m, LEAD("Timestamp", 20) OVER w AS ts_5m,
        LEAD(future_ffilled, 60) OVER w AS future_15m, LEAD("Timestamp", 60) OVER w AS ts_15m
    FROM future_filled
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
),
future_moves AS (
    SELECT "AsOfDate", "Timestamp",
        CASE WHEN ts_5m - "Timestamp" BETWEEN INTERVAL '4 minutes 30 seconds' AND INTERVAL '5 minutes 30 seconds'
             THEN future_5m - future_ffilled END AS fwd_5m,
        CASE WHEN ts_15m - "Timestamp" BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN future_15m - future_ffilled END AS fwd_15m
    FROM future_lead
),
band_week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeBandCadenceSnapshots") d
),
depth AS (
    SELECT b."AsOfDate", b."Timestamp", b."CadenceMinutes", b."BandDefinition",
        CASE WHEN bwm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label,
        (b."CallDepthImbalanceAvg" - b."PutDepthImbalanceAvg") AS depth_imbalance_diff
    FROM "StrikeBandCadenceSnapshots" b
    JOIN band_week_map bwm ON bwm."AsOfDate" = b."AsOfDate" AND bwm."ExpiryDate" = b."ExpiryDate"
)
SELECT d."AsOfDate", d.week_label, d."CadenceMinutes", d."BandDefinition",
    COUNT(d.depth_imbalance_diff) AS n,
    ROUND(CORR(d.depth_imbalance_diff, fm.fwd_5m)::numeric,3) AS future_fwd5m,
    ROUND(CORR(d.depth_imbalance_diff, fm.fwd_15m)::numeric,3) AS future_fwd15m
FROM depth d
JOIN future_moves fm ON fm."AsOfDate" = d."AsOfDate" AND fm."Timestamp" = d."Timestamp"
GROUP BY 1,2,3,4
ORDER BY 2,3,4,1;
