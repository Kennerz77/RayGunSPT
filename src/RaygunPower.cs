using System.Collections.Generic;
using UnityEngine;

namespace Raygun
{
    /// <summary>
    /// The Ray Gun in the hands lights up only when it's switched on: the tube, the dial's backlight and the sight
    /// glow while the power switch (tag_switch_animate) sits where the idle has it ("rg_switch_on" in the model, the
    /// import's copy of that turn), and are dark while it's off, as at the start of the first raise, where the left
    /// thumb flicks it on. The glow comes up quickly once it's on. Per gun, through property blocks: the shared
    /// materials (and every other Ray Gun) keep glowing.
    /// </summary>
    internal static class RaygunPower
    {
        private const string SwitchOn = "rg_switch_on", Switch = "tag_switch_animate";
        private static readonly string[] GlowParts = { "_power", "_meter", "_reticle" };
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

        // Turn (degrees) off the "on" turn still counted as on; the switch flicks through 30
        private const float OnWithin = 8f;
        // Glow per second coming up (and going down)
        private const float Rise = 7f, Fall = 12f;

        private static Transform _root, _switch, _on;
        private static readonly List<Renderer> Renderers = new List<Renderer>();
        private static readonly List<Material> Shared = new List<Material>();
        private static readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();
        private static readonly MaterialPropertyBlock Empty = new MaterialPropertyBlock();
        private static float _level = 1f;
        private static bool _dimmed;
        private static int _frame = -1;

        /// <summary>Once a frame, for the Ray Gun rig in the hands.</summary>
        public static void Update(Transform root)
        {
            if (_frame == Time.frameCount)
                return;
            _frame = Time.frameCount;
            if (root != _root)
                Find(root);
            if (_switch == null || _on == null)
                return;

            bool on = Quaternion.Angle(_switch.localRotation, _on.localRotation) <= OnWithin;
            _level = Mathf.MoveTowards(_level, on ? 1f : 0f, Time.deltaTime * (on ? Rise : Fall));
            if (_level >= 1f)
            {
                if (_dimmed)
                    Apply(1f);
                _dimmed = false;
                return;
            }
            _dimmed = true;
            Apply(_level * _level);
        }

        private static void Find(Transform root)
        {
            _root = root;
            _switch = _on = null;
            Renderers.Clear();
            _level = 1f;
            _dimmed = false;
            // The gun's sounds at the F12 levels
            RaygunSounds.Register(root.GetComponentInChildren<WeaponSoundPlayer>(true));
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == Switch)
                    _switch = t;
                else if (t.name == SwitchOn)
                    _on = t;
            }
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r.name == "rg_upgraded_materials" || r.name == "rg_plain_materials")
                    continue;
                r.GetSharedMaterials(Shared);
                foreach (Material m in Shared)
                {
                    if (m != null && IsGlow(m.name))
                    {
                        Renderers.Add(r);
                        break;
                    }
                }
            }
        }

        private static bool IsGlow(string name)
        {
            foreach (string part in GlowParts)
                if (name.Contains(part))
                    return true;
            return false;
        }

        /// <summary>Glow parts at <paramref name="level"/> of their material's own glow; 1 hands them back to it.</summary>
        private static void Apply(float level)
        {
            foreach (Renderer r in Renderers)
            {
                if (r == null)
                    continue;
                r.GetSharedMaterials(Shared);
                for (int i = 0; i < Shared.Count; i++)
                {
                    Material m = Shared[i];
                    if (m == null || !IsGlow(m.name))
                        continue;
                    if (level >= 1f)
                    {
                        r.SetPropertyBlock(Empty, i);
                        continue;
                    }
                    Block.Clear();
                    Block.SetColor(EmissionColor, m.GetColor(EmissionColor) * level);
                    r.SetPropertyBlock(Block, i);
                }
            }
        }
    }
}
