using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Globalization;
using DeadCellsMultiplayerMod;
using Serilog;
using Serilog.Core;
using Steamworks;

public enum NetRole { None, Host, Client }

public sealed class NetNode : IDisposable
{
    private readonly ILogger _log;
    private readonly NetRole _role;

    private sealed class ClientConnection : IDisposable
    {
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public int AssignedId { get; }
        public EndPoint? RemoteEndPoint => Client.Client?.RemoteEndPoint;

        public ClientConnection(TcpClient client, int assignedId)
        {
            Client = client;
            Stream = client.GetStream();
            AssignedId = assignedId;
        }

        public void Dispose()
        {
            try { Stream.Close(); } catch { }
            try { Client.Close(); } catch { }
            try { SendLock.Dispose(); } catch { }
        }
    }

    private sealed class SteamClientConnection : IDisposable
    {
        public CSteamID SteamId { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public int AssignedId { get; }

        public SteamClientConnection(CSteamID steamId, int assignedId)
        {
            SteamId = steamId;
            AssignedId = assignedId;
        }

        public void Dispose()
        {
            try { SendLock.Dispose(); } catch { }
        }
    }

    private sealed class RemoteState
    {
        public int Id { get; }
        public double X;
        public double Y;
        public int Dir = 1;
        public bool HasRemote;
        public string? LevelId;
        public string? RoomLevelId;
        public int? RoomId;
        public bool HasRoom;
        public string? Anim;
        public int? AnimQueue;
        public bool? AnimG;
        public bool HasAnim;
        public int Life;
        public int MaxLife;
        public int Lif;
        public int BonusLife;
        public int Recover;
        public string? Username;
        public string? Skin;
        public string? Head;

        public string HeadAnim;
        public bool HasHeadAnim;

        public string? WeaponKind;
        public int WeaponSlot;
        public int WeaponPermanentId;
        public int WeaponAmmo = int.MinValue;
        public bool HasWeaponUpdate;

        public RemoteState(int id)
        {
            Id = id;
        }
    }

    public readonly struct RemoteSnapshot
    {
        public readonly int Id;
        public readonly double X;
        public readonly double Y;
        public readonly int Dir;
        public readonly string? LevelId;
        public readonly string? RoomLevelId;
        public readonly int? RoomId;
        public readonly bool HasRoom;
        public readonly string? Anim;
        public readonly int? AnimQueue;
        public readonly bool? AnimG;
        public readonly bool HasAnim;
        public readonly string? Username;
        public readonly string? HeadAnim;
        public readonly bool HasHeadAnim;

        public RemoteSnapshot(
            int id,
            double x,
            double y,
            int dir,
            string? levelId,
            string? roomLevelId,
            int? roomId,
            bool hasRoom,
            string? anim,
            int? animQueue,
            bool? animG,
            bool hasAnim,
            string? username,
            string? headAnim,
            bool hasHeadAnim)
        {
            Id = id;
            X = x;
            Y = y;
            Dir = dir;
            LevelId = levelId;
            RoomLevelId = roomLevelId;
            RoomId = roomId;
            HasRoom = hasRoom;
            Anim = anim;
            AnimQueue = animQueue;
            AnimG = animG;
            HasAnim = hasAnim;
            Username = username;
            HeadAnim = headAnim;
            HasHeadAnim = hasHeadAnim;
        }
    }

    public readonly struct RemoteWeaponSnapshot
    {
        public readonly int Id;
        public readonly string? Kind;
        public readonly int Slot;
        public readonly int PermanentId;
        public readonly int? Ammo;

        public RemoteWeaponSnapshot(int id, string? kind, int slot, int permanentId, int? ammo)
        {
            Id = id;
            Kind = kind;
            Slot = slot;
            PermanentId = permanentId;
            Ammo = ammo;
        }
    }

    public readonly struct RemoteAttack
    {
        public readonly int Id;
        public readonly string? Kind;
        public readonly int Slot;
        public readonly int PermanentId;
        public readonly int? Ammo;

        public RemoteAttack(int id, string? kind, int slot, int permanentId, int? ammo)
        {
            Id = id;
            Kind = kind;
            Slot = slot;
            PermanentId = permanentId;
            Ammo = ammo;
        }
    }

    public readonly struct RemoteHpSnapshot
    {
        public readonly int Id;
        public readonly int Life;
        public readonly int MaxLife;
        public readonly int Lif;
        public readonly int BonusLife;
        public readonly int Recover;
        public readonly string? Username;

        public RemoteHpSnapshot(int id, int life, int maxLife, int lif, int bonusLife, int recover, string? username)
        {
            Id = id;
            Life = life;
            MaxLife = maxLife;
            Lif = lif;
            BonusLife = bonusLife;
            Recover = recover;
            Username = username;
        }
    }

    public readonly struct RemoteUserSnapshot
    {
        public readonly int Id;
        public readonly string? Username;

        public RemoteUserSnapshot(int id, string? username)
        {
            Id = id;
            Username = username;
        }
    }

    public readonly struct RemoteChatMessage
    {
        public readonly int Id;
        public readonly string? Username;
        public readonly string Message;

        public RemoteChatMessage(int id, string? username, string message)
        {
            Id = id;
            Username = username;
            Message = message ?? string.Empty;
        }
    }

    public readonly struct MobStateSnapshot
    {
        public readonly int Index;
        public readonly double X;
        public readonly double Y;
        public readonly int Dir;
        public readonly int Life;
        public readonly int MaxLife;
        public readonly string AnimPayload;
        public readonly string Type;
        public readonly string StatePayload;

        public MobStateSnapshot(int index, double x, double y, int dir, int life, int maxLife, string animPayload, string type, string statePayload = "")
        {
            Index = index;
            X = x;
            Y = y;
            Dir = dir;
            Life = life;
            MaxLife = maxLife;
            AnimPayload = animPayload ?? string.Empty;
            Type = type ?? string.Empty;
            StatePayload = statePayload ?? string.Empty;
        }
    }

    public readonly struct MobHit
    {
        public readonly int UserId;
        public readonly int MobIndex;
        public readonly int Hp;
        public readonly double X;
        public readonly double Y;

        public MobHit(int userId, int mobIndex, int hp, double x, double y)
        {
            UserId = userId;
            MobIndex = mobIndex;
            Hp = hp;
            X = x;
            Y = y;
        }
    }

    public readonly struct MobDie
    {
        public readonly int UserId;
        public readonly int MobIndex;
        public readonly double X;
        public readonly double Y;

        public MobDie(int userId, int mobIndex, double x, double y)
        {
            UserId = userId;
            MobIndex = mobIndex;
            X = x;
            Y = y;
        }
    }

    public readonly struct MobAttack
    {
        public readonly int Index;
        public readonly string SkillId;
        public readonly bool RequiresTargetInArea;
        public readonly int? Data;
        public readonly double X;
        public readonly double Y;
        public readonly int TargetUserId;
        public readonly int Dir;

        public MobAttack(int index, string skillId, bool requiresTargetInArea, int? data, double x, double y, int targetUserId, int dir = 0)
        {
            Index = index;
            SkillId = skillId ?? string.Empty;
            RequiresTargetInArea = requiresTargetInArea;
            Data = data;
            X = x;
            Y = y;
            TargetUserId = targetUserId;
            Dir = dir;
        }
    }

    public readonly struct MobDraw
    {
        public readonly int UserId;
        public readonly int MobIndex;
        public readonly bool IsOutOfGame;
        public readonly bool IsOnScreen;

        public MobDraw(int userId, int mobIndex, bool isOutOfGame, bool isOnScreen)
        {
            UserId = userId;
            MobIndex = mobIndex;
            IsOutOfGame = isOutOfGame;
            IsOnScreen = isOnScreen;
        }
    }

    public readonly struct ExitReadyState
    {
        public readonly int UserId;
        public readonly int DoorCx;
        public readonly int DoorCy;
        public readonly bool Pressed;
        public readonly bool InsideCircle;
        public readonly bool IsOutOfGame;
        public readonly bool IsOnScreen;

        public ExitReadyState(int userId, int doorCx, int doorCy, bool pressed, bool insideCircle, bool isOutOfGame, bool isOnScreen)
        {
            UserId = userId;
            DoorCx = doorCx;
            DoorCy = doorCy;
            Pressed = pressed;
            InsideCircle = insideCircle;
            IsOutOfGame = isOutOfGame;
            IsOnScreen = isOnScreen;
        }
    }

    public readonly struct PlayerDownState
    {
        public readonly int UserId;
        public readonly bool IsDowned;
        public readonly double X;
        public readonly double Y;
        public readonly string LevelId;
        public readonly bool HasHeadPosition;
        public readonly double HeadX;
        public readonly double HeadY;
        public readonly bool HasHeadAnim;
        public readonly string? HeadAnim;

        public PlayerDownState(int userId, bool isDowned, double x, double y, string levelId, bool hasHeadPosition = false, double headX = 0, double headY = 0, bool hasHeadAnim = false, string? headAnim = null)
        {
            UserId = userId;
            IsDowned = isDowned;
            X = x;
            Y = y;
            LevelId = levelId ?? string.Empty;
            HasHeadPosition = hasHeadPosition;
            HeadX = headX;
            HeadY = headY;
            HasHeadAnim = hasHeadAnim;
            HeadAnim = hasHeadAnim ? (headAnim ?? string.Empty) : null;
        }
    }

    public readonly struct PlayerReviveRequest
    {
        public readonly int ReviverId;
        public readonly int TargetId;

        public PlayerReviveRequest(int reviverId, int targetId)
        {
            ReviverId = reviverId;
            TargetId = targetId;
        }
    }

    private TcpListener? _listener;   // host
    private TcpClient? _client;     // client
    private NetworkStream? _stream;
    private Task? _steamTransportTask;
    private readonly bool _useSteamTransport;
    private readonly CSteamID _steamHostId;
    private const int SteamP2PChannel = 0;
    private static int _connectedClientCount;

    private int ID;

    public int id => ID;

    private static readonly int[] ClientIds = { 2, 3, 4 };
    public static int MaxClientSlots => ClientIds.Length;
    public static int ConnectedClientCount => _connectedClientCount;

    private static readonly HashSet<int> UsedClientIds = new();

    private readonly object _clientsLock = new();
    private readonly Dictionary<int, ClientConnection> _clients = new();
    private readonly Dictionary<int, SteamClientConnection> _steamClients = new();
    private readonly Dictionary<ulong, int> _steamClientIdsBySteam = new();
    private readonly Dictionary<int, RemoteState> _remotes = new();
    private readonly List<RemoteAttack> _pendingAttacks = new();
    private readonly List<RemoteChatMessage> _pendingChatMessages = new();
    private List<MobStateSnapshot> _pendingMobStates = new();
    private readonly List<MobHit> _pendingMobHits = new();
    private readonly List<MobDie> _pendingMobDies = new();
    private readonly List<MobAttack> _pendingMobAttacks = new();
    private readonly List<MobDraw> _pendingMobDraws = new();
    private readonly List<ExitReadyState> _pendingExitReadyStates = new();
    private readonly List<PlayerDownState> _pendingPlayerDownStates = new();
    private readonly List<PlayerReviveRequest> _pendingPlayerReviveRequests = new();
    private int _primaryRemoteId;

    private readonly IPEndPoint _bindEp;   // host bind
    private readonly IPEndPoint _destEp;   // client connect

    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _recvTask;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    private readonly object _sync = new();
    private bool _hasRemote;
    private bool _hasLocalHpSnapshot;
    private int _localHpLife;
    private int _localHpMaxLife;
    private int _localHpLif;
    private int _localHpBonusLife;
    private int _localHpRecover;
    private readonly object _hostCacheSync = new();
    private int? _cachedHostSeed;
    private int? _cachedHostBossRune;
    private int? _cachedHostSerializerSeq;
    private int? _cachedHostSerializerUid;
    private string? _cachedHostCountersPayload;
    private string? _cachedHostBlueprintsPayload;
    private string? _cachedHostLevelGraphPayload;

    public bool HasRemote
    {
        get
        {
            if (_role == NetRole.Host)
            {
                lock (_clientsLock)
                {
                    if (_useSteamTransport)
                        return _steamClients.Count > 0;
                    return _clients.Count > 0;
                }
            }
            lock (_sync) return _hasRemote;
        }
    }
    public bool IsAlive =>
        _useSteamTransport
            ? _cts != null && !_cts.IsCancellationRequested
            : (_role == NetRole.Host && _listener != null) ||
              (_role == NetRole.Client && _client != null);
    public bool IsHost => _role == NetRole.Host;

    public IPEndPoint? ListenerEndpoint =>
        _useSteamTransport
            ? null
            : _listener != null ? (IPEndPoint?)_listener.LocalEndpoint : null;

    public static NetNode CreateHost(ILogger log, IPEndPoint ep)  => new(log, NetRole.Host,  ep);
    public static NetNode CreateClient(ILogger log, IPEndPoint ep)=> new(log, NetRole.Client, ep);
    public static NetNode CreateSteamHost(ILogger log) => new(log, NetRole.Host, new CSteamID(0));
    public static NetNode CreateSteamClient(ILogger log, ulong hostSteamId) => new(log, NetRole.Client, new CSteamID(hostSteamId));

    private NetNode(ILogger log, NetRole role, IPEndPoint ep)
    {
        _log  = log;
        _role = role;
        _useSteamTransport = false;
        _steamHostId = new CSteamID(0);

        if (role == NetRole.Host)
        {
            _bindEp = ep;
            _destEp = new IPEndPoint(IPAddress.None, 0);
            StartHost();
            ID = 1;
        }
        else
        {
            _destEp = ep;
            _bindEp = new IPEndPoint(IPAddress.None, 0);
            StartClient();
            ID = 0;
        }
    }

    private NetNode(ILogger log, NetRole role, CSteamID hostSteamId)
    {
        _log = log;
        _role = role;
        _useSteamTransport = true;
        _steamHostId = hostSteamId;
        _bindEp = new IPEndPoint(IPAddress.None, 0);
        _destEp = new IPEndPoint(IPAddress.None, 0);

        if (role == NetRole.Host)
        {
            ID = 1;
            StartSteamHost();
        }
        else
        {
            ID = 0;
            StartSteamClient();
        }
    }

    // ================= HOST =================
    private void StartHost()
    {
        // try
        // {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(_bindEp);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();

            var lep = (IPEndPoint)_listener.LocalEndpoint;

            _log.Information("[NetNode] Host started OK. Bound to {0}:{1}", lep.Address, lep.Port);

            _acceptTask = Task.Run(() => AcceptLoop(_cts.Token));
        // }
        // catch (Exception ex)
        // {
        //     _log.Error("[NetNode] Host start failed: {msg}", ex.Message);
        //     Dispose();
        //     throw;
        // }
    }

    private void StartSteamHost()
    {
        _cts = new CancellationTokenSource();
        _log.Information("[NetNode] Host started with Steam P2P transport");
        _steamTransportTask = Task.Run(() => SteamHostLoop(_cts.Token));
    }

    private void StartSteamClient()
    {
        _cts = new CancellationTokenSource();
        _steamTransportTask = Task.Run(() => SteamClientLoop(_cts.Token));
        _ = Task.Run(() => ConnectWithRetrySteamAsync(_cts.Token));
    }

    private async Task ConnectWithRetrySteamAsync(CancellationToken ct)
    {
        var maxAttempts = GameMenu.ClientConnectMaxAttempts;
        var attempt = 0;

        if (_steamHostId.m_SteamID == 0UL)
        {
            _log.Warning("[NetNode] Steam client host id is missing");
            GameMenu.EnqueueMainThread(GameMenu.NotifyClientConnectFailed);
            return;
        }

        while (!ct.IsCancellationRequested && attempt < maxAttempts)
        {
            attempt++;
            GameMenu.EnqueueMainThread(() => GameMenu.NotifyClientConnectAttempt(attempt));
            _log.Information("[NetNode] Steam client connecting to hostSteamId={HostSteamId}", _steamHostId.m_SteamID);

            await SendLineToSteamPeerSafe(_steamHostId, _sendLock, "HELLO\n").ConfigureAwait(false);

            var connected = false;
            var startedAt = DateTime.UtcNow;
            while (!ct.IsCancellationRequested && DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(4))
            {
                lock (_sync)
                {
                    connected = _hasRemote;
                }
                if (connected)
                    break;

                await Task.Delay(150, ct).ConfigureAwait(false);
            }

            if (connected)
            {
                if (ID <= 0)
                {
                    _log.Warning("[NetNode] Steam client connected without assigned ID yet; requesting handshake refresh");
                    await SendLineToSteamPeerSafe(_steamHostId, _sendLock, "HELLO\n").ConfigureAwait(false);
                }

                GameMenu.EnqueueMainThread(() =>
                {
                    GameMenu.NetRef = this;
                    GameMenu.SetRole(_role);
                    GameMenu.NotifyRemoteConnected(_role);
                });
                return;
            }

            if (attempt >= maxAttempts)
            {
                GameMenu.EnqueueMainThread(GameMenu.NotifyClientConnectFailed);
                break;
            }

            await Task.Delay(1500, ct).ConfigureAwait(false);
        }
    }

    private async Task SteamHostLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var hasPacket = false;
            try
            {
                while (SteamNetworking.IsP2PPacketAvailable(out uint packetSize, SteamP2PChannel))
                {
                    hasPacket = true;
                    if (packetSize == 0 || packetSize > 1024 * 1024)
                    {
                        break;
                    }

                    var buffer = new byte[packetSize];
                    if (!SteamNetworking.ReadP2PPacket(buffer, packetSize, out uint bytesRead, out CSteamID remoteSteamId, SteamP2PChannel))
                        break;

                    if (bytesRead == 0)
                        continue;

                    try { SteamNetworking.AcceptP2PSessionWithUser(remoteSteamId); } catch { }

                    if (!TryGetOrRegisterSteamClient(remoteSteamId, out var connection) || connection == null)
                        continue;

                    var payload = Encoding.UTF8.GetString(buffer, 0, (int)bytesRead);
                    ProcessIncomingSteamPayload(payload, connection.AssignedId, connection);
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[NetNode] Steam host loop error: {msg}", ex.Message);
            }

            TryRunSteamCallbacks();
            if (!hasPacket)
                await Task.Delay(8, ct).ConfigureAwait(false);
        }
    }

    private async Task SteamClientLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var hasPacket = false;
            try
            {
                while (SteamNetworking.IsP2PPacketAvailable(out uint packetSize, SteamP2PChannel))
                {
                    hasPacket = true;
                    if (packetSize == 0 || packetSize > 1024 * 1024)
                        break;

                    var buffer = new byte[packetSize];
                    if (!SteamNetworking.ReadP2PPacket(buffer, packetSize, out uint bytesRead, out CSteamID remoteSteamId, SteamP2PChannel))
                        break;

                    if (bytesRead == 0)
                        continue;
                    if (_steamHostId.m_SteamID != 0UL && remoteSteamId.m_SteamID != _steamHostId.m_SteamID)
                        continue;

                    try { SteamNetworking.AcceptP2PSessionWithUser(remoteSteamId); } catch { }
                    var payload = Encoding.UTF8.GetString(buffer, 0, (int)bytesRead);
                    ProcessIncomingSteamPayload(payload, 1, null);
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[NetNode] Steam client loop error: {msg}", ex.Message);
            }

            TryRunSteamCallbacks();
            if (!hasPacket)
                await Task.Delay(8, ct).ConfigureAwait(false);
        }
    }

    private void TryRunSteamCallbacks()
    {
        try
        {
            SteamAPI.RunCallbacks();
        }
        catch
        {
        }
    }

    private bool TryGetOrRegisterSteamClient(CSteamID remoteSteamId, out SteamClientConnection? connection)
    {
        connection = null;
        var steamKey = remoteSteamId.m_SteamID;

        int existingId;
        lock (_clientsLock)
        {
            if (_steamClientIdsBySteam.TryGetValue(steamKey, out existingId) &&
                _steamClients.TryGetValue(existingId, out var existingConnection))
            {
                connection = existingConnection;
                return true;
            }
        }

        int assignedId;
        lock (UsedClientIds)
        {
            assignedId = ClientIds.FirstOrDefault(id => !UsedClientIds.Contains(id));
            if (assignedId == 0)
            {
                _log.Warning("[NetNode] Max players reached, ignoring Steam client {SteamId}", steamKey);
                return false;
            }
            UsedClientIds.Add(assignedId);
        }

        var newConnection = new SteamClientConnection(remoteSteamId, assignedId);
        lock (_clientsLock)
        {
            _steamClients[assignedId] = newConnection;
            _steamClientIdsBySteam[steamKey] = assignedId;
            _connectedClientCount = _steamClients.Count;
        }
        lock (_sync)
        {
            if (_primaryRemoteId == 0)
                _primaryRemoteId = assignedId;
            _hasRemote = true;
        }

        connection = newConnection;
        _ = Task.Run(() => SendInitialStateToSteamClient(newConnection));
        GameMenu.EnqueueMainThread(() =>
        {
            GameMenu.NetRef = this;
            GameMenu.SetRole(_role);
            GameMenu.NotifyRemoteConnected(_role);
        });
        return true;
    }

    private async Task SendInitialStateToSteamClient(SteamClientConnection connection)
    {
        await SendLineToSteamClientSafe(connection, "WELCOME\n").ConfigureAwait(false);

        int? cachedBossRune;
        int? cachedSeed;
        int? cachedSerializerSeq;
        int? cachedSerializerUid;
        string? cachedCountersPayload;
        string? cachedBlueprintsPayload;
        string? cachedLevelGraphPayload;
        lock (_hostCacheSync)
        {
            cachedBossRune = _cachedHostBossRune;
            cachedSeed = _cachedHostSeed;
            cachedSerializerSeq = _cachedHostSerializerSeq;
            cachedSerializerUid = _cachedHostSerializerUid;
            cachedCountersPayload = _cachedHostCountersPayload;
            cachedBlueprintsPayload = _cachedHostBlueprintsPayload;
            cachedLevelGraphPayload = _cachedHostLevelGraphPayload;
        }

        if (cachedSerializerSeq.HasValue && cachedSerializerUid.HasValue)
            await SendLineToSteamClientSafe(connection, $"HXSYNC|{cachedSerializerSeq.Value}|{cachedSerializerUid.Value}\n").ConfigureAwait(false);
        if (cachedBossRune.HasValue)
            await SendLineToSteamClientSafe(connection, $"BOSSRUNE|{cachedBossRune.Value}\n").ConfigureAwait(false);
        if (cachedSeed.HasValue)
            await SendLineToSteamClientSafe(connection, $"SEED|{cachedSeed.Value}\n").ConfigureAwait(false);
        if (cachedCountersPayload != null)
            await SendLineToSteamClientSafe(connection, $"COUNTERS|{cachedCountersPayload}\n").ConfigureAwait(false);
        if (cachedBlueprintsPayload != null)
            await SendLineToSteamClientSafe(connection, $"BLUEPRINTS|{cachedBlueprintsPayload}\n").ConfigureAwait(false);
        if (cachedLevelGraphPayload != null)
            await SendLineToSteamClientSafe(connection, $"LGRAPH|{cachedLevelGraphPayload}\n").ConfigureAwait(false);

        await SendLineToSteamClientSafe(connection, $"ID|{connection.AssignedId}\n").ConfigureAwait(false);
        await SendKnownUsersToSteamClientSafe(connection).ConfigureAwait(false);
        if (_role == NetRole.Host && TryBuildLocalHpLine(out var localHpLine))
            await SendLineToSteamClientSafe(connection, localHpLine).ConfigureAwait(false);
    }

    private void ProcessIncomingSteamPayload(string payload, int senderId, SteamClientConnection? senderConnection)
    {
        if (string.IsNullOrEmpty(payload))
            return;

        var lines = payload.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;

            var lineCopy = line;
            GameMenu.EnqueueMainThread(() =>
            {
                try
                {
                    if (!HandleLine(lineCopy, senderId, out var forwardLine))
                    {
                        if (_role == NetRole.Host && senderConnection != null)
                            CleanupHostSteamClient(senderConnection);
                        else
                            CleanupClient();
                        return;
                    }

                    if (_role == NetRole.Host && senderConnection != null && forwardLine != null)
                        ForwardLineToOtherSteamClients(senderConnection, forwardLine);
                }
                catch (Exception ex)
                {
                    _log.Warning("[NetNode] Steam HandleLine(main-thread) failed: {msg}", ex.Message);
                }
            });
        }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                var tcp = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    try { tcp.Close(); } catch { }
                    break;
                }

                int assignedId;
                lock (UsedClientIds)
                {
                    assignedId = ClientIds.FirstOrDefault(id => !UsedClientIds.Contains(id));
                    if (assignedId == 0)
                    {
                        _log.Warning("[NetNode] Max players reached, kicking client");
                        try { tcp.Close(); } catch { }
                        continue;
                    }
                    UsedClientIds.Add(assignedId);
                }
                tcp.NoDelay = true;
                var connection = new ClientConnection(tcp, assignedId);
                lock (_clientsLock)
                {
                    _clients[assignedId] = connection;
                    _connectedClientCount = _clients.Count;
                }
                lock (_sync)
                {
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = assignedId;
                    _hasRemote = true;
                }

                _log.Information("[NetNode] Host accepted {ep}", connection.RemoteEndPoint);

                await SendLineToClientSafe(connection, "WELCOME\n").ConfigureAwait(false);
                if (_role == NetRole.Host)
                {
                    int? cachedBossRune;
                    int? cachedSeed;
                    int? cachedSerializerSeq;
                    int? cachedSerializerUid;
                    string? cachedCountersPayload;
                    string? cachedBlueprintsPayload;
                    string? cachedLevelGraphPayload;
                    lock (_hostCacheSync)
                    {
                        cachedBossRune = _cachedHostBossRune;
                        cachedSeed = _cachedHostSeed;
                        cachedSerializerSeq = _cachedHostSerializerSeq;
                        cachedSerializerUid = _cachedHostSerializerUid;
                        cachedCountersPayload = _cachedHostCountersPayload;
                        cachedBlueprintsPayload = _cachedHostBlueprintsPayload;
                        cachedLevelGraphPayload = _cachedHostLevelGraphPayload;
                    }

                    if (cachedSerializerSeq.HasValue && cachedSerializerUid.HasValue)
                        await SendLineToClientSafe(connection, $"HXSYNC|{cachedSerializerSeq.Value}|{cachedSerializerUid.Value}\n").ConfigureAwait(false);

                    if (cachedBossRune.HasValue)
                        await SendLineToClientSafe(connection, $"BOSSRUNE|{cachedBossRune.Value}\n").ConfigureAwait(false);

                    if (cachedSeed.HasValue)
                        await SendLineToClientSafe(connection, $"SEED|{cachedSeed.Value}\n").ConfigureAwait(false);

                    if (cachedCountersPayload != null)
                        await SendLineToClientSafe(connection, $"COUNTERS|{cachedCountersPayload}\n").ConfigureAwait(false);

                    if (cachedBlueprintsPayload != null)
                        await SendLineToClientSafe(connection, $"BLUEPRINTS|{cachedBlueprintsPayload}\n").ConfigureAwait(false);

                    if (cachedLevelGraphPayload != null)
                        await SendLineToClientSafe(connection, $"LGRAPH|{cachedLevelGraphPayload}\n").ConfigureAwait(false);
                }

                GameMenu.EnqueueMainThread(() =>
                {
                    GameMenu.NetRef = this;
                    GameMenu.SetRole(_role);
                    GameMenu.NotifyRemoteConnected(_role);
                });
                await SendLineToClientSafe(connection, $"ID|{assignedId}\n").ConfigureAwait(false);
                await SendKnownUsersToClientSafe(connection).ConfigureAwait(false);
                if (_role == NetRole.Host && TryBuildLocalHpLine(out var localHpLine))
                    await SendLineToClientSafe(connection, localHpLine).ConfigureAwait(false);

                _ = Task.Run(() => RecvLoop(connection.Stream, ct, assignedId, connection));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] AcceptLoop error: {msg}", ex.Message);
        }
    }

    // ================= CLIENT =================
    private void StartClient()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectWithRetryAsync(_cts.Token));
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        var maxAttempts = GameMenu.ClientConnectMaxAttempts;
        var attempt = 0;

        while (!ct.IsCancellationRequested && attempt < maxAttempts)
        {
            attempt++;
            GameMenu.EnqueueMainThread(() => GameMenu.NotifyClientConnectAttempt(attempt));
            try
            {
                _log.Information("[NetNode] Client connecting to {dest}", _destEp);

                var tcp = new TcpClient(AddressFamily.InterNetwork);
                tcp.NoDelay = true;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

                await tcp.ConnectAsync(_destEp.Address, _destEp.Port, timeoutCts.Token).ConfigureAwait(false);
                _client = tcp;
                _stream = tcp.GetStream();

                _log.Information("[NetNode] Client connected to {dest}", _destEp);

                await SendLineSafe("HELLO\n").ConfigureAwait(false);

                lock (_sync)
                {
                    _hasRemote = true;
                    _connectedClientCount = 1;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = 1;
                }
                GameMenu.EnqueueMainThread(() =>
                {
                    GameMenu.NetRef = this;
                    GameMenu.SetRole(_role);
                    GameMenu.NotifyRemoteConnected(_role);
                });

                _recvTask = Task.Run(() => RecvLoop(_stream!, ct, 1, null));
                return;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                CloseClientConnection();
                _log.Warning("[NetNode] Client connect error: {msg}", ex.Message);
                if (attempt >= maxAttempts)
                {
                    GameMenu.EnqueueMainThread(GameMenu.NotifyClientConnectFailed);
                    break;
                }
                await Task.Delay(3000, ct).ConfigureAwait(false);
            }
        }
    }

    // ============== COMMON IO ==============
    private async Task RecvLoop(NetworkStream stream, CancellationToken ct, int? senderId, ClientConnection? sender)
    {
        using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var recvCt = recvCts.Token;
        var incomingLines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

        try
        {
            var readTask = ReadIncomingLinesLoop(stream, incomingLines.Writer, recvCt);
            var processTask = ProcessIncomingLinesLoop(incomingLines.Reader, senderId, sender, recvCts, recvCt);
            await Task.WhenAll(readTask, processTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (recvCt.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] RecvLoop error: {msg}", ex.Message);
        }
        finally
        {
            try { recvCts.Cancel(); } catch { }
            if (_role == NetRole.Host && sender != null)
            {
                CleanupHostClient(sender);
            }
            else
            {
                CleanupClient();
            }
        }
    }

    private async Task ReadIncomingLinesLoop(NetworkStream stream, ChannelWriter<string> writer, CancellationToken ct)
    {
        var buf = new byte[4096];
        var sb = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
                if (n <= 0)
                    break;

                sb.Append(Encoding.UTF8.GetString(buf, 0, n));

                while (TryReadBufferedLine(sb, out var line))
                {
                    if (line.Length == 0)
                        continue;

                    await writer.WriteAsync(line, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] Recv read error: {msg}", ex.Message);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task ProcessIncomingLinesLoop(
        ChannelReader<string> reader,
        int? senderId,
        ClientConnection? sender,
        CancellationTokenSource recvCts,
        CancellationToken ct)
    {
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var line))
                {
                    var lineCopy = line;

                    // Fast-path packet types used during level generation waits.
                    // They only update thread-safe buffers in GameDataSync and must not wait for main-thread queue.
                    if (_role == NetRole.Client && TryHandleClientFastPathLine(lineCopy))
                        continue;

                    GameMenu.EnqueueMainThread(() =>
                    {
                        try
                        {
                            if (!HandleLine(lineCopy, senderId, out var forwardLine))
                            {
                                try { recvCts.Cancel(); } catch { }
                                return;
                            }

                            if (_role == NetRole.Host && sender != null && forwardLine != null)
                                ForwardLineToOtherClients(sender, forwardLine);
                        }
                        catch (Exception ex)
                        {
                            _log.Warning("[NetNode] HandleLine(main-thread) failed: {msg}", ex.Message);
                        }
                    });
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] Recv process error: {msg}", ex.Message);
        }
    }

    private bool TryHandleClientFastPathLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return false;

        try
        {
            if (line.StartsWith("HXSYNC|", StringComparison.Ordinal))
            {
                var payload = line["HXSYNC|".Length..];
                lock (_sync) _hasRemote = true;
                GameDataSync.ReceiveSerializerSync(payload);
                return true;
            }

            if (line.StartsWith("LSEED|", StringComparison.Ordinal))
            {
                var payload = line["LSEED|".Length..];
                lock (_sync) _hasRemote = true;
                GameDataSync.ReceiveLevelSeed(payload);
                return true;
            }

            if (line.StartsWith("LGRAPH|", StringComparison.Ordinal))
            {
                var payload = line["LGRAPH|".Length..];
                lock (_sync) _hasRemote = true;
                GameDataSync.ReceiveLevelGraph(payload);
                return true;
            }
        }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] Fast-path line handling failed: {msg}", ex.Message);
        }

        return false;
    }

    private static bool TryReadBufferedLine(StringBuilder buffer, out string line)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\n')
                continue;

            line = buffer.ToString(0, i).Trim();
            buffer.Remove(0, i + 1);
            return true;
        }

        line = string.Empty;
        return false;
    }

    private bool HandleLine(string line, int? senderId, out string? forwardLine)
    {
        forwardLine = null;
        var forceSenderId = _role == NetRole.Host && senderId.HasValue;

        if (line.StartsWith("ID|"))
        {
            var part = line["ID|".Length..];
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
            {
                ID = parsedId;
                _log.Information("[NetNode] Assigned ID {Id}", ID);
            }
            return true;
        }

        if (line.StartsWith("WELCOME"))
        {
            lock (_sync) _hasRemote = true;
            return true;
        }

        if (line.StartsWith("HELLO"))
        {
            lock (_sync) _hasRemote = true;
            if (_role == NetRole.Host && _useSteamTransport && senderId.HasValue)
            {
                SteamClientConnection? steamConnection = null;
                lock (_clientsLock)
                {
                    _steamClients.TryGetValue(senderId.Value, out steamConnection);
                }

                if (steamConnection != null)
                    _ = Task.Run(() => SendInitialStateToSteamClient(steamConnection));
            }
            return true;
        }

        if (line.StartsWith("SEED|"))
        {
            var partsSeed = line.Split('|');
            if (partsSeed.Length >= 2 && int.TryParse(partsSeed[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostSeed))
            {
                lock (_sync) _hasRemote = true;
                GameMenu.ReceiveHostRunSeed(hostSeed);
                _log.Information("[NetNode] Received host run seed {Seed}", hostSeed);
            }
            else
            {
                _log.Warning("[NetNode] Malformed SEED line: \"{line}\"");
            }
            return true;
        }

        if (line.StartsWith("HXSYNC|", StringComparison.Ordinal))
        {
            var payload = line["HXSYNC|".Length..];
            lock (_sync) _hasRemote = true;
            GameDataSync.ReceiveSerializerSync(payload);
            return true;
        }

        if (line.StartsWith("COUNTERS|"))
        {
            var payload = line["COUNTERS|".Length..];
            lock (_sync) _hasRemote = true;
            GameDataSync.ReceiveCounters(payload);
            return true;
        }

        if (line.StartsWith("BLUEPRINTS|"))
        {
            var payload = line["BLUEPRINTS|".Length..];
            lock (_sync) _hasRemote = true;
            GameDataSync.ReceiveBlueprints(payload);
            return true;
        }

        if (line.StartsWith("BOSSRUNE|"))
        {
            var payload = line["BOSSRUNE|".Length..];
            lock (_sync) _hasRemote = true;
            GameDataSync.ReceiveBossRune(payload);
            return true;
        }

        if (line.StartsWith("USER|"))
        {
            var payload = line["USER|".Length..];
            var effectiveId = ResolvePayloadId(payload, senderId, out var username);
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                int primaryId;
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.Username = username;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                    primaryId = _primaryRemoteId;
                }

                if (effectiveId.Value == primaryId)
                    GameMenu.ReceiveRemoteUsername(username);

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildTaggedLine("USER", effectiveId.Value, username);
            }
            return true;
        }

        if (line.StartsWith("CHAT|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["CHAT|".Length..];
            ParseChatPayload(payload, out var parsedId, out var message);
            var effectiveId = parsedId ?? senderId;
            if (forceSenderId)
                effectiveId = senderId;

            message = SanitizeChatMessage(message);
            if (effectiveId.HasValue && !string.IsNullOrWhiteSpace(message))
            {
                string? username;
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                    username = state.Username;
                    _pendingChatMessages.Add(new RemoteChatMessage(effectiveId.Value, username, message));
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildChatLine(effectiveId.Value, message);
            }

            return true;
        }

        if (line.StartsWith("LDESC|"))
        {
            var payload = line["LDESC|".Length..];
            lock (_sync) _hasRemote = true;
            GameMenu.ReceiveLevelDesc(payload);
            return true;
        }

        if (line.StartsWith("LSEED|"))
        {
            var payload = line["LSEED|".Length..];
            lock (_sync) _hasRemote = true;
            GameDataSync.ReceiveLevelSeed(payload);
            return true;
        }

        if (line.StartsWith("LGRAPH|"))
        {
            var payload = line["LGRAPH|".Length..];
            lock (_sync) _hasRemote = true;
            GameDataSync.ReceiveLevelGraph(payload);
            return true;
        }

        if (line.StartsWith("SKIN|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["SKIN|".Length..];
            var effectiveId = ResolvePayloadId(payload, senderId, out var skin);
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                int primaryId;
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.Skin = skin;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                    primaryId = _primaryRemoteId;
                }

                var skinId = effectiveId.Value;
                var skinValue = skin;
                GameMenu.EnqueueMainThread(() =>
                {
                    try
                    {
                        ModEntry.SetClientSkin(skinId, skinValue);
                        if (skinId == primaryId)
                            GameDataSync.ReceiveHeroSkin(skinValue);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("[NetNode] Failed to handle hero skin: {msg}", ex.Message);
                    }
                });

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildTaggedLine("SKIN", effectiveId.Value, skin);
            }
            return true;
        }

        if (line.StartsWith("HEAD|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["HEAD|".Length..];
            var effectiveId = ResolvePayloadId(payload, senderId, out var skinHead);
            _log.Debug($"{skinHead}");
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                int primaryId;
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.Head = skinHead;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                    primaryId = _primaryRemoteId;
                }

                var headId = effectiveId.Value;
                var headSkinValue = skinHead;
                GameMenu.EnqueueMainThread(() =>
                {
                    try
                    {
                        ModEntry.SetClientHeadSkin(headId, headSkinValue);
                        if (headId == primaryId)
                            GameDataSync.ReceiveHeroHeadSkin(headSkinValue);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("[NetNode] Failed to handle hero skin: {msg}", ex.Message);
                    }
                });

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildTaggedLine("HEAD", effectiveId.Value, skinHead);
            }
            return true;
        }

        if (line.StartsWith("GEN|"))
        {
            var payload = line["GEN|".Length..];
            lock (_sync) _hasRemote = true;
            GameMenu.ReceiveGeneratePayload(payload);
            return true;
        }

        if (line.StartsWith("LEVEL|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["LEVEL|".Length..];
            var partsLevel = payload.Split(new[] { '|' }, 2);
            int? parsedId = null;
            string levelValue = payload;
            if (partsLevel.Length >= 2)
            {
                levelValue = partsLevel[1];
                if (int.TryParse(partsLevel[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLevelRemoteId))
                {
                    parsedId = parsedLevelRemoteId;
                }
            }
            var effectiveId = parsedId ?? senderId;
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.LevelId = levelValue;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildTaggedLine("LEVEL", effectiveId.Value, levelValue);
            }
            return true;
        }

        if (line.StartsWith("ZROOM|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["ZROOM|".Length..];
            ParseRoomPayload(payload, out var parsedId, out var roomLevelValue, out var roomIdValue);
            var effectiveId = parsedId ?? senderId;
            if (forceSenderId)
                effectiveId = senderId;

            if (effectiveId.HasValue && roomIdValue >= 0)
            {
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.RoomLevelId = roomLevelValue;
                    state.RoomId = roomIdValue;
                    state.HasRoom = true;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildRoomLine(effectiveId.Value, roomLevelValue, roomIdValue);
            }
            return true;
        }

        if (line.StartsWith("ANIM|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line[(line.IndexOf('|') + 1)..];
            ParseAnimPayload(payload, out var parsedId, out var animName, out var q, out var gFlag);
            var effectiveId = parsedId ?? senderId;
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.Anim = animName;
                    state.AnimQueue = q;
                    state.AnimG = gFlag;
                    state.HasAnim = true;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildAnimLine(effectiveId.Value, animName, q, gFlag);
            }
            return true;
        }


        if (line.StartsWith("HEADANIM|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line[(line.IndexOf('|') + 1)..];
            ParseHeadAnimPayload(payload, out var parsedId, out var animName);
            var effectiveId = parsedId ?? senderId;
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.HeadAnim = animName;
                    state.HasHeadAnim = true;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildHeadAnimLine(effectiveId.Value, animName);
            }
            return true;
        }

        if (line.StartsWith("INV|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line[(line.IndexOf('|') + 1)..];
            ParseWeaponPayload(payload, out var parsedId, out var kind, out var slot, out var permanentId, out var ammo);
            var effectiveId = parsedId ?? senderId;
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.WeaponKind = kind;
                    state.WeaponSlot = slot;
                    state.WeaponPermanentId = permanentId;
                    state.WeaponAmmo = ammo ?? int.MinValue;
                    state.HasWeaponUpdate = true;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildWeaponLine("INV", effectiveId.Value, kind, slot, permanentId, ammo);
            }
            return true;
        }

        if (line.StartsWith("ATK|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line[(line.IndexOf('|') + 1)..];
            ParseWeaponPayload(payload, out var parsedId, out var kind, out var slot, out var permanentId, out var ammo);
            var effectiveId = parsedId ?? senderId;
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    _pendingAttacks.Add(new RemoteAttack(effectiveId.Value, kind, slot, permanentId, ammo));
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildWeaponLine("ATK", effectiveId.Value, kind, slot, permanentId, ammo);
            }
            return true;
        }

        if (line.StartsWith("HP|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line[(line.IndexOf('|') + 1)..];
            ParseHpPayload(payload, out var parsedId, out var life, out var maxLife, out var lif, out var bonusLife, out var recover);
            var effectiveId = parsedId ?? senderId;
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.Life = life;
                    state.MaxLife = maxLife;
                    state.Lif = lif;
                    state.BonusLife = bonusLife;
                    state.Recover = recover;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildHpLine(effectiveId.Value, life, maxLife, lif, bonusLife, recover);
            }
            return true;
        }

        if (line.StartsWith("MOBSTATE|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["MOBSTATE|".Length..];
            var parsedStates = ParseMobStatesPayload(payload);
            lock (_sync)
            {
                if (_role == NetRole.Host)
                {
                    if (parsedStates.Count > 0)
                        _pendingMobStates.AddRange(parsedStates);
                }
                else
                {
                    _pendingMobStates = parsedStates;
                }
                _hasRemote = true;
            }
            return true;
        }

        if (line.StartsWith("MOBHIT|", StringComparison.OrdinalIgnoreCase))
        {
            if (_role != NetRole.Host)
                return true;

            var payload = line["MOBHIT|".Length..];
            if (TryParseMobHitPayload(payload, senderId, forceSenderId, out var hit))
            {
                lock (_sync)
                {
                    _pendingMobHits.Add(hit);
                    _hasRemote = true;
                }
            }
            return true;
        }

        if (line.StartsWith("MOBDIE|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["MOBDIE|".Length..];
            if (TryParseMobDiePayload(payload, senderId, forceSenderId, out var die))
            {
                lock (_sync)
                {
                    _pendingMobDies.Add(die);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildMobDieLine(die);
            }
            return true;
        }

        if (line.StartsWith("MOBDRAW|", StringComparison.OrdinalIgnoreCase))
        {
            if (_role != NetRole.Host)
                return true;

            var payload = line["MOBDRAW|".Length..];
            if (TryParseMobDrawPayload(payload, senderId, forceSenderId, out var draws))
            {
                lock (_sync)
                {
                    _pendingMobDraws.AddRange(draws);
                    _hasRemote = true;
                }
            }
            return true;
        }

        if (line.StartsWith("EXITREADY|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["EXITREADY|".Length..];
            if (TryParseExitReadyPayload(payload, senderId, forceSenderId, out var state))
            {
                lock (_sync)
                {
                    _pendingExitReadyStates.Add(state);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildExitReadyLine(state);
            }
            return true;
        }

        if (line.StartsWith("PDOWN|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["PDOWN|".Length..];
            if (TryParsePlayerDownPayload(payload, senderId, forceSenderId, out var state))
            {
                lock (_sync)
                {
                    _pendingPlayerDownStates.Add(state);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildPlayerDownLine(state);
            }
            return true;
        }

        if (line.StartsWith("PREVIVE|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["PREVIVE|".Length..];
            if (TryParsePlayerRevivePayload(payload, senderId, forceSenderId, out var request))
            {
                lock (_sync)
                {
                    _pendingPlayerReviveRequests.Add(request);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildPlayerReviveLine(request);
            }
            return true;
        }

        if (line.StartsWith("MOBATK|", StringComparison.OrdinalIgnoreCase))
        {
            if (_role == NetRole.Host)
                return true;

            var payload = line["MOBATK|".Length..];
            if (TryParseMobAttackPayload(payload, out var attack))
            {
                lock (_sync)
                {
                    _pendingMobAttacks.Add(attack);
                    _hasRemote = true;
                }
            }
            return true;
        }

        if (line.StartsWith("DIED", StringComparison.OrdinalIgnoreCase))
        {
            if (_role == NetRole.Host)
            {
                var _remoteId = senderId ?? 0;
                _log.Information("[NetNode] Remote hero died (id {Id})", _remoteId);
                forwardLine = "DIED\n";
            }
            GameDataSync.TriggerRemoteDeath();
            return true;
        }

        if (line.StartsWith("KICK"))
        {
            return false;
        }

        if (TryParsePositionLine(line, senderId, out var remoteId, out var cx, out var cy, out var dir, out var hasDir))
        {
            if (forceSenderId && senderId.HasValue)
                remoteId = senderId.Value;
            int forwardDir = dir;
            lock (_sync)
            {
                var state = GetOrCreateRemoteLocked(remoteId);
                var prevX = state.X;
                var hadRemote = state.HasRemote;
                state.X = cx;
                state.Y = cy;
                if (hasDir)
                {
                    state.Dir = dir;
                }
                else if (hadRemote && cx != prevX)
                {
                    state.Dir = cx < prevX ? -1 : 1;
                }
                state.HasRemote = true;
                _hasRemote = true;
                if (_primaryRemoteId == 0)
                    _primaryRemoteId = remoteId;
                forwardDir = state.Dir;
            }
            if (_role == NetRole.Host && senderId.HasValue)
                forwardLine = BuildPosLine(remoteId, cx, cy, forwardDir);
        }

        return true;
    }

    private void ForwardLineToOtherClients(ClientConnection sender, string line)
    {
        List<ClientConnection> snapshot;
        lock (_clientsLock)
        {
            snapshot = _clients.Values.Where(c => c.AssignedId != sender.AssignedId).ToList();
        }

        foreach (var client in snapshot)
        {
            _ = SendLineToClientSafe(client, line);
        }
    }

    private void ForwardLineToOtherSteamClients(SteamClientConnection sender, string line)
    {
        List<SteamClientConnection> snapshot;
        lock (_clientsLock)
        {
            snapshot = _steamClients.Values.Where(c => c.AssignedId != sender.AssignedId).ToList();
        }

        foreach (var client in snapshot)
        {
            _ = SendLineToSteamClientSafe(client, line);
        }
    }

    private void CleanupHostClient(ClientConnection sender)
    {
        bool hasClients;
        lock (_clientsLock)
        {
            _clients.Remove(sender.AssignedId);
            _connectedClientCount = _clients.Count;
            hasClients = _connectedClientCount > 0;
        }

        sender.Dispose();

        if (sender.AssignedId >= 2)
        {
            lock (UsedClientIds)
            {
                UsedClientIds.Remove(sender.AssignedId);
            }
        }

        lock (_sync)
        {
            RemoveRemoteLocked(sender.AssignedId);
            _pendingAttacks.RemoveAll(a => a.Id == sender.AssignedId);
            _pendingChatMessages.RemoveAll(m => m.Id == sender.AssignedId);
            _pendingMobHits.RemoveAll(h => h.UserId == sender.AssignedId);
            _pendingMobDies.RemoveAll(d => d.UserId == sender.AssignedId);
            _pendingExitReadyStates.RemoveAll(s => s.UserId == sender.AssignedId);
            _pendingPlayerDownStates.RemoveAll(s => s.UserId == sender.AssignedId);
            _pendingPlayerReviveRequests.RemoveAll(s => s.ReviverId == sender.AssignedId || s.TargetId == sender.AssignedId);
            _hasRemote = hasClients;
        }

        if (!hasClients)
        {
            bool stillEmpty;
            lock (_clientsLock)
            {
                stillEmpty = _clients.Count == 0;
            }
            if (stillEmpty)
                GameMenu.EnqueueMainThread(() => GameMenu.NotifyRemoteDisconnected(_role));
        }
    }

    private void CleanupHostSteamClient(SteamClientConnection sender)
    {
        bool hasClients;
        lock (_clientsLock)
        {
            _steamClients.Remove(sender.AssignedId);
            _steamClientIdsBySteam.Remove(sender.SteamId.m_SteamID);
            _connectedClientCount = _steamClients.Count;
            hasClients = _connectedClientCount > 0;
        }

        try { SteamNetworking.CloseP2PSessionWithUser(sender.SteamId); } catch { }
        sender.Dispose();

        if (sender.AssignedId >= 2)
        {
            lock (UsedClientIds)
            {
                UsedClientIds.Remove(sender.AssignedId);
            }
        }

        lock (_sync)
        {
            RemoveRemoteLocked(sender.AssignedId);
            _pendingAttacks.RemoveAll(a => a.Id == sender.AssignedId);
            _pendingChatMessages.RemoveAll(m => m.Id == sender.AssignedId);
            _pendingMobHits.RemoveAll(h => h.UserId == sender.AssignedId);
            _pendingMobDies.RemoveAll(d => d.UserId == sender.AssignedId);
            _pendingExitReadyStates.RemoveAll(s => s.UserId == sender.AssignedId);
            _pendingPlayerDownStates.RemoveAll(s => s.UserId == sender.AssignedId);
            _pendingPlayerReviveRequests.RemoveAll(s => s.ReviverId == sender.AssignedId || s.TargetId == sender.AssignedId);
            _hasRemote = hasClients;
        }

        if (!hasClients)
            GameMenu.EnqueueMainThread(() => GameMenu.NotifyRemoteDisconnected(_role));
    }

    private void CleanupClient()
    {
        lock (_sync)
        {
            _hasRemote = false;
            _connectedClientCount = 0;
            _remotes.Clear();
            _primaryRemoteId = 0;
            _pendingAttacks.Clear();
            _pendingChatMessages.Clear();
            _pendingMobStates.Clear();
            _pendingMobHits.Clear();
            _pendingMobDies.Clear();
            _pendingMobAttacks.Clear();
            _pendingMobDraws.Clear();
            _pendingExitReadyStates.Clear();
            _pendingPlayerDownStates.Clear();
            _pendingPlayerReviveRequests.Clear();
        }
        if (_useSteamTransport)
        {
            if (_steamHostId.m_SteamID != 0UL)
            {
                try { SteamNetworking.CloseP2PSessionWithUser(_steamHostId); } catch { }
            }
        }
        else
        {
            CloseClientConnection();
        }
        GameMenu.EnqueueMainThread(() => GameMenu.NotifyRemoteDisconnected(_role));
    }

    private RemoteState GetOrCreateRemoteLocked(int id)
    {
        if (!_remotes.TryGetValue(id, out var state))
        {
            state = new RemoteState(id);
            _remotes[id] = state;
        }
        return state;
    }

    private void RemoveRemoteLocked(int id)
    {
        _remotes.Remove(id);
        if (_primaryRemoteId == id)
        {
            _primaryRemoteId = 0;
            foreach (var key in _remotes.Keys)
            {
                if (_primaryRemoteId == 0 || key < _primaryRemoteId)
                    _primaryRemoteId = key;
            }
        }
    }

    private static int? ResolvePayloadId(string payload, int? senderId, out string cleanedPayload)
    {
        cleanedPayload = payload;
        var parts = payload.Split(new[] { '|' }, 2);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
        {
            cleanedPayload = parts[1];
            return parsedId;
        }
        return senderId;
    }

    private static void ParseAnimPayload(string payload, out int? parsedId, out string animName, out int? queue, out bool? gFlag)
    {
        parsedId = null;
        animName = string.Empty;
        queue = null;
        gFlag = null;

        var parts = payload.Split('|');
        var startIndex = 0;
        if (parts.Length >= 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idValue))
        {
            parsedId = idValue;
            startIndex = 1;
        }

        if (parts.Length > startIndex)
            animName = parts[startIndex];

        if (parts.Length > startIndex + 1 &&
            int.TryParse(parts[startIndex + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedQ))
            queue = parsedQ;

        if (parts.Length > startIndex + 2 && TryParseBool(parts[startIndex + 2], out var parsedBool))
            gFlag = parsedBool;
    }

    private static void ParseRoomPayload(string payload, out int? parsedId, out string levelId, out int roomId)
    {
        parsedId = null;
        levelId = string.Empty;
        roomId = -1;

        if (string.IsNullOrWhiteSpace(payload))
            return;

        var parts = payload.Split('|');
        if (parts.Length >= 3 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRemoteId))
        {
            parsedId = parsedRemoteId;
            levelId = parts[1];
            _ = int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out roomId);
            return;
        }

        if (parts.Length >= 2)
        {
            levelId = parts[0];
            _ = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out roomId);
        }
    }


    private static void ParseHeadAnimPayload(string payload, out int? parsedId, out string animName)
    {
        parsedId = null;
        animName = string.Empty;

        var parts = payload.Split('|');
        var startIndex = 0;
        if (parts.Length >= 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idValue))
        {
            parsedId = idValue;
            startIndex = 1;
        }

        if (parts.Length > startIndex)
            animName = parts[startIndex];
    }

    private static void ParseWeaponPayload(string payload, out int? parsedId, out string kind, out int slot, out int permanentId, out int? ammo)
    {
        parsedId = null;
        kind = string.Empty;
        slot = -1;
        permanentId = 0;
        ammo = null;

        var parts = payload.Split('|');
        var startIndex = 0;
        if (parts.Length >= 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idValue))
        {
            parsedId = idValue;
            startIndex = 1;
        }

        if (parts.Length > startIndex)
            kind = parts[startIndex];

        if (parts.Length > startIndex + 1 &&
            int.TryParse(parts[startIndex + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSlot))
            slot = parsedSlot;

        if (parts.Length > startIndex + 2 &&
            int.TryParse(parts[startIndex + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPermanent))
            permanentId = parsedPermanent;

        if (parts.Length > startIndex + 3 &&
            int.TryParse(parts[startIndex + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAmmo))
            ammo = parsedAmmo;
    }

    private static void ParseHpPayload(string payload, out int? parsedId, out int life, out int maxLife, out int lif, out int bonusLife, out int recover)
    {
        parsedId = null;
        life = 0;
        maxLife = 0;
        lif = 0;
        bonusLife = 0;
        recover = 0;

        var parts = payload.Split('|');
        var startIndex = 0;
        if (parts.Length >= 6 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idValue))
        {
            parsedId = idValue;
            startIndex = 1;
        }

        if (parts.Length > startIndex &&
            int.TryParse(parts[startIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLife))
            life = parsedLife;
        if (parts.Length > startIndex + 1 &&
            int.TryParse(parts[startIndex + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMaxLife))
            maxLife = parsedMaxLife;
        if (parts.Length > startIndex + 2 &&
            int.TryParse(parts[startIndex + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLif))
            lif = parsedLif;
        if (parts.Length > startIndex + 3 &&
            int.TryParse(parts[startIndex + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBonusLife))
            bonusLife = parsedBonusLife;
        if (parts.Length > startIndex + 4 &&
            int.TryParse(parts[startIndex + 4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRecover))
            recover = parsedRecover;
    }

    private static void ParseChatPayload(string payload, out int? parsedId, out string message)
    {
        parsedId = null;
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(payload))
            return;

        var parts = payload.Split(new[] { '|' }, 2);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idValue))
        {
            parsedId = idValue;
            message = parts[1];
            return;
        }

        message = payload;
    }

    private static List<MobStateSnapshot> ParseMobStatesPayload(string payload)
    {
        var states = new List<MobStateSnapshot>();
        if (string.IsNullOrWhiteSpace(payload))
            return states;

        var entries = payload.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in entries)
        {
            var parts = entry.Split(',');
            if (parts.Length < 7)
                continue;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                continue;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                continue;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                continue;
            if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dir))
                continue;
            if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var life))
                continue;
            if (!int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxLife))
                continue;
            var animPayload = parts[6];
            var type = parts.Length > 7 ? parts[7] : string.Empty;
            var statePayload = parts.Length > 8 ? parts[8] : string.Empty;

            states.Add(new MobStateSnapshot(index, x, y, dir, life, maxLife, animPayload, type, statePayload));
        }

        return states;
    }

    private static bool TryParseMobHitPayload(string payload, int? senderId, bool forceSenderId, out MobHit hit)
    {
        hit = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 5)
            return false;

        int parsedUserId = 0;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedUserId))
            parsedUserId = senderId ?? 0;

        if (forceSenderId && senderId.HasValue)
            parsedUserId = senderId.Value;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mobIndex))
            return false;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hp))
            return false;
        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;
        if (!double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;

        hit = new MobHit(parsedUserId, mobIndex, hp, x, y);
        return true;
    }

    private static bool TryParseMobDiePayload(string payload, int? senderId, bool forceSenderId, out MobDie die)
    {
        die = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 4)
            return false;

        int parsedUserId = 0;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedUserId))
            parsedUserId = senderId ?? 0;

        if (forceSenderId && senderId.HasValue)
            parsedUserId = senderId.Value;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mobIndex))
            return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;
        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;

        die = new MobDie(parsedUserId, mobIndex, x, y);
        return true;
    }

    private static bool TryParseMobAttackPayload(string payload, out MobAttack attack)
    {
        attack = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split(',');
        if (parts.Length < 7)
            return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mobIndex))
            return false;

        string skillId;
        try
        {
            skillId = Uri.UnescapeDataString(parts[1]);
        }
        catch
        {
            skillId = parts[1];
        }

        if (string.IsNullOrWhiteSpace(skillId))
            return false;

        var requiresTargetInArea = parts[2] == "1";
        var hasData = parts[3] == "1";

        int? data = null;
        if (hasData)
        {
            if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedData))
                return false;
            data = parsedData;
        }

        if (!double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;
        if (!double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;

        var targetUserId = 0;
        if (parts.Length > 7)
            int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out targetUserId);

        var dir = 0;
        if (parts.Length > 8)
            int.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out dir);

        attack = new MobAttack(mobIndex, skillId, requiresTargetInArea, data, x, y, targetUserId, dir);
        return true;
    }

    private static bool TryParseMobDrawPayload(string payload, int? senderId, bool forceSenderId, out List<MobDraw> draws)
    {
        draws = new List<MobDraw>();
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var entries = payload.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length == 0)
            entries = new[] { payload };

        for (int i = 0; i < entries.Length; i++)
        {
            if (!TryParseSingleMobDrawPayload(entries[i], senderId, forceSenderId, out var draw))
                continue;

            draws.Add(draw);
        }

        return draws.Count > 0;
    }

    private static bool TryParseSingleMobDrawPayload(string payload, int? senderId, bool forceSenderId, out MobDraw draw)
    {
        draw = default;
        var parts = payload.Split('|');
        if (parts.Length < 4)
            return false;

        int parsedUserId = 0;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedUserId))
            parsedUserId = senderId ?? 0;

        if (forceSenderId && senderId.HasValue)
            parsedUserId = senderId.Value;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mobIndex))
            return false;
        if (!TryParseBool(parts[2], out var isOutOfGame))
            return false;
        if (!TryParseBool(parts[3], out var isOnScreen))
            return false;

        draw = new MobDraw(parsedUserId, mobIndex, isOutOfGame, isOnScreen);
        return true;
    }

    private static bool TryParseExitReadyPayload(string payload, int? senderId, bool forceSenderId, out ExitReadyState state)
    {
        state = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 7)
            return false;

        int parsedUserId = 0;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedUserId))
            parsedUserId = senderId ?? 0;

        if (forceSenderId && senderId.HasValue)
            parsedUserId = senderId.Value;
        if (parsedUserId <= 0)
            return false;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var doorCx))
            return false;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var doorCy))
            return false;
        if (!TryParseBool(parts[3], out var pressed))
            return false;
        if (!TryParseBool(parts[4], out var insideCircle))
            return false;
        if (!TryParseBool(parts[5], out var isOutOfGame))
            return false;
        if (!TryParseBool(parts[6], out var isOnScreen))
            return false;

        state = new ExitReadyState(parsedUserId, doorCx, doorCy, pressed, insideCircle, isOutOfGame, isOnScreen);
        return true;
    }

    private static bool TryParsePlayerDownPayload(string payload, int? senderId, bool forceSenderId, out PlayerDownState state)
    {
        state = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 5)
            return false;

        int parsedUserId;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedUserId))
            parsedUserId = senderId ?? 0;
        if (forceSenderId && senderId.HasValue)
            parsedUserId = senderId.Value;
        if (parsedUserId <= 0)
            return false;

        if (!TryParseBool(parts[1], out var isDowned))
            return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;
        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;

        var levelId = parts[4] ?? string.Empty;
        levelId = levelId.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        if (levelId.Length == 0)
            levelId = string.Empty;

        var hasHeadPosition = false;
        var headX = 0d;
        var headY = 0d;
        var hasHeadAnim = false;
        string? headAnim = null;
        if (parts.Length >= 7 &&
            double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedHeadX) &&
            double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedHeadY))
        {
            hasHeadPosition = true;
            headX = parsedHeadX;
            headY = parsedHeadY;

            if (parts.Length >= 8)
            {
                var parsedAnim = (parts[7] ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(parsedAnim))
                {
                    hasHeadAnim = true;
                    headAnim = parsedAnim;
                }
            }
        }

        state = new PlayerDownState(parsedUserId, isDowned, x, y, levelId, hasHeadPosition, headX, headY, hasHeadAnim, headAnim);
        return true;
    }

    private static bool TryParsePlayerRevivePayload(string payload, int? senderId, bool forceSenderId, out PlayerReviveRequest request)
    {
        request = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 2)
            return false;

        int reviverId;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out reviverId))
            reviverId = senderId ?? 0;
        if (forceSenderId && senderId.HasValue)
            reviverId = senderId.Value;
        if (reviverId <= 0)
            return false;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetId))
            return false;
        if (targetId <= 0)
            return false;

        request = new PlayerReviveRequest(reviverId, targetId);
        return true;
    }

    private static bool TryParsePositionLine(string line, int? senderId, out int remoteId, out double rx, out double ry, out int dir, out bool hasDir)
    {
        remoteId = 0;
        rx = 0;
        ry = 0;
        dir = 0;
        hasDir = false;

        var parts = line.Split('|');
        if (parts.Length >= 4 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRemoteIdWithDir) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cxWithDir) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var cyWithDir) &&
            int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDir))
        {
            remoteId = parsedRemoteIdWithDir;
            rx = cxWithDir;
            ry = cyWithDir;
            dir = parsedDir < 0 ? -1 : parsedDir > 0 ? 1 : 0;
            hasDir = true;
            return true;
        }

        if (parts.Length >= 3 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRemoteId) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cx) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var cy))
        {
            remoteId = parsedRemoteId;
            rx = cx;
            ry = cy;
            return true;
        }

        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var cxFallback) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cyFallback) &&
            senderId.HasValue)
        {
            remoteId = senderId.Value;
            rx = cxFallback;
            ry = cyFallback;
            return true;
        }

        return false;
    }

    private static string BuildTaggedLine(string tag, int id, string payload)
    {
        return $"{tag}|{id}|{payload}\n";
    }

    private static string BuildAnimLine(int id, string animName, int? queue, bool? gFlag)
    {
        var queuePart = queue.HasValue ? queue.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        var gPart = gFlag.HasValue ? (gFlag.Value ? "1" : "0") : string.Empty;
        return $"ANIM|{id}|{animName}|{queuePart}|{gPart}\n";
    }

    private static string BuildHeadAnimLine(int id, string animName)
    {
        return $"HEADANIM|{id}|{animName}\n";
    }

    private static string BuildRoomLine(int id, string levelId, int roomId)
    {
        var safeLevelId = (levelId ?? string.Empty)
            .Replace("|", "/", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"ZROOM|{id}|{safeLevelId}|{roomId}\n");
    }

    private static string BuildWeaponLine(string tag, int id, string kind, int slot, int permanentId, int? ammo)
    {
        if (ammo.HasValue)
            return $"{tag}|{id}|{kind}|{slot}|{permanentId}|{ammo.Value}\n";
        return $"{tag}|{id}|{kind}|{slot}|{permanentId}\n";
    }

    private static string BuildHpLine(int id, int life, int maxLife, int lif, int bonusLife, int recover)
    {
        return $"HP|{id}|{life}|{maxLife}|{lif}|{bonusLife}|{recover}\n";
    }

    private static string BuildChatLine(int id, string message)
    {
        var safe = SanitizeChatMessage(message);
        return $"CHAT|{id}|{safe}\n";
    }

    private static string SanitizeChatMessage(string? message)
    {
        var safe = (message ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("|", "/", StringComparison.Ordinal)
            .Trim();

        const int maxLength = 256;
        if (safe.Length > maxLength)
            safe = safe[..maxLength];

        return safe;
    }

    private bool TryBuildLocalHpLine(out string line)
    {
        lock (_sync)
        {
            if (!_hasLocalHpSnapshot)
            {
                line = string.Empty;
                return false;
            }

            var senderId = ID > 0 ? ID : 1;
            line = BuildHpLine(senderId, _localHpLife, _localHpMaxLife, _localHpLif, _localHpBonusLife, _localHpRecover);
            return true;
        }
    }

    private static string BuildMobStatesLine(IReadOnlyList<MobStateSnapshot> states)
    {
        var sb = new StringBuilder("MOBSTATE|");
        if (states != null)
        {
            for (int i = 0; i < states.Count; i++)
            {
                var state = states[i];
                if (i > 0)
                    sb.Append(';');
                sb.Append(state.Index.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(state.X.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(state.Y.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(state.Dir.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(state.Life.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(state.MaxLife.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(state.AnimPayload ?? string.Empty);
                sb.Append(',');
                sb.Append(state.Type ?? string.Empty);
                sb.Append(',');
                sb.Append(state.StatePayload ?? string.Empty);
            }
        }
        sb.Append('\n');
        return sb.ToString();
    }

    private static string BuildMobAttackLine(MobAttack attack)
    {
        string encodedSkill;
        try
        {
            encodedSkill = Uri.EscapeDataString(attack.SkillId ?? string.Empty);
        }
        catch
        {
            encodedSkill = attack.SkillId ?? string.Empty;
        }

        var hasData = attack.Data.HasValue;
        var dataPart = attack.Data.GetValueOrDefault().ToString(CultureInfo.InvariantCulture);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"MOBATK|{attack.Index},{encodedSkill},{(attack.RequiresTargetInArea ? 1 : 0)},{(hasData ? 1 : 0)},{dataPart},{attack.X},{attack.Y},{attack.TargetUserId},{attack.Dir}\n");
    }

    private static string BuildMobDieLine(MobDie die)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"MOBDIE|{die.UserId}|{die.MobIndex}|{die.X}|{die.Y}\n");
    }

    private static string BuildMobDrawLine(int userId, int mobIndex, bool isOutOfGame, bool isOnScreen)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"MOBDRAW|{userId}|{mobIndex}|{(isOutOfGame ? 1 : 0)}|{(isOnScreen ? 1 : 0)}\n");
    }

    private static string BuildMobDrawLine(IReadOnlyList<MobDraw> draws)
    {
        var sb = new StringBuilder("MOBDRAW|");
        if (draws != null)
        {
            for (int i = 0; i < draws.Count; i++)
            {
                var draw = draws[i];
                if (i > 0)
                    sb.Append(';');

                sb.Append(draw.UserId.ToString(CultureInfo.InvariantCulture));
                sb.Append('|');
                sb.Append(draw.MobIndex.ToString(CultureInfo.InvariantCulture));
                sb.Append('|');
                sb.Append(draw.IsOutOfGame ? '1' : '0');
                sb.Append('|');
                sb.Append(draw.IsOnScreen ? '1' : '0');
            }
        }

        sb.Append('\n');
        return sb.ToString();
    }

    private static string BuildExitReadyLine(ExitReadyState state)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"EXITREADY|{state.UserId}|{state.DoorCx}|{state.DoorCy}|{(state.Pressed ? 1 : 0)}|{(state.InsideCircle ? 1 : 0)}|{(state.IsOutOfGame ? 1 : 0)}|{(state.IsOnScreen ? 1 : 0)}\n");
    }

    private static string BuildPlayerDownLine(PlayerDownState state)
    {
        var safeLevelId = (state.LevelId ?? string.Empty)
            .Replace("|", "/", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        var safeHeadAnim = (state.HeadAnim ?? string.Empty)
            .Replace("|", "/", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);

        if (state.HasHeadPosition)
        {
            if (state.HasHeadAnim && !string.IsNullOrWhiteSpace(safeHeadAnim))
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"PDOWN|{state.UserId}|{(state.IsDowned ? 1 : 0)}|{state.X}|{state.Y}|{safeLevelId}|{state.HeadX}|{state.HeadY}|{safeHeadAnim}\n");
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"PDOWN|{state.UserId}|{(state.IsDowned ? 1 : 0)}|{state.X}|{state.Y}|{safeLevelId}|{state.HeadX}|{state.HeadY}\n");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"PDOWN|{state.UserId}|{(state.IsDowned ? 1 : 0)}|{state.X}|{state.Y}|{safeLevelId}\n");
    }

    private static string BuildPlayerReviveLine(PlayerReviveRequest request)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"PREVIVE|{request.ReviverId}|{request.TargetId}\n");
    }

    private static string BuildPosLine(int id, double cx, double cy, int dir)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{id}|{cx}|{cy}|{dir}\n");
    }

    private void CloseClientConnection()
    {
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        _stream = null;
        _client = null;
    }

    private bool HasAnyConnection()
    {
        if (_role == NetRole.Host)
        {
            lock (_clientsLock)
            {
                return _useSteamTransport ? _steamClients.Count > 0 : _clients.Count > 0;
            }
        }
        if (_useSteamTransport)
        {
            lock (_sync) return _hasRemote;
        }

        return _stream != null && _client != null && _client.Connected;
    }

    private Task SendLineSafe(string line)
    {
        if (_role == NetRole.Host)
            return BroadcastLineSafe(line);

        if (_useSteamTransport)
            return SendLineToSteamPeerSafe(_steamHostId, _sendLock, line);

        return SendLineToStreamSafe(_stream, _sendLock, line);
    }

    private Task SendLineToClientSafe(ClientConnection client, string line)
    {
        return SendLineToStreamSafe(client.Stream, client.SendLock, line);
    }

    private async Task BroadcastLineSafe(string line)
    {
        if (_useSteamTransport)
        {
            List<SteamClientConnection> steamSnapshot;
            lock (_clientsLock)
            {
                steamSnapshot = _steamClients.Values.ToList();
            }
            if (steamSnapshot.Count == 0) return;
            var steamTasks = steamSnapshot.Select(client => SendLineToSteamClientSafe(client, line));
            await Task.WhenAll(steamTasks).ConfigureAwait(false);
            return;
        }

        List<ClientConnection> snapshot;
        lock (_clientsLock)
        {
            snapshot = _clients.Values.ToList();
        }
        if (snapshot.Count == 0) return;
        var tasks = snapshot.Select(client => SendLineToClientSafe(client, line));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task SendKnownUsersToClientSafe(ClientConnection connection)
    {
        List<RemoteState> snapshot;
        lock (_sync)
        {
            if (_remotes.Count == 0)
                return;
            snapshot = new List<RemoteState>(_remotes.Values);
        }

        foreach (var state in snapshot)
        {
            var username = state.Username;
            if (string.IsNullOrWhiteSpace(username))
                continue;
            var line = BuildTaggedLine("USER", state.Id, username);
            await SendLineToClientSafe(connection, line).ConfigureAwait(false);
        }
    }

    private async Task SendKnownUsersToSteamClientSafe(SteamClientConnection connection)
    {
        List<RemoteState> snapshot;
        lock (_sync)
        {
            if (_remotes.Count == 0)
                return;
            snapshot = new List<RemoteState>(_remotes.Values);
        }

        foreach (var state in snapshot)
        {
            var username = state.Username;
            if (string.IsNullOrWhiteSpace(username))
                continue;
            var line = BuildTaggedLine("USER", state.Id, username);
            await SendLineToSteamClientSafe(connection, line).ConfigureAwait(false);
        }
    }

    private async Task SendLineToStreamSafe(NetworkStream? stream, SemaphoreSlim? sendLock, string line)
    {
        if (stream == null || sendLock == null) return;

        var bytes = Encoding.UTF8.GetBytes(line);
        bool locked = false;
        try
        {
            await sendLock.WaitAsync().ConfigureAwait(false);
            locked = true;
            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), CancellationToken.None).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] send error: {msg}", ex.Message);
        }
        finally
        {
            if (locked) sendLock.Release();
        }
    }

    private Task SendLineToSteamClientSafe(SteamClientConnection client, string line)
    {
        return SendLineToSteamPeerSafe(client.SteamId, client.SendLock, line);
    }

    private async Task SendLineToSteamPeerSafe(CSteamID steamId, SemaphoreSlim? sendLock, string line)
    {
        if (sendLock == null || steamId.m_SteamID == 0UL)
            return;

        var bytes = Encoding.UTF8.GetBytes(line);
        bool locked = false;
        try
        {
            await sendLock.WaitAsync().ConfigureAwait(false);
            locked = true;
            var sent = SteamNetworking.SendP2PPacket(
                steamId,
                bytes,
                (uint)bytes.Length,
                EP2PSend.k_EP2PSendReliable,
                SteamP2PChannel);

            if (!sent)
                _log.Warning("[NetNode] Steam send failed to {SteamId}", steamId.m_SteamID);
        }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] Steam send error: {msg}", ex.Message);
        }
        finally
        {
            if (locked)
                sendLock.Release();
        }
    }

    public void TickSend(double cx, double cy, int dir)
    {
        if (!HasAnyConnection()) return;
        if (ID <= 0) return;
        var line = BuildPosLine(ID, cx, cy, dir);
        _ = SendLineSafe(line);
    }

    public void LevelSend(int senderId, string lvl) => SendLevelId(senderId, lvl);

    public void SendSeed(int seed)
    {
        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
            {
                _cachedHostSeed = seed;
            }
        }

        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending seed {Seed}: no connected client", seed);
            return;
        }
        var line = $"SEED|{seed}\n";
        _ = SendLineSafe(line);
        _log.Information("[NetNode] Sent seed {Seed}", seed);
    }

    public void SendSerializerSync(int seq, int uid)
    {
        if (_role != NetRole.Host)
            return;
        lock (_hostCacheSync)
        {
            _cachedHostSerializerSeq = seq;
            _cachedHostSerializerUid = uid;
        }
        if (!HasAnyConnection())
            return;

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"HXSYNC|{seq}|{uid}\n");
        _ = SendLineSafe(line);
    }

    public void SendCounters(string countersPayload)
    {
        var safeCounters = (countersPayload ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
            {
                _cachedHostCountersPayload = safeCounters;
            }
        }

        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending counters sync: no connected client");
            return;
        }

        SendRaw($"COUNTERS|{safeCounters}");
        _log.Information("[NetNode] Sent counters sync");
    }

    public void SendBlueprints(string blueprintsPayload)
    {
        var safeBlueprints = (blueprintsPayload ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
            {
                _cachedHostBlueprintsPayload = safeBlueprints;
            }
        }

        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending blueprints sync: no connected client");
            return;
        }

        SendRaw($"BLUEPRINTS|{safeBlueprints}");
        _log.Information("[NetNode] Sent blueprints sync");
    }

    public void SendUsername(string username)
    {
        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending username: no connected client");
            return;
        }

        var safe = (username ?? "guest").Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (safe.Length == 0) safe = "guest";

        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        SendRaw("USER|" + idPart + safe);
        _log.Information("[NetNode] Sent username {Username}", safe);
    }

    public void SendBossRune(int bossRune)
    {
        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
            {
                _cachedHostBossRune = bossRune;
            }
        }

        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending boss rune: no connected client");
            return;
        }

        var payload = bossRune.ToString(CultureInfo.InvariantCulture);
        SendRaw("BOSSRUNE|" + payload);
        _log.Information("[NetNode] Sent boss rune {BossRune}", bossRune);
    }

    public void SendLevelDesc(string json)
    {
        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending level desc: no connected client");
            return;
        }

        SendRaw("LDESC|" + json);
        _log.Information("[NetNode] Sent LevelDesc payload");
    }

    public void SendLevelSeed(string levelId, double seed)
    {
        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending level seed: no connected client");
            return;
        }

        var safeId = (levelId ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        SendRaw($"LSEED|{safeId}|{seed.ToString(CultureInfo.InvariantCulture)}");
        _log.Information("[NetNode] Sent level seed for {LevelId}", safeId);
    }

    public void SendLevelGraph(string json)
    {
        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending level graph: no connected client");
            lock (_hostCacheSync)
                _cachedHostLevelGraphPayload = string.IsNullOrWhiteSpace(json) ? null : json;
            return;
        }

        if (string.IsNullOrWhiteSpace(json))
            return;

        lock (_hostCacheSync)
            _cachedHostLevelGraphPayload = json;

        SendRaw("LGRAPH|" + json);
        _log.Information("[NetNode] Sent level graph ({Length} bytes)", json.Length);
    }

    public void SendGeneratePayload(string json)
    {
        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending generate payload: no connected client");
            return;
        }

        SendRaw("GEN|" + json);
        _log.Information("[NetNode] Sent Generate payload ({Length} bytes)", json.Length);
    }


    public void SendHP(double life, double maxLife, double lif, double bonusLife, double recover)
    {
        lock (_sync)
        {
            _localHpLife = (int)System.Math.Round(life, System.MidpointRounding.AwayFromZero);
            _localHpMaxLife = (int)System.Math.Round(maxLife, System.MidpointRounding.AwayFromZero);
            _localHpLif = (int)System.Math.Round(lif, System.MidpointRounding.AwayFromZero);
            _localHpBonusLife = (int)System.Math.Round(bonusLife, System.MidpointRounding.AwayFromZero);
            _localHpRecover = (int)System.Math.Round(recover, System.MidpointRounding.AwayFromZero);
            _hasLocalHpSnapshot = true;
        }

        if (!HasAnyConnection())
        {
            return;
        }
        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        SendRaw($"HP|{idPart}{life}|{maxLife}|{lif}|{bonusLife}|{recover}");
    }

    public void SendChatMessage(string message)
    {
        if (!HasAnyConnection())
            return;

        var safe = SanitizeChatMessage(message);
        if (string.IsNullOrWhiteSpace(safe))
            return;

        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        SendRaw($"CHAT|{idPart}{safe}");
    }

    public void SendLevelId(int senderId, string levelId)
    {
        if (!HasAnyConnection())
        {
            return;
        }

        var safe = levelId.Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        SendRaw($"LEVEL|{senderId}|{safe}");
    }

    public void SendRoomTarget(string levelId, int roomId)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0 || roomId < 0)
            return;

        var safe = (levelId ?? string.Empty)
            .Replace("|", "/", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(safe))
            return;

        SendRaw($"ZROOM|{ID}|{safe}|{roomId}");
    }

    public void SendKick()
    {
        if (!HasAnyConnection()) return;
        SendRaw("KICK");
    }


    public void SendHeadAnim(string anim)
    {
        if (!HasAnyConnection())
        {
            return;
        }

        var safe = (anim ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        SendRaw($"HEADANIM|{idPart}{safe}");
    }

    public void SendAnim(string anim, int? queueAnim = null, bool? g = null)
    {
        if (!HasAnyConnection())
        {
            return;
            
        }

        var safe = (anim ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (safe.Length == 0) safe = "idle";
        var queuePart = queueAnim.HasValue ? queueAnim.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        var gPart = g.HasValue ? (g.Value ? "1" : "0") : string.Empty;
        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        SendRaw($"ANIM|{idPart}{safe}|{queuePart}|{gPart}");
    }

    public void SendInventoryWeapon(string kind, int slot, int permanentId, int? ammo = null)
    {
        if (!HasAnyConnection())
        {
            return;
        }

        var safe = (kind ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (safe.Length == 0) return;

        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        if (ammo.HasValue)
            SendRaw($"INV|{idPart}{safe}|{slot}|{permanentId}|{ammo.Value}");
        else
            SendRaw($"INV|{idPart}{safe}|{slot}|{permanentId}");
    }

    public void SendAttack(string kind, int slot, int permanentId, int? ammo = null)
    {
        if (!HasAnyConnection())
        {
            return;
        }

        var safe = (kind ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (safe.Length == 0) return;

        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        if (ammo.HasValue)
            SendRaw($"ATK|{idPart}{safe}|{slot}|{permanentId}|{ammo.Value}");
        else
            SendRaw($"ATK|{idPart}{safe}|{slot}|{permanentId}");
    }

    public void SendHeroSkin(string skin)
    {
        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending hero skin: no connected client");
            return;
        }

        var safe = (skin ?? "PrisonerDefault").Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (string.IsNullOrWhiteSpace(safe))
            safe = "PrisonerDefault";

        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        SendRaw("SKIN|" + idPart + safe);
        _log.Information("[NetNode] Sent hero skin {Skin}", safe);
    }

    public void SendHeroHeadSkin(string skin)
    {
        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending hero skin: no connected client");
            return;
        }

        var safe = (skin ?? "PrisonerDefault").Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (string.IsNullOrWhiteSpace(safe))
            safe = "BaseFlame";

        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        SendRaw("HEAD|" + idPart + safe);
        _log.Information("[NetNode] Sent hero skin {Skin}", safe);
    }

    public void SendHeroDeath()
    {
        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending death: no connected client");
            return;
        }

        SendRaw("DIED");
        _log.Information("[NetNode] Sent hero death");
    }

    public void SendPlayerDownState(bool isDowned, double x, double y, string? levelId, double? headX = null, double? headY = null, string? headAnim = null)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        var hasHead = isDowned && headX.HasValue && headY.HasValue;
        var hasAnim = hasHead && !string.IsNullOrWhiteSpace(headAnim);
        var state = new PlayerDownState(ID, isDowned, x, y, levelId ?? string.Empty, hasHead, headX ?? 0, headY ?? 0, hasAnim, headAnim);
        var line = BuildPlayerDownLine(state);
        _ = SendLineSafe(line);
    }

    public void SendPlayerReviveRequest(int targetId)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0 || targetId <= 0)
            return;

        var request = new PlayerReviveRequest(ID, targetId);
        var line = BuildPlayerReviveLine(request);
        _ = SendLineSafe(line);
    }

    public void SendMobStates(IReadOnlyList<MobStateSnapshot> states)
    {
        if (_role != NetRole.Host && _role != NetRole.Client)
            return;
        if (!HasAnyConnection())
            return;
        if (states == null || states.Count == 0)
            return;

        var line = BuildMobStatesLine(states);
        _ = SendLineSafe(line);
    }

    public void SendMobAttack(int mobIndex, string skillId, bool requiresTargetInArea, int? data, double x, double y, int targetUserId, int dir = 0)
    {
        if (_role != NetRole.Host)
            return;
        if (!HasAnyConnection())
            return;
        if (mobIndex < 0 || string.IsNullOrWhiteSpace(skillId))
            return;

        var attack = new MobAttack(mobIndex, skillId, requiresTargetInArea, data, x, y, targetUserId, dir);
        var line = BuildMobAttackLine(attack);
        _ = SendLineSafe(line);
    }

    public void SendMobHit(int mobIndex, int hp, double x, double y)
    {
        if (_role != NetRole.Client)
            return;
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"MOBHIT|{ID}|{mobIndex}|{hp}|{x}|{y}");
        SendRaw(payload);
    }

    public void SendMobDie(int mobIndex, double x, double y)
    {
        if (_role != NetRole.Client && _role != NetRole.Host)
            return;
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"MOBDIE|{ID}|{mobIndex}|{x}|{y}");
        SendRaw(payload);
    }

    public void SendMobDraw(int mobIndex, bool isOutOfGame, bool isOnScreen)
    {
        if (_role != NetRole.Client)
            return;
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;
        if (mobIndex < 0)
            return;

        var line = BuildMobDrawLine(ID, mobIndex, isOutOfGame, isOnScreen);
        _ = SendLineSafe(line);
    }

    public void SendMobDrawBatch(IReadOnlyList<MobDraw> draws)
    {
        if (_role != NetRole.Client)
            return;
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;
        if (draws == null || draws.Count == 0)
            return;

        var line = BuildMobDrawLine(draws);
        _ = SendLineSafe(line);
    }

    public void SendExitReady(int doorCx, int doorCy, bool pressed, bool insideCircle, bool isOutOfGame, bool isOnScreen)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        var state = new ExitReadyState(ID, doorCx, doorCy, pressed, insideCircle, isOutOfGame, isOnScreen);
        var line = BuildExitReadyLine(state);
        _ = SendLineSafe(line);
    }

    private void SendRaw(string payload)
    {
        var line = payload.EndsWith('\n') ? payload : payload + "\n";
        _ = SendLineSafe(line);
    }

    public bool TryGetRemote(out int remoteId, out double rx, out double ry)
    {
        lock (_sync)
        {
            if (_primaryRemoteId != 0 && _remotes.TryGetValue(_primaryRemoteId, out var state) && state.HasRemote)
            {
                remoteId = state.Id;
                rx = state.X;
                ry = state.Y;
                return true;
            }
            remoteId = 0;
            rx = 0;
            ry = 0;
            return false;
        }
    }

    public bool TryConsumeRemoteSnapshot(out List<RemoteSnapshot> snapshot)
    {
        lock (_sync)
        {
            if (_remotes.Count == 0)
            {
                snapshot = new List<RemoteSnapshot>();
                return false;
            }

            snapshot = new List<RemoteSnapshot>(_remotes.Count);
            foreach (var state in _remotes.Values)
            {
                if (!state.HasRemote)
                    continue;

                var hasAnim = state.HasAnim;
                var hasHeadAnim = state.HasHeadAnim;
                var hasRoom = state.HasRoom;
                var anim = hasAnim ? state.Anim : null;
                var animQueue = hasAnim ? state.AnimQueue : null;
                var animG = hasAnim ? state.AnimG : null;
                var headAnim = hasHeadAnim ? state.HeadAnim : null;
                var roomLevelId = hasRoom ? state.RoomLevelId : null;
                var roomId = hasRoom ? state.RoomId : null;

                snapshot.Add(new RemoteSnapshot(
                    state.Id,
                    state.X,
                    state.Y,
                    state.Dir,
                    state.LevelId,
                    roomLevelId,
                    roomId,
                    hasRoom,
                    anim,
                    animQueue,
                    animG,
                    hasAnim,
                    state.Username,
                    headAnim,
                    hasHeadAnim));

                if (hasAnim)
                    state.HasAnim = false;
                if (hasHeadAnim)
                    state.HasHeadAnim = false;
            }

            return snapshot.Count > 0;
        }
    }

    public bool TryConsumeRemoteWeaponSnapshots(out List<RemoteWeaponSnapshot> snapshot)
    {
        lock (_sync)
        {
            if (_remotes.Count == 0)
            {
                snapshot = new List<RemoteWeaponSnapshot>();
                return false;
            }

            snapshot = new List<RemoteWeaponSnapshot>();
            foreach (var state in _remotes.Values)
            {
                if (!state.HasRemote || !state.HasWeaponUpdate)
                    continue;

                int? ammo = state.WeaponAmmo != int.MinValue ? state.WeaponAmmo : (int?)null;
                snapshot.Add(new RemoteWeaponSnapshot(state.Id, state.WeaponKind, state.WeaponSlot, state.WeaponPermanentId, ammo));
                state.HasWeaponUpdate = false;
            }

            return snapshot.Count > 0;
        }
    }

    public bool TryConsumeRemoteAttacks(out List<RemoteAttack> attacks)
    {
        lock (_sync)
        {
            if (_pendingAttacks.Count == 0)
            {
                attacks = new List<RemoteAttack>();
                return false;
            }

            attacks = new List<RemoteAttack>(_pendingAttacks);
            _pendingAttacks.Clear();
            return attacks.Count > 0;
        }
    }

    public bool TryConsumeChatMessages(out List<RemoteChatMessage> messages)
    {
        lock (_sync)
        {
            if (_pendingChatMessages.Count == 0)
            {
                messages = new List<RemoteChatMessage>();
                return false;
            }

            messages = new List<RemoteChatMessage>(_pendingChatMessages);
            _pendingChatMessages.Clear();
            return messages.Count > 0;
        }
    }

    public void ClearMobSyncQueues()
    {
        lock (_sync)
        {
            _pendingMobStates.Clear();
            _pendingMobHits.Clear();
            _pendingMobDies.Clear();
            _pendingMobAttacks.Clear();
            _pendingMobDraws.Clear();
        }
    }

    public bool TryConsumeMobStates(out List<MobStateSnapshot> snapshot)
    {
        lock (_sync)
        {
            if (_pendingMobStates.Count == 0)
            {
                snapshot = new List<MobStateSnapshot>();
                return false;
            }

            snapshot = new List<MobStateSnapshot>(_pendingMobStates);
            _pendingMobStates.Clear();
            return snapshot.Count > 0;
        }
    }

    public bool TryConsumeMobHits(out List<MobHit> hits)
    {
        lock (_sync)
        {
            if (_pendingMobHits.Count == 0)
            {
                hits = new List<MobHit>();
                return false;
            }

            hits = new List<MobHit>(_pendingMobHits);
            _pendingMobHits.Clear();
            return hits.Count > 0;
        }
    }

    public bool TryConsumeMobDies(out List<MobDie> dies)
    {
        lock (_sync)
        {
            if (_pendingMobDies.Count == 0)
            {
                dies = new List<MobDie>();
                return false;
            }

            dies = new List<MobDie>(_pendingMobDies);
            _pendingMobDies.Clear();
            return dies.Count > 0;
        }
    }

    public bool TryConsumeMobAttacks(out List<MobAttack> attacks)
    {
        lock (_sync)
        {
            if (_pendingMobAttacks.Count == 0)
            {
                attacks = new List<MobAttack>();
                return false;
            }

            attacks = new List<MobAttack>(_pendingMobAttacks);
            _pendingMobAttacks.Clear();
            return attacks.Count > 0;
        }
    }

    public bool TryConsumeMobDraws(out List<MobDraw> draws)
    {
        lock (_sync)
        {
            if (_pendingMobDraws.Count == 0)
            {
                draws = new List<MobDraw>();
                return false;
            }

            draws = new List<MobDraw>(_pendingMobDraws);
            _pendingMobDraws.Clear();
            return draws.Count > 0;
        }
    }

    public bool TryConsumeExitReadyStates(out List<ExitReadyState> states)
    {
        lock (_sync)
        {
            if (_pendingExitReadyStates.Count == 0)
            {
                states = new List<ExitReadyState>();
                return false;
            }

            states = new List<ExitReadyState>(_pendingExitReadyStates);
            _pendingExitReadyStates.Clear();
            return states.Count > 0;
        }
    }

    public bool TryConsumePlayerDownStates(out List<PlayerDownState> states)
    {
        lock (_sync)
        {
            if (_pendingPlayerDownStates.Count == 0)
            {
                states = new List<PlayerDownState>();
                return false;
            }

            states = new List<PlayerDownState>(_pendingPlayerDownStates);
            _pendingPlayerDownStates.Clear();
            return states.Count > 0;
        }
    }

    public bool TryConsumePlayerReviveRequests(out List<PlayerReviveRequest> requests)
    {
        lock (_sync)
        {
            if (_pendingPlayerReviveRequests.Count == 0)
            {
                requests = new List<PlayerReviveRequest>();
                return false;
            }

            requests = new List<PlayerReviveRequest>(_pendingPlayerReviveRequests);
            _pendingPlayerReviveRequests.Clear();
            return requests.Count > 0;
        }
    }

    public bool TryGetRemoteHpSnapshots(out List<RemoteHpSnapshot> snapshot)
    {
        lock (_sync)
        {
            if (_remotes.Count == 0)
            {
                snapshot = new List<RemoteHpSnapshot>();
                return false;
            }

            snapshot = new List<RemoteHpSnapshot>(_remotes.Count);
            foreach (var state in _remotes.Values)
            {
                if (!state.HasRemote)
                    continue;

                snapshot.Add(new RemoteHpSnapshot(state.Id, state.Life, state.MaxLife, state.Lif, state.BonusLife, state.Recover, state.Username));
            }

            return snapshot.Count > 0;
        }
    }

    public bool TryGetRemoteUserSnapshots(out List<RemoteUserSnapshot> snapshot)
    {
        lock (_sync)
        {
            if (_remotes.Count == 0)
            {
                snapshot = new List<RemoteUserSnapshot>();
                return false;
            }

            snapshot = new List<RemoteUserSnapshot>(_remotes.Count);
            foreach (var state in _remotes.Values)
            {
                if (!state.HasRemote)
                    continue;

                snapshot.Add(new RemoteUserSnapshot(state.Id, state.Username));
            }

            return snapshot.Count > 0;
        }
    }

    public bool TryGetRemoteLevelId(out string? levelId)
    {
        lock (_sync)
        {
            if (_primaryRemoteId != 0 && _remotes.TryGetValue(_primaryRemoteId, out var state))
            {
                levelId = state.LevelId;
                return state.HasRemote && !string.IsNullOrEmpty(levelId);
            }
            levelId = null;
            return false;
        }
    }

    public bool TryGetRemoteHP(out int life, out int maxLife, out int lif, out int bonusLife, out int recover)
    {
        lock (_sync)
        {
            if (_primaryRemoteId != 0 && _remotes.TryGetValue(_primaryRemoteId, out var state))
            {
                life = state.Life;
                maxLife = state.MaxLife;
                lif = state.Lif;
                bonusLife = state.BonusLife;
                recover = state.Recover;
                return state.HasRemote;
            }
            life = 0;
            maxLife = 0;
            lif = 0;
            bonusLife = 0;
            recover = 0;
            return false;
        }
    }

    public bool TryGetRemoteAnim(out string? anim, out int? queueAnim, out bool? g)
    {
        lock (_sync)
        {
            if (_primaryRemoteId != 0 && _remotes.TryGetValue(_primaryRemoteId, out var state))
            {
                if (!state.HasAnim)
                {
                    anim = null;
                    queueAnim = null;
                    g = null;
                    return false;
                }
                anim = state.Anim;
                queueAnim = state.AnimQueue;
                g = state.AnimG;
                state.HasAnim = false;
                return state.HasRemote && anim != null;
            }
            anim = null;
            queueAnim = null;
            g = null;
            return false;
        }
    }

    private static bool TryParseBool(string text, out bool value)
    {
        if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }
        if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }
        value = false;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        List<ClientConnection> clients;
        List<SteamClientConnection> steamClients;
        lock (_clientsLock)
        {
            clients = _clients.Values.ToList();
            steamClients = _steamClients.Values.ToList();
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
            try { SteamNetworking.CloseP2PSessionWithUser(steamClient.SteamId); } catch { }
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
            try { SteamNetworking.CloseP2PSessionWithUser(_steamHostId); } catch { }
        }

        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _steamTransportTask?.Wait(200); } catch { }
        GameDataSync.Seed = 0;
        lock (_hostCacheSync)
        {
            _cachedHostSeed = null;
            _cachedHostBossRune = null;
            _cachedHostSerializerSeq = null;
            _cachedHostSerializerUid = null;
            _cachedHostCountersPayload = null;
            _cachedHostBlueprintsPayload = null;
            _cachedHostLevelGraphPayload = null;
        }
        lock (_sync)
        {
            _remotes.Clear();
            _primaryRemoteId = 0;
            _hasRemote = false;
            _connectedClientCount = 0;
            _pendingAttacks.Clear();
            _pendingMobStates.Clear();
            _pendingMobHits.Clear();
            _pendingMobDies.Clear();
            _pendingMobAttacks.Clear();
            _pendingMobDraws.Clear();
            _pendingExitReadyStates.Clear();
            _pendingPlayerDownStates.Clear();
            _pendingPlayerReviveRequests.Clear();
        }
        _stream = null; _client = null; _listener = null;
        try { _sendLock.Dispose(); } catch { }
    }
}
