-- 2026-09-13: OiBuildupNet retest with a ~4-minute OI-change window, closer to live's real
-- OiLookbackWindow (FeatureWindowLengths.OiComparisonWindow). Two variants:
--   A) EXACT live pairing: 4-min OI change + previous-SINGLE-15s-cadence spot change (the
--      documented F50 mismatch -- live's actual current formula, warts and all).
--   B) Self-consistent pairing: 4-min OI change + matching 4-min spot change.
-- Same classifier/sign table as oi-buildup-net-vs-price.sql, same ATM+-2 band, both option types.
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
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."StrikePrice", s."StrikeOffsetFromAtm", s."OpenInterest",
        CASE WHEN wm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate"
    WHERE s."StrikeOffsetFromAtm" BETWEEN -2 AND 2
),
oi_grouped AS (
    SELECT *, COUNT("OpenInterest") OVER (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice" ORDER BY "Timestamp") AS grp
    FROM all_strikes
),
oi_filled AS (
    SELECT "AsOfDate", "Timestamp", week_label, "OptionType", "StrikePrice",
        FIRST_VALUE("OpenInterest") OVER (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice", grp ORDER BY "Timestamp") AS oi_ffilled
    FROM oi_grouped
),
oi_bucket AS (
    SELECT *,
        oi_ffilled - LAG(oi_ffilled, 16) OVER w AS oi_change_4min,
        "Timestamp" - LAG("Timestamp", 16) OVER w AS oi_elapsed_4min
    FROM oi_filled
    WINDOW w AS (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice" ORDER BY "Timestamp")
),
classified AS (
    SELECT o."AsOfDate", o."Timestamp", o.week_label,
        -- Variant A: exact live pairing (4-min OI, prev-cadence spot)
        CASE
            WHEN o.oi_change_4min IS NULL OR sb.spot_change_prevcadence IS NULL
                 OR o.oi_elapsed_4min NOT BETWEEN INTERVAL '3 minutes 45 seconds' AND INTERVAL '4 minutes 15 seconds'
                 OR sb.spot_elapsed_prevcadence <> INTERVAL '15 seconds'
                 OR o.oi_change_4min = 0 OR sb.spot_change_prevcadence = 0
            THEN 0
            WHEN o."OptionType" = 1 AND sb.spot_change_prevcadence > 0 AND o.oi_change_4min > 0 THEN 1
            WHEN o."OptionType" = 1 AND sb.spot_change_prevcadence > 0 AND o.oi_change_4min < 0 THEN 1
            WHEN o."OptionType" = 1 AND sb.spot_change_prevcadence < 0 AND o.oi_change_4min > 0 THEN -1
            WHEN o."OptionType" = 1 AND sb.spot_change_prevcadence < 0 AND o.oi_change_4min < 0 THEN -1
            WHEN o."OptionType" = 2 AND sb.spot_change_prevcadence < 0 AND o.oi_change_4min > 0 THEN 1
            WHEN o."OptionType" = 2 AND sb.spot_change_prevcadence < 0 AND o.oi_change_4min < 0 THEN 1
            WHEN o."OptionType" = 2 AND sb.spot_change_prevcadence > 0 AND o.oi_change_4min > 0 THEN -1
            WHEN o."OptionType" = 2 AND sb.spot_change_prevcadence > 0 AND o.oi_change_4min < 0 THEN -1
            ELSE 0
        END * ABS(COALESCE(o.oi_change_4min, 0)) AS signed_vote_A_livepairing,
        -- Variant B: self-consistent pairing (4-min OI, 4-min spot)
        CASE
            WHEN o.oi_change_4min IS NULL OR sb.spot_change_4min IS NULL
                 OR o.oi_elapsed_4min NOT BETWEEN INTERVAL '3 minutes 45 seconds' AND INTERVAL '4 minutes 15 seconds'
                 OR sb.spot_elapsed_4min NOT BETWEEN INTERVAL '3 minutes 45 seconds' AND INTERVAL '4 minutes 15 seconds'
                 OR o.oi_change_4min = 0 OR sb.spot_change_4min = 0
            THEN 0
            WHEN o."OptionType" = 1 AND sb.spot_change_4min > 0 AND o.oi_change_4min > 0 THEN 1
            WHEN o."OptionType" = 1 AND sb.spot_change_4min > 0 AND o.oi_change_4min < 0 THEN 1
            WHEN o."OptionType" = 1 AND sb.spot_change_4min < 0 AND o.oi_change_4min > 0 THEN -1
            WHEN o."OptionType" = 1 AND sb.spot_change_4min < 0 AND o.oi_change_4min < 0 THEN -1
            WHEN o."OptionType" = 2 AND sb.spot_change_4min < 0 AND o.oi_change_4min > 0 THEN 1
            WHEN o."OptionType" = 2 AND sb.spot_change_4min < 0 AND o.oi_change_4min < 0 THEN 1
            WHEN o."OptionType" = 2 AND sb.spot_change_4min > 0 AND o.oi_change_4min > 0 THEN -1
            WHEN o."OptionType" = 2 AND sb.spot_change_4min > 0 AND o.oi_change_4min < 0 THEN -1
            ELSE 0
        END * ABS(COALESCE(o.oi_change_4min, 0)) AS signed_vote_B_consistent
    FROM oi_bucket o
    JOIN spot_bucket sb ON sb."AsOfDate" = o."AsOfDate" AND sb."Timestamp" = o."Timestamp"
),
net AS (
    SELECT "AsOfDate", "Timestamp", week_label,
        SUM(signed_vote_A_livepairing) AS net_A,
        SUM(signed_vote_B_consistent) AS net_B
    FROM classified GROUP BY 1,2,3
),
all_strikes2 AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."StrikePrice", s."StrikeOffsetFromAtm", s."MarkPrice",
        CASE WHEN wm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate"
),
grouped2 AS (
    SELECT *, COUNT("MarkPrice") OVER (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice" ORDER BY "Timestamp") AS grp
    FROM all_strikes2
),
filled2 AS (
    SELECT "AsOfDate", "Timestamp", week_label, "OptionType", "StrikePrice", "StrikeOffsetFromAtm",
        FIRST_VALUE("MarkPrice") OVER (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice", grp ORDER BY "Timestamp") AS mark_ffilled
    FROM grouped2
),
with_lookahead AS (
    SELECT *,
        LEAD(mark_ffilled, 20) OVER w AS price_5m_later, LEAD("Timestamp", 20) OVER w AS ts_5m_later,
        LEAD(mark_ffilled, 60) OVER w AS price_15m_later, LEAD("Timestamp", 60) OVER w AS ts_15m_later
    FROM filled2
    WINDOW w AS (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice" ORDER BY "Timestamp")
),
option_changes AS (
    SELECT "AsOfDate", "Timestamp", week_label, "OptionType",
        CASE WHEN ts_5m_later - "Timestamp" BETWEEN INTERVAL '4 minutes 30 seconds' AND INTERVAL '5 minutes 30 seconds'
             THEN price_5m_later - mark_ffilled END AS opt_fwd_5m,
        CASE WHEN ts_15m_later - "Timestamp" BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN price_15m_later - mark_ffilled END AS opt_fwd_15m
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
        LEAD(future_ffilled, 60) OVER w AS future_15m, LEAD("Timestamp", 60) OVER w AS ts_15m
    FROM future_filled
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
),
future_changes AS (
    SELECT "AsOfDate", "Timestamp",
        CASE WHEN ts_5m - "Timestamp" BETWEEN INTERVAL '4 minutes 30 seconds' AND INTERVAL '5 minutes 30 seconds'
             THEN future_5m - future_ffilled END AS future_fwd_5m,
        CASE WHEN ts_15m - "Timestamp" BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN future_15m - future_ffilled END AS future_fwd_15m
    FROM future_lead
)
SELECT n."AsOfDate", n.week_label,
    COUNT(*) FILTER (WHERE n.net_A <> 0) AS n_nonzero_A,
    ROUND(CORR(n.net_A, oc.opt_fwd_5m)::numeric,3) AS A_vs_option_fwd5m,
    ROUND(CORR(n.net_A, oc.opt_fwd_15m)::numeric,3) AS A_vs_option_fwd15m,
    ROUND(CORR(n.net_A, fc.future_fwd_5m)::numeric,3) AS A_vs_future_fwd5m,
    ROUND(CORR(n.net_A, fc.future_fwd_15m)::numeric,3) AS A_vs_future_fwd15m,
    ROUND(CORR(n.net_B, oc.opt_fwd_5m)::numeric,3) AS B_vs_option_fwd5m,
    ROUND(CORR(n.net_B, oc.opt_fwd_15m)::numeric,3) AS B_vs_option_fwd15m,
    ROUND(CORR(n.net_B, fc.future_fwd_5m)::numeric,3) AS B_vs_future_fwd5m,
    ROUND(CORR(n.net_B, fc.future_fwd_15m)::numeric,3) AS B_vs_future_fwd15m
FROM net n
JOIN option_changes oc ON oc."AsOfDate" = n."AsOfDate" AND oc."Timestamp" = n."Timestamp" AND oc.week_label = n.week_label AND oc."OptionType" = 1
JOIN future_changes fc ON fc."AsOfDate" = n."AsOfDate" AND fc."Timestamp" = n."Timestamp"
GROUP BY 1,2
ORDER BY 2,1;
