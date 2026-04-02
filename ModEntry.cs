
using ModCore.Events.Interfaces.Game;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Mods;
using System.Diagnostics;
using System.Net;
using System.Threading;
using dc.en;
using dc.en.inter;
using dc.pr;
using ModCore.Utilities;
using dc.level;
using dc.hl.types;
using dc;
using dc.libs.heaps.slib;
using Rand = dc.libs.Rand;
using dc.ui.hud;
using dc.h2d;
using Hashlink.Virtuals;
using dc.tool;
using dc.tool.mainSkills;
using HaxeProxy.Runtime;
using dc.cine;
using dc.cine.coll;
using dc.cine.dlcp;
using dc.cine.kf;
using dc.cine.queen;
using CineHookInitialize;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using ModCore.Events;
using DeadCellsMultiplayerMod.Mobs.MobsSynchronization;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using DeadCellsMultiplayerMod.MultiplayerModUI.Minimap;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using DeadCellsMultiplayerMod.MultiplayerModUI.LevelExit;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using DeadCellsMultiplayerMod.Tools.ModLang;
using DeadCellsMultiplayerMod.KingHead;
using DeadCellsMultiplayerMod.Mobs.Levelinit;
using dc.en.inter.door;
using dc.en.mob.boss;
using Steamworks;
using System.Reflection;
using DeadCellsMultiplayerMod.Interaction;
using DeadCellsMultiplayerMod.UI;


namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry(ModInfo info) : ModBase(info),
        IOnGameEndInit,
        IOnHeroInit,
        IOnHeroUpdate,
        IOnFrameUpdate,
        IOnAfterLoadingCDB,
        IOnAdvancedModuleInitializing
    {
        public static ModEntry? Instance { get; private set; }
        private bool _ready;
        private static bool s_hooksInstalled;
        private static IDisposable? s_steamOverlayJoinCallback;
        private static IDisposable? s_steamRichPresenceJoinCallback;
        private static bool s_steamOverlayCallbackPending;
        private static Timer? s_steamCallbackPumpTimer;
        private static int s_steamOverlayCallbackRetryCount;
        private static bool s_steamApiReady;
        private static string s_lastSteamLaunchCommand = string.Empty;
        private static string s_lastSteamLaunchConnectLobbyParam = string.Empty;
        private static ulong s_lastOverlayJoinLobbyId;
        private static long s_lastOverlayJoinTicks;
        private static long s_nextSteamLaunchPollTicks;
        private const int SteamOverlayCallbackMaxRetries = 600;
        private const int SteamOverlayLaunchPollIntervalMs = 500;
        private const int SteamOverlayJoinDedupMs = 3000;
        private NetRole _netRole = NetRole.None;
        public static NetNode? _net;

        public dc.pr.Game? game;

        public static GhostKing[] clients = new GhostKing[NetNode.MaxClientSlots];
        public static Kinghead?[] clientHeads = new Kinghead?[NetNode.MaxClientSlots];
        public static string?[] clientLabels = new string?[NetNode.MaxClientSlots];
        public static int[] clientIds = new int[NetNode.MaxClientSlots];
        public static string?[] clientSkins = new string?[NetNode.MaxClientSlots];
        public static string?[] clientHeadSkins = new string?[NetNode.MaxClientSlots];
        private static bool[] pendingClientHeadRecreate = new bool[NetNode.MaxClientSlots];
        public static Hero me = null!;
        public static GhostHero _ghost = null!;

        private GameDataSync? gds;
        private Hero? _debugPerkAppliedHero;
        private string _debugPerkAppliedId = string.Empty;
        private string _lastDebugPerkApplyErrorId = string.Empty;
        private long _nextDebugPerkApplyTick;
        private ItemMetaManager? _debugExplorerRuneInjectedMeta;
        private bool _debugExplorerRuneInjectedByDebug;
        private const string ExplorerRunePermanentItemId = "ExploKey";
        /// <summary>Last successful minimap reveal signature (level + branch); cleared on level/wakeup.</summary>
        private string _debugExplorerRevealAppliedSignature = string.Empty;
        private long _nextDebugExplorerRevealRetryTick;
        private int _debugExplorerRevealAllCount;
        private const int MaxDebugExplorerRevealAllCalls = 3;

        private string? _lastAnimSent;
        private int? _lastAnimQueueSent;
        private bool? _lastAnimGSent;
        private long _suppressHeroAnimUntilTicks;
        private string? _lastSentHeroSkin;
        private string? _lastSentHeroHeadSkin;

        public static MiniMap miniMap = null!;

        public static bool kingInitialized = false;

        public string levelId = string.Empty;

        public static int remotePlayerId = -1;

        public string remoteSkin = string.Empty;
        public string remoteHeadSkin = string.Empty;

        public string lastHeadAnim = string.Empty;
        public static ArrayDyn customHeads = null!;

        public InventItem inventItem = null!;
        private bool _inventorySyncGuard;
        private bool _localFakeDead;
        private bool _localExitPenaltyApplied;
        private long _localFakeDeadStartedTicks;
        private DeadBase? _localDeadCine;
        private double _localDownedX;
        private double _localDownedY;
        private double _localHeldX;
        private double _localHeldY;
        private string _localDownedLevelId = string.Empty;
        private long _nextReviveAttemptTicks;
        private long _nextDownedStateSendTicks;
        private long _postReviveLockUntilTicks;
        private double _postReviveLockX;
        private double _postReviveLockY;
        private int _reviveHoldTargetId;
        private long _reviveHoldStartedTicks;
        private const double ReviveUseDistancePx = 48.0;
        private const int ReviveInteractKey = 82; // R
        private const double ReviveAttemptCooldownSeconds = 0.2;
        private const double ReviveHoldSeconds = 0.7;
        private const double ReviveHomunculusBodyMaxDistancePx = 64.0;
        private const double DownedStateResendSeconds = 0.4;
        private const double DownedHeadStateResendSeconds = 1.0 / 30.0;
        private const double DownedGhostBodyYOffsetPx = 40.0;
        private const double LocalReviveBodyYOffsetPx = 0.5;
        private const double PostRevivePositionLockSeconds = 0.0;
        private const string ReviveHintText = "Hold to revive.";
        private string _lastDoorMarkerLevelId = string.Empty;
        private int _lastDoorMarkerToken = int.MinValue;
        private string _localLastDoorMarkerLevelId = string.Empty;
        private int _localLastDoorMarkerToken = int.MinValue;

        private sealed class RemoteDownedState
        {
            public int UserId;
            public double X;
            public double Y;
            public bool HasHeadPosition;
            public double HeadX;
            public double HeadY;
            public bool HasHeadAnim;
            public string HeadAnim = string.Empty;
            public string LevelId = string.Empty;
            public long UpdatedAtTicks;
        }

        private sealed class RemoteDoorMarkerState
        {
            public int MarkerToken;
            public string LevelId = string.Empty;
            public long UpdatedAtTicks;
        }

        private readonly Dictionary<int, RemoteDownedState> _remoteDowned = new();
        private readonly Dictionary<int, RemoteDownedCorpse> _remoteDownedCines = new();
        private readonly HashSet<int> _downedAnnouncements = new();
        private readonly Dictionary<int, RemoteDoorMarkerState> _remoteLastDoorMarkers = new();
        private readonly Dictionary<int, RemoteDoorMarkerState> _remotePendingDoorMarkers = new();
        private readonly Dictionary<int, long> _pendingClientDisposeTicks = new();
        private const double ClientDisposeTransitionSeconds = 0.28;
        private const double PendingDoorMarkerHideMaxSeconds = 1.5;

        private static readonly HashSet<string> BossLevelIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "BeholderPit", "Throne", "DeathArena", "DookuArena", "QueenArena", "Observatory",
            "SwampHeart", "CastleAlchemy", "LighthouseTop", "LighthouseBottom", "Giant",
            "DookuCastle", "RichterCastle", "BossRushZone", "Bridge", "TopClockTower"
        };

        internal static bool IsBossLevel(string? levelId)
        {
            return !string.IsNullOrWhiteSpace(levelId) && BossLevelIds.Contains(levelId);
        }

        /// <summary>Known boss-room genericEventIds from game's HiddenTrigger (set via marker.customId in level data).</summary>
        private static readonly HashSet<string> BossRoomGenericEventIds = new(StringComparer.Ordinal)
        {
            "roomDeath", "roomBeholder", "roomBerserk", "roomCollectorBoss", "roomDooku",
            "roomGardenerBoss", "roomGiant", "roomKingsHand", "roomQueen", "roomBehemoth",
            "roomMamaTick", "roomKingsHandAsKing"
        };
        private static readonly HashSet<string> BossDeathCineTypeNames = new(StringComparer.Ordinal)
        {
            "BeholderDeath", "GiantDeath", "GiantDeath4", "KillKingCinem", "KillQueenCinem",
            "QueenDefeated", "KillDookuBeastCinem", "FakeKillDooku", "RichterDeath",
            "EndCollectorPreSmash", "SmashCinem", "EndCollectorPostSmash", "EndCollectorPostSmashKS"
        };
        private static readonly HashSet<string> BossIntroCineTypeNames = new(StringComparer.Ordinal)
        {
            "EnterRoomBoss", "EnterRoomDeathBoss", "EnterRoomGardenerBoss", "EnterRoomQueenBoss",
            "EnterThroneRoom", "EnterThroneBossRush", "EnterThroneRoomAsKing", "EnterGiantRoom",
            "EnterModifiedGiantRoom", "EnterDualBehemoth", "StartCollectorFight", "StartCollectorFightAlt",
            "MeetCollectorEnd"
        };
        private string? _lastBossCineSentLevelId;
        private long _lastBossCineSentTick;
        private const double BossCineSendCooldownSeconds = 2.0;
        private readonly Dictionary<string, long> _pendingBossCineApplyByLevel = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _suppressBossCineEchoByLevel = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _completedBossCineLevels = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _appliedBossHeroTeleportLevels = new(StringComparer.OrdinalIgnoreCase);
        private const double BossCineApplyPendingTtlSeconds = 20.0;
        private const double BossCineEchoSuppressSeconds = 12.0;
        private const double BossHeroTeleportYOffsetPx = 20.0;
        private const double BossHeroTeleportEchoSuppressSeconds = 1.5;
        private int _suppressBossCineSendDepth;
        private long _suppressBossTriggerNetSendUntilTick;


        void IOnAfterLoadingCDB.OnAfterLoadingCDB(dc._Data_ cdb)
        {
            customHeads = cdb.customHead.all;
        }


        internal static void SetRemoteSkin(string? skin)
        {
            var instance = Instance;
            if (instance == null)
                return;

            instance.remoteSkin = string.IsNullOrWhiteSpace(skin)
                ? "PrisonerDefault"
                : skin.Replace("|", "/").Trim();
        }

        internal static void SetRemoteHeadSkin(string? skin)
        {
            var instance = Instance;
            if (instance == null)
                return;

            instance.remoteHeadSkin = NormalizeSkin(skin, "BaseFlame");
        }

        public static string GetClientLabel(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= clientLabels.Length)
                return string.Empty;

            return clientLabels[slotIndex] ?? string.Empty;
        }

        /// <summary>Assigned id for the listen-server host (<see cref="NetNode"/>).</summary>
        internal const int MultiplayerHostAssignedId = 1;

        internal static bool IsLocalPlayerDowned()
        {
            return Instance != null && Instance._localFakeDead;
        }

        /// <summary>
        /// True when the session host is fake-dead (on host) or their down state was received (on client).
        /// </summary>
        internal static bool IsSessionHostDowned(NetNode? net)
        {
            if (net == null || !net.IsAlive)
                return false;
            if (net.IsHost)
                return IsLocalPlayerDowned();
            return IsRemotePlayerDowned(MultiplayerHostAssignedId);
        }

        internal static void ApplyLocalDownedExitPenaltyIfNeeded()
        {
            Instance?.ApplyLocalDownedExitPenaltyIfNeededCore();
        }

        internal static bool IsRemotePlayerDowned(int userId)
        {
            var instance = Instance;
            if (instance == null || userId <= 0)
                return false;

            if (!instance._remoteDowned.TryGetValue(userId, out var state) || state == null)
                return false;

            var localLevelId = instance.GetCurrentLevelId();
            if (!string.IsNullOrEmpty(localLevelId) &&
                !string.IsNullOrEmpty(state.LevelId) &&
                !string.Equals(localLevelId, state.LevelId, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        internal static bool IsEntityDownedForCombat(Entity? entity)
        {
            if (entity == null)
                return false;

            var localHero = me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (localHero != null && ReferenceEquals(entity, localHero))
                return IsLocalPlayerDowned();

            var net = _net;
            var localId = net?.id ?? 0;
            for (int i = 0; i < clients.Length; i++)
            {
                var client = clients[i];
                if (client == null || !ReferenceEquals(entity, client))
                    continue;

                var remoteId = clientIds[i];
                if (remoteId <= 0)
                    return false;
                if (localId > 0 && remoteId == localId)
                    return IsLocalPlayerDowned();
                return IsRemotePlayerDowned(remoteId);
            }

            return false;
        }

        internal static GhostKing? GetPrimaryClient()
        {
            for (int i = 0; i < clients.Length; i++)
            {
                var client = clients[i];
                if (client != null && clientIds[i] > 0)
                    return client;
            }

            return clients.Length > 0 ? clients[0] : null;
        }

        internal static void ResetClientSlots()
        {
            for (int i = 0; i < clients.Length; i++)
            {
                var head = clientHeads[i];
                if (head != null)
                {
                    head.dispose();
                    clientHeads[i] = null;
                }
                clients[i] = null!;
                clientLabels[i] = null;
                clientIds[i] = 0;
                clientSkins[i] = null;
                clientHeadSkins[i] = null;
                pendingClientHeadRecreate[i] = false;
                rLastX[i] = 0;
                rLastY[i] = 0;
            }
        }

        private static string BuildRemoteLabel(int remoteId, string? username)
        {
            var clean = string.IsNullOrWhiteSpace(username) ? "Guest" : username.Trim();
            if (remoteId > 0)
                return $"{clean}";
            return clean;
        }


        public void OnGameEndInit()
        {
            _ready = true;
            GameMenu.SetRole(NetRole.None);
            s_steamOverlayCallbackPending = true;
            s_steamOverlayCallbackRetryCount = 0;
            _debugPerkAppliedHero = null;
            _debugPerkAppliedId = string.Empty;
            _lastDebugPerkApplyErrorId = string.Empty;
            _nextDebugPerkApplyTick = 0;
            _debugExplorerRuneInjectedMeta = null;
            _debugExplorerRuneInjectedByDebug = false;
            _debugExplorerRevealAppliedSignature = string.Empty;
            _nextDebugExplorerRevealRetryTick = 0;
            _debugExplorerRevealAllCount = 0;
            TryEnsureSteamApiInitialized("OnGameEndInit", logFailure: true);
            TryParseConnectLobbyFromCommandLine();
        }

        private static void TryParseConnectLobbyFromCommandLine()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                for (var i = 0; i < args.Length - 1; i++)
                {
                    if (string.Equals(args[i], "+connect_lobby", StringComparison.OrdinalIgnoreCase) &&
                        ulong.TryParse(args[i + 1], out var lobbyId) && lobbyId > 0)
                    {
                        Instance?.Logger.Information("[NetMod][Steam] Launch parameter +connect_lobby detected lobbyId={LobbyId}", lobbyId);
                        GameMenu.EnqueueMainThread(() => GameMenu.HandleSteamOverlayJoinRequest(lobbyId));
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Instance?.Logger.Debug(ex, "[NetMod] Failed to parse +connect_lobby from command line");
            }
        }

        private static void TryDeferredSteamOverlayCallbackRegistration()
        {
            if (!s_steamOverlayCallbackPending || (s_steamOverlayJoinCallback != null && s_steamRichPresenceJoinCallback != null))
                return;
            if (s_steamOverlayCallbackRetryCount >= SteamOverlayCallbackMaxRetries)
            {
                s_steamOverlayCallbackPending = false;
                Instance?.Logger.Warning("[NetMod] Steam overlay join callback registration gave up after {Count} retries", SteamOverlayCallbackMaxRetries);
                return;
            }
            s_steamOverlayCallbackRetryCount++;
            try
            {
                var shouldLogFailure = s_steamOverlayCallbackRetryCount == 1 || s_steamOverlayCallbackRetryCount % 60 == 0;
                if (!TryEnsureSteamApiInitialized($"callback registration attempt {s_steamOverlayCallbackRetryCount}", shouldLogFailure))
                {
                    if (shouldLogFailure)
                        Instance?.Logger.Debug("[NetMod] Steam overlay: SteamAPI.Init()=false (attempt {Attempt}). Trying callback without Init (game may have Steam).", s_steamOverlayCallbackRetryCount);
                    try
                    {
                        s_steamOverlayJoinCallback = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
                        s_steamRichPresenceJoinCallback = Callback<GameRichPresenceJoinRequested_t>.Create(OnGameRichPresenceJoinRequested);
                        s_steamOverlayCallbackPending = false;
                        StartSteamCallbackPumpTimer();
                        Instance?.Logger.Information("[NetMod] Steam overlay join callbacks registered (game had Steam initialized)");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Instance?.Logger.Warning(ex, "[NetMod] Steam overlay: callback registration failed (Init was false): {Message}", ex.Message);
                        WriteOverlayCallbackFailedDiagnostics(ex);
                        return;
                    }
                }
                s_steamOverlayJoinCallback = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
                s_steamRichPresenceJoinCallback = Callback<GameRichPresenceJoinRequested_t>.Create(OnGameRichPresenceJoinRequested);
                s_steamOverlayCallbackPending = false;
                StartSteamCallbackPumpTimer();
                Instance?.Logger.Information("[NetMod] Steam overlay join callbacks registered (attempt {Attempt})", s_steamOverlayCallbackRetryCount);
            }
            catch (Exception ex)
            {
                if (s_steamOverlayCallbackRetryCount == 1 || s_steamOverlayCallbackRetryCount % 60 == 0)
                {
                    Instance?.Logger.Warning(ex, "[NetMod] Steam overlay callback registration attempt {Attempt} failed: {Message}", s_steamOverlayCallbackRetryCount, ex.Message);
                    WriteOverlayCallbackFailedDiagnostics(ex);
                }
            }
        }

        private static void WriteOverlayJoinDiagnostic(string callbackType, string data)
        {
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dccm_overlay_join_fired.txt");
                System.IO.File.WriteAllText(path, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z | {callbackType} | {data}");
            }
            catch { }
        }

        private static void WriteOverlayCallbackFailedDiagnostics(Exception ex)
        {
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dccm_overlay_callback_failed.txt");
                var lines = new[]
                {
                    $"DCCM Steam overlay callback registration failed - {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z",
                    $"Message: {ex.Message}",
                    $"Type: {ex.GetType().FullName}",
                    ex.StackTrace ?? "(no stack trace)"
                };
                System.IO.File.WriteAllLines(path, lines);
            }
            catch
            {
                // Best-effort diagnostics
            }
        }

        private static void TryRegisterSteamOverlayJoinCallback()
        {
            if (s_steamOverlayJoinCallback != null)
                return;
            try
            {
                s_steamOverlayJoinCallback = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
            }
            catch (Exception ex)
            {
                Instance?.Logger.Warning("[NetMod] Steam overlay join callback not registered: {Error}", ex.Message);
            }
        }

        private static void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t data)
        {
            WriteOverlayJoinDiagnostic("GameLobbyJoinRequested_t", data.m_steamIDLobby.m_SteamID.ToString());
            Instance?.Logger.Information("[NetMod][Steam] GameLobbyJoinRequested_t callback fired");
            var lobbyId = data.m_steamIDLobby.m_SteamID;
            if (lobbyId == 0UL)
                return;
            Instance?.Logger.Information("[NetMod][Steam] Overlay lobby join requested lobbyId={LobbyId}", lobbyId);
            EnqueueAndProcessOverlayJoin(lobbyId, "GameLobbyJoinRequested_t");
        }

        private static void OnGameRichPresenceJoinRequested(GameRichPresenceJoinRequested_t data)
        {
            var connect = data.m_rgchConnect ?? string.Empty;
            WriteOverlayJoinDiagnostic("GameRichPresenceJoinRequested_t", connect);
            Instance?.Logger.Information("[NetMod][Steam] GameRichPresenceJoinRequested_t callback fired");
            if (string.IsNullOrWhiteSpace(connect))
            {
                Instance?.Logger.Information("[NetMod][Steam] Rich Presence join requested but connect string is empty (host may not have set Rich Presence)");
                return;
            }
            Instance?.Logger.Information("[NetMod][Steam] Overlay Rich Presence join requested connect={Connect}", connect);
            var lobbyId = TryParseLobbyIdFromConnectString(connect);
            if (lobbyId == 0UL)
            {
                Instance?.Logger.Warning("[NetMod][Steam] Could not parse lobby ID from connect string: {Connect}", connect);
                return;
            }
            EnqueueAndProcessOverlayJoin(lobbyId, "GameRichPresenceJoinRequested_t");
        }

        private static void EnqueueAndProcessOverlayJoin(ulong lobbyId, string source)
        {
            var nowTicks = Environment.TickCount64;
            if (lobbyId == s_lastOverlayJoinLobbyId &&
                nowTicks - s_lastOverlayJoinTicks < SteamOverlayJoinDedupMs)
            {
                Instance?.Logger.Debug("[NetMod][Steam] Ignoring duplicate overlay join request lobbyId={LobbyId} source={Source}", lobbyId, source);
                return;
            }

            s_lastOverlayJoinLobbyId = lobbyId;
            s_lastOverlayJoinTicks = nowTicks;
            Instance?.Logger.Information("[NetMod][Steam] Queueing overlay join request lobbyId={LobbyId} source={Source}", lobbyId, source);
            GameMenu.EnqueueMainThread(() => GameMenu.HandleSteamOverlayJoinRequest(lobbyId));
        }

        private static ulong TryParseLobbyIdFromConnectString(string connect)
        {
            if (string.IsNullOrWhiteSpace(connect))
                return 0UL;
            var parts = connect.Split((char[]?)[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (string.Equals(parts[i], "+connect_lobby", StringComparison.OrdinalIgnoreCase) &&
                    ulong.TryParse(parts[i + 1], out var lobbyId) && lobbyId > 0)
                    return lobbyId;
            }
            if (ulong.TryParse(connect.Trim(), out var direct) && direct > 0)
                return direct;
            return 0UL;
        }

        private static void TryRunSteamCallbacks()
        {
            try
            {
                SteamAPI.RunCallbacks();
            }
            catch
            {
            }
        }

        /// <summary>
        /// Call from GameMenu when at main menu so Steam overlay join callbacks are pumped even if frame update is throttled.
        /// </summary>
        internal static void PumpSteamCallbacksForOverlay()
        {
            TryRunSteamCallbacks();
            TryDeferredSteamOverlayCallbackRegistration();
            TryPollSteamOverlayJoinFromLaunchData();
        }

        private static bool TryEnsureSteamApiInitialized(string source, bool logFailure)
        {
            if (s_steamApiReady)
                return true;

            try
            {
                SteamConnect.PrepareSteamNativePathForRuntime();
                if (SteamAPI.Init())
                {
                    s_steamApiReady = true;
                    Instance?.Logger.Information("[NetMod][Steam] SteamAPI.Init succeeded ({Source})", source);
                    return true;
                }

                if (logFailure)
                    Instance?.Logger.Debug("[NetMod][Steam] SteamAPI.Init returned false ({Source})", source);
            }
            catch (Exception ex)
            {
                if (logFailure)
                    Instance?.Logger.Warning(ex, "[NetMod][Steam] SteamAPI.Init failed ({Source}): {Message}", source, ex.Message);
            }

            return false;
        }

        private static void TryPollSteamOverlayJoinFromLaunchData()
        {
            if (!s_steamApiReady)
                return;

            var nowTicks = Environment.TickCount64;
            if (nowTicks < s_nextSteamLaunchPollTicks)
                return;

            s_nextSteamLaunchPollTicks = nowTicks + SteamOverlayLaunchPollIntervalMs;

            try
            {
                string steamLaunchCommand = string.Empty;
                var launchCommandLength = SteamApps.GetLaunchCommandLine(out steamLaunchCommand, 2048);
                steamLaunchCommand = (steamLaunchCommand ?? string.Empty).Trim();
                if (launchCommandLength > 0 &&
                    !string.IsNullOrWhiteSpace(steamLaunchCommand) &&
                    !string.Equals(steamLaunchCommand, s_lastSteamLaunchCommand, StringComparison.Ordinal))
                {
                    s_lastSteamLaunchCommand = steamLaunchCommand;
                    var lobbyId = TryParseLobbyIdFromConnectString(steamLaunchCommand);
                    if (lobbyId > 0UL)
                    {
                        Instance?.Logger.Information("[NetMod][Steam] Detected overlay join from Steam launch command: {Command}", steamLaunchCommand);
                        EnqueueAndProcessOverlayJoin(lobbyId, "SteamApps.GetLaunchCommandLine");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Instance?.Logger.Debug(ex, "[NetMod][Steam] GetLaunchCommandLine poll failed");
            }

            try
            {
                var connectLobby = (SteamApps.GetLaunchQueryParam("connect_lobby") ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(connectLobby) ||
                    string.Equals(connectLobby, s_lastSteamLaunchConnectLobbyParam, StringComparison.Ordinal))
                    return;

                s_lastSteamLaunchConnectLobbyParam = connectLobby;
                if (ulong.TryParse(connectLobby, out var lobbyId) && lobbyId > 0UL)
                {
                    Instance?.Logger.Information("[NetMod][Steam] Detected overlay join from Steam launch query param connect_lobby={LobbyId}", lobbyId);
                    EnqueueAndProcessOverlayJoin(lobbyId, "SteamApps.GetLaunchQueryParam");
                }
            }
            catch (Exception ex)
            {
                Instance?.Logger.Debug(ex, "[NetMod][Steam] GetLaunchQueryParam poll failed");
            }
        }

        /// <summary>
        /// Background timer pumps Steam callbacks so overlay Join works even when game loop is paused (overlay open).
        /// Callbacks run on timer thread; we EnqueueMainThread for game ops.
        /// </summary>
        private static void StartSteamCallbackPumpTimer()
        {
            if (s_steamCallbackPumpTimer != null)
                return;
            try
            {
                s_steamCallbackPumpTimer = new Timer(
                    _ =>
                    {
                        try
                        {
                            SteamAPI.RunCallbacks();
                        }
                        catch
                        {
                            // Ignore - Steam may not be ready
                        }
                    },
                    null,
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(100));
                Instance?.Logger.Debug("[NetMod] Steam callback pump timer started");
            }
            catch (Exception ex)
            {
                Instance?.Logger.Warning(ex, "[NetMod] Failed to start Steam callback pump timer");
            }
        }

        public override void Initialize()
        {
            Instance = this;

            this.gds = new GameDataSync(Logger);
            InitializeOptionalModule(
                DebugModuleId.MultiplayerModLang,
                "MultiplayerModLang",
                () => _ = new MultiplayerModLang(this));
            InitializeOptionalModule(
                DebugModuleId.CineHooks,
                "CineHooks",
                () => _ = new CineHooks());
            InitializeOptionalModule(
                DebugModuleId.MultiplayerUI,
                "MultiplayerUI",
                () => _ = new MultiplayerUI(this, 0));

            _ = new SettingsUI(this);

            InitializeOptionalModule(
                DebugModuleId.LevelInit,
                "Levelinit",
                () => _ = new Levelinit(info));
            InitializeOptionalModule(
                DebugModuleId.MobsSynchronization,
                "MobsSynchronization",
                () => _ = new MobsSynchronization(this));
            InitializeOptionalModule(
                DebugModuleId.MinimapReveal,
                "Minimapreveal",
                () => _ = new Minimapreveal());
            InitializeOptionalModule(
                DebugModuleId.LevelExitSync,
                "LevelExitSync",
                () => _ = new LevelExitSync(this));
            InitializeOptionalModule(
                DebugModuleId.InteractionSync,
                "InteractionSync",
                () => _ = new InteractionSync(this));
            InitializeOptionalModule(
                DebugModuleId.ConnectionUI,
                "ConnectionUI",
                () => ConnectionUI.Initialize(this));

            GameMenu.Initialize(Logger);
            s_steamOverlayCallbackPending = true;
            s_steamOverlayCallbackRetryCount = 0;
            EventSystem.BroadcastEvent<IOnAdvancedModuleInitializing, ModEntry>(this);

            void InitializeOptionalModule(DebugModuleId moduleId, string moduleName, Action init)
            {
                var isEnabled = !MultiplayerSettingsStorage.IsDebugSectionEnabled ||
                                MultiplayerSettingsStorage.IsModuleEnabled(moduleId);
                if (isEnabled)
                {
                    init();
                    return;
                }

                Logger.Information("[NetMod][Debug] Module disabled by settings: {Module}", moduleName);
            }
        }

        void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
        {
            if (s_hooksInstalled)
                return;

            s_hooksInstalled = true;
            entry.Logger.Information("\x1b[32m[[ModEntry] Initializing ModEntry...]\x1b[0m ");
            Hook_Game.init += Hook_gameinit;
            Hook_Hero.wakeup += hook_hero_wakeup;
            Hook_Hero.onLevelChanged += hook_level_changed;
            Hook_User.newGame += GameDataSync.user_hook_new_game;
            Hook_User.unserialize += Hook_User_unserialize;
            Hook_Game.onDispose += Hook_Game_onDispose;
            Hook__Save.save += Hook__Save_save;
            Hook_AnimManager.play += Hook_AnimManager_play;
            Hook_MiniMap.track += Hook_MiniMap_track;
            Hook__LevelStruct.get += Hook__LevelStruct_get;
            Hook_LevelGen.generateGraph += Hook_LevelGen_generateGraph;
            Hook_Boot.update += hook_boot_update;
            Hook_Game.pause += Hook_Game_pause;
            Hook_Hero.kill += Hook_Hero_kill;
            Hook_Hero.onDie += Hook_Hero_onDie;
            Hook_Hero.onDamage += Hook_Hero_onDamage;
            Hook_Hero.checkCursedWeaponHit += Hook_Hero_checkCursedWeaponHit;
            Hook_Hero.startDeathCine += Hook_Hero_startDeathCine;
            Hook_Hero.onHeroDie += Hook_Hero_onHeroDie;
            Hook_ZDoor.onActivate += Hook_ZDoor_onActivate;
            Hook_BossRushDoor.initGfx += Hook_BossRushDoor_initGfx;
            Hook_Hero.applySkin += Hook_Hero_applySkin;
            Hook_HeroHead.initCustomHead += Hook_HeroHead_initCustomHead;
            Hook_DiveAttack.onStart += Hook_DiveAttack_onStart;
            Hook_DiveAttack.onOwnerLand += Hook_DiveAttack_onOwnerLand;
            Hook_HiddenTrigger.trigger += Hook_HiddenTrigger_trigger;
            Hook__HeroDeath.__constructor__ += Hook__HeroDeath__constructor__;
            Hook__HeroDeathBase.__constructor__ += Hook__HeroDeathBase__constructor__;
            Hook__HeroDeathContinue.__constructor__ += Hook__HeroDeathContinue__constructor__;
            Hook__HeroDeathRespawn.__constructor__ += Hook__HeroDeathRespawn__constructor__;
            Hook__HeroDeathDLCP.__constructor__ += Hook__HeroDeathDLCP__constructor__;
            Hook__BeholderDeath.__constructor__ += Hook__BeholderDeath__constructor__;
            Hook__GiantDeath.__constructor__ += Hook__GiantDeath__constructor__;
            Hook__GiantDeath4.__constructor__ += Hook__GiantDeath4__constructor__;
            Hook__KillKingCinem.__constructor__ += Hook__KillKingCinem__constructor__;
            Hook__KillQueenCinem.__constructor__ += Hook__KillQueenCinem__constructor__;
            Hook__QueenDefeated.__constructor__ += Hook__QueenDefeated__constructor__;
            Hook__KillDookuBeastCinem.__constructor__ += Hook__KillDookuBeastCinem__constructor__;
            Hook__FakeKillDooku.__constructor__ += Hook__FakeKillDooku__constructor__;
            Hook__RichterDeath.__constructor__ += Hook__RichterDeath__constructor__;
            Hook__EndCollectorPreSmash.__constructor__ += Hook__EndCollectorPreSmash__constructor__;
            Hook__SmashCinem.__constructor__ += Hook__SmashCinem__constructor__;
            Hook__EndCollectorPostSmash.__constructor__ += Hook__EndCollectorPostSmash__constructor__;
            Hook__EndCollectorPostSmashKS.__constructor__ += Hook__EndCollectorPostSmashKS__constructor__;
            // Hook_Hero.tryToApplyYoloPerk += Hook_Hero_tryToApplyYoloPerk;
            // Hook_Hero.onEnterRoom += 
            Ghost.KingWeaponHooks.Install();
        }


        private void Hook_Hero_applySkin(Hook_Hero.orig_applySkin orig, Hero self, dc.String skinId)
        {
            orig(self, skinId);
            if (_netRole == NetRole.None)
                return;

            var net = _net;
            if (net == null || !net.IsAlive || me == null || self == null || !ReferenceEquals(self, me))
                return;

            try
            {
                var rawSkin = dc.Main.Class.ME?.user?.heroSkin?.ToString();
                if (string.IsNullOrWhiteSpace(rawSkin))
                {
                    try { rawSkin = self.getSkinInfo()?.consoleCmdId?.ToString(); } catch { }
                }

                var skin = NormalizeSkin(rawSkin, "PrisonerDefault");

                if (string.Equals(_lastSentHeroSkin, skin, StringComparison.Ordinal))
                    return;

                net.SendHeroSkin(skin);
                _lastSentHeroSkin = skin;
            }
            catch (Exception ex)
            {
                Logger.Warning("[NetMod] Failed to send skin from applySkin hook: {msg}", ex.Message);
            }
        }

        private void Hook_HeroHead_initCustomHead(Hook_HeroHead.orig_initCustomHead orig, HeroHead self)
        {
            orig(self);
            if (_netRole == NetRole.None)
                return;

            var net = _net;
            if (net == null || !net.IsAlive || me == null || self == null)
                return;

            HeroHead? localHead = null;
            try { localHead = me.heroHead; } catch { }
            if (localHead == null || !ReferenceEquals(self, localHead))
                return;

            try
            {
                var skin = NormalizeSkin(dc.Main.Class.ME?.user?.heroHeadSkin?.ToString(), "BaseFlame");
                if (string.Equals(_lastSentHeroHeadSkin, skin, StringComparison.Ordinal))
                    return;

                net.SendHeroHeadSkin(skin);
                _lastSentHeroHeadSkin = skin;
            }
            catch (Exception ex)
            {
                Logger.Warning("[NetMod] Failed to send head skin from initCustomHead hook: {msg}", ex.Message);
            }
        }

        private void Hook_ZDoor_onActivate(Hook_ZDoor.orig_onActivate orig, ZDoor self, Hero lp, bool mob)
        {
            orig(self, lp, mob);

            if (_netRole != NetRole.None &&
                _net != null &&
                me != null &&
                lp != null &&
                ReferenceEquals(lp, me))
            {
                try { SendCurrentRoomTarget(force: true); } catch { }
                try { ReceiveGhostCoords(); } catch (Exception ex) { Logger.Warning(ex, "[NetMod] ReceiveGhostCoords failed"); }
            }
        }

        private void Hook_BossRushDoor_initGfx(Hook_BossRushDoor.orig_initGfx orig, BossRushDoor self)
        {
            try
            {
                orig(self);
                return;
            }
            catch (Exception ex)
            {
                if (_netRole != NetRole.Client || self == null || !ContainsBossRushFrameCrash(ex))
                    throw;

                string? bossRushType = null;
                try { bossRushType = self.bossRushType?.ToString(); } catch { }

                Logger.Warning("[NetMod] BossRushDoor.initGfx skipped on client level={LevelId}: type={Type} ({Msg})",
                    levelId,
                    bossRushType ?? "null",
                    ex.Message);
                try { self.spr = null; } catch (Exception ex2) { Logger.Warning(ex2, "[NetMod] BossRushDoor spr=null failed"); }
                return;
            }
        }

        private void Hook_HiddenTrigger_trigger(Hook_HiddenTrigger.orig_trigger orig, HiddenTrigger self, Entity dh)
        {
            var senderLevelId = string.IsNullOrWhiteSpace(levelId) ? string.Empty : levelId.Trim();
            string? senderGenericEventId = null;
            double? senderPreX = null;
            double? senderPreY = null;
            int? senderPreDir = null;
            double? senderPostX = null;
            double? senderPostY = null;
            int? senderPostDir = null;

            if (self != null)
            {
                try { senderGenericEventId = self.genericEventId?.ToString()?.Trim(); } catch { }
            }

            if (dh != null && me != null && ReferenceEquals(dh, me))
                TryCaptureBossCineHeroPosition(me, out senderPreX, out senderPreY, out senderPreDir);

            orig(self, dh);

            if (_suppressBossCineSendDepth > 0)
                return;
            if (_suppressBossTriggerNetSendUntilTick != 0 &&
                Stopwatch.GetTimestamp() < _suppressBossTriggerNetSendUntilTick)
                return;
            if (_netRole == NetRole.None || _net == null || !_net.IsAlive)
                return;
            if (self == null || dh == null || me == null || !ReferenceEquals(dh, me))
                return;

            if (!BossLevelIds.Contains(senderLevelId))
                return;
            if (IsBossCineCompleted(senderLevelId) ||
                IsBossCineSendSuppressed(senderLevelId) ||
                HasAppliedBossHeroTeleport(senderLevelId))
                return;
            if (string.IsNullOrWhiteSpace(senderGenericEventId))
                return;
            if (!BossRoomGenericEventIds.Contains(senderGenericEventId))
                return;
            if (!DidBossTriggerStart(dc.pr.Game.Class.ME, self))
                return;

            TryCaptureBossCineHeroPosition(me, out senderPostX, out senderPostY, out senderPostDir);
            if (senderPreX.HasValue && senderPreY.HasValue)
                _net.SendBossHeroTeleport(senderPreX.Value, senderPreY.Value, senderPreDir ?? 0);

            if (TrySendBossCinePayload(BuildBossCinePayload(
                senderLevelId,
                senderGenericEventId,
                senderPreX,
                senderPreY,
                senderPreDir,
                senderPostX,
                senderPostY,
                senderPostDir)))
                MarkBossCineCompleted(senderLevelId);
        }

        private static bool ContainsBossRushFrameCrash(Exception ex)
        {
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                var msg = cur.Message;
                if (!string.IsNullOrWhiteSpace(msg) &&
                    msg.IndexOf("Unknown frame: bossRushDoor", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private void Hook_Hero_lockControlFromSkill(Hook_Hero.orig_lockControlFromSkill orig, Hero self, double sec)
        {
            if(Ghost.KingWeaponSupport.IsInKingContext && me != null && ReferenceEquals(self, me))
                return;
            orig(self, sec);
        }

        private void Hook_Hero_unlockControls(Hook_Hero.orig_unlockControls orig, Hero self)
        {
            if(Ghost.KingWeaponSupport.IsInKingContext && me != null && ReferenceEquals(self, me))
                return;
            orig(self);
        }

        private void Hook_User_unserialize(Hook_User.orig_unserialize orig, User self, dc.hxbit.Serializer v)
        {
            orig(self, v);
            if (_netRole == NetRole.Client)
                GameDataSync.CaptureOriginalUserData(self, allowReplaceWhenBetter: true);
        }

        private void Hook_Game_onDispose(Hook_Game.orig_onDispose orig, dc.pr.Game self)
        {
            if (_netRole == NetRole.Client)
            {
                var user = self?.user;
                if (user != null)
                    GameDataSync.SwapToOriginalUserData(user);
                GameDataSync.SwapToLocalSerializerSync();
            }

            orig(self);
        }

        private void Hook__Save_save(Hook__Save.orig_save orig, User u, bool onlyGameData)
        {
            if (_netRole == NetRole.Host)
            {
                orig(u, onlyGameData);
                return;
            }

            if (_netRole == NetRole.Client)
            {
                var serializerSwapped = GameDataSync.SwapToLocalSerializerSync();
                try
                {
                    orig(u, onlyGameData);
                }
                finally
                {
                    if (serializerSwapped)
                        GameDataSync.RestoreRemoteSerializerSync();
                }
                return;
            }

            orig(u, onlyGameData);
        }

        private void Hook_Viewport_bumpDir(Hook_Viewport.orig_bumpDir orig, Viewport self, int dir, double? pow)
        {
            if(Ghost.KingWeaponSupport.IsInKingContext)
                return;
            orig(self, dir, pow);
        }

        private void Hook_Entity_recoil(Hook_Entity.orig_recoil orig, Entity self, double dx)
        {
            if(Ghost.KingWeaponSupport.IsInKingContext && me != null && ReferenceEquals(self, me))
                return;
            orig(self, dx);
        }

        private void Hook_Entity_bump(Hook_Entity.orig_bump orig, Entity self, double dy, double ignoreResist, bool? dx)
        {
            if(Ghost.KingWeaponSupport.IsInKingContext && me != null && ReferenceEquals(self, me))
                return;
            orig(self, dy, ignoreResist, dx);
        }

        private void Hook_Entity_bumpAwayFrom(Hook_Entity.orig_bumpAwayFrom orig, Entity self, Entity e, double? pow, bool? ignoreResist)
        {
            if(Ghost.KingWeaponSupport.IsInKingContext && me != null && ReferenceEquals(self, me))
                return;
            orig(self, e, pow, ignoreResist);
        }

        private void Hook_Entity_cancelVelocities(Hook_Entity.orig_cancelVelocities orig, Entity self)
        {
            if(Ghost.KingWeaponSupport.IsInKingContext && me != null && ReferenceEquals(self, me))
                return;
            orig(self);
        }

        private void Hook_Entity_setAffectS(Hook_Entity.orig_setAffectS orig, Entity self, int id, double sec, Ref<double> ignoreResist, bool? allowResist)
        {
            if(Ghost.KingWeaponSupport.IsInKingContext && me != null && ReferenceEquals(self, me))
                return;
            orig(self, id, sec, ignoreResist, allowResist);
        }

        private void Hook_Entity_removeAllAffects(Hook_Entity.orig_removeAllAffects orig, Entity self, int list)
        {
            if(Ghost.KingWeaponSupport.IsInKingContext && me != null && ReferenceEquals(self, me))
                return;
            orig(self, list);
        }

        private void Hook_Hero_onLevelChanged()
        {

        }



        private void Hook_Game_pause(Hook_Game.orig_pause orig, dc.pr.Game self)
        {
            // don't change that
            return; 
        }


        private void hook_boot_update(Hook_Boot.orig_update orig, Boot self, double dt)
        {
            orig(self, dt);
            GameMenu.ProcessMainThreadQueue();
            GameMenu.HandleTextInputClipboardShortcuts();
            _ghost?.UpdateLabels();
        }



        private LevelStruct Hook__LevelStruct_get(Hook__LevelStruct.orig_get orig,
        User user,
        virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_ l,
        Rand rng)
        {
            levelId = l.id.ToString();
            var net = _net;
            Logger.Information("[NetMod] _LevelStruct.get hook role={Role} level={LevelId}", _netRole, levelId);
            SendLevel(levelId);

            if (_netRole == NetRole.Host)
                GameDataSync.SendLevelSeed(levelId, rng, net);
            else if (_netRole == NetRole.Client)
            {
                GameDataSync.TryApplyRemoteSerializerSync();
                GameDataSync.TryApplyRemoteLevelSeed(levelId, rng);
            }

            var result = orig(user, l, rng);

            return result;
        }

        private RoomNode Hook_LevelGen_generateGraph(Hook_LevelGen.orig_generateGraph orig,
        LevelGen self,
        User user,
        virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_ l,
        Rand rng)
        {
            var graphLevelId = l?.id?.ToString() ?? levelId ?? string.Empty;
            Logger.Information("[NetMod] LevelGen.generateGraph hook role={Role} level={LevelId}", _netRole, graphLevelId);

            var root = orig(self, user, l, rng);
            var graph = root?.@struct;

            if (_netRole == NetRole.Host)
            {
                try
                {
                    GameDataSync.SendLevelGraph(graphLevelId, root, graph, rng, _net);
                    var activeUser = user ?? game?.user ?? dc.Main.Class.ME?.user;
                    if (activeUser != null)
                        GameDataSync.SendBossRune(activeUser, _net);
                }
                catch (Exception ex)
                {
                    Logger.Warning("[NetMod] Failed to send level graph for {LevelId}: {msg}", graphLevelId, ex.Message);
                }
            }
            else if (_netRole == NetRole.Client)
            {
                try
                {
                    const int graphSyncWaitMs = 10000;
                    if (GameDataSync.TryApplyRemoteLevelGraph(graphLevelId, graph, rng, graphSyncWaitMs, out var remoteRoot, out var reason))
                    {
                        Logger.Information("[NetMod] Applied remote level graph+rand for {LevelId}", graphLevelId);
                        if (remoteRoot != null)
                            root = remoteRoot;
                    }
                    else
                    {
                        Logger.Warning("[NetMod] Remote level graph not applied for {LevelId}: {Reason}", graphLevelId, reason);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning("[NetMod] Failed to apply remote level graph for {LevelId}: {msg}", graphLevelId, ex.Message);
                }
            }

            return root!;
        }


        private void Hook_MiniMap_track(Hook_MiniMap.orig_track orig, MiniMap self, Entity col, int? iconId, dc.String forcedIconColor, int? blink, bool? customTile, Tile text, dc.String itemKind, dc.String isInfectedFood)
        {

            miniMap = self;
            orig(self, col, iconId, forcedIconColor, blink, customTile, text, itemKind, isInfectedFood);
        }

        private AnimManager Hook_AnimManager_play(Hook_AnimManager.orig_play orig, AnimManager self, dc.String plays, int? queueAnim, bool? g)
        {
            if(plays == null)
                return orig(self, plays, queueAnim, g);

            var play = plays.ToString();
            if(string.IsNullOrWhiteSpace(play))
                return orig(self, plays, queueAnim, g);

            if (me != null && me?.spr?._animManager != null && ReferenceEquals(self, me.spr._animManager))
            {
                if (!DeadCellsMultiplayerMod.Ghost.KingWeaponSupport.IsInKingContext &&
                    !IsAttackAnim(play))
                    SendHeroAnim(play, queueAnim, g);
            }
            if(me != null && me.heroHead.customHeadSpr != null && ReferenceEquals(self, me.heroHead.customHeadSpr._animManager))
            {
                SendHeadAnim(play);
            }

            return orig(self, plays, queueAnim, g);
        }


        private static bool IsAttackAnim(string anim)
        {
            if (string.IsNullOrWhiteSpace(anim)) return false;
            var a = anim.Trim();
            if (a.StartsWith("w_", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.StartsWith("attack", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.StartsWith("atk", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.IndexOf("shoot", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("charge", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("hold", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("bow", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("crossbow", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("xbow", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("shield", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("block", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("parry", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("whip", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public void hook_level_changed(Hook_Hero.orig_onLevelChanged orig, Hero self, Level oldLevel)
        {
            kingInitialized = false;
            DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization.ClearTrackingForLevelChange();
            try { _net?.ClearMobSyncQueues(); } catch (Exception ex) { Logger.Warning(ex, "[NetMod] ClearMobSyncQueues failed"); }
            _pendingBossCineApplyByLevel.Clear();
            _suppressBossCineEchoByLevel.Clear();
            _completedBossCineLevels.Clear();
            _appliedBossHeroTeleportLevels.Clear();
            _lastBossCineSentLevelId = null;
            _lastBossCineSentTick = 0;
            ResetFakeDeathState(unlockLocalHero: true, sendNetworkUpState: false, clearRemoteDownedTracking: false, clearDownedAnnouncements: false);
            me = self;
            try { me._targetable = true; } catch { }
            orig(self, oldLevel);
            var currentLevelId = GetCurrentLevelId();
            if (!string.IsNullOrWhiteSpace(currentLevelId))
                SendLevel(currentLevelId);
            SendCurrentRoomTarget(force: true);
            try { _net?.ClearMobSyncQueues(); } catch (Exception ex) { Logger.Warning(ex, "[NetMod] ClearMobSyncQueues failed"); }
            EnsureHeroVisibilityAfterRoomChange(me);
            if (_netRole == NetRole.None) return;
            var net = _net;
            var localId = net?.id ?? 0;
            _ghost = new GhostHero(localId, game!, me, Logger, this);
            _ghost.SetLabel(me, GameMenu.Username);

            for (int i = 0; i < clients.Length; i++)
            {
                DisposeClientSlot(i, clearIdentity: false);
                rLastX[i] = 0;
                rLastY[i] = 0;
            }

            DrainRemoteCombatQueuesAfterLevelChange();
            ReceiveGhostCoords();

            _debugExplorerRevealAppliedSignature = string.Empty;
            _nextDebugExplorerRevealRetryTick = 0;
            _debugExplorerRevealAllCount = 0;
        }


        public void hook_hero_wakeup(Hook_Hero.orig_wakeup orig, Hero self, Level lvl, int cx, int cy)
        {
            me = self;
            try { me._targetable = true; } catch { }
            _debugExplorerRevealAppliedSignature = string.Empty;
            orig(self, lvl, cx, cy);
            EnsureHeroVisibilityAfterRoomChange(me);
            SendCurrentRoomTarget(force: true);
            SendEquippedWeapons(self.inventory);
        }


        public void Hook_gameinit(Hook_Game.orig_init orig, dc.pr.Game self)
        {
            game = self;
            orig(self);
        }

        public void OnHeroInit()
        {
            GameMenu.MarkInRun();
            ApplyDebugHeroRuntimeOptions();
        }

        public void OnFrameUpdate(double dt)
        {
            if (!_ready) return;
            GameMenu.ProcessMainThreadQueue();
            GameMenu.TickMenu(dt);
            DetectAndSendBossCine();
            ApplyReceivedBossHeroTeleport();
            ApplyReceivedBossCine();
            SuppressRemoteBossDeathCineIfNeeded();
        }

        private void DetectAndSendBossCine()
        {
            if (_netRole == NetRole.None || _net == null || !_net.IsAlive)
                return;

            var currentLevelId = string.IsNullOrWhiteSpace(levelId) ? null : levelId.Trim();
            if (string.IsNullOrEmpty(currentLevelId) || !BossLevelIds.Contains(currentLevelId))
                return;
            if (IsBossCineCompleted(currentLevelId))
                return;

            try
            {
                var game = dc.pr.Game.Class.ME;
                var cine = game?.curCine;
                if (cine == null || cine.destroyed)
                    return;

                if (cine is DeadBase || cine is RemoteDownedCorpse)
                    return;

                try
                {
                    if (cine is HeroDeath || cine is HeroDeathBase || cine is HeroDeathContinue ||
                        cine is HeroDeathRespawn || cine is HeroDeathDLCP)
                        return;
                }
                catch
                {
                    var typeName = cine.GetType().FullName ?? string.Empty;
                    if (typeName.IndexOf("HeroDeath", StringComparison.OrdinalIgnoreCase) >= 0)
                        return;
                }

                if (TrySendBossCinePayload(BuildBossCinePayload(currentLevelId, null)))
                    MarkBossCineCompleted(currentLevelId);
            }
            catch
            {
            }
        }

        private bool TrySendBossCinePayload(string payload)
        {
            if (_netRole == NetRole.None || _net == null || !_net.IsAlive)
                return false;

            if (!TryParseBossCinePayload(payload, out var levelId, out var _, out var _, out var _, out var _, out var _, out var _, out var _))
                return false;

            if (IsBossCineCompleted(levelId))
                return false;

            if (IsBossCineSendSuppressed(levelId))
                return false;

            var now = Stopwatch.GetTimestamp();
            var cooldownTicks = (long)(Stopwatch.Frequency * BossCineSendCooldownSeconds);
            if (_lastBossCineSentLevelId == levelId && now - _lastBossCineSentTick < cooldownTicks)
                return false;

            _lastBossCineSentLevelId = levelId;
            _lastBossCineSentTick = now;
            _net.SendBossCine(payload);
            return true;
        }

        private string BuildBossCinePayload(string levelId, string? genericEventId)
        {
            double? x = null;
            double? y = null;
            int? dir = null;
            if (me != null)
            {
                TryCaptureBossCineHeroPosition(me, out x, out y, out dir);
            }

            return BuildBossCinePayload(levelId, genericEventId, x, y, dir, null, null, null);
        }

        private string BuildBossCinePayload(string levelId, string? genericEventId, double? x, double? y, int? dir)
        {
            return BuildBossCinePayload(levelId, genericEventId, x, y, dir, null, null, null);
        }

        private string BuildBossCinePayload(
            string levelId,
            string? genericEventId,
            double? x,
            double? y,
            int? dir,
            double? finalX,
            double? finalY,
            int? finalDir)
        {
            var safeLevelId = string.IsNullOrWhiteSpace(levelId)
                ? string.Empty
                : levelId.Replace("\r", string.Empty, StringComparison.Ordinal)
                    .Replace("\n", string.Empty, StringComparison.Ordinal)
                    .Trim();
            if (string.IsNullOrEmpty(safeLevelId))
                return string.Empty;

            var safeEventId = string.IsNullOrWhiteSpace(genericEventId)
                ? string.Empty
                : genericEventId.Replace("\r", string.Empty, StringComparison.Ordinal)
                    .Replace("\n", string.Empty, StringComparison.Ordinal)
                    .Trim();

            if (!x.HasValue || !y.HasValue)
                return string.IsNullOrEmpty(safeEventId) ? safeLevelId : $"{safeLevelId}|{safeEventId}";

            var resolvedFinalX = finalX ?? x.Value;
            var resolvedFinalY = finalY ?? y.Value;
            var resolvedFinalDir = finalDir ?? dir ?? 0;

            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{safeLevelId}|{safeEventId}|{x.Value}|{y.Value}|{dir ?? 0}|{resolvedFinalX}|{resolvedFinalY}|{resolvedFinalDir}");
        }

        private static bool TryParseBossCinePayload(
            string? payload,
            out string levelId,
            out string? genericEventId,
            out double? snapX,
            out double? snapY,
            out int? snapDir,
            out double? finalX,
            out double? finalY,
            out int? finalDir)
        {
            levelId = string.Empty;
            genericEventId = null;
            snapX = null;
            snapY = null;
            snapDir = null;
            finalX = null;
            finalY = null;
            finalDir = null;

            if (string.IsNullOrWhiteSpace(payload))
                return false;

            var normalized = payload
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal)
                .Trim();
            if (normalized.Length == 0)
                return false;

            var parts = normalized.Split('|');
            if (parts.Length == 0)
                return false;

            levelId = parts[0].Trim();
            if (string.IsNullOrWhiteSpace(levelId))
                return false;

            if (parts.Length >= 2)
            {
                var eventId = parts[1].Trim();
                if (eventId.Length > 0)
                    genericEventId = eventId;
            }

            if (parts.Length >= 4)
            {
                if (double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedX))
                    snapX = parsedX;
                if (double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedY))
                    snapY = parsedY;
            }

            if (parts.Length >= 5 &&
                int.TryParse(parts[4], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedDir))
            {
                snapDir = parsedDir;
            }

            if (parts.Length >= 7)
            {
                if (double.TryParse(parts[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedFinalX))
                    finalX = parsedFinalX;
                if (double.TryParse(parts[6], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedFinalY))
                    finalY = parsedFinalY;
            }

            if (parts.Length >= 8 &&
                int.TryParse(parts[7], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedFinalDir))
            {
                finalDir = parsedFinalDir;
            }

            return true;
        }

        private static void TryCaptureBossCineHeroPosition(Hero? hero, out double? x, out double? y, out int? dir)
        {
            x = null;
            y = null;
            dir = null;

            if (hero == null)
                return;

            try
            {
                if (hero.spr != null)
                {
                    x = hero.spr.x;
                    y = hero.spr.y;
                }
                else
                {
                    x = hero.get_targetSprPosX();
                    y = hero.get_targetSprPosY();
                }
            }
            catch
            {
            }

            try { dir = hero.dir; } catch { }
        }

        private void ApplyReceivedBossCine()
        {
            var net = _net;
            if (net == null || !net.IsAlive)
            {
                _pendingBossCineApplyByLevel.Clear();
                _suppressBossCineEchoByLevel.Clear();
                _completedBossCineLevels.Clear();
                _appliedBossHeroTeleportLevels.Clear();
                return;
            }

            if (net.TryConsumeBossCineLevelIds(out var levelIds) && levelIds.Count > 0)
            {
                var defaultExpiry = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * BossCineApplyPendingTtlSeconds);
                for (int i = 0; i < levelIds.Count; i++)
                {
                    var receivedPayload = levelIds[i];
                    if (!TryParseBossCinePayload(receivedPayload, out var receivedLevelId, out var receivedGenericEventId, out var receivedSnapX, out var receivedSnapY, out var _, out var _, out var _, out var _))
                        continue;

                    ClearBossCineCompleted(receivedLevelId);
                    if (!string.IsNullOrWhiteSpace(receivedGenericEventId) && receivedSnapX.HasValue && receivedSnapY.HasValue)
                        SuppressBossCineSend(receivedLevelId);

                    var normalized = receivedPayload
                        .Replace("\r", string.Empty, StringComparison.Ordinal)
                        .Replace("\n", string.Empty, StringComparison.Ordinal)
                        .Trim();

                    var expiry = defaultExpiry;
                    if (_pendingBossCineApplyByLevel.TryGetValue(normalized, out var oldExpiry) && oldExpiry > expiry)
                        expiry = oldExpiry;

                    _pendingBossCineApplyByLevel[normalized] = expiry;
                }
            }

            if (_pendingBossCineApplyByLevel.Count == 0)
                return;

            var now = Stopwatch.GetTimestamp();
            var currentLevelId = string.IsNullOrWhiteSpace(levelId) ? string.Empty : levelId.Trim();
            List<string>? remove = null;
            foreach (var kv in _pendingBossCineApplyByLevel)
            {
                var pendingPayload = kv.Key;
                var expiryTicks = kv.Value;
                if (now >= expiryTicks)
                {
                    (remove ??= new List<string>()).Add(pendingPayload);
                    continue;
                }

                if (!TryParseBossCinePayload(pendingPayload, out var pendingLevelId, out var genericEventId, out var snapX, out var snapY, out var snapDir, out var finalX, out var finalY, out var finalDir))
                {
                    (remove ??= new List<string>()).Add(pendingPayload);
                    continue;
                }

                if (string.IsNullOrEmpty(currentLevelId) ||
                    !string.Equals(pendingLevelId, currentLevelId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var triggered =
                    !string.IsNullOrWhiteSpace(genericEventId) &&
                    snapX.HasValue &&
                    snapY.HasValue
                        ? TrySyncBossCinematicFromLocalTrigger(pendingLevelId, genericEventId)
                        : TryTriggerBossCinematic(pendingLevelId, genericEventId, snapX, snapY, snapDir, finalX, finalY, finalDir);

                if (triggered)
                {
                    MarkBossCineCompleted(pendingLevelId);
                    SuppressBossCineSend(pendingLevelId);
                    (remove ??= new List<string>()).Add(pendingPayload);
                }
            }

            if (remove == null || remove.Count == 0)
                return;

            for (int i = 0; i < remove.Count; i++)
                _pendingBossCineApplyByLevel.Remove(remove[i]);
        }

        private void ApplyReceivedBossHeroTeleport()
        {
            var net = _net;
            if (net == null || !net.IsAlive)
                return;

            if (!net.TryConsumeBossHeroTeleportEvents(out var teleports) || teleports.Count == 0)
                return;

            var localHero = me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (localHero == null)
                return;

            var localId = net.id;
            var currentLevelId = GetCurrentLevelId();
            foreach (var teleport in teleports)
            {
                if (teleport.UserId > 0 && teleport.UserId == localId)
                    continue;
                if (HasAppliedBossHeroTeleport(currentLevelId))
                    continue;

                MarkBossHeroTeleportApplied(currentLevelId);
                SuppressBossCineSend(currentLevelId);
                _suppressBossTriggerNetSendUntilTick =
                    Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * BossHeroTeleportEchoSuppressSeconds);
                try { localHero.cancelVelocities(); } catch { }
                try { localHero.setPosPixel(teleport.X, teleport.Y - BossHeroTeleportYOffsetPx); } catch { }
                try { localHero.dir = teleport.Dir; } catch { }
            }
        }

        private bool TrySyncBossCinematicFromLocalTrigger(
            string levelId,
            string? genericEventId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(levelId) || !BossLevelIds.Contains(levelId))
                    return false;

                var game = dc.pr.Game.Class.ME;
                var hero = game?.hero ?? me;
                var level = hero?._level;
                if (game == null || hero == null || level == null)
                    return false;

                var currentCine = game.curCine;
                if (currentCine != null && !currentCine.destroyed)
                    return IsBossIntroCinematic(currentCine);

                if (GameHasAnyCinematic(game))
                    return false;

                if (!DidBossHiddenTriggerStart(level, genericEventId))
                    return false;

                _lastBossCineSentLevelId = levelId;
                _lastBossCineSentTick = Stopwatch.GetTimestamp();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool DidBossHiddenTriggerStart(Level level, string? genericEventId)
        {
            if (level == null || string.IsNullOrWhiteSpace(genericEventId))
                return false;

            try
            {
                var entitiesByClass = level.entitiesByClass;
                var triggerClid = HiddenTrigger.Class.__clid;
                var entries = entitiesByClass?.get(triggerClid) as dc.hl.types.ArrayObj;
                if (entries == null)
                    return false;

                for (var i = 0; i < entries.length; i++)
                {
                    if (entries.getDyn(i) is not HiddenTrigger ht)
                        continue;

                    var evId = ht.genericEventId?.ToString();
                    if (!string.Equals(evId, genericEventId, StringComparison.Ordinal))
                        continue;

                    if (ht.used)
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private bool TryTriggerBossCinematic(
            string levelId,
            string? genericEventId,
            double? snapX,
            double? snapY,
            int? snapDir,
            double? finalX,
            double? finalY,
            int? finalDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(levelId))
                    return false;

                if (!BossLevelIds.Contains(levelId))
                    return false;

                var game = dc.pr.Game.Class.ME;
                var hero = game?.hero ?? me;
                var level = hero?._level;
                if (game == null || level == null || hero == null)
                    return false;

                var currentCine = game.curCine;
                if (currentCine != null && !currentCine.destroyed)
                    return IsBossIntroCinematic(currentCine);

                var entitiesByClass = level.entitiesByClass;
                if (entitiesByClass != null)
                {
                    var triggerClid = HiddenTrigger.Class.__clid;
                    var entries = entitiesByClass.get(triggerClid) as dc.hl.types.ArrayObj;
                    if (entries != null)
                    {
                        HiddenTrigger? usedReplayCandidate = null;
                        for (var i = 0; i < entries.length; i++)
                        {
                            if (entries.getDyn(i) is not HiddenTrigger ht)
                                continue;

                            var evId = ht.genericEventId?.ToString();
                            if (string.IsNullOrEmpty(evId))
                                continue;
                            if (!string.IsNullOrWhiteSpace(genericEventId))
                            {
                                if (!string.Equals(evId, genericEventId, StringComparison.Ordinal))
                                    continue;
                            }
                            else if (!BossRoomGenericEventIds.Contains(evId))
                            {
                                continue;
                            }

                            if (ht.used)
                            {
                                usedReplayCandidate ??= ht;
                                continue;
                            }

                            if (GameHasAnyCinematic(game))
                                return false;

                            TrySnapHeroToBossCinePosition(hero, snapX, snapY, snapDir);
                            RunWithSuppressedBossCineSend(() => ht.trigger(hero));
                            if (DidBossTriggerStart(game, ht))
                            {
                                TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                                _lastBossCineSentLevelId = levelId;
                                _lastBossCineSentTick = Stopwatch.GetTimestamp();
                                return true;
                            }

                            return false;
                        }

                        if (usedReplayCandidate != null && TryReplayBossHiddenTrigger(usedReplayCandidate, hero, snapX, snapY, snapDir, finalX, finalY, finalDir))
                        {
                            _lastBossCineSentLevelId = levelId;
                            _lastBossCineSentTick = Stopwatch.GetTimestamp();
                            return true;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(genericEventId) &&
                    TryCreateBossCinematicDirectly(level, hero, genericEventId, snapX, snapY, snapDir, finalX, finalY, finalDir))
                {
                    MarkBossRoomHiddenTriggersUsed(level, genericEventId);
                    _lastBossCineSentLevelId = levelId;
                    _lastBossCineSentTick = Stopwatch.GetTimestamp();
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private bool TryReplayBossHiddenTrigger(
            HiddenTrigger trigger,
            Hero hero,
            double? snapX,
            double? snapY,
            int? snapDir,
            double? finalX,
            double? finalY,
            int? finalDir)
        {
            if (trigger == null || hero == null)
                return false;

            try
            {
                var wasUsed = trigger.used;
                var game = dc.pr.Game.Class.ME;
                if (GameHasAnyCinematic(game))
                    return false;

                trigger.used = false;
                TrySnapHeroToBossCinePosition(hero, snapX, snapY, snapDir);
                RunWithSuppressedBossCineSend(() => trigger.trigger(hero));

                if (DidBossTriggerStart(game, trigger))
                {
                    TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                    return true;
                }

                trigger.used = wasUsed;
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool DidBossTriggerStart(dc.pr.Game? game, HiddenTrigger trigger)
        {
            if (trigger != null)
            {
                try
                {
                    if (trigger.used)
                        return true;
                }
                catch
                {
                }
            }

            var currentCine = game?.curCine;
            return currentCine != null && !currentCine.destroyed && IsBossIntroCinematic(currentCine);
        }

        private static bool GameHasAnyCinematic(dc.pr.Game? game)
        {
            if (game == null)
                return false;

            try
            {
                return game.hasCinematic();
            }
            catch
            {
                var currentCine = game.curCine;
                return currentCine != null && !currentCine.destroyed;
            }
        }

        private void TrySnapHeroToBossCinePosition(Hero hero, double? snapX, double? snapY, int? snapDir)
        {
            if (hero == null || !snapX.HasValue || !snapY.HasValue)
                return;

            try { hero.cancelVelocities(); } catch { }
            SnapHeroToDownedPosition(hero, snapX.Value, snapY.Value, clampToGround: false);
            if (snapDir.HasValue)
            {
                try { hero.dir = snapDir.Value; } catch { }
            }
        }

        private static bool IsBossIntroCinematic(dc.GameCinematic? cine)
        {
            if (cine == null)
                return false;

            try
            {
                var typeName = cine.GetType().Name ?? string.Empty;
                return BossIntroCineTypeNames.Contains(typeName);
            }
            catch
            {
                return false;
            }
        }

        private bool TryCreateBossCinematicDirectly(
            Level level,
            Hero hero,
            string genericEventId,
            double? snapX,
            double? snapY,
            int? snapDir,
            double? finalX,
            double? finalY,
            int? finalDir)
        {
            if (level == null || hero == null || string.IsNullOrWhiteSpace(genericEventId))
                return false;

            try
            {
                TrySnapHeroToBossCinePosition(hero, snapX, snapY, snapDir);
                switch (genericEventId.Trim())
                {
                    case "roomDeath":
                        RunWithSuppressedBossCineSend(() => _ = new EnterRoomDeathBoss(hero));
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomBeholder":
                    case "roomBerserk":
                        RunWithSuppressedBossCineSend(() => _ = new EnterRoomBoss(hero));
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomCollectorBoss":
                        RunWithSuppressedBossCineSend(() =>
                        {
                            var story = level.game?.user?.story;
                            var counters = story?.counters;
                            var key = "collectorMet".AsHaxeString();
                            var collectorMet = 0;
                            if (counters != null && counters.exists(key))
                            {
                                var rawValue = counters.get(key)?.ToString();
                                int.TryParse(rawValue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out collectorMet);
                            }
                            if (collectorMet == 1)
                                _ = new StartCollectorFightAlt(hero);
                            else
                                _ = new MeetCollectorEnd(null);
                        });
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomDooku":
                        RunWithSuppressedBossCineSend(() => _ = new EnterDookuBossRoom(hero));
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomGardenerBoss":
                        RunWithSuppressedBossCineSend(() => _ = new EnterRoomGardenerBoss(hero));
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomGiant":
                        RunWithSuppressedBossCineSend(() =>
                        {
                            if (level.boss is Giant giant)
                            {
                                var modifiers = giant.bossRushModifiers;
                                if (modifiers != null && modifiers.enabled)
                                {
                                    _ = new EnterModifiedGiantRoom(hero);
                                    return;
                                }
                            }

                            _ = new EnterGiantRoom(hero);
                        });
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomKingsHand":
                        RunWithSuppressedBossCineSend(() =>
                        {
                            if (level.game.isBossRush())
                            {
                                _ = new EnterThroneBossRush(hero);
                                return;
                            }

                            if (hero.hasSkin("king".AsHaxeString(), null))
                                return;

                            _ = new EnterThroneRoom(hero);
                        });
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomKingsHandAsKing":
                        if (!hero.hasSkin("king".AsHaxeString(), null))
                            return false;

                        RunWithSuppressedBossCineSend(() => _ = new EnterThroneRoomAsKing(hero));
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomMamaTick":
                        RunWithSuppressedBossCineSend(() =>
                        {
                            var storyProgress = 0;
                            try
                            {
                                var ug = level.game?.user;
                                if (ug != null)
                                {
                                    var sp = ug.story.getNpcProgress(new NpcId.TickPriest());
                                    storyProgress = sp ?? 0;
                                }
                            }
                            catch { }

                            if (storyProgress > 0 && level.boss is MamaTick mamaTick)
                            {
                                mamaTick.publicEmerge();
                                return;
                            }

                            _ = new EnterRoomBoss(hero);
                        });
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomQueen":
                        RunWithSuppressedBossCineSend(() => _ = new EnterRoomQueenBoss());
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomBehemoth":
                        RunWithSuppressedBossCineSend(() =>
                        {
                            if (level.boss is Behemoth behemoth)
                            {
                                var modifiers = behemoth.bossRushModifiers;
                                if (modifiers != null && modifiers.bossRushClone != null)
                                {
                                    _ = new EnterDualBehemoth(hero);
                                    return;
                                }
                            }

                            _ = new EnterRoomBoss(hero);
                        });
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static void MarkBossRoomHiddenTriggersUsed(Level level, string genericEventId)
        {
            if (level == null || string.IsNullOrWhiteSpace(genericEventId))
                return;

            try
            {
                var entries = level.entitiesByClass?.get(HiddenTrigger.Class.__clid) as dc.hl.types.ArrayObj;
                if (entries == null)
                    return;

                for (var i = 0; i < entries.length; i++)
                {
                    if (entries.getDyn(i) is not HiddenTrigger ht)
                        continue;

                    var eventId = ht.genericEventId?.ToString();
                    if (!string.Equals(eventId, genericEventId, StringComparison.Ordinal))
                        continue;

                    ht.used = true;
                }
            }
            catch
            {
            }
        }

        private bool IsBossCineSendSuppressed(string? levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return false;

            var normalized = levelId.Trim();
            if (normalized.Length == 0)
                return false;

            if (!_suppressBossCineEchoByLevel.TryGetValue(normalized, out var expiry))
                return false;

            var now = Stopwatch.GetTimestamp();
            if (now < expiry)
                return true;

            _suppressBossCineEchoByLevel.Remove(normalized);
            return false;
        }

        private void SuppressBossCineSend(string? levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return;

            var normalized = levelId.Trim();
            if (normalized.Length == 0)
                return;

            _suppressBossCineEchoByLevel[normalized] =
                Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * BossCineEchoSuppressSeconds);
        }

        private bool IsBossCineCompleted(string? levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return false;

            return _completedBossCineLevels.Contains(levelId.Trim());
        }

        private void MarkBossCineCompleted(string? levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return;

            _completedBossCineLevels.Add(levelId.Trim());
        }

        private void ClearBossCineCompleted(string? levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return;

            _completedBossCineLevels.Remove(levelId.Trim());
        }

        private void RunWithSuppressedBossCineSend(Action action)
        {
            if (action == null)
                return;

            _suppressBossCineSendDepth++;
            try
            {
                action();
            }
            finally
            {
                _suppressBossCineSendDepth--;
            }
        }

        private bool HasAppliedBossHeroTeleport(string? levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return false;

            return _appliedBossHeroTeleportLevels.Contains(levelId.Trim());
        }

        private void MarkBossHeroTeleportApplied(string? levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return;

            _appliedBossHeroTeleportLevels.Add(levelId.Trim());
        }

        private void SuppressRemoteBossDeathCineIfNeeded()
        {
            if (_netRole != NetRole.Client || _net == null || !_net.IsAlive)
                return;

            try
            {
                var game = dc.pr.Game.Class.ME;
                var cine = game?.curCine;
                if (cine == null || cine.destroyed)
                    return;

                if (cine is DeadBase || cine is RemoteDownedCorpse)
                    return;

                var typeName = cine.GetType().Name ?? string.Empty;
                if (!BossDeathCineTypeNames.Contains(typeName))
                    return;

                SuppressRemoteBossDeathCineState(cine);
            }
            catch
            {
            }
        }

        private bool ShouldSuppressRemoteBossDeathCineConstruction()
        {
            return _netRole == NetRole.Client && _net != null && _net.IsAlive;
        }

        private void SuppressRemoteBossDeathCineState(dc.GameCinematic? cine)
        {
            try
            {
                var game = dc.pr.Game.Class.ME;
                if (game != null && cine != null && ReferenceEquals(game.curCine, cine))
                    game.curCine = null;
            }
            catch
            {
            }

            try { cine?.destroy(); } catch { }
            try { cine?.disposeImmediately(); } catch { }
            try { me?.cancelSkillControlLock(); } catch { }
            try { me?.unlockControls(); } catch { }
            EnsureHeroVisibilityAfterRoomChange(me);
        }

        private void Hook__BeholderDeath__constructor__(Hook__BeholderDeath.orig___constructor__ orig, BeholderDeath e, Beholder boss)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e, boss);
        }

        private void Hook__GiantDeath__constructor__(Hook__GiantDeath.orig___constructor__ orig, GiantDeath e, Hero heroTarget)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e, heroTarget);
        }

        private void Hook__GiantDeath4__constructor__(Hook__GiantDeath4.orig___constructor__ orig, GiantDeath4 e, Hero heroTarget)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e, heroTarget);
        }

        private void Hook__KillKingCinem__constructor__(Hook__KillKingCinem.orig___constructor__ orig, KillKingCinem e, HlAction tween)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e, tween);
        }

        private void Hook__KillQueenCinem__constructor__(Hook__KillQueenCinem.orig___constructor__ orig, KillQueenCinem e)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e);
        }

        private void Hook__QueenDefeated__constructor__(Hook__QueenDefeated.orig___constructor__ orig, QueenDefeated e, Queen queen, HlAction dialogEnd)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e, queen, dialogEnd);
        }

        private void Hook__KillDookuBeastCinem__constructor__(Hook__KillDookuBeastCinem.orig___constructor__ orig, KillDookuBeastCinem e)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e);
        }

        private void Hook__FakeKillDooku__constructor__(Hook__FakeKillDooku.orig___constructor__ orig, FakeKillDooku e, Hero manager, DookuManager instant, Ref<bool> instantRef)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e, manager, instant, instantRef);
        }

        private void Hook__RichterDeath__constructor__(Hook__RichterDeath.orig___constructor__ orig, RichterDeath e, Hero lostBody, bool titleLib)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e, lostBody, titleLib);
        }

        private void Hook__EndCollectorPreSmash__constructor__(Hook__EndCollectorPreSmash.orig___constructor__ orig, EndCollectorPreSmash e, dc.en.mob.Boss boss, HlAction lt)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e, boss, lt);
        }

        private void Hook__SmashCinem__constructor__(Hook__SmashCinem.orig___constructor__ orig, SmashCinem e, bool hasKingSkin, HlAction endCb)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e, hasKingSkin, endCb);
        }

        private void Hook__EndCollectorPostSmash__constructor__(Hook__EndCollectorPostSmash.orig___constructor__ orig, EndCollectorPostSmash e)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e);
        }

        private void Hook__EndCollectorPostSmashKS__constructor__(Hook__EndCollectorPostSmashKS.orig___constructor__ orig, EndCollectorPostSmashKS e)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e);
        }


        void IOnHeroUpdate.OnHeroUpdate(double dt)
        {
            if (me == null) return;
            ApplyDebugHeroRuntimeOptions();
            TryRecoverMissedFakeDeathFromLife();
            if (_netRole == NetRole.None || _net == null)
                return;
            TrySendCurrentDiveSkillInfoSnapshot();
            SendCurrentRoomTarget(force: false);
            if (!_localFakeDead)
                SendHeroCoords();
            ReceiveGhostCoords();
            UpdateFakeDeathFlow(dt);
            MaintainPostRevivePositionLock();
            ReceiveGhostWeapons();
            ReceiveGhostAttacks();
            UpdateGhostWeapons();
            UpdateGhostHeads();
        }

        private static bool IsDebugImmortalLocalHero(Hero? hero)
        {
            return hero != null &&
                   me != null &&
                   ReferenceEquals(hero, me) &&
                   MultiplayerSettingsStorage.IsDebugSectionEnabled &&
                   MultiplayerSettingsStorage.DebugPlayerImmortal;
        }

        private static void ApplyDebugImmortalState(Hero hero)
        {
            if (hero == null)
                return;

            try { hero.noDamageDuringBossBattle = true; } catch { }
            try
            {
                if (hero.maxLife > 0 && hero.life < hero.maxLife)
                    hero.life = hero.maxLife;
            }
            catch
            {
                try { hero.fullHeal(); } catch { }
            }
            try { hero._targetable = true; } catch { }
        }

        private void ApplyDebugHeroRuntimeOptions()
        {
            var hero = me;
            if (hero == null || !MultiplayerSettingsStorage.IsDebugSectionEnabled)
                return;

            if (IsDebugImmortalLocalHero(hero))
            {
                ApplyDebugImmortalState(hero);
            }
            else
            {
                try { hero.noDamageDuringBossBattle = false; } catch { }
            }

            TryApplyDebugStartPerk(hero);
            TryApplyDebugExplorerRune(hero);
        }

        private void TryApplyDebugStartPerk(Hero hero)
        {
            if (hero == null)
                return;

            var configuredPerkId = MultiplayerSettingsStorage.DebugStartPerkId;
            if (string.IsNullOrWhiteSpace(configuredPerkId) ||
                string.Equals(configuredPerkId, MultiplayerSettingsStorage.NoStartPerkValue, StringComparison.OrdinalIgnoreCase))
            {
                _debugPerkAppliedHero = null;
                _debugPerkAppliedId = string.Empty;
                _lastDebugPerkApplyErrorId = string.Empty;
                _nextDebugPerkApplyTick = 0;
                return;
            }

            var perkId = configuredPerkId.Trim();
            if (ReferenceEquals(_debugPerkAppliedHero, hero) &&
                string.Equals(_debugPerkAppliedId, perkId, StringComparison.Ordinal))
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            if (_nextDebugPerkApplyTick != 0 && now < _nextDebugPerkApplyTick)
                return;

            try
            {
                var item = new InventItem(new InventItemKind.Perk(perkId.AsHaxeString()));
                hero.applyItemPickEffect(hero, item);

                if (string.Equals(perkId, "P_Yolo", StringComparison.OrdinalIgnoreCase))
                {
                    try { hero.tryToApplyYoloPerk(); } catch { }
                }

                _debugPerkAppliedHero = hero;
                _debugPerkAppliedId = perkId;
                _lastDebugPerkApplyErrorId = string.Empty;
                _nextDebugPerkApplyTick = 0;
            }
            catch (Exception ex)
            {
                _nextDebugPerkApplyTick = now + (long)(Stopwatch.Frequency * 1.5);
                if (string.Equals(_lastDebugPerkApplyErrorId, perkId, StringComparison.Ordinal))
                    return;

                _lastDebugPerkApplyErrorId = perkId;
                Logger.Warning(ex, "[NetMod] Failed to apply debug start perk {PerkId}", perkId);
            }
        }

        private void TryApplyDebugExplorerRune(Hero hero)
        {
            if (hero == null)
                return;

            ItemMetaManager? itemMeta = null;
            try
            {
                var user = hero._level?.game?.user ?? game?.user ?? dc.pr.Game.Class.ME?.user;
                if (user == null)
                    return;

                itemMeta = user.itemMeta ?? new ItemMetaManager(user);
                itemMeta.itemProgress ??= (ArrayObj)ArrayUtils.CreateDyn().array;
                itemMeta.permanentItems ??= (ArrayObj)ArrayUtils.CreateDyn().array;
                user.itemMeta = itemMeta;
            }
            catch
            {
                return;
            }

            if (itemMeta == null)
                return;

            if (MultiplayerSettingsStorage.DebugUseExplorersRune)
            {
                try
                {
                    var runeKey = ExplorerRunePermanentItemId.AsHaxeString();
                    if (!itemMeta.hasPermanentItem(runeKey))
                    {
                        if (itemMeta.addPermanentItem(runeKey))
                        {
                            _debugExplorerRuneInjectedByDebug = true;
                            _debugExplorerRuneInjectedMeta = itemMeta;
                        }
                    }
                }
                catch
                {
                }

                TryRevealAllMinimapForDebugExplorerRune(hero);

                return;
            }

            if (!_debugExplorerRuneInjectedByDebug)
                return;

            try
            {
                var runeKey = ExplorerRunePermanentItemId.AsHaxeString();
                var targetMeta = _debugExplorerRuneInjectedMeta ?? itemMeta;
                var permanentItems = targetMeta?.permanentItems;
                if (permanentItems != null)
                {
                    while (permanentItems.remove(runeKey))
                    {
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _debugExplorerRuneInjectedByDebug = false;
                _debugExplorerRuneInjectedMeta = null;
                _debugExplorerRevealAppliedSignature = string.Empty;
                _nextDebugExplorerRevealRetryTick = 0;
            }
        }

        private void TryRevealAllMinimapForDebugExplorerRune(Hero hero)
        {
            if (_debugExplorerRevealAllCount >= MaxDebugExplorerRevealAllCalls)
                return;

            var now = Stopwatch.GetTimestamp();

            var sig = GetDebugExplorerRevealSignature(hero);
            if (!string.IsNullOrWhiteSpace(sig) &&
                string.Equals(_debugExplorerRevealAppliedSignature, sig, StringComparison.Ordinal))
                return;

            if (_nextDebugExplorerRevealRetryTick != 0 && now < _nextDebugExplorerRevealRetryTick)
                return;

            try
            {
                var feedback = false;
                try
                {
                    // Match the native game flow: reveal rooms + refresh minimap trackers.
                    hero.triggerExplorerInstinct(Ref<bool>.From(ref feedback));
                }
                catch
                {
                }

                var minimap = hero._level?.game?.hud?.minimap ?? dc.ui.HUD.Class.ME?.minimap;
                if (minimap == null)
                {
                    _nextDebugExplorerRevealRetryTick = now + (long)(Stopwatch.Frequency * 0.05);
                    return;
                }

                minimap.revealAll();
                _debugExplorerRevealAllCount++;
                try { minimap.forceRenderRooms(); } catch { }
                try { minimap.invalidateMinimap(); } catch { }

                if (string.IsNullOrWhiteSpace(sig))
                    sig = GetDebugExplorerRevealSignature(hero);

                if (!string.IsNullOrWhiteSpace(sig))
                    _debugExplorerRevealAppliedSignature = sig;

                _nextDebugExplorerRevealRetryTick = 0;
            }
            catch
            {
                _nextDebugExplorerRevealRetryTick = now + (long)(Stopwatch.Frequency * 0.25);
            }
        }

        /// <summary>Level id + branch token so we re-reveal after room/sub-level changes with the same map id.</summary>
        private string GetDebugExplorerRevealSignature(Hero hero)
        {
            if (TryGetCurrentVisibilityContext(out var levelId, out var branch) && branch >= 0 &&
                !string.IsNullOrWhiteSpace(levelId))
                return $"{levelId.Trim()}|{branch}";

            var fallback = GetDebugExplorerRevealLevelKey(hero);
            if (!string.IsNullOrWhiteSpace(fallback))
                return $"{fallback.Trim()}|0";

            return string.Empty;
        }

        private string GetDebugExplorerRevealLevelKey(Hero hero)
        {
            try
            {
                var levelFromHero = hero?._level?.map?.id?.ToString();
                if (!string.IsNullOrWhiteSpace(levelFromHero))
                    return levelFromHero.Trim();
            }
            catch
            {
            }

            var currentLevelId = GetCurrentLevelId();
            if (!string.IsNullOrWhiteSpace(currentLevelId))
                return currentLevelId.Trim();

            return string.Empty;
        }

        private void UpdateGhostHeads()
        {
            var main = dc.Main.Class.ME;
            if (main == null || main.user == null)
            {
                return;
            }
            var ftime = dc.pr.Game.Class.ME.ftime;
            for (int i = 0; i < clientHeads.Length; i++)
            {
                var client = clients[i];
                if (client == null)
                {
                    pendingClientHeadRecreate[i] = false;
                    continue;
                }

                var head = clientHeads[i];
                if (head == null)
                {
                    var hasKnownHead = !string.IsNullOrWhiteSpace(client.RemoteHeadSkinId) ||
                                       !string.IsNullOrWhiteSpace(clientHeadSkins[i]);
                    if (pendingClientHeadRecreate[i] || hasKnownHead)
                        RecreateClientHead(i);
                    continue;
                }

                try
                {
                    head.updateHeadFx(ftime);
                }
                catch
                {
                    pendingClientHeadRecreate[i] = true;
                    RecreateClientHead(i);
                }
            }
        }



        private void SendLevel(string lvl)
        {
            if (_netRole == NetRole.None) return;
            var net = _net;
            if (net == null) return;

            int senderId = net.id;
            if (senderId <= 0) return;
            net.LevelSend(senderId, lvl);
        }

        private void SendRoomTarget(string? targetLevelId, int targetRoomId, bool force)
        {
            if (_netRole == NetRole.None)
                return;

            var net = _net;
            if (net == null || net.id <= 0)
                return;
            if (targetRoomId < 0)
                return;

            var effectiveLevelId = string.IsNullOrWhiteSpace(targetLevelId)
                ? GetCurrentLevelId()
                : targetLevelId.Trim();
            if (string.IsNullOrWhiteSpace(effectiveLevelId))
                return;

            if (!force &&
                string.Equals(_lastDoorMarkerLevelId, effectiveLevelId, StringComparison.Ordinal) &&
                _lastDoorMarkerToken == targetRoomId)
            {
                return;
            }

            net.SendRoomTarget(effectiveLevelId, targetRoomId);
            _lastDoorMarkerLevelId = effectiveLevelId;
            _lastDoorMarkerToken = targetRoomId;
        }

        private void SendCurrentRoomTarget(bool force)
        {
            if (!TryGetCurrentVisibilityContext(out var targetLevelId, out var branchToken))
                return;

            RegisterLocalDoorMarker(targetLevelId, branchToken);
            SendRoomTarget(targetLevelId, branchToken, force);
        }

        private bool TryGetCurrentVisibilityContext(out string levelContextId, out int branchToken)
        {
            levelContextId = GetCurrentLevelId();
            branchToken = 0;

            Level? currentLevel = null;
            try { currentLevel = me?._level; } catch { }
            if (currentLevel == null)
            {
                try { currentLevel = game?.curLevel; } catch { }
            }

            if (currentLevel == null)
                return !string.IsNullOrWhiteSpace(levelContextId);

            try
            {
                var liveLevelId = currentLevel.map?.id?.ToString();
                if (!string.IsNullOrWhiteSpace(liveLevelId))
                    levelContextId = liveLevelId.Trim();
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(levelContextId))
                return false;

            branchToken = ComputeLevelBranchToken(currentLevel, levelContextId);
            return branchToken >= 0;
        }

        private int ComputeLevelBranchToken(Level currentLevel, string levelContextId)
        {
            try
            {
                if (!currentLevel.isSubLevel)
                    return 0;
            }
            catch
            {
                return 0;
            }

            unchecked
            {
                try
                {
                    var ownerGame = currentLevel.game ?? game;
                    var subLevels = ownerGame?.subLevels;
                    if (subLevels != null)
                    {
                        var targetUid = currentLevel.__uid;
                        for (int i = 0; i < subLevels.length; i++)
                        {
                            Level? candidate = null;
                            try { candidate = subLevels.getDyn(i) as Level; } catch { }
                            if (candidate == null)
                                continue;

                            if (ReferenceEquals(candidate, currentLevel))
                                return i + 1;

                            try
                            {
                                if (candidate.__uid == targetUid)
                                    return i + 1;
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                catch
                {
                }

                return ComputeStablePositiveToken($"SUB|{levelContextId}");
            }
        }

        private static int ComputeStablePositiveToken(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return 0;

            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < key.Length; i++)
                {
                    hash ^= key[i];
                    hash *= 16777619;
                }

                var positive = (int)(hash & 0x7FFFFFFF);
                return positive == 0 ? 1 : positive;
            }
        }

        private void RegisterLocalDoorMarker(string? levelId, int markerToken)
        {
            if (markerToken < 0)
                return;

            _localLastDoorMarkerLevelId = string.IsNullOrWhiteSpace(levelId)
                ? string.Empty
                : levelId.Trim();
            _localLastDoorMarkerToken = markerToken;
            _remotePendingDoorMarkers.Clear();
        }

        double last_x, last_y;
        int lastDir;

        private void SendHeroCoords()
        {
            if (_netRole == NetRole.None) return;
            if (_net == null || me == null) return;
            int dir = me.dir;
            if (me.spr.x == last_x && me.spr.y == last_y && lastDir == dir) return;

            _net.TickSend(me.spr.x, me.spr.y, dir);
            last_x = me.spr.x;
            last_y = me.spr.y;
            lastDir = dir;
        }

        public static double[] rLastX = new double[NetNode.MaxClientSlots];
        public static double[] rLastY = new double[NetNode.MaxClientSlots];

        internal static bool TryGetClientIndex(int localId, int remoteId, out int index)
        {
            index = -1;
            if (localId <= 0 || remoteId <= 0 || remoteId == localId)
                return false;

            var mapped = remoteId < localId ? remoteId - 1 : remoteId - 2;
            if (mapped < 0 || mapped >= clients.Length)
                return false;

            index = mapped;
            return true;
        }

        internal static void SetClientSkin(int remoteId, string? skin)
        {
            var instance = Instance;
            if (instance == null)
                return;

            var net = _net;
            var localId = net?.id ?? 0;
            if (!TryGetClientIndex(localId, remoteId, out var index))
                return;

            var cleaned = NormalizeSkin(skin, "PrisonerDefault");
            var prev = clientSkins[index];
            clientSkins[index] = cleaned;

            var client = clients[index];
            if (client != null)
            {
                if (!string.Equals(prev, cleaned, StringComparison.Ordinal) || client.spr == null)
                {
                    try
                    {
                        client.ApplyRemoteSkin(cleaned);
                    }
                    catch (Exception ex)
                    {
                        instance.Logger.Warning(
                            "[NetMod] Failed to apply client skin remoteId={RemoteId} slot={Slot}: {Message}",
                            remoteId,
                            index,
                            ex.Message);

                        if (!string.Equals(cleaned, "PrisonerDefault", StringComparison.Ordinal))
                        {
                            try
                            {
                                client.ApplyRemoteSkin("PrisonerDefault");
                                clientSkins[index] = "PrisonerDefault";
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
        }

        internal static void SetClientHeadSkin(int remoteId, string? skin)
        {
            var instance = Instance;
            if (instance == null)
                return;

            var net = _net;
            var localId = net?.id ?? 0;
            if (!TryGetClientIndex(localId, remoteId, out var index))
                return;

            var cleaned = NormalizeSkin(skin, "BaseFlame");
            var prev = clientHeadSkins[index];
            clientHeadSkins[index] = cleaned;

            var client = clients[index];
            if (client != null)
                client.RemoteHeadSkinId = cleaned;

            if (!string.Equals(prev, cleaned, StringComparison.Ordinal) || client?.head == null)
                instance.RecreateClientHead(index);
        }

        private static string NormalizeSkin(string? skin, string defaultSkin)
        {
            return string.IsNullOrWhiteSpace(skin) ? defaultSkin : skin.Replace("|", "/").Trim();
        }

        private void RecreateClientHead(int slot)
        {
            if (slot < 0 || slot >= clients.Length)
                return;

            var client = clients[slot];
            var localHero = me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            var localLevel = localHero?._level;
            if (client == null || localHero == null || localLevel == null || client.spr == null)
            {
                pendingClientHeadRecreate[slot] = true;
                return;
            }

            var existing = clientHeads[slot];
            if (existing != null)
            {
                try { existing.dispose(); } catch { }
                clientHeads[slot] = null;
            }

            var desiredHead = NormalizeSkin(client.RemoteHeadSkinId, "BaseFlame");
            var previousGlobalHead = remoteHeadSkin;
            remoteHeadSkin = desiredHead;
            try
            {
                bool fromUI = false;
                var attachRoot = new dc.h2d.Object(client.spr);
                var newHead = new Kinghead(localHero, client, localLevel, Logger);
                newHead.init(localLevel, attachRoot, Ref<bool>.From(ref fromUI));
                clientHeads[slot] = newHead;
                client.head = newHead;
                pendingClientHeadRecreate[slot] = false;
            }
            catch (Exception ex)
            {
                pendingClientHeadRecreate[slot] = true;
                Logger.Warning("[NetMod] Failed to recreate client head slot {slot}: {msg}", slot, ex.Message);
            }
            finally
            {
                remoteHeadSkin = previousGlobalHead;
            }
        }

        private void ReceiveGhostCoords()
        {
            var net = _net;
            var ghost = _ghost;
            if (net == null || me == null || ghost == null) return;

            if (!net.TryConsumeRemoteSnapshot(out var remotes))
                return;

            var localId = net.id;
            var localLevelId = GetCurrentLevelId();
            if (string.IsNullOrWhiteSpace(localLevelId))
            {
                try { localLevelId = me._level?.map?.id?.ToString() ?? string.Empty; }
                catch { localLevelId = string.Empty; }
            }

            foreach (var remote in remotes)
            {
                if (!TryGetClientIndex(localId, remote.Id, out var index))
                    continue;

                remotePlayerId = remote.Id;
                clientIds[index] = remote.Id;
                ProcessRemoteDoorMarker(remote);
                if (!ShouldKeepRemoteKingVisibleInRoom(remote, localLevelId))
                {
                    QueueClientDisposeWithTransition(index);
                    continue;
                }

                CancelPendingClientDispose(index);

                var client = EnsureClientKingSlot(index);
                if (client == null)
                    continue;

                var drawX = remote.X;
                var drawY = remote.Y - 0.2d;
                var useDownedOffset = false;
                if (_remoteDowned.TryGetValue(remote.Id, out var downed))
                {
                    var currentLevelId = GetCurrentLevelId();
                    if (string.IsNullOrEmpty(currentLevelId) ||
                        string.IsNullOrEmpty(downed.LevelId) ||
                        string.Equals(currentLevelId, downed.LevelId, StringComparison.Ordinal))
                    {
                        drawX = downed.X;
                        drawY = downed.Y;
                        useDownedOffset = true;
                        if (_remoteDownedCines.TryGetValue(remote.Id, out var downedCine) &&
                            downedCine != null &&
                            !downedCine.destroyed)
                        {
                            downedCine.UpdateTarget(drawX, drawY, remote.Dir);
                        }
                    }
                }

                if (useDownedOffset)
                {
                    drawY -= DownedGhostBodyYOffsetPx;
                    try { client._targetable = false; } catch { }
                }
                else
                {
                    try { client._targetable = true; } catch { }
                }

                client.setPosPixel(drawX, drawY);
                client.dir = remote.Dir;
                rLastX[index] = drawX;
                rLastY[index] = drawY;

                var newLabel = BuildRemoteLabel(remote.Id, remote.Username);
                if (!string.Equals(clientLabels[index], newLabel, StringComparison.Ordinal))
                {
                    ghost.SetLabel(client, newLabel);
                    clientLabels[index] = newLabel;
                }

                if (remote.HasAnim && !string.IsNullOrWhiteSpace(remote.Anim))
                    PlayGhostAnim(client, remote.Anim!, remote.AnimQueue, remote.AnimG);
                if(remote.HasHeadAnim && !string.IsNullOrWhiteSpace(remote.HeadAnim))
                    PlayGhostHeadAnim(client, remote.HeadAnim);
            }
        }

        private bool ShouldKeepRemoteKingVisibleInRoom(NetNode.RemoteSnapshot remote, string localLevelId)
        {
            if (!string.IsNullOrWhiteSpace(localLevelId) &&
                !string.IsNullOrWhiteSpace(remote.LevelId) &&
                !string.Equals(remote.LevelId, localLevelId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!remote.HasRoom ||
                !remote.RoomId.HasValue ||
                remote.RoomId.Value < 0 ||
                string.IsNullOrWhiteSpace(remote.RoomLevelId))
            {
                return true;
            }

            if (!TryGetCurrentVisibilityContext(out var localContextLevelId, out var localBranchToken))
            {
                localContextLevelId = localLevelId;
                localBranchToken = _localLastDoorMarkerToken >= 0 ? _localLastDoorMarkerToken : 0;
            }

            var remoteContextLevelId = remote.RoomLevelId.Trim();
            if (!string.Equals(remoteContextLevelId, localContextLevelId, StringComparison.Ordinal))
                return false;

            if (remote.RoomId.Value != localBranchToken)
                return false;

            return true;
        }

        private void ProcessRemoteDoorMarker(NetNode.RemoteSnapshot remote)
        {
            if (!remote.HasRoom ||
                !remote.RoomId.HasValue ||
                remote.RoomId.Value < 0 ||
                string.IsNullOrWhiteSpace(remote.RoomLevelId))
            {
                return;
            }

            var markerToken = remote.RoomId.Value;
            var markerLevelId = remote.RoomLevelId.Trim();
            if (string.IsNullOrWhiteSpace(markerLevelId))
                return;

            if (_remoteLastDoorMarkers.TryGetValue(remote.Id, out var last) &&
                last != null &&
                last.MarkerToken == markerToken &&
                string.Equals(last.LevelId, markerLevelId, StringComparison.Ordinal))
            {
                return;
            }

            _remoteLastDoorMarkers[remote.Id] = new RemoteDoorMarkerState
            {
                MarkerToken = markerToken,
                LevelId = markerLevelId,
                UpdatedAtTicks = Stopwatch.GetTimestamp()
            };
            _remotePendingDoorMarkers.Remove(remote.Id);
        }

        private void QueueClientDisposeWithTransition(int slot)
        {
            if (slot < 0 || slot >= clients.Length)
                return;

            var client = clients[slot];
            if (client == null)
            {
                DisposeClientSlot(slot, clearIdentity: false);
                return;
            }

            if (!_pendingClientDisposeTicks.TryGetValue(slot, out var startedAtTicks))
            {
                _pendingClientDisposeTicks[slot] = Stopwatch.GetTimestamp();
                try { client.spr?._animManager?.play("walkOut".AsHaxeString(), null, null); } catch { }
                return;
            }

            var elapsed = Stopwatch.GetElapsedTime(startedAtTicks).TotalSeconds;
            if (elapsed < ClientDisposeTransitionSeconds)
                return;

            DisposeClientSlot(slot, clearIdentity: false);
        }

        private void CancelPendingClientDispose(int slot)
        {
            _pendingClientDisposeTicks.Remove(slot);
        }

        private GhostKing? EnsureClientKingSlot(int slot)
        {
            if (slot < 0 || slot >= clients.Length)
                return null;

            var existing = clients[slot];
            if (existing != null)
                return existing;

            if (_ghost == null || me == null || me._level == null)
                return null;

            var created = _ghost.CreateGhostKing(me._level);
            clients[slot] = created;

            var knownSkin = clientSkins[slot];
            if (!string.IsNullOrWhiteSpace(knownSkin))
                created.ApplyRemoteSkin(knownSkin);

            var knownHead = clientHeadSkins[slot];
            created.RemoteHeadSkinId = NormalizeSkin(
                !string.IsNullOrWhiteSpace(knownHead) ? knownHead : remoteHeadSkin,
                "BaseFlame");
            RecreateClientHead(slot);

            if (!string.IsNullOrWhiteSpace(clientLabels[slot]))
                _ghost.SetLabel(created, clientLabels[slot]);

            ApplyCachedRemoteDiveSkillInfoIfAny(clientIds[slot], created);

            return created;
        }

        private void DisposeClientSlot(int slot, bool clearIdentity)
        {
            if (slot < 0 || slot >= clients.Length)
                return;

            _pendingClientDisposeTicks.Remove(slot);

            var previousRemoteId = clientIds[slot];

            var head = clientHeads[slot];
            if (head != null)
            {
                try { head.dispose(); } catch { }
                clientHeads[slot] = null;
            }
            pendingClientHeadRecreate[slot] = false;

            var client = clients[slot];
            if (client != null)
            {
                try { client.destroy(); } catch { }
                try { client.dispose(); } catch { }
                try { client.disposeGfx(); } catch { }
            }
            clients[slot] = null!;

            if (!clearIdentity)
                return;

            if (previousRemoteId > 0)
            {
                _remoteLastDoorMarkers.Remove(previousRemoteId);
                _remotePendingDoorMarkers.Remove(previousRemoteId);
                ClearCachedRemoteDiveSkillInfo(previousRemoteId);
            }

            clientIds[slot] = 0;
            clientLabels[slot] = null;
            rLastX[slot] = 0;
            rLastY[slot] = 0;
        }

        private void ReceiveGhostWeapons()
        {
            var net = _net;
            if (net == null || me == null) return;

            if (!net.TryConsumeRemoteWeaponSnapshots(out var updates))
                return;

            foreach (var update in updates)
            {
                ApplyRemoteWeaponUpdate(update.Id, update.Kind, update.Slot, update.PermanentId, update.Ammo);
            }
        }

        private void DrainRemoteCombatQueuesAfterLevelChange()
        {
            var net = _net;
            if (net == null)
                return;

            try { net.TryConsumeRemoteWeaponSnapshots(out _); } catch { }
            try { net.TryConsumeRemoteAttacks(out _); } catch { }
        }

        private void ReceiveGhostAttacks()
        {
            var net = _net;
            if (net == null || me == null) return;

            if (!net.TryConsumeRemoteAttacks(out var attacks))
                return;

            var localId = net.id;
            foreach (var attack in attacks)
            {
                if (TryHandleRemoteDiveAttack(attack, localId))
                    continue;

                if (attack.Slot < 0 &&
                    (string.IsNullOrWhiteSpace(attack.Kind) ||
                     attack.Kind.StartsWith("__", StringComparison.Ordinal)))
                {
                    continue;
                }

                ApplyRemoteWeaponUpdate(attack.Id, attack.Kind, attack.Slot, attack.PermanentId, attack.Ammo);
                if (!TryGetClientIndex(localId, attack.Id, out var index))
                    continue;

                var client = clients[index];
                if (client?.kingWeaponsManager == null) continue;
                if (attack.Action == RemoteAttackAction.Interrupt)
                    client.kingWeaponsManager.queueInterrupt(attack.Slot);
                else
                    client.kingWeaponsManager.queueAttack(attack.Slot);
            }
        }

        private void UpdateGhostWeapons()
        {
            for (int i = 0; i < clients.Length; i++)
            {
                var client = clients[i];
                if (client?.kingWeaponsManager == null) continue;
                client.kingWeaponsManager.update();
            }
        }

        private void PlayGhostAnim(GhostKing client, string anim, int? queueAnim, bool? g)
        {
            if (client?.spr?._animManager == null) return;
            if (string.IsNullOrWhiteSpace(anim)) return;
            var shieldActive = client.kingWeaponsManager != null && client.kingWeaponsManager.IsShieldActive;
            if (shieldActive && ShouldLoopRemoteAnim(anim))
            {
                return;
            }

            if (anim.IndexOf("hold", StringComparison.OrdinalIgnoreCase) >= 0 ||
                anim.IndexOf("shield", StringComparison.OrdinalIgnoreCase) >= 0 ||
                anim.IndexOf("parry", StringComparison.OrdinalIgnoreCase) >= 0 ||
                anim.IndexOf("block", StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            var animManager = client.spr._animManager;
            try
            {
                var current = client.spr.groupName;
                if(current != null && string.Equals(current.ToString(), anim, StringComparison.Ordinal))
                    return;
            }
            catch
            {
            }

            if (ShouldLoopRemoteAnim(anim))
            {
                if (!shieldActive)
                {
                    try { client.removeAllAffects(96); } catch { }
                    try { client.removeAllAffects(98); } catch { }
                    try { client.removeAllAffects(99); } catch { }
                }
                animManager.play(anim.AsHaxeString(), null, null).loop(null);
                return;
            }
            animManager.play(anim.AsHaxeString(), queueAnim, g).stopOnLastFrame(Ref<bool>.Null);
        }

        private static bool ShouldLoopRemoteAnim(string anim)
        {
            if(string.IsNullOrWhiteSpace(anim)) return false;
            var a = anim.Trim();

            // Don't ever force-loop weapon/hold-ish states; those should be driven by weapon replication.
            if(IsAttackAnim(a)) return false;
            if(a.IndexOf("guard", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if(a.IndexOf("defend", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            if (a.StartsWith("idle", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.StartsWith("run", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.StartsWith("walk", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.IndexOf("move", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("jump", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("fall", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("land", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("climb", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("ladder", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("crouch", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("volte", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("remain", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        private void PlayGhostHeadAnim(GhostKing client, string anim)
        {
            if (client == null || client?.head == null || client?.head?.customHeadSpr._animManager == null) return;
            if (string.IsNullOrWhiteSpace(anim)) return;
            var animManager = client.head.customHeadSpr._animManager;
            animManager.play(anim.AsHaxeString(), null, null).loop(null);
            animManager.genSpeed = 0.4;
        }

        private void SendHeroAnim(string anim, int? queueAnim, bool? g, bool force = false)
        {
            if (_netRole == NetRole.None) return;
            var net = _net;
            if (net == null || string.IsNullOrWhiteSpace(anim)) return;
            if (!force &&
                string.Equals(_lastAnimSent, anim, StringComparison.Ordinal) &&
                _lastAnimQueueSent == queueAnim &&
                _lastAnimGSent == g)
                return;

            net.SendAnim(anim, queueAnim, g);
            _lastAnimSent = anim;
            _lastAnimQueueSent = queueAnim;
            _lastAnimGSent = g;
        }


        private void SendHeadAnim(string anim)
        {
            if (_netRole == NetRole.None) return;
            var net = _net;
            if (net == null || string.IsNullOrWhiteSpace(anim)) return;
            net.SendHeadAnim(anim);
        }

        private void SendEquippedWeapons(Inventory inv)
        {
            if (_netRole == NetRole.None || inv == null) return;
            var w0 = inv.getEquippedWeaponOn(0);
            if (w0 != null)
                SendInventoryWeapon(w0, 0);
            var w1 = inv.getEquippedWeaponOn(1);
            if (w1 != null)
                SendInventoryWeapon(w1, 1);
        }

        private void SendInventoryWeapon(InventItem item, int slot)
        {
            if (_netRole == NetRole.None) return;
            if (item == null) return;
            if (!TryGetWeaponKindId(item, out var kindId)) return;
            var net = _net;
            if (net == null || string.IsNullOrWhiteSpace(kindId)) return;
            net.SendInventoryWeapon(kindId!, slot, item.permanentId, GetWeaponAmmoForSync(item));
        }

        private static bool TryGetWeaponKindId(InventItem item, out string? kindId)
        {
            kindId = null;
            if (item == null) return false;
            var kind = item.kind;
            if (kind is InventItemKind.Weapon w)
            {
                kindId = w.Param0?.ToString();
                return !string.IsNullOrWhiteSpace(kindId);
            }
            return false;
        }

        private static int? GetWeaponAmmoForSync(InventItem? item)
        {
            if(item == null)
                return null;

            try
            {
                var maxAmmo = item.getMaxAmmo();
                if(maxAmmo <= 0)
                    return null;

                var ammo = item.ammo;
                if(ammo < 0) ammo = 0;
                if(ammo > maxAmmo) ammo = maxAmmo;
                return ammo;
            }
            catch
            {
                return null;
            }
        }

        private static int GetWeaponSlot(Inventory inv, InventItem item)
        {
            if (inv == null || item == null) return -1;
            var id = item.permanentId;
            var w0 = inv.getEquippedWeaponOn(0);
            if (w0 != null && w0.permanentId == id) return 0;
            var w1 = inv.getEquippedWeaponOn(1);
            if (w1 != null && w1.permanentId == id) return 1;
            return item.posID;
        }

        private bool IsLocalInventory(Inventory self)
        {
            return me != null && self != null && ReferenceEquals(self, me.inventory);
        }

        private void ApplyRemoteWeaponUpdate(int remoteId, string? kindId, int slot, int permanentId, int? ammo = null)
        {
            if (string.IsNullOrWhiteSpace(kindId)) return;
            var net = _net;
            var localId = net?.id ?? 0;
            if (!TryGetClientIndex(localId, remoteId, out var index))
                return;

            var client = clients[index];
            if (client?.inventory == null) return;

            var cleaned = kindId.Replace("|", "/").Trim();
            if (cleaned.Length == 0) return;

            var inv = client.inventory;
            var existing = permanentId != 0 ? inv.getByPermanentId(permanentId) : null;
            var currentSlotItem = slot >= 0 ? inv.getEquippedWeaponOn(slot) : null;

            if(existing == null && permanentId == 0)
            {
                if(IsWeaponKindMatch(currentSlotItem, cleaned))
                    existing = currentSlotItem;
                else if (slot < 0)
                {
                    var w0 = inv.getEquippedWeaponOn(0);
                    if(IsWeaponKindMatch(w0, cleaned))
                        existing = w0;
                    else
                    {
                        var w1 = inv.getEquippedWeaponOn(1);
                        if(IsWeaponKindMatch(w1, cleaned))
                            existing = w1;
                    }
                }
            }

            if (existing == null)
            {
                var newItem = new InventItem(new InventItemKind.Weapon(cleaned.AsHaxeString()));
                if (permanentId != 0)
                    newItem.permanentId = permanentId;
                if (slot >= 0)
                    newItem.posID = slot;
                _inventorySyncGuard = true;
                try
                {
                    if(currentSlotItem != null)
                        currentSlotItem.posID = -1;
                    inv.add(newItem);
                }
                finally
                {
                    _inventorySyncGuard = false;
                }
                existing = newItem;
            }
            else if(currentSlotItem != null &&
                    !ReferenceEquals(currentSlotItem, existing) &&
                    (currentSlotItem.permanentId == 0 ||
                     existing.permanentId == 0 ||
                     currentSlotItem.permanentId != existing.permanentId))
            {
                currentSlotItem.posID = -1;
            }

            if (slot >= 0)
                existing.posID = slot;

            _inventorySyncGuard = true;
            try
            {
                inv.equip(existing);
                ApplyRemoteWeaponAmmo(existing, ammo);
            }
            finally
            {
                _inventorySyncGuard = false;
            }
        }

        private static void ApplyRemoteWeaponAmmo(InventItem item, int? ammo)
        {
            if(item == null || !ammo.HasValue)
                return;

            try
            {
                var maxAmmo = item.getMaxAmmo();
                if(maxAmmo <= 0)
                    return;

                var value = ammo.Value;
                if(value < 0) value = 0;
                if(value > maxAmmo) value = maxAmmo;
                item.ammo = value;
            }
            catch
            {
            }
        }

        private static bool IsWeaponKindMatch(InventItem? item, string expectedKindId)
        {
            if(item == null || string.IsNullOrWhiteSpace(expectedKindId))
                return false;
            if(!TryGetWeaponKindId(item, out var itemKindId) || string.IsNullOrWhiteSpace(itemKindId))
                return false;
            return string.Equals(itemKindId, expectedKindId, StringComparison.Ordinal);
        }

        private void ResetLocalSkinSendCache()
        {
            _lastSentHeroSkin = null;
            _lastSentHeroHeadSkin = null;
        }

        private void ResetDoorMarkerState()
        {
            _lastDoorMarkerLevelId = string.Empty;
            _lastDoorMarkerToken = int.MinValue;
            _localLastDoorMarkerLevelId = string.Empty;
            _localLastDoorMarkerToken = int.MinValue;
            _remoteLastDoorMarkers.Clear();
            _remotePendingDoorMarkers.Clear();
            _pendingClientDisposeTicks.Clear();
        }

        private void ResetNetworkState()
        {
            ResetFakeDeathState(unlockLocalHero: true, sendNetworkUpState: false);
            ResetLocalSkinSendCache();
            ResetDoorMarkerState();
            _lastSentDiveInfoPayload = string.Empty;
            _remoteDiveInfoPayloadById.Clear();
            _lastLocalDiveStartSendTicks = 0;
            _lastLocalDiveLandSendTicks = 0;
            _lastDiveInfoScanTicks = 0;
        }

        private IPEndPoint BuildEndpoint(string ipText, int port)
        {
            if (port <= 0 || port > 65535) port = 1234;
            if (!IPAddress.TryParse(ipText, out var ip))
            {
                ip = IPAddress.Loopback;
            }
            return new IPEndPoint(ip, port);
        }

        public void StartHostFromMenu(string ipText, int port)
        {
            var ep = BuildEndpoint(ipText, port);
            StartHostWithEndpoint(ep);
        }

        public void StartClientFromMenu(string ipText, int port)
        {
            var ep = BuildEndpoint(ipText, port);
            StartClientWithEndpoint(ep);
        }

        public void StartSteamHostFromMenu(int hostPort)
        {
            StartHostWithSteamTransport(hostPort);
        }

        public void StartSteamClientFromMenu(ulong hostSteamId)
        {
            StartClientWithSteamTransport(hostSteamId);
        }

        private void StartHostCore(Action createHost)
        {
            _net?.Dispose();
            ResetNetworkState();
            createHost();
            _netRole = NetRole.Host;
            GameMenu.SetRole(_netRole);
            GameMenu.NetRef = _net;
            ConnectionUI.NotifyConnectionsChanged();
        }

        private void StartHostWithEndpoint(IPEndPoint ep)
        {
            try
            {
                StartHostCore(() => _net = NetNode.CreateHost(Logger, ep));
                var lep = _net?.ListenerEndpoint;
                if (lep != null)
                    Logger.Information($"[NetMod] Host listening at {lep.Address}:{lep.Port}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[NetMod] Host start failed: {ex.Message}");
                _netRole = NetRole.None;
                _net = null;
                GameMenu.SetRole(_netRole);
            }
        }

        private void StartClientCore(Action createClient)
        {
            _net?.Dispose();
            try
            {
                var main = dc.Main.Class.ME;
                if (main?.user != null)
                    GameDataSync.RestoreOriginalUserState(main.user, true);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "[NetMod] RestoreOriginalUserState failed before client start");
            }
            ResetNetworkState();
            createClient();
            _netRole = NetRole.Client;
            GameMenu.SetRole(_netRole);
            GameMenu.NetRef = _net;
            ConnectionUI.NotifyConnectionsChanged();
        }

        private void StartClientWithEndpoint(IPEndPoint ep)
        {
            try
            {
                StartClientCore(() => _net = NetNode.CreateClient(Logger, ep));
                Logger.Information($"[NetMod] Client connecting to {ep.Address}:{ep.Port}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[NetMod] Client start failed: {ex.Message}");
                _netRole = NetRole.None;
                _net = null;
                GameMenu.SetRole(_netRole);
            }
        }

        private void StartHostWithSteamTransport(int hostPort)
        {
            if (!_ready)
            {
                Logger.Warning("[NetMod] Steam host start rejected: OnGameEndInit not yet run");
                return;
            }
            try
            {
                StartHostCore(() => _net = NetNode.CreateSteamHost(Logger, hostPort));
                Logger.Information("[NetMod] Host started with Steam P2P transport");
            }
            catch (Exception ex)
            {
                Logger.Error($"[NetMod] Steam host start failed: {ex.Message}");
                _netRole = NetRole.None;
                _net = null;
                GameMenu.SetRole(_netRole);
            }
        }

        private void StartClientWithSteamTransport(ulong hostSteamId)
        {
            if (!_ready)
            {
                Logger.Warning("[NetMod] Steam client start rejected: OnGameEndInit not yet run");
                return;
            }
            try
            {
                StartClientCore(() => _net = NetNode.CreateSteamClient(Logger, hostSteamId));
                Logger.Information("[NetMod] Client connecting via Steam P2P to hostSteamId={HostSteamId}", hostSteamId);
            }
            catch (Exception ex)
            {
                Logger.Error($"[NetMod] Steam client start failed: {ex.Message}");
                _netRole = NetRole.None;
                _net = null;
                GameMenu.SetRole(_netRole);
            }
        }

        public void StopNetworkFromMenu()
        {
            var roleBeforeStop = _netRole;
            try
            {
                if (roleBeforeStop == NetRole.Client)
                {
                    Logger.Information("[NetMod] Disconnecting client from host...");
                    _net?.SendControlAndFlush("BYE", 500);
                }
                else if (roleBeforeStop == NetRole.Host)
                {
                    Logger.Information("[NetMod] Disposing host server...");
                    _net?.SendControlAndFlush("KICK", 500);
                }

                _net?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "[NetMod] Error during network stop/dispose");
            }
            ResetNetworkState();
            _net = null;
            _netRole = NetRole.None;
            GameMenu.NetRef = null;
            GameMenu.SetRole(_netRole);
            ConnectionUI.NotifyConnectionsChanged();
        }


    }
}
