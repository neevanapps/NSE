namespace NiftySignal.Trading.Core.Risk;

/// <summary>
/// Typed reasons why an Enter intent that reached the engine did not become an entry. The numeric order is the stable report order: when several
/// reasons hold at once they are listed in ascending value, so the same inputs always produce the same text.
/// </summary>
public enum RejectionReason
{
    BeforeEntryWindow = 1,
    EntryWindowClosed = 2,

    KillSwitchGlobal = 10,
    KillSwitchFamily = 11,
    KillSwitchVariant = 12,

    IntegrityDegraded = 20,
    IntegrityRecovering = 21,

    VariantCapacity = 30,

    GlobalTradeLimit = 40,
    FamilyTradeLimit = 41,
    VariantTradeLimit = 42,

    GlobalDailyLoss = 50,
    FamilyDailyLoss = 51,
    VariantDailyLoss = 52,

    FamilyConsecutiveLoss = 60,
    VariantConsecutiveLoss = 61,

    GlobalCapital = 70,

    NoEligibleContract = 80,
    NoExecutableAsk = 81,
    PremiumOutsideBand = 82,
}

public static class RejectionReasons
{
    /// <summary>Canonical text of a reason set: distinct names in ascending value order, comma separated.</summary>
    public static string Canonical(IEnumerable<RejectionReason> reasons)
        => string.Join(",", reasons.Distinct().OrderBy(r => (int)r).Select(r => r.ToString()));

    /// <summary>The note outcome name used by the long-standing evaluation notes (kept so existing audit consumers see unchanged names for kill and integrity blocks).</summary>
    public static string NoteName(RejectionReason reason)
        => reason switch
        {
            RejectionReason.KillSwitchGlobal or RejectionReason.KillSwitchFamily or RejectionReason.KillSwitchVariant => "KillSwitch",
            _ => reason.ToString(),
        };
}
