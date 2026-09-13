-- Corrected F58 retest: strike-identity-safe ATM anchor (partitioned by StrikePrice, matching the
-- CVD-redo template) for call/put reaction, future as underlying reference (spot performed
-- near-identically in the sanity check, future chosen since it's the tradeable instrument).
-- Residual = actual reaction minus the user's own naive "half the move" expectation (beta=0.5),
-- correlated against the NEXT 15-min window's move to test the actual reversal/exhaustion claim.
WITH spot_grouped AS (
    SELECT "AsOfDate", "Timestamp",
        COUNT("FutureCloseFromLastCadence") OVER (PARTITION BY "AsOfDate" ORDER BY "Timestamp") AS future_grp,
        "FutureCloseFromLastCadence"
    FROM "CadenceContexts"
),
underlying_filled AS (
    SELECT "AsOfDate", "Timestamp",
        FIRST_VALUE("FutureCloseFromLastCadence") OVER (PARTITION BY "AsOfDate", future_grp ORDER BY "Timestamp") AS future_ffilled
    FROM spot_grouped
),
underlying_lead AS (
    SELECT *,
        LEAD(future_ffilled, 60) OVER w AS future_15m, LEAD("Timestamp", 60) OVER w AS ts_15m,
        LEAD(future_ffilled, 120) OVER w AS future_30m, LEAD("Timestamp", 120) OVER w AS ts_30m
    FROM underlying_filled
    WINDOW w AS (PARTITION BY "AsOfDate" ORDER BY "Timestamp")
),
underlying_moves AS (
    SELECT "AsOfDate", "Timestamp",
        CASE WHEN ts_15m - "Timestamp" BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN future_15m - future_ffilled END AS future_move1,
        CASE WHEN ts_30m - ts_15m BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN future_30m - future_15m END AS future_move2
    FROM underlying_lead
),
week_map AS (
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
    SELECT *, LEAD(mark_ffilled, 60) OVER w AS price_15m_later, LEAD("Timestamp", 60) OVER w AS ts_15m_later
    FROM filled
    WINDOW w AS (PARTITION BY "AsOfDate", week_label, "OptionType", "StrikePrice" ORDER BY "Timestamp")
),
anchor_changes AS (
    SELECT "AsOfDate", "Timestamp", week_label, "OptionType",
        CASE WHEN ts_15m_later - "Timestamp" BETWEEN INTERVAL '14 minutes 30 seconds' AND INTERVAL '15 minutes 30 seconds'
             THEN price_15m_later - mark_ffilled END AS change1
    FROM with_lookahead
    WHERE "StrikeOffsetFromAtm" = 0
),
combined AS (
    SELECT u."AsOfDate", u."Timestamp", callc.week_label, u.future_move1, u.future_move2,
        callc.change1 AS call_change1, putc.change1 AS put_change1,
        callc.change1 - 0.5 * u.future_move1 AS call_residual,
        putc.change1 + 0.5 * u.future_move1 AS put_residual
    FROM underlying_moves u
    JOIN anchor_changes callc ON callc."AsOfDate" = u."AsOfDate" AND callc."Timestamp" = u."Timestamp" AND callc."OptionType" = 1
    JOIN anchor_changes putc ON putc."AsOfDate" = u."AsOfDate" AND putc."Timestamp" = u."Timestamp" AND putc."OptionType" = 2 AND putc.week_label = callc.week_label
    WHERE u.future_move1 IS NOT NULL AND ABS(u.future_move1) >= 15
)
SELECT "AsOfDate", week_label,
    COUNT(*) FILTER (WHERE future_move2 IS NOT NULL) AS n,
    ROUND(CORR(call_change1, future_move1)::numeric,3) AS call_vs_move1_corr,
    ROUND(CORR(put_change1, future_move1)::numeric,3) AS put_vs_move1_corr,
    ROUND(CORR(call_residual, future_move2)::numeric,3) AS call_residual_vs_next_move,
    ROUND(CORR(put_residual, future_move2)::numeric,3) AS put_residual_vs_next_move
FROM combined
GROUP BY 1,2
ORDER BY 1,2;
