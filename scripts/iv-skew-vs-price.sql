-- 2026-09-13: IV skew (PutAvgIv - CallAvgIv, level) first test. Genuinely different character from
-- every prior Table 3 candidate -- a pricing-surface read, not an activity/position-flow read.
-- Both legs already solved against the same parity-consistent underlying, so the pooled level sits
-- close to zero by construction; any real signal is in the sign/movement of the thin spread.
-- Strike-identity-safe/time-guarded forward-window template throughout for the option-price target;
-- future price forward-filled from CadenceContexts for the future target. Per-day, all 4 bands,
-- both cadences, level AND its own cadence-to-cadence delta.
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
all_strikes AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."StrikePrice", s."StrikeOffsetFromAtm", s."MarkPrice",
        CASE WHEN wm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate"
),
grouped AS (
    SELECT *, COUNT("MarkPrice") OVER (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice" ORDER BY "Timestamp") AS grp
    FROM all_strikes
),
filled AS (
    SELECT "AsOfDate", "Timestamp", week_label, "OptionType", "StrikePrice", "StrikeOffsetFromAtm",
        FIRST_VALUE("MarkPrice") OVER (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice", grp ORDER BY "Timestamp") AS mark_ffilled
    FROM grouped
),
with_lookahead AS (
    SELECT *,
        LEAD(mark_ffilled, 20) OVER w AS price_5m_later, LEAD("Timestamp", 20) OVER w AS ts_5m_later,
        LEAD(mark_ffilled, 60) OVER w AS price_15m_later, LEAD("Timestamp", 60) OVER w AS ts_15m_later
    FROM filled
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
),
band_week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeBandCadenceSnapshots") d
),
skew AS (
    SELECT b."AsOfDate", b."Timestamp", b."CadenceMinutes", b."BandDefinition",
        CASE WHEN bwm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label,
        (b."PutAvgIv" - b."CallAvgIv") AS skew_level
    FROM "StrikeBandCadenceSnapshots" b
    JOIN band_week_map bwm ON bwm."AsOfDate" = b."AsOfDate" AND bwm."ExpiryDate" = b."ExpiryDate"
),
skew_delta AS (
    SELECT *,
        skew_level - LAG(skew_level) OVER (PARTITION BY "AsOfDate", week_label, "CadenceMinutes", "BandDefinition" ORDER BY "Timestamp") AS skew_change
    FROM skew
)
SELECT s."AsOfDate", s.week_label, s."CadenceMinutes", s."BandDefinition",
    COUNT(s.skew_level) AS n,
    ROUND(CORR(s.skew_level, oc.opt_fwd_5m)::numeric,3) AS level_vs_optcall_fwd5m,
    ROUND(CORR(s.skew_level, oc.opt_fwd_15m)::numeric,3) AS level_vs_optcall_fwd15m,
    ROUND(CORR(s.skew_level, fc.future_fwd_5m)::numeric,3) AS level_vs_future_fwd5m,
    ROUND(CORR(s.skew_level, fc.future_fwd_15m)::numeric,3) AS level_vs_future_fwd15m,
    ROUND(CORR(s.skew_change, fc.future_fwd_5m)::numeric,3) AS delta_vs_future_fwd5m,
    ROUND(CORR(s.skew_change, fc.future_fwd_15m)::numeric,3) AS delta_vs_future_fwd15m
FROM skew_delta s
JOIN option_changes oc ON oc."AsOfDate" = s."AsOfDate" AND oc."Timestamp" = s."Timestamp" AND oc.week_label = s.week_label AND oc."OptionType" = 1
JOIN future_changes fc ON fc."AsOfDate" = s."AsOfDate" AND fc."Timestamp" = s."Timestamp"
GROUP BY 1,2,3,4
ORDER BY 2,3,4,1;
