using System;
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
using ModCore.Storage;
using ModCore.Utitities;

namespace DeadCellsMultiplayerMod.KingHead
{
    public class Kinghead : HeroHead, IHxbitSerializable<object>
    {
        private Hero? me;
        private GhostKing? king;
        private Level? lvl;
        private dc.h2d.Object? headContainer;
        private dc.h2d.Object? headParticleContainer;
        private dc.h2d.Tile? headMaterial;
        private bool? useLocalSpace;

        // Parameterless ctor for serializer fallback when older saves don't carry data.
        public Kinghead()
        {
        }

        public Kinghead(Hero _me, GhostKing _kingSkin, Level level)
        {
            me = _me;
            king = _kingSkin;
            lvl = level;
        }

        object IHxbitSerializable<object>.GetData()
        {
            return new();
        }

        void IHxbitSerializable<object>.SetData(object data)
        {
        }


        public override void init(Level parent, dc.h2d.Object fromUI, Ref<bool> fromUI1)
        {
            var headSprite = king?.spr;
            if (headSprite != null)
            {
                headMaterial = headSprite.frameData?.tile;
                var useLocal = UseLocalSpace();
                if (useLocal)
                {
                    headContainer = new dc.h2d.Object(headSprite);
                    headParticleContainer = new dc.h2d.Object(headContainer);
                }
                else
                {
                    headContainer = null;
                    headParticleContainer = new dc.h2d.Object(fromUI);
                }
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
        public override void updateHeadFx(double c1)
        {
            if (king == null)
            {
                return;
            }

            double headX = king.get_headX();
            double headY = king.get_headY();
            double localHeadX = headX;
            double localHeadY = headY;
            var sprite = king.spr;
            if (sprite != null && UseLocalSpace())
            {
                localHeadX = headX - sprite.x;
                localHeadY = headY - sprite.y;
            }

            if (sprite != null && UseLocalSpace())
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

        private bool UseLocalSpace()
        {
            if (useLocalSpace.HasValue)
            {
                return useLocalSpace.Value;
            }

            var hero = me;
            var heroHead = hero?.heroHead;
            var heroSprite = hero?.spr;
            if (hero == null || heroHead == null || heroSprite == null)
            {
                useLocalSpace = true;
                return true;
            }

            var forced = heroHead.forcedPos;
            if (forced == null)
            {
                useLocalSpace = true;
                return true;
            }

            var heroHeadX = hero.get_headX();
            var heroHeadY = hero.get_headY();
            var localX = heroHeadX - heroSprite.x;
            var localY = heroHeadY - heroSprite.y;
            var distLocal = global::System.Math.Abs(forced.x - localX) + global::System.Math.Abs(forced.y - localY);
            var distWorld = global::System.Math.Abs(forced.x - heroHeadX) + global::System.Math.Abs(forced.y - heroHeadY);
            useLocalSpace = distLocal <= distWorld;
            return useLocalSpace.Value;
        }

    }


}
