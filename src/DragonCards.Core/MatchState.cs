namespace DragonCards.Core;

public sealed class MatchState
{
    public MatchState(
        GameModeDefinition mode,
        IReadOnlyDictionary<string, CardDefinition> cards,
        PlayerState firstPlayer,
        PlayerState secondPlayer)
    {
        Mode = mode;
        Cards = cards;
        Players = [firstPlayer, secondPlayer];
        foreach (var player in Players)
        {
            player.InitializeEnergyPool(mode.Elements);
        }
    }

    public GameModeDefinition Mode { get; }
    public IReadOnlyDictionary<string, CardDefinition> Cards { get; }
    public List<PlayerState> Players { get; }
    public int ActivePlayerIndex { get; set; }
    public int PhaseIndex { get; set; }
    public int TurnNumber { get; set; } = 1;
    public int EnergyAddsThisTurn { get; set; }
    public bool HasAddedEnergyThisTurn => EnergyAddsThisTurn >= Mode.EnergyRules.AddsPerTurn;
    public PendingAttack? PendingAttack { get; set; }
    public PendingEnergyChoice? PendingEnergyChoice { get; set; }
    public PendingTargetChoice? PendingTargetChoice { get; set; }
    public int? WinnerIndex { get; set; }
    public List<string> Log { get; } = [];

    public string CurrentPhase => Mode.Phases[PhaseIndex];
    public PlayerState ActivePlayer => Players[ActivePlayerIndex];
    public PlayerState DefendingPlayer => Players[1 - ActivePlayerIndex];

    public CardDefinition DefinitionFor(CardInstance instance) => Cards[instance.CardId];

    public string CardName(CardInstance instance) => DefinitionFor(instance).Name;
}

public sealed class PlayerState
{
    public PlayerState(string name, IReadOnlyList<CardInstance> deck)
    {
        Name = name;
        Deck = deck.ToList();
    }

    public string Name { get; set; }
    public List<CardInstance> Deck { get; }
    public List<CardInstance> Hand { get; } = [];
    public List<CardInstance> UnitField { get; } = [];
    public List<CardInstance> SupportField { get; } = [];
    public Dictionary<string, int> EnergyPool { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int NextCardCostReduction { get; set; }
    public Dictionary<string, int> LastPayment { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CardInstance> DiscardPile { get; } = [];
    public List<CardInstance> DamageZone { get; } = [];

    public void InitializeEnergyPool(IEnumerable<string> elements)
    {
        foreach (var element in elements)
        {
            EnergyPool.TryAdd(element, 0);
        }
    }
}

public sealed class CardInstance
{
    public CardInstance(string cardId)
    {
        Id = Guid.NewGuid().ToString("N");
        CardId = cardId;
    }

    public string Id { get; }
    public string CardId { get; }
    public bool Exhausted { get; set; }
}

public sealed record PendingAttack(int AttackerPlayerIndex, string AttackerInstanceId);

public sealed record PendingEnergyChoice(int PlayerIndex, PendingEnergyChoiceType Type, int Amount, string Message);

public enum PendingEnergyChoiceType
{
    Gain,
    ConvertTo
}

public sealed record PendingTargetChoice(
    int PlayerIndex,
    PendingTargetChoiceType Type,
    TargetScope Scope,
    string SourceInstanceId,
    string Message);

public enum PendingTargetChoiceType
{
    ExhaustUnit,
    ReadyUnit
}

public enum TargetScope
{
    EnemyUnit,
    FriendlyUnit,
    AnyUnit
}

public enum SacrificeSource
{
    Hand,
    UnitField,
    SupportField
}

public sealed record EnergyPaymentPlan(
    IReadOnlyDictionary<string, int> AdjustedCost,
    IReadOnlyDictionary<string, int> Spent,
    int ReductionApplied);

public sealed record ActivatableAbility(
    int PlayerIndex,
    string SourceInstanceId,
    CardDefinition Card,
    ActivatedAbilityDefinition Ability);

public readonly record struct ZoneRef(int PlayerIndex, string Zone, int Index = -1);

public enum MatchEventKind
{
    PhaseChanged,
    CardDrawn,
    CardPlayed,
    CardDiscarded,
    CardSacrificed,
    EnergySpent,
    EnergyGained,
    EnergyConverted,
    EnergyRefunded,
    CostReduced,
    AbilityActivated,
    TargetChoiceQueued,
    TargetResolved,
    AttackDeclared,
    BlockDeclared,
    CombatResolved,
    DamageTaken,
    CardReadied
}

public sealed record MatchEvent
{
    public MatchEventKind Kind { get; init; }
    public int PlayerIndex { get; init; } = -1;
    public string CardId { get; init; } = "";
    public string InstanceId { get; init; } = "";
    public ZoneRef? From { get; init; }
    public ZoneRef? To { get; init; }
    public string Element { get; init; } = "";
    public int Amount { get; init; }
    public string Message { get; init; } = "";
}

public readonly record struct GameActionResult(bool Success, string Message, IReadOnlyList<MatchEvent> Events)
{
    public GameActionResult(bool success, string message)
        : this(success, message, [])
    {
    }

    public static GameActionResult Ok(string message) => new(true, message);
    public static GameActionResult Ok(string message, IEnumerable<MatchEvent> events) => new(true, message, events.ToArray());
    public static GameActionResult Ok(string message, params MatchEvent[] events) => new(true, message, events);
    public static GameActionResult Fail(string message) => new(false, message);
}
