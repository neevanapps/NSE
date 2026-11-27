using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;
using NiftySignal.MarketSummary;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.PaperEngineV2.Frames;
using NiftySignal.PaperEngineV2.Runtime;
using NiftySignal.Persistence;

namespace NiftySignal.ExecutionService;

/// <summary>
/// The database feed. Bounded reads only: the raw tick table is tailed by its identity (one writer, the Host ingestion worker), published bars are read in sequence, and a frame is assembled by the existing
/// frame builder from the Observer's persisted, verified rows.
/// </summary>
public sealed class DbExecutionFeed(Func<AdaptiveObserverDbContext> observerDb, Func<NiftySignalDbContext> sourceDb, ExecutionServiceOptions options) : IExecutionFeed
{
    readonly InputSourceFrameFactory _frames = new(new DbFrameInputSource(observerDb, sourceDb));
    MarketStateFrameBuilder _builder = new();
    long _builderSession = -1;
    int _builtThrough = -1;
    AdaptiveIncrementalTickNormalizer _normalizer = new();
    long _tickSession = -1;
    long _cursor;
    string? _vixToken;
    string? _futureToken;
    ObserverState _lastObserver = new(false, null, null);

    public async Task<SessionRead> ReadSessionAsync(DateOnly tradeDate, CancellationToken ct)
    {
        try
        {
            await using var db = observerDb();
            var rows = await db.Sessions.AsNoTracking().Where(x => x.TradeDate == tradeDate).ToListAsync(ct);
            var row = rows.Where(x => AdaptiveFixedClockV1.IsFixedClock(x.ModelVersion)).OrderByDescending(x => x.Id).FirstOrDefault();
            if (row is null) return new SessionRead(null, _lastObserver = new ObserverState(false, null, null));
            var runtime = await db.Runtime.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == row.Id, ct);
            _futureToken = string.IsNullOrWhiteSpace(row.FutureToken) ? null : row.FutureToken;
            // without the future's token its backlog cannot be seen, so the processed position is not usable
            _lastObserver = new ObserverState(true, runtime?.RuntimeStatus, runtime?.LastHeartbeatUtc, false,
                _futureToken is null ? null : runtime?.LastProcessedSourceAvailableAtUtc, runtime?.LastCompletedBarSeq);
            return new SessionRead(SessionOf(row), _lastObserver);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SessionRead(null, _lastObserver with { ReadFailed = true });
        }
    }

    internal static V2SessionInfo? SessionOf(AdaptiveSessionStateRow row)
    {
        if (row.WeeklyOptionExpiry is not { } expiry || string.IsNullOrWhiteSpace(row.OptionUniverseJson)) return null;
        var options = (JsonSerializer.Deserialize<List<ObserverOptionInstrument>>(row.OptionUniverseJson) ?? []).Where(o => o.ExpiryDate == expiry).ToList();
        if (options.Count == 0) return null;
        var strikes = options.GroupBy(o => o.Strike).OrderBy(g => g.Key)
            .Select(g => new ListedStrike(g.Key, g.FirstOrDefault(o => o.OptionType == OptionType.Call)?.Token, g.FirstOrDefault(o => o.OptionType == OptionType.Put)?.Token)).ToList();
        return new V2SessionInfo(row.Id, row.TradeDate, row.ModelVersion, row.AtmRuleVersion, expiry, strikes, options.ToDictionary(o => o.Token, o => o.LotSize, StringComparer.Ordinal));
    }

    public async Task<IReadOnlyList<FeedTick>> ReadTicksAsync(V2SessionInfo session, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = sourceDb();
        if (_tickSession != session.SessionId || _vixToken is null)
        {
            var vix = await db.Instruments.AsNoTracking().Where(i => i.AsOfDate == session.TradeDate && i.InstrumentType == InstrumentType.Vix).Select(i => i.Token).Distinct().ToListAsync(ct);
            _vixToken = vix.Count == 1 ? vix[0] : null;              // never a guessed instrument: without a unique India VIX the partial target is disabled
        }

        var tokens = session.LotSizes.Keys.ToList();
        if (_vixToken is not null) tokens.Add(_vixToken);
        if (_futureToken is not null) tokens.Add(_futureToken);
        var upper = await db.Ticks.AsNoTracking().OrderByDescending(t => t.Id).Select(t => t.Id).FirstOrDefaultAsync(ct);
        var result = new List<FeedTick>();
        if (_tickSession != session.SessionId)
        {
            var from = (now - options.StartupQuoteLookback).ToUniversalTime();
            var window = db.Ticks.AsNoTracking().Where(t => tokens.Contains(t.Token) && t.ExchangeTimestamp >= from && t.ReceivedAt >= from && t.Id <= upper);
            var normalizer = new AdaptiveIncrementalTickNormalizer();
            Append(result, normalizer, await ProjectAsync(window.OrderBy(t => t.Id), ct));
            _normalizer = normalizer; _cursor = upper; _tickSession = session.SessionId;
            return result;
        }

        while (_cursor < upper)
        {
            var after = _cursor;
            var batch = await ProjectAsync(db.Ticks.AsNoTracking().Where(t => t.Id > after && t.Id <= upper && tokens.Contains(t.Token)).OrderBy(t => t.Id).Take(options.TickBatchSize), ct);
            Append(result, _normalizer, batch);
            _cursor = batch.Count < options.TickBatchSize ? upper : batch[^1].Tick.Id;
        }

        return result;
    }

    void Append(List<FeedTick> into, AdaptiveIncrementalTickNormalizer normalizer, IReadOnlyList<(string Token, ObserverRawTick Tick)> raw)
    {
        foreach (var (token, tick) in raw)
            if (normalizer.Process(token, tick) is { } clean) into.Add(new FeedTick(token, clean, token == _vixToken, token == _futureToken));
    }

    static async Task<IReadOnlyList<(string Token, ObserverRawTick Tick)>> ProjectAsync(IQueryable<NiftySignal.Domain.Entities.Tick> query, CancellationToken ct)
    {
        var rows = await query.Select(t => new
        {
            t.Token, t.Id, t.ExchangeTimestamp, t.ReceivedAt, t.LastPrice, t.Volume, t.OpenInterest,
            Bid = t.Depth == null ? 0m : t.Depth.Bid1Price, Ask = t.Depth == null ? 0m : t.Depth.Ask1Price,
            BidQty = t.Depth == null ? 0L : t.Depth.Bid1Qty, AskQty = t.Depth == null ? 0L : t.Depth.Ask1Qty,
        }).ToListAsync(ct);
        return rows.Select(t => (t.Token, new ObserverRawTick(t.Id, t.ExchangeTimestamp, t.ReceivedAt, (double)t.LastPrice, (double)t.Bid, (double)t.Ask, t.BidQty, t.AskQty, t.Volume, t.OpenInterest))).ToList();
    }

    public async Task<IReadOnlyList<PublishedBar>> ReadPublishedAsync(long sessionId, int afterBarSeq, int limit, CancellationToken ct)
    {
        await using var db = observerDb();
        return await db.BarPublications.AsNoTracking().Where(x => x.SessionId == sessionId && x.BarSeq > afterBarSeq).OrderBy(x => x.BarSeq).Take(limit)
            .Select(x => new PublishedBar(x.BarSeq, x.Verified, x.UnverifiedReason)).ToListAsync(ct);
    }

    public async Task<(MarketStateFrame? Frame, string? Problem)> BuildFrameAsync(V2SessionInfo session, PublishedBar published, CancellationToken ct)
    {
        try
        {
            if (_builderSession != session.SessionId || published.BarSeq != _builtThrough + 1)
            {
                // The frame's same-instant ordinal depends on the bars built before it. After a restart or a retry, build the earlier bars of the same market update first so the frame is identical.
                _builder = new MarketStateFrameBuilder(); _builderSession = session.SessionId;
                List<PublishedBar> siblings;
                await using (var db = observerDb())
                {
                    var end = await db.FutureBars.AsNoTracking().Where(x => x.SessionId == session.SessionId && x.BarSeq == published.BarSeq).Select(x => (DateTimeOffset?)x.EndAvailableAtUtc).SingleOrDefaultAsync(ct);
                    var earlier = end is null ? [] : await db.FutureBars.AsNoTracking().Where(x => x.SessionId == session.SessionId && x.BarSeq < published.BarSeq && x.EndAvailableAtUtc == end)
                        .Select(x => x.BarSeq).ToListAsync(ct);
                    siblings = earlier.Count == 0 ? [] : await db.BarPublications.AsNoTracking().Where(x => x.SessionId == session.SessionId && earlier.Contains(x.BarSeq)).OrderBy(x => x.BarSeq)
                        .Select(x => new PublishedBar(x.BarSeq, x.Verified, x.UnverifiedReason)).ToListAsync(ct);
                }

                foreach (var sibling in siblings) await BuildOneAsync(session, sibling, ct);
            }

            return await BuildOneAsync(session, published, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _builtThrough = -1;
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    async Task<(MarketStateFrame? Frame, string? Problem)> BuildOneAsync(V2SessionInfo session, PublishedBar published, CancellationToken ct)
    {
        AdaptiveFutureBarRow? bar;
        double? centre;
        await using (var db = observerDb())
        {
            bar = await db.FutureBars.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == session.SessionId && x.BarSeq == published.BarSeq, ct);
            var centres = await db.OptionBandBars.AsNoTracking().Where(x => x.SessionId == session.SessionId && x.BarSeq == published.BarSeq).OrderBy(x => x.Side).Select(x => (double?)x.CenterStrike).ToListAsync(ct);
            centre = centres.FirstOrDefault(c => c is > 0d);
        }

        if (bar is null) return (null, $"bar {published.BarSeq} is published but not persisted");
        var input = new BarInput(session.SessionId, session.TradeDate, session.ModelVersion, session.AtmRuleVersion, bar.BarSeq, bar.StartAvailableAtUtc, bar.EndAvailableAtUtc, bar.LastSourceTickId, bar.DurationSeconds,
            published.Verified, published.Verified ? null : published.UnverifiedReason ?? AdaptiveBarPublisher.UnknownIntegrity);
        var result = await _frames.BuildAsync(new V2BarNotice(session, input, centre, []), _builder, ct);
        _builtThrough = result.Frame is null ? -1 : published.BarSeq;
        return result;
    }

    public async Task<IReadOnlyList<ContractAverage>> ReadAveragesAsync(V2SessionInfo session, IReadOnlyCollection<string> tokens, int barSeq, CancellationToken ct)
    {
        await using var db = observerDb();
        return await DbFrameInputSource.AveragesAsync(db, session.SessionId, session.Expiry, tokens, barSeq, ct);
    }
}
