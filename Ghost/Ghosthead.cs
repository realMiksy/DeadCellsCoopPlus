
using dc;
using dc.en;

using dc.libs.heaps;
using dc.libs.heaps.slib;
using dc.pr;

using HaxeProxy.Runtime;
using ModCore.Utitities;


namespace DeadCellsMultiplayerMod;

public class Ghosthead(Hero _me, KingSkin _kingSkin, Level level)
{
    private Hero me = _me;
    private KingSkin king = _kingSkin;
    private Level lvl = level;

    public void init()
    {
        kinghd(king);
        KingHeadTile(lvl, Const.Class.DP_ROOM_MAIN_HERO);
    }

    public ParticlePool? Pool { get; set; }
    public dc.h2d.Object? parent { get; set; }
    public HSpriteBatch? hSpriteBatch { get; set; }
    public HSprite? eye { get; set; }


    public void kinghd(KingSkin king)
    {
        SpriteLib fx2 = Assets.Class.fx;
        int db = 0;
        this.eye = new HSprite(fx2, "fxSmallStar".AsHaxeString(), new Ref<int>(ref db), null);
        eye.pivot.centerFactorX = 1;
        eye.pivot.centerFactorY = 1;
        eye.pivot.usingFactor = true;
        eye.x = king.get_headX();
        eye.y = king.get_headY();
        eye.scaleX = eye.scaleY = 2;
        eye.alpha = 1.0;
        eye.rotation = 0f;
        king.spr.addChild(this.eye);
    }


    public void KingHeadTile(dc.pr.Level level, int layer)
    {
        SpriteLib fx = Assets.Class.fx;
        int length = fx.pages.length;
        int judgment = 1;
        if (judgment < length)
        {

        }

        SpriteLib fx2 = Assets.Class.fx;

        int db = 0;
        this.eye = new HSprite(fx2, "fxSmallStar".AsHaxeString(), new Ref<int>(ref db), null);
        king.spr.addChild(this.eye);
        eye.pivot.centerFactorX = 0.5;
        eye.pivot.centerFactorY = 0.5;
        eye.pivot.usingFactor = true;
        eye.x = king.get_headX();
        eye.y = king.get_headY();
        eye.scaleX = eye.scaleY = 1.0;
        eye.alpha = 1.0;
        eye.rotation = 0f;





        return;

    }


    public void dispose()
    {
        this.hSpriteBatch = null;
        this.Pool = null;
        this.parent = null;
    }
}
