\echo '==> RatioCompositeScore percentile distribution, all 4 days (warmed-up rows only)'
SELECT
    percentile_cont(0.50) WITHIN GROUP (ORDER BY "RatioCompositeScore") AS p50,
    percentile_cont(0.75) WITHIN GROUP (ORDER BY "RatioCompositeScore") AS p75,
    percentile_cont(0.90) WITHIN GROUP (ORDER BY "RatioCompositeScore") AS p90,
    percentile_cont(0.925) WITHIN GROUP (ORDER BY "RatioCompositeScore") AS p92_5,
    percentile_cont(0.95) WITHIN GROUP (ORDER BY "RatioCompositeScore") AS p95,
    COUNT(*) AS n
FROM score_snapshots
WHERE "RatioIsWarmedUp" AND "RatioCompositeScore" IS NOT NULL
  AND "ComputedAt" >= '2026-09-08' AND "ComputedAt" < '2026-09-12';

\echo '==> Same, for |RatioCompositeScore| (direction-agnostic, matches how MinAbsScore is actually applied)'
SELECT
    percentile_cont(0.50) WITHIN GROUP (ORDER BY ABS("RatioCompositeScore")) AS p50,
    percentile_cont(0.75) WITHIN GROUP (ORDER BY ABS("RatioCompositeScore")) AS p75,
    percentile_cont(0.90) WITHIN GROUP (ORDER BY ABS("RatioCompositeScore")) AS p90,
    percentile_cont(0.925) WITHIN GROUP (ORDER BY ABS("RatioCompositeScore")) AS p92_5,
    percentile_cont(0.95) WITHIN GROUP (ORDER BY ABS("RatioCompositeScore")) AS p95
FROM score_snapshots
WHERE "RatioIsWarmedUp" AND "RatioCompositeScore" IS NOT NULL
  AND "ComputedAt" >= '2026-09-08' AND "ComputedAt" < '2026-09-12';

\echo '==> Same for the ORIGINAL composite, |CompositeScore|, for comparison'
SELECT
    percentile_cont(0.50) WITHIN GROUP (ORDER BY ABS("CompositeScore")) AS p50,
    percentile_cont(0.75) WITHIN GROUP (ORDER BY ABS("CompositeScore")) AS p75,
    percentile_cont(0.90) WITHIN GROUP (ORDER BY ABS("CompositeScore")) AS p90,
    percentile_cont(0.925) WITHIN GROUP (ORDER BY ABS("CompositeScore")) AS p92_5,
    percentile_cont(0.95) WITHIN GROUP (ORDER BY ABS("CompositeScore")) AS p95
FROM score_snapshots
WHERE "IsWarmedUp" AND "CompositeScore" IS NOT NULL
  AND "ComputedAt" >= '2026-09-08' AND "ComputedAt" < '2026-09-12';
