using dc.en;
using dc.tool;
using dc.tool.hero;
using dc.tool.weap;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using HaxeProxy.Runtime;
using ModCore.Utilities;
using System.Diagnostics;

namespace DeadCellsMultiplayerMod.Ghost
{
    public class KingWeaponsManager : HeroWeaponsManager
    {
        private readonly GhostKing king;
        private Inventory inventory = null!;
        private Weapon weapon = null!;
        private InventItem weaponItem = null!;
        private int pendingAttacks;
        private int pendingSlot = -1;
        private long _shieldLastPulseTicks;
        private bool _shieldActive;
        private long _shieldIgnorePulsesUntilTicks;

        public bool IsShieldActive => _shieldActive;

        public KingWeaponsManager(Hero hero, GhostKing king) : base(hero)
        {
            this.king = king;
        }

        public override void init()
        {
            inventory = king.inventory;
        }

        public void update()
        {
            if(hero == null) return;
            if(inventory == null) inventory = king.inventory;

            var item = GetWeaponItem(pendingSlot);
            if(item == null || item.kind?.Index == InventItemKind.Indexes.Meta) return;

            if(NeedsWeaponRebuild(item))
            {
                if(weapon != null && !weapon.destroyed)
                {
                    try { weapon.dispose(); } catch { }
                }

                weaponItem = item;
                weapon = KingWeaponSupport.CreateWeapon(hero, item, king);
                _shieldActive = false;
                _shieldLastPulseTicks = 0;
                _shieldIgnorePulsesUntilTicks = 0;
                ClearShieldAffects();
            }

            var game = dc.pr.Game.Class.ME;
            if(game != null) weapon.cd.update(game.tmod);

            if(weapon is BaseShield)
            {
                var now = Stopwatch.GetTimestamp();

                if(pendingAttacks > 0)
                {
                    // Treat incoming ATK as "button still held" pulses. Don't stack them.
                    pendingAttacks = 0;

                    // When the remote releases the shield, a few late ATK packets can arrive and would re-trigger hold,
                    // causing the animation/state to flicker (release -> hold -> release ...). Ignore pulses briefly after release.
                    if(now >= _shieldIgnorePulsesUntilTicks)
                    {
                        _shieldLastPulseTicks = now;

                        if(!_shieldActive && weapon.isReady())
                        {
                            ClearShieldAffects();
                            KingWeaponSupport.SyncSource(weapon);
                            weapon.prepare(getWeaponAttackSpeed(weapon));
                            _shieldActive = true;
                        }
                    }
                }

                if(_shieldActive && !weapon.destroyed)
                {
                    // Keep the shield logic running while we receive pulses; when pulses stop, release.
                    if(weapon is BaseShield shield)
                    {
                        try { shield.onShieldHolding(1.0); } catch { }
                    }

                    weapon.fixedUpdate();
                    weapon.postUpdate();

                    var sincePulse = now - _shieldLastPulseTicks;
                    var releaseAfter = (long)(Stopwatch.Frequency * 0.22);
                    if(_shieldLastPulseTicks != 0 && sincePulse > releaseAfter)
                    {
                        if(weapon is BaseShield shieldToRelease)
                        {
                            try { shieldToRelease.tryToCancel(false); } catch { }
                            try { shieldToRelease.onShieldReleased(); } catch { }
                        }

                        try { weapon.interrupt(); } catch { }
                        try { weapon.fixedUpdate(); } catch { }
                        try { weapon.postUpdate(); } catch { }
                        _shieldActive = false;
                        _shieldLastPulseTicks = 0;
                        _shieldIgnorePulsesUntilTicks = now + (long)(Stopwatch.Frequency * 0.25);
                        ClearShieldAffects();
                        try { king.spr?._animManager?.play("idle".AsHaxeString(), null, null)?.loop(null); } catch { }
                    }
                }

                return;
            }

            if(pendingAttacks > 0 && weapon.isReady())
            {
                KingWeaponSupport.SyncSource(weapon);

                weapon.prepare(getWeaponAttackSpeed(weapon));

                pendingAttacks--;
            }

            if(pendingAttacks > 1)
                pendingAttacks = 1;

            if(!weapon.destroyed)
            {
                if(weapon is BaseBow)
                {
                    // Keep ranged recoveries (mini-arrows/boomerangs) bound to KingSkin context
                    // without re-triggering full bow fixed logic each tick.
                    weapon.postUpdate();
                }
                else
                {
                    weapon.fixedUpdate();
                    weapon.postUpdate();
                }
            }
        }

        public void queueAttack(int slot = -1)
        {
            if(slot >= 0) pendingSlot = slot;
            if(pendingAttacks < 3)
                pendingAttacks++;
        }

        private bool NeedsWeaponRebuild(InventItem item)
        {
            if(item == null)
                return false;
            if(weapon == null || weapon.destroyed || weaponItem == null)
                return true;
            if(ReferenceEquals(weaponItem, item))
                return false;

            var oldPermanentId = weaponItem.permanentId;
            var newPermanentId = item.permanentId;
            if(oldPermanentId != 0 && newPermanentId != 0 && oldPermanentId != newPermanentId)
                return true;

            var oldKind = GetWeaponKindId(weaponItem);
            var newKind = GetWeaponKindId(item);
            if(!string.Equals(oldKind, newKind, StringComparison.Ordinal))
                return true;

            if(weaponItem.posID != item.posID)
                return true;

            return false;
        }

        private static string? GetWeaponKindId(InventItem? item)
        {
            if(item?.kind is InventItemKind.Weapon w)
                return w.Param0?.ToString();
            return null;
        }

        private void ClearShieldAffects()
        {
            try { king.removeAllAffects(96); } catch { }
            try { king.removeAllAffects(98); } catch { }
            try { king.removeAllAffects(99); } catch { }
        }

        private InventItem? GetWeaponItem(int slot)
        {
            var inv = inventory;
            if(inv != null)
            {
                if(slot >= 0)
                {
                    var prefer = inv.getEquippedWeaponOn(slot);
                    if(prefer != null) return prefer;
                }
                var w0 = inv.getEquippedWeaponOn(0);
                if(w0 != null) return w0;
                var w1 = inv.getEquippedWeaponOn(1);
                if(w1 != null) return w1;
            }

            if(ModEntry._net == null)
                return ModEntry.Instance?.inventItem;
            return null;
        }

    }
}
