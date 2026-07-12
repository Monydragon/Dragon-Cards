namespace DragonCards.Core;

public static class DragonCardsModeIds
{
    public const string DragonDuel = "dragon-duel";
    public const string StarterClash = "starter-clash";
    public const string DragonAvatar = "dragon-avatar";
    public const string SealedGauntlet = "sealed-gauntlet";
    public const string SandboxLab = "sandbox-lab";
    public const string TutorialTrials = "tutorial-trials";
}

public sealed record PlayableModeDefinition(
    string Id,
    string Name,
    string Description,
    string ActionLabel,
    bool StartsMatch,
    bool ProgressionEligible);

public static class PlayableModeCatalog
{
    public static readonly IReadOnlyList<PlayableModeDefinition> All =
    [
        new(DragonCardsModeIds.DragonDuel, "Dragon Duel", "The standard 50-card Dragon Cards duel with progression-safe rewards.", "Start Duel", true, true),
        new(DragonCardsModeIds.StarterClash, "Starter Clash", "Quickly battle with mono-element starter decks. Owned starters apply in progression modes; sandbox can preview all.", "Choose Starters", true, true),
        new(DragonCardsModeIds.DragonAvatar, "Dragon Avatar", "A progression-eligible 1v1 singleton identity mode inspired by commander-style deck expression.", "Choose Avatar", true, true),
        new(DragonCardsModeIds.SealedGauntlet, "Sealed Gauntlet", "Open a deterministic temporary six-booster pool, build a 40-card deck, and earn progression through the AI challenge.", "Generate Pool", true, true),
        new(DragonCardsModeIds.SandboxLab, "Sandbox Lab", "All cards, all starters, unlimited deck building, and no progression pressure.", "Open Lab", true, false),
        new(DragonCardsModeIds.TutorialTrials, "Tutorial Trials", "Guided lessons with one-time 250 Coin rewards per tutorial.", "Open Tutorials", false, true)
    ];

    public static PlayableModeDefinition ById(string id) =>
        All.First(mode => mode.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

public sealed record AvatarDeckValidationContext(
    string AvatarCardId,
    DeckDefinition Deck);

public static class DragonAvatarService
{
    public const int DeckSize = 60;
    public const int ReplayGenericCostIncrease = 2;

    public static IReadOnlyList<CardDefinition> AvatarCandidates(GameData data) =>
        data.Cards
            .Where(card => card.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase))
            .Where(card => CardRarities.Normalize(card.Rarity) is CardRarities.Legendary or CardRarities.Mythic)
            .OrderBy(card => card.Elements.FirstOrDefault() ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<CardDefinition> PlayableAvatarCandidates(GameData data) =>
        AvatarCandidates(data)
            .Where(card => LegalDeckCards(data, card).Count() >= DeckSize)
            .ToArray();

    public static IReadOnlyList<ValidationIssue> ValidateAvatarDeck(GameData data, string avatarCardId, DeckDefinition deck)
    {
        var issues = GameDataValidator.ValidateDeck(deck, data, DragonCardsModeIds.DragonAvatar).ToList();
        if (!data.CardsById.TryGetValue(avatarCardId, out var avatar))
        {
            issues.Add(new ValidationIssue("avatar.missing", $"Avatar card '{avatarCardId}' does not exist.", avatarCardId));
            return issues;
        }

        if (!avatar.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue("avatar.type", "Dragon Avatar must be a Unit.", avatarCardId));
        }

        if (!CardRarities.IsRarePlus(avatar.Rarity) || CardRarities.Normalize(avatar.Rarity) == CardRarities.Rare)
        {
            issues.Add(new ValidationIssue("avatar.rarity", "Dragon Avatar must be Legendary or Mythic.", avatarCardId));
        }

        if (deck.Count != DeckSize)
        {
            issues.Add(new ValidationIssue("avatar.deck_size", $"Dragon Avatar deck must contain exactly {DeckSize} cards.", deck.Id));
        }

        foreach (var (cardId, count) in deck.Cards)
        {
            if (count != 1)
            {
                issues.Add(new ValidationIssue("avatar.singleton", $"Dragon Avatar decks allow one copy of '{cardId}'.", cardId));
            }

            if (!data.CardsById.TryGetValue(cardId, out var card))
            {
                issues.Add(new ValidationIssue("avatar.card_missing", $"Deck references missing card '{cardId}'.", cardId));
                continue;
            }

            if (!IsWithinIdentity(avatar, card))
            {
                issues.Add(new ValidationIssue("avatar.identity", $"'{card.Name}' is outside {avatar.Name}'s element identity.", cardId));
            }
        }

        return issues;
    }

    public static int ReplayCostIncrease(int previousCommandZoneCasts) =>
        Math.Max(0, previousCommandZoneCasts) * ReplayGenericCostIncrease;

    public static DeckDefinition BuildSampleAvatarDeck(GameData data, string avatarCardId, string idSuffix = "")
    {
        var avatar = data.CardsById[avatarCardId];
        var cards = LegalDeckCards(data, avatar)
            .OrderByDescending(card => CardRarities.IsRarePlus(card.Rarity))
            .ThenBy(card => card.TotalCost)
            .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
            .Take(DeckSize)
            .ToDictionary(card => card.Id, _ => 1, StringComparer.OrdinalIgnoreCase);

        return new DeckDefinition
        {
            Id = $"avatar-{avatar.Id}{idSuffix}",
            Name = $"{avatar.Name} Avatar Deck",
            ModeId = DragonCardsModeIds.DragonAvatar,
            Cards = cards
        };
    }

    private static IEnumerable<CardDefinition> LegalDeckCards(GameData data, CardDefinition avatar)
    {
        var mode = data.GameModesById[DragonCardsModeIds.DragonAvatar];
        return data.Cards
            .Where(card => mode.AllowedCardTypes.Contains(card.Type, StringComparer.OrdinalIgnoreCase))
            .Where(card => !EnergySource.IsEnergySourceToken(card))
            .Where(card => IsWithinIdentity(avatar, card));
    }

    private static bool IsWithinIdentity(CardDefinition avatar, CardDefinition card)
    {
        var identity = avatar.Elements.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return card.Elements.Count == 0 || card.Elements.All(identity.Contains);
    }
}

public sealed record SealedPool(IReadOnlyList<string> CardIds, DeckDefinition Deck);

public static class SealedGauntletService
{
    public const int BoosterCount = 6;
    public const int DeckSize = 40;
    public const int CompletionCoins = 500;

    public static SealedPool GeneratePool(GameData data, int seed)
    {
        var profile = new PlayerProfile { PlayerName = "Sealed Preview" };
        var opening = BoosterService.OpenBoosters(data, profile, BoosterService.StandardBoosterId, BoosterCount, seed, consumeUnopened: false);
        var pool = opening.Cards.Select(card => card.CardId).ToArray();
        var chosen = pool
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Card: data.CardsById[group.Key], Count: group.Count()))
            .OrderByDescending(item => CardRarityScore(item.Card.Rarity))
            .ThenByDescending(item => item.Card.Power)
            .ThenBy(item => item.Card.TotalCost)
            .ThenBy(item => item.Card.Name, StringComparer.OrdinalIgnoreCase)
            .Take(DeckSize)
            .ToDictionary(item => item.Card.Id, _ => 1, StringComparer.OrdinalIgnoreCase);

        if (chosen.Count < DeckSize)
        {
            foreach (var card in data.Cards.OrderBy(card => card.Name, StringComparer.OrdinalIgnoreCase))
            {
                chosen.TryAdd(card.Id, 1);
                if (chosen.Count == DeckSize)
                {
                    break;
                }
            }
        }

        return new SealedPool(pool, new DeckDefinition
        {
            Id = $"sealed-{seed}",
            Name = "Sealed Gauntlet Deck",
            ModeId = DragonCardsModeIds.SealedGauntlet,
            Cards = chosen
        });
    }

    private static int CardRarityScore(string rarity) => CardRarities.Normalize(rarity) switch
    {
        CardRarities.Mythic => 5,
        CardRarities.Legendary => 4,
        CardRarities.Rare => 3,
        CardRarities.Uncommon => 2,
        _ => 1
    };
}
