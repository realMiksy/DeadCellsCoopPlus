using dc;
using dc.cine;
using dc.en;
using dc.hl._NativeArray;
using dc.libs._Cooldown;
using dc.steam.ugc;
using dc.tool;
using DeadCellsMultiplayerMod;
using Hashlink.Proxy.Objects;
using Hashlink.Reflection.Members;
using ModCore.Utitities;
using Serilog;
using Cd = CooldownHelper.Cooldown;

namespace CineHookInitialize
{
    public class CineHooks
    {
        public CineHooks()
        {
            DeadBasehooks();
        }
        public static InventItem? item;
        public void DeadBasehooks()
        {
            Hook__HeroDeath.__constructor__ += Hook__HeroDeath_init;
            Hook__HeroDeathBase.__constructor__ += Hook_HeroDeathBase_base;
            Hook__HeroDeathRespawn.__constructor__ += Hook__HeroDeathRespawn__constructor__;
            Hook__HeroDeathContinue.__constructor__ += Hook__HeroDeathContinue__constructor__;
            Hook_Hero.tryToApplyYoloPerk += Hook_Hero_tryToApplyYoloPerk;
        }

        private bool Hook_Hero_tryToApplyYoloPerk(Hook_Hero.orig_tryToApplyYoloPerk orig, Hero self)
        {
            var king = ModEntry.GetPrimaryClient();
            if (king == null)
                return orig(self);
            DeadBase deadBase = new DeadBase(self, king);
            item = new InventItem(new InventItemKind.Perk("P_Yolo".AsHaxeString()));
            
            ModEntry.me.applyItemPickEffect(ModEntry.me, item);
            bool or = orig(self);
            return or;
        }

        public static void fastchek()
        {

        }

        private void Hook__HeroDeathContinue__constructor__(Hook__HeroDeathContinue.orig___constructor__ orig, HeroDeathContinue arg1, Hero e, bool lostBody)
        {

        }


        private void Hook__HeroDeathRespawn__constructor__(Hook__HeroDeathRespawn.orig___constructor__ orig, HeroDeathRespawn e, Hero e1)
        {
            orig(e, e1);
        }

        private void Hook_HeroDeathBase_base(Hook__HeroDeathBase.orig___constructor__ orig, HeroDeathBase e, Hero lostBody, bool mob)
        {
            HeroDeathRespawn respawn = new HeroDeathRespawn(lostBody);
            HeroDeathContinue hero = new HeroDeathContinue(lostBody, false);
            FakeHeroDeath fake = new FakeHeroDeath(lostBody, null, true, null, null);



        }

        private void Hook__HeroDeath_init(Hook__HeroDeath.orig___constructor__ orig, HeroDeath e, Hero lostBody, bool e1)
        {
            orig(e, lostBody, e1);

        }
    }
}
