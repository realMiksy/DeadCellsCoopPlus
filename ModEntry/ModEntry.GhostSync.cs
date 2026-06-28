using System.Diagnostics;
using dc.pr;
using ModCore.Utilities;
using dc.tool;
using HaxeProxy.Runtime;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using DeadCellsMultiplayerMod.KingHead;
using DeadCellsMultiplayerMod.Tools;

namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry
    {
        private void UpdateGhostHeads()
        {
            var hitchStart = RuntimeHitchWatch.Start();
            var main = dc.Main.Class.ME;
            if (main == null || main.user == null)
            {
                return;
            }
            var ftime = dc.pr.Game.Class.ME.ftime;
            var now = Stopwatch.GetTimestamp();
            var activeClients = 0;
            var recreatedHeads = 0;
            var updatedHeadFx = 0;
            var throttledHeads = 0;
            for (int i = 0; i < clientHeads.Length; i++)
            {
                var client = clients[i];
                if (client == null)
                {
                    pendingClientHeadRecreate[i] = false;
                    ResetGhostHeadRuntimeState(i);
                    continue;
                }
                activeClients++;

                var attemptedRecreate = false;
                if (pendingClientHeadRecreate[i] && now >= clientNextHeadRecreateTick[i])
                {
                    attemptedRecreate = true;
                    var recreateStart = RuntimeHitchWatch.Start();
                    RecreateClientHead(i);
                    if (clientHeads[i] != null)
                        recreatedHeads++;
                    LogGhostRuntimeStepIfSlow(
                        "ModEntry.UpdateGhostHeads.RecreateClientHead",
                        recreateStart,
                        string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"slot={i} remoteId={clientIds[i]} pending={CountPendingClientHeadRecreate()}"));
                }

                var head = clientHeads[i];
                if (head == null)
                {
                    var hasKnownHead = !string.IsNullOrWhiteSpace(client.RemoteHeadSkinId) ||
                                       !string.IsNullOrWhiteSpace(clientHeadSkins[i]);
                    if (!attemptedRecreate &&
                        (pendingClientHeadRecreate[i] || hasKnownHead) &&
                        now >= clientNextHeadRecreateTick[i])
                    {
                        var recreateStart = RuntimeHitchWatch.Start();
                        RecreateClientHead(i);
                        if (clientHeads[i] != null)
                            recreatedHeads++;
                        LogGhostRuntimeStepIfSlow(
                            "ModEntry.UpdateGhostHeads.RecreateClientHead",
                            recreateStart,
                            string.Create(
                                System.Globalization.CultureInfo.InvariantCulture,
                                $"slot={i} remoteId={clientIds[i]} pending={CountPendingClientHeadRecreate()}"));
                    }
                    continue;
                }

                if (!ShouldUpdateGhostHead(i, client, now))
                {
                    throttledHeads++;
                    continue;
                }

                var fxStart = RuntimeHitchWatch.Start();
                head.updateHeadFx(ftime);
                clientHeadDirty[i] = false;
                clientNextHeadFxTick[i] = IsGhostHeadHighPriority(client)
                    ? 0
                    : now + (long)(Stopwatch.Frequency * GhostHeadDormantUpdateSeconds);
                updatedHeadFx++;
                LogGhostRuntimeStepIfSlow(
                    "ModEntry.UpdateGhostHeads.HeadFx",
                    fxStart,
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"slot={i} remoteId={clientIds[i]}"));
            }

            var hitchMs = RuntimeHitchWatch.GetElapsedMilliseconds(hitchStart);
            if (hitchMs >= RuntimeHitchWatch.GhostRuntimeSlowThresholdMs)
            {
                RuntimeHitchWatch.LogSlow(
                    Logger,
                    "ModEntry.UpdateGhostHeads",
                    hitchMs,
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"activeClients={activeClients} updatedHeadFx={updatedHeadFx} recreatedHeads={recreatedHeads} throttledHeads={throttledHeads} pendingRecreate={CountPendingClientHeadRecreate()}"));
            }
        }

        private static void ResetGhostHeadRuntimeState(int slot)
        {
            if (slot < 0 || slot >= clientHeadDirty.Length)
                return;

            clientHeadDirty[slot] = false;
            clientNextHeadFxTick[slot] = 0;
            clientNextHeadRecreateTick[slot] = 0;
        }

        private static void MarkGhostHeadDirty(int slot, bool immediate)
        {
            if (slot < 0 || slot >= clientHeadDirty.Length)
                return;

            clientHeadDirty[slot] = true;
            if (immediate)
                clientNextHeadFxTick[slot] = 0;
        }

        private void ScheduleGhostHeadRecreate(int slot, bool immediate)
        {
            if (slot < 0 || slot >= pendingClientHeadRecreate.Length)
                return;

            pendingClientHeadRecreate[slot] = true;
            clientNextHeadRecreateTick[slot] = immediate
                ? 0
                : Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * GhostHeadRecreateRetrySeconds);
            MarkGhostHeadDirty(slot, immediate: true);
        }

        private static bool IsGhostHeadHighPriority(GhostKing client)
        {
            if (client == null)
                return false;

            try
            {
                return client.visible && client.isOnScreen && !client.isOutOfGame;
            }
            catch
            {
                return false;
            }
        }

        private static bool ShouldUpdateGhostHead(int slot, GhostKing client, long now)
        {
            if (slot < 0 || slot >= clientHeadDirty.Length)
                return false;

            if (IsGhostHeadHighPriority(client))
                return true;

            return clientHeadDirty[slot] || now >= clientNextHeadFxTick[slot];
        }



        private void SendLevel(string lvl)
        {
            if (_netRole == NetRole.None) return;
            var net = _net;
            if (net == null) return;

            int senderId = net.id;
            if (senderId <= 0) return;
            net.LevelSend(senderId, lvl);
        }

        private void SendRoomTarget(string? targetLevelId, int targetRoomId, bool force)
        {
            if (_netRole == NetRole.None)
                return;

            var net = _net;
            if (net == null || net.id <= 0)
                return;
            if (targetRoomId < 0)
                return;

            var effectiveLevelId = string.IsNullOrWhiteSpace(targetLevelId)
                ? GetCurrentLevelId()
                : targetLevelId.Trim();
            if (string.IsNullOrWhiteSpace(effectiveLevelId))
                return;

            if (!force &&
                string.Equals(_lastDoorMarkerLevelId, effectiveLevelId, StringComparison.Ordinal) &&
                _lastDoorMarkerToken == targetRoomId)
            {
                return;
            }

            net.SendRoomTarget(effectiveLevelId, targetRoomId);
            _lastDoorMarkerLevelId = effectiveLevelId;
            _lastDoorMarkerToken = targetRoomId;
        }

        private void SendCurrentRoomTarget(bool force)
        {
            if (!TryGetCurrentVisibilityContext(out var targetLevelId, out var branchToken))
                return;

            RegisterLocalDoorMarker(targetLevelId, branchToken);
            SendRoomTarget(targetLevelId, branchToken, force);
        }

        private bool TryGetCurrentVisibilityContext(out string levelContextId, out int branchToken)
        {
            levelContextId = GetCurrentLevelId();
            branchToken = 0;

            Level? currentLevel = me?._level;
            if (currentLevel == null)
                currentLevel = game?.curLevel;

            if (currentLevel == null)
                return !string.IsNullOrWhiteSpace(levelContextId);

            var liveLevelId = currentLevel.map?.id?.ToString();
            if (!string.IsNullOrWhiteSpace(liveLevelId))
                levelContextId = liveLevelId.Trim();

            if (string.IsNullOrWhiteSpace(levelContextId))
                return false;

            branchToken = ComputeLevelBranchToken(currentLevel, levelContextId);
            return branchToken >= 0;
        }

        private int ComputeLevelBranchToken(Level currentLevel, string levelContextId)
        {
            try
            {
                if (!currentLevel.isSubLevel)
                    return 0;
            }
            catch
            {
                return 0;
            }

            unchecked
            {
                try
                {
                    var ownerGame = currentLevel.game ?? game;
                    var subLevels = ownerGame?.subLevels;
                    if (subLevels == null)
                        return ComputeStablePositiveToken($"SUB|{levelContextId}");

                    int targetUid;
                    try
                    {
                        targetUid = currentLevel.__uid;
                    }
                    catch
                    {
                        return ComputeStablePositiveToken($"SUB|{levelContextId}");
                    }

                    for (int i = 0; i < subLevels.length; i++)
                    {
                        try
                        {
                            if (subLevels.getDyn(i) is not Level candidate)
                                continue;

                            if (ReferenceEquals(candidate, currentLevel))
                                return i + 1;

                            if (candidate.__uid == targetUid)
                                return i + 1;
                        }
                        catch
                        {
                            continue;
                        }
                    }
                }
                catch
                {
                    return ComputeStablePositiveToken($"SUB|{levelContextId}");
                }

                return ComputeStablePositiveToken($"SUB|{levelContextId}");
            }
        }

        private static int ComputeStablePositiveToken(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return 0;

            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < key.Length; i++)
                {
                    hash ^= key[i];
                    hash *= 16777619;
                }

                var positive = (int)(hash & 0x7FFFFFFF);
                return positive == 0 ? 1 : positive;
            }
        }

        private void RegisterLocalDoorMarker(string? levelId, int markerToken)
        {
            if (markerToken < 0)
                return;

            _localLastDoorMarkerLevelId = string.IsNullOrWhiteSpace(levelId)
                ? string.Empty
                : levelId.Trim();
            _localLastDoorMarkerToken = markerToken;
        }

        double last_x, last_y;
        int lastDir;

        private void SendHeroCoords()
        {
            if (_netRole == NetRole.None) return;
            if (_net == null || me == null) return;
            int dir = me.dir;
            if (me.spr.x == last_x && me.spr.y == last_y && lastDir == dir) return;

            _net.TickSend(me.spr.x, me.spr.y, dir);
            last_x = me.spr.x;
            last_y = me.spr.y;
            lastDir = dir;
        }

        public static double[] rLastX = new double[NetNode.MaxClientSlots];
        public static double[] rLastY = new double[NetNode.MaxClientSlots];
        public static double[] EmergencyLastRemoteX = new double[NetNode.MaxClientSlots];
        public static double[] EmergencyLastRemoteY = new double[NetNode.MaxClientSlots];
        public static long[] EmergencyLastRemoteTicks = new long[NetNode.MaxClientSlots];

        internal static bool TryGetClientIndex(int localId, int remoteId, out int index)
        {
            index = -1;
            if (localId <= 0 || remoteId <= 0 || remoteId == localId)
                return false;

            var mapped = remoteId < localId ? remoteId - 1 : remoteId - 2;
            if (mapped < 0 || mapped >= clients.Length)
                return false;

            index = mapped;
            return true;
        }

        internal static void SetClientSkin(int remoteId, string? skin)
        {
            var instance = Instance;
            if (instance == null)
                return;

            var net = _net;
            var localId = net?.id ?? 0;
            if (!TryGetClientIndex(localId, remoteId, out var index))
                return;

            var cleaned = NormalizeSkin(skin, "PrisonerDefault");
            var prev = clientSkins[index];
            clientSkins[index] = cleaned;

            var client = clients[index];
            if (client != null)
            {
                if (!string.Equals(prev, cleaned, StringComparison.Ordinal) || client.spr == null)
                    client.ApplyRemoteSkin(cleaned);
            }
        }

        internal static void SetClientHeadSkin(int remoteId, string? skin)
        {
            var instance = Instance;
            if (instance == null)
                return;

            var net = _net;
            var localId = net?.id ?? 0;
            if (!TryGetClientIndex(localId, remoteId, out var index))
                return;

            var cleaned = NormalizeSkin(skin, "BaseFlame");
            var prev = clientHeadSkins[index];
            clientHeadSkins[index] = cleaned;

            var client = clients[index];
            if (client != null)
                client.RemoteHeadSkinId = cleaned;

            if (!string.Equals(prev, cleaned, StringComparison.Ordinal) || client?.head == null)
                instance.ScheduleGhostHeadRecreate(index, immediate: true);
        }

        private static string NormalizeSkin(string? skin, string defaultSkin)
        {
            return string.IsNullOrWhiteSpace(skin) ? defaultSkin : skin.Replace("|", "/").Trim();
        }

        private void RecreateClientHead(int slot)
        {
            var hitchStart = RuntimeHitchWatch.Start();
            if (slot < 0 || slot >= clients.Length)
                return;

            var client = clients[slot];
            var localHero = me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            var localLevel = localHero?._level;
            if (client == null || localHero == null || localLevel == null || client.spr == null)
            {
                ScheduleGhostHeadRecreate(slot, immediate: false);
                return;
            }

            var existing = clientHeads[slot];
            var hadExisting = existing != null;
            if (existing != null)
            {
                existing.dispose();
                clientHeads[slot] = null;
            }

            var desiredHead = NormalizeSkin(client.RemoteHeadSkinId, "BaseFlame");
            var previousGlobalHead = remoteHeadSkin;
            remoteHeadSkin = desiredHead;
            try
            {
                bool fromUI = false;
                var attachRoot = new dc.h2d.Object(client.spr);
                var newHead = new Kinghead(localHero, client, localLevel);
                newHead.init(localLevel, attachRoot, Ref<bool>.From(ref fromUI));
                clientHeads[slot] = newHead;
                client.head = newHead;
                pendingClientHeadRecreate[slot] = false;
                clientNextHeadRecreateTick[slot] = 0;
                MarkGhostHeadDirty(slot, immediate: true);
            }
            finally
            {
                remoteHeadSkin = previousGlobalHead;
            }

            LogGhostRuntimeStepIfSlow(
                "ModEntry.RecreateClientHead",
                hitchStart,
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"slot={slot} remoteId={clientIds[slot]} hadExisting={(hadExisting ? 1 : 0)} desiredHead={desiredHead}"));
        }

        private void ReceiveGhostCoords()
        {
            var hitchStart = RuntimeHitchWatch.Start();
            var net = _net;
            var ghost = _ghost;
            if (net == null || me == null || ghost == null) return;

            if (!net.TryConsumeRemoteSnapshot(out var remotes))
                return;

            try
            {
                var localId = net.id;
                var localLevelId = GetCurrentLevelId();
                if (string.IsNullOrWhiteSpace(localLevelId))
                    localLevelId = me._level?.map?.id?.ToString() ?? string.Empty;

                var createdSlots = 0;
                var updatedLabels = 0;
                var playedAnims = 0;
                var playedHeadAnims = 0;
                var disposedSlots = 0;

                foreach (var remote in remotes)
                {
                    var remoteStart = RuntimeHitchWatch.Start();
                    if (!TryGetClientIndex(localId, remote.Id, out var index))
                        continue;

                    remotePlayerId = remote.Id;
                    clientIds[index] = remote.Id;
                    EmergencyLastRemoteX[index] = remote.X;
                    EmergencyLastRemoteY[index] = remote.Y;
                    EmergencyLastRemoteTicks[index] = Stopwatch.GetTimestamp();
                    ProcessRemoteDoorMarker(remote);
                    if (!ShouldKeepRemoteKingVisibleInRoom(remote, localLevelId))
                    {
                        QueueClientDisposeWithTransition(index);
                        disposedSlots++;
                        LogGhostRuntimeStepIfSlow(
                            "ModEntry.ReceiveGhostCoords.Remote",
                            remoteStart,
                            string.Create(
                                System.Globalization.CultureInfo.InvariantCulture,
                                $"remoteId={remote.Id} slot={index} disposed=1 anim={(remote.HasAnim ? 1 : 0)} headAnim={(remote.HasHeadAnim ? 1 : 0)}"));
                        continue;
                    }

                    CancelPendingClientDispose(index);

                    var hadClientBefore = clients[index] != null;
                    var client = EnsureClientKingSlot(index);
                    if (client == null)
                        continue;
                    if (!hadClientBefore)
                    {
                        createdSlots++;
                        MarkGhostHeadDirty(index, immediate: true);
                    }

                    var drawX = remote.X;
                    var drawY = remote.Y - 0.2d;
                    var useDownedOffset = false;
                    var headDirty = !hadClientBefore;
                    if (_remoteDowned.TryGetValue(remote.Id, out var downed))
                    {
                        var currentLevelId = GetCurrentLevelId();
                        if (string.IsNullOrEmpty(currentLevelId) ||
                            string.IsNullOrEmpty(downed.LevelId) ||
                            string.Equals(currentLevelId, downed.LevelId, StringComparison.Ordinal))
                        {
                            drawX = downed.X;
                            drawY = downed.Y;
                            useDownedOffset = true;
                            if (_remoteDownedCines.TryGetValue(remote.Id, out var downedCine) &&
                                downedCine != null &&
                                !downedCine.destroyed)
                            {
                                downedCine.UpdateTarget(drawX, drawY, remote.Dir);
                            }
                        }
                    }

                    if (useDownedOffset)
                        drawY -= DownedGhostBodyYOffsetPx;

                    if (clientLastDownedOffsets[index] != useDownedOffset)
                    {
                        client._targetable = !useDownedOffset;
                        clientLastDownedOffsets[index] = useDownedOffset;
                        headDirty = true;
                    }

                    if (rLastX[index] != drawX || rLastY[index] != drawY)
                    {
                        client.setPosPixel(drawX, drawY);
                        rLastX[index] = drawX;
                        rLastY[index] = drawY;
                        headDirty = true;
                    }

                    if (clientLastDirs[index] != remote.Dir)
                    {
                        client.dir = remote.Dir;
                        clientLastDirs[index] = remote.Dir;
                        headDirty = true;
                    }

                    var newLabel = BuildRemoteLabel(remote.Id, remote.Username);
                    if (!string.Equals(clientLabels[index], newLabel, StringComparison.Ordinal))
                    {
                        var labelStart = RuntimeHitchWatch.Start();
                        ghost.SetLabel(client, newLabel);
                        clientLabels[index] = newLabel;
                        updatedLabels++;
                        LogGhostRuntimeStepIfSlow(
                            "ModEntry.ReceiveGhostCoords.SetLabel",
                            labelStart,
                            string.Create(
                                System.Globalization.CultureInfo.InvariantCulture,
                                $"remoteId={remote.Id} slot={index} label={newLabel}"));
                    }

                    if (remote.HasAnim &&
                        !string.IsNullOrWhiteSpace(remote.Anim) &&
                        (!string.Equals(clientLastBodyAnims[index], remote.Anim, StringComparison.Ordinal) ||
                         clientLastBodyAnimQueues[index] != remote.AnimQueue ||
                         clientLastBodyAnimGs[index] != remote.AnimG))
                    {
                        var animStart = RuntimeHitchWatch.Start();
                        PlayGhostAnim(client, remote.Anim!, remote.AnimQueue, remote.AnimG);
                        clientLastBodyAnims[index] = remote.Anim;
                        clientLastBodyAnimQueues[index] = remote.AnimQueue;
                        clientLastBodyAnimGs[index] = remote.AnimG;
                        playedAnims++;
                        headDirty = true;
                        LogGhostRuntimeStepIfSlow(
                            "ModEntry.ReceiveGhostCoords.PlayGhostAnim",
                            animStart,
                            string.Create(
                                System.Globalization.CultureInfo.InvariantCulture,
                                $"remoteId={remote.Id} slot={index} anim={remote.Anim}"));
                    }
                    if (remote.HasHeadAnim &&
                        !string.IsNullOrWhiteSpace(remote.HeadAnim) &&
                        !string.Equals(clientLastHeadAnims[index], remote.HeadAnim, StringComparison.Ordinal))
                    {
                        var headAnimStart = RuntimeHitchWatch.Start();
                        PlayGhostHeadAnim(client, remote.HeadAnim);
                        clientLastHeadAnims[index] = remote.HeadAnim;
                        playedHeadAnims++;
                        headDirty = true;
                        LogGhostRuntimeStepIfSlow(
                            "ModEntry.ReceiveGhostCoords.PlayGhostHeadAnim",
                            headAnimStart,
                            string.Create(
                                System.Globalization.CultureInfo.InvariantCulture,
                                $"remoteId={remote.Id} slot={index} anim={remote.HeadAnim}"));
                    }

                    LogGhostRuntimeStepIfSlow(
                        "ModEntry.ReceiveGhostCoords.Remote",
                        remoteStart,
                        string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"remoteId={remote.Id} slot={index} created={(hadClientBefore ? 0 : 1)} downed={(useDownedOffset ? 1 : 0)} anim={(remote.HasAnim ? 1 : 0)} headAnim={(remote.HasHeadAnim ? 1 : 0)}"));

                    if (headDirty)
                        MarkGhostHeadDirty(index, immediate: true);
                }

                var hitchMs = RuntimeHitchWatch.GetElapsedMilliseconds(hitchStart);
                if (hitchMs >= RuntimeHitchWatch.GhostRuntimeSlowThresholdMs)
                {
                    RuntimeHitchWatch.LogSlow(
                        Logger,
                        "ModEntry.ReceiveGhostCoords",
                        hitchMs,
                        string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"remotes={remotes.Count} createdSlots={createdSlots} updatedLabels={updatedLabels} playedAnims={playedAnims} playedHeadAnims={playedHeadAnims} disposedSlots={disposedSlots}"));
                }
            }
            finally
            {
                NetNode.ReleaseConsumedList(remotes);
            }
        }

        private bool ShouldKeepRemoteKingVisibleInRoom(NetNode.RemoteSnapshot remote, string localLevelId)
        {
            if (!string.IsNullOrWhiteSpace(localLevelId) &&
                !string.IsNullOrWhiteSpace(remote.LevelId) &&
                !string.Equals(remote.LevelId, localLevelId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!remote.HasRoom ||
                !remote.RoomId.HasValue ||
                remote.RoomId.Value < 0 ||
                string.IsNullOrWhiteSpace(remote.RoomLevelId))
            {
                return true;
            }

            if (!TryGetCurrentVisibilityContext(out var localContextLevelId, out var localBranchToken))
            {
                localContextLevelId = localLevelId;
                localBranchToken = _localLastDoorMarkerToken >= 0 ? _localLastDoorMarkerToken : 0;
            }

            var remoteContextLevelId = remote.RoomLevelId.Trim();
            if (!string.Equals(remoteContextLevelId, localContextLevelId, StringComparison.Ordinal))
                return false;

            // Same main level should keep the remote visible even if a stale door/room marker is
            // temporarily different after forced exit assist or teleporter transitions.  Only use
            // the room-token hide when the local player is actually inside a sublevel/branch.
            if (!IsLocalHeroInSubLevel())
                return true;

            if (remote.RoomId.Value != localBranchToken)
                return false;

            return true;
        }

        private static bool IsLocalHeroInSubLevel()
        {
            try
            {
                var level = me?._level ?? ModCore.Modules.Game.Instance?.HeroInstance?._level;
                return level != null && level.isSubLevel;
            }
            catch
            {
                return false;
            }
        }

        private void ProcessRemoteDoorMarker(NetNode.RemoteSnapshot remote)
        {
            if (!remote.HasRoom ||
                !remote.RoomId.HasValue ||
                remote.RoomId.Value < 0 ||
                string.IsNullOrWhiteSpace(remote.RoomLevelId))
            {
                return;
            }

            var markerToken = remote.RoomId.Value;
            var markerLevelId = remote.RoomLevelId.Trim();
            if (string.IsNullOrWhiteSpace(markerLevelId))
                return;

            if (_remoteLastDoorMarkers.TryGetValue(remote.Id, out var last) &&
                last != null &&
                last.MarkerToken == markerToken &&
                string.Equals(last.LevelId, markerLevelId, StringComparison.Ordinal))
            {
                return;
            }

            _remoteLastDoorMarkers[remote.Id] = new RemoteDoorMarkerState
            {
                MarkerToken = markerToken,
                LevelId = markerLevelId,
                UpdatedAtTicks = Stopwatch.GetTimestamp()
            };

        }

        private void QueueClientDisposeWithTransition(int slot)
        {
            if (slot < 0 || slot >= clients.Length)
                return;

            var client = clients[slot];
            if (client == null)
            {
                DisposeClientSlot(slot, clearIdentity: false);
                return;
            }

            if (!_pendingClientDisposeTicks.TryGetValue(slot, out var startedAtTicks))
            {
                _pendingClientDisposeTicks[slot] = Stopwatch.GetTimestamp();
                client.spr?._animManager?.play("walkOut".AsHaxeString(), null, null);
                return;
            }

            var elapsed = Stopwatch.GetElapsedTime(startedAtTicks).TotalSeconds;
            if (elapsed < ClientDisposeTransitionSeconds)
                return;

            DisposeClientSlot(slot, clearIdentity: false);
        }

        private void CancelPendingClientDispose(int slot)
        {
            _pendingClientDisposeTicks.Remove(slot);
        }

        private GhostKing? EnsureClientKingSlot(int slot)
        {
            var hitchStart = RuntimeHitchWatch.Start();
            if (slot < 0 || slot >= clients.Length)
                return null;

            var existing = clients[slot];
            if (existing != null)
                return existing;

            if (_ghost == null || me == null || me._level == null)
                return null;

            var created = _ghost.CreateGhostKing(me._level);
            clients[slot] = created;

            var knownSkin = clientSkins[slot];
            if (!string.IsNullOrWhiteSpace(knownSkin))
                created.ApplyRemoteSkin(knownSkin);

            var knownHead = clientHeadSkins[slot];
            created.RemoteHeadSkinId = NormalizeSkin(
                !string.IsNullOrWhiteSpace(knownHead) ? knownHead : remoteHeadSkin,
                "BaseFlame");
            RecreateClientHead(slot);
            MarkGhostHeadDirty(slot, immediate: true);

            if (!string.IsNullOrWhiteSpace(clientLabels[slot]))
                _ghost.SetLabel(created, clientLabels[slot]);

            ApplyCachedRemoteDiveSkillInfoIfAny(clientIds[slot], created);

            LogGhostRuntimeStepIfSlow(
                "ModEntry.EnsureClientKingSlot",
                hitchStart,
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"slot={slot} remoteId={clientIds[slot]} created=1 skin={(string.IsNullOrWhiteSpace(knownSkin) ? 0 : 1)} head={(string.IsNullOrWhiteSpace(knownHead) ? 0 : 1)}"));

            return created;
        }

        private void DisposeClientSlot(int slot, bool clearIdentity)
        {
            if (slot < 0 || slot >= clients.Length)
                return;

            _pendingClientDisposeTicks.Remove(slot);

            var previousRemoteId = clientIds[slot];

            var head = clientHeads[slot];
            if (head != null)
            {
                head.dispose();
                clientHeads[slot] = null;
            }
            pendingClientHeadRecreate[slot] = false;
            ResetGhostHeadRuntimeState(slot);

            var client = clients[slot];
            if (client != null)
            {
                client.destroy();
                client.dispose();
                client.disposeGfx();
            }
            clients[slot] = null!;
            clientLastBodyAnims[slot] = null;
            clientLastBodyAnimQueues[slot] = null;
            clientLastBodyAnimGs[slot] = null;
            clientLastHeadAnims[slot] = null;
            clientLastDirs[slot] = 0;
            clientLastDownedOffsets[slot] = false;
            rLastX[slot] = 0;
            rLastY[slot] = 0;
            // Do not clear EmergencyLastRemoteX/Y here. F8 recovery needs the last known
            // network position even when the visible ghost was hidden by a room/sublevel mismatch.

            if (!clearIdentity)
                return;

            if (previousRemoteId > 0)
            {
                _remoteLastDoorMarkers.Remove(previousRemoteId);
                ClearCachedRemoteDiveSkillInfo(previousRemoteId);
            }

            clientIds[slot] = 0;
            clientLabels[slot] = null;
        }

        private void ReceiveGhostWeapons()
        {
            // v5.7: stability mode. Do not create/equip the remote player's real weapons on
            // the ghost. Heavy/charged weapons such as Flint can start vanilla powered-feedback
            // cleanup on the receiving client, where the ghost has no real local controller,
            // causing Hashlink "Null access .stopPoweredFeedback" crashes.
            var net = _net;
            if (net == null) return;

            if (net.TryConsumeRemoteWeaponSnapshots(out var updates))
                NetNode.ReleaseConsumedList(updates);
        }

        private void DrainRemoteCombatQueuesAfterLevelChange()
        {
            var net = _net;
            if (net == null)
                return;

            if (net.TryConsumeRemoteWeaponSnapshots(out var weaponUpdates))
                NetNode.ReleaseConsumedList(weaponUpdates);
            if (net.TryConsumeRemoteAttacks(out var attacks))
                NetNode.ReleaseConsumedList(attacks);
        }

        private void ReceiveGhostAttacks()
        {
            // v5.7: stability mode. Drain remote attack packets but do not replay them on the
            // ghost weapon manager. The host's mob HP/death packets still drive progression;
            // this only removes client-side visual attack simulation that crashes with Flint.
            var net = _net;
            if (net == null) return;

            if (net.TryConsumeRemoteAttacks(out var attacks))
                NetNode.ReleaseConsumedList(attacks);
        }

        private void UpdateGhostWeapons()
        {
            // v5.7: stability mode. Do not tick remote ghost weapon managers. Their internal
            // weapon feedback state is not safe for non-local ghosts in the current DCCM build.
        }

        private static int CountPendingClientHeadRecreate()
        {
            var count = 0;
            for (int i = 0; i < pendingClientHeadRecreate.Length; i++)
            {
                if (pendingClientHeadRecreate[i])
                    count++;
            }

            return count;
        }

        private void LogGhostRuntimeStepIfSlow(string key, long stepStart, string? details)
        {
            var stepMs = RuntimeHitchWatch.GetElapsedMilliseconds(stepStart);
            if (stepMs < RuntimeHitchWatch.GhostRuntimeStepSlowThresholdMs)
                return;

            RuntimeHitchWatch.LogSlow(Logger, key, stepMs, details);
        }

        private void PlayGhostAnim(GhostKing client, string anim, int? queueAnim, bool? g)
        {
            if (client?.spr?._animManager == null) return;
            if (string.IsNullOrWhiteSpace(anim)) return;
            if (!IsSafeNetworkHeroAnim(anim)) return;

            var animManager = client.spr._animManager;
            var current = client.spr.groupName;
            if (current != null && string.Equals(current.ToString(), anim, StringComparison.Ordinal))
                return;

            try
            {
                if (ShouldLoopRemoteAnim(anim))
                {
                    client.removeAllAffects(96);
                    client.removeAllAffects(98);
                    client.removeAllAffects(99);
                    animManager.play(anim.AsHaxeString(), null, null).loop(null);
                    return;
                }

                // Visual-only: play the remote hero animation, but do not replay the weapon/action
                // object itself. This is what keeps Flint and powered-feedback weapons stable.
                animManager.play(anim.AsHaxeString(), queueAnim, g).stopOnLastFrame(Ref<bool>.Null);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "[GhostSync] Remote visual animation failed anim={Anim}", anim);
            }
        }

        private static bool ShouldLoopRemoteAnim(string anim)
        {
            if (string.IsNullOrWhiteSpace(anim)) return false;
            var a = anim.Trim();

            // v5.9: loop only true locomotion/idle animations. One-shot actions such as
            // attacks, bow shots, healing, scroll pickup, talking, teleporter use, ladders,
            // exits, ground-pound and doors must play once; looping them made movement/level
            // transitions look wrong.
            if (IsAttackAnim(a)) return false;
            if (a.IndexOf("heal", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (a.IndexOf("potion", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (a.IndexOf("scroll", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (a.IndexOf("talk", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (a.IndexOf("teleport", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (a.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (a.IndexOf("ground", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (a.IndexOf("bound", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (a.IndexOf("ladder", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (a.IndexOf("climb", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (a.IndexOf("stair", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (a.IndexOf("exit", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            if (a.StartsWith("idle", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.StartsWith("run", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.StartsWith("walk", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.IndexOf("move", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("fall", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("remain", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        private void PlayGhostHeadAnim(GhostKing client, string anim)
        {
            if (client == null || client?.head == null || client?.head?.customHeadSpr._animManager == null) return;
            if (string.IsNullOrWhiteSpace(anim)) return;
            if (!IsSafeNetworkHeroAnim(anim)) return;

            try
            {
                var animManager = client.head.customHeadSpr._animManager;
                animManager.play(anim.AsHaxeString(), null, null).loop(null);
                animManager.genSpeed = 0.4;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "[GhostSync] Remote head animation failed anim={Anim}", anim);
            }
        }

        private void SendHeroAnim(string anim, int? queueAnim, bool? g, bool force = false)
        {
            if (_netRole == NetRole.None) return;
            var net = _net;
            if (net == null || string.IsNullOrWhiteSpace(anim)) return;
            if (!force &&
                string.Equals(_lastAnimSent, anim, StringComparison.Ordinal) &&
                _lastAnimQueueSent == queueAnim &&
                _lastAnimGSent == g)
                return;

            net.SendAnim(anim, queueAnim, g);
            _lastAnimSent = anim;
            _lastAnimQueueSent = queueAnim;
            _lastAnimGSent = g;
        }


        private void SendHeadAnim(string anim)
        {
            if (_netRole == NetRole.None) return;
            var net = _net;
            if (net == null || string.IsNullOrWhiteSpace(anim)) return;
            net.SendHeadAnim(anim);
        }

        private void SendEquippedWeapons(Inventory inv)
        {
            if (_netRole == NetRole.None || inv == null) return;
            var w0 = inv.getEquippedWeaponOn(0);
            if (w0 != null)
                SendInventoryWeapon(w0, 0);
            var w1 = inv.getEquippedWeaponOn(1);
            if (w1 != null)
                SendInventoryWeapon(w1, 1);
        }

        private void SendInventoryWeapon(InventItem item, int slot)
        {
            if (RemoteWeaponVisualSyncDisabled()) return;
            if (_netRole == NetRole.None) return;
            if (item == null) return;
            if (!TryGetWeaponKindId(item, out var kindId)) return;
            var net = _net;
            if (net == null || string.IsNullOrWhiteSpace(kindId)) return;
            net.SendInventoryWeapon(kindId!, slot, item.permanentId, GetWeaponAmmoForSync(item));
        }

        private static bool TryGetWeaponKindId(InventItem item, out string? kindId)
        {
            kindId = null;
            if (item == null) return false;
            var kind = item.kind;
            if (kind is InventItemKind.Weapon w)
            {
                kindId = w.Param0?.ToString();
                return !string.IsNullOrWhiteSpace(kindId);
            }
            return false;
        }

        private static bool RemoteWeaponVisualSyncDisabled()
        {
            // Method instead of a const so the compiler does not mark the fallback body unreachable.
            return true;
        }

        private static int? GetWeaponAmmoForSync(InventItem? item)
        {
            if(item == null)
                return null;

            var maxAmmo = item.getMaxAmmo();
            if(maxAmmo <= 0)
                return null;

            var ammo = item.ammo;
            if(ammo < 0) ammo = 0;
            if(ammo > maxAmmo) ammo = maxAmmo;
            return ammo;
        }

        private static int GetWeaponSlot(Inventory inv, InventItem item)
        {
            if (inv == null || item == null) return -1;
            var id = item.permanentId;
            var w0 = inv.getEquippedWeaponOn(0);
            if (w0 != null && w0.permanentId == id) return 0;
            var w1 = inv.getEquippedWeaponOn(1);
            if (w1 != null && w1.permanentId == id) return 1;
            return item.posID;
        }

        private bool IsLocalInventory(Inventory self)
        {
            return me != null && self != null && ReferenceEquals(self, me.inventory);
        }

        private void ApplyRemoteWeaponUpdate(int remoteId, string? kindId, int slot, int permanentId, int? ammo = null)
        {
            // v5.7: disabled for stability. Creating/equipping remote InventItem weapons on
            // ghost heroes can trigger Hashlink powered-feedback cleanup crashes with Flint.
            if (RemoteWeaponVisualSyncDisabled())
                return;

            var hitchStart = RuntimeHitchWatch.Start();
            if (string.IsNullOrWhiteSpace(kindId)) return;
            var net = _net;
            var localId = net?.id ?? 0;
            if (!TryGetClientIndex(localId, remoteId, out var index))
                return;

            var client = clients[index];
            if (client?.inventory == null) return;

            var cleaned = kindId.Replace("|", "/").Trim();
            if (cleaned.Length == 0) return;

            var inv = client.inventory;
            var existing = permanentId != 0 ? inv.getByPermanentId(permanentId) : null;
            var currentSlotItem = slot >= 0 ? inv.getEquippedWeaponOn(slot) : null;
            if (slot >= 0 && IsRemoteWeaponStateMatch(currentSlotItem, cleaned, permanentId, ammo))
                return;

            if(existing == null && permanentId == 0)
            {
                if(IsWeaponKindMatch(currentSlotItem, cleaned))
                    existing = currentSlotItem;
                else if (slot < 0)
                {
                    var w0 = inv.getEquippedWeaponOn(0);
                    if(IsWeaponKindMatch(w0, cleaned))
                        existing = w0;
                    else
                    {
                        var w1 = inv.getEquippedWeaponOn(1);
                        if(IsWeaponKindMatch(w1, cleaned))
                            existing = w1;
                    }
                }
            }

            if (existing == null)
            {
                var newItem = new InventItem(new InventItemKind.Weapon(cleaned.AsHaxeString()));
                if (permanentId != 0)
                    newItem.permanentId = permanentId;
                if (slot >= 0)
                    newItem.posID = slot;
                _inventorySyncGuard = true;
                try
                {
                    if(currentSlotItem != null)
                        currentSlotItem.posID = -1;
                    inv.add(newItem);
                }
                finally
                {
                    _inventorySyncGuard = false;
                }
                existing = newItem;
            }
            else if(currentSlotItem != null &&
                    !ReferenceEquals(currentSlotItem, existing) &&
                    (currentSlotItem.permanentId == 0 ||
                     existing.permanentId == 0 ||
                     currentSlotItem.permanentId != existing.permanentId))
            {
                currentSlotItem.posID = -1;
            }

            if (slot >= 0)
                existing.posID = slot;

            var needsEquip = slot < 0 || !ReferenceEquals(currentSlotItem, existing);
            var needsAmmoUpdate = !DoesWeaponAmmoMatch(existing, ammo);
            if (!needsEquip && !needsAmmoUpdate)
                return;

            _inventorySyncGuard = true;
            try
            {
                if (needsEquip)
                    inv.equip(existing);
                if (needsAmmoUpdate)
                    ApplyRemoteWeaponAmmo(existing, ammo);
            }
            finally
            {
                _inventorySyncGuard = false;
            }

            LogGhostRuntimeStepIfSlow(
                "ModEntry.ApplyRemoteWeaponUpdate",
                hitchStart,
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"remoteId={remoteId} slot={slot} permanentId={permanentId} ammo={(ammo.HasValue ? ammo.Value : -1)} kind={cleaned}"));
        }

        private static void ApplyRemoteWeaponAmmo(InventItem item, int? ammo)
        {
            if(item == null || !ammo.HasValue)
                return;

            var maxAmmo = item.getMaxAmmo();
            if(maxAmmo <= 0)
                return;

            var value = ammo.Value;
            if(value < 0) value = 0;
            if(value > maxAmmo) value = maxAmmo;
            item.ammo = value;
        }

        private static bool DoesWeaponAmmoMatch(InventItem? item, int? ammo)
        {
            if (item == null || !ammo.HasValue)
                return true;

            var maxAmmo = item.getMaxAmmo();
            if (maxAmmo <= 0)
                return true;

            var expected = ammo.Value;
            if (expected < 0)
                expected = 0;
            if (expected > maxAmmo)
                expected = maxAmmo;
            return item.ammo == expected;
        }

        private static bool IsRemoteWeaponStateMatch(InventItem? item, string expectedKindId, int expectedPermanentId, int? ammo)
        {
            if(item == null || !IsWeaponKindMatch(item, expectedKindId))
                return false;
            if(expectedPermanentId != 0 && item.permanentId != expectedPermanentId)
                return false;
            return DoesWeaponAmmoMatch(item, ammo);
        }

        private static bool IsWeaponKindMatch(InventItem? item, string expectedKindId)
        {
            if(item == null || string.IsNullOrWhiteSpace(expectedKindId))
                return false;
            if(!TryGetWeaponKindId(item, out var itemKindId) || string.IsNullOrWhiteSpace(itemKindId))
                return false;
            return string.Equals(itemKindId, expectedKindId, StringComparison.Ordinal);
        }

        private void ResetLocalSkinSendCache()
        {
            _lastSentHeroSkin = null;
            _lastSentHeroHeadSkin = null;
        }

        private void ResetDoorMarkerState()
        {
            _lastDoorMarkerLevelId = string.Empty;
            _lastDoorMarkerToken = int.MinValue;
            _localLastDoorMarkerLevelId = string.Empty;
            _localLastDoorMarkerToken = int.MinValue;
            _remoteLastDoorMarkers.Clear();
            _pendingClientDisposeTicks.Clear();
        }

        private void ResetNetworkState()
        {
            GameDataSync.RestoreOrigHpMultipliers();
            ResetFakeDeathState(unlockLocalHero: true, sendNetworkUpState: false);
            ResetLocalSkinSendCache();
            ResetDoorMarkerState();
            _lastSentDiveInfoPayload = string.Empty;
            _remoteDiveInfoPayloadById.Clear();
            _lastLocalDiveStartSendTicks = 0;
            _lastLocalDiveLandSendTicks = 0;
            _lastDiveInfoScanTicks = 0;
        }
    }
}
