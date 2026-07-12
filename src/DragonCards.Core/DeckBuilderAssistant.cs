namespace DragonCards.Core;

public enum DeckAssistantGoal
{
    Balanced,
    Aggro,
    Control,
    Ramp,
    Combo
}

public enum DeckSuggestionKind
{
    Add,
    Cut
}

public sealed record DeckRoleCounts
{
    public int Units { get; init; }
    public int Supports { get; init; }
    public int Spells { get; init; }
    public int Draw { get; init; }
    public int Removal { get; init; }
    public int Ramp { get; init; }
    public int Combat { get; init; }
    public decimal AverageCost { get; init; }
}

public sealed record DeckAnalysis
{
    public DeckAssistantGoal Goal { get; init; }
    public int DeckCount { get; init; }
    public int RequiredDeckCount { get; init; }
    public int CardsNeeded => Math.Max(0, RequiredDeckCount - DeckCount);
    public DeckRoleCounts Roles { get; init; } = new();
    public IReadOnlyList<ValidationIssue> ValidationIssues { get; init; } = [];
    public IReadOnlyList<ValidationIssue> OwnershipIssues { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    public bool IsLegal => ValidationIssues.Count == 0 && OwnershipIssues.Count == 0;
}

public sealed record DeckSuggestion
{
    public DeckSuggestionKind Kind { get; init; }
    public string CardId { get; init; } = "";
    public string CardName { get; init; } = "";
    public string Rarity { get; init; } = CardRarities.Common;
    public string Reason { get; init; } = "";
    public int CurrentCount { get; init; }
    public int SuggestedCount { get; init; }
    public int AvailableCount { get; init; }
    public int Score { get; init; }
}

public static class DeckBuilderAssistantService
{
    public static DeckAnalysis AnalyzeDeck(
        GameData data,
        DeckDefinition deck,
        PlayerProfile? profile,
        GameRulesConfig rules,
        DeckAssistantGoal goal)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(deck);
        rules = (rules ?? GameRulesConfig.ForPreset(GameRulesPreset.Standard)).Normalize();

        var mode = ModeForDeck(data, deck);
        var validationIssues = GameDataValidator.ValidateDeck(deck, data);
        var ownershipIssues = profile is null
            ? Array.Empty<ValidationIssue>()
            : DeckOwnershipValidator.ValidateDeckOwnership(deck, profile, rules);
        var roles = CountRoles(data, deck);
        var notes = BuildNotes(deck, mode, roles, validationIssues, ownershipIssues, goal);

        return new DeckAnalysis
        {
            Goal = goal,
            DeckCount = deck.Count,
            RequiredDeckCount = mode.DeckRules.DeckSize,
            Roles = roles,
            ValidationIssues = validationIssues,
            OwnershipIssues = ownershipIssues,
            Notes = notes
        };
    }

    public static IReadOnlyList<DeckSuggestion> SuggestAdds(
        GameData data,
        DeckDefinition deck,
        PlayerProfile? profile,
        GameRulesConfig rules,
        DeckAssistantGoal goal,
        int maxSuggestions = 8)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(deck);
        rules = (rules ?? GameRulesConfig.ForPreset(GameRulesPreset.Standard)).Normalize();
        var mode = ModeForDeck(data, deck);
        var roles = CountRoles(data, deck);
        var dominantElement = DominantElement(data, deck);
        var suggestions = new List<DeckSuggestion>();

        foreach (var card in data.Cards)
        {
            if (EnergySource.IsEnergySourceToken(card))
            {
                continue;
            }

            if (!mode.AllowedCardTypes.Contains(card.Type, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var current = deck.Cards.GetValueOrDefault(card.Id);
            var available = AvailableCopies(card, current, profile, rules, mode);
            if (available <= 0)
            {
                continue;
            }

            var score = AddScore(card, roles, dominantElement, goal, mode.DeckRules.DeckSize);
            suggestions.Add(new DeckSuggestion
            {
                Kind = DeckSuggestionKind.Add,
                CardId = card.Id,
                CardName = card.Name,
                Rarity = CardRarities.Normalize(card.Rarity),
                CurrentCount = current,
                SuggestedCount = current + 1,
                AvailableCount = available,
                Score = score,
                Reason = AddReason(card, roles, dominantElement, goal)
            });
        }

        return suggestions
            .OrderByDescending(item => item.Score)
            .ThenBy(item => data.CardsById[item.CardId].TotalCost)
            .ThenBy(item => item.CardName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, maxSuggestions))
            .ToArray();
    }

    public static IReadOnlyList<DeckSuggestion> SuggestCuts(
        GameData data,
        DeckDefinition deck,
        PlayerProfile? profile,
        GameRulesConfig rules,
        DeckAssistantGoal goal,
        int maxSuggestions = 8)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(deck);
        rules = (rules ?? GameRulesConfig.ForPreset(GameRulesPreset.Standard)).Normalize();
        var mode = ModeForDeck(data, deck);
        var roles = CountRoles(data, deck);
        var dominantElement = DominantElement(data, deck);
        var suggestions = new List<DeckSuggestion>();

        foreach (var (cardId, count) in deck.Cards)
        {
            if (count <= 0 || !data.CardsById.TryGetValue(cardId, out var card))
            {
                continue;
            }

            if (EnergySource.IsEnergySourceToken(card))
            {
                continue;
            }

            var score = CutScore(card, count, roles, dominantElement, goal, mode.DeckRules.DeckSize);
            suggestions.Add(new DeckSuggestion
            {
                Kind = DeckSuggestionKind.Cut,
                CardId = card.Id,
                CardName = card.Name,
                Rarity = CardRarities.Normalize(card.Rarity),
                CurrentCount = count,
                SuggestedCount = count - 1,
                AvailableCount = count,
                Score = score,
                Reason = CutReason(card, count, dominantElement, goal)
            });
        }

        return suggestions
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => data.CardsById[item.CardId].TotalCost)
            .ThenBy(item => item.CardName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, maxSuggestions))
            .ToArray();
    }

    public static DeckDefinition AutoFill(
        GameData data,
        DeckDefinition deck,
        PlayerProfile? profile,
        GameRulesConfig rules,
        DeckAssistantGoal goal)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(deck);
        rules = (rules ?? GameRulesConfig.ForPreset(GameRulesPreset.Standard)).Normalize();
        var mode = ModeForDeck(data, deck);
        var cards = deck.Cards
            .Where(entry => entry.Value > 0)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        var working = deck with { Cards = cards };

        var guard = 0;
        while (working.Count > mode.DeckRules.DeckSize && guard++ < 200)
        {
            var cut = SuggestCuts(data, working, profile, rules, goal, 1).FirstOrDefault();
            if (cut is null)
            {
                break;
            }

            RemoveOne(cards, cut.CardId);
            working = working with { Cards = new Dictionary<string, int>(cards, StringComparer.OrdinalIgnoreCase) };
        }

        // A deliberate assistant action can establish the land-like starter baseline;
        // manually assembled decks stay legal without Basic Energy because Add Energy remains.
        var dominantElement = DominantElement(data, working);
        var basicEnergyId = string.IsNullOrWhiteSpace(dominantElement) ? "" : BasicEnergy.CardId(dominantElement);
        if (mode.Id.Equals(DragonCardsModeIds.DragonDuel, StringComparison.OrdinalIgnoreCase) &&
            data.CardsById.TryGetValue(basicEnergyId, out var basicEnergy) &&
            BasicEnergy.IsBasicEnergyCard(basicEnergy))
        {
            while (cards.GetValueOrDefault(basicEnergyId) < 12 && guard++ < 300)
            {
                if (working.Count >= mode.DeckRules.DeckSize)
                {
                    var cut = SuggestCuts(data, working, profile, rules, goal, 16)
                        .FirstOrDefault(item => !item.CardId.Equals(basicEnergyId, StringComparison.OrdinalIgnoreCase));
                    if (cut is null)
                    {
                        break;
                    }

                    RemoveOne(cards, cut.CardId);
                }

                cards[basicEnergyId] = cards.GetValueOrDefault(basicEnergyId) + 1;
                working = working with { Cards = new Dictionary<string, int>(cards, StringComparer.OrdinalIgnoreCase) };
            }
        }

        guard = 0;
        while (working.Count < mode.DeckRules.DeckSize && guard++ < 300)
        {
            var add = SuggestAdds(data, working, profile, rules, goal, 1).FirstOrDefault();
            if (add is null)
            {
                break;
            }

            cards[add.CardId] = cards.GetValueOrDefault(add.CardId) + 1;
            working = working with { Cards = new Dictionary<string, int>(cards, StringComparer.OrdinalIgnoreCase) };
        }

        return deck with
        {
            Cards = cards
                .Where(entry => entry.Value > 0)
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static GameModeDefinition ModeForDeck(GameData data, DeckDefinition deck) =>
        data.GameModesById.TryGetValue(deck.ModeId, out var mode)
            ? mode
            : data.GameModesById[DragonCardsModeIds.DragonDuel];

    private static DeckRoleCounts CountRoles(GameData data, DeckDefinition deck)
    {
        var units = 0;
        var supports = 0;
        var spells = 0;
        var draw = 0;
        var removal = 0;
        var ramp = 0;
        var combat = 0;
        var totalCost = 0;
        var countedCards = 0;

        foreach (var (cardId, count) in deck.Cards)
        {
            if (count <= 0 || !data.CardsById.TryGetValue(cardId, out var card))
            {
                continue;
            }

            if (card.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase))
            {
                units += count;
            }
            else if (card.Type.Equals("Support", StringComparison.OrdinalIgnoreCase))
            {
                supports += count;
            }
            else if (card.Type.Equals("Spell", StringComparison.OrdinalIgnoreCase))
            {
                spells += count;
            }

            if (HasRole(card, "draw", "cantrip", "filter"))
            {
                draw += count;
            }

            if (HasRole(card, "damage", "return", "exhaust", "discard", "bounce", "defeat", "opponent"))
            {
                removal += count;
            }

            if (HasRole(card, "energy", "reduce", "refund", "ramp", "costs"))
            {
                ramp += count;
            }

            if (HasRole(card, "strike", "ready", "attack", "power", "damage"))
            {
                combat += count;
            }

            totalCost += card.TotalCost * count;
            countedCards += count;
        }

        return new DeckRoleCounts
        {
            Units = units,
            Supports = supports,
            Spells = spells,
            Draw = draw,
            Removal = removal,
            Ramp = ramp,
            Combat = combat,
            AverageCost = countedCards == 0 ? 0m : Math.Round(totalCost / (decimal)countedCards, 2)
        };
    }

    private static IReadOnlyList<string> BuildNotes(
        DeckDefinition deck,
        GameModeDefinition mode,
        DeckRoleCounts roles,
        IReadOnlyList<ValidationIssue> validationIssues,
        IReadOnlyList<ValidationIssue> ownershipIssues,
        DeckAssistantGoal goal)
    {
        var notes = new List<string>();
        if (deck.Count < mode.DeckRules.DeckSize)
        {
            notes.Add($"Add {mode.DeckRules.DeckSize - deck.Count} cards to reach {mode.DeckRules.DeckSize}.");
        }
        else if (deck.Count > mode.DeckRules.DeckSize)
        {
            notes.Add($"Cut {deck.Count - mode.DeckRules.DeckSize} cards to reach {mode.DeckRules.DeckSize}.");
        }
        else
        {
            notes.Add("Deck size is on target.");
        }

        if (ownershipIssues.Count > 0)
        {
            notes.Add($"{ownershipIssues.Count} owned-copy issue(s) need replacement.");
        }

        var targets = Targets(goal, mode.DeckRules.DeckSize);
        if (roles.Units < targets.MinUnits)
        {
            notes.Add("Add more Units so you can pressure and block.");
        }

        if (roles.Draw < targets.MinDraw)
        {
            notes.Add("Add more draw/filtering to keep turns flowing.");
        }

        if (roles.Ramp < targets.MinRamp)
        {
            notes.Add("Add more energy/ramp/value cards.");
        }

        if (roles.Removal < targets.MinRemoval)
        {
            notes.Add("Add more removal, bounce, exhaust, or discard effects.");
        }

        if (validationIssues.Count > 0)
        {
            notes.Add(validationIssues[0].Message);
        }

        return notes.Take(5).ToArray();
    }

    private static int AddScore(CardDefinition card, DeckRoleCounts roles, string dominantElement, DeckAssistantGoal goal, int deckSize)
    {
        var targets = Targets(goal, deckSize);
        var score = 100 + GoalScore(card, goal);
        if (BasicEnergy.IsBasicEnergyCard(card) &&
            card.Elements.Contains(dominantElement, StringComparer.OrdinalIgnoreCase))
        {
            score += 140;
        }
        if (!string.IsNullOrWhiteSpace(dominantElement) && card.Elements.Contains(dominantElement, StringComparer.OrdinalIgnoreCase))
        {
            score += 24;
        }

        if (roles.Units < targets.MinUnits && card.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        if (roles.Draw < targets.MinDraw && HasRole(card, "draw", "cantrip", "filter"))
        {
            score += 24;
        }

        if (roles.Ramp < targets.MinRamp && HasRole(card, "energy", "reduce", "refund", "ramp", "costs"))
        {
            score += 24;
        }

        if (roles.Removal < targets.MinRemoval && HasRole(card, "damage", "return", "exhaust", "discard", "bounce", "opponent"))
        {
            score += 24;
        }

        score -= Math.Abs(card.TotalCost - targets.IdealCost) * 4;
        return score;
    }

    private static int CutScore(CardDefinition card, int count, DeckRoleCounts roles, string dominantElement, DeckAssistantGoal goal, int deckSize)
    {
        var targets = Targets(goal, deckSize);
        var score = 100 - GoalScore(card, goal);
        if (count > 2)
        {
            score += 18;
        }

        if (!string.IsNullOrWhiteSpace(dominantElement) && !card.Elements.Contains(dominantElement, StringComparer.OrdinalIgnoreCase))
        {
            score += 24;
        }

        if (card.TotalCost > targets.IdealCost + 2)
        {
            score += 18;
        }

        if (roles.Units > targets.MaxUnits && card.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase))
        {
            score += 18;
        }

        return score;
    }

    private static int GoalScore(CardDefinition card, DeckAssistantGoal goal) => goal switch
    {
        DeckAssistantGoal.Aggro =>
            (card.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase) ? 25 : 0) +
            (card.TotalCost <= 3 ? 22 : -8) +
            (HasRole(card, "damage", "strike", "attack", "discard") ? 25 : 0),
        DeckAssistantGoal.Control =>
            (HasRole(card, "return", "exhaust", "discard", "bounce", "draw") ? 34 : 0) +
            (card.Type.Equals("Spell", StringComparison.OrdinalIgnoreCase) ? 12 : 0),
        DeckAssistantGoal.Ramp =>
            (HasRole(card, "energy", "reduce", "refund", "ramp", "costs") ? 42 : 0) +
            (card.Power >= 7000 ? 16 : 0),
        DeckAssistantGoal.Combo =>
            (HasRole(card, "draw", "cantrip", "ready", "refund", "reduce") ? 40 : 0) +
            (card.TotalCost <= 3 ? 14 : 0),
        _ =>
            (HasRole(card, "draw", "damage", "energy", "return", "exhaust", "ready") ? 20 : 0) +
            (card.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase) ? 10 : 0)
    };

    private static string AddReason(CardDefinition card, DeckRoleCounts roles, string dominantElement, DeckAssistantGoal goal)
    {
        if (!string.IsNullOrWhiteSpace(dominantElement) && card.Elements.Contains(dominantElement, StringComparer.OrdinalIgnoreCase))
        {
            return $"Fits your {dominantElement} core and {goal} plan.";
        }

        if (HasRole(card, "draw", "cantrip"))
        {
            return "Improves card flow.";
        }

        if (HasRole(card, "energy", "reduce", "ramp"))
        {
            return "Improves energy and tempo.";
        }

        if (HasRole(card, "damage", "return", "exhaust", "discard"))
        {
            return "Adds interaction.";
        }

        return $"Useful {card.Type} for {goal} decks.";
    }

    private static string CutReason(CardDefinition card, int count, string dominantElement, DeckAssistantGoal goal)
    {
        if (count > 2)
        {
            return "High copy count; cutting one keeps variety.";
        }

        if (!string.IsNullOrWhiteSpace(dominantElement) && !card.Elements.Contains(dominantElement, StringComparer.OrdinalIgnoreCase))
        {
            return $"Off-plan for your {dominantElement} core.";
        }

        if (card.TotalCost >= 6)
        {
            return "High cost; trim if the deck feels slow.";
        }

        return $"Lower priority for {goal}.";
    }

    private static int AvailableCopies(CardDefinition card, int current, PlayerProfile? profile, GameRulesConfig rules, GameModeDefinition mode)
    {
        if (BasicEnergy.IsBasicEnergyCard(card))
        {
            return Math.Max(0, mode.DeckRules.DeckSize - current);
        }

        var copyLimit = mode.DeckRules.MaxCopies;
        if (rules.AllUnlocks || rules.UnlimitedDeckBuilder || !rules.EnforceDeckOwnership || profile is null)
        {
            return Math.Max(0, copyLimit - current);
        }

        return Math.Max(0, Math.Min(copyLimit, PlayerCollection.CountOwned(profile, card.Id)) - current);
    }

    private static string DominantElement(GameData data, DeckDefinition deck)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cardId, count) in deck.Cards)
        {
            if (count <= 0 || !data.CardsById.TryGetValue(cardId, out var card))
            {
                continue;
            }

            foreach (var element in card.Elements)
            {
                counts[element] = counts.GetValueOrDefault(element) + count;
            }
        }

        return counts
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Key)
            .FirstOrDefault() ?? "";
    }

    private static bool HasRole(CardDefinition card, params string[] needles)
    {
        var haystack = string.Join(' ',
            card.RulesText,
            string.Join(' ', card.Hooks),
            string.Join(' ', card.Tags),
            string.Join(' ', card.Keywords),
            string.Join(' ', card.Abilities.Select(ability => $"{ability.Name} {ability.Hook} {ability.RulesText}")));
        return needles.Any(needle => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static RoleTargets Targets(DeckAssistantGoal goal, int deckSize)
    {
        var scale = deckSize / 50m;
        return (goal switch
        {
            DeckAssistantGoal.Aggro => new RoleTargets(28, 38, 5, 4, 2, 2),
            DeckAssistantGoal.Control => new RoleTargets(18, 30, 8, 8, 1, 4),
            DeckAssistantGoal.Ramp => new RoleTargets(22, 34, 6, 4, 7, 4),
            DeckAssistantGoal.Combo => new RoleTargets(18, 30, 9, 3, 4, 3),
            _ => new RoleTargets(22, 34, 6, 5, 4, 3)
        }).Scale(scale);
    }

    private static void RemoveOne(Dictionary<string, int> cards, string cardId)
    {
        var count = cards.GetValueOrDefault(cardId);
        if (count <= 1)
        {
            cards.Remove(cardId);
            return;
        }

        cards[cardId] = count - 1;
    }

    private readonly record struct RoleTargets(int MinUnits, int MaxUnits, int MinDraw, int MinRemoval, int MinRamp, int IdealCost)
    {
        public RoleTargets Scale(decimal amount) => new(
            Math.Max(1, (int)Math.Round(MinUnits * amount)),
            Math.Max(1, (int)Math.Round(MaxUnits * amount)),
            Math.Max(1, (int)Math.Round(MinDraw * amount)),
            Math.Max(1, (int)Math.Round(MinRemoval * amount)),
            Math.Max(0, (int)Math.Round(MinRamp * amount)),
            IdealCost);
    }
}
