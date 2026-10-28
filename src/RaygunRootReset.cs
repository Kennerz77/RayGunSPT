using System.Collections.Generic;
using System.Reflection;
using EFT.Animations;
using HarmonyLib;
using UnityEngine;

namespace Raygun
{
    /// <summary>
    /// EFT's procedural weapon animation layers sway, look and recoil rotation onto Weapon_root_anim's current local
    /// rotation every frame, trusting the weapon's animation to have put it back to rest first. On our rebuilt rig
    /// (the BO1 arms rig) that reset didn't happen, so the rotations piled up and the gun cartwheeled around the player. Our
    /// clips never rotate Weapon_root_anim (the gun moves through the weapon bone below it), so resetting it to rest
    /// here is exactly what the animation should do. Only Ray Gun rigs are touched.
    /// The rest pose carries the view fit from the F12 "View position" and "Aiming" sliders (defaults: the fit tuned
    /// in game, 2026-10-27). Aiming turns the gun so the eye looks through the rear hoop ("rg_hoop") at the ring
    /// sight ("rg_sight"), and seats the aim camera on that line.
    /// </summary>
    internal static class RaygunRootReset
    {
        // 0 from the hip, 1 aiming: the gun's hip tilt eases out while aiming, so the sight lines up dead centre
        private static float _aim;
        private static int _aimFrame = -1;

        // Rest local positions as the clips set them (Weapon_root_anim) or the prefab has them (aim_camera)
        private static readonly Dictionary<Transform, Vector3> Rest = new Dictionary<Transform, Vector3>();

        public static void Enable(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(RaygunRootReset).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (string method in new[] { "ApplyComplexRotation", "ApplySimpleRotation" })
                harmony.Patch(AccessTools.Method(typeof(ProceduralWeaponAnimation), method), prefix);
        }

        private static void Prefix(ProceduralWeaponAnimation __instance)
        {
            Transform rootAnim = __instance.HandsContainer?.WeaponRootAnim;
            if (rootAnim == null || !rootAnim.root.name.StartsWith("weapon_raygun"))
                return;

            if (_aimFrame != Time.frameCount)
            {
                _aimFrame = Time.frameCount;
                _aim = Mathf.MoveTowards(_aim, __instance.IsAiming ? 1f : 0f, Time.deltaTime * 5f);
            }
            float ease = _aim * _aim * (3f - 2f * _aim);
            rootAnim.localRotation = Quaternion.Slerp(RaygunSettings.ViewRotation, Quaternion.identity, ease);
            rootAnim.localPosition = RestOf(rootAnim) + RaygunSettings.ViewOffset;
            RaygunPower.Update(rootAnim.root);
            QuickenX2Fire(__instance, rootAnim.root);
            Transform aim = rootAnim.Find("weapon/aim_camera");
            if (aim == null)
                return;
            aim.localPosition = RestOf(aim) + RaygunSettings.AimOffset;
            if (ease > 0f && RaygunSettings.AimThroughHoop.Value)
                LineUpHoop(rootAnim, aim, ease);
        }

        /// <summary>Porter's X2 fires a little faster: the shot animation (what paces the shots) plays this much quicker.</summary>
        private const float X2FireSpeed = 1.2f;

        private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, EFT.Player.FirearmController> FirearmOf =
            AccessTools.FieldRefAccess<ProceduralWeaponAnimation, EFT.Player.FirearmController>("_firearmController");

        private static Transform _animatorRoot;
        private static Animator _animator;

        private static void QuickenX2Fire(ProceduralWeaponAnimation pwa, Transform root)
        {
            // Looked up once per model, not every frame
            if (root != _animatorRoot || _animator == null)
            {
                _animatorRoot = root;
                _animator = root.GetComponentInChildren<Animator>(true);
            }
            Animator animator = _animator;
            if (animator == null)
                return;
            bool firing = false;
            for (int layer = 0; layer < animator.layerCount && !firing; layer++)
                firing = animator.GetCurrentAnimatorStateInfo(layer).IsName("FIRE");
            bool x2 = firing && FirearmOf(pwa)?.Item is EFT.InventoryLogic.Weapon gun && RaygunPaP.IsInfused(gun);
            if (x2)
                animator.speed = X2FireSpeed;
            else if (animator.speed == X2FireSpeed)
                animator.speed = 1f;
        }

        private static Transform _markersRoot, _sight, _hoop;

        /// <summary>
        /// Turns the gun (by <paramref name="ease"/>) so the line from the rear hoop's opening to the ring sight runs
        /// along the view, and puts the aim camera on that line, as far behind the ring as before.
        /// </summary>
        private static void LineUpHoop(Transform rootAnim, Transform aim, float ease)
        {
            Camera cam = Camera.main;
            Transform weapon = aim.parent;
            if (cam == null || weapon == null || !Markers(rootAnim.root))
                return;
            Vector3 hoop = _hoop.position + weapon.TransformVector(new Vector3(0f, 0f, RaygunSettings.HoopUp.Value * 0.01f));
            Vector3 line = _sight.position - hoop;
            if (line.sqrMagnitude < 1e-6f)
                return;
            Quaternion turn = Quaternion.FromToRotation(line, cam.transform.forward);
            rootAnim.rotation = Quaternion.Slerp(Quaternion.identity, turn, ease) * rootAnim.rotation;

            // After the turn: the eye point on the line, the aim camera's old distance behind the ring
            hoop = _hoop.position + weapon.TransformVector(new Vector3(0f, 0f, RaygunSettings.HoopUp.Value * 0.01f));
            Vector3 dir = (_sight.position - hoop).normalized;
            float back = Vector3.Distance(weapon.TransformPoint(RestOf(aim)), _sight.position);
            Vector3 eye = weapon.InverseTransformPoint(_sight.position - dir * back) + RaygunSettings.AimOffset;
            aim.localPosition = Vector3.Lerp(aim.localPosition, eye, ease);
        }

        private static bool Markers(Transform root)
        {
            if (root != _markersRoot || _sight == null || _hoop == null)
            {
                _markersRoot = root;
                _sight = _hoop = null;
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "rg_sight")
                        _sight = t;
                    else if (t.name == "rg_hoop")
                        _hoop = t;
                }
            }
            return _sight != null && _hoop != null;
        }

        private static Vector3 RestOf(Transform t)
        {
            if (Rest.Count > 64)
                Rest.Clear(); // old rigs from earlier raids: keyed by destroyed transforms
            if (!Rest.TryGetValue(t, out Vector3 rest))
                Rest[t] = rest = t.localPosition;
            return rest;
        }
    }
}
