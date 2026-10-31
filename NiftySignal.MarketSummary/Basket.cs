namespace NiftySignal.MarketSummary;

public enum Moneyness { Itm, Atm, Otm }

public enum GridKind { Atm, Itm, Otm, Total }

/// <summary>One listed strike of the session's frozen weekly expiry with its exact CE / PE tokens (a side may be absent from the frozen universe).</summary>
public sealed record ListedStrike(double Strike, string? CeToken, string? PeToken);

/// <summary>
/// One slot of the 5-strike × CE/PE basket. <see cref="Offset"/> is -2..2 in listed strike steps from the centre. <see cref="Token"/> is null when the slot could not be resolved (reported in
/// <see cref="Basket.Problems"/>); a missing slot is never filled with another strike, expiry or token.
/// </summary>
public sealed record BasketContract(int Offset, double? Strike, bool IsCall, string? Token, Moneyness Moneyness);

/// <summary>The fixed basket: one centre K, one expiry, K-2Δ..K+2Δ, CE and PE (Δ = the actual listed strike interval).</summary>
public sealed record Basket(DateOnly Expiry, double Center, double? Step, IReadOnlyList<BasketContract> Contracts, IReadOnlyList<string> Problems)
{
    public const int HalfWidth = 2;

    /// <summary>Contracts of a summary grid, in a stable order (offset, then CE before PE).</summary>
    public IReadOnlyList<BasketContract> For(GridKind kind) => kind switch
    {
        GridKind.Atm => Contracts.Where(c => c.Offset == 0).ToList(),
        GridKind.Itm => Contracts.Where(c => c.Moneyness == Moneyness.Itm).ToList(),
        GridKind.Otm => Contracts.Where(c => c.Moneyness == Moneyness.Otm).ToList(),
        _ => Contracts,
    };

    /// <summary>Expected contract count of a grid: ATM 2, ITM 4, OTM 4, TOTAL 10 - independent of what could be resolved.</summary>
    public static int Expected(GridKind kind) => kind switch { GridKind.Atm => 2, GridKind.Itm => 4, GridKind.Otm => 4, _ => 10 };
}

public static class BasketResolver
{
    /// <summary>
    /// Classification: CE is ITM below the centre, ATM at it, OTM above; PE is the mirror image (OTM below, ITM above). Always returns the ten slots; unresolved slots have a null token and a
    /// problem line. Δ is the gap to the next listed strike above the centre (the one below if there is none above).
    /// </summary>
    public static Basket Resolve(DateOnly expiry, double center, IReadOnlyList<ListedStrike> listed)
    {
        var problems = new List<string>();
        var byStrike = new Dictionary<double, ListedStrike>();
        foreach (var l in listed) byStrike[l.Strike] = l;
        var sorted = byStrike.Keys.OrderBy(x => x).ToList();
        double? step = null;
        if (byStrike.ContainsKey(center))
        {
            var i = sorted.IndexOf(center);
            if (i + 1 < sorted.Count) step = sorted[i + 1] - center;
            else if (i > 0) step = center - sorted[i - 1];
        }
        else problems.Add($"centre {center:0.##} is not a listed strike of expiry {expiry:yyyy-MM-dd}");

        var contracts = new List<BasketContract>(10);
        for (var off = -Basket.HalfWidth; off <= Basket.HalfWidth; off++)
        {
            double? strike = step is { } d and > 0d ? center + off * d : null;
            ListedStrike? l = strike is { } s && byStrike.TryGetValue(s, out var found) ? found : null;
            if (l is null) problems.Add(strike is { } s2 ? $"strike {s2:0.##} (offset {off:+0;-0;0}) is not listed" : $"offset {off:+0;-0;0} cannot be resolved (no listed interval)");
            foreach (var isCall in new[] { true, false })
            {
                var token = l is null ? null : isCall ? l.CeToken : l.PeToken;
                if (l is not null && token is null) problems.Add($"strike {l.Strike:0.##} has no {(isCall ? "CE" : "PE")} contract");
                var m = off == 0 ? Moneyness.Atm : (off < 0) == isCall ? Moneyness.Itm : Moneyness.Otm;
                contracts.Add(new BasketContract(off, strike, isCall, token, m));
            }
        }

        return new Basket(expiry, center, step, contracts, problems);
    }
}
