using DragonCards.Core;

namespace DragonCards.Desktop;

internal static class StoreCatalogFilter
{
    public static IReadOnlyList<ShopCatalogItem> Apply(
        IEnumerable<ShopCatalogItem> catalog,
        GameData data,
        ShopItemKind category,
        string? search = null,
        string? element = null,
        string? rarity = null,
        string? setId = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(data);

        IEnumerable<ShopCatalogItem> query = catalog.Where(item => item.Kind == category);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(item => item.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        if (category == ShopItemKind.SingleCard)
        {
            query = query.Where(item =>
                data.CardsById.TryGetValue(item.CardId, out var card) &&
                Matches(element, card.Elements) &&
                Matches(rarity, [card.Rarity]) &&
                Matches(setId, [card.SetId]));
        }

        return query.ToArray();
    }

    private static bool Matches(string? filter, IEnumerable<string> values) =>
        string.IsNullOrWhiteSpace(filter) ||
        filter.Equals("All", StringComparison.OrdinalIgnoreCase) ||
        values.Contains(filter, StringComparer.OrdinalIgnoreCase);
}
