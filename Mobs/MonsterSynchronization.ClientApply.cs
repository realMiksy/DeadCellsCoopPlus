using System;
using System.Diagnostics;
using dc;
using dc.en;
using dc.libs.heaps.slib._AnimManager;
using DeadCellsMultiplayerMod.Mobs.Bosses;
using ModCore.Utilities;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public partial class MobsSynchronization
    {
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

            if (!ShouldProcessClientVisualState(self, localIndex))
                return;

            var preserveLocalMotion = (HasLocalQueuedOrChargingSkill(self) && ShouldPreserveClientAttackMotion(self))
                || IsWithinClientNetworkAttackMotionPreserveWindow(self, localIndex);

            if (!preserveLocalMotion)
            {
                var syncY = IsClientVerticalSyncEnabled();
                try
                {
                    if (!self.hasGravity)
                        syncY = true;
                }
                catch
                {
                }

                var currentX = GetWorldX(self);
                var currentY = GetWorldY(self);
                var interpolationAlpha = GetClientInterpolationAlpha();
                var lerpedX = currentX + (target.X - currentX) * interpolationAlpha;
                var lerpedY = syncY
                    ? currentY + (target.Y - currentY) * interpolationAlpha
                    : currentY;

                try
                {
                    if (syncY)
                        self.setPosPixel(lerpedX, lerpedY);
                    else
                        SetWorldXKeepingY(self, lerpedX);
                }
                catch
                {
                    if (self.spr != null)
                    {
                        self.spr.x = lerpedX;
                        if (syncY)
                            self.spr.y = lerpedY;
                    }
                }

                try
                {
                    self.dx = 0;
                    self.bdx = 0;
                    if (syncY)
                    {
                        self.dy = 0;
                        self.bdy = 0;
                        self.fallStartY = lerpedY;
                    }
                }
                catch
                {
                }
            }

            var responsiveDir = ComputeResponsiveFacingDir(self, target);
            if (responsiveDir != 0)
                self.dir = responsiveDir;

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

        private static bool ShouldProcessClientVisualState(Mob mob, int localIndex)
        {
            if (mob == null)
                return false;
            if (BossSyncHelpers.IsBossMob(mob))
                return true;
            if (HasValidLivingPlayerCombatTarget(mob))
                return true;
            if (IsWithinClientNetworkAttackMotionPreserveWindow(mob, localIndex))
                return true;

            if (TryGetMobVisibilityState(mob, out var isOnScreen, out var isOutOfGame, out var onScreenRecent))
            {
                if (isOnScreen || !isOutOfGame || onScreenRecent > 0.0)
                    return true;
            }

            if (!TryGetNearestPlayerDistanceSq(mob, out var distanceSq) || distanceSq > MobSyncDistanceSq)
                return false;

            if (ShouldStaggerFarClientVisualInterpolation(mob, localIndex))
                return false;

            return true;
        }

        /// <summary>Spreads distance-only visual work across frames when the mob is far / off-screen.</summary>
        private static bool ShouldStaggerFarClientVisualInterpolation(Mob mob, int localIndex)
        {
            if (mob == null)
                return false;

            try
            {
                if (!TryGetMobSyncId(mob, out var syncId))
                    syncId = localIndex;

                var level = mob._level;
                if (level == null)
                    return false;

                var phase = (int)System.Math.Floor(level.ftime * 45.0) % ClientVisualInterpolationStaggerPhases;
                var bucket = ((syncId % ClientVisualInterpolationStaggerPhases) + ClientVisualInterpolationStaggerPhases) % ClientVisualInterpolationStaggerPhases;
                return bucket != phase;
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
                if (BossSyncHelpers.IsBossMob(mob))
                    return;

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
            var shouldApplyAnimThisFrame = true;
            lock (Sync)
            {
                if (!clientMobTargets.TryGetValue(localIndex, out target))
                    return;

                shouldApplyAnimThisFrame = ShouldApplyClientAnimationForFrameLocked(self, localIndex);
            }

            if (!ShouldProcessClientVisualState(self, localIndex))
                return;

            var responsiveDir = ComputeResponsiveFacingDir(self, target);
            if (responsiveDir != 0)
                self.dir = responsiveDir;

            if (HasLocalQueuedOrChargingSkill(self))
                return;

            if (!shouldApplyAnimThisFrame)
                return;

            ApplyAnimPayload(localIndex, self, target.AnimPayload);
        }

        private static bool ShouldApplyClientAnimationForFrameLocked(Mob mob, int localIndex)
        {
            if (mob == null)
                return true;

            try
            {
                var level = mob._level ?? currentLevel;
                if (level == null)
                    return true;

                var frame = level.ftime;
                if (clientLastAnimationApplyFrameByLocalIndex.TryGetValue(localIndex, out var lastFrame) &&
                    lastFrame == frame)
                {
                    return false;
                }

                clientLastAnimationApplyFrameByLocalIndex[localIndex] = frame;
                return true;
            }
            catch
            {
                return true;
            }
        }

        private static void ApplyAnimPayload(int localIndex, Mob mob, string? payload)
        {
            if (mob == null || mob.life <= 0 || mob.destroyed)
                return;

            var safePayload = payload ?? string.Empty;
            var nowTick = Stopwatch.GetTimestamp();
            lock (Sync)
            {
                if (clientLastAppliedAnimPayloadByLocalIndex.TryGetValue(localIndex, out var lastApplied) &&
                    string.Equals(lastApplied.Payload, safePayload, StringComparison.Ordinal) &&
                    ElapsedSeconds(lastApplied.Tick, nowTick) < ClientAnimPayloadRefreshSeconds)
                {
                    return;
                }

                clientLastAppliedAnimPayloadByLocalIndex[localIndex] = new TimedStringPayload(safePayload, nowTick);
            }

            if (!TryGetParsedAnimPayloadCached(safePayload, out var parsed))
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
    }
}
