namespace DragonCards.Core;

public static class GameDataValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(GameData data, IEffectHookRegistry? hooks = null)
    {
        var issues = new List<ValidationIssue>();
        hooks ??= DefaultEffectHookRegistry.Create();

        issues.AddRange(ValidateUniqueIds(data.GameModes.Select(mode => mode.Id), "mode"));
        issues.AddRange(ValidateUniqueIds(data.Cards.Select(card => card.Id), "card"));
        issues.AddRange(ValidateUniqueIds(data.Decks.Select(deck => deck.Id), "deck"));

        var allElements = data.GameModes
            .SelectMany(mode => mode.Elements)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedCardTypes = data.GameModes
            .SelectMany(mode => mode.AllowedCardTypes)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var mode in data.GameModes)
        {
            ValidateMode(mode, issues);
        }

        foreach (var card in data.Cards)
        {
            ValidateCard(card, allElements, allowedCardTypes, hooks, issues);
        }

        foreach (var deck in data.Decks)
        {
            issues.AddRange(ValidateDeck(deck, data));
        }

        return issues;
    }

    public static IReadOnlyList<ValidationIssue> ValidateDeck(DeckDefinition deck, GameData data)
    {
        var issues = new List<ValidationIssue>();

        if (!data.GameModesById.TryGetValue(deck.ModeId, out var mode))
        {
            issues.Add(new ValidationIssue("deck.mode_missing", $"Deck '{deck.Name}' references missing mode '{deck.ModeId}'.", deck.Id));
            return issues;
        }

        if (deck.Count != mode.DeckRules.DeckSize)
        {
            issues.Add(new ValidationIssue("deck.size", $"Deck '{deck.Name}' has {deck.Count} cards; {mode.DeckRules.DeckSize} required.", deck.Id));
        }

        foreach (var (cardId, count) in deck.Cards)
        {
            if (count <= 0)
            {
                issues.Add(new ValidationIssue("deck.count", $"Deck '{deck.Name}' has non-positive count for '{cardId}'.", deck.Id));
                continue;
            }

            if (count > mode.DeckRules.MaxCopies)
            {
                issues.Add(new ValidationIssue("deck.max_copies", $"Deck '{deck.Name}' has {count} copies of '{cardId}'; max is {mode.DeckRules.MaxCopies}.", deck.Id));
            }

            if (!data.CardsById.TryGetValue(cardId, out var card))
            {
                issues.Add(new ValidationIssue("deck.card_missing", $"Deck '{deck.Name}' references missing card '{cardId}'.", deck.Id));
                continue;
            }

            if (!mode.AllowedCardTypes.Contains(card.Type, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new ValidationIssue("deck.card_type", $"Deck '{deck.Name}' contains card type '{card.Type}' not allowed by mode '{mode.Name}'.", deck.Id));
            }
        }

        return issues;
    }

    private static void ValidateMode(GameModeDefinition mode, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(mode.Id))
        {
            issues.Add(new ValidationIssue("mode.id", "A game mode is missing an id."));
        }

        if (mode.Elements.Count == 0)
        {
            issues.Add(new ValidationIssue("mode.elements", $"Mode '{mode.Name}' must define at least one element.", mode.Id));
        }

        if (mode.Phases.Count == 0)
        {
            issues.Add(new ValidationIssue("mode.phases", $"Mode '{mode.Name}' must define phases.", mode.Id));
        }

        if (mode.DeckRules.DeckSize <= 0)
        {
            issues.Add(new ValidationIssue("mode.deck_size", $"Mode '{mode.Name}' must use a positive deck size.", mode.Id));
        }

        if (mode.DeckRules.MaxCopies <= 0)
        {
            issues.Add(new ValidationIssue("mode.max_copies", $"Mode '{mode.Name}' must use a positive copy limit.", mode.Id));
        }

        if (mode.ZoneLimits.UnitSlots <= 0 || mode.ZoneLimits.SupportSlots <= 0)
        {
            issues.Add(new ValidationIssue("mode.zone_limits", $"Mode '{mode.Name}' must use positive unit/support limits.", mode.Id));
        }

        if (mode.EnergyRules.MaxPerElement <= 0)
        {
            issues.Add(new ValidationIssue("mode.energy_max", $"Mode '{mode.Name}' must use a positive energy cap.", mode.Id));
        }

        if (mode.EnergyRules.AddsPerTurn <= 0)
        {
            issues.Add(new ValidationIssue("mode.energy_adds", $"Mode '{mode.Name}' must allow at least one energy add per turn.", mode.Id));
        }

        ValidateElementAdvantage(mode, issues);
    }

    private static void ValidateElementAdvantage(GameModeDefinition mode, List<ValidationIssue> issues)
    {
        var advantage = mode.ElementAdvantage;
        if (advantage is null)
        {
            return;
        }

        if (advantage.PowerBonus <= 0)
        {
            issues.Add(new ValidationIssue("mode.element_advantage_bonus", $"Mode '{mode.Name}' must use a positive elemental advantage power bonus.", mode.Id));
        }

        var elements = mode.Elements.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (element, strongAgainst) in advantage.StrongAgainst)
        {
            if (!elements.Contains(element))
            {
                issues.Add(new ValidationIssue("mode.element_advantage_source", $"Mode '{mode.Name}' gives unknown element '{element}' an elemental advantage.", mode.Id));
            }

            foreach (var target in strongAgainst)
            {
                if (!elements.Contains(target))
                {
                    issues.Add(new ValidationIssue("mode.element_advantage_target", $"Mode '{mode.Name}' says '{element}' is strong against unknown element '{target}'.", mode.Id));
                }
            }
        }
    }

    private static void ValidateCard(
        CardDefinition card,
        IReadOnlySet<string> allElements,
        IReadOnlySet<string> allowedCardTypes,
        IEffectHookRegistry hooks,
        List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(card.Id))
        {
            issues.Add(new ValidationIssue("card.id", "A card is missing an id."));
        }

        if (string.IsNullOrWhiteSpace(card.Name))
        {
            issues.Add(new ValidationIssue("card.name", $"Card '{card.Id}' is missing a name.", card.Id));
        }

        if (!allowedCardTypes.Contains(card.Type))
        {
            issues.Add(new ValidationIssue("card.type", $"Card '{card.Name}' has unsupported type '{card.Type}'.", card.Id));
        }

        if (card.Elements.Count == 0)
        {
            issues.Add(new ValidationIssue("card.elements", $"Card '{card.Name}' must have at least one element.", card.Id));
        }

        foreach (var element in card.Elements)
        {
            if (!allElements.Contains(element))
            {
                issues.Add(new ValidationIssue("card.element", $"Card '{card.Name}' uses unknown element '{element}'.", card.Id));
            }
        }

        foreach (var (costElement, amount) in card.Cost)
        {
            ValidateCost(card.Name, card.Id, costElement, amount, allElements, "card", issues);
        }

        if (card.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase) && card.Power <= 0)
        {
            issues.Add(new ValidationIssue("card.power", $"Unit '{card.Name}' must have positive power.", card.Id));
        }

        foreach (var keyword in card.Keywords)
        {
            if (!DragonCardConstants.BuiltInKeywords.Contains(keyword, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new ValidationIssue("card.keyword", $"Card '{card.Name}' uses unknown keyword '{keyword}'.", card.Id));
            }
        }

        if (!CardRarities.All.Contains(card.Rarity, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue("card.rarity", $"Card '{card.Name}' uses unknown rarity '{card.Rarity}'.", card.Id));
        }

        foreach (var hook in card.Hooks)
        {
            if (!hooks.HasHook(hook))
            {
                issues.Add(new ValidationIssue("card.hook", $"Card '{card.Name}' references missing hook '{hook}'.", card.Id));
            }
        }

        if (card.Visual is not null)
        {
            if (string.IsNullOrWhiteSpace(card.Visual.Frame))
            {
                issues.Add(new ValidationIssue("card.visual_frame", $"Card '{card.Name}' has visual metadata without a frame key.", card.Id));
            }

            if (string.IsNullOrWhiteSpace(card.Visual.Effect))
            {
                issues.Add(new ValidationIssue("card.visual_effect", $"Card '{card.Name}' has visual metadata without an effect key.", card.Id));
            }
        }

        foreach (var ability in card.Abilities)
        {
            if (string.IsNullOrWhiteSpace(ability.Id))
            {
                issues.Add(new ValidationIssue("card.ability_id", $"Card '{card.Name}' has an ability without an id.", card.Id));
            }

            if (string.IsNullOrWhiteSpace(ability.Name))
            {
                issues.Add(new ValidationIssue("card.ability_name", $"Card '{card.Name}' has an ability without a name.", card.Id));
            }

            if (string.IsNullOrWhiteSpace(ability.Hook) || !hooks.HasHook(ability.Hook))
            {
                issues.Add(new ValidationIssue("card.ability_hook", $"Card '{card.Name}' ability '{ability.Name}' references missing hook '{ability.Hook}'.", card.Id));
            }

            foreach (var (costElement, amount) in ability.Cost)
            {
                ValidateCost($"{card.Name} ability {ability.Name}", card.Id, costElement, amount, allElements, "card.ability", issues);
            }

            foreach (var timing in ability.Timings)
            {
                if (!timing.Equals(DragonCardConstants.MainTiming, StringComparison.OrdinalIgnoreCase) &&
                    !timing.Equals(DragonCardConstants.CombatTiming, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new ValidationIssue("card.ability_timing", $"Card '{card.Name}' ability '{ability.Name}' uses unknown timing '{timing}'.", card.Id));
                }
            }
        }
    }

    private static void ValidateCost(
        string subjectName,
        string subjectId,
        string costElement,
        int amount,
        IReadOnlySet<string> allElements,
        string codePrefix,
        List<ValidationIssue> issues)
    {
        if (amount < 0)
        {
            issues.Add(new ValidationIssue($"{codePrefix}.cost_amount", $"{subjectName} has negative cost for '{costElement}'.", subjectId));
        }

        if (!costElement.Equals(DragonCardConstants.GenericCost, StringComparison.OrdinalIgnoreCase) &&
            !allElements.Contains(costElement))
        {
            issues.Add(new ValidationIssue($"{codePrefix}.cost_element", $"{subjectName} has unknown cost element '{costElement}'.", subjectId));
        }
    }

    private static IEnumerable<ValidationIssue> ValidateUniqueIds(IEnumerable<string> ids, string subject)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (!seen.Add(id))
            {
                yield return new ValidationIssue($"{subject}.duplicate_id", $"Duplicate {subject} id '{id}'.", id);
            }
        }
    }
}
