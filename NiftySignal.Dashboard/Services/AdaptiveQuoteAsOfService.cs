using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.Dashboard.Services;

/// <summary>One quote resolved as of a boundary. <see cref="AvailableAtUtc"/> is when the source tick became available.</summary>
public sealed record PinnedQuote(QuickQuote Quote, DateTimeOffset AvailableAtUtc);

/// <summary>
/// Deterministic Live Quote state for a pinned Telegram screenshot (08-Oct plan, section 18).
/// Every value comes from ticks available at or before <see cref="BoundaryUtc"/>; nothing depends on the
/// wall-clock time the screenshot browser ran.
/// </summary>
public sealed record PinnedQuoteSnapshot(
    DateTimeOffset? BoundaryUtc,
    string? UnavailableReason,
    PinnedQuote? Spot,
    PinnedQuote? Future,
    PinnedQuote? Vix,
    decimal? CallStrike,
    decimal? PutStrike,
    PinnedQuote? Call,
    PinnedQuote? Put)
{
    public static PinnedQuoteSnapshot Unavailable(string reason, DateTimeOffset? boundary = null) =>
        new(boundary, reason, null, null, null, null, null, null, null);
}

/// <summary>Pure tick selection so the as-of rule is unit-testable without a database.</summary>
public static class AsOfTickSelector
{
    /// <summary>A tick is available when both its exchange and receive timestamps are known; availability is the later of the two (same convention as the adaptive observer).</summary>
    public static DateTimeOffset AvailableAt(Tick tick) =>
        tick.ReceivedAt < tick.ExchangeTimestamp ? tick.ExchangeTimestamp : tick.ReceivedAt;

    /// <summary>Latest valid (positive price) tick available at or before <paramref name="boundary"/>, ties broken by source Id. Never a later tick.</summary>
    public static Tick? Latest(IEnumerable<Tick> candidates, DateTimeOffset boundary) =>
        candidates.Where(t => t.LastPrice > 0m && AvailableAt(t) <= boundary)
            .OrderByDescending(AvailableAt).ThenByDescending(t => t.Id)
            .FirstOrDefault();
}

/// <summary>
/// Resolves the pinned quote state for a screenshot job: spot, the session's frozen future, VIX, and the center
/// CE/PE of the target completed option-band row, all as of that bar's end boundary.
/// </summary>
public sealed class AdaptiveQuoteAsOfService(
    IDbContextFactory<NiftySignalDbContext> sourceFactory,
    IDbContextFactory<AdaptiveObserverDbContext> observerFactory)
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    /// <summary>
    /// Candidate window per token, newest exchange timestamps first. Option/index ticks arrive a few per second at most, so the
    /// window comfortably spans the second containing the boundary; it exists to keep the query on the (Token, ExchangeTimestamp) index.
    /// </summary>
    const int CandidateWindow = 200;

    public async Task<PinnedQuoteSnapshot> LoadAsync(long? sessionId, int targetBarSeq, CancellationToken ct = default)
    {
        if (sessionId is not { } id) return PinnedQuoteSnapshot.Unavailable("No adaptive session is pinned to this capture.");

        await using var observer = await observerFactory.CreateDbContextAsync(ct);
        var session = await observer.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (session is null) return PinnedQuoteSnapshot.Unavailable("Pinned adaptive session was not found.");

        DateTimeOffset boundary;
        decimal? centerStrike;
        if (targetBarSeq > 0)
        {
            var bar = await observer.FutureBars.AsNoTracking()
                .SingleOrDefaultAsync(x => x.SessionId == id && x.BarSeq == targetBarSeq, ct);
            if (bar is null) return PinnedQuoteSnapshot.Unavailable("Pinned completed bar was not found.");
            boundary = bar.EndAvailableAtUtc;
            var band = await observer.OptionBandBars.AsNoTracking()
                .Where(x => x.SessionId == id && x.BarSeq == targetBarSeq && x.BandAvailable && x.CenterStrike > 0)
                .Select(x => x.CenterStrike).FirstOrDefaultAsync(ct);
            centerStrike = band > 0 ? (decimal)band : null;
        }
        else
        {
            // Initialization capture: the frozen 09:30 boundary and the frozen 09:30 diagnostic center.
            boundary = session.OpeningWindowEndUtc;
            centerStrike = session.ResidualCenterStrike is > 0 ? (decimal)session.ResidualCenterStrike.Value : null;
        }

        var dayStartUtc = new DateTimeOffset(session.TradeDate.ToDateTime(TimeOnly.MinValue), IstOffset).ToUniversalTime();
        await using var source = await sourceFactory.CreateDbContextAsync(ct);

        var indexRows = await source.Instruments.AsNoTracking()
            .Where(i => i.AsOfDate == session.TradeDate && i.Underlying == "NIFTY"
                && (i.InstrumentType == InstrumentType.Index || i.InstrumentType == InstrumentType.Vix))
            .Select(i => new { i.Token, i.InstrumentType }).ToListAsync(ct);
        var spotTokens = indexRows.Where(x => x.InstrumentType == InstrumentType.Index).Select(x => x.Token).Distinct().ToArray();
        var vixTokens = indexRows.Where(x => x.InstrumentType == InstrumentType.Vix).Select(x => x.Token).Distinct().ToArray();

        string? callToken = null, putToken = null;
        if (centerStrike is { } strike && !string.IsNullOrWhiteSpace(session.OptionUniverseJson))
        {
            var options = JsonSerializer.Deserialize<List<ObserverOptionInstrument>>(session.OptionUniverseJson) ?? [];
            callToken = options.FirstOrDefault(x => x.OptionType == OptionType.Call && (decimal)x.Strike == strike)?.Token;
            putToken = options.FirstOrDefault(x => x.OptionType == OptionType.Put && (decimal)x.Strike == strike)?.Token;
        }

        // A non-unique spot instrument is ambiguous: show it unavailable rather than guess.
        var spot = spotTokens.Length == 1 ? await QuoteAsync(source, spotTokens[0], dayStartUtc, boundary, ct) : null;
        var future = await QuoteAsync(source, session.FutureToken, dayStartUtc, boundary, ct);
        var vix = vixTokens.Length == 1 ? await QuoteAsync(source, vixTokens[0], dayStartUtc, boundary, ct) : null;
        var call = callToken is null ? null : await QuoteAsync(source, callToken, dayStartUtc, boundary, ct);
        var put = putToken is null ? null : await QuoteAsync(source, putToken, dayStartUtc, boundary, ct);
        return new PinnedQuoteSnapshot(boundary, null, spot, future, vix, centerStrike, centerStrike, call, put);
    }

    static async Task<PinnedQuote?> QuoteAsync(NiftySignalDbContext db, string token,
        DateTimeOffset dayStartUtc, DateTimeOffset boundary, CancellationToken ct)
    {
        var candidates = await db.Ticks.AsNoTracking()
            .Where(t => t.Token == token && t.ExchangeTimestamp >= dayStartUtc && t.ExchangeTimestamp <= boundary
                && t.ReceivedAt <= boundary)
            .OrderByDescending(t => t.ExchangeTimestamp).ThenByDescending(t => t.Id)
            .Take(CandidateWindow).ToListAsync(ct);
        var latest = AsOfTickSelector.Latest(candidates, boundary);
        if (latest is null) return null;

        // Same day-open baseline definition as live mode: the first tick of the trade day.
        var open = await db.Ticks.AsNoTracking()
            .Where(t => t.Token == token && t.ExchangeTimestamp >= dayStartUtc && t.ExchangeTimestamp <= boundary)
            .OrderBy(t => t.ExchangeTimestamp).ThenBy(t => t.Id)
            .Select(t => (decimal?)t.LastPrice).FirstOrDefaultAsync(ct);
        var depth = latest.Depth;
        return new PinnedQuote(new QuickQuote(latest.LastPrice,
            depth?.Bid1Price is > 0 ? depth.Bid1Price : null,
            depth?.Ask1Price is > 0 ? depth.Ask1Price : null,
            open is { } o ? latest.LastPrice - o : null), AsOfTickSelector.AvailableAt(latest));
    }
}
