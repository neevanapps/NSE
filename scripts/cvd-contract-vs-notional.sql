-- 2026-09-12: CVD-only deep dive, per user instruction (one metric at a time). Compares
-- contract-based CVD diff (CallCvdProxyVolumeNet - PutCvdProxyVolumeNet) against notional-based
-- CVD diff (CallCvdProxyNotionalNet - PutCvdProxyNotionalNet), across both cadence granularities
-- (5/15 min), both tracked expiries (near-week/current, next-week), and all 4 bands -- pooled
-- across all 4 days first, to see whether band choice matters for CVD the way it didn't for
-- OI-change-diff before collapsing that dimension in later, more detailed queries.
WITH cc AS (
    SELECT "Id", "AsOfDate", "Timestamp", "FutureChangeFromLastCadence" AS chg
    FROM "CadenceContexts"
    WHERE "FutureChangeFromLastCadence" IS NOT NULL
),
windowed AS (
    SELECT "Id",
        SUM(chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN CURRENT ROW AND INTERVAL '5 minutes' FOLLOWING) - chg AS fwd_5m,
        SUM(chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN CURRENT ROW AND INTERVAL '15 minutes' FOLLOWING) - chg AS fwd_15m
    FROM cc
),
tagged AS (
    SELECT b.*, w.fwd_5m, w.fwd_15m,
        (b."CallCvdProxyVolumeNet" - b."PutCvdProxyVolumeNet") AS cvd_vol_diff,
        (b."CallCvdProxyNotionalNet" - b."PutCvdProxyNotionalNet") AS cvd_notional_diff,
        CASE WHEN b."ExpiryDate" = (SELECT MIN("ExpiryDate") FROM "StrikeBandCadenceSnapshots" WHERE "AsOfDate" = b."AsOfDate")
             THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label
    FROM "StrikeBandCadenceSnapshots" b
    JOIN windowed w ON w."Id" = b."CadenceContextId"
)
SELECT week_label, "CadenceMinutes", "BandDefinition",
    COUNT(cvd_vol_diff) AS n,
    ROUND(CORR(cvd_vol_diff, fwd_5m)::numeric,3) AS contract_fwd5m,
    ROUND(CORR(cvd_vol_diff, fwd_15m)::numeric,3) AS contract_fwd15m,
    ROUND(CORR(cvd_notional_diff, fwd_5m)::numeric,3) AS notional_fwd5m,
    ROUND(CORR(cvd_notional_diff, fwd_15m)::numeric,3) AS notional_fwd15m
FROM tagged
GROUP BY 1,2,3
ORDER BY 1,2,3;
