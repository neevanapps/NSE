-- 2026-09-17: TobDepthDivergence/DepthImbalance redundancy check. TobDepthDivergence's own raw
-- formula is DepthImbalance - TopOfBookImbalance, so it LITERALLY contains DepthImbalance as one
-- of its two terms -- before crediting it as a genuinely independent 10th candidate, check
-- whether it's mostly just DepthImbalance restated with TOB noise subtracted, or a real
-- combination. Same discipline as every other redundancy check this track has done
-- (scripts/depthimbalance-vs-barduration-redundancy.sql, scripts/pcroi-vs-oidiff-redundancy.sql):
-- level-vs-level and delta-vs-delta, per day and pooled, at a single shared threshold (2600 --
-- TobDepthDivergence's own best combo).
WITH bars AS (
    SELECT
        "AsOfDate",
        "BarIndex",
        "FutureDepthImbalance" AS depth_imbalance,
        "FutureDepthImbalance" - "TopOfBookImbalance" AS divergence
    FROM "VolumeBars"
    WHERE "BarVolumeThreshold" = 2600
      AND "AsOfDate" BETWEEN '2026-09-08' AND '2026-09-16'
      AND "FutureDepthImbalance" IS NOT NULL AND "TopOfBookImbalance" IS NOT NULL
),
with_delta AS (
    SELECT *,
        depth_imbalance - LAG(depth_imbalance) OVER (PARTITION BY "AsOfDate" ORDER BY "BarIndex") AS depth_delta,
        divergence - LAG(divergence) OVER (PARTITION BY "AsOfDate" ORDER BY "BarIndex") AS divergence_delta
    FROM bars
)
SELECT "AsOfDate"::text AS as_of_date, COUNT(*) AS n,
    ROUND(CORR(depth_imbalance, divergence)::numeric, 3) AS level_vs_level,
    ROUND(CORR(depth_delta, divergence_delta)::numeric, 3) AS delta_vs_delta
FROM with_delta
GROUP BY GROUPING SETS (("AsOfDate"), ())
ORDER BY as_of_date NULLS LAST;
