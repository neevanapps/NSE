using NiftySignal.Domain.Enums;

namespace NiftySignal.Domain.Configuration;

public sealed class MarketDataOptions
{
    public const string SectionName = "MarketData";
    public MarketDataProvider Provider { get; set; } = MarketDataProvider.FlatTrade;
}
