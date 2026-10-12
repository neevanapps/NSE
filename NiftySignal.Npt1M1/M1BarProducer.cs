using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Events;

namespace NiftySignal.Npt1M1;

/// <summary>One finalized M1 bar: the engine event (carrying readiness and the immutable strategy frame), the frame and the frame's content hash.</summary>
public sealed record M1Bar(BarCompletedEvent Event, AdaptiveStrategyFrame Frame, string FrameHash);

/// <summary>All events of one availability instant of the M1 stream: option / future quotes in the engine's group order and the M1 bars the group completed.</summary>
public sealed record M1Group(DateTimeOffset AvailableAt, long Ordinal, IReadOnlyList<TranslatedQuote> Quotes, IReadOnlyList<M1Bar> Bars);

/// <summary>Builds the immutable NPT1 strategy frame of a completed M1 bar from the unchanged adaptive engine's package.</summary>
public static class M1Frames
{
    /// <summary>
    /// The exact metrics of the specification, read from the package of the bar itself (nothing from a neighbouring bar or from adaptive-v1):
    /// m = <c>Microstructure.MicroDevTimeWeighted</c> (null when the bar had no usable book seconds), w = <c>Microstructure.MicroDevUsableSeconds</c> (0 on such a bar, as in the research export), P = <c>OptionBand.Put.ContractStrictDelta</c>,
    /// band centre / expiry = <c>OptionBand.Selection</c>. When the Microstructure sidecar was not produced, m and w are unavailable (null), never zero.
    /// </summary>
    public static AdaptiveStrategyFrame Build(AdaptiveCompletedBarPackage package, BarInfo info, string sessionId, DateOnly tradeDate, IReadOnlyCollection<double>? anchorStrikes = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(info);
        var bar = package.FutureBar;
        var micro = package.Microstructure;
        var band = package.OptionBand;
        return new AdaptiveStrategyFrame(
            sessionId,
            tradeDate,
            bar.BarSeq,
            bar.StartAvailableAtUtc,
            bar.EndAvailableAtUtc,
            info.Ready,
            info.IsValid,
            info.ConsecutiveValidBars,
            M1Identity.ModelVersion,
            info.ReadinessSpec,
            FuturesMicrostructureBar.MetricsVersion,
            micro?.MicroDevTimeWeighted,
            micro?.MicroDevUsableSeconds,
            band.Put?.ContractStrictDelta,
            band.Selection?.CenterStrike,
            band.Selection?.ExpiryDate,
            band.Selection is null ? band.UnavailableReason ?? "BandUnavailable" : null,
            band.Selection is null ? null : string.Join("|", band.Selection.Strikes.Concat(anchorStrikes ?? Array.Empty<double>()).Distinct().Order().Select(x => x.ToString("R", CultureInfo.InvariantCulture))));
    }

    /// <summary>Canonical text of a frame (invariant culture, round-trip doubles); the hash protects the stored row and detects any divergence after a restart.</summary>
    public static string Canonical(AdaptiveStrategyFrame f)
    {
        string D(double? v) => v is null ? "~" : v.Value.ToString("R", CultureInfo.InvariantCulture);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{f.AdaptiveSessionId}|{f.TradeDate:yyyy-MM-dd}|{f.BarSeq}|{f.StartAvailableAt.UtcTicks}|{f.EndAvailableAt.UtcTicks}|{(f.Ready ? 1 : 0)}|{(f.IsValid ? 1 : 0)}|{f.ConsecutiveValidBars}|{f.ModelVersion}|{f.ReadinessSpec}|{f.MicrostructureVersion}|m={D(f.MicroDevTimeWeighted)}|w={D(f.MicroDevUsableSeconds)}|P={(f.PeBandContractStrictDelta is { } p ? p.ToString(CultureInfo.InvariantCulture) : "~")}|c={D(f.BandCenterStrike)}|x={(f.BandExpiry is { } e ? e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "~")}|r={f.BandUnavailableReason ?? "~"}|s={f.CandidateStrikes ?? "~"}");
    }

    public static string Hash(AdaptiveStrategyFrame f) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(f)))).ToLowerInvariant();
}

/// <summary>
/// The M1 bar producer: the unchanged <see cref="AdaptiveEventTranslator"/> (the same admission, normalisation, ordering and
/// <see cref="AdaptiveObserverEngine.ProcessAvailabilityGroup"/> path the research export used) over the M1 session freeze, with every completed bar turned into an immutable
/// <see cref="AdaptiveStrategyFrame"/>. Deterministic and pure: ordered source rows in, finalized frames out; no database, no clock, no broker. Its state is entirely rebuilt by feeding the
/// day's rows again, which is the restart mechanism.
/// </summary>
public sealed class M1BarProducer
{
    private readonly AdaptiveEventTranslator _translator;
    private readonly string _sessionId;
    private readonly DateOnly _tradeDate;
    private readonly IReadOnlyCollection<double> _anchorStrikes;

    public M1BarProducer(M1Freeze freeze, TranslatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(freeze);
        Freeze = freeze;
        _translator = new AdaptiveEventTranslator(freeze.Freeze, options ?? new TranslatorOptions());
        _tradeDate = freeze.Freeze.TradeDate;
        _anchorStrikes = freeze.Freeze.ResidualAnchor is { } anchor
            ? anchor.Calls.Concat(anchor.Puts).Select(c => c.Instrument.Strike).Distinct().ToList()
            : Array.Empty<double>();
        _sessionId = M1Identity.SessionId(_tradeDate);
    }

    public M1Freeze Freeze { get; }

    public string SessionId => _sessionId;

    /// <summary>Readiness label recorded on every bar of this stream.</summary>
    public string ReadinessSpec => _translator.ReadinessSpec;

    public IReadOnlyList<ExactAdaptiveBar> Bars => _translator.FutureBars;

    public TranslatorCounters Counters => _translator.Counters;

    public RowDisposition Offer(SourceTick row) => _translator.Offer(row);

    public void AdvanceSource(DateTimeOffset through) => _translator.AdvanceSource(through);

    public IReadOnlyList<M1Group> Release() => Map(_translator.Release());

    public IReadOnlyList<M1Group> Finish() => Map(_translator.Finish());

    private IReadOnlyList<M1Group> Map(IReadOnlyList<TranslatedGroup> groups)
    {
        var result = new List<M1Group>(groups.Count);
        foreach (var group in groups)
        {
            var bars = new List<M1Bar>(group.Bars.Count);
            foreach (var translated in group.Bars)
            {
                var package = translated.Package ?? throw new InvalidOperationException("The translator did not return the completed-bar package.");
                var info = translated.Event.Info ?? throw new InvalidOperationException("The translated bar carries no readiness information.");
                var frame = M1Frames.Build(package, info, _sessionId, _tradeDate, _anchorStrikes);
                var evt = new BarCompletedEvent(translated.Event.AvailableAt, translated.Event.BarSeq, translated.Event.Token, info, frame);
                bars.Add(new M1Bar(evt, frame, M1Frames.Hash(frame)));
            }

            result.Add(new M1Group(group.AvailableAt, group.Ordinal, group.Quotes, bars));
        }

        return result;
    }
}
