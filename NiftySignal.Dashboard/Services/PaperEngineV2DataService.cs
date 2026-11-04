using System.Text.Json;
using Npgsql;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// Read-only access to the Paper Engine V2 SHADOW tables (schema <c>pev2</c>) for the Home page. The connection is opened with <c>default_transaction_read_only=on</c> and a statement timeout, every query is a bounded
/// SELECT (events: time-indexed, at most 10,000 rows; one latest frame; counts), and nothing here ever writes. When V2 is not installed or the schema is incompatible the view says so instead of showing anything.
/// </summary>
public sealed class PaperEngineV2DataService : IDisposable
{
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    static readonly TimeSpan SchemaCacheFor = TimeSpan.FromSeconds(60);

    readonly NpgsqlDataSource _db;
    readonly PostgresEventStore _events;
    SchemaVerdict? _schema;
    DateTimeOffset _schemaAt;

    public PaperEngineV2DataService(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString) { Options = "-c default_transaction_read_only=on -c statement_timeout=5000", ApplicationName = "niftysignal-dashboard-v2-readonly", MaxPoolSize = 4 };
        _db = NpgsqlDataSource.Create(b.ConnectionString);
        _events = new PostgresEventStore(_db);
    }

    public static DateOnly IstDate(DateTimeOffset t) => DateOnly.FromDateTime((t + Ist).DateTime);

    public async Task<V2HomeView> LoadAsync(DateTimeOffset now, CancellationToken ct)
    {
        var day = IstDate(now);
        try
        {
            if (_schema is null || now - _schemaAt > SchemaCacheFor || !_schema.Compatible)
            {
                _schema = await V2SchemaCheck.CheckAsync(_db, ct);
                _schemaAt = now;
            }

            if (!_schema.Compatible) return V2HomeView.Unavailable(now, day, [.. _schema.Problems]);

            var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), Ist).ToUniversalTime();
            var to = from.AddDays(1);
            var configured = await ConfiguredAsync(ct);
            var events = await _events.ReadAsync(from, to, null, PostgresEventStore.MaxRead, ct);
            var perf = await PerformanceAsync(from, to, ct);
            var marks = await LatestMarksAsync(ct);
            var health = await HealthAsync(from, to, ct);
            return V2HomeProjection.Build(now, day, health, configured, events, perf, marks, events.Count >= PostgresEventStore.MaxRead);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return V2HomeView.Unavailable(now, day, $"the V2 ledger could not be read ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// The compact opportunity view of one trade date: counts per strategy, rejection counts by reason and a bounded list of the newest rows. Read-only, three bounded statements. When the owner has not run the
    /// AddObservability script (undefined table) or the read-only role lacks SELECT (insufficient privilege) the view says so; nothing throws.
    /// </summary>
    public async Task<V2OpportunityView> LoadOpportunitiesAsync(DateOnly day, CancellationToken ct)
    {
        var d = day.ToString("yyyy-MM-dd");
        try
        {
            var strategies = new List<V2OpportunityCounts>();
            await using (var c = _db.CreateCommand(@"SELECT strategy_id,
                       count(*) FILTER (WHERE stage = 'Decision'),
                       count(*) FILTER (WHERE stage = 'Decision' AND direction IN ('CE','PE')),
                       count(*) FILTER (WHERE order_attempted AND stage = 'Decision'),
                       count(*) FILTER (WHERE category = 9),
                       count(*) FILTER (WHERE stage = 'Decision' AND category IN (3,5,6)),
                       count(*) FILTER (WHERE category = 8)
                FROM pev2.pev2_opportunities WHERE trade_date = $1 GROUP BY strategy_id ORDER BY strategy_id LIMIT 100"))
            {
                c.Parameters.AddWithValue(d);
                await using var r = await c.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) strategies.Add(new V2OpportunityCounts(r.GetString(0), (int)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3), (int)r.GetInt64(4), (int)r.GetInt64(5), (int)r.GetInt64(6)));
            }

            var rejections = new List<V2RejectionCount>();
            await using (var c = _db.CreateCommand("SELECT category_name, reason, count(*) FROM pev2.pev2_opportunities WHERE trade_date = $1 AND category IN (3,5,6,7,8) GROUP BY category_name, reason ORDER BY count(*) DESC, category_name, reason LIMIT 50"))
            {
                c.Parameters.AddWithValue(d);
                await using var r = await c.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) rejections.Add(new V2RejectionCount(r.GetString(0), r.GetString(1), (int)r.GetInt64(2)));
            }

            var recent = new List<V2OpportunityRecent>();
            await using (var c = _db.CreateCommand("SELECT strategy_id, bar_seq, stage, direction, category_name, reason, detail, order_result, decision_utc_ticks FROM pev2.pev2_opportunities WHERE trade_date = $1 AND (direction IN ('CE','PE') OR stage = 'Outcome') ORDER BY decision_utc_ticks DESC, opportunity_id LIMIT 25"))
            {
                c.Parameters.AddWithValue(d);
                await using var r = await c.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) recent.Add(new V2OpportunityRecent(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), new DateTimeOffset(r.GetInt64(8), TimeSpan.Zero)));
            }

            return new V2OpportunityView(true, null, day, strategies, rejections, recent);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable || ex.SqlState == PostgresErrorCodes.InvalidSchemaName)
        {
            return V2OpportunityView.Unavailable(day, "the opportunity log is not installed (the owner has not run the AddObservability script)");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return V2OpportunityView.Unavailable(day, "the Dashboard's read-only role has no SELECT permission on the opportunity log");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return V2OpportunityView.Unavailable(day, $"the opportunity log could not be read ({ex.GetType().Name})");
        }
    }

    /// <summary>A publisher heartbeat older than this means the Host is not updating marks: every Fresh row is then shown Stale.</summary>
    public static readonly TimeSpan PublisherSilence = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The latest published live marks. One bounded read of a tiny table (at most 200 rows) - never a ledger scan - so it can run every second. Rows of another IST trade date are ignored (no cross-session stale marks).
    /// A missing table, a missing SELECT grant or an unreachable database degrades to a plain message.
    /// </summary>
    public async Task<V2LiveMarksView> LoadLiveMarksAsync(DateTimeOffset now, CancellationToken ct)
    {
        var day = IstDate(now);
        var today = day.ToString("yyyy-MM-dd");
        try
        {
            var raw = new List<(string Strategy, string Position, string Day, string Payload, long Published)>();
            await using (var c = _db.CreateCommand("SELECT strategy_id, position_id, trade_date, payload_json, published_utc_ticks FROM pev2.pev2_live_marks WHERE trade_date = $1 ORDER BY strategy_id, position_id LIMIT 200"))
            {
                c.Parameters.AddWithValue(today);
                await using var r = await c.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) raw.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4)));
            }

            var beat = raw.FirstOrDefault(x => x.Position == "_publisher");
            if (beat.Position is null) return V2LiveMarksView.Unavailable(V2PublisherState.Waiting, "the Host has not published any live mark today (live marks may be switched off, or the Host has not started)", day, now);
            var beatAt = new DateTimeOffset(beat.Published, TimeSpan.Zero);
            var silent = now - beatAt > PublisherSilence;
            var rows = new List<V2LiveMarkRow>();
            foreach (var x in raw.Where(x => x.Position != "_publisher"))
            {
                var m = PositionMarkJson.Deserialize(x.Payload);
                if (m is null || m.TradeDate != day) continue;
                var published = new DateTimeOffset(x.Published, TimeSpan.Zero);
                var status = m.Status;
                var detail = m.StatusDetail;
                if (silent && status is MarkStatus.Fresh) { status = MarkStatus.Stale; detail = $"the Host's mark publisher has been silent for {(now - beatAt).TotalSeconds:0}s"; }
                var age = m.QuoteAvailableAt is { } at ? Math.Max(0d, (now - at).TotalSeconds) : (double?)null;
                rows.Add(new V2LiveMarkRow(m, status, detail, age, published));
            }

            return new V2LiveMarksView(silent ? V2PublisherState.Silent : V2PublisherState.Running, silent ? $"the Host's mark publisher has been silent for {(now - beatAt).TotalSeconds:0}s" : null, day, now, beatAt, rows);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable || ex.SqlState == PostgresErrorCodes.InvalidSchemaName)
        {
            return V2LiveMarksView.Unavailable(V2PublisherState.NotInstalled, "live marks are not installed (the owner has not run the AddObservability script)", day, now);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return V2LiveMarksView.Unavailable(V2PublisherState.NoAccess, "the Dashboard's read-only role has no SELECT permission on the live marks", day, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return V2LiveMarksView.Unavailable(V2PublisherState.Unreachable, $"live marks could not be read ({ex.GetType().Name})", day, now);
        }
    }

    async Task<IReadOnlyList<V2ConfiguredStrategy>> ConfiguredAsync(CancellationToken ct)
    {
        var list = new List<V2ConfiguredStrategy>();
        await using var c = _db.CreateCommand("SELECT DISTINCT ON (strategy_id) strategy_id, version, canonical_json FROM pev2.pev2_strategy_configs ORDER BY strategy_id, version DESC, recorded_utc_ticks DESC LIMIT 100");
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var enabled = false;
            try { using var doc = JsonDocument.Parse(r.GetString(2)); enabled = doc.RootElement.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True; }
            catch (JsonException) { }
            list.Add(new V2ConfiguredStrategy(r.GetString(0), r.GetInt32(1), enabled));
        }

        return list;
    }

    async Task<IReadOnlyList<V2StrategyPerformance>> PerformanceAsync(DateTimeOffset todayFrom, DateTimeOffset todayTo, CancellationToken ct)
    {
        var list = new List<V2StrategyPerformance>();
        await using var c = _db.CreateCommand(@"
            WITH x AS (SELECT strategy_id, at_utc_ticks,
                              (payload_json::jsonb ->> 'NetPnl')::numeric AS net, (payload_json::jsonb ->> 'GrossPnl')::numeric AS gross
                       FROM pev2.pev2_events WHERE kind = 'ExitFilled' AND at_utc_ticks >= $1 AND at_utc_ticks < $2)
            SELECT strategy_id,
                   count(*) FILTER (WHERE at_utc_ticks >= $3), count(*) FILTER (WHERE at_utc_ticks >= $3 AND net > 0),
                   coalesce(sum(gross) FILTER (WHERE at_utc_ticks >= $3), 0), coalesce(sum(net) FILTER (WHERE at_utc_ticks >= $3), 0),
                   count(*), count(*) FILTER (WHERE net > 0), coalesce(sum(net), 0)
            FROM x GROUP BY strategy_id ORDER BY strategy_id LIMIT 100");
        c.Parameters.AddWithValue(todayFrom.AddDays(-30).UtcTicks); c.Parameters.AddWithValue(todayTo.UtcTicks); c.Parameters.AddWithValue(todayFrom.UtcTicks);
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var grossToday = r.GetDecimal(3); var netToday = r.GetDecimal(4);
            list.Add(new V2StrategyPerformance(r.GetString(0), (int)r.GetInt64(1), (int)r.GetInt64(2), grossToday, grossToday - netToday, netToday, (int)r.GetInt64(5), (int)r.GetInt64(6), r.GetDecimal(7)));
        }

        return list;
    }

    async Task<V2MarkSource?> LatestMarksAsync(CancellationToken ct)
    {
        await using var c = _db.CreateCommand("SELECT bar_seq, bar_end_utc_ticks, payload_json FROM pev2.pev2_frames ORDER BY journal_seq DESC LIMIT 1");
        await using var r = await c.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var bar = r.GetInt32(0);
        var at = new DateTimeOffset(r.GetInt64(1), TimeSpan.Zero);
        var bids = new Dictionary<string, (decimal, DateTimeOffset)>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(r.GetString(2));
        if (doc.RootElement.TryGetProperty("quotes", out var quotes))
            foreach (var q in quotes.EnumerateArray())
                bids[q.GetProperty("token").GetString()!] = ((decimal)q.GetProperty("bid").GetDouble(), q.GetProperty("availableAt").GetDateTimeOffset());
        return new V2MarkSource(at, bar, bids);
    }

    async Task<V2EngineHealth> HealthAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        DateTimeOffset? lastAt = null; int? lastBar = null; var frames = 0; var blocked = 0; var gaps = 0; int pending = 0, uncertain = 0, failed = 0;
        await using (var c = _db.CreateCommand("SELECT bar_seq, bar_end_utc_ticks FROM pev2.pev2_frames ORDER BY journal_seq DESC LIMIT 1"))
        await using (var r = await c.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct)) { lastBar = r.GetInt32(0); lastAt = new DateTimeOffset(r.GetInt64(1), TimeSpan.Zero); }

        await using (var c = _db.CreateCommand("SELECT count(*), count(*) FILTER (WHERE NOT d.entries_allowed) FROM pev2.pev2_frames f LEFT JOIN pev2.pev2_dispositions d ON d.session_id = f.session_id AND d.bar_seq = f.bar_seq WHERE f.bar_end_utc_ticks >= $1 AND f.bar_end_utc_ticks < $2"))
        {
            c.Parameters.AddWithValue(from.UtcTicks); c.Parameters.AddWithValue(to.UtcTicks);
            await using var r = await c.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { frames = (int)r.GetInt64(0); blocked = (int)r.GetInt64(1); }
        }

        await using (var c = _db.CreateCommand("SELECT count(*) FROM pev2.pev2_gaps WHERE session_id IN (SELECT DISTINCT session_id FROM pev2.pev2_frames WHERE bar_end_utc_ticks >= $1 AND bar_end_utc_ticks < $2)"))
        {
            c.Parameters.AddWithValue(from.UtcTicks); c.Parameters.AddWithValue(to.UtcTicks);
            gaps = (int)(long)(await c.ExecuteScalarAsync(ct))!;
        }

        await using (var c = _db.CreateCommand("SELECT status, count(*) FROM pev2.pev2_alert_outbox WHERE status IN (0, 2, 3) GROUP BY status"))
        await using (var r = await c.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var n = (int)r.GetInt64(1);
                switch (r.GetInt32(0)) { case 0: pending = n; break; case 2: uncertain = n; break; default: failed = n; break; }
            }

        return new V2EngineHealth(true, [], lastAt, lastBar, frames, blocked, gaps, pending, uncertain, failed);
    }

    public void Dispose() => _db.Dispose();
}
