using System;
using EFT.InventoryLogic;
using EFT.Trading;
using HarmonyLib;

namespace Raygun
{
    /// <summary>
    /// What traders pay for the Ray Gun's items (the server pays the price the client offers). The gun always sells
    /// for what the wall charged, plus the Pack-a-Punch fee when infused. A cell sells for what the ammo case charged,
    /// less 1,000 for each charge spent, and for nothing once empty.
    /// </summary>
    internal static class RaygunSellPrice
    {
        public const int GunRub = 935_000;
        public const int FullCellRub = 10_000;

        public static void Enable(Harmony harmony)
        {
            // Last, so PaP's own postfix (which adds its fee to the vanilla price) has already run and is replaced
            harmony.Patch(AccessTools.Method(typeof(Trader), nameof(Trader.GetUserItemPrice)),
                postfix: new HarmonyMethod(typeof(RaygunSellPrice).GetMethod(nameof(PricePostfix),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)) { priority = Priority.Last });
        }

        private static void PricePostfix(Trader __instance, Item item, ref Trader.ItemPrice? __result)
        {
            try
            {
                // No price means this trader doesn't buy it; that stays so
                if (__result == null || item == null)
                    return;
                long rub;
                if (RaygunIds.Is(item))
                    rub = GunRub + (RaygunPaP.IsInfused(item) ? RaygunPaP.InfusedFeeRub : 0);
                else if (RaygunIds.IsCell(item))
                    rub = (long)FullCellRub * RaygunIds.CellLevel(item) / RaygunIds.FullCell * Math.Max(1, item.StackObjectsCount);
                else
                    return;
                Trader.ItemPrice price = __result.Value;
                double course = price.CurrencyId.HasValue && __instance.CurrencyCourses != null
                                && __instance.CurrencyCourses.TryGetValue(price.CurrencyId.Value.ToString(), out double c) && c > 0 ? c : 1.0;
                __result = new Trader.ItemPrice(price.CurrencyId, (int)Math.Min((long)Math.Floor(rub / course), int.MaxValue));
            }
            catch (Exception e)
            {
                RaygunPlugin.Log.LogError($"Sell price failed: {e.Message}");
            }
        }
    }
}
