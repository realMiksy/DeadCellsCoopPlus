
using System.Security.Cryptography;
using dc;
using dc.cine;
using dc.en;
using dc.en.mob;
using dc.h2d;
using dc.hxd;
using dc.level.@struct;
using dc.libs._Cooldown;
using dc.pr;
using dc.tool;
using dc.tool.log;
using dc.ui;
using dc.ui.hud;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using ModCore.Utitities;
using Serilog;
using Cooldown = CooldownHelper.Cooldown;

namespace DeadCellsMultiplayerMod;

public class MultiplayerUI
{
    private ModEntry mod { get; set; }
    public dc.ui.hud.LifeBar kingLife { get; set; } = null!;
    public dc.h2d.Flow toplib { get; set; } = null!;
    public static dc.h2d.Flow flowContainer = null!;
    private static NetNode? _net;
    public int SlotIndex { get; set; }
    private dc.h2d.Text? kingNameText;
    private string? lastNameText;

    private int lastLife = 0;
    private int lastMaxLife = 0;
    private int kingmaxlife = 100;

    public FlowBox box { get; set; } = null!;

    public MultiplayerUI(ModEntry Entry, int slotIndex = 0)
    {
        mod = Entry;
        SlotIndex = slotIndex;
    }

    public void init()
    {
        Hook_HUD.initHero += Hook_HUD_initking;

        Hook_Hero.updateLifeBar += Hook_Hero_kinglifupdate;
    }

    private void Hook_HUD_initking(Hook_HUD.orig_initHero orig, HUD self)
    {
        orig(self);
        initkingLife(self);
    }
    public bool CanUseJumpHit()
    {
        int key = Cooldown.Encode(Cooldown.Keys.JUMP_HIT);
        return !ModEntry.me.cd.fastCheck.exists(key);
    }

    public bool CanUseAirSkill()
    {
        int key = Cooldown.Encode(Cooldown.Keys.AIR_SKILL);
        return !ModEntry.me.cd.fastCheck.exists(key);
    }
    public void Debugkeys()
    {

        if (Key.Class.isPressed(97))//num1
        {
            LevelTransition.Class.@goto("Custom".AsHaxeString());

        }
        if (Key.Class.isPressed(98))//num2
        {
            var hero = ModCore.Modules.Game.Instance.HeroInstance!;
            Zombie zombie = new Zombie(hero._level, hero.cx, hero.cy, 0, 100);
            zombie.init();
            int key = Cooldown.Encode(Cooldown.Keys.JUMP_HIT);
            ModEntry.me.cd.fastCheck.set(key, new CdInst(key, 3.0));
        }
        if (Key.Class.isPressed(99))//num3
        {
            int key = Cooldown.Encode(Cooldown.Keys.JUMP_HIT);
            ModEntry.me.cd.fastCheck.remove(key);
        }
        if (!CanUseJumpHit())
        {
            Log.Debug("跳跃命中冷却中");
            return;
        }

    }
    private bool initlif = true;
    private void Hook_Hero_kinglifupdate(Hook_Hero.orig_updateLifeBar orig, Hero self)
    {
        orig(self);
        if (initlif) this.kingLife.init(100, 100);
        initlif = false;
        var king = ModEntry._companionKing;
        if (king == null) return;
        _net = ModEntry._net;
        var net = _net;
        if (net == null) return;


        if (lastLife != self.life || lastMaxLife != self.maxLife)
        {
            net.SendHP(self.life, self.maxLife, self.life, self.bonusLife, self.radius);
            lastLife = self.life;
            lastMaxLife = self.maxLife;
        }

        var displayName = ModEntry.GetClientLabel(SlotIndex);
        if (kingNameText != null && lastNameText != displayName)
        {
            kingNameText.text = displayName.AsHaxeString();
            lastNameText = displayName;
        }

        if (!net.TryGetRemoteHP(out int life, out int maxLife, out int lif, out int bonusLife, out int recover))
            return;

        kingLifeUpdate(king!, life, maxLife, lif, bonusLife, recover);
        this.kingmaxlife = maxLife;
        if (self.life <= 0)
        {
            life = -1;
        }
        if (this.kingLife.curState.life < 0 || self.life <= 0 || life < 0)
        {
            //self.startDeathCine();
            // GameMenu.Initialize(mod.Logger);
            // Main me = Main.Class.ME;
            // HlFunc<dc.libs.Process> pause = new HlFunc<dc.libs.Process>(this.process);
            // me.transition(null, pause, Ref<bool>.Null, null, null);
        }
    }

    private dc.libs.Process process()
    {
        bool? titleLib = null;
        return new TitleScreen(titleLib);
    }



    public void initkingLife(HUD self)
    {
        this.toplib = self.topRightFlowT;
        var displayName = ModEntry.GetClientLabel(SlotIndex);
        dc.String remoteUsername = displayName.AsHaxeString();
        double wh = remoteUsername.length + 2;
        double hh = 6;
        bool logo = true;
        FlowBox uibox = FlowBox.Class.createBoxValidation(null, Ref<double>.From(ref wh), Ref<double>.From(ref hh), Ref<bool>.From(ref logo), null);
        this.box = uibox;

        dc.h2d.Text text_h2d = Assets.Class.makeText(remoteUsername, dc.ui.Text.Class.COLORS.get("ST".AsHaxeString()), false, this.box);
        text_h2d.textColor = 16766720;
        kingNameText = text_h2d;
        lastNameText = displayName;
        this.toplib.addChild(this.box);

        dc.ui.hud.LifeBar kingLifeBar = new dc.ui.hud.LifeBar(new LifeBarColorMode.Normal(), this.toplib);
        kingLifeBar.init(100, 100);
        this.kingLife = kingLifeBar;

        this.toplib.set_verticalAlign(new FlowAlign.Top());
        this.toplib.set_horizontalAlign(new FlowAlign.Right());

        var geth = Viewport.Class.NATIVE_HEIGHT;
        var getw = Viewport.Class.NATIVE_WIDTH;

        double pixelScale = self.get_pixelScale.Invoke();

        int rightMargin = (int)(5 * pixelScale);
        int topMargin = (int)(5 * pixelScale);


        int w = (int)(100 * pixelScale);
        int h = (int)(10 * pixelScale);

        int labelHeight = (int)(hh * pixelScale);
        int labelBarGap = (int)(2 * pixelScale);
        int slotGap = (int)(6 * pixelScale);
        int slotHeight = labelHeight + labelBarGap + h + slotGap;

        int targetX = getw - w - rightMargin;
        int targetY = topMargin + (System.Math.Max(0, SlotIndex) * slotHeight);

        this.box.x = targetX;
        this.box.y = targetY;

        this.kingLife.setSize(w, h);
        this.kingLife.get_pixelScale = self.get_pixelScale;
        this.kingLife.enableText();
        this.kingLife.x = targetX;
        this.kingLife.y = targetY + labelHeight + labelBarGap;
    }

    public void kingLifeUpdate(KingSkin king, int max, int maxLife, int lif, int bonusLife, int recover)
    {
        var k = this.kingLife;
        k.init(max, maxLife);
        k.curState.life = (double)lif;
        k.curState.bonusLife = (double)bonusLife!;
        k.curState.recover = (double)recover;
    }
    private static Queue<dc.h2d.Text> textQueue = new Queue<dc.h2d.Text>();
    private const int MAX_TEXTS = 10;

    public void DebugUI(string @string)
    {
        if (flowContainer == null)
        {
            flowContainer = new dc.h2d.Flow(HUD.Class.ME.root);
            flowContainer.multiline = true;
            flowContainer.isVertical = true;
            flowContainer.set_verticalAlign(new FlowAlign.Top());
            flowContainer.set_horizontalAlign(new FlowAlign.Left());
        }

        dc.h2d.Text text_h2d = Assets.Class.makeText(@string.AsHaxeString(),
            dc.ui.Text.Class.COLORS.get("WO".AsHaxeString()),
            false, flowContainer);
        text_h2d.scaleX = 1.5;
        text_h2d.scaleY = 1.5;
        text_h2d.textColor = 16766720;

        textQueue.Enqueue(text_h2d);

        if (textQueue.Count > MAX_TEXTS)
        {
            var oldestText = textQueue.Dequeue();
            flowContainer.removeChild(oldestText);
            oldestText.remove();
        }


    }


}
