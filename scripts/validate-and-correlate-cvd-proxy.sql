-- Validate FutureCvdProxyThisCadence/CumulativeDay after repopulation, then correlate against
-- the future's own price change, same backward/forward methodology as the depth-imbalance check.

\echo '=== 1. Row counts (sanity: still 1500/day, 6000 total) ==='
SELECT "AsOfDate", COUNT(*) FROM "CadenceContexts" GROUP BY "AsOfDate" ORDER BY "AsOfDate";

\echo '=== 2. Null rates for the two new columns ==='
SELECT COUNT(*) AS total_rows,
       COUNT(*) - COUNT("FutureCvdProxyThisCadence") AS null_cadence_net,
       COUNT(*) - COUNT("FutureCvdProxyCumulativeDay") AS null_cumulative_net
FROM "CadenceContexts";

\echo '=== 3. Per-day null rate + range sanity ==='
SELECT "AsOfDate",
       COUNT(*) FILTER (WHERE "FutureCvdProxyThisCadence" IS NULL) AS null_cadence,
       MIN("FutureCvdProxyCumulativeDay") AS min_cumulative,
       MAX("FutureCvdProxyCumulativeDay") AS max_cumulative,
       (array_agg("FutureCvdProxyCumulativeDay" ORDER BY "Timestamp" DESC))[1] AS last_cumulative_of_day
FROM "CadenceContexts" GROUP BY "AsOfDate" ORDER BY "AsOfDate";

\echo '=== 4. Spot-check against the real tick example (11 Sep, cadence ending 13:55:15) ==='
\x on
SELECT "Timestamp", "FutureCloseFromLastCadence", "FutureVolumeDeltaThisCadence",
       "FutureCvdProxyThisCadence", "FutureCvdProxyCumulativeDay"
FROM "CadenceContexts"
WHERE "AsOfDate" = '2026-09-11' AND "Timestamp" >= '2026-09-11 13:55:00+05:30' AND "Timestamp" <= '2026-09-11 13:55:30+05:30'
ORDER BY "Timestamp";
\x off

\echo '=== 5. Cumulative really is monotonic-ish (increments match cadence net, no drift) ==='
SELECT "AsOfDate", COUNT(*) AS mismatches
FROM (
    SELECT "AsOfDate", "Timestamp", "FutureCvdProxyCumulativeDay",
           "FutureCvdProxyCumulativeDay" - LAG("FutureCvdProxyCumulativeDay") OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp") AS implied_delta,
           "FutureCvdProxyThisCadence"
    FROM "CadenceContexts"
    WHERE "FutureCvdProxyCumulativeDay" IS NOT NULL
) x
WHERE implied_delta IS NOT NULL AND "FutureCvdProxyThisCadence" IS NOT NULL AND implied_delta <> "FutureCvdProxyThisCadence"
GROUP BY "AsOfDate";

\echo '=== 6. Correlation: FutureCvdProxy vs future price, backward and forward, pooled ==='
WITH base AS (
    SELECT
        "AsOfDate", "Timestamp",
        "FutureCloseFromLastCadence" AS price,
        "FutureCvdProxyThisCadence" AS cvd_cadence,
        "FutureCvdProxyCumulativeDay" AS cvd_cum,
        LEAD("FutureCloseFromLastCadence", 4)  OVER w - "FutureCloseFromLastCadence" AS fwd_1m,
        LEAD("FutureCloseFromLastCadence", 20) OVER w - "FutureCloseFromLastCadence" AS fwd_5m,
        LEAD("FutureCloseFromLastCadence", 60) OVER w - "FutureCloseFromLastCadence" AS fwd_15m,
        "FutureCloseFromLastCadence" - LAG("FutureCloseFromLastCadence", 4)  OVER w AS bwd_1m,
        "FutureCloseFromLastCadence" - LAG("FutureCloseFromLastCadence", 20) OVER w AS bwd_5m,
        "FutureCloseFromLastCadence" - LAG("FutureCloseFromLastCadence", 60) OVER w AS bwd_15m
    FROM "CadenceContexts"
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
)
SELECT 'cvd_this_cadence' AS metric,
    COUNT(*) FILTER (WHERE cvd_cadence IS NOT NULL AND fwd_1m IS NOT NULL) AS n,
    ROUND(CORR(cvd_cadence, bwd_1m)::numeric, 3)  AS bwd_1m,
    ROUND(CORR(cvd_cadence, bwd_5m)::numeric, 3)  AS bwd_5m,
    ROUND(CORR(cvd_cadence, bwd_15m)::numeric, 3) AS bwd_15m,
    ROUND(CORR(cvd_cadence, fwd_1m)::numeric, 3)  AS fwd_1m,
    ROUND(CORR(cvd_cadence, fwd_5m)::numeric, 3)  AS fwd_5m,
    ROUND(CORR(cvd_cadence, fwd_15m)::numeric, 3) AS fwd_15m
FROM base
UNION ALL
SELECT 'cvd_cumulative_day',
    COUNT(*) FILTER (WHERE cvd_cum IS NOT NULL AND fwd_1m IS NOT NULL),
    ROUND(CORR(cvd_cum, bwd_1m)::numeric, 3),
    ROUND(CORR(cvd_cum, bwd_5m)::numeric, 3),
    ROUND(CORR(cvd_cum, bwd_15m)::numeric, 3),
    ROUND(CORR(cvd_cum, fwd_1m)::numeric, 3),
    ROUND(CORR(cvd_cum, fwd_5m)::numeric, 3),
    ROUND(CORR(cvd_cum, fwd_15m)::numeric, 3)
FROM base;

\echo '=== 7. Per day (5-min horizon) -- is either version consistent across days? ==='
WITH base AS (
    SELECT "AsOfDate", "FutureCvdProxyThisCadence" AS cvd_cadence, "FutureCvdProxyCumulativeDay" AS cvd_cum,
        LEAD("FutureCloseFromLastCadence", 20) OVER w - "FutureCloseFromLastCadence" AS fwd_5m,
        "FutureCloseFromLastCadence" - LAG("FutureCloseFromLastCadence", 20) OVER w AS bwd_5m
    FROM "CadenceContexts"
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
)
SELECT "AsOfDate",
    ROUND(CORR(cvd_cadence, bwd_5m)::numeric, 3) AS cadence_bwd_5m,
    ROUND(CORR(cvd_cadence, fwd_5m)::numeric, 3) AS cadence_fwd_5m,
    ROUND(CORR(cvd_cum, bwd_5m)::numeric, 3) AS cumulative_bwd_5m,
    ROUND(CORR(cvd_cum, fwd_5m)::numeric, 3) AS cumulative_fwd_5m
FROM base GROUP BY "AsOfDate" ORDER BY "AsOfDate";
