using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.State;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Trading.Core.Strategies;

/// <summary>
/// Compatibility adapter: presents a single-case <see cref="IBaseStrategy"/> (one validity + side per bar) as a family whose variants all
/// receive that SAME decision. It is the only place where the legacy OptionSide appears next to the family contract (Call = Long, Put = Short),
/// and it exists so that the one canonical family path serves old single-case strategies unchanged. The engine never sees the legacy type.
/// </summary>
public sealed class LegacyBaseStrategyFamily : IStrategyFamily, IStateContributor
{
    private readonly IBaseStrategy _inner;
    private readonly string[] _variantIds;

    public LegacyBaseStrategyFamily(IBaseStrategy inner, IEnumerable<string> variantIds)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(variantIds);
        _inner = inner;
        _variantIds = variantIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    public string StrategyId => _inner.StrategyId;

    public string Version => _inner.Version;

    public IReadOnlyCollection<string> VariantIds => _variantIds;

    public FamilyDecisions OnBar(BarCompletedEvent bar)
    {
        var validity = _inner.OnBar(bar);
        var direction = validity.Side == OptionSide.Call ? StrategyDirection.Long : StrategyDirection.Short;
        return new FamilyDecisions(_variantIds
            .Select(id => validity.IsValid
                ? VariantDecision.Enter(id, direction, validity.Reason)
                : VariantDecision.Wait(id, validity.Reason))
            .ToList());
    }

    public void WriteState(StateWriter w)
    {
        if (_inner is IStateContributor contributor)
        {
            contributor.WriteState(w);
        }
    }
}

/// <summary>Builds the canonical family dictionary from legacy single-case strategies, using the variants the configuration assigns to each StrategyId.</summary>
public static class LegacyStrategies
{
    public static IReadOnlyDictionary<string, IStrategyFamily> AsFamilies(Config.TradingConfig config, IReadOnlyDictionary<string, IBaseStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(strategies);
        var families = new Dictionary<string, IStrategyFamily>(StringComparer.Ordinal);
        foreach (var (strategyId, strategy) in strategies)
        {
            var variantIds = config.Variants.Values.Where(v => string.Equals(v.StrategyId, strategyId, StringComparison.Ordinal)).Select(v => v.VariantId);
            families.Add(strategyId, new LegacyBaseStrategyFamily(strategy, variantIds));
        }

        return families;
    }
}
