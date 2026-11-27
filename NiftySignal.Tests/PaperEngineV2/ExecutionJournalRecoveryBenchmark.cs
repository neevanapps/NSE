using System.Diagnostics;
using Npgsql;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Frames;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.Tests.Trading.Data;
using Xunit.Abstractions;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>Runs only when <c>NSE_MEMORY_BENCH=1</c> and <c>NSE_TRADING_TEST_DB</c> are set.</summary>
sealed class JournalBenchFactAttribute : FactAttribute
{
    public JournalBenchFactAttribute()
    {
        if (!PostgresTestDatabase.Available || Environment.GetEnvironmentVariable("NSE_MEMORY_BENCH") != "1") Skip = "set NSE_MEMORY_BENCH=1 and NSE_TRADING_TEST_DB";
    }
}

/// <summary>
/// Time and managed memory of the execution service rebuilding a day from its journal. Synthetic: one journaled frame per bar with a full Market Summary metric set. The figures are only comparable between
/// two builds on the same machine; they are not a certification.
/// </summary>
[Collection("PostgresTradingTests")]
public class ExecutionJournalRecoveryBenchmark(ITestOutputHelper output)
{
    [JournalBenchFact]
    public async Task RebuildFromTheJournal_TimeAndManagedMemory()
    {
        var template = FrameFixtures.Build(new MarketStateFrameBuilder(), 1, 130);
        foreach (var bars in new[] { 600, 3_000 })
        {
            double seconds = 0; long peakLive = 0, retained = 0, journalBytes = 0;
            foreach (var sampleLive in new[] { false, true })
            {
                await using var rig = await ExecutionServicePostgresTests.Rig.CreateAsync();
                journalBytes = await SeedAsync(rig.Db, template, bars);
                var (coordinator, _) = rig.New();
                rig.Clock.Now = At(bars).AddSeconds(1);
                var before = GC.GetTotalMemory(true);
                long peak = 0;
                using var stop = new CancellationTokenSource();
                var sampler = sampleLive ? Task.Run(async () => { while (!stop.IsCancellationRequested) { peak = Math.Max(peak, GC.GetTotalMemory(true) - before); await Task.Delay(25); } }) : Task.CompletedTask;
                var watch = Stopwatch.StartNew();
                await coordinator.RunCycleAsync(default);
                watch.Stop();
                await stop.CancelAsync();
                await sampler;
                Assert.Equal(bars, coordinator.Snapshot!.LastBarSeq);
                if (sampleLive) { peakLive = peak; retained = GC.GetTotalMemory(true) - before; }
                else seconds = watch.Elapsed.TotalSeconds;
                GC.KeepAlive(coordinator);
            }

            var line = $"bars={bars} journal={journalBytes / 1048576.0:0.0} MiB rebuild={seconds:0.00} s peakLiveManaged={peakLive / 1048576.0:0.0} MiB retainedAfter={retained / 1048576.0:0.0} MiB";
            output.WriteLine(line);
            if (Environment.GetEnvironmentVariable("NSE_MEMORY_BENCH_OUT") is { Length: > 0 } path) File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    static DateTimeOffset At(int seq) => FrameFixtures.T0.AddSeconds(seq * 6);       // 3,000 bars stay inside one IST day

    static async Task<long> SeedAsync(NpgsqlDataSource db, MarketStateFrame template, int bars)
    {
        long bytes = 0;
        var metrics = template.Metrics().ToArray();
        MarketStateFrame? previous = null;
        await using var connection = await db.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        for (var seq = 1; seq <= bars; seq++)
        {
            var end = At(seq);
            var frame = MarketStateFrame.Create(FrameFixtures.Identity(1, seq, end, duration: 6), template.Basket, metrics,
                template.Quotes.Select(q => q with { AvailableAt = end.AddMilliseconds(-300) }), template.QuoteIssues, new VixAvailability(end.AddSeconds(-2), 2, "Fresh"));
            var input = new ExecutionInput(end.AddSeconds(1), FrameFixtures.Day, 1, frame.Quotes, null, [], SafetyState.Healthy, frame, previous, ExecFixtures.Blocked);
            var payload = JournaledInput.Serialize(input);
            bytes += payload.Length;
            await using var insert = new NpgsqlCommand("INSERT INTO pev2.pev2_execution_journal (trade_date, epoch, at_utc_ticks, payload_json) VALUES ($1,1,$2,$3)", connection, tx);
            insert.Parameters.AddWithValue(FrameFixtures.Day.ToString("yyyy-MM-dd")); insert.Parameters.AddWithValue(input.Now.UtcTicks); insert.Parameters.AddWithValue(payload);
            await insert.ExecuteNonQueryAsync();
            previous = frame;
        }

        await tx.CommitAsync();
        return bytes;
    }
}
