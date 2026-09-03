using NiftySignal.Domain.Enums;
using NiftySignal.Ingestion.FlatTrade;

namespace NiftySignal.Tests.Ingestion;

public class FlatTradeInstrumentMasterProviderTests
{
    const string SampleCsv = """
        Exchange,Token,Lotsize,Symbol,Tradingsymbol,Instrument,Expiry,Strike,Optiontype
        NFO,26000,65,NIFTY,NIFTY25SEP26C25000,OPTIDX,25-SEP-2026,25000.00,CE
        NFO,26001,65,NIFTY,NIFTY25SEP26P25000,OPTIDX,25-SEP-2026,25000.00,PE
        NFO,26002,65,NIFTY,NIFTY30SEP26FUT,FUTIDX,30-SEP-2026,0.00,
        NFO,69681,30,BANKNIFTY,BANKNIFTY29SEP26P52800,OPTIDX,29-SEP-2026,52800.00,PE
        """;

    [Fact]
    public void ParseNiftyInstruments_FiltersToOnlyTheRequestedUnderlying()
    {
        var asOfDate = new DateOnly(2026, 9, 3);

        var instruments = FlatTradeInstrumentMasterProvider.ParseNiftyInstruments(SampleCsv, "NIFTY", asOfDate);

        Assert.Equal(3, instruments.Count);
        Assert.All(instruments, i => Assert.Equal("NIFTY", i.Underlying));
    }

    [Fact]
    public void ParseNiftyInstruments_MapsAnOptionRowCorrectly()
    {
        var instruments = FlatTradeInstrumentMasterProvider.ParseNiftyInstruments(SampleCsv, "NIFTY", new DateOnly(2026, 9, 3));

        var call = instruments.Single(i => i.Token == "26000");

        Assert.Equal(Exchange.Nfo, call.Exchange);
        Assert.Equal("NIFTY25SEP26C25000", call.TradingSymbol);
        Assert.Equal(InstrumentType.Option, call.InstrumentType);
        Assert.Equal(OptionType.Call, call.OptionType);
        Assert.Equal(25000.00m, call.StrikePrice);
        Assert.Equal(new DateOnly(2026, 9, 25), call.ExpiryDate);
        Assert.Equal(65, call.LotSize);
    }

    [Fact]
    public void ParseNiftyInstruments_MapsAFutureRow_WithNoStrikeOrOptionType()
    {
        var instruments = FlatTradeInstrumentMasterProvider.ParseNiftyInstruments(SampleCsv, "NIFTY", new DateOnly(2026, 9, 3));

        var future = instruments.Single(i => i.Token == "26002");

        Assert.Equal(InstrumentType.Future, future.InstrumentType);
        Assert.Equal(OptionType.None, future.OptionType);
        Assert.Null(future.StrikePrice);
    }

    [Fact]
    public void ParseNiftyInstruments_StampsEveryRowWithTheGivenAsOfDate()
    {
        var asOfDate = new DateOnly(2026, 9, 3);

        var instruments = FlatTradeInstrumentMasterProvider.ParseNiftyInstruments(SampleCsv, "NIFTY", asOfDate);

        Assert.All(instruments, i => Assert.Equal(asOfDate, i.AsOfDate));
    }

    [Fact]
    public void ParseNiftyInstruments_ThrowsWhenTheCsvHeaderShapeHasChanged()
    {
        var csvWithUnexpectedHeader = "Exch,Token,Lot\nNFO,1,1";

        Assert.Throws<InvalidOperationException>(() =>
            FlatTradeInstrumentMasterProvider.ParseNiftyInstruments(csvWithUnexpectedHeader, "NIFTY", new DateOnly(2026, 9, 3)));
    }
}
