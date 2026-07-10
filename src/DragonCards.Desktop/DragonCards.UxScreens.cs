using System;
using System.Collections.Generic;
using System.Linq;
using DragonCards.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace DragonCards.Desktop;

public sealed partial class DragonCardsGame
{
    private void SetStoreCategory(StoreCategory category)
    {
        if (_storeCategory == category)
        {
            return;
        }

        PersistActiveStorePosition();
        _storeCategory = category;
        _storeFocus = _storeCategoryFocus[(int)_storeCategory];
        _storeScrollOffset = ActiveStoreScroll.Offset;
        _storeFocusArea = StoreFocusArea.Catalog;
        ClampActiveStoreSelection(ensureVisible: false);
        _cardDetailScrollOffset = 0;
    }

    private void DrawStoreUx()
    {
        DrawUiText("Store", new Vector2(54, 102), Color.White, 1.0f);
        DrawUiText(_profile is null
                ? "Create a profile to use the store."
                : $"{_profile.Coins} Coins   {BoosterService.TotalUnopenedPacks(_profile)} unopened pack(s)",
            new Vector2(56, 142), UiTheme.TextMuted, 0.58f);

        var rules = CurrentRules();
        var tabs = new[]
        {
            (StoreCategory.Packs, "Packs"),
            (StoreCategory.StarterDecks, "Starter Decks"),
            (StoreCategory.Singles, "Singles")
        };
        for (var i = 0; i < tabs.Length; i++)
        {
            var tab = tabs[i];
            if (Button(new Rectangle(54 + i * 178, 178, 164, 40), tab.Item2, selected: _storeCategory == tab.Item1))
            {
                SetStoreCategory(tab.Item1);
            }
        }

        var listPanel = new Rectangle(54, 230, 720, 528);
        DrawPanel(listPanel, UiTheme.PanelRaised, border: UiTheme.Border);
        var searchRect = new Rectangle(listPanel.X + 24, listPanel.Y + 20, 442, 38);
        Fill(searchRect, _storeSearchActive ? UiTheme.PanelRaised : UiTheme.PanelInset);
        Border(searchRect, _storeSearchActive ? UiTheme.Focus : UiTheme.BorderStrong, _storeSearchActive ? 2 : 1);
        var searchText = string.IsNullOrWhiteSpace(_storeSearch) ? "Search this category..." : _storeSearch;
        DrawFittedText(searchText, new Vector2(searchRect.X + 12, searchRect.Y + 10), searchRect.Width - 24,
            string.IsNullOrWhiteSpace(_storeSearch) ? UiTheme.TextDisabled : Color.White, 0.58f, 0.42f);
        if (Hit(UiTheme.MinimumHitTarget(searchRect)))
        {
            _storeSearchActive = true;
        }

        if (Button(new Rectangle(searchRect.Right + 10, searchRect.Y, 84, 38), "Clear", !string.IsNullOrWhiteSpace(_storeSearch)))
        {
            _storeSearch = "";
            ResetActiveStorePosition();
        }

        var contentTop = listPanel.Y + 72;
        if (_storeCategory == StoreCategory.Singles)
        {
            var setFilters = StoreSetFilters();
            if (Button(new Rectangle(listPanel.X + 24, listPanel.Y + 72, 188, 34), $"Element: {StoreElementFilters[_storeElementFilterIndex]}",
                focused: _usingController && _storeFocusArea == StoreFocusArea.Filters && _storeFilterFocus == 0))
            {
                CycleStoreFilter(ref _storeElementFilterIndex, StoreElementFilters.Length);
            }

            if (Button(new Rectangle(listPanel.X + 222, listPanel.Y + 72, 188, 34), $"Rarity: {StoreRarityFilters[_storeRarityFilterIndex]}",
                focused: _usingController && _storeFocusArea == StoreFocusArea.Filters && _storeFilterFocus == 1))
            {
                CycleStoreFilter(ref _storeRarityFilterIndex, StoreRarityFilters.Length);
            }

            if (Button(new Rectangle(listPanel.X + 420, listPanel.Y + 72, 188, 34), $"Set: {BoosterService.SetDisplayName(setFilters[_storeSetFilterIndex])}",
                focused: _usingController && _storeFocusArea == StoreFocusArea.Filters && _storeFilterFocus == 2))
            {
                CycleStoreFilter(ref _storeSetFilterIndex, setFilters.Count);
            }

            if (Button(new Rectangle(listPanel.Right - 78, listPanel.Y + 72, 54, 34), "Reset",
                focused: _usingController && _storeFocusArea == StoreFocusArea.Filters && _storeFilterFocus == 3))
            {
                ResetStoreFilters();
            }

            contentTop = listPanel.Y + 118;
        }

        var items = FilteredStoreCatalog();
        var rowHeight = 62;
        var visibleRows = _storeCategory == StoreCategory.Singles ? 5 : 6;
        ActiveStoreScroll.Configure(items.Count, visibleRows);
        _storeFocus = Math.Clamp(_storeFocus, 0, Math.Max(0, items.Count - 1));
        _storeCategoryFocus[(int)_storeCategory] = _storeFocus;
        _storeScrollOffset = ActiveStoreScroll.Offset;

        DrawUiText($"{items.Count} result{(items.Count == 1 ? "" : "s")}", new Vector2(listPanel.Right - 154, listPanel.Y + 28), UiTheme.TextMuted, 0.42f);
        if (items.Count == 0)
        {
            DrawText("No catalog items match the current search and filters.",
                new Rectangle(listPanel.X + 28, contentTop + 48, listPanel.Width - 56, 60), UiTheme.TextMuted, 0.62f);
            if (Button(new Rectangle(listPanel.X + 28, contentTop + 126, 180, 40), "Clear Filters"))
            {
                ResetStoreFilters();
            }
        }
        else
        {
            var start = ActiveStoreScroll.Offset;
            var end = Math.Min(items.Count, start + visibleRows);
            for (var index = start; index < end; index++)
            {
                var item = items[index];
                var row = index - start;
                var rect = new Rectangle(listPanel.X + 24, contentTop + row * rowHeight, listPanel.Width - 60, 52);
                var selected = index == _storeFocus;
                var focused = selected && _usingController && _storeFocusArea == StoreFocusArea.Catalog;
                DrawPanel(rect, selected ? UiTheme.Selection : UiTheme.Panel, border: focused ? UiTheme.Focus : selected ? UiTheme.DragonGold : UiTheme.Border);
                DrawFittedText(item.Name, new Vector2(rect.X + 14, rect.Y + 7), rect.Width - 28, Color.White, 0.58f, 0.4f);
                DrawFittedText($"{StoreKindLabel(item)}   {StoreItemStatus(item, rules)}", new Vector2(rect.X + 14, rect.Y + 29), rect.Width - 28, UiTheme.TextMuted, 0.42f, 0.3f);
                if (Hit(rect))
                {
                    _storeFocus = index;
                    _storeCategoryFocus[(int)_storeCategory] = index;
                    _cardDetailScrollOffset = 0;
                }
            }
        }

        DrawUxScrollBar("store-catalog", new Rectangle(listPanel.Right - 16, contentTop, 8, visibleRows * rowHeight - 10), ActiveStoreScroll, UiTheme.Focus);

        var selectedItem = items.Count == 0 ? null : items[_storeFocus];
        var detailPanel = new Rectangle(798, 198, 746, 560);
        DrawStoreDetail(detailPanel, selectedItem, rules);
        if (_usingController && _storeFocusArea == StoreFocusArea.Detail)
        {
            Border(detailPanel, UiTheme.Focus, 3);
            DrawText("Detail actions focused - Up/Down chooses, Left returns to catalog.",
                new Rectangle(detailPanel.X + 28, detailPanel.Bottom - 30, detailPanel.Width - 56, 22), UiTheme.Focus, 0.44f);
        }

        DrawText("Mouse: wheel/drag scrollbar   Keyboard: arrows, Tab, Enter, Ctrl+F   Controller: D-pad, A, shoulders, Y filters, X clear",
            new Rectangle(230, 794, 1040, 30), UiTheme.TextMuted, 0.48f);
        if (Button(new Rectangle(54, 786, 150, 42), "Back"))
        {
            _screen = UxBackDestination(Screen.MainMenu);
            _status = "Returned.";
        }
    }

    private int StoreDetailActionCount(ShopCatalogItem item) => item.Kind == ShopItemKind.Booster ? 3 : 1;

    private void HandleStoreUxInput()
    {
        var items = FilteredStoreCatalog();
        ClampActiveStoreSelection(ensureVisible: false);
        if (FocusPressed(out var focusDelta))
        {
            _storeFocusArea = _storeCategory == StoreCategory.Singles
                ? (StoreFocusArea)(((int)_storeFocusArea + focusDelta + 3) % 3)
                : _storeFocusArea == StoreFocusArea.Catalog ? StoreFocusArea.Detail : StoreFocusArea.Catalog;
            _usingController = true;
        }

        var detailCanScroll = _storeFocusArea == StoreFocusArea.Detail &&
            items.Count > 0 &&
            items[Math.Clamp(_storeFocus, 0, items.Count - 1)].Kind == ShopItemKind.SingleCard;
        var pageDelta = _uiActions.TriggerCount(UiAction.PageNext) - _uiActions.TriggerCount(UiAction.PagePrevious);
        if (detailCanScroll && pageDelta != 0)
        {
            _usingController = true;
            _cardDetailScrollOffset = Math.Max(0, _cardDetailScrollOffset + pageDelta * 8);
        }
        else if (_storeFocusArea == StoreFocusArea.Catalog && items.Count > 0 && pageDelta != 0)
        {
            _usingController = true;
            ActiveStoreScroll.PageBy(pageDelta);
            _storeFocus = Math.Clamp(ActiveStoreScroll.Offset, 0, items.Count - 1);
            _storeCategoryFocus[(int)_storeCategory] = _storeFocus;
        }

        if (detailCanScroll && _uiActions.Triggered(UiAction.MoveToStart))
        {
            _cardDetailScrollOffset = 0;
        }
        else if (detailCanScroll && _uiActions.Triggered(UiAction.MoveToEnd))
        {
            _cardDetailScrollOffset = int.MaxValue;
        }

        if (Pressed(Buttons.X))
        {
            _usingController = true;
            ResetStoreFilters();
        }

        if (_storeCategory == StoreCategory.Singles && Pressed(Buttons.Y))
        {
            _usingController = true;
            _storeFocusArea = _storeFocusArea == StoreFocusArea.Filters
                ? StoreFocusArea.Catalog
                : StoreFocusArea.Filters;
        }

        if (_storeFocusArea == StoreFocusArea.Filters)
        {
            if (DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out var filterHorizontal))
            {
                _storeFilterFocus = Math.Clamp(_storeFilterFocus + filterHorizontal, 0, 3);
            }

            if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var filterVertical) || Pressed(Buttons.A))
            {
                var delta = filterVertical == 0 ? 1 : filterVertical;
                if (_storeFilterFocus == 3)
                {
                    ResetStoreFilters();
                }
                else if (_storeFilterFocus == 0)
                {
                    CycleStoreFilter(ref _storeElementFilterIndex, StoreElementFilters.Length, delta);
                }
                else if (_storeFilterFocus == 1)
                {
                    CycleStoreFilter(ref _storeRarityFilterIndex, StoreRarityFilters.Length, delta);
                }
                else
                {
                    CycleStoreFilter(ref _storeSetFilterIndex, StoreSetFilters().Count, delta);
                }
            }
            return;
        }

        if (items.Count == 0)
        {
            return;
        }

        if (_storeFocusArea == StoreFocusArea.Catalog)
        {
            if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical))
            {
                _storeFocus = Math.Clamp(_storeFocus + vertical, 0, items.Count - 1);
                ActiveStoreScroll.EnsureVisible(_storeFocus);
                _storeCategoryFocus[(int)_storeCategory] = _storeFocus;
                _cardDetailScrollOffset = 0;
            }

            if (_uiActions.Triggered(UiAction.MoveToStart))
            {
                ActiveStoreScroll.MoveToStart();
                _storeFocus = 0;
            }
            else if (_uiActions.Triggered(UiAction.MoveToEnd))
            {
                ActiveStoreScroll.MoveToEnd();
                _storeFocus = items.Count - 1;
            }
            _storeCategoryFocus[(int)_storeCategory] = _storeFocus;

            if (DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out var horizontal))
            {
                SwitchStoreCategory(horizontal);
            }

            if (Pressed(Buttons.A))
            {
                _usingController = true;
                _storeFocusArea = StoreFocusArea.Detail;
                _storeDetailFocus = 0;
            }
            return;
        }

        var item = items[_storeFocus];
        var actionCount = StoreDetailActionCount(item);
        if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var detailVertical))
        {
            _storeDetailFocus = Math.Clamp(_storeDetailFocus + detailVertical, 0, actionCount - 1);
        }

        if (DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out var detailHorizontal))
        {
            if (item.Kind == ShopItemKind.Booster && _storeDetailFocus == 0)
            {
                _storeQuantityIndex = Math.Clamp(_storeQuantityIndex + detailHorizontal, 0, 3);
            }
            else if (detailHorizontal < 0)
            {
                _storeFocusArea = StoreFocusArea.Catalog;
            }
        }

        if (!Pressed(Buttons.A))
        {
            return;
        }

        _usingController = true;
        if (item.Kind == ShopItemKind.Booster)
        {
            if (_storeDetailFocus == 0)
            {
                _storeQuantityIndex = (_storeQuantityIndex + 1) % 4;
            }
            else if (_storeDetailFocus == 1)
            {
                BuyCatalogBooster(item, StoreBuyQuantity(item));
            }
            else
            {
                OpenCatalogBooster(item, StoreOpenQuantity(item));
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

    private void DrawDeckBuilderUx()
    {
        DrawUiText("Deck Builder", new Vector2(42, 98), Color.White, 0.96f);
        DrawUiText($"{_deckBuilder.DeckName}  -  {DeckBuilderModeLabel()}", new Vector2(42, 136), UiTheme.TextMuted, 0.58f);
        DrawFilterBar();

        var cards = _deckBuilder.FilteredCards;
        const int columns = 6;
        const int visibleRows = 2;
        var rowCount = Math.Max(1, (int)Math.Ceiling(cards.Count / (double)columns));
        _deckGridScroll.Configure(rowCount, visibleRows);
        _deckFocusIndex = Math.Clamp(_deckFocusIndex, 0, Math.Max(0, cards.Count - 1));
        if (cards.Count > 0 && _usingController && _deckFocusArea == DeckFocusArea.Grid)
        {
            _deckBuilder.SelectedCardId = cards[_deckFocusIndex].Id;
        }

        var firstCard = Math.Min(cards.Count, _deckGridScroll.Offset * columns);
        var lastCard = Math.Min(cards.Count, firstCard + columns * visibleRows);
        DrawUiText(cards.Count == 0 ? "No cards match these filters." : $"Cards {firstCard + 1}-{lastCard} of {cards.Count}",
            new Vector2(42, 206), UiTheme.TextMuted, 0.48f);

        var libraryArea = new Rectangle(42, 246, 920, 548);
        DrawPanel(libraryArea, UiTheme.PanelRaised, border: UiTheme.Border);
        for (var index = firstCard; index < lastCard; index++)
        {
            var local = index - firstCard;
            var column = local % columns;
            var row = local / columns;
            var rect = new Rectangle(libraryArea.X + 22 + column * 146, libraryArea.Y + 20 + row * 250, 122, 178);
            var card = cards[index];
            var selected = _deckBuilder.SelectedCard?.Id == card.Id ||
                _usingController && _deckFocusArea == DeckFocusArea.Grid && _deckFocusIndex == index;
            if (CardButton(rect, card, selected, _deckBuilder.CardCount(card.Id), compact: false))
            {
                _deckBuilder.SelectedCardId = card.Id;
                _deckFocusIndex = index;
                _cardDetailScrollOffset = 0;
            }
            DrawOwnedCardCount(card, rect);
        }

        DrawUxScrollBar("deck-grid", new Rectangle(libraryArea.Right - 14, libraryArea.Y + 18, 8, libraryArea.Height - 36), _deckGridScroll, UiTheme.Focus);
        DrawDeckSidebar(new Rectangle(1000, 96, 544, 746));
    }

    private void HandleDeckBuilderUxInput()
    {
        if (Pressed(Keys.Tab))
        {
            CycleDeckFocusArea(IsDown(Keys.LeftShift) || IsDown(Keys.RightShift) ? -1 : 1);
            _usingController = true;
        }

        var cards = _deckBuilder.FilteredCards;
        const int columns = 6;
        const int visibleRows = 2;
        var rows = Math.Max(1, (int)Math.Ceiling(cards.Count / (double)columns));
        _deckGridScroll.Configure(rows, visibleRows);
        if (cards.Count == 0 && _deckFocusArea == DeckFocusArea.Grid)
        {
            _deckFocusIndex = 0;
            if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var emptyVertical))
            {
                _deckFocusArea = emptyVertical < 0 ? DeckFocusArea.ElementFilters : DeckFocusArea.TypeFilters;
                SyncDeckControlFocus();
            }
            return;
        }

        if (_deckFocusArea != DeckFocusArea.Grid)
        {
            HandleDeckControlInput(cards);
            return;
        }

        var focusMoved = false;
        if (DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out var horizontal))
        {
            if (horizontal > 0 && _deckFocusIndex % columns == columns - 1)
            {
                _deckFocusArea = DeckFocusArea.CardActions;
                _deckControlFocus = 0;
            }
            else
            {
                var next = Math.Clamp(_deckFocusIndex + horizontal, 0, cards.Count - 1);
                focusMoved |= next != _deckFocusIndex;
                _deckFocusIndex = next;
            }
        }

        if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical))
        {
            var currentRow = _deckFocusIndex / columns;
            var lastRow = Math.Max(0, (cards.Count - 1) / columns);
            if (vertical < 0 && currentRow == 0)
            {
                _deckFocusArea = DeckFocusArea.ElementFilters;
                SyncDeckControlFocus();
            }
            else if (vertical > 0 && currentRow == lastRow)
            {
                _deckFocusArea = DeckFocusArea.TypeFilters;
                SyncDeckControlFocus();
            }
            else
            {
                var next = Math.Clamp(_deckFocusIndex + vertical * columns, 0, cards.Count - 1);
                focusMoved |= next != _deckFocusIndex;
                _deckFocusIndex = next;
            }
        }

        if (Pressed(Buttons.LeftShoulder) || Pressed(Keys.PageUp))
        {
            _usingController = true;
            var next = Math.Max(0, _deckFocusIndex - columns * visibleRows);
            focusMoved |= next != _deckFocusIndex;
            _deckFocusIndex = next;
        }

        if (Pressed(Buttons.RightShoulder) || Pressed(Keys.PageDown))
        {
            _usingController = true;
            var next = Math.Min(cards.Count - 1, _deckFocusIndex + columns * visibleRows);
            focusMoved |= next != _deckFocusIndex;
            _deckFocusIndex = next;
        }

        if (Pressed(Keys.Home))
        {
            focusMoved |= _deckFocusIndex != 0;
            _deckFocusIndex = 0;
        }
        else if (Pressed(Keys.End))
        {
            focusMoved |= _deckFocusIndex != cards.Count - 1;
            _deckFocusIndex = cards.Count - 1;
        }

        if (focusMoved || _deckFocusVisibilityPending)
        {
            _deckGridScroll.EnsureVisible(_deckFocusIndex / columns);
            _deckFocusVisibilityPending = false;
        }
        var selected = cards[_deckFocusIndex];
        _deckBuilder.SelectedCardId = selected.Id;
        if (Pressed(Buttons.X))
        {
            _usingController = true;
            var deck = _deckBuilder.CreateDeck();
            if (CanAddCardToDeck(selected, deck))
            {
                _deckBuilder.Add(selected.Id);
                _status = $"Added {selected.Name}.";
            }
        }

        if (Pressed(Buttons.Y) && _deckBuilder.CardCount(selected.Id) > 0)
        {
            _usingController = true;
            _deckBuilder.Remove(selected.Id);
            _status = $"Removed {selected.Name}.";
        }

        if (Pressed(Buttons.A))
        {
            _usingController = true;
            _deckFocusArea = DeckFocusArea.CardActions;
            _deckControlFocus = 0;
        }
    }

    private void HandleDeckControlInput(IReadOnlyList<CardDefinition> cards)
    {
        var horizontalPressed = DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out var horizontal);
        var verticalPressed = DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical);
        var confirm = Pressed(Buttons.A);

        switch (_deckFocusArea)
        {
            case DeckFocusArea.ElementFilters:
                if (horizontalPressed)
                {
                    _deckControlFocus = Math.Clamp(_deckControlFocus + horizontal, 0, _deckBuilder.ElementFilters.Count - 1);
                }
                if (verticalPressed && vertical > 0)
                {
                    _deckFocusArea = DeckFocusArea.Grid;
                }
                if (confirm)
                {
                    ApplyDeckElementFilter(_deckBuilder.ElementFilters[_deckControlFocus]);
                }
                break;

            case DeckFocusArea.TypeFilters:
                if (horizontalPressed)
                {
                    _deckControlFocus = Math.Clamp(_deckControlFocus + horizontal, 0, _deckBuilder.TypeFilters.Count - 1);
                }
                if (verticalPressed)
                {
                    _deckFocusArea = vertical < 0 ? DeckFocusArea.Grid : DeckFocusArea.Footer;
                    SyncDeckControlFocus();
                }
                if (confirm)
                {
                    ApplyDeckTypeFilter(_deckBuilder.TypeFilters[_deckControlFocus]);
                }
                break;

            case DeckFocusArea.CardDetail:
                if (verticalPressed)
                {
                    _cardDetailScrollOffset = Math.Max(0, _cardDetailScrollOffset + vertical);
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
                if (horizontalPressed)
                {
                    _deckFocusArea = horizontal < 0 ? DeckFocusArea.Grid : DeckFocusArea.CardActions;
                    SyncDeckControlFocus();
                }
                break;

            case DeckFocusArea.CardActions:
                if (horizontalPressed)
                {
                    if (horizontal < 0 && _deckControlFocus == 0)
                    {
                        _deckFocusArea = DeckFocusArea.Grid;
                    }
                    else
                    {
                        _deckControlFocus = Math.Clamp(_deckControlFocus + horizontal, 0, 1);
                    }
                }
                if (verticalPressed)
                {
                    _deckFocusArea = vertical < 0 ? DeckFocusArea.CardDetail : DeckFocusArea.AssistantGoals;
                    SyncDeckControlFocus();
                }
                if (confirm)
                {
                    ActivateDeckCardAction(cards, _deckControlFocus);
                }
                break;

            case DeckFocusArea.AssistantGoals:
                var goals = Enum.GetValues<DeckAssistantGoal>();
                if (horizontalPressed)
                {
                    _deckControlFocus = Math.Clamp(_deckControlFocus + horizontal, 0, goals.Length - 1);
                }
                if (verticalPressed)
                {
                    _deckFocusArea = vertical < 0 ? DeckFocusArea.CardActions : DeckFocusArea.AssistantActions;
                    SyncDeckControlFocus();
                }
                if (confirm)
                {
                    _deckAssistantGoal = goals[_deckControlFocus];
                    _deckAssistantSuggestions = [];
                    _status = $"Assistant goal set to {_deckAssistantGoal}.";
                }
                break;

            case DeckFocusArea.AssistantActions:
                if (horizontalPressed)
                {
                    _deckControlFocus = Math.Clamp(_deckControlFocus + horizontal, 0, 4);
                }
                if (verticalPressed)
                {
                    _deckFocusArea = vertical < 0
                        ? DeckFocusArea.AssistantGoals
                        : _deckAssistantSuggestions.Count > 0 ? DeckFocusArea.Suggestions : DeckFocusArea.Footer;
                    SyncDeckControlFocus();
                }
                if (confirm)
                {
                    ActivateDeckAssistantAction(_deckControlFocus);
                }
                break;

            case DeckFocusArea.Suggestions:
                var suggestionCount = Math.Min(3, _deckAssistantSuggestions.Count);
                if (suggestionCount == 0)
                {
                    _deckFocusArea = DeckFocusArea.Footer;
                    SyncDeckControlFocus();
                    break;
                }
                if (horizontalPressed)
                {
                    _deckControlFocus = Math.Clamp(_deckControlFocus + horizontal, 0, suggestionCount - 1);
                }
                if (verticalPressed)
                {
                    _deckFocusArea = vertical < 0 ? DeckFocusArea.AssistantActions : DeckFocusArea.Footer;
                    SyncDeckControlFocus();
                }
                if (confirm)
                {
                    var suggestion = _deckAssistantSuggestions[_deckControlFocus];
                    _deckBuilder.SelectedCardId = suggestion.CardId;
                    _cardDetailScrollOffset = 0;
                    _status = suggestion.Reason;
                }
                break;

            case DeckFocusArea.Footer:
                if (horizontalPressed)
                {
                    _deckControlFocus = Math.Clamp(_deckControlFocus + horizontal, 0, 1);
                }
                if (verticalPressed && vertical < 0)
                {
                    _deckFocusArea = _deckAssistantSuggestions.Count > 0 ? DeckFocusArea.Suggestions : DeckFocusArea.TypeFilters;
                    SyncDeckControlFocus();
                }
                if (confirm)
                {
                    if (_deckControlFocus == 0)
                    {
                        SaveDeck(_deckBuilder.CreateDeck());
                    }
                    else
                    {
                        _screen = UxBackDestination(Screen.MainMenu);
                        _status = "Returned.";
                    }
                }
                break;
        }
    }

    private void CycleDeckFocusArea(int delta)
    {
        var count = Enum.GetValues<DeckFocusArea>().Length;
        _deckFocusArea = (DeckFocusArea)(((int)_deckFocusArea + delta + count) % count);
        if (_deckFocusArea == DeckFocusArea.Suggestions && _deckAssistantSuggestions.Count == 0)
        {
            _deckFocusArea = delta >= 0 ? DeckFocusArea.Footer : DeckFocusArea.AssistantActions;
        }
        SyncDeckControlFocus();
    }

    private void SyncDeckControlFocus()
    {
        _deckControlFocus = _deckFocusArea switch
        {
            DeckFocusArea.ElementFilters => Math.Max(0, _deckBuilder.ElementFilters.ToList().FindIndex(item => item.Equals(_deckBuilder.ElementFilter, StringComparison.OrdinalIgnoreCase))),
            DeckFocusArea.TypeFilters => Math.Max(0, _deckBuilder.TypeFilters.ToList().FindIndex(item => item.Equals(_deckBuilder.TypeFilter, StringComparison.OrdinalIgnoreCase))),
            DeckFocusArea.AssistantGoals => (int)_deckAssistantGoal,
            _ => 0
        };
    }

    private void ApplyDeckElementFilter(string element)
    {
        _deckBuilder.ElementFilter = element;
        ResetDeckFilterPosition();
    }

    private void ApplyDeckTypeFilter(string type)
    {
        _deckBuilder.TypeFilter = type;
        ResetDeckFilterPosition();
    }

    private void ResetDeckFilterPosition()
    {
        _deckBuilder.Page = 0;
        _deckFocusIndex = 0;
        _deckGridScroll.MoveToStart();
        _deckFocusVisibilityPending = true;
        _cardDetailScrollOffset = 0;
        if (_deckBuilder.FilteredCards.Count > 0)
        {
            _deckBuilder.SelectedCardId = _deckBuilder.FilteredCards[0].Id;
        }
    }

    private void ActivateDeckCardAction(IReadOnlyList<CardDefinition> cards, int action)
    {
        if (cards.Count == 0)
        {
            return;
        }
        var card = cards[Math.Clamp(_deckFocusIndex, 0, cards.Count - 1)];
        if (action == 0)
        {
            if (CanAddCardToDeck(card, _deckBuilder.CreateDeck()))
            {
                _deckBuilder.Add(card.Id);
                _status = $"Added {card.Name}.";
            }
        }
        else if (_deckBuilder.CardCount(card.Id) > 0)
        {
            _deckBuilder.Remove(card.Id);
            _status = $"Removed {card.Name}.";
        }
    }

    private void ActivateDeckAssistantAction(int action)
    {
        var deck = _deckBuilder.CreateDeck();
        var rules = CurrentRules();
        switch (action)
        {
            case 0:
                _deckAssistantSuggestions = DeckBuilderAssistantService.SuggestAdds(_data, deck, _profile, rules, _deckAssistantGoal, 4);
                _status = _deckAssistantSuggestions.Count == 0 ? "No legal add suggestions found." : "Assistant add suggestions ready.";
                break;
            case 1:
                _deckAssistantSuggestions = DeckBuilderAssistantService.SuggestCuts(_data, deck, _profile, rules, _deckAssistantGoal, 4);
                _status = _deckAssistantSuggestions.Count == 0 ? "No cut suggestions found." : "Assistant cut suggestions ready.";
                break;
            case 2:
                ApplyAssistantDeck(DeckBuilderAssistantService.AutoFill(_data, deck, _profile, rules, _deckAssistantGoal), "Assistant auto-filled the deck.");
                break;
            case 3:
                ApplyAssistantDeck(DeckBuilderAssistantService.AutoFill(_data, deck, _profile, rules, _deckAssistantGoal), "Assistant completed the deck as far as possible.");
                break;
            case 4:
                _deckAssistantSuggestions = [];
                _status = "Assistant suggestions cleared.";
                break;
        }
    }

    private void DrawPackOpeningUx()
    {
        DrawUiText("Pack Opening", new Vector2(54, 104), Color.White, 1.0f);
        DrawUiText(_lastBoosterOpening is null
                ? "No pack opened."
                : $"{_lastBoosterOpening.PackName} x{_lastBoosterOpening.PackCount}   Duplicate coins: {_lastBoosterOpening.CoinsFromDuplicates}",
            new Vector2(56, 144), UiTheme.TextMuted, 0.58f);
        var panel = new Rectangle(54, 198, 1490, 560);
        DrawPanel(panel, UiTheme.PanelRaised, border: UiTheme.Border);

        if (_lastBoosterOpening is not null)
        {
            const int columns = 8;
            const int visibleRows = 2;
            const int rowHeight = 238;
            var cards = _lastBoosterOpening.Cards;
            var rows = Math.Max(1, (int)Math.Ceiling(cards.Count / (double)columns));
            _packGridScroll.Configure(rows, visibleRows);
            _packGridScroll.ScrollBy(_packOpeningScrollOffset - _packGridScroll.Offset);
            _packOpeningScrollOffset = _packGridScroll.Offset;
            _packFocusIndex = Math.Clamp(_packFocusIndex, 0, Math.Max(0, cards.Count - 1));
            var start = _packGridScroll.Offset * columns;
            var end = Math.Min(cards.Count, start + columns * visibleRows);
            for (var index = start; index < end; index++)
            {
                var grant = cards[index];
                if (!_data.CardsById.TryGetValue(grant.CardId, out var card))
                {
                    continue;
                }

                var local = index - start;
                var column = local % columns;
                var row = local / columns;
                var revealStart = 0.06f + local * 0.055f;
                var revealProgress = _settings.ReducedMotion
                    ? 1f
                    : Math.Clamp((_screenElapsed - revealStart) / 0.14f, 0f, 1f);
                if (revealProgress <= 0f)
                {
                    continue;
                }
                var revealEase = revealProgress * revealProgress * (3f - 2f * revealProgress);
                var lift = _settings.ReducedMotion ? 0 : (int)MathF.Round((1f - revealEase) * 10f);
                var rect = new Rectangle(panel.X + 30 + column * 180, panel.Y + 44 + row * rowHeight + lift, 148, 208);
                var selected = _usingController && _packFocusIndex == index;
                var clicked = false;
                DrawWithOpacity(revealEase, () =>
                {
                    clicked = CardButton(rect, card, selected, grant.CopiesAdded, compact: false);
                    var note = grant.CopiesAdded > 0 ? $"+{grant.CopiesAdded} copy" : $"+{grant.DuplicateCoins} Coins";
                    DrawFittedCenteredText(note, new Rectangle(rect.X, rect.Bottom + 10, rect.Width, 24),
                        grant.CopiesAdded > 0 ? UiTheme.Success : UiTheme.DragonGold, 0.46f, 0.32f);
                });
                if (clicked)
                {
                    _packFocusIndex = index;
                }
            }

            if (DrawUxScrollBar("pack-grid", new Rectangle(panel.Right - 18, panel.Y + 28, 8, panel.Height - 56), _packGridScroll, UiTheme.Focus))
            {
                _packOpeningScrollOffset = _packGridScroll.Offset;
            }
        }

        if (Button(new Rectangle(54, 786, 150, 42), "Continue"))
        {
            _screen = Screen.Store;
            _status = "Returned to store.";
        }
    }

    private void HandlePackOpeningUxInput()
    {
        if (_lastBoosterOpening is null)
        {
            if (Pressed(Buttons.A))
            {
                _screen = Screen.Store;
            }
            return;
        }

        const int columns = 8;
        const int visibleRows = 2;
        var count = _lastBoosterOpening.Cards.Count;
        var rows = Math.Max(1, (int)Math.Ceiling(count / (double)columns));
        _packGridScroll.Configure(rows, visibleRows);
        if (count == 0)
        {
            _packFocusIndex = 0;
            _packFocusVisibilityPending = false;
            if (Pressed(Buttons.A))
            {
                _screen = Screen.Store;
            }
            return;
        }

        var focusMoved = false;
        if (DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out var horizontal))
        {
            var next = Math.Clamp(_packFocusIndex + horizontal, 0, count - 1);
            focusMoved |= next != _packFocusIndex;
            _packFocusIndex = next;
        }
        if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical))
        {
            var next = Math.Clamp(_packFocusIndex + vertical * columns, 0, count - 1);
            focusMoved |= next != _packFocusIndex;
            _packFocusIndex = next;
        }
        if (_uiActions.Triggered(UiAction.PagePrevious))
        {
            var next = Math.Max(0, _packFocusIndex - columns * visibleRows);
            focusMoved |= next != _packFocusIndex;
            _packFocusIndex = next;
        }
        else if (_uiActions.Triggered(UiAction.PageNext))
        {
            var next = Math.Min(count - 1, _packFocusIndex + columns * visibleRows);
            focusMoved |= next != _packFocusIndex;
            _packFocusIndex = next;
        }
        else if (_uiActions.Triggered(UiAction.MoveToStart))
        {
            focusMoved |= _packFocusIndex != 0;
            _packFocusIndex = 0;
        }
        else if (_uiActions.Triggered(UiAction.MoveToEnd))
        {
            focusMoved |= _packFocusIndex != count - 1;
            _packFocusIndex = count - 1;
        }
        if (focusMoved || _packFocusVisibilityPending)
        {
            _packGridScroll.EnsureVisible(_packFocusIndex / columns);
            _packFocusVisibilityPending = false;
        }
        _packOpeningScrollOffset = _packGridScroll.Offset;
        if (Pressed(Buttons.A))
        {
            _screen = Screen.Store;
        }
    }
}
