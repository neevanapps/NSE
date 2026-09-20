using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Phase G performance fix (docs/LIVE_PARITY_PLAN.md): a per-process, cross-poll cache of each
/// touched option token's <see cref="OptionQuoteSeries"/>/<see cref="OptionOiSeries"/> for "today,"
/// so <see cref="LiveOptionAtmPopulator"/>/<see cref="LiveOptionMaxPainPopulator"/> stop re-scanning
/// each token's FULL day-so-far tick history from scratch on every poll -- concretely measured
/// (docs/LIVE_PARITY_PLAN.md's Phase G section, <c>perf-check</c> CLI command) to cost multiple
/// seconds per token by late in a trading day, enough combined across the touched tokens to threaten
/// the whole 10s poll budget.
///
/// Registered as a Singleton and owned by <see cref="NiftySignal.Host.LiveVolumeBarWriter"/> alone
/// (its own <c>BackgroundService.ExecuteAsync</c> never runs two iterations concurrently -- same "no
/// lock needed, only one caller ever touches this" reasoning <see cref="TradingDaySession"/> already
/// documents for its own cross-poll state). NOT thread-safe by construction -- do not share one
/// instance across two different pollers.
///
/// Pure performance optimization, zero correctness risk: every value returned is byte-identical to
/// what a from-scratch <see cref="OptionQuoteSeries.LoadAsync(NiftySignalDbContext,string,DateTimeOffset,DateTimeOffset,CancellationToken)"/>/
/// <see cref="OptionOiSeries.LoadAsync(NiftySignalDbContext,string,DateTimeOffset,DateTimeOffset,CancellationToken)"/>
/// call would have produced for the same (token, dayStart, dayEnd) -- the incremental overloads this
/// class calls only change HOW MUCH of the tick table gets re-queried, never what the resulting
/// series contains. A Host restart mid-day simply starts with an empty cache (this class holds no
/// durable state, never writes anything) and re-derives everything from scratch on the first poll
/// after restart, exactly like today -- no restart-safety story needed beyond what already exists.
/// </summary>
public sealed class LiveOptionSeriesCache
{
    DateOnly? _cachedDate;
    readonly Dictionary<string, OptionQuoteSeries> _quoteByToken = new();
    readonly Dictionary<string, OptionOiSeries> _oiByToken = new();

    /// <summary>Diagnostic only (Phase G restart-safety addendum, docs/LIVE_PARITY_PLAN.md) -- how many tokens' quote series are currently warm, so a replay/perf harness can report cache state around a simulated restart without reaching into private fields.</summary>
    public int QuoteTokenCountForDiagnostics => _quoteByToken.Count;

    /// <summary>Diagnostic only (Phase G restart-safety addendum) -- same as <see cref="QuoteTokenCountForDiagnostics"/> for OI series.</summary>
    public int OiTokenCountForDiagnostics => _oiByToken.Count;

    public async Task<OptionQuoteSeries> GetQuoteSeriesAsync(
        NiftySignalDbContext source, DateOnly asOfDate, string token, DateTimeOffset dayStart, DateTimeOffset dayEnd, CancellationToken cancellationToken)
    {
        ResetIfNewDay(asOfDate);
        var existing = _quoteByToken.GetValueOrDefault(token);
        var updated = await OptionQuoteSeries.LoadAsync(existing, source, token, dayStart, dayEnd, cancellationToken);
        _quoteByToken[token] = updated;
        return updated;
    }

    public async Task<OptionOiSeries> GetOiSeriesAsync(
        NiftySignalDbContext source, DateOnly asOfDate, string token, DateTimeOffset dayStart, DateTimeOffset dayEnd, CancellationToken cancellationToken)
    {
        ResetIfNewDay(asOfDate);
        var existing = _oiByToken.GetValueOrDefault(token);
        var updated = await OptionOiSeries.LoadAsync(existing, source, token, dayStart, dayEnd, cancellationToken);
        _oiByToken[token] = updated;
        return updated;
    }

    /// <summary>
    /// A new trading day means yesterday's cached series are for the wrong day entirely -- discard
    /// them rather than let a stale token-keyed entry from a prior day silently answer for today (a
    /// token can, in principle, be reused across days in the underlying Ticks table's own token
    /// space if the exchange ever reassigns one, however unlikely -- discarding on date change costs
    /// nothing and removes the question entirely).
    /// </summary>
    void ResetIfNewDay(DateOnly asOfDate)
    {
        if (_cachedDate == asOfDate)
        {
            return;
        }

        _cachedDate = asOfDate;
        _quoteByToken.Clear();
        _oiByToken.Clear();
    }
}
