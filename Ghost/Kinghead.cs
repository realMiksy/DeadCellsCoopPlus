using System;
using dc;
using dc.en;
using dc.haxe;
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
using ModCore.Utilities;
using Serilog.Core;

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
        private ArrayBytes_Int? headSkeleton;
        private bool? useLocalSpace;
        private FPoint? kingLastHeadPos;

        Serilog.ILogger _log;

        public Kinghead()
        {
        }

        public Kinghead(Hero _me, GhostKing _kingSkin, Level level, Serilog.ILogger log)
        {
            me = _me;
            king = _kingSkin;
            lvl = level;
            _log = log;
        }

        object IHxbitSerializable<object>.GetData()
        {
            return new();
        }

        void IHxbitSerializable<object>.SetData(object data)
        {
        }

        // public new void customHeadFx()
        // {
        //     var data = this.forcedCustomHead ?? this._customHeadInfoCache;
        //     if (data?.particleEffects == null)
        //         return;

        //     var arr = data.particleEffects;
        //     for (int i = arr.length - 1; i >= 0; i--)
        //     {
        //         if (arr.getDyn(i) == null)
        //             arr.splice(i, 1);
        //     }

        //     if (arr.length == 0)
        //         return;

        //     base.customHeadFx();
        // }

        public override void init(Level parent, dc.h2d.Object fromUI, Ref<bool> fromUI1)
        {
            var headSprite = king?.spr;
            var remoteHeadSkin = ModEntry.Instance!.remoteHeadSkin;
            if (remoteHeadSkin == null) remoteHeadSkin = "BaseFlame";
            for(int i=0; i < ModEntry.customHeads.array.length; i++)
            {
                var cHead = ModEntry.customHeads.getDyn(i);
                if(cHead.item.ToString() == remoteHeadSkin)
                {
                    var data = new Hashlink.Virtuals.virtual_atlas_glowData_item_particleEffects_properties_();
                    this.customHead = true;
                    data.atlas = "customHead".AsHaxeString();

                    var glowData = ArrayUtils.CreateDyn();
                    var glowData_none = ArrayUtils.CreateDyn();
                    glowData.array.pushDyn(cHead.glowData.getDyn(0));
                    if(((ArrayObj)glowData.array).getDyn(0) == null) data.glowData = (ArrayObj)glowData_none.array;
                    else data.glowData = (ArrayObj)glowData.array;
                    
                    data.item = remoteHeadSkin.AsHaxeString();
                    var particleEffects = ArrayUtils.CreateDyn();
                    particleEffects.array.pushDyn(cHead.particleEffects.getDyn(0));
                    var particleEffects_none = ArrayUtils.CreateDyn();
                    if(((ArrayObj)particleEffects.array).getDyn(0) == null) data.particleEffects = (ArrayObj)particleEffects_none.array;
                    else data.particleEffects = (ArrayObj)particleEffects.array;
                    var properties = ArrayUtils.CreateDyn();
                    for (int b=0; b < cHead.properties.length; b++)
                    {
                        properties.array.pushDyn(cHead.properties.getDyn(b));
                    }
                    data.properties = (ArrayObj)properties.array; 
                    this.forcedCustomHead = data;
                    this._customHeadInfoCache = data;

                }
            }
            if (headSprite != null)
            {
                headMaterial = headSprite.frameData?.tile;
                headSkeleton = ResolveHeadSkeleton(headSprite);
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

            var sprite = king.spr;
            double headX;
            double headY;
            if (!TryGetHeadSkeletonPosition(sprite, out headX, out headY))
            {
                if (this.forcedPos == null)
                {
                    return;
                }

                UpdateHeadFxWithKingContext(c1);
                return;
            }

            if (sprite != null && UseLocalSpace())
            {
                this.setForcedPos(headX - sprite.x, headY - sprite.y);
            }
            else
            {
                this.setForcedPos(headX, headY);
            }
            UpdateHeadFxWithKingContext(c1);
            this.customHeadFx();
        }

        private void UpdateHeadFxWithKingContext(double c1)
        {
            var hero = me;
            var ghost = king;
            if (hero == null || ghost == null || ghost.spr == null)
            {
                return;
            }

            var savedLastHeadPos = hero.lastHeadPos;
            if (kingLastHeadPos == null)
            {
                kingLastHeadPos = new FPoint(0, 0);
            }
            hero.lastHeadPos = kingLastHeadPos;

            try
            {
                base.updateHeadFx(c1);
                this.postUpdate();
            }
            finally
            {
                kingLastHeadPos = hero.lastHeadPos;
                hero.lastHeadPos = savedLastHeadPos;
            }
        }

        private bool TryGetHeadSkeletonPosition(HSprite? sprite, out double headX, out double headY)
        {
            headX = 0;
            headY = 0;

            if (sprite == null)
            {
                return false;
            }

            headSkeleton = ResolveHeadSkeleton(sprite);
            if (headSkeleton == null)
            {
                return false;
            }

            var frameData = sprite.frameData;
            var pivot = sprite.pivot;
            if (frameData == null || pivot == null)
            {
                return false;
            }

            int dir = king?.dir ?? 1;
            int frame = sprite.frame;
            headX = sprite.x - frameData.realWid * pivot.centerFactorX;
            headX += AnimationTrack_Impl_.Class.x(headSkeleton, frame);
            headY = sprite.y - frameData.realHei * pivot.centerFactorY - 3;
            headY += AnimationTrack_Impl_.Class.y(headSkeleton, frame);
            return true;
        }

        private ArrayBytes_Int? ResolveHeadSkeleton(HSprite sprite)
        {
            var tracks = king?.animationTracks;
            var groupName = sprite.groupName;
            if (tracks == null || groupName == null)
            {
                return null;
            }

            var groupTracks = tracks.get(groupName) as StringMap;
            if (groupTracks == null)
            {
                return null;
            }

            return groupTracks.get("headBone".AsHaxeString()) as ArrayBytes_Int;
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