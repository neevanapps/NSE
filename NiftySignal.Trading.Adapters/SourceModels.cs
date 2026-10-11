using NiftySignal.AdaptiveObserver;
using NiftySignal.Domain.Enums;
using NiftySignal.Trading.Core.Instruments;

namespace NiftySignal.Trading.Adapters;

/// <summary>
/// One recorded source row, exactly as the authoritative tick table holds it (contract section A.16.1). <see cref="Id"/> is the source order key;
/// <see cref="ExchangeTimestamp"/> has one-second resolution; <see cref="ReceivedAt"/> is the RECORDED arrival time (never the wall clock of this
/// process). Prices are decimals as stored; the adaptive calculators receive the same <c>(double)</c> conversion the Host applies.
/// </summary>
public sealed record SourceTick(
    string Token,
    long Id,
    DateTimeOffset ExchangeTimestamp,
    DateTimeOffset ReceivedAt,
    decimal Last,
    decimal Bid,
    decimal Ask,
    long BidQty,
    long AskQty,
    long Volume,
    long? OpenInterest)
{
    public ObserverRawTick ToRaw()
        => new(Id, ExchangeTimestamp, ReceivedAt, (double)Last, (double)Bid, (double)Ask, BidQty, AskQty, Volume, OpenInterest);

    /// <summary>Everything that identifies the row's content (used to tell an exact re-delivery from a conflicting one).</summary>
    public string Fingerprint()
        => FormattableString.Invariant($"{Token}|{ExchangeTimestamp.UtcTicks}|{ReceivedAt.UtcTicks}|{Last}|{Bid}|{Ask}|{BidQty}|{AskQty}|{Volume}|{OpenInterest}");
}

/// <summary>
/// The immutable session state both adapters start from (contract section A.16.3): the frozen adaptive session (base bar volume, strong threshold,
/// 09:30 residual anchor, signal start, weekly option universe) plus the effective-date contract metadata. In production it is read from the
/// observer's session row; nothing here is recomputed at run time, so historical replay and live paper cannot disagree about it.
/// </summary>
/// <param name="Definition">Frozen adaptive session definition (future token, lot size, base bar volume, rolling window).</param>
/// <param name="WeeklyOptionExpiry">Expiry of the option universe.</param>
/// <param name="RiskFreeRate">Frozen risk-free rate used by the adaptive pricing.</param>
/// <param name="StrongThreshold">Frozen strong-ratio threshold (null when no prior states existed).</param>
/// <param name="SignalStartUtc">First instant at which a bar counts as tradable evidence (09:30, or the frozen observation start of a late-start session).</param>
/// <param name="ObservationStartUtc">First availability instant the translator accepts (market open 09:15, or the frozen observation start).</param>
/// <param name="Options">Frozen option universe (token, strike, expiry, lot size).</param>
/// <param name="ResidualAnchor">Frozen 09:30 residual anchor, or null when it could not be built.</param>
/// <param name="TickSizes">Effective-date tick size per option token (from the instrument master of the trade date).</param>
/// <param name="ModelVersion">Opening-volume model version of the frozen session (provenance of the readiness state).</param>
public sealed record SessionFreeze(
    AdaptiveSessionDefinition Definition,
    DateOnly WeeklyOptionExpiry,
    double RiskFreeRate,
    double? StrongThreshold,
    DateTimeOffset SignalStartUtc,
    DateTimeOffset ObservationStartUtc,
    IReadOnlyList<ObserverOptionInstrument> Options,
    OptionResidualAnchor? ResidualAnchor,
    IReadOnlyDictionary<string, decimal> TickSizes,
    string ModelVersion)
{
    public DateOnly TradeDate => Definition.TradeDate;

    public string FutureToken => Definition.FutureToken;

    /// <summary>The contract universe of the trading core, derived from the frozen universe and the effective-date tick sizes (an option without a tick size is an error, never defaulted).</summary>
    public IReadOnlyList<OptionContract> ToContracts()
        => Options
            .OrderBy(o => o.Token, StringComparer.Ordinal)
            .Select(o => new OptionContract(
                o.Token,
                o.OptionType == OptionType.Call ? OptionSide.Call : OptionSide.Put,
                (decimal)o.Strike,
                o.ExpiryDate,
                o.LotSize,
                TickSizes.TryGetValue(o.Token, out var tick) && tick > 0m
                    ? tick
                    : throw new InvalidOperationException($"No effective-date tick size for option '{o.Token}'."),
                o.TradingSymbol))
            .ToList();
}

/// <summary>How the translator derives availability when the exchange timestamp is later than the received time. Only one policy exists today (the existing adaptive rule).</summary>
public enum AvailabilityFallbackPolicy
{
    /// <summary><c>AvailableAt = max(ReceivedAt, ExchangeTimestamp)</c>: if a row claims to have been received before the exchange stamped it, the exchange stamp wins and the row is flagged <see cref="AvailabilityBasis.ExchangeTimestampClamp"/>. Identical to <c>AdaptiveIncrementalTickNormalizer</c>.</summary>
    MaxOfReceivedAndExchange = 0,
}

/// <summary>Which of the two recorded timestamps defined a quote's availability.</summary>
public enum AvailabilityBasis
{
    ReceivedAt = 0,
    ExchangeTimestampClamp = 1,
}

/// <summary>Translator settings. Every value is explicit; nothing is read from the environment.</summary>
public sealed record TranslatorOptions
{
    /// <summary>
    /// Ordering lag: a group at availability T is released only when the source has proven progress past <c>T + StableOrderingLag</c>
    /// (strictly). The production adaptive worker uses 2 s (<c>AdaptiveObserverWorker.StableOrderingLag</c>); that existing constant is the default
    /// here, NOT a new tuning. Larger is safer, smaller risks late rows (which are quarantined, never merged).
    /// </summary>
    public TimeSpan StableOrderingLag { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Rows whose availability is before this instant are counted as outside the window (their normaliser state is still updated, as in the existing reader). Default: the session's observation start.</summary>
    public DateTimeOffset? AcceptFromUtc { get; init; }

    /// <summary>Rows whose availability is after this instant are counted as outside the window. Default: no upper bound.</summary>
    public DateTimeOffset? AcceptThroughUtc { get; init; }

    /// <summary>How long source row ids are remembered for duplicate / conflict recognition (measured behind the highest received time seen). Older ids that reappear are quarantined as <c>IdOutOfOrder</c>.</summary>
    public TimeSpan SeenIdRetention { get; init; } = TimeSpan.FromMinutes(10);

    public AvailabilityFallbackPolicy AvailabilityFallback { get; init; } = AvailabilityFallbackPolicy.MaxOfReceivedAndExchange;

    /// <summary>Version label of the readiness definition recorded on every bar (adaptive readiness policy + frozen session model).</summary>
    public string ReadinessSpec(SessionFreeze freeze)
        => FormattableString.Invariant($"adaptive-readiness-v1;consecutiveValidBars={AdaptiveReadinessPolicy.RequiredBars};signalStart={freeze.SignalStartUtc:O};model={freeze.ModelVersion}");

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (StableOrderingLag < TimeSpan.Zero)
        {
            errors.Add("StableOrderingLag must not be negative.");
        }

        if (SeenIdRetention <= TimeSpan.Zero)
        {
            errors.Add("SeenIdRetention must be positive.");
        }

        if (!Enum.IsDefined(AvailabilityFallback))
        {
            errors.Add("AvailabilityFallback is not defined.");
        }

        return errors;
    }
}
