using NiftySignal.Domain.Enums;
using NiftySignal.Pricing;

namespace NiftySignal.AdaptiveObserver;

public static class OptionResidualModel
{
    public static OptionResidualAnchor? BuildAnchor(
        DateTimeOffset atUtc,
        DateOnly expiry,
        double future0930,
        IReadOnlyList<ObserverOptionInstrument> chain,
        IReadOnlyDictionary<string, OptionQuoteSnapshot> quotes,
        double riskFreeRate,
        double maxQuoteAgeSeconds = 5d)
    {
        var selection = AdaptiveOptionBandCalculator.Select(
            atUtc, expiry, future0930, chain, quotes, riskFreeRate, maxQuoteAgeSeconds, halfWidth:2);

        if (selection is null)
        {
            return null;
        }

        var t = TimeToExpiry.YearsUntilExpiry(expiry, atUtc);
        var calls = BuildComponents(selection.Calls, selection.CenterStrike, selection.SyntheticUnderlying, atUtc, quotes, t, riskFreeRate, maxQuoteAgeSeconds);
        var puts = BuildComponents(selection.Puts, selection.CenterStrike, selection.SyntheticUnderlying, atUtc, quotes, t, riskFreeRate, maxQuoteAgeSeconds);
        if (calls is null || puts is null)
        {
            return null;
        }

        return new OptionResidualAnchor(
            atUtc,
            expiry,
            future0930,
            selection.SyntheticUnderlying,
            selection.CenterStrike,
            calls,
            puts);
    }

    public static IReadOnlyList<OptionResidualReading> Evaluate(
        OptionResidualAnchor anchor,
        DateTimeOffset asOfUtc,
        double futureNow,
        IReadOnlyDictionary<string, OptionQuoteSnapshot> quotes,
        double riskFreeRate,
        double maxQuoteAgeSeconds = 5d)
    {
        var results = new List<OptionResidualReading>(2);
        foreach (var variant in new[] { ResidualVariant.Atm, ResidualVariant.AtmPlusMinus2 })
        {
            var calls = variant == ResidualVariant.Atm
                ? anchor.Calls.Where(x => x.IsCenterStrike).ToArray()
                : anchor.Calls.ToArray();
            var puts = variant == ResidualVariant.Atm
                ? anchor.Puts.Where(x => x.IsCenterStrike).ToArray()
                : anchor.Puts.ToArray();

            if (calls.Length == 0 || puts.Length == 0)
            {
                continue;
            }

            var modeledUnderlying = anchor.SyntheticUnderlying0930 + (futureNow - anchor.Future0930);
            var t = TimeToExpiry.YearsUntilExpiry(anchor.ExpiryDate, asOfUtc);

            if (!TrySide(calls, OptionType.Call, asOfUtc, modeledUnderlying, t, quotes, riskFreeRate, maxQuoteAgeSeconds,
                    out var ceActual, out var ceExpected, out var ceBase, out var ceMaxAge)
                || !TrySide(puts, OptionType.Put, asOfUtc, modeledUnderlying, t, quotes, riskFreeRate, maxQuoteAgeSeconds,
                    out var peActual, out var peExpected, out var peBase, out var peMaxAge))
            {
                continue;
            }

            var ceActualChange = ceActual - ceBase;
            var ceExpectedChange = ceExpected - ceBase;
            var peActualChange = peActual - peBase;
            var peExpectedChange = peExpected - peBase;
            var ceResidual = ceActual - ceExpected;
            var peResidual = peActual - peExpected;
            var ceResidualPct = 100d * ceResidual / ceBase;
            var peResidualPct = 100d * peResidual / peBase;

            results.Add(new OptionResidualReading(
                variant,
                asOfUtc,
                anchor.CenterStrike,
                futureNow - anchor.Future0930,
                modeledUnderlying,
                ceActual,
                ceExpected,
                ceActualChange,
                ceExpectedChange,
                ceResidual,
                ceResidualPct,
                peActual,
                peExpected,
                peActualChange,
                peExpectedChange,
                peResidual,
                peResidualPct,
                ceResidualPct - peResidualPct,
                (ceResidualPct + peResidualPct) / 2d,
                Math.Max(ceMaxAge, peMaxAge)));
        }

        return results;
    }

    static IReadOnlyList<OptionResidualAnchorComponent>? BuildComponents(
        IReadOnlyList<ObserverOptionInstrument> instruments,
        double center,
        double synthetic,
        DateTimeOffset at,
        IReadOnlyDictionary<string, OptionQuoteSnapshot> quotes,
        double t,
        double rate,
        double maxAge)
    {
        var result = new List<OptionResidualAnchorComponent>();
        foreach (var i in instruments)
        {
            if (!quotes.TryGetValue(i.Token, out var q)
                || q.AvailableAt > at
                || (at-q.AvailableAt).TotalSeconds > maxAge
                || q.Mid is not { } mid)
            {
                return null;
            }

            var iv = AdaptiveResearchPricing.SolveIv(i.OptionType, mid, synthetic, i.Strike, t, rate);
            if (iv is null)
            {
                return null;
            }

            result.Add(new OptionResidualAnchorComponent(
                i, mid, iv.Value, q.AvailableAt, (at-q.AvailableAt).TotalSeconds, i.Strike == center));
        }

        return result;
    }

    static bool TrySide(
        IReadOnlyList<OptionResidualAnchorComponent> components,
        OptionType side,
        DateTimeOffset asOf,
        double modeledUnderlying,
        double t,
        IReadOnlyDictionary<string, OptionQuoteSnapshot> quotes,
        double rate,
        double maxAge,
        out double actual,
        out double expected,
        out double baseline,
        out double maxQuoteAge)
    {
        actual = 0d;
        expected = 0d;
        baseline = 0d;
        maxQuoteAge = 0d;

        foreach (var c in components)
        {
            if (!quotes.TryGetValue(c.Instrument.Token, out var q)
                || q.AvailableAt > asOf
                || (asOf-q.AvailableAt).TotalSeconds > maxAge
                || q.Mid is not { } mid)
            {
                return false;
            }

            actual += mid;
            baseline += c.Price0930;
            expected += AdaptiveResearchPricing.Price(side, modeledUnderlying, c.Instrument.Strike, t, rate, c.ImpliedVolatility0930);
            maxQuoteAge = Math.Max(maxQuoteAge, (asOf-q.AvailableAt).TotalSeconds);
        }

        return baseline > 0d;
    }
}
