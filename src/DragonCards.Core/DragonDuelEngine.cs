namespace DragonCards.Core;

public sealed class DragonDuelEngine
{
    private readonly IEffectHookRegistry _hooks;

    private DragonDuelEngine(MatchState state, IEffectHookRegistry hooks)
    {
        State = state;
        _hooks = hooks;
    }

    public MatchState State { get; }

    public int GetEffectiveCombatPower(CardDefinition card, CardDefinition opponent) =>
        GetEffectiveCombatPower(State.Mode, card, opponent);

    public int GetElementAdvantageBonus(CardDefinition card, CardDefinition opponent) =>
        GetElementAdvantageBonus(State.Mode, card, opponent);

    public static int GetEffectiveCombatPower(GameModeDefinition mode, CardDefinition card, CardDefinition opponent) =>
        card.Power + GetElementAdvantageBonus(mode, card, opponent);

    public static int GetElementAdvantageBonus(GameModeDefinition mode, CardDefinition card, CardDefinition opponent) =>
        HasElementAdvantage(mode, card, opponent) ? mode.ElementAdvantage?.PowerBonus ?? 0 : 0;

    public static bool HasElementAdvantage(GameModeDefinition mode, CardDefinition card, CardDefinition opponent)
    {
        var element = GetCombatElement(mode, card);
        var opponentElement = GetCombatElement(mode, opponent);
        if (string.IsNullOrWhiteSpace(element) ||
            string.IsNullOrWhiteSpace(opponentElement) ||
            mode.ElementAdvantage is null ||
            !mode.ElementAdvantage.StrongAgainst.TryGetValue(element, out var strongAgainst))
        {
            return false;
        }

        return strongAgainst.Contains(opponentElement, StringComparer.OrdinalIgnoreCase);
    }

    public static string? GetCombatElement(GameModeDefinition mode, CardDefinition card) =>
        card.Elements.FirstOrDefault(element => mode.Elements.Contains(element, StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyList<string> GetStrongAgainstElements(GameModeDefinition mode, CardDefinition card)
    {
        var element = GetCombatElement(mode, card);
        if (string.IsNullOrWhiteSpace(element) ||
            mode.ElementAdvantage is null ||
            !mode.ElementAdvantage.StrongAgainst.TryGetValue(element, out var strongAgainst))
        {
            return [];
        }

        return strongAgainst;
    }

    public static IReadOnlyList<string> GetWeakAgainstElements(GameModeDefinition mode, CardDefinition card)
    {
        var element = GetCombatElement(mode, card);
        if (string.IsNullOrWhiteSpace(element) || mode.ElementAdvantage is null)
        {
            return [];
        }

        return mode.ElementAdvantage.StrongAgainst
            .Where(entry => entry.Value.Contains(element, StringComparer.OrdinalIgnoreCase))
            .Select(entry => entry.Key)
            .ToArray();
    }

    public static DragonDuelEngine Create(
        GameData data,
        string modeId,
        DeckDefinition firstDeck,
        DeckDefinition secondDeck,
        int? seed = null,
        bool shuffle = true,
        IEffectHookRegistry? hooks = null)
    {
        if (!data.GameModesById.TryGetValue(modeId, out var mode))
        {
            throw new ArgumentException($"Unknown game mode '{modeId}'.", nameof(modeId));
        }

        var firstIssues = GameDataValidator.ValidateDeck(firstDeck, data);
        var secondIssues = GameDataValidator.ValidateDeck(secondDeck, data);
        if (firstIssues.Count > 0 || secondIssues.Count > 0)
        {
            var joined = string.Join(Environment.NewLine, firstIssues.Concat(secondIssues).Select(issue => issue.Message));
            throw new InvalidOperationException($"Cannot start match with invalid decks:{Environment.NewLine}{joined}");
        }

        var random = seed is null ? Random.Shared : new Random(seed.Value);
        var firstPlayer = new PlayerState("Player 1", BuildDeck(firstDeck, shuffle, random, playerIndex: 0));
        var secondPlayer = new PlayerState("Player 2", BuildDeck(secondDeck, shuffle, random, playerIndex: 1));
        var state = new MatchState(mode, data.CardsById, firstPlayer, secondPlayer);
        var engine = new DragonDuelEngine(state, hooks ?? DefaultEffectHookRegistry.Create());

        engine.DrawCards(0, mode.StartingHand);
        engine.DrawCards(1, mode.StartingHand);
        engine.BeginCurrentPhase();
        state.Log.Add("Dragon Duel match started.");
        return engine;
    }

    public GameActionResult AdvancePhase()
    {
        if (State.WinnerIndex is not null)
        {
            return GameActionResult.Fail("The match is already over.");
        }

        if (State.PendingAttack is not null)
        {
            return GameActionResult.Fail("Resolve the current attack before changing phases.");
        }

        if (State.PendingCombatAction is not null)
        {
            return GameActionResult.Fail("Resolve the combat action window before changing phases.");
        }

        if (State.PendingEnergyChoice is not null)
        {
            return GameActionResult.Fail("Choose an energy element before changing phases.");
        }

        if (State.PendingTargetChoice is not null)
        {
            return GameActionResult.Fail("Choose a target before changing phases.");
        }

        var events = new List<MatchEvent>();
        if (State.CurrentPhase.Equals("End", StringComparison.OrdinalIgnoreCase))
        {
            State.ActivePlayerIndex = 1 - State.ActivePlayerIndex;
            State.PhaseIndex = 0;
            State.TurnNumber++;
            State.EnergyAddsThisTurn = 0;
            events.Add(PhaseEvent($"{State.ActivePlayer.Name}'s turn begins."));
            events.AddRange(BeginCurrentPhase());
            return GameActionResult.Ok($"{State.ActivePlayer.Name}'s turn begins.", events);
        }

        State.PhaseIndex++;
        events.Add(PhaseEvent($"Advanced to {State.CurrentPhase}."));
        events.AddRange(BeginCurrentPhase());
        return GameActionResult.Ok($"Advanced to {State.CurrentPhase}.", events);
    }

    public GameActionResult AdvanceToNextDecisionPhase()
    {
        if (State.WinnerIndex is not null)
        {
            return GameActionResult.Fail("The match is already over.");
        }

        if (State.PendingAttack is not null)
        {
            return GameActionResult.Fail("Resolve the current attack first.");
        }

        if (State.PendingCombatAction is not null)
        {
            return GameActionResult.Fail("Resolve the combat action window first.");
        }

        if (State.PendingEnergyChoice is not null)
        {
            return GameActionResult.Fail("Choose an energy element first.");
        }

        if (State.PendingCombatAction is not null)
        {
            return GameActionResult.Fail("Resolve the combat action window first.");
        }

        if (State.PendingTargetChoice is not null)
        {
            return GameActionResult.Fail("Choose a target first.");
        }

        var advanced = false;
        var events = new List<MatchEvent>();
        while (State.CurrentPhase.Equals("Ready", StringComparison.OrdinalIgnoreCase) ||
               State.CurrentPhase.Equals("Draw", StringComparison.OrdinalIgnoreCase) ||
               State.CurrentPhase.Equals("End", StringComparison.OrdinalIgnoreCase))
        {
            var result = AdvancePhase();
            if (!result.Success)
            {
                return result;
            }

            advanced = true;
            events.AddRange(result.Events);
        }

        return GameActionResult.Ok(advanced
            ? $"{State.ActivePlayer.Name}'s {State.CurrentPhase} phase."
            : $"Already in {State.CurrentPhase}.", events);
    }

    public GameActionResult AddEnergy(string element)
    {
        if (State.WinnerIndex is not null)
        {
            return GameActionResult.Fail("The match is already over.");
        }

        if (State.PendingEnergyChoice is not null)
        {
            return GameActionResult.Fail("Choose an energy element first.");
        }

        if (State.PendingTargetChoice is not null)
        {
            return GameActionResult.Fail("Choose a target first.");
        }

        if (!IsMainPhase())
        {
            return GameActionResult.Fail("Energy can only be added during a main phase.");
        }

        if (State.EnergyAddsThisTurn >= State.Mode.EnergyRules.AddsPerTurn)
        {
            return GameActionResult.Fail("Energy has already been added this turn.");
        }

        return GainEnergy(State.ActivePlayerIndex, element, 1, countsAsTurnAdd: true);
    }

    public bool CanAddEnergy(string? element = null)
    {
        if (State.WinnerIndex is not null ||
            State.PendingCombatAction is not null ||
            State.PendingEnergyChoice is not null ||
            State.PendingTargetChoice is not null ||
            !IsMainPhase() ||
            State.EnergyAddsThisTurn >= State.Mode.EnergyRules.AddsPerTurn)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(element))
        {
            return true;
        }

        return State.Mode.Elements.Contains(element, StringComparer.OrdinalIgnoreCase) &&
            State.ActivePlayer.EnergyPool.GetValueOrDefault(element) < State.Mode.EnergyRules.MaxPerElement;
    }

    public GameActionResult PlaceEnergyFromHand(int handIndex)
    {
        var player = State.ActivePlayer;
        if (!IsValidIndex(player.Hand, handIndex))
        {
            return GameActionResult.Fail("No card exists at that hand position.");
        }

        var card = State.DefinitionFor(player.Hand[handIndex]);
        var element = card.Elements.FirstOrDefault() ?? State.Mode.Elements.First();
        return AddEnergy(element);
    }

    public GameActionResult ResolveEnergyChoice(string element)
    {
        var choice = State.PendingEnergyChoice;
        if (choice is null)
        {
            return GameActionResult.Fail("There is no pending energy choice.");
        }

        if (!State.Mode.Elements.Contains(element, StringComparer.OrdinalIgnoreCase))
        {
            return GameActionResult.Fail($"'{element}' is not a valid element.");
        }

        State.PendingEnergyChoice = null;
        if (choice.Type == PendingEnergyChoiceType.Gain)
        {
            return GainEnergy(choice.PlayerIndex, element, choice.Amount, countsAsTurnAdd: false);
        }

        return ConvertOneEnergy(choice.PlayerIndex, element);
    }

    public void QueueEnergyChoice(
        int playerIndex,
        PendingEnergyChoiceType type,
        int amount,
        string message,
        CardInstance? source = null,
        string effectText = "")
    {
        State.PendingEnergyChoice = new PendingEnergyChoice(
            playerIndex,
            type,
            amount,
            message,
            source?.Id ?? "",
            source?.CardId ?? "",
            effectText);
        State.Log.Add(message);
    }

    public void QueueTargetChoice(
        int playerIndex,
        PendingTargetChoiceType type,
        TargetScope scope,
        CardInstance source,
        string message,
        TargetZoneKind zones = TargetZoneKind.Units,
        string effectText = "")
    {
        State.PendingTargetChoice = new PendingTargetChoice(playerIndex, type, scope, source.Id, message, zones, source.CardId, effectText);
        var hasLegalTarget = EnumerateTargetZones(zones).Any(CanResolveTargetChoice);
        if (!hasLegalTarget)
        {
            State.PendingTargetChoice = null;
            State.Log.Add("No legal target was available.");
            return;
        }

        State.Log.Add(message);
    }

    public bool CanResolveTargetChoice(int targetPlayerIndex, int targetFieldIndex) =>
        CanResolveTargetChoice(new ZoneRef(targetPlayerIndex, "UnitField", targetFieldIndex));

    public bool CanResolveTargetChoice(ZoneRef target)
    {
        var choice = State.PendingTargetChoice;
        if (choice is null || !TryGetFieldTarget(target, choice.Zones, out _, out _, out _))
        {
            return false;
        }

        return choice.Scope switch
        {
            TargetScope.EnemyUnit => target.Zone.Equals("UnitField", StringComparison.OrdinalIgnoreCase) && target.PlayerIndex == 1 - choice.PlayerIndex,
            TargetScope.FriendlyUnit => target.Zone.Equals("UnitField", StringComparison.OrdinalIgnoreCase) && target.PlayerIndex == choice.PlayerIndex,
            TargetScope.AnyUnit => target.Zone.Equals("UnitField", StringComparison.OrdinalIgnoreCase),
            TargetScope.EnemyField => target.PlayerIndex == 1 - choice.PlayerIndex,
            TargetScope.FriendlyField => target.PlayerIndex == choice.PlayerIndex,
            TargetScope.AnyField => true,
            _ => false
        };
    }

    public GameActionResult ResolveTargetChoice(int targetPlayerIndex, int targetFieldIndex) =>
        ResolveTargetChoice(new ZoneRef(targetPlayerIndex, "UnitField", targetFieldIndex));

    public GameActionResult ResolveTargetChoice(ZoneRef targetRef)
    {
        var choice = State.PendingTargetChoice;
        if (choice is null)
        {
            return GameActionResult.Fail("There is no pending target choice.");
        }

        if (!CanResolveTargetChoice(targetRef) ||
            !TryGetFieldTarget(targetRef, choice.Zones, out var field, out var target, out var zoneName))
        {
            return GameActionResult.Fail("That is not a legal target.");
        }

        var targetPlayerIndex = targetRef.PlayerIndex;
        var targetFieldIndex = targetRef.Index;
        var targetZone = new ZoneRef(targetPlayerIndex, zoneName, targetFieldIndex);
        var card = State.DefinitionFor(target);
        State.PendingTargetChoice = null;

        if (choice.Type == PendingTargetChoiceType.ExhaustUnit)
        {
            target.Exhausted = true;
            var message = $"{card.Name} was exhausted.";
            State.Log.Add(message);
            return GameActionResult.Ok(message, new MatchEvent
            {
                Kind = MatchEventKind.TargetResolved,
                PlayerIndex = choice.PlayerIndex,
                CardId = target.CardId,
                InstanceId = target.Id,
                To = targetZone,
                Message = message
            });
        }

        if (choice.Type == PendingTargetChoiceType.ReadyUnit)
        {
            target.Exhausted = false;
            var readyMessage = $"{card.Name} was readied.";
            State.Log.Add(readyMessage);
            return GameActionResult.Ok(readyMessage, new MatchEvent
            {
                Kind = MatchEventKind.CardReadied,
                PlayerIndex = choice.PlayerIndex,
                CardId = target.CardId,
                InstanceId = target.Id,
                To = targetZone,
                Message = readyMessage
            });
        }

        field.RemoveAt(targetFieldIndex);
        target.Exhausted = false;
        var owner = State.Players[targetPlayerIndex];
        owner.Hand.Add(target);
        var returnMessage = $"{card.Name} returned to {owner.Name}'s hand.";
        State.Log.Add(returnMessage);
        return GameActionResult.Ok(returnMessage, new MatchEvent
        {
            Kind = MatchEventKind.CardReturnedToHand,
            PlayerIndex = choice.PlayerIndex,
            CardId = target.CardId,
            InstanceId = target.Id,
            From = targetZone,
            To = new ZoneRef(targetPlayerIndex, "Hand", owner.Hand.Count - 1),
            Message = returnMessage
        });
    }

    private IEnumerable<ZoneRef> EnumerateTargetZones(TargetZoneKind zones)
    {
        for (var playerIndex = 0; playerIndex < State.Players.Count; playerIndex++)
        {
            var player = State.Players[playerIndex];
            if (zones.HasFlag(TargetZoneKind.Units))
            {
                for (var index = 0; index < player.UnitField.Count; index++)
                {
                    yield return new ZoneRef(playerIndex, "UnitField", index);
                }
            }

            if (zones.HasFlag(TargetZoneKind.Supports))
            {
                for (var index = 0; index < player.SupportField.Count; index++)
                {
                    yield return new ZoneRef(playerIndex, "SupportField", index);
                }
            }
        }
    }

    private bool TryGetFieldTarget(
        ZoneRef target,
        TargetZoneKind allowedZones,
        out List<CardInstance> field,
        out CardInstance instance,
        out string zoneName)
    {
        field = null!;
        instance = null!;
        zoneName = "";
        if (target.PlayerIndex < 0 || target.PlayerIndex >= State.Players.Count)
        {
            return false;
        }

        var player = State.Players[target.PlayerIndex];
        if (target.Zone.Equals("UnitField", StringComparison.OrdinalIgnoreCase) &&
            allowedZones.HasFlag(TargetZoneKind.Units))
        {
            field = player.UnitField;
            zoneName = "UnitField";
        }
        else if (target.Zone.Equals("SupportField", StringComparison.OrdinalIgnoreCase) &&
            allowedZones.HasFlag(TargetZoneKind.Supports))
        {
            field = player.SupportField;
            zoneName = "SupportField";
        }
        else
        {
            return false;
        }

        if (!IsValidIndex(field, target.Index))
        {
            return false;
        }

        instance = field[target.Index];
        return true;
    }

    public GameActionResult PlayCardFromHand(int handIndex)
    {
        if (State.WinnerIndex is not null)
        {
            return GameActionResult.Fail("The match is already over.");
        }

        if (State.PendingEnergyChoice is not null)
        {
            return GameActionResult.Fail("Choose an energy element first.");
        }

        if (State.PendingTargetChoice is not null)
        {
            return GameActionResult.Fail("Choose a target first.");
        }

        if (!IsMainPhase())
        {
            return GameActionResult.Fail("Cards can only be played during a main phase.");
        }

        var player = State.ActivePlayer;
        if (!IsValidIndex(player.Hand, handIndex))
        {
            return GameActionResult.Fail("No card exists at that hand position.");
        }

        var instance = player.Hand[handIndex];
        var definition = State.DefinitionFor(instance);
        var zoneIssue = ValidatePlayZone(player, definition);
        if (zoneIssue is not null)
        {
            return GameActionResult.Fail(zoneIssue);
        }

        if (!SpendCost(State.ActivePlayerIndex, definition.Cost, consumeNextCardReduction: true, out var paymentPlan))
        {
            return GameActionResult.Fail($"Not enough energy to pay {definition.Name}'s cost.");
        }

        player.Hand.RemoveAt(handIndex);
        ApplyCardResolution(player, instance, definition);
        State.Log.Add($"{player.Name} played {definition.Name}.");
        var zone = definition.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase)
            ? "UnitField"
            : definition.Type.Equals("Support", StringComparison.OrdinalIgnoreCase)
                ? "SupportField"
                : "DiscardPile";
        var toIndex = zone == "UnitField"
            ? player.UnitField.FindIndex(card => card.Id == instance.Id)
            : zone == "SupportField"
                ? player.SupportField.FindIndex(card => card.Id == instance.Id)
                : player.DiscardPile.FindIndex(card => card.Id == instance.Id);
        var events = new List<MatchEvent>();
        events.AddRange(PaymentEvents(State.ActivePlayerIndex, paymentPlan));
        events.Add(new MatchEvent
        {
            Kind = MatchEventKind.CardPlayed,
            PlayerIndex = State.ActivePlayerIndex,
            CardId = instance.CardId,
            InstanceId = instance.Id,
            From = new ZoneRef(State.ActivePlayerIndex, "Hand", handIndex),
            To = new ZoneRef(State.ActivePlayerIndex, zone, toIndex),
            Message = $"{definition.Name} played."
        });
        if (definition.Type.Equals("Spell", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new MatchEvent
            {
                Kind = MatchEventKind.CardDiscarded,
                PlayerIndex = State.ActivePlayerIndex,
                CardId = instance.CardId,
                InstanceId = instance.Id,
                To = new ZoneRef(State.ActivePlayerIndex, "DiscardPile", toIndex),
                Message = $"{definition.Name} resolved."
            });
        }

        if (State.PendingEnergyChoice is not null)
        {
            events.Add(new MatchEvent
            {
                Kind = MatchEventKind.TargetChoiceQueued,
                PlayerIndex = State.PendingEnergyChoice.PlayerIndex,
                CardId = State.PendingEnergyChoice.CardId,
                InstanceId = State.PendingEnergyChoice.SourceInstanceId,
                EffectText = State.PendingEnergyChoice.EffectText,
                Amount = State.PendingEnergyChoice.Amount,
                Message = State.PendingEnergyChoice.Message
            });
        }

        if (State.PendingTargetChoice is not null)
        {
            events.Add(new MatchEvent
            {
                Kind = MatchEventKind.TargetChoiceQueued,
                PlayerIndex = State.PendingTargetChoice.PlayerIndex,
                CardId = State.PendingTargetChoice.CardId,
                InstanceId = State.PendingTargetChoice.SourceInstanceId,
                EffectText = State.PendingTargetChoice.EffectText,
                Message = State.PendingTargetChoice.Message
            });
        }

        return GameActionResult.Ok($"{definition.Name} played.", events);
    }

    public GameActionResult ActivateAbility(int ownerIndex, string sourceInstanceId, string abilityId)
    {
        if (State.WinnerIndex is not null)
        {
            return GameActionResult.Fail("The match is already over.");
        }

        if (State.PendingEnergyChoice is not null)
        {
            return GameActionResult.Fail("Choose an energy element first.");
        }

        if (State.PendingTargetChoice is not null)
        {
            return GameActionResult.Fail("Choose a target first.");
        }

        if (ownerIndex < 0 || ownerIndex >= State.Players.Count)
        {
            return GameActionResult.Fail("That player does not exist.");
        }

        var owner = State.Players[ownerIndex];
        var unitFieldIndex = owner.UnitField.FindIndex(card => card.Id == sourceInstanceId);
        var supportFieldIndex = unitFieldIndex < 0
            ? owner.SupportField.FindIndex(card => card.Id == sourceInstanceId)
            : -1;
        var source = unitFieldIndex >= 0
            ? owner.UnitField[unitFieldIndex]
            : supportFieldIndex >= 0
                ? owner.SupportField[supportFieldIndex]
                : null;
        if (source is null)
        {
            return GameActionResult.Fail("That card is not on that player's field.");
        }

        var sourceZone = unitFieldIndex >= 0
            ? new ZoneRef(ownerIndex, "UnitField", unitFieldIndex)
            : new ZoneRef(ownerIndex, "SupportField", supportFieldIndex);

        if (source.Exhausted)
        {
            return GameActionResult.Fail($"{State.CardName(source)} is exhausted.");
        }

        var card = State.DefinitionFor(source);
        var ability = card.Abilities.FirstOrDefault(item => item.Id.Equals(abilityId, StringComparison.OrdinalIgnoreCase));
        if (ability is null)
        {
            return GameActionResult.Fail($"{card.Name} does not have that ability.");
        }

        if (!CanUseAbilityTiming(ownerIndex, ability))
        {
            return State.PendingCombatAction is not null
                ? GameActionResult.Fail("That player does not have combat action priority or that ability is not a combat action.")
                : GameActionResult.Fail("Activated abilities can only be used by the active player during a main phase.");
        }

        if (!SpendCost(ownerIndex, ability.Cost, consumeNextCardReduction: false, out var paymentPlan))
        {
            return GameActionResult.Fail($"Not enough energy to activate {ability.Name}.");
        }

        source.Exhausted = true;
        _hooks.Invoke(ability.Hook, new EffectContext(this, State, ownerIndex, source, card, ability));
        State.Log.Add($"{owner.Name} activated {card.Name}: {ability.Name}.");
        var events = new List<MatchEvent>();
        events.AddRange(PaymentEvents(ownerIndex, paymentPlan));
        events.Add(new MatchEvent
        {
            Kind = MatchEventKind.AbilityActivated,
            PlayerIndex = ownerIndex,
            CardId = source.CardId,
            InstanceId = source.Id,
            AbilityName = ability.Name,
            EffectText = ability.RulesText,
            From = sourceZone,
            To = sourceZone,
            Message = $"{ability.Name} activated."
        });
        if (State.PendingCombatAction is not null)
        {
            AdvanceCombatActionPriorityAfterAction(ownerIndex, events);
        }

        if (State.PendingEnergyChoice is not null)
        {
            events.Add(new MatchEvent
            {
                Kind = MatchEventKind.TargetChoiceQueued,
                PlayerIndex = State.PendingEnergyChoice.PlayerIndex,
                CardId = State.PendingEnergyChoice.CardId,
                InstanceId = State.PendingEnergyChoice.SourceInstanceId,
                EffectText = State.PendingEnergyChoice.EffectText,
                Amount = State.PendingEnergyChoice.Amount,
                Message = State.PendingEnergyChoice.Message
            });
        }

        if (State.PendingTargetChoice is not null)
        {
            events.Add(new MatchEvent
            {
                Kind = MatchEventKind.TargetChoiceQueued,
                PlayerIndex = State.PendingTargetChoice.PlayerIndex,
                CardId = State.PendingTargetChoice.CardId,
                InstanceId = State.PendingTargetChoice.SourceInstanceId,
                EffectText = State.PendingTargetChoice.EffectText,
                Message = State.PendingTargetChoice.Message
            });
        }

        return GameActionResult.Ok($"{ability.Name} activated.", events);
    }

    public bool CanPlayCardFromHand(int handIndex)
    {
        if (!IsMainPhase() ||
            State.PendingCombatAction is not null ||
            State.PendingEnergyChoice is not null ||
            State.PendingTargetChoice is not null)
        {
            return false;
        }

        var player = State.ActivePlayer;
        if (!IsValidIndex(player.Hand, handIndex))
        {
            return false;
        }

        var card = State.DefinitionFor(player.Hand[handIndex]);
        return ValidatePlayZone(player, card) is null &&
            CreatePaymentPlan(State.ActivePlayerIndex, card.Cost, includeNextCardReduction: true) is not null;
    }

    public bool CanActivateAbility(int ownerIndex, string sourceInstanceId, string abilityId)
    {
        if (State.WinnerIndex is not null ||
            State.PendingEnergyChoice is not null ||
            State.PendingTargetChoice is not null ||
            ownerIndex < 0 ||
            ownerIndex >= State.Players.Count)
        {
            return false;
        }

        var owner = State.Players[ownerIndex];
        var source = owner.UnitField.Concat(owner.SupportField).FirstOrDefault(card => card.Id == sourceInstanceId);
        if (source is null)
        {
            return false;
        }

        if (source.Exhausted)
        {
            return false;
        }

        var ability = State.DefinitionFor(source).Abilities.FirstOrDefault(item => item.Id.Equals(abilityId, StringComparison.OrdinalIgnoreCase));
        return ability is not null &&
            CanUseAbilityTiming(ownerIndex, ability) &&
            CreatePaymentPlan(ownerIndex, ability.Cost, includeNextCardReduction: false) is not null;
    }

    public IReadOnlyList<int> GetPlayableHandIndices()
    {
        var indices = new List<int>();
        for (var i = 0; i < State.ActivePlayer.Hand.Count; i++)
        {
            if (CanPlayCardFromHand(i))
            {
                indices.Add(i);
            }
        }

        return indices;
    }

    public IReadOnlyList<ActivatableAbility> GetActivatableAbilities(int ownerIndex)
    {
        var abilities = new List<ActivatableAbility>();
        if (ownerIndex < 0 || ownerIndex >= State.Players.Count)
        {
            return abilities;
        }

        var owner = State.Players[ownerIndex];
        foreach (var source in owner.UnitField.Concat(owner.SupportField))
        {
            var card = State.DefinitionFor(source);
            foreach (var ability in card.Abilities)
            {
                if (CanActivateAbility(ownerIndex, source.Id, ability.Id))
                {
                    abilities.Add(new ActivatableAbility(ownerIndex, source.Id, card, ability));
                }
            }
        }

        return abilities;
    }

    private bool CanUseAbilityTiming(int ownerIndex, ActivatedAbilityDefinition ability)
    {
        if (State.PendingCombatAction is not null)
        {
            return State.CurrentPhase.Equals("Combat", StringComparison.OrdinalIgnoreCase) &&
                State.PendingCombatAction.PriorityPlayerIndex == ownerIndex &&
                AbilityHasTiming(ability, DragonCardConstants.CombatTiming);
        }

        return ownerIndex == State.ActivePlayerIndex &&
            IsMainPhase() &&
            AbilityHasTiming(ability, DragonCardConstants.MainTiming);
    }

    private static bool AbilityHasTiming(ActivatedAbilityDefinition ability, string timing)
    {
        if (ability.Timings.Count == 0)
        {
            return timing.Equals(DragonCardConstants.MainTiming, StringComparison.OrdinalIgnoreCase);
        }

        return ability.Timings.Contains(timing, StringComparer.OrdinalIgnoreCase);
    }

    public bool CanSacrificeForEnergy(SacrificeSource source, int index)
    {
        if (State.WinnerIndex is not null ||
            State.PendingAttack is not null ||
            State.PendingCombatAction is not null ||
            State.PendingEnergyChoice is not null ||
            State.PendingTargetChoice is not null ||
            !IsMainPhase())
        {
            return false;
        }

        if (!TryGetSacrificeCard(source, index, out var instance, out var definition))
        {
            return false;
        }

        var preview = GetSacrificeEnergyPreview(definition);
        return !string.IsNullOrWhiteSpace(preview.Element) &&
            State.ActivePlayer.EnergyPool.GetValueOrDefault(preview.Element) < State.Mode.EnergyRules.MaxPerElement;
    }

    public GameActionResult SacrificeForEnergy(SacrificeSource source, int index)
    {
        if (State.WinnerIndex is not null)
        {
            return GameActionResult.Fail("The match is already over.");
        }

        if (State.PendingAttack is not null)
        {
            return GameActionResult.Fail("Resolve the current attack before sacrificing.");
        }

        if (State.PendingEnergyChoice is not null)
        {
            return GameActionResult.Fail("Choose an energy element before sacrificing.");
        }

        if (State.PendingTargetChoice is not null)
        {
            return GameActionResult.Fail("Choose a target before sacrificing.");
        }

        if (!IsMainPhase())
        {
            return GameActionResult.Fail("Cards can only be sacrificed during a main phase.");
        }

        if (!TryGetSacrificeCard(source, index, out var instance, out var definition))
        {
            return GameActionResult.Fail("No card exists at that sacrifice position.");
        }

        var preview = GetSacrificeEnergyPreview(definition);
        if (State.ActivePlayer.EnergyPool.GetValueOrDefault(preview.Element) >= State.Mode.EnergyRules.MaxPerElement)
        {
            return GameActionResult.Fail($"{preview.Element} energy is already maxed at {State.Mode.EnergyRules.MaxPerElement}.");
        }

        RemoveSacrificeCard(source, instance);
        instance.Exhausted = false;
        State.ActivePlayer.DiscardPile.Add(instance);
        var result = GainEnergy(State.ActivePlayerIndex, preview.Element, preview.Amount, countsAsTurnAdd: false);
        State.Log.Add($"{State.ActivePlayer.Name} sacrificed {definition.Name} for {preview.Amount} {preview.Element} energy.");
        var events = new List<MatchEvent>
        {
            new()
            {
                Kind = MatchEventKind.CardSacrificed,
                PlayerIndex = State.ActivePlayerIndex,
                CardId = instance.CardId,
                InstanceId = instance.Id,
                From = new ZoneRef(State.ActivePlayerIndex, source.ToString(), index),
                To = new ZoneRef(State.ActivePlayerIndex, "DiscardPile", State.ActivePlayer.DiscardPile.Count - 1),
                Element = preview.Element,
                Amount = preview.Amount,
                Message = $"{definition.Name} sacrificed."
            }
        };
        events.AddRange(result.Events);
        return result.Success
            ? GameActionResult.Ok($"{definition.Name} sacrificed for +{preview.Amount} {preview.Element}.", events)
            : result;
    }

    public (string Element, int Amount) GetSacrificeEnergyPreview(CardDefinition card)
    {
        var element = card.Elements.FirstOrDefault(item => State.Mode.Elements.Contains(item, StringComparer.OrdinalIgnoreCase))
            ?? State.Mode.Elements.FirstOrDefault()
            ?? "";
        var totalCost = Math.Max(0, card.Cost.Values.Where(amount => amount > 0).Sum());
        var amount = Math.Max(1, (totalCost + 1) / 2);
        return (element, amount);
    }

    public EnergyPaymentPlan? CreatePaymentPlan(int playerIndex, IReadOnlyDictionary<string, int> cost, bool includeNextCardReduction)
    {
        var player = State.Players[playerIndex];
        var adjusted = NormalizeCost(cost);
        var reductionApplied = 0;

        if (includeNextCardReduction && player.NextCardCostReduction > 0)
        {
            reductionApplied = ApplyCostReduction(adjusted, player.NextCardCostReduction);
        }

        var available = player.EnergyPool.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        var spent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (element, amount) in adjusted.Where(entry => !entry.Key.Equals(DragonCardConstants.GenericCost, StringComparison.OrdinalIgnoreCase)))
        {
            if (amount <= 0)
            {
                continue;
            }

            if (!available.TryGetValue(element, out var count) || count < amount)
            {
                return null;
            }

            available[element] = count - amount;
            spent[element] = spent.GetValueOrDefault(element) + amount;
        }

        var generic = adjusted.GetValueOrDefault(DragonCardConstants.GenericCost);
        for (var i = 0; i < generic; i++)
        {
            var element = State.Mode.Elements
                .Where(available.ContainsKey)
                .OrderByDescending(item => available[item])
                .FirstOrDefault(item => available[item] > 0);
            if (element is null)
            {
                return null;
            }

            available[element]--;
            spent[element] = spent.GetValueOrDefault(element) + 1;
        }

        return new EnergyPaymentPlan(adjusted, spent, reductionApplied);
    }

    public IReadOnlyList<MatchEvent> DrawCards(int playerIndex, int count)
    {
        var events = new List<MatchEvent>();
        var player = State.Players[playerIndex];
        for (var i = 0; i < count; i++)
        {
            if (player.Deck.Count == 0)
            {
                State.WinnerIndex = 1 - playerIndex;
                State.Log.Add($"{player.Name} tried to draw from an empty deck and lost.");
                return events;
            }

            var card = player.Deck[0];
            player.Deck.RemoveAt(0);
            card.Exhausted = false;
            player.Hand.Add(card);
            events.Add(new MatchEvent
            {
                Kind = MatchEventKind.CardDrawn,
                PlayerIndex = playerIndex,
                CardId = card.CardId,
                InstanceId = card.Id,
                From = new ZoneRef(playerIndex, "Deck"),
                To = new ZoneRef(playerIndex, "Hand", player.Hand.Count - 1),
                Message = $"{player.Name} drew a card."
            });
        }

        return events;
    }

    public IReadOnlyList<MatchEvent> DiscardCardsFromHand(int playerIndex, int count)
    {
        var events = new List<MatchEvent>();
        var player = State.Players[playerIndex];
        for (var i = 0; i < count && player.Hand.Count > 0; i++)
        {
            var handIndex = player.Hand.Count - 1;
            var discarded = player.Hand[handIndex];
            player.Hand.RemoveAt(handIndex);
            discarded.Exhausted = false;
            player.DiscardPile.Add(discarded);
            var message = $"{player.Name} discarded {State.CardName(discarded)}.";
            State.Log.Add(message);
            events.Add(new MatchEvent
            {
                Kind = MatchEventKind.CardDiscarded,
                PlayerIndex = playerIndex,
                CardId = discarded.CardId,
                InstanceId = discarded.Id,
                From = new ZoneRef(playerIndex, "Hand", handIndex),
                To = new ZoneRef(playerIndex, "DiscardPile", player.DiscardPile.Count - 1),
                Message = message
            });
        }

        return events;
    }

    public IReadOnlyList<MatchEvent> DealDamageToOpponent(int ownerIndex, int amount)
    {
        var events = new List<MatchEvent>();
        var defenderIndex = 1 - ownerIndex;
        var defender = State.Players[defenderIndex];

        for (var i = 0; i < amount; i++)
        {
            if (defender.Deck.Count == 0)
            {
                State.WinnerIndex = ownerIndex;
                State.Log.Add($"{defender.Name} had no cards left for damage.");
                return events;
            }

            var damageCard = defender.Deck[0];
            defender.Deck.RemoveAt(0);
            damageCard.Exhausted = false;
            defender.DamageZone.Add(damageCard);
            State.Log.Add($"{defender.Name} took 1 damage.");
            events.Add(new MatchEvent
            {
                Kind = MatchEventKind.DamageTaken,
                PlayerIndex = defenderIndex,
                CardId = damageCard.CardId,
                InstanceId = damageCard.Id,
                From = new ZoneRef(defenderIndex, "Deck"),
                To = new ZoneRef(defenderIndex, "DamageZone", defender.DamageZone.Count - 1),
                Amount = 1,
                Message = $"{defender.Name} took 1 damage."
            });

            if (defender.DamageZone.Count >= State.Mode.DamageLimit)
            {
                State.WinnerIndex = ownerIndex;
                State.Log.Add($"{State.Players[ownerIndex].Name} wins by damage.");
                return events;
            }
        }

        return events;
    }

    public GameActionResult GainEnergy(int playerIndex, string element, int amount, bool countsAsTurnAdd = false)
    {
        if (!State.Mode.Elements.Contains(element, StringComparer.OrdinalIgnoreCase))
        {
            return GameActionResult.Fail($"'{element}' is not a valid element.");
        }

        var player = State.Players[playerIndex];
        var current = player.EnergyPool.GetValueOrDefault(element);
        var max = State.Mode.EnergyRules.MaxPerElement;
        if (current >= max)
        {
            return GameActionResult.Fail($"{element} energy is already maxed at {max}.");
        }

        var gained = Math.Min(amount, max - current);
        player.EnergyPool[element] = current + gained;
        if (countsAsTurnAdd)
        {
            State.EnergyAddsThisTurn++;
        }

        State.Log.Add($"{player.Name} gained {gained} {element} energy.");
        return GameActionResult.Ok($"{element} energy +{gained}.", new MatchEvent
        {
            Kind = MatchEventKind.EnergyGained,
            PlayerIndex = playerIndex,
            Element = element,
            Amount = gained,
            To = new ZoneRef(playerIndex, "EnergyPool"),
            Message = $"{element} energy +{gained}."
        });
    }

    public void ReduceNextCardCost(int playerIndex, int amount)
    {
        var player = State.Players[playerIndex];
        player.NextCardCostReduction += amount;
        State.Log.Add($"{player.Name}'s next card costs {amount} less.");
    }

    public int RefundLastPayment(int playerIndex, int amount)
    {
        var player = State.Players[playerIndex];
        var refunded = 0;
        foreach (var element in State.Mode.Elements)
        {
            while (refunded < amount &&
                player.LastPayment.GetValueOrDefault(element) > 0 &&
                player.EnergyPool.GetValueOrDefault(element) < State.Mode.EnergyRules.MaxPerElement)
            {
                player.LastPayment[element]--;
                player.EnergyPool[element] = player.EnergyPool.GetValueOrDefault(element) + 1;
                refunded++;
            }
        }

        if (refunded > 0)
        {
            State.Log.Add($"{player.Name} refunded {refunded} energy.");
        }

        return refunded;
    }

    public GameActionResult DeclareAttack(int attackerFieldIndex)
    {
        if (State.WinnerIndex is not null)
        {
            return GameActionResult.Fail("The match is already over.");
        }

        if (State.PendingEnergyChoice is not null)
        {
            return GameActionResult.Fail("Choose an energy element first.");
        }

        if (State.PendingTargetChoice is not null)
        {
            return GameActionResult.Fail("Choose a target first.");
        }

        if (State.PendingCombatAction is not null)
        {
            return GameActionResult.Fail("Resolve the combat action window first.");
        }

        if (!State.CurrentPhase.Equals("Combat", StringComparison.OrdinalIgnoreCase))
        {
            return GameActionResult.Fail("Units can only attack during combat.");
        }

        if (State.PendingAttack is not null)
        {
            return GameActionResult.Fail("An attack is already pending.");
        }

        var attacker = State.ActivePlayer;
        if (!IsValidIndex(attacker.UnitField, attackerFieldIndex))
        {
            return GameActionResult.Fail("No unit exists at that field position.");
        }

        var unit = attacker.UnitField[attackerFieldIndex];
        if (unit.Exhausted)
        {
            return GameActionResult.Fail($"{State.CardName(unit)} is exhausted.");
        }

        unit.Exhausted = true;
        State.PendingAttack = new PendingAttack(State.ActivePlayerIndex, unit.Id);
        State.Log.Add($"{attacker.Name} attacked with {State.CardName(unit)}.");
        return GameActionResult.Ok("Attack declared.", new MatchEvent
        {
            Kind = MatchEventKind.AttackDeclared,
            PlayerIndex = State.ActivePlayerIndex,
            CardId = unit.CardId,
            InstanceId = unit.Id,
            From = new ZoneRef(State.ActivePlayerIndex, "UnitField", attackerFieldIndex),
            Message = $"{attacker.Name} attacked with {State.CardName(unit)}."
        });
    }

    public bool CanDeclareAttack(int attackerFieldIndex)
    {
        if (State.WinnerIndex is not null ||
            State.PendingCombatAction is not null ||
            State.PendingEnergyChoice is not null ||
            State.PendingTargetChoice is not null ||
            !State.CurrentPhase.Equals("Combat", StringComparison.OrdinalIgnoreCase) ||
            State.PendingAttack is not null ||
            !IsValidIndex(State.ActivePlayer.UnitField, attackerFieldIndex))
        {
            return false;
        }

        return !State.ActivePlayer.UnitField[attackerFieldIndex].Exhausted;
    }

    public GameActionResult Block(int blockerFieldIndex)
    {
        if (State.PendingAttack is null)
        {
            return GameActionResult.Fail("There is no pending attack to block.");
        }

        if (State.PendingCombatAction is not null)
        {
            return GameActionResult.Fail("Resolve the combat action window first.");
        }

        var defender = State.Players[1 - State.PendingAttack.AttackerPlayerIndex];
        if (!IsValidIndex(defender.UnitField, blockerFieldIndex))
        {
            return GameActionResult.Fail("No unit exists at that field position.");
        }

        var blocker = defender.UnitField[blockerFieldIndex];
        if (blocker.Exhausted)
        {
            return GameActionResult.Fail($"{State.CardName(blocker)} is exhausted.");
        }

        blocker.Exhausted = true;
        var events = new List<MatchEvent>
        {
            new()
            {
                Kind = MatchEventKind.BlockDeclared,
                PlayerIndex = 1 - State.PendingAttack.AttackerPlayerIndex,
                CardId = blocker.CardId,
                InstanceId = blocker.Id,
                From = new ZoneRef(1 - State.PendingAttack.AttackerPlayerIndex, "UnitField", blockerFieldIndex),
                Message = $"{State.CardName(blocker)} blocked."
            }
        };
        return OpenCombatActionWindow(blocker.Id, events, $"{State.CardName(blocker)} blocked.");
    }

    public bool CanBlock(int blockerFieldIndex)
    {
        if (State.PendingAttack is null || State.PendingCombatAction is not null)
        {
            return false;
        }

        var defender = State.Players[1 - State.PendingAttack.AttackerPlayerIndex];
        return IsValidIndex(defender.UnitField, blockerFieldIndex) && !defender.UnitField[blockerFieldIndex].Exhausted;
    }

    public GameActionResult PassBlock()
    {
        if (State.PendingAttack is null)
        {
            return GameActionResult.Fail("There is no pending attack.");
        }

        if (State.PendingCombatAction is not null)
        {
            return GameActionResult.Fail("Resolve the combat action window first.");
        }

        return OpenCombatActionWindow("", [], "No blocker declared.");
    }

    public bool CanPassCombatAction(int playerIndex) =>
        State.WinnerIndex is null &&
        State.PendingCombatAction is not null &&
        State.PendingEnergyChoice is null &&
        State.PendingTargetChoice is null &&
        State.PendingCombatAction.PriorityPlayerIndex == playerIndex;

    public GameActionResult PassCombatAction(int playerIndex)
    {
        var action = State.PendingCombatAction;
        if (action is null)
        {
            return GameActionResult.Fail("There is no combat action window.");
        }

        if (!CanPassCombatAction(playerIndex))
        {
            return GameActionResult.Fail("That player does not have combat action priority.");
        }

        var player = State.Players[playerIndex];
        var events = new List<MatchEvent>
        {
            new()
            {
                Kind = MatchEventKind.CombatActionPassed,
                PlayerIndex = playerIndex,
                Message = $"{player.Name} passed combat action priority."
            }
        };
        State.Log.Add($"{player.Name} passed combat action priority.");
        var passes = action.ConsecutivePasses + 1;
        if (passes >= 2)
        {
            State.PendingCombatAction = null;
            var result = ResolveCombatByIds(action);
            events.AddRange(result.Events);
            return result.Success
                ? GameActionResult.Ok(result.Message, events)
                : result;
        }

        var nextPlayer = 1 - playerIndex;
        State.PendingCombatAction = action with { PriorityPlayerIndex = nextPlayer, ConsecutivePasses = passes };
        events.Add(new MatchEvent
        {
            Kind = MatchEventKind.CombatActionQueued,
            PlayerIndex = nextPlayer,
            Message = $"{State.Players[nextPlayer].Name} has combat action priority."
        });
        return GameActionResult.Ok($"{player.Name} passed. {State.Players[nextPlayer].Name} may act.", events);
    }

    private GameActionResult OpenCombatActionWindow(string blockerInstanceId, IReadOnlyList<MatchEvent> openingEvents, string message)
    {
        var pending = State.PendingAttack;
        if (pending is null)
        {
            return GameActionResult.Fail("There is no pending attack.");
        }

        var defenderIndex = 1 - pending.AttackerPlayerIndex;
        var attacker = State.Players[pending.AttackerPlayerIndex].UnitField
            .FirstOrDefault(card => card.Id.Equals(pending.AttackerInstanceId, StringComparison.OrdinalIgnoreCase));
        State.PendingCombatAction = new PendingCombatAction(defenderIndex, pending.AttackerInstanceId, blockerInstanceId, 0);
        State.Log.Add($"{State.Players[defenderIndex].Name} has combat action priority.");
        var events = new List<MatchEvent>(openingEvents)
        {
            new()
            {
                Kind = MatchEventKind.CombatActionQueued,
                PlayerIndex = defenderIndex,
                CardId = attacker?.CardId ?? "",
                InstanceId = pending.AttackerInstanceId,
                Message = $"{State.Players[defenderIndex].Name} has combat action priority."
            }
        };
        return GameActionResult.Ok(message, events);
    }

    private void AdvanceCombatActionPriorityAfterAction(int playerIndex, List<MatchEvent> events)
    {
        var action = State.PendingCombatAction;
        if (action is null || action.PriorityPlayerIndex != playerIndex)
        {
            return;
        }

        var nextPlayer = 1 - playerIndex;
        State.PendingCombatAction = action with { PriorityPlayerIndex = nextPlayer, ConsecutivePasses = 0 };
        State.Log.Add($"{State.Players[nextPlayer].Name} has combat action priority.");
        events.Add(new MatchEvent
        {
            Kind = MatchEventKind.CombatActionQueued,
            PlayerIndex = nextPlayer,
            Message = $"{State.Players[nextPlayer].Name} has combat action priority."
        });
    }

    private GameActionResult ResolveCombatByIds(PendingCombatAction action)
    {
        var defender = State.Players[1 - State.PendingAttack!.AttackerPlayerIndex];
        var blocker = string.IsNullOrWhiteSpace(action.BlockerInstanceId)
            ? null
            : defender.UnitField.FirstOrDefault(card => card.Id.Equals(action.BlockerInstanceId, StringComparison.OrdinalIgnoreCase));
        return ResolveCombat(blocker, blockedDeclared: !string.IsNullOrWhiteSpace(action.BlockerInstanceId));
    }

    private static IReadOnlyList<CardInstance> BuildDeck(DeckDefinition deck, bool shuffle, Random random, int playerIndex)
    {
        var sequence = 0;
        var cards = deck.Cards
            .SelectMany(entry => Enumerable.Range(0, entry.Value).Select(_ => new CardInstance(entry.Key, $"p{playerIndex}-d{sequence++:0000}-{entry.Key}")))
            .ToList();

        if (!shuffle)
        {
            return cards;
        }

        for (var i = cards.Count - 1; i > 0; i--)
        {
            var swapIndex = random.Next(i + 1);
            (cards[i], cards[swapIndex]) = (cards[swapIndex], cards[i]);
        }

        return cards;
    }

    private IReadOnlyList<MatchEvent> BeginCurrentPhase()
    {
        var events = new List<MatchEvent>();
        if (State.CurrentPhase.Equals("Ready", StringComparison.OrdinalIgnoreCase))
        {
            ReadyPlayer(State.ActivePlayer);
            State.EnergyAddsThisTurn = 0;
            State.Log.Add($"{State.ActivePlayer.Name} readied their field.");
            events.Add(new MatchEvent
            {
                Kind = MatchEventKind.CardReadied,
                PlayerIndex = State.ActivePlayerIndex,
                Message = $"{State.ActivePlayer.Name} readied their field."
            });
        }

        if (State.CurrentPhase.Equals("Draw", StringComparison.OrdinalIgnoreCase))
        {
            events.AddRange(DrawCards(State.ActivePlayerIndex, 1));
            State.Log.Add($"{State.ActivePlayer.Name} drew a card.");
        }

        return events;
    }

    private void ApplyCardResolution(PlayerState player, CardInstance instance, CardDefinition definition)
    {
        if (definition.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase))
        {
            instance.Exhausted = false;
            player.UnitField.Add(instance);
        }
        else if (definition.Type.Equals("Support", StringComparison.OrdinalIgnoreCase))
        {
            instance.Exhausted = false;
            player.SupportField.Add(instance);
        }

        ApplyKeywords(instance, definition);
        foreach (var hookName in definition.Hooks)
        {
            _hooks.Invoke(hookName, new EffectContext(this, State, State.ActivePlayerIndex, instance, definition, Ability: null));
        }

        if (definition.Type.Equals("Spell", StringComparison.OrdinalIgnoreCase))
        {
            player.DiscardPile.Add(instance);
        }
    }

    private void ApplyKeywords(CardInstance instance, CardDefinition definition)
    {
        foreach (var keyword in definition.Keywords)
        {
            if (keyword.Equals("Cantrip", StringComparison.OrdinalIgnoreCase))
            {
                DrawCards(State.ActivePlayerIndex, 1);
            }
            else if (keyword.Equals("Refresh", StringComparison.OrdinalIgnoreCase))
            {
                ReduceNextCardCost(State.ActivePlayerIndex, 1);
            }
            else if (keyword.Equals("Strike", StringComparison.OrdinalIgnoreCase))
            {
                DealDamageToOpponent(State.ActivePlayerIndex, 1);
            }
        }
    }

    private GameActionResult ResolveCombat(CardInstance? blocker, bool blockedDeclared)
    {
        var pending = State.PendingAttack;
        if (pending is null)
        {
            return GameActionResult.Fail("There is no pending attack.");
        }

        var attackerPlayer = State.Players[pending.AttackerPlayerIndex];
        var attacker = attackerPlayer.UnitField.FirstOrDefault(card => card.Id == pending.AttackerInstanceId);
        if (attacker is null)
        {
            State.PendingAttack = null;
            State.PendingCombatAction = null;
            var message = "The attacking unit left combat.";
            State.Log.Add(message);
            return GameActionResult.Ok(message, new MatchEvent
            {
                Kind = MatchEventKind.CombatResolved,
                PlayerIndex = pending.AttackerPlayerIndex,
                Message = message
            });
        }

        if (blocker is null)
        {
            if (blockedDeclared)
            {
                State.PendingAttack = null;
                State.PendingCombatAction = null;
                var blockedMessage = "The attack stayed blocked, but no blocker remained for combat damage.";
                State.Log.Add(blockedMessage);
                return GameActionResult.Ok(blockedMessage, new MatchEvent
                {
                    Kind = MatchEventKind.CombatResolved,
                    PlayerIndex = pending.AttackerPlayerIndex,
                    Message = blockedMessage
                });
            }

            var damageEvents = DealDamageToOpponent(pending.AttackerPlayerIndex, 1);
            State.PendingAttack = null;
            State.PendingCombatAction = null;
            var events = new List<MatchEvent>(damageEvents)
            {
                new()
                {
                    Kind = MatchEventKind.CombatResolved,
                    PlayerIndex = pending.AttackerPlayerIndex,
                    Message = "Attack was unblocked."
                }
            };
            return GameActionResult.Ok("Attack was unblocked.", events);
        }

        var defenderPlayer = State.Players[1 - pending.AttackerPlayerIndex];
        var attackerDefinition = State.DefinitionFor(attacker);
        var blockerDefinition = State.DefinitionFor(blocker);
        var attackerBonus = GetElementAdvantageBonus(attackerDefinition, blockerDefinition);
        var blockerBonus = GetElementAdvantageBonus(blockerDefinition, attackerDefinition);
        var attackerPower = attackerDefinition.Power + attackerBonus;
        var blockerPower = blockerDefinition.Power + blockerBonus;
        var combatEvents = new List<MatchEvent>();
        var advantageNotes = new List<string>();

        if (attackerBonus > 0)
        {
            advantageNotes.Add($"{attackerDefinition.Name} +{attackerBonus}");
        }

        if (blockerBonus > 0)
        {
            advantageNotes.Add($"{blockerDefinition.Name} +{blockerBonus}");
        }

        if (advantageNotes.Count > 0)
        {
            State.Log.Add($"Element advantage: {string.Join(", ", advantageNotes)}.");
        }

        if (attackerPower >= blockerPower)
        {
            var blockerFieldIndex = defenderPlayer.UnitField.FindIndex(card => card.Id == blocker.Id);
            MoveFromFieldToDiscard(defenderPlayer.UnitField, defenderPlayer.DiscardPile, blocker);
            State.Log.Add($"{State.CardName(blocker)} was defeated.");
            combatEvents.Add(new MatchEvent
            {
                Kind = MatchEventKind.CardDiscarded,
                PlayerIndex = 1 - pending.AttackerPlayerIndex,
                CardId = blocker.CardId,
                InstanceId = blocker.Id,
                From = new ZoneRef(1 - pending.AttackerPlayerIndex, "UnitField", blockerFieldIndex),
                To = new ZoneRef(1 - pending.AttackerPlayerIndex, "DiscardPile", defenderPlayer.DiscardPile.Count - 1),
                Message = $"{State.CardName(blocker)} was defeated."
            });
        }

        if (blockerPower >= attackerPower)
        {
            var attackerFieldIndex = attackerPlayer.UnitField.FindIndex(card => card.Id == attacker.Id);
            MoveFromFieldToDiscard(attackerPlayer.UnitField, attackerPlayer.DiscardPile, attacker);
            State.Log.Add($"{State.CardName(attacker)} was defeated.");
            combatEvents.Add(new MatchEvent
            {
                Kind = MatchEventKind.CardDiscarded,
                PlayerIndex = pending.AttackerPlayerIndex,
                CardId = attacker.CardId,
                InstanceId = attacker.Id,
                From = new ZoneRef(pending.AttackerPlayerIndex, "UnitField", attackerFieldIndex),
                To = new ZoneRef(pending.AttackerPlayerIndex, "DiscardPile", attackerPlayer.DiscardPile.Count - 1),
                Message = $"{State.CardName(attacker)} was defeated."
            });
        }

        State.PendingAttack = null;
        State.PendingCombatAction = null;
        combatEvents.Add(new MatchEvent
        {
            Kind = MatchEventKind.CombatResolved,
            PlayerIndex = pending.AttackerPlayerIndex,
            Message = advantageNotes.Count == 0
                ? "Combat resolved."
                : $"Combat resolved. Element advantage: {string.Join(", ", advantageNotes)}."
        });
        return GameActionResult.Ok("Combat resolved.", combatEvents);
    }

    private bool SpendCost(int playerIndex, IReadOnlyDictionary<string, int> cost, bool consumeNextCardReduction, out EnergyPaymentPlan? plan)
    {
        plan = CreatePaymentPlan(playerIndex, cost, includeNextCardReduction: consumeNextCardReduction);
        if (plan is null)
        {
            return false;
        }

        var player = State.Players[playerIndex];
        foreach (var element in State.Mode.Elements)
        {
            var spent = plan.Spent.GetValueOrDefault(element);
            if (spent > 0)
            {
                player.EnergyPool[element] = player.EnergyPool.GetValueOrDefault(element) - spent;
            }
        }

        player.LastPayment.Clear();
        foreach (var (element, spent) in plan.Spent)
        {
            player.LastPayment[element] = spent;
        }

        if (consumeNextCardReduction)
        {
            player.NextCardCostReduction = 0;
        }

        return true;
    }

    private static Dictionary<string, int> NormalizeCost(IReadOnlyDictionary<string, int> cost)
    {
        var normalized = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (element, amount) in cost)
        {
            if (amount > 0)
            {
                normalized[element] = normalized.GetValueOrDefault(element) + amount;
            }
        }

        normalized.TryAdd(DragonCardConstants.GenericCost, 0);
        return normalized;
    }

    private static int ApplyCostReduction(Dictionary<string, int> adjusted, int reduction)
    {
        var applied = 0;
        while (applied < reduction && adjusted.GetValueOrDefault(DragonCardConstants.GenericCost) > 0)
        {
            adjusted[DragonCardConstants.GenericCost]--;
            applied++;
        }

        foreach (var key in adjusted.Keys.Where(key => !key.Equals(DragonCardConstants.GenericCost, StringComparison.OrdinalIgnoreCase)).OrderBy(key => key).ToArray())
        {
            while (applied < reduction && adjusted[key] > 0)
            {
                adjusted[key]--;
                applied++;
            }
        }

        return applied;
    }

    private GameActionResult ConvertOneEnergy(int playerIndex, string toElement)
    {
        var player = State.Players[playerIndex];
        if (player.EnergyPool.GetValueOrDefault(toElement) >= State.Mode.EnergyRules.MaxPerElement)
        {
            return GameActionResult.Fail($"{toElement} energy is already maxed.");
        }

        var fromElement = State.Mode.Elements.FirstOrDefault(element =>
            !element.Equals(toElement, StringComparison.OrdinalIgnoreCase) &&
            player.EnergyPool.GetValueOrDefault(element) > 0);
        if (fromElement is null)
        {
            return GameActionResult.Fail("No different energy is available to convert.");
        }

        player.EnergyPool[fromElement]--;
        player.EnergyPool[toElement] = player.EnergyPool.GetValueOrDefault(toElement) + 1;
        State.Log.Add($"{player.Name} converted {fromElement} energy to {toElement}.");
        return GameActionResult.Ok($"Converted 1 energy to {toElement}.", new MatchEvent
        {
            Kind = MatchEventKind.EnergyConverted,
            PlayerIndex = playerIndex,
            Element = toElement,
            Amount = 1,
            To = new ZoneRef(playerIndex, "EnergyPool"),
            Message = $"Converted 1 energy to {toElement}."
        });
    }

    private MatchEvent PhaseEvent(string message) => new()
    {
        Kind = MatchEventKind.PhaseChanged,
        PlayerIndex = State.ActivePlayerIndex,
        Message = message
    };

    private static IEnumerable<MatchEvent> PaymentEvents(int playerIndex, EnergyPaymentPlan? paymentPlan)
    {
        if (paymentPlan is null)
        {
            yield break;
        }

        foreach (var (element, amount) in paymentPlan.Spent.Where(item => item.Value > 0))
        {
            yield return new MatchEvent
            {
                Kind = MatchEventKind.EnergySpent,
                PlayerIndex = playerIndex,
                Element = element,
                Amount = amount,
                From = new ZoneRef(playerIndex, "EnergyPool"),
                Message = $"{amount} {element} spent."
            };
        }
    }

    private static void MoveFromFieldToDiscard(List<CardInstance> field, List<CardInstance> discard, CardInstance instance)
    {
        field.Remove(instance);
        instance.Exhausted = false;
        discard.Add(instance);
    }

    private static void ReadyPlayer(PlayerState player)
    {
        foreach (var card in player.UnitField.Concat(player.SupportField))
        {
            card.Exhausted = false;
        }
    }

    private static bool IsValidIndex<T>(IReadOnlyList<T> list, int index) => index >= 0 && index < list.Count;

    public bool IsMainPhase() =>
        State.CurrentPhase.Equals("Main", StringComparison.OrdinalIgnoreCase) ||
        State.CurrentPhase.Equals("Second Main", StringComparison.OrdinalIgnoreCase);

    private bool TryGetSacrificeCard(SacrificeSource source, int index, out CardInstance instance, out CardDefinition definition)
    {
        instance = null!;
        definition = null!;

        var list = source switch
        {
            SacrificeSource.Hand => State.ActivePlayer.Hand,
            SacrificeSource.UnitField => State.ActivePlayer.UnitField,
            SacrificeSource.SupportField => State.ActivePlayer.SupportField,
            _ => []
        };

        if (!IsValidIndex(list, index))
        {
            return false;
        }

        instance = list[index];
        definition = State.DefinitionFor(instance);
        return true;
    }

    private void RemoveSacrificeCard(SacrificeSource source, CardInstance instance)
    {
        var list = source switch
        {
            SacrificeSource.Hand => State.ActivePlayer.Hand,
            SacrificeSource.UnitField => State.ActivePlayer.UnitField,
            SacrificeSource.SupportField => State.ActivePlayer.SupportField,
            _ => []
        };

        list.Remove(instance);
    }

    private string? ValidatePlayZone(PlayerState player, CardDefinition definition)
    {
        if (definition.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase) &&
            player.UnitField.Count >= State.Mode.ZoneLimits.UnitSlots)
        {
            return "The unit field is full.";
        }

        if (definition.Type.Equals("Support", StringComparison.OrdinalIgnoreCase) &&
            player.SupportField.Count >= State.Mode.ZoneLimits.SupportSlots)
        {
            return "The support row is full.";
        }

        return null;
    }
}
