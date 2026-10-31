using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using EFT.InventoryLogic;
using HarmonyLib;
using PaPHideoutModule;
using UnityEngine;

namespace Raygun
{
    /// <summary>
    /// Everything the Ray Gun asks of Pack-a-Punch. PaP is optional: callers go through <see cref="RaygunPaP"/>, which
    /// only touches this class (and so only loads PaPHideoutModule.dll) when PaP is installed.
    /// </summary>
    internal static class RaygunPaPBridge
    {
        private static Func<Item, bool> _onInfused;
        private static bool _looked;

        /// <summary>
        /// Porter's X2: infused and out of the machine. PaP's records say a gun is infused the moment the server agrees,
        /// while it's still going through the machine; its camo waits until the gun is back out, and so do we.
        /// </summary>
        public static bool IsInfused(Item item)
        {
            if (!_looked)
            {
                _looked = true;
                Type camo = AccessTools.TypeByName("PaPHideoutModule.PaPCamo");
                MethodInfo on = camo == null ? null : AccessTools.Method(camo, "OnInfusedGun", new[] { typeof(Item) });
                _onInfused = on == null ? null : (Func<Item, bool>)Delegate.CreateDelegate(typeof(Func<Item, bool>), on);
            }
            return _onInfused != null ? _onInfused(item) : PaPApi.IsInfused(item);
        }

        public static bool IsOrigins(Item item) => PaPApi.IsOriginsGun(item);

        /// <summary>What the machine added to the gun's worth: its fee, which depends on the mode picked in PaP.</summary>
        public static int InfusedFeeRub => PaPApi.BonusesActive ? 500_000 : 5_000;

        public static void Setup(Harmony harmony, GameObject host)
        {
            host.AddComponent<RaygunGlowKeeper>();
            // The pickup sound is for taking the gun at the machine's prompt, not for it coming out (see RaygunGlowKeeper)
            PaPApi.WeaponUpgraded += gun => RaygunGlowKeeper.Awaiting = RaygunIds.Is(gun);
            PaPApi.RegisterName(RaygunIds.Weapon, RaygunIds.UpgradedName, "X2 Ray Gun");
            // The X2 fires faster than the plain Ray Gun
            PaPApi.RegisterBonuses(RaygunIds.Weapon, new PaPBonusSet { FireRate = 1.35f });
            // Its body is "plastic" and its bars "metal" in PaP's eyes; both are painted
            PaPApi.KeepMaterialsPlain("raygun", "rg_cell");
            PaPApi.KeepOriginsPlain("rg_cell"); // cold fusion cells never get a camo
            // Origins camo: the red body takes the blue crystal, the bars and handles the metal plating
            // Its red takes the crystal, its black and grey the metal plating (the gold print is its own overlay)
            PaPApi.SetOriginsLook("rg_plate", false); // the body's black and grey faces (cut out at build time); its red is the crystal
            PaPApi.SetOriginsLook("plastic", true);
            PaPApi.SetOriginsLook("metal", false);
            // The dial's needle, the side vents, the knob and the switch are metal too (they share the body's material)
            foreach (string part in new[] { "knob", "switch" })
                PaPApi.SetOriginsPartLook(part, false);
            AudioClip shot = Wav.Load(Path.Combine(RaygunPlugin.Dir, "sounds", "ray_shot_f.wav"));
            if (shot != null)
                PaPApi.RegisterShotSound(RaygunIds.Weapon, shot);
            else
                RaygunPlugin.Log.LogWarning("Ray Gun shot sound for Pack-a-Punch missing: PaP's own plays");

            // Every model PaP paints (or un-paints) gets its glow parts to match
            Type camo = AccessTools.TypeByName("PaPHideoutModule.PaPCamo");
            MethodInfo apply = camo == null ? null : AccessTools.Method(camo, "Apply", new[] { typeof(GameObject), typeof(Item), typeof(bool?) });
            if (apply != null)
                harmony.Patch(apply, postfix: new HarmonyMethod(typeof(RaygunPaPBridge).GetMethod(nameof(ApplyPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            else
                RaygunPlugin.Log.LogWarning("PaP camo hook not found: an upgraded Ray Gun keeps its green glow");
        }

        private static void ApplyPostfix(GameObject go, Item item, bool? force)
        {
            try
            {
                if (go == null || (item != null && !RaygunIds.Is(item)))
                    return;
                // No item: the machine's display gun going in or out, plain
                bool upgraded = item != null && (force ?? IsInfused(item));
                RaygunGlow.Set(go, upgraded, upgraded && IsOrigins(item));
                // Photo models (forced) go straight back to the pool; the rest are kept right (RaygunGlowKeeper)
                if (force == null && item != null)
                    RaygunGlowKeeper.Models[go] = item;
            }
            catch (Exception e)
            {
                RaygunPlugin.Log.LogWarning($"Ray Gun upgraded glow skipped: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Twice a second: every Ray Gun model PaP has painted gets its glow parts checked against its gun. PaP paints a
    /// model while the game is still building it, and a model re-drawn from the pool, so the one-off swap when PaP
    /// applies its camo could land on a model with no parts yet (or be undone), leaving the blue tube, the white sight
    /// and the green dial on a Porter's X2 until the gun was drawn a few times.
    /// </summary>
    internal class RaygunGlowKeeper : MonoBehaviour
    {
        public static readonly Dictionary<GameObject, Item> Models = new Dictionary<GameObject, Item>();

        /// <summary>A Ray Gun has just come out of the machine and the take prompt is up (or was).</summary>
        public static bool Awaiting;

        private float _next;
        private object _machine;
        private FieldInfo _canTake, _taken;
        private bool _wasCanTake;

        private void Update()
        {
            WatchTake();
            if (Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 0.5f;
            foreach (GameObject dead in Models.Keys.Where(go => go == null).ToList())
                Models.Remove(dead);
            foreach (var pair in Models)
            {
                try
                {
                    bool upgraded = RaygunPaPBridge.IsInfused(pair.Value);
                    RaygunGlow.Set(pair.Key, upgraded, upgraded && RaygunPaPBridge.IsOrigins(pair.Value));
                }
                catch (Exception e)
                {
                    RaygunPlugin.Log.LogWarning($"Ray Gun glow check skipped: {e.Message}");
                }
            }
        }

        // PaP keeps its take prompt's state in private fields of its plugin: the prompt is up while _canTake, and the
        // player took the gun if _takeRequested is set by the time it closes. Left alone, the machine eats the gun
        // and nothing plays.
        private float _awaitingSince;
        private bool _gaveUp;

        private void WatchTake()
        {
            if (!Awaiting)
            {
                _awaitingSince = 0f;
                _wasCanTake = false;
                return;
            }
            if (_awaitingSince == 0f)
                _awaitingSince = Time.unscaledTime;
            // Not waiting for ever: the prompt is over a minute at most
            if (_gaveUp || Time.unscaledTime - _awaitingSince > 60f)
            {
                Awaiting = false;
                return;
            }
            if (_machine == null)
            {
                // PaP's own plugin object (its fields hold the prompt's state), looked up once
                _machine = Chainloader.PluginInfos.TryGetValue("com.kennerz.paphideoutmodule", out PluginInfo info) ? info.Instance : null;
                Type plugin = _machine?.GetType();
                _canTake = plugin == null ? null : AccessTools.Field(plugin, "_canTake");
                _taken = plugin == null ? null : AccessTools.Field(plugin, "_takeRequested");
                if (_machine == null || _canTake == null || _taken == null)
                {
                    _gaveUp = true;
                    RaygunPlugin.Log.LogWarning("Pack-a-Punch take prompt not found: no pickup sound at the machine");
                    return;
                }
            }
            bool canTake = (bool)_canTake.GetValue(_machine);
            if (_wasCanTake && !canTake)
            {
                if ((bool)_taken.GetValue(_machine))
                    RaygunPickup.Play();
                Awaiting = false;
            }
            _wasCanTake = canTake;
        }
    }

    /// <summary>Pack-a-Punch hooks that are safe to call with or without PaP installed.</summary>
    internal static class RaygunPaP
    {
        // PaP 2.0.0 or newer: older ones lack the API these features call (an older one is treated as not installed)
        public static readonly bool Installed =
            Chainloader.PluginInfos.TryGetValue("com.kennerz.paphideoutmodule", out BepInEx.PluginInfo pap) && pap.Metadata.Version >= new System.Version(2, 0, 0);

        public static void Setup(Harmony harmony, GameObject host)
        {
            if (Installed)
                RaygunPaPBridge.Setup(harmony, host);
        }

        /// <summary>A Pack-a-Punched gun (Porter's X2). Never without PaP.</summary>
        public static bool IsInfused(Item item) => Installed && item != null && RaygunPaPBridge.IsInfused(item);

        /// <summary>Upgraded in PaP's Origins machine.</summary>
        public static bool IsOrigins(Item item) => Installed && item != null && RaygunPaPBridge.IsOrigins(item);

        public static int InfusedFeeRub => Installed ? RaygunPaPBridge.InfusedFeeRub : 0;
    }

    /// <summary>
    /// The Pack-a-Punched Ray Gun's glow: red tube, red dial and a red sight. Its "..._up" materials ride in the
    /// weapon bundle on a switched-off renderer (rg_upgraded_materials); they're swapped in by name, and back out.
    /// </summary>
    internal static class RaygunGlow
    {
        private const string Holder = "rg_upgraded_materials";
        private const string Up = "_up";

        private const string PlainHolder = "rg_plain_materials";

        /// <summary>A material's name without the " (Instance)" the game adds when it copies one.</summary>
        private static string Bare(string name) => name.EndsWith(" (Instance)") ? name.Substring(0, name.Length - " (Instance)".Length) : name;

        private static Dictionary<string, Material> Named(Renderer holder) => holder == null
            ? new Dictionary<string, Material>()
            : holder.sharedMaterials.Where(m => m != null).GroupBy(m => m.name).ToDictionary(g => g.Key, g => g.First());

        // On a gun from PaP's Origins machine the tube and the sight glow deep red (as in BO2's Origins): the tube is a
        // copy of the plain tube (its moving plasma, which RaygunPlasma animates by name) turned deep red, the sight a
        // deep red copy of the upgraded one. Named with this tag
        private const string OriginsTag = " (ori)";
        private static readonly string[] OriginsGlowParts = { "power", "reticle" };
        private static readonly Dictionary<Material, Material> OriginsCopies = new Dictionary<Material, Material>();

        public static void Set(GameObject model, bool upgraded, bool origins = false)
        {
            Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
            Renderer holder = renderers.FirstOrDefault(r => r.name == Holder);
            if (holder == null)
                return; // not a Ray Gun model
            // Both sets ride in the model itself, so a pooled model can always be put back either way
            Dictionary<string, Material> ups = Named(holder);
            Dictionary<string, Material> plains = Named(renderers.FirstOrDefault(r => r.name == PlainHolder));
            foreach (Renderer r in renderers)
            {
                if (r == holder || r.name == PlainHolder)
                    continue;
                Material[] mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    Material m = mats[i];
                    if (m == null)
                        continue;
                    string current = Bare(m.name);
                    // The dial keeps its own colours (needle and meter)
                    if (current.Contains("meter") || current.Contains("needle"))
                        continue;
                    string name = current.EndsWith(OriginsTag) ? current.Substring(0, current.Length - OriginsTag.Length) : current;
                    bool isUp = name.EndsWith(Up);
                    string plainName = isUp ? name.Substring(0, name.Length - Up.Length) : name;
                    Material want = null;
                    if (upgraded && ups.TryGetValue(plainName + Up, out Material up))
                        want = origins && OriginsGlowParts.Any(plainName.Contains)
                            ? OriginsCopy(up, plains.TryGetValue(plainName, out Material plainTube) && plainName.Contains("power") ? plainTube : null)
                            : up;
                    else if (!upgraded && isUp && plains.TryGetValue(plainName, out Material plain))
                        want = plain;
                    // Compared by name: the game may draw its own copy of ours, which must not be swapped every check
                    if (want != null && want.name != current)
                    {
                        mats[i] = want;
                        changed = true;
                    }
                }
                if (changed)
                    r.sharedMaterials = mats;
            }
        }

        /// <param name="plain">The plain tube, whose moving plasma the Origins tube keeps; null for the sight.</param>
        private static Material OriginsCopy(Material up, Material plain)
        {
            if (OriginsCopies.TryGetValue(up, out Material done) && done != null)
                return done;
            var copy = new Material(plain != null ? plain : up) { name = up.name + OriginsTag };
            copy.SetColor("_Color", DeepRed(copy.GetColor("_Color"), 0.5f));
            copy.SetColor("_EmissionColor", plain != null ? DeepRed(copy.GetColor("_EmissionColor"), 1f) : new Color(1.6f, 0.05f, 0.03f, 1f));
            copy.EnableKeyword("_EMISSION");
            OriginsCopies[up] = copy;
            return copy;
        }

        /// <summary>The same brightness (times <paramref name="scale"/>) as a deep red, same alpha.</summary>
        private static Color DeepRed(Color c, float scale)
        {
            float b = Mathf.Max(c.r, c.g, c.b) * scale;
            return new Color(b, b * 0.04f, b * 0.025f, c.a);
        }
    }

    /// <summary>A PCM WAV file (8/16/24/32-bit integer or 32-bit float) as an AudioClip.</summary>
    internal static class Wav
    {
        public static AudioClip Load(string path)
        {
            if (!File.Exists(path))
                return null;
            byte[] d = File.ReadAllBytes(path);
            int channels = 1, rate = 44100, bits = 16, format = 1, pos = 12;
            while (pos + 8 <= d.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(d, pos, 4);
                int size = BitConverter.ToInt32(d, pos + 4);
                if (id == "fmt ")
                {
                    format = BitConverter.ToInt16(d, pos + 8);
                    channels = BitConverter.ToInt16(d, pos + 10);
                    rate = BitConverter.ToInt32(d, pos + 12);
                    bits = BitConverter.ToInt16(d, pos + 22);
                }
                else if (id == "data")
                {
                    int bytes = bits / 8, count = Math.Min(size, d.Length - pos - 8) / bytes;
                    var samples = new float[count];
                    for (int i = 0, o = pos + 8; i < count; i++, o += bytes)
                    {
                        samples[i] = format == 3 ? BitConverter.ToSingle(d, o)
                            : bits == 8 ? (d[o] - 128) / 128f
                            : bits == 16 ? BitConverter.ToInt16(d, o) / 32768f
                            : bits == 24 ? ((d[o] << 8 | d[o + 1] << 16 | d[o + 2] << 24) >> 8) / 8388608f
                            : BitConverter.ToInt32(d, o) / 2147483648f;
                    }
                    AudioClip clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), count / channels, channels, rate, false);
                    clip.SetData(samples, 0);
                    return clip;
                }
                pos += 8 + size + (size & 1);
            }
            return null;
        }
    }
}
