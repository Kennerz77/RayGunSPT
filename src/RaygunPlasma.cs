using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Raygun
{
    /// <summary>
    /// The cell tube's plasma moves: its glow (BO3's grey noise, tinted green in the bundle) slides along and around
    /// the tube and pulses. Every Ray Gun shares the tube's material, so moving that material moves every tube at once;
    /// copies the game makes of it (named "... (Instance)") are found too.
    /// </summary>
    internal class RaygunPlasma : MonoBehaviour
    {
        private const string Material = "mtl_wpn_t7_zmb_raygun_power";
        // UV per second along and around the tube, and the pulse
        private static readonly Vector2 Scroll = new Vector2(0.11f, 0.045f);
        private const float PulseDepth = 0.18f, PulseHz = 1.1f;
        // A second, slower wobble across the tube so the drift never looks like a straight conveyor
        private const float WobbleAmount = 0.06f, WobbleHz = 0.23f;

        private readonly List<(Material mat, Color glow)> _tubes = new List<(Material, Color)>();
        private float _nextScan;

        private void Update()
        {
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 2f;
                Scan();
            }
            if (_tubes.Count == 0)
                return;
            float t = Time.time;
            var offset = new Vector2(Scroll.x * t, Scroll.y * t + WobbleAmount * Mathf.Sin(t * WobbleHz * 2f * Mathf.PI));
            float pulse = 1f + PulseDepth * Mathf.Sin(t * PulseHz * 2f * Mathf.PI);
            for (int i = _tubes.Count - 1; i >= 0; i--)
            {
                var (mat, glow) = _tubes[i];
                if (mat == null)
                {
                    _tubes.RemoveAt(i);
                    continue;
                }
                // Standard samples the emission map with the main texture's tiling and offset
                mat.SetTextureOffset("_MainTex", offset);
                mat.SetColor("_EmissionColor", glow * pulse);
            }
        }

        private void Scan()
        {
            foreach (Material mat in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (mat == null || !mat.name.StartsWith(Material) || _tubes.Any(x => x.mat == mat))
                    continue;
                _tubes.Add((mat, mat.GetColor("_EmissionColor")));
            }
        }
    }
}
