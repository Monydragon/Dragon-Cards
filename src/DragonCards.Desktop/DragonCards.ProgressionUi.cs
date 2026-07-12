using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DragonCards.Core;
using DragonCards.Networking;
using DragonCards.Persistence;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace DragonCards.Desktop;

public sealed partial class DragonCardsGame
{
    private sealed record DeckLibraryEntry(DeckDefinition Deck, bool IsStarter);

    private static readonly GameRulesPreset[] RulesPresetOrder =
    [
        GameRulesPreset.Casual,
        GameRulesPreset.Easy,
        GameRulesPreset.Standard,
        GameRulesPreset.Hard,
        GameRulesPreset.VeryHard,
        GameRulesPreset.Insane,
        GameRulesPreset.Custom
    ];

    private static readonly Playstyle[] PlaystyleOrder =
    [
        Playstyle.Balanced,
        Playstyle.Aggro,
        Playstyle.Control,
        Playstyle.Ramp,
        Playstyle.Combo
    ];

    private PlayerProfile? _profile;
    private IProfileRepository? _profileRepository;
    private string? _activeProfileId;
    private int _profilePickerFocus;
    private int _profilePickerScrollOffset;
    private bool _profilePickerOpenedFromMainMenu;
    private bool _profileDeleteConfirmation;
    private bool _profileRenameActive;
    private string _profileRenameText = "";
    private string _profileRepositoryNotice = "";
    private bool _deckLibraryOpen;
    private int _deckLibraryFocus;
    private int _deckLibraryScrollOffset;
    private bool _deckLibraryDeleteConfirmation;
    private bool _deckNameEditing;
    private string _deckNameText = "";
    private string _creationName = "Player";
    private int _creationPresetIndex = 2;
    private int _creationPlaystyleIndex;
    private int _creationStarterIndex;
    private int _storeFocus;
    private int _storeScrollOffset;
    private int _storeQuantityIndex;
    private int _packOpeningScrollOffset;
    private int _cardDetailScrollOffset;
    private BoosterOpening? _lastBoosterOpening;
    private MatchReward? _lastMatchReward;
    private IReadOnlyList<QuestReward> _lastQuestRewards = [];
    private BattleSpoilsReward? _lastBattleSpoils;
    private bool _pendingResultScreen;
    private bool _matchRewardApplied;
    private bool _lastMatchWon;
    private DeckDefinition? _rematchFirstDeck;
    private DeckDefinition? _rematchSecondDeck;
    private MatchKind _rematchKind;
    private string _rematchModeId = DragonCardsModeIds.DragonDuel;
    private DirectMatchConnection? _networkConnection;
    private Task<DirectMatchConnection>? _networkConnectTask;
    private Task<NetworkInvite>? _networkDiscoveryTask;
    private Task<NetworkMatchStart>? _networkStartTask;
    private Task<NetworkCommand>? _networkReadTask;
    private CancellationTokenSource? _networkCancellation;
    private int _networkLocalPlayerIndex;
    private int _networkSequence;
    private bool _applyingRemoteCommand;

    private static string ProfileDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DragonCards");

    private void InitializeProgressionState()
    {
        var sqliteDatabasePath = Path.Combine(ProfileDataDirectory, SqliteProfileRepository.DatabaseFileName);
        _profileRepository = File.Exists(sqliteDatabasePath)
            ? new SqliteProfileRepository(ProfileDataDirectory)
            : new LocalProfileRepository(ProfileDataDirectory);
        if (!_profileRepository.Initialize(out var migrated, out var error))
        {
            _profileRepositoryNotice = error ?? "Local profiles could not be opened.";
            _deckBuilder = CreateDeckBuilderState(StarterDecks().First());
            _screen = Screen.ProfilePicker;
            _status = _profileRepositoryNotice;
            return;
        }

        _deckBuilder = CreateDeckBuilderState(StarterDecks().First());
        _profilePickerFocus = Math.Max(0, _profileRepository.Profiles.ToList().FindIndex(summary =>
            summary.Id.Equals(_profileRepository.LastActiveProfileId, StringComparison.OrdinalIgnoreCase)));
        _profileRepositoryNotice = migrated
            ? "Your 0.1.2 save was safely moved into a local profile."
            : _profileRepository.StorageKind == ProfileStorageKind.Sqlite
                ? _profileRepository.Errors.FirstOrDefault()?.Message ?? "Choose a local SQLite profile to continue."
                : _profileRepository.Errors.FirstOrDefault()?.Message ?? "Choose a local profile to continue.";
        _screen = Screen.ProfilePicker;
        _status = _profileRepositoryNotice;
    }

    private void HandleProgressionUpdate()
    {
        HandlePlayerCreationTextInput();
        HandleProfileRenameTextInput();
        HandleDeckNameTextInput();
        HandleJoinInviteTextInput();
        UpdateNetworkTasks();
    }

    private IReadOnlyList<DeckDefinition> StarterDecks() =>
        _data.Decks
            .Where(deck => deck.Id.StartsWith("starter-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(deck => ElementOrder.ToList().IndexOf(StarterElement(deck)))
            .ThenBy(deck => deck.Name)
            .ToArray();

    private static string StarterElement(DeckDefinition deck)
    {
        if (!string.IsNullOrWhiteSpace(deck.Id) && deck.Id.StartsWith("starter-", StringComparison.OrdinalIgnoreCase))
        {
            var key = deck.Id["starter-".Length..];
            return ElementOrder.FirstOrDefault(element => element.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? deck.Name.Replace("Starter:", "", StringComparison.OrdinalIgnoreCase).Trim();
        }

        return deck.Name.Replace("Starter:", "", StringComparison.OrdinalIgnoreCase).Trim();
    }

    private GameRulesConfig CurrentRules() =>
        (_profile?.DefaultRules ?? GameRulesConfig.ForPreset(GameRulesPreset.Standard)).Normalize();

    private DeckBuilderState CreateDeckBuilderState(DeckDefinition deck)
    {
        var state = new DeckBuilderState(_data, deck);
        state.ConfigureCollection(_profile, CurrentRules().IsSandbox);
        return state;
    }

    private DeckDefinition CurrentDeck() => _deckBuilder.CreateDeck();

    private DeckDefinition OpponentDeck()
    {
        var rules = CurrentRules();
        var preferred = rules.Playstyle switch
        {
            Playstyle.Aggro => "starter-lightning",
            Playstyle.Control => "starter-dark",
            Playstyle.Ramp => "starter-earth",
            Playstyle.Combo => "starter-water",
            _ => "starter-ice"
        };
        if (_data.DecksById.TryGetValue(preferred, out var deck) && !deck.Id.Equals(CurrentDeck().Id, StringComparison.OrdinalIgnoreCase))
        {
            return deck;
        }

        return StarterDecks().First(deck => !deck.Id.Equals(CurrentDeck().Id, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<ValidationIssue> ValidateCurrentDeckOwnership(DeckDefinition deck) =>
        _profile is null ? [] : DeckOwnershipValidator.ValidateDeckOwnership(deck, _profile, CurrentRules());

    private bool CanAddCardToDeck(CardDefinition card, DeckDefinition deck)
    {
        var mode = _data.GameModesById["dragon-duel"];
        if (deck.Count >= mode.DeckRules.DeckSize ||
            (!BasicEnergy.IsBasicEnergyCard(card) && _deckBuilder.CardCount(card.Id) >= mode.DeckRules.MaxCopies))
        {
            return false;
        }

        var rules = CurrentRules();
        return rules.UnlimitedDeckBuilder || rules.AllUnlocks || _profile is null || _deckBuilder.CardCount(card.Id) < PlayerCollection.CountOwned(_profile, card.Id);
    }

    private void ExportCurrentDeckCode(DeckDefinition deck)
    {
        var code = DeckCode.Export(deck);
        _status = DesktopClipboard.TrySetText(code, out var error)
            ? "DCD1 deck code copied to the clipboard."
            : error;
    }

    private void ImportDeckCodeFromClipboard()
    {
        if (!DesktopClipboard.TryGetText(out var code, out var clipboardError))
        {
            _status = clipboardError;
            return;
        }

        if (!DeckCode.TryImport(code, out var imported, out var decodeError) || imported is null)
        {
            _status = decodeError;
            return;
        }

        var legalityIssues = GameDataValidator.ValidateDeck(imported, _data);
        if (legalityIssues.Count > 0)
        {
            _status = $"Deck code rejected: {legalityIssues[0].Message}";
            return;
        }

        var ownershipIssues = ValidateCurrentDeckOwnership(imported);
        _deckBuilder.ReplaceWith(imported with { Id = $"deck-{Guid.NewGuid():N}" });
        _status = ownershipIssues.Count == 0
            ? $"Imported legal deck '{imported.Name}'. Save it to keep it."
            : $"Imported legal deck preview: {ownershipIssues.Count} owned-copy issue(s). No cards were granted.";
    }

    private string DeckBuilderModeLabel()
    {
        var rules = CurrentRules();
        return rules.UnlimitedDeckBuilder || rules.AllUnlocks
            ? $"{rules.Preset} sandbox: unlimited deck builder"
            : $"{rules.Preset}: owned inventory deck builder";
    }

    private string OwnedSummary(CardDefinition card)
    {
        if (BasicEnergy.IsBasicEnergyCard(card))
        {
            return $"Always available  In deck {_deckBuilder.CardCount(card.Id)}";
        }

        var rules = CurrentRules();
        if (rules.UnlimitedDeckBuilder || rules.AllUnlocks || _profile is null)
        {
            return "Sandbox: all copies available";
        }

        return $"Owned {PlayerCollection.CountOwned(_profile, card.Id)}/3  In deck {_deckBuilder.CardCount(card.Id)}";
    }

    private void DrawOwnedCardCount(CardDefinition card, Rectangle rect)
    {
        var text = CurrentRules().UnlimitedDeckBuilder || CurrentRules().AllUnlocks || _profile is null
            ? "All"
            : BasicEnergy.IsBasicEnergyCard(card)
                ? $"{_deckBuilder.CardCount(card.Id)}/Unlimited"
            : $"{_deckBuilder.CardCount(card.Id)}/{PlayerCollection.CountOwned(_profile, card.Id)}";
        DrawFittedCenteredText(text, new Rectangle(rect.X + 6, rect.Bottom + 4, rect.Width - 12, 18), new Color(225, 233, 242), 0.42f, 0.28f);
    }

    private string MainMenuSubtitle()
    {
        if (_profile is null)
        {
            return $"Library {_data.Cards.Count} cards   Decks {_data.Decks.Count}";
        }

        var rules = CurrentRules();
        var progression = rules.IsProgressionSafe ? "Progression on" : "Sandbox";
        return $"{_profile.PlayerName}   Level {_profile.Level}   {_profile.Coins} Coins   {BoosterService.TotalUnopenedPacks(_profile)} Packs   {rules.Preset} / {rules.Playstyle}   {progression}";
    }

    private void DrawProfileSummary(Rectangle rect)
    {
        DrawPanel(rect, new Color(31, 37, 46), border: new Color(81, 96, 116));
        if (_profile is null)
        {
            DrawText("No Profile", new Vector2(rect.X + 30, rect.Y + 28), Color.White, 0.94f);
            DrawText("Create a player to unlock progression, inventory, packs, and starters.", new Rectangle(rect.X + 30, rect.Y + 72, rect.Width - 60, 90), new Color(205, 214, 225), 0.62f);
            return;
        }

        var rules = CurrentRules();
        DrawText(_profile.PlayerName, new Vector2(rect.X + 30, rect.Y + 26), Color.White, 0.98f);
        DrawText($"Level {_profile.Level}   XP {_profile.ExperienceIntoLevel}/{ProgressionService.ExperiencePerLevel}", new Vector2(rect.X + 30, rect.Y + 64), new Color(205, 214, 225), 0.66f);
        var xpBar = new Rectangle(rect.X + 30, rect.Y + 96, rect.Width - 60, 16);
        Fill(xpBar, new Color(20, 25, 32));
        var progress = _profile.Level >= ProgressionService.MaxLevel ? 1f : _profile.ExperienceIntoLevel / (float)ProgressionService.ExperiencePerLevel;
        Fill(new Rectangle(xpBar.X, xpBar.Y, (int)(xpBar.Width * progress), xpBar.Height), new Color(148, 224, 164));
        Border(xpBar, new Color(75, 90, 110), 1);
        DrawText($"{_profile.Coins} Coins   {BoosterService.TotalUnopenedPacks(_profile)} Packs", new Vector2(rect.X + 30, rect.Y + 128), new Color(244, 230, 158), 0.66f);
        DrawText($"{rules.Preset} / {rules.Playstyle}", new Vector2(rect.X + 30, rect.Y + 160), new Color(205, 214, 225), 0.62f);
        DrawText(rules.IsProgressionSafe ? "Progression rewards enabled." : "Sandbox rules: rewards disabled.", new Rectangle(rect.X + 30, rect.Y + 184, rect.Width - 60, 24), rules.IsProgressionSafe ? new Color(148, 224, 164) : new Color(255, 190, 120), 0.52f);
    }

    private void DrawPlayerCreation()
    {
        DrawText("Create Player", new Vector2(54, 108), Color.White, 1.2f);
        DrawText("Name yourself, choose a difficulty, playstyle, and first starter deck.", new Vector2(56, 146), new Color(196, 207, 220), 0.72f);

        var panel = new Rectangle(54, 198, 1490, 590);
        DrawPanel(panel, new Color(31, 37, 46), border: new Color(81, 96, 116));

        DrawText("Name", new Vector2(panel.X + 32, panel.Y + 28), Color.White, 0.82f);
        var nameBox = new Rectangle(panel.X + 32, panel.Y + 66, 390, 48);
        Fill(nameBox, new Color(20, 25, 32));
        Border(nameBox, new Color(124, 143, 166), 2);
        DrawText(_creationName, new Vector2(nameBox.X + 14, nameBox.Y + 13), Color.White, 0.7f);

        DrawText("Difficulty", new Vector2(panel.X + 32, panel.Y + 150), Color.White, 0.82f);
        for (var i = 0; i < RulesPresetOrder.Length; i++)
        {
            var rect = new Rectangle(panel.X + 32 + i * 140, panel.Y + 190, 128, 42);
            if (Button(rect, RulesPresetOrder[i].ToString(), selected: _creationPresetIndex == i,
                focused: _usingController && _creationControlFocus == 0 && _creationPresetIndex == i))
            {
                _creationPresetIndex = i;
            }
        }

        DrawText("Playstyle", new Vector2(panel.X + 32, panel.Y + 268), Color.White, 0.82f);
        for (var i = 0; i < PlaystyleOrder.Length; i++)
        {
            var rect = new Rectangle(panel.X + 32 + i * 150, panel.Y + 308, 138, 42);
            if (Button(rect, PlaystyleOrder[i].ToString(), selected: _creationPlaystyleIndex == i,
                focused: _usingController && _creationControlFocus == 1 && _creationPlaystyleIndex == i))
            {
                _creationPlaystyleIndex = i;
            }
        }

        DrawText("Starter Deck", new Vector2(panel.X + 32, panel.Y + 386), Color.White, 0.82f);
        var starters = StarterDecks();
        for (var i = 0; i < starters.Count; i++)
        {
            var column = i % 4;
            var row = i / 4;
            var rect = new Rectangle(panel.X + 32 + column * 236, panel.Y + 426 + row * 58, 216, 44);
            if (Button(rect, StarterElement(starters[i]), selected: _creationStarterIndex == i,
                focused: _usingController && _creationControlFocus == 2 && _creationStarterIndex == i))
            {
                _creationStarterIndex = i;
            }
        }

        var rules = GameRulesConfig.ForPreset(RulesPresetOrder[_creationPresetIndex], PlaystyleOrder[_creationPlaystyleIndex]).Normalize();
        DrawText(rules.IsProgressionSafe ? "Progression mode: inventory, XP, coins, packs, and locked starters." : "Sandbox mode: all unlocks and unlimited deck building; progression rewards disabled.", new Rectangle(panel.X + 1000, panel.Y + 190, 390, 86), rules.IsProgressionSafe ? new Color(148, 224, 164) : new Color(255, 190, 120), 0.64f);

        if (Button(new Rectangle(panel.Right - 248, panel.Bottom - 72, 196, 46), "Create Player",
            focused: _usingController && _creationControlFocus == 3))
        {
            CreateProfileFromSelection();
        }
    }

    private void DrawProfilePicker()
    {
        DrawText("Profiles", new Vector2(54, 108), Color.White, 1.2f);
        DrawText("Choose a local player profile. Progression, tutorials, inventory, rules, and decks stay on this device.", new Vector2(56, 146), new Color(196, 207, 220), 0.66f);

        var profiles = _profileRepository?.Profiles ?? [];
        _profilePickerFocus = Math.Clamp(_profilePickerFocus, 0, profiles.Count);
        const int visibleRows = 6;
        _profilePickerScrollOffset = Math.Clamp(_profilePickerScrollOffset, 0, Math.Max(0, profiles.Count - visibleRows));
        if (_profilePickerFocus < _profilePickerScrollOffset)
        {
            _profilePickerScrollOffset = _profilePickerFocus;
        }
        else if (_profilePickerFocus < profiles.Count && _profilePickerFocus >= _profilePickerScrollOffset + visibleRows)
        {
            _profilePickerScrollOffset = _profilePickerFocus - visibleRows + 1;
        }

        var listPanel = new Rectangle(54, 198, 710, 560);
        DrawPanel(listPanel, new Color(31, 37, 46), border: new Color(81, 96, 116));
        DrawText("Local Profiles", new Vector2(listPanel.X + 26, listPanel.Y + 22), Color.White, 0.88f);
        if (profiles.Count == 0)
        {
            DrawText("No local profiles yet. Create one to begin a separate Dragon Cards journey on this device.", new Rectangle(listPanel.X + 28, listPanel.Y + 86, listPanel.Width - 56, 72), new Color(205, 214, 225), 0.66f);
        }

        for (var row = 0; row < visibleRows && _profilePickerScrollOffset + row < profiles.Count; row++)
        {
            var index = _profilePickerScrollOffset + row;
            var summary = profiles[index];
            var rect = new Rectangle(listPanel.X + 24, listPanel.Y + 66 + row * 72, listPanel.Width - 48, 60);
            var selected = index == _profilePickerFocus;
            if (Button(rect, summary.DisplayName, selected: selected, focused: _usingController && selected))
            {
                _profilePickerFocus = index;
                SelectProfile(summary.Id);
            }

            DrawText($"Last played {FormatProfileTime(summary.LastPlayedUtc)}", new Vector2(rect.X + 18, rect.Y + 35), new Color(187, 199, 214), 0.46f);
        }

        if (profiles.Count > visibleRows)
        {
            var maxOffset = Math.Max(1, profiles.Count - visibleRows);
            var track = new Rectangle(listPanel.Right - 18, listPanel.Y + 68, 8, 416);
            Fill(track, new Color(18, 23, 30));
            var thumbHeight = Math.Max(28, track.Height * visibleRows / profiles.Count);
            var thumbY = track.Y + (track.Height - thumbHeight) * _profilePickerScrollOffset / maxOffset;
            Fill(new Rectangle(track.X, thumbY, track.Width, thumbHeight), UiTheme.Focus);
        }

        var createSelected = _profilePickerFocus == profiles.Count;
        if (Button(new Rectangle(listPanel.X + 24, listPanel.Bottom - 58, 230, 38), "Create Profile", _profileRepository?.IsInitialized == true, focused: _usingController && createSelected))
        {
            BeginProfileCreation();
        }
        if (Button(new Rectangle(listPanel.Right - 174, listPanel.Bottom - 58, 150, 38), "Exit", focused: false))
        {
            try { Exit(); }
            catch (PlatformNotSupportedException) { }
        }

        var detailPanel = new Rectangle(792, 198, 752, 560);
        DrawPanel(detailPanel, new Color(34, 41, 51), border: new Color(84, 99, 119));
        if (_profilePickerFocus < profiles.Count && TryProfilePreview(profiles[_profilePickerFocus], out var profile))
        {
            var summary = profiles[_profilePickerFocus];
            DrawText(profile.PlayerName, new Vector2(detailPanel.X + 32, detailPanel.Y + 30), Color.White, 1.04f);
            DrawText($"Level {profile.Level}   {profile.Coins} Coins", new Vector2(detailPanel.X + 32, detailPanel.Y + 74), new Color(244, 230, 158), 0.7f);
            var starterName = _data.DecksById.TryGetValue(profile.SelectedStarterDeckId, out var starter) ? starter.Name : "No starter selected";
            DrawText($"Starter: {starterName}", new Vector2(detailPanel.X + 32, detailPanel.Y + 110), new Color(205, 214, 225), 0.62f);
            DrawText($"Last played: {FormatProfileTime(summary.LastPlayedUtc)}", new Vector2(detailPanel.X + 32, detailPanel.Y + 144), new Color(205, 214, 225), 0.58f);
            DrawText("Profiles are local only. Switching is unavailable while a match, pack opening, or active lobby is in progress.", new Rectangle(detailPanel.X + 32, detailPanel.Y + 190, detailPanel.Width - 64, 50), new Color(148, 224, 164), 0.56f);

            if (_profileRenameActive)
            {
                DrawText("Rename", new Vector2(detailPanel.X + 32, detailPanel.Y + 280), Color.White, 0.72f);
                var nameBox = new Rectangle(detailPanel.X + 32, detailPanel.Y + 316, 386, 46);
                Fill(nameBox, new Color(20, 25, 32));
                Border(nameBox, UiTheme.Focus, 2);
                DrawText(_profileRenameText, new Vector2(nameBox.X + 14, nameBox.Y + 12), Color.White, 0.68f);
                if (Button(new Rectangle(detailPanel.X + 32, detailPanel.Y + 386, 142, 42), "Save Rename", focused: _usingController))
                {
                    RenameSelectedProfile();
                }
                if (Button(new Rectangle(detailPanel.X + 190, detailPanel.Y + 386, 116, 42), "Cancel"))
                {
                    _profileRenameActive = false;
                }
            }
            else
            {
                if (Button(new Rectangle(detailPanel.X + 32, detailPanel.Bottom - 76, 168, 44), "Select", focused: _usingController))
                {
                    SelectProfile(summary.Id);
                }
                if (Button(new Rectangle(detailPanel.X + 218, detailPanel.Bottom - 76, 142, 44), "Rename"))
                {
                    _profileRenameActive = true;
                    _profileRenameText = profile.PlayerName;
                }
                if (Button(new Rectangle(detailPanel.X + 378, detailPanel.Bottom - 76, 142, 44), "Delete"))
                {
                    _profileDeleteConfirmation = true;
                }
            }
        }
        else
        {
            DrawText("Create a Profile", new Vector2(detailPanel.X + 32, detailPanel.Y + 30), Color.White, 1.04f);
            DrawText("Each profile keeps its own progress, collection, tutorial completion, rules, and named decks. Device display, audio, and accessibility options stay shared.", new Rectangle(detailPanel.X + 32, detailPanel.Y + 84, detailPanel.Width - 64, 94), new Color(205, 214, 225), 0.66f);
            if (Button(new Rectangle(detailPanel.X + 32, detailPanel.Y + 214, 206, 46), "Create Profile", _profileRepository?.IsInitialized == true, focused: _usingController && createSelected))
            {
                BeginProfileCreation();
            }
        }

        if (!string.IsNullOrWhiteSpace(_profileRepositoryNotice))
        {
            DrawText(_profileRepositoryNotice, new Rectangle(56, 778, 1440, 36), _profileRepositoryNotice.Contains("could not", StringComparison.OrdinalIgnoreCase) || _profileRepositoryNotice.Contains("recovery", StringComparison.OrdinalIgnoreCase) ? new Color(255, 190, 120) : new Color(148, 224, 164), 0.54f);
        }

        if (_profileDeleteConfirmation && _profilePickerFocus < profiles.Count)
        {
            DrawProfileDeleteConfirmation(profiles[_profilePickerFocus]);
        }
    }

    private void DrawProfileDeleteConfirmation(LocalProfileSummary summary)
    {
        _drawingModal = true;
        try
        {
            Fill(new Rectangle(0, 0, VirtualWidth, VirtualHeight), new Color(0, 0, 0, 178));
            var panel = new Rectangle(474, 290, 652, 310);
            DrawPanel(panel, UiTheme.PanelRaised, border: new Color(214, 102, 92));
            DrawText("Delete Profile?", new Vector2(panel.X + 32, panel.Y + 30), Color.White, 0.96f);
            DrawText($"Delete {summary.DisplayName} permanently? This removes its progression, tutorials, inventory, rules, and saved decks from this device.", new Rectangle(panel.X + 32, panel.Y + 82, panel.Width - 64, 86), new Color(224, 208, 205), 0.66f);
            if (Button(new Rectangle(panel.X + 32, panel.Bottom - 68, 168, 42), "Delete Permanently", focused: _usingController))
            {
                DeleteSelectedProfile();
            }
            if (Button(new Rectangle(panel.Right - 160, panel.Bottom - 68, 128, 42), "Cancel"))
            {
                _profileDeleteConfirmation = false;
            }
        }
        finally
        {
            _drawingModal = false;
        }
    }

    private bool TryProfilePreview(LocalProfileSummary summary, out PlayerProfile profile)
    {
        profile = new PlayerProfile();
        string? error = null;
        if (_profileRepository is null || !_profileRepository.TryLoadProfile(summary.Id, out var loaded, out error) || loaded is null)
        {
            _profileRepositoryNotice = error ?? "The selected profile could not be loaded.";
            return false;
        }

        profile = loaded;
        return true;
    }

    private static string FormatProfileTime(DateTimeOffset value)
    {
        if (value == default)
        {
            return "never";
        }

        var local = value.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("h:mm tt") : local.ToString("MMM d, yyyy");
    }

    private IReadOnlyList<DeckLibraryEntry> DeckLibraryEntries()
    {
        var entries = StarterDecks().Select(deck => new DeckLibraryEntry(deck, true)).ToList();
        if (_profileRepository is null || string.IsNullOrWhiteSpace(_activeProfileId))
        {
            return entries;
        }

        var customDecks = _profileRepository.LoadDecks(_activeProfileId, out var errors);
        if (errors.Count > 0)
        {
            _status = errors[0].Message;
        }

        entries.AddRange(customDecks.Select(deck => new DeckLibraryEntry(deck, false)));
        return entries;
    }

    private void DrawDeckLibraryOverlay()
    {
        if (!_deckLibraryOpen)
        {
            return;
        }

        _drawingModal = true;
        try
        {
            Fill(new Rectangle(0, 0, VirtualWidth, VirtualHeight), new Color(0, 0, 0, 178));
            var panel = new Rectangle(180, 102, 1240, 698);
            DrawPanel(panel, UiTheme.PanelRaised, border: UiTheme.BorderStrong);
            DrawUiText("Deck Library", new Vector2(panel.X + 30, panel.Y + 24), Color.White, 0.96f);
            DrawUiText("Starter decks are read-only. Custom decks belong only to the selected local profile.", new Vector2(panel.X + 32, panel.Y + 62), UiTheme.TextMuted, 0.52f);

            var entries = DeckLibraryEntries();
            _deckLibraryFocus = Math.Clamp(_deckLibraryFocus, 0, Math.Max(0, entries.Count - 1));
            const int visibleRows = 7;
            _deckLibraryScrollOffset = Math.Clamp(_deckLibraryScrollOffset, 0, Math.Max(0, entries.Count - visibleRows));
            if (_deckLibraryFocus < _deckLibraryScrollOffset)
            {
                _deckLibraryScrollOffset = _deckLibraryFocus;
            }
            else if (_deckLibraryFocus >= _deckLibraryScrollOffset + visibleRows)
            {
                _deckLibraryScrollOffset = _deckLibraryFocus - visibleRows + 1;
            }

            var list = new Rectangle(panel.X + 30, panel.Y + 106, 564, 468);
            DrawPanel(list, UiTheme.Panel, border: UiTheme.Border);
            for (var row = 0; row < visibleRows && _deckLibraryScrollOffset + row < entries.Count; row++)
            {
                var index = _deckLibraryScrollOffset + row;
                var entry = entries[index];
                var rect = new Rectangle(list.X + 16, list.Y + 16 + row * 62, list.Width - 32, 52);
                var selected = index == _deckLibraryFocus;
                if (Button(rect, entry.Deck.Name, selected: selected, focused: _usingController && selected))
                {
                    _deckLibraryFocus = index;
                    LoadDeckLibraryEntry(entry);
                }
                DrawUiText(entry.IsStarter ? "Starter deck - read-only" : $"Custom - {entry.Deck.Count} cards", new Vector2(rect.X + 16, rect.Y + 31), entry.IsStarter ? UiTheme.TextMuted : new Color(148, 224, 164), 0.42f);
            }

            if (entries.Count > visibleRows)
            {
                var maxOffset = Math.Max(1, entries.Count - visibleRows);
                var track = new Rectangle(list.Right - 12, list.Y + 14, 6, list.Height - 28);
                Fill(track, new Color(18, 23, 30));
                var thumbHeight = Math.Max(26, track.Height * visibleRows / entries.Count);
                var thumbY = track.Y + (track.Height - thumbHeight) * _deckLibraryScrollOffset / maxOffset;
                Fill(new Rectangle(track.X, thumbY, track.Width, thumbHeight), UiTheme.Focus);
            }

            if (entries.Count == 0)
            {
                DrawUiText("No decks are available.", new Vector2(list.X + 20, list.Y + 24), UiTheme.TextMuted, 0.56f);
            }

            var selectedEntry = entries.Count == 0 ? null : entries[_deckLibraryFocus];
            var detail = new Rectangle(panel.X + 622, panel.Y + 106, 588, 468);
            DrawPanel(detail, UiTheme.Panel, border: UiTheme.Border);
            if (selectedEntry is not null)
            {
                DrawUiText(selectedEntry.Deck.Name, new Vector2(detail.X + 24, detail.Y + 22), Color.White, 0.82f);
                DrawUiText(selectedEntry.IsStarter ? "Built-in starter deck" : "Profile-owned custom deck", new Vector2(detail.X + 24, detail.Y + 58), selectedEntry.IsStarter ? UiTheme.TextMuted : new Color(148, 224, 164), 0.5f);
                DrawUiText($"{selectedEntry.Deck.Count} cards - {selectedEntry.Deck.ModeId}", new Vector2(detail.X + 24, detail.Y + 88), UiTheme.Text, 0.52f);
                DrawText(string.Join("\n", selectedEntry.Deck.Cards.OrderBy(card => card.Key, StringComparer.OrdinalIgnoreCase).Take(11).Select(card => $"{card.Value}x {_data.CardsById.GetValueOrDefault(card.Key)?.Name ?? card.Key}")), new Rectangle(detail.X + 24, detail.Y + 132, detail.Width - 48, 202), UiTheme.Text, 0.5f);

                if (_deckNameEditing)
                {
                    DrawUiText("Deck name", new Vector2(detail.X + 24, detail.Bottom - 120), Color.White, 0.58f);
                    var nameBox = new Rectangle(detail.X + 24, detail.Bottom - 92, 322, 38);
                    Fill(nameBox, new Color(20, 25, 32));
                    Border(nameBox, UiTheme.Focus, 2);
                    DrawUiText(_deckNameText, new Vector2(nameBox.X + 10, nameBox.Y + 9), Color.White, 0.54f);
                    if (Button(new Rectangle(detail.Right - 192, detail.Bottom - 92, 78, 38), "Save", focused: _usingController))
                    {
                        RenameLibraryDeck();
                    }
                    if (Button(new Rectangle(detail.Right - 104, detail.Bottom - 92, 80, 38), "Cancel"))
                    {
                        _deckNameEditing = false;
                    }
                }
                else
                {
                    if (Button(new Rectangle(detail.X + 24, detail.Bottom - 62, 110, 38), "Load", focused: _usingController))
                    {
                        LoadDeckLibraryEntry(selectedEntry);
                    }
                    if (Button(new Rectangle(detail.X + 146, detail.Bottom - 62, 116, 38), "Duplicate"))
                    {
                        DuplicateLibraryDeck(selectedEntry.Deck);
                    }
                    if (!selectedEntry.IsStarter && Button(new Rectangle(detail.X + 274, detail.Bottom - 62, 100, 38), "Rename"))
                    {
                        _deckNameText = selectedEntry.Deck.Name;
                        _deckNameEditing = true;
                    }
                    if (!selectedEntry.IsStarter && Button(new Rectangle(detail.X + 386, detail.Bottom - 62, 100, 38), "Delete"))
                    {
                        _deckLibraryDeleteConfirmation = true;
                    }
                }
            }

            if (Button(new Rectangle(panel.X + 30, panel.Bottom - 64, 146, 38), "New Deck"))
            {
                DuplicateLibraryDeck(_deckBuilder.CreateDeck());
            }
            if (Button(new Rectangle(panel.Right - 142, panel.Bottom - 64, 112, 38), "Close"))
            {
                _deckLibraryOpen = false;
                _deckNameEditing = false;
            }

            if (_deckLibraryDeleteConfirmation && selectedEntry is not null)
            {
                DrawDeckDeleteConfirmation(selectedEntry);
            }
        }
        finally
        {
            _drawingModal = false;
        }
    }

    private void DrawDeckDeleteConfirmation(DeckLibraryEntry entry)
    {
        Fill(new Rectangle(0, 0, VirtualWidth, VirtualHeight), new Color(0, 0, 0, 122));
        var panel = new Rectangle(510, 326, 580, 248);
        DrawPanel(panel, UiTheme.PanelRaised, border: UiTheme.Danger);
        DrawUiText("Delete Deck?", new Vector2(panel.X + 28, panel.Y + 24), Color.White, 0.88f);
        DrawUiText($"Delete {entry.Deck.Name} permanently from this profile?", new Rectangle(panel.X + 28, panel.Y + 70, panel.Width - 56, 54), UiTheme.Text, 0.58f);
        if (Button(new Rectangle(panel.X + 28, panel.Bottom - 58, 166, 38), "Delete Deck", focused: _usingController))
        {
            DeleteLibraryDeck(entry.Deck);
        }
        if (Button(new Rectangle(panel.Right - 130, panel.Bottom - 58, 102, 38), "Cancel"))
        {
            _deckLibraryDeleteConfirmation = false;
        }
    }

    private void LoadDeckLibraryEntry(DeckLibraryEntry entry)
    {
        _deckBuilder = CreateDeckBuilderState(entry.Deck);
        if (_profile is not null)
        {
            _profile.ActiveDeckId = entry.Deck.Id;
            SaveProfile();
        }

        _deckLibraryOpen = false;
        _deckNameEditing = false;
        _status = $"Loaded {entry.Deck.Name}.";
    }

    private void DuplicateLibraryDeck(DeckDefinition source)
    {
        if (_profileRepository is null || string.IsNullOrWhiteSpace(_activeProfileId))
        {
            _status = "Select a local profile before creating a deck.";
            return;
        }

        var deck = source with
        {
            Id = $"deck-{Guid.NewGuid():N}",
            Name = UniqueDeckName($"{source.Name} Copy"),
            Cards = source.Cards.ToDictionary(card => card.Key, card => card.Value, StringComparer.OrdinalIgnoreCase)
        };
        if (!_profileRepository.TrySaveDeck(_activeProfileId, deck, out var error))
        {
            _status = error ?? "Could not create the deck.";
            return;
        }

        _deckBuilder = CreateDeckBuilderState(deck);
        _profile!.ActiveDeckId = deck.Id;
        SaveProfile();
        _deckLibraryOpen = false;
        _deckNameEditing = false;
        _status = $"Created {deck.Name}.";
    }

    private void RenameLibraryDeck()
    {
        var entries = DeckLibraryEntries();
        if (_profileRepository is null || string.IsNullOrWhiteSpace(_activeProfileId) || _deckLibraryFocus >= entries.Count || entries[_deckLibraryFocus].IsStarter)
        {
            return;
        }

        var entry = entries[_deckLibraryFocus];
        var name = _deckNameText.Trim();
        if (name.Length is < 1 or > 32)
        {
            _status = "Deck names must be 1-32 characters.";
            return;
        }
        if (DeckLibraryEntries().Any(other => !other.Deck.Id.Equals(entry.Deck.Id, StringComparison.OrdinalIgnoreCase) && other.Deck.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            _status = "Deck names must be unique in this profile.";
            return;
        }

        var renamed = entry.Deck with { Name = name };
        if (!_profileRepository.TrySaveDeck(_activeProfileId, renamed, out var error))
        {
            _status = error ?? "Could not rename the deck.";
            return;
        }

        if (_deckBuilder.DeckId.Equals(renamed.Id, StringComparison.OrdinalIgnoreCase))
        {
            _deckBuilder.SetIdentity(renamed.Id, renamed.Name, renamed.ModeId);
        }
        _deckNameEditing = false;
        _status = $"Renamed deck to {renamed.Name}.";
    }

    private void DeleteLibraryDeck(DeckDefinition deck)
    {
        if (_profileRepository is null || string.IsNullOrWhiteSpace(_activeProfileId))
        {
            return;
        }

        if (!_profileRepository.TryDeleteDeck(_activeProfileId, deck.Id, out var error))
        {
            _status = error ?? "Could not delete the deck.";
            return;
        }

        if (_profile is not null && _profile.ActiveDeckId.Equals(deck.Id, StringComparison.OrdinalIgnoreCase))
        {
            var fallback = StarterDecks().FirstOrDefault(item => item.Id.Equals(_profile.SelectedStarterDeckId, StringComparison.OrdinalIgnoreCase)) ?? StarterDecks().First();
            _profile.ActiveDeckId = fallback.Id;
            _deckBuilder = CreateDeckBuilderState(fallback);
            SaveProfile();
        }

        _deckLibraryFocus = Math.Max(0, _deckLibraryFocus - 1);
        _deckLibraryDeleteConfirmation = false;
        _status = $"Deleted {deck.Name}.";
    }

    private string UniqueDeckName(string baseName)
    {
        var existing = DeckLibraryEntries().Select(entry => entry.Deck.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = string.IsNullOrWhiteSpace(baseName) ? "New Deck" : baseName.Trim();
        if (!existing.Contains(candidate))
        {
            return candidate;
        }

        for (var index = 2; index < 1000; index++)
        {
            var numbered = $"{candidate} {index}";
            if (!existing.Contains(numbered))
            {
                return numbered;
            }
        }

        return $"Deck {Guid.NewGuid():N}";
    }

    private void DrawStore()
    {
        DrawText("Store / Packs", new Vector2(54, 108), Color.White, 1.2f);
        DrawText(_profile is null ? "Create a profile to use the store." : $"{_profile.Coins} Coins   {BoosterService.TotalUnopenedPacks(_profile)} unopened pack(s)", new Vector2(56, 146), new Color(196, 207, 220), 0.72f);
        var rules = CurrentRules();
        var catalog = ShopCatalogService.CreateCatalog(_data);
        _storeFocus = Math.Clamp(_storeFocus, 0, Math.Max(0, catalog.Count - 1));
        var selected = catalog.Count == 0 ? null : catalog[_storeFocus];

        var listPanel = new Rectangle(54, 198, 690, 560);
        DrawPanel(listPanel, new Color(31, 37, 46), border: new Color(81, 96, 116));
        DrawText("Catalog", new Vector2(listPanel.X + 24, listPanel.Y + 20), Color.White, 0.88f);
        var rowHeight = 62;
        var visibleRows = 7;
        _storeScrollOffset = Math.Clamp(_storeScrollOffset, 0, Math.Max(0, catalog.Count - visibleRows));
        if (_storeFocus < _storeScrollOffset)
        {
            _storeScrollOffset = _storeFocus;
        }
        else if (_storeFocus >= _storeScrollOffset + visibleRows)
        {
            _storeScrollOffset = _storeFocus - visibleRows + 1;
        }

        for (var row = 0; row < visibleRows && _storeScrollOffset + row < catalog.Count; row++)
        {
            var index = _storeScrollOffset + row;
            var item = catalog[index];
            var rect = new Rectangle(listPanel.X + 24, listPanel.Y + 64 + row * rowHeight, listPanel.Width - 48, 52);
            var isSelected = index == _storeFocus;
            DrawPanel(rect, isSelected ? new Color(54, 68, 83) : new Color(38, 45, 56), border: isSelected ? new Color(244, 230, 158) : new Color(76, 90, 110));
            DrawText(item.Name, new Vector2(rect.X + 16, rect.Y + 8), Color.White, 0.58f);
            DrawText($"{StoreKindLabel(item)}   {StoreItemStatus(item, rules)}", new Vector2(rect.X + 16, rect.Y + 30), new Color(205, 214, 225), 0.42f);
            if (Button(new Rectangle(rect.Right - 88, rect.Y + 10, 70, 32), "View", selected: isSelected))
            {
                _storeFocus = index;
                _cardDetailScrollOffset = 0;
            }
        }

        DrawStoreDetail(new Rectangle(772, 198, 772, 560), selected, rules);

        if (Button(new Rectangle(54, 786, 150, 42), "Back"))
        {
            _screen = Screen.MainMenu;
            _status = "Returned to main menu.";
        }
    }

    private void DrawPackOpening()
    {
        DrawText("Pack Opening", new Vector2(54, 108), Color.White, 1.2f);
        DrawText(_lastBoosterOpening is null ? "No pack opened." : $"{_lastBoosterOpening.PackName} x{_lastBoosterOpening.PackCount}   Duplicate coins: {_lastBoosterOpening.CoinsFromDuplicates}", new Vector2(56, 146), new Color(196, 207, 220), 0.72f);
        var panel = new Rectangle(54, 198, 1490, 560);
        DrawPanel(panel, new Color(31, 37, 46), border: new Color(81, 96, 116));

        if (_lastBoosterOpening is not null)
        {
            var columns = 8;
            var rowHeight = 238;
            var visibleRows = 2;
            var maxRow = Math.Max(0, (int)Math.Ceiling(_lastBoosterOpening.Cards.Count / (double)columns) - visibleRows);
            _packOpeningScrollOffset = Math.Clamp(_packOpeningScrollOffset, 0, maxRow);
            var start = _packOpeningScrollOffset * columns;
            var end = Math.Min(_lastBoosterOpening.Cards.Count, start + columns * visibleRows);
            for (var i = start; i < end; i++)
            {
                var grant = _lastBoosterOpening.Cards[i];
                if (!_data.CardsById.TryGetValue(grant.CardId, out var card))
                {
                    continue;
                }

                var local = i - start;
                var column = local % columns;
                var row = local / columns;
                var rect = new Rectangle(panel.X + 30 + column * 180, panel.Y + 44 + row * rowHeight, 148, 208);
                DrawCardFrame(rect, card, selected: true, exhausted: false, count: grant.CopiesAdded, compact: false);
                var note = grant.CopiesAdded > 0 ? $"+{grant.CopiesAdded} copy" : $"+{grant.DuplicateCoins} Coins";
                DrawFittedCenteredText(note, new Rectangle(rect.X, rect.Bottom + 10, rect.Width, 24), grant.CopiesAdded > 0 ? new Color(148, 224, 164) : new Color(244, 230, 158), 0.46f, 0.32f);
            }

            if (maxRow > 0)
            {
                DrawText($"Rows {_packOpeningScrollOffset + 1}-{Math.Min(_packOpeningScrollOffset + visibleRows, maxRow + visibleRows)}", new Vector2(panel.Right - 156, panel.Bottom - 34), new Color(196, 207, 220), 0.48f);
            }
        }

        if (Button(new Rectangle(54, 786, 150, 42), "Continue"))
        {
            _screen = Screen.Store;
            _status = "Returned to store.";
        }
    }

    private void DrawMatchResult()
    {
        var title = _lastMatchWon ? "Victory" : "Defeat";
        DrawWithOpacity(ResultRevealOpacity(0f), () =>
            DrawText(title, new Vector2(54, 108), _lastMatchWon ? new Color(148, 224, 164) : new Color(255, 162, 128), 1.3f));
        var panel = new Rectangle(54, 198, 1490, 520);
        DrawPanel(panel, new Color(31, 37, 46), border: new Color(81, 96, 116));
        DrawText($"Rules: {CurrentRules().Preset} / {CurrentRules().Playstyle}", new Vector2(panel.X + 36, panel.Y + 36), Color.White, 0.8f);

        if (_lastMatchReward is { ProgressionApplied: true } reward)
        {
            DrawWithOpacity(ResultRevealOpacity(0.08f), () =>
                DrawText($"+{reward.ExperienceGained} XP", new Vector2(panel.X + 36, panel.Y + 96), new Color(148, 224, 164), 0.9f));
            DrawWithOpacity(ResultRevealOpacity(0.14f), () =>
                DrawText($"+{reward.CoinsGained} Coins", new Vector2(panel.X + 36, panel.Y + 144), new Color(244, 230, 158), 0.9f));
            DrawWithOpacity(ResultRevealOpacity(0.20f), () =>
                DrawText(reward.LevelsGained > 0 ? $"Level {reward.StartingLevel} -> {reward.EndingLevel}" : $"Level {_profile?.Level ?? 1}", new Vector2(panel.X + 36, panel.Y + 192), Color.White, 0.76f));
            DrawWithOpacity(ResultRevealOpacity(0.26f), () =>
                DrawText(reward.BoostersGained > 0 ? $"+{reward.BoostersGained} booster reward" : "No level booster this match.", new Vector2(panel.X + 36, panel.Y + 238), new Color(205, 214, 225), 0.68f));
            var spoilsOpacity = ResultRevealOpacity(0.32f);
            var spoilsLift = _settings.ReducedMotion ? 0 : (int)MathF.Round((1f - spoilsOpacity) * 8f);
            DrawWithOpacity(spoilsOpacity, () =>
                DrawBattleSpoils(new Rectangle(panel.X + 36, panel.Y + 270 + spoilsLift, 990, 260)));
        }
        else
        {
            DrawWithOpacity(ResultRevealOpacity(0.10f), () =>
                DrawText("Progression rewards disabled for this rules configuration.", new Rectangle(panel.X + 36, panel.Y + 96, 650, 72), new Color(255, 190, 120), 0.76f));
        }

        if (_profile is not null)
        {
            DrawProfileSummary(new Rectangle(panel.Right - 430, panel.Y + 36, 360, 214));
        }

        var questSummary = _lastQuestRewards.Count == 0
            ? "Quest progress updated for this eligible match."
            : string.Join("  ", _lastQuestRewards.Select(reward =>
                $"Quest complete: +{reward.Coins} Coins{(reward.StandardPacks > 0 ? $" +{reward.StandardPacks} Pack" : "")}"));
        DrawFittedText(questSummary, new Vector2(panel.Right - 430, panel.Y + 266), 360,
            _lastQuestRewards.Count > 0 ? new Color(148, 224, 164) : new Color(205, 214, 225), 0.54f, 0.38f);

        DrawText(ResultNextStepText(), new Rectangle(panel.Right - 430, panel.Y + 320, 360, 96), new Color(205, 214, 225), 0.6f);

        var y = 786;
        if (Button(new Rectangle(54, y, 150, 42), "Continue",
            focused: _usingController && _resultFocus == 0))
        {
            ActivateResultAction(0);
        }

        if (Button(new Rectangle(224, y, 150, 42), "Rematch", _rematchFirstDeck is not null && _rematchSecondDeck is not null,
            focused: _usingController && _resultFocus == 1))
        {
            ActivateResultAction(1);
        }

        if (Button(new Rectangle(394, y, 150, 42), "Deck Builder",
            focused: _usingController && _resultFocus == 2))
        {
            ActivateResultAction(2);
        }

        if (Button(new Rectangle(564, y, 150, 42), "Store / Packs",
            focused: _usingController && _resultFocus == 3))
        {
            ActivateResultAction(3);
        }
    }

    private void DrawQuestBoard()
    {
        DrawText("Quest Board", new Vector2(54, 108), Color.White, 1.2f);
        DrawText("Complete eligible matches in Dragon Duel or Starter Clash. Rewards are applied automatically.",
            new Rectangle(56, 146, 1080, 34), UiTheme.TextMuted, 0.62f);

        if (_profile is null)
        {
            DrawText("Create or select a profile to track quests.", new Vector2(56, 218), UiTheme.TextMuted, 0.72f);
            return;
        }

        var refresh = QuestService.Refresh(_profile, DateTimeOffset.UtcNow);
        if (refresh.StateChanged)
        {
            SaveProfile();
        }

        var panel = new Rectangle(54, 206, 1490, 510);
        DrawPanel(panel, new Color(31, 37, 46), border: new Color(81, 96, 116));
        for (var i = 0; i < QuestService.Definitions.Count; i++)
        {
            var quest = QuestService.Definitions[i];
            var entry = QuestService.EntryFor(_profile, quest);
            var row = new Rectangle(panel.X + 28, panel.Y + 26 + i * 92, panel.Width - 56, 72);
            Fill(row, entry.Completed ? new Color(40, 68, 57) : UiTheme.PanelInset);
            Border(row, entry.Completed ? new Color(148, 224, 164) : UiTheme.BorderStrong, 1);
            DrawText(quest.Cadence == QuestCadence.Daily ? "DAILY" : "WEEKLY", new Vector2(row.X + 18, row.Y + 14), UiTheme.DragonGold, 0.54f);
            DrawText(quest.Title, new Vector2(row.X + 130, row.Y + 12), Color.White, 0.72f);
            DrawText(entry.Completed ? "Complete" : $"{entry.Progress}/{quest.Target}", new Vector2(row.X + 130, row.Y + 42),
                entry.Completed ? new Color(148, 224, 164) : new Color(205, 214, 225), 0.56f);
            var rewardText = $"+{quest.Coins} Coins" + (quest.StandardPacks > 0 ? $"  +{quest.StandardPacks} Standard Pack" : "");
            DrawFittedText(rewardText, new Vector2(row.Right - 320, row.Y + 24), 284, UiTheme.DragonGold, 0.58f, 0.42f);
        }

        DrawText($"Daily reset: {QuestService.DailyPeriod(DateTimeOffset.UtcNow)} UTC - Weekly reset: {QuestService.WeeklyPeriod(DateTimeOffset.UtcNow)} UTC",
            new Vector2(panel.X + 28, panel.Bottom + 22), UiTheme.TextMuted, 0.54f);
        if (Button(new Rectangle(54, 786, 150, 42), "Back", focused: _usingController))
        {
            _screen = Screen.MainMenu;
        }
    }

    private float ResultRevealOpacity(float delaySeconds)
    {
        if (_settings.ReducedMotion)
        {
            return 1f;
        }

        var progress = Math.Clamp((_screenElapsed - delaySeconds) / 0.18f, 0f, 1f);
        return progress * progress * (3f - 2f * progress);
    }

    private string ResultNextStepText()
    {
        if (_profile is null)
        {
            return "Next: create a profile to save progression rewards.";
        }

        if (_profile.TotalUnopenedPacks > 0)
        {
            return "Next: open your packs, then use the Deck Assistant to upgrade your list.";
        }

        if (_profile.Coins >= ProgressionService.BoosterCost)
        {
            return "Next: buy a booster in Store / Packs or tune your deck with the Deck Assistant.";
        }

        if (_profile.CompletedTutorialIds.Count < TutorialDefinitions.Count)
        {
            return "Next: finish Tutorial Trials for one-time Coins and smoother play.";
        }

        return _lastMatchWon
            ? "Next: rematch on a harder preset or improve your deck with new pulls."
            : "Next: open Deck Builder and ask the Assistant for cuts and adds.";
    }

    private void DrawStoreDetail(Rectangle panel, ShopCatalogItem? item, GameRulesConfig rules)
    {
        DrawPanel(panel, new Color(31, 37, 46), border: new Color(81, 96, 116));
        if (item is null)
        {
            DrawText("No catalog item selected.", new Rectangle(panel.X + 28, panel.Y + 28, panel.Width - 56, 40), new Color(205, 214, 225), 0.7f);
            return;
        }

        DrawText(item.Name, new Vector2(panel.X + 28, panel.Y + 26), Color.White, 0.88f);
        DrawText(item.Description, new Rectangle(panel.X + 28, panel.Y + 66, panel.Width - 56, 54), new Color(205, 214, 225), 0.58f);
        DrawText(StoreItemStatus(item, rules), new Vector2(panel.X + 28, panel.Y + 124), rules.IsProgressionSafe ? new Color(244, 230, 158) : new Color(255, 190, 120), 0.58f);

        if (item.Kind == ShopItemKind.Booster)
        {
            var unopened = _profile is null ? 0 : BoosterService.GetUnopenedPackCount(_profile, item.PackId);
            DrawText($"Unopened: {unopened}", new Vector2(panel.X + 28, panel.Y + 172), new Color(205, 214, 225), 0.68f);
            DrawText("Quantity", new Vector2(panel.X + 28, panel.Y + 220), Color.White, 0.66f);
            var labels = new[] { "1", "5", "10", "Max" };
            _storeQuantityIndex = Math.Clamp(_storeQuantityIndex, 0, labels.Length - 1);
            for (var i = 0; i < labels.Length; i++)
            {
                if (Button(new Rectangle(panel.X + 28 + i * 88, panel.Y + 254, 76, 36), labels[i], selected: _storeQuantityIndex == i,
                    focused: _usingController && _storeFocusArea == StoreFocusArea.Detail && _storeDetailFocus == 0 && _storeQuantityIndex == i))
                {
                    _storeQuantityIndex = i;
                }
            }

            var buyQuantity = StoreBuyQuantity(item);
            var openQuantity = StoreOpenQuantity(item);
            if (Button(new Rectangle(panel.X + 28, panel.Y + 322, 180, 42), $"Buy x{buyQuantity}", _profile is not null && rules.IsProgressionSafe && buyQuantity > 0 && _profile.Coins >= item.Cost * buyQuantity,
                focused: _usingController && _storeFocusArea == StoreFocusArea.Detail && _storeDetailFocus == 1))
            {
                BuyCatalogBooster(item, buyQuantity);
            }

            if (Button(new Rectangle(panel.X + 228, panel.Y + 322, 180, 42), $"Open x{openQuantity}", _profile is not null && openQuantity > 0,
                focused: _usingController && _storeFocusArea == StoreFocusArea.Detail && _storeDetailFocus == 2))
            {
                OpenCatalogBooster(item, openQuantity);
            }

            DrawText("Rare+ slot odds: 84% Rare, 15% Legendary, 1% Mythic.", new Rectangle(panel.X + 28, panel.Y + 404, panel.Width - 56, 48), new Color(196, 207, 220), 0.56f);
            DrawText(!rules.IsProgressionSafe ? "Sandbox store preview: purchases disabled." : "", new Rectangle(panel.X + 28, panel.Y + 464, panel.Width - 56, 42), new Color(255, 190, 120), 0.56f);
            return;
        }

        if (item.Kind == ShopItemKind.StarterDeck && _data.DecksById.TryGetValue(item.DeckId, out var deck))
        {
            var owned = _profile is not null && (rules.AllUnlocks || PlayerCollection.HasStarterDeck(_profile, deck.Id));
            DrawText($"{deck.Count}/50 cards", new Vector2(panel.X + 28, panel.Y + 174), new Color(205, 214, 225), 0.68f);
            DrawText(owned ? "Owned / unlocked" : $"{item.Cost} Coins", new Vector2(panel.X + 28, panel.Y + 212), owned ? new Color(148, 224, 164) : new Color(244, 230, 158), 0.68f);
            if (Button(new Rectangle(panel.X + 28, panel.Y + 272, 176, 42), owned || !rules.IsProgressionSafe ? "Open" : "Buy", _profile is not null && (owned || !rules.IsProgressionSafe || _profile.Coins >= item.Cost),
                focused: _usingController && _storeFocusArea == StoreFocusArea.Detail))
            {
                BuyOrSelectStarter(deck);
            }

            return;
        }

        if (item.Kind == ShopItemKind.SingleCard && _data.CardsById.TryGetValue(item.CardId, out var card))
        {
            DrawCardFrame(new Rectangle(panel.X + 28, panel.Y + 154, 178, 250), card, selected: true, exhausted: false, count: _profile is null ? 0 : PlayerCollection.CountOwned(_profile, card.Id), compact: false);
            var detailRect = new Rectangle(panel.X + 232, panel.Y + 154, panel.Width - 266, 286);
            DrawScrollableText(CardDetailText(card), detailRect, ref _cardDetailScrollOffset, new Color(211, 220, 231), CardDetailTextScale);
            var owned = _profile is null ? 0 : PlayerCollection.CountOwned(_profile, card.Id);
            DrawText($"Owned {owned}/{PlayerCollection.MaxOwnedCopies}   {item.Cost} Coins", new Vector2(panel.X + 232, panel.Y + 462), owned >= PlayerCollection.MaxOwnedCopies ? new Color(255, 190, 120) : new Color(244, 230, 158), 0.58f);
            if (Button(new Rectangle(panel.X + 232, panel.Y + 502, 176, 38), "Buy Single", _profile is not null && rules.IsProgressionSafe && owned < PlayerCollection.MaxOwnedCopies && _profile.Coins >= item.Cost,
                focused: _usingController && _storeFocusArea == StoreFocusArea.Detail))
            {
                BuySingleCard(card);
            }
        }
    }

    private void DrawBattleSpoils(Rectangle rect)
    {
        if (_lastBattleSpoils is null)
        {
            DrawText("Victory spoils: none.", new Rectangle(rect.X, rect.Y, rect.Width, 32), new Color(205, 214, 225), 0.58f);
            return;
        }

        if (_lastBattleSpoils.Grant is not { } grant)
        {
            DrawText($"Victory spoils: {_lastBattleSpoils.Reason}", new Rectangle(rect.X, rect.Y, rect.Width, 46), new Color(205, 214, 225), 0.56f);
            return;
        }

        if (!_data.CardsById.TryGetValue(grant.CardId, out var card))
        {
            DrawText("Victory Spoils", new Vector2(rect.X, rect.Y), Color.White, 0.68f);
            DrawText($"{grant.CardName} ({grant.Rarity})", new Vector2(rect.X, rect.Y + 38), new Color(244, 230, 158), 0.62f);
            var fallbackNote = grant.CopiesAdded > 0 ? $"+{grant.CopiesAdded} copy from opponent deck" : $"+{grant.DuplicateCoins} duplicate Coins";
            DrawText(fallbackNote, new Rectangle(rect.X, rect.Y + 72, rect.Width, 46), grant.CopiesAdded > 0 ? new Color(148, 224, 164) : new Color(244, 230, 158), 0.56f);
            return;
        }

        DrawPanel(rect, new Color(26, 32, 41), border: new Color(91, 107, 128));
        DrawText("Victory Spoils", new Vector2(rect.X + 18, rect.Y + 12), Color.White, 0.64f);
        var cardRect = new Rectangle(rect.X + 18, rect.Y + 42, 142, 200);
        DrawCardFrame(cardRect, card, selected: true, exhausted: false, count: grant.CopiesAdded, compact: false);
        var titleX = cardRect.Right + 22;
        DrawRarityBadge(new Rectangle(titleX, rect.Y + 46, 92, 22), grant.Rarity, compact: false);
        DrawFittedText(grant.CardName, new Vector2(titleX + 104, rect.Y + 48), rect.Right - titleX - 124, new Color(244, 230, 158), 0.62f, 0.34f);
        var note = grant.CopiesAdded > 0 ? $"+{grant.CopiesAdded} copy from opponent deck" : $"+{grant.DuplicateCoins} duplicate Coins";
        DrawText(note, new Rectangle(titleX, rect.Y + 78, rect.Right - titleX - 18, 28), grant.CopiesAdded > 0 ? new Color(148, 224, 164) : new Color(244, 230, 158), 0.5f);
        DrawScrollableText(CardDetailText(card), new Rectangle(titleX, rect.Y + 112, rect.Right - titleX - 18, rect.Bottom - rect.Y - 130), ref _cardDetailScrollOffset, new Color(211, 220, 231), SmallCardDetailTextScale);
    }

    private string StoreKindLabel(ShopCatalogItem item) => item.Kind switch
    {
        ShopItemKind.Booster => "Booster",
        ShopItemKind.StarterDeck => "Starter",
        ShopItemKind.SingleCard => "Single",
        _ => "Item"
    };

    private string StoreItemStatus(ShopCatalogItem item, GameRulesConfig rules)
    {
        if (!rules.IsProgressionSafe)
        {
            return "Sandbox preview";
        }

        if (_profile is null)
        {
            return $"{item.Cost} Coins";
        }

        return item.Kind switch
        {
            ShopItemKind.Booster => $"{item.Cost} Coins   {_profile.UnopenedPacks.GetValueOrDefault(item.PackId)} unopened",
            ShopItemKind.StarterDeck when PlayerCollection.HasStarterDeck(_profile, item.DeckId) => "Owned",
            ShopItemKind.SingleCard when _data.CardsById.TryGetValue(item.CardId, out var card) => $"{PlayerCollection.CountOwned(_profile, card.Id)}/{PlayerCollection.MaxOwnedCopies} owned   {item.Cost} Coins",
            _ => $"{item.Cost} Coins"
        };
    }

    private int StoreBuyQuantity(ShopCatalogItem item)
    {
        if (_storeQuantityIndex < 3)
        {
            return new[] { 1, 5, 10 }[_storeQuantityIndex];
        }

        return _profile is null || item.Cost <= 0 ? 1 : Math.Max(1, _profile.Coins / item.Cost);
    }

    private int StoreOpenQuantity(ShopCatalogItem item)
    {
        var unopened = _profile is null ? 0 : BoosterService.GetUnopenedPackCount(_profile, item.PackId);
        if (_storeQuantityIndex < 3)
        {
            return Math.Min(unopened, new[] { 1, 5, 10 }[_storeQuantityIndex]);
        }

        return unopened;
    }

    private void HandlePlayerCreationController()
    {
        if (FocusPressed(out var focusDelta))
        {
            _creationControlFocus = (_creationControlFocus + focusDelta + 4) % 4;
        }
        if (_uiActions.Triggered(UiAction.MoveToStart)) _creationControlFocus = 0;
        else if (_uiActions.Triggered(UiAction.MoveToEnd)) _creationControlFocus = 3;
        if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical))
        {
            _creationControlFocus = Math.Clamp(_creationControlFocus + vertical, 0, 3);
        }

        if (DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out var horizontal))
        {
            if (_creationControlFocus == 0)
            {
                _creationPresetIndex = Math.Clamp(_creationPresetIndex + horizontal, 0, RulesPresetOrder.Length - 1);
            }
            else if (_creationControlFocus == 1)
            {
                _creationPlaystyleIndex = Math.Clamp(_creationPlaystyleIndex + horizontal, 0, PlaystyleOrder.Length - 1);
            }
            else if (_creationControlFocus == 2)
            {
                _creationStarterIndex = Math.Clamp(_creationStarterIndex + horizontal, 0, StarterDecks().Count - 1);
            }
        }

        if (Pressed(Buttons.A))
        {
            _usingController = true;
            if (_creationControlFocus == 3)
            {
                CreateProfileFromSelection();
            }
            else
            {
                _creationControlFocus++;
            }
        }
    }

    private void HandleProfilePickerController()
    {
        var profiles = _profileRepository?.Profiles ?? [];
        if (_profileDeleteConfirmation)
        {
            if (Pressed(Buttons.A))
            {
                DeleteSelectedProfile();
            }
            else if (Pressed(Buttons.B))
            {
                _profileDeleteConfirmation = false;
            }

            return;
        }

        if (_profileRenameActive)
        {
            if (Pressed(Buttons.A))
            {
                RenameSelectedProfile();
            }
            else if (Pressed(Buttons.B))
            {
                _profileRenameActive = false;
            }

            return;
        }

        if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical))
        {
            _profilePickerFocus = Math.Clamp(_profilePickerFocus + vertical, 0, profiles.Count);
        }
        else if (_uiActions.Triggered(UiAction.PagePrevious))
        {
            _profilePickerFocus = Math.Max(0, _profilePickerFocus - 6);
        }
        else if (_uiActions.Triggered(UiAction.PageNext))
        {
            _profilePickerFocus = Math.Min(profiles.Count, _profilePickerFocus + 6);
        }
        else if (_uiActions.Triggered(UiAction.MoveToStart))
        {
            _profilePickerFocus = 0;
        }
        else if (_uiActions.Triggered(UiAction.MoveToEnd))
        {
            _profilePickerFocus = profiles.Count;
        }

        if (Pressed(Buttons.X) || Pressed(Keys.R))
        {
            if (_profilePickerFocus < profiles.Count && TryProfilePreview(profiles[_profilePickerFocus], out var profile))
            {
                _profileRenameText = profile.PlayerName;
                _profileRenameActive = true;
            }

            return;
        }

        if (Pressed(Buttons.Y) || Pressed(Keys.Delete))
        {
            if (_profilePickerFocus < profiles.Count)
            {
                _profileDeleteConfirmation = true;
            }

            return;
        }

        if (Pressed(Buttons.A))
        {
            if (_profilePickerFocus < profiles.Count)
            {
                SelectProfile(profiles[_profilePickerFocus].Id);
            }
            else
            {
                BeginProfileCreation();
            }
        }
    }

    private void HandleStoreController()
    {
        var catalog = ShopCatalogService.CreateCatalog(_data);
        if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical))
        {
            _storeFocus = Math.Clamp(_storeFocus + vertical, 0, Math.Max(0, catalog.Count - 1));
        }

        if (DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out var horizontal))
        {
            _storeQuantityIndex = Math.Clamp(_storeQuantityIndex + horizontal, 0, 3);
        }

        if (Pressed(Buttons.A) && catalog.Count > 0)
        {
            var item = catalog[_storeFocus];
            if (item.Kind == ShopItemKind.Booster)
            {
                var openQuantity = StoreOpenQuantity(item);
                if (openQuantity > 0)
                {
                    OpenCatalogBooster(item, openQuantity);
                }
                else
                {
                    BuyCatalogBooster(item, StoreBuyQuantity(item));
                }
            }
            else if (item.Kind == ShopItemKind.StarterDeck && _data.DecksById.TryGetValue(item.DeckId, out var deck))
            {
                BuyOrSelectStarter(deck);
            }
            else if (item.Kind == ShopItemKind.SingleCard && _data.CardsById.TryGetValue(item.CardId, out var card))
            {
                BuySingleCard(card);
            }
        }
    }

    private void HandlePackOpeningController()
    {
        if (Pressed(Buttons.A))
        {
            _screen = Screen.Store;
        }
    }

    private void HandleMatchResultController()
    {
        if (FocusPressed(out var focusDelta))
        {
            _resultFocus = (_resultFocus + focusDelta + 4) % 4;
        }
        if (_uiActions.Triggered(UiAction.MoveToStart)) _resultFocus = 0;
        else if (_uiActions.Triggered(UiAction.MoveToEnd)) _resultFocus = 3;
        if (DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out var horizontal))
        {
            _resultFocus = Math.Clamp(_resultFocus + horizontal, 0, 3);
        }
        else if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical))
        {
            _resultFocus = Math.Clamp(_resultFocus + vertical, 0, 3);
        }

        if (_uiActions.Triggered(UiAction.PagePrevious))
        {
            _cardDetailScrollOffset = Math.Max(0, _cardDetailScrollOffset - 8);
        }
        else if (_uiActions.Triggered(UiAction.PageNext))
        {
            _cardDetailScrollOffset += 8;
        }
        else if (_uiActions.Triggered(UiAction.MoveToStart))
        {
            _cardDetailScrollOffset = 0;
        }
        else if (_uiActions.Triggered(UiAction.MoveToEnd))
        {
            _cardDetailScrollOffset = int.MaxValue;
        }

        if (Pressed(Buttons.A))
        {
            _usingController = true;
            ActivateResultAction(_resultFocus);
        }
    }

    private void ActivateResultAction(int action)
    {
        if (_rematchKind == MatchKind.Online)
        {
            CloseNetworkMatchConnection();
        }

        switch (Math.Clamp(action, 0, 3))
        {
            case 0:
                _screen = Screen.MainMenu;
                _status = "Returned to main menu.";
                break;
            case 1 when _rematchKind == MatchKind.Online:
                _multiplayerSection = MultiplayerSection.HostLobby;
                _directLobbyState = DirectLobbyState.Idle;
                _screen = Screen.Multiplayer;
                _multiplayerNotice = "Create a new direct lobby to rematch.";
                _status = "Direct matches rematch through a new lobby.";
                break;
            case 1 when _rematchFirstDeck is not null && _rematchSecondDeck is not null:
                StartMatch(_rematchFirstDeck, _rematchSecondDeck, _rematchKind, _rematchModeId);
                break;
            case 2:
                _screen = Screen.DeckBuilder;
                _status = "Deck builder opened.";
                break;
            case 3:
                _screen = Screen.Store;
                _status = "Store opened.";
                break;
        }
    }

    private void HandlePlayerCreationTextInput()
    {
        if (_screen != Screen.PlayerCreation)
        {
            return;
        }

        if (Pressed(Keys.Back) && _creationName.Length > 0)
        {
            _creationName = _creationName[..^1];
            return;
        }

        if (Pressed(Keys.Space) && _creationName.Length < 18)
        {
            _creationName += " ";
            return;
        }

        var shift = _keyboard.IsKeyDown(Keys.LeftShift) || _keyboard.IsKeyDown(Keys.RightShift);
        foreach (var key in _keyboard.GetPressedKeys())
        {
            if (_previousKeyboard.IsKeyDown(key) || _creationName.Length >= 18)
            {
                continue;
            }

            if (key is >= Keys.A and <= Keys.Z)
            {
                var c = (char)('a' + (key - Keys.A));
                _creationName += shift ? char.ToUpperInvariant(c) : c;
            }
            else if (key is >= Keys.D0 and <= Keys.D9)
            {
                _creationName += (char)('0' + (key - Keys.D0));
            }
        }

        _creationName = _creationName.TrimStart();
        if (string.IsNullOrWhiteSpace(_creationName))
        {
            _creationName = "";
        }
    }

    private void HandleProfileRenameTextInput()
    {
        if (_screen != Screen.ProfilePicker || !_profileRenameActive)
        {
            return;
        }

        if (Pressed(Keys.Back) && _profileRenameText.Length > 0)
        {
            _profileRenameText = _profileRenameText[..^1];
            return;
        }

        if (Pressed(Keys.Space) && _profileRenameText.Length < 18)
        {
            _profileRenameText += " ";
            return;
        }

        var shift = _keyboard.IsKeyDown(Keys.LeftShift) || _keyboard.IsKeyDown(Keys.RightShift);
        foreach (var key in _keyboard.GetPressedKeys())
        {
            if (_previousKeyboard.IsKeyDown(key) || _profileRenameText.Length >= 18)
            {
                continue;
            }

            if (key is >= Keys.A and <= Keys.Z)
            {
                var character = (char)('a' + (key - Keys.A));
                _profileRenameText += shift ? char.ToUpperInvariant(character) : character;
            }
            else if (key is >= Keys.D0 and <= Keys.D9)
            {
                _profileRenameText += (char)('0' + (key - Keys.D0));
            }
        }

        _profileRenameText = _profileRenameText.TrimStart();
    }

    private void HandleDeckNameTextInput()
    {
        if (_screen != Screen.DeckBuilder || !_deckLibraryOpen || !_deckNameEditing)
        {
            return;
        }

        if (Pressed(Keys.Back) && _deckNameText.Length > 0)
        {
            _deckNameText = _deckNameText[..^1];
            return;
        }

        if (Pressed(Keys.Space) && _deckNameText.Length < 32)
        {
            _deckNameText += " ";
            return;
        }

        var shift = _keyboard.IsKeyDown(Keys.LeftShift) || _keyboard.IsKeyDown(Keys.RightShift);
        foreach (var key in _keyboard.GetPressedKeys())
        {
            if (_previousKeyboard.IsKeyDown(key) || _deckNameText.Length >= 32)
            {
                continue;
            }

            if (key is >= Keys.A and <= Keys.Z)
            {
                var character = (char)('a' + (key - Keys.A));
                _deckNameText += shift ? char.ToUpperInvariant(character) : character;
            }
            else if (key is >= Keys.D0 and <= Keys.D9)
            {
                _deckNameText += (char)('0' + (key - Keys.D0));
            }
        }

        _deckNameText = _deckNameText.TrimStart();
    }

    private void HandleJoinInviteTextInput()
    {
        if (!IsJoinInviteTextActive)
        {
            return;
        }

        if (Pressed(Keys.Back) && _joinInviteCode.Length > 0)
        {
            _joinInviteCode = _joinInviteCode[..^1];
            return;
        }

        if (Pressed(Keys.Delete))
        {
            _joinInviteCode = "";
            return;
        }

        if ((_keyboard.IsKeyDown(Keys.LeftControl) || _keyboard.IsKeyDown(Keys.RightControl)) && Pressed(Keys.V))
        {
            PasteJoinInviteCode();
            return;
        }

        foreach (var key in _keyboard.GetPressedKeys())
        {
            if (_previousKeyboard.IsKeyDown(key) || _joinInviteCode.Length >= 900)
            {
                continue;
            }

            if (key is >= Keys.A and <= Keys.Z)
            {
                _joinInviteCode += (char)('A' + (key - Keys.A));
            }
            else if (key is >= Keys.D0 and <= Keys.D9)
            {
                _joinInviteCode += (char)('0' + (key - Keys.D0));
            }
            else if (key == Keys.OemMinus || key == Keys.Subtract)
            {
                _joinInviteCode += "-";
            }
        }
    }

    private void CreateProfileFromSelection()
    {
        var starter = StarterDecks()[Math.Clamp(_creationStarterIndex, 0, StarterDecks().Count - 1)];
        var rules = GameRulesConfig.ForPreset(RulesPresetOrder[_creationPresetIndex], PlaystyleOrder[_creationPlaystyleIndex]).Normalize();
        var profile = ProgressionService.CreateProfile(_creationName.Trim(), rules, starter, _data);
        string? error = null;
        if (_profileRepository is null || !_profileRepository.TryCreateProfile(profile, DateTimeOffset.UtcNow, out var summary, out error) || summary is null)
        {
            _status = error ?? "Could not create the local profile.";
            _profileRepositoryNotice = _status;
            return;
        }

        _profile = profile;
        _activeProfileId = summary.Id;
        _deckBuilder = CreateDeckBuilderState(starter);
        _screen = Screen.MainMenu;
        _status = $"Welcome, {_profile.PlayerName}.";
    }

    private void PrepareCaptureProfile()
    {
        var starter = _data.DecksById["starter-fire"];
        _profile = ProgressionService.CreateProfile("Astra", GameRulesConfig.ForPreset(GameRulesPreset.Standard, Playstyle.Aggro), starter, _data);
        _profile.Experience = 4200;
        _profile.Level = ProgressionService.LevelForExperience(_profile.Experience);
        _profile.Coins = 3200;
        BoosterService.AddUnopenedPack(_profile, BoosterService.StandardBoosterId, 2);
        _profile.OwnedStarterDeckIds.Add("starter-ice");
        _profile.Normalize();
        _deckBuilder = CreateDeckBuilderState(starter);
    }

    private void BeginNewGame()
    {
        if (!CanSwitchProfiles())
        {
            _status = "Profiles cannot be switched during a match, pack opening, or active multiplayer lobby.";
            return;
        }

        _profilePickerOpenedFromMainMenu = _screen == Screen.MainMenu;
        _profileRenameActive = false;
        _profileDeleteConfirmation = false;
        _screen = Screen.ProfilePicker;
        _status = "Choose a local profile.";
    }

    private void BeginProfileCreation()
    {
        if (_profileRepository?.IsInitialized != true)
        {
            _status = "Profile storage needs recovery before a new profile can be created. Existing files were left unchanged.";
            return;
        }

        _creationName = "";
        _creationPresetIndex = 2;
        _creationPlaystyleIndex = 0;
        _creationStarterIndex = 0;
        _screen = Screen.PlayerCreation;
        _status = "Create a local profile.";
    }

    private void SaveProfile()
    {
        if (_profile is not null && _profileRepository is not null && !string.IsNullOrWhiteSpace(_activeProfileId))
        {
            if (!_profileRepository.TrySaveProfile(_activeProfileId, _profile, DateTimeOffset.UtcNow, out var error))
            {
                _status = error ?? "Could not save the local profile.";
                _profileRepositoryNotice = _status;
            }
        }
    }

    private bool CanSwitchProfiles() =>
        _screen is not Screen.Match and not Screen.PackOpening &&
        _directLobbyState is not DirectLobbyState.Hosting and not DirectLobbyState.Connected &&
        _networkConnection is null;

    private void SelectProfile(string profileId)
    {
        if (!CanSwitchProfiles())
        {
            _status = "Profiles cannot be switched during a match, pack opening, or active multiplayer lobby.";
            return;
        }

        string? error = null;
        if (_profileRepository is null || !_profileRepository.TryLoadProfile(profileId, out var profile, out error) || profile is null)
        {
            _status = error ?? "Could not load the local profile.";
            _profileRepositoryNotice = _status;
            return;
        }

        if (!_profileRepository.TrySelectProfile(profileId, DateTimeOffset.UtcNow, out error))
        {
            _status = error ?? "Could not select the local profile.";
            _profileRepositoryNotice = _status;
            return;
        }

        _profile = profile;
        _activeProfileId = profileId;
        _deckBuilder = CreateDeckBuilderState(ResolveProfileDeck(profileId, profile));
        _profileRenameActive = false;
        _profileDeleteConfirmation = false;
        _screen = Screen.MainMenu;
        _status = $"Welcome back, {_profile.PlayerName}.";
    }

    private DeckDefinition ResolveProfileDeck(string profileId, PlayerProfile profile)
    {
        if (_data.DecksById.TryGetValue(profile.ActiveDeckId, out var starterDeck))
        {
            return starterDeck;
        }

        if (_profileRepository is not null)
        {
            var decks = _profileRepository.LoadDecks(profileId, out var deckErrors);
            if (deckErrors.Count > 0)
            {
                _profileRepositoryNotice = deckErrors[0].Message;
            }

            var activeDeck = decks.FirstOrDefault(deck => deck.Id.Equals(profile.ActiveDeckId, StringComparison.OrdinalIgnoreCase));
            if (activeDeck is not null)
            {
                return activeDeck;
            }
        }

        var fallback = StarterDecks().FirstOrDefault(deck => deck.Id.Equals(profile.SelectedStarterDeckId, StringComparison.OrdinalIgnoreCase)) ?? StarterDecks().First();
        profile.ActiveDeckId = fallback.Id;
        SaveProfile();
        return fallback;
    }

    private void RenameSelectedProfile()
    {
        var profiles = _profileRepository?.Profiles ?? [];
        if (_profileRepository is null || _profilePickerFocus >= profiles.Count)
        {
            return;
        }

        if (_profileRepository.TryRenameProfile(profiles[_profilePickerFocus].Id, _profileRenameText, DateTimeOffset.UtcNow, out var error))
        {
            _profileRenameActive = false;
            _profileRepositoryNotice = "Profile renamed. LAN matches now use the new display name.";
            _status = _profileRepositoryNotice;
            if (_activeProfileId is not null && _activeProfileId.Equals(profiles[_profilePickerFocus].Id, StringComparison.OrdinalIgnoreCase) && _profile is not null)
            {
                _profile.PlayerName = _profileRenameText.Trim();
            }
        }
        else
        {
            _profileRepositoryNotice = error ?? "Could not rename the profile.";
            _status = _profileRepositoryNotice;
        }
    }

    private void DeleteSelectedProfile()
    {
        var profiles = _profileRepository?.Profiles ?? [];
        if (_profileRepository is null || _profilePickerFocus >= profiles.Count)
        {
            return;
        }

        var summary = profiles[_profilePickerFocus];
        if (_profileRepository.TryDeleteProfile(summary.Id, out var error))
        {
            if (_activeProfileId?.Equals(summary.Id, StringComparison.OrdinalIgnoreCase) == true)
            {
                _profile = null;
                _activeProfileId = null;
            }

            _profilePickerFocus = Math.Clamp(_profilePickerFocus, 0, _profileRepository.Profiles.Count);
            _profileDeleteConfirmation = false;
            _profileRepositoryNotice = $"{summary.DisplayName} was permanently deleted.";
            _status = _profileRepositoryNotice;
        }
        else
        {
            _profileDeleteConfirmation = false;
            _profileRepositoryNotice = error ?? "Could not delete the profile.";
            _status = _profileRepositoryNotice;
        }
    }

    private void BuyBooster()
    {
        var item = ShopCatalogService.CreateCatalog(_data)
            .FirstOrDefault(entry => entry.Kind == ShopItemKind.Booster && entry.PackId == BoosterService.StandardBoosterId);
        if (item is not null)
        {
            BuyCatalogBooster(item, 1);
        }
    }

    private void BuyCatalogBooster(ShopCatalogItem item, int quantity)
    {
        if (_profile is null)
        {
            _status = "Create a profile first.";
            return;
        }

        if (!CurrentRules().IsProgressionSafe)
        {
            _status = "Boosters are progression rewards and are disabled in sandbox.";
            return;
        }

        if (BoosterService.BuyBooster(_profile, item.PackId, quantity, item.Cost))
        {
            SaveProfile();
            _status = $"{item.Name} x{quantity} purchased.";
        }
        else
        {
            _status = "Not enough Coins.";
        }
    }

    private void OpenBooster()
    {
        var item = ShopCatalogService.CreateCatalog(_data)
            .FirstOrDefault(entry => entry.Kind == ShopItemKind.Booster && entry.PackId == BoosterService.StandardBoosterId);
        if (item is not null)
        {
            OpenCatalogBooster(item, 1);
        }
    }

    private void OpenCatalogBooster(ShopCatalogItem item, int quantity)
    {
        if (_profile is null || BoosterService.GetUnopenedPackCount(_profile, item.PackId) <= 0)
        {
            _status = "No unopened boosters.";
            return;
        }

        quantity = Math.Clamp(quantity, 1, BoosterService.GetUnopenedPackCount(_profile, item.PackId));
        _lastBoosterOpening = BoosterService.OpenBoosters(_data, _profile, item.PackId, quantity);
        _packOpeningScrollOffset = 0;
        SaveProfile();
        _screen = Screen.PackOpening;
        _audio.Play(PackOpeningSound(_lastBoosterOpening));
        _status = $"{item.Name} x{quantity} opened.";
    }

    private void BuySingleCard(CardDefinition card)
    {
        if (_profile is null)
        {
            _status = "Create a profile first.";
            return;
        }

        if (!CurrentRules().IsProgressionSafe)
        {
            _status = "Single-card purchases are disabled in sandbox.";
            return;
        }

        var result = ShopCatalogService.BuySingleCard(_profile, card);
        SaveProfile();
        _status = result.Message;
    }

    private void BuyOrSelectStarter(DeckDefinition deck)
    {
        if (_profile is null)
        {
            _status = "Create a profile first.";
            return;
        }

        var rules = CurrentRules();
        if (rules.AllUnlocks || rules.UnlimitedDeckBuilder || PlayerCollection.HasStarterDeck(_profile, deck.Id))
        {
            _deckBuilder = CreateDeckBuilderState(deck);
            _profile.ActiveDeckId = deck.Id;
            SaveProfile();
            _status = $"{deck.Name} selected.";
            return;
        }

        if (_profile.Coins < ProgressionService.StarterDeckCost)
        {
            _status = "Not enough Coins.";
            return;
        }

        _profile.Coins -= ProgressionService.StarterDeckCost;
        PlayerCollection.GrantDeck(_profile, deck, _data, addStarterOwnership: true);
        _deckBuilder = CreateDeckBuilderState(deck);
        _profile.ActiveDeckId = deck.Id;
        SaveProfile();
        _status = $"{deck.Name} purchased.";
    }

    private void ConfigureMatchStart(DeckDefinition firstDeck, DeckDefinition secondDeck, MatchKind matchKind)
    {
        if (_engine is null)
        {
            return;
        }

        _rematchFirstDeck = firstDeck;
        _rematchSecondDeck = secondDeck;
        _rematchKind = matchKind;
        _rematchModeId = _engine.State.Mode.Id;
        _matchRewardApplied = false;
        _pendingResultScreen = false;
        _lastMatchReward = null;
        _lastBattleSpoils = null;
        _lastQuestRewards = [];
        _networkSequence = 0;
        _matchTimelineEntries.Clear();
        if (matchKind == MatchKind.Online)
        {
            return;
        }

        _engine.State.Players[0].Name = _profile?.PlayerName ?? "Player";
        if (matchKind == MatchKind.VsAi)
        {
            _engine.State.Players[AiPlayerIndex].Name = $"AI: {StarterElement(secondDeck)}";
        }
    }

    private void QueueResultScreen()
    {
        _pendingResultScreen = true;
    }

    private bool TryOpenPendingResultScreen()
    {
        if (!_pendingResultScreen || _engine?.State.WinnerIndex is null || _screen != Screen.Match)
        {
            return false;
        }

        _pendingResultScreen = false;
        var winner = _engine.State.WinnerIndex.Value;
        var profilePlayerIndex = _matchKind == MatchKind.Online ? LocalPlayerIndexForMatch() : HumanPlayerIndex;
        _lastMatchWon = winner == profilePlayerIndex;
        if (_profile is not null && !_matchRewardApplied)
        {
            var rewardKind = _matchKind == MatchKind.VsAi ? MatchRewardKind.Ai : MatchRewardKind.HumanMultiplayer;
            var rewardRules = _engine.State.Mode.ProgressionEligible
                ? CurrentRules()
                : GameRulesConfig.ForPreset(GameRulesPreset.Casual);
            _lastMatchReward = ProgressionService.ApplyMatchReward(_profile, rewardRules, rewardKind, _lastMatchWon);
            var opponentDeck = profilePlayerIndex == 0 ? _rematchSecondDeck : _rematchFirstDeck;
            if (opponentDeck is not null)
            {
                _lastBattleSpoils = BattleSpoilsService.GrantVictorySpoils(_data, _profile, rewardRules, opponentDeck, _lastMatchWon);
            }

            if (_engine.State.Mode.ProgressionEligible && CurrentRules().IsProgressionSafe)
            {
                var questRewards = new List<QuestReward>();
                var matchUpdate = QuestService.Record(_profile, DateTimeOffset.UtcNow, QuestMetric.EligibleMatches, 1, eligible: true);
                questRewards.AddRange(matchUpdate.Rewards);
                if (_lastMatchWon)
                {
                    questRewards.AddRange(QuestService.Record(_profile, DateTimeOffset.UtcNow, QuestMetric.EligibleWins, 1, eligible: true).Rewards);
                }

                _lastQuestRewards = _lastQuestRewards.Concat(questRewards).ToArray();
            }

            _matchRewardApplied = true;
            SaveProfile();
        }

        _screen = Screen.MatchResult;
        _audio.Play(_lastMatchWon ? SoundKeys.Victory : SoundKeys.Defeat);
        ClearPresentation();
        ClearSelections();
        return true;
    }

    private static string PackOpeningSound(BoosterOpening opening)
    {
        if (opening.Cards.Any(card => CardRarities.Normalize(card.Rarity) == CardRarities.Mythic))
        {
            return SoundKeys.MythicPull;
        }

        return opening.Cards.Any(card => CardRarities.Normalize(card.Rarity) is CardRarities.Rare or CardRarities.Legendary)
            ? SoundKeys.RarePull
            : SoundKeys.PackOpen;
    }

    private int LocalPlayerIndexForMatch() => _matchKind == MatchKind.Online ? _networkLocalPlayerIndex : HumanPlayerIndex;

    private int LocalBoardPlayerIndex(MatchState state) =>
        _matchKind == MatchKind.Hotseat ? state.ActivePlayerIndex : LocalPlayerIndexForMatch();

    private void BeginHostDirectMatch() =>
        BeginHostDirectMatch(DragonCardsModeIds.DragonDuel, CurrentDeck());

    private void BeginHostDirectMatch(string modeId, DeckDefinition hostDeck)
    {
        if (IsDirectLobbyActive || _networkConnection is not null)
        {
            _status = "A direct lobby is already active.";
            return;
        }

        GenerateHostInvite(modeId, hostDeck);
        var handshake = DirectMatchConnection.CreateHandshake(
            _profile?.PlayerName ?? "Player",
            modeId,
            hostDeck,
            CurrentRules(),
            _hostInvite.LobbyToken);
        _networkCancellation?.Cancel();
        _networkCancellation?.Dispose();
        _networkCancellation = new CancellationTokenSource();
        _networkConnectTask = DirectMatchConnection.HostLobbyAsync(_hostInvite, handshake, _networkCancellation.Token);
        _networkStartTask = null;
        _networkReadTask = null;
        _multiplayerSection = MultiplayerSection.HostLobby;
        _directLobbyState = DirectLobbyState.Hosting;
        _screen = Screen.Multiplayer;
        _multiplayerNotice = $"Hosting {ModeName(modeId)}. Share the five-character LAN code with one guest.";
        _status = $"Lobby open: waiting for a guest in {ModeName(modeId)}.";
    }

    private void BeginJoinDirectMatch()
    {
        if (IsDirectLobbyActive || _networkConnection is not null)
        {
            _status = "A direct lobby is already active.";
            return;
        }

        if (InviteCode.TryDecodeLobbyCode(_joinInviteCode, out var lobbyToken, out var lobbyCodeError))
        {
            _networkCancellation?.Cancel();
            _networkCancellation?.Dispose();
            _networkCancellation = new CancellationTokenSource();
            _networkDiscoveryTask = LanLobbyDiscovery.ResolveAsync(lobbyToken, cancellationToken: _networkCancellation.Token);
            _networkConnectTask = null;
            _networkStartTask = null;
            _networkReadTask = null;
            _multiplayerSection = MultiplayerSection.JoinLobby;
            _directLobbyState = DirectLobbyState.Joining;
            _joinInviteEditing = false;
            _multiplayerNotice = $"Looking for LAN lobby {InviteCode.EncodeLobbyCode(lobbyToken)}.";
            _status = "Finding the host on the local network.";
            return;
        }

        var normalizedLobbyCode = new string(_joinInviteCode.Where(character => !char.IsWhiteSpace(character) && character != '-').ToArray());
        if (normalizedLobbyCode.Length == InviteCode.LobbyCodeLength)
        {
            _multiplayerNotice = lobbyCodeError;
            _status = "Lobby code is invalid.";
            return;
        }

        if (!InviteCode.TryDecode(_joinInviteCode, out var invite, out var error))
        {
            _multiplayerNotice = error;
            _status = "Direct invite is invalid.";
            return;
        }

        BeginJoinResolvedDirectMatch(invite);
    }

    private void BeginJoinResolvedDirectMatch(NetworkInvite invite)
    {
        if (!TryCreateLocalDeckForMode(invite.ModeId, out var joinDeck, out var deckError))
        {
            _multiplayerNotice = deckError;
            _status = "Could not build a compatible deck for this invite.";
            return;
        }

        var handshake = DirectMatchConnection.CreateHandshake(
            _profile?.PlayerName ?? "Player",
            invite.ModeId,
            joinDeck,
            CurrentRules(),
            invite.LobbyToken);
        _networkCancellation?.Cancel();
        _networkCancellation?.Dispose();
        _networkCancellation = new CancellationTokenSource();
        _networkDiscoveryTask = null;
        _networkConnectTask = DirectMatchConnection.JoinLobbyAsync(invite, handshake, _networkCancellation.Token);
        _networkStartTask = null;
        _networkReadTask = null;
        _multiplayerSection = MultiplayerSection.JoinLobby;
        _directLobbyState = DirectLobbyState.Joining;
        _joinInviteEditing = false;
        _multiplayerNotice = $"Connecting to the {ModeName(invite.ModeId)} lobby at {invite.Host}:{invite.Port}.";
        _status = "Connecting to host lobby.";
    }

    private void UpdateNetworkTasks()
    {
        if (_networkDiscoveryTask is { IsCompleted: true })
        {
            var task = _networkDiscoveryTask;
            _networkDiscoveryTask = null;
            if (task.IsFaulted || task.IsCanceled)
            {
                FailDirectLobby(task.Exception?.GetBaseException().Message ?? "The LAN lobby could not be found.");
            }
            else
            {
                _multiplayerNotice = $"Found {ModeName(task.Result.ModeId)} lobby at {task.Result.Host}:{task.Result.Port}. Connecting.";
                BeginJoinResolvedDirectMatch(task.Result);
            }
        }

        if (_networkConnectTask is { IsCompleted: true })
        {
            var task = _networkConnectTask;
            _networkConnectTask = null;
            if (task.IsFaulted)
            {
                FailDirectLobby(task.Exception?.GetBaseException().Message ?? "Direct match connection failed.");
            }
            else if (!task.IsCanceled)
            {
                _networkConnection = task.Result;
                _networkLocalPlayerIndex = _networkConnection.LocalPlayerIndex;
                _networkReadTask = null;
                _directLobbyState = DirectLobbyState.Connected;
                if (_networkConnection.IsHost)
                {
                    _multiplayerNotice = $"{_networkConnection.Lobby.Joiner.PlayerName} joined. Review the roster, then start when ready.";
                    _status = "Guest connected to your lobby.";
                }
                else
                {
                    _multiplayerNotice = $"Joined {_networkConnection.Lobby.Host.PlayerName}'s lobby. Waiting for the host to start.";
                    _status = "Lobby joined. Waiting for host start.";
                    _networkStartTask = _networkConnection.WaitForMatchStartAsync(_networkCancellation?.Token ?? CancellationToken.None);
                }
            }
        }

        if (_networkStartTask is { IsCompleted: true })
        {
            var task = _networkStartTask;
            _networkStartTask = null;
            if (task.IsFaulted || task.IsCanceled || _networkConnection is null)
            {
                FailDirectLobby(task.Exception?.GetBaseException().Message ?? "The lobby did not start.");
            }
            else
            {
                StartNetworkMatch(_networkConnection);
            }
        }

        if (_matchKind != MatchKind.Online || _networkConnection is null || _screen != Screen.Match)
        {
            return;
        }

        _networkReadTask ??= _networkConnection.ReadCommandAsync(_networkCancellation?.Token ?? CancellationToken.None);
        if (!_networkReadTask.IsCompleted)
        {
            return;
        }

        var readTask = _networkReadTask;
        _networkReadTask = null;
        if (readTask.IsCompletedSuccessfully)
        {
            ApplyRemoteCommand(readTask.Result);
        }
        else
        {
            _status = readTask.Exception?.GetBaseException().Message ?? "Remote player disconnected.";
        }
    }

    private void StartNetworkMatch(DirectMatchConnection connection)
    {
        ClearPresentation();
        _networkConnection = connection;
        _networkLocalPlayerIndex = connection.LocalPlayerIndex;
        _networkReadTask = null;
        _networkStartTask = null;
        _matchKind = MatchKind.Online;
        _engine = DragonDuelEngine.Create(
            _data,
            connection.MatchStart.ModeId,
            connection.MatchStart.Host.Deck,
            connection.MatchStart.Joiner.Deck,
            seed: connection.MatchStart.Seed);
        _engine.State.Players[0].Name = connection.MatchStart.Host.PlayerName;
        _engine.State.Players[1].Name = connection.MatchStart.Joiner.PlayerName;
        ConfigureMatchStart(connection.MatchStart.Host.Deck, connection.MatchStart.Joiner.Deck, MatchKind.Online);
        var flowResult = _engine.AdvanceToNextDecisionPhase();
        _screen = Screen.Match;
        ClearSelections();
        QueuePresentation(flowResult.Events);
        _directLobbyState = DirectLobbyState.Idle;
        _multiplayerNotice = connection.IsHost ? "Direct lobby started." : "Host started the direct lobby.";
        _status = connection.IsHost ? "Direct match started." : "Direct match joined.";
    }

    private void StartHostedLobbyMatch()
    {
        if (_networkConnection is null || !_networkConnection.IsHost || _directLobbyState != DirectLobbyState.Connected)
        {
            _status = "Wait for a guest before starting the lobby.";
            return;
        }

        _directLobbyState = DirectLobbyState.Starting;
        _networkStartTask = _networkConnection.StartMatchAsync(Environment.TickCount, _networkCancellation?.Token ?? CancellationToken.None);
        _multiplayerNotice = "Starting the match for both players.";
        _status = "Starting direct match.";
    }

    private void CancelDirectLobby(string notice = "Direct lobby canceled.")
    {
        _networkCancellation?.Cancel();
        _networkCancellation?.Dispose();
        _networkCancellation = null;
        _networkDiscoveryTask = null;
        _networkConnectTask = null;
        _networkStartTask = null;
        _networkReadTask = null;
        var connection = _networkConnection;
        _networkConnection = null;
        if (connection is not null)
        {
            _ = connection.DisposeAsync().AsTask();
        }

        _directLobbyState = DirectLobbyState.Idle;
        _joinInviteEditing = false;
        _multiplayerNotice = notice;
        _status = notice;
    }

    private void FailDirectLobby(string notice)
    {
        CancelDirectLobby(notice);
        _directLobbyState = DirectLobbyState.Failed;
    }

    private void CloseNetworkMatchConnection()
    {
        if (_matchKind != MatchKind.Online && _networkConnection is null)
        {
            return;
        }

        _networkCancellation?.Cancel();
        _networkCancellation?.Dispose();
        _networkCancellation = null;
        _networkDiscoveryTask = null;
        _networkConnectTask = null;
        _networkStartTask = null;
        _networkReadTask = null;
        var connection = _networkConnection;
        _networkConnection = null;
        if (connection is not null)
        {
            _ = connection.DisposeAsync().AsTask();
        }
    }

    private void SendOnlineCommand(string kind, string payload, GameActionResult result)
    {
        if (!result.Success || _matchKind != MatchKind.Online || _networkConnection is null || _applyingRemoteCommand)
        {
            return;
        }

        var command = new NetworkCommand
        {
            Kind = kind,
            PlayerIndex = LocalPlayerIndexForMatch(),
            Sequence = ++_networkSequence,
            PayloadJson = payload
        };
        _ = _networkConnection.SendCommandAsync(command);
    }

    private bool ExecuteCommand(string kind, string payload, Func<GameActionResult> action)
    {
        if (!TutorialAllowsCommand(kind, payload))
        {
            return false;
        }

        var result = action();
        ApplyCommandResult(kind, payload, result);
        return result.Success;
    }

    private void ApplyCommandResult(string kind, string payload, GameActionResult result)
    {
        ApplyHumanResult(result);
        if (result.Success)
        {
            RecordQuestProgress(kind, result);
            AdvanceTutorial(kind, payload);
        }

        SendOnlineCommand(kind, payload, result);
    }

    private void RecordQuestProgress(string kind, GameActionResult result)
    {
        if (_profile is null || _engine is null || !_engine.State.Mode.ProgressionEligible || !CurrentRules().IsProgressionSafe)
        {
            return;
        }

        var rewards = new List<QuestReward>();
        var changed = false;
        void Record(QuestMetric metric, int amount)
        {
            var update = QuestService.Record(_profile, DateTimeOffset.UtcNow, metric, amount, eligible: true);
            changed |= update.StateChanged;
            rewards.AddRange(update.Rewards);
        }

        if (kind == "play-card")
        {
            var played = result.Events.FirstOrDefault(entry => entry.Kind == MatchEventKind.CardPlayed);
            if (played is not null && _data.CardsById.TryGetValue(played.CardId, out var card) && !BasicEnergy.IsBasicEnergyCard(card))
            {
                Record(QuestMetric.NonEnergyCardsPlayed, 1);
            }
        }

        var createdSources = result.Events.Where(entry => entry.Kind == MatchEventKind.EnergySourceCreated).Sum(entry => entry.Amount);
        if (createdSources > 0)
        {
            Record(QuestMetric.EnergySourcesAdded, createdSources);
        }

        if (changed)
        {
            _lastQuestRewards = _lastQuestRewards.Concat(rewards).ToArray();
            SaveProfile();
        }
    }

    private void RecordQuestDamage(IReadOnlyList<MatchEvent> events)
    {
        if (_profile is null || _engine is null || !_engine.State.Mode.ProgressionEligible || !CurrentRules().IsProgressionSafe)
        {
            return;
        }

        var profilePlayerIndex = _matchKind == MatchKind.Online ? LocalPlayerIndexForMatch() : HumanPlayerIndex;
        var damage = events
            .Where(entry => entry.Kind == MatchEventKind.DamageTaken && 1 - entry.PlayerIndex == profilePlayerIndex)
            .Sum(entry => entry.Amount);
        if (damage <= 0)
        {
            return;
        }

        var update = QuestService.Record(_profile, DateTimeOffset.UtcNow, QuestMetric.DamageDealt, damage, eligible: true);
        if (update.StateChanged)
        {
            _lastQuestRewards = _lastQuestRewards.Concat(update.Rewards).ToArray();
            SaveProfile();
        }
    }

    private void ApplyRemoteCommand(NetworkCommand command)
    {
        if (_engine is null)
        {
            return;
        }

        _applyingRemoteCommand = true;
        try
        {
            var result = command.Kind switch
            {
                "advance" => AdvanceMatchFlow(),
                "add-energy" => _engine.AddEnergy(command.PayloadJson),
                "resolve-energy" => _engine.ResolveEnergyChoice(command.PayloadJson),
                "resolve-energy-source" => _engine.ResolveEnergySourceChoice(command.PayloadJson),
                "play-card" => _engine.PlayCardFromHand(ParseInt(command.PayloadJson)),
                "play-energy" => _engine.PlayEnergyFromHand(ParseInt(command.PayloadJson)),
                "sacrifice" => ApplyRemoteSacrifice(command.PayloadJson),
                "target" => ApplyRemoteTarget(command.PayloadJson),
                "attack" => _engine.DeclareAttack(ParseInt(command.PayloadJson)),
                "block" => _engine.Block(ParseInt(command.PayloadJson)),
                "pass-block" => _engine.PassBlock(),
                "combat-pass" => _engine.PassCombatAction(ParseInt(command.PayloadJson)),
                "ability" => ApplyRemoteAbility(command.PayloadJson),
                _ => GameActionResult.Fail($"Unknown network command '{command.Kind}'.")
            };
            ApplyResult(result);
        }
        finally
        {
            _applyingRemoteCommand = false;
        }
    }

    private GameActionResult ApplyRemoteSacrifice(string payload)
    {
        var parts = payload.Split('|');
        return parts.Length == 2 && Enum.TryParse<SacrificeSource>(parts[0], out var source)
            ? _engine!.SacrificeForEnergy(source, ParseInt(parts[1]))
            : GameActionResult.Fail("Invalid sacrifice command.");
    }

    private GameActionResult ApplyRemoteTarget(string payload)
    {
        var parts = payload.Split('|');
        return parts.Length switch
        {
            2 => _engine!.ResolveTargetChoice(ParseInt(parts[0]), ParseInt(parts[1])),
            3 => _engine!.ResolveTargetChoice(new ZoneRef(ParseInt(parts[0]), parts[1], ParseInt(parts[2]))),
            _ => GameActionResult.Fail("Invalid target command.")
        };
    }

    private GameActionResult ApplyRemoteAbility(string payload)
    {
        var parts = payload.Split('|');
        return parts.Length == 3
            ? _engine!.ActivateAbility(ParseInt(parts[0]), parts[1], parts[2])
            : GameActionResult.Fail("Invalid ability command.");
    }

    private static int ParseInt(string text) => int.TryParse(text, out var value) ? value : -1;
}
