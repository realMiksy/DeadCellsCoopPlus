using dc.pr;
using dc.ui;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using ModCore.Events;
using ModCore.Utitities;
using System.Collections.Generic;

namespace DeadCellsMultiplayerMod.MultiplayerModUI.Connection
{
    public static class _ConnectionUI
    {
        public static NetNode net = ModEntry._net!;

        public static List<string> GetAllPlayerNames()
        {
            var playerNames = new List<string>();

            if (net == null) return playerNames;

            if (!net.TryGetRemoteHpSnapshots(out var snapshots))
                return playerNames;

            var localId = net.id;

            foreach (var remote in snapshots)
            {
                string displayName = GetPlayerName(localId, remote.Id, remote.Username!);
                playerNames.Add(displayName);
            }

            return playerNames;
        }


        public static string GetPlayerName(int localId, int remoteId, string remoteUsername)
        {
            if (ModEntry.TryGetClientIndex(localId, remoteId, out var slotIndex))
            {
                var displayName = ModEntry.GetClientLabel(slotIndex);

                if (string.IsNullOrWhiteSpace(displayName) ||
                    string.Equals(displayName, "Guest", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(remoteUsername))
                        displayName = remoteUsername.Trim();
                }

                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = "Guest";

                return displayName;
            }
            return string.IsNullOrWhiteSpace(remoteUsername) ? "Guest" : remoteUsername.Trim();
        }
        public static bool ShouldAutoHideConnectionUI(this TitleScreen titleScreen, bool visible)
        {
            return ConnectionUI.set_visible = visible;
        }

    }
}