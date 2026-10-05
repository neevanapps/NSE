using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.AdaptiveObserverData.Migrations
{
    /// <inheritdoc />
    public partial class InitialAdaptiveObserver : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "adaptive_sessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TradeDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ModelVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceBranch = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceCommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BuildUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsHistoricalSeed = table.Column<bool>(type: "boolean", nullable: false),
                    FutureToken = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FutureSymbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FutureExpiry = table.Column<DateOnly>(type: "date", nullable: false),
                    LotSize = table.Column<int>(type: "integer", nullable: false),
                    RiskFreeRate = table.Column<double>(type: "double precision", nullable: false),
                    OpeningWindowStartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OpeningWindowEndUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OpeningVolume = table.Column<long>(type: "bigint", nullable: false),
                    EstimatorName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EstimatorIntercept = table.Column<double>(type: "double precision", nullable: false),
                    EstimatorSlope = table.Column<double>(type: "double precision", nullable: false),
                    TargetBarsPerDay = table.Column<int>(type: "integer", nullable: false),
                    RoundingLots = table.Column<int>(type: "integer", nullable: false),
                    EstimatedFullDayVolume = table.Column<double>(type: "double precision", nullable: false),
                    BaseBarVolume = table.Column<long>(type: "bigint", nullable: false),
                    RollingWindowBars = table.Column<int>(type: "integer", nullable: false),
                    RollingWindowVolume = table.Column<long>(type: "bigint", nullable: false),
                    StrongQuantile = table.Column<double>(type: "double precision", nullable: false),
                    StrongThreshold = table.Column<double>(type: "double precision", nullable: true),
                    StrongThresholdPriorStateCount = table.Column<int>(type: "integer", nullable: false),
                    WeeklyOptionExpiry = table.Column<DateOnly>(type: "date", nullable: true),
                    Future0930 = table.Column<double>(type: "double precision", nullable: true),
                    SyntheticWeeklyUnderlying0930 = table.Column<double>(type: "double precision", nullable: true),
                    ResidualCenterStrike = table.Column<double>(type: "double precision", nullable: true),
                    ResidualAnchorCompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "adaptive_future_bars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    BarSeq = table.Column<int>(type: "integer", nullable: false),
                    StartAvailableAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndAvailableAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FirstSourceTickId = table.Column<long>(type: "bigint", nullable: false),
                    LastSourceTickId = table.Column<long>(type: "bigint", nullable: false),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Open = table.Column<double>(type: "double precision", nullable: false),
                    High = table.Column<double>(type: "double precision", nullable: false),
                    Low = table.Column<double>(type: "double precision", nullable: false),
                    Close = table.Column<double>(type: "double precision", nullable: false),
                    BarPriceDisplacement = table.Column<double>(type: "double precision", nullable: false),
                    Vwap = table.Column<double>(type: "double precision", nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false),
                    TradeUpdates = table.Column<int>(type: "integer", nullable: false),
                    StrictBuyVolume = table.Column<long>(type: "bigint", nullable: false),
                    StrictSellVolume = table.Column<long>(type: "bigint", nullable: false),
                    StrictUnknownVolume = table.Column<long>(type: "bigint", nullable: false),
                    StrictDelta = table.Column<long>(type: "bigint", nullable: false),
                    StrictDeltaRatioTotal = table.Column<double>(type: "double precision", nullable: false),
                    StrictCoverage = table.Column<double>(type: "double precision", nullable: false),
                    EnrichedBuyVolume = table.Column<long>(type: "bigint", nullable: false),
                    EnrichedSellVolume = table.Column<long>(type: "bigint", nullable: false),
                    EnrichedUnknownVolume = table.Column<long>(type: "bigint", nullable: false),
                    EnrichedDelta = table.Column<long>(type: "bigint", nullable: false),
                    EnrichedDeltaRatio = table.Column<double>(type: "double precision", nullable: false),
                    OiOpen = table.Column<long>(type: "bigint", nullable: true),
                    OiClose = table.Column<long>(type: "bigint", nullable: true),
                    OiChange = table.Column<long>(type: "bigint", nullable: true),
                    Bid = table.Column<double>(type: "double precision", nullable: true),
                    Ask = table.Column<double>(type: "double precision", nullable: true),
                    BidQty = table.Column<long>(type: "bigint", nullable: true),
                    AskQty = table.Column<long>(type: "bigint", nullable: true),
                    Spread = table.Column<double>(type: "double precision", nullable: true),
                    BookImbalance = table.Column<double>(type: "double precision", nullable: true),
                    Microprice = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_future_bars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_future_bars_adaptive_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "adaptive_observer_runtime",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    RuntimeStatus = table.Column<int>(type: "integer", nullable: false),
                    LastHeartbeatUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastProcessedSourceAvailableAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastProcessedSourceTickId = table.Column<long>(type: "bigint", nullable: true),
                    LastCompletedBarSeq = table.Column<int>(type: "integer", nullable: false),
                    CurrentPartialBarVolume = table.Column<long>(type: "bigint", nullable: false),
                    CurrentPartialBarStartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRecoveryStartedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRecoveryCompletedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRecoveryReconciledBars = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_observer_runtime", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_observer_runtime_adaptive_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "adaptive_option_band_bars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    BarSeq = table.Column<int>(type: "integer", nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    CenterStrike = table.Column<double>(type: "double precision", nullable: false),
                    BandStrikes = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    BandRolled = table.Column<bool>(type: "boolean", nullable: false),
                    BandAvailable = table.Column<bool>(type: "boolean", nullable: false),
                    UnavailableReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    StartAvailableAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndAvailableAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    BandPremiumIndexOpen = table.Column<double>(type: "double precision", nullable: true),
                    BandPremiumIndexClose = table.Column<double>(type: "double precision", nullable: true),
                    BarBandPriceChange = table.Column<double>(type: "double precision", nullable: true),
                    BarBandReturnPct = table.Column<double>(type: "double precision", nullable: true),
                    RollingBandPriceChange = table.Column<double>(type: "double precision", nullable: true),
                    RollingReturnPct = table.Column<double>(type: "double precision", nullable: true),
                    RollingEfficiency = table.Column<double>(type: "double precision", nullable: true),
                    ContractTotalQuantity = table.Column<long>(type: "bigint", nullable: false),
                    ContractStrictBuy = table.Column<long>(type: "bigint", nullable: false),
                    ContractStrictSell = table.Column<long>(type: "bigint", nullable: false),
                    ContractStrictUnknown = table.Column<long>(type: "bigint", nullable: false),
                    ContractStrictDelta = table.Column<long>(type: "bigint", nullable: false),
                    ContractStrictDeltaRatioTotal = table.Column<double>(type: "double precision", nullable: false),
                    ContractStrictCoverage = table.Column<double>(type: "double precision", nullable: false),
                    ContractEnrichedBuy = table.Column<long>(type: "bigint", nullable: false),
                    ContractEnrichedSell = table.Column<long>(type: "bigint", nullable: false),
                    ContractEnrichedUnknown = table.Column<long>(type: "bigint", nullable: false),
                    ContractEnrichedDelta = table.Column<long>(type: "bigint", nullable: false),
                    ContractEnrichedDeltaRatio = table.Column<double>(type: "double precision", nullable: false),
                    ContractRollingStrictDelta = table.Column<long>(type: "bigint", nullable: true),
                    ContractRollingStrictAbsDeltaChange = table.Column<long>(type: "bigint", nullable: true),
                    ContractRollingStrictDeltaRatioTotal = table.Column<double>(type: "double precision", nullable: true),
                    ContractRollingEnrichedDelta = table.Column<long>(type: "bigint", nullable: true),
                    ContractRollingEnrichedDeltaChange = table.Column<long>(type: "bigint", nullable: true),
                    ContractRollingEnrichedDeltaRatio = table.Column<double>(type: "double precision", nullable: true),
                    NotionalTotal = table.Column<double>(type: "double precision", nullable: false),
                    NotionalStrictBuy = table.Column<double>(type: "double precision", nullable: false),
                    NotionalStrictSell = table.Column<double>(type: "double precision", nullable: false),
                    NotionalStrictUnknown = table.Column<double>(type: "double precision", nullable: false),
                    NotionalStrictDelta = table.Column<double>(type: "double precision", nullable: false),
                    NotionalStrictDeltaRatioTotal = table.Column<double>(type: "double precision", nullable: false),
                    NotionalStrictCoverage = table.Column<double>(type: "double precision", nullable: false),
                    NotionalEnrichedBuy = table.Column<double>(type: "double precision", nullable: false),
                    NotionalEnrichedSell = table.Column<double>(type: "double precision", nullable: false),
                    NotionalEnrichedUnknown = table.Column<double>(type: "double precision", nullable: false),
                    NotionalEnrichedDelta = table.Column<double>(type: "double precision", nullable: false),
                    NotionalEnrichedDeltaRatio = table.Column<double>(type: "double precision", nullable: false),
                    NotionalRollingStrictDelta = table.Column<double>(type: "double precision", nullable: true),
                    NotionalRollingStrictAbsDeltaChange = table.Column<double>(type: "double precision", nullable: true),
                    NotionalRollingStrictDeltaRatioTotal = table.Column<double>(type: "double precision", nullable: true),
                    NotionalRollingEnrichedDelta = table.Column<double>(type: "double precision", nullable: true),
                    NotionalRollingEnrichedDeltaChange = table.Column<double>(type: "double precision", nullable: true),
                    NotionalRollingEnrichedDeltaRatio = table.Column<double>(type: "double precision", nullable: true),
                    BarTradeUpdates = table.Column<long>(type: "bigint", nullable: false),
                    RollingTradeUpdates = table.Column<long>(type: "bigint", nullable: true),
                    ContractRollingStrictCoverage = table.Column<double>(type: "double precision", nullable: true),
                    NotionalRollingStrictCoverage = table.Column<double>(type: "double precision", nullable: true),
                    ContractRollingActivityPerSecond = table.Column<double>(type: "double precision", nullable: true),
                    NotionalRollingActivityPerSecond = table.Column<double>(type: "double precision", nullable: true),
                    BandOiOpen = table.Column<long>(type: "bigint", nullable: true),
                    BandOiClose = table.Column<long>(type: "bigint", nullable: true),
                    BarOiChange = table.Column<long>(type: "bigint", nullable: true),
                    BarOiChangePct = table.Column<double>(type: "double precision", nullable: true),
                    RollingOiChange = table.Column<long>(type: "bigint", nullable: true),
                    RollingOiChangePct = table.Column<double>(type: "double precision", nullable: true),
                    PremiumNotionalOiAtClose = table.Column<double>(type: "double precision", nullable: true),
                    PremiumNotionalOiChange = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_option_band_bars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_option_band_bars_adaptive_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "adaptive_option_residual_bars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    BarSeq = table.Column<int>(type: "integer", nullable: false),
                    Variant = table.Column<int>(type: "integer", nullable: false),
                    EndAvailableAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CenterStrike = table.Column<double>(type: "double precision", nullable: false),
                    FutureChangeFrom0930 = table.Column<double>(type: "double precision", nullable: false),
                    ModeledWeeklyUnderlying = table.Column<double>(type: "double precision", nullable: false),
                    CEActual = table.Column<double>(type: "double precision", nullable: false),
                    CEExpected = table.Column<double>(type: "double precision", nullable: false),
                    CEActualChange = table.Column<double>(type: "double precision", nullable: false),
                    CEExpectedChange = table.Column<double>(type: "double precision", nullable: false),
                    CEResidual = table.Column<double>(type: "double precision", nullable: false),
                    CEResidualPct = table.Column<double>(type: "double precision", nullable: false),
                    PEActual = table.Column<double>(type: "double precision", nullable: false),
                    PEExpected = table.Column<double>(type: "double precision", nullable: false),
                    PEActualChange = table.Column<double>(type: "double precision", nullable: false),
                    PEExpectedChange = table.Column<double>(type: "double precision", nullable: false),
                    PEResidual = table.Column<double>(type: "double precision", nullable: false),
                    PEResidualPct = table.Column<double>(type: "double precision", nullable: false),
                    DirectionalResidualPct = table.Column<double>(type: "double precision", nullable: false),
                    ResidualDelta = table.Column<double>(type: "double precision", nullable: true),
                    ResidualDirection = table.Column<int>(type: "integer", nullable: false),
                    FuturesRollingDirection = table.Column<int>(type: "integer", nullable: false),
                    Relationship = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CommonResidualPct = table.Column<double>(type: "double precision", nullable: false),
                    MaxQuoteAgeSeconds = table.Column<double>(type: "double precision", nullable: false),
                    IsAvailable = table.Column<bool>(type: "boolean", nullable: false),
                    UnavailableReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_option_residual_bars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_option_residual_bars_adaptive_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "adaptive_residual_anchor_components",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    Strike = table.Column<double>(type: "double precision", nullable: false),
                    Token = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TradingSymbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LotSize = table.Column<int>(type: "integer", nullable: false),
                    IsCenterStrike = table.Column<bool>(type: "boolean", nullable: false),
                    Price0930 = table.Column<double>(type: "double precision", nullable: false),
                    ImpliedVolatility0930 = table.Column<double>(type: "double precision", nullable: false),
                    QuoteTimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    QuoteAgeSeconds = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_residual_anchor_components", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_residual_anchor_components_adaptive_sessions_Sessi~",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "adaptive_rolling_states",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    StartBarSeq = table.Column<int>(type: "integer", nullable: false),
                    EndBarSeq = table.Column<int>(type: "integer", nullable: false),
                    StartAvailableAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndAvailableAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WindowBars = table.Column<int>(type: "integer", nullable: false),
                    WindowVolume = table.Column<long>(type: "bigint", nullable: false),
                    ElapsedSeconds = table.Column<double>(type: "double precision", nullable: false),
                    StartPrice = table.Column<double>(type: "double precision", nullable: false),
                    EndPrice = table.Column<double>(type: "double precision", nullable: false),
                    High = table.Column<double>(type: "double precision", nullable: false),
                    Low = table.Column<double>(type: "double precision", nullable: false),
                    PriceDisplacement = table.Column<double>(type: "double precision", nullable: false),
                    ReturnBps = table.Column<double>(type: "double precision", nullable: false),
                    PathLength = table.Column<double>(type: "double precision", nullable: false),
                    Efficiency = table.Column<double>(type: "double precision", nullable: false),
                    SignedEfficiency = table.Column<double>(type: "double precision", nullable: false),
                    StrictBuyVolume = table.Column<long>(type: "bigint", nullable: false),
                    StrictSellVolume = table.Column<long>(type: "bigint", nullable: false),
                    StrictUnknownVolume = table.Column<long>(type: "bigint", nullable: false),
                    StrictDelta = table.Column<long>(type: "bigint", nullable: false),
                    StrictQuoteCoverage = table.Column<double>(type: "double precision", nullable: false),
                    StrictDeltaRatioTotal = table.Column<double>(type: "double precision", nullable: false),
                    StrictDeltaRatioClassified = table.Column<double>(type: "double precision", nullable: true),
                    EnrichedBuyVolume = table.Column<long>(type: "bigint", nullable: false),
                    EnrichedSellVolume = table.Column<long>(type: "bigint", nullable: false),
                    EnrichedUnknownVolume = table.Column<long>(type: "bigint", nullable: false),
                    EnrichedDelta = table.Column<long>(type: "bigint", nullable: false),
                    EnrichedDeltaRatio = table.Column<double>(type: "double precision", nullable: false),
                    FallbackShare = table.Column<double>(type: "double precision", nullable: false),
                    OiStart = table.Column<long>(type: "bigint", nullable: true),
                    OiEnd = table.Column<long>(type: "bigint", nullable: true),
                    OiChange = table.Column<long>(type: "bigint", nullable: true),
                    OiChangePct = table.Column<double>(type: "double precision", nullable: true),
                    VolumePerSecond = table.Column<double>(type: "double precision", nullable: true),
                    TradeUpdates = table.Column<int>(type: "integer", nullable: false),
                    WindowRange = table.Column<double>(type: "double precision", nullable: false),
                    PriceDirection = table.Column<int>(type: "integer", nullable: false),
                    StrictDeltaDirection = table.Column<int>(type: "integer", nullable: false),
                    EnrichedDeltaDirection = table.Column<int>(type: "integer", nullable: false),
                    PriceStrictDeltaAgree = table.Column<bool>(type: "boolean", nullable: true),
                    PriceEnrichedDeltaAgree = table.Column<bool>(type: "boolean", nullable: true),
                    RollingStrictDeltaChange = table.Column<long>(type: "bigint", nullable: true),
                    RollingStrictAbsDeltaChange = table.Column<long>(type: "bigint", nullable: true),
                    RollingEnrichedDeltaChange = table.Column<long>(type: "bigint", nullable: true),
                    RollingOiChangePctChange = table.Column<double>(type: "double precision", nullable: true),
                    StrictDominanceEvolution = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    IsStrong = table.Column<bool>(type: "boolean", nullable: false),
                    WeakeningSequence = table.Column<int>(type: "integer", nullable: false),
                    StrongBaseBarSeq = table.Column<int>(type: "integer", nullable: true),
                    Weak1BarSeq = table.Column<int>(type: "integer", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_rolling_states", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_rolling_states_adaptive_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "adaptive_weak2_observations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    StrongBaseBarSeq = table.Column<int>(type: "integer", nullable: false),
                    Weak1BarSeq = table.Column<int>(type: "integer", nullable: false),
                    TriggerBarSeq = table.Column<int>(type: "integer", nullable: false),
                    OldTrendDirection = table.Column<int>(type: "integer", nullable: false),
                    ReversalDirection = table.Column<int>(type: "integer", nullable: false),
                    StrictNoTurnDiagnostic = table.Column<bool>(type: "boolean", nullable: false),
                    TriggerTimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OptionSide = table.Column<int>(type: "integer", nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Strike = table.Column<double>(type: "double precision", nullable: true),
                    Token = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TradingSymbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EntryAsk = table.Column<double>(type: "double precision", nullable: true),
                    EntryBid = table.Column<double>(type: "double precision", nullable: true),
                    EntryQuoteTimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EntryQuoteLatencySeconds = table.Column<double>(type: "double precision", nullable: true),
                    CEOiStrongBase = table.Column<long>(type: "bigint", nullable: true),
                    PEOiStrongBase = table.Column<long>(type: "bigint", nullable: true),
                    CEOiTrigger = table.Column<long>(type: "bigint", nullable: true),
                    PEOiTrigger = table.Column<long>(type: "bigint", nullable: true),
                    PairOiStrongBase = table.Column<long>(type: "bigint", nullable: true),
                    PairOiTrigger = table.Column<long>(type: "bigint", nullable: true),
                    PairOiChange = table.Column<long>(type: "bigint", nullable: true),
                    PairOiChangePct = table.Column<double>(type: "double precision", nullable: true),
                    OiGatePassed = table.Column<bool>(type: "boolean", nullable: true),
                    AtmResidualDirectionalPct = table.Column<double>(type: "double precision", nullable: true),
                    BandResidualDirectionalPct = table.Column<double>(type: "double precision", nullable: true),
                    ResidualSupportsReversalDiagnostic = table.Column<bool>(type: "boolean", nullable: true),
                    H5TargetBarSeq = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SelectionPolicy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UnavailableReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ExitBid = table.Column<double>(type: "double precision", nullable: true),
                    ExitAsk = table.Column<double>(type: "double precision", nullable: true),
                    ExitTimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExitLatencySeconds = table.Column<double>(type: "double precision", nullable: true),
                    HoldingSeconds = table.Column<double>(type: "double precision", nullable: true),
                    PnlPoints = table.Column<double>(type: "double precision", nullable: true),
                    ReturnPct = table.Column<double>(type: "double precision", nullable: true),
                    MfePointsExecutableBid = table.Column<double>(type: "double precision", nullable: true),
                    MaePointsExecutableBid = table.Column<double>(type: "double precision", nullable: true),
                    FuturesH5Move = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_weak2_observations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_weak2_observations_adaptive_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_future_bars_SessionId_BarSeq",
                table: "adaptive_future_bars",
                columns: new[] { "SessionId", "BarSeq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_future_bars_SessionId_EndAvailableAtUtc",
                table: "adaptive_future_bars",
                columns: new[] { "SessionId", "EndAvailableAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_observer_runtime_SessionId",
                table: "adaptive_observer_runtime",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_option_band_bars_SessionId_BarSeq_Side",
                table: "adaptive_option_band_bars",
                columns: new[] { "SessionId", "BarSeq", "Side" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_option_band_bars_SessionId_Side_BarSeq",
                table: "adaptive_option_band_bars",
                columns: new[] { "SessionId", "Side", "BarSeq" });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_option_residual_bars_SessionId_BarSeq_Variant",
                table: "adaptive_option_residual_bars",
                columns: new[] { "SessionId", "BarSeq", "Variant" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_option_residual_bars_SessionId_Variant_BarSeq",
                table: "adaptive_option_residual_bars",
                columns: new[] { "SessionId", "Variant", "BarSeq" });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_residual_anchor_components_SessionId_Side_Strike",
                table: "adaptive_residual_anchor_components",
                columns: new[] { "SessionId", "Side", "Strike" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_rolling_states_SessionId_EndAvailableAtUtc",
                table: "adaptive_rolling_states",
                columns: new[] { "SessionId", "EndAvailableAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_rolling_states_SessionId_EndBarSeq",
                table: "adaptive_rolling_states",
                columns: new[] { "SessionId", "EndBarSeq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_sessions_TradeDate_ModelVersion",
                table: "adaptive_sessions",
                columns: new[] { "TradeDate", "ModelVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_weak2_observations_SessionId_Status",
                table: "adaptive_weak2_observations",
                columns: new[] { "SessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_weak2_observations_SessionId_TriggerBarSeq",
                table: "adaptive_weak2_observations",
                columns: new[] { "SessionId", "TriggerBarSeq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "adaptive_future_bars");

            migrationBuilder.DropTable(
                name: "adaptive_observer_runtime");

            migrationBuilder.DropTable(
                name: "adaptive_option_band_bars");

            migrationBuilder.DropTable(
                name: "adaptive_option_residual_bars");

            migrationBuilder.DropTable(
                name: "adaptive_residual_anchor_components");

            migrationBuilder.DropTable(
                name: "adaptive_rolling_states");

            migrationBuilder.DropTable(
                name: "adaptive_weak2_observations");

            migrationBuilder.DropTable(
                name: "adaptive_sessions");
        }
    }
}
