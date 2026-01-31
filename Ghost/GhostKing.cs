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
using Hashlink.Virtuals;
using ModCore.Storage;
using ModCore.Utitities;
using dc.spine.support.utils;
using DeadCellsMultiplayerMod.Ghost;

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
            initScarf();
        }


        public void initScarf()
        {
            var remoteSkin = RemoteSkinId ?? ModEntry.Instance?.remoteSkin;
            if(remoteSkin == null) remoteSkin = "PrisonerDefault";
            var item = Cdb.Class.getSkinInfo(remoteSkin.AsHaxeString()).item;
            var scarf = ScarfManager.Class.create(this, item);
            
            scarf.owner = this;
            this.scarf = scarf;
            // Weapon weapon;
            // weapon.Class.create();
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

            // glow
            // ArrayObj glowData = CdbTypeConverter.Class.getGlowData(Cdb.Class.getSkinInfo(remoteSkin.AsHaxeString()));
            // if (glowData != null)
            // {
            //     GlowKey s2 = new GlowKey(glowData);
            //     if (s2 != null)
            //     {
            //         this.spr.addShader(s2);
            //     }
            // }

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

    }
}
