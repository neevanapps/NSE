namespace NiftySignal.Host;

/// <summary>
/// Which optional raw-tick collections the ingestion worker runs. <see cref="CollectSensexBankNifty"/> is the pause switch for the
/// write-only SENSEX / BANKNIFTY archive collection: false (the shipped default) = paused, true = resumed. It gates BOTH the fresh
/// resolution of those universes AND the subscription of already-resolved rows reused from the database on a restart. Instruments and
/// ticks already stored are never touched; NIFTY is never affected.
/// </summary>
public sealed class MarketDataCollectionOptions
{
    public const string SectionName = "MarketData";

    public bool CollectSensexBankNifty { get; set; }
}
