using NiftySignal.Domain.Enums;

namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// One option contract's Top-5 observation for one completed bar (options-depth5-v1, S5). The contract identity (token, expiry, strike, side) is carried with the values so a consumer never has to re-derive it;
/// <see cref="Depth"/> has exactly the Futures definition (same OFI5 / MicroDev5Proxy / notional DepthN5 formulas, same hard 5 s freshness, same D10 conditional bridging) computed from THIS token's own events only.
/// </summary>
public sealed record OptionDepth5Bar(
    string Token,
    string TradingSymbol,
    DateOnly Expiry,
    double Strike,
    OptionType Side,
    FuturesDepth5Bar Depth)
{
    /// <summary>options-depth5-v1: per-token application of the futures-depth5-v1 rules to every contract of the frozen session universe. A different rule is a new version.</summary>
    public const string MetricsVersion = "options-depth5-v1";
}

/// <summary>
/// Independent Top-5 state per option token of the frozen session universe. Each token owns a <see cref="FuturesDepth5Tracker"/> (a pure function of that token's delivered events and the bar boundaries),
/// so no token's observations, ordering, freshness clock, OFI baseline or invalid/gap state can influence another's. Events of a token outside the frozen universe are ignored and counted.
/// Memory is bounded: each tracker prunes everything a later bar cannot need at every <see cref="Complete"/>.
/// </summary>
public sealed class OptionsDepth5Tracker
{
    readonly IReadOnlyList<ObserverOptionInstrument> _ordered;
    readonly Dictionary<string, FuturesDepth5Tracker> _trackers;

    public OptionsDepth5Tracker(IReadOnlyList<ObserverOptionInstrument> universe)
    {
        // A stable order (token, ordinal) independent of the universe's own ordering, so rows and digests do not depend on how the universe list was assembled.
        _ordered = universe.GroupBy(x => x.Token, StringComparer.Ordinal).Select(g => g.First()).OrderBy(x => x.Token, StringComparer.Ordinal).ToArray();
        _trackers = _ordered.ToDictionary(x => x.Token, _ => new FuturesDepth5Tracker(), StringComparer.Ordinal);
    }

    public int TokenCount => _ordered.Count;

    /// <summary>Events for tokens that are not in the frozen universe (diagnostic).</summary>
    public int UnknownTokenEvents { get; private set; }

    public int LateEvents => _trackers.Values.Sum(x => x.LateEvents);

    public int RepeatsIgnored => _trackers.Values.Sum(x => x.RepeatsIgnored);

    public int RetainedEvents => _trackers.Values.Sum(x => x.RetainedEvents);

    public bool Contains(string token) => _trackers.ContainsKey(token);

    public void Observe(Depth5Observation observation)
    {
        if (_trackers.TryGetValue(observation.Token, out var tracker)) tracker.Observe(observation);
        else UnknownTokenEvents++;
    }

    /// <summary>One entry per universe contract (token order), each computed from that token's own events only; a contract that never produced an event is fully "unobserved", never zero-filled.</summary>
    public IReadOnlyList<OptionDepth5Bar> Complete(DateTimeOffset start, DateTimeOffset end)
    {
        var rows = new List<OptionDepth5Bar>(_ordered.Count);
        foreach (var o in _ordered)
            rows.Add(new OptionDepth5Bar(o.Token, o.TradingSymbol, o.ExpiryDate, o.Strike, o.OptionType, _trackers[o.Token].Complete(start, end)));
        return rows;
    }
}
