using System;
using dc.en;
using DeadCellsMultiplayerMod.Ghost.GhostBase;

namespace DeadCellsMultiplayerMod
{
    public class DeadBase : dc.GameCinematic
    {
        private readonly Hero _hero;
        private HeroDeadCorpse? _corpse;
        private bool _lethalFallStarted;
        private bool _hadHeroVisibleState;
        private bool _heroWasVisible;

        public DeadBase(Hero hero, GhostKing? king)
        {
            _DeadBase.EnterGhostDead(this, hero, king);
            _hero = hero;

            CaptureHeroVisibility();
            HideHero();
            CreateCorpse();
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

            try { _hero.cancelVelocities(); } catch { }
            try { _hero.lockControlsS(0.25); } catch { }
            try { _hero.cancelSkillControlLock(); } catch { }

            HideHero();
            EnsureCorpse();
            EnsureCorpseFalling();
            EnsureViewportTracksHero(immediate: false);
        }

        public override void onDispose()
        {
            base.onDispose();
            DisposeCorpse();
            RestoreHeroVisibility();
            EnsureViewportTracksHero(immediate: true);
        }

        private void EnsureCorpse()
        {
            var corpse = _corpse;
            if (corpse == null || corpse.destroyed)
                CreateCorpse();
        }

        private void CreateCorpse()
        {
            DisposeCorpse();

            try
            {
                var corpse = new HeroDeadCorpse(this, _hero);
                corpse.init();
                _corpse = corpse;
                _lethalFallStarted = false;
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

            EnsureLethalFallStarted();
        }

        private void EnsureLethalFallStarted()
        {
            var corpse = _corpse;
            if (corpse == null || corpse.destroyed || _lethalFallStarted)
                return;

            _lethalFallStarted = true;
            try { corpse.startLethalFall(); } catch { }
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

                try { head.parent?.set_visible(visible); } catch { }
                try { head.customHeadSpr?.set_visible(visible); } catch { }
                try { head.customBackSpr?.set_visible(visible); } catch { }
                try { head.headNormalSb?.set_visible(visible); } catch { }
                try { head.headAddSb?.set_visible(visible); } catch { }
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
    }
}
