
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


namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry(ModInfo info) : ModBase(info),
        IOnGameEndInit,
        IOnHeroInit,
        IOnHeroUpdate,
        IOnFrameUpdate
    {
        public static ModEntry? Instance { get; private set; }
        private bool _ready;

        private NetRole _netRole = NetRole.None;
        public static NetNode? _net;

        public dc.pr.Game? game;



        public static KingSkin[] clients = new KingSkin[NetNode.MaxClientSlots];
        public static string?[] clientLabels = new string?[NetNode.MaxClientSlots];
        public static int[] clientIds = new int[NetNode.MaxClientSlots];
        public static Hero me = null;
        public static GhostHero _ghost = null;

        private GameDataSync gds;
        private MultiplayerUI UI { get; set; } = null!;

        private string? _lastAnimSent;
        private int? _lastAnimQueueSent;
        private bool? _lastAnimGSent;
        private double _animResendElapsed;
        private double? _lastAnimPlayRatio;
        private const double AnimLoopThreshold = 0.995;
        private const double RatioDropThreshold = 0.5;
        private const double LoopDetectionCooldown = 0.08;

        public static MiniMap miniMap;

        public static bool kingInitialized = false;

        public string levelId;

        public static string remoteLevelId;
        public static int remotePlayerId = -1;

        private string remoteSkin;

        int _layer;

        HeroHead head;

        internal static void SetRemoteSkin(string? skin)
        {
            var instance = Instance;
            if (instance == null)
                return;

            instance.remoteSkin = string.IsNullOrWhiteSpace(skin)
                ? "PrisonerDefault"
                : skin.Replace("|", "/").Trim();
        }

        public static string GetClientLabel(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= clientLabels.Length)
                return GameMenu.RemoteUsername;

            return clientLabels[slotIndex] ?? GameMenu.RemoteUsername;
        }

        internal static KingSkin? GetPrimaryClient()
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
                clients[i] = null!;
                clientLabels[i] = null;
                clientIds[i] = 0;
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
            this.UI = new MultiplayerUI(this, 0);
            
            this.UI.init();
            CineHooks cine = new CineHooks();
            MobsSynchronization.MobsSynchronization mobs = new MobsSynchronization.MobsSynchronization(this);
            GameMenu.Initialize(Logger);
            Hook_Game.init += Hook_gameinit;
            Hook_Hero.wakeup += hook_hero_wakeup;
            Hook_Hero.onLevelChanged += hook_level_changed;
            Hook_User.newGame += GameDataSync.user_hook_new_game;
            Hook_LevelGen.generate += GameDataSync.hook_generate;
            Hook_AnimManager.play += Hook_AnimManager_play;
            Hook_MiniMap.track += Hook_MiniMap_track;
            Hook_KingSkin.initGfx += Hook_KingSkin_initgfx;
            Hook__LevelStruct.get += Hook__LevelStruct_get;
            Hook_Boot.update += hook_boot_update;
            Hook_Game.pause += Hook_Game_pause;


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



        private void Hook_KingSkin_initgfx(Hook_KingSkin.orig_initGfx orig, KingSkin self)
        {
            if (remoteSkin == null) remoteSkin = "PrisonerDefault";
            orig(self);
            dc.String group = "idle".AsHaxeString();
            SpriteLib heroLib = Assets.Class.getHeroLib(Cdb.Class.getSkinInfo(remoteSkin.AsHaxeString()));
            Texture normalMapFromGroup = heroLib.getNormalMapFromGroup(group);
            int? dp_ROOM_MAIN_HERO = Const.Class.DP_ROOM_MAIN_HERO;
            self.initSprite(heroLib, group, 0.5, 0.5, dp_ROOM_MAIN_HERO, true, null, normalMapFromGroup);
            self.initColorMap(Cdb.Class.getSkinInfo(remoteSkin.AsHaxeString()));

            // glow
            ArrayObj glowData = CdbTypeConverter.Class.getGlowData(Cdb.Class.getSkinInfo(remoteSkin.AsHaxeString()));
            if (glowData != null)
            {
                GlowKey s2 = new GlowKey(glowData);
                if (s2 != null)
                {
                    self.spr.addShader(s2);
                }
            }


            // Ambient light
            var General = 1.0;
            var radiusCase = 1.2 * General;
            var Math = dc.Math.Class.random() * 0.20000000000000007;
            General = 0.9 + Math;
            var decayStart = 5.0 * General;
            self.createLight(1161471, radiusCase, decayStart, 0.35);
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


            return orig(self, plays, queueAnim, g);
        }

        public void hook_level_changed(Hook_Hero.orig_onLevelChanged orig, Hero self, Level oldLevel)
        {
            kingInitialized = false;
            me = self;
            SendLevel(levelId);
            orig(self, oldLevel);
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
                clients[i] = _ghost.CreateGhostKing(me._level);
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


        private HashSet<string> _loopAnimations = new HashSet<string>
        {
            "idle", "run", "jumpUp", "jumpDown", "crouch", "land",
            "rollStart", "rolling", "rollEnd"
        };
        void IOnHeroUpdate.OnHeroUpdate(double dt)
        {
            if (me == null) return;
            SendHeroCoords();
            ReceiveGhostCoords();
            if (_lastAnimSent != null && _loopAnimations.Contains(_lastAnimSent))
            {
                ResendCurrentAnim(dt);
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

        private void SendHeroCoords()
        {
            if (_netRole == NetRole.None) return;
            float dx = (float)(me.spr.x - last_x);
            float dy = (float)(me.spr.y - last_y);
            float distSq = dx * dx + dy * dy;

            if (distSq < 4.0f) return;
            if (_net == null || me == null) return;
            if (me.spr.x == last_x && me.spr.y == last_y) return;

            _net.TickSend(me.spr.x, me.spr.y);
            last_x = me.spr.x;
            last_y = me.spr.y;
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
                if (client == null)
                {
                    var label = BuildRemoteLabel(remote.Id, remote.Username);
                    clients[index] = ghost.CreateGhostKing(me._level, label);
                    client = clients[index];
                    clientLabels[index] = label;
                    clientIds[index] = remote.Id;
                }
                if (client == null)
                    continue;

                remotePlayerId = remote.Id;
                clientIds[index] = remote.Id;
                client.setPosPixel(remote.X, remote.Y - 0.2d);
                if (remote.X < rLastX[index])
                    client.dir = -1;
                if (remote.X > rLastX[index])
                    client.dir = 1;
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
            }
        }

        private void PlayGhostAnim(KingSkin client, string anim, int? queueAnim, bool? g)
        {
            if (client?.spr?._animManager == null) return;
            if (string.IsNullOrWhiteSpace(anim)) return;
            var animManager = client.spr._animManager;
            try
            {
                animManager.stopWithoutStateAnims(anim.AsHaxeString(), queueAnim);
                animManager.setFrame(0);
            }
            catch { }
            animManager.play(anim.AsHaxeString(), queueAnim, g);
        }

        private void SendHeroAnim(string anim, int? queueAnim, bool? g, bool force = false)
        {
            if (_netRole == NetRole.None) return;
            var net = _net;
            var animManager = me?.spr?._animManager;
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

        private void ResendCurrentAnim(double dt)
        {
            if (_netRole == NetRole.None) return;
            var net = _net;
            var animManager = me?.spr?._animManager;
            if (net == null || animManager == null) return;
            if (string.IsNullOrWhiteSpace(_lastAnimSent)) return;
            _animResendElapsed += dt;
            if (_animResendElapsed < 0.033f) return;

            bool looped = DidLoop(animManager);

            if (!looped) return;
            if (_animResendElapsed >= 0.2)
            {
                net.SendAnim(_lastAnimSent, _lastAnimQueueSent, _lastAnimGSent);
                _animResendElapsed = 0;
            }
        }

        private bool DidLoop(AnimManager animManager)
        {
            double currentRatio = 0;
            if (!TryGetPlayRatio(animManager, out currentRatio))
            {
                _lastAnimPlayRatio = null;
                return false;
            }

            bool looped = false;
            if (_lastAnimPlayRatio.HasValue)
            {
                var prev = _lastAnimPlayRatio.Value;
                var enoughTime = _animResendElapsed >= LoopDetectionCooldown;

                if (enoughTime && prev >= RatioDropThreshold && currentRatio < prev)
                {
                    looped = true;
                }
                else if (enoughTime && currentRatio >= AnimLoopThreshold && prev < AnimLoopThreshold)
                {
                    looped = true;
                }
            }

            _lastAnimPlayRatio = currentRatio;
            return looped;
        }


        private bool TryGetPlayRatio(AnimManager animManager, out double ratio)
        {
            try
            {
                ratio = animManager.getPlayRatio();
                return true;
            }
            catch
            {
                ratio = 0;
                return false;
            }
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
            // try
            // {
                _net?.Dispose();

                _net = NetNode.CreateHost(Logger, ep);
                _netRole = NetRole.Host;
                GameMenu.SetRole(_netRole);
                GameMenu.NetRef = _net;

                var lep = _net.ListenerEndpoint;
                if (lep != null)
                    Logger.Information($"[NetMod] Host listening at {lep.Address}:{lep.Port}");
            // }
            // catch (Exception ex)
            // {
            //     Logger.Error($"[NetMod] Host start failed: {ex.Message}");
            //     _netRole = NetRole.None;
            //     _net = null;
            //     GameMenu.SetRole(_netRole);
            // }
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
