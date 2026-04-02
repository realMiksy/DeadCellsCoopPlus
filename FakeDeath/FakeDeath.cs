using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
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

                orig(self, a);

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

            UpdateReviveHintsByProximity();
            ProcessReviveHold(net);
        }

        private void ConsumeRemoteDownedStates(NetNode net)
        {
            if (!net.TryConsumePlayerDownStates(out var states))
                return;

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

            if (net.TryGetRemoteUserSnapshots(out var users))
            {
                for (int i = 0; i < users.Count; i++)
                {
                    var user = users[i];
                    if (user.Id != userId)
                        continue;

                    if (!string.IsNullOrWhiteSpace(user.Username))
                        return user.Username.Trim();
                    break;
                }
            }

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
                if (net.TryConsumePlayerReviveRequests(out _))
                {
                    // Intentionally ignored: local player is alive, revive requests target other players.
                }
                return;
            }

            if (!net.TryConsumePlayerReviveRequests(out var requests))
                return;

            var localId = net.id;
            for (int i = 0; i < requests.Count; i++)
            {
                var req = requests[i];
                if (req.TargetId != localId)
                    continue;

                if (_localDeadCine == null || !_localDeadCine.IsHomunculusNearCorpse(ReviveHomunculusBodyMaxDistancePx))
                    continue;

                ReviveLocalPlayer(net);
                return;
            }
        }

        private void PruneRemoteDownedStates(NetNode net)
        {
            if (_remoteDowned.Count == 0)
                return;

            var activeIds = new HashSet<int>();
            var localId = net.id;
            if (localId > 0)
                activeIds.Add(localId);

            if (net.TryGetRemoteUserSnapshots(out var users))
            {
                for (int i = 0; i < users.Count; i++)
                {
                    var id = users[i].Id;
                    if (id > 0)
                        activeIds.Add(id);
                }
            }

            for (int i = 0; i < clientIds.Length; i++)
            {
                var id = clientIds[i];
                if (id > 0)
                    activeIds.Add(id);
            }

            var stale = new List<int>();
            foreach (var pair in _remoteDowned)
            {
                if (!activeIds.Contains(pair.Key))
                    stale.Add(pair.Key);
            }

            for (int i = 0; i < stale.Count; i++)
            {
                DisposeRemoteDownedCine(stale[i]);
                _remoteDowned.Remove(stale[i]);
                _downedAnnouncements.Remove(stale[i]);
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
            var activeCorpseIds = new HashSet<int>();
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

                activeCorpseIds.Add(state.UserId);
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
                var staleCorpseIds = new List<int>();
                foreach (var pair in _remoteDownedCines)
                {
                    if (!activeCorpseIds.Contains(pair.Key))
                        staleCorpseIds.Add(pair.Key);
                }

                for (int i = 0; i < staleCorpseIds.Count; i++)
                    DisposeRemoteDownedCine(staleCorpseIds[i]);
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

            var ids = new List<int>(_remoteDownedCines.Keys);
            for (int i = 0; i < ids.Count; i++)
                DisposeRemoteDownedCine(ids[i]);
        }

        private bool HasAliveRemoteTeammate(NetNode net)
        {
            var localId = net.id;
            var activeIds = new HashSet<int>();

            if (net.TryGetRemoteUserSnapshots(out var users))
            {
                for (int i = 0; i < users.Count; i++)
                {
                    var id = users[i].Id;
                    if (id > 0 && id != localId)
                        activeIds.Add(id);
                }
            }

            for (int i = 0; i < clientIds.Length; i++)
            {
                var id = clientIds[i];
                if (id > 0 && id != localId)
                    activeIds.Add(id);
            }

            if (activeIds.Count == 0)
            {
                if (_remoteDowned.Count > 0)
                    return false;

                if (net.IsHost)
                    return NetNode.ConnectedClientCount > 0;
                return net.IsAlive;
            }

            var localLevelId = GetCurrentLevelId();
            foreach (var id in activeIds)
            {
                if (!_remoteDowned.TryGetValue(id, out var downed))
                    return true;

                // If teammate is tracked as downed on another level, treat them as alive.
                if (!string.Equals(localLevelId, downed.LevelId, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private void EnterLocalFakeDeath(Hero hero, NetNode net)
        {
            if (hero == null)
                return;

            ResetAllDownedGameOverState();
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
            SnapHeroToDownedPosition(hero, _localDownedX, _localDownedY, clampToGround: false);
            StartLocalDeadCine(hero);

            SendLocalDownedState(net, isDowned: true, force: true);
        }

        private void MaintainLocalFakeDeath(NetNode net)
        {
            if (!_localFakeDead || me == null)
                return;

            try
            {
                if (me.life <= 0)
                    me.life = 1;
            }
            catch
            {
            }

            if (_localDeadCine == null)
                StartLocalDeadCine(me);

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

            var cine = _localDeadCine;
            if (cine != null && cine.TryGetCorpsePixelPosition(out var corpseX, out var corpseY))
            {
                TryUpdateDownedPositionFromCorpse(corpseX, corpseY);
            }

            SnapHeroToDownedPosition(me, _localHeldX, _localHeldY, clampToGround: false);
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

            try { hero.cancelVelocities(); } catch { }
            try { hero.cancelSkillControlLock(); } catch { }
            try { hero.unlockControls(); } catch { }
            try { hero._targetable = true; } catch { }

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

            SendLocalDownedState(net, isDowned: false, force: true);
        }

        private void ApplyLocalDownedExitPenaltyIfNeededCore()
        {
            if (!_localFakeDead || _localExitPenaltyApplied || me == null)
                return;

            _localExitPenaltyApplied = true;
            var hero = me;

            try { hero.spdComboKills = 0; } catch { }
            try { hero.perfectKillsCount = 0; } catch { }
            try { hero.goldCombo = 0; } catch { }

            try
            {
                var data = hero._level?.game?.data;
                if (data != null)
                {
                    data.killCount = 0;
                    data.corruptedHealingKillCount = 0;
                }
            }
            catch
            {
            }

            try
            {
                bool noStats = true;
                hero.tryToSubstractMoney(int.MaxValue, Ref<bool>.From(ref noStats));
            }
            catch
            {
                try
                {
                    var data = hero._level?.game?.data;
                    if (data != null)
                        data.money = 0;
                    hero.hudSetMoney(0);
                }
                catch
                {
                }
            }

            try
            {
                var inventory = hero.inventory;
                if (inventory != null)
                {
                    inventory.removeAll("BrutalityUp".AsHaxeString());
                    inventory.removeAll("SurvivalUp".AsHaxeString());
                    inventory.removeAll("TacticUp".AsHaxeString());
                }
            }
            catch
            {
            }

            try { hero.computeTiers(); } catch { }

            try
            {
                var data = hero._level?.game?.data;
                if (data != null)
                {
                    data.money = 0;
                    data.brutalityTier = hero.brutalityTier;
                    data.survivalTier = hero.survivalTier;
                    data.tacticTier = hero.tacticTier;
                }
            }
            catch
            {
            }
        }

        private void ProcessReviveHold(NetNode net)
        {
            if (me == null || _remoteDowned.Count == 0)
            {
                ResetReviveHold();
                ClearReviveHints();
                return;
            }

            bool isHoldPressed;
            try { isHoldPressed = dc.hxd.Key.Class.isDown(ReviveInteractKey); }
            catch { isHoldPressed = false; }

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

            net.SendPlayerReviveRequest(nearest.UserId);
            _nextReviveAttemptTicks = now + (long)(Stopwatch.Frequency * ReviveAttemptCooldownSeconds);
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

                if (state.HasHeadPosition)
                {
                    var hdx = state.HeadX - state.X;
                    var hdy = state.HeadY - state.Y;
                    var headBodyDistSq = hdx * hdx + hdy * hdy;
                    if (headBodyDistSq > ReviveHomunculusBodyMaxDistancePx * ReviveHomunculusBodyMaxDistancePx)
                        continue;
                }

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
            if (isDowned && _localDeadCine != null && _localDeadCine.TryGetHomunculusPixelPosition(out var hx, out var hy))
            {
                headX = hx;
                headY = hy;
                _localDeadCine.TryGetHomunculusAnim(out headAnim);
            }

            var now = Stopwatch.GetTimestamp();
            var resend = (long)(Stopwatch.Frequency * DownedStateResendSeconds);
            if (isDowned && headX.HasValue && headY.HasValue)
            {
                var fastResend = (long)(Stopwatch.Frequency * DownedHeadStateResendSeconds);
                if (fastResend > 0 && (resend <= 0 || fastResend < resend))
                    resend = fastResend;
            }
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
            if (hero == null)
                return;

            if (_localDeadCine != null)
                return;

            try
            {
                _localDeadCine = new DeadBase(hero, ModEntry.GetPrimaryClient());
            }
            catch
            {
                _localDeadCine = null;
            }
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
            _hasLocalDownedAnchor = false;
            _localDownedAnchorX = 0;
            _localDownedAnchorY = 0;
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

            if (unlockLocalHero && me != null)
            {
                try { me.cancelSkillControlLock(); } catch { }
                try { me.unlockControls(); } catch { }
                try { me._targetable = true; } catch { }
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

            if (_localDeadCine == null)
                StartLocalDeadCine(me);

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

            var cine = _localDeadCine;
            if (cine != null && cine.TryGetCorpsePixelPosition(out var corpseX, out var corpseY))
            {
                TryUpdateDownedPositionFromCorpse(corpseX, corpseY);
            }
            SnapHeroToDownedPosition(me, _localHeldX, _localHeldY, clampToGround: false);
            SendLocalDownedState(net, isDowned: true, force: false);

            if (_allDownedRestartQueued || _netRole != NetRole.Host)
                return;

            if (_allDownedRestartAtTicks != 0 && now < _allDownedRestartAtTicks)
                return;

            _allDownedRestartQueued = true;
            GameMenu.QueueHostRestartFromDeath("all_players_downed");
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

        private bool TryUpdateDownedPositionFromCorpse(double corpseX, double corpseY)
        {
            if (!double.IsFinite(corpseX) || !double.IsFinite(corpseY))
                return false;

            if (!_hasLocalDownedAnchor)
            {
                _localDownedAnchorX = _localDownedX;
                _localDownedAnchorY = _localDownedY;
                _hasLocalDownedAnchor = true;
            }

            var dx = corpseX - _localDownedAnchorX;
            var dy = corpseY - _localDownedAnchorY;
            var distSq = dx * dx + dy * dy;
            if (distSq > DownedCorpseMaxDriftSq)
                return false;

            _localDownedX = corpseX;
            _localDownedY = corpseY;
            _localHeldX = _localDownedX;
            _localHeldY = _localDownedY;
            _localDownedAnchorX = corpseX;
            _localDownedAnchorY = corpseY;
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
                instance.ResetFakeDeathState(unlockLocalHero: true, sendNetworkUpState: false);
            }
            catch
            {
            }
        }
    }
}
