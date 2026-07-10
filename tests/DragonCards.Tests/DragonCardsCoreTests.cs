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
        Assert.True(data.GameModesById.ContainsKey("dragon-avatar"));
        Assert.True(data.GameModesById.ContainsKey("sealed-gauntlet"));
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
    public void ReleaseModesLoadWithExpectedProgressionEligibility()
    {
        var data = LoadData();

        Assert.Contains(PlayableModeCatalog.All, mode => mode.Id == DragonCardsModeIds.DragonDuel);
        Assert.Contains(PlayableModeCatalog.All, mode => mode.Id == DragonCardsModeIds.DragonAvatar);
        Assert.True(data.GameModesById[DragonCardsModeIds.DragonDuel].ProgressionEligible);
        Assert.True(data.GameModesById[DragonCardsModeIds.StarterClash].ProgressionEligible);
        Assert.False(data.GameModesById[DragonCardsModeIds.DragonAvatar].ProgressionEligible);
        Assert.False(data.GameModesById[DragonCardsModeIds.SealedGauntlet].ProgressionEligible);
        Assert.False(data.GameModesById[DragonCardsModeIds.SandboxLab].ProgressionEligible);
        Assert.True(data.GameModesById[DragonCardsModeIds.TutorialTrials].ProgressionEligible);
        Assert.True(data.GameModesById[DragonCardsModeIds.DragonAvatar].DeckRules.Singleton);
        Assert.Equal(60, data.GameModesById[DragonCardsModeIds.DragonAvatar].DeckRules.DeckSize);
        Assert.Equal(10, data.GameModesById[DragonCardsModeIds.DragonAvatar].DamageLimit);
    }

    [Fact]
    public void DragonAvatarDeckValidationEnforcesSingletonIdentityAndAvatarRules()
    {
        var data = LoadData();
        var avatar = DragonAvatarService.PlayableAvatarCandidates(data).First();
        var valid = DragonAvatarService.BuildSampleAvatarDeck(data, avatar.Id);

        Assert.Empty(DragonAvatarService.ValidateAvatarDeck(data, avatar.Id, valid));

        var invalidCards = valid.Cards.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        invalidCards[invalidCards.Keys.First()] = 2;
        invalidCards[data.Cards.First(card => !card.Elements.Contains(avatar.Elements[0], StringComparer.OrdinalIgnoreCase)).Id] = 1;
        var invalid = valid with { Cards = invalidCards };

        var issues = DragonAvatarService.ValidateAvatarDeck(data, avatar.Id, invalid);

        Assert.Contains(issues, issue => issue.Code == "avatar.singleton");
        Assert.Contains(issues, issue => issue.Code == "avatar.identity");
    }

    [Fact]
    public void DragonAvatarReplayCostScalesByTwoGeneric()
    {
        Assert.Equal(0, DragonAvatarService.ReplayCostIncrease(0));
        Assert.Equal(2, DragonAvatarService.ReplayCostIncrease(1));
        Assert.Equal(6, DragonAvatarService.ReplayCostIncrease(3));
    }

    [Fact]
    public void SealedPoolGenerationIsDeterministicAndBuildsFortyCardDeck()
    {
        var data = LoadData();

        var first = SealedGauntletService.GeneratePool(data, seed: 1234);
        var second = SealedGauntletService.GeneratePool(data, seed: 1234);

        Assert.Equal(first.CardIds, second.CardIds);
        Assert.Equal(SealedGauntletService.BoosterCount * BoosterService.CardsPerPack, first.CardIds.Count);
        Assert.Equal(SealedGauntletService.DeckSize, first.Deck.Count);
        Assert.Equal(DragonCardsModeIds.SealedGauntlet, first.Deck.ModeId);
        Assert.Empty(GameDataValidator.ValidateDeck(first.Deck, data));
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
    public void CardLibraryIncludesElementalAscensionExpansionWithVisualMetadata()
    {
        var data = LoadData();

        Assert.Equal(480, data.Cards.Count);
        Assert.Equal(data.Cards.Count, data.Cards.Select(card => card.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(data.Cards, card => Assert.Contains(card.Rarity, CardRarities.All));
        Assert.Equal(232, data.Cards.Count(card => card.Rarity == CardRarities.Common));
        Assert.Equal(96, data.Cards.Count(card => card.Rarity == CardRarities.Uncommon));
        Assert.Equal(88, data.Cards.Count(card => card.Rarity == CardRarities.Rare));
        Assert.Equal(40, data.Cards.Count(card => card.Rarity == CardRarities.Legendary));
        Assert.Equal(24, data.Cards.Count(card => card.Rarity == CardRarities.Mythic));
        Assert.All(data.Cards.Where(card => card.Id.EndsWith("-ancient-dragon", StringComparison.OrdinalIgnoreCase)), card =>
        {
            Assert.Equal(CardRarities.Mythic, card.Rarity);
            Assert.Equal(CardSets.AncientAwakening, card.SetId);
        });
        Assert.Equal(40, data.Cards.Count(card => card.SetId == CardSets.AncientAwakening));
        Assert.Equal(160, data.Cards.Count(card => card.SetId == CardSets.ElementalAscension));
        Assert.Equal(160, data.Cards.Count(card => card.SetId == CardSets.PrimalClash));
        foreach (var element in data.GameModesById["dragon-duel"].Elements)
        {
            Assert.Equal(60, data.Cards.Count(card => card.Elements.FirstOrDefault()?.Equals(element, StringComparison.OrdinalIgnoreCase) == true));
            var expansionCards = data.Cards
                .Where(card => card.SetId == CardSets.ElementalAscension &&
                    card.Elements.FirstOrDefault()?.Equals(element, StringComparison.OrdinalIgnoreCase) == true)
                .ToArray();
            Assert.Equal(20, expansionCards.Length);
            Assert.Equal(8, expansionCards.Count(card => card.Rarity == CardRarities.Common));
            Assert.Equal(5, expansionCards.Count(card => card.Rarity == CardRarities.Uncommon));
            Assert.Equal(4, expansionCards.Count(card => card.Rarity == CardRarities.Rare));
            Assert.Equal(2, expansionCards.Count(card => card.Rarity == CardRarities.Legendary));
            Assert.Equal(1, expansionCards.Count(card => card.Rarity == CardRarities.Mythic));
            Assert.Equal(10, expansionCards.Count(card => card.Type == "Unit"));
            Assert.Equal(4, expansionCards.Count(card => card.Type == "Support"));
            Assert.Equal(6, expansionCards.Count(card => card.Type == "Spell"));

            var primalCards = data.Cards
                .Where(card => card.SetId == CardSets.PrimalClash &&
                    card.Elements.FirstOrDefault()?.Equals(element, StringComparison.OrdinalIgnoreCase) == true)
                .ToArray();
            Assert.Equal(20, primalCards.Length);
            Assert.Equal(8, primalCards.Count(card => card.Rarity == CardRarities.Common));
            Assert.Equal(5, primalCards.Count(card => card.Rarity == CardRarities.Uncommon));
            Assert.Equal(4, primalCards.Count(card => card.Rarity == CardRarities.Rare));
            Assert.Equal(2, primalCards.Count(card => card.Rarity == CardRarities.Legendary));
            Assert.Equal(1, primalCards.Count(card => card.Rarity == CardRarities.Mythic));
            Assert.Equal(10, primalCards.Count(card => card.Type == "Unit"));
            Assert.Equal(4, primalCards.Count(card => card.Type == "Support"));
            Assert.Equal(6, primalCards.Count(card => card.Type == "Spell"));
            Assert.Contains(primalCards, card => card.Abilities.Any(ability => ability.Timings.Contains(DragonCardConstants.CombatTiming, StringComparer.OrdinalIgnoreCase)));
        }

        Assert.All(data.Cards.Where(card => card.Visual is not null), card =>
        {
            Assert.False(string.IsNullOrWhiteSpace(card.Visual!.Frame));
            Assert.False(string.IsNullOrWhiteSpace(card.Visual.Effect));
        });
        Assert.Contains(data.Cards, card => card.Tags.Contains("Finisher", StringComparer.OrdinalIgnoreCase));
        Assert.Contains(data.Cards, card => card.Hooks.Contains("exhaust_enemy_unit_choice"));
        Assert.Contains(data.Cards, card => card.Hooks.Contains("return_enemy_field_to_hand_choice"));
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
        var elements = data.GameModesById["dragon-duel"].Elements;

        Assert.All(data.Decks, deck => Assert.All(deck.Cards, entry => Assert.InRange(entry.Value, 1, 3)));
        Assert.Equal(elements.Count, data.Decks.Count);
        foreach (var element in elements)
        {
            var deck = data.DecksById[$"starter-{element.ToLowerInvariant()}"];
            Assert.All(deck.Cards.Keys, cardId => Assert.Equal(element, data.CardsById[cardId].Elements[0]));
            Assert.Contains(deck.Cards.Keys, cardId => data.CardsById[cardId].Hooks.Any(hook => hook.Contains("energy", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public void RulesPresetsControlProgressionAndSandboxDeckBuilding()
    {
        var casual = GameRulesConfig.ForPreset(GameRulesPreset.Casual);
        var standard = GameRulesConfig.ForPreset(GameRulesPreset.Standard);
        var customSandbox = GameRulesConfig.ForPreset(GameRulesPreset.Custom) with { UnlimitedDeckBuilder = true };

        Assert.False(casual.Normalize().IsProgressionSafe);
        Assert.True(casual.UnlimitedDeckBuilder);
        Assert.True(standard.Normalize().IsProgressionSafe);
        Assert.False(customSandbox.Normalize().IsProgressionSafe);
    }

    [Fact]
    public void ProfileCreationGrantsChosenStarterAndSerializes()
    {
        var data = LoadData();
        var starter = data.DecksById["starter-fire"];

        var profile = ProgressionService.CreateProfile("Astra", GameRulesConfig.ForPreset(GameRulesPreset.Standard, Playstyle.Ramp), starter, data);
        var json = PlayerProfileSerializer.ToJson(profile);
        var roundTripped = PlayerProfileSerializer.FromJson(json);

        Assert.Equal("Astra", roundTripped.PlayerName);
        Assert.Equal(1, roundTripped.Level);
        Assert.Contains("starter-fire", roundTripped.OwnedStarterDeckIds);
        Assert.All(starter.Cards, entry => Assert.Equal(entry.Value, roundTripped.OwnedCards[entry.Key]));
        Assert.Equal(Playstyle.Ramp, roundTripped.DefaultRules.Playstyle);
    }

    [Fact]
    public void TutorialCompletionGrantsCoinsOnce()
    {
        var profile = new PlayerProfile { PlayerName = "Astra", Coins = 10 };

        var first = TutorialRewardService.CompleteTutorial(profile, "sacrifice-energy");
        var second = TutorialRewardService.CompleteTutorial(profile, "sacrifice-energy");

        Assert.True(first.Awarded);
        Assert.Equal(250, first.CoinsAwarded);
        Assert.False(second.Awarded);
        Assert.Equal(0, second.CoinsAwarded);
        Assert.Equal(260, profile.Coins);
        Assert.Single(profile.CompletedTutorialIds);
        Assert.Contains("sacrifice-energy", profile.CompletedTutorialIds);
    }

    [Fact]
    public void ProfileSerializationPreservesCompletedTutorialIds()
    {
        var profile = new PlayerProfile { PlayerName = "Astra" };
        TutorialRewardService.CompleteTutorial(profile, "add-energy");
        TutorialRewardService.CompleteTutorial(profile, "blocking-attacks");

        var roundTripped = PlayerProfileSerializer.FromJson(PlayerProfileSerializer.ToJson(profile));

        Assert.Contains("add-energy", roundTripped.CompletedTutorialIds);
        Assert.Contains("blocking-attacks", roundTripped.CompletedTutorialIds);
        Assert.Equal(2, roundTripped.CompletedTutorialIds.Count);
        Assert.Equal(500, roundTripped.Coins);
    }

    [Fact]
    public void MatchRewardsUseLinearLevelCurveAndDifficultyMultipliers()
    {
        var profile = new PlayerProfile { PlayerName = "Astra", Experience = 3900 };
        profile.Normalize();
        var hard = GameRulesConfig.ForPreset(GameRulesPreset.Hard);

        var reward = ProgressionService.ApplyMatchReward(profile, hard, MatchRewardKind.Ai, won: true);

        Assert.True(reward.ProgressionApplied);
        Assert.Equal(720, reward.ExperienceGained);
        Assert.Equal(4, reward.StartingLevel);
        Assert.Equal(5, reward.EndingLevel);
        Assert.Equal(1, reward.BoostersGained);
        Assert.Equal(5, profile.Level);
        Assert.Equal(170, profile.Coins);
        Assert.Equal(1, BoosterService.GetUnopenedPackCount(profile, BoosterService.StandardBoosterId));
    }

    [Fact]
    public void SandboxRewardsDoNotApply()
    {
        var profile = new PlayerProfile { PlayerName = "Astra" };
        var reward = ProgressionService.ApplyMatchReward(profile, GameRulesConfig.ForPreset(GameRulesPreset.Casual), MatchRewardKind.HumanMultiplayer, won: true);

        Assert.False(reward.ProgressionApplied);
        Assert.Equal(0, profile.Experience);
        Assert.Equal(0, profile.Coins);
    }

    [Fact]
    public void DeckOwnershipValidatorHonorsSandboxOverrides()
    {
        var deck = LoadData().DecksById["starter-fire"];
        var profile = new PlayerProfile { OwnedCards = new Dictionary<string, int> { ["fire-ember-whelp"] = 1 } };
        profile.Normalize();

        var progressionIssues = DeckOwnershipValidator.ValidateDeckOwnership(deck, profile, GameRulesConfig.ForPreset(GameRulesPreset.Standard));
        var sandboxIssues = DeckOwnershipValidator.ValidateDeckOwnership(deck, profile, GameRulesConfig.ForPreset(GameRulesPreset.Casual));

        Assert.NotEmpty(progressionIssues);
        Assert.Empty(sandboxIssues);
    }

    [Fact]
    public void BoosterOpeningUsesFiveRaritySlotsAndConvertsDuplicates()
    {
        var data = LoadData();
        var profile = new PlayerProfile { UnopenedBoosters = 1 };
        foreach (var card in data.Cards)
        {
            profile.OwnedCards[card.Id] = PlayerCollection.MaxOwnedCopies;
        }

        var opening = BoosterService.OpenBooster(data, profile, seed: 11);

        Assert.Equal(5, opening.Cards.Count);
        Assert.Equal(0, profile.UnopenedBoosters);
        Assert.Equal(0, BoosterService.GetUnopenedPackCount(profile, BoosterService.StandardBoosterId));
        Assert.True(opening.CoinsFromDuplicates > 0);
        Assert.Contains(opening.Cards, grant => grant.Rarity == CardRarities.Uncommon);
        Assert.Contains(opening.Cards, grant => CardRarities.IsRarePlus(grant.Rarity));
    }

    [Fact]
    public void RarityNormalizationSupportsLegacyAliasesAndMythical()
    {
        Assert.Equal(CardRarities.Legendary, CardRarities.Normalize("Legend"));
        Assert.Equal(CardRarities.Legendary, CardRarities.Normalize("legendary"));
        Assert.Equal(CardRarities.Mythic, CardRarities.Normalize("Mythical"));
        Assert.True(CardRarities.IsRarePlus("Mythical"));
    }

    [Fact]
    public void BoosterRarePlusRollUsesRareLegendaryMythicOdds()
    {
        Assert.Equal(CardRarities.Rare, BoosterService.RollRarePlusRarity(new Random(0)));
        Assert.Equal(CardRarities.Legendary, BoosterService.RollRarePlusRarity(new Random(14)));
        Assert.Equal(CardRarities.Mythic, BoosterService.RollRarePlusRarity(new Random(146)));
    }

    [Fact]
    public void BoosterInventoryTracksPackIdsAndQuantityPurchases()
    {
        var data = LoadData();
        var profile = new PlayerProfile { Coins = 5000, UnopenedBoosters = 2 };
        profile.Normalize();

        Assert.Equal(2, BoosterService.GetUnopenedPackCount(profile, BoosterService.StandardBoosterId));
        Assert.True(BoosterService.BuyBooster(profile, BoosterService.AncientAwakeningBoosterId, quantity: 3));
        var opening = BoosterService.OpenBoosters(data, profile, BoosterService.AncientAwakeningBoosterId, quantity: 2, seed: 9);

        Assert.Equal(10, opening.Cards.Count);
        Assert.Equal(1, BoosterService.GetUnopenedPackCount(profile, BoosterService.AncientAwakeningBoosterId));
        Assert.All(opening.Cards, grant => Assert.True(data.CardsById.ContainsKey(grant.CardId)));
    }

    [Fact]
    public void ElementalAscensionBoosterPullsFromExpansionSet()
    {
        var data = LoadData();
        var profile = new PlayerProfile();
        BoosterService.AddUnopenedPack(profile, BoosterService.ElementalAscensionBoosterId);

        var opening = BoosterService.OpenBooster(data, profile, BoosterService.ElementalAscensionBoosterId, seed: 17);

        Assert.Equal("Elemental Ascension Booster", opening.PackName);
        Assert.Equal(5, opening.Cards.Count);
        Assert.All(opening.Cards, grant => Assert.Equal(CardSets.ElementalAscension, data.CardsById[grant.CardId].SetId));
    }

    [Fact]
    public void PrimalClashBoosterPullsFromCombatExpansionSet()
    {
        var data = LoadData();
        var profile = new PlayerProfile();
        BoosterService.AddUnopenedPack(profile, BoosterService.PrimalClashBoosterId);

        var opening = BoosterService.OpenBooster(data, profile, BoosterService.PrimalClashBoosterId, seed: 23);

        Assert.Equal("Primal Clash Booster", opening.PackName);
        Assert.Equal(5, opening.Cards.Count);
        Assert.All(opening.Cards, grant => Assert.Equal(CardSets.PrimalClash, data.CardsById[grant.CardId].SetId));
    }

    [Fact]
    public void MythicDuplicateConvertsToCoins()
    {
        var data = LoadData();
        var profile = new PlayerProfile();
        var mythic = data.Cards.First(card => card.Rarity == CardRarities.Mythic);
        profile.OwnedCards[mythic.Id] = PlayerCollection.MaxOwnedCopies;

        var grant = PlayerCollection.GrantCard(profile, mythic);

        Assert.Equal(0, grant.CopiesAdded);
        Assert.Equal(750, grant.DuplicateCoins);
        Assert.Equal(750, profile.Coins);
    }

    [Fact]
    public void BattleSpoilsApplyOnlyForProgressionSafeWins()
    {
        var data = LoadData();
        var standardProfile = new PlayerProfile { PlayerName = "Astra" };
        var sandboxProfile = new PlayerProfile { PlayerName = "Astra" };

        var reward = BattleSpoilsService.GrantVictorySpoils(data, standardProfile, GameRulesConfig.ForPreset(GameRulesPreset.Standard), data.DecksById["starter-fire"], won: true, seed: 5);
        var sandbox = BattleSpoilsService.GrantVictorySpoils(data, sandboxProfile, GameRulesConfig.ForPreset(GameRulesPreset.Casual), data.DecksById["starter-fire"], won: true, seed: 5);
        var loss = BattleSpoilsService.GrantVictorySpoils(data, standardProfile, GameRulesConfig.ForPreset(GameRulesPreset.Standard), data.DecksById["starter-fire"], won: false, seed: 5);

        Assert.True(reward.ProgressionApplied);
        Assert.NotNull(reward.Grant);
        Assert.True(CardRarities.IsRarePlus(reward.Grant!.Rarity));
        Assert.False(sandbox.ProgressionApplied);
        Assert.False(loss.ProgressionApplied);
    }

    [Fact]
    public void BattleSpoilsCanTargetMythicAndConvertDuplicates()
    {
        var data = LoadData();
        var mythic = data.Cards.First(card => card.Rarity == CardRarities.Mythic);
        var deck = new DeckDefinition
        {
            Id = "mythic-only",
            Name = "Mythic Only",
            ModeId = "dragon-duel",
            Cards = new Dictionary<string, int> { [mythic.Id] = 3 }
        };
        var profile = new PlayerProfile { OwnedCards = new Dictionary<string, int> { [mythic.Id] = PlayerCollection.MaxOwnedCopies } };

        var reward = BattleSpoilsService.GrantVictorySpoils(data, profile, GameRulesConfig.ForPreset(GameRulesPreset.Standard), deck, won: true, seed: 2);

        Assert.True(reward.ProgressionApplied);
        Assert.Equal(mythic.Id, reward.Grant?.CardId);
        Assert.Equal(0, reward.Grant?.CopiesAdded);
        Assert.Equal(750, reward.Grant?.DuplicateCoins);
    }

    [Fact]
    public void BattleSpoilsCanLootElementalAscensionRarePlusCards()
    {
        var data = LoadData();
        var expansionMythic = data.CardsById["fire-solar-apex-dragon"];
        var deck = new DeckDefinition
        {
            Id = "ascension-spoils",
            Name = "Ascension Spoils",
            ModeId = "dragon-duel",
            Cards = new Dictionary<string, int> { [expansionMythic.Id] = 2 }
        };
        var profile = new PlayerProfile();

        var reward = BattleSpoilsService.GrantVictorySpoils(data, profile, GameRulesConfig.ForPreset(GameRulesPreset.Standard), deck, won: true, seed: 12);

        Assert.True(reward.ProgressionApplied);
        Assert.Equal(expansionMythic.Id, reward.Grant?.CardId);
        Assert.Equal(CardSets.ElementalAscension, data.CardsById[reward.Grant!.CardId].SetId);
    }

    [Fact]
    public void ShopCatalogIncludesBoostersStartersAndSingles()
    {
        var data = LoadData();
        var catalog = ShopCatalogService.CreateCatalog(data);

        Assert.Contains(catalog, item => item.Kind == ShopItemKind.Booster && item.PackId == BoosterService.StandardBoosterId);
        Assert.Contains(catalog, item => item.Kind == ShopItemKind.Booster && item.PackId == BoosterService.AncientAwakeningBoosterId);
        Assert.Contains(catalog, item => item.Kind == ShopItemKind.Booster && item.PackId == BoosterService.ElementalAscensionBoosterId && item.SetId == CardSets.ElementalAscension);
        Assert.Contains(catalog, item => item.Kind == ShopItemKind.Booster && item.PackId == BoosterService.PrimalClashBoosterId && item.SetId == CardSets.PrimalClash);
        Assert.Contains(catalog, item => item.Kind == ShopItemKind.StarterDeck && item.DeckId == "starter-fire");
        Assert.Contains(catalog, item => item.Kind == ShopItemKind.SingleCard && item.CardId == "fire-ancient-dragon" && item.Cost == 5000);
        Assert.Contains(catalog, item => item.Kind == ShopItemKind.SingleCard && item.CardId == "fire-solar-apex-dragon" && item.Cost == 5000);
    }

    [Fact]
    public void ShopSingleCardPurchaseEnforcesCoinsAndMaxCopies()
    {
        var data = LoadData();
        var card = data.CardsById["fire-ancient-dragon"];
        var profile = new PlayerProfile { Coins = 5000 };

        var purchased = ShopCatalogService.BuySingleCard(profile, card);
        var ownedAfterPurchase = PlayerCollection.CountOwned(profile, card.Id);
        profile.Coins = 5000;
        profile.OwnedCards[card.Id] = PlayerCollection.MaxOwnedCopies;
        var maxed = ShopCatalogService.BuySingleCard(profile, card);

        Assert.True(purchased.Success);
        Assert.Equal(1, ownedAfterPurchase);
        Assert.False(maxed.Success);
    }

    [Fact]
    public void CardDetailFormatterIncludesStandardFields()
    {
        var card = LoadData().CardsById["fire-ancient-dragon"];

        var text = CardDetailFormatter.Format(card, "Advantage: sample");

        Assert.Contains("Name:", text);
        Assert.Contains("Rarity: Mythic", text);
        Assert.Contains("Type:", text);
        Assert.Contains("Elements:", text);
        Assert.Contains("Cost:", text);
        Assert.Contains("Power:", text);
        Assert.Contains("Rules Text:", text);
        Assert.Contains("Keywords:", text);
        Assert.Contains("Tags:", text);
        Assert.Contains("Advantage: sample", text);
    }

    [Fact]
    public void SeededMatchesCreateDeterministicInstanceIds()
    {
        var first = CreateEngine();
        var second = CreateEngine();

        Assert.Equal(
            first.State.Players[0].Hand.Select(card => card.Id),
            second.State.Players[0].Hand.Select(card => card.Id));
        Assert.All(first.State.Players[0].Hand, card => Assert.StartsWith("p0-d", card.Id));
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
        var source = new CardInstance("fire-hearth-shrine");
        player.Hand.Add(source);
        player.EnergyPool["Fire"] = 1;

        var playResult = engine.PlayCardFromHand(0);

        Assert.True(playResult.Success);
        Assert.NotNull(engine.State.PendingEnergyChoice);
        Assert.Equal(source.Id, engine.State.PendingEnergyChoice.SourceInstanceId);
        Assert.Equal(source.CardId, engine.State.PendingEnergyChoice.CardId);
        Assert.Contains("choose an element", engine.State.PendingEnergyChoice.EffectText, StringComparison.OrdinalIgnoreCase);
        var queued = Assert.Single(playResult.Events, item => item.Kind == MatchEventKind.TargetChoiceQueued);
        Assert.Equal(source.Id, queued.InstanceId);
        Assert.Equal(source.CardId, queued.CardId);
        Assert.Equal(2, queued.Amount);
        Assert.Contains("choose an element", queued.EffectText, StringComparison.OrdinalIgnoreCase);
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
        var activated = Assert.Single(result.Events, item => item.Kind == MatchEventKind.AbilityActivated);
        Assert.Equal("Kindle", activated.AbilityName);
        Assert.Contains("Gain 1 energy", activated.EffectText);
        Assert.Equal(new ZoneRef(0, "SupportField", 0), activated.From);
        Assert.Equal(new ZoneRef(0, "SupportField", 0), activated.To);
        var queued = Assert.Single(result.Events, item => item.Kind == MatchEventKind.TargetChoiceQueued);
        Assert.Equal(source.Id, queued.InstanceId);
        Assert.Equal(source.CardId, queued.CardId);
        Assert.Contains("Gain 1 energy", queued.EffectText);
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
        var source = new CardInstance("fire-battle-seer");
        player.Hand.Add(source);
        player.EnergyPool["Fire"] = 2;
        player.EnergyPool["Wind"] = 1;
        defender.UnitField.Add(new CardInstance("ice-crystal-champion"));

        var play = engine.PlayCardFromHand(0);

        Assert.True(play.Success);
        Assert.NotNull(engine.State.PendingTargetChoice);
        Assert.Equal(source.Id, engine.State.PendingTargetChoice.SourceInstanceId);
        Assert.Equal(source.CardId, engine.State.PendingTargetChoice.CardId);
        Assert.Contains("choose an enemy Unit", engine.State.PendingTargetChoice.EffectText, StringComparison.OrdinalIgnoreCase);
        var queued = Assert.Single(play.Events, item => item.Kind == MatchEventKind.TargetChoiceQueued);
        Assert.Equal(source.Id, queued.InstanceId);
        Assert.Equal(source.CardId, queued.CardId);
        Assert.Contains("choose an enemy Unit", queued.EffectText, StringComparison.OrdinalIgnoreCase);
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
    public void ReturnToHandTargetChoiceSupportsUnitsAndSupports()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var source = new CardInstance("fire-ember-whelp");
        var enemySupport = new CardInstance("ice-winter-shrine") { Exhausted = true };
        engine.State.ActivePlayer.UnitField.Add(source);
        engine.State.DefendingPlayer.SupportField.Add(enemySupport);
        var startingHandCount = engine.State.DefendingPlayer.Hand.Count;
        engine.QueueTargetChoice(
            0,
            PendingTargetChoiceType.ReturnToHand,
            TargetScope.EnemyField,
            source,
            "Choose an enemy field card.",
            TargetZoneKind.Field);

        var target = new ZoneRef(1, "SupportField", 0);
        Assert.True(engine.CanResolveTargetChoice(target));

        var result = engine.ResolveTargetChoice(target);

        Assert.True(result.Success);
        Assert.Empty(engine.State.DefendingPlayer.SupportField);
        Assert.Equal(startingHandCount + 1, engine.State.DefendingPlayer.Hand.Count);
        Assert.Same(enemySupport, engine.State.DefendingPlayer.Hand[^1]);
        Assert.False(engine.State.DefendingPlayer.Hand[^1].Exhausted);
        Assert.Contains(result.Events, item => item.Kind == MatchEventKind.CardReturnedToHand);
    }

    [Fact]
    public void UnitOnlyTargetChoiceRejectsSupportField()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var source = new CardInstance("fire-ember-whelp");
        engine.State.ActivePlayer.UnitField.Add(source);
        engine.State.DefendingPlayer.SupportField.Add(new CardInstance("ice-winter-shrine"));
        engine.State.DefendingPlayer.UnitField.Add(new CardInstance("ice-glacial-wisp"));
        engine.QueueTargetChoice(
            0,
            PendingTargetChoiceType.ReturnToHand,
            TargetScope.EnemyUnit,
            source,
            "Choose an enemy unit.",
            TargetZoneKind.Units);

        Assert.False(engine.CanResolveTargetChoice(new ZoneRef(1, "SupportField", 0)));
        Assert.True(engine.CanResolveTargetChoice(new ZoneRef(1, "UnitField", 0)));
    }

    [Fact]
    public void DiscardFromHandMovesCardsDeterministicallyAndEmitsEvents()
    {
        var engine = CreateEngine();
        AdvanceToMain(engine);
        var player = engine.State.ActivePlayer;
        player.Hand.Clear();
        player.Hand.Add(new CardInstance("fire-ember-whelp"));
        player.Hand.Add(new CardInstance("fire-cinder-adept"));

        var events = engine.DiscardCardsFromHand(0, 1);

        Assert.Single(events);
        Assert.Equal(MatchEventKind.CardDiscarded, events[0].Kind);
        Assert.Single(player.Hand);
        Assert.Single(player.DiscardPile);
        Assert.Equal("fire-cinder-adept", player.DiscardPile[0].CardId);
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
        Assert.NotNull(engine.State.PendingCombatAction);

        PassCombatPriorityUntilResolved(engine);

        Assert.Single(engine.State.DefendingPlayer.DamageZone);
        Assert.Null(engine.State.PendingAttack);
    }

    [Fact]
    public void CombatActionWindowAllowsCombatTimedAbilitiesBeforeDamage()
    {
        var engine = CreateEngine();
        var attacker = engine.State.Players[0];
        var defender = engine.State.Players[1];
        attacker.UnitField.Add(new CardInstance("fire-ember-whelp"));
        var support = new CardInstance("fire-primal-watch-post");
        defender.SupportField.Add(support);
        defender.EnergyPool["Fire"] = 1;
        AdvanceToCombat(engine);

        Assert.True(engine.DeclareAttack(0).Success);
        Assert.True(engine.PassBlock().Success);
        Assert.NotNull(engine.State.PendingCombatAction);
        Assert.True(engine.CanActivateAbility(1, support.Id, "combat-watch-post"));

        var ability = engine.ActivateAbility(1, support.Id, "combat-watch-post");

        Assert.True(ability.Success);
        Assert.True(support.Exhausted);
        Assert.Single(attacker.DamageZone);
        Assert.Equal(0, engine.State.PendingCombatAction?.PriorityPlayerIndex);

        PassCombatPriorityUntilResolved(engine);

        Assert.Single(defender.DamageZone);
        Assert.Null(engine.State.PendingAttack);
    }

    [Fact]
    public void ActivatedUnitAbilityReportsItsFieldEndpoint()
    {
        var engine = CreateEngine();
        var attacker = engine.State.Players[0];
        attacker.UnitField.Add(new CardInstance("fire-ember-whelp"));
        var source = new CardInstance("fire-primal-line-keeper");
        attacker.UnitField.Add(source);
        attacker.EnergyPool["Fire"] = 1;
        AdvanceToCombat(engine);

        Assert.True(engine.DeclareAttack(0).Success);
        Assert.True(engine.PassBlock().Success);
        Assert.True(engine.PassCombatAction(1).Success);

        var result = engine.ActivateAbility(0, source.Id, "combat-line-keeper");

        Assert.True(result.Success, result.Message);
        var activated = Assert.Single(result.Events, item => item.Kind == MatchEventKind.AbilityActivated);
        Assert.Equal(new ZoneRef(0, "UnitField", 1), activated.From);
        Assert.Equal(new ZoneRef(0, "UnitField", 1), activated.To);
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
        Assert.NotNull(engine.State.PendingCombatAction);

        PassCombatPriorityUntilResolved(engine);

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
        Assert.True(engine.Block(0).Success);
        var result = PassCombatPriorityUntilResolved(engine);

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
        var rules = GameRulesConfig.ForPreset(GameRulesPreset.Standard);
        var invite = new NetworkInvite
        {
            Host = "127.0.0.1",
            Port = 47288,
            ModeId = "dragon-duel",
            ProtocolVersion = InviteCode.ProtocolVersion,
            DeckHash = InviteCode.DeckHash(data.DecksById["starter-fire"].Cards),
            RulesHash = InviteCode.RulesHash(rules)
        };

        var code = InviteCode.Encode(invite);
        var decoded = InviteCode.Decode(code);

        Assert.StartsWith(InviteCode.Prefix, code);
        Assert.Equal(invite.Host, decoded.Host);
        Assert.Equal(invite.Port, decoded.Port);
        Assert.Equal(invite.ModeId, decoded.ModeId);
        Assert.Equal(invite.DeckHash, decoded.DeckHash);
        Assert.Equal(invite.RulesHash, decoded.RulesHash);
        var compact = invite with { LobbyToken = 0x2A7B };
        var compactCode = InviteCode.EncodeCompact(compact);
        var compactDecoded = InviteCode.Decode(compactCode);
        Assert.StartsWith(InviteCode.CompactPrefix, compactCode);
        Assert.True(compactCode.Length <= 24);
        Assert.Equal(compact.Host, compactDecoded.Host);
        Assert.Equal(compact.Port, compactDecoded.Port);
        Assert.Equal(compact.ModeId, compactDecoded.ModeId);
        Assert.Equal(compact.LobbyToken, compactDecoded.LobbyToken);
        var lobbyCode = InviteCode.EncodeLobbyCode(compact.LobbyToken);
        Assert.Equal(InviteCode.LobbyCodeLength, lobbyCode.Length);
        Assert.DoesNotContain('0', lobbyCode);
        Assert.DoesNotContain('1', lobbyCode);
        Assert.DoesNotContain('I', lobbyCode);
        Assert.DoesNotContain('O', lobbyCode);
        Assert.True(InviteCode.TryDecodeLobbyCode(lobbyCode.ToLowerInvariant(), out var decodedLobbyToken, out _));
        Assert.Equal(compact.LobbyToken, decodedLobbyToken);
        Assert.False(InviteCode.TryDecodeLobbyCode($"{lobbyCode[..^1]}{(lobbyCode[^1] == '2' ? '3' : '2')}", out _, out _));
        var tamperedCompactCode = $"{compactCode[..^1]}{(compactCode[^1] == '2' ? '3' : '2')}";
        Assert.False(InviteCode.TryDecode(tamperedCompactCode, out _, out _));
        Assert.False(InviteCode.TryDecode("bad-code", out _, out var error));
        Assert.Contains(InviteCode.Prefix, error);
    }

    [Fact]
    public void NetworkHandshakePreservesReleaseModeIds()
    {
        var data = LoadData();
        var rules = GameRulesConfig.ForPreset(GameRulesPreset.Casual);
        var avatar = DragonAvatarService.PlayableAvatarCandidates(data).First();
        var deck = DragonAvatarService.BuildSampleAvatarDeck(data, avatar.Id);

        var handshake = DirectMatchConnection.CreateHandshake("Host", DragonCardsModeIds.DragonAvatar, deck, rules);
        var invite = new NetworkInvite
        {
            Host = "127.0.0.1",
            Port = 47288,
            ModeId = DragonCardsModeIds.DragonAvatar,
            ProtocolVersion = InviteCode.ProtocolVersion,
            DeckHash = InviteCode.DeckHash(deck.Cards),
            RulesHash = InviteCode.RulesHash(rules)
        };
        var decoded = InviteCode.Decode(InviteCode.Encode(invite));

        Assert.Equal(DragonCardsModeIds.DragonAvatar, handshake.ModeId);
        Assert.Equal(DragonCardsModeIds.DragonAvatar, decoded.ModeId);
        Assert.Equal(deck.Id, handshake.Deck.Id);
    }

    [Fact]
    public async Task DirectTcpConnectionExchangesHandshakeAndCommands()
    {
        var data = LoadData();
        var rules = GameRulesConfig.ForPreset(GameRulesPreset.Standard);
        var port = GetFreeTcpPort();
        var invite = new NetworkInvite
        {
            Host = "127.0.0.1",
            Port = port,
            ModeId = "dragon-duel",
            ProtocolVersion = InviteCode.ProtocolVersion,
            DeckHash = InviteCode.DeckHash(data.DecksById["starter-fire"].Cards),
            RulesHash = InviteCode.RulesHash(rules)
        };
        var hostHandshake = DirectMatchConnection.CreateHandshake("Host", "dragon-duel", data.DecksById["starter-fire"], rules);
        var joinHandshake = DirectMatchConnection.CreateHandshake("Joiner", "dragon-duel", data.DecksById["starter-ice"], rules);

        var hostTask = DirectMatchConnection.HostAsync(invite, hostHandshake, seed: 123);
        await using var joiner = await DirectMatchConnection.JoinAsync(invite, joinHandshake);
        await using var host = await hostTask;

        Assert.True(host.IsHost);
        Assert.False(joiner.IsHost);
        Assert.Equal(123, host.MatchStart.Seed);
        Assert.Equal("Joiner", host.MatchStart.Joiner.PlayerName);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await host.SendCommandAsync(new NetworkCommand { Kind = "advance", PlayerIndex = 0, Sequence = 1, PayloadJson = "" }, timeout.Token);
        var command = await joiner.ReadCommandAsync(timeout.Token);

        Assert.Equal("advance", command.Kind);
        Assert.Equal(1, command.Sequence);
    }

    [Fact]
    public async Task DirectLobbyWaitsForHostStartAndPreservesCompactInviteToken()
    {
        var data = LoadData();
        var rules = GameRulesConfig.ForPreset(GameRulesPreset.Standard);
        var port = GetFreeTcpPort();
        var invite = new NetworkInvite
        {
            Host = "127.0.0.1",
            Port = port,
            ModeId = DragonCardsModeIds.DragonDuel,
            ProtocolVersion = InviteCode.ProtocolVersion,
            LobbyToken = 0x73A1
        };
        var hostHandshake = DirectMatchConnection.CreateHandshake("Host", invite.ModeId, data.DecksById["starter-fire"], rules, invite.LobbyToken);
        var joinHandshake = DirectMatchConnection.CreateHandshake("Joiner", invite.ModeId, data.DecksById["starter-ice"], rules, invite.LobbyToken);

        var hostTask = DirectMatchConnection.HostLobbyAsync(invite, hostHandshake);
        await using var joiner = await DirectMatchConnection.JoinLobbyAsync(invite, joinHandshake);
        await using var host = await hostTask;

        Assert.True(host.IsHost);
        Assert.Equal("Joiner", host.Lobby.Joiner.PlayerName);
        Assert.Equal(invite.LobbyToken, joiner.Lobby.Host.LobbyToken);
        Assert.Equal(0, host.MatchStart.Seed);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var waitForStart = joiner.WaitForMatchStartAsync(timeout.Token);
        var hostStart = await host.StartMatchAsync(seed: 717, timeout.Token);
        var joinStart = await waitForStart;

        Assert.Equal(717, hostStart.Seed);
        Assert.Equal(hostStart.Seed, joinStart.Seed);
        Assert.Equal(hostStart.ModeId, joinStart.ModeId);
        Assert.Equal(hostStart.Host.PlayerName, joinStart.Host.PlayerName);
        Assert.Equal(hostStart.Joiner.PlayerName, joinStart.Joiner.PlayerName);
    }

    [Fact]
    public async Task LanDiscoveryResolvesTheFiveCharacterLobbyCode()
    {
        var invite = new NetworkInvite
        {
            Host = "203.0.113.42",
            Port = GetFreeTcpPort(),
            ModeId = DragonCardsModeIds.DragonDuel,
            ProtocolVersion = InviteCode.ProtocolVersion,
            LobbyToken = 0x2A7B
        };

        var code = InviteCode.EncodeLobbyCode(invite.LobbyToken);
        Assert.True(InviteCode.TryDecodeLobbyCode(code.ToLowerInvariant(), out var token, out _));
        await using var host = LanLobbyDiscoveryHost.Start(invite);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var resolved = await LanLobbyDiscovery.ResolveAsync(token, TimeSpan.FromSeconds(2), timeout.Token);

        Assert.NotEqual(invite.Host, resolved.Host);
        Assert.True(System.Net.IPAddress.TryParse(resolved.Host, out _));
        Assert.Equal(invite.Port, resolved.Port);
        Assert.Equal(invite.ModeId, resolved.ModeId);
        Assert.Equal(invite.LobbyToken, resolved.LobbyToken);
    }

    [Fact]
    public async Task FiveCharacterCodeDiscoversAndJoinsAHostedLobby()
    {
        var data = LoadData();
        var rules = GameRulesConfig.ForPreset(GameRulesPreset.Standard);
        var invite = new NetworkInvite
        {
            Host = "203.0.113.42",
            Port = GetFreeTcpPort(),
            ModeId = DragonCardsModeIds.DragonDuel,
            ProtocolVersion = InviteCode.ProtocolVersion,
            LobbyToken = 0x4D2E
        };
        var hostHandshake = DirectMatchConnection.CreateHandshake("Host", invite.ModeId, data.DecksById["starter-fire"], rules, invite.LobbyToken);
        var joinHandshake = DirectMatchConnection.CreateHandshake("Joiner", invite.ModeId, data.DecksById["starter-ice"], rules, invite.LobbyToken);

        var hostTask = DirectMatchConnection.HostLobbyAsync(invite, hostHandshake);
        var lobbyCode = InviteCode.EncodeLobbyCode(invite.LobbyToken);
        Assert.True(InviteCode.TryDecodeLobbyCode(lobbyCode, out var token, out _));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var discoveredInvite = await LanLobbyDiscovery.ResolveAsync(token, TimeSpan.FromSeconds(2), timeout.Token);
        await using var joiner = await DirectMatchConnection.JoinLobbyAsync(discoveredInvite, joinHandshake, timeout.Token);
        await using var host = await hostTask;

        Assert.True(host.IsHost);
        Assert.False(joiner.IsHost);
        Assert.Equal("Joiner", host.Lobby.Joiner.PlayerName);
        Assert.Equal(invite.LobbyToken, joiner.Lobby.Host.LobbyToken);
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
    public void AiPlaystyleChangesPlayableCardPriority()
    {
        var aggroEngine = CreateEngine();
        SetActivePhase(aggroEngine, playerIndex: 1, phase: "Main");
        PrepareStylePriorityHand(aggroEngine);
        var ai = new DragonDuelAi();

        ai.RunUntilHumanInput(aggroEngine, aiPlayerIndex: 1, GameRulesConfig.ForPreset(GameRulesPreset.Standard, Playstyle.Aggro), maxActions: 1);

        Assert.Contains(aggroEngine.State.Players[1].UnitField, card => card.CardId == "lightning-arc-lancer");

        var rampEngine = CreateEngine();
        SetActivePhase(rampEngine, playerIndex: 1, phase: "Main");
        PrepareStylePriorityHand(rampEngine);

        ai.RunUntilHumanInput(rampEngine, aiPlayerIndex: 1, GameRulesConfig.ForPreset(GameRulesPreset.Standard, Playstyle.Ramp), maxActions: 1);

        Assert.Contains(rampEngine.State.Players[1].SupportField, card => card.CardId == "lightning-thunder-relay");
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
        Assert.NotNull(engine.State.PendingCombatAction);
        PassCombatPriorityUntilResolved(engine);

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

    [Fact]
    public void CardFrameRulesSummaryCapsLinesWithEllipsis()
    {
        var card = new CardDefinition
        {
            Name = "Long Rules Test",
            Type = "Support",
            RulesText = "When played, draw 1 card and gain 1 Fire energy. Then return an enemy support to its owner's hand.",
            Abilities =
            [
                new ActivatedAbilityDefinition
                {
                    Name = "Flare Drive",
                    Cost = new Dictionary<string, int> { ["Fire"] = 2 },
                    RulesText = "Your next card costs 2 less."
                }
            ]
        };

        var summary = CardDetailFormatter.FrameRulesSummary(card, maxLines: 2, maxCharactersPerLine: 36);
        var lines = summary.Split(Environment.NewLine);

        Assert.True(lines.Length <= 2);
        Assert.EndsWith("...", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DeckAssistantRespectsOwnedCopiesForProgressionSuggestions()
    {
        var data = LoadData();
        var profile = new PlayerProfile();
        PlayerCollection.GrantDeck(profile, data.DecksById["starter-fire"], data, addStarterOwnership: true);
        var partial = data.DecksById["starter-fire"] with
        {
            Cards = data.DecksById["starter-fire"].Cards.Take(4).ToDictionary(entry => entry.Key, entry => 1, StringComparer.OrdinalIgnoreCase)
        };

        var suggestions = DeckBuilderAssistantService.SuggestAdds(data, partial, profile, GameRulesConfig.ForPreset(GameRulesPreset.Standard), DeckAssistantGoal.Balanced, 20);

        Assert.NotEmpty(suggestions);
        Assert.All(suggestions, suggestion =>
        {
            var owned = PlayerCollection.CountOwned(profile, suggestion.CardId);
            Assert.True(owned > partial.Cards.GetValueOrDefault(suggestion.CardId));
        });
    }

    [Fact]
    public void DeckAssistantSandboxSuggestionsCanUseAllCards()
    {
        var data = LoadData();
        var partial = data.DecksById["starter-fire"] with
        {
            Cards = data.DecksById["starter-fire"].Cards.Take(2).ToDictionary(entry => entry.Key, entry => 1, StringComparer.OrdinalIgnoreCase)
        };

        var suggestions = DeckBuilderAssistantService.SuggestAdds(data, partial, profile: null, GameRulesConfig.ForPreset(GameRulesPreset.Casual), DeckAssistantGoal.Control, 20);

        Assert.NotEmpty(suggestions);
        Assert.Contains(suggestions, suggestion => !partial.Cards.ContainsKey(suggestion.CardId));
    }

    [Fact]
    public void DeckAssistantSuggestionsDifferByPlaystyle()
    {
        var data = LoadData();
        var empty = new DeckDefinition
        {
            Id = "empty",
            Name = "Empty",
            ModeId = DragonCardsModeIds.DragonDuel,
            Cards = []
        };
        var sandbox = GameRulesConfig.ForPreset(GameRulesPreset.Casual);

        var aggro = DeckBuilderAssistantService.SuggestAdds(data, empty, null, sandbox, DeckAssistantGoal.Aggro, 5).Select(item => item.CardId).ToArray();
        var ramp = DeckBuilderAssistantService.SuggestAdds(data, empty, null, sandbox, DeckAssistantGoal.Ramp, 5).Select(item => item.CardId).ToArray();

        Assert.NotEqual(aggro, ramp);
    }

    [Fact]
    public void DeckAssistantAutoFillCompletesLegalOwnedDeck()
    {
        var data = LoadData();
        var profile = new PlayerProfile();
        PlayerCollection.GrantDeck(profile, data.DecksById["starter-fire"], data, addStarterOwnership: true);
        var partial = data.DecksById["starter-fire"] with
        {
            Id = "partial-fire",
            Name = "Partial Fire",
            Cards = data.DecksById["starter-fire"].Cards.Take(5).ToDictionary(entry => entry.Key, entry => 1, StringComparer.OrdinalIgnoreCase)
        };

        var filled = DeckBuilderAssistantService.AutoFill(data, partial, profile, GameRulesConfig.ForPreset(GameRulesPreset.Standard), DeckAssistantGoal.Aggro);

        Assert.Equal(data.GameModesById[DragonCardsModeIds.DragonDuel].DeckRules.DeckSize, filled.Count);
        Assert.Empty(GameDataValidator.ValidateDeck(filled, data));
        Assert.Empty(DeckOwnershipValidator.ValidateDeckOwnership(filled, profile, GameRulesConfig.ForPreset(GameRulesPreset.Standard)));
    }

    [Fact]
    public void DeckAssistantAnalysisReportsOwnershipAndMissingRoles()
    {
        var data = LoadData();
        var profile = new PlayerProfile();
        var starter = data.DecksById["starter-fire"];

        var analysis = DeckBuilderAssistantService.AnalyzeDeck(data, starter, profile, GameRulesConfig.ForPreset(GameRulesPreset.Standard), DeckAssistantGoal.Balanced);

        Assert.False(analysis.IsLegal);
        Assert.NotEmpty(analysis.OwnershipIssues);
        Assert.Contains(analysis.Notes, note => note.Contains("owned-copy", StringComparison.OrdinalIgnoreCase));
    }

    private static GameData LoadData() => GameData.LoadDefault();

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static DragonDuelEngine CreateEngine()
    {
        var data = LoadData();
        return DragonDuelEngine.Create(
            data,
            "dragon-duel",
            data.DecksById["starter-fire"],
            data.DecksById["starter-ice"],
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

    private static GameActionResult PassCombatPriorityUntilResolved(DragonDuelEngine engine)
    {
        var result = GameActionResult.Fail("Combat action window did not resolve.");
        for (var guard = 0; guard < 4 && engine.State.PendingCombatAction is not null; guard++)
        {
            var priorityPlayer = engine.State.PendingCombatAction.PriorityPlayerIndex;
            result = engine.PassCombatAction(priorityPlayer);
            Assert.True(result.Success, result.Message);
        }

        Assert.Null(engine.State.PendingCombatAction);
        return result;
    }

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

    private static void PrepareStylePriorityHand(DragonDuelEngine engine)
    {
        var aiPlayer = engine.State.Players[1];
        aiPlayer.Hand.Clear();
        aiPlayer.Hand.Add(new CardInstance("lightning-arc-lancer"));
        aiPlayer.Hand.Add(new CardInstance("lightning-thunder-relay"));
        aiPlayer.EnergyPool["Lightning"] = 5;
        engine.State.EnergyAddsThisTurn = 1;
    }
}
