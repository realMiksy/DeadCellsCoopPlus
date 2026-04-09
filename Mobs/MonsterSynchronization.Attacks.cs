using System;
using System.Collections.Concurrent;
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
            var encodedSkill = Uri.EscapeDataString(skillId);
            var reqTarget = requiresTargetInArea ? 1 : 0;
            var dataVal = data ?? 0;
            var attackEvent = $"attack|{encodedSkill}|0|0|{reqTarget}|{dataVal}|{targetUserId}|{dir}";
            var mobType = BuildMobStateTypeSignature(mob);
            var update = new NetNode.MobEventUpdate(mobSyncId, x, y, dir, SingleEvent(attackEvent), mobType);
            MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(net), SingleUpdate(update));
            net.SendMobEvents(SingleUpdate(update));
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
                SyncMobIdRegistry.RebuildForLevel(level, IsSyncMob);
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

            lock (Sync)
            {
                s_batchMobsScratch.Clear();
                s_batchMobsScratch.AddRange(trackedMobs);
            }

            for (int i = 0; i < s_batchMobsScratch.Count; i++)
                QueueInitialMobSync(s_batchMobsScratch[i]);

            s_batchMobsScratch.Clear();
        }

        private static int AddTrackedMobLocked(Mob mob)
        {
            if (mob == null)
                return -1;

            var existingIndex = FindTrackedMobIndexLocked(mob);
            if (existingIndex >= 0)
                return existingIndex;

            var syncId = -1;
            if (TryGetMobSyncId(mob, out syncId) && TryGetTrackedIndexBySyncIdLocked(syncId, out existingIndex))
            {
                var existingMob = trackedMobs[existingIndex];
                if (!ReferenceEquals(existingMob, mob) && existingMob != null)
                    trackedMobIndices.Remove(existingMob);

                if (!ReferenceEquals(existingMob, mob))
                    trackedMobs[existingIndex] = mob;

                trackedMobIndices[mob] = existingIndex;
                trackedLocalIndexBySyncId[syncId] = existingIndex;
                return existingIndex;
            }

            trackedMobs.Add(mob);
            var addedIndex = trackedMobs.Count - 1;
            trackedMobIndices[mob] = addedIndex;
            if (syncId >= 0)
                trackedLocalIndexBySyncId[syncId] = addedIndex;
            return addedIndex;
        }

        private static void ResetMobTrackingLocked()
        {
            trackedMobs.Clear();
            trackedMobIndices.Clear();
            trackedLocalIndexBySyncId.Clear();
            clientMobTargets.Clear();
            clientCachedAttackTargetByLocalIndex.Clear();
            clientQueuedOldSkillMarkers.Clear();
            hostLastSentContactTargetUserIdByLocalIndex.Clear();
            clientLastReportedMobLife.Clear();
            clientLastSentAffectPayloadBySyncId.Clear();
            clientLastSentDrawStateBySyncId.Clear();
            clientLastAppliedHostAffectPayloadBySyncId.Clear();
            hostLastAppliedClientAffectPayloadBySyncId.Clear();
            clientLastAppliedAnimPayloadByLocalIndex.Clear();
            clientLastAnimationApplyFrameByLocalIndex.Clear();
            clientActiveNetworkAttackLocalIndices.Clear();
            clientAiLockedLocalIndices.Clear();
            parsedAnimPayloadCache.Clear();
            hostMobTypeBySyncId.Clear();
            ClearHostClientInterestLocked();
            hostLastSentMobStatesBySyncId.Clear();
            hostDetectedTargets.Clear();
            s_trackedMobValidationPending = true;
            s_syncMobTypeCache.Clear();
            ClearQueuedDirtyStateLocked();
            currentLevel = null;
        }

        private static void RemoveTrackedMobLocked(Mob mob)
        {
            s_trackedMobValidationPending = true;
            var index = FindTrackedMobIndexLocked(mob);
            if (index < 0 &&
                SyncMobIdRegistry.TryGetExistingSyncId(mob, out var syncId) &&
                TryGetTrackedIndexBySyncIdLocked(syncId, out var syncIndex))
            {
                index = syncIndex;
            }

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

            s_trackedMobValidationPending = true;
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
                {
                    trackedMobIndices[movedMob] = index;
                    if (SyncMobIdRegistry.TryGetExistingSyncId(movedMob, out var movedSyncId))
                        trackedLocalIndexBySyncId[movedSyncId] = index;
                }

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

            trackedLocalIndexBySyncId.Remove(syncId);
            clientLastSentAffectPayloadBySyncId.Remove(syncId);
            clientLastSentDrawStateBySyncId.Remove(syncId);
            clientLastAppliedHostAffectPayloadBySyncId.Remove(syncId);
            hostLastAppliedClientAffectPayloadBySyncId.Remove(syncId);
            hostMobTypeBySyncId.Remove(syncId);
            hostClientInterestUsersBySyncId.Remove(syncId);
            hostLastSentMobStatesBySyncId.Remove(syncId);
            hostObservedMobStatesBySyncId.Remove(syncId);
            hostDirtyFlagsBySyncId.Remove(syncId);
            hostDirtyQueuedSyncIds.Remove(syncId);
            clientObservedDrawStateBySyncId.Remove(syncId);
            clientDirtyFlagsBySyncId.Remove(syncId);
            clientDirtyQueuedSyncIds.Remove(syncId);
        }

        private static void ClearLocalIndexCachesLocked(int index)
        {
            clientMobTargets.Remove(index);
            clientCachedAttackTargetByLocalIndex.Remove(index);
            clientQueuedOldSkillMarkers.Remove(index);
            hostLastSentContactTargetUserIdByLocalIndex.Remove(index);
            clientLastReportedMobLife.Remove(index);
            clientLastAppliedAnimPayloadByLocalIndex.Remove(index);
            clientLastAnimationApplyFrameByLocalIndex.Remove(index);
            clientActiveNetworkAttackLocalIndices.Remove(index);
            clientAiLockedLocalIndices.Remove(index);
        }

        private static void MoveLocalIndexCachesLocked(int fromIndex, int toIndex)
        {
            MoveLocalIndexCacheEntryLocked(clientMobTargets, fromIndex, toIndex);
            MoveLocalIndexCacheEntryLocked(clientCachedAttackTargetByLocalIndex, fromIndex, toIndex);
            MoveLocalIndexCacheEntryLocked(clientQueuedOldSkillMarkers, fromIndex, toIndex);
            MoveLocalIndexCacheEntryLocked(hostLastSentContactTargetUserIdByLocalIndex, fromIndex, toIndex);
            MoveLocalIndexCacheEntryLocked(clientLastReportedMobLife, fromIndex, toIndex);
            MoveLocalIndexCacheEntryLocked(clientLastAppliedAnimPayloadByLocalIndex, fromIndex, toIndex);
            MoveLocalIndexCacheEntryLocked(clientLastAnimationApplyFrameByLocalIndex, fromIndex, toIndex);
            MoveLocalIndexSetEntryLocked(clientActiveNetworkAttackLocalIndices, fromIndex, toIndex);
            MoveLocalIndexSetEntryLocked(clientAiLockedLocalIndices, fromIndex, toIndex);
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
                s_trackedMobValidationPending = true;
            }

            return -1;
        }

        private static bool TryGetTrackedIndexBySyncIdLocked(int syncId, out int index)
        {
            index = -1;
            if (syncId < 0 || trackedMobs.Count == 0)
                return false;

            if (!trackedLocalIndexBySyncId.TryGetValue(syncId, out index))
                return false;

            if (index < 0 || index >= trackedMobs.Count)
            {
                trackedLocalIndexBySyncId.Remove(syncId);
                s_trackedMobValidationPending = true;
                index = -1;
                return false;
            }

            var mob = trackedMobs[index];
            if (mob == null)
            {
                trackedLocalIndexBySyncId.Remove(syncId);
                s_trackedMobValidationPending = true;
                index = -1;
                return false;
            }

            if (!SyncMobIdRegistry.TryGetExistingSyncId(mob, out var mappedSyncId) || mappedSyncId != syncId)
            {
                trackedLocalIndexBySyncId.Remove(syncId);
                s_trackedMobValidationPending = true;
                index = -1;
                return false;
            }

            return true;
        }

        private static void PruneInvalidTrackedMobsLocked()
        {
            if (trackedMobs.Count == 0)
                return;

            if (!s_trackedMobValidationPending)
                return;

            s_trackedMobValidationPending = false;

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

                return IsSyncMobByType(mob);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSyncMobByType(Mob mob)
        {
            return s_syncMobTypeCache.GetOrAdd(mob.GetType(), static (System.Type t) =>
            {
                var typeName = t.FullName ?? t.Name;
                return typeName.Contains("dc.en.boss.", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains(".boss.", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("dc.en.mob.", StringComparison.Ordinal)
                    || typeName.Contains(".Mob", StringComparison.Ordinal)
                    || typeName.Contains(".mob.", StringComparison.Ordinal);
            });
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

            if (mob != null)
                QueueInitialMobSync(mob);
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

            if (TryGetTrackedIndexBySyncIdLocked(syncId, out var mappedIndex))
                return mappedIndex;

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

            QuantizeWorldPositionToPixelsInt32(x, y, out var qRefX, out var qRefY);
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

                QuantizeWorldPositionToPixelsInt32(GetWorldX(mob!), GetWorldY(mob), out var qMobX, out var qMobY);
                if (!DoesQuantizedPositionMatchForRebindLocked(mob, qRefX, qRefY, qMobX, qMobY))
                    continue;

                var score = GetQuantizedRebindScoreLocked(mob, qRefY, qMobY);

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

        /// <summary>Rounds world coordinates to int32 pixels so host/client hit routing agrees despite float drift.</summary>
        private static void QuantizeWorldPositionToPixelsInt32(double x, double y, out int qx, out int qy)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                qx = 0;
                qy = 0;
                return;
            }

            const double lim = int.MaxValue - 8;
            var rx = System.Math.Clamp(System.Math.Round(x, MidpointRounding.AwayFromZero), -lim, lim);
            var ry = System.Math.Clamp(System.Math.Round(y, MidpointRounding.AwayFromZero), -lim, lim);
            qx = (int)rx;
            qy = (int)ry;
        }

        private static bool DoesQuantizedPositionMatchForRebindLocked(Mob mob, int qRefX, int qRefY, int qMobX, int qMobY)
        {
            var grounded = true;
            try
            {
                grounded = mob.hasGravity;
            }
            catch
            {
            }

            return grounded ? qMobX == qRefX : (qMobX == qRefX && qMobY == qRefY);
        }

        private static double GetQuantizedRebindScoreLocked(Mob mob, int qRefY, int qMobY)
        {
            var grounded = true;
            try
            {
                grounded = mob.hasGravity;
            }
            catch
            {
            }

            return grounded ? System.Math.Abs((double)qMobY - qRefY) : 0.0;
        }

        private static int FindBestTrackedMobIndexForHitByQuantizedPositionLocked(string? expectedType, double refX, double refY)
        {
            if (trackedMobs.Count == 0 || string.IsNullOrWhiteSpace(expectedType))
                return -1;

            QuantizeWorldPositionToPixelsInt32(refX, refY, out var qRefX, out var qRefY);

            var bestIndex = -1;
            double bestScore = double.MaxValue;

            for (var i = 0; i < trackedMobs.Count; i++)
            {
                var mob = trackedMobs[i];
                if (!IsStateRebindCandidateLocked(mob))
                    continue;

                if (!DoesMobMatchStateType(mob, expectedType))
                    continue;

                QuantizeWorldPositionToPixelsInt32(GetWorldX(mob), GetWorldY(mob), out var qMx, out var qMy);
                if (!DoesQuantizedPositionMatchForRebindLocked(mob, qRefX, qRefY, qMx, qMy))
                    continue;

                var score = GetQuantizedRebindScoreLocked(mob, qRefY, qMy);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

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

        private static bool IsClientNetworkAttackActive(int localIndex)
        {
            if (localIndex < 0)
                return false;

            lock (Sync)
            {
                return clientActiveNetworkAttackLocalIndices.Contains(localIndex);
            }
        }

        private static void MarkClientNetworkAttackActive(Mob mob)
        {
            if (mob == null || !TryGetTrackedIndex(mob, out var localIndex))
                return;

            lock (Sync)
            {
                clientActiveNetworkAttackLocalIndices.Add(localIndex);
            }

            TryUnlockClientMobAiAuthority(mob, localIndex);
        }

        private static void RefreshClientNetworkAttackState(Mob mob, int localIndex)
        {
            if (mob == null || localIndex < 0 || !IsClientNetworkAttackActive(localIndex))
                return;

            if (HasLocalQueuedOrChargingSkill(mob) || ShouldPreserveClientAttackMotion(mob))
                return;

            lock (Sync)
            {
                clientActiveNetworkAttackLocalIndices.Remove(localIndex);
            }
        }

        private static void UpdateClientMobAiAuthority(Mob mob)
        {
            if (mob == null || !TryGetTrackedIndex(mob, out var localIndex))
                return;

            RefreshClientNetworkAttackState(mob, localIndex);
            if (HasLocalQueuedOrChargingSkill(mob) || IsClientNetworkAttackActive(localIndex))
            {
                TryUnlockClientMobAiAuthority(mob, localIndex);
                return;
            }

            TryLockClientMobAiAuthority(mob, localIndex);
        }

        private static void TryLockClientMobAiAuthority(Mob mob, int localIndex)
        {
            if (mob == null || localIndex < 0)
                return;

            lock (Sync)
            {
                if (!clientAiLockedLocalIndices.Add(localIndex))
                    return;
            }

            try
            {
                mob.lockAiS(ClientAiAuthorityLockDurationSeconds);
            }
            catch
            {
            }
        }

        private static void TryUnlockClientMobAiAuthority(Mob mob, int localIndex)
        {
            if (mob == null || localIndex < 0)
                return;

            lock (Sync)
            {
                if (!clientAiLockedLocalIndices.Remove(localIndex))
                    return;
            }

            try
            {
                mob.unlockAi();
            }
            catch
            {
            }
        }

        private static void TryAssignHostAttackTarget(Mob mob)
        {
            if (mob == null)
                return;
            if (!IsMobHostileToPlayers(mob))
                return;

            RefreshHostContactAttackState(mob);
            var clearedInvalidTargets = TryClearHostMobInvalidPlayerTargets(mob);
            var hasLivingTarget = HasValidLivingPlayerCombatTarget(mob);
            if (ShouldSuppressHostRetarget(mob))
                return;
            if (!clearedInvalidTargets && hasLivingTarget)
                return;

            if (!TryResolveDetectedHostCombatTarget(mob, out var selected))
            {
                if (clearedInvalidTargets && !hasLivingTarget)
                    TryClearHostMobInvalidPlayerTargets(mob);
                return;
            }

            try
            {
                mob.setAttackTarget(selected);
            }
            catch
            {
            }

            TrySetNemesisTargetExact(mob, selected);
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

        private static bool ShouldSuppressHostRetarget(Mob mob)
        {
            if (mob == null)
                return false;

            return HasLocalQueuedOrChargingSkill(mob);
        }

        private static bool TryClearHostMobInvalidPlayerTargets(Mob mob)
        {
            if (mob == null)
                return false;

            var cleared = false;

            try
            {
                var at = mob.aTarget;
                if (IsInvalidPlayerTargetEntity(at))
                {
                    mob.setAttackTarget(null);
                    cleared = true;
                }
            }
            catch
            {
            }

            try
            {
                var nt = mob.nemesisTarget;
                if (IsInvalidPlayerTargetEntity(nt))
                {
                    mob.setNemesisTarget(null);
                    cleared = true;
                }
            }
            catch
            {
            }

            return cleared;
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

        private static void RefreshHostContactAttackState(Mob mob)
        {
            if (mob == null)
                return;
            if (!TryGetTrackedIndex(mob, out var localIndex) || localIndex < 0)
                return;

            var currentTargetUserId = ResolveHostTargetUserId(ResolveCurrentHostPlayerCombatTarget(mob), GameMenu.NetRef?.id ?? 0);
            lock (Sync)
            {
                if (currentTargetUserId <= 0)
                {
                    hostLastSentContactTargetUserIdByLocalIndex.Remove(localIndex);
                    return;
                }

                if (!hostLastSentContactTargetUserIdByLocalIndex.TryGetValue(localIndex, out var sentTargetUserId))
                    return;

                if (sentTargetUserId != currentTargetUserId)
                    hostLastSentContactTargetUserIdByLocalIndex.Remove(localIndex);
            }
        }

        private static Entity? ResolveCurrentHostPlayerCombatTarget(Mob mob)
        {
            if (mob == null)
                return null;

            try
            {
                if (IsPlayerCombatTargetEntity(mob.aTarget))
                    return mob.aTarget;
            }
            catch
            {
            }

            try
            {
                if (IsPlayerCombatTargetEntity(mob.nemesisTarget))
                    return mob.nemesisTarget;
            }
            catch
            {
            }

            return null;
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

            return IsKnownPlayerEntity(entity);
        }

        private static bool IsInvalidPlayerTargetEntity(Entity? entity)
        {
            var safeEntity = entity;
            if (!IsKnownPlayerEntity(safeEntity) || safeEntity == null)
                return false;

            if (ModEntry.IsEntityDownedForCombat(safeEntity))
                return true;

            try
            {
                return safeEntity.destroyed || safeEntity.life <= 0;
            }
            catch
            {
                return true;
            }
        }

        private static void MoveLocalIndexSetEntryLocked(HashSet<int> set, int fromIndex, int toIndex)
        {
            if (set == null)
                return;

            if (!set.Remove(fromIndex))
            {
                set.Remove(toIndex);
                return;
            }

            set.Add(toIndex);
        }

        private static bool IsKnownPlayerEntity(Entity? entity)
        {
            if (entity == null)
                return false;

            if (entity is Hero || entity is KingSkin)
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

    }
}
