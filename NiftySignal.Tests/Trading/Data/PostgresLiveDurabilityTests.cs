using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NiftySignal.Tests.Trading.Recovery;
using NiftySignal.Trading.Data;
using NiftySignal.Trading.Recovery;

namespace NiftySignal.Tests.Trading.Data;

/// <summary>
/// What only the real database can prove about a live run: the typed ownership rows and the risk-ledger cache written in the same transaction as each step, detection of
/// disagreement between derived data and the outputs, and recovery after a REAL operating-system process kill (a child process killed without any cleanup, also in the
/// middle of a transaction). Needs the local test database (skipped visibly otherwise).
/// </summary>
[Collection("PostgresTradingTests")]
public class PostgresLiveDurabilityTests
{
    private const string RunId = "live-1";

    private static async Task<(PostgresContractFixture Fixture, LiveEngineRunContractTests.Scenario Scenario)> FullRunAsync()
    {
        var fixture = new PostgresContractFixture();
        var scenario = LiveEngineRunContractTests.XScenario();
        using var run = LiveEngineRun.Open(new BlockingRunStoreAdapter(fixture.Store), "owner-1", LiveHarness.Spec(scenario.Config, scenario.Families, RunId));
        LiveHarness.Deliver(run, scenario.Script.Steps);
        run.Finish();
        await Task.CompletedTask;
        return (fixture, scenario);
    }

    [PostgresFact]
    public async Task TheTypedRowsAndTheRiskCache_AreWrittenWithEveryStep_AndAgreeWithTheOutputs()
    {
        var (fixture, _) = await FullRunAsync();
        using var _f = fixture;

        Assert.Empty(await fixture.Postgres.VerifyDerivedAsync(RunId));
        await using var db = PostgresTestDatabase.NewContext();
        var positions = await db.Positions.Where(p => p.RunId == RunId).OrderBy(p => p.VariantId).ToListAsync();
        Assert.Equal(new[] { "X1", "X2", "X3" }, positions.Select(p => p.VariantId));
        Assert.All(positions, p => Assert.Equal("X", p.StrategyId));
        Assert.All(positions, p => Assert.NotNull(p.ClosedOutputSeq));
        Assert.Equal(new[] { "TradeSl", "MaxHold", "TradeSl" }, positions.Select(p => p.FinalExitReason));
        Assert.Equal(2, positions.Count(p => p.Direction == "Long"));
        Assert.NotNull(positions[1].MaxHoldDeadlineUtcTicks);
        Assert.All(positions, p => Assert.False(string.IsNullOrEmpty(p.MfeText)));
        var steps = await db.Steps.CountAsync(s => s.RunId == RunId);
        Assert.Equal(steps, await db.RiskCheckpoints.CountAsync(r => r.RunId == RunId));
        Assert.Equal(steps, await db.Checkpoints.CountAsync(r => r.RunId == RunId));            // CheckpointEverySteps = 1 in the harness: a state digest per step
        Assert.True(await db.Inputs.CountAsync(i => i.RunId == RunId) > 10);
    }

    [PostgresFact]
    public async Task ThePositionSnapshotCache_ShowsTheOpenPositionsWithRemainingQuantityTp1AndExcursion()
    {
        using var fixture = new PostgresContractFixture();
        var scenario = LiveEngineRunContractTests.XScenario();
        using var run = LiveEngineRun.Open(new BlockingRunStoreAdapter(fixture.Store), "owner-1", LiveHarness.Spec(scenario.Config, scenario.Families, RunId));
        LiveHarness.Deliver(run, scenario.Script.Steps.Take(scenario.AfterTp1));

        await using var db = PostgresTestDatabase.NewContext();
        var rows = await db.PositionSnapshots.Where(r => r.RunId == RunId).OrderBy(r => r.VariantId).ToListAsync();

        Assert.Equal(new[] { "X1", "X2", "X3" }, rows.Select(r => r.VariantId));
        Assert.All(rows, r => Assert.Equal("Open", r.Status));
        Assert.Equal(325, rows[0].RemainingQuantity);
        Assert.Equal("Done", rows[0].Tp1State);
        Assert.Equal(325, rows[0].Tp1FilledQuantity);
        Assert.Equal(650, rows[1].RemainingQuantity);
        Assert.NotNull(rows[0].MfeText);
        Assert.All(rows, r => Assert.True(r.SourceOutputSeq >= 0 && r.SourceStepSeq >= 0));
    }

    [PostgresFact]
    public async Task ADerivedRowThatDisagreesWithTheOutputs_IsDetected_AndRecoveryHaltsVisibly()
    {
        var (fixture, _) = await FullRunAsync();
        using var _f = fixture;

        await using (var connection = new NpgsqlConnection(PostgresTestDatabase.ConnectionString))
        {
            await connection.OpenAsync();
            await using var tamper = new NpgsqlCommand("UPDATE trading.positions SET net_pnl_text = '123456' WHERE run_id = 'live-1' AND variant_id = 'X1'", connection);
            Assert.Equal(1, await tamper.ExecuteNonQueryAsync());
        }

        var problems = await fixture.Postgres.VerifyDerivedAsync(RunId);
        Assert.Contains(problems, p => p.StartsWith("positions: row", StringComparison.Ordinal) && p.Contains("NetPnlText", StringComparison.Ordinal));
        var halt = await Assert.ThrowsAsync<RecoveryHaltException>(() => fixture.Postgres.RequireDerivedConsistentAsync(RunId));
        Assert.Equal(HaltKind.CorruptLedger, halt.Kind);

        await using (var connection = new NpgsqlConnection(PostgresTestDatabase.ConnectionString))
        {
            await connection.OpenAsync();
            await using var fix = new NpgsqlCommand("UPDATE trading.positions SET net_pnl_text = (SELECT net_pnl_text FROM trading.positions WHERE run_id = 'live-1' AND variant_id = 'X3') WHERE run_id = 'live-1' AND variant_id = 'X1'", connection);
            await fix.ExecuteNonQueryAsync();
            await using var risk = new NpgsqlCommand("UPDATE trading.risk_checkpoints SET counters_text = replace(counters_text, 'seq=', 'seq=1') WHERE run_id = 'live-1' AND step_seq = (SELECT max(step_seq) FROM trading.risk_checkpoints WHERE run_id = 'live-1')", connection);
            await risk.ExecuteNonQueryAsync();
        }

        var cache = await fixture.Postgres.VerifyDerivedAsync(RunId);
        Assert.Contains(cache, p => p.StartsWith("risk_checkpoints", StringComparison.Ordinal));
    }

    [PostgresFact]
    public void ARealProcessKill_AtAnyPoint_LeavesAConsistentLedger_AndTheRestartConvergesToTheUninterruptedRun()
    {
        PostgresTestDatabaseClean.CleanAsync().GetAwaiter().GetResult();
        var harness = HarnessDll();
        string[] reference;
        using (var inMemory = new InMemoryContractFixture())
        {
            using var r = LiveEngineRun.Open(new BlockingRunStoreAdapter(inMemory.Store), "ref", TradingCrashHarness.CrashScenario.Spec("reference"));
            TradingCrashHarness.CrashScenario.Deliver(r, TradingCrashHarness.CrashScenario.Steps());
            r.Finish();
            reference = LiveHarness.Payloads(r).ToArray();
        }

        var n = 0;
        foreach (var (point, nth) in new[] { ("BeforeCommit", 2), ("AfterCommit", 3), ("InTransaction", 4), ("InTransaction", 1), ("AfterCommit", 7), ("BeforeCommit", 9), ("InTransaction", 8) })
        {
            var runId = $"kill-{n++}";
            var exit = RunChild(harness, runId, point, nth);
            Assert.NotEqual(0, exit);                                                      // the child never returned normally: it was killed

            using var fixture = new PostgresContractFixture(clean: false);        // the child's data must survive
            var durable = fixture.Store.LoadAsync(runId).GetAwaiter().GetResult();
            LedgerCheck.Verify(durable);                                                   // whatever was committed is a consistent prefix
            if (point == "InTransaction")
            {
                Assert.Equal(nth - 1, durable.Steps.Count);                                // the step that was in the transaction left no trace at all
                Assert.Equal(durable.Steps.Count == 0 ? 0 : durable.Steps[^1].OutputToExclusive, durable.Outputs.Count);
            }

            Assert.Empty(fixture.Postgres.VerifyDerivedAsync(runId).GetAwaiter().GetResult());
            fixture.Postgres.RequireDerivedConsistentAsync(runId).GetAwaiter().GetResult();

            using var survivor = OpenWithRetry(fixture, runId);
            TradingCrashHarness.CrashScenario.Deliver(survivor, TradingCrashHarness.CrashScenario.Steps());
            survivor.Finish();

            Assert.Equal(reference, LiveHarness.Payloads(survivor));
            Assert.Empty(fixture.Postgres.VerifyDerivedAsync(runId).GetAwaiter().GetResult());
        }
    }

    private static LiveEngineRun OpenWithRetry(PostgresContractFixture fixture, string runId)
    {
        // the dead process's lock disappears with its connection; the server may need a moment to notice
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                return LiveEngineRun.Open(new BlockingRunStoreAdapter(fixture.Store), "survivor", TradingCrashHarness.CrashScenario.Spec(runId));
            }
            catch (OwnershipConflictException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static string HarnessDll()
    {
        // the test project references the harness, so building the tests builds it; it runs from its own output folder with its own dependencies
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        var dll = Path.Combine(TradingModelTests.RepoRoot(), "tools", "TradingCrashHarness", "bin", configuration, "net10.0", "TradingCrashHarness.dll");
        Assert.True(File.Exists(dll), $"The crash harness was not built: {dll}");
        return dll;
    }

    private static int RunChild(string harness, string runId, string point, int nth)
    {
        var info = new ProcessStartInfo("dotnet", $"\"{harness}\" {runId} {point} {nth}") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment[PostgresTestDatabase.Variable] = PostgresTestDatabase.ConnectionString!;      // the guarded local test database, passed by environment only
        using var child = Process.Start(info)!;
        var stderr = child.StandardError.ReadToEndAsync();
        child.StandardOutput.ReadToEnd();
        if (!child.WaitForExit(120_000))
        {
            child.Kill();
            throw new TimeoutException("The crash harness did not end.");
        }

        var error = stderr.GetAwaiter().GetResult();
        Assert.True(!error.Contains("Unhandled exception", StringComparison.Ordinal), "The crash harness failed instead of being killed: " + error);
        return child.ExitCode;
    }
}
