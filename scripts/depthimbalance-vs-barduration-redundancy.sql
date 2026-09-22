-- 2026-09-17: DepthImbalance/BarDurationUrgency redundancy check -- the open item flagged when
-- both survived the volume-bar-cadence evaluation cycle (see docs/VOLUME_BAR_FINDINGS.md). Same
-- discipline as the time-cadence pipeline's own redundancy checks (see
-- scripts/pcroi-vs-oidiff-redundancy.sql, scripts/itm-vs-otm-skew-redundancy.sql): correlate the
-- two metrics' RAW values directly against EACH OTHER (not against price/PnL), level-vs-level
-- and delta-vs-delta, before assuming a combination would add independent weight.
--
-- Run against a single SHARED bar threshold (1300 -- both metrics have solid target-zone
-- calibration rows there) so the two series are aligned bar-for-bar; DepthImbalance's own best
-- combo (1300/95) and BarDurationUrgency's own best combo (2600/90) sit on different thresholds
-- and are not directly comparable without this alignment.
--
-- BarDurationUrgency's raw pre-rank value is reconstructed here exactly as
-- NiftySignal.VolumeBarData.TradeSimulator.ComputeBarDurationScore computes it:
-- SIGN(ClosePrice - previous bar's ClosePrice) / DurationSeconds -- signed by the bar's own price
-- direction, magnitude is fill speed. SIGN(0) = 0 already handles the "price unchanged" case the
-- same way the C# does (score 0, no direction), so no special-casing is needed here. The first
-- bar of each day has no previous close and is excluded, matching the C#'s own null handling.
--
-- DepthImbalance's raw value is FutureDepthImbalance, already persisted per bar -- no
-- reconstruction needed, it IS the level TradeSimulator session-rank-normalizes.
WITH bars AS (
    SELECT
        "AsOfDate",
        "BarIndex",
        "FutureDepthImbalance" AS depth_imbalance,
        SIGN("ClosePrice" - LAG("ClosePrice") OVER (PARTITION BY "AsOfDate" ORDER BY "BarIndex"))
            / NULLIF("DurationSeconds", 0) AS bar_urgency
    FROM "VolumeBars"
    WHERE "BarVolumeThreshold" = 1300
      AND "AsOfDate" BETWEEN '2026-09-08' AND '2026-09-16'
),
with_delta AS (
    SELECT *,
        depth_imbalance - LAG(depth_imbalance) OVER (PARTITION BY "AsOfDate" ORDER BY "BarIndex") AS depth_delta,
        bar_urgency - LAG(bar_urgency) OVER (PARTITION BY "AsOfDate" ORDER BY "BarIndex") AS urgency_delta
    FROM bars
    WHERE depth_imbalance IS NOT NULL AND bar_urgency IS NOT NULL
)
SELECT "AsOfDate"::text AS as_of_date, COUNT(*) AS n,
    ROUND(CORR(depth_imbalance, bar_urgency)::numeric, 3) AS level_vs_level,
    ROUND(CORR(depth_delta, urgency_delta)::numeric, 3) AS delta_vs_delta
FROM with_delta
GROUP BY GROUPING SETS (("AsOfDate"), ())
ORDER BY as_of_date NULLS LAST;
