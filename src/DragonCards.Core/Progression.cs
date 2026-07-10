using System.Text.Json;
using System.Text.Json.Serialization;

namespace DragonCards.Core;

public enum GameRulesPreset
{
    Casual,
    Easy,
    Standard,
    Hard,
    VeryHard,
    Insane,
    Custom
}

public enum Playstyle
{
    Balanced,
    Aggro,
    Control,
    Ramp,
    Combo
}

public static class CardRarities
{
    public const string Common = "Common";
    public const string Uncommon = "Uncommon";
    public const string Rare = "Rare";
    public const string Legendary = "Legendary";
    public const string Mythic = "Mythic";
    public const string Legend = Legendary;

    public static readonly string[] All = [Common, Uncommon, Rare, Legendary, Mythic];
    public static readonly string[] RarePlus = [Rare, Legendary, Mythic];

    public static string Normalize(string? rarity)
    {
        if (string.IsNullOrWhiteSpace(rarity))
        {
            return Common;
        }

        if (rarity.Equals("Legendary", StringComparison.OrdinalIgnoreCase) ||
            rarity.Equals("Legend", StringComparison.OrdinalIgnoreCase))
        {
            return Legendary;
        }

        if (rarity.Equals("Mythic", StringComparison.OrdinalIgnoreCase) ||
            rarity.Equals("Mythical", StringComparison.OrdinalIgnoreCase))
        {
            return Mythic;
        }

        return All.FirstOrDefault(item => item.Equals(rarity, StringComparison.OrdinalIgnoreCase)) ?? Common;
    }

    public static bool IsRarePlus(string? rarity) =>
        RarePlus.Contains(Normalize(rarity), StringComparer.OrdinalIgnoreCase);
}

public sealed record GameRulesConfig
{
    public GameRulesPreset Preset { get; init; } = GameRulesPreset.Standard;
    public Playstyle Playstyle { get; init; } = Playstyle.Balanced;
    public bool ProgressionEnabled { get; init; } = true;
    public bool AllUnlocks { get; init; }
    public bool UnlimitedDeckBuilder { get; init; }
    public bool StarterUnlockOverride { get; init; }
    public decimal RewardMultiplier { get; init; } = 1m;
    public decimal AiDifficultyModifier { get; init; } = 1m;
    public bool EnforceDeckOwnership { get; init; } = true;
    public bool UsesDefaultDeckRules { get; init; } = true;

    [JsonIgnore]
    public bool IsSandbox => AllUnlocks || UnlimitedDeckBuilder || !EnforceDeckOwnership || StarterUnlockOverride;

    [JsonIgnore]
    public bool IsProgressionSafe =>
        ProgressionEnabled &&
        !IsSandbox &&
        UsesDefaultDeckRules &&
        RewardMultiplier == DefaultRewardMultiplier(Preset);

    public static GameRulesConfig ForPreset(GameRulesPreset preset, Playstyle playstyle = Playstyle.Balanced)
    {
        var baseConfig = preset switch
        {
            GameRulesPreset.Casual => new GameRulesConfig
            {
                Preset = preset,
                ProgressionEnabled = false,
                AllUnlocks = true,
                UnlimitedDeckBuilder = true,
                RewardMultiplier = 0m,
                AiDifficultyModifier = 0.75m,
                EnforceDeckOwnership = false
            },
            GameRulesPreset.Easy => new GameRulesConfig
            {
                Preset = preset,
                RewardMultiplier = 0.85m,
                AiDifficultyModifier = 0.75m
            },
            GameRulesPreset.Hard => new GameRulesConfig
            {
                Preset = preset,
                RewardMultiplier = 1.2m,
                AiDifficultyModifier = 1.2m
            },
            GameRulesPreset.VeryHard => new GameRulesConfig
            {
                Preset = preset,
                RewardMultiplier = 1.4m,
                AiDifficultyModifier = 1.45m
            },
            GameRulesPreset.Insane => new GameRulesConfig
            {
                Preset = preset,
                RewardMultiplier = 1.75m,
                AiDifficultyModifier = 1.8m
            },
            GameRulesPreset.Custom => new GameRulesConfig
            {
                Preset = preset
            },
            _ => new GameRulesConfig
            {
                Preset = GameRulesPreset.Standard
            }
        };

        return baseConfig with { Playstyle = playstyle };
    }

    public GameRulesConfig Normalize()
    {
        var rewardMultiplier = Math.Clamp(RewardMultiplier, 0m, 10m);
        var aiDifficulty = Math.Clamp(AiDifficultyModifier, 0.1m, 10m);
        var progressionEnabled = ProgressionEnabled &&
            Preset != GameRulesPreset.Casual &&
            !AllUnlocks &&
            !UnlimitedDeckBuilder &&
            !StarterUnlockOverride &&
            EnforceDeckOwnership &&
            UsesDefaultDeckRules &&
            rewardMultiplier == DefaultRewardMultiplier(Preset);

        return this with
        {
            RewardMultiplier = rewardMultiplier,
            AiDifficultyModifier = aiDifficulty,
            ProgressionEnabled = progressionEnabled
        };
    }

    private static decimal DefaultRewardMultiplier(GameRulesPreset preset) => preset switch
    {
        GameRulesPreset.Easy => 0.85m,
        GameRulesPreset.Hard => 1.2m,
        GameRulesPreset.VeryHard => 1.4m,
        GameRulesPreset.Insane => 1.75m,
        GameRulesPreset.Casual => 0m,
        _ => 1m
    };
}

public sealed record PlayerProfile
{
    public int Version { get; init; } = 2;
    public string PlayerName { get; set; } = "Player";
    public int Level { get; set; } = 1;
    public int Experience { get; set; }
    public int Coins { get; set; }
    public int UnopenedBoosters { get; set; }
    public Dictionary<string, int> UnopenedPacks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public GameRulesConfig DefaultRules { get; set; } = GameRulesConfig.ForPreset(GameRulesPreset.Standard);
    public string SelectedStarterDeckId { get; set; } = "";
    public string ActiveDeckId { get; set; } = "";
    public Dictionary<string, int> OwnedCards { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> OwnedStarterDeckIds { get; set; } = [];
    public List<string> CompletedTutorialIds { get; set; } = [];

    [JsonIgnore]
    public int ExperienceIntoLevel => ProgressionService.ExperienceIntoLevel(Experience);

    [JsonIgnore]
    public int ExperienceForNextLevel => Level >= ProgressionService.MaxLevel ? 0 : ProgressionService.ExperiencePerLevel;

    [JsonIgnore]
    public int TotalUnopenedPacks => UnopenedPacks.Values.Sum();

    public void Normalize()
    {
        PlayerName = string.IsNullOrWhiteSpace(PlayerName) ? "Player" : PlayerName.Trim();
        Experience = Math.Clamp(Experience, 0, ProgressionService.MaxExperience);
        Level = ProgressionService.LevelForExperience(Experience);
        Coins = Math.Max(0, Coins);
        UnopenedPacks ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (UnopenedBoosters > 0)
        {
            UnopenedPacks[BoosterService.StandardBoosterId] = UnopenedPacks.GetValueOrDefault(BoosterService.StandardBoosterId) + UnopenedBoosters;
        }

        UnopenedBoosters = 0;
        UnopenedPacks = UnopenedPacks
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value > 0)
            .ToDictionary(entry => entry.Key.Trim(), entry => Math.Max(0, entry.Value), StringComparer.OrdinalIgnoreCase);
        DefaultRules = (DefaultRules ?? GameRulesConfig.ForPreset(GameRulesPreset.Standard)).Normalize();
        OwnedCards = OwnedCards
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value > 0)
            .ToDictionary(entry => entry.Key, entry => Math.Min(PlayerCollection.MaxOwnedCopies, entry.Value), StringComparer.OrdinalIgnoreCase);
        OwnedStarterDeckIds = OwnedStarterDeckIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        CompletedTutorialIds = CompletedTutorialIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed record TutorialCompletionResult(bool Awarded, int CoinsAwarded, string Message);

public static class TutorialRewardService
{
    public const int CoinsPerTutorial = 250;

    public static TutorialCompletionResult CompleteTutorial(PlayerProfile profile, string tutorialId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        tutorialId = string.IsNullOrWhiteSpace(tutorialId) ? "" : tutorialId.Trim();
        if (tutorialId.Length == 0)
        {
            return new TutorialCompletionResult(false, 0, "Tutorial id is missing.");
        }

        profile.Normalize();
        if (profile.CompletedTutorialIds.Contains(tutorialId, StringComparer.OrdinalIgnoreCase))
        {
            return new TutorialCompletionResult(false, 0, "Tutorial already completed.");
        }

        profile.CompletedTutorialIds.Add(tutorialId);
        profile.Coins += CoinsPerTutorial;
        profile.Normalize();
        return new TutorialCompletionResult(true, CoinsPerTutorial, $"+{CoinsPerTutorial} Coins awarded.");
    }
}

public sealed record MatchReward(
    bool ProgressionApplied,
    int ExperienceGained,
    int CoinsGained,
    int BoostersGained,
    int LevelsGained,
    int StartingLevel,
    int EndingLevel,
    string Reason);

public enum MatchRewardKind
{
    Ai,
    HumanMultiplayer
}

public static class RewardCalculator
{
    public static MatchReward PreviewMatchReward(PlayerProfile profile, GameRulesConfig rules, MatchRewardKind kind, bool won)
    {
        ArgumentNullException.ThrowIfNull(profile);
        rules = (rules ?? GameRulesConfig.ForPreset(GameRulesPreset.Standard)).Normalize();
        if (!rules.IsProgressionSafe)
        {
            return new MatchReward(false, 0, 0, 0, 0, profile.Level, profile.Level, "Progression disabled for this rules preset.");
        }

        var (baseXp, baseCoins) = kind == MatchRewardKind.Ai
            ? won ? (600, 100) : (250, 40)
            : won ? (900, 150) : (350, 60);
        var xp = DecimalToInt(baseXp * rules.RewardMultiplier);
        var coins = DecimalToInt(baseCoins * rules.RewardMultiplier);
        var startingLevel = profile.Level;
        var endingLevel = ProgressionService.LevelForExperience(Math.Min(ProgressionService.MaxExperience, profile.Experience + xp));
        var levelsGained = Math.Max(0, endingLevel - startingLevel);
        return new MatchReward(
            true,
            xp,
            coins + levelsGained * ProgressionService.CoinsPerLevel,
            endingLevel / ProgressionService.BoosterEveryLevels - startingLevel / ProgressionService.BoosterEveryLevels,
            levelsGained,
            startingLevel,
            endingLevel,
            "");
    }

    private static int DecimalToInt(decimal value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}

public static class ProgressionService
{
    public const int MaxLevel = 100;
    public const int ExperiencePerLevel = 1000;
    public const int CoinsPerLevel = 50;
    public const int BoosterEveryLevels = 5;
    public const int StarterDeckCost = 2500;
    public const int BoosterCost = 500;
    public const int MaxExperience = (MaxLevel - 1) * ExperiencePerLevel;

    public static PlayerProfile CreateProfile(
        string playerName,
        GameRulesConfig rules,
        DeckDefinition starterDeck,
        GameData data)
    {
        ArgumentNullException.ThrowIfNull(starterDeck);
        ArgumentNullException.ThrowIfNull(data);
        var profile = new PlayerProfile
        {
            PlayerName = playerName,
            DefaultRules = rules.Normalize(),
            SelectedStarterDeckId = starterDeck.Id,
            ActiveDeckId = starterDeck.Id
        };
        PlayerCollection.GrantDeck(profile, starterDeck, data, addStarterOwnership: true);
        profile.Normalize();
        return profile;
    }

    public static MatchReward ApplyMatchReward(PlayerProfile profile, GameRulesConfig rules, MatchRewardKind kind, bool won)
    {
        var reward = RewardCalculator.PreviewMatchReward(profile, rules, kind, won);
        if (!reward.ProgressionApplied)
        {
            return reward;
        }

        profile.Experience = Math.Min(MaxExperience, profile.Experience + reward.ExperienceGained);
        profile.Level = LevelForExperience(profile.Experience);
        profile.Coins += reward.CoinsGained;
        BoosterService.AddUnopenedPack(profile, BoosterService.StandardBoosterId, reward.BoostersGained);
        profile.Normalize();
        return reward;
    }

    public static int LevelForExperience(int experience) =>
        Math.Clamp(experience, 0, MaxExperience) / ExperiencePerLevel + 1;

    public static int ExperienceIntoLevel(int experience) =>
        LevelForExperience(experience) >= MaxLevel ? 0 : Math.Clamp(experience, 0, MaxExperience) % ExperiencePerLevel;
}

public static class PlayerCollection
{
    public const int MaxOwnedCopies = 3;

    public static int CountOwned(PlayerProfile profile, string cardId) =>
        profile.OwnedCards.GetValueOrDefault(cardId);

    public static bool HasStarterDeck(PlayerProfile profile, string deckId) =>
        profile.OwnedStarterDeckIds.Contains(deckId, StringComparer.OrdinalIgnoreCase);

    public static BoosterCardGrant GrantCard(PlayerProfile profile, CardDefinition card, int count = 1)
    {
        var added = 0;
        var duplicateCoins = 0;
        for (var i = 0; i < Math.Max(0, count); i++)
        {
            var owned = profile.OwnedCards.GetValueOrDefault(card.Id);
            if (owned < MaxOwnedCopies)
            {
                profile.OwnedCards[card.Id] = owned + 1;
                added++;
            }
            else
            {
                duplicateCoins += DuplicateCoins(card.Rarity);
            }
        }

        if (duplicateCoins > 0)
        {
            profile.Coins += duplicateCoins;
        }

        return new BoosterCardGrant(card.Id, card.Name, card.Rarity, added, duplicateCoins);
    }

    public static IReadOnlyList<BoosterCardGrant> GrantDeck(PlayerProfile profile, DeckDefinition deck, GameData data, bool addStarterOwnership)
    {
        var grants = new List<BoosterCardGrant>();
        foreach (var (cardId, count) in deck.Cards)
        {
            if (data.CardsById.TryGetValue(cardId, out var card))
            {
                grants.Add(GrantCard(profile, card, count));
            }
        }

        if (addStarterOwnership && !HasStarterDeck(profile, deck.Id))
        {
            profile.OwnedStarterDeckIds.Add(deck.Id);
        }

        profile.Normalize();
        return grants;
    }

    public static int DuplicateCoins(string rarity) => CardRarities.Normalize(rarity) switch
    {
        CardRarities.Uncommon => 25,
        CardRarities.Rare => 75,
        CardRarities.Legendary => 250,
        CardRarities.Mythic => 750,
        _ => 10
    };
}

public static class DeckOwnershipValidator
{
    public static IReadOnlyList<ValidationIssue> ValidateDeckOwnership(DeckDefinition deck, PlayerProfile profile, GameRulesConfig rules)
    {
        rules = (rules ?? GameRulesConfig.ForPreset(GameRulesPreset.Standard)).Normalize();
        if (!rules.EnforceDeckOwnership || rules.UnlimitedDeckBuilder || rules.AllUnlocks)
        {
            return [];
        }

        var issues = new List<ValidationIssue>();
        foreach (var (cardId, required) in deck.Cards)
        {
            var owned = PlayerCollection.CountOwned(profile, cardId);
            if (required > owned)
            {
                issues.Add(new ValidationIssue("deck.ownership", $"Deck uses {required} copies of '{cardId}', but only {owned} owned.", cardId));
            }
        }

        return issues;
    }
}

public sealed record BoosterCardGrant(string CardId, string CardName, string Rarity, int CopiesAdded, int DuplicateCoins);

public sealed record BoosterOpening(IReadOnlyList<BoosterCardGrant> Cards, int CoinsFromDuplicates, string PackId = BoosterService.StandardBoosterId, string PackName = "Core Booster")
{
    public bool HasCards => Cards.Count > 0;
    public int PackCount => Math.Max(1, Cards.Count / BoosterService.CardsPerPack);
}

public enum ShopItemKind
{
    Booster,
    StarterDeck,
    SingleCard
}

public sealed record ShopCatalogItem
{
    public string Id { get; init; } = "";
    public ShopItemKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public int Cost { get; init; }
    public string PackId { get; init; } = "";
    public string DeckId { get; init; } = "";
    public string CardId { get; init; } = "";
    public string SetId { get; init; } = "";
}

public sealed record ShopPurchaseResult(bool Success, string Message, BoosterCardGrant? Grant = null);

public static class ShopCatalogService
{
    public static IReadOnlyList<ShopCatalogItem> CreateCatalog(GameData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var items = new List<ShopCatalogItem>
        {
            new()
            {
                Id = BoosterService.StandardBoosterId,
                Kind = ShopItemKind.Booster,
                Name = "Core Booster",
                Description = "5 cards from all available sets: 3 Common, 1 Uncommon, 1 Rare+.",
                Cost = ProgressionService.BoosterCost,
                PackId = BoosterService.StandardBoosterId
            }
        };

        items.AddRange(data.Cards
            .Select(card => card.SetId)
            .Where(setId => !string.IsNullOrWhiteSpace(setId) &&
                !setId.Equals(CardSets.Core, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(BoosterService.SetDisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(setId =>
            {
                var packId = BoosterService.PackIdForSet(setId);
                var setName = BoosterService.SetDisplayName(setId);
                return new ShopCatalogItem
                {
                    Id = packId,
                    Kind = ShopItemKind.Booster,
                    Name = $"{setName} Booster",
                    Description = $"5 cards focused on the {setName} expansion.",
                    Cost = ProgressionService.BoosterCost,
                    PackId = packId,
                    SetId = setId
                };
            }));

        items.AddRange(data.Decks
            .Where(deck => deck.Id.StartsWith("starter-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(deck => deck.Name, StringComparer.OrdinalIgnoreCase)
            .Select(deck => new ShopCatalogItem
            {
                Id = $"starter:{deck.Id}",
                Kind = ShopItemKind.StarterDeck,
                Name = deck.Name,
                Description = "50-card mono-element starter deck.",
                Cost = ProgressionService.StarterDeckCost,
                DeckId = deck.Id
            }));

        items.AddRange(data.Cards
            .OrderBy(card => CardRarityRank(card.Rarity))
            .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
            .Select(card => new ShopCatalogItem
            {
                Id = $"single:{card.Id}",
                Kind = ShopItemKind.SingleCard,
                Name = card.Name,
                Description = $"{card.Rarity} {card.Type} single.",
                Cost = SingleCardCost(card.Rarity),
                CardId = card.Id,
                SetId = card.SetId
            }));

        return items;
    }

    public static int SingleCardCost(string rarity) => CardRarities.Normalize(rarity) switch
    {
        CardRarities.Uncommon => 250,
        CardRarities.Rare => 750,
        CardRarities.Legendary => 2000,
        CardRarities.Mythic => 5000,
        _ => 100
    };

    public static ShopPurchaseResult BuySingleCard(PlayerProfile profile, CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(card);
        if (PlayerCollection.CountOwned(profile, card.Id) >= PlayerCollection.MaxOwnedCopies)
        {
            return new ShopPurchaseResult(false, "Already own the maximum copies.");
        }

        var cost = SingleCardCost(card.Rarity);
        if (profile.Coins < cost)
        {
            return new ShopPurchaseResult(false, "Not enough Coins.");
        }

        profile.Coins -= cost;
        var grant = PlayerCollection.GrantCard(profile, card);
        profile.Normalize();
        return new ShopPurchaseResult(true, $"{card.Name} purchased.", grant);
    }

    private static int CardRarityRank(string rarity) => CardRarities.Normalize(rarity) switch
    {
        CardRarities.Common => 0,
        CardRarities.Uncommon => 1,
        CardRarities.Rare => 2,
        CardRarities.Legendary => 3,
        CardRarities.Mythic => 4,
        _ => 0
    };
}

public static class BoosterService
{
    public const int CardsPerPack = 5;
    public const string StandardBoosterId = "booster-core";
    public const string AncientAwakeningBoosterId = "booster-ancient-awakening";
    public const string ElementalAscensionBoosterId = "booster-elemental-ascension";
    public const string PrimalClashBoosterId = "booster-primal-clash";
    private const string BoosterPrefix = "booster-";

    private static readonly string[] SlotRarities =
    [
        CardRarities.Common,
        CardRarities.Common,
        CardRarities.Common,
        CardRarities.Uncommon
    ];

    public static int GetUnopenedPackCount(PlayerProfile profile, string packId)
    {
        profile.Normalize();
        return profile.UnopenedPacks.GetValueOrDefault(packId);
    }

    public static void AddUnopenedPack(PlayerProfile profile, string packId, int count = 1)
    {
        if (count <= 0)
        {
            return;
        }

        profile.UnopenedPacks[packId] = profile.UnopenedPacks.GetValueOrDefault(packId) + count;
        profile.Normalize();
    }

    public static int TotalUnopenedPacks(PlayerProfile profile)
    {
        profile.Normalize();
        return profile.TotalUnopenedPacks;
    }

    public static BoosterOpening OpenBooster(GameData data, PlayerProfile profile, int? seed = null, bool consumeUnopened = true) =>
        OpenBooster(data, profile, StandardBoosterId, seed, consumeUnopened);

    public static BoosterOpening OpenBooster(GameData data, PlayerProfile profile, string packId, int? seed = null, bool consumeUnopened = true) =>
        OpenBoosters(data, profile, packId, quantity: 1, seed, consumeUnopened);

    public static BoosterOpening OpenBoosters(GameData data, PlayerProfile profile, string packId, int quantity, int? seed = null, bool consumeUnopened = true)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(profile);
        quantity = Math.Max(1, quantity);
        profile.Normalize();
        if (consumeUnopened)
        {
            var unopened = profile.UnopenedPacks.GetValueOrDefault(packId);
            if (unopened < quantity)
            {
                throw new InvalidOperationException("Not enough unopened boosters are available.");
            }

            profile.UnopenedPacks[packId] = unopened - quantity;
        }

        var random = seed is null ? Random.Shared : new Random(seed.Value);
        var setId = PackSetId(packId);
        var grants = new List<BoosterCardGrant>();
        for (var pack = 0; pack < quantity; pack++)
        {
            foreach (var rarity in SlotRarities)
            {
                grants.Add(PlayerCollection.GrantCard(profile, PickCard(data, rarity, random, setId)));
            }

            var finalRarity = RollRarePlusRarity(random);
            grants.Add(PlayerCollection.GrantCard(profile, PickCard(data, finalRarity, random, setId)));
        }

        profile.Normalize();
        return new BoosterOpening(grants, grants.Sum(item => item.DuplicateCoins), packId, PackName(packId));
    }

    public static bool BuyBooster(PlayerProfile profile) =>
        BuyBooster(profile, StandardBoosterId, quantity: 1);

    public static bool BuyBooster(PlayerProfile profile, string packId, int quantity = 1, int? unitCost = null)
    {
        quantity = Math.Max(1, quantity);
        var cost = (unitCost ?? ProgressionService.BoosterCost) * quantity;
        if (profile.Coins < cost)
        {
            return false;
        }

        profile.Coins -= cost;
        AddUnopenedPack(profile, packId, quantity);
        profile.Normalize();
        return true;
    }

    public static string PackName(string packId)
    {
        var setId = PackSetId(packId);
        return string.IsNullOrWhiteSpace(setId)
            ? "Core Booster"
            : $"{SetDisplayName(setId)} Booster";
    }

    public static string PackIdForSet(string setId) => $"{BoosterPrefix}{setId}";

    public static string? PackSetId(string packId)
    {
        if (string.IsNullOrWhiteSpace(packId) ||
            packId.Equals(StandardBoosterId, StringComparison.OrdinalIgnoreCase) ||
            !packId.StartsWith(BoosterPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return packId[BoosterPrefix.Length..];
    }

    public static string SetDisplayName(string setId) => setId switch
    {
        CardSets.AncientAwakening => "Ancient Awakening",
        CardSets.ElementalAscension => "Elemental Ascension",
        CardSets.PrimalClash => "Primal Clash",
        _ => string.Join(" ", setId
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]))
    };

    public static string RollRarePlusRarity(Random random)
    {
        var roll = random.NextDouble();
        if (roll < 0.01)
        {
            return CardRarities.Mythic;
        }

        return roll < 0.16 ? CardRarities.Legendary : CardRarities.Rare;
    }

    private static CardDefinition PickCard(GameData data, string rarity, Random random, string? setId = null)
    {
        var normalized = CardRarities.Normalize(rarity);
        var pool = data.Cards
            .Where(card => string.IsNullOrWhiteSpace(setId) || card.SetId.Equals(setId, StringComparison.OrdinalIgnoreCase))
            .Where(card => CardRarities.Normalize(card.Rarity).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .OrderBy(card => card.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (pool.Length == 0)
        {
            pool = data.Cards
                .Where(card => CardRarities.Normalize(card.Rarity).Equals(normalized, StringComparison.OrdinalIgnoreCase))
                .OrderBy(card => card.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (pool.Length == 0)
        {
            pool = data.Cards.OrderBy(card => card.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        return pool[random.Next(pool.Length)];
    }
}

public sealed record BattleSpoilsReward(bool ProgressionApplied, BoosterCardGrant? Grant, string Reason)
{
    public bool HasCard => Grant is not null;
}

public static class BattleSpoilsService
{
    public static BattleSpoilsReward GrantVictorySpoils(
        GameData data,
        PlayerProfile profile,
        GameRulesConfig rules,
        DeckDefinition opponentDeck,
        bool won,
        int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(opponentDeck);
        rules = (rules ?? GameRulesConfig.ForPreset(GameRulesPreset.Standard)).Normalize();
        if (!won)
        {
            return new BattleSpoilsReward(false, null, "Spoils are only awarded for victories.");
        }

        if (!rules.IsProgressionSafe)
        {
            return new BattleSpoilsReward(false, null, "Progression spoils disabled for this rules preset.");
        }

        var rarePlus = opponentDeck.Cards
            .Select(entry => data.CardsById.TryGetValue(entry.Key, out var card)
                ? (Card: (CardDefinition?)card, Count: entry.Value)
                : (Card: (CardDefinition?)null, Count: 0))
            .Where(entry => entry.Card is not null && entry.Count > 0 && CardRarities.IsRarePlus(entry.Card.Rarity))
            .Select(entry => (Card: entry.Card!, Count: entry.Count))
            .ToArray();
        if (rarePlus.Length == 0)
        {
            return new BattleSpoilsReward(false, null, "Opponent deck had no rare+ cards.");
        }

        var random = seed is null ? Random.Shared : new Random(seed.Value);
        var mythics = rarePlus.Where(entry => entry.Card.Rarity == CardRarities.Mythic).ToArray();
        var nonMythics = rarePlus.Where(entry => entry.Card.Rarity != CardRarities.Mythic).ToArray();
        var chosenPool = mythics.Length > 0 && (nonMythics.Length == 0 || random.NextDouble() < 0.10)
            ? mythics
            : nonMythics.Length > 0 ? nonMythics : mythics;
        var chosenCard = PickWeighted(chosenPool, random);
        var grant = PlayerCollection.GrantCard(profile, chosenCard);
        profile.Normalize();
        return new BattleSpoilsReward(true, grant, "");
    }

    private static CardDefinition PickWeighted((CardDefinition Card, int Count)[] pool, Random random)
    {
        var total = pool.Sum(entry => Math.Max(0, entry.Count));
        var roll = random.Next(Math.Max(1, total));
        foreach (var (card, count) in pool)
        {
            roll -= Math.Max(0, count);
            if (roll < 0)
            {
                return card;
            }
        }

        return pool[^1].Card;
    }
}

public static class CardDetailFormatter
{
    private static readonly string[] ElementOrder = ["Fire", "Ice", "Wind", "Earth", "Lightning", "Water", "Light", "Dark"];

    public static string Format(CardDefinition card, string? elementAdvantage = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        var lines = new List<string>
        {
            $"Name: {card.Name}",
            $"Rarity: {card.Rarity}",
            $"Type: {card.Type}",
            $"Elements: {(card.Elements.Count == 0 ? "None" : string.Join(", ", card.Elements))}",
            $"Cost: {CostText(card)}",
            card.Power > 0 ? $"Power: {card.Power}" : "Power: None"
        };

        if (!string.IsNullOrWhiteSpace(card.RulesText))
        {
            lines.Add($"Rules Text: {card.RulesText}");
        }

        if (card.Keywords.Count > 0)
        {
            lines.Add($"Keywords: {string.Join(", ", card.Keywords)}");
        }

        if (card.Abilities.Count > 0)
        {
            lines.Add("Activated Abilities:");
            lines.AddRange(card.Abilities.Select(ability => $"{ability.Name} ({CostText(ability.Cost)}): {ability.RulesText}"));
        }

        if (card.Tags.Count > 0)
        {
            lines.Add($"Tags: {string.Join(", ", card.Tags)}");
        }

        if (!string.IsNullOrWhiteSpace(elementAdvantage))
        {
            lines.Add(elementAdvantage);
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string RulesText(CardDefinition card)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(card.RulesText))
        {
            lines.Add(card.RulesText);
        }

        if (card.Keywords.Count > 0)
        {
            lines.Add($"Keywords: {string.Join(", ", card.Keywords)}");
        }

        foreach (var ability in card.Abilities)
        {
            lines.Add($"{ability.Name} ({CostText(ability.Cost)}): {ability.RulesText}");
        }

        return lines.Count == 0 ? "No rules text." : string.Join(Environment.NewLine, lines);
    }

    public static string FrameRulesSummary(CardDefinition card, int maxLines = 3, int maxCharactersPerLine = 48)
    {
        ArgumentNullException.ThrowIfNull(card);
        maxLines = Math.Max(1, maxLines);
        maxCharactersPerLine = Math.Max(12, maxCharactersPerLine);
        var wrapped = new List<string>();
        foreach (var entry in RulesText(card)
            .Replace("\r", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var line in WrapByCharacters(entry, maxCharactersPerLine))
            {
                wrapped.Add(line);
                if (wrapped.Count == maxLines)
                {
                    break;
                }
            }

            if (wrapped.Count == maxLines)
            {
                break;
            }
        }

        if (wrapped.Count == 0)
        {
            return "No rules text.";
        }

        var fullLineCount = RulesText(card)
            .Replace("\r", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(line => WrapByCharacters(line, maxCharactersPerLine))
            .Count();
        if (fullLineCount > wrapped.Count)
        {
            wrapped[^1] = WithEllipsis(wrapped[^1], maxCharactersPerLine);
        }

        return string.Join(Environment.NewLine, wrapped);
    }

    public static string CostText(CardDefinition card) => CostText(card.Cost);

    public static string CostText(IReadOnlyDictionary<string, int> cost) =>
        cost.Count == 0
            ? "0"
            : string.Join(" ", OrderedCosts(cost).Select(item =>
                item.Key.Equals(DragonCardConstants.GenericCost, StringComparison.OrdinalIgnoreCase)
                    ? $"Generic {item.Value}"
                    : $"{item.Key} {item.Value}"));

    private static IEnumerable<KeyValuePair<string, int>> OrderedCosts(IReadOnlyDictionary<string, int> costMap)
    {
        foreach (var element in ElementOrder)
        {
            if (costMap.TryGetValue(element, out var amount) && amount > 0)
            {
                yield return new KeyValuePair<string, int>(element, amount);
            }
        }

        foreach (var cost in costMap
            .Where(cost => cost.Value > 0 &&
                !cost.Key.Equals(DragonCardConstants.GenericCost, StringComparison.OrdinalIgnoreCase) &&
                !ElementOrder.Contains(cost.Key, StringComparer.OrdinalIgnoreCase))
            .OrderBy(cost => cost.Key, StringComparer.OrdinalIgnoreCase))
        {
            yield return cost;
        }

        if (costMap.TryGetValue(DragonCardConstants.GenericCost, out var generic) && generic > 0)
        {
            yield return new KeyValuePair<string, int>(DragonCardConstants.GenericCost, generic);
        }
    }

    private static IEnumerable<string> WrapByCharacters(string text, int maxCharactersPerLine)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = "";
        foreach (var word in words)
        {
            var candidate = string.IsNullOrEmpty(line) ? word : $"{line} {word}";
            if (candidate.Length > maxCharactersPerLine && !string.IsNullOrEmpty(line))
            {
                yield return line;
                line = word.Length > maxCharactersPerLine ? WithEllipsis(word, maxCharactersPerLine) : word;
            }
            else
            {
                line = candidate;
            }
        }

        if (!string.IsNullOrEmpty(line))
        {
            yield return line;
        }
    }

    private static string WithEllipsis(string text, int maxCharacters)
    {
        const string ellipsis = "...";
        if (text.EndsWith(ellipsis, StringComparison.Ordinal) || text.Length <= maxCharacters - ellipsis.Length)
        {
            return text.EndsWith(ellipsis, StringComparison.Ordinal) ? text : $"{text}{ellipsis}";
        }

        return $"{text[..Math.Max(0, maxCharacters - ellipsis.Length)].TrimEnd()}{ellipsis}";
    }
}

public static class PlayerProfileSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        TypeInfoResolver = DragonCardsProgressionJsonContext.Default,
        WriteIndented = true
    };

    public static string ToJson(PlayerProfile profile) => JsonSerializer.Serialize(profile, JsonOptions);

    public static PlayerProfile FromJson(string json)
    {
        var profile = JsonSerializer.Deserialize<PlayerProfile>(json, JsonOptions) ?? new PlayerProfile();
        profile.Normalize();
        return profile;
    }

    public static PlayerProfile Load(string path)
    {
        var profile = File.Exists(path) ? FromJson(File.ReadAllText(path)) : new PlayerProfile();
        profile.Normalize();
        return profile;
    }

    public static void Save(string path, PlayerProfile profile)
    {
        profile.Normalize();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, ToJson(profile));
    }
}

[JsonSerializable(typeof(PlayerProfile))]
[JsonSerializable(typeof(GameRulesConfig))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class DragonCardsProgressionJsonContext : JsonSerializerContext;
