using DragonCards.Core;
using DragonCards.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DragonCards.Tests;

public sealed class SqliteProfileStoreTests
{
    [Fact]
    public async Task SqliteStoreMigratesAndRoundTripsProfileDeckAndAuditData()
    {
        var root = CreateTemporaryDirectory();
        var databasePath = Path.Combine(root, "dragoncards.db");
        try
        {
            using var store = new SqliteProfileStore(databasePath);
            Assert.True((await store.InitializeAsync()).Success);

            var created = await store.CreateProfileAsync(CreateProfile("Astra"), new DateTimeOffset(2026, 7, 11, 17, 30, 0, TimeSpan.Zero));

            Assert.True(created.Success, created.Message);
            var profile = Assert.IsType<StoredProfile>(created.Value);
            Assert.Equal(1, profile.Revision);
            Assert.Equal("Astra", profile.Profile.PlayerName);
            Assert.Equal(3, profile.Profile.OwnedCards["fire-cinder-adept"]);
            Assert.Equal("2026-W28", profile.Profile.Quests.WeeklyPeriod);
            Assert.Equal(profile.Id, await store.GetLastActiveProfileIdAsync());

            var saved = await store.SaveProfileAsync(profile.Id, profile.Profile with { Coins = 910 }, profile.Revision, new DateTimeOffset(2026, 7, 11, 18, 0, 0, TimeSpan.Zero));

            Assert.True(saved.Success, saved.Message);
            var revised = Assert.IsType<StoredProfile>(saved.Value);
            Assert.Equal(2, revised.Revision);
            Assert.Equal(910, revised.Profile.Coins);
            Assert.False((await store.SaveProfileAsync(profile.Id, revised.Profile, expectedRevision: 1, DateTimeOffset.UtcNow)).Success);

            var deck = new DeckDefinition
            {
                Id = "astra-fire",
                Name = "Astra Fire",
                ModeId = DragonCardsModeIds.DragonDuel,
                Cards = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["fire-cinder-adept"] = 3,
                    [BasicEnergy.CardId("Fire")] = 12
                }
            };
            var deckSaved = await store.SaveDeckAsync(profile.Id, deck, revised.Revision, DateTimeOffset.UtcNow);

            Assert.True(deckSaved.Success, deckSaved.Message);
            var loadedDeck = Assert.Single(await store.LoadDecksAsync(profile.Id));
            Assert.Equal(3, loadedDeck.Cards["fire-cinder-adept"]);
            Assert.Equal(12, loadedDeck.Cards[BasicEnergy.CardId("Fire")]);
            var audit = await store.ReadAuditAsync(profile.Id);
            Assert.Contains(audit, entry => entry.Kind == "profile.created");
            Assert.Contains(audit, entry => entry.Kind == "profile.saved");
            Assert.Contains(audit, entry => entry.Kind == "deck.saved");

            await using var db = DragonCardsDbContextFactory.Create(databasePath);
            Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), migration => migration.EndsWith("InitialProfileStore", StringComparison.Ordinal));
            Assert.Equal(1, await db.Profiles.CountAsync());
            Assert.Equal(2, await db.DeckCards.CountAsync());
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task SqliteStoreProtectsNamesAndCascadesProfileDeletion()
    {
        var root = CreateTemporaryDirectory();
        var databasePath = Path.Combine(root, "dragoncards.db");
        try
        {
            using var store = new SqliteProfileStore(databasePath);
            var first = await store.CreateProfileAsync(CreateProfile("Astra"), DateTimeOffset.UtcNow);
            Assert.True(first.Success, first.Message);
            var profile = Assert.IsType<StoredProfile>(first.Value);

            var duplicate = await store.CreateProfileAsync(CreateProfile("astra"), DateTimeOffset.UtcNow);
            Assert.False(duplicate.Success);
            Assert.Contains("unique", duplicate.Message, StringComparison.OrdinalIgnoreCase);

            Assert.True((await store.SaveDeckAsync(profile.Id, new DeckDefinition
            {
                Id = "delete-me",
                Name = "Delete Me",
                ModeId = DragonCardsModeIds.DragonDuel,
                Cards = new Dictionary<string, int> { ["fire-cinder-adept"] = 1 }
            }, profile.Revision, DateTimeOffset.UtcNow)).Success);
            Assert.True((await store.DeleteProfileAsync(profile.Id)).Success);
            Assert.Empty(await store.ListProfilesAsync());
            Assert.Null(await store.GetLastActiveProfileIdAsync());

            await using var db = DragonCardsDbContextFactory.Create(databasePath);
            Assert.Equal(0, await db.CardCopies.CountAsync());
            Assert.Equal(0, await db.Decks.CountAsync());
            Assert.Equal(0, await db.ProfileEvents.CountAsync());
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static PlayerProfile CreateProfile(string name)
    {
        var profile = new PlayerProfile
        {
            PlayerName = name,
            Experience = 2400,
            Coins = 800,
            DefaultRules = GameRulesConfig.ForPreset(GameRulesPreset.Standard, Playstyle.Ramp),
            SelectedStarterDeckId = "starter-fire",
            ActiveDeckId = "astra-fire",
            OwnedCards = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["fire-cinder-adept"] = 3
            },
            UnopenedPacks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [BoosterService.StandardBoosterId] = 2
            },
            OwnedStarterDeckIds = ["starter-fire"],
            CompletedTutorialIds = ["first-turn-basics"],
            Quests = new QuestProgressState
            {
                DailyPeriod = "2026-07-11",
                WeeklyPeriod = "2026-W28",
                Entries = new Dictionary<string, QuestEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    ["daily-energy-sources"] = new QuestEntry { Progress = 3, Completed = false }
                }
            }
        };
        profile.Normalize();
        return profile;
    }

    private static string CreateTemporaryDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "DragonCardsSqliteTests", Guid.NewGuid().ToString("N"));
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
