using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Trading.Signals;

/// <summary>
/// PLUMBING STRATEGY -- NO TRADING EDGE IS CLAIMED OR IMPLIED. It exists only to drive the engine's episode, opportunity, selection and
/// entry-condition machinery deterministically in tests and replays. Validity is a pure function of the bar sequence number: an
/// episode starts at every bar whose sequence is a multiple of <c>Period</c>, stays valid for <c>ValidBars</c> bars, and the side
/// alternates Call, Put, Call, ... per episode. Stateless, so one instance can serve several variants.
/// </summary>
public sealed class PlumbingEveryNthBarStrategy : IBaseStrategy
{
    private readonly int _period;
    private readonly int _validBars;

    public PlumbingEveryNthBarStrategy(string strategyId, int period, int validBars)
    {
        ArgumentException.ThrowIfNullOrEmpty(strategyId);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(validBars, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(validBars, period);
        StrategyId = strategyId;
        _period = period;
        _validBars = validBars;
    }

    public string StrategyId { get; }

    public string Version => "plumbing-v1";

    public StrategyValidity OnBar(BarCompletedEvent bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        if (bar.BarSeq < 1)
        {
            return new StrategyValidity(false, OptionSide.Call, "BarSeqBelowOne");
        }

        var phase = bar.BarSeq % _period;
        var episodeIndex = bar.BarSeq / _period;
        var side = episodeIndex % 2 == 0 ? OptionSide.Call : OptionSide.Put;
        return phase < _validBars
            ? new StrategyValidity(true, side, "PlumbingWindow")
            : new StrategyValidity(false, side, "PlumbingOutsideWindow");
    }
}
