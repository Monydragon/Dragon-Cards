namespace DragonCards.Core;

public static class DragonCardConstants
{
    public const string GenericCost = "Generic";
    public const string MainTiming = "Main";
    public const string CombatTiming = "Combat";

    public static readonly string[] BuiltInPhases =
    [
        "Ready",
        "Draw",
        "Main",
        "Combat",
        "Second Main",
        "End"
    ];

    public static readonly string[] BuiltInCardTypes =
    [
        "Unit",
        "Support",
        "Spell"
    ];

    public static readonly string[] BuiltInKeywords =
    [
        "Cantrip",
        "Refresh",
        "Strike"
    ];
}

public static class CardSets
{
    public const string Core = "core";
    public const string AncientAwakening = "ancient-awakening";
    public const string ElementalAscension = "elemental-ascension";
    public const string PrimalClash = "primal-clash";
}

public sealed record GameModeDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public GameModeDisplayDefinition Display { get; init; } = new();
    public List<string> Elements { get; set; } = [];
    public List<string> Phases { get; set; } = [];
    public List<string> AllowedCardTypes { get; set; } = [];
    public DeckRulesDefinition DeckRules { get; init; } = new();
    public ZoneLimitDefinition ZoneLimits { get; init; } = new();
    public EnergyRulesDefinition EnergyRules { get; init; } = new();
    public ElementAdvantageDefinition? ElementAdvantage { get; init; }
    public AvatarRulesDefinition? AvatarRules { get; init; }
    public SealedRulesDefinition? SealedRules { get; init; }
    public bool ProgressionEligible { get; init; } = true;
    public int DamageLimit { get; init; } = 7;
    public int StartingHand { get; init; } = 5;
}

public sealed record GameModeDisplayDefinition
{
    public string Category { get; init; } = "";
    public string ShortName { get; init; } = "";
    public string FeatureText { get; init; } = "";
    public string[] Tags { get; init; } = [];
}

public sealed record DeckRulesDefinition
{
    public int DeckSize { get; init; } = 50;
    public int MaxCopies { get; init; } = 3;
    public bool Singleton { get; init; }
    public bool EnforceElementIdentity { get; init; }
}

public sealed record ZoneLimitDefinition
{
    public int UnitSlots { get; init; } = 5;
    public int SupportSlots { get; init; } = 5;
}

public sealed record EnergyRulesDefinition
{
    public int MaxPerElement { get; init; } = 10;
    public int AddsPerTurn { get; init; } = 1;
}

public sealed record ElementAdvantageDefinition
{
    public int PowerBonus { get; init; }
    public Dictionary<string, List<string>> StrongAgainst { get; set; } = [];
}

public sealed record AvatarRulesDefinition
{
    public bool Enabled { get; init; }
    public string RequiredType { get; init; } = "Unit";
    public string[] AllowedRarities { get; init; } = [CardRarities.Legendary, CardRarities.Mythic];
    public int ReplayGenericCostIncrease { get; init; } = 2;
    public int StartingCommandZoneCards { get; init; } = 1;
}

public sealed record SealedRulesDefinition
{
    public bool Enabled { get; init; }
    public int BoosterCount { get; init; } = 6;
    public int BuildDeckSize { get; init; } = 40;
    public int GauntletWinsRequired { get; init; } = 3;
    public int CompletionCoins { get; init; } = 500;
}

public sealed record CardDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public List<string> Elements { get; set; } = [];
    public Dictionary<string, int> Cost { get; set; } = [];
    public int Power { get; init; }
    public List<string> Keywords { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> Hooks { get; set; } = [];
    public List<ActivatedAbilityDefinition> Abilities { get; set; } = [];
    public CardVisualDefinition? Visual { get; init; }
    public string Rarity { get; set; } = CardRarities.Common;
    public string SetId { get; set; } = CardSets.Core;
    public string RulesText { get; init; } = "";

    public int TotalCost => Cost.Values.Sum();
}

public sealed record CardVisualDefinition
{
    public string Frame { get; init; } = "";
    public string Effect { get; init; } = "";
    public string Rarity { get; init; } = "";
    public string ArtKey { get; init; } = "";
}

public sealed record ActivatedAbilityDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public Dictionary<string, int> Cost { get; set; } = [];
    public List<string> Timings { get; set; } = [];
    public string Hook { get; init; } = "";
    public string RulesText { get; init; } = "";
}

public sealed record DeckDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string ModeId { get; init; } = "";
    public Dictionary<string, int> Cards { get; set; } = [];

    public int Count => Cards.Values.Sum();
}

public sealed record ValidationIssue(string Code, string Message, string? SubjectId = null);
