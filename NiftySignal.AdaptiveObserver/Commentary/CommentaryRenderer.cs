using System.Text;

namespace NiftySignal.AdaptiveObserver.Commentary;

/// <summary>
/// Deterministic text from a structured event (plan section 59). It only mentions metrics present in the event's evidence lists and fixed
/// definition-level sentences for the event type; it never reads raw frames and never invents evidence. No LLM.
/// </summary>
public static class CommentaryRenderer
{
    public static string Title(CommentaryEventType type) => type switch
    {
        CommentaryEventType.BuyerExpansion => "Buyer expansion",
        CommentaryEventType.SellerExpansion => "Seller expansion",
        CommentaryEventType.ShortCovering => "Short covering",
        CommentaryEventType.LongLiquidation => "Long liquidation",
        CommentaryEventType.BuyerAbsorption => "Buyer absorption",
        CommentaryEventType.SellerAbsorption => "Seller absorption",
        CommentaryEventType.FlowConflict => "Flow conflict",
        CommentaryEventType.FamilyConflict => "Evidence-family conflict",
        CommentaryEventType.SellerRejectionConfirmed => "Seller rejection confirmed",
        CommentaryEventType.BuyerRejectionConfirmed => "Buyer rejection confirmed",
        _ => type.ToString(),
    };

    static string Verb(CommentaryLifecycle lifecycle) => lifecycle switch
    {
        CommentaryLifecycle.New => "started",
        CommentaryLifecycle.Strengthening => "strengthening",
        CommentaryLifecycle.Weakening => "weakening",
        CommentaryLifecycle.Confirmed => "confirmed",
        CommentaryLifecycle.Resolved => "resolved",
        CommentaryLifecycle.Flipped => "flipped",
        _ => lifecycle.ToString().ToLowerInvariant(),
    };

    // Definition-level meaning of each event type (what its explicit conditions say), never data-dependent claims.
    static string Meaning(CommentaryEventType type) => type switch
    {
        CommentaryEventType.BuyerExpansion => "Buyer-aggressive flow is persistent, open interest is expanding and price is accepting the move higher.",
        CommentaryEventType.SellerExpansion => "Seller-aggressive flow is persistent, open interest is expanding and price is accepting the move lower.",
        CommentaryEventType.ShortCovering => "Buyer-aggressive flow with contracting open interest and rising price: covering-compatible.",
        CommentaryEventType.LongLiquidation => "Seller-aggressive flow with contracting open interest and falling price: liquidation-compatible.",
        CommentaryEventType.BuyerAbsorption => "Aggressive buying persists but price is not moving higher.",
        CommentaryEventType.SellerAbsorption => "Aggressive selling persists but price is not moving lower.",
        CommentaryEventType.FlowConflict => "Strict and Enriched flow disagree; no directional interpretation is made.",
        CommentaryEventType.FamilyConflict => "Independent evidence families point in opposite directions.",
        CommentaryEventType.SellerRejectionConfirmed => "Selling was absorbed, price turned up and independent families now point higher.",
        CommentaryEventType.BuyerRejectionConfirmed => "Buying was absorbed, price turned down and independent families now point lower.",
        _ => string.Empty,
    };

    static string Upper(EventBias b) => b.ToString().ToUpperInvariant();

    static string Group(IReadOnlyList<EvidenceItem> items) =>
        string.Join("; ", items.GroupBy(x => x.Family).Select(g => $"{g.Key} ({string.Join(", ", g.Select(i => $"{i.Metric} {i.Value}"))})"));

    public static string Render(CommentaryEvent e)
    {
        var sb = new StringBuilder();
        sb.Append(Title(e.EventType)).Append(' ').Append(Verb(e.Lifecycle)).Append('.').Append('\n');
        sb.Append("Bias ").Append(Upper(e.EventBias))
            .Append(" | Regime ").Append(e.MarketRegime.ToString().ToUpperInvariant())
            .Append(" | EvidenceAgreement ").Append(e.EvidenceAgreement.ToString().ToUpperInvariant()).Append('.');

        if (e.Lifecycle == CommentaryLifecycle.Resolved)
        {
            sb.Append('\n').Append("The event condition no longer holds.");
        }
        else
        {
            var meaning = Meaning(e.EventType);
            if (meaning.Length > 0) sb.Append('\n').Append(meaning);
            if (e.PrimaryEvidence.Count > 0)
                sb.Append('\n').Append("Evidence: ").Append(string.Join(", ", e.PrimaryEvidence.Select(i => $"{i.Metric} {i.Value}"))).Append('.');
            if (e.Confirmations.Count > 0) sb.Append('\n').Append("Supported by ").Append(Group(e.Confirmations)).Append('.');
            if (e.Contradictions.Count > 0) sb.Append('\n').Append("Contradicted by ").Append(Group(e.Contradictions)).Append('.');
        }

        if (e.DataQuality.Count > 0) sb.Append('\n').Append("Data: ").Append(string.Join("; ", e.DataQuality)).Append('.');
        return sb.ToString();
    }
}
