using DragonCards.Core;
using DragonCards.Networking;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace DragonCards.Desktop;

public sealed partial class DragonCardsGame
{
    private enum MultiplayerSection
    {
        Local,
        HostLobby,
        JoinLobby
    }

    private enum DirectLobbyState
    {
        Idle,
        Hosting,
        Joining,
        Connected,
        Starting,
        Failed
    }

    private MultiplayerSection _multiplayerSection;
    private DirectLobbyState _directLobbyState;
    private bool _joinInviteEditing;
    private int _hostLobbyPort = 47288;

    private bool IsDirectLobbyActive =>
        _networkDiscoveryTask is not null ||
        _networkConnectTask is not null ||
        _networkStartTask is not null ||
        _directLobbyState is DirectLobbyState.Hosting or DirectLobbyState.Joining or DirectLobbyState.Connected or DirectLobbyState.Starting;

    private bool IsJoinInviteTextActive =>
        _screen == Screen.Multiplayer &&
        _multiplayerSection == MultiplayerSection.JoinLobby &&
        _joinInviteEditing &&
        _directLobbyState is DirectLobbyState.Idle or DirectLobbyState.Failed;

    private void DrawMultiplayerLobbyUi()
    {
        EnsureHostInvite();
        var selectedMode = PlayableModeCatalog.All[Math.Clamp(_modeFocus, 0, PlayableModeCatalog.All.Count - 1)];
        var canStart = selectedMode.StartsMatch &&
            selectedMode.Id != DragonCardsModeIds.TutorialTrials &&
            CanStartSelectedMode(selectedMode) &&
            _dataIssues.Count == 0;

        DrawText("Multiplayer", new Vector2(54, 108), Color.White, 1.2f);
        DrawText("Play locally on one device, or host a direct two-player lobby for a player on the same LAN.",
            new Rectangle(56, 146, 1140, 36), UiTheme.TextMuted, 0.64f);

        DrawMultiplayerSectionTabs();
        var panel = new Rectangle(54, 246, 1490, 512);
        DrawPanel(panel, UiTheme.PanelRaised, border: UiTheme.Border);
        switch (_multiplayerSection)
        {
            case MultiplayerSection.Local:
                DrawLocalMultiplayerPanel(panel, selectedMode, canStart);
                break;
            case MultiplayerSection.HostLobby:
                DrawHostLobbyPanel(panel, selectedMode, canStart);
                break;
            case MultiplayerSection.JoinLobby:
                DrawJoinLobbyPanel(panel);
                break;
        }

        if (Button(new Rectangle(54, 786, 150, 42), IsDirectLobbyActive ? "Cancel Lobby" : "Back",
                focused: _usingController && _multiplayerFocus == MultiplayerActionCount() - 1))
        {
            if (IsDirectLobbyActive)
            {
                CancelDirectLobby();
            }
            else
            {
                _screen = UxBackDestination(Screen.MainMenu);
                _status = "Returned.";
            }
        }
    }

    private void DrawMultiplayerSectionTabs()
    {
        if (Button(new Rectangle(54, 190, 176, 40), "Local", selected: _multiplayerSection == MultiplayerSection.Local))
        {
            SelectMultiplayerSection(MultiplayerSection.Local);
        }

        if (Button(new Rectangle(242, 190, 176, 40), "Host Lobby", selected: _multiplayerSection == MultiplayerSection.HostLobby))
        {
            SelectMultiplayerSection(MultiplayerSection.HostLobby);
        }

        if (Button(new Rectangle(430, 190, 176, 40), "Join Lobby", selected: _multiplayerSection == MultiplayerSection.JoinLobby))
        {
            SelectMultiplayerSection(MultiplayerSection.JoinLobby);
        }
    }

    private void DrawLocalMultiplayerPanel(Rectangle panel, PlayableModeDefinition selectedMode, bool canStart)
    {
        DrawText("Local Hotseat", new Vector2(panel.X + 30, panel.Y + 28), Color.White, 0.92f);
        DrawText("Two players share this device and take turns on the same match board.",
            new Rectangle(panel.X + 30, panel.Y + 68, 620, 42), UiTheme.TextMuted, 0.58f);
        DrawModeSelection(panel, selectedMode, allowChange: true);

        if (Button(new Rectangle(panel.X + 30, panel.Bottom - 98, 260, 52), "Start Local Hotseat", canStart,
                focused: _usingController && _multiplayerFocus == 0))
        {
            StartSelectedMode(selectedMode, MatchKind.Hotseat);
        }

        DrawPanel(new Rectangle(panel.X + 804, panel.Y + 44, 624, 332), UiTheme.PanelInset, border: UiTheme.Border);
        DrawText("Same Screen, Same Rules", new Vector2(panel.X + 836, panel.Y + 76), UiTheme.DragonGold, 0.74f);
        DrawText("Use Local Hotseat when both players are together. The selected mode, deck rules, and progression eligibility are the same as a normal local match.",
            new Rectangle(panel.X + 836, panel.Y + 120, 554, 128), UiTheme.TextMuted, 0.6f);
        DrawText("Keyboard/controller: Page Up/Down selects mode; Enter/A starts.",
            new Rectangle(panel.X + 836, panel.Y + 286, 554, 32), UiTheme.TextMuted, 0.5f);
    }

    private void DrawHostLobbyPanel(Rectangle panel, PlayableModeDefinition selectedMode, bool canStart)
    {
        DrawText("Host a Direct Lobby", new Vector2(panel.X + 30, panel.Y + 28), Color.White, 0.92f);
        DrawText("One guest can join this client-hosted LAN lobby with a five-character code. The host starts after the roster is complete.",
            new Rectangle(panel.X + 30, panel.Y + 68, 720, 42), UiTheme.TextMuted, 0.58f);
        DrawModeSelection(panel, selectedMode, allowChange: !IsDirectLobbyActive);

        var codePanel = new Rectangle(panel.X + 30, panel.Y + 194, 714, 212);
        DrawPanel(codePanel, UiTheme.PanelInset, border: UiTheme.BorderStrong);
        DrawText("Share this LAN code", new Vector2(codePanel.X + 24, codePanel.Y + 18), UiTheme.DragonGold, 0.68f);
        DrawFittedCenteredText(_hostInviteCode, new Rectangle(codePanel.X + 24, codePanel.Y + 50, codePanel.Width - 48, 42), Color.White, 1.18f, 0.58f);
        var copyFocus = _directLobbyState is DirectLobbyState.Idle or DirectLobbyState.Failed or DirectLobbyState.Connected ? 1 : 0;
        if (Button(new Rectangle(codePanel.Right - 170, codePanel.Y + 102, 146, 36), "Copy Code",
                focused: _usingController && _multiplayerFocus == copyFocus))
        {
            CopyHostInviteCode();
        }

        DrawText($"LAN host: {_hostInvite.Host}:{_hostInvite.Port}", new Vector2(codePanel.X + 24, codePanel.Y + 112), UiTheme.TextMuted, 0.54f);
        DrawText(_hostInvite.Host == "127.0.0.1"
                ? "No LAN IPv4 address was detected; this invite is available only on this device."
                : $"Guests enter the code on the same LAN. Allow Private-network UDP {LanLobbyDiscovery.Port} and TCP {_hostInvite.Port} if Windows asks.",
            new Rectangle(codePanel.X + 24, codePanel.Y + 158, codePanel.Width - 48, 36), UiTheme.TextMuted, 0.45f);

        DrawLobbyRoster(new Rectangle(panel.X + 780, panel.Y + 120, 646, 286));
        if (_directLobbyState is DirectLobbyState.Idle or DirectLobbyState.Failed)
        {
            if (Button(new Rectangle(panel.X + 30, panel.Bottom - 98, 220, 52), "Host Lobby", canStart,
                    focused: _usingController && _multiplayerFocus == 0))
            {
                HostSelectedMode(selectedMode);
            }

            if (Button(new Rectangle(panel.X + 438, panel.Bottom - 98, 192, 52), "New Code", canStart,
                    focused: _usingController && _multiplayerFocus == 2))
            {
                GenerateHostInviteForSelectedMode();
                _multiplayerNotice = "Created a new lobby invite.";
            }
        }
        else if (_directLobbyState == DirectLobbyState.Connected && _networkConnection?.IsHost == true)
        {
            if (Button(new Rectangle(panel.X + 30, panel.Bottom - 98, 220, 52), "Start Match",
                    focused: _usingController && _multiplayerFocus == 0))
            {
                StartHostedLobbyMatch();
            }

            if (Button(new Rectangle(panel.X + 438, panel.Bottom - 98, 192, 52), "Cancel Lobby",
                    focused: _usingController && _multiplayerFocus == 2))
            {
                CancelDirectLobby();
            }
        }
        else
        {
            DrawText(DirectLobbyStatusLabel(), new Rectangle(panel.X + 30, panel.Bottom - 100, 620, 38), UiTheme.TextMuted, 0.62f);
        }
    }

    private void DrawJoinLobbyPanel(Rectangle panel)
    {
        DrawText("Join a Direct Lobby", new Vector2(panel.X + 30, panel.Y + 28), Color.White, 0.92f);
        DrawText("Enter or paste the five-character code from the host. The game finds that lobby on your local network; no matchmaking service is involved.",
            new Rectangle(panel.X + 30, panel.Y + 68, 860, 42), UiTheme.TextMuted, 0.58f);

        var inviteRect = new Rectangle(panel.X + 30, panel.Y + 136, 880, 64);
        var inviteHitTarget = UiTheme.MinimumHitTarget(inviteRect);
        if ((_directLobbyState is DirectLobbyState.Idle or DirectLobbyState.Failed) && Hit(inviteHitTarget))
        {
            _joinInviteEditing = true;
            _multiplayerFocus = 0;
        }

        Fill(inviteRect, _joinInviteEditing ? UiTheme.PanelRaised : UiTheme.PanelInset);
        Border(inviteRect, _joinInviteEditing ? UiTheme.Focus : UiTheme.BorderStrong, _joinInviteEditing ? 2 : 1);
        DrawText(string.IsNullOrWhiteSpace(_joinInviteCode) ? "Click here, then type the 5-character LAN code" : _joinInviteCode,
            new Rectangle(inviteRect.X + 18, inviteRect.Y + 20, inviteRect.Width - 36, 28),
            string.IsNullOrWhiteSpace(_joinInviteCode) ? UiTheme.TextMuted : Color.White, 0.62f);

        if (InviteCode.TryDecodeLobbyCode(_joinInviteCode, out var lobbyToken, out var lobbyError))
        {
            DrawText($"Valid LAN code {InviteCode.EncodeLobbyCode(lobbyToken)}. Connect searches for the host on this network.",
                new Rectangle(panel.X + 30, panel.Y + 218, 880, 32), UiTheme.Success, 0.54f);
        }
        else if (InviteCode.TryDecode(_joinInviteCode, out var invite, out var error))
        {
            DrawText($"Valid legacy {InviteLabel(_joinInviteCode)} invite: {ModeName(invite.ModeId)} at {invite.Host}:{invite.Port}",
                new Rectangle(panel.X + 30, panel.Y + 218, 880, 32), UiTheme.Success, 0.54f);
        }
        else if (!string.IsNullOrWhiteSpace(_joinInviteCode))
        {
            var normalizedLobbyCode = new string(_joinInviteCode.Where(character => !char.IsWhiteSpace(character) && character != '-').ToArray());
            DrawText(normalizedLobbyCode.Length == InviteCode.LobbyCodeLength ? lobbyError : error,
                new Rectangle(panel.X + 30, panel.Y + 218, 880, 32), UiTheme.Danger, 0.52f);
        }

        if (_directLobbyState is DirectLobbyState.Idle or DirectLobbyState.Failed)
        {
            if (Button(new Rectangle(panel.X + 30, panel.Y + 282, 190, 52), "Connect",
                    focused: _usingController && _multiplayerFocus == 1))
            {
                BeginJoinDirectMatch();
            }

            if (Button(new Rectangle(panel.X + 238, panel.Y + 282, 150, 52), "Paste Code",
                    focused: _usingController && _multiplayerFocus == 2))
            {
                PasteJoinInviteCode();
            }

            if (Button(new Rectangle(panel.X + 406, panel.Y + 282, 150, 52), "Clear",
                    focused: _usingController && _multiplayerFocus == 3))
            {
                _joinInviteCode = "";
                _joinInviteEditing = true;
                _multiplayerNotice = "Invite entry cleared.";
            }
        }

        DrawLobbyRoster(new Rectangle(panel.X + 956, panel.Y + 112, 470, 294));
        DrawText(DirectLobbyStatusLabel(), new Rectangle(panel.X + 30, panel.Bottom - 110, 1240, 40),
            _directLobbyState == DirectLobbyState.Failed ? UiTheme.Danger : UiTheme.TextMuted, 0.62f);
        DrawText("Paste: Ctrl+V or Paste Code. Keyboard/controller: left/right changes view, Tab changes action, Enter/A activates. Codes ignore letter case.",
            new Rectangle(panel.X + 30, panel.Bottom - 60, 1240, 28), UiTheme.TextMuted, 0.48f);
    }

    private void DrawModeSelection(Rectangle panel, PlayableModeDefinition selectedMode, bool allowChange)
    {
        DrawText($"Mode: {selectedMode.Name}", new Vector2(panel.X + 30, panel.Y + 122),
            selectedMode.ProgressionEligible ? UiTheme.Success : UiTheme.DragonGold, 0.68f);
        DrawText(selectedMode.Description, new Rectangle(panel.X + 30, panel.Y + 154, 660, 38), UiTheme.TextMuted, 0.5f);
        if (Button(new Rectangle(panel.X + 610, panel.Y + 114, 72, 34), "Prev", allowChange))
        {
            CycleMultiplayerMode(-1);
        }

        if (Button(new Rectangle(panel.X + 692, panel.Y + 114, 72, 34), "Next", allowChange))
        {
            CycleMultiplayerMode(1);
        }
    }

    private void DrawLobbyRoster(Rectangle rect)
    {
        DrawPanel(rect, UiTheme.PanelInset, border: UiTheme.Border);
        DrawText("Lobby Roster", new Vector2(rect.X + 24, rect.Y + 22), Color.White, 0.7f);
        var hostName = _networkConnection?.Lobby.Host.PlayerName ?? (_profile?.PlayerName ?? "Host");
        var joinerName = _networkConnection?.Lobby.Joiner.PlayerName;
        DrawLobbyPlayerRow(new Rectangle(rect.X + 24, rect.Y + 66, rect.Width - 48, 64), "Host", hostName, "Ready", UiTheme.Success);
        DrawLobbyPlayerRow(new Rectangle(rect.X + 24, rect.Y + 144, rect.Width - 48, 64), "Guest",
            string.IsNullOrWhiteSpace(joinerName) ? "Waiting for invite connection" : joinerName,
            string.IsNullOrWhiteSpace(joinerName) ? "Waiting" : _directLobbyState == DirectLobbyState.Starting ? "Starting" : "Connected",
            string.IsNullOrWhiteSpace(joinerName) ? UiTheme.TextMuted : UiTheme.Success);
        DrawText(_multiplayerNotice, new Rectangle(rect.X + 24, rect.Bottom - 64, rect.Width - 48, 42),
            _directLobbyState == DirectLobbyState.Failed ? UiTheme.Danger : UiTheme.TextMuted, 0.48f);
    }

    private void DrawLobbyPlayerRow(Rectangle rect, string role, string name, string state, Color stateColor)
    {
        Fill(rect, UiTheme.PanelRaised);
        Border(rect, UiTheme.Border, 1);
        DrawText(role, new Vector2(rect.X + 14, rect.Y + 12), UiTheme.DragonGold, 0.48f);
        DrawText(name, new Rectangle(rect.X + 90, rect.Y + 10, rect.Width - 210, 28), Color.White, 0.58f);
        DrawText(state, new Rectangle(rect.Right - 108, rect.Y + 18, 92, 22), stateColor, 0.46f);
    }

    private void HandleMultiplayerLobbyInput()
    {
        if (FocusPressed(out var focusDelta))
        {
            _multiplayerFocus = Math.Clamp(_multiplayerFocus + focusDelta, 0, MultiplayerActionCount() - 1);
        }
        if (_uiActions.Triggered(UiAction.MoveToStart)) _multiplayerFocus = 0;
        else if (_uiActions.Triggered(UiAction.MoveToEnd)) _multiplayerFocus = MultiplayerActionCount() - 1;

        if (DirectionPressed(Buttons.DPadLeft, Buttons.DPadRight, out var sectionDelta))
        {
            SelectMultiplayerSection((MultiplayerSection)(((int)_multiplayerSection + sectionDelta + 3) % 3));
            return;
        }

        if (DirectionPressed(Buttons.DPadUp, Buttons.DPadDown, out var vertical))
        {
            _multiplayerFocus = Math.Clamp(_multiplayerFocus + vertical, 0, MultiplayerActionCount() - 1);
        }

        if ((_multiplayerSection is MultiplayerSection.Local or MultiplayerSection.HostLobby) && !IsDirectLobbyActive)
        {
            if (_uiActions.Triggered(UiAction.PagePrevious)) CycleMultiplayerMode(-1);
            else if (_uiActions.Triggered(UiAction.PageNext)) CycleMultiplayerMode(1);
        }

        if (!Pressed(Buttons.A))
        {
            return;
        }

        _usingController = true;
        var selectedMode = PlayableModeCatalog.All[Math.Clamp(_modeFocus, 0, PlayableModeCatalog.All.Count - 1)];
        var canStart = selectedMode.StartsMatch && selectedMode.Id != DragonCardsModeIds.TutorialTrials && CanStartSelectedMode(selectedMode);
        switch (_multiplayerSection)
        {
            case MultiplayerSection.Local when _multiplayerFocus == 0 && canStart:
                StartSelectedMode(selectedMode, MatchKind.Hotseat);
                break;
            case MultiplayerSection.Local:
                NavigateAwayFromMultiplayer();
                break;
            case MultiplayerSection.HostLobby when _directLobbyState is DirectLobbyState.Idle or DirectLobbyState.Failed:
                if (_multiplayerFocus == 0 && canStart) HostSelectedMode(selectedMode);
                else if (_multiplayerFocus == 1) CopyHostInviteCode();
                else if (_multiplayerFocus == 2) GenerateHostInviteForSelectedMode();
                else NavigateAwayFromMultiplayer();
                break;
            case MultiplayerSection.HostLobby when _directLobbyState == DirectLobbyState.Connected && _networkConnection?.IsHost == true:
                if (_multiplayerFocus == 0) StartHostedLobbyMatch();
                else if (_multiplayerFocus == 1) CopyHostInviteCode();
                else if (_multiplayerFocus == 2) CancelDirectLobby();
                else NavigateAwayFromMultiplayer();
                break;
            case MultiplayerSection.HostLobby:
                if (_multiplayerFocus == 0) CopyHostInviteCode();
                else NavigateAwayFromMultiplayer();
                break;
            case MultiplayerSection.JoinLobby when _directLobbyState is DirectLobbyState.Idle or DirectLobbyState.Failed:
                if (_multiplayerFocus == 0)
                {
                    _joinInviteEditing = true;
                    _status = "Type the host's five-character LAN code.";
                }
                else if (_multiplayerFocus == 1) BeginJoinDirectMatch();
                else if (_multiplayerFocus == 2) PasteJoinInviteCode();
                else if (_multiplayerFocus == 3)
                {
                    _joinInviteCode = "";
                    _joinInviteEditing = true;
                }
                else NavigateAwayFromMultiplayer();
                break;
            case MultiplayerSection.JoinLobby:
                if (_multiplayerFocus == 0) CancelDirectLobby();
                else NavigateAwayFromMultiplayer();
                break;
        }
    }

    private void SelectMultiplayerSection(MultiplayerSection section)
    {
        if (section == _multiplayerSection)
        {
            return;
        }

        if (IsDirectLobbyActive)
        {
            _status = "Cancel the active lobby before switching multiplayer views.";
            return;
        }

        _multiplayerSection = section;
        _multiplayerFocus = 0;
        _joinInviteEditing = false;
        if (section == MultiplayerSection.HostLobby)
        {
            EnsureHostInvite();
        }
    }

    private void CycleMultiplayerMode(int delta)
    {
        var modes = PlayableModeCatalog.All;
        _modeFocus = (_modeFocus + delta + modes.Count) % modes.Count;
        GenerateHostInviteForSelectedMode();
    }

    private int MultiplayerActionCount() => _multiplayerSection switch
    {
        MultiplayerSection.Local => 2,
        MultiplayerSection.HostLobby when _directLobbyState is DirectLobbyState.Idle or DirectLobbyState.Failed => 4,
        MultiplayerSection.HostLobby when _directLobbyState == DirectLobbyState.Connected && _networkConnection?.IsHost == true => 4,
        MultiplayerSection.HostLobby => 2,
        MultiplayerSection.JoinLobby when _directLobbyState is DirectLobbyState.Idle or DirectLobbyState.Failed => 5,
        _ => 2
    };

    private void NavigateAwayFromMultiplayer()
    {
        if (IsDirectLobbyActive)
        {
            CancelDirectLobby();
            return;
        }

        _screen = UxBackDestination(Screen.MainMenu);
        _status = "Returned.";
    }

    private void CopyHostInviteCode()
    {
        if (DesktopClipboard.TrySetText(_hostInviteCode, out var error))
        {
            _multiplayerNotice = "Lobby code copied. Share it with a player on your local network.";
            _status = "Invite code copied to the clipboard.";
        }
        else
        {
            _multiplayerNotice = error;
            _status = error;
        }
    }

    private void PasteJoinInviteCode()
    {
        if (!DesktopClipboard.TryGetText(out var code, out var error))
        {
            _multiplayerNotice = error;
            _status = error;
            return;
        }

        _joinInviteCode = code.Length <= 900 ? code : code[..900];
        _joinInviteEditing = true;
        _multiplayerNotice = "Invite code pasted. Review it, then connect.";
        _status = "Invite code pasted from the clipboard.";
    }

    private static string InviteLabel(string code) => code.StartsWith(InviteCode.CompactPrefix, StringComparison.OrdinalIgnoreCase) ? "DC2" : "DC1";

    private string DirectLobbyStatusLabel() => _directLobbyState switch
    {
        DirectLobbyState.Hosting => "Lobby open. Waiting for one guest. Allow Dragon Cards on Private networks if Windows asks.",
        DirectLobbyState.Joining => "Searching the LAN, then connecting to the host and validating rules.",
        DirectLobbyState.Connected when _networkConnection?.IsHost == true => "Guest connected. Start the match when both players are ready.",
        DirectLobbyState.Connected => "Connected to the lobby. Waiting for the host to start.",
        DirectLobbyState.Starting => "Host is starting the synchronized match.",
        DirectLobbyState.Failed => _multiplayerNotice,
        _ => "Create a lobby or enter a five-character LAN code to connect."
    };
}
