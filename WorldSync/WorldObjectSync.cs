using dc;
using dc.en;
using dc.en.inter;
using dc.pr;
using DeadCellsMultiplayerMod;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using DeadCellsMultiplayerMod.Interaction;
using ModCore.Events;
using ModCore.Events.Interfaces.Game.Hero;
using Serilog;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace DeadCellsMultiplayerMod.WorldSync;

/// <summary>
/// Visibility-safe host/world-object state sync.
///
/// v6.4.4 deliberately avoids direct sprite hiding/alpha changes. The previous broad v6.4
/// scanner could read temporary culling/invisible sprite state as a real terminal state and then
/// made matching objects invisible on the other client. This version only lets the host broadcast
/// conservative terminal flags for specific object families, and the receiver only writes gameplay
/// state booleans. It never forces spr.visible=false or alpha=0.
/// </summary>
public sealed class WorldObjectSync :
    IEventReceiver,
    IOnAdvancedModuleInitializing,
    IOnHeroUpdate
{
    private const double TileSizePx = 24.0;
    private const double ScanIntervalSeconds = 1.85;
    private const double HostCorrectionIntervalSeconds = 7.50;
    private const int MaxObjectsPerScan = 48;
    private const double MatchRadiusPx = 40.0;
    private const double MatchRadiusSq = MatchRadiusPx * MatchRadiusPx;

    private readonly ILogger _log;
    private long _nextScanTick;
    private long _nextHostCorrectionTick;
    private string _lastLevelId = string.Empty;
    private readonly Dictionary<string, int> _lastSentFlags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _recentApplied = new(StringComparer.Ordinal);

    private static readonly string[] InterestingTypeTokens =
    {
        "item", "loot", "weapon", "skill", "scroll", "blueprint", "key", "rune",
        "secret", "legendary", "cursed", "chest", "reward", "treasure",
        "altar", "breakable", "wall", "shrine", "protector", "guardian",
        "challenge", "slab", "pedestal", "amulet"
    };

    private static readonly string[] BadTypeTokens =
    {
        "hero", "mob", "zombie", "grenader", "archer", "runner", "bat", "dasher",
        "shielder", "shieldbearer", "bullet", "projectile", "grenade", "ammo", "pet", "familiar",
        "fx", "particle", "decal", "cine", "camera", "ui", "hud", "minimap", "ghost"
    };

    public WorldObjectSync(ModEntry entry)
    {
        _log = entry.Logger;
        EventSystem.AddReceiver(this);
    }

    void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
    {
        entry.Logger.Information("\x1b[32m[[WorldObjectSync] Initializing v6.4.4 visibility-safe host world-object sync...]\x1b[0m ");
    }

    void IOnHeroUpdate.OnHeroUpdate(double dt)
    {
        var net = GameMenu.NetRef;
        var hero = ModEntry.me;
        var level = hero?._level;
        if (net == null || !net.IsAlive || hero == null || level == null)
            return;

        var levelId = TryGetCurrentLevelId(hero);
        if (string.IsNullOrWhiteSpace(levelId))
            return;

        if (!string.Equals(_lastLevelId, levelId, StringComparison.Ordinal))
        {
            _lastLevelId = levelId;
            _lastSentFlags.Clear();
            _recentApplied.Clear();
            _nextScanTick = 0;
            _nextHostCorrectionTick = 0;
        }

        // Host-authoritative: clients apply host state, but clients do not broad-scan and send
        // their own world object states. This prevents one client's temporary culling/hidden state
        // from making every matching object invisible for the whole run.
        if (!net.IsHost)
            ApplyPendingWorldObjectStates(net, level, levelId);

        if (!net.IsHost)
            return;

        var now = Stopwatch.GetTimestamp();
        if (now < _nextScanTick)
            return;

        _nextScanTick = now + SecondsToTicks(ScanIntervalSeconds);
        var forceCorrection = now >= _nextHostCorrectionTick;
        if (forceCorrection)
            _nextHostCorrectionTick = now + SecondsToTicks(HostCorrectionIntervalSeconds);

        ScanAndSendChangedWorldObjectStates(net, hero, level, levelId, forceCorrection);
    }

    private void ScanAndSendChangedWorldObjectStates(NetNode net, Hero hero, Level level, string levelId, bool forceCorrection)
    {
        var entities = SafeRead(() => level.entities, null);
        if (entities == null)
            return;

        var sent = 0;
        for (var i = 0; i < entities.length && sent < MaxObjectsPerScan; i++)
        {
            Entity? e = null;
            try { e = entities.getDyn(i) as Entity; } catch { }
            if (e == null || ReferenceEquals(e, hero))
                continue;

            var typeName = GetStableTypeName(e);
            if (!ShouldTrackWorldObject(typeName))
                continue;

            var flags = ReadWorldObjectFlags(e, typeName);
            if (flags == 0)
                continue;

            var (x, y) = GetEntityPixelPos(e);
            if (!IsUsefulPos(x, y))
                continue;

            var key = BuildStableKey(levelId, typeName, x, y);
            if (!forceCorrection && _lastSentFlags.TryGetValue(key, out var last) && last == flags)
                continue;

            _lastSentFlags[key] = flags;
            net.SendWorldObjectState(levelId, typeName, x, y, flags);
            sent++;
        }
    }

    private void ApplyPendingWorldObjectStates(NetNode net, Level level, string localLevelId)
    {
        if (!net.TryConsumeWorldObjectStates(out var states) || states == null || states.Count == 0)
            return;

        foreach (var state in states)
        {
            if (string.IsNullOrWhiteSpace(state.LevelId) ||
                !string.Equals(state.LevelId, localLevelId, StringComparison.Ordinal))
            {
                continue;
            }

            var safeFlags = StripUnsafeFlags(state.Flags);
            if ((safeFlags & (int)WorldObjectSyncFlags.Important) == 0)
                continue;

            if (!ShouldTrackWorldObject(state.TypeName))
                continue;

            var key = BuildStableKey(state.LevelId, state.TypeName, state.X, state.Y);
            var now = Stopwatch.GetTimestamp();
            if (_recentApplied.TryGetValue(key, out var last) && now - last < SecondsToTicks(0.85))
                continue;
            _recentApplied[key] = now;

            var local = FindMatchingWorldObject(level, state);
            if (local == null)
                continue;

            try
            {
                ApplyWorldObjectFlags(local, state.TypeName, safeFlags);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "[WorldObjectSync] Apply failed type={Type} x={X} y={Y} flags={Flags}", state.TypeName, state.X, state.Y, safeFlags);
            }
        }
    }

    private static Entity? FindMatchingWorldObject(Level level, WorldObjectState state)
    {
        var entities = SafeRead(() => level.entities, null);
        if (entities == null)
            return null;

        Entity? nearestSameType = null;
        Entity? nearestCompatible = null;
        var nearestSameSq = MatchRadiusSq;
        var nearestCompatibleSq = MatchRadiusSq * 0.36;
        var wantedType = state.TypeName ?? string.Empty;

        for (var i = 0; i < entities.length; i++)
        {
            Entity? e = null;
            try { e = entities.getDyn(i) as Entity; } catch { }
            if (e == null)
                continue;

            var typeName = GetStableTypeName(e);
            if (!ShouldTrackWorldObject(typeName))
                continue;

            var (x, y) = GetEntityPixelPos(e);
            if (!IsUsefulPos(x, y))
                continue;

            var dx = x - state.X;
            var dy = y - state.Y;
            var dSq = dx * dx + dy * dy;
            if (dSq > MatchRadiusSq)
                continue;

            if (string.Equals(typeName, wantedType, StringComparison.Ordinal))
            {
                if (dSq < nearestSameSq)
                {
                    nearestSameSq = dSq;
                    nearestSameType = e;
                }
            }
            else if (dSq < nearestCompatibleSq && AreWorldTypesCompatible(typeName, wantedType))
            {
                nearestCompatibleSq = dSq;
                nearestCompatible = e;
            }
        }

        return nearestSameType ?? nearestCompatible;
    }

    private static bool AreWorldTypesCompatible(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();

        if ((a.Contains("chest") && b.Contains("chest")) ||
            (a.Contains("scroll") && b.Contains("scroll")) ||
            (a.Contains("item") && b.Contains("item")) ||
            (a.Contains("loot") && b.Contains("loot")) ||
            (a.Contains("weapon") && b.Contains("weapon")) ||
            (a.Contains("skill") && b.Contains("skill")) ||
            (a.Contains("blueprint") && b.Contains("blueprint")) ||
            (a.Contains("key") && b.Contains("key")) ||
            (a.Contains("secret") && b.Contains("secret")) ||
            (a.Contains("breakable") && b.Contains("breakable")) ||
            (a.Contains("legendary") && b.Contains("legendary")) ||
            (a.Contains("reward") && b.Contains("reward")))
        {
            return true;
        }

        return false;
    }

    private static void ApplyWorldObjectFlags(Entity e, string typeName, int flags)
    {
        flags = StripUnsafeFlags(flags);
        if ((flags & (int)WorldObjectSyncFlags.Important) == 0)
            return;

        if ((flags & (int)WorldObjectSyncFlags.Opened) != 0 && IsOpenableWorldObjectType(typeName))
        {
            SetBoolMember(e, "opened", true);
            SetBoolMember(e, "isOpen", true);
            SetBoolMember(e, "open", true);
            SetBoolMember(e, "activated", true);
            SetBoolMember(e, "used", true);
            SetBoolMember(e, "done", true);
        }

        if ((flags & (int)WorldObjectSyncFlags.Broken) != 0 && IsBreakableWorldObjectType(typeName))
        {
            SetBoolMember(e, "broken", true);
        }

        if ((flags & (int)WorldObjectSyncFlags.Consumed) != 0 && IsConsumableWorldObjectType(typeName))
        {
            SetBoolMember(e, "picked", true);
            SetBoolMember(e, "pickedUp", true);
            SetBoolMember(e, "collected", true);
            SetBoolMember(e, "taken", true);
            SetBoolMember(e, "looted", true);
            SetBoolMember(e, "used", true);
            SetBoolMember(e, "isOutOfGame", true);
            SetBoolMember(e, "outOfGame", true);
        }
    }

    private static int ReadWorldObjectFlags(Entity e, string typeName)
    {
        var flags = 0;

        if (IsConsumableWorldObjectType(typeName) &&
            (BoolMember(e, "picked") || BoolMember(e, "pickedUp") || BoolMember(e, "collected") ||
             BoolMember(e, "taken") || BoolMember(e, "looted") || BoolMember(e, "consumed")))
        {
            flags |= (int)WorldObjectSyncFlags.Consumed;
        }

        if (IsOpenableWorldObjectType(typeName) &&
            (BoolMember(e, "opened") || BoolMember(e, "isOpen") || BoolMember(e, "open") ||
             BoolMember(e, "activated") || BoolMember(e, "used") || BoolMember(e, "done")))
        {
            flags |= (int)WorldObjectSyncFlags.Opened;
        }

        if (IsBreakableWorldObjectType(typeName) &&
            (BoolMember(e, "broken") || BoolMember(e, "destroyed")))
        {
            flags |= (int)WorldObjectSyncFlags.Broken;
        }

        // Do not derive Hidden from sprite visibility/alpha. Dead Cells hides/culls many valid
        // objects temporarily, and syncing that state made items/secrets/chests vanish remotely.
        flags = StripUnsafeFlags(flags);

        if (flags != 0)
            flags |= (int)WorldObjectSyncFlags.Important;

        return flags;
    }

    private static int StripUnsafeFlags(int flags)
    {
        // Hidden was too broad for Dead Cells' renderer/culling state. Keep the enum/protocol for
        // compatibility with older packets, but ignore it in v6.4.4 runtime logic.
        flags &= ~(int)WorldObjectSyncFlags.Hidden;
        if ((flags & ((int)WorldObjectSyncFlags.Consumed | (int)WorldObjectSyncFlags.Opened | (int)WorldObjectSyncFlags.Broken)) == 0)
            flags &= ~(int)WorldObjectSyncFlags.Important;
        return flags;
    }

    private static bool ShouldTrackWorldObject(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return false;

        var lower = typeName.ToLowerInvariant();
        foreach (var bad in BadTypeTokens)
        {
            if (lower.Contains(bad))
                return false;
        }

        foreach (var token in InterestingTypeTokens)
        {
            if (lower.Contains(token))
                return true;
        }

        return false;
    }

    private static bool IsConsumableWorldObjectType(string typeName)
    {
        var lower = (typeName ?? string.Empty).ToLowerInvariant();
        return lower.Contains("item") || lower.Contains("loot") || lower.Contains("weapon") ||
               lower.Contains("skill") || lower.Contains("scroll") || lower.Contains("blueprint") ||
               lower.Contains("key") || lower.Contains("rune") || lower.Contains("reward") ||
               lower.Contains("treasure") || lower.Contains("amulet");
    }

    private static bool IsOpenableWorldObjectType(string typeName)
    {
        var lower = (typeName ?? string.Empty).ToLowerInvariant();
        return lower.Contains("chest") || lower.Contains("cursed") || lower.Contains("reward") ||
               lower.Contains("treasure") || lower.Contains("secret") || lower.Contains("legendary") ||
               lower.Contains("altar") || lower.Contains("shrine") || lower.Contains("pedestal") ||
               lower.Contains("slab") || lower.Contains("challenge");
    }

    private static bool IsBreakableWorldObjectType(string typeName)
    {
        var lower = (typeName ?? string.Empty).ToLowerInvariant();
        return lower.Contains("breakable") || lower.Contains("wall") || lower.Contains("secret");
    }

    private static bool BoolMember(object obj, string name)
    {
        try
        {
            var t = obj.GetType();
            var prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (prop != null && prop.PropertyType == typeof(bool))
                return (bool)(prop.GetValue(obj) ?? false);
            var field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (field != null && field.FieldType == typeof(bool))
                return (bool)(field.GetValue(obj) ?? false);
        }
        catch { }
        return false;
    }

    private static void SetBoolMember(object obj, string name, bool value)
    {
        try
        {
            var t = obj.GetType();
            var prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool))
            {
                prop.SetValue(obj, value);
                return;
            }
            var field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (field != null && field.FieldType == typeof(bool))
                field.SetValue(obj, value);
        }
        catch { }
    }

    private static (double x, double y) GetEntityPixelPos(Entity e)
    {
        try
        {
            if (e.spr != null)
                return (e.spr.x, e.spr.y);
        }
        catch { }

        try { return ((e.cx + e.xr) * TileSizePx, (e.cy + e.yr) * TileSizePx); }
        catch { return (0, 0); }
    }

    private static bool IsUsefulPos(double x, double y)
    {
        return double.IsFinite(x) && double.IsFinite(y) && (System.Math.Abs(x) > 0.01 || System.Math.Abs(y) > 0.01);
    }

    private static string GetStableTypeName(object obj)
    {
        try { return obj.GetType().Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string TryGetCurrentLevelId(Hero hero)
    {
        try
        {
            var levelId = hero._level?.map?.id?.ToString();
            if (!string.IsNullOrWhiteSpace(levelId))
                return levelId.Trim();
        }
        catch { }
        return string.Empty;
    }

    private static string BuildStableKey(string levelId, string typeName, double x, double y)
    {
        var qx = (int)System.Math.Round(x / 12.0);
        var qy = (int)System.Math.Round(y / 12.0);
        return $"{levelId}|{typeName}|{qx}|{qy}";
    }

    private static T? SafeRead<T>(Func<T> fn, T? fallback)
    {
        try { return fn(); }
        catch { return fallback; }
    }

    private static long SecondsToTicks(double seconds) => (long)(Stopwatch.Frequency * seconds);
}
