using DragonCards.Core;

namespace DragonCards.Persistence;

public enum ProfileSeedScenario
{
    Demo,
    CollectionShowcase
}

public sealed record ProfileSeedRequest(long Seed, ProfileSeedScenario Scenario, DateTimeOffset EffectiveUtc);

public sealed record ProfileSeedPreview(
    PlayerProfile Profile,
    long Seed,
    string AlgorithmVersion,
    string Scenario,
    string Summary,
    IReadOnlyList<string> GrantedCardIds);

/// <summary>Reproducible local-profile seed generator using a versioned SplitMix64 stream.</summary>
public sealed class ProfileSeedService
{
    public const string AlgorithmVersion = "splitmix64-v1";

    public ProfileSeedPreview CreatePreview(PlayerProfile current, GameData data, ProfileSeedRequest request)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(data);
        var profile = PlayerProfileSerializer.FromJson(PlayerProfileSerializer.ToJson(current));
        var random = new SplitMix64(unchecked((ulong)request.Seed));
        var eligibleCards = data.Cards
            .Where(card => !BasicEnergy.IsBasicEnergyCard(card) && !EnergySource.IsEnergySourceToken(card))
            .OrderBy(card => card.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var granted = request.Scenario switch
        {
            ProfileSeedScenario.CollectionShowcase => GrantCards(profile, eligibleCards, ref random, 80, replaceCollection: true),
            _ => GrantCards(profile, eligibleCards, ref random, 24, replaceCollection: false)
        };

        if (request.Scenario == ProfileSeedScenario.CollectionShowcase)
        {
            profile.Coins = 10000;
            profile.UnopenedPacks[BoosterService.StandardBoosterId] = 12;
        }
        else
        {
            profile.Coins += 1200 + random.NextInt(801);
            profile.UnopenedPacks[BoosterService.StandardBoosterId] = profile.UnopenedPacks.GetValueOrDefault(BoosterService.StandardBoosterId) + 3;
        }

        profile.Quests = new QuestProgressState
        {
            DailyPeriod = QuestService.DailyPeriod(request.EffectiveUtc),
            WeeklyPeriod = QuestService.WeeklyPeriod(request.EffectiveUtc),
            Entries = new Dictionary<string, QuestEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["daily-energy-sources"] = new QuestEntry { Progress = random.NextInt(6), Completed = false },
                ["weekly-damage"] = new QuestEntry { Progress = random.NextInt(16), Completed = false }
            }
        };
        profile.Normalize();
        var scenario = request.Scenario.ToString();
        var summary = $"{scenario} seed {request.Seed} at {request.EffectiveUtc.UtcDateTime:O} granted {granted.Count} deterministic card entries.";
        return new ProfileSeedPreview(profile, request.Seed, AlgorithmVersion, scenario, summary, granted);
    }

    private static IReadOnlyList<string> GrantCards(PlayerProfile profile, IReadOnlyList<CardDefinition> cards, ref SplitMix64 random, int count, bool replaceCollection)
    {
        if (replaceCollection)
        {
            profile.OwnedCards.Clear();
        }

        var available = cards.ToList();
        var granted = new List<string>();
        for (var index = 0; index < Math.Min(count, available.Count); index++)
        {
            var selectedIndex = random.NextInt(available.Count);
            var card = available[selectedIndex];
            available.RemoveAt(selectedIndex);
            profile.OwnedCards[card.Id] = 1 + random.NextInt(PlayerCollection.MaxOwnedCopies);
            granted.Add(card.Id);
        }

        return granted;
    }

    private struct SplitMix64(ulong state)
    {
        private ulong _state = state;

        public int NextInt(int exclusiveMaximum)
        {
            if (exclusiveMaximum <= 0)
            {
                return 0;
            }

            return (int)(NextUInt64() % (uint)exclusiveMaximum);
        }

        private ulong NextUInt64()
        {
            var value = (_state += 0x9E3779B97F4A7C15UL);
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}
