using HaxeProxy.Runtime;
using dc.en;
using dc.ui;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using ModCore.Utilities;

namespace DeadCellsMultiplayerMod
{
    public sealed class RemoteDownedCorpse : dc.GameCinematic
    {
        private readonly Hero _templateHero;
        private readonly GhostKing _ghost;
        private readonly dc.GameCinematic? _previousCine;
        private HeroDeadCorpse? _corpse;
        private Homunculus? _homunculus;
        private bool _hadGhostVisibleState;
        private bool _ghostWasVisible;
        private bool _hadGhostGravityState;
        private bool _ghostHadGravity;
        private bool _hadTemplateHeroVisibleState;
        private bool _templateHeroWasVisible;
        private bool _hadTemplateHeroHeadBlackState;
        private int _templateHeroHeadBlackValue;
        private bool _cineSuppressed;
        private bool _lethalFallStarted;
        private bool _hasTarget;
        private double _targetX;
        private double _targetY;
        private int _targetDir;
        private double _headTargetX;
        private double _headTargetY;
        private bool _hasHeadAnimTarget;
        private string? _headAnimTarget;
        private string? _interactionLabelText;
        private LightTip? _interactionLightTip;
        private Pointer? _corpsePointer;
        private const int CorpseMarkerColor = 0xED6a1F;
        private const int PointerFxSuppressionKey = 188743680;
        private const double HomunculusIdleYSnapTolerancePx = 2.0;

        public RemoteDownedCorpse(Hero templateHero, GhostKing ghost, double x, double y, int dir, dc.GameCinematic? previousCine)
        {
            _templateHero = templateHero;
            _ghost = ghost;
            _previousCine = previousCine;

            cancellable = false;
            SuppressCineEffects();
            CaptureGhostVisibility();
            CaptureGhostRuntime();
            CaptureTemplateHeroVisibility();
            HideGhost();
            CreateCorpse();
            EnsureTemplateHeroVisible();
            UpdateTarget(x, y, dir);
            EnsureViewportTracksTemplateHero(immediate: true);
        }

        public void UpdateTarget(double x, double y, int dir, double? headX = null, double? headY = null, string? headAnim = null)
        {
            var normalizedDir = ResolveTargetDir(dir);
            var changed = !_hasTarget ||
                          Math.Abs(_targetX - x) > 0.001 ||
                          Math.Abs(_targetY - y) > 0.001 ||
                          _targetDir != normalizedDir;

            _targetX = x;
            _targetY = y;
            _targetDir = normalizedDir;
            _hasTarget = true;
            if (headX.HasValue && headY.HasValue)
            {
                _headTargetX = headX.Value;
                _headTargetY = headY.Value;
            }
            else
            {
                _headTargetX = x;
                _headTargetY = y;
            }
            if (!string.IsNullOrWhiteSpace(headAnim))
            {
                _headAnimTarget = headAnim.Trim();
                _hasHeadAnimTarget = true;
            }
            else
            {
                _headAnimTarget = null;
                _hasHeadAnimTarget = false;
            }

            if (changed)
                ApplyTargetToCorpse(forceStartFall: true);
            ApplyTargetToHomunculus();
        }

        public void SetInteractionLabel(string? text)
        {
            var normalized = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            if (string.Equals(_interactionLabelText, normalized, StringComparison.Ordinal))
                return;

            _interactionLabelText = normalized;
            ApplyInteractionLabel();
        }

        private int ResolveTargetDir(int dir)
        {
            if (dir > 0)
                return 1;
            if (dir < 0)
                return -1;

            if (_hasTarget && _targetDir != 0)
                return _targetDir;

            try
            {
                if (_ghost != null && !_ghost.destroyed)
                    return _ghost.dir < 0 ? -1 : 1;
            }
            catch
            {
            }

            return 1;
        }

        public override void update()
        {
            base.update();
            SuppressCineEffects();

            if (_templateHero == null || _templateHero.destroyed || _ghost == null || _ghost.destroyed)
            {
                destroy();
                return;
            }

            HideGhost();
            EnsureCorpse();
            EnsureHomunculus();
            EnsureTemplateHeroVisible();
            EnsureViewportTracksTemplateHero(immediate: false);
        }

        public override void onDispose()
        {
            base.onDispose();
            RestoreCineState();
            DisposeCorpse();
            DisposeHomunculus();
            RestoreGhostRuntime();
            RestoreGhostVisibility();
            RestoreTemplateHeroVisibility();
            EnsureViewportTracksTemplateHero(immediate: true);
        }

        private void EnsureCorpse()
        {
            // v6.4.5: remote downed marker is ghost-only. Never create or simulate a
            // HeroDeadCorpse because it can trigger Dead Cells drop/out-of-game logic.
            DisposeCorpse();
            ApplyTargetToCorpse(forceStartFall: false);
        }

        private void CreateCorpse()
        {
            // v6.4.5: disabled physical remote corpse creation to prevent cells/blueprints
            // from being created by spectator-side downed visuals.
            DisposeCorpse();
            DisposeHomunculus();
            ApplyTargetToCorpse(forceStartFall: false);
            EnsureTemplateHeroVisible();
        }

        private HeroDeadCorpse? CreateCorpseWithoutDrops()
        {
            // v6.4.5: physical corpses are disabled in multiplayer revive visuals.
            return null;
        }

        private bool IsSafeToCreateCorpse()
        {
            var hero = _templateHero;
            if (hero == null || hero.destroyed)
                return false;

            try
            {
                var level = hero._level;
                if (level == null || level.destroyed)
                    return false;
                if (level.game == null || level.game.destroyed)
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        private void ApplyTargetToCorpse(bool forceStartFall)
        {
            if (!_hasTarget || _ghost == null)
                return;

            try { _ghost.visible = true; } catch { }
            try { _ghost._targetable = false; } catch { }
            try { _ghost.hasGravity = false; } catch { }
            try { _ghost.cancelVelocities(); } catch { }
            try { _ghost.dx = 0; } catch { }
            try { _ghost.dy = 0; } catch { }
            try { _ghost.bdx = 0; } catch { }
            try { _ghost.bdy = 0; } catch { }
            try { _ghost.dir = _targetDir; } catch { }
            try { _ghost.setPosPixel(_targetX, _targetY - 40.0); } catch { }
        }

        private static void SafeSnapCorpse(HeroDeadCorpse corpse, double x, double y)
        {
            try { corpse.setPosPixel(x, y); } catch { }

            // Prevent network snaps from placing corpse slightly below ground tiles.
            try
            {
                var map = corpse._level?.map;
                if (map == null)
                    return;

                var cx = corpse.cx;
                var cy = corpse.cy;
                var xr = corpse.xr;
                var yr = corpse.yr;
                var groundYr = map.getGroundYr(cx, cy, Ref<double>.From(ref xr), Ref<double>.From(ref yr));
                if (double.IsFinite(groundYr) && corpse.yr > groundYr)
                    corpse.setPosCase(cx, cy, xr, groundYr);
            }
            catch
            {
            }
        }

        private static bool IsCorpseStabilized(HeroDeadCorpse corpse)
        {
            try
            {
                var group = corpse.spr?.groupName?.ToString();
                if (!string.IsNullOrEmpty(group) &&
                    group.IndexOf("lethalSlam", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private void EnsureLethalFallStarted()
        {
            var corpse = _corpse;
            if (corpse == null || corpse.destroyed || _lethalFallStarted)
                return;

            _lethalFallStarted = true;
            try { corpse.startLethalFall(); } catch { }
        }

        private void EnsureHomunculus()
        {
            // Remote downed visual is corpse-only.
            DisposeHomunculus();
        }

        private void CreateHomunculus(HeroDeadCorpse corpse)
        {
            _homunculus = null;
        }

        private void ApplyTargetToHomunculus()
        {
            // No-op: head entity is disabled.
        }

        private static bool IsIdleLikeHomunculusAnim(string? anim)
        {
            if (string.IsNullOrWhiteSpace(anim))
                return false;

            return anim.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void DisableRemoteHomunculusController(Homunculus hom)
        {
            if (hom == null)
                return;

            try
            {
                var heroCtrl = hom._level?.game?.hero?.controller;
                var ctrl = hom.controller;
                if (ctrl != null && !ReferenceEquals(ctrl, heroCtrl))
                    ctrl.manualLock = true;
            }
            catch
            {
            }

            try
            {
                var game = hom._level?.game;
                var hero = game?.hero;
                if (hero != null && game != null)
                {
                    try { hero.controller.manualLock = false; } catch { }
                    try { game.curCine = null; } catch { }
                    try { hom._level?.viewport?.track(hero, null); } catch { }
                }
            }
            catch
            {
            }
        }

        private static void RemoveFromHomunculusSkillEntityList(Homunculus hom)
        {
            if (hom == null)
                return;

            try
            {
                var bucketObj = hom._level?.entitiesByClass?.get(17969);
                if (bucketObj is dc.hl.types.ArrayObj bucket)
                    bucket.remove(hom);
            }
            catch
            {
            }
        }

        private static void KeepCorpseActive(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed)
                return;

            var wasOutOfGame = false;
            try { wasOutOfGame = corpse.isOutOfGame; } catch { }

            try { corpse.isOnScreen = true; } catch { }
            try
            {
                if (corpse.onScreenRecent < 1200.0)
                    corpse.onScreenRecent = 1200.0;
            }
            catch { }

            try { corpse.lastOutOfGame = false; } catch { }
            try { corpse.isOutOfGame = false; } catch { }

            if (!wasOutOfGame)
                return;

            try { corpse.onOutOfGameChange(); } catch { }
        }

        private void SuppressCineEffects()
        {
            RestoreCineState();

            if (_cineSuppressed)
                return;

            try { disableBars(); } catch { }
            try { bars = 0.0; } catch { }

            try
            {
                var top = topBar;
                if (top != null)
                    top.set_visible(false);
            }
            catch
            {
            }

            try
            {
                var bottom = bottomBar;
                if (bottom != null)
                    bottom.set_visible(false);
            }
            catch
            {
            }

            try { dc.pr.Game.Class.ME?.hud?.show(null); } catch { }
            _cineSuppressed = true;
        }

        private void RestoreCineState()
        {
            var game = dc.pr.Game.Class.ME;
            if (game == null)
                return;

            try
            {
                if (_previousCine != null && !_previousCine.destroyed)
                    game.curCine = _previousCine;
                else if (ReferenceEquals(game.curCine, this))
                    game.curCine = null;
            }
            catch
            {
            }

        }

        private void HideGhost()
        {
            // v6.4.5: do not hide the remote player while downed. The ghost itself is now the
            // safe revive marker, because a physical HeroDeadCorpse can duplicate drops.
            try { _ghost.visible = true; } catch { }
            try { _ghost._targetable = false; } catch { }
            try { _ghost.hasGravity = false; } catch { }
            try { _ghost.cancelVelocities(); } catch { }
            try { _ghost.dx = 0; } catch { }
            try { _ghost.dy = 0; } catch { }
            try { _ghost.bdx = 0; } catch { }
            try { _ghost.bdy = 0; } catch { }
            if (_hasTarget)
            {
                try { _ghost.setPosPixel(_targetX, _targetY - 40.0); } catch { }
            }
        }

        private void EnsureCorpsePointer()
        {
            // v6.4.5: no corpse pointer when physical corpse is disabled.
            ClearCorpsePointer();
        }

        private void ClearCorpsePointer()
        {
            if (_corpsePointer == null)
                return;

            try
            {
                if (!_corpsePointer.destroyed)
                    _corpsePointer.destroy();
            }
            catch
            {
            }
            finally
            {
                _corpsePointer = null;
            }
        }

        private void ApplyInteractionLabel()
        {
            var corpse = _corpse;
            if (corpse == null || corpse.destroyed)
                return;

            ClearInteractionLightTip();

            try
            {
                if (string.IsNullOrEmpty(_interactionLabelText))
                    return;

                var tip = corpse.createLightTip(null);
                if (tip == null)
                    return;

                tip.addActivate(_interactionLabelText.AsHaxeString(), null, null);
                _interactionLightTip = tip;
            }
            catch
            {
                _interactionLightTip = null;
            }
        }

        private void ClearInteractionLightTip()
        {
            _interactionLightTip = null;

            var corpse = _corpse;
            if (corpse == null || corpse.destroyed)
                return;

            try { corpse.removeLightTip(); } catch { }
        }

        private void EnsureViewportTracksTemplateHero(bool immediate)
        {
            var hero = _templateHero;
            if (hero == null || hero.destroyed)
                return;

            try
            {
                var viewport = hero._level?.viewport;
                if (viewport == null)
                    return;

                if (!ReferenceEquals(viewport.tracked, hero))
                    viewport.track(hero, immediate);
            }
            catch
            {
            }
        }

        private void CaptureGhostVisibility()
        {
            if (_hadGhostVisibleState)
                return;

            try { _ghostWasVisible = _ghost.visible; }
            catch { _ghostWasVisible = true; }
            _hadGhostVisibleState = true;
        }

        private void CaptureGhostRuntime()
        {
            if (_hadGhostGravityState || _ghost == null)
                return;

            try { _ghostHadGravity = _ghost.hasGravity; }
            catch { _ghostHadGravity = true; }
            _hadGhostGravityState = true;
        }

        private void RestoreGhostRuntime()
        {
            if (_ghost == null || _ghost.destroyed)
                return;

            try { _ghost.hasGravity = _hadGhostGravityState ? _ghostHadGravity : true; } catch { }
            try { _ghost.cancelVelocities(); } catch { }
            _hadGhostGravityState = false;
            _ghostHadGravity = true;
        }

        private void CaptureTemplateHeroVisibility()
        {
            if (_hadTemplateHeroVisibleState || _templateHero == null)
                return;

            try { _templateHeroWasVisible = _templateHero.visible; }
            catch { _templateHeroWasVisible = true; }
            _hadTemplateHeroVisibleState = true;

            try
            {
                var head = _templateHero.heroHead;
                if (head != null)
                {
                    _templateHeroHeadBlackValue = head.headBlack;
                    _hadTemplateHeroHeadBlackState = true;
                }
            }
            catch
            {
            }
        }

        private void EnsureTemplateHeroVisible()
        {
            if (_templateHero == null || _templateHero.destroyed)
                return;
            if (ModEntry.IsLocalPlayerDowned())
                return;

            try { _templateHero.visible = true; } catch { }
            SetTemplateHeroHeadVisible(true);
        }

        private void RestoreTemplateHeroVisibility()
        {
            if (_templateHero == null || _templateHero.destroyed || !_hadTemplateHeroVisibleState)
                return;
            if (ModEntry.IsLocalPlayerDowned())
                return;

            try { _templateHero.visible = _templateHeroWasVisible; } catch { }
            SetTemplateHeroHeadVisible(_templateHeroWasVisible);
        }

        private void SetTemplateHeroHeadVisible(bool visible)
        {
            var hero = _templateHero;
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
                if (visible && _hadTemplateHeroHeadBlackState)
                {
                    try { head.headBlack = _templateHeroHeadBlackValue; } catch { }
                }
                else
                {
                    try { head.headBlack = 0; } catch { }
                }
                try { head.eye?.set_visible(visible); } catch { }
            }
            catch
            {
            }
        }

        private void RestoreGhostVisibility()
        {
            if (!_hadGhostVisibleState || _ghost == null || _ghost.destroyed)
                return;

            try { _ghost.visible = _ghostWasVisible; } catch { }
        }

        private void DisposeCorpse()
        {
            ClearInteractionLightTip();
            ClearCorpsePointer();

            var corpse = _corpse;
            _corpse = null;
            _lethalFallStarted = false;
            if (corpse == null)
                return;

            try
            {
                if (!corpse.destroyed)
                    corpse.destroy();
            }
            catch
            {
            }
        }

        private void DisposeHomunculus()
        {
            var hom = _homunculus;
            _homunculus = null;
            _hasHeadAnimTarget = false;
            _headAnimTarget = null;
            if (hom == null)
                return;

            try
            {
                if (!hom.destroyed)
                    hom.destroy();
            }
            catch
            {
            }

            try { hom.dispose(); } catch { }
        }
    }
}
