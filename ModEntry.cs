
using ModCore.Events.Interfaces.Game;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Mods;
using System.Diagnostics;
using System.Net;
using dc.en;
using dc.en.inter;
using dc.pr;
using ModCore.Utilities;
using ModCore.Modules;
using dc.level;
using dc.hl.types;
using dc;
using dc.shader;
using dc.libs.heaps.slib;
using Rand = dc.libs.Rand;
using dc.h3d.mat;
using dc.ui.hud;
using dc.h2d;
using dc.hxbit;
using Hashlink.Virtuals;
using dc.tool;
using dc.tool.weap;
using dc.tool.atk;
using dc.tool.mainSkills;
using dc.hxd;
using System.Timers;
using HaxeProxy.Runtime;
using dc.en.mob;
using dc.haxe;
using dc.cine;
using CineHookInitialize;
using Serilog.Core;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using ModCore.Events;
using DeadCellsMultiplayerMod.Mobs.MobsSynchronization;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using DeadCellsMultiplayerMod.MultiplayerModUI;
using DeadCellsMultiplayerMod.MultiplayerModUI.Minimap;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using DeadCellsMultiplayerMod.MultiplayerModUI.LevelExit;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using DeadCellsMultiplayerMod.Tools.ModLang;
using DeadCellsMultiplayerMod.KingHead;
using dc.steam.ugc;
using DeadCellsMultiplayerMod.Mobs.Levelinit;


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

        private NetRole _netRole = NetRole.None;
        public static NetNode? _net;

        public dc.pr.Game? game;

        public static GhostKing[] clients = new GhostKing[NetNode.MaxClientSlots];
        public static Kinghead?[] clientHeads = new Kinghead?[NetNode.MaxClientSlots];
        public static string?[] clientLabels = new string?[NetNode.MaxClientSlots];
        public static int[] clientIds = new int[NetNode.MaxClientSlots];
        public static string?[] clientSkins = new string?[NetNode.MaxClientSlots];
        public static string?[] clientHeadSkins = new string?[NetNode.MaxClientSlots];
        public static Hero me = null;
        public static GhostHero _ghost = null;

        private GameDataSync gds;

        private string? _lastAnimSent;
        private int? _lastAnimQueueSent;
        private bool? _lastAnimGSent;
        private double _animResendElapsed;
        private double? _lastAnimPlayRatio;
        private long _suppressHeroAnimUntilTicks;
        private string? _lastSentHeroSkin;
        private string? _lastSentHeroHeadSkin;

        public static MiniMap miniMap;

        public static bool kingInitialized = false;

        public string levelId;

        public static int remotePlayerId = -1;

        public string remoteSkin;
        public string remoteHeadSkin;

        public string lastHeadAnim;
        public static ArrayDyn customHeads;

        public InventItem inventItem;
        private bool _inventorySyncGuard;
        private bool _localFakeDead;
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
        private const double DownedStateResendSeconds = 0.4;
        private const double DownedGhostBodyYOffsetPx = 40.0;
        private const double LocalReviveBodyYOffsetPx = 0.5;
        private const double PostRevivePositionLockSeconds = 0.0;
        private const string ReviveHintText = "Hold R to restore";
        private string _lastRoomStateLevelId = string.Empty;
        private int _lastRoomStateRoomId = int.MinValue;
        private const double RoomMismatchKeepDistancePx = 850;

        private sealed class RemoteDownedState
        {
            public int UserId;
            public double X;
            public double Y;
            public string LevelId = string.Empty;
            public long UpdatedAtTicks;
        }

        private readonly Dictionary<int, RemoteDownedState> _remoteDowned = new();
        private readonly Dictionary<int, RemoteDownedCorpse> _remoteDownedCines = new();
        private readonly HashSet<int> _downedAnnouncements = new();


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

            instance.remoteHeadSkin = NormalizeHeadSkin(skin);
        }

        public static string GetClientLabel(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= clientLabels.Length)
                return string.Empty;

            return clientLabels[slotIndex] ?? string.Empty;
        }

        internal static bool IsLocalPlayerDowned()
        {
            return Instance != null && Instance._localFakeDead;
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
        }

        public override void Initialize()
        {
            Instance = this;

            this.gds = new GameDataSync(Logger);
            MultiplayerModLang modLang = new MultiplayerModLang(this);
            CineHooks CineHooks = new CineHooks();
            MultiplayerUI MultiplayerUI = new MultiplayerUI(this, 0);
            Levelinit levelinit = new Levelinit(info);
            MobsSynchronization mobs = new MobsSynchronization(this);
            Minimapreveal minimapreveal = new Minimapreveal();
            LevelExitSync levelExitSync = new LevelExitSync(this);
            ConnectionUI.Initialize(this);
            GameMenu.Initialize(Logger);
            EventSystem.BroadcastEvent<IOnAdvancedModuleInitializing, ModEntry>(this);
        }

        void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
        {
            if (s_hooksInstalled)
                return;

            s_hooksInstalled = true;
            entry.Logger.Information("\x1b[32m[[ModEntry] Mod Initializing Hooks...]\x1b[0m ");
            Hook_Game.init += Hook_gameinit;
            Hook_Hero.wakeup += hook_hero_wakeup;
            Hook_Hero.onLevelChanged += hook_level_changed;
            Hook_User.newGame += GameDataSync.user_hook_new_game;
            Hook_User.prepareSave += Hook_User_prepareSave;
            Hook_User.serialize += Hook_User_serialize;
            Hook_User.unserialize += Hook_User_unserialize;
            Hook_AnimManager.play += Hook_AnimManager_play;
            Hook_MiniMap.track += Hook_MiniMap_track;
            Hook__LevelStruct.get += Hook__LevelStruct_get;
            Hook_Boot.update += hook_boot_update;
            Hook_Game.pause += Hook_Game_pause;
            Hook_Hero.kill += Hook_Hero_kill;
            Hook_Hero.onDie += Hook_Hero_onDie;
            Hook_Hero.startDeathCine += Hook_Hero_startDeathCine;
            Hook_Hero.onHeroDie += Hook_Hero_onHeroDie;
            Hook_ZDoor.onActivate += Hook_ZDoor_onActivate;
            Hook_Hero.applySkin += Hook_Hero_applySkin;
            Hook_HeroHead.initCustomHead += Hook_HeroHead_initCustomHead;
            // Hook_Hero.tryToApplyYoloPerk += Hook_Hero_tryToApplyYoloPerk;
            Hook__TitleScreen.__constructor__ += Hook_TitleScreen__constructor__;
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

                var skin = NormalizeBodySkin(rawSkin);

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
                var skin = NormalizeHeadSkin(dc.Main.Class.ME?.user?.heroHeadSkin?.ToString());
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
            var shouldRefresh = false;
            if (_netRole != NetRole.None &&
                _net != null &&
                me != null &&
                lp != null &&
                ReferenceEquals(lp, me) &&
                self != null)
            {
                try
                {
                    var targetLevelId = self.destMap?.id?.ToString();
                    var targetRoomId = self.linkId;
                    if (targetRoomId >= 0)
                    {
                        var targetAreaKey = targetRoomId;
                        try
                        {
                            var targetRoom = self.destMap?.getRoomById(targetRoomId);
                            var mapped = ComputeRoomAreaKey(targetRoom);
                            if (mapped >= 0)
                                targetAreaKey = mapped;
                        }
                        catch
                        {
                        }

                        SendRoomTarget(targetLevelId, targetAreaKey, force: true);
                        shouldRefresh = true;
                    }
                }
                catch
                {
                }
            }

            orig(self, lp, mob);

            if (shouldRefresh)
            {
                try { ReceiveGhostCoords(); } catch { }
            }
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

        private bool Hook_User_prepareSave(Hook_User.orig_prepareSave orig, User self)
        {
            if (_netRole == NetRole.Client)
            {
                var swapped = GameDataSync.SwapToOriginalUserData(self);
                try
                {
                    return orig(self);
                }
                finally
                {
                    if (swapped)
                        GameDataSync.RestoreRemoteUserData(self);
                }
            }


            return orig(self);
        }

        private void Hook_User_serialize(Hook_User.orig_serialize orig, User self, dc.hxbit.Serializer __ctx)
        {
            if (_netRole == NetRole.Client)
            {
                var swapped = GameDataSync.SwapToOriginalUserData(self);
                try
                {
                    orig(self, __ctx);
                }
                finally
                {
                    if (swapped)
                        GameDataSync.RestoreRemoteUserData(self);
                }
                return;
            }

            orig(self, __ctx);
        }

        private void Hook_User_unserialize(Hook_User.orig_unserialize orig, User self, dc.hxbit.Serializer v)
        {
            orig(self, v);
            if (_netRole == NetRole.Client)
                GameDataSync.CaptureOriginalUserData(self);
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


        private void Hook_TitleScreen__constructor__(Hook__TitleScreen.orig___constructor__ orig, TitleScreen playMusic, bool? titleLib)
        {
            orig(playMusic, titleLib);
            ConnectionUI connectionUI = new ConnectionUI(playMusic);
            playMusic.addChild(connectionUI);
            connectionUI.root.set_visible(false);

        }

        private void Hook_Game_pause(Hook_Game.orig_pause orig, dc.pr.Game self)
        {
            return;
        }


        private void Hook_MobsGen_addElites(Hook_MobsGen.orig_addElites orig, MobsGen self, ArrayObj mobsPerRooms)
        {
            orig(self, mobsPerRooms);
            dynamic mobs = mobsPerRooms.array.Count;
            dynamic b = mobsPerRooms.array;
            for (int i = 0; i < mobs; i++)
            {
                var m = b[i];
                // Logger.Information($"[DEBUG|MOB] mobs at index {i}: {m}");

            }
        }

        private void Hook_LevelGen_genmobs(Hook_LevelGen.orig_genMobs orig, LevelGen self, User maps, ArrayObj extraMobs, ArrayObj bonusTotalMobCount1, Ref<int> bonusTotalMobCount)
        {
            orig(self, maps, extraMobs, bonusTotalMobCount1, bonusTotalMobCount);
            dynamic count = extraMobs.array.Count;
            for (int i = 0; i < count; i++)
            {
                var mobs = extraMobs.array[i];
            }
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
            SendLevel(levelId);
            var net = _net;
            if (_netRole == NetRole.Host)
                GameDataSync.SendLevelSeed(levelId, rng, net);
            else if (_netRole == NetRole.Client)
            {
                GameDataSync.TryApplyRemoteSerializerSync();
                GameDataSync.TryApplyRemoteLevelSeed(levelId, rng);
            }
            return orig(user, l, rng);
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
                    Stopwatch.GetTimestamp() >= _suppressHeroAnimUntilTicks &&
                    !IsAttackAnim(play))
                    SendHeroAnim(play, queueAnim, g, force: true);
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
            ResetFakeDeathState(unlockLocalHero: true, sendNetworkUpState: false, clearRemoteDownedTracking: false, clearDownedAnnouncements: false);
            me = self;
            try { me._targetable = true; } catch { }
            SendLevel(levelId);
            orig(self, oldLevel);
            EnsureHeroVisibilityAfterRoomChange(me);
            if (_netRole == NetRole.None) return;
            var net = _net;
            var localId = net?.id ?? 0;
            if (_ghost == null)
                _ghost = new GhostHero(localId, game!, me, Logger, this);
            _ghost.SetLabel(me, GameMenu.Username);

            for (int i = 0; i < clients.Length; i++)
            {
                DisposeClientSlot(i, clearIdentity: false);
                rLastX[i] = 0;
                rLastY[i] = 0;
            }

            SendCurrentRoomStateIfNeeded(force: true);
            ReceiveGhostCoords();
        }


        public void hook_hero_wakeup(Hook_Hero.orig_wakeup orig, Hero self, Level lvl, int cx, int cy)
        {
            me = self;
            try { me._targetable = true; } catch { }
            orig(self, lvl, cx, cy);
            EnsureHeroVisibilityAfterRoomChange(me);
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

        }

        public void OnFrameUpdate(double dt)
        {
            if (!_ready) return;
            GameMenu.TickMenu(dt);

        }


        void IOnHeroUpdate.OnHeroUpdate(double dt)
        {
            if (me == null) return;
            TryRecoverMissedFakeDeathFromLife();
            if (!_localFakeDead)
                SendHeroCoords();
            SendCurrentRoomStateIfNeeded(force: false);
            ReceiveGhostCoords();
            UpdateFakeDeathFlow(dt);
            MaintainPostRevivePositionLock();
            ReceiveGhostWeapons();
            ReceiveGhostAttacks();
            UpdateGhostWeapons();
            UpdateGhostHeads();
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
                var head = clientHeads[i];
                if (head != null)
                {
                    head.updateHeadFx(ftime);
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
                string.Equals(_lastRoomStateLevelId, effectiveLevelId, StringComparison.Ordinal) &&
                _lastRoomStateRoomId == targetRoomId)
            {
                return;
            }

            net.SendRoomTarget(effectiveLevelId, targetRoomId);
            _lastRoomStateLevelId = effectiveLevelId;
            _lastRoomStateRoomId = targetRoomId;
        }

        private void SendCurrentRoomStateIfNeeded(bool force)
        {
            if (me == null)
                return;
            if (!TryGetHeroRoomSignature(me, out var currentLevelId, out var currentRoomId))
                return;

            SendRoomTarget(currentLevelId, currentRoomId, force);
        }

        private static bool TryGetHeroRoomSignature(Hero? hero, out string levelIdValue, out int roomId)
        {
            levelIdValue = string.Empty;
            roomId = -1;
            if (hero == null)
                return false;

            try
            {
                var level = hero._level;
                var map = level?.map;
                if (map == null)
                    return false;

                levelIdValue = map.id?.ToString() ?? string.Empty;
                Room? room = null;

                if (hero.lastRoomId >= 0)
                    room = map.getRoomById(hero.lastRoomId);
                if (room == null)
                    room = map.getRoomAt(hero.cx, hero.cy);
                if (room == null)
                    return false;

                roomId = ComputeRoomAreaKey(room);
                if (roomId < 0)
                    roomId = room.id;
                return roomId >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static int ComputeRoomAreaKey(Room? room)
        {
            if (room == null)
                return -1;

            try
            {
                var group = room.rGroup;
                if (group >= 0)
                    return group;
            }
            catch
            {
            }

            try
            {
                var parent = room.parent;
                if (parent != null && parent.id >= 0)
                    return parent.id;
            }
            catch
            {
            }

            try
            {
                if (room.id >= 0)
                    return room.id;
            }
            catch
            {
            }

            return -1;
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

            var cleaned = NormalizeBodySkin(skin);
            var prev = clientSkins[index];
            clientSkins[index] = cleaned;

            var client = clients[index];
            if (client != null)
            {
                if (!string.Equals(prev, cleaned, StringComparison.Ordinal))
                {
                    if (!instance.RecreateClientKing(index))
                        client.ApplyRemoteSkin(cleaned);
                }
                else if (client.spr == null)
                {
                    client.ApplyRemoteSkin(cleaned);
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

            var cleaned = NormalizeHeadSkin(skin);
            var prev = clientHeadSkins[index];
            clientHeadSkins[index] = cleaned;

            var client = clients[index];
            if (client != null)
                client.RemoteHeadSkinId = cleaned;

            if (!string.Equals(prev, cleaned, StringComparison.Ordinal) || client?.head == null)
                instance.RecreateClientHead(index);
        }

        private static string NormalizeHeadSkin(string? skin)
        {
            return string.IsNullOrWhiteSpace(skin)
                ? "BaseFlame"
                : skin.Replace("|", "/").Trim();
        }

        private static string NormalizeBodySkin(string? skin)
        {
            return string.IsNullOrWhiteSpace(skin)
                ? "PrisonerDefault"
                : skin.Replace("|", "/").Trim();
        }

        private bool RecreateClientKing(int slot)
        {
            if (slot < 0 || slot >= clients.Length)
                return false;

            var existing = clients[slot];
            if (existing == null)
                return false;

            var x = rLastX[slot];
            var y = rLastY[slot];
            var dir = 1;
            var targetable = true;
            try
            {
                if (existing.spr != null)
                {
                    x = existing.spr.x;
                    y = existing.spr.y;
                }
            }
            catch
            {
            }

            try { dir = existing.dir; } catch { }
            try { targetable = existing._targetable; } catch { }

            DisposeClientSlot(slot, clearIdentity: false);
            var recreated = EnsureClientKingSlot(slot);
            if (recreated == null)
                return false;

            try { recreated.setPosPixel(x, y); } catch { }
            try { recreated.dir = dir; } catch { }
            try { recreated._targetable = targetable; } catch { }
            rLastX[slot] = x;
            rLastY[slot] = y;
            return true;
        }

        private void RecreateClientHead(int slot)
        {
            if (slot < 0 || slot >= clients.Length)
                return;

            var client = clients[slot];
            if (client == null || me == null || me._level == null)
                return;

            var existing = clientHeads[slot];
            if (existing != null)
            {
                existing.dispose();
                clientHeads[slot] = null;
            }

            var desiredHead = NormalizeHeadSkin(client.RemoteHeadSkinId);
            var previousGlobalHead = remoteHeadSkin;
            remoteHeadSkin = desiredHead;
            try
            {
                bool fromUI = false;
                var newHead = new Kinghead(me, client, me._level, Logger);
                newHead.init(me._level, null, Ref<bool>.From(ref fromUI));
                clientHeads[slot] = newHead;
                client.head = newHead;
            }
            catch (Exception ex)
            {
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
            var localRoomId = -1;
            try
            {
                var map = me._level?.map;
                Room? localRoom = null;
                if (map != null)
                {
                    if (me.lastRoomId >= 0)
                        localRoom = map.getRoomById(me.lastRoomId);
                    if (localRoom == null)
                        localRoom = map.getRoomAt(me.cx, me.cy);
                }

                localRoomId = ComputeRoomAreaKey(localRoom);
                if (localRoomId < 0 && localRoom != null)
                    localRoomId = localRoom.id;
            }
            catch
            {
                localRoomId = -1;
            }

            foreach (var remote in remotes)
            {
                if (!TryGetClientIndex(localId, remote.Id, out var index))
                    continue;

                remotePlayerId = remote.Id;
                clientIds[index] = remote.Id;
                if (!ShouldKeepRemoteKingVisibleInRoom(remote, localLevelId, localRoomId))
                {
                    DisposeClientSlot(index, clearIdentity: false);
                    continue;
                }

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

        private bool ShouldKeepRemoteKingVisibleInRoom(NetNode.RemoteSnapshot remote, string localLevelId, int localRoomId)
        {
            if (string.IsNullOrWhiteSpace(localLevelId))
                return true;

            if (!string.IsNullOrWhiteSpace(localLevelId) &&
                !string.IsNullOrWhiteSpace(remote.LevelId) &&
                !string.Equals(remote.LevelId, localLevelId, StringComparison.Ordinal))
            {
                return false;
            }

            if (localRoomId < 0)
                return true;

            if (remote.HasRoom &&
                remote.RoomId.HasValue &&
                remote.RoomId.Value >= 0 &&
                !string.IsNullOrWhiteSpace(remote.RoomLevelId))
            {
                if (!string.Equals(remote.RoomLevelId, localLevelId, StringComparison.Ordinal))
                    return false;

                if (remote.RoomId.Value == localRoomId)
                    return true;

                // Transition/chest/teleporter boundaries can report different room ids
                // while players are still effectively in the same visible area.
                return ShouldKeepRemoteByProximity(remote);
            }

            return true;
        }

        private bool ShouldKeepRemoteByProximity(NetNode.RemoteSnapshot remote)
        {
            var hero = me;
            if (hero?.spr == null)
                return false;

            var dx = remote.X - hero.spr.x;
            var dy = remote.Y - hero.spr.y;
            if (!double.IsFinite(dx) || !double.IsFinite(dy))
                return false;

            var distSq = dx * dx + dy * dy;
            var maxDist = RoomMismatchKeepDistancePx;
            return distSq <= maxDist * maxDist;
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
            created.RemoteHeadSkinId = NormalizeHeadSkin(
                !string.IsNullOrWhiteSpace(knownHead) ? knownHead : remoteHeadSkin
            );
            RecreateClientHead(slot);

            if (!string.IsNullOrWhiteSpace(clientLabels[slot]))
                _ghost.SetLabel(created, clientLabels[slot]);

            return created;
        }

        private void DisposeClientSlot(int slot, bool clearIdentity)
        {
            if (slot < 0 || slot >= clients.Length)
                return;

            var head = clientHeads[slot];
            if (head != null)
            {
                try { head.dispose(); } catch { }
                clientHeads[slot] = null;
            }

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

        private void ReceiveGhostAttacks()
        {
            var net = _net;
            if (net == null || me == null) return;

            if (!net.TryConsumeRemoteAttacks(out var attacks))
                return;

            var localId = net.id;
            foreach (var attack in attacks)
            {
                ApplyRemoteWeaponUpdate(attack.Id, attack.Kind, attack.Slot, attack.PermanentId, attack.Ammo);
                if (!TryGetClientIndex(localId, attack.Id, out var index))
                    continue;

                var client = clients[index];
                if (client?.kingWeaponsManager == null) continue;
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

                animManager.play(anim.AsHaxeString(), queueAnim, g).loop(null);
                return;
            }

            animManager.play(anim.AsHaxeString(), queueAnim, g);
        }

        private static bool ShouldLoopRemoteAnim(string anim)
        {
            if(string.IsNullOrWhiteSpace(anim)) return false;
            var a = anim.Trim();

            // Don't ever force-loop weapon/hold-ish states; those should be driven by weapon replication.
            if(IsAttackAnim(a)) return false;
            if(a.IndexOf("guard", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if(a.IndexOf("defend", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            // Loop only locomotion/idles to avoid getting stuck in "hold/parry" forever.
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

            return false;
        }

        private void PlayGhostHeadAnim(GhostKing client, string anim)
        {
            if (client.head == null || client.head.customHeadSpr._animManager == null) return;
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
            _animResendElapsed = 0;
            _lastAnimPlayRatio = null;
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
                else
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

        private void StartHostWithEndpoint(IPEndPoint ep)
        {
            try
            {
                _net?.Dispose();
                ResetFakeDeathState(unlockLocalHero: true, sendNetworkUpState: false);
                ResetLocalSkinSendCache();

                _net = NetNode.CreateHost(Logger, ep);
                _netRole = NetRole.Host;
                GameMenu.SetRole(_netRole);
                GameMenu.NetRef = _net;
                ConnectionUI.NotifyConnectionsChanged();

                var lep = _net.ListenerEndpoint;
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

        private void StartClientWithEndpoint(IPEndPoint ep)
        {
            try
            {
                _net?.Dispose();
                ResetFakeDeathState(unlockLocalHero: true, sendNetworkUpState: false);
                ResetLocalSkinSendCache();

                _net = NetNode.CreateClient(Logger, ep);
                _netRole = NetRole.Client;
                GameMenu.SetRole(_netRole);
                GameMenu.NetRef = _net;
                ConnectionUI.NotifyConnectionsChanged();

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

        public void StopNetworkFromMenu()
        {
            var roleBeforeStop = _netRole;
            try
            {
                if (roleBeforeStop == NetRole.Client)
                    Logger.Information("[NetMod] Disconnecting client from host...");
                else if (roleBeforeStop == NetRole.Host)
                    Logger.Information("[NetMod] Disposing host server...");

                _net?.Dispose();
            }
            catch { }
            ResetFakeDeathState(unlockLocalHero: true, sendNetworkUpState: false);
            ResetLocalSkinSendCache();
            _net = null;
            _netRole = NetRole.None;
            GameMenu.NetRef = null;
            GameMenu.SetRole(_netRole);
            ConnectionUI.NotifyConnectionsChanged();
        }


    }
}
