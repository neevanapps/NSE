using System.Text.Json;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Dashboard.Services;

/// <summary>One strike of the frozen weekly universe with the exact CE and PE contracts of that expiry (a side may be missing from the frozen universe).</summary>
public sealed record FixedStrikeContract(double Strike, string? CeToken, string? CeSymbol, string? PeToken, string? PeSymbol);

/// <summary>The strikes the fixed-strike selector offers: the frozen weekly expiry's option universe of the session, read from the session row (no broker call, no tick query).</summary>
public sealed record FixedStrikeCatalog(DateOnly Expiry, IReadOnlyList<FixedStrikeContract> Strikes)
{
    public FixedStrikeContract? Find(double strike) => Strikes.FirstOrDefault(s => s.Strike == strike);

    /// <summary>The offered strike nearest to <paramref name="center"/> (used only to keep a pinned selection valid when the universe does not contain it).</summary>
    public double? Nearest(double center) => Strikes.Count == 0 ? null : Strikes.OrderBy(s => Math.Abs(s.Strike - center)).ThenBy(s => s.Strike).First().Strike;

    public static FixedStrikeCatalog? FromSession(AdaptiveSessionStateRow session)
    {
        if (string.IsNullOrWhiteSpace(session.OptionUniverseJson))
        {
            return null;
        }

        var options = JsonSerializer.Deserialize<List<ObserverOptionInstrument>>(session.OptionUniverseJson);
        if (options is null || options.Count == 0)
        {
            return null;
        }

        var expiry = session.WeeklyOptionExpiry ?? options.Min(o => o.ExpiryDate);
        var strikes = options.Where(o => o.ExpiryDate == expiry).GroupBy(o => o.Strike).OrderBy(g => g.Key)
            .Select(g =>
            {
                var ce = g.FirstOrDefault(o => o.OptionType == OptionType.Call);
                var pe = g.FirstOrDefault(o => o.OptionType == OptionType.Put);
                return new FixedStrikeContract(g.Key, ce?.Token, ce?.TradingSymbol, pe?.Token, pe?.TradingSymbol);
            }).ToList();
        return new FixedStrikeCatalog(expiry, strikes);
    }
}

/// <summary>One displayed completed bar of the fixed strike. A side without a stored row stays null: it is shown as unavailable and NEVER replaced by another strike.</summary>
public sealed record FixedStrikeRow(int BarSeq, DateTimeOffset EndAvailableAtUtc, AdaptiveOptionStrikeBarRow? Ce, AdaptiveOptionStrikeBarRow? Pe,
    RollPrice? CeRoll = null, RollPrice? PeRoll = null);

/// <summary>
/// Rolling price change of ONE exact contract over the last N completed bars: PriceEnd of the current bar minus PriceStart of the first bar of the window. Built only from that contract's own persisted
/// per-strike rows (same session, expiry, strike and token); it never reads the moving ATM±2 band's rolling values.
/// </summary>
public sealed record RollPrice(double Delta, double StartPrice);

public sealed record FixedStrikeHistory(DateOnly Expiry, double Strike, FixedStrikeContract Contract, IReadOnlyList<FixedStrikeRow> Rows);

/// <summary>Pure assembly of the fixed-strike rows: exactly one expiry + strike + CE token + PE token, aligned to the displayed completed bars.</summary>
public static class FixedStrikeProjection
{
    public static FixedStrikeHistory Assemble(
        FixedStrikeCatalog catalog,
        FixedStrikeContract contract,
        IReadOnlyList<(int BarSeq, DateTimeOffset EndAvailableAtUtc)> bars,
        IEnumerable<AdaptiveOptionStrikeBarRow> stored,
        int rollWindowBars = 0)
    {
        // Identity filter: a stored row counts only when it is exactly this expiry, this strike AND this side's token.
        var byBar = stored
            .Where(r => r.Expiry == catalog.Expiry && r.Strike == contract.Strike
                && ((r.Side == OptionType.Call && contract.CeToken is not null && r.Token == contract.CeToken)
                    || (r.Side == OptionType.Put && contract.PeToken is not null && r.Token == contract.PeToken))
                && (r.MetricsVersion == OptionStrikeBarMetrics.MetricsVersion || r.MetricsVersion == OptionStrikeBarMetrics.LegacyMetricsVersion))
            .GroupBy(r => (r.BarSeq, r.Side))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.MetricsVersion == OptionStrikeBarMetrics.MetricsVersion).First());   // latest supported version wins, v1 is the fallback
        // A rolling window is comparable only when EVERY bar of the window has a stored row for this exact side/token; anything less is unavailable (never bridged, never another strike).
        RollPrice? Roll(int seq, OptionType side)
        {
            if (rollWindowBars <= 1 || seq - rollWindowBars + 1 < 1)
            {
                return null;
            }

            for (var k = seq - rollWindowBars + 1; k <= seq; k++)
            {
                if (!byBar.ContainsKey((k, side)))
                {
                    return null;
                }
            }

            var first = byBar[(seq - rollWindowBars + 1, side)].PriceStart;
            var last = byBar[(seq, side)].PriceEnd;
            return first is { } a && last is { } z ? new RollPrice(z - a, a) : null;
        }

        var rows = bars.OrderByDescending(b => b.BarSeq)
            .Select(b => new FixedStrikeRow(
                b.BarSeq,
                b.EndAvailableAtUtc,
                byBar.GetValueOrDefault((b.BarSeq, OptionType.Call)),
                byBar.GetValueOrDefault((b.BarSeq, OptionType.Put)),
                Roll(b.BarSeq, OptionType.Call),
                Roll(b.BarSeq, OptionType.Put)))
            .ToList();
        return new FixedStrikeHistory(catalog.Expiry, contract.Strike, contract, rows);
    }
}

/// <summary>
/// The Options selector's state machine. <b>Auto follow ON</b>: the selected strike follows the authoritative CenterStrike of the latest COMPLETED adaptive bar, and only when a NEW completed bar arrives.
/// <b>Auto follow OFF</b>: the selection is pinned. A manual selection turns Auto follow OFF. Turning it ON again synchronises at once to the latest completed bar's center.
/// Incomplete-bar movement and live spot ticks are not inputs of this class by construction.
/// </summary>
public sealed class FixedStrikeSelection
{
    public bool AutoFollow { get; private set; } = true;

    public double? SelectedStrike { get; private set; }

    public double? CurrentCenter { get; private set; }

    public int? LastCompletedBarSeq { get; private set; }

    /// <summary>A completed bar became available. Returns true when the selected strike changed (the whole grid must then be reloaded for the new strike).</summary>
    public bool OnCompletedBar(int barSeq, double? center)
    {
        if (LastCompletedBarSeq is { } last && barSeq <= last)
        {
            return false;                                   // not a new completed bar
        }

        LastCompletedBarSeq = barSeq;
        if (center is { } c && c > 0d)
        {
            CurrentCenter = c;
        }

        return AutoFollow && CurrentCenter is { } follow && Select(follow);
    }

    /// <summary>The user picked a strike in the dropdown: pin it (Auto follow OFF).</summary>
    public bool SelectManually(double strike)
    {
        AutoFollow = false;
        return Select(strike);
    }

    /// <summary>The user toggled Auto follow. Turning it ON synchronises to the latest completed bar's center immediately; returns true when the selected strike changed.</summary>
    public bool SetAutoFollow(bool on)
    {
        AutoFollow = on;
        return on && CurrentCenter is { } c && Select(c);
    }

    /// <summary>First display (or a new session): start from the center when nothing is selected yet.</summary>
    public bool EnsureInitialized()
    {
        return SelectedStrike is null && CurrentCenter is { } c && Select(c);
    }

    /// <summary>A new trading session: forget everything (the pinned strike of yesterday must not survive).</summary>
    public void Reset()
    {
        AutoFollow = true;
        SelectedStrike = null;
        CurrentCenter = null;
        LastCompletedBarSeq = null;
    }

    bool Select(double strike)
    {
        var changed = SelectedStrike != strike;
        SelectedStrike = strike;
        return changed;
    }
}
