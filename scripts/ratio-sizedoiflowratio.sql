-- 2026-09-13: ratio-composite metric 2, SizedOiFlowRatio -- confirmed exact formula from
-- ComputeRatioSizedOiFlowRaw (LiveFeatureEngine.cs:1875-1936): (callConstructive+1)/(putConstructive+1),
-- sum of |oiChange| for classifier-confirmed constructive strikes only (call: LongBuildup/
-- ShortCovering; put: ShortBuildup/LongUnwinding -- same table as OiBuildupNet), ATM+-5 band,
-- floor of 50 contracts (MinContractsForOiFlow). Same spot/OI window mismatch as OiBuildupNet
-- (confirmed in the source's own comment) -- tested both ways again: A) exact live pairing
-- (~4-min OI change + previous-single-15s-cadence spot), B) self-consistent (~4-min/~4-min).
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
spot_grouped AS (
    SELECT "AsOfDate", "Timestamp",
        COUNT("SpotCloseFromLastCadence") OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp") AS spot_grp,
        "SpotCloseFromLastCadence"
    FROM "CadenceContexts"
),
spot_filled AS (
    SELECT "AsOfDate", "Timestamp",
        FIRST_VALUE("SpotCloseFromLastCadence") OVER (PARTITION BY "AsOfDate", spot_grp ORDER BY "Timestamp") AS spot_ffilled
    FROM spot_grouped
),
spot_bucket AS (
    SELECT "AsOfDate", "Timestamp",
        spot_ffilled - LAG(spot_ffilled, 1) OVER w AS spot_change_prevcadence,
        "Timestamp" - LAG("Timestamp", 1) OVER w AS spot_elapsed_prevcadence,
        spot_ffilled - LAG(spot_ffilled, 16) OVER w AS spot_change_4min,
        "Timestamp" - LAG("Timestamp", 16) OVER w AS spot_elapsed_4min
    FROM spot_filled
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
),
all_strikes AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."StrikePrice", s."StrikeOffsetFromAtm", s."OpenInterest"
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate" AND wm.week_rank = 1
    WHERE s."StrikeOffsetFromAtm" BETWEEN -5 AND 5
),
oi_grouped AS (
    SELECT *, COUNT("OpenInterest") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice" ORDER BY "Timestamp") AS grp
    FROM all_strikes
),
oi_filled AS (
    SELECT "AsOfDate", "Timestamp", "OptionType", "StrikePrice",
        FIRST_VALUE("OpenInterest") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice", grp ORDER BY "Timestamp") AS oi_ffilled
    FROM oi_grouped
),
oi_bucket AS (
    SELECT *,
        oi_ffilled - LAG(oi_ffilled, 16) OVER w AS oi_change_4min,
        "Timestamp" - LAG("Timestamp", 16) OVER w AS oi_elapsed_4min
    FROM oi_filled
    WINDOW w AS (PARTITION BY "AsOfDate", "OptionType", "StrikePrice" ORDER BY "Timestamp")
),
classified AS (
    SELECT o."AsOfDate", o."Timestamp",
        -- Variant A: exact live pairing
        CASE WHEN o.oi_change_4min IS NOT NULL AND sb.spot_change_prevcadence IS NOT NULL
                  AND o.oi_elapsed_4min BETWEEN INTERVAL '3 minutes 45 seconds' AND INTERVAL '4 minutes 15 seconds'
                  AND sb.spot_elapsed_prevcadence = INTERVAL '15 seconds'
                  AND o."OptionType" = 1 AND sb.spot_change_prevcadence > 0 AND o.oi_change_4min > 0 THEN ABS(o.oi_change_4min) ELSE 0 END
        + CASE WHEN o.oi_change_4min IS NOT NULL AND sb.spot_change_prevcadence IS NOT NULL
                  AND o.oi_elapsed_4min BETWEEN INTERVAL '3 minutes 45 seconds' AND INTERVAL '4 minutes 15 seconds'
                  AND sb.spot_elapsed_prevcadence = INTERVAL '15 seconds'
                  AND o."OptionType" = 1 AND sb.spot_change_prevcadence < 0 AND o.oi_change_4min < 0 THEN ABS(o.oi_change_4min) ELSE 0 END AS call_constructive_A,
        CASE WHEN o.oi_change_4min IS NOT NULL AND sb.spot_change_prevcadence IS NOT NULL
                  AND o.oi_elapsed_4min BETWEEN INTERVAL '3 minutes 45 seconds' AND INTERVAL '4 minutes 15 seconds'
                  AND sb.spot_elapsed_prevcadence = INTERVAL '15 seconds'
                  AND o."OptionType" = 2 AND sb.spot_change_prevcadence < 0 AND o.oi_change_4min > 0 THEN ABS(o.oi_change_4min) ELSE 0 END
        + CASE WHEN o.oi_change_4min IS NOT NULL AND sb.spot_change_prevcadence IS NOT NULL
                  AND o.oi_elapsed_4min BETWEEN INTERVAL '3 minutes 45 seconds' AND INTERVAL '4 minutes 15 seconds'
                  AND sb.spot_elapsed_prevcadence = INTERVAL '15 seconds'
                  AND o."OptionType" = 2 AND sb.spot_change_prevcadence > 0 AND o.oi_change_4min < 0 THEN ABS(o.oi_change_4min) ELSE 0 END AS put_constructive_A,
        -- Variant B: self-consistent pairing
        CASE WHEN o.oi_change_4min IS NOT NULL AND sb.spot_change_4min IS NOT NULL
                  AND o.oi_elapsed_4min BETWEEN INTERVAL '3 minutes 45 seconds' AND INTERVAL '4 minutes 15 seconds'
                  AND sb.spot_elapsed_4min BETWEEN INTERVAL '3 minutes 45 seconds' AND INTERVAL '4 minutes 15 seconds'
                  AND o."OptionType" = 1 AND sb.spot_change_4min > 0 AND o.oi_change_4min > 0 THEN ABS(o.oi_change_4min) ELSE 0 END
        + CASE WHEN o.oi_change_4min IS NOT NULL AND sb.spot_change_4min IS NOT NULL
                  AND o.oi_elapsed_4min BETWEEN INTERVAL '3 minutes 45 seconds' AND INTERVAL '4 minutes 15 seconds'
                  AND sb.spot_elapsed_4min BETWEEN INTERVAL '3 minutes 45 seconds' AND INTERVAL '4 minutes 15 seconds'
                  AND o."OptionType" = 1 AND sb.spot_change_4min < 0 AND o.oi_change_4min < 0 THEN ABS(o.oi_change_4min) ELSE 0 END AS call_constructive_B,
        CASE WHEN o.oi_change_4min IS NOT NULL AND sb.spot_change_4min IS NOT NULL
                  AND o.oi_elapsed_4min BETWEEN INTERVAL '3 minutes 45 seconds' AND INTERVAL '4 minutes 15 seconds'
                  AND sb.spot_elapsed_4min BETWEEN INTERVAL '3 minutes 45 seconds' AND INTERVAL '4 minutes 15 seconds'
                  AND o."OptionType" = 2 AND sb.spot_change_4min < 0 AND o.oi_change_4min > 0 THEN ABS(o.oi_change_4min) ELSE 0 END
        + CASE WHEN o.oi_change_4min IS NOT NULL AND sb.spot_change_4min IS NOT NULL
                  AND o.oi_elapsed_4min BETWEEN INTERVAL '3 minutes 45 seconds' AND INTERVAL '4 minutes 15 seconds'
                  AND sb.spot_elapsed_4min BETWEEN INTERVAL '3 minutes 45 seconds' AND INTERVAL '4 minutes 15 seconds'
                  AND o."OptionType" = 2 AND sb.spot_change_4min > 0 AND o.oi_change_4min < 0 THEN ABS(o.oi_change_4min) ELSE 0 END AS put_constructive_B
    FROM oi_bucket o
    JOIN spot_bucket sb ON sb."AsOfDate" = o."AsOfDate" AND sb."Timestamp" = o."Timestamp"
),
net AS (
    SELECT "AsOfDate", "Timestamp",
        SUM(call_constructive_A) AS call_A, SUM(put_constructive_A) AS put_A,
        SUM(call_constructive_B) AS call_B, SUM(put_constructive_B) AS put_B
    FROM classified GROUP BY 1,2
),
ratio AS (
    SELECT "AsOfDate", "Timestamp",
        CASE WHEN (call_A + put_A) >= 50 THEN (call_A + 1) / (put_A + 1) END AS ratio_A,
        CASE WHEN (call_B + put_B) >= 50 THEN (call_B + 1) / (put_B + 1) END AS ratio_B
    FROM net
),
all_strikes2 AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."StrikePrice", s."StrikeOffsetFromAtm", s."MarkPrice"
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate" AND wm.week_rank = 1
),
grouped2 AS (
    SELECT *, COUNT("MarkPrice") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice" ORDER BY "Timestamp") AS grp
    FROM all_strikes2
),
filled2 AS (
    SELECT "AsOfDate", "Timestamp", "OptionType", "StrikePrice", "StrikeOffsetFromAtm",
        FIRST_VALUE("MarkPrice") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice", grp ORDER BY "Timestamp") AS mark_ffilled
    FROM grouped2
),
with_lookahead AS (
    SELECT *,
        LEAD(mark_ffilled, 20) OVER w AS price_5m_later, LEAD("Timestamp", 20) OVER w AS ts_5m_later,
        LEAD(mark_ffilled, 60) OVER w AS price_15m_later, LEAD("Timestamp", 60) OVER w AS ts_15m_later,
        LEAD(mark_ffilled, 120) OVER w AS price_30m_later, LEAD("Timestamp", 120) OVER w AS ts_30m_later
    FROM filled2
    WINDOW w AS (PARTITION BY "AsOfDate", "OptionType", "StrikePrice" ORDER BY "Timestamp")
),
option_changes AS (
    SELECT "AsOfDate", "Timestamp", "OptionType",
        CASE WHEN ts_5m_later - "Timestamp" BETWEEN INTERVAL '4 minutes 30 seconds' AND INTERVAL '5 minutes 30 seconds'
             THEN price_5m_later - mark_ffilled END AS opt_fwd_5m,
        CASE WHEN ts_15m_later - "Timestamp" BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN price_15m_later - mark_ffilled END AS opt_fwd_15m,
        CASE WHEN ts_30m_later - "Timestamp" BETWEEN INTERVAL '29 minutes 30 seconds' AND INTERVAL '30 minutes 30 seconds'
             THEN price_30m_later - mark_ffilled END AS opt_fwd_30m
    FROM with_lookahead
    WHERE "StrikeOffsetFromAtm" = 0
),
future_grouped AS (
    SELECT "AsOfDate", "Timestamp",
        COUNT("FutureCloseFromLastCadence") OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp") AS future_grp,
        "FutureCloseFromLastCadence"
    FROM "CadenceContexts"
),
future_filled AS (
    SELECT "AsOfDate", "Timestamp",
        FIRST_VALUE("FutureCloseFromLastCadence") OVER (PARTITION BY "AsOfDate", future_grp ORDER BY "Timestamp") AS future_ffilled
    FROM future_grouped
),
future_lead AS (
    SELECT *,
        LEAD(future_ffilled, 20) OVER w AS future_5m, LEAD("Timestamp", 20) OVER w AS ts_5m,
        LEAD(future_ffilled, 60) OVER w AS future_15m, LEAD("Timestamp", 60) OVER w AS ts_15m,
        LEAD(future_ffilled, 120) OVER w AS future_30m, LEAD("Timestamp", 120) OVER w AS ts_30m
    FROM future_filled
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
),
future_changes AS (
    SELECT "AsOfDate", "Timestamp",
        CASE WHEN ts_5m - "Timestamp" BETWEEN INTERVAL '4 minutes 30 seconds' AND INTERVAL '5 minutes 30 seconds'
             THEN future_5m - future_ffilled END AS future_fwd_5m,
        CASE WHEN ts_15m - "Timestamp" BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN future_15m - future_ffilled END AS future_fwd_15m,
        CASE WHEN ts_30m - "Timestamp" BETWEEN INTERVAL '29 minutes 30 seconds' AND INTERVAL '30 minutes 30 seconds'
             THEN future_30m - future_ffilled END AS future_fwd_30m
    FROM future_lead
)
SELECT r."AsOfDate",
    COUNT(r.ratio_A) AS n_A, COUNT(r.ratio_B) AS n_B,
    ROUND(CORR(r.ratio_A, oc.opt_fwd_15m)::numeric,3) AS A_opt15, ROUND(CORR(r.ratio_A, fc.future_fwd_15m)::numeric,3) AS A_fut15, ROUND(CORR(r.ratio_A, fc.future_fwd_30m)::numeric,3) AS A_fut30,
    ROUND(CORR(r.ratio_B, oc.opt_fwd_15m)::numeric,3) AS B_opt15, ROUND(CORR(r.ratio_B, fc.future_fwd_15m)::numeric,3) AS B_fut15, ROUND(CORR(r.ratio_B, fc.future_fwd_30m)::numeric,3) AS B_fut30
FROM ratio r
JOIN option_changes oc ON oc."AsOfDate" = r."AsOfDate" AND oc."Timestamp" = r."Timestamp" AND oc."OptionType" = 1
JOIN future_changes fc ON fc."AsOfDate" = r."AsOfDate" AND fc."Timestamp" = r."Timestamp"
GROUP BY 1
ORDER BY 1;
