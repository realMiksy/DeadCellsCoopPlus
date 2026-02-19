using System;
using System.Reflection;
using dc;
using dc.en;
using dc.haxe.ds;
using dc.h3d.mat;
using dc.hl.types;
using dc.hxd;
using dc.libs.heaps.slib;
using dc.pow;
using dc.pr;
using dc.shader;
using dc.tool;
using dc.tool._AnimationTrack;
using Hashlink.Virtuals;
using ModCore.Storage;
using ModCore.Utilities;
using dc.spine.support.utils;
using DeadCellsMultiplayerMod.Ghost;
using HaxeProxy.Runtime;

namespace DeadCellsMultiplayerMod.Ghost.GhostBase
{
    public class GhostKing : KingSkin, IHxbitSerializable<object>
    {
        public StringMap? animationTracks;

        public Inventory inventory;
        public HeroHead head;
        public string? RemoteSkinId;
        public string? RemoteHeadSkinId;
        public KingWeaponsManager kingWeaponsManager = null!;

        ScarfManager scarf;

        public GhostKing() : base(null, 0, 0)
        {
        }

        public GhostKing(Level lvl, int x, int y) : base(lvl, x, y)
        {
        }
        object IHxbitSerializable<object>.GetData()
        {
            return new();
        }

        void IHxbitSerializable<object>.SetData(object data)
        {
        }


        public override void init()
        {
            this.inventory = ModEntry.me.inventory.clone();
            kingWeaponsManager = new KingWeaponsManager(ModEntry.me, this);
            kingWeaponsManager.init();
            base.init();
        }


        public void initScarf()
        {
            var remoteSkin = RemoteSkinId ?? ModEntry.Instance?.remoteSkin;
            if(remoteSkin == null) remoteSkin = "PrisonerDefault";
            var skinInfo = Cdb.Class.getSkinInfo(remoteSkin.AsHaxeString());
            if(skinInfo == null) return;

            if(scarf != null)
                scarf.dispose();

            var item = skinInfo.item;
            var newScarf = ScarfManager.Class.create(this, item);
            newScarf.owner = this;
            scarf = newScarf;
        }

        public void DisposeScarf()
        {
            if (scarf == null)
                return;

            try { scarf.dispose(); } catch { }
            scarf = null;
        }

        public override void disposeGfx()
        {
            DisposeScarf();
            base.disposeGfx();
        }

        public override void dispose()
        {
            DisposeScarf();
            base.dispose();
        }


        public override void initGfx()
        {
            base.initGfx();
            var remoteSkin = RemoteSkinId ?? ModEntry.Instance?.remoteSkin;
            if (remoteSkin == null) remoteSkin = "PrisonerDefault";
            virtual_colorMap_consoleCmdId_glowData_group_head_incompatibleHeads_item_model_onlyDefaultHead_scarfBlendMode_scarfs_ skinInfo =
                Cdb.Class.getSkinInfo(remoteSkin.AsHaxeString());
            animationTracks = ResolveAnimationTracks(skinInfo);
            dc.String group = "idle".AsHaxeString();
            SpriteLib heroLib = Assets.Class.getHeroLib(skinInfo);
            Texture normalMapFromGroup = heroLib.getNormalMapFromGroup(group);
            int? dp_ROOM_MAIN_HERO = Const.Class.DP_ROOM_MAIN_HERO;
            this.initSprite(heroLib, group, 0.5, 0.5, dp_ROOM_MAIN_HERO, true, null, normalMapFromGroup);
            this.initColorMap(skinInfo);

            ArrayObj glowData = CdbTypeConverter.Class.getGlowData(Cdb.Class.getSkinInfo(remoteSkin.AsHaxeString()));
            if (glowData != null && glowData.length > 0)
            {
                GlowKey glowKey = (GlowKey)this.spr.getShader(GlowKey.Class);
                if (glowKey == null)
                {
                    glowKey = new GlowKey(null);
                    this.spr.addShader(glowKey);
                }
                glowKey.setGlowDatas(glowData);
                
            }


            // Ambient light
            var General = 1.0;
            var radiusCase = 1.2 * General;
            var Math = dc.Math.Class.random() * 0.20000000000000007;
            General = 0.9 + Math;
            var decayStart = 5.0 * General;
            this.createLight(1161471, radiusCase, decayStart, 0.35);


            // Scarf
            initScarf();


        }

        public void ApplyRemoteSkin(string? skin)
        {
            var cleaned = string.IsNullOrWhiteSpace(skin)
                ? "PrisonerDefault"
                : skin.Replace("|", "/").Trim();
            if (string.Equals(RemoteSkinId, cleaned, StringComparison.Ordinal))
                return;

            RemoteSkinId = cleaned;
            if (this.spr != null)
            {
                this.disposeGfx();
                this.initGfx();
            }
        }

        private static StringMap? ResolveAnimationTracks(
            virtual_colorMap_consoleCmdId_glowData_group_head_incompatibleHeads_item_model_onlyDefaultHead_scarfBlendMode_scarfs_ skinInfo)
        {
            if (skinInfo == null)
            {
                return null;
            }

            dc._String _String = dc.String.Class;
            dc.String path = "atlas/".AsHaxeString();
            path = _String.__add__(_String.__add__(path, skinInfo.model), "_tracks.json".AsHaxeString());
            if (!Res.Class.get_loader().exists(path))
            {
                return null;
            }

            return Assets.Class.getAnimationTracks(Res.Class.load(path));
        }

        public override void onActivate(Hero by, bool longPress)
        {
            base.onActivate(by, longPress);
        }

        public override void fixedUpdate()
        {
            base.fixedUpdate();
            scarf?.push(0.0, Ref<bool>.Null);
        }

        public override void postUpdate()
        {
            base.postUpdate();
            scarf?.postUpdate();
        }

        public override double get_headX()
        {
            if (life <= 0 || destroyed || spr == null || spr.frameData == null || spr.pivot == null || animationTracks == null)
            {
                return base.get_headX();
            }

            var headBone = ResolveHeadSkeleton();
            if (headBone == null)
            {
                return base.get_headX();
            }

            double baseX = spr.x - spr.frameData.realWid * spr.pivot.centerFactorX * dir;
            double x = baseX + AnimationTrack_Impl_.Class.x(headBone, spr.frame) * dir;
            return x == 0.0 ? base.get_headX() : x;
        }

        public override double get_headY()
        {
            if (life <= 0 || destroyed || spr == null || spr.frameData == null || spr.pivot == null || animationTracks == null)
            {
                return base.get_headY();
            }

            var headBone = ResolveHeadSkeleton();
            if (headBone == null)
            {
                return base.get_headY();
            }

            double baseY = spr.y - spr.frameData.realHei * spr.pivot.centerFactorY;
            double y = baseY + AnimationTrack_Impl_.Class.y(headBone, spr.frame);
            return y == 0.0 ? base.get_headY() : y;
        }

        private ArrayBytes_Int? ResolveHeadSkeleton()
        {
            var tracks = animationTracks;
            var groupName = spr?.groupName;
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

    }
}
