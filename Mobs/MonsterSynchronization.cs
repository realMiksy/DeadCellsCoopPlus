using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using dc;
using dc.en;
using dc.h2d;
using dc.libs.heaps.slib;
using dc.libs.heaps.slib._AnimManager;
using dc.pr;
using dc.tool.atk;
using dc.tool.skill;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using DeadCellsMultiplayerMod.Mobs.Levelinit;
using Hashlink.Virtuals;
using ModCore.Events;
using ModCore.Events.Interfaces.Game;
using ModCore.Utilities;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public class MobsSynchronization :
    IOnAdvancedModuleInitializing,
    IOnFrameUpdate,
    IEventReceiver
    {
        private readonly ModEntry modEntry;
        private static bool s_eventReceiverInstalled;
        private static bool s_hooksInstalled;

        private static readonly object Sync = new();
        private static readonly List<Mob> trackedMobs = new();

        private static readonly Dictionary<int, ClientMobState> clientMobTargets = new();
        private static readonly Dictionary<int, long> clientAttackUnlockUntilTick = new();
        private static readonly Dictionary<int, int> clientAttackForcedDir = new();
        private static readonly Dictionary<int, long> clientAttackForcedDirUntilTick = new();
        private static readonly Dictionary<int, long> hostContactAttackSendTick = new();
        private static readonly Dictionary<int, QueuedOldSkillMarker> clientQueuedOldSkillMarkers = new();
        private static readonly Dictionary<int, int> clientLastReportedMobLife = new();
        private static readonly Dictionary<int, long> clientLastMobHitReportTick = new();
        private static readonly Dictionary<int, string> clientLastSentAffectPayloadBySyncId = new();
        private static readonly Dictionary<int, TimedStringPayload> clientAffectSampleBySyncId = new();
        private static readonly Dictionary<int, long> clientLastSentAffectTickBySyncId = new();
        private static readonly Dictionary<int, ClientDrawSentState> clientLastSentDrawStateBySyncId = new();
        private static readonly Dictionary<int, TimedStringPayload> clientLastAppliedHostAffectPayloadBySyncId = new();
        private static readonly Dictionary<int, string> hostMobTypeBySyncId = new();
        private static readonly Dictionary<int, HostMobSentState> hostLastSentMobStatesBySyncId = new();
        private static readonly Dictionary<int, CachedHostMobPayload> hostCachedPayloadBySyncId = new();
        private static readonly Dictionary<int, long> hostAttackRetargetLockUntilTick = new();
        private static readonly List<Entity> hostDetectedTargets = new();
        private static int suppressMobDieSendDepth;

        private static Level? currentLevel;
        private static Level? lastClientNetPumpLevel;
        private static Level? lastHostNetPumpLevel;
        private static Level? lastHostStateDeltaPumpLevel;
        private static double lastClientNetPumpFrame = double.NaN;
        private static double lastHostNetPumpFrame = double.NaN;
        private static double lastHostStateDeltaPumpFrame = double.NaN;
        private static long lastClientMobDrawSendTick;
        private static long lastClientStateSendTick;
        private static long lastHostStateSendTick;
        private static int forceExactNemesisTargetDepth;
        private static readonly Dictionary<int, QueuedOldSkillMarker> hostQueuedOldSkillMarkers = new();

        private const double ClientMobDrawSendRateHz = 30.0;
        private const double ClientStateSendRateHz = 30.0;
        private const double HostStateSendRateHz = 30.0;
        private const double ClientMobDrawMinRateHz = 8.0;
        private const double ClientStateMinRateHz = 8.0;
        private const double HostStateMinRateHz = 12.0;
        private const int AdaptiveRateStartMobCount = 24;
        private const int AdaptiveRateEndMobCount = 120;
        private const double HostPayloadRefreshBaseSeconds = 0.18;
        private const double HostPayloadRefreshMaxSeconds = 0.45;
        private const double ClientAffectSampleBaseSeconds = 0.10;
        private const double ClientAffectSampleMaxSeconds = 0.28;
        private const double ClientAffectResendBaseSeconds = ClientAffectSyncSeconds;
        private const double ClientAffectResendMaxSeconds = ClientAffectSyncSeconds;
        private const double ClientDrawKeepAliveSeconds = 0.9;
        private const double ClientInterpolationAlpha = 0.7;
        private const double ClientAiLockSeconds = 0.3;
        private const double ClientAttackUnlockSeconds = 2.2;
        private const double ClientAttackForcedDirSeconds = 0.22;
        private const double HostContactAttackSendCooldownSeconds = 0.3;
        private const double ClientMobHitReportMinIntervalSeconds = 0.05;
        private const double ClientAnimSpeedEpsilon = 0.05;
        private static readonly bool ClientSyncVerticalPosition = false;
        private const double ClientTurnSnapDeltaPx = 2.0;
        private const double MobStatePositionEpsilon = 0.35;
        private const double PixelsPerCase = 24.0;
        private const double MaxCoordinateMatchDistance = 96.0;
        private const double MaxCoordinateMatchDistanceSq = MaxCoordinateMatchDistance * MaxCoordinateMatchDistance;
        private const double MobStateTypeRebindSearchRadius = 96.0;
        private const double MobStateTypeRebindSearchRadiusSq = MobStateTypeRebindSearchRadius * MobStateTypeRebindSearchRadius;
        private const string ContactAttackPacketSkillId = "@contact";
        private const string OldSkillPreparePacketPrefix = "@oldprep:";
        private const string OldSkillChargeCompletePacketPrefix = "@oldcc:";
        private const string OldSkillExecutePacketPrefix = "@oldexec:";
        private const string NewSkillExecutePacketPrefix = "@newexec:";
        private const double HostQueuedOldSkillMarkerSeconds = 3.0;
        private const double ClientQueuedOldSkillMarkerSeconds = 0.4;
        private const double HostContactRetargetLockSeconds = 0.25;
        private const double HostOldSkillRetargetLockSeconds = 0.75;
        private const double ClientAffectSyncSeconds = 0.35;

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
            entry.Logger.Information("\x1b[32m[[ModEntry.MobsSynchronization] Initializing MobsSynchronization hooks...]\x1b[0m ");

            Hook_Level.entitiesPostCreate += Hook_Level_entitiesPostCreate;
            Hook_Level.registerEntity += Hook_Level_registerEntity;
            Hook_Level.unregisterEntity += Hook_Level_unregisterEntity;
            Hook_Level.onDispose += Hook_Level_onDispose;

            Hook_Mob.setNemesisTarget += Hook_Mob_setNemesisTarget;
            Hook_Mob.preUpdate += Hook_Mob_preUpdate;
            Hook_Mob.fixedUpdate += Hook_Mob_fixedupdate;
            Hook_Mob.postUpdate += Hook_Mob_postUpdate;
            Hook_Mob.onDamage += Hook_Mob_onDamage;
            Hook_Mob.onDie += Hook_Mob_onDie;
            Hook_Mob.contactAttack += Hook_Mob_contactAttack;
            Hook_Mob.onTouch += Hook_Mob_onTouch;
            Hook_Mob.queueAttack += Hook_Mob_queueAttack;
            Hook_OldMobSkill.prepareOnOwnerTarget += Hook_OldMobSkill_prepareOnOwnerTarget;
            Hook_OldMobSkill.execute += Hook_OldMobSkill_execute;
            Hook_MobSkill.execute += Hook_MobSkill_execute;
        }

        void IOnFrameUpdate.OnFrameUpdate(double dt)
        {
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
                // Host must always consume MOBDRAW/HIT/DIE, even when tracked mobs exist.
                // Otherwise remote clients cannot wake mobs that are far from the host camera,
                // because no local mob pre/postUpdate runs for those mobs.
                ConsumeIncomingClientMobStates(net);
                ConsumeIncomingMobDraws(net);
                ConsumeIncomingMobDies(net);
                ConsumeIncomingMobHits(net);
                return;
            }

            if (IsClient(net))
            {
                if (hasTrackedMobs)
                    return;

                ConsumeIncomingHostMobStates(net);
                ConsumeIncomingHostMobAttacks(net);
                ConsumeIncomingMobDies(net);
                TrySendClientMobDraws(net);
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

            TryGetMobSyncId(mob, out _);

            lock (Sync)
            {
                if (currentLevel != null && !ReferenceEquals(currentLevel, self))
                    return;

                if (FindTrackedMobIndexLocked(mob) >= 0)
                    return;

                trackedMobs.Add(mob);
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

            EnsureMobTracked(self);

            if (isClient && ShouldRunClientNetPumpForFrame(self))
            {
                ConsumeIncomingHostMobStates(net!);
                ConsumeIncomingHostMobAttacks(net!);
                ConsumeIncomingMobDies(net!);
                TrySendClientMobDraws(net!);
                TrySendClientMobStateDeltaBatchPreUpdate(net!);
            }

            if (isClient && IsSyncMob(self))
            {
                if (!IsClientAttackUnlockActive(self))
                    TryLockMobAi(self, ClientAiLockSeconds);
            }

            if (isHost && IsSyncMob(self))
                TryAssignHostAttackTarget(self);

            orig(self);

            if (isHost && IsSyncMob(self) && ShouldRunHostStateDeltaPumpForFrame(self))
                TrySendHostMobStateDeltaBatchPreUpdate(net!);
        }

        private void Hook_Mob_fixedupdate(Hook_Mob.orig_fixedUpdate orig, Mob self)
        {
            var net = GameMenu.NetRef;
            if (IsClient(net) && IsSyncMob(self))
            {
                if (IsClientAttackUnlockActive(self))
                    orig(self);

                ApplyInterpolatedState(self);
                ApplyClientAnimationStateBeforeUpdate(self);
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
            var shouldSendDie = false;
            var dieSyncId = -1;
            var dieX = 0.0;
            var dieY = 0.0;
            NetNode? dieNet = null;
            if (self != null && suppressMobDieSendDepth <= 0)
            {
                dieNet = GameMenu.NetRef;
                if (dieNet != null && dieNet.IsAlive && TryGetMobSyncId(self, out dieSyncId))
                {
                    shouldSendDie = true;
                    dieX = GetSyncX(self);
                    dieY = GetSyncY(self);
                }
            }

            orig(self);

            if (self == null)
                return;

            if (shouldSendDie && dieNet != null && dieNet.IsAlive && dieSyncId >= 0)
            {
                dieNet.SendMobDie(dieSyncId, dieX, dieY);
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

        private void Hook_Mob_onDamage(Hook_Mob.orig_onDamage orig, Mob self, AttackData i)
        {
            orig(self, i);

            if (self == null || i == null)
                return;

            var net = GameMenu.NetRef;
            if (net == null || !IsSyncMob(self))
                return;

            bool shouldReport = false;
            if (IsHost(net))
            {
                shouldReport = true;
            }
            else if (IsClient(net))
            {
                shouldReport = IsDamageFromLocalPlayer(i);
            }

            if (!shouldReport)
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
                TrySendImmediateHostMobState(net, mobSyncId, self, GetWorldX(self), GetWorldY(self));
            }

            if (IsClient(net))
            {
                var now = Stopwatch.GetTimestamp();
                var minDelta = (long)(Stopwatch.Frequency * ClientMobHitReportMinIntervalSeconds);

                lock (Sync)
                {
                    if (clientLastReportedMobLife.TryGetValue(localIndex, out var lastLife) && life >= lastLife)
                        return;

                    if (life > 0 &&
                        clientLastMobHitReportTick.TryGetValue(localIndex, out var lastTick) &&
                        now - lastTick < minDelta)
                    {
                        clientLastReportedMobLife[localIndex] = life;
                        return;
                    }

                    clientLastReportedMobLife[localIndex] = life;
                    clientLastMobHitReportTick[localIndex] = now;
                }
            }

            net.SendMobHit(mobSyncId, life, x, y);
        }

        private static bool IsDamageFromLocalPlayer(AttackData attack)
        {
            if (attack == null)
                return false;

            var localHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (localHero == null)
                return false;

            try
            {
                var source = attack.source;
                if (source != null && ReferenceEquals(source, localHero))
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

                var owner = sourceWeapon.owner;
                if (owner != null && ReferenceEquals(owner, localHero))
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static void TrySendImmediateHostMobState(NetNode net, int mobSyncId, Mob mob, double x, double y)
        {
            if (!IsHost(net) || mob == null || mobSyncId < 0)
                return;

            var dir = NormalizeDir(mob.dir);
            var life = mob.life;
            var maxLife = mob.maxLife;
            var animPayload = BuildAnimPayload(mob);
            var mobType = BuildMobStateTypeSignature(mob);
            var statePayload = BuildMobAffectStatePayload(mob);

            var one = new List<NetNode.MobStateSnapshot>(1)
            {
                new(mobSyncId, x, y, dir, life, maxLife, animPayload, mobType, statePayload)
            };

            lock (Sync)
            {
                hostLastSentMobStatesBySyncId[mobSyncId] = new HostMobSentState(
                    x, y, dir, life, maxLife, animPayload, mobType, statePayload);
            }

            net.SendMobStates(one);
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
            var hostRateHz = ComputeAdaptiveRateHz(HostStateSendRateHz, HostStateMinRateHz, trackedMobCount);
            var minDelta = (long)(Stopwatch.Frequency / hostRateHz);
            if (lastHostStateSendTick != 0 && now - lastHostStateSendTick < minDelta)
                return;
            lastHostStateSendTick = now;

            List<Mob> mobs;
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                if (trackedMobs.Count == 0)
                    return;

                mobs = new List<Mob>(trackedMobs.Count);
                for (int i = 0; i < trackedMobs.Count; i++)
                {
                    var mob = trackedMobs[i];
                    if (mob != null && IsSyncMob(mob))
                        mobs.Add(mob);
                }
            }

            if (mobs.Count == 0)
                return;

            var deltas = new List<NetNode.MobStateSnapshot>(mobs.Count);
            for (int i = 0; i < mobs.Count; i++)
            {
                if (TryBuildHostMobStateDeltaSnapshot(mobs[i], now, trackedMobCount, out var snapshot))
                    deltas.Add(snapshot);
            }

            if (deltas.Count > 0)
                net.SendMobStates(deltas);
        }

        private static void TrySendClientMobStateDeltaBatchPreUpdate(NetNode net)
        {
            if (!IsClient(net))
                return;

            var trackedMobCount = 0;
            lock (Sync)
            {
                trackedMobCount = trackedMobs.Count;
            }

            if (trackedMobCount <= 0)
                return;

            var now = Stopwatch.GetTimestamp();
            var clientRateHz = ComputeAdaptiveRateHz(ClientStateSendRateHz, ClientStateMinRateHz, trackedMobCount);
            var minDelta = (long)(Stopwatch.Frequency / clientRateHz);
            if (lastClientStateSendTick != 0 && now - lastClientStateSendTick < minDelta)
                return;
            lastClientStateSendTick = now;

            List<Mob> mobs;
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                if (trackedMobs.Count == 0)
                    return;

                mobs = new List<Mob>(trackedMobs.Count);
                for (int i = 0; i < trackedMobs.Count; i++)
                {
                    var mob = trackedMobs[i];
                    if (mob != null && IsSyncMob(mob))
                        mobs.Add(mob);
                }
            }

            if (mobs.Count == 0)
                return;

            var affectResendTicks = (long)(Stopwatch.Frequency * ComputeAdaptiveAffectResendSeconds(mobs.Count));
            var deltas = new List<NetNode.MobStateSnapshot>(mobs.Count);
            for (int i = 0; i < mobs.Count; i++)
            {
                var mob = mobs[i];
                if (mob == null)
                    continue;
                if (!TryGetMobSyncId(mob, out var mobSyncId) || mobSyncId < 0)
                    continue;

                var statePayload = GetClientAffectPayloadForSend(mob, mobSyncId, now, mobs.Count);
                var shouldSend = false;
                var hasAnyState = !string.IsNullOrWhiteSpace(statePayload);

                lock (Sync)
                {
                    var changed = !clientLastSentAffectPayloadBySyncId.TryGetValue(mobSyncId, out var lastPayload) ||
                                  !string.Equals(lastPayload, statePayload, StringComparison.Ordinal);
                    clientLastSentAffectTickBySyncId.TryGetValue(mobSyncId, out var lastSendTick);
                    var periodicRefresh = hasAnyState && (lastSendTick == 0 || now - lastSendTick >= affectResendTicks);
                    if (changed || periodicRefresh)
                    {
                        clientLastSentAffectPayloadBySyncId[mobSyncId] = statePayload;
                        clientLastSentAffectTickBySyncId[mobSyncId] = now;
                        shouldSend = true;
                    }
                }

                if (!shouldSend)
                    continue;

                deltas.Add(new NetNode.MobStateSnapshot(
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

            if (deltas.Count > 0)
                net.SendMobStates(deltas);
        }

        private static bool TryBuildHostMobStateDeltaSnapshot(Mob mob, long nowTick, int trackedMobCount, out NetNode.MobStateSnapshot snapshot)
        {
            snapshot = default;
            if (mob == null)
                return false;

            if (!TryGetMobSyncId(mob, out var mobSyncId) || mobSyncId < 0)
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
            CachedHostMobPayload cachedPayload = default;
            var hasCachedPayload = false;

            lock (Sync)
            {
                hadPrevious = hostLastSentMobStatesBySyncId.TryGetValue(mobSyncId, out previous);
                hasCachedPayload = hostCachedPayloadBySyncId.TryGetValue(mobSyncId, out cachedPayload);
            }

            var payloadRefreshTicks = (long)(Stopwatch.Frequency * ComputeAdaptiveHostPayloadRefreshSeconds(trackedMobCount));
            var shouldRefreshPayload = !hasCachedPayload ||
                                       !hadPrevious ||
                                       nowTick - cachedPayload.Tick >= payloadRefreshTicks;

            if (shouldRefreshPayload)
            {
                animPayload = BuildAnimPayload(mob);
                mobType = BuildMobStateTypeSignature(mob);
                statePayload = BuildMobAffectStatePayload(mob);
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
            if (hadPrevious && HostMobSentStateEquals(previous, current))
                return false;

            lock (Sync)
            {
                hostLastSentMobStatesBySyncId[mobSyncId] = current;
                if (!hasCachedPayload)
                    hostCachedPayloadBySyncId[mobSyncId] = new CachedHostMobPayload(animPayload, mobType, statePayload, nowTick);
            }

            snapshot = new NetNode.MobStateSnapshot(mobSyncId, x, y, dir, life, maxLife, animPayload, mobType, statePayload);
            return true;
        }

        private static bool HostMobSentStateEquals(HostMobSentState a, HostMobSentState b)
        {
            return IsApproximatelyEqual(a.X, b.X, MobStatePositionEpsilon) &&
                   IsApproximatelyEqual(a.Y, b.Y, MobStatePositionEpsilon) &&
                   a.Dir == b.Dir &&
                   a.Life == b.Life &&
                   a.MaxLife == b.MaxLife &&
                   string.Equals(a.AnimPayload, b.AnimPayload, StringComparison.Ordinal) &&
                   string.Equals(a.Type, b.Type, StringComparison.Ordinal) &&
                   string.Equals(a.StatePayload, b.StatePayload, StringComparison.Ordinal);
        }

        private static double ComputeAdaptiveRateHz(double baseRateHz, double minRateHz, int trackedMobCount)
        {
            if (trackedMobCount <= AdaptiveRateStartMobCount)
                return baseRateHz;

            if (trackedMobCount >= AdaptiveRateEndMobCount)
                return minRateHz;

            var range = AdaptiveRateEndMobCount - AdaptiveRateStartMobCount;
            if (range <= 0)
                return minRateHz;

            var clamped = System.Math.Max(0, trackedMobCount - AdaptiveRateStartMobCount);
            var t = clamped / (double)range;
            var adaptive = baseRateHz - ((baseRateHz - minRateHz) * t);
            return System.Math.Max(minRateHz, adaptive);
        }

        private static double ComputeAdaptiveHostPayloadRefreshSeconds(int trackedMobCount)
        {
            return ComputeAdaptiveSeconds(HostPayloadRefreshBaseSeconds, HostPayloadRefreshMaxSeconds, trackedMobCount);
        }

        private static double ComputeAdaptiveClientAffectSampleSeconds(int trackedMobCount)
        {
            return ComputeAdaptiveSeconds(ClientAffectSampleBaseSeconds, ClientAffectSampleMaxSeconds, trackedMobCount);
        }

        private static double ComputeAdaptiveAffectResendSeconds(int trackedMobCount)
        {
            return ComputeAdaptiveSeconds(ClientAffectResendBaseSeconds, ClientAffectResendMaxSeconds, trackedMobCount);
        }

        private static double ComputeAdaptiveSeconds(double baseSeconds, double maxSeconds, int trackedMobCount)
        {
            if (trackedMobCount <= AdaptiveRateStartMobCount)
                return baseSeconds;

            if (trackedMobCount >= AdaptiveRateEndMobCount)
                return maxSeconds;

            var range = AdaptiveRateEndMobCount - AdaptiveRateStartMobCount;
            if (range <= 0)
                return maxSeconds;

            var clamped = System.Math.Max(0, trackedMobCount - AdaptiveRateStartMobCount);
            var t = clamped / (double)range;
            return baseSeconds + ((maxSeconds - baseSeconds) * t);
        }

        private static string GetClientAffectPayloadForSend(Mob mob, int mobSyncId, long nowTick, int trackedMobCount)
        {
            var sampleTicks = (long)(Stopwatch.Frequency * ComputeAdaptiveClientAffectSampleSeconds(trackedMobCount));
            lock (Sync)
            {
                if (clientAffectSampleBySyncId.TryGetValue(mobSyncId, out var cached) &&
                    nowTick - cached.Tick < sampleTicks)
                {
                    return cached.Payload;
                }
            }

            var payload = BuildMobAffectStatePayload(mob);
            lock (Sync)
            {
                clientAffectSampleBySyncId[mobSyncId] = new TimedStringPayload(payload, nowTick);
            }

            return payload;
        }

        private static bool IsApproximatelyEqual(double a, double b, double epsilon)
        {
            return System.Math.Abs(a - b) <= epsilon;
        }

        private static string BuildMobAffectStatePayload(Mob mob)
        {
            if (mob == null)
                return string.Empty;

            try
            {
                var affects = mob.getAllAffects();
                if (affects == null || affects.length <= 0)
                    return string.Empty;

                var ids = new List<int>(affects.length);
                for (int i = 0; i < affects.length; i++)
                {
                    var affectList = affects.getDyn(i);
                    if (TryGetDynLength(affectList) <= 0)
                        continue;

                    ids.Add(i);
                }

                if (ids.Count == 0)
                    return string.Empty;

                ids.Sort();
                var raw = string.Join(".", ids);
                if (string.IsNullOrWhiteSpace(raw))
                    return string.Empty;

                try
                {
                    return Uri.EscapeDataString(raw);
                }
                catch
                {
                    return raw;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private void Hook_Mob_contactAttack(Hook_Mob.orig_contactAttack orig, Mob self, Entity pow)
        {
            orig(self, pow);

            var net = GameMenu.NetRef;
            if (!IsHost(net) || !IsPlayerCombatTargetEntity(pow))
                return;

            if (TryGetTrackedIndex(self, out var mobIndex) && ShouldSendHostContactPacket(mobIndex))
                TrySendHostMobAttack(self, ContactAttackPacketSkillId, false, null, pow);
        }

        private void Hook_Mob_onTouch(Hook_Mob.orig_onTouch orig, Mob self, Entity atk)
        {
            orig(self, atk);

            var net = GameMenu.NetRef;
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
            orig(self, a, requiresTargetInArea, data);

            if (self == null || a == null)
                return;

            var net = GameMenu.NetRef;
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
            var x = GetWorldX(mob);
            var y = GetWorldY(mob);
            var dir = NormalizeDir(mob.dir);
            RegisterHostAttackRetargetLock(mob, skillId);
            net.SendMobAttack(mobSyncId, skillId, requiresTargetInArea, data, x, y, targetUserId, dir);
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

            if (e != null && ModEntry.IsEntityDownedForCombat(e))
            {
                try
                {
                    var team = self?._team;
                    var helper = team?.get_targetHelper();
                    if (helper != null)
                    {
                        helper.filterUntargetables();
                        var best = helper.getBest();
                        if (best != null && !ModEntry.IsEntityDownedForCombat(best))
                        {
                            orig(self, best);
                            return;
                        }
                    }
                }
                catch
                {
                }

                return;
            }

            var gameHero = ModCore.Modules.Game.Instance?.HeroInstance;
            if (gameHero != null && ReferenceEquals(e, gameHero))
            {
                try
                {
                    var team = self?._team;
                    var helper = team?.get_targetHelper();
                    if (helper != null)
                    {
                        helper.filterUntargetables();
                        var best = helper.getBest();
                        if (best != null && !ModEntry.IsEntityDownedForCombat(best))
                        {
                            orig(self, best);
                            return;
                        }
                    }
                }
                catch
                {
                }
            }

            orig(self, e);
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

                var buffer = new List<Mob>();
                var entities = level.entities;
                for (int i = 0; i < entities.length; i++)
                {
                    var mob = entities.getDyn(i) as Mob;
                    if (mob == null || !IsSyncMob(mob))
                        continue;
                    buffer.Add(mob);
                }
                
                foreach (var mob in buffer)
                {
                    if (mob != null)
                        trackedMobs.Add(mob);
                }
            }
        }

        private static void ResetMobTrackingLocked()
        {
            trackedMobs.Clear();
            clientMobTargets.Clear();
            clientAttackUnlockUntilTick.Clear();
            clientAttackForcedDir.Clear();
            clientAttackForcedDirUntilTick.Clear();
            clientQueuedOldSkillMarkers.Clear();
            hostContactAttackSendTick.Clear();
            hostAttackRetargetLockUntilTick.Clear();
            clientLastReportedMobLife.Clear();
            clientLastMobHitReportTick.Clear();
            clientLastSentAffectPayloadBySyncId.Clear();
            clientAffectSampleBySyncId.Clear();
            clientLastSentAffectTickBySyncId.Clear();
            clientLastSentDrawStateBySyncId.Clear();
            clientLastAppliedHostAffectPayloadBySyncId.Clear();
            hostMobTypeBySyncId.Clear();
            hostLastSentMobStatesBySyncId.Clear();
            hostCachedPayloadBySyncId.Clear();
            hostQueuedOldSkillMarkers.Clear();
            hostDetectedTargets.Clear();
            currentLevel = null;
            lastClientNetPumpLevel = null;
            lastHostNetPumpLevel = null;
            lastHostStateDeltaPumpLevel = null;
            lastClientNetPumpFrame = double.NaN;
            lastHostNetPumpFrame = double.NaN;
            lastHostStateDeltaPumpFrame = double.NaN;
            lastClientMobDrawSendTick = 0;
            lastClientStateSendTick = 0;
            lastHostStateSendTick = 0;
        }

        private static void RemoveTrackedMobLocked(Mob mob)
        {
            if (SyncMobIdRegistry.TryGetExistingSyncId(mob, out var syncId))
            {
                clientLastSentAffectPayloadBySyncId.Remove(syncId);
                clientAffectSampleBySyncId.Remove(syncId);
                clientLastSentAffectTickBySyncId.Remove(syncId);
                clientLastSentDrawStateBySyncId.Remove(syncId);
                clientLastAppliedHostAffectPayloadBySyncId.Remove(syncId);
                hostMobTypeBySyncId.Remove(syncId);
                hostLastSentMobStatesBySyncId.Remove(syncId);
                hostCachedPayloadBySyncId.Remove(syncId);
            }

            SyncMobIdRegistry.RemoveMob(mob);
            var index = FindTrackedMobIndexLocked(mob);
            if (index < 0)
                return;

            RemoveTrackedMobAtIndexLocked(index);
        }

        private static void RemoveTrackedMobAtIndexLocked(int index)
        {
            if (index < 0 || index >= trackedMobs.Count)
                return;

            var mob = trackedMobs[index];
            if (SyncMobIdRegistry.TryGetExistingSyncId(mob, out var syncId))
            {
                clientLastSentAffectPayloadBySyncId.Remove(syncId);
                clientAffectSampleBySyncId.Remove(syncId);
                clientLastSentAffectTickBySyncId.Remove(syncId);
                clientLastSentDrawStateBySyncId.Remove(syncId);
                clientLastAppliedHostAffectPayloadBySyncId.Remove(syncId);
                hostMobTypeBySyncId.Remove(syncId);
                hostLastSentMobStatesBySyncId.Remove(syncId);
                hostCachedPayloadBySyncId.Remove(syncId);
            }

            SyncMobIdRegistry.RemoveMob(mob);
            trackedMobs.RemoveAt(index);
            ShiftIndicesAfterRemovalLocked(index);
        }

        private static void ShiftIndicesAfterRemovalLocked(int deletedIndex)
        {
            ShiftDictionaryKeysLocked(clientMobTargets, deletedIndex);
            ShiftDictionaryKeysLocked(clientAttackUnlockUntilTick, deletedIndex);
            ShiftDictionaryKeysLocked(clientAttackForcedDir, deletedIndex);
            ShiftDictionaryKeysLocked(clientAttackForcedDirUntilTick, deletedIndex);
            ShiftDictionaryKeysLocked(clientQueuedOldSkillMarkers, deletedIndex);
            ShiftDictionaryKeysLocked(hostContactAttackSendTick, deletedIndex);
            ShiftDictionaryKeysLocked(hostAttackRetargetLockUntilTick, deletedIndex);
            ShiftDictionaryKeysLocked(clientLastReportedMobLife, deletedIndex);
            ShiftDictionaryKeysLocked(clientLastMobHitReportTick, deletedIndex);
            ShiftDictionaryKeysLocked(hostQueuedOldSkillMarkers, deletedIndex);
        }

        private static void ShiftDictionaryKeysLocked<T>(Dictionary<int, T> dict, int deletedIndex)
        {
            var keys = System.Linq.Enumerable.ToList(dict.Keys);
            foreach (var k in keys)
            {
                if (k == deletedIndex)
                {
                    dict.Remove(k);
                }
                else if (k > deletedIndex)
                {
                    var val = dict[k];
                    dict.Remove(k);
                    dict[k - 1] = val;
                }
            }
        }

        private static int FindTrackedMobIndexLocked(Mob mob)
        {
            if (mob == null || trackedMobs.Count == 0)
                return -1;

            var hasTargetSyncId = TryGetMobSyncId(mob, out var targetSyncId);

            for (int i = 0; i < trackedMobs.Count; i++)
            {
                var candidate = trackedMobs[i];
                if (candidate == null)
                    continue;

                if (ReferenceEquals(candidate, mob))
                    return i;

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
                    shouldRemove = mob.destroyed || mob._level == null || mob.life <= 0;
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
            if (mob == null)
                return false;

            try
            {
                var typeName = mob.GetType().ToString();
                
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
                    trackedMobs.Add(mob);
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

            trackedMobs.Add(mob);
            return trackedMobs.Count - 1;
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

                if (!string.IsNullOrWhiteSpace(preferredStatePayload))
                {
                    try
                    {
                        var statePayload = BuildMobAffectStatePayload(mob);
                        if (string.Equals(statePayload, preferredStatePayload, StringComparison.Ordinal))
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
            hostMobTypeBySyncId.TryGetValue(attack.Index, out var expectedType);

            if (localIndex >= 0 && localIndex < trackedMobs.Count)
            {
                var mappedMob = trackedMobs[localIndex];
                if (string.IsNullOrWhiteSpace(expectedType) || DoesMobMatchStateType(mappedMob, expectedType))
                    return localIndex;
            }

            if (string.IsNullOrWhiteSpace(expectedType))
                return -1;

            var rebindIndex = FindBestTrackedMobIndexForTypeAndPositionLocked(expectedType, attack.X, attack.Y, null);
            if (rebindIndex >= 0 && rebindIndex < trackedMobs.Count)
            {
                var candidate = trackedMobs[rebindIndex];
                if (candidate != null)
                {
                    SyncMobIdRegistry.BindSyncId(candidate, attack.Index);
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
                if (mob.destroyed || mob._level == null || mob.life <= 0)
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

        private static bool ShouldRunClientNetPumpForFrame(Mob mob)
        {
            return ShouldRunNetPumpForFrame(mob, isClientPump: true);
        }

        private static bool ShouldRunHostStateDeltaPumpForFrame(Mob mob)
        {
            var level = mob._level ?? currentLevel;
            if (level == null)
                return true;

            var frame = level.ftime;
            lock (Sync)
            {
                if (!ReferenceEquals(lastHostStateDeltaPumpLevel, level) || lastHostStateDeltaPumpFrame != frame)
                {
                    lastHostStateDeltaPumpLevel = level;
                    lastHostStateDeltaPumpFrame = frame;
                    return true;
                }
            }

            return false;
        }

        private static bool ShouldRunNetPumpForFrame(Mob mob, bool isClientPump)
        {
            var level = mob._level ?? currentLevel;
            if (level == null)
                return true;

            var frame = level.ftime;

            lock (Sync)
            {
                if (isClientPump)
                {
                    if (!ReferenceEquals(lastClientNetPumpLevel, level) || lastClientNetPumpFrame != frame)
                    {
                        lastClientNetPumpLevel = level;
                        lastClientNetPumpFrame = frame;
                        return true;
                    }

                    return false;
                }

                if (!ReferenceEquals(lastHostNetPumpLevel, level) || lastHostNetPumpFrame != frame)
                {
                    lastHostNetPumpLevel = level;
                    lastHostNetPumpFrame = frame;
                    return true;
                }

                return false;
            }
        }

        private static void TryAssignHostAttackTarget(Mob mob)
        {
            if (mob == null)
                return;
            if (!IsMobHostileToPlayers(mob))
                return;
            if (ShouldSuppressHostRetarget(mob))
                return;

            Entity? selected = null;

            lock (Sync)
            {
                hostDetectedTargets.Clear();

                TryCollectDetectedTarget(mob, ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance);

                for (int i = 0; i < ModEntry.clients.Length; i++)
                {
                    if (ModEntry.clientIds[i] <= 0)
                        continue;

                    TryCollectDetectedTarget(mob, ModEntry.clients[i]);
                }

                if (hostDetectedTargets.Count == 0)
                    return;

                var currentTarget = mob.nemesisTarget;
                if (currentTarget != null && hostDetectedTargets.Contains(currentTarget))
                {
                    selected = currentTarget;
                }
                else
                {
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
                }

                // Never keep entity wrappers across frames here.
                hostDetectedTargets.Clear();
            }

            if (selected == null)
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
                    var maxDeltaTicks = (long)(Stopwatch.Frequency * HostQueuedOldSkillMarkerSeconds);
                    if (nowTick - marker.Tick <= maxDeltaTicks)
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

        private static void TryCollectDetectedTarget(Mob mob, Entity? candidate)
        {
            if (candidate == null)
                return;
            if (ReferenceEquals(candidate, mob))
                return;
            if (ModEntry.IsEntityDownedForCombat(candidate))
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

            var until = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * seconds);
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

        private static bool IsClientAttackUnlockActive(Mob mob)
        {
            if (!TryGetTrackedIndex(mob, out var localIndex))
                return false;

            lock (Sync)
            {
                return IsClientAttackUnlockActiveLocked(localIndex, Stopwatch.GetTimestamp());
            }
        }

        private static bool IsClientAttackUnlockActiveLocked(int localIndex, long nowTick)
        {
            if (!clientAttackUnlockUntilTick.TryGetValue(localIndex, out var until))
                return false;

            if (nowTick <= until)
                return true;

            clientAttackUnlockUntilTick.Remove(localIndex);
            return false;
        }

        private static bool TryGetClientForcedDirLocked(int localIndex, long nowTick, out int dir)
        {
            dir = 0;
            if (!clientAttackForcedDir.TryGetValue(localIndex, out var rawDir))
                return false;

            if (clientAttackForcedDirUntilTick.TryGetValue(localIndex, out var until) && nowTick > until)
            {
                clientAttackForcedDir.Remove(localIndex);
                clientAttackForcedDirUntilTick.Remove(localIndex);
                return false;
            }

            dir = NormalizeDir(rawDir);
            if (dir == 0)
            {
                clientAttackForcedDir.Remove(localIndex);
                clientAttackForcedDirUntilTick.Remove(localIndex);
                return false;
            }

            return true;
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

        private static void ApplyAnimPayload(Mob mob, string? payload)
        {
            if (mob == null || mob.life <= 0 || mob.destroyed)
                return;

            if (!TryParseAnimPayload(payload, out var parsed))
                return;

            var spr = mob.spr;
            if (spr == null)
                return;

            var animManager = GetMobAnimManager(mob);
            if (animManager == null)
                return;

            try
            {
                var top = GetTopAnimInstance(animManager);
                var currentGroup = top?.group?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(currentGroup))
                    currentGroup = spr.groupName?.ToString() ?? string.Empty;

                if (!string.Equals(currentGroup, parsed.Group, StringComparison.Ordinal))
                {
                    animManager.play(parsed.Group.AsHaxeString(), null, null).loop(null);
                    top = GetTopAnimInstance(animManager);
                }

                if (top != null)
                {
                    if (top.reverse != parsed.Reverse)
                        top.reverse = parsed.Reverse;
                    if (System.Math.Abs(top.speed - parsed.Speed) > ClientAnimSpeedEpsilon)
                        top.speed = parsed.Speed;
                }
            }
            catch
            {
            }
        }

        private static void ConsumeIncomingHostMobStates(NetNode net)
        {
            if (!net.TryConsumeMobStates(out var states))
                return;

            ApplyIncomingHostMobStates(states);
        }

        private static void ConsumeIncomingClientMobStates(NetNode net)
        {
            if (!net.TryConsumeMobStates(out var states))
                return;

            ApplyIncomingClientMobStatesOnHost(states);
        }

        private static void ApplyIncomingClientMobStatesOnHost(IReadOnlyList<NetNode.MobStateSnapshot> states)
        {
            if (states == null || states.Count == 0)
                return;

            List<(Mob mob, string statePayload)> applies = new(states.Count);
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

                    applies.Add((mob, state.StatePayload));
                }
            }

            for (int i = 0; i < applies.Count; i++)
            {
                var entry = applies[i];
                ApplyClientReportedAffectStateOnHost(entry.mob, entry.statePayload);
            }
        }

        private static void ApplyClientReportedAffectStateOnHost(Mob mob, string? payload)
        {
            if (mob == null || mob.destroyed)
                return;

            var desired = ParseAffectStatePayload(payload);
            if (desired.Count == 0)
                return;

            foreach (var affectId in desired)
            {
                try
                {
                    if (mob.hasAffect(affectId))
                        mob.minTimeAffect(affectId, ClientAffectSyncSeconds);
                    else
                        mob.setAffectS(affectId, ClientAffectSyncSeconds, HaxeProxy.Runtime.Ref<double>.Null, null);
                }
                catch
                {
                }
            }
        }

        private static void ApplyIncomingHostMobStates(IReadOnlyList<NetNode.MobStateSnapshot> states)
        {
            if (states == null || states.Count == 0)
                return;

            List<(int syncId, Mob mob, int life, int maxLife, string statePayload)> stateApplies = new(states.Count);
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                var usedLocalIndices = new HashSet<int>();

                foreach (var state in states)
                {
                    hostMobTypeBySyncId[state.Index] = state.Type ?? string.Empty;

                    var localIndex = ResolveLocalIndexForIncomingStateLocked(state, usedLocalIndices);
                    if (localIndex < 0)
                        continue;

                    usedLocalIndices.Add(localIndex);

                    Mob? mob = null;
                    if (localIndex >= 0 && localIndex < trackedMobs.Count)
                        mob = trackedMobs[localIndex];
                    if (mob != null)
                    {
                        var incomingDir = NormalizeDir(state.Dir);
                        if (incomingDir != 0)
                        {
                            try { mob.dir = incomingDir; } catch { }
                        }

                        stateApplies.Add((state.Index, mob, state.Life, state.MaxLife, state.StatePayload ?? string.Empty));
                    }

                    var animPayload = state.AnimPayload;
                    clientMobTargets[localIndex] = new ClientMobState(
                        state.X,
                        state.Y,
                        NormalizeDir(state.Dir),
                        state.Life,
                        state.MaxLife,
                        animPayload,
                        state.StatePayload ?? string.Empty);
                }
            }

            for (int i = 0; i < stateApplies.Count; i++)
            {
                var entry = stateApplies[i];
                ApplyAuthoritativeLifeState(entry.mob, entry.life, entry.maxLife);
                ApplyAuthoritativeAffectState(entry.syncId, entry.mob, entry.statePayload);
            }
        }

        private static void ApplyAuthoritativeAffectState(int mobSyncId, Mob mob, string? payload)
        {
            if (mob == null || mob.destroyed)
                return;

            var safePayload = payload ?? string.Empty;
            var nowTick = Stopwatch.GetTimestamp();
            var minDelta = (long)(Stopwatch.Frequency * ClientAffectSyncSeconds);
            lock (Sync)
            {
                if (clientLastAppliedHostAffectPayloadBySyncId.TryGetValue(mobSyncId, out var lastApplied) &&
                    string.Equals(lastApplied.Payload, safePayload, StringComparison.Ordinal) &&
                    nowTick - lastApplied.Tick < minDelta)
                {
                    return;
                }

                clientLastAppliedHostAffectPayloadBySyncId[mobSyncId] = new TimedStringPayload(safePayload, nowTick);
            }

            var desired = ParseAffectStatePayload(safePayload);
            if (!TryGetCurrentAffectIds(mob, out var current))
                return;

            if (current.SetEquals(desired))
            {
                if (desired.Count == 0)
                    return;

                foreach (var affectId in desired)
                {
                    try { mob.minTimeAffect(affectId, ClientAffectSyncSeconds); } catch { }
                }
                return;
            }

            foreach (var currentId in current)
            {
                if (desired.Contains(currentId))
                    continue;

                try { mob.removeAllAffects(currentId); } catch { }
            }

            foreach (var affectId in desired)
            {
                if (current.Contains(affectId))
                {
                    try { mob.minTimeAffect(affectId, ClientAffectSyncSeconds); } catch { }
                    continue;
                }

                try { mob.setAffectS(affectId, ClientAffectSyncSeconds, HaxeProxy.Runtime.Ref<double>.Null, null); } catch { }
            }
        }

        private static bool TryGetCurrentAffectIds(Mob mob, out HashSet<int> ids)
        {
            ids = new HashSet<int>();
            if (mob == null)
                return false;

            try
            {
                var affects = mob.getAllAffects();
                if (affects == null || affects.length <= 0)
                    return true;

                for (int i = 0; i < affects.length; i++)
                {
                    var affectList = affects.getDyn(i);
                    if (TryGetDynLength(affectList) <= 0)
                        continue;
                    ids.Add(i);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static HashSet<int> ParseAffectStatePayload(string? payload)
        {
            var ids = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(payload))
                return ids;

            var decoded = payload!;
            try { decoded = Uri.UnescapeDataString(decoded); } catch { }
            if (string.IsNullOrWhiteSpace(decoded))
                return ids;

            var parts = decoded.Split('.', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    continue;
                if (id < 0)
                    continue;
                ids.Add(id);
            }

            return ids;
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

        private static void ConsumeIncomingHostMobAttacks(NetNode net)
        {
            if (!net.TryConsumeMobAttacks(out var attacks))
                return;

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
                    {
                        mob = trackedMobs[localIndex];
                        var unlockTicks = (long)(Stopwatch.Frequency * ClientAttackUnlockSeconds);
                        var forcedDirTicks = (long)(Stopwatch.Frequency * ClientAttackForcedDirSeconds);
                        var nowTick = Stopwatch.GetTimestamp();
                        clientAttackUnlockUntilTick[localIndex] = nowTick + unlockTicks;
                        var normalizedAttackDir = NormalizeDir(attack.Dir);
                        if (normalizedAttackDir != 0)
                        {
                            clientAttackForcedDir[localIndex] = normalizedAttackDir;
                            clientAttackForcedDirUntilTick[localIndex] = nowTick + forcedDirTicks;
                        }
                        else
                        {
                            clientAttackForcedDir.Remove(localIndex);
                            clientAttackForcedDirUntilTick.Remove(localIndex);
                        }
                    }
                }

                if (mob == null)
                    continue;

                TryQueueClientMobAttack(mob, attack.SkillId, attack.RequiresTargetInArea, attack.Data, attack.TargetUserId, attack.Dir);
            }
        }

        private static void TrySendClientMobDraws(NetNode net)
        {
            var trackedMobCount = 0;
            lock (Sync)
            {
                trackedMobCount = trackedMobs.Count;
            }

            if (trackedMobCount <= 0)
                return;

            var now = Stopwatch.GetTimestamp();
            var drawRateHz = ComputeAdaptiveRateHz(ClientMobDrawSendRateHz, ClientMobDrawMinRateHz, trackedMobCount);
            var minDelta = (long)(Stopwatch.Frequency / drawRateHz);
            if (lastClientMobDrawSendTick != 0 && now - lastClientMobDrawSendTick < minDelta)
                return;
            lastClientMobDrawSendTick = now;
            var keepAliveTicks = (long)(Stopwatch.Frequency * ClientDrawKeepAliveSeconds);

            List<NetNode.MobDraw> draws = new();
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                for (int i = 0; i < trackedMobs.Count; i++)
                {
                    var mob = trackedMobs[i];
                    if (!IsSyncMob(mob))
                        continue;
                    if (!TryGetMobSyncId(mob!, out var mobSyncId))
                        continue;

                    bool isOutOfGame;
                    bool isOnScreen;
                    try
                    {
                        isOutOfGame = mob!.isOutOfGame;
                        isOnScreen = mob.isOnScreen;
                    }
                    catch
                    {
                        continue;
                    }

                    if (!isOnScreen && isOutOfGame)
                    {
                        var hero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
                        if (hero?.spr == null)
                            continue;

                        var dx = GetSyncX(mob) - hero.spr.x;
                        var dy = GetSyncY(mob) - hero.spr.y;
                        var distSq = dx * dx + dy * dy;
                        if (distSq > MaxCoordinateMatchDistanceSq * 400.0)
                            continue;
                    }

                    var shouldSend = false;
                    if (!clientLastSentDrawStateBySyncId.TryGetValue(mobSyncId, out var lastDraw) ||
                        lastDraw.IsOutOfGame != isOutOfGame ||
                        lastDraw.IsOnScreen != isOnScreen ||
                        now - lastDraw.Tick >= keepAliveTicks)
                    {
                        clientLastSentDrawStateBySyncId[mobSyncId] = new ClientDrawSentState(isOutOfGame, isOnScreen, now);
                        shouldSend = true;
                    }

                    if (!shouldSend)
                        continue;

                    draws.Add(new NetNode.MobDraw(net.id, mobSyncId, isOutOfGame, isOnScreen));
                }
            }

            if (draws.Count > 0)
                net.SendMobDrawBatch(draws);
        }

        private static void ConsumeIncomingMobDraws(NetNode net)
        {
            if (!net.TryConsumeMobDraws(out var draws))
                return;

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

            if (!draw.IsOnScreen && draw.IsOutOfGame)
                return;

            var refreshFrames = 1200.0; // 20 seconds at 60fps to keep it very awake
            try
            {
                if (draw.IsOnScreen)
                    mob.isOnScreen = true;
                if (mob.onScreenRecent < refreshFrames)
                    mob.onScreenRecent = refreshFrames;
                
                // Keep 'lastOutOfGame' false to prevent immediate re-culling
                mob.lastOutOfGame = false;
            }
            catch
            {
            }

            var wasOutOfGame = false;
            try
            {
                wasOutOfGame = mob.isOutOfGame;
            }
            catch
            {
            }

            if (!wasOutOfGame)
                return;

            try
            {
                mob.isOutOfGame = false;
            }
            catch
            {
            }

            try
            {
                mob.lastOutOfGame = false;
            }
            catch
            {
            }

            try
            {
                mob.onOutOfGameChange();
            }
            catch
            {
            }
        }

        private static void TryQueueClientMobAttack(Mob mob, string skillId, bool requiresTargetInArea, int? data, int targetUserId, int attackDir)
        {
            if (mob == null || string.IsNullOrWhiteSpace(skillId))
                return;

            if (string.Equals(skillId, ContactAttackPacketSkillId, StringComparison.Ordinal))
            {
                TryApplyClientContactAttack(mob, targetUserId, attackDir);
                return;
            }

            if (skillId.StartsWith(OldSkillExecutePacketPrefix, StringComparison.Ordinal))
            {
                TryExecuteClientOldSkill(mob, skillId[OldSkillExecutePacketPrefix.Length..], data, targetUserId, attackDir);
                return;
            }

            if (skillId.StartsWith(OldSkillPreparePacketPrefix, StringComparison.Ordinal))
            {
                TryPrepareClientOldSkill(mob, skillId[OldSkillPreparePacketPrefix.Length..], data, targetUserId, attackDir);
                return;
            }

            if (skillId.StartsWith(OldSkillChargeCompletePacketPrefix, StringComparison.Ordinal))
            {
                TryExecuteClientOldSkill(mob, skillId[OldSkillChargeCompletePacketPrefix.Length..], data, targetUserId, attackDir);
                return;
            }

            if (skillId.StartsWith(NewSkillExecutePacketPrefix, StringComparison.Ordinal))
            {
                TryExecuteClientNewSkill(mob, skillId[NewSkillExecutePacketPrefix.Length..], data, targetUserId, attackDir);
                return;
            }

            try
            {
                if (IsQueuedOrChargingOldSkillId(mob, skillId))
                    return;

                TrySetClientMobAttackTarget(mob, targetUserId, attackDir, forceRetarget: true);

                var haxeSkillId = skillId.AsHaxeString();
                if (!mob.hasOldSkill(haxeSkillId))
                    return;

                var oldSkill = mob.getOldSkill(haxeSkillId) as OldMobSkill;
                if (oldSkill == null)
                    return;

                mob.queueAttack(oldSkill, requiresTargetInArea, data);
            }
            catch
            {
                // Queue packet represents early intent (prepare window).
                // Avoid forcing immediate execute here to prevent duplicate visual-only attacks.
            }
        }

        private static void TryExecuteClientOldSkill(Mob mob, string rawSkillId, int? data, int targetUserId, int attackDir)
        {
            if (mob == null || string.IsNullOrWhiteSpace(rawSkillId))
                return;

            try
            {
                var normalizedSkillId = rawSkillId.Trim();
                if (string.IsNullOrWhiteSpace(normalizedSkillId))
                    return;

                if (ShouldSkipClientOldSkillExecuteFromMarker(mob, normalizedSkillId))
                    return;

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

                TrySetClientMobAttackTarget(mob, targetUserId, attackDir, forceRetarget: true);
                TryWakeMobForForcedSimulation(mob);
                TryInvokeOldSkillChargeComplete(oldSkill);
                oldSkill.execute(null);
            }
            catch
            {
            }
        }

        private static void TryPrepareClientOldSkill(Mob mob, string rawSkillId, int? data, int targetUserId, int attackDir)
        {
            if (mob == null || string.IsNullOrWhiteSpace(rawSkillId))
                return;

            try
            {
                var normalizedSkillId = rawSkillId.Trim();
                if (string.IsNullOrWhiteSpace(normalizedSkillId))
                    return;

                var skillId = normalizedSkillId.AsHaxeString();
                if (!mob.hasOldSkill(skillId))
                    return;

                var oldSkill = mob.getOldSkill(skillId) as OldMobSkill;
                if (oldSkill == null)
                    return;

                TrySetClientMobAttackTarget(mob, targetUserId, attackDir, forceRetarget: true);
                TryWakeMobForForcedSimulation(mob);

                if (!TryExecuteClientOldSkillNativeLike(oldSkill, data))
                {
                    oldSkill.prepare(data);
                }
            }
            catch
            {
            }
        }

        private static void TryInvokeOldSkillChargeComplete(OldMobSkill oldSkill)
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
            catch
            {
            }
        }

        private static void TryExecuteClientNewSkill(Mob mob, string rawSkillId, int? data, int targetUserId, int attackDir)
        {
            if (mob == null || string.IsNullOrWhiteSpace(rawSkillId))
                return;

            try
            {
                var normalizedSkillId = rawSkillId.Trim();
                if (string.IsNullOrWhiteSpace(normalizedSkillId))
                    return;

                if (TryGetChargingNewSkillId(mob, out var chargingNewSkillId))
                {
                    if (!string.Equals(chargingNewSkillId, normalizedSkillId, StringComparison.Ordinal))
                        return;

                    var chargingSkill = mob.getChargingNewSkill() as MobSkill;
                    if (chargingSkill == null)
                        return;

                    chargingSkill.execute(null);
                    return;
                }

                TrySetClientMobAttackTarget(mob, targetUserId, attackDir, forceRetarget: true);

                var skillId = normalizedSkillId.AsHaxeString();
                var skill = mob.getSkill(skillId) as MobSkill;
                if (skill == null)
                    return;

                skill.prepare(data);
                skill.execute(null);
            }
            catch
            {
            }
        }

        private static bool TryExecuteClientOldSkillNativeLike(OldMobSkill oldSkill, int? data)
        {
            if (oldSkill == null)
                return false;

            try
            {
                oldSkill.prepareOnOwnerTarget(true, data);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TryApplyClientContactAttack(Mob mob, int targetUserId, int attackDir)
        {
            var target = ResolveClientAttackTargetEntity(mob, targetUserId);
            if (target == null)
                return;

            TrySetClientMobAttackTarget(mob, targetUserId, attackDir, forceRetarget: true);
            TryWakeMobForForcedSimulation(mob);

            try
            {
                // Use native contact pipeline to keep melee timing/fx consistent with host.
                mob.contactAttack(target);
                return;
            }
            catch
            {
            }

            try
            {
                mob.onTouch(target);
            }
            catch
            {
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
            var maxDeltaTicks = (long)(Stopwatch.Frequency * ClientQueuedOldSkillMarkerSeconds);
            var nowTick = Stopwatch.GetTimestamp();

            lock (Sync)
            {
                if (!clientQueuedOldSkillMarkers.TryGetValue(localIndex, out marker))
                    return false;

                if (nowTick - marker.Tick > maxDeltaTicks)
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

        private static void TrySetClientMobAttackTarget(Mob mob, int targetUserId, int attackDir, bool forceRetarget = false)
        {
            var target = ResolveClientAttackTargetEntity(mob, targetUserId);
            if (target == null)
                return;

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

            var candidates = new List<Entity>(4);
            var hero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (hero != null)
                candidates.Add(hero);

            for (int i = 0; i < ModEntry.clients.Length; i++)
            {
                var client = ModEntry.clients[i];
                if (client != null)
                    candidates.Add(client);
            }

            Entity? best = null;
            var bestDistSq = double.MaxValue;
            var mx = GetWorldX(mob);
            var my = GetWorldY(mob);

            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
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

            return best;
        }

        private static void ApplyInterpolatedState(Mob self)
        {
            if (!TryGetTrackedIndex(self, out var localIndex))
                return;

            ClientMobState target;
            var forcedDir = 0;
            var useForcedDir = false;
            var preserveLocalMotion = false;
            lock (Sync)
            {
                if (!clientMobTargets.TryGetValue(localIndex, out target))
                    return;

                var nowTick = Stopwatch.GetTimestamp();
                var attackUnlock = IsClientAttackUnlockActiveLocked(localIndex, nowTick);
                if (attackUnlock &&
                    TryGetClientForcedDirLocked(localIndex, nowTick, out var attackDir))
                {
                    forcedDir = NormalizeDir(attackDir);
                    useForcedDir = forcedDir != 0;
                }

                preserveLocalMotion = attackUnlock && ShouldPreserveClientAttackMotion(self);
            }

            if (!preserveLocalMotion)
            {
                var currentX = GetWorldX(self);
                var currentY = GetWorldY(self);
                var lerpedX = currentX + (target.X - currentX) * ClientInterpolationAlpha;
                var lerpedY = ClientSyncVerticalPosition
                    ? currentY + (target.Y - currentY) * ClientInterpolationAlpha
                    : currentY;

                try
                {
                    if (ClientSyncVerticalPosition)
                        self.setPosPixel(lerpedX, lerpedY);
                    else
                        SetWorldXKeepingY(self, lerpedX);
                }
                catch
                {
                    if (self.spr != null)
                    {
                        self.spr.x = lerpedX;
                        if (ClientSyncVerticalPosition)
                            self.spr.y = lerpedY;
                    }
                }

                try
                {
                    self.dx = 0;
                    self.bdx = 0;
                    if (ClientSyncVerticalPosition)
                    {
                        self.dy = 0;
                        self.bdy = 0;
                        self.fallStartY = lerpedY;
                    }
                    self.hasGravity = true;
                }
                catch
                {
                }
            }

            if (useForcedDir)
                self.dir = forcedDir;
            else
            {
                var responsiveDir = ComputeResponsiveFacingDir(self, target);
                if (responsiveDir != 0)
                    self.dir = responsiveDir;
            }

            ApplyAuthoritativeLifeState(self, target.Life, target.MaxLife);
        }

        private static bool ShouldPreserveClientAttackMotion(Mob mob)
        {
            if (mob == null)
                return false;

            if (HasLocalQueuedOrChargingSkill(mob))
                return true;

            try
            {
                var motion =
                    System.Math.Abs(mob.dx) +
                    System.Math.Abs(mob.bdx) +
                    System.Math.Abs(mob.dy) +
                    System.Math.Abs(mob.bdy);
                return motion > 0.02;
            }
            catch
            {
                return false;
            }
        }

        private static void ApplyAuthoritativeLifeState(Mob mob, int targetLife, int targetMaxLife)
        {
            if (mob == null)
                return;

            if (targetMaxLife > 0 && mob.maxLife != targetMaxLife)
                mob.maxLife = targetMaxLife;

            var clampedLife = targetLife;
            if (mob.maxLife > 0)
                clampedLife = System.Math.Clamp(clampedLife, 0, mob.maxLife);
            else if (clampedLife < 0)
                clampedLife = 0;

            if (mob.life == clampedLife)
                return;

            var wasAlive = mob.life > 0;
            mob.life = clampedLife;

            if (mob.life <= 0 && wasAlive)
            {
                try
                {
                    if (!mob.destroyed)
                    {
                        RunWithSuppressedMobDieSend(() =>
                        {
                            mob.life = 0;
                            mob.onDie();
                        });
                    }

                    var animManager = GetMobAnimManager(mob);
                    if (animManager?.stack != null)
                    {
                        while (animManager.stack.length > 0)
                            animManager.stack.pop();
                    }
                }
                catch
                {
                }
            }
        }

        private static void ApplyClientAnimationStateBeforeUpdate(Mob self)
        {
            if (!TryGetTrackedIndex(self, out var localIndex))
                return;

            ClientMobState target;
            var forcedDir = 0;
            var useForcedDir = false;
            var attackUnlock = false;
            lock (Sync)
            {
                if (!clientMobTargets.TryGetValue(localIndex, out target))
                    return;

                var nowTick = Stopwatch.GetTimestamp();
                attackUnlock = IsClientAttackUnlockActiveLocked(localIndex, nowTick);
                if (attackUnlock && TryGetClientForcedDirLocked(localIndex, nowTick, out var attackDir))
                {
                    forcedDir = NormalizeDir(attackDir);
                    useForcedDir = forcedDir != 0;
                }
            }

            if (useForcedDir)
                self.dir = forcedDir;
            else
            {
                var responsiveDir = ComputeResponsiveFacingDir(self, target);
                if (responsiveDir != 0)
                    self.dir = responsiveDir;
            }

            if (attackUnlock && HasLocalQueuedOrChargingSkill(self))
                return;

            ApplyAnimPayload(self, target.AnimPayload);
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

        private static void ConsumeIncomingMobHits(NetNode net)
        {
            if (!net.TryConsumeMobHits(out var hits))
                return;

            ApplyIncomingMobHits(hits);
        }

        private static void ConsumeIncomingMobDies(NetNode net)
        {
            if (!net.TryConsumeMobDies(out var dies))
                return;

            ApplyIncomingMobDies(dies);
        }

        private static void ApplyIncomingMobDies(IReadOnlyList<NetNode.MobDie> dies)
        {
            if (dies == null || dies.Count == 0)
                return;

            List<Mob> victims = new(dies.Count);
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                foreach (var die in dies)
                {
                    var mob = ResolveMobFromDieLocked(die);
                    if (mob == null)
                        continue;

                    var life = 0;
                    try
                    {
                        life = mob.life;
                    }
                    catch
                    {
                        continue;
                    }

                    if (life <= 0)
                        continue;

                    var alreadyAdded = false;
                    for (int i = 0; i < victims.Count; i++)
                    {
                        if (ReferenceEquals(victims[i], mob))
                        {
                            alreadyAdded = true;
                            break;
                        }
                    }

                    if (!alreadyAdded)
                        victims.Add(mob);
                }
            }

            for (int i = 0; i < victims.Count; i++)
            {
                var mob = victims[i];
                if (mob == null)
                    continue;

                TryWakeMobForForcedSimulation(mob);
                try
                {
                    RunWithSuppressedMobDieSend(() =>
                    {
                        mob.life = 0;
                        mob.onDie();
                    });
                }
                catch
                {
                }
            }
        }

        private static void ApplyIncomingMobHits(IReadOnlyList<NetNode.MobHit> hits)
        {
            if (hits == null || hits.Count == 0)
                return;

            var net = GameMenu.NetRef;
            var isHost = IsHost(net);
            var pending = new List<(Mob Mob, int TargetLife, int TargetMaxLife, bool ForceDie, int SyncId)>(hits.Count);

            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                foreach (var hit in hits)
                {
                    if (isHost && !IsKnownRemoteHitSenderOnHost(net, hit.UserId))
                        continue;

                    var mob = ResolveMobFromHitLocked(hit);
                    if (mob == null)
                        continue;

                    if (!TryGetMobLifeAndMaxSafe(mob, out var prevLife, out var maxLife))
                        continue;

                    var targetLife = System.Math.Clamp(hit.Hp, 0, maxLife);

                    if (targetLife >= prevLife)
                        continue;

                    var forceDie = targetLife <= 0 && prevLife > 0;
                    var syncId = -1;
                    TryGetMobSyncId(mob, out syncId);
                    pending.Add((mob, targetLife, maxLife, forceDie, syncId));
                }
            }

            for (int i = 0; i < pending.Count; i++)
            {
                var update = pending[i];
                var mob = update.Mob;
                if (mob == null)
                    continue;

                if (update.ForceDie)
                {
                    TryWakeMobForForcedSimulation(mob);
                    if (isHost)
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
                    else
                    {
                        ApplyAuthoritativeLifeState(mob, 0, update.TargetMaxLife);
                    }
                }
                else
                {
                    ApplyAuthoritativeLifeState(mob, update.TargetLife, update.TargetMaxLife);
                }

                if (isHost && net != null && update.SyncId >= 0)
                {
                    var sx = GetWorldX(mob);
                    var sy = GetWorldY(mob);
                    TrySendImmediateHostMobState(net, update.SyncId, mob, sx, sy);
                }
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
            var minDelta = (long)(Stopwatch.Frequency * HostContactAttackSendCooldownSeconds);

            lock (Sync)
            {
                if (hostContactAttackSendTick.TryGetValue(mobIndex, out var lastTick))
                {
                    if (now - lastTick < minDelta)
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
    }
}
