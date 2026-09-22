-- 2026-09-12: PCR-OI/OI-diff redundancy check -- the finalization question delegated by the user
-- ("I will let you decide pcr-oi/oi and finalise it first."). Cross-correlates the two metrics
-- directly against EACH OTHER (not against option price) to determine whether PCR-OI carries
-- information distinct from OI-diff, or is effectively the same signal restated.
--
-- Two checks, same day/band/cadence (ThisWeek, Itm2Atm1, 5-min):
--   1. Level-vs-level: pcr_oi_log against raw OiDiff.
--   2. Delta-vs-delta: PCR-OI's own bar-to-bar change against OiDiff -- the more theoretically
--      apt comparison, since OiDiff IS the flow that mechanically moves PCR-OI's OI stock. If
--      PCR-OI's delta doesn't track OiDiff, the two are NOT mechanically the same signal.
WITH band_week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeBandCadenceSnapshots") d
),
metrics AS (
    SELECT b."AsOfDate", b."Timestamp",
        CASE WHEN bwm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label,
        CASE WHEN b."CallOiSum" > 0 AND b."PutOiSum" > 0 THEN LN(b."CallOiSum"::float / b."PutOiSum") END AS pcr_oi_log,
        (b."CallOiChangeSum" - b."PutOiChangeSum")::float AS oi_diff
    FROM "StrikeBandCadenceSnapshots" b
    JOIN band_week_map bwm ON bwm."AsOfDate" = b."AsOfDate" AND bwm."ExpiryDate" = b."ExpiryDate"
    WHERE b."CadenceMinutes" = 5 AND b."BandDefinition" = 'Itm2Atm1'
),
with_delta AS (
    SELECT *,
        pcr_oi_log - LAG(pcr_oi_log) OVER (PARTITION BY "AsOfDate", week_label ORDER BY "Timestamp") AS pcr_oi_delta
    FROM metrics
    WHERE week_label = 'ThisWeek'
)
SELECT "AsOfDate", COUNT(*) AS n,
    ROUND(CORR(pcr_oi_log, oi_diff)::numeric,3) AS pcroi_level_vs_oidiff,
    ROUND(CORR(pcr_oi_delta, oi_diff)::numeric,3) AS pcroi_delta_vs_oidiff
FROM with_delta
GROUP BY 1
ORDER BY 1;
