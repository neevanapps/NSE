-- 2026-09-13: 09 Sep has now lost money in EVERY variant tried today (hysteresis threshold AND
-- every crossover window combo) -- investigate whether it's the same "one term locked wrong all
-- day" mechanism found for 11 Sep, or a genuinely different failure mode (e.g. a choppy,
-- no-net-trend day where any directional strategy whipsaws). Two checks: (1) the day's own price
-- shape (net move vs path length -- a low ratio means lots of back-and-forth, a high ratio means a
-- clean trend), (2) each of the 8 Core-score terms' whole-day directional bias, same method as the
-- 11 Sep investigation.
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
this_week AS (
    SELECT s.* FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate" AND wm.week_rank = 1
    WHERE s."AsOfDate" = '2026-09-09'
),
price_shape AS (
    SELECT
        (array_agg("FutureCloseFromLastCadence" ORDER BY "Timestamp" ASC))[1] AS future_open,
        (array_agg("FutureCloseFromLastCadence" ORDER BY "Timestamp" DESC))[1] AS future_close,
        SUM(ABS(COALESCE("FutureChangeFromLastCadence", 0))) AS path_length,
        MIN("FutureCloseFromLastCadence") AS day_low,
        MAX("FutureCloseFromLastCadence") AS day_high
    FROM "CadenceContexts" WHERE "AsOfDate" = '2026-09-09'
),
per_cadence AS (
    SELECT "Timestamp",
        AVG("DepthImbalanceFromLastCadence") FILTER (WHERE "OptionType" = 1 AND "StrikeOffsetFromAtm" BETWEEN -2 AND 0)
            - AVG("DepthImbalanceFromLastCadence") FILTER (WHERE "OptionType" = 2 AND "StrikeOffsetFromAtm" BETWEEN 0 AND 2) AS depth_imbalance_raw,
        AVG("ImpliedVolatility") FILTER (WHERE "OptionType" = 2 AND "StrikeOffsetFromAtm" BETWEEN 0 AND 2)
            - AVG("ImpliedVolatility") FILTER (WHERE "OptionType" = 1 AND "StrikeOffsetFromAtm" BETWEEN -2 AND 0) AS itm_skew_raw,
        SUM("Gamma" * "OpenInterest") FILTER (WHERE "OptionType" = 1 AND ABS("StrikeOffsetFromAtm") <= 10)
            - SUM("Gamma" * "OpenInterest") FILTER (WHERE "OptionType" = 2 AND ABS("StrikeOffsetFromAtm") <= 10) AS gamma_exposure_raw,
        SUM("VolumeDelta" * "MarkPrice") FILTER (WHERE "OptionType" = 1 AND ABS("StrikeOffsetFromAtm") <= 5 AND "VolumeDelta" > 0 AND "MarkPrice" > 0) AS call_notional,
        SUM("VolumeDelta" * "MarkPrice") FILTER (WHERE "OptionType" = 2 AND ABS("StrikeOffsetFromAtm") <= 5 AND "VolumeDelta" > 0 AND "MarkPrice" > 0) AS put_notional
    FROM this_week
    GROUP BY "Timestamp"
),
combined AS (
    SELECT p.*, cc."FutureCvdProxyNet5Min" AS future_cvd,
        CASE WHEN p.call_notional > 0 AND p.put_notional > 0 THEN LN(p.put_notional/p.call_notional) END AS notional_log_ratio_raw
    FROM per_cadence p
    LEFT JOIN "CadenceContexts" cc ON cc."Timestamp" = p."Timestamp"
)
SELECT
    (SELECT future_open FROM price_shape) AS future_open,
    (SELECT future_close FROM price_shape) AS future_close,
    (SELECT future_close - future_open FROM price_shape) AS future_net_move,
    (SELECT path_length FROM price_shape) AS future_path_length,
    (SELECT day_high - day_low FROM price_shape) AS future_day_range,
    ROUND((SELECT ABS(future_close - future_open) / NULLIF(path_length, 0) FROM price_shape)::numeric, 4) AS net_over_path_ratio,
    COUNT(*) AS cadences,
    ROUND(100.0*COUNT(*) FILTER (WHERE depth_imbalance_raw > 0)/NULLIF(COUNT(*) FILTER (WHERE depth_imbalance_raw IS NOT NULL),0),1) AS pct_depth_bullish,
    ROUND(100.0*COUNT(*) FILTER (WHERE itm_skew_raw > 0)/NULLIF(COUNT(*) FILTER (WHERE itm_skew_raw IS NOT NULL),0),1) AS pct_skew_bullish,
    ROUND(100.0*COUNT(*) FILTER (WHERE gamma_exposure_raw > 0)/NULLIF(COUNT(*) FILTER (WHERE gamma_exposure_raw IS NOT NULL),0),1) AS pct_gamma_bullish,
    ROUND(100.0*COUNT(*) FILTER (WHERE notional_log_ratio_raw > 0)/NULLIF(COUNT(*) FILTER (WHERE notional_log_ratio_raw IS NOT NULL),0),1) AS pct_notional_bullish,
    ROUND(100.0*COUNT(*) FILTER (WHERE future_cvd > 0)/NULLIF(COUNT(*) FILTER (WHERE future_cvd IS NOT NULL),0),1) AS pct_cvd_bullish
FROM combined;
