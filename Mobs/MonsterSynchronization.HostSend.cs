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
        private static bool IsMobOnScreenForSync(Mob mob)
        {
            if (mob == null)
                return false;

            var hasVisibility = TryGetMobVisibilityState(mob, out var isOnScreen, out _, out _);
            if (hasVisibility && isOnScreen)
                return true;

            if (IsHost(GameMenu.NetRef) && TryGetMobSyncId(mob, out var mobSyncId) && mobSyncId >= 0 &&
                IsMobClientVisibleForSync(mobSyncId))
                return true;

            return false;
        }

        /// <summary>
        /// When a mob is off-screen we still must push state for HP changes and death (host → clients).
        /// </summary>
        private static bool IsMobClientVisibleForSync(int mobSyncId)
        {
            if (mobSyncId < 0)
                return false;

            lock (Sync)
            {
                return hostClientInterestUsersBySyncId.TryGetValue(mobSyncId, out var users) &&
                       users != null &&
                       users.Count > 0;
            }
        }

        /// <summary>
        /// Client → host affect payloads must still send when HP/death matters while off-screen.
        /// </summary>
        private static void SetHostClientInterestLocked(int mobSyncId, int userId, bool isInterested)
        {
            if (mobSyncId < 0 || userId <= 0)
                return;

            if (!isInterested)
            {
                if (!hostClientInterestUsersBySyncId.TryGetValue(mobSyncId, out var existing))
                    return;

                existing.Remove(userId);
                if (existing.Count <= 0)
                    hostClientInterestUsersBySyncId.Remove(mobSyncId);
                return;
            }

            if (!hostClientInterestUsersBySyncId.TryGetValue(mobSyncId, out var users) || users == null)
            {
                users = new HashSet<int>();
                hostClientInterestUsersBySyncId[mobSyncId] = users;
            }

            users.Add(userId);
        }

        private static void ClearHostClientInterestLocked()
        {
            foreach (var users in hostClientInterestUsersBySyncId.Values)
                users?.Clear();

            hostClientInterestUsersBySyncId.Clear();
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
                {
                    // Was alive before this damage (fallbackLife > 0) and dead after — keep dead so lethal
                    // hit|0 can be sent and host can apply kill; do not resurrect here.
                    if (fallbackLife > 0)
                        return;

                    mob.life = System.Math.Max(1, fallbackLife);
                }
            }
            catch
            {
            }
        }

        private static void TryApplyHostClientVisibilityInterest(Mob mob)
        {
            if (mob == null)
                return;
            if (!TryGetMobSyncId(mob, out var syncId) || syncId < 0)
                return;

            if (!IsMobClientVisibleForSync(syncId))
                return;

            PromoteMobToSyncVisibleState(mob);
        }

        private static void PromoteMobToSyncVisibleState(Mob mob)
        {
            if (mob == null)
                return;

            try
            {
                var wasOutOfGame = mob.isOutOfGame;
                mob.isOnScreen = true;
                if (mob.onScreenRecent < 1.0)
                    mob.onScreenRecent = 1.0;
                mob.isOutOfGame = false;
                mob.lastOutOfGame = false;
                if (wasOutOfGame)
                    mob.onOutOfGameChange();
            }
            catch
            {
            }
        }

        private static bool TryBuildHostMobDeltaSnapshot(
            Mob mob,
            int mobSyncId,
            bool forceFullState,
            out bool sendStateSnapshot,
            out NetNode.MobStateSnapshot stateSnapshot,
            out NetNode.MobMoveSnapshot moveSnapshot,
            HostMobSyncPriority? priorityHint = null,
            string? prebuiltAnimPayload = null)
        {
            sendStateSnapshot = true;
            stateSnapshot = default;
            moveSnapshot = default;
            if (mob == null)
                return false;

            if (!TryGetCurrentLevelIdentityToken(out var identityToken))
                return false;

            var x = GetWorldX(mob);
            var y = GetWorldY(mob);
            var dir = NormalizeDir(mob.dir);
            var life = mob.life;
            var maxLife = mob.maxLife;
            var animPayload = string.Empty;
            var mobType = string.Empty;
            var statePayload = string.Empty;
            HostMobObservedState observed = default;
            var hasObserved = false;
            HostMobSentState previous;
            var hadPrevious = false;
            var ft = GetCurrentFrame(mob);
            double dx = 0.0, dy = 0.0;
            try { dx = mob.dx + mob.bdx; dy = mob.dy + mob.bdy; } catch { }

            lock (Sync)
            {
                hasObserved = hostObservedMobStatesBySyncId.TryGetValue(mobSyncId, out observed);
                hadPrevious = hostLastSentMobStatesBySyncId.TryGetValue(mobSyncId, out previous);
            }

            if (hasObserved)
            {
                animPayload = prebuiltAnimPayload ?? observed.AnimPayload;
                mobType = observed.MobType;
                statePayload = observed.StatePayload;
            }
            else
            {
                animPayload = prebuiltAnimPayload ?? BuildAnimPayload(mob);
                mobType = BuildMobStateTypeSignature(mob);
                statePayload = BuildHostMobStatePayload(mob);
            }

            var current = new HostMobSentState(x, y, dir, life, maxLife, animPayload, mobType, statePayload);
            var resolvedPriority = priorityHint ?? GetHostMobSyncPriority(mob);
            var positionEpsilon = GetHostStatePositionEpsilon(resolvedPriority);
            var lifeChanged = !hadPrevious || previous.Life != life || previous.MaxLife != maxLife;
            var payloadChanged = !hadPrevious ||
                                 !string.Equals(previous.Type, mobType, StringComparison.Ordinal) ||
                                 !string.Equals(previous.StatePayload, statePayload, StringComparison.Ordinal);
            var animChanged = !hadPrevious ||
                              !string.Equals(previous.AnimPayload, animPayload, StringComparison.Ordinal);
            var positionChanged = !hadPrevious ||
                                  !IsApproximatelyEqual(previous.X, x, positionEpsilon) ||
                                  !IsApproximatelyEqual(previous.Y, y, positionEpsilon) ||
                                  previous.Dir != dir;

            if (!forceFullState && hadPrevious && !lifeChanged && !payloadChanged && !animChanged && !positionChanged)
            {
                return false;
            }

            lock (Sync)
            {
                hostLastSentMobStatesBySyncId[mobSyncId] = current;
            }

            if (!forceFullState && hadPrevious && !lifeChanged && !payloadChanged && (positionChanged || animChanged))
            {
                sendStateSnapshot = false;
                moveSnapshot = new NetNode.MobMoveSnapshot(
                    mobSyncId,
                    x,
                    y,
                    dir,
                    animChanged ? animPayload : string.Empty,
                    identityToken,
                    ft,
                    dx,
                    dy);
                return true;
            }

            var snapshotAnimPayload = forceFullState
                ? animPayload
                : hadPrevious && !animChanged ? string.Empty : animPayload;
            var snapshotMobType = forceFullState
                ? mobType
                : hadPrevious &&
                                  string.Equals(previous.Type, mobType, StringComparison.Ordinal)
                ? string.Empty
                : mobType;
            var snapshotStatePayload = forceFullState
                ? EncodeStatePayloadForWire(statePayload)
                : hadPrevious &&
                                       string.Equals(previous.StatePayload, statePayload, StringComparison.Ordinal)
                ? string.Empty
                : EncodeStatePayloadForWire(statePayload);

            stateSnapshot = new NetNode.MobStateSnapshot(
                mobSyncId,
                x,
                y,
                dir,
                life,
                maxLife,
                snapshotAnimPayload,
                snapshotMobType,
                snapshotStatePayload,
                identityToken,
                ft,
                dx,
                dy);
            return true;
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

        private static HostMobSyncPriority GetHostMobSyncPriority(Mob? mob)
        {
            if (mob == null)
                return HostMobSyncPriority.Dormant;
            if (BossSyncHelpers.IsBossMob(mob) || HasValidLivingPlayerCombatTarget(mob))
                return HostMobSyncPriority.Active;

            if (TryGetMobSyncId(mob, out var syncId) && syncId >= 0 && IsMobClientVisibleForSync(syncId))
                return HostMobSyncPriority.Active;

            TryGetMobVisibilityState(mob, out var isOnScreen, out var isOutOfGame, out var onScreenRecent);

            if (isOnScreen)
                return HostMobSyncPriority.Active;
            if (onScreenRecent > 0.0 || !isOutOfGame)
                return HostMobSyncPriority.MidRange;

            return HostMobSyncPriority.Dormant;
        }

        private static double GetHostStatePositionEpsilon(HostMobSyncPriority priority) =>
            priority switch
            {
                HostMobSyncPriority.Active => MobStatePositionEpsilon,
                HostMobSyncPriority.MidRange => HostMobStateMidPositionEpsilon,
                _ => HostMobStateDormantPositionEpsilon
            };

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

        private static string EncodeStatePayloadForWire(string? payload)
        {
            var safePayload = payload ?? string.Empty;
            return safePayload.Length == 0 ? ExplicitEmptyStatePayloadMarker : safePayload;
        }

        private static bool TryDecodeStatePayloadFromWire(string? wirePayload, out string payload)
        {
            var safePayload = wirePayload ?? string.Empty;
            if (safePayload.Length == 0)
            {
                payload = string.Empty;
                return false;
            }

            if (string.Equals(safePayload, ExplicitEmptyStatePayloadMarker, StringComparison.Ordinal))
            {
                payload = string.Empty;
                return true;
            }

            payload = safePayload;
            return true;
        }

        private static string ExtractAffectPresenceSignature(string? payload)
        {
            var parsed = ParseAffectStatePayload(payload);
            if (parsed.Count == 0)
                return string.Empty;

            var ids = new List<int>(parsed.Count);
            foreach (var affectId in parsed)
                ids.Add(affectId);
            ids.Sort();
            return string.Join(".", ids);
        }

        private void Hook_Mob_contactAttack(Hook_Mob.orig_contactAttack orig, Mob self, Entity pow)
        {
            var net = GameMenu.NetRef;
            if (IsHost(net) && IsInvalidPlayerTargetEntity(pow))
                return;

            orig(self, pow);

            if (!IsHost(net) || !IsPreservablePlayerCombatTargetForMob(self, pow))
                return;

            if (ShouldSendHostContactPacket(self, pow))
                TrySendHostMobAttack(self, ContactAttackPacketSkillId, false, null, pow);
        }

        private void Hook_Mob_onTouch(Hook_Mob.orig_onTouch orig, Mob self, Entity atk)
        {
            var net = GameMenu.NetRef;
            if (IsHost(net) && IsInvalidPlayerTargetEntity(atk))
                return;

            orig(self, atk);

            if (!IsHost(net) || !IsSyncMob(self) || !IsPreservablePlayerCombatTargetForMob(self, atk))
                return;

            EnsureMobTracked(self);
            if (ShouldSendHostContactPacket(self, atk))
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

    }
}
