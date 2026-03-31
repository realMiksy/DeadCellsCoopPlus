using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using ModCore.Events;
using dc;
using dc.haxe.ds;
using dc.level;
using dc.pr;
using dc.tool;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using ModCore.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using dc.haxe;
using dc.haxe.io;
using dc.hl.types;
using Rand = dc.libs.Rand;

namespace DeadCellsMultiplayerMod
{
    internal partial class GameDataSync : IEventReceiver, IOnAdvancedModuleInitializing
    {
        static Serilog.ILogger _log;
        static public int Seed;
        private static readonly bool EnableStoryManagerSync = false;

        // When false, host does not send PROGRESS (packed user) to clients.
        private static readonly bool EnableSendHostUserProgress = false;

        static public virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_ _isTwitch;
        static public bool _isCustom;
        static public bool _mode;

        static public LaunchMode _launch;
        private static readonly object _bossRuneLock = new();
        private static int? _remoteBossRune;
        private static int? _hostBossRune;
        private static string? _remoteProgressPayload;
        public static string? HostProgressPayload;
        private static bool _origProgressCaptured;
        private static string? _origProgressPayload;
        private static bool _origHeroCosmeticsCaptured;
        private static string? _origHeroSkin;
        private static string? _origHeroHeadSkin;
        private static User? _cachedBuiltProgressPayloadUser;
        private static string? _cachedBuiltProgressPayload;
        private static NetNode? _lastProgressSyncNet;
        private static string? _lastProgressSyncPayload;
        private static NetNode? _lastHeroSkinSyncNet;
        private static string? _lastHeroSkinSyncPayload;
        private static NetNode? _lastHeroHeadSkinSyncNet;
        private static string? _lastHeroHeadSkinSyncPayload;

        private static string? _remoteCountersPayload;
        public static string? HostCountersPayload;
        private static string? _remoteBlueprintsPayload;
        public static string? HostBlueprintsPayload;
        private static bool _hasRemoteCounters;
        private static bool _hasRemoteBlueprints;
        private static bool _origStoryCaptured;
        private static bool _origStoryWasNull;
        private static StoryManager? _origStory;
        private static StringMap? _origCounters;
        private static readonly Dictionary<string, int> _origCountersSnapshot = new(StringComparer.Ordinal);
        private static readonly Dictionary<int, int> _origNpcProgressSnapshot = new();
        private static readonly Dictionary<string, int> _origLoreRoomRunIdsSnapshot = new(StringComparer.Ordinal);
        private static readonly HashSet<string> _origVisitedLoreRoomsSnapshot = new(StringComparer.Ordinal);
        private static readonly List<int> _origPlannedLoresSnapshot = new();
        private static int _origStoryDataVersion;
        private static bool _sessionStoryCaptured;
        private static bool _sessionStoryWasNull;
        private static readonly Dictionary<string, int> _sessionCountersSnapshot = new(StringComparer.Ordinal);
        private static readonly Dictionary<int, int> _sessionNpcProgressSnapshot = new();
        private static readonly Dictionary<string, int> _sessionLoreRoomRunIdsSnapshot = new(StringComparer.Ordinal);
        private static readonly HashSet<string> _sessionVisitedLoreRoomsSnapshot = new(StringComparer.Ordinal);
        private static readonly List<int> _sessionPlannedLoresSnapshot = new();
        private static int _sessionStoryDataVersion;
        private static bool _origItemMetaCaptured;
        private static ItemMetaManager? _origItemMeta;
        private static ArrayObj? _origItemProgress;
        private static ArrayObj? _origPermanentItems;
        private static bool _origItemMetaWasNull;
        private static bool _origBossRuneCaptured;
        private static int _origBossRune;
        private static bool _hasRemoteBossRune;
        private static bool _suppressDeathBroadcast;
        private static readonly object _levelSeedLock = new();
        private static string? _remoteLevelId;
        private static double? _remoteLevelSeed;
        private static readonly object _serializerSyncLock = new();
        private static int _remoteSerializerSeq;
        private static int _remoteSerializerUid;
        private static bool _hasRemoteSerializerSync;
        private static bool _hasRemoteSerializerValues;
        private static bool _localSerializerCaptured;
        private static int _localSerializerSeq;
        private static int _localSerializerUid;
        private static readonly Dictionary<string, int> _remoteCountersSnapshot = new(StringComparer.Ordinal);
        private static readonly Dictionary<int, int> _remoteNpcProgressSnapshot = new();
        private static readonly Dictionary<string, int> _remoteLoreRoomRunIdsSnapshot = new(StringComparer.Ordinal);
        private static readonly HashSet<string> _remoteVisitedLoreRoomsSnapshot = new(StringComparer.Ordinal);
        private static readonly HashSet<int> _remotePlannedLoresSnapshot = new();
        private static int _remoteStoryDataVersion;
        private static bool _hasRemoteStoryDataVersion;

        public GameDataSync(Serilog.ILogger log)
        {
            _log = log;
            EventSystem.AddReceiver(this);
        }

        void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
        {
            entry.Logger.Information("\x1b[32m[[ModEntry.GameDataSync] Initializing GameDataSync...]\x1b[0m ");
        }


        

        public static void user_hook_new_game(Hook_User.orig_newGame orig,
        User self,
        int lvl,
        virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_ isTwitch,
        bool isCustom,
        bool mode,
        LaunchMode gdata)
        {
            isCustom = false;
            mode = false;
            Seed = lvl;
            ModEntry.me = null;
            ModEntry.ResetClientSlots();
            ModEntry.kingInitialized = false;
            ModEntry._ghost = null;
            var net = GameMenu.NetRef;
            var shouldSynchronizeSeed = ShouldSynchronizeRunSeed(gdata);
            var appliedRemoteProgressBeforeNewGame = false;
            var appliedRemoteFallbackBeforeNewGame = false;

            if (net == null || !net.IsAlive)
                RestoreOriginalUserState(self, true);

            if (net != null && net.IsHost)
            {
                MarkProgressPayloadDirty();
                if (shouldSynchronizeSeed)
                    Seed = GameMenu.ForceGenerateServerSeed("NewGame_hook");
                else
                    Seed = lvl;
                SendBossRune(self, net);
                SendSerializerSync(net);
                if (shouldSynchronizeSeed)
                    net.SendSeed(Seed);
                SendProgressSync(self, net);
            }
            else if (net != null)
            {
                if (shouldSynchronizeSeed && GameMenu.TryGetRemoteSeed(out var remoteSeed))
                {
                    Seed = remoteSeed;
                }
                else
                {
                    Seed = lvl;
                }
                if (TryGetRemoteBossRune(out var bossRune))
                {
                    ApplyRemoteBossRune(self, bossRune);
                }
                else
                {
                    _log?.Warning("[NetMod] Remote boss rune not received yet");
                }

                CaptureOriginalUserData(self, allowReplaceWhenBetter: true);
                if (!string.IsNullOrEmpty(_remoteProgressPayload))
                {
                    ReceiveProgressSync(_remoteProgressPayload, self);
                    appliedRemoteProgressBeforeNewGame = true;
                }
                else
                {
                    if (!string.IsNullOrEmpty(_remoteBlueprintsPayload))
                    {
                        ReceiveBlueprints(_remoteBlueprintsPayload, self);
                        appliedRemoteFallbackBeforeNewGame = true;
                    }

                    if (!string.IsNullOrEmpty(_remoteCountersPayload))
                    {
                        ReceiveCounters(_remoteCountersPayload, self);
                        appliedRemoteFallbackBeforeNewGame = true;
                    }
                }
            }
            lvl = Seed;
            _isTwitch = isTwitch;
            _isCustom = isCustom;
            _mode = mode;
            _launch = gdata;
            self.pickDeathItem();
            SendHeroSkin(self, net);
            SendHeroHeadSkin(self, net);
            orig(self, lvl, isTwitch, isCustom, mode, gdata);

            if (net != null && net.IsHost)
            {
                MarkProgressPayloadDirty();
                SendProgressSync(self, net);
            }
            else if (net != null)
            {
                if (!appliedRemoteProgressBeforeNewGame && !string.IsNullOrEmpty(_remoteProgressPayload))
                    ReceiveProgressSync(_remoteProgressPayload, self);
                else if (!appliedRemoteFallbackBeforeNewGame && !string.IsNullOrEmpty(_remoteBlueprintsPayload))
                    ReceiveBlueprints(_remoteBlueprintsPayload, self);
                if (!appliedRemoteProgressBeforeNewGame && !appliedRemoteFallbackBeforeNewGame && string.IsNullOrEmpty(_remoteProgressPayload) && !string.IsNullOrEmpty(_remoteCountersPayload))
                    ReceiveCounters(_remoteCountersPayload, self);
            }
        }

        private static bool ShouldSynchronizeRunSeed(LaunchMode? launch)
        {
            return launch is LaunchMode.NewGame;
        }

        public static void MarkProgressPayloadDirty()
        {
            _cachedBuiltProgressPayloadUser = null;
            _cachedBuiltProgressPayload = null;
        }

        private static string? GetCurrentProgressPayload(User user)
        {
            if (user == null)
                return null;

            if (!string.IsNullOrWhiteSpace(_cachedBuiltProgressPayload) &&
                ReferenceEquals(_cachedBuiltProgressPayloadUser, user))
            {
                return _cachedBuiltProgressPayload;
            }

            var payload = BuildProgressPayload(user);
            if (string.IsNullOrWhiteSpace(payload))
                return null;

            _cachedBuiltProgressPayloadUser = user;
            _cachedBuiltProgressPayload = payload;
            return payload;
        }

        public static void ReceiveBlueprints(string payload, User? target = null)
        {
            _remoteBlueprintsPayload = payload;
            if (string.IsNullOrEmpty(payload))
                return;

            void apply(User user)
            {
                CaptureOriginalUserData(user);
                var meta = EnsureItemMeta(user, user.itemMeta);
                var arr = CloneItemProgress(meta.itemProgress) ?? EnsureArray(null);
                var existing = new Dictionary<string, ItemProgress>(StringComparer.Ordinal);
                for (int i = 0; i < arr.length; i++)
                {
                    var progress = arr.getDyn(i) as ItemProgress;
                    var id = progress?.itemId?.ToString();
                    if (progress != null && !string.IsNullOrWhiteSpace(id))
                        existing[id] = progress;
                }
                var permanent = CloneItemList(meta.permanentItems) ?? EnsureArray(null);
                var existingPermanent = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < permanent.length; i++)
                {
                    var permanentId = permanent.getDyn(i)?.ToString();
                    if (!string.IsNullOrWhiteSpace(permanentId))
                        existingPermanent.Add(permanentId);
                }
                _hasRemoteBlueprints = true;

                var isV2 = payload.Equals("V2", StringComparison.Ordinal) || payload.StartsWith("V2|", StringComparison.Ordinal);
                ForEachEscapedToken(payload, token =>
                {
                    if (string.IsNullOrWhiteSpace(token))
                        return;

                    if (isV2)
                    {
                        if (token.Equals("V2", StringComparison.Ordinal))
                            return;

                        if (token.StartsWith("I:", StringComparison.Ordinal))
                        {
                            var parts = token.Split(':');
                            if (parts.Length < 5)
                                return;

                            var itemId = DecodeToken(parts[1]);
                            if (string.IsNullOrWhiteSpace(itemId))
                                return;

                            if (!existing.TryGetValue(itemId, out var progress) || progress == null)
                            {
                                progress = new ItemProgress(itemId.AsHaxeString());
                                arr.pushDyn(progress);
                                existing[itemId] = progress;
                            }

                            progress.investedCells = ParseInt(parts[2], ToInt(progress.investedCells));
                            progress.unlocked = ParseBool(parts[3], progress.unlocked);
                            progress.isNew = ParseBool(parts[4], progress.isNew);
                            return;
                        }

                        if (token.StartsWith("P:", StringComparison.Ordinal))
                        {
                            var permanentId = DecodeToken(token[2..]);
                            if (string.IsNullOrWhiteSpace(permanentId))
                                return;
                            if (existingPermanent.Add(permanentId))
                                permanent.pushDyn(permanentId.AsHaxeString());
                        }
                        return;
                    }

                    var text = token;
                    if (!string.IsNullOrWhiteSpace(text) && !existing.ContainsKey(text))
                    {
                        var progress = new ItemProgress(text.AsHaxeString());
                        progress.unlocked = true;
                        arr.pushDyn(progress);
                        existing[text] = progress;
                    }
                });

                meta.itemProgress = arr;
                meta.permanentItems = permanent;
                user.itemMeta = meta;
            }

            if (target != null)
            {
                apply(target);
            }
            // When target is null (e.g. from network), only store payload.
            // Apply happens when user_hook_new_game or RestoreRemoteUserData runs with target.
        }

        public static void SendBlueprints(User user, NetNode? net)
        {
            if (user == null)
                return;

            var meta = user.itemMeta;
            var builder = new StringBuilder();
            builder.Append("V2");

            var list = meta?.itemProgress;
            if (list != null)
            {
                for (int i = 0; i < list.length; i++)
                {
                    var progress = list.getDyn(i) as ItemProgress;
                    if (progress == null)
                        continue;

                    var text = progress.itemId?.ToString();
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    builder.Append("|I:");
                    builder.Append(EncodeToken(text));
                    builder.Append(':');
                    builder.Append(ToInt(progress.investedCells).ToString(CultureInfo.InvariantCulture));
                    builder.Append(':');
                    builder.Append(progress.unlocked ? "1" : "0");
                    builder.Append(':');
                    builder.Append(progress.isNew ? "1" : "0");
                }
            }

            var permanentItems = meta?.permanentItems;
            if (permanentItems != null)
            {
                for (int i = 0; i < permanentItems.length; i++)
                {
                    var item = permanentItems.getDyn(i);
                    var text = item?.ToString();
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    builder.Append("|P:");
                    builder.Append(EncodeToken(text));
                }
            }

            var payload = builder.ToString();
            HostBlueprintsPayload = payload;
            if (net != null && net.IsAlive)
                net.SendBlueprints(payload);
        }

        public static bool SwapToOriginalUserData(User user)
        {
            if (TryApplyProgressPayload(user, _origProgressPayload))
                return true;

            var swapped = false;
            if (_hasRemoteCounters && _origStoryCaptured)
            {
                RestoreOriginalStory(user, preserveLocalProgress: true);
                swapped = true;
            }

            if (_hasRemoteBlueprints && _origItemMetaCaptured)
            {
                if (_origItemMetaWasNull)
                {
                    user.itemMeta = null;
                }
                else
                {
                    var meta = EnsureItemMeta(user, _origItemMeta ?? user.itemMeta);
                    meta.itemProgress = MergeItemProgressWithLocalProgress(meta.itemProgress);
                    meta.permanentItems = MergePermanentItemsWithLocalProgress(meta.permanentItems);
                    user.itemMeta = meta;
                }
                swapped = true;
            }

            if (_hasRemoteBossRune && _origBossRuneCaptured)
            {
                user.bossRuneActivated = _origBossRune;
                swapped = true;
            }

            return swapped;
        }

        public static bool RestoreOriginalUserState(User user, bool clearRemote)
        {
            var restored = false;
            if (TryApplyProgressPayload(user, _origProgressPayload))
                restored = true;

            if (!restored && _origStoryCaptured)
            {
                RestoreOriginalStory(user, preserveLocalProgress: false);
                restored = true;
            }

            if (!restored && _origItemMetaCaptured)
            {
                if (_origItemMetaWasNull)
                {
                    user.itemMeta = null;
                }
                else
                {
                    var meta = EnsureItemMeta(user, _origItemMeta ?? user.itemMeta);
                    meta.itemProgress = MergeItemProgressWithLocalProgress(meta.itemProgress);
                    meta.permanentItems = MergePermanentItemsWithLocalProgress(meta.permanentItems);
                    user.itemMeta = meta;
                }
                restored = true;
            }

            if (!restored && _origBossRuneCaptured)
            {
                user.bossRuneActivated = _origBossRune;
                restored = true;
            }

            ApplyHeroCosmetics(user, _origHeroSkin, _origHeroHeadSkin);

            if (clearRemote)
            {
                _remoteProgressPayload = null;
                HostProgressPayload = null;
                _remoteCountersPayload = null;
                _remoteBlueprintsPayload = null;
                _hasRemoteCounters = false;
                _hasRemoteBlueprints = false;
                _hasRemoteBossRune = false;
                _remoteCountersSnapshot.Clear();
                _remoteNpcProgressSnapshot.Clear();
                _remoteLoreRoomRunIdsSnapshot.Clear();
                _remoteVisitedLoreRoomsSnapshot.Clear();
                _remotePlannedLoresSnapshot.Clear();
                _remoteStoryDataVersion = 0;
                _hasRemoteStoryDataVersion = false;
                _hasRemoteSerializerSync = false;
                _hasRemoteSerializerValues = false;
                _remoteSerializerSeq = 0;
                _remoteSerializerUid = 0;
                lock (_bossRuneLock)
                {
                    _remoteBossRune = null;
                }
                RestoreLocalSerializerSyncIfCaptured();
                _origProgressCaptured = false;
                _origProgressPayload = null;
                _origHeroCosmeticsCaptured = false;
                _origHeroSkin = null;
                _origHeroHeadSkin = null;
                _origStoryCaptured = false;
                _origStoryWasNull = false;
                _origStory = null;
                _origCounters = null;
                _origCountersSnapshot.Clear();
                _origNpcProgressSnapshot.Clear();
                _origLoreRoomRunIdsSnapshot.Clear();
                _origVisitedLoreRoomsSnapshot.Clear();
                _origPlannedLoresSnapshot.Clear();
                _origStoryDataVersion = 0;
                ClearSessionStory();
                _origItemMetaCaptured = false;
                _origItemMeta = null;
                _origItemProgress = null;
                _origPermanentItems = null;
                _origItemMetaWasNull = false;
                _origBossRuneCaptured = false;
                _origBossRune = 0;
                MarkProgressPayloadDirty();
                _lastProgressSyncNet = null;
                _lastProgressSyncPayload = null;
                _lastHeroSkinSyncNet = null;
                _lastHeroSkinSyncPayload = null;
                _lastHeroHeadSkinSyncNet = null;
                _lastHeroHeadSkinSyncPayload = null;
            }

            return restored;
        }

        public static void CaptureOriginalUserData(User user, bool allowReplaceWhenBetter = false)
        {
            if (user != null && (!_origProgressCaptured || (allowReplaceWhenBetter && string.IsNullOrWhiteSpace(_origProgressPayload))))
            {
                var payload = BuildProgressPayload(user);
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    _origProgressCaptured = true;
                    _origProgressPayload = payload;
                }
            }

            if (user != null &&
                (!_origHeroCosmeticsCaptured ||
                 (allowReplaceWhenBetter &&
                  (string.IsNullOrWhiteSpace(_origHeroSkin) || string.IsNullOrWhiteSpace(_origHeroHeadSkin)))))
            {
                _origHeroCosmeticsCaptured = true;
                _origHeroSkin = CleanSkin(user.heroSkin?.ToString());
                _origHeroHeadSkin = CleanSkin(user.heroHeadSkin?.ToString());
            }

            if (EnableStoryManagerSync)
            {
                var shouldCaptureOriginalStory = !_origStoryCaptured;
                if (!shouldCaptureOriginalStory &&
                    allowReplaceWhenBetter &&
                    !HasAnyStorySnapshotData(
                        _origCountersSnapshot,
                        _origNpcProgressSnapshot,
                        _origLoreRoomRunIdsSnapshot,
                        _origVisitedLoreRoomsSnapshot,
                        _origPlannedLoresSnapshot,
                        _origStoryDataVersion) &&
                    HasAnyUserStoryData(user))
                {
                    shouldCaptureOriginalStory = true;
                }

                if (shouldCaptureOriginalStory)
                {
                    _origStoryCaptured = true;
                    _origStory = user.story;
                    _origCounters = user.story?.counters;
                    CaptureStorySnapshot(
                        user,
                        _origCountersSnapshot,
                        _origNpcProgressSnapshot,
                        _origLoreRoomRunIdsSnapshot,
                        _origVisitedLoreRoomsSnapshot,
                        _origPlannedLoresSnapshot,
                        out _origStoryWasNull,
                        out _origStoryDataVersion);
                }
            }

            if (!_origItemMetaCaptured)
            {
                var meta = user.itemMeta;
                if (meta != null)
                    meta = EnsureItemMeta(user, meta);
                _origItemMetaCaptured = true;
                _origItemMeta = meta;
                _origItemMetaWasNull = meta == null;
                _origItemProgress = CloneItemProgress(meta?.itemProgress);
                _origPermanentItems = CloneItemList(meta?.permanentItems);
            }

            if (!_origBossRuneCaptured)
            {
                _origBossRuneCaptured = true;
                _origBossRune = user.bossRuneActivated;
            }
        }

        public static void CaptureSessionStory(User user)
        {
            if (!EnableStoryManagerSync || user == null)
                return;

            _sessionStoryCaptured = true;
            CaptureStorySnapshot(
                user,
                _sessionCountersSnapshot,
                _sessionNpcProgressSnapshot,
                _sessionLoreRoomRunIdsSnapshot,
                _sessionVisitedLoreRoomsSnapshot,
                _sessionPlannedLoresSnapshot,
                out _sessionStoryWasNull,
                out _sessionStoryDataVersion);
        }

        public static void RestoreSessionStory(User user)
        {
            if (!EnableStoryManagerSync || !_sessionStoryCaptured || user == null)
                return;

            if (_sessionStoryWasNull && _sessionCountersSnapshot.Count == 0 && _sessionNpcProgressSnapshot.Count == 0 && _sessionStoryDataVersion == 0)
            {
                ClearUserStoryState(user);
                ClearSessionStory();
                return;
            }

            ApplyStoryState(
                user,
                _sessionCountersSnapshot,
                _sessionNpcProgressSnapshot,
                _sessionStoryDataVersion,
                _sessionLoreRoomRunIdsSnapshot,
                _sessionVisitedLoreRoomsSnapshot,
                _sessionPlannedLoresSnapshot);
            ClearSessionStory();
        }

        public static void RestoreRemoteUserData(User user)
        {
            if (TryApplyProgressPayload(user, _remoteProgressPayload))
                return;

            if (EnableStoryManagerSync && !string.IsNullOrEmpty(_remoteCountersPayload))
                ReceiveCounters(_remoteCountersPayload, user);
            if (!string.IsNullOrEmpty(_remoteBlueprintsPayload))
                ReceiveBlueprints(_remoteBlueprintsPayload, user);
            if (TryGetRemoteBossRune(out var bossRune))
                ApplyRemoteBossRune(user, bossRune);
        }

        public static void TriggerRemoteDeath()
        {
            _suppressDeathBroadcast = true;
            GameMenu.EnqueueMainThread(() =>
            {
                try
                {
                    ModEntry.me?.kill();
                }
                catch
                {
                }
            });
        }

        public static bool ConsumeSuppressDeathBroadcast()
        {
            if (!_suppressDeathBroadcast)
                return false;
            _suppressDeathBroadcast = false;
            return true;
        }

        public static void ReceiveLevelSeed(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return;

            var sep = payload.IndexOf('|');
            if (sep <= 0 || sep >= payload.Length - 1)
                return;

            var levelId = payload[..sep];
            var seedText = payload[(sep + 1)..];
            if (string.IsNullOrWhiteSpace(levelId))
                return;

            if (!double.TryParse(seedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seed))
                return;

            lock (_levelSeedLock)
            {
                _remoteLevelId = levelId;
                _remoteLevelSeed = seed;
            }
        }

        public static bool TryApplyRemoteLevelSeed(string levelId, Rand rng)
        {
            if (rng == null || string.IsNullOrWhiteSpace(levelId))
                return false;

            lock (_levelSeedLock)
            {
                if (_remoteLevelSeed.HasValue && string.Equals(_remoteLevelId, levelId, StringComparison.Ordinal))
                {
                    rng.seed = _remoteLevelSeed.Value;
                    return true;
                }
            }

            return false;
        }

        public static void SendLevelSeed(string levelId, Rand rng, NetNode? net)
        {
            if (net == null || !net.IsAlive || rng == null || string.IsNullOrWhiteSpace(levelId))
                return;

            SendSerializerSync(net);
            net.SendLevelSeed(levelId, rng.seed);
        }

        public static void ReceiveSerializerSync(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return;

            var parts = payload.Split('|');
            if (parts.Length < 2)
                return;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
                return;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid))
                return;

            lock (_serializerSyncLock)
            {
                _remoteSerializerSeq = seq;
                _remoteSerializerUid = uid;
                _hasRemoteSerializerSync = true;
                _hasRemoteSerializerValues = true;
            }
        }

        public static bool TryApplyRemoteSerializerSync()
        {
            int seq;
            int uid;
            lock (_serializerSyncLock)
            {
                if (!_hasRemoteSerializerSync)
                    return false;

                seq = _remoteSerializerSeq;
                uid = _remoteSerializerUid;
                _hasRemoteSerializerSync = false;
            }

            try
            {
                var serializerClass = dc.hxbit.Serializer.Class;
                if (serializerClass == null)
                    return false;

                if (!_localSerializerCaptured)
                {
                    _localSerializerSeq = serializerClass.SEQ;
                    _localSerializerUid = serializerClass.UID;
                    _localSerializerCaptured = true;
                }

                serializerClass.SEQ = seq;
                serializerClass.UID = uid;
                _hasRemoteSerializerValues = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool SwapToLocalSerializerSync()
        {
            if (!_localSerializerCaptured)
                return false;

            try
            {
                var serializerClass = dc.hxbit.Serializer.Class;
                if (serializerClass == null)
                    return false;

                if (serializerClass.SEQ == _localSerializerSeq &&
                    serializerClass.UID == _localSerializerUid)
                {
                    return false;
                }

                serializerClass.SEQ = _localSerializerSeq;
                serializerClass.UID = _localSerializerUid;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void RestoreRemoteSerializerSync()
        {
            if (!_hasRemoteSerializerValues)
                return;

            try
            {
                var serializerClass = dc.hxbit.Serializer.Class;
                if (serializerClass == null)
                    return;

                serializerClass.SEQ = _remoteSerializerSeq;
                serializerClass.UID = _remoteSerializerUid;
            }
            catch
            {
            }
        }

        public static void SendSerializerSync(NetNode? net)
        {
            if (net == null || !net.IsAlive || !net.IsHost)
                return;

            try
            {
                var serializerClass = dc.hxbit.Serializer.Class;
                if (serializerClass == null)
                    return;

                net.SendSerializerSync(serializerClass.SEQ, serializerClass.UID);
            }
            catch
            {
            }
        }

        public static void ReceiveCounters(string payload, User? target = null)
        {
            _remoteCountersPayload = payload;
            if (!EnableStoryManagerSync)
            {
                _hasRemoteCounters = false;
                return;
            }

            if (string.IsNullOrEmpty(payload))
                return;

            void apply(User user)
            {
                var counters = new Dictionary<string, int>(StringComparer.Ordinal);
                var npcProgress = new Dictionary<int, int>();
                var loreRoomRunIds = new Dictionary<string, int>(StringComparer.Ordinal);
                var visitedLoreRooms = new HashSet<string>(StringComparer.Ordinal);
                var plannedLores = new List<int>();
                int? storyDataVersion = null;
                var hasExtendedStoryPayload = TryParseCountersPayloadV4(
                    payload,
                    counters,
                    npcProgress,
                    loreRoomRunIds,
                    visitedLoreRooms,
                    plannedLores,
                    out storyDataVersion);
                if (!hasExtendedStoryPayload &&
                    !TryParseCountersPayloadV3(payload, counters, npcProgress, out storyDataVersion))
                {
                    ParseLegacyCountersPayload(payload, counters);
                }

                if (!HasAnyIncomingStoryData(counters, npcProgress, loreRoomRunIds, visitedLoreRooms, plannedLores, storyDataVersion) &&
                    HasAnyUserStoryData(user))
                {
                    _log?.Warning("[NetMod] Ignoring empty story counters payload to avoid wiping local story state");
                    return;
                }

                CaptureOriginalUserData(user);
                if (!hasExtendedStoryPayload)
                {
                    loreRoomRunIds.Clear();
                    CopyStoryStringIntMapToDictionary(user.story?.loreRoomRunIds, loreRoomRunIds);
                    visitedLoreRooms.Clear();
                    CopyStoryVisitedLoreRoomsToSet(user.story?.visitedLoreRooms, visitedLoreRooms);
                    plannedLores.Clear();
                    CopyStoryPlannedLoresToList(user.story?.plannedLores, plannedLores);
                }

                var storyDataVersionToApply = storyDataVersion ?? (user.story?.storyDataVersion ?? 0);
                ApplyStoryState(
                    user,
                    counters,
                    npcProgress,
                    storyDataVersionToApply,
                    loreRoomRunIds,
                    visitedLoreRooms,
                    plannedLores);

                _remoteCountersSnapshot.Clear();
                foreach (var kv in counters)
                    _remoteCountersSnapshot[kv.Key] = kv.Value;

                _remoteNpcProgressSnapshot.Clear();
                foreach (var kv in npcProgress)
                    _remoteNpcProgressSnapshot[kv.Key] = kv.Value;

                _remoteLoreRoomRunIdsSnapshot.Clear();
                _remoteVisitedLoreRoomsSnapshot.Clear();
                _remotePlannedLoresSnapshot.Clear();
                if (hasExtendedStoryPayload)
                {
                    foreach (var kv in loreRoomRunIds)
                        _remoteLoreRoomRunIdsSnapshot[kv.Key] = kv.Value;

                    foreach (var key in visitedLoreRooms)
                        _remoteVisitedLoreRoomsSnapshot.Add(key);

                    for (var i = 0; i < plannedLores.Count; i++)
                        _remotePlannedLoresSnapshot.Add(plannedLores[i]);
                }

                _hasRemoteStoryDataVersion = storyDataVersion.HasValue;
                _remoteStoryDataVersion = storyDataVersion ?? 0;
                _hasRemoteCounters = true;
            }

            if (target != null)
            {
                apply(target);
                return;
            }

            GameMenu.EnqueueMainThread(() =>
            {
                try
                {
                    var main = dc.Main.Class.ME;
                    if (main?.user != null)
                        apply(main.user);
                }
                catch
                {
                }
            });
        }

        private static void SendCounters(User user, NetNode? net)
        {
            if (!EnableStoryManagerSync || user == null)
                return;

            var map = user.story?.counters;
            var builder = new StringBuilder();
            builder.Append("V4");
            if (map != null)
            {
                var keys = map.keys();
                while (keys.hasNext.Invoke())
                {
                    var key = keys.next.Invoke();
                    if (key == null)
                        continue;
                    var keyText = key.ToString();
                    if (string.IsNullOrWhiteSpace(keyText))
                        continue;
                    var value = ToInt(map.get(key));
                    builder.Append("|C:");
                    builder.Append(EncodeToken(keyText));
                    builder.Append(':');
                    builder.Append(value.ToString(CultureInfo.InvariantCulture));
                }
            }

            var npcProgress = user.story?.npcProgresses;
            if (npcProgress != null)
            {
                var npcKeys = npcProgress.keys();
                while (npcKeys.hasNext.Invoke())
                {
                    var npcObj = npcKeys.next.Invoke();
                    if (npcObj is not NpcId npcId)
                        continue;

                    var value = ToInt(npcProgress.get(npcObj));
                    builder.Append("|N:");
                    builder.Append(((int)npcId.Index).ToString(CultureInfo.InvariantCulture));
                    builder.Append(':');
                    builder.Append(value.ToString(CultureInfo.InvariantCulture));
                }
            }

            var loreRoomRunIds = user.story?.loreRoomRunIds;
            if (loreRoomRunIds != null)
            {
                try
                {
                    dynamic keys = loreRoomRunIds.keys.Invoke();
                    while (keys.hasNext.Invoke())
                    {
                        var keyObj = keys.next.Invoke();
                        if (keyObj == null)
                            continue;

                        var key = keyObj.ToString();
                        if (string.IsNullOrWhiteSpace(key))
                            continue;

                        var value = ToInt(loreRoomRunIds.get.Invoke(keyObj));
                        builder.Append("|L:");
                        builder.Append(EncodeToken(key));
                        builder.Append(':');
                        builder.Append(value.ToString(CultureInfo.InvariantCulture));
                    }
                }
                catch
                {
                }
            }

            var visitedLoreRooms = user.story?.visitedLoreRooms;
            if (visitedLoreRooms != null)
            {
                try
                {
                    dynamic keys = visitedLoreRooms.keys.Invoke();
                    while (keys.hasNext.Invoke())
                    {
                        var keyObj = keys.next.Invoke();
                        if (keyObj == null)
                            continue;

                        var key = keyObj.ToString();
                        if (string.IsNullOrWhiteSpace(key))
                            continue;

                        var raw = visitedLoreRooms.get.Invoke(keyObj);
                        var visited = raw is bool b ? b : ToInt(raw) != 0;
                        if (!visited)
                            continue;

                        builder.Append("|V:");
                        builder.Append(EncodeToken(key));
                    }
                }
                catch
                {
                }
            }

            var plannedLores = user.story?.plannedLores;
            if (plannedLores != null)
            {
                var seenPlanned = new HashSet<int>();
                for (var i = 0; i < plannedLores.length; i++)
                {
                    int planned;
                    try
                    {
                        planned = ToInt(plannedLores.getDyn(i));
                    }
                    catch
                    {
                        continue;
                    }

                    if (!seenPlanned.Add(planned))
                        continue;

                    builder.Append("|P:");
                    builder.Append(planned.ToString(CultureInfo.InvariantCulture));
                }
            }

            builder.Append("|S:");
            builder.Append((user.story?.storyDataVersion ?? 0).ToString(CultureInfo.InvariantCulture));

            var payload = builder.ToString();
            HostCountersPayload = payload;
            if (net != null && net.IsAlive)
                net.SendCounters(payload);
        }

        public static void SendHostStorySync(User user, NetNode? net)
        {
            SendProgressSync(user, net);
        }

        public static void SendProgressSync(User user, NetNode? net)
        {
            if (user == null || net == null || !net.IsHost || !net.IsAlive)
                return;

            if (!EnableSendHostUserProgress)
                return;

            var payload = GetCurrentProgressPayload(user);
            if (string.IsNullOrWhiteSpace(payload))
                return;

            HostProgressPayload = payload;
            if (ReferenceEquals(_lastProgressSyncNet, net) &&
                string.Equals(_lastProgressSyncPayload, payload, StringComparison.Ordinal))
            {
                return;
            }

            net.SendProgress(payload);
            _lastProgressSyncNet = net;
            _lastProgressSyncPayload = payload;
        }

        public static void ReceiveProgressSync(string payload, User? target = null)
        {
            _remoteProgressPayload = payload;
            if (string.IsNullOrWhiteSpace(payload))
                return;

            void apply(User user)
            {
                CaptureOriginalUserData(user);
                if (TryApplyProgressPayload(user, payload))
                {
                    lock (_bossRuneLock)
                    {
                        _remoteBossRune = GetEffectiveBossRune(user);
                    }
                    _hasRemoteBossRune = true;
                }
            }

            if (target != null)
            {
                apply(target);
                return;
            }

            GameMenu.EnqueueMainThread(() =>
            {
                try
                {
                    var user = dc.Main.Class.ME?.user;
                    if (user != null)
                        apply(user);
                }
                catch
                {
                }
            });
        }




        internal static int GetBossRuneInt(User? user)
        {
            return user == null ? 0 : GetEffectiveBossRune(user);
        }

        public static void SendBossRune(User self, NetNode? net)
        {
            if (self == null)
                return;

            var bossRune = GetEffectiveBossRune(self);
            lock (_bossRuneLock)
            {
                _hostBossRune = bossRune;
            }

            if (net == null || !net.IsAlive)
                return;

            net.SendBossRune(bossRune);
        }

        public static void ReceiveBossRune(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return;

            if (!int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bossRune))
            {
                return;
            }

            lock (_bossRuneLock)
            {
                _remoteBossRune = bossRune;
            }
            _hasRemoteBossRune = true;

            var net = GameMenu.NetRef;
            if (net != null && net.IsHost)
                return;

            MarkPendingBossRuneReload(bossRune);
            // _log?.Information("[NetMod] Received remote boss rune {BossRune}", bossRune);

            GameMenu.EnqueueMainThread(() =>
            {
                try
                {
                    var user = dc.Main.Class.ME?.user;
                    if (user != null)
                        ApplyRemoteBossRune(user, bossRune);
                }
                catch
                {
                }
            });

        }

        public static bool TryGetHostBossRune(out int bossRune)
        {
            lock (_bossRuneLock)
            {
                if (_hostBossRune.HasValue)
                {
                    bossRune = _hostBossRune.Value;
                    return true;
                }
            }

            bossRune = 0;
            return false;
        }

        public static bool TryGetRemoteBossRune(out int bossRune)
        {
            lock (_bossRuneLock)
            {
                if (_remoteBossRune.HasValue)
                {
                    bossRune = _remoteBossRune.Value;
                    return true;
                }
            }

            bossRune = 0;
            return false;
        }

        public static void ReceiveHeroSkin(string skin)
        {
            
            var cleaned = CleanSkin(skin);
            if (string.IsNullOrWhiteSpace(cleaned))
                cleaned = "PrisonerDefault";

            ModEntry.SetRemoteSkin(cleaned);
            
        }


        public static void ReceiveHeroHeadSkin(string skin)
        {
            var cleaned = CleanSkin(skin);
            if (string.IsNullOrWhiteSpace(cleaned))
                cleaned = "BaseFlame";

            ModEntry.SetRemoteHeadSkin(cleaned);
        }

        private static void SendHeroSkin(User user, NetNode? net)
        {
            if (net == null || !net.IsAlive)
                return;

            try
            {
                var skin = CleanSkin(user?.heroSkin?.ToString());
                if (string.IsNullOrWhiteSpace(skin))
                    skin = "PrisonerDefault";

                if (ReferenceEquals(_lastHeroSkinSyncNet, net) &&
                    string.Equals(_lastHeroSkinSyncPayload, skin, StringComparison.Ordinal))
                {
                    return;
                }

                net.SendHeroSkin(skin);
                _lastHeroSkinSyncNet = net;
                _lastHeroSkinSyncPayload = skin;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to send hero skin: {Message}", ex.Message);
            }
        }


        private static void SendHeroHeadSkin(User user, NetNode? net)
        {
            if (net == null || !net.IsAlive)
                return;

            try
            {
                var skin = CleanSkin(user?.heroHeadSkin?.ToString());
                if (string.IsNullOrWhiteSpace(skin))
                    skin = "BaseFlame";

                if (ReferenceEquals(_lastHeroHeadSkinSyncNet, net) &&
                    string.Equals(_lastHeroHeadSkinSyncPayload, skin, StringComparison.Ordinal))
                {
                    return;
                }

                net.SendHeroHeadSkin(skin);
                _lastHeroHeadSkinSyncNet = net;
                _lastHeroHeadSkinSyncPayload = skin;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to send hero skin: {Message}", ex.Message);
            }
        }

        private static string CleanSkin(string? skin)
        {
            if (string.IsNullOrEmpty(skin))
                return string.Empty;

            return skin.Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        }

        private static void ApplyRemoteBossRune(User user, int bossRune)
        {
            CaptureOriginalUserData(user);

            try
            {
                user.br_setActivated(bossRune);
            }
            catch
            {
                user.bossRuneActivated = bossRune;
            }

            try
            {
                dynamic? gameData = user.game?.data;
                if (gameData?.cgData != null)
                    gameData.cgData.numBossCells = bossRune;
            }
            catch
            {
            }

            _hasRemoteBossRune = true;
        }

        private static int GetEffectiveBossRune(User user)
        {
            if (user == null)
                return 0;

            var fallback = ToInt(user.bossRuneActivated);

            // During active runs the currently selected BC is mirrored in cgData.numBossCells (>=0).
            // Prefer that when available so decreases are propagated too.
            try
            {
                dynamic? gameData = user.game?.data;
                if (gameData?.cgData != null)
                {
                    var cgBossRune = ToInt(gameData.cgData.numBossCells);
                    if (cgBossRune >= 0)
                        return cgBossRune;
                }
            }
            catch
            {
            }

            try
            {
                var activated = user.br_numActivated();
                // br_numActivated() can return 0 in scoring contexts when cgData is unavailable.
                if (activated == 0 && fallback > 0)
                    return fallback;
                return activated;
            }
            catch
            {
                return fallback;
            }
        }

        private static void ForEachEscapedToken(string payload, Action<string> onToken)
        {
            if (string.IsNullOrEmpty(payload))
                return;

            var token = new StringBuilder();
            var escaped = false;
            for (var i = 0; i < payload.Length; i++)
            {
                var c = payload[i];
                if (escaped)
                {
                    token.Append(c);
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '|')
                {
                    onToken(token.ToString());
                    token.Clear();
                    continue;
                }

                token.Append(c);
            }

            if (escaped)
                token.Append('\\');
            onToken(token.ToString());
        }

        private static string EncodeToken(string value)
        {
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
        }

        private static string? DecodeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            try
            {
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch
            {
                return null;
            }
        }

        private static int ParseInt(string value, int fallback)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return fallback;
        }

        private static bool ParseBool(string value, bool fallback)
        {
            if (value == "1")
                return true;
            if (value == "0")
                return false;
            if (bool.TryParse(value, out var parsed))
                return parsed;
            return fallback;
        }

        private static void RestoreLocalSerializerSyncIfCaptured()
        {
            if (!_localSerializerCaptured)
                return;

            try
            {
                var serializerClass = dc.hxbit.Serializer.Class;
                if (serializerClass == null)
                    return;

                serializerClass.SEQ = _localSerializerSeq;
                serializerClass.UID = _localSerializerUid;
            }
            catch
            {
            }
        }

        private static void ClearSessionStory()
        {
            _sessionStoryCaptured = false;
            _sessionStoryWasNull = false;
            _sessionCountersSnapshot.Clear();
            _sessionNpcProgressSnapshot.Clear();
            _sessionLoreRoomRunIdsSnapshot.Clear();
            _sessionVisitedLoreRoomsSnapshot.Clear();
            _sessionPlannedLoresSnapshot.Clear();
            _sessionStoryDataVersion = 0;
        }

        private static void RestoreOriginalStory(User user, bool preserveLocalProgress)
        {
            var currentStory = user.story;
            Dictionary<string, int> countersToApply;
            Dictionary<int, int> npcProgressToApply;
            Dictionary<string, int> loreRoomRunIdsToApply;
            HashSet<string> visitedLoreRoomsToApply;
            List<int> plannedLoresToApply;
            int storyDataVersionToApply;
            if (preserveLocalProgress)
            {
                countersToApply = MergeCountersWithLocalProgress(currentStory);
                npcProgressToApply = MergeNpcProgressWithLocalProgress(currentStory);
                loreRoomRunIdsToApply = MergeLoreRoomRunIdsWithLocalProgress(currentStory);
                visitedLoreRoomsToApply = MergeVisitedLoreRoomsWithLocalProgress(currentStory);
                plannedLoresToApply = MergePlannedLoresWithLocalProgress(currentStory);
                storyDataVersionToApply = MergeStoryDataVersion(currentStory);
            }
            else
            {
                countersToApply = new Dictionary<string, int>(_origCountersSnapshot, StringComparer.Ordinal);
                npcProgressToApply = new Dictionary<int, int>(_origNpcProgressSnapshot);
                loreRoomRunIdsToApply = new Dictionary<string, int>(_origLoreRoomRunIdsSnapshot, StringComparer.Ordinal);
                visitedLoreRoomsToApply = new HashSet<string>(_origVisitedLoreRoomsSnapshot, StringComparer.Ordinal);
                plannedLoresToApply = new List<int>(_origPlannedLoresSnapshot);
                storyDataVersionToApply = _origStoryDataVersion;
            }

            if (_origStoryWasNull &&
                countersToApply.Count == 0 &&
                npcProgressToApply.Count == 0 &&
                loreRoomRunIdsToApply.Count == 0 &&
                visitedLoreRoomsToApply.Count == 0 &&
                plannedLoresToApply.Count == 0 &&
                storyDataVersionToApply == 0)
            {
                ClearUserStoryState(user);
                return;
            }

            ApplyStoryState(
                user,
                countersToApply,
                npcProgressToApply,
                storyDataVersionToApply,
                loreRoomRunIdsToApply,
                visitedLoreRoomsToApply,
                plannedLoresToApply);
        }

        private static Dictionary<string, int> MergeCountersWithLocalProgress(StoryManager? currentStory)
        {
            var merged = new Dictionary<string, int>(_origCountersSnapshot, StringComparer.Ordinal);
            var currentCounters = new Dictionary<string, int>(StringComparer.Ordinal);
            CopyCountersToDictionary(currentStory?.counters, currentCounters);
            foreach (var kv in currentCounters)
            {
                if (!_remoteCountersSnapshot.TryGetValue(kv.Key, out var remoteValue) || remoteValue != kv.Value)
                    merged[kv.Key] = kv.Value;
            }

            return merged;
        }

        private static Dictionary<int, int> MergeNpcProgressWithLocalProgress(StoryManager? currentStory)
        {
            var merged = new Dictionary<int, int>(_origNpcProgressSnapshot);
            var currentNpcProgress = new Dictionary<int, int>();
            CopyNpcProgressToDictionary(currentStory?.npcProgresses, currentNpcProgress);
            foreach (var kv in currentNpcProgress)
            {
                if (!_remoteNpcProgressSnapshot.TryGetValue(kv.Key, out var remoteValue) || remoteValue != kv.Value)
                    merged[kv.Key] = kv.Value;
            }

            return merged;
        }

        private static int MergeStoryDataVersion(StoryManager? currentStory)
        {
            var merged = _origStoryDataVersion;
            var current = currentStory?.storyDataVersion ?? merged;
            if (!_hasRemoteStoryDataVersion || current != _remoteStoryDataVersion)
                merged = current;
            return merged;
        }

        private static Dictionary<string, int> MergeLoreRoomRunIdsWithLocalProgress(StoryManager? currentStory)
        {
            var merged = new Dictionary<string, int>(_origLoreRoomRunIdsSnapshot, StringComparer.Ordinal);
            var currentLore = new Dictionary<string, int>(StringComparer.Ordinal);
            CopyStoryStringIntMapToDictionary(currentStory?.loreRoomRunIds, currentLore);
            foreach (var kv in currentLore)
            {
                if (!_remoteLoreRoomRunIdsSnapshot.TryGetValue(kv.Key, out var remoteValue) || remoteValue != kv.Value)
                    merged[kv.Key] = kv.Value;
            }

            return merged;
        }

        private static HashSet<string> MergeVisitedLoreRoomsWithLocalProgress(StoryManager? currentStory)
        {
            var merged = new HashSet<string>(_origVisitedLoreRoomsSnapshot, StringComparer.Ordinal);
            var currentVisited = new HashSet<string>(StringComparer.Ordinal);
            CopyStoryVisitedLoreRoomsToSet(currentStory?.visitedLoreRooms, currentVisited);
            foreach (var key in currentVisited)
            {
                if (!_remoteVisitedLoreRoomsSnapshot.Contains(key))
                    merged.Add(key);
            }

            return merged;
        }

        private static List<int> MergePlannedLoresWithLocalProgress(StoryManager? currentStory)
        {
            var mergedSet = new HashSet<int>(_origPlannedLoresSnapshot);
            var currentPlanned = new List<int>();
            CopyStoryPlannedLoresToList(currentStory?.plannedLores, currentPlanned);
            for (var i = 0; i < currentPlanned.Count; i++)
            {
                var planned = currentPlanned[i];
                if (!_remotePlannedLoresSnapshot.Contains(planned))
                    mergedSet.Add(planned);
            }

            var merged = new List<int>(mergedSet);
            merged.Sort();
            return merged;
        }

        private static ArrayObj MergeItemProgressWithLocalProgress(ArrayObj? currentItemProgress)
        {
            var merged = new Dictionary<string, ItemProgress>(StringComparer.Ordinal);
            var orig = _origItemProgress;
            if (orig != null)
            {
                for (int i = 0; i < orig.length; i++)
                {
                    var p = orig.getDyn(i) as ItemProgress;
                    if (p != null)
                    {
                        var id = p.itemId?.ToString();
                        if (!string.IsNullOrWhiteSpace(id))
                            merged[id] = p;
                    }
                }
            }
            if (currentItemProgress != null)
            {
                for (int i = 0; i < currentItemProgress.length; i++)
                {
                    var curr = currentItemProgress.getDyn(i) as ItemProgress;
                    if (curr == null)
                        continue;
                    var id = curr.itemId?.ToString();
                    if (string.IsNullOrWhiteSpace(id))
                        continue;
                    if (!merged.TryGetValue(id, out var origP) || origP == null)
                    {
                        merged[id] = curr;
                        continue;
                    }
                    var currUnlocked = curr.unlocked;
                    var currInvested = ToInt(curr.investedCells);
                    var currIsNew = curr.isNew;
                    var origUnlocked = origP.unlocked;
                    var origInvested = ToInt(origP.investedCells);
                    var origIsNew = origP.isNew;
                    if (currUnlocked && !origUnlocked || currInvested > origInvested || currIsNew && !origIsNew)
                        merged[id] = curr;
                }
            }
            var arr = ArrayUtils.CreateDyn();
            foreach (var p in merged.Values)
                arr.array.pushDyn(p);
            return (ArrayObj)arr.array;
        }

        private static ArrayObj MergePermanentItemsWithLocalProgress(ArrayObj? currentPermanentItems)
        {
            var merged = new HashSet<string>(StringComparer.Ordinal);
            var orig = _origPermanentItems;
            if (orig != null)
            {
                for (int i = 0; i < orig.length; i++)
                {
                    var id = orig.getDyn(i)?.ToString();
                    if (!string.IsNullOrWhiteSpace(id))
                        merged.Add(id);
                }
            }
            if (currentPermanentItems != null)
            {
                for (int i = 0; i < currentPermanentItems.length; i++)
                {
                    var id = currentPermanentItems.getDyn(i)?.ToString();
                    if (!string.IsNullOrWhiteSpace(id))
                        merged.Add(id);
                }
            }
            var arr = ArrayUtils.CreateDyn();
            foreach (var id in merged)
                arr.array.pushDyn(id.AsHaxeString());
            return (ArrayObj)arr.array;
        }

        private static bool TryParseCountersPayloadV4(
            string payload,
            Dictionary<string, int> counters,
            Dictionary<int, int> npcProgress,
            Dictionary<string, int> loreRoomRunIds,
            HashSet<string> visitedLoreRooms,
            List<int> plannedLores,
            out int? storyDataVersion)
        {
            int? parsedStoryDataVersion = null;
            var isV4 = payload.Equals("V4", StringComparison.Ordinal) ||
                       payload.StartsWith("V4|", StringComparison.Ordinal);
            if (!isV4)
            {
                storyDataVersion = null;
                return false;
            }

            var plannedSet = new HashSet<int>();
            ForEachEscapedToken(payload, token =>
            {
                if (string.IsNullOrWhiteSpace(token) || token.Equals("V4", StringComparison.Ordinal))
                    return;

                if (token.StartsWith("C:", StringComparison.Ordinal))
                {
                    var parts = token.Split(':', 3);
                    if (parts.Length < 3)
                        return;

                    var key = DecodeToken(parts[1]);
                    if (string.IsNullOrWhiteSpace(key))
                        return;

                    counters[key] = ParseInt(parts[2], 0);
                    return;
                }

                if (token.StartsWith("N:", StringComparison.Ordinal))
                {
                    var parts = token.Split(':', 3);
                    if (parts.Length < 3)
                        return;

                    if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var npcIndex))
                        return;

                    npcProgress[npcIndex] = ParseInt(parts[2], 0);
                    return;
                }

                if (token.StartsWith("L:", StringComparison.Ordinal))
                {
                    var parts = token.Split(':', 3);
                    if (parts.Length < 3)
                        return;

                    var key = DecodeToken(parts[1]);
                    if (string.IsNullOrWhiteSpace(key))
                        return;

                    loreRoomRunIds[key] = ParseInt(parts[2], 0);
                    return;
                }

                if (token.StartsWith("V:", StringComparison.Ordinal))
                {
                    var key = DecodeToken(token[2..]);
                    if (string.IsNullOrWhiteSpace(key))
                        return;

                    visitedLoreRooms.Add(key);
                    return;
                }

                if (token.StartsWith("P:", StringComparison.Ordinal))
                {
                    if (int.TryParse(token[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var planned))
                    {
                        if (plannedSet.Add(planned))
                            plannedLores.Add(planned);
                    }
                    return;
                }

                if (token.StartsWith("S:", StringComparison.Ordinal))
                {
                    if (int.TryParse(token[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        parsedStoryDataVersion = parsed;
                    return;
                }
            });

            storyDataVersion = parsedStoryDataVersion;
            return true;
        }

        private static bool TryParseCountersPayloadV3(
            string payload,
            Dictionary<string, int> counters,
            Dictionary<int, int> npcProgress,
            out int? storyDataVersion)
        {
            int? parsedStoryDataVersion = null;
            var isV3 = payload.Equals("V3", StringComparison.Ordinal) ||
                       payload.StartsWith("V3|", StringComparison.Ordinal);
            if (!isV3)
            {
                storyDataVersion = null;
                return false;
            }

            ForEachEscapedToken(payload, token =>
            {
                if (string.IsNullOrWhiteSpace(token) || token.Equals("V3", StringComparison.Ordinal))
                    return;

                if (token.StartsWith("C:", StringComparison.Ordinal))
                {
                    var parts = token.Split(':', 3);
                    if (parts.Length < 3)
                        return;

                    var key = DecodeToken(parts[1]);
                    if (string.IsNullOrWhiteSpace(key))
                        return;

                    counters[key] = ParseInt(parts[2], 0);
                    return;
                }

                if (token.StartsWith("N:", StringComparison.Ordinal))
                {
                    var parts = token.Split(':', 3);
                    if (parts.Length < 3)
                        return;

                    if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var npcIndex))
                        return;

                    npcProgress[npcIndex] = ParseInt(parts[2], 0);
                    return;
                }

                if (token.StartsWith("S:", StringComparison.Ordinal))
                {
                    if (int.TryParse(token[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        parsedStoryDataVersion = parsed;
                    return;
                }
            });

            storyDataVersion = parsedStoryDataVersion;
            return true;
        }

        private static void ParseLegacyCountersPayload(string payload, Dictionary<string, int> counters)
        {
            var key = new StringBuilder();
            var value = new StringBuilder();
            var inKey = true;
            var escaped = false;

            void commitPair()
            {
                if (key.Length <= 0)
                    return;

                var keyText = key.ToString();
                var valueText = value.ToString();
                counters[keyText] = ParseInt(valueText, 0);
            }

            for (var i = 0; i < payload.Length; i++)
            {
                var c = payload[i];
                if (escaped)
                {
                    if (inKey)
                        key.Append(c);
                    else
                        value.Append(c);
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (inKey && c == '=')
                {
                    inKey = false;
                    continue;
                }

                if (!inKey && c == '|')
                {
                    commitPair();
                    key.Clear();
                    value.Clear();
                    inKey = true;
                    continue;
                }

                if (inKey)
                    key.Append(c);
                else
                    value.Append(c);
            }

            commitPair();
        }

        private static void CaptureStorySnapshot(
            User user,
            Dictionary<string, int> countersTarget,
            Dictionary<int, int> npcProgressTarget,
            Dictionary<string, int> loreRoomRunIdsTarget,
            HashSet<string> visitedLoreRoomsTarget,
            List<int> plannedLoresTarget,
            out bool storyWasNull,
            out int storyDataVersion)
        {
            countersTarget.Clear();
            npcProgressTarget.Clear();
            loreRoomRunIdsTarget.Clear();
            visitedLoreRoomsTarget.Clear();
            plannedLoresTarget.Clear();

            var story = user.story;
            storyWasNull = story == null;
            if (story != null)
            {
                CopyCountersToDictionary(story.counters, countersTarget);
                CopyNpcProgressToDictionary(story.npcProgresses, npcProgressTarget);
                CopyStoryStringIntMapToDictionary(story.loreRoomRunIds, loreRoomRunIdsTarget);
                CopyStoryVisitedLoreRoomsToSet(story.visitedLoreRooms, visitedLoreRoomsTarget);
                CopyStoryPlannedLoresToList(story.plannedLores, plannedLoresTarget);
                storyDataVersion = story.storyDataVersion;
                return;
            }

            storyDataVersion = 0;
            CopyCountersToDictionary(user.counters, countersTarget);
            CopyNpcProgressToDictionary(user.npcs, npcProgressTarget);
            if (countersTarget.Count > 0 || npcProgressTarget.Count > 0)
                storyWasNull = false;
        }

        private static bool HasAnyIncomingStoryData(
            Dictionary<string, int> counters,
            Dictionary<int, int> npcProgress,
            Dictionary<string, int> loreRoomRunIds,
            HashSet<string> visitedLoreRooms,
            List<int> plannedLores,
            int? storyDataVersion)
        {
            return counters.Count > 0 ||
                   npcProgress.Count > 0 ||
                   loreRoomRunIds.Count > 0 ||
                   visitedLoreRooms.Count > 0 ||
                   plannedLores.Count > 0 ||
                   (storyDataVersion ?? 0) != 0;
        }

        private static bool HasAnyStorySnapshotData(
            Dictionary<string, int> counters,
            Dictionary<int, int> npcProgress,
            Dictionary<string, int> loreRoomRunIds,
            HashSet<string> visitedLoreRooms,
            List<int> plannedLores,
            int storyDataVersion)
        {
            return counters.Count > 0 ||
                   npcProgress.Count > 0 ||
                   loreRoomRunIds.Count > 0 ||
                   visitedLoreRooms.Count > 0 ||
                   plannedLores.Count > 0 ||
                   storyDataVersion != 0;
        }

        private static bool HasAnyUserStoryData(User user)
        {
            if (HasAnyStoryData(user.story))
                return true;

            var legacyCounters = new Dictionary<string, int>(StringComparer.Ordinal);
            CopyCountersToDictionary(user.counters, legacyCounters);
            if (legacyCounters.Count > 0)
                return true;

            var legacyNpcProgress = new Dictionary<int, int>();
            CopyNpcProgressToDictionary(user.npcs, legacyNpcProgress);
            return legacyNpcProgress.Count > 0;
        }

        private static bool HasAnyStoryData(StoryManager? story)
        {
            if (story == null)
                return false;

            if (story.storyDataVersion != 0)
                return true;

            var counters = new Dictionary<string, int>(StringComparer.Ordinal);
            CopyCountersToDictionary(story.counters, counters);
            if (counters.Count > 0)
                return true;

            var npcProgress = new Dictionary<int, int>();
            CopyNpcProgressToDictionary(story.npcProgresses, npcProgress);
            if (npcProgress.Count > 0)
                return true;

            var loreRoomRunIds = new Dictionary<string, int>(StringComparer.Ordinal);
            CopyStoryStringIntMapToDictionary(story.loreRoomRunIds, loreRoomRunIds);
            if (loreRoomRunIds.Count > 0)
                return true;

            var visitedLoreRooms = new HashSet<string>(StringComparer.Ordinal);
            CopyStoryVisitedLoreRoomsToSet(story.visitedLoreRooms, visitedLoreRooms);
            if (visitedLoreRooms.Count > 0)
                return true;

            var plannedLores = new List<int>();
            CopyStoryPlannedLoresToList(story.plannedLores, plannedLores);
            return plannedLores.Count > 0;
        }

        private static void CopyCountersToDictionary(StringMap? map, Dictionary<string, int> target)
        {
            target.Clear();
            if (map == null)
                return;

            try
            {
                var keys = map.keys();
                while (keys.hasNext.Invoke())
                {
                    var key = keys.next.Invoke();
                    if (key == null)
                        continue;

                    var keyText = key.ToString();
                    if (string.IsNullOrWhiteSpace(keyText))
                        continue;

                    target[keyText] = ToInt(map.get(key));
                }
            }
            catch
            {
            }
        }

        private static void CopyNpcProgressToDictionary(EnumValueMap? map, Dictionary<int, int> target)
        {
            target.Clear();
            if (map == null)
                return;

            try
            {
                var keys = map.keys();
                while (keys.hasNext.Invoke())
                {
                    var key = keys.next.Invoke();
                    if (key is not NpcId npcId)
                        continue;

                    target[(int)npcId.Index] = ToInt(map.get(key));
                }
            }
            catch
            {
            }
        }

        private static void CopyStoryStringIntMapToDictionary(virtual_exists_get_iterator_keys_remove_set_toString_? map, Dictionary<string, int> target)
        {
            target.Clear();
            if (map == null)
                return;

            try
            {
                dynamic keys = map.keys.Invoke();
                while (keys.hasNext.Invoke())
                {
                    var keyObj = keys.next.Invoke();
                    if (keyObj == null)
                        continue;

                    var key = keyObj.ToString();
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    target[key] = ToInt(map.get.Invoke(keyObj));
                }
            }
            catch
            {
            }
        }

        private static void CopyStoryVisitedLoreRoomsToSet(virtual_exists_get_iterator_keys_remove_set_toString_? map, HashSet<string> target)
        {
            target.Clear();
            if (map == null)
                return;

            try
            {
                dynamic keys = map.keys.Invoke();
                while (keys.hasNext.Invoke())
                {
                    var keyObj = keys.next.Invoke();
                    if (keyObj == null)
                        continue;

                    var key = keyObj.ToString();
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    var raw = map.get.Invoke(keyObj);
                    var visited = raw is bool b ? b : ToInt(raw) != 0;
                    if (visited)
                        target.Add(key);
                }
            }
            catch
            {
            }
        }

        private static void CopyStoryPlannedLoresToList(ArrayBytes_Int? source, List<int> target)
        {
            target.Clear();
            if (source == null)
                return;

            var seen = new HashSet<int>();
            for (var i = 0; i < source.length; i++)
            {
                int planned;
                try
                {
                    planned = ToInt(source.getDyn(i));
                }
                catch
                {
                    continue;
                }

                if (seen.Add(planned))
                    target.Add(planned);
            }
        }

        private static StringMap BuildCountersMap(Dictionary<string, int> values)
        {
            var map = new StringMap();
            foreach (var kv in values)
                map.set(kv.Key.AsHaxeString(), kv.Value);
            return map;
        }

        private static EnumValueMap BuildNpcProgressMap(Dictionary<int, int> values)
        {
            var map = new EnumValueMap();
            foreach (var kv in values)
            {
                var npcId = CreateNpcIdFromIndex(kv.Key);
                if (npcId == null)
                    continue;

                map.set(npcId, kv.Value);
            }
            return map;
        }

        private static void ApplyStoryStringIntMap(virtual_exists_get_iterator_keys_remove_set_toString_? map, Dictionary<string, int> values)
        {
            if (map == null)
                return;

            try
            {
                var keysToRemove = new List<object>();
                dynamic keys = map.keys.Invoke();
                while (keys.hasNext.Invoke())
                {
                    var keyObj = keys.next.Invoke();
                    if (keyObj != null)
                        keysToRemove.Add(keyObj);
                }

                for (var i = 0; i < keysToRemove.Count; i++)
                {
                    try { map.remove.Invoke(keysToRemove[i]); } catch { }
                }
            }
            catch
            {
            }

            foreach (var kv in values)
            {
                try
                {
                    map.set.Invoke(kv.Key.AsHaxeString(), kv.Value);
                }
                catch
                {
                }
            }
        }

        private static void ApplyStoryVisitedLoreRoomsMap(virtual_exists_get_iterator_keys_remove_set_toString_? map, HashSet<string> values)
        {
            if (map == null)
                return;

            try
            {
                var keysToRemove = new List<object>();
                dynamic keys = map.keys.Invoke();
                while (keys.hasNext.Invoke())
                {
                    var keyObj = keys.next.Invoke();
                    if (keyObj != null)
                        keysToRemove.Add(keyObj);
                }

                for (var i = 0; i < keysToRemove.Count; i++)
                {
                    try { map.remove.Invoke(keysToRemove[i]); } catch { }
                }
            }
            catch
            {
            }

            foreach (var key in values)
            {
                try
                {
                    map.set.Invoke(key.AsHaxeString(), 1);
                }
                catch
                {
                }
            }
        }

        private static ArrayBytes_Int BuildStoryPlannedLoresArray(List<int> values)
        {
            var arr = new ArrayBytes_Int();
            var seen = new HashSet<int>();
            for (var i = 0; i < values.Count; i++)
            {
                var planned = values[i];
                if (!seen.Add(planned))
                    continue;

                try
                {
                    arr.push(planned);
                }
                catch
                {
                    try
                    {
                        arr.pushDyn(planned);
                    }
                    catch
                    {
                    }
                }
            }

            return arr;
        }

        private static void ApplyStoryState(
            User user,
            Dictionary<string, int> counters,
            Dictionary<int, int> npcProgress,
            int storyDataVersion,
            Dictionary<string, int> loreRoomRunIds,
            HashSet<string> visitedLoreRooms,
            List<int> plannedLores)
        {
            user.story = BuildStoryManager(
                counters,
                npcProgress,
                storyDataVersion,
                loreRoomRunIds,
                visitedLoreRooms,
                plannedLores);
            user.counters = BuildCountersMap(counters);
            user.npcs = BuildNpcProgressMap(npcProgress);
        }

        private static void ClearUserStoryState(User user)
        {
            user.story = null;
            user.counters = new StringMap();
            user.npcs = new EnumValueMap();
        }

        private static StoryManager BuildStoryManager(
            Dictionary<string, int> counters,
            Dictionary<int, int> npcProgress,
            int storyDataVersion,
            Dictionary<string, int> loreRoomRunIds,
            HashSet<string> visitedLoreRooms,
            List<int> plannedLores)
        {
            var story = new StoryManager();
            try
            {
                story.onReload();
            }
            catch
            {
            }

            story.counters = BuildCountersMap(counters);
            story.npcProgresses = BuildNpcProgressMap(npcProgress);
            ApplyStoryStringIntMap(story.loreRoomRunIds, loreRoomRunIds);
            ApplyStoryVisitedLoreRoomsMap(story.visitedLoreRooms, visitedLoreRooms);
            story.plannedLores = BuildStoryPlannedLoresArray(plannedLores);
            story.storyDataVersion = storyDataVersion;
            return story;
        }

        private static ArrayObj? CloneItemProgress(ArrayObj? source)
        {
            if (source == null)
                return null;

            var arr = ArrayUtils.CreateDyn();
            for (int i = 0; i < source.length; i++)
            {
                var item = source.getDyn(i) as ItemProgress;
                if (item == null)
                    continue;
                var copy = new ItemProgress(item.itemId);
                copy.investedCells = item.investedCells;
                copy.isNew = item.isNew;
                copy.unlocked = item.unlocked;
                copy.__uid = item.__uid;
                arr.array.pushDyn(copy);
            }
            return (ArrayObj)arr.array;
        }

        private static ArrayObj? CloneMetaProgress(ArrayObj? source)
        {
            if (source == null)
                return null;

            var arr = ArrayUtils.CreateDyn();
            for (int i = 0; i < source.length; i++)
            {
                var item = source.getDyn(i) as MetaProgress;
                if (item == null)
                    continue;

                var copy = new MetaProgress(item.itemId);
                copy.investedCells = item.investedCells;
                copy.isNew = item.isNew;
                copy.unlocked = item.unlocked;
                copy.upgradeLevel = item.upgradeLevel;
                copy.n = item.n;
                copy.done = item.done;
                copy.metaLevel = item.metaLevel;
                arr.array.pushDyn(copy);
            }

            return (ArrayObj)arr.array;
        }

        private static ArrayObj? CloneItemList(ArrayObj? source)
        {
            if (source == null)
                return null;

            var arr = ArrayUtils.CreateDyn();
            for (int i = 0; i < source.length; i++)
            {
                object? item = source.getDyn(i);
                arr.array.pushDyn(item);
            }
            return (ArrayObj)arr.array;
        }

        private static IntMap? CloneIntMap(IntMap? source)
        {
            if (source == null)
                return null;

            var map = new IntMap();
            try
            {
                var keys = source.keys();
                while (keys.hasNext.Invoke())
                {
                    var key = keys.next.Invoke();
                    map.set(key, ToInt(source.get(key)));
                }
            }
            catch
            {
            }

            return map;
        }

        internal static bool TryBuildSafeSaveUser(User currentUser, bool onlyGameData, out User? saveUser)
        {
            saveUser = null;
            if (currentUser == null || string.IsNullOrWhiteSpace(_origProgressPayload))
                return false;

            try
            {
                var cloneBytes = Save.Class.genSave.Invoke(currentUser, onlyGameData);
                if (cloneBytes == null)
                    return false;

                var clone = Save.Class.readSave.Invoke(cloneBytes);
                if (clone == null)
                    return false;

                if (!TryApplyProgressPayload(clone, _origProgressPayload))
                    return false;

                saveUser = clone;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryApplyProgressPayload(User target, string? payload)
        {
            if (target == null || string.IsNullOrWhiteSpace(payload))
                return false;

            if (!TryDeserializeProgressUser(payload, out var source) || source == null)
                return false;

            ApplyProgressSnapshot(target, source);
            return true;
        }

        private static void ApplyProgressSnapshot(User target, User source)
        {
            if (target == null || source == null)
                return;

            var preservedHeroSkin = !string.IsNullOrWhiteSpace(_origHeroSkin)
                ? _origHeroSkin
                : CleanSkin(target.heroSkin?.ToString());
            var preservedHeroHeadSkin = !string.IsNullOrWhiteSpace(_origHeroHeadSkin)
                ? _origHeroHeadSkin
                : CleanSkin(target.heroHeadSkin?.ToString());

            var legacyCounters = new Dictionary<string, int>(StringComparer.Ordinal);
            CopyCountersToDictionary(source.counters, legacyCounters);
            var legacyNpcProgress = new Dictionary<int, int>();
            CopyNpcProgressToDictionary(source.npcs, legacyNpcProgress);
            var sourceStory = source.story;
            if (sourceStory != null)
            {
                if (sourceStory.counters == null)
                    sourceStory.counters = BuildCountersMap(legacyCounters);
                if (sourceStory.npcProgresses == null)
                    sourceStory.npcProgresses = BuildNpcProgressMap(legacyNpcProgress);
            }

            target.flags = source.flags;
            target.userId = source.userId;
            target.deathMoney = source.deathMoney;
            target.deathCells = source.deathCells;
            target.bossRuneActivated = GetEffectiveBossRune(source);
            target.tutorial = source.tutorial;
            target.counters = sourceStory?.counters ?? BuildCountersMap(legacyCounters);
            target.npcs = sourceStory?.npcProgresses ?? BuildNpcProgressMap(legacyNpcProgress);
            target.story = sourceStory;
            target.itemMeta = null;
            target.userStats = source.userStats;
            target.activeMods = CloneItemList(source.activeMods);
            target.meta = CloneMetaProgress(source.meta);
            target.metaItems = CloneItemList(source.metaItems);
            target.achievements = CloneItemList(source.achievements);
            target.localAchievements = CloneItemList(source.localAchievements);
            target.deathItem = source.deathItem;
            target.consecutiveCompletedRuns = source.consecutiveCompletedRuns;
            ApplyHeroCosmetics(target, preservedHeroSkin, preservedHeroHeadSkin);
            MirrorStoryStateToSaveUser(target);

            try
            {
                target.userStats?.init();
            }
            catch
            {
            }

            try
            {
                target.onReload();
            }
            catch
            {
            }

            ApplyHeroCosmetics(target, preservedHeroSkin, preservedHeroHeadSkin);
            MirrorStoryStateToSaveUser(target);

            try
            {
                target.story?.onReload();
            }
            catch
            {
            }

            var targetMeta = EnsureItemMeta(target, target.itemMeta);
            targetMeta._user = target;
            if (source.itemMeta != null)
            {
                targetMeta.itemProgress = CloneItemProgress(source.itemMeta.itemProgress) ?? EnsureArray(targetMeta.itemProgress);
                targetMeta.permanentItems = CloneItemList(source.itemMeta.permanentItems) ?? EnsureArray(targetMeta.permanentItems);
                targetMeta.forgeInvestedCells = CloneIntMap(source.itemMeta.forgeInvestedCells) ?? new IntMap();
            }

            try
            {
                targetMeta.onReload();
            }
            catch
            {
            }

            try
            {
                targetMeta.revealAllBaseItems();
            }
            catch
            {
            }

            try
            {
                targetMeta.cleanDuplicatedItemProgress();
            }
            catch
            {
            }

            target.itemMeta = targetMeta;

            try
            {
                target.br_setActivated(GetEffectiveBossRune(source));
            }
            catch
            {
                target.bossRuneActivated = GetEffectiveBossRune(source);
            }
        }

        private static void MirrorStoryStateToSaveUser(User user)
        {
            if (user == null)
                return;

            try
            {
                var saveUser = user.mainGameData?.sUser;
                if (saveUser == null || ReferenceEquals(saveUser, user))
                    return;

                saveUser.story = user.story;
                saveUser.counters = user.counters;
                saveUser.npcs = user.npcs;
            }
            catch
            {
            }
        }

        private static void ApplyHeroCosmetics(User? user, string? heroSkin, string? heroHeadSkin)
        {
            if (user == null)
                return;

            if (!string.IsNullOrWhiteSpace(heroSkin))
                user.heroSkin = heroSkin.AsHaxeString();

            if (!string.IsNullOrWhiteSpace(heroHeadSkin))
                user.heroHeadSkin = heroHeadSkin.AsHaxeString();
        }

        private static string? BuildProgressPayload(User user)
        {
            if (user == null)
                return null;

            try
            {
                var prepared = false;
                try
                {
                    prepared = user.prepareSave();
                }
                catch (Exception ex)
                {
                    _log?.Debug(ex, "[NetMod] user.prepareSave() threw while building progress payload");
                }

                if (!prepared)
                    _log?.Debug("[NetMod] user.prepareSave() returned false; trying packed progress payload anyway");

                var saveBytes = Save.Class.genSave.Invoke(user, true);
                if (saveBytes != null)
                {
                    var saveRaw = CopyBytesToManaged(saveBytes);
                    return saveRaw.Length == 0 ? "P2|" : "P2|" + Convert.ToBase64String(saveRaw);
                }

                _log?.Warning("[NetMod] Failed to build packed progress payload: save bytes were null");
            }
            catch (Exception ex)
            {
                _log?.Warning(ex, "[NetMod] Failed to build packed progress payload");
            }

            if (TrySerializeUserBytes(user, out var userBytes) && userBytes != null)
            {
                var userRaw = CopyBytesToManaged(userBytes);
                return userRaw.Length == 0 ? "P1|" : "P1|" + Convert.ToBase64String(userRaw);
            }

            _log?.Warning("[NetMod] Failed to build progress payload from both packed-save and raw-user paths");
            return null;
        }

        private static bool TryDeserializeProgressUser(string payload, out User? user)
        {
            user = null;
            if (string.IsNullOrWhiteSpace(payload))
                return false;

            var isPackedSavePayload = payload.StartsWith("P2|", StringComparison.Ordinal);
            var encoded =
                payload.StartsWith("P2|", StringComparison.Ordinal) ? payload[3..] :
                payload.StartsWith("P1|", StringComparison.Ordinal) ? payload[3..] :
                payload;
            byte[] raw;
            try
            {
                raw = string.IsNullOrEmpty(encoded) ? Array.Empty<byte>() : Convert.FromBase64String(encoded);
            }
            catch
            {
                return false;
            }

            if (!TryCreateHaxeBytes(raw, out var bytes) || bytes == null)
                return false;

            try
            {
                if (isPackedSavePayload)
                {
                    user = Save.Class.readSave.Invoke(bytes);
                    return user != null;
                }

                var serializer = new dc.hxbit.Serializer
                {
                    refs = new IntMap()
                };
                var position = 0;
                serializer.beginLoad(bytes, Ref<int>.From(ref position));
                user = (User)(object)serializer.getRef(User.Class, User.Class.__clid);
                serializer.endLoad();
                user?.onReload();
                return user != null;
            }
            catch (Exception ex)
            {
                _log?.Warning(ex, "[NetMod] Failed to deserialize progress payload");
                user = null;
                return false;
            }
        }

        private static bool TrySerializeUserBytes(User user, out Bytes? bytes)
        {
            bytes = null;
            if (user == null)
                return false;

            try
            {
                var serializer = new dc.hxbit.Serializer();
                serializer.beginSave();
                var userRef = user.unnamedField0;
                if (userRef == null)
                    userRef = user.unnamedField0 = (virtual___uid_getCLID_getSerializeSchema_serialize_unserialize_unserializeInit_)(object)user;
                serializer.addKnownRef(userRef);
                var position = 0;
                bytes = serializer.endSave(Ref<int>.From(ref position));
                return bytes != null;
            }
            catch (Exception ex)
            {
                _log?.Warning(ex, "[NetMod] Failed to serialize raw user progress payload");
                bytes = null;
                return false;
            }
        }

        private static byte[] CopyBytesToManaged(Bytes bytes)
        {
            if (bytes == null || bytes.length <= 0 || bytes.b == IntPtr.Zero)
                return Array.Empty<byte>();

            var raw = new byte[bytes.length];
            Marshal.Copy(bytes.b, raw, 0, raw.Length);
            return raw;
        }

        private static bool TryCreateHaxeBytes(byte[] raw, out Bytes? bytes)
        {
            bytes = null;

            try
            {
                bytes = Bytes.Class.alloc.Invoke(raw.Length);
                if (bytes == null)
                    return false;

                if (raw.Length > 0 && bytes.b != IntPtr.Zero)
                    Marshal.Copy(raw, 0, bytes.b, raw.Length);

                return true;
            }
            catch
            {
                bytes = null;
                return false;
            }
        }

        private static int ToInt(object? value)
        {
            if (value == null)
                return 0;

            if (value is int i)
                return i;

            if (value is bool b)
                return b ? 1 : 0;

            if (value is IConvertible conv)
            {
                try
                {
                    return conv.ToInt32(CultureInfo.InvariantCulture);
                }
                catch { }
            }

            return 0;
        }

        private static ArrayObj EnsureArray(ArrayObj? source)
        {
            if (source != null)
                return source;
            return (ArrayObj)ArrayUtils.CreateDyn().array;
        }

        private static ItemMetaManager EnsureItemMeta(User user, ItemMetaManager? meta)
        {
            var result = meta ?? user.itemMeta ?? new ItemMetaManager(user);
            result.itemProgress = EnsureArray(result.itemProgress);
            result.permanentItems = EnsureArray(result.permanentItems);
            return result;
        }

        private static LevelGraphSync? CaptureLevelGraph(string levelId, LevelStruct graph)
        {
            var sync = new LevelGraphSync
            {
                V = 1,
                LevelId = levelId,
                ZLinkId = graph.zLinkId
            };

            var seenUids = new HashSet<string>(StringComparer.Ordinal);
            var all = graph.all;
            if (all != null)
            {
                for (int i = 0; i < all.length; i++)
                {
                    TryCaptureLevelGraphNode(all.getDyn(i), sync, seenUids);
                }
            }

            if (sync.Nodes.Count == 0 && graph.nodes != null)
            {
                try
                {
                    var keys = graph.nodes.keys();
                    while (keys.hasNext.Invoke())
                    {
                        var key = keys.next.Invoke();
                        if (key == null)
                            continue;
                        TryCaptureLevelGraphNode(graph.nodes.get(key), sync, seenUids);
                    }
                }
                catch
                {
                }
            }

            return sync;
        }

        private static void TryCaptureLevelGraphNode(object? candidate, LevelGraphSync sync, HashSet<string> seenUids)
        {
            if (candidate is not RoomNode node)
                return;

            var nodeSync = CaptureLevelGraphNode(node);
            if (nodeSync == null || string.IsNullOrWhiteSpace(nodeSync.Uid))
                return;

            if (!seenUids.Add(nodeSync.Uid))
                return;

            sync.Nodes.Add(nodeSync);
        }

        private static LevelGraphNodeSync? CaptureLevelGraphNode(RoomNode node)
        {
            var uid = node.uid?.ToString();
            var rType = node.rType?.ToString();
            if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(rType))
                return null;

            int? parentLinkConstraint = null;
            try
            {
                if (node.parentLinkConstraint is HaxeEnum plc)
                    parentLinkConstraint = plc.RawIndex;
            }
            catch
            {
            }

            return new LevelGraphNodeSync
            {
                Uid = uid,
                ParentUid = node.parent?.uid?.ToString(),
                SubTeleportUid = node.subTeleportTo?.uid?.ToString(),
                IsZRoot = node.isZRoot,
                RType = rType,
                Group = node.group,
                Id = node.id,
                Flags = node.flags,
                ForcedTemplateId = node.forcedTemplate?.id?.ToString(),
                ExitLevel = node.exitLevel?.ToString(),
                ExitName = node.exitName?.ToString(),
                ExitColor = node.exitColor,
                ChildPriority = node.childPriority,
                X = node.x,
                Y = node.y,
                SpawnDistance = node.spawnDistance,
                FillerWeight = node.fillerWeight,
                ParentLinkConstraint = parentLinkConstraint,
                ChildrenUids = CaptureRoomNodeUids(node.children),
                ZChildrenUids = CaptureRoomNodeUids(node.zChildren),
                Npcs = CaptureNpcIds(node.npcs),
                ZLinks = CaptureZLinks(node.zLinks),
                GenData = CaptureLevelGraphGenData(node.genData)
            };
        }

        private static List<string> CaptureRoomNodeUids(ArrayObj? nodes)
        {
            var result = new List<string>();
            if (nodes == null)
                return result;

            for (int i = 0; i < nodes.length; i++)
            {
                try
                {
                    if (nodes.getDyn(i) is not RoomNode node)
                        continue;

                    var uid = node.uid?.ToString();
                    if (!string.IsNullOrEmpty(uid))
                        result.Add(uid);
                }
                catch
                {
                }
            }

            return result;
        }

        private static List<int> CaptureNpcIds(ArrayObj? npcs)
        {
            var result = new List<int>();
            if (npcs == null)
                return result;

            for (int i = 0; i < npcs.length; i++)
            {
                try
                {
                    if (npcs.getDyn(i) is NpcId npcId)
                        result.Add((int)npcId.Index);
                }
                catch
                {
                }
            }

            return result;
        }

        private static List<LevelGraphZLinkSync> CaptureZLinks(ArrayObj? zLinks)
        {
            var result = new List<LevelGraphZLinkSync>();
            if (zLinks == null)
                return result;

            for (int i = 0; i < zLinks.length; i++)
            {
                try
                {
                    var link = zLinks.getDyn(i) as virtual_contentClue_dest_doorId_id_;
                    if (link == null)
                        continue;

                    var destUid = link.dest?.uid?.ToString();
                    if (string.IsNullOrWhiteSpace(destUid))
                        continue;

                    int? clue = null;
                    try
                    {
                        var contentClue = link.contentClue;
                    if (contentClue is HaxeEnum haxeEnum)
                        clue = haxeEnum.RawIndex;
                    }
                    catch
                    {
                    }

                    result.Add(new LevelGraphZLinkSync
                    {
                        Id = link.id,
                        DestUid = destUid,
                        DoorId = link.doorId?.ToString(),
                        ContentClue = clue
                    });
                }
                catch
                {
                }
            }

            return result;
        }

        private static LevelGraphGenDataSync? CaptureLevelGraphGenData(virtual_altarItemGroup_brLegendaryMultiTreasure_broken_cells_doorCost_doorCurse_flaskRefill_forcedMerchantType_forcePauseTimer_isCliffPath_itemInWall_itemLevelBonus_killsMultiTreasure_locked_maxPerks_mins_noHealingShop_shouldBeFlipped_specificBiome_subTeleportTo_timedMultiTreasure_zDoorLock_zDoorType_? genData)
        {
            if (genData == null)
                return null;

            var result = new LevelGraphGenDataSync();
            var hasAny = false;

            try
            {
                var v = genData.specificBiome?.ToString();
                if (!string.IsNullOrWhiteSpace(v))
                {
                    result.SpecificBiome = v;
                    hasAny = true;
                }
            }
            catch { }

            try
            {
                var v = genData.zDoorLock;
                if (v.HasValue)
                {
                    result.ZDoorLock = v;
                    hasAny = true;
                }
            }
            catch { }

            try
            {
                var v = genData.forcePauseTimer;
                if (v.HasValue)
                {
                    result.ForcePauseTimer = v;
                    hasAny = true;
                }
            }
            catch { }

            try
            {
                var v = genData.shouldBeFlipped;
                if (v.HasValue)
                {
                    result.ShouldBeFlipped = v;
                    hasAny = true;
                }
            }
            catch { }

            try
            {
                var v = genData.subTeleportTo;
                if (v.HasValue)
                {
                    result.GenSubTeleportTo = v;
                    hasAny = true;
                }
            }
            catch { }

            try
            {
                var zDoorType = CaptureZDoorType(genData.zDoorType);
                if (zDoorType != null)
                {
                    result.ZDoorType = zDoorType;
                    hasAny = true;
                }
            }
            catch { }

            return hasAny ? result : null;
        }

        private static LevelGraphZDoorTypeSync? CaptureZDoorType(ZDoorType? zDoorType)
        {
            if (zDoorType is null)
                return null;

            if (zDoorType is not HaxeEnum haxeEnum)
                return null;

            var result = new LevelGraphZDoorTypeSync
            {
                RawIndex = haxeEnum.RawIndex
            };

            switch (zDoorType)
            {
                case ZDoorType.BossRune bossRune:
                    result.IntParam0 = bossRune.Param0;
                    break;
                case ZDoorType.PerfectKills perfectKills:
                    result.IntParam0 = perfectKills.Param0;
                    break;
                case ZDoorType.Timed timed:
                    result.DoubleParam0 = timed.Param0;
                    break;
            }

            return result;
        }

        private static void ApplyLevelGraphGenData(RoomNode node, LevelGraphGenDataSync? genData)
        {
            if (node == null || genData == null)
                return;

            // WARNING:
            // Mutating RoomNode.genData from C# (even via Reflect) can corrupt HL objects and later
            // explode as unrelated casts like "tool.InventItem -> level.LevelMap".
            // Keep genData sync capture for diagnostics, but do not apply it here until a native-safe path exists.
        }

        private static bool ApplyLevelGraph(LevelStruct target, LevelGraphSync sync, out RoomNode? rebuiltRoot, out string reason)
        {
            rebuiltRoot = null;
            reason = string.Empty;
            if (sync.Nodes == null || sync.Nodes.Count == 0)
            {
                reason = "no nodes";
                return false;
            }

            try
            {
                var localNodesByUid = CaptureExistingRoomNodesByUid(target);

                target.nodes = new StringMap();
                target.all = (ArrayObj)ArrayUtils.CreateDyn().array;
                target.zLinkId = sync.ZLinkId;

                var byUid = new Dictionary<string, RoomNode>(StringComparer.Ordinal);
                var syncByUid = new Dictionary<string, LevelGraphNodeSync>(StringComparer.Ordinal);
                var orderedNodes = new List<RoomNode>(sync.Nodes.Count);

                for (int i = 0; i < sync.Nodes.Count; i++)
                {
                    var src = sync.Nodes[i];
                    if (src == null || string.IsNullOrWhiteSpace(src.Uid) || string.IsNullOrWhiteSpace(src.RType))
                        continue;

                    if (byUid.ContainsKey(src.Uid))
                        continue;

                    var ctorGroup = src.Group;
                    var node = new RoomNode(src.RType.AsHaxeString(), Ref<int>.From(ref ctorGroup), target, null);
                    node.uid = src.Uid.AsHaxeString();
                    node.rType = src.RType.AsHaxeString();
                    node.group = src.Group;
                    node.id = src.Id;
                    node.flags = src.Flags;
                    node.childPriority = src.ChildPriority;
                    node.x = src.X;
                    node.y = src.Y;
                    node.spawnDistance = src.SpawnDistance;
                    node.fillerWeight = src.FillerWeight;
                    node.exitLevel = string.IsNullOrWhiteSpace(src.ExitLevel) ? null : src.ExitLevel.AsHaxeString();
                    node.exitName = string.IsNullOrWhiteSpace(src.ExitName) ? null : src.ExitName.AsHaxeString();
                    node.exitColor = src.ExitColor;

                    if (!string.IsNullOrWhiteSpace(src.ForcedTemplateId))
                    {
                        try
                        {
                            node.forceTemplate(src.ForcedTemplateId.AsHaxeString());
                        }
                        catch
                        {
                            try
                            {
                                node.forcedTemplate = (virtual_active_flags_group_id_type_)(object)dc.Data.Class.room.byId.get(src.ForcedTemplateId.AsHaxeString());
                            }
                            catch
                            {
                            }
                        }

                        // Keep payload fields authoritative even if native forceTemplate mutates them.
                        node.rType = src.RType.AsHaxeString();
                        node.group = src.Group;
                    }

                    if (src.ParentLinkConstraint.HasValue)
                    {
                        var constraint = CreateLinkConstraintFromIndex(src.ParentLinkConstraint.Value);
                        if (constraint is not null)
                            node.parentLinkConstraint = constraint;
                    }

                    if (src.Npcs != null)
                    {
                        for (int n = 0; n < src.Npcs.Count; n++)
                        {
                            var npc = CreateNpcIdFromIndex(src.Npcs[n]);
                            if (npc is not null)
                                node.npcs.pushDyn(npc);
                        }
                    }

                    if (localNodesByUid.TryGetValue(src.Uid, out var localNode))
                    {
                        try
                        {
                            if (localNode.genData != null)
                                node.genData = localNode.genData;
                        }
                        catch
                        {
                        }
                    }

                    ApplyLevelGraphGenData(node, src.GenData);

                    orderedNodes.Add(node);
                    byUid[src.Uid] = node;
                    syncByUid[src.Uid] = src;
                }

                // Populate struct lookup early so RoomNode.addZChild() can resolve @struct.getId(uid).
                var earlyAll = ArrayUtils.CreateDyn();
                var earlyNodes = new StringMap();
                for (int i = 0; i < sync.Nodes.Count; i++)
                {
                    var src = sync.Nodes[i];
                    if (src == null || string.IsNullOrWhiteSpace(src.Uid))
                        continue;
                    if (!byUid.TryGetValue(src.Uid, out var node))
                        continue;
                    earlyAll.array.pushDyn(node);
                    earlyNodes.set(src.Uid.AsHaxeString(), node);
                }
                target.all = (ArrayObj)earlyAll.array;
                target.nodes = earlyNodes;

                for (int i = 0; i < sync.Nodes.Count; i++)
                {
                    var src = sync.Nodes[i];
                    if (src == null || string.IsNullOrWhiteSpace(src.Uid))
                        continue;

                    if (!byUid.TryGetValue(src.Uid, out var node))
                        continue;

                    try { node.set_isZRoot(src.IsZRoot); } catch { }

                    if (!string.IsNullOrWhiteSpace(src.ParentUid) && byUid.TryGetValue(src.ParentUid, out var parent))
                        node.set_parent(parent);
                    else
                        node.set_parent(null);
                }

                for (int i = 0; i < sync.Nodes.Count; i++)
                {
                    var src = sync.Nodes[i];
                    if (src == null || string.IsNullOrWhiteSpace(src.Uid))
                        continue;

                    if (!byUid.TryGetValue(src.Uid, out var node))
                        continue;

                    // Rebuild Z-links through native RoomNode.addZChild to keep HL object layout valid.
                    if (!src.IsZRoot || string.IsNullOrWhiteSpace(src.ParentUid))
                        continue;
                    if (!byUid.TryGetValue(src.ParentUid, out var parent))
                        continue;

                    ZDoorContentClue? clue = null;
                    string? parentDoorId = null;
                    string? childDoorId = null;
                    if (syncByUid.TryGetValue(src.ParentUid, out var parentSrc))
                    {
                        if (TryFindZLinkSync(parentSrc.ZLinks, src.Uid, out var parentToChild))
                        {
                            parentDoorId = parentToChild.DoorId;
                            if (parentToChild.ContentClue.HasValue)
                                clue = CreateZDoorContentClueFromIndex(parentToChild.ContentClue.Value);
                        }
                    }
                    if (TryFindZLinkSync(src.ZLinks, src.ParentUid, out var childToParent))
                        childDoorId = childToParent.DoorId;

                    try
                    {
                        parent.addZChild(node, clue);
                    }
                    catch
                    {
                        // If native rebuild fails, leave local z-links and continue; reason will surface later in apply/debug logs.
                    }

                    if (parentDoorId != null)
                        TrySetZLinkDoorId(parent, node, parentDoorId);
                    if (childDoorId != null)
                        TrySetZLinkDoorId(node, parent, childDoorId);
                }

                target.zLinkId = sync.ZLinkId;

                // Rebuild child arrays in host order. Parent pointers are already set above.
                for (int i = 0; i < sync.Nodes.Count; i++)
                {
                    var src = sync.Nodes[i];
                    if (src == null || string.IsNullOrWhiteSpace(src.Uid))
                        continue;
                    if (!byUid.TryGetValue(src.Uid, out var node))
                        continue;

                    node.children = BuildRoomNodeArrayByUid(src.ChildrenUids, byUid);
                    node.zChildren = BuildRoomNodeArrayByUid(src.ZChildrenUids, byUid);
                }

                for (int i = 0; i < sync.Nodes.Count; i++)
                {
                    var src = sync.Nodes[i];
                    if (src == null || string.IsNullOrWhiteSpace(src.Uid))
                        continue;
                    if (!byUid.TryGetValue(src.Uid, out var node))
                        continue;

                    if (!string.IsNullOrWhiteSpace(src.SubTeleportUid) && byUid.TryGetValue(src.SubTeleportUid, out var subTp))
                        node.subTeleportTo = subTp;
                    else
                        node.subTeleportTo = null;
                }

                var rebuiltAll = ArrayUtils.CreateDyn();
                var rebuiltNodes = new StringMap();
                for (int i = 0; i < sync.Nodes.Count; i++)
                {
                    var src = sync.Nodes[i];
                    if (src == null || string.IsNullOrWhiteSpace(src.Uid))
                        continue;
                    if (!byUid.TryGetValue(src.Uid, out var node))
                        continue;

                    rebuiltAll.array.pushDyn(node);
                    rebuiltNodes.set(src.Uid.AsHaxeString(), node);
                }

                target.all = (ArrayObj)rebuiltAll.array;
                target.nodes = rebuiltNodes;

                if (!string.IsNullOrWhiteSpace(sync.RootUid) && byUid.TryGetValue(sync.RootUid, out var explicitRoot))
                {
                    rebuiltRoot = explicitRoot;
                }
                else
                {
                    for (int i = 0; i < sync.Nodes.Count; i++)
                    {
                        var src = sync.Nodes[i];
                        if (src == null || string.IsNullOrWhiteSpace(src.Uid))
                            continue;
                        if (src.IsZRoot)
                            continue;
                        if (!string.IsNullOrWhiteSpace(src.ParentUid))
                            continue;

                        if (byUid.TryGetValue(src.Uid, out var inferredRoot))
                        {
                            rebuiltRoot = inferredRoot;
                            break;
                        }
                    }
                }

                if (rebuiltRoot == null)
                {
                    reason = "rebuilt root not found";
                    return false;
                }

                try
                {
                    LogGenericZDoorDiagnostics(sync, byUid);
                }
                catch
                {
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        private static NpcId? CreateNpcIdFromIndex(int index)
        {
            return CreateEnumByIndex<NpcId, NpcId.Indexes>(index);
        }

        private static Dictionary<string, RoomNode> CaptureExistingRoomNodesByUid(LevelStruct target)
        {
            var result = new Dictionary<string, RoomNode>(StringComparer.Ordinal);
            if (target == null)
                return result;

            try
            {
                var all = target.all;
                if (all == null)
                    return result;

                for (int i = 0; i < all.length; i++)
                {
                    if (all.getDyn(i) is not RoomNode node)
                        continue;

                    var uid = node.uid?.ToString();
                    if (string.IsNullOrWhiteSpace(uid))
                        continue;

                    if (!result.ContainsKey(uid))
                        result[uid] = node;
                }
            }
            catch
            {
            }

            return result;
        }

        private static LinkConstraint? CreateLinkConstraintFromIndex(int index)
        {
            return index switch
            {
                0 => new LinkConstraint.All(),
                1 => new LinkConstraint.NeverDown(),
                2 => new LinkConstraint.NeverUp(),
                3 => new LinkConstraint.NeverRight(),
                4 => new LinkConstraint.NeverLeft(),
                5 => new LinkConstraint.HorizontalOnly(),
                6 => new LinkConstraint.VerticalOnly(),
                7 => new LinkConstraint.HorizontalLevelDirOnly(),
                8 => new LinkConstraint.RightOnly(),
                9 => new LinkConstraint.LeftOnly(),
                10 => new LinkConstraint.UpOnly(),
                11 => new LinkConstraint.DownOnly(),
                _ => null
            };
        }

        private static ZDoorContentClue? CreateZDoorContentClueFromIndex(int index)
        {
            return CreateEnumByIndex<ZDoorContentClue, ZDoorContentClue.Indexes>(index);
        }

        private static ZDoorType? CreateZDoorTypeFromSync(LevelGraphZDoorTypeSync? sync)
        {
            if (sync == null)
                return null;

            try
            {
                return sync.RawIndex switch
                {
                    0 => new ZDoorType.BossRune(sync.IntParam0 ?? 0),
                    1 => new ZDoorType.PerfectKills(sync.IntParam0 ?? 0),
                    2 => new ZDoorType.Timed(sync.DoubleParam0 ?? 0d),
                    3 => new ZDoorType.Conditional(),
                    4 => new ZDoorType.TumulusAntichamber(),
                    5 => new ZDoorType.CliffEnigma(),
                    6 => new ZDoorType.TrainingArena(),
                    7 => new ZDoorType.PurpleTeleport(),
                    8 => new ZDoorType.BossRushTeleport(),
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        private static TEnum? CreateEnumByIndex<TEnum, TIndex>(int index)
            where TEnum : class
            where TIndex : struct, Enum
        {
            if (!Enum.IsDefined(typeof(TIndex), index))
                return null;

            var name = Enum.GetName(typeof(TIndex), index);
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var nested = typeof(TEnum).GetNestedType(name, System.Reflection.BindingFlags.Public);
            if (nested == null)
                return null;

            try
            {
                return Activator.CreateInstance(nested) as TEnum;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryFindZLinkSync(List<LevelGraphZLinkSync>? zLinks, string? destUid, out LevelGraphZLinkSync result)
        {
            result = null!;
            if (zLinks == null || string.IsNullOrWhiteSpace(destUid))
                return false;

            for (int i = 0; i < zLinks.Count; i++)
            {
                var item = zLinks[i];
                if (item == null || string.IsNullOrWhiteSpace(item.DestUid))
                    continue;
                if (!string.Equals(item.DestUid, destUid, StringComparison.Ordinal))
                    continue;
                result = item;
                return true;
            }

            return false;
        }

        private static void TrySetZLinkDoorId(RoomNode from, RoomNode dest, string doorId)
        {
            if (from == null || dest == null)
                return;

            try
            {
                var zLinks = from.zLinks;
                if (zLinks == null)
                    return;

                for (int i = 0; i < zLinks.length; i++)
                {
                    var link = zLinks.getDyn(i) as virtual_contentClue_dest_doorId_id_;
                    if (link == null)
                        continue;
                    if (!ReferenceEquals(link.dest, dest))
                        continue;
                    link.doorId = doorId.AsHaxeString();
                    return;
                }
            }
            catch
            {
            }
        }

        private static void LogGenericZDoorDiagnostics(LevelGraphSync sync, Dictionary<string, RoomNode> byUid)
        {
            if (_log == null || sync.Nodes == null)
                return;

            for (int i = 0; i < sync.Nodes.Count; i++)
            {
                var src = sync.Nodes[i];
                if (src == null || !string.Equals(src.RType, "GenericZDoor", StringComparison.Ordinal))
                    continue;
                if (!byUid.TryGetValue(src.Uid, out var node))
                    continue;

                var childInfo = new List<string>();
                try
                {
                    var children = node.children;
                    if (children != null)
                    {
                        for (int c = 0; c < children.length; c++)
                        {
                            if (children.getDyn(c) is not RoomNode child)
                                continue;
                            var plc = "null";
                            var payloadPlc = "null";
                            try
                            {
                                if (child.parentLinkConstraint is HaxeEnum he)
                                    plc = he.RawIndex.ToString(CultureInfo.InvariantCulture);
                            }
                            catch { }
                            try
                            {
                                var childUid = child.uid?.ToString();
                                if (!string.IsNullOrWhiteSpace(childUid) &&
                                    sync.Nodes != null)
                                {
                                    for (int s = 0; s < sync.Nodes.Count; s++)
                                    {
                                        var childSrc = sync.Nodes[s];
                                        if (childSrc == null || !string.Equals(childSrc.Uid, childUid, StringComparison.Ordinal))
                                            continue;
                                        if (childSrc.ParentLinkConstraint.HasValue)
                                            payloadPlc = childSrc.ParentLinkConstraint.Value.ToString(CultureInfo.InvariantCulture);
                                        break;
                                    }
                                }
                            }
                            catch { }
                            childInfo.Add($"{child.uid}:{plc}/p{payloadPlc}");
                        }
                    }
                }
                catch { }

                var zdoorInfo = new List<string>();
                try
                {
                    var zLinks = node.zLinks;
                    if (zLinks != null)
                    {
                        for (int z = 0; z < zLinks.length; z++)
                        {
                            var link = zLinks.getDyn(z) as virtual_contentClue_dest_doorId_id_;
                            if (link == null)
                                continue;
                            zdoorInfo.Add(link.doorId?.ToString() ?? "null");
                        }
                    }
                }
                catch { }

                _log.Information(
                    "[NetMod] GenericZDoor diag {LevelId} uid={Uid} rType={RType} g={Group} forced={Forced} runtimeForced={RuntimeForced} parent={Parent} isZ={IsZ} children={ChildCount}[{ChildInfo}] zLinks={ZCount}[{ZInfo}] payloadChildren={PChild} payloadZ={PZ}",
                    sync.LevelId,
                    src.Uid,
                    src.RType ?? "null",
                    src.Group,
                    src.ForcedTemplateId ?? "null",
                    node.forcedTemplate?.id?.ToString() ?? "null",
                    src.ParentUid ?? "null",
                    src.IsZRoot,
                    node.children?.length ?? -1,
                    string.Join(",", childInfo),
                    node.zLinks?.length ?? -1,
                    string.Join(",", zdoorInfo),
                    src.ChildrenUids?.Count ?? 0,
                    src.ZLinks?.Count ?? 0);
            }
        }

        private static ArrayObj BuildRoomNodeArrayByUid(List<string>? orderedUids, Dictionary<string, RoomNode> byUid)
        {
            var arr = ArrayUtils.CreateDyn();
            if (orderedUids == null)
                return (ArrayObj)arr.array;

            for (int i = 0; i < orderedUids.Count; i++)
            {
                var uid = orderedUids[i];
                if (string.IsNullOrWhiteSpace(uid))
                    continue;
                if (!byUid.TryGetValue(uid, out var node))
                    continue;
                arr.array.pushDyn(node);
            }

            return (ArrayObj)arr.array;
        }

    }
}
