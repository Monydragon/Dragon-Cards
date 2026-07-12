using DragonCards.Core;
using Microsoft.Data.Sqlite;

namespace DragonCards.Persistence;

/// <summary>
/// Synchronous desktop adapter over the asynchronous SQLite store. It preserves the existing
/// profile-picker contract while keeping SQLite as the only active writer once selected.
/// </summary>
public sealed class SqliteProfileRepository : IProfileRepository, IDisposable
{
    public const string DatabaseFileName = "dragoncards.db";

    private readonly SqliteProfileStore _store;
    private readonly List<ProfileRepositoryError> _errors = [];
    private readonly Dictionary<string, int> _revisions = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<LocalProfileSummary> _profiles = [];
    private string? _lastActiveProfileId;

    public SqliteProfileRepository(string applicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        ApplicationDataDirectory = applicationDataDirectory;
        DatabasePath = Path.Combine(applicationDataDirectory, DatabaseFileName);
        _store = new SqliteProfileStore(DatabasePath);
    }

    public string ApplicationDataDirectory { get; }
    public string DatabasePath { get; }
    public ProfileStorageKind StorageKind => ProfileStorageKind.Sqlite;
    public IReadOnlyList<LocalProfileSummary> Profiles => _profiles;
    public string? LastActiveProfileId => _lastActiveProfileId;
    public IReadOnlyList<ProfileRepositoryError> Errors => _errors;
    public bool IsInitialized { get; private set; }

    public bool Initialize(out bool migrated, out string? error)
    {
        migrated = false;
        error = null;
        _errors.Clear();
        try
        {
            var result = _store.InitializeAsync().GetAwaiter().GetResult();
            if (!result.Success)
            {
                error = result.Message;
                AddError(error);
                return false;
            }

            Refresh();
            IsInitialized = true;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException)
        {
            error = $"The SQLite profile database could not be opened: {exception.Message}";
            AddError(error);
            return false;
        }
    }

    public bool TryLoadProfile(string profileId, out PlayerProfile? profile, out string? error)
    {
        profile = null;
        error = null;
        if (!EnsureInitialized(out error))
        {
            return false;
        }

        var result = _store.LoadProfileAsync(profileId).GetAwaiter().GetResult();
        if (!result.Success || result.Value is null)
        {
            error = result.Message;
            AddError(error);
            return false;
        }

        profile = result.Value.Profile;
        _revisions[profileId] = result.Value.Revision;
        return true;
    }

    public bool TryCreateProfile(PlayerProfile profile, DateTimeOffset now, out LocalProfileSummary? summary, out string? error)
    {
        summary = null;
        error = null;
        if (!EnsureInitialized(out error))
        {
            return false;
        }

        var result = _store.CreateProfileAsync(profile, now).GetAwaiter().GetResult();
        if (!result.Success || result.Value is null)
        {
            error = result.Message;
            AddError(error);
            return false;
        }

        _revisions[result.Value.Id] = result.Value.Revision;
        Refresh();
        summary = SummaryFor(result.Value);
        return true;
    }

    public bool TrySaveProfile(string profileId, PlayerProfile profile, DateTimeOffset now, out string? error)
    {
        error = null;
        if (!EnsureInitialized(out error))
        {
            return false;
        }

        var result = _store.SaveProfileAsync(profileId, profile, RevisionFor(profileId), now).GetAwaiter().GetResult();
        if (!result.Success || result.Value is null)
        {
            error = result.Message;
            AddError(error);
            return false;
        }

        _revisions[profileId] = result.Value.Revision;
        Refresh();
        return true;
    }

    public bool TrySelectProfile(string profileId, DateTimeOffset now, out string? error)
    {
        error = null;
        if (!EnsureInitialized(out error))
        {
            return false;
        }

        var result = _store.SelectProfileAsync(profileId, now).GetAwaiter().GetResult();
        if (!result.Success)
        {
            error = result.Message;
            AddError(error);
            return false;
        }

        Refresh();
        return true;
    }

    public bool TryRenameProfile(string profileId, string displayName, DateTimeOffset now, out string? error)
    {
        error = null;
        if (!TryLoadProfile(profileId, out var profile, out error) || profile is null)
        {
            return false;
        }

        profile.PlayerName = displayName;
        return TrySaveProfile(profileId, profile, now, out error);
    }

    public bool TryDeleteProfile(string profileId, out string? error)
    {
        error = null;
        if (!EnsureInitialized(out error))
        {
            return false;
        }

        var result = _store.DeleteProfileAsync(profileId).GetAwaiter().GetResult();
        if (!result.Success)
        {
            error = result.Message;
            AddError(error);
            return false;
        }

        _revisions.Remove(profileId);
        Refresh();
        return true;
    }

    public IReadOnlyList<DeckDefinition> LoadDecks(string profileId, out IReadOnlyList<ProfileRepositoryError> errors)
    {
        if (!EnsureInitialized(out var error))
        {
            errors = [new ProfileRepositoryError(DatabasePath, error ?? "SQLite profile storage is unavailable.")];
            return [];
        }

        try
        {
            errors = [];
            return _store.LoadDecksAsync(profileId).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            var repositoryError = new ProfileRepositoryError(DatabasePath, $"Decks could not be read: {exception.Message}");
            AddError(repositoryError.Message);
            errors = [repositoryError];
            return [];
        }
    }

    public bool TrySaveDeck(string profileId, DeckDefinition deck, out string? error)
    {
        error = null;
        if (!EnsureInitialized(out error))
        {
            return false;
        }

        var result = _store.SaveDeckAsync(profileId, deck, RevisionFor(profileId), DateTimeOffset.UtcNow).GetAwaiter().GetResult();
        if (!result.Success)
        {
            error = result.Message;
            AddError(error);
            return false;
        }

        RefreshRevision(profileId);
        Refresh();
        return true;
    }

    public bool TryDeleteDeck(string profileId, string deckId, out string? error)
    {
        error = null;
        if (!EnsureInitialized(out error))
        {
            return false;
        }

        var result = _store.DeleteDeckAsync(profileId, deckId, RevisionFor(profileId), DateTimeOffset.UtcNow).GetAwaiter().GetResult();
        if (!result.Success)
        {
            error = result.Message;
            AddError(error);
            return false;
        }

        RefreshRevision(profileId);
        Refresh();
        return true;
    }

    public void Dispose() => _store.Dispose();

    private bool EnsureInitialized(out string? error)
    {
        if (IsInitialized)
        {
            error = null;
            return true;
        }

        error = "SQLite profile storage is unavailable because initialization did not complete.";
        return false;
    }

    private int? RevisionFor(string profileId)
    {
        if (_revisions.TryGetValue(profileId, out var revision))
        {
            return revision;
        }

        RefreshRevision(profileId);
        return _revisions.GetValueOrDefault(profileId, 0) is var loaded && loaded > 0 ? loaded : null;
    }

    private void RefreshRevision(string profileId)
    {
        var loaded = _store.LoadProfileAsync(profileId).GetAwaiter().GetResult();
        if (loaded.Success && loaded.Value is not null)
        {
            _revisions[profileId] = loaded.Value.Revision;
        }
    }

    private void Refresh()
    {
        _profiles = _store.ListProfilesAsync().GetAwaiter().GetResult()
            .Select(summary => new LocalProfileSummary
            {
                Id = summary.Id,
                DisplayName = summary.DisplayName,
                CreatedUtc = summary.CreatedUtc,
                LastPlayedUtc = summary.LastPlayedUtc
            })
            .ToArray();
        _lastActiveProfileId = _store.GetLastActiveProfileIdAsync().GetAwaiter().GetResult();
    }

    private static LocalProfileSummary SummaryFor(StoredProfile profile) => new()
    {
        Id = profile.Id,
        DisplayName = profile.Profile.PlayerName,
        CreatedUtc = profile.CreatedUtc,
        LastPlayedUtc = profile.LastPlayedUtc
    };

    private void AddError(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message) && _errors.All(error => !error.Message.Equals(message, StringComparison.Ordinal)))
        {
            _errors.Add(new ProfileRepositoryError(DatabasePath, message));
        }
    }
}
