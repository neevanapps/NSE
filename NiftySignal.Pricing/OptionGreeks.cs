namespace NiftySignal.Pricing;

/// <summary>
/// Delta/Gamma/Vega/Rho are the raw per-unit-underlying / per-unit-volatility / per-unit-rate
/// sensitivities (i.e. Vega is dPrice/dSigma for a full 1.00 = 100% vol move, the standard
/// Black-Scholes convention, not the "per 1 vol point" convention some trading platforms
/// display). Theta is the exception: reported as decay per calendar day (annualized
/// dPrice/dT divided by 365), since that's what "Theta" means to anyone reading it.
/// </summary>
/// <summary>
/// Vanna (d Delta / d sigma) and CharmPerDay (d Delta / d t, calendar time -- same "per day"
/// convention as ThetaPerDay, not the d Delta / d TimeToExpiry sign) are the second-order
/// dealer-hedging Greeks: Vanna drives futures/spot rehedging when IV moves (2026-09-08),
/// Charm drives it as OTM deltas decay toward zero approaching expiry.
/// </summary>
public readonly record struct OptionGreeks(double Delta, double Gamma, double ThetaPerDay, double Vega, double Rho, double Vanna, double CharmPerDay);
