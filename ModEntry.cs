
using ModCore.Events.Interfaces.Game;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Mods;
using System.Diagnostics;
using System.Net;
using dc.en;
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
        private long _suppressHeroAnimUntilTicks;

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
            Hook_User.prepareSave += Hook_User_prepareSave;
            Hook_User.serialize += Hook_User_serialize;
            Hook_User.unserialize += Hook_User_unserialize;
            Hook_AnimManager.play += Hook_AnimManager_play;
            Hook_AnimManager.stopWithStateAnims += Hook_AnimManager_stopWithStateAnims;
            Hook_MiniMap.track += Hook_MiniMap_track;
            Hook__LevelStruct.get += Hook__LevelStruct_get;
            Hook_Boot.update += hook_boot_update;
            Hook_Game.pause += Hook_Game_pause;
            Hook_Hero.onHeroDie += Hook_Hero_onHeroDie;
            Hook__TitleScreen.__constructor__ += Hook_TitleScreen__constructor__;
            // Hook_Hero.onEnterRoom += 
            Ghost.KingWeaponHooks.Install();
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

        // bool added=false;
        private InventItem Hook_Inventory_add(Hook_Inventory.orig_add orig, Inventory self, InventItem i)
        {
            if(_inventorySyncGuard)
                return orig(self, i);

            if(me != null && ReferenceEquals(self, me.inventory))
                inventItem = i;

            var result = orig(self, i);

            if(_netRole != NetRole.None && IsLocalInventory(self))
                SendEquippedWeapons(self);

            return result;
        }

        private bool Hook_Inventory_equip(Hook_Inventory.orig_equip orig, Inventory self, InventItem i)
        {
            var result = orig(self, i);
            if(_inventorySyncGuard)
                return result;
            if(!IsLocalInventory(self))
                return result;
            SendEquippedWeapons(self);
            return result;
        }

        private void Hook_Inventory_swapWeapons(Hook_Inventory.orig_swapWeapons orig, Inventory self)
        {
            orig(self);
            if(_inventorySyncGuard)
                return;
            if(!IsLocalInventory(self))
                return;
            SendEquippedWeapons(self);
        }

        private void Hook_Inventory_replace(Hook_Inventory.orig_replace orig, Inventory self, InventItem by, InventItem oldPos)
        {
            orig(self, by, oldPos);
            if(_inventorySyncGuard)
                return;
            if(!IsLocalInventory(self))
                return;
            SendEquippedWeapons(self);
        }

        private void Hook_Weapon_prepare(Hook_Weapon.orig_prepare orig, Weapon self, double attackSpeed)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.PatchCurrentSkill(self);
                Ghost.KingWeaponSupport.WithKingContext(self, () => orig(self, attackSpeed));
                Ghost.KingWeaponSupport.PatchCurrentSkill(self);
                Ghost.KingWeaponSupport.SyncSource(self);
                return;
            }

            if(_netRole != NetRole.None && self != null && me != null)
            {
                if(ReferenceEquals(self.owner, me))
                {
                    var item = self.item;
                    if(item != null && TryGetWeaponKindId(item, out var kindId))
                    {
                        var slot = GetWeaponSlot(me.inventory, item);
                        _net?.SendAttack(kindId!, slot, item.permanentId);
                        _suppressHeroAnimUntilTicks = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 0.25);
                    }
                }
            }

            orig(self, attackSpeed);
        }

        private double Hook_Weapon_get_shootX(Hook_Weapon.orig_get_shootX orig, Weapon self)
        {
            if(Ghost.KingWeaponSupport.TryGetSource(self, out var source) && source != null)
                return source.get_shootX();
            return orig(self);
        }

        private double Hook_Weapon_get_shootY(Hook_Weapon.orig_get_shootY orig, Weapon self)
        {
            if(Ghost.KingWeaponSupport.TryGetSource(self, out var source) && source != null)
                return source.get_shootY();
            return orig(self);
        }

        private void Hook_Weapon_fixedUpdate(Hook_Weapon.orig_fixedUpdate orig, Weapon self)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.PatchCurrentSkill(self);
                Ghost.KingWeaponSupport.WithKingContext(self, () => orig(self));
                Ghost.KingWeaponSupport.SyncSource(self);
                return;
            }

            orig(self);
        }

        private void Hook_Weapon_postUpdate(Hook_Weapon.orig_postUpdate orig, Weapon self)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.PatchCurrentSkill(self);
                Ghost.KingWeaponSupport.WithKingContext(self, () => orig(self));
                Ghost.KingWeaponSupport.SyncSource(self);
                return;
            }

            orig(self);
        }

        private void Hook_Weapon_dynOnAttackAnim(Hook_Weapon.orig_dynOnAttackAnim orig, Weapon self, WeaponSkill s,
            virtual_animId_animSpd_area_breachBonus_canCrit_charge_coolDown_critMul_dynamicCharge_earlyCombo_fxId_fxProps_glowColor_hitFrame_lockCtrlAfter_onionSkinFrame_onionSkinOffX_power_props_sfxCharge_sfxHit_sfxProps_sfxRelease_ a)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { orig(self, s, a); } catch { }
                });
                return;
            }

            orig(self, s, a);
        }

        private void Hook_Weapon_dynOnFxFrame(Hook_Weapon.orig_dynOnFxFrame orig, Weapon self, WeaponSkill s,
            virtual_animId_animSpd_area_breachBonus_canCrit_charge_coolDown_critMul_dynamicCharge_earlyCombo_fxId_fxProps_glowColor_hitFrame_lockCtrlAfter_onionSkinFrame_onionSkinOffX_power_props_sfxCharge_sfxHit_sfxProps_sfxRelease_ cinf)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { orig(self, s, cinf); } catch { }
                });
                return;
            }

            orig(self, s, cinf);
        }

        private void Hook_Weapon_updateAmmoHud(Hook_Weapon.orig_updateAmmoHud orig, Weapon self)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
                return;
            orig(self);
        }

        private void Hook_BaseBow_dynOnAttackAnim(Hook_BaseBow.orig_dynOnAttackAnim orig, BaseBow self, WeaponSkill s,
            virtual_animId_animSpd_area_breachBonus_canCrit_charge_coolDown_critMul_dynamicCharge_earlyCombo_fxId_fxProps_glowColor_hitFrame_lockCtrlAfter_onionSkinFrame_onionSkinOffX_power_props_sfxCharge_sfxHit_sfxProps_sfxRelease_ cinf)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { orig(self, s, cinf); } catch { }
                });
                return;
            }

            orig(self, s, cinf);
        }

        private void Hook_BaseBow_fixedUpdate(Hook_BaseBow.orig_fixedUpdate orig, BaseBow self)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.PatchCurrentSkill(self);
                Ghost.KingWeaponSupport.WithKingContext(self, () => orig(self));
                Ghost.KingWeaponSupport.SyncSource(self);
                return;
            }
            orig(self);
        }

        private double Hook_BaseBow_get_shootY(Hook_BaseBow.orig_get_shootY orig, BaseBow self)
        {
            if(Ghost.KingWeaponSupport.TryGetSource(self, out var source) && source != null)
                return source.get_shootY();
            return orig(self);
        }

        private void Hook_BaseBow_playShootAnim(Hook_BaseBow.orig_playShootAnim orig, BaseBow self)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { orig(self); } catch { }
                });
                return;
            }
            orig(self);
        }

        private void Hook_BaseBow_shoot(Hook_BaseBow.orig_shoot orig, BaseBow self, ArrayObj entity)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { orig(self, entity); } catch { }
                });
                return;
            }
            orig(self, entity);
        }

        private void Hook_BaseBow_dynamicChargeExecute(Hook_BaseBow.orig_dynamicChargeExecute orig, BaseBow self)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { orig(self); } catch { }
                });
                return;
            }
            orig(self);
        }

        private void Hook_BaseBow_onBowChargeStart(Hook_BaseBow.orig_onBowChargeStart orig, BaseBow self)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { orig(self); } catch { }
                });
                return;
            }
            orig(self);
        }

        private void Hook_BaseBow_onBowCharging(Hook_BaseBow.orig_onBowCharging orig, BaseBow self, double r)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { orig(self, r); } catch { }
                });
                return;
            }
            orig(self, r);
        }

        private bool Hook_BaseBow_onExecute(Hook_BaseBow.orig_onExecute orig, BaseBow self)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
                return Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { return orig(self); } catch { return false; }
                });
            return orig(self);
        }

        private void Hook_BaseBow_interrupt(Hook_BaseBow.orig_interrupt orig, BaseBow self)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { orig(self); } catch { }
                });
                return;
            }
            orig(self);
        }

        private bool Hook_BaseShield_tryToCancel(Hook_BaseShield.orig_tryToCancel orig, BaseShield self, bool byWeapon)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
                return Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { return orig(self, byWeapon); } catch { return false; }
                });
            return orig(self, byWeapon);
        }

        private void Hook_BaseShield_onShieldChargeStart(Hook_BaseShield.orig_onShieldChargeStart orig, BaseShield self)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { orig(self); } catch { }
                });
                return;
            }
            orig(self);
        }

        private void Hook_BaseShield_onShieldReleased(Hook_BaseShield.orig_onShieldReleased orig, BaseShield self)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { orig(self); } catch { }
                });
                return;
            }
            orig(self);
        }

        private void Hook_BaseShield_startParry(Hook_BaseShield.orig_startParry orig, BaseShield self)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { orig(self); } catch { }
                });
                return;
            }
            orig(self);
        }

        private void Hook_BaseShield_onShieldStartParry(Hook_BaseShield.orig_onShieldStartParry orig, BaseShield self)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { orig(self); } catch { }
                });
                return;
            }
            orig(self);
        }

        private void Hook_BaseShield_onShieldEndParry(Hook_BaseShield.orig_onShieldEndParry orig, BaseShield self)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { orig(self); } catch { }
                });
                return;
            }
            orig(self);
        }

        private void Hook_BaseShield_onShieldHolding(Hook_BaseShield.orig_onShieldHolding orig, BaseShield self, double ratio)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { orig(self, ratio); } catch { }
                });
                return;
            }
            orig(self, ratio);
        }

        private void Hook_BaseShield_onShieldBlock(Hook_BaseShield.orig_onShieldBlock orig, BaseShield self, AttackData sourceAtk, bool fullParry)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { orig(self, sourceAtk, fullParry); } catch { }
                });
                return;
            }
            orig(self, sourceAtk, fullParry);
        }

        private void Hook_BaseShield_onShieldCounterSuccessful(Hook_BaseShield.orig_onShieldCounterSuccessful orig, BaseShield self, AttackData sourceAtk, bool fullParry)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { orig(self, sourceAtk, fullParry); } catch { }
                });
                return;
            }
            orig(self, sourceAtk, fullParry);
        }

        private void Hook_BaseShield_counterGrenade(Hook_BaseShield.orig_counterGrenade orig, BaseShield self, Grenade repelled)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
            {
                Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { orig(self, repelled); } catch { }
                });
                return;
            }
            orig(self, repelled);
        }

        private Bullet Hook_BaseShield_counterBullet(Hook_BaseShield.orig_counterBullet orig, BaseShield self, AttackData sourceAtk, Bullet cBullet, bool fullParry)
        {
            if(Ghost.KingWeaponSupport.IsKingWeapon(self))
                return Ghost.KingWeaponSupport.WithKingContext(self, () =>
                {
                    try { return orig(self, sourceAtk, cBullet, fullParry); } catch { return cBullet; }
                });
            return orig(self, sourceAtk, cBullet, fullParry);
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
            if (!GameDataSync.ConsumeSuppressDeathBroadcast() && _netRole != NetRole.None)
                net?.SendHeroDeath();
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
                GameDataSync.TryApplyRemoteLevelSeed(levelId, rng);
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

        private void Hook_AnimManager_stopWithStateAnims(Hook_AnimManager.orig_stopWithStateAnims orig, AnimManager self)
        {
            orig(self);

            if(_netRole == NetRole.None)
                return;
            if(DeadCellsMultiplayerMod.Ghost.KingWeaponSupport.IsInKingContext)
                return;

            if(me != null && me?.spr?._animManager != null && ReferenceEquals(self, me.spr._animManager))
            {
                if(Stopwatch.GetTimestamp() < _suppressHeroAnimUntilTicks)
                    return;

                string anim = "idle";
                try
                {
                    var group = me.spr.groupName;
                    if(group != null)
                        anim = group.ToString();
                }
                catch
                {
                    anim = "idle";
                }

                anim = SanitizeRemoteBaseAnim(anim);
                if(!string.IsNullOrWhiteSpace(anim))
                    SendHeroAnim(anim, null, null, force: false);
            }
        }

        private static string SanitizeRemoteBaseAnim(string anim)
        {
            if(string.IsNullOrWhiteSpace(anim))
                return "idle";

            var a = anim.Trim();
            if(string.IsNullOrWhiteSpace(a))
                return "idle";

            if(IsAttackAnim(a))
                return "idle";
            if(a.IndexOf("guard", StringComparison.OrdinalIgnoreCase) >= 0)
                return "idle";

            return a;
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

                var knownSkin = clientSkins[i];
                if (!string.IsNullOrWhiteSpace(knownSkin))
                    clients[i].ApplyRemoteSkin(knownSkin);
                var knownHead = clientHeadSkins[i];
                clients[i].RemoteHeadSkinId = NormalizeHeadSkin(
                    !string.IsNullOrWhiteSpace(knownHead) ? knownHead : remoteHeadSkin
                );

                RecreateClientHead(i);

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
            SendHeroCoords();
            ReceiveGhostCoords();
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

        private void ReceiveGhostWeapons()
        {
            var net = _net;
            if (net == null || me == null) return;

            if (!net.TryConsumeRemoteWeaponSnapshots(out var updates))
                return;

            foreach (var update in updates)
            {
                ApplyRemoteWeaponUpdate(update.Id, update.Kind, update.Slot, update.PermanentId);
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
                ApplyRemoteWeaponUpdate(attack.Id, attack.Kind, attack.Slot, attack.PermanentId);
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

                try { animManager.stopWithStateAnims(); } catch { }
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
            net.SendInventoryWeapon(kindId!, slot, item.permanentId);
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

        private void ApplyRemoteWeaponUpdate(int remoteId, string? kindId, int slot, int permanentId)
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
            else if(currentSlotItem != null && currentSlotItem.permanentId != existing.permanentId)
            {
                currentSlotItem.posID = -1;
            }

            if (slot >= 0)
                existing.posID = slot;

            _inventorySyncGuard = true;
            try
            {
                inv.equip(existing);
            }
            finally
            {
                _inventorySyncGuard = false;
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
            _net = null;
            _netRole = NetRole.None;
            GameMenu.NetRef = null;
            GameMenu.SetRole(_netRole);
            ConnectionUI.NotifyConnectionsChanged();
        }


    }
}
