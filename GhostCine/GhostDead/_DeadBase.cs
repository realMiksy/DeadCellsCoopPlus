using dc.en;
using HaxeProxy.Runtime;

namespace DeadCellsMultiplayerMod
{
    public static class _DeadBase
    {
        public static DeadBase? deadBase;
        public static Hero? GetHero;
        public static KingSkin? kingSkin;
        public static void EnterGhostDead(DeadBase @base, Hero hero, KingSkin king)
        {
            deadBase = @base;
            GetHero = hero;
            kingSkin = king;
            var cm = @base.cm;


            cm.__beginNewQueue();
            cm.__add(AHlAchtion001(), 0, null);
            cm.__add(AHlAchtion002(), 0, null);
            cm.__add(AHlAchtion003(), 0, null);
            cm.__add(AHlAchtion004(), 0, null);

        }

        private static HlAction AHlAchtion001()
        {
            HlAction hl = new HlAction(() =>
            {

            });
            return hl;
        }
        private static HlAction AHlAchtion002()
        {
            HlAction hl = new HlAction(() =>
            {

            });
            return hl;
        }
        private static HlAction AHlAchtion003()
        {
            HlAction hl = new HlAction(() =>
            {

            });
            return hl;
        }
        private static HlAction AHlAchtion004()
        {
            HlAction hl = new HlAction(() =>
            {

            });
            return hl;
        }
    }
}