using NiftySignal.Domain.Enums;

namespace NiftySignal.Execution;

/// <summary>
/// One option instrument's current quote snapshot, as far as strike selection needs to
/// know. Deliberately not tied to a live broker call -- <see cref="StrikeSelector"/> ranks
/// whatever list it's handed, so it's testable with synthetic candidates and the live
/// quote-fetching (Ingestion's job) can be wired in later without touching this type.
/// </summary>
public sealed record StrikeCandidate(
    string Token,
    string TradingSymbol,
    OptionType OptionType,
    decimal Mid,
    decimal? BidPrice,
    decimal? AskPrice,
    long OpenInterest,
    long Volume,
    double? Delta,
    double? ImpliedVolatility);
