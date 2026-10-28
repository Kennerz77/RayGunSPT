using System.Collections.Generic;
using System.IO;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Raygun
{
    /// <summary>
    /// What a Ray Gun bolt looks and sounds like. The cells are explosive rounds on the server (a small ball of gas, no
    /// shrapnel, "raygun_bolt" as the explosion's name, which no effect of the game's has): the game flies the bolt,
    /// explodes it on impact and deals the blast; this draws it. Each bolt gets a glowing core, a bright line, rings
    /// left along its path every so far (BO3's) and a thin smoky trail (raygun_fx.bundle, beside the plugin), and the
    /// impact a swelling ball of gas that splashes outward, with BO3's ray explosion. Green, or red from Porter's X2.
    /// </summary>
    internal class RaygunBolts : MonoBehaviour
    {
        private const string ExplosionName = "raygun_bolt";
        private const string BundleFile = "raygun_fx.bundle";

        private static AssetBundle _bundle;
        private static bool _bundleTried;
        private static readonly Dictionary<string, GameObject> Prefabs = new Dictionary<string, GameObject>();

        // Bolts in flight and the effect riding each
        private static readonly List<Bolt> Live = new List<Bolt>();

        // One ring left every so far along a bolt's path (metres)
        private const float RingSpacing = 1.6f;

        private class Bolt
        {
            public Shot Shot;
            public GameObject Fx;
            public ParticleSystem Rings;
            public Vector3 Last;
            public float SinceRing;
        }

        // Whether the hit being handled came from a Pack-a-Punched gun (set by the hit handler, read by the effect)
        private static bool _upgradedHit;

        public static void Enable(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(BallisticsCalculator), nameof(BallisticsCalculator.CreateShot)),
                postfix: new HarmonyMethod(Method(nameof(CreateShotPostfix))));
            harmony.Patch(AccessTools.Method(typeof(Systems.Effects.Effects), nameof(Systems.Effects.Effects.EmitGrenade)),
                new HarmonyMethod(Method(nameof(EmitGrenadePrefix))));
            foreach (System.Type world in new[] { typeof(ClientGameWorld), typeof(GameWorld) })
            {
                MethodInfo hit = AccessTools.DeclaredMethod(world, "ShotDelegate");
                if (hit != null)
                    harmony.Patch(hit, new HarmonyMethod(Method(nameof(ShotDelegatePrefix))));
            }
        }

        private static MethodInfo Method(string name) => typeof(RaygunBolts).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        internal static GameObject Prefab(string name)
        {
            if (Prefabs.TryGetValue(name, out GameObject cached) && cached != null)
                return cached;
            if (_bundle == null && !_bundleTried)
            {
                _bundleTried = true;
                string path = Path.Combine(RaygunPlugin.Dir, BundleFile);
                _bundle = File.Exists(path) ? AssetBundle.LoadFromFile(path) : null;
                if (_bundle == null)
                    RaygunPlugin.Log.LogWarning($"Ray Gun effects bundle missing or unreadable: {path}");
            }
            GameObject prefab = _bundle != null ? _bundle.LoadAsset<GameObject>(name) : null;
            Prefabs[name] = prefab;
            return prefab;
        }

        // ---------- The bolt in flight ----------

        private static void CreateShotPostfix(Shot __result, Ammo ammo, Item weapon, int fragmentIndex)
        {
            try
            {
                if (__result == null || fragmentIndex != 0 || !RaygunIds.IsCell(ammo) || !RaygunIds.Is(weapon))
                    return;
                GameObject prefab = Prefab(RaygunPaP.IsInfused(weapon) ? "rg_bolt_up" : "rg_bolt");
                if (prefab == null)
                    return;
                GameObject fx = Instantiate(prefab, __result.CurrentPosition, Quaternion.identity);
                fx.SetActive(true);
                Transform rings = fx.transform.Find("rings");
                Live.Add(new Bolt { Shot = __result, Fx = fx, Rings = rings != null ? rings.GetComponent<ParticleSystem>() : null, Last = __result.CurrentPosition });
            }
            catch (System.Exception e)
            {
                RaygunPlugin.Log.LogWarning($"Ray Gun bolt effect skipped: {e.Message}");
            }
        }

        private void LateUpdate()
        {
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                Bolt bolt = Live[i];
                if (bolt.Fx == null)
                {
                    Live.RemoveAt(i);
                    continue;
                }
                if (bolt.Shot == null || bolt.Shot.IsShotFinished)
                {
                    Land(bolt.Fx);
                    Live.RemoveAt(i);
                    continue;
                }
                Vector3 now = bolt.Shot.CurrentPosition;
                bolt.Fx.transform.position = now;
                LeaveRings(bolt, now);
            }
        }

        /// <summary>Rings along the stretch flown since last frame, one every <see cref="RingSpacing"/>, facing the flight.</summary>
        private static void LeaveRings(Bolt bolt, Vector3 now)
        {
            Vector3 step = now - bolt.Last;
            float length = step.magnitude;
            if (bolt.Rings == null || length < 1e-4f)
            {
                bolt.Last = now;
                return;
            }
            Vector3 dir = step / length;
            var turn = Quaternion.LookRotation(dir).eulerAngles;
            float along = RingSpacing - bolt.SinceRing;
            while (along <= length)
            {
                var p = new ParticleSystem.EmitParams
                {
                    position = bolt.Last + dir * along,
                    rotation3D = turn,
                    applyShapeToPosition = false,
                };
                bolt.Rings.Emit(p, 1);
                along += RingSpacing;
            }
            bolt.SinceRing = length - (along - RingSpacing);
            bolt.Last = now;
        }

        /// <summary>The bolt has hit: its core stops, the line, rings and trail it left fade out where they hang.</summary>
        private static void Land(GameObject fx)
        {
            foreach (ParticleSystem ps in fx.GetComponentsInChildren<ParticleSystem>())
            {
                if (ps.main.simulationSpace == ParticleSystemSimulationSpace.Local)
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                else
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
            foreach (Light light in fx.GetComponentsInChildren<Light>())
                light.enabled = false;
            foreach (TrailRenderer line in fx.GetComponentsInChildren<TrailRenderer>())
                line.emitting = false;
            Destroy(fx, 1.5f);
        }

        // ---------- The impact ----------

        private static void ShotDelegatePrefix(Shot shotResult)
        {
            try
            {
                _upgradedHit = shotResult?.Weapon != null && RaygunIds.Is(shotResult.Weapon) && RaygunPaP.IsInfused(shotResult.Weapon);
            }
            catch (System.Exception)
            {
                _upgradedHit = false;
            }
        }

        /// <summary>Our explosion's name: the gas burst instead of the game's (it has none by that name anyway).</summary>
        private static bool EmitGrenadePrefix(string ename, Vector3 position, Vector3 normal)
        {
            if (ename != ExplosionName)
                return true;
            try
            {
                GameObject prefab = Prefab(_upgradedHit ? "rg_impact_up" : "rg_impact");
                if (prefab != null)
                {
                    GameObject fx = Instantiate(prefab, position + normal * 0.1f, Quaternion.LookRotation(normal == Vector3.zero ? Vector3.up : normal));
                    fx.SetActive(true);
                    fx.AddComponent<Flash>();
                    foreach (AudioSource audio in fx.GetComponentsInChildren<AudioSource>())
                        audio.volume *= RaygunSettings.ImpactVolume.Value;
                    Destroy(fx, 4f);
                }
            }
            catch (System.Exception e)
            {
                RaygunPlugin.Log.LogWarning($"Ray Gun impact effect skipped: {e.Message}");
            }
            return false;
        }

        /// <summary>The impact's light: a flash that dies away.</summary>
        private class Flash : MonoBehaviour
        {
            private Light _light;
            private float _start, _intensity;

            private void Start()
            {
                _light = GetComponentInChildren<Light>();
                _start = Time.time;
                _intensity = _light != null ? _light.intensity : 0f;
            }

            private void Update()
            {
                if (_light == null)
                    return;
                float t = (Time.time - _start) / 0.7f;
                _light.intensity = _intensity * Mathf.Clamp01(1f - t);
                if (t >= 1f)
                    _light.enabled = false;
            }
        }
    }
}
