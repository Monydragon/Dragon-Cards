using System.Text.Json;
using System.Text.Json.Serialization;

namespace DragonCards.Core;

public sealed class GameData
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        TypeInfoResolver = DragonCardsJsonContext.Default,
        WriteIndented = true
    };

    public GameData(
        IReadOnlyList<GameModeDefinition> gameModes,
        IReadOnlyList<CardDefinition> cards,
        IReadOnlyList<DeckDefinition> decks)
    {
        foreach (var mode in gameModes)
        {
            mode.Elements ??= [];
            mode.Phases ??= [];
            mode.AllowedCardTypes ??= [];
            if (mode.ElementAdvantage is not null)
            {
                mode.ElementAdvantage.StrongAgainst = (mode.ElementAdvantage.StrongAgainst ?? [])
                    .ToDictionary(entry => entry.Key, entry => entry.Value ?? [], StringComparer.OrdinalIgnoreCase);
            }
        }

        foreach (var card in cards)
        {
            card.Elements ??= [];
            card.Cost ??= [];
            card.Keywords ??= [];
            card.Hooks ??= [];
            card.Abilities ??= [];
            foreach (var ability in card.Abilities)
            {
                ability.Cost ??= [];
            }
        }

        foreach (var deck in decks)
        {
            deck.Cards ??= [];
        }

        GameModes = gameModes;
        Cards = cards;
        Decks = decks;
        GameModesById = gameModes.ToDictionary(mode => mode.Id, StringComparer.OrdinalIgnoreCase);
        CardsById = cards.ToDictionary(card => card.Id, StringComparer.OrdinalIgnoreCase);
        DecksById = decks.ToDictionary(deck => deck.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<GameModeDefinition> GameModes { get; }
    public IReadOnlyList<CardDefinition> Cards { get; }
    public IReadOnlyList<DeckDefinition> Decks { get; }
    public IReadOnlyDictionary<string, GameModeDefinition> GameModesById { get; }
    public IReadOnlyDictionary<string, CardDefinition> CardsById { get; }
    public IReadOnlyDictionary<string, DeckDefinition> DecksById { get; }

    public static GameData LoadDefault()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var outputDataDirectory = Path.Combine(baseDirectory, "Data");

        if (Directory.Exists(outputDataDirectory))
        {
            return LoadFromDirectory(outputDataDirectory);
        }

        var sourceDataDirectory = FindSourceDataDirectory(baseDirectory);
        if (sourceDataDirectory is not null)
        {
            return LoadFromDirectory(sourceDataDirectory);
        }

        throw new DirectoryNotFoundException("Could not find Dragon Cards data directory.");
    }

    public static GameData LoadFromDirectory(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory))
        {
            throw new DirectoryNotFoundException($"Data directory not found: {dataDirectory}");
        }

        var gameModes = LoadFolder<GameModeDefinition>(Path.Combine(dataDirectory, "game-modes"));
        var cards = LoadFolder<CardDefinition>(Path.Combine(dataDirectory, "cards"));
        var decks = LoadFolder<DeckDefinition>(Path.Combine(dataDirectory, "decks"));

        return new GameData(gameModes, cards, decks);
    }

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static IReadOnlyList<T> LoadFolder<T>(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(folder, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .SelectMany(LoadJsonFile<T>)
            .ToArray();
    }

    private static IReadOnlyList<T> LoadJsonFile<T>(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        var json = document.RootElement.GetRawText();
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<T[]>(json, JsonOptions) ?? [];
        }

        var value = JsonSerializer.Deserialize<T>(json, JsonOptions);
        return value is null ? [] : [value];
    }

    private static string? FindSourceDataDirectory(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "data");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

[JsonSerializable(typeof(GameModeDefinition))]
[JsonSerializable(typeof(GameModeDefinition[]))]
[JsonSerializable(typeof(CardDefinition))]
[JsonSerializable(typeof(CardDefinition[]))]
[JsonSerializable(typeof(CardVisualDefinition))]
[JsonSerializable(typeof(CardVisualDefinition[]))]
[JsonSerializable(typeof(ActivatedAbilityDefinition))]
[JsonSerializable(typeof(ActivatedAbilityDefinition[]))]
[JsonSerializable(typeof(DeckDefinition))]
[JsonSerializable(typeof(DeckDefinition[]))]
[JsonSerializable(typeof(DeckRulesDefinition))]
[JsonSerializable(typeof(ZoneLimitDefinition))]
[JsonSerializable(typeof(EnergyRulesDefinition))]
[JsonSerializable(typeof(ElementAdvantageDefinition))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<ActivatedAbilityDefinition>))]
internal sealed partial class DragonCardsJsonContext : JsonSerializerContext;
