\echo '==> Future instrument tokens, 11 Sep 2026'
SELECT "Token", "TradingSymbol" FROM instruments WHERE "AsOfDate" = '2026-09-11' AND "InstrumentType" = 1;

\echo '==> Future tick volume sample, 09:15-09:20 IST'
SELECT t."ExchangeTimestamp" AT TIME ZONE 'Asia/Kolkata' AS at_ist, t."LastPrice", t."Volume"
FROM ticks t
JOIN instruments i ON i."Token" = t."Token"
WHERE i."AsOfDate" = '2026-09-11' AND i."InstrumentType" = 1
  AND t."ExchangeTimestamp" >= '2026-09-11 03:45:00+00' AND t."ExchangeTimestamp" < '2026-09-11 03:50:00+00'
ORDER BY t."ExchangeTimestamp"
LIMIT 20;

\echo '==> Future total volume range across the day'
SELECT MIN(t."Volume") AS min_vol, MAX(t."Volume") AS max_vol, COUNT(*) AS ticks
FROM ticks t
JOIN instruments i ON i."Token" = t."Token"
WHERE i."AsOfDate" = '2026-09-11' AND i."InstrumentType" = 1;

\echo '==> Spot/Index tick volume sample (for comparison -- expected to be 0/meaningless)'
SELECT t."ExchangeTimestamp" AT TIME ZONE 'Asia/Kolkata' AS at_ist, t."LastPrice", t."Volume"
FROM ticks t
JOIN instruments i ON i."Token" = t."Token"
WHERE i."AsOfDate" = '2026-09-11' AND i."InstrumentType" = 0
  AND t."ExchangeTimestamp" >= '2026-09-11 03:45:00+00' AND t."ExchangeTimestamp" < '2026-09-11 03:50:00+00'
ORDER BY t."ExchangeTimestamp"
LIMIT 10;
