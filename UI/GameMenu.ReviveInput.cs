using System.Runtime.InteropServices;
using dc.en;
using dc.hl.types;
using dc.hxd;
using dc.pr;
using dc.tool;
using dc.ui;

namespace DeadCellsMultiplayerMod;

internal static partial class GameMenu
{
    private const int ReviveInteractKeyCode = 82; // R (keyboard)

    /// <summary>Hold-to-revive: keyboard R plus gamepad face buttons / primary-secondary (same binding resolution as menus).</summary>
    internal static bool IsReviveHoldInputDown(Hero? hero)
    {
        if (hero == null)
            return false;

        try
        {
            if (dc.hxd.Key.Class.isDown(ReviveInteractKeyCode))
                return true;
        }
        catch
        {
        }

#pragma warning disable CS8602
        try
        {
            if (hero.controller is not ControllerAccess access)
                return false;
            if (access.manualLock)
                return false;

            var controller = access.parent;
            if (controller == null || controller.isLocked)
                return false;

            var b = controller.get_bindings();

            bool PadHeld(ArrayBytes_Int? bind)
            {
                if (bind == null)
                    return false;
                try
                {
                    for (var i = 0; i < bind.length; i++)
                    {
                        var code = Marshal.ReadInt32(bind.bytes, i << 2);
                        if (code < 0)
                            continue;
                        if (controller.padIsPressed(code))
                            return true;
                    }
                }
                catch
                {
                }

                return false;
            }

            bool KeyHeld(ArrayBytes_Int? bind)
            {
                if (bind == null)
                    return false;
                try
                {
                    for (var i = 0; i < bind.length; i++)
                    {
                        var code = Marshal.ReadInt32(bind.bytes, i << 2);
                        if (code < 0)
                            continue;
                        if (dc.hxd.Key.Class.isDown(code))
                            return true;
                    }
                }
                catch
                {
                }

                return false;
            }

            if (PadHeld(b.padA))
                return true;
            if (PadHeld(b.padB))
                return true;
            if (PadHeld(b.padC))
                return true;
            if (KeyHeld(b.primary))
                return true;
            if (KeyHeld(b.secondary))
                return true;
            if (KeyHeld(b.third))
                return true;
        }
        catch
        {
        }
#pragma warning restore CS8602

        return false;
    }
}
