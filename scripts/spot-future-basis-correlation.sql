-- 2026-09-12: does the spot/future basis (or its change over time) carry directional information
-- about the future's own forward price move? Same backward/forward, per-day + pooled methodology
-- used for every other candidate in docs/SCORE_CANDIDATES.md. Basis = future - spot (positive =
-- future at premium/contango), matching the sign convention already used in the live composite's
-- own FuturesBasisRaw (see CLAUDE.md's audit-remediation plan, F11).
WITH base AS (
    SELECT "AsOfDate", "Timestamp",
           "FutureChangeFromLastCadence" AS fut_chg,
           "SpotChangeFromLastCadence" AS spot_chg,
           ("FutureCloseFromLastCadence" - "SpotCloseFromLastCadence") AS basis_level,
           ("FutureChangeFromLastCadence" - "SpotChangeFromLastCadence") AS basis_chg
    FROM "CadenceContexts"
    WHERE "FutureCloseFromLastCadence" IS NOT NULL AND "SpotCloseFromLastCadence" IS NOT NULL
),
windowed AS (
    SELECT *,
        SUM(basis_chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN INTERVAL '5 minutes' PRECEDING AND CURRENT ROW) AS basis_chg_trail_5m,
        SUM(basis_chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN INTERVAL '15 minutes' PRECEDING AND CURRENT ROW) AS basis_chg_trail_15m,
        SUM(fut_chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN INTERVAL '5 minutes' PRECEDING AND CURRENT ROW) AS fut_bwd_5m,
        SUM(fut_chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN INTERVAL '15 minutes' PRECEDING AND CURRENT ROW) AS fut_bwd_15m,
        SUM(fut_chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN CURRENT ROW AND INTERVAL '5 minutes' FOLLOWING) - fut_chg AS fut_fwd_5m,
        SUM(fut_chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN CURRENT ROW AND INTERVAL '15 minutes' FOLLOWING) - fut_chg AS fut_fwd_15m
    FROM base
)
SELECT 'basis LEVEL vs bwd5m' AS test, "AsOfDate", COUNT(*) AS n,
    ROUND(CORR(basis_level, fut_bwd_5m)::numeric,3) AS corr
FROM windowed GROUP BY "AsOfDate"
UNION ALL SELECT 'basis LEVEL vs fwd5m', "AsOfDate", COUNT(*), ROUND(CORR(basis_level, fut_fwd_5m)::numeric,3) FROM windowed GROUP BY "AsOfDate"
UNION ALL SELECT 'basis LEVEL vs fwd15m', "AsOfDate", COUNT(*), ROUND(CORR(basis_level, fut_fwd_15m)::numeric,3) FROM windowed GROUP BY "AsOfDate"
UNION ALL SELECT 'basis CHANGE (per-cadence) vs fwd5m', "AsOfDate", COUNT(*), ROUND(CORR(basis_chg, fut_fwd_5m)::numeric,3) FROM windowed GROUP BY "AsOfDate"
UNION ALL SELECT 'basis CHANGE (per-cadence) vs fwd15m', "AsOfDate", COUNT(*), ROUND(CORR(basis_chg, fut_fwd_15m)::numeric,3) FROM windowed GROUP BY "AsOfDate"
UNION ALL SELECT 'basis CHANGE (rolling 5m sum) vs fwd5m', "AsOfDate", COUNT(*), ROUND(CORR(basis_chg_trail_5m, fut_fwd_5m)::numeric,3) FROM windowed GROUP BY "AsOfDate"
UNION ALL SELECT 'basis CHANGE (rolling 15m sum) vs fwd15m', "AsOfDate", COUNT(*), ROUND(CORR(basis_chg_trail_15m, fut_fwd_15m)::numeric,3) FROM windowed GROUP BY "AsOfDate"
UNION ALL SELECT 'basis CHANGE (rolling 5m sum) vs bwd5m', "AsOfDate", COUNT(*), ROUND(CORR(basis_chg_trail_5m, fut_bwd_5m)::numeric,3) FROM windowed GROUP BY "AsOfDate"
ORDER BY test, "AsOfDate";

WITH base AS (
    SELECT "AsOfDate", "Timestamp",
           "FutureChangeFromLastCadence" AS fut_chg,
           "SpotChangeFromLastCadence" AS spot_chg,
           ("FutureCloseFromLastCadence" - "SpotCloseFromLastCadence") AS basis_level,
           ("FutureChangeFromLastCadence" - "SpotChangeFromLastCadence") AS basis_chg
    FROM "CadenceContexts"
    WHERE "FutureCloseFromLastCadence" IS NOT NULL AND "SpotCloseFromLastCadence" IS NOT NULL
),
windowed AS (
    SELECT *,
        SUM(basis_chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN INTERVAL '5 minutes' PRECEDING AND CURRENT ROW) AS basis_chg_trail_5m,
        SUM(basis_chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN INTERVAL '15 minutes' PRECEDING AND CURRENT ROW) AS basis_chg_trail_15m,
        SUM(fut_chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN INTERVAL '5 minutes' PRECEDING AND CURRENT ROW) AS fut_bwd_5m,
        SUM(fut_chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN CURRENT ROW AND INTERVAL '5 minutes' FOLLOWING) - fut_chg AS fut_fwd_5m,
        SUM(fut_chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN CURRENT ROW AND INTERVAL '15 minutes' FOLLOWING) - fut_chg AS fut_fwd_15m
    FROM base
)
SELECT 'POOLED basis LEVEL vs bwd5m' AS test, ROUND(CORR(basis_level, fut_bwd_5m)::numeric,3) AS corr FROM windowed
UNION ALL SELECT 'POOLED basis LEVEL vs fwd5m', ROUND(CORR(basis_level, fut_fwd_5m)::numeric,3) FROM windowed
UNION ALL SELECT 'POOLED basis LEVEL vs fwd15m', ROUND(CORR(basis_level, fut_fwd_15m)::numeric,3) FROM windowed
UNION ALL SELECT 'POOLED basis CHANGE (per-cadence) vs fwd5m', ROUND(CORR(basis_chg, fut_fwd_5m)::numeric,3) FROM windowed
UNION ALL SELECT 'POOLED basis CHANGE (per-cadence) vs fwd15m', ROUND(CORR(basis_chg, fut_fwd_15m)::numeric,3) FROM windowed
UNION ALL SELECT 'POOLED basis CHANGE (rolling 5m sum) vs fwd5m', ROUND(CORR(basis_chg_trail_5m, fut_fwd_5m)::numeric,3) FROM windowed
UNION ALL SELECT 'POOLED basis CHANGE (rolling 15m sum) vs fwd15m', ROUND(CORR(basis_chg_trail_15m, fut_fwd_15m)::numeric,3) FROM windowed
UNION ALL SELECT 'POOLED basis CHANGE (rolling 5m sum) vs bwd5m', ROUND(CORR(basis_chg_trail_5m, fut_bwd_5m)::numeric,3) FROM windowed;

-- Sanity: basis level range per day (is it stable/small as expected for an index future, or noisy)
SELECT "AsOfDate",
    ROUND(MIN("FutureCloseFromLastCadence" - "SpotCloseFromLastCadence")::numeric,2) AS min_basis,
    ROUND(MAX("FutureCloseFromLastCadence" - "SpotCloseFromLastCadence")::numeric,2) AS max_basis,
    ROUND(AVG("FutureCloseFromLastCadence" - "SpotCloseFromLastCadence")::numeric,2) AS avg_basis
FROM "CadenceContexts"
WHERE "FutureCloseFromLastCadence" IS NOT NULL AND "SpotCloseFromLastCadence" IS NOT NULL
GROUP BY "AsOfDate" ORDER BY "AsOfDate";
