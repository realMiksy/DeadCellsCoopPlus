using dc;
using dc.h2d;
using dc.hxd;
using dc.libs.heaps.slib;
using dc.pr;
using dc.shader;
using dc.ui;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using ModCore.Events;
using ModCore.Utitities;
using Serilog;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection.LightingInitializer;
using dc.hl.types;
using dc.hxd.res;
using dc.haxe.ds;
using dc.achievements;

namespace DeadCellsMultiplayerMod.MultiplayerModUI.Connection
{
    public class ConnectionUI :
    Process,
    IEventReceiver
    {
        private Flow? rootFlow;
        private UIBox? bg;
        private dc.h2d.Interactive? inter;
        private Flow? spritesflow;
        private Flow? MainTitleflow;
        private readonly List<HSprite> sprites = new();

        private static ConnectionUI? Instance;
        private HSprite? spriteui;


        public ConnectionUI(Process parent) : base(parent)
        {
            Instance = this;
            this.createRoot(parent.root);
            MainPageLightingInitializer mainPage = new MainPageLightingInitializer(this);
            this.BuildUI();
            EventSystem.AddReceiver(this);
            this.root.visible = set_visible;
        }

        public static bool set_visible
        {
            get => Instance!.root.visible;
            set => Instance!.root.visible = value;
        }


        private void BuildUI()
        {
            this.clean();

            this.rootFlow = new Flow(null);
            this.rootFlow.set_isVertical(true);
            this.rootFlow.multiline = true;
            this.rootFlow.set_verticalAlign(new FlowAlign.Middle());
            this.rootFlow.set_horizontalAlign(new FlowAlign.Right());


            base.root.addChild(this.rootFlow);
            this.onResize();
            List<(string loColor, string hiColor)> colorPairs = new List<(string, string)>
            {
                ("#FF0000", "#0000FF"),
                ("#00FF00", "#FF00FF"),
                ("#FFFF00", "#FF0000"),
                ("#00FFFF", "#FF00FF"),
                ("#FFA500", "#800080"),
                ("#FF69B4", "#4169E1"),
            };


            for (int i = 0; i < sprx.Count; i++)
            {

                loadspr(sprx[i], sprmodu[i], i);
            }

        }

        private List<double> sprx = new List<double> { 0.4, -1.0, -0.2, -0.6 };
        private List<string> animlist = new List<string>
        {
           "atkScytheB1", "runDance","wineSpit","wineRetreat"
        };
        private List<string> sprmodu = new List<string>
        {
            "Tick4","PrisonerGold","KingWhite","PrisonerDefault"
        };

        private void loadspr(double x, string sprmuld, int count)
        {
            this.spritesflow = new Flow(null);
            this.spritesflow.set_verticalAlign(new FlowAlign.Top());
            this.spritesflow.set_horizontalAlign(new FlowAlign.Middle());
            this.spritesflow.isVertical = false;



            dc.String idle = "idle".AsHaxeString();
            string skinanim = animlist[count];
            SpriteLib g = Assets.Class.getHeroLib(Cdb.Class.getSkinInfo(sprmuld.AsHaxeString()));
            this.spriteui = new HSprite(g, skinanim.AsHaxeString(), Ref<int>.Null, null);



            SpritePivot pivot = this.spriteui.pivot;
            pivot.centerFactorX = x;
            pivot.centerFactorY = 0.5;
            pivot.usingFactor = true;
            pivot.isUndefined = false;

            initColorMap(sprmuld);


            AnimManager animManager = this.spriteui.get_anim().play(skinanim.AsHaxeString(), null, null).loop(null);
            animManager.genSpeed = 0.4;

            this.spriteui.set_visible(true);
            this.spritesflow.addChild(this.spriteui);
            this.bg?.addChild(this.spritesflow);
            this.sprites.Add(this.spriteui);
        }

        private string GetRandomAnimation(List<string> values)
        {
            Random fallbackRandom = new Random();
            int fallbackIndex = fallbackRandom.Next(values.Count);
            return values[fallbackIndex];
        }


        public void playallanims(HSprite hSprite)
        {
            dynamic groups = hSprite.lib.groups;
            if (groups != null)
            {
                dynamic keysIterator = groups.keys();
                animlist.Clear();

                while (keysIterator.hasNext())
                {
                    string key = keysIterator.next().ToString();
                    if (!key.StartsWith("Atk", StringComparison.OrdinalIgnoreCase))
                    {
                        animlist.Add(key);
                    }
                }
            }
        }


        public void loadText()
        {

        }



        public void initColorMap(string colorMap)
        {
            dc.shader.ColorMap shader = (dc.shader.ColorMap)this.spriteui!.getShader(dc.shader.ColorMap.Class);
            if (shader != null)
            {
                this.spriteui.removeShader(shader);
            }

            dc.h3d.mat.Texture texture = Res.Class.load("atlas/beheaded_aladdin_s.png".AsHaxeString()).toTexture();
            dc.h3d.mat.Filter filter = new dc.h3d.mat.Filter.Nearest();
            filter = texture.set_filter(filter);

            virtual_colorMap_consoleCmdId_glowData_group_head_incompatibleHeads_item_model_onlyDefaultHead_scarfBlendMode_scarfs_ skinInfo = Cdb.Class.getSkinInfo(colorMap.AsHaxeString());
            dc.h3d.mat.Texture heroColorMap = Assets.Class.getHeroColorMap(skinInfo);
            dc.shader.ColorMap colorMapp = (ColorMap)this.spriteui.addShader(new dc.shader.ColorMap(heroColorMap));


            DirLighted s2 = new DirLighted();
            s2 = (DirLighted)this.spriteui.addShader(s2);


            dc.h3d.mat.Texture normalMapFromGroup = this.spriteui.lib.getNormalMapFromSprite(this.spriteui);
            dc.shader.NormalMap normal = new dc.shader.NormalMap(normalMapFromGroup);
            this.spriteui.addShader(normal);
        }


        private void clean()
        {
            this.bg?.remove();
            this.rootFlow?.remove();
            this.inter?.remove();
            this.sprites.Clear();
        }


        public override void onResize()
        {
            base.onResize();
            if (this.rootFlow == null || base.root == null)
                return;

            var win = dc.hxd.Window.Class.getInstance();
            double screenWidth = win.get_width();
            double screenHeight = win.get_height();


            base.root.x = 0;
            base.root.y = 0;

            this.rootFlow.set_minWidth((int)(screenWidth * 0.4)); //宽度 30%
            this.rootFlow.set_minHeight((int)(screenHeight * 0.3)); // 高度 80%
            this.rootFlow.reflow();


            double flowW = this.rootFlow.get_outerWidth();
            double flowH = this.rootFlow.get_outerHeight();


            this.bg?.remove();
            this.bg = UIBox.Class.drawBoxValidation(
                (int)flowW,
                (int)flowH,
                Ref<int>.Null,
                Ref<int>.Null,
                null,
                true
            );
            base.root.addChild(this.bg);



            double posX = screenWidth - flowW - base.get_pixelScale.Invoke() * 200.0; // 离右边 20 像素
            double posY = (screenHeight - flowH) / 1.35;
            this.rootFlow.x = posX;
            this.rootFlow.y = posY;


            this.bg.x = posX;
            this.bg.y = posY;


            this.inter?.remove();
            this.inter = new dc.h2d.Interactive(screenWidth, screenHeight, this.bg, null);
            this.inter.onClick = new HlAction<Event>(this.OnClick);

            BGtext();
        }


        private void BGtext()
        {

            this.MainTitleflow = new Flow(null);
            this.MainTitleflow.isVertical = true;
            this.MainTitleflow.set_verticalAlign(new FlowAlign.Middle());
            this.MainTitleflow.set_horizontalAlign(new FlowAlign.Middle());
            this.MainTitleflow.x = 45;
            this.bg!.addChild(this.MainTitleflow);


            dc.ui.Text title = Assets.Class.makeText(
                "DeadCells Multiplayer".AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#7effdf"),
                true,
                null
            );
            title.scaleX = 0.7;
            title.scaleY = 0.7;
            this.MainTitleflow.addChild(title);

            dc.ui.Text subtitle = Assets.Class.makeText(
                "Connection Lobby".AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#ffffff"),
                false,
                null
            );
            subtitle.scaleX = 0.45;
            subtitle.scaleY = 0.45;
            this.MainTitleflow.addChild(subtitle);


            dc.ui.Text player1 = Assets.Class.makeText(
                "Homeowner：未连接".AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#ff6b6b"),
                false,
                null
            );
            player1.scaleX = 0.4;
            player1.scaleY = 0.4;
            this.MainTitleflow.addChild(player1);


            dc.ui.Text tip = Assets.Class.makeText(
                "等待主机开启房间...".AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#bfbfbf"),
                false,
                null
            );
            tip.scaleX = 0.4;
            tip.scaleY = 0.4;
            this.MainTitleflow.addChild(tip);

            List<string> allname = _ConnectionUI.GetAllPlayerNames();
            foreach (var item in allname)
            {
                dc.ui.Text player2 = Assets.Class.makeText(
                item.AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#7effdf"),
                false,
                null
            );
                player2.scaleX = 0.4;
                player2.scaleY = 0.4;
                this.MainTitleflow.addChild(player2);
            }


        }



        public override void update()
        {
            base.update();
            if (dc.hxd.Key.Class.isPressed(80))
            {
                clean();
                Log.Debug("destory ui");


            }
        }

        public override void postUpdate()
        {
            base.postUpdate();

        }

        private void OnClick(Event e)
        {

        }


        public static void Initialize(ModEntry entry)
        {
            entry.Logger.Information("\x1b[36m[[ConnectionUI] Initializing...]\x1b[0m");
        }



    }
}
