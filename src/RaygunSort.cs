using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using HarmonyLib;

namespace Raygun
{
    /// <summary>
    /// The inventory's Sort button only moves items about; it never joins part-filled stacks. For the Ray Gun's full
    /// cold fusion cells it does here: before the sort runs, the cells in the sorted container are poured together
    /// into stacks of six, so sorting leaves full stacks and at most one part stack.
    /// </summary>
    internal static class RaygunSort
    {
        private static FieldInfo _item, _controller;
        private static bool _running;

        public static void Enable(Harmony harmony)
        {
            _item = AccessTools.Field(typeof(GridSortPanel), "_item");
            _controller = AccessTools.Field(typeof(GridSortPanel), "_controller");
            harmony.Patch(AccessTools.Method(typeof(GridSortPanel), nameof(GridSortPanel.SortAsync)),
                new HarmonyMethod(typeof(RaygunSort).GetMethod(nameof(SortPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static bool SortPrefix(GridSortPanel __instance, ref Task __result)
        {
            if (_running || !(_item.GetValue(__instance) is CompoundItem container) || !(_controller.GetValue(__instance) is InventoryController controller))
                return true;
            if (Stacks(container).Count(g => g.Count() > 1) == 0)
                return true;
            __result = MergeThenSort(__instance, container, controller);
            return false;
        }

        /// <summary>The container's full cells, grouped by their template: only these can stack.</summary>
        private static IEnumerable<IGrouping<string, Item>> Stacks(CompoundItem container) =>
            container.Grids.SelectMany(g => g.Items)
                .Where(i => RaygunIds.CellLevel(i) == RaygunIds.FullCell && i.StackObjectsCount < i.StackMaxSize)
                .GroupBy(i => i.TemplateId.ToString());

        private static async Task MergeThenSort(GridSortPanel panel, CompoundItem container, InventoryController controller)
        {
            try
            {
                var cells = container.Grids.SelectMany(g => g.Items).Where(i => RaygunIds.CellLevel(i) == RaygunIds.FullCell).ToList();
                // Fullest first: each smaller stack pours into the fullest one that has room
                List<Item> stacks = cells.OrderByDescending(i => i.StackObjectsCount).ToList();
                for (int to = 0; to < stacks.Count; to++)
                {
                    Item target = stacks[to];
                    for (int from = stacks.Count - 1; from > to && target.StackObjectsCount < target.StackMaxSize; from--)
                    {
                        Item source = stacks[from];
                        if (source.StackObjectsCount <= 0)
                            continue;
                        int room = target.StackMaxSize - target.StackObjectsCount;
                        if (source.StackObjectsCount <= room)
                        {
                            var merge = ItemManipulator.Merge(source, target, controller, simulate: false);
                            if (merge.Succeeded)
                            {
                                await controller.TryRunNetworkTransaction(merge);
                                stacks.RemoveAt(from);
                            }
                        }
                        else
                        {
                            var transfer = ItemManipulator.TransferExact(source, room, target, controller, simulate: false);
                            if (transfer.Succeeded)
                                await controller.TryRunNetworkTransaction(transfer);
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                RaygunPlugin.Log.LogWarning($"Joining cell stacks before the sort skipped: {e.Message}");
            }
            _running = true;
            try
            {
                await panel.SortAsync();
            }
            finally
            {
                _running = false;
            }
        }
    }
}
