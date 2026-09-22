-- 2026-09-12: quick test to decide (a) whether trend efficiency should be scored as a directional
-- (continuation) signal or treated as a non-directional volatility/confidence read, and (b) whether
-- VIX change has real forward (not just contemporaneous) predictive correlation with future price,
-- since a score input needs to predict what happens AFTER it's computed, not just describe what
-- already happened. Trailing window = 15 minutes (matches FutureDirectSimulator's own default).
WITH base AS (
    SELECT "AsOfDate", "Timestamp", "FutureChangeFromLastCadence" AS chg,
           "VixChangeFromLastCadence" AS vix_chg
    FROM "CadenceContexts"
    WHERE "FutureChangeFromLastCadence" IS NOT NULL
),
windowed AS (
    SELECT *,
        SUM(chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN INTERVAL '15 minutes' PRECEDING AND CURRENT ROW) AS trail_net_15m,
        SUM(ABS(chg)) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN INTERVAL '15 minutes' PRECEDING AND CURRENT ROW) AS trail_path_15m,
        SUM(chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN CURRENT ROW AND INTERVAL '5 minutes' FOLLOWING) - chg AS fwd_5m,
        SUM(chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN CURRENT ROW AND INTERVAL '15 minutes' FOLLOWING) - chg AS fwd_15m
    FROM base
),
scored AS (
    SELECT *,
        CASE WHEN trail_path_15m > 0 THEN trail_net_15m / trail_path_15m END AS signed_efficiency
    FROM windowed
)
SELECT
    'trend efficiency: signed (continuation) vs fwd 5m' AS test, "AsOfDate",
    COUNT(*) FILTER (WHERE signed_efficiency IS NOT NULL AND fwd_5m IS NOT NULL) AS n,
    ROUND(CORR(signed_efficiency, fwd_5m)::numeric, 3) AS corr
FROM scored GROUP BY "AsOfDate"
UNION ALL
SELECT 'trend efficiency: signed (continuation) vs fwd 15m', "AsOfDate",
    COUNT(*) FILTER (WHERE signed_efficiency IS NOT NULL AND fwd_15m IS NOT NULL),
    ROUND(CORR(signed_efficiency, fwd_15m)::numeric, 3)
FROM scored GROUP BY "AsOfDate"
UNION ALL
SELECT 'trend efficiency: |magnitude| (volatility) vs |fwd 5m|', "AsOfDate",
    COUNT(*) FILTER (WHERE signed_efficiency IS NOT NULL AND fwd_5m IS NOT NULL),
    ROUND(CORR(ABS(signed_efficiency), ABS(fwd_5m))::numeric, 3)
FROM scored GROUP BY "AsOfDate"
UNION ALL
SELECT 'trend efficiency: |magnitude| (volatility) vs |fwd 15m|', "AsOfDate",
    COUNT(*) FILTER (WHERE signed_efficiency IS NOT NULL AND fwd_15m IS NOT NULL),
    ROUND(CORR(ABS(signed_efficiency), ABS(fwd_15m))::numeric, 3)
FROM scored GROUP BY "AsOfDate"
UNION ALL
SELECT 'VIX change (this cadence) vs fwd 5m price change', "AsOfDate",
    COUNT(*) FILTER (WHERE vix_chg IS NOT NULL AND fwd_5m IS NOT NULL),
    ROUND(CORR(vix_chg, fwd_5m)::numeric, 3)
FROM scored GROUP BY "AsOfDate"
UNION ALL
SELECT 'VIX change (this cadence) vs fwd 15m price change', "AsOfDate",
    COUNT(*) FILTER (WHERE vix_chg IS NOT NULL AND fwd_15m IS NOT NULL),
    ROUND(CORR(vix_chg, fwd_15m)::numeric, 3)
FROM scored GROUP BY "AsOfDate"
ORDER BY test, "AsOfDate";

-- Pooled versions (all 4 days together)
WITH base AS (
    SELECT "AsOfDate", "Timestamp", "FutureChangeFromLastCadence" AS chg,
           "VixChangeFromLastCadence" AS vix_chg
    FROM "CadenceContexts"
    WHERE "FutureChangeFromLastCadence" IS NOT NULL
),
windowed AS (
    SELECT *,
        SUM(chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN INTERVAL '15 minutes' PRECEDING AND CURRENT ROW) AS trail_net_15m,
        SUM(ABS(chg)) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN INTERVAL '15 minutes' PRECEDING AND CURRENT ROW) AS trail_path_15m,
        SUM(chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN CURRENT ROW AND INTERVAL '5 minutes' FOLLOWING) - chg AS fwd_5m,
        SUM(chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN CURRENT ROW AND INTERVAL '15 minutes' FOLLOWING) - chg AS fwd_15m
    FROM base
),
scored AS (
    SELECT *,
        CASE WHEN trail_path_15m > 0 THEN trail_net_15m / trail_path_15m END AS signed_efficiency
    FROM windowed
)
SELECT 'POOLED trend eff signed vs fwd5m' AS test,
    ROUND(CORR(signed_efficiency, fwd_5m)::numeric, 3) AS corr FROM scored
UNION ALL
SELECT 'POOLED trend eff signed vs fwd15m', ROUND(CORR(signed_efficiency, fwd_15m)::numeric, 3) FROM scored
UNION ALL
SELECT 'POOLED trend eff |mag| vs |fwd5m|', ROUND(CORR(ABS(signed_efficiency), ABS(fwd_5m))::numeric, 3) FROM scored
UNION ALL
SELECT 'POOLED trend eff |mag| vs |fwd15m|', ROUND(CORR(ABS(signed_efficiency), ABS(fwd_15m))::numeric, 3) FROM scored
UNION ALL
SELECT 'POOLED vix chg vs fwd5m', ROUND(CORR(vix_chg, fwd_5m)::numeric, 3) FROM scored
UNION ALL
SELECT 'POOLED vix chg vs fwd15m', ROUND(CORR(vix_chg, fwd_15m)::numeric, 3) FROM scored;
