namespace NiftySignal.Execution;

public sealed record FillResult(decimal FillPrice, decimal Brokerage, decimal GrossValue, decimal NetValue);
