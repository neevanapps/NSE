-- Does the future's own order-book depth imbalance (5min/15min mean) predict what the future's
-- price does next, or does it just coincide with what already happened? Same backward-vs-forward
-- correlation methodology already used elsewhere in this project (PriceMomentum, SpreadRatio) --
-- computed per trading day AND pooled across all 4, per the "don't conclude from one run" rule.
-- Forward/backward windows are day-scoped (PARTITION BY "AsOfDate") so a lead/lag never bleeds
-- across a day boundary into a nonsensical overnight "return".

WITH base AS (
    SELECT
        "AsOfDate",
        "Timestamp",
        "FutureCloseFromLastCadence" AS price,
        "FutureDepthImbalanceMean5Min" AS imb5,
        "FutureDepthImbalanceMean15Min" AS imb15,
        LEAD("FutureCloseFromLastCadence", 4)  OVER w - "FutureCloseFromLastCadence" AS fwd_1m,
        LEAD("FutureCloseFromLastCadence", 20) OVER w - "FutureCloseFromLastCadence" AS fwd_5m,
        LEAD("FutureCloseFromLastCadence", 60) OVER w - "FutureCloseFromLastCadence" AS fwd_15m,
        "FutureCloseFromLastCadence" - LAG("FutureCloseFromLastCadence", 4)  OVER w AS bwd_1m,
        "FutureCloseFromLastCadence" - LAG("FutureCloseFromLastCadence", 20) OVER w AS bwd_5m,
        "FutureCloseFromLastCadence" - LAG("FutureCloseFromLastCadence", 60) OVER w AS bwd_15m
    FROM "CadenceContexts"
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
)
\echo '=== Pooled across all 4 days ==='
SELECT
    'imb5min'  AS metric,
    COUNT(*) FILTER (WHERE imb5 IS NOT NULL AND fwd_1m IS NOT NULL) AS n,
    ROUND(CORR(imb5, bwd_1m)::numeric, 3)  AS bwd_1m,
    ROUND(CORR(imb5, bwd_5m)::numeric, 3)  AS bwd_5m,
    ROUND(CORR(imb5, bwd_15m)::numeric, 3) AS bwd_15m,
    ROUND(CORR(imb5, fwd_1m)::numeric, 3)  AS fwd_1m,
    ROUND(CORR(imb5, fwd_5m)::numeric, 3)  AS fwd_5m,
    ROUND(CORR(imb5, fwd_15m)::numeric, 3) AS fwd_15m
FROM base
UNION ALL
SELECT
    'imb15min',
    COUNT(*) FILTER (WHERE imb15 IS NOT NULL AND fwd_1m IS NOT NULL),
    ROUND(CORR(imb15, bwd_1m)::numeric, 3),
    ROUND(CORR(imb15, bwd_5m)::numeric, 3),
    ROUND(CORR(imb15, bwd_15m)::numeric, 3),
    ROUND(CORR(imb15, fwd_1m)::numeric, 3),
    ROUND(CORR(imb15, fwd_5m)::numeric, 3),
    ROUND(CORR(imb15, fwd_15m)::numeric, 3)
FROM base;

\echo '=== Per day -- is the sign/magnitude consistent, or one day driving it? ==='
WITH base AS (
    SELECT
        "AsOfDate",
        "FutureCloseFromLastCadence" AS price,
        "FutureDepthImbalanceMean5Min" AS imb5,
        "FutureDepthImbalanceMean15Min" AS imb15,
        LEAD("FutureCloseFromLastCadence", 20) OVER w - "FutureCloseFromLastCadence" AS fwd_5m,
        "FutureCloseFromLastCadence" - LAG("FutureCloseFromLastCadence", 20) OVER w AS bwd_5m
    FROM "CadenceContexts"
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
)
SELECT "AsOfDate",
    ROUND(CORR(imb5, bwd_5m)::numeric, 3) AS imb5_bwd_5m,
    ROUND(CORR(imb5, fwd_5m)::numeric, 3) AS imb5_fwd_5m,
    ROUND(CORR(imb15, bwd_5m)::numeric, 3) AS imb15_bwd_5m,
    ROUND(CORR(imb15, fwd_5m)::numeric, 3) AS imb15_fwd_5m
FROM base GROUP BY "AsOfDate" ORDER BY "AsOfDate";
