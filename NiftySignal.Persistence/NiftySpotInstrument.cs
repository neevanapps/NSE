using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Persistence;

/// <summary>The outcome of resolving the NIFTY spot (index) instrument of a trade date from its instrument master.</summary>
/// <param name="Token">The unique instrument's token, or null when the master has zero or several NIFTY Index instruments.</param>
/// <param name="Symbol">The unique instrument's trading symbol (null exactly when <paramref name="Token"/> is).</param>
/// <param name="CandidateCount">How many distinct NIFTY Index instruments the master holds for the day.</param>
public sealed record NiftySpotResolution(string? Token, string? Symbol, int CandidateCount)
{
    public bool IsUnique => Token is not null;
}

/// <summary>
/// The ONE rule that identifies the NIFTY spot instrument: the unique NIFTY Index instrument of the trade date's instrument master. The adaptive observer's supplemental freeze, the opening-volume
/// session creation and the NPT1 M1 session builder all use this, so the spot a session centers its band on can never differ between paths.
/// </summary>
public static class NiftySpotInstrument
{
    public static async Task<NiftySpotResolution> ResolveAsync(NiftySignalDbContext source, DateOnly tradeDate, CancellationToken ct)
    {
        var candidates = await source.Instruments.AsNoTracking()
            .Where(i => i.AsOfDate == tradeDate && i.Underlying == "NIFTY" && i.InstrumentType == InstrumentType.Index)
            .Select(i => new { i.Token, i.TradingSymbol })
            .Distinct()
            .ToListAsync(ct);
        return candidates.Count == 1 ? new NiftySpotResolution(candidates[0].Token, candidates[0].TradingSymbol, 1) : new NiftySpotResolution(null, null, candidates.Count);
    }
}
