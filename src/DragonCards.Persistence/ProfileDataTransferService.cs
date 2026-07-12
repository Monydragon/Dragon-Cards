using System.Security.Cryptography;
using System.Text.Json;
using DragonCards.Core;
using Microsoft.Data.Sqlite;

namespace DragonCards.Persistence;

public sealed record ProfileDataExport
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset ExportedUtc { get; init; }
    public string SourceProfileId { get; init; } = "";
    public int SourceRevision { get; init; }
    public PlayerProfile Profile { get; init; } = new();
    public IReadOnlyList<DeckDefinition> Decks { get; init; } = [];
    public string Checksum { get; init; } = "";
}

public sealed record ProfileDataImportPreview(
    bool IsValid,
    string Message,
    ProfileDataExport? Export,
    int CardCopies,
    int DeckCount);

public sealed record ProfileDataImportResult(bool Success, string Message, StoredProfile? ImportedProfile = null);

public sealed record ProfileDatabaseVerification(
    bool IsValid,
    string Message,
    IReadOnlyList<string> AppliedMigrations,
    int ProfileCount,
    int CardCopyCount,
    int DeckCount);

/// <summary>Transparent, checksummed profile export/import and consistent SQLite backup helpers.</summary>
public sealed class ProfileDataTransferService
{
    public const string ExportPrefix = "DCP1";

    /// <summary>Runs SQLite integrity checks and reports the active schema and persisted totals.</summary>
    public Task<ProfileDatabaseVerification> VerifyDatabaseAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath))
        {
            return Task.FromResult(new ProfileDatabaseVerification(false, "The SQLite profile database does not exist.", [], 0, 0, 0));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = OpenConnection(databasePath);
            using var integrityCommand = connection.CreateCommand();
            integrityCommand.CommandText = "PRAGMA integrity_check;";
            var integrity = integrityCommand.ExecuteScalar()?.ToString();
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new ProfileDatabaseVerification(false, $"SQLite integrity_check returned '{integrity ?? "no result"}'.", [], 0, 0, 0));
            }

            var migrations = new List<string>();
            using (var migrationCommand = connection.CreateCommand())
            {
                migrationCommand.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;";
                using var reader = migrationCommand.ExecuteReader();
                while (reader.Read())
                {
                    migrations.Add(reader.GetString(0));
                }
            }

            var profileCount = ReadCount(connection, "Profiles");
            var cardCopyCount = ReadCount(connection, "CardCopies");
            var deckCount = ReadCount(connection, "Decks");
            return Task.FromResult(new ProfileDatabaseVerification(
                true,
                $"SQLite integrity check passed: {profileCount} profile{(profileCount == 1 ? "" : "s")}, {cardCopyCount} card-copy rows, {deckCount} deck{(deckCount == 1 ? "" : "s")}, and {migrations.Count} applied migration{(migrations.Count == 1 ? "" : "s") }.",
                migrations,
                profileCount,
                cardCopyCount,
                deckCount));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            return Task.FromResult(new ProfileDatabaseVerification(false, $"SQLite verification failed: {exception.Message}", [], 0, 0, 0));
        }
    }

    public async Task<ProfileStoreResult<string>> ExportAsync(
        SqliteProfileStore store,
        string profileId,
        string destinationPath,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var profileResult = await store.LoadProfileAsync(profileId, cancellationToken);
        if (!profileResult.Success || profileResult.Value is null)
        {
            return ProfileStoreResult<string>.Fail(profileResult.Message);
        }

        var decks = await store.LoadDecksAsync(profileId, cancellationToken);
        var export = NormalizeExport(new ProfileDataExport
        {
            ExportedUtc = now,
            SourceProfileId = profileResult.Value.Id,
            SourceRevision = profileResult.Value.Revision,
            Profile = profileResult.Value.Profile,
            Decks = decks
        });
        export = export with { Checksum = ComputeChecksum(export) };

        try
        {
            var fullPath = Path.GetFullPath(destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(export, PersistenceJsonContext.Default.ProfileDataExport), cancellationToken);
            File.Move(temporaryPath, fullPath, overwrite: false);
            return ProfileStoreResult<string>.Ok(fullPath, $"Profile export written to {fullPath}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ProfileStoreResult<string>.Fail($"The profile export could not be written: {exception.Message}");
        }
    }

    public async Task<ProfileDataImportPreview> PreviewImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            return new ProfileDataImportPreview(false, "The selected profile export does not exist.", null, 0, 0);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(await File.ReadAllTextAsync(sourcePath, cancellationToken), PersistenceJsonContext.Default.ProfileDataExport);
            if (parsed is null || parsed.SchemaVersion != 1 || string.IsNullOrWhiteSpace(parsed.Checksum))
            {
                return new ProfileDataImportPreview(false, "The profile export format is unsupported.", null, 0, 0);
            }

            var normalized = NormalizeExport(parsed);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(parsed.Checksum), Convert.FromHexString(ComputeChecksum(normalized))))
            {
                return new ProfileDataImportPreview(false, "The profile export checksum does not match its contents.", null, 0, 0);
            }

            return new ProfileDataImportPreview(
                true,
                $"Import preview: {normalized.Profile.PlayerName}, {normalized.Profile.OwnedCards.Count} distinct cards, and {normalized.Decks.Count} deck{(normalized.Decks.Count == 1 ? "" : "s")}.",
                normalized,
                normalized.Profile.OwnedCards.Values.Sum(),
                normalized.Decks.Count);
        }
        catch (Exception exception) when (exception is IOException or JsonException or FormatException or NotSupportedException)
        {
            return new ProfileDataImportPreview(false, $"The profile export could not be read: {exception.Message}", null, 0, 0);
        }
    }

    public async Task<ProfileDataImportResult> ImportAsNewProfileAsync(
        SqliteProfileStore store,
        string sourcePath,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var preview = await PreviewImportAsync(sourcePath, cancellationToken);
        if (!preview.IsValid || preview.Export is null)
        {
            return new ProfileDataImportResult(false, preview.Message);
        }

        var profile = CloneProfile(preview.Export.Profile);
        var profiles = await store.ListProfilesAsync(cancellationToken);
        profile.PlayerName = UniqueImportName(profile.PlayerName, profiles.Select(item => item.DisplayName));
        var result = await store.CreateProfileWithDecksAsync(profile, preview.Export.Decks, now, cancellationToken);
        return result.Success && result.Value is not null
            ? new ProfileDataImportResult(true, $"Imported '{result.Value.Profile.PlayerName}' as a separate local profile.", result.Value)
            : new ProfileDataImportResult(false, result.Message);
    }

    public async Task<ProfileStoreResult<string>> CreateDatabaseBackupAsync(
        string databasePath,
        string backupDirectory,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath))
        {
            return ProfileStoreResult<string>.Fail("The SQLite profile database does not exist.");
        }

        try
        {
            Directory.CreateDirectory(backupDirectory);
            var destinationPath = Path.Combine(backupDirectory, $"dragoncards-{now.UtcDateTime:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
            cancellationToken.ThrowIfCancellationRequested();
            using var source = OpenConnection(databasePath);
            using var destination = OpenConnection(destinationPath);
            source.BackupDatabase(destination);
            return ProfileStoreResult<string>.Ok(destinationPath, $"SQLite backup written to {destinationPath}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            return ProfileStoreResult<string>.Fail($"The SQLite backup could not be created: {exception.Message}");
        }
    }

    public async Task<ProfileStoreResult<string>> RestoreDatabaseBackupAsync(
        string databasePath,
        string backupPath,
        string safetyBackupDirectory,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupPath))
        {
            return ProfileStoreResult<string>.Fail("The selected SQLite backup does not exist.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var validation = OpenConnection(backupPath))
            using (var command = validation.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check;";
                if (!string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                {
                    return ProfileStoreResult<string>.Fail("The selected SQLite backup did not pass integrity_check.");
                }
            }

            var safety = await CreateDatabaseBackupAsync(databasePath, safetyBackupDirectory, now, cancellationToken);
            if (!safety.Success || safety.Value is null)
            {
                return ProfileStoreResult<string>.Fail($"Restore was not started because the current database could not be backed up: {safety.Message}");
            }

            using var source = OpenConnection(backupPath);
            using var destination = OpenConnection(databasePath);
            source.BackupDatabase(destination);
            return ProfileStoreResult<string>.Ok(safety.Value, $"Database restored. Safety backup: {safety.Value}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            return ProfileStoreResult<string>.Fail($"The SQLite backup could not be restored: {exception.Message}");
        }
    }

    private static ProfileDataExport NormalizeExport(ProfileDataExport export)
    {
        var profile = CloneProfile(export.Profile);
        var decks = export.Decks
            .Where(deck => !string.IsNullOrWhiteSpace(deck.Id) && !string.IsNullOrWhiteSpace(deck.Name) && !string.IsNullOrWhiteSpace(deck.ModeId))
            .OrderBy(deck => deck.Id, StringComparer.OrdinalIgnoreCase)
            .Select(deck => new DeckDefinition
            {
                Id = deck.Id.Trim(),
                Name = deck.Name.Trim(),
                ModeId = deck.ModeId.Trim(),
                Cards = deck.Cards.Where(card => !string.IsNullOrWhiteSpace(card.Key) && card.Value > 0)
                    .OrderBy(card => card.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(card => card.Key.Trim(), card => card.Value, StringComparer.OrdinalIgnoreCase)
            })
            .ToArray();
        return export with
        {
            Profile = profile,
            Decks = decks,
            Checksum = ""
        };
    }

    private static PlayerProfile CloneProfile(PlayerProfile profile) =>
        PlayerProfileSerializer.FromJson(PlayerProfileSerializer.ToJson(profile));

    private static string ComputeChecksum(ProfileDataExport export)
    {
        var canonical = NormalizeExport(export) with { Checksum = "" };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical, PersistenceJsonContext.Default.ProfileDataExport)));
    }

    private static SqliteConnection OpenConnection(string path)
    {
        var connection = new SqliteConnection($"Data Source={Path.GetFullPath(path)};Foreign Keys=True");
        connection.Open();
        return connection;
    }

    private static int ReadCount(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string UniqueImportName(string original, IEnumerable<string> existingNames)
    {
        var existing = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var suffix = 1; suffix < 1000; suffix++)
        {
            var suffixText = suffix == 1 ? " Import" : $" Import {suffix}";
            var prefixLength = Math.Max(1, 18 - suffixText.Length);
            var candidate = $"{original.Trim()[..Math.Min(prefixLength, original.Trim().Length)]}{suffixText}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"Import-{Guid.NewGuid():N}"[..18];
    }
}
