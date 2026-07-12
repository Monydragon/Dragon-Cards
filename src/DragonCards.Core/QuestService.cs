using System.Globalization;

namespace DragonCards.Core;

public enum QuestCadence
{
    Daily,
    Weekly
}

public enum QuestMetric
{
    EligibleMatches,
    NonEnergyCardsPlayed,
    EnergySourcesAdded,
    EligibleWins,
    DamageDealt
}

public sealed record QuestDefinition(
    string Id,
    QuestCadence Cadence,
    string Title,
    QuestMetric Metric,
    int Target,
    int Coins,
    int StandardPacks);

public sealed record QuestEntry
{
    public int Progress { get; set; }
    public bool Completed { get; set; }
}

public sealed record QuestProgressState
{
    public string DailyPeriod { get; set; } = "";
    public string WeeklyPeriod { get; set; } = "";
    public Dictionary<string, QuestEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Normalize()
    {
        Entries ??= new Dictionary<string, QuestEntry>(StringComparer.OrdinalIgnoreCase);
        Entries = Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value is not null)
            .ToDictionary(entry => entry.Key.Trim(), entry => entry.Value, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record QuestReward(string QuestId, string Title, int Coins, int StandardPacks);

public sealed record QuestUpdate(IReadOnlyList<QuestReward> Rewards, bool StateChanged)
{
    public static QuestUpdate None { get; } = new([], false);
}

public static class QuestService
{
    public static readonly IReadOnlyList<QuestDefinition> Definitions =
    [
        new("daily-eligible-matches", QuestCadence.Daily, "Play 2 eligible matches", QuestMetric.EligibleMatches, 2, 100, 0),
        new("daily-non-energy-cards", QuestCadence.Daily, "Play 10 non-Energy cards", QuestMetric.NonEnergyCardsPlayed, 10, 100, 0),
        new("daily-energy-sources", QuestCadence.Daily, "Add or play 5 energy sources", QuestMetric.EnergySourcesAdded, 5, 100, 0),
        new("weekly-eligible-wins", QuestCadence.Weekly, "Win 5 eligible matches", QuestMetric.EligibleWins, 5, 500, 1),
        new("weekly-damage", QuestCadence.Weekly, "Deal 15 damage", QuestMetric.DamageDealt, 15, 500, 1)
    ];

    public static QuestUpdate Refresh(PlayerProfile profile, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Quests ??= new QuestProgressState();
        profile.Quests.Normalize();
        var changed = false;
        var daily = DailyPeriod(now);
        var weekly = WeeklyPeriod(now);
        if (!string.Equals(profile.Quests.DailyPeriod, daily, StringComparison.Ordinal))
        {
            profile.Quests.DailyPeriod = daily;
            RemoveCadenceEntries(profile.Quests, QuestCadence.Daily);
            changed = true;
        }

        if (!string.Equals(profile.Quests.WeeklyPeriod, weekly, StringComparison.Ordinal))
        {
            profile.Quests.WeeklyPeriod = weekly;
            RemoveCadenceEntries(profile.Quests, QuestCadence.Weekly);
            changed = true;
        }

        foreach (var quest in Definitions)
        {
            if (!profile.Quests.Entries.ContainsKey(quest.Id))
            {
                profile.Quests.Entries[quest.Id] = new QuestEntry();
                changed = true;
            }
        }

        return new QuestUpdate([], changed);
    }

    public static QuestUpdate Record(PlayerProfile profile, DateTimeOffset now, QuestMetric metric, int amount, bool eligible)
    {
        if (!eligible || amount <= 0)
        {
            return Refresh(profile, now);
        }

        var refreshed = Refresh(profile, now);
        var rewards = new List<QuestReward>();
        foreach (var quest in Definitions.Where(item => item.Metric == metric))
        {
            var entry = profile.Quests.Entries[quest.Id];
            if (entry.Completed)
            {
                continue;
            }

            entry.Progress = Math.Min(quest.Target, entry.Progress + amount);
            if (entry.Progress < quest.Target)
            {
                continue;
            }

            entry.Completed = true;
            profile.Coins += quest.Coins;
            BoosterService.AddUnopenedPack(profile, BoosterService.StandardBoosterId, quest.StandardPacks);
            rewards.Add(new QuestReward(quest.Id, quest.Title, quest.Coins, quest.StandardPacks));
        }

        return new QuestUpdate(rewards, refreshed.StateChanged || amount > 0);
    }

    public static QuestEntry EntryFor(PlayerProfile profile, QuestDefinition quest) =>
        profile.Quests.Entries.GetValueOrDefault(quest.Id) ?? new QuestEntry();

    public static string DailyPeriod(DateTimeOffset now) => now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string WeeklyPeriod(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        return $"{ISOWeek.GetYear(utc):0000}-W{ISOWeek.GetWeekOfYear(utc):00}";
    }

    private static void RemoveCadenceEntries(QuestProgressState state, QuestCadence cadence)
    {
        foreach (var id in Definitions.Where(item => item.Cadence == cadence).Select(item => item.Id).ToArray())
        {
            state.Entries.Remove(id);
        }
    }
}
