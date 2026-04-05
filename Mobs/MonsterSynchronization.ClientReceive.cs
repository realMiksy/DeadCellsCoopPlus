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
            s_mobHitMergeScratch.Clear();
            s_mobHitMergeScratch.AddRange(s_deferredMobHitsScratch);
            s_deferredMobHitsScratch.Clear();

            if (net.TryConsumeMobHits(out var incoming) && incoming != null && incoming.Count > 0)
                s_mobHitMergeScratch.AddRange(incoming);

            if (s_mobHitMergeScratch.Count == 0)
                return;

            MobSyncTrace.LogRecvHits(net.IsHost ? "hitsOnHost" : "hitsOnClient", s_mobHitMergeScratch);

            var reResolve = MobSyncChunkedHitsEnabled;
            if (MobSyncChunkedHitsEnabled && s_mobHitMergeScratch.Count > MobSyncChunkedHitsPerFrameMax)
            {
                var n = s_mobHitMergeScratch.Count;
                var take = MobSyncChunkedHitsPerFrameMax;
                for (int i = take; i < n; i++)
                    s_deferredMobHitsScratch.Add(s_mobHitMergeScratch[i]);
                ApplyIncomingMobHits(s_mobHitMergeScratch, 0, take, reResolve);
            }
            else
            {
                ApplyIncomingMobHits(s_mobHitMergeScratch, 0, s_mobHitMergeScratch.Count, reResolve);
            }
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

        private static void ApplyIncomingMobHits(IReadOnlyList<NetNode.MobHit> hits, bool reResolveMobBySyncIdOnApply)
        {
            if (hits == null || hits.Count == 0)
                return;
            ApplyIncomingMobHits(hits, 0, hits.Count, reResolveMobBySyncIdOnApply);
        }

        private static void ApplyIncomingMobHits(IReadOnlyList<NetNode.MobHit> hits, int start, int count, bool reResolveMobBySyncIdOnApply)
        {
            if (hits == null || count <= 0)
                return;

            var end = start + count;
            if (start < 0 || end > hits.Count)
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
                for (int i = start; i < end; i++)
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
                Mob? mob;
                if (reResolveMobBySyncIdOnApply && update.SyncId >= 0)
                {
                    lock (Sync)
                    {
                        mob = ResolveMobBySyncIdLocked(update.SyncId);
                    }
                }
                else
                {
                    mob = update.Mob;
                }

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
                    var evUpdate = new NetNode.MobEventUpdate(update.SyncId, sx, sy, dir, SingleEvent(hitEv));
                    MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(net), SingleUpdate(evUpdate));
                    net.SendMobEvents(SingleUpdate(evUpdate));
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
                    if (TryFinalizeHostMobDeath(mob))
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
                        if (TryFinalizeHostMobDeath(mob))
                            return;
                    }
                }

                if (TryFinalizeHostMobDeath(mob))
                    return;

                // Last-resort force; some bosses need explicit life zero before onDie branching.
                mob.life = 0;
                TryFinalizeHostMobDeath(mob);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Host boss finishing hit replay failed");
            }
        }

        private static bool TryFinalizeHostMobDeath(Mob mob)
        {
            if (mob == null)
                return true;

            try
            {
                if (mob.destroyed)
                    return true;
            }
            catch
            {
            }

            var life = GetMobLifeOrFallback(mob, 1);
            if (life > 0)
                return false;

            try
            {
                mob.life = 0;
                mob.onDie();
            }
            catch
            {
            }

            try
            {
                return mob.destroyed || GetMobLifeOrFallback(mob, 1) <= 0;
            }
            catch
            {
                return true;
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
