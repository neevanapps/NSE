using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserver.Commentary;
using NiftySignal.AdaptiveObserverData;

namespace NiftySignal.Host;

/// <summary>
/// Builds the immutable <see cref="CommentaryFrame"/> for a completed bar from PERSISTED rows only (core rows plus current-version sidecars).
/// Live processing and historical replay therefore see identical inputs. No wall clock and no live quote is read, so a later tick can never
/// reinterpret an earlier bar (08-Oct plan sections 45-46).
/// </summary>
public sealed class AdaptiveCommentaryFrameLoader
{
    public async Task<CommentaryFrame?> LoadAsync(AdaptiveObserverDbContext db, AdaptiveSessionStateRow session, int barSeq, CancellationToken ct)
    {
        var bar = await db.FutureBars.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == session.Id && x.BarSeq == barSeq, ct);
        if (bar is null) return null;

        var rolling = await db.RollingStates.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == session.Id && x.EndBarSeq == barSeq, ct);
        var bands = await db.OptionBandBars.AsNoTracking().Where(x => x.SessionId == session.Id && x.BarSeq == barSeq).ToListAsync(ct);
        var futuresVersion = FuturesMicrostructureBar.MetricsVersion;
        var optionsVersion = OptionsSupplementalBar.MetricsVersion;
        var futuresSupplemental = await db.FuturesSupplemental.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SessionId == session.Id && x.BarSeq == barSeq && x.MetricsVersion == futuresVersion, ct);
        var optionsSupplemental = await db.OptionsSupplemental.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SessionId == session.Id && x.BarSeq == barSeq && x.MetricsVersion == optionsVersion, ct);

        // A sidecar bar that failed its replay verification is known-invalid (durable marker): commentary treats it exactly like a missing
        // sidecar (unavailable evidence), never as trustworthy input. The stored row is left untouched.
        var invalid = await db.ProjectionHealth.AsNoTracking()
            .Where(x => x.SessionId == session.Id && x.BarSeq == barSeq
                && ((x.Component == AdaptiveProjectionComponents.FuturesSupplemental && x.Version == futuresVersion)
                    || (x.Component == AdaptiveProjectionComponents.OptionsSupplemental && x.Version == optionsVersion)))
            .Select(x => x.Component).ToListAsync(ct);
        if (invalid.Contains(AdaptiveProjectionComponents.FuturesSupplemental)) futuresSupplemental = null;
        if (invalid.Contains(AdaptiveProjectionComponents.OptionsSupplemental)) optionsSupplemental = null;

        // Primary residual view for commentary is ATM±2 (plan section 69). The previous bar is needed for adjacent-bar deltas.
        var residuals = await db.OptionResidualBars.AsNoTracking()
            .Where(x => x.SessionId == session.Id && x.Variant == ResidualVariant.AtmPlusMinus2 && (x.BarSeq == barSeq || x.BarSeq == barSeq - 1))
            .ToListAsync(ct);
        var anchors = await db.ResidualAnchorComponents.AsNoTracking().Where(x => x.SessionId == session.Id).ToListAsync(ct);

        var recent = await db.FutureBars.AsNoTracking()
            .Where(x => x.SessionId == session.Id && x.BarSeq <= barSeq && x.BarSeq > barSeq - AdaptiveReadinessPolicy.RequiredBars)
            .ToListAsync(ct);
        var validBars = AdaptiveReadinessPolicy.CountValidSuffix(recent.Select(x => (x.BarSeq,
            AdaptiveReadinessPolicy.IsValid(x.Volume, session.BaseBarVolume, x.Open, x.High, x.Low, x.Close, x.TradeUpdates,
                x.StartAvailableAtUtc, x.EndAvailableAtUtc))), barSeq);

        return AdaptiveCommentaryFrameBuilder.Build(session, bar, rolling, bands, futuresSupplemental, optionsSupplemental, residuals, anchors,
            validBars >= AdaptiveReadinessPolicy.RequiredBars);
    }
}

/// <summary>Pure mapping from persisted rows to a frame (separate from the loader so it is unit-testable without a database).</summary>
public static class AdaptiveCommentaryFrameBuilder
{
    public static CommentaryFrame Build(
        AdaptiveSessionStateRow session,
        AdaptiveFutureBarRow bar,
        AdaptiveRollingStateRow? rolling,
        IReadOnlyList<AdaptiveOptionBandBarRow> bands,
        AdaptiveFuturesSupplementalRow? futuresSupplemental,
        AdaptiveOptionsSupplementalRow? optionsSupplemental,
        IReadOnlyList<AdaptiveOptionResidualBarRow> residualCurrentAndPrevious,
        IReadOnlyList<AdaptiveResidualAnchorComponentRow> anchors,
        bool readinessMet)
    {
        var call = bands.FirstOrDefault(x => x.Side == NiftySignal.Domain.Enums.OptionType.Call);
        var put = bands.FirstOrDefault(x => x.Side == NiftySignal.Domain.Enums.OptionType.Put);
        var bandUsable = call is { BandAvailable: true } && put is { BandAvailable: true };

        var current = residualCurrentAndPrevious.FirstOrDefault(x => x.BarSeq == bar.BarSeq);
        ResidualDerivedPoint? derived = null;
        if (current is { IsAvailable: true })
        {
            var bases = new ResidualBases(
                anchors.Where(x => x.Side == NiftySignal.Domain.Enums.OptionType.Call).Sum(x => x.Price0930),
                anchors.Where(x => x.Side == NiftySignal.Domain.Enums.OptionType.Put).Sum(x => x.Price0930));
            derived = AdaptiveResidualProjection.Derive(residualCurrentAndPrevious.Select(x => new ResidualReadingPoint(
                x.BarSeq, x.IsAvailable, x.CEResidual, x.PEResidual, x.CEResidualPct, x.PEResidualPct, x.DirectionalResidualPct)), bases)
                .FirstOrDefault(x => x.BarSeq == bar.BarSeq);
        }

        return new CommentaryFrame
        {
            SessionId = session.Id,
            TradeDate = session.TradeDate,
            BarSeq = bar.BarSeq,
            BarEndAvailableAtUtc = bar.EndAvailableAtUtc,
            ReadinessMet = readinessMet,

            BarPriceDisplacement = bar.BarPriceDisplacement,
            RollingPriceDisplacement = rolling?.PriceDisplacement,
            StrictDelta = bar.StrictDelta,
            EnrichedDelta = bar.EnrichedDelta,
            RollingStrictDelta = rolling?.StrictDelta,
            RollingEnrichedDelta = rolling?.EnrichedDelta,
            RollingStrictAbsDeltaChange = rolling?.RollingStrictAbsDeltaChange,
            RollOiDelta = rolling?.OiChange,
            Urgency = AdaptiveMetricMath.Urgency(bar.Volume, bar.DurationSeconds),
            MicroDev = futuresSupplemental?.MicroDevTimeWeighted,
            Ofi = futuresSupplemental?.Ofi,
            // Basis stays unavailable until the owner approves a spot-freshness rule (plan section 9.6). Deliberately not wired.
            DeltaBasis = null,
            RollingEfficiency = rolling?.Efficiency,
            Evolution = rolling?.StrictDominanceEvolution,
            State = rolling?.State.ToString(),

            CenterStrike = optionsSupplemental?.CenterStrike,
            CePosition = optionsSupplemental?.CePosition,
            PePosition = optionsSupplemental?.PePosition,
            CeStrictDelta = bandUsable ? call!.ContractStrictDelta : null,
            PeStrictDelta = bandUsable ? put!.ContractStrictDelta : null,
            CeDeltaIv = optionsSupplemental?.CeDeltaIv,
            PeDeltaIv = optionsSupplemental?.PeDeltaIv,
            IvSkew = optionsSupplemental?.IvSkewEnd,
            VolPcr = optionsSupplemental?.VolPcr,
            RollVolPcr = optionsSupplemental?.RollVolPcr,

            ResidualAvailable = current is { IsAvailable: true },
            DirectionalResidualPct = current is { IsAvailable: true } ? current.DirectionalResidualPct : null,
            AdjacentDirectionalResidualDelta = derived?.AdjacentDirectionalResidualDelta,
            CeResidualDeltaPct = derived?.CeResidualDeltaPct,
            PeResidualDeltaPct = derived?.PeResidualDeltaPct,
            StraddleResidualPct = derived?.StraddleResidualPct,
        };
    }
}

/// <summary>JSON used for the structured evidence columns (never prose only).</summary>
public static class AdaptiveCommentaryJson
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static string Items(IReadOnlyList<EvidenceItem> items) => JsonSerializer.Serialize(items, Options);

    public static string Notes(IReadOnlyList<string> notes) => JsonSerializer.Serialize(notes, Options);
}
