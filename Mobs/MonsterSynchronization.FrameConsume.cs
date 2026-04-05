using System.Threading.Tasks;
using ModCore.Events.Interfaces.Game;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    /// <summary>
    /// Per-frame incoming network consume for mob sync. Uses <c>await Task.Yield()</c> between stages so DCCM can schedule
    /// continuations without any environment configuration. <see cref="IOnFrameUpdate.OnFrameUpdate"/> runs the task to
    /// completion via <c>GetAwaiter().GetResult()</c>. Hashlink <c>Mob</c>/<c>Level</c> access stays inside <c>Consume*</c> only.
    /// </summary>
    public partial class MobsSynchronization
    {
        private static async Task RunHostIncomingFrameConsumeAsync(NetNode net)
        {
            ConsumeIncomingClientMobStates(net);
            await Task.Yield();
            ConsumeIncomingMobDraws(net);
            await Task.Yield();
            ConsumeIncomingMobDies(net);
            await Task.Yield();
            ConsumeIncomingMobHits(net);
        }

        private static async Task RunClientIncomingFrameConsumeAsync(NetNode net)
        {
            ConsumeIncomingHostMobStates(net);
            await Task.Yield();
            ConsumeIncomingHostMobAttacks(net);
            await Task.Yield();
            ConsumeIncomingMobDies(net);
            await Task.Yield();
            ConsumeIncomingMobHits(net);
        }
    }
}
