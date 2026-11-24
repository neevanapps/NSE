using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using Npgsql;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// Read-only adaptive observer projection. SignalR only invalidates this view; all values are read
/// from the committed adaptive database so Dashboard restart/reconnect cannot invent state.
/// </summary>
public sealed class AdaptiveObserverDataService(
    IDbContextFactory<AdaptiveObserverDbContext> dbFactory,
    ILogger<AdaptiveObserverDataService> logger)
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    long? _notifiedSessionId;
    int? _notifiedBarSeq;

    public event Action? Updated;

    public long? LastNotifiedSessionId => _notifiedSessionId;
    public int? LastNotifiedBarSeq => _notifiedBarSeq;

    public void NotifyStateChanged(long sessionId, int barSeq)
    {
        _notifiedSessionId = sessionId;
        _notifiedBarSeq = barSeq;
        Updated?.Invoke();
    }

    public async Task<AdaptiveObserverHeaderSnapshot?> LoadHeaderAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await FindTodaySessionAsync(db, ct);
        if (session is null)
        {
            return null;
        }

        var runtime = await db.Runtime.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SessionId == session.Id, ct);
        return runtime is null ? null : new AdaptiveObserverHeaderSnapshot(session, runtime,
            await LoadValidBarCountAsync(db, session, runtime.LastCompletedBarSeq, ct));
    }

    /// <summary>
    /// Steady-state live refresh: the already-displayed session is known, so a single indexed read of its runtime row is enough to refresh status,
    /// heartbeat and partial-bar progress. Whether a completed bar changed (and a full reload is needed) is decided by the caller from
    /// <c>LastCompletedBarSeq</c>; the session row and the ten-bar readiness only change when a bar completes.
    /// </summary>
    public async Task<AdaptiveObserverRuntimeRow?> LoadRuntimeAsync(long sessionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Runtime.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == sessionId, ct);
    }

    public async Task<AdaptiveObserverSnapshot?> LoadSnapshotAsync(int requestedRows, CancellationToken ct = default, long? captureSessionId = null, int? throughBarSeq = null)
    {
        var rowCount = requestedRows switch
        {
            <= 5 => 5,
            <= 10 => 10,
            _ => 15,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = captureSessionId is { } id
            ? await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            : await FindTodaySessionAsync(db, ct);
        if (session is null)
        {
            return null;
        }

        var runtime = await db.Runtime.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SessionId == session.Id, ct);
        if (runtime is null)
        {
            return null;
        }

        if (throughBarSeq is { } boundary)
        {
            if (captureSessionId is null || boundary < 0 || boundary > runtime.LastCompletedBarSeq)
                throw new InvalidOperationException("Capture boundary is not committed.");
            runtime.LastCompletedBarSeq = boundary;
            runtime.CurrentPartialBarVolume = 0;
            runtime.CurrentPartialBarStartedAtUtc = null;
            runtime.LastProcessedSourceAvailableAtUtc = null;
        }
        var futureBars = await db.FutureBars.AsNoTracking()
            .Where(x => x.SessionId == session.Id && x.BarSeq <= runtime.LastCompletedBarSeq)
            .OrderByDescending(x => x.BarSeq)
            .Take(rowCount)
            .ToListAsync(ct);

        var seqs = futureBars.Select(x => x.BarSeq).ToArray();
        if (throughBarSeq is { } expected && expected > 0 && futureBars.FirstOrDefault()?.BarSeq != expected)
            throw new InvalidOperationException("Capture boundary bar is missing.");
        if (throughBarSeq is not null) runtime.LastProcessedSourceAvailableAtUtc = futureBars.FirstOrDefault()?.EndAvailableAtUtc;
        var rolling = seqs.Length == 0
            ? new List<AdaptiveRollingStateRow>()
            : await db.RollingStates.AsNoTracking()
                .Where(x => x.SessionId == session.Id && seqs.Contains(x.EndBarSeq))
                .ToListAsync(ct);
        var rollingBySeq = rolling.ToDictionary(x => x.EndBarSeq);

        var futures = futureBars
            .Select(x => new AdaptiveFuturesGridRow(x, rollingBySeq.GetValueOrDefault(x.BarSeq)))
            .ToList();

        var optionRows = seqs.Length == 0
            ? new List<AdaptiveOptionBandBarRow>()
            : await db.OptionBandBars.AsNoTracking()
                .Where(x => x.SessionId == session.Id && seqs.Contains(x.BarSeq))
                .ToListAsync(ct);
        var callBySeq = optionRows.Where(x => x.Side == NiftySignal.Domain.Enums.OptionType.Call).ToDictionary(x => x.BarSeq);
        var putBySeq = optionRows.Where(x => x.Side == NiftySignal.Domain.Enums.OptionType.Put).ToDictionary(x => x.BarSeq);
        var options = futureBars.Select(x => new AdaptiveOptionPairGridRow(
                x.BarSeq,
                x.EndAvailableAtUtc,
                callBySeq.GetValueOrDefault(x.BarSeq),
                putBySeq.GetValueOrDefault(x.BarSeq)))
            .ToList();

        var residuals = seqs.Length == 0
            ? new List<AdaptiveOptionResidualBarRow>()
            : await db.OptionResidualBars.AsNoTracking()
                .Where(x => x.SessionId == session.Id && seqs.Contains(x.BarSeq))
                .OrderByDescending(x => x.BarSeq)
                .ThenBy(x => x.Variant)
                .ToListAsync(ct);

        // Every grid shares the same completed sequences. A missing/pre-09:30 diagnostic
        // remains an explicit unavailable row, rather than silently shortening Grid 3.
        var residualByKey = residuals.ToDictionary(x => (x.BarSeq,x.Variant));
        residuals = futureBars.SelectMany(bar => new[] { ResidualVariant.Atm, ResidualVariant.AtmPlusMinus2 }
            .Select(variant => residualByKey.GetValueOrDefault((bar.BarSeq,variant))
                ?? new AdaptiveOptionResidualBarRow { SessionId=session.Id, BarSeq=bar.BarSeq, Variant=variant,
                    EndAvailableAtUtc=bar.EndAvailableAtUtc, Relationship="NEUTRAL", IsAvailable=false,
                    UnavailableReason="Diagnostic row unavailable for this completed bar." }))
            .ToList();

        var anchors = await db.ResidualAnchorComponents.AsNoTracking()
            .Where(x => x.SessionId == session.Id)
            .OrderBy(x => x.Side)
            .ThenBy(x => x.Strike)
            .ToListAsync(ct);

        var invalidFutures = await LoadInvalidBarsAsync(db, session.Id, AdaptiveProjectionComponents.FuturesSupplemental, FuturesMicrostructureBar.MetricsVersion, seqs, ct);
        var invalidOptions = await LoadInvalidBarsAsync(db, session.Id, AdaptiveProjectionComponents.OptionsSupplemental, OptionsSupplementalBar.MetricsVersion, seqs, ct);
        // Known-invalid sidecar bars are withheld (shown unavailable), never displayed as trustworthy values.
        var futuresSupplemental = (await LoadFuturesSupplementalAsync(db, session.Id, seqs, ct))
            .Where(x => !invalidFutures.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
        var optionsSupplemental = (await LoadOptionsSupplementalAsync(db, session.Id, seqs, ct))
            .Where(x => !invalidOptions.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
        var invalidBasis = await LoadInvalidBarsAsync(db, session.Id, AdaptiveProjectionComponents.BasisSupplemental, FuturesBasisBar.MetricsVersion, seqs, ct);
        var basisSupplemental = (await LoadBasisSupplementalAsync(db, session.Id, seqs, ct))
            .Where(x => !invalidBasis.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
        return new AdaptiveObserverSnapshot(session, runtime, futures, options, residuals, anchors,
            await LoadValidBarCountAsync(db, session, runtime.LastCompletedBarSeq, ct),
            futuresSupplemental,
            optionsSupplemental,
            seqs.Length == 0 ? [] : await db.OptionResidualBars.AsNoTracking()
                .Where(x => x.SessionId == session.Id && x.BarSeq == seqs.Min() - 1).ToListAsync(ct),
            invalidFutures, invalidOptions, basisSupplemental, invalidBasis,
            await LoadFuturesDepth5Async(db, session.Id, seqs, ct));
    }

    /// <summary>
    /// DAY AVG inputs: ALL completed bars of the session from the start through <paramref name="throughBarSeq"/> (a screenshot of bar 196 never reads bar 197+), the sidecars of those bars and, for the
    /// fixed-strike grids, ONLY the selected expiry/strike/CE-PE tokens (never the option universe). Read-only; callers cache by session/bar/strike so a quote tick never re-runs it.
    /// </summary>
    public async Task<DayAverageData?> LoadDayAverageDataAsync(
        long sessionId, int throughBarSeq, FixedStrikeCatalog? catalog, double? strike, int rollWindowBars, IReadOnlyList<AdaptiveResidualAnchorComponentRow> anchors, CancellationToken ct = default)
    {
        if (throughBarSeq <= 0)
        {
            return null;
        }

        List<AdaptiveFutureBarRow> bars;
        List<AdaptiveFuturesGridRow> futures;
        Dictionary<int, AdaptiveFuturesSupplementalRow> sup;
        Dictionary<int, AdaptiveBasisSupplementalRow> basis;
        List<AdaptiveOptionResidualBarRow> residuals;
        IReadOnlyDictionary<int, AdaptiveFuturesDepth5Row> futuresDepth;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            bars = await db.FutureBars.AsNoTracking()
                .Where(x => x.SessionId == sessionId && x.BarSeq <= throughBarSeq)
                .OrderByDescending(x => x.BarSeq)
                .ToListAsync(ct);
            var seqs = bars.Select(x => x.BarSeq).ToArray();
            var rolling = (await db.RollingStates.AsNoTracking()
                .Where(x => x.SessionId == sessionId && x.EndBarSeq <= throughBarSeq)
                .ToListAsync(ct)).ToDictionary(x => x.EndBarSeq);
            futures = bars.Select(x => new AdaptiveFuturesGridRow(x, rolling.GetValueOrDefault(x.BarSeq))).ToList();

            var invalidFutures = await LoadInvalidBarsAsync(db, sessionId, AdaptiveProjectionComponents.FuturesSupplemental, FuturesMicrostructureBar.MetricsVersion, seqs, ct);
            var invalidBasis = await LoadInvalidBarsAsync(db, sessionId, AdaptiveProjectionComponents.BasisSupplemental, FuturesBasisBar.MetricsVersion, seqs, ct);
            sup = (await LoadFuturesSupplementalAsync(db, sessionId, seqs, ct)).Where(x => !invalidFutures.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
            basis = (await LoadBasisSupplementalAsync(db, sessionId, seqs, ct)).Where(x => !invalidBasis.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
            residuals = await db.OptionResidualBars.AsNoTracking()
                .Where(x => x.SessionId == sessionId && x.BarSeq <= throughBarSeq)
                .ToListAsync(ct);
            futuresDepth = await LoadFuturesDepth5Async(db, sessionId, seqs, ct);
        }

        FixedStrikeHistory? fixedHistory = null;
        if (catalog is not null && strike is { } k)
        {
            fixedHistory = await LoadFixedStrikeHistoryAsync(sessionId, catalog, k, bars.Select(b => (b.BarSeq, b.EndAvailableAtUtc)).ToList(), ct, rollWindowBars);
        }

        return new DayAverageData(sessionId, throughBarSeq, bars.Count, strike, futures, sup, basis, fixedHistory, residuals, anchors, futuresDepth);
    }

    /// <summary>
    /// History of ONE exact expiry + strike + CE token + PE token over the displayed completed bars. Reads only the persisted per-strike sidecar by the indexed
    /// (session, expiry, strike) key; a missing table (Dashboard started before the migration) or a bar without rows yields unavailable cells, never another strike.
    /// </summary>
    public async Task<FixedStrikeHistory?> LoadFixedStrikeHistoryAsync(
        long sessionId, FixedStrikeCatalog catalog, double strike, IReadOnlyList<(int BarSeq, DateTimeOffset EndAvailableAtUtc)> bars, CancellationToken ct = default, int rollWindowBars = 0)
    {
        var contract = catalog.Find(strike);
        if (contract is null)
        {
            return null;
        }

        var displayed = bars.Select(b => b.BarSeq).ToArray();
        // The rolling window of the earliest displayed bar reaches back (window - 1) bars; those earlier rows of the SAME contract are read too.
        var seqs = displayed.Length == 0 || rollWindowBars <= 1
            ? displayed
            : Enumerable.Range(Math.Max(1, displayed.Min() - (rollWindowBars - 1)), displayed.Max() - Math.Max(1, displayed.Min() - (rollWindowBars - 1)) + 1).ToArray();
        IReadOnlyList<AdaptiveOptionStrikeBarRow> stored = Array.Empty<AdaptiveOptionStrikeBarRow>();
        IReadOnlyList<AdaptiveOptionsDepth5Row> depth = Array.Empty<AdaptiveOptionsDepth5Row>();
        var unverified = UnverifiedStrikeBars.None;
        if (seqs.Length > 0)
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                unverified = await UnverifiedStrikeBars.LoadAsync(db, sessionId, seqs.Max(), ct);
                var version = OptionStrikeBarMetrics.MetricsVersion;
                var previous = OptionStrikeBarMetrics.PreviousMetricsVersion;
                var legacy = OptionStrikeBarMetrics.LegacyMetricsVersion;
                var expiry = catalog.Expiry;
                stored = await db.OptionStrikeBars.AsNoTracking()
                    .Where(x => x.SessionId == sessionId && x.Expiry == expiry && x.Strike == strike
                        && (x.MetricsVersion == version || x.MetricsVersion == previous || x.MetricsVersion == legacy)
                        && seqs.Contains(x.BarSeq))
                    .ToListAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                // not migrated yet: every cell is unavailable
            }

            // Top-5 option depth of exactly this strike's CE / PE token, only through the attestation (no attestation, a marker, changed values or a missing table: unavailable). Independent of the strike rows above.
            var depthTokens = new[] { contract.CeToken, contract.PeToken }.Where(t => t is not null).Select(t => t!).ToArray();
            if (depthTokens.Length > 0)
            {
                await using var depthDb = await dbFactory.CreateDbContextAsync(ct);
                depth = await LoadOptionDepth5Async(depthDb, sessionId, displayed.Min(), displayed.Max(), depthTokens, ct);
            }
        }

        return FixedStrikeProjection.Assemble(catalog, contract, bars, stored, rollWindowBars, unverified, depth);
    }

    /// <summary>The ATM at a completed bar's CLOSE. <see cref="Source"/>: stored (the Host's close-atm-v1 row), next-bar-start (a bar recorded before that sidecar existed: the NEXT bar's band was selected at this bar's close instant, so its centre is the same causal value),
    /// stored-unavailable / unverified / none (no strike; <see cref="Reason"/> says why). No later tick, prior ATM or manual selection is ever used.</summary>
    public sealed record CloseAtmResult(double? Strike, string Source, string? Reason);

    public async Task<CloseAtmResult> LoadCloseAtmAsync(long sessionId, int barSeq, CancellationToken ct = default)
    {
        if (barSeq < 1) return new(null, "none", "no completed bar");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        try
        {
            if (await db.ProjectionHealth.AsNoTracking().AnyAsync(x => x.SessionId == sessionId && x.Component == AdaptiveProjectionComponents.BarCloseAtm && x.Version == BarCloseAtm.Version && x.BarSeq == barSeq, ct))
                return new(null, "unverified", "the stored close ATM of this bar disagreed with its recomputation after a restart, so it is withheld");
            var row = await db.BarCloseAtm.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == sessionId && x.BarSeq == barSeq && x.Version == BarCloseAtm.Version, ct);
            if (row is not null)
                return row.AtmStrike is { } k ? new(k, "stored", null) : new(null, "stored-unavailable", row.UnavailableReason ?? "no ATM could be derived at this bar's close");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // close-atm-v1 not migrated yet: fall through to the causal derivation below
        }

        // A bar recorded before the close-ATM sidecar: the next bar's band is selected at THIS bar's close instant with the same rule, so its centre is the same causal value (a disclosed source, not a fallback to another ATM).
        // It is accepted ONLY when the persisted timestamps prove it: bar N+1's band is selected at bar N's close instant, so N+1's recorded start must equal N's recorded end exactly (this includes zero-duration split bars).
        // A band that started later was initialised from later data and is never N's close ATM; neither is one whose selection was unavailable (BandAvailable false).
        var closeInstant = await db.FutureBars.AsNoTracking().Where(x => x.SessionId == sessionId && x.BarSeq == barSeq).Select(x => (DateTimeOffset?)x.EndAvailableAtUtc).FirstOrDefaultAsync(ct);
        double? next = null;
        if (closeInstant is { } end)
            next = await db.OptionBandBars.AsNoTracking()
                .Where(x => x.SessionId == sessionId && x.BarSeq == barSeq + 1 && x.BandAvailable && x.StartAvailableAtUtc == end)
                .Select(x => (double?)x.CenterStrike).FirstOrDefaultAsync(ct);
        return next is { } c ? new(c, "next-bar-start", null) : new(null, "none", "close ATM is not recorded for this bar, and no persisted band proves the selection at its close instant");
    }

    /// <summary>
    /// The Market Summary grids for ONE session, pinned at completed bar <paramref name="endBarSeq"/> (no bar after it is read). The basket is the session's frozen weekly expiry resolved around
    /// <paramref name="centreStrike"/> - the authoritative ATM centre of the latest completed bar (the pinned target bar for a historical view), independent of the fixed-strike selector. The Lower (centre -1 listed step) and
    /// Upper (+1 step) TOTAL grids use the same centre. One scoped query reads the exact tokens of ATM-3..ATM+3; a missing table or missing rows yields coverage gaps, never another strike.
    /// </summary>
    public async Task<MarketSummaryView> LoadMarketSummaryAsync(long sessionId, int endBarSeq, FixedStrikeCatalog? catalog, double? centreStrike, CancellationToken ct = default, string? closeAtmSource = null, string? centreUnavailableReason = null)
    {
        if (catalog is null || centreStrike is not { } centre || endBarSeq < 1)
        {
            return MarketSummaryView.Unavailable(endBarSeq, catalog is null ? "no frozen option universe for this session" : centreStrike is null ? centreUnavailableReason ?? "no close ATM for this bar" : "no completed bar");
        }

        var listed = MarketSummaryMapper.Listed(catalog);
        var basket = NiftySignal.MarketSummary.BasketResolver.Resolve(catalog.Expiry, centre, listed);
        // Lower (ATM-1 step) and Upper (ATM+1 step) around the SAME centre: the history below is read once for the union of the three baskets (ATM-3..ATM+3).
        var lowerBasket = NiftySignal.MarketSummary.BasketResolver.Resolve(catalog.Expiry, centre, listed, -1);
        var upperBasket = NiftySignal.MarketSummary.BasketResolver.Resolve(catalog.Expiry, centre, listed, 1);
        var allBaskets = new[] { basket, lowerBasket, upperBasket };
        var tokens = allBaskets.SelectMany(b => b.Contracts).Where(c => c.Token is not null).Select(c => c.Token!).ToHashSet(StringComparer.Ordinal);
        List<AdaptiveFutureBarRow> bars;
        Dictionary<int, AdaptiveFuturesSupplementalRow> sup;
        List<AdaptiveOptionStrikeBarRow> stored = [];
        var unverified = UnverifiedStrikeBars.None;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            unverified = await UnverifiedStrikeBars.LoadAsync(db, sessionId, endBarSeq, ct);
            bars = await db.FutureBars.AsNoTracking().Where(x => x.SessionId == sessionId && x.BarSeq <= endBarSeq).OrderBy(x => x.BarSeq).ToListAsync(ct);
            var seqs = bars.Select(x => x.BarSeq).ToArray();
            var invalidFutures = await LoadInvalidBarsAsync(db, sessionId, AdaptiveProjectionComponents.FuturesSupplemental, FuturesMicrostructureBar.MetricsVersion, seqs, ct);
            sup = (await LoadFuturesSupplementalAsync(db, sessionId, seqs, ct)).Where(x => !invalidFutures.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
            if (tokens.Count > 0)
            {
                // Scoped by the indexed (SessionId, Expiry, Strike, Side, BarSeq) key: the five strikes of the basket only, never the whole universe. The exact-token filter is applied by the mapper.
                var strikeArray = allBaskets.SelectMany(b => b.Contracts).Where(c => c.Strike is not null).Select(c => c.Strike!.Value).Distinct().ToArray();
                var expiry = catalog.Expiry;
                try
                {
                    stored = await db.OptionStrikeBars.AsNoTracking()
                        .Where(x => x.SessionId == sessionId && x.Expiry == expiry && strikeArray.Contains(x.Strike) && x.BarSeq <= endBarSeq)
                        .ToListAsync(ct);
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
                {
                    // not migrated yet: every contract is a coverage gap
                }
            }
        }

        // Top-5 depth (flag-gated sidecars; absent rows are unavailable, never zero). Option rows only through the attestation, for exactly the three baskets' tokens.
        IReadOnlyDictionary<int, AdaptiveFuturesDepth5Row> futuresDepth;
        IReadOnlyList<AdaptiveOptionsDepth5Row> optionDepth = [];
        string? depthNote = null;
        await using (var depthDb = await dbFactory.CreateDbContextAsync(ct))
        {
            futuresDepth = await LoadFuturesDepth5Async(depthDb, sessionId, bars.Select(x => x.BarSeq).ToArray(), ct);
            if (tokens.Count > 0)
            {
                var verified = await VerifiedOptionsDepth5.LoadAsync(depthDb, sessionId, endBarSeq, ct);
                if (verified.TablePending) depthNote = "option depth unavailable: verification tables unreadable";
                optionDepth = await LoadOptionDepth5Async(depthDb, sessionId, 1, endBarSeq, tokens, ct);
                if (!verified.TablePending && optionDepth.Count == 0) depthNote = "option depth unavailable: no attested options-depth5 rows (flag off, or restart verification pending)";
            }
        }

        var futures = bars.ToDictionary(b => b.BarSeq, b => MarketSummaryMapper.FuturesBarOf(b, sup.GetValueOrDefault(b.BarSeq)));
        var depthHistory = new NiftySignal.MarketSummary.DepthHistory(optionDepth.Select(r => (r.Token, Depth5Ratios.ToDepthBar(r))));
        var futuresDepthBars = futuresDepth.Values.Select(Depth5Ratios.ToDepthBar).Where(x => x is not null).ToDictionary(x => x!.BarSeq, x => x!);
        // A4: strike bars recovery found disagreeing with the reconstruction are withheld (a coverage gap, never a normal observation) and listed in the view.
        var unverifiedNotes = new List<string>();
        if (unverified.TablePending) unverifiedNotes.Add("verification tables unreadable - ALL per-strike rows withheld");
        else
        {
            var withheld = stored.Select(r => (Row: r, Reason: unverified.WithheldReason(r))).Where(x => x.Reason is not null).ToList();
            unverifiedNotes.AddRange(withheld.Where(x => x.Reason != "no verification attestation").Select(x => $"{x.Row.TradingSymbol ?? x.Row.Token} bar {x.Row.BarSeq} ({x.Reason})").Distinct().OrderBy(x => x, StringComparer.Ordinal));
            var unattested = withheld.Count(x => x.Reason == "no verification attestation");
            if (unattested > 0) unverifiedNotes.Add($"{unattested} per-strike rows withheld: no verification attestation yet (Host restart replay pending, or a historical session that was never re-verified)");
        }

        stored = unverified.Exclude(stored).ToList();
        var history = new NiftySignal.MarketSummary.ContractHistory(MarketSummaryMapper.ContractBars(stored, tokens, catalog.Expiry, endBarSeq), bars.ToDictionary(b => b.BarSeq, b => b.DurationSeconds));
        // The boundaries of each window (start of its first bar, end of the target bar) for the as-of VIX alignment; bars are completed bars through the target only.
        var startOf = bars.ToDictionary(b => b.BarSeq, b => b.StartAvailableAtUtc);
        var endOf = bars.ToDictionary(b => b.BarSeq, b => b.EndAvailableAtUtc);
        var instants = NiftySignal.MarketSummary.SummaryWindow.All.Select(w =>
        {
            var from = w.Bars is { } n ? endBarSeq - n + 1 : 1;
            return new NiftySignal.MarketSummary.WindowInstant(w,
                from >= 1 && startOf.TryGetValue(from, out var s) ? s : null,
                endOf.TryGetValue(endBarSeq, out var e) ? e : null);
        }).ToList();
        return new MarketSummaryView(endBarSeq, basket, NiftySignal.MarketSummary.FuturesSummaryCalculator.Compute(futures, endBarSeq),
            NiftySignal.MarketSummary.OptionSummaryCalculator.ComputeAll(basket, history, endBarSeq), null, instants)
        {
            Unverified = unverifiedNotes,
            FuturesDepth = NiftySignal.MarketSummary.DepthSummaryCalculator.Futures(futuresDepthBars, endBarSeq),
            TotalDepth = NiftySignal.MarketSummary.DepthSummaryCalculator.Direction(basket, NiftySignal.MarketSummary.GridKind.Total, depthHistory, endBarSeq),
            LowerDepth = NiftySignal.MarketSummary.DepthSummaryCalculator.Direction(lowerBasket, NiftySignal.MarketSummary.GridKind.Total, depthHistory, endBarSeq),
            UpperDepth = NiftySignal.MarketSummary.DepthSummaryCalculator.Direction(upperBasket, NiftySignal.MarketSummary.GridKind.Total, depthHistory, endBarSeq),
            DepthNote = depthNote,
            CloseAtmStrike = centre,
            CloseAtmSource = closeAtmSource,
            LowerBasket = lowerBasket,
            Lower = NiftySignal.MarketSummary.OptionSummaryCalculator.Compute(lowerBasket, NiftySignal.MarketSummary.GridKind.Total, history, endBarSeq),
            UpperBasket = upperBasket,
            Upper = NiftySignal.MarketSummary.OptionSummaryCalculator.Compute(upperBasket, NiftySignal.MarketSummary.GridKind.Total, history, endBarSeq),
        };
    }

    /// <summary>Bars (of the displayed range) whose sidecar carries a durable failed-verification marker. Tolerates a not-yet-migrated schema.</summary>
    static async Task<IReadOnlySet<int>> LoadInvalidBarsAsync(
        AdaptiveObserverDbContext db, long sessionId, string component, string version, int[] seqs, CancellationToken ct)
    {
        if (seqs.Length == 0) return new HashSet<int>();
        try
        {
            var bars = await db.ProjectionHealth.AsNoTracking()
                .Where(x => x.SessionId == sessionId && x.Component == component && x.Version == version && seqs.Contains(x.BarSeq))
                .Select(x => x.BarSeq).ToListAsync(ct);
            return bars.ToHashSet();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new HashSet<int>();
        }
    }

    /// <summary>
    /// Futures Top-5 sidecar (futures-depth5-v1) of the given bars: the futures trust model (insert-if-missing, verified on restart, a mismatch writes a per-bar marker), so a marked bar is withheld. A missing relation
    /// (Dashboard started before the Host migrated) or a flag-off Host simply has no rows: every value is then unavailable, never zero.
    /// </summary>
    static async Task<IReadOnlyDictionary<int, AdaptiveFuturesDepth5Row>> LoadFuturesDepth5Async(
        AdaptiveObserverDbContext db, long sessionId, int[] seqs, CancellationToken ct)
    {
        if (seqs.Length == 0) return new Dictionary<int, AdaptiveFuturesDepth5Row>();
        try
        {
            var version = FuturesDepth5Bar.MetricsVersion;
            var invalid = await LoadInvalidBarsAsync(db, sessionId, AdaptiveProjectionComponents.FuturesDepth5, version, seqs, ct);
            var rows = await db.FuturesDepth5.AsNoTracking()
                .Where(x => x.SessionId == sessionId && x.MetricsVersion == version && seqs.Contains(x.BarSeq))
                .ToListAsync(ct);
            return rows.Where(x => !invalid.Contains(x.BarSeq)).GroupBy(x => x.BarSeq).ToDictionary(g => g.Key, g => g.First());
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new Dictionary<int, AdaptiveFuturesDepth5Row>();
        }
    }

    /// <summary>Attested option depth rows (options-depth5-v1) of the given tokens over a bar range. Fails closed: no attestation, a marker, changed values or an unreadable table all yield NO row (unavailable).</summary>
    static async Task<IReadOnlyList<AdaptiveOptionsDepth5Row>> LoadOptionDepth5Async(
        AdaptiveObserverDbContext db, long sessionId, int fromBar, int throughBar, IReadOnlyCollection<string> tokens, CancellationToken ct)
    {
        if (throughBar < 1 || tokens.Count == 0) return [];
        return await VerifiedOptionsDepth5.LoadServedRowsAsync(db, sessionId, Math.Max(1, fromBar), throughBar, ct, tokens);
    }

    /// <summary>Basis sidecar read (approved 5-second freshness already applied to the stored values); same missing-relation tolerance.</summary>
    static async Task<IReadOnlyDictionary<int, AdaptiveBasisSupplementalRow>> LoadBasisSupplementalAsync(
        AdaptiveObserverDbContext db, long sessionId, int[] seqs, CancellationToken ct)
    {
        if (seqs.Length == 0) return new Dictionary<int, AdaptiveBasisSupplementalRow>();
        try
        {
            var version = FuturesBasisBar.MetricsVersion;
            var rows = await db.BasisSupplemental.AsNoTracking()
                .Where(x => x.SessionId == sessionId && x.MetricsVersion == version && seqs.Contains(x.BarSeq))
                .ToListAsync(ct);
            return rows.ToDictionary(x => x.BarSeq);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new Dictionary<int, AdaptiveBasisSupplementalRow>();
        }
    }

    /// <summary>Options sidecar read; same missing-relation tolerance as <see cref="LoadFuturesSupplementalAsync"/>.</summary>
    static async Task<IReadOnlyDictionary<int, AdaptiveOptionsSupplementalRow>> LoadOptionsSupplementalAsync(
        AdaptiveObserverDbContext db, long sessionId, int[] seqs, CancellationToken ct)
    {
        if (seqs.Length == 0) return new Dictionary<int, AdaptiveOptionsSupplementalRow>();
        try
        {
            var version = OptionsSupplementalBar.MetricsVersion;
            var rows = await db.OptionsSupplemental.AsNoTracking()
                .Where(x => x.SessionId == sessionId && x.MetricsVersion == version && seqs.Contains(x.BarSeq))
                .ToListAsync(ct);
            return rows.ToDictionary(x => x.BarSeq);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new Dictionary<int, AdaptiveOptionsSupplementalRow>();
        }
    }

    /// <summary>
    /// Sidecar read (08-Oct plan section 71.4): supplemental metrics are optional, so a missing relation (Dashboard started before the
    /// Host migrated) means "no supplemental metrics", never a failed panel.
    /// </summary>
    static async Task<IReadOnlyDictionary<int, AdaptiveFuturesSupplementalRow>> LoadFuturesSupplementalAsync(
        AdaptiveObserverDbContext db, long sessionId, int[] seqs, CancellationToken ct)
    {
        if (seqs.Length == 0) return new Dictionary<int, AdaptiveFuturesSupplementalRow>();
        try
        {
            var version = FuturesMicrostructureBar.MetricsVersion;
            var legacy = FuturesMicrostructureBar.LegacyMetricsVersion;
            var rows = await db.FuturesSupplemental.AsNoTracking()
                .Where(x => x.SessionId == sessionId && (x.MetricsVersion == version || x.MetricsVersion == legacy) && seqs.Contains(x.BarSeq))
                .ToListAsync(ct);
            // latest supported version wins per bar; a v2 row (no GrossOfi) is a valid fallback with an unavailable OFI ratio
            return rows.GroupBy(x => x.BarSeq).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.MetricsVersion == version).First());
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new Dictionary<int, AdaptiveFuturesSupplementalRow>();
        }
    }

    static async Task<int> LoadValidBarCountAsync(AdaptiveObserverDbContext db, AdaptiveSessionStateRow session,
        int boundary, CancellationToken ct)
    {
        var bars = await db.FutureBars.AsNoTracking()
            .Where(x => x.SessionId == session.Id && x.BarSeq <= boundary
                && x.BarSeq > boundary - AdaptiveReadinessPolicy.RequiredBars)
            .ToListAsync(ct);
        return AdaptiveReadinessPolicy.CountValidSuffix(bars.Select(x => (x.BarSeq,
            AdaptiveReadinessPolicy.IsValid(x.Volume, session.BaseBarVolume, x.Open, x.High, x.Low,
                x.Close, x.TradeUpdates, x.StartAvailableAtUtc, x.EndAvailableAtUtc))), boundary);
    }

    async Task<AdaptiveSessionStateRow?> FindTodaySessionAsync(AdaptiveObserverDbContext db, CancellationToken ct)
    {
        var todayIst = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(IstOffset).DateTime);
        try
        {
            return await db.Sessions.AsNoTracking()
                .Where(x => x.TradeDate == todayIst && (x.ModelVersion == OpeningVolumeProjectionV1.ModelVersion || x.ModelVersion == AdaptiveFixedClockV1.ModelVersion))
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Adaptive dashboard read failed while locating today's session.");
            throw;
        }
    }
}
