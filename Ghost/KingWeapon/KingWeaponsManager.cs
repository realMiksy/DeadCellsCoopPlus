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
        // Vanilla affect ids used by shield block/hold/parry; remote clear must stay aligned with game data.
        private const int ShieldAffectClearBlockOrHold0 = 96;
        private const int ShieldAffectClearBlockOrHold1 = 98;
        private const int ShieldAffectClearBlockOrHold2 = 99;

        private const double ShieldReleaseAfterLastPulseSeconds = 0.22;
        private const double ShieldPulseIgnoreAfterReleaseSeconds = 0.25;

        private readonly GhostKing king;
        private Inventory inventory = null!;
        private Weapon weapon = null!;
        private InventItem weaponItem = null!;
        private int pendingAttacks;
        private int pendingInterrupts;
        private int pendingSlot = -1;
        private long _shieldLastPulseTicks;
        private bool _shieldActive;
        private long _lastShieldReleaseTimestamp;

        public bool IsShieldActive => _shieldActive;

        public KingWeaponsManager(Hero hero, GhostKing king) : base(hero)
        {
            this.king = king;
        }

        public override void init()
        {
            var inv = king.inventory;
            if(inv != null)
                inventory = inv;
        }

        public void update()
        {
            if(hero == null) return;
            var inv = king.inventory;
            if(inventory == null && inv != null)
                inventory = inv;

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
                _lastShieldReleaseTimestamp = 0;
                pendingInterrupts = 0;
                ClearShieldAffects();
            }

            var game = dc.pr.Game.Class.ME;
            if(game != null) weapon.cd.update(game.tmod);
            var now = Stopwatch.GetTimestamp();

            if(weapon is BaseShield)
            {
                if(pendingInterrupts > 0)
                {
                    pendingInterrupts = 0;
                    pendingAttacks = 0;
                    if(_shieldActive || weapon.isCharging())
                        ReleaseShield(now);
                }

                if(pendingAttacks > 0)
                {
                    // Treat incoming ATK as "button still held" pulses. Don't stack them.
                    pendingAttacks = 0;

                    // When the remote releases the shield, a few late ATK packets can arrive and would re-trigger hold,
                    // causing the animation/state to flicker (release -> hold -> release ...). Ignore pulses briefly after release.
                    var ignorePulses = _lastShieldReleaseTimestamp != 0 &&
                        Stopwatch.GetElapsedTime(_lastShieldReleaseTimestamp, now).TotalSeconds < ShieldPulseIgnoreAfterReleaseSeconds;
                    if(!ignorePulses)
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

                    var sincePulseS = _shieldLastPulseTicks != 0
                        ? Stopwatch.GetElapsedTime(_shieldLastPulseTicks, now).TotalSeconds
                        : 0.0;
                    if(_shieldLastPulseTicks != 0 && sincePulseS > ShieldReleaseAfterLastPulseSeconds)
                    {
                        ReleaseShield(now);
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

            if(pendingInterrupts > 0)
            {
                pendingInterrupts = 0;
                if(!weapon.destroyed && weapon.isCharging())
                {
                    try { weapon.interrupt(); } catch { }
                    try { weapon.fixedUpdate(); } catch { }
                    try { weapon.postUpdate(); } catch { }
                }
            }
        }

        public void queueAttack(int slot = -1)
        {
            if(slot >= 0) pendingSlot = slot;
            if(pendingAttacks < 3)
                pendingAttacks++;
        }

        public void queueInterrupt(int slot = -1)
        {
            if(slot >= 0) pendingSlot = slot;
            if(pendingInterrupts < 3)
                pendingInterrupts++;
        }

        /// <summary>Disposes the managed weapon and clears shield state; call when GhostKing is torn down to avoid use-after-dispose.</summary>
        internal void DisposeManagedWeapon()
        {
            if(weapon != null && !weapon.destroyed)
            {
                try { weapon.dispose(); } catch { }
            }

            weapon = null!;
            weaponItem = null!;
            pendingAttacks = 0;
            pendingInterrupts = 0;
            pendingSlot = -1;
            _shieldActive = false;
            _shieldLastPulseTicks = 0;
            _lastShieldReleaseTimestamp = 0;
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
            try { king.removeAllAffects(ShieldAffectClearBlockOrHold0); } catch { }
            try { king.removeAllAffects(ShieldAffectClearBlockOrHold1); } catch { }
            try { king.removeAllAffects(ShieldAffectClearBlockOrHold2); } catch { }
        }

        private void ReleaseShield(long now)
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
            _lastShieldReleaseTimestamp = now;
            ClearShieldAffects();
            try { king.spr?._animManager?.play("idle".AsHaxeString(), null, null)?.loop(null); } catch { }
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
