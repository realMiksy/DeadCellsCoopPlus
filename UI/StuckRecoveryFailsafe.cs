using dc;
using dc.en;
using dc.pr;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using ModCore.Events;
using ModCore.Events.Interfaces.Game.Hero;
using Serilog;

namespace DeadCellsMultiplayerMod.UI;

/// <summary>
/// Manual recovery for multiplayer softlocks: press F8 to teleport your local hero to the nearest
/// remote player ghost. This is intentionally local-only; the normal position sync then tells the
/// other side where you moved.
/// </summary>
public sealed class StuckRecoveryFailsafe :
    IEventReceiver,
    IOnAdvancedModuleInitializing,
    IOnHeroUpdate
{
    private const int EmergencyTeleportKeyCode = 119; // F8 on hxd.Key/DOM-style key codes.
    private const double EmergencyTeleportCooldownSeconds = 2.0;
    private const double EmergencyTeleportYOffsetPx = 16.0;
    private const double MinDistanceForInfoMessagePx = 280.0;

    private readonly ILogger _log;
    private long _nextAllowedTeleportTick;
    private long _nextInfoMessageTick;

    public StuckRecoveryFailsafe(ModEntry entry)
    {
        _log = entry.Logger;
        EventSystem.AddReceiver(this);
    }

    void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
    {
        entry.Logger.Information("\x1b[32m[[StuckRecoveryFailsafe] Initializing stuck recovery failsafe...]\x1b[0m ");
    }

    void IOnHeroUpdate.OnHeroUpdate(double dt)
    {
        var net = GameMenu.NetRef;
        var hero = ModEntry.me;
        if (net == null || !net.IsAlive || net.id <= 0 || hero == null || hero._level == null)
            return;

        if (!IsEmergencyTeleportPressed())
            return;

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < _nextAllowedTeleportTick)
        {
            MaybePushInfo("Emergency teleport is cooling down.");
            return;
        }

        var hasVisibleTarget = TryFindNearestRemoteHero(hero, out var target, out var distanceSq);
        var hasRawTarget = false;
        double rawX = 0;
        double rawY = 0;
        int rawDir = SafeRead(() => hero.dir, 0);
        if (!hasVisibleTarget)
            hasRawTarget = TryFindLastNetworkRemotePosition(hero, out rawX, out rawY, out distanceSq);

        if (!hasVisibleTarget && !hasRawTarget)
        {
            MaybePushInfo("No remote player position found to teleport to.");
            return;
        }

        try
        {
            var x = hasVisibleTarget ? GetEntityX(target!) : rawX;
            var y = (hasVisibleTarget ? GetEntityY(target!) : rawY) - EmergencyTeleportYOffsetPx;
            var dir = hasVisibleTarget ? SafeRead(() => target!.dir, rawDir) : rawDir;

            RecoverLocalHeroControl(hero);
            hero.setPosPixel(x, y);
            hero.dir = dir;
            RecoverLocalHeroControl(hero);
            try { net.TickSend(x, y, dir); } catch { }

            _nextAllowedTeleportTick = now + (long)(System.Diagnostics.Stopwatch.Frequency * EmergencyTeleportCooldownSeconds);
            MultiplayerUI.PushSystemMessage("Emergency teleport used: moved to the other player.");
            _log.Information("[StuckRecovery] Emergency teleport applied distance={Distance}", System.Math.Sqrt(distanceSq));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[StuckRecovery] Emergency teleport failed");
            MaybePushInfo("Emergency teleport failed. Try again after moving/camera settling.");
        }
    }

    private bool IsEmergencyTeleportPressed()
    {
        try
        {
            return dc.hxd.Key.Class.isPressed(EmergencyTeleportKeyCode);
        }
        catch
        {
            return false;
        }
    }

    private bool TryFindNearestRemoteHero(Hero localHero, out GhostKing? target, out double distanceSq)
    {
        target = null;
        distanceSq = double.MaxValue;
        var level = localHero._level;
        var lx = GetEntityX(localHero);
        var ly = GetEntityY(localHero);

        // Prefer a visible ghost on the same Level object, then any visible ghost, then finally
        // the last raw network coordinate even if the visible ghost was hidden/disposed by a
        // room/sublevel mismatch. This makes F8 work at any distance and in more DLC/transition
        // softlocks.
        TryFindNearestRemoteHeroPass(level, lx, ly, requireSameLevel: true, ref target, ref distanceSq);
        if (target == null)
            TryFindNearestRemoteHeroPass(level, lx, ly, requireSameLevel: false, ref target, ref distanceSq);

        if (target != null)
        {
            if (distanceSq < MinDistanceForInfoMessagePx * MinDistanceForInfoMessagePx)
                _log.Debug("[StuckRecovery] Emergency teleport used while players were already close distance={Distance}", System.Math.Sqrt(distanceSq));
            return true;
        }

        return false;
    }

    private static void TryFindNearestRemoteHeroPass(Level? level, double lx, double ly, bool requireSameLevel, ref GhostKing? target, ref double distanceSq)
    {
        for (var i = 0; i < ModEntry.clients.Length; i++)
        {
            var remote = ModEntry.clients[i];
            if (remote == null)
                continue;
            if (requireSameLevel && !ReferenceEquals(remote._level, level))
                continue;
            if (SafeRead(() => remote.destroyed, true))
                continue;
            if (SafeRead(() => remote.isOutOfGame, false))
                continue;

            var rx = GetEntityX(remote);
            var ry = GetEntityY(remote);
            if (System.Math.Abs(rx) < 0.001 && System.Math.Abs(ry) < 0.001)
                continue;

            var dx = rx - lx;
            var dy = ry - ly;
            var dSq = dx * dx + dy * dy;
            if (dSq < distanceSq)
            {
                target = remote;
                distanceSq = dSq;
            }
        }
    }

    private static bool TryFindLastNetworkRemotePosition(Hero localHero, out double x, out double y, out double distanceSq)
    {
        x = 0;
        y = 0;
        distanceSq = double.MaxValue;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var maxAgeTicks = System.Diagnostics.Stopwatch.Frequency * 30L;
        var lx = GetEntityX(localHero);
        var ly = GetEntityY(localHero);

        for (var i = 0; i < ModEntry.EmergencyLastRemoteX.Length && i < ModEntry.EmergencyLastRemoteY.Length && i < ModEntry.EmergencyLastRemoteTicks.Length; i++)
        {
            var tick = ModEntry.EmergencyLastRemoteTicks[i];
            if (tick <= 0 || now - tick > maxAgeTicks)
                continue;

            var rx = ModEntry.EmergencyLastRemoteX[i];
            var ry = ModEntry.EmergencyLastRemoteY[i];
            if (System.Math.Abs(rx) < 0.001 && System.Math.Abs(ry) < 0.001)
                continue;

            var dx = rx - lx;
            var dy = ry - ly;
            var dSq = dx * dx + dy * dy;
            if (dSq < distanceSq)
            {
                distanceSq = dSq;
                x = rx;
                y = ry;
            }
        }

        return distanceSq < double.MaxValue;
    }

    private static void RecoverLocalHeroControl(Hero hero)
    {
        try { hero.cancelVelocities(); } catch { }
        try { hero.cancelSkillControlLock(); } catch { }
        try { hero.unlockControls(); } catch { }
        try { hero._targetable = true; } catch { }
        try
        {
            if (hero.life <= 0)
                hero.life = 1;
        }
        catch { }

        try
        {
            var data = hero._level?.game?.data;
            if (data != null)
                data.stopGameTime = false;
        }
        catch { }
    }

    private void MaybePushInfo(string message)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < _nextInfoMessageTick)
            return;

        _nextInfoMessageTick = now + System.Diagnostics.Stopwatch.Frequency;
        MultiplayerUI.PushSystemMessage(message);
    }

    private static double GetEntityX(Entity e)
    {
        try
        {
            if (e.spr != null)
                return e.spr.x;
        }
        catch { }

        try { return (e.cx + e.xr) * 24.0; } catch { return 0.0; }
    }

    private static double GetEntityY(Entity e)
    {
        try
        {
            if (e.spr != null)
                return e.spr.y;
        }
        catch { }

        try { return (e.cy + e.yr) * 24.0; } catch { return 0.0; }
    }

    private static T SafeRead<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }
}
