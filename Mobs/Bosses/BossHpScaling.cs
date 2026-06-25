using dc.en;
using dc.en.mob;

namespace DeadCellsMultiplayerMod.Mobs.Bosses;

public static class BossHpScaling
{
    public static void ScaleForMultiplayer(Mob mob)
    {
        if (mob == null)
            return;

        var net = DeadCellsMultiplayerMod.GameMenu.NetRef;
        var playerCount = (net != null && net.IsAlive) ? (1 + NetNode.ConnectedClientCount) : 1;

        try
        {
            var mult = BossSyncHelpers.GetHpMultiplierForMob(mob, playerCount);
            if (System.Math.Abs(mult - 1.0) <= 0.0001)
                return;
            var maxLife = System.Math.Max(1, mob.maxLife);
            var life = mob.life;

            var newMaxLife = System.Math.Max(1, (int)System.Math.Round(maxLife * mult));
            var newLife = System.Math.Clamp((int)System.Math.Round(life * mult), 0, newMaxLife);
            mob.maxLife = newMaxLife;
            mob.life = newLife;
            try { mob.initLife(newLife, newMaxLife); } catch { }
        }
        catch
        {
            // ignore
        }
    }
}
