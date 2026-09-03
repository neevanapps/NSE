namespace NiftySignal.Pricing;

/// <summary>
/// Standard normal CDF/PDF. .NET has no built-in erf/normal-CDF, so this hand-rolls the
/// Abramowitz &amp; Stegun 7.1.26 rational approximation of erf (max absolute error ~1.5e-7),
/// which is far tighter than the IV solver's own 1e-6 price-convergence tolerance needs.
/// </summary>
public static class NormalDistribution
{
    public static double Cdf(double x) => 0.5 * (1.0 + Erf(x / Math.Sqrt(2.0)));

    public static double Pdf(double x) => Math.Exp(-0.5 * x * x) / Math.Sqrt(2.0 * Math.PI);

    static double Erf(double x)
    {
        var sign = x < 0 ? -1.0 : 1.0;
        x = Math.Abs(x);

        const double A1 = 0.254829592;
        const double A2 = -0.284496736;
        const double A3 = 1.421413741;
        const double A4 = -1.453152027;
        const double A5 = 1.061405429;
        const double P = 0.3275911;

        var t = 1.0 / (1.0 + (P * x));
        var y = 1.0 - ((((((A5 * t) + A4) * t) + A3) * t + A2) * t + A1) * t * Math.Exp(-x * x);

        return sign * y;
    }
}
