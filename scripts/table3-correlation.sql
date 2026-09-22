-- 2026-09-12: first correlation pass on StrikeBandCadenceSnapshot's already-built columns, against
-- the tracked future's own forward/backward price change (same target series every other
-- correlation check this project has run uses, joined via CadenceContextId). Screening pass:
-- pooled across all 4 days, near-week expiry only, all 4 bands x both cadence granularities, on
-- the call-minus-put difference of CVD volume net / CVD notional net / OI change sum -- a natural
-- "net directional pressure from the option chain" reading. Forward AND backward, same discipline
-- as every prior metric this project has evaluated.
WITH cc AS (
    SELECT "Id", "AsOfDate", "Timestamp", "FutureChangeFromLastCadence" AS chg
    FROM "CadenceContexts"
    WHERE "FutureChangeFromLastCadence" IS NOT NULL
),
windowed AS (
    SELECT "Id",
        SUM(chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN INTERVAL '5 minutes' PRECEDING AND CURRENT ROW) AS bwd_5m,
        SUM(chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN INTERVAL '15 minutes' PRECEDING AND CURRENT ROW) AS bwd_15m,
        SUM(chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN CURRENT ROW AND INTERVAL '5 minutes' FOLLOWING) - chg AS fwd_5m,
        SUM(chg) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp"
            RANGE BETWEEN CURRENT ROW AND INTERVAL '15 minutes' FOLLOWING) - chg AS fwd_15m
    FROM cc
),
near_week AS (
    SELECT b.*, w.bwd_5m, w.bwd_15m, w.fwd_5m, w.fwd_15m,
        (b."CallCvdProxyVolumeNet" - b."PutCvdProxyVolumeNet") AS cvd_vol_diff,
        (b."CallCvdProxyNotionalNet" - b."PutCvdProxyNotionalNet") AS cvd_notional_diff,
        (b."CallOiChangeSum" - b."PutOiChangeSum") AS oi_change_diff
    FROM "StrikeBandCadenceSnapshots" b
    JOIN windowed w ON w."Id" = b."CadenceContextId"
    WHERE b."ExpiryDate" = (SELECT MIN("ExpiryDate") FROM "StrikeBandCadenceSnapshots" WHERE "AsOfDate" = b."AsOfDate")
)
SELECT "BandDefinition", "CadenceMinutes",
    COUNT(cvd_vol_diff) AS n_cvdvol,
    ROUND(CORR(cvd_vol_diff, bwd_5m)::numeric,3) AS cvdvol_bwd5m,
    ROUND(CORR(cvd_vol_diff, fwd_5m)::numeric,3) AS cvdvol_fwd5m,
    ROUND(CORR(cvd_vol_diff, fwd_15m)::numeric,3) AS cvdvol_fwd15m,
    ROUND(CORR(cvd_notional_diff, fwd_5m)::numeric,3) AS cvdnotional_fwd5m,
    ROUND(CORR(cvd_notional_diff, fwd_15m)::numeric,3) AS cvdnotional_fwd15m,
    ROUND(CORR(oi_change_diff, fwd_5m)::numeric,3) AS oichg_fwd5m,
    ROUND(CORR(oi_change_diff, fwd_15m)::numeric,3) AS oichg_fwd15m
FROM near_week
GROUP BY 1,2
ORDER BY 2,1;
