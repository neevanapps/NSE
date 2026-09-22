-- Validate FutureCvdProxyNet5Min/Net15Min after repopulation, then correlate against price --
-- specifically checking whether isolating recent flow fixes the day-long cumulative version's
-- sign-flip problem found in the previous round.

\echo '=== 1. Row counts ==='
SELECT "AsOfDate", COUNT(*) FROM "CadenceContexts" GROUP BY "AsOfDate" ORDER BY "AsOfDate";

\echo '=== 2. Null rates for the two new rolling columns, and warm-up timing ==='
SELECT COUNT(*) AS total_rows,
       COUNT(*) - COUNT("FutureCvdProxyNet5Min") AS null_5min,
       COUNT(*) - COUNT("FutureCvdProxyNet15Min") AS null_15min
FROM "CadenceContexts";

SELECT "AsOfDate",
       MIN("Timestamp") FILTER (WHERE "FutureCvdProxyNet5Min" IS NOT NULL) AS first_5min_warm,
       MIN("Timestamp") FILTER (WHERE "FutureCvdProxyNet15Min" IS NOT NULL) AS first_15min_warm,
       MIN("Timestamp") AS day_start
FROM "CadenceContexts" GROUP BY "AsOfDate" ORDER BY "AsOfDate";

\echo '=== 3. Range sanity (no absurd outliers) ==='
SELECT "AsOfDate",
       MIN("FutureCvdProxyNet5Min") AS min_5min, MAX("FutureCvdProxyNet5Min") AS max_5min,
       MIN("FutureCvdProxyNet15Min") AS min_15min, MAX("FutureCvdProxyNet15Min") AS max_15min
FROM "CadenceContexts" GROUP BY "AsOfDate" ORDER BY "AsOfDate";

\echo '=== 4. Correlation vs future price, backward and forward, pooled -- all four CVD variants together ==='
WITH base AS (
    SELECT
        "AsOfDate", "Timestamp",
        "FutureCvdProxyThisCadence" AS cvd_cadence,
        "FutureCvdProxyCumulativeDay" AS cvd_cum,
        "FutureCvdProxyNet5Min" AS cvd_5m,
        "FutureCvdProxyNet15Min" AS cvd_15m,
        LEAD("FutureCloseFromLastCadence", 4)  OVER w - "FutureCloseFromLastCadence" AS fwd_1m,
        LEAD("FutureCloseFromLastCadence", 20) OVER w - "FutureCloseFromLastCadence" AS fwd_5m,
        LEAD("FutureCloseFromLastCadence", 60) OVER w - "FutureCloseFromLastCadence" AS fwd_15m,
        "FutureCloseFromLastCadence" - LAG("FutureCloseFromLastCadence", 4)  OVER w AS bwd_1m,
        "FutureCloseFromLastCadence" - LAG("FutureCloseFromLastCadence", 20) OVER w AS bwd_5m,
        "FutureCloseFromLastCadence" - LAG("FutureCloseFromLastCadence", 60) OVER w AS bwd_15m
    FROM "CadenceContexts"
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
)
SELECT 'cvd_cadence' AS metric, COUNT(*) FILTER (WHERE cvd_cadence IS NOT NULL AND fwd_1m IS NOT NULL) AS n,
    ROUND(CORR(cvd_cadence, bwd_1m)::numeric,3) bwd_1m, ROUND(CORR(cvd_cadence, bwd_5m)::numeric,3) bwd_5m, ROUND(CORR(cvd_cadence, bwd_15m)::numeric,3) bwd_15m,
    ROUND(CORR(cvd_cadence, fwd_1m)::numeric,3) fwd_1m, ROUND(CORR(cvd_cadence, fwd_5m)::numeric,3) fwd_5m, ROUND(CORR(cvd_cadence, fwd_15m)::numeric,3) fwd_15m
FROM base
UNION ALL
SELECT 'cvd_cumulative_day', COUNT(*) FILTER (WHERE cvd_cum IS NOT NULL AND fwd_1m IS NOT NULL),
    ROUND(CORR(cvd_cum, bwd_1m)::numeric,3), ROUND(CORR(cvd_cum, bwd_5m)::numeric,3), ROUND(CORR(cvd_cum, bwd_15m)::numeric,3),
    ROUND(CORR(cvd_cum, fwd_1m)::numeric,3), ROUND(CORR(cvd_cum, fwd_5m)::numeric,3), ROUND(CORR(cvd_cum, fwd_15m)::numeric,3)
FROM base
UNION ALL
SELECT 'cvd_net_5min', COUNT(*) FILTER (WHERE cvd_5m IS NOT NULL AND fwd_1m IS NOT NULL),
    ROUND(CORR(cvd_5m, bwd_1m)::numeric,3), ROUND(CORR(cvd_5m, bwd_5m)::numeric,3), ROUND(CORR(cvd_5m, bwd_15m)::numeric,3),
    ROUND(CORR(cvd_5m, fwd_1m)::numeric,3), ROUND(CORR(cvd_5m, fwd_5m)::numeric,3), ROUND(CORR(cvd_5m, fwd_15m)::numeric,3)
FROM base
UNION ALL
SELECT 'cvd_net_15min', COUNT(*) FILTER (WHERE cvd_15m IS NOT NULL AND fwd_1m IS NOT NULL),
    ROUND(CORR(cvd_15m, bwd_1m)::numeric,3), ROUND(CORR(cvd_15m, bwd_5m)::numeric,3), ROUND(CORR(cvd_15m, bwd_15m)::numeric,3),
    ROUND(CORR(cvd_15m, fwd_1m)::numeric,3), ROUND(CORR(cvd_15m, fwd_5m)::numeric,3), ROUND(CORR(cvd_15m, fwd_15m)::numeric,3)
FROM base;

\echo '=== 5. Per-day (5-min horizon) -- is the rolling version consistently signed across days now? ==='
WITH base AS (
    SELECT "AsOfDate", "FutureCvdProxyNet5Min" AS cvd_5m, "FutureCvdProxyNet15Min" AS cvd_15m,
        LEAD("FutureCloseFromLastCadence", 20) OVER w - "FutureCloseFromLastCadence" AS fwd_5m,
        "FutureCloseFromLastCadence" - LAG("FutureCloseFromLastCadence", 20) OVER w AS bwd_5m
    FROM "CadenceContexts"
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
)
SELECT "AsOfDate",
    ROUND(CORR(cvd_5m, bwd_5m)::numeric,3) AS net5m_bwd_5m, ROUND(CORR(cvd_5m, fwd_5m)::numeric,3) AS net5m_fwd_5m,
    ROUND(CORR(cvd_15m, bwd_5m)::numeric,3) AS net15m_bwd_5m, ROUND(CORR(cvd_15m, fwd_5m)::numeric,3) AS net15m_fwd_5m
FROM base GROUP BY "AsOfDate" ORDER BY "AsOfDate";
