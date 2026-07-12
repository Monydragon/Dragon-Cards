using DragonCards.Core;
using DragonCards.Persistence;
using Microsoft.Xna.Framework;

namespace DragonCards.Desktop;

public sealed partial class DragonCardsGame
{
    private JsonProfileMigrationPreview? _sqliteMigrationPreview;
    private bool _sqliteMigrationConfirmation;
    private bool _profileRestoreConfirmation;
    private long _profileSeedValue = 2026071201;
    private ProfileSeedPreview? _profileSeedPreview;
    private IReadOnlyList<ProfileAuditEntry> _profileDataAudit = [];
    private IReadOnlyList<ProfileSeedRun> _profileSeedRuns = [];
    private string _profileDataNotice = "";
    private int _profileDataCardOffset;

    private static string ProfileDatabasePath => Path.Combine(ProfileDataDirectory, SqliteProfileRepository.DatabaseFileName);
    private static string ProfileExportDirectory => Path.Combine(ProfileDataDirectory, "exports");
    private static string ProfileImportPath => Path.Combine(ProfileDataDirectory, "imports", "profile-import.json");
    private static string ProfileBackupDirectory => Path.Combine(ProfileDataDirectory, "backups");
    private static string ProfileSafetyBackupDirectory => Path.Combine(ProfileDataDirectory, "safety-backups");

    private void DrawProfileData()
    {
        DrawText("Profile Data", new Vector2(54, 108), Color.White, 1.2f);
        DrawText("Inspect and control local profile data. Exports are readable JSON; SQLite actions never contact a match peer.", new Vector2(56, 146), new Color(196, 207, 220), 0.64f);
        if (_profile is null || string.IsNullOrWhiteSpace(_activeProfileId) || _profileRepository is null)
        {
            DrawText("Select a local profile before opening its data workspace.", new Vector2(56, 218), new Color(255, 190, 120), 0.76f);
            if (Button(new Rectangle(56, 276, 190, 46), "Profiles"))
            {
                BeginNewGame();
            }
            return;
        }

        var profile = _profile;
        var deckErrors = Array.Empty<ProfileRepositoryError>();
        var decks = _profileRepository.LoadDecks(_activeProfileId, out var errors);
        deckErrors = errors.ToArray();
        DrawProfileDataSummary(profile, decks, new Rectangle(54, 198, 498, 254));
        DrawProfileDataCollection(profile, new Rectangle(54, 474, 498, 324));
        DrawProfileDataDetails(profile, decks, new Rectangle(578, 198, 480, 600));
        DrawProfileDataActions(profile, new Rectangle(1084, 198, 460, 600));

        if (deckErrors.Length > 0)
        {
            _profileDataNotice = deckErrors[0].Message;
        }
        if (!string.IsNullOrWhiteSpace(_profileDataNotice))
        {
            DrawText(_profileDataNotice, new Rectangle(56, 820, 1220, 42), _profileDataNotice.Contains("could not", StringComparison.OrdinalIgnoreCase) || _profileDataNotice.Contains("failed", StringComparison.OrdinalIgnoreCase) ? new Color(255, 190, 120) : new Color(148, 224, 164), 0.5f);
        }
        if (Button(new Rectangle(1362, 816, 162, 42), "Back"))
        {
            _screen = Screen.MainMenu;
            _status = "Returned to main menu.";
        }
    }

    private void DrawProfileDataSummary(PlayerProfile profile, IReadOnlyList<DeckDefinition> decks, Rectangle panel)
    {
        DrawPanel(panel, new Color(31, 37, 46), border: new Color(81, 96, 116));
        DrawText("Identity & Progression", new Vector2(panel.X + 24, panel.Y + 20), Color.White, 0.82f);
        var storage = _profileRepository!.StorageKind == ProfileStorageKind.Sqlite ? "SQLite (active)" : "JSON (active)";
        var storageColor = _profileRepository.StorageKind == ProfileStorageKind.Sqlite ? new Color(148, 224, 164) : new Color(255, 210, 132);
        DrawText(profile.PlayerName, new Vector2(panel.X + 24, panel.Y + 62), Color.White, 0.94f);
        DrawText(storage, new Vector2(panel.X + 24, panel.Y + 98), storageColor, 0.64f);
        DrawText($"Profile ID: {_activeProfileId}", new Rectangle(panel.X + 24, panel.Y + 132, panel.Width - 48, 24), new Color(196, 207, 220), 0.48f);
        DrawText($"Level {profile.Level}  XP {profile.Experience}  Coins {profile.Coins}", new Vector2(panel.X + 24, panel.Y + 166), new Color(244, 230, 158), 0.64f);
        DrawText($"Packs {profile.TotalUnopenedPacks}  Decks {decks.Count}  Tutorials {profile.CompletedTutorialIds.Count}", new Vector2(panel.X + 24, panel.Y + 202), new Color(196, 207, 220), 0.58f);
    }

    private void DrawProfileDataCollection(PlayerProfile profile, Rectangle panel)
    {
        DrawPanel(panel, new Color(31, 37, 46), border: new Color(81, 96, 116));
        DrawText("Collection (local data)", new Vector2(panel.X + 24, panel.Y + 18), Color.White, 0.78f);
        DrawText($"{profile.OwnedCards.Count} distinct / {profile.OwnedCards.Values.Sum()} total owned copies", new Vector2(panel.X + 24, panel.Y + 52), new Color(148, 224, 164), 0.56f);
        var cards = profile.OwnedCards
            .OrderBy(entry => _data.CardsById.TryGetValue(entry.Key, out var card) ? card.Name : entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _profileDataCardOffset = Math.Clamp(_profileDataCardOffset, 0, Math.Max(0, cards.Length - 10));
        for (var row = 0; row < 10 && _profileDataCardOffset + row < cards.Length; row++)
        {
            var entry = cards[_profileDataCardOffset + row];
            var name = _data.CardsById.TryGetValue(entry.Key, out var card) ? card.Name : entry.Key;
            DrawText($"{name} x{entry.Value}", new Rectangle(panel.X + 24, panel.Y + 84 + row * 20, panel.Width - 48, 18), new Color(205, 214, 225), 0.48f);
        }

        if (cards.Length > 10)
        {
            if (Button(new Rectangle(panel.X + 24, panel.Bottom - 48, 108, 32), "Prev Cards", _profileDataCardOffset > 0))
            {
                _profileDataCardOffset = Math.Max(0, _profileDataCardOffset - 10);
            }
            if (Button(new Rectangle(panel.X + 146, panel.Bottom - 48, 108, 32), "Next Cards", _profileDataCardOffset + 10 < cards.Length))
            {
                _profileDataCardOffset = Math.Min(Math.Max(0, cards.Length - 10), _profileDataCardOffset + 10);
            }
            DrawText($"{_profileDataCardOffset + 1}-{Math.Min(cards.Length, _profileDataCardOffset + 10)} / {cards.Length}", new Vector2(panel.X + 276, panel.Bottom - 40), new Color(187, 199, 214), 0.48f);
        }
    }

    private void DrawProfileDataDetails(PlayerProfile profile, IReadOnlyList<DeckDefinition> decks, Rectangle panel)
    {
        DrawPanel(panel, new Color(34, 41, 51), border: new Color(84, 99, 119));
        DrawText("Rules, Decks & Activity", new Vector2(panel.X + 24, panel.Y + 20), Color.White, 0.8f);
        DrawText($"Rules: {profile.DefaultRules.Preset} / {profile.DefaultRules.Playstyle}", new Vector2(panel.X + 24, panel.Y + 58), new Color(196, 207, 220), 0.56f);
        DrawText($"Active deck: {profile.ActiveDeckId}", new Rectangle(panel.X + 24, panel.Y + 84, panel.Width - 48, 22), new Color(196, 207, 220), 0.5f);
        DrawText($"Starter ownership: {string.Join(", ", profile.OwnedStarterDeckIds.DefaultIfEmpty("none"))}", new Rectangle(panel.X + 24, panel.Y + 112, panel.Width - 48, 38), new Color(196, 207, 220), 0.48f);
        DrawText($"Quests: {profile.Quests.Entries.Count} entries; daily {profile.Quests.DailyPeriod}; weekly {profile.Quests.WeeklyPeriod}", new Rectangle(panel.X + 24, panel.Y + 154, panel.Width - 48, 42), new Color(196, 207, 220), 0.48f);
        DrawText("Saved Decks", new Vector2(panel.X + 24, panel.Y + 212), new Color(244, 230, 158), 0.62f);
        foreach (var (deck, index) in decks.Take(5).Select((deck, index) => (deck, index)))
        {
            DrawText($"{deck.Name}  {deck.Count} cards  {deck.ModeId}", new Rectangle(panel.X + 24, panel.Y + 242 + index * 22, panel.Width - 48, 20), new Color(205, 214, 225), 0.48f);
        }

        if (_profileRepository!.StorageKind == ProfileStorageKind.Sqlite)
        {
            RefreshProfileDataReadModel();
            DrawText("Audit Trail", new Vector2(panel.X + 24, panel.Y + 370), new Color(244, 230, 158), 0.62f);
            foreach (var (entry, index) in _profileDataAudit.Take(5).Select((entry, index) => (entry, index)))
            {
                DrawText($"{entry.OccurredUtc.ToLocalTime():g}  {entry.Kind}: {entry.Summary}", new Rectangle(panel.X + 24, panel.Y + 400 + index * 28, panel.Width - 48, 26), new Color(187, 199, 214), 0.44f);
            }
            if (_profileSeedRuns.Count > 0)
            {
                var seed = _profileSeedRuns[0];
                DrawText($"Latest seed: {seed.Scenario} / {seed.Seed}", new Rectangle(panel.X + 24, panel.Y + 550, panel.Width - 48, 22), new Color(148, 224, 164), 0.48f);
            }
        }
    }

    private void DrawProfileDataActions(PlayerProfile profile, Rectangle panel)
    {
        DrawPanel(panel, new Color(34, 41, 51), border: new Color(84, 99, 119));
        DrawText("Storage Controls", new Vector2(panel.X + 24, panel.Y + 20), Color.White, 0.8f);
        if (_profileRepository!.StorageKind == ProfileStorageKind.Json)
        {
            DrawJsonMigrationControls(panel);
            return;
        }

        DrawSqliteDataControls(profile, panel);
    }

    private void DrawJsonMigrationControls(Rectangle panel)
    {
        DrawText("JSON remains active until you opt in.", new Rectangle(panel.X + 24, panel.Y + 58, panel.Width - 48, 36), new Color(255, 210, 132), 0.56f);
        if (_sqliteMigrationPreview is null)
        {
            if (Button(new Rectangle(panel.X + 24, panel.Y + 112, 250, 42), "Preview SQLite Migration"))
            {
                try
                {
                    _sqliteMigrationPreview = new JsonProfileMigrationService().PreviewAsync(ProfileDataDirectory, ProfileDatabasePath).GetAwaiter().GetResult();
                    _profileDataNotice = _sqliteMigrationPreview.CanMigrate
                        ? $"Ready: {_sqliteMigrationPreview.ProfileCount} profiles, {_sqliteMigrationPreview.DeckCount} decks, {_sqliteMigrationPreview.CardCopyCount} card copies."
                        : _sqliteMigrationPreview.Issues.FirstOrDefault()?.Message ?? "SQLite migration preview needs attention.";
                }
                catch (Exception exception)
                {
                    _profileDataNotice = $"SQLite migration preview failed: {exception.Message}";
                }
            }
            DrawText("Preview verifies the source and target first. Migration makes a SHA-256 JSON backup before creating SQLite.", new Rectangle(panel.X + 24, panel.Y + 176, panel.Width - 48, 72), new Color(196, 207, 220), 0.5f);
            return;
        }

        var preview = _sqliteMigrationPreview;
        DrawText($"Profiles {preview.ProfileCount}  Decks {preview.DeckCount}  Copies {preview.CardCopyCount}", new Rectangle(panel.X + 24, panel.Y + 108, panel.Width - 48, 28), new Color(148, 224, 164), 0.54f);
        DrawText(preview.CanMigrate ? "Ready. JSON will remain untouched." : preview.Issues.FirstOrDefault()?.Message ?? "Migration is not ready.", new Rectangle(panel.X + 24, panel.Y + 146, panel.Width - 48, 66), preview.CanMigrate ? new Color(196, 207, 220) : new Color(255, 190, 120), 0.5f);
        if (!_sqliteMigrationConfirmation)
        {
            if (Button(new Rectangle(panel.X + 24, panel.Y + 232, 244, 42), "Back Up + Migrate", preview.CanMigrate))
            {
                _sqliteMigrationConfirmation = true;
            }
        }
        else
        {
            DrawText("Confirm: this switches future saves to SQLite. Your verified JSON backup remains available.", new Rectangle(panel.X + 24, panel.Y + 226, panel.Width - 48, 50), new Color(255, 210, 132), 0.5f);
            if (Button(new Rectangle(panel.X + 24, panel.Y + 292, 156, 42), "Confirm"))
            {
                MigrateAndActivateSqlite();
            }
            if (Button(new Rectangle(panel.X + 196, panel.Y + 292, 126, 42), "Cancel"))
            {
                _sqliteMigrationConfirmation = false;
            }
        }
        if (Button(new Rectangle(panel.X + 24, panel.Bottom - 60, 164, 38), "Refresh Preview"))
        {
            _sqliteMigrationPreview = null;
            _sqliteMigrationConfirmation = false;
        }
    }

    private void DrawSqliteDataControls(PlayerProfile profile, Rectangle panel)
    {
        var transfer = new ProfileDataTransferService();
        if (Button(new Rectangle(panel.X + 24, panel.Y + 58, 188, 40), "Export JSON"))
        {
            try
            {
                using var store = new SqliteProfileStore(ProfileDatabasePath);
                store.InitializeAsync().GetAwaiter().GetResult();
                var path = Path.Combine(ProfileExportDirectory, $"{profile.PlayerName.Trim().Replace(' ', '-')}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
                var result = transfer.ExportAsync(store, _activeProfileId!, path, DateTimeOffset.UtcNow).GetAwaiter().GetResult();
                _profileDataNotice = result.Message;
            }
            catch (Exception exception)
            {
                _profileDataNotice = $"Profile export failed: {exception.Message}";
            }
        }
        if (Button(new Rectangle(panel.X + 228, panel.Y + 58, 188, 40), "Import Copy"))
        {
            try
            {
                using var store = new SqliteProfileStore(ProfileDatabasePath);
                store.InitializeAsync().GetAwaiter().GetResult();
                var result = transfer.ImportAsNewProfileAsync(store, ProfileImportPath, DateTimeOffset.UtcNow).GetAwaiter().GetResult();
                _profileDataNotice = result.Message;
                if (result.Success)
                {
                    RefreshSqliteRepository();
                }
            }
            catch (Exception exception)
            {
                _profileDataNotice = $"Profile import failed: {exception.Message}";
            }
        }
        DrawText($"Import path: {ProfileImportPath}", new Rectangle(panel.X + 24, panel.Y + 108, panel.Width - 48, 38), new Color(187, 199, 214), 0.42f);
        if (Button(new Rectangle(panel.X + 24, panel.Y + 158, 188, 40), "Backup SQLite"))
        {
            try
            {
                var result = transfer.CreateDatabaseBackupAsync(ProfileDatabasePath, ProfileBackupDirectory, DateTimeOffset.UtcNow).GetAwaiter().GetResult();
                _profileDataNotice = result.Message;
            }
            catch (Exception exception)
            {
                _profileDataNotice = $"SQLite backup failed: {exception.Message}";
            }
        }
        var latestBackup = Directory.Exists(ProfileBackupDirectory)
            ? Directory.EnumerateFiles(ProfileBackupDirectory, "*.db").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        if (!_profileRestoreConfirmation)
        {
            if (Button(new Rectangle(panel.X + 228, panel.Y + 158, 188, 40), "Restore Latest", !string.IsNullOrWhiteSpace(latestBackup)))
            {
                _profileRestoreConfirmation = true;
            }
        }
        else
        {
            DrawText("Restore creates a safety backup first.", new Rectangle(panel.X + 24, panel.Y + 208, panel.Width - 48, 26), new Color(255, 210, 132), 0.48f);
            if (Button(new Rectangle(panel.X + 24, panel.Y + 242, 138, 38), "Confirm Restore"))
            {
                try
                {
                    var result = transfer.RestoreDatabaseBackupAsync(ProfileDatabasePath, latestBackup!, ProfileSafetyBackupDirectory, DateTimeOffset.UtcNow).GetAwaiter().GetResult();
                    _profileDataNotice = result.Message;
                    _profileRestoreConfirmation = false;
                    if (result.Success)
                    {
                        RefreshSqliteRepository();
                    }
                }
                catch (Exception exception)
                {
                    _profileRestoreConfirmation = false;
                    _profileDataNotice = $"SQLite restore failed: {exception.Message}";
                }
            }
            if (Button(new Rectangle(panel.X + 178, panel.Y + 242, 116, 38), "Cancel"))
            {
                _profileRestoreConfirmation = false;
            }
        }

        if (!_profileRestoreConfirmation)
        {
            if (Button(new Rectangle(panel.X + 24, panel.Y + 212, 188, 36), "Verify SQLite"))
            {
                var verification = transfer.VerifyDatabaseAsync(ProfileDatabasePath).GetAwaiter().GetResult();
                _profileDataNotice = verification.Message;
            }
            if (Button(new Rectangle(panel.X + 228, panel.Y + 212, 188, 36), "Back Up JSON Source"))
            {
                try
                {
                    var backup = new JsonProfileMigrationService().BackupJsonSourceAsync(ProfileDataDirectory, DateTimeOffset.UtcNow).GetAwaiter().GetResult();
                    _profileDataNotice = backup.Message;
                }
                catch (Exception exception)
                {
                    _profileDataNotice = $"JSON source backup failed: {exception.Message}";
                }
            }
        }

        DrawText("Deterministic Seed", new Vector2(panel.X + 24, panel.Y + 320), new Color(244, 230, 158), 0.62f);
        DrawText($"Demo / {_profileSeedValue} / {ProfileSeedService.AlgorithmVersion}", new Rectangle(panel.X + 24, panel.Y + 350, panel.Width - 48, 24), new Color(196, 207, 220), 0.5f);
        if (Button(new Rectangle(panel.X + 24, panel.Y + 382, 72, 36), "Seed -")) _profileSeedValue--;
        if (Button(new Rectangle(panel.X + 108, panel.Y + 382, 72, 36), "Seed +")) _profileSeedValue++;
        if (Button(new Rectangle(panel.X + 196, panel.Y + 382, 130, 36), "Preview Seed"))
        {
            _profileSeedPreview = new ProfileSeedService().CreatePreview(profile, _data, new ProfileSeedRequest(_profileSeedValue, ProfileSeedScenario.Demo, DateTimeOffset.UtcNow));
            _profileDataNotice = _profileSeedPreview.Summary;
        }
        if (_profileSeedPreview is not null)
        {
            DrawText(_profileSeedPreview.Summary, new Rectangle(panel.X + 24, panel.Y + 432, panel.Width - 48, 42), new Color(148, 224, 164), 0.48f);
            if (Button(new Rectangle(panel.X + 24, panel.Y + 486, 154, 40), "Apply Seed"))
            {
                ApplySeedPreview(_profileSeedPreview);
            }
        }
    }

    private void MigrateAndActivateSqlite()
    {
        try
        {
            var result = new JsonProfileMigrationService().MigrateAsync(ProfileDataDirectory, ProfileDatabasePath, DateTimeOffset.UtcNow).GetAwaiter().GetResult();
            _profileDataNotice = result.Success && !string.IsNullOrWhiteSpace(result.BackupDirectory)
                ? $"{result.Message} Backup: {result.BackupDirectory}"
                : result.Message;
            if (result.Success)
            {
                _sqliteMigrationConfirmation = false;
                _sqliteMigrationPreview = null;
                ActivateSqliteRepository();
            }
        }
        catch (Exception exception)
        {
            _profileDataNotice = $"SQLite migration failed: {exception.Message}";
        }
    }

    private void ApplySeedPreview(ProfileSeedPreview preview)
    {
        string? error = null;
        if (_profileRepository is null || _activeProfileId is null || !_profileRepository.TrySaveProfile(_activeProfileId, preview.Profile, DateTimeOffset.UtcNow, out error))
        {
            _profileDataNotice = error ?? "The seeded profile could not be saved.";
            return;
        }

        _profile = preview.Profile;
        using var store = new SqliteProfileStore(ProfileDatabasePath);
        store.InitializeAsync().GetAwaiter().GetResult();
        var recorded = store.RecordSeedRunAsync(_activeProfileId, preview.Seed, preview.AlgorithmVersion, preview.Scenario, preview.Summary, DateTimeOffset.UtcNow).GetAwaiter().GetResult();
        _profileDataNotice = recorded.Success ? preview.Summary : recorded.Message;
        _profileSeedPreview = null;
        RefreshProfileDataReadModel(force: true);
    }

    private void ActivateSqliteRepository()
    {
        var activeProfileId = _activeProfileId;
        var sqlite = new SqliteProfileRepository(ProfileDataDirectory);
        if (!sqlite.Initialize(out _, out var error))
        {
            sqlite.Dispose();
            _profileDataNotice = error ?? "SQLite profile storage could not be opened.";
            return;
        }

        if (_profileRepository is IDisposable disposable)
        {
            disposable.Dispose();
        }
        _profileRepository = sqlite;
        if (!string.IsNullOrWhiteSpace(activeProfileId) && sqlite.TryLoadProfile(activeProfileId, out var loaded, out error) && loaded is not null)
        {
            sqlite.TrySelectProfile(activeProfileId, DateTimeOffset.UtcNow, out _);
            _profile = loaded;
            _activeProfileId = activeProfileId;
            _deckBuilder = CreateDeckBuilderState(ResolveProfileDeck(activeProfileId, loaded));
            RefreshProfileDataReadModel(force: true);
            return;
        }

        _profile = null;
        _activeProfileId = null;
        _screen = Screen.ProfilePicker;
        _profileDataNotice = "SQLite migration completed. Choose a migrated profile.";
    }

    private void RefreshSqliteRepository() => ActivateSqliteRepository();

    private void RefreshProfileDataReadModel(bool force = false)
    {
        if (!force && (_profileDataAudit.Count > 0 || _profileRepository?.StorageKind != ProfileStorageKind.Sqlite || string.IsNullOrWhiteSpace(_activeProfileId)))
        {
            return;
        }

        try
        {
            using var store = new SqliteProfileStore(ProfileDatabasePath);
            store.InitializeAsync().GetAwaiter().GetResult();
            _profileDataAudit = store.ReadAuditAsync(_activeProfileId!, 8).GetAwaiter().GetResult();
            _profileSeedRuns = store.ReadSeedRunsAsync(_activeProfileId!, 5).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _profileDataNotice = $"Profile audit could not be loaded: {exception.Message}";
        }
    }
}
