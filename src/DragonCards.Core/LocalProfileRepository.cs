using System.Text.Json;
using System.Text.Json.Serialization;

namespace DragonCards.Core;

/// <summary>
/// Summary data kept in the local profile index. Gameplay data remains in the
/// profile's own directory so profiles do not share progression or decks.
/// </summary>
public sealed record LocalProfileSummary
{
    public string Id { get; init; } = "";
    public string DisplayName { get; set; } = "Player";
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset LastPlayedUtc { get; set; }
}

public sealed record LocalProfileIndex
{
    public int Version { get; init; } = 1;
    public string LastActiveProfileId { get; set; } = "";
    public List<LocalProfileSummary> Profiles { get; set; } = [];
}

public sealed record ProfileRepositoryError(string Path, string Message);

/// <summary>
/// Per-device local profile storage. The repository deliberately leaves files
/// it cannot parse in place and returns a recoverable error to the UI.
/// </summary>
public sealed class LocalProfileRepository : IProfileRepository
{
    private readonly List<ProfileRepositoryError> _errors = [];
    private LocalProfileIndex _index = new();
    private bool _initialized;

    public LocalProfileRepository(string applicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        ApplicationDataDirectory = applicationDataDirectory;
        ProfilesDirectory = Path.Combine(applicationDataDirectory, "profiles");
        IndexPath = Path.Combine(ProfilesDirectory, "index.json");
    }

    public string ApplicationDataDirectory { get; }
    public ProfileStorageKind StorageKind => ProfileStorageKind.Json;
    public string ProfilesDirectory { get; }
    public string IndexPath { get; }
    public string LegacyProfilePath => Path.Combine(ApplicationDataDirectory, "profile.json");
    public string LegacyDecksDirectory => Path.Combine(ApplicationDataDirectory, "decks");
    public IReadOnlyList<LocalProfileSummary> Profiles => _index.Profiles
        .OrderByDescending(profile => profile.LastPlayedUtc)
        .ThenBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    public string? LastActiveProfileId => string.IsNullOrWhiteSpace(_index.LastActiveProfileId) ? null : _index.LastActiveProfileId;
    public IReadOnlyList<ProfileRepositoryError> Errors => _errors;
    public bool IsInitialized => _initialized;

    /// <summary>Loads the index and imports a valid 0.1.x root profile once.</summary>
    public bool Initialize(out bool migrated, out string? error)
    {
        migrated = false;
        error = null;
        _initialized = false;
        _errors.Clear();

        try
        {
            Directory.CreateDirectory(ProfilesDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"Could not open the profiles folder: {exception.Message}";
            return false;
        }

        if (File.Exists(IndexPath))
        {
            if (!TryReadIndex(out error))
            {
                return false;
            }

            _initialized = true;
            return true;
        }

        if (File.Exists(LegacyProfilePath))
        {
            if (!TryMigrateLegacyProfile(out migrated, out error))
            {
                return false;
            }

            _initialized = true;
            return true;
        }

        _index = new LocalProfileIndex();
        _initialized = TryWriteIndex(out error);
        return _initialized;
    }

    public bool TryLoadProfile(string profileId, out PlayerProfile? profile, out string? error)
    {
        profile = null;
        error = null;
        if (!EnsureInitialized(out error))
        {
            return false;
        }
        if (FindSummary(profileId) is null)
        {
            error = "That local profile no longer exists.";
            return false;
        }

        var path = ProfilePath(profileId);
        if (!File.Exists(path))
        {
            error = "The profile data file is missing. It was left unchanged for recovery.";
            AddError(path, error);
            return false;
        }

        try
        {
            profile = PlayerProfileSerializer.Load(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
        {
            error = "The profile data could not be read and was left unchanged for recovery.";
            AddError(path, $"{error} {exception.Message}");
            return false;
        }
    }

    public bool TryCreateProfile(PlayerProfile profile, DateTimeOffset now, out LocalProfileSummary? summary, out string? error)
    {
        ArgumentNullException.ThrowIfNull(profile);
        summary = null;
        if (!EnsureInitialized(out error))
        {
            return false;
        }
        if (!TryValidateName(profile.PlayerName, null, out error))
        {
            return false;
        }

        profile.Normalize();
        var id = Guid.NewGuid().ToString("N");
        summary = new LocalProfileSummary
        {
            Id = id,
            DisplayName = profile.PlayerName,
            CreatedUtc = now,
            LastPlayedUtc = now
        };

        try
        {
            WriteAtomic(ProfilePath(id), PlayerProfileSerializer.ToJson(profile));
            _index.Profiles.Add(summary);
            _index.LastActiveProfileId = id;
            if (!TryWriteIndex(out error))
            {
                _index.Profiles.Remove(summary);
                _index.LastActiveProfileId = "";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"Could not save the new profile: {exception.Message}";
            AddError(ProfilePath(id), error);
            return false;
        }
    }

    public bool TrySaveProfile(string profileId, PlayerProfile profile, DateTimeOffset now, out string? error)
    {
        ArgumentNullException.ThrowIfNull(profile);
        error = null;
        if (!EnsureInitialized(out error))
        {
            return false;
        }
        var summary = FindSummary(profileId);
        if (summary is null)
        {
            error = "That local profile no longer exists.";
            return false;
        }

        profile.Normalize();
        try
        {
            WriteAtomic(ProfilePath(profileId), PlayerProfileSerializer.ToJson(profile));
            summary.DisplayName = profile.PlayerName;
            summary.LastPlayedUtc = now;
            _index.LastActiveProfileId = profileId;
            return TryWriteIndex(out error);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"Could not save the profile: {exception.Message}";
            AddError(ProfilePath(profileId), error);
            return false;
        }
    }

    public bool TrySelectProfile(string profileId, DateTimeOffset now, out string? error)
    {
        error = null;
        if (!EnsureInitialized(out error))
        {
            return false;
        }
        var summary = FindSummary(profileId);
        if (summary is null)
        {
            error = "That local profile no longer exists.";
            return false;
        }

        summary.LastPlayedUtc = now;
        _index.LastActiveProfileId = profileId;
        return TryWriteIndex(out error);
    }

    public bool TryRenameProfile(string profileId, string displayName, DateTimeOffset now, out string? error)
    {
        error = null;
        if (!EnsureInitialized(out error))
        {
            return false;
        }
        var summary = FindSummary(profileId);
        if (summary is null)
        {
            error = "That local profile no longer exists.";
            return false;
        }

        if (!TryValidateName(displayName, profileId, out error) || !TryLoadProfile(profileId, out var profile, out error) || profile is null)
        {
            return false;
        }

        profile.PlayerName = displayName.Trim();
        return TrySaveProfile(profileId, profile, now, out error);
    }

    public bool TryDeleteProfile(string profileId, out string? error)
    {
        error = null;
        if (!EnsureInitialized(out error))
        {
            return false;
        }
        var summary = FindSummary(profileId);
        if (summary is null)
        {
            error = "That local profile no longer exists.";
            return false;
        }

        _index.Profiles.Remove(summary);
        if (_index.LastActiveProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase))
        {
            _index.LastActiveProfileId = _index.Profiles
                .OrderByDescending(candidate => candidate.LastPlayedUtc)
                .Select(candidate => candidate.Id)
                .FirstOrDefault() ?? "";
        }

        if (!TryWriteIndex(out error))
        {
            _index.Profiles.Add(summary);
            return false;
        }

        try
        {
            var directory = ProfileDirectory(profileId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"The profile was removed from the index but its files could not be deleted: {exception.Message}";
            AddError(ProfileDirectory(profileId), error);
            return false;
        }
    }

    public IReadOnlyList<DeckDefinition> LoadDecks(string profileId, out IReadOnlyList<ProfileRepositoryError> errors)
    {
        var results = new List<DeckDefinition>();
        var operationErrors = new List<ProfileRepositoryError>();
        if (!EnsureInitialized(out var initializationError))
        {
            errors = [new ProfileRepositoryError(IndexPath, initializationError ?? "Profile storage is unavailable.")];
            return results;
        }
        var directory = DecksDirectory(profileId);
        if (!Directory.Exists(directory))
        {
            errors = operationErrors;
            return results;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var deck = JsonSerializer.Deserialize(File.ReadAllText(path), LocalProfileJsonContext.Default.DeckDefinition);
                if (deck is null || string.IsNullOrWhiteSpace(deck.Id) || string.IsNullOrWhiteSpace(deck.Name) || deck.Cards is null)
                {
                    throw new JsonException("The deck is missing required data.");
                }

                deck.Cards = deck.Cards
                    .Where(card => !string.IsNullOrWhiteSpace(card.Key) && card.Value > 0)
                    .ToDictionary(card => card.Key, card => card.Value, StringComparer.OrdinalIgnoreCase);
                results.Add(deck);
            }
            catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
            {
                var repositoryError = new ProfileRepositoryError(path, $"Deck could not be read and was left unchanged for recovery: {exception.Message}");
                operationErrors.Add(repositoryError);
                AddError(path, repositoryError.Message);
            }
        }

        errors = operationErrors;
        return results
            .GroupBy(deck => deck.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(deck => deck.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool TrySaveDeck(string profileId, DeckDefinition deck, out string? error)
    {
        ArgumentNullException.ThrowIfNull(deck);
        error = null;
        if (!EnsureInitialized(out error))
        {
            return false;
        }
        if (FindSummary(profileId) is null)
        {
            error = "That local profile no longer exists.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(deck.Id) || string.IsNullOrWhiteSpace(deck.Name))
        {
            error = "Decks need an id and a name.";
            return false;
        }

        try
        {
            var normalized = deck with
            {
                Id = deck.Id.Trim(),
                Name = deck.Name.Trim(),
                Cards = deck.Cards
                    .Where(card => !string.IsNullOrWhiteSpace(card.Key) && card.Value > 0)
                    .OrderBy(card => card.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(card => card.Key, card => card.Value, StringComparer.OrdinalIgnoreCase)
            };
            WriteAtomic(DeckPath(profileId, normalized.Id), JsonSerializer.Serialize(normalized, LocalProfileJsonContext.Default.DeckDefinition));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"Could not save the deck: {exception.Message}";
            AddError(DeckPath(profileId, deck.Id), error);
            return false;
        }
    }

    public bool TryDeleteDeck(string profileId, string deckId, out string? error)
    {
        error = null;
        if (!EnsureInitialized(out error))
        {
            return false;
        }
        if (FindSummary(profileId) is null)
        {
            error = "That local profile no longer exists.";
            return false;
        }

        try
        {
            var path = DeckPath(profileId, deckId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"Could not delete the deck: {exception.Message}";
            AddError(DeckPath(profileId, deckId), error);
            return false;
        }
    }

    public string ProfileDirectory(string profileId) => Path.Combine(ProfilesDirectory, profileId);
    public string ProfilePath(string profileId) => Path.Combine(ProfileDirectory(profileId), "profile.json");
    public string DecksDirectory(string profileId) => Path.Combine(ProfileDirectory(profileId), "decks");
    public string DeckPath(string profileId, string deckId) => Path.Combine(DecksDirectory(profileId), $"{deckId}.json");

    private bool TryMigrateLegacyProfile(out bool migrated, out string? error)
    {
        migrated = false;
        error = null;
        PlayerProfile profile;
        try
        {
            profile = PlayerProfileSerializer.Load(LegacyProfilePath);
        }
        catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
        {
            error = "The legacy profile could not be read and was left unchanged for recovery.";
            AddError(LegacyProfilePath, $"{error} {exception.Message}");
            return false;
        }

        if (!TryValidateName(profile.PlayerName, null, out error))
        {
            error = $"The legacy profile name needs attention before migration: {error}";
            AddError(LegacyProfilePath, error);
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var summary = new LocalProfileSummary
        {
            Id = id,
            DisplayName = profile.PlayerName,
            CreatedUtc = now,
            LastPlayedUtc = now
        };

        try
        {
            WriteAtomic(ProfilePath(id), PlayerProfileSerializer.ToJson(profile));

            var importedDeckPaths = new List<string>();
            if (Directory.Exists(LegacyDecksDirectory))
            {
                foreach (var legacyDeckPath in Directory.EnumerateFiles(LegacyDecksDirectory, "*.json"))
                {
                    try
                    {
                        var deck = JsonSerializer.Deserialize(File.ReadAllText(legacyDeckPath), LocalProfileJsonContext.Default.DeckDefinition);
                        if (deck is null || string.IsNullOrWhiteSpace(deck.Id) || string.IsNullOrWhiteSpace(deck.Name) || deck.Cards is null)
                        {
                            throw new JsonException("The deck is missing required data.");
                        }

                        if (TrySaveDeckFile(id, deck, out var deckError))
                        {
                            importedDeckPaths.Add(legacyDeckPath);
                        }
                        else
                        {
                            AddError(legacyDeckPath, deckError ?? "Deck could not be imported and was left unchanged.");
                        }
                    }
                    catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
                    {
                        AddError(legacyDeckPath, $"Legacy deck could not be imported and was left unchanged: {exception.Message}");
                    }
                }
            }

            _index = new LocalProfileIndex
            {
                LastActiveProfileId = id,
                Profiles = [summary]
            };
            if (!TryWriteIndex(out error))
            {
                return false;
            }

            File.Delete(LegacyProfilePath);
            foreach (var legacyDeckPath in importedDeckPaths)
            {
                File.Delete(legacyDeckPath);
            }

            migrated = true;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"The legacy profile could not be migrated and was left unchanged: {exception.Message}";
            AddError(LegacyProfilePath, error);
            return false;
        }
    }

    private bool TrySaveDeckFile(string profileId, DeckDefinition deck, out string? error)
    {
        error = null;
        try
        {
            var normalized = deck with
            {
                Id = deck.Id.Trim(),
                Name = deck.Name.Trim(),
                Cards = deck.Cards
                    .Where(card => !string.IsNullOrWhiteSpace(card.Key) && card.Value > 0)
                    .ToDictionary(card => card.Key, card => card.Value, StringComparer.OrdinalIgnoreCase)
            };
            WriteAtomic(DeckPath(profileId, normalized.Id), JsonSerializer.Serialize(normalized, LocalProfileJsonContext.Default.DeckDefinition));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }

    private bool TryReadIndex(out string? error)
    {
        error = null;
        try
        {
            var index = JsonSerializer.Deserialize(File.ReadAllText(IndexPath), LocalProfileJsonContext.Default.LocalProfileIndex);
            if (index is null || index.Version != 1 || index.Profiles is null ||
                index.Profiles.Any(profile => string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.DisplayName)))
            {
                throw new JsonException("The profile index is missing required data.");
            }

            _index = index with
            {
                Profiles = index.Profiles
                    .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList()
            };
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
        {
            error = "The profile index could not be read and was left unchanged for recovery.";
            AddError(IndexPath, $"{error} {exception.Message}");
            return false;
        }
    }

    private bool TryWriteIndex(out string? error)
    {
        error = null;
        try
        {
            WriteAtomic(IndexPath, JsonSerializer.Serialize(_index, LocalProfileJsonContext.Default.LocalProfileIndex));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"Could not save the profile index: {exception.Message}";
            AddError(IndexPath, error);
            return false;
        }
    }

    private bool TryValidateName(string? displayName, string? excludingProfileId, out string? error)
    {
        var name = displayName?.Trim() ?? "";
        if (name.Length is < 1 or > 18)
        {
            error = "Profile names must be 1–18 characters.";
            return false;
        }

        if (name.Any(char.IsControl))
        {
            error = "Profile names cannot contain control characters.";
            return false;
        }

        if (_index.Profiles.Any(profile =>
                !profile.Id.Equals(excludingProfileId, StringComparison.OrdinalIgnoreCase) &&
                profile.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            error = "Profile names must be unique on this device.";
            return false;
        }

        error = null;
        return true;
    }

    private LocalProfileSummary? FindSummary(string profileId) => _index.Profiles
        .FirstOrDefault(profile => profile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));

    private bool EnsureInitialized(out string? error)
    {
        if (_initialized)
        {
            error = null;
            return true;
        }

        error = "Profile storage is unavailable because its index needs recovery. Existing files were left unchanged.";
        return false;
    }

    private void AddError(string path, string message)
    {
        if (_errors.All(error => !error.Path.Equals(path, StringComparison.OrdinalIgnoreCase) || !error.Message.Equals(message, StringComparison.Ordinal)))
        {
            _errors.Add(new ProfileRepositoryError(path, message));
        }
    }

    private static void WriteAtomic(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, contents);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

[JsonSerializable(typeof(LocalProfileIndex))]
[JsonSerializable(typeof(LocalProfileSummary))]
[JsonSerializable(typeof(List<LocalProfileSummary>))]
[JsonSerializable(typeof(DeckDefinition))]
[JsonSerializable(typeof(Dictionary<string, int>))]
internal sealed partial class LocalProfileJsonContext : JsonSerializerContext;
