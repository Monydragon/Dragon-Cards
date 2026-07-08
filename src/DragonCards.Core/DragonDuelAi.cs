namespace DragonCards.Core;

public sealed class DragonDuelAi
{
    private const int DefaultMaxActions = 30;

    public AiTurnResult RunUntilHumanInput(DragonDuelEngine engine, int aiPlayerIndex, int maxActions = DefaultMaxActions)
    {
        var decisions = new List<AiDecision>();
        var actions = 0;
        var handSacrificedTurnKey = "";

        while (actions < maxActions && engine.State.WinnerIndex is null)
        {
            var turnKey = $"{engine.State.ActivePlayerIndex}:{engine.State.TurnNumber}";

            if (engine.State.PendingEnergyChoice is not null)
            {
                if (engine.State.PendingEnergyChoice.PlayerIndex != aiPlayerIndex)
                {
                    return new AiTurnResult(AiTurnStatus.WaitingForHuman, decisions);
                }

                var element = ChooseEnergyElement(engine, aiPlayerIndex);
                var result = engine.ResolveEnergyChoice(element);
                decisions.Add(new AiDecision("energy-choice", result.Message, result.Events));
                actions++;
                continue;
            }

            if (engine.State.PendingTargetChoice is not null)
            {
                if (engine.State.PendingTargetChoice.PlayerIndex != aiPlayerIndex)
                {
                    return new AiTurnResult(AiTurnStatus.WaitingForHuman, decisions);
                }

                var targetResult = ResolveAiTarget(engine, aiPlayerIndex);
                decisions.Add(new AiDecision("target-choice", targetResult.Message, targetResult.Events));
                actions++;
                continue;
            }

            if (engine.State.PendingAttack is not null)
            {
                if (engine.State.PendingAttack.AttackerPlayerIndex == aiPlayerIndex)
                {
                    return new AiTurnResult(AiTurnStatus.WaitingForHumanBlock, decisions);
                }

                var blockResult = ResolveAiBlock(engine, aiPlayerIndex);
                decisions.Add(new AiDecision("block", blockResult.Message, blockResult.Events));
                actions++;
                continue;
            }

            if (engine.State.ActivePlayerIndex != aiPlayerIndex)
            {
                return new AiTurnResult(AiTurnStatus.WaitingForHuman, decisions);
            }

            if (engine.State.CurrentPhase.Equals("Ready", StringComparison.OrdinalIgnoreCase) ||
                engine.State.CurrentPhase.Equals("Draw", StringComparison.OrdinalIgnoreCase) ||
                engine.State.CurrentPhase.Equals("End", StringComparison.OrdinalIgnoreCase))
            {
                var result = engine.AdvanceToNextDecisionPhase();
                decisions.Add(new AiDecision("phase", result.Message, result.Events));
                actions++;
                continue;
            }

            if (engine.IsMainPhase())
            {
                if (engine.CanAddEnergy())
                {
                    var element = ChooseEnergyElement(engine, aiPlayerIndex);
                    var result = engine.AddEnergy(element);
                    decisions.Add(new AiDecision("add-energy", result.Message, result.Events));
                    actions++;
                    continue;
                }

                var ability = ChooseAbility(engine, aiPlayerIndex);
                if (ability is not null)
                {
                    var result = engine.ActivateAbility(aiPlayerIndex, ability.SourceInstanceId, ability.Ability.Id);
                    decisions.Add(new AiDecision("ability", result.Message, result.Events));
                    actions++;
                    continue;
                }

                var handIndex = ChoosePlayableCard(engine);
                if (handIndex is not null)
                {
                    var result = engine.PlayCardFromHand(handIndex.Value);
                    decisions.Add(new AiDecision("play-card", result.Message, result.Events));
                    actions++;
                    continue;
                }

                var fullZoneSacrifice = ChooseFullZoneSacrifice(engine);
                if (fullZoneSacrifice is not null)
                {
                    var result = engine.SacrificeForEnergy(fullZoneSacrifice.Value.Source, fullZoneSacrifice.Value.Index);
                    decisions.Add(new AiDecision("sacrifice", result.Message, result.Events));
                    actions++;
                    continue;
                }

                if (!turnKey.Equals(handSacrificedTurnKey, StringComparison.OrdinalIgnoreCase))
                {
                    var handSacrificeIndex = ChooseHandSacrifice(engine);
                    if (handSacrificeIndex is not null)
                    {
                        var result = engine.SacrificeForEnergy(SacrificeSource.Hand, handSacrificeIndex.Value);
                        decisions.Add(new AiDecision("sacrifice", result.Message, result.Events));
                        handSacrificedTurnKey = turnKey;
                        actions++;
                        continue;
                    }
                }

                var phaseResult = engine.State.CurrentPhase.Equals("Second Main", StringComparison.OrdinalIgnoreCase)
                    ? AdvancePastEnd(engine)
                    : engine.AdvancePhase();
                decisions.Add(new AiDecision("phase", phaseResult.Message, phaseResult.Events));
                actions++;
                continue;
            }

            if (engine.State.CurrentPhase.Equals("Combat", StringComparison.OrdinalIgnoreCase))
            {
                var attackerIndex = ChooseAttacker(engine);
                if (attackerIndex is not null)
                {
                    var attackResult = engine.DeclareAttack(attackerIndex.Value);
                    decisions.Add(new AiDecision("attack", attackResult.Message, attackResult.Events));
                    return new AiTurnResult(AiTurnStatus.WaitingForHumanBlock, decisions);
                }

                var combatPhaseResult = engine.AdvancePhase();
                decisions.Add(new AiDecision("phase", combatPhaseResult.Message, combatPhaseResult.Events));
                actions++;
                continue;
            }

            var fallback = engine.AdvancePhase();
            decisions.Add(new AiDecision("phase", fallback.Message, fallback.Events));
            actions++;
        }

        return new AiTurnResult(actions >= maxActions ? AiTurnStatus.ActionLimitReached : AiTurnStatus.Completed, decisions);
    }

    private static GameActionResult AdvancePastEnd(DragonDuelEngine engine)
    {
        var end = engine.AdvancePhase();
        return end.Success ? engine.AdvanceToNextDecisionPhase() : end;
    }

    private static int? ChoosePlayableCard(DragonDuelEngine engine)
    {
        return engine.GetPlayableHandIndices()
            .Select(index => (Index: index, Card: engine.State.DefinitionFor(engine.State.ActivePlayer.Hand[index])))
            .OrderByDescending(item => PlayPriority(item.Card))
            .ThenBy(item => item.Card.TotalCost)
            .ThenBy(item => item.Index)
            .Select(item => (int?)item.Index)
            .FirstOrDefault();
    }

    private static (SacrificeSource Source, int Index)? ChooseFullZoneSacrifice(DragonDuelEngine engine)
    {
        var player = engine.State.ActivePlayer;
        var candidates = player.Hand
            .Select((instance, index) => (Instance: instance, Index: index, Card: engine.State.DefinitionFor(instance)))
            .Where(item => item.Card.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase) ||
                item.Card.Type.Equals("Support", StringComparison.OrdinalIgnoreCase))
            .Where(item => IsZoneFull(engine, item.Card.Type))
            .OrderByDescending(item => PlayPriority(item.Card))
            .ThenByDescending(item => item.Card.Power)
            .ToArray();

        foreach (var candidate in candidates)
        {
            var source = candidate.Card.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase)
                ? SacrificeSource.UnitField
                : SacrificeSource.SupportField;
            var sacrificeIndex = ChooseWeakestFieldCard(engine, source, candidate.Card);
            if (sacrificeIndex is null || !engine.CanSacrificeForEnergy(source, sacrificeIndex.Value))
            {
                continue;
            }

            var sacrificeCard = FieldFor(player, source)[sacrificeIndex.Value];
            var preview = engine.GetSacrificeEnergyPreview(engine.State.DefinitionFor(sacrificeCard));
            if (CanPayWithBonus(engine, candidate.Card.Cost, preview.Element, preview.Amount))
            {
                return (source, sacrificeIndex.Value);
            }
        }

        return null;
    }

    private static int? ChooseHandSacrifice(DragonDuelEngine engine)
    {
        var player = engine.State.ActivePlayer;
        var unmetElement = ChooseEnergyElement(engine, engine.State.ActivePlayerIndex);
        var sacrifices = player.Hand
            .Select((instance, index) => (Instance: instance, Index: index, Card: engine.State.DefinitionFor(instance)))
            .Where(item => engine.CanSacrificeForEnergy(SacrificeSource.Hand, item.Index))
            .Select(item =>
            {
                var preview = engine.GetSacrificeEnergyPreview(item.Card);
                var unlocks = player.Hand
                    .Select((instance, index) => (Instance: instance, Index: index, Card: engine.State.DefinitionFor(instance)))
                    .Any(target => target.Index != item.Index &&
                        !IsZoneFull(engine, target.Card.Type) &&
                        CanPayWithBonus(engine, target.Card.Cost, preview.Element, preview.Amount));
                var improvesNeed = preview.Element.Equals(unmetElement, StringComparison.OrdinalIgnoreCase);
                return (item.Index, item.Card, preview.Element, Unlocks: unlocks, ImprovesNeed: improvesNeed);
            })
            .Where(item => item.Unlocks || item.ImprovesNeed)
            .OrderByDescending(item => item.Unlocks)
            .ThenByDescending(item => item.ImprovesNeed)
            .ThenBy(item => PlayPriority(item.Card))
            .ThenBy(item => item.Card.TotalCost)
            .ThenBy(item => item.Index)
            .ToArray();

        return sacrifices.Select(item => (int?)item.Index).FirstOrDefault();
    }

    private static int? ChooseWeakestFieldCard(DragonDuelEngine engine, SacrificeSource source, CardDefinition incoming)
    {
        var field = FieldFor(engine.State.ActivePlayer, source);
        var weakest = field
            .Select((instance, index) => (Instance: instance, Index: index, Card: engine.State.DefinitionFor(instance)))
            .OrderBy(item => PlayPriority(item.Card))
            .ThenBy(item => item.Card.Power)
            .ThenByDescending(item => item.Card.TotalCost)
            .ThenBy(item => item.Index)
            .FirstOrDefault();

        if (weakest.Instance is null)
        {
            return null;
        }

        var incomingScore = PlayPriority(incoming) * 100000 + incoming.Power + incoming.TotalCost;
        var weakestScore = PlayPriority(weakest.Card) * 100000 + weakest.Card.Power + weakest.Card.TotalCost;
        return incomingScore > weakestScore ? weakest.Index : null;
    }

    private static bool IsZoneFull(DragonDuelEngine engine, string type)
    {
        var player = engine.State.ActivePlayer;
        if (type.Equals("Unit", StringComparison.OrdinalIgnoreCase))
        {
            return player.UnitField.Count >= engine.State.Mode.ZoneLimits.UnitSlots;
        }

        if (type.Equals("Support", StringComparison.OrdinalIgnoreCase))
        {
            return player.SupportField.Count >= engine.State.Mode.ZoneLimits.SupportSlots;
        }

        return false;
    }

    private static List<CardInstance> FieldFor(PlayerState player, SacrificeSource source) =>
        source == SacrificeSource.UnitField ? player.UnitField : player.SupportField;

    private static bool CanPayWithBonus(DragonDuelEngine engine, IReadOnlyDictionary<string, int> cost, string bonusElement, int bonusAmount)
    {
        var player = engine.State.ActivePlayer;
        var adjusted = cost
            .Where(entry => entry.Value > 0)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        adjusted.TryAdd(DragonCardConstants.GenericCost, 0);

        var reduction = player.NextCardCostReduction;
        while (reduction > 0 && adjusted.GetValueOrDefault(DragonCardConstants.GenericCost) > 0)
        {
            adjusted[DragonCardConstants.GenericCost]--;
            reduction--;
        }

        foreach (var key in adjusted.Keys
            .Where(key => !key.Equals(DragonCardConstants.GenericCost, StringComparison.OrdinalIgnoreCase))
            .OrderBy(key => key)
            .ToArray())
        {
            while (reduction > 0 && adjusted[key] > 0)
            {
                adjusted[key]--;
                reduction--;
            }
        }

        var available = player.EnergyPool.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(bonusElement))
        {
            var max = engine.State.Mode.EnergyRules.MaxPerElement;
            available[bonusElement] = Math.Min(max, available.GetValueOrDefault(bonusElement) + Math.Max(0, bonusAmount));
        }

        foreach (var (element, amount) in adjusted.Where(entry => !entry.Key.Equals(DragonCardConstants.GenericCost, StringComparison.OrdinalIgnoreCase)))
        {
            if (available.GetValueOrDefault(element) < amount)
            {
                return false;
            }

            available[element] -= amount;
        }

        var generic = adjusted.GetValueOrDefault(DragonCardConstants.GenericCost);
        return available.Values.Where(amount => amount > 0).Sum() >= generic;
    }

    private static int PlayPriority(CardDefinition card)
    {
        if (card.Tags.Contains("Finisher", StringComparer.OrdinalIgnoreCase))
        {
            return 95;
        }

        if (IsRampCard(card) && card.Type.Equals("Support", StringComparison.OrdinalIgnoreCase))
        {
            return 90;
        }

        if (card.Tags.Contains("Removal", StringComparer.OrdinalIgnoreCase) || HasHook(card, "exhaust"))
        {
            return 82;
        }

        if (card.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase))
        {
            return 70;
        }

        if (IsRampCard(card) || HasHook(card, "draw"))
        {
            return 60;
        }

        if (HasHook(card, "deal"))
        {
            return 50;
        }

        if (card.Type.Equals("Support", StringComparison.OrdinalIgnoreCase))
        {
            return 40;
        }

        return 10;
    }

    private static ActivatableAbility? ChooseAbility(DragonDuelEngine engine, int aiPlayerIndex)
    {
        return engine.GetActivatableAbilities(aiPlayerIndex)
            .Where(item => IsHelpfulAbility(item.Ability))
            .OrderByDescending(item => AbilityPriority(item.Ability))
            .ThenBy(item => item.Card.TotalCost)
            .FirstOrDefault();
    }

    private static bool IsHelpfulAbility(ActivatedAbilityDefinition ability) =>
        ability.Hook.Contains("energy", StringComparison.OrdinalIgnoreCase) ||
        ability.Hook.Contains("reduce", StringComparison.OrdinalIgnoreCase) ||
        ability.Hook.Contains("draw", StringComparison.OrdinalIgnoreCase) ||
        ability.Hook.Contains("refund", StringComparison.OrdinalIgnoreCase) ||
        ability.Hook.Contains("convert", StringComparison.OrdinalIgnoreCase) ||
        ability.Hook.Contains("exhaust", StringComparison.OrdinalIgnoreCase) ||
        ability.Hook.Contains("ready", StringComparison.OrdinalIgnoreCase);

    private static int AbilityPriority(ActivatedAbilityDefinition ability)
    {
        if (ability.Hook.Contains("energy", StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (ability.Hook.Contains("reduce", StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        if (ability.Hook.Contains("draw", StringComparison.OrdinalIgnoreCase))
        {
            return 60;
        }

        return 40;
    }

    private static int? ChooseAttacker(DragonDuelEngine engine)
    {
        return engine.State.ActivePlayer.UnitField
            .Select((instance, index) => (Instance: instance, Index: index, Power: engine.State.DefinitionFor(instance).Power))
            .Where(item => engine.CanDeclareAttack(item.Index))
            .OrderByDescending(item => item.Power)
            .ThenBy(item => item.Index)
            .Select(item => (int?)item.Index)
            .FirstOrDefault();
    }

    private static GameActionResult ResolveAiBlock(DragonDuelEngine engine, int aiPlayerIndex)
    {
        var pending = engine.State.PendingAttack;
        if (pending is null)
        {
            return GameActionResult.Fail("No attack to block.");
        }

        var attacker = engine.State.Players[pending.AttackerPlayerIndex].UnitField
            .FirstOrDefault(card => card.Id == pending.AttackerInstanceId);
        if (attacker is null)
        {
            return engine.PassBlock();
        }

        var attackerPower = engine.State.DefinitionFor(attacker).Power;
        var defender = engine.State.Players[aiPlayerIndex];
        var blockers = defender.UnitField
            .Select((instance, index) => (Instance: instance, Index: index, Power: engine.State.DefinitionFor(instance).Power))
            .Where(item => engine.CanBlock(item.Index))
            .ToArray();

        var surviving = blockers
            .Where(item => item.Power > attackerPower)
            .OrderBy(item => item.Power)
            .ThenBy(item => item.Index)
            .FirstOrDefault();
        if (surviving.Instance is not null)
        {
            return engine.Block(surviving.Index);
        }

        var trading = blockers
            .Where(item => item.Power >= attackerPower)
            .OrderBy(item => item.Power)
            .ThenBy(item => item.Index)
            .FirstOrDefault();
        return trading.Instance is not null
            ? engine.Block(trading.Index)
            : engine.PassBlock();
    }

    private static GameActionResult ResolveAiTarget(DragonDuelEngine engine, int aiPlayerIndex)
    {
        var choice = engine.State.PendingTargetChoice;
        if (choice is null)
        {
            return GameActionResult.Fail("No target choice is pending.");
        }

        var candidates = engine.State.Players
            .SelectMany((player, playerIndex) => player.UnitField.Select((instance, index) =>
                (PlayerIndex: playerIndex, Index: index, Instance: instance, Card: engine.State.DefinitionFor(instance))))
            .Where(item => engine.CanResolveTargetChoice(item.PlayerIndex, item.Index))
            .ToArray();

        var target = choice.Type == PendingTargetChoiceType.ReadyUnit
            ? candidates
                .Where(item => item.PlayerIndex == aiPlayerIndex && item.Instance.Exhausted)
                .OrderByDescending(item => item.Card.Power)
                .ThenBy(item => item.Index)
                .FirstOrDefault()
            : candidates
                .Where(item => item.PlayerIndex != aiPlayerIndex)
                .OrderByDescending(item => item.Card.Power)
                .ThenByDescending(item => item.Card.TotalCost)
                .ThenBy(item => item.Index)
                .FirstOrDefault();

        if (target.Instance is null)
        {
            return GameActionResult.Fail("No legal target is available.");
        }

        return engine.ResolveTargetChoice(target.PlayerIndex, target.Index);
    }

    private static string ChooseEnergyElement(DragonDuelEngine engine, int playerIndex)
    {
        var player = engine.State.Players[playerIndex];
        var deficits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in player.Hand)
        {
            var card = engine.State.DefinitionFor(instance);
            foreach (var (element, cost) in card.Cost)
            {
                if (element.Equals(DragonCardConstants.GenericCost, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var missing = cost - player.EnergyPool.GetValueOrDefault(element);
                if (missing > 0)
                {
                    deficits[element] = Math.Max(deficits.GetValueOrDefault(element), missing);
                }
            }
        }

        var deficitElement = engine.State.Mode.Elements
            .Where(element => player.EnergyPool.GetValueOrDefault(element) < engine.State.Mode.EnergyRules.MaxPerElement)
            .OrderByDescending(element => deficits.GetValueOrDefault(element))
            .ThenByDescending(element => ColorCount(engine, player, element))
            .FirstOrDefault(element => deficits.GetValueOrDefault(element) > 0);
        if (deficitElement is not null)
        {
            return deficitElement;
        }

        return engine.State.Mode.Elements
            .Where(element => player.EnergyPool.GetValueOrDefault(element) < engine.State.Mode.EnergyRules.MaxPerElement)
            .OrderByDescending(element => ColorCount(engine, player, element))
            .FirstOrDefault() ?? engine.State.Mode.Elements.First();
    }

    private static int ColorCount(DragonDuelEngine engine, PlayerState player, string element) =>
        player.Hand.Concat(player.Deck).Concat(player.UnitField).Concat(player.SupportField)
            .Select(engine.State.DefinitionFor)
            .Count(card => card.Elements.Contains(element, StringComparer.OrdinalIgnoreCase));

    private static bool IsRampCard(CardDefinition card) =>
        card.Hooks.Any(IsRampHook) || card.Abilities.Any(ability => IsRampHook(ability.Hook));

    private static bool IsRampHook(string hookName) =>
        hookName.Contains("energy", StringComparison.OrdinalIgnoreCase) ||
        hookName.Contains("reduce", StringComparison.OrdinalIgnoreCase) ||
        hookName.Contains("refund", StringComparison.OrdinalIgnoreCase) ||
        hookName.Contains("convert", StringComparison.OrdinalIgnoreCase);

    private static bool HasHook(CardDefinition card, string pattern) =>
        card.Hooks.Any(hook => hook.Contains(pattern, StringComparison.OrdinalIgnoreCase)) ||
        card.Abilities.Any(ability => ability.Hook.Contains(pattern, StringComparison.OrdinalIgnoreCase));
}

public sealed record AiTurnResult(AiTurnStatus Status, IReadOnlyList<AiDecision> Decisions);

public sealed record AiDecision(string Kind, string Message, IReadOnlyList<MatchEvent> Events);

public enum AiTurnStatus
{
    Completed,
    WaitingForHuman,
    WaitingForHumanBlock,
    ActionLimitReached
}
