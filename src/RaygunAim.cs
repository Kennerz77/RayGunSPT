using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Raygun
{
    /// <summary>
    /// The Ray Gun's bolts go where the player looks: still out of the muzzle, but aimed at whatever is under the
    /// middle of the screen from the hip, and through the ring sight ("rg_sight" in the model) when aiming down the
    /// sights, plus the F12 offset. EFT fires along the
    /// muzzle's own axis, and the gun is held at an angle in the hand, so the bolts went low and to the right.
    /// Just before each shot the muzzle point is turned onto the aim point.
    /// </summary>
    internal static class RaygunAim
    {
        private const float MaxRange = 1500f;
        // Hits closer than this are the player's own gun, arms or body
        private const float MinRange = 1.2f;

        private static FieldInfo _player;

        public static void Enable(Harmony harmony)
        {
            _player = AccessTools.Field(typeof(Player.FirearmController), "_player");
            harmony.Patch(AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.Shot)),
                new HarmonyMethod(typeof(RaygunAim).GetMethod(nameof(ShotPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static void ShotPrefix(Player.FirearmController __instance, Weapon weapon)
        {
            try
            {
                if (!RaygunSettings.AimAtCentre.Value || !RaygunIds.Is(weapon) || !(_player?.GetValue(__instance) is Player player) || !player.IsYourPlayer)
                    return;
                Transform muzzle = __instance.CurrentFireport?.Original;
                Camera cam = Camera.main;
                if (muzzle == null || cam == null)
                    return;
                Vector3 from = cam.transform.position, forward = cam.transform.forward;
                if (__instance.IsAiming && RaygunSettings.AimThroughSight.Value && Sight(muzzle) is Transform sight)
                {
                    forward = (sight.position - from).normalized;
                    // cm at 10 m: 1 cm is a thousandth of the distance
                    forward = (forward + cam.transform.right * (RaygunSettings.SightShotRight.Value * 0.001f)
                                       + cam.transform.up * (RaygunSettings.SightShotUp.Value * 0.001f)).normalized;
                }
                Vector3 target = AimPoint(from, forward);
                Vector3 want = target - muzzle.position;
                Vector3 now = muzzle.TransformDirection(player.LocalShotDirection);
                if (want.sqrMagnitude < 1e-6f || now.sqrMagnitude < 1e-6f)
                    return;
                muzzle.rotation = Quaternion.FromToRotation(now, want) * muzzle.rotation;
            }
            catch (System.Exception e)
            {
                RaygunPlugin.Log.LogWarning($"Ray Gun aim skipped: {e.Message}");
            }
        }

        private static Transform _sightRoot, _sight;

        /// <summary>The ring sight marker on the gun this muzzle belongs to.</summary>
        private static Transform Sight(Transform muzzle)
        {
            Transform root = muzzle.root;
            if (root != _sightRoot || _sight == null)
            {
                _sightRoot = root;
                _sight = null;
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "rg_sight")
                    {
                        _sight = t;
                        break;
                    }
                }
            }
            return _sight;
        }

        /// <summary>The first solid thing along the view beyond arm's reach, or a point far out.</summary>
        private static Vector3 AimPoint(Vector3 from, Vector3 forward)
        {
            float best = MaxRange;
            foreach (RaycastHit hit in Physics.RaycastAll(from, forward, MaxRange, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.distance >= MinRange && hit.distance < best)
                    best = hit.distance;
            }
            return from + forward * best;
        }
    }
}
