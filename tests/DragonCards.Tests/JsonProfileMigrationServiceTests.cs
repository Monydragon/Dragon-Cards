using DragonCards.Core;
using DragonCards.Persistence;

namespace DragonCards.Tests;

public sealed class JsonProfileMigrationServiceTests
{
    [Fact]
    public async Task MigrationBacksUpVerifiesAndImportsExistingJsonProfilesWithoutMutatingSource()
    {
        var root = CreateTemporaryDirectory();
        var jsonRoot = Path.Combine(root, "json-source");
        var databasePath = Path.Combine(root, "sqlite", "dragoncards.db");
        var backupPath = Path.Combine(root, "json-backup");
        try
        {
            var (firstId, secondId) = CreateJsonProfiles(jsonRoot);
            var firstProfilePath = Path.Combine(jsonRoot, "profiles", firstId, "profile.json");
            var sourceBefore = await File.ReadAllTextAsync(firstProfilePath);
            var service = new JsonProfileMigrationService();

            var preview = await service.PreviewAsync(jsonRoot, databasePath);

            Assert.True(preview.CanMigrate);
            Assert.Equal(2, preview.ProfileCount);
            Assert.Equal(1, preview.DeckCount);
            Assert.Equal(3, preview.CardCopyCount);
            Assert.Equal(firstId, preview.LastActiveProfileId);
            Assert.NotEmpty(preview.SourceFiles);

            var migrated = await service.MigrateAsync(jsonRoot, databasePath, new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero), backupPath);

            Assert.True(migrated.Success, migrated.Message);
            Assert.Equal(backupPath, migrated.BackupDirectory);
            Assert.True(File.Exists(Path.Combine(backupPath, "migration-manifest.json")));
            Assert.Equal(sourceBefore, await File.ReadAllTextAsync(firstProfilePath));
            Assert.Equal(sourceBefore, await File.ReadAllTextAsync(Path.Combine(backupPath, "profiles", firstId, "profile.json")));

            var sourceBackup = await service.BackupJsonSourceAsync(jsonRoot, new DateTimeOffset(2026, 7, 12, 1, 30, 0, TimeSpan.Zero));
            Assert.True(sourceBackup.Success, sourceBackup.Message);
            Assert.Equal(4, sourceBackup.BackedUpFiles);
            Assert.NotNull(sourceBackup.BackupDirectory);
            Assert.StartsWith(Path.Combine(jsonRoot, "json-backups"), sourceBackup.BackupDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(sourceBackup.BackupDirectory!, "migration-manifest.json")));

            using var store = new SqliteProfileStore(databasePath);
            Assert.Equal(firstId, await store.GetLastActiveProfileIdAsync());
            var profiles = await store.ListProfilesAsync();
            Assert.Equal(new[] { firstId, secondId }.OrderBy(id => id), profiles.Select(profile => profile.Id).OrderBy(id => id));
            var first = await store.LoadProfileAsync(firstId);
            Assert.True(first.Success, first.Message);
            Assert.Equal(3, first.Value!.Profile.OwnedCards["fire-cinder-adept"]);
            Assert.Equal(2, first.Value.Profile.UnopenedPacks[BoosterService.StandardBoosterId]);
            var deck = Assert.Single(await store.LoadDecksAsync(firstId));
            Assert.Equal(12, deck.Cards[BasicEnergy.CardId("Fire")]);
            Assert.Contains(await store.ReadAuditAsync(firstId), entry => entry.Kind == "profile.imported");
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task MigrationRefusesExistingTargetBeforeCreatingABackupOrChangingJsonSource()
    {
        var root = CreateTemporaryDirectory();
        var jsonRoot = Path.Combine(root, "json-source");
        var databasePath = Path.Combine(root, "sqlite", "dragoncards.db");
        var backupPath = Path.Combine(root, "json-backup");
        try
        {
            var (firstId, _) = CreateJsonProfiles(jsonRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            await File.WriteAllTextAsync(databasePath, "already exists");
            var sourcePath = Path.Combine(jsonRoot, "profiles", firstId, "profile.json");
            var sourceBefore = await File.ReadAllTextAsync(sourcePath);

            var result = await new JsonProfileMigrationService().MigrateAsync(jsonRoot, databasePath, DateTimeOffset.UtcNow, backupPath);

            Assert.False(result.Success);
            Assert.False(Directory.Exists(backupPath));
            Assert.Equal("already exists", await File.ReadAllTextAsync(databasePath));
            Assert.Equal(sourceBefore, await File.ReadAllTextAsync(sourcePath));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static (string FirstId, string SecondId) CreateJsonProfiles(string jsonRoot)
    {
        var repository = new LocalProfileRepository(jsonRoot);
        Assert.True(repository.Initialize(out _, out var initializationError), initializationError);
        var now = new DateTimeOffset(2026, 7, 11, 16, 0, 0, TimeSpan.Zero);
        var first = new PlayerProfile
        {
            PlayerName = "Astra",
            Experience = 1800,
            Coins = 750,
            OwnedCards = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["fire-cinder-adept"] = 3 },
            UnopenedPacks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [BoosterService.StandardBoosterId] = 2 },
            OwnedStarterDeckIds = ["starter-fire"],
            CompletedTutorialIds = ["first-turn-basics"],
            Quests = new QuestProgressState
            {
                DailyPeriod = "2026-07-11",
                WeeklyPeriod = "2026-W28",
                Entries = new Dictionary<string, QuestEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    ["daily-energy-sources"] = new QuestEntry { Progress = 4, Completed = false }
                }
            }
        };
        first.Normalize();
        Assert.True(repository.TryCreateProfile(first, now, out var firstSummary, out var firstError), firstError);
        Assert.NotNull(firstSummary);
        Assert.True(repository.TrySaveDeck(firstSummary!.Id, new DeckDefinition
        {
            Id = "astra-fire",
            Name = "Astra Fire",
            ModeId = DragonCardsModeIds.DragonDuel,
            Cards = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["fire-cinder-adept"] = 3,
                [BasicEnergy.CardId("Fire")] = 12
            }
        }, out var deckError), deckError);

        var second = new PlayerProfile { PlayerName = "Bryn", Coins = 200 };
        second.Normalize();
        Assert.True(repository.TryCreateProfile(second, now.AddMinutes(5), out var secondSummary, out var secondError), secondError);
        Assert.NotNull(secondSummary);
        Assert.True(repository.TrySelectProfile(firstSummary.Id, now.AddMinutes(10), out var selectError), selectError);
        return (firstSummary.Id, secondSummary!.Id);
    }

    private static string CreateTemporaryDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "DragonCardsJsonMigrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTemporaryDirectory(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
