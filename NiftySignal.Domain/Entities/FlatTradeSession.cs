namespace NiftySignal.Domain.Entities;

/// <summary>
/// The current FlatTrade session token (plan section 4.3: "store the current session
/// token in the database with its issue timestamp"). Single row (Id=1), same pattern as
/// <see cref="KillSwitchState"/> -- the Dashboard (where the login exchange happens) and
/// the Host (which consumes the token to open the WebSocket) are separate processes, so
/// the database is the shared source of truth rather than a file either process could miss.
/// Null fields mean no successful login has happened yet.
/// </summary>
public sealed class FlatTradeSession
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public string? Token { get; set; }

    public string? ClientId { get; set; }

    /// <summary>UTC. FlatTrade tokens are broker-day-scoped; a token issued before the most recent 5-6 AM IST reset must be treated as stale -- see plan 4.3 ("do not silently retry").</summary>
    public DateTimeOffset? IssuedAt { get; set; }

    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    /// <summary>
    /// FlatTrade's docs (Login Flow section): tokens are valid ~24h and are specifically
    /// cleared between 5-6 AM IST daily -- "same calendar date" isn't quite right (a token
    /// from 2 AM today would wrongly read as fresh), so the real cutoff is today's 6 AM
    /// IST, rolled back a day if it's currently before that. Shared by Host (decides
    /// whether to start ingestion) and Dashboard (shows the login panel's status badge) so
    /// the rule can't drift between the two.
    /// </summary>
    public bool IsValidAt(DateTimeOffset nowUtc) => IssuedAt is { } issuedAt && issuedAt >= TokenResetCutoff(nowUtc);

    static DateTimeOffset TokenResetCutoff(DateTimeOffset nowUtc)
    {
        var nowIst = nowUtc.ToOffset(IstOffset);
        var todayCutoffIst = new DateTimeOffset(nowIst.Year, nowIst.Month, nowIst.Day, 6, 0, 0, IstOffset);
        return nowIst < todayCutoffIst ? todayCutoffIst.AddDays(-1) : todayCutoffIst;
    }
}
