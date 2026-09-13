-- 2026-09-13: ITM-band skew (Itm2Atm1, PutAvgIv-CallAvgIv) vs OTM 25-delta skew (nearest-strike
-- approximation, putIv/callIv) -- direct cross-correlation, same methodology as the PCR-OI/OI-diff
-- redundancy check. Both already showed OPPOSITE signs vs price (ITM: put-skew-up -> price up;
-- OTM: put-skew-up -> price down) -- if they're capturing the same underlying smile information,
-- their own raw values should be strongly correlated with each other (negatively, given the
-- opposite price relationship). Weak/near-zero direct correlation would mean they're carrying
-- more independent information than the opposite price-signs alone suggest. ThisWeek only, all 4
-- days (08 Sep included for completeness even though excluded from both individual findings).
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
-- OTM 25-delta skew, computed fresh per 15s cadence (same logic as ratio-ivskew25delta.sql)
all_strikes AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."StrikePrice", s."Delta", s."ImpliedVolatility"
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate" AND wm.week_rank = 1
    WHERE s."StrikeOffsetFromAtm" BETWEEN -10 AND 10
),
grouped AS (
    SELECT *, COUNT("Delta") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice" ORDER BY "Timestamp") AS grp
    FROM all_strikes
),
filled AS (
    SELECT "AsOfDate", "Timestamp", "OptionType", "StrikePrice",
        FIRST_VALUE("Delta") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice", grp ORDER BY "Timestamp") AS delta_f,
        FIRST_VALUE("ImpliedVolatility") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice", grp ORDER BY "Timestamp") AS iv_f
    FROM grouped
),
ranked AS (
    SELECT *, ROW_NUMBER() OVER (PARTITION BY "AsOfDate", "Timestamp", "OptionType" ORDER BY ABS(ABS(delta_f) - 0.25)) AS rn
    FROM filled
    WHERE delta_f IS NOT NULL AND iv_f IS NOT NULL
),
nearest_25d AS (
    SELECT "AsOfDate", "Timestamp", "OptionType", iv_f
    FROM ranked WHERE rn = 1
),
otm_skew AS (
    SELECT c."AsOfDate", c."Timestamp",
        CASE WHEN c.iv_f > 0 THEN p.iv_f / c.iv_f END AS otm_skew_level
    FROM nearest_25d c
    JOIN nearest_25d p ON p."AsOfDate" = c."AsOfDate" AND p."Timestamp" = c."Timestamp" AND p."OptionType" = 2
    WHERE c."OptionType" = 1
),
-- ITM-band skew, from StrikeBandCadenceSnapshot (5-min boundary cadence)
band_week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeBandCadenceSnapshots") d
),
itm_skew AS (
    SELECT b."AsOfDate", b."Timestamp", (b."PutAvgIv" - b."CallAvgIv") AS itm_skew_level
    FROM "StrikeBandCadenceSnapshots" b
    JOIN band_week_map bwm ON bwm."AsOfDate" = b."AsOfDate" AND bwm."ExpiryDate" = b."ExpiryDate" AND bwm.week_rank = 1
    WHERE b."CadenceMinutes" = 5 AND b."BandDefinition" = 'Itm2Atm1'
),
combined AS (
    SELECT i."AsOfDate", i."Timestamp", i.itm_skew_level, o.otm_skew_level,
        i.itm_skew_level - LAG(i.itm_skew_level) OVER (PARTITION BY i."AsOfDate" ORDER BY i."Timestamp") AS itm_delta,
        o.otm_skew_level - LAG(o.otm_skew_level) OVER (PARTITION BY i."AsOfDate" ORDER BY i."Timestamp") AS otm_delta
    FROM itm_skew i
    JOIN otm_skew o ON o."AsOfDate" = i."AsOfDate" AND o."Timestamp" = i."Timestamp"
)
SELECT "AsOfDate", COUNT(*) AS n,
    ROUND(CORR(itm_skew_level, otm_skew_level)::numeric,3) AS level_vs_level,
    ROUND(CORR(itm_delta, otm_delta)::numeric,3) AS delta_vs_delta
FROM combined
GROUP BY 1
ORDER BY 1;
