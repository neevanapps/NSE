using System.Globalization;
using NiftySignal.AdaptiveObserver.Commentary;
using NiftySignal.AdaptiveObserverData;

namespace NiftySignal.Dashboard.Services;

public enum ToastKind { Commentary, TradeOpened, TradeClosed }

/// <summary>A candidate toast built from a durable row (commentary event identity, paper position id). Never contains anything that could be written back anywhere.</summary>
public sealed record ToastCandidate(string Id, ToastKind Kind, DateTimeOffset AtUtc, string Title, IReadOnlyList<string> Lines, string Tone);

/// <summary>
/// Per-browser-circuit toast de-duplication. The FIRST observation of a source only establishes its baseline (nothing already in the database is replayed as a toast); afterwards each durable
/// identity yields at most one toast. The seen-set is bounded (oldest identities are forgotten first), so a long session cannot grow it without limit. UI-only: it holds no reference to any trading
/// or notification path.
/// </summary>
public sealed class ToastTracker(int capacity = 500)
{
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    readonly Queue<string> _order = new();
    readonly HashSet<string> _baselined = new(StringComparer.Ordinal);

    public int SeenCount => _seen.Count;

    /// <summary>Returns the candidates not seen before, oldest first. The first call for a given <paramref name="source"/> returns none (baseline).</summary>
    public IReadOnlyList<ToastCandidate> Observe(string source, IEnumerable<ToastCandidate> candidates)
    {
        var list = candidates.OrderBy(c => c.AtUtc).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        var first = _baselined.Add(source);
        var fresh = new List<ToastCandidate>();
        foreach (var c in list)
        {
            if (!_seen.Add(c.Id))
            {
                continue;
            }

            _order.Enqueue(c.Id);
            while (_order.Count > capacity)
            {
                _seen.Remove(_order.Dequeue());
            }

            if (!first)
            {
                fresh.Add(c);
            }
        }

        return fresh;
    }
}

public static class ToastFactory
{
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    static string Time(DateTimeOffset at) => at.ToOffset(Ist).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Uses the persisted event terminology only (lifecycle, bias, regime, evidence agreement); no probability is invented.</summary>
    public static ToastCandidate FromCommentary(AdaptiveCommentaryEventRow e) => new(
        $"commentary:{e.EventIdentity}", ToastKind.Commentary, e.OccurredAtUtc, "COMMENTARY",
        [Time(e.OccurredAtUtc), $"{e.Lifecycle.ToString().ToUpperInvariant()} → {e.EventBias.ToString().ToUpperInvariant()}", CommentaryRenderer.Title(e.EventType),
            $"Regime {e.MarketRegime.ToString().ToUpperInvariant()}", $"Agreement {e.EvidenceAgreement.ToString().ToUpperInvariant()}"],
        "neutral");

    public static ToastCandidate FromOpen(PaperOpenPosition p) => new(
        $"open:{p.PositionId}", ToastKind.TradeOpened, p.EntryAt, "TRADE OPENED",
        [ResearchFormat.ShortStrategy(p.StrategyId), $"{p.OptionType} {Strike(p.Strike)}", $"₹{p.AvgEntryPrice} x{p.EntryQuantity}"], "info");

    public static ToastCandidate FromOpen(PaperClosedTrade c) => new(
        $"open:{c.PositionId}", ToastKind.TradeOpened, c.EntryAt, "TRADE OPENED",
        [ResearchFormat.ShortStrategy(c.StrategyId), $"{c.OptionType} {Strike(c.Strike)}", $"₹{c.AvgEntryPrice} x{c.Quantity}"], "info");

    public static ToastCandidate FromClosed(PaperClosedTrade c)
    {
        var net = decimal.TryParse(c.Net, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : (decimal?)null;
        var netText = net is { } v ? (v >= 0 ? "+" : "-") + "₹" + Math.Abs(v).ToString("N0", CultureInfo.InvariantCulture) : c.Net;
        return new ToastCandidate(
            $"close:{c.PositionId}", ToastKind.TradeClosed, c.ExitAt, "TRADE CLOSED",
            [ResearchFormat.ShortStrategy(c.StrategyId), $"{c.OptionType} {Strike(c.Strike)}", $"Exit ₹{c.AvgExitPrice}", $"Net {netText}", c.ExitReason],
            net is > 0m ? "profit" : net is < 0m ? "loss" : "info");
    }

    static string Strike(string? s) => string.IsNullOrEmpty(s) ? "—" : s;

    /// <summary>NPT1 ledger rows of the given trade date as toast candidates: opens of open and closed positions, and the closes.</summary>
    public static IEnumerable<ToastCandidate> FromLedger(PaperLiveView? view, DateOnly todayIst)
    {
        if (view is null || view.Status.TradeDate != todayIst)
        {
            yield break;
        }

        foreach (var p in view.Open.Where(p => p.StrategyId.StartsWith("NPT1-", StringComparison.Ordinal)))
        {
            yield return FromOpen(p);
        }

        foreach (var c in view.Closed.Where(c => c.StrategyId.StartsWith("NPT1-", StringComparison.Ordinal)))
        {
            yield return FromOpen(c);
            yield return FromClosed(c);
        }
    }
}
