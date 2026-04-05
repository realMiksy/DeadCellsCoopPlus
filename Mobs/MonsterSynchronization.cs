using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using dc;
using dc.en;
using dc.h2d;
using dc.libs.heaps.slib;
using dc.libs.heaps.slib._AnimManager;
using dc.pr;
using dc.tool;
using dc.tool.atk;
using dc.tool.skill;
using DeadCellsMultiplayerMod.Ghost;
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
        private static readonly List<NetNode.MobHit> s_deferredMobHitsScratch = new();
        private static readonly List<NetNode.MobHit> s_mobHitMergeScratch = new();
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
        private static double s_lastPruneFrame = double.NaN;

        /// <summary>Per-type eligibility cache so IsSyncMob never allocates a string on the hot per-frame path.</summary>
        private static readonly ConcurrentDictionary<System.Type, bool> s_syncMobTypeCache = new();

        /// <summary>Thread-local reuse buffer for single-element string[] event arrays; avoids per-event GC allocation.</summary>
        [ThreadStatic]
        private static string[]? s_singleEventBuf;

        /// <summary>Thread-local reuse buffer for single-element MobEventUpdate[] arrays; avoids per-send GC allocation.</summary>
        [ThreadStatic]
        private static NetNode.MobEventUpdate[]? s_singleUpdateBuf;

        private static string[] SingleEvent(string ev)
        {
            var buf = s_singleEventBuf ??= new string[1];
            buf[0] = ev;
            return buf;
        }

        private static NetNode.MobEventUpdate[] SingleUpdate(NetNode.MobEventUpdate update)
        {
            var buf = s_singleUpdateBuf ??= new NetNode.MobEventUpdate[1];
            buf[0] = update;
            return buf;
        }

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
                RunHostIncomingFrameConsumeAsync(net).GetAwaiter().GetResult();
                if (hasTrackedMobs)
                    TrySendHostMobStateDeltaBatchPreUpdate(net);

                return;
            }

            if (IsClient(net))
            {
                RunClientIncomingFrameConsumeAsync(net).GetAwaiter().GetResult();
                if (hasTrackedMobs)
                {
                    if (!TryCaptureTrackedMobsForBatch(out var trackedMobCount))
                        return;

                    var now = Stopwatch.GetTimestamp();
                    TrySendClientMobBatchesNetFrame(net, now);
                }
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
                ApplyInterpolatedState(self);
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
                    ApplyClientAnimationStateBeforeUpdate(self);

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
                var update = new NetNode.MobEventUpdate(dieSyncId, dieX, dieY, 0, SingleEvent("die"));
                MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(dieNet), SingleUpdate(update));
                dieNet.SendMobEvents(SingleUpdate(update));
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
                    var update = new NetNode.MobEventUpdate(mobSyncId, hx, hy, NormalizeDir(self.dir), SingleEvent(hitEvent));
                    MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(net), SingleUpdate(update));
                    net.SendMobEvents(SingleUpdate(update));
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
                    var clientUpdate = new NetNode.MobEventUpdate(mobSyncId, x, y, 0, SingleEvent(clientHitEvent));
                    MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(net), SingleUpdate(clientUpdate));
                    net.SendMobEvents(SingleUpdate(clientUpdate));
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

            try
            {
                InventItem sourceItem;
                try
                {
                    sourceItem = attack.sourceItem;
                }
                catch
                {
                    sourceItem = null!;
                }

                if (sourceItem != null &&
                    KingWeaponSupport.TryGetSourceByItem(sourceItem, out var kingSkin) &&
                    kingSkin != null &&
                    !IsKnownRemoteClientEntity(kingSkin))
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

    }
}
