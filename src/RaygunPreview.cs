using System.Reflection;
using EFT.UI.WeaponModding;
using HarmonyLib;
using UnityEngine;

namespace Raygun
{
    /// <summary>
    /// The 3D view in the item window centres a gun on its saved pivot, which only fits the pose the model was saved in.
    /// A Ray Gun model that has been in the hands comes back in another pose, and a fresh one sat off to the side. Here
    /// the Ray Gun is centred on what's actually drawn, every time.
    /// </summary>
    internal static class RaygunPreview
    {
        public static void Enable(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(WeaponPreview), nameof(WeaponPreview.PositionGameObject)),
                postfix: new HarmonyMethod(typeof(RaygunPreview).GetMethod(nameof(PositionPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static void PositionPostfix(GameObject itemGameObject)
        {
            try
            {
                if (itemGameObject == null || !IsRaygun(itemGameObject))
                    return;
                Bounds bounds = WeaponPreview.GetBounds(itemGameObject);
                if (bounds.extents == Vector3.zero)
                    return;
                Transform parent = itemGameObject.transform.parent;
                Vector3 centre = parent != null ? parent.position : Vector3.zero;
                itemGameObject.transform.position += centre - bounds.center;
            }
            catch (System.Exception e)
            {
                RaygunPlugin.Log.LogWarning($"Ray Gun preview centring skipped: {e.Message}");
            }
        }

        // The Ray Gun's model carries its hidden upgraded-materials holder
        private static bool IsRaygun(GameObject go)
        {
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                if (t.name == "rg_upgraded_materials")
                    return true;
            return false;
        }
    }
}
