namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// How the ATM center strike of the option band is chosen. It is a versioned contract stamped on the session row (<c>AtmRuleVersion</c>), separate from the session model identity
/// (<c>adaptive-v1</c> / <c>adaptive-fixed13650-v1</c>) so opening-volume and Strong-threshold history keyed on the model identity is not reset.
/// </summary>
public enum AtmCenterRule
{
    /// <summary>Sessions created before the spot rule (null/absent stamp): the strike nearest the put-call-parity synthetic forward. Replayed exactly as it was originally computed.</summary>
    SyntheticForwardLegacy = 0,

    /// <summary>spot-atm-v1: the strike nearest the fresh NIFTY spot (ties choose the lower strike). Without a fresh spot there is NO band and no fallback to any other price.</summary>
    SpotV1 = 1,
}

public static class AtmCenterRules
{
    /// <summary>The stamp of sessions created under <see cref="AtmCenterRule.SpotV1"/>.</summary>
    public const string SpotV1Version = "spot-atm-v1";

    /// <summary>The deterministic reason of an unavailable band when the spot rule has no fresh spot.</summary>
    public const string SpotUnavailableReason = "spot-atm-v1 requires a NIFTY spot tick at most 5 s old; none is available, so the weekly ATM±2 band is unavailable (no fallback to futures or the synthetic forward).";

    /// <summary>A null/empty stamp is the legacy rule, the exact known stamp is the spot rule; any other stamp is refused (a session of a newer contract must never be replayed under another rule).</summary>
    public static AtmCenterRule FromVersion(string? version) =>
        string.IsNullOrEmpty(version) ? AtmCenterRule.SyntheticForwardLegacy
        : string.Equals(version, SpotV1Version, StringComparison.Ordinal) ? AtmCenterRule.SpotV1
        : throw new NotSupportedException($"Unsupported ATM rule version '{version}'.");

    /// <summary>Display label of a session's stamp (captions / provenance): a legacy session is visibly different from a spot-rule one. Never throws, an unknown stamp is shown as stored.</summary>
    public static string Describe(string? version) =>
        string.IsNullOrEmpty(version) ? "synthetic-forward (legacy)" : version;

    public static string? ToVersion(AtmCenterRule rule) => rule == AtmCenterRule.SpotV1 ? SpotV1Version : null;

    /// <summary>
    /// The spot in force at <paramref name="at"/> among <paramref name="spots"/> (in arrival order, i.e. by availability then id): the latest observation with <c>At &lt;= at</c>, and only when it is fresh by the
    /// ONE existing rule, <c>0 &lt;= at - At &lt;= <see cref="FuturesBasisBar.SpotFreshnessSeconds"/></c> (inclusive). Non-positive and non-finite prices are not observations. Used where a session is frozen from
    /// stored ticks (opening-volume creation, NPT1 freeze); it must agree with <see cref="FuturesBasisTracker.FreshSpotAt"/>, which serves the live engine (asserted by a test).
    /// </summary>
    public static double? FreshSpot(IEnumerable<(DateTimeOffset At, double Price)> spots, DateTimeOffset at)
    {
        (DateTimeOffset At, double Price)? latest = null;
        foreach (var spot in spots)
        {
            if (spot.At > at || !(spot.Price > 0d) || !double.IsFinite(spot.Price))
            {
                continue;
            }

            if (latest is null || spot.At >= latest.Value.At)
            {
                latest = spot;
            }
        }

        return latest is { } l && at - l.At <= TimeSpan.FromSeconds(FuturesBasisBar.SpotFreshnessSeconds) ? l.Price : null;
    }

    /// <summary>Nearest strike to <paramref name="reference"/>; an exact midpoint chooses the lower strike. Strikes must be non-empty.</summary>
    public static double NearestStrike(IReadOnlyList<double> strikes, double reference) =>
        strikes.OrderBy(k => Math.Abs(k - reference)).ThenBy(k => k).First();
}
