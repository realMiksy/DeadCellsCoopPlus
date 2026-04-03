using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using dc;
using dc.en;
using dc.h2d;
using dc.libs.heaps.slib;
using dc.libs.heaps.slib._AnimManager;
using dc.pr;
using dc.tool.atk;
using dc.tool.skill;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using DeadCellsMultiplayerMod.Mobs.Bosses;
using DeadCellsMultiplayerMod.Mobs.Levelinit;
using Hashlink.Virtuals;
using ModCore.Events;
using ModCore.Events.Interfaces.Game;
using ModCore.Utilities;
using Serilog;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public partial class MobsSynchronization :
    IOnAdvancedModuleInitializing,
    IOnFrameUpdate,
    IEventReceiver
    {
        private readonly ModEntry modEntry;
        private static bool s_eventReceiverInstalled;
        private static bool s_hooksInstalled;

        private static readonly object Sync = new();
        private static readonly List<Mob> trackedMobs = new();
        private static readonly Dictionary<Mob, int> trackedMobIndices = new(ReferenceEqualityComparer.Instance);

        private static readonly Dictionary<int, ClientMobState> clientMobTargets = new();
        private static readonly Dictionary<int, Entity?> clientCachedAttackTargetByLocalIndex = new();
        private static readonly Dictionary<int, long> hostContactAttackSendTick = new();
        private static readonly Dictionary<int, QueuedOldSkillMarker> clientQueuedOldSkillMarkers = new();
        private static readonly Dictionary<int, int> clientLastReportedMobLife = new();
        private static readonly Dictionary<int, long> clientLastMobHitReportTick = new();
        private static readonly Dictionary<int, long> clientLastAiLockTickByLocalIndex = new();
        private static readonly Dictionary<int, long> clientLastAffectEvalTickBySyncId = new();
        private static readonly Dictionary<int, string> clientLastSentAffectPayloadBySyncId = new();
        private static readonly Dictionary<int, TimedStringPayload> clientAffectSampleBySyncId = new();
        private static readonly Dictionary<int, long> clientLastSentAffectTickBySyncId = new();
        private static readonly Dictionary<int, long> clientLastDrawEvalTickBySyncId = new();
        private static readonly Dictionary<int, ClientDrawSentState> clientLastSentDrawStateBySyncId = new();
        private static readonly Dictionary<int, TimedStringPayload> clientLastAppliedHostAffectPayloadBySyncId = new();
        private static readonly Dictionary<int, TimedStringPayload> clientLastAppliedAnimPayloadByLocalIndex = new();
        private static readonly Dictionary<int, double> clientLastAnimationApplyFrameByLocalIndex = new();
        private static readonly Dictionary<string, ParsedAnimPayload> parsedAnimPayloadCache = new(StringComparer.Ordinal);
        private static readonly Dictionary<int, string> hostMobTypeBySyncId = new();
        private static readonly Dictionary<int, long> hostLastStateEvalTickBySyncId = new();
        private static readonly Dictionary<int, long> hostLastStateSentTickBySyncId = new();
        private static readonly Dictionary<int, long> hostClientVisibleUntilTickBySyncId = new();
        private static readonly Dictionary<int, HostMobSentState> hostLastSentMobStatesBySyncId = new();
        private static readonly Dictionary<int, CachedHostMobPayload> hostCachedPayloadBySyncId = new();
        private static readonly Dictionary<int, long> hostAttackRetargetLockUntilTick = new();
        private static readonly Dictionary<int, long> hostLastRetargetEvalTickByLocalIndex = new();
        private static readonly List<Entity> hostDetectedTargets = new();
        private static readonly List<Entity> s_clientDetectedTargetsScratch = new();
        private static readonly List<PlayerInterestPoint> s_playerInterestPointsScratch = new();
        private static readonly List<Mob> s_batchMobsScratch = new();
        private static readonly List<NetNode.MobStateSnapshot> s_batchSnapshotsScratch = new();
        private static readonly List<PendingClientAffectApply> s_clientAffectAppliesScratch = new();
        private static readonly List<PendingHostStateApply> s_hostStateAppliesScratch = new();
        private static readonly List<PendingMobHitApply> s_pendingMobHitAppliesScratch = new();
        private static readonly List<NetNode.MobDraw> s_drawsScratch = new();
        private static readonly List<Mob> s_dieVictimsScratch = new();
        private static readonly HashSet<Mob> s_dieVictimDedupScratch = new(ReferenceEqualityComparer.Instance);
        private static readonly HashSet<int> s_usedLocalIndicesScratch = new();
        private static int suppressMobDieSendDepth;
        private static int suppressMobHitSendDepth;

        private static Level? currentLevel;
        private static Level? lastPlayerInterestLevel;
        private static double lastPlayerInterestFrame = double.NaN;
        private static int forceExactNemesisTargetDepth;
        private static int clientNetworkQueuedAttackDepth;
        private static Mob? clientNetworkQueuedAttackMob;
        private static readonly Dictionary<int, QueuedOldSkillMarker> hostQueuedOldSkillMarkers = new();
        private static readonly Dictionary<int, long> clientLastNetworkAttackTickByLocalIndex = new();
        private static readonly HashSet<Mob> clientPendingSuppressedBossDies = new(ReferenceEqualityComparer.Instance);
        private static int authoritativeClientBossDieDepth;
        private const string MobSyncWorkerDisableEnv = "DCCM_MOB_SYNC_WORKER";
        private const string MobSyncAsyncInProcEnv = "DCCM_MOB_SYNC_ASYNC_INPROC";

        private static double s_distanceSqCacheFrameKey = double.NaN;
        private static readonly Dictionary<int, double> s_localIndexToNearestDistanceSq = new();

        private static double ElapsedSeconds(long startTimestamp, long endTimestamp) =>
            Stopwatch.GetElapsedTime(startTimestamp, endTimestamp).TotalSeconds;

        private static long OffsetTimestampBySeconds(long timestamp, double seconds) =>
            timestamp + (long)(Stopwatch.Frequency * seconds);

        private readonly struct QueuedOldSkillMarker
        {
            public readonly string SkillId;
            public readonly long Tick;

            public QueuedOldSkillMarker(string skillId, long tick)
            {
                SkillId = skillId ?? string.Empty;
                Tick = tick;
            }
        }

        private readonly struct ClientMobAttackIntent
        {
            public readonly string SkillId;
            public readonly bool RequiresTargetInArea;
            public readonly int? Data;
            public readonly int TargetUserId;
            public readonly int AttackDir;
            public readonly long Timestamp;

            public ClientMobAttackIntent(string skillId, bool requiresTargetInArea, int? data, int targetUserId, int attackDir)
            {
                SkillId = skillId ?? string.Empty;
                RequiresTargetInArea = requiresTargetInArea;
                Data = data;
                TargetUserId = targetUserId;
                AttackDir = attackDir;
                Timestamp = Stopwatch.GetTimestamp();
            }
        }

        private readonly struct ClientMobState
        {
            public readonly double X;
            public readonly double Y;
            public readonly int Dir;
            public readonly int Life;
            public readonly int MaxLife;
            public readonly string AnimPayload;
            public readonly string StatePayload;

            public ClientMobState(double x, double y, int dir, int life, int maxLife, string animPayload, string statePayload)
            {
                X = x;
                Y = y;
                Dir = dir;
                Life = life;
                MaxLife = maxLife;
                AnimPayload = animPayload ?? string.Empty;
                StatePayload = statePayload ?? string.Empty;
            }
        }

        private readonly struct HostMobSentState
        {
            public readonly double X;
            public readonly double Y;
            public readonly int Dir;
            public readonly int Life;
            public readonly int MaxLife;
            public readonly string AnimPayload;
            public readonly string Type;
            public readonly string StatePayload;

            public HostMobSentState(double x, double y, int dir, int life, int maxLife, string animPayload, string type, string statePayload)
            {
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

        private readonly struct CachedHostMobPayload
        {
            public readonly string AnimPayload;
            public readonly string Type;
            public readonly string StatePayload;
            public readonly long Tick;

            public CachedHostMobPayload(string animPayload, string type, string statePayload, long tick)
            {
                AnimPayload = animPayload ?? string.Empty;
                Type = type ?? string.Empty;
                StatePayload = statePayload ?? string.Empty;
                Tick = tick;
            }
        }

        private readonly struct TimedStringPayload
        {
            public readonly string Payload;
            public readonly long Tick;

            public TimedStringPayload(string payload, long tick)
            {
                Payload = payload ?? string.Empty;
                Tick = tick;
            }
        }

        private readonly struct PendingClientAffectApply
        {
            public readonly Mob Mob;
            public readonly string StatePayload;

            public PendingClientAffectApply(Mob mob, string statePayload)
            {
                Mob = mob;
                StatePayload = statePayload ?? string.Empty;
            }
        }

        private readonly struct PendingHostStateApply
        {
            public readonly int SyncId;
            public readonly Mob Mob;
            public readonly int Life;
            public readonly int MaxLife;
            public readonly int Dir;
            public readonly string StatePayload;

            public PendingHostStateApply(int syncId, Mob mob, int life, int maxLife, int dir, string statePayload)
            {
                SyncId = syncId;
                Mob = mob;
                Life = life;
                MaxLife = maxLife;
                Dir = dir;
                StatePayload = statePayload ?? string.Empty;
            }
        }

        private readonly struct PendingMobHitApply
        {
            public readonly Mob Mob;
            public readonly int TargetLife;
            public readonly int TargetMaxLife;
            public readonly bool ForceDie;
            public readonly int SyncId;
            public readonly bool IsBoss;
            public readonly bool ReplaySpecialHit;

            public PendingMobHitApply(Mob mob, int targetLife, int targetMaxLife, bool forceDie, int syncId, bool isBoss, bool replaySpecialHit)
            {
                Mob = mob;
                TargetLife = targetLife;
                TargetMaxLife = targetMaxLife;
                ForceDie = forceDie;
                SyncId = syncId;
                IsBoss = isBoss;
                ReplaySpecialHit = replaySpecialHit;
            }
        }

        private readonly struct PlayerInterestPoint
        {
            public readonly Entity Entity;
            public readonly double X;
            public readonly double Y;

            public PlayerInterestPoint(Entity entity, double x, double y)
            {
                Entity = entity;
                X = x;
                Y = y;
            }
        }

        private enum HostMobSyncPriority
        {
            Active,
            MidRange,
            FarRange,
            Dormant
        }

        private readonly struct ClientDrawSentState
        {
            public readonly bool IsOutOfGame;
            public readonly bool IsOnScreen;
            public readonly long Tick;

            public ClientDrawSentState(bool isOutOfGame, bool isOnScreen, long tick)
            {
                IsOutOfGame = isOutOfGame;
                IsOnScreen = isOnScreen;
                Tick = tick;
            }
        }

        public MobsSynchronization(ModEntry entry)
        {
            modEntry = entry;
            if (!s_eventReceiverInstalled)
            {
                EventSystem.AddReceiver(this);
                s_eventReceiverInstalled = true;
            }
        }

        public static void ClearTrackingForLevelChange()
        {
            lock (Sync)
            {
                ResetMobTrackingLocked();
            }
            SyncMobIdRegistry.ClearForLevel(null);
        }

        public void OnAdvancedModuleInitializing(ModEntry entry)
        {
            if (s_hooksInstalled)
                return;

            s_hooksInstalled = true;
            entry.Logger.Information("\x1b[32m[[ModEntry.MobsSynchronization] Initializing MobsSynchronization...]\x1b[0m ");
            try
            {
                // Keep mob encoding in-process by default; worker process can increase overhead under heavy mob counts.
                Environment.SetEnvironmentVariable(MobSyncWorkerDisableEnv, "0");
                Environment.SetEnvironmentVariable(MobSyncAsyncInProcEnv, "0");
            }
            catch
            {
            }

            Hook_Level.entitiesPostCreate += Hook_Level_entitiesPostCreate;
            Hook_Level.registerEntity += Hook_Level_registerEntity;
            Hook_Level.unregisterEntity += Hook_Level_unregisterEntity;
            Hook_Level.onDispose += Hook_Level_onDispose;

            Hook_Mob.setAttackTarget += Hook_Mob_setAttackTarget;
            Hook_Mob.setNemesisTarget += Hook_Mob_setNemesisTarget;
            Hook_Mob.preUpdate += Hook_Mob_preUpdate;
            Hook_Mob.fixedUpdate += Hook_Mob_fixedupdate;
            Hook_Mob.postUpdate += Hook_Mob_postUpdate;
            Hook_Mob.onDamage += Hook_Mob_onDamage;
            Hook_Mob.onDie += Hook_Mob_onDie;
            Hook_Mob.contactAttack += Hook_Mob_contactAttack;
            Hook_Mob.onTouch += Hook_Mob_onTouch;
            Hook_Mob.queueAttack += Hook_Mob_queueAttack;
            Hook_OldSkill.prepare += Hook_OldSkill_prepare;
            Hook_OldSkill.execute += Hook_OldSkill_execute;
            Hook_OldMobSkill.prepareOnOwnerTarget += Hook_OldMobSkill_prepareOnOwnerTarget;
            Hook_OldMobSkill.execute += Hook_OldMobSkill_execute;
            Hook_MobSkill.execute += Hook_MobSkill_execute;
        }

        void IOnFrameUpdate.OnFrameUpdate(double dt)
        {
            if (!MultiplayerSettingsStorage.EnableMobsSync)
            {
                lock (Sync)
                {
                    if (trackedMobs.Count > 0 || currentLevel != null)
                        ResetMobTrackingLocked();
                }
                return;
            }

            var net = GameMenu.NetRef;
            if (net == null || !net.IsAlive)
                return;

            var hasTrackedMobs = false;
            lock (Sync)
            {
                hasTrackedMobs = currentLevel != null && trackedMobs.Count > 0;
            }

            if (IsHost(net))
            {
                ConsumeIncomingClientMobStates(net);
                ConsumeIncomingMobDraws(net);
                ConsumeIncomingMobDies(net);
                ConsumeIncomingMobHits(net);
                if (hasTrackedMobs)
                {
                    var t0 = Stopwatch.GetTimestamp();
                    TrySendHostMobStateDeltaBatchPreUpdate(net);
                    MobSyncProfiler.AddFrameBatch(Stopwatch.GetTimestamp() - t0);
                }

                MobSyncProfiler.TickFrame(ModEntry.Instance?.Logger);
                return;
            }

            if (IsClient(net))
            {
                ConsumeIncomingHostMobStates(net);
                ConsumeIncomingHostMobAttacks(net);
                ConsumeIncomingMobDies(net);
                ConsumeIncomingMobHits(net);
                if (hasTrackedMobs)
                {
                    if (!TryCaptureTrackedMobsForBatch(out var trackedMobCount))
                    {
                        MobSyncProfiler.TickFrame(ModEntry.Instance?.Logger);
                        return;
                    }

                    var now = Stopwatch.GetTimestamp();
                    var t0 = Stopwatch.GetTimestamp();
                    TrySendClientMobBatchesNetFrame(net, now);
                    MobSyncProfiler.AddFrameBatch(Stopwatch.GetTimestamp() - t0);
                }

                MobSyncProfiler.TickFrame(ModEntry.Instance?.Logger);
            }
        }

        private static bool IsHost(NetNode? net) => net != null && net.IsAlive && net.IsHost;
        private static bool IsClient(NetNode? net) => net != null && net.IsAlive && !net.IsHost;

        private static void Hook_Level_entitiesPostCreate(Hook_Level.orig_entitiesPostCreate orig, Level self)
        {
            orig(self);
            RebuildMobArray(self);
            try { GameMenu.NetRef?.ClearMobSyncQueues(); } catch { }
        }

        private static void Hook_Level_registerEntity(Hook_Level.orig_registerEntity orig, Level self, Entity clid)
        {
            orig(self, clid);

            if (clid is not Mob mob)
                return;

            if (!IsSyncMob(mob))
                return;

            ScaleMobHpForMultiplayer(mob);

            TryGetMobSyncId(mob, out var registerSyncId);

            lock (Sync)
            {
                if (currentLevel != null && !ReferenceEquals(currentLevel, self))
                    return;

                if (FindTrackedMobIndexLocked(mob) >= 0)
                    return;

                AddTrackedMobLocked(mob);
            }

            if (TryGetTrackedIndex(mob, out var registerLocalIndex))
            {
                var regNet = GameMenu.NetRef;
                var regRole = regNet == null || !regNet.IsAlive ? "none" : (regNet.IsHost ? "host" : "client");
                MobSyncTrace.LogRegisterTracked(regRole, registerSyncId, registerLocalIndex, BuildMobStateTypeSignature(mob));
            }
        }

        private static void Hook_Level_unregisterEntity(Hook_Level.orig_unregisterEntity orig, Level self, Entity clid)
        {
            var mob = clid as Mob;
            if (mob != null)
            {
                lock (Sync)
                {
                    RemoveTrackedMobLocked(mob);
                }
            }

            orig(self, clid);
        }

        private static void Hook_Level_onDispose(Hook_Level.orig_onDispose orig, Level self)
        {
            lock (Sync)
            {
                ResetMobTrackingLocked();
            }
            SyncMobIdRegistry.ClearForLevel(self);

            orig(self);

            lock (Sync)
            {
                ResetMobTrackingLocked();
            }
        }

        private void Hook_Mob_preUpdate(Hook_Mob.orig_preUpdate orig, Mob self)
        {
            var net = GameMenu.NetRef;
            var isHost = IsHost(net);
            var isClient = IsClient(net);
            
            if (!isHost && !isClient)
            {
                lock (Sync)
                {
                    if (trackedMobs.Count > 0 || currentLevel != null)
                        ResetMobTrackingLocked();
                }
                orig(self);
                return;
            }

            var isSyncMob = IsSyncMob(self);
            if (isSyncMob)
                EnsureMobTracked(self);

            if (isClient && isSyncMob && ShouldRefreshClientMobAiLock(self))
            {
                TryLockMobAi(self, ClientAiLockSeconds);
            }

            if (isHost && isSyncMob)
            {
                TryApplyHostClientVisibilityLease(self);
                TryAssignHostAttackTarget(self);
            }

            orig(self);

        }

        private void Hook_Mob_fixedupdate(Hook_Mob.orig_fixedUpdate orig, Mob self)
        {
            var net = GameMenu.NetRef;
            if (IsClient(net) && IsSyncMob(self))
            {
                orig(self);
                var t0 = Stopwatch.GetTimestamp();
                ApplyInterpolatedState(self);
                MobSyncProfiler.AddFixedApply(Stopwatch.GetTimestamp() - t0);
                return;
            }

            orig(self);
        }

        private void Hook_Mob_postUpdate(Hook_Mob.orig_postUpdate orig, Mob self)
        {
            var net = GameMenu.NetRef;
            var isHost = IsHost(net);
            
            if (!isHost)
            {
                orig(self);
                if (IsClient(net) && IsSyncMob(self))
                {
                    var t0 = Stopwatch.GetTimestamp();
                    ApplyClientAnimationStateBeforeUpdate(self);
                    MobSyncProfiler.AddPostAnim(Stopwatch.GetTimestamp() - t0);
                }

                return;
            }

            orig(self);
        }

        private static void Hook_Mob_onDie(Hook_Mob.orig_onDie orig, Mob self)
        {
            if (ShouldSuppressClientBossDie(self))
            {
                MarkSuppressedClientBossDie(self);
                return;
            }

            var shouldSendDie = false;
            var dieSyncId = -1;
            var dieX = 0.0;
            var dieY = 0.0;
            NetNode? dieNet = null;
            var isClient = false;
            if (self != null && suppressMobDieSendDepth <= 0)
            {
                dieNet = GameMenu.NetRef;
                isClient = IsClient(dieNet);

                // Client is not authoritative for mob death; wait for host die/hit confirmation.
                if (isClient && IsSyncMob(self))
                {
                    try
                    {
                        if (self.life <= 0)
                            self.life = 1;
                    }
                    catch
                    {
                    }

                    return;
                }

                if (dieNet != null &&
                    dieNet.IsAlive &&
                    dieNet.IsHost &&
                    TryGetMobSyncId(self, out dieSyncId))
                {
                    shouldSendDie = true;
                    dieX = GetSyncX(self);
                    dieY = GetSyncY(self);
                }
            }

            orig(self);

            if (self == null)
                return;

            ClearSuppressedClientBossDie(self);

            if (shouldSendDie && dieNet != null && dieNet.IsAlive && dieSyncId >= 0)
            {
                var update = new NetNode.MobEventUpdate(dieSyncId, dieX, dieY, 0, new[] { "die" });
                MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(dieNet), new[] { update });
                dieNet.SendMobEvents(new[] { update });
            }

            lock (Sync)
            {
                RemoveTrackedMobLocked(self);
            }
        }

        private static void RunWithSuppressedMobDieSend(Action action)
        {
            if (action == null)
                return;

            suppressMobDieSendDepth++;
            try
            {
                action();
            }
            finally
            {
                suppressMobDieSendDepth--;
            }
        }

        private static void RunWithSuppressedMobHitSend(Action action)
        {
            if (action == null)
                return;

            suppressMobHitSendDepth++;
            try
            {
                action();
            }
            finally
            {
                suppressMobHitSendDepth--;
            }
        }

        private void Hook_Mob_onDamage(Hook_Mob.orig_onDamage orig, Mob self, AttackData i)
        {
            var preDamageLife = GetMobLifeOrFallback(self, 0);
            orig(self, i);

            try
            {
                if (self == null || i == null)
                    return;

                var net = GameMenu.NetRef;
                if (net == null || !IsSyncMob(self))
                    return;

                var isClient = IsClient(net);
                var tookLifeDelta = self.life < preDamageLife;
                var becameDead = self.life <= 0;
                bool shouldReport = false;
                if (IsHost(net))
                {
                    shouldReport = true;
                }
                else if (isClient)
                {
                    shouldReport = IsDamageFromLocalPlayer(i);
                    // Fallback for damage-source edge cases: never drop a lethal hit report.
                    if (!shouldReport && tookLifeDelta && becameDead)
                        shouldReport = true;
                }

                if (!shouldReport)
                    return;

                if (System.Threading.Volatile.Read(ref suppressMobHitSendDepth) > 0)
                    return;

                if (!TryGetTrackedIndex(self, out var localIndex))
                    return;

                if (!TryGetMobSyncId(self, out var mobSyncId))
                    return;

                var life = self.life;
                var x = GetSyncX(self);
                var y = GetSyncY(self);

                if (IsHost(net))
                {
                    var hx = GetWorldX(self);
                    var hy = GetWorldY(self);
                    var hitEvent = $"hit|{life.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                    var update = new NetNode.MobEventUpdate(mobSyncId, hx, hy, NormalizeDir(self.dir), new[] { hitEvent });
                    MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(net), new[] { update });
                    net.SendMobEvents(new[] { update });
                }

                if (IsClient(net))
                {
                    var now = Stopwatch.GetTimestamp();

                    lock (Sync)
                    {
                        if (!clientLastReportedMobLife.TryGetValue(localIndex, out var lastLife))
                        {
                            // First locally-confirmed hit for this tracked mob: establish baseline and
                            // propagate immediately when damage actually reduced life.
                            clientLastReportedMobLife[localIndex] = life;
                            var maxLife = self.maxLife;
                            if (life >= maxLife && life > 0)
                                return;

                            clientLastMobHitReportTick[localIndex] = now;
                        }
                        else
                        {
                            if (life >= lastLife)
                                return;

                            if (life > 0 &&
                                clientLastMobHitReportTick.TryGetValue(localIndex, out var lastTick) &&
                                ElapsedSeconds(lastTick, now) < ClientMobHitReportMinIntervalSeconds)
                            {
                                clientLastReportedMobLife[localIndex] = life;
                                return;
                            }

                            clientLastReportedMobLife[localIndex] = life;
                            clientLastMobHitReportTick[localIndex] = now;
                        }
                    }

                    var clientHitEvent = $"hit|{life.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                    var clientUpdate = new NetNode.MobEventUpdate(mobSyncId, x, y, 0, new[] { clientHitEvent });
                    MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(net), new[] { clientUpdate });
                    net.SendMobEvents(new[] { clientUpdate });
                }
            }
            finally
            {
                TryRecoverClientSyncMobLifeAfterLocalDamage(self, preDamageLife);
                TryRecoverSuppressedClientBossDie(self, preDamageLife);
            }
        }

        private static string MobSyncNetRoleForTrace(NetNode? net) =>
            net == null || !net.IsAlive ? "none" : (net.IsHost ? "host" : "client");

        private static bool ShouldSuppressClientBossDie(Mob? mob)
        {
            if (mob == null || !BossSyncHelpers.IsBossMob(mob))
                return false;

            var net = GameMenu.NetRef;
            if (!IsClient(net))
                return false;
            if (!IsSyncMob(mob))
                return false;

            return System.Threading.Volatile.Read(ref authoritativeClientBossDieDepth) <= 0;
        }

        private static void MarkSuppressedClientBossDie(Mob? mob)
        {
            if (mob == null)
                return;

            lock (Sync)
            {
                clientPendingSuppressedBossDies.Add(mob);
            }
        }

        private static void ClearSuppressedClientBossDie(Mob? mob)
        {
            if (mob == null)
                return;

            lock (Sync)
            {
                clientPendingSuppressedBossDies.Remove(mob);
            }
        }

        private static void TryRecoverSuppressedClientBossDie(Mob? mob, int fallbackLife)
        {
            if (mob == null || mob.destroyed)
                return;

            var net = GameMenu.NetRef;
            if (!IsClient(net))
            {
                ClearSuppressedClientBossDie(mob);
                return;
            }

            bool hadSuppressedDie;
            lock (Sync)
            {
                hadSuppressedDie = clientPendingSuppressedBossDies.Remove(mob);
            }

            if (!hadSuppressedDie)
                return;

            try
            {
                if (mob.life <= 0)
                    mob.life = System.Math.Max(1, fallbackLife);
            }
            catch
            {
            }
        }

        private static void RunWithAuthoritativeClientBossDie(Mob? mob, Action action)
        {
            if (action == null)
                return;

            var net = GameMenu.NetRef;
            if (!IsClient(net) || mob == null || !BossSyncHelpers.IsBossMob(mob))
            {
                action();
                return;
            }

            authoritativeClientBossDieDepth++;
            try
            {
                action();
            }
            finally
            {
                authoritativeClientBossDieDepth--;
            }
        }

        private static bool IsDamageFromLocalPlayer(AttackData attack)
        {
            if (attack == null)
                return false;

            var localHero = (Entity?)(ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance);
            var gameHero = (Entity?)ModCore.Modules.Game.Instance?.HeroInstance;
            if (localHero == null && gameHero == null)
                return false;

            try
            {
                var source = attack.source;
                if (IsLocalPlayerDamageSource(source, localHero, gameHero))
                    return true;
            }
            catch
            {
            }

            try
            {
                var carrier = attack.carrier;
                if (IsLocalPlayerDamageSource(carrier, localHero, gameHero))
                    return true;
            }
            catch
            {
            }

            try
            {
                var sourceWeapon = attack.sourceWeapon;
                if (sourceWeapon == null)
                    return false;

                var owner = (Entity?)sourceWeapon.owner;
                if (IsLocalPlayerDamageSource(owner, localHero, gameHero))
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static bool IsLocalPlayerDamageSource(Entity? source, Entity? localHero, Entity? gameHero)
        {
            if (source == null)
                return false;

            if (localHero != null && IsSameEntityForDamage(source, localHero))
                return true;

            if (gameHero != null && IsSameEntityForDamage(source, gameHero))
                return true;

            try
            {
                if ((source is Hero || source is KingSkin) && !IsKnownRemoteClientEntity(source))
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static bool IsKnownRemoteClientEntity(Entity? source)
        {
            if (source == null)
                return false;

            for (int i = 0; i < ModEntry.clients.Length; i++)
            {
                var client = ModEntry.clients[i];
                if (client != null && IsSameEntityForDamage(source, client))
                    return true;
            }

            return false;
        }

        private static bool IsSameEntityForDamage(Entity? left, Entity? right)
        {
            if (left == null || right == null)
                return false;

            if (ReferenceEquals(left, right))
                return true;

            try
            {
                var leftUid = left.__uid;
                var rightUid = right.__uid;
                if (leftUid > 0 && rightUid > 0 && leftUid == rightUid)
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static void TrySendHostMobStateDeltaBatchPreUpdate(NetNode net)
        {
            if (!IsHost(net))
                return;

            var trackedMobCount = 0;
            lock (Sync)
            {
                trackedMobCount = trackedMobs.Count;
            }

            if (trackedMobCount <= 0)
                return;

            var now = Stopwatch.GetTimestamp();

            if (!TryCaptureTrackedMobsForBatch(out trackedMobCount))
                return;

            s_batchSnapshotsScratch.Clear();
            for (int i = 0; i < s_batchMobsScratch.Count; i++)
            {
                var mob = s_batchMobsScratch[i];
                if (!TryGetMobSyncId(mob, out var mobSyncId) || mobSyncId < 0)
                    continue;
                if (!IsMobOnScreenForSync(mob))
                    continue;
                var forceAnimTransitionSend = ShouldForceHostAnimStateSend(mob, mobSyncId);
                if (!ShouldEvaluateMobBySyncId(
                        hostLastStateEvalTickBySyncId,
                        mobSyncId,
                        now,
                        forceAnimTransitionSend ? 0.0 : GetHostMobStateEvalSeconds(mob)))
                {
                    continue;
                }

                if (TryBuildHostMobStateDeltaSnapshot(mob, mobSyncId, now, forceAnimTransitionSend, out var snapshot))
                    s_batchSnapshotsScratch.Add(snapshot);
            }

            // Mob sync encoding is in-process.
            if (s_batchSnapshotsScratch.Count > 0)
            {
                MobSyncTrace.LogSendStatesBatch("host", s_batchSnapshotsScratch);
                net.SendMobStates(s_batchSnapshotsScratch);
            }
        }

        private static void TrySendClientMobBatchesNetFrame(NetNode net, long now)
        {
            if (!IsClient(net))
                return;

            var keepAliveSeconds = ClientDrawKeepAliveSeconds;
            s_drawsScratch.Clear();
            s_batchSnapshotsScratch.Clear();

            for (int i = 0; i < s_batchMobsScratch.Count; i++)
            {
                var mob = s_batchMobsScratch[i];
                if (mob == null)
                    continue;
                if (!TryGetMobSyncId(mob, out var mobSyncId) || mobSyncId < 0)
                    continue;

                GetClientMobDrawAndAffectEvalSeconds(mob, out var drawEvalSec, out var affectEvalSec);

                if (ShouldEvaluateMobBySyncId(
                        clientLastDrawEvalTickBySyncId,
                        mobSyncId,
                        now,
                        drawEvalSec))
                {
                    try
                    {
                        var isOutOfGame = mob.isOutOfGame;
                        var isOnScreen = mob.isOnScreen;
                        var shouldSendDraw = false;
                        lock (Sync)
                        {
                            var changed = !clientLastSentDrawStateBySyncId.TryGetValue(mobSyncId, out var lastDraw) ||
                                          lastDraw.IsOutOfGame != isOutOfGame ||
                                          lastDraw.IsOnScreen != isOnScreen;
                            var periodicRefresh = !isOutOfGame &&
                                                  (!clientLastSentDrawStateBySyncId.TryGetValue(mobSyncId, out var lastActiveDraw) ||
                                                   ElapsedSeconds(lastActiveDraw.Tick, now) >= keepAliveSeconds);
                            if (changed || periodicRefresh)
                            {
                                clientLastSentDrawStateBySyncId[mobSyncId] = new ClientDrawSentState(isOutOfGame, isOnScreen, now);
                                shouldSendDraw = true;
                            }
                        }

                        if (shouldSendDraw)
                            s_drawsScratch.Add(new NetNode.MobDraw(net.id, mobSyncId, isOutOfGame, isOnScreen));
                    }
                    catch
                    {
                    }
                }

                if (!IsMobOnScreenForSync(mob))
                    continue;

                if (!ShouldEvaluateMobBySyncId(
                        clientLastAffectEvalTickBySyncId,
                        mobSyncId,
                        now,
                        affectEvalSec))
                {
                    continue;
                }

                var statePayload = GetClientAffectPayloadForSend(mob, mobSyncId, now);
                var shouldSendAffect = false;

                lock (Sync)
                {
                    var changed = !clientLastSentAffectPayloadBySyncId.TryGetValue(mobSyncId, out var lastPayload) ||
                                  !string.Equals(lastPayload, statePayload, StringComparison.Ordinal);
                    if (changed)
                    {
                        clientLastSentAffectPayloadBySyncId[mobSyncId] = statePayload;
                        clientLastSentAffectTickBySyncId[mobSyncId] = now;
                        shouldSendAffect = true;
                    }
                }

                if (!shouldSendAffect)
                    continue;

                s_batchSnapshotsScratch.Add(new NetNode.MobStateSnapshot(
                    mobSyncId,
                    0.0,
                    0.0,
                    0,
                    0,
                    0,
                    string.Empty,
                    string.Empty,
                    statePayload));
            }

            if (s_drawsScratch.Count > 0)
            {
                MobSyncTrace.LogSendDrawBatch("client", s_drawsScratch);
                net.SendMobDrawBatch(s_drawsScratch);
            }

            if (s_batchSnapshotsScratch.Count > 0)
            {
                MobSyncTrace.LogSendStatesBatch("client", s_batchSnapshotsScratch);
                net.SendMobStates(s_batchSnapshotsScratch);
            }

            s_drawsScratch.Clear();
        }

        private static bool IsMobOnScreenForSync(Mob mob)
        {
            if (mob == null)
                return false;

            var hasVisibility = TryGetMobVisibilityState(mob, out var isOnScreen, out _, out _);
            if (hasVisibility && isOnScreen)
                return true;

            if (IsHost(GameMenu.NetRef) && TryGetMobSyncId(mob, out var mobSyncId) && mobSyncId >= 0)
            {
                var now = Stopwatch.GetTimestamp();
                if (HasActiveHostClientVisibilityLease(mobSyncId, now, pruneExpired: true))
                    return true;
            }

            return false;
        }

        private static bool HasActiveHostClientVisibilityLease(int mobSyncId, long nowTick, bool pruneExpired)
        {
            if (mobSyncId < 0)
                return false;

            lock (Sync)
            {
                if (!hostClientVisibleUntilTickBySyncId.TryGetValue(mobSyncId, out var visibleUntilTick))
                    return false;

                if (nowTick <= visibleUntilTick)
                    return true;

                if (pruneExpired)
                    hostClientVisibleUntilTickBySyncId.Remove(mobSyncId);

                return false;
            }
        }

        private static void TryRecoverClientSyncMobLifeAfterLocalDamage(Mob? mob, int fallbackLife)
        {
            if (mob == null || mob.destroyed)
                return;
            if (BossSyncHelpers.IsBossMob(mob))
                return;

            var net = GameMenu.NetRef;
            if (!IsClient(net) || !IsSyncMob(mob))
                return;

            try
            {
                if (mob.life <= 0)
                    mob.life = System.Math.Max(1, fallbackLife);
            }
            catch
            {
            }
        }

        private static void TryApplyHostClientVisibilityLease(Mob mob)
        {
            if (mob == null)
                return;
            if (!TryGetMobSyncId(mob, out var syncId) || syncId < 0)
                return;

            var now = Stopwatch.GetTimestamp();
            if (!HasActiveHostClientVisibilityLease(syncId, now, pruneExpired: true))
                return;

            try
            {
                var wasOutOfGame = mob.isOutOfGame;
                mob.isOnScreen = true;
                if (mob.onScreenRecent < 180.0)
                    mob.onScreenRecent = 180.0;
                mob.isOutOfGame = false;
                mob.lastOutOfGame = false;
                if (wasOutOfGame)
                    mob.onOutOfGameChange();
            }
            catch
            {
            }
        }

        private static bool TryBuildHostMobStateDeltaSnapshot(
            Mob mob,
            int mobSyncId,
            long nowTick,
            bool forcePayloadRefresh,
            out NetNode.MobStateSnapshot snapshot)
        {
            snapshot = default;
            if (mob == null)
                return false;

            var x = GetWorldX(mob);
            var y = GetWorldY(mob);
            var dir = NormalizeDir(mob.dir);
            var life = mob.life;
            var maxLife = mob.maxLife;
            var animPayload = string.Empty;
            var mobType = string.Empty;
            var statePayload = string.Empty;

            HostMobSentState previous;
            var hadPrevious = false;
            var lastSentTick = 0L;
            CachedHostMobPayload cachedPayload = default;
            var hasCachedPayload = false;

            lock (Sync)
            {
                hadPrevious = hostLastSentMobStatesBySyncId.TryGetValue(mobSyncId, out previous);
                hostLastStateSentTickBySyncId.TryGetValue(mobSyncId, out lastSentTick);
                hasCachedPayload = hostCachedPayloadBySyncId.TryGetValue(mobSyncId, out cachedPayload);
            }

            var shouldRefreshPayload = forcePayloadRefresh ||
                                       !hasCachedPayload ||
                                       !hadPrevious ||
                                       ElapsedSeconds(cachedPayload.Tick, nowTick) >= HostPayloadRefreshSeconds;
            if (BossSyncHelpers.IsBossMob(mob))
                shouldRefreshPayload = true;

            if (shouldRefreshPayload)
            {
                animPayload = BuildAnimPayload(mob);
                mobType = BuildMobStateTypeSignature(mob);
                statePayload = BuildHostMobStatePayload(mob);
                cachedPayload = new CachedHostMobPayload(animPayload, mobType, statePayload, nowTick);
                hasCachedPayload = true;
                lock (Sync)
                {
                    hostCachedPayloadBySyncId[mobSyncId] = cachedPayload;
                }
            }
            else
            {
                animPayload = cachedPayload.AnimPayload;
                mobType = cachedPayload.Type;
                statePayload = cachedPayload.StatePayload;
            }

            var current = new HostMobSentState(x, y, dir, life, maxLife, animPayload, mobType, statePayload);
            var positionEpsilon = GetHostStatePositionEpsilon(mob);
            if (hadPrevious && HostMobSentStateEquals(previous, current, positionEpsilon))
            {
                if (lastSentTick != 0 && ElapsedSeconds(lastSentTick, nowTick) < HostUnchangedStateResendGateSeconds)
                    return false;
            }
            else if (GetHostMobSyncPriority(mob) == HostMobSyncPriority.Dormant &&
                     hadPrevious &&
                     life == previous.Life &&
                     maxLife == previous.MaxLife &&
                     lastSentTick != 0 &&
                     ElapsedSeconds(lastSentTick, nowTick) < HostDormantDuplicateLifeMinSeconds)
            {
                return false;
            }

            lock (Sync)
            {
                hostLastSentMobStatesBySyncId[mobSyncId] = current;
                hostLastStateSentTickBySyncId[mobSyncId] = nowTick;
                if (!hasCachedPayload)
                    hostCachedPayloadBySyncId[mobSyncId] = new CachedHostMobPayload(animPayload, mobType, statePayload, nowTick);
            }

            var snapshotAnimPayload = hadPrevious &&
                                      string.Equals(previous.AnimPayload, animPayload, StringComparison.Ordinal)
                ? string.Empty
                : animPayload;
            var snapshotMobType = hadPrevious &&
                                  string.Equals(previous.Type, mobType, StringComparison.Ordinal)
                ? string.Empty
                : mobType;
            var snapshotStatePayload = hadPrevious &&
                                       string.Equals(previous.StatePayload, statePayload, StringComparison.Ordinal)
                ? string.Empty
                : statePayload;

            snapshot = new NetNode.MobStateSnapshot(
                mobSyncId,
                x,
                y,
                dir,
                life,
                maxLife,
                snapshotAnimPayload,
                snapshotMobType,
                snapshotStatePayload);
            return true;
        }

        private static bool ShouldForceHostAnimStateSend(Mob mob, int mobSyncId)
        {
            if (mob == null || mobSyncId < 0)
                return false;

            string cachedAnimPayload;
            lock (Sync)
            {
                if (!hostCachedPayloadBySyncId.TryGetValue(mobSyncId, out var cachedPayload))
                    return false;

                cachedAnimPayload = cachedPayload.AnimPayload ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(cachedAnimPayload))
                return false;

            var currentAnimPayload = BuildAnimPayload(mob);
            if (string.IsNullOrWhiteSpace(currentAnimPayload) ||
                string.Equals(currentAnimPayload, cachedAnimPayload, StringComparison.Ordinal))
            {
                return false;
            }

            // Treat pure speed jitter as non-transitional; force only on group/reverse changes.
            if (TryParseAnimPayload(currentAnimPayload, out var currentParsed) &&
                TryParseAnimPayload(cachedAnimPayload, out var cachedParsed))
            {
                if (string.Equals(currentParsed.Group, cachedParsed.Group, StringComparison.Ordinal) &&
                    currentParsed.Reverse == cachedParsed.Reverse)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HostMobSentStateEquals(HostMobSentState a, HostMobSentState b, double positionEpsilon)
        {
            var posEpsilon = positionEpsilon <= 0.0 ? MobStatePositionEpsilon : positionEpsilon;
            return IsApproximatelyEqual(a.X, b.X, posEpsilon) &&
                   IsApproximatelyEqual(a.Y, b.Y, posEpsilon) &&
                   a.Dir == b.Dir &&
                   a.Life == b.Life &&
                   a.MaxLife == b.MaxLife &&
                   string.Equals(a.AnimPayload, b.AnimPayload, StringComparison.Ordinal) &&
                   string.Equals(a.Type, b.Type, StringComparison.Ordinal) &&
                   string.Equals(a.StatePayload, b.StatePayload, StringComparison.Ordinal);
        }

        private static void EnsurePlayerInterestPointsForFrame(Level? level)
        {
            if (level == null)
            {
                s_playerInterestPointsScratch.Clear();
                lastPlayerInterestLevel = null;
                lastPlayerInterestFrame = double.NaN;
                return;
            }

            double frame;
            try
            {
                frame = level.ftime;
            }
            catch
            {
                frame = double.NaN;
            }

            if (ReferenceEquals(lastPlayerInterestLevel, level) && lastPlayerInterestFrame == frame)
                return;

            s_playerInterestPointsScratch.Clear();
            lastPlayerInterestLevel = level;
            lastPlayerInterestFrame = frame;

            TryAddPlayerInterestPoint(level, ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance);

            for (int i = 0; i < ModEntry.clients.Length; i++)
                TryAddPlayerInterestPoint(level, ModEntry.clients[i]);
        }

        private static void TryAddPlayerInterestPoint(Level level, Entity? entity)
        {
            if (entity == null)
                return;
            if (ModEntry.IsEntityDownedForCombat(entity))
                return;

            try
            {
                if (entity.destroyed || entity.life <= 0)
                    return;
                if (entity._level != null && !ReferenceEquals(entity._level, level))
                    return;
            }
            catch
            {
                return;
            }

            for (int i = 0; i < s_playerInterestPointsScratch.Count; i++)
            {
                if (ReferenceEquals(s_playerInterestPointsScratch[i].Entity, entity))
                    return;
            }

            try
            {
                s_playerInterestPointsScratch.Add(new PlayerInterestPoint(entity, GetWorldX(entity), GetWorldY(entity)));
            }
            catch
            {
            }
        }

        private static bool TryGetNearestPlayerDistanceSq(Mob mob, out double distanceSq)
        {
            distanceSq = double.PositiveInfinity;
            if (mob == null)
                return false;

            Level? level;
            try
            {
                level = mob._level ?? currentLevel;
            }
            catch
            {
                level = currentLevel;
            }

            EnsurePlayerInterestPointsForFrame(level);
            if (s_playerInterestPointsScratch.Count == 0)
                return false;

            double frameKey;
            try
            {
                frameKey = level?.ftime ?? double.NaN;
            }
            catch
            {
                frameKey = double.NaN;
            }

            if (!double.IsNaN(frameKey) && !double.Equals(frameKey, s_distanceSqCacheFrameKey))
            {
                s_localIndexToNearestDistanceSq.Clear();
                s_distanceSqCacheFrameKey = frameKey;
            }

            if (TryGetTrackedIndex(mob, out var cacheIdx) &&
                s_localIndexToNearestDistanceSq.TryGetValue(cacheIdx, out var cachedSq))
            {
                distanceSq = cachedSq;
                return double.IsFinite(cachedSq);
            }

            double mx;
            double my;
            try
            {
                mx = GetWorldX(mob);
                my = GetWorldY(mob);
            }
            catch
            {
                return false;
            }

            var best = double.PositiveInfinity;
            for (int i = 0; i < s_playerInterestPointsScratch.Count; i++)
            {
                var point = s_playerInterestPointsScratch[i];
                var dx = point.X - mx;
                var dy = point.Y - my;
                var candidate = dx * dx + dy * dy;
                if (candidate < best)
                    best = candidate;
            }

            distanceSq = best;
            if (double.IsFinite(best) && TryGetTrackedIndex(mob, out var idxForCache))
                s_localIndexToNearestDistanceSq[idxForCache] = best;

            return double.IsFinite(best);
        }

        private static bool TryGetMobVisibilityState(Mob mob, out bool isOnScreen, out bool isOutOfGame, out double onScreenRecent)
        {
            isOnScreen = false;
            isOutOfGame = true;
            onScreenRecent = 0.0;
            if (mob == null)
                return false;

            try
            {
                isOnScreen = mob.isOnScreen;
                isOutOfGame = mob.isOutOfGame;
                onScreenRecent = mob.onScreenRecent;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryCanMobUpdate(Mob mob, out bool canUpdate)
        {
            canUpdate = false;
            if (mob == null)
                return false;

            try
            {
                canUpdate = mob.canUpdate();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static HostMobSyncPriority GetHostMobSyncPriority(Mob? mob)
        {
            if (mob == null)
                return HostMobSyncPriority.Dormant;
            if (BossSyncHelpers.IsBossMob(mob) || HasValidLivingPlayerCombatTarget(mob))
                return HostMobSyncPriority.Active;

            var hasDistance = TryGetNearestPlayerDistanceSq(mob, out var distanceSq);
            TryGetMobVisibilityState(mob, out var isOnScreen, out var isOutOfGame, out var onScreenRecent);

            if (isOnScreen || onScreenRecent > 0.0 || !isOutOfGame)
                return HostMobSyncPriority.Active;
            if (hasDistance && distanceSq <= MobSyncDistanceSq)
                return HostMobSyncPriority.Active;
            if (hasDistance)
                return HostMobSyncPriority.MidRange;

            return HostMobSyncPriority.FarRange;
        }

        private static double GetHostStatePositionEpsilon(Mob mob)
        {
            var priority = GetHostMobSyncPriority(mob);
            return priority switch
            {
                HostMobSyncPriority.Active => MobStatePositionEpsilon,
                HostMobSyncPriority.MidRange => HostMobStateMidPositionEpsilon,
                HostMobSyncPriority.FarRange => HostMobStateFarPositionEpsilon,
                _ => HostMobStateDormantPositionEpsilon
            };
        }

        private static double GetHostMobStateEvalSeconds(Mob mob)
        {
            var priority = GetHostMobSyncPriority(mob);
            var seconds = priority switch
            {
                HostMobSyncPriority.Active => HostActiveStateEvalSeconds,
                HostMobSyncPriority.MidRange => HostFarStateEvalSeconds,
                HostMobSyncPriority.FarRange => HostDormantStateEvalSeconds,
                _ => HostDormantStateEvalSeconds * 1.6
            };

            if (seconds <= 0.0)
                return seconds;

            lock (Sync)
            {
                if (trackedMobs.Count >= HostCrowdMobCountThreshold)
                {
                    if (priority == HostMobSyncPriority.Active)
                        return seconds * HostCrowdActiveEvalStretchMultiplier;

                    return seconds * HostCrowdEvalStretchMultiplier;
                }
            }

            return seconds;
        }

        private static void GetClientMobDrawAndAffectEvalSeconds(Mob? mob, out double drawSeconds, out double affectSeconds)
        {
            if (mob == null)
            {
                drawSeconds = ClientDormantDrawEvalSeconds;
                affectSeconds = ClientDormantAffectEvalSeconds;
                return;
            }

            if (BossSyncHelpers.IsBossMob(mob) || HasValidLivingPlayerCombatTarget(mob))
            {
                drawSeconds = 0.0;
                affectSeconds = 0.0;
                return;
            }

            TryGetMobVisibilityState(mob, out var isOnScreen, out var isOutOfGame, out var onScreenRecent);

            if (isOnScreen || onScreenRecent > 0.0 || !isOutOfGame)
                affectSeconds = 0.0;
            else
                affectSeconds = double.NaN;

            if (isOnScreen)
                drawSeconds = 0.0;
            else if (!isOutOfGame)
                drawSeconds = ClientFarDrawEvalSeconds;
            else
                drawSeconds = double.NaN;

            if (!double.IsNaN(affectSeconds) && !double.IsNaN(drawSeconds))
                return;

            var hasDistance = TryGetNearestPlayerDistanceSq(mob, out var distanceSq);

            if (double.IsNaN(affectSeconds))
            {
                if (hasDistance && distanceSq <= MobSyncDistanceSq)
                    affectSeconds = 0.0;
                else if (hasDistance)
                    affectSeconds = ClientFarAffectEvalSeconds;
                else
                    affectSeconds = ClientDormantAffectEvalSeconds * 1.6;
            }

            if (double.IsNaN(drawSeconds))
            {
                if (hasDistance && distanceSq <= MobDrawNearDistanceSq)
                    drawSeconds = 0.0;
                else if (hasDistance && distanceSq <= MobSyncDistanceSq)
                    drawSeconds = ClientFarDrawEvalSeconds;
                else
                    drawSeconds = ClientDormantDrawEvalSeconds * 1.35;
            }
        }

        private static bool ShouldEvaluateMobBySyncId(
            Dictionary<int, long> lastEvalTickBySyncId,
            int syncId,
            long nowTick,
            double intervalSeconds)
        {
            if (syncId < 0)
                return false;

            if (intervalSeconds <= 0.0)
            {
                lock (Sync)
                {
                    lastEvalTickBySyncId.Remove(syncId);
                }

                return true;
            }

            lock (Sync)
            {
                if (lastEvalTickBySyncId.TryGetValue(syncId, out var lastTick) &&
                    ElapsedSeconds(lastTick, nowTick) < intervalSeconds)
                    return false;

                lastEvalTickBySyncId[syncId] = nowTick;
                return true;
            }
        }

        private static string GetClientAffectPayloadForSend(Mob mob, int mobSyncId, long nowTick)
        {
            lock (Sync)
            {
                if (clientAffectSampleBySyncId.TryGetValue(mobSyncId, out var cached) &&
                    ElapsedSeconds(cached.Tick, nowTick) < ClientAffectSampleSeconds)
                {
                    return cached.Payload;
                }
            }

            // Client->host affect sync sends presence only; duration ticks are too noisy and create packet spam.
            var payload = BuildMobAffectPresencePayload(mob);
            lock (Sync)
            {
                clientAffectSampleBySyncId[mobSyncId] = new TimedStringPayload(payload, nowTick);
            }

            return payload;
        }

        private static string BuildHostMobStatePayload(Mob mob)
        {
            if (mob == null)
                return string.Empty;

            var presencePayload = BuildMobAffectPresencePayload(mob);
            return BossStateSync.AppendBossState(presencePayload, mob);
        }

        private static bool IsApproximatelyEqual(double a, double b, double epsilon)
        {
            return System.Math.Abs(a - b) <= epsilon;
        }

        private static bool TryCaptureTrackedMobsForBatch(out int trackedMobCount)
        {
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                trackedMobCount = trackedMobs.Count;
                s_batchMobsScratch.Clear();
                if (trackedMobCount <= 0)
                    return false;

                s_batchMobsScratch.AddRange(trackedMobs);
                return true;
            }
        }

        private static string BuildMobAffectStatePayload(Mob mob, bool includeBossStateForHost = false)
        {
            if (mob == null)
                return string.Empty;
            if (BossSyncHelpers.IsBossMob(mob))
                return includeBossStateForHost ? BossStateSync.AppendBossState(string.Empty, mob) : string.Empty;

            try
            {
                var affects = mob.getAllAffects();
                if (affects == null || affects.length <= 0)
                    return string.Empty;

                StringBuilder? builder = null;
                for (int i = 0; i < affects.length; i++)
                {
                    var affectList = affects.getDyn(i);
                    var affectCount = TryGetDynLength(affectList);
                    if (affectCount <= 0)
                        continue;

                    var maxFrames = 0;
                    for (int j = 0; j < affectCount; j++)
                    {
                        var affect = TryGetDynAffectEntry(affectList, j);
                        if (affect == null)
                            continue;

                        var frames = NormalizeAffectFrames(affect.t);
                        if (frames > maxFrames)
                            maxFrames = frames;
                    }

                    if (maxFrames <= 0)
                        maxFrames = ClientAffectSyncDefaultFrames;

                    builder ??= new StringBuilder(affects.length * 6);
                    if (builder.Length > 0)
                        builder.Append('.');

                    builder.Append(i.ToString(CultureInfo.InvariantCulture));
                    builder.Append(':');
                    builder.Append(maxFrames.ToString(CultureInfo.InvariantCulture));
                }

                if (builder == null || builder.Length == 0)
                    return string.Empty;

                var basePayload = builder.ToString();
                return includeBossStateForHost ? BossStateSync.AppendBossState(basePayload, mob) : basePayload;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string BuildMobAffectPresencePayload(Mob mob)
        {
            if (mob == null)
                return string.Empty;

            try
            {
                var affects = mob.getAllAffects();
                if (affects == null || affects.length <= 0)
                    return string.Empty;

                StringBuilder? builder = null;
                for (int i = 0; i < affects.length; i++)
                {
                    if (TryGetDynLength(affects.getDyn(i)) <= 0)
                        continue;

                    builder ??= new StringBuilder(affects.length * 3);
                    if (builder.Length > 0)
                        builder.Append('.');

                    builder.Append(i.ToString(CultureInfo.InvariantCulture));
                }

                return builder?.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ExtractAffectPresenceSignature(string? payload)
        {
            var parsed = ParseAffectStatePayload(payload);
            if (parsed.Count == 0)
                return string.Empty;

            var ids = new List<int>(parsed.Count);
            foreach (var key in parsed.Keys)
                ids.Add(key);
            ids.Sort();
            return string.Join(".", ids);
        }

        private void Hook_Mob_contactAttack(Hook_Mob.orig_contactAttack orig, Mob self, Entity pow)
        {
            var net = GameMenu.NetRef;
            if (IsHost(net) && ModEntry.IsLocalPlayerDowned() && IsPlayerCombatTargetEntity(pow))
                return;

            orig(self, pow);

            if (!IsHost(net) || !IsPlayerCombatTargetEntity(pow))
                return;

            if (TryGetTrackedIndex(self, out var mobIndex) && ShouldSendHostContactPacket(mobIndex))
                TrySendHostMobAttack(self, ContactAttackPacketSkillId, false, null, pow);
        }

        private void Hook_Mob_onTouch(Hook_Mob.orig_onTouch orig, Mob self, Entity atk)
        {
            var net = GameMenu.NetRef;
            if (IsHost(net) && ModEntry.IsLocalPlayerDowned() && IsPlayerCombatTargetEntity(atk))
                return;

            orig(self, atk);

            if (!IsHost(net) || !IsSyncMob(self) || !IsPlayerCombatTargetEntity(atk))
                return;

            EnsureMobTracked(self);
            if (TryGetTrackedIndex(self, out var mobIndex) && ShouldSendHostContactPacket(mobIndex))
                TrySendHostMobAttack(self, ContactAttackPacketSkillId, false, null, atk);
        }

        private void Hook_OldMobSkill_execute(Hook_OldMobSkill.orig_execute orig, OldMobSkill self, double? a)
        {
            orig(self, a);

            var net = GameMenu.NetRef;
            var ownerMob = self?.owner as Mob;
            var skillId = self?.id?.ToString() ?? string.Empty;

            if (ownerMob == null || string.IsNullOrWhiteSpace(skillId))
                return;

            if (IsClient(net))
            {
                RegisterClientQueuedOldSkillMarker(ownerMob, skillId);
                return;
            }

            if (!IsHost(net))
                return;

            TrySendHostMobAttack(ownerMob, OldSkillChargeCompletePacketPrefix + skillId, false, null);
        }

        private bool Hook_OldSkill_prepare(Hook_OldSkill.orig_prepare orig, OldSkill self, int? data)
        {
            var prepared = false;
            try
            {
                prepared = orig(self, data);
            }
            catch
            {
                return false;
            }

            if (!prepared || self is OldMobSkill)
                return prepared;

            var net = GameMenu.NetRef;
            if (!IsHost(net))
                return true;

            var ownerMob = self?.owner as Mob;
            var skillId = self?.id?.ToString() ?? string.Empty;
            if (ownerMob == null || string.IsNullOrWhiteSpace(skillId))
                return true;

            Entity? explicitTarget = null;
            try { explicitTarget = ownerMob.aTarget; } catch { }
            TrySendHostMobAttack(ownerMob, OldSkillPreparePacketPrefix + skillId, false, data, explicitTarget);
            return true;
        }

        private void Hook_OldSkill_execute(Hook_OldSkill.orig_execute orig, OldSkill self, double? ratio)
        {
            orig(self, ratio);

            if (self is OldMobSkill)
                return;

            var net = GameMenu.NetRef;
            var ownerMob = self?.owner as Mob;
            var skillId = self?.id?.ToString() ?? string.Empty;

            if (ownerMob == null || string.IsNullOrWhiteSpace(skillId))
                return;

            if (IsClient(net))
            {
                RegisterClientQueuedOldSkillMarker(ownerMob, skillId);
                return;
            }

            if (!IsHost(net))
                return;

            TrySendHostMobAttack(ownerMob, OldSkillExecutePacketPrefix + skillId, false, null);
        }

        private bool Hook_OldMobSkill_prepareOnOwnerTarget(Hook_OldMobSkill.orig_prepareOnOwnerTarget orig, OldMobSkill self, bool? data, int? e)
        {
            var prepared = false;
            try
            {
                prepared = orig(self, data, e);
            }
            catch
            {
                return false;
            }

            if (!prepared)
                return false;

            var net = GameMenu.NetRef;
            if (!IsHost(net))
                return true;

            var ownerMob = self?.owner as Mob;
            var skillId = self?.id?.ToString() ?? string.Empty;
            if (ownerMob == null || string.IsNullOrWhiteSpace(skillId))
                return true;

            Entity? explicitTarget = null;
            try { explicitTarget = ownerMob.aTarget; } catch { }
            TrySendHostMobAttack(ownerMob, OldSkillPreparePacketPrefix + skillId, false, e, explicitTarget);
            return true;
        }

        private void Hook_Mob_queueAttack(Hook_Mob.orig_queueAttack orig, Mob self, OldMobSkill a, bool requiresTargetInArea, int? data)
        {
            var net = GameMenu.NetRef;
            if (IsClient(net) && IsSyncMob(self) && !IsClientNetworkQueuedAttackAllowed(self))
                return;

            orig(self, a, requiresTargetInArea, data);

            if (self == null || a == null)
                return;

            if (!IsHost(net))
                return;

            var skillId = a.id?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(skillId))
                return;

            EnsureMobTracked(self);
            if (!TryGetTrackedIndex(self, out var mobIndex))
                return;

            lock (Sync)
            {
                hostQueuedOldSkillMarkers[mobIndex] = new QueuedOldSkillMarker(skillId, Stopwatch.GetTimestamp());
            }

            TrySendHostMobAttack(self, skillId, requiresTargetInArea, data);
        }

        private static bool IsClientNetworkQueuedAttackAllowed(Mob? mob)
        {
            if (mob == null)
                return false;

            return clientNetworkQueuedAttackDepth > 0 &&
                   clientNetworkQueuedAttackMob != null &&
                   ReferenceEquals(clientNetworkQueuedAttackMob, mob);
        }

        private static void WithClientNetworkQueuedAttackContext(Mob mob, Action action)
        {
            if (mob == null || action == null)
                return;

            var previousMob = clientNetworkQueuedAttackMob;
            clientNetworkQueuedAttackMob = mob;
            clientNetworkQueuedAttackDepth++;
            try
            {
                action();
            }
            finally
            {
                clientNetworkQueuedAttackDepth--;
                clientNetworkQueuedAttackMob = previousMob;
            }
        }

        private void Hook_MobSkill_execute(Hook_MobSkill.orig_execute orig, MobSkill self, double? ratio)
        {
            orig(self, ratio);

            var net = GameMenu.NetRef;
            if (!IsHost(net))
                return;

            var ownerMob = self?.owner as Mob;
            var skillId = self?.id?.ToString() ?? string.Empty;
            
            if (ownerMob != null && !string.IsNullOrWhiteSpace(skillId))
                TrySendHostMobAttack(ownerMob, NewSkillExecutePacketPrefix + skillId, false, null);
        }

        private static void TrySendHostMobAttack(Mob mob, string skillId, bool requiresTargetInArea, int? data, Entity? explicitTarget = null)
        {
            if (mob == null || string.IsNullOrWhiteSpace(skillId))
                return;

            var net = GameMenu.NetRef;
            if (!IsHost(net))
                return;

            if (!IsSyncMob(mob))
                return;

            if (!TryGetMobSyncId(mob, out var mobSyncId))
                return;

            var targetEntity = ResolveMobAttackTargetEntity(mob, explicitTarget);

            var targetUserId = ResolveHostTargetUserId(targetEntity, net!.id);
            if (ModEntry.IsLocalPlayerDowned() && targetUserId > 0 && targetUserId != net.id)
                return;

            var x = GetWorldX(mob);
            var y = GetWorldY(mob);
            var dir = NormalizeDir(mob.dir);
            RegisterHostAttackRetargetLock(mob, skillId);

            var encodedSkill = Uri.EscapeDataString(skillId);
            var reqTarget = requiresTargetInArea ? 1 : 0;
            var dataVal = data ?? 0;
            var attackEvent = $"attack|{encodedSkill}|0|0|{reqTarget}|{dataVal}|{targetUserId}|{dir}";
            var mobType = BuildMobStateTypeSignature(mob);
            var update = new NetNode.MobEventUpdate(mobSyncId, x, y, dir, new[] { attackEvent }, mobType);
            MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(net), new[] { update });
            net.SendMobEvents(new[] { update });
        }

        private void Hook_Mob_setAttackTarget(Hook_Mob.orig_setAttackTarget orig, Mob self, Entity e)
        {
            if (TryResolveFallbackPlayerCombatTarget(self, e, out var fallbackTarget))
            {
                orig(self, fallbackTarget);
                return;
            }

            orig(self, e);
        }

        private void Hook_Mob_setNemesisTarget(Hook_Mob.orig_setNemesisTarget orig, Mob self, Entity e)
        {
            if (System.Threading.Volatile.Read(ref forceExactNemesisTargetDepth) > 0)
            {
                orig(self, e);
                return;
            }

            if (!IsMobHostileToPlayers(self))
            {
                orig(self, e);
                return;
            }

            if (TryResolveFallbackPlayerCombatTarget(self, e, out var fallbackTarget))
            {
                orig(self, fallbackTarget);
                return;
            }

            orig(self, e);
        }

        private static bool TryResolveFallbackPlayerCombatTarget(Mob? mob, Entity? currentTarget, out Entity fallbackTarget)
        {
            fallbackTarget = null!;
            if (mob == null)
                return false;
            if (!IsMobHostileToPlayers(mob))
                return false;
            if (currentTarget == null)
                return false;

            var shouldReplace = ModEntry.IsEntityDownedForCombat(currentTarget);
            if (!shouldReplace)
            {
                var gameHero = ModCore.Modules.Game.Instance?.HeroInstance;
                shouldReplace = gameHero != null && ReferenceEquals(currentTarget, gameHero);
            }

            if (!shouldReplace)
                return false;

            try
            {
                var helper = mob._team?.get_targetHelper();
                if (helper != null)
                {
                    helper.filterUntargetables();
                    var best = helper.getBest();
                    if (best != null && !ModEntry.IsEntityDownedForCombat(best))
                    {
                        fallbackTarget = best;
                        return true;
                    }
                }
            }
            catch
            {
            }

            var detectedFallback = ResolveDetectedClientTargetEntity(mob);
            if (detectedFallback == null)
                return false;

            fallbackTarget = detectedFallback;
            return true;
        }

        private static void RebuildMobArray(Level? level)
        {
            lock (Sync)
            {
                ResetMobTrackingLocked();
                currentLevel = level;
                SyncMobIdRegistry.RebuildForLevel(level);
                if (level == null || level.entities == null)
                    return;

                var entities = level.entities;
                for (int i = 0; i < entities.length; i++)
                {
                    var mob = entities.getDyn(i) as Mob;
                    if (mob == null || !IsSyncMob(mob))
                        continue;

                    AddTrackedMobLocked(mob);
                }
            }
        }

        private static int AddTrackedMobLocked(Mob mob)
        {
            if (mob == null)
                return -1;

            if (trackedMobIndices.TryGetValue(mob, out var existingIndex))
            {
                if (existingIndex >= 0 && existingIndex < trackedMobs.Count && ReferenceEquals(trackedMobs[existingIndex], mob))
                    return existingIndex;

                trackedMobIndices.Remove(mob);
            }

            trackedMobs.Add(mob);
            var addedIndex = trackedMobs.Count - 1;
            trackedMobIndices[mob] = addedIndex;
            return addedIndex;
        }

        private static void ResetMobTrackingLocked()
        {
            trackedMobs.Clear();
            trackedMobIndices.Clear();
            clientMobTargets.Clear();
            clientCachedAttackTargetByLocalIndex.Clear();
            clientQueuedOldSkillMarkers.Clear();
            hostContactAttackSendTick.Clear();
            hostAttackRetargetLockUntilTick.Clear();
            hostLastRetargetEvalTickByLocalIndex.Clear();
            clientLastReportedMobLife.Clear();
            clientLastMobHitReportTick.Clear();
            clientLastAiLockTickByLocalIndex.Clear();
            clientLastAffectEvalTickBySyncId.Clear();
            clientLastSentAffectPayloadBySyncId.Clear();
            clientAffectSampleBySyncId.Clear();
            clientLastSentAffectTickBySyncId.Clear();
            clientLastDrawEvalTickBySyncId.Clear();
            clientLastSentDrawStateBySyncId.Clear();
            clientLastAppliedHostAffectPayloadBySyncId.Clear();
            clientLastAppliedAnimPayloadByLocalIndex.Clear();
            clientLastAnimationApplyFrameByLocalIndex.Clear();
            clientLastNetworkAttackTickByLocalIndex.Clear();
            parsedAnimPayloadCache.Clear();
            hostMobTypeBySyncId.Clear();
            hostLastStateEvalTickBySyncId.Clear();
            hostLastStateSentTickBySyncId.Clear();
            hostClientVisibleUntilTickBySyncId.Clear();
            hostLastSentMobStatesBySyncId.Clear();
            hostCachedPayloadBySyncId.Clear();
            hostQueuedOldSkillMarkers.Clear();
            hostDetectedTargets.Clear();
            s_playerInterestPointsScratch.Clear();
            s_localIndexToNearestDistanceSq.Clear();
            s_distanceSqCacheFrameKey = double.NaN;
            currentLevel = null;
            lastPlayerInterestLevel = null;
            lastPlayerInterestFrame = double.NaN;
        }

        private static void RemoveTrackedMobLocked(Mob mob)
        {
            var index = FindTrackedMobIndexLocked(mob);
            if (index < 0)
            {
                CleanupTrackedMobCachesLocked(mob);
                SyncMobIdRegistry.RemoveMob(mob);
                return;
            }

            RemoveTrackedMobAtIndexLocked(index);
        }

        private static void RemoveTrackedMobAtIndexLocked(int index)
        {
            if (index < 0 || index >= trackedMobs.Count)
                return;

            var mob = trackedMobs[index];
            CleanupTrackedMobCachesLocked(mob);
            SyncMobIdRegistry.RemoveMob(mob);
            trackedMobIndices.Remove(mob);

            var lastIndex = trackedMobs.Count - 1;
            if (index != lastIndex)
            {
                var movedMob = trackedMobs[lastIndex];
                trackedMobs[index] = movedMob;
                if (movedMob != null)
                    trackedMobIndices[movedMob] = index;

                MoveLocalIndexCachesLocked(lastIndex, index);
            }
            else
            {
                ClearLocalIndexCachesLocked(index);
            }

            trackedMobs.RemoveAt(lastIndex);
        }

        private static void CleanupTrackedMobCachesLocked(Mob? mob)
        {
            if (mob == null)
                return;

            clientPendingSuppressedBossDies.Remove(mob);
            trackedMobIndices.Remove(mob);

            if (!SyncMobIdRegistry.TryGetExistingSyncId(mob, out var syncId))
                return;

            clientLastSentAffectPayloadBySyncId.Remove(syncId);
            clientAffectSampleBySyncId.Remove(syncId);
            clientLastSentAffectTickBySyncId.Remove(syncId);
            clientLastAffectEvalTickBySyncId.Remove(syncId);
            clientLastDrawEvalTickBySyncId.Remove(syncId);
            clientLastSentDrawStateBySyncId.Remove(syncId);
            clientLastAppliedHostAffectPayloadBySyncId.Remove(syncId);
            hostMobTypeBySyncId.Remove(syncId);
            hostLastStateEvalTickBySyncId.Remove(syncId);
            hostLastStateSentTickBySyncId.Remove(syncId);
            hostClientVisibleUntilTickBySyncId.Remove(syncId);
            hostLastSentMobStatesBySyncId.Remove(syncId);
            hostCachedPayloadBySyncId.Remove(syncId);
        }

        private static void ClearLocalIndexCachesLocked(int index)
        {
            clientMobTargets.Remove(index);
            clientCachedAttackTargetByLocalIndex.Remove(index);
            clientQueuedOldSkillMarkers.Remove(index);
            hostContactAttackSendTick.Remove(index);
            hostAttackRetargetLockUntilTick.Remove(index);
            hostLastRetargetEvalTickByLocalIndex.Remove(index);
            clientLastReportedMobLife.Remove(index);
            clientLastMobHitReportTick.Remove(index);
            clientLastAiLockTickByLocalIndex.Remove(index);
            hostQueuedOldSkillMarkers.Remove(index);
            clientLastAppliedAnimPayloadByLocalIndex.Remove(index);
            clientLastAnimationApplyFrameByLocalIndex.Remove(index);
            clientLastNetworkAttackTickByLocalIndex.Remove(index);
        }

        private static void MoveLocalIndexCachesLocked(int fromIndex, int toIndex)
        {
            MoveLocalIndexCacheEntryLocked(clientMobTargets, fromIndex, toIndex);
            MoveLocalIndexCacheEntryLocked(clientCachedAttackTargetByLocalIndex, fromIndex, toIndex);
            MoveLocalIndexCacheEntryLocked(clientQueuedOldSkillMarkers, fromIndex, toIndex);
            MoveLocalIndexCacheEntryLocked(hostContactAttackSendTick, fromIndex, toIndex);
            MoveLocalIndexCacheEntryLocked(hostAttackRetargetLockUntilTick, fromIndex, toIndex);
            MoveLocalIndexCacheEntryLocked(hostLastRetargetEvalTickByLocalIndex, fromIndex, toIndex);
            MoveLocalIndexCacheEntryLocked(clientLastReportedMobLife, fromIndex, toIndex);
            MoveLocalIndexCacheEntryLocked(clientLastMobHitReportTick, fromIndex, toIndex);
            MoveLocalIndexCacheEntryLocked(clientLastAiLockTickByLocalIndex, fromIndex, toIndex);
            MoveLocalIndexCacheEntryLocked(hostQueuedOldSkillMarkers, fromIndex, toIndex);
            MoveLocalIndexCacheEntryLocked(clientLastAppliedAnimPayloadByLocalIndex, fromIndex, toIndex);
            MoveLocalIndexCacheEntryLocked(clientLastAnimationApplyFrameByLocalIndex, fromIndex, toIndex);
            MoveLocalIndexCacheEntryLocked(clientLastNetworkAttackTickByLocalIndex, fromIndex, toIndex);
        }

        private static void MoveLocalIndexCacheEntryLocked<T>(Dictionary<int, T> dict, int fromIndex, int toIndex)
        {
            if (fromIndex == toIndex)
            {
                dict.Remove(fromIndex);
                return;
            }

            if (dict.TryGetValue(fromIndex, out var value))
            {
                dict[toIndex] = value;
                dict.Remove(fromIndex);
            }
            else
            {
                dict.Remove(toIndex);
            }
        }

        private static int FindTrackedMobIndexLocked(Mob mob)
        {
            if (mob == null || trackedMobs.Count == 0)
                return -1;

            if (trackedMobIndices.TryGetValue(mob, out var directIndex))
            {
                if (directIndex >= 0 && directIndex < trackedMobs.Count && ReferenceEquals(trackedMobs[directIndex], mob))
                    return directIndex;

                trackedMobIndices.Remove(mob);
            }

            var hasTargetSyncId = TryGetMobSyncId(mob, out var targetSyncId);

            for (int i = 0; i < trackedMobs.Count; i++)
            {
                var candidate = trackedMobs[i];
                if (candidate == null)
                    continue;

                if (ReferenceEquals(candidate, mob))
                {
                    trackedMobIndices[mob] = i;
                    return i;
                }

                try
                {
                    if (hasTargetSyncId &&
                        TryGetMobSyncId(candidate, out var candidateSyncId) &&
                        candidateSyncId == targetSyncId)
                    {
                        return i;
                    }
                }
                catch
                {
                }
            }

            return -1;
        }

        private static void PruneInvalidTrackedMobsLocked()
        {
            if (trackedMobs.Count == 0)
                return;

            for (int i = trackedMobs.Count - 1; i >= 0; i--)
            {
                var mob = trackedMobs[i];
                if (mob == null)
                {
                    RemoveTrackedMobAtIndexLocked(i);
                    continue;
                }

                var shouldRemove = false;
                try
                {
                    // Do not prune by life<=0: some bosses spawn/transition with temporary zero life
                    // and must stay tracked to receive authoritative host life.
                    shouldRemove = mob.destroyed || mob._level == null;
                }
                catch
                {
                    shouldRemove = true;
                }

                if (!shouldRemove && currentLevel != null)
                {
                    try
                    {
                        var mobLevel = mob._level;
                        shouldRemove = mobLevel != null && !ReferenceEquals(mobLevel, currentLevel);
                    }
                    catch
                    {
                        shouldRemove = true;
                    }
                }

                if (shouldRemove)
                {
                    RemoveTrackedMobAtIndexLocked(i);
                }
            }
        }

        private static bool IsSyncMob(Mob? mob)
        {
            if (!MultiplayerSettingsStorage.EnableMobsSync)
                return false;

            if (mob == null)
                return false;

            try
            {
                if (mob.destroyed || mob._level == null)
                    return false;

                if (BossSyncConstants.DisableBossSyncTemporarily && BossSyncHelpers.IsBossMob(mob))
                    return false;

                // Primary rule: any combat-hostile mob (including bosses) must be synced.
                if (IsMobHostileToPlayers(mob))
                    return true;

                var typeName = mob.GetType().ToString();

                if (typeName.Contains("dc.en.boss.", StringComparison.OrdinalIgnoreCase) ||
                    typeName.Contains(".boss.", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                
                if (typeName.Contains("dc.en.mob.", StringComparison.Ordinal))
                    return true;
                
                if (typeName.Contains(".Mob", StringComparison.Ordinal) || 
                    typeName.Contains(".mob.", StringComparison.Ordinal))
                {
                    return true;
                }
                
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureMobTracked(Mob mob)
        {
            if (!IsSyncMob(mob))
                return;

            lock (Sync)
            {
                var mobLevel = mob._level;
                if (mobLevel != null && !ReferenceEquals(currentLevel, mobLevel))
                {
                    RebuildMobArray(mobLevel);
                    return;
                }

                if (FindTrackedMobIndexLocked(mob) >= 0)
                    return;

                if (mob != null)
                    AddTrackedMobLocked(mob);
            }
        }

        private static bool TryGetTrackedIndex(Mob mob, out int index)
        {
            lock (Sync)
            {
                index = FindTrackedMobIndexLocked(mob);
                return index >= 0;
            }
        }

        private static bool TryGetMobSyncId(Mob mob, out int syncId)
        {
            syncId = -1;
            if (!IsSyncMob(mob))
                return false;

            return SyncMobIdRegistry.TryGetSyncId(mob, out syncId);
        }

        private static int ResolveLocalIndexBySyncIdLocked(int syncId)
        {
            if (syncId < 0)
                return -1;

            if (!SyncMobIdRegistry.TryGetMobBySyncId(syncId, out var mob) || mob == null || !IsSyncMob(mob))
                return -1;

            try
            {
                if (currentLevel != null && mob._level != null && !ReferenceEquals(currentLevel, mob._level))
                    return -1;
            }
            catch
            {
                return -1;
            }

            var localIndex = FindTrackedMobIndexLocked(mob);
            if (localIndex >= 0)
                return localIndex;

            return AddTrackedMobLocked(mob);
        }

        private static int ResolveLocalIndexForIncomingStateLocked(NetNode.MobStateSnapshot state, HashSet<int>? reservedLocalIndices)
        {
            var localIndex = ResolveLocalIndexBySyncIdLocked(state.Index);
            if (localIndex >= 0 && localIndex < trackedMobs.Count)
            {
                var mappedMob = trackedMobs[localIndex];
                var reserved = reservedLocalIndices != null && reservedLocalIndices.Contains(localIndex);
                if (!reserved && DoesMobMatchStateType(mappedMob, state.Type))
                    return localIndex;
            }

            var rebindIndex = FindBestTrackedMobIndexForStateTypeLocked(state, reservedLocalIndices);
            if (rebindIndex >= 0 && rebindIndex < trackedMobs.Count)
            {
                var candidate = trackedMobs[rebindIndex];
                if (candidate != null)
                {
                    SyncMobIdRegistry.BindSyncId(candidate, state.Index);
                    MobSyncTrace.LogBindSyncId("state", state.Index, state.Type ?? string.Empty, state.X, state.Y);
                    return rebindIndex;
                }
            }

            return -1;
        }

        private static int FindBestTrackedMobIndexForStateTypeLocked(NetNode.MobStateSnapshot state, HashSet<int>? reservedLocalIndices)
        {
            return FindBestTrackedMobIndexForTypeAndPositionLocked(
                state.Type,
                state.X,
                state.Y,
                reservedLocalIndices,
                state.Dir,
                state.Life,
                state.MaxLife,
                state.StatePayload);
        }

        private static int FindBestTrackedMobIndexForTypeAndPositionLocked(
            string? expectedType,
            double x,
            double y,
            HashSet<int>? reservedLocalIndices,
            int preferredDir = 0,
            int preferredLife = int.MinValue,
            int preferredMaxLife = int.MinValue,
            string? preferredStatePayload = null)
        {
            if (trackedMobs.Count == 0)
                return -1;

            if (string.IsNullOrWhiteSpace(expectedType))
                return -1;

            var bestIndex = -1;
            var bestScore = double.MaxValue;
            var preferredStateSignature = ExtractAffectPresenceSignature(preferredStatePayload);

            for (int i = 0; i < trackedMobs.Count; i++)
            {
                if (reservedLocalIndices != null && reservedLocalIndices.Contains(i))
                    continue;

                var mob = trackedMobs[i];
                if (!IsStateRebindCandidateLocked(mob))
                    continue;

                if (!DoesMobMatchStateType(mob, expectedType))
                    continue;

                var dx = GetWorldX(mob!) - x;
                var dy = GetWorldY(mob) - y;
                if (!double.IsFinite(dx) || !double.IsFinite(dy))
                    continue;

                var distanceSq = dx * dx + dy * dy;
                if (distanceSq > MobStateTypeRebindSearchRadiusSq)
                    continue;

                var score = distanceSq;

                if (preferredLife != int.MinValue || preferredMaxLife != int.MinValue)
                {
                    try
                    {
                        var lifeDelta = preferredLife == int.MinValue ? 0 : System.Math.Abs(mob.life - preferredLife);
                        var maxLifeDelta = preferredMaxLife == int.MinValue ? 0 : System.Math.Abs(mob.maxLife - preferredMaxLife);
                        score += lifeDelta * 8.0;
                        score += maxLifeDelta * 2.0;
                    }
                    catch
                    {
                    }
                }

                if (!string.IsNullOrWhiteSpace(preferredStateSignature))
                {
                    try
                    {
                        var stateSignature = BuildMobAffectPresencePayload(mob);
                        if (string.Equals(stateSignature, preferredStateSignature, StringComparison.Ordinal))
                            score -= 16.0;
                    }
                    catch
                    {
                    }
                }

                var normalizedPreferredDir = NormalizeDir(preferredDir);
                if (normalizedPreferredDir != 0)
                {
                    try
                    {
                        if (NormalizeDir(mob.dir) != normalizedPreferredDir)
                            score += 4.0;
                    }
                    catch
                    {
                    }
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
                return -1;

            return bestIndex;
        }

        private static int ResolveLocalIndexForIncomingAttackLocked(NetNode.MobAttack attack)
        {
            var localIndex = ResolveLocalIndexBySyncIdLocked(attack.Index);
            var expectedType = attack.Type;
            if (string.IsNullOrWhiteSpace(expectedType))
                hostMobTypeBySyncId.TryGetValue(attack.Index, out expectedType);

            if (localIndex >= 0 && localIndex < trackedMobs.Count)
            {
                var mappedMob = trackedMobs[localIndex];
                if (string.IsNullOrWhiteSpace(expectedType) || DoesMobMatchStateType(mappedMob, expectedType))
                    return localIndex;
            }

            if (string.IsNullOrWhiteSpace(expectedType))
                return -1;

            if (!string.IsNullOrWhiteSpace(attack.Type))
                hostMobTypeBySyncId[attack.Index] = attack.Type;

            var rebindIndex = FindBestTrackedMobIndexForTypeAndPositionLocked(expectedType, attack.X, attack.Y, null);
            if (rebindIndex >= 0 && rebindIndex < trackedMobs.Count)
            {
                var candidate = trackedMobs[rebindIndex];
                if (candidate != null)
                {
                    SyncMobIdRegistry.BindSyncId(candidate, attack.Index);
                    MobSyncTrace.LogBindSyncId("attack", attack.Index, expectedType ?? string.Empty, attack.X, attack.Y);
                    return rebindIndex;
                }
            }

            return -1;
        }

        private static bool IsStateRebindCandidateLocked(Mob? mob)
        {
            if (mob == null || !IsSyncMob(mob))
                return false;

            try
            {
                if (mob.destroyed || mob._level == null)
                    return false;

                if (currentLevel != null && !ReferenceEquals(mob._level, currentLevel))
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        private static string BuildMobStateTypeSignature(Mob mob)
        {
            var typeId = GetMobTypeIdSafe(mob);
            var runtimeClass = GetMobRuntimeClassKeySafe(mob);

            if (!string.IsNullOrWhiteSpace(typeId) && !string.IsNullOrWhiteSpace(runtimeClass))
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{typeId}|{runtimeClass}");
            }

            if (!string.IsNullOrWhiteSpace(typeId))
                return typeId;

            return runtimeClass;
        }

        private static bool DoesMobMatchStateType(Mob? mob, string? stateType)
        {
            if (mob == null)
                return false;

            if (string.IsNullOrWhiteSpace(stateType))
                return true;

            var actualType = GetMobTypeIdSafe(mob);
            var actualClass = GetMobRuntimeClassKeySafe(mob);

            if (TrySplitStateTypeSignature(stateType, out var expectedType, out var expectedClass))
            {
                var typeMatches = string.IsNullOrWhiteSpace(expectedType) ||
                                  (!string.IsNullOrWhiteSpace(actualType) &&
                                   string.Equals(expectedType, actualType, StringComparison.OrdinalIgnoreCase));

                var classMatches = string.IsNullOrWhiteSpace(expectedClass) ||
                                   (!string.IsNullOrWhiteSpace(actualClass) &&
                                    string.Equals(expectedClass, actualClass, StringComparison.OrdinalIgnoreCase));

                return typeMatches && classMatches;
            }

            var legacyExpected = NormalizeMobTypeKey(stateType);
            if (string.IsNullOrWhiteSpace(legacyExpected))
                return true;

            if (!string.IsNullOrWhiteSpace(actualType) &&
                string.Equals(legacyExpected, actualType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(actualClass) &&
                string.Equals(legacyExpected, actualClass, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static bool TryResolveSafeBossNemesisTarget(Mob? mob, Entity? requestedTarget, out Entity safeTarget)
        {
            safeTarget = null!;

            if (mob == null || !BossSyncHelpers.IsBossMob(mob))
                return false;

            if (requestedTarget is Hero heroTarget)
            {
                safeTarget = heroTarget;
                return true;
            }

            try
            {
                var currentHeroTarget = mob.nemesisTarget as Hero;
                if (currentHeroTarget != null &&
                    !currentHeroTarget.destroyed &&
                    currentHeroTarget.life > 0 &&
                    !ModEntry.IsEntityDownedForCombat(currentHeroTarget))
                {
                    safeTarget = currentHeroTarget;
                    return true;
                }
            }
            catch
            {
            }

            var localHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (localHero != null)
            {
                try
                {
                    if (!localHero.destroyed &&
                        localHero.life > 0 &&
                        !ModEntry.IsEntityDownedForCombat(localHero))
                    {
                        safeTarget = localHero;
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TrySplitStateTypeSignature(string? rawValue, out string typeId, out string runtimeClass)
        {
            typeId = string.Empty;
            runtimeClass = string.Empty;

            if (string.IsNullOrWhiteSpace(rawValue))
                return false;

            var value = rawValue.Trim();
            var pipeIndex = value.IndexOf('|');
            if (pipeIndex < 0)
                return false;

            if (pipeIndex > 0)
                typeId = NormalizeMobTypeKey(value[..pipeIndex]);

            if (pipeIndex + 1 < value.Length)
                runtimeClass = NormalizeMobTypeKey(value[(pipeIndex + 1)..]);

            return !string.IsNullOrWhiteSpace(typeId) || !string.IsNullOrWhiteSpace(runtimeClass);
        }

        private static string GetMobTypeIdSafe(Mob? mob)
        {
            if (mob == null)
                return string.Empty;

            try
            {
                return NormalizeMobTypeKey(mob.type?.ToString());
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetMobRuntimeClassKeySafe(Mob? mob)
        {
            if (mob == null)
                return string.Empty;

            try
            {
                var runtimeType = mob.GetType();
                if (runtimeType == null)
                    return string.Empty;

                return NormalizeMobTypeKey(runtimeType.FullName ?? runtimeType.Name);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizeMobTypeKey(string? rawType)
        {
            if (string.IsNullOrWhiteSpace(rawType))
                return string.Empty;

            var value = rawType.Trim();

            var slash = value.LastIndexOf('/');
            var dot = value.LastIndexOf('.');
            var colon = value.LastIndexOf(':');
            var separator = System.Math.Max(System.Math.Max(slash, dot), colon);
            if (separator >= 0 && separator + 1 < value.Length)
                value = value[(separator + 1)..];

            return value.Trim();
        }

        private static void TryLockMobAi(Mob mob, double seconds)
        {
            try
            {
                mob.lockAiS(seconds);
            }
            catch
            {
            }
        }

        private static bool ShouldLockClientMobAi(Mob mob)
        {
            if (mob == null)
                return false;

            if (!BossSyncHelpers.IsBossMob(mob))
                return true;

            if (HasLocalQueuedOrChargingSkill(mob))
                return false;

            if (!TryGetTrackedIndex(mob, out var localIndex))
                return true;

            return !IsWithinClientNetworkAttackAiPreserveWindow(mob, localIndex);
        }

        private static bool ShouldRefreshClientMobAiLock(Mob mob)
        {
            if (!ShouldLockClientMobAi(mob))
                return false;

            if (!TryGetTrackedIndex(mob, out var localIndex))
                return true;

            var now = Stopwatch.GetTimestamp();
            lock (Sync)
            {
                if (clientLastAiLockTickByLocalIndex.TryGetValue(localIndex, out var lastTick) &&
                    ElapsedSeconds(lastTick, now) < ClientAiLockRefreshSeconds)
                {
                    return false;
                }

                clientLastAiLockTickByLocalIndex[localIndex] = now;
                return true;
            }
        }

        private static void TryAssignHostAttackTarget(Mob mob)
        {
            if (mob == null)
                return;
            if (!IsMobHostileToPlayers(mob))
                return;
            var hasDownedTarget = HasDownedPlayerCombatTarget(mob);
            var hasLivingTarget = HasValidLivingPlayerCombatTarget(mob);
            if (!hasDownedTarget && hasLivingTarget && ShouldSuppressHostRetarget(mob))
                return;
            if (ShouldSkipHostRetargetEvaluation(mob))
                return;

            if (ModEntry.IsLocalPlayerDowned())
            {
                TryClearHostMobLivingPlayerTargets(mob);
                return;
            }

            if (!TryResolveDetectedHostCombatTarget(mob, out var selected))
                return;

            try
            {
                mob.setAttackTarget(selected);
            }
            catch
            {
            }

            TrySetNemesisTargetExact(mob, selected);
        }

        private static bool HasDownedPlayerCombatTarget(Mob mob)
        {
            if (mob == null)
                return false;

            try
            {
                var attackTarget = mob.aTarget;
                if (attackTarget != null && ModEntry.IsEntityDownedForCombat(attackTarget))
                    return true;
            }
            catch
            {
            }

            try
            {
                var nemesisTarget = mob.nemesisTarget;
                if (nemesisTarget != null && ModEntry.IsEntityDownedForCombat(nemesisTarget))
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static bool HasValidLivingPlayerCombatTarget(Mob mob)
        {
            if (mob == null)
                return false;

            try
            {
                var attackTarget = mob.aTarget;
                if (attackTarget != null && IsPlayerCombatTargetEntity(attackTarget))
                    return true;
            }
            catch
            {
            }

            try
            {
                var nemesisTarget = mob.nemesisTarget;
                if (nemesisTarget != null && IsPlayerCombatTargetEntity(nemesisTarget))
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static bool ShouldSkipHostRetargetEvaluation(Mob mob)
        {
            if (mob == null || !TryGetTrackedIndex(mob, out var localIndex))
                return false;

            var hasLivingTarget = HasValidLivingPlayerCombatTarget(mob);
            TryGetNearestPlayerDistanceSq(mob, out var distanceSq);
            TryGetMobVisibilityState(mob, out _, out var isOutOfGame, out _);
            TryCanMobUpdate(mob, out var canUpdate);

            if (!hasLivingTarget &&
                isOutOfGame &&
                !canUpdate &&
                double.IsFinite(distanceSq) &&
                distanceSq > MobSyncDistanceSq)
            {
                return true;
            }

            var now = Stopwatch.GetTimestamp();
            lock (Sync)
            {
                if (hostLastRetargetEvalTickByLocalIndex.TryGetValue(localIndex, out var lastTick) &&
                    ElapsedSeconds(lastTick, now) < HostRetargetRefreshSeconds)
                {
                    return true;
                }

                hostLastRetargetEvalTickByLocalIndex[localIndex] = now;
                return false;
            }
        }

        private static bool ShouldSuppressHostRetarget(Mob mob)
        {
            if (mob == null || !TryGetTrackedIndex(mob, out var localIndex))
                return false;

            lock (Sync)
            {
                var nowTick = Stopwatch.GetTimestamp();
                if (hostAttackRetargetLockUntilTick.TryGetValue(localIndex, out var until))
                {
                    if (nowTick <= until)
                        return true;

                    hostAttackRetargetLockUntilTick.Remove(localIndex);
                }

                if (hostQueuedOldSkillMarkers.TryGetValue(localIndex, out var marker))
                {
                    if (ElapsedSeconds(marker.Tick, nowTick) <= HostQueuedOldSkillMarkerSeconds)
                        return true;
                }
            }

            try
            {
                if (mob.queuedOldSkill?.a != null)
                    return true;
            }
            catch
            {
            }

            try
            {
                if (mob.hasSkillCharging())
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static void TryClearHostMobLivingPlayerTargets(Mob mob)
        {
            if (mob == null)
                return;

            try
            {
                var at = mob.aTarget;
                if (at != null && IsPlayerCombatTargetEntity(at))
                    mob.setAttackTarget(null);
            }
            catch
            {
            }

            try
            {
                var nt = mob.nemesisTarget;
                if (nt != null && IsPlayerCombatTargetEntity(nt))
                    mob.setNemesisTarget(null);
            }
            catch
            {
            }
        }

        private static void TryCollectDetectedTarget(Mob mob, Entity? candidate)
        {
            if (candidate == null)
                return;
            if (ReferenceEquals(candidate, mob))
                return;
            if (ModEntry.IsEntityDownedForCombat(candidate))
                return;

            if (ModEntry.IsLocalPlayerDowned() && IsPlayerCombatTargetEntity(candidate))
                return;

            try
            {
                if (candidate.destroyed || candidate.life <= 0)
                    return;
            }
            catch
            {
                return;
            }

            var mobLevel = mob._level;
            var candidateLevel = candidate._level;
            if (mobLevel != null && candidateLevel != null && !ReferenceEquals(mobLevel, candidateLevel))
                return;

            bool inDetectArea;
            try
            {
                inDetectArea = mob.inDetectArea(candidate);
            }
            catch
            {
                return;
            }

            if (!inDetectArea)
                return;

            if (!hostDetectedTargets.Contains(candidate))
                hostDetectedTargets.Add(candidate);
        }

        private static void RegisterHostAttackRetargetLock(Mob mob, string skillId)
        {
            if (mob == null || string.IsNullOrWhiteSpace(skillId))
                return;

            if (!TryGetTrackedIndex(mob, out var localIndex))
                return;

            double seconds;
            if (string.Equals(skillId, ContactAttackPacketSkillId, StringComparison.Ordinal))
            {
                seconds = HostContactRetargetLockSeconds;
            }
            else if (skillId.StartsWith(OldSkillExecutePacketPrefix, StringComparison.Ordinal) ||
                     !skillId.StartsWith(NewSkillExecutePacketPrefix, StringComparison.Ordinal))
            {
                seconds = HostOldSkillRetargetLockSeconds;
            }
            else
            {
                seconds = 0.0;
            }

            if (seconds <= 0.0)
                return;

            var until = OffsetTimestampBySeconds(Stopwatch.GetTimestamp(), seconds);
            lock (Sync)
            {
                hostAttackRetargetLockUntilTick[localIndex] = until;
            }
        }

        private static bool IsMobHostileToPlayers(Mob? mob)
        {
            if (mob == null)
                return false;

            try
            {
                var level = mob._level;
                var mobTeam = mob._team;
                if (level == null || mobTeam == null)
                    return false;

                return ReferenceEquals(mobTeam, level.teamMob);
            }
            catch
            {
                return false;
            }
        }

        private static int ResolveHostTargetUserId(Entity? target, int localUserId)
        {
            if (target == null || localUserId <= 0)
                return 0;
            if (ModEntry.IsEntityDownedForCombat(target))
                return 0;

            var localHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (localHero != null && ReferenceEquals(target, localHero))
                return localUserId;

            var gameHero = ModCore.Modules.Game.Instance?.HeroInstance;
            if (gameHero != null && ReferenceEquals(target, gameHero))
                return localUserId;

            for (int i = 0; i < ModEntry.clients.Length; i++)
            {
                var clientId = ModEntry.clientIds[i];
                var client = ModEntry.clients[i];
                if (clientId <= 0 || client == null)
                    continue;

                if (ReferenceEquals(target, client))
                    return clientId;
            }

            return 0;
        }

        private static bool TryResolveDetectedHostCombatTarget(Mob mob, out Entity selected)
        {
            selected = null!;
            if (mob == null)
                return false;

            lock (Sync)
            {
                hostDetectedTargets.Clear();
                try
                {
                    TryCollectDetectedTarget(mob, ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance);

                    for (int i = 0; i < ModEntry.clients.Length; i++)
                    {
                        if (ModEntry.clientIds[i] <= 0)
                            continue;

                        TryCollectDetectedTarget(mob, ModEntry.clients[i]);
                    }

                    if (hostDetectedTargets.Count == 0)
                        return false;

                    try
                    {
                        var currentNemesis = mob.nemesisTarget;
                        if (currentNemesis != null && hostDetectedTargets.Contains(currentNemesis))
                        {
                            selected = currentNemesis;
                            return true;
                        }
                    }
                    catch
                    {
                    }

                    try
                    {
                        var currentTarget = mob.aTarget;
                        if (currentTarget != null && hostDetectedTargets.Contains(currentTarget))
                        {
                            selected = currentTarget;
                            return true;
                        }
                    }
                    catch
                    {
                    }

                    var mx = GetWorldX(mob);
                    var my = GetWorldY(mob);
                    var bestDistSq = double.MaxValue;

                    for (int i = 0; i < hostDetectedTargets.Count; i++)
                    {
                        var candidate = hostDetectedTargets[i];
                        if (candidate == null)
                            continue;

                        var dx = GetWorldX(candidate) - mx;
                        var dy = GetWorldY(candidate) - my;
                        var distSq = dx * dx + dy * dy;
                        if (distSq < bestDistSq)
                        {
                            bestDistSq = distSq;
                            selected = candidate;
                        }
                    }

                    return selected != null;
                }
                finally
                {
                    hostDetectedTargets.Clear();
                }
            }
        }

        private static Entity? ResolveMobAttackTargetEntity(Mob mob, Entity? explicitTarget)
        {
            if (explicitTarget != null && IsPlayerCombatTargetEntity(explicitTarget))
                return explicitTarget;

            try
            {
                if (mob.aTarget != null && IsPlayerCombatTargetEntity(mob.aTarget))
                    return mob.aTarget;
            }
            catch
            {
            }

            try
            {
                if (mob.nemesisTarget != null && IsPlayerCombatTargetEntity(mob.nemesisTarget))
                    return mob.nemesisTarget;
            }
            catch
            {
            }

            if (TryResolveDetectedHostCombatTarget(mob, out var detectedTarget))
                return detectedTarget;

            return null;
        }

        private static bool IsPlayerCombatTargetEntity(Entity entity)
        {
            if (entity == null)
                return false;
            if (ModEntry.IsEntityDownedForCombat(entity))
                return false;

            if (entity is Hero || entity is KingSkin && entity.visible == true)
                return true;

            var localHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (localHero != null && ReferenceEquals(entity, localHero))
                return true;

            for (int i = 0; i < ModEntry.clients.Length; i++)
            {
                var client = ModEntry.clients[i];
                if (client != null && ReferenceEquals(entity, client))
                    return true;
            }

            return false;
        }

        private static void TrySetNemesisTargetExact(Mob mob, Entity target)
        {
            if (mob == null || target == null)
                return;

            if (TryResolveSafeBossNemesisTarget(mob, target, out var safeBossTarget))
                target = safeBossTarget;
            else if (BossSyncHelpers.IsBossMob(mob))
                return;

            System.Threading.Interlocked.Increment(ref forceExactNemesisTargetDepth);
            try
            {
                mob.setNemesisTarget(target);
            }
            catch
            {
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref forceExactNemesisTargetDepth);
            }
        }

        private static void TrySetMobAttackTargetsExact(Mob mob, Entity target, int attackDir = 0, bool forceAttackDir = false)
        {
            if (mob == null || target == null)
                return;

            try
            {
                var mobX = GetWorldX(mob);
                var targetX = GetWorldX(target);
                var targetDir = targetX < mobX ? -1 : targetX > mobX ? 1 : mob.dir;

                if (forceAttackDir)
                {
                    var normalized = NormalizeDir(attackDir);
                    if (normalized != 0)
                        mob.dir = normalized;
                }
                else
                {
                    if (targetDir != 0)
                        mob.dir = targetDir;
                }
            }
            catch
            {
            }

            try
            {
                mob.setAttackTarget(target);
            }
            catch
            {
            }

            TrySetNemesisTargetExact(mob, target);

            if (forceAttackDir)
            {
                try
                {
                    var normalized = NormalizeDir(attackDir);
                    if (normalized != 0)
                        mob.dir = normalized;
                }
                catch
                {
                }
            }
        }

        private static int NormalizeDir(int dir)
        {
            if (dir < 0) return -1;
            if (dir > 0) return 1;
            return 0;
        }

        private static int ComputeResponsiveFacingDir(Mob mob, ClientMobState state)
        {
            if (mob == null)
                return NormalizeDir(state.Dir);

            var netDir = NormalizeDir(state.Dir);
            if (netDir != 0)
                return netDir;

            var currentX = GetWorldX(mob);
            var deltaX = state.X - currentX;
            if (deltaX >= ClientTurnSnapDeltaPx)
                return 1;
            if (deltaX <= -ClientTurnSnapDeltaPx)
                return -1;

            return NormalizeDir(mob.dir);
        }

        private static double GetWorldX(Entity entity)
        {
            return (entity.cx + entity.xr) * PixelsPerCase;
        }

        private static double GetWorldY(Entity entity)
        {
            return (entity.cy + entity.yr) * PixelsPerCase;
        }

        private static double GetSyncX(Entity entity)
        {
            try
            {
                var spr = entity.spr;
                if (spr != null)
                    return spr.x;
            }
            catch
            {
            }

            return GetWorldX(entity);
        }

        private static double GetSyncY(Entity entity)
        {
            try
            {
                var spr = entity.spr;
                if (spr != null)
                    return spr.y;
            }
            catch
            {
            }

            return GetWorldY(entity);
        }

        private static void SetWorldXKeepingY(Mob mob, double worldX)
        {
            if (mob == null)
                return;

            // Update X only and preserve the entity's current Y cell/fraction to avoid
            // disturbing native vertical physics (oldSkill/mid-air states).
            var xCellFloat = worldX / PixelsPerCase;
            var xCase = (int)System.Math.Floor(xCellFloat);
            var xFrac = xCellFloat - xCase;

            if (xFrac < 0.0)
                xFrac = 0.0;
            else if (xFrac > 1.0)
                xFrac = 1.0;

            mob.setPosCase(xCase, mob.cy, xFrac, mob.yr);
        }

        private readonly struct ParsedAnimPayload
        {
            public readonly string Group;
            public readonly bool Reverse;
            public readonly double Speed;

            public ParsedAnimPayload(string group, bool reverse, double speed)
            {
                Group = group ?? string.Empty;
                Reverse = reverse;
                Speed = speed;
            }
        }

        private static AnimManager? GetMobAnimManager(Mob mob)
        {
            var spr = mob.spr;
            if (spr == null)
                return null;

            try
            {
                return spr._animManager ?? spr.get_anim();
            }
            catch
            {
                return spr._animManager;
            }
        }

        private static AnimInstance? GetTopAnimInstance(AnimManager? animManager)
        {
            var stack = animManager?.stack;
            if (stack == null || stack.length <= 0)
                return null;

            try
            {
                return stack.getDyn(0) as AnimInstance;
            }
            catch
            {
                return null;
            }
        }

        private static string BuildAnimPayload(Mob mob)
        {
            var spr = mob.spr;
            if (spr == null)
                return string.Empty;

            var group = spr.groupName?.ToString() ?? string.Empty;
            var reverse = false;
            var speed = 1.0;

            try
            {
                var animManager = GetMobAnimManager(mob);
                var top = GetTopAnimInstance(animManager);
                if (top != null)
                {
                    if (!string.IsNullOrWhiteSpace(top.group?.ToString()))
                        group = top.group.ToString();
                    reverse = top.reverse;
                    if (top.speed > 0.0)
                        speed = top.speed;
                }
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(group))
                return string.Empty;

            string encodedGroup;
            try
            {
                encodedGroup = Uri.EscapeDataString(group);
            }
            catch
            {
                encodedGroup = group;
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{encodedGroup}~{(reverse ? 1 : 0)}~{speed:R}");
        }

        private static bool TryParseAnimPayload(string? payload, out ParsedAnimPayload parsed)
        {
            parsed = default;
            if (string.IsNullOrWhiteSpace(payload))
                return false;

            var parts = payload.Split('~', StringSplitOptions.None);
            if (parts.Length < 3)
                return false;

            var encodedGroup = parts[0];
            string group;
            try
            {
                group = Uri.UnescapeDataString(encodedGroup);
            }
            catch
            {
                group = encodedGroup;
            }

            if (string.IsNullOrWhiteSpace(group))
                return false;

            var hasLegacyFrame = parts.Length >= 4 &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
            var reversePart = hasLegacyFrame ? parts[2] : parts[1];
            var speedPart = hasLegacyFrame ? parts[3] : parts[2];

            var reverse = reversePart == "1";
            if (!double.TryParse(speedPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                speed = 1.0;

            parsed = new ParsedAnimPayload(group, reverse, System.Math.Max(0.01, speed));
            return true;
        }

        private static bool TryGetParsedAnimPayloadCached(string payload, out ParsedAnimPayload parsed)
        {
            parsed = default;
            if (string.IsNullOrWhiteSpace(payload))
                return false;

            lock (Sync)
            {
                if (parsedAnimPayloadCache.TryGetValue(payload, out parsed))
                    return true;
            }

            if (!TryParseAnimPayload(payload, out parsed))
                return false;

            lock (Sync)
            {
                if (parsedAnimPayloadCache.Count >= ParsedAnimPayloadCacheLimit)
                    parsedAnimPayloadCache.Clear();

                parsedAnimPayloadCache[payload] = parsed;
            }

            return true;
        }

        private static void ConsumeIncomingHostMobStates(NetNode net)
        {
            if (!net.TryConsumeMobStates(out var states))
                return;

            MobSyncTrace.LogRecvStates("hostStatesFromHost", states);
            ApplyIncomingHostMobStates(states);
        }

        private static void ConsumeIncomingClientMobStates(NetNode net)
        {
            if (!net.TryConsumeMobStates(out var states))
                return;

            MobSyncTrace.LogRecvStates("clientAffectFromClient", states);
            ApplyIncomingClientMobStatesOnHost(states);
        }

        private static void ApplyIncomingClientMobStatesOnHost(IReadOnlyList<NetNode.MobStateSnapshot> states)
        {
            if (states == null || states.Count == 0)
                return;

            var hostVisibilityLeaseUntilTick = OffsetTimestampBySeconds(Stopwatch.GetTimestamp(), HostClientDrawVisibilityHoldSeconds);
            s_clientAffectAppliesScratch.Clear();
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                for (int i = 0; i < states.Count; i++)
                {
                    var state = states[i];
                    var mob = ResolveMobBySyncIdLocked(state.Index);
                    if (mob == null)
                        continue;
                    if (string.IsNullOrWhiteSpace(state.StatePayload))
                        continue;

                    if (state.Index >= 0)
                        hostClientVisibleUntilTickBySyncId[state.Index] = hostVisibilityLeaseUntilTick;

                    s_clientAffectAppliesScratch.Add(new PendingClientAffectApply(mob, state.StatePayload));
                }
            }

            for (int i = 0; i < s_clientAffectAppliesScratch.Count; i++)
            {
                var entry = s_clientAffectAppliesScratch[i];
                ApplyClientReportedAffectStateOnHost(entry.Mob, entry.StatePayload);
            }

            s_clientAffectAppliesScratch.Clear();
        }

        private static void ApplyClientReportedAffectStateOnHost(Mob mob, string? payload)
        {
            if (mob == null || mob.destroyed)
                return;
            if (BossSyncHelpers.IsBossMob(mob))
                return;

            var desired = ParseAffectStatePayload(payload);
            if (desired.Count == 0)
                return;

            foreach (var entry in desired)
                ApplySyncedAffectState(mob, entry.Key, entry.Value);
            BossStateSync.ApplyBossStateFromPayload(mob, payload);
        }

        private static void ApplyIncomingHostMobStates(IReadOnlyList<NetNode.MobStateSnapshot> states)
        {
            if (states == null || states.Count == 0)
                return;

            s_hostStateAppliesScratch.Clear();
            s_usedLocalIndicesScratch.Clear();
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();

                for (int i = 0; i < states.Count; i++)
                {
                    var state = states[i];
                    if (!string.IsNullOrWhiteSpace(state.Type))
                        hostMobTypeBySyncId[state.Index] = state.Type;

                    var localIndex = ResolveLocalIndexForIncomingStateLocked(state, s_usedLocalIndicesScratch);
                    if (localIndex < 0)
                        continue;

                    s_usedLocalIndicesScratch.Add(localIndex);

                    Mob? mob = null;
                    if (localIndex >= 0 && localIndex < trackedMobs.Count)
                        mob = trackedMobs[localIndex];
                    var incomingDir = NormalizeDir(state.Dir);
                    if (mob != null)
                    {
                        clientLastReportedMobLife[localIndex] = state.Life;
                        s_hostStateAppliesScratch.Add(new PendingHostStateApply(
                            state.Index,
                            mob,
                            state.Life,
                            state.MaxLife,
                            incomingDir,
                            state.StatePayload ?? string.Empty));
                    }

                    var mergedAnimPayload = state.AnimPayload ?? string.Empty;
                    var mergedStatePayload = state.StatePayload ?? string.Empty;
                    if (clientMobTargets.TryGetValue(localIndex, out var previousTarget))
                    {
                        if (string.IsNullOrEmpty(mergedAnimPayload))
                            mergedAnimPayload = previousTarget.AnimPayload;
                        if (string.IsNullOrEmpty(mergedStatePayload))
                            mergedStatePayload = previousTarget.StatePayload;
                    }

                    clientMobTargets[localIndex] = new ClientMobState(
                        state.X,
                        state.Y,
                        incomingDir,
                        state.Life,
                        state.MaxLife,
                        mergedAnimPayload,
                        mergedStatePayload);
                }
            }

            s_usedLocalIndicesScratch.Clear();

            for (int i = 0; i < s_hostStateAppliesScratch.Count; i++)
            {
                var entry = s_hostStateAppliesScratch[i];
                if (entry.Dir != 0)
                {
                    try { entry.Mob.dir = entry.Dir; } catch { }
                }
                ApplyAuthoritativeLifeState(entry.Mob, entry.Life, entry.MaxLife);
                ApplyAuthoritativeAffectState(entry.SyncId, entry.Mob, entry.StatePayload);
            }

            s_hostStateAppliesScratch.Clear();
        }

        private static void ApplyAuthoritativeAffectState(int mobSyncId, Mob mob, string? payload)
        {
            if (mob == null || mob.destroyed)
                return;

            var safePayload = payload ?? string.Empty;
            var nowTick = Stopwatch.GetTimestamp();
            lock (Sync)
            {
                if (clientLastAppliedHostAffectPayloadBySyncId.TryGetValue(mobSyncId, out var lastApplied) &&
                    string.Equals(lastApplied.Payload, safePayload, StringComparison.Ordinal) &&
                    ElapsedSeconds(lastApplied.Tick, nowTick) < ClientAffectSyncSeconds)
                {
                    return;
                }

                clientLastAppliedHostAffectPayloadBySyncId[mobSyncId] = new TimedStringPayload(safePayload, nowTick);
            }

            if (BossSyncHelpers.IsBossMob(mob))
            {
                BossStateSync.ApplyBossStateFromPayload(mob, safePayload);
                return;
            }

            var desired = ParseAffectStatePayload(safePayload);
            if (desired.Count == 0)
                return;

            foreach (var entry in desired)
                ApplySyncedAffectState(mob, entry.Key, entry.Value);
            BossStateSync.ApplyBossStateFromPayload(mob, payload);
        }

        private static void ApplySyncedAffectState(Mob mob, int affectId, int targetFrames)
        {
            if (mob == null || mob.destroyed || affectId < 0)
                return;

            var normalizedFrames = targetFrames > 0 ? targetFrames : ClientAffectSyncDefaultFrames;
            var targetSeconds = normalizedFrames / AffectFramesPerSecond;
            var hadAffect = false;

            try
            {
                hadAffect = mob.hasAffect(affectId);
            }
            catch
            {
            }

            if (!hadAffect)
            {
                try
                {
                    mob.setAffectS(affectId, targetSeconds, HaxeProxy.Runtime.Ref<double>.Null, null);
                }
                catch
                {
                }
            }

            SyncExistingAffectTimeFrames(mob, affectId, normalizedFrames, allowIncrease: !hadAffect);
        }

        private static void SyncExistingAffectTimeFrames(Mob mob, int affectId, int targetFrames, bool allowIncrease)
        {
            if (mob == null || affectId < 0 || targetFrames <= 0)
                return;

            try
            {
                var affects = mob.getAllAffects();
                if (affects == null || affectId >= affects.length)
                    return;

                var affectList = affects.getDyn(affectId);
                var affectCount = TryGetDynLength(affectList);
                if (affectCount <= 0)
                    return;

                for (int i = 0; i < affectCount; i++)
                {
                    var affect = TryGetDynAffectEntry(affectList, i);
                    if (affect == null)
                        continue;

                    var currentFrames = NormalizeAffectFrames(affect.t);
                    if (currentFrames <= 0)
                    {
                        affect.t = targetFrames;
                        continue;
                    }

                    if (currentFrames > targetFrames)
                    {
                        affect.t = targetFrames;
                        continue;
                    }

                    if (allowIncrease || targetFrames - currentFrames >= AffectTimeIncreaseThresholdFrames)
                        affect.t = targetFrames;
                }
            }
            catch
            {
            }
        }

        private static Dictionary<int, int> ParseAffectStatePayload(string? payload)
        {
            var affects = new Dictionary<int, int>();
            if (string.IsNullOrWhiteSpace(payload))
                return affects;

            var decoded = payload!;
            try { decoded = Uri.UnescapeDataString(decoded); } catch { }
            if (string.IsNullOrWhiteSpace(decoded))
                return affects;

            var parts = decoded.Split('.', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                var token = parts[i]?.Trim();
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                var idPart = token;
                var frames = ClientAffectSyncDefaultFrames;

                var separator = token.IndexOf(':');
                if (separator > 0 && separator < token.Length - 1)
                {
                    idPart = token[..separator];
                    var framesPart = token[(separator + 1)..];
                    if (int.TryParse(framesPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFrames) && parsedFrames > 0)
                        frames = parsedFrames;
                }

                if (!int.TryParse(idPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    continue;
                if (id < 0)
                    continue;

                if (affects.TryGetValue(id, out var existing))
                {
                    if (frames > existing)
                        affects[id] = frames;
                }
                else
                {
                    affects[id] = frames;
                }
            }

            return affects;
        }

        private static int TryGetDynLength(object? dynArray)
        {
            if (dynArray == null)
                return 0;

            try
            {
                return ((dynamic)dynArray).length;
            }
            catch
            {
                return 0;
            }
        }

        private static virtual_a_t_uniqId_val_? TryGetDynAffectEntry(object? dynArray, int index)
        {
            if (dynArray == null || index < 0)
                return null;

            try
            {
                return ((dynamic)dynArray).getDyn(index) as virtual_a_t_uniqId_val_;
            }
            catch
            {
                return null;
            }
        }

        private static int NormalizeAffectFrames(double frames)
        {
            if (!double.IsFinite(frames) || frames <= 0.0)
                return 0;

            var normalized = (int)System.Math.Ceiling(frames);
            return normalized <= 0 ? 0 : normalized;
        }

        private static void ConsumeIncomingHostMobAttacks(NetNode net)
        {
            if (!net.TryConsumeMobAttacks(out var attacks))
                return;

            MobSyncTrace.LogRecvAttacks("hostAttacksFromHost", attacks);
            ApplyIncomingHostMobAttacks(attacks);
        }

        private static void ApplyIncomingHostMobAttacks(IReadOnlyList<NetNode.MobAttack> attacks)
        {
            if (attacks == null || attacks.Count == 0)
                return;

            for (int i = 0; i < attacks.Count; i++)
            {
                var attack = attacks[i];
                Mob? mob = null;

                lock (Sync)
                {
                    var localIndex = ResolveLocalIndexForIncomingAttackLocked(attack);
                    if (localIndex >= 0 && localIndex < trackedMobs.Count)
                        mob = trackedMobs[localIndex];
                }

                if (mob == null)
                    continue;

                TryQueueClientMobAttack(mob, attack.SkillId, attack.RequiresTargetInArea, attack.Data, attack.TargetUserId, attack.Dir);
            }
        }

        private static void ConsumeIncomingMobDraws(NetNode net)
        {
            if (!net.TryConsumeMobDraws(out var draws))
                return;

            MobSyncTrace.LogRecvDraws("clientDrawsFromClient", draws);
            ApplyIncomingMobDraws(draws);
        }

        private static void ApplyIncomingMobDraws(IReadOnlyList<NetNode.MobDraw> draws)
        {
            if (draws == null || draws.Count == 0)
                return;

            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                for (int i = 0; i < draws.Count; i++)
                {
                    var draw = draws[i];
                    var localIndex = ResolveLocalIndexBySyncIdLocked(draw.MobIndex);
                    if (localIndex < 0 || localIndex >= trackedMobs.Count)
                        continue;

                    var mob = trackedMobs[localIndex];
                    if (!IsSyncMob(mob))
                        continue;

                    TryApplyHostDrawRequestLocked(mob!, draw);
                }
            }
        }

        private static void TryApplyHostDrawRequestLocked(Mob mob, NetNode.MobDraw draw)
        {
            if (mob == null)
                return;

            if (TryGetMobSyncId(mob, out var drawSyncId) && drawSyncId >= 0)
            {
                if (!draw.IsOutOfGame)
                {
                    hostClientVisibleUntilTickBySyncId[drawSyncId] =
                        OffsetTimestampBySeconds(Stopwatch.GetTimestamp(), HostClientDrawVisibilityHoldSeconds);

                    if (draw.IsOnScreen)
                        TryWakeMobForForcedSimulation(mob);
                }
                else
                {
                    hostClientVisibleUntilTickBySyncId.Remove(drawSyncId);
                }
            }
        }

        private static void TryQueueClientMobAttack(Mob mob, string skillId, bool requiresTargetInArea, int? data, int targetUserId, int attackDir)
        {
            if (mob == null || string.IsNullOrWhiteSpace(skillId))
                return;

            var intent = new ClientMobAttackIntent(skillId, requiresTargetInArea, data, targetUserId, attackDir);
            ProcessClientMobAttackIntent(mob, intent);
        }

        private static void ProcessClientMobAttackIntent(Mob mob, ClientMobAttackIntent intent)
        {
            if (mob == null || string.IsNullOrWhiteSpace(intent.SkillId))
                return;

            var netUi = GameMenu.NetRef;
            if (IsClient(netUi) && ModEntry.IsSessionHostDowned(netUi))
                return;

            var skillId = intent.SkillId;
            var traceRoute = ResolveClientAttackRouteForTrace(skillId);
            _ = TryGetMobSyncId(mob, out var traceSyncId);
            MobSyncTrace.LogClientAttackRoute(traceRoute, traceSyncId, skillId);

            if (string.Equals(skillId, ContactAttackPacketSkillId, StringComparison.Ordinal))
            {
                ProcessClientContactAttack(mob, intent);
                return;
            }

            if (skillId.StartsWith(OldSkillExecutePacketPrefix, StringComparison.Ordinal))
            {
                ProcessClientOldSkillExecute(mob, skillId[OldSkillExecutePacketPrefix.Length..], intent);
                return;
            }

            if (skillId.StartsWith(OldSkillPreparePacketPrefix, StringComparison.Ordinal))
            {
                ProcessClientOldSkillPrepare(mob, skillId[OldSkillPreparePacketPrefix.Length..], intent);
                return;
            }

            if (skillId.StartsWith(OldSkillChargeCompletePacketPrefix, StringComparison.Ordinal))
            {
                ProcessClientOldSkillExecute(mob, skillId[OldSkillChargeCompletePacketPrefix.Length..], intent);
                return;
            }

            if (skillId.StartsWith(NewSkillExecutePacketPrefix, StringComparison.Ordinal))
            {
                ProcessClientNewSkillExecute(mob, skillId[NewSkillExecutePacketPrefix.Length..], intent);
                return;
            }

            ProcessClientOldSkillQueue(mob, intent);
        }

        private static string ResolveClientAttackRouteForTrace(string skillId)
        {
            if (string.Equals(skillId, ContactAttackPacketSkillId, StringComparison.Ordinal))
                return "contact";

            if (skillId.StartsWith(OldSkillExecutePacketPrefix, StringComparison.Ordinal))
                return "oldSkillExecute";

            if (skillId.StartsWith(OldSkillPreparePacketPrefix, StringComparison.Ordinal))
                return "oldSkillPrepare";

            if (skillId.StartsWith(OldSkillChargeCompletePacketPrefix, StringComparison.Ordinal))
                return "oldSkillChargeComplete";

            if (skillId.StartsWith(NewSkillExecutePacketPrefix, StringComparison.Ordinal))
                return "newSkillExecute";

            return "oldSkillQueue";
        }

        private static void ProcessClientContactAttack(Mob mob, ClientMobAttackIntent intent)
        {
            TrySetClientMobAttackTarget(mob, intent.TargetUserId, intent.AttackDir, forceRetarget: true);
            TryWakeMobForForcedSimulation(mob);

            var target = ResolveClientAttackTargetEntity(mob, intent.TargetUserId);
            if (target == null)
                target = ResolveClientAttackTargetEntity(mob, 0);
            if (target == null)
                return;

            RegisterClientNetworkAttackExecuted(mob);

            try
            {
                mob.contactAttack(target);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Client contactAttack failed for mob");
            }

            try
            {
                mob.onTouch(target);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Client onTouch failed for mob");
            }
        }

        private static void ProcessClientOldSkillExecute(Mob mob, string rawSkillId, ClientMobAttackIntent intent)
        {
            if (string.IsNullOrWhiteSpace(rawSkillId))
                return;

            var normalizedSkillId = rawSkillId.Trim();
            if (string.IsNullOrWhiteSpace(normalizedSkillId))
                return;

            if (ShouldSkipClientOldSkillExecuteFromMarker(mob, normalizedSkillId))
                return;

            try
            {
                var skillId = normalizedSkillId.AsHaxeString();
                if (!mob.hasOldSkill(skillId))
                    return;

                var oldSkill = mob.getOldSkill(skillId) as OldMobSkill;
                if (oldSkill == null)
                    return;

                if (TryGetChargingOldSkillId(mob, out var chargingOldSkillId))
                {
                    if (!string.Equals(chargingOldSkillId, normalizedSkillId, StringComparison.Ordinal))
                        return;
                }

                RegisterClientNetworkAttackExecuted(mob);
                TrySetClientMobAttackTarget(mob, intent.TargetUserId, intent.AttackDir, forceRetarget: true);
                TryWakeMobForForcedSimulation(mob);
                if (ResolveClientAttackTargetEntity(mob, intent.TargetUserId) == null)
                    TrySetClientMobAttackTarget(mob, 0, intent.AttackDir, forceRetarget: true);

                if (!TryGetChargingOldSkillId(mob, out _))
                {
                    if (oldSkill is OldMobSkill oldMobSkill && TryExecuteClientOldSkillNativeLike(oldMobSkill, intent.Data))
                    { }
                    else
                    {
                        oldSkill.prepare(intent.Data);
                    }
                }

                TryInvokeOldSkillChargeComplete(oldSkill);
                oldSkill.execute(null);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Client oldSkill execute failed: {SkillId}", normalizedSkillId);
            }
        }

        private static void ProcessClientOldSkillPrepare(Mob mob, string rawSkillId, ClientMobAttackIntent intent)
        {
            if (string.IsNullOrWhiteSpace(rawSkillId))
                return;

            var normalizedSkillId = rawSkillId.Trim();
            if (string.IsNullOrWhiteSpace(normalizedSkillId))
                return;

            try
            {
                if (!TryGetMobOldSkill(mob, normalizedSkillId, out var oldSkill))
                    return;

                RegisterClientNetworkAttackExecuted(mob);
                TrySetClientMobAttackTarget(mob, intent.TargetUserId, intent.AttackDir, forceRetarget: true);
                TryWakeMobForForcedSimulation(mob);
                if (ResolveClientAttackTargetEntity(mob, intent.TargetUserId) == null)
                    TrySetClientMobAttackTarget(mob, 0, intent.AttackDir, forceRetarget: true);

                if (oldSkill is OldMobSkill oldMobSkill && TryExecuteClientOldSkillNativeLike(oldMobSkill, intent.Data))
                    return;

                if (!oldSkill.prepare(intent.Data))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Client oldSkill prepare failed: {SkillId}", normalizedSkillId);
            }
        }

        private static void ProcessClientNewSkillExecute(Mob mob, string rawSkillId, ClientMobAttackIntent intent)
        {
            if (string.IsNullOrWhiteSpace(rawSkillId))
                return;

            var normalizedSkillId = rawSkillId.Trim();
            if (string.IsNullOrWhiteSpace(normalizedSkillId))
                return;

            try
            {
                if (TryGetChargingNewSkillId(mob, out var chargingNewSkillId))
                {
                    if (!string.Equals(chargingNewSkillId, normalizedSkillId, StringComparison.Ordinal))
                        return;

                    var chargingSkill = mob.getChargingNewSkill() as MobSkill;
                    if (chargingSkill == null)
                        return;

                    RegisterClientNetworkAttackExecuted(mob);
                    chargingSkill.execute(null);
                    return;
                }

                RegisterClientNetworkAttackExecuted(mob);
                TrySetClientMobAttackTarget(mob, intent.TargetUserId, intent.AttackDir, forceRetarget: true);
                TryWakeMobForForcedSimulation(mob);

                var skillId = normalizedSkillId.AsHaxeString();
                var skill = mob.getSkill(skillId) as MobSkill;
                if (skill == null)
                    return;

                skill.prepare(intent.Data);
                skill.execute(null);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Client newSkill execute failed: {SkillId}", normalizedSkillId);
            }
        }

        private static void ProcessClientOldSkillQueue(Mob mob, ClientMobAttackIntent intent)
        {
            try
            {
                if (IsQueuedOrChargingOldSkillId(mob, intent.SkillId))
                    return;

                RegisterClientNetworkAttackExecuted(mob);
                TrySetClientMobAttackTarget(mob, intent.TargetUserId, intent.AttackDir, forceRetarget: true);
                TryWakeMobForForcedSimulation(mob);
                if (ResolveClientAttackTargetEntity(mob, intent.TargetUserId) == null)
                    TrySetClientMobAttackTarget(mob, 0, intent.AttackDir, forceRetarget: true);

                var haxeSkillId = intent.SkillId.AsHaxeString();
                if (!mob.hasOldSkill(haxeSkillId))
                    return;

                var oldSkill = mob.getOldSkill(haxeSkillId) as OldMobSkill;
                if (oldSkill == null)
                    return;

                WithClientNetworkQueuedAttackContext(mob, () =>
                {
                    mob.queueAttack(oldSkill, intent.RequiresTargetInArea, intent.Data);
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Client oldSkill queue failed: {SkillId}", intent.SkillId);
            }
        }

        private static void TryInvokeOldSkillChargeComplete(OldSkill oldSkill)
        {
            if (oldSkill == null)
                return;

            try
            {
                var cb = oldSkill.dynOnChargeComplete;
                if (cb != null)
                {
                    cb.Invoke();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] OldSkill dynOnChargeComplete invoke failed");
            }
        }

        private static bool TryExecuteClientOldSkillNativeLike(OldMobSkill oldSkill, int? data)
        {
            if (oldSkill == null)
                return false;

            if (TryPrepareClientOldSkillOnOwnerTarget(oldSkill, null, data))
                return true;

            if (!data.HasValue && TryPrepareClientOldSkillOnOwnerTarget(oldSkill, null, null))
                return true;

            if (TryPrepareClientOldSkillOnOwnerTarget(oldSkill, true, data))
                return true;

            return !data.HasValue && TryPrepareClientOldSkillOnOwnerTarget(oldSkill, true, null);
        }

        private static bool TryPrepareClientOldSkillOnOwnerTarget(OldMobSkill oldSkill, bool? useTargetData, int? data)
        {
            try
            {
                return oldSkill.prepareOnOwnerTarget(useTargetData, data);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetMobOldSkill(Mob mob, string normalizedSkillId, out OldSkill oldSkill)
        {
            oldSkill = null!;
            if (mob == null || string.IsNullOrWhiteSpace(normalizedSkillId))
                return false;

            try
            {
                var skillId = normalizedSkillId.AsHaxeString();
                if (!mob.hasOldSkill(skillId))
                    return false;

                oldSkill = mob.getOldSkill(skillId) as OldSkill;
                return oldSkill != null;
            }
            catch
            {
                oldSkill = null!;
                return false;
            }
        }

        private static bool IsQueuedOrChargingOldSkillId(Mob mob, string expectedSkillId)
        {
            if (mob == null || string.IsNullOrWhiteSpace(expectedSkillId))
                return false;

            if (IsQueuedOldSkillId(mob, expectedSkillId))
                return true;

            if (TryGetChargingOldSkillId(mob, out var chargingOldSkillId) &&
                string.Equals(chargingOldSkillId, expectedSkillId, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static bool IsQueuedOldSkillId(Mob mob, string expectedSkillId)
        {
            if (mob == null || string.IsNullOrWhiteSpace(expectedSkillId))
                return false;

            if (!TryGetQueuedOldSkillId(mob, out var queuedOldSkillId))
                return false;

            return string.Equals(queuedOldSkillId, expectedSkillId, StringComparison.Ordinal);
        }

        private static bool TryGetQueuedOldSkillId(Mob mob, out string skillId)
        {
            skillId = string.Empty;
            if (mob == null)
                return false;

            try
            {
                var queued = mob.queuedOldSkill;
                var queuedSkill = queued?.a;
                skillId = queuedSkill?.id?.ToString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(skillId);
            }
            catch
            {
                skillId = string.Empty;
                return false;
            }
        }

        private static bool TryGetChargingOldSkillId(Mob mob, out string skillId)
        {
            skillId = string.Empty;
            if (mob == null)
                return false;

            try
            {
                var chargingOldSkill = mob.getChargingOldSkill() as OldSkill;
                skillId = chargingOldSkill?.id?.ToString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(skillId);
            }
            catch
            {
                skillId = string.Empty;
                return false;
            }
        }

        private static bool TryGetChargingNewSkillId(Mob mob, out string skillId)
        {
            skillId = string.Empty;
            if (mob == null)
                return false;

            try
            {
                var chargingNewSkill = mob.getChargingNewSkill();
                skillId = chargingNewSkill?.id?.ToString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(skillId);
            }
            catch
            {
                skillId = string.Empty;
                return false;
            }
        }

        private static void RegisterClientQueuedOldSkillMarker(Mob mob, string skillId)
        {
            if (mob == null || string.IsNullOrWhiteSpace(skillId))
                return;

            if (!TryGetTrackedIndex(mob, out var localIndex))
                return;

            lock (Sync)
            {
                clientQueuedOldSkillMarkers[localIndex] = new QueuedOldSkillMarker(skillId, Stopwatch.GetTimestamp());
            }
        }

        private static bool ShouldSkipClientOldSkillExecuteFromMarker(Mob mob, string incomingSkillId)
        {
            if (mob == null || string.IsNullOrWhiteSpace(incomingSkillId))
                return false;

            if (!TryGetTrackedIndex(mob, out var localIndex))
                return false;

            QueuedOldSkillMarker marker;
            var nowTick = Stopwatch.GetTimestamp();

            lock (Sync)
            {
                if (!clientQueuedOldSkillMarkers.TryGetValue(localIndex, out marker))
                    return false;

                if (ElapsedSeconds(marker.Tick, nowTick) > ClientQueuedOldSkillMarkerSeconds)
                {
                    clientQueuedOldSkillMarkers.Remove(localIndex);
                    return false;
                }

                if (string.Equals(marker.SkillId, incomingSkillId, StringComparison.Ordinal))
                {
                    clientQueuedOldSkillMarkers.Remove(localIndex);
                    return true;
                }
            }

            return false;
        }

        private static bool IsEntityValidForAttack(Entity? e)
        {
            if (e == null)
                return false;
            try
            {
                return !e.destroyed && e.life > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void TrySetClientMobAttackTarget(Mob mob, int targetUserId, int attackDir, bool forceRetarget = false)
        {
            Entity? target = null;

            if (TryGetTrackedIndex(mob, out var localIndex))
            {
                lock (Sync)
                {
                    if (!forceRetarget &&
                        clientCachedAttackTargetByLocalIndex.TryGetValue(localIndex, out var cached) &&
                        IsEntityValidForAttack(cached))
                    {
                        target = cached;
                    }
                }
            }

            if (target == null)
            {
                target = ResolveClientAttackTargetEntity(mob, targetUserId);
                if (target == null)
                    return;

                if (TryGetTrackedIndex(mob, out localIndex))
                {
                    lock (Sync)
                    {
                        clientCachedAttackTargetByLocalIndex[localIndex] = target;
                    }
                }
            }

            var normalizedAttackDir = NormalizeDir(attackDir);
            if (!forceRetarget)
            {
                try
                {
                    if (targetUserId <= 0)
                    {
                        if (mob.aTarget != null && IsPlayerCombatTargetEntity(mob.aTarget))
                        {
                            if (normalizedAttackDir != 0)
                                mob.dir = normalizedAttackDir;
                            return;
                        }

                        if (mob.nemesisTarget != null && IsPlayerCombatTargetEntity(mob.nemesisTarget))
                        {
                            if (normalizedAttackDir != 0)
                                mob.dir = normalizedAttackDir;
                            return;
                        }
                    }
                    else
                    {
                        if (ReferenceEquals(mob.aTarget, target) || ReferenceEquals(mob.nemesisTarget, target))
                        {
                            if (normalizedAttackDir != 0)
                                mob.dir = normalizedAttackDir;
                            return;
                        }
                    }
                }
                catch
                {
                }
            }

            TrySetMobAttackTargetsExact(mob, target, attackDir, forceAttackDir: true);
        }

        private static void TrySetClientMobAttackFacingOnly(Mob mob, int targetUserId, int attackDir)
        {
            if (mob == null)
                return;

            var normalizedAttackDir = NormalizeDir(attackDir);
            if (normalizedAttackDir != 0)
            {
                try { mob.dir = normalizedAttackDir; } catch { }
                return;
            }

            var target = ResolveClientAttackTargetEntity(mob, targetUserId);
            if (target == null)
                return;

            try
            {
                var mobX = GetWorldX(mob);
                var targetX = GetWorldX(target);
                var facing = targetX < mobX ? -1 : targetX > mobX ? 1 : NormalizeDir(mob.dir);
                if (facing != 0)
                    mob.dir = facing;
            }
            catch
            {
            }
        }

        private static Entity? ResolveClientAttackTargetEntity(Mob mob, int targetUserId)
        {
            if (!IsMobHostileToPlayers(mob))
                return null;

            if (targetUserId > 0)
            {
                var net = GameMenu.NetRef;
                var localId = net?.id ?? 0;
                if (localId > 0)
                {
                    if (targetUserId == localId)
                    {
                        var localHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
                        if (localHero != null && !ModEntry.IsEntityDownedForCombat(localHero))
                            return localHero;
                        return null;
                    }

                    if (ModEntry.TryGetClientIndex(localId, targetUserId, out var index))
                    {
                        var client = ModEntry.clients[index];
                        if (client != null && !ModEntry.IsEntityDownedForCombat(client))
                            return client;
                    }
                }
            }

            try
            {
                if (mob.aTarget != null && IsPlayerCombatTargetEntity(mob.aTarget))
                    return mob.aTarget;
            }
            catch
            {
            }

            try
            {
                if (mob.nemesisTarget != null && IsPlayerCombatTargetEntity(mob.nemesisTarget))
                    return mob.nemesisTarget;
            }
            catch
            {
            }

            var detected = ResolveDetectedClientTargetEntity(mob);
            if (detected != null)
                return detected;

            return null;
        }

        private static Entity? ResolveDetectedClientTargetEntity(Mob mob)
        {
            if (mob == null)
                return null;
            if (!IsMobHostileToPlayers(mob))
                return null;

            s_clientDetectedTargetsScratch.Clear();
            var hero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (hero != null)
                s_clientDetectedTargetsScratch.Add(hero);

            for (int i = 0; i < ModEntry.clients.Length; i++)
            {
                var client = ModEntry.clients[i];
                if (client != null)
                    s_clientDetectedTargetsScratch.Add(client);
            }

            Entity? best = null;
            var bestDistSq = double.MaxValue;
            var mx = GetWorldX(mob);
            var my = GetWorldY(mob);

            for (int i = 0; i < s_clientDetectedTargetsScratch.Count; i++)
            {
                var candidate = s_clientDetectedTargetsScratch[i];
                if (candidate == null || ReferenceEquals(candidate, mob))
                    continue;
                if (ModEntry.IsEntityDownedForCombat(candidate))
                    continue;

                try
                {
                    if (candidate.destroyed || candidate.life <= 0)
                        continue;
                    if (!mob.inDetectArea(candidate))
                        continue;
                }
                catch
                {
                    continue;
                }

                var dx = GetWorldX(candidate) - mx;
                var dy = GetWorldY(candidate) - my;
                var distSq = dx * dx + dy * dy;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = candidate;
                }
            }

            s_clientDetectedTargetsScratch.Clear();
            return best;
        }

        private static bool HasLocalQueuedOrChargingSkill(Mob mob)
        {
            if (mob == null)
                return false;

            try
            {
                if (mob.queuedOldSkill?.a != null)
                    return true;
            }
            catch
            {
            }

            return TryGetChargingOldSkillId(mob, out _) || TryGetChargingNewSkillId(mob, out _);
        }

        private static void RegisterClientNetworkAttackExecuted(Mob mob)
        {
            if (mob == null || !TryGetTrackedIndex(mob, out var localIndex))
                return;

            lock (Sync)
            {
                clientLastNetworkAttackTickByLocalIndex[localIndex] = Stopwatch.GetTimestamp();
            }
        }

        private static bool IsWithinClientNetworkAttackMotionPreserveWindow(Mob mob, int localIndex)
        {
            var preserveSeconds = BossSyncHelpers.IsBossMob(mob)
                ? ClientBossNetworkAttackMotionPreserveSeconds
                : ClientNetworkAttackMotionPreserveSeconds;

            return IsWithinClientNetworkAttackWindow(localIndex, preserveSeconds);
        }

        private static bool IsWithinClientNetworkAttackAiPreserveWindow(Mob mob, int localIndex)
        {
            var preserveSeconds = BossSyncHelpers.IsBossMob(mob)
                ? ClientBossNetworkAttackAiPreserveSeconds
                : ClientNetworkAttackMotionPreserveSeconds;

            return IsWithinClientNetworkAttackWindow(localIndex, preserveSeconds);
        }

        private static bool IsWithinClientNetworkAttackWindow(int localIndex, double preserveSeconds)
        {
            lock (Sync)
            {
                if (!clientLastNetworkAttackTickByLocalIndex.TryGetValue(localIndex, out var tick))
                    return false;

                var now = Stopwatch.GetTimestamp();
                return ElapsedSeconds(tick, now) <= preserveSeconds;
            }
        }

        private static void ConsumeIncomingMobHits(NetNode net)
        {
            if (!net.TryConsumeMobHits(out var hits))
                return;

            MobSyncTrace.LogRecvHits(net.IsHost ? "hitsOnHost" : "hitsOnClient", hits);
            ApplyIncomingMobHits(hits);
        }

        private static void ConsumeIncomingMobDies(NetNode net)
        {
            if (!net.TryConsumeMobDies(out var dies))
                return;

            MobSyncTrace.LogRecvDies(net.IsHost ? "diesOnHost" : "diesOnClient", dies);

            // Host is authoritative for mob death. Ignore remote client die packets.
            if (net.IsHost)
                return;

            ApplyIncomingMobDies(dies);
        }

        private static void ApplyIncomingMobDies(IReadOnlyList<NetNode.MobDie> dies)
        {
            if (dies == null || dies.Count == 0)
                return;

            s_dieVictimsScratch.Clear();
            s_dieVictimDedupScratch.Clear();
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                for (int i = 0; i < dies.Count; i++)
                {
                    var die = dies[i];
                    var mob = ResolveMobFromDieLocked(die);
                    if (mob == null)
                        continue;

                    var isBoss = BossSyncHelpers.IsBossMob(mob);
                    var life = 0;
                    try
                    {
                        life = mob.life;
                        if (mob.destroyed)
                            continue;
                    }
                    catch
                    {
                        continue;
                    }

                    if (!isBoss && life <= 0)
                        continue;

                    if (s_dieVictimDedupScratch.Add(mob))
                        s_dieVictimsScratch.Add(mob);
                }
            }

            s_dieVictimDedupScratch.Clear();

            for (int i = 0; i < s_dieVictimsScratch.Count; i++)
            {
                var mob = s_dieVictimsScratch[i];
                if (mob == null)
                    continue;

                TryWakeMobForForcedSimulation(mob);
                try
                {
                    RunWithAuthoritativeClientBossDie(mob, () =>
                    {
                        RunWithSuppressedMobDieSend(() =>
                        {
                            mob.life = 0;
                            mob.onDie();
                        });
                    });
                }
                catch
                {
                }
            }

            s_dieVictimsScratch.Clear();
        }

        private static void ApplyIncomingMobHits(IReadOnlyList<NetNode.MobHit> hits)
        {
            if (hits == null || hits.Count == 0)
                return;

            var net = GameMenu.NetRef;
            var isHost = IsHost(net);
            var hostVisibilityLeaseUntilTick = isHost
                ? OffsetTimestampBySeconds(Stopwatch.GetTimestamp(), HostClientDrawVisibilityHoldSeconds)
                : 0L;
            s_pendingMobHitAppliesScratch.Clear();

            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                for (int i = 0; i < hits.Count; i++)
                {
                    var hit = hits[i];
                    if (isHost && !IsKnownRemoteHitSenderOnHost(net, hit.UserId))
                        continue;

                    var mob = ResolveMobFromHitLocked(hit);
                    if (mob == null)
                        continue;

                    if (!TryGetMobLifeAndMaxSafe(mob, out var prevLife, out var maxLife))
                        continue;

                    var targetLife = System.Math.Clamp(hit.Hp, 0, maxLife);
                    var replaySpecialHit = false;

                    if (targetLife >= prevLife)
                    {
                        replaySpecialHit = ShouldReplayIncomingHitWithoutLifeDelta(mob);
                        if (!replaySpecialHit)
                            continue;

                        targetLife = prevLife;
                    }

                    var forceDie = targetLife <= 0 && prevLife > 0;
                    var syncId = -1;
                    TryGetMobSyncId(mob, out syncId);
                    if (isHost && syncId >= 0)
                        hostClientVisibleUntilTickBySyncId[syncId] = hostVisibilityLeaseUntilTick;
                    MobSyncTrace.LogIncomingHitApply(syncId, hit.Hp, hit.UserId, replaySpecialHit, forceDie);
                    s_pendingMobHitAppliesScratch.Add(new PendingMobHitApply(
                        mob,
                        targetLife,
                        maxLife,
                        forceDie,
                        syncId,
                        BossSyncHelpers.IsBossMob(mob),
                        replaySpecialHit));
                }
            }

            for (int i = 0; i < s_pendingMobHitAppliesScratch.Count; i++)
            {
                var update = s_pendingMobHitAppliesScratch[i];
                var mob = update.Mob;
                if (mob == null)
                    continue;

                if (isHost)
                    TryWakeMobForForcedSimulation(mob);

                var appliedLife = update.TargetLife;
                if (update.ReplaySpecialHit)
                {
                    TryWakeMobForForcedSimulation(mob);
                    TryReplayIncomingSpecialHitReaction(mob);
                    appliedLife = GetMobLifeOrFallback(mob, update.TargetLife);
                }
                else if (update.ForceDie)
                {
                    TryWakeMobForForcedSimulation(mob);
                    if (isHost)
                    {
                        if (update.IsBoss)
                        {
                            TryApplyHostBossFinishingHit(mob, update.TargetMaxLife);
                        }
                        else
                        {
                            try
                            {
                                if (!mob.destroyed)
                                {
                                    mob.life = 0;
                                    mob.onDie();
                                }
                                else
                                {
                                    mob.life = 0;
                                }
                            }
                            catch
                            {
                            }
                        }

                        appliedLife = GetMobLifeOrFallback(mob, 0);
                    }
                    else
                    {
                        ApplyAuthoritativeLifeState(mob, 0, update.TargetMaxLife);
                        appliedLife = 0;
                    }
                }
                else
                {
                    ApplyAuthoritativeLifeState(mob, update.TargetLife, update.TargetMaxLife);
                    appliedLife = GetMobLifeOrFallback(mob, update.TargetLife);
                }

                if (isHost && net != null && update.SyncId >= 0)
                {
                    var sx = GetWorldX(mob);
                    var sy = GetWorldY(mob);
                    var dir = NormalizeDir(mob.dir);
                    var hitEv = $"hit|{appliedLife.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                    var evUpdate = new NetNode.MobEventUpdate(update.SyncId, sx, sy, dir, new[] { hitEv });
                    MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(net), new[] { evUpdate });
                    net.SendMobEvents(new[] { evUpdate });
                }
            }

            s_pendingMobHitAppliesScratch.Clear();
        }

        private static void TryApplyHostBossFinishingHit(Mob mob, int targetMaxLife)
        {
            if (mob == null)
                return;

            try
            {
                var damage = System.Math.Max(1.0, targetMaxLife * 8.0);
                var attackUtils = AttackUtils.Class;
                var createFromHeroAndHit = attackUtils?.createFromHeroAndHit;
                if (createFromHeroAndHit != null)
                {
                    _ = createFromHeroAndHit(null, damage, null, mob);
                    if (mob.destroyed || GetMobLifeOrFallback(mob, 1) <= 0)
                        return;
                }

                var createFromHero = attackUtils?.createFromHero;
                var hit = attackUtils?.hit;
                if (createFromHero != null && hit != null)
                {
                    var attack = createFromHero(null, damage, null);
                    if (attack != null)
                    {
                        hit(attack, mob);
                        if (mob.destroyed || GetMobLifeOrFallback(mob, 1) <= 0)
                            return;
                    }
                }

                if (!mob.destroyed)
                {
                    mob.life = 0;
                    mob.onDie();
                    return;
                }

                mob.life = 0;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Host boss finishing hit replay failed");
            }
        }

        private static bool ShouldReplayIncomingHitWithoutLifeDelta(Mob mob)
        {
            if (mob == null)
                return false;

            var typeId = GetMobTypeIdSafe(mob);
            if (string.Equals(typeId, "mushroom", StringComparison.OrdinalIgnoreCase))
                return true;

            var runtimeClass = GetMobRuntimeClassKeySafe(mob);
            return string.Equals(runtimeClass, "Mushroom", StringComparison.OrdinalIgnoreCase);
        }

        private static void TryReplayIncomingSpecialHitReaction(Mob mob)
        {
            if (mob == null)
                return;

            try
            {
                RunWithSuppressedMobHitSend(() =>
                {
                    var attackUtils = AttackUtils.Class;
                    var createFromHeroAndHit = attackUtils?.createFromHeroAndHit;
                    if (createFromHeroAndHit != null)
                    {
                        _ = createFromHeroAndHit(null, 1.0, null, mob);
                        return;
                    }

                    var createFromHero = attackUtils?.createFromHero;
                    var hit = attackUtils?.hit;
                    if (createFromHero == null || hit == null)
                        return;

                    var attack = createFromHero(null, 1.0, null);
                    if (attack != null)
                        hit(attack, mob);
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Special incoming mob hit replay failed");
            }
        }

        private static void TryWakeMobForForcedSimulation(Mob mob)
        {
            if (mob == null)
                return;

            var refreshFrames = 1200.0;
            try
            {
                mob.isOnScreen = true;
                if (mob.onScreenRecent < refreshFrames)
                    mob.onScreenRecent = refreshFrames;
                
                mob.isOutOfGame = false;
                mob.lastOutOfGame = false;
                mob.onOutOfGameChange();
            }
            catch
            {
            }
        }

        private static bool ShouldSendHostContactPacket(int mobIndex)
        {
            if (mobIndex < 0)
                return false;

            var now = Stopwatch.GetTimestamp();

            lock (Sync)
            {
                if (hostContactAttackSendTick.TryGetValue(mobIndex, out var lastTick))
                {
                    if (ElapsedSeconds(lastTick, now) < HostContactAttackSendCooldownSeconds)
                        return false;
                }

                hostContactAttackSendTick[mobIndex] = now;
                return true;
            }
        }

        private static Mob? ResolveMobFromHitLocked(NetNode.MobHit hit)
        {
            lock (Sync)
            {
                return ResolveMobBySyncIdLocked(hit.MobIndex);
            }
        }

        private static Mob? ResolveMobFromDieLocked(NetNode.MobDie die)
        {
            lock (Sync)
            {
                return ResolveMobBySyncIdLocked(die.MobIndex);
            }
        }

        private static Mob? ResolveMobBySyncIdLocked(int mobIndex)
        {
            if (mobIndex < 0)
                return null;

            if (!SyncMobIdRegistry.TryGetMobBySyncId(mobIndex, out var mob) || mob == null || !IsSyncMob(mob))
                return null;

            try
            {
                if (mob.destroyed || mob._level == null)
                    return null;

                if (currentLevel != null && mob._level != null && !ReferenceEquals(currentLevel, mob._level))
                    return null;
            }
            catch
            {
                return null;
            }

            return mob;
        }

        private static bool IsKnownRemoteHitSenderOnHost(NetNode? net, int senderId)
        {
            if (!IsHost(net))
                return true;

            var localId = net?.id ?? 0;
            if (senderId <= 0 || senderId == localId)
                return false;

            if (!ModEntry.TryGetClientIndex(localId, senderId, out var index))
                return false;

            if (index < 0 || index >= ModEntry.clientIds.Length)
                return false;

            return ModEntry.clientIds[index] == senderId;
        }

        private static bool TryGetMobLifeAndMaxSafe(Mob mob, out int life, out int maxLife)
        {
            life = 0;
            maxLife = 1;
            if (mob == null)
                return false;

            try
            {
                life = mob.life;
                maxLife = System.Math.Max(1, mob.maxLife);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int GetMobLifeOrFallback(Mob mob, int fallback)
        {
            if (mob == null)
                return fallback;

            try
            {
                return mob.life < 0 ? 0 : mob.life;
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>Scale mob HP for multiplayer: +0.5 per player for regular mobs, +2 per player for bosses.</summary>
        private static void ScaleMobHpForMultiplayer(Mob mob)
        {
            BossHpScaling.ScaleForMultiplayer(mob);
        }
    }
}
