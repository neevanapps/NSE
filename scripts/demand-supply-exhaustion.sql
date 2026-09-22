-- 2026-09-12: "Demand/supply exhaustion" idea (F58) -- does the option chain's own reaction to a
-- Nifty move (Strike7 band, +-3 strikes, avg call price / avg put price) come in proportional to
-- the move, and does a WEAK/anomalous reaction predict a reversal in the NEXT window? Distinct from
-- the already-closed residual-autocorrelation thread (that tested one leg's own mean-reversion;
-- this tests cross-leg proportionality as a forward-looking exhaustion signal).
--
-- Window 1 (T to T+15m): the "current move" -- measured via regression slope (regr_slope), not a
-- raw per-row ratio (change/change) -- averaging ratios row-by-row is statistically unstable near
-- a small denominator even with a floor; the slope of CallAvgChange1 on NiftyMove1 is the standard,
-- stable way to ask "how many rupees does the call move per point of underlying move."
-- Window 2 (T+15m to T+30m): the "does it reverse" target.
-- Reference underlying: future (real traded volume) primary; spot computed alongside for comparison.
WITH spot_grouped AS (
    SELECT "AsOfDate", "Timestamp",
        COUNT("SpotCloseFromLastCadence") OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp") AS spot_grp,
        COUNT("FutureCloseFromLastCadence") OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp") AS future_grp,
        "SpotCloseFromLastCadence", "FutureCloseFromLastCadence"
    FROM "CadenceContexts"
),
underlying_filled AS (
    SELECT "AsOfDate", "Timestamp",
        FIRST_VALUE("SpotCloseFromLastCadence") OVER (PARTITION BY "AsOfDate", spot_grp ORDER BY "Timestamp") AS spot_ffilled,
        FIRST_VALUE("FutureCloseFromLastCadence") OVER (PARTITION BY "AsOfDate", future_grp ORDER BY "Timestamp") AS future_ffilled
    FROM spot_grouped
),
underlying_lead AS (
    SELECT *,
        LEAD(spot_ffilled, 60) OVER w AS spot_15m,
        LEAD(future_ffilled, 60) OVER w AS future_15m,
        LEAD("Timestamp", 60) OVER w AS ts_15m,
        LEAD(spot_ffilled, 120) OVER w AS spot_30m,
        LEAD(future_ffilled, 120) OVER w AS future_30m,
        LEAD("Timestamp", 120) OVER w AS ts_30m
    FROM underlying_filled
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
),
underlying_moves AS (
    SELECT "AsOfDate", "Timestamp",
        CASE WHEN ts_15m - "Timestamp" BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN future_15m - future_ffilled END AS future_move1,
        CASE WHEN ts_15m - "Timestamp" BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN spot_15m - spot_ffilled END AS spot_move1,
        CASE WHEN ts_30m - ts_15m BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN future_30m - future_15m END AS future_move2,
        CASE WHEN ts_30m - ts_15m BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN spot_30m - spot_15m END AS spot_move2
    FROM underlying_lead
),
week_map AS (
    SELECT "AsOfDate", "ExpiryDate",
        ROW_NUMBER() OVER (PARTITION BY "AsOfDate" ORDER BY "ExpiryDate") AS week_rank
    FROM (SELECT DISTINCT "AsOfDate", "ExpiryDate" FROM "StrikeCadenceSnapshots") d
),
band_strikes AS (
    SELECT s."AsOfDate", s."Timestamp", s."OptionType", s."StrikePrice", s."MarkPrice",
        CASE WHEN wm.week_rank = 1 THEN 'ThisWeek' ELSE 'NextWeek' END AS week_label
    FROM "StrikeCadenceSnapshots" s
    JOIN week_map wm ON wm."AsOfDate" = s."AsOfDate" AND wm."ExpiryDate" = s."ExpiryDate"
    WHERE s."StrikeOffsetFromAtm" BETWEEN -3 AND 3
),
band_grouped AS (
    SELECT *, COUNT("MarkPrice") OVER (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice" ORDER BY "Timestamp") AS grp
    FROM band_strikes
),
band_filled AS (
    SELECT "AsOfDate", "Timestamp", week_label, "OptionType", "StrikePrice",
        FIRST_VALUE("MarkPrice") OVER (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice", grp ORDER BY "Timestamp") AS mark_ffilled
    FROM band_grouped
),
band_avg AS (
    SELECT "AsOfDate", "Timestamp", week_label, "OptionType", AVG(mark_ffilled) AS avg_price
    FROM band_filled
    GROUP BY 1,2,3,4
),
band_lead AS (
    SELECT *,
        LEAD(avg_price, 60) OVER w AS avg_price_15m,
        LEAD("Timestamp", 60) OVER w AS ts_15m
    FROM band_avg
    WINDOW w AS (PARTITION BY "AsOfDate", week_label, "OptionType" ORDER BY "Timestamp")
),
band_change1 AS (
    SELECT "AsOfDate", "Timestamp", week_label, "OptionType",
        CASE WHEN ts_15m - "Timestamp" BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN avg_price_15m - avg_price END AS avg_change1
    FROM band_lead
),
combined AS (
    SELECT u."AsOfDate", u."Timestamp", callc.week_label,
        u.future_move1, u.spot_move1, u.future_move2, u.spot_move2,
        callc.avg_change1 AS call_avg_change1,
        putc.avg_change1 AS put_avg_change1
    FROM underlying_moves u
    JOIN band_change1 callc ON callc."AsOfDate" = u."AsOfDate" AND callc."Timestamp" = u."Timestamp" AND callc."OptionType" = 1
    JOIN band_change1 putc ON putc."AsOfDate" = u."AsOfDate" AND putc."Timestamp" = u."Timestamp" AND putc."OptionType" = 2 AND putc.week_label = callc.week_label
)
SELECT "AsOfDate", week_label,
    COUNT(*) FILTER (WHERE call_avg_change1 IS NOT NULL AND future_move1 IS NOT NULL) AS n,
    ROUND(REGR_SLOPE(call_avg_change1, future_move1)::numeric,3) AS call_beta_per_future_point,
    ROUND(CORR(call_avg_change1, future_move1)::numeric,3) AS call_vs_future_corr,
    ROUND(REGR_SLOPE(put_avg_change1, future_move1)::numeric,3) AS put_beta_per_future_point,
    ROUND(CORR(put_avg_change1, future_move1)::numeric,3) AS put_vs_future_corr
FROM combined
GROUP BY 1,2
ORDER BY 1,2;
