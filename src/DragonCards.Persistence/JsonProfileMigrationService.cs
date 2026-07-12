using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DragonCards.Core;

namespace DragonCards.Persistence;

public sealed record ProfileMigrationIssue(string Path, string Message);

public sealed record JsonProfileMigrationPreview(
    string SourceApplicationDataDirectory,
    string TargetDatabasePath,
    bool TargetDatabaseAlreadyExists,
    IReadOnlyList<ProfileImportSnapshot> Profiles,
    string? LastActiveProfileId,
    IReadOnlyList<string> SourceFiles,
    IReadOnlyList<ProfileMigrationIssue> Issues)
{
    public int ProfileCount => Profiles.Count;
    public int DeckCount => Profiles.Sum(profile => profile.Decks.Count);
    public int CardCopyCount => Profiles.Sum(profile => profile.Profile.OwnedCards.Values.Sum());
    public bool CanMigrate => !TargetDatabaseAlreadyExists && Profiles.Count > 0 && Issues.Count == 0;
}

public sealed record JsonProfileMigrationResult(
    bool Success,
    string Message,
    string? BackupDirectory,
    int ImportedProfiles,
    int ImportedDecks,
    IReadOnlyList<ProfileMigrationIssue> Issues);

public sealed record JsonProfileBackupResult(
    bool Success,
    string Message,
    string? BackupDirectory,
    int BackedUpFiles,
    IReadOnlyList<ProfileMigrationIssue> Issues);

/// <summary>
/// Performs an explicit, one-way import from the existing JSON profile repository into a new,
/// empty SQLite database. The source files are copied and SHA-256 verified before any database
/// file is created. The importer never edits or deletes the JSON source.
/// </summary>
public sealed class JsonProfileMigrationService
{
    /// <summary>Creates a verified, read-only backup of the current JSON profile source without migrating it.</summary>
    public async Task<JsonProfileBackupResult> BackupJsonSourceAsync(
        string sourceApplicationDataDirectory,
        DateTimeOffset now,
        string? backupDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var sourceDirectory = Path.GetFullPath(sourceApplicationDataDirectory);
        var source = await ReadSourceAsync(sourceDirectory, cancellationToken);
        if (source.Issues.Count > 0 || source.SourceFiles.Count == 0)
        {
            return new JsonProfileBackupResult(
                false,
                "JSON profile backup was not started. Resolve the reported source issues first.",
                null,
                0,
                source.Issues);
        }

        var backupPath = backupDirectory is null
            ? DefaultBackupDirectory(sourceDirectory, now)
            : Path.GetFullPath(backupDirectory);
        try
        {
            await CreateVerifiedBackupAsync(sourceDirectory, source.SourceFiles, backupPath, now, cancellationToken);
            return new JsonProfileBackupResult(
                true,
                $"Verified JSON backup written to {backupPath}.",
                backupPath,
                source.SourceFiles.Count,
                source.Issues);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new JsonProfileBackupResult(false, $"JSON profile backup failed: {exception.Message}", backupPath, 0, source.Issues);
        }
    }

    public async Task<JsonProfileMigrationPreview> PreviewAsync(
        string sourceApplicationDataDirectory,
        string targetDatabasePath,
        CancellationToken cancellationToken = default)
    {
        var sourceDirectory = Path.GetFullPath(sourceApplicationDataDirectory);
        var targetPath = Path.GetFullPath(targetDatabasePath);
        var source = await ReadSourceAsync(sourceDirectory, cancellationToken);
        var issues = source.Issues.ToList();
        if (File.Exists(targetPath))
        {
            issues.Add(new ProfileMigrationIssue(targetPath, "The SQLite target already exists. Migration only imports into a new database and will not merge data."));
        }

        return new JsonProfileMigrationPreview(
            sourceDirectory,
            targetPath,
            File.Exists(targetPath),
            source.Profiles,
            source.LastActiveProfileId,
            source.SourceFiles,
            issues);
    }

    public async Task<JsonProfileMigrationResult> MigrateAsync(
        string sourceApplicationDataDirectory,
        string targetDatabasePath,
        DateTimeOffset now,
        string? backupDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var preview = await PreviewAsync(sourceApplicationDataDirectory, targetDatabasePath, cancellationToken);
        if (!preview.CanMigrate)
        {
            return new JsonProfileMigrationResult(
                false,
                "JSON profile migration was not started. Resolve the reported preview issues first.",
                null,
                0,
                0,
                preview.Issues);
        }

        var backupPath = backupDirectory is null
            ? DefaultBackupDirectory(preview.SourceApplicationDataDirectory, now)
            : Path.GetFullPath(backupDirectory);

        try
        {
            await CreateVerifiedBackupAsync(preview.SourceApplicationDataDirectory, preview.SourceFiles, backupPath, now, cancellationToken);

            using var store = new SqliteProfileStore(preview.TargetDatabasePath);
            var initialized = await store.InitializeAsync(cancellationToken);
            if (!initialized.Success)
            {
                DeleteNewDatabase(preview.TargetDatabasePath);
                return new JsonProfileMigrationResult(false, initialized.Message, backupPath, 0, 0, preview.Issues);
            }

            var imported = await store.ImportSnapshotsAsync(preview.Profiles, preview.LastActiveProfileId, now, cancellationToken);
            if (!imported.Success)
            {
                DeleteNewDatabase(preview.TargetDatabasePath);
                return new JsonProfileMigrationResult(false, imported.Message, backupPath, 0, 0, preview.Issues);
            }

            var verificationError = await VerifyImportAsync(store, preview, cancellationToken);
            if (verificationError is not null)
            {
                DeleteNewDatabase(preview.TargetDatabasePath);
                return new JsonProfileMigrationResult(false, verificationError, backupPath, 0, 0, preview.Issues);
            }

            return new JsonProfileMigrationResult(
                true,
                $"Imported {preview.ProfileCount} JSON profile{(preview.ProfileCount == 1 ? "" : "s")} after creating a verified backup.",
                backupPath,
                preview.ProfileCount,
                preview.DeckCount,
                preview.Issues);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            DeleteNewDatabase(preview.TargetDatabasePath);
            return new JsonProfileMigrationResult(false, $"JSON profile migration failed: {exception.Message}", backupPath, 0, 0, preview.Issues);
        }
    }

    private static async Task<MigrationSource> ReadSourceAsync(string sourceDirectory, CancellationToken cancellationToken)
    {
        var profiles = new List<ProfileImportSnapshot>();
        var sourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<ProfileMigrationIssue>();
        if (!Directory.Exists(sourceDirectory))
        {
            issues.Add(new ProfileMigrationIssue(sourceDirectory, "The JSON profile directory does not exist."));
            return new MigrationSource(profiles, null, sourceFiles.ToArray(), issues);
        }

        var profilesDirectory = Path.Combine(sourceDirectory, "profiles");
        var indexPath = Path.Combine(profilesDirectory, "index.json");
        if (File.Exists(indexPath))
        {
            AddSourceFile(sourceFiles, indexPath);
            LocalProfileIndex? index;
            try
            {
                index = JsonSerializer.Deserialize(await File.ReadAllTextAsync(indexPath, cancellationToken), PersistenceJsonContext.Default.LocalProfileIndex);
            }
            catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
            {
                issues.Add(new ProfileMigrationIssue(indexPath, $"The profile index could not be read: {exception.Message}"));
                return new MigrationSource(profiles, null, sourceFiles.ToArray(), issues);
            }

            if (index is null)
            {
                issues.Add(new ProfileMigrationIssue(indexPath, "The profile index is empty."));
                return new MigrationSource(profiles, null, sourceFiles.ToArray(), issues);
            }

            foreach (var summary in index.Profiles ?? [])
            {
                if (!IsSafeProfileId(summary.Id))
                {
                    issues.Add(new ProfileMigrationIssue(indexPath, $"Profile id '{summary.Id}' is not safe to import."));
                    continue;
                }

                var profilePath = Path.Combine(profilesDirectory, summary.Id, "profile.json");
                var snapshot = await ReadProfileSnapshotAsync(
                    summary.Id,
                    profilePath,
                    Path.Combine(profilesDirectory, summary.Id, "decks"),
                    summary.CreatedUtc,
                    summary.LastPlayedUtc,
                    sourceFiles,
                    issues,
                    cancellationToken);
                if (snapshot is not null)
                {
                    profiles.Add(snapshot);
                }
            }

            var lastActiveProfileId = profiles.Any(profile => profile.ProfileId.Equals(index.LastActiveProfileId, StringComparison.OrdinalIgnoreCase))
                ? index.LastActiveProfileId
                : null;
            return new MigrationSource(profiles, lastActiveProfileId, sourceFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(), issues);
        }

        var legacyProfilePath = Path.Combine(sourceDirectory, "profile.json");
        if (!File.Exists(legacyProfilePath))
        {
            issues.Add(new ProfileMigrationIssue(sourceDirectory, "No profile index or legacy profile.json file was found."));
            return new MigrationSource(profiles, null, sourceFiles.ToArray(), issues);
        }

        var legacySnapshot = await ReadProfileSnapshotAsync(
            LegacyProfileId(sourceDirectory),
            legacyProfilePath,
            Path.Combine(sourceDirectory, "decks"),
            FileTimestamp(File.GetCreationTimeUtc(legacyProfilePath)),
            FileTimestamp(File.GetLastWriteTimeUtc(legacyProfilePath)),
            sourceFiles,
            issues,
            cancellationToken);
        if (legacySnapshot is not null)
        {
            profiles.Add(legacySnapshot);
        }

        return new MigrationSource(profiles, legacySnapshot?.ProfileId, sourceFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(), issues);
    }

    private static async Task<ProfileImportSnapshot?> ReadProfileSnapshotAsync(
        string profileId,
        string profilePath,
        string decksDirectory,
        DateTimeOffset createdUtc,
        DateTimeOffset lastPlayedUtc,
        ISet<string> sourceFiles,
        ICollection<ProfileMigrationIssue> issues,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(profilePath))
        {
            issues.Add(new ProfileMigrationIssue(profilePath, "The profile data file is missing."));
            return null;
        }

        AddSourceFile(sourceFiles, profilePath);
        PlayerProfile profile;
        try
        {
            profile = PlayerProfileSerializer.FromJson(await File.ReadAllTextAsync(profilePath, cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
        {
            issues.Add(new ProfileMigrationIssue(profilePath, $"The profile data could not be read: {exception.Message}"));
            return null;
        }

        var decks = await ReadDecksAsync(decksDirectory, sourceFiles, issues, cancellationToken);
        return new ProfileImportSnapshot(
            profileId,
            profile,
            createdUtc == default ? FileTimestamp(File.GetCreationTimeUtc(profilePath)) : createdUtc,
            lastPlayedUtc == default ? FileTimestamp(File.GetLastWriteTimeUtc(profilePath)) : lastPlayedUtc,
            decks);
    }

    private static async Task<IReadOnlyList<DeckDefinition>> ReadDecksAsync(
        string decksDirectory,
        ISet<string> sourceFiles,
        ICollection<ProfileMigrationIssue> issues,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(decksDirectory))
        {
            return [];
        }

        var decks = new List<DeckDefinition>();
        foreach (var deckPath in Directory.EnumerateFiles(decksDirectory, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            AddSourceFile(sourceFiles, deckPath);
            try
            {
                var deck = JsonSerializer.Deserialize(await File.ReadAllTextAsync(deckPath, cancellationToken), PersistenceJsonContext.Default.DeckDefinition);
                if (deck is null || string.IsNullOrWhiteSpace(deck.Id) || string.IsNullOrWhiteSpace(deck.Name) || string.IsNullOrWhiteSpace(deck.ModeId) || deck.Cards is null)
                {
                    throw new JsonException("The deck is missing an id, name, mode, or cards.");
                }

                deck.Cards = deck.Cards
                    .Where(card => !string.IsNullOrWhiteSpace(card.Key) && card.Value > 0)
                    .OrderBy(card => card.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(card => card.Key.Trim(), card => card.Value, StringComparer.OrdinalIgnoreCase);
                decks.Add(deck);
            }
            catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
            {
                issues.Add(new ProfileMigrationIssue(deckPath, $"The deck could not be read: {exception.Message}"));
            }
        }

        return decks;
    }

    private static async Task CreateVerifiedBackupAsync(
        string sourceDirectory,
        IReadOnlyList<string> sourceFiles,
        string backupDirectory,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(backupDirectory))
        {
            throw new IOException("The requested JSON backup directory already exists.");
        }

        Directory.CreateDirectory(backupDirectory);
        var manifest = new List<BackupFileManifest>();
        foreach (var sourceFile in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            {
                throw new InvalidOperationException("A JSON profile source file falls outside the selected profile directory.");
            }

            var destinationFile = Path.Combine(backupDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite: false);
            var sourceHash = await FileSha256Async(sourceFile, cancellationToken);
            var backupHash = await FileSha256Async(destinationFile, cancellationToken);
            if (!sourceHash.Equals(backupHash, StringComparison.Ordinal))
            {
                throw new IOException($"The backup checksum did not match '{relativePath}'.");
            }

            manifest.Add(new BackupFileManifest(relativePath, sourceHash, new FileInfo(sourceFile).Length));
        }

        var manifestPath = Path.Combine(backupDirectory, "migration-manifest.json");
        var manifestJson = JsonSerializer.Serialize(new BackupManifest(now, manifest), PersistenceJsonContext.Default.BackupManifest);
        await File.WriteAllTextAsync(manifestPath, manifestJson, cancellationToken);
    }

    private static async Task<string?> VerifyImportAsync(SqliteProfileStore store, JsonProfileMigrationPreview preview, CancellationToken cancellationToken)
    {
        var importedProfiles = await store.ListProfilesAsync(cancellationToken);
        if (importedProfiles.Count != preview.ProfileCount || !importedProfiles.Select(profile => profile.Id).OrderBy(id => id).SequenceEqual(preview.Profiles.Select(profile => profile.ProfileId).OrderBy(id => id)))
        {
            return "SQLite verification failed because the imported profile set does not match the JSON source.";
        }

        foreach (var expected in preview.Profiles)
        {
            var loaded = await store.LoadProfileAsync(expected.ProfileId, cancellationToken);
            if (!loaded.Success || loaded.Value is null)
            {
                return $"SQLite verification failed because profile '{expected.ProfileId}' could not be read.";
            }

            var actual = loaded.Value.Profile;
            if (!actual.PlayerName.Equals(expected.Profile.PlayerName, StringComparison.Ordinal) ||
                actual.Experience != expected.Profile.Experience ||
                actual.Coins != expected.Profile.Coins ||
                !EquivalentCounts(actual.OwnedCards, expected.Profile.OwnedCards) ||
                !EquivalentCounts(actual.UnopenedPacks, expected.Profile.UnopenedPacks) ||
                !actual.OwnedStarterDeckIds.OrderBy(id => id).SequenceEqual(expected.Profile.OwnedStarterDeckIds.OrderBy(id => id), StringComparer.OrdinalIgnoreCase) ||
                !actual.CompletedTutorialIds.OrderBy(id => id).SequenceEqual(expected.Profile.CompletedTutorialIds.OrderBy(id => id), StringComparer.OrdinalIgnoreCase) ||
                !EquivalentQuestEntries(actual.Quests.Entries, expected.Profile.Quests.Entries))
            {
                return $"SQLite verification failed because profile '{expected.Profile.PlayerName}' does not match its JSON source.";
            }

            var actualDecks = await store.LoadDecksAsync(expected.ProfileId, cancellationToken);
            if (actualDecks.Count != expected.Decks.Count ||
                !actualDecks.OrderBy(deck => deck.Id).Select(deck => deck.Id).SequenceEqual(expected.Decks.OrderBy(deck => deck.Id).Select(deck => deck.Id), StringComparer.OrdinalIgnoreCase) ||
                actualDecks.Any(actualDeck =>
                {
                    var expectedDeck = expected.Decks.Single(deck => deck.Id.Equals(actualDeck.Id, StringComparison.OrdinalIgnoreCase));
                    return !actualDeck.Name.Equals(expectedDeck.Name, StringComparison.Ordinal) ||
                        !actualDeck.ModeId.Equals(expectedDeck.ModeId, StringComparison.Ordinal) ||
                        !EquivalentCounts(actualDeck.Cards, expectedDeck.Cards);
                }))
            {
                return $"SQLite verification failed because a deck for '{expected.Profile.PlayerName}' does not match its JSON source.";
            }
        }

        var activeProfileId = await store.GetLastActiveProfileIdAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(preview.LastActiveProfileId) && !string.Equals(activeProfileId, preview.LastActiveProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return "SQLite verification failed because the active profile does not match the JSON source.";
        }

        return null;
    }

    private static bool EquivalentCounts(IReadOnlyDictionary<string, int> left, IReadOnlyDictionary<string, int> right) =>
        left.Count == right.Count && left.All(entry => right.GetValueOrDefault(entry.Key) == entry.Value);

    private static bool EquivalentQuestEntries(IReadOnlyDictionary<string, QuestEntry> left, IReadOnlyDictionary<string, QuestEntry> right) =>
        left.Count == right.Count && left.All(entry =>
            right.TryGetValue(entry.Key, out var expected) &&
            expected.Progress == entry.Value.Progress &&
            expected.Completed == entry.Value.Completed);

    private static string DefaultBackupDirectory(string sourceDirectory, DateTimeOffset now)
    {
        return Path.Combine(sourceDirectory, "json-backups", $"migration-{now.UtcDateTime:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
    }

    private static string LegacyProfileId(string sourceDirectory)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sourceDirectory).ToUpperInvariant()));
        return new Guid(hash.AsSpan(0, 16)).ToString("N");
    }

    private static bool IsSafeProfileId(string? profileId) =>
        !string.IsNullOrWhiteSpace(profileId) && profileId.Length <= 32 && profileId.All(character => char.IsAsciiLetterOrDigit(character));

    private static DateTimeOffset FileTimestamp(DateTime timestamp) =>
        new(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));

    private static void AddSourceFile(ISet<string> sourceFiles, string path) => sourceFiles.Add(Path.GetFullPath(path));

    private static async Task<string> FileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static void DeleteNewDatabase(string databasePath)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record MigrationSource(
        IReadOnlyList<ProfileImportSnapshot> Profiles,
        string? LastActiveProfileId,
        IReadOnlyList<string> SourceFiles,
        IReadOnlyList<ProfileMigrationIssue> Issues);

}
