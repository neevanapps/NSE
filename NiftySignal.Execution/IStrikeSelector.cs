using NiftySignal.Domain.Enums;
using NiftySignal.Rules;

namespace NiftySignal.Execution;

/// <summary>Diagnostics lists every excluded candidate and why, even when Selected is non-null -- plan section 8: "log the reason" applies to every rejection, not just a total failure.</summary>
public sealed record StrikeSelectionResult(StrikeCandidate? Selected, IReadOnlyList<string> Diagnostics);

/// <summary>Behind an interface per plan section 8, "so it can evolve independently."</summary>
public interface IStrikeSelector
{
    StrikeSelectionResult SelectBestCandidate(IReadOnlyList<StrikeCandidate> candidates, EntryDirection direction, RulesetConfig config);
}
