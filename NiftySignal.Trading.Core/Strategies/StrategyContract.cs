using System.Globalization;
using NiftySignal.Trading.Core.Events;

namespace NiftySignal.Trading.Core.Strategies;

/// <summary>What a strategy variant wants for the current finalized bar.</summary>
public enum StrategyAction
{
    /// <summary>The variant wants to open a position in the given direction (Long or Short). Valid on every bar the setup stays valid; entry conditions and capacity are the engine's business.</summary>
    Enter = 0,

    /// <summary>No change requested for an existing position of the variant. Carries no direction and requests nothing.</summary>
    Hold = 1,

    /// <summary>The variant wants its own open position closed, whatever side it holds. Carries no direction: the position owns its side.</summary>
    Exit = 2,

    /// <summary>No entry intent now (setup not actionable). Carries no direction. Ends any entry opportunity of the variant that is still waiting.</summary>
    Wait = 3,
}

/// <summary>Market direction of an Enter. Which instrument expresses it (call or put) is decided BELOW the strategy layer.</summary>
public enum StrategyDirection
{
    Neutral = 0,
    Long = 1,
    Short = 2,
}

/// <summary>
/// One variant's decision for one finalized bar. Pure intent: no strike, quantity, price, capital or fill information exists here by design.
/// Valid combinations: Enter with Long or Short; Hold, Exit and Wait with Neutral. Every decision carries a reason code.
/// </summary>
public sealed record VariantDecision
{
    public VariantDecision(string variantId, StrategyAction action, StrategyDirection direction, string reasonCode, string? detail = null)
    {
        VariantId = variantId;
        Action = action;
        Direction = direction;
        ReasonCode = reasonCode;
        Detail = detail;
        var errors = Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors));
        }
    }

    public string VariantId { get; }

    public StrategyAction Action { get; }

    public StrategyDirection Direction { get; }

    /// <summary>Short stable machine-readable reason (audit). Required for every decision.</summary>
    public string ReasonCode { get; }

    /// <summary>Optional free-form audit context (invariant formatting only; never read by the engine).</summary>
    public string? Detail { get; }

    public static VariantDecision Enter(string variantId, StrategyDirection direction, string reasonCode, string? detail = null) => new(variantId, StrategyAction.Enter, direction, reasonCode, detail);

    public static VariantDecision Hold(string variantId, string reasonCode, string? detail = null) => new(variantId, StrategyAction.Hold, StrategyDirection.Neutral, reasonCode, detail);

    public static VariantDecision Exit(string variantId, string reasonCode, string? detail = null) => new(variantId, StrategyAction.Exit, StrategyDirection.Neutral, reasonCode, detail);

    public static VariantDecision Wait(string variantId, string reasonCode, string? detail = null) => new(variantId, StrategyAction.Wait, StrategyDirection.Neutral, reasonCode, detail);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!VariantKey.IsValidPart(VariantId))
        {
            errors.Add($"VariantId '{VariantId}' is not valid.");
        }

        if (!Enum.IsDefined(Action) || !Enum.IsDefined(Direction))
        {
            errors.Add("Action or Direction is not defined.");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(ReasonCode))
        {
            errors.Add($"Variant '{VariantId}': every decision needs a reason code.");
        }

        if (Action == StrategyAction.Enter && Direction == StrategyDirection.Neutral)
        {
            errors.Add($"Variant '{VariantId}': Enter needs a Long or Short direction.");
        }

        if (Action != StrategyAction.Enter && Direction != StrategyDirection.Neutral)
        {
            errors.Add($"Variant '{VariantId}': {Action} carries no direction (Neutral only).");
        }

        return errors;
    }
}

/// <summary>
/// A family answer for one bar: zero or more variant decisions, at most one per variant. A variant without a decision is treated as
/// Wait with reason NoDecision (never as Hold).
/// </summary>
public sealed record FamilyDecisions
{
    public static FamilyDecisions None { get; } = new(Array.Empty<VariantDecision>());

    public FamilyDecisions(IReadOnlyList<VariantDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        Decisions = decisions;
    }

    public IReadOnlyList<VariantDecision> Decisions { get; }
}

/// <summary>
/// The strategy plug-in contract. One family = one logical strategy (StrategyId) evaluated ONCE per finalized bar, answering for each of its
/// variants. Implementations must be deterministic functions of the bars they have been shown (no clock, no randomness, no I/O) and must not
/// know about contracts, strikes, quantities, quotes, fills or capital. A family that owns state contributes it through IStateContributor.
/// </summary>
public interface IStrategyFamily
{
    string StrategyId { get; }

    string Version { get; }

    /// <summary>Every VariantId this family can answer for. Decisions for any other id are an error.</summary>
    IReadOnlyCollection<string> VariantIds { get; }

    FamilyDecisions OnBar(BarCompletedEvent bar);
}

/// <summary>A variant decision stamped by the engine with the identity of the finalized input bar it was made on.</summary>
public sealed record AttributedDecision(
    VariantKey Variant,
    StrategyAction Action,
    StrategyDirection Direction,
    string ReasonCode,
    string? Detail,
    string SourceSessionId,
    long BarSeq,
    DateTimeOffset BarAvailableAt)
{
    /// <summary>Compact invariant text used by the state digest and audit lines.</summary>
    public string CanonicalText()
        => string.Create(CultureInfo.InvariantCulture, $"{Variant}|{Action}|{Direction}|{ReasonCode}|{SourceSessionId}|{BarSeq}|{BarAvailableAt.UtcTicks}");
}
