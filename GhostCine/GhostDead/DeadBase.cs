using dc.en;
using Cooldown = CooldownHelper.Cooldown;

namespace DeadCellsMultiplayerMod
{
    public class DeadBase : dc.GameCinematic
    {
        public DeadBase(Hero hero, KingSkin king)
        {
            _DeadBase.EnterGhostDead(this, hero, king);
        }
        public override void update()
        {
            base.update();
            if (!this.CanGohostCreate()) return;
        }

        public bool CanGohostCreate()
        {
            int k = Cooldown.Encode(Cooldown.Keys.KING_Create);
            return ModEntry._companionKing.cd.fastCheck.exists(k);
        }

    }
}