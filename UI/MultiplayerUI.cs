
using System;
using System.Security.Cryptography;
using System.Xml.Serialization;
using dc;
using dc.cine;
using dc.en;
using dc.en.mob;
using dc.h2d;
using dc.hl.types;
using dc.hxd;
using dc.level.@struct;
using dc.libs._Cooldown;
using dc.pow;
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
    private sealed class LifeSlot
    {
        public int SlotIndex { get; }
        public dc.ui.hud.LifeBar LifeBar { get; }
        public FlowBox Container { get; }
        public FlowBox LabelBox { get; }
        public dc.h2d.Text? LabelText { get; set; }
        public string? LastLabel { get; set; }

        public LifeSlot(int slotIndex, dc.ui.hud.LifeBar lifeBar, FlowBox container, FlowBox labelBox)
        {
            SlotIndex = slotIndex;
            LifeBar = lifeBar;
            Container = container;
            LabelBox = labelBox;
        }
    }

    private ModEntry mod { get; set; }
    private dc.h2d.Flow toplib { get; set; } = null!;
    public static dc.h2d.Flow flowContainer = null!;
    private static NetNode? _net;
    public int SlotIndex { get; set; }

    private LifeSlot?[] _slots = System.Array.Empty<LifeSlot?>();
    private bool[] _slotActive = System.Array.Empty<bool>();
    private HUD? _hud;

    private int lastLife = 0;
    private int lastMaxLife = 0;

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

        _hud = self;
        int slotCount = NetNode.MaxClientSlots;
        _slots = new LifeSlot?[slotCount];
        _slotActive = new bool[slotCount];
    }
    public bool CanUseJumpHit()
    {
        int key = Cooldown.Encode(Cooldown.Keys.JUMP_HIT);
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


            //ModEntry.me.deathRespawn();

            InventItem inventItem = new InventItem(new InventItemKind.Perk("P_Yolo".AsHaxeString()));
            ModEntry.me.applyItemPickEffect(ModEntry.me, inventItem);
            inventItem.clone(true, "P_Yolo".AsHaxeString());

            // int length = items.array.Count;
            // for (int i = 0; i < length; i++)
            // {
            //     inventItem = (InventItem?)items.array[i]!;
            // }
            // virtual_ambiantDesc_castCD_cellCost_commonProps_dlc_droppable_gameplayDesc_group_icon_id_legendAffixes_moneyCost_name_props_synergy_tags_tier1_tier2_ itemData = (virtual_ambiantDesc_castCD_cellCost_commonProps_dlc_droppable_gameplayDesc_group_icon_id_legendAffixes_moneyCost_name_props_synergy_tags_tier1_tier2_)item.byId.get(string3);
            // inventItem._itemData = itemData;
            ModEntry.me.tryToApplyYoloPerk();
            ModEntry.me.removeTemporaryItems();



        }
        if (!CanUseJumpHit())
        {
            Log.Debug("跳跃命中冷却中");
            return;
        }

    }
    private void Hook_Hero_kinglifupdate(Hook_Hero.orig_updateLifeBar orig, Hero self)
    {
        orig(self);
        KingLifeUpdate(self);
    }

    private dc.libs.Process process()
    {
        bool? titleLib = null;
        return new TitleScreen(titleLib);
    }

    public void KingLifeUpdate(Hero self)
    {
        _net = ModEntry._net;
        var net = _net;
        if (net == null) return;


        if (lastLife != self.life || lastMaxLife != self.maxLife)
        {
            net.SendHP(self.life, self.maxLife, self.life, self.bonusLife, self.radius);
            lastLife = self.life;
            lastMaxLife = self.maxLife;
        }

        if (_slots.Length == 0)
            return;

        if (!net.TryGetRemoteHpSnapshots(out var snapshots) || snapshots.Count == 0)
        {
            ClearSlots();
            return;
        }

        System.Array.Clear(_slotActive, 0, _slotActive.Length);
        var localId = net.id;
        foreach (var remote in snapshots)
        {
            if (!ModEntry.TryGetClientIndex(localId, remote.Id, out var slotIndex))
                continue;

            if (slotIndex < 0 || slotIndex >= _slots.Length)
                continue;

            var slot = _slots[slotIndex];
            if (slot == null)
            {
                var hud = _hud;
                if (hud == null)
                    continue;
                var lifeBar = new dc.ui.hud.LifeBar(new LifeBarColorMode.Normal(), null);
                slot = initkingLife(hud, slotIndex, lifeBar);
                _slots[slotIndex] = slot;
            }

            var displayName = ModEntry.GetClientLabel(slotIndex);
            if (string.IsNullOrWhiteSpace(displayName) ||
                string.Equals(displayName, "Guest", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(displayName, GameMenu.RemoteUsername, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(remote.Username))
                    displayName = remote.Username.Trim();
            }
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = "Guest";
            UpdateSlotLabel(slot, displayName);
            UpdateLifeBar(slot.LifeBar, remote.Life, remote.MaxLife, remote.Lif, remote.BonusLife, remote.Recover);
            _slotActive[slotIndex] = true;
        }

        RemoveInactiveSlots();
    }


    private LifeSlot initkingLife(HUD self, int slotIndex, dc.ui.hud.LifeBar kinglifeui)
    {
        this.toplib = self.topRightFlowT;

        var displayName = ModEntry.GetClientLabel(slotIndex);
        dc.String remoteUsername = displayName.AsHaxeString();
        double wh = remoteUsername.length + 2;
        double hh = 1.5;
        bool logo = true;

        FlowBox flowBox = FlowBox.Class.createBoxValidation(null, Ref<double>.Null, Ref<double>.Null, Ref<bool>.Null, null);
        flowBox.isVertical = false;
        flowBox.box.alpha = 0;


        flowBox.set_horizontalAlign(new FlowAlign.Middle());
        flowBox.set_verticalAlign(new FlowAlign.Middle());

        FlowBox uibox = FlowBox.Class.createBoxValidation(null, Ref<double>.From(ref wh), Ref<double>.From(ref hh), Ref<bool>.From(ref logo), null);
        dc.h2d.Text text_h2d = Assets.Class.makeText(remoteUsername, dc.ui.Text.Class.COLORS.get("WO".AsHaxeString()), false, uibox);
        text_h2d.textColor = 16766720;

        flowBox.addChild(kinglifeui);
        flowBox.addChild(uibox);

        this.toplib.addChild(flowBox);
        this.toplib.isVertical = true;
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

        kinglifeui.setSize(w, h);
        kinglifeui.get_pixelScale = self.get_pixelScale;
        kinglifeui.enableText();

        int horizontalSpacing = (int)(5 * pixelScale);

        //horizontalContainer.horizontalSpacing = horizontalSpacing;
        var slot = new LifeSlot(slotIndex, kinglifeui, flowBox, uibox)
        {
            LabelText = text_h2d,
            LastLabel = displayName
        };
        return slot;
    }

    private static void UpdateSlotLabel(LifeSlot slot, string displayName)
    {
        if (slot.LabelText != null && slot.LastLabel != displayName)
        {
            slot.LabelText.text = displayName.AsHaxeString();
            slot.LastLabel = displayName;
        }
    }

    private static void UpdateLifeBar(dc.ui.hud.LifeBar lifeBar, int max, int maxLife, int lif, int bonusLife, int recover)
    {
        lifeBar.init(max, maxLife);
        lifeBar.curState.life = (double)lif;
        lifeBar.curState.bonusLife = (double)bonusLife;
        lifeBar.curState.recover = (double)recover;
    }

    public void kingLifeUpdate(KingSkin king, dc.ui.hud.LifeBar kingLife, int max, int maxLife, int lif, int bonusLife, int recover)
    {
        UpdateLifeBar(kingLife, max, maxLife, lif, bonusLife, recover);
    }

    private void ClearSlots()
    {
        if (_slots.Length == 0)
            return;

        for (int i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[i];
            if (slot == null)
                continue;
            try
            {
                toplib?.removeChild(slot.Container);
                slot.Container.remove();
            }
            catch { }
            _slots[i] = null;
        }
    }

    private void RemoveInactiveSlots()
    {
        if (_slots.Length == 0)
            return;

        for (int i = 0; i < _slots.Length; i++)
        {
            if (_slotActive[i])
                continue;
            var slot = _slots[i];
            if (slot == null)
                continue;
            try
            {
                toplib?.removeChild(slot.Container);
                slot.Container.remove();
            }
            catch { }
            _slots[i] = null;
        }
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
