using DragonCards.Core;
using DragonCards.Desktop;

namespace DragonCards.Tests;

public sealed class PresentationDirectorTests
{
    [Fact]
    public void RecipesCoverEveryMatchEventKind()
    {
        var kinds = Enum.GetValues<MatchEventKind>();

        Assert.Equal(kinds.Length, AnimationRecipes.All.Count);
        foreach (var kind in kinds)
        {
            var recipe = AnimationRecipes.For(kind);
            Assert.True(recipe.DurationSeconds > 0f);
            Assert.False(string.IsNullOrWhiteSpace(recipe.Caption));
            Assert.NotEmpty(recipe.SoundCues);
            if (recipe.BlocksInput)
            {
                Assert.Equal(PresentationPriority.Primary, recipe.Priority);
            }
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => AnimationRecipes.For((MatchEventKind)int.MaxValue));
    }

    [Fact]
    public void NaturalPaceKeepsMajorNarrationReadableAfterMotionSettles()
    {
        var director = new PresentationDirector();
        director.Enqueue([Event(MatchEventKind.CardPlayed)]);
        var active = Assert.IsType<PresentationBeat>(director.Active);

        Assert.True(active.CaptionDuration >= 1.4f);
        Assert.True(active.CaptionDuration > active.AnimationDuration);

        director.Update(active.AnimationDuration + 0.01f);

        Assert.False(director.IsBlocking);
        Assert.NotNull(director.Active);
        Assert.True(active.CaptionOpacity > 0.9f);
    }

    [Theory]
    [InlineData(55, 75, 140)]
    [InlineData(70, 100, 100)]
    [InlineData(100, 125, 75)]
    [InlineData(140, 150, 55)]
    public void LegacyInteractionPaceMigratesToSeparateSettings(int legacy, int animation, int message)
    {
        Assert.Equal(animation, PresentationPacing.MigrateLegacyAnimationSpeed(legacy));
        Assert.Equal(message, PresentationPacing.MigrateLegacyMessageDuration(legacy));
    }

    [Fact]
    public void RepeatedDrawResourceAndDamageEventsAreCoalesced()
    {
        var director = new PresentationDirector();
        director.Enqueue(
        [
            Event(MatchEventKind.CardDrawn, playerIndex: 0, amount: 0),
            Event(MatchEventKind.CardDrawn, playerIndex: 0, amount: 0),
            Event(MatchEventKind.EnergySpent, playerIndex: 0, element: "Fire", amount: 1),
            Event(MatchEventKind.EnergySpent, playerIndex: 0, element: "Fire", amount: 2),
            Event(MatchEventKind.DamageTaken, playerIndex: 1, amount: 1),
            Event(MatchEventKind.DamageTaken, playerIndex: 1, amount: 1)
        ]);

        var initial = director.ActiveBeats;
        var draw = Assert.Single(initial, beat => beat.Event.Kind == MatchEventKind.CardDrawn);
        var energy = Assert.Single(initial, beat => beat.Event.Kind == MatchEventKind.EnergySpent);
        Assert.Equal(2, draw.SourceEvents.Count);
        Assert.Equal(2, draw.Event.Amount);
        Assert.Equal("Drew 2 cards.", draw.Event.Message);
        Assert.Equal(2, energy.SourceEvents.Count);
        Assert.Equal(3, energy.Event.Amount);

        var initialActivations = director.DrainActivations();
        Assert.Equal(2, initialActivations.Count);
        Assert.Contains(initialActivations, activation => activation.Event.Kind == MatchEventKind.CardDrawn && activation.SourceEventCount == 2);
        Assert.Contains(initialActivations, activation => activation.Event.Kind == MatchEventKind.EnergySpent && activation.SourceEventCount == 2);

        director.Update(draw.Duration);

        var damage = Assert.IsType<PresentationBeat>(director.Active);
        Assert.Equal(MatchEventKind.DamageTaken, damage.Event.Kind);
        Assert.Equal(2, damage.Event.Amount);
        Assert.Equal(2, damage.SourceEvents.Count);
        Assert.Equal(MatchEventKind.DamageTaken, Assert.Single(director.DrainActivations()).Event.Kind);
    }

    [Fact]
    public void SecondaryFeedbackRunsAlongsidePrimaryBeatWithoutBlockingIt()
    {
        var director = new PresentationDirector();
        director.Enqueue(
        [
            Event(MatchEventKind.EnergySpent, element: "Fire", amount: 1),
            Event(MatchEventKind.CardPlayed, cardId: "fire-ember-whelp")
        ]);

        Assert.Equal(2, director.ActiveBeats.Count);
        Assert.True(director.IsBlocking);
        var energy = Assert.Single(director.ActiveBeats, beat => beat.Event.Kind == MatchEventKind.EnergySpent);
        Assert.False(energy.Blocking);

        director.Update(energy.Duration);

        var active = Assert.IsType<PresentationBeat>(director.Active);
        Assert.Equal(MatchEventKind.CardPlayed, active.Event.Kind);
        Assert.Single(director.ActiveBeats);
        Assert.False(director.IsBlocking);
    }

    [Fact]
    public void SpellResolutionMarkerDoesNotRepeatThePlayMovement()
    {
        var director = new PresentationDirector();
        var destination = new ZoneRef(0, "DiscardPile", 0);
        director.Enqueue(
        [
            new MatchEvent
            {
                Kind = MatchEventKind.CardPlayed,
                PlayerIndex = 0,
                CardId = "fire-spark-burst",
                InstanceId = "spell-1",
                From = new ZoneRef(0, "Hand", 0),
                To = destination,
                Message = "Spark Burst played."
            },
            new MatchEvent
            {
                Kind = MatchEventKind.CardDiscarded,
                PlayerIndex = 0,
                CardId = "fire-spark-burst",
                InstanceId = "spell-1",
                To = destination,
                Message = "Spark Burst resolved."
            }
        ]);

        var active = Assert.IsType<PresentationBeat>(director.Active);
        Assert.Equal(MatchEventKind.CardPlayed, active.Event.Kind);
        Assert.Single(director.DrainActivations());

        director.Update(active.Duration);
        Assert.Null(director.Active);
        Assert.Empty(director.DrainActivations());
    }

    [Fact]
    public void UpdateConsumesLeftoverDeltaAcrossBeatsAndActions()
    {
        var director = new PresentationDirector();
        director.Enqueue(
        [
            Event(MatchEventKind.CardPlayed),
            Event(MatchEventKind.AttackDeclared)
        ]);
        director.Enqueue([Event(MatchEventKind.CardDrawn)]);
        director.DrainActivations();

        var firstActionDuration = AnimationRecipes.For(MatchEventKind.CardPlayed).MinimumCaptionSeconds +
            AnimationRecipes.For(MatchEventKind.AttackDeclared).MinimumCaptionSeconds;
        director.Update(firstActionDuration + 0.01f);

        var active = Assert.IsType<PresentationBeat>(director.Active);
        Assert.Equal(MatchEventKind.CardDrawn, active.Event.Kind);
        Assert.InRange(active.Elapsed, 0.009f, 0.011f);
        var activations = director.DrainActivations();
        Assert.Equal([MatchEventKind.AttackDeclared, MatchEventKind.CardDrawn], activations.Select(item => item.Event.Kind));
    }

    [Fact]
    public void SkipDropsTheWholeActionOnlyAfterMinimumDisplayTime()
    {
        var director = new PresentationDirector();
        director.Enqueue(
        [
            Event(MatchEventKind.CardPlayed),
            Event(MatchEventKind.AttackDeclared)
        ]);
        director.DrainActivations();

        director.Update(PresentationDirector.MinimumSkipDelaySeconds - 0.001f);
        Assert.False(director.SkipActive());

        director.Update(0.002f);
        Assert.True(director.SkipActive());
        Assert.Null(director.Active);
        Assert.Equal(0, director.PendingActionCount);
        Assert.Empty(director.DrainActivations());
    }

    [Fact]
    public void ClearRemovesPresentationAndUnplayedActivationCues()
    {
        var director = new PresentationDirector();
        director.Enqueue([Event(MatchEventKind.CardPlayed)]);

        Assert.True(director.HasPendingActivations);
        director.Clear();

        Assert.Null(director.Active);
        Assert.False(director.IsBlocking);
        Assert.Equal(0, director.PendingActionCount);
        Assert.Empty(director.DrainActivations());
    }

    [Fact]
    public void ReducedMotionUsesBriefNonBlockingHighlightAndRetainsMeaning()
    {
        var standard = AnimationRecipes.For(MatchEventKind.CardPlayed);
        var director = new PresentationDirector { ReducedMotion = true };
        director.Enqueue([Event(MatchEventKind.CardPlayed)]);

        var active = Assert.IsType<PresentationBeat>(director.Active);
        Assert.Equal(0f, active.Recipe.MotionSeconds);
        Assert.Equal(PresentationDirector.ReducedMotionHighlightSeconds, active.Recipe.SettleSeconds);
        Assert.True(active.CaptionDuration >= standard.MinimumCaptionSeconds);
        Assert.True(active.Duration > active.AnimationDuration);
        Assert.False(active.Blocking);
        Assert.False(director.IsBlocking);
        Assert.Equal(PresentationMotion.StaticHighlight, active.Recipe.Motion);
        Assert.Equal(standard.Caption, active.Recipe.Caption);
        Assert.Equal(standard.AddToTimeline, active.Recipe.AddToTimeline);
        Assert.Equal(standard.SoundCues, active.Recipe.SoundCues);

        director.Update(active.Duration);
        Assert.Null(director.Active);
    }

    [Fact]
    public void AnimationSpeedScalesMotionWithoutShorteningNarration()
    {
        var natural = new PresentationDirector();
        natural.Enqueue([Event(MatchEventKind.CardPlayed)]);
        var naturalBeat = Assert.IsType<PresentationBeat>(natural.Active);
        var relaxed = new PresentationDirector { AnimationSpeedMultiplier = 0.75f };
        relaxed.Enqueue([Event(MatchEventKind.CardPlayed)]);
        var relaxedBeat = Assert.IsType<PresentationBeat>(relaxed.Active);

        Assert.True(relaxedBeat.AnimationDuration > naturalBeat.AnimationDuration);
        Assert.Equal(naturalBeat.CaptionDuration, relaxedBeat.CaptionDuration);
        relaxed.Update(naturalBeat.AnimationDuration);

        Assert.InRange(relaxedBeat.AnimationProgress, 0.74f, 0.76f);
        Assert.True(relaxed.IsBlocking);
    }

    [Fact]
    public void MessageDurationScalesNarrationWithoutChangingMotion()
    {
        var comfortable = new PresentationDirector();
        comfortable.Enqueue([Event(MatchEventKind.CardPlayed)]);
        var comfortableBeat = Assert.IsType<PresentationBeat>(comfortable.Active);
        var extended = new PresentationDirector { MessageDurationMultiplier = 1.4f };
        extended.Enqueue([Event(MatchEventKind.CardPlayed)]);
        var extendedBeat = Assert.IsType<PresentationBeat>(extended.Active);

        Assert.Equal(comfortableBeat.AnimationDuration, extendedBeat.AnimationDuration);
        Assert.True(extendedBeat.CaptionDuration > comfortableBeat.CaptionDuration);
    }

    [Fact]
    public void ActivationAudioAddsCardTypeCueAndCanCancelLegacyQueue()
    {
        var director = new PresentationDirector();
        director.Enqueue([Event(MatchEventKind.CardPlayed, cardId: "fire-ember-whelp")]);
        var activation = Assert.Single(director.DrainActivations());
        var audio = new AudioService();
        audio.Configure(() => 80, () => 70, () => false, _ => "Unit");

        Assert.Equal(
            [SoundKeys.CardPlay, SoundKeys.UnitSummon],
            audio.ResolveActivationCueKeys(activation));

        audio.PlayForEvents([activation.Event]);
        Assert.Equal(1, audio.QueuedCueCount);
        audio.CancelQueuedCues();
        Assert.Equal(0, audio.QueuedCueCount);
    }

    [Fact]
    public void CombatDiscardEventsIncludeFieldOriginsAndDiscardDestinations()
    {
        var data = GameData.LoadDefault();
        var engine = DragonDuelEngine.Create(
            data,
            DragonCardsModeIds.DragonDuel,
            data.DecksById["starter-fire"],
            data.DecksById["starter-ice"],
            seed: 7,
            shuffle: false);
        engine.State.ActivePlayer.UnitField.Add(new CardInstance("fire-ember-whelp"));
        engine.State.DefendingPlayer.UnitField.Add(new CardInstance("wind-gale-scout"));
        engine.State.PhaseIndex = engine.State.Mode.Phases.FindIndex(phase => phase == "Combat");

        Assert.True(engine.DeclareAttack(0).Success);
        Assert.True(engine.Block(0).Success);

        var result = GameActionResult.Fail("Combat did not resolve.");
        for (var guard = 0; guard < 4 && engine.State.PendingCombatAction is not null; guard++)
        {
            result = engine.PassCombatAction(engine.State.PendingCombatAction.PriorityPlayerIndex);
            Assert.True(result.Success, result.Message);
        }

        var discarded = Assert.Single(result.Events, matchEvent => matchEvent.Kind == MatchEventKind.CardDiscarded);
        Assert.Equal(new ZoneRef(0, "UnitField", 0), discarded.From);
        Assert.Equal(new ZoneRef(0, "DiscardPile", 0), discarded.To);
    }

    [Fact]
    public void TravelingCardsSuppressOnlyTheirMatchingLiveDestinationInstances()
    {
        var director = new PresentationDirector();
        director.Enqueue([
            new MatchEvent
            {
                Kind = MatchEventKind.CardDrawn,
                PlayerIndex = 0,
                CardId = "fire-ember-whelp",
                InstanceId = "draw-1",
                From = new ZoneRef(0, "Deck"),
                To = new ZoneRef(0, "Hand", 4),
                Message = "Drew a card."
            },
            new MatchEvent
            {
                Kind = MatchEventKind.CardDrawn,
                PlayerIndex = 0,
                CardId = "fire-cinder-adept",
                InstanceId = "draw-2",
                From = new ZoneRef(0, "Deck"),
                To = new ZoneRef(0, "Hand", 5),
                Message = "Drew a card."
            }
        ]);

        Assert.True(PresentationVisibility.SuppressesDestination(director.ActiveBeats, new ZoneRef(0, "Hand", 4), "draw-1", reducedMotion: false));
        Assert.True(PresentationVisibility.SuppressesDestination(director.ActiveBeats, new ZoneRef(0, "Hand", 5), "draw-2", reducedMotion: false));
        Assert.True(PresentationVisibility.SuppressesDestination(director.ActiveBeats, new ZoneRef(0, "Hand", 6), "draw-1", reducedMotion: false));
        Assert.False(PresentationVisibility.SuppressesDestination(director.ActiveBeats, new ZoneRef(0, "Hand", 4), "different-instance", reducedMotion: false));
        Assert.False(PresentationVisibility.SuppressesDestination(director.ActiveBeats, new ZoneRef(0, "Hand", 4), "draw-1", reducedMotion: true));
    }

    private static MatchEvent Event(
        MatchEventKind kind,
        int playerIndex = 0,
        string cardId = "",
        string element = "",
        int amount = 0) => new()
        {
            Kind = kind,
            PlayerIndex = playerIndex,
            CardId = cardId,
            InstanceId = string.IsNullOrWhiteSpace(cardId) ? "" : $"instance-{cardId}",
            Element = element,
            Amount = amount,
            From = new ZoneRef(playerIndex, "Source", 0),
            To = new ZoneRef(playerIndex, "Destination", 0),
            Message = kind.ToString()
        };
}
