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
    public const string ModelVersion = "npt1-m1-v1";

    public static string SessionId(DateOnly tradeDate) => $"{ModelVersion}:{tradeDate:yyyy-MM-dd}";
}
