using System.Globalization;

namespace NiftySignal.Dashboard.Services;

public enum IncompleteBarState
{
    /// <summary>No authoritative runtime data (no session, row missing, inconsistent quantity, read failed).</summary>
    Unavailable,
    /// <summary>The runtime row is fresh and the observer is Live: the figures are the builder's current provisional state.</summary>
    Live,
    /// <summary>The runtime heartbeat is older than <see cref="IncompleteBarView.StaleAfter"/>: the figures are the last known state, not current.</summary>
    Stale,
    /// <summary>Fresh row but the observer is calibrating, rebuilding or degraded: the figures are shown but not as live progress.</summary>
    NotLive,
    /// <summary>The session is closed.</summary>
    Closed,
}

/// <summary>
/// What the sticky NIFTY LIVE QUOTE shows for the CURRENT incomplete fixed-volume adaptive bar - provisional, never a completed bar. Built only from the authoritative runtime row that the Host already persists about once
/// a second (<see cref="BarProgress"/>): the bar after the last completed one, the quantity accumulated so far against the target, the bar's start time and the runtime heartbeat / last processed source tick.
/// <para>NOT available through the Dashboard data contract (the Host persists no per-bar OHLC and no trade-update counter for the incomplete bar): open, high, low, last price and trade-update count. They are reported as
/// unavailable (<see cref="UnavailableFields"/>); nothing is synthesized from unrelated quote snapshots.</para>
/// Source timestamps (the heartbeat and the last processed tick) are kept apart from the time the Dashboard evaluated them (<c>now</c>).
/// </summary>
public sealed record IncompleteBarView(
    IncompleteBarState State, int? BarSeq, long? Quantity, long Target, double? Percent,
    DateTimeOffset? StartedAtUtc, TimeSpan? Elapsed, DateTimeOffset? UpdatedAtUtc, TimeSpan? HeartbeatAge, DateTimeOffset? LastSourceAtUtc, string? Reason)
{
    /// <summary>The Host flushes the runtime row about once a second; ten missed seconds is already a stalled or stopped Host.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

    public static readonly IReadOnlyList<string> UnavailableFields = ["Open", "High", "Low", "Last price", "Trade-update count"];

    public const string UnavailableFieldsReason = "not persisted by the Host for the incomplete bar (no OHLC or trade-update counter in adaptive_observer_runtime)";

    public bool ShowsProgress => State is IncompleteBarState.Live or IncompleteBarState.NotLive or IncompleteBarState.Stale && Quantity is not null && Percent is not null;

    public string QuantityText => Quantity is { } q && Percent is { } p
        ? string.Create(CultureInfo.InvariantCulture, $"{q:N0} / {Target:N0} · {p:0.0}%")
        : "unavailable";

    public static IncompleteBarView From(BarProgress? p, DateTimeOffset now)
    {
        if (p is null) return Missing("not loaded");
        if (p.UnavailableReason is { } why) return Missing(why, p);
        var age = p.HeartbeatUtc is { } hb ? now - hb : (TimeSpan?)null;
        if (age is { } a && a < TimeSpan.Zero) age = TimeSpan.Zero;   // clock skew between the Host and the Dashboard is not a negative age
        var seq = p.LastCompletedBarSeq is { } last ? last + 1 : (int?)null;
        var state = p.RuntimeStatus == "Closed" ? IncompleteBarState.Closed
            : age is null || age > StaleAfter ? IncompleteBarState.Stale
            : p.RuntimeStatus == "Live" ? IncompleteBarState.Live
            : IncompleteBarState.NotLive;
        // A live bar is timed against now; a stale or closed one only up to its last heartbeat, so a stopped Host never shows an ever-growing "elapsed".
        var until = state == IncompleteBarState.Live || state == IncompleteBarState.NotLive ? now : p.HeartbeatUtc;
        TimeSpan? elapsed = p.StartedAtUtc is { } s && until is { } u && u >= s ? u - s : null;
        return new(state, seq, p.Quantity, p.Target, p.Percent, p.StartedAtUtc, elapsed, p.HeartbeatUtc, age, p.LastSourceAtUtc,
            state == IncompleteBarState.Stale ? $"runtime not updated for {(age is { } x ? $"{x.TotalSeconds:0}s" : "an unknown time")} - last known state"
            : state == IncompleteBarState.Closed ? "session closed"
            : state == IncompleteBarState.NotLive ? $"observer {p.RuntimeStatus}" : null);
    }

    static IncompleteBarView Missing(string reason, BarProgress? p = null) =>
        new(IncompleteBarState.Unavailable, null, null, p?.Target ?? 0, null, null, null, p?.HeartbeatUtc, null, null, reason);

    public static string Ist(DateTimeOffset? t) => t is { } x ? x.ToOffset(TimeSpan.FromHours(5.5)).ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "—";

    public static string Duration(TimeSpan? d) => d is { } x ? $"{(int)x.TotalMinutes}:{x.Seconds:00}" : "—";
}
