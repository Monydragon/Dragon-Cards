using DragonCards.Core;
using DragonCards.Persistence;

namespace DragonCards.Tests;

public sealed class ProfileDataServicesTests
{
    [Fact]
    public async Task ExportImportAndDatabaseRestoreKeepUserDataTransparentAndRecoverable()
    {
        var root = CreateTemporaryDirectory();
        var databasePath = Path.Combine(root, "dragoncards.db");
        var exportPath = Path.Combine(root, "exports", "astra.json");
        try
        {
            using var store = new SqliteProfileStore(databasePath);
            var created = await store.CreateProfileAsync(new PlayerProfile
            {
                PlayerName = "Astra",
                Coins = 400,
                OwnedCards = new Dictionary<string, int> { ["fire-cinder-adept"] = 3 },
                UnopenedPacks = new Dictionary<string, int> { [BoosterService.StandardBoosterId] = 2 }
            }, DateTimeOffset.UtcNow);
            var profile = Assert.IsType<StoredProfile>(created.Value);
            Assert.True((await store.SaveDeckAsync(profile.Id, new DeckDefinition
            {
                Id = "astra-fire",
                Name = "Astra Fire",
                ModeId = DragonCardsModeIds.DragonDuel,
                Cards = new Dictionary<string, int> { ["fire-cinder-adept"] = 3 }
            }, profile.Revision, DateTimeOffset.UtcNow)).Success);

            var transfer = new ProfileDataTransferService();
            var exported = await transfer.ExportAsync(store, profile.Id, exportPath, DateTimeOffset.UtcNow);
            Assert.True(exported.Success, exported.Message);
            var preview = await transfer.PreviewImportAsync(exportPath);
            Assert.True(preview.IsValid, preview.Message);
            Assert.Equal(3, preview.CardCopies);
            Assert.Equal(1, preview.DeckCount);

            var imported = await transfer.ImportAsNewProfileAsync(store, exportPath, DateTimeOffset.UtcNow);
            Assert.True(imported.Success, imported.Message);
            Assert.NotNull(imported.ImportedProfile);
            Assert.Equal("Astra Import", imported.ImportedProfile!.Profile.PlayerName);
            Assert.Single(await store.LoadDecksAsync(imported.ImportedProfile.Id));

            var backup = await transfer.CreateDatabaseBackupAsync(databasePath, Path.Combine(root, "backups"), DateTimeOffset.UtcNow);
            Assert.True(backup.Success, backup.Message);
            Assert.NotNull(backup.Value);
            var verification = await transfer.VerifyDatabaseAsync(databasePath);
            Assert.True(verification.IsValid, verification.Message);
            Assert.Equal(2, verification.ProfileCount);
            Assert.Equal(2, verification.CardCopyCount);
            Assert.Equal(2, verification.DeckCount);
            Assert.Contains(verification.AppliedMigrations, migration => migration.EndsWith("InitialProfileStore", StringComparison.Ordinal));
            Assert.Contains(verification.AppliedMigrations, migration => migration.EndsWith("AddProfileSeedRuns", StringComparison.Ordinal));
            var current = await store.LoadProfileAsync(profile.Id);
            Assert.True((await store.SaveProfileAsync(profile.Id, current.Value!.Profile with { Coins = 9999 }, current.Value.Revision, DateTimeOffset.UtcNow)).Success);
            var restored = await transfer.RestoreDatabaseBackupAsync(databasePath, backup.Value!, Path.Combine(root, "safety"), DateTimeOffset.UtcNow);
            Assert.True(restored.Success, restored.Message);
            using var recovered = new SqliteProfileStore(databasePath);
            var recoveredProfile = await recovered.LoadProfileAsync(profile.Id);
            Assert.Equal(400, recoveredProfile.Value!.Profile.Coins);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void SeedPreviewsAreStableAndRecordedAsVersionedInputs()
    {
        var source = new PlayerProfile { PlayerName = "Astra", Coins = 50 };
        var data = GameData.LoadDefault();
        var request = new ProfileSeedRequest(424242, ProfileSeedScenario.Demo, new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero));
        var seeds = new ProfileSeedService();

        var first = seeds.CreatePreview(source, data, request);
        var second = seeds.CreatePreview(source, data, request);
        var different = seeds.CreatePreview(source, data, request with { Seed = 424243 });

        Assert.Equal(ProfileSeedService.AlgorithmVersion, first.AlgorithmVersion);
        Assert.Equal(first.GrantedCardIds, second.GrantedCardIds);
        Assert.Equal(first.Profile.OwnedCards.OrderBy(entry => entry.Key), second.Profile.OwnedCards.OrderBy(entry => entry.Key));
        Assert.NotEqual(first.GrantedCardIds, different.GrantedCardIds);
    }

    [Fact]
    public void SqliteRepositoryImplementsTheDesktopProfileContractWithoutJsonWrites()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var repository = new SqliteProfileRepository(root);
            Assert.True(repository.Initialize(out var migrated, out var error), error);
            Assert.False(migrated);
            Assert.Equal(ProfileStorageKind.Sqlite, repository.StorageKind);
            Assert.True(repository.TryCreateProfile(new PlayerProfile { PlayerName = "Astra" }, DateTimeOffset.UtcNow, out var summary, out error), error);
            Assert.NotNull(summary);
            Assert.True(File.Exists(Path.Combine(root, SqliteProfileRepository.DatabaseFileName)));
            Assert.False(File.Exists(Path.Combine(root, "profiles", "index.json")));
            Assert.True(repository.TryLoadProfile(summary!.Id, out var profile, out error), error);
            Assert.Equal("Astra", profile!.PlayerName);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "DragonCardsProfileDataTests", Guid.NewGuid().ToString("N"));
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
