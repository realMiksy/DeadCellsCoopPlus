using dc.en;
using dc.tool;
using dc.tool.hero;
using dc.tool.weap;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
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
            if(hero == null || king == null) return;
            if(inventory == null) inventory = king.inventory;

            var item = GetWeaponItem(pendingSlot);
            if(item == null || item.kind?.Index == InventItemKind.Indexes.Meta) return;

            if(weapon == null || weaponItem == null || weaponItem.permanentId != item.permanentId)
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

            if(weapon is BaseShield shield)
            {
                var now = Stopwatch.GetTimestamp();

                if(pendingAttacks > 0)
                {
                    // Treat incoming ATK as "button still held" pulses. Don't stack them.
                    pendingAttacks = 0;

                    // When the remote releases the shield, a few late ATK packets can arrive and would re-trigger hold,
                    // causing the animation/state to flicker (release -> hold -> release ...). Ignore pulses briefly after release.
                    if(now < _shieldIgnorePulsesUntilTicks)
                    {
                        // Pulse ignored.
                    }
                    else
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
                    weapon.fixedUpdate();
                    weapon.postUpdate();

                    var sincePulse = now - _shieldLastPulseTicks;
                    var releaseAfter = (long)(Stopwatch.Frequency * 0.18);
                    if(_shieldLastPulseTicks != 0 && sincePulse > releaseAfter)
                    {
                        weapon.interrupt();
                        _shieldActive = false;
                        _shieldLastPulseTicks = 0;
                        _shieldIgnorePulsesUntilTicks = now + (long)(Stopwatch.Frequency * 0.25);
                        ClearShieldAffects();
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

            if(!weapon.destroyed && weapon is not BaseBow && weapon is not BaseShield)
            {
                weapon.fixedUpdate();
                weapon.postUpdate();
            }
        }

        public void queueAttack(int slot = -1)
        {
            if(slot >= 0) pendingSlot = slot;
            if(pendingAttacks < 3)
                pendingAttacks++;
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
