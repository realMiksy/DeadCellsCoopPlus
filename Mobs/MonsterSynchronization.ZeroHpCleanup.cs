using dc.en;
using DeadCellsMultiplayerMod.Mobs.Bosses;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public partial class MobsSynchronization
    {
        /// <summary>
        /// v6.4.5: finalize non-boss mobs that reached zero HP but were left as live entities.
        /// This can happen after client lethal-hit requests, revive-state network floods, or stale
        /// host snapshots where the HP bar reaches zero before vanilla onDie finishes.
        /// </summary>
        private static void TryFinalizeNonBossZeroLifeMob(Mob? mob, string context)
        {
            if (mob == null)
                return;

            try
            {
                if (mob.destroyed)
                    return;
                if (BossSyncHelpers.IsBossMob(mob))
                    return;
                if (mob.life > 0)
                    return;
            }
            catch
            {
                return;
            }

            try
            {
                RunWithSuppressedMobDieSend(() =>
                {
                    try { mob.life = 0; } catch { }
                    try { mob.onDie(); } catch { }
                });
            }
            catch
            {
            }

            try { mob._targetable = false; } catch { }
            try { mob.isOutOfGame = true; } catch { }

            try
            {
                lock (Sync)
                {
                    RemoveTrackedMobLocked(mob);
                }
            }
            catch
            {
            }
        }
    }
}
