namespace DragonCards.Persistence;

public sealed class AppSettingEntity
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public long UpdatedUnixMilliseconds { get; set; }
}

public sealed class ProfileEntity
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "Player";
    public long CreatedUnixMilliseconds { get; set; }
    public long LastPlayedUnixMilliseconds { get; set; }
    public long UpdatedUnixMilliseconds { get; set; }
    public int Revision { get; set; } = 1;
    public int Experience { get; set; }
    public int Coins { get; set; }
    public string DefaultRulesJson { get; set; } = "{}";
    public string SelectedStarterDeckId { get; set; } = "";
    public string ActiveDeckId { get; set; } = "";
}

public sealed class CardCopyEntity
{
    public string ProfileId { get; set; } = "";
    public string CardId { get; set; } = "";
    public int Copies { get; set; }
}

public sealed class PackInventoryEntity
{
    public string ProfileId { get; set; } = "";
    public string PackId { get; set; } = "";
    public int Quantity { get; set; }
}

public sealed class StarterDeckOwnershipEntity
{
    public string ProfileId { get; set; } = "";
    public string StarterDeckId { get; set; } = "";
}

public sealed class TutorialCompletionEntity
{
    public string ProfileId { get; set; } = "";
    public string TutorialId { get; set; } = "";
    public long CompletedUnixMilliseconds { get; set; }
}

public sealed class QuestStateEntity
{
    public string ProfileId { get; set; } = "";
    public string DailyPeriod { get; set; } = "";
    public string WeeklyPeriod { get; set; } = "";
}

public sealed class QuestEntryEntity
{
    public string ProfileId { get; set; } = "";
    public string QuestId { get; set; } = "";
    public int Progress { get; set; }
    public bool Completed { get; set; }
}

public sealed class DeckEntity
{
    public string ProfileId { get; set; } = "";
    public string DeckId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ModeId { get; set; } = "";
    public long UpdatedUnixMilliseconds { get; set; }
}

public sealed class DeckCardEntity
{
    public string ProfileId { get; set; } = "";
    public string DeckId { get; set; } = "";
    public string CardId { get; set; } = "";
    public int Copies { get; set; }
}

public sealed class ProfileEventEntity
{
    public string Id { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public long OccurredUnixMilliseconds { get; set; }
    public string Kind { get; set; } = "";
    public string Summary { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
}

public sealed class ProfileSeedRunEntity
{
    public string Id { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public long Seed { get; set; }
    public string AlgorithmVersion { get; set; } = "";
    public string Scenario { get; set; } = "";
    public string Summary { get; set; } = "";
    public long AppliedUnixMilliseconds { get; set; }
}
