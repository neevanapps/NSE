using System.Globalization;

namespace NiftySignal.AdaptiveObserver.Commentary;

/// <summary>
/// Deterministic per-bar commentary evaluation (08-Oct plan sections 44-61). Pure: the same state and frame always give the same step.
/// Bars must be evaluated in strict BarSeq order. Directions are interpretation hypotheses, not validated edge (plan section 0.5).
/// </summary>
public static class CommentaryEvaluator
{
    /// <summary>Identifies the rule set that produced an event so history can be reproduced after any later rule change.</summary>
    public const string Version = "commentary-v1";

    static readonly EvidenceFamily[] ExternalFamilies =
        [EvidenceFamily.Book, EvidenceFamily.InterMarketBasis, EvidenceFamily.Options, EvidenceFamily.Residual];

    sealed record Candidate(
        CommentaryEventType Type,
        EventBias Bias,
        EvidenceAgreement Agreement,
        IReadOnlyList<EvidenceFamily> Supporting,
        IReadOnlyList<EvidenceItem> Primary,
        IReadOnlyList<EvidenceItem> Confirmations,
        IReadOnlyList<EvidenceItem> Contradictions);

    public static CommentaryStep Evaluate(CommentaryState state, CommentaryFrame frame)
    {
        if (frame.BarSeq <= state.LastEvaluatedBarSeq)
            throw new ArgumentException($"Bar {frame.BarSeq} is not after the last evaluated bar {state.LastEvaluatedBarSeq}; commentary evaluates bars in strict BarSeq order.", nameof(frame));

        var families = Families(frame);
        var candidate = Classify(state, frame, families);
        var advanced = state with { LastEvaluatedBarSeq = frame.BarSeq };
        var active = state.Active;

        if (candidate is null)
        {
            if (active is null) return new CommentaryStep(advanced, null);

            // An active event followed by NoMaterialEvent: one Resolved row, active cleared. The reason is recorded (plan 55.5 / 55.8).
            var reason = MissingInputs(frame, active.Type) is { Count: > 0 } missing
                ? $"required inputs unavailable: {string.Join(", ", missing)}"
                : !frame.ReadinessMet ? "readiness gate (ten valid contiguous bars) not met" : "event condition no longer holds";
            var resolvedState = advanced with { Active = null, CurrentBias = EventBias.Neutral };
            var resolved = Build(frame, active.Type, EventBias.Neutral, state.Regime, CommentaryLifecycle.Resolved, active.Agreement,
                state.CurrentBias, [], [], [], [reason]);
            return new CommentaryStep(resolvedState, resolved);
        }

        var regime = NextRegime(state.Regime, candidate);
        var isRejection = candidate.Type is CommentaryEventType.SellerRejectionConfirmed or CommentaryEventType.BuyerRejectionConfirmed;

        CommentaryLifecycle lifecycle;
        if (active is null)
        {
            lifecycle = isRejection ? CommentaryLifecycle.Confirmed : CommentaryLifecycle.New;
        }
        else if (active.Type == candidate.Type && active.Bias == candidate.Bias)
        {
            // Same event still in force: only an explicit phase change produces a row; otherwise it is an unchanged continuation.
            var neutralKind = candidate.Type is CommentaryEventType.FlowConflict or CommentaryEventType.FamilyConflict;
            var target = neutralKind ? active.Phase : TargetPhase(active, candidate, frame);
            var updated = new ActiveCommentaryEvent(active.Type, active.Bias, active.StartedBarSeq, candidate.Agreement, candidate.Supporting, target);
            var continued = advanced with { Regime = regime, Active = updated };
            if (target == active.Phase) return new CommentaryStep(continued, null);
            return Emit(continued, frame, candidate, target, state);
        }
        else if (active.Bias != EventBias.Neutral && candidate.Bias != EventBias.Neutral && active.Bias != candidate.Bias)
        {
            lifecycle = CommentaryLifecycle.Flipped;   // opposite directional event replaces the active one; no separate Resolved row
        }
        else
        {
            lifecycle = isRejection ? CommentaryLifecycle.Confirmed : CommentaryLifecycle.New;
        }

        var next = advanced with
        {
            Regime = regime,
            Active = new ActiveCommentaryEvent(candidate.Type, candidate.Bias, frame.BarSeq, candidate.Agreement, candidate.Supporting, lifecycle),
            CurrentBias = candidate.Bias,
        };
        return Emit(next, frame, candidate, lifecycle, state);
    }

    static CommentaryStep Emit(CommentaryState next, CommentaryFrame frame, Candidate c, CommentaryLifecycle lifecycle, CommentaryState previous)
    {
        var quality = DataQualityNotes(frame, c.Type);
        var ev = Build(frame, c.Type, c.Bias, next.Regime, lifecycle, c.Agreement, previous.CurrentBias, c.Primary, c.Confirmations, c.Contradictions, quality);
        return new CommentaryStep(next with { CurrentBias = c.Bias }, ev);
    }

    // ------------------------------------------------------------------ lifecycle phase (Strengthening / Weakening)

    /// <summary>
    /// Phase of a still-active event. Weakening takes precedence when both directions of change are present. A row is produced only when the
    /// phase actually changes, so repeated bars in the same phase are unchanged continuations. No magnitude threshold is used: only the
    /// existing Evolution label, the sign of the |Strict| change, supporting-family loss/arrival and EvidenceAgreement movement.
    /// </summary>
    static CommentaryLifecycle TargetPhase(ActiveCommentaryEvent active, Candidate c, CommentaryFrame f)
    {
        var lost = active.SupportingFamilies.Any(x => !c.Supporting.Contains(x));
        var gained = c.Supporting.Any(x => !active.SupportingFamilies.Contains(x));
        var weakening = f.Evolution == "Weakening" || f.RollingStrictAbsDeltaChange < 0 || lost || c.Agreement < active.Agreement;
        var strengthening = f.Evolution == "Strengthening" || f.RollingStrictAbsDeltaChange > 0 || gained || c.Agreement > active.Agreement;
        if (weakening) return CommentaryLifecycle.Weakening;
        if (strengthening) return CommentaryLifecycle.Strengthening;
        return active.Phase;
    }

    // ------------------------------------------------------------------ classification

    static Candidate? Classify(CommentaryState state, CommentaryFrame f, IReadOnlyDictionary<EvidenceFamily, FamilyDirection> fam)
    {
        if (!f.ReadinessMet) return null;

        // Rejection precedence: only directly after a still-active matching absorption.
        if (state.Active is { Type: CommentaryEventType.SellerAbsorption or CommentaryEventType.BuyerAbsorption } absorption
            && TryRejection(absorption, f, fam) is { } rejection)
            return rejection;

        if (f.StrictDelta is { } strict && f.EnrichedDelta is { } enriched && f.RollingStrictDelta is { } rStrict && f.RollingEnrichedDelta is { } rEnriched)
        {
            var buy = strict > 0 && enriched > 0 && rStrict > 0 && rEnriched > 0;
            var sell = strict < 0 && enriched < 0 && rStrict < 0 && rEnriched < 0;
            var oi = f.RollOiDelta; var bar = f.BarPriceDisplacement; var roll = f.RollingPriceDisplacement;

            if (buy)
            {
                if (oi > 0 && bar > 0 && roll > 0) return Build(CommentaryEventType.BuyerExpansion, EventBias.Long, f, fam);
                if (oi < 0 && bar > 0 && roll > 0) return Build(CommentaryEventType.ShortCovering, EventBias.Long, f, fam);
                if (oi >= 0 && bar <= 0) return Build(CommentaryEventType.BuyerAbsorption, EventBias.Neutral, f, fam);
            }
            else if (sell)
            {
                if (oi > 0 && bar < 0 && roll < 0) return Build(CommentaryEventType.SellerExpansion, EventBias.Short, f, fam);
                if (oi < 0 && bar < 0 && roll < 0) return Build(CommentaryEventType.LongLiquidation, EventBias.Short, f, fam);
                if (oi >= 0 && bar >= 0) return Build(CommentaryEventType.SellerAbsorption, EventBias.Neutral, f, fam);
            }
            else if (Math.Sign(strict) != Math.Sign(enriched) || Math.Sign(rStrict) != Math.Sign(rEnriched))
            {
                return Build(CommentaryEventType.FlowConflict, EventBias.Neutral, f, fam);
            }
        }

        // No explicit primary futures event: independent families that point in opposite directions form a FamilyConflict.
        var available = fam.Values.Where(x => x is FamilyDirection.Long or FamilyDirection.Short).ToArray();
        if (available.Contains(FamilyDirection.Long) && available.Contains(FamilyDirection.Short))
            return Build(CommentaryEventType.FamilyConflict, EventBias.Neutral, f, fam);

        return null;
    }

    static Candidate Build(CommentaryEventType type, EventBias bias, CommentaryFrame f, IReadOnlyDictionary<EvidenceFamily, FamilyDirection> fam)
    {
        // For neutral absorption events the interpretation is "the aggressive side is failing": supporting families point the other way.
        var support = type switch
        {
            CommentaryEventType.SellerAbsorption => FamilyDirection.Long,
            CommentaryEventType.BuyerAbsorption => FamilyDirection.Short,
            _ => bias switch { EventBias.Long => FamilyDirection.Long, EventBias.Short => FamilyDirection.Short, _ => FamilyDirection.Neutral },
        };
        var conflictKind = type is CommentaryEventType.FlowConflict or CommentaryEventType.FamilyConflict;
        var agreement = conflictKind ? EvidenceAgreement.Low : Agreement(fam, support);
        var supporting = conflictKind ? [] : ExternalFamilies.Where(x => fam[x] == support).ToArray();
        var opposing = conflictKind ? [] : ExternalFamilies.Where(x => fam[x] == Opposite(support)).ToArray();
        return new Candidate(type, bias, agreement, supporting, PrimaryEvidence(f), Describe(f, supporting), Describe(f, opposing));
    }

    static Candidate? TryRejection(ActiveCommentaryEvent absorption, CommentaryFrame f, IReadOnlyDictionary<EvidenceFamily, FamilyDirection> fam)
    {
        var seller = absorption.Type == CommentaryEventType.SellerAbsorption;
        if (f.BarPriceDisplacement is not { } bar || (seller ? !(bar > 0) : !(bar < 0))) return null;

        var dominance = f.Evolution == "Weakening"
            || f.Evolution == (seller ? "FlipToBuyer" : "FlipToSeller")
            || f.RollingStrictAbsDeltaChange < 0;
        if (!dominance) return null;

        var support = seller ? FamilyDirection.Long : FamilyDirection.Short;
        var supporting = ExternalFamilies.Where(x => fam[x] == support).ToArray();
        if (supporting.Length < 2) return null;     // at least two INDEPENDENT families (Book counts once even if OFI and MicroDev agree)

        var opposing = ExternalFamilies.Where(x => fam[x] == Opposite(support)).ToArray();
        return new Candidate(
            seller ? CommentaryEventType.SellerRejectionConfirmed : CommentaryEventType.BuyerRejectionConfirmed,
            seller ? EventBias.Long : EventBias.Short,
            Agreement(fam, support), supporting, PrimaryEvidence(f), Describe(f, supporting), Describe(f, opposing));
    }

    // ------------------------------------------------------------------ evidence families and agreement

    /// <summary>Direction of every family under the sign-only definitions of plan section 52.1.</summary>
    public static IReadOnlyDictionary<EvidenceFamily, FamilyDirection> Families(CommentaryFrame f)
    {
        var result = new Dictionary<EvidenceFamily, FamilyDirection>
        {
            [EvidenceFamily.FuturesCore] = FuturesCore(f),
            [EvidenceFamily.Book] = Book(f),
            [EvidenceFamily.InterMarketBasis] = f.DeltaBasis is { } basis ? Sign(basis) : FamilyDirection.Unavailable,
            [EvidenceFamily.Options] = Options(f),
            [EvidenceFamily.Residual] = f.ResidualAvailable && f.DirectionalResidualPct is { } r ? Sign(r) : FamilyDirection.Unavailable,
        };
        return result;
    }

    static FamilyDirection FuturesCore(CommentaryFrame f)
    {
        if (f.StrictDelta is not { } s || f.EnrichedDelta is not { } e || f.RollingStrictDelta is not { } rs || f.RollingEnrichedDelta is not { } re)
            return FamilyDirection.Unavailable;
        if (s > 0 && e > 0 && rs > 0 && re > 0) return FamilyDirection.Long;
        if (s < 0 && e < 0 && rs < 0 && re < 0) return FamilyDirection.Short;
        return FamilyDirection.Neutral;
    }

    static FamilyDirection Book(CommentaryFrame f)
    {
        if (f.Ofi is null && f.MicroDev is null) return FamilyDirection.Unavailable;
        var up = f.Ofi > 0 || f.MicroDev > 0; var down = f.Ofi < 0 || f.MicroDev < 0;
        return up && !down ? FamilyDirection.Long : down && !up ? FamilyDirection.Short : FamilyDirection.Neutral;
    }

    static FamilyDirection Options(CommentaryFrame f)
    {
        var ceKnown = f.CePosition is not null && f.CeStrictDelta is not null;
        var peKnown = f.PePosition is not null && f.PeStrictDelta is not null;
        if (!ceKnown && !peKnown) return FamilyDirection.Unavailable;
        // Strong support needs Position AND aggressive flow; PCR, IV and skew are context only and never create direction.
        var strongLong = (f.CePosition == "CallLongBuild" && f.CeStrictDelta > 0) || (f.PePosition == "PutWriting" && f.PeStrictDelta < 0);
        var strongShort = (f.PePosition == "PutLongBuild" && f.PeStrictDelta > 0) || (f.CePosition == "CallWriting" && f.CeStrictDelta < 0);
        return strongLong && !strongShort ? FamilyDirection.Long : strongShort && !strongLong ? FamilyDirection.Short : FamilyDirection.Neutral;
    }

    static FamilyDirection Sign(double x) => x > 0d ? FamilyDirection.Long : x < 0d ? FamilyDirection.Short : FamilyDirection.Neutral;

    static FamilyDirection Opposite(FamilyDirection d) => d switch { FamilyDirection.Long => FamilyDirection.Short, FamilyDirection.Short => FamilyDirection.Long, _ => FamilyDirection.Unavailable };

    /// <summary>HIGH: >=3 of the 4 external families support and none contradict. MEDIUM: >=1 supports and at most one contradicts. Else LOW. Unavailable counts as neither.</summary>
    static EvidenceAgreement Agreement(IReadOnlyDictionary<EvidenceFamily, FamilyDirection> fam, FamilyDirection support)
    {
        if (support is not (FamilyDirection.Long or FamilyDirection.Short)) return EvidenceAgreement.Low;
        var against = Opposite(support);
        var s = ExternalFamilies.Count(x => fam[x] == support);
        var c = ExternalFamilies.Count(x => fam[x] == against);
        if (s >= 3 && c == 0) return EvidenceAgreement.High;
        if (s >= 1 && c <= 1) return EvidenceAgreement.Medium;
        return EvidenceAgreement.Low;
    }

    // ------------------------------------------------------------------ regime (plan section 54; no time-based expiry)

    static bool IsDirectionalPrimary(CommentaryEventType t) =>
        t is CommentaryEventType.BuyerExpansion or CommentaryEventType.ShortCovering or CommentaryEventType.SellerExpansion or CommentaryEventType.LongLiquidation;

    static MarketRegime NextRegime(MarketRegime current, Candidate c)
    {
        if (c.Type == CommentaryEventType.FamilyConflict) return MarketRegime.Conflict;
        var directional = IsDirectionalPrimary(c.Type);
        var strong = c.Agreement >= EvidenceAgreement.Medium;
        switch (current)
        {
            case MarketRegime.Neutral:
                if (directional && strong) return c.Bias == EventBias.Long ? MarketRegime.Bullish : MarketRegime.Bearish;
                break;
            case MarketRegime.Bullish:
                if (c.Type is CommentaryEventType.BuyerAbsorption or CommentaryEventType.BuyerRejectionConfirmed
                    || (directional && c.Bias == EventBias.Short)) return MarketRegime.Transition;
                break;
            case MarketRegime.Bearish:
                if (c.Type is CommentaryEventType.SellerAbsorption or CommentaryEventType.SellerRejectionConfirmed
                    || (directional && c.Bias == EventBias.Long)) return MarketRegime.Transition;
                break;
            case MarketRegime.Transition:
                if (c.Type == CommentaryEventType.BuyerExpansion && strong) return MarketRegime.Bullish;
                if (c.Type == CommentaryEventType.SellerExpansion && strong) return MarketRegime.Bearish;
                break;
            case MarketRegime.Conflict:
                if (directional && strong) return c.Bias == EventBias.Long ? MarketRegime.Bullish : MarketRegime.Bearish;
                break;
        }

        return current;
    }

    // ------------------------------------------------------------------ evidence descriptions

    static string F(double x, string format = "0.###") => x.ToString(format, CultureInfo.InvariantCulture);

    static string Signed(double x, string format = "0.###") => x.ToString("+" + format + ";-" + format + ";0", CultureInfo.InvariantCulture);

    static IReadOnlyList<EvidenceItem> PrimaryEvidence(CommentaryFrame f)
    {
        var items = new List<EvidenceItem>();
        void Add(string metric, long? v) { if (v is { } x) items.Add(new(EvidenceFamily.FuturesCore, metric, Signed(x))); }
        void AddD(string metric, double? v, string format = "0.##") { if (v is { } x) items.Add(new(EvidenceFamily.FuturesCore, metric, Signed(x, format))); }
        Add("Strict Δ", f.StrictDelta); Add("Enriched Δ", f.EnrichedDelta);
        Add("Roll Strict", f.RollingStrictDelta); Add("Roll Enriched", f.RollingEnrichedDelta);
        Add("Roll OI Δ", f.RollOiDelta); AddD("Bar ΔPx", f.BarPriceDisplacement); AddD("Roll ΔPx", f.RollingPriceDisplacement);
        if (f.Evolution is { } evo) items.Add(new(EvidenceFamily.FuturesCore, "Evolution", evo));
        return items;
    }

    static IReadOnlyList<EvidenceItem> Describe(CommentaryFrame f, IEnumerable<EvidenceFamily> families)
    {
        var items = new List<EvidenceItem>();
        foreach (var family in families)
        {
            switch (family)
            {
                case EvidenceFamily.Book:
                    if (f.Ofi is { } ofi) items.Add(new(family, "OFI", Signed(ofi)));
                    if (f.MicroDev is { } micro) items.Add(new(family, "MicroDev", Signed(micro, "0.###")));
                    break;
                case EvidenceFamily.InterMarketBasis:
                    if (f.DeltaBasis is { } basis) items.Add(new(family, "ΔBasis", Signed(basis, "0.##")));
                    break;
                case EvidenceFamily.Options:
                    if (f.CePosition is { } cp) items.Add(new(family, "CE Position", cp));
                    if (f.PePosition is { } pp) items.Add(new(family, "PE Position", pp));
                    if (f.CeStrictDelta is { } cs) items.Add(new(family, "CE Strict Δ", Signed(cs)));
                    if (f.PeStrictDelta is { } ps) items.Add(new(family, "PE Strict Δ", Signed(ps)));
                    if (f.CeDeltaIv is { } civ) items.Add(new(family, "CE ΔIV", Signed(civ, "0.##")));
                    if (f.PeDeltaIv is { } piv) items.Add(new(family, "PE ΔIV", Signed(piv, "0.##")));
                    break;
                case EvidenceFamily.Residual:
                    if (f.DirectionalResidualPct is { } dr) items.Add(new(family, "Directional Res %", Signed(dr, "0.##")));
                    if (f.AdjacentDirectionalResidualDelta is { } ad) items.Add(new(family, "Adjacent Directional Res Δ", Signed(ad, "0.##")));
                    break;
            }
        }

        return items;
    }

    static IReadOnlyList<string> DataQualityNotes(CommentaryFrame f, CommentaryEventType type)
    {
        var notes = new List<string>();
        var fam = Families(f);
        foreach (var family in ExternalFamilies.Where(x => fam[x] == FamilyDirection.Unavailable))
            notes.Add($"{family} evidence unavailable");
        if (type is not (CommentaryEventType.FlowConflict or CommentaryEventType.FamilyConflict) && fam[EvidenceFamily.FuturesCore] == FamilyDirection.Unavailable)
            notes.Add("FuturesCore flow inputs unavailable");
        return notes;
    }

    /// <summary>Mandatory futures inputs that are unavailable for the given event type (plan section 48 availability gates).</summary>
    static IReadOnlyList<string> MissingInputs(CommentaryFrame f, CommentaryEventType type)
    {
        var missing = new List<string>();
        void Need(bool present, string name) { if (!present) missing.Add(name); }
        Need(f.StrictDelta.HasValue, "Strict Δ"); Need(f.EnrichedDelta.HasValue, "Enriched Δ");
        Need(f.RollingStrictDelta.HasValue, "Roll Strict"); Need(f.RollingEnrichedDelta.HasValue, "Roll Enriched");
        if (type is not CommentaryEventType.FlowConflict && type is not CommentaryEventType.FamilyConflict)
        {
            Need(f.RollOiDelta.HasValue, "Roll OI Δ"); Need(f.BarPriceDisplacement.HasValue, "Bar ΔPx");
            if (type is CommentaryEventType.BuyerExpansion or CommentaryEventType.SellerExpansion or CommentaryEventType.ShortCovering or CommentaryEventType.LongLiquidation)
                Need(f.RollingPriceDisplacement.HasValue, "Roll ΔPx");
        }

        return missing;
    }

    // ------------------------------------------------------------------ event assembly

    static CommentaryEvent Build(CommentaryFrame f, CommentaryEventType type, EventBias bias, MarketRegime regime, CommentaryLifecycle lifecycle,
        EvidenceAgreement agreement, EventBias previousBias, IReadOnlyList<EvidenceItem> primary, IReadOnlyList<EvidenceItem> confirmations,
        IReadOnlyList<EvidenceItem> contradictions, IReadOnlyList<string> quality)
    {
        var changed = previousBias != bias;
        var (notify, reason) = TelegramPolicy(lifecycle, previousBias, bias);
        // Severity is Dashboard presentation and is deliberately independent of Telegram eligibility: narrowing what Telegram sends must not change what the Dashboard shows.
        var high = lifecycle is CommentaryLifecycle.Confirmed or CommentaryLifecycle.Flipped
            || (lifecycle != CommentaryLifecycle.Resolved && previousBias != EventBias.Neutral && bias != previousBias);
        var severity = high ? CommentarySeverity.High : lifecycle == CommentaryLifecycle.New ? CommentarySeverity.Medium : CommentarySeverity.Info;
        var draft = new CommentaryEvent(f.SessionId, f.TradeDate, f.BarSeq, f.BarEndAvailableAtUtc, type, bias, regime, lifecycle, agreement, severity,
            previousBias, changed, primary, confirmations, contradictions, quality, notify, reason, Version, string.Empty);
        return draft with { RenderedCommentary = CommentaryRenderer.Render(draft) };
    }

    /// <summary>
    /// V1 conservative Telegram eligibility (plan section 60.1): Confirmed, Flipped, or a true direct LONG&lt;-&gt;SHORT reversal that is not
    /// already a Flipped event. A move to or from Neutral (LONG/SHORT to NEUTRAL, ordinary New, Strengthening, Weakening, absorption,
    /// FlowConflict, FamilyConflict, Resolved) is never sent; the Dashboard still persists and shows every event.
    /// </summary>
    public static (bool Notify, string? Reason) TelegramPolicy(CommentaryLifecycle lifecycle, EventBias previousBias, EventBias bias)
    {
        if (lifecycle == CommentaryLifecycle.Confirmed) return (true, "Confirmed");
        if (lifecycle == CommentaryLifecycle.Flipped) return (true, "Flipped");
        if (lifecycle != CommentaryLifecycle.Resolved && previousBias is EventBias.Long or EventBias.Short
            && bias is EventBias.Long or EventBias.Short && bias != previousBias) return (true, "DirectReversal");
        return (false, null);
    }
}
