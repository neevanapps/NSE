\echo '==> Per-day tick counts, 08-11 Sep 2026 (IST)'
SELECT ("ExchangeTimestamp" AT TIME ZONE 'Asia/Kolkata')::date AS ist_day,
       COUNT(*) AS ticks,
       MIN("ExchangeTimestamp" AT TIME ZONE 'Asia/Kolkata') AS first_tick,
       MAX("ExchangeTimestamp" AT TIME ZONE 'Asia/Kolkata') AS last_tick
FROM ticks
WHERE "ExchangeTimestamp" >= '2026-09-08' AND "ExchangeTimestamp" < '2026-09-12'
GROUP BY 1 ORDER BY 1;

\echo '==> Instrument AsOfDate resolution, 08-11 Sep 2026'
SELECT "AsOfDate",
       COUNT(*) AS instruments,
       COUNT(*) FILTER (WHERE "InstrumentType" = 2) AS options
FROM instruments
WHERE "AsOfDate" >= '2026-09-08' AND "AsOfDate" <= '2026-09-11'
GROUP BY 1 ORDER BY 1;

\echo '==> ScoreSnapshot counts per day'
SELECT ("ComputedAt" AT TIME ZONE 'Asia/Kolkata')::date AS ist_day,
       COUNT(*) AS cadences,
       COUNT(*) FILTER (WHERE "CompositeScore" IS NOT NULL) AS scored
FROM score_snapshots
WHERE "ComputedAt" >= '2026-09-08' AND "ComputedAt" < '2026-09-12'
GROUP BY 1 ORDER BY 1;

\echo '==> DataGaps, 08-11 Sep 2026'
SELECT "StartedAt" AT TIME ZONE 'Asia/Kolkata' AS started_ist,
       "EndedAt" AT TIME ZONE 'Asia/Kolkata' AS ended_ist,
       ROUND(EXTRACT(EPOCH FROM (COALESCE("EndedAt", now()) - "StartedAt"))/60.0, 1) AS duration_min,
       "Reason"
FROM data_gaps
WHERE "StartedAt" >= '2026-09-08' AND "StartedAt" < '2026-09-12'
ORDER BY 1;
