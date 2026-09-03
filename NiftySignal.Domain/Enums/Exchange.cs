namespace NiftySignal.Domain.Enums;

/// <summary>
/// Exchange segment codes as FlatTrade's Noren-based API represents them
/// (e.g. "NFO" for NSE F&amp;O). Kept broker-shaped rather than abstracted further,
/// since every broker on this platform family uses the same codes.
/// </summary>
public enum Exchange
{
    Nse,
    Nfo,
    Bse,
    Bfo,
    Mcx,
    Cds,
}
