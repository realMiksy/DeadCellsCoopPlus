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

            var item = GetWeaponItem();
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

            if(weapon.isReady())
            {
                weapon.SyncSource();
                weapon.prepare(getWeaponAttackSpeed(weapon));
            }

            if(!weapon.destroyed)
            {
                weapon.fixedUpdate();
                weapon.postUpdate();
            }
        }

        private InventItem? GetWeaponItem()
        {
            var inv = inventory;
            if(inv != null)
            {
                var w0 = inv.getEquippedWeaponOn(0);
                if(w0 != null) return w0;
                var w1 = inv.getEquippedWeaponOn(1);
                if(w1 != null) return w1;
            }

            return ModEntry.Instance?.inventItem;
        }

    }
}
