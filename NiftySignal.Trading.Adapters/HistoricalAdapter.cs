namespace NiftySignal.Trading.Adapters;

public sealed record HistoricalOptions
{
    /// <summary>Rows offered to the translator between two group releases. Affects memory and latency only, never the result (tested for several sizes).</summary>
    public int PageSize { get; init; } = 5_000;
}

/// <summary>
/// Historical adapter (contract section A.16.2): feeds recorded rows, in source id order, through the shared translator and the trading engine. The
/// input is an <see cref="IEnumerable{T}"/> that the caller streams (for the database harness: keyset-paged, read-only), so a session is never
/// materialised here: the translator buffers only the rows that are not yet stable, the loop remembers only a bounded window of delivered events,
/// and the recorders keep digests. There are no heartbeats; deadlines fire when the next group arrives and at the explicit horizon.
/// </summary>
public static class HistoricalAdapter
{
    public static RunResult Run(TradingRun run, IEnumerable<SourceTick> rows, HistoricalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(rows);
        var pageSize = (options ?? new HistoricalOptions()).PageSize;
        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PageSize must be at least 1.");
        }

        var inPage = 0;
        foreach (var row in rows)
        {
            run.Offer(row);
            if (++inPage >= pageSize)
            {
                run.NotePage(inPage);
                run.Pump(heartbeat: false);
                inPage = 0;
            }
        }

        if (inPage > 0)
        {
            run.NotePage(inPage);
            run.Pump(heartbeat: false);
        }

        return run.Finish();
    }
}
