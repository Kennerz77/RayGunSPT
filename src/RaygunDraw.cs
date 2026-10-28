using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.InputSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Raygun
{
    /// <summary>
    /// A Ray Gun bought off the wall into an empty holster goes straight into the player's hands. In the hideout's
    /// weapons mode the hands hold a copy of the gear made when the mode started, so the new gun is copied into it
    /// first. Hideout Uncensored (a hard dependency: weapons anywhere in the hideout) draws it through its slot key, as
    /// that mod expects; should that fail, only the shooting range draws it, where the game allows weapons.
    /// </summary>
    internal static class RaygunDraw
    {
        public const string HideoutUncensoredGuid = "com.trappuss.hideoutuncensored";

        private const string Holster = "Holster";

        /// <summary>True while the holster is empty right now (taken before the purchase is sent: the server fills it).</summary>
        public static bool HolsterEmpty(HideoutPlayerOwner owner)
        {
            HideoutPlayer player = owner?.HideoutPlayer;
            return player != null && RealSlot(player, Holster)?.ContainedItem == null;
        }

        /// <summary>True if drawing the bought gun was started (the holster was free, so it was equipped, not stashed).</summary>
        public static bool TryDrawBought(HideoutPlayerOwner owner, bool holsterWasEmpty)
        {
            if (owner?.HideoutPlayer == null || !holsterWasEmpty)
                return false;
            RaygunPlugin.Runner.StartCoroutine(DrawWhenEquipped(owner));
            return true;
        }

        private static Slot RealSlot(HideoutPlayer player, string id) =>
            (player.OriginalInventory?.Inventory ?? player.Inventory)?.Equipment?.GetAllSlots().FirstOrDefault(s => s.ID == id);

        private static IEnumerator DrawWhenEquipped(HideoutPlayerOwner owner)
        {
            HideoutPlayer player = owner.HideoutPlayer;
            // The server's reply is applied to the profile's inventory as it arrives: wait for the gun (2 s at most)
            Weapon gun = null;
            for (float t = 0f; t < 2f && gun == null && player != null; t += Time.unscaledDeltaTime)
            {
                if (RealSlot(player, Holster)?.ContainedItem is Weapon w && RaygunIds.Is(w))
                    gun = w;
                else
                    yield return null;
            }
            if (gun == null)
            {
                Notify("Ray Gun bought.");
                yield break;
            }

            try
            {
                // Weapons mode: the hands' copy of the gear gets the new gun too
                if (owner.InShootingRange && player.ShootingRangeInventory is InventoryController copies)
                {
                    Slot copySlot = copies.Inventory?.Equipment?.GetAllSlots().FirstOrDefault(s => s.ID == Holster);
                    if (copySlot != null && copySlot.ContainedItem == null)
                    {
                        var added = ItemManipulator.AddWithoutRestrictions(gun.CloneItem(), copySlot.CreateItemAddress(), copies);
                        if (added.Failed)
                            RaygunPlugin.Log.LogWarning($"Couldn't put the bought Ray Gun into the weapons-mode gear: {added.Error}");
                    }
                }
                if (HideoutUncensored.Draw(owner))
                    Notify("Ray Gun bought and equipped.");
                else if (owner.InShootingRange)
                {
                    player.SetSlotItem(EquipmentSlot.Holster, _ => { });
                    Notify("Ray Gun bought and equipped.");
                }
                else
                    Notify("Ray Gun bought and equipped (draw it in the shooting range).");
            }
            catch (Exception e)
            {
                RaygunPlugin.Log.LogWarning($"Drawing the bought Ray Gun failed: {e.Message}");
                Notify("Ray Gun bought and equipped.");
            }
        }

        private static void Notify(string message) => EFT.Communications.NotificationManager.DisplayMessageNotification(message);

        /// <summary>
        /// Hideout Uncensored keeps weapons out anywhere in the hideout and decides every frame whether they
        /// should be; it's asked to draw through its own slot-key handler, by reflection, as the PaP machine does.
        /// </summary>
        private static class HideoutUncensored
        {
            private static PropertyInfo _want;
            private static MethodInfo _onWeaponKey;
            private static bool _looked;

            public static bool Draw(HideoutPlayerOwner owner)
            {
                if (!_looked)
                {
                    _looked = true;
                    Type session = AccessTools.TypeByName("HideoutUncensored.HideoutSession");
                    _want = session == null ? null : AccessTools.Property(session, "Want");
                    _onWeaponKey = session == null ? null : AccessTools.Method(session, "OnWeaponKey");
                }
                if (_want?.GetSetMethod(true) == null)
                    return false;
                try
                {
                    _onWeaponKey?.Invoke(null, new object[] { owner, ECommand.SelectSecondaryWeapon });
                }
                catch (Exception e)
                {
                    RaygunPlugin.Log.LogWarning($"Drawing by slot failed, drawing normally: {e.GetBaseException().Message}");
                }
                _want.GetSetMethod(true).Invoke(null, new object[] { true });
                return true;
            }
        }
    }
}
