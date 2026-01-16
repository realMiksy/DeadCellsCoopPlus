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
using HaxeProxy.Runtime;
using ModCore.Utitities;

namespace DeadCellsMultiplayerMod.KingHead
{
    public class Kinghead(Hero _me, KingSkin _kingSkin, Level level) : HeroHead
    {
        private Hero me = _me;
        private KingSkin king = _kingSkin;
        private Level lvl = level;


        public override void init(Level parent, dc.h2d.Object fromUI, Ref<bool> fromUI1)
        {
            base.init(parent, fromUI, fromUI1);
        }
        public new void setForcedPos(double x, double y)
        {
            FPoint fpoint;
            if (this.forcedPos == null)
            {
                fpoint = new FPoint(x, y);
                this.forcedPos = fpoint;
                return;
            }
            fpoint = this.forcedPos;
            fpoint.x = x;
            fpoint.y = y;
        }

        public override void updateHeadFx(double c1)
        {
            base.updateHeadFx(c1);
            double num = king.spr.x - (double)king.spr.frameData.realWid * king.spr.pivot.centerFactorX;

            this.setForcedPos(king.get_headX(), king.get_headY());
            this.postUpdate();


            this.customHeadSpr.x = king.get_headX();
            this.customHeadSpr.y = king.get_headY();
            if (this.eye != null)
            {
                this.eye.x = king.get_headX();
                this.eye.y = king.get_headY();

            }
        }

    }


}