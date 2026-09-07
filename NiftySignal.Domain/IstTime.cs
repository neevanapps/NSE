namespace NiftySignal.Domain;

/// <summary>
/// The one IST constant/conversion shared across projects (2026-09-07). Before this, five-plus
/// files each redefined their own <c>IstOffset = TimeSpan.FromHours(5.5)</c> local constant --
/// harmless on its own, but it's exactly why <see cref="NiftySignal.Rules.ExitRuleEvaluator"/>'s
/// square-off check was missed when <see cref="NiftySignal.Rules.EntryRuleEvaluator"/>'s
/// equivalent bug was fixed earlier the same day: there was no single place a "did every wall-
/// clock comparison convert first?" audit could anchor on. New wall-clock-from-UTC conversions
/// should use <see cref="ToIst"/> rather than adding another local offset constant.
/// </summary>
public static class IstTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(5.5);

    /// <summary>Converts a UTC-backed <see cref="DateTimeOffset"/> to its IST wall-clock reading -- never call <c>.DateTime</c> on a UTC value directly when IST is what's intended.</summary>
    public static DateTimeOffset ToIst(this DateTimeOffset utc) => utc.ToOffset(Offset);
}
