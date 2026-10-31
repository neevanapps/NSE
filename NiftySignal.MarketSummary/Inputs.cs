namespace NiftySignal.MarketSummary;

/// <summary>
/// One exact option contract over one completed bar (a plain mapping of an <c>adaptive_option_strike_bars</c> row; the mapper has already chosen v3 &gt; v2 &gt; v1 for this bar, so fields a version does not
/// carry are null). Delta / Vega are raw (unscaled) Black-Scholes values.
/// </summary>
public sealed record ContractBar(
    int BarSeq, string Token, bool IsCall,
    double? PriceStart, double? PriceEnd,
    long TotalQuantity, long StrictDelta, long EnrichedBuy, long EnrichedSell, long EnrichedUnknown, long EnrichedDelta,
    long? OiStart, long? OiEnd,
    double? IvStart, double? IvEnd,
    double? DeltaStart, double? DeltaEnd, double? VegaStart, double? VegaEnd,
    double? TobTimeWeighted, double? MicroDevTimeWeighted, double? TobUsableSeconds,
    long? NetOfi, long? GrossOfi);

/// <summary>Completed futures bar (core row + futures supplemental row; sidecar fields are null when no usable sidecar row exists).</summary>
public sealed record FuturesBar(
    int BarSeq, double DurationSeconds, double Open, double Close, long Volume, long StrictDelta,
    long EnrichedBuy, long EnrichedSell, long EnrichedUnknown, long EnrichedDelta, long? OiOpen, long? OiClose,
    double? TobTimeWeighted, double? MicroDevTimeWeighted, double? TobUsableSeconds, double? MicroDevUsableSeconds,
    long? Ofi, long? GrossOfi);

/// <summary>All stored per-bar rows of the basket's tokens (history included - Roll ΔPx needs bars before a window), plus each completed bar's duration. Completed bars only.</summary>
public sealed class ContractHistory
{
    readonly Dictionary<string, Dictionary<int, ContractBar>> _byToken = new(StringComparer.Ordinal);

    public ContractHistory(IEnumerable<ContractBar> bars, IReadOnlyDictionary<int, double> barDurationSeconds)
    {
        foreach (var b in bars)
        {
            if (!_byToken.TryGetValue(b.Token, out var m)) _byToken[b.Token] = m = new();
            m[b.BarSeq] = b;
        }

        BarDurationSeconds = barDurationSeconds;
    }

    public IReadOnlyDictionary<int, double> BarDurationSeconds { get; }

    public ContractBar? Get(string token, int barSeq) => _byToken.TryGetValue(token, out var m) && m.TryGetValue(barSeq, out var b) ? b : null;
}
