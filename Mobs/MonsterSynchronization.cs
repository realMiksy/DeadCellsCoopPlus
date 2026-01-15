using dc;
using dc.en;
using dc.h2d;
using dc.hl.types;
using dc.tool;
using dc.ui.hud;
using DeadCellsMultiplayerMod;
using ModCore.Modules;
using ModCore.Utitities;


namespace MobsSynchronization
{
    public class MobsSynchronization
    {
        private ModEntry modEntry;
        private MultiplayerUI uI;
        private static List<Mob> trackedMobs = new List<Mob>();

        public MobsSynchronization(ModEntry entry)
        {
            modEntry = entry;
            uI = new MultiplayerUI(entry);
            HookInitialize();
        }

        public void HookInitialize()
        {
            //Hook__MMTracker.__constructor__ += Hook__MMTracker_TRACKER;
            dc.en.Hook_Mob.setNemesisTarget += Hook_Mob_setNemesisTarget;
            Hook_Mob.fixedUpdate += Hook_Mob_fixedupdate;
        }

        private void Hook_Mob_fixedupdate(Hook_Mob.orig_fixedUpdate orig, Mob self)
        {
            orig(self);

            string typeName = self.GetType().ToString();
            if (typeName.Contains("dc.en.mob."))
            {
                Mob mob = self;

                try
                {
                    string mobType = mob.type?.ToString() ?? "Unknown";
                    bool isElite = mob.elite;
                    bool isHidden = mob.hidden;
                    bool isWalking = mob.isWalking();
                    bool hasSkillCharging = mob.hasSkillCharging();
                    double lifePercent = (mob.life / mob.maxLife) * 100;
                    double maxscore = self.threatList.length;
                    string? scorelist = null;
                    for (int i = 0; i < maxscore; i++)
                    {
                        scorelist = $"scorelist:{self.threatList.array[i]}";
                    }
                    Entity aTarget = mob.aTarget;
                    Entity nemesisTarget = mob.nemesisTarget;

                    string displayText = $"MobType: {mobType} | Elite: {isElite}" +
                                $"|Pos: ({mob.spr.x},{mob.spr.y})" +
                                $"|Walking: {isWalking}" +
                                $"{lifePercent}|" + scorelist;


                    if (aTarget != null)
                    {
                        string targetType = aTarget.GetType().ToString();
                        displayText += $"|Target: {targetType}";
                    }

                    if (nemesisTarget != null)
                    {
                        string nemesisType = nemesisTarget.GetType().ToString();
                        displayText += $"|Nemesis: {nemesisType}";
                    }

                    // uI.DebugUI(displayText);
                }
                catch (Exception ex)
                {
                    // uI.DebugUI($"Error getting mob info: {ex.Message}");
                }
            }

        }

        private void Hook__MMTracker_TRACKER(Hook__MMTracker.orig___constructor__ orig,
    MMTracker arg1, Entity arg2, BatchElement arg3, ArrayObj arg4,
    bool arg5, dc.h2d.Object arg6, dc.ui.hud.map.Text arg7)
        {
            orig(arg1, arg2, arg3, arg4, arg5, arg6, arg7);
            return;
            if (Std.Class.@is(arg2, Mob.Class))
            {
                string typeName = arg2.GetType().ToString();
                if (typeName.Contains("dc.en.mob."))
                {
                    Mob mob = (Mob)arg2;

                    try
                    {
                        string mobType = mob.type?.ToString() ?? "Unknown";
                        bool isElite = mob.elite;
                        bool isHidden = mob.hidden;
                        bool isWalking = mob.isWalking();
                        bool hasSkillCharging = mob.hasSkillCharging();
                        double lifePercent = (mob.life / mob.maxLife) * 100;

                        Entity aTarget = mob.aTarget;
                        Entity nemesisTarget = mob.nemesisTarget;

                        string displayText = $"MobType: {mobType} | Elite: {isElite}" +
                                    $"|Pos: ({mob.spr.x},{mob.spr.y})" +
                                    $"|Walking: {isWalking}" +
                                    $"{lifePercent}|";


                        if (aTarget != null)
                        {
                            string targetType = aTarget.GetType().ToString();
                            displayText += $"|Target: {targetType}";
                        }

                        if (nemesisTarget != null)
                        {
                            string nemesisType = nemesisTarget.GetType().ToString();
                            displayText += $"|Nemesis: {nemesisType}";
                        }

                        uI.DebugUI(displayText);
                    }
                    catch (Exception ex)
                    {
                        uI.DebugUI($"Error getting mob info: {ex.Message}");
                    }
                }
            }
        }

        private void Hook_Mob_setNemesisTarget(dc.en.Hook_Mob.orig_setNemesisTarget orig, dc.en.Mob self,
            Entity e)
        {
            if (e == Game.Instance.HeroInstance)
            {
                var team = self._team;
                var th = team.get_targetHelper();
                th.filterUntargetables();
                e = th.getBest();

                orig(self, th.getBest());
                return;
            }
            orig(self, e);
        }

    }
}