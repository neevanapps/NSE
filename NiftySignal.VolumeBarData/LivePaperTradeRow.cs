using System.ComponentModel.DataAnnotations.Schema;
using NiftySignal.Domain.Enums;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// One live PAPER TRADE for <c>OptionsScoreThreeWaySwitchMaxPainConfirmed</c> -- Phase D of
/// docs/LIVE_PARITY_PLAN.md, the layer <see cref="LiveEntrySignalRow"/>'s own doc comment says is
/// deliberately out of scope for Phase C ("no strike, no fill price, no P&amp;L"). One row per trade's
/// whole lifecycle (inserted on entry with exit fields null, updated in place on exit), same
/// single-row-per-lifecycle shape <see cref="LiveEntrySignalRow"/> already established, plus the real
/// option leg and fill/P&amp;L fields on top. <see cref="EntryBarIndex"/> ties a paper trade back to its
/// originating <see cref="LiveEntrySignalRow"/> 1:1 (same AsOfDate/BarVolumeThreshold/EntryBarIndex
/// identity) -- deliberately a SEPARATE table rather than widening <see cref="LiveEntrySignalRow"/>
/// itself, since a signal can in principle fire with no tradeable instrument/price found (see
/// <see cref="LivePaperTradeExecutor.OpenAsync"/>'s own null-propagation), which would otherwise force
/// every field below to be nullable on a row whose whole point is "here is what we actually traded."
///
/// Fill-price policy (docs/LIVE_PARITY_PLAN.md's own "Fill-price policy" section, a real, accepted
/// difference from the backtest -- not a bug): <see cref="EntryPrice"/>/<see cref="ExitPrice"/> are
/// each the LATEST available live tick price for the traded instrument as of that decision's own
/// <see cref="EntryDecisionTimestamp"/>/<see cref="ExitDecisionTimestamp"/> -- never a lookback search
/// against the full day's tick log the way the offline backtest's own
/// <see cref="OptionPriceSeries.PriceAtOrBefore"/> works. Both the decision timestamp AND the price
/// used are stored for every fill specifically so a timing-driven price difference from the offline
/// backtest's own trade is diagnosable at a glance, distinct from an actual scoring/strike/direction
/// bug (see docs/LIVE_PARITY_PLAN.md Phase D section for the measured deltas).
/// </summary>
public sealed class LivePaperTradeRow
{
    public long Id { get; set; }

    public required DateOnly AsOfDate { get; set; }

    public required long BarVolumeThreshold { get; set; }

    /// <summary>Call = long a call, Put = long a put -- same convention <see cref="LiveEntrySignalRow.Side"/>/<see cref="VolumeBarTrade.Side"/> already use.</summary>
    public required OptionType Side { get; set; }

    /// <summary>Ties this trade 1:1 back to the <see cref="LiveEntrySignalRow"/> that triggered it (same AsOfDate/BarVolumeThreshold/EntryBarIndex identity).</summary>
    public required int EntryBarIndex { get; set; }

    public required DateTimeOffset EntryTimestamp { get; set; }

    /// <summary>The scaled (-100..100) score that fired this entry -- matches <see cref="LiveEntrySignalRow.EntryScore"/>/<see cref="VolumeBarTrade.EntryScore"/>'s own scale.</summary>
    public required double EntryScore { get; set; }

    /// <summary>The traded option's own token (matches <see cref="Domain.Entities.Instrument.Token"/>) -- the literal instrument, not just the strike/side.</summary>
    public required string Token { get; set; }

    public required decimal StrikePrice { get; set; }

    public required decimal EntryPrice { get; set; }

    /// <summary>When the entry fill DECISION was made (live wall-clock time the paper-trade engine acted, or the simulated poll-checkpoint "now" in a replay run) -- see this class's own doc comment on the fill-price policy. Distinct from <see cref="EntryTimestamp"/> (the SIGNAL bar's own EndTimestamp), which can be earlier if scoring/polling introduced latency.</summary>
    public required DateTimeOffset EntryDecisionTimestamp { get; set; }

    /// <summary>Null while the trade is still open.</summary>
    public int? ExitBarIndex { get; set; }

    public DateTimeOffset? ExitTimestamp { get; set; }

    /// <summary>"ScoreInvalidated"/"TimeCutoff"/"EndOfDay" -- the same 3 reasons <see cref="LiveEntrySignalRow.ExitReason"/> uses (this trade closes in lockstep with its originating signal).</summary>
    public string? ExitReason { get; set; }

    public decimal? ExitPrice { get; set; }

    /// <summary>See <see cref="EntryDecisionTimestamp"/>'s own doc comment -- the exit fill's own decision timestamp.</summary>
    public DateTimeOffset? ExitDecisionTimestamp { get; set; }

    /// <summary><see cref="ExitPrice"/> minus <see cref="EntryPrice"/> -- points on the traded option's own price, no lot size, no transaction costs, always a long position -- same convention <see cref="VolumeBarTrade.NetPnlPoints"/> already uses, for direct comparability against the offline backtest's own trades. Null while open. Not a mapped column (computed on read).</summary>
    [NotMapped]
    public decimal? NetPnlPoints => ExitPrice is { } exit ? exit - EntryPrice : null;
}
