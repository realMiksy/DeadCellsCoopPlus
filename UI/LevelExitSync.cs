using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using dc;
using dc.en;
using dc.en.inter;
using dc.h2d;
using dc.pr;
using dc.ui;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using ModCore.Events;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Utilities;
using Serilog;

namespace DeadCellsMultiplayerMod.MultiplayerModUI.LevelExit;

public class LevelExitSync :
    IEventReceiver,
    IOnAdvancedModuleInitializing,
    IOnHeroUpdate
{
    private sealed class DoorVisual
    {
        public Exit? Door;
        public Graphics? Circle;
        public dc.h2d.Text? Counter;
    }

    private sealed class PlayerExitState
    {
        public int UserId;
        public string DoorKey = string.Empty;
        public int DoorCx;
        public int DoorCy;
        public bool Pressed;
        public bool InsideCircle;
        public bool IsOutOfGame;
        public bool IsOnScreen;
        public long LastTick;
    }

    private const double ExitCircleRadiusPx = 84.0;
    private const double ExitCounterYOffsetPx = 100.0;
    private const double ExitStateResendSeconds = 0.20;
    private const double CounterScale = 1.10;
    private const int CounterColor = 0xFFFFFF;
    private const int MarkerColor = 0x68AD3D;
    private const int CircleColor = 0x59D5FF;
    private const double CircleAlphaIdle = 0.10;
    private const double CircleAlphaActive = 0.22;
    private const int PointerFxSuppressionKey = 188743680;

    private readonly ILogger _log;

    private readonly Dictionary<string, DoorVisual> _doorVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<int, PlayerExitState> _playerStates = new();
    private readonly HashSet<int> _activePlayerIds = new();
    private Pointer? _exitPointer;

    private Level? _lastLevel;
    private string _localDoorKey = string.Empty;
    private int _localDoorCx;
    private int _localDoorCy;
    private bool _localPressed;
    private bool _localInsideCircle;
    private bool _localDoorOutOfGame;
    private bool _localDoorOnScreen;
    private string _lastSentStateSignature = string.Empty;
    private long _lastLocalStateSendTick;
    private bool _suppressDoorActivateHook;
    private string _transitionDoorKey = string.Empty;
    private bool _timerPausedByExit;

    public LevelExitSync(ModEntry entry)
    {
        _log = entry.Logger;
        EventSystem.AddReceiver(this);
    }

    void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
    {
        entry.Logger.Information("\x1b[32m[[ModEntry.LevelExitSync] Initializing LevelExitSync hooks...]\x1b[0m ");
        Hook_Exit.postUpdate += Hook_Exit_postUpdate;
        Hook_Exit.onActivate += Hook_Exit_onActivate;
    }

    private void Hook_Exit_postUpdate(Hook_Exit.orig_postUpdate orig, Exit self)
    {
        orig(self);

        var visual = EnsureDoorVisual(self);
        UpdateDoorVisual(visual);
    }

    private void Hook_Exit_onActivate(Hook_Exit.orig_onActivate orig, Exit self, Hero by, bool inf)
    {
        if (_suppressDoorActivateHook)
        {
            orig(self, by, inf);
            return;
        }

        var localHero = ModEntry.me;
        if (self == null || by == null || localHero == null || !ReferenceEquals(by, localHero))
        {
            orig(self, by, inf);
            return;
        }

        var net = GameMenu.NetRef;
        if (net == null || !net.IsAlive || net.id <= 0)
        {
            orig(self, by, inf);
            return;
        }

        if (!IsEntityInsideExitCircle(localHero, self))
        {
            orig(self, by, inf);
            return;
        }

        _localDoorKey = BuildDoorKey(self.cx, self.cy);
        _localDoorCx = self.cx;
        _localDoorCy = self.cy;
        _localDoorOutOfGame = SafeRead(() => self.isOutOfGame, false);
        _localDoorOnScreen = SafeRead(() => self.isOnScreen, false);
        _localInsideCircle = true;
        var wasReadyHere = _localPressed &&
                           string.Equals(_localDoorKey, BuildDoorKey(self), StringComparison.Ordinal) &&
                           _localInsideCircle;
        _localPressed = true;

        UpdateLocalPlayerState(net, forceSend: true);
        if (!wasReadyHere)
            PushReachedExitMessage(net.id, self, net);

        if (AreAllPlayersReadyForDoor(_localDoorKey, net))
        {
            ApplyLocalTimerPause(false);
            TriggerExitTransition(self, localHero, () => orig(self, by, inf));
            return;
        }

        ApplyLocalTimerPause(true);
        return;
    }

    void IOnHeroUpdate.OnHeroUpdate(double dt)
    {
        var hero = ModEntry.me;
        var net = GameMenu.NetRef;
        if (hero == null || net == null || !net.IsAlive || net.id <= 0)
        {
            if (_lastLevel != null || _doorVisuals.Count > 0 || _playerStates.Count > 0 || _exitPointer != null)
                ResetLevelState(null);
            ApplyLocalTimerPause(false);
            return;
        }

        var currentLevel = hero._level;
        if (!ReferenceEquals(_lastLevel, currentLevel))
            ResetLevelState(currentLevel);

        ConsumeIncomingExitReadyStates(net);
        RefreshActivePlayers(net);
        PrunePlayerStates(net.id);
        PruneDoorVisuals(currentLevel);

        var nearestExit = FindNearestExit(hero, currentLevel, out var insideCircle);
        if (nearestExit != null)
        {
            _localDoorKey = BuildDoorKey(nearestExit);
            _localDoorCx = nearestExit.cx;
            _localDoorCy = nearestExit.cy;
            _localDoorOutOfGame = SafeRead(() => nearestExit.isOutOfGame, false);
            _localDoorOnScreen = SafeRead(() => nearestExit.isOnScreen, false);
            _localInsideCircle = insideCircle;
            EnsureDoorVisual(nearestExit);
        }
        else
        {
            _localDoorKey = string.Empty;
            _localDoorCx = 0;
            _localDoorCy = 0;
            _localDoorOutOfGame = true;
            _localDoorOnScreen = false;
            _localInsideCircle = false;
        }

        if (_localPressed && (!_localInsideCircle || string.IsNullOrEmpty(_localDoorKey)))
        {
            _localPressed = false;
            _transitionDoorKey = string.Empty;
        }

        UpdateLocalPlayerState(net, forceSend: false);
        ApplyLocalTimerPause(_localPressed && _localInsideCircle);
        RefreshDoorVisuals();

        if (_localPressed &&
            _localInsideCircle &&
            nearestExit != null &&
            !string.IsNullOrEmpty(_localDoorKey) &&
            !string.Equals(_transitionDoorKey, _localDoorKey, StringComparison.Ordinal) &&
            AreAllPlayersReadyForDoor(_localDoorKey, net))
        {
            TriggerExitTransition(nearestExit, hero, null);
        }

        UpdateExitPointer();
    }

    private void TriggerExitTransition(Exit exit, Hero hero, Action? origActivate)
    {
        if (exit == null || hero == null)
            return;

        _transitionDoorKey = BuildDoorKey(exit.cx, exit.cy);
        _suppressDoorActivateHook = true;
        try
        {
            if (origActivate != null)
                origActivate();
            else
                exit.onActivate(hero, false);
        }
        catch (Exception ex)
        {
            _log.Warning("[ExitSync] Failed to trigger level exit transition: {Message}", ex.Message);
            _transitionDoorKey = string.Empty;
        }
        finally
        {
            _suppressDoorActivateHook = false;
        }
    }

    private void ConsumeIncomingExitReadyStates(NetNode net)
    {
        if (!net.TryConsumeExitReadyStates(out var states))
            return;

        var currentLevel = ModEntry.me?._level ?? _lastLevel;
        var localId = net.id;
        for (int i = 0; i < states.Count; i++)
        {
            var state = states[i];
            if (state.UserId <= 0 || state.UserId == localId)
                continue;

            var wasReady = _playerStates.TryGetValue(state.UserId, out var prev) &&
                           prev.Pressed &&
                           prev.InsideCircle &&
                           prev.DoorCx == state.DoorCx &&
                           prev.DoorCy == state.DoorCy;
            var isReady = state.Pressed && state.InsideCircle;

            _playerStates[state.UserId] = new PlayerExitState
            {
                UserId = state.UserId,
                DoorKey = BuildDoorKey(state.DoorCx, state.DoorCy),
                DoorCx = state.DoorCx,
                DoorCy = state.DoorCy,
                Pressed = state.Pressed,
                InsideCircle = state.InsideCircle,
                IsOutOfGame = state.IsOutOfGame,
                IsOnScreen = state.IsOnScreen,
                LastTick = Stopwatch.GetTimestamp()
            };

            if (!wasReady && isReady)
            {
                var exit = FindExitByCoordinates(currentLevel, state.DoorCx, state.DoorCy);
                PushReachedExitMessage(state.UserId, exit, net);
            }
        }
    }

    private void RefreshActivePlayers(NetNode net)
    {
        _activePlayerIds.Clear();
        if (net.id > 0)
            _activePlayerIds.Add(net.id);

        if (net.TryGetRemoteUserSnapshots(out var users))
        {
            for (int i = 0; i < users.Count; i++)
            {
                var id = users[i].Id;
                if (id > 0)
                    _activePlayerIds.Add(id);
            }
        }

        for (int i = 0; i < ModEntry.clientIds.Length; i++)
        {
            var id = ModEntry.clientIds[i];
            if (id > 0)
                _activePlayerIds.Add(id);
        }
    }

    private void PrunePlayerStates(int localId)
    {
        if (_playerStates.Count == 0)
            return;

        List<int>? stale = null;
        foreach (var pair in _playerStates)
        {
            if (pair.Key == localId)
                continue;
            if (_activePlayerIds.Contains(pair.Key))
                continue;
            stale ??= new List<int>();
            stale.Add(pair.Key);
        }

        if (stale == null)
            return;

        for (int i = 0; i < stale.Count; i++)
        {
            var id = stale[i];
            _playerStates.Remove(id);
        }
    }

    private void UpdateLocalPlayerState(NetNode net, bool forceSend)
    {
        var localId = net.id;
        if (localId <= 0)
            return;

        _playerStates[localId] = new PlayerExitState
        {
            UserId = localId,
            DoorKey = _localDoorKey,
            DoorCx = _localDoorCx,
            DoorCy = _localDoorCy,
            Pressed = _localPressed,
            InsideCircle = _localInsideCircle,
            IsOutOfGame = _localDoorOutOfGame,
            IsOnScreen = _localDoorOnScreen,
            LastTick = Stopwatch.GetTimestamp()
        };

        var signature = string.Create(
            CultureInfo.InvariantCulture,
            $"{localId}|{_localDoorCx}|{_localDoorCy}|{(_localPressed ? 1 : 0)}|{(_localInsideCircle ? 1 : 0)}|{(_localDoorOutOfGame ? 1 : 0)}|{(_localDoorOnScreen ? 1 : 0)}");

        var now = Stopwatch.GetTimestamp();
        var resendTicks = (long)(Stopwatch.Frequency * ExitStateResendSeconds);
        var changed = !string.Equals(signature, _lastSentStateSignature, StringComparison.Ordinal);
        var timedOut = _lastLocalStateSendTick == 0 || now - _lastLocalStateSendTick >= resendTicks;
        if (!forceSend && !changed && !timedOut)
            return;

        net.SendExitReady(_localDoorCx, _localDoorCy, _localPressed, _localInsideCircle, _localDoorOutOfGame, _localDoorOnScreen);
        _lastSentStateSignature = signature;
        _lastLocalStateSendTick = now;
    }

    private bool AreAllPlayersReadyForDoor(string doorKey, NetNode net)
    {
        if (string.IsNullOrWhiteSpace(doorKey))
            return false;

        var expected = GetExpectedPlayerCount(net);
        if (expected <= 1)
            return true;

        var ready = 0;
        foreach (var state in _playerStates.Values)
        {
            if (state.UserId <= 0)
                continue;
            if (!state.Pressed || !state.InsideCircle)
                continue;
            if (!string.Equals(state.DoorKey, doorKey, StringComparison.Ordinal))
                continue;
            ready++;
        }

        return ready >= expected;
    }

    private int CountReadyPlayersForDoor(string doorKey)
    {
        if (string.IsNullOrWhiteSpace(doorKey))
            return 0;

        var ready = 0;
        foreach (var state in _playerStates.Values)
        {
            if (state.UserId <= 0)
                continue;
            if (!state.Pressed || !state.InsideCircle)
                continue;
            if (!string.Equals(state.DoorKey, doorKey, StringComparison.Ordinal))
                continue;
            ready++;
        }

        return ready;
    }

    private int GetExpectedPlayerCount(NetNode net)
    {
        var expected = System.Math.Max(1, _activePlayerIds.Count);
        expected = System.Math.Max(expected, _playerStates.Count);
        if (net.IsHost)
        {
            var hostExpected = 1 + NetNode.ConnectedClientCount;
            if (hostExpected > expected)
                expected = hostExpected;
        }
        else if (net.IsAlive)
        {
            // Client is connected to host even when snapshots are late.
            expected = System.Math.Max(expected, 2);
        }
        return System.Math.Max(1, expected);
    }

    private void PushReachedExitMessage(int userId, Exit? exit, NetNode net)
    {
        var playerName = ResolveUserDisplayName(userId, net);
        var destination = ResolveExitDestinationName(exit);
        MultiplayerUI.PushSystemMessage(FormatLocalized("{0} reached the exit to {1}", playerName, destination));
    }

    private static string ResolveExitDestinationName(Exit? exit)
    {
        if (exit == null)
            return Localize("next area");

        var byFunc = SafeRead(() => exit.getDestName()?.ToString() ?? string.Empty, string.Empty);
        if (!string.IsNullOrWhiteSpace(byFunc))
            return byFunc.Trim();

        var byField = SafeRead(() => exit.destName?.ToString() ?? string.Empty, string.Empty);
        if (!string.IsNullOrWhiteSpace(byField))
            return byField.Trim();

        var byLevel = SafeRead(() => exit.destLevel?.ToString() ?? string.Empty, string.Empty);
        if (!string.IsNullOrWhiteSpace(byLevel))
            return byLevel.Trim();

        return Localize("next area");
    }

    private static string ResolveUserDisplayName(int userId, NetNode net)
    {
        if (userId <= 0)
            return Localize("Guest");

        if (net.id > 0 && userId == net.id)
            return string.IsNullOrWhiteSpace(GameMenu.Username) ? Localize("Guest") : GameMenu.Username.Trim();

        if (net.TryGetRemoteUserSnapshots(out var users))
        {
            for (int i = 0; i < users.Count; i++)
            {
                var user = users[i];
                if (user.Id != userId)
                    continue;

                var name = user.Username?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }

        if (userId == 1 && !string.IsNullOrWhiteSpace(GameMenu.RemoteUsername))
            return GameMenu.RemoteUsername.Trim();

        return FormatLocalized("Player {0}", userId);
    }

    private static string Localize(string value)
    {
        try
        {
            var localized = Lang.Class.t.get(value.AsHaxeString(), null)?.ToString();
            if (!string.IsNullOrWhiteSpace(localized))
                return localized;
        }
        catch
        {
        }

        return value;
    }

    private static string FormatLocalized(string format, params object[] args)
    {
        var localizedFormat = Localize(format);
        try
        {
            return string.Format(CultureInfo.InvariantCulture, localizedFormat, args);
        }
        catch
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
    }

    private static string BuildDoorKey(Exit exit)
    {
        return BuildDoorKey(exit.cx, exit.cy);
    }

    private static string BuildDoorKey(int cx, int cy)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{cx}:{cy}");
    }

    private static bool TryParseDoorKey(string key, out int cx, out int cy)
    {
        cx = 0;
        cy = 0;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var sep = key.IndexOf(':');
        if (sep <= 0 || sep >= key.Length - 1)
            return false;

        return int.TryParse(key.AsSpan(0, sep), NumberStyles.Integer, CultureInfo.InvariantCulture, out cx) &&
               int.TryParse(key.AsSpan(sep + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out cy);
    }

    private static Exit? FindExitByDoorKey(Level? level, string key)
    {
        if (!TryParseDoorKey(key, out var cx, out var cy))
            return null;
        return FindExitByCoordinates(level, cx, cy);
    }

    private static Exit? FindExitByCoordinates(Level? level, int cx, int cy)
    {
        if (level?.entities == null)
            return null;

        var entities = level.entities;
        for (int i = 0; i < entities.length; i++)
        {
            var exit = entities.getDyn(i) as Exit;
            if (exit == null)
                continue;
            if (exit.cx == cx && exit.cy == cy)
                return exit;
        }

        return null;
    }

    private DoorVisual EnsureDoorVisual(Exit exit)
    {
        var key = BuildDoorKey(exit);
        if (_doorVisuals.TryGetValue(key, out var existing))
        {
            existing.Door = exit;
            return existing;
        }

        var visual = new DoorVisual
        {
            Door = exit
        };

        var parent = exit.spr;
        if (parent != null)
        {
            try
            {
                visual.Circle = new Graphics(parent);
                DrawDoorCircle(visual.Circle, false);
                visual.Circle.visible = true;
            }
            catch
            {
                visual.Circle = null;
            }

            try
            {
                var net = GameMenu.NetRef;
                var expected = net != null && net.IsAlive ? GetExpectedPlayerCount(net) : System.Math.Max(1, _activePlayerIds.Count);
                var initialLabel = BuildCounterLabel(0, expected);
                visual.Counter = Assets.Class.makeText(initialLabel.AsHaxeString(), dc.ui.Text.Class.COLORS.get("WO".AsHaxeString()), false, parent);
                visual.Counter.textColor = CounterColor;
                visual.Counter.scaleX = CounterScale;
                visual.Counter.scaleY = CounterScale;
                visual.Counter.alpha = 1.0;
                visual.Counter.visible = true;
            }
            catch
            {
                visual.Counter = null;
            }
        }

        _doorVisuals[key] = visual;
        return visual;
    }

    private void UpdateDoorVisual(DoorVisual visual)
    {
        if (visual.Door == null)
            return;

        var door = visual.Door;
        var key = BuildDoorKey(door);
        var isActive = !string.IsNullOrEmpty(_localDoorKey) && string.Equals(_localDoorKey, key, StringComparison.Ordinal) && _localInsideCircle;
        var ready = CountReadyPlayersForDoor(key);
        var net = GameMenu.NetRef;
        var expected = net != null ? GetExpectedPlayerCount(net) : System.Math.Max(1, _activePlayerIds.Count);
        var label = BuildCounterLabel(ready, expected);

        if (visual.Circle != null)
        {
            DrawDoorCircle(visual.Circle, isActive);
            visual.Circle.visible = true;
            visual.Circle.y = 0;
        }

        if (visual.Counter != null)
        {
            visual.Counter.set_text(label.AsHaxeString());
            visual.Counter.y = -ExitCounterYOffsetPx;
            var textWidth = SafeRead(() => visual.Counter.textWidth, label.Length * 10);
            visual.Counter.x = -(textWidth * visual.Counter.scaleX) * 0.5;
            visual.Counter.visible = true;
        }
    }

    private void RefreshDoorVisuals()
    {
        if (_doorVisuals.Count == 0)
            return;

        foreach (var visual in _doorVisuals.Values)
            UpdateDoorVisual(visual);
    }

    private static string BuildCounterLabel(int ready, int expected)
    {
        if (ready < 0)
            ready = 0;
        if (expected < 1)
            expected = 1;
        if (ready > expected)
            ready = expected;
        return string.Create(CultureInfo.InvariantCulture, $"{ready}/{expected}");
    }

    private static void DrawDoorCircle(Graphics circle, bool active)
    {
        if (circle == null)
            return;

        try
        {
            dynamic g = circle;
            g.clear();
            var alpha = active ? CircleAlphaActive : CircleAlphaIdle;
            g.beginFill(CircleColor, alpha);
            g.drawCircle(0.0, 0.0, ExitCircleRadiusPx, null);
            g.endFill();
        }
        catch
        {
        }
    }

    private Exit? FindNearestExit(Hero hero, Level? level, out bool insideCircle)
    {
        insideCircle = false;
        if (hero == null || level == null || level.entities == null)
            return null;

        var heroX = GetEntityX(hero);
        var heroY = GetEntityY(hero);

        Exit? best = null;
        var bestDistSq = double.MaxValue;
        var entities = level.entities;
        for (int i = 0; i < entities.length; i++)
        {
            var exit = entities.getDyn(i) as Exit;
            if (exit == null)
                continue;

            var dx = GetEntityX(exit) - heroX;
            var dy = GetEntityY(exit) - heroY;
            var distSq = dx * dx + dy * dy;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = exit;
            }
        }

        if (best == null)
            return null;

        insideCircle = bestDistSq <= ExitCircleRadiusPx * ExitCircleRadiusPx;
        return best;
    }

    private static bool IsEntityInsideExitCircle(Entity entity, Exit exit)
    {
        if (entity == null || exit == null)
            return false;

        var dx = GetEntityX(entity) - GetEntityX(exit);
        var dy = GetEntityY(entity) - GetEntityY(exit);
        var distSq = dx * dx + dy * dy;
        return distSq <= ExitCircleRadiusPx * ExitCircleRadiusPx;
    }

    private void UpdateExitPointer()
    {
        if (_exitPointer != null && SafeRead(() => _exitPointer.destroyed, true))
            _exitPointer = null;

        var watchedDoor = ResolveWatchedDoorKey();
        if (string.IsNullOrWhiteSpace(watchedDoor))
        {
            ClearExitPointer();
            return;
        }

        var net = GameMenu.NetRef;
        if (net == null || AreAllPlayersReadyForDoor(watchedDoor, net))
        {
            ClearExitPointer();
            return;
        }

        var level = ModEntry.me?._level ?? _lastLevel;
        var door = FindExitByDoorKey(level, watchedDoor);
        if (door == null)
        {
            ClearExitPointer();
            return;
        }

        if (_exitPointer != null && !SafeRead(() => _exitPointer.destroyed, true))
        {
            _exitPointer.e = door;
            return;
        }

        try
        {
            _exitPointer = new Pointer(door, "".AsHaxeString(), 99999.0, MarkerColor);
            SuppressPointerFx(_exitPointer);
        }
        catch
        {
            _exitPointer = null;
        }
    }

    private string ResolveWatchedDoorKey()
    {
        if (_localPressed && _localInsideCircle && !string.IsNullOrWhiteSpace(_localDoorKey))
            return _localDoorKey;

        foreach (var pair in _playerStates)
        {
            var state = pair.Value;
            if (!state.Pressed || !state.InsideCircle)
                continue;
            if (string.IsNullOrWhiteSpace(state.DoorKey))
                continue;
            return state.DoorKey;
        }

        return string.Empty;
    }

    private void ClearExitPointer()
    {
        if (_exitPointer == null)
            return;

        try
        {
            _exitPointer.destroy();
        }
        catch
        {
        }
        finally
        {
            _exitPointer = null;
        }
    }

    private static void SuppressPointerFx(Pointer? pointer)
    {
        if (pointer == null)
            return;

        try
        {
            dynamic fastCheck = pointer.cd.fastCheck;
            fastCheck.set(PointerFxSuppressionKey, (object)1);
        }
        catch
        {
        }
    }

    private void PruneDoorVisuals(Level? currentLevel)
    {
        if (_doorVisuals.Count == 0)
            return;

        List<string>? stale = null;
        foreach (var pair in _doorVisuals)
        {
            var door = pair.Value.Door;
            var remove = door == null || currentLevel == null;
            if (!remove)
            {
                remove = !ReferenceEquals(door._level, currentLevel) || SafeRead(() => door.destroyed, true);
            }

            if (!remove)
                continue;

            stale ??= new List<string>();
            stale.Add(pair.Key);
        }

        if (stale == null)
            return;

        for (int i = 0; i < stale.Count; i++)
            RemoveDoorVisual(stale[i]);
    }

    private void RemoveDoorVisual(string key)
    {
        if (!_doorVisuals.TryGetValue(key, out var visual))
            return;

        try { visual.Circle?.remove(); } catch { }
        try { visual.Counter?.remove(); } catch { }
        _doorVisuals.Remove(key);
    }

    private void ResetLevelState(Level? newLevel)
    {
        _lastLevel = newLevel;
        _localDoorKey = string.Empty;
        _localDoorCx = 0;
        _localDoorCy = 0;
        _localPressed = false;
        _localInsideCircle = false;
        _localDoorOutOfGame = true;
        _localDoorOnScreen = false;
        _lastLocalStateSendTick = 0;
        _lastSentStateSignature = string.Empty;
        _transitionDoorKey = string.Empty;

        foreach (var key in new List<string>(_doorVisuals.Keys))
            RemoveDoorVisual(key);

        _playerStates.Clear();
        ClearExitPointer();
        ApplyLocalTimerPause(false);
    }

    private void ApplyLocalTimerPause(bool paused)
    {
        if (_timerPausedByExit == paused)
            return;

        var game = ModEntry.Instance?.game;
        if (game?.data == null)
            return;

        try
        {
            game.data.stopGameTime = paused;
            _timerPausedByExit = paused;
        }
        catch
        {
        }
    }

    private static double GetEntityX(Entity e)
    {
        if (e?.spr != null)
            return e.spr.x;
        return e.cx * 24.0;
    }

    private static double GetEntityY(Entity e)
    {
        if (e?.spr != null)
            return e.spr.y;
        return e.cy * 24.0;
    }

    private static T SafeRead<T>(Func<T> getter, T fallback)
    {
        try { return getter(); } catch { return fallback; }
    }
}
