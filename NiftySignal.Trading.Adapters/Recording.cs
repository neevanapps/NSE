using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NiftySignal.Trading.Adapters;

/// <summary>A running digest point of one channel (contract section A.16.6): after <see cref="Count"/> lines the cumulative SHA-256 was <see cref="CumulativeHash"/>.</summary>
public sealed record ChannelCheckpoint(long Count, string CumulativeHash, string LastKey);

public sealed record ChannelDigest(string Name, long Count, string Hash, IReadOnlyList<ChannelCheckpoint> Checkpoints);

public sealed record CapturedLine(long Index, string Key, string Line);

/// <summary>Recording settings of a run. <see cref="CaptureAll"/> keeps every line (tests); a capture window keeps only the lines of a bucket (the run-diff second pass).</summary>
public sealed record RecorderOptions
{
    public long CheckpointEvery { get; init; } = 1000;

    public bool CaptureAll { get; init; }

    /// <summary>Per channel: capture lines with index in [From, To] (zero-based, inclusive).</summary>
    public IReadOnlyDictionary<string, (long From, long To)> CaptureWindows { get; init; } = new Dictionary<string, (long From, long To)>();
}

/// <summary>
/// One ordered, hashed stream of canonical text lines. Memory is O(checkpoints), not O(lines), unless capture is requested for the line index
/// (all lines in tests, a window in the second pass of a divergence search).
/// </summary>
public sealed class ChannelRecorder
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly List<ChannelCheckpoint> _checkpoints = new();
    private readonly List<CapturedLine> _captured = new();
    private readonly long _every;
    private readonly bool _all;
    private readonly (long From, long To)? _window;
    private long _count;

    public ChannelRecorder(string name, RecorderOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(options);
        Name = name;
        _every = Math.Max(1, options.CheckpointEvery);
        _all = options.CaptureAll;
        _window = options.CaptureWindows.TryGetValue(name, out var w) ? w : null;
    }

    public string Name { get; }

    public long Count => _count;

    public IReadOnlyList<CapturedLine> Captured => _captured;

    public void Add(string key, string line)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(line);
        _hash.AppendData(Encoding.UTF8.GetBytes(line));
        _hash.AppendData("\n"u8);
        if (_all || (_window is { } w && _count >= w.From && _count <= w.To))
        {
            _captured.Add(new CapturedLine(_count, key, line));
        }

        _count++;
        if (_count % _every == 0)
        {
            _checkpoints.Add(new ChannelCheckpoint(_count, Convert.ToHexString(_hash.GetCurrentHash()), key));
        }
    }

    public ChannelDigest Digest() => new(Name, _count, Convert.ToHexString(_hash.GetCurrentHash()), _checkpoints.ToList());
}

/// <summary>The named channels of one run. Channels are created on first use, in a fixed vocabulary (see <see cref="Names"/>).</summary>
public sealed class RunRecorders
{
    public static class Names
    {
        public const string Quotes = "quotes";
        public const string Bars = "bars";
        public const string Readiness = "readiness";
        public const string Candles = "candles";
        public const string Opportunities = "opportunities";
        public const string Outputs = "outputs";
        public const string RowQuarantine = "rowQuarantine";
        public const string Audit = "audit";
        public const string Summaries = "summaries";

        public static readonly IReadOnlyList<string> All = new[] { Quotes, Bars, Readiness, Candles, Opportunities, Outputs, RowQuarantine, Audit, Summaries };
    }

    private readonly Dictionary<string, ChannelRecorder> _channels = new(StringComparer.Ordinal);

    public RunRecorders(RecorderOptions? options = null)
    {
        Options = options ?? new RecorderOptions();
        foreach (var name in Names.All)
        {
            _channels[name] = new ChannelRecorder(name, Options);
        }
    }

    public RecorderOptions Options { get; }

    public ChannelRecorder this[string name] => _channels[name];

    public IReadOnlyDictionary<string, ChannelDigest> Digests() => _channels.ToDictionary(c => c.Key, c => c.Value.Digest(), StringComparer.Ordinal);
}

/// <summary>Canonical text of values: System.Text.Json is culture-invariant, enums are names, timestamps are ISO-8601 with the UTC offset.</summary>
public static class Canonical
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static string Json(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, value.GetType(), Options);
    }

    public static string Typed(object value) => value.GetType().Name + ":" + Json(value);

    public static string Time(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture);
}
