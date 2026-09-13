-- Why did 10 Sep behave differently across three independent analyses (CVD-net forward
-- correlation, depth-imbalance forward correlation, and the live DynamicHybrid backtest's 0-for-4
-- day)? Characterize each of the 4 days on: trend vs. chop, volatility, spot-future basis
-- behavior, and OI change over a real 15-minute window (not just the ~4min lookback already
-- persisted) -- the two specific angles requested, plus enough context to judge them properly.

\echo '=== 1. Day-level shape: trend vs. chop ==='
WITH per_cadence AS (
    SELECT "AsOfDate", "Timestamp", "FutureCloseFromLastCadence" AS price,
        ABS("FutureCloseFromLastCadence" - LAG("FutureCloseFromLastCadence") OVER w) AS abs_move
    FROM "CadenceContexts"
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
)
SELECT "AsOfDate",
    (array_agg(price ORDER BY "Timestamp"))[1] AS day_open,
    (array_agg(price ORDER BY "Timestamp" DESC))[1] AS day_close,
    MAX(price) - MIN(price) AS day_range,
    (array_agg(price ORDER BY "Timestamp" DESC))[1] - (array_agg(price ORDER BY "Timestamp"))[1] AS net_change,
    ROUND(SUM(abs_move)::numeric, 1) AS total_path_length,
    -- Trend efficiency: |net change| / total path length. Near 1 = strongly trending (little
    -- wasted back-and-forth). Near 0 = choppy/range-bound (lots of motion, little net progress).
    ROUND((ABS((array_agg(price ORDER BY "Timestamp" DESC))[1] - (array_agg(price ORDER BY "Timestamp"))[1]) / NULLIF(SUM(abs_move), 0))::numeric, 3) AS trend_efficiency
FROM per_cadence
GROUP BY "AsOfDate" ORDER BY "AsOfDate";

\echo '=== 2. Volatility context: VIX and realized future volatility ==='
WITH per_cadence AS (
    SELECT "AsOfDate", "Timestamp", "VixCloseFromLastCadence" AS vix, "FutureCloseFromLastCadence" AS price,
        ("FutureCloseFromLastCadence" - LAG("FutureCloseFromLastCadence") OVER w) AS price_change
    FROM "CadenceContexts"
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
)
SELECT "AsOfDate",
    ROUND(AVG(vix)::numeric, 2) AS avg_vix,
    ROUND((MAX(vix) - MIN(vix))::numeric, 2) AS vix_range,
    ROUND(STDDEV(price_change)::numeric, 3) AS cadence_price_stddev
FROM per_cadence GROUP BY "AsOfDate" ORDER BY "AsOfDate";

\echo '=== 3. Spot-vs-future basis behavior per day ==='
SELECT "AsOfDate",
    ROUND(AVG("FutureCloseFromLastCadence" - "SpotCloseFromLastCadence")::numeric, 2) AS avg_basis,
    ROUND(STDDEV("FutureCloseFromLastCadence" - "SpotCloseFromLastCadence")::numeric, 2) AS basis_stddev,
    ROUND(MIN("FutureCloseFromLastCadence" - "SpotCloseFromLastCadence")::numeric, 2) AS min_basis,
    ROUND(MAX("FutureCloseFromLastCadence" - "SpotCloseFromLastCadence")::numeric, 2) AS max_basis
FROM "CadenceContexts"
WHERE "SpotCloseFromLastCadence" IS NOT NULL AND "FutureCloseFromLastCadence" IS NOT NULL
GROUP BY "AsOfDate" ORDER BY "AsOfDate";

\echo '=== 4. OI change over a real 15-minute window (60 cadences) -- not the ~4min lookback column ==='
WITH oi_15min AS (
    SELECT "AsOfDate", "Timestamp", "FutureOpenInterest",
        "FutureOpenInterest" - LAG("FutureOpenInterest", 60) OVER w AS oi_change_15min
    FROM "CadenceContexts"
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
)
SELECT "AsOfDate",
    (array_agg("FutureOpenInterest" ORDER BY "Timestamp"))[1] AS oi_open,
    (array_agg("FutureOpenInterest" ORDER BY "Timestamp" DESC))[1] AS oi_close,
    (array_agg("FutureOpenInterest" ORDER BY "Timestamp" DESC))[1] - (array_agg("FutureOpenInterest" ORDER BY "Timestamp"))[1] AS oi_net_change_day,
    ROUND(AVG(ABS(oi_change_15min))::numeric, 0) AS avg_abs_15min_oi_churn,
    MIN(oi_change_15min) AS min_15min_oi_change,
    MAX(oi_change_15min) AS max_15min_oi_change
FROM oi_15min GROUP BY "AsOfDate" ORDER BY "AsOfDate";

\echo '=== 5. Correlation: OI change (15min) vs forward 5min price move -- does OI churn predict better than volume/CVD did? ==='
WITH base AS (
    SELECT "AsOfDate", "Timestamp",
        "FutureOpenInterest" - LAG("FutureOpenInterest", 60) OVER w AS oi_change_15min,
        LEAD("FutureCloseFromLastCadence", 20) OVER w - "FutureCloseFromLastCadence" AS fwd_5m
    FROM "CadenceContexts"
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
)
SELECT "AsOfDate", COUNT(*) FILTER (WHERE oi_change_15min IS NOT NULL AND fwd_5m IS NOT NULL) AS n,
    ROUND(CORR(oi_change_15min, fwd_5m)::numeric, 3) AS oi15min_vs_fwd5m
FROM base GROUP BY "AsOfDate" ORDER BY "AsOfDate";

\echo '=== 6. Trading activity volume: was 10 Sep just quiet? ==='
SELECT "AsOfDate",
    (array_agg("FutureVolumeCumulativeDay" ORDER BY "Timestamp" DESC))[1] AS total_day_volume,
    SUM("TicksObservedFuture") AS total_future_ticks,
    (array_agg("FutureCvdProxyCumulativeDay" ORDER BY "Timestamp" DESC))[1] AS final_cvd_cumulative
FROM "CadenceContexts" GROUP BY "AsOfDate" ORDER BY "AsOfDate";
