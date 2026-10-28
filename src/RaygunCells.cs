using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;

namespace Raygun
{
    /// <summary>
    /// The cold fusion cell rules. The Ray Gun is a two-chamber break-action (the sawed-off MP-43's mechanics) and a cell
    /// loads into a chamber like a shell, but holds ten shots: each charge level is its own stackable ammo template
    /// (<see cref="RaygunIds.Cell"/>), and a shot from a cell puts the next level down straight back in its chamber. The
    /// first chamber fires until its cell is spent, then the second.
    /// A reload can start at any time and always swaps both cells, as BO3's reload shows: the old ones are thrown out
    /// and land on the floor with their charges (an empty one is gone), to be picked up again; only full cells stack.
    /// The fullest cells are loaded first.
    /// </summary>
    internal static class RaygunCells
    {
        public static void Enable(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Player.FirearmController.FireOnlyBarrelFireOperation), "OnFireEvent"),
                postfix: new HarmonyMethod(Method(nameof(OnFireEventPostfix))));
            harmony.Patch(AccessTools.Method(typeof(Player.FirearmController.FireOnlyBarrelFireOperation), nameof(Player.FirearmController.FireOnlyBarrelFireOperation.PrepareShot)),
                new HarmonyMethod(Method(nameof(PrepareShotPrefix))));
            MethodInfo run = AccessTools.Method(typeof(Player.FirearmController.ReloadMultiBarrelResult), "Run");
            harmony.Patch(run, new HarmonyMethod(Method(nameof(RunPrefix))), new HarmonyMethod(Method(nameof(RunPostfix))));
            harmony.Patch(AccessTools.Method(typeof(FirearmHandsInputTranslator), nameof(FirearmHandsInputTranslator.ReloadBarrels)),
                new HarmonyMethod(Method(nameof(ReloadKeyPrefix))));
        }

        // ---------- One bolt a pull, in the only mode: automatic ----------

        /// <summary>
        /// The game fires every chamber at once in any mode but single (a "doublet"). The Ray Gun's one mode is
        /// automatic, so its pull is handled as a single shot here: the first loaded chamber, one bolt.
        /// </summary>
        private static bool PrepareShotPrefix(Player.FirearmController.FireOnlyBarrelFireOperation __instance)
        {
            Weapon weapon = __instance.Weapon;
            if (!RaygunIds.Is(weapon))
                return true;
            int chamber = Array.IndexOf(weapon.Chambers, weapon.FirstLoadedChamberSlot);
            if (chamber < 0)
                return true;
            __instance.SingleShot(chamber);
            __instance.FirearmsAnimator.SetFire(__instance.Controller.IsTriggerPressed);
            return false;
        }

        // ---------- The reload key ----------

        /// <summary>
        /// The reload key. The game's own only reloads a break-action with an empty or spent chamber, and loads
        /// whichever ammo it finds first. For the Ray Gun: both chambers, at any time, with the fullest cells the
        /// player can reach.
        /// </summary>
        private static bool ReloadKeyPrefix(FirearmHandsInputTranslator __instance, Weapon weapon)
        {
            try
            {
                if (!RaygunIds.Is(weapon))
                    return true;
                int needed = weapon.Chambers.Length;
                if (needed == 0 || __instance._inventoryController == null || __instance._controller == null)
                    return false;
                var reachable = new List<Ammo>();
                __instance._inventoryController.GetReachableItemsOfTypeNonAlloc(reachable,
                    a => a.StackObjectsCount > 0 && RaygunIds.IsCell(a) && a.CheckAction(null).Succeeded
                         && !weapon.Chambers.Any(c => c.ContainedItem == a));
                if (reachable.Count == 0)
                {
                    __instance._controller.ReloadMagNotFound();
                    return false;
                }
                var pack = new List<Ammo>();
                int count = 0;
                foreach (Ammo cell in reachable.OrderByDescending(RaygunIds.CellLevel))
                {
                    if (count >= needed)
                        break;
                    pack.Add(cell);
                    count += cell.StackObjectsCount;
                }
                if (__instance._controller.CanStartReload())
                    __instance._controller.ReloadBarrels(new AmmoPack(pack), null, null);
            }
            catch (Exception e)
            {
                RaygunPlugin.Log.LogError($"Ray Gun reload key failed, using the game's own: {e}");
                return true;
            }
            return false;
        }

        private static MethodInfo Method(string name) => typeof(RaygunCells).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        // ---------- Ten shots a cell (twenty in Porter's X2) ----------

        /// <summary>
        /// Porter's X2 spends a cell's charge every second shot: twenty shots a cell. Chambers whose current charge has
        /// had its first shot (by gun id and chamber). Not saved: a restart gives the half shot back.
        /// </summary>
        private static readonly HashSet<string> HalfSpent = new HashSet<string>();

        private static string HalfKey(Weapon weapon, int chamber) => weapon.Id + ":" + chamber;

        /// <summary>True when this chamber's cell has already had one of its charge's two shots in Porter's X2.</summary>
        public static bool IsHalfSpent(Weapon weapon, int chamber) => HalfSpent.Contains(HalfKey(weapon, chamber));

        /// <summary>Shots left in the Ray Gun's chambers: a charge is a shot, or two in Porter's X2.</summary>
        public static int ShotsLeft(Weapon weapon)
        {
            bool x2 = RaygunPaP.IsInfused(weapon);
            int shots = 0;
            for (int i = 0; i < weapon.Chambers.Length; i++)
            {
                if (!(weapon.Chambers[i].ContainedItem is Ammo cell) || cell.IsUsed)
                    continue;
                int level = RaygunIds.CellLevel(cell);
                shots += x2 ? level * 2 - (IsHalfSpent(weapon, i) ? 1 : 0) : level;
            }
            return shots;
        }

        /// <summary>After a shot: the cell that fired goes back in its chamber one charge down, unless that was its last.</summary>
        private static void OnFireEventPostfix(Player.FirearmController.FireOnlyBarrelFireOperation __instance)
        {
            try
            {
                Weapon weapon = __instance.Weapon;
                if (!RaygunIds.Is(weapon))
                    return;
                for (int i = 0; i < __instance._chambersForFire.Count && i < __instance._singleShotsData.Count; i++)
                {
                    int chamber = __instance._chambersForFire[i];
                    int level = RaygunIds.CellLevel(__instance._singleShotsData[i].AmmoToFire);
                    if (level < 1 || chamber < 0 || chamber >= weapon.Chambers.Length)
                        continue;
                    // Porter's X2: the first shot of a charge leaves the cell as it was; the second spends it
                    string key = HalfKey(weapon, chamber);
                    int next = level - 1;
                    if (RaygunPaP.IsInfused(weapon) && HalfSpent.Add(key))
                        next = level;
                    else
                        HalfSpent.Remove(key);
                    if (next < 1)
                        continue;
                    Slot slot = weapon.Chambers[chamber];
                    if (slot.ContainedItem != null || !(NewCell(next) is Ammo cell))
                        continue;
                    if (slot.AddWithoutRestrictions(cell).Failed)
                        continue;
                    // Still a live round in there: no spent shell for the reload to throw out
                    weapon.ShellsInChambers[chamber] = null;
                    TryShowRound(__instance.Controller, cell, chamber);
                }
                // The shot is over: the animator's fire flag goes down now. Left up (the game lowers it later, from the
                // trigger) with a round back in the chamber, the animator went straight from IDLE into FIRE again and
                // every FIRE fired the shot once more: the endless shooting. The chamber count is set on the way out
                __instance.FirearmsAnimator.SetFire(false);
            }
            catch (Exception e)
            {
                RaygunPlugin.Log.LogError($"Ray Gun cell refill failed: {e}");
            }
        }

        private static Item NewCell(int level)
        {
            Item item = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(), RaygunIds.CellId(level), null);
            if (item != null)
                item.StackObjectsCount = 1;
            return item;
        }

        // The round model in the chamber (hidden in our rig, but the game keeps track of it)
        private static MethodInfo _setRound;

        private static void TryShowRound(Player.FirearmController controller, Ammo cell, int chamber)
        {
            try
            {
                object firearms = AccessTools.Field(controller.GetType(), "Firearms")?.GetValue(controller)
                                  ?? AccessTools.Property(controller.GetType(), "Firearms")?.GetValue(controller);
                if (firearms == null)
                    return;
                _setRound = _setRound ?? AccessTools.Method(firearms.GetType(), "SetRoundIntoWeapon", new[] { typeof(Ammo), typeof(int) });
                _setRound?.Invoke(firearms, new object[] { cell, chamber });
            }
            catch (Exception)
            {
                // Only the hidden round model: nothing the player sees
            }
        }

        // ---------- Reload at any time ----------

        /// <summary>
        /// The game only reloads chambers whose round is spent, and keeps a live round where it is. For the Ray Gun every
        /// cell counts as spent for this (so both are swapped), and nothing is put back in the bag: with no place given,
        /// the game throws a live round out as loot when the old cells come out. The fullest cells go in first.
        /// </summary>
        private static void RunPrefix(Weapon weapon, ref AmmoPack ammoPack, ref ItemAddress placeToPutContainedAmmo, out List<(int chamber, Ammo cell)> __state)
        {
            __state = null;
            try
            {
                if (!RaygunIds.Is(weapon))
                    return;
                placeToPutContainedAmmo = null;
                __state = new List<(int, Ammo)>();
                for (int i = 0; i < weapon.Chambers.Length; i++)
                {
                    if (weapon.Chambers[i].ContainedItem is Ammo cell && !cell.IsUsed)
                    {
                        cell.IsUsed = true;
                        __state.Add((i, cell));
                    }
                }
                ammoPack = Fullest(ammoPack);
            }
            catch (Exception e)
            {
                RaygunPlugin.Log.LogError($"Ray Gun reload rule failed, using the game's own: {e}");
            }
        }

        private static void RunPostfix(Weapon weapon, List<(int chamber, Ammo cell)> __state)
        {
            if (__state == null)
                return;
            // New cells: no half-spent charge carries over
            HalfSpent.RemoveWhere(k => k.StartsWith(weapon.Id + ":"));
            try
            {
                foreach (var (chamber, cell) in __state)
                {
                    // Live again, so the eject throws it out as loot (it keeps its charges)
                    cell.IsUsed = false;
                    // If it was taken out for a fresh one, a "shell" in the chamber makes the reload play its eject
                    if (weapon.Chambers[chamber].ContainedItem != cell && weapon.ShellsInChambers[chamber] == null)
                        weapon.ShellsInChambers[chamber] = cell.AmmoTemplate;
                }
            }
            catch (Exception e)
            {
                RaygunPlugin.Log.LogError($"Ray Gun reload rule failed after the swap: {e}");
            }
        }

        /// <summary>The same cells, fullest first.</summary>
        private static AmmoPack Fullest(AmmoPack pack)
        {
            if (pack?._ammoPacks == null || pack._ammoPacks.Size < 2)
                return pack;
            var cells = new List<Ammo>();
            for (int i = 0; i < pack._ammoPacks.Size; i++)
                cells.Add(pack._ammoPacks[i]);
            return new AmmoPack(cells.OrderByDescending(RaygunIds.CellLevel).ToList());
        }
    }
}
