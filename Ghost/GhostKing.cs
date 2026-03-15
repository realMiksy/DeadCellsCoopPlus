using System;
using System.Diagnostics;
using System.Reflection;
using dc;
using dc.en;
using dc.haxe.ds;
using dc.h3d.mat;
using dc.hl.types;
using dc.hxd;
using dc.libs.heaps.slib;
using dc.pow;
using dc.pr;
using dc.shader;
using dc.tool;
using dc.tool._AnimationTrack;
using dc.tool._Cooldown;
using dc.tool.mainSkills;
using Hashlink.Virtuals;
using ModCore.Storage;
using ModCore.Utilities;
using dc.spine.support.utils;
using DeadCellsMultiplayerMod.Ghost;
using HaxeProxy.Runtime;

namespace DeadCellsMultiplayerMod.Ghost.GhostBase
{
    public class GhostKing : KingSkin, IHxbitSerializable<object>
    {
        private const string DefaultBodySkinId = "PrisonerDefault";
        public StringMap? animationTracks;

        public Inventory? inventory;
        public HeroHead? head;
        public string? RemoteSkinId;
        public string? RemoteHeadSkinId;
        public KingWeaponsManager? kingWeaponsManager;
        private DiveAttack? remoteDiveAttack;
        private virtual_cooldown_duration_flags_forbiddenItem_props_requiredItem_skill_? remoteDiveSkillInfos;
        private long _lastRemoteDiveStartTicks;
        private long _lastRemoteDiveLandTicks;

        private const double RemoteDiveReplayMinSeconds = 0.03;
        private const int DiveAttackCooldownKey = 729808896;

        ScarfManager? scarf;

        public GhostKing() : base(null, 0, 0)
        {
        }

        public GhostKing(Level lvl, int x, int y) : base(lvl, x, y)
        {
        }
        object IHxbitSerializable<object>.GetData()
        {
            return new();
        }

        void IHxbitSerializable<object>.SetData(object data)
        {
        }


        public override void init()
        {
            EnsureRuntimeDependencies();
            base.init();
            base.initSpeechDeck();
        }

        private static Hero? ResolveLocalHero()
        {
            var hero = ModEntry.me;
            if (hero != null)
                return hero;

            try
            {
                return ModCore.Modules.Game.Instance?.HeroInstance;
            }
            catch
            {
                return null;
            }
        }

        private void EnsureRuntimeDependencies()
        {
            var localHero = ResolveLocalHero();
            if (localHero?.inventory != null && inventory == null)
            {
                try
                {
                    inventory = localHero.inventory.clone();
                }
                catch
                {
                    inventory = localHero.inventory;
                }
            }

            if (kingWeaponsManager == null && localHero != null)
            {
                try
                {
                    kingWeaponsManager = new KingWeaponsManager(localHero, this);
                    kingWeaponsManager.init();
                }
                catch
                {
                    kingWeaponsManager = null;
                }
            }
        }

        public void TriggerRemoteDiveAttackStart()
        {
            if (destroyed || _level == null)
                return;

            var localHero = ResolveLocalHero();
            if (localHero == null)
                return;

            var dive = EnsureRemoteDiveAttack(localHero, forceRecreate: true);
            if (dive == null)
                return;

            if (IsReplayTooSoon(ref _lastRemoteDiveStartTicks, RemoteDiveReplayMinSeconds))
                return;

            ExecuteRemoteDive(localHero, dive, high: 1.0, startOnly: true);
        }

        public void TriggerRemoteDiveAttackLand(double high)
        {
            if (destroyed || _level == null)
                return;

            var localHero = ResolveLocalHero();
            if (localHero == null)
                return;

            var dive = EnsureRemoteDiveAttack(localHero, forceRecreate: false);
            if (dive == null)
                return;

            if (IsReplayTooSoon(ref _lastRemoteDiveLandTicks, RemoteDiveReplayMinSeconds))
                return;

            ExecuteRemoteDive(localHero, dive, high, startOnly: false);
            DisposeRemoteDiveAttack();
        }

        public void SetRemoteDiveSkillInfos(virtual_cooldown_duration_flags_forbiddenItem_props_requiredItem_skill_? skillInfos)
        {
            remoteDiveSkillInfos = CloneDiveSkillInfos(skillInfos);
            DisposeRemoteDiveAttack();
        }

        private static bool IsReplayTooSoon(ref long lastTicks, double minSeconds)
        {
            var now = Stopwatch.GetTimestamp();
            var minTicks = (long)(Stopwatch.Frequency * minSeconds);
            if (lastTicks != 0 && now - lastTicks < minTicks)
                return true;
            lastTicks = now;
            return false;
        }

        private DiveAttack? EnsureRemoteDiveAttack(Hero localHero, bool forceRecreate)
        {
            if (forceRecreate)
                DisposeRemoteDiveAttack();

            if (remoteDiveAttack != null)
                return remoteDiveAttack;

            try
            {
                var manager = localHero.mainSkillsManager;
                var localDive = manager?.getMainSkill(DiveAttack.Class) as DiveAttack;
                if (localDive?.skillInfos == null)
                    return null;

                var currentGame = localHero._level?.game ?? dc.pr.Game.Class.ME;
                if (currentGame == null)
                    return null;

                var sourceSkillInfos = remoteDiveSkillInfos ?? localDive.skillInfos;
                var copiedSkillInfos = CloneDiveSkillInfos(sourceSkillInfos) ?? localDive.skillInfos;
                DiveAttack? created = null;
                KingWeaponSupport.WithKingContext(localHero, this, () =>
                {
                    created = new DiveAttack(localHero, currentGame, copiedSkillInfos);
                    created.init();
                });
                remoteDiveAttack = created;
                return created;
            }
            catch
            {
                return null;
            }
        }

        private void ExecuteRemoteDive(Hero localHero, DiveAttack dive, double high, bool startOnly)
        {
            var cooldownSnapshot = CaptureCooldown(localHero.cd, DiveAttackCooldownKey);
            try
            {
                KingWeaponSupport.WithLocalHeroDamageAllowed(() =>
                {
                    KingWeaponSupport.WithKingContext(localHero, this, () =>
                    {
                        if (!EnsureDiveActive(dive))
                            return;

                        try { dive.activeFixedUpdate(); } catch { }

                        if (startOnly)
                            return;

                        try
                        {
                            dive.onOwnerLand(high);
                        }
                        catch
                        {
                            try { dive.end(); } catch { }
                        }
                    });
                });
            }
            finally
            {
                RestoreCooldown(localHero.cd, DiveAttackCooldownKey, cooldownSnapshot);
            }
        }

        private static bool EnsureDiveActive(DiveAttack dive)
        {
            try
            {
                if (dive.isActive())
                    return true;
            }
            catch
            {
            }

            try
            {
                dive.start();
            }
            catch
            {
                try
                {
                    dive.onStart();
                }
                catch
                {
                    return false;
                }
            }

            try
            {
                return dive.isActive();
            }
            catch
            {
                return true;
            }
        }

        private readonly struct CooldownSnapshot
        {
            public readonly bool HadEntry;
            public readonly CdInst? Entry;
            public readonly double Frames;
            public readonly double Initial;
            public readonly int SubIndexBits;

            public CooldownSnapshot(bool hadEntry, CdInst? entry, double frames, double initial, int subIndexBits)
            {
                HadEntry = hadEntry;
                Entry = entry;
                Frames = frames;
                Initial = initial;
                SubIndexBits = subIndexBits;
            }
        }

        private static CooldownSnapshot CaptureCooldown(Cooldown? cooldown, int key)
        {
            if (cooldown?.fastCheck == null)
                return default;

            try
            {
                var fastCheck = cooldown.fastCheck;
                if (!fastCheck.exists(key))
                    return default;

                var entry = fastCheck.get(key) as CdInst;
                if (entry == null)
                    return default;

                return new CooldownSnapshot(
                    hadEntry: true,
                    entry: entry,
                    frames: entry.frames,
                    initial: entry.initial,
                    subIndexBits: entry.subIndexBits);
            }
            catch
            {
                return default;
            }
        }

        private static void RestoreCooldown(Cooldown? cooldown, int key, CooldownSnapshot snapshot)
        {
            if (cooldown?.fastCheck == null)
                return;

            try
            {
                var fastCheck = cooldown.fastCheck;
                var current = fastCheck.get(key) as CdInst;

                if (!snapshot.HadEntry || snapshot.Entry == null)
                {
                    if (current != null)
                    {
                        try { cooldown.cdList?.remove(current); } catch { }
                        try { fastCheck.remove(key); } catch { }
                    }
                    return;
                }

                if (current != null && !ReferenceEquals(current, snapshot.Entry))
                {
                    try { cooldown.cdList?.remove(current); } catch { }
                }

                snapshot.Entry.frames = snapshot.Frames;
                snapshot.Entry.initial = snapshot.Initial;
                snapshot.Entry.subIndexBits = snapshot.SubIndexBits;
                fastCheck.set(key, snapshot.Entry);
            }
            catch
            {
            }
        }

        private void DisposeRemoteDiveAttack()
        {
            if (remoteDiveAttack == null)
                return;

            try { remoteDiveAttack.cancel(); } catch { }
            try { remoteDiveAttack.destroy(); } catch { }
            remoteDiveAttack = null;
        }

        private static virtual_cooldown_duration_flags_forbiddenItem_props_requiredItem_skill_? CloneDiveSkillInfos(
            virtual_cooldown_duration_flags_forbiddenItem_props_requiredItem_skill_? source)
        {
            if (source == null)
                return null;

            var clone = new virtual_cooldown_duration_flags_forbiddenItem_props_requiredItem_skill_();
            SafeAssign(() => clone.cooldown = SafeRead(() => source.cooldown, 0.0));
            SafeAssign(() => clone.duration = SafeRead(() => source.duration, 0.0));
            SafeAssign(() => clone.flags = SafeRead(() => source.flags, default(int?)));
            SafeAssign(() => clone.forbiddenItem = SafeRead(() => source.forbiddenItem, default(dc.String)));
            SafeAssign(() => clone.requiredItem = SafeRead(() => source.requiredItem, default(dc.String)));
            SafeAssign(() => clone.skill = SafeRead(() => source.skill, default(dc.String)));

            var sourceProps = SafeRead(() => source.props, default(virtual_affect_alpha_buff_buff2_color_color2_color3_count_duration2_duration3_pct_pct2_pct3_power_power2_power3_radius_radius2_speed_threshold_));
            SafeAssign(() => clone.props = CloneDiveProps(sourceProps));
            return clone;
        }

        private static virtual_affect_alpha_buff_buff2_color_color2_color3_count_duration2_duration3_pct_pct2_pct3_power_power2_power3_radius_radius2_speed_threshold_? CloneDiveProps(
            virtual_affect_alpha_buff_buff2_color_color2_color3_count_duration2_duration3_pct_pct2_pct3_power_power2_power3_radius_radius2_speed_threshold_? source)
        {
            if (source == null)
                return null;

            var clone = new virtual_affect_alpha_buff_buff2_color_color2_color3_count_duration2_duration3_pct_pct2_pct3_power_power2_power3_radius_radius2_speed_threshold_();
            SafeAssign(() => clone.affect = SafeRead(() => source.affect, default(double?)));
            SafeAssign(() => clone.alpha = SafeRead(() => source.alpha, default(double?)));
            SafeAssign(() => clone.buff = SafeRead(() => source.buff, default(double?)));
            SafeAssign(() => clone.buff2 = SafeRead(() => source.buff2, default(double?)));
            SafeAssign(() => clone.color = SafeRead(() => source.color, default(int?)));
            SafeAssign(() => clone.color2 = SafeRead(() => source.color2, default(int?)));
            SafeAssign(() => clone.color3 = SafeRead(() => source.color3, default(int?)));
            SafeAssign(() => clone.count = SafeRead(() => source.count, default(int?)));
            SafeAssign(() => clone.duration2 = SafeRead(() => source.duration2, default(double?)));
            SafeAssign(() => clone.duration3 = SafeRead(() => source.duration3, default(double?)));
            SafeAssign(() => clone.pct = SafeRead(() => source.pct, default(double?)));
            SafeAssign(() => clone.pct2 = SafeRead(() => source.pct2, default(double?)));
            SafeAssign(() => clone.pct3 = SafeRead(() => source.pct3, default(double?)));
            SafeAssign(() => clone.power = SafeRead(() => source.power, default(double?)));
            SafeAssign(() => clone.power2 = SafeRead(() => source.power2, default(double?)));
            SafeAssign(() => clone.power3 = SafeRead(() => source.power3, default(double?)));
            SafeAssign(() => clone.radius = SafeRead(() => source.radius, default(double?)));
            SafeAssign(() => clone.radius2 = SafeRead(() => source.radius2, default(double?)));
            SafeAssign(() => clone.speed = SafeRead(() => source.speed, default(double?)));
            SafeAssign(() => clone.threshold = SafeRead(() => source.threshold, default(double?)));
            return clone;
        }

        private static T SafeRead<T>(Func<T> getter, T fallback)
        {
            try
            {
                return getter();
            }
            catch
            {
                return fallback;
            }
        }

        private static void SafeAssign(Action setter)
        {
            try
            {
                setter();
            }
            catch
            {
            }
        }

        private static string NormalizeBodySkinId(string? skin)
        {
            return string.IsNullOrWhiteSpace(skin)
                ? DefaultBodySkinId
                : skin.Replace("|", "/").Trim();
        }

        private static virtual_colorMap_consoleCmdId_glowData_group_head_incompatibleHeads_item_model_onlyDefaultHead_scarfBlendMode_scarfs_? ResolveBodySkinInfo(string? skin, out string resolvedSkinId)
        {
            resolvedSkinId = NormalizeBodySkinId(skin);

            virtual_colorMap_consoleCmdId_glowData_group_head_incompatibleHeads_item_model_onlyDefaultHead_scarfBlendMode_scarfs_? info = null;
            try { info = Cdb.Class.getSkinInfo(resolvedSkinId.AsHaxeString()); } catch { }
            if (info != null)
                return info;

            resolvedSkinId = DefaultBodySkinId;
            try { return Cdb.Class.getSkinInfo(DefaultBodySkinId.AsHaxeString()); } catch { return null; }
        }


        public void initScarf()
        {
            var skinInfo = ResolveBodySkinInfo(RemoteSkinId ?? ModEntry.Instance?.remoteSkin, out var resolvedSkinId);
            if (skinInfo == null)
            {
                DisposeScarf();
                return;
            }
            RemoteSkinId = resolvedSkinId;

            if(scarf != null)
                scarf.dispose();

            var item = skinInfo.item;
            if (item == null)
            {
                DisposeScarf();
                return;
            }
            var newScarf = ScarfManager.Class.create(this, item);
            if (newScarf == null)
            {
                DisposeScarf();
                return;
            }
            newScarf.owner = this;
            scarf = newScarf;
        }

        public void DisposeScarf()
        {
            if (scarf == null)
                return;

            try { scarf.dispose(); } catch { }
            scarf = null;
        }

        public override void disposeGfx()
        {
            DisposeScarf();
            base.disposeGfx();
        }

        public override void dispose()
        {
            DisposeScarf();
            DisposeRemoteDiveAttack();
            base.dispose();
        }


        public override void initGfx()
        {
            base.initGfx();
            var skinInfo = ResolveBodySkinInfo(RemoteSkinId ?? ModEntry.Instance?.remoteSkin, out var resolvedSkinId);
            if (skinInfo == null)
                return;

            RemoteSkinId = resolvedSkinId;
            animationTracks = ResolveAnimationTracks(skinInfo);
            dc.String group = "idle".AsHaxeString();
            SpriteLib heroLib = Assets.Class.getHeroLib(skinInfo);
            if (heroLib == null)
                return;

            Texture normalMapFromGroup = heroLib.getNormalMapFromGroup(group);
            if (!TryRetargetCurrentSprite(heroLib, group, normalMapFromGroup))
            {
                int? dp_ROOM_MAIN_HERO = Const.Class.DP_ROOM_MAIN_HERO;
                this.initSprite(heroLib, group, 0.5, 0.5, dp_ROOM_MAIN_HERO, true, null, normalMapFromGroup);
            }
            this.initColorMap(skinInfo);

            ArrayObj glowData = CdbTypeConverter.Class.getGlowData(skinInfo);
            if (glowData != null && glowData.length > 0)
            {
                GlowKey glowKey = (GlowKey)this.spr.getShader(GlowKey.Class);
                if (glowKey == null)
                {
                    glowKey = new GlowKey(null);
                    this.spr.addShader(glowKey);
                }
                glowKey.setGlowDatas(glowData);
                
            }


            // Ambient light
            var General = 1.0;
            var radiusCase = 1.2 * General;
            var Math = dc.Math.Class.random() * 0.20000000000000007;
            General = 0.9 + Math;
            var decayStart = 5.0 * General;
            this.createLight(1161471, radiusCase, decayStart, 0.35);


            // Scarf
            initScarf();


        }

        private bool TryRetargetCurrentSprite(SpriteLib heroLib, dc.String group, Texture normalMap)
        {
            var currentSprite = spr;
            if (currentSprite == null)
                return false;

            try
            {
                int startFrame = 0;
                bool stopAllAnims = true;
                currentSprite.set(heroLib, group, Ref<int>.From(ref startFrame), Ref<bool>.From(ref stopAllAnims));
                if (normalMap != null)
                    currentSprite.addOrUpdateNormalMapTexture(normalMap);

                return true;
            }
            catch
            {
                return false;
            }
        }

        public void ApplyRemoteSkin(string? skin)
        {
            var cleaned = NormalizeBodySkinId(skin);
            if (string.Equals(RemoteSkinId, cleaned, StringComparison.Ordinal))
                return;

            RemoteSkinId = cleaned;
            if (this.spr != null)
            {
                try
                {
                    this.disposeGfx();
                    this.initGfx();
                }
                catch
                {
                    RemoteSkinId = DefaultBodySkinId;
                    try
                    {
                        this.disposeGfx();
                        this.initGfx();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static StringMap? ResolveAnimationTracks(
            virtual_colorMap_consoleCmdId_glowData_group_head_incompatibleHeads_item_model_onlyDefaultHead_scarfBlendMode_scarfs_ skinInfo)
        {
            if (skinInfo == null)
            {
                return null;
            }

            dc._String _String = dc.String.Class;
            dc.String path = "atlas/".AsHaxeString();
            path = _String.__add__(_String.__add__(path, skinInfo.model), "_tracks.json".AsHaxeString());
            if (!Res.Class.get_loader().exists(path))
            {
                return null;
            }

            return Assets.Class.getAnimationTracks(Res.Class.load(path));
        }

        public override void onActivate(Hero by, bool longPress)
        {
            base.onActivate(by, longPress);
        }

        public override void fixedUpdate()
        {
            EnsureRuntimeDependencies();
            base.fixedUpdate();
            scarf?.push(0.0, Ref<bool>.Null);
        }

        public override void postUpdate()
        {
            base.postUpdate();
            scarf?.postUpdate();
        }

        public override double get_headX()
        {
            if (life <= 0 || destroyed || spr == null || spr.frameData == null || spr.pivot == null || animationTracks == null)
            {
                return base.get_headX();
            }

            var headBone = ResolveHeadSkeleton();
            if (headBone == null)
            {
                return base.get_headX();
            }

            double baseX = spr.x - spr.frameData.realWid * spr.pivot.centerFactorX * dir;
            double x = baseX + AnimationTrack_Impl_.Class.x(headBone, spr.frame) * dir;
            return x == 0.0 ? base.get_headX() : x;
        }

        public override double get_headY()
        {
            if (life <= 0 || destroyed || spr == null || spr.frameData == null || spr.pivot == null || animationTracks == null)
            {
                return base.get_headY();
            }

            var headBone = ResolveHeadSkeleton();
            if (headBone == null)
            {
                return base.get_headY();
            }

            double baseY = spr.y - spr.frameData.realHei * spr.pivot.centerFactorY;
            double y = baseY + AnimationTrack_Impl_.Class.y(headBone, spr.frame);
            return y == 0.0 ? base.get_headY() : y;
        }

        private ArrayBytes_Int? ResolveHeadSkeleton()
        {
            var tracks = animationTracks;
            var groupName = spr?.groupName;
            if (tracks == null || groupName == null)
            {
                return null;
            }

            var groupTracks = tracks.get(groupName) as StringMap;
            if (groupTracks == null)
            {
                return null;
            }

            return groupTracks.get("headBone".AsHaxeString()) as ArrayBytes_Int;
        }

    }
}
