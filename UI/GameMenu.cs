using System;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dc.pr;
using dc.ui;
using HaxeProxy.Runtime;
using Newtonsoft.Json;
using Hashlink.Virtuals;
using Serilog;
using ModCore.Utilities;
using Microsoft.Win32;
using Serilog.Core;
using dc.cine;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using ModCore.Modules;


namespace DeadCellsMultiplayerMod
{
    internal static partial class GameMenu
    {
        private static readonly object Sync = new();
        private static ILogger? _log;
        private static NetRole _role = NetRole.None;
        private static bool _inActualRun;
        private static int? _serverSeed;
        private static int? _remoteSeed;
        private const int MaxSeed = 999_999;
        public static NetNode? NetRef { get; set; }
        private static readonly ConcurrentQueue<Action> _mainThreadQueue = new();

        private static bool _menuHooksAttached;
        private static bool _addMenuHookRegistered;
        private static WeakReference<TitleScreen?>? _titleScreenRef;
        private static string _mpIp = "127.0.0.1";
        private static int _mpPort = 1234;
        private static NetRole _menuSelection = NetRole.None;
        private enum ConnectionTransport
        {
            Lan,
            Steam
        }
        private static ConnectionTransport _menuTransport = ConnectionTransport.Lan;
        private static bool _steamLobbyActive;
        private static ulong _steamLobbyId;
        private static string _steamLobbyCode = string.Empty;
        private static ulong _steamHostSteamId;
        private static ulong? _pendingOverlayJoinLobbyId;
        private static bool _waitingForHost;
        internal const int ClientConnectMaxAttempts = 3;
        private static int _clientConnectAttempt;
        private static bool _clientConnecting;
        private static bool _pendingAutoStart;
        private static bool _levelDescArrived;
        private static bool _autoStartTriggered;
        private static DateTime _autoStartRetryAt = DateTime.MinValue;
        private const int DeathRestartCooldownMs = 1000;
        private static DateTime _deathRestartCooldownUntil = DateTime.MinValue;
        private const string AutoStartMutexName = "DeadCellsMultiplayerMod.AutoStart";
        private static bool _mainMenuButtonAdded;
        private static bool _suppressAutoButton;
        private static bool _worldExitHandled;
        private static bool _hostDisconnectCountdownActive;
        private static DateTime _hostDisconnectCountdownUntil = DateTime.MinValue;
        private static int _lastHostDisconnectCountdown = -1;
        private const int HostDisconnectCountdownSeconds = 5;
        private static bool _seedArrived;
        private static string _username = "guest";
        private static string _remoteUsername = "guest";
        private static string _playerId = Guid.NewGuid().ToString("N");
        public static string Username => _username;
        public static string RemoteUsername => _remoteUsername;

        internal static string GetSteamLobbyCodeForUi()
        {
            if (!string.IsNullOrWhiteSpace(_steamLobbyCode))
                return _steamLobbyCode;

            if (_steamLobbyId > 0)
                return SteamConnect.BuildLobbyCodeFromLobbyId(_steamLobbyId);

            return string.Empty;
        }

        internal static bool TryCopySteamLobbyCodeFromUi()
        {
            var code = GetSteamLobbyCodeForUi();
            if (string.IsNullOrWhiteSpace(code))
                return false;

            return SteamConnect.TryCopyLobbyCodeToClipboard(code);
        }
        private static bool _localReady;
        private static List<PlayerInfo> _playersDisplay = new();
        private static bool _inHostStatusMenu;
        private static bool _inClientWaitingMenu;
        private static bool _genArrived;
        private static LevelDescSync? _cachedLevelDescSync;
        private static readonly object TextInputSync = new();
        private static WeakReference<TextInput?>? _activeTextInputRef;
        private static bool _activeTextInputNoSpaces;
        private const int KeyCtrl = 17;
        private const int KeyLCtrl = 162;
        private const int KeyRCtrl = 163;
        private const int KeyC = 67;
        private const int KeyV = 86;
        private const int KeySpace = 32;
        private const int KeyEsc = 27;
        // Win32 clipboard helpers for text input shortcuts.
        private const uint CfUnicodeText = 13;
        private const uint GmemMoveable = 0x0002;
        [DllImport("user32.dll")]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();
        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();
        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("user32.dll")]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        public static void Initialize(ILogger logger)
        {
            logger.Information("\x1b[32m[[ModEntry.GameMenu] Initializing GameMenu...]\x1b[0m ");
            lock (Sync)
            {
                _log = logger;
                _role = NetRole.None;
                _inActualRun = false;
                _serverSeed = null;
                _remoteSeed = null;
                _levelDescArrived = false;
                _pendingAutoStart = false;
                _autoStartTriggered = false;
                _genArrived = false;
                _seedArrived = false;
                _clientConnectAttempt = 0;
                _clientConnecting = false;
                _deathRestartCooldownUntil = DateTime.MinValue;
                _cachedLevelDescSync = null;
                _hostDisconnectCountdownActive = false;
                _hostDisconnectCountdownUntil = DateTime.MinValue;
                _lastHostDisconnectCountdown = -1;
                _menuTransport = ConnectionTransport.Lan;
                _steamLobbyActive = false;
                _steamLobbyId = 0;
                _steamLobbyCode = string.Empty;
                _steamHostSteamId = 0UL;
            }

            InitializeMenuUiHooks();
        }

        internal static void EnqueueMainThread(Action action)
        {
            if (action == null) return;
            _mainThreadQueue.Enqueue(action);
        }

        internal static void ProcessMainThreadQueue()
        {
            ModEntry.PumpSteamCallbacksForOverlay();
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Main thread task failed: {Message}", ex.Message);
                }
            }
        }

        public static void MarkInRun()
        {
            lock (Sync)
            {
                _inActualRun = true;
            }
        }

        public static void SetRole(NetRole role)
        {
            var previous = _role;
            lock (Sync)
            {
                _role = role;
            }
            if (previous == NetRole.Client && role != NetRole.Client)
            {
                EnqueueMainThread(() =>
                {
                    try
                    {
                        var main = dc.Main.Class.ME;
                        if (main?.user != null)
                            GameDataSync.RestoreOriginalUserState(main.user, true);
                    }
                    catch
                    {
                    }
                });
            }
        }

        public static int ForceGenerateServerSeed(string reason)
        {
            var seed = Random.Shared.Next(1, MaxSeed + 1);
            lock (Sync)
            {
                _serverSeed = seed;
            }
            _log?.Information("[NetMod] Generated host seed {Seed} ({Reason})", seed, reason);
            return seed;
        }

        public static bool TryGetHostRunSeed(out int seed)
        {
            lock (Sync)
            {
                if (_serverSeed.HasValue)
                {
                    seed = _serverSeed.Value;
                    return true;
                }
            }

            seed = 0;
            return false;
        }

        public static void ReceiveHostRunSeed(int seed)
        {
            int? previousSeed = null;
            bool restartClientWorldNow = false;
            lock (Sync)
            {
                previousSeed = _remoteSeed;
                _remoteSeed = seed;
                if (_role == NetRole.Client)
                {
                    var firstSeedForClient = !previousSeed.HasValue;
                    var seedChanged = previousSeed.HasValue && previousSeed.Value != seed;
                    if (_inActualRun)
                    {
                        if (firstSeedForClient || seedChanged)
                        {
                            _inActualRun = false;
                            _pendingAutoStart = false;
                            _autoStartTriggered = false;
                            restartClientWorldNow = true;
                        }
                    }
                    else
                    {
                        _seedArrived = true;
                        _pendingAutoStart = true;
                    }
                }
            }
            _log?.Information("[NetMod] Client received host seed {Seed}", seed);
            if (restartClientWorldNow)
                QueueClientRestartFromHostSeed(seed, "host_restart");
        }

        internal static void QueueHostRestartFromDeath(string reason)
        {
            var now = DateTime.UtcNow;
            lock (Sync)
            {
                if (_role != NetRole.Host)
                    return;
                if (now < _deathRestartCooldownUntil)
                    return;
                _deathRestartCooldownUntil = now.AddMilliseconds(DeathRestartCooldownMs);
            }

            EnqueueMainThread(() =>
            {
                ModEntry.ResetDownedPlayersForRestart();

                var game = ModEntry.Instance?.game;
                if (game?.user == null)
                {
                    _log?.Warning("[NetMod] Skipping host restart ({Reason}): game not ready", reason);
                    return;
                }

                _log?.Information("[NetMod] Host restarting run ({Reason})", reason);
                try
                {
                    var main = dc.Main.Class.ME;
                    if (main != null)
                    {
                        main.launchGame(GameDataSync._launch, null, 0.8);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Host launchGame restart failed, fallback to direct newGame: {Message}", ex.Message);
                }

                game.destroy();
                game.disposeImmediately();
                game.user.newGame(GameDataSync.Seed, GameDataSync._isTwitch, GameDataSync._isCustom, GameDataSync._mode, GameDataSync._launch);
            });
        }

        private static void QueueClientRestartFromHostSeed(int seed, string reason)
        {
            EnqueueMainThread(() =>
            {
                ModEntry.ResetDownedPlayersForRestart();

                var game = ModEntry.Instance?.game;
                if (game?.user == null)
                {
                    _log?.Warning("[NetMod] Skipping client restart ({Reason}): game not ready", reason);
                    lock (Sync)
                    {
                        _seedArrived = true;
                        _pendingAutoStart = true;
                        _autoStartTriggered = false;
                    }
                    return;
                }

                _log?.Information("[NetMod] Client restarting run from host seed {Seed} ({Reason})", seed, reason);
                try
                {
                    var main = dc.Main.Class.ME;
                    if (main != null)
                    {
                        main.launchGame(GameDataSync._launch, null, 0.8);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Client launchGame restart failed, fallback to direct newGame: {Message}", ex.Message);
                }

                game.destroy();
                game.disposeImmediately();
                game.user.newGame(seed, GameDataSync._isTwitch, GameDataSync._isCustom, GameDataSync._mode, GameDataSync._launch);
            });
        }

        public static bool TryGetRemoteSeed(out int seed)
        {
            lock (Sync)
            {
                if (_remoteSeed.HasValue)
                {
                    seed = _remoteSeed.Value;
                    return true;
                }
            }

            seed = 0;
            return false;
        }

        public static void ReceiveLevelDesc(string json)
        {
            try
            {
                var sync = JsonConvert.DeserializeObject<LevelDescSync>(json);
                if (sync == null) return;

                CacheLevelDescSync(sync);
                NotifyLevelDescReceived();
                _log?.Information("[NetMod] Client received LevelDesc");
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to parse LevelDesc: {Message}", ex.Message);
            }
        }

        public static void ReceiveRemoteUsername(string username)
        {
            var cleaned = CleanUsername(username);
            string previous;
            lock (Sync)
            {
                previous = _remoteUsername;
                _remoteUsername = cleaned;
            }
            _log?.Information("[NetMod] Received remote username {Username}", cleaned);
            if (_role == NetRole.Host &&
                !string.Equals(previous, cleaned, StringComparison.Ordinal))
            {
                var userForMsg = cleaned;
                EnqueueMainThread(() =>
                    MultiplayerUI.PushSystemMessage(FormatLocalized("{0} connected to the server.", userForMsg)));
            }
        }

        private static void SendCachedGeneratePayload()
        {
            var net = NetRef;
            if (net == null) return;

            LevelDescSync? levelDesc;
            lock (Sync)
            {
                levelDesc = _cachedLevelDescSync;
            }

            if (levelDesc == null)
                return;

            var payload = new
            {
                levelDesc = levelDesc ?? new LevelDescSync(),
                rawDesc = string.Empty
            };
            var json = JsonConvert.SerializeObject(payload);
            net.SendGeneratePayload(json);
        }

        private static void CacheLevelDescSync(LevelDescSync? sync)
        {
            lock (Sync)
            {
                _cachedLevelDescSync = sync;
            }
        }

        private static LevelDescSync? GetCachedLevelDescSync()
        {
            lock (Sync)
            {
                return _cachedLevelDescSync;
            }
        }

        public static void TickMenu(double dt)
        {
            UpdateHostDisconnectCountdown();
            if (DateTime.UtcNow < _autoStartRetryAt)
                return;

            bool shouldStart = false;

            lock (Sync)
            {
                if (_role == NetRole.Client &&
                    !_inActualRun &&
                    _pendingAutoStart &&
                    _seedArrived &&
                    !_autoStartTriggered)
                {
                    _autoStartTriggered = true;
                    shouldStart = true;
                }
            }

            if (!shouldStart)
                return;

            var ts = GetTitleScreen();
            if (ts != null)
            {
                try
                {
                    Mutex? mutex = null;
                    bool hasHandle = false;
                    try
                    {
                        mutex = new Mutex(false, AutoStartMutexName);
                        try
                        {
                            hasHandle = mutex.WaitOne(0);
                        }
                        catch (AbandonedMutexException)
                        {
                            hasHandle = true;
                        }

                        if (!hasHandle)
                        {
                            lock (Sync)
                            {
                                _autoStartTriggered = false;
                                _pendingAutoStart = true;
                            }
                            _autoStartRetryAt = DateTime.UtcNow.AddMilliseconds(250);
                            return;
                        }

                        ts.startNewGame(custom: true);
                    }
                    finally
                    {
                        if (hasHandle)
                            mutex?.ReleaseMutex();
                        mutex?.Dispose();
                    }
                    _log?.Information("[NetMod] Auto-started new game after seed");
                }
                catch (IOException ioEx)
                {
                    _log?.Warning("[NetMod] Auto-start blocked by config lock: {Message}", ioEx.Message);
                    lock (Sync)
                    {
                        _autoStartTriggered = false;
                        _pendingAutoStart = true;
                    }
                    _autoStartRetryAt = DateTime.UtcNow.AddSeconds(1.5);
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Failed to auto-start new game: {Message}", ex.Message);
                    lock (Sync)
                    {
                        _autoStartTriggered = false;
                        _pendingAutoStart = true;
                    }
                }
            }
            else
            {
                lock (Sync)
                {
                    _autoStartTriggered = false;
                    _pendingAutoStart = true;
                }
            }
        }

        private static void NotifyLevelDescReceived()
        {
            lock (Sync)
            {
                if (_role == NetRole.Client && !_inActualRun)
                {
                    _levelDescArrived = true;
                    _pendingAutoStart = true;
                }
            }
        }

        private static void ShowMultiplayerMenu(TitleScreen screen)
        {
            


            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();
                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Host game"),
                    () => ShowHostTransportMenu(screen),
                    GetText.Instance.GetString("Create a multiplayer session"));
                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Join game"),
                    () => ShowJoinTransportMenu(screen),
                    GetText.Instance.GetString("Connect to an existing host"));
                AddMenuButton(screen, GetText.Instance.GetString("Back"), () =>
                {
                    StopNetworkFromMenu();
                    screen.mainMenu();
                }, GetText.Instance.GetString("Return to main menu"));
                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                RemoveDuplicatesKeepFirst(screen, GetText.Instance.GetString("Host game"), GetText.Instance.GetString("Join game"));
                _inHostStatusMenu = false;
                _inClientWaitingMenu = false;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open multiplayer menu: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        private static void ShowHostTransportMenu(TitleScreen screen)
        {
            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();

                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Lan host"),
                    () => ShowConnectionMenu(screen, NetRole.Host),
                    GetText.Instance.GetString("Use direct IP/port hosting"));

                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Steam host"),
                    () => StartSteamHost(screen),
                    GetText.Instance.GetString("Create Steam lobby and start immediately"));

                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Back"),
                    () => ShowMultiplayerMenu(screen),
                    GetText.Instance.GetString("Back to multiplayer menu"));

                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                RemoveDuplicatesKeepFirst(
                    screen,
                    GetText.Instance.GetString("Lan host"),
                    GetText.Instance.GetString("Steam host"),
                    GetText.Instance.GetString("Back"));
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open host transport menu: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        private static void ShowJoinTransportMenu(TitleScreen screen)
        {
            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();

                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Lan join"),
                    () => ShowConnectionMenu(screen, NetRole.Client),
                    GetText.Instance.GetString("Connect by IP/port"));

                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Steam join"),
                    () => StartSteamJoin(screen),
                    GetText.Instance.GetString("Connect by Steam lobby id/code from clipboard"));

                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Back"),
                    () => ShowMultiplayerMenu(screen),
                    GetText.Instance.GetString("Back to multiplayer menu"));

                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                RemoveDuplicatesKeepFirst(
                    screen,
                    GetText.Instance.GetString("Lan join"),
                    GetText.Instance.GetString("Steam join"),
                    GetText.Instance.GetString("Back"));
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open join transport menu: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        private static void ShowConnectionMenu(TitleScreen screen, NetRole role)
        {
            _menuSelection = role;
            _menuTransport = ConnectionTransport.Lan;
            if (role == NetRole.Client)
                _waitingForHost = true;

            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();

                AddMenuButton(
                    screen,
                    $"{GetText.Instance.GetString("Username: ")}{_username}",
                    () => EditUsername(screen),
                    GetText.Instance.GetString("Edit display name"));

                AddMenuButton(screen, $"{GetText.Instance.GetString("IP: ")}{_mpIp}", () =>
                {
                    OpenTextInput(screen, GetText.Instance.GetString("IP address"), _mpIp, value =>
                    {
                        _mpIp = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value;
                        SaveConfig();
                        ShowConnectionMenu(screen, role);
                    }, noSpaces: true);
                }, GetText.Instance.GetString("Edit IP"));

                AddMenuButton(screen, $"{GetText.Instance.GetString("Port: ")}{_mpPort}", () =>
                {
                    OpenTextInput(screen, GetText.Instance.GetString("Port"), _mpPort.ToString(), value =>
                    {
                        if (!int.TryParse(value, out var parsed) || parsed <= 0 || parsed > 65535)
                            parsed = 1234;
                        _mpPort = parsed;
                        SaveConfig();
                        ShowConnectionMenu(screen, role);
                    }, noSpaces: true);
                }, GetText.Instance.GetString("Edit port"));

                var actionLabel = role == NetRole.Host
                    ? GetText.Instance.GetString("Host")
                    : GetText.Instance.GetString("Join");
                if (role == NetRole.Host)
                {
                    AddMenuButton(screen, actionLabel, () =>
                    {
                        StartHostServerOnly();
                        ShowHostStatusMenu(screen);
                        screen.ShouldAutoHideConnectionUI(true);
                    }, GetText.Instance.GetString("Start hosting"));
                }
                else
                {
                    AddMenuButton(screen, actionLabel, () =>
                    {
                        StartNetwork(role, screen);
                        ShowClientWaitingMenu(screen);
                        screen.ShouldAutoHideConnectionUI(true);
                    }, GetText.Instance.GetString("Connect to host"));
                }

                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Back"),
                    () =>
                    {
                        if (role == NetRole.Host)
                            ShowHostTransportMenu(screen);
                        else
                            ShowJoinTransportMenu(screen);
                        screen.ShouldAutoHideConnectionUI(false);
                    },
                    GetText.Instance.GetString("Back to multiplayer menu"));
                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                RemoveDuplicatesKeepFirst(
                    screen,
                    GetText.Instance.GetString("Host game"),
                    GetText.Instance.GetString("Join game"),
                    "About Core Modding");
                _inHostStatusMenu = false;
                _inClientWaitingMenu = false;
                if (role == NetRole.Host)
                {
                    SetRole(NetRole.None);
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to show connection menu: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        private static void StartSteamHost(TitleScreen screen)
        {
            _menuSelection = NetRole.Host;
            _menuTransport = ConnectionTransport.Steam;
            _steamLobbyActive = false;
            _steamLobbyId = 0;
            _steamLobbyCode = string.Empty;
            _steamHostSteamId = 0UL;
            ConnectionUI.NotifyConnectionsChanged();
            ApplySteamPersonaUsername();

            StartHostServerOnly(bindAnyAddress: true);
            if (NetRef == null || !NetRef.IsAlive || !NetRef.IsHost)
            {
                _log?.Warning("[NetMod][Steam] Host start failed: host server was not created");
                ShowConnectionErrorPopup(
                    screen,
                    GetText.Instance.GetString("Steam host failed"),
                    GetText.Instance.GetString("Could not start Steam host. Check console logs."),
                    () => ShowHostTransportMenu(screen));
                return;
            }

            var lobby = NetRef.HostLobbyResult;
            if (lobby == null || !lobby.Success)
            {
                StopNetworkFromMenu();
                _log?.Warning("[NetMod][SteamWorkerError] {Error}", lobby?.Error ?? "Lobby creation failed");
                ShowConnectionErrorPopup(
                    screen,
                    GetText.Instance.GetString("Steam host failed"),
                    GetText.Instance.GetString("Steam lobby creation failed. Check console logs."),
                    () => ShowHostTransportMenu(screen));
                return;
            }

            if (!string.IsNullOrWhiteSpace(lobby.PersonaName))
                ApplySteamPersonaUsername(lobby.PersonaName);

            _steamLobbyActive = true;
            _steamLobbyId = lobby.LobbyId;
            _steamLobbyCode = SteamConnect.BuildLobbyCodeFromLobbyId(_steamLobbyId);
            ConnectionUI.NotifyConnectionsChanged();
            _log?.Information("[NetMod][Steam] Host lobby ready: id={LobbyId} code={LobbyCode}", _steamLobbyId, _steamLobbyCode);

            if (!(NetRef?.TrySetSteamHostRichPresence(_steamLobbyId) ?? false))
                _log?.Warning("[NetMod][Steam] Skipping main-process rich presence fallback for lobby {LobbyId}; Steam worker must own rich presence", _steamLobbyId);

            var copied = SteamConnect.TryCopyLobbyCodeToClipboard(_steamLobbyCode)
                         || SteamConnect.TryCopyLobbyIdToClipboard(lobby.LobbyId);
            if (copied)
                MultiplayerUI.PushSystemMessage("Lobby id copied to clipboard");

            ShowHostStatusMenu(screen);
            screen.ShouldAutoHideConnectionUI(true);
        }

        private static void StartSteamJoin(TitleScreen screen)
        {
            _menuSelection = NetRole.Client;
            _menuTransport = ConnectionTransport.Steam;
            _steamLobbyActive = false;
            _steamLobbyId = 0;
            _steamLobbyCode = string.Empty;
            _steamHostSteamId = 0UL;
            ConnectionUI.NotifyConnectionsChanged();
            ApplySteamPersonaUsername();

            ShowSteamConnectingMenu(screen);
            _ = Task.Run(() =>
            {
                var ok = SteamConnect.TryResolveJoinEndpointFromClipboard(out var join);
                EnqueueMainThread(() => ApplySteamJoinResult(screen, ok, join, fromOverlay: false));
            });
        }

        internal static void HandleSteamOverlayJoinRequest(ulong lobbyId)
        {
            var screen = GetTitleScreen();
            if (screen == null)
            {
                _pendingOverlayJoinLobbyId = lobbyId;
                _log?.Information("[NetMod][Steam] Overlay join request queued: not at main menu (lobbyId={LobbyId})", lobbyId);
                return;
            }

            _log?.Information("[NetMod][Steam] Overlay join starting: lobbyId={LobbyId} screen=ok", lobbyId);

            _menuSelection = NetRole.Client;
            _menuTransport = ConnectionTransport.Steam;
            _steamLobbyActive = false;
            _steamLobbyId = 0;
            _steamLobbyCode = string.Empty;
            _steamHostSteamId = 0UL;
            ConnectionUI.NotifyConnectionsChanged();
            ApplySteamPersonaUsername();

            ShowSteamConnectingMenu(screen);
            _ = Task.Run(() =>
            {
                _log?.Information("[NetMod][Steam] Overlay join resolving lobby (lobbyId={LobbyId})", lobbyId);
                var ok = SteamConnect.TryResolveJoinEndpointFromLobbyId(lobbyId, out var join);
                EnqueueMainThread(() => ApplySteamJoinResult(screen, ok, join, fromOverlay: true));
            });
        }

        private static void ApplySteamPersonaUsername(string? preferredPersona = null)
        {
            var candidate = string.IsNullOrWhiteSpace(preferredPersona)
                ? GetDefaultUsername()
                : preferredPersona;

            var cleaned = CleanUsername(candidate);
            if (string.IsNullOrWhiteSpace(cleaned))
                return;

            _username = cleaned;
            SaveConfig();
            SendUsernameToRemote();
        }

        private static void ShowSteamConnectingMenu(TitleScreen screen)
        {
            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();
                AddInfoLine(screen, GetText.Instance.GetString("Connecting to Steam lobby..."), infoColor: 0xE0E0E0);
                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                _inClientWaitingMenu = false;
                _inHostStatusMenu = false;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to show Steam connecting menu: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        private static void ApplySteamJoinResult(TitleScreen screen, bool ok, SteamConnect.JoinLobbyResult join, bool fromOverlay)
        {
            if (fromOverlay)
                _log?.Information("[NetMod][Steam] Overlay join result: ok={Ok} error={Error}", ok, join.Error ?? "(none)");

            if (!ok)
            {
                _log?.Warning("[NetMod][SteamWorkerError] {Error}", join.Error);
                ShowConnectionErrorPopup(
                    screen,
                    GetText.Instance.GetString("Steam join failed"),
                    GetText.Instance.GetString("Steam join failed. Check console logs."),
                    () => ShowJoinTransportMenu(screen));
                return;
            }

            if (!string.IsNullOrWhiteSpace(join.PersonaName))
                ApplySteamPersonaUsername(join.PersonaName);

            if (join.HostSteamId == 0UL && join.Endpoint == null)
            {
                _log?.Warning("[NetMod][Steam] Join failed: lobby endpoint and host Steam id are missing");
                ShowConnectionErrorPopup(
                    screen,
                    GetText.Instance.GetString("Steam join failed"),
                    GetText.Instance.GetString("Steam lobby endpoint is invalid. Check console logs."),
                    () => ShowJoinTransportMenu(screen));
                return;
            }

            if (join.Endpoint != null)
            {
                _mpIp = join.Endpoint.Address.ToString();
                _mpPort = join.Endpoint.Port;
                SaveConfig();
            }
            else if (join.HostSteamId != 0UL)
            {
                _log?.Information("[NetMod][Steam] {Source} join: P2P-only (hostSteamId={HostSteamId})", fromOverlay ? "Overlay" : "Clipboard", join.HostSteamId);
            }
            _steamLobbyId = join.LobbyId;
            _steamLobbyCode = SteamConnect.BuildLobbyCodeFromLobbyId(_steamLobbyId);
            _steamHostSteamId = join.HostSteamId;
            ConnectionUI.NotifyConnectionsChanged();
            _log?.Information("[NetMod][Steam] Joined lobby: id={LobbyId} code={LobbyCode} hostSteamId={HostSteamId}", _steamLobbyId, _steamLobbyCode, _steamHostSteamId);

            StartNetwork(NetRole.Client, screen);
            ShowClientWaitingMenu(screen);
            screen.ShouldAutoHideConnectionUI(true);
        }

        private static void ShowConnectionErrorPopup(TitleScreen screen, string title, string details, Action onOk)
        {
            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();

                AddInfoLine(screen, title, infoColor: 0xFF9090);
                if (!string.IsNullOrWhiteSpace(details))
                    AddInfoLine(screen, details, infoColor: 0xE0E0E0);

                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("OK"),
                    onOk,
                    GetText.Instance.GetString("Return to previous menu"));

                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                RemoveDuplicatesKeepFirst(screen, GetText.Instance.GetString("OK"));
                _inClientWaitingMenu = false;
                _inHostStatusMenu = false;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open connection error popup: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        private static void StartNetwork(NetRole role, TitleScreen screen)
        {
            try
            {
                if (ModEntry.Instance == null)
                {
                    _log?.Warning("[NetMod] ModEntry instance unavailable for network start");
                    return;
                }

                if (role == NetRole.Host)
                {
                    if (_menuTransport == ConnectionTransport.Steam)
                        ModEntry.Instance.StartSteamHostFromMenu(_mpPort);
                    else
                        ModEntry.Instance.StartHostFromMenu(_mpIp, _mpPort);
                    _waitingForHost = false;
                    try
                    {
                        screen.startNewGame(custom: false);
                    }
                    catch (Exception ex)
                    {
                        _log?.Warning("[NetMod] Failed to start host run: {Message}", ex.Message);
                    }
                }
                else if (role == NetRole.Client)
                {
                    if (_menuTransport == ConnectionTransport.Steam)
                    {
                        if (_steamHostSteamId == 0UL)
                        {
                            _log?.Warning("[NetMod][Steam] Client start aborted: host Steam id is missing");
                            ShowConnectionErrorPopup(
                                screen,
                                GetText.Instance.GetString("Steam join failed"),
                                GetText.Instance.GetString("Steam host id is missing. Check console logs."),
                                () => ShowJoinTransportMenu(screen));
                            return;
                        }
                    }

                    lock (Sync)
                    {
                        _levelDescArrived = false;
                        _pendingAutoStart = false;
                        _autoStartTriggered = false;
                        _seedArrived = false;
                        _clientConnectAttempt = 0;
                        _clientConnecting = true;
                        _waitingForHost = true;
                    }

                    if (_menuTransport == ConnectionTransport.Steam)
                        ModEntry.Instance.StartSteamClientFromMenu(_steamHostSteamId);
                    else
                        ModEntry.Instance.StartClientFromMenu(_mpIp, _mpPort);
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to start network: {Message}", ex.Message);
            }
        }

        private static void StartHostServerOnly(bool bindAnyAddress = false)
        {
            // try
            // {
            if (ModEntry.Instance == null)
            {
                _log?.Warning("[NetMod] ModEntry instance unavailable for host start");
                return;
            }

            if (NetRef != null && NetRef.IsAlive && NetRef.IsHost)
            {
                _waitingForHost = false;
                return;
            }

            if (_menuTransport == ConnectionTransport.Steam)
            {
                ModEntry.Instance.StartSteamHostFromMenu(_mpPort);
            }
            else
            {
                var hostIp = bindAnyAddress ? "0.0.0.0" : _mpIp;
                ModEntry.Instance.StartHostFromMenu(hostIp, _mpPort);
            }
            _waitingForHost = false;
            // }
            // catch (Exception ex)
            // {
            //     _log?.Warning("[NetMod] Host start failed: {Message}", ex.Message);
            // }
        }

        private static void StartHostRun(TitleScreen screen)
        {
            StartHostServerOnly();
            try
            {
                screen.startNewGame(custom: false);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to start host run: {Message}", ex.Message);
            }
        }

        // private static void GameDisposeHook(Hook_Game.orig_onDispose orig, Game self)
        // {
        //     try
        //     {
        //         HandleWorldExit(isDisposeHook: true);
        //     }
        //     catch (Exception ex)
        //     {
        //         _log?.Warning("[NetMod] onDispose hook error: {Message}", ex.Message);
        //     }

        //     orig(self);
        // }

        private static void HandleWorldExit(bool isDisposeHook = false)
        {
            lock (Sync)
            {
                if (_worldExitHandled) return;
                _worldExitHandled = true;
            }

            var roleBefore = _role;
            if (roleBefore == NetRole.Host)
            {
                try { NetRef?.SendControlAndFlush("KICK", 320); } catch { }
            }

            try
            {
                NetRef?.Dispose();
            }
            catch { }

            SetRole(NetRole.None);
            NetRef = null;
            _waitingForHost = false;
            ResetClientConnectState();
            _menuSelection = NetRole.None;
            ResetSteamState();

            if (roleBefore == NetRole.Client)
            {
                ForceExitToMainMenu();
            }

            lock (Sync)
            {
                _worldExitHandled = false;
            }
        }

        private static void ForceExitToMainMenu()
        {
            try
            {
                var boot = dc.Boot.Class?.ME;
                if (boot != null)
                {
                    boot.returnToMainMenu();
                    return;
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Boot.returnToMainMenu failed: {Message}", ex.Message);
            }

            try
            {
                var titleScreen = GetTitleScreen();
                if (titleScreen != null)
                {
                    titleScreen.mainMenu();
                    return;
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] TitleScreen.mainMenu failed: {Message}", ex.Message);
            }

            try
            {
                var main = dc.Main.Class?.ME;
                if (main != null)
                    _ = main.onExit();
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Main.onExit fallback failed: {Message}", ex.Message);
            }
        }

        private static void ShowHostStatusMenu(TitleScreen screen)
        {
            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();

                var multiplayerSaveLabel = GetMultiplayerSaveButtonLabel();
                AddMenuButton(screen, GetText.Instance.GetString("Play"), () => StartHostRun(screen), GetText.Instance.GetString("Launch game"));
                AddMenuButton(screen, multiplayerSaveLabel, () => OpenMultiplayerSlotMenu(screen), Localize("Choose multiplayer save slot"));
                AddMenuButton(screen, GetText.Instance.GetString("Back"), () =>
                {
                    StopNetworkFromMenu();
                    SetRole(NetRole.None);
                    _menuSelection = NetRole.None;
                    ShowMultiplayerMenu(screen);
                    screen.ShouldAutoHideConnectionUI(false);
                }, GetText.Instance.GetString("Back to host setup"));

                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                RemoveDuplicatesKeepFirst(screen, GetText.Instance.GetString("Play"), multiplayerSaveLabel, GetText.Instance.GetString("Back"));
                _inHostStatusMenu = true;
                _inClientWaitingMenu = false;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open host status menu: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        private static void ShowClientWaitingMenu(TitleScreen screen)
        {
            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();

                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Disconnect"),
                    () => {DisconnectFromMenu(screen); screen.ShouldAutoHideConnectionUI(false);},
                    GetText.Instance.GetString("Disconnect and return to main menu"));
                var multiplayerSaveLabel = GetMultiplayerSaveButtonLabel();
                AddMenuButton(screen, multiplayerSaveLabel, () => OpenMultiplayerSlotMenu(screen), Localize("Choose multiplayer save slot"));

                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                RemoveDuplicatesKeepFirst(screen, GetText.Instance.GetString("Disconnect"), multiplayerSaveLabel);
                _inClientWaitingMenu = true;
                _inHostStatusMenu = false;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open client waiting menu: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        private static void ShowLobbyNotFoundPopup(TitleScreen screen)
        {
            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();

                AddInfoLine(screen, GetText.Instance.GetString("Can't find lobby"), infoColor: 0xFF9090);
                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("OK"),
                    () => ShowConnectionMenu(screen, NetRole.Client),
                    GetText.Instance.GetString("Return to join menu"));

                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                RemoveDuplicatesKeepFirst(screen, GetText.Instance.GetString("OK"));
                _inClientWaitingMenu = false;
                _inHostStatusMenu = false;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open lobby not found popup: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        private static void DisconnectFromMenu(TitleScreen screen)
        {
            StopNetworkFromMenu();
            _waitingForHost = false;
            ResetClientConnectState();
            _menuSelection = NetRole.None;
            ResetSteamState();
            _inHostStatusMenu = false;
            _inClientWaitingMenu = false;
            screen.mainMenu();
        }

        private static void StopNetworkFromMenu()
        {
            ResetHostDisconnectCountdown();
            try
            {
                ModEntry.Instance?.StopNetworkFromMenu();
            }
            catch { }
            lock (Sync)
            {
                _inActualRun = false;
            }
            ResetSteamState();
        }

        private static void EditUsername(TitleScreen screen)
        {
            OpenTextInput(screen, GetText.Instance.GetString("Username"), _username, value =>
            {
                var cleaned = CleanUsername(value);
                _username = cleaned;
                SaveConfig();
                SendUsernameToRemote();
                ShowConnectionMenu(screen, _menuSelection == NetRole.None ? NetRole.Host : _menuSelection);
            }, noSpaces: true);
        }

        public static void NotifyRemoteConnected(NetRole role)
        {
            ResetHostDisconnectCountdown();
            SendUsernameToRemote();

            if (role == NetRole.Host)
            {
                _waitingForHost = false;
                SendCachedDataToRemote();
                SendCachedGeneratePayload();
                ConnectionUI.NotifyConnectionsChanged();

                if (_menuSelection == NetRole.Host)
                {
                    var ts = GetTitleScreen();
                    if (ts != null) ShowHostStatusMenu(ts);
                }
            }
            else if (role == NetRole.Client)
            {
                _waitingForHost = false;
                _clientConnecting = false;
                _clientConnectAttempt = 0;
                ConnectionUI.NotifyConnectionsChanged();
                if (_menuSelection == NetRole.Client)
                {
                    var ts = GetTitleScreen();
                    if (ts != null) ShowClientWaitingMenu(ts);
                }
            }
        }

        internal static void NotifyClientConnectAttempt(int attempt)
        {
            lock (Sync)
            {
                _clientConnectAttempt = attempt;
                _clientConnecting = true;
                _waitingForHost = true;
            }

            if (_menuSelection == NetRole.Client)
            {
                var ts = GetTitleScreen();
                if (ts != null) ShowClientWaitingMenu(ts);
            }
        }

        internal static void NotifyClientConnectFailed()
        {
            StopNetworkFromMenu();
            ResetClientConnectState();
            _waitingForHost = false;
            _menuSelection = NetRole.Client;

            var ts = GetTitleScreen();
            if (ts != null) ShowLobbyNotFoundPopup(ts);
        }

        public static void NotifyRemoteDisconnected(NetRole role)
        {
            if (role == NetRole.Host)
            {
                var disconnectedName = string.IsNullOrWhiteSpace(_remoteUsername) ? Localize("Guest") : _remoteUsername.Trim();
                MultiplayerUI.PushSystemMessage(FormatLocalized("{0} disconnected from the server.", disconnectedName));
                _remoteUsername = "guest";
                _localReady = false;
                _genArrived = false;
                _seedArrived = false;
                if (_menuSelection == NetRole.Host)
                {
                    var ts = GetTitleScreen();
                    if (ts != null) ShowHostStatusMenu(ts);
                }
                return;
            }

            var wasInRun = _inActualRun;
            SetRole(NetRole.None);
            NetRef = null;
            _waitingForHost = false;
            _menuSelection = NetRole.None;
            ResetSteamState();
            ClearNetworkCaches();
            _inHostStatusMenu = false;
            _inClientWaitingMenu = false;
            _remoteUsername = "guest";
            _localReady = false;
            _genArrived = false;
            MultiplayerUI.PushSystemMessage(Localize("Host disconnected from server."));
            if (wasInRun)
                StartHostDisconnectCountdown();
        }

        private static void SendUsernameToRemote()
        {
            var net = NetRef;
            if (net == null || !net.HasRemote) return;

            try
            {
                net.SendUsername(_username);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to send username: {Message}", ex.Message);
            }
        }

        private static void SendCachedDataToRemote()
        {
            var net = NetRef;
            if (net == null) return;

            try
            {
                var ld = GetCachedLevelDescSync();
                if (ld != null)
                    net.SendLevelDesc(JsonConvert.SerializeObject(ld));
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to re-send LevelDesc: {Message}", ex.Message);
            }
        }

        private static bool AllPlayersReady()
        {
            if (!_localReady) return false;
            if (_playersDisplay.Count == 0) return true;
            return _playersDisplay.All(p => p.Ready);
        }

        private static void ClearNetworkCaches()
        {
            CacheLevelDescSync(null);
            _genArrived = false;
            _seedArrived = false;
        }

        private static void ResetSteamState()
        {
            if (!(NetRef?.TryClearSteamRichPresence() ?? false) && _steamLobbyId != 0UL)
                _log?.Warning("[NetMod][Steam] Skipping main-process rich presence clear fallback for lobby {LobbyId}; Steam worker must own rich presence", _steamLobbyId);
            var lobbyId = _steamLobbyId;
            if (lobbyId != 0UL)
            {
                try { SteamConnect.LeaveLobby(lobbyId); } catch { }
            }
            try { SteamConnect.StopHostLobbyWorker(); } catch { }
            _steamLobbyActive = false;
            _steamLobbyId = 0;
            _steamLobbyCode = string.Empty;
            _steamHostSteamId = 0UL;
            ConnectionUI.NotifyConnectionsChanged();
            if (_menuTransport == ConnectionTransport.Steam)
                _menuTransport = ConnectionTransport.Lan;
        }

        private static void StartHostDisconnectCountdown()
        {
            _hostDisconnectCountdownActive = true;
            _hostDisconnectCountdownUntil = DateTime.UtcNow.AddSeconds(HostDisconnectCountdownSeconds);
            _lastHostDisconnectCountdown = HostDisconnectCountdownSeconds;
            MultiplayerUI.PushSystemMessage(FormatLocalized("Back to menu in {0}...", HostDisconnectCountdownSeconds));
        }

        private static void ResetHostDisconnectCountdown()
        {
            _hostDisconnectCountdownActive = false;
            _hostDisconnectCountdownUntil = DateTime.MinValue;
            _lastHostDisconnectCountdown = -1;
        }

        private static void UpdateHostDisconnectCountdown()
        {
            if (!_hostDisconnectCountdownActive)
                return;

            var remaining = (int)Math.Ceiling((_hostDisconnectCountdownUntil - DateTime.UtcNow).TotalSeconds);
            if (remaining < 0)
                remaining = 0;

            if (remaining != _lastHostDisconnectCountdown)
            {
                _lastHostDisconnectCountdown = remaining;
                MultiplayerUI.PushSystemMessage(FormatLocalized("Back to menu in {0}...", remaining));
            }

            if (remaining > 0)
                return;

            _hostDisconnectCountdownActive = false;
            ForceExitToMainMenu();
        }

        internal static string Localize(string message)
        {
            return GetText.Instance.GetString(message);
        }

        private static string FormatLocalized(string format, params object[] args)
        {
            var localizedFormat = Localize(format);
            try
            {
                return string.Format(CultureInfo.InvariantCulture, localizedFormat, args);
            }
            catch
            {
                return string.Format(CultureInfo.InvariantCulture, format, args);
            }
        }

        public static void ReceiveGeneratePayload(string json)
        {
            try
            {
                var payload = JsonConvert.DeserializeAnonymousType(json, new
                {
                    levelDesc = new LevelDescSync(),
                    rawDesc = string.Empty
                });
                if (payload == null) return;

                if (payload.levelDesc != null && !IsChallengeLevel(payload.levelDesc.LevelId))
                {
                    CacheLevelDescSync(payload.levelDesc);
                    _log?.Information("[NetMod] Client cached LevelDescSync from generate payload");
                }

                if (!string.IsNullOrWhiteSpace(payload.rawDesc))
                {
                    _log?.Information("[NetMod] Client received raw LevelDesc: {Json}", payload.rawDesc);
                }

                lock (Sync)
                {
                    if (_role == NetRole.Client && !_inActualRun)
                    {
                        _genArrived = true;
                        _pendingAutoStart = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to receive generate payload: {Message}", ex.Message);
            }
        }

        private static bool IsChallengeLevel(string levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId)) return false;
            return levelId.IndexOf("challenge", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class LevelDescSync
        {
            public string LevelId { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public int MapDepth { get; set; }
            public double MobDensity { get; set; }
            public int MinGold { get; set; }
            public double EliteRoomChance { get; set; }
            public double EliteWanderChance { get; set; }
            public int DoubleUps { get; set; }
            public int TripleUps { get; set; }
            public int QuarterUpsBC3 { get; set; }
            public int QuarterUpsBC4 { get; set; }
            public int WorldDepth { get; set; }
            public int BaseLootLevel { get; set; }
            public double BonusTripleScrollAfterBC { get; set; }
            public double CellBonus { get; set; }
            public int Group { get; set; }
        }

        private sealed class MenuConfig
        {
            public string user { get; set; } = "guest";
            public string last_ip { get; set; } = "127.0.0.1";
            public int last_port { get; set; } = 1234;
            public string player_id { get; set; } = Guid.NewGuid().ToString("N");
        }

        private sealed class PlayerInfo
        {
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public string Name { get; set; } = "guest";
            public bool Ready { get; set; }
            public bool IsHost { get; set; }
        }

        private static void LoadConfig()
        {
            try
            {
                var path = GetConfigPath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var cfg = JsonConvert.DeserializeObject<MenuConfig>(json);
                    if (cfg != null)
                    {
                        _username = CleanUsername(string.IsNullOrWhiteSpace(cfg.user) ? GetDefaultUsername() : cfg.user);
                        _mpIp = string.IsNullOrWhiteSpace(cfg.last_ip) ? "127.0.0.1" : cfg.last_ip.Trim();
                        _mpPort = cfg.last_port <= 0 || cfg.last_port > 65535 ? 1234 : cfg.last_port;
                        _playerId = string.IsNullOrWhiteSpace(cfg.player_id) ? Guid.NewGuid().ToString("N") : cfg.player_id.Trim();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to load config: {Message}", ex.Message);
            }

            _username = CleanUsername(GetDefaultUsername());
            _mpIp = "127.0.0.1";
            _mpPort = 1234;
            _playerId = Guid.NewGuid().ToString("N");
            SaveConfig();
        }

        private static void SaveConfig()
        {
            try
            {
                var path = GetConfigPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var cfg = new MenuConfig
                {
                    user = _username,
                    last_ip = _mpIp,
                    last_port = _mpPort,
                    player_id = _playerId
                };
                var json = JsonConvert.SerializeObject(cfg, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to save config: {Message}", ex.Message);
            }
        }

        private static string GetConfigPath()
        {
            var baseDir = AppContext.BaseDirectory;
            var root = Directory.GetParent(baseDir)?.Parent?.Parent?.Parent?.FullName ?? baseDir;
            var dir = Path.Combine(root, "mods", "DeadCellsMultiplayerMod");
            return Path.Combine(dir, "config.json");
        }

        private static string CleanUsername(string? value)
        {
            var cleaned = string.IsNullOrWhiteSpace(value) ? "guest" : value.Trim();
            cleaned = cleaned.Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
            return cleaned.Length == 0 ? "guest" : cleaned;
        }

        private static string GetDefaultUsername()
        {
            var steamName = TryGetSteamPersonaName();
            if (!string.IsNullOrWhiteSpace(steamName))
                return CleanUsername(steamName);
            try
            {
                var env = Environment.UserName;
                if (!string.IsNullOrWhiteSpace(env))
                    return CleanUsername(env);
            }
            catch { }
            return "guest";
        }

        private static string? TryGetSteamPersonaName()
        {
            string? steamPath = null;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key != null)
                {
                    steamPath = key.GetValue("SteamPath") as string;
                    if (string.IsNullOrWhiteSpace(steamPath))
                        steamPath = key.GetValue("InstallPath") as string;
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(steamPath))
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                    if (key != null)
                        steamPath = key.GetValue("InstallPath") as string;
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(steamPath))
                return null;

            string loginUsersPath = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(loginUsersPath))
                return null;

            return TryParseMostRecentPersonaName(loginUsersPath);
        }

        private static string? TryParseMostRecentPersonaName(string path)
        {
            try
            {
                var lines = File.ReadAllLines(path);
                int depth = 0;
                bool pendingUserBlock = false;
                bool inUserBlock = false;
                int userBlockDepth = 0;
                bool isMostRecent = false;
                string? personaCandidate = null;

                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0)
                        continue;

                    if (!inUserBlock && depth == 1 && IsQuotedKeyOnly(line))
                    {
                        pendingUserBlock = true;
                        isMostRecent = false;
                        personaCandidate = null;
                    }

                    if (line.StartsWith("{", StringComparison.Ordinal))
                    {
                        depth++;
                        if (pendingUserBlock && depth == 2)
                        {
                            inUserBlock = true;
                            userBlockDepth = depth;
                            pendingUserBlock = false;
                        }
                        continue;
                    }

                    if (line.StartsWith("}", StringComparison.Ordinal))
                    {
                        if (inUserBlock && depth == userBlockDepth)
                        {
                            if (isMostRecent && !string.IsNullOrWhiteSpace(personaCandidate))
                                return personaCandidate;
                            inUserBlock = false;
                            personaCandidate = null;
                            isMostRecent = false;
                        }
                        depth = Math.Max(0, depth - 1);
                        continue;
                    }

                    if (!inUserBlock)
                        continue;

                    if (TryParseVdfPair(line, out var key, out var value))
                    {
                        if (key.Equals("PersonaName", StringComparison.OrdinalIgnoreCase))
                            personaCandidate = value;
                        else if (key.Equals("MostRecent", StringComparison.OrdinalIgnoreCase))
                            isMostRecent = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch { }

            return null;
        }

        private static bool IsQuotedKeyOnly(string line)
        {
            if (!line.StartsWith("\"", StringComparison.Ordinal))
                return false;
            int secondQuote = line.IndexOf('"', 1);
            if (secondQuote < 0)
                return false;
            int thirdQuote = line.IndexOf('"', secondQuote + 1);
            return thirdQuote < 0;
        }

        private static bool TryParseVdfPair(string line, out string key, out string value)
        {
            key = string.Empty;
            value = string.Empty;
            if (!line.StartsWith("\"", StringComparison.Ordinal))
                return false;
            int keyEnd = line.IndexOf('"', 1);
            if (keyEnd < 0)
                return false;
            int valueStart = line.IndexOf('"', keyEnd + 1);
            if (valueStart < 0)
                return false;
            int valueEnd = line.IndexOf('"', valueStart + 1);
            if (valueEnd < 0)
                return false;
            key = line.Substring(1, keyEnd - 1);
            value = line.Substring(valueStart + 1, valueEnd - valueStart - 1);
            return true;
        }

        private static string BuildStatus(NetRole role)
        {
            var net = NetRef;
            if (role == NetRole.Client && _clientConnecting)
            {
                if (_clientConnectAttempt > 0)
                    return $"{GetText.Instance.GetString("connecting...")} ({_clientConnectAttempt}/{ClientConnectMaxAttempts})";
                return GetText.Instance.GetString("connecting...");
            }

            if (net != null && net.HasRemote)
                return role == NetRole.Host
                    ? GetText.Instance.GetString("client connected")
                    : GetText.Instance.GetString("connected to host");

            if (role == NetRole.Client)
                return _waitingForHost
                    ? GetText.Instance.GetString("waiting for the host")
                    : GetText.Instance.GetString("not connected");

            return GetText.Instance.GetString("waiting for client");
        }

        private static List<string> BuildPlayerLines(NetRole role)
        {
            var parts = new System.Collections.Generic.List<string>();
            var net = NetRef;
            if (role == NetRole.Host)
            {
                parts.Add(_username);
                if (net != null && net.HasRemote)
                    parts.Add(_remoteUsername);
            }
            else
            {
                parts.Add(_username);
                if (net != null && net.HasRemote)
                    parts.Add(_remoteUsername);
            }

            return parts;
        }

        private static void AddPlayerLines(TitleScreen screen, NetRole role, int? infoColor = null)
        {
            var prefix = GetText.Instance.GetString("- ");
            foreach (var line in BuildPlayerLines(role))
            {
                AddInfoLine(screen, $"{prefix}{line}", infoColor: infoColor);
            }
        }

        private static void ResetClientConnectState()
        {
            lock (Sync)
            {
                _clientConnectAttempt = 0;
                _clientConnecting = false;
            }
        }

        internal static void HandleTextInputClipboardShortcuts()
        {
            var textInput = GetActiveTextInput();
            if (textInput == null)
                return;

            if (!IsTextInputActive(textInput))
            {
                ClearActiveTextInput();
                return;
            }

            if (_activeTextInputNoSpaces)
                RemoveSpacesFromTextInput(textInput);

            if (dc.hxd.Key.Class.isPressed(KeyEsc))
            {
                try
                {
                    textInput.cancel();
                }
                finally
                {
                    ClearActiveTextInput();
                }
                return;
            }

            if (dc.hxd.Key.Class.isPressed(KeySpace))
            {
                try
                {
                    textInput.validate();
                }
                finally
                {
                    ClearActiveTextInput();
                }
                return;
            }

            if (!IsCtrlDown())
                return;

            if (dc.hxd.Key.Class.isPressed(KeyC))
            {
                if (TryGetTextInputValue(textInput, out var text))
                    TrySetClipboardText(text);
                return;
            }

            if (dc.hxd.Key.Class.isPressed(KeyV))
            {
                var clip = TryGetClipboardText();
                if (!string.IsNullOrEmpty(clip))
                {
                    if (_activeTextInputNoSpaces)
                        clip = RemoveSpaces(clip);
                    TrySetTextInputValue(textInput, clip);
                }
            }
        }

        private static bool IsCtrlDown()
        {
            return dc.hxd.Key.Class.isDown(KeyCtrl) || dc.hxd.Key.Class.isDown(KeyLCtrl) || dc.hxd.Key.Class.isDown(KeyRCtrl);
        }

        private static void RegisterActiveTextInput(TextInput input, bool noSpaces)
        {
            lock (TextInputSync)
            {
                _activeTextInputRef = new WeakReference<TextInput?>(input);
                _activeTextInputNoSpaces = noSpaces;
            }
        }

        private static void ClearActiveTextInput()
        {
            lock (TextInputSync)
            {
                _activeTextInputRef = null;
                _activeTextInputNoSpaces = false;
            }
        }

        private static TextInput? GetActiveTextInput()
        {
            lock (TextInputSync)
            {
                if (_activeTextInputRef != null && _activeTextInputRef.TryGetTarget(out var input))
                    return input;
            }

            return null;
        }

        private static bool IsTextInputActive(TextInput input)
        {
            var active = GetMemberValue(input, "isActive", true) ?? GetMemberValue(input, "active", true);
            if (active is bool activeBool)
                return activeBool;

            var visible = GetMemberValue(input, "visible", true) ?? GetMemberValue(input, "isVisible", true);
            if (visible is bool visibleBool)
                return visibleBool;

            var target = GetTextInputTarget(input);
            var focused = GetMemberValue(target, "hasFocus", true) ?? GetMemberValue(target, "focused", true);
            if (focused is bool focusedBool)
                return focusedBool;

            return true;
        }

        private static object? GetTextInputTarget(TextInput input)
        {
            return GetMemberValue(input, "input", true)
                ?? GetMemberValue(input, "textInput", true)
                ?? GetMemberValue(input, "textField", true)
                ?? input;
        }

        private static bool TryGetTextInputValue(TextInput input, out string text)
        {
            text = string.Empty;
            var target = GetTextInputTarget(input);
            if (target == null)
                return false;

            var value = GetMemberValue(target, "text", true)
                ?? GetMemberValue(target, "value", true)
                ?? GetMemberValue(target, "str", true);
            if (value == null)
                return false;

            if (value is dc.String ds)
            {
                text = ds.ToString() ?? string.Empty;
                return true;
            }

            text = value.ToString() ?? string.Empty;
            return true;
        }

        private static bool TrySetTextInputValue(TextInput input, string text)
        {
            var target = GetTextInputTarget(input);
            if (target == null)
                return false;

            if (TryInvokeTextInputSetter(target, MakeHLString(text))
                || TryInvokeTextInputSetter(target, text))
                return true;

            return TrySetMember(target, "text", MakeHLString(text))
                || TrySetMember(target, "value", MakeHLString(text))
                || TrySetMember(target, "str", MakeHLString(text))
                || TrySetMember(target, "text", text)
                || TrySetMember(target, "value", text)
                || TrySetMember(target, "str", text);
        }

        private static bool TryInvokeTextInputSetter(object target, object value)
        {
            var type = target.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            foreach (var name in new[] { "setText", "set_text", "setValue", "set_value" })
            {
                var method = type.GetMethod(name, flags);
                if (method == null)
                    continue;

                try
                {
                    method.Invoke(target, new[] { value });
                    return true;
                }
                catch
                {
                    // Try next setter.
                }
            }

            return false;
        }

        private static void RemoveSpacesFromTextInput(TextInput input)
        {
            if (!TryGetTextInputValue(input, out var text))
                return;

            if (!text.Contains(' ', StringComparison.Ordinal))
                return;

            TrySetTextInputValue(input, RemoveSpaces(text));
        }

        private static string RemoveSpaces(string value)
        {
            return value.Replace(" ", string.Empty, StringComparison.Ordinal);
        }

        private static string? TryGetClipboardText()
        {
            try
            {
                if (!IsClipboardFormatAvailable(CfUnicodeText))
                    return null;
                if (!OpenClipboard(IntPtr.Zero))
                    return null;

                try
                {
                    var handle = GetClipboardData(CfUnicodeText);
                    if (handle == IntPtr.Zero)
                        return null;

                    var ptr = GlobalLock(handle);
                    if (ptr == IntPtr.Zero)
                        return null;

                    try
                    {
                        return Marshal.PtrToStringUni(ptr);
                    }
                    finally
                    {
                        GlobalUnlock(handle);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool TrySetClipboardText(string text)
        {
            try
            {
                if (!OpenClipboard(IntPtr.Zero))
                    return false;

                try
                {
                    if (!EmptyClipboard())
                        return false;

                    var bytes = (text.Length + 1) * 2;
                    var hGlobal = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
                    if (hGlobal == IntPtr.Zero)
                        return false;

                    var target = GlobalLock(hGlobal);
                    if (target == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return false;
                    }

                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                        Marshal.WriteInt16(target, text.Length * 2, 0);
                    }
                    finally
                    {
                        GlobalUnlock(hGlobal);
                    }

                    if (SetClipboardData(CfUnicodeText, hGlobal) == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return false;
                    }

                    return true;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch
            {
                return false;
            }
        }

        private static void OpenTextInput(TitleScreen screen, string title, string initial, Action<string> onValidate, bool noSpaces = false)
        {
            try
            {
                ClearActiveTextInput();
                if (noSpaces && initial.Contains(' ', StringComparison.Ordinal))
                    initial = RemoveSpaces(initial);
                var initialText = initial ?? string.Empty;
                var input = new TextInput(
                    screen,
                    MakeHLString(title),
                    MakeHLString(initialText),
                    MakeHLString(initialText),
                    new HlAction<dc.String>(s =>
                    {
                        var text = s?.ToString() ?? string.Empty;
                        if (noSpaces)
                            text = RemoveSpaces(text);
                        try
                        {
                            onValidate(text);
                        }
                        finally
                        {
                            ClearActiveTextInput();
                        }
                    }),
                    MakeHLString(GetText.Instance.GetString("OK")),
                    MakeHLString(GetText.Instance.GetString("Cancel")),
                    (dc.hxd.res.Sound?)null);
                RegisterActiveTextInput(input, noSpaces);
            }
            catch (Exception ex)
            {
                ClearActiveTextInput();
                _log?.Warning("[NetMod] Failed to open text input: {Message}", ex.Message);
            }
        }

        private static void TryAddMenuButton(TitleScreen screen, string label, Action onClick, string? help = null)
        {
            try
            {
                AddMenuButton(screen, label, onClick, help);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Menu add failed for {Label}: {Message}", label, ex.Message);
            }
        }

        private static void AddMenuButton(TitleScreen screen, string label, Action onClick, string? help = null)
        {
            var cb = new HlAction(onClick);
            var labelStr = MakeHLString(label);
            var helpStr = MakeHLString(help ?? string.Empty);
            int colorVal = 0xFFFFFF;
            var color = Ref<int>.From(ref colorVal);
            screen.addMenu(labelStr, cb, helpStr, null, color);
        }

        private static void AddInfoLine(TitleScreen screen, string text, int? infoColor = null)
        {
            int colorVal = infoColor ?? 0xFFFFFF;
            var labelStr = MakeHLString(text);
            var helpStr = MakeHLString(string.Empty);
            var color = Ref<int>.From(ref colorVal);
            var cb = new HlAction(() => { });
            screen.addMenu(labelStr, cb, helpStr, false, color);
        }

        private static object? GetMemberValue(object? obj, string name, bool ignoreCase)
        {
            if (obj == null || string.IsNullOrWhiteSpace(name)) return null;

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = obj.GetType();
            var flags = ignoreCase ? Flags | BindingFlags.IgnoreCase : Flags;
            try
            {
                var prop = type.GetProperty(name, flags);
                if (prop != null) return prop.GetValue(obj);

                var field = type.GetField(name, flags);
                if (field != null) return field.GetValue(obj);
            }
            catch { }

            return null;
        }

        private static bool TrySetMember(object? obj, string name, object? value)
        {
            if (obj == null || string.IsNullOrWhiteSpace(name)) return false;

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var type = obj.GetType();
            try
            {
                var prop = type.GetProperty(name, Flags);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(obj, value);
                    return true;
                }

                var field = type.GetField(name, Flags);
                if (field != null)
                {
                    field.SetValue(obj, value);
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static dc.String MakeHLString(string value)
        {
            return value.AsHaxeString();
        }

        private static bool GetIsMainMenu(TitleScreen screen)
        {
            try
            {
                var val = GetMemberValue(screen, "isMainMenu", true);
                if (val is bool b) return b;
            }
            catch { }
            return false;
        }

        private static void SetIsMainMenu(TitleScreen screen, bool value)
        {
            try
            {
                TrySetMember(screen, "isMainMenu", value);
            }
            catch { }
        }

        private static int GetArrayLength(object arrObj)
        {
            try
            {
                var lenObj = GetMemberValue(arrObj, "length", true);
                if (lenObj is IConvertible conv)
                    return conv.ToInt32(null);
            }
            catch { }
            return 0;
        }

        private static int FindMenuIndexByLabel(object? arrObj, string label)
        {
            if (arrObj == null) return -1;
            try
            {
                var type = arrObj.GetType();
                var getDyn = type.GetMethod("getDyn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (getDyn == null) return -1;

                int len = GetArrayLength(arrObj);
                for (int i = 0; i < len; i++)
                {
                    var item = getDyn.Invoke(arrObj, new object[] { i });
                    var text = GetMenuLabel(item);
                    if (text.Equals(label, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            catch { }
            return -1;
        }

        private static string GetMenuLabel(object? menuItem)
        {
            if (menuItem == null) return string.Empty;

            try
            {
                var t = GetMemberValue(menuItem, "t", true);
                if (t is dc.String ds)
                    return ds.ToString() ?? string.Empty;

                var textValue = GetMemberValue(t ?? menuItem, "text", true)
                             ?? GetMemberValue(t ?? menuItem, "str", true);
                if (textValue != null)
                    return textValue.ToString() ?? string.Empty;

                return t?.ToString() ?? menuItem.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void RemoveMenuItems(TitleScreen screen, params string[] labels)
        {
            if (labels.Length == 0) return;
            var arrObj = GetMemberValue(screen, "menuItems", true);
            if (arrObj == null) return;

            try
            {
                var type = arrObj.GetType();
                var getDyn = type.GetMethod("getDyn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var removeDyn = type.GetMethod("removeDyn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              ?? type.GetMethod("remove", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (getDyn == null || removeDyn == null) return;

                var targets = new System.Collections.Generic.List<object>();
                int len = GetArrayLength(arrObj);
                for (int i = 0; i < len; i++)
                {
                    var item = getDyn.Invoke(arrObj, new object[] { i });
                    if (item == null)
                        continue;
                    var label = GetMenuLabel(item);
                    foreach (var l in labels)
                    {
                        if (label.Equals(l, StringComparison.OrdinalIgnoreCase))
                        {
                            targets.Add(item);
                            break;
                        }
                    }
                }

                foreach (var it in targets)
                {
                    removeDyn.Invoke(arrObj, new object[] { it });
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to clean menu items: {Message}", ex.Message);
            }
        }

        private static void RemoveDuplicatesKeepFirst(TitleScreen screen, params string[] labels)
        {
            if (labels.Length == 0) return;
            var arrObj = GetMemberValue(screen, "menuItems", true);
            if (arrObj == null) return;

            try
            {
                var type = arrObj.GetType();
                var getDyn = type.GetMethod("getDyn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var removeDyn = type.GetMethod("removeDyn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              ?? type.GetMethod("remove", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (getDyn == null || removeDyn == null) return;

                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var toRemove = new System.Collections.Generic.List<object>();

                int len = GetArrayLength(arrObj);
                for (int i = 0; i < len; i++)
                {
                    var item = getDyn.Invoke(arrObj, new object[] { i });
                    if (item == null)
                        continue;
                    var label = GetMenuLabel(item);
                    foreach (var l in labels)
                    {
                        if (label.Equals(l, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!seen.Add(label))
                                toRemove.Add(item);
                            break;
                        }
                    }
                }

                foreach (var it in toRemove)
                {
                    removeDyn.Invoke(arrObj, new object[] { it });
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to clean duplicate menu items: {Message}", ex.Message);
            }
        }


        private static void StoreTitleScreen(TitleScreen ts)
        {
            _titleScreenRef = new WeakReference<TitleScreen?>(ts);
        }

        private static TitleScreen? GetTitleScreen()
        {
            if (_titleScreenRef != null && _titleScreenRef.TryGetTarget(out var ts))
                return ts;
            return null;
        }
    }
}
