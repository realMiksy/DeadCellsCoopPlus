using System.Net;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;

namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry
    {
        private IPEndPoint BuildEndpoint(string ipText, int port)
        {
            if (port <= 0 || port > 65535) port = 1234;
            if (!IPAddress.TryParse(ipText, out var ip))
            {
                ip = IPAddress.Loopback;
            }
            return new IPEndPoint(ip, port);
        }

        public void StartHostFromMenu(string ipText, int port)
        {
            var ep = BuildEndpoint(ipText, port);
            StartHostWithEndpoint(ep);
        }

        public void StartClientFromMenu(string ipText, int port)
        {
            var ep = BuildEndpoint(ipText, port);
            StartClientWithEndpoint(ep);
        }

        public void StartSteamHostFromMenu(int hostPort)
        {
            StartHostWithSteamTransport(hostPort);
        }

        public void StartSteamClientFromMenu(ulong hostSteamId)
        {
            StartClientWithSteamTransport(hostSteamId);
        }

        private void StartHostCore(Action createHost)
        {
            s_isDisposing = false;
            _net?.Dispose();
            ResetNetworkState();
            createHost();
            _netRole = NetRole.Host;
            _net?.SendHpMultipliers();
            GameMenu.SetRole(_netRole);
            GameMenu.NetRef = _net;
            ConnectionUI.NotifyConnectionsChanged();
        }

        private void StartHostWithEndpoint(IPEndPoint ep)
        {
            StartHostCore(() => _net = NetNode.CreateHost(Logger, ep));
            var lep = _net?.ListenerEndpoint;
            if (lep != null)
                Logger.Information($"[NetMod] Host listening at {lep.Address}:{lep.Port}");
        }

        private void StartClientCore(Action createClient)
        {
            s_isDisposing = false;
            _net?.Dispose();
            var main = dc.Main.Class.ME;
            if (main?.user != null)
                GameDataSync.RestoreOriginalUserState(main.user, true);
            ResetNetworkState();
            createClient();
            _netRole = NetRole.Client;
            GameMenu.SetRole(_netRole);
            GameMenu.NetRef = _net;
            ConnectionUI.NotifyConnectionsChanged();
        }

        private void StartClientWithEndpoint(IPEndPoint ep)
        {
            StartClientCore(() => _net = NetNode.CreateClient(Logger, ep));
            Logger.Information($"[NetMod] Client connecting to {ep.Address}:{ep.Port}");
        }

        private void StartHostWithSteamTransport(int hostPort)
        {
            if (!_ready)
            {
                Logger.Warning("[NetMod] Steam host start rejected: OnGameEndInit not yet run");
                return;
            }
            StartHostCore(() => _net = NetNode.CreateSteamHost(Logger, hostPort));
            Logger.Information("[NetMod] Host started with Steam P2P transport");
        }

        private void StartClientWithSteamTransport(ulong hostSteamId)
        {
            if (!_ready)
            {
                Logger.Warning("[NetMod] Steam client start rejected: OnGameEndInit not yet run");
                return;
            }
            StartClientCore(() => _net = NetNode.CreateSteamClient(Logger, hostSteamId));
            Logger.Information("[NetMod] Client connecting via Steam P2P to hostSteamId={HostSteamId}", hostSteamId);
        }

        public void StopNetworkFromMenu()
        {
            StopSteamCallbackPumpTimer();
            var roleBeforeStop = _netRole;
            if (roleBeforeStop == NetRole.Client)
            {
                Logger.Information("[NetMod] Disconnecting client from host...");
                _net?.SendControlAndFlush("BYE", 500);
            }
            else if (roleBeforeStop == NetRole.Host)
            {
                Logger.Information("[NetMod] Disposing host server...");
                _net?.SendControlAndFlush("KICK", 500);
            }

            _net?.Dispose();
            ResetNetworkState();
            _net = null;
            _netRole = NetRole.None;
            GameMenu.NetRef = null;
            GameMenu.SetRole(_netRole);
            ConnectionUI.NotifyConnectionsChanged();
        }
    }
}
