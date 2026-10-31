using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.MarketSummary;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.PaperEngineV2.Frames;

namespace NiftySignal.PaperEngineV2.Runtime;

/// <summary>What the observer knows about a session at its start (the frozen weekly option universe). Immutable.</summary>
public sealed record V2SessionInfo(
    long SessionId, DateOnly TradeDate, string ModelVersion, string? AtmRuleVersion, DateOnly Expiry, IReadOnlyList<ListedStrike> Strikes, IReadOnlyDictionary<string, int> LotSizes);

/// <summary>One completed bar, captured on the observer thread: plain immutable values plus the basket's last top-of-book quotes at the bar's availability instant. No database, no engine.</summary>
public sealed record V2BarNotice(V2SessionInfo Session, BarInput Bar, double? CenterStrike, IReadOnlyList<QuoteInput> Quotes);

/// <summary>Everything the frame builder needs besides the bar and the quotes; assembled by the V2 task, never by the observer.</summary>
public sealed record V2FrameInputs(Basket Basket, IReadOnlyList<FuturesWindowSummary> Futures, IReadOnlyList<GridSummary> Grids, IReadOnlyList<VixWindowSummary>? Vix);

public interface IV2FrameInputSource
{
    /// <summary>Returns the inputs for the notice's bar, or null (with a reason) when they cannot be assembled; null is a visible build failure, never a fabricated frame.</summary>
    Task<(V2FrameInputs? Inputs, string? Problem)> LoadAsync(V2BarNotice notice, CancellationToken ct);
}

/// <summary>Lot sizes from the session's frozen option universe (instrument-master values); a missing token is a rejected order, never a guess.</summary>
public sealed class SessionLotSizes : ILotSizeSource
{
    volatile V2SessionInfo? _session;

    public void Set(V2SessionInfo session) => _session = session;

    public int? LotSize(DateOnly tradeDate, string token) =>
        _session is { } s && s.TradeDate == tradeDate && s.LotSizes.TryGetValue(token, out var lot) && lot > 0 ? lot : null;
}

/// <summary>Turns a bar notice into an immutable frame. The production factory assembles the persisted summaries and uses the session's <see cref="MarketStateFrameBuilder"/>; tests inject recorded or hand-built frames.</summary>
public interface IV2FrameFactory
{
    Task<(MarketStateFrame? Frame, string? Problem)> BuildAsync(V2BarNotice notice, MarketStateFrameBuilder builder, CancellationToken ct);
}

/// <summary>Production factory: persisted summaries (via <see cref="IV2FrameInputSource"/>) + the session's builder + the basket's quotes only.</summary>
public sealed class InputSourceFrameFactory(IV2FrameInputSource source) : IV2FrameFactory
{
    public async Task<(MarketStateFrame? Frame, string? Problem)> BuildAsync(V2BarNotice notice, MarketStateFrameBuilder builder, CancellationToken ct)
    {
        var (inputs, problem) = await source.LoadAsync(notice, ct);
        if (inputs is null) return (null, problem ?? "frame inputs unavailable");
        var tokens = inputs.Basket.Contracts.Where(c => c.Token is not null).Select(c => c.Token!).ToHashSet(StringComparer.Ordinal);
        var quotes = notice.Quotes.Where(q => tokens.Contains(q.Token)).ToList();
        return (builder.Build(notice.Bar, inputs.Basket, inputs.Futures, inputs.Grids, inputs.Vix, quotes), null);
    }
}
