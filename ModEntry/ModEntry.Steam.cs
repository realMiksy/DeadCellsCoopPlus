using Steamworks;
using DeadCellsMultiplayerMod.Tools;


namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry
    {
        private static void TryParseConnectLobbyFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "+connect_lobby", StringComparison.OrdinalIgnoreCase) &&
                    ulong.TryParse(args[i + 1], out var lobbyId) && lobbyId > 0)
                {
                    Instance?.Logger.Information("[NetMod][Steam] Launch parameter +connect_lobby detected lobbyId={LobbyId}", lobbyId);
                    GameMenu.EnqueueMainThreadCoalesced("steam:overlay-join", () => GameMenu.HandleSteamOverlayJoinRequest(lobbyId));
                    return;
                }
            }
        }

        private static void TryDeferredSteamOverlayCallbackRegistration()
        {
            if (!s_steamOverlayCallbackPending || (s_steamOverlayJoinCallback != null && s_steamRichPresenceJoinCallback != null))
                return;
            if (s_steamOverlayCallbackRetryCount >= SteamOverlayCallbackMaxRetries)
            {
                s_steamOverlayCallbackPending = false;
                Instance?.Logger.Warning("[NetMod] Steam overlay join callback registration gave up after {Count} retries", SteamOverlayCallbackMaxRetries);
                return;
            }
            s_steamOverlayCallbackRetryCount++;
            var shouldLogFailure = s_steamOverlayCallbackRetryCount == 1 || s_steamOverlayCallbackRetryCount % 60 == 0;
            if (!TryEnsureSteamApiInitialized($"callback registration attempt {s_steamOverlayCallbackRetryCount}", shouldLogFailure))
            {
                if (shouldLogFailure)
                    Instance?.Logger.Debug("[NetMod] Steam overlay: SteamAPI.Init()=false (attempt {Attempt}). Trying callback without Init (game may have Steam).", s_steamOverlayCallbackRetryCount);
                s_steamOverlayJoinCallback = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
                s_steamRichPresenceJoinCallback = Callback<GameRichPresenceJoinRequested_t>.Create(OnGameRichPresenceJoinRequested);
                s_steamOverlayCallbackPending = false;
                StartSteamCallbackPumpTimer();
                Instance?.Logger.Information("[NetMod] Steam overlay join callbacks registered (game had Steam initialized)");
                return;
            }
            s_steamOverlayJoinCallback = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
            s_steamRichPresenceJoinCallback = Callback<GameRichPresenceJoinRequested_t>.Create(OnGameRichPresenceJoinRequested);
            s_steamOverlayCallbackPending = false;
            StartSteamCallbackPumpTimer();
            Instance?.Logger.Information("[NetMod] Steam overlay join callbacks registered (attempt {Attempt})", s_steamOverlayCallbackRetryCount);
        }

        private static void WriteOverlayJoinDiagnostic(string callbackType, string data)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dccm_overlay_join_fired.txt");
            System.IO.File.WriteAllText(path, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z | {callbackType} | {data}");
        }

        private static void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t data)
        {
            WriteOverlayJoinDiagnostic("GameLobbyJoinRequested_t", data.m_steamIDLobby.m_SteamID.ToString());
            Instance?.Logger.Information("[NetMod][Steam] GameLobbyJoinRequested_t callback fired");
            var lobbyId = data.m_steamIDLobby.m_SteamID;
            if (lobbyId == 0UL)
                return;
            Instance?.Logger.Information("[NetMod][Steam] Overlay lobby join requested lobbyId={LobbyId}", lobbyId);
            EnqueueAndProcessOverlayJoin(lobbyId, "GameLobbyJoinRequested_t");
        }

        private static void OnGameRichPresenceJoinRequested(GameRichPresenceJoinRequested_t data)
        {
            var connect = data.m_rgchConnect ?? string.Empty;
            WriteOverlayJoinDiagnostic("GameRichPresenceJoinRequested_t", connect);
            Instance?.Logger.Information("[NetMod][Steam] GameRichPresenceJoinRequested_t callback fired");
            if (string.IsNullOrWhiteSpace(connect))
            {
                Instance?.Logger.Information("[NetMod][Steam] Rich Presence join requested but connect string is empty (host may not have set Rich Presence)");
                return;
            }
            Instance?.Logger.Information("[NetMod][Steam] Overlay Rich Presence join requested connect={Connect}", connect);
            var lobbyId = TryParseLobbyIdFromConnectString(connect);
            if (lobbyId == 0UL)
            {
                Instance?.Logger.Warning("[NetMod][Steam] Could not parse lobby ID from connect string: {Connect}", connect);
                return;
            }
            EnqueueAndProcessOverlayJoin(lobbyId, "GameRichPresenceJoinRequested_t");
        }

        private static void EnqueueAndProcessOverlayJoin(ulong lobbyId, string source)
        {
            var nowTicks = Environment.TickCount64;
            if (lobbyId == s_lastOverlayJoinLobbyId &&
                nowTicks - s_lastOverlayJoinTicks < SteamOverlayJoinDedupMs)
            {
                Instance?.Logger.Debug("[NetMod][Steam] Ignoring duplicate overlay join request lobbyId={LobbyId} source={Source}", lobbyId, source);
                return;
            }

            s_lastOverlayJoinLobbyId = lobbyId;
            s_lastOverlayJoinTicks = nowTicks;
            Instance?.Logger.Information("[NetMod][Steam] Queueing overlay join request lobbyId={LobbyId} source={Source}", lobbyId, source);
            GameMenu.EnqueueMainThreadCoalesced("steam:overlay-join", () => GameMenu.HandleSteamOverlayJoinRequest(lobbyId));
        }

        private static ulong TryParseLobbyIdFromConnectString(string connect)
        {
            if (string.IsNullOrWhiteSpace(connect))
                return 0UL;
            var parts = connect.Split((char[]?)[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (string.Equals(parts[i], "+connect_lobby", StringComparison.OrdinalIgnoreCase) &&
                    ulong.TryParse(parts[i + 1], out var lobbyId) && lobbyId > 0)
                    return lobbyId;
            }
            if (ulong.TryParse(connect.Trim(), out var direct) && direct > 0)
                return direct;
            return 0UL;
        }

        private static void TryRunSteamCallbacks()
        {
            SteamAPI.RunCallbacks();
        }

        /// <summary>
        /// Call from GameMenu when at main menu so Steam overlay join callbacks are pumped even if frame update is throttled.
        /// </summary>
        internal static void PumpSteamCallbacksForOverlay()
        {
            var callbacksStart = RuntimeHitchWatch.Start();
            TryRunSteamCallbacks();
            var callbacksMs = RuntimeHitchWatch.GetElapsedMilliseconds(callbacksStart);
            if (callbacksMs >= RuntimeHitchWatch.InteractionSlowThresholdMs)
            {
                RuntimeHitchWatch.LogSlow(
                    Instance?.Logger,
                    "ModEntry.TryRunSteamCallbacks",
                    callbacksMs,
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"steamReady={(s_steamApiReady ? 1 : 0)} pendingOverlay={(s_steamOverlayCallbackPending ? 1 : 0)}"));
            }

            var auxStart = RuntimeHitchWatch.Start();
            TryDeferredSteamOverlayCallbackRegistration();
            TryPollSteamOverlayJoinFromLaunchData();
            var auxMs = RuntimeHitchWatch.GetElapsedMilliseconds(auxStart);
            if (auxMs >= RuntimeHitchWatch.InteractionSlowThresholdMs)
            {
                RuntimeHitchWatch.LogSlow(
                    Instance?.Logger,
                    "ModEntry.PumpSteamCallbacksForOverlay",
                    auxMs,
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"steamReady={(s_steamApiReady ? 1 : 0)} pendingOverlay={(s_steamOverlayCallbackPending ? 1 : 0)}"));
            }
        }

        private static bool TryEnsureSteamApiInitialized(string source, bool logFailure)
        {
            if (s_steamApiReady)
                return true;

            SteamConnect.PrepareSteamNativePathForRuntime();
            if (SteamAPI.Init())
            {
                s_steamApiReady = true;
                Instance?.Logger.Information("[NetMod][Steam] SteamAPI.Init succeeded ({Source})", source);
                return true;
            }

            if (logFailure)
                Instance?.Logger.Debug("[NetMod][Steam] SteamAPI.Init returned false ({Source})", source);

            return false;
        }

        private static void TryPollSteamOverlayJoinFromLaunchData()
        {
            if (!s_steamApiReady)
                return;

            var nowTicks = Environment.TickCount64;
            if (nowTicks < s_nextSteamLaunchPollTicks)
                return;

            s_nextSteamLaunchPollTicks = nowTicks + SteamOverlayLaunchPollIntervalMs;

            string steamLaunchCommand = string.Empty;
            var launchCommandLength = SteamApps.GetLaunchCommandLine(out steamLaunchCommand, 2048);
            steamLaunchCommand = (steamLaunchCommand ?? string.Empty).Trim();
            if (launchCommandLength > 0 &&
                !string.IsNullOrWhiteSpace(steamLaunchCommand) &&
                !string.Equals(steamLaunchCommand, s_lastSteamLaunchCommand, StringComparison.Ordinal))
            {
                s_lastSteamLaunchCommand = steamLaunchCommand;
                var lobbyId = TryParseLobbyIdFromConnectString(steamLaunchCommand);
                if (lobbyId > 0UL)
                {
                    Instance?.Logger.Information("[NetMod][Steam] Detected overlay join from Steam launch command: {Command}", steamLaunchCommand);
                    EnqueueAndProcessOverlayJoin(lobbyId, "SteamApps.GetLaunchCommandLine");
                    return;
                }
            }

            var connectLobby = (SteamApps.GetLaunchQueryParam("connect_lobby") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(connectLobby) ||
                string.Equals(connectLobby, s_lastSteamLaunchConnectLobbyParam, StringComparison.Ordinal))
                return;

            s_lastSteamLaunchConnectLobbyParam = connectLobby;
            if (ulong.TryParse(connectLobby, out var lobbyId2) && lobbyId2 > 0UL)
            {
                Instance?.Logger.Information("[NetMod][Steam] Detected overlay join from Steam launch query param connect_lobby={LobbyId}", lobbyId2);
                EnqueueAndProcessOverlayJoin(lobbyId2, "SteamApps.GetLaunchQueryParam");
            }
        }

        /// <summary>
        /// Background timer pumps Steam callbacks so overlay Join works when game loop is paused (overlay open).
        /// Callbacks run on timer thread; we EnqueueMainThread for game ops.
        /// </summary>
        private static void StartSteamCallbackPumpTimer()
        {
            if (s_steamCallbackPumpTimer != null)
                return;
            s_steamCallbackPumpTimer = new Timer(
                _ => SteamAPI.RunCallbacks(),
                null,
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(100));
            Instance?.Logger.Debug("[NetMod] Steam callback pump timer started");
        }
    }
}
