using DragonCards.Core;
using DragonCards.Networking;

namespace DragonCards.Tests;

public sealed class DragonCardsCoreTests
{
    [Fact]
    public void DefaultDataLoadsAndValidates()
    {
        var data = LoadData();

        Assert.True(data.GameModesById.ContainsKey("dragon-duel"));
        Assert.True(data.Cards.Count >= 80);
        Assert.Contains(data.Cards, card => card.Type == "Unit");
        Assert.Contains(data.Cards, card => card.Type == "Support");
        Assert.Contains(data.Cards, card => card.Type == "Spell");
        Assert.Contains(data.Cards, card => card.Abilities.Count > 0);
        Assert.Equal(2000, data.GameModesById["dragon-duel"].ElementAdvantage?.PowerBonus);
        Assert.Contains("Fire", data.GameModesById["dragon-duel"].ElementAdvantage!.StrongAgainst["Water"]);
        Assert.Empty(GameDataValidator.Validate(data));
    }

    [Fact]
    public void ElementAdvantageDataValidatesUnknownElementsAndBonus()
    {
        var mode = new GameModeDefinition
        {
            Id = "test-duel",
            Name = "Test Duel",
            Elements = ["Fire"],
            Phases = DragonCardConstants.BuiltInPhases.ToList(),
            AllowedCardTypes = DragonCardConstants.BuiltInCardTypes.ToList(),
            ElementAdvantage = new ElementAdvantageDefinition
            {
                PowerBonus = 0,
                StrongAgainst = new Dictionary<string, List<string>>
                {
                    ["Fire"] = ["Water"],
                    ["Void"] = ["Fire"]
                }
            }
        };
        var data = new GameData([mode], [], []);

        var issues = GameDataValidator.Validate(data);

        Assert.Contains(issues, issue => issue.Code == "mode.element_advantage_bonus");
        Assert.Contains(issues, issue => issue.Code == "mode.element_advantage_source");
        Assert.Contains(issues, issue => issue.Code == "mode.element_advantage_target");
    }

    [Fact]
    public void StarterLibraryExpandedToOneHundredTwentyCardsWithVisualMetadata()
    {
        var data = LoadData();

        Assert.Equal(120, data.Cards.Count);
        Assert.Equal(data.Cards.Count, data.Cards.Select(card => card.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(data.Cards.Where(card => card.Visual is not null), card =>
        {
            Assert.False(string.IsNullOrWhiteSpace(card.Visual!.Frame));
            Assert.False(string.IsNullOrWhiteSpace(card.Visual.Effect));
        });
        Assert.Contains(data.Cards, card => card.Tags.Contains("Finisher", StringComparer.OrdinalIgnoreCase));
        Assert.Contains(data.Cards, card => card.Hooks.Contains("exhaust_enemy_unit_choice"));
    }

    [Fact]
    public void StarterDecksAreValidForDragonDuel()
    {
        var data = LoadData();

        foreach (var deck in data.Decks)
        {
            var issues = GameDataValidator.ValidateDeck(deck, data);
            Assert.Empty(issues);
            Assert.Equal(50, deck.Count);
        }
    }

    [Fact]
    public void StarterDecksContainRequiredRampPackage()
    {
        var data = LoadData();
        var flame = data.DecksById["starter-flame-gale"];
        var frost = data.DecksById["starter-frost-stone"];

        Assert.All(data.Decks, deck => Assert.All(deck.Cards, entry => Assert.InRange(entry.Value, 1, 3)));
        Assert.Equal(3, flame.Cards["fire-hearth-shrine"]);
        Assert.Equal(3, flame.Cards["wind-waystone-shrine"]);
        Assert.Equal(2, flame.Cards["fire-forge-caller"]);
        Assert.Equal(2, flame.Cards["wind-mapmaker"]);
        Assert.Equal(3, frost.Cards["ice-winter-shrine"]);
        Assert.Equal(3, frost.Cards["earth-grove-shrine"]);
        Assert.Equal(2, frost.Cards["ice-mirror-sage"]);
        Assert.Equal(2, frost.Cards["earth-grove-keeper"]);
    }

    [Fact]
    public void MatchStartsWithOpeningHandsAndAdvancesPhases()
    {
        var engine = CreateEngine();

        Assert.Equal("Ready", engine.State.CurrentPhase);
        Assert.Equal(5, engine.State.Players[0].Hand.Count);
        Assert.Equal(45, engine.State.Players[0].Deck.Count);

        var drawResult = engine.AdvancePhase();
        Assert.True(drawResult.Success);
        Assert.Equal("Draw", engine.State.CurrentPhase);
        Assert.Equal(6, engine.State.Players[0].Hand.Count);

        Assert.True(engine.AdvancePhase().Success);
        Assert.Equal("Main", engine.State.CurrentPhase);
    }

    [Fact]
    public void ActionResultsCarryPresentationEvents()
    {
        var engine = CreateEngine();

        var draw = engine.AdvancePhase();

        Assert.Contains(draw.Events, item => item.Kind == MatchEventKind.CardDrawn);

        Assert.True(engine.AdvancePhase().Success);
        var player = engine.State.ActivePlayer;
        player.Hand.Clear();
        player.Hand.Add(new CardInstance("fire-ember-whelp"));
        player.EnergyPool["Fire"] = 1;

        var play = engine.PlayCardFromHand(0);

        Assert.True(play.Success);
        Assert.Contains(play.Events, item => item.Kind == MatchEventKind.CardPlayed);
        Assert.Contains(play.Events, item => item.Kind == MatchEventKind.EnergySpent);

        var sacrifice = engine.SacrificeForEnergy(SacrificeSource.UnitField, 0);

        Assert.True(sacrifice.Success);
        Assert.Contains(sacrifice.Events, item => item.Kind == MatchEventKind.CardSacrificed);
        Assert.Contains(sacrifice.Events, item => item.Kind == MatchEventKind.EnergyGained);
    }

    [Fact]
    public void DecisionPhaseHelperSkipsReadyAndDrawIntoMain()
    {
        var engine = CreateEngine();

        var result = engine.AdvanceToNextDecisionPhase();

        Assert.True(result.Success);
        Assert.Equal("Main", engine.State.CurrentPhase);
        Assert.Equal(6, engine.State.ActivePlayer.Hand.Count);
        Assert.True(engine.CanAddEnergy("Fire"));
    }

    [Fact]
    public void AddsOneChosenEnergyPerTurn()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);

        var result = engine.AddEnergy("Fire");

        Assert.True(result.Success);
        Assert.Equal(1, engine.State.ActivePlayer.EnergyPool["Fire"]);
        Assert.True(engine.State.HasAddedEnergyThisTurn);
        Assert.False(engine.AddEnergy("Ice").Success);
    }

    [Fact]
    public void EnergyCapsAtTenPerElement()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        engine.State.ActivePlayer.EnergyPool["Fire"] = 10;

        var result = engine.AddEnergy("Fire");

        Assert.False(result.Success);
        Assert.Equal(10, engine.State.ActivePlayer.EnergyPool["Fire"]);
    }

    [Fact]
    public void ExactAndGenericEnergyCanBeSpentToPlayAUnit()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        player.Hand.Clear();
        player.Hand.Add(new CardInstance("fire-cinder-adept"));
        player.EnergyPool["Fire"] = 1;
        player.EnergyPool["Wind"] = 1;

        var playResult = engine.PlayCardFromHand(0);

        Assert.True(playResult.Success);
        Assert.Single(player.UnitField);
        Assert.Equal(0, player.EnergyPool["Fire"]);
        Assert.Equal(0, player.EnergyPool["Wind"]);
        Assert.Equal(1, player.LastPayment["Fire"]);
        Assert.Equal(1, player.LastPayment["Wind"]);
    }

    [Fact]
    public void InsufficientEnergyPreventsPlayingCard()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        player.Hand.Clear();
        player.Hand.Add(new CardInstance("fire-cinder-adept"));
        player.EnergyPool["Fire"] = 1;

        var result = engine.PlayCardFromHand(0);

        Assert.False(result.Success);
        Assert.Empty(player.UnitField);
        Assert.Single(player.Hand);
        Assert.Equal(1, player.EnergyPool["Fire"]);
    }

    [Fact]
    public void SpellHookDrawsThenMovesSpellToDiscard()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        player.Hand.Clear();
        player.Hand.Add(new CardInstance("fire-flare-reading"));
        player.EnergyPool["Fire"] = 2;
        var deckCountBefore = player.Deck.Count;

        var result = engine.PlayCardFromHand(0);

        Assert.True(result.Success);
        Assert.Single(player.Hand);
        Assert.Equal(deckCountBefore - 1, player.Deck.Count);
        Assert.Single(player.DiscardPile);
        Assert.Equal("fire-flare-reading", player.DiscardPile[0].CardId);
        Assert.Equal(0, player.EnergyPool["Fire"]);
    }

    [Fact]
    public void CostReductionIsConsumedByNextPlayedCard()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        player.Hand.Clear();
        player.Hand.Add(new CardInstance("fire-cinder-adept"));
        player.EnergyPool["Fire"] = 1;

        engine.ReduceNextCardCost(0, 1);
        var result = engine.PlayCardFromHand(0);

        Assert.True(result.Success);
        Assert.Single(player.UnitField);
        Assert.Equal(0, player.NextCardCostReduction);
        Assert.Equal(0, player.EnergyPool["Fire"]);
    }

    [Fact]
    public void HandSacrificeDiscardsCardAndGrantsHalfRoundedUpEnergy()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        player.Hand.Clear();
        player.Hand.Add(new CardInstance("fire-ashen-champion"));

        var result = engine.SacrificeForEnergy(SacrificeSource.Hand, 0);

        Assert.True(result.Success);
        Assert.Empty(player.Hand);
        Assert.Single(player.DiscardPile);
        Assert.Equal("fire-ashen-champion", player.DiscardPile[0].CardId);
        Assert.Equal(3, player.EnergyPool["Fire"]);
    }

    [Fact]
    public void FieldUnitSacrificeMovesUnitToDiscardAndGrantsEnergy()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        player.UnitField.Add(new CardInstance("fire-cinder-adept"));

        var result = engine.SacrificeForEnergy(SacrificeSource.UnitField, 0);

        Assert.True(result.Success);
        Assert.Empty(player.UnitField);
        Assert.Single(player.DiscardPile);
        Assert.Equal(1, player.EnergyPool["Fire"]);
    }

    [Fact]
    public void FieldSupportSacrificeMovesSupportToDiscardAndGrantsEnergy()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        player.SupportField.Add(new CardInstance("fire-forge-caller"));

        var result = engine.SacrificeForEnergy(SacrificeSource.SupportField, 0);

        Assert.True(result.Success);
        Assert.Empty(player.SupportField);
        Assert.Single(player.DiscardPile);
        Assert.Equal(1, player.EnergyPool["Fire"]);
    }

    [Fact]
    public void ZeroCostSacrificeStillGrantsAtLeastOneEnergy()
    {
        var engine = CreateZeroCostEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        player.Hand.Add(new CardInstance("zero-unit"));

        var result = engine.SacrificeForEnergy(SacrificeSource.Hand, 0);

        Assert.True(result.Success);
        Assert.Single(player.DiscardPile);
        Assert.Equal(1, player.EnergyPool["Fire"]);
    }

    [Fact]
    public void SacrificeFailsWhenTimingOrEnergyStateIsIllegal()
    {
        var engine = CreateEngine();
        var player = engine.State.ActivePlayer;
        player.Hand.Clear();
        player.Hand.Add(new CardInstance("fire-ember-whelp"));

        Assert.False(engine.SacrificeForEnergy(SacrificeSource.Hand, 0).Success);
        Assert.Single(player.Hand);

        AdvanceToMain(engine);
        player.EnergyPool["Fire"] = 10;
        Assert.False(engine.SacrificeForEnergy(SacrificeSource.Hand, 0).Success);
        Assert.Contains(player.Hand, card => card.CardId == "fire-ember-whelp");

        player.EnergyPool["Fire"] = 0;
        engine.State.PendingEnergyChoice = new PendingEnergyChoice(0, PendingEnergyChoiceType.Gain, 1, "Choose.");
        Assert.False(engine.SacrificeForEnergy(SacrificeSource.Hand, 0).Success);
        engine.State.PendingEnergyChoice = null;

        player.UnitField.Add(new CardInstance("fire-ember-whelp"));
        Assert.True(engine.AdvancePhase().Success);
        Assert.True(engine.DeclareAttack(0).Success);
        Assert.False(engine.SacrificeForEnergy(SacrificeSource.Hand, 0).Success);
    }

    [Fact]
    public void SacrificeDoesNotConsumeFreeEnergyAdd()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        player.Hand.Clear();
        player.Hand.Add(new CardInstance("fire-ember-whelp"));

        Assert.True(engine.SacrificeForEnergy(SacrificeSource.Hand, 0).Success);

        Assert.False(engine.State.HasAddedEnergyThisTurn);
        Assert.True(engine.CanAddEnergy("Fire"));
    }

    [Fact]
    public void FullUnitZoneBlocksPlayUntilAUnitIsSacrificed()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        player.Hand.Clear();
        player.Hand.Add(new CardInstance("fire-ember-whelp"));
        player.EnergyPool["Fire"] = 1;
        for (var i = 0; i < engine.State.Mode.ZoneLimits.UnitSlots; i++)
        {
            player.UnitField.Add(new CardInstance("fire-cinder-adept"));
        }

        Assert.False(engine.CanPlayCardFromHand(0));
        Assert.False(engine.PlayCardFromHand(0).Success);

        Assert.True(engine.SacrificeForEnergy(SacrificeSource.UnitField, 0).Success);

        Assert.True(engine.CanPlayCardFromHand(0));
        Assert.True(engine.PlayCardFromHand(0).Success);
        Assert.Equal(engine.State.Mode.ZoneLimits.UnitSlots, player.UnitField.Count);
    }

    [Fact]
    public void ShrineOnPlayGainsTwoChosenEnergyAndNetsPositive()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        player.Hand.Clear();
        player.Hand.Add(new CardInstance("fire-hearth-shrine"));
        player.EnergyPool["Fire"] = 1;

        var playResult = engine.PlayCardFromHand(0);

        Assert.True(playResult.Success);
        Assert.NotNull(engine.State.PendingEnergyChoice);
        Assert.Equal(0, player.EnergyPool["Fire"]);

        var choiceResult = engine.ResolveEnergyChoice("Fire");

        Assert.True(choiceResult.Success);
        Assert.Equal(2, player.EnergyPool["Fire"]);
        Assert.Single(player.SupportField);
    }

    [Fact]
    public void UtilitySupportGainsOneSourceElementEnergy()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        player.Hand.Clear();
        player.Hand.Add(new CardInstance("fire-forge-caller"));
        player.EnergyPool["Fire"] = 1;
        player.EnergyPool["Wind"] = 1;
        var deckCountBefore = player.Deck.Count;

        var playResult = engine.PlayCardFromHand(0);

        Assert.True(playResult.Success);
        Assert.Single(player.SupportField);
        Assert.Single(player.Hand);
        Assert.Equal(deckCountBefore - 1, player.Deck.Count);
        Assert.Equal(1, player.EnergyPool["Fire"]);
        Assert.Equal(0, player.EnergyPool["Wind"]);
    }

    [Fact]
    public void ScriptedRampLineReachesFourEnergyOnTurnThree()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.Players[0];
        player.Hand.Clear();
        player.Hand.Add(new CardInstance("fire-hearth-shrine"));
        player.EnergyPool["Fire"] = 1;

        Assert.True(engine.PlayCardFromHand(0).Success);
        Assert.True(engine.ResolveEnergyChoice("Fire").Success);
        Assert.Equal(2, player.EnergyPool["Fire"]);

        Assert.True(engine.AdvancePhase().Success);
        AdvanceToMainForPlayer(engine, 0);
        Assert.True(engine.AddEnergy("Fire").Success);

        Assert.True(engine.AdvancePhase().Success);
        AdvanceToMainForPlayer(engine, 0);
        Assert.True(engine.AddEnergy("Fire").Success);

        Assert.True(player.EnergyPool.Values.Sum() >= 4);
    }

    [Fact]
    public void ActivatedAbilityCanGainChosenEnergy()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        var source = new CardInstance("fire-hearth-shrine");
        player.SupportField.Add(source);
        player.EnergyPool["Fire"] = 1;

        var result = engine.ActivateAbility(0, source.Id, "kindle");
        Assert.True(result.Success);
        Assert.NotNull(engine.State.PendingEnergyChoice);
        Assert.Equal(0, player.EnergyPool["Fire"]);

        var choice = engine.ResolveEnergyChoice("Ice");

        Assert.True(choice.Success);
        Assert.Null(engine.State.PendingEnergyChoice);
        Assert.Equal(1, player.EnergyPool["Ice"]);
        Assert.True(source.Exhausted);
    }

    [Fact]
    public void ActivatedAbilityCannotBeUsedWhileSourceIsExhausted()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        var source = new CardInstance("fire-hearth-shrine");
        player.SupportField.Add(source);
        player.EnergyPool["Fire"] = 2;

        Assert.True(engine.ActivateAbility(0, source.Id, "kindle").Success);

        Assert.True(source.Exhausted);
        Assert.False(engine.CanActivateAbility(0, source.Id, "kindle"));
        Assert.False(engine.ActivateAbility(0, source.Id, "kindle").Success);
    }

    [Fact]
    public void ActivatedAbilitySourceReadiesOnNextReadyPhase()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        var source = new CardInstance("fire-hearth-shrine") { Exhausted = true };
        player.SupportField.Add(source);

        Assert.True(engine.AdvancePhase().Success);
        AdvanceToPhaseForPlayer(engine, playerIndex: 0, phase: "Ready");

        Assert.False(source.Exhausted);
    }

    [Fact]
    public void ActivatedAbilityAvailabilityRequiresMainPhase()
    {
        var engine = CreateEngine();
        var player = engine.State.ActivePlayer;
        var source = new CardInstance("fire-hearth-shrine");
        player.SupportField.Add(source);
        player.EnergyPool["Fire"] = 1;

        Assert.False(engine.CanActivateAbility(0, source.Id, "kindle"));

        Assert.True(engine.AdvanceToNextDecisionPhase().Success);

        Assert.True(engine.CanActivateAbility(0, source.Id, "kindle"));
    }

    [Fact]
    public void EnergyCanBeConverted()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        var source = new CardInstance("ice-winter-shrine");
        player.SupportField.Add(source);
        player.EnergyPool["Ice"] = 1;
        player.EnergyPool["Fire"] = 1;

        Assert.True(engine.ActivateAbility(0, source.Id, "chill-channel").Success);
        var result = engine.ResolveEnergyChoice("Water");

        Assert.True(result.Success);
        Assert.Equal(0, player.EnergyPool["Ice"]);
        Assert.Equal(0, player.EnergyPool["Fire"]);
        Assert.Equal(1, player.EnergyPool["Water"]);
    }

    [Fact]
    public void TargetChoiceHookQueuesAndResolvesEnemyUnit()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        var defender = engine.State.DefendingPlayer;
        player.Hand.Clear();
        player.Hand.Add(new CardInstance("fire-battle-seer"));
        player.EnergyPool["Fire"] = 2;
        player.EnergyPool["Wind"] = 1;
        defender.UnitField.Add(new CardInstance("ice-crystal-champion"));

        var play = engine.PlayCardFromHand(0);

        Assert.True(play.Success);
        Assert.NotNull(engine.State.PendingTargetChoice);
        Assert.True(engine.CanResolveTargetChoice(1, 0));

        var target = engine.ResolveTargetChoice(1, 0);

        Assert.True(target.Success);
        Assert.Null(engine.State.PendingTargetChoice);
        Assert.True(defender.UnitField[0].Exhausted);
        Assert.Contains(target.Events, item => item.Kind == MatchEventKind.TargetResolved);
    }

    [Fact]
    public void TargetChoiceRejectsIllegalScope()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        engine.State.ActivePlayer.UnitField.Add(new CardInstance("fire-ember-whelp"));
        engine.State.DefendingPlayer.UnitField.Add(new CardInstance("ice-glacial-wisp"));
        engine.QueueTargetChoice(0, PendingTargetChoiceType.ExhaustUnit, TargetScope.EnemyUnit, engine.State.ActivePlayer.UnitField[0], "Choose an enemy.");

        Assert.False(engine.CanResolveTargetChoice(0, 0));
        Assert.False(engine.ResolveTargetChoice(0, 0).Success);
        Assert.True(engine.ResolveTargetChoice(1, 0).Success);
    }

    [Fact]
    public void LastPaymentCanBeRefunded()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        var source = new CardInstance("earth-grove-shrine");
        player.SupportField.Add(source);
        player.EnergyPool["Earth"] = 1;

        var result = engine.ActivateAbility(0, source.Id, "root-bank");

        Assert.True(result.Success);
        Assert.Equal(1, player.EnergyPool["Earth"]);
    }

    [Fact]
    public void CanPlayCardFromHandSupportsDragDropValidation()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        player.Hand.Clear();
        player.Hand.Add(new CardInstance("fire-ember-whelp"));

        Assert.False(engine.CanPlayCardFromHand(0));

        player.EnergyPool["Fire"] = 1;

        Assert.True(engine.CanPlayCardFromHand(0));
    }

    [Fact]
    public void UnblockedAttackAddsDamage()
    {
        var engine = CreateEngine();
        engine.State.ActivePlayer.UnitField.Add(new CardInstance("fire-ember-whelp"));
        AdvanceToCombat(engine);

        Assert.True(engine.DeclareAttack(0).Success);
        Assert.True(engine.PassBlock().Success);

        Assert.Single(engine.State.DefendingPlayer.DamageZone);
        Assert.Null(engine.State.PendingAttack);
    }

    [Fact]
    public void BlockedClashMovesDefeatedUnitsToDiscard()
    {
        var engine = CreateEngine();
        var attacker = engine.State.ActivePlayer;
        var defender = engine.State.DefendingPlayer;
        attacker.UnitField.Add(new CardInstance("fire-ember-whelp"));
        defender.UnitField.Add(new CardInstance("wind-gale-scout"));
        AdvanceToCombat(engine);

        Assert.True(engine.DeclareAttack(0).Success);
        Assert.True(engine.Block(0).Success);

        Assert.Empty(attacker.UnitField);
        Assert.Single(attacker.DiscardPile);
        Assert.Single(defender.UnitField);
        Assert.Empty(defender.DiscardPile);
    }

    [Theory]
    [InlineData("water-tide-minnow", "fire-cinder-adept")]
    [InlineData("fire-ember-whelp", "ice-snowguard-adept")]
    public void ElementAdvantageAddsTwoThousandPowerInBlockedCombat(string attackerCardId, string blockerCardId)
    {
        var engine = CreateEngine();
        var attacker = engine.State.ActivePlayer;
        var defender = engine.State.DefendingPlayer;
        attacker.UnitField.Add(new CardInstance(attackerCardId));
        defender.UnitField.Add(new CardInstance(blockerCardId));
        var attackerDefinition = engine.State.DefinitionFor(attacker.UnitField[0]);
        var blockerDefinition = engine.State.DefinitionFor(defender.UnitField[0]);
        AdvanceToCombat(engine);

        Assert.Equal(attackerDefinition.Power + 2000, engine.GetEffectiveCombatPower(attackerDefinition, blockerDefinition));
        Assert.True(engine.DeclareAttack(0).Success);
        var result = engine.Block(0);

        Assert.True(result.Success);
        Assert.Empty(attacker.UnitField);
        Assert.Empty(defender.UnitField);
        Assert.Single(attacker.DiscardPile);
        Assert.Single(defender.DiscardPile);
        Assert.Contains(result.Events, item => item.Kind == MatchEventKind.CombatResolved && item.Message.Contains("Element advantage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MultiElementCardsUseTheirPrimaryCombatElement()
    {
        var data = LoadData();
        var mode = data.GameModesById["dragon-duel"];
        var phoenix = data.CardsById["fire-phoenix"];
        var darkUnit = data.CardsById["dark-voidbinder"];
        var lightDragon = data.CardsById["light-seraphic-dragon"];

        Assert.Equal("Fire", DragonDuelEngine.GetCombatElement(mode, phoenix));
        Assert.Equal(0, DragonDuelEngine.GetElementAdvantageBonus(mode, phoenix, darkUnit));
        Assert.Equal(2000, DragonDuelEngine.GetElementAdvantageBonus(mode, lightDragon, darkUnit));
    }

    [Fact]
    public void SevenDamageWinsTheGame()
    {
        var engine = CreateEngine();

        engine.DealDamageToOpponent(0, 7);

        Assert.Equal(0, engine.State.WinnerIndex);
        Assert.Equal(7, engine.State.Players[1].DamageZone.Count);
    }

    [Fact]
    public void InvalidDeckReportsCopyAndSizeIssues()
    {
        var data = LoadData();
        var deck = new DeckDefinition
        {
            Id = "bad-deck",
            Name = "Bad Deck",
            ModeId = "dragon-duel",
            Cards = new Dictionary<string, int>
            {
                ["fire-ember-whelp"] = 4
            }
        };

        var issues = GameDataValidator.ValidateDeck(deck, data);

        Assert.Contains(issues, issue => issue.Code == "deck.size");
        Assert.Contains(issues, issue => issue.Code == "deck.max_copies");
    }

    [Fact]
    public void DirectInviteCodesRoundTripAndValidate()
    {
        var data = LoadData();
        var invite = new NetworkInvite
        {
            Host = "127.0.0.1",
            Port = 47288,
            ModeId = "dragon-duel",
            ProtocolVersion = InviteCode.ProtocolVersion,
            DeckHash = InviteCode.DeckHash(data.DecksById["starter-flame-gale"].Cards)
        };

        var code = InviteCode.Encode(invite);
        var decoded = InviteCode.Decode(code);

        Assert.StartsWith(InviteCode.Prefix, code);
        Assert.Equal(invite.Host, decoded.Host);
        Assert.Equal(invite.Port, decoded.Port);
        Assert.Equal(invite.ModeId, decoded.ModeId);
        Assert.Equal(invite.DeckHash, decoded.DeckHash);
        Assert.False(InviteCode.TryDecode("bad-code", out _, out var error));
        Assert.Contains(InviteCode.Prefix, error);
    }

    [Fact]
    public void AiAddsEnergyDuringMainPhase()
    {
        var engine = CreateEngine();
        SetActivePhase(engine, playerIndex: 1, phase: "Main");
        var ai = new DragonDuelAi();

        var result = ai.RunUntilHumanInput(engine, aiPlayerIndex: 1, maxActions: 1);

        Assert.Equal(AiTurnStatus.ActionLimitReached, result.Status);
        Assert.Contains(result.Decisions, decision => decision.Kind == "add-energy");
        Assert.True(engine.State.Players[1].EnergyPool.Values.Sum() > 0);
    }

    [Fact]
    public void AiPlaysAffordableCardWhenAvailable()
    {
        var engine = CreateEngine();
        SetActivePhase(engine, playerIndex: 1, phase: "Main");
        var aiPlayer = engine.State.Players[1];
        aiPlayer.Hand.Clear();
        aiPlayer.Hand.Add(new CardInstance("ice-glacial-wisp"));
        aiPlayer.EnergyPool["Ice"] = 1;
        engine.State.EnergyAddsThisTurn = 1;
        var ai = new DragonDuelAi();

        var result = ai.RunUntilHumanInput(engine, aiPlayerIndex: 1);

        Assert.Contains(result.Decisions, decision => decision.Kind == "play-card");
        Assert.Single(aiPlayer.UnitField);
    }

    [Fact]
    public void AiResolvesPendingEnergyChoice()
    {
        var engine = CreateEngine();
        SetActivePhase(engine, playerIndex: 1, phase: "Main");
        engine.State.PendingEnergyChoice = new PendingEnergyChoice(1, PendingEnergyChoiceType.Gain, 2, "AI chooses energy.");
        var ai = new DragonDuelAi();

        var result = ai.RunUntilHumanInput(engine, aiPlayerIndex: 1, maxActions: 1);

        Assert.Equal(AiTurnStatus.ActionLimitReached, result.Status);
        Assert.Null(engine.State.PendingEnergyChoice);
        Assert.Equal(2, engine.State.Players[1].EnergyPool.Values.Sum());
    }

    [Fact]
    public void AiResolvesPendingTargetChoice()
    {
        var engine = CreateEngine();
        SetActivePhase(engine, playerIndex: 1, phase: "Main");
        engine.State.Players[1].UnitField.Add(new CardInstance("ice-glacial-wisp"));
        engine.State.Players[0].UnitField.Add(new CardInstance("fire-ashen-champion"));
        engine.QueueTargetChoice(1, PendingTargetChoiceType.ExhaustUnit, TargetScope.EnemyUnit, engine.State.Players[1].UnitField[0], "AI chooses target.");
        var ai = new DragonDuelAi();

        var result = ai.RunUntilHumanInput(engine, aiPlayerIndex: 1, maxActions: 1);

        Assert.Equal(AiTurnStatus.ActionLimitReached, result.Status);
        Assert.Null(engine.State.PendingTargetChoice);
        Assert.True(engine.State.Players[0].UnitField[0].Exhausted);
        Assert.Contains(result.Decisions, decision => decision.Kind == "target-choice");
    }

    [Fact]
    public void AiAttacksDuringCombatWithReadyUnit()
    {
        var engine = CreateEngine();
        SetActivePhase(engine, playerIndex: 1, phase: "Combat");
        engine.State.ActivePlayer.UnitField.Add(new CardInstance("ice-glacial-wisp"));
        var ai = new DragonDuelAi();

        var result = ai.RunUntilHumanInput(engine, aiPlayerIndex: 1);

        Assert.Equal(AiTurnStatus.WaitingForHumanBlock, result.Status);
        Assert.NotNull(engine.State.PendingAttack);
        Assert.Equal(1, engine.State.PendingAttack!.AttackerPlayerIndex);
    }

    [Fact]
    public void AiBlocksTradingAttack()
    {
        var engine = CreateEngine();
        SetActivePhase(engine, playerIndex: 0, phase: "Combat");
        engine.State.Players[0].UnitField.Add(new CardInstance("fire-cinder-adept"));
        engine.State.Players[1].UnitField.Add(new CardInstance("wind-gale-scout"));
        Assert.True(engine.DeclareAttack(0).Success);
        var ai = new DragonDuelAi();

        var result = ai.RunUntilHumanInput(engine, aiPlayerIndex: 1);

        Assert.Equal(AiTurnStatus.WaitingForHuman, result.Status);
        Assert.Null(engine.State.PendingAttack);
        Assert.Single(engine.State.Players[0].DiscardPile);
        Assert.Single(engine.State.Players[1].DiscardPile);
    }

    [Fact]
    public void AiTurnExecutorReturnsControlWhenTurnEnds()
    {
        var engine = CreateEngine();
        SetActivePhase(engine, playerIndex: 1, phase: "Main");
        var aiPlayer = engine.State.Players[1];
        aiPlayer.Hand.Clear();
        aiPlayer.UnitField.Clear();
        aiPlayer.SupportField.Clear();
        engine.State.EnergyAddsThisTurn = 1;
        var ai = new DragonDuelAi();

        var result = ai.RunUntilHumanInput(engine, aiPlayerIndex: 1);

        Assert.Equal(AiTurnStatus.WaitingForHuman, result.Status);
        Assert.Equal(0, engine.State.ActivePlayerIndex);
        Assert.Equal("Main", engine.State.CurrentPhase);
    }

    [Fact]
    public void AiSacrificesHandCardWhenItUnlocksAPlayableCard()
    {
        var engine = CreateEngine();
        SetActivePhase(engine, playerIndex: 1, phase: "Main");
        var aiPlayer = engine.State.Players[1];
        aiPlayer.Hand.Clear();
        aiPlayer.Hand.Add(new CardInstance("ice-glacial-wisp"));
        aiPlayer.Hand.Add(new CardInstance("ice-lance"));
        engine.State.EnergyAddsThisTurn = 1;
        var ai = new DragonDuelAi();

        var result = ai.RunUntilHumanInput(engine, aiPlayerIndex: 1);

        Assert.Contains(result.Decisions, decision => decision.Kind == "sacrifice");
        Assert.Contains(result.Decisions, decision => decision.Kind == "play-card");
        Assert.Single(aiPlayer.UnitField);
        Assert.Contains(aiPlayer.DiscardPile, card => card.CardId == "ice-lance");
    }

    [Fact]
    public void AiSacrificesWeakFieldCardToReplaceIntoFullUnitZone()
    {
        var engine = CreateEngine();
        SetActivePhase(engine, playerIndex: 1, phase: "Main");
        var aiPlayer = engine.State.Players[1];
        aiPlayer.Hand.Clear();
        aiPlayer.Hand.Add(new CardInstance("ice-crystal-champion"));
        aiPlayer.EnergyPool["Ice"] = 5;
        engine.State.EnergyAddsThisTurn = 1;
        for (var i = 0; i < engine.State.Mode.ZoneLimits.UnitSlots; i++)
        {
            aiPlayer.UnitField.Add(new CardInstance("ice-glacial-wisp"));
        }

        var ai = new DragonDuelAi();
        var result = ai.RunUntilHumanInput(engine, aiPlayerIndex: 1);

        Assert.Contains(result.Decisions, decision => decision.Kind == "sacrifice");
        Assert.Contains(result.Decisions, decision => decision.Kind == "play-card");
        Assert.Equal(engine.State.Mode.ZoneLimits.UnitSlots, aiPlayer.UnitField.Count);
        Assert.Contains(aiPlayer.UnitField, card => card.CardId == "ice-crystal-champion");
        Assert.Contains(aiPlayer.DiscardPile, card => card.CardId == "ice-glacial-wisp");
    }

    [Fact]
    public void AiSacrificesAtMostOneHandCardInSingleMainPhase()
    {
        var engine = CreateEngine();
        SetActivePhase(engine, playerIndex: 1, phase: "Main");
        var aiPlayer = engine.State.Players[1];
        aiPlayer.Hand.Clear();
        aiPlayer.Hand.Add(new CardInstance("ice-deep-freeze"));
        aiPlayer.Hand.Add(new CardInstance("ice-lance"));
        aiPlayer.Hand.Add(new CardInstance("ice-cold-reading"));
        engine.State.EnergyAddsThisTurn = 1;
        var ai = new DragonDuelAi();

        var result = ai.RunUntilHumanInput(engine, aiPlayerIndex: 1);

        Assert.True(result.Decisions.Count(decision => decision.Kind == "sacrifice") <= 1);
    }

    private static GameData LoadData() => GameData.LoadDefault();

    private static DragonDuelEngine CreateEngine()
    {
        var data = LoadData();
        return DragonDuelEngine.Create(
            data,
            "dragon-duel",
            data.DecksById["starter-flame-gale"],
            data.DecksById["starter-frost-stone"],
            seed: 7,
            shuffle: false);
    }

    private static DragonDuelEngine CreateZeroCostEngine()
    {
        var mode = new GameModeDefinition
        {
            Id = "test-duel",
            Name = "Test Duel",
            Elements = ["Fire"],
            Phases = DragonCardConstants.BuiltInPhases.ToList(),
            AllowedCardTypes = DragonCardConstants.BuiltInCardTypes.ToList(),
            DeckRules = new DeckRulesDefinition { DeckSize = 1, MaxCopies = 3 },
            ZoneLimits = new ZoneLimitDefinition { UnitSlots = 5, SupportSlots = 5 },
            EnergyRules = new EnergyRulesDefinition { MaxPerElement = 10, AddsPerTurn = 1 },
            StartingHand = 0
        };
        var card = new CardDefinition
        {
            Id = "zero-unit",
            Name = "Zero Unit",
            Type = "Unit",
            Elements = ["Fire"],
            Cost = [],
            Power = 1000
        };
        var deck = new DeckDefinition
        {
            Id = "zero-deck",
            Name = "Zero Deck",
            ModeId = "test-duel",
            Cards = new Dictionary<string, int> { ["zero-unit"] = 1 }
        };
        var data = new GameData([mode], [card], [deck]);
        return DragonDuelEngine.Create(data, "test-duel", deck, deck, seed: 1, shuffle: false);
    }

    private static void AdvanceToMain(DragonDuelEngine engine)
    {
        Assert.True(engine.AdvancePhase().Success);
        Assert.True(engine.AdvancePhase().Success);
        Assert.Equal("Main", engine.State.CurrentPhase);
    }

    private static void AdvanceToCombat(DragonDuelEngine engine)
    {
        AdvanceToMain(engine);
        Assert.True(engine.AdvancePhase().Success);
        Assert.Equal("Combat", engine.State.CurrentPhase);
    }

    private static void AdvanceToMainForPlayer(DragonDuelEngine engine, int playerIndex) =>
        AdvanceToPhaseForPlayer(engine, playerIndex, "Main");

    private static void AdvanceToPhaseForPlayer(DragonDuelEngine engine, int playerIndex, string phase)
    {
        for (var guard = 0; guard < 30; guard++)
        {
            if (engine.State.ActivePlayerIndex == playerIndex &&
                engine.State.CurrentPhase.Equals(phase, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var result = phase.Equals("Main", StringComparison.OrdinalIgnoreCase) &&
                engine.State.CurrentPhase is "Ready" or "Draw" or "End"
                ? engine.AdvanceToNextDecisionPhase()
                : engine.AdvancePhase();
            Assert.True(result.Success, result.Message);
        }

        Assert.Fail($"Could not advance to player {playerIndex}'s {phase} phase.");
    }

    private static void SetActivePhase(DragonDuelEngine engine, int playerIndex, string phase)
    {
        engine.State.ActivePlayerIndex = playerIndex;
        engine.State.PhaseIndex = engine.State.Mode.Phases.FindIndex(item => item.Equals(phase, StringComparison.OrdinalIgnoreCase));
        engine.State.EnergyAddsThisTurn = 0;
        engine.State.PendingAttack = null;
        engine.State.PendingEnergyChoice = null;
    }
}
