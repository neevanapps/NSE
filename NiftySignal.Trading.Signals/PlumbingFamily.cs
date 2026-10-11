using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Strategies;

namespace NiftySignal.Trading.Signals;

/// <summary>One scripted validity window of a plumbing variant: it says Enter (in the given direction) on every bar with BarSeq in [FromBarSeq, ToBarSeq].</summary>
public sealed record PlumbingWindow(long FromBarSeq, long ToBarSeq, StrategyDirection Direction)
{
    public bool Contains(long barSeq) => barSeq >= FromBarSeq && barSeq <= ToBarSeq;
}

/// <summary>The script of one plumbing variant: its entry windows and the bars on which it asks for its own position to be closed.</summary>
public sealed record PlumbingVariantScript(IReadOnlyList<PlumbingWindow> Windows, IReadOnlyCollection<long>? ExitBarSeqs = null);

/// <summary>
/// TEST / PLUMBING FAMILY - NO TRADING EDGE IS CLAIMED OR IMPLIED. It exists only to drive the strategy-family, variant, capacity, risk and
/// execution machinery deterministically in tests and replays. Each variant follows a fixed script that is a pure function of the bar sequence
/// number: Enter inside a scripted window, Exit on scripted bars, Wait otherwise. It holds no state, reads no market data and must never be
/// enabled for normal paper trading (<see cref="IsTestOnly"/>).
/// </summary>
public sealed class PlumbingFamily : IStrategyFamily
{
    /// <summary>Marker read by hosts: a family with this set must stay disabled unless plumbing is explicitly switched on.</summary>
    public const bool IsTestOnly = true;

    private readonly IReadOnlyDictionary<string, PlumbingVariantScript> _scripts;
    private readonly string[] _variantIds;

    public PlumbingFamily(string strategyId, IReadOnlyDictionary<string, PlumbingVariantScript> scripts)
    {
        ArgumentException.ThrowIfNullOrEmpty(strategyId);
        ArgumentNullException.ThrowIfNull(scripts);
        StrategyId = strategyId;
        _scripts = scripts;
        _variantIds = scripts.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    public string StrategyId { get; }

    public string Version => "plumbing-family-v1";

    public IReadOnlyCollection<string> VariantIds => _variantIds;

    public FamilyDecisions OnBar(BarCompletedEvent bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        var decisions = new List<VariantDecision>(_variantIds.Length);
        foreach (var id in _variantIds)
        {
            var script = _scripts[id];
            if (script.ExitBarSeqs is { } exits && exits.Contains(bar.BarSeq))
            {
                decisions.Add(VariantDecision.Exit(id, "PlumbingExit"));
                continue;
            }

            var window = script.Windows.FirstOrDefault(w => w.Contains(bar.BarSeq));
            decisions.Add(window is null
                ? VariantDecision.Wait(id, "PlumbingOutsideWindow")
                : VariantDecision.Enter(id, window.Direction, "PlumbingWindow"));
        }

        return new FamilyDecisions(decisions);
    }
}
