using dc.en.inter;

namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry
    {
        // v5.6 rollback: v5.5's live level-quad mutation was too aggressive and could
        // destabilize Hashlink GC/native state. Keep these methods as safe no-ops so older
        // ModEntry.cs call sites compile, but do not mutate level entity/quad lists.
        private void RunFrameStabilityGuards()
        {
        }

        private void Hook_HiddenTrigger_fixedUpdate(Hook_HiddenTrigger.orig_fixedUpdate orig, HiddenTrigger self)
        {
            orig?.Invoke(self);
        }
    }
}
