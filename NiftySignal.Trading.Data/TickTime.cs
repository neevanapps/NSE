namespace NiftySignal.Trading.Data;

/// <summary>
/// Timestamp representation (contract section A.19.4). .NET resolves 100 ns ticks; PostgreSQL <c>timestamptz</c> resolves 1 microsecond, so storing
/// a <see cref="DateTimeOffset"/> there would silently drop up to nine ticks and could reorder or merge distinct instants. The AUTHORITATIVE value is
/// therefore the UTC tick count (<c>bigint</c>), which is exact over the whole <see cref="DateTimeOffset"/> range; a <c>timestamptz</c> column is a
/// convenience copy for people and ad-hoc queries only and is produced by <see cref="Convenience"/>.
/// </summary>
public static class TickTime
{
    public const long TicksPerMicrosecond = 10;

    /// <summary>Ticks of 9999-12-31T23:59:59.9999999Z, the last representable instant.</summary>
    public const long MaxTicks = 3_155_378_975_999_999_999L;

    public static long ToTicks(DateTimeOffset value)
        => value.Offset == TimeSpan.Zero ? value.UtcTicks : throw new NotSupportedException($"Only UTC timestamps are stored; got offset {value.Offset}.");

    public static DateTimeOffset FromTicks(long ticks)
        => ticks is >= 0 and <= MaxTicks ? new DateTimeOffset(ticks, TimeSpan.Zero) : throw new ArgumentOutOfRangeException(nameof(ticks));

    /// <summary>The value PostgreSQL <c>timestamptz</c> would keep: truncated (never rounded up) to a whole microsecond.</summary>
    public static DateTimeOffset Convenience(long ticks) => FromTicks(ticks - (ticks % TicksPerMicrosecond));
}
