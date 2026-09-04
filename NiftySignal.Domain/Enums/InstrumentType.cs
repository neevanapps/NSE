namespace NiftySignal.Domain.Enums;

public enum InstrumentType
{
    Index,
    Future,
    Option,

    /// <summary>India VIX (2026-09-04) -- deliberately not InstrumentType.Index: several call sites resolve "the" Index-type instrument as Spot via .First(...), and a second Index-type row would make that resolution order-dependent instead of explicit.</summary>
    Vix,
}
