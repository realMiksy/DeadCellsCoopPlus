using dc;
using dc.en;
using dc.h2d;
using dc.hl.types;
using dc.tool;
using dc.ui.hud;
using DeadCellsMultiplayerMod;
using HaxeProxy.Runtime;
using ModCore.Events.Interfaces.Game.Hero;
using Serilog;
using System.Collections.Generic;
using System.Linq; // 如果需要使用LINQ，但这里暂时不需要

namespace MobsSynchronization
{
    public class MobsSynchronization
    {
        private ModEntry modEntry;
        private MultiplayerUI uI;
        private static List<Mob> trackedMobs = new List<Mob>();
        private int frameCount = 0;

        public MobsSynchronization(ModEntry entry)
        {
            modEntry = entry;
            uI = new MultiplayerUI(entry);
        }

        public void HookInitialize()
        {
            Hook__MMTracker.__constructor__ += Hook__MMTracker_TRACKER;
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


    }
}