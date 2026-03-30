namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public partial class MobsSynchronization
    {
        private const double ClientMobDrawSendRateHz = 30.0;
        private const double ClientStateSendRateHz = 30.0;
        private const double HostStateSendRateHz = 30.0;
        private const double ClientMobDrawMinRateHz = 10.0;
        private const double ClientStateMinRateHz = 12.0;
        private const double HostStateMinRateHz = 18.0;
        private const int AdaptiveRateStartMobCount = 32;
        private const int AdaptiveRateEndMobCount = 160;
        private const double HostPayloadRefreshBaseSeconds = 0.18;
        private const double HostPayloadRefreshMaxSeconds = 0.45;
        private const double ClientAffectSampleBaseSeconds = 0.10;
        private const double ClientAffectSampleMaxSeconds = 0.28;
        private const double ClientAffectResendBaseSeconds = ClientAffectSyncSeconds;
        private const double ClientAffectResendMaxSeconds = ClientAffectSyncSeconds;
        private const double ClientAnimPayloadRefreshSeconds = 0.30;
        private const int ParsedAnimPayloadCacheLimit = 1024;
        private const double ClientDrawKeepAliveSeconds = 0.9;
        private const double ClientInterpolationAlpha = 0.62;
        private const double ClientAiLockSeconds = 0.3;
        private const double ClientAiLockRefreshBaseSeconds = 0.09;
        private const double ClientAiLockRefreshMaxSeconds = 0.16;
        private const double ClientNetworkAttackMotionPreserveSeconds = 0.05;
        private const double ClientBossNetworkAttackMotionPreserveSeconds = 0.85;
        private const double ClientBossNetworkAttackAiPreserveSeconds = 1.2;
        private const double HostContactAttackSendCooldownSeconds = 0.3;
        private const double HostRetargetRefreshBaseSeconds = 0.05;
        private const double HostRetargetRefreshMaxSeconds = 0.16;
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
        private const double AffectFramesPerSecond = 60.0;
        private const int ClientAffectSyncDefaultFrames = 21;
        private const int AffectTimeIncreaseThresholdFrames = 12;
    }
}
