-- 2026-09-13: ratio-composite metric 1, NotionalVolumeRatio -- confirmed exact formula from
-- ComputeRatioNotionalVolumeRaw (LiveFeatureEngine.cs:2704-2752): same notional calc as VolumePcr
-- (volumeDelta x mid, per cadence) but ATM+-5 band (RatioWideStrikeBand), floor of Rs 5,000 total
-- notional (MinNotionalForVolumeRatio), and INVERTED orientation: callNotional/putNotional (not
-- put/call like VolumePcr). ThisWeek only.
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
per_strike AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType",
        (CASE WHEN s."OptionType" = 1 THEN COALESCE(s."VolumeDelta",0) * COALESCE(s."MarkPrice",0) ELSE 0 END) AS call_notional,
        (CASE WHEN s."OptionType" = 2 THEN COALESCE(s."VolumeDelta",0) * COALESCE(s."MarkPrice",0) ELSE 0 END) AS put_notional
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate" AND wm.week_rank = 1
    WHERE s."StrikeOffsetFromAtm" BETWEEN -5 AND 5 AND s."VolumeDelta" > 0 AND s."MarkPrice" > 0
),
net_by_cadence AS (
    SELECT "AsOfDate", "Timestamp", SUM(call_notional) AS call_total, SUM(put_notional) AS put_total
    FROM per_strike GROUP BY 1,2
),
ratio AS (
    SELECT "AsOfDate", "Timestamp",
        CASE WHEN put_total > 0 AND (call_total + put_total) >= 5000 THEN call_total / put_total END AS raw_level
    FROM net_by_cadence
),
ratio_with_delta AS (
    SELECT *, raw_level - LAG(raw_level, 20) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp") AS raw_delta_5m
    FROM ratio
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
    COUNT(r.raw_level) AS n,
    ROUND(CORR(r.raw_level, oc.opt_fwd_5m)::numeric,3) AS raw_opt5, ROUND(CORR(r.raw_level, oc.opt_fwd_15m)::numeric,3) AS raw_opt15, ROUND(CORR(r.raw_level, oc.opt_fwd_30m)::numeric,3) AS raw_opt30,
    ROUND(CORR(r.raw_level, fc.future_fwd_5m)::numeric,3) AS raw_fut5, ROUND(CORR(r.raw_level, fc.future_fwd_15m)::numeric,3) AS raw_fut15, ROUND(CORR(r.raw_level, fc.future_fwd_30m)::numeric,3) AS raw_fut30,
    ROUND(CORR(r.raw_delta_5m, fc.future_fwd_5m)::numeric,3) AS rawD_fut5, ROUND(CORR(r.raw_delta_5m, fc.future_fwd_15m)::numeric,3) AS rawD_fut15
FROM ratio_with_delta r
JOIN option_changes oc ON oc."AsOfDate" = r."AsOfDate" AND oc."Timestamp" = r."Timestamp" AND oc."OptionType" = 1
JOIN future_changes fc ON fc."AsOfDate" = r."AsOfDate" AND fc."Timestamp" = r."Timestamp"
GROUP BY 1
ORDER BY 1;
