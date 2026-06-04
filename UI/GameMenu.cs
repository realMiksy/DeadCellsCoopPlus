using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using dc.pr;
using dc.ui;
using Newtonsoft.Json;
using Serilog;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using DeadCellsMultiplayerMod.Tools;
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
        private readonly struct MainThreadWorkItem
        {
            public readonly Action? Action;
            public readonly string? CoalesceKey;

            public MainThreadWorkItem(Action action)
            {
                Action = action;
                CoalesceKey = null;
            }

            public MainThreadWorkItem(string coalesceKey)
            {
                Action = null;
                CoalesceKey = coalesceKey;
            }
        }

        private static readonly ConcurrentQueue<MainThreadWorkItem> _mainThreadQueue = new();
        private static readonly ConcurrentDictionary<MethodInfo, string> _mainThreadActionLabelCache = new();
        private static readonly object MainThreadCoalesceSync = new();
        private static readonly Dictionary<string, Action> _coalescedMainThreadActions = new(StringComparer.Ordinal);
        private static readonly HashSet<string> _pendingCoalescedMainThreadKeys = new(StringComparer.Ordinal);
        private static int _mainThreadQueueDepth;
        private const int MainThreadQueueMaxActionsPerPump = 64;
        private const double MainThreadQueueBudgetMs = 4.0;

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
        private static bool _steamJoinLobbyResolvePending;
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
        // While a client full-run restart (from host seed) is pending, the host's freshly broadcast level
        // graph for the restart level must NOT trigger an in-place reloadAfterBossRuneModif on the client:
        // that collides with the queued launchGame restart and leaves the old downed hero / Game Over stuck.
        private static long _clientRestartPendingUntilTicks;
        private const int ClientRestartPendingTtlMs = 12000;
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

        /// <summary>True while clipboard/overlay join is resolving the Steam lobby (before <see cref="ApplySteamJoinResult"/>).</summary>
        internal static bool IsSteamJoinLobbyResolvePending() => _steamJoinLobbyResolvePending;
        private static bool _localReady;
        private static List<PlayerInfo> _playersDisplay = new();
        private static bool _inHostStatusMenu;
        private static bool _inClientWaitingMenu;
        /// <summary>Prevents nested host/client status menu rebuilds when addMenu hook runs ProcessMainThreadQueue before orig.</summary>
        private static int _menuRebuildDepth;
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
                _mainThreadQueueDepth = _mainThreadQueue.Count;
                lock (MainThreadCoalesceSync)
                {
                    _coalescedMainThreadActions.Clear();
                    _pendingCoalescedMainThreadKeys.Clear();
                }
            }

            InitializeMenuUiHooks();
        }

        internal static void EnqueueMainThread(Action action)
        {
            if (action == null) return;
            _mainThreadQueue.Enqueue(new MainThreadWorkItem(action));
            Interlocked.Increment(ref _mainThreadQueueDepth);
        }

        internal static void EnqueueMainThreadCoalesced(string coalesceKey, Action action)
        {
            if (action == null)
                return;

            if (string.IsNullOrWhiteSpace(coalesceKey))
            {
                EnqueueMainThread(action);
                return;
            }

            var shouldEnqueue = false;
            lock (MainThreadCoalesceSync)
            {
                _coalescedMainThreadActions[coalesceKey] = action;
                if (_pendingCoalescedMainThreadKeys.Add(coalesceKey))
                    shouldEnqueue = true;
            }

            if (!shouldEnqueue)
                return;

            _mainThreadQueue.Enqueue(new MainThreadWorkItem(coalesceKey));
            Interlocked.Increment(ref _mainThreadQueueDepth);
        }

        internal static void ProcessMainThreadQueue()
        {
            var hitchStart = RuntimeHitchWatch.Start();
            var perfEnabled = RuntimeHitchWatch.Enabled;
            var startDepth = Volatile.Read(ref _mainThreadQueueDepth);
            var processed = 0;
            var slowActions = 0;
            var maxActionMs = 0.0;
            var maxActionLabel = string.Empty;
            var actionsStart = RuntimeHitchWatch.Start();
            while (_mainThreadQueue.TryDequeue(out var workItem))
            {
                Interlocked.Decrement(ref _mainThreadQueueDepth);
                Action? action = workItem.Action;
                var actionLabel = workItem.CoalesceKey;
                if (actionLabel != null)
                {
                    lock (MainThreadCoalesceSync)
                    {
                        _pendingCoalescedMainThreadKeys.Remove(actionLabel);
                        _coalescedMainThreadActions.TryGetValue(actionLabel, out action);
                        _coalescedMainThreadActions.Remove(actionLabel);
                    }
                }

                if (action == null)
                    continue;

                processed++;
                var actionStart = perfEnabled ? RuntimeHitchWatch.Start() : 0;
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Main thread task failed: {Message}", ex.Message);
                }
                finally
                {
                    if (perfEnabled)
                    {
                        var actionMs = RuntimeHitchWatch.GetElapsedMilliseconds(actionStart);
                        if (actionMs > maxActionMs)
                        {
                            actionLabel ??= DescribeMainThreadAction(action);
                            maxActionMs = actionMs;
                            maxActionLabel = actionLabel;
                        }

                        if (actionMs >= RuntimeHitchWatch.MainThreadQueueActionSlowThresholdMs)
                        {
                            slowActions++;
                            actionLabel ??= DescribeMainThreadAction(action);
                            RuntimeHitchWatch.LogSlow(
                                _log,
                                $"GameMenu.MainThreadQueueAction:{actionLabel}",
                                actionMs,
                                string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"action={actionLabel} processed={processed} startDepth={startDepth}"));
                        }
                    }
                }

                if (processed >= MainThreadQueueMaxActionsPerPump)
                    break;
                if (RuntimeHitchWatch.GetElapsedMilliseconds(actionsStart) >= MainThreadQueueBudgetMs)
                    break;
            }
            var actionsMs = RuntimeHitchWatch.GetElapsedMilliseconds(actionsStart);

            var remainingDepth = Volatile.Read(ref _mainThreadQueueDepth);
            var observedDepth = System.Math.Max(startDepth, remainingDepth);
            if (perfEnabled && observedDepth >= RuntimeHitchWatch.MainThreadQueueDepthThreshold)
            {
                RuntimeHitchWatch.LogCount(
                    _log,
                    "GameMenu.MainThreadQueueDepth",
                    observedDepth,
                    RuntimeHitchWatch.MainThreadQueueDepthThreshold,
                    string.Create(CultureInfo.InvariantCulture, $"processed={processed} remaining={remainingDepth}"));
            }

            if (actionsMs >= RuntimeHitchWatch.MainThreadQueueActionsSlowThresholdMs)
            {
                RuntimeHitchWatch.LogSlow(
                    _log,
                    "GameMenu.ExecuteMainThreadActions",
                    actionsMs,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"processed={processed} slowActions={slowActions} maxAction={maxActionLabel} maxMs={maxActionMs:0.00} remaining={remainingDepth}"));
            }

            var hitchMs = RuntimeHitchWatch.GetElapsedMilliseconds(hitchStart);
            if (hitchMs >= RuntimeHitchWatch.MainThreadQueueSlowThresholdMs)
            {
                RuntimeHitchWatch.LogSlow(
                    _log,
                    "GameMenu.ProcessMainThreadQueue",
                    hitchMs,
                    string.Create(CultureInfo.InvariantCulture, $"processed={processed} startDepth={startDepth} remaining={remainingDepth}"));
            }
        }

        private static string DescribeMainThreadAction(Action? action)
        {
            if (action == null)
                return "null";

            var method = action.Method;
            return _mainThreadActionLabelCache.GetOrAdd(method, static m =>
            {
                var declaringType = m.DeclaringType?.FullName;
                if (!string.IsNullOrWhiteSpace(declaringType))
                    return $"{declaringType}.{m.Name}";

                return m.Name;
            });
        }

        public static void MarkInRun()
        {
            lock (Sync)
            {
                _inActualRun = true;
            }
            // The (re)started run's hero is up — the restart completed, so stop suppressing level reloads.
            ClearClientRestartPending();
        }

        internal static void MarkClientRestartPending()
        {
            Volatile.Write(ref _clientRestartPendingUntilTicks, Environment.TickCount64 + ClientRestartPendingTtlMs);
        }

        internal static void ClearClientRestartPending()
        {
            Volatile.Write(ref _clientRestartPendingUntilTicks, 0);
        }

        internal static bool IsClientRestartPending()
        {
            var until = Volatile.Read(ref _clientRestartPendingUntilTicks);
            return until != 0 && Environment.TickCount64 < until;
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
            // Set synchronously (before the queued action runs) so any level graph that arrives in the
            // meantime is prevented from firing an in-place reload that would pre-empt this full restart.
            MarkClientRestartPending();
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
            ApplySteamPersonaUsername();

            _steamJoinLobbyResolvePending = true;
            PrepareSteamJoinConnectionUiOnly(screen);
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
            ApplySteamPersonaUsername();

            _steamJoinLobbyResolvePending = true;
            PrepareSteamJoinConnectionUiOnly(screen);
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

        /// <summary>Clears title menu and shows ConnectionUI while the Steam lobby is resolved off-thread.</summary>
        private static void PrepareSteamJoinConnectionUiOnly(TitleScreen screen)
        {
            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();
                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                _inClientWaitingMenu = false;
                _inHostStatusMenu = false;
                screen.ShouldAutoHideConnectionUI(true);
                ConnectionUI.NotifyConnectionsChanged();
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to prepare Steam join UI: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        private static void ApplySteamJoinResult(TitleScreen screen, bool ok, SteamConnect.JoinLobbyResult join, bool fromOverlay)
        {
            _steamJoinLobbyResolvePending = false;

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
            try
            {
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
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Host start failed: {Message}", ex.Message);
            }
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
    }
}
