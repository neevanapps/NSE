using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Host;

public sealed record AdaptivePersistResult(bool Inserted, bool VerifiedExisting, int BarSeq);

public sealed class AdaptiveObserverPersistence(ILogger<AdaptiveObserverPersistence> logger)
{
    const double DoubleTolerance = 1e-9;

    public async Task<AdaptivePersistResult> PersistOrVerifyAsync(
        AdaptiveObserverDbContext db,
        AdaptiveSessionStateRow session,
        AdaptiveCompletedBarPackage package,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var existing = await db.FutureBars
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.SessionId == session.Id && x.BarSeq == package.FutureBar.BarSeq, ct);

        if (existing is not null)
        {
            VerifyFuture(existing, package.FutureBar);
            await VerifyDependentRowsAsync(db, session, package, ct);
            logger.LogDebug("Adaptive replay verified session={SessionId}, bar={BarSeq}", session.Id, package.FutureBar.BarSeq);
            return new AdaptivePersistResult(false, true, package.FutureBar.BarSeq);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        db.FutureBars.Add(MapFuture(session.Id, package.FutureBar));
        if (package.FlowState.Rolling is not null)
        {
            db.RollingStates.Add(MapRolling(session.Id, package.FlowState));
        }

        db.OptionBandBars.Add(MapOptionBand(session.Id, package, OptionType.Call));
        db.OptionBandBars.Add(MapOptionBand(session.Id, package, OptionType.Put));

        foreach (var residual in package.Residuals)
        {
            db.OptionResidualBars.Add(MapResidual(session, package.FutureBar.BarSeq, package.FutureBar.EndAvailableAtUtc, residual));
        }

        var runtime = await db.Runtime.SingleAsync(x => x.SessionId == session.Id, ct);
        runtime.LastHeartbeatUtc = nowUtc;
        runtime.RuntimeStatus = AdaptiveRuntimeStatus.Live;
        runtime.LastCompletedBarSeq = package.FutureBar.BarSeq;
        runtime.CurrentPartialBarVolume = 0;
        runtime.CurrentPartialBarStartedAtUtc = package.FutureBar.EndAvailableAtUtc;

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new AdaptivePersistResult(true, false, package.FutureBar.BarSeq);
    }

    public async Task UpdatePartialRuntimeAsync(
        AdaptiveObserverDbContext db,
        long sessionId,
        AdaptivePartialBarStatus partial,
        DateTimeOffset heartbeatUtc,
        DateTimeOffset? lastSourceAvailableAt,
        long? lastSourceTickId,
        CancellationToken ct)
    {
        var runtime = await db.Runtime.SingleAsync(x => x.SessionId == sessionId, ct);
        runtime.LastHeartbeatUtc = heartbeatUtc;
        runtime.LastProcessedSourceAvailableAtUtc = lastSourceAvailableAt;
        runtime.LastProcessedSourceTickId = lastSourceTickId;
        runtime.CurrentPartialBarVolume = partial.AccumulatedVolume;
        runtime.CurrentPartialBarStartedAtUtc = partial.StartedAtUtc;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetRuntimeStatusAsync(
        AdaptiveObserverDbContext db,
        long sessionId,
        AdaptiveRuntimeStatus status,
        DateTimeOffset nowUtc,
        string? error,
        CancellationToken ct)
    {
        var runtime = await db.Runtime.SingleAsync(x => x.SessionId == sessionId, ct);
        runtime.RuntimeStatus = status;
        runtime.LastHeartbeatUtc = nowUtc;
        runtime.LastError = error;
        if (status == AdaptiveRuntimeStatus.Rebuilding)
        {
            runtime.LastRecoveryStartedUtc = nowUtc;
        }
        if (status == AdaptiveRuntimeStatus.Live)
        {
            runtime.LastRecoveryCompletedUtc = nowUtc;
        }
        await db.SaveChangesAsync(ct);
    }

    static AdaptiveFutureBarRow MapFuture(long sessionId, ExactAdaptiveBar b) => new()
    {
        SessionId = sessionId,
        BarSeq = b.BarSeq,
        StartAvailableAtUtc = b.StartAvailableAtUtc,
        EndAvailableAtUtc = b.EndAvailableAtUtc,
        FirstSourceTickId = b.FirstSourceTickId,
        LastSourceTickId = b.LastSourceTickId,
        DurationSeconds = b.DurationSeconds,
        Open = b.Open,
        High = b.High,
        Low = b.Low,
        Close = b.Close,
        BarPriceDisplacement = b.BarPriceDisplacement,
        Vwap = b.Vwap,
        Volume = b.Volume,
        TradeUpdates = b.TradeUpdates,
        StrictBuyVolume = b.StrictBuyVolume,
        StrictSellVolume = b.StrictSellVolume,
        StrictUnknownVolume = b.StrictUnknownVolume,
        StrictDelta = b.StrictDelta,
        StrictDeltaRatioTotal = b.StrictDeltaRatioTotal,
        StrictCoverage = b.StrictCoverage,
        EnrichedBuyVolume = b.EnrichedBuyVolume,
        EnrichedSellVolume = b.EnrichedSellVolume,
        EnrichedUnknownVolume = b.EnrichedUnknownVolume,
        EnrichedDelta = b.EnrichedDelta,
        EnrichedDeltaRatio = b.EnrichedDeltaRatio,
        OiOpen = b.OiOpen,
        OiClose = b.OiClose,
        OiChange = b.OiChange,
        Bid = b.Bid,
        Ask = b.Ask,
        BidQty = b.BidQty,
        AskQty = b.AskQty,
        Spread = b.Spread,
        BookImbalance = b.BookImbalance,
        Microprice = b.Microprice,
    };

    static AdaptiveRollingStateRow MapRolling(long sessionId, AdaptiveFlowState flow)
    {
        var s = flow.Rolling ?? throw new ArgumentException("Rolling state is required.", nameof(flow));
        return new AdaptiveRollingStateRow
        {
            SessionId = sessionId,
            StartBarSeq = s.StartBarSeq,
            EndBarSeq = s.EndBarSeq,
            StartAvailableAtUtc = s.StartAvailableAtUtc,
            EndAvailableAtUtc = s.EndAvailableAtUtc,
            WindowBars = s.WindowBars,
            WindowVolume = s.WindowVolume,
            ElapsedSeconds = s.ElapsedSeconds,
            StartPrice = s.StartPrice,
            EndPrice = s.EndPrice,
            High = s.High,
            Low = s.Low,
            PriceDisplacement = s.PriceDisplacement,
            ReturnBps = s.ReturnBps,
            PathLength = s.PathLength,
            Efficiency = s.Efficiency,
            SignedEfficiency = s.SignedEfficiency,
            StrictBuyVolume = s.StrictBuyVolume,
            StrictSellVolume = s.StrictSellVolume,
            StrictUnknownVolume = s.StrictUnknownVolume,
            StrictDelta = s.StrictDelta,
            StrictQuoteCoverage = s.StrictQuoteCoverage,
            StrictDeltaRatioTotal = s.StrictDeltaRatioTotal,
            StrictDeltaRatioClassified = s.StrictDeltaRatioClassified,
            EnrichedBuyVolume = s.EnrichedBuyVolume,
            EnrichedSellVolume = s.EnrichedSellVolume,
            EnrichedUnknownVolume = s.EnrichedUnknownVolume,
            EnrichedDelta = s.EnrichedDelta,
            EnrichedDeltaRatio = s.EnrichedDeltaRatio,
            FallbackShare = s.FallbackShare,
            OiStart = s.OiStart,
            OiEnd = s.OiEnd,
            OiChange = s.OiChange,
            OiChangePct = s.OiChangePct,
            VolumePerSecond = s.VolumePerSecond,
            TradeUpdates = s.TradeUpdates,
            WindowRange = s.WindowRange,
            PriceDirection = s.PriceDirection,
            StrictDeltaDirection = s.StrictDeltaDirection,
            EnrichedDeltaDirection = s.EnrichedDeltaDirection,
            PriceStrictDeltaAgree = s.PriceStrictDeltaAgree,
            PriceEnrichedDeltaAgree = s.PriceEnrichedDeltaAgree,
            RollingStrictDeltaChange = flow.RollingStrictDeltaChange,
            RollingStrictAbsDeltaChange = flow.RollingStrictAbsDeltaChange,
            RollingEnrichedDeltaChange = flow.RollingEnrichedDeltaChange,
            RollingOiChangePctChange = flow.RollingOiChangePctChange,
            StrictDominanceEvolution = flow.StrictDominanceEvolution,
            IsStrong = flow.IsStrong,
            WeakeningSequence = flow.WeakeningSequence,
            StrongBaseBarSeq = flow.StrongBaseBarSeq,
            Weak1BarSeq = flow.Weak1BarSeq,
            State = flow.State,
        };
    }

    static AdaptiveOptionBandBarRow MapOptionBand(
        long sessionId,
        AdaptiveCompletedBarPackage package,
        OptionType side)
    {
        var result = package.OptionBand;
        var m = side == OptionType.Call ? result.Call : result.Put;
        var rolling = side == OptionType.Call ? result.CallRolling : result.PutRolling;
        var selection = result.Selection;
        var bar = package.FutureBar;

        return new AdaptiveOptionBandBarRow
        {
            SessionId = sessionId,
            BarSeq = bar.BarSeq,
            Side = side,
            CenterStrike = selection?.CenterStrike ?? 0d,
            BandStrikes = selection is null ? string.Empty : string.Join(';', selection.Strikes),
            BandRolled = result.BandRolled,
            BandAvailable = m is not null,
            UnavailableReason = m is null ? result.UnavailableReason : null,
            StartAvailableAtUtc = bar.StartAvailableAtUtc,
            EndAvailableAtUtc = bar.EndAvailableAtUtc,
            DurationSeconds = bar.DurationSeconds,
            BandPremiumIndexOpen = m?.PremiumIndexOpen,
            BandPremiumIndexClose = m?.PremiumIndexClose,
            BarBandPriceChange = m?.BarPriceChange,
            BarBandReturnPct = m?.PremiumIndexOpen is > 0 && m.BarPriceChange.HasValue
                ? 100d * m.BarPriceChange.Value / m.PremiumIndexOpen.Value
                : null,
            RollingBandPriceChange = rolling?.RollingBandPriceChange,
            RollingReturnPct = rolling?.RollingReturnPct,
            RollingEfficiency = rolling?.RollingEfficiency,

            ContractTotalQuantity = m?.ContractTotalQuantity ?? 0,
            ContractStrictBuy = m?.ContractStrictBuy ?? 0,
            ContractStrictSell = m?.ContractStrictSell ?? 0,
            ContractStrictUnknown = m?.ContractStrictUnknown ?? 0,
            ContractStrictDelta = m?.ContractStrictDelta ?? 0,
            ContractStrictDeltaRatioTotal = m?.ContractStrictDeltaRatioTotal ?? 0,
            ContractStrictCoverage = m?.ContractStrictCoverage ?? 0,
            ContractEnrichedBuy = m?.ContractEnrichedBuy ?? 0,
            ContractEnrichedSell = m?.ContractEnrichedSell ?? 0,
            ContractEnrichedUnknown = m?.ContractEnrichedUnknown ?? 0,
            ContractEnrichedDelta = m?.ContractEnrichedDelta ?? 0,
            ContractEnrichedDeltaRatio = m?.ContractEnrichedDeltaRatio ?? 0,
            ContractRollingStrictDelta = rolling?.ContractStrictDelta,
            ContractRollingStrictAbsDeltaChange = rolling?.ContractStrictAbsDeltaChange,
            ContractRollingStrictDeltaRatioTotal = rolling?.ContractStrictDeltaRatioTotal,
            ContractRollingEnrichedDelta = rolling?.ContractEnrichedDelta,
            ContractRollingEnrichedDeltaChange = rolling?.ContractEnrichedDeltaChange,
            ContractRollingEnrichedDeltaRatio = rolling?.ContractEnrichedDeltaRatio,

            NotionalTotal = m?.NotionalTotal ?? 0,
            NotionalStrictBuy = m?.NotionalStrictBuy ?? 0,
            NotionalStrictSell = m?.NotionalStrictSell ?? 0,
            NotionalStrictUnknown = m?.NotionalStrictUnknown ?? 0,
            NotionalStrictDelta = m?.NotionalStrictDelta ?? 0,
            NotionalStrictDeltaRatioTotal = m?.NotionalStrictDeltaRatioTotal ?? 0,
            NotionalStrictCoverage = m?.NotionalStrictCoverage ?? 0,
            NotionalEnrichedBuy = m?.NotionalEnrichedBuy ?? 0,
            NotionalEnrichedSell = m?.NotionalEnrichedSell ?? 0,
            NotionalEnrichedUnknown = m?.NotionalEnrichedUnknown ?? 0,
            NotionalEnrichedDelta = m?.NotionalEnrichedDelta ?? 0,
            NotionalEnrichedDeltaRatio = m?.NotionalEnrichedDeltaRatio ?? 0,
            NotionalRollingStrictDelta = rolling?.NotionalStrictDelta,
            NotionalRollingStrictAbsDeltaChange = rolling?.NotionalStrictAbsDeltaChange,
            NotionalRollingStrictDeltaRatioTotal = rolling?.NotionalStrictDeltaRatioTotal,
            NotionalRollingEnrichedDelta = rolling?.NotionalEnrichedDelta,
            NotionalRollingEnrichedDeltaChange = rolling?.NotionalEnrichedDeltaChange,
            NotionalRollingEnrichedDeltaRatio = rolling?.NotionalEnrichedDeltaRatio,

            BarTradeUpdates = m?.TradeUpdates ?? 0,
            RollingTradeUpdates = rolling?.TradeUpdates,
            ContractRollingStrictCoverage = rolling?.ContractStrictCoverage,
            NotionalRollingStrictCoverage = rolling?.NotionalStrictCoverage,
            ContractRollingActivityPerSecond = rolling?.ContractActivityPerSecond,
            NotionalRollingActivityPerSecond = rolling?.NotionalActivityPerSecond,

            BandOiOpen = m?.OiOpen,
            BandOiClose = m?.OiClose,
            BarOiChange = m?.OiChange,
            BarOiChangePct = m?.OiChangePct,
            RollingOiChange = rolling?.OiChange,
            RollingOiChangePct = rolling?.OiChangePct,
            PremiumNotionalOiAtClose = m?.PremiumNotionalOiClose,
            PremiumNotionalOiChange = m?.PremiumNotionalOiChange,
        };
    }

    static AdaptiveOptionResidualBarRow MapResidual(
        AdaptiveSessionStateRow session,
        int barSeq,
        DateTimeOffset endUtc,
        ResidualBarResult result)
    {
        var r = result.Reading;
        return new AdaptiveOptionResidualBarRow
        {
            SessionId = session.Id,
            BarSeq = barSeq,
            Variant = result.Variant,
            EndAvailableAtUtc = endUtc,
            CenterStrike = r?.CenterStrike ?? session.ResidualCenterStrike ?? 0d,
            FutureChangeFrom0930 = r?.FutureChangeFrom0930 ?? 0d,
            ModeledWeeklyUnderlying = r?.ModeledWeeklyUnderlying ?? 0d,
            CEActual = r?.CEActual ?? 0d,
            CEExpected = r?.CEExpected ?? 0d,
            CEActualChange = r?.CEActualChange ?? 0d,
            CEExpectedChange = r?.CEExpectedChange ?? 0d,
            CEResidual = r?.CEResidual ?? 0d,
            CEResidualPct = r?.CEResidualPct ?? 0d,
            PEActual = r?.PEActual ?? 0d,
            PEExpected = r?.PEExpected ?? 0d,
            PEActualChange = r?.PEActualChange ?? 0d,
            PEExpectedChange = r?.PEExpectedChange ?? 0d,
            PEResidual = r?.PEResidual ?? 0d,
            PEResidualPct = r?.PEResidualPct ?? 0d,
            DirectionalResidualPct = r?.DirectionalResidualPct ?? 0d,
            ResidualDelta = result.ResidualDelta,
            ResidualDirection = result.ResidualDirection,
            FuturesRollingDirection = result.FuturesRollingDirection,
            Relationship = result.Relationship,
            CommonResidualPct = r?.CommonResidualPct ?? 0d,
            MaxQuoteAgeSeconds = r?.MaxQuoteAgeSeconds ?? 0d,
            IsAvailable = result.IsAvailable,
            UnavailableReason = result.UnavailableReason,
        };
    }

    async Task VerifyDependentRowsAsync(
        AdaptiveObserverDbContext db,
        AdaptiveSessionStateRow session,
        AdaptiveCompletedBarPackage package,
        CancellationToken ct)
    {
        var barSeq = package.FutureBar.BarSeq;

        var rolling = package.FlowState.Rolling;
        var existingRolling = await db.RollingStates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SessionId == session.Id && x.EndBarSeq == barSeq, ct);
        if (rolling is null)
        {
            if (existingRolling is not null)
                throw Mismatch("rolling row exists but replay has no rolling state", barSeq);
        }
        else
        {
            if (existingRolling is null)
                throw Mismatch("persisted rolling row is missing", barSeq);
            Equal(existingRolling.StrictDelta, rolling.StrictDelta, "Rolling.StrictDelta", barSeq);
            Near(existingRolling.StrictDeltaRatioTotal, rolling.StrictDeltaRatioTotal, "Rolling.StrictDeltaRatioTotal", barSeq);
            Near(existingRolling.PriceDisplacement, rolling.PriceDisplacement, "Rolling.PriceDisplacement", barSeq);
            if (existingRolling.State != package.FlowState.State)
                throw Mismatch($"State persisted={existingRolling.State}, replay={package.FlowState.State}", barSeq);
        }

        var optionRows = await db.OptionBandBars.AsNoTracking()
            .Where(x => x.SessionId == session.Id && x.BarSeq == barSeq)
            .ToListAsync(ct);
        if (optionRows.Count != 2)
            throw Mismatch($"expected 2 option rows, persisted={optionRows.Count}", barSeq);

        foreach (var side in new[] { OptionType.Call, OptionType.Put })
        {
            var expected = MapOptionBand(session.Id, package, side);
            var actual = optionRows.Single(x => x.Side == side);
            if (actual.BandAvailable != expected.BandAvailable)
                throw Mismatch($"{side} BandAvailable differs", barSeq);
            Near(actual.NotionalStrictDelta, expected.NotionalStrictDelta, $"{side}.NotionalStrictDelta", barSeq);
            Near(actual.ContractStrictDeltaRatioTotal, expected.ContractStrictDeltaRatioTotal, $"{side}.ContractStrictDeltaRatioTotal", barSeq);
        }

        if (barSeq > 0 && package.FutureBar.EndAvailableAtUtc >= session.OpeningWindowEndUtc)
        {
            var residualRows = await db.OptionResidualBars.AsNoTracking()
                .Where(x => x.SessionId == session.Id && x.BarSeq == barSeq)
                .ToListAsync(ct);
            if (residualRows.Count != 2)
                throw Mismatch($"expected 2 residual rows, persisted={residualRows.Count}", barSeq);

            foreach (var expectedResult in package.Residuals)
            {
                var expected = MapResidual(session, barSeq, package.FutureBar.EndAvailableAtUtc, expectedResult);
                var actual = residualRows.Single(x => x.Variant == expected.Variant);
                if (actual.IsAvailable != expected.IsAvailable)
                    throw Mismatch($"{expected.Variant} residual availability differs", barSeq);
                if (actual.IsAvailable)
                    Near(actual.DirectionalResidualPct, expected.DirectionalResidualPct, $"{expected.Variant}.DirectionalResidualPct", barSeq);
            }
        }
    }

    static void VerifyFuture(AdaptiveFutureBarRow actual, ExactAdaptiveBar expected)
    {
        var seq = expected.BarSeq;
        Equal(actual.Volume, expected.Volume, "Future.Volume", seq);
        Equal(actual.FirstSourceTickId, expected.FirstSourceTickId, "Future.FirstSourceTickId", seq);
        Equal(actual.LastSourceTickId, expected.LastSourceTickId, "Future.LastSourceTickId", seq);
        if (actual.StartAvailableAtUtc != expected.StartAvailableAtUtc || actual.EndAvailableAtUtc != expected.EndAvailableAtUtc)
            throw Mismatch("future timestamps differ", seq);
        Near(actual.Open, expected.Open, "Future.Open", seq);
        Near(actual.High, expected.High, "Future.High", seq);
        Near(actual.Low, expected.Low, "Future.Low", seq);
        Near(actual.Close, expected.Close, "Future.Close", seq);
        Equal(actual.StrictDelta, expected.StrictDelta, "Future.StrictDelta", seq);
        Equal(actual.EnrichedDelta, expected.EnrichedDelta, "Future.EnrichedDelta", seq);
    }

    static void Equal(long actual, long expected, string field, int seq)
    {
        if (actual != expected) throw Mismatch($"{field}: persisted={actual}, replay={expected}", seq);
    }

    static void Near(double actual, double expected, string field, int seq)
    {
        if (Math.Abs(actual - expected) > DoubleTolerance)
            throw Mismatch($"{field}: persisted={actual:R}, replay={expected:R}", seq);
    }

    static InvalidOperationException Mismatch(string message, int seq) =>
        new($"Adaptive restart parity mismatch at BarSeq {seq}: {message}. Persisted history will not be overwritten.");
}
