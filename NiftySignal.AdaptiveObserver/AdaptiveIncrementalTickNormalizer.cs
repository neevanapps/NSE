namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// Per-token incremental counterpart to <see cref="AdaptiveTickCleaner"/>. Feed raw rows in
/// database/source Id order. Duplicate comparison remains per instrument; returned clean ticks carry
/// AvailableAt but the caller is responsible for global (AvailableAt, Id) ordering before engine use.
/// </summary>
public sealed class AdaptiveIncrementalTickNormalizer
{
    readonly Dictionary<string, ObserverRawTick> _previousByToken = new(StringComparer.Ordinal);

    public CleanObserverTick? Process(string token, ObserverRawTick raw)
    {
        if (_previousByToken.TryGetValue(token, out var previous)
            && AdaptiveTickCleaner.SamePayload(previous, raw))
        {
            _previousByToken[token] = raw;
            return null;
        }

        _previousByToken[token] = raw;
        var availableAt = raw.ReceivedAt < raw.ExchangeTimestamp ? raw.ExchangeTimestamp : raw.ReceivedAt;
        return new CleanObserverTick(
            raw.Id,
            raw.ExchangeTimestamp,
            availableAt,
            raw.Last,
            raw.Bid,
            raw.Ask,
            raw.BidQty,
            raw.AskQty,
            raw.Volume,
            raw.OpenInterest);
    }

    public void Reset() => _previousByToken.Clear();
}
