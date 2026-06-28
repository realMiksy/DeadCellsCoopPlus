using DeadCellsMultiplayerMod;

public sealed partial class NetNode
{
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        List<ClientConnection> clients;
        List<SteamClientConnection> steamClients;
        lock (_clientsLock)
        {
            clients = new List<ClientConnection>(_clients.Count);
            foreach (var c in _clients.Values)
                clients.Add(c);
            steamClients = new List<SteamClientConnection>(_steamClients.Count);
            foreach (var c in _steamClients.Values)
                steamClients.Add(c);
            _clients.Clear();
            _steamClients.Clear();
            _steamClientIdsBySteam.Clear();
            _connectedClientCount = 0;
        }
        foreach (var client in clients)
        {
            try { client.Dispose(); } catch { }
            if (client.AssignedId >= 2)
            {
                lock (UsedClientIds)
                {
                    UsedClientIds.Remove(client.AssignedId);
                }
            }
        }
        foreach (var steamClient in steamClients)
        {
            _steamBridge?.TryClosePeer(steamClient.SteamId.m_SteamID);
            try { steamClient.Dispose(); } catch { }
            if (steamClient.AssignedId >= 2)
            {
                lock (UsedClientIds)
                {
                    UsedClientIds.Remove(steamClient.AssignedId);
                }
            }
        }

        if (_useSteamTransport && _steamHostId.m_SteamID != 0UL)
        {
            _steamBridge?.TryClosePeer(_steamHostId.m_SteamID);
        }

        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _steamTransportTask?.Wait(400); } catch { }
        if (_useSteamTransport && _role == NetRole.Host)
        {
            try { TryClearSteamRichPresence(); } catch { }
        }
        GameDataSync.Seed = 0;
        lock (_hostCacheSync)
        {
            _cachedHostSeed = null;
            _cachedHostBossRune = null;
            _cachedHostSerializerSeq = null;
            _cachedHostSerializerUid = null;
            _cachedHostLevelDescPayload = null;
            _cachedHostLevelSeedPayload = null;
            _cachedHostHeroSkin = null;
            _cachedHostHeroHeadSkin = null;
            _cachedHostLevelGraphPayload = null;
            _cachedHostMobsHpMult = null;
            _cachedHostBossesHpMult = null;
            _cachedHostLevelGraphsByLevelId.Clear();
        }
        lock (_sync)
        {
            _remotes.Clear();
            _primaryRemoteId = 0;
            _hasRemote = false;
            _connectedClientCount = 0;
            _pendingAttacks.Clear();
            _pendingMobStates.Clear();
            _pendingMobMoves.Clear();
            _pendingMobCharges.Clear();
            _pendingMobHits.Clear();
            _pendingMobDies.Clear();
            _pendingMobAttacks.Clear();
            _pendingMobDraws.Clear();
            _pendingExitReadyStates.Clear();
            _pendingBossCineLevelIds.Clear();
            _pendingBossHeroTeleports.Clear();
            _pendingPlayerDownStates.Clear();
            _pendingPlayerReviveRequests.Clear();
            _pendingInterDoorEvents.Clear();
            _pendingInterElevatorEvents.Clear();
            _pendingInterPressurePlateEvents.Clear();
            _pendingInterTreasureChestEvents.Clear();
            _pendingInterVineLadderEvents.Clear();
            _pendingInterTeleportEvents.Clear();
            _pendingInterBreakableGroundEvents.Clear();
            _pendingBossRuneUpdateCells.Clear();
            _pendingInterPortalEvents.Clear();
            _pendingInterGenericActivateEvents.Clear();
            _pendingWorldObjectStates.Clear();
        }
        _stream = null; _client = null; _listener = null;
        try { _sendLock.Dispose(); } catch { }

        try
        {
            _steamBridge?.Dispose();
            _steamBridge = null;
        }
        catch { }

    }
}
