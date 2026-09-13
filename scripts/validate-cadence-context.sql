-- Sanity-check the newly populated CadenceContexts table (niftysignal_backtest_analysis).
-- Row counts, null rates per column, day-level trend sanity (DTE shrinking, day range growing),
-- and a spot-check against the exact real numbers already hand-verified in the scoring reference.

\echo '=== 1. Row counts per day ==='
SELECT "AsOfDate", COUNT(*) AS rows, MIN("Timestamp") AS first_ts, MAX("Timestamp") AS last_ts
FROM "CadenceContexts"
GROUP BY "AsOfDate" ORDER BY "AsOfDate";

\echo '=== 2. Null rate per column (out of 6000 total rows) ==='
SELECT
  COUNT(*) AS total_rows,
  COUNT(*) - COUNT("AtmStrikeBySpot") AS null_atm_spot,
  COUNT(*) - COUNT("AtmStrikeByFuture") AS null_atm_future,
  COUNT(*) - COUNT("AtmStrikeBySyntheticForward") AS null_atm_forward,
  COUNT(*) - COUNT("HoursToExpiryCalendar") AS null_hte_cal,
  COUNT(*) - COUNT("HoursToExpiryTrading") AS null_hte_trading,
  COUNT(*) - COUNT("SpotCloseFromLastCadence") AS null_spot_close,
  COUNT(*) - COUNT("FutureCloseFromLastCadence") AS null_future_close,
  COUNT(*) - COUNT("VixCloseFromLastCadence") AS null_vix_close,
  COUNT(*) - COUNT("FutureVwap") AS null_vwap,
  COUNT(*) - COUNT("FutureOpenInterest") AS null_future_oi,
  COUNT(*) - COUNT("FutureOiChangeFromLastKnown") AS null_oi_change_known,
  COUNT(*) - COUNT("FutureDepthImbalanceFromLastCadence") AS null_depth_imbalance,
  COUNT(*) - COUNT("FutureDepthImbalanceMean5Min") AS null_depth_5min,
  COUNT(*) - COUNT("FutureDepthImbalanceMean15Min") AS null_depth_15min
FROM "CadenceContexts";

\echo '=== 3. Tick coverage -- any cadence with zero ticks for Spot or Future? ==='
SELECT "AsOfDate", COUNT(*) FILTER (WHERE "TicksObservedSpot" = 0) AS spot_gaps,
       COUNT(*) FILTER (WHERE "TicksObservedFuture" = 0) AS future_gaps,
       COUNT(*) FILTER (WHERE "TicksObservedVix" = 0) AS vix_gaps,
       ROUND(AVG("TicksObservedSpot"),1) AS avg_spot_ticks,
       ROUND(AVG("TicksObservedFuture"),1) AS avg_future_ticks
FROM "CadenceContexts" GROUP BY "AsOfDate" ORDER BY "AsOfDate";

\echo '=== 4. DTE trend -- HoursToExpiryCalendar/Trading should shrink day over day (expiry 2026-09-15) ==='
SELECT "AsOfDate",
       MIN("HoursToExpiryCalendar") AS min_hte_cal, MAX("HoursToExpiryCalendar") AS max_hte_cal,
       MIN("HoursToExpiryTrading") AS min_hte_trade, MAX("HoursToExpiryTrading") AS max_hte_trade
FROM "CadenceContexts" GROUP BY "AsOfDate" ORDER BY "AsOfDate";

\echo '=== 5. Day range sanity -- SpotHighForDay >= SpotLowForDay, and growing intraday ==='
SELECT "AsOfDate", MIN("SpotOpenForDay") AS day_open, MAX("SpotHighForDay") AS day_high,
       MIN("SpotLowForDay") AS day_low, COUNT(*) FILTER (WHERE "SpotHighForDay" < "SpotLowForDay") AS inverted_rows
FROM "CadenceContexts" GROUP BY "AsOfDate" ORDER BY "AsOfDate";

\echo '=== 6. Spot-check against the hand-verified real cadence: 2026-09-11 14:00:48-49 IST ==='
\x on
SELECT "Timestamp", "AsOfDate", "SpotCloseFromLastCadence", "FutureCloseFromLastCadence",
       "AtmStrikeBySpot", "AtmStrikeByFuture", "AtmStrikeBySyntheticForward",
       "HoursToExpiryCalendar", "FutureOpenInterest", "FutureVwap",
       "FutureDepthImbalanceFromLastCadence", "FutureTotalBidQty", "FutureTotalAskQty"
FROM "CadenceContexts"
WHERE "Timestamp" >= '2026-09-11 14:00:45+05:30' AND "Timestamp" < '2026-09-11 14:01:15+05:30'
ORDER BY "Timestamp";
\x off

\echo '=== 7. Depth imbalance warm-up -- first non-null 5min/15min mean each day, and how many minutes in ==='
SELECT "AsOfDate",
       MIN("Timestamp") FILTER (WHERE "FutureDepthImbalanceMean5Min" IS NOT NULL) AS first_5min_warm,
       MIN("Timestamp") FILTER (WHERE "FutureDepthImbalanceMean15Min" IS NOT NULL) AS first_15min_warm,
       MIN("Timestamp") AS day_start
FROM "CadenceContexts" GROUP BY "AsOfDate" ORDER BY "AsOfDate";

\echo '=== 8. Any negative volumes/OI-changes-for-day (would indicate a real bug) ==='
SELECT COUNT(*) FILTER (WHERE "FutureVolumeDeltaThisCadence" < 0) AS negative_volume_delta,
       COUNT(*) FILTER (WHERE "FutureVolumeCumulativeDay" < 0) AS negative_cumulative_volume,
       COUNT(*) FILTER (WHERE "FutureTotalBidQty" < 0 OR "FutureTotalAskQty" < 0) AS negative_depth_qty,
       COUNT(*) FILTER (WHERE "FutureDepthImbalanceFromLastCadence" < -1 OR "FutureDepthImbalanceFromLastCadence" > 1) AS out_of_range_imbalance
FROM "CadenceContexts";

\echo '=== 9. FutureVwap sanity -- should track close to FutureCloseFromLastCadence, not wildly off ==='
SELECT "AsOfDate",
       ROUND(AVG(ABS("FutureVwap" - "FutureCloseFromLastCadence")),2) AS avg_abs_vwap_vs_close_diff,
       ROUND(MAX(ABS("FutureVwap" - "FutureCloseFromLastCadence")),2) AS max_abs_vwap_vs_close_diff
FROM "CadenceContexts" WHERE "FutureVwap" IS NOT NULL GROUP BY "AsOfDate" ORDER BY "AsOfDate";

\echo '=== 10. Duplicate timestamp check (should be zero -- unique index should already guarantee this) ==='
SELECT "Timestamp", COUNT(*) FROM "CadenceContexts" GROUP BY "Timestamp" HAVING COUNT(*) > 1;
