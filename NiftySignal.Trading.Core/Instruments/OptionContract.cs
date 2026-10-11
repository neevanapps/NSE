namespace NiftySignal.Trading.Core.Instruments;

public enum OptionSide
{
    Call = 0,
    Put = 1,
}

/// <summary>
/// Contract metadata known at selection time (instrument master of the trade date). <see cref="Token"/> is the stable contract
/// identifier used for ordinal tie-breaking. Strike and expiry are metadata only: they never feed prices or candles.
/// </summary>
public sealed record OptionContract(string Token, OptionSide Side, decimal Strike, DateOnly Expiry, int LotSize, decimal TickSize, string Symbol = "");
