using System;
using System.Collections.Generic;
using dc;
using dc.en;
using dc.en.inter;
using dc.hl.types;
using dc.pr;
using Hashlink.Virtuals;
using dc.tool.atk;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using ModCore.Events;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Utilities;
using Serilog;
using HaxeProxy.Runtime;

namespace DeadCellsMultiplayerMod.Interaction;

public class InteractionSync :
    IEventReceiver,
    IOnAdvancedModuleInitializing,
    IOnHeroUpdate
{
    private const double PosTolerance = 1.0;
    private const double PlatePosTolerance = 8.0;
    private const double ChestPosTolerance = 16.0;
    private const double DoorProximityRadiusPx = 100.0;

    private readonly ILogger _log;
    private readonly HashSet<Door> _openedDoors = new();
    private bool _applyingRemoteDoorEvents;
    private bool _applyingRemoteChestEvents;

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
    }

    private void Hook_Door_init(Hook_Door.orig_init orig, Door self)
    {
        orig(self);
        var net = GameMenu.NetRef;
        if (net != null && net.IsAlive)
            self.autoClose = false;
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
        if (_applyingRemoteDoorEvents)
            return;
        var net = GameMenu.NetRef;
        if (net == null || !net.IsAlive || net.id <= 0)
            return;
        // Both host and clients send door events when they open/close/damage/die

        try
        {
            var (x, y) = GetEntityPixelPos(self);
            var broken = action == "die" || SafeRead(() => self.broken, false);
            net.SendInterDoor(net.id, x, y, action, broken);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] Door send failed");
        }
    }

    private void Hook_Elevator_onStep(Hook_Elevator.orig_onStep orig, Elevator self)
    {
        orig(self);

        var net = GameMenu.NetRef;
        if (net == null || !net.IsAlive || net.id <= 0)
            return;

        try
        {
            var (x, y) = GetEntityPixelPos(self);
            net.SendInterElevator(x, y);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] Elevator send failed");
        }
    }

    private void Hook_PressurePlate_trigger(Hook_PressurePlate.orig_trigger orig, PressurePlate self, Entity by)
    {
        orig(self, by);
        TrySendPressurePlateEvent(self);
    }

    private void TrySendPressurePlateEvent(PressurePlate self)
    {
        var net = GameMenu.NetRef;
        if (net == null || !net.IsAlive || net.id <= 0)
            return;

        try
        {
            var (x, y) = GetEntityPixelPos(self);
            net.SendInterPressurePlate(x, y);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] PressurePlate send failed");
        }
    }

    private void Hook_TreasureChest_open(Hook_TreasureChest.orig_open orig, TreasureChest self, Hero by)
    {
        orig(self, by);
        if (!_applyingRemoteChestEvents)
            TrySendTreasureChestEvent(self);
    }

    private void TrySendTreasureChestEvent(TreasureChest self)
    {
        var net = GameMenu.NetRef;
        if (net == null || !net.IsAlive || net.id <= 0)
            return;

        try
        {
            var (x, y) = GetEntityPixelPos(self);
            net.SendInterTreasureChest(x, y);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] TreasureChest send failed");
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

    void IOnHeroUpdate.OnHeroUpdate(double dt)
    {
        var net = GameMenu.NetRef;
        if (net == null || !net.IsAlive)
            return;

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

        if (net.IsHost)
            CheckAndCloseDoorsWhenNoOneNearby();
    }

    private void CheckAndCloseDoorsWhenNoOneNearby()
    {
        var level = ModEntry.me?._level;
        if (level == null)
            return;

        var toRemove = default(List<Door>?);
        foreach (var door in _openedDoors)
        {
            try
            {
                if (door == null || SafeRead(() => door.destroyed, true) || SafeRead(() => door.broken, false))
                {
                    toRemove ??= new List<Door>();
                    toRemove.Add(door!);
                    continue;
                }
                if (!ReferenceEquals(door._level, level))
                    continue;

                var (doorX, doorY) = GetEntityPixelPos(door);
                if (IsAnyPlayerNearby(level, doorX, doorY))
                    continue;

                if (!_openedDoors.Contains(door))
                    continue;

                _openedDoors.Remove(door);
                int delayMs = 2000;
                door.close(Ref<int>.From(ref delayMs));
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[InteractionSync] Door auto-close check failed");
            }
        }

        if (toRemove != null)
        {
            foreach (var d in toRemove)
                _openedDoors.Remove(d);
        }
    }

    private static bool IsAnyPlayerNearby(Level level, double doorX, double doorY)
    {
        var radiusSq = DoorProximityRadiusPx * DoorProximityRadiusPx;

        var hero = ModEntry.me;
        if (hero != null && ReferenceEquals(hero._level, level))
        {
            if (!SafeRead(() => hero.destroyed, true) && SafeRead(() => hero.life, 0) > 0)
            {
                var (hx, hy) = GetEntityPixelPos(hero);
                var dx = hx - doorX;
                var dy = hy - doorY;
                if (dx * dx + dy * dy <= radiusSq)
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
            if (dx * dx + dy * dy <= radiusSq)
                return true;
        }

        return false;
    }

    private void ApplyRemoteDoorEvents(List<InterDoorEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level?.entities == null || events == null)
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

                try
                {
                    switch (ev.Action)
                {
                    case "open":
                        door.open(300, null, null);
                        break;
                    case "close":
                        if (!_openedDoors.Contains(door))
                            break;
                        _openedDoors.Remove(door);
                        int delayMs = 2000;
                        door.close(Ref<int>.From(ref delayMs));
                        break;
                    case "damage":
                        if (ev.Broken)
                        {
                            _openedDoors.Remove(door);
                            if (!SafeRead(() => door.broken, false))
                            {
                                door.life = 0;
                                door.onDie();
                            }
                        }
                        break;
                    case "die":
                        _openedDoors.Remove(door);
                        if (!SafeRead(() => door.broken, false))
                        {
                            door.life = 0;
                            door.onDie();
                        }
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
        if (level?.entities == null || events == null)
            return;

        foreach (var ev in events)
        {
            var elevator = FindElevatorByPos(level, ev.X, ev.Y);
            if (elevator == null)
                continue;

            try
            {
                elevator.onStep();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[InteractionSync] Apply elevator event failed x={X} y={Y}", ev.X, ev.Y);
            }
        }
    }

    private void ApplyRemotePressurePlateEvents(List<InterPressurePlateEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level?.entities == null || events == null)
            return;

        var localHero = ModEntry.me as Entity;
        if (localHero == null)
            return;

        foreach (var ev in events)
        {
            var plate = FindPressurePlateByPos(level, ev.X, ev.Y);
            if (plate == null)
                continue;

            try
            {
                plate.trigger(localHero);
                bool noLoop = false;
                var noLoopRef = Ref<bool>.From(ref noLoop);
                plate.executeOn(localHero, null, noLoopRef);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[InteractionSync] Apply pressure plate event failed x={X} y={Y}", ev.X, ev.Y);
            }
        }
    }

    private static Door? FindDoorByPos(Level level, double x, double y)
    {
        return FindInteractByPos<Door>(level, x, y);
    }

    private static Elevator? FindElevatorByPos(Level level, double x, double y)
    {
        return FindInteractByPos<Elevator>(level, x, y);
    }

    private static PressurePlate? FindPressurePlateByPos(Level level, double x, double y)
    {
        return FindInteractByPos<PressurePlate>(level, x, y, PlatePosTolerance);
    }

    private void ApplyRemoteTreasureChestEvents(List<InterTreasureChestEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level?.entities == null || events == null)
            return;

        var localHero = ModEntry.me;
        if (localHero == null)
            return;

        _applyingRemoteChestEvents = true;
        try
        {
            foreach (var ev in events)
            {
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
        // Fallback: find nearest TreasureChest (positions can differ between host/client)
        return FindNearestTreasureChest(level, x, y);
    }

    private static TreasureChest? FindNearestTreasureChest(Level level, double x, double y)
    {
        if (level?.entities == null) return null;
        TreasureChest? nearest = null;
        double nearestSq = ChestPosTolerance * ChestPosTolerance * 4; // ~32px radius
        for (var i = 0; i < level.entities.length; i++)
        {
            var e = level.entities.getDyn(i) as TreasureChest;
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

    private static T? FindInteractByPos<T>(Level level, double x, double y, double tolerance = PosTolerance) where T : Entity
    {
        if (level?.entities == null)
            return null;

        var entities = level.entities;
        for (var i = 0; i < entities.length; i++)
        {
            var e = entities.getDyn(i) as T;
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

        return null;
    }
}
