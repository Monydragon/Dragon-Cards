using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DragonCards.Core;
using DragonCards.Networking;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace DragonCards.Desktop;

public sealed partial class DragonCardsGame : Game
{
    private enum DeckFocusArea
    {
        Grid,
        ElementFilters,
        CollectionFilters,
        TypeFilters,
        CardDetail,
        CardActions,
        AssistantGoals,
        AssistantActions,
        Suggestions,
        Footer
    }

    private const int VirtualWidth = 1600;
    private const int VirtualHeight = 900;
    private const int WindowedWidth = 1440;
    private const int WindowedHeight = 900;
    private const float CardDetailTextScale = 0.74f;
    private const float SmallCardDetailTextScale = 0.64f;
    private const float PromptDetailTextScale = 0.72f;
    private const float AiInterActionPauseSeconds = 0.18f;
    private static readonly int[] InteractionSpeedPercentOptions = [55, 70, 100, 140];
    private static readonly string[] ElementOrder = ["Fire", "Ice", "Wind", "Earth", "Lightning", "Water", "Light", "Dark"];
    private static readonly (int Width, int Height)[] WindowSizeOptions = [(1280, 720), (1440, 900), (1600, 900), (1920, 1080)];
    private static readonly IReadOnlyList<TutorialDefinition> TutorialDefinitions = CreateTutorialDefinitions();

    private readonly GraphicsDeviceManager _graphics;
    private readonly bool _captureScreensOnStart;
    private readonly string _captureDirectory;
    private readonly GameSettings _settings = GameSettings.Load();
    private SpriteBatch? _spriteBatch;
    private SpriteFont? _font;
    private SpriteFont? _uiFont;
    private TextLayoutCache? _textLayoutCache;
    private Texture2D? _pixel;
    private Texture2D? _logoTexture;
    private Texture2D? _mainMenuDragonTexture;
    private readonly AudioService _audio = new();
    private readonly Dictionary<string, Texture2D> _rarityIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MatchTimelineEntry> _matchTimelineEntries = [];
    private MouseState _mouse;
    private MouseState _previousMouse;
    private KeyboardState _keyboard;
    private KeyboardState _previousKeyboard;
    private GamePadState _gamePad;
    private GamePadState _previousGamePad;
    private Point _virtualMouse;
    private bool _clickConsumed;
    private bool _captureCompleted;
    private bool _usingController;
    private Rectangle _viewportRectangle;
    private float _viewportScale = 1f;

    private GameData _data = null!;
    private IReadOnlyList<ValidationIssue> _dataIssues = [];
    private Screen _screen = Screen.MainMenu;
    private DeckBuilderState _deckBuilder = null!;
    private DragonDuelEngine? _engine;
    private readonly DragonDuelAi _ai = new();
    private readonly PresentationDirector _presentation = new();
    private float _aiInteractionDelaySeconds;
    private bool _aiAwaitingPresentation;
    private MatchKind _matchKind = MatchKind.Hotseat;
    private const int HumanPlayerIndex = 0;
    private const int AiPlayerIndex = 1;
    private int _selectedHandIndex = -1;
    private int _selectedUnitIndex = -1;
    private int _selectedSupportIndex = -1;
    private int _energyFieldScrollOffset;
    private int _energyChoiceIndex;
    private int _energySourceChoiceIndex;
    private int _selectedBlockerIndex = -1;
    private int _menuFocus;
    private int _mainMenuModeIndex;
    private int _multiplayerFocus;
    private int _optionsFocus;
    private int _deckFocusIndex;
    private DeckFocusArea _deckFocusArea;
    private int _deckControlFocus;
    private int _tutorialFocus;
    private int _modeFocus;
    private int _modeActionFocus;
    private int _resultFocus;
    private int _avatarFocus;
    private int _starterClashOpponentIndex = 1;
    private DeckAssistantGoal _deckAssistantGoal = DeckAssistantGoal.Balanced;
    private IReadOnlyList<DeckSuggestion> _deckAssistantSuggestions = [];
    private SealedPool? _sealedPool;
    private MatchFocus _matchFocus = MatchFocus.Hand;
    private string _status = "Ready.";
    private string _tutorialNotice = "";
    private CardDefinition? _zoomCard;
    private int _zoomCount;
    private Rectangle _zoomSource;
    private DraggedHandCard? _draggedHandCard;
    private bool _chooseFreeEnergy;
    private int _replacementHandIndex = -1;
    private ReplacementTarget? _replacementTarget;
    private NetworkInvite _hostInvite = new();
    private string _hostInviteCode = "";
    private string _hostSameComputerInviteCode = "";
    private string _joinInviteCode = "";
    private string _multiplayerNotice = "Choose Local, Host Lobby, or Join Lobby to start multiplayer.";
    private int _logScrollOffset;
    private TutorialRuntimeState? _tutorial;
    private bool _modalInputActive;
    private bool _drawingModal;
    private Rectangle _hoveredButtonRect;
    private bool _buttonHoveredThisFrame;
    private float _drawOpacity = 1f;
    private bool _matchInspectorFocused;
    private bool _optionsFocusVisibilityPending;

    public DragonCardsGame(bool captureScreensOnStart = false, string? captureDirectory = null)
    {
        _captureScreensOnStart = captureScreensOnStart;
        _captureDirectory = string.IsNullOrWhiteSpace(captureDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "render-captures")
            : captureDirectory;

        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = _settings.WindowWidth,
            PreferredBackBufferHeight = _settings.WindowHeight,
            IsFullScreen = _settings.Fullscreen,
            SynchronizeWithVerticalRetrace = true
        };

        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        Window.Title = "Dragon Cards";
        Window.AllowUserResizing = true;
    }

    protected override void Initialize()
    {
        _data = GameData.LoadDefault();
        _dataIssues = GameDataValidator.Validate(_data);
        InitializeProgressionState();
        _settings.Save();
        base.Initialize();
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _font = Content.Load<SpriteFont>("Fonts/Default");
        _uiFont = Content.Load<SpriteFont>("Fonts/Ui");
        _textLayoutCache = new TextLayoutCache(text => _font!.MeasureString(text).X, capacity: 512);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData([Color.White]);
        LoadRarityIcons();
        TryLoadLogo();
        TryLoadMainMenuDragonArt();
        _audio.Configure(() => _settings.SoundVolume, () => _settings.MusicVolume, () => _settings.MuteAudio, CardTypeForAudio);
        _audio.Load(Content);
        _audio.LoadLoopingMusic(Content, "Audio/Throne_of_Parchment", "Throne of Parchment");
    }

    private string CardTypeForAudio(string cardId) =>
        !string.IsNullOrWhiteSpace(cardId) && _data.CardsById.TryGetValue(cardId, out var card) ? card.Type : "";

    private void TryLoadLogo()
    {
        try
        {
            _logoTexture = Content.Load<Texture2D>("Branding/dragon-cards-logo");
        }
        catch
        {
            _logoTexture = null;
        }
    }

    private void TryLoadMainMenuDragonArt()
    {
        try
        {
            _mainMenuDragonTexture = Content.Load<Texture2D>("Branding/main-menu-blue-dragon");
        }
        catch
        {
            _mainMenuDragonTexture = null;
        }
    }

    private void LoadRarityIcons()
    {
        TryLoadRarityIcon(CardRarities.Common, "common");
        TryLoadRarityIcon(CardRarities.Uncommon, "uncommon");
        TryLoadRarityIcon(CardRarities.Rare, "rare");
        TryLoadRarityIcon(CardRarities.Legendary, "legendary");
        TryLoadRarityIcon(CardRarities.Mythic, "mythic");
    }

    private void TryLoadRarityIcon(string rarity, string assetName)
    {
        try
        {
            _rarityIcons[rarity] = Content.Load<Texture2D>($"Icons/Rarity/{assetName}");
        }
        catch
        {
            // Generated icons are cosmetic; the drawn rarity badge remains the fallback.
        }
    }

    protected override void Update(GameTime gameTime)
    {
        var elapsedSeconds = (float)gameTime.ElapsedGameTime.TotalSeconds;
        _previousMouse = _mouse;
        _previousKeyboard = _keyboard;
        _previousGamePad = _gamePad;
        _mouse = Mouse.GetState();
        _keyboard = Keyboard.GetState();
        _gamePad = GamePad.GetState(PlayerIndex.One);
        _clickConsumed = false;
        _audio.Update(gameTime.ElapsedGameTime.TotalSeconds);
        UpdateViewportMapping();
        UpdateUxPass(elapsedSeconds);
        HandleLogScroll();
        HandleProgressionUpdate();

        if (Pressed(Keys.F11) || (IsDown(Keys.LeftAlt) && Pressed(Keys.Enter)) || Pressed(Buttons.Start))
        {
            ToggleFullscreen();
        }

        if (Pressed(Keys.F10))
        {
            CaptureScreens();
        }

        if (Pressed(Keys.Escape) || Pressed(Buttons.B))
        {
            if (!HandleUxBack())
            {
                GoBack();
            }

            TrackScreenTransition();
            base.Update(gameTime);
            return;
        }

        _presentation.ReducedMotion = _settings.ReducedMotion;
        _presentation.SpeedMultiplier = InteractionSpeedMultiplier;
        _presentation.Update(elapsedSeconds);
        _audio.PlayActivationCues(_presentation.DrainActivations());
        var skipBlockingBeat = _presentation.IsBlocking &&
            (Pressed(Keys.Space) || Pressed(Buttons.A) || Hit(new Rectangle(0, 0, VirtualWidth, VirtualHeight)));
        var skipNonBlockingGroup = !_presentation.IsBlocking &&
            (Pressed(Keys.K) || Pressed(Buttons.LeftStick));
        if (_presentation.CanSkip && (skipBlockingBeat || skipNonBlockingGroup))
        {
            _presentation.SkipActive();
            _audio.PlayActivationCues(_presentation.DrainActivations());
            TrackScreenTransition();
            base.Update(gameTime);
            return;
        }

        if (_presentation.IsBlocking)
        {
            TrackScreenTransition();
            base.Update(gameTime);
            return;
        }

        if (TryOpenPendingResultScreen())
        {
            TrackScreenTransition();
            base.Update(gameTime);
            return;
        }

        HandleControllerInput();
        HandleMouseDrag();
        UpdateAiInteractionPacing(elapsedSeconds);
        TrackScreenTransition();
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_spriteBatch is null || _font is null || _pixel is null)
        {
            return;
        }

        if (_captureScreensOnStart && !_captureCompleted)
        {
            CaptureScreens();
            _captureCompleted = true;
            try { Exit(); }
            catch (PlatformNotSupportedException) { }
            return;
        }

        UpdateViewportMapping();
        GraphicsDevice.Clear(Color.Black);
        var transform =
            Matrix.CreateScale(_viewportScale, _viewportScale, 1f) *
            Matrix.CreateTranslation(_viewportRectangle.X, _viewportRectangle.Y, 0f);

        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, transformMatrix: transform);
        DrawVirtualScene();
        _spriteBatch.End();
        base.Draw(gameTime);
    }

    private void DrawVirtualScene()
    {
        _zoomCard = null;
        _zoomCount = 0;
        _zoomSource = Rectangle.Empty;
        _buttonHoveredThisFrame = false;
        DrawBackdrop();
        DrawHeader();
        _modalInputActive = IsDecisionPromptActive() || _matchHistoryOpen;

        if (_screen == Screen.MainMenu)
        {
            DrawMainMenu();
        }
        else if (_screen == Screen.ModeSelect)
        {
            DrawModeSelect();
        }
        else if (_screen == Screen.PlayerCreation)
        {
            DrawPlayerCreation();
        }
        else if (_screen == Screen.ProfilePicker)
        {
            DrawProfilePicker();
        }
        else if (_screen == Screen.Store)
        {
            DrawStoreUx();
        }
        else if (_screen == Screen.PackOpening)
        {
            DrawPackOpeningUx();
        }
        else if (_screen == Screen.Quests)
        {
            DrawQuestBoard();
        }
        else if (_screen == Screen.ProfileData)
        {
            DrawProfileData();
        }
        else if (_screen == Screen.MatchResult)
        {
            DrawMatchResult();
        }
        else if (_screen == Screen.Multiplayer)
        {
            DrawMultiplayerMenu();
        }
        else if (_screen == Screen.Tutorials)
        {
            DrawTutorials();
        }
        else if (_screen == Screen.Options)
        {
            DrawOptionsMenu();
        }
        else if (_screen == Screen.DeckBuilder)
        {
            DrawDeckBuilderUx();
        }
        else
        {
            DrawMatch();
        }

        DrawStatusBar();
        DrawPresentationOverlay();
        DrawDecisionPromptOverlay();
        DrawTutorialStepOverlay();
        DrawMatchHistoryOverlayUx();
        DrawZoomPreview();
        DrawDraggedCard();
        DrawElementPicker();
        if (!_buttonHoveredThisFrame)
        {
            _hoveredButtonRect = Rectangle.Empty;
        }

        DrawUxTransition();

        _modalInputActive = false;
    }

    private void DrawBackdrop()
    {
        Fill(new Rectangle(0, 0, VirtualWidth, VirtualHeight), new Color(16, 18, 23));
        Fill(new Rectangle(0, 0, VirtualWidth, 130), new Color(24, 28, 36));
        Fill(new Rectangle(0, 130, VirtualWidth, 770), new Color(21, 25, 31));
        for (var i = 0; i < 16; i++)
        {
            var tone = i % 2 == 0 ? new Color(18, 22, 28) : new Color(23, 27, 34);
            Fill(new Rectangle(i * 100, 130, 42, 770), tone);
        }
    }

    private void DrawHeader()
    {
        Fill(new Rectangle(0, 0, VirtualWidth, 74), new Color(28, 33, 42));
        Fill(new Rectangle(0, 72, VirtualWidth, 2), new Color(112, 126, 144));
        if (_logoTexture is not null)
        {
            DrawImageContain(_logoTexture, new Rectangle(28, 3, 76, 68), Color.White);
            DrawText("Dragon Cards", new Vector2(116, 18), Color.White, 0.96f);
            DrawText(CurrentModeHeader(), new Vector2(278, 25), new Color(207, 217, 229), 0.78f);
        }
        else
        {
            DrawText("Dragon Cards", new Vector2(34, 18), Color.White, 1.35f);
            DrawText(CurrentModeHeader(), new Vector2(278, 25), new Color(207, 217, 229), 0.78f);
        }
    }

    private string CurrentModeHeader()
    {
        if (_engine is not null)
        {
            return _engine.State.Mode.Name;
        }

        if (_screen == Screen.MainMenu && PlayableModeCatalog.All.Count > 0)
        {
            var index = Math.Clamp(_mainMenuModeIndex, 0, PlayableModeCatalog.All.Count - 1);
            return $"Chosen: {PlayableModeCatalog.All[index].Name}";
        }

        return "Dragon Cards";
    }

    private void DrawMainMenu()
    {
        var currentDeck = CurrentDeck();
        var currentDeckIssues = GameDataValidator.ValidateDeck(currentDeck, _data);
        var canOpenModes = _profile is not null && _dataIssues.Count == 0;
        var modes = PlayableModeCatalog.All;
        _mainMenuModeIndex = Math.Clamp(_mainMenuModeIndex, 0, Math.Max(0, modes.Count - 1));
        var selectedMode = modes.Count == 0 ? null : modes[_mainMenuModeIndex];

        DrawDragonHeroBackground();
        DrawText("ENTER THE DRAGON'S ROOST", new Vector2(56, 108), new Color(218, 236, 255), 1.0f);
        DrawText("Choose your deck, choose your flight, and let the blue wyrm carry you into the next duel.", new Vector2(58, 144), new Color(180, 209, 235), 0.6f);

        var roostPanel = new Rectangle(50, 172, 770, 582);
        DrawPanel(roostPanel, new Color(5, 13, 29, 224), border: new Color(70, 136, 194));
        DrawText("YOUR CREST", new Vector2(roostPanel.X + 28, roostPanel.Y + 22), new Color(112, 201, 255), 0.58f);
        DrawDeckSummary(new Rectangle(roostPanel.X + 28, roostPanel.Y + 58, roostPanel.Width - 56, 234), currentDeck, currentDeckIssues);
        DrawMainMenuModeChooser(new Rectangle(roostPanel.X + 28, roostPanel.Y + 316, roostPanel.Width - 56, 236), selectedMode, canOpenModes);

        DrawProfileSummary(new Rectangle(850, 180, 300, 222));
        DrawPanel(new Rectangle(850, 424, 300, 118), new Color(6, 16, 34, 204), border: new Color(70, 136, 194));
        DrawText("THE BLUE WYRM WATCHES", new Vector2(872, 446), new Color(116, 205, 255), 0.5f);
        DrawText("Wing over the horizon. Tail around the table. Keep your eyes on the next card.", new Rectangle(872, 478, 256, 48), new Color(195, 218, 237), 0.48f);

        var navigationPanel = new Rectangle(1192, 172, 348, 632);
        DrawPanel(navigationPanel, new Color(5, 13, 29, 216), border: new Color(70, 136, 194));
        DrawText("TAKE FLIGHT", new Vector2(navigationPanel.X + 26, navigationPanel.Y + 24), new Color(112, 201, 255), 0.64f);
        DrawText(selectedMode is null ? "Select a mode" : $"Prepared for {selectedMode.Name}", new Rectangle(navigationPanel.X + 26, navigationPanel.Y + 56, navigationPanel.Width - 52, 34), new Color(213, 231, 247), 0.5f);

        var buttonX = navigationPanel.X + 26;
        var buttonWidth = navigationPanel.Width - 52;
        if (Button(new Rectangle(buttonX, navigationPanel.Y + 104, buttonWidth, 48), "Open Selected Mode", canOpenModes, _usingController && _menuFocus == 0))
        {
            OpenSelectedMainMenuMode();
        }

        if (Button(new Rectangle(buttonX, navigationPanel.Y + 160, buttonWidth, 44), "Multiplayer", canOpenModes, _usingController && _menuFocus == 1))
        {
            EnsureHostInvite();
            _screen = Screen.Multiplayer;
            _multiplayerSection = MultiplayerSection.Local;
            _directLobbyState = DirectLobbyState.Idle;
            _joinInviteEditing = false;
            _status = "Multiplayer opened.";
        }

        if (Button(new Rectangle(buttonX, navigationPanel.Y + 212, buttonWidth, 44), "Deck Builder", focused: _usingController && _menuFocus == 2))
        {
            _screen = Screen.DeckBuilder;
            _status = "Deck builder opened.";
        }

        if (Button(new Rectangle(buttonX, navigationPanel.Y + 264, buttonWidth, 44), "Store / Packs", focused: _usingController && _menuFocus == 3))
        {
            _screen = Screen.Store;
            _status = "Store opened.";
        }

        if (Button(new Rectangle(buttonX, navigationPanel.Y + 316, buttonWidth, 44), "Tutorials", focused: _usingController && _menuFocus == 4))
        {
            _screen = Screen.Tutorials;
            _status = "Tutorials opened.";
        }

        if (Button(new Rectangle(buttonX, navigationPanel.Y + 368, buttonWidth, 44), "Quest Board", _profile is not null, _usingController && _menuFocus == 5))
        {
            _screen = Screen.Quests;
            _status = "Quest Board opened.";
        }

        if (Button(new Rectangle(buttonX, navigationPanel.Y + 420, buttonWidth, 44), "Options", focused: _usingController && _menuFocus == 6))
        {
            _screen = Screen.Options;
            _status = "Options opened.";
        }

        if (Button(new Rectangle(buttonX, navigationPanel.Y + 472, buttonWidth, 44), "Profile Data", _profile is not null, _usingController && _menuFocus == 7))
        {
            _screen = Screen.ProfileData;
            _profileDataNotice = "Profile data workspace opened.";
            _profileDataAudit = [];
            _profileSeedRuns = [];
        }

        if (Button(new Rectangle(buttonX, navigationPanel.Y + 524, (buttonWidth - 12) / 2, 44), "Profiles", focused: _usingController && _menuFocus == 8))
        {
            BeginNewGame();
        }

        if (Button(new Rectangle(buttonX + (buttonWidth + 12) / 2, navigationPanel.Y + 524, (buttonWidth - 12) / 2, 44), "Exit", focused: _usingController && _menuFocus == 9))
        {
            try { Exit(); }
            catch (PlatformNotSupportedException) { }
        }
    }

    private void DrawDragonHeroBackground()
    {
        if (_mainMenuDragonTexture is not null)
        {
            DrawImageCover(_mainMenuDragonTexture, new Rectangle(0, 74, VirtualWidth, 788), new Color(255, 255, 255, 238));
        }

        Fill(new Rectangle(0, 74, 930, 788), new Color(3, 10, 24, 208));
        Fill(new Rectangle(0, 74, VirtualWidth, 788), new Color(3, 8, 20, 54));
        for (var index = 0; index < 9; index++)
        {
            var y = 176 + index * 72;
            Fill(new Rectangle(824 + index * 16, y, 2, 46), new Color(73, 180, 255, 90 - index * 6));
        }
    }

    private void DrawMainMenuModeChooser(Rectangle rect, PlayableModeDefinition? selectedMode, bool canOpenModes)
    {
        DrawPanel(rect, new Color(8, 22, 45, 228), border: new Color(63, 138, 200));
        DrawText("CHOOSE YOUR FLIGHT", new Vector2(rect.X + 22, rect.Y + 18), new Color(112, 201, 255), 0.54f);
        if (selectedMode is null)
        {
            DrawText("No playable modes are available.", new Vector2(rect.X + 22, rect.Y + 72), new Color(255, 162, 128), 0.64f);
            return;
        }

        if (Button(new Rectangle(rect.X + 22, rect.Y + 62, 48, 42), "<", PlayableModeCatalog.All.Count > 1))
        {
            MoveMainMenuMode(-1);
        }
        if (Button(new Rectangle(rect.Right - 70, rect.Y + 62, 48, 42), ">", PlayableModeCatalog.All.Count > 1))
        {
            MoveMainMenuMode(1);
        }

        DrawFittedCenteredText(selectedMode.Name, new Rectangle(rect.X + 90, rect.Y + 62, rect.Width - 180, 42), Color.White, 0.84f, 0.5f);
        DrawText(selectedMode.ProgressionEligible ? "Progression enabled" : "Progression disabled", new Rectangle(rect.X + 22, rect.Y + 120, 250, 24), selectedMode.ProgressionEligible ? new Color(148, 224, 164) : new Color(255, 190, 120), 0.48f);
        DrawText(selectedMode.Description, new Rectangle(rect.X + 22, rect.Y + 148, rect.Width - 44, 48), new Color(205, 222, 239), 0.5f);
        if (Button(new Rectangle(rect.Right - 210, rect.Bottom - 48, 188, 34), "Mode Setup", canOpenModes))
        {
            OpenSelectedMainMenuMode();
        }
    }

    private void MoveMainMenuMode(int delta)
    {
        var count = PlayableModeCatalog.All.Count;
        if (count == 0)
        {
            return;
        }

        _mainMenuModeIndex = (_mainMenuModeIndex + delta + count) % count;
        _modeFocus = _mainMenuModeIndex;
        _status = $"Selected {PlayableModeCatalog.All[_mainMenuModeIndex].Name}.";
    }

    private void OpenSelectedMainMenuMode()
    {
        if (PlayableModeCatalog.All.Count == 0)
        {
            return;
        }

        _modeFocus = Math.Clamp(_mainMenuModeIndex, 0, PlayableModeCatalog.All.Count - 1);
        _modeListScroll.SetOffset(Math.Max(0, _modeFocus - 2));
        _screen = Screen.ModeSelect;
        _status = $"{PlayableModeCatalog.All[_modeFocus].Name} selected. Choose a setup to continue.";
    }

    private void DrawModeSelect()
    {
        DrawText("Mode Select", new Vector2(54, 108), Color.White, 1.2f);
        DrawText("Choose a way to play. Every mode earns profile progression except Sandbox Lab, where all content is unlocked.", new Vector2(56, 146), new Color(196, 207, 220), 0.72f);

        var modes = PlayableModeCatalog.All;
        _modeFocus = Math.Clamp(_modeFocus, 0, modes.Count - 1);
        var selected = modes[_modeFocus];

        var listPanel = new Rectangle(54, 198, 520, 560);
        DrawPanel(listPanel, new Color(31, 37, 46), border: new Color(81, 96, 116));
        DrawText("Play Modes", new Vector2(listPanel.X + 24, listPanel.Y + 24), Color.White, 0.9f);
        const int visibleModeRows = 6;
        _modeListScroll.Configure(modes.Count, visibleModeRows);
        for (var i = _modeListScroll.Offset; i < Math.Min(modes.Count, _modeListScroll.Offset + visibleModeRows); i++)
        {
            var mode = modes[i];
            var row = i - _modeListScroll.Offset;
            var rect = new Rectangle(listPanel.X + 24, listPanel.Y + 72 + row * 72, listPanel.Width - 64, 58);
            var focused = i == _modeFocus;
            DrawPanel(rect, focused ? new Color(54, 68, 83) : new Color(38, 45, 56), border: focused ? new Color(244, 230, 158) : new Color(76, 90, 110));
            DrawText(mode.Name, new Vector2(rect.X + 16, rect.Y + 8), Color.White, 0.56f);
            DrawText(mode.ProgressionEligible ? "Progression eligible" : "Progression disabled", new Vector2(rect.X + 16, rect.Y + 32), mode.ProgressionEligible ? new Color(148, 224, 164) : new Color(255, 190, 120), 0.4f);
            if (Button(new Rectangle(rect.Right - 92, rect.Y + 12, 74, 34), "View", selected: focused))
            {
                _modeFocus = i;
            }
        }
        DrawUxScrollBar("mode-list", new Rectangle(listPanel.Right - 16, listPanel.Y + 72, 8, 430), _modeListScroll, UiTheme.Focus);

        var detailPanel = new Rectangle(604, 198, 940, 560);
        DrawPanel(detailPanel, new Color(31, 37, 46), border: new Color(81, 96, 116));
        DrawText(selected.Name, new Vector2(detailPanel.X + 30, detailPanel.Y + 28), Color.White, 1.0f);
        DrawText(selected.Description, new Rectangle(detailPanel.X + 30, detailPanel.Y + 76, detailPanel.Width - 60, 68), new Color(205, 214, 225), 0.62f);

        if (selected.Id == DragonCardsModeIds.DragonAvatar)
        {
            DrawDragonAvatarModeDetail(detailPanel);
        }
        else if (selected.Id == DragonCardsModeIds.SealedGauntlet)
        {
            DrawSealedGauntletModeDetail(detailPanel);
        }
        else if (selected.Id == DragonCardsModeIds.StarterClash)
        {
            DrawStarterClashModeDetail(detailPanel);
        }
        else
        {
            DrawText(ModeDataSummary(selected.Id), new Rectangle(detailPanel.X + 30, detailPanel.Y + 164, detailPanel.Width - 60, 130), new Color(244, 230, 158), 0.58f);
        }

        DrawModePlayButtons(detailPanel, selected);

        if (Button(new Rectangle(54, 786, 150, 42), "Back"))
        {
            _screen = Screen.MainMenu;
            _status = "Returned to main menu.";
        }
    }

    private void DrawDragonAvatarModeDetail(Rectangle panel)
    {
        var avatars = DragonAvatarService.PlayableAvatarCandidates(_data);
        _avatarFocus = Math.Clamp(_avatarFocus, 0, Math.Max(0, avatars.Count - 1));
        var avatar = avatars.Count == 0 ? null : avatars[_avatarFocus];
        DrawText("Choose Avatar", new Vector2(panel.X + 30, panel.Y + 162), Color.White, 0.72f);
        if (avatar is not null)
        {
            if (Button(new Rectangle(panel.X + 30, panel.Y + 206, 54, 34), "Prev", avatars.Count > 1))
            {
                _avatarFocus = (_avatarFocus - 1 + avatars.Count) % avatars.Count;
            }

            if (Button(new Rectangle(panel.X + 96, panel.Y + 206, 54, 34), "Next", avatars.Count > 1))
            {
                _avatarFocus = (_avatarFocus + 1) % avatars.Count;
            }

            DrawCardFrame(new Rectangle(panel.X + 30, panel.Y + 258, 168, 236), avatar, selected: true, exhausted: false, count: 1, compact: false);
            DrawText($"{avatar.Name}   {avatar.Rarity}   {string.Join("/", avatar.Elements)}", new Rectangle(panel.X + 230, panel.Y + 208, 620, 36), new Color(244, 230, 158), 0.62f);
            DrawText("Rules: 60-card singleton, avatar element identity, 10 damage limit, replay tax +2 generic per prior command-zone cast. Progression rewards enabled.", new Rectangle(panel.X + 230, panel.Y + 258, 600, 110), new Color(205, 214, 225), 0.58f);
            var sampleDeck = DragonAvatarService.BuildSampleAvatarDeck(_data, avatar.Id);
            var issues = DragonAvatarService.ValidateAvatarDeck(_data, avatar.Id, sampleDeck);
            DrawText(issues.Count == 0 ? "Generated avatar deck is legal." : issues[0].Message, new Rectangle(panel.X + 230, panel.Y + 386, 600, 42), issues.Count == 0 ? new Color(148, 224, 164) : new Color(255, 162, 128), 0.54f);
        }
    }

    private void DrawSealedGauntletModeDetail(Rectangle panel)
    {
        _sealedPool ??= SealedGauntletService.GeneratePool(_data, Environment.TickCount);
        DrawText("Temporary Pool", new Vector2(panel.X + 30, panel.Y + 162), Color.White, 0.72f);
        DrawText($"6 boosters -> {_sealedPool.CardIds.Count} cards. Auto-builds a 40-card sealed deck for this prototype pass.", new Rectangle(panel.X + 30, panel.Y + 204, 760, 40), new Color(244, 230, 158), 0.58f);
        DrawText("No owned cards are consumed. Completed matches earn normal progression rewards while the temporary pool is discarded.", new Rectangle(panel.X + 30, panel.Y + 252, 760, 44), new Color(205, 214, 225), 0.54f);
        if (Button(new Rectangle(panel.X + 30, panel.Y + 320, 168, 38), "Reroll Pool"))
        {
            _sealedPool = SealedGauntletService.GeneratePool(_data, Environment.TickCount);
            _status = "Sealed pool regenerated.";
        }

        foreach (var (cardId, index) in _sealedPool.CardIds.Distinct(StringComparer.OrdinalIgnoreCase).Take(6).Select((id, index) => (id, index)))
        {
            var card = _data.CardsById[cardId];
            DrawCardFrame(new Rectangle(panel.X + 230 + index * 104, panel.Y + 318, 82, 116), card, selected: false, exhausted: false, count: _sealedPool.CardIds.Count(id => id.Equals(cardId, StringComparison.OrdinalIgnoreCase)), compact: true);
        }
    }

    private void DrawStarterClashModeDetail(Rectangle panel)
    {
        var starters = StarterDecks();
        _starterClashOpponentIndex = Math.Clamp(_starterClashOpponentIndex, 0, starters.Count - 1);
        DrawText("Opponent Starter", new Vector2(panel.X + 30, panel.Y + 162), Color.White, 0.72f);
        for (var i = 0; i < starters.Count; i++)
        {
            var rect = new Rectangle(panel.X + 30 + (i % 4) * 160, panel.Y + 206 + (i / 4) * 52, 142, 38);
            if (Button(rect, StarterElement(starters[i]), selected: _starterClashOpponentIndex == i))
            {
                _starterClashOpponentIndex = i;
            }
        }

        DrawText("Starter Clash uses your active deck against a chosen starter opponent. Sandbox rules can preview locked starters.", new Rectangle(panel.X + 30, panel.Y + 332, 720, 60), new Color(205, 214, 225), 0.58f);
    }

    private string ModeDataSummary(string modeId)
    {
        if (!_data.GameModesById.TryGetValue(modeId, out var mode))
        {
            return "Mode opens an existing screen.";
        }

        var category = string.IsNullOrWhiteSpace(mode.Display?.Category) ? "Standard" : mode.Display.Category;
        var feature = string.IsNullOrWhiteSpace(mode.Display?.FeatureText) ? mode.Description : mode.Display.FeatureText;
        return $"{category}  |  {mode.DeckRules.DeckSize} cards  |  Max copies {mode.DeckRules.MaxCopies}  |  Damage limit {mode.DamageLimit}\n{feature}";
    }

    private string ModeName(string modeId) =>
        _data.GameModesById.TryGetValue(modeId, out var mode) && !string.IsNullOrWhiteSpace(mode.Name)
            ? mode.Name
            : PlayableModeCatalog.All.FirstOrDefault(mode => mode.Id.Equals(modeId, StringComparison.OrdinalIgnoreCase))?.Name ?? modeId;

    private void DrawModePlayButtons(Rectangle panel, PlayableModeDefinition selected)
    {
        if (selected.Id == DragonCardsModeIds.TutorialTrials)
        {
            _modeActionFocus = 0;
            if (Button(new Rectangle(panel.X + 30, panel.Bottom - 74, 190, 42), "Open Tutorials", _profile is not null,
                focused: _usingController && _modeActionFocus == 0))
            {
                StartSelectedMode(selected, MatchKind.VsAi);
            }

            return;
        }

        var enabled = CanStartSelectedMode(selected);
        if (Button(new Rectangle(panel.X + 30, panel.Bottom - 74, 150, 42), "Solo AI", enabled,
            focused: _usingController && _modeActionFocus == 0))
        {
            StartSelectedMode(selected, MatchKind.VsAi);
        }

        if (Button(new Rectangle(panel.X + 196, panel.Bottom - 74, 150, 42), "Local Hotseat", enabled,
            focused: _usingController && _modeActionFocus == 1))
        {
            StartSelectedMode(selected, MatchKind.Hotseat);
        }

        if (Button(new Rectangle(panel.X + 362, panel.Bottom - 74, 150, 42), "Host LAN", enabled,
            focused: _usingController && _modeActionFocus == 2))
        {
            HostSelectedMode(selected);
        }

        if (Button(new Rectangle(panel.X + 528, panel.Bottom - 74, 150, 42), "Join LAN",
            focused: _usingController && _modeActionFocus == 3))
        {
            EnsureHostInvite();
            _screen = Screen.Multiplayer;
            _multiplayerSection = MultiplayerSection.JoinLobby;
            _joinInviteEditing = true;
            _status = "Type a host invite to join a direct lobby.";
        }
    }

    private bool CanStartSelectedMode(PlayableModeDefinition mode) =>
        mode.Id == DragonCardsModeIds.TutorialTrials ||
        (_profile is not null &&
            (mode.Id != DragonCardsModeIds.DragonAvatar || DragonAvatarService.PlayableAvatarCandidates(_data).Count > 0));

    private void StartSelectedMode(PlayableModeDefinition mode, MatchKind matchKind)
    {
        if (mode.Id == DragonCardsModeIds.TutorialTrials)
        {
            _screen = Screen.Tutorials;
            _status = "Tutorial Trials opened.";
            return;
        }

        if (_profile is null)
        {
            _status = "Create a player profile first.";
            BeginNewGame();
            return;
        }

        if (!TryCreateDecksForMode(mode.Id, out var playerDeck, out var opponentDeck, out var error))
        {
            _status = error;
            return;
        }

        StartMatch(playerDeck, opponentDeck, matchKind, mode.Id);
        if (mode.Id == DragonCardsModeIds.DragonAvatar && DragonAvatarService.PlayableAvatarCandidates(_data).Count > 0)
        {
            var avatar = DragonAvatarService.PlayableAvatarCandidates(_data)[Math.Clamp(_avatarFocus, 0, DragonAvatarService.PlayableAvatarCandidates(_data).Count - 1)];
            _status = $"Dragon Avatar started with {avatar.Name}.";
        }
    }

    private void HostSelectedMode(PlayableModeDefinition mode)
    {
        if (mode.Id == DragonCardsModeIds.TutorialTrials)
        {
            _screen = Screen.Tutorials;
            _status = "Tutorials are local-only.";
            return;
        }

        if (!TryCreateDecksForMode(mode.Id, out var playerDeck, out _, out var error))
        {
            _status = error;
            return;
        }

        BeginHostDirectMatch(mode.Id, playerDeck);
    }

    private bool TryCreateDecksForMode(string modeId, out DeckDefinition playerDeck, out DeckDefinition opponentDeck, out string error)
    {
        playerDeck = CurrentDeck();
        opponentDeck = OpponentDeck();
        error = "";

        if (_profile is null)
        {
            error = "Create a player profile first.";
            BeginNewGame();
            return false;
        }

        if (modeId == DragonCardsModeIds.DragonDuel)
        {
            return ValidateDecksForMode(modeId, playerDeck, opponentDeck, out error);
        }

        if (modeId == DragonCardsModeIds.StarterClash)
        {
            var starters = StarterDecks();
            if (starters.Count == 0)
            {
                error = "No starter decks are available for Starter Clash.";
                return false;
            }

            _starterClashOpponentIndex = Math.Clamp(_starterClashOpponentIndex, 0, starters.Count - 1);
            opponentDeck = starters[_starterClashOpponentIndex];
            return ValidateDecksForMode(modeId, playerDeck, opponentDeck, out error);
        }

        if (modeId == DragonCardsModeIds.DragonAvatar)
        {
            var avatars = DragonAvatarService.PlayableAvatarCandidates(_data);
            if (avatars.Count == 0)
            {
                error = "No Dragon Avatar has enough identity cards for a legal deck yet.";
                return false;
            }

            var avatar = avatars[Math.Clamp(_avatarFocus, 0, avatars.Count - 1)];
            playerDeck = DragonAvatarService.BuildSampleAvatarDeck(_data, avatar.Id, "-player");
            var opponentAvatar = avatars.FirstOrDefault(card => !card.Elements.SequenceEqual(avatar.Elements)) ?? avatars[^1];
            opponentDeck = DragonAvatarService.BuildSampleAvatarDeck(_data, opponentAvatar.Id, "-ai");
            return ValidateDecksForMode(modeId, playerDeck, opponentDeck, out error);
        }

        if (modeId == DragonCardsModeIds.SealedGauntlet)
        {
            _sealedPool ??= SealedGauntletService.GeneratePool(_data, Environment.TickCount);
            playerDeck = _sealedPool.Deck;
            opponentDeck = SealedGauntletService.GeneratePool(_data, Environment.TickCount + 71).Deck;
            return ValidateDecksForMode(modeId, playerDeck, opponentDeck, out error);
        }

        if (modeId == DragonCardsModeIds.SandboxLab)
        {
            return ValidateDecksForMode(modeId, playerDeck, opponentDeck, out error);
        }

        error = $"Mode '{modeId}' cannot start a match.";
        return false;
    }

    private bool ValidateDecksForMode(
        string modeId,
        DeckDefinition playerDeck,
        DeckDefinition opponentDeck,
        out string error)
    {
        var issues = GameDataValidator.ValidateDeck(playerDeck, _data, modeId)
            .Concat(GameDataValidator.ValidateDeck(opponentDeck, _data, modeId))
            .ToArray();
        error = issues.Length == 0
            ? ""
            : $"Cannot start {ModeName(modeId)}: {issues[0].Message}";
        return issues.Length == 0;
    }

    private bool TryCreateLocalDeckForMode(string modeId, out DeckDefinition deck, out string error)
    {
        if (TryCreateDecksForMode(modeId, out deck, out _, out error))
        {
            return true;
        }

        deck = CurrentDeck();
        return false;
    }

    private void DrawMultiplayerMenu()
    {
        DrawMultiplayerLobbyUi();
    }

    private void DrawTutorials()
    {
        DrawText("Tutorials", new Vector2(54, 108), Color.White, 1.2f);
        DrawText("Guided lessons use prepared matches and award 250 Coins the first time each one is completed.", new Rectangle(56, 146, 980, 36), new Color(196, 207, 220), 0.68f);

        var listPanel = new Rectangle(54, 198, 690, 560);
        DrawPanel(listPanel, new Color(31, 37, 46), border: new Color(81, 96, 116));
        DrawText("Lessons", new Vector2(listPanel.X + 24, listPanel.Y + 24), Color.White, 0.92f);
        _tutorialFocus = Math.Clamp(_tutorialFocus, 0, TutorialDefinitions.Count - 1);
        const int visibleTutorialRows = 6;
        _tutorialListScroll.Configure(TutorialDefinitions.Count, visibleTutorialRows);
        for (var i = _tutorialListScroll.Offset; i < Math.Min(TutorialDefinitions.Count, _tutorialListScroll.Offset + visibleTutorialRows); i++)
        {
            var tutorial = TutorialDefinitions[i];
            var completed = tutorial.IsCompleted(_profile);
            var row = i - _tutorialListScroll.Offset;
            var rect = new Rectangle(listPanel.X + 24, listPanel.Y + 72 + row * 72, listPanel.Width - 64, 58);
            var selected = i == _tutorialFocus;
            DrawPanel(rect, selected ? new Color(54, 68, 83) : new Color(38, 45, 56), border: selected ? new Color(244, 230, 158) : new Color(76, 90, 110));
            DrawText(tutorial.Name, new Vector2(rect.X + 16, rect.Y + 10), Color.White, 0.58f);
            DrawText(completed ? "Completed" : "Reward available", new Vector2(rect.X + 16, rect.Y + 34), completed ? new Color(148, 224, 164) : new Color(244, 230, 158), 0.42f);
            if (Button(new Rectangle(rect.Right - 102, rect.Y + 12, 82, 34), completed ? "Replay" : "Start", _profile is not null, selected: selected))
            {
                _tutorialFocus = i;
                BeginTutorial(tutorial);
            }
        }
        DrawUxScrollBar("tutorial-list", new Rectangle(listPanel.Right - 16, listPanel.Y + 72, 8, 430), _tutorialListScroll, UiTheme.Focus);

        var detail = TutorialDefinitions[_tutorialFocus];
        var detailPanel = new Rectangle(772, 198, 772, 560);
        DrawPanel(detailPanel, new Color(31, 37, 46), border: new Color(81, 96, 116));
        DrawText(detail.Name, new Vector2(detailPanel.X + 28, detailPanel.Y + 28), Color.White, 0.92f);
        DrawText(detail.Description, new Rectangle(detailPanel.X + 28, detailPanel.Y + 74, detailPanel.Width - 56, 90), new Color(205, 214, 225), 0.62f);
        DrawText("Steps", new Vector2(detailPanel.X + 28, detailPanel.Y + 184), Color.White, 0.68f);
        for (var i = 0; i < detail.Steps.Count; i++)
        {
            DrawText($"{i + 1}. {detail.Steps[i].Title}", new Vector2(detailPanel.X + 48, detailPanel.Y + 222 + i * 38), new Color(211, 220, 231), 0.5f);
        }

        var rewardText = _profile is null
            ? "Create a player profile before starting tutorials."
            : detail.IsCompleted(_profile)
                ? "Reward already claimed for this tutorial."
                : $"+{TutorialRewardService.CoinsPerTutorial} Coins available.";
        DrawText(rewardText, new Rectangle(detailPanel.X + 28, detailPanel.Bottom - 130, detailPanel.Width - 56, 38), _profile is null ? new Color(255, 190, 120) : new Color(244, 230, 158), 0.58f);
        if (!string.IsNullOrWhiteSpace(_tutorialNotice))
        {
            DrawText(_tutorialNotice, new Rectangle(detailPanel.X + 28, detailPanel.Bottom - 88, detailPanel.Width - 56, 34), new Color(148, 224, 164), 0.52f);
        }

        if (Button(new Rectangle(54, 786, 150, 42), "Back"))
        {
            _screen = UxBackDestination(Screen.MainMenu);
            _status = "Returned.";
        }
    }

    private void BeginTutorial(TutorialDefinition tutorial)
    {
        if (_profile is null)
        {
            _status = "Create a player before starting tutorials.";
            BeginNewGame();
            return;
        }

        StartMatch(_data.DecksById["starter-fire"], _data.DecksById["starter-ice"], MatchKind.Hotseat);
        _tutorial = new TutorialRuntimeState(tutorial);
        _tutorialNotice = "";
        PrepareTutorialScenario(tutorial.Id);
        ClearPresentation();
        ClearSelections();
        _status = $"Tutorial: {tutorial.Name}";
    }

    private void PrepareTutorialScenario(string tutorialId)
    {
        if (_engine is null)
        {
            return;
        }

        ResetTutorialState();
        var state = _engine.State;
        state.Players[0].Name = _profile?.PlayerName ?? "Player";
        state.Players[1].Name = "Training Drake";
        state.PhaseIndex = Math.Max(0, state.Mode.Phases.FindIndex(phase => phase.Equals("Main", StringComparison.OrdinalIgnoreCase)));

        switch (tutorialId)
        {
            case "first-turn-basics":
                AddHandCard(0, "fire-ember-whelp");
                state.Players[0].EnergyPool["Fire"] = 1;
                _selectedHandIndex = 0;
                _matchFocus = MatchFocus.Hand;
                break;
            case "playing-cards":
                AddHandCard(0, "fire-ember-whelp");
                AddHandCard(0, "fire-spark-offering");
                state.Players[0].EnergyPool["Fire"] = 3;
                _selectedHandIndex = 0;
                _matchFocus = MatchFocus.Hand;
                break;
            case "add-energy":
                AddHandCard(0, "fire-cinder-adept");
                _selectedHandIndex = 0;
                _matchFocus = MatchFocus.Hand;
                break;
            case "sacrifice-energy":
                AddHandCard(0, "fire-cinder-adept");
                _selectedHandIndex = 0;
                _matchFocus = MatchFocus.Hand;
                break;
            case "blocking-attacks":
                var attacker = AddUnit(1, "ice-glacial-wisp");
                AddUnit(0, "fire-cinder-adept");
                attacker.Exhausted = true;
                state.PendingAttack = new PendingAttack(1, attacker.Id);
                _selectedBlockerIndex = 0;
                _matchFocus = MatchFocus.Blockers;
                break;
            case "card-effects":
                AddHandCard(0, "fire-battle-seer");
                AddUnit(1, "ice-crystal-champion");
                state.Players[0].EnergyPool["Fire"] = 2;
                state.Players[0].EnergyPool["Wind"] = 1;
                _selectedHandIndex = 0;
                _matchFocus = MatchFocus.Hand;
                break;
        }
    }

    private void ResetTutorialState()
    {
        if (_engine is null)
        {
            return;
        }

        foreach (var player in _engine.State.Players)
        {
            player.Hand.Clear();
            player.UnitField.Clear();
            player.SupportField.Clear();
            player.DiscardPile.Clear();
            player.DamageZone.Clear();
            player.NextCardCostReduction = 0;
            player.LastPayment.Clear();
            foreach (var element in _engine.State.Mode.Elements)
            {
                player.EnergyPool[element] = 0;
            }
        }

        _engine.State.ActivePlayerIndex = 0;
        _engine.State.PendingAttack = null;
        _engine.State.PendingTargetChoice = null;
        _engine.State.PendingEnergyChoice = null;
        _engine.State.WinnerIndex = null;
        _engine.State.EnergyAddsThisTurn = 0;
        _engine.State.Log.Clear();
        _engine.State.Log.Add("Tutorial started.");
    }

    private CardInstance AddHandCard(int playerIndex, string cardId)
    {
        var player = _engine!.State.Players[playerIndex];
        var instance = new CardInstance(cardId, $"tutorial-p{playerIndex}-hand-{cardId}-{player.Hand.Count}");
        player.Hand.Add(instance);
        return instance;
    }

    private CardInstance AddUnit(int playerIndex, string cardId)
    {
        var player = _engine!.State.Players[playerIndex];
        var instance = new CardInstance(cardId, $"tutorial-p{playerIndex}-unit-{cardId}-{player.UnitField.Count}");
        player.UnitField.Add(instance);
        return instance;
    }

    private void DrawOptionsMenu()
    {
        DrawText("Options", new Vector2(54, 108), Color.White, 1.2f);
        DrawText("Display, audio, and gameplay preferences are saved locally.", new Vector2(56, 146), new Color(196, 207, 220), 0.74f);

        var panel = new Rectangle(54, 198, 1070, 560);
        DrawPanel(panel, new Color(31, 37, 46), border: new Color(81, 96, 116));
        DrawText("Preferences", new Vector2(panel.X + 34, panel.Y + 18), Color.White, 0.82f);

        var listViewport = new Rectangle(panel.X + 34, panel.Y + 50, 690, 414);
        var list = ListViewLayout.Create(listViewport, 9, 54, 8, _optionsListScroll);
        if (_optionsFocusVisibilityPending && _optionsFocus <= 7)
        {
            _optionsListScroll.EnsureVisible(OptionsLogicalRow(_optionsFocus));
            _optionsFocusVisibilityPending = false;
            list = ListViewLayout.Create(listViewport, 9, 54, 8, _optionsListScroll);
        }

        foreach (var row in Enumerable.Range(list.VisibleItems.Start, list.VisibleItems.Count))
        {
            var bounds = list.ItemBounds(row);
            switch (row)
            {
                case 0:
                    DrawToggleRow(0, bounds, "Display - Fullscreen", _graphics.IsFullScreen ? "On" : "Off", ToggleFullscreen);
                    break;
                case 1:
                    DrawChoiceRow(1, bounds, "Display - Window Size", $"{_settings.WindowWidth} x {_settings.WindowHeight}", () => CycleWindowSize(-1), () => CycleWindowSize(1));
                    break;
                case 2:
                    DrawStaticRow(bounds, "Display - UI Scale", "Aspect fit to window");
                    break;
                case 3:
                    DrawChoiceRow(2, bounds, "Audio - Music Volume", $"{_settings.MusicVolume}%", () => AdjustMusicVolume(-10), () => AdjustMusicVolume(10));
                    break;
                case 4:
                    DrawChoiceRow(3, bounds, "Audio - Sound Volume", $"{_settings.SoundVolume}%", () => AdjustSoundVolume(-10), () => AdjustSoundVolume(10));
                    break;
                case 5:
                    DrawToggleRow(4, bounds, "Audio - Mute", _settings.MuteAudio ? "On" : "Off", ToggleMuteAudio);
                    break;
                case 6:
                    DrawToggleRow(5, bounds, "Gameplay - Card Hover Zoom", _settings.CardZoom ? "On" : "Off", ToggleCardZoom);
                    break;
                case 7:
                    DrawChoiceRow(6, bounds, "Gameplay - Interaction Pace", InteractionSpeedLabel(), () => CycleInteractionSpeed(-1), () => CycleInteractionSpeed(1));
                    break;
                case 8:
                    DrawToggleRow(7, bounds, "Accessibility - Reduced Motion", _settings.ReducedMotion ? "On" : "Off", ToggleReducedMotion);
                    break;
            }
        }

        DrawUxScrollBar("options-list", new Rectangle(listViewport.Right + 12, listViewport.Y, 8, listViewport.Height), _optionsListScroll, UiTheme.Focus);
        DrawText("Wheel or Page Up/Down to scroll. Moving focus keeps the active setting visible.",
            new Rectangle(listViewport.X, listViewport.Bottom + 18, listViewport.Width, 48), new Color(171, 186, 204), 0.55f);

        DrawPanel(new Rectangle(818, 274, 250, 276), new Color(34, 41, 51), border: new Color(84, 99, 119));
        DrawText("Audio Status", new Vector2(846, 304), Color.White, 0.82f);
        DrawText($"SFX {_audio.LoadedSoundCount}/{_audio.ExpectedSoundCount}", new Rectangle(846, 340, 184, 24), new Color(196, 207, 220), 0.56f);
        DrawText(_audio.MusicStatus, new Rectangle(846, 370, 184, 42), new Color(196, 207, 220), 0.56f);
        if (Button(new Rectangle(846, 424, 160, 34), "Test Music", _audio.MusicLoaded, focused: _usingController && _optionsFocus == 8))
        {
            _audio.RestartMusic();
            _status = "BGM restarted.";
        }

        if (Button(new Rectangle(846, 468, 160, 34), "Test Sound", focused: _usingController && _optionsFocus == 9))
        {
            _audio.Play(SoundKeys.RarePull, throttleSeconds: 0);
            _status = "Sound test played.";
        }

        DrawPanel(new Rectangle(818, 574, 250, 132), new Color(34, 41, 51), border: new Color(84, 99, 119));
        DrawText("Settings File", new Vector2(846, 604), Color.White, 0.76f);
        DrawText(ShortPath(GameSettings.SettingsFilePath), new Rectangle(846, 638, 184, 46), new Color(196, 207, 220), 0.5f);

        if (Button(new Rectangle(54, 786, 150, 42), "Back", focused: _usingController && _optionsFocus == 10))
        {
            _screen = UxBackDestination(Screen.MainMenu);
            _status = "Options saved.";
        }
    }

    private static int OptionsLogicalRow(int focusIndex) => focusIndex switch
    {
        <= 1 => Math.Max(0, focusIndex),
        2 => 3,
        3 => 4,
        4 => 5,
        5 => 6,
        6 => 7,
        _ => 8
    };

    private static int OptionsFocusForLogicalRow(int logicalRow) => logicalRow switch
    {
        <= 0 => 0,
        1 => 1,
        <= 3 => 2,
        4 => 3,
        5 => 4,
        6 => 5,
        7 => 6,
        _ => 7
    };

    private void DrawStaticRow(Rectangle rect, string label, string value)
    {
        Fill(rect, new Color(38, 45, 56));
        Border(rect, new Color(76, 90, 110), 1);
        DrawText(label, new Vector2(rect.X + 18, rect.Y + 11), new Color(211, 220, 231), 0.64f);
        DrawText(value, new Rectangle(rect.Right - 264, rect.Y + 10, 238, 24), new Color(196, 207, 220), 0.6f);
    }

    private void DrawToggleRow(int focusIndex, Rectangle rect, string label, string value, Action toggle)
    {
        var focused = _usingController && _screen == Screen.Options && _optionsFocus == focusIndex;
        Fill(rect, focused ? new Color(48, 61, 75) : new Color(38, 45, 56));
        Border(rect, focused ? new Color(244, 230, 158) : new Color(76, 90, 110), focused ? 2 : 1);
        DrawText(label, new Vector2(rect.X + 18, rect.Y + 11), new Color(211, 220, 231), 0.64f);
        if (Button(new Rectangle(rect.Right - 124, rect.Y + 6, 96, 30), value, focused: focused))
        {
            toggle();
        }
    }

    private void DrawChoiceRow(int focusIndex, Rectangle rect, string label, string value, Action previous, Action next)
    {
        var focused = _usingController && _screen == Screen.Options && _optionsFocus == focusIndex;
        Fill(rect, focused ? new Color(48, 61, 75) : new Color(38, 45, 56));
        Border(rect, focused ? new Color(244, 230, 158) : new Color(76, 90, 110), focused ? 2 : 1);
        DrawText(label, new Vector2(rect.X + 18, rect.Y + 11), new Color(211, 220, 231), 0.64f);
        DrawText(value, new Rectangle(rect.Right - 284, rect.Y + 10, 170, 24), Color.White, 0.6f);
        if (Button(new Rectangle(rect.Right - 104, rect.Y + 6, 36, 30), "-", focused: focused))
        {
            previous();
        }

        if (Button(new Rectangle(rect.Right - 58, rect.Y + 6, 36, 30), "+", focused: focused))
        {
            next();
        }
    }

    private void DrawDeckSummary(Rectangle rect, DeckDefinition deck, IReadOnlyList<ValidationIssue> issues)
    {
        var avatar = DeckAvatarCard(deck);
        var accent = avatar?.Elements.FirstOrDefault() ?? "Water";
        var accentColor = ElementColor(accent);
        DrawPanel(rect, new Color(9, 25, 50, 232), border: Color.Lerp(accentColor, new Color(128, 214, 255), 0.45f));
        Fill(new Rectangle(rect.X, rect.Y, 8, rect.Height), Color.Lerp(accentColor, Color.Black, 0.12f));
        DrawText(deck.Name, new Rectangle(rect.X + 28, rect.Y + 22, rect.Width - 250, 30), Color.White, 0.78f);
        DrawText($"{deck.Count} cards  |  {string.Join(" / ", DeckElements(deck))}", new Rectangle(rect.X + 28, rect.Y + 60, rect.Width - 250, 24), issues.Count == 0 ? new Color(148, 224, 164) : new Color(255, 162, 128), 0.52f);
        DrawText(issues.Count == 0 ? "Ready for the standard Dragon Duel." : issues[0].Message, new Rectangle(rect.X + 28, rect.Y + 94, rect.Width - 250, 44), new Color(204, 220, 236), 0.5f);

        if (avatar is null)
        {
            DrawText("No playable avatar card found in this deck.", new Rectangle(rect.X + 28, rect.Y + 148, rect.Width - 250, 36), new Color(255, 190, 120), 0.48f);
            return;
        }

        DrawText("DECK AVATAR", new Vector2(rect.X + 28, rect.Y + 154), new Color(112, 201, 255), 0.46f);
        DrawText(avatar.Name, new Rectangle(rect.X + 28, rect.Y + 178, rect.Width - 250, 32), new Color(244, 230, 158), 0.6f);
        DrawCardFrame(new Rectangle(rect.Right - 190, rect.Y + 18, 144, 200), avatar, selected: false, exhausted: false, count: deck.Cards.GetValueOrDefault(avatar.Id), compact: false);
    }

    private CardDefinition? DeckAvatarCard(DeckDefinition deck) =>
        deck.Cards
            .Where(entry => entry.Value > 0 && _data.CardsById.TryGetValue(entry.Key, out _))
            .Select(entry => new { Card = _data.CardsById[entry.Key], entry.Value })
            .Where(entry => !BasicEnergy.IsBasicEnergyCard(entry.Card) && !EnergySource.IsEnergySourceToken(entry.Card))
            .OrderByDescending(entry => entry.Card.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(entry => entry.Card.TotalCost)
            .ThenByDescending(entry => entry.Card.Power)
            .ThenByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Card.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Card)
            .FirstOrDefault();

    private IReadOnlyList<string> DeckElements(DeckDefinition deck) =>
        deck.Cards.Keys
            .Where(cardId => _data.CardsById.TryGetValue(cardId, out _))
            .SelectMany(cardId => _data.CardsById[cardId].Elements)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .DefaultIfEmpty("Mixed")
            .ToArray();

    private void DrawModePips(Rectangle rect)
    {
        var mode = _data.GameModesById["dragon-duel"];
        for (var i = 0; i < mode.Elements.Count; i++)
        {
            var element = mode.Elements[i];
            var column = i % 4;
            var row = i / 4;
            var swatch = new Rectangle(rect.X + column * 126, rect.Y + row * 30, 112, 24);
            Fill(swatch, Color.Lerp(ElementColor(element), Color.Black, 0.16f));
            Border(swatch, new Color(16, 18, 23), 2);
            var labelPosition = new Vector2(swatch.X + 10, swatch.Y + 5);
            DrawText(element, labelPosition + new Vector2(1, 1), new Color(0, 0, 0, 180), 0.34f);
            DrawText(element, labelPosition, Color.White, 0.34f);
        }
    }

    private void DrawDeckBuilder()
    {
        DrawText("Deck Builder", new Vector2(42, 98), Color.White, 1.16f);
        DrawText($"{_deckBuilder.DeckName}  -  {DeckBuilderModeLabel()}", new Vector2(42, 134), new Color(200, 211, 224), 0.76f);

        DrawFilterBar();

        var cards = _deckBuilder.FilteredCards;
        var pageCount = Math.Max(1, (int)Math.Ceiling(cards.Count / (double)DeckBuilderState.PageSize));
        _deckBuilder.Page = Math.Clamp(_deckBuilder.Page, 0, pageCount - 1);
        _deckFocusIndex = Math.Clamp(_deckFocusIndex, 0, Math.Max(0, cards.Skip(_deckBuilder.Page * DeckBuilderState.PageSize).Take(DeckBuilderState.PageSize).Count() - 1));

        DrawText($"Page {_deckBuilder.Page + 1}/{pageCount}", new Vector2(42, 206), new Color(199, 209, 222), 0.68f);
        if (Button(new Rectangle(176, 196, 86, 34), "Prev", _deckBuilder.Page > 0))
        {
            _deckBuilder.Page--;
            _deckFocusIndex = 0;
        }

        if (Button(new Rectangle(274, 196, 86, 34), "Next", _deckBuilder.Page < pageCount - 1))
        {
            _deckBuilder.Page++;
            _deckFocusIndex = 0;
        }

        var libraryArea = new Rectangle(42, 246, 920, 548);
        DrawPanel(libraryArea, new Color(29, 35, 44), border: new Color(72, 86, 106));
        var visibleCards = cards.Skip(_deckBuilder.Page * DeckBuilderState.PageSize).Take(DeckBuilderState.PageSize).ToArray();
        for (var i = 0; i < visibleCards.Length; i++)
        {
            var column = i % 6;
            var row = i / 6;
            var rect = new Rectangle(libraryArea.X + 22 + column * 146, libraryArea.Y + 20 + row * 250, 122, 178);
            var card = visibleCards[i];
            var selected = _deckBuilder.SelectedCard?.Id == card.Id || (_usingController && _deckFocusIndex == i);
            if (CardButton(rect, card, selected, _deckBuilder.CardCount(card.Id), compact: false))
            {
                _deckBuilder.SelectedCardId = card.Id;
                _deckFocusIndex = i;
                _cardDetailScrollOffset = 0;
            }
            DrawOwnedCardCount(card, rect);
        }

        DrawDeckSidebar(new Rectangle(1000, 96, 544, 698));
    }

    private void DrawFilterBar()
    {
        var x = 42;
        for (var index = 0; index < _deckBuilder.ElementFilters.Count; index++)
        {
            var element = _deckBuilder.ElementFilters[index];
            if (Button(new Rectangle(x, 164, 94, 30), element,
                selected: _deckBuilder.ElementFilter.Equals(element, StringComparison.OrdinalIgnoreCase),
                focused: _usingController && _deckFocusArea == DeckFocusArea.ElementFilters && _deckControlFocus == index))
            {
                ApplyDeckElementFilter(element);
            }

            x += 102;
        }

        var collectionFilters = new[]
        {
            $"Owned: {_deckBuilder.OwnershipFilter}",
            $"Rarity: {_deckBuilder.RarityFilter}",
            $"Set: {(_deckBuilder.SetFilter == "All" ? "All" : BoosterService.SetDisplayName(_deckBuilder.SetFilter))}",
            $"Sort: {DeckSortLabel(_deckBuilder.SortMode)}"
        };
        var collectionWidths = new[] { 152, 160, 226, 146 };
        x = 42;
        for (var index = 0; index < collectionFilters.Length; index++)
        {
            if (Button(new Rectangle(x, 198, collectionWidths[index], 28), collectionFilters[index],
                focused: _usingController && _deckFocusArea == DeckFocusArea.CollectionFilters && _deckControlFocus == index))
            {
                CycleDeckCollectionFilter(index);
            }

            x += collectionWidths[index] + 10;
        }

        x = 42;
        for (var index = 0; index < _deckBuilder.TypeFilters.Count; index++)
        {
            var type = _deckBuilder.TypeFilters[index];
            if (Button(new Rectangle(x, 806, 100, 32), type,
                selected: _deckBuilder.TypeFilter.Equals(type, StringComparison.OrdinalIgnoreCase),
                focused: _usingController && _deckFocusArea == DeckFocusArea.TypeFilters && _deckControlFocus == index))
            {
                ApplyDeckTypeFilter(type);
            }

            x += 110;
        }
    }

    private static string DeckSortLabel(CollectionSortMode sortMode) => sortMode switch
    {
        CollectionSortMode.OwnedCopies => "Owned",
        _ => sortMode.ToString()
    };

    private void CycleDeckCollectionFilter(int filter)
    {
        switch (filter)
        {
            case 0:
                _deckBuilder.OwnershipFilter = (CollectionOwnershipFilter)(((int)_deckBuilder.OwnershipFilter + 1) % Enum.GetValues<CollectionOwnershipFilter>().Length);
                break;
            case 1:
                _deckBuilder.RarityFilter = NextFilterValue(_deckBuilder.RarityFilters, _deckBuilder.RarityFilter);
                break;
            case 2:
                _deckBuilder.SetFilter = NextFilterValue(_deckBuilder.SetFilters, _deckBuilder.SetFilter);
                break;
            case 3:
                _deckBuilder.SortMode = (CollectionSortMode)(((int)_deckBuilder.SortMode + 1) % Enum.GetValues<CollectionSortMode>().Length);
                break;
        }

        ResetDeckFilterPosition();
    }

    private static string NextFilterValue(IReadOnlyList<string> values, string current)
    {
        var index = values.ToList().FindIndex(value => value.Equals(current, StringComparison.OrdinalIgnoreCase));
        return values[(Math.Max(0, index) + 1) % values.Count];
    }

    private void DrawDeckSidebar(Rectangle panel)
    {
        DrawPanel(panel, new Color(34, 41, 51), border: new Color(84, 99, 119));
        var deck = _deckBuilder.CreateDeck();
        var issues = GameDataValidator.ValidateDeck(deck, _data);
        var ownershipIssues = ValidateCurrentDeckOwnership(deck);
        var selectedCard = _deckBuilder.SelectedCard;
        var mode = _data.GameModesById["dragon-duel"];

        DrawText("Selected Card", new Vector2(panel.X + 28, panel.Y + 24), Color.White, 0.94f);
        if (selectedCard is not null)
        {
            DrawCardFrame(new Rectangle(panel.X + 30, panel.Y + 70, 194, 272), selectedCard, selected: true, exhausted: false, count: _deckBuilder.CardCount(selectedCard.Id), compact: false);
            var detailRect = new Rectangle(panel.X + 248, panel.Y + 70, 260, 246);
            DrawScrollableText(CardDetailText(selectedCard), detailRect, ref _cardDetailScrollOffset, new Color(211, 220, 231), CardDetailTextScale);
            if (_usingController && _deckFocusArea == DeckFocusArea.CardDetail)
            {
                Border(detailRect, UiTheme.Focus, 3);
            }
            DrawText(OwnedSummary(selectedCard), new Rectangle(panel.X + 248, panel.Y + 324, 260, 24), new Color(148, 224, 164), 0.58f);

            if (Button(new Rectangle(panel.X + 248, panel.Y + 358, 104, 40), "Add", CanAddCardToDeck(selectedCard, deck),
                focused: _usingController && _deckFocusArea == DeckFocusArea.CardActions && _deckControlFocus == 0))
            {
                _deckBuilder.Add(selectedCard.Id);
                _status = $"Added {selectedCard.Name}.";
            }

            if (Button(new Rectangle(panel.X + 364, panel.Y + 358, 104, 40), "Remove", _deckBuilder.CardCount(selectedCard.Id) > 0,
                focused: _usingController && _deckFocusArea == DeckFocusArea.CardActions && _deckControlFocus == 1))
            {
                _deckBuilder.Remove(selectedCard.Id);
                _status = $"Removed {selectedCard.Name}.";
            }
        }

        Fill(new Rectangle(panel.X + 28, panel.Y + 414, panel.Width - 56, 1), new Color(86, 100, 120));
        DrawDeckAssistantPanel(new Rectangle(panel.X + 28, panel.Y + 434, panel.Width - 56, 204), deck, issues, ownershipIssues);

        if (Button(new Rectangle(panel.X + 28, panel.Bottom - 76, 86, 42), "Save",
            focused: _usingController && _deckFocusArea == DeckFocusArea.Footer && _deckControlFocus == 0))
        {
            SaveDeck(deck);
        }

        if (Button(new Rectangle(panel.X + 122, panel.Bottom - 76, 90, 42), "Library",
            focused: _usingController && _deckFocusArea == DeckFocusArea.Footer && _deckControlFocus == 1))
        {
            _deckLibraryOpen = true;
            _deckLibraryDeleteConfirmation = false;
            _deckNameEditing = false;
            _deckLibraryFocus = Math.Max(0, DeckLibraryEntries().ToList().FindIndex(entry => entry.Deck.Id.Equals(_deckBuilder.DeckId, StringComparison.OrdinalIgnoreCase)));
            _status = "Deck library opened.";
        }

        if (Button(new Rectangle(panel.X + 220, panel.Bottom - 76, 88, 42), "Export",
            focused: _usingController && _deckFocusArea == DeckFocusArea.Footer && _deckControlFocus == 2))
        {
            ExportCurrentDeckCode(deck);
        }

        if (Button(new Rectangle(panel.X + 316, panel.Bottom - 76, 88, 42), "Import",
            focused: _usingController && _deckFocusArea == DeckFocusArea.Footer && _deckControlFocus == 3))
        {
            ImportDeckCodeFromClipboard();
        }

        if (Button(new Rectangle(panel.X + 412, panel.Bottom - 76, 76, 42), "Back",
            focused: _usingController && _deckFocusArea == DeckFocusArea.Footer && _deckControlFocus == 4))
        {
            _screen = UxBackDestination(Screen.MainMenu);
            _status = "Returned.";
        }
    }

    private void DrawDeckAssistantPanel(Rectangle rect, DeckDefinition deck, IReadOnlyList<ValidationIssue> deckIssues, IReadOnlyList<ValidationIssue> ownershipIssues)
    {
        var rules = CurrentRules();
        var analysis = DeckBuilderAssistantService.AnalyzeDeck(_data, deck, _profile, rules, _deckAssistantGoal);
        DrawText("Deck Assistant", new Vector2(rect.X, rect.Y), Color.White, 0.72f);
        DrawText(rules.UnlimitedDeckBuilder || rules.AllUnlocks ? "Sandbox suggestions" : "Owned-card suggestions", new Vector2(rect.Right - 170, rect.Y + 3), rules.IsSandbox ? new Color(255, 190, 120) : new Color(148, 224, 164), 0.42f);

        var goals = Enum.GetValues<DeckAssistantGoal>();
        var goalWidth = 82;
        for (var i = 0; i < goals.Length; i++)
        {
            var goal = goals[i];
            if (Button(new Rectangle(rect.X + i * (goalWidth + 8), rect.Y + 30, goalWidth, 28), goal.ToString(),
                selected: _deckAssistantGoal == goal,
                focused: _usingController && _deckFocusArea == DeckFocusArea.AssistantGoals && _deckControlFocus == i))
            {
                _deckAssistantGoal = goal;
                _deckAssistantSuggestions = [];
                _status = $"Assistant goal set to {goal}.";
            }
        }

        var healthY = rect.Y + 72;
        var valid = deckIssues.Count == 0 && ownershipIssues.Count == 0;
        DrawText($"{analysis.DeckCount}/{analysis.RequiredDeckCount}", new Vector2(rect.X, healthY), valid ? new Color(148, 224, 164) : new Color(255, 162, 128), 0.68f);
        DrawText($"Units {analysis.Roles.Units}  Support {analysis.Roles.Supports}  Spells {analysis.Roles.Spells}", new Vector2(rect.X + 96, healthY), new Color(205, 214, 225), 0.5f);
        DrawText($"Draw {analysis.Roles.Draw}  Interact {analysis.Roles.Removal}  Ramp {analysis.Roles.Ramp}  Avg {analysis.Roles.AverageCost:0.0}", new Vector2(rect.X + 96, healthY + 24), new Color(205, 214, 225), 0.46f);

        var note = analysis.Notes.FirstOrDefault() ?? "Deck looks ready.";
        DrawText(note, new Rectangle(rect.X, healthY + 48, rect.Width, 24), valid ? new Color(196, 207, 220) : new Color(255, 190, 120), 0.5f);

        var buttonY = rect.Y + 132;
        if (Button(new Rectangle(rect.X, buttonY, 92, 30), "Suggest Adds",
            focused: _usingController && _deckFocusArea == DeckFocusArea.AssistantActions && _deckControlFocus == 0))
        {
            _deckAssistantSuggestions = DeckBuilderAssistantService.SuggestAdds(_data, deck, _profile, rules, _deckAssistantGoal, 4);
            _status = _deckAssistantSuggestions.Count == 0 ? "No legal add suggestions found." : "Assistant add suggestions ready.";
        }

        if (Button(new Rectangle(rect.X + 100, buttonY, 92, 30), "Suggest Cuts",
            focused: _usingController && _deckFocusArea == DeckFocusArea.AssistantActions && _deckControlFocus == 1))
        {
            _deckAssistantSuggestions = DeckBuilderAssistantService.SuggestCuts(_data, deck, _profile, rules, _deckAssistantGoal, 4);
            _status = _deckAssistantSuggestions.Count == 0 ? "No cut suggestions found." : "Assistant cut suggestions ready.";
        }

        if (Button(new Rectangle(rect.X + 200, buttonY, 92, 30), "Auto-Fill",
            focused: _usingController && _deckFocusArea == DeckFocusArea.AssistantActions && _deckControlFocus == 2))
        {
            ApplyAssistantDeck(DeckBuilderAssistantService.AutoFill(_data, deck, _profile, rules, _deckAssistantGoal), "Assistant auto-filled the deck.");
        }

        if (Button(new Rectangle(rect.X + 300, buttonY, 92, 30), "Complete",
            focused: _usingController && _deckFocusArea == DeckFocusArea.AssistantActions && _deckControlFocus == 3))
        {
            ApplyAssistantDeck(DeckBuilderAssistantService.AutoFill(_data, deck, _profile, rules, _deckAssistantGoal), "Assistant completed the deck as far as possible.");
        }

        if (Button(new Rectangle(rect.X + 400, buttonY, 66, 30), "Clear", _deckAssistantSuggestions.Count > 0,
            focused: _usingController && _deckFocusArea == DeckFocusArea.AssistantActions && _deckControlFocus == 4))
        {
            _deckAssistantSuggestions = [];
            _status = "Assistant suggestions cleared.";
        }

        DrawAssistantSuggestions(new Rectangle(rect.X, rect.Y + 168, rect.Width, 34));
    }

    private void DrawAssistantSuggestions(Rectangle rect)
    {
        if (_deckAssistantSuggestions.Count == 0)
        {
            DrawText("Tip: choose a goal, then ask for adds or cuts.", rect, new Color(165, 176, 190), 0.42f);
            return;
        }

        var x = rect.X;
        foreach (var (suggestion, suggestionIndex) in _deckAssistantSuggestions.Take(3).Select((item, index) => (item, index)))
        {
            var label = suggestion.Kind == DeckSuggestionKind.Add ? "+" : "-";
            var width = Math.Min(154, Math.Max(112, rect.Right - x));
            var itemRect = new Rectangle(x, rect.Y, width, rect.Height);
            Fill(itemRect, suggestion.Kind == DeckSuggestionKind.Add ? new Color(35, 58, 48) : new Color(63, 45, 43));
            Border(itemRect, suggestion.Kind == DeckSuggestionKind.Add ? new Color(148, 224, 164) : new Color(255, 162, 128), 1);
            DrawFittedText($"{label} {suggestion.CardName}", new Vector2(itemRect.X + 8, itemRect.Y + 7), itemRect.Width - 16, Color.White, 0.38f, 0.24f);
            if (Button(new Rectangle(itemRect.Right - 30, itemRect.Y + 4, 24, 24), "?",
                focused: _usingController && _deckFocusArea == DeckFocusArea.Suggestions && _deckControlFocus == suggestionIndex))
            {
                _deckBuilder.SelectedCardId = suggestion.CardId;
                _cardDetailScrollOffset = 0;
                _status = suggestion.Reason;
            }

            x += width + 8;
            if (x >= rect.Right)
            {
                break;
            }
        }
    }

    private void ApplyAssistantDeck(DeckDefinition deck, string status)
    {
        _deckBuilder.ReplaceWith(deck);
        _deckAssistantSuggestions = [];
        _status = status;
    }

    private void DrawMatch()
    {
        if (_engine is null)
        {
            _screen = Screen.MainMenu;
            return;
        }

        var state = _engine.State;
        var selectingBlocker = state.PendingAttack is not null && state.WinnerIndex is null;
        if (selectingBlocker && CanHumanResolveBlock(state))
        {
            EnsureSelectedBlocker(state);
        }

        var winnerText = state.WinnerIndex is null ? "" : $"{state.Players[state.WinnerIndex.Value].Name} wins";
        var bottomPlayerIndex = LocalBoardPlayerIndex(state);
        var topPlayerIndex = 1 - bottomPlayerIndex;
        var bottom = state.Players[bottomPlayerIndex];
        var top = state.Players[topPlayerIndex];
        var bottomIsActive = state.ActivePlayerIndex == bottomPlayerIndex;
        var topIsActive = state.ActivePlayerIndex == topPlayerIndex;
        var humanCanAct = CanHumanUseActions(state);
        var blockingPlayerIndex = BlockingPlayerIndex(state);
        var bottomCanSelectUnits = selectingBlocker
            ? blockingPlayerIndex == bottomPlayerIndex && CanHumanResolveBlock(state)
            : bottomIsActive && humanCanAct;
        var topCanSelectUnits = selectingBlocker
            ? blockingPlayerIndex == topPlayerIndex && CanHumanResolveBlock(state)
            : topIsActive && humanCanAct && _matchKind == MatchKind.Hotseat;

        DrawPanel(new Rectangle(34, 92, 1212, 68), new Color(31, 37, 46), border: new Color(81, 96, 116));
        DrawText($"Turn {state.TurnNumber}", new Vector2(62, 116), Color.White, 0.8f);
        DrawText(state.ActivePlayer.Name, new Vector2(172, 116), new Color(211, 220, 231), 0.8f);
        DrawPhaseTrack(new Rectangle(344, 108, 574, 34), state);
        DrawText(MatchPrompt(), new Rectangle(940, 112, 286, 34), new Color(244, 230, 158), 0.58f);
        DrawMatchTimeline(new Rectangle(940, 134, 286, 22));
        if (!string.IsNullOrWhiteSpace(winnerText))
        {
            DrawText(winnerText, new Vector2(980, 116), new Color(148, 224, 164), 0.76f);
        }

        DrawPlayerBoard(top, new Rectangle(34, 180, 1212, 238), topIsActive, topCanSelectUnits, selectingBlocker && blockingPlayerIndex == topPlayerIndex);
        DrawPlayerBoard(bottom, ActiveBoardRect(), bottomIsActive, bottomCanSelectUnits, selectingBlocker && blockingPlayerIndex == bottomPlayerIndex);
        DrawHand(bottom, HandAreaRect());
        DrawActionPanel(RightRailRect());
    }

    private void DrawPhaseTrack(Rectangle rect, MatchState state)
    {
        var phases = state.Mode.Phases;
        var segmentWidth = rect.Width / phases.Count;
        for (var i = 0; i < phases.Count; i++)
        {
            var segment = new Rectangle(rect.X + i * segmentWidth, rect.Y, segmentWidth - 4, rect.Height);
            var active = i == state.PhaseIndex;
            Fill(segment, active ? new Color(91, 117, 143) : new Color(45, 53, 65));
            Border(segment, active ? Color.White : new Color(82, 94, 112), active ? 2 : 1);
            DrawText(phases[i], new Vector2(segment.X + 8, segment.Y + 9), active ? Color.White : new Color(190, 199, 211), 0.48f);
        }
    }

    private string MatchPrompt()
    {
        if (_engine is null)
        {
            return "";
        }

        var state = _engine.State;
        if (state.WinnerIndex is not null)
        {
            return $"{state.Players[state.WinnerIndex.Value].Name} wins";
        }

        if (state.PendingCombatAction is not null)
        {
            return $"{state.Players[state.PendingCombatAction.PriorityPlayerIndex].Name}: use ability or pass";
        }

        if (state.PendingAttack is not null)
        {
            var attacker = state.Players[state.PendingAttack.AttackerPlayerIndex].UnitField
                .FirstOrDefault(card => card.Id == state.PendingAttack.AttackerInstanceId);
            var attackerName = attacker is null ? "a unit" : state.CardName(attacker);
            return state.PendingAttack.AttackerPlayerIndex != LocalPlayerIndexForMatch() && _matchKind != MatchKind.Hotseat
                ? $"Incoming: {attackerName}"
                : $"Attack with {attackerName}: choose blocker";
        }

        if (_matchKind != MatchKind.Hotseat && state.ActivePlayerIndex != LocalPlayerIndexForMatch())
        {
            return _matchKind == MatchKind.VsAi ? "AI is playing" : "Remote player is playing";
        }

        return state.CurrentPhase.Equals("Main", StringComparison.OrdinalIgnoreCase)
            ? $"{state.ActivePlayer.Name}: play, energy, or sacrifice"
            : $"{state.ActivePlayer.Name}: {state.CurrentPhase}";
    }

    private void DrawMatchLog(Rectangle rect)
    {
        if (_engine is null)
        {
            return;
        }

        DrawPanel(rect, new Color(26, 32, 41, 220), border: new Color(75, 90, 110));
        DrawText("Log", new Vector2(rect.X + 12, rect.Y + 10), Color.White, 0.46f);
        if (Button(new Rectangle(rect.Right - 76, rect.Y + 5, 64, 22), "History"))
        {
            _matchHistoryOpen = true;
            _matchHistoryScrollOffset = 0;
        }

        var content = new Rectangle(rect.X + 12, rect.Y + 30, rect.Width - 30, rect.Height - 40);
        var logText = string.Join('\n', _engine.State.Log);
        var lines = _textLayoutCache?.Wrap(logText, content.Width, 0.34f).ToArray() ?? WrappedLines(_engine.State.Log, content.Width, 0.34f).ToArray();
        var lineHeight = Math.Max(10, (int)MathF.Ceiling(_font!.LineSpacing * 0.34f));
        var visibleCount = Math.Max(1, content.Height / lineHeight);
        var wasAtEnd = _compactLogScroll.Offset >= _compactLogScroll.MaxOffset;
        _compactLogScroll.Configure(lines.Length, visibleCount);
        if (lines.Length != _compactLogLineCount && (_compactLogLineCount == 0 || wasAtEnd))
        {
            _compactLogScroll.MoveToEnd();
        }
        _compactLogLineCount = lines.Length;
        var start = _compactLogScroll.Offset;
        var y = content.Y;
        for (var i = start; i < Math.Min(lines.Length, start + visibleCount); i++)
        {
            DrawFittedText(lines[i], new Vector2(content.X, y), content.Width, new Color(196, 207, 220), 0.34f, 0.24f);
            y += lineHeight;
        }

        DrawUxScrollBar("compact-log", new Rectangle(rect.Right - 10, content.Y, 6, content.Height), _compactLogScroll, UiTheme.Focus);
    }

    private void DrawPlayerBoard(PlayerState player, Rectangle area, bool isActive, bool selectableUnits, bool blockerSelection)
    {
        DrawPanel(area, isActive ? new Color(35, 46, 52) : new Color(32, 37, 47), border: new Color(82, 98, 118));
        DrawFittedText(player.Name, new Vector2(area.X + 18, area.Y + 16), 220, Color.White, 0.82f, 0.48f);
        DrawZoneStats(player, new Rectangle(area.Right - 188, area.Y + 18, 160, 48));

        var contentTop = area.Y + 76;
        var contentHeight = area.Bottom - contentTop - 18;
        DrawEnergyPool(player, new Rectangle(area.X + 18, contentTop, 174, contentHeight), isActive);
        DrawCardZone("Support", player.SupportField, new Rectangle(area.X + 212, contentTop, 408, contentHeight), compact: true, selectable: isActive && selectableUnits && !blockerSelection, blockerSelection: false, supportSelection: true);
        DrawCardZone("Units", player.UnitField, new Rectangle(area.X + 640, contentTop, 528, contentHeight), compact: true, selectable: selectableUnits, blockerSelection: blockerSelection, supportSelection: false);
    }

    private void DrawEnergyPool(PlayerState player, Rectangle rect, bool isActive)
    {
        if (_engine!.State.Mode.EnergyRules.UsesPersistentEnergySources)
        {
            DrawPersistentEnergyField(player, rect, isActive);
            return;
        }

        var canHumanAdd = isActive && CanHumanUseActions(_engine!.State) && _engine!.CanAddEnergy();
        var draggedHandIndex = _draggedHandCard?.HandIndex;
        var canPlayEnergy = isActive && draggedHandIndex is not null && _engine!.CanPlayEnergyFromHand(draggedHandIndex.Value);
        var label = canHumanAdd ? $"Energy / Add - Sources {player.EnergyField.Count}" : $"Energy - Sources {player.EnergyField.Count}";
        DrawText(label, new Vector2(rect.X, rect.Y - 26), new Color(199, 209, 222), 0.58f);
        var isDropTarget = isActive && _draggedHandCard is not null && rect.Contains(_virtualMouse);
        Fill(rect, isDropTarget ? canPlayEnergy ? new Color(48, 68, 61) : new Color(66, 42, 39) : new Color(28, 34, 43));
        Border(rect, isDropTarget ? canPlayEnergy ? new Color(148, 224, 164) : new Color(255, 145, 120) : new Color(70, 84, 104), isDropTarget ? 3 : 1);

        var elements = _engine!.State.Mode.Elements;
        var gap = 2;
        var pipHeight = Math.Max(12, Math.Min(17, (rect.Height - 16 - gap * (elements.Count - 1)) / Math.Max(1, elements.Count)));
        var rowStep = pipHeight + gap;
        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            var pip = new Rectangle(rect.X + 10, rect.Y + 8 + i * rowStep, rect.Width - 20, pipHeight);
            var count = player.EnergyPool.GetValueOrDefault(element);
            var maxed = count >= _engine.State.Mode.EnergyRules.MaxPerElement;
            var canAdd = canHumanAdd && _engine.CanAddEnergy(element);
            var hot = canAdd && pip.Contains(_virtualMouse);
            Fill(pip, Color.Lerp(ElementColor(element), Color.Black, maxed ? 0.08f : hot ? 0.12f : 0.32f));
            Border(pip, maxed ? new Color(255, 216, 128) : hot ? Color.White : new Color(105, 119, 138), maxed || hot ? 2 : 1);
            DrawFittedText(element, new Vector2(pip.X + 8, pip.Y + 2), pip.Width - 46, Color.White, 0.34f, 0.22f);
            DrawFittedCenteredText(count.ToString(), new Rectangle(pip.Right - 32, pip.Y + 1, 24, pip.Height - 2), Color.White, 0.36f, 0.24f);

            if (canAdd && Hit(pip))
            {
                ExecuteCommand("add-energy", element, () => _engine.AddEnergy(element));
            }
        }
    }

    private void DrawPersistentEnergyField(PlayerState player, Rectangle rect, bool isActive)
    {
        var engine = _engine!;
        var canHumanAdd = isActive && CanHumanUseActions(engine.State) && engine.CanAddEnergy();
        var draggedHandIndex = _draggedHandCard?.HandIndex;
        var canPlayEnergy = isActive && draggedHandIndex is not null && engine.CanPlayEnergyFromHand(draggedHandIndex.Value);
        DrawText(canHumanAdd ? "Energy Sources / Add" : "Energy Sources", new Vector2(rect.X, rect.Y - 26), new Color(199, 209, 222), 0.58f);
        var isDropTarget = isActive && _draggedHandCard is not null && rect.Contains(_virtualMouse);
        Fill(rect, isDropTarget ? canPlayEnergy ? new Color(48, 68, 61) : new Color(66, 42, 39) : new Color(28, 34, 43));
        Border(rect, isDropTarget ? canPlayEnergy ? new Color(148, 224, 164) : new Color(255, 145, 120) : new Color(70, 84, 104), isDropTarget ? 3 : 1);

        var elements = engine.State.Mode.Elements;
        for (var index = 0; index < elements.Count; index++)
        {
            var element = elements[index];
            var column = index % 4;
            var row = index / 4;
            var pip = new Rectangle(rect.X + 6 + column * 42, rect.Y + 6 + row * 18, 38, 15);
            var available = player.EnergyPool.GetValueOrDefault(element);
            var sources = player.EnergyField.Count(card => engine.State.DefinitionFor(card).Elements.Contains(element, StringComparer.OrdinalIgnoreCase));
            var canAdd = canHumanAdd && engine.CanAddEnergy(element);
            Fill(pip, Color.Lerp(ElementColor(element), Color.Black, 0.24f));
            Border(pip, canAdd && pip.Contains(_virtualMouse) ? Color.White : new Color(105, 119, 138), 1);
            DrawFittedCenteredText($"{ElementAbbreviation(element)}{available}/{sources}", pip, Color.White, 0.25f, 0.18f);
            if (canAdd && Hit(pip))
            {
                ExecuteCommand("add-energy", element, () => engine.AddEnergy(element));
            }
        }

        var visible = player.EnergyField.Skip(_energyFieldScrollOffset).Take(6).ToArray();
        for (var index = 0; index < visible.Length; index++)
        {
            var source = visible[index];
            var card = engine.State.DefinitionFor(source);
            var column = index % 3;
            var row = index / 3;
            var cardRect = new Rectangle(rect.X + 7 + column * 55, rect.Y + 47 + row * 43, 50, 38);
            DrawMiniEnergySource(cardRect, card, source);
        }

        if (player.EnergyField.Count > visible.Length)
        {
            var maxOffset = Math.Max(0, player.EnergyField.Count - 6);
            _energyFieldScrollOffset = Math.Clamp(_energyFieldScrollOffset, 0, maxOffset);
            if (Button(new Rectangle(rect.X + 6, rect.Bottom - 18, 28, 14), "<", _energyFieldScrollOffset > 0))
            {
                _energyFieldScrollOffset--;
            }

            if (Button(new Rectangle(rect.X + 38, rect.Bottom - 18, 28, 14), ">", _energyFieldScrollOffset < maxOffset))
            {
                _energyFieldScrollOffset++;
            }

            DrawFittedCenteredText($"{_energyFieldScrollOffset + 1}-{Math.Min(player.EnergyField.Count, _energyFieldScrollOffset + 6)}/{player.EnergyField.Count}", new Rectangle(rect.Right - 68, rect.Bottom - 18, 62, 14), UiTheme.TextMuted, 0.24f, 0.16f);
        }
    }

    private void DrawMiniEnergySource(Rectangle rect, CardDefinition card, CardInstance source)
    {
        var isBasic = BasicEnergy.IsBasicEnergyCard(card);
        var fill = Color.Lerp(ElementColor(card.Elements[0]), Color.Black, source.Exhausted ? 0.72f : 0.34f);
        Fill(rect, fill);
        Border(rect, isBasic ? UiTheme.DragonGold : new Color(128, 206, 244), isBasic ? 2 : 1);
        DrawFittedCenteredText(ElementAbbreviation(card.Elements[0]), new Rectangle(rect.X + 2, rect.Y + 4, rect.Width - 4, 14), Color.White, 0.32f, 0.2f);
        DrawFittedCenteredText(source.SourceOrigin switch
        {
            EnergySourceOrigin.BasicCard => "CARD",
            EnergySourceOrigin.FreeAdd => "ADD",
            EnergySourceOrigin.Sacrifice => "SAC",
            EnergySourceOrigin.Converted => "CONV",
            _ => "GAIN"
        }, new Rectangle(rect.X + 2, rect.Bottom - 16, rect.Width - 4, 12), source.Exhausted ? UiTheme.TextDisabled : Color.White, 0.2f, 0.16f);
    }


    private void DrawZoneStats(PlayerState player, Rectangle rect)
    {
        var damageLimit = _engine?.State.Mode.DamageLimit ?? 7;
        var damageWarningThreshold = Math.Max(1, damageLimit - 2);
        Fill(rect, new Color(28, 34, 43, 180));
        Border(rect, new Color(70, 84, 104), 1);
        DrawFittedText($"Deck {player.Deck.Count}", new Vector2(rect.X + 10, rect.Y + 8), 64, new Color(205, 214, 225), 0.46f, 0.28f);
        DrawFittedText($"Discard {player.DiscardPile.Count}", new Vector2(rect.X + 82, rect.Y + 8), 68, new Color(205, 214, 225), 0.46f, 0.28f);
        DrawFittedText($"Damage {player.DamageZone.Count}/{damageLimit}", new Vector2(rect.X + 10, rect.Y + 28), 136, player.DamageZone.Count >= damageWarningThreshold ? new Color(255, 190, 120) : new Color(205, 214, 225), 0.48f, 0.3f);
    }

    private void DrawCardZone(string label, List<CardInstance> instances, Rectangle rect, bool compact, bool selectable, bool blockerSelection, bool supportSelection)
    {
        DrawText(label, new Vector2(rect.X, rect.Y - 26), new Color(199, 209, 222), 0.58f);
        var isDropTarget = _draggedHandCard is not null && rect.Contains(_virtualMouse);
        var legalDrop = isDropTarget && CanDropDraggedCardAs(label.Equals("Units", StringComparison.OrdinalIgnoreCase) ? "Unit" : "Support");
        var zoneSource = supportSelection ? SacrificeSource.SupportField : SacrificeSource.UnitField;
        Fill(rect, isDropTarget ? legalDrop ? new Color(48, 68, 61) : new Color(66, 42, 39) : new Color(28, 34, 43));
        Border(rect, isDropTarget ? legalDrop ? new Color(148, 224, 164) : new Color(255, 145, 120) : new Color(70, 84, 104), isDropTarget ? 3 : 1);
        var cardSlots = 5;
        var gap = compact ? 8 : 14;
        var cardWidth = compact
            ? Math.Min(84, Math.Max(58, (rect.Width - 24 - gap * (cardSlots - 1)) / cardSlots))
            : 106;
        var cardHeight = compact ? Math.Min(116, rect.Height - 28) : 146;
        var spacing = cardWidth + gap;
        var ownerIndex = OwnerIndexForZone(instances);
        for (var i = 0; i < Math.Min(instances.Count, 5); i++)
        {
            var card = _engine!.State.DefinitionFor(instances[i]);
            var cardRect = new Rectangle(rect.X + 12 + i * spacing, rect.Y + 16, cardWidth, cardHeight);
            var selected = selectable && (blockerSelection ? _selectedBlockerIndex == i : supportSelection ? _selectedSupportIndex == i : _selectedUnitIndex == i);
            var targetRef = new ZoneRef(ownerIndex, supportSelection ? "SupportField" : "UnitField", i);
            var legalTarget = ownerIndex >= 0 &&
                CanHumanResolveTarget(_engine.State) &&
                _engine.CanResolveTargetChoice(targetRef);
            var legalBlocker = blockerSelection && ownerIndex == BlockingPlayerIndex(_engine.State) && _engine.CanBlock(i);
            if (PresentationSuppressesZone(targetRef, instances[i].Id))
            {
                continue;
            }
            if (InstanceCardButton(
                cardRect,
                card,
                selected || legalTarget || legalBlocker,
                instances[i].Exhausted,
                count: 0,
                compact: true,
                playable: legalTarget || legalBlocker,
                allowPromptBoardPassthrough: legalTarget || legalBlocker))
            {
                if (legalTarget)
                {
                    var payload = TargetPayload(targetRef);
                    ExecuteCommand("target", payload, () => _engine.ResolveTargetChoice(targetRef));
                    ClearSelections();
                    return;
                }

                if (selectable)
                {
                    if (CanCompleteReplacementWith(zoneSource))
                    {
                        ReplaceWithSacrifice(zoneSource, i);
                        return;
                    }

                    if (blockerSelection)
                    {
                        if (legalBlocker)
                        {
                            _selectedBlockerIndex = i;
                            _matchFocus = MatchFocus.Blockers;
                            _cardDetailScrollOffset = 0;
                        }
                    }
                    else if (supportSelection)
                    {
                        _selectedSupportIndex = i;
                        _selectedUnitIndex = -1;
                        _selectedHandIndex = -1;
                        _matchFocus = MatchFocus.Supports;
                        _cardDetailScrollOffset = 0;
                    }
                    else
                    {
                        _selectedUnitIndex = i;
                        _selectedHandIndex = -1;
                        _selectedSupportIndex = -1;
                        _matchFocus = MatchFocus.Units;
                        _cardDetailScrollOffset = 0;
                    }
                }
            }
        }
    }

    private void DrawHand(PlayerState visiblePlayer, Rectangle area)
    {
        DrawPanel(area, new Color(34, 41, 51), border: new Color(84, 99, 119));
        DrawText("Hand", new Vector2(area.X + 18, area.Y + 14), Color.White, 0.7f);
        var state = _engine!.State;
        var canUseHand = CanHumanUseActions(state) && ReferenceEquals(visiblePlayer, state.ActivePlayer);
        const int visibleHandCards = 9;
        var strip = HorizontalStripLayout.Create(
            new Rectangle(area.X + 54, area.Y + 36, 92 * visibleHandCards + 14 * (visibleHandCards - 1), 108),
            visiblePlayer.Hand.Count,
            itemWidth: 92,
            itemGap: 14,
            scroll: _handStripScroll,
            maximumVisibleItems: visibleHandCards);
        var handEnd = strip.VisibleItems.EndExclusive;
        for (var i = strip.VisibleItems.Start; i < handEnd; i++)
        {
            var instance = visiblePlayer.Hand[i];
            var ownerIndex = state.Players.IndexOf(visiblePlayer);
            var handRef = new ZoneRef(ownerIndex, "Hand", i);
            if (PresentationSuppressesZone(handRef, instance.Id))
            {
                continue;
            }

            var card = state.DefinitionFor(instance);
            var rect = strip.ItemBounds(i);
            var selected = _selectedHandIndex == i && _matchFocus == MatchFocus.Hand;
            var playable = canUseHand && _engine.CanPlayCardFromHand(i);
            var unavailable = !playable && (_matchKind != MatchKind.Hotseat && state.ActivePlayerIndex != LocalPlayerIndexForMatch() || canUseHand && _engine.IsMainPhase());
            if (InstanceCardButton(rect, card, selected, exhausted: false, count: 0, compact: true, playable: playable, unavailable: unavailable))
            {
                _selectedHandIndex = i;
                _selectedUnitIndex = -1;
                _selectedSupportIndex = -1;
                _selectedBlockerIndex = -1;
                _matchFocus = MatchFocus.Hand;
            }
        }

        if (_handStripScroll.CanScroll)
        {
            DrawText($"{_handStripScroll.Offset + 1}-{handEnd} / {visiblePlayer.Hand.Count}",
                new Vector2(area.Right - 154, area.Y + 14), UiTheme.TextMuted, 0.42f);
            if (Button(new Rectangle(area.Right - 82, area.Y + 8, 28, 24), "<", _handStripScroll.Offset > 0))
            {
                _handStripScroll.ScrollBy(-1);
            }
            if (Button(new Rectangle(area.Right - 46, area.Y + 8, 28, 24), ">", _handStripScroll.Offset < _handStripScroll.MaxOffset))
            {
                _handStripScroll.ScrollBy(1);
            }
        }
    }

    private PlayerState VisibleHandPlayer(MatchState state) =>
        _matchKind == MatchKind.Hotseat ? state.ActivePlayer : state.Players[LocalPlayerIndexForMatch()];

    private int OwnerIndexForZone(List<CardInstance> instances)
    {
        if (_engine is null)
        {
            return -1;
        }

        for (var i = 0; i < _engine.State.Players.Count; i++)
        {
            var player = _engine.State.Players[i];
            if (ReferenceEquals(instances, player.UnitField) || ReferenceEquals(instances, player.SupportField))
            {
                return i;
            }
        }

        return -1;
    }

    private bool CanHumanUseActions(MatchState state) =>
        state.WinnerIndex is null &&
        state.PendingCombatAction is null &&
        state.PendingEnergyChoice is null &&
        state.PendingTargetChoice is null &&
        (_matchKind == MatchKind.Hotseat || state.ActivePlayerIndex == LocalPlayerIndexForMatch());

    private bool CanHumanResolveTarget(MatchState state) =>
        state.WinnerIndex is null &&
        state.PendingTargetChoice is not null &&
        (_matchKind == MatchKind.Hotseat || state.PendingTargetChoice.PlayerIndex == LocalPlayerIndexForMatch());

    private ZoneRef? FirstLegalTargetChoice()
    {
        if (_engine is null)
        {
            return null;
        }

        for (var playerIndex = 0; playerIndex < _engine.State.Players.Count; playerIndex++)
        {
            var player = _engine.State.Players[playerIndex];
            var units = player.UnitField;
            for (var index = 0; index < units.Count; index++)
            {
                var target = new ZoneRef(playerIndex, "UnitField", index);
                if (_engine.CanResolveTargetChoice(target))
                {
                    return target;
                }
            }

            var supports = player.SupportField;
            for (var index = 0; index < supports.Count; index++)
            {
                var target = new ZoneRef(playerIndex, "SupportField", index);
                if (_engine.CanResolveTargetChoice(target))
                {
                    return target;
                }
            }
        }

        return null;
    }

    private static string TargetPayload(ZoneRef target) => $"{target.PlayerIndex}|{target.Zone}|{target.Index}";

    private bool CanHumanResolveBlock(MatchState state) =>
        state.WinnerIndex is null &&
        state.PendingAttack is not null &&
        state.PendingCombatAction is null &&
        (_matchKind == MatchKind.Hotseat || state.PendingAttack.AttackerPlayerIndex != LocalPlayerIndexForMatch());

    private bool CanHumanResolveCombatAction(MatchState state) =>
        state.WinnerIndex is null &&
        state.PendingCombatAction is not null &&
        state.PendingEnergyChoice is null &&
        state.PendingTargetChoice is null &&
        (_matchKind == MatchKind.Hotseat || state.PendingCombatAction.PriorityPlayerIndex == LocalPlayerIndexForMatch());

    private static int? BlockingPlayerIndex(MatchState state) =>
        state.PendingAttack is null ? null : 1 - state.PendingAttack.AttackerPlayerIndex;

    private PlayerState? BlockingPlayer(MatchState state)
    {
        var index = BlockingPlayerIndex(state);
        return index is null ? null : state.Players[index.Value];
    }

    private void EnsureSelectedBlocker(MatchState state)
    {
        var blockingPlayer = BlockingPlayer(state);
        if (_engine is null || blockingPlayer is null || !CanHumanResolveBlock(state))
        {
            _selectedBlockerIndex = -1;
            return;
        }

        if (_engine.CanBlock(_selectedBlockerIndex))
        {
            _matchFocus = MatchFocus.Blockers;
            return;
        }

        _selectedBlockerIndex = Enumerable
            .Range(0, blockingPlayer.UnitField.Count)
            .Select(index => _engine.CanBlock(index) ? index : -1)
            .Where(index => index >= 0)
            .DefaultIfEmpty(-1)
            .First();
        _matchFocus = MatchFocus.Blockers;
    }

    private string SelectedPlayHint(CardDefinition? card)
    {
        if (_engine is null)
        {
            return "";
        }

        var state = _engine.State;
        if (state.PendingTargetChoice is not null)
        {
            return state.PendingTargetChoice.PlayerIndex == LocalPlayerIndexForMatch() || _matchKind == MatchKind.Hotseat
                ? state.PendingTargetChoice.Message
                : _matchKind == MatchKind.VsAi ? "AI is choosing a target." : "Remote player is choosing a target.";
        }

        if (state.PendingEnergyChoice is not null)
        {
            return state.PendingEnergyChoice.PlayerIndex == LocalPlayerIndexForMatch() || _matchKind == MatchKind.Hotseat
                ? "Choose energy to continue."
                : _matchKind == MatchKind.VsAi ? "AI is choosing energy." : "Remote player is choosing energy.";
        }

        if (_matchKind != MatchKind.Hotseat && state.ActivePlayerIndex != LocalPlayerIndexForMatch())
        {
            return state.PendingAttack is null
                ? _matchKind == MatchKind.VsAi ? "AI is playing." : "Remote player is playing."
                : "Choose a blocker or no block.";
        }

        if (state.PendingAttack is not null)
        {
            return "Choose a blocker or pass.";
        }

        if (_replacementTarget is not null)
        {
            return $"Choose a {ReplacementTypeName(_replacementTarget.Value)} to sacrifice for replacement.";
        }

        if (card is null)
        {
            return _engine.IsMainPhase() ? "Select a card, add energy, or use a ready support." : "Advance to a main phase to play cards.";
        }

        if (!_engine.IsMainPhase())
        {
            return "Wrong phase. Play cards during Main or Second Main.";
        }

        if (card.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase) &&
            state.ActivePlayer.UnitField.Count >= state.Mode.ZoneLimits.UnitSlots)
        {
            return "Unit field is full. Press Replace, then choose a Unit to sacrifice.";
        }

        if (card.Type.Equals("Support", StringComparison.OrdinalIgnoreCase) &&
            state.ActivePlayer.SupportField.Count >= state.Mode.ZoneLimits.SupportSlots)
        {
            return "Support row is full. Press Replace, then choose a Support to sacrifice.";
        }

        if (_selectedHandIndex < 0 || !_engine.CanPlayCardFromHand(_selectedHandIndex))
        {
            return $"Missing energy for {CostText(card)}.";
        }

        return card.Type.Equals("Spell", StringComparison.OrdinalIgnoreCase)
            ? "Playable. Drag to Cast Spell or press Play."
            : $"Playable. Drag to {card.Type} zone or press Play.";
    }

    private void DrawActionPanel(Rectangle area)
    {
        DrawPanel(area, new Color(34, 41, 51), border: new Color(84, 99, 119));
        var state = _engine!.State;
        var handPlayer = VisibleHandPlayer(state);
        var canUseActions = CanHumanUseActions(state);
        var selectedHandCard = state.PendingAttack is null && _selectedHandIndex >= 0 && _selectedHandIndex < handPlayer.Hand.Count
            ? state.DefinitionFor(handPlayer.Hand[_selectedHandIndex])
            : null;
        var selectedField = SelectedHumanFieldCard(state);
        var inspectionCard = _zoomCard ?? (state.PendingAttack is not null ? selectedField?.Definition : selectedHandCard ?? selectedField?.Definition);
        var title = inspectionCard?.Name ?? "No card selected";
        DrawText(title, new Rectangle(area.X + 18, area.Y + 14, area.Width - 36, 28), Color.White, 0.64f);

        if (inspectionCard is not null)
        {
            DrawCardFrame(new Rectangle(area.X + 78, area.Y + 48, 146, 206), inspectionCard, selected: true, exhausted: false, count: _zoomCount, compact: false);
            var detailRect = new Rectangle(area.X + 18, area.Y + 266, area.Width - 36, 132);
            DrawScrollableText(CardDetailText(inspectionCard), detailRect, ref _cardDetailScrollOffset, new Color(205, 214, 225), SmallCardDetailTextScale);
            if (_matchInspectorFocused)
            {
                Border(detailRect, UiTheme.Focus, 3);
            }
        }
        else
        {
            DrawPanel(new Rectangle(area.X + 72, area.Y + 48, 158, 222), new Color(24, 29, 37), border: new Color(70, 84, 104));
            DrawFittedCenteredText("Select or hover a card", new Rectangle(area.X + 84, area.Y + 144, 134, 28), new Color(165, 176, 190), 0.48f, 0.3f);
        }

        DrawText(SelectedPlayHint(selectedHandCard), new Rectangle(area.X + 18, area.Y + 404, area.Width - 36, 30), new Color(196, 207, 220), 0.42f);
        DrawSacrificeTooltip(new Rectangle(area.X + 18, area.Y + 428, area.Width - 36, 72), selectedHandCard, selectedField);
        var castDrop = CastDropRect();
        var castHot = _draggedHandCard is not null && castDrop.Contains(_virtualMouse);
        var castLegal = castHot && CanDropDraggedCardAs("Spell");
        if (_draggedHandCard is not null)
        {
            Border(castDrop, castHot ? castLegal ? new Color(148, 224, 164) : new Color(255, 145, 120) : new Color(82, 98, 118), castHot ? 3 : 1);
            DrawFittedCenteredText("Cast Spell", castDrop, castHot ? castLegal ? new Color(148, 224, 164) : new Color(255, 145, 120) : new Color(165, 176, 190), 0.5f, 0.32f);
        }

        if (Button(new Rectangle(area.X + 18, area.Y + 506, area.Width - 36, 34), NextPhaseLabel(state), canUseActions && state.PendingAttack is null && state.PendingTargetChoice is null && state.WinnerIndex is null))
        {
            ExecuteCommand("advance", "", AdvanceMatchFlow);
            ClearSelections();
        }

        if (state.PendingAttack is null && state.PendingTargetChoice is null)
        {
            var addEnergyRect = new Rectangle(area.X + 18, area.Y + 552, 126, 32);
            if (Button(addEnergyRect, "Add Energy", canUseActions && _engine.CanAddEnergy()))
            {
                _chooseFreeEnergy = true;
                _status = "Choose an element to add energy.";
            }

            if (Button(new Rectangle(area.X + 158, area.Y + 552, 126, 32), "Sacrifice", canUseActions && CanSacrificeSelectedCard()))
            {
                SacrificeSelectedCard();
            }

            var playLabel = ShouldReplaceSelectedHandCard(selectedHandCard) ? "Replace" : "Play";
            var playEnabled = canUseActions &&
                _selectedHandIndex >= 0 &&
                (ShouldReplaceSelectedHandCard(selectedHandCard) || _engine.CanPlayCardFromHand(_selectedHandIndex));
            if (Button(new Rectangle(area.X + 18, area.Y + 596, 126, 32), playLabel, playEnabled))
            {
                if (ShouldReplaceSelectedHandCard(selectedHandCard))
                {
                    BeginReplacement(_selectedHandIndex, selectedHandCard!);
                }
                else
                {
                    PlayCardFromHand(_selectedHandIndex);
                }
            }

            if (Button(new Rectangle(area.X + 158, area.Y + 596, 126, 32), "Attack", canUseActions && _engine.CanDeclareAttack(_selectedUnitIndex) && _replacementTarget is null))
            {
                var payload = _selectedUnitIndex.ToString();
                ExecuteCommand("attack", payload, () => _engine.DeclareAttack(_selectedUnitIndex));
                _selectedUnitIndex = -1;
                _selectedBlockerIndex = -1;
                _matchFocus = MatchFocus.Blockers;
            }

            DrawAbilityButtons(area, selectedField, canUseActions);
        }
        else if (state.PendingTargetChoice is not null)
        {
            DrawText("Choose a highlighted Unit on the battlefield.", new Rectangle(area.X + 18, area.Y + 518, area.Width - 36, 42), new Color(244, 230, 158), 0.5f);
        }
        else
        {
            var canResolveBlock = CanHumanResolveBlock(state);
            if (Button(new Rectangle(area.X + 18, area.Y + 552, 126, 32), "Block", canResolveBlock && _engine.CanBlock(_selectedBlockerIndex)))
            {
                var payload = _selectedBlockerIndex.ToString();
                ExecuteCommand("block", payload, () => _engine.Block(_selectedBlockerIndex));
                ClearSelections();
            }

            if (Button(new Rectangle(area.X + 158, area.Y + 552, 126, 32), "No Block", canResolveBlock && state.WinnerIndex is null))
            {
                ExecuteCommand("pass-block", "", _engine.PassBlock);
                ClearSelections();
            }
        }

        DrawMatchLog(MatchLogRect());
    }

    private void DrawMatchTimeline(Rectangle rect)
    {
        DrawPanel(rect, new Color(26, 32, 41, 220), border: new Color(75, 90, 110));
        if (rect.Height < 44)
        {
            var latest = _matchTimelineEntries.LastOrDefault();
            if (latest is null)
            {
                DrawFittedCenteredText("Timeline: ready", Inset(rect, 4), new Color(165, 176, 190), 0.34f, 0.22f);
                return;
            }

            var icon = new Rectangle(rect.X + 6, rect.Y + 3, 20, rect.Height - 6);
            Fill(icon, Color.Lerp(latest.Color, Color.Black, 0.28f));
            DrawFittedCenteredText(latest.Icon, Inset(icon, 2), Color.White, 0.28f, 0.18f);
            DrawFittedText(latest.Text, new Vector2(icon.Right + 8, rect.Y + 5), rect.Right - icon.Right - 16, new Color(205, 214, 225), 0.32f, 0.2f);
            return;
        }

        DrawText("Timeline", new Vector2(rect.X + 12, rect.Y + 8), Color.White, 0.42f);
        var entries = _matchTimelineEntries.TakeLast(3).ToArray();
        if (entries.Length == 0)
        {
            DrawText("Actions will appear here.", new Rectangle(rect.X + 12, rect.Y + 30, rect.Width - 24, 24), new Color(165, 176, 190), 0.34f);
            return;
        }

        var y = rect.Y + 30;
        foreach (var entry in entries)
        {
            var icon = new Rectangle(rect.X + 12, y, 22, 18);
            Fill(icon, Color.Lerp(entry.Color, Color.Black, 0.28f));
            Border(icon, Color.Lerp(entry.Color, Color.White, 0.22f), 1);
            DrawFittedCenteredText(entry.Icon, Inset(icon, 2), Color.White, 0.3f, 0.18f);
            DrawFittedText(entry.Text, new Vector2(icon.Right + 8, y + 1), rect.Right - icon.Right - 18, new Color(205, 214, 225), 0.34f, 0.22f);
            y += 20;
        }
    }

    private (CardInstance Instance, CardDefinition Definition)? SelectedHumanFieldCard(MatchState state)
    {
        var blockingPlayer = BlockingPlayer(state);
        if (state.PendingAttack is not null &&
            blockingPlayer is not null &&
            _selectedBlockerIndex >= 0 &&
            _selectedBlockerIndex < blockingPlayer.UnitField.Count)
        {
            var blocker = blockingPlayer.UnitField[_selectedBlockerIndex];
            return (blocker, state.DefinitionFor(blocker));
        }

        var player = _matchKind != MatchKind.Hotseat
            ? state.Players[LocalPlayerIndexForMatch()]
            : state.ActivePlayer;

        if (_selectedUnitIndex >= 0 && _selectedUnitIndex < player.UnitField.Count)
        {
            var instance = player.UnitField[_selectedUnitIndex];
            return (instance, state.DefinitionFor(instance));
        }

        if (_selectedSupportIndex >= 0 && _selectedSupportIndex < player.SupportField.Count)
        {
            var instance = player.SupportField[_selectedSupportIndex];
            return (instance, state.DefinitionFor(instance));
        }

        return null;
    }

    private string SelectedSacrificePreviewText(CardDefinition? selectedHandCard, (CardInstance Instance, CardDefinition Definition)? selectedField)
    {
        if (_engine is null)
        {
            return "";
        }

        var card = _matchFocus == MatchFocus.Hand ? selectedHandCard : selectedField?.Definition;
        if (card is null)
        {
            return "";
        }

        var preview = _engine.GetSacrificeEnergyPreview(card);
        return $"Sacrifice: +{preview.Amount} {preview.Element}";
    }

    private void DrawSacrificeTooltip(Rectangle rect, CardDefinition? selectedHandCard, (CardInstance Instance, CardDefinition Definition)? selectedField)
    {
        if (_engine is null || _engine.State.PendingAttack is not null)
        {
            return;
        }

        var card = _matchFocus == MatchFocus.Hand ? selectedHandCard : selectedField?.Definition;
        DrawPanel(rect, new Color(25, 31, 39), border: new Color(76, 90, 110));
        if (card is null)
        {
            DrawText("Sacrifice Preview", new Vector2(rect.X + 10, rect.Y + 8), Color.White, 0.42f);
            DrawText("Select a hand, unit, or support card.", new Rectangle(rect.X + 10, rect.Y + 32, rect.Width - 20, 26), new Color(165, 176, 190), 0.34f);
            return;
        }

        var preview = _engine.GetSacrificeEnergyPreview(card);
        var source = TryGetSelectedSacrificeSource();
        var sourceLabel = source?.Source.ToString() ?? "Selection";
        var enabled = source is not null && _engine.CanSacrificeForEnergy(source.Value.Source, source.Value.Index);
        DrawText("Sacrifice Preview", new Vector2(rect.X + 10, rect.Y + 7), Color.White, 0.4f);
        DrawFittedText(card.Name, new Vector2(rect.X + 10, rect.Y + 28), rect.Width - 120, new Color(244, 230, 158), 0.38f, 0.24f);
        DrawFittedText(sourceLabel, new Vector2(rect.X + 10, rect.Y + 48), rect.Width - 120, new Color(196, 207, 220), 0.3f, 0.2f);
        var gainRect = new Rectangle(rect.Right - 96, rect.Y + 16, 76, 28);
        Fill(gainRect, enabled ? new Color(42, 78, 55) : new Color(54, 48, 43));
        Border(gainRect, enabled ? new Color(148, 224, 164) : new Color(255, 190, 120), 1);
        DrawFittedCenteredText($"+{preview.Amount} {preview.Element}", Inset(gainRect, 3), Color.White, 0.28f, 0.16f);
        if (!enabled)
        {
            DrawFittedText(SacrificeDisabledReason(source), new Vector2(rect.X + 108, rect.Y + 50), rect.Width - 128, new Color(255, 190, 120), 0.28f, 0.18f);
        }
    }

    private string SacrificeDisabledReason((SacrificeSource Source, int Index)? source)
    {
        if (_engine is null || source is null)
        {
            return "No sacrifice source selected.";
        }

        var state = _engine.State;
        if (state.PendingEnergyChoice is not null)
        {
            return "Choose energy first.";
        }

        if (state.PendingTargetChoice is not null)
        {
            return "Choose target first.";
        }

        if (!state.CurrentPhase.Contains("Main", StringComparison.OrdinalIgnoreCase))
        {
            return "Main phase only.";
        }

        return "Energy maxed or unavailable.";
    }

    private string ElementAdvantageSummary(CardDefinition card)
    {
        var mode = _engine?.State.Mode ?? _data.GameModesById["dragon-duel"];
        var strongAgainst = DragonDuelEngine.GetStrongAgainstElements(mode, card);
        var weakAgainst = DragonDuelEngine.GetWeakAgainstElements(mode, card);
        if (strongAgainst.Count == 0 && weakAgainst.Count == 0)
        {
            return "";
        }

        var strongText = strongAgainst.Count == 0 ? "None" : string.Join("/", strongAgainst);
        var weakText = weakAgainst.Count == 0 ? "None" : string.Join("/", weakAgainst);
        return $"Strong vs {strongText} / Weak to {weakText}";
    }

    private bool CanSacrificeSelectedCard()
    {
        if (_engine is null || TryGetSelectedSacrificeSource() is not { } selected)
        {
            return false;
        }

        return _engine.CanSacrificeForEnergy(selected.Source, selected.Index);
    }

    private void SacrificeSelectedCard()
    {
        if (_engine is null || TryGetSelectedSacrificeSource() is not { } selected)
        {
            _status = "Select a hand, unit, or support card to sacrifice.";
            return;
        }

        var payload = $"{selected.Source}|{selected.Index}";
        if (ExecuteCommand("sacrifice", payload, () => _engine.SacrificeForEnergy(selected.Source, selected.Index)))
        {
            ClearSelections();
        }
    }

    private (SacrificeSource Source, int Index)? TryGetSelectedSacrificeSource()
    {
        if (_engine is null)
        {
            return null;
        }

        return _matchFocus switch
        {
            MatchFocus.Hand when IsValidHandIndex(_selectedHandIndex) => (SacrificeSource.Hand, _selectedHandIndex),
            MatchFocus.Units when _selectedUnitIndex >= 0 && _selectedUnitIndex < _engine.State.ActivePlayer.UnitField.Count => (SacrificeSource.UnitField, _selectedUnitIndex),
            MatchFocus.Supports when _selectedSupportIndex >= 0 && _selectedSupportIndex < _engine.State.ActivePlayer.SupportField.Count => (SacrificeSource.SupportField, _selectedSupportIndex),
            _ => null
        };
    }

    private bool ShouldReplaceSelectedHandCard(CardDefinition? selectedHandCard) =>
        _engine is not null &&
        selectedHandCard is not null &&
        _selectedHandIndex >= 0 &&
        IsZoneFullForCard(selectedHandCard);

    private void BeginReplacement(int handIndex, CardDefinition card)
    {
        if (_engine is null || !IsValidHandIndex(handIndex))
        {
            return;
        }

        var target = ReplacementTargetForCard(card);
        if (target is null)
        {
            PlayCardFromHand(handIndex);
            return;
        }

        _replacementHandIndex = handIndex;
        _replacementTarget = target;

        if (TryGetSelectedReplacementSource() is { } selected)
        {
            ReplaceWithSacrifice(selected.Source, selected.Index);
            return;
        }

        _status = $"Choose a {ReplacementTypeName(target.Value)} to sacrifice, then {card.Name} will be played.";
    }

    private bool CanCompleteReplacementWith(SacrificeSource source)
    {
        if (_replacementTarget is null)
        {
            return false;
        }

        return (_replacementTarget == ReplacementTarget.Unit && source == SacrificeSource.UnitField) ||
            (_replacementTarget == ReplacementTarget.Support && source == SacrificeSource.SupportField);
    }

    private void ReplaceWithSacrifice(SacrificeSource source, int index)
    {
        if (_engine is null || _replacementTarget is null || !CanCompleteReplacementWith(source) || !IsValidHandIndex(_replacementHandIndex))
        {
            _status = "Replacement target is no longer valid.";
            _replacementTarget = null;
            _replacementHandIndex = -1;
            return;
        }

        var replacingCard = _engine.State.DefinitionFor(_engine.State.ActivePlayer.Hand[_replacementHandIndex]);
        var sacrifice = _engine.SacrificeForEnergy(source, index);
        if (!sacrifice.Success)
        {
            ApplyResult(sacrifice);
            return;
        }

        var play = _engine.PlayCardFromHand(_replacementHandIndex);
        if (play.Success)
        {
            SendOnlineCommand("sacrifice", $"{source}|{index}", sacrifice);
            SendOnlineCommand("play-card", _replacementHandIndex.ToString(), play);
        }

        _replacementTarget = null;
        _replacementHandIndex = -1;
        ApplyHumanResult(play.Success
            ? GameActionResult.Ok($"{replacingCard.Name} replaced the sacrificed card.")
            : GameActionResult.Fail($"{sacrifice.Message} {play.Message}"));

        if (play.Success)
        {
            ClearSelections();
        }
    }

    private (SacrificeSource Source, int Index)? TryGetSelectedReplacementSource()
    {
        if (_replacementTarget == ReplacementTarget.Unit &&
            _selectedUnitIndex >= 0 &&
            _engine is not null &&
            _selectedUnitIndex < _engine.State.ActivePlayer.UnitField.Count)
        {
            return (SacrificeSource.UnitField, _selectedUnitIndex);
        }

        if (_replacementTarget == ReplacementTarget.Support &&
            _selectedSupportIndex >= 0 &&
            _engine is not null &&
            _selectedSupportIndex < _engine.State.ActivePlayer.SupportField.Count)
        {
            return (SacrificeSource.SupportField, _selectedSupportIndex);
        }

        return null;
    }

    private bool IsZoneFullForCard(CardDefinition card) =>
        _engine is not null &&
        (card.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase) &&
            _engine.State.ActivePlayer.UnitField.Count >= _engine.State.Mode.ZoneLimits.UnitSlots ||
         card.Type.Equals("Support", StringComparison.OrdinalIgnoreCase) &&
            _engine.State.ActivePlayer.SupportField.Count >= _engine.State.Mode.ZoneLimits.SupportSlots);

    private static ReplacementTarget? ReplacementTargetForCard(CardDefinition card)
    {
        if (card.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase))
        {
            return ReplacementTarget.Unit;
        }

        if (card.Type.Equals("Support", StringComparison.OrdinalIgnoreCase))
        {
            return ReplacementTarget.Support;
        }

        return null;
    }

    private static string ReplacementTypeName(ReplacementTarget target) =>
        target == ReplacementTarget.Unit ? "Unit" : "Support";

    private void DrawAbilityButtons(Rectangle area, (CardInstance Instance, CardDefinition Definition)? selectedField, bool canUseActions)
    {
        if (_engine is null || selectedField is null || selectedField.Value.Definition.Abilities.Count == 0)
        {
            return;
        }

        var ownerIndex = _matchKind == MatchKind.Hotseat ? _engine.State.ActivePlayerIndex : LocalPlayerIndexForMatch();
        var abilities = selectedField.Value.Definition.Abilities.Take(2).ToArray();
        for (var i = 0; i < abilities.Length; i++)
        {
            var ability = abilities[i];
            var rect = new Rectangle(area.X + 18, area.Y + 642 + i * 32, area.Width - 36, 28);
            var enabled = canUseActions && _engine.CanActivateAbility(ownerIndex, selectedField.Value.Instance.Id, ability.Id);
            if (Button(rect, ability.Name, enabled))
            {
                var payload = $"{ownerIndex}|{selectedField.Value.Instance.Id}|{ability.Id}";
                ExecuteCommand("ability", payload, () => _engine.ActivateAbility(ownerIndex, selectedField.Value.Instance.Id, ability.Id));
            }
        }
    }

    private bool CardButton(Rectangle rect, CardDefinition card, bool selected, int count, bool compact)
    {
        var clicked = Hit(rect);
        var hover = rect.Contains(_virtualMouse);
        var drawRect = !_settings.ReducedMotion && (hover || selected)
            ? new Rectangle(rect.X, rect.Y - 3, rect.Width, rect.Height)
            : rect;
        DrawCardFrame(drawRect, card, selected || hover, exhausted: false, count, compact);
        if (hover)
        {
            RequestZoom(card, count, drawRect);
        }

        return clicked;
    }

    private bool InstanceCardButton(
        Rectangle rect,
        CardDefinition card,
        bool selected,
        bool exhausted,
        int count,
        bool compact,
        bool playable = false,
        bool unavailable = false,
        bool allowPromptBoardPassthrough = false)
    {
        var clicked = Hit(rect, allowPromptBoardPassthrough);
        var hover = rect.Contains(_virtualMouse);
        var drawRect = !_settings.ReducedMotion && (hover || selected)
            ? new Rectangle(rect.X, rect.Y - 3, rect.Width, rect.Height)
            : rect;
        DrawCardFrame(drawRect, card, selected || hover, exhausted, count, compact);
        if (unavailable)
        {
            Fill(drawRect, new Color(0, 0, 0, 112));
        }

        if (playable)
        {
            Border(new Rectangle(rect.X - 2, rect.Y - 2, rect.Width + 4, rect.Height + 4), new Color(148, 224, 164), selected || hover ? 3 : 2);
        }

        if (hover)
        {
            RequestZoom(card, count, drawRect);
        }

        return clicked;
    }

    private void DrawCardFrame(Rectangle rect, CardDefinition card, bool selected, bool exhausted, int count, bool compact)
    {
        if (compact)
        {
            DrawCompactCardFrame(rect, card, selected, exhausted, count);
            return;
        }

        DrawFullCardFrame(rect, card, selected, exhausted, count);
    }

    private void DrawCompactCardFrame(Rectangle rect, CardDefinition card, bool selected, bool exhausted, int count)
    {
        var element = card.Elements.FirstOrDefault() ?? "";
        var color = ElementColor(element);
        var dim = exhausted ? 0.58f : 0f;
        var borderColor = selected ? new Color(244, 230, 158) : new Color(17, 19, 24);
        var border = selected ? 4 : 2;

        Fill(new Rectangle(rect.X + 7, rect.Y + 9, rect.Width, rect.Height), new Color(0, 0, 0, selected ? 120 : 86));
        Fill(rect, Color.Lerp(Color.Lerp(color, new Color(231, 222, 196), 0.2f), Color.Black, dim));
        Border(rect, borderColor, border);
        var rim = Inset(rect, selected ? 6 : 5);
        Fill(rim, Color.Lerp(color, Color.Black, 0.18f + dim * 0.3f));
        Border(rim, Color.Lerp(Color.Black, color, 0.36f), 1);
        var inner = Inset(rect, 8);
        Fill(inner, Color.Lerp(new Color(239, 232, 214), Color.Black, dim * 0.4f));

        var title = new Rectangle(inner.X + 3, inner.Y + 3, inner.Width - 6, Math.Clamp(rect.Height / 7, 16, 24));
        var costs = new Rectangle(inner.X + 3, title.Bottom + 2, inner.Width - 6, Math.Clamp(rect.Height / 10, 12, 16));
        var footer = new Rectangle(inner.X + 3, inner.Bottom - Math.Clamp(rect.Height / 7, 16, 22), inner.Width - 6, Math.Clamp(rect.Height / 7, 16, 22));
        var type = new Rectangle(inner.X + 3, footer.Y - Math.Clamp(rect.Height / 7, 16, 22) - 3, inner.Width - 6, Math.Clamp(rect.Height / 7, 16, 22));
        var art = new Rectangle(inner.X + 5, costs.Bottom + 3, inner.Width - 10, Math.Max(22, type.Y - costs.Bottom - 6));

        Fill(title, Color.Lerp(color, Color.Black, 0.2f + dim * 0.5f));
        Fill(new Rectangle(title.X, title.Bottom - 2, title.Width, 2), Color.Lerp(color, Color.White, 0.34f));
        var rarity = new Rectangle(title.Right - 13, title.Y + 1, 11, 10);
        DrawRarityBadge(rarity, card.Rarity, compact: true);
        DrawFittedText(card.Name, new Vector2(title.X + 3, title.Y + 2), title.Width - rarity.Width - 10, Color.White, 0.34f, 0.2f);
        DrawCostBadges(card, costs, compact: true);

        Fill(art, Color.Lerp(color, Color.Black, 0.12f + dim * 0.4f));
        Fill(new Rectangle(art.X + 5, art.Y + 5, art.Width - 10, art.Height - 10), Color.Lerp(color, Color.White, 0.2f));
        Fill(new Rectangle(art.X + 10, art.Y + 10, art.Width - 20, Math.Max(5, art.Height / 7)), new Color(255, 255, 255, exhausted ? 22 : 58));
        Fill(new Rectangle(art.X + 10, art.Bottom - Math.Max(11, art.Height / 5), art.Width - 20, Math.Max(5, art.Height / 8)), new Color(0, 0, 0, exhausted ? 42 : 64));
        DrawFittedCenteredText(ElementDisplay(card), Inset(art, 4), new Color(255, 255, 255, exhausted ? 86 : 176), 0.46f, 0.2f);
        Border(art, Color.Lerp(Color.Black, color, 0.28f), 2);

        Fill(type, Color.Lerp(TypeColor(card.Type), Color.White, 0.66f));
        Border(type, Color.Lerp(TypeColor(card.Type), Color.Black, 0.38f), 1);
        var typeLine = $"{card.Type} / {string.Join(" ", card.Elements)}";
        DrawFittedText(typeLine, new Vector2(type.X + 3, type.Y + 2), type.Width - 6, new Color(33, 31, 29), 0.32f, 0.18f);

        Fill(footer, new Color(250, 246, 233));
        Border(footer, new Color(84, 73, 58), 1);
        DrawCardFooterBadges(footer, rect, card, count, compact: true);
    }

    private void DrawFullCardFrame(Rectangle rect, CardDefinition card, bool selected, bool exhausted, int count)
    {
        var element = card.Elements.FirstOrDefault() ?? "";
        var color = ElementColor(element);
        var dim = exhausted ? 0.58f : 0f;
        var borderColor = selected ? new Color(244, 230, 158) : new Color(17, 19, 24);
        var border = selected ? 4 : 2;

        Fill(new Rectangle(rect.X + 7, rect.Y + 9, rect.Width, rect.Height), new Color(0, 0, 0, selected ? 120 : 86));
        Fill(rect, Color.Lerp(Color.Lerp(color, new Color(231, 222, 196), 0.2f), Color.Black, dim));
        Border(rect, borderColor, border);
        var rim = Inset(rect, selected ? 6 : 5);
        Fill(rim, Color.Lerp(color, Color.Black, 0.18f + dim * 0.3f));
        Border(rim, Color.Lerp(Color.Black, color, 0.36f), 1);
        var inner = Inset(rect, 10);
        Fill(inner, Color.Lerp(new Color(239, 232, 214), Color.Black, dim * 0.4f));

        var tight = rect.Height < 240;
        var titleHeight = tight ? 30 : 48;
        var typeHeight = tight ? 20 : 30;
        var footerHeight = tight ? 24 : 32;
        var title = new Rectangle(inner.X + 4, inner.Y + 4, inner.Width - 8, titleHeight);
        var footer = new Rectangle(inner.X + 4, inner.Bottom - footerHeight - 4, inner.Width - 8, footerHeight);
        var artHeight = Math.Clamp(rect.Height * 26 / 100, tight ? 34 : 56, tight ? 52 : 98);
        var art = new Rectangle(inner.X + 8, title.Bottom + 5, inner.Width - 16, artHeight);
        var type = new Rectangle(inner.X + 6, art.Bottom + 5, inner.Width - 12, typeHeight);
        var rules = new Rectangle(inner.X + 6, type.Bottom + 5, inner.Width - 12, Math.Max(18, footer.Y - type.Bottom - 10));

        Fill(title, Color.Lerp(color, Color.Black, 0.2f + dim * 0.5f));
        Fill(new Rectangle(title.X, title.Bottom - 3, title.Width, 3), Color.Lerp(color, Color.White, 0.34f));
        var rarity = new Rectangle(title.Right - (tight ? 17 : 22), title.Y + 4, tight ? 14 : 18, tight ? 14 : 18);
        DrawRarityBadge(rarity, card.Rarity, compact: true);
        DrawFittedText(card.Name, new Vector2(title.X + 6, title.Y + 3), title.Width - rarity.Width - 18, Color.White, tight ? 0.44f : 0.64f, tight ? 0.24f : 0.34f);
        DrawCostBadges(card, new Rectangle(title.X + 6, title.Bottom - (tight ? 16 : 22), title.Width - 12, tight ? 13 : 18), compact: false);

        Fill(art, Color.Lerp(color, Color.Black, 0.12f + dim * 0.4f));
        Fill(new Rectangle(art.X + 5, art.Y + 5, art.Width - 10, art.Height - 10), Color.Lerp(color, Color.White, 0.2f));
        Fill(new Rectangle(art.X + 10, art.Y + 10, art.Width - 20, Math.Max(5, art.Height / 7)), new Color(255, 255, 255, exhausted ? 22 : 58));
        Fill(new Rectangle(art.X + 10, art.Bottom - Math.Max(11, art.Height / 5), art.Width - 20, Math.Max(5, art.Height / 8)), new Color(0, 0, 0, exhausted ? 42 : 64));
        DrawFittedCenteredText(ElementDisplay(card), Inset(art, 10), new Color(255, 255, 255, exhausted ? 86 : 176), tight ? 0.72f : 0.98f, 0.38f);
        Border(art, Color.Lerp(Color.Black, color, 0.28f), 2);

        Fill(type, Color.Lerp(TypeColor(card.Type), Color.White, 0.66f));
        Border(type, Color.Lerp(TypeColor(card.Type), Color.Black, 0.38f), 1);
        var typeLine = $"{card.Type} / {string.Join(" ", card.Elements)}";
        DrawFittedText(typeLine, new Vector2(type.X + 5, type.Y + 3), type.Width - 10, new Color(33, 31, 29), tight ? 0.38f : 0.58f, tight ? 0.2f : 0.3f);

        Fill(rules, new Color(250, 246, 233));
        Border(rules, new Color(84, 73, 58), 1);
        if (rules.Height >= 26)
        {
            DrawCardFrameRulesText(card, new Rectangle(rules.X + 6, rules.Y + 5, rules.Width - 12, rules.Height - 10), new Color(38, 35, 31), tight ? 0.48f : 0.66f);
        }

        Fill(footer, new Color(250, 246, 233));
        Border(footer, new Color(84, 73, 58), 1);
        DrawCardFooterBadges(footer, rect, card, count, compact: false);
    }

    private void DrawCardFooterBadges(Rectangle footer, Rectangle cardRect, CardDefinition card, int count, bool compact)
    {
        var abilityRect = Rectangle.Empty;
        var powerRect = Rectangle.Empty;

        if (card.Power > 0)
        {
            var width = compact ? Math.Min(42, footer.Width - 6) : Math.Min(60, footer.Width - 8);
            powerRect = new Rectangle(footer.Right - width - 2, footer.Y + 2, width, footer.Height - 4);
            Fill(powerRect, new Color(31, 34, 39));
            Border(powerRect, new Color(216, 199, 139), 1);
            DrawFittedCenteredText(card.Power.ToString(), Inset(powerRect, 2), Color.White, compact ? 0.32f : 0.48f, compact ? 0.18f : 0.28f);
        }

        if (card.Abilities.Count > 0)
        {
            var width = compact ? Math.Min(28, footer.Width - 6) : Math.Min(44, footer.Width - 8);
            abilityRect = new Rectangle(footer.X + 2, footer.Y + 2, width, footer.Height - 4);
            Fill(abilityRect, new Color(44, 48, 58));
            Border(abilityRect, new Color(183, 204, 232), 1);
            DrawFittedCenteredText("ACT", Inset(abilityRect, 2), Color.White, compact ? 0.28f : 0.42f, compact ? 0.16f : 0.26f);
        }

        if (count > 0)
        {
            var width = compact ? 24 : 30;
            var badge = new Rectangle(footer.Center.X - width / 2, footer.Y + 2, width, footer.Height - 4);
            if (!abilityRect.IsEmpty && badge.Intersects(abilityRect) ||
                !powerRect.IsEmpty && badge.Intersects(powerRect))
            {
                badge = new Rectangle(cardRect.Right - width - 5, cardRect.Y + 6, width, compact ? 14 : 18);
            }

            Fill(badge, new Color(18, 22, 28));
            Border(badge, new Color(221, 206, 150), 1);
            DrawFittedCenteredText($"x{count}", Inset(badge, 2), Color.White, compact ? 0.28f : 0.42f, compact ? 0.16f : 0.26f);
        }
    }

    private void DrawRarityBadge(Rectangle rect, string rarity, bool compact)
    {
        var normalized = CardRarities.Normalize(rarity);
        Fill(rect, Color.Lerp(RarityColor(normalized), Color.Black, 0.12f));
        Border(rect, Color.Lerp(RarityColor(normalized), Color.White, 0.34f), 1);
        if (_rarityIcons.TryGetValue(normalized, out var icon))
        {
            var iconSize = Math.Max(8, Math.Min(rect.Height - 4, compact ? rect.Width - 6 : 16));
            var iconRect = compact
                ? new Rectangle(rect.Center.X - iconSize / 2, rect.Center.Y - iconSize / 2, iconSize, iconSize)
                : new Rectangle(rect.X + 3, rect.Center.Y - iconSize / 2, iconSize, iconSize);
            _spriteBatch!.Draw(icon, iconRect, ApplyDrawOpacity(Color.White));
            if (!compact)
            {
                var textX = iconRect.Right + 3;
                DrawFittedText(normalized, new Vector2(textX, rect.Y + 3), Math.Max(0, rect.Right - textX - 3), Color.White, 0.26f, 0.14f);
            }

            return;
        }

        DrawFittedCenteredText(compact ? RarityAbbreviation(normalized) : normalized, Inset(rect, 2), Color.White, compact ? 0.18f : 0.3f, compact ? 0.11f : 0.18f);
    }

    private static string RarityAbbreviation(string rarity) => CardRarities.Normalize(rarity) switch
    {
        CardRarities.Common => "C",
        CardRarities.Uncommon => "U",
        CardRarities.Rare => "R",
        CardRarities.Legendary => "L",
        CardRarities.Mythic => "M",
        _ => "C"
    };

    private static Color RarityColor(string rarity) => CardRarities.Normalize(rarity) switch
    {
        CardRarities.Common => new Color(118, 128, 138),
        CardRarities.Uncommon => new Color(68, 148, 92),
        CardRarities.Rare => new Color(70, 126, 202),
        CardRarities.Legendary => new Color(206, 154, 56),
        CardRarities.Mythic => new Color(198, 70, 104),
        _ => new Color(118, 128, 138)
    };

    private void DrawCostBadges(CardDefinition card, Rectangle rect, bool compact)
    {
        var costs = OrderedCosts(card).ToArray();
        if (costs.Length == 0)
        {
            var free = new Rectangle(rect.Right - (compact ? 18 : 26), rect.Y + (rect.Height - (compact ? 10 : 16)) / 2, compact ? 18 : 26, compact ? 10 : 16);
            Fill(free, new Color(43, 48, 58));
            Border(free, new Color(210, 218, 228), 1);
            DrawFittedCenteredText("0", Inset(free, 1), Color.White, compact ? 0.28f : 0.44f, compact ? 0.16f : 0.26f);
            return;
        }

        var badgeWidth = compact ? 24 : 78;
        var badgeHeight = compact ? Math.Min(10, rect.Height) : Math.Min(16, rect.Height);
        var gap = compact ? 2 : 4;
        if (costs.Length * badgeWidth + (costs.Length - 1) * gap > rect.Width)
        {
            badgeWidth = Math.Max(compact ? 17 : 44, (rect.Width - (costs.Length - 1) * gap) / costs.Length);
        }

        var totalWidth = costs.Length * badgeWidth + (costs.Length - 1) * gap;
        var x = Math.Max(rect.X, rect.Right - totalWidth);
        var y = rect.Y + (rect.Height - badgeHeight) / 2;

        foreach (var (element, amount) in costs)
        {
            var badge = new Rectangle(x, y, badgeWidth, badgeHeight);
            var isGeneric = element.Equals(DragonCardConstants.GenericCost, StringComparison.OrdinalIgnoreCase);
            Fill(badge, isGeneric ? new Color(58, 63, 74) : Color.Lerp(ElementColor(element), Color.Black, 0.08f));
            Border(badge, isGeneric ? new Color(216, 221, 230) : new Color(255, 248, 214), 1);
            var label = compact ? CompactCostText(element, amount) : isGeneric ? $"Generic {amount}" : $"{element} {amount}";
            DrawFittedCenteredText(label, Inset(badge, compact ? 1 : 2), Color.White, compact ? 0.24f : 0.4f, compact ? 0.14f : 0.24f);
            x += badgeWidth + gap;
        }
    }

    private void DrawZoomPreview()
    {
        if (!_settings.CardZoom ||
            _zoomCard is null ||
            _draggedHandCard is not null ||
            _chooseFreeEnergy ||
            _engine?.State.PendingEnergyChoice is not null ||
            _engine?.State.PendingEnergySourceChoice is not null)
        {
            return;
        }

        const int width = 760;
        const int height = 560;
        const int margin = 18;
        var x = _zoomSource.Center.X < VirtualWidth / 2
            ? _zoomSource.Right + 24
            : _zoomSource.X - width - 24;
        var y = _zoomSource.Center.Y - height / 2;
        if (_zoomSource.Bottom > VirtualHeight - 220)
        {
            y = _zoomSource.Y - height - 24;
        }

        x = Math.Clamp(x, margin, VirtualWidth - width - margin);
        y = Math.Clamp(y, 86, VirtualHeight - height - 58);
        var rect = new Rectangle(x, y, width, height);
        Fill(new Rectangle(rect.X + 12, rect.Y + 14, rect.Width, rect.Height), new Color(0, 0, 0, 150));
        DrawPanel(rect, new Color(24, 30, 39, 238), border: new Color(124, 143, 166));

        var cardRect = new Rectangle(rect.X + 24, rect.Y + 28, 318, 446);
        DrawCardFrame(cardRect, _zoomCard, selected: true, exhausted: false, count: _zoomCount, compact: false);

        var detailX = cardRect.Right + 28;
        DrawRarityBadge(new Rectangle(detailX, rect.Y + 30, 110, 26), _zoomCard.Rarity, compact: false);
        DrawFittedText(_zoomCard.Name, new Vector2(detailX + 126, rect.Y + 30), rect.Right - detailX - 150, Color.White, 0.84f, 0.48f);
        DrawText($"{_zoomCard.Type} / {string.Join(" ", _zoomCard.Elements)}", new Rectangle(detailX, rect.Y + 64, rect.Right - detailX - 24, 32), new Color(244, 230, 158), 0.64f);
        DrawScrollableText(CardDetailText(_zoomCard), new Rectangle(detailX, rect.Y + 108, rect.Right - detailX - 24, rect.Height - 134), ref _cardDetailScrollOffset, new Color(221, 229, 239), CardDetailTextScale);
    }

    private void DrawDraggedCard()
    {
        if (_draggedHandCard is null || _engine is null || !IsValidHandIndex(_draggedHandCard.HandIndex))
        {
            return;
        }

        var card = _engine.State.DefinitionFor(_engine.State.ActivePlayer.Hand[_draggedHandCard.HandIndex]);
        var topLeft = _virtualMouse - _draggedHandCard.Offset;
        var rect = new Rectangle(topLeft.X, topLeft.Y, 92, 132);
        DrawCardFrame(rect, card, selected: true, exhausted: false, count: 0, compact: true);
    }

    private void DrawPresentationOverlay()
    {
        if (_engine is null)
        {
            return;
        }

        foreach (var beat in _presentation.ActiveBeats)
        {
            DrawPresentationBeat(beat);
        }
    }

    private bool PresentationSuppressesZone(ZoneRef zone, string instanceId)
    {
        if (_settings.ReducedMotion)
        {
            return false;
        }

        return PresentationVisibility.SuppressesDestination(
            _presentation.ActiveBeats,
            zone,
            instanceId,
            _settings.ReducedMotion);
    }

    private void DrawPresentationBeat(PresentationBeat beat)
    {
        var progress = EaseOutCubic(beat.Progress);
        var pulse = (float)Math.Sin(beat.Progress * Math.PI);
        var envelope = Math.Clamp(Math.Min(beat.Progress / 0.18f, (1f - beat.Progress) / 0.18f), 0f, 1f);
        var previousOpacity = _drawOpacity;
        _drawOpacity *= envelope;
        try
        {
            var color = PresentationColor(beat.Event);
            if (_settings.ReducedMotion || beat.Recipe.Motion == PresentationMotion.StaticHighlight)
            {
                var target = ZoneCenter(beat.Event.To ?? beat.Event.From, beat.Event.PlayerIndex);
                DrawRing(target, 38f, color, 3);
                DrawBeatLabel(beat.Recipe.Caption, beat.Event.Message, color);
                return;
            }

            if (beat.Event.Kind == MatchEventKind.PhaseChanged)
            {
                var banner = new Rectangle(494, 356, 612, 92);
                Fill(banner, new Color(18, 22, 29, 210));
                Border(banner, Color.Lerp(color, Color.White, 0.3f), 3);
                DrawFittedCenteredText(beat.Event.Message, banner, Color.White, 1.0f + pulse * 0.12f, 0.52f);
                return;
            }

            var from = ZoneCenter(beat.Event.From, beat.Event.PlayerIndex);
            var to = ZoneCenter(beat.Event.To, beat.Event.PlayerIndex);
            if (beat.Event.Kind is MatchEventKind.EnergyGained or MatchEventKind.EnergyRefreshed or MatchEventKind.EnergySpent or MatchEventKind.EnergyConverted)
            {
                to = ZoneCenter(new ZoneRef(beat.Event.PlayerIndex, "EnergyPool"), beat.Event.PlayerIndex);
                var radius = 28 + (int)(pulse * 18);
                DrawRing(to, radius, color, 3);
                DrawFittedCenteredText($"{beat.Event.Element} {SignedAmount(beat.Event)}", new Rectangle(to.X - 58, to.Y - 12, 116, 24), Color.White, 0.58f, 0.36f);
                return;
            }

            if (beat.Event.Kind is MatchEventKind.AttackDeclared or MatchEventKind.BlockDeclared or MatchEventKind.DamageTaken)
            {
                var start = ZoneCenter(beat.Event.From, beat.Event.PlayerIndex);
                var end = beat.Event.Kind == MatchEventKind.DamageTaken
                    ? ZoneCenter(beat.Event.To ?? new ZoneRef(1 - beat.Event.PlayerIndex, "DamageZone"), 1 - beat.Event.PlayerIndex)
                    : beat.Event.Kind == MatchEventKind.AttackDeclared
                        ? BoardRectForPlayer(1 - beat.Event.PlayerIndex).Center
                        : BoardRectForPlayer(beat.Event.PlayerIndex).Center;
                var moving = LerpPoint(start, end, progress);
                DrawArrow(start, moving, color, 5);
                var label = beat.Event.Kind switch
                {
                    MatchEventKind.AttackDeclared => "Attack",
                    MatchEventKind.BlockDeclared => "Block",
                    _ => "Damage"
                };
                DrawBeatLabel(label, beat.Event.Message, color);
                return;
            }

            if (beat.Event.Kind is MatchEventKind.CombatActionQueued or MatchEventKind.CombatActionPassed or MatchEventKind.CombatResolved or MatchEventKind.TargetResolved or MatchEventKind.CardReadied)
            {
                var center = to;
                var radius = 34 + (int)(pulse * 24);
                DrawRing(center, radius, color, 3);
                DrawBeatLabel(beat.Event.Kind == MatchEventKind.CombatResolved ? "Resolve" : "Action", beat.Event.Message, color);
                return;
            }

            var point = LerpPoint(from, to, progress);
            var width = 96 + (int)(pulse * 10);
            var height = 136 + (int)(pulse * 14);
            var cardRect = new Rectangle(point.X - width / 2, point.Y - height / 2, width, height);
            if (!string.IsNullOrWhiteSpace(beat.Event.CardId) && _engine!.State.Cards.TryGetValue(beat.Event.CardId, out var card))
            {
                DrawCardFrame(cardRect, card, selected: true, exhausted: false, count: 0, compact: true);
            }
            else
            {
                Fill(cardRect, Color.Lerp(color, Color.Black, 0.25f));
                Border(cardRect, Color.White, 2);
            }

            Border(new Rectangle(cardRect.X - 8, cardRect.Y - 8, cardRect.Width + 16, cardRect.Height + 16), color, 3);
            if (!string.IsNullOrWhiteSpace(beat.Event.Message))
            {
                DrawBeatLabel("Event", beat.Event.Message, color);
            }
        }
        finally
        {
            _drawOpacity = previousOpacity;
        }
    }

    private void DrawBeatLabel(string title, string message, Color color)
    {
        var banner = new Rectangle(430, 376, 740, 58);
        Fill(banner, new Color(18, 22, 29, 218));
        Border(banner, Color.Lerp(color, Color.White, 0.25f), 2);
        DrawFittedText(title, new Vector2(banner.X + 18, banner.Y + 10), 116, Color.Lerp(color, Color.White, 0.35f), 0.62f, 0.36f);
        DrawFittedText(message, new Vector2(banner.X + 132, banner.Y + 14), banner.Width - 150, Color.White, 0.52f, 0.3f);
    }

    private void DrawArrow(Point from, Point to, Color color, int thickness)
    {
        DrawLine(from, to, color, thickness);
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1f)
        {
            return;
        }

        var angle = MathF.Atan2(dy, dx);
        const float arrowLength = 22f;
        var left = new Point(
            to.X - (int)MathF.Round(MathF.Cos(angle - 0.52f) * arrowLength),
            to.Y - (int)MathF.Round(MathF.Sin(angle - 0.52f) * arrowLength));
        var right = new Point(
            to.X - (int)MathF.Round(MathF.Cos(angle + 0.52f) * arrowLength),
            to.Y - (int)MathF.Round(MathF.Sin(angle + 0.52f) * arrowLength));
        DrawLine(to, left, color, thickness);
        DrawLine(to, right, color, thickness);
    }

    private void DrawLine(Point from, Point to, Color color, int thickness)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= 0.5f)
        {
            return;
        }

        var angle = MathF.Atan2(dy, dx);
        _spriteBatch!.Draw(
            _pixel!,
            new Rectangle(from.X, from.Y, (int)MathF.Round(length), Math.Max(1, thickness)),
            null,
            ApplyDrawOpacity(color),
            angle,
            new Vector2(0f, thickness / 2f),
            SpriteEffects.None,
            0f);
    }

    private void DrawRing(Point center, float radius, Color color, int thickness, int segments = 24)
    {
        var previous = new Point(center.X + (int)MathF.Round(radius), center.Y);
        for (var i = 1; i <= segments; i++)
        {
            var angle = MathHelper.TwoPi * i / segments;
            var next = new Point(
                center.X + (int)MathF.Round(MathF.Cos(angle) * radius),
                center.Y + (int)MathF.Round(MathF.Sin(angle) * radius));
            DrawLine(previous, next, color, thickness);
            previous = next;
        }
    }

    private bool IsDecisionPromptActive()
    {
        if (_screen != Screen.Match || _engine is null || _presentation.IsBlocking)
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

    private bool CanHumanResolveEnergyChoice(MatchState state) =>
        state.WinnerIndex is null &&
        state.PendingEnergyChoice is not null &&
        (_matchKind == MatchKind.Hotseat || state.PendingEnergyChoice.PlayerIndex == LocalPlayerIndexForMatch());

    private bool CanHumanResolveEnergySourceChoice(MatchState state) =>
        state.WinnerIndex is null &&
        state.PendingEnergySourceChoice is not null &&
        (_matchKind == MatchKind.Hotseat || state.PendingEnergySourceChoice.PlayerIndex == LocalPlayerIndexForMatch());

    private void DrawDecisionPromptOverlay()
    {
        if (!_modalInputActive || _engine is null)
        {
            return;
        }

        _drawingModal = true;
        try
        {
            Fill(new Rectangle(0, 0, VirtualWidth, VirtualHeight), new Color(0, 0, 0, 168));
            if (_engine.State.PendingEnergyChoice is not null && CanHumanResolveEnergyChoice(_engine.State))
            {
                DrawEnergyDecisionPrompt();
            }
            else if (_engine.State.PendingEnergySourceChoice is not null && CanHumanResolveEnergySourceChoice(_engine.State))
            {
                DrawEnergySourceDecisionPrompt();
            }
            else if (_engine.State.PendingTargetChoice is not null && CanHumanResolveTarget(_engine.State))
            {
                DrawTargetDecisionPrompt();
            }
            else if (_engine.State.PendingCombatAction is not null && CanHumanResolveCombatAction(_engine.State))
            {
                DrawCombatActionPrompt();
            }
            else if (_engine.State.PendingAttack is not null && CanHumanResolveBlock(_engine.State))
            {
                DrawBlockDecisionPrompt();
            }
        }
        finally
        {
            _drawingModal = false;
        }
    }

    private void DrawEnergyDecisionPrompt()
    {
        var choice = _engine!.State.PendingEnergyChoice!;
        var panel = new Rectangle(306, 148, 988, 596);
        DrawPanel(panel, new Color(29, 36, 46), border: new Color(124, 143, 166));
        DrawText("Resolve Card Effect", new Vector2(panel.X + 30, panel.Y + 24), Color.White, 0.94f);
        DrawPromptSourceCard(choice.CardId, choice.SourceInstanceId, new Rectangle(panel.X + 34, panel.Y + 82, 190, 266));
        DrawText(choice.Message, new Rectangle(panel.X + 252, panel.Y + 82, panel.Width - 286, 50), new Color(244, 230, 158), 0.66f);
        DrawScrollableText(PromptEffectText(choice.EffectText, choice.CardId), new Rectangle(panel.X + 252, panel.Y + 142, panel.Width - 286, 116), ref _cardDetailScrollOffset, new Color(211, 220, 231), PromptDetailTextScale);
        DrawText("Choose an element", new Vector2(panel.X + 252, panel.Y + 286), Color.White, 0.62f);

        var elements = _engine.State.Mode.Elements;
        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            var column = i % 4;
            var row = i / 4;
            var rect = new Rectangle(panel.X + 252 + column * 168, panel.Y + 326 + row * 62, 146, 44);
            var choicePlayer = _engine.State.Players[choice.PlayerIndex];
            var maxed = _engine.State.Mode.EnergyRules.UsesPersistentEnergySources
                ? choicePlayer.EnergyField.Count(card => _engine.State.DefinitionFor(card).Elements.Contains(element, StringComparer.OrdinalIgnoreCase)) >= _engine.State.Mode.EnergyRules.MaxPerElement
                : choicePlayer.EnergyPool.GetValueOrDefault(element) >= _engine.State.Mode.EnergyRules.MaxPerElement;
            if (Button(rect, element, !maxed))
            {
                if (ExecuteCommand("resolve-energy", element, () => _engine.ResolveEnergyChoice(element)))
                {
                    ClearSelections();
                }
            }
        }
    }

    private void DrawTargetDecisionPrompt()
    {
        var choice = _engine!.State.PendingTargetChoice!;
        var panel = new Rectangle(252, 118, 1096, 650);
        DrawPanel(panel, new Color(29, 36, 46), border: new Color(124, 143, 166));
        DrawText("Choose Effect Target", new Vector2(panel.X + 30, panel.Y + 24), Color.White, 0.94f);
        DrawPromptSourceCard(choice.CardId, choice.SourceInstanceId, new Rectangle(panel.X + 34, panel.Y + 82, 190, 266));
        DrawText(choice.Message, new Rectangle(panel.X + 252, panel.Y + 82, panel.Width - 286, 50), new Color(244, 230, 158), 0.66f);
        DrawScrollableText(PromptEffectText(choice.EffectText, choice.CardId), new Rectangle(panel.X + 252, panel.Y + 142, panel.Width - 286, 96), ref _cardDetailScrollOffset, new Color(211, 220, 231), PromptDetailTextScale);
        DrawText("Legal choices", new Vector2(panel.X + 252, panel.Y + 262), Color.White, 0.62f);

        var choices = LegalTargetChoices().Take(8).ToArray();
        for (var i = 0; i < choices.Length; i++)
        {
            var option = choices[i];
            var column = i % 4;
            var row = i / 4;
            var cardRect = new Rectangle(panel.X + 252 + column * 194, panel.Y + 300 + row * 150, 86, 120);
            DrawCardFrame(cardRect, option.Card, selected: true, exhausted: option.Instance.Exhausted, count: 0, compact: true);
            var button = new Rectangle(cardRect.Right + 8, cardRect.Y + 34, 82, 36);
            if (Button(button, "Choose"))
            {
                var payload = TargetPayload(option.Target);
                if (ExecuteCommand("target", payload, () => _engine.ResolveTargetChoice(option.Target)))
                {
                    ClearSelections();
                }
            }
        }
    }

    private void DrawEnergySourceDecisionPrompt()
    {
        var choice = _engine!.State.PendingEnergySourceChoice!;
        var player = _engine.State.Players[choice.PlayerIndex];
        var choices = player.EnergyField
            .Select((source, index) => (Source: source, Index: index, Card: _engine.State.DefinitionFor(source)))
            .Where(item => !item.Source.Exhausted && !item.Card.Elements.Contains(choice.DestinationElement, StringComparer.OrdinalIgnoreCase))
            .Take(8)
            .ToArray();
        var panel = new Rectangle(252, 118, 1096, 650);
        DrawPanel(panel, new Color(29, 36, 46), border: new Color(124, 143, 166));
        DrawText("Convert an Energy Source", new Vector2(panel.X + 30, panel.Y + 24), Color.White, 0.94f);
        DrawText(choice.Message, new Rectangle(panel.X + 30, panel.Y + 70, panel.Width - 60, 48), new Color(244, 230, 158), 0.66f);
        DrawText("Choose a ready source card", new Vector2(panel.X + 30, panel.Y + 132), Color.White, 0.62f);
        for (var index = 0; index < choices.Length; index++)
        {
            var option = choices[index];
            var column = index % 4;
            var row = index / 4;
            var cardRect = new Rectangle(panel.X + 34 + column * 262, panel.Y + 174 + row * 196, 112, 156);
            DrawCardFrame(cardRect, option.Card, selected: true, exhausted: false, count: 0, compact: true);
            DrawFittedText(SourceOriginLabel(option.Source.SourceOrigin), new Vector2(cardRect.X, cardRect.Bottom + 8), cardRect.Width, UiTheme.TextMuted, 0.36f, 0.24f);
            if (Button(new Rectangle(cardRect.X + 124, cardRect.Y + 56, 104, 42), "Convert"))
            {
                if (ExecuteCommand("resolve-energy-source", option.Source.Id, () => _engine.ResolveEnergySourceChoice(option.Source.Id)))
                {
                    ClearSelections();
                }
            }
        }
    }

    private static string SourceOriginLabel(EnergySourceOrigin origin) => origin switch
    {
        EnergySourceOrigin.BasicCard => "Played Basic Energy",
        EnergySourceOrigin.FreeAdd => "Free Add Energy",
        EnergySourceOrigin.Sacrifice => "Sacrifice reward",
        EnergySourceOrigin.Converted => "Converted source",
        _ => "Effect energy"
    };

    private void DrawBlockDecisionPrompt()
    {
        var state = _engine!.State;
        var pending = state.PendingAttack!;
        var attacker = state.Players[pending.AttackerPlayerIndex].UnitField.FirstOrDefault(card => card.Id == pending.AttackerInstanceId);
        var attackerCard = attacker is null ? null : state.DefinitionFor(attacker);
        var panel = new Rectangle(252, 126, 1096, 632);
        DrawPanel(panel, new Color(29, 36, 46), border: new Color(124, 143, 166));
        DrawText("Incoming Attack", new Vector2(panel.X + 30, panel.Y + 24), Color.White, 0.94f);
        if (attackerCard is not null)
        {
            DrawCardFrame(new Rectangle(panel.X + 34, panel.Y + 82, 190, 266), attackerCard, selected: true, exhausted: true, count: 0, compact: false);
        }

        DrawText($"{state.Players[pending.AttackerPlayerIndex].Name} attacked. Choose a blocker or take the hit.", new Rectangle(panel.X + 252, panel.Y + 82, panel.Width - 286, 56), new Color(244, 230, 158), 0.66f);
        DrawText("Cards that can block", new Vector2(panel.X + 252, panel.Y + 164), Color.White, 0.62f);
        var blockers = BlockerChoices().Take(5).ToArray();
        for (var i = 0; i < blockers.Length; i++)
        {
            var blocker = blockers[i];
            var cardRect = new Rectangle(panel.X + 252 + i * 134, panel.Y + 204, 94, 132);
            DrawCardFrame(cardRect, blocker.Card, selected: _selectedBlockerIndex == blocker.Index && blocker.CanBlock, exhausted: blocker.Instance.Exhausted, count: 0, compact: true);
            if (!blocker.CanBlock)
            {
                Fill(cardRect, new Color(0, 0, 0, 96));
            }

            var blockButton = new Rectangle(cardRect.X, cardRect.Bottom + 12, cardRect.Width, 34);
            if (Button(blockButton, blocker.CanBlock ? "Block" : blocker.Reason, blocker.CanBlock))
            {
                _selectedBlockerIndex = blocker.Index;
                if (ExecuteCommand("block", blocker.Index.ToString(), () => _engine.Block(blocker.Index)))
                {
                    ClearSelections();
                }
            }
        }

        if (blockers.Length == 0)
        {
            DrawText("No Units are on the defending field.", new Rectangle(panel.X + 252, panel.Y + 204, 420, 40), new Color(255, 190, 120), 0.54f);
        }

        if (Button(new Rectangle(panel.X + 252, panel.Bottom - 82, 156, 42), "No Block"))
        {
            if (ExecuteCommand("pass-block", "", _engine.PassBlock))
            {
                ClearSelections();
            }
        }
    }

    private void DrawCombatActionPrompt()
    {
        var state = _engine!.State;
        var action = state.PendingCombatAction!;
        var pending = state.PendingAttack;
        var attacker = pending is null
            ? null
            : state.Players[pending.AttackerPlayerIndex].UnitField.FirstOrDefault(card => card.Id == pending.AttackerInstanceId);
        var blocker = string.IsNullOrWhiteSpace(action.BlockerInstanceId) || pending is null
            ? null
            : state.Players[1 - pending.AttackerPlayerIndex].UnitField.FirstOrDefault(card => card.Id == action.BlockerInstanceId);
        var panel = new Rectangle(224, 116, 1152, 656);
        DrawPanel(panel, new Color(29, 36, 46), border: new Color(124, 143, 166));
        DrawText("Combat Action", new Vector2(panel.X + 30, panel.Y + 24), Color.White, 0.94f);
        DrawText($"{state.Players[action.PriorityPlayerIndex].Name} has priority.", new Rectangle(panel.X + 30, panel.Y + 64, panel.Width - 60, 34), new Color(244, 230, 158), 0.62f);

        if (attacker is not null)
        {
            DrawText("Attacker", new Vector2(panel.X + 34, panel.Y + 116), new Color(205, 214, 225), 0.48f);
            DrawCardFrame(new Rectangle(panel.X + 34, panel.Y + 144, 150, 210), state.DefinitionFor(attacker), selected: true, exhausted: attacker.Exhausted, count: 0, compact: false);
        }

        if (blocker is not null)
        {
            DrawText("Blocker", new Vector2(panel.X + 204, panel.Y + 116), new Color(205, 214, 225), 0.48f);
            DrawCardFrame(new Rectangle(panel.X + 204, panel.Y + 144, 150, 210), state.DefinitionFor(blocker), selected: true, exhausted: blocker.Exhausted, count: 0, compact: false);
        }
        else
        {
            DrawText("No blocker declared.", new Rectangle(panel.X + 204, panel.Y + 178, 170, 44), new Color(205, 214, 225), 0.5f);
        }

        DrawText("Combat actions", new Vector2(panel.X + 410, panel.Y + 116), Color.White, 0.62f);
        DrawText("Use a combat-timed ability from your field, or pass. Combat resolves after both players pass in sequence.", new Rectangle(panel.X + 410, panel.Y + 148, panel.Width - 450, 54), new Color(205, 214, 225), 0.46f);

        var abilities = _engine.GetActivatableAbilities(action.PriorityPlayerIndex).Take(6).ToArray();
        for (var i = 0; i < abilities.Length; i++)
        {
            var option = abilities[i];
            var column = i % 3;
            var row = i / 3;
            var rect = new Rectangle(panel.X + 410 + column * 226, panel.Y + 230 + row * 132, 206, 112);
            DrawPanel(rect, new Color(35, 43, 54), border: new Color(74, 92, 116));
            DrawRarityBadge(new Rectangle(rect.X + 10, rect.Y + 10, 18, 18), option.Card.Rarity, compact: true);
            DrawFittedText(option.Card.Name, new Vector2(rect.X + 36, rect.Y + 10), rect.Width - 48, Color.White, 0.42f, 0.22f);
            DrawFittedText(option.Ability.Name, new Vector2(rect.X + 12, rect.Y + 38), rect.Width - 24, new Color(244, 230, 158), 0.38f, 0.2f);
            DrawText(option.Ability.RulesText, new Rectangle(rect.X + 12, rect.Y + 60, rect.Width - 92, 42), new Color(205, 214, 225), 0.44f);
            if (Button(new Rectangle(rect.Right - 74, rect.Bottom - 36, 62, 26), "Use"))
            {
                var payload = $"{option.PlayerIndex}|{option.SourceInstanceId}|{option.Ability.Id}";
                if (ExecuteCommand("ability", payload, () => _engine.ActivateAbility(option.PlayerIndex, option.SourceInstanceId, option.Ability.Id)))
                {
                    ClearSelections();
                }
            }
        }

        if (abilities.Length == 0)
        {
            DrawText("No combat abilities are currently available.", new Rectangle(panel.X + 410, panel.Y + 230, 420, 42), new Color(205, 214, 225), 0.52f);
        }

        if (Button(new Rectangle(panel.Right - 190, panel.Bottom - 78, 150, 42), "Pass"))
        {
            if (ExecuteCommand("combat-pass", action.PriorityPlayerIndex.ToString(), () => _engine.PassCombatAction(action.PriorityPlayerIndex)))
            {
                ClearSelections();
            }
        }
    }

    private void DrawPromptSourceCard(string cardId, string instanceId, Rectangle rect)
    {
        if (PromptSourceCard(cardId, instanceId) is { } card)
        {
            DrawCardFrame(rect, card, selected: true, exhausted: false, count: 0, compact: false);
            return;
        }

        DrawPanel(rect, new Color(24, 29, 37), border: new Color(70, 84, 104));
        DrawFittedCenteredText("Effect", Inset(rect, 18), new Color(165, 176, 190), 0.76f, 0.4f);
    }

    private CardDefinition? PromptSourceCard(string cardId, string instanceId)
    {
        if (!string.IsNullOrWhiteSpace(cardId) && _engine!.State.Cards.TryGetValue(cardId, out var card))
        {
            return card;
        }

        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        return _engine!.State.Players
            .SelectMany(player => player.Hand.Concat(player.UnitField).Concat(player.SupportField).Concat(player.DiscardPile))
            .FirstOrDefault(instance => instance.Id.Equals(instanceId, StringComparison.OrdinalIgnoreCase)) is { } instance
                ? _engine.State.DefinitionFor(instance)
                : null;
    }

    private string PromptEffectText(string effectText, string cardId)
    {
        if (!string.IsNullOrWhiteSpace(effectText))
        {
            return effectText;
        }

        return !string.IsNullOrWhiteSpace(cardId) && _engine!.State.Cards.TryGetValue(cardId, out var card)
            ? CardDetailText(card)
            : "Resolve the pending card effect.";
    }

    private IEnumerable<(ZoneRef Target, CardInstance Instance, CardDefinition Card)> LegalTargetChoices()
    {
        if (_engine is null)
        {
            yield break;
        }

        for (var playerIndex = 0; playerIndex < _engine.State.Players.Count; playerIndex++)
        {
            var player = _engine.State.Players[playerIndex];
            for (var index = 0; index < player.UnitField.Count; index++)
            {
                var target = new ZoneRef(playerIndex, "UnitField", index);
                if (_engine.CanResolveTargetChoice(target))
                {
                    var instance = player.UnitField[index];
                    yield return (target, instance, _engine.State.DefinitionFor(instance));
                }
            }

            for (var index = 0; index < player.SupportField.Count; index++)
            {
                var target = new ZoneRef(playerIndex, "SupportField", index);
                if (_engine.CanResolveTargetChoice(target))
                {
                    var instance = player.SupportField[index];
                    yield return (target, instance, _engine.State.DefinitionFor(instance));
                }
            }
        }
    }

    private IEnumerable<(int Index, CardInstance Instance, CardDefinition Card, bool CanBlock, string Reason)> BlockerChoices()
    {
        if (_engine is null || BlockingPlayer(_engine.State) is not { } player)
        {
            yield break;
        }

        for (var index = 0; index < player.UnitField.Count; index++)
        {
            var instance = player.UnitField[index];
            var canBlock = _engine.CanBlock(index);
            yield return (index, instance, _engine.State.DefinitionFor(instance), canBlock, BlockerDisabledReason(instance, canBlock));
        }
    }

    private static string BlockerDisabledReason(CardInstance instance, bool canBlock)
    {
        if (canBlock)
        {
            return "";
        }

        return instance.Exhausted ? "Exhausted" : "Cannot block";
    }

    private void DrawTutorialStepOverlay()
    {
        if (_tutorial is null || _screen != Screen.Match || _modalInputActive)
        {
            return;
        }

        var step = _tutorial.CurrentStep;
        var rect = new Rectangle(54, 92, 540, 112);
        DrawPanel(rect, new Color(24, 31, 40, 235), border: new Color(244, 230, 158));
        DrawText($"{_tutorial.Definition.Name}  {_tutorial.StepIndex + 1}/{_tutorial.Definition.Steps.Count}", new Vector2(rect.X + 18, rect.Y + 14), Color.White, 0.52f);
        DrawText(step?.Title ?? "Complete", new Vector2(rect.X + 18, rect.Y + 42), new Color(244, 230, 158), 0.54f);
        DrawText(step?.Instruction ?? "Tutorial complete.", new Rectangle(rect.X + 18, rect.Y + 68, rect.Width - 36, 34), new Color(205, 214, 225), 0.42f);
    }

    private Point ZoneCenter(ZoneRef? zone, int fallbackPlayerIndex)
    {
        if (_engine is null)
        {
            return new Point(VirtualWidth / 2, VirtualHeight / 2);
        }

        var zoneValue = zone.GetValueOrDefault(new ZoneRef(fallbackPlayerIndex, ""));
        var playerIndex = zone.HasValue && zoneValue.PlayerIndex >= 0 ? zoneValue.PlayerIndex : fallbackPlayerIndex;
        var board = BoardRectForPlayer(playerIndex);
        var zoneName = zoneValue.Zone;
        if (zoneName.Equals("Hand", StringComparison.OrdinalIgnoreCase))
        {
            var index = Math.Max(0, zoneValue.Index);
            return HandCardRect(index).Center;
        }

        if (zoneName.Equals("UnitField", StringComparison.OrdinalIgnoreCase))
        {
            return CardSlotCenter(new Rectangle(board.X + 640, board.Y + 58, 528, 150), zoneValue.Index);
        }

        if (zoneName.Equals("SupportField", StringComparison.OrdinalIgnoreCase))
        {
            return CardSlotCenter(new Rectangle(board.X + 212, board.Y + 58, 408, 150), zoneValue.Index);
        }

        if (zoneName.Equals("EnergyPool", StringComparison.OrdinalIgnoreCase))
        {
            return new Rectangle(board.X + 18, board.Y + 58, 174, 168).Center;
        }

        if (zoneName.Equals("EnergyField", StringComparison.OrdinalIgnoreCase))
        {
            return new Rectangle(board.X + 18, board.Y + 58, 174, 168).Center;
        }

        if (zoneName.Equals("DamageZone", StringComparison.OrdinalIgnoreCase))
        {
            return new Rectangle(board.Right - 164, board.Y + 42, 84, 30).Center;
        }

        if (zoneName.Equals("DiscardPile", StringComparison.OrdinalIgnoreCase))
        {
            return new Rectangle(board.Right - 74, board.Y + 16, 58, 30).Center;
        }

        if (zoneName.Equals("Deck", StringComparison.OrdinalIgnoreCase))
        {
            return new Rectangle(board.Right - 164, board.Y + 16, 58, 30).Center;
        }

        return board.Center;
    }

    private Rectangle BoardRectForPlayer(int playerIndex)
    {
        if (_engine is null)
        {
            return ActiveBoardRect();
        }

        var bottomPlayerIndex = LocalBoardPlayerIndex(_engine.State);
        return playerIndex == bottomPlayerIndex
            ? ActiveBoardRect()
            : new Rectangle(34, 180, 1212, 238);
    }

    private static Point CardSlotCenter(Rectangle zone, int index)
    {
        var slotCount = 5;
        var gap = 8;
        var cardWidth = Math.Min(84, Math.Max(58, (zone.Width - 24 - gap * (slotCount - 1)) / slotCount));
        var cardHeight = Math.Min(116, zone.Height - 28);
        var spacing = cardWidth + gap;
        var x = zone.X + 12 + Math.Clamp(index, 0, 4) * spacing + cardWidth / 2;
        var y = zone.Y + 16 + cardHeight / 2;
        return new Point(x, y);
    }

    private static float EaseOutCubic(float value)
    {
        var inverted = 1f - Math.Clamp(value, 0f, 1f);
        return 1f - inverted * inverted * inverted;
    }

    private static Point LerpPoint(Point from, Point to, float amount) => new(
        (int)MathF.Round(MathHelper.Lerp(from.X, to.X, amount)),
        (int)MathF.Round(MathHelper.Lerp(from.Y, to.Y, amount)));

    private static string SignedAmount(MatchEvent matchEvent) =>
        matchEvent.Kind == MatchEventKind.EnergySpent ? $"-{matchEvent.Amount}" : $"+{matchEvent.Amount}";

    private static Color PresentationColor(MatchEvent matchEvent)
    {
        if (!string.IsNullOrWhiteSpace(matchEvent.Element))
        {
            return ElementColor(matchEvent.Element);
        }

        return matchEvent.Kind switch
        {
            MatchEventKind.CardDrawn => new Color(132, 180, 232),
            MatchEventKind.CardPlayed => new Color(244, 230, 158),
            MatchEventKind.CardSacrificed => new Color(148, 224, 164),
            MatchEventKind.DamageTaken => new Color(235, 92, 76),
            MatchEventKind.AttackDeclared or MatchEventKind.BlockDeclared or MatchEventKind.CombatActionQueued or MatchEventKind.CombatActionPassed or MatchEventKind.CombatResolved => new Color(255, 172, 100),
            MatchEventKind.PhaseChanged => new Color(160, 186, 220),
            _ => new Color(205, 214, 225)
        };
    }

    private void DrawElementPicker()
    {
        if (_engine is null)
        {
            return;
        }

        var pendingChoice = _engine.State.PendingEnergyChoice;
        if (!_chooseFreeEnergy && pendingChoice is not null)
        {
            return;
        }

        if (!_chooseFreeEnergy && pendingChoice is null)
        {
            return;
        }

        if (pendingChoice is not null && _matchKind != MatchKind.Hotseat && pendingChoice.PlayerIndex != LocalPlayerIndexForMatch())
        {
            return;
        }

        Fill(new Rectangle(0, 0, VirtualWidth, VirtualHeight), new Color(0, 0, 0, 150));
        var panel = new Rectangle(430, 230, 740, 390);
        DrawPanel(panel, new Color(29, 36, 46), border: new Color(124, 143, 166));
        var title = pendingChoice?.Message ?? "Choose one element to add to your energy pool.";
        DrawText("Choose Energy", new Vector2(panel.X + 34, panel.Y + 28), Color.White, 1.05f);
        DrawText(title, new Rectangle(panel.X + 34, panel.Y + 72, panel.Width - 68, 48), new Color(211, 220, 231), 0.72f);

        var elements = _engine.State.Mode.Elements;
        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            var column = i % 4;
            var row = i / 4;
            var rect = new Rectangle(panel.X + 44 + column * 166, panel.Y + 146 + row * 86, 140, 56);
            var count = _engine.State.Players[pendingChoice?.PlayerIndex ?? _engine.State.ActivePlayerIndex].EnergyPool.GetValueOrDefault(element);
            var maxed = count >= _engine.State.Mode.EnergyRules.MaxPerElement;
            var canChoose = pendingChoice is not null ? !maxed : _engine.CanAddEnergy(element);
            Fill(rect, Color.Lerp(ElementColor(element), Color.Black, maxed ? 0.55f : canChoose && rect.Contains(_virtualMouse) ? 0.02f : 0.12f));
            Border(rect, maxed ? new Color(255, 190, 120) : canChoose ? new Color(220, 229, 240) : new Color(92, 101, 113), maxed || canChoose && rect.Contains(_virtualMouse) ? 2 : 1);
            DrawText(element, new Vector2(rect.X + 12, rect.Y + 9), Color.White, 0.62f);
            DrawText($"{count}/10", new Vector2(rect.Right - 48, rect.Y + 30), Color.White, 0.5f);

            if (canChoose && Hit(rect))
            {
                var commandKind = _chooseFreeEnergy ? "add-energy" : "resolve-energy";
                if (ExecuteCommand(commandKind, element, () => _chooseFreeEnergy ? _engine.AddEnergy(element) : _engine.ResolveEnergyChoice(element)))
                {
                    _chooseFreeEnergy = false;
                }
            }
        }

        if (Button(new Rectangle(panel.Right - 142, panel.Bottom - 58, 104, 34), "Cancel"))
        {
            _chooseFreeEnergy = false;
            if (_engine.State.PendingEnergyChoice is not null)
            {
                _status = "Resolve the pending energy choice to continue.";
            }
        }
    }

    private void DrawStatusBar()
    {
        Fill(new Rectangle(0, 862, VirtualWidth, 38), new Color(24, 28, 36));
        Border(new Rectangle(0, 862, VirtualWidth, 38), new Color(58, 70, 88), 1);
        DrawText(_status, new Vector2(34, 872), new Color(207, 216, 228), 0.68f);
    }

    private void DrawPanel(Rectangle rect, Color color, Color? border = null)
    {
        Fill(rect, color);
        Border(rect, border ?? new Color(70, 82, 98), 1);
    }

    private bool Button(Rectangle rect, string label, bool enabled = true, bool selected = false, bool focused = false)
    {
        var hitTarget = UiTheme.MinimumHitTarget(rect);
        var clicked = enabled && Hit(hitTarget);
        var hover = enabled && hitTarget.Contains(_virtualMouse);
        if (hover)
        {
            _buttonHoveredThisFrame = true;
            if (_hoveredButtonRect != rect)
            {
                _hoveredButtonRect = rect;
                _audio.Play(SoundKeys.UiHover, throttleSeconds: 0.08);
            }
        }

        var controlState = UiControlState.None;
        if (hover) controlState |= UiControlState.Hovered;
        if (focused) controlState |= UiControlState.Focused;
        if (selected) controlState |= UiControlState.Selected;
        if (!enabled) controlState |= UiControlState.Disabled;
        if (hover && _mouse.LeftButton == ButtonState.Pressed) controlState |= UiControlState.Pressed;
        var palette = UiTheme.ControlPalette(controlState);
        Fill(rect, palette.Fill);
        Border(rect, palette.Border, focused ? 3 : selected ? 2 : 1);
        if (hover || focused)
        {
            Fill(new Rectangle(rect.X + 2, rect.Y + 2, Math.Max(0, rect.Width - 4), 2), palette.Accent);
        }
        var scale = rect.Height < 34 ? 0.6f : 0.66f;
        DrawFittedCenteredText(label, Inset(rect, 8), palette.Text, scale, 0.42f);
        if (clicked)
        {
            _audio.Play(SoundKeys.UiClick);
        }

        return clicked;
    }

    private Color ApplyDrawOpacity(Color color) => _drawOpacity >= 0.999f
        ? color
        : color * Math.Clamp(_drawOpacity, 0f, 1f);

    private void DrawWithOpacity(float opacity, Action draw)
    {
        var previousOpacity = _drawOpacity;
        _drawOpacity *= Math.Clamp(opacity, 0f, 1f);
        try
        {
            draw();
        }
        finally
        {
            _drawOpacity = previousOpacity;
        }
    }

    private void Fill(Rectangle rect, Color color) => _spriteBatch!.Draw(_pixel!, rect, ApplyDrawOpacity(color));

    private void DrawImageContain(Texture2D texture, Rectangle bounds, Color color)
    {
        var scale = Math.Min(bounds.Width / (float)texture.Width, bounds.Height / (float)texture.Height);
        var width = Math.Max(1, (int)MathF.Round(texture.Width * scale));
        var height = Math.Max(1, (int)MathF.Round(texture.Height * scale));
        var rect = new Rectangle(bounds.Center.X - width / 2, bounds.Center.Y - height / 2, width, height);
        _spriteBatch!.Draw(texture, rect, ApplyDrawOpacity(color));
    }

    private void DrawImageCover(Texture2D texture, Rectangle bounds, Color color)
    {
        var sourceAspect = texture.Width / (float)texture.Height;
        var targetAspect = bounds.Width / (float)bounds.Height;
        var source = new Rectangle(0, 0, texture.Width, texture.Height);
        if (sourceAspect > targetAspect)
        {
            source.Width = Math.Max(1, (int)MathF.Round(texture.Height * targetAspect));
            source.X = (texture.Width - source.Width) / 2;
        }
        else if (sourceAspect < targetAspect)
        {
            source.Height = Math.Max(1, (int)MathF.Round(texture.Width / targetAspect));
            source.Y = (texture.Height - source.Height) / 2;
        }

        _spriteBatch!.Draw(texture, bounds, source, ApplyDrawOpacity(color));
    }

    private void Border(Rectangle rect, Color color, int thickness)
    {
        Fill(new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
        Fill(new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
        Fill(new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
        Fill(new Rectangle(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
    }

    private void DrawText(string text, Vector2 position, Color color, float scale) =>
        _spriteBatch!.DrawString(_font!, text, position, ApplyDrawOpacity(color), 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);

    private void DrawCenteredText(string text, Rectangle bounds, Color color, float scale)
    {
        var measured = _font!.MeasureString(text) * scale;
        var position = new Vector2(
            bounds.Center.X - measured.X / 2f,
            bounds.Center.Y - measured.Y / 2f);
        DrawText(text, position, color, scale);
    }

    private void DrawFittedCenteredText(string text, Rectangle bounds, Color color, float preferredScale, float minimumScale)
    {
        var scale = preferredScale;
        while (scale > minimumScale && _font!.MeasureString(text).X * scale > bounds.Width)
        {
            scale -= 0.02f;
        }

        DrawCenteredText(text, bounds, color, Math.Max(minimumScale, scale));
    }

    private void DrawFittedText(string text, Vector2 position, int maxWidth, Color color, float preferredScale, float minimumScale)
    {
        var scale = preferredScale;
        while (scale > minimumScale && _font!.MeasureString(text).X * scale > maxWidth)
        {
            scale -= 0.02f;
        }

        DrawText(text, position, color, Math.Max(minimumScale, scale));
    }

    private void DrawText(string text, Rectangle bounds, Color color, float scale)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = "";
        var y = bounds.Y;
        var lineHeight = MathF.Ceiling(_font!.LineSpacing * scale * 1.18f);
        foreach (var word in words)
        {
            var candidate = string.IsNullOrEmpty(line) ? word : $"{line} {word}";
            if (_font.MeasureString(candidate).X * scale > bounds.Width && !string.IsNullOrEmpty(line))
            {
                DrawText(line, new Vector2(bounds.X, y), color, scale);
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
            DrawText(line, new Vector2(bounds.X, y), color, scale);
        }
    }

    private void DrawCardFrameRulesText(CardDefinition card, Rectangle bounds, Color color, float scale)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var lineHeight = Math.Max(8, (int)MathF.Ceiling(_font!.LineSpacing * scale * 1.12f));
        var maxLines = Math.Max(1, bounds.Height / lineHeight);
        var summary = CardFrameRulesText(card, maxLines);
        var lines = WrappedLines(summary.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries), bounds.Width, scale).ToArray();
        if (lines.Length == 0)
        {
            return;
        }

        var visible = lines.Take(maxLines).ToArray();
        if (lines.Length > visible.Length)
        {
            visible[^1] = FittedEllipsis(visible[^1], bounds.Width, scale);
        }

        var y = bounds.Y;
        foreach (var line in visible)
        {
            if (y + lineHeight > bounds.Bottom)
            {
                break;
            }

            DrawText(line, new Vector2(bounds.X, y), color, scale);
            y += lineHeight;
        }
    }

    private void DrawScrollableText(string text, Rectangle bounds, ref int scrollOffset, Color color, float scale)
    {
        Fill(bounds, new Color(22, 27, 35, 150));
        Border(bounds, new Color(66, 80, 98), 1);
        var content = Inset(bounds, 8);
        var sourceLines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lines = _textLayoutCache?.Wrap(text, content.Width - 8, scale).ToArray() ?? WrappedLines(sourceLines, content.Width - 8, scale).ToArray();
        var lineHeight = Math.Max(10, (int)MathF.Ceiling(_font!.LineSpacing * scale * 1.18f));
        var visibleCount = Math.Max(1, content.Height / lineHeight);
        var state = TextScrollState(bounds);
        state.Configure(lines.Length, visibleCount);
        state.SetOffset(scrollOffset);
        scrollOffset = state.Offset;
        var y = content.Y;
        for (var i = scrollOffset; i < Math.Min(lines.Length, scrollOffset + visibleCount); i++)
        {
            DrawText(lines[i], new Vector2(content.X, y), color, scale);
            y += lineHeight;
        }

        DrawUxScrollBar($"text-{bounds.X}-{bounds.Y}-{bounds.Width}-{bounds.Height}",
            new Rectangle(bounds.Right - 8, content.Y, 5, content.Height), state, UiTheme.Focus);
        scrollOffset = state.Offset;
    }

    private string FittedEllipsis(string text, int maxWidth, float scale)
    {
        const string ellipsis = "...";
        var candidate = text.EndsWith(ellipsis, StringComparison.Ordinal) ? text : $"{text}{ellipsis}";
        while (candidate.Length > ellipsis.Length && _font!.MeasureString(candidate).X * scale > maxWidth)
        {
            candidate = $"{candidate[..^4].TrimEnd()}{ellipsis}";
        }

        return candidate;
    }

    private IEnumerable<string> WrappedLines(IEnumerable<string> entries, int maxWidth, float scale)
    {
        foreach (var entry in entries)
        {
            var words = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var line = "";
            foreach (var word in words)
            {
                var candidate = string.IsNullOrEmpty(line) ? word : $"{line} {word}";
                if (_font!.MeasureString(candidate).X * scale > maxWidth && !string.IsNullOrEmpty(line))
                {
                    yield return line;
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }

            if (!string.IsNullOrEmpty(line))
            {
                yield return line;
            }
        }
    }

    private bool Hit(Rectangle rect, bool allowPromptBoardPassthrough = false)
    {
        if (_clickConsumed ||
            _draggedHandCard is not null ||
            _mouse.LeftButton != ButtonState.Pressed ||
            _previousMouse.LeftButton == ButtonState.Pressed ||
            !rect.Contains(_virtualMouse))
        {
            return false;
        }

        if (_modalInputActive && !_drawingModal)
        {
            var panel = ActiveDecisionPromptPanel();
            if (!allowPromptBoardPassthrough || panel is not null && panel.Value.Contains(_virtualMouse))
            {
                return false;
            }
        }

        _usingController = false;
        _clickConsumed = true;
        return true;
    }

    private Rectangle? ActiveDecisionPromptPanel()
    {
        if (!_modalInputActive || _engine is null)
        {
            return null;
        }

        if (_engine.State.PendingEnergyChoice is not null && CanHumanResolveEnergyChoice(_engine.State))
        {
            return new Rectangle(306, 148, 988, 596);
        }

        if (_engine.State.PendingEnergySourceChoice is not null && CanHumanResolveEnergySourceChoice(_engine.State))
        {
            return new Rectangle(252, 118, 1096, 650);
        }

        if (_engine.State.PendingTargetChoice is not null && CanHumanResolveTarget(_engine.State))
        {
            return new Rectangle(252, 118, 1096, 650);
        }

        if (_engine.State.PendingCombatAction is not null && CanHumanResolveCombatAction(_engine.State))
        {
            return new Rectangle(224, 116, 1152, 656);
        }

        if (_engine.State.PendingAttack is not null && CanHumanResolveBlock(_engine.State))
        {
            return new Rectangle(252, 126, 1096, 632);
        }

        return null;
    }

    private void HandleMouseDrag()
    {
        if (_screen != Screen.Match || _engine is null || !CanHumanUseActions(_engine.State))
        {
            _draggedHandCard = null;
            return;
        }

        if (_mouse.LeftButton == ButtonState.Pressed &&
            _previousMouse.LeftButton != ButtonState.Pressed &&
            _draggedHandCard is null)
        {
            var active = _engine.State.ActivePlayer;
            _handStripScroll.Configure(active.Hand.Count, 9);
            for (var i = _handStripScroll.Offset; i < Math.Min(active.Hand.Count, _handStripScroll.Offset + 9); i++)
            {
                var rect = HandCardRect(i);
                if (rect.Contains(_virtualMouse))
                {
                    _selectedHandIndex = i;
                    _selectedUnitIndex = -1;
                    _selectedSupportIndex = -1;
                    _selectedBlockerIndex = -1;
                    _matchFocus = MatchFocus.Hand;
                    _cardDetailScrollOffset = 0;
                    _draggedHandCard = new DraggedHandCard(i, _virtualMouse - rect.Location);
                    _clickConsumed = true;
                    return;
                }
            }
        }

        if (_draggedHandCard is not null &&
            _mouse.LeftButton != ButtonState.Pressed &&
            _previousMouse.LeftButton == ButtonState.Pressed)
        {
            ResolveHandDrop(_draggedHandCard.HandIndex, _virtualMouse);
            _draggedHandCard = null;
        }
    }

    private void HandleLogScroll()
    {
        var delta = _mouse.ScrollWheelValue - _previousMouse.ScrollWheelValue;
        if (delta == 0)
        {
            return;
        }

        var direction = delta < 0 ? 1 : -1;

        if (_screen == Screen.Store)
        {
            if (new Rectangle(54, 230, 720, 528).Contains(_virtualMouse))
            {
                ClampActiveStoreSelection(ensureVisible: false);
                ActiveStoreScroll.ScrollBy(direction);
                _storeScrollOffset = ActiveStoreScroll.Offset;
                _usingController = false;
                return;
            }

            if (new Rectangle(798, 198, 746, 560).Contains(_virtualMouse))
            {
                _cardDetailScrollOffset += direction;
                _cardDetailScrollOffset = Math.Max(0, _cardDetailScrollOffset);
                _usingController = false;
                return;
            }
        }

        if (_screen == Screen.ModeSelect && new Rectangle(54, 198, 520, 560).Contains(_virtualMouse))
        {
            _modeListScroll.ScrollBy(direction);
            _usingController = false;
            return;
        }

        if (_screen == Screen.Tutorials && new Rectangle(54, 198, 690, 560).Contains(_virtualMouse))
        {
            _tutorialListScroll.ScrollBy(direction);
            _usingController = false;
            return;
        }

        if (_screen == Screen.Options && new Rectangle(88, 248, 720, 414).Contains(_virtualMouse))
        {
            _optionsListScroll.ScrollBy(direction);
            _optionsFocusVisibilityPending = false;
            _usingController = false;
            return;
        }

        if (_screen == Screen.DeckBuilder && new Rectangle(42, 246, 920, 548).Contains(_virtualMouse))
        {
            _deckGridScroll.ScrollBy(direction);
            _deckFocusVisibilityPending = false;
            _usingController = false;
            return;
        }

        if (_screen == Screen.DeckBuilder && new Rectangle(1248, 160, 280, 270).Contains(_virtualMouse))
        {
            _cardDetailScrollOffset += direction;
            _cardDetailScrollOffset = Math.Max(0, _cardDetailScrollOffset);
            _usingController = false;
            return;
        }

        if (_screen == Screen.PackOpening && new Rectangle(54, 198, 1490, 560).Contains(_virtualMouse))
        {
            _packGridScroll.ScrollBy(direction);
            _packOpeningScrollOffset = _packGridScroll.Offset;
            _packFocusVisibilityPending = false;
            _usingController = false;
            return;
        }

        if (_screen != Screen.Match || _engine is null)
        {
            _logScrollOffset = 0;
            return;
        }

        if (_matchHistoryOpen)
        {
            _matchHistoryScrollOffset = Math.Max(0, _matchHistoryScrollOffset + direction * 3);
            _usingController = false;
            return;
        }

        if (HandAreaRect().Contains(_virtualMouse))
        {
            _handStripScroll.ScrollBy(direction);
            _usingController = false;
            return;
        }

        if (_engine.State.Mode.EnergyRules.UsesPersistentEnergySources && AddEnergyDropRect().Contains(_virtualMouse))
        {
            var maxOffset = Math.Max(0, _engine.State.ActivePlayer.EnergyField.Count - 6);
            _energyFieldScrollOffset = Math.Clamp(_energyFieldScrollOffset + direction, 0, maxOffset);
            _usingController = false;
            return;
        }

        if (new Rectangle(1282, 350, 266, 150).Contains(_virtualMouse))
        {
            _cardDetailScrollOffset += direction;
            _cardDetailScrollOffset = Math.Max(0, _cardDetailScrollOffset);
            _usingController = false;
            return;
        }

        if (!MatchLogRect().Contains(_virtualMouse))
        {
            return;
        }

        _compactLogScroll.ScrollBy(direction);
        _logScrollOffset = Math.Max(0, _compactLogScroll.MaxOffset - _compactLogScroll.Offset);
        _usingController = false;
    }

    private void ResolveHandDrop(int handIndex, Point dropPoint)
    {
        if (_engine is null || !IsValidHandIndex(handIndex))
        {
            return;
        }

        var card = _engine.State.DefinitionFor(_engine.State.ActivePlayer.Hand[handIndex]);
        if (AddEnergyDropRect().Contains(dropPoint))
        {
            if (BasicEnergy.IsBasicEnergyCard(card))
            {
                PlayEnergyFromHand(handIndex);
                return;
            }

            _status = "Drop a Basic Energy card here. The free Add Energy action remains available separately.";
            return;
        }

        if (UnitDropRect().Contains(dropPoint))
        {
            if (!card.Type.Equals("Unit", StringComparison.OrdinalIgnoreCase))
            {
                _status = "Only Units can be dropped on the unit field.";
                return;
            }

            if (IsZoneFullForCard(card))
            {
                BeginReplacement(handIndex, card);
                return;
            }

            PlayCardFromHand(handIndex);
            return;
        }

        if (SupportDropRect().Contains(dropPoint))
        {
            if (!card.Type.Equals("Support", StringComparison.OrdinalIgnoreCase))
            {
                _status = "Only Supports can be dropped on the support row.";
                return;
            }

            if (IsZoneFullForCard(card))
            {
                BeginReplacement(handIndex, card);
                return;
            }

            PlayCardFromHand(handIndex);
            return;
        }

        if (CastDropRect().Contains(dropPoint))
        {
            if (!card.Type.Equals("Spell", StringComparison.OrdinalIgnoreCase))
            {
                _status = "Only Spells can be dropped on the cast lane.";
                return;
            }

            PlayCardFromHand(handIndex);
        }
    }

    private bool CanDropDraggedCardAs(string expectedType)
    {
        if (_engine is null || _draggedHandCard is null || !IsValidHandIndex(_draggedHandCard.HandIndex) || !CanHumanUseActions(_engine.State))
        {
            return false;
        }

        var card = _engine.State.DefinitionFor(_engine.State.ActivePlayer.Hand[_draggedHandCard.HandIndex]);
        return card.Type.Equals(expectedType, StringComparison.OrdinalIgnoreCase) &&
            (_engine.CanPlayCardFromHand(_draggedHandCard.HandIndex) || IsZoneFullForCard(card));
    }

    private bool Pressed(Keys key) => _keyboard.IsKeyDown(key) && !_previousKeyboard.IsKeyDown(key);

    private bool IsDown(Keys key) => _keyboard.IsKeyDown(key);

    private bool Pressed(Buttons button)
    {
        if (_gamePad.IsButtonDown(button) && !_previousGamePad.IsButtonDown(button))
        {
            return true;
        }

        var textEntryActive = _screen is Screen.PlayerCreation or Screen.Multiplayer || _profileRenameActive || _deckNameEditing || _storeSearchActive;
        return button switch
        {
            Buttons.A => Pressed(Keys.Enter) || !textEntryActive && Pressed(Keys.Space),
            Buttons.B => _uiActions.Triggered(UiAction.Back),
            Buttons.LeftShoulder => _uiActions.Triggered(UiAction.PagePrevious),
            Buttons.RightShoulder => _uiActions.Triggered(UiAction.PageNext),
            Buttons.Back => _uiActions.Triggered(UiAction.History),
            _ => false
        };
    }

    private bool DirectionPressed(Buttons negative, Buttons positive, out int delta)
    {
        delta = 0;
        var negativeAction = negative switch
        {
            Buttons.DPadUp => UiAction.NavigateUp,
            Buttons.DPadLeft => UiAction.NavigateLeft,
            _ => UiAction.None
        };
        var positiveAction = positive switch
        {
            Buttons.DPadDown => UiAction.NavigateDown,
            Buttons.DPadRight => UiAction.NavigateRight,
            _ => UiAction.None
        };
        var negativeCount = negativeAction == UiAction.None ? 0 : _uiActions.TriggerCount(negativeAction);
        var positiveCount = positiveAction == UiAction.None ? 0 : _uiActions.TriggerCount(positiveAction);
        if (negativeCount > 0)
        {
            delta = -negativeCount;
        }
        else if (positiveCount > 0)
        {
            delta = positiveCount;
        }

        if (delta != 0)
        {
            _usingController = true;
            return true;
        }

        return false;
    }

    private bool FocusPressed(out int delta)
    {
        delta = _uiActions.Triggered(UiAction.FocusPrevious)
            ? -1
            : _uiActions.Triggered(UiAction.FocusNext) ? 1 : 0;
        if (delta == 0)
        {
            return false;
        }

        _usingController = true;
        return true;
    }

    private void RestoreScrollableFocusForKeyboardOrController()
    {
        if (_usingController || !HasFocusBearingUiAction())
        {
            return;
        }

        _usingController = true;
        if (_screen == Screen.Store && _storeFocusArea == StoreFocusArea.Catalog)
        {
            var items = FilteredStoreCatalog();
            ClampActiveStoreSelection(ensureVisible: false);
            if (items.Count > 0 && !ActiveStoreScroll.VisibleRange.Contains(_storeFocus))
            {
                _storeFocus = ActiveStoreScroll.VisibleRange.Clamp(_storeFocus, items.Count);
                _storeCategoryFocus[(int)_storeCategory] = _storeFocus;
                _cardDetailScrollOffset = 0;
            }
            return;
        }

        if (_screen == Screen.DeckBuilder && _deckFocusArea == DeckFocusArea.Grid)
        {
            const int columns = 6;
            var cards = _deckBuilder.FilteredCards;
            _deckGridScroll.Configure(Math.Max(1, (int)Math.Ceiling(cards.Count / (double)columns)), 2);
            var visibleCards = new VisibleRange(_deckGridScroll.Offset * columns, _deckGridScroll.VisibleRange.Count * columns);
            if (cards.Count > 0 && !visibleCards.Contains(_deckFocusIndex))
            {
                _deckFocusIndex = visibleCards.Clamp(_deckFocusIndex, cards.Count);
                _deckBuilder.SelectedCardId = cards[_deckFocusIndex].Id;
                _cardDetailScrollOffset = 0;
            }
            return;
        }

        if (_screen == Screen.PackOpening && _lastBoosterOpening is not null)
        {
            const int columns = 8;
            var count = _lastBoosterOpening.Cards.Count;
            _packGridScroll.Configure(Math.Max(1, (int)Math.Ceiling(count / (double)columns)), 2);
            var visibleCards = new VisibleRange(_packGridScroll.Offset * columns, _packGridScroll.VisibleRange.Count * columns);
            if (count > 0 && !visibleCards.Contains(_packFocusIndex))
            {
                _packFocusIndex = visibleCards.Clamp(_packFocusIndex, count);
            }
            return;
        }

        if (_screen == Screen.Options && _optionsFocus <= 7)
        {
            _optionsListScroll.Configure(9, 6);
            if (!_optionsListScroll.VisibleRange.Contains(OptionsLogicalRow(_optionsFocus)))
            {
                _optionsFocus = OptionsFocusForLogicalRow(_optionsListScroll.VisibleRange.Clamp(OptionsLogicalRow(_optionsFocus), 9));
            }
            return;
        }

        if (_screen == Screen.ModeSelect)
        {
            _modeListScroll.Configure(PlayableModeCatalog.All.Count, 6);
            if (!_modeListScroll.VisibleRange.Contains(_modeFocus))
            {
                _modeFocus = _modeListScroll.VisibleRange.Clamp(_modeFocus, PlayableModeCatalog.All.Count);
            }
            return;
        }

        if (_screen == Screen.Tutorials)
        {
            _tutorialListScroll.Configure(TutorialDefinitions.Count, 6);
            if (!_tutorialListScroll.VisibleRange.Contains(_tutorialFocus))
            {
                _tutorialFocus = _tutorialListScroll.VisibleRange.Clamp(_tutorialFocus, TutorialDefinitions.Count);
            }
            return;
        }

        if (_screen == Screen.Match && _engine is not null && _matchFocus == MatchFocus.Hand)
        {
            var hand = VisibleHandPlayer(_engine.State).Hand;
            _handStripScroll.Configure(hand.Count, 9);
            if (hand.Count > 0 && !_handStripScroll.VisibleRange.Contains(_selectedHandIndex))
            {
                _selectedHandIndex = _handStripScroll.VisibleRange.Clamp(_selectedHandIndex, hand.Count);
                _cardDetailScrollOffset = 0;
            }
        }
    }

    private bool HasFocusBearingUiAction() =>
        _uiActions.Triggered(UiAction.NavigateUp) ||
        _uiActions.Triggered(UiAction.NavigateDown) ||
        _uiActions.Triggered(UiAction.NavigateLeft) ||
        _uiActions.Triggered(UiAction.NavigateRight) ||
        _uiActions.Triggered(UiAction.FocusPrevious) ||
        _uiActions.Triggered(UiAction.FocusNext) ||
        _uiActions.Triggered(UiAction.Confirm) ||
        _uiActions.Triggered(UiAction.PagePrevious) ||
        _uiActions.Triggered(UiAction.PageNext) ||
        _uiActions.Triggered(UiAction.MoveToStart) ||
        _uiActions.Triggered(UiAction.MoveToEnd) ||
        _uiActions.Triggered(UiAction.Secondary) ||
        _uiActions.Triggered(UiAction.Tertiary);

    private void HandleControllerInput()
    {
        if (_matchHistoryOpen)
        {
            HandleMatchHistoryInput();
            return;
        }

        if (_screen == Screen.Store && _storeSearchActive)
        {
            return;
        }

        if (IsTypingTextInput())
        {
            return;
        }

        RestoreScrollableFocusForKeyboardOrController();

        if (_screen == Screen.MainMenu)
        {
            if (FocusPressed(out var focusDelta))
            {
                _menuFocus = (_menuFocus + focusDelta + 10) % 10;
            }
            if (_uiActions.Triggered(UiAction.MoveToStart)) _menuFocus = 0;
            else if (_uiActions.Triggered(UiAction.MoveToEnd)) _menuFocus = 9;
            if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical))
            {
                _menuFocus = Math.Clamp(_menuFocus + vertical, 0, 9);
            }

            if (Pressed(Buttons.A))
            {
                _usingController = true;
                if (_menuFocus == 0 && _profile is not null && _dataIssues.Count == 0)
                {
                    OpenSelectedMainMenuMode();
                }
                else if (_menuFocus == 1)
                {
                    EnsureHostInvite();
                    _screen = Screen.Multiplayer;
                    _multiplayerSection = MultiplayerSection.Local;
                    _directLobbyState = DirectLobbyState.Idle;
                    _joinInviteEditing = false;
                    _status = "Multiplayer opened.";
                }
                else if (_menuFocus == 2)
                {
                    _screen = Screen.DeckBuilder;
                    _status = "Deck builder opened.";
                }
                else if (_menuFocus == 3)
                {
                    _screen = Screen.Store;
                    _status = "Store opened.";
                }
                else if (_menuFocus == 4)
                {
                    _screen = Screen.Tutorials;
                    _status = "Tutorials opened.";
                }
                else if (_menuFocus == 5)
                {
                    _screen = Screen.Quests;
                    _status = "Quest Board opened.";
                }
                else if (_menuFocus == 6)
                {
                    _screen = Screen.Options;
                    _status = "Options opened.";
                }
                else if (_menuFocus == 7)
                {
                    _screen = Screen.ProfileData;
                    _profileDataNotice = "Profile data workspace opened.";
                    _profileDataAudit = [];
                    _profileSeedRuns = [];
                }
                else if (_menuFocus == 8)
                {
                    BeginNewGame();
                }
                else if (_menuFocus == 9)
                {
                    try { Exit(); }
                    catch (PlatformNotSupportedException) { }
                }
            }
        }
        else if (_screen == Screen.ModeSelect)
        {
            HandleModeSelectController();
        }
        else if (_screen == Screen.Multiplayer)
        {
            HandleMultiplayerController();
        }
        else if (_screen == Screen.PlayerCreation)
        {
            HandlePlayerCreationController();
        }
        else if (_screen == Screen.ProfilePicker)
        {
            HandleProfilePickerController();
        }
        else if (_screen == Screen.Store)
        {
            HandleStoreUxInput();
        }
        else if (_screen == Screen.PackOpening)
        {
            HandlePackOpeningUxInput();
        }
        else if (_screen == Screen.Quests)
        {
            if (Pressed(Buttons.A))
            {
                _screen = Screen.MainMenu;
            }
        }
        else if (_screen == Screen.MatchResult)
        {
            HandleMatchResultController();
        }
        else if (_screen == Screen.Tutorials)
        {
            HandleTutorialsController();
        }
        else if (_screen == Screen.Options)
        {
            HandleOptionsController();
        }
        else if (_screen == Screen.DeckBuilder)
        {
            HandleDeckBuilderUxInput();
        }
        else
        {
            HandleMatchController();
        }
    }

    private bool IsTypingTextInput()
    {
        if (_screen != Screen.PlayerCreation && !_profileRenameActive && !_deckNameEditing && !IsJoinInviteTextActive)
        {
            return false;
        }

        return _keyboard.GetPressedKeys().Any(key =>
            key is >= Keys.A and <= Keys.Z ||
            key is >= Keys.D0 and <= Keys.D9 ||
            key is Keys.Space or Keys.OemMinus or Keys.Subtract or Keys.Back or Keys.Delete);
    }

    private void HandleOptionsController()
    {
        _optionsListScroll.Configure(9, 6);
        var focusMoved = false;
        if (FocusPressed(out var focusDelta))
        {
            _optionsFocus = (_optionsFocus + focusDelta + 11) % 11;
            focusMoved = true;
        }
        if (_uiActions.Triggered(UiAction.PagePrevious))
        {
            _optionsListScroll.PageBy(-1);
            _optionsFocus = OptionsFocusForLogicalRow(_optionsListScroll.Offset);
            _usingController = true;
            focusMoved = true;
        }
        else if (_uiActions.Triggered(UiAction.PageNext))
        {
            _optionsListScroll.PageBy(1);
            _optionsFocus = OptionsFocusForLogicalRow(_optionsListScroll.Offset);
            _usingController = true;
            focusMoved = true;
        }
        else if (_uiActions.Triggered(UiAction.MoveToStart))
        {
            _optionsFocus = 0;
            _usingController = true;
            focusMoved = true;
        }
        else if (_uiActions.Triggered(UiAction.MoveToEnd))
        {
            _optionsFocus = 10;
            _usingController = true;
            focusMoved = true;
        }
        if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical))
        {
            _optionsFocus = Math.Clamp(_optionsFocus + vertical, 0, 10);
            focusMoved = true;
        }

        if (focusMoved && _optionsFocus <= 7)
        {
            _optionsListScroll.EnsureVisible(OptionsLogicalRow(_optionsFocus));
            _optionsFocusVisibilityPending = false;
        }

        if (DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out var horizontal))
        {
            AdjustFocusedOption(horizontal);
        }

        if (Pressed(Buttons.A))
        {
            _usingController = true;
            if (_optionsFocus is 0 or 4 or 5 or 6 or 7)
            {
                AdjustFocusedOption(1);
            }
            else if (_optionsFocus == 8)
            {
                _audio.RestartMusic();
                _status = "BGM restarted.";
            }
            else if (_optionsFocus == 9)
            {
                _audio.Play(SoundKeys.RarePull, throttleSeconds: 0);
                _status = "Sound test played.";
            }
            else if (_optionsFocus == 10)
            {
                _screen = UxBackDestination(Screen.MainMenu);
                _status = "Options saved.";
            }
        }
    }

    private void HandleModeSelectController()
    {
        _modeListScroll.Configure(PlayableModeCatalog.All.Count, 6);
        if (FocusPressed(out var focusDelta))
        {
            var selectedMode = PlayableModeCatalog.All[_modeFocus];
            var actionCount = selectedMode.Id == DragonCardsModeIds.TutorialTrials ? 1 : 4;
            _modeActionFocus = (_modeActionFocus + focusDelta + actionCount) % actionCount;
        }
        if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical))
        {
            _modeFocus = Math.Clamp(_modeFocus + vertical, 0, PlayableModeCatalog.All.Count - 1);
            _modeListScroll.Configure(PlayableModeCatalog.All.Count, 6);
            _modeListScroll.EnsureVisible(_modeFocus);
        }

        if (_uiActions.Triggered(UiAction.PagePrevious))
        {
            _modeListScroll.PageBy(-1);
            _modeFocus = Math.Clamp(_modeListScroll.Offset, 0, PlayableModeCatalog.All.Count - 1);
        }
        else if (_uiActions.Triggered(UiAction.PageNext))
        {
            _modeListScroll.PageBy(1);
            _modeFocus = Math.Clamp(_modeListScroll.Offset, 0, PlayableModeCatalog.All.Count - 1);
        }
        else if (_uiActions.Triggered(UiAction.MoveToStart))
        {
            _modeFocus = 0;
            _modeListScroll.MoveToStart();
        }
        else if (_uiActions.Triggered(UiAction.MoveToEnd))
        {
            _modeFocus = PlayableModeCatalog.All.Count - 1;
            _modeListScroll.MoveToEnd();
        }

        if (DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out var horizontal))
        {
            var selected = PlayableModeCatalog.All[_modeFocus];
            if (IsDown(Keys.LeftShift) || IsDown(Keys.RightShift))
            {
                AdjustModeVariant(selected, horizontal);
            }
            else
            {
                var actionCount = selected.Id == DragonCardsModeIds.TutorialTrials ? 1 : 4;
                _modeActionFocus = Math.Clamp(_modeActionFocus + horizontal, 0, actionCount - 1);
            }
        }

        var variantDelta = Pressed(Buttons.X) ? -1 : Pressed(Buttons.Y) ? 1 : 0;
        if (variantDelta != 0)
        {
            _usingController = true;
            AdjustModeVariant(PlayableModeCatalog.All[_modeFocus], variantDelta);
        }

        if (Pressed(Buttons.A))
        {
            _usingController = true;
            ActivateModeAction(PlayableModeCatalog.All[_modeFocus], _modeActionFocus);
        }

    }

    private void AdjustModeVariant(PlayableModeDefinition selected, int delta)
    {
        if (selected.Id == DragonCardsModeIds.DragonAvatar)
        {
            var count = Math.Max(1, DragonAvatarService.PlayableAvatarCandidates(_data).Count);
            _avatarFocus = (_avatarFocus + delta + count) % count;
        }
        else if (selected.Id == DragonCardsModeIds.StarterClash)
        {
            var count = Math.Max(1, StarterDecks().Count);
            _starterClashOpponentIndex = (_starterClashOpponentIndex + delta + count) % count;
        }
        else if (selected.Id == DragonCardsModeIds.SealedGauntlet && delta > 0)
        {
            _sealedPool = SealedGauntletService.GeneratePool(_data, Environment.TickCount);
            _status = "Sealed pool regenerated.";
        }
    }

    private void ActivateModeAction(PlayableModeDefinition selected, int action)
    {
        if (selected.Id == DragonCardsModeIds.TutorialTrials)
        {
            StartSelectedMode(selected, MatchKind.VsAi);
            return;
        }

        switch (Math.Clamp(action, 0, 3))
        {
            case 0:
                StartSelectedMode(selected, MatchKind.VsAi);
                break;
            case 1:
                StartSelectedMode(selected, MatchKind.Hotseat);
                break;
            case 2:
                HostSelectedMode(selected);
                break;
            case 3:
                EnsureHostInvite();
                _screen = Screen.Multiplayer;
                _multiplayerSection = MultiplayerSection.JoinLobby;
                _joinInviteEditing = true;
                _status = "Type a host invite to join a direct lobby.";
                break;
        }
    }

    private void AdjustFocusedOption(int delta)
    {
        switch (_optionsFocus)
        {
            case 0:
                ToggleFullscreen();
                break;
            case 1:
                CycleWindowSize(delta >= 0 ? 1 : -1);
                break;
            case 2:
                AdjustMusicVolume(delta >= 0 ? 10 : -10);
                break;
            case 3:
                AdjustSoundVolume(delta >= 0 ? 10 : -10);
                break;
            case 4:
                ToggleMuteAudio();
                break;
            case 5:
                ToggleCardZoom();
                break;
            case 6:
                CycleInteractionSpeed(delta);
                break;
            case 7:
                ToggleReducedMotion();
                break;
            case 8:
                _audio.RestartMusic();
                _status = "BGM restarted.";
                break;
            case 9:
                _audio.Play(SoundKeys.RarePull, throttleSeconds: 0);
                _status = "Sound test played.";
                break;
        }
    }

    private void HandleMultiplayerController()
    {
        HandleMultiplayerLobbyInput();
    }

    private void HandleTutorialsController()
    {
        _tutorialListScroll.Configure(TutorialDefinitions.Count, 6);
        if (FocusPressed(out var focusDelta))
        {
            _tutorialFocus = (_tutorialFocus + focusDelta + TutorialDefinitions.Count) % TutorialDefinitions.Count;
            _tutorialListScroll.EnsureVisible(_tutorialFocus);
        }
        if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical))
        {
            _tutorialFocus = Math.Clamp(_tutorialFocus + vertical, 0, TutorialDefinitions.Count - 1);
            _tutorialListScroll.Configure(TutorialDefinitions.Count, 6);
            _tutorialListScroll.EnsureVisible(_tutorialFocus);
        }

        if (_uiActions.Triggered(UiAction.PagePrevious))
        {
            _tutorialListScroll.PageBy(-1);
            _tutorialFocus = Math.Clamp(_tutorialListScroll.Offset, 0, TutorialDefinitions.Count - 1);
        }
        else if (_uiActions.Triggered(UiAction.PageNext))
        {
            _tutorialListScroll.PageBy(1);
            _tutorialFocus = Math.Clamp(_tutorialListScroll.Offset, 0, TutorialDefinitions.Count - 1);
        }
        else if (_uiActions.Triggered(UiAction.MoveToStart))
        {
            _tutorialFocus = 0;
            _tutorialListScroll.MoveToStart();
        }
        else if (_uiActions.Triggered(UiAction.MoveToEnd))
        {
            _tutorialFocus = TutorialDefinitions.Count - 1;
            _tutorialListScroll.MoveToEnd();
        }

        if (Pressed(Buttons.A))
        {
            _usingController = true;
            BeginTutorial(TutorialDefinitions[_tutorialFocus]);
        }
    }

    private void HandleDeckBuilderController()
    {
        var cards = _deckBuilder.FilteredCards;
        var pageCount = Math.Max(1, (int)Math.Ceiling(cards.Count / (double)DeckBuilderState.PageSize));
        var visibleCount = cards.Skip(_deckBuilder.Page * DeckBuilderState.PageSize).Take(DeckBuilderState.PageSize).Count();

        if (DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out var horizontal))
        {
            _deckFocusIndex = Math.Clamp(_deckFocusIndex + horizontal, 0, Math.Max(0, visibleCount - 1));
        }

        if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical))
        {
            _deckFocusIndex = Math.Clamp(_deckFocusIndex + vertical * 6, 0, Math.Max(0, visibleCount - 1));
        }

        if (Pressed(Buttons.LeftShoulder))
        {
            _usingController = true;
            _deckBuilder.Page = Math.Max(0, _deckBuilder.Page - 1);
            _deckFocusIndex = 0;
        }

        if (Pressed(Buttons.RightShoulder))
        {
            _usingController = true;
            _deckBuilder.Page = Math.Min(pageCount - 1, _deckBuilder.Page + 1);
            _deckFocusIndex = 0;
        }

        var selected = cards.Skip(_deckBuilder.Page * DeckBuilderState.PageSize).Skip(_deckFocusIndex).FirstOrDefault();
        if (selected is not null)
        {
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
        }
    }

    private void HandleMatchController()
    {
        if (_engine is null)
        {
            return;
        }

        if (Pressed(Keys.Tab) || Pressed(Buttons.RightStick))
        {
            _matchInspectorFocused = !_matchInspectorFocused;
            _usingController = true;
            return;
        }

        if (_matchInspectorFocused)
        {
            if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var inspectorVertical))
            {
                _cardDetailScrollOffset = Math.Max(0, _cardDetailScrollOffset + inspectorVertical);
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
            if (Pressed(Buttons.A) || DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out _))
            {
                _matchInspectorFocused = false;
            }
            return;
        }

        var state = _engine.State;
        var canUseActions = CanHumanUseActions(state);
        var canResolveEnergy = CanHumanResolveEnergyChoice(state);
        var canResolveEnergySource = CanHumanResolveEnergySourceChoice(state);
        var canResolveBlock = CanHumanResolveBlock(state);
        var canResolveTarget = CanHumanResolveTarget(state);
        var canResolveCombatAction = CanHumanResolveCombatAction(state);
        if (!canUseActions && !canResolveEnergy && !canResolveEnergySource && !canResolveBlock && !canResolveTarget && !canResolveCombatAction)
        {
            return;
        }

        if (canResolveEnergy)
        {
            var elements = state.Mode.Elements;
            if (DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out var energyHorizontal) ||
                DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out energyHorizontal))
            {
                _energyChoiceIndex = Math.Clamp(_energyChoiceIndex + energyHorizontal, 0, elements.Count - 1);
            }

            if (Pressed(Buttons.A))
            {
                _usingController = true;
                var element = elements[_energyChoiceIndex];
                ExecuteCommand("resolve-energy", element, () => _engine.ResolveEnergyChoice(element));
            }

            return;
        }

        if (canResolveEnergySource)
        {
            var choice = state.PendingEnergySourceChoice!;
            var sources = state.Players[choice.PlayerIndex].EnergyField
                .Where(source => !source.Exhausted &&
                    !state.DefinitionFor(source).Elements.Contains(choice.DestinationElement, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out var sourceHorizontal) ||
                DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out sourceHorizontal))
            {
                _energySourceChoiceIndex = Math.Clamp(_energySourceChoiceIndex + sourceHorizontal, 0, Math.Max(0, sources.Length - 1));
            }

            if (Pressed(Buttons.A) && sources.Length > 0)
            {
                _usingController = true;
                var source = sources[_energySourceChoiceIndex];
                ExecuteCommand("resolve-energy-source", source.Id, () => _engine.ResolveEnergySourceChoice(source.Id));
            }

            return;
        }

        if (state.Mode.EnergyRules.UsesPersistentEnergySources &&
            (_uiActions.Triggered(UiAction.PagePrevious) || _uiActions.Triggered(UiAction.PageNext)))
        {
            var direction = _uiActions.Triggered(UiAction.PagePrevious) ? -1 : 1;
            _energyFieldScrollOffset = Math.Clamp(_energyFieldScrollOffset + direction * 6, 0, Math.Max(0, state.ActivePlayer.EnergyField.Count - 6));
            _usingController = true;
            return;
        }

        if (state.PendingAttack is not null)
        {
            _matchFocus = MatchFocus.Blockers;
        }

        if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out _))
        {
            _matchFocus = _matchFocus switch
            {
                MatchFocus.Hand => MatchFocus.Units,
                MatchFocus.Units => MatchFocus.Supports,
                MatchFocus.Supports => MatchFocus.Hand,
                _ => MatchFocus.Blockers
            };
        }

        if (DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out var horizontal))
        {
            var selectionPlayer = _matchKind == MatchKind.Hotseat ? state.ActivePlayer : state.Players[LocalPlayerIndexForMatch()];
            if (_matchFocus == MatchFocus.Hand)
            {
                _selectedHandIndex = Math.Clamp(_selectedHandIndex < 0 ? 0 : _selectedHandIndex + horizontal, 0, Math.Max(0, selectionPlayer.Hand.Count - 1));
                _handStripScroll.Configure(selectionPlayer.Hand.Count, 9);
                _handStripScroll.EnsureVisible(_selectedHandIndex);
            }
            else if (_matchFocus == MatchFocus.Units)
            {
                _selectedUnitIndex = Math.Clamp(_selectedUnitIndex < 0 ? 0 : _selectedUnitIndex + horizontal, 0, Math.Max(0, selectionPlayer.UnitField.Count - 1));
            }
            else if (_matchFocus == MatchFocus.Supports)
            {
                _selectedSupportIndex = Math.Clamp(_selectedSupportIndex < 0 ? 0 : _selectedSupportIndex + horizontal, 0, Math.Max(0, selectionPlayer.SupportField.Count - 1));
            }
            else
            {
                var blockingPlayer = BlockingPlayer(state);
                _selectedBlockerIndex = Math.Clamp(_selectedBlockerIndex < 0 ? 0 : _selectedBlockerIndex + horizontal, 0, Math.Max(0, (blockingPlayer?.UnitField.Count ?? 0) - 1));
            }
        }

        if (Pressed(Buttons.RightShoulder) && canUseActions && state.PendingAttack is null)
        {
            _usingController = true;
            ExecuteCommand("advance", "", AdvanceMatchFlow);
            ClearSelections();
        }

        if (Pressed(Buttons.Y) && canUseActions && state.PendingAttack is null)
        {
            _usingController = true;
            if (_engine.CanAddEnergy())
            {
                _chooseFreeEnergy = true;
                _status = "Choose an element to add energy.";
            }
            else
            {
                _status = _engine.IsMainPhase()
                    ? "Energy has already been added or every element is maxed."
                    : "Energy can only be added during a main phase.";
            }
        }

        if (Pressed(Buttons.X) && canUseActions && _selectedHandIndex >= 0 && state.PendingAttack is null)
        {
            _usingController = true;
            var card = state.DefinitionFor(state.ActivePlayer.Hand[_selectedHandIndex]);
            if (ShouldReplaceSelectedHandCard(card))
            {
                BeginReplacement(_selectedHandIndex, card);
            }
            else
            {
                PlayCardFromHand(_selectedHandIndex);
            }
        }

        if (Pressed(Buttons.A))
        {
            _usingController = true;
            if (CanHumanResolveTarget(state) && FirstLegalTargetChoice() is { } target)
            {
                var payload = TargetPayload(target);
                ExecuteCommand("target", payload, () => _engine.ResolveTargetChoice(target));
                ClearSelections();
            }
            else if (canResolveCombatAction && state.PendingCombatAction is { } action)
            {
                var ability = _engine.GetActivatableAbilities(action.PriorityPlayerIndex).FirstOrDefault();
                if (ability is not null)
                {
                    var payload = $"{ability.PlayerIndex}|{ability.SourceInstanceId}|{ability.Ability.Id}";
                    ExecuteCommand("ability", payload, () => _engine.ActivateAbility(ability.PlayerIndex, ability.SourceInstanceId, ability.Ability.Id));
                }
                else
                {
                    ExecuteCommand("combat-pass", action.PriorityPlayerIndex.ToString(), () => _engine.PassCombatAction(action.PriorityPlayerIndex));
                }

                ClearSelections();
            }
            else if (canUseActions && state.PendingAttack is null && TryGetSelectedReplacementSource() is { } replacement)
            {
                ReplaceWithSacrifice(replacement.Source, replacement.Index);
            }
            else if (canUseActions && state.PendingAttack is null && _selectedUnitIndex >= 0 && _replacementTarget is null)
            {
                var payload = _selectedUnitIndex.ToString();
                ExecuteCommand("attack", payload, () => _engine.DeclareAttack(_selectedUnitIndex));
                _selectedUnitIndex = -1;
                _matchFocus = MatchFocus.Blockers;
            }
            else if (canResolveBlock && state.PendingAttack is not null && _selectedBlockerIndex >= 0)
            {
                var payload = _selectedBlockerIndex.ToString();
                ExecuteCommand("block", payload, () => _engine.Block(_selectedBlockerIndex));
                ClearSelections();
            }
            else if (canUseActions && state.PendingAttack is null)
            {
                var selectedField = SelectedHumanFieldCard(state);
                var ability = selectedField?.Definition.Abilities.FirstOrDefault();
                if (selectedField is not null && ability is not null)
                {
                    var ownerIndex = _matchKind == MatchKind.Hotseat ? state.ActivePlayerIndex : LocalPlayerIndexForMatch();
                    var payload = $"{ownerIndex}|{selectedField.Value.Instance.Id}|{ability.Id}";
                    ExecuteCommand("ability", payload, () => _engine.ActivateAbility(ownerIndex, selectedField.Value.Instance.Id, ability.Id));
                }
            }
        }

        if (Pressed(Buttons.LeftShoulder) && canUseActions && state.PendingAttack is null)
        {
            _usingController = true;
            SacrificeSelectedCard();
        }

        if (Pressed(Buttons.LeftShoulder) && canResolveBlock && state.PendingAttack is not null)
        {
            _usingController = true;
            ExecuteCommand("pass-block", "", _engine.PassBlock);
            ClearSelections();
        }

        if (Pressed(Buttons.LeftShoulder) && canResolveCombatAction && state.PendingCombatAction is { } combatAction)
        {
            _usingController = true;
            ExecuteCommand("combat-pass", combatAction.PriorityPlayerIndex.ToString(), () => _engine.PassCombatAction(combatAction.PriorityPlayerIndex));
            ClearSelections();
        }
    }

    private static Rectangle ActiveBoardRect() => new(34, 438, 1212, 238);

    private static Rectangle HandAreaRect() => new(34, 704, 1212, 150);

    private static Rectangle RightRailRect() => new(1264, 92, 302, 762);

    private static Rectangle MatchLogRect()
    {
        var area = RightRailRect();
        return new Rectangle(area.X + 18, area.Y + 674, area.Width - 36, 78);
    }

    private Rectangle HandCardRect(int index)
    {
        var area = HandAreaRect();
        return new Rectangle(area.X + 54 + (index - _handStripScroll.Offset) * 106, area.Y + 36, 92, 108);
    }

    private static Rectangle AddEnergyDropRect()
    {
        var area = ActiveBoardRect();
        return new Rectangle(area.X + 18, area.Y + 58, 174, 168);
    }

    private static Rectangle SupportDropRect()
    {
        var area = ActiveBoardRect();
        return new Rectangle(area.X + 212, area.Y + 58, 408, 150);
    }

    private static Rectangle UnitDropRect()
    {
        var area = ActiveBoardRect();
        return new Rectangle(area.X + 640, area.Y + 58, 528, 150);
    }

    private static Rectangle CastDropRect() => new(1288, 626, 132, 54);

    private bool IsValidHandIndex(int handIndex) =>
        _engine is not null && handIndex >= 0 && handIndex < _engine.State.ActivePlayer.Hand.Count;

    private void UpdateViewportMapping()
    {
        var backBufferWidth = Math.Max(1, GraphicsDevice.PresentationParameters.BackBufferWidth);
        var backBufferHeight = Math.Max(1, GraphicsDevice.PresentationParameters.BackBufferHeight);
        var aspectFit = AspectFitViewport.Calculate(backBufferWidth, backBufferHeight, VirtualWidth, VirtualHeight);
        _viewportScale = aspectFit.Scale;
        _viewportRectangle = aspectFit.Rectangle;
        _virtualMouse = aspectFit.ToVirtual(new Point(_mouse.X, _mouse.Y));
    }

    private void ToggleFullscreen()
    {
        _graphics.ToggleFullScreen();
        if (!_graphics.IsFullScreen)
        {
            _graphics.PreferredBackBufferWidth = _settings.WindowWidth;
            _graphics.PreferredBackBufferHeight = _settings.WindowHeight;
            _graphics.ApplyChanges();
        }

        _settings.Fullscreen = _graphics.IsFullScreen;
        _settings.Save();
        _status = _graphics.IsFullScreen ? "Fullscreen enabled." : "Windowed mode enabled.";
    }

    private void CycleWindowSize(int delta)
    {
        var current = WindowSizeOptions
            .Select((size, index) => (size, index))
            .FirstOrDefault(item => item.size.Width == _settings.WindowWidth && item.size.Height == _settings.WindowHeight);
        var currentIndex = current == default ? 1 : current.index;
        var nextIndex = (currentIndex + delta + WindowSizeOptions.Length) % WindowSizeOptions.Length;
        var next = WindowSizeOptions[nextIndex];
        _settings.WindowWidth = next.Width;
        _settings.WindowHeight = next.Height;
        _settings.Save();

        if (!_graphics.IsFullScreen)
        {
            _graphics.PreferredBackBufferWidth = next.Width;
            _graphics.PreferredBackBufferHeight = next.Height;
            _graphics.ApplyChanges();
        }

        _status = $"Window size set to {next.Width} x {next.Height}.";
    }

    private void AdjustMusicVolume(int delta)
    {
        _settings.MusicVolume = Math.Clamp(_settings.MusicVolume + delta, 0, 100);
        _settings.Save();
        _status = $"Music volume {_settings.MusicVolume}%.";
    }

    private void AdjustSoundVolume(int delta)
    {
        _settings.SoundVolume = Math.Clamp(_settings.SoundVolume + delta, 0, 100);
        _settings.Save();
        _status = $"Sound volume {_settings.SoundVolume}%.";
    }

    private void ToggleMuteAudio()
    {
        _settings.MuteAudio = !_settings.MuteAudio;
        _settings.Save();
        _status = _settings.MuteAudio ? "Audio muted." : "Audio unmuted.";
    }

    private void ToggleCardZoom()
    {
        _settings.CardZoom = !_settings.CardZoom;
        _settings.Save();
        _status = _settings.CardZoom ? "Card hover zoom enabled." : "Card hover zoom disabled.";
    }

    private float InteractionSpeedMultiplier => _settings.InteractionSpeedPercent / 100f;

    private string InteractionSpeedLabel() => _settings.InteractionSpeedPercent switch
    {
        55 => "Cinematic (0.55x)",
        70 => "Natural (0.70x)",
        100 => "Quick (1.00x)",
        140 => "Fast (1.40x)",
        _ => $"Custom ({InteractionSpeedMultiplier:0.##}x)"
    };

    private void CycleInteractionSpeed(int delta)
    {
        var currentIndex = Array.IndexOf(InteractionSpeedPercentOptions, _settings.InteractionSpeedPercent);
        if (currentIndex < 0)
        {
            currentIndex = Array.FindIndex(InteractionSpeedPercentOptions, value => value >= _settings.InteractionSpeedPercent);
            currentIndex = currentIndex < 0 ? InteractionSpeedPercentOptions.Length - 1 : currentIndex;
        }

        var nextIndex = (currentIndex + Math.Sign(delta) + InteractionSpeedPercentOptions.Length) % InteractionSpeedPercentOptions.Length;
        _settings.InteractionSpeedPercent = InteractionSpeedPercentOptions[nextIndex];
        _presentation.SpeedMultiplier = InteractionSpeedMultiplier;
        _settings.Save();
        _status = $"Interaction pace set to {InteractionSpeedLabel()}.";
    }

    private void ToggleReducedMotion()
    {
        _settings.ReducedMotion = !_settings.ReducedMotion;
        _settings.Save();
        _status = _settings.ReducedMotion ? "Reduced motion enabled." : "Full motion enabled.";
    }

    private void GoBack()
    {
        if (_profileDeleteConfirmation)
        {
            _profileDeleteConfirmation = false;
            return;
        }

        if (_profileRenameActive)
        {
            _profileRenameActive = false;
            return;
        }

        if (_deckLibraryDeleteConfirmation)
        {
            _deckLibraryDeleteConfirmation = false;
            return;
        }

        if (_deckNameEditing)
        {
            _deckNameEditing = false;
            return;
        }

        if (_deckLibraryOpen)
        {
            _deckLibraryOpen = false;
            return;
        }

        if (_screen == Screen.PlayerCreation)
        {
            _screen = Screen.ProfilePicker;
            _status = "Profile creation cancelled.";
            return;
        }

        if (_screen == Screen.ProfilePicker)
        {
            if (_profilePickerOpenedFromMainMenu && _profile is not null)
            {
                _screen = Screen.MainMenu;
                _status = "Returned to main menu.";
            }
            return;
        }

        if (_screen == Screen.MainMenu)
        {
            return;
        }

        if (_tutorial is not null)
        {
            _tutorial = null;
            _tutorialNotice = "";
            ClearPresentation();
        }

        if (_screen == Screen.PackOpening)
        {
            _screen = Screen.Store;
            _status = "Returned to store.";
            return;
        }

        if (_screen is Screen.Match or Screen.MatchResult)
        {
            if (_matchKind == MatchKind.Online)
            {
                CloseNetworkMatchConnection();
            }
            ClearPresentation();
            _screen = Screen.MainMenu;
        }
        else
        {
            _screen = UxBackDestination(Screen.MainMenu);
        }
        _audio.Play(SoundKeys.UiBack);
        _status = _screen == Screen.MainMenu ? "Returned to main menu." : "Returned.";
        ClearSelections();
    }

    private void StartMatch(DeckDefinition firstDeck, DeckDefinition secondDeck, MatchKind matchKind) =>
        StartMatch(firstDeck, secondDeck, matchKind, DragonCardsModeIds.DragonDuel);

    private void StartMatch(DeckDefinition firstDeck, DeckDefinition secondDeck, MatchKind matchKind, string modeId)
    {
        ClearPresentation();
        _tutorial = null;
        _tutorialNotice = "";
        _matchKind = matchKind;
        var opponentDeck = matchKind == MatchKind.VsAi
            ? secondDeck
            : secondDeck;
        _engine = DragonDuelEngine.Create(_data, modeId, firstDeck, opponentDeck, seed: Environment.TickCount);
        ConfigureMatchStart(firstDeck, opponentDeck, matchKind);
        _matchTimelineEntries.Clear();

        var flowResult = _engine.AdvanceToNextDecisionPhase();
        _screen = Screen.Match;
        ClearSelections();
        _matchFocus = MatchFocus.Hand;
        QueuePresentation(flowResult.Events);
        _status = _matchKind == MatchKind.VsAi
            ? $"{_engine.State.Mode.Name} started. Your Main Phase."
            : flowResult.Success ? flowResult.Message : "Match started.";
    }

    private void EnsureHostInvite()
    {
        if (string.IsNullOrWhiteSpace(_hostInviteCode))
        {
            GenerateHostInviteForSelectedMode();
        }
    }

    private void GenerateHostInviteForSelectedMode()
    {
        if (IsDirectLobbyActive)
        {
            return;
        }

        var selectedMode = PlayableModeCatalog.All[Math.Clamp(_modeFocus, 0, PlayableModeCatalog.All.Count - 1)];
        if (!selectedMode.StartsMatch || !TryCreateDecksForMode(selectedMode.Id, out var deck, out _, out _))
        {
            GenerateHostInvite(DragonCardsModeIds.DragonDuel, CurrentDeck());
            return;
        }

        GenerateHostInvite(selectedMode.Id, deck);
    }

    private void GenerateHostInvite(string modeId, DeckDefinition deck)
    {
        var rules = CurrentRules();
        _hostInvite = new NetworkInvite
        {
            Host = LocalNetworkAddress.PreferredIpv4Address(),
            Port = _hostLobbyPort,
            ModeId = modeId,
            ProtocolVersion = InviteCode.ProtocolVersion,
            DeckHash = InviteCode.DeckHash(deck.Cards),
            RulesHash = InviteCode.RulesHash(rules),
            LobbyToken = Random.Shared.Next(1, ushort.MaxValue + 1)
        };
        _hostInviteCode = InviteCode.EncodeLobbyCode(_hostInvite.LobbyToken);
        _hostSameComputerInviteCode = InviteCode.Encode(_hostInvite with { Host = "127.0.0.1" });
        _multiplayerNotice = $"Five-character LAN code ready for {ModeName(modeId)}.";
    }

    private void ValidateJoinInvite()
    {
        EnsureHostInvite();
        if (InviteCode.TryDecodeLobbyCode(_joinInviteCode, out var lobbyToken, out var lobbyError))
        {
            _multiplayerNotice = $"Valid LAN code {InviteCode.EncodeLobbyCode(lobbyToken)}. Connect searches for the host on this network.";
            _status = "Lobby code is ready to connect.";
            return;
        }

        if (InviteCode.TryDecode(_joinInviteCode, out var invite, out var error))
        {
            _multiplayerNotice = $"Valid {InviteLabel(_joinInviteCode)} invite for {ModeName(invite.ModeId)} at {invite.Host}:{invite.Port}.";
            _status = "Invite is ready to connect.";
            return;
        }

        var normalizedLobbyCode = new string(_joinInviteCode.Where(character => !char.IsWhiteSpace(character) && character != '-').ToArray());
        _multiplayerNotice = normalizedLobbyCode.Length == InviteCode.LobbyCodeLength ? lobbyError : error;
        _status = "Invite is invalid.";
    }

    private void SaveDeck(DeckDefinition deck)
    {
        if (_profile is null || _profileRepository is null || string.IsNullOrWhiteSpace(_activeProfileId))
        {
            _status = "Select a local profile before saving a deck.";
            return;
        }

        if (_data.DecksById.ContainsKey(deck.Id))
        {
            var name = UniqueDeckName($"{deck.Name} Copy");
            deck = deck with { Id = $"deck-{Guid.NewGuid():N}", Name = name };
            _deckBuilder.SetIdentity(deck.Id, deck.Name, deck.ModeId);
        }

        if (!_profileRepository.TrySaveDeck(_activeProfileId, deck, out var error))
        {
            _status = error ?? "Could not save the deck.";
            return;
        }

        _profile.ActiveDeckId = deck.Id;
        SaveProfile();
        _status = $"Saved {deck.Name} in this profile's deck library.";
    }

    private void ApplyResult(GameActionResult result)
    {
        _status = result.Message;
        if (!result.Success)
        {
            _audio.Play(SoundKeys.UiError);
        }

        QueuePresentation(result.Events);
        if (result.Success)
        {
            RecordQuestDamage(result.Events);
        }
        if (result.Success && _engine?.State.WinnerIndex is not null)
        {
            _status = $"{_engine.State.Players[_engine.State.WinnerIndex.Value].Name} wins.";
            QueueResultScreen();
        }
    }

    private void ApplyHumanResult(GameActionResult result)
    {
        ApplyResult(result);
        if (result.Success)
        {
            TryAdvanceAiTurn();
        }
    }

    private void QueuePresentation(IReadOnlyList<MatchEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        AddTimelineEntries(events);
        _presentation.ReducedMotion = _settings.ReducedMotion;
        _presentation.SpeedMultiplier = InteractionSpeedMultiplier;
        _presentation.Enqueue(events);
        _audio.PlayActivationCues(_presentation.DrainActivations());
    }

    private void ClearPresentation()
    {
        _presentation.Clear();
        _audio.CancelQueuedCues();
        _aiInteractionDelaySeconds = 0f;
        _aiAwaitingPresentation = false;
    }

    private void AddTimelineEntries(IEnumerable<MatchEvent> events)
    {
        foreach (var matchEvent in events)
        {
            if (!ShouldAddTimelineEntry(matchEvent))
            {
                continue;
            }

            _matchTimelineEntries.Add(new MatchTimelineEntry(
                TimelineIcon(matchEvent),
                TimelineText(matchEvent),
                PresentationColor(matchEvent)));
        }

        if (_matchTimelineEntries.Count > 40)
        {
            _matchTimelineEntries.RemoveRange(0, _matchTimelineEntries.Count - 40);
        }
    }

    private static bool ShouldAddTimelineEntry(MatchEvent matchEvent) =>
        AnimationRecipes.For(matchEvent.Kind).AddToTimeline;

    private static string TimelineIcon(MatchEvent matchEvent) => matchEvent.Kind switch
    {
        MatchEventKind.CardDrawn => "D",
        MatchEventKind.CardPlayed => "P",
        MatchEventKind.CardDiscarded => "X",
        MatchEventKind.CardSacrificed => "S",
        MatchEventKind.AbilityActivated => "@",
        MatchEventKind.TargetResolved => ">",
        MatchEventKind.AttackDeclared => "A",
        MatchEventKind.BlockDeclared => "B",
        MatchEventKind.CombatActionPassed => "...",
        MatchEventKind.CombatResolved => "!",
        MatchEventKind.DamageTaken => "-1",
        MatchEventKind.CardReturnedToHand => "<",
        _ => "*"
    };

    private static string TimelineText(MatchEvent matchEvent)
    {
        if (!string.IsNullOrWhiteSpace(matchEvent.Message))
        {
            return matchEvent.Message;
        }

        return matchEvent.Kind switch
        {
            MatchEventKind.CardDrawn => "Card drawn.",
            MatchEventKind.CardPlayed => "Card played.",
            MatchEventKind.AttackDeclared => "Attack declared.",
            MatchEventKind.BlockDeclared => "Block declared.",
            MatchEventKind.DamageTaken => "Damage taken.",
            _ => matchEvent.Kind.ToString()
        };
    }

    private void UpdateAiInteractionPacing(float elapsedSeconds)
    {
        if (_aiInteractionDelaySeconds > 0f)
        {
            _aiInteractionDelaySeconds = Math.Max(0f, _aiInteractionDelaySeconds - elapsedSeconds);
        }

        TryAdvanceAiTurn();
    }

    private void TryAdvanceAiTurn()
    {
        if (_engine is null ||
            _screen != Screen.Match ||
            _matchKind != MatchKind.VsAi ||
            _engine.State.WinnerIndex is not null)
        {
            return;
        }

        if (_aiAwaitingPresentation)
        {
            if (_presentation.PendingActionCount > 0)
            {
                return;
            }

            _aiAwaitingPresentation = false;
            _aiInteractionDelaySeconds = AiInterActionPauseSeconds / InteractionSpeedMultiplier;
            return;
        }

        if (_aiInteractionDelaySeconds > 0f || _presentation.PendingActionCount > 0)
        {
            return;
        }

        // Apply just one decision at a time. This keeps the board state, sound cues, and visual
        // feedback readable instead of resolving an entire AI turn between two rendered frames.
        var result = _ai.RunUntilHumanInput(_engine, AiPlayerIndex, CurrentRules(), maxActions: 1);
        if (result.Decisions.Count > 0)
        {
            _status = result.Decisions[^1].Message;
            foreach (var decision in result.Decisions)
            {
                QueuePresentation(decision.Events);
            }

            _aiAwaitingPresentation = true;
        }

        if (_engine.State.WinnerIndex is not null)
        {
            _status = $"{_engine.State.Players[_engine.State.WinnerIndex.Value].Name} wins.";
            QueueResultScreen();
            return;
        }

        if (result.Status == AiTurnStatus.WaitingForHumanBlock)
        {
            _status = MatchPrompt();
            _matchFocus = MatchFocus.Blockers;
            EnsureSelectedBlocker(_engine.State);
        }
        else if (result.Status == AiTurnStatus.ActionLimitReached && result.Decisions.Count == 0)
        {
            _status = "AI stopped after its action guard.";
        }
        else if (result.Decisions.Count > 0 && _engine.State.ActivePlayerIndex == HumanPlayerIndex)
        {
            _status = MatchPrompt();
        }
    }

    private GameActionResult AdvanceMatchFlow()
    {
        if (_engine is null)
        {
            return GameActionResult.Fail("No match is active.");
        }

        var phase = _engine.State.CurrentPhase;
        if (phase.Equals("Ready", StringComparison.OrdinalIgnoreCase) ||
            phase.Equals("Draw", StringComparison.OrdinalIgnoreCase) ||
            phase.Equals("End", StringComparison.OrdinalIgnoreCase))
        {
            return _engine.AdvanceToNextDecisionPhase();
        }

        if (phase.Equals("Second Main", StringComparison.OrdinalIgnoreCase))
        {
            var endPhase = _engine.AdvancePhase();
            return endPhase.Success ? _engine.AdvanceToNextDecisionPhase() : endPhase;
        }

        return _engine.AdvancePhase();
    }

    private void PlayCardFromHand(int handIndex)
    {
        if (_engine is null)
        {
            return;
        }

        if (IsValidHandIndex(handIndex) && BasicEnergy.IsBasicEnergyCard(_engine.State.DefinitionFor(_engine.State.ActivePlayer.Hand[handIndex])))
        {
            PlayEnergyFromHand(handIndex);
            return;
        }

        var payload = handIndex.ToString();
        if (ExecuteCommand("play-card", payload, () => _engine.PlayCardFromHand(handIndex)))
        {
            ClearSelections();
        }
    }

    private void PlayEnergyFromHand(int handIndex)
    {
        if (_engine is null)
        {
            return;
        }

        var payload = handIndex.ToString();
        if (ExecuteCommand("play-energy", payload, () => _engine.PlayEnergyFromHand(handIndex)))
        {
            ClearSelections();
        }
    }

    private bool TutorialAllowsCommand(string kind, string payload)
    {
        if (_tutorial is null)
        {
            return true;
        }

        var step = _tutorial.CurrentStep;
        if (step is null)
        {
            _status = "Tutorial complete.";
            return false;
        }

        if (step.Matches(kind, payload))
        {
            return true;
        }

        _status = string.IsNullOrWhiteSpace(step.Hint)
            ? $"Tutorial step: {step.Instruction}"
            : step.Hint;
        return false;
    }

    private void AdvanceTutorial(string kind, string payload)
    {
        if (_tutorial is null || _tutorial.CurrentStep is not { } step || !step.Matches(kind, payload))
        {
            return;
        }

        _tutorial.StepIndex++;
        if (!_tutorial.IsComplete)
        {
            _status = _tutorial.CurrentStep?.Instruction ?? $"Tutorial: {_tutorial.Definition.Name}";
            return;
        }

        var result = _profile is null
            ? new TutorialCompletionResult(false, 0, "Create a profile to earn tutorial rewards.")
            : TutorialRewardService.CompleteTutorial(_profile, _tutorial.Definition.Id);
        if (_profile is not null)
        {
            SaveProfile();
        }

        _tutorialNotice = result.Awarded
            ? $"{_tutorial.Definition.Name} completed. +{result.CoinsAwarded} Coins."
            : $"{_tutorial.Definition.Name} completed. {result.Message}";
        _status = _tutorialNotice;
        _tutorial = null;
        ClearPresentation();
        ClearSelections();
        _screen = Screen.Tutorials;
    }

    private void CaptureScreens()
    {
        if (_spriteBatch is null)
        {
            return;
        }

        Directory.CreateDirectory(_captureDirectory);
        ClearPresentation();
        var previousScreen = _screen;
        var previousEngine = _engine;
        var previousStatus = _status;
        var previousHand = _selectedHandIndex;
        var previousUnit = _selectedUnitIndex;
        var previousSupport = _selectedSupportIndex;
        var previousBlocker = _selectedBlockerIndex;
        var previousChooseFreeEnergy = _chooseFreeEnergy;
        var previousOptionsFocus = _optionsFocus;
        var previousOptionsScroll = _optionsListScroll.Offset;
        var previousOptionsFocusVisibilityPending = _optionsFocusVisibilityPending;
        var previousUsingController = _usingController;
        var previousModeActionFocus = _modeActionFocus;
        var previousModeFocus = _modeFocus;
        var previousMultiplayerFocus = _multiplayerFocus;
        var previousResultFocus = _resultFocus;
        var previousMatchKind = _matchKind;
        var previousVirtualMouse = _virtualMouse;
        var previousCardZoom = _settings.CardZoom;
        var previousReducedMotion = _settings.ReducedMotion;
        var previousProfile = _profile;
        var previousProfileRepository = _profileRepository;
        var previousActiveProfileId = _activeProfileId;
        var previousDeckBuilder = _deckBuilder;
        var previousOpening = _lastBoosterOpening;
        var previousReward = _lastMatchReward;
        var previousSpoils = _lastBattleSpoils;
        var previousPendingResult = _pendingResultScreen;
        var previousRewardApplied = _matchRewardApplied;
        var previousStoreFocus = _storeFocus;
        var previousStoreScroll = _storeScrollOffset;
        var previousPackScroll = _packOpeningScrollOffset;
        var previousCardDetailScroll = _cardDetailScrollOffset;
        var previousTutorial = _tutorial;
        var previousTutorialFocus = _tutorialFocus;
        var previousTutorialNotice = _tutorialNotice;
        var previousStoreCategory = _storeCategory;
        var previousStoreSearch = _storeSearch;
        var previousStoreElementFilter = _storeElementFilterIndex;
        var previousStoreRarityFilter = _storeRarityFilterIndex;
        var previousStoreSetFilter = _storeSetFilterIndex;
        var previousStoreFocusArea = _storeFocusArea;
        var previousStoreSearchActive = _storeSearchActive;
        var previousStoreCategoryFocus = _storeCategoryFocus.ToArray();
        var previousStoreCategoryScroll = _storeCategoryScroll.Select(state => state.Offset).ToArray();
        var previousDeckElementFilter = _deckBuilder.ElementFilter;
        var previousDeckTypeFilter = _deckBuilder.TypeFilter;
        var previousDeckSelectedCard = _deckBuilder.SelectedCardId;
        var previousDeckPage = _deckBuilder.Page;
        var previousDeckFocus = _deckFocusIndex;
        var previousDeckFocusArea = _deckFocusArea;
        var previousDeckControlFocus = _deckControlFocus;
        var previousDeckFocusVisibilityPending = _deckFocusVisibilityPending;
        var previousDeckScroll = _deckGridScroll.Offset;
        var previousModeScroll = _modeListScroll.Offset;
        var previousTutorialScroll = _tutorialListScroll.Offset;
        var previousPackGridScroll = _packGridScroll.Offset;
        var previousPackFocusVisibilityPending = _packFocusVisibilityPending;
        var previousHandScroll = _handStripScroll.Offset;
        var previousMatchHistoryOpen = _matchHistoryOpen;
        var previousMatchHistoryScroll = _matchHistoryScrollOffset;
        var previousScreenElapsed = _screenElapsed;
        var previousScreenFade = _screenFadeRemaining;
        var previousMatchInspectorFocused = _matchInspectorFocused;
        var previousDrawOpacity = _drawOpacity;
        var previousHostInvite = _hostInvite;
        var previousHostInviteCode = _hostInviteCode;
        var previousHostSameComputerInviteCode = _hostSameComputerInviteCode;
        var previousJoinInviteCode = _joinInviteCode;
        var previousMultiplayerNotice = _multiplayerNotice;
        var previousMultiplayerSection = _multiplayerSection;
        var previousDirectLobbyState = _directLobbyState;
        var previousJoinInviteEditing = _joinInviteEditing;
        var previousHostLobbyPort = _hostLobbyPort;
        var previousSqliteMigrationPreview = _sqliteMigrationPreview;
        var previousSqliteMigrationConfirmation = _sqliteMigrationConfirmation;
        var previousProfileRestoreConfirmation = _profileRestoreConfirmation;
        var previousProfileSeedValue = _profileSeedValue;
        var previousProfileSeedPreview = _profileSeedPreview;
        var previousProfileDataAudit = _profileDataAudit;
        var previousProfileSeedRuns = _profileSeedRuns;
        var previousProfileDataNotice = _profileDataNotice;
        var previousProfileDataCardOffset = _profileDataCardOffset;
        var captureProfileRoot = Path.Combine(Path.GetTempPath(), "DragonCardsCaptureProfiles", Guid.NewGuid().ToString("N"));
        _profileRepository = new LocalProfileRepository(captureProfileRoot);
        _profileRepository.Initialize(out _, out _);
        _activeProfileId = null;
        _settings.CardZoom = true;
        _usingController = false;
        _storeFocusArea = StoreFocusArea.Catalog;
        _storeSearchActive = false;
        _deckFocusArea = DeckFocusArea.Grid;
        _matchInspectorFocused = false;
        _screenFadeRemaining = 0f;
        _drawOpacity = 1f;

        CaptureScreen("profile-picker-empty.png", () =>
        {
            _profile = null;
            _activeProfileId = null;
            _profilePickerFocus = 0;
            _profilePickerScrollOffset = 0;
            _profileDeleteConfirmation = false;
            _screen = Screen.ProfilePicker;
            _status = "Capture: empty profile picker.";
        });
        CaptureScreen("profile-picker-multiple.png", () =>
        {
            var fireStarter = _data.DecksById["starter-fire"];
            var iceStarter = _data.DecksById["starter-ice"];
            var astra = ProgressionService.CreateProfile("Astra", GameRulesConfig.ForPreset(GameRulesPreset.Standard, Playstyle.Aggro), fireStarter, _data);
            astra.Coins = 3200;
            astra.Experience = 4200;
            astra.Normalize();
            _profileRepository!.TryCreateProfile(astra, DateTimeOffset.UtcNow, out var astraSummary, out _);
            var bryn = ProgressionService.CreateProfile("Bryn", GameRulesConfig.ForPreset(GameRulesPreset.Standard), iceStarter, _data);
            bryn.Coins = 800;
            bryn.Normalize();
            _profileRepository.TryCreateProfile(bryn, DateTimeOffset.UtcNow.AddMinutes(1), out _, out _);
            _profile = astra;
            _activeProfileId = astraSummary?.Id;
            _deckBuilder = CreateDeckBuilderState(fireStarter);
            _screen = Screen.ProfilePicker;
            _profilePickerFocus = 1;
            _profileDeleteConfirmation = false;
            _status = "Capture: multiple local profiles.";
        });
        CaptureScreen("profile-delete-confirmation.png", () =>
        {
            _screen = Screen.ProfilePicker;
            _profilePickerFocus = 0;
            _profileDeleteConfirmation = true;
            _status = "Capture: profile deletion confirmation.";
        });

        CaptureScreen("player-creation.png", () =>
        {
            _profile = null;
            _screen = Screen.PlayerCreation;
            _creationName = "Astra";
            _creationPresetIndex = 2;
            _creationPlaystyleIndex = 1;
            _creationStarterIndex = 0;
            _status = "Capture: player creation.";
        });
        CaptureScreen("main-menu.png", () =>
        {
            PrepareCaptureProfile();
            _screen = Screen.MainMenu;
            _status = "Capture: main menu.";
        });
        CaptureScreen("mode-select.png", () =>
        {
            PrepareCaptureProfile();
            _screen = Screen.ModeSelect;
            _modeFocus = 0;
            _modeListScroll.MoveToStart();
            _status = "Capture: mode select.";
        });
        CaptureScreen("dragon-avatar-setup.png", () =>
        {
            PrepareCaptureProfile();
            _screen = Screen.ModeSelect;
            _modeFocus = PlayableModeCatalog.All.ToList().FindIndex(mode => mode.Id == DragonCardsModeIds.DragonAvatar);
            _avatarFocus = 0;
            _status = "Capture: Dragon Avatar setup.";
        });
        CaptureScreen("sealed-gauntlet-setup.png", () =>
        {
            PrepareCaptureProfile();
            _screen = Screen.ModeSelect;
            _modeFocus = PlayableModeCatalog.All.ToList().FindIndex(mode => mode.Id == DragonCardsModeIds.SealedGauntlet);
            _sealedPool = SealedGauntletService.GeneratePool(_data, 44);
            _status = "Capture: Sealed Gauntlet setup.";
        });
        CaptureScreen("tutorials-menu.png", () =>
        {
            PrepareCaptureProfile();
            _screen = Screen.Tutorials;
            _tutorialFocus = 0;
            _tutorialListScroll.MoveToStart();
            _tutorialNotice = "Tutorials reward 250 Coins the first time you complete each lesson.";
            _status = "Capture: tutorials menu.";
        });
        CaptureScreen("tutorial-first-turn-basics.png", () => PrepareCaptureTutorial("first-turn-basics"));
        CaptureScreen("tutorial-playing-cards.png", () => PrepareCaptureTutorial("playing-cards"));
        CaptureScreen("tutorial-add-energy.png", () => PrepareCaptureTutorial("add-energy"));
        CaptureScreen("tutorial-sacrifice-energy.png", () => PrepareCaptureTutorial("sacrifice-energy"));
        CaptureScreen("tutorial-blocking-attacks.png", () => PrepareCaptureTutorial("blocking-attacks"));
        CaptureScreen("tutorial-card-effects.png", () => PrepareCaptureTutorial("card-effects"));
        CaptureScreen("quest-board.png", () =>
        {
            PrepareCaptureProfile();
            QuestService.Record(_profile!, DateTimeOffset.UtcNow, QuestMetric.EligibleMatches, 1, eligible: true);
            QuestService.Record(_profile!, DateTimeOffset.UtcNow, QuestMetric.NonEnergyCardsPlayed, 10, eligible: true);
            _screen = Screen.Quests;
            _status = "Capture: quest board.";
        });
        CaptureScreen("profile-data-json.png", () =>
        {
            PrepareCaptureProfile();
            _sqliteMigrationPreview = null;
            _sqliteMigrationConfirmation = false;
            _profileRestoreConfirmation = false;
            _profileSeedPreview = null;
            _profileDataAudit = [];
            _profileSeedRuns = [];
            _profileDataCardOffset = 0;
            _profileDataNotice = "Capture: profile data workspace before SQLite migration.";
            _screen = Screen.ProfileData;
            _status = "Capture: profile data workspace.";
        });
        CaptureScreen("multiplayer.png", () =>
        {
            PrepareCaptureProfile();
            _screen = Screen.Multiplayer;
            _multiplayerSection = MultiplayerSection.HostLobby;
            _directLobbyState = DirectLobbyState.Idle;
            PrepareCaptureMultiplayerInvite();
            _multiplayerFocus = 0;
            _status = "Capture: host lobby setup.";
        });
        CaptureScreen("multiplayer-host-waiting.png", () =>
        {
            PrepareCaptureProfile();
            _screen = Screen.Multiplayer;
            _multiplayerSection = MultiplayerSection.HostLobby;
            _directLobbyState = DirectLobbyState.Idle;
            PrepareCaptureMultiplayerInvite();
            _directLobbyState = DirectLobbyState.Hosting;
            _multiplayerFocus = 0;
            _multiplayerNotice = "Hosting Dragon Duel. Waiting for one guest to enter the invite.";
            _status = "Capture: waiting host lobby.";
        });
        CaptureScreen("multiplayer-join-lobby.png", () =>
        {
            PrepareCaptureProfile();
            _screen = Screen.Multiplayer;
            _multiplayerSection = MultiplayerSection.JoinLobby;
            _directLobbyState = DirectLobbyState.Idle;
            PrepareCaptureMultiplayerInvite();
            _joinInviteCode = _hostInviteCode;
            _joinInviteEditing = true;
            _multiplayerFocus = 0;
            _status = "Capture: join lobby entry.";
        });
        CaptureScreen("deck-builder.png", () =>
        {
            _screen = Screen.DeckBuilder;
            _deckBuilder.ElementFilter = "Fire";
            _deckBuilder.TypeFilter = "All";
            _deckBuilder.Page = 0;
            _deckFocusIndex = 4;
            _deckGridScroll.MoveToStart();
            _deckBuilder.SelectedCardId = "fire-ashen-champion";
            _status = "Capture: deck builder.";
        });
        CaptureScreen("deck-builder-scroll.png", () =>
        {
            _screen = Screen.DeckBuilder;
            _deckBuilder.ElementFilter = "All";
            _deckBuilder.TypeFilter = "All";
            _deckFocusIndex = 76;
            _deckGridScroll.Configure((int)Math.Ceiling(_deckBuilder.FilteredCards.Count / 6d), 2);
            _deckGridScroll.SetOffset(12);
            _deckBuilder.SelectedCardId = _deckBuilder.FilteredCards[_deckFocusIndex].Id;
            _status = "Capture: scrolled deck builder.";
        });
        CaptureScreen("deck-library.png", () =>
        {
            PrepareCaptureProfile();
            var deck = new DeckDefinition
            {
                Id = "deck-capture-sunfire",
                Name = "Sunfire Tactics",
                ModeId = DragonCardsModeIds.DragonDuel,
                Cards = _data.DecksById["starter-fire"].Cards.ToDictionary(card => card.Key, card => card.Value, StringComparer.OrdinalIgnoreCase)
            };
            _profileRepository!.TrySaveDeck(_activeProfileId!, deck, out _);
            _deckBuilder = CreateDeckBuilderState(deck);
            _deckLibraryOpen = true;
            _deckLibraryFocus = DeckLibraryEntries().ToList().FindIndex(entry => entry.Deck.Id == deck.Id);
            _deckLibraryDeleteConfirmation = false;
            _deckNameEditing = false;
            _screen = Screen.DeckBuilder;
            _status = "Capture: profile deck library.";
        });
        CaptureScreen("collection-filter-sort.png", () =>
        {
            PrepareCaptureProfile();
            _deckLibraryOpen = false;
            _deckLibraryDeleteConfirmation = false;
            _deckNameEditing = false;
            _deckBuilder.ElementFilter = "All";
            _deckBuilder.TypeFilter = "All";
            _deckBuilder.OwnershipFilter = CollectionOwnershipFilter.Owned;
            _deckBuilder.RarityFilter = "All";
            _deckBuilder.SetFilter = "All";
            _deckBuilder.SortMode = CollectionSortMode.OwnedCopies;
            _deckFocusIndex = 0;
            _deckGridScroll.MoveToStart();
            _screen = Screen.DeckBuilder;
            _status = "Capture: owned collection sorted by copies.";
        });
        CaptureScreen("store.png", () =>
        {
            PrepareCaptureProfile();
            _storeCategory = StoreCategory.Packs;
            _storeSearch = "";
            ResetStoreFilters();
            _screen = Screen.Store;
            _status = "Capture: store.";
        });
        CaptureScreen("store-singles.png", () =>
        {
            PrepareCaptureProfile();
            _storeCategory = StoreCategory.Singles;
            ResetStoreFilters();
            ClampActiveStoreSelection(ensureVisible: false);
            ActiveStoreScroll.SetOffset(18);
            _storeFocus = 20;
            _storeCategoryFocus[(int)_storeCategory] = _storeFocus;
            _screen = Screen.Store;
            _status = "Capture: filtered singles catalog.";
        });
        CaptureScreen("store-filter-empty.png", () =>
        {
            PrepareCaptureProfile();
            _storeCategory = StoreCategory.Singles;
            ResetStoreFilters();
            _storeSearch = "no-card-matches-this";
            ResetActiveStorePosition();
            _screen = Screen.Store;
            _status = "Capture: empty store filter.";
        });
        CaptureScreen("pack-opening.png", () =>
        {
            PrepareCaptureProfile();
            _lastBoosterOpening = BoosterService.OpenBooster(_data, _profile!, seed: 19, consumeUnopened: false);
            _packGridScroll.MoveToStart();
            _packOpeningScrollOffset = 0;
            _screen = Screen.PackOpening;
            _screenElapsed = 2f;
            _status = "Capture: pack opening.";
        });
        CaptureScreen("pack-opening-overflow.png", () =>
        {
            PrepareCaptureProfile();
            _lastBoosterOpening = BoosterService.OpenBoosters(_data, _profile!, BoosterService.StandardBoosterId, quantity: 5, seed: 29, consumeUnopened: false);
            _packGridScroll.Configure(4, 2);
            _packGridScroll.SetOffset(1);
            _packOpeningScrollOffset = _packGridScroll.Offset;
            _packFocusIndex = 17;
            _screen = Screen.PackOpening;
            _screenElapsed = 2f;
            _status = "Capture: pack opening overflow.";
        });
        CaptureScreen("options.png", () =>
        {
            _screen = Screen.Options;
            _optionsFocus = 0;
            _optionsListScroll.Configure(9, 6);
            _optionsListScroll.MoveToStart();
            _optionsFocusVisibilityPending = true;
            _settings.ReducedMotion = false;
            _status = "Capture: options.";
        });
        CaptureScreen("options-reduced-motion.png", () =>
        {
            _screen = Screen.Options;
            _optionsFocus = 7;
            _optionsListScroll.Configure(9, 6);
            _optionsFocusVisibilityPending = true;
            _settings.ReducedMotion = true;
            _status = "Capture: Reduced Motion enabled.";
        });
        CaptureScreen("match.png", () =>
        {
            PrepareCaptureMatch();
            _screen = Screen.Match;
            _status = "Capture: match board.";
        });
        CaptureScreen("single-player-match.png", () =>
        {
            PrepareCaptureSinglePlayerMatch();
            _screen = Screen.Match;
            _status = "Capture: single-player AI match.";
        });
        CaptureScreen("energy-source-conversion.png", () =>
        {
            PrepareCaptureMatch();
            _engine!.State.PendingEnergySourceChoice = new PendingEnergySourceChoice(
                _engine.State.ActivePlayerIndex,
                "Water",
                "Choose a ready Energy source to convert to Water.");
            _screen = Screen.Match;
            _status = "Capture: Energy source conversion picker.";
        });
        CaptureScreen("long-hand.png", () =>
        {
            PrepareCaptureMatch();
            while (_engine!.State.ActivePlayer.Hand.Count < 12)
            {
                AddHandCard(_engine.State.ActivePlayerIndex, "fire-ember-whelp");
            }
            _handStripScroll.Configure(_engine.State.ActivePlayer.Hand.Count, 9);
            _handStripScroll.SetOffset(3);
            _selectedHandIndex = 10;
            _matchFocus = MatchFocus.Hand;
            _screen = Screen.Match;
            _status = "Capture: long scrolling hand.";
        });
        CaptureScreen("match-history.png", () =>
        {
            PrepareCaptureMatch();
            for (var index = 1; index <= 18; index++)
            {
                _engine!.State.Log.Add($"History entry {index}: a visible match action resolved.");
            }
            _matchTimelineEntries.Clear();
            _matchTimelineEntries.Add(new MatchTimelineEntry("P", "Ignition Tyrant played.", new Color(244, 230, 158)));
            _matchTimelineEntries.Add(new MatchTimelineEntry("A", "Ashen Champion attacked.", new Color(255, 172, 100)));
            _matchTimelineEntries.Add(new MatchTimelineEntry("!", "Combat resolved.", new Color(235, 92, 76)));
            _matchHistoryOpen = true;
            _matchHistoryScrollOffset = 4;
            _screen = Screen.Match;
            _status = "Capture: expanded match history.";
        });
        CaptureScreen("hover-zoom.png", () =>
        {
            PrepareCaptureSinglePlayerMatch();
            _screen = Screen.Match;
            _virtualMouse = HandCardRect(0).Center;
            _status = "Capture: card hover zoom.";
        });
        CaptureScreen("block-choice.png", () =>
        {
            PrepareCaptureBlockChoice();
            _screen = Screen.Match;
        });
        CaptureScreen("blocking-modal.png", () =>
        {
            PrepareCaptureBlockChoice();
            _screen = Screen.Match;
        });
        CaptureScreen("blocking-modal-exhausted.png", () =>
        {
            PrepareCaptureBlockChoice(exhaustHumanBlocker: true);
            _screen = Screen.Match;
        });
        CaptureScreen("combat-action-modal.png", PrepareCaptureCombatActionPrompt);
        CaptureScreen("card-effect-modal.png", PrepareCaptureCardEffectPrompt);
        CaptureScreen("sacrifice-tooltip.png", PrepareCaptureSacrificeTooltip);
        CaptureScreen("animation-showcase.png", () =>
        {
            PrepareCaptureMatch();
            _screen = Screen.Match;
            _presentation.PrimeForCapture(new MatchEvent
            {
                Kind = MatchEventKind.CardPlayed,
                PlayerIndex = HumanPlayerIndex,
                CardId = "fire-ignition-tyrant",
                From = new ZoneRef(HumanPlayerIndex, "Hand", 0),
                To = new ZoneRef(HumanPlayerIndex, "UnitField", 2),
                Message = "Ignition Tyrant played."
            });
            _status = "Capture: animation showcase.";
        });
        CaptureScreen("animation-reduced-motion.png", () =>
        {
            PrepareCaptureMatch();
            _settings.ReducedMotion = true;
            _presentation.ReducedMotion = true;
            _screen = Screen.Match;
            _presentation.PrimeForCapture(new MatchEvent
            {
                Kind = MatchEventKind.CardPlayed,
                PlayerIndex = HumanPlayerIndex,
                CardId = "fire-ignition-tyrant",
                From = new ZoneRef(HumanPlayerIndex, "Hand", 0),
                To = new ZoneRef(HumanPlayerIndex, "UnitField", 2),
                Message = "Ignition Tyrant played."
            });
            _status = "Capture: Reduced Motion event feedback.";
        });
        CaptureScreen("result-screen.png", () =>
        {
            PrepareCaptureProfile();
            ClearPresentation();
            _lastMatchWon = true;
            _lastMatchReward = RewardCalculator.PreviewMatchReward(_profile!, CurrentRules(), MatchRewardKind.Ai, won: true);
            _lastBattleSpoils = BattleSpoilsService.GrantVictorySpoils(_data, _profile!, CurrentRules(), _data.DecksById["starter-ice"], won: true, seed: 7);
            _screen = Screen.MatchResult;
            _screenElapsed = 2f;
            _status = "Capture: result screen.";
        });

        _screen = previousScreen;
        _engine = previousEngine;
        _status = previousStatus;
        _selectedHandIndex = previousHand;
        _selectedUnitIndex = previousUnit;
        _selectedSupportIndex = previousSupport;
        _selectedBlockerIndex = previousBlocker;
        _chooseFreeEnergy = previousChooseFreeEnergy;
        _optionsFocus = previousOptionsFocus;
        _optionsListScroll.Configure(9, 6);
        _optionsListScroll.SetOffset(previousOptionsScroll);
        _optionsFocusVisibilityPending = previousOptionsFocusVisibilityPending;
        _usingController = previousUsingController;
        _modeActionFocus = previousModeActionFocus;
        _modeFocus = previousModeFocus;
        _multiplayerFocus = previousMultiplayerFocus;
        _resultFocus = previousResultFocus;
        _matchKind = previousMatchKind;
        _virtualMouse = previousVirtualMouse;
        _settings.CardZoom = previousCardZoom;
        _settings.ReducedMotion = previousReducedMotion;
        _profile = previousProfile;
        _profileRepository = previousProfileRepository;
        _activeProfileId = previousActiveProfileId;
        _lastBoosterOpening = previousOpening;
        _lastMatchReward = previousReward;
        _lastBattleSpoils = previousSpoils;
        _pendingResultScreen = previousPendingResult;
        _matchRewardApplied = previousRewardApplied;
        _storeFocus = previousStoreFocus;
        _storeScrollOffset = previousStoreScroll;
        _packOpeningScrollOffset = previousPackScroll;
        _cardDetailScrollOffset = previousCardDetailScroll;
        _tutorial = previousTutorial;
        _tutorialFocus = previousTutorialFocus;
        _tutorialNotice = previousTutorialNotice;
        _storeCategory = previousStoreCategory;
        _storeSearch = previousStoreSearch;
        _storeElementFilterIndex = previousStoreElementFilter;
        _storeRarityFilterIndex = previousStoreRarityFilter;
        _storeSetFilterIndex = previousStoreSetFilter;
        _storeFocusArea = previousStoreFocusArea;
        _storeSearchActive = previousStoreSearchActive;
        for (var index = 0; index < _storeCategoryFocus.Length; index++)
        {
            _storeCategoryFocus[index] = previousStoreCategoryFocus[index];
            _storeCategoryScroll[index].SetOffset(previousStoreCategoryScroll[index]);
        }
        _deckBuilder.ElementFilter = previousDeckElementFilter;
        _deckBuilder.TypeFilter = previousDeckTypeFilter;
        _deckBuilder.SelectedCardId = previousDeckSelectedCard;
        _deckBuilder.Page = previousDeckPage;
        _deckFocusIndex = previousDeckFocus;
        _deckFocusArea = previousDeckFocusArea;
        _deckControlFocus = previousDeckControlFocus;
        _deckFocusVisibilityPending = previousDeckFocusVisibilityPending;
        _deckGridScroll.SetOffset(previousDeckScroll);
        _modeListScroll.SetOffset(previousModeScroll);
        _tutorialListScroll.SetOffset(previousTutorialScroll);
        _packGridScroll.SetOffset(previousPackGridScroll);
        _packFocusVisibilityPending = previousPackFocusVisibilityPending;
        _handStripScroll.SetOffset(previousHandScroll);
        _matchHistoryOpen = previousMatchHistoryOpen;
        _matchHistoryScrollOffset = previousMatchHistoryScroll;
        _screenElapsed = previousScreenElapsed;
        _screenFadeRemaining = previousScreenFade;
        _matchInspectorFocused = previousMatchInspectorFocused;
        _drawOpacity = previousDrawOpacity;
        _hostInvite = previousHostInvite;
        _hostInviteCode = previousHostInviteCode;
        _hostSameComputerInviteCode = previousHostSameComputerInviteCode;
        _joinInviteCode = previousJoinInviteCode;
        _multiplayerNotice = previousMultiplayerNotice;
        _multiplayerSection = previousMultiplayerSection;
        _directLobbyState = previousDirectLobbyState;
        _joinInviteEditing = previousJoinInviteEditing;
        _hostLobbyPort = previousHostLobbyPort;
        _sqliteMigrationPreview = previousSqliteMigrationPreview;
        _sqliteMigrationConfirmation = previousSqliteMigrationConfirmation;
        _profileRestoreConfirmation = previousProfileRestoreConfirmation;
        _profileSeedValue = previousProfileSeedValue;
        _profileSeedPreview = previousProfileSeedPreview;
        _profileDataAudit = previousProfileDataAudit;
        _profileSeedRuns = previousProfileSeedRuns;
        _profileDataNotice = previousProfileDataNotice;
        _profileDataCardOffset = previousProfileDataCardOffset;
        _deckBuilder = previousDeckBuilder;
        ClearPresentation();
        GraphicsDevice.SetRenderTarget(null);
        try { Directory.Delete(captureProfileRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _status = $"Captured screens to {_captureDirectory}.";
    }

    private void CaptureScreen(string fileName, Action prepare)
    {
        // A fresh target per capture avoids stale tiles observed when the KNI/GL target is rebound repeatedly.
        using var target = new RenderTarget2D(
            GraphicsDevice,
            VirtualWidth,
            VirtualHeight,
            mipMap: false,
            preferredFormat: SurfaceFormat.Color,
            preferredDepthFormat: DepthFormat.None,
            preferredMultiSampleCount: 0,
            usage: RenderTargetUsage.PreserveContents);
        _virtualMouse = new Point(-1000, -1000);
        prepare();
        GraphicsDevice.SetRenderTarget(target);
        GraphicsDevice.Clear(Color.Black);
        _spriteBatch!.Begin(samplerState: SamplerState.LinearClamp);
        DrawVirtualScene();
        _spriteBatch.End();
        GraphicsDevice.SetRenderTarget(null);

        var path = Path.Combine(_captureDirectory, fileName);
        using var stream = File.Create(path);
        target.SaveAsPng(stream, VirtualWidth, VirtualHeight);
    }

    private void PrepareCaptureMultiplayerInvite()
    {
        _modeFocus = PlayableModeCatalog.All.ToList().FindIndex(mode => mode.Id == DragonCardsModeIds.DragonDuel);
        var deck = CurrentDeck();
        var rules = CurrentRules();
        _hostInvite = new NetworkInvite
        {
            Host = "192.168.1.42",
            Port = 47288,
            ModeId = DragonCardsModeIds.DragonDuel,
            ProtocolVersion = InviteCode.ProtocolVersion,
            DeckHash = InviteCode.DeckHash(deck.Cards),
            RulesHash = InviteCode.RulesHash(rules),
            LobbyToken = 0x2A7B
        };
        _hostInviteCode = InviteCode.EncodeLobbyCode(_hostInvite.LobbyToken);
        _hostSameComputerInviteCode = InviteCode.Encode(_hostInvite with { Host = "127.0.0.1" });
        _multiplayerNotice = "Five-character LAN code ready for Dragon Duel.";
    }

    private void PrepareCaptureMatch()
    {
        _settings.ReducedMotion = false;
        _presentation.ReducedMotion = false;
        _matchHistoryOpen = false;
        _handStripScroll.MoveToStart();
        _tutorial = null;
        _tutorialNotice = "";
        _matchKind = MatchKind.Hotseat;
        _engine = DragonDuelEngine.Create(_data, "dragon-duel", _data.DecksById["starter-fire"], _data.DecksById["starter-ice"], seed: 3, shuffle: false);
        _engine.AdvanceToNextDecisionPhase();
        var active = _engine.State.ActivePlayer;
        var defender = _engine.State.DefendingPlayer;
        active.EnergyField.Add(new CardInstance(BasicEnergy.CardId("Fire"), "capture-fire-basic", EnergySourceOrigin.BasicCard));
        active.EnergyField.Add(new CardInstance(EnergySource.CardId("Fire"), "capture-fire-free", EnergySourceOrigin.FreeAdd) { Exhausted = true });
        active.EnergyField.Add(new CardInstance(BasicEnergy.CardId("Wind"), "capture-wind-basic", EnergySourceOrigin.BasicCard));
        active.EnergyField.Add(new CardInstance(EnergySource.CardId("Wind"), "capture-wind-effect", EnergySourceOrigin.Effect));
        active.EnergyField.Add(new CardInstance(EnergySource.CardId("Light"), "capture-light-sacrifice", EnergySourceOrigin.Sacrifice));
        defender.EnergyField.Add(new CardInstance(BasicEnergy.CardId("Ice"), "capture-ice-basic", EnergySourceOrigin.BasicCard));
        defender.EnergyField.Add(new CardInstance(EnergySource.CardId("Ice"), "capture-ice-free", EnergySourceOrigin.FreeAdd));
        defender.EnergyField.Add(new CardInstance(EnergySource.CardId("Earth"), "capture-earth-effect", EnergySourceOrigin.Effect));
        active.EnergyPool["Fire"] = 3;
        active.EnergyPool["Wind"] = 2;
        active.EnergyPool["Light"] = 1;
        defender.EnergyPool["Ice"] = 2;
        defender.EnergyPool["Earth"] = 1;
        active.SupportField.Add(new CardInstance("fire-hearth-shrine"));
        active.SupportField.Add(new CardInstance("fire-forge-caller"));
        active.SupportField.Add(new CardInstance("wind-waystone-shrine"));
        active.SupportField.Add(new CardInstance("wind-mapmaker"));
        active.SupportField.Add(new CardInstance("light-sun-shrine"));
        active.UnitField.Add(new CardInstance("fire-cinder-adept"));
        active.UnitField.Add(new CardInstance("wind-gale-scout"));
        active.UnitField.Add(new CardInstance("fire-ember-whelp"));
        active.UnitField.Add(new CardInstance("wind-breeze-sprite"));
        active.UnitField.Add(new CardInstance("light-dawn-initiate"));
        defender.UnitField.Add(new CardInstance("ice-snowguard-adept"));
        defender.UnitField.Add(new CardInstance("earth-rootwarden"));
        defender.UnitField.Add(new CardInstance("ice-glacial-wisp"));
        defender.SupportField.Add(new CardInstance("earth-grove-shrine"));
        defender.SupportField.Add(new CardInstance("ice-mirror-sage"));
        defender.DamageZone.Add(new CardInstance("ice-lance"));
        _selectedHandIndex = -1;
        _selectedUnitIndex = -1;
        _selectedSupportIndex = 0;
        _selectedBlockerIndex = -1;
        _matchFocus = MatchFocus.Supports;
    }

    private void PrepareCaptureSinglePlayerMatch()
    {
        PrepareCaptureMatch();
        _matchKind = MatchKind.VsAi;
        _engine!.State.Players[AiPlayerIndex].Name = "AI: Ice";
        _engine.State.ActivePlayer.EnergyPool["Fire"] = 4;
        _engine.State.ActivePlayer.EnergyPool["Wind"] = 3;
        _engine.State.Players[AiPlayerIndex].EnergyPool["Ice"] = 4;
        _engine.State.Players[AiPlayerIndex].EnergyPool["Earth"] = 3;
        _engine.State.Log.Add("Single-player AI mode started.");
        _status = "Your Main Phase.";
        _selectedSupportIndex = -1;
        _selectedHandIndex = 0;
        _matchFocus = MatchFocus.Hand;
    }

    private void PrepareCaptureBlockChoice(bool exhaustHumanBlocker = false)
    {
        PrepareCaptureSinglePlayerMatch();
        if (_engine is null)
        {
            return;
        }

        var combatIndex = _engine.State.Mode.Phases.FindIndex(phase => phase.Equals("Combat", StringComparison.OrdinalIgnoreCase));
        if (combatIndex >= 0)
        {
            _engine.State.PhaseIndex = combatIndex;
        }

        _engine.State.ActivePlayerIndex = AiPlayerIndex;
        _engine.State.PendingAttack = null;
        _engine.State.Players[AiPlayerIndex].UnitField.Clear();
        _engine.State.Players[HumanPlayerIndex].UnitField.Clear();
        _engine.State.Players[AiPlayerIndex].UnitField.Add(new CardInstance("earth-pebble-imp"));
        _engine.State.Players[HumanPlayerIndex].UnitField.Add(new CardInstance("fire-cinder-adept") { Exhausted = exhaustHumanBlocker });

        var result = _engine.DeclareAttack(0);
        if (result.Success)
        {
            EnsureSelectedBlocker(_engine.State);
            _status = MatchPrompt();
        }
        else
        {
            _status = result.Message;
        }
    }

    private void PrepareCaptureTutorial(string tutorialId)
    {
        PrepareCaptureProfile();
        var tutorial = TutorialDefinitions.FirstOrDefault(item => item.Id.Equals(tutorialId, StringComparison.OrdinalIgnoreCase))
            ?? TutorialDefinitions[0];
        BeginTutorial(tutorial);
        _screen = Screen.Match;
        _status = $"Capture: {tutorial.Name}.";
    }

    private void PrepareCaptureCardEffectPrompt()
    {
        PrepareCaptureTutorial("card-effects");
        if (_engine is null)
        {
            return;
        }

        var result = _engine.PlayCardFromHand(0);
        ClearPresentation();
        if (_tutorial is not null && result.Success)
        {
            _tutorial.StepIndex = 1;
        }

        _status = result.Success ? "Capture: card effect prompt." : result.Message;
    }

    private void PrepareCaptureCombatActionPrompt()
    {
        PrepareCaptureBlockChoice();
        if (_engine is null)
        {
            return;
        }

        _engine.State.Players[HumanPlayerIndex].SupportField.Add(new CardInstance("fire-primal-watch-post"));
        _engine.State.Players[HumanPlayerIndex].EnergyPool["Fire"] = 2;
        var result = _engine.PassBlock();
        ClearPresentation();
        _screen = Screen.Match;
        _status = result.Success ? "Capture: combat action prompt." : result.Message;
    }

    private void PrepareCaptureSacrificeTooltip()
    {
        PrepareCaptureTutorial("sacrifice-energy");
        _selectedHandIndex = 0;
        _selectedUnitIndex = -1;
        _selectedSupportIndex = -1;
        _matchFocus = MatchFocus.Hand;
        _status = "Capture: sacrifice preview.";
    }

    private void ClearSelections()
    {
        _selectedHandIndex = -1;
        _selectedUnitIndex = -1;
        _selectedSupportIndex = -1;
        _selectedBlockerIndex = -1;
        _matchFocus = MatchFocus.Hand;
        _matchInspectorFocused = false;
        _replacementHandIndex = -1;
        _replacementTarget = null;
    }

    private void RequestZoom(CardDefinition card, int count, Rectangle source)
    {
        if (!_settings.CardZoom)
        {
            return;
        }

        _zoomCard = card;
        _zoomCount = count;
        _zoomSource = source;
    }

    private static Rectangle Inset(Rectangle rect, int inset) =>
        new(rect.X + inset, rect.Y + inset, rect.Width - inset * 2, rect.Height - inset * 2);

    private static string ShortPath(string path)
    {
        var fileName = Path.GetFileName(path);
        var parent = Directory.GetParent(path)?.Name;
        return string.IsNullOrWhiteSpace(parent) ? fileName : $@"...\{parent}\{fileName}";
    }

    private static string ChunkInviteCode(string inviteCode)
    {
        if (string.IsNullOrWhiteSpace(inviteCode))
        {
            return "";
        }

        const int chunkSize = 34;
        var chunks = Enumerable.Range(0, (inviteCode.Length + chunkSize - 1) / chunkSize)
            .Select(index => inviteCode.Substring(index * chunkSize, Math.Min(chunkSize, inviteCode.Length - index * chunkSize)));
        return string.Join(" ", chunks);
    }

    private string CardDetailText(CardDefinition card) =>
        CardDetailFormatter.Format(card, ElementAdvantageSummary(card));

    private static string CardFrameRulesText(CardDefinition card, int maxLines = 3) =>
        CardDetailFormatter.FrameRulesSummary(card, maxLines, maxCharactersPerLine: 48);

    private static string InspectionRulesText(CardDefinition card) =>
        CardDetailFormatter.RulesText(card);

    private static string CostText(CardDefinition card) =>
        CardDetailFormatter.CostText(card);

    private static string CostText(IReadOnlyDictionary<string, int> cost) =>
        CardDetailFormatter.CostText(cost);

    private static string CompactCostText(string element, int amount) =>
        element.Equals(DragonCardConstants.GenericCost, StringComparison.OrdinalIgnoreCase)
            ? $"G{amount}"
            : $"{ElementAbbreviation(element)}{amount}";

    private static string ElementAbbreviation(string element) => element.ToLowerInvariant() switch
    {
        "fire" => "Fi",
        "ice" => "Ic",
        "wind" => "Wi",
        "earth" => "Ea",
        "lightning" => "Lt",
        "water" => "Wa",
        "light" => "Li",
        "dark" => "Da",
        _ => element.Length <= 2 ? element : element[..2]
    };

    private static string NextPhaseLabel(MatchState state) => state.CurrentPhase switch
    {
        "Ready" or "Draw" => "To Main",
        "Second Main" or "End" => "End Turn",
        _ => "Next Phase"
    };

    private static IEnumerable<KeyValuePair<string, int>> OrderedCosts(CardDefinition card)
    {
        foreach (var cost in OrderedCosts(card.Cost))
        {
            yield return cost;
        }
    }

    private static IEnumerable<KeyValuePair<string, int>> OrderedCosts(IReadOnlyDictionary<string, int> costMap)
    {
        foreach (var element in ElementOrder)
        {
            if (costMap.TryGetValue(element, out var amount) && amount > 0)
            {
                yield return new KeyValuePair<string, int>(element, amount);
            }
        }

        foreach (var cost in costMap
            .Where(cost => cost.Value > 0 &&
                !cost.Key.Equals(DragonCardConstants.GenericCost, StringComparison.OrdinalIgnoreCase) &&
                !ElementOrder.Contains(cost.Key, StringComparer.OrdinalIgnoreCase))
            .OrderBy(cost => cost.Key))
        {
            yield return cost;
        }

        if (costMap.TryGetValue(DragonCardConstants.GenericCost, out var generic) && generic > 0)
        {
            yield return new KeyValuePair<string, int>(DragonCardConstants.GenericCost, generic);
        }
    }

    private static string ElementDisplay(CardDefinition card)
    {
        if (card.Elements.Count == 0)
        {
            return "DC";
        }

        return string.Join("/", card.Elements);
    }

    private static Color TypeColor(string type) => type.ToLowerInvariant() switch
    {
        "unit" => new Color(188, 86, 66),
        "support" => new Color(86, 142, 112),
        "spell" => new Color(80, 114, 178),
        _ => new Color(112, 122, 136)
    };

    private static Color ElementColor(string element) => element.ToLowerInvariant() switch
    {
        "fire" => new Color(198, 76, 54),
        "ice" => new Color(92, 162, 210),
        "wind" => new Color(102, 170, 118),
        "earth" => new Color(142, 108, 68),
        "lightning" => new Color(220, 188, 72),
        "water" => new Color(62, 126, 188),
        "light" => new Color(216, 204, 146),
        "dark" => new Color(120, 92, 150),
        _ => new Color(110, 120, 132)
    };

    private static IReadOnlyList<TutorialDefinition> CreateTutorialDefinitions() =>
    [
        new(
            "first-turn-basics",
            "First Turn Basics",
            "Learn the turn track by moving from Main into Combat, then onward.",
            [
                new(TutorialStepKind.AdvancePhase, "Advance to Combat", "Press Next Phase to leave Main and enter Combat.", "advance", "", "Use the Next Phase button."),
                new(TutorialStepKind.AdvancePhase, "Advance Again", "Press Next Phase once more to continue the turn.", "advance", "", "Keep using Next Phase for this lesson.")
            ]),
        new(
            "playing-cards",
            "Playing Cards",
            "Play a simple Unit from hand, then cast a Spell with the remaining energy.",
            [
                new(TutorialStepKind.PlayCard, "Play Ember Whelp", "Select Ember Whelp and press Play.", "play-card", "0", "Play the first card in your hand."),
                new(TutorialStepKind.PlayCard, "Cast Spark Offering", "Select Spark Offering and press Play to cast it.", "play-card", "0", "Play the spell now in the first hand slot.")
            ]),
        new(
            "add-energy",
            "Add Energy",
            "Open the energy picker and add Fire energy for your next play.",
            [
                new(TutorialStepKind.AddEnergy, "Add Fire Energy", "Press Add Energy, then choose Fire.", "add-energy", "Fire", "Choose Fire from the energy picker.")
            ]),
        new(
            "sacrifice-energy",
            "Sacrifice For Energy",
            "Read the sacrifice preview, then sacrifice the selected hand card for energy.",
            [
                new(TutorialStepKind.Sacrifice, "Sacrifice Cinder Adept", "Use the Sacrifice button on the selected card.", "sacrifice", "Hand|0", "Sacrifice the selected hand card.")
            ]),
        new(
            "blocking-attacks",
            "Blocking Attacks",
            "Respond to an incoming attack by choosing a legal blocker.",
            [
                new(TutorialStepKind.Block, "Block the Attack", "Choose Fire Cinder Adept as the blocker.", "block", "0", "Choose the available blocker instead of passing.")
            ]),
        new(
            "card-effects",
            "Card Effects",
            "Play a card with a target effect, then choose the enemy Unit it affects.",
            [
                new(TutorialStepKind.PlayCard, "Play Battle Seer", "Play Battle Seer to queue its exhaust effect.", "play-card", "0", "Play the selected Battle Seer."),
                new(TutorialStepKind.ResolveTarget, "Choose the Target", "Choose the enemy Crystal Champion in the effect prompt.", "target", "1|UnitField|0", "Choose the highlighted enemy Unit.")
            ])
    ];

    private enum Screen
    {
        ProfilePicker,
        PlayerCreation,
        MainMenu,
        ModeSelect,
        Multiplayer,
        Tutorials,
        Options,
        DeckBuilder,
        Store,
        PackOpening,
        Quests,
        ProfileData,
        Match,
        MatchResult
    }

    private enum MatchKind
    {
        Hotseat,
        VsAi,
        Online
    }

    private enum MatchFocus
    {
        Hand,
        Units,
        Supports,
        Blockers
    }

    private enum ReplacementTarget
    {
        Unit,
        Support
    }

    private sealed record DraggedHandCard(int HandIndex, Point Offset);

    private sealed record TutorialDefinition(string Id, string Name, string Description, IReadOnlyList<TutorialStep> Steps)
    {
        public bool IsCompleted(PlayerProfile? profile) =>
            profile?.CompletedTutorialIds.Contains(Id, StringComparer.OrdinalIgnoreCase) == true;
    }

    private sealed record TutorialStep(
        TutorialStepKind Kind,
        string Title,
        string Instruction,
        string CommandKind,
        string ExpectedPayload,
        string Hint)
    {
        public bool Matches(string commandKind, string payload) =>
            CommandKind.Equals(commandKind, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(ExpectedPayload) || ExpectedPayload.Equals(payload, StringComparison.OrdinalIgnoreCase));
    }

    private enum TutorialStepKind
    {
        AdvancePhase,
        AddEnergy,
        PlayCard,
        Sacrifice,
        Block,
        ResolveTarget
    }

    private sealed class TutorialRuntimeState
    {
        public TutorialRuntimeState(TutorialDefinition definition)
        {
            Definition = definition;
        }

        public TutorialDefinition Definition { get; }
        public int StepIndex { get; set; }
        public TutorialStep? CurrentStep => StepIndex >= 0 && StepIndex < Definition.Steps.Count ? Definition.Steps[StepIndex] : null;
        public bool IsComplete => StepIndex >= Definition.Steps.Count;
    }

    private sealed record MatchTimelineEntry(string Icon, string Text, Color Color);

    private sealed class GameSettings
    {
        public bool Fullscreen { get; set; }
        public int WindowWidth { get; set; } = WindowedWidth;
        public int WindowHeight { get; set; } = WindowedHeight;
        public int MusicVolume { get; set; } = 70;
        public int SoundVolume { get; set; } = 80;
        public bool MuteAudio { get; set; }
        public bool CardZoom { get; set; } = true;
        public int InteractionSpeedPercent { get; set; } = 70;
        public bool ReducedMotion { get; set; }

        public static string SettingsFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DragonCards", "settings.json");

        public static GameSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(SettingsFilePath));
                    var root = document.RootElement;
                    var loaded = new GameSettings
                    {
                        Fullscreen = ReadBoolean(root, nameof(Fullscreen), defaultValue: false),
                        WindowWidth = ReadInteger(root, nameof(WindowWidth), WindowedWidth),
                        WindowHeight = ReadInteger(root, nameof(WindowHeight), WindowedHeight),
                        MusicVolume = ReadInteger(root, nameof(MusicVolume), 70),
                        SoundVolume = ReadInteger(root, nameof(SoundVolume), 80),
                        MuteAudio = ReadBoolean(root, nameof(MuteAudio), defaultValue: false),
                        CardZoom = ReadBoolean(root, nameof(CardZoom), defaultValue: true),
                        InteractionSpeedPercent = ReadInteger(root, nameof(InteractionSpeedPercent), 70),
                        ReducedMotion = ReadBoolean(root, nameof(ReducedMotion), defaultValue: false)
                    };
                    loaded.Normalize();
                    return loaded;
                }
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }

            return new GameSettings();
        }

        public void Save()
        {
            Normalize();
            var folder = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            try
            {
                using var stream = File.Open(SettingsFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                writer.WriteStartObject();
                writer.WriteBoolean(nameof(Fullscreen), Fullscreen);
                writer.WriteNumber(nameof(WindowWidth), WindowWidth);
                writer.WriteNumber(nameof(WindowHeight), WindowHeight);
                writer.WriteNumber(nameof(MusicVolume), MusicVolume);
                writer.WriteNumber(nameof(SoundVolume), SoundVolume);
                writer.WriteBoolean(nameof(MuteAudio), MuteAudio);
                writer.WriteBoolean(nameof(CardZoom), CardZoom);
                writer.WriteNumber(nameof(InteractionSpeedPercent), InteractionSpeedPercent);
                writer.WriteBoolean(nameof(ReducedMotion), ReducedMotion);
                writer.WriteEndObject();
            }
            catch (IOException)
            {
            }
        }

        private static int ReadInteger(JsonElement root, string propertyName, int defaultValue) =>
            root.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
                ? value
                : defaultValue;

        private static bool ReadBoolean(JsonElement root, string propertyName, bool defaultValue) =>
            root.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? property.GetBoolean()
                : defaultValue;

        private void Normalize()
        {
            WindowWidth = Math.Clamp(WindowWidth, 1024, 3840);
            WindowHeight = Math.Clamp(WindowHeight, 576, 2160);
            MusicVolume = Math.Clamp(MusicVolume, 0, 100);
            SoundVolume = Math.Clamp(SoundVolume, 0, 100);
            InteractionSpeedPercent = Math.Clamp(InteractionSpeedPercent, 35, 200);
        }
    }

    private sealed class DeckBuilderState
    {
        public const int PageSize = 12;
        private readonly GameData _data;
        private readonly Dictionary<string, int> _cards;
        private IReadOnlyDictionary<string, int> _ownedCards = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public DeckBuilderState(GameData data, DeckDefinition startingDeck)
        {
            _data = data;
            _cards = startingDeck.Cards.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
            DeckId = startingDeck.Id;
            DeckName = startingDeck.Name;
            ModeId = string.IsNullOrWhiteSpace(startingDeck.ModeId) ? DragonCardsModeIds.DragonDuel : startingDeck.ModeId;
            ElementFilters = ["All", .. data.GameModesById["dragon-duel"].Elements];
            TypeFilters = ["All", .. data.GameModesById["dragon-duel"].AllowedCardTypes];
            RarityFilters = ["All", .. CardRarities.All];
            SetFilters = ["All", .. data.Cards.Where(card => !BasicEnergy.IsBasicEnergyCard(card) && !EnergySource.IsEnergySourceToken(card)).Select(card => card.SetId).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(setId => setId, StringComparer.OrdinalIgnoreCase)];
            SelectedCardId = data.Cards.FirstOrDefault()?.Id;
        }

        public string DeckId { get; private set; }
        public string DeckName { get; private set; }
        public string ModeId { get; private set; }
        public string ElementFilter { get; set; } = "All";
        public string TypeFilter { get; set; } = "All";
        public string RarityFilter { get; set; } = "All";
        public string SetFilter { get; set; } = "All";
        public CollectionOwnershipFilter OwnershipFilter { get; set; }
        public CollectionSortMode SortMode { get; set; }
        public bool IsSandbox { get; private set; }
        public int Page { get; set; }
        public string? SelectedCardId { get; set; }
        public IReadOnlyList<string> ElementFilters { get; }
        public IReadOnlyList<string> TypeFilters { get; }
        public IReadOnlyList<string> RarityFilters { get; }
        public IReadOnlyList<string> SetFilters { get; }

        public IReadOnlyList<CardDefinition> FilteredCards => CollectionDiscoveryService.FilterAndSort(
            _data.Cards,
            _ownedCards,
            ElementFilter,
            TypeFilter,
            RarityFilter,
            SetFilter,
            OwnershipFilter,
            SortMode);

        public CollectionSummary CollectionSummary => CollectionDiscoveryService.Summarize(_data.Cards, _ownedCards);

        public CardDefinition? SelectedCard =>
            SelectedCardId is not null && _data.CardsById.TryGetValue(SelectedCardId, out var card) ? card : null;

        public int CardCount(string cardId) => _cards.GetValueOrDefault(cardId);

        public void Add(string cardId)
        {
            _cards[cardId] = CardCount(cardId) + 1;
        }

        public void Remove(string cardId)
        {
            var count = CardCount(cardId);
            if (count <= 1)
            {
                _cards.Remove(cardId);
                return;
            }

            _cards[cardId] = count - 1;
        }

        public void ReplaceWith(DeckDefinition deck)
        {
            _cards.Clear();
            foreach (var (cardId, count) in deck.Cards)
            {
                if (count > 0)
                {
                    _cards[cardId] = count;
                }
            }

            SetIdentity(deck.Id, deck.Name, deck.ModeId);
        }

        public void SetIdentity(string id, string name, string modeId)
        {
            DeckId = id;
            DeckName = name;
            ModeId = string.IsNullOrWhiteSpace(modeId) ? DragonCardsModeIds.DragonDuel : modeId;
        }

        public void ConfigureCollection(PlayerProfile? profile, bool isSandbox)
        {
            _ownedCards = profile?.OwnedCards is { } cards
                ? new Dictionary<string, int>(cards, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            IsSandbox = isSandbox;
        }

        public DeckDefinition CreateDeck() => new()
        {
            Id = DeckId,
            Name = DeckName,
            ModeId = ModeId,
            Cards = _cards
                .Where(entry => entry.Value > 0)
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase)
        };
    }
}
