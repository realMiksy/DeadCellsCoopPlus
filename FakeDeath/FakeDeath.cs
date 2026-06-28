using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using dc;
using dc.en;
using dc.tool.atk;
using dc.tool.mainSkills;
using dc.ui;
using dc.cine;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using ModCore.Modules;
using HaxeProxy.Runtime;
using ModCore.Utilities;

namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry
    {
        private bool _allDownedGameOverShown;
        private bool _allDownedRestartQueued;
        private long _allDownedRestartAtTicks;
        private const double AllDownedGameOverDelaySeconds = 0.2;
        private bool _hasLocalDownedAnchor;
        private double _localDownedAnchorX;
        private double _localDownedAnchorY;
        private const double DownedCorpseMaxDriftPx = 96.0;
        private const double DownedCorpseMaxDriftSq = DownedCorpseMaxDriftPx * DownedCorpseMaxDriftPx;
        private bool _hasLocalReviveSafePosition;
        private double _localReviveSafeX;
        private double _localReviveSafeY;
        private string _localReviveSafeLevelId = string.Empty;
        private long _localReviveSafeTicks;
        private bool _hasLocalReviveTeleporterPosition;
        private double _localReviveTeleporterX;
        private double _localReviveTeleporterY;
        private string _localReviveTeleporterLevelId = string.Empty;
        private long _localReviveTeleporterTicks;
        private readonly List<ReviveSafeAnchor> _localReviveSafeHistory = new();
        private const double ReviveSafePositionMaxAgeSeconds = 20.0;
        private const double ReviveTeleporterMaxAgeSeconds = 900.0;
        private const double ReviveSafeHistoryMinAgeSeconds = 1.25;
        private const double ReviveSafeHistoryMaxAgeSeconds = 12.0;
        private const int ReviveSafeHistoryMaxEntries = 36;
        private const double DownedVoidRescueDropPx = 160.0;
        private const double DownedUnsafeRescueCheckIntervalSeconds = 0.20;
        private const double DownedSafeRescueLockSeconds = 6.0;
        private const double DownedPermanentAnchorRefreshSeconds = 2.0;
        private const double DownedParkedHeroYOffsetPx = 8.0;
        private bool _localDownedHeroGravityWasCaptured;
        private bool _localDownedHeroHadGravity;
        private bool _localDownedHeroVisibilityWasCaptured;
        private bool _localDownedHeroWasVisible;
        private long _nextDownedSafeRescueCheckTicks;
        private long _downedSafeRescueLockUntilTicks;
        private double _downedSafeRescueLockX;
        private double _downedSafeRescueLockY;
        private readonly HashSet<int> _scratchRemoteActiveIds = new();
        private readonly HashSet<int> _scratchActiveCorpseIds = new();
        private readonly List<int> _scratchStaleRemoteIds = new();
        private readonly List<int> _scratchStaleCorpseIds = new();

        private readonly struct ReviveSafeAnchor
        {
            public readonly double X;
            public readonly double Y;
            public readonly string LevelId;
            public readonly long Ticks;

            public ReviveSafeAnchor(double x, double y, string levelId, long ticks)
            {
                X = x;
                Y = y;
                LevelId = levelId ?? string.Empty;
                Ticks = ticks;
            }
        }

        private void Hook_Hero_onHeroDie(Hook_Hero.orig_onHeroDie orig, Hero self)
        {
            if (IsDebugImmortalLocalHero(self))
            {
                ApplyDebugImmortalState(self);
                return;
            }

            var net = _net;
            var suppressBroadcast = GameDataSync.ConsumeSuppressDeathBroadcast();

            if (me != null &&
                ReferenceEquals(self, me) &&
                _localFakeDead)
            {
                // Prevent second onHeroDie pass from falling into vanilla death while local player is already downed.
                return;
            }

            if (suppressBroadcast)
            {
                if (_netRole != NetRole.None &&
                    net != null &&
                    me != null &&
                    ReferenceEquals(self, me) &&
                    !_localFakeDead)
                {
                    EnterLocalFakeDeath(self, net);
                }
                return;
            }

            if (_netRole != NetRole.None &&
                net != null &&
                me != null &&
                ReferenceEquals(self, me) &&
                !_localFakeDead)
            {
                EnterLocalFakeDeath(self, net);
                return;
            }

            orig(self);
        }

        private void Hook_Hero_kill(Hook_Hero.orig_kill orig, Hero self)
        {
            if (IsDebugImmortalLocalHero(self))
            {
                ApplyDebugImmortalState(self);
                return;
            }

            var net = _net;
            if (_netRole != NetRole.None &&
                net != null &&
                me != null &&
                ReferenceEquals(self, me))
            {
                if (_localFakeDead)
                    return;

                if (ShouldEnterFakeDeathFromEarlyDeathHook(self, net))
                {
                    EnterLocalFakeDeath(self, net);
                    return;
                }
            }

            orig(self);
        }

        private void Hook_Hero_onDie(Hook_Hero.orig_onDie orig, Hero self)
        {
            if (IsDebugImmortalLocalHero(self))
            {
                ApplyDebugImmortalState(self);
                return;
            }

            var net = _net;
            if (_netRole != NetRole.None &&
                net != null &&
                me != null &&
                ReferenceEquals(self, me))
            {
                if (_localFakeDead)
                    return;

                if (ShouldEnterFakeDeathFromEarlyDeathHook(self, net))
                {
                    EnterLocalFakeDeath(self, net);
                    return;
                }
            }

            orig(self);
        }

        private void Hook_Hero_onDamage(Hook_Hero.orig_onDamage orig, Hero self, AttackData disengageRatio)
        {
            if (IsDebugImmortalLocalHero(self))
            {
                ApplyDebugImmortalState(self);
                return;
            }

            orig(self, disengageRatio);
        }

        private void Hook_Hero_checkCursedWeaponHit(Hook_Hero.orig_checkCursedWeaponHit orig, Hero self, AttackData a)
        {
            if (IsDebugImmortalLocalHero(self))
            {
                ApplyDebugImmortalState(self);
                return;
            }

            var net = _net;
            if (_netRole != NetRole.None &&
                net != null &&
                me != null &&
                ReferenceEquals(self, me))
            {
                if (_localFakeDead)
                    return;

                // v5.8: cursed deaths are unsafe in vanilla multiplayer because the vanilla death
                // flow can start controller/animation feedback cleanup before our fake-death hooks
                // see it. If the hero appears cursed, go directly to fake death and never let the
                // vanilla cursed-death path run.
                if (IsHeroLikelyCursed(self))
                {
                    EnterLocalFakeDeath(self, net);
                    return;
                }

                try
                {
                    orig(self, a);
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "[NetMod][FakeDeath] Suppressed cursed death exception and entered fake death");
                    EnterLocalFakeDeath(self, net);
                    return;
                }

                if (_localFakeDead)
                    return;

                // Cursed death sometimes starts vanilla death flow without passing through local death hooks.
                if (ShouldEnterFakeDeathFromEarlyDeathHook(self, net) || IsVanillaHeroDeathCineActive())
                {
                    EnterLocalFakeDeath(self, net);
                }
                return;
            }

            orig(self, a);
        }

        private static bool IsHeroLikelyCursed(Hero self)
        {
            if (self == null)
                return false;

            try
            {
                var t = self.GetType();
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
                                                              System.Reflection.BindingFlags.Public |
                                                              System.Reflection.BindingFlags.NonPublic;

                foreach (var f in t.GetFields(flags))
                {
                    if (!LooksLikeCurseMemberName(f.Name))
                        continue;
                    if (MemberValueMeansCurseActive(f.GetValue(self)))
                        return true;
                }

                foreach (var p in t.GetProperties(flags))
                {
                    if (!p.CanRead || !LooksLikeCurseMemberName(p.Name))
                        continue;
                    if (p.GetIndexParameters().Length != 0)
                        continue;
                    if (MemberValueMeansCurseActive(p.GetValue(self)))
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool LooksLikeCurseMemberName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            return name.IndexOf("curse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("cursed", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool MemberValueMeansCurseActive(object? value)
        {
            try
            {
                switch (value)
                {
                    case null:
                        return false;
                    case bool b:
                        return b;
                    case byte v:
                        return v > 0;
                    case sbyte v:
                        return v > 0;
                    case short v:
                        return v > 0;
                    case ushort v:
                        return v > 0;
                    case int v:
                        return v > 0;
                    case uint v:
                        return v > 0;
                    case long v:
                        return v > 0;
                    case ulong v:
                        return v > 0;
                    case float v:
                        return v > 0;
                    case double v:
                        return v > 0;
                }
            }
            catch
            {
            }

            return false;
        }

        private bool ShouldEnterFakeDeathFromEarlyDeathHook(Hero self, NetNode net)
        {
            if (self == null || net == null)
                return false;
            if (_localFakeDead)
                return false;
            if (me == null || !ReferenceEquals(self, me))
                return false;

            // Guard against spawn/initialization lifecycle where kill/onDie may fire transiently.
            try
            {
                if (self._level == null || self.spr == null)
                    return false;
                if (self.maxLife <= 0)
                    return false;
                if (self.life > 0)
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        private static bool IsVanillaHeroDeathCineActive()
        {
            try
            {
                var cine = dc.pr.Game.Class.ME?.curCine;
                return cine is HeroDeath ||
                       cine is HeroDeathBase ||
                       cine is HeroDeathContinue ||
                       cine is HeroDeathRespawn ||
                       cine is HeroDeathDLCP;
            }
            catch
            {
                return false;
            }
        }

        private bool ShouldSuppressVanillaHeroDeathCinematic(Hero? lostBody)
        {
            return _netRole != NetRole.None &&
                   _net != null &&
                   _net.IsAlive &&
                   me != null &&
                   lostBody != null &&
                   ReferenceEquals(lostBody, me);
        }

        private bool SuppressVanillaHeroDeathCinematic(Hero? lostBody, dc.GameCinematic? cine)
        {
            if (!ShouldSuppressVanillaHeroDeathCinematic(lostBody))
                return false;

            if (!_localFakeDead && lostBody != null && _net != null)
                EnterLocalFakeDeath(lostBody, _net);

            try
            {
                var game = dc.pr.Game.Class.ME;
                if (game != null && cine != null && ReferenceEquals(game.curCine, cine))
                    game.curCine = null;
            }
            catch
            {
            }

            try { cine?.destroy(); } catch { }
            try { cine?.disposeImmediately(); } catch { }
            return true;
        }

        private void Hook__HeroDeath__constructor__(Hook__HeroDeath.orig___constructor__ orig, HeroDeath e, Hero lostBody, bool fromMob)
        {
            if (SuppressVanillaHeroDeathCinematic(lostBody, e))
                return;

            orig(e, lostBody, fromMob);
        }

        private void Hook__HeroDeathBase__constructor__(Hook__HeroDeathBase.orig___constructor__ orig, HeroDeathBase e, Hero lostBody, bool mob)
        {
            if (SuppressVanillaHeroDeathCinematic(lostBody, e))
                return;

            orig(e, lostBody, mob);
        }

        private void Hook__HeroDeathContinue__constructor__(Hook__HeroDeathContinue.orig___constructor__ orig, HeroDeathContinue e, Hero lostBody, bool keepBody)
        {
            if (SuppressVanillaHeroDeathCinematic(lostBody, e))
                return;

            orig(e, lostBody, keepBody);
        }

        private void Hook__HeroDeathRespawn__constructor__(Hook__HeroDeathRespawn.orig___constructor__ orig, HeroDeathRespawn e, Hero lostBody)
        {
            if (SuppressVanillaHeroDeathCinematic(lostBody, e))
                return;

            orig(e, lostBody);
        }

        private void Hook__HeroDeathDLCP__constructor__(Hook__HeroDeathDLCP.orig___constructor__ orig, HeroDeathDLCP e, Hero lostBody, bool fromMob)
        {
            if (SuppressVanillaHeroDeathCinematic(lostBody, e))
                return;

            orig(e, lostBody, fromMob);
        }

        private void Hook_Hero_startDeathCine(Hook_Hero.orig_startDeathCine orig, Hero self)
        {
            if (IsDebugImmortalLocalHero(self))
            {
                ApplyDebugImmortalState(self);
                return;
            }

            var net = _net;
            if (_netRole != NetRole.None &&
                net != null &&
                me != null &&
                ReferenceEquals(self, me))
            {
                if (_localFakeDead)
                    return;

                // Cursed deaths can route straight into vanilla death cine and destroy hero.
                // In multiplayer we always redirect local death cine to fake-death flow.
                EnterLocalFakeDeath(self, net);
                return;
            }

            if (me != null && ReferenceEquals(self, me) && _localFakeDead)
                return;

            orig(self);
        }

        private void TryRecoverMissedFakeDeathFromLife()
        {
            var net = _net;
            var hero = me;
            if (_netRole == NetRole.None || net == null || hero == null)
                return;
            if (_localFakeDead)
                return;

            try
            {
                if (hero.destroyed || hero._level == null || hero.spr == null)
                    return;
                if (hero.maxLife <= 0)
                    return;
                if (hero.life > 0)
                    return;
            }
            catch
            {
                return;
            }

            EnterLocalFakeDeath(hero, net);
        }

        private void UpdateFakeDeathFlow(double dt)
        {
            var net = _net;
            if (_netRole == NetRole.None || net == null || me == null)
            {
                if (_localFakeDead || _remoteDowned.Count > 0)
                    ResetFakeDeathState(unlockLocalHero: true, sendNetworkUpState: false);
                ClearReviveHints();
                return;
            }

            ConsumeRemoteDownedStates(net);
            ConsumeReviveRequests(net);
            PruneRemoteDownedStates(net);
            ApplyRemoteDownedGhostPositions(net);

            if (_localFakeDead)
            {
                ClearReviveHints();
                MaintainLocalFakeDeath(net);
                return;
            }

            TrackLocalReviveSafePosition();
            ContinueReviveRequestBurst(net);
            UpdateReviveHintsByProximity();
            ProcessReviveHold(net);
        }

        private void ConsumeRemoteDownedStates(NetNode net)
        {
            if (!net.TryConsumePlayerDownStates(out var states))
                return;

            try
            {
                var localId = net.id;
                if (localId <= 0)
                    return;
                for (int i = 0; i < states.Count; i++)
                {
                    var state = states[i];
                    if (state.UserId <= 0 || state.UserId == localId)
                        continue;

                    if (!state.IsDowned)
                    {
                        if (TryGetClientIndex(localId, state.UserId, out var revivedIdx))
                        {
                            var revivedClient = clients[revivedIdx];
                            if (revivedClient != null)
                            {
                                try { revivedClient._targetable = true; } catch { }
                            }
                        }

                        _remoteDowned.Remove(state.UserId);
                        if (_reviveBurstTargetId == state.UserId)
                            ResetReviveBurst();
                        _downedAnnouncements.Remove(state.UserId);
                        DisposeRemoteDownedCine(state.UserId);
                        continue;
                    }

                    if (!_remoteDowned.TryGetValue(state.UserId, out var existing))
                    {
                        existing = new RemoteDownedState
                        {
                            UserId = state.UserId
                        };
                        _remoteDowned[state.UserId] = existing;
                    }

                    if (_downedAnnouncements.Add(state.UserId))
                        NotifyRemotePlayerDowned(net, state.UserId);

                    existing.X = state.X;
                    existing.Y = state.Y;
                    existing.HasHeadPosition = state.HasHeadPosition;
                    existing.HeadX = state.HeadX;
                    existing.HeadY = state.HeadY;
                    existing.HasHeadAnim = state.HasHeadAnim;
                    existing.HeadAnim = state.HasHeadAnim ? (state.HeadAnim ?? string.Empty) : string.Empty;
                    existing.LevelId = state.LevelId ?? string.Empty;
                    existing.UpdatedAtTicks = Stopwatch.GetTimestamp();

                    if (TryGetClientIndex(localId, state.UserId, out var downedIdx))
                    {
                        var downedClient = clients[downedIdx];
                        if (downedClient != null)
                        {
                            try { downedClient._targetable = false; } catch { }
                        }
                    }
                }
            }
            finally
            {
                NetNode.ReleaseConsumedList(states);
            }
        }

        private void NotifyRemotePlayerDowned(NetNode net, int userId)
        {
            if (userId <= 0)
                return;

            try
            {
                var displayName = ResolveRemotePlayerDisplayName(net, userId);
                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = $"Player {userId}";
                MultiplayerUI.PushSystemMessage(FormatLocalized("{0} fell!", displayName));
            }
            catch
            {
            }
        }

        private string ResolveRemotePlayerDisplayName(NetNode net, int userId)
        {
            if (net == null || userId <= 0)
                return string.Empty;

            if (net.TryGetRemoteUsername(userId, out var username) && !string.IsNullOrWhiteSpace(username))
                return username.Trim();

            if (TryGetClientIndex(net.id, userId, out var slot))
            {
                var label = GetClientLabel(slot);
                if (!string.IsNullOrWhiteSpace(label))
                    return label.Trim();
            }

            return string.Empty;
        }

        private void ConsumeReviveRequests(NetNode net)
        {
            if (!_localFakeDead)
            {
                if (net.TryConsumePlayerReviveRequests(out var ignoredRequests))
                    NetNode.ReleaseConsumedList(ignoredRequests);
                return;
            }

            if (!net.TryConsumePlayerReviveRequests(out var requests))
                return;

            try
            {
                var localId = net.id;
                for (int i = 0; i < requests.Count; i++)
                {
                    var req = requests[i];
                    if (req.TargetId != localId)
                        continue;

                    // v6.0: trust the reviver-side proximity/flask check. The downed player's
                    // local DeadBase/homunculus object can be missing or desynced after boss/DLC
                    // transitions, which made valid revive holds do nothing.
                    ReviveLocalPlayer(net);
                    return;
                }
            }
            finally
            {
                NetNode.ReleaseConsumedList(requests);
            }
        }

        private void PruneRemoteDownedStates(NetNode net)
        {
            if (_remoteDowned.Count == 0)
                return;

            _scratchRemoteActiveIds.Clear();
            var localId = net.id;
            if (localId > 0)
                _scratchRemoteActiveIds.Add(localId);

            net.CopyRemoteUserIdsTo(_scratchRemoteActiveIds);

            for (int i = 0; i < clientIds.Length; i++)
            {
                var id = clientIds[i];
                if (id > 0)
                    _scratchRemoteActiveIds.Add(id);
            }

            _scratchStaleRemoteIds.Clear();
            foreach (var pair in _remoteDowned)
            {
                if (!_scratchRemoteActiveIds.Contains(pair.Key))
                    _scratchStaleRemoteIds.Add(pair.Key);
            }

            for (int i = 0; i < _scratchStaleRemoteIds.Count; i++)
            {
                var staleId = _scratchStaleRemoteIds[i];
                DisposeRemoteDownedCine(staleId);
                _remoteDowned.Remove(staleId);
                _downedAnnouncements.Remove(staleId);
            }
        }

        private void ApplyRemoteDownedGhostPositions(NetNode net)
        {
            if (net == null)
                return;

            if (_remoteDowned.Count == 0)
            {
                DisposeAllRemoteDownedCines();
                for (int i = 0; i < clients.Length; i++)
                {
                    var client = clients[i];
                    if (client != null)
                    {
                        try { client._targetable = true; } catch { }
                    }
                }
                return;
            }

            var localId = net.id;
            var localLevelId = GetCurrentLevelId();
            _scratchActiveCorpseIds.Clear();
            foreach (var state in _remoteDowned.Values)
            {
                if (state == null || state.UserId <= 0)
                    continue;
                if (!TryGetClientIndex(localId, state.UserId, out var index))
                {
                    DisposeRemoteDownedCine(state.UserId);
                    continue;
                }

                var client = clients[index];
                if (client == null)
                {
                    DisposeRemoteDownedCine(state.UserId);
                    continue;
                }

                if (!string.IsNullOrEmpty(localLevelId) &&
                    !string.IsNullOrEmpty(state.LevelId) &&
                    !string.Equals(state.LevelId, localLevelId, StringComparison.Ordinal))
                {
                    DisposeRemoteDownedCine(state.UserId);
                    continue;
                }

                _scratchActiveCorpseIds.Add(state.UserId);
                var cine = EnsureRemoteDownedCine(state, client);
                if (cine != null)
                {
                    try
                    {
                        cine.UpdateTarget(
                            state.X,
                            state.Y,
                            client.dir,
                            state.HasHeadPosition ? state.HeadX : null,
                            state.HasHeadPosition ? state.HeadY : null,
                            state.HasHeadAnim ? state.HeadAnim : null);
                    }
                    catch { DisposeRemoteDownedCine(state.UserId); }
                }

                try { client._targetable = false; } catch { }
                try { client.setPosPixel(state.X, state.Y - DownedGhostBodyYOffsetPx); } catch { }

                rLastX[index] = state.X;
                rLastY[index] = state.Y - DownedGhostBodyYOffsetPx;
            }

            if (_remoteDownedCines.Count > 0)
            {
                _scratchStaleCorpseIds.Clear();
                foreach (var pair in _remoteDownedCines)
                {
                    if (!_scratchActiveCorpseIds.Contains(pair.Key))
                        _scratchStaleCorpseIds.Add(pair.Key);
                }

                for (int i = 0; i < _scratchStaleCorpseIds.Count; i++)
                    DisposeRemoteDownedCine(_scratchStaleCorpseIds[i]);
            }
        }

        private RemoteDownedCorpse? EnsureRemoteDownedCine(RemoteDownedState state, GhostKing client)
        {
            if (state == null || client == null || me == null)
                return null;

            if (_remoteDownedCines.TryGetValue(state.UserId, out var existing))
            {
                if (existing != null)
                    return existing;

                _remoteDownedCines.Remove(state.UserId);
            }

            try
            {
                var previousCine = dc.pr.Game.Class.ME?.curCine;
                var created = new RemoteDownedCorpse(me, client, state.X, state.Y, client.dir, previousCine);
                _remoteDownedCines[state.UserId] = created;
                return created;
            }
            catch
            {
                _remoteDownedCines.Remove(state.UserId);
                return null;
            }
        }

        private void DisposeRemoteDownedCine(int userId)
        {
            if (!_remoteDownedCines.TryGetValue(userId, out var cine) || cine == null)
                return;

            _remoteDownedCines.Remove(userId);
            try { cine.destroy(); } catch { }
            try { cine.disposeImmediately(); } catch { }
        }

        private void DisposeAllRemoteDownedCines()
        {
            if (_remoteDownedCines.Count == 0)
                return;

            _scratchStaleCorpseIds.Clear();
            foreach (var id in _remoteDownedCines.Keys)
                _scratchStaleCorpseIds.Add(id);

            for (int i = 0; i < _scratchStaleCorpseIds.Count; i++)
                DisposeRemoteDownedCine(_scratchStaleCorpseIds[i]);
        }

        private bool HasAliveRemoteTeammate(NetNode net)
        {
            var localId = net.id;
            _scratchRemoteActiveIds.Clear();
            net.CopyRemoteUserIdsTo(_scratchRemoteActiveIds, includePrimary: false);

            for (int i = 0; i < clientIds.Length; i++)
            {
                var id = clientIds[i];
                if (id > 0 && id != localId)
                    _scratchRemoteActiveIds.Add(id);
            }

            if (_scratchRemoteActiveIds.Count == 0)
            {
                if (_remoteDowned.Count > 0)
                    return false;

                if (net.IsHost)
                    return NetNode.ConnectedClientCount > 0;
                return net.IsAlive;
            }

            var localLevelId = GetCurrentLevelId();
            foreach (var id in _scratchRemoteActiveIds)
            {
                if (!_remoteDowned.TryGetValue(id, out var downed))
                    return true;

                // If teammate is tracked as downed on another level, treat them as alive.
                if (!string.Equals(localLevelId, downed.LevelId, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private void CaptureLocalDownedStats(Hero hero)
        {
            if (hero == null)
                return;

            _localDownedStatsCaptured = true;
            try { _localDownedSavedMaxLife = System.Math.Max(_localDownedSavedMaxLife, hero.maxLife); } catch { }
            try { _localDownedSavedBrutalityTier = System.Math.Max(_localDownedSavedBrutalityTier, hero.brutalityTier); } catch { }
            try { _localDownedSavedSurvivalTier = System.Math.Max(_localDownedSavedSurvivalTier, hero.survivalTier); } catch { }
            try { _localDownedSavedTacticTier = System.Math.Max(_localDownedSavedTacticTier, hero.tacticTier); } catch { }
            try { _localDownedSavedBonusLife = System.Math.Max(_localDownedSavedBonusLife, (int)System.Math.Round((double)hero.bonusLife)); } catch { }

            try
            {
                var data = hero._level?.game?.data;
                if (data != null)
                {
                    try { _localDownedSavedBrutalityTier = System.Math.Max(_localDownedSavedBrutalityTier, data.brutalityTier); } catch { }
                    try { _localDownedSavedSurvivalTier = System.Math.Max(_localDownedSavedSurvivalTier, data.survivalTier); } catch { }
                    try { _localDownedSavedTacticTier = System.Math.Max(_localDownedSavedTacticTier, data.tacticTier); } catch { }
                }
            }
            catch { }
        }

        private void RestoreLocalDownedStats(Hero hero)
        {
            if (hero == null || !_localDownedStatsCaptured)
                return;

            try
            {
                if (_localDownedSavedMaxLife > 0 && hero.maxLife < _localDownedSavedMaxLife)
                    hero.maxLife = _localDownedSavedMaxLife;
            }
            catch { }

            try
            {
                if (_localDownedSavedBonusLife > 0 && hero.bonusLife < _localDownedSavedBonusLife)
                    hero.bonusLife = _localDownedSavedBonusLife;
            }
            catch { }

            try
            {
                if (_localDownedSavedBrutalityTier > 0 && hero.brutalityTier < _localDownedSavedBrutalityTier)
                    hero.brutalityTier = _localDownedSavedBrutalityTier;
                if (_localDownedSavedSurvivalTier > 0 && hero.survivalTier < _localDownedSavedSurvivalTier)
                    hero.survivalTier = _localDownedSavedSurvivalTier;
                if (_localDownedSavedTacticTier > 0 && hero.tacticTier < _localDownedSavedTacticTier)
                    hero.tacticTier = _localDownedSavedTacticTier;
            }
            catch { }

            try
            {
                var data = hero._level?.game?.data;
                if (data != null)
                {
                    if (_localDownedSavedBrutalityTier > 0 && data.brutalityTier < _localDownedSavedBrutalityTier)
                        data.brutalityTier = _localDownedSavedBrutalityTier;
                    if (_localDownedSavedSurvivalTier > 0 && data.survivalTier < _localDownedSavedSurvivalTier)
                        data.survivalTier = _localDownedSavedSurvivalTier;
                    if (_localDownedSavedTacticTier > 0 && data.tacticTier < _localDownedSavedTacticTier)
                        data.tacticTier = _localDownedSavedTacticTier;
                }
            }
            catch { }

            try
            {
                if (_localDownedSavedMaxLife > 0 && hero.maxLife < _localDownedSavedMaxLife)
                    hero.maxLife = _localDownedSavedMaxLife;
            }
            catch { }
        }

        private void ClearLocalDownedStatSnapshot()
        {
            _localDownedStatsCaptured = false;
            _localDownedSavedMaxLife = 0;
            _localDownedSavedBrutalityTier = 0;
            _localDownedSavedSurvivalTier = 0;
            _localDownedSavedTacticTier = 0;
            _localDownedSavedBonusLife = 0;
        }

        private static void SetLocalGameTimerPausedForRevive(bool paused)
        {
            // v6.3.5: do not freeze Dead Cells global game time while fake-dead.
            // Using data.stopGameTime for revive pause can soft-lock the client after spike/void
            // rescue because the local game keeps its cinematic/control state frozen. Keep the
            // world running and only make sure older paused states are cleared.
            try
            {
                var data = dc.pr.Game.Class.ME?.data;
                if (data != null && data.stopGameTime)
                    data.stopGameTime = false;
            }
            catch { }
        }

        private void ResetReviveBurst()
        {
            _reviveBurstTargetId = 0;
            _reviveBurstUntilTicks = 0;
            _nextReviveBurstSendTicks = 0;
        }

        private void StartReviveRequestBurst(NetNode net, int targetId)
        {
            if (net == null || targetId <= 0)
                return;

            var now = Stopwatch.GetTimestamp();
            _reviveBurstTargetId = targetId;
            _reviveBurstUntilTicks = now + (long)(Stopwatch.Frequency * ReviveRequestBurstSeconds);
            _nextReviveBurstSendTicks = 0;
            SendReviveRequestBurstTick(net, now);
        }

        private void ContinueReviveRequestBurst(NetNode net)
        {
            if (net == null || _reviveBurstTargetId <= 0)
                return;

            var now = Stopwatch.GetTimestamp();
            if (_reviveBurstUntilTicks == 0 || now >= _reviveBurstUntilTicks)
            {
                ResetReviveBurst();
                return;
            }

            if (!_remoteDowned.ContainsKey(_reviveBurstTargetId))
            {
                ResetReviveBurst();
                return;
            }

            SendReviveRequestBurstTick(net, now);
        }

        private void SendReviveRequestBurstTick(NetNode net, long now)
        {
            if (_reviveBurstTargetId <= 0)
                return;
            if (_nextReviveBurstSendTicks != 0 && now < _nextReviveBurstSendTicks)
                return;

            try { net.SendPlayerReviveRequest(_reviveBurstTargetId); } catch { }
            _nextReviveBurstSendTicks = now + (long)(Stopwatch.Frequency * ReviveRequestBurstIntervalSeconds);
        }

        private void EnterLocalFakeDeath(Hero hero, NetNode net)
        {
            if (hero == null)
                return;

            ResetAllDownedGameOverState();
            ClearLocalDownedStatSnapshot();
            CaptureLocalDownedStats(hero);
            SetLocalGameTimerPausedForRevive(true);
            _localFakeDead = true;
            _localExitPenaltyApplied = false;
            _localFakeDeadStartedTicks = Stopwatch.GetTimestamp();
            double sprX, sprY;
            if (hero.spr != null)
            {
                sprX = hero.spr.x;
                sprY = hero.spr.y;
            }
            else
            {
                try
                {
                    var cx = hero.cx;
                    var xr = hero.xr;
                    var cy = hero.cy;
                    var yr = hero.yr;
                    sprX = (cx + xr) * 24.0;
                    sprY = (cy + yr) * 24.0;
                }
                catch
                {
                    sprX = 0;
                    sprY = 0;
                }
            }
            _localDownedX = sprX;
            _localDownedY = sprY;
            _localHeldX = _localDownedX;
            _localHeldY = _localDownedY;
            RescueLocalDownedPositionIfUnsafe(hero, "enter_fake_death", force: true);
            _localDownedAnchorX = _localDownedX;
            _localDownedAnchorY = _localDownedY;
            _hasLocalDownedAnchor = true;
            _localDownedLevelId = GetCurrentLevelId();
            _nextDownedStateSendTicks = 0;
            _nextReviveAttemptTicks = 0;
            _postReviveLockUntilTicks = 0;

            try
            {
                if (hero.life <= 0)
                    hero.life = 1;
            }
            catch { }

            try { hero._targetable = false; } catch { }
            try { hero.cancelVelocities(); } catch { }
            try { hero.lockControlsS(10.0); } catch { }
            try { hero.cancelSkillControlLock(); } catch { }
            ForceParkLocalDownedHero(hero, clampToGround: true);

            SendLocalDownedState(net, isDowned: true, force: true);
        }

        private void MaintainLocalFakeDeath(NetNode net)
        {
            if (!_localFakeDead || me == null)
                return;

            SetLocalGameTimerPausedForRevive(true);

            try
            {
                if (me.life <= 0)
                    me.life = 1;
            }
            catch
            {
            }

            // v6.4.6: no local death cinematic/corpse while downed. The hidden hero is parked
            // at one safe anchor; this prevents the vanilla death body from falling through the map.

            if (!HasAliveRemoteTeammate(net))
            {
                var now = Stopwatch.GetTimestamp();
                var graceTicks = (long)(Stopwatch.Frequency * 1.25);
                if (_localFakeDeadStartedTicks != 0 &&
                    now - _localFakeDeadStartedTicks < graceTicks)
                {
                    return;
                }

                HandleAllPlayersDowned(net);
                return;
            }

            if (_allDownedGameOverShown || _allDownedRestartQueued)
                ResetAllDownedGameOverState();

            try { me.cancelVelocities(); } catch { }
            try { me.lockControlsS(0.25); } catch { }
            try { me.cancelSkillControlLock(); } catch { }
            try { me._targetable = false; } catch { }

            // v6.4.7: once fake-dead, keep one authoritative anchor until revive/reset.
            // Do not let any corpse/hero physics update become the new revive position.
            if (!MaintainDownedSafeRescueLock())
                RescueLocalDownedPositionIfUnsafe(me, "maintain_fake_death", force: false);
            ReassertLocalDownedAnchor(me);

            ForceParkLocalDownedHero(me, clampToGround: true);
            SendLocalDownedState(net, isDowned: true, force: false);
        }

        private void MaintainPostRevivePositionLock()
        {
            if (_localFakeDead || me == null)
                return;
            if (_postReviveLockUntilTicks == 0)
                return;

            var now = Stopwatch.GetTimestamp();
            if (now >= _postReviveLockUntilTicks)
            {
                _postReviveLockUntilTicks = 0;
                return;
            }

            SnapHeroToDownedPosition(me, _postReviveLockX, _postReviveLockY);
        }

        private void ForceParkLocalDownedHero(Hero hero, bool clampToGround = true)
        {
            if (hero == null)
                return;

            CaptureLocalDownedHeroRuntimeState(hero);

            try
            {
                if (hero.life <= 0)
                    hero.life = 1;
            }
            catch { }

            try { hero._targetable = false; } catch { }
            try { hero.visible = false; } catch { }
            TrySetHeroHeadVisible(hero, false);

            try { hero.dx = 0; } catch { }
            try { hero.dy = 0; } catch { }
            try { hero.bdx = 0; } catch { }
            try { hero.bdy = 0; } catch { }
            try { hero.hasGravity = false; } catch { }
            try { hero.cancelVelocities(); } catch { }
            try { hero.cancelSkillControlLock(); } catch { }
            try { hero.lockControlsS(0.35); } catch { }

            var x = _localHeldX;
            var y = _localHeldY - DownedParkedHeroYOffsetPx;
            try { hero.setPosPixel(x, y); } catch { }
            try { ForceSetHeroCaseFromPixel(hero, x, y); } catch { }
            if (clampToGround)
                SnapHeroToDownedPosition(hero, x, y, clampToGround: true);

            try { hero.cancelVelocities(); } catch { }
            try { hero.dx = 0; } catch { }
            try { hero.dy = 0; } catch { }
            try { hero.bdx = 0; } catch { }
            try { hero.bdy = 0; } catch { }
        }

        private void CaptureLocalDownedHeroRuntimeState(Hero hero)
        {
            if (hero == null)
                return;

            if (!_localDownedHeroGravityWasCaptured)
            {
                try { _localDownedHeroHadGravity = hero.hasGravity; }
                catch { _localDownedHeroHadGravity = true; }
                _localDownedHeroGravityWasCaptured = true;
            }

            if (!_localDownedHeroVisibilityWasCaptured)
            {
                try { _localDownedHeroWasVisible = hero.visible; }
                catch { _localDownedHeroWasVisible = true; }
                _localDownedHeroVisibilityWasCaptured = true;
            }
        }

        private void RestoreLocalDownedHeroRuntimeState(Hero? hero)
        {
            if (hero != null)
            {
                try { hero.hasGravity = _localDownedHeroGravityWasCaptured ? _localDownedHeroHadGravity : true; } catch { }
                try { hero.visible = _localDownedHeroVisibilityWasCaptured ? _localDownedHeroWasVisible : true; } catch { }
                TrySetHeroHeadVisible(hero, true);
                try { hero._targetable = true; } catch { }
            }

            _localDownedHeroGravityWasCaptured = false;
            _localDownedHeroHadGravity = true;
            _localDownedHeroVisibilityWasCaptured = false;
            _localDownedHeroWasVisible = true;
        }

        private static void TrySetHeroHeadVisible(Hero hero, bool visible)
        {
            if (hero == null)
                return;

            try
            {
                var head = hero.heroHead;
                if (head == null)
                    return;

                try { head.customHeadSpr?.set_visible(visible); } catch { }
                try { head.customBackSpr?.set_visible(visible); } catch { }
                try { head.headNormalSb?.set_visible(visible); } catch { }
                try { head.headAddSb?.set_visible(visible); } catch { }
                try { head.eye?.set_visible(visible); } catch { }
                // Leave headBlack untouched; sprite visibility is enough and avoids changing
                // custom-head state after revive.
            }
            catch
            {
            }
        }

        private void ReassertLocalDownedAnchor(Hero hero)
        {
            if (hero == null || !_localFakeDead)
                return;

            if (!_hasLocalDownedAnchor)
            {
                _localDownedAnchorX = _localHeldX;
                _localDownedAnchorY = _localHeldY;
                _hasLocalDownedAnchor = double.IsFinite(_localDownedAnchorX) && double.IsFinite(_localDownedAnchorY);
            }

            if (!_hasLocalDownedAnchor)
                return;

            _localDownedX = _localDownedAnchorX;
            _localDownedY = _localDownedAnchorY;
            _localHeldX = _localDownedAnchorX;
            _localHeldY = _localDownedAnchorY;
            _downedSafeRescueLockX = _localDownedAnchorX;
            _downedSafeRescueLockY = _localDownedAnchorY;
            if (_downedSafeRescueLockUntilTicks == 0)
                _downedSafeRescueLockUntilTicks = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * DownedPermanentAnchorRefreshSeconds);

            try { hero.cancelVelocities(); } catch { }
            try { hero.dx = 0; } catch { }
            try { hero.dy = 0; } catch { }
            try { hero.bdx = 0; } catch { }
            try { hero.bdy = 0; } catch { }
            try { hero.hasGravity = false; } catch { }
            try { hero._targetable = false; } catch { }
            try { hero.visible = false; } catch { }
            TrySetHeroHeadVisible(hero, false);
            try { hero.setPosPixel(_localDownedAnchorX, _localDownedAnchorY - DownedParkedHeroYOffsetPx); } catch { }
            try { ForceSetHeroCaseFromPixel(hero, _localDownedAnchorX, _localDownedAnchorY - DownedParkedHeroYOffsetPx); } catch { }
        }

        private static void ForceSetHeroCaseFromPixel(Hero hero, double x, double y)
        {
            if (hero == null || !double.IsFinite(x) || !double.IsFinite(y))
                return;

            try
            {
                var cx = (int)System.Math.Floor(x / 24.0);
                var cy = (int)System.Math.Floor(y / 24.0);
                var xr = (x / 24.0) - cx;
                var yr = (y / 24.0) - cy;
                if (double.IsFinite(xr) && double.IsFinite(yr))
                    hero.setPosCase(cx, cy, xr, yr);
            }
            catch
            {
            }
        }

        private static void SnapHeroToDownedPosition(Hero hero, double x, double y, bool clampToGround = true)
        {
            if (hero == null)
                return;

            try { hero.setPosPixel(x, y); } catch { }

            if (!clampToGround)
                return;

            // Keep hero on/above ground in case target position is slightly below tiles.
            try
            {
                var map = hero._level?.map;
                if (map == null)
                    return;

                var cx = hero.cx;
                var cy = hero.cy;
                var xr = hero.xr;
                var yr = hero.yr;
                var groundYr = map.getGroundYr(cx, cy, Ref<double>.From(ref xr), Ref<double>.From(ref yr));
                if (double.IsFinite(groundYr) && hero.yr > groundYr)
                    hero.setPosCase(cx, cy, xr, groundYr);
            }
            catch
            {
            }
        }

        private void ReviveLocalPlayer(NetNode net)
        {
            if (me == null)
                return;

            ResetAllDownedGameOverState();
            var hero = me;
            RestoreLocalDownedStats(hero);
            SetLocalGameTimerPausedForRevive(false);
            RestoreLocalDownedHeroRuntimeState(hero);
            _localFakeDead = false;
            _localExitPenaltyApplied = false;
            _localFakeDeadStartedTicks = 0;
            _nextDownedStateSendTicks = 0;
            _nextReviveAttemptTicks = 0;
            _localDownedLevelId = string.Empty;
            _hasLocalDownedAnchor = false;
            _localDownedAnchorX = 0;
            _localDownedAnchorY = 0;
            StopLocalDeadCine();

            var reviveX = _localDownedX;
            var reviveY = _localDownedY - LocalReviveBodyYOffsetPx;
            SnapHeroToDownedPosition(hero, reviveX, reviveY);
            try
            {
                _postReviveLockX = hero.get_targetSprPosX();
                _postReviveLockY = hero.get_targetSprPosY();
            }
            catch
            {
                _postReviveLockX = reviveX;
                _postReviveLockY = reviveY;
            }
            _postReviveLockUntilTicks = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * PostRevivePositionLockSeconds);
            _localHeldX = _postReviveLockX;
            _localHeldY = _postReviveLockY;

            if (IsHeroRuntimeSafeForControlUnlock(hero))
            {
                try { hero.cancelVelocities(); } catch { }
                try { hero.cancelSkillControlLock(); } catch { }
                try { hero.unlockControls(); } catch { }
                try { hero._targetable = true; } catch { }
            }

            try
            {
                var currentLife = hero.life;
                var maxLife = hero.maxLife;
                var targetLife = System.Math.Max(1, (int)System.Math.Ceiling(maxLife * 0.5));
                var healAmount = targetLife - currentLife;
                if (healAmount > 0)
                    hero.heal(healAmount);
                if (hero.life < targetLife)
                    hero.life = targetLife;
            }
            catch
            {
                try { hero.fullHeal(); } catch { }
            }

            try { net.SendHP(hero.life, hero.maxLife, hero.life, hero.bonusLife, hero.radius); } catch { }
            SendLocalDownedState(net, isDowned: false, force: true);
            ClearLocalDownedStatSnapshot();
        }

        private void ApplyLocalDownedExitPenaltyIfNeededCore()
        {
            // v6.3: no stat/HP penalty while downed. The old penalty removed
            // Brutality/Survival/TacticUp items during auto-follow/exit failsafe,
            // which made revived players drop back to base HP after being carried
            // through doors or boss exits.
            _localExitPenaltyApplied = true;
        }

        private void ProcessReviveHold(NetNode net)
        {
            if (me == null || _remoteDowned.Count == 0)
            {
                ResetReviveHold();
                ResetReviveBurst();
                ClearReviveHints();
                return;
            }

            var isHoldPressed = GameMenu.IsReviveHoldInputDown(me);

            if (!isHoldPressed)
            {
                ResetReviveHold();
                return;
            }

            var nearest = FindNearestReviveTarget();
            if (nearest == null)
            {
                ResetReviveHold();
                return;
            }

            ShowReviveHintFor(nearest.UserId);
            var now = Stopwatch.GetTimestamp();
            var holdTicks = (long)(Stopwatch.Frequency * ReviveHoldSeconds);

            if (_reviveHoldTargetId != nearest.UserId)
            {
                _reviveHoldTargetId = nearest.UserId;
                _reviveHoldStartedTicks = now;
                return;
            }

            if (_reviveHoldStartedTicks == 0)
                _reviveHoldStartedTicks = now;

            if (now - _reviveHoldStartedTicks < holdTicks)
                return;

            if (_nextReviveAttemptTicks != 0 && now < _nextReviveAttemptTicks)
                return;

            if (!TryConsumeOneFlask(me))
            {
                ResetReviveHold();
                return;
            }

            StartReviveRequestBurst(net, nearest.UserId);
            _nextReviveAttemptTicks = now + (long)(Stopwatch.Frequency * ReviveAttemptCooldownSeconds);
            try { MultiplayerUI.PushSystemMessage(FormatLocalized("Reviving player..."), 2.0, 0.3); } catch { }
            ResetReviveHold();
            ClearReviveHints();
        }

        private void UpdateReviveHintsByProximity()
        {
            if (me == null || _remoteDowned.Count == 0)
            {
                ClearReviveHints();
                return;
            }

            var nearest = FindNearestReviveTarget();
            if (nearest == null)
            {
                ClearReviveHints();
                return;
            }

            ShowReviveHintFor(nearest.UserId);
        }

        private RemoteDownedState? FindNearestReviveTarget()
        {
            if (me == null || _remoteDowned.Count == 0)
                return null;

            var localLevelId = GetCurrentLevelId();
            RemoteDownedState? nearest = null;
            var x = me.spr?.x ?? 0;
            var y = me.spr?.y ?? 0;
            var bestDistSq = double.MaxValue;

            foreach (var state in _remoteDowned.Values)
            {
                if (state == null || state.UserId <= 0)
                    continue;

                if (!string.IsNullOrEmpty(localLevelId) &&
                    !string.IsNullOrEmpty(state.LevelId) &&
                    !string.Equals(state.LevelId, localLevelId, StringComparison.Ordinal))
                {
                    continue;
                }

                var dx = state.X - x;
                var dy = state.Y - y;
                var distSq = dx * dx + dy * dy;
                if (distSq > ReviveUseDistancePx * ReviveUseDistancePx)
                    continue;

                // v6.0: do not reject revive because the downed player's homunculus/head
                // position is stale. The body position is the authoritative revive target.

                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    nearest = state;
                }
            }

            return nearest;
        }

        private void ResetReviveHold()
        {
            _reviveHoldTargetId = 0;
            _reviveHoldStartedTicks = 0;
        }

        private void ShowReviveHintFor(int userId)
        {
            if (_remoteDownedCines.Count == 0)
                return;

            foreach (var pair in _remoteDownedCines)
            {
                var cine = pair.Value;
                if (cine == null)
                    continue;

                try
                {
                    if (pair.Key == userId)
                        cine.SetInteractionLabel(Localize(ReviveHintText));
                    else
                        cine.SetInteractionLabel(null);
                }
                catch
                {
                }
            }
        }

        private void ClearReviveHints()
        {
            if (_remoteDownedCines.Count == 0)
                return;

            foreach (var cine in _remoteDownedCines.Values)
            {
                if (cine == null)
                    continue;
                try { cine.SetInteractionLabel(null); } catch { }
            }
        }

        private bool TryConsumeOneFlask(Hero hero)
        {
            if (hero == null)
                return false;

            try
            {
                var manager = hero.mainSkillsManager;
                if (manager == null)
                    return false;

                var heal = manager.getMainSkill(Heal.Class) as Heal;
                if (heal == null)
                    return false;

                var current = heal.get_healings();
                if (current <= 0)
                    return false;

                var next = current - 1;
                if (next < 0)
                    next = 0;
                heal.set_healings(next);
                heal.setFlaskGlow();

                try
                {
                    var max = heal.get_maxHealings();
                    var hud = dc.ui.HUD.Class.ME;
                    hud?.setHealings(heal.get_healings(), max);
                }
                catch { }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void SendLocalDownedState(NetNode net, bool isDowned, bool force)
        {
            if (net == null || net.id <= 0)
                return;

            double? headX = null;
            double? headY = null;
            string? headAnim = null;

            var now = Stopwatch.GetTimestamp();
            var resend = (long)(Stopwatch.Frequency * DownedStateResendSeconds);
            if (!force && _nextDownedStateSendTicks != 0 && now < _nextDownedStateSendTicks)
                return;

            var level = isDowned
                ? (!string.IsNullOrWhiteSpace(_localDownedLevelId) ? _localDownedLevelId : GetCurrentLevelId())
                : GetCurrentLevelId();
            var x = isDowned ? _localDownedX : (me?.spr?.x ?? _localDownedX);
            var y = isDowned ? _localDownedY : (me?.spr?.y ?? _localDownedY);

            net.SendPlayerDownState(isDowned, x, y, level, headX, headY, headAnim);
            _nextDownedStateSendTicks = now + resend;
        }

        private string GetCurrentLevelId()
        {
            try
            {
                var currentLevelId = me?._level?.map?.id?.ToString();
                if (!string.IsNullOrWhiteSpace(currentLevelId))
                    return currentLevelId.Trim();
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(levelId))
                return levelId.Trim();

            return string.Empty;
        }

        private void StartLocalDeadCine(Hero hero)
        {
            // v6.4.6: intentionally disabled. Even with HeroDeadCorpse creation disabled in
            // DeadBase, entering the vanilla ghost-death cinematic can still leave a physical
            // body/target anchor that falls through floors and can trigger duplicated drops.
            _localDeadCine = null;
        }

        private void StopLocalDeadCine()
        {
            var cine = _localDeadCine;
            _localDeadCine = null;
            if (cine == null)
                return;

            try { cine.destroy(); } catch { }
            try { cine.disposeImmediately(); } catch { }
        }

        private void ResetFakeDeathState(
            bool unlockLocalHero,
            bool sendNetworkUpState,
            bool clearRemoteDownedTracking = true,
            bool clearDownedAnnouncements = true)
        {
            ResetAllDownedGameOverState();
            var wasFakeDead = _localFakeDead;
            RestoreLocalDownedHeroRuntimeState(me);
            _localFakeDead = false;
            _localExitPenaltyApplied = false;
            _localFakeDeadStartedTicks = 0;
            StopLocalDeadCine();
            _localDownedX = 0;
            _localDownedY = 0;
            _localHeldX = 0;
            _localHeldY = 0;
            _localDownedLevelId = string.Empty;
            _nextReviveAttemptTicks = 0;
            _nextDownedStateSendTicks = 0;
            _postReviveLockUntilTicks = 0;
            _postReviveLockX = 0;
            _postReviveLockY = 0;
            _downedSafeRescueLockUntilTicks = 0;
            _downedSafeRescueLockX = 0;
            _downedSafeRescueLockY = 0;
            SetLocalGameTimerPausedForRevive(false);
            ResetReviveBurst();
            ClearLocalDownedStatSnapshot();
            _hasLocalDownedAnchor = false;
            _localDownedAnchorX = 0;
            _localDownedAnchorY = 0;
            _nextDownedSafeRescueCheckTicks = 0;
            ResetReviveHold();
            ClearReviveHints();
            if (clearRemoteDownedTracking)
                _remoteDowned.Clear();
            if (clearDownedAnnouncements)
                _downedAnnouncements.Clear();
            DisposeAllRemoteDownedCines();
            for (int i = 0; i < clients.Length; i++)
            {
                var client = clients[i];
                if (client != null)
                {
                    try { client._targetable = true; } catch { }
                }
            }

            if (unlockLocalHero && IsHeroRuntimeSafeForControlUnlock(me))
            {
                try { me!.cancelSkillControlLock(); } catch { }
                try { me!.unlockControls(); } catch { }
                try { me!._targetable = true; } catch { }
            }

            if (sendNetworkUpState && wasFakeDead && _net != null && _netRole != NetRole.None)
            {
                try { _net.SendPlayerDownState(false, me?.spr?.x ?? 0, me?.spr?.y ?? 0, GetCurrentLevelId()); } catch { }
            }
        }

        private void HandleAllPlayersDowned(NetNode net)
        {
            if (me == null || net == null)
                return;

            try
            {
                if (me.life <= 0)
                    me.life = 1;
            }
            catch
            {
            }

            var now = Stopwatch.GetTimestamp();
            if (!_allDownedGameOverShown)
            {
                ShowAllDownedGameOverLogo();
                _allDownedGameOverShown = true;
                _allDownedRestartAtTicks = now + (long)(Stopwatch.Frequency * AllDownedGameOverDelaySeconds);
            }

            try { me.cancelVelocities(); } catch { }
            try { me.lockControlsS(0.25); } catch { }
            try { me.cancelSkillControlLock(); } catch { }
            try { me._targetable = false; } catch { }

            RescueLocalDownedPositionIfUnsafe(me, "all_downed_fake_death", force: false);
            ForceParkLocalDownedHero(me, clampToGround: true);
            SendLocalDownedState(net, isDowned: true, force: false);

            if (_allDownedRestartQueued || _netRole != NetRole.Host)
                return;

            if (_allDownedRestartAtTicks != 0 && now < _allDownedRestartAtTicks)
                return;

            // v5.9: do not auto-call launchGame/newGame while both players are in the
            // fake-death/game-over state. The current DCCM/Hashlink build can hit an
            // AccessViolation in User.newGame/GC during that transition. Stop the session
            // cleanly and let the host start a fresh multiplayer run from the menu instead.
            _allDownedRestartQueued = true;
            try { MultiplayerUI.PushSystemMessage("Both players are down. Multiplayer stopped safely; start a new run from the menu."); } catch { }
            try { StopNetworkFromMenu(); } catch { }
        }

        private void ShowAllDownedGameOverLogo()
        {
            try
            {
                if (dc.ui.Console.Class.ME != null &&
                    dc.ui.Console.Class.ME.flags.exists(dc.ui.Console.Class.HIDE_UI))
                {
                    return;
                }
            }
            catch
            {
            }

            try
            {
                var existing = GameOver.Class.ME;
                if (existing != null)
                    return;
            }
            catch
            {
            }

            try
            {
                _ = new GameOver(Localize("Game Over").AsHaxeString(), true, null);
            }
            catch
            {
            }
        }

        private static bool IsHeroRuntimeSafeForControlUnlock(Hero? hero)
        {
            if (hero == null)
                return false;

            try
            {
                if (hero.destroyed || hero._level == null || hero._level.destroyed)
                    return false;
                if (hero._level.game == null || hero._level.game.destroyed)
                    return false;
                if (dc.pr.Game.Class.ME == null)
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        private static string Localize(string message)
        {
            return GetText.Instance.GetString(message);
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

        private void ResetAllDownedGameOverState()
        {
            _allDownedGameOverShown = false;
            _allDownedRestartQueued = false;
            _allDownedRestartAtTicks = 0;
        }

        private void TrackLocalReviveSafePosition()
        {
            var hero = me;
            if (hero == null || _localFakeDead)
                return;

            if (!TryGetSafeReviveAnchor(hero, out var x, out var y))
                return;

            _hasLocalReviveSafePosition = true;
            _localReviveSafeX = x;
            _localReviveSafeY = y;
            _localReviveSafeLevelId = GetCurrentLevelId();
            _localReviveSafeTicks = Stopwatch.GetTimestamp();
            RecordLocalReviveSafeHistory(x, y, _localReviveSafeLevelId, _localReviveSafeTicks);
        }

        private bool RescueLocalDownedPositionIfUnsafe(Hero hero, string reason, bool force, double? proposedX = null, double? proposedY = null)
        {
            if (hero == null)
                return false;

            var now = Stopwatch.GetTimestamp();
            if (!force && _nextDownedSafeRescueCheckTicks != 0 && now < _nextDownedSafeRescueCheckTicks)
                return false;
            _nextDownedSafeRescueCheckTicks = now + (long)(Stopwatch.Frequency * DownedUnsafeRescueCheckIntervalSeconds);

            var checkX = proposedX ?? _localHeldX;
            var checkY = proposedY ?? _localHeldY;
            if (!ShouldUseSavedSafeDownedPosition(hero, checkX, checkY, force))
                return false;

            if (!TryGetBestDownedRescuePoint(hero, out var safeX, out var safeY, out var source))
                return false;

            _localDownedX = safeX;
            _localDownedY = safeY;
            _localHeldX = safeX;
            _localHeldY = safeY;
            _localDownedAnchorX = safeX;
            _localDownedAnchorY = safeY;
            _hasLocalDownedAnchor = true;
            _downedSafeRescueLockX = safeX;
            _downedSafeRescueLockY = safeY;
            _downedSafeRescueLockUntilTicks = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * DownedSafeRescueLockSeconds);
            _nextDownedStateSendTicks = 0;

            StabilizeLocalDownedAfterSafeRescue(hero, reason);

            try
            {
                Logger.Information("[NetMod][Revive] Moved downed body to safe revive point reason={Reason} source={Source} x={X:0.0} y={Y:0.0}", reason, source, safeX, safeY);
            }
            catch
            {
            }

            return true;
        }

        private void StabilizeLocalDownedAfterSafeRescue(Hero hero, string reason)
        {
            if (hero == null)
                return;

            // The safe-position rescue can happen while the player is dying in spikes/void and
            // while vanilla death/hazard states still hold velocities or cinematic locks. Clear
            // those transient states without reviving the player; they remain fake-dead/downed.
            try { hero.cancelVelocities(); } catch { }
            try { hero.cancelSkillControlLock(); } catch { }
            try { hero.lockControlsS(0.25); } catch { }
            try { hero._targetable = false; } catch { }
            try
            {
                var data = dc.pr.Game.Class.ME?.data;
                if (data != null && data.stopGameTime)
                    data.stopGameTime = false;
            }
            catch { }

            ForceParkLocalDownedHero(hero, clampToGround: true);
        }

        private bool MaintainDownedSafeRescueLock()
        {
            if (!_localFakeDead || me == null || _downedSafeRescueLockUntilTicks == 0)
                return false;

            var now = Stopwatch.GetTimestamp();
            // v6.4.7: do not expire the safe anchor while the player is downed. The previous
            // 6 second expiry let vanilla falling/corpse physics become authoritative again,
            // which caused the marker to sink through floors and trigger duplicate drops.
            if (now >= _downedSafeRescueLockUntilTicks)
                _downedSafeRescueLockUntilTicks = now + (long)(Stopwatch.Frequency * DownedPermanentAnchorRefreshSeconds);

            _localDownedX = _downedSafeRescueLockX;
            _localDownedY = _downedSafeRescueLockY;
            _localHeldX = _downedSafeRescueLockX;
            _localHeldY = _downedSafeRescueLockY;
            _localDownedAnchorX = _downedSafeRescueLockX;
            _localDownedAnchorY = _downedSafeRescueLockY;
            _hasLocalDownedAnchor = true;

            try { me.cancelVelocities(); } catch { }
            try { me.lockControlsS(0.25); } catch { }
            try { me._targetable = false; } catch { }
            ForceParkLocalDownedHero(me, clampToGround: true);
            return true;
        }

        private void TrySnapLocalDeadCineToHeldPosition()
        {
            // v6.4.6: local death cinematic is disabled. The hidden hero is parked directly.
        }

        private bool ShouldUseSavedSafeDownedPosition(Hero hero, double x, double y, bool force)
        {
            if (hero == null)
                return false;
            if (!double.IsFinite(x) || !double.IsFinite(y))
                return true;
            if (force && !IsPixelPositionReviveAccessible(hero, x, y))
                return true;

            if (_hasLocalReviveSafePosition)
            {
                if (IsLocalReviveSafePositionFresh() && IsLocalReviveSafePositionForCurrentLevel())
                {
                    if (y > _localReviveSafeY + DownedVoidRescueDropPx)
                        return true;
                }
            }

            if (!IsPixelPositionReviveAccessible(hero, x, y))
                return true;

            return false;
        }

        private bool TryGetBestDownedRescuePoint(Hero hero, out double x, out double y, out string source)
        {
            // v6.3.6: Prefer the last actually visited teleporter/fast-travel point.
            // Trap floors such as spikes can look like valid ground, so the most recent
            // generic safe-ground sample is not always safe enough for revive placement.
            if (TryGetLastVisitedTeleporterRevivePoint(hero, out x, out y))
            {
                source = "last_teleporter";
                return true;
            }

            // If there is no teleporter yet, use an older safe-ground history point instead
            // of the latest frame. This avoids saving the exact spike/trap tile during the
            // death frame and moving the downed body back into the hazard.
            if (TryGetAgedLocalReviveSafePosition(hero, out x, out y))
            {
                source = "aged_safe_position";
                return true;
            }

            if (TryGetNearestRemotePlayerPixel(hero, out x, out y))
            {
                y -= LocalReviveBodyYOffsetPx;
                source = "teammate_position";
                return true;
            }

            if (TryGetSafeReviveAnchor(hero, out x, out y))
            {
                source = "current_ground";
                return true;
            }

            source = string.Empty;
            x = 0;
            y = 0;
            return false;
        }

        public void RememberLocalReviveTeleporterPosition(double x, double y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y))
                return;

            var hero = me;
            if (hero == null)
                return;

            try
            {
                if (hero._level == null || hero._level.destroyed)
                    return;
            }
            catch
            {
                return;
            }

            // Put the revive body slightly above the teleporter platform so it does not sink
            // into the teleporter entity or nearby hazard floor.
            _hasLocalReviveTeleporterPosition = true;
            _localReviveTeleporterX = x;
            _localReviveTeleporterY = y - LocalReviveBodyYOffsetPx;
            _localReviveTeleporterLevelId = GetCurrentLevelId();
            _localReviveTeleporterTicks = Stopwatch.GetTimestamp();
            RecordLocalReviveSafeHistory(_localReviveTeleporterX, _localReviveTeleporterY, _localReviveTeleporterLevelId, _localReviveTeleporterTicks);
        }

        private void RecordLocalReviveSafeHistory(double x, double y, string levelId, long ticks)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || ticks <= 0)
                return;

            if (_localReviveSafeHistory.Count > 0)
            {
                var last = _localReviveSafeHistory[_localReviveSafeHistory.Count - 1];
                var dx = last.X - x;
                var dy = last.Y - y;
                if (dx * dx + dy * dy < 48.0 * 48.0 &&
                    ticks - last.Ticks < (long)(Stopwatch.Frequency * 0.5))
                {
                    return;
                }
            }

            _localReviveSafeHistory.Add(new ReviveSafeAnchor(x, y, levelId, ticks));
            if (_localReviveSafeHistory.Count > ReviveSafeHistoryMaxEntries)
                _localReviveSafeHistory.RemoveRange(0, _localReviveSafeHistory.Count - ReviveSafeHistoryMaxEntries);
        }

        private bool TryGetLastVisitedTeleporterRevivePoint(Hero hero, out double x, out double y)
        {
            x = 0;
            y = 0;
            if (hero == null || !_hasLocalReviveTeleporterPosition || _localReviveTeleporterTicks == 0)
                return false;

            var now = Stopwatch.GetTimestamp();
            if (now - _localReviveTeleporterTicks > (long)(Stopwatch.Frequency * ReviveTeleporterMaxAgeSeconds))
                return false;
            if (!IsAnchorLevelCurrent(_localReviveTeleporterLevelId))
                return false;
            if (!IsPixelPositionReviveAccessible(hero, _localReviveTeleporterX, _localReviveTeleporterY))
                return false;

            x = _localReviveTeleporterX;
            y = _localReviveTeleporterY;
            return true;
        }

        private bool TryGetAgedLocalReviveSafePosition(Hero hero, out double x, out double y)
        {
            x = 0;
            y = 0;
            if (hero == null)
                return false;

            var now = Stopwatch.GetTimestamp();
            var minAge = (long)(Stopwatch.Frequency * ReviveSafeHistoryMinAgeSeconds);
            var maxAge = (long)(Stopwatch.Frequency * ReviveSafeHistoryMaxAgeSeconds);

            for (var i = _localReviveSafeHistory.Count - 1; i >= 0; i--)
            {
                var anchor = _localReviveSafeHistory[i];
                var age = now - anchor.Ticks;
                if (age < minAge)
                    continue;
                if (age > maxAge)
                    break;
                if (!IsAnchorLevelCurrent(anchor.LevelId))
                    continue;
                if (!IsPixelPositionReviveAccessible(hero, anchor.X, anchor.Y))
                    continue;

                x = anchor.X;
                y = anchor.Y;
                return true;
            }

            // Backwards-compatible fallback for old sessions that have no history yet.
            // Only use it if it is not from the immediate death frame.
            if (_hasLocalReviveSafePosition && IsLocalReviveSafePositionFresh() && IsLocalReviveSafePositionForCurrentLevel())
            {
                var age = now - _localReviveSafeTicks;
                if (age >= minAge && IsPixelPositionReviveAccessible(hero, _localReviveSafeX, _localReviveSafeY))
                {
                    x = _localReviveSafeX;
                    y = _localReviveSafeY;
                    return true;
                }
            }

            return false;
        }

        private bool IsAnchorLevelCurrent(string levelId)
        {
            var current = GetCurrentLevelId();
            if (string.IsNullOrWhiteSpace(levelId) || string.IsNullOrWhiteSpace(current))
                return true;
            return string.Equals(levelId, current, StringComparison.Ordinal);
        }

        private bool IsLocalReviveSafePositionFresh()
        {
            if (!_hasLocalReviveSafePosition || _localReviveSafeTicks == 0)
                return false;
            var ageTicks = Stopwatch.GetTimestamp() - _localReviveSafeTicks;
            return ageTicks >= 0 && ageTicks <= (long)(Stopwatch.Frequency * ReviveSafePositionMaxAgeSeconds);
        }

        private bool IsLocalReviveSafePositionForCurrentLevel()
        {
            var current = GetCurrentLevelId();
            if (string.IsNullOrWhiteSpace(_localReviveSafeLevelId) || string.IsNullOrWhiteSpace(current))
                return true;
            return string.Equals(_localReviveSafeLevelId, current, StringComparison.Ordinal);
        }

        private bool TryGetSafeReviveAnchor(Hero hero, out double x, out double y)
        {
            x = 0;
            y = 0;
            if (hero == null)
                return false;

            try
            {
                if (hero.destroyed || hero._level == null || hero._level.destroyed || hero.spr == null)
                    return false;
                if (!double.IsFinite(hero.spr.x) || !double.IsFinite(hero.spr.y))
                    return false;
                if (!IsPixelPositionReviveAccessible(hero, hero.spr.x, hero.spr.y))
                    return false;

                x = hero.spr.x;
                y = hero.spr.y;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool IsPixelPositionReviveAccessible(Hero hero, double pixelX, double pixelY)
        {
            if (hero == null || !double.IsFinite(pixelX) || !double.IsFinite(pixelY))
                return false;

            try
            {
                var level = hero._level;
                var map = level?.map;
                if (level == null || level.destroyed || map == null)
                    return false;

                var tx = pixelX / 24.0;
                var ty = pixelY / 24.0;
                var cx = (int)System.Math.Floor(tx);
                var cy = (int)System.Math.Floor(ty);
                var xr = tx - cx;
                var yr = ty - cy;
                if (!double.IsFinite(xr) || !double.IsFinite(yr))
                    return false;

                var probeXr = xr;
                var probeYr = yr;
                var groundYr = map.getGroundYr(cx, cy, Ref<double>.From(ref probeXr), Ref<double>.From(ref probeYr));
                if (!double.IsFinite(groundYr))
                    return false;

                // getGroundYr returns the closest floor fractional Y for this cell. If the body is
                // many cells below the found ground, it is probably falling into void/out-of-bounds.
                if (yr > groundYr + 2.25)
                    return false;

                // Spike/trap floors can still look like valid ground to getGroundYr. Reject obvious
                // nearby hazard entities so the downed body does not get anchored back into the trap
                // that killed the player.
                if (IsPixelNearLikelyReviveHazard(hero, pixelX, pixelY))
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool IsPixelNearLikelyReviveHazard(Hero hero, double pixelX, double pixelY)
        {
            if (hero == null || !double.IsFinite(pixelX) || !double.IsFinite(pixelY))
                return true;

            try
            {
                var level = hero._level;
                var elements = level?.listCurrentQuadElements;
                if (level == null || level.destroyed || elements == null)
                    return false;

                const double hazardRadiusPx = 72.0;
                const double hazardRadiusSq = hazardRadiusPx * hazardRadiusPx;

                for (var i = 0; i < elements.length; i++)
                {
                    object? raw;
                    try { raw = elements.getDyn(i); } catch { continue; }
                    if (raw is not dc.Entity entity)
                        continue;

                    if (!IsLikelyReviveHazardEntity(entity))
                        continue;

                    if (!TryGetEntityPixelPosition(entity, out var ex, out var ey))
                        continue;

                    var dx = ex - pixelX;
                    var dy = ey - pixelY;
                    if (dx * dx + dy * dy <= hazardRadiusSq)
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool IsLikelyReviveHazardEntity(dc.Entity entity)
        {
            if (entity == null)
                return false;

            string name;
            try { name = entity.GetType().Name ?? string.Empty; } catch { name = string.Empty; }
            if (string.IsNullOrWhiteSpace(name))
                return false;

            return name.IndexOf("Spike", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Trap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Saw", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Blade", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Lava", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Acid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Poison", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Crusher", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Thorn", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryGetEntityPixelPosition(dc.Entity entity, out double x, out double y)
        {
            x = 0;
            y = 0;
            if (entity == null)
                return false;

            try
            {
                var spr = entity.spr;
                if (spr != null && double.IsFinite(spr.x) && double.IsFinite(spr.y))
                {
                    x = spr.x;
                    y = spr.y;
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                x = (entity.cx + entity.xr) * 24.0;
                y = (entity.cy + entity.yr) * 24.0;
                return double.IsFinite(x) && double.IsFinite(y);
            }
            catch
            {
                return false;
            }
        }

        private bool TryGetNearestRemotePlayerPixel(Hero hero, out double x, out double y)
        {
            x = 0;
            y = 0;
            if (hero == null)
                return false;

            var bestDist = double.MaxValue;
            var found = false;
            var localX = hero.spr?.x ?? 0;
            var localY = hero.spr?.y ?? 0;
            var localLevelId = GetCurrentLevelId();

            for (int i = 0; i < clients.Length; i++)
            {
                var client = clients[i];
                if (client == null)
                    continue;

                try
                {
                    if (client.destroyed || client.spr == null)
                        continue;
                }
                catch
                {
                    continue;
                }

                var remoteId = clientIds[i];
                if (remoteId > 0 && _remoteDowned.ContainsKey(remoteId))
                    continue;

                double rx;
                double ry;
                try
                {
                    rx = client.spr.x;
                    ry = client.spr.y;
                }
                catch
                {
                    continue;
                }

                if (!double.IsFinite(rx) || !double.IsFinite(ry))
                    continue;
                if (!string.IsNullOrWhiteSpace(localLevelId) && client._level?.map?.id != null)
                {
                    var remoteLevel = client._level.map.id.ToString();
                    if (!string.IsNullOrWhiteSpace(remoteLevel) && !string.Equals(remoteLevel, localLevelId, StringComparison.Ordinal))
                        continue;
                }

                var dx = rx - localX;
                var dy = ry - localY;
                var dist = dx * dx + dy * dy;
                if (dist >= bestDist)
                    continue;

                bestDist = dist;
                x = rx;
                y = ry;
                found = true;
            }

            return found;
        }

        private bool TryUpdateDownedPositionFromCorpse(double corpseX, double corpseY)
        {
            // v6.4.7: never let a corpse/body position update become authoritative for revive.
            // Dead Cells can still produce or move a death body/anchor even when the mod tries to
            // hide/disable it. Accepting those coordinates caused the downed marker to fall through
            // floors, bounce between spikes and teleporters, and repeatedly trigger cell/blueprint
            // drops. The authoritative position is now only _localHeldX/_localHeldY.
            if (!_localFakeDead)
                return false;

            if (me != null)
            {
                if (_hasLocalDownedAnchor)
                {
                    _localDownedX = _localDownedAnchorX;
                    _localDownedY = _localDownedAnchorY;
                    _localHeldX = _localDownedAnchorX;
                    _localHeldY = _localDownedAnchorY;
                    _downedSafeRescueLockX = _localDownedAnchorX;
                    _downedSafeRescueLockY = _localDownedAnchorY;
                    _downedSafeRescueLockUntilTicks = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * DownedPermanentAnchorRefreshSeconds);
                }
                else
                {
                    RescueLocalDownedPositionIfUnsafe(me, "corpse_position_ignored", force: true);
                }

                ReassertLocalDownedAnchor(me);
                _nextDownedStateSendTicks = 0;
            }

            return true;
        }

        private static void EnsureHeroVisibilityAfterRoomChange(Hero? hero)
        {
            if (hero == null)
                return;

            try
            {
                if (ModEntry.IsLocalPlayerDowned())
                    return;
            }
            catch
            {
            }

            try { hero.visible = true; } catch { }
            try
            {
                var head = hero.heroHead;
                if (head == null)
                    return;

                try { head.customHeadSpr?.set_visible(true); } catch { }
                try { head.customBackSpr?.set_visible(true); } catch { }
                try { head.headNormalSb?.set_visible(true); } catch { }
                try { head.headAddSb?.set_visible(true); } catch { }
                try { head.eye?.set_visible(true); } catch { }
            }
            catch
            {
            }
        }

        internal static void ResetDownedPlayersForRestart()
        {
            var instance = Instance;
            if (instance == null)
                return;

            try
            {
                // Broadcast the revived (not-downed) state on restart so the peer clears its stale
                // remote-downed tracking; otherwise the host keeps pinning this player as a corpse
                // and gates interactions (e.g. exit doors) as if still downed.
                instance.ResetFakeDeathState(unlockLocalHero: true, sendNetworkUpState: true);
            }
            catch
            {
            }
        }
    }
}
