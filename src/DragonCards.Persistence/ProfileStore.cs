using System.Text.Json;
using DragonCards.Core;
using Microsoft.EntityFrameworkCore;

namespace DragonCards.Persistence;

public sealed record ProfileStoreResult(bool Success, string Message)
{
    public static ProfileStoreResult Ok(string message = "") => new(true, message);
    public static ProfileStoreResult Fail(string message) => new(false, message);
}

public sealed record ProfileStoreResult<T>(bool Success, T? Value, string Message)
{
    public static ProfileStoreResult<T> Ok(T value, string message = "") => new(true, value, message);
    public static ProfileStoreResult<T> Fail(string message) => new(false, default, message);
}

public sealed record StoredProfile(
    string Id,
    PlayerProfile Profile,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastPlayedUtc,
    int Revision);

public sealed record StoredProfileSummary(
    string Id,
    string DisplayName,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastPlayedUtc,
    int Revision);

public sealed record ProfileAuditEntry(
    string Id,
    string ProfileId,
    DateTimeOffset OccurredUtc,
    string Kind,
    string Summary,
    string PayloadJson);

public sealed record ProfileSeedRun(
    string Id,
    string ProfileId,
    long Seed,
    string AlgorithmVersion,
    string Scenario,
    string Summary,
    DateTimeOffset AppliedUtc);

/// <summary>Validated JSON profile data ready for one atomic import into an empty SQLite store.</summary>
public sealed record ProfileImportSnapshot(
    string ProfileId,
    PlayerProfile Profile,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastPlayedUtc,
    IReadOnlyList<DeckDefinition> Decks);

/// <summary>
/// Local-first profile persistence contract. A future cloud sync implementation can use the
/// profile revision and audit stream without exposing a peer's database to a match host.
/// </summary>
public interface IProfileStore
{
    string DatabasePath { get; }

    Task<ProfileStoreResult> InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredProfileSummary>> ListProfilesAsync(CancellationToken cancellationToken = default);
    Task<string?> GetLastActiveProfileIdAsync(CancellationToken cancellationToken = default);
    Task<ProfileStoreResult<StoredProfile>> CreateProfileAsync(PlayerProfile profile, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<ProfileStoreResult<StoredProfile>> LoadProfileAsync(string profileId, CancellationToken cancellationToken = default);
    Task<ProfileStoreResult<StoredProfile>> SaveProfileAsync(string profileId, PlayerProfile profile, int? expectedRevision, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<ProfileStoreResult> SelectProfileAsync(string profileId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<ProfileStoreResult> DeleteProfileAsync(string profileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeckDefinition>> LoadDecksAsync(string profileId, CancellationToken cancellationToken = default);
    Task<ProfileStoreResult> SaveDeckAsync(string profileId, DeckDefinition deck, int? expectedProfileRevision, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<ProfileStoreResult> DeleteDeckAsync(string profileId, string deckId, int? expectedProfileRevision, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProfileAuditEntry>> ReadAuditAsync(string profileId, int take = 100, CancellationToken cancellationToken = default);
}

public sealed class SqliteProfileStore : IProfileStore, IDisposable
{
    private const string LastActiveProfileIdKey = "last-active-profile-id";
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    public SqliteProfileStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath { get; }

    public async Task<ProfileStoreResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return ProfileStoreResult.Ok();
            }

            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var db = CreateContext();
            await db.Database.MigrateAsync(cancellationToken);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken);
            _initialized = true;
            return ProfileStoreResult.Ok();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DbUpdateException or InvalidOperationException)
        {
            return ProfileStoreResult.Fail($"The profile database could not be opened: {exception.Message}");
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<IReadOnlyList<StoredProfileSummary>> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsureInitializedAsync(cancellationToken))
        {
            return [];
        }

        await using var db = CreateContext();
        return await db.Profiles.AsNoTracking()
            .OrderByDescending(profile => profile.LastPlayedUnixMilliseconds)
            .ThenBy(profile => profile.DisplayName)
            .Select(profile => new StoredProfileSummary(
                profile.Id,
                profile.DisplayName,
                FromUnixMilliseconds(profile.CreatedUnixMilliseconds),
                FromUnixMilliseconds(profile.LastPlayedUnixMilliseconds),
                profile.Revision))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<string?> GetLastActiveProfileIdAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsureInitializedAsync(cancellationToken))
        {
            return null;
        }

        await using var db = CreateContext();
        var profileId = await db.AppSettings.AsNoTracking()
            .Where(setting => setting.Key == LastActiveProfileIdKey)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(profileId) ? null : profileId;
    }

    public async Task<ProfileStoreResult<StoredProfile>> CreateProfileAsync(PlayerProfile profile, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var initialized = await InitializeAsync(cancellationToken);
        if (!initialized.Success)
        {
            return ProfileStoreResult<StoredProfile>.Fail(initialized.Message);
        }

        profile.Normalize();
        await using var db = CreateContext();
        var validationError = await ValidateProfileNameAsync(db, profile.PlayerName, null, cancellationToken);
        if (validationError is not null)
        {
            return ProfileStoreResult<StoredProfile>.Fail(validationError);
        }

        var profileId = Guid.NewGuid().ToString("N");
        var timestamp = ToUnixMilliseconds(now);
        var entity = new ProfileEntity
        {
            Id = profileId,
            DisplayName = profile.PlayerName,
            CreatedUnixMilliseconds = timestamp,
            LastPlayedUnixMilliseconds = timestamp,
            UpdatedUnixMilliseconds = timestamp,
            Revision = 1
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            ApplyProfile(entity, profile);
            db.Profiles.Add(entity);
            AddProfileChildren(db, profileId, profile, timestamp);
            SetLastActiveProfile(db, profileId, timestamp);
            AddAuditEvent(db, profileId, now, "profile.created", "Profile created.", new ProfileAuditPayload(Revision: entity.Revision));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ProfileStoreResult<StoredProfile>.Ok(ToStoredProfile(entity, profile));
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ProfileStoreResult<StoredProfile>.Fail("A profile with that name already exists on this device.");
        }
    }

    public async Task<ProfileStoreResult<StoredProfile>> LoadProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (!await EnsureInitializedAsync(cancellationToken))
        {
            return ProfileStoreResult<StoredProfile>.Fail("Profile storage is unavailable.");
        }

        await using var db = CreateContext();
        var entity = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(profile => profile.Id == profileId, cancellationToken);
        if (entity is null)
        {
            return ProfileStoreResult<StoredProfile>.Fail("That local profile no longer exists.");
        }

        var profile = await BuildProfileAsync(db, entity, cancellationToken);
        return ProfileStoreResult<StoredProfile>.Ok(ToStoredProfile(entity, profile));
    }

    /// <summary>Creates a separate imported profile and its custom decks in one transaction.</summary>
    public async Task<ProfileStoreResult<StoredProfile>> CreateProfileWithDecksAsync(
        PlayerProfile profile,
        IReadOnlyList<DeckDefinition> decks,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var initialized = await InitializeAsync(cancellationToken);
        if (!initialized.Success)
        {
            return ProfileStoreResult<StoredProfile>.Fail(initialized.Message);
        }

        profile.Normalize();
        await using var db = CreateContext();
        var validationError = await ValidateProfileNameAsync(db, profile.PlayerName, null, cancellationToken);
        if (validationError is not null)
        {
            return ProfileStoreResult<StoredProfile>.Fail(validationError);
        }

        var profileId = Guid.NewGuid().ToString("N");
        var timestamp = ToUnixMilliseconds(now);
        var entity = new ProfileEntity
        {
            Id = profileId,
            DisplayName = profile.PlayerName,
            CreatedUnixMilliseconds = timestamp,
            LastPlayedUnixMilliseconds = timestamp,
            UpdatedUnixMilliseconds = timestamp,
            Revision = 1
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            ApplyProfile(entity, profile);
            db.Profiles.Add(entity);
            AddProfileChildren(db, profileId, profile, timestamp);
            AddDecks(db, profileId, decks, timestamp);
            AddAuditEvent(db, profileId, now, "profile.imported-copy", "Imported a user-selected profile export as a new local profile.", new ProfileAuditPayload(Revision: entity.Revision));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ProfileStoreResult<StoredProfile>.Ok(ToStoredProfile(entity, profile));
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ProfileStoreResult<StoredProfile>.Fail("A profile with that name already exists on this device.");
        }
    }

    public async Task<ProfileStoreResult<StoredProfile>> SaveProfileAsync(
        string profileId,
        PlayerProfile profile,
        int? expectedRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!await EnsureInitializedAsync(cancellationToken))
        {
            return ProfileStoreResult<StoredProfile>.Fail("Profile storage is unavailable.");
        }

        profile.Normalize();
        await using var db = CreateContext();
        var entity = await db.Profiles.SingleOrDefaultAsync(item => item.Id == profileId, cancellationToken);
        if (entity is null)
        {
            return ProfileStoreResult<StoredProfile>.Fail("That local profile no longer exists.");
        }
        if (expectedRevision is not null && expectedRevision.Value != entity.Revision)
        {
            return ProfileStoreResult<StoredProfile>.Fail("This profile changed elsewhere. Reload it before saving.");
        }

        var validationError = await ValidateProfileNameAsync(db, profile.PlayerName, profileId, cancellationToken);
        if (validationError is not null)
        {
            return ProfileStoreResult<StoredProfile>.Fail(validationError);
        }

        var timestamp = ToUnixMilliseconds(now);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        ApplyProfile(entity, profile);
        entity.DisplayName = profile.PlayerName;
        entity.LastPlayedUnixMilliseconds = timestamp;
        entity.UpdatedUnixMilliseconds = timestamp;
        entity.Revision++;
        await RemoveProfileChildrenAsync(db, profileId, cancellationToken);
        AddProfileChildren(db, profileId, profile, timestamp);
        SetLastActiveProfile(db, profileId, timestamp);
        AddAuditEvent(db, profileId, now, "profile.saved", "Profile data updated.", new ProfileAuditPayload(Revision: entity.Revision));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ProfileStoreResult<StoredProfile>.Ok(ToStoredProfile(entity, profile));
    }

    public async Task<ProfileStoreResult> SelectProfileAsync(string profileId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (!await EnsureInitializedAsync(cancellationToken))
        {
            return ProfileStoreResult.Fail("Profile storage is unavailable.");
        }

        await using var db = CreateContext();
        var profile = await db.Profiles.SingleOrDefaultAsync(item => item.Id == profileId, cancellationToken);
        if (profile is null)
        {
            return ProfileStoreResult.Fail("That local profile no longer exists.");
        }

        var timestamp = ToUnixMilliseconds(now);
        profile.LastPlayedUnixMilliseconds = timestamp;
        SetLastActiveProfile(db, profileId, timestamp);
        await db.SaveChangesAsync(cancellationToken);
        return ProfileStoreResult.Ok();
    }

    public async Task<ProfileStoreResult> DeleteProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (!await EnsureInitializedAsync(cancellationToken))
        {
            return ProfileStoreResult.Fail("Profile storage is unavailable.");
        }

        await using var db = CreateContext();
        var profile = await db.Profiles.SingleOrDefaultAsync(item => item.Id == profileId, cancellationToken);
        if (profile is null)
        {
            return ProfileStoreResult.Fail("That local profile no longer exists.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Profiles.Remove(profile);
        var lastActive = await db.AppSettings.SingleOrDefaultAsync(setting => setting.Key == LastActiveProfileIdKey, cancellationToken);
        if (lastActive?.Value == profileId)
        {
            var replacement = await db.Profiles.AsNoTracking()
                .Where(item => item.Id != profileId)
                .OrderByDescending(item => item.LastPlayedUnixMilliseconds)
                .Select(item => item.Id)
                .FirstOrDefaultAsync(cancellationToken) ?? "";
            lastActive.Value = replacement;
            lastActive.UpdatedUnixMilliseconds = ToUnixMilliseconds(DateTimeOffset.UtcNow);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ProfileStoreResult.Ok();
    }

    public async Task<IReadOnlyList<DeckDefinition>> LoadDecksAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (!await EnsureInitializedAsync(cancellationToken))
        {
            return [];
        }

        await using var db = CreateContext();
        var decks = await db.Decks.AsNoTracking()
            .Where(deck => deck.ProfileId == profileId)
            .OrderBy(deck => deck.Name)
            .ToArrayAsync(cancellationToken);
        var cards = await db.DeckCards.AsNoTracking()
            .Where(card => card.ProfileId == profileId)
            .ToArrayAsync(cancellationToken);

        return decks.Select(deck => new DeckDefinition
        {
            Id = deck.DeckId,
            Name = deck.Name,
            ModeId = deck.ModeId,
            Cards = cards.Where(card => card.DeckId == deck.DeckId && card.Copies > 0)
                .OrderBy(card => card.CardId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(card => card.CardId, card => card.Copies, StringComparer.OrdinalIgnoreCase)
        }).ToArray();
    }

    public async Task<ProfileStoreResult> SaveDeckAsync(
        string profileId,
        DeckDefinition deck,
        int? expectedProfileRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deck);
        if (string.IsNullOrWhiteSpace(deck.Id) || string.IsNullOrWhiteSpace(deck.Name) || string.IsNullOrWhiteSpace(deck.ModeId))
        {
            return ProfileStoreResult.Fail("Decks need an id, name, and mode.");
        }
        if (!await EnsureInitializedAsync(cancellationToken))
        {
            return ProfileStoreResult.Fail("Profile storage is unavailable.");
        }

        await using var db = CreateContext();
        var profile = await db.Profiles.SingleOrDefaultAsync(item => item.Id == profileId, cancellationToken);
        if (profile is null)
        {
            return ProfileStoreResult.Fail("That local profile no longer exists.");
        }
        if (expectedProfileRevision is not null && expectedProfileRevision.Value != profile.Revision)
        {
            return ProfileStoreResult.Fail("This profile changed elsewhere. Reload it before saving a deck.");
        }

        var deckId = deck.Id.Trim();
        var timestamp = ToUnixMilliseconds(now);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entity = await db.Decks.SingleOrDefaultAsync(item => item.ProfileId == profileId && item.DeckId == deckId, cancellationToken);
        if (entity is null)
        {
            entity = new DeckEntity { ProfileId = profileId, DeckId = deckId };
            db.Decks.Add(entity);
        }
        entity.Name = deck.Name.Trim();
        entity.ModeId = deck.ModeId.Trim();
        entity.UpdatedUnixMilliseconds = timestamp;

        db.DeckCards.RemoveRange(await db.DeckCards.Where(item => item.ProfileId == profileId && item.DeckId == deckId).ToListAsync(cancellationToken));
        db.DeckCards.AddRange(deck.Cards
            .Where(card => !string.IsNullOrWhiteSpace(card.Key) && card.Value > 0)
            .OrderBy(card => card.Key, StringComparer.OrdinalIgnoreCase)
            .Select(card => new DeckCardEntity { ProfileId = profileId, DeckId = deckId, CardId = card.Key.Trim(), Copies = card.Value }));

        profile.LastPlayedUnixMilliseconds = timestamp;
        profile.UpdatedUnixMilliseconds = timestamp;
        profile.Revision++;
        AddAuditEvent(db, profileId, now, "deck.saved", $"Deck '{entity.Name}' saved.", new ProfileAuditPayload(Revision: profile.Revision, DeckId: deckId));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ProfileStoreResult.Ok();
    }

    public async Task<ProfileStoreResult> DeleteDeckAsync(
        string profileId,
        string deckId,
        int? expectedProfileRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureInitializedAsync(cancellationToken))
        {
            return ProfileStoreResult.Fail("Profile storage is unavailable.");
        }

        await using var db = CreateContext();
        var profile = await db.Profiles.SingleOrDefaultAsync(item => item.Id == profileId, cancellationToken);
        if (profile is null)
        {
            return ProfileStoreResult.Fail("That local profile no longer exists.");
        }
        if (expectedProfileRevision is not null && expectedProfileRevision.Value != profile.Revision)
        {
            return ProfileStoreResult.Fail("This profile changed elsewhere. Reload it before deleting a deck.");
        }

        var deck = await db.Decks.SingleOrDefaultAsync(item => item.ProfileId == profileId && item.DeckId == deckId, cancellationToken);
        if (deck is null)
        {
            return ProfileStoreResult.Fail("That deck no longer exists.");
        }

        var timestamp = ToUnixMilliseconds(now);
        db.Decks.Remove(deck);
        profile.LastPlayedUnixMilliseconds = timestamp;
        profile.UpdatedUnixMilliseconds = timestamp;
        profile.Revision++;
        AddAuditEvent(db, profileId, now, "deck.deleted", $"Deck '{deck.Name}' deleted.", new ProfileAuditPayload(Revision: profile.Revision, DeckId: deckId));
        await db.SaveChangesAsync(cancellationToken);
        return ProfileStoreResult.Ok();
    }

    public async Task<IReadOnlyList<ProfileAuditEntry>> ReadAuditAsync(string profileId, int take = 100, CancellationToken cancellationToken = default)
    {
        if (!await EnsureInitializedAsync(cancellationToken))
        {
            return [];
        }

        take = Math.Clamp(take, 1, 500);
        await using var db = CreateContext();
        return await db.ProfileEvents.AsNoTracking()
            .Where(item => item.ProfileId == profileId)
            .OrderByDescending(item => item.OccurredUnixMilliseconds)
            .ThenByDescending(item => item.Id)
            .Take(take)
            .Select(item => new ProfileAuditEntry(
                item.Id,
                item.ProfileId,
                FromUnixMilliseconds(item.OccurredUnixMilliseconds),
                item.Kind,
                item.Summary,
                item.PayloadJson))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<ProfileStoreResult> RecordSeedRunAsync(
        string profileId,
        long seed,
        string algorithmVersion,
        string scenario,
        string summary,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureInitializedAsync(cancellationToken))
        {
            return ProfileStoreResult.Fail("Profile storage is unavailable.");
        }

        await using var db = CreateContext();
        if (!await db.Profiles.AnyAsync(profile => profile.Id == profileId, cancellationToken))
        {
            return ProfileStoreResult.Fail("That local profile no longer exists.");
        }

        db.ProfileSeedRuns.Add(new ProfileSeedRunEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            ProfileId = profileId,
            Seed = seed,
            AlgorithmVersion = algorithmVersion,
            Scenario = scenario,
            Summary = summary,
            AppliedUnixMilliseconds = ToUnixMilliseconds(now)
        });
        AddAuditEvent(db, profileId, now, "seed.applied", summary, new ProfileAuditPayload(Seed: seed, AlgorithmVersion: algorithmVersion, Scenario: scenario));
        await db.SaveChangesAsync(cancellationToken);
        return ProfileStoreResult.Ok();
    }

    public async Task<IReadOnlyList<ProfileSeedRun>> ReadSeedRunsAsync(string profileId, int take = 30, CancellationToken cancellationToken = default)
    {
        if (!await EnsureInitializedAsync(cancellationToken))
        {
            return [];
        }

        take = Math.Clamp(take, 1, 100);
        await using var db = CreateContext();
        return await db.ProfileSeedRuns.AsNoTracking()
            .Where(item => item.ProfileId == profileId)
            .OrderByDescending(item => item.AppliedUnixMilliseconds)
            .Take(take)
            .Select(item => new ProfileSeedRun(
                item.Id,
                item.ProfileId,
                item.Seed,
                item.AlgorithmVersion,
                item.Scenario,
                item.Summary,
                FromUnixMilliseconds(item.AppliedUnixMilliseconds)))
            .ToArrayAsync(cancellationToken);
    }

    internal async Task<ProfileStoreResult> ImportSnapshotsAsync(
        IReadOnlyList<ProfileImportSnapshot> snapshots,
        string? lastActiveProfileId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (snapshots.Count == 0)
        {
            return ProfileStoreResult.Fail("The JSON source contains no profiles to import.");
        }

        if (!await EnsureInitializedAsync(cancellationToken))
        {
            return ProfileStoreResult.Fail("Profile storage is unavailable.");
        }

        var duplicateId = snapshots.GroupBy(snapshot => snapshot.ProfileId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
        {
            return ProfileStoreResult.Fail($"The JSON source contains duplicate profile id '{duplicateId.Key}'.");
        }

        var duplicateName = snapshots.GroupBy(snapshot => snapshot.Profile.PlayerName.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateName is not null)
        {
            return ProfileStoreResult.Fail($"The JSON source contains duplicate profile name '{duplicateName.Key}'.");
        }

        foreach (var snapshot in snapshots)
        {
            if (string.IsNullOrWhiteSpace(snapshot.ProfileId) || snapshot.ProfileId.Length > 32)
            {
                return ProfileStoreResult.Fail("The JSON source contains an invalid profile id.");
            }

            var nameError = ValidateProfileNameFormat(snapshot.Profile.PlayerName);
            if (nameError is not null)
            {
                return ProfileStoreResult.Fail(nameError);
            }

            var duplicateDeck = snapshot.Decks.GroupBy(deck => deck.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (duplicateDeck is not null)
            {
                return ProfileStoreResult.Fail($"Profile '{snapshot.Profile.PlayerName}' contains duplicate deck id '{duplicateDeck.Key}'.");
            }
        }

        await using var db = CreateContext();
        if (await db.Profiles.AnyAsync(cancellationToken))
        {
            return ProfileStoreResult.Fail("SQLite profile migration requires an empty target database and will not merge data.");
        }

        var timestamp = ToUnixMilliseconds(now);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var snapshot in snapshots)
            {
                snapshot.Profile.Normalize();
                var entity = new ProfileEntity
                {
                    Id = snapshot.ProfileId,
                    DisplayName = snapshot.Profile.PlayerName,
                    CreatedUnixMilliseconds = ToUnixMilliseconds(snapshot.CreatedUtc),
                    LastPlayedUnixMilliseconds = ToUnixMilliseconds(snapshot.LastPlayedUtc),
                    UpdatedUnixMilliseconds = timestamp,
                    Revision = 1
                };
                ApplyProfile(entity, snapshot.Profile);
                db.Profiles.Add(entity);
                AddProfileChildren(db, entity.Id, snapshot.Profile, timestamp);
                AddDecks(db, entity.Id, snapshot.Decks, timestamp);
                AddAuditEvent(db, entity.Id, now, "profile.imported", "Imported from the verified JSON profile store.", new ProfileAuditPayload(Revision: entity.Revision));
            }

            var activeProfileId = snapshots.Any(snapshot => snapshot.ProfileId.Equals(lastActiveProfileId, StringComparison.OrdinalIgnoreCase))
                ? lastActiveProfileId!
                : snapshots.OrderByDescending(snapshot => snapshot.LastPlayedUtc).First().ProfileId;
            SetLastActiveProfile(db, activeProfileId, timestamp);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ProfileStoreResult.Ok($"Imported {snapshots.Count} JSON profile{(snapshots.Count == 1 ? "" : "s")}.");
        }
        catch (DbUpdateException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ProfileStoreResult.Fail($"The JSON profile import could not be committed: {exception.Message}");
        }
    }

    public void Dispose() => _initializeGate.Dispose();

    private DragonCardsDbContext CreateContext() => DragonCardsDbContextFactory.Create(DatabasePath);

    private async Task<bool> EnsureInitializedAsync(CancellationToken cancellationToken) =>
        _initialized || (await InitializeAsync(cancellationToken)).Success;

    private static async Task<string?> ValidateProfileNameAsync(DragonCardsDbContext db, string? displayName, string? excludingProfileId, CancellationToken cancellationToken)
    {
        var name = displayName?.Trim() ?? "";
        var formatError = ValidateProfileNameFormat(name);
        if (formatError is not null)
        {
            return formatError;
        }

        var nameInUse = await db.Profiles.AsNoTracking().AnyAsync(profile =>
            profile.Id != excludingProfileId && profile.DisplayName == name, cancellationToken);
        return nameInUse ? "Profile names must be unique on this device." : null;
    }

    private static string? ValidateProfileNameFormat(string? displayName)
    {
        var name = displayName?.Trim() ?? "";
        if (name.Length is < 1 or > 18)
        {
            return "Profile names must be 1–18 characters.";
        }
        return name.Any(char.IsControl) ? "Profile names cannot contain control characters." : null;
    }

    private static async Task<PlayerProfile> BuildProfileAsync(DragonCardsDbContext db, ProfileEntity entity, CancellationToken cancellationToken)
    {
        var profile = new PlayerProfile
        {
            PlayerName = entity.DisplayName,
            Experience = entity.Experience,
            Coins = entity.Coins,
            DefaultRules = DeserializeRules(entity.DefaultRulesJson),
            SelectedStarterDeckId = entity.SelectedStarterDeckId,
            ActiveDeckId = entity.ActiveDeckId,
            OwnedCards = (await db.CardCopies.AsNoTracking().Where(item => item.ProfileId == entity.Id).ToArrayAsync(cancellationToken))
                .ToDictionary(item => item.CardId, item => item.Copies, StringComparer.OrdinalIgnoreCase),
            UnopenedPacks = (await db.PackInventory.AsNoTracking().Where(item => item.ProfileId == entity.Id).ToArrayAsync(cancellationToken))
                .ToDictionary(item => item.PackId, item => item.Quantity, StringComparer.OrdinalIgnoreCase),
            OwnedStarterDeckIds = (await db.StarterDeckOwnership.AsNoTracking().Where(item => item.ProfileId == entity.Id).ToArrayAsync(cancellationToken))
                .Select(item => item.StarterDeckId).ToList(),
            CompletedTutorialIds = (await db.TutorialCompletions.AsNoTracking().Where(item => item.ProfileId == entity.Id).ToArrayAsync(cancellationToken))
                .Select(item => item.TutorialId).ToList()
        };

        var questState = await db.QuestStates.AsNoTracking().SingleOrDefaultAsync(item => item.ProfileId == entity.Id, cancellationToken);
        profile.Quests = new QuestProgressState
        {
            DailyPeriod = questState?.DailyPeriod ?? "",
            WeeklyPeriod = questState?.WeeklyPeriod ?? "",
            Entries = (await db.QuestEntries.AsNoTracking().Where(item => item.ProfileId == entity.Id).ToArrayAsync(cancellationToken))
                .ToDictionary(item => item.QuestId, item => new QuestEntry { Progress = item.Progress, Completed = item.Completed }, StringComparer.OrdinalIgnoreCase)
        };
        profile.Normalize();
        return profile;
    }

    private static void ApplyProfile(ProfileEntity entity, PlayerProfile profile)
    {
        entity.Experience = profile.Experience;
        entity.Coins = profile.Coins;
        entity.DefaultRulesJson = JsonSerializer.Serialize(profile.DefaultRules, PersistenceJsonContext.Default.GameRulesConfig);
        entity.SelectedStarterDeckId = profile.SelectedStarterDeckId;
        entity.ActiveDeckId = profile.ActiveDeckId;
    }

    private static void AddProfileChildren(DragonCardsDbContext db, string profileId, PlayerProfile profile, long timestamp)
    {
        db.CardCopies.AddRange(profile.OwnedCards.Select(card => new CardCopyEntity { ProfileId = profileId, CardId = card.Key, Copies = card.Value }));
        db.PackInventory.AddRange(profile.UnopenedPacks.Select(pack => new PackInventoryEntity { ProfileId = profileId, PackId = pack.Key, Quantity = pack.Value }));
        db.StarterDeckOwnership.AddRange(profile.OwnedStarterDeckIds.Select(deckId => new StarterDeckOwnershipEntity { ProfileId = profileId, StarterDeckId = deckId }));
        db.TutorialCompletions.AddRange(profile.CompletedTutorialIds.Select(tutorialId => new TutorialCompletionEntity { ProfileId = profileId, TutorialId = tutorialId, CompletedUnixMilliseconds = timestamp }));
        db.QuestStates.Add(new QuestStateEntity
        {
            ProfileId = profileId,
            DailyPeriod = profile.Quests.DailyPeriod,
            WeeklyPeriod = profile.Quests.WeeklyPeriod
        });
        db.QuestEntries.AddRange(profile.Quests.Entries.Select(entry => new QuestEntryEntity
        {
            ProfileId = profileId,
            QuestId = entry.Key,
            Progress = entry.Value.Progress,
            Completed = entry.Value.Completed
        }));
    }

    private static void AddDecks(DragonCardsDbContext db, string profileId, IEnumerable<DeckDefinition> decks, long timestamp)
    {
        foreach (var deck in decks)
        {
            var deckId = deck.Id.Trim();
            if (deckId.Length == 0 || string.IsNullOrWhiteSpace(deck.Name) || string.IsNullOrWhiteSpace(deck.ModeId))
            {
                continue;
            }

            db.Decks.Add(new DeckEntity
            {
                ProfileId = profileId,
                DeckId = deckId,
                Name = deck.Name.Trim(),
                ModeId = deck.ModeId.Trim(),
                UpdatedUnixMilliseconds = timestamp
            });
            db.DeckCards.AddRange(deck.Cards
                .Where(card => !string.IsNullOrWhiteSpace(card.Key) && card.Value > 0)
                .OrderBy(card => card.Key, StringComparer.OrdinalIgnoreCase)
                .Select(card => new DeckCardEntity
                {
                    ProfileId = profileId,
                    DeckId = deckId,
                    CardId = card.Key.Trim(),
                    Copies = card.Value
                }));
        }
    }

    private static async Task RemoveProfileChildrenAsync(DragonCardsDbContext db, string profileId, CancellationToken cancellationToken)
    {
        db.CardCopies.RemoveRange(await db.CardCopies.Where(item => item.ProfileId == profileId).ToListAsync(cancellationToken));
        db.PackInventory.RemoveRange(await db.PackInventory.Where(item => item.ProfileId == profileId).ToListAsync(cancellationToken));
        db.StarterDeckOwnership.RemoveRange(await db.StarterDeckOwnership.Where(item => item.ProfileId == profileId).ToListAsync(cancellationToken));
        db.TutorialCompletions.RemoveRange(await db.TutorialCompletions.Where(item => item.ProfileId == profileId).ToListAsync(cancellationToken));
        db.QuestStates.RemoveRange(await db.QuestStates.Where(item => item.ProfileId == profileId).ToListAsync(cancellationToken));
        db.QuestEntries.RemoveRange(await db.QuestEntries.Where(item => item.ProfileId == profileId).ToListAsync(cancellationToken));
    }

    private static void SetLastActiveProfile(DragonCardsDbContext db, string profileId, long timestamp)
    {
        var setting = db.AppSettings.Local.SingleOrDefault(item => item.Key == LastActiveProfileIdKey)
            ?? db.AppSettings.SingleOrDefault(item => item.Key == LastActiveProfileIdKey);
        if (setting is null)
        {
            db.AppSettings.Add(new AppSettingEntity { Key = LastActiveProfileIdKey, Value = profileId, UpdatedUnixMilliseconds = timestamp });
            return;
        }

        setting.Value = profileId;
        setting.UpdatedUnixMilliseconds = timestamp;
    }

    private static void AddAuditEvent(DragonCardsDbContext db, string profileId, DateTimeOffset now, string kind, string summary, ProfileAuditPayload payload) =>
        db.ProfileEvents.Add(new ProfileEventEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            ProfileId = profileId,
            OccurredUnixMilliseconds = ToUnixMilliseconds(now),
            Kind = kind,
            Summary = summary,
            PayloadJson = JsonSerializer.Serialize(payload, PersistenceJsonContext.Default.ProfileAuditPayload)
        });

    private static StoredProfile ToStoredProfile(ProfileEntity entity, PlayerProfile profile) =>
        new(entity.Id, profile, FromUnixMilliseconds(entity.CreatedUnixMilliseconds), FromUnixMilliseconds(entity.LastPlayedUnixMilliseconds), entity.Revision);

    private static GameRulesConfig DeserializeRules(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.GameRulesConfig) ?? GameRulesConfig.ForPreset(GameRulesPreset.Standard)).Normalize();
        }
        catch (JsonException)
        {
            return GameRulesConfig.ForPreset(GameRulesPreset.Standard);
        }
    }

    private static long ToUnixMilliseconds(DateTimeOffset value) => value.ToUniversalTime().ToUnixTimeMilliseconds();
    private static DateTimeOffset FromUnixMilliseconds(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);
}
