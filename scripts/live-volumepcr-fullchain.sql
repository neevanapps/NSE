-- 2026-09-13: live VolumePcr (putNotional/callNotional, raw ratio, notional = VolumeDelta x
-- MarkPrice PER CADENCE -- confirmed directly from ComputeVolumePcrAndCvdProxy, not the per-tick
-- weighted NotionalDelta column, a real distinction checked before reusing anything). Full chain
-- (ATM+-10, now matching live's "entire nearest-expiry chain"), ThisWeek only. Raw and log
-- transforms, level and delta, 5/15/30-min horizons, both targets, per day.
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
    WHERE s."StrikeOffsetFromAtm" BETWEEN -10 AND 10 AND s."VolumeDelta" > 0 AND s."MarkPrice" > 0
),
net_by_cadence AS (
    SELECT "AsOfDate", "Timestamp", SUM(call_notional) AS call_total, SUM(put_notional) AS put_total
    FROM per_strike GROUP BY 1,2
),
pcr AS (
    SELECT "AsOfDate", "Timestamp",
        CASE WHEN call_total > 0 THEN put_total / call_total END AS raw_level,
        CASE WHEN call_total > 0 AND put_total > 0 THEN LN(call_total/put_total) END AS log_level
    FROM net_by_cadence
),
pcr_with_delta AS (
    SELECT *,
        raw_level - LAG(raw_level, 20) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp") AS raw_delta_5m,
        log_level - LAG(log_level, 20) OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp") AS log_delta_5m
    FROM pcr
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
SELECT p."AsOfDate",
    COUNT(p.raw_level) AS n,
    ROUND(CORR(p.raw_level, oc.opt_fwd_5m)::numeric,3) AS raw_opt5, ROUND(CORR(p.raw_level, oc.opt_fwd_15m)::numeric,3) AS raw_opt15, ROUND(CORR(p.raw_level, oc.opt_fwd_30m)::numeric,3) AS raw_opt30,
    ROUND(CORR(p.raw_level, fc.future_fwd_5m)::numeric,3) AS raw_fut5, ROUND(CORR(p.raw_level, fc.future_fwd_15m)::numeric,3) AS raw_fut15, ROUND(CORR(p.raw_level, fc.future_fwd_30m)::numeric,3) AS raw_fut30,
    ROUND(CORR(p.log_level, fc.future_fwd_5m)::numeric,3) AS log_fut5, ROUND(CORR(p.log_level, fc.future_fwd_15m)::numeric,3) AS log_fut15, ROUND(CORR(p.log_level, fc.future_fwd_30m)::numeric,3) AS log_fut30,
    ROUND(CORR(p.raw_delta_5m, fc.future_fwd_5m)::numeric,3) AS rawD_fut5, ROUND(CORR(p.raw_delta_5m, fc.future_fwd_15m)::numeric,3) AS rawD_fut15,
    ROUND(CORR(p.log_delta_5m, fc.future_fwd_5m)::numeric,3) AS logD_fut5, ROUND(CORR(p.log_delta_5m, fc.future_fwd_15m)::numeric,3) AS logD_fut15
FROM pcr_with_delta p
JOIN option_changes oc ON oc."AsOfDate" = p."AsOfDate" AND oc."Timestamp" = p."Timestamp" AND oc."OptionType" = 1
JOIN future_changes fc ON fc."AsOfDate" = p."AsOfDate" AND fc."Timestamp" = p."Timestamp"
GROUP BY 1
ORDER BY 1;
