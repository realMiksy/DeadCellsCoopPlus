using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using ModCore.Events;

namespace DeadCellsMultiplayerMod.MultiplayerModUI.ConnectionUI
{
    public class ConnectionUI :
    IEventReceiver,
    IOnAdvancedModuleInitializing
    {
        private ModEntry mod { get; set; }

        public ConnectionUI(ModEntry Entry)
        {
            mod = Entry;
            EventSystem.AddReceiver(this);
        }

        void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
        {

            entry.Logger.Information("\x1b[32m[[ModEntry.Connection] Initializing Connection...]\x1b[0m ");
            
        }
    }
}