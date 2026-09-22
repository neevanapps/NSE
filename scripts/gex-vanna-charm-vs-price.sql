-- 2026-09-13: GammaExposure/VannaExposure/CharmExposure (version A: sum of PERSISTED per-strike
-- Greeks, each solved from that strike's own IV -- not live's shared-ATM-vol simplification, see
-- design discussion). Full ATM+-10 chain (now that the schema is widened to match), both option
-- types, ThisWeek only, sign convention exactly matching live (call:+Greek*OI, put:-Greek*OI).
-- Level AND its own bucket-level change, 5/15/30-min horizons, both option and future targets,
-- per day.
WITH week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
all_strikes AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."StrikePrice", s."StrikeOffsetFromAtm",
        s."OpenInterest", s."Gamma", s."Vanna", s."CharmPerDay"
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate" AND wm.week_rank = 1
    WHERE s."StrikeOffsetFromAtm" BETWEEN -10 AND 10
),
greek_grouped AS (
    SELECT *, COUNT("Gamma") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice" ORDER BY "Timestamp") AS ggrp,
        COUNT("OpenInterest") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice" ORDER BY "Timestamp") AS ogrp
    FROM all_strikes
),
filled AS (
    SELECT "AsOfDate", "Timestamp", "OptionType", "StrikePrice",
        FIRST_VALUE("Gamma") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice", ggrp ORDER BY "Timestamp") AS gamma_f,
        FIRST_VALUE("Vanna") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice", ggrp ORDER BY "Timestamp") AS vanna_f,
        FIRST_VALUE("CharmPerDay") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice", ggrp ORDER BY "Timestamp") AS charm_f,
        FIRST_VALUE("OpenInterest") OVER (PARTITION BY "AsOfDate", "OptionType", "StrikePrice", ogrp ORDER BY "Timestamp") AS oi_f
    FROM greek_grouped
),
contrib AS (
    SELECT "AsOfDate", "Timestamp",
        (CASE WHEN "OptionType" = 1 THEN 1 ELSE -1 END) * gamma_f * COALESCE(oi_f, 0) AS gex_c,
        (CASE WHEN "OptionType" = 1 THEN 1 ELSE -1 END) * vanna_f * COALESCE(oi_f, 0) AS vanna_c,
        (CASE WHEN "OptionType" = 1 THEN 1 ELSE -1 END) * charm_f * COALESCE(oi_f, 0) AS charm_c
    FROM filled
    WHERE gamma_f IS NOT NULL AND oi_f IS NOT NULL
),
net_by_cadence AS (
    SELECT "AsOfDate", "Timestamp",
        SUM(gex_c) AS gex_level, SUM(vanna_c) AS vanna_level, SUM(charm_c) AS charm_level
    FROM contrib GROUP BY 1,2
),
with_delta AS (
    SELECT *,
        gex_level - LAG(gex_level, 20) OVER w AS gex_delta_5m,
        vanna_level - LAG(vanna_level, 20) OVER w AS vanna_delta_5m,
        charm_level - LAG(charm_level, 20) OVER w AS charm_delta_5m
    FROM net_by_cadence
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
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
SELECT n."AsOfDate",
    COUNT(*) AS obs,
    ROUND(CORR(n.gex_level, oc.opt_fwd_5m)::numeric,3) AS gex_opt5, ROUND(CORR(n.gex_level, oc.opt_fwd_15m)::numeric,3) AS gex_opt15, ROUND(CORR(n.gex_level, oc.opt_fwd_30m)::numeric,3) AS gex_opt30,
    ROUND(CORR(n.gex_level, fc.future_fwd_5m)::numeric,3) AS gex_fut5, ROUND(CORR(n.gex_level, fc.future_fwd_15m)::numeric,3) AS gex_fut15, ROUND(CORR(n.gex_level, fc.future_fwd_30m)::numeric,3) AS gex_fut30,
    ROUND(CORR(n.gex_delta_5m, fc.future_fwd_5m)::numeric,3) AS gexD_fut5, ROUND(CORR(n.gex_delta_5m, fc.future_fwd_15m)::numeric,3) AS gexD_fut15,
    ROUND(CORR(n.vanna_level, oc.opt_fwd_5m)::numeric,3) AS van_opt5, ROUND(CORR(n.vanna_level, oc.opt_fwd_15m)::numeric,3) AS van_opt15, ROUND(CORR(n.vanna_level, oc.opt_fwd_30m)::numeric,3) AS van_opt30,
    ROUND(CORR(n.vanna_level, fc.future_fwd_5m)::numeric,3) AS van_fut5, ROUND(CORR(n.vanna_level, fc.future_fwd_15m)::numeric,3) AS van_fut15, ROUND(CORR(n.vanna_level, fc.future_fwd_30m)::numeric,3) AS van_fut30,
    ROUND(CORR(n.vanna_delta_5m, fc.future_fwd_5m)::numeric,3) AS vanD_fut5, ROUND(CORR(n.vanna_delta_5m, fc.future_fwd_15m)::numeric,3) AS vanD_fut15,
    ROUND(CORR(n.charm_level, oc.opt_fwd_5m)::numeric,3) AS chr_opt5, ROUND(CORR(n.charm_level, oc.opt_fwd_15m)::numeric,3) AS chr_opt15, ROUND(CORR(n.charm_level, oc.opt_fwd_30m)::numeric,3) AS chr_opt30,
    ROUND(CORR(n.charm_level, fc.future_fwd_5m)::numeric,3) AS chr_fut5, ROUND(CORR(n.charm_level, fc.future_fwd_15m)::numeric,3) AS chr_fut15, ROUND(CORR(n.charm_level, fc.future_fwd_30m)::numeric,3) AS chr_fut30,
    ROUND(CORR(n.charm_delta_5m, fc.future_fwd_5m)::numeric,3) AS chrD_fut5, ROUND(CORR(n.charm_delta_5m, fc.future_fwd_15m)::numeric,3) AS chrD_fut15
FROM with_delta n
JOIN option_changes oc ON oc."AsOfDate" = n."AsOfDate" AND oc."Timestamp" = n."Timestamp" AND oc."OptionType" = 1
JOIN future_changes fc ON fc."AsOfDate" = n."AsOfDate" AND fc."Timestamp" = n."Timestamp"
GROUP BY 1
ORDER BY 1;
