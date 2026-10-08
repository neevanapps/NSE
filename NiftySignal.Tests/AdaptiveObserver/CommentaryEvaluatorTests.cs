using NiftySignal.AdaptiveObserver.Commentary;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>08-Oct plan Slice 4A: the pure commentary domain, driven only by hand-built frames.</summary>
public sealed class CommentaryEvaluatorTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 8, 5, 0, 0, TimeSpan.Zero);

    static CommentaryFrame Frame(int seq) => new()
    {
        SessionId = 1, TradeDate = new DateOnly(2026, 10, 8), BarSeq = seq, BarEndAvailableAtUtc = T0.AddMinutes(seq), ReadinessMet = true,
    };

    static CommentaryFrame SellerExpansion(int seq) => Frame(seq) with
    {
        StrictDelta = -18400, EnrichedDelta = -22600, RollingStrictDelta = -121000, RollingEnrichedDelta = -130000,
        RollOiDelta = 12450, BarPriceDisplacement = -7.5, RollingPriceDisplacement = -31.5,
    };

    static CommentaryFrame BuyerExpansion(int seq) => Frame(seq) with
    {
        StrictDelta = 18400, EnrichedDelta = 22600, RollingStrictDelta = 121000, RollingEnrichedDelta = 130000,
        RollOiDelta = 12450, BarPriceDisplacement = 7.5, RollingPriceDisplacement = 31.5,
    };

    static CommentaryFrame SellerAbsorption(int seq) => SellerExpansion(seq) with { BarPriceDisplacement = 0.5, RollingPriceDisplacement = -10 };

    static CommentaryStep Step(CommentaryState s, CommentaryFrame f) => CommentaryEvaluator.Evaluate(s, f);

    static CommentaryState Run(out List<CommentaryEvent> events, params CommentaryFrame[] frames)
    {
        events = [];
        var state = CommentaryState.Initial;
        foreach (var f in frames)
        {
            var step = Step(state, f);
            state = step.State;
            if (step.Event is { } e) events.Add(e);
        }

        return state;
    }

    // ---- primary futures events ----

    [Theory]
    [InlineData("buyer", CommentaryEventType.BuyerExpansion, EventBias.Long)]
    [InlineData("seller", CommentaryEventType.SellerExpansion, EventBias.Short)]
    [InlineData("cover", CommentaryEventType.ShortCovering, EventBias.Long)]
    [InlineData("liquidate", CommentaryEventType.LongLiquidation, EventBias.Short)]
    public void PrimaryEvents_ClassifyAndStartNew(string kind, CommentaryEventType expected, EventBias bias)
    {
        var frame = kind switch
        {
            "buyer" => BuyerExpansion(1),
            "seller" => SellerExpansion(1),
            "cover" => BuyerExpansion(1) with { RollOiDelta = -500 },
            _ => SellerExpansion(1) with { RollOiDelta = -500 },
        };
        var e = Assert.IsType<CommentaryEvent>(Step(CommentaryState.Initial, frame).Event);
        Assert.Equal(expected, e.EventType); Assert.Equal(bias, e.EventBias); Assert.Equal(CommentaryLifecycle.New, e.Lifecycle);
        Assert.Equal(CommentaryEvaluator.Version, e.CommentaryVersion);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Absorption_StartsNeutral_NeverDirectional(bool seller)
    {
        var frame = seller ? SellerAbsorption(1) : BuyerExpansion(1) with { BarPriceDisplacement = -0.5, RollingPriceDisplacement = 10 };
        var e = Step(CommentaryState.Initial, frame).Event!;
        Assert.Equal(seller ? CommentaryEventType.SellerAbsorption : CommentaryEventType.BuyerAbsorption, e.EventType);
        Assert.Equal(EventBias.Neutral, e.EventBias);
        Assert.Equal(MarketRegime.Neutral, e.MarketRegime);       // absorption alone does not create a regime
    }

    [Fact]
    public void UnmatchedCombinations_AreNoMaterialEvent_NothingPersistedAndCheckpointAdvances()
    {
        // buy-aligned, OI falling, price not rising
        var a = BuyerExpansion(1) with { RollOiDelta = -5, BarPriceDisplacement = -1, RollingPriceDisplacement = 4 };
        // buy-aligned, OI rising, bar price up but rolling price not up
        var b = BuyerExpansion(2) with { RollingPriceDisplacement = -2 };
        // sell-aligned, OI falling, bar price up
        var c = SellerExpansion(3) with { RollOiDelta = -5, BarPriceDisplacement = 2 };
        var state = Run(out var events, a, b, c);
        Assert.Empty(events);
        Assert.Equal(3, state.LastEvaluatedBarSeq);
        Assert.Null(state.Active); Assert.Equal(EventBias.Neutral, state.CurrentBias); Assert.Equal(MarketRegime.Neutral, state.Regime);
    }

    [Fact]
    public void FlowConflict_IsNeutralLowAndNotDirectional()
    {
        var f = BuyerExpansion(1) with { EnrichedDelta = -100 };
        var e = Step(CommentaryState.Initial, f).Event!;
        Assert.Equal(CommentaryEventType.FlowConflict, e.EventType);
        Assert.Equal(EventBias.Neutral, e.EventBias); Assert.Equal(EvidenceAgreement.Low, e.EvidenceAgreement);
        // zero is not forced into alignment: a zero Enriched against a positive Strict is a conflict, two zeros are not.
        Assert.Equal(CommentaryEventType.FlowConflict, Step(CommentaryState.Initial, BuyerExpansion(1) with { EnrichedDelta = 0 }).Event!.EventType);
        Assert.Null(Step(CommentaryState.Initial, Frame(1) with { StrictDelta = 0, EnrichedDelta = 0, RollingStrictDelta = 0, RollingEnrichedDelta = 0 }).Event);
    }

    [Fact]
    public void ReadinessGate_SuppressesEveryEvent()
    {
        Assert.Null(Step(CommentaryState.Initial, SellerExpansion(1) with { ReadinessMet = false }).Event);
    }

    // ---- mandatory rolling inputs ----

    public static IEnumerable<object[]> MissingRollingInputs()
    {
        yield return ["RollingStrictDelta", (Func<CommentaryFrame, CommentaryFrame>)(f => f with { RollingStrictDelta = null })];
        yield return ["RollingEnrichedDelta", (Func<CommentaryFrame, CommentaryFrame>)(f => f with { RollingEnrichedDelta = null })];
        yield return ["RollingPriceDisplacement", (Func<CommentaryFrame, CommentaryFrame>)(f => f with { RollingPriceDisplacement = null })];
        yield return ["RollOiDelta", (Func<CommentaryFrame, CommentaryFrame>)(f => f with { RollOiDelta = null })];
    }

    [Theory]
    [MemberData(nameof(MissingRollingInputs))]
    public void PrimaryDirectionalEvent_NeverFiresWithoutItsMandatoryRollingInput(string name, Func<CommentaryFrame, CommentaryFrame> remove)
    {
        Assert.Null(Step(CommentaryState.Initial, remove(SellerExpansion(1))).Event);
        Assert.Null(Step(CommentaryState.Initial, remove(BuyerExpansion(1))).Event);
        Assert.NotEmpty(name);
    }

    [Fact]
    public void ActiveEvent_IsResolved_WhenItsRollingInputsDisappear_AndTheReasonIsRecorded()
    {
        var state = Step(CommentaryState.Initial, SellerExpansion(1)).State;
        var step = Step(state, SellerExpansion(2) with { RollingStrictDelta = null });
        var e = step.Event!;
        Assert.Equal(CommentaryLifecycle.Resolved, e.Lifecycle);
        Assert.Equal(EventBias.Neutral, e.EventBias); Assert.Equal(EventBias.Short, e.PreviousBias); Assert.True(e.BiasChanged);
        Assert.Contains(e.DataQuality, d => d.Contains("Roll Strict"));
        Assert.Null(step.State.Active);
        // The next valid bar is a fresh New event, not a continuation.
        Assert.Equal(CommentaryLifecycle.New, Step(step.State, SellerExpansion(3)).Event!.Lifecycle);
    }

    [Fact]
    public void MissingExternalEvidence_IsNeitherSupportNorContradiction_AndNeverBlocksAFuturesEvent()
    {
        var e = Step(CommentaryState.Initial, SellerExpansion(1)).Event!;   // no Book/Basis/Options/Residual at all
        Assert.Equal(CommentaryEventType.SellerExpansion, e.EventType);
        Assert.Equal(EvidenceAgreement.Low, e.EvidenceAgreement);
        Assert.Empty(e.Confirmations); Assert.Empty(e.Contradictions);
        Assert.Contains("Book evidence unavailable", e.DataQuality);
        Assert.Contains("InterMarketBasis evidence unavailable", e.DataQuality);
    }

    // ---- evidence families and agreement ----

    static CommentaryFrame WithBook(CommentaryFrame f, long ofi, double micro) => f with { Ofi = ofi, MicroDev = micro };
    static CommentaryFrame WithBasis(CommentaryFrame f, double v) => f with { DeltaBasis = v };
    static CommentaryFrame WithResidual(CommentaryFrame f, double v) => f with { ResidualAvailable = true, DirectionalResidualPct = v };
    static CommentaryFrame WithOptionsShort(CommentaryFrame f) => f with { PePosition = "PutLongBuild", PeStrictDelta = 9200, CePosition = "Neutral", CeStrictDelta = 0 };

    [Fact]
    public void Agreement_Bands_FollowSupportingAndContradictingExternalFamilies()
    {
        var seller = SellerExpansion(1);
        // HIGH: three families support SHORT, none contradict.
        var high = WithResidual(WithOptionsShort(WithBook(seller, -6200, -0.31)), -5.2);
        Assert.Equal(EvidenceAgreement.High, Step(CommentaryState.Initial, high).Event!.EvidenceAgreement);
        // Three support but one contradicts => MEDIUM (not HIGH).
        Assert.Equal(EvidenceAgreement.Medium, Step(CommentaryState.Initial, WithBasis(high, +3)).Event!.EvidenceAgreement);
        // One supports, none contradict => MEDIUM.
        Assert.Equal(EvidenceAgreement.Medium, Step(CommentaryState.Initial, WithBook(seller, -10, -0.1)).Event!.EvidenceAgreement);
        // One supports, one contradicts => MEDIUM; one supports, two contradict => LOW.
        var oneAgainst = WithBook(WithResidual(seller, +2), -10, -0.1);
        Assert.Equal(EvidenceAgreement.Medium, Step(CommentaryState.Initial, oneAgainst).Event!.EvidenceAgreement);
        Assert.Equal(EvidenceAgreement.Low, Step(CommentaryState.Initial, WithBasis(oneAgainst, +1)).Event!.EvidenceAgreement);
        // Nothing supports (only contradiction) => LOW.
        Assert.Equal(EvidenceAgreement.Low, Step(CommentaryState.Initial, WithBook(seller, +10, +0.1)).Event!.EvidenceAgreement);
    }

    [Fact]
    public void BookFamily_CountsOnce_EvenWhenOfiAndMicroDevAgree_AndDisagreementIsNeutral()
    {
        var both = Step(CommentaryState.Initial, WithBook(SellerExpansion(1), -6200, -0.31)).Event!;
        Assert.Equal(EvidenceAgreement.Medium, both.EvidenceAgreement);        // one family, not two
        Assert.Equal(2, both.Confirmations.Count);                              // both metrics are listed as that single family's evidence
        Assert.All(both.Confirmations, c => Assert.Equal(EvidenceFamily.Book, c.Family));
        var split = CommentaryEvaluator.Families(WithBook(SellerExpansion(1), -6200, +0.31));
        Assert.Equal(FamilyDirection.Neutral, split[EvidenceFamily.Book]);
    }

    [Fact]
    public void OptionsFamily_NeedsPositionAndFlow_PcrIvAndShortCoverNeverCreateDirection()
    {
        Assert.Equal(FamilyDirection.Long, CommentaryEvaluator.Families(Frame(1) with { CePosition = "CallLongBuild", CeStrictDelta = 5 })[EvidenceFamily.Options]);
        Assert.Equal(FamilyDirection.Long, CommentaryEvaluator.Families(Frame(1) with { PePosition = "PutWriting", PeStrictDelta = -5 })[EvidenceFamily.Options]);
        Assert.Equal(FamilyDirection.Short, CommentaryEvaluator.Families(Frame(1) with { PePosition = "PutLongBuild", PeStrictDelta = 5 })[EvidenceFamily.Options]);
        Assert.Equal(FamilyDirection.Short, CommentaryEvaluator.Families(Frame(1) with { CePosition = "CallWriting", CeStrictDelta = -5 })[EvidenceFamily.Options]);
        // Position without matching aggressive flow, short-cover labels, and PCR/IV/skew alone are not directional.
        Assert.Equal(FamilyDirection.Neutral, CommentaryEvaluator.Families(Frame(1) with { CePosition = "CallLongBuild", CeStrictDelta = -5 })[EvidenceFamily.Options]);
        Assert.Equal(FamilyDirection.Neutral, CommentaryEvaluator.Families(Frame(1) with { CePosition = "CallShortCover", CeStrictDelta = 50 })[EvidenceFamily.Options]);
        Assert.Equal(FamilyDirection.Unavailable, CommentaryEvaluator.Families(Frame(1) with { VolPcr = 3.0, RollVolPcr = 1.0, CeDeltaIv = 4, PeDeltaIv = 4, IvSkew = 2 })[EvidenceFamily.Options]);
        Assert.Equal(FamilyDirection.Neutral, CommentaryEvaluator.Families(Frame(1) with { CePosition = "Neutral", CeStrictDelta = 0, VolPcr = 9 })[EvidenceFamily.Options]);
    }

    [Fact]
    public void FamilyConflict_RequiresLongAndShortAvailableFamilies_WithNoPrimaryEvent()
    {
        var conflict = WithResidual(WithBasis(Frame(1), +2), -3);                // Basis Long, Residual Short, no flow at all
        var e = Step(CommentaryState.Initial, conflict).Event!;
        Assert.Equal(CommentaryEventType.FamilyConflict, e.EventType);
        Assert.Equal(EventBias.Neutral, e.EventBias); Assert.Equal(EvidenceAgreement.Low, e.EvidenceAgreement);
        Assert.Equal(MarketRegime.Conflict, e.MarketRegime);

        Assert.Null(Step(CommentaryState.Initial, WithBasis(Frame(1), +2)).Event);                                  // one family only: unavailable others are not a conflict
        Assert.Null(Step(CommentaryState.Initial, WithResidual(WithBasis(Frame(1), +2), +1)).Event);                // both Long
        Assert.NotEqual(CommentaryEventType.FamilyConflict,
            Step(CommentaryState.Initial, WithResidual(SellerExpansion(1), +3)).Event!.EventType);                  // a primary futures event wins over a family conflict
    }

    // ---- lifecycle ----

    [Fact]
    public void UnchangedContinuation_PersistsNothing_ButAdvancesTheCheckpoint()
    {
        var state = Run(out var events, SellerExpansion(1), SellerExpansion(2), SellerExpansion(3), SellerExpansion(4));
        Assert.Single(events);
        Assert.Equal(4, state.LastEvaluatedBarSeq);
        Assert.Equal(CommentaryEventType.SellerExpansion, state.Active!.Type);
    }

    [Fact]
    public void Strengthening_And_Weakening_AreEdgeTriggeredPhaseChanges_NotRepeatedPerBar()
    {
        var s = SellerExpansion(0);
        Run(out var events,
            s with { BarSeq = 1 },                                           // New
            s with { BarSeq = 2, Evolution = "Strengthening" },              // -> Strengthening
            s with { BarSeq = 3, Evolution = "Strengthening" },              // same phase: nothing
            s with { BarSeq = 4, RollingStrictAbsDeltaChange = 500 },        // still strengthening: nothing
            s with { BarSeq = 5, Evolution = "Weakening" },                  // -> Weakening
            s with { BarSeq = 6, RollingStrictAbsDeltaChange = -10 },        // same phase: nothing
            s with { BarSeq = 7, Evolution = "Strengthening" });             // -> Strengthening
        Assert.Equal([CommentaryLifecycle.New, CommentaryLifecycle.Strengthening, CommentaryLifecycle.Weakening, CommentaryLifecycle.Strengthening],
            events.Select(e => e.Lifecycle));
        Assert.Equal([1, 2, 5, 7], events.Select(e => e.BarSeq));
    }

    [Fact]
    public void ArrivalOrLossOfASupportingFamily_AndAgreementMovement_DriveThePhase()
    {
        var seller = SellerExpansion(0);
        Run(out var events,
            seller with { BarSeq = 1 },
            WithBook(seller with { BarSeq = 2 }, -10, -0.1),                 // Book becomes supportive: LOW -> MEDIUM => Strengthening
            WithBook(seller with { BarSeq = 3 }, -12, -0.2),                 // unchanged support
            seller with { BarSeq = 4 });                                    // Book support disappears => Weakening
        Assert.Equal([CommentaryLifecycle.New, CommentaryLifecycle.Strengthening, CommentaryLifecycle.Weakening], events.Select(e => e.Lifecycle));
        Assert.Equal(EvidenceAgreement.Low, events[2].EvidenceAgreement);
    }

    [Fact]
    public void ActiveEventFollowedByNoMaterialEvent_WritesOneResolvedRow()
    {
        var state = Run(out var events, SellerExpansion(1), Frame(2), Frame(3));
        Assert.Equal([CommentaryLifecycle.New, CommentaryLifecycle.Resolved], events.Select(e => e.Lifecycle));
        Assert.Equal(2, events[1].BarSeq);
        Assert.Equal(CommentaryEventType.SellerExpansion, events[1].EventType);
        Assert.Null(state.Active);
        Assert.Equal(MarketRegime.Neutral, state.Regime);                    // resolving does not touch the regime
    }

    [Fact]
    public void OppositeDirectionalReplacement_IsFlipped_WithoutASeparateResolvedRow()
    {
        Run(out var events, SellerExpansion(1), SellerExpansion(2) with { BarSeq = 2, RollOiDelta = -9, StrictDelta = 10, EnrichedDelta = 10, RollingStrictDelta = 10, RollingEnrichedDelta = 10, BarPriceDisplacement = 3, RollingPriceDisplacement = 4 });
        Assert.Equal([CommentaryLifecycle.New, CommentaryLifecycle.Flipped], events.Select(e => e.Lifecycle));
        Assert.Equal(CommentaryEventType.ShortCovering, events[1].EventType);
        Assert.Equal(EventBias.Short, events[1].PreviousBias); Assert.Equal(EventBias.Long, events[1].EventBias);
        Assert.DoesNotContain(events, e => e.Lifecycle == CommentaryLifecycle.Resolved);
    }

    [Fact]
    public void NonOppositeReplacement_IsNew_NotFlipped()
    {
        Run(out var events, SellerAbsorption(1), SellerExpansion(2));        // neutral absorption -> short expansion
        Assert.Equal([CommentaryLifecycle.New, CommentaryLifecycle.New], events.Select(e => e.Lifecycle));
        Assert.Equal(CommentaryEventType.SellerExpansion, events[1].EventType);
    }

    // ---- rejection confirmation ----

    static CommentaryFrame RejectionFrame(int seq) => SellerAbsorption(seq) with
    {
        BarPriceDisplacement = 2.5, Evolution = "Weakening", Ofi = 3100, MicroDev = 0.2, ResidualAvailable = true, DirectionalResidualPct = 1.4,
    };

    [Fact]
    public void SellerRejection_RequiresPriorAbsorption_PriceTurn_DominanceChange_AndTwoIndependentFamilies()
    {
        var state = Step(CommentaryState.Initial, SellerAbsorption(1)).State;
        var e = Step(state, RejectionFrame(2)).Event!;
        Assert.Equal(CommentaryEventType.SellerRejectionConfirmed, e.EventType);
        Assert.Equal(EventBias.Long, e.EventBias); Assert.Equal(CommentaryLifecycle.Confirmed, e.Lifecycle);
        Assert.Equal(EventBias.Neutral, e.PreviousBias); Assert.True(e.BiasChanged);
        Assert.True(e.ShouldNotifyTelegram); Assert.Equal("Confirmed", e.NotificationReason);

        // Without the prior absorption it can never be a rejection.
        Assert.NotEqual(CommentaryEventType.SellerRejectionConfirmed, Step(CommentaryState.Initial, RejectionFrame(2)).Event?.EventType);
        // One family only (Book counted once even with OFI and MicroDev) is not enough.
        Assert.NotEqual(CommentaryEventType.SellerRejectionConfirmed, Step(state, RejectionFrame(2) with { ResidualAvailable = false, DirectionalResidualPct = null }).Event?.EventType);
        // Price must have turned up.
        Assert.NotEqual(CommentaryEventType.SellerRejectionConfirmed, Step(state, RejectionFrame(2) with { BarPriceDisplacement = 0 }).Event?.EventType);
        // A dominance change is mandatory: Evolution/|Strict| change alone absent => no rejection.
        Assert.NotEqual(CommentaryEventType.SellerRejectionConfirmed, Step(state, RejectionFrame(2) with { Evolution = "Strengthening", RollingStrictAbsDeltaChange = 5 }).Event?.EventType);
        // |Strict| contraction or a flip to buyers also satisfies the dominance condition.
        Assert.Equal(CommentaryEventType.SellerRejectionConfirmed, Step(state, RejectionFrame(2) with { Evolution = null, RollingStrictAbsDeltaChange = -1 }).Event!.EventType);
        Assert.Equal(CommentaryEventType.SellerRejectionConfirmed, Step(state, RejectionFrame(2) with { Evolution = "FlipToBuyer" }).Event!.EventType);
    }

    [Fact]
    public void BuyerRejection_IsTheMirror_AndAbsorptionAloneNeverImpliesReversal()
    {
        var buyerAbsorption = BuyerExpansion(1) with { BarPriceDisplacement = -0.5, RollingPriceDisplacement = 9 };
        var state = Step(CommentaryState.Initial, buyerAbsorption).State;
        Assert.Equal(CommentaryEventType.BuyerAbsorption, state.Active!.Type);
        var e = Step(state, buyerAbsorption with { BarSeq = 2, BarPriceDisplacement = -3, Evolution = "FlipToSeller", Ofi = -50, MicroDev = -0.1, DeltaBasis = -2 }).Event!;
        Assert.Equal(CommentaryEventType.BuyerRejectionConfirmed, e.EventType); Assert.Equal(EventBias.Short, e.EventBias);
    }

    // ---- regime ----

    [Fact]
    public void Regime_FollowsTheExplicitTransitionTable_AndNeverDecaysWithTime()
    {
        var strong = WithBook(WithResidual(SellerExpansion(0), -4), -100, -0.2);                   // two families => MEDIUM+
        var weakSeller = SellerExpansion(0);                                                        // LOW agreement

        // LOW agreement cannot start a regime; MEDIUM can.
        Assert.Equal(MarketRegime.Neutral, Step(CommentaryState.Initial, weakSeller with { BarSeq = 1 }).State.Regime);
        var bearish = Step(CommentaryState.Initial, strong with { BarSeq = 1 }).State;
        Assert.Equal(MarketRegime.Bearish, bearish.Regime);

        // 500 unchanged bars later the regime is still Bearish (no time-based expiry).
        var state = bearish;
        for (var i = 2; i < 500; i++) state = Step(state, strong with { BarSeq = i }).State;
        Assert.Equal(MarketRegime.Bearish, state.Regime);

        // Seller absorption challenges a bearish regime => Transition; a rejection stays Transition.
        var transition = Step(state, SellerAbsorption(500)).State;
        Assert.Equal(MarketRegime.Transition, transition.Regime);

        // Transition -> Bullish only through BuyerExpansion with MEDIUM/HIGH; a LOW one leaves it.
        var lowBuyer = BuyerExpansion(501);
        Assert.Equal(MarketRegime.Transition, Step(transition, lowBuyer).State.Regime);
        var strongBuyer = WithBook(WithResidual(BuyerExpansion(501), +4), +100, +0.2);
        Assert.Equal(MarketRegime.Bullish, Step(transition, strongBuyer).State.Regime);

        // Bearish + any LONG directional primary event (even LOW) => Transition.
        Assert.Equal(MarketRegime.Transition, Step(bearish, BuyerExpansion(2)).State.Regime);
        // Conflict is entered by FamilyConflict and left by the next MEDIUM/HIGH directional primary event.
        var conflict = Step(bearish, WithResidual(WithBasis(Frame(2), +2), -3)).State;
        Assert.Equal(MarketRegime.Conflict, conflict.Regime);
        Assert.Equal(MarketRegime.Conflict, Step(conflict, SellerExpansion(3)).State.Regime);       // LOW => stays
        Assert.Equal(MarketRegime.Bearish, Step(conflict, strong with { BarSeq = 3 }).State.Regime);
    }

    // ---- bias change / Telegram policy ----

    [Fact]
    public void TelegramPolicy_IsLimitedToConfirmedFlippedAndActualBiasChange()
    {
        // Ordinary New expansion from silence: bias Neutral -> Short is recorded, but is NOT Telegram-eligible.
        var started = Step(CommentaryState.Initial, SellerExpansion(1)).Event!;
        Assert.True(started.BiasChanged); Assert.False(started.ShouldNotifyTelegram);
        Assert.Equal(CommentarySeverity.Medium, started.Severity);

        // Directional -> Neutral through a persisted neutral event (absorption) is eligible.
        var s1 = Step(CommentaryState.Initial, SellerExpansion(1)).State;
        var absorbed = Step(s1, SellerAbsorption(2)).Event!;
        Assert.Equal(EventBias.Short, absorbed.PreviousBias); Assert.True(absorbed.ShouldNotifyTelegram); Assert.Equal("BiasChanged", absorbed.NotificationReason);
        Assert.Equal(CommentarySeverity.High, absorbed.Severity);

        // Flipped is eligible; Resolved is never eligible by itself; Strengthening/Weakening are Dashboard-only.
        var flipped = Step(s1, BuyerExpansion(2) with { RollOiDelta = -3 }).Event!;
        Assert.Equal(CommentaryLifecycle.Flipped, flipped.Lifecycle); Assert.True(flipped.ShouldNotifyTelegram);
        var resolved = Step(s1, Frame(2)).Event!;
        Assert.Equal(CommentaryLifecycle.Resolved, resolved.Lifecycle); Assert.False(resolved.ShouldNotifyTelegram);
        var strengthening = Step(s1, SellerExpansion(2) with { Evolution = "Strengthening" }).Event!;
        Assert.Equal(CommentaryLifecycle.Strengthening, strengthening.Lifecycle); Assert.False(strengthening.ShouldNotifyTelegram);
        Assert.Equal(CommentarySeverity.Info, strengthening.Severity);
    }

    // ---- determinism, identity, ordering, rendering ----

    [Fact]
    public void SameFramesAlwaysProduceIdenticalEvents_AndIdentitiesAreUniquePerBarEventLifecycleBias()
    {
        CommentaryFrame[] Frames()
        {
            var rng = new Random(11); var list = new List<CommentaryFrame>();
            for (var i = 1; i <= 400; i++)
            {
                var dir = rng.Next(0, 3) - 1;
                list.Add(Frame(i) with
                {
                    StrictDelta = dir * 100, EnrichedDelta = dir * 90, RollingStrictDelta = dir * 1000, RollingEnrichedDelta = dir * 900,
                    RollOiDelta = rng.Next(-5, 6), BarPriceDisplacement = rng.Next(-3, 4), RollingPriceDisplacement = rng.Next(-3, 4),
                    Evolution = new[] { "Strengthening", "Weakening", "Stable", null }[rng.Next(0, 4)], RollingStrictAbsDeltaChange = rng.Next(-3, 4),
                    Ofi = rng.Next(-2, 3), ResidualAvailable = rng.Next(0, 2) == 0, DirectionalResidualPct = rng.Next(-2, 3),
                });
            }

            return list.ToArray();
        }

        Run(out var a, Frames()); Run(out var b, Frames());
        Assert.NotEmpty(a);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(a), System.Text.Json.JsonSerializer.Serialize(b)); // identical events across two independent runs
        Assert.Equal(a.Count, a.Select(e => e.Identity).Distinct().Count());
        Assert.Contains(a, e => e.Lifecycle == CommentaryLifecycle.Resolved);
        Assert.Contains(a, e => e.Lifecycle == CommentaryLifecycle.Flipped);
    }

    [Fact]
    public void Evaluation_RequiresStrictBarSeqOrder()
    {
        var state = Step(CommentaryState.Initial, SellerExpansion(5)).State;
        Assert.Throws<ArgumentException>(() => Step(state, SellerExpansion(5)));
        Assert.Throws<ArgumentException>(() => Step(state, SellerExpansion(4)));
        Assert.NotNull(Step(state, SellerExpansion(6)));
    }

    [Fact]
    public void Renderer_MentionsOnlyEvidencePresentInTheEvent_AndNeverTheWordConfidence()
    {
        var e = Step(CommentaryState.Initial, WithBook(SellerExpansion(1), -6200, -0.31)).Event!;
        var text = e.RenderedCommentary;
        Assert.StartsWith("Seller expansion started.", text);
        Assert.Contains("Bias SHORT | Regime BEARISH | EvidenceAgreement MEDIUM.", text);
        Assert.Contains("Strict Δ -18400", text);
        Assert.Contains("Supported by Book (OFI -6200, MicroDev -0.31).", text);
        Assert.DoesNotContain("Residual (", text); Assert.DoesNotContain("Options", text.Replace("Options evidence", ""));
        Assert.Contains("Residual evidence unavailable", text);              // absence is stated as a data-quality note, not as support
        Assert.DoesNotContain("onfidence", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onfidence", string.Join(' ', Enum.GetNames<EvidenceAgreement>()), StringComparison.OrdinalIgnoreCase);

        var resolved = Step(Step(CommentaryState.Initial, SellerExpansion(1)).State, Frame(2)).Event!;
        Assert.Contains("resolved.", resolved.RenderedCommentary);
        Assert.DoesNotContain("Evidence:", resolved.RenderedCommentary);
    }
}
