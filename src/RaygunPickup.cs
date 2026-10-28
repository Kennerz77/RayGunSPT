using System.Reflection;
using EFT.InventoryLogic;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace Raygun
{
    /// <summary>
    /// BO3's Ray Gun pickup sound, when the player picks a Ray Gun up off the ground. Not for any other way it changes
    /// hands: bought, taken out of a container, moved about the inventory. The game's own loot item hears its item
    /// leave the world (the pickup), so that's the moment.
    /// </summary>
    internal static class RaygunPickup
    {
        // A pickup this close to the player's own camera is theirs (others' pickups, bots', are heard from afar)
        private const float OwnReach = 6f;

        public static void Enable(Harmony harmony)
        {
            MethodInfo remove = AccessTools.Method(typeof(LootItem), nameof(LootItem.RemoveLootItem));
            if (remove == null)
            {
                RaygunPlugin.Log.LogWarning("Ray Gun pickup sound: no loot-removal hook found");
                return;
            }
            harmony.Patch(remove, postfix: new HarmonyMethod(typeof(RaygunPickup).GetMethod(nameof(RemovedPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static void RemovedPostfix(LootItem __instance, RemoveItemEventArgs args)
        {
            try
            {
                // The same test as the game's own: the item left the world, succeeded
                if (args.Status != CommandStatus.Succeed || !(args.From is OwnerItself) || !RaygunIds.Is(args.Item))
                    return;
                Camera cam = Camera.main;
                if (cam != null && Vector3.Distance(cam.transform.position, __instance.transform.position) > OwnReach)
                    return;
                Play();
            }
            catch (System.Exception e)
            {
                RaygunPlugin.Log.LogWarning($"Ray Gun pickup sound skipped: {e.Message}");
            }
        }

        /// <summary>The pickup sound, in the player's head, at its F12 volume.</summary>
        public static void Play()
        {
            GameObject prefab = RaygunBolts.Prefab("rg_pickup");
            if (prefab == null)
                return;
            GameObject sound = Object.Instantiate(prefab);
            sound.SetActive(true);
            AudioSource audio = sound.GetComponent<AudioSource>();
            if (audio != null)
                audio.volume *= RaygunSettings.PickupVolume.Value;
            Object.Destroy(sound, audio != null && audio.clip != null ? audio.clip.length + 0.2f : 1f);
        }
    }
}
