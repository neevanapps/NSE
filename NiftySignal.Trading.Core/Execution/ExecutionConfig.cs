namespace NiftySignal.Trading.Core.Execution;

public enum ExecutionAdapter
{
    /// <summary>Deterministic simulated execution (replay and live paper use the same model).</summary>
    Simulated = 0,

    /// <summary>Reserved for a future broker adapter. NOT AVAILABLE: configuration validation refuses it and no code path places a real order.</summary>
    Broker = 1,
}

/// <summary>How a fill whose execution-time ask is below <c>MinimumEntryPremium</c> is treated. A buy limit order cannot enforce a minimum fill price; this setting only chooses what the simulator does about it.</summary>
public enum BelowBandMode
{
    /// <summary>Default research policy: the execution ask must be inside [MinimumEntryPremium, MaximumEntryPremium] (inclusive) and at or below the order limit, otherwise there is no fill (the order keeps working until it expires).</summary>
    ResearchEnforceBand = 0,

    /// <summary>Broker-realistic: a fill below MinimumEntryPremium happens at the actual executable ask (never clamped), is flagged <c>BelowBand</c> and is reported separately. The ask must still be at or below the order limit.</summary>
    BrokerRealisticBelowBand = 1,
}

/// <summary>
/// Whether the quote that generated the signal (or any quote not newer than the decision) may be used to fill the order. A positive latency
/// only guarantees that execution happens LATER; it does not by itself force a NEWER quote. With the default a still-fresh signal quote can fill.
/// </summary>
public enum SignalQuoteReusePolicy
{
    /// <summary>Default (the actual CP3 behaviour): the execution quote is the latest delivered quote at or before the execution time. It may be the very quote that triggered the intent, provided it is still fresh (age at the execution time &lt;= MaxQuoteAge) and its displayed liquidity has not been consumed by this book.</summary>
    AllowFreshSignalQuote = 0,

    /// <summary>Conservative sensitivity: the execution quote must have AvailableAt strictly AFTER the decision time; until one arrives the order keeps working (note AwaitingNewerQuote).</summary>
    RequireQuoteAfterDecision = 1,
}

/// <summary>Initial partial-entry policy: keep what filled (whole lots) and cancel the remaining entry quantity; a partial fill consumes the opportunity.</summary>
public enum PartialEntryPolicy
{
    KeepFilledCancelRemainder = 0,
}

/// <summary>Independent research variants each get their own virtual execution book. Their fills are NOT jointly feasible in one account.</summary>
public enum BookMode
{
    IndependentVirtualBooks = 0,
}

/// <summary>
/// Simulated execution assumptions (versioned, hashed). Every numeric value here is a PLACEHOLDER assumption pending Phase 3
/// calibration: recorded quotes show feed timing and book state, not broker round-trip latency, queue position or rejections.
/// Level-1 simulation limits: only the best bid/ask and their displayed sizes are known; depth beyond level 1, queue priority,
/// hidden liquidity, other participants' orders and market impact are not modelled.
/// </summary>
public sealed record ExecutionConfig
{
    public ExecutionAdapter Adapter { get; init; } = ExecutionAdapter.Simulated;

    /// <summary>Time from decision to the simulated exchange; must be positive so that execution happens strictly after the decision. It does NOT by itself exclude the signal quote (see <see cref="SignalQuoteReusePolicy"/>). PLACEHOLDER.</summary>
    public TimeSpan Latency { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A cancellation requested at t takes effect at t + CancelLatency (except the 15:00 entry-close cancel, which is immediate). PLACEHOLDER.</summary>
    public TimeSpan CancelLatency { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>An entry order is live for this long from the decision time; it can fill in [decision + Latency, decision + EntryOrderLifetime). PLACEHOLDER.</summary>
    public TimeSpan EntryOrderLifetime { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>A quote older than this at the execution time cannot be filled against. Measured against the execution timestamp, never against a later watermark. PLACEHOLDER.</summary>
    public TimeSpan MaxQuoteAge { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Entries only: no fill when ask - bid exceeds this (rupees); null = not enforced. PLACEHOLDER.</summary>
    public decimal? MaxSpreadAbsolute { get; init; } = 2.00m;

    /// <summary>Entries only: no fill when (ask - bid) / mid exceeds this fraction; null = not enforced. PLACEHOLDER.</summary>
    public decimal? MaxSpreadFraction { get; init; } = 0.03m;

    /// <summary>When true a missing or zero displayed quantity means no fill (liquidity unknown is never assumed unlimited).</summary>
    public bool RequireDisplayedQuantity { get; init; } = true;

    /// <summary>Share of the unconsumed displayed quantity a fill may take (0 &lt; f &lt;= 1). PLACEHOLDER.</summary>
    public decimal FillFractionOfDisplayed { get; init; } = 1.0m;

    /// <summary>Delay between exit attempts when an exit order could not (fully) fill. Used by every exit reason including session liquidation. PLACEHOLDER.</summary>
    public TimeSpan ExitRetryInterval { get; init; } = TimeSpan.FromSeconds(1);

    public BelowBandMode BelowBand { get; init; } = BelowBandMode.ResearchEnforceBand;

    public SignalQuoteReusePolicy SignalQuoteReuse { get; init; } = SignalQuoteReusePolicy.AllowFreshSignalQuote;

    public PartialEntryPolicy PartialEntry { get; init; } = PartialEntryPolicy.KeepFilledCancelRemainder;

    public BookMode Books { get; init; } = BookMode.IndependentVirtualBooks;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Adapter != ExecutionAdapter.Simulated)
        {
            errors.Add("Execution.Adapter must be Simulated: real-order / broker execution is not available.");
        }

        if (Latency <= TimeSpan.Zero)
        {
            errors.Add("Execution.Latency must be positive.");
        }

        if (CancelLatency < TimeSpan.Zero)
        {
            errors.Add("Execution.CancelLatency must not be negative.");
        }

        if (EntryOrderLifetime <= Latency)
        {
            errors.Add("Execution.EntryOrderLifetime must exceed Latency.");
        }

        if (MaxQuoteAge <= TimeSpan.Zero)
        {
            errors.Add("Execution.MaxQuoteAge must be positive.");
        }

        if (MaxSpreadAbsolute is <= 0m)
        {
            errors.Add("Execution.MaxSpreadAbsolute must be positive when set.");
        }

        if (MaxSpreadFraction is <= 0m)
        {
            errors.Add("Execution.MaxSpreadFraction must be positive when set.");
        }

        if (FillFractionOfDisplayed is <= 0m or > 1m)
        {
            errors.Add("Execution.FillFractionOfDisplayed must be in (0, 1].");
        }

        if (ExitRetryInterval <= TimeSpan.Zero)
        {
            errors.Add("Execution.ExitRetryInterval must be positive.");
        }

        if (!Enum.IsDefined(BelowBand) || !Enum.IsDefined(PartialEntry) || !Enum.IsDefined(Books) || !Enum.IsDefined(SignalQuoteReuse))
        {
            errors.Add("Execution mode enum value is not defined.");
        }

        return errors;
    }
}
