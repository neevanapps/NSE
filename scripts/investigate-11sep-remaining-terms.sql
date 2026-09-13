-- 2026-09-13: which of the 6 REMAINING Core-score terms (after dropping BasisChange and
-- TrendReversion15m) kept the composite from ever crossing back to +threshold during the two
-- catastrophic "stuck put" windows on 11 Sep -- both windows show the future rising steadily while
-- a long Put position stayed open for 79-183 minutes, meaning the score never got bullish enough
-- to trigger the hysteresis exit. Compute each term's own raw value per 15s cadence and summarize
-- sign/magnitude per window to see which one(s) resisted the bullish push.
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
this_week AS (
    SELECT s.* FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate" AND wm.week_rank = 1
    WHERE s."AsOfDate" = '2026-09-11'
),
windows AS (
    SELECT * FROM (VALUES
        ('trade1_stuck', '10:02:00'::time, '13:06:00'::time),
        ('trade3_forceclose', '13:55:00'::time, '15:16:00'::time)
    ) AS w(label, start_t, end_t)
),
depth AS (
    SELECT "Timestamp",
        AVG("DepthImbalanceFromLastCadence") FILTER (WHERE "OptionType" = 1 AND "StrikeOffsetFromAtm" BETWEEN -2 AND 0)
            - AVG("DepthImbalanceFromLastCadence") FILTER (WHERE "OptionType" = 2 AND "StrikeOffsetFromAtm" BETWEEN 0 AND 2) AS depth_imbalance_raw,
        AVG("ImpliedVolatility") FILTER (WHERE "OptionType" = 2 AND "StrikeOffsetFromAtm" BETWEEN 0 AND 2)
            - AVG("ImpliedVolatility") FILTER (WHERE "OptionType" = 1 AND "StrikeOffsetFromAtm" BETWEEN -2 AND 0) AS itm_skew_raw,
        SUM("Gamma" * "OpenInterest") FILTER (WHERE "OptionType" = 1 AND ABS("StrikeOffsetFromAtm") <= 10)
            - SUM("Gamma" * "OpenInterest") FILTER (WHERE "OptionType" = 2 AND ABS("StrikeOffsetFromAtm") <= 10) AS gamma_exposure_raw,
        SUM("OpenInterestDelta") FILTER (WHERE "OptionType" = 1 AND ABS("StrikeOffsetFromAtm") <= 2) AS call_oi_delta,
        SUM("OpenInterestDelta") FILTER (WHERE "OptionType" = 2 AND ABS("StrikeOffsetFromAtm") <= 2) AS put_oi_delta
    FROM this_week
    GROUP BY "Timestamp"
),
oi_rolling AS (
    SELECT "Timestamp",
        SUM(COALESCE(call_oi_delta,0) - COALESCE(put_oi_delta,0)) OVER (
            ORDER BY "Timestamp" RANGE BETWEEN INTERVAL '15 minutes' PRECEDING AND CURRENT ROW
        ) AS oi_change_diff_15m_raw
    FROM depth
),
notional AS (
    SELECT "Timestamp",
        SUM("VolumeDelta" * "MarkPrice") FILTER (WHERE "OptionType" = 1 AND ABS("StrikeOffsetFromAtm") <= 5 AND "VolumeDelta" > 0 AND "MarkPrice" > 0) AS call_notional,
        SUM("VolumeDelta" * "MarkPrice") FILTER (WHERE "OptionType" = 2 AND ABS("StrikeOffsetFromAtm") <= 5 AND "VolumeDelta" > 0 AND "MarkPrice" > 0) AS put_notional
    FROM this_week
    GROUP BY "Timestamp"
),
combined AS (
    SELECT d."Timestamp", d.depth_imbalance_raw, d.itm_skew_raw, d.gamma_exposure_raw,
        o.oi_change_diff_15m_raw,
        CASE WHEN n.call_notional > 0 AND n.put_notional > 0 THEN LN(n.put_notional / n.call_notional) END AS notional_log_ratio_raw,
        cc."FutureCvdProxyNet5Min" AS future_cvd_net5min
    FROM depth d
    JOIN oi_rolling o ON o."Timestamp" = d."Timestamp"
    JOIN notional n ON n."Timestamp" = d."Timestamp"
    LEFT JOIN "CadenceContexts" cc ON cc."Timestamp" = d."Timestamp"
),
tagged AS (
    SELECT c.*, w.label
    FROM combined c
    JOIN windows w ON (c."Timestamp" AT TIME ZONE 'Asia/Kolkata')::time BETWEEN w.start_t AND w.end_t
)
SELECT
    label,
    COUNT(*) AS cadences,
    ROUND(AVG(depth_imbalance_raw)::numeric, 4) AS avg_depth_imb,
    ROUND(100.0 * COUNT(*) FILTER (WHERE depth_imbalance_raw > 0) / NULLIF(COUNT(*) FILTER (WHERE depth_imbalance_raw IS NOT NULL),0), 1) AS pct_depth_bullish,
    ROUND(AVG(itm_skew_raw)::numeric, 5) AS avg_itm_skew,
    ROUND(100.0 * COUNT(*) FILTER (WHERE itm_skew_raw > 0) / NULLIF(COUNT(*) FILTER (WHERE itm_skew_raw IS NOT NULL),0), 1) AS pct_skew_bullish,
    ROUND(AVG(gamma_exposure_raw)::numeric, 1) AS avg_gamma_exp,
    ROUND(100.0 * COUNT(*) FILTER (WHERE gamma_exposure_raw > 0) / NULLIF(COUNT(*) FILTER (WHERE gamma_exposure_raw IS NOT NULL),0), 1) AS pct_gamma_bullish,
    ROUND(AVG(oi_change_diff_15m_raw)::numeric, 1) AS avg_oi_diff_15m,
    ROUND(100.0 * COUNT(*) FILTER (WHERE oi_change_diff_15m_raw > 0) / NULLIF(COUNT(*) FILTER (WHERE oi_change_diff_15m_raw IS NOT NULL),0), 1) AS pct_oidiff_bullish,
    ROUND(AVG(notional_log_ratio_raw)::numeric, 4) AS avg_notional_logratio,
    ROUND(100.0 * COUNT(*) FILTER (WHERE notional_log_ratio_raw > 0) / NULLIF(COUNT(*) FILTER (WHERE notional_log_ratio_raw IS NOT NULL),0), 1) AS pct_notional_bullish,
    ROUND(AVG(future_cvd_net5min)::numeric, 1) AS avg_future_cvd,
    ROUND(100.0 * COUNT(*) FILTER (WHERE future_cvd_net5min > 0) / NULLIF(COUNT(*) FILTER (WHERE future_cvd_net5min IS NOT NULL),0), 1) AS pct_cvd_bullish
FROM tagged
GROUP BY label
ORDER BY label;
