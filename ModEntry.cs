
using ModCore.Events.Interfaces.Game;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Mods;
using System.Net;
using dc.en;
using dc.pr;
using ModCore.Utitities;
using ModCore.Modules;
using dc.level;
using dc.hl.types;
using dc;
using dc.shader;
using dc.libs.heaps.slib;
using dc.h3d.mat;
using dc.ui.hud;
using dc.h2d;
using Hashlink.Virtuals;
using dc.tool;
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
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using DeadCellsMultiplayerMod.Tools.ModLang;
using DeadCellsMultiplayerMod.KingHead;
using dc.steam.ugc;


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

        public static MiniMap miniMap;

        public static bool kingInitialized = false;

        public string levelId;

        public static int remotePlayerId = -1;

        public string remoteSkin;
        public string remoteHeadSkin;

        public string lastHeadAnim;
        public static ArrayDyn customHeads;

        public InventItem inventItem;
        private bool _inventoryAddGuard;


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

            instance.remoteHeadSkin = string.IsNullOrWhiteSpace(skin)
                ? "BaseFlame"
                : skin.Replace("|", "/").Trim();
        }

        public static string GetClientLabel(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= clientLabels.Length)
                return string.Empty;

            return clientLabels[slotIndex] ?? string.Empty;
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
            MobsSynchronization mobs = new MobsSynchronization(this);
            Minimapreveal minimapreveal = new Minimapreveal();
            ConnectionUI.Initialize(this);
            GameMenu.Initialize(Logger);
            EventSystem.BroadcastEvent<IOnAdvancedModuleInitializing, ModEntry>(this);
        }

        void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
        {
            entry.Logger.Information("\x1b[32m[[ModEntry] Mod Initializing Hooks...]\x1b[0m ");
            Hook_Game.init += Hook_gameinit;
            Hook_Hero.wakeup += hook_hero_wakeup;
            Hook_Hero.onLevelChanged += hook_level_changed;
            Hook_User.newGame += GameDataSync.user_hook_new_game;
            Hook_LevelGen.generate += GameDataSync.hook_generate;
            Hook_LevelGen.generateGraph += GameDataSync.hook_generateGraph;
            Hook_StoryManager.levelRequiresLoreRoom += GameDataSync.hook_levelRequiresLoreRoom;
            Hook_AnimManager.play += Hook_AnimManager_play;
            Hook_MiniMap.track += Hook_MiniMap_track;
            Hook__LevelStruct.get += Hook__LevelStruct_get;
            Hook_Boot.update += hook_boot_update;
            Hook_Game.pause += Hook_Game_pause;
            Hook_Hero.onHeroDie += Hook_Hero_onHeroDie;
            Hook__TitleScreen.__constructor__ += Hook_TitleScreen__constructor__;
            // Hook_Hero.onEnterRoom += 
            Hook_Inventory.add += Hook_Inventory_add;
        }

        // bool added=false;
        private InventItem Hook_Inventory_add(Hook_Inventory.orig_add orig, Inventory self, InventItem i)
        {
            inventItem = i;
            if(_inventoryAddGuard)
                return orig(self, i);

            _inventoryAddGuard = true;
            try
            {
                var king = GetPrimaryClient();
                if(king != null && king.inventory != null && i != null && !ReferenceEquals(self, king.inventory))
                {
                    var existing = king.inventory.getByPermanentId(i.permanentId);
                    if(existing == null)
                    {
                        king.inventory.add(i);
                        king.inventory.equip(i);
                    }
                }
                return orig(self, i);
            }
            finally
            {
                _inventoryAddGuard = false;
            }
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

        private void Hook_Hero_onHeroDie(Hook_Hero.orig_onHeroDie orig, Hero self)
        {
            var net = _net;
            if (_netRole == NetRole.Client)
                net?.SendHeroDeath();
            else if (_netRole == NetRole.Host)
                GameMenu.QueueHostRestartFromDeath("host died");
            orig(self);
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
                // Logger.Information($"[DEBUG|MOB] mobs at index {i}: {mobs}");
            }
        }

        private void hook_boot_update(Hook_Boot.orig_update orig, Boot self, double dt)
        {

            orig(self, dt);
            GameMenu.ProcessMainThreadQueue();
            GameMenu.HandleTextInputClipboardShortcuts();
        }



        private LevelStruct Hook__LevelStruct_get(Hook__LevelStruct.orig_get orig,
        User user,
        virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_ l,
        dc.libs.Rand rng)
        {
            levelId = l.id.ToString();
            SendLevel(levelId);
            return orig(user, l, rng);
        }


        private void Hook_MiniMap_track(Hook_MiniMap.orig_track orig, MiniMap self, Entity col, int? iconId, dc.String forcedIconColor, int? blink, bool? customTile, Tile text, dc.String itemKind, dc.String isInfectedFood)
        {

            miniMap = self;
            orig(self, col, iconId, forcedIconColor, blink, customTile, text, itemKind, isInfectedFood);
        }

        private AnimManager Hook_AnimManager_play(Hook_AnimManager.orig_play orig, AnimManager self, dc.String plays, int? queueAnim, bool? g)
        {
            var play = plays.ToString();
            if (me != null && me?.spr?._animManager != null && ReferenceEquals(self, me.spr._animManager))
            {
                SendHeroAnim(play, queueAnim, g, force: true);
            }
            if(me != null && me.heroHead.customHeadSpr != null && ReferenceEquals(self, me.heroHead.customHeadSpr._animManager))
            {
                SendHeadAnim(play);
            }

            return orig(self, plays, queueAnim, g);
        }

        public void hook_level_changed(Hook_Hero.orig_onLevelChanged orig, Hero self, Level oldLevel)
        {
            kingInitialized = false;
            me = self;
            SendLevel(levelId);
            orig(self, oldLevel);
            if (_netRole == NetRole.None) return;
            var net = _net;
            var localId = net?.id ?? 0;
            if (_ghost == null)
                _ghost = new GhostHero(localId, game!, me, Logger, this);
            _ghost.SetLabel(me, GameMenu.Username);

            for (int i = 0; i < clients.Length; i++)
            {
                var client = clients[i];
                if (client != null)
                {
                    client.destroy();
                    client.dispose();
                    client.disposeGfx();
                }
                var head = clientHeads[i];
                if (head != null)
                {
                    head.dispose();
                    clientHeads[i] = null;
                }
                clients[i] = _ghost.CreateGhostKing(me._level);

                bool fromUI = false;
                var newHead = new Kinghead(me, clients[i], me._level, Logger);
                newHead.init(me._level, null, Ref<bool>.From(ref fromUI));

                clientHeads[i] = newHead;
                clients[i].head = newHead;

                var knownSkin = clientSkins[i];
                if (!string.IsNullOrWhiteSpace(knownSkin))
                    clients[i].ApplyRemoteSkin(knownSkin);
                var knownHead = clientHeadSkins[i];
                if (!string.IsNullOrWhiteSpace(knownHead))
                {
                    clients[i].RemoteHeadSkinId = knownHead;
                    clientHeads[i]?.ApplyRemoteHeadSkin(knownHead);
                }

                rLastX[i] = 0;
                rLastY[i] = 0;
                clientLabels[i] = null;
                clientIds[i] = 0;
            }
        }


        public void hook_hero_wakeup(Hook_Hero.orig_wakeup orig, Hero self, Level lvl, int cx, int cy)
        {
            me = self;
            orig(self, lvl, cx, cy);
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
            SendHeroCoords();
            ReceiveGhostCoords();
            UpdateGhostHeads();
            Attacking();

        }

        private void Attacking()
        {
            var king = GetPrimaryClient();
            if(king == null || king.kingWeaponsManager == null) return;
            king.kingWeaponsManager.update();
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

            var cleaned = string.IsNullOrWhiteSpace(skin)
                ? "PrisonerDefault"
                : skin.Replace("|", "/").Trim();
            clientSkins[index] = cleaned;

            var client = clients[index];
            if (client != null)
                client.ApplyRemoteSkin(cleaned);
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

            var cleaned = string.IsNullOrWhiteSpace(skin)
                ? "BaseFlame"
                : skin.Replace("|", "/").Trim();
            clientHeadSkins[index] = cleaned;

            var client = clients[index];
            if (client != null)
                client.RemoteHeadSkinId = cleaned;
            clientHeads[index]?.ApplyRemoteHeadSkin(cleaned);
        }

        private void ReceiveGhostCoords()
        {
            var net = _net;
            var ghost = _ghost;
            if (net == null || me == null || ghost == null) return;

            if (!net.TryConsumeRemoteSnapshot(out var remotes))
                return;

            var localId = net.id;
            foreach (var remote in remotes)
            {
                if (!TryGetClientIndex(localId, remote.Id, out var index))
                    continue;

                var client = clients[index];

                remotePlayerId = remote.Id;
                clientIds[index] = remote.Id;
                client.setPosPixel(remote.X, remote.Y - 0.2d);
                client.dir = remote.Dir;
                rLastX[index] = remote.X;
                rLastY[index] = remote.Y;

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

        private void PlayGhostAnim(GhostKing client, string anim, int? queueAnim, bool? g)
        {
            if (client?.spr?._animManager == null) return;
            if (string.IsNullOrWhiteSpace(anim)) return;
            var animManager = client.spr._animManager;
            animManager.play(anim.AsHaxeString(), queueAnim, g).loop(null);
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
            try
            {
                _net?.Dispose();
            }
            catch { }
            _net = null;
            _netRole = NetRole.None;
            GameMenu.NetRef = null;
            GameMenu.SetRole(_netRole);
        }


    }
}
