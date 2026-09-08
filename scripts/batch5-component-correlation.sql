-- Batch 5 prep (audit finding F9, 2026-09-08): pairwise Pearson correlation across the 14
-- weighted score components, computed from live score_snapshots data on the Batch 3/4-corrected
-- baseline (F2/F4/F5/F7/F8 raw formulas, F1's fixed k). This is the input the plan's Batch 5
-- section calls for -- deciding which components to residualize or reweight by inverse
-- correlation, once several live sessions of clean data exist to compute it from.
--
-- Do NOT run this against data from before the Batch 4 deploy (2026-09-08 evening) -- rows
-- before that reflect the old dynamic-k / pre-F2-F8 formulas, and a correlation computed across
-- a formula change would measure the change, not genuine redundancy between components. Update
-- `cutoff` below to the first live session date after that deploy (2026-09-09 is the earliest).
-- The plan calls for "several sessions" before trusting this, not just one -- rerun with a wider
-- window as more sessions accumulate.
--
-- Reading the output: component pairs near the top (|pearson_r| close to +/-1) are the ones
-- reading substantially the same underlying signal -- e.g. the audit's own suspects were
-- OiBuildupNet/Pcr/IvSkew (all option-positioning reads) and FuturesBasis/PriceMomentum (both
-- driven off the futures price). A high-|r| pair is a candidate to residualize (keep one, orthogonalize
-- the other against it) or fold into inverse-correlation-derived weights -- not an instruction to
-- drop one blindly.

WITH since AS (
    SELECT DATE '2026-09-09' AS cutoff
),
long AS (
    SELECT s."ComputedAt", v.component, v.z
    FROM score_snapshots s
    CROSS JOIN LATERAL (VALUES
        ('OiBuildupNet',     s."OiBuildupNetZ"),
        ('Pcr',              s."PcrZ"),
        ('FuturesBasis',     s."FuturesBasisZ"),
        ('IvSkew',           s."IvSkewZ"),
        ('PriceMomentum',    s."PriceMomentumZ"),
        ('DepthImbalance',   s."DepthImbalanceZ"),
        ('VixChange',        s."VixChangeZ"),
        ('GammaExposure',    s."GammaExposureZ"),
        ('VolumePcr',        s."VolumePcrZ"),
        ('SpreadRatio',      s."SpreadRatioZ"),
        ('VannaExposure',    s."VannaExposureZ"),
        ('CharmExposure',    s."CharmExposureZ"),
        ('CvdProxy',         s."CvdProxyZ"),
        ('StraddleRichness', s."StraddleRichnessZ")
    ) AS v(component, z)
    WHERE s."ComputedAt" >= (SELECT cutoff FROM since)
      AND v.z IS NOT NULL
)
SELECT
    a.component AS component_a,
    b.component AS component_b,
    corr(a.z, b.z) AS pearson_r,
    count(*) AS overlapping_cadences
FROM long a
JOIN long b
    ON a."ComputedAt" = b."ComputedAt"
   AND a.component < b.component   -- upper triangle only: no self-pairs, no mirrored duplicates
GROUP BY a.component, b.component
HAVING count(*) >= 100             -- drop pairs with too few overlapping readings to trust
ORDER BY abs(corr(a.z, b.z)) DESC;

-- Companion sanity check: per-component non-null coverage over the same window, so a component
-- that's mostly null (e.g. still warming up, or a feed gap) doesn't quietly get a misleadingly
-- confident correlation from a handful of overlapping points.
--
-- WITH since AS (SELECT DATE '2026-09-09' AS cutoff)
-- SELECT v.component, count(*) AS non_null_cadences
-- FROM score_snapshots s
-- CROSS JOIN LATERAL (VALUES
--     ('OiBuildupNet', s."OiBuildupNetZ"), ('Pcr', s."PcrZ"), ('FuturesBasis', s."FuturesBasisZ"),
--     ('IvSkew', s."IvSkewZ"), ('PriceMomentum', s."PriceMomentumZ"), ('DepthImbalance', s."DepthImbalanceZ"),
--     ('VixChange', s."VixChangeZ"), ('GammaExposure', s."GammaExposureZ"), ('VolumePcr', s."VolumePcrZ"),
--     ('SpreadRatio', s."SpreadRatioZ"), ('VannaExposure', s."VannaExposureZ"), ('CharmExposure', s."CharmExposureZ"),
--     ('CvdProxy', s."CvdProxyZ"), ('StraddleRichness', s."StraddleRichnessZ")
-- ) AS v(component, z)
-- WHERE s."ComputedAt" >= (SELECT cutoff FROM since) AND v.z IS NOT NULL
-- GROUP BY v.component
-- ORDER BY non_null_cadences ASC;
