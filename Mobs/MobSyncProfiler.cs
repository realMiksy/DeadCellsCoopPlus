using System;
using System.Diagnostics;
using Serilog;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization;

/// <summary>Opt-in cumulative timings for mob sync (set DCCM_MOB_SYNC_PROFILE=1).</summary>
internal static class MobSyncProfiler
{
    private const int LogEveryFrames = 180;

    public static bool Enabled { get; }

    private static long _fixedApplyTicks;
    private static long _postAnimTicks;
    private static long _frameBatchTicks;
    private static long _wireEncodeTicks;
    private static long _wireParseTicks;
    private static int _frameCounter;

    static MobSyncProfiler()
    {
        Enabled = string.Equals(Environment.GetEnvironmentVariable("DCCM_MOB_SYNC_PROFILE"), "1", StringComparison.Ordinal);
    }

    public static void AddFixedApply(long elapsedTicks)
    {
        if (Enabled)
            _fixedApplyTicks += elapsedTicks;
    }

    public static void AddPostAnim(long elapsedTicks)
    {
        if (Enabled)
            _postAnimTicks += elapsedTicks;
    }

    public static void AddFrameBatch(long elapsedTicks)
    {
        if (Enabled)
            _frameBatchTicks += elapsedTicks;
    }

    public static void AddWireEncode(long elapsedTicks)
    {
        if (Enabled)
            _wireEncodeTicks += elapsedTicks;
    }

    public static void AddWireParse(long elapsedTicks)
    {
        if (Enabled)
            _wireParseTicks += elapsedTicks;
    }

    public static void TickFrame(ILogger? log)
    {
        if (!Enabled || log == null)
            return;

        if (++_frameCounter < LogEveryFrames)
            return;

        _frameCounter = 0;
        var f = Stopwatch.Frequency;
        log.Information(
            "[MobSyncProfile] ~{Window}s: fixedApply={FixedMs:F1}ms postAnim={PostMs:F1}ms frameBatch={BatchMs:F1}ms wireEnc={EncMs:F1}ms wireParse={ParseMs:F1}ms",
            LogEveryFrames / 60.0,
            TicksToMs(_fixedApplyTicks, f),
            TicksToMs(_postAnimTicks, f),
            TicksToMs(_frameBatchTicks, f),
            TicksToMs(_wireEncodeTicks, f),
            TicksToMs(_wireParseTicks, f));

        _fixedApplyTicks = 0;
        _postAnimTicks = 0;
        _frameBatchTicks = 0;
        _wireEncodeTicks = 0;
        _wireParseTicks = 0;
    }

    private static double TicksToMs(long ticks, long frequency) =>
        ticks <= 0 ? 0.0 : ticks * 1000.0 / frequency;
}
