using dc.pr;
using dc.ui;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using HaxeProxy.Runtime;
using ModCore.Menu;
using ModCore.Modules;
using ModCore.Utilities;

namespace DeadCellsMultiplayerMod
{
    internal static partial class GameMenu
    {
        private static IModMenu? _dccmRootMenu;
        private static IModMenu? _dccmActiveMenu;
        private static string _dccmErrorTitle = string.Empty;
        private static string _dccmErrorDetails = string.Empty;
        private static Action? _dccmErrorBack;

        private static readonly IModMenu DccmMultiplayerMenu = new DccmMenu(
            "Multiplayer",
            "Host or join a multiplayer session",
            BuildDccmMultiplayerMenu);

        private static readonly IModMenu DccmHostTransportMenu = new DccmMenu(
            "Host game",
            "Choose how players connect to you",
            BuildDccmHostTransportMenu);

        private static readonly IModMenu DccmJoinTransportMenu = new DccmMenu(
            "Join game",
            "Choose how to connect to a host",
            BuildDccmJoinTransportMenu);

        private static readonly IModMenu DccmHostLanMenu = new DccmMenu(
            "LAN host setup",
            "Configure local network hosting",
            options => BuildDccmConnectionMenu(options, NetRole.Host));

        private static readonly IModMenu DccmJoinLanMenu = new DccmMenu(
            "LAN join setup",
            "Configure local network join",
            options => BuildDccmConnectionMenu(options, NetRole.Client));

        private static readonly IModMenu DccmHostStatusMenu = new DccmMenu(
            "Host lobby",
            "Start the run when players are ready",
            BuildDccmHostStatusMenu);

        private static readonly IModMenu DccmClientWaitingMenu = new DccmMenu(
            "Client lobby",
            "Waiting for host data",
            BuildDccmClientWaitingMenu);

        private static readonly IModMenu DccmLobbyNotFoundMenu = new DccmMenu(
            "Can't find lobby",
            "Connection failed",
            BuildDccmLobbyNotFoundMenu);

        private static readonly IModMenu DccmErrorMenu = new DccmMenu(
            "Connection error",
            null,
            BuildDccmErrorMenu);

        internal static void OpenDccmMultiplayerMenu(IModMenu rootMenu)
        {
            _dccmRootMenu = rootMenu;
            OpenDccmMenu(DccmMultiplayerMenu);
        }

        private static void OpenDccmMenu(IModMenu menu)
        {
            try
            {
                _dccmActiveMenu = menu;
                MenuModule.Instance.SetSection(menu);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open DCCM menu {Menu}: {Message}", menu.GetName(), ex.Message);
            }
        }

        private static void OpenDccmMenuFromTitle(TitleScreen screen, IModMenu menu)
        {
            try
            {
                if (screen == null || screen.destroyed)
                    return;

                screen.showOptions(MenuModule.Instance.DCCMMainMenu);
                OpenDccmMenu(menu);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to reopen DCCM menu {Menu}: {Message}", menu.GetName(), ex.Message);
            }
        }

        private static void OpenDccmRootMenu()
        {
            if (_dccmRootMenu != null)
            {
                OpenDccmMenu(_dccmRootMenu);
                return;
            }

            var screen = GetTitleScreen();
            if (screen != null && !screen.destroyed)
                screen.mainMenu();
        }

        private static dc.h2d.Flow? BeginDccmMenu(Options options, string title)
        {
            if (options == null || options.destroyed)
                return null;

            options.title?.set_text(Localize(title).AsHaxeString());
            options.createScroller(0.0);
            return options.scrollerFlow;
        }

        private static void AddDccmButton(
            Options options,
            dc.h2d.Flow flow,
            string label,
            string? help,
            Action action,
            bool localizeLabel = true)
        {
            int leftPadding = 5;
            options.addSimpleWidget(
                (localizeLabel ? Localize(label) : label).AsHaxeString(),
                string.IsNullOrWhiteSpace(help) ? null : Localize(help).AsHaxeString(),
                new HlAction(action),
                Ref<int>.From(ref leftPadding),
                flow);
        }

        private static void AddDccmInfo(Options options, dc.h2d.Flow flow, string label, string? value = null)
        {
            int leftPadding = 0;
            options.addSimpleWidget(
                Localize(label).AsHaxeString(),
                string.IsNullOrWhiteSpace(value) ? null : value.AsHaxeString(),
                new HlAction(() => { }),
                Ref<int>.From(ref leftPadding),
                flow);
        }

        private static void AddDccmBackToRoot(Options options, dc.h2d.Flow flow)
        {
            AddDccmButton(options, flow, "Back", null, OpenDccmRootMenu);
        }

        private static void BuildDccmMultiplayerMenu(Options options)
        {
            var flow = BeginDccmMenu(options, "Multiplayer");
            if (flow == null)
                return;

            AddDccmButton(options, flow, "Host game", "Create a multiplayer lobby", () => OpenDccmMenu(DccmHostTransportMenu));
            AddDccmButton(options, flow, "Join game", "Join another player's lobby", () => OpenDccmMenu(DccmJoinTransportMenu));
            AddDccmBackToRoot(options, flow);
            options.updateScroller();
        }

        private static void BuildDccmHostTransportMenu(Options options)
        {
            var flow = BeginDccmMenu(options, "Host game");
            if (flow == null)
                return;

            AddDccmButton(options, flow, "LAN", "Host on a local TCP port", () =>
            {
                _menuSelection = NetRole.Host;
                _menuTransport = ConnectionTransport.Lan;
                _waitingForHost = false;
                OpenDccmMenu(DccmHostLanMenu);
            });
            AddDccmButton(options, flow, "Steam", "Host through a Steam lobby", DccmStartSteamHost);
            AddDccmButton(options, flow, "Back", null, () => OpenDccmMenu(DccmMultiplayerMenu));
            options.updateScroller();
        }

        private static void BuildDccmJoinTransportMenu(Options options)
        {
            var flow = BeginDccmMenu(options, "Join game");
            if (flow == null)
                return;

            AddDccmButton(options, flow, "LAN", "Join by IP and port", () =>
            {
                _menuSelection = NetRole.Client;
                _menuTransport = ConnectionTransport.Lan;
                _waitingForHost = true;
                OpenDccmMenu(DccmJoinLanMenu);
            });
            AddDccmButton(options, flow, "Steam", "Join from a Steam lobby code in clipboard", DccmStartSteamJoin);
            AddDccmButton(options, flow, "Back", null, () => OpenDccmMenu(DccmMultiplayerMenu));
            options.updateScroller();
        }

        private static void BuildDccmConnectionMenu(Options options, NetRole role)
        {
            var title = role == NetRole.Host ? "LAN host setup" : "LAN join setup";
            var flow = BeginDccmMenu(options, title);
            if (flow == null)
                return;

            _menuSelection = role;
            _menuTransport = ConnectionTransport.Lan;
            if (role == NetRole.Client)
                _waitingForHost = true;

            AddDccmButton(options, flow, $"{GetText.Instance.GetString("Username: ")}{_username}", "Edit display name", () => DccmEditUsername(role), localizeLabel: false);
            AddDccmButton(options, flow, $"{GetText.Instance.GetString("IP: ")}{_mpIp}", "Edit IP", () => DccmEditIp(role), localizeLabel: false);
            AddDccmButton(options, flow, $"{GetText.Instance.GetString("Port: ")}{_mpPort}", "Edit port", () => DccmEditPort(role), localizeLabel: false);

            AddDccmButton(
                options,
                flow,
                role == NetRole.Host ? "Host" : "Join",
                role == NetRole.Host ? "Start hosting" : "Connect to host",
                () => DccmStartLan(role));

            AddDccmButton(
                options,
                flow,
                "Back",
                null,
                () => OpenDccmMenu(role == NetRole.Host ? DccmHostTransportMenu : DccmJoinTransportMenu));
            options.updateScroller();
        }

        private static void BuildDccmHostStatusMenu(Options options)
        {
            var flow = BeginDccmMenu(options, "Host lobby");
            if (flow == null)
                return;

            AddDccmInfo(options, flow, "Username", _username);
            AddDccmInfo(options, flow, "Connection", _menuTransport == ConnectionTransport.Steam ? "Steam" : $"{_mpIp}:{_mpPort}");

            var lobbyCode = GetSteamLobbyCodeForUi();
            if (!string.IsNullOrWhiteSpace(lobbyCode))
                AddDccmInfo(options, flow, "Lobby code", lobbyCode);

            AddDccmInfo(options, flow, "Remote player", string.IsNullOrWhiteSpace(_remoteUsername) ? Localize("Waiting") : _remoteUsername);
            AddDccmInfo(options, flow, "Ready", string.IsNullOrWhiteSpace(_remoteUsername) ? Localize("Waiting") : Localize("Ready"));

            AddDccmButton(options, flow, "Play", "Launch game", () => DccmStartHostRun(options));
            AddDccmButton(options, flow, GetMultiplayerSaveButtonLabel(), "Choose multiplayer save slot", () => DccmOpenMultiplayerSlotMenu(options), localizeLabel: false);

            if (!string.IsNullOrWhiteSpace(lobbyCode))
                AddDccmButton(options, flow, "Copy lobby id", "Copy lobby code to clipboard", () => TryCopySteamLobbyCodeFromUi());

            AddDccmButton(options, flow, "Back", "Stop hosting and return to multiplayer menu", () =>
            {
                StopNetworkFromMenu();
                SetRole(NetRole.None);
                _menuSelection = NetRole.None;
                OpenDccmMenu(DccmMultiplayerMenu);
                GetTitleScreen()?.ShouldAutoHideConnectionUI(false);
            });
            options.updateScroller();
        }

        private static void BuildDccmClientWaitingMenu(Options options)
        {
            var flow = BeginDccmMenu(options, "Client lobby");
            if (flow == null)
                return;

            AddDccmInfo(options, flow, "Username", _username);
            AddDccmInfo(options, flow, "Connection", _menuTransport == ConnectionTransport.Steam ? "Steam" : $"{_mpIp}:{_mpPort}");

            var status = _clientConnecting
                ? FormatLocalized("Connecting ({0}/{1})", _clientConnectAttempt, ClientConnectMaxAttempts)
                : _waitingForHost
                    ? Localize("Waiting for host")
                    : Localize("Connected");
            AddDccmInfo(options, flow, "Status", status);

            AddDccmButton(
                options,
                flow,
                "Disconnect",
                "Disconnect and return to multiplayer menu",
                () =>
                {
                    StopNetworkFromMenu();
                    _waitingForHost = false;
                    ResetClientConnectState();
                    _menuSelection = NetRole.None;
                    OpenDccmMenu(DccmMultiplayerMenu);
                    GetTitleScreen()?.ShouldAutoHideConnectionUI(false);
                });
            AddDccmButton(options, flow, GetMultiplayerSaveButtonLabel(), "Choose multiplayer save slot", () => DccmOpenMultiplayerSlotMenu(options), localizeLabel: false);
            options.updateScroller();
        }

        private static void BuildDccmLobbyNotFoundMenu(Options options)
        {
            var flow = BeginDccmMenu(options, "Can't find lobby");
            if (flow == null)
                return;

            AddDccmInfo(options, flow, "Can't find lobby", Localize("Check the address or Steam lobby code."));
            AddDccmButton(options, flow, "OK", "Return to join menu", () => OpenDccmMenu(DccmJoinLanMenu));
            options.updateScroller();
        }

        private static void BuildDccmErrorMenu(Options options)
        {
            var flow = BeginDccmMenu(options, string.IsNullOrWhiteSpace(_dccmErrorTitle) ? "Connection error" : _dccmErrorTitle);
            if (flow == null)
                return;

            if (!string.IsNullOrWhiteSpace(_dccmErrorDetails))
                AddDccmInfo(options, flow, _dccmErrorDetails);

            AddDccmButton(options, flow, "OK", "Return to previous menu", () =>
            {
                var back = _dccmErrorBack;
                _dccmErrorBack = null;
                back?.Invoke();
            });
            options.updateScroller();
        }

        private static void DccmStartLan(NetRole role)
        {
            if (role == NetRole.Host)
            {
                _menuSelection = NetRole.Host;
                _menuTransport = ConnectionTransport.Lan;
                StartHostServerOnly();
                OpenDccmMenu(DccmHostStatusMenu);
                GetTitleScreen()?.ShouldAutoHideConnectionUI(true);
                return;
            }

            var screen = GetTitleScreen();
            if (screen == null)
            {
                ShowDccmError("Connection failed", "Main menu is not available.", () => OpenDccmMenu(DccmJoinLanMenu));
                return;
            }

            _menuSelection = NetRole.Client;
            _menuTransport = ConnectionTransport.Lan;
            StartNetwork(NetRole.Client, screen);
            OpenDccmMenu(DccmClientWaitingMenu);
            screen.ShouldAutoHideConnectionUI(true);
        }

        private static void DccmStartSteamHost()
        {
            SharedStartSteamHost(
                showError: (title, details, onBack) => ShowDccmError(title, details, onBack),
                showStatus: () => { OpenDccmMenu(DccmHostStatusMenu); GetTitleScreen()?.ShouldAutoHideConnectionUI(true); },
                showTransport: () => OpenDccmMenu(DccmHostTransportMenu)
            );
        }

        private static void DccmStartSteamJoin()
        {
            _steamJoinLobbyResolvePending = true;
            _waitingForHost = true;
            _clientConnecting = true;
            OpenDccmMenu(DccmClientWaitingMenu);
            GetTitleScreen()?.ShouldAutoHideConnectionUI(true);

            _ = Task.Run(() =>
            {
                var ok = SteamConnect.TryResolveJoinEndpointFromClipboard(out var join);
                EnqueueMainThread(() => DccmApplySteamJoinResult(ok, join, fromOverlay: false));
            });
        }

        private static void DccmApplySteamJoinResult(bool ok, SteamConnect.JoinLobbyResult join, bool fromOverlay)
        {
            SharedApplySteamJoinResult(ok, join, fromOverlay,
                showError: (title, details, onBack) => ShowDccmError(title, details, onBack),
                showStatus: () => { OpenDccmMenu(DccmClientWaitingMenu); GetTitleScreen()?.ShouldAutoHideConnectionUI(true); },
                showTransport: () => OpenDccmMenu(DccmJoinTransportMenu)
            );
        }

        private static void ShowDccmError(string title, string details, Action onBack)
        {
            _dccmErrorTitle = title;
            _dccmErrorDetails = details;
            _dccmErrorBack = onBack;
            OpenDccmMenu(DccmErrorMenu);
        }

        private static void DccmStartHostRun(Options options)
        {
            var screen = GetTitleScreen();
            if (screen == null)
            {
                ShowDccmError("Launch failed", "Main menu is not available.", () => OpenDccmMenu(DccmHostStatusMenu));
                return;
            }

            CloseDccmOptions(options);
            StartHostRun(screen);
        }

        private static void DccmOpenMultiplayerSlotMenu(Options options)
        {
            var screen = GetTitleScreen();
            if (screen == null)
            {
                ShowDccmError("Save menu failed", "Main menu is not available.", () => OpenDccmMenu(_menuSelection == NetRole.Client ? DccmClientWaitingMenu : DccmHostStatusMenu));
                return;
            }

            CloseDccmOptions(options);
            OpenMultiplayerSlotMenu(screen);
        }

        private static void DccmEditUsername(NetRole returnRole)
        {
            var screen = GetTitleScreen();
            if (screen == null)
                return;

            CloseCurrentDccmOptions();
            OpenTextInput(screen, GetText.Instance.GetString("Username"), _username, value =>
            {
                var cleaned = CleanUsername(value);
                _username = cleaned;
                SaveConfig();
                SendUsernameToRemote();
                OpenDccmMenuFromTitle(screen, returnRole == NetRole.Host ? DccmHostLanMenu : DccmJoinLanMenu);
            }, noSpaces: true);
        }

        private static void DccmEditIp(NetRole returnRole)
        {
            var screen = GetTitleScreen();
            if (screen == null)
                return;

            CloseCurrentDccmOptions();
            OpenTextInput(screen, GetText.Instance.GetString("IP address"), _mpIp, value =>
            {
                _mpIp = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value;
                SaveConfig();
                OpenDccmMenuFromTitle(screen, returnRole == NetRole.Host ? DccmHostLanMenu : DccmJoinLanMenu);
            }, noSpaces: true);
        }

        private static void DccmEditPort(NetRole returnRole)
        {
            var screen = GetTitleScreen();
            if (screen == null)
                return;

            CloseCurrentDccmOptions();
            OpenTextInput(screen, GetText.Instance.GetString("Port"), _mpPort.ToString(), value =>
            {
                if (!int.TryParse(value, out var parsed) || parsed <= 0 || parsed > 65535)
                    parsed = 1234;
                _mpPort = parsed;
                SaveConfig();
                OpenDccmMenuFromTitle(screen, returnRole == NetRole.Host ? DccmHostLanMenu : DccmJoinLanMenu);
            }, noSpaces: true);
        }

        private static void CloseCurrentDccmOptions()
        {
            var options = Options.Class.ME;
            if (options != null && !options.destroyed)
                CloseDccmOptions(options);
        }

        private static void CloseDccmOptions(Options options)
        {
            try
            {
                if (options != null && !options.destroyed)
                    options.onQuit();
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to close DCCM options menu: {Message}", ex.Message);
            }
        }

        private static void RefreshDccmMenuIfActive(IModMenu menu)
        {
            if (!ReferenceEquals(_dccmActiveMenu, menu))
                return;

            OpenDccmMenu(menu);
        }

        private sealed class DccmMenu(string name, string? subText, Action<Options> build) : IModMenu
        {
            public string GetName() => name;

            public string? GetSubText() => subText;

            public void BuildMenu(Options options) => build(options);
        }
    }
}
