using dc;
using dc.en;
using dc.en.mob;
using dc.h2d;
using dc.hxd;
using dc.libs._Cooldown;
using dc.pr;
using dc.tool;
using dc.ui;
using dc.ui.hud;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using HaxeProxy.Runtime;
using ModCore.Events;
using ModCore.Utilities;
using Serilog;
using Cooldown = CooldownHelper.Cooldown;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using ModCore.Events.Interfaces.Game.Hero;

namespace DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI
{

    public class MultiplayerUI :
        IEventReceiver,
        IOnAdvancedModuleInitializing,
        IOnHeroUpdate
    {
        private sealed class LifeSlot
        {
            public int SlotIndex { get; }
            public dc.ui.hud.LifeBar LifeBar { get; }
            public FlowBox Container { get; }
            public FlowBox LabelBox { get; }
            public dc.h2d.Text? LabelText { get; set; }
            public string? LastLabel { get; set; }
            public int LastLife { get; set; } = int.MinValue;
            public int LastMaxLife { get; set; } = int.MinValue;
            public int LastLif { get; set; } = int.MinValue;
            public int LastBonusLife { get; set; } = int.MinValue;
            public int LastRecover { get; set; } = int.MinValue;

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
        private NetNode? _boundNet;
        public int SlotIndex { get; set; }

        private LifeSlot?[] _slots = System.Array.Empty<LifeSlot?>();
        private bool[] _slotActive = System.Array.Empty<bool>();
        private HUD? _hud;

        private int lastLife = 0;
        private int lastMaxLife = 0;
        private int _lastHostConnectedClientCount = -1;

        private static MultiplayerUI? _instance;


        public MultiplayerUI(ModEntry Entry, int slotIndex = 0)
        {
            mod = Entry;
            SlotIndex = slotIndex;
            _instance = this;
            EventSystem.AddReceiver(this);
        }

        void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
        {

            entry.Logger.Information("\x1b[32m[[ModEntry.MultiplayerUI] Initializing MultiplayerUI...]\x1b[0m ");
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
            try
            {
                var fastCheck = ModEntry.me?.cd?.fastCheck;
                if (fastCheck == null)
                    return false;

                int key = Cooldown.Encode(Cooldown.Keys.JUMP_HIT);
                return !fastCheck.exists(key);
            }
            catch { return false; }
        }
        public void Debugkeys()
        {

            if (Key.Class.isPressed(97))//num1
            {
                //LevelTransition.Class.@goto("Custom".AsHaxeString());
                Log.Debug("KeyPress");
                ConnectionUI connectionUI = new ConnectionUI(HUD.Class.ME);

            }
            if (Key.Class.isPressed(98))//num2
            {
                var hero = ModCore.Modules.Game.Instance.HeroInstance!;
                Zombie zombie = new Zombie(hero._level, hero.cx, hero.cy, 0, 100);
                zombie.init();
                var fastCheck = ModEntry.me?.cd?.fastCheck;
                if (fastCheck != null)
                {
                    int key = Cooldown.Encode(Cooldown.Keys.JUMP_HIT);
                    fastCheck.set(key, new CdInst(key, 3.0));
                }
            }
            if (Key.Class.isPressed(99))//num3
            {
                var me = ModEntry.me;
                var fastCheck = me?.cd?.fastCheck;
                if (fastCheck != null)
                {
                    int key = Cooldown.Encode(Cooldown.Keys.JUMP_HIT);
                    fastCheck.remove(key);
                }

                //ModEntry.me.deathRespawn();

                if (me != null)
                {
                    InventItem inventItem = new InventItem(new InventItemKind.Perk("P_Yolo".AsHaxeString()));
                    me.applyItemPickEffect(me, inventItem);
                    inventItem.clone(true, "P_Yolo".AsHaxeString());

                    // int length = items.array.Count;
                    // for (int i = 0; i < length; i++)
                    // {
                    //     inventItem = (InventItem?)items.array[i]!;
                    // }
                    // virtual_ambiantDesc_castCD_cellCost_commonProps_dlc_droppable_gameplayDesc_group_icon_id_legendAffixes_moneyCost_name_props_synergy_tags_tier1_tier2_ itemData = (virtual_ambiantDesc_castCD_cellCost_commonProps_dlc_droppable_gameplayDesc_group_icon_id_legendAffixes_moneyCost_name_props_synergy_tags_tier1_tier2_)item.byId.get(string3);
                    // inventItem._itemData = itemData;
                    me.tryToApplyYoloPerk();
                    me.removeTemporaryItems();
                }
            }
            if (!CanUseJumpHit())
            {
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
            if (net == null)
            {
                if (_boundNet != null)
                {
                    _boundNet = null;
                    lastLife = int.MinValue;
                    lastMaxLife = int.MinValue;
                    _lastHostConnectedClientCount = -1;
                    ClearSlots();
                }
                return;
            }

            if (!ReferenceEquals(_boundNet, net))
            {
                _boundNet = net;
                lastLife = int.MinValue;
                lastMaxLife = int.MinValue;
                _lastHostConnectedClientCount = -1;
                ClearSlots();
            }

            if (net.IsHost)
            {
                var connectedClients = NetNode.ConnectedClientCount;
                if (connectedClients != _lastHostConnectedClientCount)
                {
                    _lastHostConnectedClientCount = connectedClients;
                    lastLife = int.MinValue;
                    lastMaxLife = int.MinValue;
                }
            }
            else
            {
                _lastHostConnectedClientCount = -1;
            }


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
                UpdateLifeBar(slot, remote.Life, remote.MaxLife, remote.Lif, remote.BonusLife, remote.Recover);
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

        private static void UpdateLifeBar(LifeSlot slot, int max, int maxLife, int lif, int bonusLife, int recover)
        {
            if (slot.LastLife == max &&
                slot.LastMaxLife == maxLife &&
                slot.LastLif == lif &&
                slot.LastBonusLife == bonusLife &&
                slot.LastRecover == recover)
            {
                return;
            }

            var lifeBar = slot.LifeBar;
            lifeBar.init(max, maxLife);
            lifeBar.curState.life = (double)lif;
            lifeBar.curState.bonusLife = (double)bonusLife;
            lifeBar.curState.recover = (double)recover;
            slot.LastLife = max;
            slot.LastMaxLife = maxLife;
            slot.LastLif = lif;
            slot.LastBonusLife = bonusLife;
            slot.LastRecover = recover;
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


        private sealed class SystemMessageEntry
        {
            public dc.h2d.Text Text = null!;
            public double LifetimeSeconds;
            public double FadeSeconds;
            public double ElapsedSeconds;
        }

        private sealed class PendingSystemMessage
        {
            public string Text = string.Empty;
            public double LifetimeSeconds;
            public double FadeSeconds;
        }

        private static readonly object SystemMsgSync = new();
        private static readonly Queue<PendingSystemMessage> PendingSystemMessages = new();
        private static readonly List<SystemMessageEntry> ActiveSystemMessages = new();

        private const int MaxSystemMessages = 8;
        private const double DefaultSystemMsgLifetimeSeconds = 10.0;
        private const double DefaultSystemMsgFadeSeconds = 2.5;
        private const double SystemMsgXOffsetPx = 20.0;
        private const double SystemMsgYOffsetPx = 250.0;
        private const double SystemMsgScale = 1.2;

        public static void PushSystemMessage(string message, double lifetimeSeconds = DefaultSystemMsgLifetimeSeconds, double fadeSeconds = DefaultSystemMsgFadeSeconds)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            var normalizedLifetime = System.Math.Max(0.25, lifetimeSeconds);
            var normalizedFade = System.Math.Max(0.15, System.Math.Min(fadeSeconds, normalizedLifetime));
            lock (SystemMsgSync)
            {
                PendingSystemMessages.Enqueue(new PendingSystemMessage
                {
                    Text = message.Trim(),
                    LifetimeSeconds = normalizedLifetime,
                    FadeSeconds = normalizedFade
                });
            }
        }

        public void DebugUI(string @string)
        {
            PushSystemMessage(@string, 5.0, 1.5);
        }

        private static bool EnsureSystemMessageFlow()
        {
            var hud = HUD.Class.ME;
            var root = hud?.root;
            if (root == null)
                return false;
            if (hud == null)
                return false;

            if (flowContainer == null || flowContainer.parent == null || !ReferenceEquals(flowContainer.parent, root))
            {
                try { flowContainer?.remove(); } catch { }
                flowContainer = new dc.h2d.Flow(root);
                flowContainer.multiline = true;
                flowContainer.isVertical = true;
                flowContainer.set_verticalAlign(new FlowAlign.Top());
                flowContainer.set_horizontalAlign(new FlowAlign.Left());
            }

            var pixelScale = hud.get_pixelScale.Invoke();
            flowContainer.x = SystemMsgXOffsetPx * pixelScale;
            flowContainer.y = SystemMsgYOffsetPx * pixelScale;
            flowContainer.alpha = 1;
            flowContainer.visible = true;
            root.addChild(flowContainer);
            return true;
        }

        private static void RemoveSystemMessageAt(int index)
        {
            if (index < 0 || index >= ActiveSystemMessages.Count)
                return;

            var entry = ActiveSystemMessages[index];
            try
            {
                flowContainer?.removeChild(entry.Text);
                entry.Text.remove();
            }
            catch
            {
            }
            ActiveSystemMessages.RemoveAt(index);
        }

        private static void EnqueueSystemMessageInternal(PendingSystemMessage pending)
        {
            if (flowContainer == null || pending == null)
                return;

            var text = Assets.Class.makeText(
                pending.Text.AsHaxeString(),
                dc.ui.Text.Class.COLORS.get("WO".AsHaxeString()),
                false,
                flowContainer);
            text.scaleX = SystemMsgScale;
            text.scaleY = SystemMsgScale;
            text.textColor = 0xFFFFFF;
            text.alpha = 1;

            ActiveSystemMessages.Add(new SystemMessageEntry
            {
                Text = text,
                LifetimeSeconds = pending.LifetimeSeconds,
                FadeSeconds = pending.FadeSeconds,
                ElapsedSeconds = 0
            });

            while (ActiveSystemMessages.Count > MaxSystemMessages)
                RemoveSystemMessageAt(0);
        }

        private void UpdateSystemMessages(double dt)
        {
            if (!EnsureSystemMessageFlow())
                return;

            lock (SystemMsgSync)
            {
                while (PendingSystemMessages.Count > 0)
                    EnqueueSystemMessageInternal(PendingSystemMessages.Dequeue());
            }

            if (ActiveSystemMessages.Count == 0)
                return;

            for (int i = ActiveSystemMessages.Count - 1; i >= 0; i--)
            {
                var msg = ActiveSystemMessages[i];
                msg.ElapsedSeconds += dt;

                var fadeStart = System.Math.Max(0.0, msg.LifetimeSeconds - msg.FadeSeconds);
                if (msg.ElapsedSeconds >= fadeStart)
                {
                    var fadeT = (msg.ElapsedSeconds - fadeStart) / System.Math.Max(0.01, msg.FadeSeconds);
                    var alpha = 1.0 - fadeT;
                    if (alpha < 0) alpha = 0;
                    msg.Text.alpha = alpha;
                }

                if (msg.ElapsedSeconds >= msg.LifetimeSeconds)
                    RemoveSystemMessageAt(i);
            }
        }

        void IOnHeroUpdate.OnHeroUpdate(double dt)
        {
            UpdateSystemMessages(dt);
            var hero = ModEntry.me;
            if (hero != null)
                KingLifeUpdate(hero);
            Debugkeys();
        }
    }
}
