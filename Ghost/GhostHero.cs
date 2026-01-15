using dc.en;
using dc.pr;
using ModCore.Utitities;
using Serilog;
using dc;
using HaxeProxy.Runtime;
using dc.shader;
using dc.hl.types;
using Hashlink.Virtuals;
using dc.libs.heaps.slib;
using System.Collections.Generic;


namespace DeadCellsMultiplayerMod
{
    public class GhostHero
    {
        private readonly dc.pr.Game _game;
        private readonly Hero _me;
        private static ILogger? _log;
        private readonly Dictionary<Entity, dc.h2d.Text> _labels = new();

        private string? _lastRemoteAnim;
        private int? _lastRemoteAnimQueue;
        private bool? _lastRemoteAnimG;

        private const double RestartFrameIndex = 0;

        public int PlayerId { get; }

        public KingSkin king = null!;
        private ModEntry modEntry = null!;
        private MultiplayerUI UI { get; set; } = null!;


        public GhostHero(
        int playerId,
        dc.pr.Game game,
        Hero me,
        ILogger logger,
        ModEntry entry)
        {
            PlayerId = playerId;
            _game = game;
            _me = me;
            _log = logger;
            modEntry = entry;
        }


        public KingSkin CreateGhostKing(Level level, string? label = null)
        {

            king = new KingSkin(level, (int)-100, (int)-100);
            king.init();
            king.set_level(level);
            king.set_team(level.teamHero);
            king._targetable = true;
            king.hasWineGlass = false;
            king.lifeBarAbove = true;
            king.initLife(100, 100);
            king.hasRepelling = true;
            king.collisionMode = new CollisionMode.Normal();
            king.hasEntityTouchChecks = true;
            bool sics = true;
            king.enableAllPhysics(Ref<bool>.From(ref sics));
            // king.setPosCase(Game.Class.ME.hero.cx, Game.Class.ME.hero.cy, Game.Class.ME.hero.xr, Game.Class.ME.hero.yr);
            king.visible = true;
            var miniMap = ModEntry.miniMap;
            if (miniMap != null && _me._level.map == king._level.map)
            {
                miniMap.track(king, 14888237, "minimapHero".AsHaxeString(), null, true, null, null, null);
            }
            if (!string.IsNullOrWhiteSpace(label))
                SetLabel(king, label);
            this.UI = new MultiplayerUI(modEntry);
            dynamic key = Data.Class.item.all.getDyn(278);
            dynamic props = key.props;
            props.prct = 0;
            return king;
        }

        private bool stopanim = false;
        public void disposeKing(KingSkin k)
        {
            stopanim = true;
            if (k.spr != null)
            {
                ColorMap shader = (ColorMap)k.spr.getShader(ColorMap.Class);

                if (shader != null)
                {
                    k.spr.removeShader(shader);
                    k.spr.lib = null;
                }
            }

            if (k.spriteClones != null)
            {
                int num = 0;
                ArrayObj arrayObj = k.spriteClones;
                for (; ; )
                {
                    int length = arrayObj.length;
                    if (num >= length)
                    {
                        break;
                    }
                    length = arrayObj.length;
                    virtual_e_followHead_notActualClone_offX_offY_scaleBonus_? virtual_e_followHead_notActualClone_offX_offY_scaleBonus_;
                    if (num >= length)
                    {
                        virtual_e_followHead_notActualClone_offX_offY_scaleBonus_ = null;
                    }
                    else
                    {
                        virtual_e_followHead_notActualClone_offX_offY_scaleBonus_ = (virtual_e_followHead_notActualClone_offX_offY_scaleBonus_)arrayObj.array[num]!;
                    }
                    num++;
                    HSprite hsprite = virtual_e_followHead_notActualClone_offX_offY_scaleBonus_!.e;
                    if (hsprite != null)
                    {
                        if (hsprite.parent != null)
                        {
                            hsprite.parent.removeChild(hsprite);
                        }
                    }
                }
            }

            if (k.speechSfxDeck != null)
            {
                k.speechSfxDeck.clear();
            }

            if (k.runAnims != null)
            {
                k.runAnims = null;
            }
            k.removeAllLights(true);
            k.disposeGfx();
            k.destroy();
            k.dispose();

        }

        public void TeleportByPixels(double x, double y)
        {
            king?.setPosPixel(x, y - 0.2d);
        }

        public void PlayAnimation(string anim, int? queueAnim = null, bool? g = null)
        {
            if (king == null || king.spr == null || king.spr._animManager == null) return;
            if (string.IsNullOrWhiteSpace(anim)) return;
            if (stopanim == true) return;
            var animManager = king.spr._animManager;

            try
            {
                animManager.stopWithoutStateAnims(anim.AsHaxeString(), queueAnim);
                animManager.setFrame((int)RestartFrameIndex);
            }
            catch { }

            animManager.play(anim.AsHaxeString(), queueAnim, g);
        }

        public void HandleRemoteAnim(NetNode? net)
        {
            if (net == null || king == null || king.spr == null) return;

            if (net.TryGetRemoteAnim(out var anim, out var queueAnim, out var g) && !string.IsNullOrWhiteSpace(anim))
            {
                _lastRemoteAnim = anim;
                _lastRemoteAnimQueue = queueAnim;
                _lastRemoteAnimG = g;
                PlayAnimation(anim, queueAnim, g);
            }
        }

        public void SetLabel(Entity entity, string? text)
        {
            if (entity == null) return;
            if (text == null) text = "Guest";
            if (_labels.TryGetValue(entity, out var existing))
            {
                try
                {
                    if (existing.parent != null)
                        existing.parent.removeChild(existing);
                    existing.remove();
                }
                catch { }
                _labels.Remove(entity);
            }
            _Assets _Assets = Assets.Class;
            dc.h2d.Text text_h2d = _Assets.makeText(text.AsHaxeString(), dc.ui.Text.Class.COLORS.get("ST".AsHaxeString()), true, entity.spr);
            text_h2d.y -= 80;
            text_h2d.x -= 2.5 * text.Length;
            text_h2d.font.size = 18;
            text_h2d.alpha = 0.8;
            text_h2d.scaleX = 0.6d;
            text_h2d.scaleY = 0.6d;
            text_h2d.textColor = 0;
            _labels[entity] = text_h2d;
        }

    }
}
