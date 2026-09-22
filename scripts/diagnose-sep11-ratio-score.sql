\echo '==> Spot price, last tick per 5-min bucket, 09:00-15:35 IST'
SELECT DISTINCT ON (bucket) bucket, "LastPrice"
FROM (
    SELECT date_trunc('hour', "ExchangeTimestamp" AT TIME ZONE 'Asia/Kolkata')
             + (EXTRACT(minute FROM "ExchangeTimestamp" AT TIME ZONE 'Asia/Kolkata')::int / 5) * interval '5 min' AS bucket,
           "ExchangeTimestamp",
           "LastPrice"
    FROM ticks
    WHERE "Token" = (SELECT "Token" FROM instruments WHERE "AsOfDate" = '2026-09-11' AND "InstrumentType" = 0 LIMIT 1)
      AND "ExchangeTimestamp" >= '2026-09-11 03:30:00+00' AND "ExchangeTimestamp" < '2026-09-11 10:05:00+00'
) x
ORDER BY bucket, "ExchangeTimestamp" DESC;

\echo '==> Live-recorded CompositeScore vs RatioCompositeScore, last row per 5-min bucket, 09:00-15:35 IST'
SELECT DISTINCT ON (bucket) bucket,
       ROUND("CompositeScore"::numeric, 1) AS composite,
       ROUND("RatioCompositeScore"::numeric, 1) AS ratio,
       "IsWarmedUp" AS composite_warm,
       "RatioIsWarmedUp" AS ratio_warm
FROM (
    SELECT date_trunc('hour', "ComputedAt" AT TIME ZONE 'Asia/Kolkata')
             + (EXTRACT(minute FROM "ComputedAt" AT TIME ZONE 'Asia/Kolkata')::int / 5) * interval '5 min' AS bucket,
           "ComputedAt", "CompositeScore", "RatioCompositeScore", "IsWarmedUp", "RatioIsWarmedUp"
    FROM score_snapshots
    WHERE "ComputedAt" >= '2026-09-11 03:30:00+00' AND "ComputedAt" < '2026-09-11 10:05:00+00'
) x
ORDER BY bucket, "ComputedAt" DESC;

\echo '==> Day summary'
SELECT COUNT(*) AS total_rows,
       COUNT(*) FILTER (WHERE "RatioCompositeScore" IS NOT NULL) AS ratio_scored,
       MIN("RatioCompositeScore") AS min_ratio,
       MAX("RatioCompositeScore") AS max_ratio,
       MIN("CompositeScore") AS min_composite,
       MAX("CompositeScore") AS max_composite
FROM score_snapshots
WHERE "ComputedAt" >= '2026-09-11 03:30:00+00' AND "ComputedAt" < '2026-09-11 10:05:00+00';
