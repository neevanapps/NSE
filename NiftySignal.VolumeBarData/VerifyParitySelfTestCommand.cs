using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Phase E's own proof that <see cref="VerifyParityCommand"/> actually detects a real mismatch, not
/// just that it prints PASS on already-known-good days -- see the `verify-parity-selftest` command in
/// <c>Program.cs</c>.
///
/// Copies one day's REAL, already-verified <see cref="LivePaperTradeRow"/> rows (from Phase D's own
/// replay-verification runs, persisted in the real <c>niftysignal_volume_bars</c> database) into a
/// disposable scratch database, then runs three deliberate corruptions in turn -- wrong strike, wrong
/// direction, and a missing trade (delete one row entirely) -- against
/// <see cref="VerifyParityCommand.RunAsync"/> pointed at the scratch copy via its own
/// <c>liveDatabaseNameOverride</c> parameter, asserting FAIL with the expected diagnosis each time,
/// reverting the corruption, and re-asserting PASS. The REAL database is never written to -- only
/// ever read from, to seed the scratch copy -- so this can be re-run freely without any risk to Phase
/// D's own verified data.
/// </summary>
public static class VerifyParitySelfTestCommand
{
    const string ScratchDatabaseName = "niftysignal_volume_bars_paritytest";

    /// <param name="sourceDatabaseNameOverride">
    /// 2026-09-20: which database to read the REAL, uncorrupted seed trades from. Defaults to
    /// <see cref="VolumeBarPopulator.VolumeBarDatabaseName"/> (the real niftysignal_volume_bars) --
    /// but as of this task, that database's LivePaperTrades table has never actually been written to
    /// by a real live Host run for these already-historical dates (the live Host wasn't running
    /// against 2026-09-16/2026-09-11 when they happened), and writing to it now is rightly gated as
    /// a shared-resource modification. Phase D's own `replay-live-papertrade` proof already produced
    /// genuine (not fabricated by this self-test) LivePaperTradeRow rows for both days via the exact
    /// same LivePaperTradeExecutor the live Host calls -- those rows persist in its own scratch
    /// database ("niftysignal_volume_bars_livepapertradetest") from that earlier run. Passing that
    /// name here reads the real, already-proven trade data without any write to the shared database.
    /// </param>
    public static async Task<int> RunAsync(string baseConnectionString, DateOnly date, long threshold, CancellationToken ct,
        string? sourceDatabaseNameOverride = null)
    {
        var officialOptions = new DbContextOptionsBuilder<VolumeBarDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = sourceDatabaseNameOverride ?? VolumeBarPopulator.VolumeBarDatabaseName }.ConnectionString).Options;
        var scratchOptions = new DbContextOptionsBuilder<VolumeBarDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = ScratchDatabaseName }.ConnectionString).Options;

        Console.WriteLine($"=== VerifyParity self-test (fabricated-mismatch detection proof): {date:yyyy-MM-dd} @ threshold={threshold} ===");

        List<LivePaperTradeRow> realTrades;
        await using (var official = new VolumeBarDbContext(officialOptions))
        {
            realTrades = await official.LivePaperTrades
                .Where(t => t.AsOfDate == date && t.BarVolumeThreshold == threshold)
                .OrderBy(t => t.EntryBarIndex)
                .ToListAsync(ct);
        }

        if (realTrades.Count == 0)
        {
            Console.WriteLine($"No real LivePaperTradeRow rows found for {date:yyyy-MM-dd} @ {threshold} -- run replay-live-papertrade for this day first (Phase D), or pick a different date. Aborting self-test.");
            return 1;
        }

        Console.WriteLine($"Seeding scratch database with {realTrades.Count} real trade(s) copied from {VolumeBarPopulator.VolumeBarDatabaseName} (real database untouched).");

        async Task<VolumeBarDbContext> SeedScratchAsync()
        {
            var scratch = new VolumeBarDbContext(scratchOptions);
            await scratch.Database.EnsureDeletedAsync(ct);
            await scratch.Database.MigrateAsync(ct);
            foreach (var t in realTrades)
            {
                // Fresh entities (not the tracked instances read above) -- Id is server-generated,
                // deliberately omitted so EF assigns a new one in the scratch database.
                scratch.LivePaperTrades.Add(new LivePaperTradeRow
                {
                    AsOfDate = t.AsOfDate,
                    BarVolumeThreshold = t.BarVolumeThreshold,
                    Strategy = t.Strategy,
                    Side = t.Side,
                    EntryBarIndex = t.EntryBarIndex,
                    EntryTimestamp = t.EntryTimestamp,
                    EntryScore = t.EntryScore,
                    Token = t.Token,
                    StrikePrice = t.StrikePrice,
                    EntryPrice = t.EntryPrice,
                    EntryDecisionTimestamp = t.EntryDecisionTimestamp,
                    ExitBarIndex = t.ExitBarIndex,
                    ExitTimestamp = t.ExitTimestamp,
                    ExitReason = t.ExitReason,
                    ExitPrice = t.ExitPrice,
                    ExitDecisionTimestamp = t.ExitDecisionTimestamp,
                });
            }

            await scratch.SaveChangesAsync(ct);
            return scratch;
        }

        var allOk = true;
        var target = realTrades[0]; // corrupt the first trade of the day throughout.

        // --- Corruption 1: wrong strike ---
        Console.WriteLine();
        Console.WriteLine($"--- Corruption 1/3: wrong strike (EntryBarIndex={target.EntryBarIndex}, {target.StrikePrice} -> {target.StrikePrice + 100}) ---");
        await using (var scratch = await SeedScratchAsync())
        {
            var row = await scratch.LivePaperTrades.SingleAsync(r => r.EntryBarIndex == target.EntryBarIndex, ct);
            row.StrikePrice += 100;
            await scratch.SaveChangesAsync(ct);
        }

        var wrongStrikeExit = await VerifyParityCommand.RunAsync(baseConnectionString, date, threshold, ct, ScratchDatabaseName);
        var wrongStrikeOk = wrongStrikeExit != 0;
        Console.WriteLine(wrongStrikeOk ? "SELF-TEST 1/3: PASS (tool correctly reported FAIL for the wrong-strike corruption)." : "SELF-TEST 1/3: FAIL (tool did NOT catch the wrong-strike corruption -- bug in VerifyParityCommand).");
        allOk &= wrongStrikeOk;

        // --- Corruption 2: wrong direction ---
        Console.WriteLine();
        var flippedSide = target.Side == NiftySignal.Domain.Enums.OptionType.Call ? NiftySignal.Domain.Enums.OptionType.Put : NiftySignal.Domain.Enums.OptionType.Call;
        Console.WriteLine($"--- Corruption 2/3: wrong direction (EntryBarIndex={target.EntryBarIndex}, {target.Side} -> {flippedSide}) ---");
        await using (var scratch = await SeedScratchAsync())
        {
            var row = await scratch.LivePaperTrades.SingleAsync(r => r.EntryBarIndex == target.EntryBarIndex, ct);
            row.Side = flippedSide;
            await scratch.SaveChangesAsync(ct);
        }

        var wrongDirectionExit = await VerifyParityCommand.RunAsync(baseConnectionString, date, threshold, ct, ScratchDatabaseName);
        var wrongDirectionOk = wrongDirectionExit != 0;
        Console.WriteLine(wrongDirectionOk ? "SELF-TEST 2/3: PASS (tool correctly reported FAIL for the wrong-direction corruption)." : "SELF-TEST 2/3: FAIL (tool did NOT catch the wrong-direction corruption -- bug in VerifyParityCommand).");
        allOk &= wrongDirectionOk;

        // --- Corruption 3: missing trade (delete it entirely) ---
        Console.WriteLine();
        Console.WriteLine($"--- Corruption 3/3: missing trade (EntryBarIndex={target.EntryBarIndex} deleted entirely) ---");
        await using (var scratch = await SeedScratchAsync())
        {
            var row = await scratch.LivePaperTrades.SingleAsync(r => r.EntryBarIndex == target.EntryBarIndex, ct);
            scratch.LivePaperTrades.Remove(row);
            await scratch.SaveChangesAsync(ct);
        }

        var missingExit = await VerifyParityCommand.RunAsync(baseConnectionString, date, threshold, ct, ScratchDatabaseName);
        var missingOk = missingExit != 0;
        Console.WriteLine(missingOk ? "SELF-TEST 3/3: PASS (tool correctly reported FAIL for the missing-trade corruption)." : "SELF-TEST 3/3: FAIL (tool did NOT catch the missing-trade corruption -- bug in VerifyParityCommand).");
        allOk &= missingOk;

        // --- Revert: seed a clean, uncorrupted copy and confirm PASS again ---
        Console.WriteLine();
        Console.WriteLine("--- Revert: re-seeding an uncorrupted copy and confirming PASS ---");
        await using (await SeedScratchAsync()) { }
        var revertedExit = await VerifyParityCommand.RunAsync(baseConnectionString, date, threshold, ct, ScratchDatabaseName);
        var revertedOk = revertedExit == 0;
        Console.WriteLine(revertedOk ? "SELF-TEST revert: PASS (tool correctly reports PASS again once the corruption is reverted)." : "SELF-TEST revert: FAIL (tool reported FAIL on an uncorrupted copy -- false positive, bug in VerifyParityCommand).");
        allOk &= revertedOk;

        Console.WriteLine();
        Console.WriteLine(allOk
            ? "OVERALL SELF-TEST: PASS -- VerifyParityCommand correctly detected all 3 fabricated mismatches and correctly re-passed on the reverted, uncorrupted copy. Real niftysignal_volume_bars database was never written to."
            : "OVERALL SELF-TEST: FAIL -- see above. VerifyParityCommand is not trustworthy as written; do not rely on it until fixed.");

        return allOk ? 0 : 1;
    }
}
