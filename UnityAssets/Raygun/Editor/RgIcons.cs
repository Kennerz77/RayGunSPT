using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Raygun.Editor
{
    /// <summary>
    /// Inventory pictures, rendered here instead of by the game's icon studio (which washes custom models out white):
    /// the Ray Gun, the Pack-a-Punched one in PaP's own camo, and the cold fusion cell. Written as transparent PNGs
    /// to icons/ in the project root; the client plugin ships them and swaps them into the cells.
    /// </summary>
    public static class RgIcons
    {
        public static string IconsDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../../icons"));

        // PaP's camo albedo (read only, never changed) and the tiling PaP paints it with
        private const string CamoAlbedo = @"C:\Git\PaPHideoutModule\UnityProject\Assets\PaP\Camo\pap_camo_albedo.png";
        private const float CamoTiling = 1f;
        private const float CamoEmission = 0.06f;

        private const int Width = 1024;

        public static void RenderAll()
        {
            Directory.CreateDirectory(IconsDir);
            var rig = new List<Object>();
            try
            {
                rig.AddRange(Lights());
                RenderGun("raygun", camo: null);
                Texture2D camo = LoadCamo();
                if (camo != null)
                {
                    rig.Add(camo);
                    RenderGun("raygun_pap", camo);
                }
                else
                {
                    Debug.LogWarning($"[Raygun] PaP camo not found at {CamoAlbedo}; no Pack-a-Punched icon");
                }
                RenderCell("cell");
            }
            finally
            {
                foreach (Object o in rig)
                    Object.DestroyImmediate(o);
            }
        }

        private static IEnumerable<Object> Lights()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.42f, 0.45f);
            foreach (var (rotation, intensity) in new[] { (Quaternion.Euler(30f, 60f, 0f), 1.0f), (Quaternion.Euler(-20f, -120f, 0f), 0.45f) })
            {
                var go = new GameObject("icon_light");
                Light light = go.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = intensity;
                go.transform.rotation = rotation;
                yield return go;
            }
        }

        private static Texture2D LoadCamo()
        {
            if (!File.Exists(CamoAlbedo))
                return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
            tex.LoadImage(File.ReadAllBytes(CamoAlbedo));
            return tex;
        }

        /// <summary>
        /// The gun from its left side, muzzle to the left as EFT pictures its guns. Laid out in CoD space through the
        /// model's root (tag_weapon): the barrel (CoD +X) along +Z, up (CoD +Z) along +Y, its left (CoD +Y) along -X.
        /// The fresh cell (hidden inside the gun at rest) is left out.
        /// </summary>
        private static void RenderGun(string name, Texture2D camo)
        {
            GameObject go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(RgImporter.WeaponPrefabPath));
            try
            {
                CodFormats.CodModel model = RgBo3.LoadModel();
                Transform gunRoot = go.GetComponentsInChildren<Transform>(true).First(t => t.name == model.Bones[0].Name);
                gunRoot.SetParent(null, true);
                gunRoot.position = Vector3.zero;
                gunRoot.rotation = RgSpace.Rot(model.Bones[0].Rest);
                Object.DestroyImmediate(go);
                go = gunRoot.gameObject;
                Transform fresh = go.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == RgImporter.CellBones[1]);
                if (fresh != null)
                    fresh.gameObject.SetActive(false);
                Dress(go, camo);
                Shoot(go, name, forward: Vector3.right, up: Vector3.up);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// The cell at an angle: lying diagonally across the picture, turned so its printed ATOMO label faces the
        /// camera, and smaller in its cell with room around it.
        /// </summary>
        private static void RenderCell(string name)
        {
            GameObject go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(RgImporter.CellPrefabPath));
            try
            {
                Dress(go, null);
                MeshFilter mf = go.GetComponentInChildren<MeshFilter>();
                Vector3 size = mf.sharedMesh.bounds.size;
                Vector3[] axes = { Vector3.right, Vector3.up, Vector3.forward };
                float[] lengths = { size.x, size.y, size.z };
                int[] order = Enumerable.Range(0, 3).OrderBy(i => lengths[i]).ToArray();
                Vector3 along = mf.transform.TransformDirection(axes[order[2]]);
                Vector3 side = mf.transform.TransformDirection(axes[order[0]]);
                // Seen from the side the label faces (the cell's -side here), turned a little about the cell and tipped
                // so a little of its end shows
                Vector3 view = Quaternion.AngleAxis(LabelTurn, along) * side;
                view = Quaternion.AngleAxis(18f, Vector3.Cross(view, along).normalized) * view;
                Vector3 forward = view.normalized;
                // Diagonal: the cell's long axis 35 degrees up from the picture's horizontal
                Vector3 right = Quaternion.AngleAxis(-35f, forward) * Vector3.ProjectOnPlane(along, forward).normalized;
                Shoot(go, name, forward, Vector3.Cross(forward, right), margin: 1.35f);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>Degrees about the cell's long axis that bring its printed label round to the camera.</summary>
        private const float LabelTurn = 180f;

        /// <summary>Copies of the model's materials, in camo where PaP would paint it.</summary>
        private static void Dress(GameObject go, Texture2D camo)
        {
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.sharedMaterials = r.sharedMaterials.Select(m =>
                {
                    if (m == null)
                        return null;
                    var copy = new Material(m);
                    // As PaPCamo.ShouldPaint: nothing from the alpha-test queue up, nothing without a colour map
                    if (camo != null && m.renderQueue < 2450 && m.HasProperty("_MainTex") && m.GetTexture("_MainTex") != null)
                    {
                        copy.SetTexture("_MainTex", camo);
                        copy.SetTextureScale("_MainTex", Vector2.one * CamoTiling);
                        copy.color = Color.white;
                        copy.SetTexture("_EmissionMap", camo);
                        copy.SetTextureScale("_EmissionMap", Vector2.one * CamoTiling);
                        copy.SetColor("_EmissionColor", new Color(CamoEmission, CamoEmission, CamoEmission));
                        copy.EnableKeyword("_EMISSION");
                    }
                    return copy;
                }).ToArray();
            }
        }

        /// <summary>An orthographic shot framing the model tightly, transparent around it.</summary>
        private static void Shoot(GameObject go, string name, Vector3 forward, Vector3 up, float margin = 1.04f)
        {
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled).ToArray();
            Quaternion view = Quaternion.LookRotation(forward, up);
            Quaternion inv = Quaternion.Inverse(view);
            // Bounds in the camera's frame: corners of every renderer's world bounds
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (Renderer r in renderers)
            {
                Bounds wb = r.bounds;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = wb.center + Vector3.Scale(wb.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                    Vector3 p = inv * corner;
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            Vector3 centre = view * ((min + max) / 2f);
            float w = max.x - min.x, h = max.y - min.y;
            w *= margin;
            h *= margin;
            int width = Width, height = Mathf.Max(64, Mathf.RoundToInt(Width * h / w));

            var camGo = new GameObject("icon_cam");
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
            try
            {
                Camera cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = h / 2f;
                cam.aspect = (float)width / height;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.transform.rotation = view;
                cam.transform.position = centre - forward.normalized * ((max.z - min.z) + 1f);
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = (max.z - min.z) * 2f + 4f;
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(IconsDir, $"{name}.png"), tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                Debug.Log($"[Raygun] Icon {name}.png: {width}x{height}");
            }
            finally
            {
                RenderTexture.active = null;
                Object.DestroyImmediate(camGo);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }
    }
}
