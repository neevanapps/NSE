-- 2026-09-13: OiBuildupNet -- the single largest live-weighted component (0.3125), never before
-- tested in this lab. Replicates LiveFeatureEngine.ComputeOiBuildupNet's exact classifier/sign
-- table (OiBuildupClassifier.Classify + the call/put sign-flip lookup), but computed on BUCKET-LEVEL
-- (5-min/15-min) spot and OI changes rather than live's single-cadence/~4-min-lookback values --
-- deliberately avoiding the "OI refresh is lumpy, single-cadence delta is usually zero" trap
-- (the same F50 bug already found and fixed live), same reasoning CallOiChangeSum/PutOiChangeSum
-- already use. Band = ATM+-2, both option types (matches Strike5's own offset range). Both weeks
-- tested (unlike OI-diff, not assuming the ThisWeek-only restriction transfers without checking).
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
-- Spot: one shared bucket-level change per (AsOfDate, Timestamp), forward-filled.
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
        spot_ffilled - LAG(spot_ffilled, 20) OVER w AS spot_change_5m,
        "Timestamp" - LAG("Timestamp", 20) OVER w AS spot_elapsed_5m,
        spot_ffilled - LAG(spot_ffilled, 60) OVER w AS spot_change_15m,
        "Timestamp" - LAG("Timestamp", 60) OVER w AS spot_elapsed_15m
    FROM spot_filled
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
),
-- Per-strike OI, ATM+-2 both option types, forward-filled, bucket-level change.
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
        oi_ffilled - LAG(oi_ffilled, 20) OVER w AS oi_change_5m,
        "Timestamp" - LAG("Timestamp", 20) OVER w AS oi_elapsed_5m,
        oi_ffilled - LAG(oi_ffilled, 60) OVER w AS oi_change_15m,
        "Timestamp" - LAG("Timestamp", 60) OVER w AS oi_elapsed_15m
    FROM oi_filled
    WINDOW w AS (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice" ORDER BY "Timestamp")
),
-- Classify per strike (5-min window), exact live sign table.
classified_5m AS (
    SELECT o."AsOfDate", o."Timestamp", o.week_label,
        CASE
            WHEN o.oi_change_5m IS NULL OR sb.spot_change_5m IS NULL
                 OR o.oi_elapsed_5m NOT BETWEEN INTERVAL '4 minutes 30 seconds' AND INTERVAL '5 minutes 30 seconds'
                 OR sb.spot_elapsed_5m NOT BETWEEN INTERVAL '4 minutes 30 seconds' AND INTERVAL '5 minutes 30 seconds'
                 OR o.oi_change_5m = 0 OR sb.spot_change_5m = 0
            THEN 0
            WHEN o."OptionType" = 1 AND sb.spot_change_5m > 0 AND o.oi_change_5m > 0 THEN 1   -- Call LongBuildup
            WHEN o."OptionType" = 1 AND sb.spot_change_5m > 0 AND o.oi_change_5m < 0 THEN 1   -- Call ShortCovering
            WHEN o."OptionType" = 1 AND sb.spot_change_5m < 0 AND o.oi_change_5m > 0 THEN -1  -- Call ShortBuildup
            WHEN o."OptionType" = 1 AND sb.spot_change_5m < 0 AND o.oi_change_5m < 0 THEN -1  -- Call LongUnwinding
            WHEN o."OptionType" = 2 AND sb.spot_change_5m < 0 AND o.oi_change_5m > 0 THEN 1   -- Put ShortBuildup
            WHEN o."OptionType" = 2 AND sb.spot_change_5m < 0 AND o.oi_change_5m < 0 THEN 1   -- Put LongUnwinding
            WHEN o."OptionType" = 2 AND sb.spot_change_5m > 0 AND o.oi_change_5m > 0 THEN -1  -- Put LongBuildup
            WHEN o."OptionType" = 2 AND sb.spot_change_5m > 0 AND o.oi_change_5m < 0 THEN -1  -- Put ShortCovering
            ELSE 0
        END * ABS(COALESCE(o.oi_change_5m, 0)) AS signed_vote_5m
    FROM oi_bucket o
    JOIN spot_bucket sb ON sb."AsOfDate" = o."AsOfDate" AND sb."Timestamp" = o."Timestamp"
),
classified_15m AS (
    SELECT o."AsOfDate", o."Timestamp", o.week_label,
        CASE
            WHEN o.oi_change_15m IS NULL OR sb.spot_change_15m IS NULL
                 OR o.oi_elapsed_15m NOT BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
                 OR sb.spot_elapsed_15m NOT BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
                 OR o.oi_change_15m = 0 OR sb.spot_change_15m = 0
            THEN 0
            WHEN o."OptionType" = 1 AND sb.spot_change_15m > 0 AND o.oi_change_15m > 0 THEN 1
            WHEN o."OptionType" = 1 AND sb.spot_change_15m > 0 AND o.oi_change_15m < 0 THEN 1
            WHEN o."OptionType" = 1 AND sb.spot_change_15m < 0 AND o.oi_change_15m > 0 THEN -1
            WHEN o."OptionType" = 1 AND sb.spot_change_15m < 0 AND o.oi_change_15m < 0 THEN -1
            WHEN o."OptionType" = 2 AND sb.spot_change_15m < 0 AND o.oi_change_15m > 0 THEN 1
            WHEN o."OptionType" = 2 AND sb.spot_change_15m < 0 AND o.oi_change_15m < 0 THEN 1
            WHEN o."OptionType" = 2 AND sb.spot_change_15m > 0 AND o.oi_change_15m > 0 THEN -1
            WHEN o."OptionType" = 2 AND sb.spot_change_15m > 0 AND o.oi_change_15m < 0 THEN -1
            ELSE 0
        END * ABS(COALESCE(o.oi_change_15m, 0)) AS signed_vote_15m
    FROM oi_bucket o
    JOIN spot_bucket sb ON sb."AsOfDate" = o."AsOfDate" AND sb."Timestamp" = o."Timestamp"
),
net_5m AS (
    SELECT "AsOfDate", "Timestamp", week_label, SUM(signed_vote_5m) AS oi_buildup_net_5m
    FROM classified_5m GROUP BY 1,2,3
),
net_15m AS (
    SELECT "AsOfDate", "Timestamp", week_label, SUM(signed_vote_15m) AS oi_buildup_net_15m
    FROM classified_15m GROUP BY 1,2,3
),
-- Forward price targets: ATM option (strike-identity-safe) and future, both fwd5m/fwd15m.
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
SELECT n5."AsOfDate", n5.week_label,
    COUNT(*) FILTER (WHERE n5.oi_buildup_net_5m <> 0) AS n_5m_nonzero,
    ROUND(CORR(n5.oi_buildup_net_5m, oc.opt_fwd_5m)::numeric,3) AS net5m_vs_option_fwd5m,
    ROUND(CORR(n5.oi_buildup_net_5m, oc.opt_fwd_15m)::numeric,3) AS net5m_vs_option_fwd15m,
    ROUND(CORR(n5.oi_buildup_net_5m, fc.future_fwd_5m)::numeric,3) AS net5m_vs_future_fwd5m,
    ROUND(CORR(n5.oi_buildup_net_5m, fc.future_fwd_15m)::numeric,3) AS net5m_vs_future_fwd15m,
    ROUND(CORR(n15.oi_buildup_net_15m, oc.opt_fwd_5m)::numeric,3) AS net15m_vs_option_fwd5m,
    ROUND(CORR(n15.oi_buildup_net_15m, oc.opt_fwd_15m)::numeric,3) AS net15m_vs_option_fwd15m,
    ROUND(CORR(n15.oi_buildup_net_15m, fc.future_fwd_5m)::numeric,3) AS net15m_vs_future_fwd5m,
    ROUND(CORR(n15.oi_buildup_net_15m, fc.future_fwd_15m)::numeric,3) AS net15m_vs_future_fwd15m
FROM net_5m n5
JOIN net_15m n15 ON n15."AsOfDate" = n5."AsOfDate" AND n15."Timestamp" = n5."Timestamp" AND n15.week_label = n5.week_label
JOIN option_changes oc ON oc."AsOfDate" = n5."AsOfDate" AND oc."Timestamp" = n5."Timestamp" AND oc.week_label = n5.week_label AND oc."OptionType" = 1
JOIN future_changes fc ON fc."AsOfDate" = n5."AsOfDate" AND fc."Timestamp" = n5."Timestamp"
GROUP BY 1,2
ORDER BY 2,1;
