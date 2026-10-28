namespace NiftySignal.Npt1M1;

/// <summary>IST helpers of the M1 producer (the same arithmetic as the research freeze builder).</summary>
public static class M1Time
{
    public static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    public static DateTimeOffset IstTime(DateOnly day, int h, int m, int s = 0) => new DateTimeOffset(day.ToDateTime(new TimeOnly(h, m, s)), Ist).ToUniversalTime();
}

/// <summary>The futures instrument of a trade date (instrument master facts the M1 freeze needs).</summary>
public sealed record FutureInstrument(string Token, string TradingSymbol, DateOnly Expiry, int LotSize);

/// <summary>Identity of the NPT1 M1 stream. It never shares a model version, session id or key with adaptive-v1.</summary>
public static class M1Identity
{
    // v2 = the spot ATM contract (spot-atm-v1): the engine centers its band on the frozen NIFTY spot, the freeze/fingerprint carry the spot token and the stream reads the spot rows.
    // v1 sessions (synthetic-forward center) are never reinterpreted: the version is part of the session id, so a v1 session and a v2 session can never be confused or share a row.
    public const string ModelVersion = "npt1-m1-v2";

    public static string SessionId(DateOnly tradeDate) => $"{ModelVersion}:{tradeDate:yyyy-MM-dd}";
}
