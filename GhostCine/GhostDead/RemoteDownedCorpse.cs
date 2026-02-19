using System;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using dc.en;
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
        private bool _hadGhostVisibleState;
        private bool _ghostWasVisible;
        private bool _cineSuppressed;
        private bool _lethalFallStarted;
        private bool _hasTarget;
        private double _targetX;
        private double _targetY;
        private int _targetDir;
        private string? _interactionLabelText;

        public RemoteDownedCorpse(Hero templateHero, GhostKing ghost, double x, double y, int dir, dc.GameCinematic? previousCine)
        {
            _templateHero = templateHero;
            _ghost = ghost;
            _previousCine = previousCine;

            cancellable = false;
            SuppressCineEffects();
            CaptureGhostVisibility();
            HideGhost();
            CreateCorpse();
            UpdateTarget(x, y, dir);
            EnsureViewportTracksTemplateHero(immediate: true);
        }

        public void UpdateTarget(double x, double y, int dir)
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

            if (changed)
                ApplyTargetToCorpse(forceStartFall: true);
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
            EnsureViewportTracksTemplateHero(immediate: false);
        }

        public override void onDispose()
        {
            base.onDispose();
            RestoreCineState();
            DisposeCorpse();
            RestoreGhostVisibility();
            EnsureViewportTracksTemplateHero(immediate: true);
        }

        private void EnsureCorpse()
        {
            var corpse = _corpse;
            if (corpse == null || corpse.destroyed)
            {
                CreateCorpse();
                return;
            }

            ApplyTargetToCorpse(forceStartFall: false);
            EnsureLethalFallStarted();
        }

        private void CreateCorpse()
        {
            DisposeCorpse();

            try
            {
                var corpse = CreateCorpseWithoutDrops();
                if (corpse == null)
                    return;

                _corpse = corpse;
                _lethalFallStarted = false;
                ApplyTargetToCorpse(forceStartFall: true);
                ApplyInteractionLabel();
            }
            catch
            {
                _corpse = null;
            }
        }

        private HeroDeadCorpse? CreateCorpseWithoutDrops()
        {
            var hero = _templateHero;
            if (hero == null)
                return null;

            var originalCells = 0;
            var capturedCells = false;
            var originalBlueprints = hero.blueprints;
            try
            {
                originalCells = hero.cells;
                capturedCells = true;
                hero.cells = 0;
                hero.blueprints = (dc.hl.types.ArrayObj)ArrayUtils.CreateDyn().array;
            }
            catch
            {
            }

            try
            {
                var corpse = new HeroDeadCorpse(this, hero);
                corpse.init();
                corpse.cells = 0;
                return corpse;
            }
            finally
            {
                try
                {
                    if (capturedCells)
                        hero.cells = originalCells;
                }
                catch
                {
                }

                try
                {
                    hero.blueprints = originalBlueprints;
                }
                catch
                {
                }
            }
        }

        private void ApplyTargetToCorpse(bool forceStartFall)
        {
            var corpse = _corpse;
            if (corpse == null || corpse.destroyed || !_hasTarget)
                return;

            try { corpse.dir = _targetDir; } catch { }
            if (!_lethalFallStarted || IsCorpseStabilized(corpse))
            {
                SafeSnapCorpse(corpse, _targetX, _targetY);
            }
            if (forceStartFall)
                EnsureLethalFallStarted();
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
            try { _ghost.visible = false; } catch { }
        }

        private void ApplyInteractionLabel()
        {
            var corpse = _corpse;
            if (corpse == null || corpse.destroyed)
                return;

            try
            {
                var textColor = 0xFFFFFF;
                if (string.IsNullOrEmpty(_interactionLabelText))
                    corpse.setGameplayLabel(null, Ref<int>.From(ref textColor));
                else
                    corpse.setGameplayLabel(_interactionLabelText.AsHaxeString(), Ref<int>.From(ref textColor));
            }
            catch
            {
            }
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

        private void RestoreGhostVisibility()
        {
            if (!_hadGhostVisibleState || _ghost == null || _ghost.destroyed)
                return;

            try { _ghost.visible = _ghostWasVisible; } catch { }
        }

        private void DisposeCorpse()
        {
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

            try { corpse.dispose(); } catch { }
        }
    }
}
