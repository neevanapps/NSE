// PORT of research/bar-size-v2 e402baf NiftySignal.Trading.Llm/VolumeHistory.cs. The computation (clean-tick normaliser + trade-flow enricher, windows [09:15,09:30) / [09:30,15:00) /
// [15:00,15:30), coverage, eligibility) is unchanged; only the instrument parameter type and the namespace differ.
using System.Globalization;
using System.Text;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Trading.Adapters;

namespace NiftySignal.Npt1M1;

/// <summary>One session's futures volumes, measured with the observer's own volume definition (the clean-tick normaliser plus the trade-flow enricher TradeVolume).</summary>
public sealed record VolumeHistoryRow(
    DateOnly Date,
    string FutureToken,
    string FutureSymbol,
    DateOnly Expiry,
    int LotSize,
    long OpeningVolume,
    long WindowVolume,
    long CloseVolume,
    long FullDayVolume,
    int OpeningCoverageMinutes,
    int WindowCoverageMinutes,
    string FirstTickIst,
    string LastTickIst,
    long FuturesTicks)
{
    /// <summary>A session may enter a baseline only when its opening is fully covered and its window volume is complete up to 15:00.</summary>
    public bool BaselineEligible => OpeningCoverageMinutes == 15 && OpeningVolume > 0 && WindowVolume > 0 && string.CompareOrdinal(LastTickIst, "15:00:00") >= 0;
}

public static class VolumeHistory
{
    public const string FileName = "volume_history.csv";

    /// <summary>
    /// The three windows are half-open in IST: opening [09:15, 09:30), window [09:30, 15:00), close [15:00, 15:30). Rows are filtered, normalised, ordered and enriched exactly as
    /// <see cref="M1SessionFreeze.Build"/> does for the futures token, so the opening volume equals the freeze's OpeningVolume (checked by the export).
    /// </summary>
    public static VolumeHistoryRow Compute(FutureInstrument future, DateOnly day, IEnumerable<SourceTick> rows)
    {
        var open = M1Time.IstTime(day, 9, 15);
        var cutoff = M1Time.IstTime(day, 9, 30);
        var windowEnd = M1Time.IstTime(day, 15, 0);
        var closeEnd = M1Time.IstTime(day, 15, 30);
        var through = M1Time.IstTime(day, 15, 35);
        var normalizer = new AdaptiveIncrementalTickNormalizer();
        var clean = new List<CleanObserverTick>();
        foreach (var row in rows)
        {
            if (row.Token != future.Token || row.ExchangeTimestamp > through || row.ReceivedAt > through)
            {
                continue;
            }

            if (normalizer.Process(row.Token, row.ToRaw()) is { } tick && tick.AvailableAt >= open && tick.AvailableAt <= through)
            {
                clean.Add(tick);
            }
        }

        clean.Sort((a, b) => a.AvailableAt != b.AvailableAt ? a.AvailableAt.CompareTo(b.AvailableAt) : a.Id.CompareTo(b.Id));
        var enricher = new AdaptiveTradeFlowEnricher();
        long opening = 0, window = 0, close = 0;
        foreach (var tick in clean)
        {
            var volume = enricher.Process(tick).TradeVolume;
            if (tick.AvailableAt < cutoff)
            {
                opening += volume;
            }
            else if (tick.AvailableAt < windowEnd)
            {
                window += volume;
            }
            else if (tick.AvailableAt < closeEnd)
            {
                close += volume;
            }
        }

        var covered = AdaptiveOpeningCoverage.CoveredMinutes(clean, open, cutoff);
        var windowMinutes = clean.Where(x => x.AvailableAt >= cutoff && x.AvailableAt < windowEnd).Select(x => (int)(x.AvailableAt - cutoff).TotalMinutes).Distinct().Count();
        string Ist(DateTimeOffset t) => t.ToOffset(M1Time.Ist).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return new VolumeHistoryRow(
            day,
            future.Token,
            future.TradingSymbol,
            future.Expiry,
            future.LotSize,
            opening,
            window,
            close,
            opening + window + close,
            covered,
            windowMinutes,
            clean.Count == 0 ? "" : Ist(clean[0].AvailableAt),
            clean.Count == 0 ? "" : Ist(clean[^1].AvailableAt),
            clean.Count);
    }

    private static readonly string[] Header =
    {
        "session_date", "future_token", "future_symbol", "expiry", "lot_size", "opening_volume", "window_volume", "close_volume", "full_day_volume",
        "opening_coverage_minutes", "window_coverage_minutes", "first_tick_ist", "last_tick_ist", "futures_ticks", "baseline_eligible", "contract_changed_vs_previous_session",
    };

    public static string ToCsv(IReadOnlyList<VolumeHistoryRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', Header)).Append('\n');
        string? previousToken = null;
        foreach (var r in rows.OrderBy(x => x.Date))
        {
            sb.Append(string.Join(',',
                r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.FutureToken, r.FutureSymbol, r.Expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.LotSize.ToString(CultureInfo.InvariantCulture), r.OpeningVolume.ToString(CultureInfo.InvariantCulture), r.WindowVolume.ToString(CultureInfo.InvariantCulture),
                r.CloseVolume.ToString(CultureInfo.InvariantCulture), r.FullDayVolume.ToString(CultureInfo.InvariantCulture), r.OpeningCoverageMinutes.ToString(CultureInfo.InvariantCulture),
                r.WindowCoverageMinutes.ToString(CultureInfo.InvariantCulture), r.FirstTickIst, r.LastTickIst, r.FuturesTicks.ToString(CultureInfo.InvariantCulture),
                r.BaselineEligible ? "true" : "false", previousToken is not null && previousToken != r.FutureToken ? "true" : "false")).Append('\n');
            previousToken = r.FutureToken;
        }

        return sb.ToString();
    }

    public static IReadOnlyList<VolumeHistoryRow> ReadCsv(string path)
    {
        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0 || lines[0] != string.Join(',', Header))
        {
            throw new InvalidOperationException($"{path} is not a {FileName} written by this tool (unexpected header).");
        }

        return lines.Skip(1).Select(l =>
        {
            var c = l.Split(',');
            return new VolumeHistoryRow(
                DateOnly.ParseExact(c[0], "yyyy-MM-dd", CultureInfo.InvariantCulture), c[1], c[2], DateOnly.ParseExact(c[3], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                int.Parse(c[4], CultureInfo.InvariantCulture), long.Parse(c[5], CultureInfo.InvariantCulture), long.Parse(c[6], CultureInfo.InvariantCulture), long.Parse(c[7], CultureInfo.InvariantCulture),
                long.Parse(c[8], CultureInfo.InvariantCulture), int.Parse(c[9], CultureInfo.InvariantCulture), int.Parse(c[10], CultureInfo.InvariantCulture), c[11], c[12], long.Parse(c[13], CultureInfo.InvariantCulture));
        }).ToList();
    }

    public static IReadOnlyList<PriorSession> Eligible(IEnumerable<VolumeHistoryRow> rows)
        => rows.Where(x => x.BaselineEligible).Select(x => new PriorSession(x.Date, x.OpeningVolume, x.WindowVolume)).ToList();
}
