namespace NiftySignal.Features;

/// <summary>
/// The tracked future's own cumulative volume/VWAP state, accumulated per real tick (not per
/// cadence) -- the same "diff cumulative day volume against the previous tick, floored at 0
/// against a feed reset" pattern LiveFeatureEngine.OnTick already uses for this exact purpose.
/// One running baseline feeds both the day-long VWAP accumulation and the per-cadence volume
/// delta, so the two can never silently disagree with each other.
///
/// 2026-09-17, moved here from `NiftySignal.BacktestData/CadencePopulator.cs` (unchanged logic,
/// same move already made for <see cref="FutureCvdProxyAccumulator"/>) so a volume-bar
/// aggregation pass (`NiftySignal.VolumeBarData`) can reuse the exact same VWAP/volume-delta
/// bookkeeping instead of a hand-copied duplicate -- <see cref="ResetCadence"/> is called on
/// whatever boundary the caller defines (a 15s cadence for the existing pipeline, a volume
/// threshold for the new one); <see cref="Vwap"/> itself is never reset, since it's a
/// whole-session running value by design.
/// </summary>
public sealed class FutureFlowAccumulator
{
    long? _previousVolume;
    double _cumulativePriceVolume;
    double _cumulativeVolume;

    public long? LatestCumulativeVolume { get; private set; }

    public long CadenceVolumeDelta { get; private set; }

    /// <summary>This tick's own volume delta (not the cadence total) -- exposed so a caller (the CVD proxy accumulator) classifies the exact same delta VWAP just weighted, rather than re-deriving its own, potentially disagreeing, volume baseline.</summary>
    public long LastVolumeDelta { get; private set; }

    public double? Vwap => _cumulativeVolume > 0 ? _cumulativePriceVolume / _cumulativeVolume : null;

    public void ApplyTick(decimal price, long volume)
    {
        var delta = _previousVolume is { } previous ? Math.Max(0, volume - previous) : 0;
        _previousVolume = volume;
        LatestCumulativeVolume = volume;
        LastVolumeDelta = delta;

        _cumulativePriceVolume += (double)price * delta;
        _cumulativeVolume += delta;
        CadenceVolumeDelta += delta;
    }

    public void ResetCadence() => CadenceVolumeDelta = 0;
}
