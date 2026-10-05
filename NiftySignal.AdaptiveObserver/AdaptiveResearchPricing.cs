using NiftySignal.Domain.Enums;

namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// Frozen residual-research pricing contract: Gaussian CDF at double precision, zero dividend
/// yield, IV bisection [1e-4,5], 80 iterations, 1e-7 price tolerance, minimum Vega 1e-4.
/// Isolated from legacy pricing so reproducing adaptive research cannot change legacy trades.
/// </summary>
public static class AdaptiveResearchPricing
{
    public static double NormalCdf(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (x >= 8.3) return 1d;
        if (x <= -8.3) return 0d;
        // Integral of exp(-u*u/2), represented as exp(-x*x/2) times a positive series.
        // Unlike an erf polynomial approximation this converges to floating-point precision.
        var a = Math.Abs(x);
        var term = a;
        var sum = a;
        for (var n = 1; n < 200; n++)
        {
            term *= a * a / (2 * n + 1);
            var next = sum + term;
            if (next == sum) break;
            sum = next;
        }
        var area = Math.Exp(-.5 * a * a) * sum / Math.Sqrt(2 * Math.PI);
        return Math.Clamp(.5 + (x < 0 ? -area : area),0d,1d);
    }

    public static double Price(OptionType side, double underlying, double strike, double years, double rate, double vol)
    {
        if (side is not (OptionType.Call or OptionType.Put)) throw new ArgumentOutOfRangeException(nameof(side));
        if (!double.IsFinite(underlying) || !double.IsFinite(strike) || !double.IsFinite(years)
            || !double.IsFinite(rate) || !double.IsFinite(vol) || Math.Min(Math.Min(underlying,strike),Math.Min(years,vol)) <= 0)
            throw new ArgumentOutOfRangeException(nameof(underlying),"Pricing inputs must be finite and S/K/T/vol positive.");
        var sqrtT=Math.Sqrt(years);
        var d1=(Math.Log(underlying/strike)+(rate+.5*vol*vol)*years)/(vol*sqrtT);
        var d2=d1-vol*sqrtT;
        var discount=Math.Exp(-rate*years);
        return side==OptionType.Call ? underlying*NormalCdf(d1)-strike*discount*NormalCdf(d2)
            : strike*discount*NormalCdf(-d2)-underlying*NormalCdf(-d1);
    }

    public static double? SolveIv(OptionType side,double market,double underlying,double strike,double years,double rate)
    {
        if (!double.IsFinite(market) || market<=0 || !double.IsFinite(underlying) || underlying<=0
            || !double.IsFinite(strike) || strike<=0 || !double.IsFinite(years) || years<=0 || !double.IsFinite(rate)) return null;
        var lo=1e-4; var hi=5d;
        if (market<Price(side,underlying,strike,years,rate,lo)-1e-9 || market>Price(side,underlying,strike,years,rate,hi)+1e-9) return null;
        for (var i=0;i<80;i++)
        {
            var mid=(lo+hi)/2;
            var p=Price(side,underlying,strike,years,rate,mid);
            if(Math.Abs(p-market)<1e-7)return Reliable(mid) ? mid : null;
            if(p<market)lo=mid;else hi=mid;
        }
        var candidate=(lo+hi)/2;
        return Reliable(candidate) ? candidate : null;

        bool Reliable(double vol)
        {
            var sqrtT=Math.Sqrt(years);
            var d1=(Math.Log(underlying/strike)+(rate+.5*vol*vol)*years)/(vol*sqrtT);
            return underlying*Math.Exp(-.5*d1*d1)/Math.Sqrt(2*Math.PI)*sqrtT>=1e-4;
        }
    }
}
