using System.Globalization;
using Microsoft.Extensions.Configuration;
using NiftySignal.PaperEngineV2.Alerts;
using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.ExecutionService;

/// <summary>
/// Settings under <c>Execution:</c>. Disabled unless the owner turns it on; an enabled service with an invalid value refuses to start. The defaults are the approved or documented starting values.
/// </summary>
public sealed record ExecutionServiceOptions
{
    public const string SectionName = "Execution";

    public bool Enabled { get; init; }
    public string? StrategyDirectory { get; init; }
    public string? CostScheduleVersion { get; init; }
    public decimal? BrokeragePerOrder { get; init; }
    public SessionTimes Times { get; init; } = SessionTimes.Default;
    /// <summary>After this IST time a position that is still open is reported Unresolved.</summary>
    public TimeOnly UnresolvedAfter { get; init; } = new(15, 25);
    public TimeSpan MaxProcessingLag { get; init; } = TimeSpan.FromSeconds(5);
    public int FramesToClearGap { get; init; } = 3;
    /// <summary>An Observer heartbeat older than this blocks entries (approved starting value).</summary>
    public TimeSpan ObserverHeartbeatStale { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Extra time before a stale heartbeat also flattens open positions (approved starting value: 0).</summary>
    public TimeSpan FlattenGrace { get; init; } = TimeSpan.Zero;
    /// <summary>Flatten when the Observer has been not Live (while heartbeating) for longer than this; zero = off.</summary>
    public TimeSpan FlattenWhenNotLiveAfter { get; init; } = TimeSpan.Zero;
    /// <summary>No tick of any watched instrument for this long blocks entries (starting value).</summary>
    public TimeSpan MarketDataStale { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>A protective or session exit still unfilled after this is alerted (starting value).</summary>
    public TimeSpan ExitStuckAfter { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public int TickBatchSize { get; init; } = 20_000;
    /// <summary>Journal rows read and replayed at a time when the state is rebuilt (a memory bound only; the result does not depend on it).</summary>
    public int JournalReplayBatchSize { get; init; } = 200;
    public TimeSpan StartupQuoteLookback { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Attempts before a published bar that cannot be assembled is skipped (it then shows as a gap and entries stay blocked).</summary>
    public int FrameBuildAttempts { get; init; } = 20;
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan AlertReminder { get; init; } = TimeSpan.FromMinutes(15);
    public bool LiveMarksEnabled { get; init; } = true;
    public AlertOptions Alerts { get; init; } = AlertOptions.AllOff;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        void Check(bool ok, string text) { if (!ok) errors.Add("Execution:" + text); }
        Check(!string.IsNullOrWhiteSpace(StrategyDirectory), "StrategyDirectory is required");
        Check(CostScheduleVersion is "npt1-frozen-fees-v1" or "nse-index-options-2026-04-01-provisional", "CostScheduleVersion must be npt1-frozen-fees-v1 or nse-index-options-2026-04-01-provisional");
        Check(BrokeragePerOrder is >= 0m, "BrokeragePerOrder is required (costs are never assumed zero)");
        errors.AddRange(Times.Validate().Select(e => "Execution:Times " + e));
        Check(UnresolvedAfter > Times.LiquidationDeadline, "UnresolvedAfter must be later than the liquidation deadline");
        Check(MaxProcessingLag > TimeSpan.Zero, "MaxProcessingLagSeconds must be positive");
        Check(FramesToClearGap >= 1, "FramesToClearGap must be >= 1");
        Check(ObserverHeartbeatStale >= TimeSpan.FromSeconds(5) && ObserverHeartbeatStale <= TimeSpan.FromMinutes(5), "ObserverHeartbeatStaleSeconds must be in [5, 300]");
        Check(FlattenGrace >= TimeSpan.Zero && FlattenGrace <= TimeSpan.FromMinutes(5), "FlattenGraceSeconds must be in [0, 300]");
        Check(FlattenWhenNotLiveAfter >= TimeSpan.Zero, "FlattenWhenNotLiveAfterSeconds must be >= 0");
        Check(MarketDataStale > TimeSpan.Zero, "MarketDataStaleSeconds must be positive");
        Check(PollInterval >= TimeSpan.FromMilliseconds(50) && PollInterval <= TimeSpan.FromSeconds(2), "PollIntervalMilliseconds must be in [50, 2000]");
        Check(TickBatchSize is >= 100 and <= 200_000, "TickBatchSize must be in [100, 200000]");
        Check(JournalReplayBatchSize is >= 1 and <= 5_000, "JournalReplayBatchSize must be in [1, 5000]");
        Check(FrameBuildAttempts >= 1, "FrameBuildAttempts must be >= 1");
        return errors;
    }

    /// <summary>Reads the section. A present but unreadable value throws: a typo never silently becomes a default.</summary>
    public static ExecutionServiceOptions Bind(IConfiguration configuration)
    {
        var s = configuration.GetSection(SectionName);
        var d = new ExecutionServiceOptions();
        T Value<T>(string key, T fallback, Func<string, T> parse) => s[key] is { Length: > 0 } text ? parse(text) : fallback;
        int Int(string text) => int.Parse(text, CultureInfo.InvariantCulture);
        TimeSpan Seconds(string key, TimeSpan fallback) => Value(key, fallback, t => TimeSpan.FromSeconds(Int(t)));
        TimeOnly Time(string key, TimeOnly fallback) => Value(key, fallback, t => TimeOnly.Parse(t, CultureInfo.InvariantCulture));
        bool Flag(string key, bool fallback) => Value(key, fallback, bool.Parse);
        return d with
        {
            Enabled = Flag("Enabled", false),
            StrategyDirectory = s["StrategyDirectory"],
            CostScheduleVersion = s["CostScheduleVersion"],
            BrokeragePerOrder = s["BrokeragePerOrder"] is { Length: > 0 } b ? decimal.Parse(b, CultureInfo.InvariantCulture) : null,
            Times = new SessionTimes(Time("EntryCutoff", SessionTimes.Default.EntryCutoff), Time("LiquidationStart", SessionTimes.Default.LiquidationStart), Time("LiquidationDeadline", SessionTimes.Default.LiquidationDeadline)),
            UnresolvedAfter = Time("UnresolvedAfter", d.UnresolvedAfter),
            MaxProcessingLag = Seconds("MaxProcessingLagSeconds", d.MaxProcessingLag),
            FramesToClearGap = Value("FramesToClearGap", d.FramesToClearGap, Int),
            ObserverHeartbeatStale = Seconds("ObserverHeartbeatStaleSeconds", d.ObserverHeartbeatStale),
            FlattenGrace = Seconds("FlattenGraceSeconds", d.FlattenGrace),
            FlattenWhenNotLiveAfter = Seconds("FlattenWhenNotLiveAfterSeconds", d.FlattenWhenNotLiveAfter),
            MarketDataStale = Seconds("MarketDataStaleSeconds", d.MarketDataStale),
            ExitStuckAfter = Seconds("ExitStuckAfterSeconds", d.ExitStuckAfter),
            PollInterval = Value("PollIntervalMilliseconds", d.PollInterval, t => TimeSpan.FromMilliseconds(Int(t))),
            TickBatchSize = Value("TickBatchSize", d.TickBatchSize, Int),
            JournalReplayBatchSize = Value("JournalReplayBatchSize", d.JournalReplayBatchSize, Int),
            StartupQuoteLookback = Seconds("StartupQuoteLookbackSeconds", d.StartupQuoteLookback),
            FrameBuildAttempts = Value("FrameBuildAttempts", d.FrameBuildAttempts, Int),
            LiveMarksEnabled = Flag("LiveMarksEnabled", d.LiveMarksEnabled),
            Alerts = new AlertOptions(Flag("Alerts:Opened", false), Flag("Alerts:Closed", false), Flag("Alerts:OrderRejected", false), Flag("Alerts:PartialFill", false), Flag("Alerts:ExecutionFailure", false)),
        };
    }
}
