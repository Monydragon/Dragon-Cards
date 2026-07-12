namespace DragonCards.Core;

public enum ProfileStorageKind
{
    Json,
    Sqlite
}

/// <summary>
/// Desktop-facing profile repository contract. Implementations own one storage format at a time;
/// callers never dual-write a profile to JSON and SQLite.
/// </summary>
public interface IProfileRepository
{
    string ApplicationDataDirectory { get; }
    ProfileStorageKind StorageKind { get; }
    IReadOnlyList<LocalProfileSummary> Profiles { get; }
    string? LastActiveProfileId { get; }
    IReadOnlyList<ProfileRepositoryError> Errors { get; }
    bool IsInitialized { get; }

    bool Initialize(out bool migrated, out string? error);
    bool TryLoadProfile(string profileId, out PlayerProfile? profile, out string? error);
    bool TryCreateProfile(PlayerProfile profile, DateTimeOffset now, out LocalProfileSummary? summary, out string? error);
    bool TrySaveProfile(string profileId, PlayerProfile profile, DateTimeOffset now, out string? error);
    bool TrySelectProfile(string profileId, DateTimeOffset now, out string? error);
    bool TryRenameProfile(string profileId, string displayName, DateTimeOffset now, out string? error);
    bool TryDeleteProfile(string profileId, out string? error);
    IReadOnlyList<DeckDefinition> LoadDecks(string profileId, out IReadOnlyList<ProfileRepositoryError> errors);
    bool TrySaveDeck(string profileId, DeckDefinition deck, out string? error);
    bool TryDeleteDeck(string profileId, string deckId, out string? error);
}
