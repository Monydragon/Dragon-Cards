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
    private const int VirtualWidth = 1600;
    private const int VirtualHeight = 900;
    private const int WindowedWidth = 1440;
    private const int WindowedHeight = 900;
    private static readonly string[] ElementOrder = ["Fire", "Ice", "Wind", "Earth", "Lightning", "Water", "Light", "Dark"];
    private static readonly (int Width, int Height)[] WindowSizeOptions = [(1280, 720), (1440, 900), (1600, 900), (1920, 1080)];
    private static readonly IReadOnlyList<TutorialDefinition> TutorialDefinitions = CreateTutorialDefinitions();

    private readonly GraphicsDeviceManager _graphics;
    private readonly bool _captureScreensOnStart;
    private readonly string _captureDirectory;
    private readonly GameSettings _settings = GameSettings.Load();
    private SpriteBatch? _spriteBatch;
    private SpriteFont? _font;
    private Texture2D? _pixel;
    private readonly Dictionary<string, Texture2D> _rarityIcons = new(StringComparer.OrdinalIgnoreCase);
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
    private readonly PresentationQueue _presentation = new();
    private MatchKind _matchKind = MatchKind.Hotseat;
    private const int HumanPlayerIndex = 0;
    private const int AiPlayerIndex = 1;
    private int _selectedHandIndex = -1;
    private int _selectedUnitIndex = -1;
    private int _selectedSupportIndex = -1;
    private int _selectedBlockerIndex = -1;
    private int _menuFocus;
    private int _multiplayerFocus;
    private int _optionsFocus;
    private int _deckFocusIndex;
    private int _tutorialFocus;
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
    private string _joinInviteCode = "";
    private string _multiplayerNotice = "Direct online match sync is ready for LAN host/join play.";
    private int _logScrollOffset;
    private TutorialRuntimeState? _tutorial;
    private bool _modalInputActive;
    private bool _drawingModal;

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
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData([Color.White]);
        LoadRarityIcons();
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
        UpdateViewportMapping();
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
            GoBack();
        }

        _presentation.Update(elapsedSeconds);
        if (_presentation.IsBlocking)
        {
            if (Pressed(Keys.Space) || Pressed(Buttons.A) || Hit(new Rectangle(0, 0, VirtualWidth, VirtualHeight)))
            {
                _presentation.SkipActive();
            }

            base.Update(gameTime);
            return;
        }

        if (TryOpenPendingResultScreen())
        {
            base.Update(gameTime);
            return;
        }

        HandleControllerInput();
        HandleMouseDrag();
        TryAdvanceAiTurn();
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
        DrawBackdrop();
        DrawHeader();
        _modalInputActive = IsDecisionPromptActive();

        if (_screen == Screen.MainMenu)
        {
            DrawMainMenu();
        }
        else if (_screen == Screen.PlayerCreation)
        {
            DrawPlayerCreation();
        }
        else if (_screen == Screen.Store)
        {
            DrawStore();
        }
        else if (_screen == Screen.PackOpening)
        {
            DrawPackOpening();
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
            DrawDeckBuilder();
        }
        else
        {
            DrawMatch();
        }

        DrawStatusBar();
        DrawPresentationOverlay();
        DrawDecisionPromptOverlay();
        DrawTutorialStepOverlay();
        DrawZoomPreview();
        DrawDraggedCard();
        DrawElementPicker();
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
        DrawText("Dragon Cards", new Vector2(34, 18), Color.White, 1.35f);
        DrawText("Dragon Duel", new Vector2(260, 26), new Color(207, 217, 229), 0.78f);
    }

    private void DrawMainMenu()
    {
        var currentDeck = CurrentDeck();
        var opponentDeck = OpponentDeck();
        var currentDeckIssues = GameDataValidator.ValidateDeck(currentDeck, _data);
        var opponentDeckIssues = GameDataValidator.ValidateDeck(opponentDeck, _data);
        var ownershipIssues = ValidateCurrentDeckOwnership(currentDeck);
        var canStart = currentDeckIssues.Count == 0 && opponentDeckIssues.Count == 0 && ownershipIssues.Count == 0 && _dataIssues.Count == 0;

        DrawText("Dragon Cards", new Vector2(54, 108), Color.White, 1.2f);
        DrawText(MainMenuSubtitle(), new Vector2(56, 146), new Color(196, 207, 220), 0.74f);

        DrawDeckSummary(new Rectangle(54, 198, 572, 292), "Single Player", currentDeck, currentDeckIssues, "fire-ashen-champion");
        DrawDeckSummary(new Rectangle(654, 198, 572, 292), "AI Opponent", opponentDeck, opponentDeckIssues, "ice-crystal-champion");

        DrawPanel(new Rectangle(54, 532, 792, 214), new Color(31, 37, 46), border: new Color(81, 96, 116));
        DrawText("Dragon Duel", new Vector2(84, 560), Color.White, 1.0f);
        DrawText("Elemental energy, units, supports, spells, blocking combat, sacrifice, and cinematic card play.", new Rectangle(84, 598, 708, 58), new Color(205, 214, 225), 0.76f);
        DrawModePips(new Rectangle(84, 662, 650, 58));
        var validation = _dataIssues.Count == 0 ? "Data validation passed." : $"{_dataIssues.Count} data issue(s) need attention.";
        DrawText(validation, new Vector2(84, 716), _dataIssues.Count == 0 ? new Color(148, 224, 164) : new Color(255, 162, 128), 0.72f);

        DrawProfileSummary(new Rectangle(896, 532, 370, 214));

        if (Button(new Rectangle(1292, 182, 248, 54), "Start Game (Single Player)", canStart, _usingController && _menuFocus == 0))
        {
            StartMatch(currentDeck, opponentDeck, MatchKind.VsAi);
        }

        if (Button(new Rectangle(1292, 250, 248, 54), "Multiplayer", canStart, _usingController && _menuFocus == 1))
        {
            EnsureHostInvite();
            _screen = Screen.Multiplayer;
            _status = "Multiplayer opened.";
        }

        if (Button(new Rectangle(1292, 318, 248, 54), "Deck Builder", focused: _usingController && _menuFocus == 2))
        {
            _screen = Screen.DeckBuilder;
            _status = "Deck builder opened.";
        }

        if (Button(new Rectangle(1292, 386, 248, 54), "Store / Packs", focused: _usingController && _menuFocus == 3))
        {
            _screen = Screen.Store;
            _status = "Store opened.";
        }

        if (Button(new Rectangle(1292, 454, 248, 54), "Tutorials", focused: _usingController && _menuFocus == 4))
        {
            _screen = Screen.Tutorials;
            _status = "Tutorials opened.";
        }

        if (Button(new Rectangle(1292, 522, 248, 54), "Options", focused: _usingController && _menuFocus == 5))
        {
            _screen = Screen.Options;
            _status = "Options opened.";
        }

        if (Button(new Rectangle(1292, 590, 248, 54), "New Game", focused: _usingController && _menuFocus == 6))
        {
            BeginNewGame();
        }

        if (Button(new Rectangle(1292, 658, 248, 54), "Exit", focused: _usingController && _menuFocus == 7))
        {
            try { Exit(); }
            catch (PlatformNotSupportedException) { }
        }
    }

    private void DrawMultiplayerMenu()
    {
        EnsureHostInvite();
        var currentDeck = CurrentDeck();
        var opponentDeck = OpponentDeck();
        var canStart = GameDataValidator.ValidateDeck(currentDeck, _data).Count == 0 &&
            GameDataValidator.ValidateDeck(opponentDeck, _data).Count == 0 &&
            ValidateCurrentDeckOwnership(currentDeck).Count == 0 &&
            _dataIssues.Count == 0;

        DrawText("Multiplayer", new Vector2(54, 108), Color.White, 1.2f);
        DrawText("Local play and direct LAN host/join are available now. Host a match, share the invite, or type a join code.", new Rectangle(56, 146, 980, 36), new Color(196, 207, 220), 0.68f);

        DrawPanel(new Rectangle(54, 198, 560, 386), new Color(31, 37, 46), border: new Color(81, 96, 116));
        DrawText("Play", new Vector2(84, 232), Color.White, 0.96f);
        if (Button(new Rectangle(84, 286, 280, 52), "Local Hotseat", canStart, _usingController && _multiplayerFocus == 0))
        {
            StartMatch(currentDeck, opponentDeck, MatchKind.Hotseat);
        }

        if (Button(new Rectangle(84, 354, 280, 52), "Host Direct Match", focused: _usingController && _multiplayerFocus == 1))
        {
            BeginHostDirectMatch();
        }

        if (Button(new Rectangle(84, 422, 280, 52), "Join Direct Match", focused: _usingController && _multiplayerFocus == 2))
        {
            BeginJoinDirectMatch();
        }

        if (Button(new Rectangle(84, 490, 280, 52), "Back", focused: _usingController && _multiplayerFocus == 3))
        {
            _screen = Screen.MainMenu;
            _status = "Returned to main menu.";
        }

        DrawPanel(new Rectangle(656, 198, 850, 386), new Color(31, 37, 46), border: new Color(81, 96, 116));
        DrawText("Direct Invite", new Vector2(690, 232), Color.White, 0.96f);
        DrawText("Host code", new Vector2(690, 282), new Color(205, 214, 225), 0.6f);
        DrawText(ChunkInviteCode(_hostInviteCode), new Rectangle(690, 312, 760, 76), new Color(232, 238, 248), 0.46f);
        DrawText("Join code", new Vector2(690, 414), new Color(205, 214, 225), 0.6f);
        DrawText(string.IsNullOrWhiteSpace(_joinInviteCode) ? "Type a DC1 invite code here, then choose Join Direct Match." : ChunkInviteCode(_joinInviteCode), new Rectangle(690, 444, 760, 46), new Color(196, 207, 220), 0.46f);
        DrawText(_multiplayerNotice, new Rectangle(690, 512, 760, 42), new Color(148, 224, 164), 0.56f);
    }

    private void DrawTutorials()
    {
        DrawText("Tutorials", new Vector2(54, 108), Color.White, 1.2f);
        DrawText("Guided lessons use prepared matches and award 250 Coins the first time each one is completed.", new Rectangle(56, 146, 980, 36), new Color(196, 207, 220), 0.68f);

        var listPanel = new Rectangle(54, 198, 690, 560);
        DrawPanel(listPanel, new Color(31, 37, 46), border: new Color(81, 96, 116));
        DrawText("Lessons", new Vector2(listPanel.X + 24, listPanel.Y + 24), Color.White, 0.92f);
        _tutorialFocus = Math.Clamp(_tutorialFocus, 0, TutorialDefinitions.Count - 1);
        for (var i = 0; i < TutorialDefinitions.Count; i++)
        {
            var tutorial = TutorialDefinitions[i];
            var completed = tutorial.IsCompleted(_profile);
            var rect = new Rectangle(listPanel.X + 24, listPanel.Y + 72 + i * 72, listPanel.Width - 48, 58);
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
            _screen = Screen.MainMenu;
            _status = "Returned to main menu.";
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
        _presentation.Clear();
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

        DrawText("Display", new Vector2(panel.X + 34, panel.Y + 32), Color.White, 0.94f);
        DrawToggleRow(0, new Rectangle(panel.X + 34, panel.Y + 76, 720, 42), "Fullscreen", _graphics.IsFullScreen ? "On" : "Off", ToggleFullscreen);
        DrawChoiceRow(1, new Rectangle(panel.X + 34, panel.Y + 128, 720, 42), "Window Size", $"{_settings.WindowWidth} x {_settings.WindowHeight}", () => CycleWindowSize(-1), () => CycleWindowSize(1));
        DrawStaticRow(new Rectangle(panel.X + 34, panel.Y + 180, 720, 42), "UI Scale", "Aspect fit to window");

        DrawText("Audio", new Vector2(panel.X + 34, panel.Y + 254), Color.White, 0.94f);
        DrawChoiceRow(2, new Rectangle(panel.X + 34, panel.Y + 298, 720, 42), "Music Volume", $"{_settings.MusicVolume}%", () => AdjustMusicVolume(-10), () => AdjustMusicVolume(10));
        DrawChoiceRow(3, new Rectangle(panel.X + 34, panel.Y + 350, 720, 42), "Sound Volume", $"{_settings.SoundVolume}%", () => AdjustSoundVolume(-10), () => AdjustSoundVolume(10));
        DrawToggleRow(4, new Rectangle(panel.X + 34, panel.Y + 402, 720, 42), "Mute Audio", _settings.MuteAudio ? "On" : "Off", ToggleMuteAudio);

        DrawText("Gameplay", new Vector2(panel.X + 34, panel.Y + 476), Color.White, 0.94f);
        DrawToggleRow(5, new Rectangle(panel.X + 34, panel.Y + 520, 720, 42), "Card Hover Zoom", _settings.CardZoom ? "On" : "Off", ToggleCardZoom);

        DrawPanel(new Rectangle(818, 274, 250, 276), new Color(34, 41, 51), border: new Color(84, 99, 119));
        DrawText("Settings File", new Vector2(846, 304), Color.White, 0.82f);
        DrawText(ShortPath(GameSettings.SettingsFilePath), new Rectangle(846, 344, 184, 96), new Color(196, 207, 220), 0.52f);
        DrawText("Audio controls are stored now and ready for future music and sound systems.", new Rectangle(846, 462, 180, 64), new Color(196, 207, 220), 0.56f);

        if (Button(new Rectangle(54, 786, 150, 42), "Back", focused: _usingController && _optionsFocus == 6))
        {
            _screen = Screen.MainMenu;
            _status = "Options saved.";
        }
    }

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

    private void DrawDeckSummary(Rectangle rect, string playerName, DeckDefinition deck, IReadOnlyList<ValidationIssue> issues, string featureCardId)
    {
        DrawPanel(rect, new Color(34, 41, 51), border: new Color(84, 99, 119));
        DrawText(playerName, new Vector2(rect.X + 28, rect.Y + 24), Color.White, 0.98f);
        DrawText(deck.Name, new Vector2(rect.X + 28, rect.Y + 62), new Color(204, 214, 226), 0.8f);
        DrawText($"{deck.Count}/50 cards", new Vector2(rect.X + 28, rect.Y + 100), issues.Count == 0 ? new Color(148, 224, 164) : new Color(255, 162, 128), 0.78f);
        DrawText(issues.Count == 0 ? "Valid for Dragon Duel." : issues[0].Message, new Rectangle(rect.X + 28, rect.Y + 136, 294, 70), new Color(196, 207, 220), 0.66f);

        var featureCard = _data.CardsById[featureCardId];
        DrawCardFrame(new Rectangle(rect.Right - 196, rect.Y + 28, 140, 196), featureCard, selected: false, exhausted: false, count: deck.Cards.GetValueOrDefault(featureCard.Id), compact: false);
    }

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
        foreach (var element in _deckBuilder.ElementFilters)
        {
            if (Button(new Rectangle(x, 164, 94, 30), element, selected: _deckBuilder.ElementFilter.Equals(element, StringComparison.OrdinalIgnoreCase)))
            {
                _deckBuilder.ElementFilter = element;
                _deckBuilder.Page = 0;
                _deckFocusIndex = 0;
            }

            x += 102;
        }

        x = 42;
        foreach (var type in _deckBuilder.TypeFilters)
        {
            if (Button(new Rectangle(x, 806, 100, 32), type, selected: _deckBuilder.TypeFilter.Equals(type, StringComparison.OrdinalIgnoreCase)))
            {
                _deckBuilder.TypeFilter = type;
                _deckBuilder.Page = 0;
                _deckFocusIndex = 0;
            }

            x += 110;
        }
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
            DrawCardFrame(new Rectangle(panel.X + 30, panel.Y + 70, 218, 306), selectedCard, selected: true, exhausted: false, count: _deckBuilder.CardCount(selectedCard.Id), compact: false);
            DrawScrollableText(CardDetailText(selectedCard), new Rectangle(panel.X + 278, panel.Y + 82, 220, 218), ref _cardDetailScrollOffset, new Color(211, 220, 231), 0.46f);
            DrawText(OwnedSummary(selectedCard), new Rectangle(panel.X + 278, panel.Y + 306, 220, 24), new Color(148, 224, 164), 0.54f);

            if (Button(new Rectangle(panel.X + 278, panel.Y + 336, 104, 40), "Add", CanAddCardToDeck(selectedCard, deck)))
            {
                _deckBuilder.Add(selectedCard.Id);
                _status = $"Added {selectedCard.Name}.";
            }

            if (Button(new Rectangle(panel.X + 394, panel.Y + 336, 104, 40), "Remove", _deckBuilder.CardCount(selectedCard.Id) > 0))
            {
                _deckBuilder.Remove(selectedCard.Id);
                _status = $"Removed {selectedCard.Name}.";
            }
        }

        Fill(new Rectangle(panel.X + 28, panel.Y + 414, panel.Width - 56, 1), new Color(86, 100, 120));
        DrawText("Deck Status", new Vector2(panel.X + 28, panel.Y + 444), Color.White, 0.94f);
        var allIssues = issues.Concat(ownershipIssues).ToArray();
        DrawText($"{deck.Count}/50 cards", new Vector2(panel.X + 28, panel.Y + 482), allIssues.Length == 0 ? new Color(148, 224, 164) : new Color(255, 162, 128), 0.82f);
        DrawText(allIssues.Length == 0 ? "Valid for Dragon Duel." : allIssues[0].Message, new Rectangle(panel.X + 28, panel.Y + 518, 440, 72), new Color(201, 212, 225), 0.7f);

        if (Button(new Rectangle(panel.X + 28, panel.Bottom - 76, 132, 42), "Save Deck"))
        {
            SaveDeck(deck);
        }

        if (Button(new Rectangle(panel.X + 178, panel.Bottom - 76, 112, 42), "Back"))
        {
            _screen = Screen.MainMenu;
            _status = "Returned to main menu.";
        }
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
            return $"{state.Players[state.PendingCombatAction.PriorityPlayerIndex].Name}: combat action";
        }

        if (state.PendingAttack is not null)
        {
            var attacker = state.Players[state.PendingAttack.AttackerPlayerIndex].UnitField
                .FirstOrDefault(card => card.Id == state.PendingAttack.AttackerInstanceId);
            var attackerName = attacker is null ? "a unit" : state.CardName(attacker);
            return state.PendingAttack.AttackerPlayerIndex != LocalPlayerIndexForMatch() && _matchKind != MatchKind.Hotseat
                ? $"AI attacked with {attackerName}"
                : "Choose blocker";
        }

        if (_matchKind != MatchKind.Hotseat && state.ActivePlayerIndex != LocalPlayerIndexForMatch())
        {
            return _matchKind == MatchKind.VsAi ? "AI is playing" : "Remote player is playing";
        }

        return state.CurrentPhase.Equals("Main", StringComparison.OrdinalIgnoreCase)
            ? "Your Main Phase"
            : $"{state.ActivePlayer.Name}'s {state.CurrentPhase}";
    }

    private void DrawMatchLog(Rectangle rect)
    {
        if (_engine is null)
        {
            return;
        }

        DrawPanel(rect, new Color(26, 32, 41, 220), border: new Color(75, 90, 110));
        DrawText("Log", new Vector2(rect.X + 12, rect.Y + 10), Color.White, 0.46f);

        var content = new Rectangle(rect.X + 12, rect.Y + 30, rect.Width - 30, rect.Height - 40);
        var lines = WrappedLines(_engine.State.Log, content.Width, 0.34f).ToArray();
        var lineHeight = Math.Max(10, (int)MathF.Ceiling(_font!.LineSpacing * 0.34f));
        var visibleCount = Math.Max(1, content.Height / lineHeight);
        var maxOffset = Math.Max(0, lines.Length - visibleCount);
        _logScrollOffset = Math.Clamp(_logScrollOffset, 0, maxOffset);
        var start = Math.Max(0, lines.Length - visibleCount - _logScrollOffset);
        var y = content.Y;
        for (var i = start; i < Math.Min(lines.Length, start + visibleCount); i++)
        {
            DrawFittedText(lines[i], new Vector2(content.X, y), content.Width, new Color(196, 207, 220), 0.34f, 0.24f);
            y += lineHeight;
        }

        if (maxOffset > 0)
        {
            var track = new Rectangle(rect.Right - 12, content.Y, 4, content.Height);
            Fill(track, new Color(52, 62, 76));
            var thumbHeight = Math.Max(14, content.Height * visibleCount / Math.Max(visibleCount, lines.Length));
            var thumbTravel = Math.Max(1, content.Height - thumbHeight);
            var thumbY = content.Y + (int)MathF.Round((maxOffset - _logScrollOffset) / (float)maxOffset * thumbTravel);
            Fill(new Rectangle(track.X, thumbY, track.Width, thumbHeight), new Color(142, 158, 178));
        }
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
        var canHumanAdd = isActive && CanHumanUseActions(_engine!.State) && _engine!.CanAddEnergy();
        var label = canHumanAdd ? "Energy / Add" : "Energy";
        DrawText(label, new Vector2(rect.X, rect.Y - 26), new Color(199, 209, 222), 0.58f);
        var isDropTarget = isActive && _draggedHandCard is not null && rect.Contains(_virtualMouse);
        Fill(rect, isDropTarget ? canHumanAdd ? new Color(48, 68, 61) : new Color(66, 42, 39) : new Color(28, 34, 43));
        Border(rect, isDropTarget ? canHumanAdd ? new Color(148, 224, 164) : new Color(255, 145, 120) : new Color(70, 84, 104), isDropTarget ? 3 : 1);

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

    private void DrawZoneStats(PlayerState player, Rectangle rect)
    {
        Fill(rect, new Color(28, 34, 43, 180));
        Border(rect, new Color(70, 84, 104), 1);
        DrawFittedText($"Deck {player.Deck.Count}", new Vector2(rect.X + 10, rect.Y + 8), 64, new Color(205, 214, 225), 0.46f, 0.28f);
        DrawFittedText($"Discard {player.DiscardPile.Count}", new Vector2(rect.X + 82, rect.Y + 8), 68, new Color(205, 214, 225), 0.46f, 0.28f);
        DrawFittedText($"Damage {player.DamageZone.Count}/7", new Vector2(rect.X + 10, rect.Y + 28), 136, player.DamageZone.Count >= 5 ? new Color(255, 190, 120) : new Color(205, 214, 225), 0.48f, 0.3f);
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
        for (var i = 0; i < Math.Min(visiblePlayer.Hand.Count, 9); i++)
        {
            var card = state.DefinitionFor(visiblePlayer.Hand[i]);
            var rect = HandCardRect(i);
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
            DrawCardFrame(new Rectangle(area.X + 72, area.Y + 48, 158, 222), inspectionCard, selected: true, exhausted: false, count: _zoomCount, compact: false);
            DrawScrollableText(CardDetailText(inspectionCard), new Rectangle(area.X + 18, area.Y + 284, area.Width - 36, 104), ref _cardDetailScrollOffset, new Color(205, 214, 225), 0.36f);
        }
        else
        {
            DrawPanel(new Rectangle(area.X + 72, area.Y + 48, 158, 222), new Color(24, 29, 37), border: new Color(70, 84, 104));
            DrawFittedCenteredText("Select or hover a card", new Rectangle(area.X + 84, area.Y + 144, 134, 28), new Color(165, 176, 190), 0.48f, 0.3f);
        }

        DrawText(SelectedPlayHint(selectedHandCard), new Rectangle(area.X + 18, area.Y + 394, area.Width - 36, 30), new Color(196, 207, 220), 0.38f);
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
        DrawCardFrame(rect, card, selected || hover, exhausted: false, count, compact);
        if (hover)
        {
            RequestZoom(card, count, rect);
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
        DrawCardFrame(rect, card, selected || hover, exhausted, count, compact);
        if (unavailable)
        {
            Fill(rect, new Color(0, 0, 0, 112));
        }

        if (playable)
        {
            Border(new Rectangle(rect.X - 2, rect.Y - 2, rect.Width + 4, rect.Height + 4), new Color(148, 224, 164), selected || hover ? 3 : 2);
        }

        if (hover)
        {
            RequestZoom(card, count, rect);
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

        var title = new Rectangle(inner.X + 3, inner.Y + 3, inner.Width - 6, 12);
        var costs = new Rectangle(inner.X + 3, title.Bottom + 1, inner.Width - 6, 11);
        var footer = new Rectangle(inner.X + 3, inner.Bottom - 16, inner.Width - 6, 14);
        var type = new Rectangle(inner.X + 3, footer.Y - 17, inner.Width - 6, 13);
        var art = new Rectangle(inner.X + 5, costs.Bottom + 3, inner.Width - 10, Math.Max(22, type.Y - costs.Bottom - 6));

        Fill(title, Color.Lerp(color, Color.Black, 0.2f + dim * 0.5f));
        Fill(new Rectangle(title.X, title.Bottom - 2, title.Width, 2), Color.Lerp(color, Color.White, 0.34f));
        var rarity = new Rectangle(title.Right - 13, title.Y + 1, 11, 10);
        DrawRarityBadge(rarity, card.Rarity, compact: true);
        DrawFittedText(card.Name, new Vector2(title.X + 3, title.Y + 1), title.Width - rarity.Width - 10, Color.White, 0.22f, 0.12f);
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
        DrawFittedText(typeLine, new Vector2(type.X + 3, type.Y + 1), type.Width - 6, new Color(33, 31, 29), 0.24f, 0.14f);

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

        var tight = rect.Height < 220;
        var titleHeight = tight ? 34 : 40;
        var typeHeight = tight ? 18 : 22;
        var footerHeight = tight ? 22 : 26;
        var title = new Rectangle(inner.X + 4, inner.Y + 4, inner.Width - 8, titleHeight);
        var footer = new Rectangle(inner.X + 4, inner.Bottom - footerHeight - 4, inner.Width - 8, footerHeight);
        var artHeight = Math.Clamp(rect.Height * 34 / 100, tight ? 48 : 64, tight ? 58 : 110);
        var art = new Rectangle(inner.X + 8, title.Bottom + 5, inner.Width - 16, artHeight);
        var type = new Rectangle(inner.X + 6, art.Bottom + 5, inner.Width - 12, typeHeight);
        var rules = new Rectangle(inner.X + 6, type.Bottom + 5, inner.Width - 12, Math.Max(18, footer.Y - type.Bottom - 10));

        Fill(title, Color.Lerp(color, Color.Black, 0.2f + dim * 0.5f));
        Fill(new Rectangle(title.X, title.Bottom - 3, title.Width, 3), Color.Lerp(color, Color.White, 0.34f));
        var rarity = new Rectangle(title.Right - (tight ? 18 : 22), title.Y + 4, tight ? 15 : 18, tight ? 15 : 18);
        DrawRarityBadge(rarity, card.Rarity, compact: true);
        DrawFittedText(card.Name, new Vector2(title.X + 6, title.Y + 3), title.Width - rarity.Width - 18, Color.White, tight ? 0.38f : 0.46f, 0.18f);
        DrawCostBadges(card, new Rectangle(title.X + 6, title.Bottom - (tight ? 16 : 19), title.Width - 12, tight ? 13 : 16), compact: false);

        Fill(art, Color.Lerp(color, Color.Black, 0.12f + dim * 0.4f));
        Fill(new Rectangle(art.X + 5, art.Y + 5, art.Width - 10, art.Height - 10), Color.Lerp(color, Color.White, 0.2f));
        Fill(new Rectangle(art.X + 10, art.Y + 10, art.Width - 20, Math.Max(5, art.Height / 7)), new Color(255, 255, 255, exhausted ? 22 : 58));
        Fill(new Rectangle(art.X + 10, art.Bottom - Math.Max(11, art.Height / 5), art.Width - 20, Math.Max(5, art.Height / 8)), new Color(0, 0, 0, exhausted ? 42 : 64));
        DrawFittedCenteredText(ElementDisplay(card), Inset(art, 10), new Color(255, 255, 255, exhausted ? 86 : 176), tight ? 0.72f : 0.94f, 0.36f);
        Border(art, Color.Lerp(Color.Black, color, 0.28f), 2);

        Fill(type, Color.Lerp(TypeColor(card.Type), Color.White, 0.66f));
        Border(type, Color.Lerp(TypeColor(card.Type), Color.Black, 0.38f), 1);
        var typeLine = $"{card.Type} / {string.Join(" ", card.Elements)}";
        DrawFittedText(typeLine, new Vector2(type.X + 5, type.Y + 2), type.Width - 10, new Color(33, 31, 29), tight ? 0.34f : 0.46f, 0.18f);

        Fill(rules, new Color(250, 246, 233));
        Border(rules, new Color(84, 73, 58), 1);
        DrawText(CardFrameRulesText(card), new Rectangle(rules.X + 6, rules.Y + 5, rules.Width - 12, rules.Height - 10), new Color(38, 35, 31), tight ? 0.26f : 0.34f);

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
            DrawFittedCenteredText(card.Power.ToString(), Inset(powerRect, 2), Color.White, compact ? 0.24f : 0.38f, compact ? 0.16f : 0.22f);
        }

        if (card.Abilities.Count > 0)
        {
            var width = compact ? Math.Min(28, footer.Width - 6) : Math.Min(44, footer.Width - 8);
            abilityRect = new Rectangle(footer.X + 2, footer.Y + 2, width, footer.Height - 4);
            Fill(abilityRect, new Color(44, 48, 58));
            Border(abilityRect, new Color(183, 204, 232), 1);
            DrawFittedCenteredText("ACT", Inset(abilityRect, 2), Color.White, compact ? 0.22f : 0.34f, compact ? 0.14f : 0.22f);
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
            DrawFittedCenteredText($"x{count}", Inset(badge, 2), Color.White, compact ? 0.22f : 0.34f, compact ? 0.14f : 0.2f);
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
            _spriteBatch!.Draw(icon, iconRect, Color.White);
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
            DrawFittedCenteredText("0", Inset(free, 1), Color.White, compact ? 0.22f : 0.34f, compact ? 0.14f : 0.2f);
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
            DrawFittedCenteredText(label, Inset(badge, compact ? 1 : 2), Color.White, compact ? 0.18f : 0.3f, compact ? 0.1f : 0.18f);
            x += badgeWidth + gap;
        }
    }

    private void DrawZoomPreview()
    {
        if (!_settings.CardZoom ||
            _zoomCard is null ||
            _draggedHandCard is not null ||
            _chooseFreeEnergy ||
            _engine?.State.PendingEnergyChoice is not null)
        {
            return;
        }

        const int width = 300;
        const int height = 420;
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
        DrawCardFrame(rect, _zoomCard, selected: true, exhausted: false, count: _zoomCount, compact: false);
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
        if (_engine is null || _presentation.Active is not { } beat)
        {
            return;
        }

        var progress = EaseOutCubic(beat.Progress);
        var pulse = (float)Math.Sin(beat.Progress * Math.PI);
        var color = PresentationColor(beat.Event);
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
        if (beat.Event.Kind is MatchEventKind.EnergyGained or MatchEventKind.EnergySpent or MatchEventKind.EnergyConverted)
        {
            to = ZoneCenter(new ZoneRef(beat.Event.PlayerIndex, "EnergyPool"), beat.Event.PlayerIndex);
            var radius = 28 + (int)(pulse * 18);
            var pulseRect = new Rectangle(to.X - radius, to.Y - radius, radius * 2, radius * 2);
            Border(pulseRect, color, 3);
            DrawFittedCenteredText($"{beat.Event.Element} {SignedAmount(beat.Event)}", new Rectangle(to.X - 58, to.Y - 12, 116, 24), Color.White, 0.58f, 0.36f);
            return;
        }

        if (beat.Event.Kind is MatchEventKind.AttackDeclared or MatchEventKind.BlockDeclared or MatchEventKind.CombatActionQueued or MatchEventKind.CombatActionPassed or MatchEventKind.CombatResolved or MatchEventKind.TargetResolved or MatchEventKind.CardReadied)
        {
            var center = beat.Event.Kind == MatchEventKind.AttackDeclared
                ? LerpPoint(from, new Point(800, 410), progress)
                : to;
            var radius = 34 + (int)(pulse * 24);
            Border(new Rectangle(center.X - radius, center.Y - radius, radius * 2, radius * 2), color, 3);
            DrawFittedCenteredText(beat.Event.Message, new Rectangle(430, 390, 740, 34), Color.White, 0.58f, 0.34f);
            return;
        }

        var point = LerpPoint(from, to, progress);
        var width = 96 + (int)(pulse * 10);
        var height = 136 + (int)(pulse * 14);
        var cardRect = new Rectangle(point.X - width / 2, point.Y - height / 2, width, height);
        if (!string.IsNullOrWhiteSpace(beat.Event.CardId) && _engine.State.Cards.TryGetValue(beat.Event.CardId, out var card))
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
            DrawFittedCenteredText(beat.Event.Message, new Rectangle(430, 390, 740, 34), Color.White, 0.56f, 0.32f);
        }
    }

    private bool IsDecisionPromptActive()
    {
        if (_screen != Screen.Match || _engine is null || _presentation.Active is not null)
        {
            return false;
        }

        var state = _engine.State;
        return CanHumanResolveEnergyChoice(state) ||
            CanHumanResolveTarget(state) ||
            CanHumanResolveCombatAction(state) ||
            CanHumanResolveBlock(state);
    }

    private bool CanHumanResolveEnergyChoice(MatchState state) =>
        state.WinnerIndex is null &&
        state.PendingEnergyChoice is not null &&
        (_matchKind == MatchKind.Hotseat || state.PendingEnergyChoice.PlayerIndex == LocalPlayerIndexForMatch());

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
        DrawScrollableText(PromptEffectText(choice.EffectText, choice.CardId), new Rectangle(panel.X + 252, panel.Y + 142, panel.Width - 286, 116), ref _cardDetailScrollOffset, new Color(211, 220, 231), 0.42f);
        DrawText("Choose an element", new Vector2(panel.X + 252, panel.Y + 286), Color.White, 0.62f);

        var elements = _engine.State.Mode.Elements;
        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            var column = i % 4;
            var row = i / 4;
            var rect = new Rectangle(panel.X + 252 + column * 168, panel.Y + 326 + row * 62, 146, 44);
            var maxed = _engine.State.Players[choice.PlayerIndex].EnergyPool.GetValueOrDefault(element) >= _engine.State.Mode.EnergyRules.MaxPerElement;
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
        DrawScrollableText(PromptEffectText(choice.EffectText, choice.CardId), new Rectangle(panel.X + 252, panel.Y + 142, panel.Width - 286, 96), ref _cardDetailScrollOffset, new Color(211, 220, 231), 0.42f);
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
            DrawText(option.Ability.RulesText, new Rectangle(rect.X + 12, rect.Y + 62, rect.Width - 92, 38), new Color(205, 214, 225), 0.28f);
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
        var clicked = enabled && Hit(rect);
        var hover = enabled && rect.Contains(_virtualMouse);
        var fill = selected
            ? new Color(80, 111, 137)
            : hover || focused
                ? new Color(72, 86, 104)
                : new Color(45, 54, 66);
        if (!enabled)
        {
            fill = new Color(34, 39, 48);
        }

        Fill(rect, fill);
        Border(rect, focused ? new Color(244, 230, 158) : new Color(88, 102, 120), focused ? 3 : 1);
        var scale = rect.Height < 34 ? 0.6f : 0.66f;
        DrawFittedCenteredText(label, Inset(rect, 8), enabled ? Color.White : new Color(120, 126, 136), scale, 0.42f);
        return clicked;
    }

    private void Fill(Rectangle rect, Color color) => _spriteBatch!.Draw(_pixel!, rect, color);

    private void Border(Rectangle rect, Color color, int thickness)
    {
        Fill(new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
        Fill(new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
        Fill(new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
        Fill(new Rectangle(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
    }

    private void DrawText(string text, Vector2 position, Color color, float scale) =>
        _spriteBatch!.DrawString(_font!, text, position, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);

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

    private void DrawScrollableText(string text, Rectangle bounds, ref int scrollOffset, Color color, float scale)
    {
        Fill(bounds, new Color(22, 27, 35, 150));
        Border(bounds, new Color(66, 80, 98), 1);
        var content = Inset(bounds, 8);
        var sourceLines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lines = WrappedLines(sourceLines, content.Width - 8, scale).ToArray();
        var lineHeight = Math.Max(10, (int)MathF.Ceiling(_font!.LineSpacing * scale * 1.18f));
        var visibleCount = Math.Max(1, content.Height / lineHeight);
        var maxOffset = Math.Max(0, lines.Length - visibleCount);
        scrollOffset = Math.Clamp(scrollOffset, 0, maxOffset);
        var y = content.Y;
        for (var i = scrollOffset; i < Math.Min(lines.Length, scrollOffset + visibleCount); i++)
        {
            DrawText(lines[i], new Vector2(content.X, y), color, scale);
            y += lineHeight;
        }

        if (maxOffset > 0)
        {
            var track = new Rectangle(bounds.Right - 8, content.Y, 4, content.Height);
            Fill(track, new Color(52, 62, 76));
            var thumbHeight = Math.Max(14, content.Height * visibleCount / Math.Max(visibleCount, lines.Length));
            var thumbTravel = Math.Max(1, content.Height - thumbHeight);
            var thumbY = content.Y + (int)MathF.Round(scrollOffset / (float)maxOffset * thumbTravel);
            Fill(new Rectangle(track.X, thumbY, track.Width, thumbHeight), new Color(142, 158, 178));
        }
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
            for (var i = 0; i < Math.Min(active.Hand.Count, 9); i++)
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

        if (_screen == Screen.Store)
        {
            if (new Rectangle(54, 198, 690, 560).Contains(_virtualMouse))
            {
                _storeScrollOffset += delta < 0 ? 1 : -1;
                _storeScrollOffset = Math.Max(0, _storeScrollOffset);
                return;
            }

            if (new Rectangle(1004, 352, 506, 286).Contains(_virtualMouse) ||
                new Rectangle(1050, 352, 460, 286).Contains(_virtualMouse))
            {
                _cardDetailScrollOffset += delta < 0 ? 1 : -1;
                _cardDetailScrollOffset = Math.Max(0, _cardDetailScrollOffset);
                return;
            }
        }

        if (_screen == Screen.DeckBuilder && new Rectangle(1278, 178, 220, 218).Contains(_virtualMouse))
        {
            _cardDetailScrollOffset += delta < 0 ? 1 : -1;
            _cardDetailScrollOffset = Math.Max(0, _cardDetailScrollOffset);
            return;
        }

        if (_screen == Screen.PackOpening)
        {
            _packOpeningScrollOffset += delta < 0 ? 1 : -1;
            _packOpeningScrollOffset = Math.Max(0, _packOpeningScrollOffset);
            return;
        }

        if (_screen != Screen.Match || _engine is null)
        {
            _logScrollOffset = 0;
            return;
        }

        if (!MatchLogRect().Contains(_virtualMouse))
        {
            return;
        }

        if (new Rectangle(1282, 376, 266, 104).Contains(_virtualMouse))
        {
            _cardDetailScrollOffset += delta < 0 ? 1 : -1;
            _cardDetailScrollOffset = Math.Max(0, _cardDetailScrollOffset);
            return;
        }

        _logScrollOffset += delta < 0 ? 1 : -1;
        _logScrollOffset = Math.Max(0, _logScrollOffset);
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
            if (!_engine.CanAddEnergy())
            {
                _status = _engine.IsMainPhase()
                    ? "Energy has already been added this turn."
                    : "Energy can only be added during a main phase.";
                return;
            }

            _chooseFreeEnergy = true;
            _status = "Choose an element to add energy.";
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

    private bool Pressed(Buttons button) => _gamePad.IsButtonDown(button) && !_previousGamePad.IsButtonDown(button);

    private bool DirectionPressed(Buttons negative, Buttons positive, out int delta)
    {
        delta = 0;
        if (Pressed(negative))
        {
            delta = -1;
        }
        else if (Pressed(positive))
        {
            delta = 1;
        }

        if (delta != 0)
        {
            _usingController = true;
            return true;
        }

        return false;
    }

    private void HandleControllerInput()
    {
        if (!_gamePad.IsConnected)
        {
            return;
        }

        if (_screen == Screen.MainMenu)
        {
            if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical))
            {
                _menuFocus = Math.Clamp(_menuFocus + vertical, 0, 7);
            }

            if (Pressed(Buttons.A))
            {
                _usingController = true;
                var currentDeck = CurrentDeck();
                var opponentDeck = OpponentDeck();
                if (_menuFocus == 0 && GameDataValidator.ValidateDeck(currentDeck, _data).Count == 0 && ValidateCurrentDeckOwnership(currentDeck).Count == 0)
                {
                    StartMatch(currentDeck, opponentDeck, MatchKind.VsAi);
                }
                else if (_menuFocus == 1)
                {
                    EnsureHostInvite();
                    _screen = Screen.Multiplayer;
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
                    _screen = Screen.Options;
                    _status = "Options opened.";
                }
                else if (_menuFocus == 6)
                {
                    BeginNewGame();
                }
                else if (_menuFocus == 7)
                {
                    try { Exit(); }
                    catch (PlatformNotSupportedException) { }
                }
            }
        }
        else if (_screen == Screen.Multiplayer)
        {
            HandleMultiplayerController();
        }
        else if (_screen == Screen.PlayerCreation)
        {
            HandlePlayerCreationController();
        }
        else if (_screen == Screen.Store)
        {
            HandleStoreController();
        }
        else if (_screen == Screen.PackOpening)
        {
            HandlePackOpeningController();
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
            HandleDeckBuilderController();
        }
        else
        {
            HandleMatchController();
        }
    }

    private void HandleOptionsController()
    {
        if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical))
        {
            _optionsFocus = Math.Clamp(_optionsFocus + vertical, 0, 6);
        }

        if (DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out var horizontal))
        {
            AdjustFocusedOption(horizontal);
        }

        if (Pressed(Buttons.A))
        {
            _usingController = true;
            if (_optionsFocus is 0 or 4 or 5)
            {
                AdjustFocusedOption(1);
            }
            else if (_optionsFocus == 6)
            {
                _screen = Screen.MainMenu;
                _status = "Options saved.";
            }
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
        }
    }

    private void HandleMultiplayerController()
    {
        if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical))
        {
            _multiplayerFocus = Math.Clamp(_multiplayerFocus + vertical, 0, 3);
        }

        if (!Pressed(Buttons.A))
        {
            return;
        }

        _usingController = true;
        var currentDeck = CurrentDeck();
        var opponentDeck = OpponentDeck();
        if (_multiplayerFocus == 0 && GameDataValidator.ValidateDeck(currentDeck, _data).Count == 0 && ValidateCurrentDeckOwnership(currentDeck).Count == 0)
        {
            StartMatch(currentDeck, opponentDeck, MatchKind.Hotseat);
        }
        else if (_multiplayerFocus == 1)
        {
            BeginHostDirectMatch();
        }
        else if (_multiplayerFocus == 2)
        {
            BeginJoinDirectMatch();
        }
        else if (_multiplayerFocus == 3)
        {
            _screen = Screen.MainMenu;
            _status = "Returned to main menu.";
        }
    }

    private void HandleTutorialsController()
    {
        if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical))
        {
            _tutorialFocus = Math.Clamp(_tutorialFocus + vertical, 0, TutorialDefinitions.Count - 1);
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

        var state = _engine.State;
        var canUseActions = CanHumanUseActions(state);
        var canResolveBlock = CanHumanResolveBlock(state);
        var canResolveTarget = CanHumanResolveTarget(state);
        var canResolveCombatAction = CanHumanResolveCombatAction(state);
        if (!canUseActions && !canResolveBlock && !canResolveTarget && !canResolveCombatAction)
        {
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

    private static Rectangle HandCardRect(int index)
    {
        var area = HandAreaRect();
        return new Rectangle(area.X + 54 + index * 106, area.Y + 36, 92, 108);
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
        _viewportScale = Math.Min(backBufferWidth / (float)VirtualWidth, backBufferHeight / (float)VirtualHeight);
        var width = (int)MathF.Round(VirtualWidth * _viewportScale);
        var height = (int)MathF.Round(VirtualHeight * _viewportScale);
        _viewportRectangle = new Rectangle((backBufferWidth - width) / 2, (backBufferHeight - height) / 2, width, height);

        var x = (int)((_mouse.X - _viewportRectangle.X) / _viewportScale);
        var y = (int)((_mouse.Y - _viewportRectangle.Y) / _viewportScale);
        _virtualMouse = new Point(x, y);
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

    private void GoBack()
    {
        if (_screen == Screen.PlayerCreation && _profile is null)
        {
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
            _presentation.Clear();
        }

        if (_screen == Screen.PackOpening)
        {
            _screen = Screen.Store;
            _status = "Returned to store.";
            return;
        }

        _screen = Screen.MainMenu;
        _status = "Returned to main menu.";
        ClearSelections();
    }

    private void StartMatch(DeckDefinition firstDeck, DeckDefinition secondDeck, MatchKind matchKind)
    {
        _tutorial = null;
        _tutorialNotice = "";
        _matchKind = matchKind;
        var opponentDeck = matchKind == MatchKind.VsAi
            ? OpponentDeck()
            : secondDeck;
        _engine = DragonDuelEngine.Create(_data, "dragon-duel", firstDeck, opponentDeck, seed: Environment.TickCount);
        ConfigureMatchStart(firstDeck, opponentDeck, matchKind);

        var flowResult = _engine.AdvanceToNextDecisionPhase();
        _screen = Screen.Match;
        ClearSelections();
        _matchFocus = MatchFocus.Hand;
        QueuePresentation(flowResult.Events);
        _status = _matchKind == MatchKind.VsAi
            ? "Single player started. Your Main Phase."
            : flowResult.Success ? flowResult.Message : "Match started.";
    }

    private void EnsureHostInvite()
    {
        if (string.IsNullOrWhiteSpace(_hostInviteCode))
        {
            GenerateHostInvite();
        }
    }

    private void GenerateHostInvite()
    {
        var deck = CurrentDeck();
        var rules = CurrentRules();
        _hostInvite = new NetworkInvite
        {
            Host = "127.0.0.1",
            Port = 47288,
            ModeId = "dragon-duel",
            ProtocolVersion = InviteCode.ProtocolVersion,
            DeckHash = InviteCode.DeckHash(deck.Cards),
            RulesHash = InviteCode.RulesHash(rules)
        };
        _hostInviteCode = InviteCode.Encode(_hostInvite);
        _joinInviteCode = _hostInviteCode;
        _multiplayerNotice = "Invite code is valid. Direct online transport is ready for a peer on this LAN.";
    }

    private void ValidateJoinInvite()
    {
        EnsureHostInvite();
        if (InviteCode.TryDecode(_joinInviteCode, out var invite, out var error))
        {
            _multiplayerNotice = $"Valid invite for {invite.ModeId} at {invite.Host}:{invite.Port}. Transport is not active yet.";
            _status = "Direct invite validated.";
            return;
        }

        _multiplayerNotice = error;
        _status = "Direct invite is invalid.";
    }

    private void SaveDeck(DeckDefinition deck)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DragonCards", "decks");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{deck.Id}.json");
        File.WriteAllText(path, GameData.ToJson(deck));
        if (_profile is not null)
        {
            _profile.ActiveDeckId = deck.Id;
            SaveProfile();
        }

        _status = $"Saved deck to {path}";
    }

    private void ApplyResult(GameActionResult result)
    {
        _status = result.Message;
        QueuePresentation(result.Events);
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

        _presentation.Enqueue(events);
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

        var result = _ai.RunUntilHumanInput(_engine, AiPlayerIndex, CurrentRules());
        if (result.Decisions.Count > 0)
        {
            _status = result.Decisions[^1].Message;
            foreach (var decision in result.Decisions)
            {
                QueuePresentation(decision.Events);
            }
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
        else if (result.Status == AiTurnStatus.ActionLimitReached)
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

        var payload = handIndex.ToString();
        if (ExecuteCommand("play-card", payload, () => _engine.PlayCardFromHand(handIndex)))
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
        _presentation.Clear();
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
        var previousScreen = _screen;
        var previousEngine = _engine;
        var previousStatus = _status;
        var previousHand = _selectedHandIndex;
        var previousUnit = _selectedUnitIndex;
        var previousSupport = _selectedSupportIndex;
        var previousBlocker = _selectedBlockerIndex;
        var previousChooseFreeEnergy = _chooseFreeEnergy;
        var previousOptionsFocus = _optionsFocus;
        var previousMatchKind = _matchKind;
        var previousVirtualMouse = _virtualMouse;
        var previousCardZoom = _settings.CardZoom;
        var previousProfile = _profile;
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
        _settings.CardZoom = true;

        using var target = new RenderTarget2D(GraphicsDevice, VirtualWidth, VirtualHeight, false, SurfaceFormat.Color, DepthFormat.None);
        CaptureScreen(target, "player-creation.png", () =>
        {
            _profile = null;
            _screen = Screen.PlayerCreation;
            _creationName = "Astra";
            _creationPresetIndex = 2;
            _creationPlaystyleIndex = 1;
            _creationStarterIndex = 0;
            _status = "Capture: player creation.";
        });
        CaptureScreen(target, "main-menu.png", () =>
        {
            PrepareCaptureProfile();
            _screen = Screen.MainMenu;
            _status = "Capture: main menu.";
        });
        CaptureScreen(target, "tutorials-menu.png", () =>
        {
            PrepareCaptureProfile();
            _screen = Screen.Tutorials;
            _tutorialFocus = 0;
            _tutorialNotice = "Tutorials reward 250 Coins the first time you complete each lesson.";
            _status = "Capture: tutorials menu.";
        });
        CaptureScreen(target, "tutorial-first-turn-basics.png", () => PrepareCaptureTutorial("first-turn-basics"));
        CaptureScreen(target, "tutorial-playing-cards.png", () => PrepareCaptureTutorial("playing-cards"));
        CaptureScreen(target, "tutorial-add-energy.png", () => PrepareCaptureTutorial("add-energy"));
        CaptureScreen(target, "tutorial-sacrifice-energy.png", () => PrepareCaptureTutorial("sacrifice-energy"));
        CaptureScreen(target, "tutorial-blocking-attacks.png", () => PrepareCaptureTutorial("blocking-attacks"));
        CaptureScreen(target, "tutorial-card-effects.png", () => PrepareCaptureTutorial("card-effects"));
        CaptureScreen(target, "multiplayer.png", () =>
        {
            _screen = Screen.Multiplayer;
            EnsureHostInvite();
            _multiplayerFocus = 1;
            _status = "Capture: multiplayer.";
        });
        CaptureScreen(target, "deck-builder.png", () =>
        {
            _screen = Screen.DeckBuilder;
            _deckBuilder.ElementFilter = "Fire";
            _deckBuilder.TypeFilter = "All";
            _deckBuilder.Page = 0;
            _deckFocusIndex = 4;
            _deckBuilder.SelectedCardId = "fire-ashen-champion";
            _status = "Capture: deck builder.";
        });
        CaptureScreen(target, "store.png", () =>
        {
            PrepareCaptureProfile();
            _screen = Screen.Store;
            _status = "Capture: store.";
        });
        CaptureScreen(target, "pack-opening.png", () =>
        {
            PrepareCaptureProfile();
            _lastBoosterOpening = BoosterService.OpenBooster(_data, _profile!, seed: 19, consumeUnopened: false);
            _screen = Screen.PackOpening;
            _status = "Capture: pack opening.";
        });
        CaptureScreen(target, "options.png", () =>
        {
            _screen = Screen.Options;
            _optionsFocus = 0;
            _status = "Capture: options.";
        });
        CaptureScreen(target, "match.png", () =>
        {
            PrepareCaptureMatch();
            _screen = Screen.Match;
            _status = "Capture: match board.";
        });
        CaptureScreen(target, "single-player-match.png", () =>
        {
            PrepareCaptureSinglePlayerMatch();
            _screen = Screen.Match;
            _status = "Capture: single-player AI match.";
        });
        CaptureScreen(target, "hover-zoom.png", () =>
        {
            PrepareCaptureSinglePlayerMatch();
            _screen = Screen.Match;
            _virtualMouse = HandCardRect(0).Center;
            _status = "Capture: card hover zoom.";
        });
        CaptureScreen(target, "block-choice.png", () =>
        {
            PrepareCaptureBlockChoice();
            _screen = Screen.Match;
        });
        CaptureScreen(target, "blocking-modal.png", () =>
        {
            PrepareCaptureBlockChoice();
            _screen = Screen.Match;
        });
        CaptureScreen(target, "blocking-modal-exhausted.png", () =>
        {
            PrepareCaptureBlockChoice(exhaustHumanBlocker: true);
            _screen = Screen.Match;
        });
        CaptureScreen(target, "combat-action-modal.png", PrepareCaptureCombatActionPrompt);
        CaptureScreen(target, "card-effect-modal.png", PrepareCaptureCardEffectPrompt);
        CaptureScreen(target, "sacrifice-tooltip.png", PrepareCaptureSacrificeTooltip);
        CaptureScreen(target, "animation-showcase.png", () =>
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
        CaptureScreen(target, "result-screen.png", () =>
        {
            PrepareCaptureProfile();
            _presentation.Clear();
            _lastMatchWon = true;
            _lastMatchReward = RewardCalculator.PreviewMatchReward(_profile!, CurrentRules(), MatchRewardKind.Ai, won: true);
            _lastBattleSpoils = BattleSpoilsService.GrantVictorySpoils(_data, _profile!, CurrentRules(), _data.DecksById["starter-ice"], won: true, seed: 7);
            _screen = Screen.MatchResult;
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
        _matchKind = previousMatchKind;
        _virtualMouse = previousVirtualMouse;
        _settings.CardZoom = previousCardZoom;
        _profile = previousProfile;
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
        _presentation.Clear();
        GraphicsDevice.SetRenderTarget(null);
        _status = $"Captured screens to {_captureDirectory}.";
    }

    private void CaptureScreen(RenderTarget2D target, string fileName, Action prepare)
    {
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

    private void PrepareCaptureMatch()
    {
        _tutorial = null;
        _tutorialNotice = "";
        _matchKind = MatchKind.Hotseat;
        _engine = DragonDuelEngine.Create(_data, "dragon-duel", _data.DecksById["starter-fire"], _data.DecksById["starter-ice"], seed: 3, shuffle: false);
        _engine.AdvanceToNextDecisionPhase();
        var active = _engine.State.ActivePlayer;
        var defender = _engine.State.DefendingPlayer;
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
        _presentation.Clear();
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
        _presentation.Clear();
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

    private static string CardFrameRulesText(CardDefinition card) =>
        CardDetailFormatter.RulesText(card);

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
        PlayerCreation,
        MainMenu,
        Multiplayer,
        Tutorials,
        Options,
        DeckBuilder,
        Store,
        PackOpening,
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

    private sealed class PresentationQueue
    {
        private readonly Queue<PresentationBeat> _pending = [];

        public PresentationBeat? Active { get; private set; }

        public bool IsBlocking => Active is { Blocking: true, IsComplete: false };

        public void Enqueue(IEnumerable<MatchEvent> events)
        {
            foreach (var matchEvent in events)
            {
                if (ShouldShow(matchEvent))
                {
                    _pending.Enqueue(new PresentationBeat(matchEvent, DurationFor(matchEvent), BlockingFor(matchEvent)));
                }
            }

            Active ??= _pending.Count > 0 ? _pending.Dequeue() : null;
        }

        public void Update(float elapsedSeconds)
        {
            if (Active is null)
            {
                Active = _pending.Count > 0 ? _pending.Dequeue() : null;
                return;
            }

            Active.Elapsed += Math.Max(0f, elapsedSeconds);
            if (Active.IsComplete)
            {
                Active = _pending.Count > 0 ? _pending.Dequeue() : null;
            }
        }

        public void SkipActive()
        {
            if (Active is not null && Active.Elapsed >= 0.2f)
            {
                Active.Elapsed = Active.Duration;
            }
        }

        public void Clear()
        {
            _pending.Clear();
            Active = null;
        }

        public void PrimeForCapture(MatchEvent matchEvent)
        {
            Clear();
            Active = new PresentationBeat(matchEvent, DurationFor(matchEvent), BlockingFor(matchEvent))
            {
                Elapsed = DurationFor(matchEvent) * 0.55f
            };
        }

        private static bool ShouldShow(MatchEvent matchEvent) =>
            matchEvent.Kind is not MatchEventKind.EnergySpent;

        private static bool BlockingFor(MatchEvent matchEvent) =>
            matchEvent.Kind is MatchEventKind.CardDrawn or
                MatchEventKind.CardPlayed or
                MatchEventKind.CardSacrificed or
                MatchEventKind.TargetChoiceQueued or
                MatchEventKind.AttackDeclared or
                MatchEventKind.BlockDeclared or
                MatchEventKind.CombatActionQueued or
                MatchEventKind.DamageTaken or
                MatchEventKind.PhaseChanged;

        private static float DurationFor(MatchEvent matchEvent) => matchEvent.Kind switch
        {
            MatchEventKind.PhaseChanged => 0.55f,
            MatchEventKind.CardDrawn => 0.38f,
            MatchEventKind.CardPlayed => 0.58f,
            MatchEventKind.CardSacrificed => 0.48f,
            MatchEventKind.TargetChoiceQueued => 0.42f,
            MatchEventKind.AttackDeclared or MatchEventKind.BlockDeclared or MatchEventKind.CombatActionQueued or MatchEventKind.CombatActionPassed or MatchEventKind.CombatResolved => 0.44f,
            MatchEventKind.DamageTaken => 0.5f,
            _ => 0.32f
        };
    }

    private sealed class PresentationBeat
    {
        public PresentationBeat(MatchEvent matchEvent, float duration, bool blocking)
        {
            Event = matchEvent;
            Duration = duration;
            Blocking = blocking;
        }

        public MatchEvent Event { get; }
        public float Duration { get; }
        public bool Blocking { get; }
        public float Elapsed { get; set; }
        public float Progress => Duration <= 0f ? 1f : Math.Clamp(Elapsed / Duration, 0f, 1f);
        public bool IsComplete => Progress >= 1f;
    }

    private sealed class GameSettings
    {
        public bool Fullscreen { get; set; }
        public int WindowWidth { get; set; } = WindowedWidth;
        public int WindowHeight { get; set; } = WindowedHeight;
        public int MusicVolume { get; set; } = 70;
        public int SoundVolume { get; set; } = 80;
        public bool MuteAudio { get; set; }
        public bool CardZoom { get; set; } = true;

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
                        CardZoom = ReadBoolean(root, nameof(CardZoom), defaultValue: true)
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
        }
    }

    private sealed class DeckBuilderState
    {
        public const int PageSize = 12;
        private readonly GameData _data;
        private readonly Dictionary<string, int> _cards;

        public DeckBuilderState(GameData data, DeckDefinition startingDeck)
        {
            _data = data;
            _cards = startingDeck.Cards.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
            DeckName = "Custom Flame Gale";
            ElementFilters = ["All", .. data.GameModesById["dragon-duel"].Elements];
            TypeFilters = ["All", .. data.GameModesById["dragon-duel"].AllowedCardTypes];
            SelectedCardId = data.Cards.FirstOrDefault()?.Id;
        }

        public string DeckName { get; }
        public string ElementFilter { get; set; } = "All";
        public string TypeFilter { get; set; } = "All";
        public int Page { get; set; }
        public string? SelectedCardId { get; set; }
        public IReadOnlyList<string> ElementFilters { get; }
        public IReadOnlyList<string> TypeFilters { get; }

        public IReadOnlyList<CardDefinition> FilteredCards => _data.Cards
            .Where(card => ElementFilter == "All" || card.Elements.Contains(ElementFilter, StringComparer.OrdinalIgnoreCase))
            .Where(card => TypeFilter == "All" || card.Type.Equals(TypeFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(card => card.Elements.FirstOrDefault())
            .ThenBy(card => card.TotalCost)
            .ThenBy(card => card.Name)
            .ToArray();

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

        public DeckDefinition CreateDeck() => new()
        {
            Id = "custom-flame-gale",
            Name = DeckName,
            ModeId = "dragon-duel",
            Cards = _cards
                .Where(entry => entry.Value > 0)
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase)
        };
    }
}
