using dc.en;
using dc.tool;
using dc.tool.hero;
using DeadCellsMultiplayerMod.Ghost.GhostBase;

namespace DeadCellsMultiplayerMod.Ghost
{
    public class KingWeaponsManager : HeroWeaponsManager
    {
        private readonly GhostKing king;
        private Inventory inventory = null!;
        private KingWeapon weapon = null!;
        private InventItem weaponItem = null!;
        private int pendingAttacks;
        private int pendingSlot = -1;

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
            if(item == null || item.kind.ToString() == "Meta") return;

            if(weapon == null || weaponItem == null || weaponItem.permanentId != item.permanentId)
            {
                weaponItem = item;
                weapon = new KingWeapon(hero, item, king);
                weapon.needButtonRelease = false;
                weapon.requireRelease = false;
            }

            var game = dc.pr.Game.Class.ME;
            if(game != null) weapon.cd.update(game.tmod);

            if(pendingAttacks > 0 && weapon.isReady())
            {
                weapon.SyncSource();
                weapon.prepare(getWeaponAttackSpeed(weapon));
                pendingAttacks--;
            }

            if(!weapon.destroyed)
            {
                weapon.fixedUpdate();
                weapon.postUpdate();
            }
        }

        public void queueAttack(int slot = -1)
        {
            if(slot >= 0) pendingSlot = slot;
            pendingAttacks++;
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
