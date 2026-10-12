namespace NiftySignal.Npt1M1.Data;

/// <summary>The frozen M1 session of one trade day: sizing provenance, universe fingerprint and the complete freeze report. One row per day; never updated after insertion.</summary>
public sealed class M1SessionRow
{
    public required string SessionId { get; set; }
    public required DateOnly TradeDate { get; set; }
    public required string ModelVersion { get; set; }
    public required string FreezeFingerprint { get; set; }
    public long BaseBarVolume { get; set; }
    public long OpeningVolume { get; set; }
    public required string BarSizeStatus { get; set; }
    public required string DecisionJson { get; set; }
    public required string ReportJson { get; set; }
    public required string FutureToken { get; set; }
    public DateOnly WeeklyExpiry { get; set; }
    public required DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>One prior session's futures volumes (append-only; a date is computed once from the raw ticks and then only read).</summary>
public sealed class M1VolumeHistoryRow
{
    public required DateOnly TradeDate { get; set; }
    public required string FutureToken { get; set; }
    public required string FutureSymbol { get; set; }
    public DateOnly Expiry { get; set; }
    public int LotSize { get; set; }
    public long OpeningVolume { get; set; }
    public long WindowVolume { get; set; }
    public long CloseVolume { get; set; }
    public long FullDayVolume { get; set; }
    public int OpeningCoverageMinutes { get; set; }
    public int WindowCoverageMinutes { get; set; }
    public required string FirstTickIst { get; set; }
    public required string LastTickIst { get; set; }
    public long FuturesTicks { get; set; }
    public bool BaselineEligible { get; set; }
    public required DateTimeOffset ComputedAtUtc { get; set; }
}

/// <summary>One finalized M1 frame. Immutable: a restart recomputes the frame from the ticks and compares the hash.</summary>
public sealed class M1FrameRow
{
    public required string SessionId { get; set; }
    public int BarSeq { get; set; }
    public required DateTimeOffset StartAvailableAtUtc { get; set; }
    public required DateTimeOffset EndAvailableAtUtc { get; set; }
    public bool Ready { get; set; }
    public bool IsValid { get; set; }
    public int ConsecutiveValidBars { get; set; }
    public double? MicroDevTimeWeighted { get; set; }
    public double? MicroDevUsableSeconds { get; set; }
    public long? PeBandContractStrictDelta { get; set; }
    public double? BandCenterStrike { get; set; }
    public DateOnly? BandExpiry { get; set; }
    public string? BandUnavailableReason { get; set; }
    public required string FrameHash { get; set; }
    public required string CanonicalText { get; set; }
    public required DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>Operational state of the M1 producer for one session (heartbeat, cursors, status). Not market data; recovery never depends on it.</summary>
public sealed class M1RuntimeRow
{
    public required string SessionId { get; set; }
    public required string Status { get; set; }
    public long LastTickId { get; set; }
    public int LastBarSeq { get; set; }
    public DateTimeOffset? LastBarEndUtc { get; set; }
    public DateTimeOffset LastHeartbeatUtc { get; set; }
    public long Polls { get; set; }
    public double LastPollMillis { get; set; }
    public double MaxPollMillis { get; set; }
    public long RowsRead { get; set; }
    public long DivergentFrames { get; set; }
    public string? Detail { get; set; }
}
