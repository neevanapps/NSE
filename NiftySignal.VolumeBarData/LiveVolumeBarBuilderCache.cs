using NiftySignal.Features;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Live performance fix (docs/LIVE_PARITY_PLAN.md, "Live performance incident: future-side bar
/// builder was not incremental"): a per-process, cross-poll cache of the single
/// <see cref="VolumeBarBuilder"/> in progress for "today" at a given <c>barVolumeThreshold</c>, so
/// <see cref="LiveVolumeBarPopulator"/> stops re-scanning and re-feeding EVERY future tick since
/// market open through a brand-new builder on every poll -- exactly the same shape
/// <see cref="LiveOptionSeriesCache"/> already established for the option-side series (see that
/// class's own doc comment): an <c>existing</c>-parameter-based incremental continuation that falls
/// back cleanly to a full reload when <c>existing</c> is null.
///
/// <see cref="VolumeBarBuilder"/> itself already carries its own partial-bar accumulator state
/// internally (private fields -- see that class) and needs no changes to "resume": simply continuing
/// to call <see cref="VolumeBarBuilder.ApplyTick"/> on the SAME instance across polls, feeding it only
/// ticks it has not seen yet, is by itself a correct incremental continuation. What it does NOT track
/// on its own is (a) the running bar-index counter (external bookkeeping the offline/live populators
/// already own, not the builder's concern) and (b) exactly which tick it last consumed -- both are
/// tracked here, alongside the builder instance, as a <see cref="State"/>.
///
/// The last-consumed tick boundary is tracked as (ExchangeTimestamp, Id), not timestamp alone --
/// ticks can share an ExchangeTimestamp (multiple updates in the same millisecond), and the existing
/// full-replay ordering (<c>OrderBy(ExchangeTimestamp).ThenBy(Id)</c>) is only unambiguous once Id
/// tie-breaks it; resuming after a wrong boundary would either replay a tick twice or, worse for
/// bar-index consistency, silently swallow one whose timestamp exactly matches the boundary.
///
/// Registered as a Singleton and owned by <see cref="NiftySignal.Host.LiveVolumeBarWriter"/> alone --
/// same single-writer, no-lock-needed reasoning already documented on <see cref="LiveOptionSeriesCache"/>
/// and <c>TradingDaySession</c>. NOT thread-safe by construction.
///
/// Restart-safety: a Host restart loses this Singleton entirely (never persisted) -- the first poll
/// after restart finds no cached state for today and <see cref="LiveVolumeBarPopulator"/> falls back
/// to its original full-day-replay path, then caches the resulting builder for every later poll to
/// resume from -- identical in spirit to <see cref="LiveOptionSeriesCache"/>'s own restart story.
/// </summary>
public sealed class LiveVolumeBarBuilderCache
{
    DateOnly? _cachedDate;
    readonly Dictionary<long, State> _byThreshold = new();

    /// <summary>Diagnostic only -- whether a resumable builder is currently cached for (asOfDate, barVolumeThreshold), so a replay/perf harness can report cache state without reaching into private fields.</summary>
    public bool HasState(DateOnly asOfDate, long barVolumeThreshold) => _cachedDate == asOfDate && _byThreshold.ContainsKey(barVolumeThreshold);

    /// <summary>
    /// Returns the cached, still-warm state for today's (asOfDate, barVolumeThreshold), or null if
    /// there is none (cold start, restart, or a day boundary just crossed -- see
    /// <see cref="ResetIfNewDay"/>). Null tells the caller to fall back to a full-day replay, exactly
    /// like a null <c>existing</c> tells <see cref="OptionQuoteSeries.LoadAsync(OptionQuoteSeries?,NiftySignal.Persistence.NiftySignalDbContext,string,DateTimeOffset,DateTimeOffset,CancellationToken)"/>.
    /// </summary>
    public State? Get(DateOnly asOfDate, long barVolumeThreshold)
    {
        ResetIfNewDay(asOfDate);
        return _byThreshold.GetValueOrDefault(barVolumeThreshold);
    }

    /// <summary>Stores (or replaces) the resumable state for (asOfDate, barVolumeThreshold) after a poll has advanced it.</summary>
    public void Set(DateOnly asOfDate, long barVolumeThreshold, State state)
    {
        ResetIfNewDay(asOfDate);
        _byThreshold[barVolumeThreshold] = state;
    }

    /// <summary>Discards the cached state for (asOfDate, barVolumeThreshold) -- used once a day is finalized, since the day's builder must never be fed a tick again (see <see cref="LiveVolumeBarPopulator"/>'s own finalizeDay handling).</summary>
    public void Clear(DateOnly asOfDate, long barVolumeThreshold)
    {
        ResetIfNewDay(asOfDate);
        _byThreshold.Remove(barVolumeThreshold);
    }

    /// <summary>
    /// A new trading day means yesterday's cached builder(s) are for the wrong day entirely --
    /// discard them rather than let a stale threshold-keyed entry from a prior day silently continue
    /// answering for today. Same reasoning as <see cref="LiveOptionSeriesCache"/>'s own
    /// ResetIfNewDay -- mirrors <c>TradingDaySession</c>'s own day-scoped lifecycle (docs/LIVE_PARITY_PLAN.md
    /// Phase C).
    /// </summary>
    void ResetIfNewDay(DateOnly asOfDate)
    {
        if (_cachedDate == asOfDate)
        {
            return;
        }

        _cachedDate = asOfDate;
        _byThreshold.Clear();
    }

    /// <summary>
    /// One (AsOfDate, BarVolumeThreshold)'s resumable state: the <see cref="VolumeBarBuilder"/>
    /// instance itself (carrying its own in-progress partial bar), the running bar-index counter
    /// (how many completed bars this builder has produced since day start -- NOT the same as
    /// <c>existingMaxIndex</c>, which only reflects what has been persisted so far), and the
    /// (ExchangeTimestamp, Id) boundary of the last tick already fed into the builder.
    /// </summary>
    public sealed class State
    {
        public required VolumeBarBuilder Builder { get; init; }
        public required int NextBarIndex { get; set; }
        public required DateTimeOffset LastTickTimestamp { get; set; }
        public required long LastTickId { get; set; }
    }
}
