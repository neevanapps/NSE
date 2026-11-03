using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// Appends one JSON line (the same object as one bar of the API) to <c>&lt;root&gt;/agent-feed/yyyy-MM-dd.jsonl</c> for every COMPLETED adaptive bar of today's session.
/// <list type="bullet">
/// <item>Only completed bars: the writer reads up to the runtime's LastCompletedBarSeq; the provisional bar cannot reach it.</item>
/// <item>Idempotent on barSeq per date file: under an exclusive write lock the file's existing barSeq values are read first and only missing bars are appended, so a restart, a reconnect, a rebuilt session or a second
/// Dashboard process never writes a bar twice (the first line written for a bar wins). A torn last line from a crash is left behind a newline and the bar is written again in full.</item>
/// <item>A restart mid-session back-fills the bars the file lacks, in order; it never rewrites or reorders existing lines.</item>
/// <item>The newest completed bar waits <see cref="AgentFeedOptions.SettleSeconds"/> after it is first seen, so the sidecars the Host persists right after a bar completes are in place; older bars are written at once.</item>
/// </list>
/// </summary>
public sealed class AgentFeedWriter(
    AgentSnapshotService snapshot,
    AdaptiveObserverDataService data,
    IOptions<AgentFeedOptions> options,
    ILogger<AgentFeedWriter> logger,
    TimeProvider? clock = null) : BackgroundService
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly Dictionary<(long Session, int Bar), DateTimeOffset> _firstSeen = [];
    long _doneSession;
    int _doneThrough;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.JsonlEnabled)
        {
            logger.LogInformation("Agent feed JSONL writer is disabled (AgentFeed:JsonlEnabled=false).");
            return;
        }

        var poll = TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds));
        logger.LogInformation("Agent feed JSONL writer started: {Directory}", options.Value.FeedDirectory());
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WriteNewBarsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed poll (database restarting, a file briefly locked) is retried on the next tick; it never takes the Dashboard down.
                logger.LogWarning(ex, "Agent feed poll failed; retrying.");
            }

            try
            {
                await Task.Delay(poll, _clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One pass: appends every completed bar of today's session that the date file does not have yet. Returns the number of lines appended.</summary>
    public async Task<int> WriteNewBarsAsync(CancellationToken ct = default)
    {
        var header = await data.LoadHeaderAsync(ct);
        if (header is null || header.Runtime.LastCompletedBarSeq <= 0)
        {
            return 0;
        }

        var session = header.Session;
        var last = header.Runtime.LastCompletedBarSeq;
        if (_doneSession == session.Id && _doneThrough >= last)
        {
            return 0;
        }

        var now = _clock.GetUtcNow();
        if (!_firstSeen.ContainsKey((session.Id, last)))
        {
            _firstSeen[(session.Id, last)] = now;
        }

        // The newest bar waits for its settle time; everything before it is already at least one bar old.
        var settled = now - _firstSeen[(session.Id, last)] >= TimeSpan.FromSeconds(Math.Max(0, options.Value.SettleSeconds));
        var through = settled ? last : last - 1;
        if (through <= 0)
        {
            return 0;
        }

        var path = options.Value.FilePath(session.TradeDate);
        var written = ReadWrittenBarSeqs(path);
        var missing = Enumerable.Range(1, through).Where(s => !written.Contains(s)).ToList();
        var appended = 0;
        if (missing.Count > 0)
        {
            var built = await snapshot.BuildBarsAsync(session, last, missing, ct);
            appended = Append(path, built);
        }

        _doneSession = session.Id;
        _doneThrough = through;
        foreach (var key in _firstSeen.Keys.Where(k => k.Session != session.Id || k.Bar < through).ToList())
        {
            _firstSeen.Remove(key);
        }

        return appended;
    }

    /// <summary>The barSeq of every complete JSON line of the file (a torn or unreadable line is ignored). A missing file has none.</summary>
    internal static HashSet<int> ReadWrittenBarSeqs(string path)
    {
        var seqs = new HashSet<int>();
        if (!File.Exists(path))
        {
            return seqs;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        ParseInto(stream, seqs);
        return seqs;
    }

    static void ParseInto(Stream stream, HashSet<int> seqs)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("barSeq", out var seq) && seq.TryGetInt32(out var value))
                {
                    seqs.Add(value);
                }
            }
            catch (JsonException)
            {
                // torn line from an interrupted write: not a bar
            }
        }
    }

    /// <summary>Appends the bars the file lacks, oldest first, under an exclusive write lock; the existing barSeq values are re-read inside the lock so two writers cannot both append the same bar.</summary>
    static int Append(string path, IReadOnlyList<AgentBar> bars)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        var existing = new HashSet<int>();
        ParseInto(stream, existing);
        stream.Seek(0, SeekOrigin.End);
        var text = new StringBuilder();
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n')
            {
                text.Append('\n');                                  // keep a torn last line on its own line
            }

            stream.Seek(0, SeekOrigin.End);
        }

        var count = 0;
        foreach (var bar in bars.Where(b => b.IsCompleted).OrderBy(b => b.BarSeq))
        {
            if (!existing.Add(bar.BarSeq))
            {
                continue;
            }

            text.Append(JsonSerializer.Serialize(bar, AgentFeedJson.Options)).Append('\n');
            count++;
        }

        if (count == 0)
        {
            return 0;
        }

        var bytes = new UTF8Encoding(false).GetBytes(text.ToString());
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);
        return count;
    }
}
