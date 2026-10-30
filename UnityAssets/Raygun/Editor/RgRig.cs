using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raygun.CodFormats;
using UnityEngine;

namespace Raygun.Editor
{
    /// <summary>Paths and conversions shared by the importer, the retarget and the controller builder.</summary>
    public static class RgPaths
    {
        // Only the arms rig comes from here: the BO1 arms-rig port (am_t5_thundergun: CoD's standard
        // viewmodel arm bones, the hierarchy above the arms that Greyhound's SEAnims leave out)
        public const string PortRoot = @"C:\Git\2_References\t5_thundergun 2\t5_thundergun";
        public static readonly string ModelDir = Directory.GetFiles(PortRoot + @"\model_export", "am_t5_thundergun.xmodel_bin",
            SearchOption.AllDirectories).Select(f => Path.GetDirectoryName(f)).FirstOrDefault();
        // BO3's Ray Gun sounds, pulled from the game's sound banks by C:\Git\99_Tools\bo3_sab_extract.py
        public static string Bo3SoundDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../../sounds_bo3"));

        public const string Root = "Assets/Raygun";
        public const string Generated = Root + "/Generated";
        public const string Textures = Root + "/Textures";
        public const string Materials = Root + "/Materials";
        public const string Audio = Root + "/Audio";
        public const string Clips = Root + "/Clips";

        public const string WeaponName = "weapon_raygun";
        public const string ContainerName = "weapon_raygun_container";
        public const string CellName = "mag_raygun_cell";

        /// <summary>Sound replacements, named after the sound they replace (see RgImporter.ImportSounds).</summary>
        public static string CustomSoundDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../../sounds_custom"));

        /// <summary>
        /// The vanilla gun whose rig, controller, events and components the Ray Gun is built on: the sawed-off MP-43
        /// (two chambers loaded like shells). Dumped by C:\Git\99_Tools\eft_weapon_dump.py into reference/mr43_*.json.
        /// </summary>
        public const string BaseGun = "mr43";
        /// <summary>Owner names in the components dump: the container and the first-person model.</summary>
        public const string BaseContainer = "mr43_sawed_off_12g_container";
        public const string BaseModel = "mr43_sawed_off_12g_model.generated";

        public static string ReferenceDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../../reference"));
        public static string LogsDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../../logs"));
        public static string Reference(string file) => Path.Combine(ReferenceDir, file);

        public static void EnsureDirs()
        {
            foreach (string dir in new[] { Generated, Textures, Materials, Audio, Clips })
                Directory.CreateDirectory(dir);
            Directory.CreateDirectory(LogsDir);
        }
    }

    /// <summary>
    /// CoD space (right-handed, Z up, X forward, Y left, inches) to Unity prefab space (left-handed, Y up, Z forward,
    /// metres). The mapping mirrors one axis; a bone's rotation is built from its mapped Y and Z axes so its X axis
    /// still maps to the mapped X (the proof: for a mirror M, Ma × Mb = −M(a × b)).
    /// </summary>
    public static class RgSpace
    {
        public const float Scale = 0.0254f;

        public static Vector3 Dir(CodVec v) => new Vector3(-v.Y, v.Z, v.X);
        public static Vector3 Pos(CodVec v) => Dir(v) * Scale;
        public static Quaternion Rot(CodPose p) => Quaternion.LookRotation(-Dir(p.AxisZ), Dir(p.AxisY));

        /// <summary>The swing-free part of <paramref name="q"/> about <paramref name="axis"/> (unit).</summary>
        public static Quaternion Twist(Quaternion q, Vector3 axis)
        {
            Vector3 v = new Vector3(q.x, q.y, q.z);
            Vector3 p = Vector3.Dot(v, axis) * axis;
            var twist = new Quaternion(p.x, p.y, p.z, q.w);
            float n = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
            return n < 1e-6f ? Quaternion.identity : new Quaternion(twist.x / n, twist.y / n, twist.z / n, twist.w / n);
        }

        /// <summary>Unity's path hash for avatar masks: CRC32 of the transform path relative to the Animator.</summary>
        public static uint Crc32(string text)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(text))
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return ~crc;
        }
    }

    /// <summary>
    /// Rebuilds the EFT weapon rig (arms, camera, weapon root, markers, chambers and slots) from the vanilla sawed-off
    /// MP-43's prefab, dumped to reference/mr43_rig.json. The bones and their rest pose are standard across EFT
    /// weapons, so our clips can drive the player's arms like any vanilla gun's. Its own meshes and effects are left out.
    /// </summary>
    public static class RgRig
    {
        public static GameObject Build(string name, Dictionary<string, Transform> bones)
        {
            object rig = MiniJson.Parse(File.ReadAllText(RgPaths.Reference($"{RgPaths.BaseGun}_rig.json")));
            var root = new GameObject(name);
            foreach (object child in rig.Get("children").Arr())
                Add(child, root.transform, bones);
            return root;
        }

        private static void Add(object node, Transform parent, Dictionary<string, Transform> bones)
        {
            string name = node.Str("name");
            List<object> comps = node.Get("comps").Arr();
            // The vanilla gun's geometry, muzzle effect clones and its stock prop aren't part of the rig
            if (comps.Any(c => (string)c == "MeshFilter") || name.EndsWith("(Clone)") || name == "prop")
                return;

            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = V3(node.Get("pos"));
            t.localRotation = Q(node.Get("rot"));
            t.localScale = V3(node.Get("scale"));
            t.gameObject.SetActive(node.Bool("active"));
            t.gameObject.layer = node.Int("layer");
            bones[name] = t;
            foreach (object child in node.Get("children").Arr())
                Add(child, t, bones);
        }

        public static Vector3 V3(object a)
        {
            List<object> l = a.Arr();
            return new Vector3((float)(double)l[0], (float)(double)l[1], (float)(double)l[2]);
        }

        public static Quaternion Q(object a)
        {
            List<object> l = a.Arr();
            return new Quaternion((float)(double)l[0], (float)(double)l[1], (float)(double)l[2], (float)(double)l[3]);
        }

        public static string PathOf(Transform t, Transform root)
        {
            var names = new List<string>();
            for (Transform x = t; x != null && x != root; x = x.parent)
                names.Add(x.name);
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
