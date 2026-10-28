using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace Raygun
{
    /// <summary>
    /// Checking the Ray Gun's ammo (either key) just shows what's left: "N charges left" across
    /// both cells, or "Empty". No animation and no sound: the break-action's chamber check, played on our clips,
    /// never finished and left the hands stuck in it.
    /// </summary>
    internal static class RaygunAmmoCheck
    {
        // Set while the game builds the check text for the Ray Gun; -1 otherwise
        private static int _charges = -1;

        public static void Enable(Harmony harmony)
        {
            foreach (string check in new[] { "CheckAmmo", "CheckChamber" })
                harmony.Patch(AccessTools.Method(typeof(Player.FirearmController), check), new HarmonyMethod(Method(nameof(CheckPrefix))));
            // The weapon window's "Fire modes" line is built in each Weapon's constructor
            foreach (ConstructorInfo ctor in typeof(Weapon).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                harmony.Patch(ctor, postfix: new HarmonyMethod(Method(nameof(WeaponCreatedPostfix))));
            harmony.Patch(AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.ChangeFireMode)),
                new HarmonyMethod(Method(nameof(ChangeFireModePrefix))));
            harmony.Patch(AccessTools.Method(typeof(AmmoCountPanel), nameof(AmmoCountPanel.ShowFireMode)),
                new HarmonyMethod(Method(nameof(FireModePrefix))));
            harmony.Patch(AccessTools.Method(typeof(AmmoCountPanel), nameof(AmmoCountPanel.GetAmmoCountByLevel)),
                postfix: new HarmonyMethod(Method(nameof(TextPostfix))));
            harmony.Patch(AccessTools.Method(typeof(AmmoCountPanel), nameof(AmmoCountPanel.GetAmmoCountByLevelForFoldingMechanismWeapon)),
                postfix: new HarmonyMethod(Method(nameof(TextPostfix))));
        }

        private static MethodInfo Method(string name) => typeof(RaygunAmmoCheck).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        /// <summary>Charges left across the chambers' cells (fired ones count nothing).</summary>
        public static int Charges(Weapon weapon) => RaygunCells.ShotsLeft(weapon);

        private static bool CheckPrefix(Player.FirearmController __instance, ref bool __result)
        {
            if (!(__instance.Item is Weapon weapon) || !RaygunIds.Is(weapon))
                return true;
            __result = true;
            try
            {
                GamePlayerOwner owner = Object.FindObjectOfType<GamePlayerOwner>();
                if (owner == null)
                    return false;
                _charges = Charges(weapon);
                owner.PlayerOnOnShowAmmoDetails(_charges, RaygunIds.FullCell * (RaygunPaP.IsInfused(weapon) ? 2 : 1) * weapon.Chambers.Length, 3, "Cold Fusion Cells", false);
            }
            catch (System.Exception e)
            {
                RaygunPlugin.Log.LogWarning($"Ray Gun ammo check skipped: {e.Message}");
            }
            finally
            {
                _charges = -1;
            }
            return false;
        }

        /// <summary>
        /// The Ray Gun fires one bolt a pull in the game's "single" mode and keeps firing while the trigger is held
        /// (RaygunAuto), so it's an automatic gun to the player: the mode shown on the screen says so.
        /// </summary>
        private static bool FireModePrefix(AmmoCountPanel __instance)
        {
            Player player = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer as Player : null;
            if (!(player?.HandsController is Player.FirearmController hands) || !RaygunIds.Is(hands.Item))
                return true;
            __instance.Show("Automatic");
            return false;
        }

        /// <summary>The fire-mode key does nothing for the Ray Gun: it has the one mode (and the game's others would break it).</summary>
        private static bool ChangeFireModePrefix(Player.FirearmController __instance, ref bool __result)
        {
            if (!RaygunIds.Is(__instance.Item))
                return true;
            __result = false;
            return false;
        }

        private static void WeaponCreatedPostfix(Weapon __instance)
        {
            try
            {
                if (!RaygunIds.Is(__instance))
                    return;
                ItemAttribute modes = __instance.Attributes.FirstOrDefault(a => a.Id is EItemAttributeId id && id == EItemAttributeId.WeaponFireType);
                if (modes != null)
                    modes.StringValue = () => "Automatic";
            }
            catch (System.Exception e)
            {
                RaygunPlugin.Log.LogWarning($"Ray Gun fire mode line skipped: {e.Message}");
            }
        }

        private static void TextPostfix(ref string __result)
        {
            if (_charges < 0)
                return;
            __result = _charges == 0 ? "Empty" : _charges == 1 ? "1 charge left" : $"{_charges} charges left";
        }
    }
}
