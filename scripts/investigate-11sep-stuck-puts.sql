-- 2026-09-13: diagnostic for the Core score's three big losing Put trades on 11 Sep (8-metric
-- composite, threshold=20): 10:02:45-10:59:00, 11:00:45-13:05:30, 13:55:45-15:15:00 (ForceClose).
-- All three show sustained adverse excursion (MFE near 0, MAE -20 to -36) held far longer than any
-- winning trade that day -- consistent with the hysteresis exit "getting stuck" because the score
-- never crossed back above the opposite (+20) threshold despite the future running hard against
-- the position the whole time. Summarize the future's own price path and FutureCvdProxyNet5Min's
-- sign balance across each stuck window -- if the future ran up hard while CVD stayed net negative
-- (or near 50/50) throughout, that's consistent with the reversion-flavored terms (TrendReversion,
-- BasisChange) and/or CVD staying stuck bearish/neutral against an actual sustained uptrend.
WITH windows AS (
    SELECT * FROM (VALUES
        ('trade1', '10:02:00'::time, '11:00:00'::time),
        ('trade2', '11:00:00'::time, '13:06:00'::time),
        ('trade3', '13:55:00'::time, '15:16:00'::time)
    ) AS w(label, start_t, end_t)
),
tagged AS (
    SELECT cc.*, w.label,
        (cc."Timestamp" AT TIME ZONE 'Asia/Kolkata')::time AS ist_time
    FROM "CadenceContexts" cc
    JOIN windows w ON true
    WHERE cc."AsOfDate" = '2026-09-11'
      AND (cc."Timestamp" AT TIME ZONE 'Asia/Kolkata')::time BETWEEN w.start_t AND w.end_t
)
SELECT
    label,
    COUNT(*) AS cadences,
    MIN(ist_time) AS window_start,
    MAX(ist_time) AS window_end,
    (array_agg("FutureCloseFromLastCadence" ORDER BY "Timestamp" ASC))[1] AS future_at_start,
    (array_agg("FutureCloseFromLastCadence" ORDER BY "Timestamp" DESC))[1] AS future_at_end,
    (array_agg("FutureCloseFromLastCadence" ORDER BY "Timestamp" DESC))[1]
      - (array_agg("FutureCloseFromLastCadence" ORDER BY "Timestamp" ASC))[1] AS future_net_move,
    ROUND(AVG("FutureCvdProxyNet5Min")::numeric, 1) AS avg_cvd_net5min,
    ROUND(100.0 * COUNT(*) FILTER (WHERE "FutureCvdProxyNet5Min" > 0) / NULLIF(COUNT(*) FILTER (WHERE "FutureCvdProxyNet5Min" IS NOT NULL), 0), 1) AS pct_cadences_cvd_positive,
    ROUND(AVG("FutureChangeFromLastCadence")::numeric, 3) AS avg_future_change_per_cadence,
    ROUND(AVG("SpotChangeFromLastCadence")::numeric, 3) AS avg_spot_change_per_cadence
FROM tagged
GROUP BY label
ORDER BY label;
