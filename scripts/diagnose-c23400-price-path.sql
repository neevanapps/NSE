\echo '==> NIFTY15SEP26C23400 tick-level price path, 13:55-14:15 IST, 11 Sep 2026'
SELECT t."ExchangeTimestamp" AT TIME ZONE 'Asia/Kolkata' AS at_ist,
       t."LastPrice",
       t.bid1_price, t.bid1_qty,
       t.ask1_price, t.ask1_qty
FROM ticks t
JOIN instruments i ON i."Token" = t."Token"
WHERE i."AsOfDate" = '2026-09-11' AND i."TradingSymbol" = 'NIFTY15SEP26C23400'
  AND t."ExchangeTimestamp" >= '2026-09-11 08:25:00+00' AND t."ExchangeTimestamp" < '2026-09-11 08:45:00+00'
ORDER BY t."ExchangeTimestamp";
