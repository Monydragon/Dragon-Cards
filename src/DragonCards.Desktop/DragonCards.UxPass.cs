using System;
using System.Collections.Generic;
using System.Linq;
using DragonCards.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace DragonCards.Desktop;

public sealed partial class DragonCardsGame
{
    private enum StoreCategory
    {
        Packs,
        StarterDecks,
        Singles
    }

    private enum StoreFocusArea
    {
        Catalog,
        Filters,
        Detail
    }

    private readonly ScrollState[] _storeCategoryScroll = [new(), new(), new()];
    private readonly int[] _storeCategoryFocus = new int[3];
    private readonly ScrollState _deckGridScroll = new();
    private readonly ScrollState _modeListScroll = new();
    private readonly ScrollState _tutorialListScroll = new();
    private readonly ScrollState _packGridScroll = new();
    private readonly ScrollState _optionsListScroll = new();
    private readonly ScrollState _handStripScroll = new();
    private readonly ScrollState _matchHistoryScroll = new();
    private readonly ScrollState _compactLogScroll = new();
    private readonly Dictionary<Screen, Screen> _screenOrigins = [];
    private readonly Dictionary<int, ScrollState> _textScrollStates = [];
    private readonly UiInputRepeater _uiInputRepeater = new();
    private StoreCategory _storeCategory;
    private StoreFocusArea _storeFocusArea;
    private int _storeDetailFocus;
    private int _storeFilterFocus;
    private string _storeSearch = "";
    private bool _storeSearchActive;
    private int _storeElementFilterIndex;
    private int _storeRarityFilterIndex;
    private int _storeSetFilterIndex;
    private int _creationControlFocus;
    private bool _matchHistoryOpen;
    private int _matchHistoryScrollOffset;
    private int _compactLogLineCount;
    private int _packFocusIndex;
    private bool _deckFocusVisibilityPending;
    private bool _packFocusVisibilityPending;
    private bool _suppressNextOriginCapture;
    private string? _draggedScrollBarId;
    private Screen _observedScreen = Screen.MainMenu;
    private float _screenFadeRemaining;
    private float _screenElapsed;
    private UiActionFrame _uiActions;

    private static readonly string[] StoreElementFilters = ["All", .. ElementOrder];
    private static readonly string[] StoreRarityFilters = ["All", .. CardRarities.All];

    private void UpdateUxPass(float elapsedSeconds)
    {
        TrackScreenTransition();
        _screenElapsed += Math.Max(0f, elapsedSeconds);
        if (_screenFadeRemaining > 0f)
        {
            _screenFadeRemaining = Math.Max(0f, _screenFadeRemaining - Math.Max(0f, elapsedSeconds));
        }

        var mapped = UiInputMapper.Map(_keyboard, _gamePad);
        _uiActions = _uiInputRepeater.Update(mapped, elapsedSeconds);
        HandleStoreSearchTextInput();
        HandleUxShortcuts();
    }

    private void TrackScreenTransition()
    {
        if (_screen == _observedScreen)
        {
            return;
        }

        var previous = _observedScreen;
        _observedScreen = _screen;
        var rememberOrigin = !_suppressNextOriginCapture;
        _suppressNextOriginCapture = false;
        if (rememberOrigin &&
            previous != Screen.PackOpening &&
            _screen is not Screen.MainMenu and not Screen.ProfilePicker and not Screen.Match and not Screen.MatchResult and not Screen.PlayerCreation and not Screen.PackOpening)
        {
            _screenOrigins[_screen] = previous;
        }

        if (_screen == Screen.DeckBuilder)
        {
            _deckFocusVisibilityPending = true;
        }
        else if (_screen == Screen.PackOpening)
        {
            _packFocusVisibilityPending = true;
        }
        else if (_screen == Screen.Options)
        {
            _optionsFocusVisibilityPending = true;
        }

        if (_screen != Screen.Match)
        {
            _matchHistoryOpen = false;
        }

        _screenFadeRemaining = _settings.ReducedMotion ? 0f : 0.24f;
        _screenElapsed = 0f;
    }

    private void HandleUxShortcuts()
    {
        if (_screen == Screen.Store &&
            ((IsDown(Keys.LeftControl) || IsDown(Keys.RightControl)) && Pressed(Keys.F) || Pressed(Keys.OemQuestion)))
        {
            _storeSearchActive = true;
            _usingController = false;
        }

        if (_screen == Screen.Match && (_matchHistoryOpen || !HasDecisionModalForHistory()) &&
            (Pressed(Keys.L) || Pressed(Buttons.Back)))
        {
            _matchHistoryOpen = !_matchHistoryOpen;
            _cardDetailScrollOffset = 0;
            _audio.Play(_matchHistoryOpen ? SoundKeys.UiClick : SoundKeys.UiBack);
        }
    }

    private bool HasDecisionModalForHistory()
    {
        if (_chooseFreeEnergy)
        {
            return true;
        }

        if (_engine is null)
        {
            return false;
        }

        var state = _engine.State;
        return CanHumanResolveEnergyChoice(state) ||
            CanHumanResolveEnergySourceChoice(state) ||
            CanHumanResolveTarget(state) ||
            CanHumanResolveCombatAction(state) ||
            CanHumanResolveBlock(state);
    }

    private bool HandleUxBack()
    {
        if (_matchHistoryOpen)
        {
            _matchHistoryOpen = false;
            _audio.Play(SoundKeys.UiBack);
            return true;
        }

        if (_screen == Screen.Multiplayer && IsDirectLobbyActive)
        {
            CancelDirectLobby();
            _audio.Play(SoundKeys.UiBack);
            return true;
        }

        if (_screen == Screen.Multiplayer && _joinInviteEditing)
        {
            _joinInviteEditing = false;
            _audio.Play(SoundKeys.UiBack);
            return true;
        }

        if (_screen == Screen.Match && _chooseFreeEnergy && _engine?.State.PendingEnergyChoice is null)
        {
            _chooseFreeEnergy = false;
            _audio.Play(SoundKeys.UiBack);
            return true;
        }

        if (_screen == Screen.Match && _replacementTarget is not null)
        {
            _replacementTarget = null;
            _replacementHandIndex = -1;
            _status = "Replacement canceled.";
            _audio.Play(SoundKeys.UiBack);
            return true;
        }

        if (_screen == Screen.Match && _engine is not null)
        {
            var state = _engine.State;
            if (CanHumanResolveBlock(state))
            {
                ExecuteCommand("pass-block", "", _engine.PassBlock);
                ClearSelections();
                return true;
            }

            if (CanHumanResolveCombatAction(state) && state.PendingCombatAction is { } combatAction)
            {
                ExecuteCommand("combat-pass", combatAction.PriorityPlayerIndex.ToString(), () => _engine.PassCombatAction(combatAction.PriorityPlayerIndex));
                ClearSelections();
                return true;
            }

            if (CanHumanResolveEnergyChoice(state) || CanHumanResolveEnergySourceChoice(state) || CanHumanResolveTarget(state))
            {
                _status = "Resolve the pending choice before leaving the match.";
                _audio.Play(SoundKeys.UiBack);
                return true;
            }
        }

        if (_storeSearchActive)
        {
            _storeSearchActive = false;
            return true;
        }

        if (_screen == Screen.Store && _storeFocusArea != StoreFocusArea.Catalog)
        {
            _storeFocusArea = StoreFocusArea.Catalog;
            return true;
        }

        return false;
    }

    private Screen UxBackDestination(Screen fallback)
    {
        _suppressNextOriginCapture = true;
        if (_screenOrigins.TryGetValue(_screen, out var origin) && origin != Screen.Match)
        {
            return origin;
        }

        return fallback;
    }

    private void DrawUxTransition()
    {
        if (_screenFadeRemaining <= 0f || _settings.ReducedMotion)
        {
            return;
        }

        var amount = Math.Clamp(_screenFadeRemaining / 0.17f, 0f, 1f);
        Fill(new Rectangle(0, 0, VirtualWidth, VirtualHeight), new Color((byte)4, (byte)7, (byte)12, (byte)MathF.Round(amount * 196f)));
    }

    private void HandleStoreSearchTextInput()
    {
        if (_screen != Screen.Store || !_storeSearchActive)
        {
            return;
        }

        if (Pressed(Keys.Back) && _storeSearch.Length > 0)
        {
            _storeSearch = _storeSearch[..^1];
            ResetActiveStorePosition();
            return;
        }

        if (Pressed(Keys.Delete))
        {
            _storeSearch = "";
            ResetActiveStorePosition();
            return;
        }

        if (_storeSearch.Length >= 32)
        {
            return;
        }

        var shift = _keyboard.IsKeyDown(Keys.LeftShift) || _keyboard.IsKeyDown(Keys.RightShift);
        foreach (var key in _keyboard.GetPressedKeys())
        {
            if (_previousKeyboard.IsKeyDown(key))
            {
                continue;
            }

            char? next = key switch
            {
                >= Keys.A and <= Keys.Z => (char)((shift ? 'A' : 'a') + (key - Keys.A)),
                >= Keys.D0 and <= Keys.D9 => (char)('0' + (key - Keys.D0)),
                Keys.Space => ' ',
                Keys.OemMinus or Keys.Subtract => '-',
                _ => null
            };
            if (next is not null)
            {
                _storeSearch += next.Value;
                ResetActiveStorePosition();
            }
        }
    }

    private IReadOnlyList<ShopCatalogItem> FilteredStoreCatalog()
    {
        var category = _storeCategory switch
        {
            StoreCategory.Packs => ShopItemKind.Booster,
            StoreCategory.StarterDecks => ShopItemKind.StarterDeck,
            _ => ShopItemKind.SingleCard
        };
        var element = StoreElementFilters[Math.Clamp(_storeElementFilterIndex, 0, StoreElementFilters.Length - 1)];
        var rarity = StoreRarityFilters[Math.Clamp(_storeRarityFilterIndex, 0, StoreRarityFilters.Length - 1)];
        var sets = StoreSetFilters();
        var set = sets[Math.Clamp(_storeSetFilterIndex, 0, sets.Count - 1)];
        return StoreCatalogFilter.Apply(
            ShopCatalogService.CreateCatalog(_data),
            _data,
            category,
            _storeSearch,
            element,
            rarity,
            set);
    }

    private IReadOnlyList<string> StoreSetFilters() =>
        ["All", .. _data.Cards.Select(card => card.SetId).Where(set => !string.IsNullOrWhiteSpace(set)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(BoosterService.SetDisplayName, StringComparer.OrdinalIgnoreCase)];

    private ScrollState ActiveStoreScroll => _storeCategoryScroll[(int)_storeCategory];

    private void PersistActiveStorePosition()
    {
        _storeCategoryFocus[(int)_storeCategory] = _storeFocus;
        _storeScrollOffset = ActiveStoreScroll.Offset;
    }

    private void SwitchStoreCategory(int delta)
    {
        PersistActiveStorePosition();
        var count = Enum.GetValues<StoreCategory>().Length;
        _storeCategory = (StoreCategory)(((int)_storeCategory + delta + count) % count);
        _storeFocus = _storeCategoryFocus[(int)_storeCategory];
        _storeScrollOffset = ActiveStoreScroll.Offset;
        _storeFocusArea = StoreFocusArea.Catalog;
        ClampActiveStoreSelection(ensureVisible: false);
    }

    private void ResetActiveStorePosition()
    {
        _storeFocus = 0;
        _storeCategoryFocus[(int)_storeCategory] = 0;
        ActiveStoreScroll.MoveToStart();
        _storeScrollOffset = 0;
        _cardDetailScrollOffset = 0;
    }

    private void ResetStoreFilters()
    {
        _storeSearch = "";
        _storeElementFilterIndex = 0;
        _storeRarityFilterIndex = 0;
        _storeSetFilterIndex = 0;
        ResetActiveStorePosition();
    }

    private void ClampActiveStoreSelection(bool ensureVisible)
    {
        var items = FilteredStoreCatalog();
        var visibleRows = _storeCategory == StoreCategory.Singles ? 5 : 6;
        ActiveStoreScroll.Configure(items.Count, visibleRows);
        _storeFocus = Math.Clamp(_storeFocus, 0, Math.Max(0, items.Count - 1));
        if (ensureVisible && items.Count > 0)
        {
            ActiveStoreScroll.EnsureVisible(_storeFocus);
        }

        PersistActiveStorePosition();
    }

    private void CycleStoreFilter(ref int index, int count, int delta = 1)
    {
        index = (index + delta + count) % count;
        ResetActiveStorePosition();
    }

    private bool DrawUxScrollBar(string id, Rectangle track, ScrollState state, Color? accent = null)
    {
        state.Configure(state.LineCount, state.ViewportLineCount);
        if (state.MaxOffset <= 0)
        {
            if (_draggedScrollBarId == id)
            {
                _draggedScrollBarId = null;
            }
            return false;
        }

        var metrics = state.GetScrollBar(track.Y, track.Height, 24);
        var thumb = new Rectangle(track.X, metrics.ThumbStart, track.Width, metrics.ThumbLength);
        var trackHitTarget = new Rectangle(track.Center.X - UiTheme.MinimumTargetSize / 2, track.Y, UiTheme.MinimumTargetSize, track.Height);
        var thumbHitTarget = UiTheme.MinimumHitTarget(thumb);
        var overTrack = trackHitTarget.Contains(_virtualMouse);
        var overThumb = thumbHitTarget.Contains(_virtualMouse);
        Fill(track, UiTheme.ScrollTrack);
        Fill(thumb, overThumb || _draggedScrollBarId == id ? accent ?? UiTheme.Focus : UiTheme.ScrollThumb);

        var pressedNow = _mouse.LeftButton == ButtonState.Pressed;
        var pressedBefore = _previousMouse.LeftButton == ButtonState.Pressed;
        if (pressedNow && !pressedBefore && overThumb)
        {
            _draggedScrollBarId = id;
            state.BeginThumbDrag(Math.Clamp(_virtualMouse.Y, metrics.ThumbStart, metrics.ThumbEnd - 1), metrics);
            _usingController = false;
            return true;
        }

        if (pressedNow && !pressedBefore && overTrack && !overThumb)
        {
            state.ClickTrack(_virtualMouse.Y, metrics);
            _usingController = false;
            return true;
        }

        if (_draggedScrollBarId == id)
        {
            if (!pressedNow)
            {
                state.EndThumbDrag();
                _draggedScrollBarId = null;
                return true;
            }

            state.DragThumb(_virtualMouse.Y, state.GetScrollBar(track.Y, track.Height, 24));
            return true;
        }

        return false;
    }

    private ScrollState TextScrollState(Rectangle bounds)
    {
        var key = HashCode.Combine(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        if (!_textScrollStates.TryGetValue(key, out var state))
        {
            state = new ScrollState();
            _textScrollStates[key] = state;
        }

        return state;
    }

    private void DrawUiText(string text, Vector2 position, Color color, float scale = 1f)
    {
        _spriteBatch!.DrawString(_uiFont ?? _font!, text, position, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }

    private void DrawUiText(string text, Rectangle bounds, Color color, float scale = 0.68f)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var font = _uiFont ?? _font!;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = "";
        var y = bounds.Y;
        var lineHeight = MathF.Ceiling(font.LineSpacing * scale * 1.16f);
        foreach (var word in words)
        {
            var candidate = string.IsNullOrEmpty(line) ? word : $"{line} {word}";
            if (font.MeasureString(candidate).X * scale > bounds.Width && !string.IsNullOrEmpty(line))
            {
                DrawUiText(line, new Vector2(bounds.X, y), color, scale);
                y += (int)lineHeight;
                line = word;
                if (y + lineHeight > bounds.Bottom)
                {
                    return;
                }
            }
            else
            {
                line = candidate;
            }
        }

        if (!string.IsNullOrEmpty(line) && y + lineHeight <= bounds.Bottom)
        {
            DrawUiText(line, new Vector2(bounds.X, y), color, scale);
        }
    }

    private void DrawMatchHistoryOverlayUx()
    {
        if (!_matchHistoryOpen || _screen != Screen.Match || _engine is null)
        {
            return;
        }

        _drawingModal = true;
        try
        {
            Fill(new Rectangle(0, 0, VirtualWidth, VirtualHeight), new Color(0, 0, 0, 178));
            var panel = new Rectangle(236, 116, 1128, 650);
            DrawPanel(panel, UiTheme.PanelRaised, border: UiTheme.BorderStrong);
            DrawUiText("Match History", new Vector2(panel.X + 30, panel.Y + 22), Color.White, 0.92f);
            DrawUiText("A chronological record of visible match actions.", new Vector2(panel.X + 32, panel.Y + 62), UiTheme.TextMuted, 0.52f);

            var timelinePanel = new Rectangle(panel.X + 30, panel.Y + 106, 310, 482);
            DrawPanel(timelinePanel, UiTheme.Panel, border: UiTheme.Border);
            DrawText("Recent Highlights", new Vector2(timelinePanel.X + 18, timelinePanel.Y + 16), Color.White, 0.68f);
            var y = timelinePanel.Y + 54;
            foreach (var entry in _matchTimelineEntries.TakeLast(12))
            {
                var icon = new Rectangle(timelinePanel.X + 18, y, 28, 24);
                Fill(icon, Color.Lerp(entry.Color, Color.Black, 0.28f));
                Border(icon, Color.Lerp(entry.Color, Color.White, 0.22f), 1);
                DrawFittedCenteredText(entry.Icon, Inset(icon, 2), Color.White, 0.34f, 0.2f);
                DrawText(entry.Text, new Rectangle(icon.Right + 10, y, timelinePanel.Width - 76, 34), UiTheme.Text, 0.44f);
                y += 36;
                if (y + 34 > timelinePanel.Bottom)
                {
                    break;
                }
            }

            var logPanel = new Rectangle(panel.X + 364, panel.Y + 106, 734, 482);
            DrawPanel(logPanel, UiTheme.Panel, border: UiTheme.Border);
            var logText = string.Join('\n', _engine.State.Log);
            var bounds = new Rectangle(logPanel.X + 18, logPanel.Y + 18, logPanel.Width - 48, logPanel.Height - 36);
            DrawScrollableText(logText, bounds, ref _matchHistoryScrollOffset, UiTheme.Text, 0.6f);

            if (Button(new Rectangle(panel.Right - 164, panel.Bottom - 50, 132, 34), "Close", focused: _usingController))
            {
                _matchHistoryOpen = false;
                _audio.Play(SoundKeys.UiBack);
            }
        }
        finally
        {
            _drawingModal = false;
        }
    }

    private void HandleMatchHistoryInput()
    {
        if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical))
        {
            _matchHistoryScrollOffset = Math.Max(0, _matchHistoryScrollOffset + vertical);
        }
        if (_uiActions.Triggered(UiAction.PagePrevious))
        {
            _matchHistoryScrollOffset = Math.Max(0, _matchHistoryScrollOffset - 10);
        }
        else if (_uiActions.Triggered(UiAction.PageNext))
        {
            _matchHistoryScrollOffset += 10;
        }
        else if (_uiActions.Triggered(UiAction.MoveToStart))
        {
            _matchHistoryScrollOffset = 0;
        }
        else if (_uiActions.Triggered(UiAction.MoveToEnd))
        {
            _matchHistoryScrollOffset = int.MaxValue;
        }
    }
}
