using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DragonCards.Core;
using DragonCards.Networking;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace DragonCards.Desktop;

public sealed partial class DragonCardsGame
{
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
    private BattleSpoilsReward? _lastBattleSpoils;
    private bool _pendingResultScreen;
    private bool _matchRewardApplied;
    private bool _lastMatchWon;
    private DeckDefinition? _rematchFirstDeck;
    private DeckDefinition? _rematchSecondDeck;
    private MatchKind _rematchKind;
    private DirectMatchConnection? _networkConnection;
    private Task<DirectMatchConnection>? _networkConnectTask;
    private Task<NetworkCommand>? _networkReadTask;
    private CancellationTokenSource? _networkCancellation;
    private int _networkLocalPlayerIndex;
    private int _networkSequence;
    private bool _applyingRemoteCommand;

    private static string ProfileFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DragonCards", "profile.json");

    private void InitializeProgressionState()
    {
        _profile = File.Exists(ProfileFilePath) ? PlayerProfileSerializer.Load(ProfileFilePath) : null;
        if (_profile is null)
        {
            _screen = Screen.PlayerCreation;
            _deckBuilder = new DeckBuilderState(_data, StarterDecks().First());
            _status = "Create your player profile.";
            return;
        }

        var startingDeck = _data.DecksById.TryGetValue(_profile.ActiveDeckId, out var deck)
            ? deck
            : StarterDecks().FirstOrDefault(item => item.Id.Equals(_profile.SelectedStarterDeckId, StringComparison.OrdinalIgnoreCase)) ?? StarterDecks().First();
        _deckBuilder = new DeckBuilderState(_data, startingDeck);
        _status = $"Welcome back, {_profile.PlayerName}.";
    }

    private void HandleProgressionUpdate()
    {
        HandlePlayerCreationTextInput();
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
        if (deck.Count >= mode.DeckRules.DeckSize || _deckBuilder.CardCount(card.Id) >= mode.DeckRules.MaxCopies)
        {
            return false;
        }

        var rules = CurrentRules();
        return rules.UnlimitedDeckBuilder || rules.AllUnlocks || _profile is null || _deckBuilder.CardCount(card.Id) < PlayerCollection.CountOwned(_profile, card.Id);
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
            if (Button(rect, RulesPresetOrder[i].ToString(), selected: _creationPresetIndex == i))
            {
                _creationPresetIndex = i;
            }
        }

        DrawText("Playstyle", new Vector2(panel.X + 32, panel.Y + 268), Color.White, 0.82f);
        for (var i = 0; i < PlaystyleOrder.Length; i++)
        {
            var rect = new Rectangle(panel.X + 32 + i * 150, panel.Y + 308, 138, 42);
            if (Button(rect, PlaystyleOrder[i].ToString(), selected: _creationPlaystyleIndex == i))
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
            if (Button(rect, StarterElement(starters[i]), selected: _creationStarterIndex == i))
            {
                _creationStarterIndex = i;
            }
        }

        var rules = GameRulesConfig.ForPreset(RulesPresetOrder[_creationPresetIndex], PlaystyleOrder[_creationPlaystyleIndex]).Normalize();
        DrawText(rules.IsProgressionSafe ? "Progression mode: inventory, XP, coins, packs, and locked starters." : "Sandbox mode: all unlocks and unlimited deck building; progression rewards disabled.", new Rectangle(panel.X + 1000, panel.Y + 190, 390, 86), rules.IsProgressionSafe ? new Color(148, 224, 164) : new Color(255, 190, 120), 0.64f);

        if (Button(new Rectangle(panel.Right - 248, panel.Bottom - 72, 196, 46), "Create Player"))
        {
            CreateProfileFromSelection();
        }
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
        var panel = new Rectangle(54, 198, 1490, 520);
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
        DrawText(title, new Vector2(54, 108), _lastMatchWon ? new Color(148, 224, 164) : new Color(255, 162, 128), 1.3f);
        var panel = new Rectangle(54, 198, 1490, 520);
        DrawPanel(panel, new Color(31, 37, 46), border: new Color(81, 96, 116));
        DrawText($"Rules: {CurrentRules().Preset} / {CurrentRules().Playstyle}", new Vector2(panel.X + 36, panel.Y + 36), Color.White, 0.8f);

        if (_lastMatchReward is { ProgressionApplied: true } reward)
        {
            DrawText($"+{reward.ExperienceGained} XP", new Vector2(panel.X + 36, panel.Y + 96), new Color(148, 224, 164), 0.9f);
            DrawText($"+{reward.CoinsGained} Coins", new Vector2(panel.X + 36, panel.Y + 144), new Color(244, 230, 158), 0.9f);
            DrawText(reward.LevelsGained > 0 ? $"Level {reward.StartingLevel} -> {reward.EndingLevel}" : $"Level {_profile?.Level ?? 1}", new Vector2(panel.X + 36, panel.Y + 192), Color.White, 0.76f);
            DrawText(reward.BoostersGained > 0 ? $"+{reward.BoostersGained} booster reward" : "No level booster this match.", new Vector2(panel.X + 36, panel.Y + 238), new Color(205, 214, 225), 0.68f);
            DrawBattleSpoils(new Rectangle(panel.X + 36, panel.Y + 282, 990, 214));
        }
        else
        {
            DrawText("Progression rewards disabled for this rules configuration.", new Rectangle(panel.X + 36, panel.Y + 96, 650, 72), new Color(255, 190, 120), 0.76f);
        }

        if (_profile is not null)
        {
            DrawProfileSummary(new Rectangle(panel.Right - 430, panel.Y + 36, 360, 214));
        }

        var y = 786;
        if (Button(new Rectangle(54, y, 150, 42), "Continue"))
        {
            _screen = Screen.MainMenu;
            _status = "Returned to main menu.";
        }

        if (Button(new Rectangle(224, y, 150, 42), "Rematch", _rematchFirstDeck is not null && _rematchSecondDeck is not null))
        {
            StartMatch(_rematchFirstDeck!, _rematchSecondDeck!, _rematchKind);
        }

        if (Button(new Rectangle(394, y, 150, 42), "Deck Builder"))
        {
            _screen = Screen.DeckBuilder;
            _status = "Deck builder opened.";
        }

        if (Button(new Rectangle(564, y, 150, 42), "Store / Packs"))
        {
            _screen = Screen.Store;
            _status = "Store opened.";
        }
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
                if (Button(new Rectangle(panel.X + 28 + i * 88, panel.Y + 254, 76, 36), labels[i], selected: _storeQuantityIndex == i))
                {
                    _storeQuantityIndex = i;
                }
            }

            var buyQuantity = StoreBuyQuantity(item);
            var openQuantity = StoreOpenQuantity(item);
            if (Button(new Rectangle(panel.X + 28, panel.Y + 322, 180, 42), $"Buy x{buyQuantity}", _profile is not null && rules.IsProgressionSafe && buyQuantity > 0 && _profile.Coins >= item.Cost * buyQuantity))
            {
                BuyCatalogBooster(item, buyQuantity);
            }

            if (Button(new Rectangle(panel.X + 228, panel.Y + 322, 180, 42), $"Open x{openQuantity}", _profile is not null && openQuantity > 0))
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
            if (Button(new Rectangle(panel.X + 28, panel.Y + 272, 176, 42), owned || !rules.IsProgressionSafe ? "Open" : "Buy", _profile is not null && (owned || !rules.IsProgressionSafe || _profile.Coins >= item.Cost)))
            {
                BuyOrSelectStarter(deck);
            }

            return;
        }

        if (item.Kind == ShopItemKind.SingleCard && _data.CardsById.TryGetValue(item.CardId, out var card))
        {
            DrawCardFrame(new Rectangle(panel.X + 28, panel.Y + 154, 178, 250), card, selected: true, exhausted: false, count: _profile is null ? 0 : PlayerCollection.CountOwned(_profile, card.Id), compact: false);
            var detailRect = new Rectangle(panel.X + 232, panel.Y + 154, panel.Width - 266, 286);
            DrawScrollableText(CardDetailText(card), detailRect, ref _cardDetailScrollOffset, new Color(211, 220, 231), 0.48f);
            var owned = _profile is null ? 0 : PlayerCollection.CountOwned(_profile, card.Id);
            DrawText($"Owned {owned}/{PlayerCollection.MaxOwnedCopies}   {item.Cost} Coins", new Vector2(panel.X + 232, panel.Y + 462), owned >= PlayerCollection.MaxOwnedCopies ? new Color(255, 190, 120) : new Color(244, 230, 158), 0.58f);
            if (Button(new Rectangle(panel.X + 232, panel.Y + 502, 176, 38), "Buy Single", _profile is not null && rules.IsProgressionSafe && owned < PlayerCollection.MaxOwnedCopies && _profile.Coins >= item.Cost))
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
        var cardRect = new Rectangle(rect.X + 18, rect.Y + 42, 118, 166);
        DrawCardFrame(cardRect, card, selected: true, exhausted: false, count: grant.CopiesAdded, compact: false);
        var titleX = cardRect.Right + 22;
        DrawRarityBadge(new Rectangle(titleX, rect.Y + 46, 92, 22), grant.Rarity, compact: false);
        DrawFittedText(grant.CardName, new Vector2(titleX + 104, rect.Y + 48), rect.Right - titleX - 124, new Color(244, 230, 158), 0.62f, 0.34f);
        var note = grant.CopiesAdded > 0 ? $"+{grant.CopiesAdded} copy from opponent deck" : $"+{grant.DuplicateCoins} duplicate Coins";
        DrawText(note, new Rectangle(titleX, rect.Y + 78, rect.Right - titleX - 18, 28), grant.CopiesAdded > 0 ? new Color(148, 224, 164) : new Color(244, 230, 158), 0.5f);
        DrawScrollableText(CardDetailText(card), new Rectangle(titleX, rect.Y + 112, rect.Right - titleX - 18, 86), ref _cardDetailScrollOffset, new Color(211, 220, 231), 0.36f);
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
        if (DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out var horizontal))
        {
            _creationStarterIndex = Math.Clamp(_creationStarterIndex + horizontal, 0, StarterDecks().Count - 1);
        }

        if (Pressed(Buttons.A))
        {
            CreateProfileFromSelection();
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
        if (Pressed(Buttons.A))
        {
            _screen = Screen.MainMenu;
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

    private void HandleJoinInviteTextInput()
    {
        if (_screen != Screen.Multiplayer)
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
        _profile = ProgressionService.CreateProfile(string.IsNullOrWhiteSpace(_creationName) ? "Player" : _creationName, rules, starter, _data);
        SaveProfile();
        _deckBuilder = new DeckBuilderState(_data, starter);
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
        _deckBuilder = new DeckBuilderState(_data, starter);
    }

    private void BeginNewGame()
    {
        _creationName = _profile?.PlayerName ?? "Player";
        _creationPresetIndex = 2;
        _creationPlaystyleIndex = 0;
        _creationStarterIndex = 0;
        _profile = null;
        _screen = Screen.PlayerCreation;
        _status = "New game started.";
    }

    private void SaveProfile()
    {
        if (_profile is not null)
        {
            PlayerProfileSerializer.Save(ProfileFilePath, _profile);
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
            _deckBuilder = new DeckBuilderState(_data, deck);
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
        _deckBuilder = new DeckBuilderState(_data, deck);
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
        _matchRewardApplied = false;
        _pendingResultScreen = false;
        _lastMatchReward = null;
        _lastBattleSpoils = null;
        _networkSequence = 0;
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
            _lastMatchReward = ProgressionService.ApplyMatchReward(_profile, CurrentRules(), rewardKind, _lastMatchWon);
            var opponentDeck = profilePlayerIndex == 0 ? _rematchSecondDeck : _rematchFirstDeck;
            if (opponentDeck is not null)
            {
                _lastBattleSpoils = BattleSpoilsService.GrantVictorySpoils(_data, _profile, CurrentRules(), opponentDeck, _lastMatchWon);
            }

            _matchRewardApplied = true;
            SaveProfile();
        }

        _screen = Screen.MatchResult;
        _presentation.Clear();
        ClearSelections();
        return true;
    }

    private int LocalPlayerIndexForMatch() => _matchKind == MatchKind.Online ? _networkLocalPlayerIndex : HumanPlayerIndex;

    private int LocalBoardPlayerIndex(MatchState state) =>
        _matchKind == MatchKind.Hotseat ? state.ActivePlayerIndex : LocalPlayerIndexForMatch();

    private void BeginHostDirectMatch()
    {
        if (_networkConnectTask is not null && !_networkConnectTask.IsCompleted)
        {
            _status = "Already waiting for a direct match.";
            return;
        }

        GenerateHostInvite();
        var handshake = DirectMatchConnection.CreateHandshake(_profile?.PlayerName ?? "Player", "dragon-duel", CurrentDeck(), CurrentRules());
        _networkCancellation?.Cancel();
        _networkCancellation = new CancellationTokenSource();
        _networkConnectTask = DirectMatchConnection.HostAsync(_hostInvite, handshake, Environment.TickCount, _networkCancellation.Token);
        _multiplayerNotice = "Hosting direct match. Share the invite code with another player on your LAN.";
        _status = "Waiting for direct join.";
    }

    private void BeginJoinDirectMatch()
    {
        if (!InviteCode.TryDecode(_joinInviteCode, out var invite, out var error))
        {
            _multiplayerNotice = error;
            _status = "Direct invite is invalid.";
            return;
        }

        var handshake = DirectMatchConnection.CreateHandshake(_profile?.PlayerName ?? "Player", invite.ModeId, CurrentDeck(), CurrentRules());
        _networkCancellation?.Cancel();
        _networkCancellation = new CancellationTokenSource();
        _networkConnectTask = DirectMatchConnection.JoinAsync(invite, handshake, _networkCancellation.Token);
        _multiplayerNotice = "Joining direct match.";
        _status = "Connecting to host.";
    }

    private void UpdateNetworkTasks()
    {
        if (_networkConnectTask is { IsCompleted: true })
        {
            var task = _networkConnectTask;
            _networkConnectTask = null;
            if (task.IsFaulted)
            {
                _multiplayerNotice = task.Exception?.GetBaseException().Message ?? "Direct match connection failed.";
                _status = "Direct match connection failed.";
            }
            else if (!task.IsCanceled)
            {
                StartNetworkMatch(task.Result);
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
        _networkConnection = connection;
        _networkLocalPlayerIndex = connection.LocalPlayerIndex;
        _networkReadTask = null;
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
        _status = connection.IsHost ? "Direct match hosted." : "Direct match joined.";
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
            AdvanceTutorial(kind, payload);
        }

        SendOnlineCommand(kind, payload, result);
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
                "play-card" => _engine.PlayCardFromHand(ParseInt(command.PayloadJson)),
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
