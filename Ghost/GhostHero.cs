using dc.en;
using dc.pr;
using ModCore.Utilities;
using Serilog;
using dc;
using HaxeProxy.Runtime;
using dc.shader;
using dc.hl.types;
using Hashlink.Virtuals;
using dc.libs.heaps.slib;
using System.Collections.Generic;
using dc.h2d;
using dc.ui;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using DeadCellsMultiplayerMod.MultiplayerModUI;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using dc.tool;
using DeadCellsMultiplayerMod.Tools;


namespace DeadCellsMultiplayerMod
{
    public class GhostHero
    {
        private const double NickScaleWindowed = 0.8;
        private const double NickScaleFullscreen = 0.5;

        private readonly dc.pr.Game _game;
        private readonly Hero _me;
        private static ILogger? _log;
        private readonly Dictionary<Entity, dc.h2d.Text> _labels = new();

        private string? _lastRemoteAnim;
        private int? _lastRemoteAnimQueue;
        private bool? _lastRemoteAnimG;

        private const double RestartFrameIndex = 0;

        public int PlayerId { get; }

        public GhostKing king = null!;
        private ModEntry modEntry = null!;
        private MultiplayerUI UI { get; set; } = null!;
        public KingHead.Kinghead kinghead = null!;


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


        public GhostKing CreateGhostKing(Level level, string? label = null)
        {

            king = new GhostKing(level, (int)-1000, (int)-1000);
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
            king.onActivate(_me, true);
            king.canBeActivated(_me);
            king.needsLongPress = true;
            king.hasEntityTouchChecks = true;


            bool sics = false;
            king.enableAllPhysics(Ref<bool>.From(ref sics));
            king.visible = true;
            var miniMap = ModEntry.miniMap;
            if (miniMap != null && _me._level == king._level)
            {
                miniMap.track(king, 14888237, "minimapHero".AsHaxeString(), null, true, null, null, null);
            }
            if (!string.IsNullOrWhiteSpace(label))
                SetLabel(king, label);
            this.UI = new MultiplayerUI(modEntry);
            // dynamic key = Data.Class.item.all.getDyn(278);
            // dynamic props = key.props;
            // props.prct = 0;
            king.spr._animManager.play("idle".AsHaxeString(), null, null).loop(null);
            return king;
        }

        private bool stopanim = false;
        public void disposeKing(GhostKing k)
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
            dc.h2d.Text text_h2d = _Assets.makeText(text.AsHaxeString(), dc.ui.Text.Class.COLORS.get("ST".AsHaxeString()), null, entity.spr);
            var targetScale = GetNicknameScale();
            text_h2d.y -= 80;
            text_h2d.x -= 2.5 * text.Length;
            text_h2d.font.size = 12;
            text_h2d.alpha = 0.8;
            text_h2d.scaleX = targetScale;
            text_h2d.scaleY = targetScale;
            text_h2d.textColor = 0;
            _labels[entity] = text_h2d;
        }

        public void UpdateLabels()
        {
            if (_labels.Count == 0) 
            {
                return;
            } 
            var targetScale = GetNicknameScale();
            List<Entity>? toRemove = null;
            foreach (var pair in _labels)
            {
                var entity = pair.Key;
                var label = pair.Value;
                if (entity == null || label == null || entity.spr == null || label.parent == null)
                {
                    toRemove ??= new List<Entity>();
                    if (entity != null)
                        toRemove.Add(entity);
                    continue;
                }

                var textValue = label.text?.ToString() ?? string.Empty;
                int len = textValue.Length;
                var targetX = -2.5 * len;
                var targetY = -80;
                if (entity.dir < 0)
                {
                    label.scaleX = -targetScale;
                    label.x = -targetX;
                }
                else
                {
                    label.scaleX = targetScale;
                    label.x = targetX;
                }
                label.scaleY = targetScale;
                label.y = targetY;
            }

            if (toRemove == null) return;
            for (int i = 0; i < toRemove.Count; i++)
            {
                _labels.Remove(toRemove[i]);
            }
        }

        private static double GetNicknameScale()
        {
            try
            {
                var win = dc.hxd.Window.Class.getInstance();
                if (win != null)
                {
                    var sdlWin = win.window;
                    if (sdlWin != null)
                    {
                        var displayMode = sdlWin.displayMode;
                        if (displayMode == 1 || displayMode == 2)
                            return NickScaleFullscreen;
                        if (displayMode == 0)
                            return NickScaleWindowed;
                    }

                    var mode = win.fullScreenMode;
                    if (mode == 1 || mode == 2)
                        return NickScaleFullscreen;
                    if (mode == 0)
                        return NickScaleWindowed;
                }
            }
            catch
            {
            }

            return NickScaleWindowed;
        }

    }
}
