using dc;
using dc.en;
using dc.h3d.mat;
using dc.hl.types;
using dc.libs.heaps.slib;
using dc.pow;
using dc.pr;
using dc.shader;
using dc.tool;
using ModCore.Storage;
using ModCore.Utitities;

namespace DeadCellsMultiplayerMod.Ghost.GhostBase
{
    public class GhostKing : KingSkin,
    IHxbitSerializable<GhostKing.KingData>
    {
        public GhostKing(Level lvl, int x, int y) : base(lvl, x, y)
        {
        }
        private KingData kingData = new();
        private class KingData
        {
            public GhostKing king = null!;
        }


        KingData IHxbitSerializable<KingData>.GetData()
        {
            this.kingData.king = this;
            return kingData;
        }

        void IHxbitSerializable<KingData>.SetData(KingData data)
        {

        }


        public override void init()
        {
            base.init();
        }

        public override void initGfx()
        {
            base.initGfx();
            var remoteSkin = ModEntry.Instance!.remoteSkin;
            if (remoteSkin == null) remoteSkin = "PrisonerDefault";
            dc.String group = "idle".AsHaxeString();
            SpriteLib heroLib = Assets.Class.getHeroLib(Cdb.Class.getSkinInfo(remoteSkin.AsHaxeString()));
            Texture normalMapFromGroup = heroLib.getNormalMapFromGroup(group);
            int? dp_ROOM_MAIN_HERO = Const.Class.DP_ROOM_MAIN_HERO;
            this.initSprite(heroLib, group, 0.5, 0.5, dp_ROOM_MAIN_HERO, true, null, normalMapFromGroup);
            this.initColorMap(Cdb.Class.getSkinInfo(remoteSkin.AsHaxeString()));

            // glow
            ArrayObj glowData = CdbTypeConverter.Class.getGlowData(Cdb.Class.getSkinInfo(remoteSkin.AsHaxeString()));
            if (glowData != null)
            {
                GlowKey s2 = new GlowKey(glowData);
                if (s2 != null)
                {
                    this.spr.addShader(s2);
                }
            }


            // Ambient light
            var General = 1.0;
            var radiusCase = 1.2 * General;
            var Math = dc.Math.Class.random() * 0.20000000000000007;
            General = 0.9 + Math;
            var decayStart = 5.0 * General;
            this.createLight(1161471, radiusCase, decayStart, 0.35);
        }

        public override void onActivate(Hero by, bool longPress)
        {
            base.onActivate(by, longPress);
        }

    }
}