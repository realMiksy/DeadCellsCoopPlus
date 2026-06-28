using System;
using System.Runtime.InteropServices;
using dc.en;
using dc.hl.types;
using dc.hxd;
using dc.pr;
using dc.tool;
using dc.ui;
using DeadCellsMultiplayerMod.UI;

namespace DeadCellsMultiplayerMod;

internal static partial class GameMenu
{
    private const int ReviveInteractKeyCode = 82; // R (keyboard)
    private const int ReviveEmergencyKeyCode = 118; // F7 fallback (keyboard)
    private const int ReviveControllerRightShoulderPadCode = 5; // common Xbox RB / PlayStation R1 in hxd pad order
    private const int ReviveControllerRightTriggerPadCode = 7; // older fallback for controllers that expose right shoulder as trigger
    private const double ReviveControllerLatchSeconds = 1.10;
    private static double _controllerReviveLatchUntilSeconds;
    private static readonly int[] ReviveControllerRightSidePadCandidates = { 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
    private static readonly int[] ReviveControllerFallbackPadCandidates = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23 };

    /// <summary>Hold-to-revive: dedicated R/F7 or controller action/RB input so doors, NPCs, elevators, and other interactables cannot steal the revive.</summary>
    internal static bool IsReviveHoldInputDown(Hero? hero)
    {
        if (hero == null)
            return false;

        try
        {
            if (dc.hxd.Key.Class.isDown(ReviveInteractKeyCode) || dc.hxd.Key.Class.isDown(ReviveEmergencyKeyCode))
                return true;
        }
        catch
        {
        }

#pragma warning disable CS8602
        try
        {
            Controller? controller = null;
            if (hero.controller is ControllerAccess access)
            {
                try { controller = access.parent; } catch { }
            }

            if (controller == null)
                controller = TryGetControllerFromHeroReflection(hero);

            if (controller == null)
                return false;

            // v6.4.7: revive is allowed to read controller buttons even if Dead Cells has the
            // controller briefly locked by a door/object/cinematic. Those locks were exactly why
            // RB/R1 held beside a downed player did nothing.
            var nowSeconds = GetCurrentUnixTimeSeconds();
            if (_controllerReviveLatchUntilSeconds > nowSeconds)
                return true;

            try
            {
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
                            if (IsControllerPadDownOrPressed(controller, code))
                                return LatchControllerReviveInput();
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
            }
            catch
            {
            }

            bool PadObjectHeld(object? bindingObject)
            {
                if (bindingObject == null)
                    return false;

                if (bindingObject is ArrayBytes_Int bytes)
                    return IsAnyArrayBytesPadHeld(controller, bytes);

                if (bindingObject is ArrayObj arrayObj)
                {
                    try
                    {
                        for (var i = 0; i < arrayObj.length; i++)
                        {
                            var raw = arrayObj.getDyn(i);
                            if (raw is not int code || code < 0)
                                continue;
                            if (IsControllerPadDownOrPressed(controller, code))
                                return LatchControllerReviveInput();
                        }
                    }
                    catch
                    {
                    }
                }

                return false;
            }

            if (IsControllerReviveShoulderHeld(controller))
                return true;

            var gamepadOptions = dc.Main.Class.ME?.options?.get_gamepad();
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "interact", true)))
                return true;
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "action", true)))
                return true;
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "use", true)))
                return true;
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "ui_select", true)))
                return true;
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "ui_confirm", true)))
                return true;
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "submit", true)))
                return true;
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "ui_next", true)))
                return true;
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "ui_page_next", true)))
                return true;
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "next", true)))
                return true;
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "rightShoulder", true)))
                return true;
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "right_shoulder", true)))
                return true;
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "rightBumper", true)))
                return true;
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "right_bumper", true)))
                return true;
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "rb", true)))
                return true;
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "r1", true)))
                return true;
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "rightTrigger", true)))
                return true;
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "right_trigger", true)))
                return true;
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "rt", true)))
                return true;
            if (PadObjectHeld(TitleScreenReflection.GetMemberValue(gamepadOptions, "r2", true)))
                return true;

            // Last fallback: DCCM/Hashlink builds can expose controller buttons with different
            // numeric codes. Only while the revive system is polling, scan common face/shoulder/
            // trigger pad codes so RB/R1 works even if the binding name is missing.
            if (IsAnyLikelyControllerPadHeld(controller))
                return true;
        }
        catch
        {
        }
#pragma warning restore CS8602

        return false;
    }

    private static Controller? TryGetControllerFromHeroReflection(Hero hero)
    {
        if (hero == null)
            return null;

        try
        {
            var rawController = TitleScreenReflection.GetMemberValue(hero, "controller", true);
            if (rawController is ControllerAccess access)
                return access.parent;
            if (rawController is Controller controller)
                return controller;

            var parent = TitleScreenReflection.GetMemberValue(rawController, "parent", true);
            if (parent is Controller reflectedController)
                return reflectedController;
        }
        catch
        {
        }

        return null;
    }

    private static bool IsAnyArrayBytesPadHeld(Controller controller, ArrayBytes_Int bind)
    {
        if (controller == null || bind == null)
            return false;

        try
        {
            for (var i = 0; i < bind.length; i++)
            {
                var code = Marshal.ReadInt32(bind.bytes, i << 2);
                if (code >= 0 && IsControllerPadDownOrPressed(controller, code))
                    return LatchControllerReviveInput();
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool IsAnyLikelyControllerPadHeld(Controller controller)
    {
        if (controller == null)
            return false;

        foreach (var code in ReviveControllerFallbackPadCandidates)
        {
            if (IsControllerPadDownOrPressed(controller, code))
                return LatchControllerReviveInput();
        }

        return false;
    }

    private static bool IsControllerReviveShoulderHeld(Controller controller)
    {
        if (controller == null)
            return false;

        foreach (var code in ReviveControllerRightSidePadCandidates)
        {
            if (IsControllerPadDownOrPressed(controller, code))
                return LatchControllerReviveInput();
        }

        // Keep the named constants too so future readers can see the intended mapping.
        if (IsControllerPadDownOrPressed(controller, ReviveControllerRightShoulderPadCode))
            return LatchControllerReviveInput();
        if (IsControllerPadDownOrPressed(controller, ReviveControllerRightTriggerPadCode))
            return LatchControllerReviveInput();

        return false;
    }

    private static bool IsControllerPadDownOrPressed(Controller controller, int code)
    {
        if (controller == null || code < 0)
            return false;

        // Prefer hold/down methods when the current GameProxy exposes them.
        if (TryInvokeControllerButtonMethod(controller, "padIsDown", code))
            return true;
        if (TryInvokeControllerButtonMethod(controller, "padIsHeld", code))
            return true;
        if (TryInvokeControllerButtonMethod(controller, "padDown", code))
            return true;
        if (TryInvokeControllerButtonMethod(controller, "isDown", code))
            return true;
        if (TryInvokeControllerButtonMethod(controller, "buttonIsDown", code))
            return true;
        if (TryInvokeControllerButtonMethod(controller, "buttonDown", code))
            return true;

        try
        {
            if (controller.padIsPressed(code))
                return true;
        }
        catch
        {
        }

        return false;
    }

    private static bool TryInvokeControllerButtonMethod(Controller controller, string methodName, int code)
    {
        if (controller == null || string.IsNullOrWhiteSpace(methodName))
            return false;

        try
        {
            var method = controller.GetType().GetMethod(methodName, new[] { typeof(int) });
            if (method == null)
                return false;

            var value = method.Invoke(controller, new object[] { code });
            return value is bool b && b;
        }
        catch
        {
            return false;
        }
    }

    private static bool LatchControllerReviveInput()
    {
        _controllerReviveLatchUntilSeconds = GetCurrentUnixTimeSeconds() + ReviveControllerLatchSeconds;
        return true;
    }
}
