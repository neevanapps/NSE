using NiftySignal.Domain.Enums;
using NiftySignal.Features;
using NiftySignal.Rules;
using NiftySignal.Scoring;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// Generates clearly-fake-but-realistic-shaped data so the dashboard UI can be built and
/// demonstrated before the live ingestion/scoring/execution pipeline exists (that's
/// blocked on FlatTrade API approval, plus wiring the pipeline into NiftySignal.Host --
/// separate work). Deliberately built against the real domain/scoring/rules types
/// (OptionType, EntryDirection, OiBuildupClassification, CompositeScoreCalculator) rather
/// than throwaway view-model-only shapes, so swapping this service for one backed by
/// NiftySignalDbContext later shouldn't require changing any Razor component.
///
/// Registered as a singleton with its own timer so every connected dashboard viewer sees
/// the same simulated feed, matching how one real shared trading system would behave.
/// </summary>
public sealed class DemoDataService : IDisposable
{
    const decimal AtmStrike = 25000m;
    const int LotSize = 65;
    static readonly string[] ComponentNames = ["OiBuildupNet", "Pcr", "FuturesBasis", "IvSkew", "PriceMomentum", "DepthImbalance"];

    readonly Random _random = new(20260903);
    readonly Timer _timer;
    readonly List<ScoreHistoryPoint> _scoreHistory = [];
    readonly List<ClosedTradeRow> _closedTrades = [];
    readonly Lock _lock = new();

    double _currentScore;
    List<OptionChainRow> _optionChain = [];
    List<PositionRow> _positions = [];

    public event Action? Updated;

    public double CurrentScore { get { lock (_lock) return _currentScore; } }

    public IReadOnlyList<ScoreHistoryPoint> ScoreHistory { get { lock (_lock) return [.. _scoreHistory]; } }

    public IReadOnlyList<ScoreComponentRow> ScoreComponents { get; private set; } = [];

    public IReadOnlyList<OptionChainRow> OptionChain { get { lock (_lock) return _optionChain; } }

    public IReadOnlyList<PositionRow> Positions { get { lock (_lock) return _positions; } }

    public IReadOnlyList<ClosedTradeRow> ClosedTrades { get { lock (_lock) return [.. _closedTrades]; } }

    public IReadOnlyList<MetricWarmUpStatus> WarmUpStatuses { get; }

    public ConnectionStatus Connection { get; private set; } = ConnectionStatus.Connected;

    public DateTimeOffset LastTickAt { get; private set; } = DateTimeOffset.Now;

    public DemoDataService()
    {
        SeedScoreHistory();
        _optionChain = BuildOptionChain();
        _positions = BuildPositions();
        SeedClosedTrades();
        WarmUpStatuses = BuildWarmUpStatuses();

        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    void SeedScoreHistory()
    {
        var now = DateTimeOffset.Now;
        var value = 20.0;
        for (var i = 90; i >= 0; i--)
        {
            value = Math.Clamp(value + ((_random.NextDouble() - 0.48) * 12), -95, 95);
            _scoreHistory.Add(new ScoreHistoryPoint(now.AddSeconds(-i * 15), value));
        }
        _currentScore = value;
        RecomputeComponents();
    }

    void RecomputeComponents()
    {
        // Genuinely runs the real Phase 4 engine against randomized inputs, rather than
        // faking a score number directly -- this demo panel is an honest preview of what
        // CompositeScoreCalculator actually produces.
        var inputs = new ScoreComponentInputs(
            OiBuildupNetZ: NextZ(), PcrZ: NextZ(), FuturesBasisZ: NextZ(),
            IvSkewZ: NextZ(), PriceMomentumZ: NextZ(), DepthImbalanceZ: NextZ());

        var result = CompositeScoreCalculator.Calculate(inputs, ScoreWeights.Default, DateTimeOffset.Now);

        _currentScore = result.Score ?? _currentScore;
        ScoreComponents = [.. result.Components.Select(c =>
            new ScoreComponentRow(c.Name, c.Weight, c.ZScore ?? 0, c.WeightedContribution ?? 0))];
    }

    double NextZ() => Math.Clamp((_random.NextDouble() - 0.5) * 4, -3, 3);

    List<OptionChainRow> BuildOptionChain()
    {
        var rows = new List<OptionChainRow>();
        for (var offset = -5; offset <= 5; offset++)
        {
            var strike = AtmStrike + (offset * 50);
            var isAtm = offset == 0;

            rows.Add(BuildRow(strike, OptionType.Call, isAtm, offset));
            rows.Add(BuildRow(strike, OptionType.Put, isAtm, offset));
        }
        return rows;
    }

    OptionChainRow BuildRow(decimal strike, OptionType type, bool isAtm, int offsetFromAtm)
    {
        // Rough moneyness-driven premium curve -- OTM cheaper, ATM/ITM richer -- just
        // enough shape to look like a real chain, not a pricing model.
        var moneyness = type == OptionType.Call ? -offsetFromAtm : offsetFromAtm;
        var basePremium = Math.Max(15m, 175m + (moneyness * 28m) + (decimal)(_random.NextDouble() * 20 - 10));
        var symbol = $"NIFTY25SEP26{(type == OptionType.Call ? "C" : "P")}{strike:0}";

        return new OptionChainRow(
            TradingSymbol: symbol,
            OptionType: type,
            Strike: strike,
            Ltp: Math.Round(basePremium, 2),
            OpenInterest: _random.Next(40_000, 900_000),
            OiChangePct: Math.Round((_random.NextDouble() - 0.5) * 20, 1),
            ImpliedVolatility: Math.Round(0.12 + (_random.NextDouble() * 0.10), 3),
            DepthImbalance: Math.Round(_random.NextDouble() * 2 - 1, 2),
            Buildup: RandomBuildup(),
            IsAtm: isAtm);
    }

    OiBuildupClassification RandomBuildup() => _random.Next(5) switch
    {
        0 => OiBuildupClassification.LongBuildup,
        1 => OiBuildupClassification.ShortBuildup,
        2 => OiBuildupClassification.LongUnwinding,
        3 => OiBuildupClassification.ShortCovering,
        _ => OiBuildupClassification.Neutral,
    };

    List<PositionRow> BuildPositions() =>
    [
        new("NIFTY25SEP26C25050", EntryDirection.Bullish, 178.50m, 194.20m, LotSize, DateTimeOffset.Now.AddMinutes(-38), false),
        new("NIFTY25SEP26P24950", EntryDirection.Bearish, 165.00m, 158.75m, LotSize, DateTimeOffset.Now.AddMinutes(-12), false),
    ];

    void SeedClosedTrades()
    {
        var now = DateTimeOffset.Now;
        string[] reasons = ["ScoreFlip", "ScoreDecay", "PartialBook", "StopLoss", "TimeStop", "SquareOff"];

        for (var i = 18; i >= 1; i--)
        {
            var entryTime = now.AddHours(-i * 3.1);
            var isWin = _random.NextDouble() > 0.42; // directional option buying: sub-50% win rate is normal (plan section 10)
            var pnl = isWin
                ? (decimal)(_random.NextDouble() * 3500 + 500)
                : -(decimal)(_random.NextDouble() * 1800 + 300);

            _closedTrades.Add(new ClosedTradeRow(
                TradingSymbol: $"NIFTY{(i % 2 == 0 ? "C" : "P")}{AtmStrike + (_random.Next(-4, 5) * 50):0}",
                Direction: i % 2 == 0 ? EntryDirection.Bullish : EntryDirection.Bearish,
                EntryTime: entryTime,
                ExitTime: entryTime.AddMinutes(_random.Next(10, 115)),
                NetPnl: Math.Round(pnl, 2),
                ExitReason: Enum.Parse<ExitReason>(reasons[_random.Next(reasons.Length)])));
        }
    }

    static List<MetricWarmUpStatus> BuildWarmUpStatuses() =>
    [
        new("Depth Imbalance", true, FeatureWindowLengths.DepthImbalance, TimeSpan.Zero),
        new("Price Momentum", true, FeatureWindowLengths.PriceMomentum, TimeSpan.Zero),
        new("PCR", true, FeatureWindowLengths.Pcr, TimeSpan.Zero),
        new("OI Buildup Net", true, FeatureWindowLengths.OiBuildupNet, TimeSpan.Zero),
        new("IV Skew", false, FeatureWindowLengths.IvSkew, TimeSpan.FromMinutes(22)),
        new("Futures Basis", true, FeatureWindowLengths.FuturesBasis, TimeSpan.Zero),
    ];

    void Tick()
    {
        lock (_lock)
        {
            RecomputeComponents();
            _scoreHistory.Add(new ScoreHistoryPoint(DateTimeOffset.Now, _currentScore));
            if (_scoreHistory.Count > 120)
            {
                _scoreHistory.RemoveAt(0);
            }

            _optionChain = [.. _optionChain.Select(r => r with
            {
                Ltp = Math.Max(0.5m, Math.Round(r.Ltp + (decimal)(_random.NextDouble() * 6 - 3), 2)),
                OpenInterest = Math.Max(0, r.OpenInterest + _random.Next(-5000, 5000)),
            })];

            _positions = [.. _positions.Select(p => p with
            {
                CurrentPremium = Math.Max(0.5m, Math.Round(p.CurrentPremium + (decimal)(_random.NextDouble() * 4 - 2), 2)),
            })];

            LastTickAt = DateTimeOffset.Now;
        }

        Updated?.Invoke();
    }

    public void Dispose() => _timer.Dispose();
}
