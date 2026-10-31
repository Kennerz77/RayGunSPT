using System;
using System.Collections.Generic;
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
    /// The character on the menu screens (character, raid confirmation, loading). The game poses it with the gun's own
    /// animations: "PATROL" to hold it, and every few seconds a random weapon move (look, check ammo, reload). The Ray
    /// Gun's animations are all first-person ones, which swing the gun around and leave the body's hands reaching for
    /// nothing. So on these screens it holds still: the gun is posed once in its idle, its animator is stopped, the
    /// character skips the random weapon moves, and the gun hangs off the right hand where the F12 "Menu pose" sliders
    /// put it. First person is untouched.
    /// </summary>
    internal static class RaygunMenuPose
    {
        // Animators this froze: only these are switched back on when their model shows up in first person again (a
        // pooled model), so models the game keeps still on purpose (loot, PaP's display gun) stay still
        public static readonly HashSet<Animator> Frozen = new HashSet<Animator>();

        public static void Enable(Harmony harmony, GameObject host)
        {
            harmony.Patch(AccessTools.Method(typeof(PlayerModelLoader), nameof(PlayerModelLoader.CreateWeapon)),
                postfix: new HarmonyMethod(Method(nameof(CreateWeaponPostfix))));
            harmony.Patch(AccessTools.Method(typeof(MenuPlayerPoser), nameof(MenuPlayerPoser.OnRandomizeAnimation)),
                new HarmonyMethod(Method(nameof(RandomizePrefix))));
            harmony.Patch(AccessTools.Method(typeof(PlayerBones), nameof(PlayerBones.Kinematics)),
                postfix: new HarmonyMethod(Method(nameof(KinematicsPostfix))));
            harmony.Patch(AccessTools.Method(typeof(PlayerBones), nameof(PlayerBones.ShiftWeaponRoot)),
                postfix: new HarmonyMethod(Method(nameof(ShiftWeaponRootPostfix))));
            host.AddComponent<Unfreezer>();
            // The left arm: its FinalIK limb solver reaches for the gun's left marker; switched off it follows the body
            Type solver = AccessTools.TypeByName("RootMotion.FinalIK.IKSolver");
            MethodInfo update = solver == null ? null : AccessTools.Method(solver, "Update");
            if (update != null)
                harmony.Patch(update, new HarmonyMethod(Method(nameof(SolverUpdatePrefix))));
            else
                RaygunPlugin.Log.LogWarning("FinalIK solver not found: the menu character's left hand stays on the gun");
        }

        // Left-arm solvers of menu characters holding the Ray Gun, and their poser
        private static readonly Dictionary<object, MenuPlayerPoser> LeftArms = new Dictionary<object, MenuPlayerPoser>();

        /// <summary>
        /// The left arm off the gun (unless the setting keeps it there): either placed where the F12 "Menu left hand"
        /// sliders say (from the left shoulder, in the body's frame), or relaxed into the body's own pose. No grip
        /// pose on the hand either way.
        /// </summary>
        private static void SolverUpdatePrefix(object __instance)
        {
            if (LeftArms.Count == 0 || RaygunSettings.MenuLeftHand.Value || !LeftArms.TryGetValue(__instance, out MenuPlayerPoser poser))
                return;
            if (poser == null)
            {
                LeftArms.Remove(__instance);
                return;
            }
            Transform shoulder = poser.PBones?.LeftShoulder?.Original;
            if (RaygunSettings.MenuLeftPlaced.Value && shoulder != null)
            {
                poser.Limbs[0].solver.SetIKPosition(shoulder.position + poser.transform.rotation * RaygunSettings.LeftHandOffset);
                poser.Limbs[0].solver.IKPositionWeight = 1f;
            }
            else
            {
                poser.Limbs[0].solver.IKPositionWeight = 0f;
            }
            poser.Limbs[0].solver.IKRotationWeight = 0f;
            if (poser.LeftHandPoser != null)
                poser.LeftHandPoser.weight = 0f;
        }

        private static MethodInfo Method(string name) => typeof(RaygunMenuPose).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        // Bones of the menu characters holding the Ray Gun; in-raid bodies never get in here
        private static readonly Dictionary<PlayerBones, Holding> Holds = new Dictionary<PlayerBones, Holding>();

        /// <summary>
        /// On a menu character holding the Ray Gun. Each frame the poser places the gun and sends both hands to their
        /// markers; right after, the gun is moved and turned about the right hand by the menu pose settings, worked out
        /// fresh from where the game just put it so nothing builds up.
        /// </summary>
        private class Holding : MonoBehaviour
        {
            public MenuPlayerPoser Poser;
            public Transform Model;
            public Transform RightMarker;

            public void Place()
            {
                Transform root = Poser?.PBones?.Weapon_Root_Anim;
                if (root == null || RightMarker == null)
                    return;
                // Sized first, about the right hand (which stays put), so the hold below is the same at any size
                RaygunFit.ApplyMenu(Model, root);
                Quaternion body = Poser.transform.rotation;
                Vector3 pivot = RightMarker.position;
                // Turned in the body's frame about the right hand, then shifted in the body's frame
                Quaternion turn = body * RaygunSettings.MenuTurn * Quaternion.Inverse(body);
                root.SetPositionAndRotation(pivot + turn * (root.position - pivot) + body * RaygunSettings.MenuShift, turn * root.rotation);
            }

            private void OnDestroy()
            {
                if (Poser?.PBones != null && Holds.TryGetValue(Poser.PBones, out Holding hold) && hold == this)
                    Holds.Remove(Poser.PBones);
            }
        }

        // The gun hangs off the right hand: place it from there
        private static void KinematicsPostfix(PlayerBones __instance)
        {
            if (Holds.TryGetValue(__instance, out Holding hold) && hold != null)
                hold.Place();
        }

        // The body animation placed the gun: hang it off the right hand instead (as the other branch does, so the
        // hold looks the same whichever the menu animation picks), which places it via KinematicsPostfix
        private static void ShiftWeaponRootPostfix(PlayerBones __instance)
        {
            if (Holds.TryGetValue(__instance, out Holding hold) && hold != null && hold.RightMarker != null)
                __instance.Kinematics(hold.RightMarker, 0f);
        }

        private static void CreateWeaponPostfix(PlayerModelLoader __instance, Item weapon)
        {
            try
            {
                MenuPlayerPoser poser = __instance.ModelPlayerPoser;
                Holding hold = poser?.GetComponent<Holding>();
                if (!RaygunIds.Is(weapon))
                {
                    if (hold != null)
                    {
                        Holds.Remove(poser.PBones);
                        UnityEngine.Object.Destroy(hold);
                    }
                    if (poser?.Limbs != null && poser.Limbs.Length > 0)
                        LeftArms.Remove(poser.Limbs[0].solver);
                    return;
                }
                // The body's hold: the game picks the rifle one for the Ray Gun (a shotgun underneath); the setting can
                // pick the pistol one instead
                Animator body = poser?.GetComponent<Animator>();
                if (body != null)
                {
                    int type = (int)RaygunFit.MenuBody.Value;
                    body.SetInteger(PlayerAnimator.WEAPON_TYPE, type);
                    body.SetFloat(PlayerAnimator.WEAPON_TYPE_FLOAT_HASH, type);
                    body.SetFloat(PlayerAnimator.UI_WEAPON_TYPE, type);
                }
                // The game's animator wrapper drives this same Unity Animator
                Animator animator = __instance._weaponPrefab?.GetComponentInChildren<Animator>(true);
                if (animator == null || animator.layerCount < 2)
                    return;
                animator.enabled = true;
                animator.Play("IDLE", 1, 0f);
                animator.Update(0f);
                // Frozen in that pose: nothing the menu sets afterwards (patrol, reload triggers) moves it
                animator.enabled = false;
                Frozen.Add(animator);

                if (poser == null)
                    return;
                hold = hold ?? poser.gameObject.AddComponent<Holding>();
                hold.Poser = poser;
                hold.Model = animator.transform;
                Transform[] all = animator.GetComponentsInChildren<Transform>(true);
                hold.RightMarker = all.FirstOrDefault(t => t.name == "weapon_R_IK_marker") ?? all.FirstOrDefault(t => t.name == "weapon_R_hand_marker");
                if (poser.PBones != null)
                    Holds[poser.PBones] = hold;
                if (poser.Limbs != null && poser.Limbs.Length > 0)
                    LeftArms[poser.Limbs[0].solver] = poser;
            }
            catch (System.Exception e)
            {
                RaygunPlugin.Log.LogWarning($"Ray Gun menu pose skipped: {e.Message}");
            }
        }

        // The character just stands holding it
        private static bool RandomizePrefix(MenuPlayerPoser __instance) => __instance.GetComponent<Holding>() == null;

        /// <summary>A model the menu froze comes back alive when it's the local player's gun in hand (pooled models are reused).</summary>
        private class Unfreezer : MonoBehaviour
        {
            private void Update()
            {
                if (Frozen.Count == 0 || !Singleton<GameWorld>.Instantiated)
                    return;
                if (!(Singleton<GameWorld>.Instance.MainPlayer?.HandsController is Player.FirearmController hands) || hands._weaponPrefab == null)
                    return;
                Animator animator = hands._weaponPrefab.GetComponentInChildren<Animator>(true);
                if (animator != null && Frozen.Remove(animator))
                    animator.enabled = true;
            }
        }
    }
}
