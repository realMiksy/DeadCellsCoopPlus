using System;
using dc;
using dc.en;
using dc.hl.types;
using dc.libs.heaps.slib;
using dc.libs.heaps.slib._AnimManager;
using dc.tool;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using HaxeProxy.Runtime.Internals;
using HaxeProxy.Runtime.Internals.Cache;
using ModCore.Utitities;

namespace DeadCellsMultiplayerMod.Ghost
{
    public class KingWeapon : Weapon
    {
        internal KingSkin source;
        private bool _usingKingContext;
        private static ObjFieldInfoCache _cachedAnimId;
        private static ObjFieldInfoCache _cachedAnimSpd;

        public KingWeapon(Hero owner, InventItem item, KingSkin source) : base(owner, item)
        {
            _KingWeapon.__inst_construct__(this, source);
            BindAreasToSource();
        }

        public override double get_shootX()
        {
            if(source == null) return base.get_shootX();
            return source.get_shootX();
        }

        public override double get_shootY()
        {
            if(source == null) return base.get_shootY();
            return source.get_shootY();
        }

        public override void dynOnFxFrame(WeaponSkill s, virtual_animId_animSpd_area_breachBonus_canCrit_charge_coolDown_critMul_dynamicCharge_earlyCombo_fxId_fxProps_glowColor_hitFrame_lockCtrlAfter_onionSkinFrame_onionSkinOffX_power_props_sfxCharge_sfxHit_sfxProps_sfxRelease_ cinf)
        {
            if(cinf == null)
                return;

            if(!disableSounds && !customSoundManagement)
            {
                playReleaseSfx(null, null, Ref<bool>.Null, null, null);
            }

            var fxId = cinf.fxId;
            if(fxId != null)
            {
                var lib = Assets.Class.fxWeapon;
                var groups = lib?.groups;
                if(groups == null || !groups.exists(fxId))
                {
                    return;
                }
                var group = groups.get(fxId) as LibGroup;
                if(group == null || group.frames == null || group.frames.length == 0)
                {
                    return;
                }
            }

            var fx = source?._level?.fx ?? owner?._level?.fx;
            if(fx == null) return;

            var castSpeed = get_curSkill()?.getCastSpeed() ?? 1.0;
            if(source?.spr != null)
            {
                lastFx = fx.playWeaponAnimFromObject(source.spr, cinf, castSpeed, null, null, null, null, null);
            }
            else
            {
                lastFx = fx.playWeaponAnim(owner, cinf, castSpeed, null, null, null);
            }
        }

        public override void dynOnAttackAnim(WeaponSkill s, virtual_animId_animSpd_area_breachBonus_canCrit_charge_coolDown_critMul_dynamicCharge_earlyCombo_fxId_fxProps_glowColor_hitFrame_lockCtrlAfter_onionSkinFrame_onionSkinOffX_power_props_sfxCharge_sfxHit_sfxProps_sfxRelease_ a)
        {
            var animId = ReadAnimId(a);
            if(animId == null) return;

            var spr = source?.spr;
            var anim = spr?.get_anim();
            if(anim == null) return;

            var animManager = anim.play(animId, null, null);
            var animManager2 = animManager.stopOnLastFrame(Ref<bool>.Null);

            var animSpd = ReadAnimSpd(a);
            double spd = animSpd ?? 1.0;
            if(spd < 0.0) spd = 0.0 - spd;
            spd *= get_curSkill()?.getCastSpeed() ?? 1.0;

            if(!animManager2.destroyed)
            {
                var stack = animManager2.stack;
                var length = stack.length;
                if(length > 0)
                {
                    var idx = length - 1;
                    var instance = stack.array[idx] as AnimInstance;
                    if(instance != null) instance.speed = spd;
                }
            }

            if(animSpd != null && (double)animSpd < 0.0)
            {
                animManager2.reverse();
            }
        }

        public override void prepare(double attackSpeed)
        {
            WithKingContext(() => base.prepare(attackSpeed));
        }

        public override void fixedUpdate()
        {
            WithKingContext(base.fixedUpdate);
        }

        public override void postUpdate()
        {
            WithKingContext(base.postUpdate);
        }

        public override void updateAmmoHud()
        {
        }

        internal void SyncSource()
        {
            BindAreasToSource();
        }

        private void BindAreasToSource()
        {
            if(source == null || areas == null) return;
            var arr = areas;
            for (int i = 0; i < arr.length; i++)
            {
                var a = arr.array[i] as Area;
                if(a != null)
                {
                    a.setRelativePos(source, a.x, a.y);
                }
            }
        }

        private static double? ReadAnimSpd(virtual_animId_animSpd_area_breachBonus_canCrit_charge_coolDown_critMul_dynamicCharge_earlyCombo_fxId_fxProps_glowColor_hitFrame_lockCtrlAfter_onionSkinFrame_onionSkinOffX_power_props_sfxCharge_sfxHit_sfxProps_sfxRelease_ a)
        {
            if(a == null) return null;
            object raw;
            try
            {
                raw = HaxeProxyHelper.GetFieldById<object>((HaxeProxyBase)(object)a, "animSpd", ref _cachedAnimSpd);
            }
            catch
            {
                return null;
            }
            if(raw == null) return null;
            if(raw is double d) return d;
            if(raw is float f) return f;
            if(raw is int i) return i;
            if(raw is long l) return l;
            return null;
        }

        private static dc.String ReadAnimId(virtual_animId_animSpd_area_breachBonus_canCrit_charge_coolDown_critMul_dynamicCharge_earlyCombo_fxId_fxProps_glowColor_hitFrame_lockCtrlAfter_onionSkinFrame_onionSkinOffX_power_props_sfxCharge_sfxHit_sfxProps_sfxRelease_ a)
        {
            if(a == null) return null;
            try
            {
                var raw = HaxeProxyHelper.GetFieldById<object>((HaxeProxyBase)(object)a, "animId", ref _cachedAnimId);
                if(raw == null) return null;
                if(raw is dc.String hs) return hs;
                if(raw is string s) return s.AsHaxeString();
                return null;
            }
            catch
            {
                return null;
            }
        }

        private void WithKingContext(Action action)
        {
            if(_usingKingContext)
            {
                action();
                return;
            }

            var hero = owner;
            var src = source;
            if(hero == null || src == null)
            {
                action();
                return;
            }

            _usingKingContext = true;
            var savedSpr = hero.spr;
            var savedLevel = hero._level;
            var savedTeam = hero._team;
            var savedCx = hero.cx;
            var savedCy = hero.cy;
            var savedXr = hero.xr;
            var savedYr = hero.yr;
            var savedDir = hero.dir;
            var savedDx = hero.dx;
            var savedDy = hero.dy;

            try
            {
                if(src.spr != null) hero.spr = src.spr;
                if(src._level != null) hero._level = src._level;
                if(src._team != null) hero._team = src._team;
                hero.cx = src.cx;
                hero.cy = src.cy;
                hero.xr = src.xr;
                hero.yr = src.yr;
                hero.dir = src.dir;
                hero.dx = src.dx;
                hero.dy = src.dy;
                action();
            }
            finally
            {
                hero.spr = savedSpr;
                hero._level = savedLevel;
                hero._team = savedTeam;
                hero.cx = savedCx;
                hero.cy = savedCy;
                hero.xr = savedXr;
                hero.yr = savedYr;
                hero.dir = savedDir;
                hero.dx = savedDx;
                hero.dy = savedDy;
                _usingKingContext = false;
            }
        }
    }
}
