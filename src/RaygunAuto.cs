using System.Reflection;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Raygun
{
    /// <summary>
    /// The Ray Gun keeps firing while the trigger is held, at its own pace (each shot's animation and the fire rate's
    /// cooldown). The game's full auto on a two-chamber gun fires both chambers together, so the gun stays on single
    /// and the trigger is pulled again for it as each shot finishes, for as long as fire is held.
    /// </summary>
    internal static class RaygunAuto
    {
        public static void Enable(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Player.FirearmController.FireOperation), nameof(Player.FirearmController.FireOperation.SwitchToIdling)),
                postfix: new HarmonyMethod(typeof(RaygunAuto).GetMethod(nameof(SwitchToIdlingPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static void SwitchToIdlingPostfix(Player.FirearmController.FireOperation __instance)
        {
            try
            {
                if (!RaygunIds.Is(__instance.Weapon) || !FireHeld() || __instance.Weapon.ChamberAmmoCount <= 0)
                    return;
                Player.FirearmController controller = __instance.Controller;
                if (controller?.CurrentOperation == null || __instance._openInventory)
                    return;
                controller.CurrentOperation.SetTriggerPressed(true);
            }
            catch (System.Exception e)
            {
                RaygunPlugin.Log.LogWarning($"Ray Gun held fire skipped: {e.Message}");
            }
        }

        // Fire is the left mouse button in play (the cursor is locked to the game view)
        private static bool FireHeld() => Input.GetMouseButton(0) && Cursor.lockState == CursorLockMode.Locked;
    }
}
