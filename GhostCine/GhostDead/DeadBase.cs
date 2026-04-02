using System;
using dc.en;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using HaxeProxy.Runtime;
using System.Diagnostics;

namespace DeadCellsMultiplayerMod
{
    public class DeadBase : dc.GameCinematic
    {
        private readonly Hero _hero;
        private HeroDeadCorpse? _corpse;
        private Homunculus? _homunculus;
        private bool _lethalFallStarted;
        private bool _cineSuppressed;
        private bool _hadHeroVisibleState;
        private bool _heroWasVisible;
        private bool _hadHeroHeadBlackState;
        private int _heroHeadBlackValue;
        private bool _hasBossArenaCorpseAnchor;
        private double _bossArenaCorpseAnchorX;
        private double _bossArenaCorpseAnchorY;
        private bool _bossArenaCorpsePushApplied;
        private long _bossArenaCorpsePushStartedTicks;
        private const double BossArenaCorpsePushSettleSeconds = 0.35;
        private const double BossArenaCorpsePushVelocityThreshold = 0.08;

        public DeadBase(Hero hero, GhostKing? king)
        {
            _DeadBase.EnterGhostDead(this, hero, king);
            _hero = hero;

            CaptureHeroVisibility();
            HideHero();
            CreateCorpse();
            SuppressCineEffects();
            EnsureViewportTracksHero(immediate: true);
        }

        public override void update()
        {
            base.update();

            if (_hero == null || _hero.destroyed)
            {
                destroy();
                return;
            }

            SuppressCineEffects();

            var hasLiveHomunculus = HasLiveHomunculus();

            try { _hero.cancelVelocities(); } catch { }
            if (!hasLiveHomunculus)
            {
                try { _hero.lockControlsS(0.25); } catch { }
            }
            try { _hero.cancelSkillControlLock(); } catch { }

            HideHero();
            EnsureCorpse();
            EnsureHomunculus();
            MaintainLocalHomunculusControl();
            EnsureCorpseFalling();
            EnsureViewportTracksHero(immediate: false);
        }

        public override void onDispose()
        {
            base.onDispose();
            RestoreCineState();
            DisposeCorpse();
            DisposeHomunculus();
            RestoreHeroVisibility();
            EnsureViewportTracksHero(immediate: true);
        }

        private void EnsureCorpse()
        {
            var corpse = _corpse;
            if (corpse == null || corpse.destroyed)
                CreateCorpse();
        }

        private void EnsureHomunculus()
        {
            // Fake-death flow no longer uses Homunculus.
            DisposeHomunculus();
        }

        private void CreateCorpse()
        {
            DisposeCorpse();
            DisposeHomunculus();

            try
            {
                var corpse = new HeroDeadCorpse(this, _hero);
                corpse.init();
                _corpse = corpse;
                _lethalFallStarted = false;
                _hasBossArenaCorpseAnchor = false;
                _bossArenaCorpseAnchorX = 0;
                _bossArenaCorpseAnchorY = 0;
                _bossArenaCorpsePushApplied = false;
                _bossArenaCorpsePushStartedTicks = 0;
                TrySnapCorpseToHeroAnchor(corpse);
                TryApplyBossArenaCorpsePush(corpse);
                EnsureLethalFallStarted();
            }
            catch
            {
                _corpse = null;
            }
        }

        private void EnsureCorpseFalling()
        {
            var corpse = _corpse;
            if (corpse == null || corpse.destroyed)
                return;

            KeepCorpseActive(corpse);
            KeepBossArenaCorpseAnchored(corpse);
            EnsureLethalFallStarted();
        }

        private void EnsureLethalFallStarted()
        {
            var corpse = _corpse;
            if (corpse == null || corpse.destroyed || _lethalFallStarted)
                return;

            var levelId = _hero?._level?.map?.id?.ToString();
            if (ModEntry.IsBossLevel(levelId))
            {
                TryClampCorpseToGround(corpse);
                return;
            }

            _lethalFallStarted = true;
            try { corpse.startLethalFall(); } catch { }
        }

        private void TrySnapCorpseToHeroAnchor(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed || _hero == null)
                return;

            try
            {
                var x = _hero.get_targetSprPosX();
                var y = _hero.get_targetSprPosY();
                corpse.setPosPixel(x, y);
            }
            catch
            {
                try
                {
                    if (_hero.spr != null)
                        corpse.setPosPixel(_hero.spr.x, _hero.spr.y);
                }
                catch
                {
                }
            }

            TryClampCorpseToGround(corpse);
        }

        private static void TryClampCorpseToGround(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed)
                return;

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

        private void CaptureBossArenaCorpseAnchor(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed || !ModEntry.IsBossLevel(_hero?._level?.map?.id?.ToString()))
                return;

            try
            {
                _bossArenaCorpseAnchorX = corpse.get_targetSprPosX();
                _bossArenaCorpseAnchorY = corpse.get_targetSprPosY();
                _hasBossArenaCorpseAnchor = true;
                return;
            }
            catch
            {
            }

            try
            {
                if (corpse.spr != null)
                {
                    _bossArenaCorpseAnchorX = corpse.spr.x;
                    _bossArenaCorpseAnchorY = corpse.spr.y;
                    _hasBossArenaCorpseAnchor = true;
                }
            }
            catch
            {
            }
        }

        private void KeepBossArenaCorpseAnchored(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed || !ModEntry.IsBossLevel(_hero?._level?.map?.id?.ToString()))
                return;

            if (!_bossArenaCorpsePushApplied)
                TryApplyBossArenaCorpsePush(corpse);

            TryClampCorpseToGround(corpse);

            if (!_hasBossArenaCorpseAnchor)
            {
                if (!IsBossArenaCorpsePushSettled(corpse))
                    return;

                CaptureBossArenaCorpseAnchor(corpse);
            }

            if (!_hasBossArenaCorpseAnchor)
                return;

            try { corpse.setPosPixel(_bossArenaCorpseAnchorX, _bossArenaCorpseAnchorY); } catch { }
            TryClampCorpseToGround(corpse);
            CaptureBossArenaCorpseAnchor(corpse);
        }

        private void TryApplyBossArenaCorpsePush(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed || _hero == null)
                return;
            if (_bossArenaCorpsePushApplied)
                return;
            if (!ModEntry.IsBossLevel(_hero._level?.map?.id?.ToString()))
                return;

            var dir = 1;
            try { dir = _hero.dir < 0 ? -1 : 1; } catch { }

            double pushX = dir * 0.18;
            double pushY = -0.12;
            var hasMomentum = false;
            try
            {
                var momentumX = _hero.dx + _hero.bdx;
                var momentumY = _hero.dy + _hero.bdy;
                if (double.IsFinite(momentumX) && double.IsFinite(momentumY))
                {
                    pushX = momentumX;
                    pushY = momentumY;
                    hasMomentum = true;
                }
            }
            catch
            {
            }

            if (!hasMomentum ||
                (System.Math.Abs(pushX) < 0.01 && System.Math.Abs(pushY) < 0.01))
            {
                pushX = dir * 0.18;
                pushY = -0.12;
            }
            else
            {
                if (System.Math.Abs(pushX) < 0.08)
                    pushX = dir * 0.12;
                if (pushY > -0.08)
                    pushY = -0.12;
            }

            try { corpse.hasGravity = true; } catch { }
            try { corpse.bump(pushX, pushY, null); } catch { }
            _bossArenaCorpsePushApplied = true;
            _bossArenaCorpsePushStartedTicks = Stopwatch.GetTimestamp();
            _hasBossArenaCorpseAnchor = false;
        }

        private bool IsBossArenaCorpsePushSettled(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed)
                return false;
            if (!_bossArenaCorpsePushApplied)
                return true;

            if (_bossArenaCorpsePushStartedTicks != 0 &&
                Stopwatch.GetElapsedTime(_bossArenaCorpsePushStartedTicks).TotalSeconds >= BossArenaCorpsePushSettleSeconds)
            {
                return true;
            }

            try
            {
                var totalVelocity =
                    System.Math.Abs(corpse.dx) +
                    System.Math.Abs(corpse.dy) +
                    System.Math.Abs(corpse.bdx) +
                    System.Math.Abs(corpse.bdy);
                if (totalVelocity <= BossArenaCorpsePushVelocityThreshold)
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private void CreateHomunculus(HeroDeadCorpse corpse)
        {
            _homunculus = null;
        }

        private bool HasLiveHomunculus()
        {
            return false;
        }

        private void MaintainLocalHomunculusControl()
        {
            // No-op: local fake-death should not create/control Homunculus.
        }

        private static dc.tool.mainSkills.Homunculus? GetHomunculusSkill(Hero? hero)
        {
            if (hero == null)
                return null;

            try
            {
                var manager = hero.mainSkillsManager;
                if (manager == null)
                    return null;

                return manager.getMainSkill(dc.tool.mainSkills.Homunculus.Class) as dc.tool.mainSkills.Homunculus;
            }
            catch
            {
                return null;
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

        public bool TryGetCorpsePixelPosition(out double x, out double y)
        {
            x = 0;
            y = 0;

            var corpse = _corpse;
            if (corpse == null || corpse.destroyed)
                return false;

            try
            {
                // Use physics-driven target coordinates so hero follows corpse reliably
                // even when sprite position is temporarily unavailable or delayed.
                x = corpse.get_targetSprPosX();
                y = corpse.get_targetSprPosY();
                return true;
            }
            catch
            {
            }

            var sprite = corpse.spr;
            if (sprite != null)
            {
                x = sprite.x;
                y = sprite.y;
                return true;
            }

            try
            {
                x = (corpse.cx + corpse.xr) * 24.0;
                y = (corpse.cy + corpse.yr) * 24.0;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool TryGetHomunculusPixelPosition(out double x, out double y)
        {
            // Keep network/revive logic compatible by mirroring corpse position
            // when fake-death head is disabled.
            if (TryGetCorpsePixelPosition(out x, out y))
                return true;

            x = 0;
            y = 0;
            return false;
        }

        public bool TryGetHomunculusAnim(out string? anim)
        {
            anim = null;
            return false;
        }

        public bool IsHomunculusNearCorpse(double maxDistancePx)
        {
            if (maxDistancePx <= 0)
                return false;

            if (!TryGetCorpsePixelPosition(out var corpseX, out var corpseY))
                return false;
            if (!TryGetHomunculusPixelPosition(out var headX, out var headY))
                return false;

            var dx = headX - corpseX;
            var dy = headY - corpseY;
            return dx * dx + dy * dy <= maxDistancePx * maxDistancePx;
        }

        public bool IsCorpseInLethalFall()
        {
            var corpse = _corpse;
            if (corpse == null || corpse.destroyed || !_lethalFallStarted)
                return false;

            if (IsCorpseStabilized(corpse))
                return false;

            try
            {
                var group = corpse.spr?.groupName?.ToString();
                if (!string.IsNullOrEmpty(group) &&
                    group.IndexOf("lethalFall", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            catch
            {
            }

            return true;
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

        private void HideHero()
        {
            try { _hero.visible = false; } catch { }
            SetHeroHeadVisible(false);
        }

        private void SuppressCineEffects()
        {
            if (_cineSuppressed)
            {
                TryKeepHudVisibleWhenAllowed();
                return;
            }

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

            // Dead player should keep normal HUD visible during fake-death state,
            // but do not override pause/full-map/UI-hidden states.
            TryKeepHudVisibleWhenAllowed();
            _cineSuppressed = true;
        }

        private static void TryKeepHudVisibleWhenAllowed()
        {
            try
            {
                var game = dc.pr.Game.Class.ME;
                if (game == null)
                    return;

                try
                {
                    if (game.paused)
                        return;
                }
                catch
                {
                }

                if (ShouldRespectMenuHiddenHud(game))
                    return;

                try
                {
                    var console = dc.ui.Console.Class.ME;
                    if (console != null && console.flags.exists(dc.ui.Console.Class.HIDE_UI))
                        return;
                }
                catch
                {
                }

                try
                {
                    dynamic hudDyn = game.hud;
                    if (hudDyn != null)
                    {
                        dynamic mini = hudDyn.minimap;
                        if (mini != null)
                        {
                            try
                            {
                                if ((bool)mini.isFullscreen)
                                    return;
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                catch
                {
                }

                try { game.hud?.show(null); } catch { }
            }
            catch
            {
            }
        }

        private static bool ShouldRespectMenuHiddenHud(dc.pr.Game game)
        {
            if (game == null)
                return false;

            try
            {
                if (game._pauseAfterFrames > 0)
                    return true;
            }
            catch
            {
            }

            try
            {
                var cine = game.curCine;
                if (cine != null && !cine.destroyed)
                {
                    var t = cine.GetType().Name;
                    if (!string.IsNullOrEmpty(t))
                    {
                        if (t.IndexOf("Pause", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            t.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
            }
            catch
            {
            }

            try
            {
                dynamic g = game;
                var maybeMenu = g.pauseMenu ?? g.menu ?? g.curMenu ?? g.inventoryMenu ?? g.modal;
                if (maybeMenu != null)
                {
                    try
                    {
                        if (!(bool)maybeMenu.destroyed)
                            return true;
                    }
                    catch
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private void RestoreCineState()
        {
            var game = dc.pr.Game.Class.ME;
            if (game == null)
                return;

            try
            {
                if (ReferenceEquals(game.curCine, this))
                    game.curCine = null;
            }
            catch
            {
            }
        }

        private void EnsureViewportTracksHero(bool immediate)
        {
            if (_hero == null || _hero.destroyed)
                return;

            try
            {
                var viewport = _hero._level?.viewport;
                if (viewport == null)
                    return;

                if (!ReferenceEquals(viewport.tracked, _hero))
                    viewport.track(_hero, immediate);
            }
            catch
            {
            }
        }

        private void CaptureHeroVisibility()
        {
            if (_hadHeroVisibleState)
                return;

            try { _heroWasVisible = _hero.visible; }
            catch { _heroWasVisible = true; }
            _hadHeroVisibleState = true;

            try
            {
                var head = _hero?.heroHead;
                if (head != null)
                {
                    _heroHeadBlackValue = head.headBlack;
                    _hadHeroHeadBlackState = true;
                }
            }
            catch
            {
            }
        }

        private void RestoreHeroVisibility()
        {
            if (!_hadHeroVisibleState || _hero == null)
                return;

            try { _hero.visible = _heroWasVisible; } catch { }
            SetHeroHeadVisible(_heroWasVisible);
        }

        private void SetHeroHeadVisible(bool visible)
        {
            try
            {
                var head = _hero?.heroHead;
                if (head == null)
                    return;

                try { head.customHeadSpr?.set_visible(visible); } catch { }
                try { head.customBackSpr?.set_visible(visible); } catch { }
                try { head.headNormalSb?.set_visible(visible); } catch { }
                try { head.headAddSb?.set_visible(visible); } catch { }
                if (visible && _hadHeroHeadBlackState)
                {
                    try { head.headBlack = _heroHeadBlackValue; } catch { }
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
            catch { }

            try { corpse.dispose(); } catch { }
        }

        private void DisposeHomunculus()
        {
            var hom = _homunculus;
            _homunculus = null;
            if (hom == null)
                return;

            RemoveFromHomunculusSkillEntityList(hom);
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
    }
}
