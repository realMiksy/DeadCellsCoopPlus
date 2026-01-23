using dc;
using dc.en;
using dc.haxe.ds;
using dc.hl.types;
using dc.hxd;
using dc.libs.heaps;
using dc.libs.heaps.slib;
using dc.pr;
using dc.tool;
using dc.tool._AnimationTrack;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using HaxeProxy.Runtime;
using ModCore.Utitities;

namespace DeadCellsMultiplayerMod.KingHead
{
    public class Kinghead(Hero _me, GhostKing _kingSkin, Level level) : HeroHead
    {
        private Hero me = _me;
        private GhostKing king = _kingSkin;
        private Level lvl = level;
        private dc.h2d.Object? headContainer;
        private dc.h2d.Object? headParticleContainer;
        private dc.h2d.Tile? headMaterial;


        public override void init(Level parent, dc.h2d.Object fromUI, Ref<bool> fromUI1)
        {
            var headSprite = king?.spr;
            if (headSprite != null)
            {
                headMaterial = headSprite.frameData?.tile;
                headContainer = new dc.h2d.Object(headSprite);
                headParticleContainer = new dc.h2d.Object(headContainer);
                base.init(parent, headParticleContainer, fromUI1);
                RebuildHeadParticles(headParticleContainer, headMaterial);
                this.heroHasHead = true;
                this.alwaysShowHead = true;
                this.alwaysShowEye = true;
                return;
            }

            base.init(parent, fromUI, fromUI1);
            this.heroHasHead = true;
            this.alwaysShowHead = true;
            this.alwaysShowEye = true;
        }

        private void RebuildHeadParticles(dc.h2d.Object particleParent, dc.h2d.Tile? material)
        {
            if (material == null)
            {
                return;
            }

            if (this.pool != null)
            {
                this.pool.dispose();
            }
            this.pool = new ParticlePool(material, 100, 30);

            if (this.headNormalSb != null && this.headNormalSb.parent != null)
            {
                this.headNormalSb.parent.removeChild(this.headNormalSb);
            }
            if (this.headAddSb != null && this.headAddSb.parent != null)
            {
                this.headAddSb.parent.removeChild(this.headAddSb);
            }

            this.headNormalSb = new HSpriteBatch(material, particleParent);
            this.headNormalSb.hasRotationScale = true;

            this.headAddSb = new HSpriteBatch(material, particleParent);
            this.headAddSb.hasRotationScale = true;
            this.headAddSb.blendMode = new dc.h2d.BlendMode.Add();
        }
        public new void setForcedPos(double x, double y)
        {
            FPoint fpoint;
            if (this.forcedPos == null)
            {
                fpoint = new FPoint(x, y+22);
                this.forcedPos = fpoint;
                return;
            }
            fpoint = this.forcedPos;
            fpoint.x = x;
            fpoint.y = y+22;
        }

        public override void updateHeadFx(double c1)
        {
            double headX;
            double headY;
            double localHeadX;
            double localHeadY;
            var sprite = king.spr;
            var frame = sprite?.frameData;
            if (sprite != null && frame != null)
            {
                localHeadX = frame.realWid * (0.5 - sprite.pivot.centerFactorX);
                localHeadY = -frame.realHei * sprite.pivot.centerFactorY;
                headX = sprite.x + localHeadX;
                headY = sprite.y + localHeadY;
            }
            else
            {
                headX = king.get_headX();
                headY = king.get_headY();
                localHeadX = headX;
                localHeadY = headY;
            }

            if (sprite != null && headContainer != null)
            {
                this.setForcedPos(localHeadX, localHeadY);
            }
            else
            {
                this.setForcedPos(headX, headY);
            }
            base.updateHeadFx(c1);
            this.postUpdate();
        }

    }


}
