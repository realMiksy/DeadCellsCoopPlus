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
using ModCore.Events;
using ModCore.Events.Interfaces.Game;
using ModCore.Modules;
using ModCore.Utilities;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public class MobsSynchronization :
    IOnAdvancedModuleInitializing,
    IOnFrameUpdate,
    IEventReceiver
    {
        private readonly ModEntry modEntry;

        private static readonly object Sync = new();
        private static readonly List<Mob> trackedMobs = new();

        private static readonly Dictionary<int, ClientMobState> clientMobTargets = new();
        private static readonly Dictionary<int, long> clientAttackUnlockUntilTick = new();
        private static readonly Dictionary<int, long> hostContactAttackSendTick = new();
        private static readonly Dictionary<int, int> clientLastReportedMobLife = new();
        private static readonly Dictionary<int, long> clientLastMobHitReportTick = new();
        private static readonly List<Entity> hostDetectedTargets = new();
        private static readonly Random hostTargetRandom = new();
        private static int suppressMobDieSendDepth;

        private static Level? currentLevel;
        private static Level? lastClientNetPumpLevel;
        private static Level? lastHostNetPumpLevel;
        private static double lastClientNetPumpFrame = double.NaN;
        private static double lastHostNetPumpFrame = double.NaN;
        private static long lastClientMobDrawSendTick;
        private static long lastHostStateSendTick;
        private static int forceExactNemesisTargetDepth;
        private static readonly Dictionary<int, QueuedOldSkillMarker> hostQueuedOldSkillMarkers = new();

        private const double ClientMobDrawSendRateHz = 60.0;
        private const double HostStateSendRateHz = 60.0;
        private const double ClientInterpolationAlpha = 0.5;
        private const double ClientAiLockSeconds = 0.3;
        private const double ClientAttackUnlockSeconds = 2.2;
        private const double HostContactAttackSendCooldownSeconds = 0.3;
        private const double ClientMobHitReportMinIntervalSeconds = 0.05;
        private const double ClientAnimSpeedEpsilon = 0.05;
        private static readonly bool ClientSyncVerticalPosition = false;
        private const double PixelsPerCase = 24.0;
        private const double MaxCoordinateMatchDistance = 96.0;
        private const double MaxCoordinateMatchDistanceSq = MaxCoordinateMatchDistance * MaxCoordinateMatchDistance;
        private const string ContactAttackPacketSkillId = "@contact";
        private const string OldSkillExecutePacketPrefix = "@oldexec:";
        private const string NewSkillExecutePacketPrefix = "@newexec:";
        private const double HostQueuedOldSkillMarkerSeconds = 3.0;

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

            public ClientMobState(double x, double y, int dir, int life, int maxLife, string animPayload)
            {
                X = x;
                Y = y;
                Dir = dir;
                Life = life;
                MaxLife = maxLife;
                AnimPayload = animPayload ?? string.Empty;
            }
        }

        public MobsSynchronization(ModEntry entry)
        {
            EventSystem.AddReceiver(this);
            modEntry = entry;
        }

        public static void ClearTrackingForLevelChange()
        {
            lock (Sync)
            {
                ResetMobTrackingLocked();
            }
        }

        public void OnAdvancedModuleInitializing(ModEntry entry)
        {
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
            Hook_OldMobSkill.execute += Hook_OldMobSkill_execute;
            Hook_MobSkill.execute += Hook_MobSkill_execute;
        }

        void IOnFrameUpdate.OnFrameUpdate(double dt)
        {
            var net = GameMenu.NetRef;
            if (net == null || !net.IsAlive)
                return;

            if (IsHost(net))
            {
                ConsumeIncomingMobDraws(net);
                ConsumeIncomingMobDies(net);
                ConsumeIncomingMobHits(net);
                TrySendHostMobStates(net);
                return;
            }

            if (IsClient(net))
            {
                ConsumeIncomingHostMobAttacks(net);
                ConsumeIncomingHostMobStates(net);
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
                ConsumeIncomingHostMobAttacks(net!);
                ConsumeIncomingHostMobStates(net!);
                ConsumeIncomingMobDies(net!);
                TrySendClientMobDraws(net!);
            }

            if (isClient && IsSyncMob(self))
            {
                ApplyClientAnimationStateBeforeUpdate(self);
                if (!IsClientAttackUnlockActive(self))
                    TryLockMobAi(self, ClientAiLockSeconds);
            }

            if (isHost && IsSyncMob(self))
                TryAssignHostAttackTarget(self);

            orig(self);
        }

        private void Hook_Mob_fixedupdate(Hook_Mob.orig_fixedUpdate orig, Mob self)
        {
            var net = GameMenu.NetRef;
            if (IsClient(net) && IsSyncMob(self))
            {
                ApplyInterpolatedState(self);
                if (IsClientAttackUnlockActive(self))
                    orig(self);
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
                return;
            }

            EnsureMobTracked(self);
            orig(self);

            if (IsSyncMob(self) && ShouldRunHostNetPumpForFrame(self))
            {
                ConsumeIncomingMobDraws(net!);
                ConsumeIncomingMobDies(net!);
                ConsumeIncomingMobHits(net!);
                TrySendHostMobStates(net!);
            }
        }

        private static void Hook_Mob_onDie(Hook_Mob.orig_onDie orig, Mob self)
        {
            orig(self);

            if (self == null)
                return;

            if (suppressMobDieSendDepth <= 0)
            {
                var net = GameMenu.NetRef;
                if (net != null && net.IsAlive && TryGetMobSyncId(self, out var mobSyncId))
                {
                    var x = GetSyncX(self);
                    var y = GetSyncY(self);
                    net.SendMobDie(mobSyncId, x, y);
                }
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
                if (i.source == null || (ModEntry.me != null && ReferenceEquals(i.source, ModEntry.me)))
                {
                    shouldReport = true;
                }
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
                TrySendImmediateHostMobState(net, mobSyncId, self, x, y);
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

            if (IsClient(net) && life <= 0)
                net.SendMobDie(mobSyncId, x, y);
        }

        private static void TrySendImmediateHostMobState(NetNode net, int mobSyncId, Mob mob, double x, double y)
        {
            if (!IsHost(net) || mob == null || mobSyncId < 0)
                return;

            var dir = NormalizeDir(mob.dir);
            var life = mob.life;
            var maxLife = mob.maxLife;
            var animPayload = BuildAnimPayload(mob);

            string mobType;
            try
            {
                mobType = mob.type?.ToString() ?? string.Empty;
            }
            catch
            {
                mobType = string.Empty;
            }

            var one = new List<NetNode.MobStateSnapshot>(1)
            {
                new(mobSyncId, x, y, dir, life, maxLife, animPayload, mobType)
            };

            net.SendMobStates(one);
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
            if (!IsHost(net))
                return;

            var ownerMob = self?.owner as Mob;
            var skillId = self?.id?.ToString() ?? string.Empty;
            
            if (ownerMob == null || string.IsNullOrWhiteSpace(skillId) || ShouldSuppressOldExecutePacket(ownerMob, skillId))
                return;

            TrySendHostMobAttack(ownerMob, OldSkillExecutePacketPrefix + skillId, false, null);
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
            var x = GetSyncX(mob);
            var y = GetSyncY(mob);
            net.SendMobAttack(mobSyncId, skillId, requiresTargetInArea, data, x, y, targetUserId);
        }

        private void Hook_Mob_setNemesisTarget(Hook_Mob.orig_setNemesisTarget orig, Mob self, Entity e)
        {
            if (System.Threading.Volatile.Read(ref forceExactNemesisTargetDepth) > 0)
            {
                orig(self, e);
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
                        if (best != null)
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
            hostContactAttackSendTick.Clear();
            clientLastReportedMobLife.Clear();
            clientLastMobHitReportTick.Clear();
            hostQueuedOldSkillMarkers.Clear();
            hostDetectedTargets.Clear();
            currentLevel = null;
            lastClientNetPumpLevel = null;
            lastHostNetPumpLevel = null;
            lastClientNetPumpFrame = double.NaN;
            lastHostNetPumpFrame = double.NaN;
            lastClientMobDrawSendTick = 0;
            lastHostStateSendTick = 0;
        }

        private static void RemoveTrackedMobLocked(Mob mob)
        {
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
            SyncMobIdRegistry.RemoveMob(mob);
            trackedMobs.RemoveAt(index);
            ShiftIndicesAfterRemovalLocked(index);
        }

        private static void ShiftIndicesAfterRemovalLocked(int deletedIndex)
        {
            ShiftDictionaryKeysLocked(clientMobTargets, deletedIndex);
            ShiftDictionaryKeysLocked(clientAttackUnlockUntilTick, deletedIndex);
            ShiftDictionaryKeysLocked(hostContactAttackSendTick, deletedIndex);
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

            for (int i = 0; i < trackedMobs.Count; i++)
            {
                var candidate = trackedMobs[i];
                if (candidate == null)
                    continue;

                if (ReferenceEquals(candidate, mob))
                    return i;

                try
                {
                    if (Equals(candidate, mob))
                        return i;
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

        private static bool ShouldRunHostNetPumpForFrame(Mob mob)
        {
            return ShouldRunNetPumpForFrame(mob, isClientPump: false);
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
                else if (hostDetectedTargets.Count == 1)
                {
                    selected = hostDetectedTargets[0];
                }
                else
                {
                    selected = hostDetectedTargets[hostTargetRandom.Next(hostDetectedTargets.Count)];
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

        private static void TryCollectDetectedTarget(Mob mob, Entity? candidate)
        {
            if (candidate == null)
                return;
            if (ReferenceEquals(candidate, mob))
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

        private static int ResolveHostTargetUserId(Entity? target, int localUserId)
        {
            if (target == null || localUserId <= 0)
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
            if (explicitTarget != null)
                return explicitTarget;

            try
            {
                if (mob.aTarget != null)
                    return mob.aTarget;
            }
            catch
            {
            }

            try
            {
                if (mob.nemesisTarget != null)
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

        private static bool ShouldSuppressOldExecutePacket(Mob ownerMob, string skillId)
        {
            if (ownerMob == null || string.IsNullOrWhiteSpace(skillId))
                return false;

            if (!TryGetTrackedIndex(ownerMob, out var mobIndex))
                return false;

            lock (Sync)
            {
                if (!hostQueuedOldSkillMarkers.TryGetValue(mobIndex, out var marker))
                    return false;

                var maxDeltaTicks = (long)(Stopwatch.Frequency * HostQueuedOldSkillMarkerSeconds);
                if (!string.Equals(marker.SkillId, skillId, StringComparison.Ordinal) ||
                    Stopwatch.GetTimestamp() - marker.Tick > maxDeltaTicks)
                {
                    hostQueuedOldSkillMarkers.Remove(mobIndex);
                    return false;
                }

                hostQueuedOldSkillMarkers.Remove(mobIndex);
                return true;
            }
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

        private static void TrySetMobAttackTargetsExact(Mob mob, Entity target)
        {
            if (mob == null || target == null)
                return;

            try
            {
                var mobX = GetWorldX(mob);
                var targetX = GetWorldX(target);
                var desiredDir = targetX < mobX ? -1 : targetX > mobX ? 1 : mob.dir;
                if (desiredDir != 0)
                    mob.dir = desiredDir;
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

        private static int NormalizeDir(int dir)
        {
            if (dir < 0) return -1;
            if (dir > 0) return 1;
            return 0;
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
            var xCase = (int)(worldX / PixelsPerCase);
            var xFrac = (worldX - xCase * PixelsPerCase) / PixelsPerCase;
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
                var currentGroup = spr.groupName?.ToString() ?? string.Empty;
                if (!string.Equals(currentGroup, parsed.Group, StringComparison.Ordinal))
                {
                    animManager.play(parsed.Group.AsHaxeString(), null, null).loop(null);
                }
            }
            catch
            {
            }

            try
            {
                var top = GetTopAnimInstance(animManager);
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

        private static void TrySendHostMobStates(NetNode net)
        {
            var now = Stopwatch.GetTimestamp();
            var minDelta = (long)(Stopwatch.Frequency / HostStateSendRateHz);
            if (lastHostStateSendTick != 0 && now - lastHostStateSendTick < minDelta)
                return;
            lastHostStateSendTick = now;

            List<NetNode.MobStateSnapshot> states = new();
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                for (int i = 0; i < trackedMobs.Count; i++)
                {
                    var mob = trackedMobs[i];
                    if (mob == null)
                        continue;

                    if (!TryGetMobSyncId(mob, out var mobSyncId))
                        continue;

                    bool isOutOfGame;
                    try
                    {
                        isOutOfGame = mob.isOutOfGame;
                    }
                    catch
                    {
                        isOutOfGame = false;
                    }

                    var x = GetSyncX(mob);
                    var y = GetSyncY(mob);
                    var dir = NormalizeDir(mob.dir);
                    var life = mob.life;
                    var maxLife = mob.maxLife;
                    var animPayload = BuildAnimPayload(mob);
                    
                    string mobType;
                    try
                    {
                        mobType = mob.type?.ToString() ?? string.Empty;
                    }
                    catch
                    {
                        mobType = string.Empty;
                    }

                    states.Add(new NetNode.MobStateSnapshot(mobSyncId, x, y, dir, life, maxLife, animPayload, mobType));
                }
            }

            if (states.Count > 0)
                net.SendMobStates(states);
        }

        private static void ConsumeIncomingHostMobStates(NetNode net)
        {
            if (!net.TryConsumeMobStates(out var states))
                return;

            ApplyIncomingHostMobStates(states);
        }

        private static void ApplyIncomingHostMobStates(IReadOnlyList<NetNode.MobStateSnapshot> states)
        {
            if (states == null || states.Count == 0)
                return;

            List<(Mob mob, int life, int maxLife)> lifeApplies = new(states.Count);
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                var nowTick = Stopwatch.GetTimestamp();

                foreach (var state in states)
                {
                    var localIndex = ResolveLocalIndexBySyncIdLocked(state.Index);
                    if (localIndex < 0)
                        continue;

                    Mob? mob = null;
                    if (localIndex >= 0 && localIndex < trackedMobs.Count)
                        mob = trackedMobs[localIndex];
                    if (mob != null)
                        lifeApplies.Add((mob, state.Life, state.MaxLife));

                    var animPayload = state.AnimPayload;
                    if (IsClientAttackUnlockActiveLocked(localIndex, nowTick) &&
                        clientMobTargets.TryGetValue(localIndex, out var prevState))
                    {
                        animPayload = prevState.AnimPayload;
                    }

                    clientMobTargets[localIndex] = new ClientMobState(
                        state.X,
                        state.Y,
                        NormalizeDir(state.Dir),
                        state.Life,
                        state.MaxLife,
                        animPayload);
                }
            }

            for (int i = 0; i < lifeApplies.Count; i++)
            {
                var entry = lifeApplies[i];
                ApplyAuthoritativeLifeState(entry.mob, entry.life, entry.maxLife);
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
                    var localIndex = ResolveLocalIndexBySyncIdLocked(attack.Index);
                    if (localIndex >= 0 && localIndex < trackedMobs.Count)
                    {
                        mob = trackedMobs[localIndex];
                        var unlockTicks = (long)(Stopwatch.Frequency * ClientAttackUnlockSeconds);
                        clientAttackUnlockUntilTick[localIndex] = Stopwatch.GetTimestamp() + unlockTicks;
                    }
                }

                if (mob == null)
                    continue;

                TryQueueClientMobAttack(mob, attack.SkillId, attack.RequiresTargetInArea, attack.Data, attack.TargetUserId);
            }
        }

        private static void TrySendClientMobDraws(NetNode net)
        {
            var now = Stopwatch.GetTimestamp();
            var minDelta = (long)(Stopwatch.Frequency / ClientMobDrawSendRateHz);
            if (lastClientMobDrawSendTick != 0 && now - lastClientMobDrawSendTick < minDelta)
                return;
            lastClientMobDrawSendTick = now;

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

        private static void TryQueueClientMobAttack(Mob mob, string skillId, bool requiresTargetInArea, int? data, int targetUserId)
        {
            if (mob == null || string.IsNullOrWhiteSpace(skillId))
                return;

            if (string.Equals(skillId, ContactAttackPacketSkillId, StringComparison.Ordinal))
            {
                TryApplyClientContactAttack(mob, targetUserId);
                return;
            }

            if (skillId.StartsWith(OldSkillExecutePacketPrefix, StringComparison.Ordinal))
            {
                TryExecuteClientOldSkill(mob, skillId[OldSkillExecutePacketPrefix.Length..], data, targetUserId);
                return;
            }

            if (skillId.StartsWith(NewSkillExecutePacketPrefix, StringComparison.Ordinal))
            {
                TryExecuteClientNewSkill(mob, skillId[NewSkillExecutePacketPrefix.Length..], data, targetUserId);
                return;
            }

            try
            {
                TrySetClientMobAttackTarget(mob, targetUserId);

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
                try
                {
                    var haxeSkillId = skillId.AsHaxeString();
                    if (!mob.hasOldSkill(haxeSkillId))
                        return;

                    var oldSkill = mob.getOldSkill(haxeSkillId) as OldMobSkill;
                    if (oldSkill == null)
                        return;

                    try { oldSkill.prepare(data); } catch { }
                    oldSkill.execute(null);
                }
                catch
                {
                }
            }
        }

        private static void TryExecuteClientOldSkill(Mob mob, string rawSkillId, int? data, int targetUserId)
        {
            if (mob == null || string.IsNullOrWhiteSpace(rawSkillId))
                return;

            try
            {
                TrySetClientMobAttackTarget(mob, targetUserId);

                var skillId = rawSkillId.AsHaxeString();
                if (!mob.hasOldSkill(skillId))
                    return;

                var oldSkill = mob.getOldSkill(skillId) as OldMobSkill;
                if (oldSkill == null)
                    return;

                try { oldSkill.prepare(data); } catch { }
                oldSkill.execute(null);
            }
            catch
            {
            }
        }

        private static void TryExecuteClientNewSkill(Mob mob, string rawSkillId, int? data, int targetUserId)
        {
            if (mob == null || string.IsNullOrWhiteSpace(rawSkillId))
                return;

            try
            {
                TrySetClientMobAttackTarget(mob, targetUserId);

                var skillId = rawSkillId.AsHaxeString();
                var skill = mob.getSkill(skillId) as MobSkill;
                if (skill == null)
                    return;

                try { skill.prepare(data); } catch { }
                skill.execute(null);
            }
            catch
            {
            }
        }

        private static void TryApplyClientContactAttack(Mob mob, int targetUserId)
        {
            try
            {
                var target = ResolveClientAttackTargetEntity(mob, targetUserId);
                if (target == null)
                    return;

                TrySetMobAttackTargetsExact(mob, target);
                
                // Manual hit feedback for heroes
                if (target is Hero hero)
                {
                    try 
                    {
                        var atk = new AttackData();
                        atk.finalDmg = 1; 
                        hero.onDamage(atk);
                    } 
                    catch { }
                }

                mob.onTouch(target);
                
                // Ensure animation resets to idle if stuck in attack pose
                try
                {
                    var animManager = GetMobAnimManager(mob);
                    var top = GetTopAnimInstance(animManager);
                    if (top != null && (top.group?.ToString().Contains("attack") == true || top.group?.ToString().Contains("melee") == true))
                    {
                        animManager?.play("idle".AsHaxeString(), null, null);
                    }
                }
                catch { }
            }
            catch
            {
                try
                {
                    var target = ResolveClientAttackTargetEntity(mob, targetUserId);
                    if (target == null)
                        return;
                    TrySetMobAttackTargetsExact(mob, target);
                    mob.contactAttack(target);
                }
                catch
                {
                }
            }
        }

        private static void TrySetClientMobAttackTarget(Mob mob, int targetUserId)
        {
            var target = ResolveClientAttackTargetEntity(mob, targetUserId);
            if (target == null)
                return;

            TrySetMobAttackTargetsExact(mob, target);
        }

        private static Entity? ResolveClientAttackTargetEntity(Mob mob, int targetUserId)
        {
            if (targetUserId > 0)
            {
                var net = GameMenu.NetRef;
                var localId = net?.id ?? 0;
                if (localId > 0)
                {
                    if (targetUserId == localId)
                        return ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;

                    if (ModEntry.TryGetClientIndex(localId, targetUserId, out var index))
                    {
                        var client = ModEntry.clients[index];
                        if (client != null)
                            return client;
                    }
                }
            }

            try
            {
                if (mob.aTarget != null)
                    return mob.aTarget;
            }
            catch
            {
            }

            try
            {
                if (mob.nemesisTarget != null)
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
            lock (Sync)
            {
                if (!clientMobTargets.TryGetValue(localIndex, out target))
                    return;
            }

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

            if (target.Dir != 0)
                self.dir = target.Dir;

            ApplyAuthoritativeLifeState(self, target.Life, target.MaxLife);
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
            lock (Sync)
            {
                if (!clientMobTargets.TryGetValue(localIndex, out target))
                    return;
            }

            if (target.Dir != 0)
                self.dir = target.Dir;

            if (IsClientAttackUnlockActive(self))
                return;

            ApplyAnimPayload(self, target.AnimPayload);
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

                    if (mob.life <= 0)
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
                    var mob = ResolveMobFromHitLocked(hit);
                    if (mob == null)
                        continue;

                    var prevLife = mob.life;
                    var maxLife = System.Math.Max(1, mob.maxLife);
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
                    var sx = GetSyncX(mob);
                    var sy = GetSyncY(mob);
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
                if (currentLevel != null && mob._level != null && !ReferenceEquals(currentLevel, mob._level))
                    return null;
            }
            catch
            {
                return null;
            }

            return mob;
        }
    }
}
