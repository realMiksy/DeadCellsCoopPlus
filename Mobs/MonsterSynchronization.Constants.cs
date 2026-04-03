namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public partial class MobsSynchronization
    {
        private const double HostPayloadRefreshSeconds = 0.72;
        private const double ClientAffectSampleSeconds = 0.40;
        private const double ClientAffectResendSeconds = 0.675;
        private const double ClientAnimPayloadRefreshSeconds = 0.33;
        private const int ParsedAnimPayloadCacheLimit = 1024;
        private const double ClientDrawKeepAliveSeconds = 1.0;
        private const double HostClientDrawVisibilityHoldSeconds = 8.0;
        private const double ClientInterpolationAlpha = 0.70;
        private const double MobSyncDistance = 256.0;
        private const double MobSyncDistanceSq = MobSyncDistance * MobSyncDistance;
        private const double MobDrawNearDistance = 256.0;
        private const double MobDrawNearDistanceSq = MobDrawNearDistance * MobDrawNearDistance;
        private const double ClientAiLockSeconds = 0.3;
        private const double ClientAiLockRefreshSeconds = 0.125;
        private const double ClientNetworkAttackMotionPreserveSeconds = 0.05;
        private const double ClientBossNetworkAttackMotionPreserveSeconds = 0.85;
        private const double ClientBossNetworkAttackAiPreserveSeconds = 1.2;
        private const double HostContactAttackSendCooldownSeconds = 0.3;
        private const double HostRetargetRefreshSeconds = 0.105;
        private const double HostActiveStateEvalSeconds = 0.066;
        private const double HostFarStateEvalSeconds = 0.29;
        private const double HostDormantStateEvalSeconds = 0.775;
        private const double ClientFarAffectEvalSeconds = 0.48;
        private const double ClientDormantAffectEvalSeconds = 1.025;
        private const double ClientFarDrawEvalSeconds = 0.375;
        private const double ClientDormantDrawEvalSeconds = 1.125;
        private const double ClientMobHitReportMinIntervalSeconds = 0.05;
        private const double ClientAnimSpeedEpsilon = 0.05;
        private static readonly bool ClientSyncVerticalPosition = false;
        private const double ClientTurnSnapDeltaPx = 2.0;
        private const double MobStatePositionEpsilon = 0.35;
        private const double HostMobStateMidPositionEpsilon = 1.20;
        private const double HostMobStateFarPositionEpsilon = 2.75;
        private const double HostMobStateDormantPositionEpsilon = 6.00;
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
        private const double HostUnchangedStateResendGateSeconds = 3.0;
        private const double HostDormantDuplicateLifeMinSeconds = 1.1;
        private const double AffectFramesPerSecond = 60.0;
        private const int ClientAffectSyncDefaultFrames = 21;
        private const int AffectTimeIncreaseThresholdFrames = 12;

        /// <summary>Stagger phases for far, distance-only client visuals (1 = off, 2 = light, 3 = aggressive CPU save).</summary>
        private const int ClientVisualInterpolationStaggerPhases = 2;

        /// <summary>When tracked mob count is at or above this, non-active host mobs get slightly longer state eval intervals.</summary>
        private const int HostCrowdMobCountThreshold = 32;

        private const double HostCrowdEvalStretchMultiplier = 1.12;
        private const double HostCrowdActiveEvalStretchMultiplier = 1.30;

        private static double GetClientInterpolationAlpha()
        {
            var configured = MultiplayerSettingsStorage.MobsInterpolationQuality;
            if (double.IsNaN(configured) || double.IsInfinity(configured))
                return ClientInterpolationAlpha;

            return System.Math.Clamp(configured, 0.20, 1.00);
        }

        private static bool IsClientVerticalSyncEnabled()
        {
            return ClientSyncVerticalPosition || MultiplayerSettingsStorage.SyncVerticalPosition;
        }
    }
}
