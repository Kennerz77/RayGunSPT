using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;

namespace Raygun
{
    /// <summary>
    /// Cold fusion cells in Porter's X2 hold twenty shots (a charge is spent every second shot, RaygunCells). Only
    /// while a cell sits in a Pack-a-Punched Ray Gun: its name shows the shots out of twenty and its description says
    /// so. Anywhere else (loose, in a bag, in a plain Ray Gun) it is the ordinary ten-shot cell, as the server made it.
    /// </summary>
    internal static class RaygunX2
    {
        private static readonly Regex Count = new Regex(@"\((\d+)/10\)");

        public static void Enable(Harmony harmony)
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(Item), nameof(Item.Description)),
                postfix: new HarmonyMethod(Method(nameof(DescriptionPostfix))));
            // The inspect window and item windows ask the item for its name key and localize it themselves: the key
            // comes back as the finished text, which they pass through unchanged
            foreach (string property in new[] { nameof(Item.Name), nameof(Item.ShortName) })
                harmony.Patch(AccessTools.PropertyGetter(typeof(Item), property), postfix: new HarmonyMethod(Method(nameof(KeyPostfix))));
            foreach (string name in new[] { nameof(LocalizationExtensions.LocalizedName), nameof(LocalizationExtensions.LocalizedShortName) })
            {
                MethodInfo method = AccessTools.Method(typeof(LocalizationExtensions), name, new[] { typeof(Item) });
                if (method != null)
                    harmony.Patch(method, postfix: new HarmonyMethod(Method(nameof(NamePostfix))));
            }
        }

        private static MethodInfo Method(string name) => typeof(RaygunX2).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        /// <summary>The Pack-a-Punched Ray Gun holding this cell in a chamber, and which chamber.</summary>
        private static bool InX2(Item item, out Weapon gun, out int chamber)
        {
            gun = null;
            chamber = -1;
            if (!RaygunIds.IsCell(item) || !(item.CurrentAddress?.Container?.ParentItem is Weapon weapon) || !RaygunIds.Is(weapon))
                return false;
            chamber = System.Array.FindIndex(weapon.Chambers, c => c.ContainedItem == item);
            if (chamber < 0 || !RaygunPaP.IsInfused(weapon))
                return false;
            gun = weapon;
            return true;
        }

        private static void KeyPostfix(Item __instance, ref string __result)
        {
            if (__result == null || !InX2(__instance, out Weapon gun, out int chamber))
                return;
            string text = __result.Localized();
            int shots = RaygunIds.CellLevel(__instance) * 2 - (RaygunCells.IsHalfSpent(gun, chamber) ? 1 : 0);
            if (text != null)
                __result = Count.Replace(text, $"({shots}/20)");
        }

        private static void NamePostfix(Item item, ref string __result)
        {
            if (__result == null || !InX2(item, out Weapon gun, out int chamber))
                return;
            int shots = RaygunIds.CellLevel(item) * 2 - (RaygunCells.IsHalfSpent(gun, chamber) ? 1 : 0);
            __result = Count.Replace(__result, $"({shots}/20)");
        }

        private static void DescriptionPostfix(Item __instance, ref string __result)
        {
            if (!InX2(__instance, out Weapon gun, out int chamber))
                return;
            int shots = RaygunIds.CellLevel(__instance) * 2 - (RaygunCells.IsHalfSpent(gun, chamber) ? 1 : 0);
            __result = $"A Group 935 cold fusion cell, charged up by Pack-a-Punch while it sits in Porter's X2 Ray Gun: it holds twenty shots here, " +
                       $"{shots} of them left. Taken out of the gun it is an ordinary ten-shot cell again.";
        }
    }
}
