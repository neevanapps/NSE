using System.Diagnostics;
using NiftySignal.Trading.Data;
using NiftySignal.Trading.Recovery;
using TradingCrashHarness;

// args: <runId> <BeforeCommit|AfterCommit|InTransaction> <n>
var runId = args[0];
var point = args[1];
var nth = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
var connection = Environment.GetEnvironmentVariable("NSE_TRADING_TEST_DB") ?? throw new InvalidOperationException("NSE_TRADING_TEST_DB is not set.");
var builder = new Npgsql.NpgsqlConnectionStringBuilder(connection);
if (builder.Host is not ("localhost" or "127.0.0.1" or "::1") || builder.Database != "niftysignal_trading_test")
{
    throw new InvalidOperationException("The crash harness only runs against the local niftysignal_trading_test database.");
}

var store = new PostgresRunStore(connection);
var commits = 0;
if (point == "InTransaction")
{
    store.FaultHook = (op, at) =>
    {
        if (op == StoreOperation.Commit && at == StoreFaultPoint.DuringApply && ++commits == nth)
        {
            Process.GetCurrentProcess().Kill();
        }
    };
}

var crash = point == "InTransaction" ? null : new Crash(point == "BeforeCommit" ? CrashPoint.BeforeCommit : CrashPoint.AfterCommit, nth);
using var run = LiveEngineRun.Open(new BlockingRunStoreAdapter(store), "child", CrashScenario.Spec(runId), crash);
CrashScenario.Deliver(run, CrashScenario.Steps());
run.Finish();
Console.WriteLine("SURVIVED");
return 0;

internal sealed class Crash(CrashPoint point, int nth) : ICrashPoints
{
    private int _seen;

    public void Reached(CrashPoint reached, long stepSeq, string detail)
    {
        if (reached == point && ++_seen == nth)
        {
            Process.GetCurrentProcess().Kill();
        }
    }
}
