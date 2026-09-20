using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.Scoring;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Phase D of docs/LIVE_PARITY_PLAN.md: turns a <see cref="TradingDaySession"/> entry SIGNAL
/// (<see cref="LiveSignalOpened"/>/<see cref="LiveSignalClosed"/>, already scored and gated by Phase
/// C -- direction/timing untouched here) into an actual paper trade -- strike selection, live fill
/// price lookup at decision time, and a persisted <see cref="LivePaperTradeRow"/> with realized P&amp;L.
/// Deliberately stateless (a static class, no instance fields) -- the "one position at a time" rule
/// is enforced by construction already, one level up: <see cref="TradingDaySession"/> itself never
/// opens a second signal while one is open (see <see cref="LiveEntrySignalRow"/>'s own doc comment),
/// so this class's own defensive check in <see cref="OpenAsync"/> should never actually trip in
/// practice -- kept anyway so a caller bug surfaces as a skipped-with-a-log-line trade, not a second
/// silently-opened paper position.
///
/// Strike selection reuses <see cref="AtmStrikeSelector.PickAtm"/> -- the SAME pure function
/// <c>TradeSimulator.cs</c>'s own (backtest) entry logic calls, so there is exactly one
/// strike-selection rule in the codebase. Fill pricing is the one place this deliberately does NOT
/// match the backtest's own <see cref="OptionPriceSeries.PriceAtOrBefore"/> -- see
/// docs/LIVE_PARITY_PLAN.md's "Fill-price policy" section and <see cref="LivePaperTradeRow"/>'s own
/// doc comment for why that's a real, accepted, logged difference rather than a bug.
/// </summary>
public static class LivePaperTradeExecutor
{
    /// <summary>
    /// Opens a paper trade for a just-fired entry signal, or returns null (and logs why, via
    /// <paramref name="log"/>) if no tradeable strike/price could be found -- the signal itself
    /// (<see cref="LiveEntrySignalRow"/>) is never rolled back for that; a signal that couldn't be
    /// traded is still a real signal, just one Phase D couldn't act on.
    /// </summary>
    /// <param name="futurePrice">The entry bar's own ClosePrice -- same value <c>TradeSimulator.cs</c>'s own <c>PickAtm(side, bar.ClosePrice)</c> call uses.</param>
    /// <param name="decisionTimestamp">
    /// When the fill DECISION is made -- real wall-clock time for the live service, or the replay
    /// harness's own simulated poll-checkpoint "now" for the Phase D parity proof. NOT the same as
    /// the signal's own EntryTimestamp (the bar's EndTimestamp) -- see
    /// <see cref="LivePaperTradeRow.EntryDecisionTimestamp"/>'s own doc comment.
    /// </param>
    public static async Task<LivePaperTradeRow?> OpenAsync(
        NiftySignalDbContext source, VolumeBarDbContext volumeBarDb,
        DateOnly asOfDate, long barVolumeThreshold, LiveSignalOpened opened, decimal futurePrice,
        DateTimeOffset decisionTimestamp, Action<string> log, CancellationToken ct)
    {
        // Phase G kill switch (docs/LIVE_PARITY_PLAN.md): checked FIRST, before the strike/price
        // lookups below -- disables NEW entries only. The signal itself (LiveEntrySignalRow, already
        // inserted by the caller before this method runs) is left exactly as it would have been with
        // the switch enabled; only whether a LivePaperTradeRow gets opened for it differs. An
        // already-open trade's own CloseAsync is never gated by this -- see LiveKillSwitchState's own
        // doc comment for the full new-entries-only-vs-halt-everything reasoning.
        var killSwitch = await volumeBarDb.LiveKillSwitchStates.FindAsync([LiveKillSwitchState.SingletonId], ct);
        if (killSwitch is not null && !killSwitch.EntriesEnabled)
        {
            log($"LivePaperTradeExecutor.OpenAsync: kill switch is DISABLED (LiveKillSwitchState.EntriesEnabled=false, last set by '{killSwitch.UpdatedBy}' at {killSwitch.UpdatedAt:O}) -- declining to open a paper trade for {opened.Side} entry signal at BarIndex {opened.EntryBarIndex} for {asOfDate:yyyy-MM-dd}. The signal itself is still recorded and will still exit normally.");
            return null;
        }

        var alreadyOpen = await volumeBarDb.LivePaperTrades.AnyAsync(
            t => t.AsOfDate == asOfDate && t.BarVolumeThreshold == barVolumeThreshold && t.ExitBarIndex == null, ct);
        if (alreadyOpen)
        {
            // Should not happen -- TradingDaySession never opens a second signal while one is open.
            // Defensive only, see this class's own doc comment.
            log($"LivePaperTradeExecutor.OpenAsync: a paper trade is already open for {asOfDate:yyyy-MM-dd} @ {barVolumeThreshold} -- skipping entry signal at BarIndex {opened.EntryBarIndex} (one position at a time).");
            return null;
        }

        var chain = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null)
            .ToListAsync(ct);
        if (chain.Count == 0)
        {
            log($"LivePaperTradeExecutor.OpenAsync: no option instruments found for {asOfDate:yyyy-MM-dd} -- cannot select a strike for BarIndex {opened.EntryBarIndex}.");
            return null;
        }

        // Same "nearest expiry" chain-narrowing TradeSimulator.cs itself uses before calling PickAtm.
        var nearestExpiry = chain.Select(o => o.ExpiryDate!.Value).Min();
        var sameExpiryChain = chain.Where(o => o.ExpiryDate == nearestExpiry).ToList();

        var candidate = AtmStrikeSelector.PickAtm(sameExpiryChain, opened.Side, futurePrice);
        if (candidate is null)
        {
            log($"LivePaperTradeExecutor.OpenAsync: no {opened.Side} instrument found in the nearest-expiry ({nearestExpiry:yyyy-MM-dd}) chain for {asOfDate:yyyy-MM-dd} -- cannot open BarIndex {opened.EntryBarIndex}.");
            return null;
        }

        var entryPrice = await GetLatestTickPriceAsync(source, candidate.Token, decisionTimestamp, ct);
        if (entryPrice is not { } ep || ep <= 0)
        {
            log($"LivePaperTradeExecutor.OpenAsync: no priced tick found for {candidate.Token} at or before {decisionTimestamp:O} -- cannot open BarIndex {opened.EntryBarIndex}.");
            return null;
        }

        var row = new LivePaperTradeRow
        {
            AsOfDate = asOfDate,
            BarVolumeThreshold = barVolumeThreshold,
            Side = opened.Side,
            EntryBarIndex = opened.EntryBarIndex,
            EntryTimestamp = opened.EntryTimestamp,
            EntryScore = opened.EntryScore,
            Token = candidate.Token,
            StrikePrice = candidate.StrikePrice!.Value,
            EntryPrice = ep,
            EntryDecisionTimestamp = decisionTimestamp,
        };

        volumeBarDb.LivePaperTrades.Add(row);
        log($"LivePaperTradeExecutor.OpenAsync: opened {opened.Side} {candidate.StrikePrice} ({candidate.Token}) @ {ep} (decision={decisionTimestamp:O}, bar EndTimestamp={opened.EntryTimestamp:O}) for {asOfDate:yyyy-MM-dd} BarIndex {opened.EntryBarIndex}.");
        return row;
    }

    /// <summary>
    /// Closes the open paper trade matching <paramref name="closed"/>'s own EntryBarIndex -- looks
    /// first among rows already tracked in this call's own change tracker (an open-then-close inside
    /// the same batch is possible on a slow poll or a replay run, same reasoning
    /// <c>LiveOptionsScoreEngine.CloseSignalRowAsync</c> already uses for <see cref="LiveEntrySignalRow"/>),
    /// then falls back to a DB query. No-op (logged) if no matching open trade exists -- e.g. the
    /// originating signal never got a tradeable strike/price in <see cref="OpenAsync"/>.
    /// </summary>
    public static async Task CloseAsync(
        VolumeBarDbContext volumeBarDb, NiftySignalDbContext source,
        DateOnly asOfDate, long barVolumeThreshold, LiveSignalClosed closed,
        DateTimeOffset decisionTimestamp, Action<string> log, CancellationToken ct)
    {
        var tracked = volumeBarDb.ChangeTracker.Entries<LivePaperTradeRow>()
            .Select(e => e.Entity)
            .FirstOrDefault(r => r.AsOfDate == asOfDate && r.BarVolumeThreshold == barVolumeThreshold && r.EntryBarIndex == closed.EntryBarIndex);

        var row = tracked ?? await volumeBarDb.LivePaperTrades.FirstOrDefaultAsync(
            r => r.AsOfDate == asOfDate && r.BarVolumeThreshold == barVolumeThreshold && r.EntryBarIndex == closed.EntryBarIndex, ct);

        if (row is null)
        {
            // Expected in the one case OpenAsync itself already logs (no tradeable strike/price at
            // entry time) -- the signal closed, but there was never a paper trade to close.
            log($"LivePaperTradeExecutor.CloseAsync: no open paper trade found for {asOfDate:yyyy-MM-dd} BarIndex {closed.EntryBarIndex} -- nothing to close (likely never opened, see OpenAsync's own log line).");
            return;
        }

        var exitPrice = await GetLatestTickPriceAsync(source, row.Token, decisionTimestamp, ct);
        row.ExitBarIndex = closed.ExitBarIndex;
        row.ExitTimestamp = closed.ExitTimestamp;
        row.ExitReason = closed.ExitReason;
        row.ExitDecisionTimestamp = decisionTimestamp;
        // Never fabricate a price -- if no priced tick exists at/before the exit decision time
        // (should not happen for an instrument that already filled on entry), fall back to the
        // entry price itself (a flat/zero-P&L close) rather than leaving ExitPrice null, mirroring
        // TradeSimulator.cs's own `?? position.EntryPrice` fallback for the identical edge case.
        row.ExitPrice = exitPrice ?? row.EntryPrice;

        log($"LivePaperTradeExecutor.CloseAsync: closed {row.Side} {row.StrikePrice} ({row.Token}) @ {row.ExitPrice} (reason={closed.ExitReason}, decision={decisionTimestamp:O}) for {asOfDate:yyyy-MM-dd} BarIndex {closed.EntryBarIndex} -- NetPnlPoints={row.NetPnlPoints}.");
    }

    /// <summary>
    /// The live fill-price mechanism itself (docs/LIVE_PARITY_PLAN.md's "Fill-price policy"):
    /// the most recent priced tick for <paramref name="token"/> AT OR BEFORE
    /// <paramref name="decisionTimestamp"/> -- never a fabricated price, and never a peek past the
    /// decision instant. This looks structurally similar to <see cref="OptionPriceSeries.PriceAtOrBefore"/>
    /// (both are "latest tick at or before a timestamp" queries), but the POLICY difference the plan
    /// calls for is in which timestamp is passed in, not the query shape: the backtest passes the
    /// bar's own EndTimestamp (an exact historical instant, looked up long after the fact, against a
    /// fully preloaded day's tick series); live passes the REAL decision instant (wall-clock "now" at
    /// the live service, always later than the bar's own EndTimestamp by however long
    /// scoring/polling took), queried fresh against the database rather than a preloaded cache. That
    /// gap is exactly what produces the accepted, logged price difference from the backtest's own
    /// fill -- never hidden, since both <see cref="LivePaperTradeRow.EntryDecisionTimestamp"/> and
    /// <see cref="LivePaperTradeRow.EntryTimestamp"/> (the bar's own) are persisted on every trade.
    /// </summary>
    public static async Task<decimal?> GetLatestTickPriceAsync(NiftySignalDbContext source, string token, DateTimeOffset decisionTimestamp, CancellationToken ct) =>
        await source.Ticks
            .Where(t => t.Token == token && t.ExchangeTimestamp <= decisionTimestamp && t.LastPrice > 0)
            .OrderByDescending(t => t.ExchangeTimestamp)
            .Select(t => (decimal?)t.LastPrice)
            .FirstOrDefaultAsync(ct);
}
