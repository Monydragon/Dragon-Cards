namespace DragonCards.Core;

public enum CollectionOwnershipFilter
{
    All,
    Owned,
    Unowned
}

public enum CollectionSortMode
{
    Name,
    Cost,
    Rarity,
    OwnedCopies
}

public sealed record CollectionSetSummary(string SetId, int OwnedDistinctCards, int TotalCards);

public sealed record CollectionSummary(
    int OwnedDistinctCards,
    int OwnedCopies,
    IReadOnlyList<CollectionSetSummary> Sets);

/// <summary>Pure filtering and collection counting used by the Deck Builder.</summary>
public static class CollectionDiscoveryService
{
    public static IReadOnlyList<CardDefinition> FilterAndSort(
        IEnumerable<CardDefinition> cards,
        IReadOnlyDictionary<string, int>? ownedCards,
        string elementFilter,
        string typeFilter,
        string rarityFilter,
        string setFilter,
        CollectionOwnershipFilter ownershipFilter,
        CollectionSortMode sortMode)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ownedCards ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ownership = ownedCards;
        var filtered = cards
            .Where(card => !BasicEnergy.IsBasicEnergyCard(card) && !EnergySource.IsEnergySourceToken(card))
            .Where(card => elementFilter.Equals("All", StringComparison.OrdinalIgnoreCase) || card.Elements.Contains(elementFilter, StringComparer.OrdinalIgnoreCase))
            .Where(card => typeFilter.Equals("All", StringComparison.OrdinalIgnoreCase) || card.Type.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
            .Where(card => rarityFilter.Equals("All", StringComparison.OrdinalIgnoreCase) || card.Rarity.Equals(rarityFilter, StringComparison.OrdinalIgnoreCase))
            .Where(card => setFilter.Equals("All", StringComparison.OrdinalIgnoreCase) || card.SetId.Equals(setFilter, StringComparison.OrdinalIgnoreCase))
            .Where(card => ownershipFilter switch
            {
                CollectionOwnershipFilter.Owned => BasicEnergy.IsBasicEnergyCard(card) || ownership.GetValueOrDefault(card.Id) > 0,
                CollectionOwnershipFilter.Unowned => !BasicEnergy.IsBasicEnergyCard(card) && ownership.GetValueOrDefault(card.Id) <= 0,
                _ => true
            });

        return sortMode switch
        {
            CollectionSortMode.Cost => filtered
                .OrderBy(card => card.TotalCost)
                .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            CollectionSortMode.Rarity => filtered
                .OrderByDescending(card => RarityRank(card.Rarity))
                .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            CollectionSortMode.OwnedCopies => filtered
                .OrderByDescending(card => BasicEnergy.IsBasicEnergyCard(card) ? int.MaxValue : ownership.GetValueOrDefault(card.Id))
                .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            _ => filtered
                .OrderBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    public static CollectionSummary Summarize(IEnumerable<CardDefinition> cards, IReadOnlyDictionary<string, int>? ownedCards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ownedCards ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var allCards = cards.Where(card => !BasicEnergy.IsBasicEnergyCard(card) && !EnergySource.IsEnergySourceToken(card)).ToArray();
        var validOwned = allCards
            .Select(card => (Card: card, Count: BasicEnergy.IsBasicEnergyCard(card) ? 1 : Math.Max(0, ownedCards.GetValueOrDefault(card.Id))))
            .ToArray();
        var sets = allCards
            .GroupBy(card => card.SetId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CollectionSetSummary(
                group.Key,
                group.Count(card => BasicEnergy.IsBasicEnergyCard(card) || ownedCards.GetValueOrDefault(card.Id) > 0),
                group.Count()))
            .ToArray();

        return new CollectionSummary(
            validOwned.Count(entry => entry.Count > 0),
            validOwned.Sum(entry => entry.Count),
            sets);
    }

    private static int RarityRank(string rarity) => CardRarities.Normalize(rarity) switch
    {
        CardRarities.Mythic => 4,
        CardRarities.Legendary => 3,
        CardRarities.Rare => 2,
        CardRarities.Uncommon => 1,
        _ => 0
    };
}
