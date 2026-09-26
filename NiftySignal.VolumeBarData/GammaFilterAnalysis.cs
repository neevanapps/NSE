using System.Globalization;
using System.Text.Json;
using NiftySignal.Domain.Enums;
using NiftySignal.Pricing;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-26: computes each Pattern A/B signal's own real delta/gamma at the moment it fired,
/// instead of treating DTE as a coarse proxy for "how much gamma is in play." Reuses this
/// project's already-validated pricing machinery unchanged (SyntheticForward -> ImpliedVolatilitySolver
/// -> BlackScholes, the exact chain LiveOptionAtmPopulator/OptionSkew25DeltaPopulator already use,
/// same 6.5% risk-free-rate convention) rather than a hand-rolled approximation -- a signal that
/// looked directionally real in the earlier event-study work is worth explaining with the actual
/// options-theory number, not a guess about it. Read-only against the already-exported
/// research-ticks/research-file-run files; never touches the database or any frozen artifact.
/// Outputs one JSON row per P0/P1 signal so the forward-outcome comparison (event-study by gamma
/// tercile instead of by DTE bucket) can be done in the same Python tooling already built and
/// checked for this research thread, rather than re-deriving that analysis a second time here.
/// </summary>
public static class GammaFilterAnalysis
{
    const double RiskFreeRate = 0.065; // Matches LiveOptionAtmPopulator/OptionAtmPopulator/OptionSkew25DeltaPopulator exactly.

    sealed record ManifestRow(string Token, string Exchange, string TradingSymbol, string InstrumentType,
        string OptionType, decimal? StrikePrice, DateOnly? ExpiryDate, string Underlying, int LotSize, decimal TickSize);

    public static async Task<int> RunAsync(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: gamma-filter <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [--in=research-ticks] [--runs=research-file-run] [--out=gamma-filter.json]");
            return 1;
        }
        var from = DateOnly.ParseExact(args[1], "yyyy-MM-dd");
        var to = DateOnly.ParseExact(args[2], "yyyy-MM-dd");
        var ticksDir = args.FirstOrDefault(a => a.StartsWith("--in=", StringComparison.Ordinal)) is { } i ? i[5..] : "research-ticks";
        var runsDir = args.FirstOrDefault(a => a.StartsWith("--runs=", StringComparison.Ordinal)) is { } r ? r[7..] : "research-file-run";
        var outPath = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal)) is { } o ? o[6..] : "gamma-filter.json";

        var rows = new List<object>();
        var skippedNoIv = 0;
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var dayDir = Path.Combine(ticksDir, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var reportPath = Path.Combine(runsDir, $"{date:yyyy-MM-dd}.json");
            if (!Directory.Exists(dayDir) || !File.Exists(reportPath)) { continue; }

            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var root = report.RootElement;
            var expiry = DateOnly.Parse(root.GetProperty("Expiry").GetString()!);

            var manifest = JsonSerializer.Deserialize<List<ManifestRow>>(await File.ReadAllTextAsync(Path.Combine(dayDir, "instruments.json")))!;
            var byToken = manifest.ToDictionary(m => m.Token);
            var byStrikeSide = manifest.Where(m => m.InstrumentType == "Option" && m.StrikePrice is not null)
                .ToDictionary(m => (m.StrikePrice!.Value, m.OptionType), m => m.Token);
            var futureToken = manifest.First(m => m.InstrumentType == "Future").Token;

            var tickCache = new Dictionary<string, List<ReversalResearch.Print>>();
            List<ReversalResearch.Print> LoadTicks(string token)
            {
                if (tickCache.TryGetValue(token, out var cached)) { return cached; }
                var list = new List<ReversalResearch.Print>();
                var path = Path.Combine(dayDir, $"{token}.ndjson");
                if (File.Exists(path))
                {
                    foreach (var line in File.ReadLines(path))
                    {
                        if (line.Length == 0) { continue; }
                        using var doc = JsonDocument.Parse(line);
                        var rr = doc.RootElement;
                        list.Add(new(rr[0].GetInt64(), rr[1].GetDateTimeOffset(), rr[2].GetDateTimeOffset(), rr[3].GetDecimal(),
                            rr[4].GetDecimal(), rr[5].GetDecimal(), rr[6].GetInt64(), rr[7].GetInt64(), rr[8].GetInt64()));
                    }
                }
                tickCache[token] = list;
                return list;
            }

            decimal? Mid(string token, DateTimeOffset time)
            {
                var p = ReversalResearch.Before(LoadTicks(token), time);
                if (p is null) { return null; }
                return p.Bid > 0 && p.Ask >= p.Bid ? (p.Bid + p.Ask) / 2 : p.Price;
            }

            foreach (var expKey in new[] { "P0", "P1" })
            {
                foreach (var s in root.GetProperty("experiments").GetProperty(expKey).GetProperty("signals").EnumerateArray())
                {
                    var token = s.GetProperty("Token").GetString()!;
                    var side = s.GetProperty("Side").GetString()!;
                    var time = s.GetProperty("Time").GetDateTimeOffset();
                    if (!byToken.TryGetValue(token, out var inst) || inst.StrikePrice is null) { continue; }
                    var strike = (double)inst.StrikePrice!.Value;
                    if (!byStrikeSide.TryGetValue((inst.StrikePrice!.Value, "Call"), out var ceToken)
                        || !byStrikeSide.TryGetValue((inst.StrikePrice!.Value, "Put"), out var peToken)) { continue; }

                    var ceMid = Mid(ceToken, time); var peMid = Mid(peToken, time);
                    var tradedMid = Mid(token, time);
                    if (ceMid is null || peMid is null || tradedMid is null || tradedMid <= 0) { continue; }

                    var t = TimeToExpiry.YearsUntilExpiry(expiry, time);
                    var forward = SyntheticForward.Compute([(strike, (double)ceMid, (double)peMid)], t, RiskFreeRate);
                    if (forward is null || forward <= 0) { continue; }

                    var optionType = side == "A" ? OptionType.Put : OptionType.Call;
                    var iv = ImpliedVolatilitySolver.Solve(optionType, (double)tradedMid, forward.Value, strike, t, RiskFreeRate);
                    if (iv is null) { skippedNoIv++; continue; }

                    var greeks = BlackScholes.Calculate(optionType, forward.Value, strike, t, RiskFreeRate, iv.Value).Greeks;
                    rows.Add(new
                    {
                        Date = date, Experiment = expKey, Token = token, Side = side,
                        Dte = expiry.DayNumber - date.DayNumber, Time = time,
                        TimeToExpiryYears = t, ImpliedVol = iv.Value,
                        Delta = greeks.Delta, Gamma = greeks.Gamma, ThetaPerDay = greeks.ThetaPerDay
                    });
                }
            }
        }
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"gamma-filter: {rows.Count} signals with a valid IV/Greeks solve, {skippedNoIv} skipped (IV solve failed -- see ImpliedVolatilitySolver's own null-is-honest convention). Saved {outPath}.");
        return 0;
    }
}
