-- Pick one representative, well-understood cadence (11 Sep 2026, the documented 13:50-14:00 spike)
-- and dump the full score_snapshots row plus the surrounding strike_snapshots (call+put) rows,
-- for use as a worked example in the metrics documentation artifact.

\x on

SELECT *
FROM score_snapshots
WHERE "ComputedAt" >= '2026-09-11 14:00:45+05:30'
  AND "ComputedAt" <  '2026-09-11 14:01:15+05:30'
ORDER BY "ComputedAt"
LIMIT 1;

\x off

SELECT "ComputedAt", "Token", "StrikePrice", "OptionType", "VolumeDelta", "OpenInterest", "OpenInterestDelta",
       "MarkPrice", "MarkPriceDelta", "OiBuildup", "BidPrice", "AskPrice", "SpreadAbs", "SpreadPctOfMid",
       "ImpliedVolatility", "Delta", "Gamma", "ThetaPerDay", "Vega", "Rho", "TheoreticalPrice", "PriceVsTheoretical"
FROM strike_snapshots
WHERE "ComputedAt" >= '2026-09-11 14:00:45+05:30'
  AND "ComputedAt" <  '2026-09-11 14:01:15+05:30'
ORDER BY "StrikePrice", "OptionType";
