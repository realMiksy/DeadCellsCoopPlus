using System;
using dc;
using dc.en;
using dc.en.loot;
using dc.h2d;
using dc.hl.types;
using dc.tool;
using dc.tool.atk;
using dc.tool.weap;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;

namespace DeadCellsMultiplayerMod.Ghost;

internal static class KingWeaponHooks
{
    private static bool _installed;

    internal static void Install()
    {
        if(_installed)
            return;
        _installed = true;

        Hook_Inventory.add += Hook_Inventory_add;
        Hook_Inventory.equip += Hook_Inventory_equip;
        Hook_Inventory.swapWeapons += Hook_Inventory_swapWeapons;
        Hook_Inventory.replace += Hook_Inventory_replace;

        Hook_Hero.lockControlFromSkill += Hook_Hero_lockControlFromSkill;
        Hook_Hero.unlockControls += Hook_Hero_unlockControls;
        Hook_Viewport.bumpDir += Hook_Viewport_bumpDir;

        Hook_Entity.recoil += Hook_Entity_recoil;
        Hook_Entity.bump += Hook_Entity_bump;
        Hook_Entity.bumpAwayFrom += Hook_Entity_bumpAwayFrom;
        Hook_Entity.cancelVelocities += Hook_Entity_cancelVelocities;
        Hook_Entity.setAffectS += Hook_Entity_setAffectS;
        Hook_Entity.removeAllAffects += Hook_Entity_removeAllAffects;

        Hook_Weapon.prepare += Hook_Weapon_prepare;
        Hook_Weapon.get_shootX += Hook_Weapon_get_shootX;
        Hook_Weapon.get_shootY += Hook_Weapon_get_shootY;
        Hook_Weapon.fixedUpdate += Hook_Weapon_fixedUpdate;
        Hook_Weapon.postUpdate += Hook_Weapon_postUpdate;
        Hook_Weapon.dynOnAttackAnim += Hook_Weapon_dynOnAttackAnim;
        Hook_Weapon.dynOnFxFrame += Hook_Weapon_dynOnFxFrame;
        Hook_Weapon.updateAmmoHud += Hook_Weapon_updateAmmoHud;

        Hook_BaseBow.dynOnAttackAnim += Hook_BaseBow_dynOnAttackAnim;
        Hook_BaseBow.fixedUpdate += Hook_BaseBow_fixedUpdate;
        Hook_BaseBow.get_shootY += Hook_BaseBow_get_shootY;
        Hook_BaseBow.playShootAnim += Hook_BaseBow_playShootAnim;
        Hook_BaseBow.shoot += Hook_BaseBow_shoot;
        Hook_BaseBow.dynamicChargeExecute += Hook_BaseBow_dynamicChargeExecute;
        Hook_BaseBow.onBowChargeStart += Hook_BaseBow_onBowChargeStart;
        Hook_BaseBow.onBowCharging += Hook_BaseBow_onBowCharging;
        Hook_BaseBow.onExecute += Hook_BaseBow_onExecute;
        Hook_BaseBow.interrupt += Hook_BaseBow_interrupt;

        Hook_BaseShield.tryToCancel += Hook_BaseShield_tryToCancel;
        Hook_BaseShield.onShieldChargeStart += Hook_BaseShield_onShieldChargeStart;
        Hook_BaseShield.onShieldReleased += Hook_BaseShield_onShieldReleased;
        Hook_BaseShield.startParry += Hook_BaseShield_startParry;
        Hook_BaseShield.onShieldStartParry += Hook_BaseShield_onShieldStartParry;
        Hook_BaseShield.onShieldEndParry += Hook_BaseShield_onShieldEndParry;
        Hook_BaseShield.onShieldHolding += Hook_BaseShield_onShieldHolding;
        Hook_BaseShield.onShieldBlock += Hook_BaseShield_onShieldBlock;
        Hook_BaseShield.onShieldCounterSuccessful += Hook_BaseShield_onShieldCounterSuccessful;
        Hook_BaseShield.counterGrenade += Hook_BaseShield_counterGrenade;
        Hook_BaseShield.counterBullet += Hook_BaseShield_counterBullet;

        Hook_Ammo.startMagnet += Hook_Ammo_startMagnet;
        Hook_Ammo.retrieve += Hook_Ammo_retrieve;
        Hook_Ammo.pickUp += Hook_Ammo_pickUp;
    }

    private static InventItem Hook_Inventory_add(Hook_Inventory.orig_add orig, Inventory self, InventItem i)
    {
        var instance = ModEntry.Instance;
        if(instance != null)
            return instance.NotifyInventoryAddFromKingWeaponHooks(orig, self, i);
        return orig(self, i);
    }

    private static bool Hook_Inventory_equip(Hook_Inventory.orig_equip orig, Inventory self, InventItem i)
    {
        var instance = ModEntry.Instance;
        if(instance != null)
            return instance.NotifyInventoryEquipFromKingWeaponHooks(orig, self, i);
        return orig(self, i);
    }

    private static void Hook_Inventory_swapWeapons(Hook_Inventory.orig_swapWeapons orig, Inventory self)
    {
        var instance = ModEntry.Instance;
        if(instance != null)
        {
            instance.NotifyInventorySwapWeaponsFromKingWeaponHooks(orig, self);
            return;
        }
        orig(self);
    }

    private static void Hook_Inventory_replace(Hook_Inventory.orig_replace orig, Inventory self, InventItem by, InventItem oldPos)
    {
        var instance = ModEntry.Instance;
        if(instance != null)
        {
            instance.NotifyInventoryReplaceFromKingWeaponHooks(orig, self, by, oldPos);
            return;
        }
        orig(self, by, oldPos);
    }

    private static void Hook_Hero_lockControlFromSkill(Hook_Hero.orig_lockControlFromSkill orig, Hero self, double sec)
    {
        if(KingWeaponSupport.IsInKingContext && ModEntry.me != null && ReferenceEquals(self, ModEntry.me))
            return;
        orig(self, sec);
    }

    private static void Hook_Hero_unlockControls(Hook_Hero.orig_unlockControls orig, Hero self)
    {
        if(KingWeaponSupport.IsInKingContext && ModEntry.me != null && ReferenceEquals(self, ModEntry.me))
            return;
        orig(self);
    }

    private static void Hook_Viewport_bumpDir(Hook_Viewport.orig_bumpDir orig, Viewport self, int dir, double? pow)
    {
        if(KingWeaponSupport.IsInKingContext)
            return;
        orig(self, dir, pow);
    }

    private static void Hook_Entity_recoil(Hook_Entity.orig_recoil orig, Entity self, double dx)
    {
        if(KingWeaponSupport.IsInKingContext && ModEntry.me != null && ReferenceEquals(self, ModEntry.me))
            return;
        orig(self, dx);
    }

    private static void Hook_Entity_bump(Hook_Entity.orig_bump orig, Entity self, double dy, double ignoreResist, bool? dx)
    {
        if(KingWeaponSupport.IsInKingContext && ModEntry.me != null && ReferenceEquals(self, ModEntry.me))
            return;
        orig(self, dy, ignoreResist, dx);
    }

    private static void Hook_Entity_bumpAwayFrom(Hook_Entity.orig_bumpAwayFrom orig, Entity self, Entity e, double? pow, bool? ignoreResist)
    {
        if(KingWeaponSupport.IsInKingContext && ModEntry.me != null && ReferenceEquals(self, ModEntry.me))
            return;
        orig(self, e, pow, ignoreResist);
    }

    private static void Hook_Entity_cancelVelocities(Hook_Entity.orig_cancelVelocities orig, Entity self)
    {
        if(KingWeaponSupport.IsInKingContext && ModEntry.me != null && ReferenceEquals(self, ModEntry.me))
            return;
        orig(self);
    }

    private static void Hook_Entity_setAffectS(
        Hook_Entity.orig_setAffectS orig,
        Entity self,
        int id,
        double sec,
        Ref<double> ignoreResist,
        bool? allowResist)
    {
        if(KingWeaponSupport.IsInKingContext && ModEntry.me != null && ReferenceEquals(self, ModEntry.me))
            return;
        orig(self, id, sec, ignoreResist, allowResist);
    }

    private static void Hook_Entity_removeAllAffects(Hook_Entity.orig_removeAllAffects orig, Entity self, int list)
    {
        if(KingWeaponSupport.IsInKingContext && ModEntry.me != null && ReferenceEquals(self, ModEntry.me))
            return;
        orig(self, list);
    }

    private static void Hook_Weapon_prepare(Hook_Weapon.orig_prepare orig, Weapon self, double attackSpeed)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.PatchCurrentSkill(self);
            KingWeaponSupport.WithKingContext(self, () => orig(self, attackSpeed));
            KingWeaponSupport.PatchCurrentSkill(self);
            KingWeaponSupport.SyncSource(self);
            return;
        }

        ModEntry.Instance?.NotifyLocalWeaponPrepareFromKingWeaponHooks(self);
        orig(self, attackSpeed);
    }

    private static double Hook_Weapon_get_shootX(Hook_Weapon.orig_get_shootX orig, Weapon self)
    {
        if(KingWeaponSupport.TryGetSource(self, out var source) && source != null)
            return source.get_shootX();
        return orig(self);
    }

    private static double Hook_Weapon_get_shootY(Hook_Weapon.orig_get_shootY orig, Weapon self)
    {
        if(KingWeaponSupport.TryGetSource(self, out var source) && source != null)
            return source.get_shootY();
        return orig(self);
    }

    private static void Hook_Weapon_fixedUpdate(Hook_Weapon.orig_fixedUpdate orig, Weapon self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.PatchCurrentSkill(self);
            KingWeaponSupport.WithKingContext(self, () => orig(self));
            KingWeaponSupport.SyncSource(self);
            return;
        }

        orig(self);
    }

    private static void Hook_Weapon_postUpdate(Hook_Weapon.orig_postUpdate orig, Weapon self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.PatchCurrentSkill(self);
            KingWeaponSupport.WithKingContext(self, () => orig(self));
            KingWeaponSupport.SyncSource(self);
            return;
        }

        orig(self);
    }

    private static void Hook_Weapon_dynOnAttackAnim(
        Hook_Weapon.orig_dynOnAttackAnim orig,
        Weapon self,
        WeaponSkill s,
        virtual_animId_animSpd_area_breachBonus_canCrit_charge_coolDown_critMul_dynamicCharge_earlyCombo_fxId_fxProps_glowColor_hitFrame_lockCtrlAfter_onionSkinFrame_onionSkinOffX_power_props_sfxCharge_sfxHit_sfxProps_sfxRelease_ a)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self, s, a); } catch { }
            });
            return;
        }

        orig(self, s, a);
    }

    private static void Hook_Weapon_dynOnFxFrame(
        Hook_Weapon.orig_dynOnFxFrame orig,
        Weapon self,
        WeaponSkill s,
        virtual_animId_animSpd_area_breachBonus_canCrit_charge_coolDown_critMul_dynamicCharge_earlyCombo_fxId_fxProps_glowColor_hitFrame_lockCtrlAfter_onionSkinFrame_onionSkinOffX_power_props_sfxCharge_sfxHit_sfxProps_sfxRelease_ cinf)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self, s, cinf); } catch { }
            });
            return;
        }

        orig(self, s, cinf);
    }

    private static void Hook_Weapon_updateAmmoHud(Hook_Weapon.orig_updateAmmoHud orig, Weapon self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
            return;
        orig(self);
    }

    private static void Hook_BaseBow_dynOnAttackAnim(
        Hook_BaseBow.orig_dynOnAttackAnim orig,
        BaseBow self,
        WeaponSkill s,
        virtual_animId_animSpd_area_breachBonus_canCrit_charge_coolDown_critMul_dynamicCharge_earlyCombo_fxId_fxProps_glowColor_hitFrame_lockCtrlAfter_onionSkinFrame_onionSkinOffX_power_props_sfxCharge_sfxHit_sfxProps_sfxRelease_ cinf)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self, s, cinf); } catch { }
            });
            return;
        }

        orig(self, s, cinf);
    }

    private static void Hook_BaseBow_fixedUpdate(Hook_BaseBow.orig_fixedUpdate orig, BaseBow self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.PatchCurrentSkill(self);
            KingWeaponSupport.WithKingContext(self, () => orig(self));
            KingWeaponSupport.SyncSource(self);
            return;
        }
        orig(self);
    }

    private static double Hook_BaseBow_get_shootY(Hook_BaseBow.orig_get_shootY orig, BaseBow self)
    {
        if(KingWeaponSupport.TryGetSource(self, out var source) && source != null)
            return source.get_shootY();
        return orig(self);
    }

    private static void Hook_BaseBow_playShootAnim(Hook_BaseBow.orig_playShootAnim orig, BaseBow self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self); } catch { }
            });
            return;
        }
        orig(self);
    }

    private static void Hook_BaseBow_shoot(Hook_BaseBow.orig_shoot orig, BaseBow self, ArrayObj entity)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self, entity); } catch { }
            });
            return;
        }
        orig(self, entity);
        ModEntry.Instance?.NotifyLocalBowShotFromKingWeaponHooks(self);
    }

    private static void Hook_BaseBow_dynamicChargeExecute(Hook_BaseBow.orig_dynamicChargeExecute orig, BaseBow self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self); } catch { }
            });
            return;
        }
        orig(self);
    }

    private static void Hook_BaseBow_onBowChargeStart(Hook_BaseBow.orig_onBowChargeStart orig, BaseBow self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self); } catch { }
            });
            return;
        }
        orig(self);
    }

    private static void Hook_BaseBow_onBowCharging(Hook_BaseBow.orig_onBowCharging orig, BaseBow self, double r)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self, r); } catch { }
            });
            return;
        }
        orig(self, r);
    }

    private static bool Hook_BaseBow_onExecute(Hook_BaseBow.orig_onExecute orig, BaseBow self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
            return KingWeaponSupport.WithKingContext(self, () =>
            {
                try { return orig(self); } catch { return false; }
            });
        return orig(self);
    }

    private static void Hook_BaseBow_interrupt(Hook_BaseBow.orig_interrupt orig, BaseBow self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self); } catch { }
            });
            return;
        }
        orig(self);
    }

    private static bool Hook_BaseShield_tryToCancel(Hook_BaseShield.orig_tryToCancel orig, BaseShield self, bool byWeapon)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
            return KingWeaponSupport.WithKingContext(self, () =>
            {
                try { return orig(self, byWeapon); } catch { return false; }
            });
        return orig(self, byWeapon);
    }

    private static void Hook_BaseShield_onShieldChargeStart(Hook_BaseShield.orig_onShieldChargeStart orig, BaseShield self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self); } catch { }
            });
            return;
        }
        orig(self);
    }

    private static void Hook_BaseShield_onShieldReleased(Hook_BaseShield.orig_onShieldReleased orig, BaseShield self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self); } catch { }
            });
            return;
        }
        orig(self);
    }

    private static void Hook_BaseShield_startParry(Hook_BaseShield.orig_startParry orig, BaseShield self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self); } catch { }
            });
            return;
        }
        orig(self);
    }

    private static void Hook_BaseShield_onShieldStartParry(Hook_BaseShield.orig_onShieldStartParry orig, BaseShield self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self); } catch { }
            });
            return;
        }
        orig(self);
    }

    private static void Hook_BaseShield_onShieldEndParry(Hook_BaseShield.orig_onShieldEndParry orig, BaseShield self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self); } catch { }
            });
            return;
        }
        orig(self);
    }

    private static void Hook_BaseShield_onShieldHolding(Hook_BaseShield.orig_onShieldHolding orig, BaseShield self, double ratio)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self, ratio); } catch { }
            });
            return;
        }

        ModEntry.Instance?.NotifyLocalShieldHoldingPulseFromKingWeaponHooks(self, ratio);
        orig(self, ratio);
    }

    private static void Hook_BaseShield_onShieldBlock(Hook_BaseShield.orig_onShieldBlock orig, BaseShield self, AttackData sourceAtk, bool fullParry)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self, sourceAtk, fullParry); } catch { }
            });
            return;
        }
        orig(self, sourceAtk, fullParry);
    }

    private static void Hook_BaseShield_onShieldCounterSuccessful(Hook_BaseShield.orig_onShieldCounterSuccessful orig, BaseShield self, AttackData sourceAtk, bool fullParry)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self, sourceAtk, fullParry); } catch { }
            });
            return;
        }
        orig(self, sourceAtk, fullParry);
    }

    private static void Hook_BaseShield_counterGrenade(Hook_BaseShield.orig_counterGrenade orig, BaseShield self, Grenade repelled)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self, repelled); } catch { }
            });
            return;
        }
        orig(self, repelled);
    }

    private static Bullet Hook_BaseShield_counterBullet(Hook_BaseShield.orig_counterBullet orig, BaseShield self, AttackData sourceAtk, Bullet cBullet, bool fullParry)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
            return KingWeaponSupport.WithKingContext(self, () =>
            {
                try { return orig(self, sourceAtk, cBullet, fullParry); } catch { return cBullet; }
            });
        return orig(self, sourceAtk, cBullet, fullParry);
    }

    private static void Hook_Ammo_startMagnet(Hook_Ammo.orig_startMagnet orig, Ammo self, Entity e)
    {
        if(TryGetAmmoSource(self, out var source) && source != null && ModEntry.me != null && ReferenceEquals(e, ModEntry.me))
        {
            orig(self, source);
            return;
        }

        orig(self, e);
    }

    private static void Hook_Ammo_retrieve(Hook_Ammo.orig_retrieve orig, Ammo self, Hero h)
    {
        orig(self, h);
        if(ModEntry.me != null && ReferenceEquals(h, ModEntry.me))
            ModEntry.Instance?.NotifyLocalAmmoChangedFromKingWeaponHooks(self?.item);
    }

    private static void Hook_Ammo_pickUp(Hook_Ammo.orig_pickUp orig, Ammo self, Hero h)
    {
        orig(self, h);
        if(ModEntry.me != null && ReferenceEquals(h, ModEntry.me))
            ModEntry.Instance?.NotifyLocalAmmoChangedFromKingWeaponHooks(self?.item);
    }

    private static bool TryGetAmmoSource(Ammo? ammo, out KingSkin source)
    {
        source = null!;
        if(ammo == null)
            return false;

        InventItem item;
        try
        {
            item = ammo.item;
        }
        catch
        {
            return false;
        }

        if(item == null)
            return false;

        if(!KingWeaponSupport.TryGetSourceByItem(item, out source) || source == null)
            return false;

        if(source.destroyed || source.life <= 0)
            return false;

        return true;
    }
}
