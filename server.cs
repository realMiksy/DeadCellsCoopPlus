using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using DeadCellsMultiplayerMod;
using HaxeProxy.Runtime;
using Serilog;
using dc;
using Serilog.Core;

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

    private sealed class RemoteState
    {
        public int Id { get; }
        public double X;
        public double Y;
        public int Dir = 1;
        public bool HasRemote;
        public string? LevelId;
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
        public readonly string? Anim;
        public readonly int? AnimQueue;
        public readonly bool? AnimG;
        public readonly bool HasAnim;
        public readonly string? Username;
        public readonly string? HeadAnim;
        public readonly bool HasHeadAnim;

        public RemoteSnapshot(int id, double x, double y, int dir, string? anim, int? animQueue, bool? animG, bool hasAnim, string? username, string? headAnim, bool hasHeadAnim)
        {
            Id = id;
            X = x;
            Y = y;
            Dir = dir;
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

        public RemoteWeaponSnapshot(int id, string? kind, int slot, int permanentId)
        {
            Id = id;
            Kind = kind;
            Slot = slot;
            PermanentId = permanentId;
        }
    }

    public readonly struct RemoteAttack
    {
        public readonly int Id;
        public readonly string? Kind;
        public readonly int Slot;
        public readonly int PermanentId;

        public RemoteAttack(int id, string? kind, int slot, int permanentId)
        {
            Id = id;
            Kind = kind;
            Slot = slot;
            PermanentId = permanentId;
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

    private TcpListener? _listener;   // host
    private TcpClient? _client;     // client
    private NetworkStream? _stream;
    private static int _connectedClientCount;

    private int ID;

    public int id => ID;

    private static readonly int[] ClientIds = { 2, 3, 4 };
    public static int MaxClientSlots => ClientIds.Length;
    public static int ConnectedClientCount => _connectedClientCount;

    private static readonly HashSet<int> UsedClientIds = new();

    private readonly object _clientsLock = new();
    private readonly Dictionary<int, ClientConnection> _clients = new();
    private readonly Dictionary<int, RemoteState> _remotes = new();
    private readonly List<RemoteAttack> _pendingAttacks = new();
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

    public bool HasRemote
    {
        get
        {
            if (_role == NetRole.Host)
            {
                lock (_clientsLock) return _clients.Count > 0;
            }
            lock (_sync) return _hasRemote;
        }
    }
    public bool IsAlive =>
        (_role == NetRole.Host && _listener != null) ||
        (_role == NetRole.Client && _client   != null);
    public bool IsHost => _role == NetRole.Host;

    public IPEndPoint? ListenerEndpoint =>
        _listener != null ? (IPEndPoint?)_listener.LocalEndpoint : null;

    public static NetNode CreateHost(ILogger log, IPEndPoint ep)  => new(log, NetRole.Host,  ep);
    public static NetNode CreateClient(ILogger log, IPEndPoint ep)=> new(log, NetRole.Client, ep);

    private NetNode(ILogger log, NetRole role, IPEndPoint ep)
    {
        _log  = log;
        _role = role;

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
                if (_role == NetRole.Host && GameDataSync.TryGetHostBossRune(out var hostBossRune))
                {
                    await SendLineToClientSafe(connection, $"BOSSRUNE|{hostBossRune}\n").ConfigureAwait(false);
                }
                if (_role == NetRole.Host && GameMenu.TryGetHostRunSeed(out var hostSeed))
                {
                    await SendLineToClientSafe(connection, $"SEED|{hostSeed}\n").ConfigureAwait(false);
                }

                GameMenu.EnqueueMainThread(() =>
                {
                    GameMenu.NetRef = this;
                    GameMenu.SetRole(_role);
                    GameMenu.NotifyRemoteConnected(_role);
                });
                await SendLineToClientSafe(connection, $"ID|{assignedId}\n").ConfigureAwait(false);
                await SendKnownUsersToClientSafe(connection).ConfigureAwait(false);

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
        var buf = new byte[2048];
        var sb = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
                if (n <= 0) break;

                sb.Append(Encoding.UTF8.GetString(buf, 0, n));

                while (true)
                {
                    var text = sb.ToString();
                    int idx = text.IndexOf('\n');
                    if (idx < 0) break;

                    var line = text[..idx].Trim();
                    sb.Remove(0, idx + 1);
                    if (line.Length == 0) continue;

                    if (!HandleLine(line, senderId, out var forwardLine))
                    {
                        return;
                    }

                    if (_role == NetRole.Host && sender != null && forwardLine != null)
                    {
                        ForwardLineToOtherClients(sender, forwardLine);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] RecvLoop error: {msg}", ex.Message);
        }
        finally
        {
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

        if (line.StartsWith("GENCFG|"))
        {
            var payload = line["GENCFG|".Length..];
            lock (_sync) _hasRemote = true;
            GameDataSync.ReceiveRoomGenConfig(payload);
            return true;
        }

        if (line.StartsWith("LORE|"))
        {
            var payload = line["LORE|".Length..];
            lock (_sync) _hasRemote = true;
            GameDataSync.ReceiveLoreRequirement(payload);
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

        if (line.StartsWith("LDESC|"))
        {
            var payload = line["LDESC|".Length..];
            lock (_sync) _hasRemote = true;
            GameMenu.ReceiveLevelDesc(payload);
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

                try
                {
                    ModEntry.SetClientSkin(effectiveId.Value, skin);
                    if (effectiveId.Value == primaryId)
                        GameDataSync.ReceiveHeroSkin(skin);
                }
                catch (Exception ex)
                {
                    _log.Warning("[NetNode] Failed to handle hero skin: {msg}", ex.Message);
                }

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

                try
                {
                    ModEntry.SetClientHeadSkin(effectiveId.Value, skinHead);
                    if (effectiveId.Value == primaryId)
                        GameDataSync.ReceiveHeroHeadSkin(skinHead);
                }
                catch (Exception ex)
                {
                    _log.Warning("[NetNode] Failed to handle hero skin: {msg}", ex.Message);
                }

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
            ParseWeaponPayload(payload, out var parsedId, out var kind, out var slot, out var permanentId);
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
                    state.HasWeaponUpdate = true;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildWeaponLine("INV", effectiveId.Value, kind, slot, permanentId);
            }
            return true;
        }

        if (line.StartsWith("ATK|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line[(line.IndexOf('|') + 1)..];
            ParseWeaponPayload(payload, out var parsedId, out var kind, out var slot, out var permanentId);
            var effectiveId = parsedId ?? senderId;
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    _pendingAttacks.Add(new RemoteAttack(effectiveId.Value, kind, slot, permanentId));
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildWeaponLine("ATK", effectiveId.Value, kind, slot, permanentId);
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

        if (line.StartsWith("DIED", StringComparison.OrdinalIgnoreCase))
        {
            if (_role == NetRole.Host)
            {
                var _remoteId = senderId ?? 0;
                _log.Information("[NetNode] Remote hero died (id {Id})", _remoteId);
                var reason = _remoteId > 0 ? $"client {_remoteId} died" : "client died";
                GameMenu.QueueHostRestartFromDeath(reason);
            }
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

    private void CleanupClient()
    {
        lock (_sync)
        {
            _hasRemote = false;
            _connectedClientCount = 0;
            _remotes.Clear();
            _primaryRemoteId = 0;
            _pendingAttacks.Clear();
        }
        CloseClientConnection();
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

    private static void ParseWeaponPayload(string payload, out int? parsedId, out string kind, out int slot, out int permanentId)
    {
        parsedId = null;
        kind = string.Empty;
        slot = -1;
        permanentId = 0;

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

    private static string BuildWeaponLine(string tag, int id, string kind, int slot, int permanentId)
    {
        return $"{tag}|{id}|{kind}|{slot}|{permanentId}\n";
    }

    private static string BuildHpLine(int id, int life, int maxLife, int lif, int bonusLife, int recover)
    {
        return $"HP|{id}|{life}|{maxLife}|{lif}|{bonusLife}|{recover}\n";
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
            lock (_clientsLock) return _clients.Count > 0;
        }
        return _stream != null && _client != null && _client.Connected;
    }

    private Task SendLineSafe(string line)
    {
        if (_role == NetRole.Host)
            return BroadcastLineSafe(line);

        return SendLineToStreamSafe(_stream, _sendLock, line);
    }

    private Task SendLineToClientSafe(ClientConnection client, string line)
    {
        return SendLineToStreamSafe(client.Stream, client.SendLock, line);
    }

    private async Task BroadcastLineSafe(string line)
    {
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
        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending seed {Seed}: no connected client", seed);
            return;
        }
        var line = $"SEED|{seed}\n";
        _ = SendLineSafe(line);
        _log.Information("[NetNode] Sent seed {Seed}", seed);
    }

    public void SendRoomGenConfig(bool disableLoreRooms, int fixedSeed)
    {
        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending room gen config: no connected client");
            return;
        }

        var disablePart = disableLoreRooms ? "1" : "0";
        SendRaw($"GENCFG|{disablePart}|{fixedSeed}");
        _log.Information("[NetNode] Sent room gen config");
    }

    public void SendLoreRequirement(string levelId, bool required)
    {
        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending lore requirement: no connected client");
            return;
        }
        if (string.IsNullOrWhiteSpace(levelId))
            return;

        var requiredPart = required ? "1" : "0";
        SendRaw($"LORE|{levelId}|{requiredPart}");
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
        if (!HasAnyConnection())
        {
            return;
        }
        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        SendRaw($"HP|{idPart}{life}|{maxLife}|{lif}|{bonusLife}|{recover}");
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

    public void SendInventoryWeapon(string kind, int slot, int permanentId)
    {
        if (!HasAnyConnection())
        {
            return;
        }

        var safe = (kind ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (safe.Length == 0) return;

        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        SendRaw($"INV|{idPart}{safe}|{slot}|{permanentId}");
    }

    public void SendAttack(string kind, int slot, int permanentId)
    {
        if (!HasAnyConnection())
        {
            return;
        }

        var safe = (kind ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (safe.Length == 0) return;

        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
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
                var anim = hasAnim ? state.Anim : null;
                var animQueue = hasAnim ? state.AnimQueue : null;
                var animG = hasAnim ? state.AnimG : null;
                var headAnim = hasHeadAnim? state.HeadAnim : null;

                snapshot.Add(new RemoteSnapshot(state.Id, state.X, state.Y, state.Dir, anim, animQueue, animG, hasAnim, state.Username, headAnim, hasHeadAnim));

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

                snapshot.Add(new RemoteWeaponSnapshot(state.Id, state.WeaponKind, state.WeaponSlot, state.WeaponPermanentId));
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
        lock (_clientsLock)
        {
            clients = _clients.Values.ToList();
            _clients.Clear();
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
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
        GameDataSync.Seed = 0;
        lock (_sync)
        {
            _remotes.Clear();
            _primaryRemoteId = 0;
            _hasRemote = false;
            _connectedClientCount = 0;
        }
        _stream = null; _client = null; _listener = null;
        try { _sendLock.Dispose(); } catch { }
    }
}
