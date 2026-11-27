using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Persistence;

namespace NiftySignal.Host;

/// <summary>Feature flag of the Futures Top-5 microstructure sidecar (S4). OFF by default: with it off no depth row is read, no depth state exists and no sidecar row is written.</summary>
public sealed class Top5MicrostructureOptions
{
    public const string SectionName = "Adaptive:Top5Microstructure";

    /// <summary>Master switch. Default false. Enabling it only adds the futures-depth5-v1 sidecar; core rows, digests, cursors and metric versions are unaffected.</summary>
    public bool Enabled { get; set; }

    /// <summary>S5 switch for the per-contract options Top-5 sidecar (options-depth5-v1). Default false; independent of <see cref="Enabled"/>. Adds only sidecar rows and their own attestation.</summary>
    public bool OptionsEnabled { get; set; }
}

/// <summary>
/// Streaming Top-5 depth reader (S3/S4 of the Top-5 microstructure design). Same row selection as <see cref="AdaptiveSourceTickReader.ReadRawSessionAsync"/> / <see cref="AdaptiveSourceTickReader.ReadRawAfterIdAsync"/>
/// (same token / time predicates, Id order) with the full 20-column depth, read separately so the existing Level-1 reader and its projection stay untouched.
/// </summary>
public sealed class AdaptiveDepthTickReader
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketOpen = new(9, 15);

    public async IAsyncEnumerable<DepthRawRow> StreamSessionAsync(
        NiftySignalDbContext db,
        DateOnly day,
        IReadOnlyCollection<string> tokens,
        DateTimeOffset throughUtc,
        [EnumeratorCancellation] CancellationToken ct,
        bool includeBeyondThrough = false)
    {
        if (tokens.Count == 0) yield break;

        var startUtc = new DateTimeOffset(day.ToDateTime(MarketOpen), IstOffset).ToUniversalTime();
        var queryStart = startUtc.AddMinutes(-1);
        var upper = includeBeyondThrough
            ? new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), IstOffset).ToUniversalTime().AddTicks(-1)
            : throughUtc;
        var tokenArray = tokens.Distinct(StringComparer.Ordinal).ToArray();

        var rows = db.Ticks.AsNoTracking()
            .Where(t => tokenArray.Contains(t.Token)
                && t.ExchangeTimestamp >= queryStart && t.ReceivedAt >= queryStart
                && t.ExchangeTimestamp <= upper && t.ReceivedAt <= upper)
            .OrderBy(t => t.Id)
            .Select(t => new { t.Token, t.Id, t.ExchangeTimestamp, t.ReceivedAt, t.Depth })
            .AsAsyncEnumerable();

        await foreach (var r in rows.WithCancellation(ct))
            yield return new DepthRawRow(r.Token, r.Id, r.ExchangeTimestamp, r.ReceivedAt, r.Depth);
    }

    /// <summary>
    /// Recovery paging: rows RECEIVED in [fromUtc, toUtc), same day predicates as <see cref="ReadAfterIdAsync"/>, Id order. A time window (not an Id keyset with LIMIT) lets PostgreSQL use the token/time index;
    /// the Id-ordered LIMIT form walks the primary key and was measured at ~24 s per page on the 2026-10-29 copy.
    /// </summary>
    public async Task<IReadOnlyList<DepthRawRow>> ReadReceivedWindowAsync(
        NiftySignalDbContext db, IReadOnlyCollection<string> tokens, DateTimeOffset fromUtc, DateTimeOffset toUtc, DateOnly day, CancellationToken ct)
    {
        if (tokens.Count == 0) return [];
        var tokenArray = tokens.Distinct(StringComparer.Ordinal).ToArray();
        var lower = new DateTimeOffset(day.ToDateTime(MarketOpen), IstOffset).ToUniversalTime().AddMinutes(-1);
        var upper = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), IstOffset).ToUniversalTime();
        var rows = await db.Ticks.AsNoTracking()
            .Where(t => tokenArray.Contains(t.Token) && t.ReceivedAt >= fromUtc && t.ReceivedAt < toUtc
                && t.ExchangeTimestamp >= lower && t.ReceivedAt >= lower && t.ExchangeTimestamp < upper && t.ReceivedAt < upper)
            .OrderBy(t => t.Id)
            .Select(t => new { t.Token, t.Id, t.ExchangeTimestamp, t.ReceivedAt, t.Depth })
            .ToListAsync(ct);
        return rows.Select(r => new DepthRawRow(r.Token, r.Id, r.ExchangeTimestamp, r.ReceivedAt, r.Depth)).ToList();
    }

    /// <summary>Live poll: rows with Id greater than the cursor, same day window as <see cref="AdaptiveSourceTickReader.ReadRawAfterIdAsync"/>, Id order.</summary>
    public async Task<IReadOnlyList<DepthRawRow>> ReadAfterIdAsync(
        NiftySignalDbContext db, IReadOnlyCollection<string> tokens, long afterId, DateOnly day, CancellationToken ct, int take = int.MaxValue)
    {
        if (tokens.Count == 0) return [];
        var tokenArray = tokens.Distinct(StringComparer.Ordinal).ToArray();
        var lower = new DateTimeOffset(day.ToDateTime(MarketOpen), IstOffset).ToUniversalTime().AddMinutes(-1);
        var upper = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), IstOffset).ToUniversalTime();
        var rows = await db.Ticks.AsNoTracking()
            .Where(t => t.Id > afterId && tokenArray.Contains(t.Token)
                && t.ExchangeTimestamp >= lower && t.ReceivedAt >= lower && t.ExchangeTimestamp < upper && t.ReceivedAt < upper)
            .OrderBy(t => t.Id)
            .Take(take)
            .Select(t => new { t.Token, t.Id, t.ExchangeTimestamp, t.ReceivedAt, t.Depth })
            .ToListAsync(ct);
        return rows.Select(r => new DepthRawRow(r.Token, r.Id, r.ExchangeTimestamp, r.ReceivedAt, r.Depth)).ToList();
    }
}

/// <summary>
/// The Host's single entry to the Futures depth channel. Disabled (the default) it is inert; enabled it builds the <see cref="FuturesDepth5Feed"/> for recovery (streaming the whole session of the future token)
/// and appends new rows during live polling. The same feed code serves both, so live and replay derive identical events from identical rows.
/// </summary>
public sealed class FuturesDepth5Gateway(IOptions<Top5MicrostructureOptions> options, AdaptiveDepthTickReader reader, IOptions<AdaptiveRecoveryOptions>? recoveryOptions = null)
{
    readonly int _liveBatch = (recoveryOptions?.Value ?? new AdaptiveRecoveryOptions()).LiveReadBatchSize;
    readonly TimeSpan _window = TimeSpan.FromSeconds((recoveryOptions?.Value ?? new AdaptiveRecoveryOptions()).ReadWindowSeconds);

    public bool Enabled => options.Value.Enabled;

    /// <summary>S5: per-contract options Top-5 sidecar switch (default off, independent of <see cref="Enabled"/>).</summary>
    public bool OptionsEnabled => options.Value.OptionsEnabled;

    public OptionsDepth5Source CreateOptionsSource(NiftySignalDbContext source, DateOnly day, IReadOnlyCollection<string> optionTokens, DateTimeOffset notBefore) =>
        new(reader, source, optionTokens, day, notBefore);

    public Task PollOptionsAsync(NiftySignalDbContext source, FuturesDepth5Feed feed, IReadOnlyCollection<string> optionTokens, DateOnly day, DateTimeOffset notBefore, CancellationToken ct,
        DateTimeOffset? coverReceivedAt = null) =>
        DrainAsync(source, feed, optionTokens, day, notBefore, coverReceivedAt, ct);

    /// <summary>Bounded-memory recovery source for the future token (the same slicing and per-token Id order the options source uses).</summary>
    public OptionsDepth5Source CreateFuturesSource(NiftySignalDbContext source, DateOnly day, string futureToken, DateTimeOffset notBefore) =>
        new(reader, source, [futureToken], day, notBefore);

    /// <summary>
    /// Reads the rows after the feed's cursor in bounded queries: all of them, or (during a live backlog) only until a row received at or after <paramref name="coverReceivedAt"/> has been fed, so the feed never
    /// holds much more depth than the Level-1 rows being processed. With no cursor yet the start is found through the received-time index (see <see cref="AdaptiveSourceTickReader.FindStartCursorAsync"/>).
    /// </summary>
    async Task DrainAsync(NiftySignalDbContext source, FuturesDepth5Feed feed, IReadOnlyCollection<string> tokens, DateOnly day, DateTimeOffset notBefore, DateTimeOffset? coverReceivedAt, CancellationToken ct)
    {
        while (true)
        {
            if (coverReceivedAt is { } cover && feed.LastFetchedReceivedAt >= cover) return;
            var cursor = feed.LastFetchedId;
            long after = cursor;
            if (cursor <= 0)
            {
                if (await AdaptiveSourceTickReader.FindStartCursorAsync(source, tokens, day, _window, ct) is not { } start) return;
                after = start;
            }

            var rows = await reader.ReadAfterIdAsync(source, tokens, after, day, ct, _liveBatch);
            feed.Ingest(rows, notBefore);
            if (rows.Count < _liveBatch || feed.LastFetchedId <= cursor) return;
        }
    }

    public async Task<FuturesDepth5Feed> RecoverFeedAsync(NiftySignalDbContext source, DateOnly day, string futureToken, DateTimeOffset notBefore, CancellationToken ct)
    {
        var feed = new FuturesDepth5Feed();
        var batch = new List<DepthRawRow>(4096);
        await foreach (var row in reader.StreamSessionAsync(source, day, [futureToken], DateTimeOffset.MaxValue, ct, includeBeyondThrough: true))
        {
            batch.Add(row);
            if (batch.Count == 4096)
            {
                feed.Ingest(batch, notBefore);
                batch.Clear();
            }
        }

        feed.Ingest(batch, notBefore);
        return feed;
    }

    public Task PollAsync(NiftySignalDbContext source, FuturesDepth5Feed feed, string futureToken, DateOnly day, DateTimeOffset notBefore, CancellationToken ct, DateTimeOffset? coverReceivedAt = null) =>
        DrainAsync(source, feed, [futureToken], day, notBefore, coverReceivedAt, ct);
}

/// <summary>
/// Options Top-5 events for recovery with BOUNDED memory: the day's option rows are read page by page (keyset on Id, each page fully materialized, so no reader is held open across the recovery's other queries) and
/// only rows that can still produce an event at or before the next group's instant are ingested. A row's AvailableAt is never earlier than its ReceivedAt, so every event with AvailableAt &lt;= T comes from a row with
/// ReceivedAt &lt;= T; the <see cref="LookaheadMargin"/> additionally covers a row whose ReceivedAt is slightly out of Id order. The events delivered through any instant are therefore exactly those a whole-day ingest
/// would deliver (and exactly those live polling delivers), while the pending set stays tiny instead of holding the whole day.
/// </summary>
public sealed class OptionsDepth5Source : IAdaptiveDepthSource
{
    public static readonly TimeSpan LookaheadMargin = TimeSpan.FromSeconds(5);
    static readonly TimeSpan WindowLength = TimeSpan.FromMinutes(2);

    readonly AdaptiveDepthTickReader _reader;
    readonly NiftySignalDbContext _source;
    readonly IReadOnlyCollection<string> _tokens;
    readonly DateOnly _day;
    readonly DateTimeOffset _notBefore;
    // Per-token buffers kept in Id order (the order the live reader and a whole-day ingest feed the per-token normalizer). A window is a RECEIVED-time slice, so a row whose Id is lower but ReceivedAt later than
    // another row of the same token can arrive in a later window; sorting each token's unconsumed rows by Id restores the exact per-token Id order, and a token is blocked at its first row beyond the limit.
    readonly Dictionary<string, TokenBuffer> _buffers = new(StringComparer.Ordinal);
    int _buffered;
    bool _exhausted;
    long _lowestPreviousWindow = long.MaxValue;
    long _lowestLastWindow = long.MaxValue;
    long _lastReadId;
    DateTimeOffset _windowStart;
    readonly DateTimeOffset _dayEnd;

    sealed class TokenBuffer
    {
        public List<DepthRawRow> Rows = new();
        public int Head;
        public long LastIngestedId = long.MinValue;
    }

    public OptionsDepth5Source(AdaptiveDepthTickReader reader, NiftySignalDbContext source, IReadOnlyCollection<string> tokens, DateOnly day, DateTimeOffset notBefore)
    {
        _reader = reader; _source = source; _tokens = tokens; _day = day; _notBefore = notBefore;
        _windowStart = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5)).ToUniversalTime().AddMinutes(-1);
        _dayEnd = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)).ToUniversalTime();
    }

    /// <summary>Rows that reached the feed with an Id lower than an already-ingested row of the same token: the received-time disorder exceeded the read-ahead (one window). Must be 0; any other value is a data-order defect.</summary>
    public long OutOfOrderRows { get; private set; }

    public FuturesDepth5Feed Feed { get; } = new();

    /// <summary>Largest number of option rows ever held in the page buffer at once (diagnostic for the bounded-memory claim).</summary>
    public int PeakBufferedRows { get; private set; }

    public long RowsRead { get; private set; }

    /// <summary>
    /// Ingests every row that can matter for events at or before <paramref name="instant"/>. Windows are read until one FULL window beyond the limit is loaded, so a lower-Id row of a token that was received up to
    /// one window later than a higher-Id row of the same token is already buffered and ingested first (per-token Id order). Every stored row lies in exactly one received-time window: no row is skipped or read twice.
    /// </summary>
    public async Task AdvanceToAsync(DateTimeOffset instant, CancellationToken ct)
    {
        var limit = instant > DateTimeOffset.MaxValue - LookaheadMargin ? DateTimeOffset.MaxValue : instant + LookaheadMargin;
        var readLimit = limit > DateTimeOffset.MaxValue - WindowLength ? DateTimeOffset.MaxValue : limit + WindowLength;
        while (!_exhausted && _windowStart < _dayEnd && _windowStart <= readLimit)
        {
            var windowEnd = _windowStart + WindowLength;
            var rows = await _reader.ReadReceivedWindowAsync(_source, _tokens, _windowStart, windowEnd, _day, ct);
            _windowStart = windowEnd;
            _lowestPreviousWindow = _lowestLastWindow;
            _lowestLastWindow = rows.Count == 0 ? long.MaxValue : rows[0].Id;
            if (rows.Count == 0) continue;
            _lastReadId = Math.Max(_lastReadId, rows[^1].Id);
            RowsRead += rows.Count;
            foreach (var r in rows)
            {
                if (!_buffers.TryGetValue(r.Token, out var b)) _buffers[r.Token] = b = new TokenBuffer();
                var unsorted = b.Rows.Count > b.Head && b.Rows[^1].Id > r.Id;
                b.Rows.Add(r);
                _buffered++;
                if (unsorted) b.Rows.Sort(b.Head, b.Rows.Count - b.Head, IdComparer.Instance);
            }

            PeakBufferedRows = Math.Max(PeakBufferedRows, _buffered);
        }

        if (_windowStart >= _dayEnd) _exhausted = true;
        var one = new List<DepthRawRow>(1);
        foreach (var b in _buffers.Values)
        {
            while (b.Head < b.Rows.Count && b.Rows[b.Head].ReceivedAt <= limit)
            {
                var row = b.Rows[b.Head++];
                _buffered--;
                if (row.Id < b.LastIngestedId) OutOfOrderRows++;
                else b.LastIngestedId = row.Id;
                one.Clear();
                one.Add(row);
                Feed.Ingest(one, _notBefore);
            }

            if (b.Head > 1024 && b.Head * 2 > b.Rows.Count) { b.Rows.RemoveRange(0, b.Head); b.Head = 0; }
        }
    }

    /// <summary>The unfed rows expressed as an Id position (see <see cref="AdaptiveRecoveryTickStream.Capture"/>).</summary>
    public AdaptiveSourcePosition Capture()
    {
        var lowest = Math.Min(_lowestPreviousWindow, _lowestLastWindow);
        foreach (var b in _buffers.Values)
            if (b.Head < b.Rows.Count) lowest = Math.Min(lowest, b.Rows[b.Head].Id);
        var resume = lowest == long.MaxValue ? _lastReadId : lowest - 1;
        var skip = _buffers.Where(x => x.Value.LastIngestedId > resume).ToDictionary(x => x.Key, x => x.Value.LastIngestedId, StringComparer.Ordinal);
        return new AdaptiveSourcePosition(resume, skip);
    }

    sealed class IdComparer : IComparer<DepthRawRow>
    {
        public static readonly IdComparer Instance = new();
        public int Compare(DepthRawRow x, DepthRawRow y) => x.Id.CompareTo(y.Id);
    }

    /// <summary>Ingests the rest of the day so the feed's cursor equals the last stored row (the not-yet-stable events stay pending for live polling).</summary>
    public Task FinishAsync(CancellationToken ct) => AdvanceToAsync(DateTimeOffset.MaxValue, ct);
}
