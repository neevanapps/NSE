using NiftySignal.Domain;
using NiftySignal.Trading.Core.Scheduling;

namespace NiftySignal.Trading.Core.Session;

/// <summary>
/// Versioned session policy (hashed as part of <see cref="Config.TradingConfig"/>). Wall times in Asia/Kolkata, which has
/// no daylight saving; the fixed +05:30 offset is taken from <see cref="IstTime.Offset"/> so no machine time-zone database
/// is consulted. No date is stored here: the hash identifies the policy, not a day.
/// </summary>
public sealed record SessionConfig
{
    public const string KolkataTimeZoneId = "Asia/Kolkata";

    public string TimeZoneId { get; init; } = KolkataTimeZoneId;

    /// <summary>First instant at which entries are eligible (inclusive). Confirmed requirement: 09:30:00.</summary>
    public TimeOnly EntryOpen { get; init; } = new(9, 30, 0);

    /// <summary>First instant at which entries are no longer eligible (exclusive); also the waiting-entry invalidation and pending-entry cancellation deadline. Confirmed: 15:00:00.</summary>
    public TimeOnly EntryClose { get; init; } = new(15, 0, 0);

    /// <summary>
    /// Start of forced liquidation. <b>Not assumed:</b> null (no liquidation-start boundary) unless explicitly configured. The
    /// contract's 15:05:00 is a PLACEHOLDER proposal, not an approved trading rule, and is deliberately not defaulted here.
    /// </summary>
    public TimeOnly? LiquidationStart { get; init; }

    /// <summary>Target for every position to be flat; the final-close deadline fires here. Confirmed requirement: 15:10:00.</summary>
    public TimeOnly FinalClose { get; init; } = new(15, 10, 0);

    /// <summary>End of the session in engine terms (adaptive hard close today, 15:35:00). Last session-boundary deadline; reporting of unresolved positions happens here.</summary>
    public TimeOnly SessionEnd { get; init; } = new(15, 35, 0);

    /// <summary>
    /// The PaperLiveV1 session: entries from 09:30:00 (inclusive) to 15:00:00 (exclusive); mandatory liquidation of every remaining position starts at 15:10:00;
    /// FinalClose coincides with the hard session end 15:35:00, the explicit boundary at which a position that still has no executable bid is reported unresolved.
    /// </summary>
    public static SessionConfig PaperLiveV1() => new() { LiquidationStart = new TimeOnly(15, 10, 0), FinalClose = new TimeOnly(15, 35, 0), SessionEnd = new TimeOnly(15, 35, 0) };

    /// <summary>
    /// The NPT1 PROSPECTIVE live session (<c>npt1-live1500-v1</c>): entries fill from 09:30:00 (inclusive) to 15:00:00 (exclusive); at 15:00:00 the terminal-close protocol of the bar-clocked
    /// variants starts (no generic 15:10 liquidation exists for them); the hard end 15:35:00 reports a position that never found a bid as unresolved.
    /// </summary>
    public static SessionConfig Npt1Live1500() => new() { EntryClose = new TimeOnly(15, 0, 0), LiquidationStart = new TimeOnly(15, 0, 0), FinalClose = new TimeOnly(15, 35, 0), SessionEnd = new TimeOnly(15, 35, 0) };

    /// <summary>
    /// The NPT1 HISTORICAL-PARITY session (<c>npt1-hist1100-v1</c>), for verifying the implementation against the frozen research timing ONLY: entries until 11:00, exits continue, the terminal objective
    /// is 15:00 (closed at the last completed bar ending at or before it). Never used live.
    /// </summary>
    public static SessionConfig Npt1HistoricalParity1100() => new() { EntryClose = new TimeOnly(11, 0, 0), LiquidationStart = new TimeOnly(15, 0, 0), FinalClose = new TimeOnly(15, 35, 0), SessionEnd = new TimeOnly(15, 35, 0) };

    /// <summary>Returns human-readable validation errors (empty when valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!string.Equals(TimeZoneId, KolkataTimeZoneId, StringComparison.Ordinal))
        {
            errors.Add($"TimeZoneId '{TimeZoneId}' is not supported; only '{KolkataTimeZoneId}'.");
        }

        if (EntryClose <= EntryOpen)
        {
            errors.Add("EntryClose must be after EntryOpen.");
        }

        if (FinalClose <= EntryClose)
        {
            errors.Add("FinalClose must be after EntryClose.");
        }

        if (SessionEnd < FinalClose)
        {
            errors.Add("SessionEnd must not be before FinalClose.");
        }

        if (LiquidationStart is { } liquidation && (liquidation <= EntryOpen || liquidation >= FinalClose))
        {
            errors.Add("LiquidationStart, when configured, must be after EntryOpen and before FinalClose.");
        }

        return errors;
    }
}

public enum SessionBoundaryKind
{
    EntryClose = 0,
    LiquidationStart = 1,
    FinalClose = 2,
    SessionEnd = 3,
}

public enum SessionPhase
{
    BeforeEntryWindow = 0,
    EntryWindow = 1,
    AfterEntryClose = 2,
    AfterFinalClose = 3,
    Ended = 4,
}

/// <summary>
/// A <see cref="SessionConfig"/> bound to one trade date: the UTC instants and the eligibility/phase functions. Pure.
/// </summary>
public sealed class SessionSchedule
{
    public const string BoundaryKeyPrefix = "session:";

    private SessionSchedule(SessionConfig config, DateOnly tradeDate)
    {
        Config = config;
        TradeDate = tradeDate;
        DayStartUtc = ToUtc(TimeOnly.MinValue);
        EntryOpenUtc = ToUtc(config.EntryOpen);
        EntryCloseUtc = ToUtc(config.EntryClose);
        LiquidationStartUtc = config.LiquidationStart is { } l ? ToUtc(l) : null;
        FinalCloseUtc = ToUtc(config.FinalClose);
        SessionEndUtc = ToUtc(config.SessionEnd);
    }

    public SessionConfig Config { get; }

    public DateOnly TradeDate { get; }

    public DateTimeOffset DayStartUtc { get; }

    public DateTimeOffset EntryOpenUtc { get; }

    public DateTimeOffset EntryCloseUtc { get; }

    public DateTimeOffset? LiquidationStartUtc { get; }

    public DateTimeOffset FinalCloseUtc { get; }

    public DateTimeOffset SessionEndUtc { get; }

    public static SessionSchedule Create(SessionConfig config, DateOnly tradeDate)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = config.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid session config: " + string.Join(" ", errors), nameof(config));
        }

        return new SessionSchedule(config, tradeDate);
    }

    /// <summary>Entry eligibility: EntryOpen &lt;= decisionTime &lt; EntryClose (inclusive start, exclusive end).</summary>
    public bool IsEntryEligible(DateTimeOffset decisionTime) => decisionTime >= EntryOpenUtc && decisionTime < EntryCloseUtc;

    public SessionPhase PhaseAt(DateTimeOffset time)
    {
        if (time < EntryOpenUtc)
        {
            return SessionPhase.BeforeEntryWindow;
        }

        if (time < EntryCloseUtc)
        {
            return SessionPhase.EntryWindow;
        }

        if (time < FinalCloseUtc)
        {
            return SessionPhase.AfterEntryClose;
        }

        return time < SessionEndUtc ? SessionPhase.AfterFinalClose : SessionPhase.Ended;
    }

    /// <summary>Boundary instants in firing order. The liquidation start is present only when configured.</summary>
    public IReadOnlyList<(SessionBoundaryKind Kind, DateTimeOffset Time)> Boundaries()
    {
        var list = new List<(SessionBoundaryKind, DateTimeOffset)> { (SessionBoundaryKind.EntryClose, EntryCloseUtc) };
        if (LiquidationStartUtc is { } liquidation)
        {
            list.Add((SessionBoundaryKind.LiquidationStart, liquidation));
        }

        list.Add((SessionBoundaryKind.FinalClose, FinalCloseUtc));
        list.Add((SessionBoundaryKind.SessionEnd, SessionEndUtc));
        return list.OrderBy(x => x.Item2.UtcTicks).ThenBy(x => (int)x.Item1).ToList();
    }

    /// <summary>Registers every boundary as a <see cref="DeadlineClass.SessionBoundary"/> deadline (global, key "session:&lt;Kind&gt;").</summary>
    public IReadOnlyList<DeadlineHandle> ScheduleBoundaries(IEngineScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        var handles = new List<DeadlineHandle>();
        foreach (var (kind, time) in Boundaries())
        {
            handles.Add(scheduler.Schedule(time, DeadlineClass.SessionBoundary, ScheduledDeadline.GlobalOrdinal, BoundaryKey(kind)));
        }

        return handles;
    }

    public static string BoundaryKey(SessionBoundaryKind kind) => BoundaryKeyPrefix + kind;

    public static bool TryParseBoundaryKey(string key, out SessionBoundaryKind kind)
    {
        kind = default;
        return key.StartsWith(BoundaryKeyPrefix, StringComparison.Ordinal)
            && key.Length > BoundaryKeyPrefix.Length
            && char.IsAsciiLetter(key[BoundaryKeyPrefix.Length])
            && Enum.TryParse(key.AsSpan(BoundaryKeyPrefix.Length), ignoreCase: false, out kind)
            && Enum.IsDefined(kind);
    }

    private DateTimeOffset ToUtc(TimeOnly wallTime)
        => new DateTimeOffset(TradeDate.ToDateTime(wallTime), IstTime.Offset).ToUniversalTime();
}
