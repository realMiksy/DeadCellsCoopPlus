using dc;
using dc.en;
using dc.en.inter;
using dc.hl.types;
using dc.pr;
using dc.tool.atk;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using HaxeProxy.Runtime;
using ModCore.Events;
using ModCore.Events.Interfaces.Game.Hero;
using Serilog;
using System.Diagnostics;
using System.Reflection;

namespace DeadCellsMultiplayerMod.Interaction;

public class InteractionSync :
    IEventReceiver,
    IOnAdvancedModuleInitializing,
    IOnHeroUpdate
{
    private sealed class LevelInteractionCache
    {
        public readonly List<Door> Doors = new();
        public readonly List<Elevator> Elevators = new();
        public readonly List<VineLadder> VineLadders = new();
        public readonly List<Teleport> Teleports = new();
        public readonly List<Portal> Portals = new();
        public readonly List<Interactive> GenericInteractives = new();
        public readonly List<PressurePlate> PressurePlates = new();
        public readonly List<TreasureChest> TreasureChests = new();
        public readonly List<SwitchBossRune> SwitchBossRunes = new();
        public readonly List<Elevator> TriggerElevators = new();
        public readonly List<Teleport> TriggerTeleports = new();
        public readonly List<Portal> TriggerPortals = new();

        public void Clear()
        {
            Doors.Clear();
            Elevators.Clear();
            VineLadders.Clear();
            Teleports.Clear();
            Portals.Clear();
            GenericInteractives.Clear();
            PressurePlates.Clear();
            TreasureChests.Clear();
            SwitchBossRunes.Clear();
            TriggerElevators.Clear();
            TriggerTeleports.Clear();
            TriggerPortals.Clear();
        }
    }

    private const double PosTolerance = 1.0;
    private const double PlatePosTolerance = 8.0;
    private const double ChestPosTolerance = 16.0;
    private const double DoorPosTolerance = 16.0;
    private const double TeleportPosTolerance = 48.0;
    private const double BreakableGroundPosTolerance = 24.0;
    private const double SwitchBossRunePosTolerance = 32.0;
    private const double ElevatorPosTolerance = 48.0;
    private const double PortalPosTolerance = 48.0;
    private const double GenericInteractPosTolerance = 72.0;
    private const double TileSizePx = 24.0;
    private const double DoorProximityRadiusPx = 100.0;
    private static readonly double DoorProximityRadiusSq = DoorProximityRadiusPx * DoorProximityRadiusPx;
    private const int DoorCloseDelayMs = 250;

    private readonly ILogger _log;
    private readonly HashSet<Door> _openedDoors = new();
    private readonly Dictionary<Door, bool> _doorHadAutoClose = new();
    private readonly List<Door> _scratchDoorsToRemove = new();
    private readonly List<Door> _scratchDoorsToClose = new();
    private readonly HashSet<Elevator> _scratchAppliedElevators = new();
    private readonly List<(double X, double Y)> _scratchAppliedBreakableGround = new();
    private static readonly PropertyInfo? LevelTriggersProperty = typeof(Level).GetProperty("triggers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly LevelInteractionCache CachedInteractionLevelData = new();
    private static Level? _cachedInteractionLevel;
    private bool _applyingRemoteDoorEvents;
    private bool _applyingRemoteChestEvents;
    private bool _applyingRemoteVineLadderEvents;
    private bool _applyingRemoteTeleportEvents;
    private bool _applyingRemoteBreakableGroundEvents;
    private bool _applyingRemotePortalEvents;
    private bool _applyingRemoteGenericActivateEvents;
    private bool _applyingRemoteElevatorEvents;
    /// <summary>Throttle elevator INTERELEV sends — onStep can fire every frame while riding.</summary>
    private readonly Dictionary<Elevator, long> _elevatorLastInterSendTickMs = new();
    private readonly Dictionary<string, long> _recentLocalInteractionSends = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _recentRemoteInteractionApplies = new(StringComparer.Ordinal);
    private const double OneShotInteractionSendDedupeSeconds = 0.75;
    private const double OneShotInteractionApplyDedupeSeconds = 2.0;

    public InteractionSync(ModEntry entry)
    {
        _log = entry.Logger;
        EventSystem.AddReceiver(this);
    }

    void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
    {
        entry.Logger.Information("\x1b[32m[[InteractionSync] Initializing InteractionSync...]\x1b[0m ");

        Hook_Door.init += Hook_Door_init;
        Hook_Door.open += Hook_Door_open;
        Hook_Door.close += Hook_Door_close;
        Hook_Door.onDamage += Hook_Door_onDamage;
        Hook_Door.onDie += Hook_Door_onDie;
        Hook_Elevator.onStep += Hook_Elevator_onStep;
        Hook_PressurePlate.trigger += Hook_PressurePlate_trigger;
        // Don't hook executeOn - it fires every frame when standing, causing infinite event flood
        Hook_TreasureChest.open += Hook_TreasureChest_open;
        Hook_VineLadder.activate += Hook_VineLadder_activate;
        Hook_Teleport.open += Hook_Teleport_open;
        Hook_Portal.show += Hook_Portal_show;
        Hook_Portal.close += Hook_Portal_close;
        // v5.1: Do not hook Hook_Interactive.onActivate directly.
        // Some DCCM/GameProxy builds expose Hook_Interactive without orig_onActivate,
        // which caused CS0426: type name orig_onActivate does not exist.
        // Specific interaction replay hooks and F8 stuck recovery remain enabled.
        Hook_Hero.breakBreakableGround += Hook_Hero_breakBreakableGround;
        Hook_SwitchBossRune.canBeActivated += Hook_SwitchBossRune_canBeActivated;
        Hook_SwitchBossRune.close += Hook_SwitchBossRune_close;
        Hook_SwitchBossRune.updateCells += Hook_SwitchBossRune_updateCells;
    }


    // v5.1 compile-fix note:
    // The generic Interactive.onActivate hook was intentionally removed because the generated
    // hook delegate name is not stable across the current DCCM/GameProxy builds. The v5 code
    // referenced Hook_Interactive.orig_onActivate, but your build exposes Hook_Interactive
    // without that nested type. This keeps the stable/specific interaction sync hooks active
    // while avoiding a compile break.

    private bool Hook_SwitchBossRune_canBeActivated(Hook_SwitchBossRune.orig_canBeActivated orig, SwitchBossRune self, Hero by)
    {
        var net = GameMenu.NetRef;
        if(net != null && !net.IsHost)
            return false;
        return orig(self, by);
    }

    private void Hook_SwitchBossRune_close(Hook_SwitchBossRune.orig_close orig, SwitchBossRune self)
    {
        var oldRune = 0;
        var net = GameMenu.NetRef;
        if (IsNetReadyForSend(net) && net!.IsHost)
        {
            try
            {
                var user = self?._level?.game?.user ?? dc.Main.Class.ME?.user;
                if (user != null)
                    oldRune = GameDataSync.GetBossRuneInt(user);
            }
            catch { }
        }

        orig(self);

        if (!IsNetReadyForSend(net) || !net!.IsHost)
            return;

        try
        {
            var user = self?._level?.game?.user ?? dc.Main.Class.ME?.user;
            if (user != null && self != null)
            {
                GameDataSync.SendBossRune(user, net);
                var newRune = GameDataSync.GetBossRuneInt(user);
                var add = newRune > oldRune;
                var count = System.Math.Abs(newRune - oldRune);
                var (x, y) = GetEntityPixelPos(self);
                for (var i = 0; i < count; i++)
                    net.SendInterBossRuneUpdateCells(x, y, add);
                QueueReliableInteractionReplay("bossrune", x, y, string.Empty, add);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] Failed to send boss rune after SwitchBossRune.close");
        }
    }

    private void Hook_SwitchBossRune_updateCells(Hook_SwitchBossRune.orig_updateCells orig, SwitchBossRune self, bool add)
    {
        orig(self, add);

        var net = GameMenu.NetRef;
        if (!IsNetReadyForSend(net) || !net!.IsHost)
            return;

        try
        {
            var (x, y) = GetEntityPixelPos(self);
            net.SendInterBossRuneUpdateCells(x, y, add);
            QueueReliableInteractionReplay("bossrune", x, y, string.Empty, add);
            var user = self?._level?.game?.user ?? dc.Main.Class.ME?.user;
            if (user != null)
                GameDataSync.SendBossRune(user, net);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] Failed to send boss rune updateCells");
        }
    }

    private void Hook_Door_init(Hook_Door.orig_init orig, Door self)
    {
        orig(self);
        var net = GameMenu.NetRef;
        if (net != null && net.IsAlive)
        {
            _doorHadAutoClose[self] = SafeRead(() => self.autoClose, false);
            self.autoClose = false;
        }
    }

    private void Hook_Door_open(Hook_Door.orig_open orig, Door self, int durationMs, int? finalRatio, double? _tween)
    {
        orig(self, durationMs, finalRatio, _tween);
        _openedDoors.Add(self);
        TrySendDoorEvent(self, "open");
    }

    private void Hook_Door_close(Hook_Door.orig_close orig, Door self, Ref<int> delayMs)
    {
        orig(self, delayMs);
        _openedDoors.Remove(self);
        TrySendDoorEvent(self, "close");
    }

    private void Hook_Door_onDamage(Hook_Door.orig_onDamage orig, Door self, AttackData a)
    {
        orig(self, a);
        TrySendDoorEvent(self, "damage");
    }

    private void Hook_Door_onDie(Hook_Door.orig_onDie orig, Door self)
    {
        orig(self);
        _openedDoors.Remove(self);
        TrySendDoorEvent(self, "die");
    }

    private void TrySendDoorEvent(Door self, string action)
    {
        if (string.Equals(action, "close", StringComparison.OrdinalIgnoreCase))
            return; // v5.6: never network-close doors; this fought pressure plates and caused flicker/stuck closed doors.

        if (_applyingRemoteDoorEvents)
            return;
        var net = GameMenu.NetRef;
        if (!IsNetReadyForSend(net))
            return;
        try
        {
            var (x, y) = GetEntityPixelPos(self);
            var broken = action == "die" || SafeRead(() => self.broken, false);
            if (!ShouldSendOneShotInteraction("door", x, y, action))
                return;
            net!.SendInterDoor(net.id, x, y, action, broken);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] Door send failed");
        }
    }

    private void Hook_Elevator_onStep(Hook_Elevator.orig_onStep orig, Elevator self)
    {
        orig(self);
        if (_applyingRemoteElevatorEvents)
            return;
        if (!IsNetReadyForSend(GameMenu.NetRef))
            return;
        try
        {
            var now = System.Environment.TickCount64;
            if (_elevatorLastInterSendTickMs.TryGetValue(self, out var last) && now - last < ElevatorInterSendMinIntervalMs)
                return;
            _elevatorLastInterSendTickMs[self] = now;

            var (x, y) = GetElevatorStableAnchor(self);
            if (!ShouldSendOneShotInteraction("elevator", x, y, string.Empty))
                return;
            GameMenu.NetRef!.SendInterElevator(x, y);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] Elevator send failed");
        }
    }

    private const int ElevatorInterSendMinIntervalMs = 100;
    private static void TryApplyElevatorRemoteActivation(Elevator elevator)
    {
        if (elevator == null)
            return;

        elevator.onStep();
    }

    private static (double x, double y) GetElevatorStableAnchor(Elevator e)
    {
        if (e == null)
            return (0, 0);
        try
        {
            return ((e.cx + e.xr) * TileSizePx, (e.cy + e.yr) * TileSizePx);
        }
        catch
        {
            return GetEntityPixelPos(e);
        }
    }

    private void Hook_PressurePlate_trigger(Hook_PressurePlate.orig_trigger orig, PressurePlate self, Entity by)
    {
        orig(self, by);
        TrySendPressurePlateEvent(self);
    }

    private void TrySendPressurePlateEvent(PressurePlate self)
    {
        // v5.6: pressure plates are stateful/toggle-like. Network replay/echo made them
        // repeatedly open/close doors, so leave pressure plates to local vanilla simulation.
        return;
    }

    private void Hook_TreasureChest_open(Hook_TreasureChest.orig_open orig, TreasureChest self, Hero by)
    {
        orig(self, by);
        if (!_applyingRemoteChestEvents)
            TrySendTreasureChestEvent(self);
    }

    private void TrySendTreasureChestEvent(TreasureChest self)
    {
        TrySendInteractEvent(self, (x, y) =>
        {
            GameMenu.NetRef!.SendInterTreasureChest(x, y);
        }, "TreasureChest", "chest", string.Empty);
    }

    private void Hook_VineLadder_activate(Hook_VineLadder.orig_activate orig, VineLadder self)
    {
        orig(self);
        TrySendVineLadderEvent(self);
    }

    private void TrySendVineLadderEvent(VineLadder self)
    {
        if (_applyingRemoteVineLadderEvents)
            return;
        TrySendInteractEvent(self, (x, y) =>
        {
            GameMenu.NetRef!.SendInterVineLadder(x, y);
        }, "VineLadder", "vine", string.Empty);
    }

    private void Hook_Teleport_open(Hook_Teleport.orig_open orig, Teleport self)
    {
        orig(self);
        TryRememberLocalTeleporterReviveAnchor(self);
        TrySendTeleportEvent(self);
    }

    private void Hook_Hero_breakBreakableGround(Hook_Hero.orig_breakBreakableGround orig, Hero self, int x, int y)
    {
        orig(self, x, y);
        if (_applyingRemoteBreakableGroundEvents)
            return;
        var net = GameMenu.NetRef;
        if (!IsNetReadyForSend(net) || ModEntry.me == null || !ReferenceEquals(self, ModEntry.me))
            return;
        try
        {
            if (!ShouldSendOneShotInteraction("break", x, y, string.Empty))
                return;
            net!.SendInterBreakableGround(x, y);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] BreakableGround send failed");
        }
    }

    private void TryRememberLocalTeleporterReviveAnchor(Teleport self)
    {
        if (_applyingRemoteTeleportEvents || self == null)
            return;

        var hero = ModEntry.me;
        if (hero == null)
            return;

        try
        {
            if (hero._level == null || self._level == null || !ReferenceEquals(hero._level, self._level))
                return;
        }
        catch
        {
            return;
        }

        var (x, y) = GetEntityPixelPos(self);
        if (!double.IsFinite(x) || !double.IsFinite(y) || (x == 0 && y == 0))
            return;

        try
        {
            var hx = hero.spr?.x ?? ((hero.cx + hero.xr) * TileSizePx);
            var hy = hero.spr?.y ?? ((hero.cy + hero.yr) * TileSizePx);
            var dx = hx - x;
            var dy = hy - y;
            if (dx * dx + dy * dy > 220.0 * 220.0)
                return;
        }
        catch
        {
            return;
        }

        try { ModEntry.Instance?.RememberLocalReviveTeleporterPosition(x, y); } catch { }
    }

    private void TrySendTeleportEvent(Teleport self)
    {
        if (_applyingRemoteTeleportEvents)
            return;
        TrySendInteractEvent(self, (x, y) =>
        {
            GameMenu.NetRef!.SendInterTeleport(x, y);
        }, "Teleport", "teleport", string.Empty);
    }

    private void Hook_Portal_show(Hook_Portal.orig_show orig, Portal self)
    {
        orig(self);
        TrySendPortalEvent(self, "show");
    }

    private void Hook_Portal_close(Hook_Portal.orig_close orig, Portal self)
    {
        orig(self);
        TrySendPortalEvent(self, "close");
    }

    private void TrySendPortalEvent(Portal self, string action)
    {
        if (_applyingRemotePortalEvents)
            return;
        if (!IsNetReadyForSend(GameMenu.NetRef))
            return;
        try
        {
            var (x, y) = GetEntityPixelPos(self);
            if (!ShouldSendOneShotInteraction("portal", x, y, action))
                return;
            GameMenu.NetRef!.SendInterPortal(x, y, action);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] Portal send failed action={Action}", action);
        }
    }

    private static bool ShouldSyncGenericInteractive(Interactive? interactive, Hero? by)
    {
        if (interactive == null || by == null || ModEntry.me == null || !ReferenceEquals(by, ModEntry.me))
            return false;
        if (!ShouldAllowGenericInteractiveApply(interactive))
            return false;

        return true;
    }

    private static bool ShouldAllowGenericInteractiveApply(Interactive? interactive)
    {
        if (interactive == null)
            return false;

        if (interactive is Door || interactive is Exit || interactive is Portal ||
            interactive is TreasureChest || interactive is VineLadder || interactive is Teleport || interactive is SwitchBossRune)
        {
            return false;
        }

        try
        {
            var typeName = interactive.GetType().Name ?? string.Empty;
            if (typeName.IndexOf("ZDoor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                typeName.IndexOf("BossRushDoor", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
        }
        catch
        {
        }

        if (SafeRead(() => interactive.destroyed, false))
            return false;
        if (SafeRead(() => interactive.isOutOfGame, false))
            return false;

        return true;
    }

    private static string GetStableInteractiveTypeName(Interactive? interactive)
    {
        if (interactive == null)
            return string.Empty;
        try
        {
            return interactive.GetType().Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static (double x, double y) GetEntityPixelPos(Entity e)
    {
        if (e?.spr == null)
            return (0, 0);
        try
        {
            return (e.spr.x, e.spr.y);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static T SafeRead<T>(Func<T> fn, T fallback)
    {
        try { return fn(); }
        catch { return fallback; }
    }

    private static bool IsNetReadyForSend(NetNode? net) =>
        net != null && net.IsAlive && net.id > 0;

    private bool TrySendInteractEvent(Entity entity, Action<double, double> send, string logContext, string? dedupeKind = null, string? dedupeAction = null)
    {
        if (!IsNetReadyForSend(GameMenu.NetRef))
            return false;
        try
        {
            var (x, y) = GetEntityPixelPos(entity);
            if (!string.IsNullOrWhiteSpace(dedupeKind) &&
                !ShouldSendOneShotInteraction(dedupeKind!, x, y, dedupeAction ?? string.Empty))
            {
                return false;
            }

            send(x, y);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] {LogContext} send failed", logContext);
            return false;
        }
    }

    private bool ShouldSendOneShotInteraction(string kind, double x, double y, string action)
    {
        return ShouldAllowOneShot(_recentLocalInteractionSends, kind, x, y, action, OneShotInteractionSendDedupeSeconds);
    }

    private bool ShouldApplyOneShotInteraction(string kind, double x, double y, string action)
    {
        return ShouldAllowOneShot(_recentRemoteInteractionApplies, kind, x, y, action, OneShotInteractionApplyDedupeSeconds);
    }

    private static bool ShouldAllowOneShot(Dictionary<string, long> map, string kind, double x, double y, string action, double seconds)
    {
        var now = StopwatchTicks();
        if (map.Count > 256)
            PruneOneShotMap(map, now);

        var key = MakeInteractionKey(kind, x, y, action);
        if (map.TryGetValue(key, out var last) &&
            now - last < (long)(System.Diagnostics.Stopwatch.Frequency * seconds))
        {
            return false;
        }

        map[key] = now;
        return true;
    }

    private static long StopwatchTicks() => System.Diagnostics.Stopwatch.GetTimestamp();

    private static void PruneOneShotMap(Dictionary<string, long> map, long now)
    {
        var cutoff = now - (long)(System.Diagnostics.Stopwatch.Frequency * 8.0);
        var stale = new List<string>();
        foreach (var kv in map)
        {
            if (kv.Value < cutoff)
                stale.Add(kv.Key);
        }

        for (var i = 0; i < stale.Count; i++)
            map.Remove(stale[i]);
    }

    private static string MakeInteractionKey(string kind, double x, double y, string action)
    {
        var qx = (int)System.Math.Round(x / 8.0);
        var qy = (int)System.Math.Round(y / 8.0);
        return string.Concat(kind, ":", action ?? string.Empty, ":", qx.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", qy.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    void IOnHeroUpdate.OnHeroUpdate(double dt)
    {
        var net = GameMenu.NetRef;
        if (net == null || !net.IsAlive)
            return;

        // v5.6: reliable replay disabled; it could repeatedly toggle stateful DLC doors/vines/plates.

        if (net.TryConsumeInterDoorEvents(out var doorEvents))
        {
            ApplyRemoteDoorEvents(doorEvents);
        }

        if (net.TryConsumeInterElevatorEvents(out var elevEvents))
        {
            ApplyRemoteElevatorEvents(elevEvents);
        }

        if (net.TryConsumeInterPressurePlateEvents(out var plateEvents))
        {
            ApplyRemotePressurePlateEvents(plateEvents);
        }

        if (net.TryConsumeInterTreasureChestEvents(out var chestEvents))
        {
            ApplyRemoteTreasureChestEvents(chestEvents);
        }

        if (net.TryConsumeInterVineLadderEvents(out var vineLadderEvents))
        {
            ApplyRemoteVineLadderEvents(vineLadderEvents);
        }

        if (net.TryConsumeInterTeleportEvents(out var teleportEvents))
        {
            ApplyRemoteTeleportEvents(teleportEvents);
        }

        if (net.TryConsumeInterPortalEvents(out var portalEvents))
        {
            ApplyRemotePortalEvents(portalEvents);
        }

        if (net.TryConsumeInterGenericActivateEvents(out var genericEvents))
        {
            ApplyRemoteGenericActivateEvents(genericEvents);
        }

        // v5.6: do not run custom auto-close; it can fight pressure plates/DLC doors.
        if (net.TryConsumeInterBreakableGroundEvents(out var breakableGroundEvents))
        {
            ApplyRemoteBreakableGroundEvents(breakableGroundEvents);
        }

        if (net.TryConsumeBossRuneUpdateCells(out var updateCellsEvents))
        {
            ApplyRemoteBossRuneUpdateCells(updateCellsEvents);
        }
    }

    private void QueueReliableInteractionReplay(string kind, double x, double y, string action, bool flag)
    {
        // v5.6 rollback: do not replay stateful interactions. Replaying open/trigger/activate
        // packets every few frames caused doors, pressure plates and vines to flicker/toggle.
        // Keep this method as a no-op so all call sites remain harmless and compile-safe.
    }

    private void CheckAndCloseDoorsWhenNoOneNearby()
    {
        var level = ModEntry.me?._level;
        if (level == null)
            return;

        _scratchDoorsToRemove.Clear();
        _scratchDoorsToClose.Clear();
        foreach (var door in _openedDoors)
        {
            try
            {
                if (door == null || SafeRead(() => door.destroyed, true) || SafeRead(() => door.broken, false))
                {
                    _scratchDoorsToRemove.Add(door!);
                    continue;
                }
                if (!_doorHadAutoClose.TryGetValue(door, out var hadAutoClose) || !hadAutoClose)
                    continue;
                if (!ReferenceEquals(door._level, level))
                    continue;

                var (doorX, doorY) = GetEntityPixelPos(door);
                if (IsAnyPlayerNearby(level, doorX, doorY))
                    continue;
                if (SafeRead(() => door.broken, false))
                    continue;

                _scratchDoorsToClose.Add(door);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[InteractionSync] Door auto-close check failed");
            }
        }

        if (_scratchDoorsToClose.Count > 0)
        {
            for (var i = 0; i < _scratchDoorsToClose.Count; i++)
            {
                var door = _scratchDoorsToClose[i];
                _openedDoors.Remove(door);
                try
                {
                    int delayMs = DoorCloseDelayMs;
                    door.close(Ref<int>.From(ref delayMs));
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] closeFast failed (door may be broken)");
                }
            }
        }

        if (_scratchDoorsToRemove.Count > 0)
        {
            for (var i = 0; i < _scratchDoorsToRemove.Count; i++)
                _openedDoors.Remove(_scratchDoorsToRemove[i]);
        }
    }

    private static bool IsAnyPlayerNearby(Level level, double doorX, double doorY)
    {
        var hero = ModEntry.me;
        if (hero != null && ReferenceEquals(hero._level, level))
        {
            if (!SafeRead(() => hero.destroyed, true) && SafeRead(() => hero.life, 0) > 0)
            {
                var (hx, hy) = GetEntityPixelPos(hero);
                var dx = hx - doorX;
                var dy = hy - doorY;
                if (dx * dx + dy * dy <= DoorProximityRadiusSq)
                    return true;
            }
        }

        for (var i = 0; i < ModEntry.clients.Length; i++)
        {
            var client = ModEntry.clients[i];
            if (client == null)
                continue;
            if (!ReferenceEquals(client._level, level))
                continue;
            if (SafeRead(() => client.destroyed, true) || SafeRead(() => client.life, 0) <= 0)
                continue;

            var (cx, cy) = GetEntityPixelPos(client);
            var dx = cx - doorX;
            var dy = cy - doorY;
            if (dx * dx + dy * dy <= DoorProximityRadiusSq)
                return true;
        }

        return false;
    }

    private void ApplyDoorDie(Door door)
    {
        _openedDoors.Remove(door);
        if (!SafeRead(() => door.broken, false))
        {
            door.life = 0;
            door.onDie();
        }
    }

    private void ApplyRemoteBossRuneUpdateCells(List<InterBossRuneUpdateCellsEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level?.entities == null || events == null || events.Count == 0)
            return;

        foreach (var ev in events)
        {
            var altar = FindSwitchBossRuneByPos(level, ev.X, ev.Y);
            if (altar == null)
            {
                _log.Warning("[InteractionSync] No SwitchBossRune found at x={X} y={Y}", ev.X, ev.Y);
                continue;
            }
            try
            {
                altar.updateCells(ev.Add);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[InteractionSync] updateCells(add={Add}) failed", ev.Add);
            }
        }
    }

    private static SwitchBossRune? FindSwitchBossRuneByPos(Level level, double x, double y)
    {
        return FindInteractByPos<SwitchBossRune>(level, x, y, SwitchBossRunePosTolerance);
    }

    private void ApplyRemoteDoorEvents(List<InterDoorEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level?.entities == null || events == null || events.Count == 0)
            return;

        _applyingRemoteDoorEvents = true;
        try
        {
            var localId = GameMenu.NetRef?.id ?? 0;
            foreach (var ev in events)
            {
                if (ev.UserId == localId)
                    continue;

                var door = FindDoorByPos(level, ev.X, ev.Y);
                if (door == null)
                    continue;

                if (!ShouldApplyOneShotInteraction("door", ev.X, ev.Y, ev.Action))
                    continue;

                try
                {
                    switch (ev.Action)
                    {
                        case "open":
                            door.open(300, null, null);
                            break;
                        case "close":
                            // v5.6: ignore remote close events. Local vanilla logic/plates should own close state.
                            break;
                        case "damage":
                            if (ev.Broken)
                                ApplyDoorDie(door);
                            break;
                        case "die":
                            ApplyDoorDie(door);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] Apply door event failed x={X} y={Y} action={Action}", ev.X, ev.Y, ev.Action);
                }
            }
        }
        finally
        {
            _applyingRemoteDoorEvents = false;
        }
    }

    private void ApplyRemoteElevatorEvents(List<InterElevatorEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level == null || events == null || events.Count == 0)
            return;

        _applyingRemoteElevatorEvents = true;
        try
        {
            _scratchAppliedElevators.Clear();
            foreach (var ev in events)
            {
                var elevator = FindElevatorByPos(level, ev.X, ev.Y);
                if (elevator == null)
                {
                    _log.Warning("[InteractionSync] No Elevator found at x={X} y={Y}", ev.X, ev.Y);
                    continue;
                }

                if (!_scratchAppliedElevators.Add(elevator))
                    continue;

                try
                {
                    TryApplyElevatorRemoteActivation(elevator);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] Apply elevator event failed x={X} y={Y}", ev.X, ev.Y);
                }
            }
        }
        finally
        {
            _applyingRemoteElevatorEvents = false;
        }
    }

    private void ApplyRemoteVineLadderEvents(List<InterVineLadderEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level == null || events == null || events.Count == 0)
            return;

        _applyingRemoteVineLadderEvents = true;
        try
        {
            foreach (var ev in events)
            {
                if (!ShouldApplyOneShotInteraction("vine", ev.X, ev.Y, string.Empty))
                    continue;

                var vine = FindVineLadderByPos(level, ev.X, ev.Y);
                if (vine == null)
                    continue;

                try
                {
                    vine.activate();
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] Apply vine ladder event failed x={X} y={Y}", ev.X, ev.Y);
                }
            }
        }
        finally
        {
            _applyingRemoteVineLadderEvents = false;
        }
    }

    private void ApplyRemotePortalEvents(List<InterPortalEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level == null || events == null || events.Count == 0)
            return;

        _applyingRemotePortalEvents = true;
        try
        {
            foreach (var ev in events)
            {
                if (!ShouldApplyOneShotInteraction("portal", ev.X, ev.Y, ev.Action))
                    continue;

                var portal = FindPortalByPos(level, ev.X, ev.Y);
                if (portal == null)
                    continue;

                try
                {
                    if (ev.Action == "show")
                        portal.show();
                    else if (ev.Action == "close")
                        portal.close();
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] Apply portal event failed x={X} y={Y} action={Action}", ev.X, ev.Y, ev.Action);
                }
            }
        }
        finally
        {
            _applyingRemotePortalEvents = false;
        }
    }

    private void ApplyRemoteGenericActivateEvents(List<InterGenericActivateEvent> events)
    {
        var level = ModEntry.me?._level;
        var localHero = ModEntry.me;
        if (level == null || localHero == null || events == null || events.Count == 0)
            return;

        _applyingRemoteGenericActivateEvents = true;
        try
        {
            foreach (var ev in events)
            {
                if (!ShouldApplyOneShotInteraction("generic", ev.X, ev.Y, ev.TypeName))
                    continue;

                var target = FindGenericInteractiveByPos(level, ev.X, ev.Y, ev.TypeName);
                if (target == null)
                {
                    _log.Warning("[InteractionSync] No generic interactive found type={Type} x={X} y={Y}", ev.TypeName, ev.X, ev.Y);
                    continue;
                }

                try
                {
                    target.onActivate(localHero, false);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] Apply generic interactive failed type={Type} x={X} y={Y}", ev.TypeName, ev.X, ev.Y);
                }
            }
        }
        finally
        {
            _applyingRemoteGenericActivateEvents = false;
        }
    }

    private void ApplyRemoteTeleportEvents(List<InterTeleportEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level?.entities == null || events == null || events.Count == 0)
            return;

        _applyingRemoteTeleportEvents = true;
        try
        {
            foreach (var ev in events)
            {
                if (!ShouldApplyOneShotInteraction("teleport", ev.X, ev.Y, string.Empty))
                    continue;

                var teleport = FindTeleportByPos(level, ev.X, ev.Y);
                if (teleport == null)
                {
                    continue;
                }

                try
                {
                    teleport.open();
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] Apply teleport event failed x={X} y={Y}", ev.X, ev.Y);
                }
            }
        }
        finally
        {
            _applyingRemoteTeleportEvents = false;
        }
    }

    private void ApplyRemoteBreakableGroundEvents(List<InterBreakableGroundEvent> events)
    {
        var hero = ModEntry.me;
        if (hero == null || events == null || events.Count == 0)
            return;

        _applyingRemoteBreakableGroundEvents = true;
        try
        {
            _scratchAppliedBreakableGround.Clear();
            foreach (var ev in events)
            {
                var alreadyNearby = false;
                for (var i = 0; i < _scratchAppliedBreakableGround.Count; i++)
                {
                    var (ax, ay) = _scratchAppliedBreakableGround[i];
                    if (System.Math.Abs(ax - ev.X) <= BreakableGroundPosTolerance && System.Math.Abs(ay - ev.Y) <= BreakableGroundPosTolerance)
                    {
                        alreadyNearby = true;
                        break;
                    }
                }
                if (alreadyNearby)
                    continue;
                if (!ShouldApplyOneShotInteraction("break", ev.X, ev.Y, string.Empty))
                    continue;

                var cx = (int)System.Math.Round(ev.X);
                var cy = (int)System.Math.Round(ev.Y);
                _scratchAppliedBreakableGround.Add((ev.X, ev.Y));

                try
                {
                    hero.breakBreakableGround(cx, cy);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] Apply breakable ground failed x={X} y={Y}", cx, cy);
                }
            }
        }
        finally
        {
            _applyingRemoteBreakableGroundEvents = false;
        }
    }

    private void ApplyRemotePressurePlateEvents(List<InterPressurePlateEvent> events)
    {
        // v5.6: ignore remote pressure plate events to prevent plate/door toggle loops.
        return;
    }

    private static Door? FindDoorByPos(Level level, double x, double y)
    {
        var byPos = FindInteractByPos<Door>(level, x, y, DoorPosTolerance);
        if (byPos != null)
            return byPos;
        return FindNearestDoor(level, x, y);
    }

    private static T? FindNearestByPos<T>(Level level, double x, double y, double maxDistSq) where T : Entity
    {
        var candidates = GetInteractionCandidates<T>(level);
        if (candidates == null || candidates.Count == 0)
            return null;

        T? nearest = null;
        double nearestSq = maxDistSq;
        for (var i = 0; i < candidates.Count; i++)
        {
            var e = candidates[i];
            if (e?.spr == null) continue;
            try
            {
                var dx = e.spr.x - x;
                var dy = e.spr.y - y;
                var dSq = dx * dx + dy * dy;
                if (dSq < nearestSq)
                {
                    nearestSq = dSq;
                    nearest = e;
                }
            }
            catch { }
        }
        return nearest;
    }

    private static Door? FindNearestDoor(Level level, double x, double y) =>
        FindNearestByPos<Door>(level, x, y, DoorPosTolerance * DoorPosTolerance * 4);

    private static Elevator? FindElevatorByPos(Level level, double x, double y)
    {
        var byAnchor = FindElevatorByStableAnchor(level, x, y);
        if (byAnchor != null)
            return byAnchor;

        var byPos = FindInteractByPos<Elevator>(level, x, y, ElevatorPosTolerance);
        if (byPos != null)
            return byPos;

        var byTrack = FindElevatorByTrackBounds(level, x, y);
        if (byTrack != null)
            return byTrack;

        var nearest = FindNearestByPos<Elevator>(level, x, y, ElevatorPosTolerance * ElevatorPosTolerance * 4);
        if (nearest != null)
            return nearest;
        return FindElevatorInTriggers(level, x, y);
    }

    private static Elevator? FindElevatorByStableAnchor(Level level, double anchorX, double anchorY)
    {
        var elevators = GetInteractionCandidates<Elevator>(level);
        if (elevators == null || elevators.Count == 0)
            return null;

        for (var i = 0; i < elevators.Count; i++)
        {
            var e = elevators[i];
            if (e == null)
                continue;
            try
            {
                var (ax, ay) = GetElevatorStableAnchor(e);
                if (System.Math.Abs(ax - anchorX) < ElevatorPosTolerance &&
                    System.Math.Abs(ay - anchorY) < ElevatorPosTolerance)
                    return e;
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static Elevator? FindElevatorByTrackBounds(Level level, double x, double y)
    {
        var elevators = GetInteractionCandidates<Elevator>(level);
        if (elevators == null || elevators.Count == 0)
            return null;

        Elevator? nearest = null;
        double nearestSq = double.MaxValue;
        for (var i = 0; i < elevators.Count; i++)
        {
            var elevator = elevators[i];
            if (elevator == null)
                continue;

            try
            {
                var leftPx = elevator.xLeft * TileSizePx - ElevatorPosTolerance;
                var rightPx = (elevator.xRight + 1) * TileSizePx + ElevatorPosTolerance;
                var topPx = elevator.yTop * TileSizePx - ElevatorPosTolerance;
                var bottomPx = (elevator.yBottom + 1) * TileSizePx + ElevatorPosTolerance;

                if (x < leftPx || x > rightPx || y < topPx || y > bottomPx)
                    continue;

                var anchorX = elevator.spr?.x ?? ((elevator.cx + elevator.xr) * TileSizePx);
                var anchorY = elevator.spr?.y ?? ((elevator.cy + elevator.yr) * TileSizePx);
                var dx = anchorX - x;
                var dy = anchorY - y;
                var dSq = dx * dx + dy * dy;
                if (dSq < nearestSq)
                {
                    nearestSq = dSq;
                    nearest = elevator;
                }
            }
            catch
            {
                // ignore bad elevator state
            }
        }

        return nearest;
    }

    private static object? TryGetLevelTriggers(Level level)
    {
        try
        {
            return LevelTriggersProperty?.GetValue(level);
        }
        catch
        {
            return null;
        }
    }

    private static int GetTriggerArrayLength(object? triggers)
    {
        if (triggers is ArrayObj ao)
            return ao.length;
        if (triggers is ArrayDyn ad)
            return ad.get_length();
        return 0;
    }

    private static T? GetTriggerAt<T>(object? triggers, int i) where T : class
    {
        if (triggers is ArrayObj ao)
            return ao.getDyn(i) as T;
        if (triggers is ArrayDyn ad)
            return ad.getDyn(i) as T;
        return null;
    }

    private static T? FindNearestTriggerByPos<T>(Level level, double x, double y, double maxDistSq) where T : Entity
    {
        try
        {
            var triggers = GetInteractionTriggerCandidates<T>(level);
            if (triggers == null || triggers.Count == 0)
                return null;

            T? nearest = null;
            var nearestSq = maxDistSq;
            for (var i = 0; i < triggers.Count; i++)
            {
                var t = triggers[i];
                if (t?.spr == null) continue;
                var dx = t.spr.x - x;
                var dy = t.spr.y - y;
                var dSq = dx * dx + dy * dy;
                if (dSq < nearestSq)
                {
                    nearestSq = dSq;
                    nearest = t;
                }
            }
            return nearest;
        }
        catch
        {
            return null;
        }
    }

    private static Elevator? FindElevatorInTriggers(Level level, double x, double y) =>
        FindNearestTriggerByPos<Elevator>(level, x, y, ElevatorPosTolerance * ElevatorPosTolerance * 4);

    private static VineLadder? FindVineLadderByPos(Level level, double x, double y)
    {
        return FindInteractByPos<VineLadder>(level, x, y, PlatePosTolerance);
    }

    private static Interactive? FindGenericInteractiveByPos(Level level, double x, double y, string typeName)
    {
        var candidates = GetInteractionCandidates<Interactive>(level);
        if (candidates == null || candidates.Count == 0)
            return null;

        Interactive? nearestSameType = null;
        Interactive? nearestAnyType = null;
        var nearestSameTypeSq = GenericInteractPosTolerance * GenericInteractPosTolerance;
        var nearestAnyTypeSq = GenericInteractPosTolerance * GenericInteractPosTolerance;
        var wanted = string.IsNullOrWhiteSpace(typeName) ? string.Empty : typeName.Trim();

        for (var i = 0; i < candidates.Count; i++)
        {
            var e = candidates[i];
            if (e?.spr == null || !ShouldAllowGenericInteractiveApply(e))
                continue;

            try
            {
                var dx = e.spr.x - x;
                var dy = e.spr.y - y;
                var dSq = dx * dx + dy * dy;
                if (dSq < nearestAnyTypeSq)
                {
                    nearestAnyTypeSq = dSq;
                    nearestAnyType = e;
                }

                if (!string.IsNullOrWhiteSpace(wanted) &&
                    !string.Equals(GetStableInteractiveTypeName(e), wanted, StringComparison.Ordinal))
                {
                    continue;
                }

                if (dSq < nearestSameTypeSq)
                {
                    nearestSameTypeSq = dSq;
                    nearestSameType = e;
                }
            }
            catch
            {
            }
        }

        return nearestSameType ?? nearestAnyType;
    }

    private Teleport? FindTeleportByPos(Level level, double x, double y)
    {
        var byPos = FindInteractByPos<Teleport>(level, x, y, TeleportPosTolerance);
        if (byPos != null)
            return byPos;
        var nearest = FindNearestByPos<Teleport>(level, x, y, 200.0 * 200.0);
        if (nearest != null)
            return nearest;
        return FindTeleportInTriggers(level, x, y);
    }

    private static Portal? FindPortalByPos(Level level, double x, double y)
    {
        var byPos = FindInteractByPos<Portal>(level, x, y, PortalPosTolerance);
        if (byPos != null)
            return byPos;
        var nearest = FindNearestByPos<Portal>(level, x, y, PortalPosTolerance * PortalPosTolerance * 4);
        if (nearest != null)
            return nearest;
        return FindPortalInTriggers(level, x, y);
    }

    private static Portal? FindPortalInTriggers(Level level, double x, double y) =>
        FindNearestTriggerByPos<Portal>(level, x, y, PortalPosTolerance * PortalPosTolerance * 4);

    private static Teleport? FindTeleportInTriggers(Level level, double x, double y) =>
        FindNearestTriggerByPos<Teleport>(level, x, y, TeleportPosTolerance * TeleportPosTolerance * 4);

    private static PressurePlate? FindPressurePlateByPos(Level level, double x, double y)
    {
        return FindInteractByPos<PressurePlate>(level, x, y, PlatePosTolerance);
    }

    private void ApplyRemoteTreasureChestEvents(List<InterTreasureChestEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level?.entities == null || events == null || events.Count == 0)
            return;

        var localHero = ModEntry.me;
        if (localHero == null)
            return;

        _applyingRemoteChestEvents = true;
        try
        {
            foreach (var ev in events)
            {
                if (!ShouldApplyOneShotInteraction("chest", ev.X, ev.Y, string.Empty))
                    continue;

                var chest = FindTreasureChestByPos(level, ev.X, ev.Y);
                if (chest == null)
                    continue;

                try
                {
                    chest.open(localHero);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] Apply treasure chest event failed x={X} y={Y}", ev.X, ev.Y);
                }
            }
        }
        finally
        {
            _applyingRemoteChestEvents = false;
        }
    }

    private static TreasureChest? FindTreasureChestByPos(Level level, double x, double y)
    {
        var byPos = FindInteractByPos<TreasureChest>(level, x, y, ChestPosTolerance);
        if (byPos != null)
            return byPos;
        return FindNearestTreasureChest(level, x, y);
    }

    private static TreasureChest? FindNearestTreasureChest(Level level, double x, double y) =>
        FindNearestByPos<TreasureChest>(level, x, y, ChestPosTolerance * ChestPosTolerance * 4);

    private static T? FindInteractByPos<T>(Level level, double x, double y, double tolerance = PosTolerance) where T : Entity
    {
        var candidates = GetInteractionCandidates<T>(level);
        if (candidates == null || candidates.Count == 0)
        {
            RebuildInteractionCache(level);
            candidates = GetInteractionCandidates<T>(level);
        }

        if (candidates == null || candidates.Count == 0)
            return null;

        for (var i = 0; i < candidates.Count; i++)
        {
            var e = candidates[i];
            if (e == null)
                continue;
            try
            {
                if (e.spr != null &&
                    System.Math.Abs(e.spr.x - x) < tolerance &&
                    System.Math.Abs(e.spr.y - y) < tolerance)
                {
                    return e;
                }
            }
            catch
            {
                // ignore
            }
        }

        // Level interaction entities can appear after our first cache pass (DLC doors, vines,
        // teleports). Refresh once before giving up so one-shot visual sync is less likely to miss.
        RebuildInteractionCache(level);
        candidates = GetInteractionCandidates<T>(level);
        if (candidates != null && candidates.Count > 0)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                var e = candidates[i];
                if (e == null)
                    continue;
                try
                {
                    if (e.spr != null &&
                        System.Math.Abs(e.spr.x - x) < tolerance &&
                        System.Math.Abs(e.spr.y - y) < tolerance)
                    {
                        return e;
                    }
                }
                catch
                {
                }
            }
        }

        return null;
    }

    private static LevelInteractionCache GetInteractionCache(Level level)
    {
        if (!ReferenceEquals(_cachedInteractionLevel, level))
            RebuildInteractionCache(level);

        return CachedInteractionLevelData;
    }

    private static void RebuildInteractionCache(Level? level)
    {
        CachedInteractionLevelData.Clear();
        _cachedInteractionLevel = level;

        if (level == null)
            return;

        var entities = level.entities;
        if (entities != null)
        {
            for (var i = 0; i < entities.length; i++)
            {
                switch (entities.getDyn(i))
                {
                    case Door door:
                        CachedInteractionLevelData.Doors.Add(door);
                        break;
                    case Elevator elevator:
                        CachedInteractionLevelData.Elevators.Add(elevator);
                        break;
                    case VineLadder vineLadder:
                        CachedInteractionLevelData.VineLadders.Add(vineLadder);
                        break;
                    case Teleport teleport:
                        CachedInteractionLevelData.Teleports.Add(teleport);
                        break;
                    case Portal portal:
                        CachedInteractionLevelData.Portals.Add(portal);
                        break;
                    case PressurePlate pressurePlate:
                        CachedInteractionLevelData.PressurePlates.Add(pressurePlate);
                        break;
                    case TreasureChest treasureChest:
                        CachedInteractionLevelData.TreasureChests.Add(treasureChest);
                        break;
                    case SwitchBossRune switchBossRune:
                        CachedInteractionLevelData.SwitchBossRunes.Add(switchBossRune);
                        break;
                    case Interactive interactive when ShouldAllowGenericInteractiveApply(interactive):
                        CachedInteractionLevelData.GenericInteractives.Add(interactive);
                        break;
                }
            }
        }

        var triggers = TryGetLevelTriggers(level);
        var triggerCount = GetTriggerArrayLength(triggers);
        for (var i = 0; i < triggerCount; i++)
        {
            switch (GetTriggerAt<Entity>(triggers, i))
            {
                case Elevator elevator:
                    CachedInteractionLevelData.TriggerElevators.Add(elevator);
                    break;
                case Teleport teleport:
                    CachedInteractionLevelData.TriggerTeleports.Add(teleport);
                    break;
                case Portal portal:
                    CachedInteractionLevelData.TriggerPortals.Add(portal);
                    break;
            }
        }
    }

    private static IReadOnlyList<T>? GetInteractionCandidates<T>(Level level) where T : Entity
    {
        var cache = GetInteractionCache(level);
        if (typeof(T) == typeof(Door))
            return (IReadOnlyList<T>)(object)cache.Doors;
        if (typeof(T) == typeof(Elevator))
            return (IReadOnlyList<T>)(object)cache.Elevators;
        if (typeof(T) == typeof(VineLadder))
            return (IReadOnlyList<T>)(object)cache.VineLadders;
        if (typeof(T) == typeof(Teleport))
            return (IReadOnlyList<T>)(object)cache.Teleports;
        if (typeof(T) == typeof(Portal))
            return (IReadOnlyList<T>)(object)cache.Portals;
        if (typeof(T) == typeof(Interactive))
            return (IReadOnlyList<T>)(object)cache.GenericInteractives;
        if (typeof(T) == typeof(PressurePlate))
            return (IReadOnlyList<T>)(object)cache.PressurePlates;
        if (typeof(T) == typeof(TreasureChest))
            return (IReadOnlyList<T>)(object)cache.TreasureChests;
        if (typeof(T) == typeof(SwitchBossRune))
            return (IReadOnlyList<T>)(object)cache.SwitchBossRunes;
        return null;
    }

    private static IReadOnlyList<T>? GetInteractionTriggerCandidates<T>(Level level) where T : Entity
    {
        var cache = GetInteractionCache(level);
        if (typeof(T) == typeof(Elevator))
            return (IReadOnlyList<T>)(object)cache.TriggerElevators;
        if (typeof(T) == typeof(Teleport))
            return (IReadOnlyList<T>)(object)cache.TriggerTeleports;
        if (typeof(T) == typeof(Portal))
            return (IReadOnlyList<T>)(object)cache.TriggerPortals;
        return null;
    }
}
