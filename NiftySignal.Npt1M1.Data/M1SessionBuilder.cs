using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.Trading.Adapters;

namespace NiftySignal.Npt1M1.Data;

/// <summary>The outcome of trying to freeze the M1 session of a day.</summary>
/// <param name="Build">The frozen session, or null.</param>
/// <param name="Wait">True when the session cannot be frozen YET (data has not advanced past 09:30:05, or the instrument master is not resolved): try again later.</param>
/// <param name="Reason">Why there is no session (waiting, or permanently unavailable for the day, e.g. a partial opening).</param>
public sealed record M1SessionAttempt(M1SessionBuild? Build, bool Wait, string Reason);

public sealed record M1SessionBuild(M1Freeze Freeze, FutureInstrument Future, IReadOnlyList<VolumeHistoryRow> Priors, IReadOnlyList<SourceTick> FreezeRows);

/// <summary>
/// Freezes the M1 session of a day exactly as the research export did (see <see cref="M1SessionFreeze"/>), from the INSTRUMENT MASTER and the raw ticks directly: the producer does not depend on
/// the adaptive-v1 session row, runtime or tables at all. The prior-session volumes are computed once per date from the raw ticks and kept in <c>npt1m1.volume_history</c> (append-only).
/// </summary>
public sealed class M1SessionBuilder(Npt1TickReader reader)
{
    /// <summary>How many prior trade dates are examined for the baseline (the rule itself uses at most 20 eligible ones).</summary>
    public const int PriorDatesExamined = 35;

    /// <summary>The freeze reads rows through 09:30:05 (research); the data must have advanced beyond it by the 2 s ordering lag before the freeze is attempted.</summary>
    public static readonly TimeSpan FreezeReadyAfterCutoff = TimeSpan.FromSeconds(7);

    public async Task<M1SessionAttempt> TryBuildAsync(NiftySignalDbContext source, Npt1M1DbContext store, DateOnly day, DateTimeOffset nowUtc, decimal fallbackTickSize, CancellationToken ct)
    {
        var cutoff = M1Time.IstTime(day, 9, 30);
        if (nowUtc < cutoff + FreezeReadyAfterCutoff)
        {
            return new M1SessionAttempt(null, true, "waiting for 09:30:07 IST");
        }

        var future = await FutureOfAsync(source, day, ct);
        if (future is null)
        {
            return new M1SessionAttempt(null, true, "the NIFTY future is not in the instrument master of this day yet");
        }

        var optionRows = await source.Instruments.AsNoTracking()
            .Where(i => i.AsOfDate == day && i.Underlying == "NIFTY" && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.StrikePrice != null)
            .OrderBy(i => i.ExpiryDate).ThenBy(i => i.StrikePrice)
            .ToListAsync(ct);
        var weekly = optionRows.Select(x => x.ExpiryDate!.Value).Distinct().OrderBy(x => x).FirstOrDefault();
        if (weekly == default)
        {
            return new M1SessionAttempt(null, true, "weekly NIFTY options are not in the instrument master of this day yet");
        }

        var nearest = optionRows.Where(x => x.ExpiryDate == weekly).ToList();
        var descriptors = nearest.Select(i => new ObserverOptionInstrument(i.Token, i.TradingSymbol, i.OptionType, (double)i.StrikePrice!.Value, i.ExpiryDate!.Value, i.LotSize)).ToList();
        var tickSizes = nearest.ToDictionary(i => i.Token, i => i.TickSize > 0m ? i.TickSize : fallbackTickSize, StringComparer.Ordinal);
        var tokens = descriptors.Select(d => d.Token).Append(future.Token).Distinct(StringComparer.Ordinal).ToArray();

        var rows = await reader.ReadWindowAsync(source, tokens, day, cutoff.AddSeconds(5), ct);
        var history = await EnsureVolumeHistoryAsync(source, store, day, nowUtc, ct);
        var priors = VolumeHistory.Eligible(history);
        try
        {
            var freeze = M1SessionFreeze.Build(day, weekly, future, descriptors, tickSizes, rows, priors);
            return new M1SessionAttempt(new M1SessionBuild(freeze, future, history, rows), false, "frozen");
        }
        catch (InvalidOperationException e) when (e.Message.StartsWith("The stored futures stream does not cross 09:30", StringComparison.Ordinal))
        {
            return new M1SessionAttempt(null, true, e.Message);
        }
        catch (InvalidOperationException e)
        {
            return new M1SessionAttempt(null, false, "M1 session unavailable for this day: " + e.Message);
        }
    }

    private static async Task<FutureInstrument?> FutureOfAsync(NiftySignalDbContext source, DateOnly day, CancellationToken ct)
    {
        var f = await source.Instruments.AsNoTracking()
            .Where(i => i.AsOfDate == day && i.Underlying == "NIFTY" && i.InstrumentType == InstrumentType.Future)
            .OrderBy(i => i.ExpiryDate)
            .FirstOrDefaultAsync(ct);
        return f is null ? null : new FutureInstrument(f.Token, f.TradingSymbol, f.ExpiryDate ?? day, f.LotSize);
    }

    /// <summary>Prior sessions' volumes: stored rows are read, missing dates are computed from the raw ticks once and stored. Only dates strictly before <paramref name="day"/>.</summary>
    public async Task<IReadOnlyList<VolumeHistoryRow>> EnsureVolumeHistoryAsync(NiftySignalDbContext source, Npt1M1DbContext store, DateOnly day, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var dates = await source.Instruments.AsNoTracking()
            .Where(i => i.AsOfDate < day && i.Underlying == "NIFTY" && i.InstrumentType == InstrumentType.Future)
            .Select(i => i.AsOfDate).Distinct()
            .OrderByDescending(d => d).Take(PriorDatesExamined)
            .ToListAsync(ct);
        var known = (await store.VolumeHistory.AsNoTracking().Where(v => dates.Contains(v.TradeDate)).ToListAsync(ct)).ToDictionary(v => v.TradeDate);
        var result = new List<VolumeHistoryRow>();
        foreach (var date in dates.OrderBy(d => d))
        {
            if (known.TryGetValue(date, out var stored))
            {
                result.Add(ToRow(stored));
                continue;
            }

            var future = await FutureOfAsync(source, date, ct);
            if (future is null)
            {
                continue;
            }

            var ticks = await reader.ReadWindowAsync(source, new[] { future.Token }, date, M1Time.IstTime(date, 15, 35), ct);
            var row = VolumeHistory.Compute(future, date, ticks);
            store.VolumeHistory.Add(new M1VolumeHistoryRow
            {
                TradeDate = row.Date,
                FutureToken = row.FutureToken,
                FutureSymbol = row.FutureSymbol,
                Expiry = row.Expiry,
                LotSize = row.LotSize,
                OpeningVolume = row.OpeningVolume,
                WindowVolume = row.WindowVolume,
                CloseVolume = row.CloseVolume,
                FullDayVolume = row.FullDayVolume,
                OpeningCoverageMinutes = row.OpeningCoverageMinutes,
                WindowCoverageMinutes = row.WindowCoverageMinutes,
                FirstTickIst = row.FirstTickIst,
                LastTickIst = row.LastTickIst,
                FuturesTicks = row.FuturesTicks,
                BaselineEligible = row.BaselineEligible,
                ComputedAtUtc = nowUtc,
            });
            await store.SaveChangesAsync(ct);
            result.Add(row);
        }

        return result;
    }

    private static VolumeHistoryRow ToRow(M1VolumeHistoryRow v)
        => new(v.TradeDate, v.FutureToken, v.FutureSymbol, v.Expiry, v.LotSize, v.OpeningVolume, v.WindowVolume, v.CloseVolume, v.FullDayVolume, v.OpeningCoverageMinutes, v.WindowCoverageMinutes, v.FirstTickIst, v.LastTickIst, v.FuturesTicks);

    /// <summary>
    /// Records the session row once; on every later call (a restart) the rebuilt freeze must have the same fingerprint, otherwise the stream refuses to run on a different universe or base bar than
    /// the one already used today. Returns an error text, or null when consistent.
    /// </summary>
    public async Task<string?> PersistOrVerifyAsync(Npt1M1DbContext store, M1SessionBuild build, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var day = build.Freeze.Freeze.TradeDate;
        var id = M1Identity.SessionId(day);
        var existing = await store.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == id, ct);
        var report = build.Freeze.Report;
        if (existing is not null)
        {
            return existing.FreezeFingerprint == report.Fingerprint
                ? null
                : $"The M1 session of {day:yyyy-MM-dd} was already frozen with fingerprint {existing.FreezeFingerprint}; the rebuilt freeze has {report.Fingerprint}. The stream refuses to run on a different freeze.";
        }

        var decision = build.Freeze.Decision;
        store.Sessions.Add(new M1SessionRow
        {
            SessionId = id,
            TradeDate = day,
            ModelVersion = M1Identity.ModelVersion,
            FreezeFingerprint = report.Fingerprint,
            BaseBarVolume = report.BaseBarVolume,
            OpeningVolume = report.OpeningVolume,
            BarSizeStatus = decision.MethodStatus,
            DecisionJson = JsonSerializer.Serialize(decision),
            ReportJson = JsonSerializer.Serialize(report),
            FutureToken = build.Future.Token,
            WeeklyExpiry = build.Freeze.Freeze.WeeklyOptionExpiry,
            CreatedAtUtc = nowUtc,
        });
        await store.SaveChangesAsync(ct);
        return null;
    }
}
