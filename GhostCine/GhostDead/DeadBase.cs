using CineHookInitialize;
using dc.en;
using Cd = CooldownHelper.Cooldown;

namespace DeadCellsMultiplayerMod
{
    public class DeadBase : dc.GameCinematic
    {
        private Hero owen = null!;
        public DeadBase(Hero hero, KingSkin king)
        {
            _DeadBase.EnterGhostDead(this, hero, king);
            owen = hero;
        }
        public override void update()
        {
            base.update();
            var item = CineHooks.item;
            if (item != null && owen.cd.fastCheck.exists(Cd.Encode(Cd.Keys.DELET_YOLO)))
            {
                owen.dropAndUpdateItem(CineHooks.item);
                owen.cd.fastCheck.remove(Cd.Encode(Cd.Keys.DELET_YOLO));
                owen.cd.cdList.remove(Cd.Encode(Cd.Keys.DELET_YOLO));
            }
            if (!this.CanGohostCreate()) return;
        }

        public bool CanGohostCreate()
        {
            int k = Cooldown.Encode(Cooldown.Keys.KING_Create);
            var king = ModEntry.GetPrimaryClient();
            if (king == null)
                return false;
            return king.cd.fastCheck.exists(k);
        }

    }
}
