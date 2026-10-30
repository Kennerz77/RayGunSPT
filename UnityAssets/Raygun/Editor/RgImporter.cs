using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EFT;
using Raygun.CodFormats;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Raygun.Editor
{
    /// <summary>
    /// Builds the Ray Gun's prefabs from BO3's HD model and viewmodel animations (Greyhound exports; no Blender/FBX):
    /// weapon_raygun (EFT's weapon rig with the gun model, BO3 clips and the rebuilt Saiga controller),
    /// weapon_raygun_container (WeaponPrefab, sound player and the other EFT components the game reads), and the
    /// cold fusion cell magazine (the cell mesh, split off the gun: EFT shows the magazine item in mod_magazine).
    /// </summary>
    public static class RgImporter
    {
        public static string WeaponPrefabPath => $"{RgPaths.Root}/{RgPaths.WeaponName}.prefab";
        public static string ContainerPrefabPath => $"{RgPaths.Root}/{RgPaths.ContainerName}.prefab";
        public static string CellPrefabPath => $"{RgPaths.Root}/{RgPaths.CellName}.prefab";
        public static string ControllerPath => $"{RgPaths.Generated}/{RgPaths.WeaponName}.controller";

        /// <summary>Gun model faces on these bones are the cells (the one in the gun, and the fresh one during a reload).</summary>
        public static readonly string[] CellBones = { "tag_battery_1_animate", "tag_battery_2_animate" };

        private const string Anims = "vm_raygun_t7_";

        /// <summary>
        /// Which BO3 animation (and frame range, 30 fps) plays in each of the sawed-off MP-43's Hands states. BO3's one
        /// reload is split at its own notes (open 13, old cell out 31, new cell in 55, close 102, end 115) over the
        /// break-action's pieces: START opens, THROW tosses the old cells (EFT ejects them on its ShellEject event),
        /// IN seats the new ones, END closes. The first raise is the inspect. States without a BO3 counterpart use the idle.
        /// </summary>
        private static readonly Dictionary<string, (string anim, int first, int last, bool loop)> HandsMap =
            new Dictionary<string, (string, int, int, bool)>
            {
                // EFT lets the next shot or reload start only as the idle loop comes round: a short loop keeps the
                // gun responsive. BO3's full breathing idle plays as the long-idle fidget.
                ["IDLE"] = (Anims + "idle", 0, 9, true),
                ["TOO IDLE"] = (Anims + "idle", 0, 999, true),
                ["SPAWN"] = (Anims + "pullout", 0, 0, false),
                // Every draw is BO3's first raise (the cell pushed home, with its sound)
                ["OUT TO IDLE"] = (Anims + "first_raise", 0, 999, false),
                ["OUT TO IDLE ARM"] = (Anims + "first_raise", 0, 999, false),
                ["IDLE TO OUT"] = (Anims + "putaway", 0, 999, false),
                // Padded at the start: EFT starts the shot state a fifth of the way in, which skipped the fins' spin
                ["FIRE"] = (FirePadded, 0, 999, false),
                // An empty trigger is only the click: no shot animation, no kick
                ["DRY FIRE"] = (Anims + "idle", 0, 4, false),
                // Reload: open, toss the old cells, seat the new ones, close
                ["RELOAD START"] = (Anims + "reload", 0, 13, false),
                ["RELOAD THROW"] = (Anims + "reload", 13, 43, false),
                ["RELOAD IN"] = (Anims + "reload", 43, 102, false),
                ["RELOAD END"] = (Anims + "reload", 102, 999, false),
                ["HAND IN"] = (Anims + "reload", 13, 20, false),
                // Unloading (Discharge): the cells come out
                ["RELOAD OUT"] = (Anims + "reload", 13, 43, false),
                ["RELOAD_OUT_ALL"] = (Anims + "reload", 13, 43, false),
                // Loading from the inventory: open with the hands off, seat, close, back down
                ["INV TO RELOAD"] = (Anims + "pullout", 0, 10, false),
                ["OPEN CHAMBER HANDS OFF"] = (Anims + "reload", 0, 13, false),
                ["RELOAD CLOSE CHAMBER"] = (Anims + "reload", 102, 999, false),
                ["RELOAD INVENTORY END"] = (Anims + "putaway", 0, 999, false),
                // Arms down for the inventory, and back up
                ["OPEN INVENTORY"] = (Anims + "putaway", 0, 999, false),
                ["IN INVENTORY"] = (Anims + "putaway", 999, 999, false),
                ["CLOSE INVENTORY"] = (Anims + "pullout", 0, 999, false),
                ["PATROL IN"] = (Anims + "sprint_in", 0, 999, false),
                ["PATROL"] = (Anims + "sprint_loop", 0, 999, true),
                ["PATROL OUT"] = (Anims + "sprint_out", 0, 999, false),
                // Inspect: the gun turned in the hand to show both sides (InspectStates). The cell check built from the
                // reload (CellInspect) wasn't liked yet (2026-10-27): kept, not used
                ["LOOK"] = (Anims + "idle", 0, 140, false),
                ["CHECK"] = (Anims + "idle", 0, 999, true),
            };

        /// <summary>States that play the built inspect (RgRetarget.InspectPose) over their clip.</summary>
        private static readonly HashSet<string> InspectStates = new HashSet<string> { "LOOK" };

        /// <summary>The fire animation with still frames in front (see HandsMap's FIRE).</summary>
        private const string FirePadded = "rg_firepadded";
        private const int FirePad = 4;

        /// <summary>How many times slower than BO3 the fins (tag_gear_animate) spin back on a shot.</summary>
        private const float FinSlowdown = 3.5f;

        /// <summary><paramref name="anim"/> with its first frame held for <paramref name="pad"/> frames in front.</summary>
        private static CodAnim Padded(CodAnim anim, int pad)
        {
            var frames = Enumerable.Repeat(anim.Frames[0], pad).Concat(anim.Frames).ToList();
            return new CodAnim { FrameRate = anim.FrameRate, Parts = anim.Parts, Frames = frames };
        }

        // ---------- The cell check (inspect) ----------

        /// <summary>The built inspect's animation name (it's assembled from the reload, not loaded).</summary>
        private const string CellInspect = "rg_cellinspect";

        // Reload frames: the lid is open from 13 to 102; the fresh cell is in the hand around 52 and seated by 102
        private const int LidOpen = 13, CellSeated = 102, CellInHand = 52, LidClosed = 115;
        private const int Blend = 8, LookFrames = 50;

        /// <summary>
        /// The cell check, from BO3's reload: open the lid, the second cell comes up out of the gun into the left hand
        /// (the cell-in movement played backwards), the hand turns it over to look at it, it goes back in, the lid
        /// closes. The first cell stays seated throughout; the old cell the reload throws away is never seen.
        /// Returns the animation, its frame extras and its sound notes (frame, our sound event).
        /// </summary>
        private static (CodAnim anim, Func<int, RgRetarget.Extras> extras, List<(int, string)> notes) BuildCellInspect(CodAnim reload)
        {
            var frames = new List<CodPose[]>();
            var notes = new List<(int, string)>();
            for (int f = 0; f <= LidOpen; f++)
                frames.Add(reload.Frames[f]);
            notes.Add((LidOpen - 2, "RgOpen"));
            // Hands from the open lid over to where they reach in for the cell
            for (int b = 1; b <= Blend; b++)
                frames.Add(LerpFrame(reload.Frames[LidOpen], reload.Frames[CellSeated], b / (float)(Blend + 1)));
            int outStart = frames.Count;
            notes.Add((outStart + 4, "RgCellOut"));
            for (int f = CellSeated; f >= CellInHand; f--)
                frames.Add(reload.Frames[f]);
            int lookStart = frames.Count;
            for (int k = 0; k < LookFrames; k++)
                frames.Add(reload.Frames[CellInHand]);
            int inStart = frames.Count;
            for (int f = CellInHand; f <= CellSeated; f++)
                frames.Add(reload.Frames[f]);
            notes.Add((inStart + (80 - CellInHand), "RgCellIn"));
            notes.Add((frames.Count - 1, "RgClose"));
            for (int f = CellSeated + 1; f <= Math.Min(LidClosed, reload.Frames.Count - 1); f++)
                frames.Add(reload.Frames[f]);
            var anim = new CodAnim { FrameRate = reload.FrameRate, Parts = reload.Parts, Frames = frames };

            var hold = new HashSet<string> { CellBones[0] };
            var group = new HashSet<string> { CellBones[1] };
            RgRetarget.Extras Extras(int i)
            {
                var e = new RgRetarget.Extras { Hold = hold, LeftGroup = group };
                // Looking it over: brought up towards the eyes, turned one way and the other
                float t = Mathf.InverseLerp(lookStart - 10, inStart + 10, i);
                float up = Mathf.Sin(Mathf.Clamp01(t) * Mathf.PI);
                float turn = Mathf.Sin(Mathf.Clamp01(Mathf.InverseLerp(lookStart, inStart, i)) * Mathf.PI * 2f);
                e.LeftMove = new Vector3(0.03f, 0.05f, -0.05f) * up;
                e.LeftTwist = Quaternion.AngleAxis(40f * turn, Vector3.up) * Quaternion.AngleAxis(-25f * up, Vector3.right);
                return e;
            }
            return (anim, Extras, notes);
        }

        /// <summary>A frame part-way between two (positions lerped, axes slerped).</summary>
        private static CodPose[] LerpFrame(CodPose[] a, CodPose[] b, float t)
        {
            var result = new CodPose[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                Quaternion qa = Rot(a[i]), qb = Rot(b[i]), q = Quaternion.Slerp(qa, qb, t);
                Vector3 pa = new Vector3(a[i].Pos.X, a[i].Pos.Y, a[i].Pos.Z), pb = new Vector3(b[i].Pos.X, b[i].Pos.Y, b[i].Pos.Z);
                Vector3 pos = Vector3.Lerp(pa, pb, t), x = q * Vector3.right, y = q * Vector3.up, z = q * Vector3.forward;
                result[i] = new CodPose
                {
                    Pos = new CodVec { X = pos.x, Y = pos.y, Z = pos.z },
                    AxisX = new CodVec { X = x.x, Y = x.y, Z = x.z },
                    AxisY = new CodVec { X = y.x, Y = y.y, Z = y.z },
                    AxisZ = new CodVec { X = z.x, Y = z.y, Z = z.z },
                };
            }
            return result;
        }

        private static Quaternion Rot(CodPose p) =>
            Quaternion.LookRotation(new Vector3(p.AxisZ.X, p.AxisZ.Y, p.AxisZ.Z), new Vector3(p.AxisY.X, p.AxisY.Y, p.AxisY.Z));

        /// <summary>
        /// The first raise, with the gun carried a little further out while the cell is pushed home, so the left hand
        /// reaches it without bending back at the wrist; smoothly out and back.
        /// </summary>
        private static RgRetarget.Extras RaisePush(int i, int frames)
        {
            float t = i / (float)Mathf.Max(1, frames - 1);
            float w = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.05f, 0.3f, t)) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.65f, 0.95f, t)));
            return new RgRetarget.Extras { Push = new Vector3(0.01f, 0.01f, 0.07f) * w };
        }

        /// <summary>Playback speed of some Hands states (none yet: BO3's own timings).</summary>
        private static readonly Dictionary<string, float> StateSpeeds = new Dictionary<string, float>
        {
            // The shot plays at BO3's own pace (as first ported): the fins' spin and the kick in full
        };

        /// <summary>
        /// Frames over which a state's clip eases in from / out to the exact idle pose, where it starts or ends away
        /// from it (the reload, the draws), so states hand over without a hitch.
        /// </summary>
        private static readonly Dictionary<string, (int easeIn, int easeOut)> Ease = new Dictionary<string, (int, int)>
        {
            // The short idle loop ends back on its first frame, so it loops without a jump
            ["IDLE"] = (0, 9),
            ["RELOAD START"] = (6, 0),
            ["OPEN CHAMBER HANDS OFF"] = (6, 0),
            ["RELOAD END"] = (0, 10),
            ["RELOAD CLOSE CHAMBER"] = (0, 10),
            ["OUT TO IDLE"] = (0, 12),
            ["OUT TO IDLE ARM"] = (0, 12),
            ["CLOSE INVENTORY"] = (0, 10),
            ["PATROL IN"] = (4, 0),
            ["PATROL OUT"] = (0, 8),
            ["LOOK"] = (0, 10),
            ["FIRE"] = (0, 4),
        };

        /// <summary>
        /// BO3's sound notes ("sndnt#alias") and the sound each plays: (our event, clip). States playing a range of an
        /// animation with notes get exactly those sounds instead of the Saiga's.
        /// </summary>
        private static readonly Dictionary<string, (string sound, string clip)> SoundAliases = new Dictionary<string, (string, string)>
        {
            ["wpn_ray_1straise"] = ("RgRaise", "wpn_ray_1straise"),
            ["wpn_ray_reload_open"] = ("RgOpen", "wpn_ray_reload_open"),
            ["wpn_ray_reload_battery_out"] = ("RgCellOut", "wpn_ray_reload_battery_out"),
            ["wpn_ray_reload_battery_in"] = ("RgCellIn", "wpn_ray_reload_battery"),
            ["wpn_ray_reload_close"] = ("RgClose", "wpn_ray_reload_close"),
        };

        /// <summary>Sounds BO3's notes don't have, per animation (frame, our event, clip).</summary>
        private static readonly Dictionary<string, (int frame, string sound, string clip)[]> ExtraNotes = new Dictionary<string, (int, string, string)[]>();

        /// <summary>Sound notes per animation, read from the animations as they load (frame, our sound event).</summary>
        private static readonly Dictionary<string, List<(int frame, string sound)>> Notetracks = new Dictionary<string, List<(int, string)>>();

        /// <summary>Our sound events and their clips (event name "Snd" + sound).</summary>
        private static Dictionary<string, string> NotetrackClips => SoundAliases.Values
            .Select(a => new KeyValuePair<string, string>(a.sound, a.clip))
            .Concat(ExtraNotes.Values.SelectMany(n => n).Select(n => new KeyValuePair<string, string>(n.sound, n.clip)))
            .GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value);

        // A state can leave before its clip ends (EFT's transitions use exit times), so late notes are pulled in
        private const float LatestNote = 0.88f;

        private static Dictionary<string, List<(float, string)>> StateSounds(Dictionary<string, (string anim, int first, int last, bool loop)> map,
            Dictionary<string, int> lastFrame)
        {
            var result = new Dictionary<string, List<(float, string)>>();
            foreach (var (state, (anim, first, last, _)) in map)
            {
                if (!Notetracks.TryGetValue(anim, out var notes))
                    continue;
                int span = Math.Max(1, last - first);
                // An empty list still replaces the Saiga's sounds: only BO3's own play during the reload. A note on
                // the frame where one piece hands over to the next belongs to the next (it played in both)
                int end = lastFrame[anim];
                result[state] = notes.Where(n => n.frame >= first && (n.frame < last || last == end))
                    .Select(n => (Mathf.Min(LatestNote, (n.frame - first) / (float)span), n.sound)).ToList();
            }
            return result;
        }

        /// <summary>
        /// State pairs that play one animation in consecutive pieces (the reload's start, out, in and end): the next
        /// piece starts where the last one stopped, so the hand-over must be instant and at the very end.
        /// </summary>
        private static HashSet<(string from, string to)> Contiguous(Dictionary<string, (string anim, int first, int last, bool loop)> map)
        {
            var pairs = new HashSet<(string, string)>();
            foreach (var (from, a) in map)
                foreach (var (to, b) in map)
                    if (from != to && a.anim == b.anim && a.last == b.first && a.last > a.first)
                        pairs.Add((from, to));
            return pairs;
        }

        [MenuItem("Raygun/1. Import Ray Gun")]
        public static void ImportAll()
        {
            // Clips are named after their animation and range: drop earlier imports' so none linger in the project
            AssetDatabase.DeleteAsset(RgPaths.Clips);
            RgPaths.EnsureDirs();
            var warnings = new List<string>();

            // BO3's HD Ray Gun; BO1's arms rig (same bone names) gives the hierarchy above the arms
            CodModel gun = RgBo3.LoadModel();
            CodModel bind = CodModel.Load(Path.Combine(RgPaths.ModelDir, "am_t5_thundergun.xmodel_bin"));
            Debug.Log($"[Raygun] Gun model: {gun.Bones.Count} bones, {gun.Vertices.Count} verts, {gun.Faces.Count} faces, {gun.Materials.Count} materials");

            Dictionary<string, AudioClip> sounds = ImportSounds();

            var rig = new Dictionary<string, Transform>();
            GameObject root = RgRig.Build(RgPaths.WeaponName, rig);
            try
            {
                Transform weapon = rig["weapon"];
                // The gun model's root (tag_weapon) faces the way the CoD identity pose does in prefab space: forward +Z, up +Y
                var jgun = new GameObject(gun.Bones[0].Name).transform;
                jgun.SetParent(weapon, false);
                jgun.gameObject.layer = weapon.gameObject.layer;
                Quaternion jgunLocal = Quaternion.Inverse(weapon.rotation) * RgSpace.Rot(gun.Bones[0].Rest);
                jgun.localRotation = jgunLocal;
                Dictionary<string, Transform> gunBones = BuildGunSkeleton(gun, jgun);

                var materials = new Dictionary<int, Material>();
                for (int m = 0; m < gun.Materials.Count; m++)
                    materials[m] = BuildMaterial(gun.Materials[m], false);
                BuildGunMeshes(gun, gunBones, materials);
                BuildGoldOverlay(gun, gunBones, materials);

                var anims = new Dictionary<string, CodAnim>();
                Notetracks.Clear();
                // The fins spin back on a shot over two frames in BO3: too quick to read (user, 2026-10-27)
                RgBo3.Slower.Clear();
                RgBo3.Slower[(Anims + "fire", "tag_gear_animate")] = FinSlowdown;
                CodAnim Anim(string n)
                {
                    if (anims.TryGetValue(n, out CodAnim a))
                        return a;
                    a = anims[n] = RgBo3.LoadAnim(n, gun, bind, out var notes);
                    var noteSounds = new List<(int, string)>();
                    foreach (var (frame, note) in notes.Where(x => x.note.StartsWith("sndnt#")))
                    {
                        string alias = note.Substring("sndnt#".Length);
                        if (SoundAliases.TryGetValue(alias, out var s))
                            noteSounds.Add((frame, s.sound));
                        else
                            warnings.Add($"{n}: no sound for note {alias} at frame {frame}");
                    }
                    if (ExtraNotes.TryGetValue(n, out var extra))
                        noteSounds.AddRange(extra.Select(x => (x.frame, x.sound)));
                    if (noteSounds.Count > 0)
                        Notetracks[n] = noteSounds;
                    return a;
                }

                var (cellInspect, inspectExtras, inspectNotes) = BuildCellInspect(Anim(Anims + "reload"));
                anims[CellInspect] = cellInspect;
                Notetracks[CellInspect] = inspectNotes;
                anims[FirePadded] = Padded(Anim(Anims + "fire"), FirePad);

                var retarget = new RgRetarget(root.transform, rig, gun, gunBones, jgunLocal, bind, Anim(Anims + "idle"));
                Debug.Log($"[Raygun] View space offset: {retarget.Offset}, shoulders moved {retarget.ShoulderShifts}");
                retarget.PlaceMarkers(Anim(Anims + "idle"), null);
                // A two-barrel gun fires each barrel from its own fireport (the MP-43's are on its barrel part, which
                // the Ray Gun doesn't have): both at the muzzle. Without them every shot threw and never finished
                if (rig.TryGetValue("fireport", out Transform muzzle))
                {
                    foreach (string port in new[] { "fireport_l", "fireport_r" })
                    {
                        var t = new GameObject(port).transform;
                        t.SetParent(muzzle, false);
                        t.gameObject.layer = muzzle.gameObject.layer;
                    }
                }
                else
                {
                    warnings.Add("no fireport in the rig: the gun can't fire");
                }

                // One clip per distinct (animation, range)
                var clips = new Dictionary<string, AnimationClip>();
                var handsClips = new Dictionary<string, AnimationClip>();
                foreach (var (state, (anim, first, last, loop)) in HandsMap)
                {
                    CodAnim a = Anim(anim);
                    int f0 = Math.Min(first, a.Frames.Count - 1), f1 = Math.Min(last, a.Frames.Count - 1);
                    Ease.TryGetValue(state, out var ease);
                    bool inspect = InspectStates.Contains(state);
                    string key = $"{(anim == CellInspect || anim == FirePadded ? anim : anim.Replace(Anims, "rg_"))}_{f0}_{f1}" + (ease.easeIn + ease.easeOut > 0 ? $"_e{ease.easeIn}_{ease.easeOut}" : "")
                                 + (inspect ? "_inspect" : "");
                    if (!clips.TryGetValue(key, out AnimationClip clip))
                    {
                        var clipWarnings = new List<string>();
                        bool raise = anim == Anims + "first_raise";
                        int span = f1 - f0 + 1;
                        Func<int, RgRetarget.Extras> extras = anim == CellInspect ? inspectExtras : raise ? i => RaisePush(i, span) : (Func<int, RgRetarget.Extras>)null;
                        clip = retarget.BakeClip(key, a, f0, f1, loop, clipWarnings, kick: false,
                            easeIn: ease.easeIn, easeOut: ease.easeOut, avoidKnob: raise, inspect: inspect, extras: extras);
                        string clipPath = $"{RgPaths.Clips}/{key}.anim";
                        AssetDatabase.DeleteAsset(clipPath);
                        AssetDatabase.CreateAsset(clip, clipPath);
                        clips[key] = clip;
                        if (clipWarnings.Count > 0)
                            warnings.Add($"{key}: {clipWarnings.Count} frame(s) out of reach, first: {clipWarnings[0]}");
                    }
                    handsClips[state] = clip;
                }
                AnimationClip idle = handsClips["IDLE"];
                Debug.Log($"[Raygun] Baked {clips.Count} clips for {handsClips.Count} Hands states");

                // HandsMap's "999" (to the end) is resolved per animation for the notetrack spans
                var resolved = HandsMap.ToDictionary(kv => kv.Key, kv => (kv.Value.anim, Math.Min(kv.Value.first, Anim(kv.Value.anim).Frames.Count - 1),
                    Math.Min(kv.Value.last, Anim(kv.Value.anim).Frames.Count - 1), kv.Value.loop));
                AnimatorController controller = RgController.Build(ControllerPath, root.transform, handsClips, idle,
                    StateSounds(resolved, anims.ToDictionary(kv => kv.Key, kv => kv.Value.Frames.Count - 1)), Contiguous(resolved), StateSpeeds);
                AddGunCollider(weapon, jgun);
                AddUpgradedMaterials(gun, jgun, materials);
                Transform sight = AddSightMarker(gun, gunBones);
                AddHoopMarker(gun, gunBones);
                // Aiming looks straight down the gun through the ring: the aim camera sits on the ring's line, behind it
                if (sight != null && rig.TryGetValue("aim_camera", out Transform aimCam))
                {
                    Vector3 s = weapon.InverseTransformPoint(sight.position);
                    // EFT weapon space: -Y forward, so behind the ring is +Y
                    aimCam.localPosition = new Vector3(s.x, s.y + SightEyeDistance, s.z);
                }

                // The cells are part of the gun model (BO3 animates them through the reload), so the rounds EFT
                // shows in the two chambers, and the fired one EFT moves to a shell port, stay out of sight
                foreach (string slot in new[] { "patron_in_weapon_000", "patron_in_weapon_001", "patron_in_weapon", "shellport", "shellport_000", "shellport_001" })
                {
                    if (rig.TryGetValue(slot, out Transform t))
                        t.localScale = Vector3.one * 0.001f;
                }

                // Rest pose for the prefab (inventory icons, and before the Animator runs): the idle
                retarget.ApplyFrame(Anim(Anims + "idle"), 0);
                // The power switch's "on" turn (as in the idle), beside it: the client lights the tube, dial and sight
                // only while the switch is there (the first raise flicks it on)
                if (gunBones.TryGetValue(SwitchBone, out Transform powerSwitch))
                {
                    var on = new GameObject(SwitchOnMarker).transform;
                    on.SetParent(powerSwitch.parent, false);
                    on.localPosition = powerSwitch.localPosition;
                    on.localRotation = powerSwitch.localRotation;
                    on.gameObject.layer = powerSwitch.gameObject.layer;
                }
                else
                {
                    warnings.Add($"no {SwitchBone}: the gun's glow stays on");
                }

                var animator = root.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                var links = root.AddComponent<TransformLinks>();
                CopyFields(links, ComponentFields("TransformLinks", RgPaths.BaseModel), r => ResolveInRig(r, root.transform));
                links.Self = root.transform;

                AssetDatabase.DeleteAsset(WeaponPrefabPath);
                Vector3 centre = root.transform.InverseTransformPoint(GunBounds(jgun).center);
                GameObject weaponPrefab = PrefabUtility.SaveAsPrefabAsset(root, WeaponPrefabPath);
                BuildContainer(weaponPrefab, controller, sounds, centre);
                RgFx.BuildAll(sounds);
                BuildCell(gun, materials, CellPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
            foreach (string w in warnings)
                Debug.LogWarning("[Raygun] " + w);
            AssetDatabase.SaveAssets();
            Debug.Log("[Raygun] Import done");
        }

        // ---------- Gun model ----------

        private static Dictionary<string, Transform> BuildGunSkeleton(CodModel gun, Transform jgun)
        {
            var bones = new Dictionary<string, Transform> { [gun.Bones[0].Name] = jgun };
            Quaternion[] world = gun.Bones.Select(b => RgSpace.Rot(b.Rest)).ToArray();
            Vector3[] pos = gun.Bones.Select(b => RgSpace.Pos(b.Rest.Pos)).ToArray();
            for (int i = 1; i < gun.Bones.Count; i++)
            {
                CodBone b = gun.Bones[i];
                var t = new GameObject(b.Name).transform;
                t.SetParent(bones[gun.Bones[b.Parent].Name], false);
                t.gameObject.layer = jgun.gameObject.layer;
                Quaternion parentRot = world[b.Parent];
                t.localPosition = Quaternion.Inverse(parentRot) * (pos[i] - pos[b.Parent]);
                t.localRotation = Quaternion.Inverse(parentRot) * world[i];
                bones[b.Name] = t;
            }
            return bones;
        }

        /// <summary>Synthetic material indices for the cells' own copies (gun material index + this).</summary>
        public const int CellMaterialBase = 1000;

        /// <summary>The dial's needle: its faces get their own material copies too (gun material index + this).</summary>
        private const int NeedleMaterialBase = 2000;
        private const string NeedleBone = "tag_arrow_animate", NeedlePrefix = "rg_needle_";

        // The dial plate and the side vents share the body's mesh and material, so Pack-a-Punch's Origins camo couldn't
        // tell them apart; their faces get material copies of their own (rg_plate_: metal under Origins, rg_dial_: the
        // gauge's face, which keeps its plain look). Regions are in the side-on icon's pixels (1024 wide), see RgIcons
        private const int DialMaterialBase = 4000, PlateMaterialBase = 5000;
        private static float _bx0, _bx1, _bz0, _bz1;
        private static bool _boundsKnown;

        private static void KnownBounds(CodModel gun)
        {
            if (_boundsKnown)
                return;
            _boundsKnown = true;
            var skip = new HashSet<int>(CellBones.Select(gun.BoneIndex));
            _bx0 = _bz0 = float.MaxValue;
            _bx1 = _bz1 = float.MinValue;
            foreach (CodFace f in gun.Faces)
            {
                if (skip.Contains(FaceBone(gun, f)))
                    continue;
                foreach (CodCorner c in f.Corners)
                {
                    CodVec p = gun.Vertices[c.Vertex].Pos;
                    _bx0 = Mathf.Min(_bx0, p.X); _bx1 = Mathf.Max(_bx1, p.X);
                    _bz0 = Mathf.Min(_bz0, p.Z); _bz1 = Mathf.Max(_bz1, p.Z);
                }
            }
        }

        /// <summary>0 body, 1 gauge face, 2 plate or vents. By the face's place in the side-on icon.</summary>
        // Under Origins the body's red takes the crystal and its black and grey the metal plating. Pack-a-Punch paints
        // per material, so the body's faces that aren't red get their own copy (rg_plate_), by the colour under them
        private static Texture2D _bodyColours;
        private static string _bodyColoursPath;

        private static bool IsRedFace(CodFace f, Material body)
        {
            string path = AssetDatabase.GetAssetPath(body.GetTexture("_MainTex"));
            if (_bodyColours == null || _bodyColoursPath != path)
            {
                _bodyColours = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                _bodyColours.LoadImage(File.ReadAllBytes(path));
                _bodyColoursPath = path;
            }
            float cu = f.Corners.Average(c => c.U), cv = f.Corners.Average(c => 1f - c.V);
            int red = 0, total = 0;
            foreach (var (u, v) in f.Corners.Select(c => (c.U * 0.7f + cu * 0.3f, (1f - c.V) * 0.7f + cv * 0.3f)).Append((cu, cv)))
            {
                Color.RGBToHSV(_bodyColours.GetPixelBilinear(u, v), out float hue, out float sat, out float val);
                if ((hue < 0.05f || hue > 0.94f) && sat > 0.35f && val > 0.1f)
                    red++;
                total++;
            }
            return red * 2 >= total;
        }

        private static int Region(CodModel gun, CodFace f)
        {
            KnownBounds(gun);
            float x = 0f, z = 0f, ny = 0f;
            foreach (CodCorner c in f.Corners)
            {
                CodVec p = gun.Vertices[c.Vertex].Pos;
                x += p.X / 3f;
                z += p.Z / 3f;
                ny += c.Normal.Y / 3f;
            }
            float wc = (_bx1 - _bx0) * 1.04f, hc = (_bz1 - _bz0) * 1.04f;
            float height = 1024f * hc / wc;
            float px = 512f - (x - (_bx0 + _bx1) / 2f) / wc * 1024f;
            float py = height / 2f - (z - (_bz0 + _bz1) / 2f) / hc * height;
            float dx = px - 840f, dy = py - 190f, r = Mathf.Sqrt(dx * dx + dy * dy);
            // Metal under Origins (user's marks): the muzzle cone, the fin, the strip along the top, the lightning badge
            // by the knob, and the plate under the gauge that houses the needle (its face only, not the disc's rim)
            bool In(float x0, float y0, float x1, float y1) => px >= x0 && px <= x1 && py >= y0 && py <= y1;
            if (In(85f, 130f, 210f, 245f) || In(275f, 80f, 410f, 170f))
                return 2;
            return 0;
        }

        /// <summary>
        /// The cells' faces use copies of the gun's materials that Pack-a-Punch never paints (it skips the alpha-test
        /// queue up): a cell keeps its own look in an upgraded gun and loose.
        /// </summary>
        private static IEnumerable<CodFace> CellFaces(CodModel gun, Dictionary<int, Material> materials)
        {
            var cellBones = new HashSet<int>(CellBones.Select(gun.BoneIndex));
            int needle = gun.BoneIndex(NeedleBone);
            foreach (CodFace f in gun.Faces)
            {
                int faceBone = FaceBone(gun, f);
                bool isNeedle = faceBone == needle;
                if (!cellBones.Contains(faceBone) && !isNeedle)
                {
                    int region = gun.Materials[f.Material].Name.EndsWith("_plastic") && !IsRedFace(f, materials[f.Material]) ? 2 : 0;
                    if (region == 0)
                    {
                        yield return f;
                        continue;
                    }
                    int split = (region == 1 ? DialMaterialBase : PlateMaterialBase) + f.Material;
                    if (!materials.ContainsKey(split))
                    {
                        string splitPath = $"{RgPaths.Materials}/{(region == 1 ? "rg_dial_" : "rg_plate_")}{gun.Materials[f.Material].Name}.mat";
                        AssetDatabase.DeleteAsset(splitPath);
                        var splitCopy = new Material(materials[f.Material]) { name = Path.GetFileNameWithoutExtension(splitPath) };
                        AssetDatabase.CreateAsset(splitCopy, splitPath);
                        materials[split] = splitCopy;
                    }
                    yield return new CodFace { Object = f.Object, Material = split, Corners = f.Corners };
                    continue;
                }
                // The dial's needle likewise keeps its own look (black on Porter's X2, see AddUpgradedMaterials)
                int index = (isNeedle ? NeedleMaterialBase : CellMaterialBase) + f.Material;
                if (!materials.ContainsKey(index))
                {
                    Material source = materials[f.Material];
                    string path = $"{RgPaths.Materials}/{(isNeedle ? NeedlePrefix : "rg_cell_")}{gun.Materials[f.Material].Name}.mat";
                    AssetDatabase.DeleteAsset(path);
                    var copy = new Material(source) { name = Path.GetFileNameWithoutExtension(path) };
                    if (copy.renderQueue < 2450)
                        KeepLook(copy);
                    AssetDatabase.CreateAsset(copy, path);
                    materials[index] = copy;
                }
                yield return new CodFace { Object = f.Object, Material = index, Corners = f.Corners };
            }
        }

        /// <summary>The model is rigid (one bone per vertex): a plain mesh per bone, no skinning.</summary>
        private static void BuildGunMeshes(CodModel gun, Dictionary<string, Transform> bones, Dictionary<int, Material> materials)
        {
            foreach (var group in CellFaces(gun, materials).ToList().GroupBy(f => FaceBone(gun, f)))
            {
                string boneName = gun.Bones[group.Key].Name;
                Mesh mesh = BuildMesh(gun, group.ToList(), group.Key, $"{RgPaths.WeaponName}_{boneName}", out List<int> mats);
                if (mesh == null)
                    continue;
                var go = new GameObject($"{RgPaths.WeaponName}_{boneName}_LOD0");
                go.layer = bones[boneName].gameObject.layer;
                go.transform.SetParent(bones[boneName], false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = mats.Select(m => materials[m]).ToArray();
            }
        }

        /// <summary>
        /// The gold print over the body (lightning bolts, Blast-O-Matic, the dots) a second time, on a cutout copy
        /// of the body material that Pack-a-Punch never paints: on Porter's X2 the camo covers the body but the gold
        /// shows through. Only the body faces whose texture area holds some gold, a hair proud of the surface.
        /// </summary>
        private static void BuildGoldOverlay(CodModel gun, Dictionary<string, Transform> bones, Dictionary<int, Material> materials)
        {
            int plastic = gun.Materials.FindIndex(m => m.Name.EndsWith("_plastic"));
            if (plastic < 0 || !(materials[plastic].GetTexture("_MainTex") is Texture2D albedo))
                return;
            // The print rides in the albedo's alpha (ImportDeepRed); a coarse grid of where it is picks the faces
            string albedoPath = AssetDatabase.GetAssetPath(albedo);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            tex.LoadImage(File.ReadAllBytes(albedoPath));
            const int grid = 256;
            var hasGold = new bool[grid * grid];
            Color32[] pixels = tex.GetPixels32();
            for (int y = 0; y < tex.height; y++)
                for (int x = 0; x < tex.width; x++)
                    if (pixels[y * tex.width + x].a > 64)
                        hasGold[y * grid / tex.height * grid + x * grid / tex.width] = true;
            int w = tex.width;
            Object.DestroyImmediate(tex);

            bool Gold(CodFace f)
            {
                float u0 = f.Corners.Min(c => c.U), u1 = f.Corners.Max(c => c.U);
                float v0 = f.Corners.Min(c => 1f - c.V), v1 = f.Corners.Max(c => 1f - c.V);
                int gx0 = Mathf.Clamp(Mathf.FloorToInt(Repeat(u0) * grid), 0, grid - 1), gx1 = Mathf.Clamp(Mathf.FloorToInt(Repeat(u0) * grid + (u1 - u0) * grid), 0, grid - 1);
                int gy0 = Mathf.Clamp(Mathf.FloorToInt(Repeat(v0) * grid), 0, grid - 1), gy1 = Mathf.Clamp(Mathf.FloorToInt(Repeat(v0) * grid + (v1 - v0) * grid), 0, grid - 1);
                for (int gy = gy0; gy <= gy1; gy++)
                    for (int gx = gx0; gx <= gx1; gx++)
                        if (hasGold[gy * grid + gx])
                            return true;
                return false;
            }

            string matPath = $"{RgPaths.Materials}/rg_gold.mat";
            AssetDatabase.DeleteAsset(matPath);
            var gold = new Material(materials[plastic]) { name = "rg_gold" };
            MakeCutout(gold, GoldCutoff);
            AssetDatabase.CreateAsset(gold, matPath);
            materials[GoldMaterial] = gold;

            var skip = new HashSet<int>(CellBones.Select(gun.BoneIndex)) { gun.BoneIndex(NeedleBone) };
            int count = 0;
            foreach (var group in gun.Faces.Where(f => f.Material == plastic && !skip.Contains(FaceBone(gun, f)) && Gold(f))
                         .Select(f => new CodFace { Object = f.Object, Material = GoldMaterial, Corners = f.Corners }).GroupBy(f => FaceBone(gun, f)))
            {
                string boneName = gun.Bones[group.Key].Name;
                Mesh mesh = BuildMesh(gun, group.ToList(), group.Key, $"{RgPaths.WeaponName}_{boneName}_gold", out _);
                if (mesh == null)
                    continue;
                Vector3[] v = mesh.vertices, n = mesh.normals;
                for (int i = 0; i < v.Length; i++)
                    v[i] += n[i] * 0.0002f;
                mesh.vertices = v;
                mesh.RecalculateBounds();
                EditorUtility.SetDirty(mesh);
                var go = new GameObject($"{RgPaths.WeaponName}_{boneName}_gold_LOD0");
                go.layer = bones[boneName].gameObject.layer;
                go.transform.SetParent(bones[boneName], false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = gold;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                count += group.Count();
            }
            Debug.Log($"[Raygun] Gold overlay: {count} faces ({w}px print mask)");
        }

        private const int GoldMaterial = 3000;

        private static float Repeat(float t) => t - Mathf.Floor(t);

        /// <summary>
        /// "rg_sight" at the middle of the ring sight (the reticle material's faces), on the bone that carries them: the
        /// client sends a shot through it when the player aims down the sights.
        /// </summary>
        /// <summary>How far behind the ring sight (metres) the aim camera sits.</summary>
        private const float SightEyeDistance = 0.2f;

        private static Transform AddSightMarker(CodModel gun, Dictionary<string, Transform> bones)
        {
            int reticle = gun.Materials.FindIndex(m => m.Name.EndsWith("_reticle"));
            List<CodFace> faces = gun.Faces.Where(f => f.Material == reticle).ToList();
            if (reticle < 0 || faces.Count == 0)
            {
                Debug.LogWarning("[Raygun] No reticle faces: no sight marker");
                return null;
            }
            Vector3 centre = faces.SelectMany(f => f.Corners).Select(c => RgSpace.Pos(gun.Vertices[c.Vertex].Pos))
                .Aggregate(Vector3.zero, (a, b) => a + b) / (faces.Count * 3);
            int boneIndex = FaceBone(gun, faces[0]);
            Transform bone = bones[gun.Bones[boneIndex].Name];
            var marker = new GameObject("rg_sight").transform;
            marker.SetParent(bone, false);
            // Bone-local, as the meshes are built: from the bone's rest pose in model space
            marker.localPosition = Quaternion.Inverse(RgSpace.Rot(gun.Bones[boneIndex].Rest)) * (centre - RgSpace.Pos(gun.Bones[boneIndex].Rest.Pos));
            marker.gameObject.layer = bone.gameObject.layer;
            return marker;
        }

        /// <summary>The power switch, and the marker holding its "on" turn.</summary>
        private const string SwitchBone = "tag_switch_animate", SwitchOnMarker = "rg_switch_on";

        /// <summary>
        /// Middle of the opening in the rear hoop (the bent bar over the back of the gun), in the model's centimetres:
        /// read off the model (crossbar underside 14.8, the body below it about 12.3, posts at +-1.8, at -5.3 along).
        /// </summary>
        private static readonly Vector3 HoopCentreCm = new Vector3(-5.3f, 0f, 13.55f);

        /// <summary>"rg_hoop" in the rear hoop's opening: aiming looks through it at the ring sight.</summary>
        private static void AddHoopMarker(CodModel gun, Dictionary<string, Transform> bones)
        {
            const float cmToInch = 1f / 2.54f;
            Vector3 centre = RgSpace.Pos(new CodVec { X = HoopCentreCm.x * cmToInch, Y = HoopCentreCm.y * cmToInch, Z = HoopCentreCm.z * cmToInch });
            Transform bone = bones[gun.Bones[0].Name];
            var marker = new GameObject("rg_hoop").transform;
            marker.SetParent(bone, false);
            marker.localPosition = Quaternion.Inverse(RgSpace.Rot(gun.Bones[0].Rest)) * (centre - RgSpace.Pos(gun.Bones[0].Rest.Pos));
            marker.gameObject.layer = bone.gameObject.layer;
        }

        /// <summary>The materials a Pack-a-Punched Ray Gun swaps in (RaygunPaP): red tube, dial and sight.</summary>
        private static readonly string[] UpgradedParts = { "_power", "_meter", "_reticle" };

        /// <summary>
        /// The Pack-a-Punched glow materials ("..._up") on a switched-off renderer under the gun, so they're in the
        /// bundle; the client swaps them in on an upgraded gun by name.
        /// </summary>
        private static void AddUpgradedMaterials(CodModel gun, Transform jgun, Dictionary<int, Material> materials)
        {
            // The needle in black (user, 2026-10-27): its own copies darkened, so the camo never reaches it
            IEnumerable<Material> needles = materials.Values.Where(m => m.name.StartsWith(NeedlePrefix)).Distinct().Select(m =>
            {
                string path = $"{RgPaths.Materials}/{m.name}_up.mat";
                AssetDatabase.DeleteAsset(path);
                var black = new Material(m) { name = Path.GetFileNameWithoutExtension(path), color = new Color(0.05f, 0.05f, 0.05f) };
                black.SetColor("_EmissionColor", Color.black);
                black.DisableKeyword("_EMISSION");
                AssetDatabase.CreateAsset(black, path);
                return black;
            }).ToList();
            Material[] ups = gun.Materials.Where(m => UpgradedParts.Any(m.Name.EndsWith)).Select(m => BuildMaterial(m, true)).Concat(needles).ToArray();
            var go = new GameObject("rg_upgraded_materials");
            go.layer = jgun.gameObject.layer;
            go.transform.SetParent(jgun, false);
            go.AddComponent<MeshFilter>();
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = ups;
            r.enabled = false;

            // And the plain ones, so a model re-drawn from the pool can be put back either way from its own parts
            Material[] plains = gun.Materials.Where(m => UpgradedParts.Any(m.Name.EndsWith)).Select(m => BuildMaterial(m, false))
                .Concat(materials.Values.Where(m => m.name.StartsWith(NeedlePrefix)).Distinct()).ToArray();
            var plainHolder = new GameObject("rg_plain_materials");
            plainHolder.layer = jgun.gameObject.layer;
            plainHolder.transform.SetParent(jgun, false);
            plainHolder.AddComponent<MeshFilter>();
            var pr = plainHolder.AddComponent<MeshRenderer>();
            pr.sharedMaterials = plains;
            pr.enabled = false;
            Debug.Log($"[Raygun] Upgraded materials: {string.Join(", ", ups.Select(m => m.name))}");
        }

        /// <summary>
        /// A box around the gun, switched off like the vanilla guns' (the game turns it on when the gun is dropped).
        /// Left on, it pushes against the player's own body while held: the slide across the floor.
        /// </summary>
        private static void AddGunCollider(Transform weapon, Transform jgun)
        {
            var bounds = new Bounds();
            bool first = true;
            foreach (MeshFilter mf in jgun.GetComponentsInChildren<MeshFilter>())
            {
                foreach (Vector3 v in mf.sharedMesh.vertices)
                {
                    Vector3 p = weapon.InverseTransformPoint(mf.transform.TransformPoint(v));
                    if (first) { bounds = new Bounds(p, Vector3.zero); first = false; }
                    else bounds.Encapsulate(p);
                }
            }
            var go = new GameObject($"{RgPaths.WeaponName}_collider");
            go.layer = weapon.gameObject.layer;
            go.transform.SetParent(weapon, false);
            var box = go.AddComponent<BoxCollider>();
            box.center = bounds.center;
            box.size = bounds.size;
            box.enabled = false;
        }

        private static int FaceBone(CodModel gun, CodFace f)
        {
            var votes = f.Corners.Select(c => gun.Vertices[c.Vertex].Weights.OrderByDescending(w => w.weight).First().bone).ToList();
            return votes.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
        }

        /// <summary>Faces into a mesh in the bone's local space, one submesh per material.</summary>
        private static Mesh BuildMesh(CodModel gun, List<CodFace> faces, int bone, string name, out List<int> materialOrder)
        {
            Quaternion inv = Quaternion.Inverse(RgSpace.Rot(gun.Bones[bone].Rest));
            Vector3 origin = RgSpace.Pos(gun.Bones[bone].Rest.Pos);
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var unique = new Dictionary<(int, Vector3, Vector2), int>();
            var submeshes = new Dictionary<int, List<int>>();

            foreach (CodFace f in faces)
            {
                if (!submeshes.TryGetValue(f.Material, out List<int> tris))
                    submeshes[f.Material] = tris = new List<int>();
                var corner = new int[3];
                for (int c = 0; c < 3; c++)
                {
                    CodCorner k = f.Corners[c];
                    Vector3 n = (inv * RgSpace.Dir(k.Normal)).normalized;
                    var uv = new Vector2(k.U, 1f - k.V);
                    var key = (k.Vertex, n, uv);
                    if (!unique.TryGetValue(key, out int index))
                    {
                        index = vertices.Count;
                        unique[key] = index;
                        vertices.Add(inv * (RgSpace.Pos(gun.Vertices[k.Vertex].Pos) - origin));
                        normals.Add(n);
                        uvs.Add(uv);
                    }
                    corner[c] = index;
                }
                tris.AddRange(corner);
            }
            if (vertices.Count == 0)
            {
                materialOrder = null;
                return null;
            }

            materialOrder = submeshes.Keys.OrderBy(k => k).ToList();
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = materialOrder.Count;
            for (int i = 0; i < materialOrder.Count; i++)
            {
                int[] tris = submeshes[materialOrder[i]].ToArray();
                // The axis mirror flips winding: keep whichever winding agrees with the vertex normals
                int agree = 0;
                for (int t = 0; t < tris.Length; t += 3)
                {
                    Vector3 a = vertices[tris[t]], b = vertices[tris[t + 1]], c = vertices[tris[t + 2]];
                    if (Vector3.Dot(Vector3.Cross(b - a, c - a), normals[tris[t]]) > 0f)
                        agree++;
                }
                if (agree * 2 < tris.Length / 3)
                {
                    for (int t = 0; t < tris.Length; t += 3)
                        (tris[t + 1], tris[t + 2]) = (tris[t + 2], tris[t + 1]);
                }
                mesh.SetTriangles(tris, i);
            }
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            string path = $"{RgPaths.Generated}/{name}.asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        // ---------- Cold fusion cell ----------

        /// <summary>The cold fusion cell item model: the gun's own cell geometry (tag_battery_1_animate) on its own.</summary>
        private static void BuildCell(CodModel gun, Dictionary<int, Material> materials, string path)
        {
            int boneIndex = gun.BoneIndex(CellBones[0]);
            List<CodFace> faces = CellFaces(gun, materials).Where(f => FaceBone(gun, f) == boneIndex).ToList();
            string name = Path.GetFileNameWithoutExtension(path);
            Mesh mesh = BuildMesh(gun, faces, boneIndex, $"{name}_mesh", out List<int> mats);
            if (mesh == null)
            {
                Debug.LogError($"[Raygun] No cell geometry on {CellBones[0]}");
                return;
            }

            var root = new GameObject(name);
            try
            {
                var lod = new GameObject($"{name}_LOD0");
                lod.transform.SetParent(root.transform, false);
                lod.AddComponent<MeshFilter>().sharedMesh = mesh;
                lod.AddComponent<MeshRenderer>().sharedMaterials = mats.Select(m => materials[m]).ToArray();
                // Off, like vanilla magazines: the cell rides in the gun, and an active collider there pushes against
                // the player's own body (the slide across the floor). The game enables it when dropped.
                var box = lod.AddComponent<BoxCollider>();
                box.center = mesh.bounds.center;
                box.size = mesh.bounds.size;
                box.enabled = false;

                // EFT places the top rounds here; the charge lives inside the cell, so keep them out of sight
                foreach (string patron in new[] { "patron_001", "patron_002" })
                {
                    var p = new GameObject(patron).transform;
                    p.SetParent(root.transform, false);
                    p.localPosition = mesh.bounds.center;
                    p.localScale = Vector3.one * 0.01f;
                }

                var pivot = root.AddComponent<PreviewPivot>();
                CopyFields(pivot, ComponentFields("PreviewPivot", "mag_saiga12", "mag_components.json"), _ => null);
                pivot.pivotPosition = mesh.bounds.center;

                AssetDatabase.DeleteAsset(path);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log($"[Raygun] Cell prefab {path}: {faces.Count} faces, bounds {mesh.bounds.size}");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---------- Container (WeaponPrefab and friends) ----------

        /// <summary>World bounds of the gun's visible meshes.</summary>
        private static Bounds GunBounds(Transform jgun)
        {
            Renderer[] renderers = jgun.GetComponentsInChildren<MeshRenderer>().Where(r => r.enabled).ToArray();
            Bounds b = renderers[0].bounds;
            foreach (Renderer r in renderers)
                b.Encapsulate(r.bounds);
            return b;
        }

        private static void BuildContainer(GameObject weaponPrefab, AnimatorController controller, Dictionary<string, AudioClip> sounds, Vector3 centre)
        {
            var root = new GameObject(RgPaths.ContainerName);
            try
            {
                AudioClip[] shot = { sounds["ray_shot_f"] };
                SoundBank body = BuildBank("raygun_fire", 300f, shot);
                SoundBank tail = BuildBank("raygun_tail", 300f, new[] { sounds["silence"] });
                SoundBank bodySilenced = BuildBank("raygun_fire_silenced", 130f, shot);
                SoundBank tailSilenced = BuildBank("raygun_tail_silenced", 130f, new[] { sounds["silence"] });

                Object Resolve(string r)
                {
                    if (r == null) return null;
                    if (r.StartsWith("GameObject:") && r.Contains(".generated")) return weaponPrefab;
                    if (r.StartsWith("SO:SoundBank:"))
                    {
                        string bank = r.Substring("SO:SoundBank:".Length);
                        return bank.EndsWith("tail_silenced") ? tailSilenced : bank.EndsWith("tail") ? tail : bank.EndsWith("silenced") ? bodySilenced : body;
                    }
                    if (r.StartsWith("MB:TransformLinks@")) return weaponPrefab.GetComponent<TransformLinks>();
                    if (r.StartsWith("Transform:")) return ResolveInRig(r, weaponPrefab.transform);
                    return null;
                }

                var wp = root.AddComponent<WeaponPrefab>();
                CopyFields(wp, ComponentFields("WeaponPrefab", RgPaths.BaseContainer), Resolve);
                wp._weaponObject = weaponPrefab;
                wp._weaponObjectSimple = weaponPrefab;
                wp._originalAnimatorController = controller;
                wp._animatorSimple = null;
                wp._animatorSpirit = null;
                wp._fastAnimatorControllerBinaryData = null;
                wp._avatar = null;
                wp.Colliders = new List<Collider>();
                wp.Renderers = new List<Renderer>();
                wp._originallyEnabledComponents = new List<Component>();
                wp.RegisteredComponentsToClean = new List<Component>();
                wp.PoolHistory = new List<string>();
                wp.Hierarchy = null;

                CopyFields(root.AddComponent<MuzzleManager>(), ComponentFields("MuzzleManager", RgPaths.BaseContainer), Resolve);
                CopyFields(root.AddComponent<ShellExtractionData>(), ComponentFields("ShellExtractionData", RgPaths.BaseContainer), Resolve);
                var pivot = root.AddComponent<PreviewPivot>();
                CopyFields(pivot, ComponentFields("PreviewPivot", RgPaths.BaseContainer), Resolve);
                pivot.pivotPosition = centre;

                var player = root.AddComponent<WeaponSoundPlayer>();
                CopyFields(player, ComponentFields("WeaponSoundPlayer", RgPaths.BaseContainer), Resolve);
                player.Body = body;
                player.Tail = tail;
                player.BodySilenced = bodySilenced;
                player.TailSilenced = tailSilenced;
                foreach (WeaponSoundPlayer_SoundElement e in player.AdditionalSounds)
                {
                    string clip = FoleyFor(e.EventName, out float volume);
                    e.SoundClips = new List<AudioClip> { sounds.TryGetValue(clip, out AudioClip c) ? c : sounds["silence"] };
                    e.Volume = volume;
                }
                foreach (var (sound, clip) in NotetrackClips)
                {
                    player.AdditionalSounds.Add(new WeaponSoundPlayer_SoundElement
                    {
                        EventName = "Snd" + sound, RollOff = 30, Volume = 1f,
                        SoundClips = new List<AudioClip> { sounds.TryGetValue(clip, out AudioClip c) ? c : sounds["silence"] },
                    });
                }
                player.MainSounds = player.MainSounds ?? new List<WeaponSoundPlayer_SoundElement>();
                foreach (WeaponSoundPlayer_SoundElement e in player.MainSounds)
                    e.SoundClips = new List<AudioClip> { sounds["ray_shot_f"] };
                // Semi-auto: the whole shot plays (as an "automatic" weapon EFT loops the body and cuts it for the tail)
                player.Non_auto = 1;

                AssetDatabase.DeleteAsset(ContainerPrefabPath);
                PrefabUtility.SaveAsPrefabAsset(root, ContainerPrefabPath);
                Debug.Log($"[Raygun] Container prefab {ContainerPrefabPath}: {player.AdditionalSounds.Count} foley events");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>BO3 Ray Gun foley for each of EFT's weapon sound events.</summary>
        private static string FoleyFor(string eventName, out float volume)
        {
            volume = 1f;
            switch (eventName)
            {
                case "SndDraw": return "act_00";
                case "SndBarrelOpen": return "wpn_ray_reload_open";
                case "SndBarrelClose": case "SndBarrelLock": return "wpn_ray_reload_close";
                case "SndLoadOne": case "SndLoadBooth": return "wpn_ray_reload_battery";
                case "SndUnloadOne": case "SndUnloadBooth": case "SndShellThrow": case "SndShellOutRig": return "wpn_ray_reload_battery_out";
                case "SndTrigger": return "eft_saiga_trigger_empty";
                // Put away: EFT's own pistol holster sound (the Makarov's, exported from the game's audio bundle)
                case "SndHolster": return "eft_pm_holster";
                case "SndMagOut": case "SndMagPullOut": return "wpn_ray_reload_battery_out";
                case "SndMagOutAll": case "SndMagSbrosnik": return "wpn_ray_reload_battery_out";
                case "SndMagIn": return "wpn_ray_reload_battery";
                case "SndMagHit": return "wpn_ray_reload_close";
                case "SndBoltOut": case "SndBoltOutSlow": case "SndBoltOutTry": return "wpn_ray_reload_open";
                case "SndBoltIn": return "wpn_ray_reload_close";
                // Empty trigger: EFT's own click (the Saiga's), exported from the game's audio bundle
                case "SndDryFire": return "eft_saiga_trigger_empty";
                case "SndMisfire": case "SndJam": return "eft_saiga_trigger_empty";
                case "SndAmmoIn": case "SndShellInGun": case "SndShellGrab": case "SndAmmoCatch": case "SndShellRemove": return "wpn_ray_reload_battery";
                default: return "silence";
            }
        }

        private static SoundBank BuildBank(string name, float rolloff, AudioClip[] clips)
        {
            string path = $"{RgPaths.Generated}/{name}.asset";
            AssetDatabase.DeleteAsset(path);
            var bank = ScriptableObject.CreateInstance<SoundBank>();
            bank.name = name;
            bank.BaseVolume = 1f;
            bank.BasePitch = 1f;
            bank.Rolloff = rolloff;
            bank.HasEnvironment = 1;
            bank._clipLiength = clips.Max(c => c.length);
            // Outdoor and indoor; close, distant and far (BlendOptions left empty: EFT then always uses the close set)
            bank.Environments = Enumerable.Range(0, 2).Select(_ => new SoundBank_EnvironmentVariety
            {
                Clips = Enumerable.Range(0, 3).Select(__ => new SoundBank_DistanceVarity { Clips = clips.ToList() }).ToList(),
            }).ToList();
            AssetDatabase.CreateAsset(bank, path);
            return bank;
        }

        // ---------- Sounds ----------

        /// <summary>
        /// BO3's Ray Gun sounds (pulled out of the game's sound banks by C:\Git\Tools\bo3_sab_extract.py into
        /// sounds_bo3\), each named after its file. A file in sounds_custom named like a sound replaces it.
        /// </summary>
        private static Dictionary<string, AudioClip> ImportSounds()
        {
            var sources = new List<string>(Directory.GetFiles(RgPaths.Bo3SoundDir, "*.wav", SearchOption.AllDirectories)
                .Where(f => f.Contains(@"\raygun\")));

            string silence = $"{RgPaths.Audio}/silence.wav";
            if (!File.Exists(silence))
                File.WriteAllBytes(silence, SilentWav(0.05f));

            var clips = new Dictionary<string, AudioClip>();
            foreach (string source in sources)
            {
                // npc\act and plr\act share a name: the player's wins
                string asset = $"{RgPaths.Audio}/{Path.GetFileName(source)}";
                if (source.Contains(@"\npc\"))
                    continue;
                File.Copy(source, asset, true);
            }

            var custom = new Dictionary<string, string>();
            if (Directory.Exists(RgPaths.CustomSoundDir))
            {
                foreach (string file in Directory.GetFiles(RgPaths.CustomSoundDir).Where(f => new[] { ".wav", ".mp3", ".ogg", ".aif", ".aiff" }.Contains(Path.GetExtension(f).ToLowerInvariant())))
                {
                    string asset = $"{RgPaths.Audio}/custom_{Path.GetFileName(file)}";
                    File.Copy(file, asset, true);
                    custom[Path.GetFileNameWithoutExtension(file)] = asset;
                }
            }
            AssetDatabase.Refresh();
            foreach (string asset in Directory.GetFiles(RgPaths.Audio).Where(f => !f.EndsWith(".meta") && !Path.GetFileName(f).StartsWith("custom_")))
            {
                string path = asset.Replace('\\', '/');
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip != null)
                    clips[Path.GetFileNameWithoutExtension(path)] = clip;
            }
            foreach (var (name, asset) in custom)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(asset);
                if (clip == null)
                    continue;
                clips[name] = clip;
                Debug.Log($"[Raygun] Custom sound: {name} ({clip.length:F2}s)");
            }
            Debug.Log($"[Raygun] {clips.Count} sounds");
            return clips;
        }

        private static byte[] SilentWav(float seconds)
        {
            const int rate = 22050;
            int samples = (int)(rate * seconds);
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + samples * 2);
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                w.Write(samples * 2);
                w.Write(new byte[samples * 2]);
                return ms.ToArray();
            }
        }

        // ---------- Materials ----------

        /// <summary>Glow colour of the cell tubes, the meter and the sight: the Ray Gun's green (red once upgraded).</summary>
        private static Color GlowColor(bool upgraded) => upgraded ? new Color(1f, 0.15f, 0.1f) : new Color(0.1f, 0.3f, 1f);

        /// <summary>
        /// Standard (Specular setup) from Greyhound's per-material maps: _c colour, _n normal, _s specular with _g gloss
        /// in its alpha, _o occlusion, _e emissive (the cell tubes, the meter and the sight glow).
        /// </summary>
        public static Material BuildMaterial(CodMaterial m, bool upgraded)
        {
            string path = $"{RgPaths.Materials}/{m.Name}{(upgraded ? "_up" : "")}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Standard (Specular setup)")) { name = Path.GetFileNameWithoutExtension(path) };
                AssetDatabase.CreateAsset(mat, path);
            }
            if (mat.shader.name != "Standard (Specular setup)")
                mat.shader = Shader.Find("Standard (Specular setup)");
            mat.SetTexture("_EmissionMap", null);
            mat.SetColor("_EmissionColor", Color.black);
            mat.DisableKeyword("_EMISSION");
            mat.renderQueue = -1;

            string folder = Path.Combine(upgraded ? RgBo3.UpgModelDir : RgBo3.ModelDir, "_images", m.Name);
            string albedo = FindBySuffix(folder, "_c.png");
            Finish finish = FinishOf(m.Name);
            bool redDial = upgraded && m.Name.EndsWith("_meter");
            string spec = FindBySuffix(folder, "_s.png");
            bool plainDial = !upgraded && m.Name.EndsWith("_meter");
            mat.SetTexture("_MainTex", albedo == null ? null : redDial ? ImportRedDial(albedo) : plainDial ? ImportVividDial(albedo)
                : finish.DeepRed ? ImportDeepRed(albedo, spec) : ImportTexture(albedo, TextureImporterType.Default, true));
            mat.color = finish.Tint;
            string normal = FindBySuffix(folder, "_n.png");
            mat.SetTexture("_BumpMap", normal != null ? ImportTexture(normal, TextureImporterType.NormalMap, false) : null);
            if (normal != null) mat.EnableKeyword("_NORMALMAP"); else mat.DisableKeyword("_NORMALMAP");
            if (spec != null)
            {
                mat.SetTexture("_SpecGlossMap", BuildSpecGloss(spec, FindBySuffix(folder, "_g.png"), finish.Spec, finish.Gloss, finish.DeepRed));
                mat.SetFloat("_GlossMapScale", finish.GlossScale);
                mat.EnableKeyword("_SPECGLOSSMAP");
            }
            else
            {
                mat.SetTexture("_SpecGlossMap", null);
                mat.DisableKeyword("_SPECGLOSSMAP");
                mat.SetColor("_SpecColor", new Color(0.25f, 0.25f, 0.25f));
                mat.SetFloat("_Glossiness", 0.6f);
            }
            string occlusion = FindBySuffix(folder, "_o.png");
            mat.SetTexture("_OcclusionMap", occlusion != null ? ImportTexture(occlusion, TextureImporterType.Default, false) : null);

            string emissive = FindBySuffix(folder, "_e.png");
            if (upgraded && m.Name.EndsWith("_meter") && mat.GetTexture("_MainTex") is Texture2D dial)
            {
                SetEmission(mat, dial, Color.white * 0.6f);
                KeepLook(mat);
                EditorUtility.SetDirty(mat);
                return mat;
            }
            if (m.Name.Contains("_power"))
            {
                // The cell tube: BO3 tints a grey plasma noise (_e) in its shader and swirls it; its _c is a lookup
                // strip, not a colour. Standard with the noise as a green glow (a custom shader came out dark in EFT's
                // renderer); the client mod (RaygunPlasma) scrolls and pulses it in game
                // Plain: a darkish blue (user, 2026-10-27), deep navy between the brighter swirls
                mat.SetTexture("_MainTex", null);
                mat.color = upgraded ? GlowColor(true) * 0.25f : new Color(0.01f, 0.03f, 0.12f);
                SetEmission(mat, emissive != null ? ImportEmissive(emissive) : null, GlowColor(upgraded) * (upgraded ? 2.5f : 1.7f));
                mat.SetTextureScale("_MainTex", Vector2.one);
                mat.SetTextureOffset("_MainTex", Vector2.zero);
                // Pack-a-Punch never paints it
                KeepLook(mat);
                EditorUtility.SetDirty(mat);
                return mat;
            }
            else if (emissive != null)
            {
                // The dial's backlight at a level that keeps its printed scale readable (brighter washed it out)
                Color tint = upgraded ? new Color(1f, 0.35f, 0.3f) : Color.white;
                SetEmission(mat, ImportEmissive(emissive), tint * (m.Name.EndsWith("_meter") ? 0.5f : 1.6f));
            }

            if (m.Name.EndsWith("_glass"))
            {
                // Clear over the dial and the cell window: only its shine shows, never a tint over the needle's scale
                mat.color = new Color(0.9f, 0.95f, 0.95f);
                mat.SetTexture("_MainTex", null);
                mat.SetFloat("_Glossiness", 0.95f);
                MakeTransparent(mat, 0.08f);
            }
            // The glowing parts keep their own look on a Pack-a-Punched gun
            if (emissive != null)
                KeepLook(mat);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>
        /// Out of Pack-a-Punch's reach: PaP doesn't paint anything from the alpha-test queue (2450) up. Unity puts an opaque Standard material back
        /// in the shader's own queue whenever it validates it, so a custom queue alone never reached the bundle; a
        /// cutout that clips nothing keeps the alpha-test queue and draws exactly like the opaque one.
        /// </summary>
        private static void KeepLook(Material mat) => MakeCutout(mat, 0f);

        private static void MakeCutout(Material mat, float cutoff)
        {
            mat.SetFloat("_Mode", 1f);
            mat.SetFloat("_Cutoff", cutoff);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            mat.SetInt("_ZWrite", 1);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHABLEND_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }

        private static string FindBySuffix(string dir, string suffix) =>
            Directory.Exists(dir) ? Directory.GetFiles(dir, "*" + suffix).FirstOrDefault(f => !Path.GetFileName(f).StartsWith("$")) : null;

        private static void SetEmission(Material mat, Texture2D map, Color color)
        {
            mat.SetTexture("_EmissionMap", map);
            mat.SetColor("_EmissionColor", color);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            mat.EnableKeyword("_EMISSION");
        }

        private static void MakeTransparent(Material mat, float alpha)
        {
            mat.SetFloat("_Mode", 2f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.renderQueue = 3000;
            Color c = mat.color;
            c.a = alpha;
            mat.color = c;
        }

        /// <summary>Prefers the port's full-resolution "_-g" maps over the "~-g" previews.</summary>
        private static string Pick(string dir, string suffix)
        {
            if (!Directory.Exists(dir))
                return null;
            string[] files = Directory.GetFiles(dir, "*" + suffix).Where(f => !Path.GetFileName(f).StartsWith("$")).ToArray();
            return files.FirstOrDefault(f => Path.GetFileName(f).StartsWith("_-")) ?? files.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        }

        private static string Largest(string dir, string suffix) =>
            Directory.Exists(dir) ? Directory.GetFiles(dir, "*" + suffix).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault() : null;

        private static string FindContaining(string dir, string part) =>
            Directory.Exists(dir) ? Directory.GetFiles(dir, "*.png").FirstOrDefault(f => Path.GetFileName(f).Contains(part)) : null;

        private static Texture2D ImportTexture(string source, TextureImporterType type, bool srgb)
        {
            if (!File.Exists(source))
                return null;
            string assetPath = $"{RgPaths.Textures}/{Path.GetFileName(source).Replace("&", "_").Replace("$", "_").Replace("~", "_")}";
            if (!File.Exists(assetPath))
            {
                File.Copy(source, assetPath);
                AssetDatabase.ImportAsset(assetPath);
            }
            var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
            if (importer.textureType != type || importer.sRGBTexture != srgb || importer.maxTextureSize != 4096)
            {
                importer.textureType = type;
                importer.sRGBTexture = srgb;
                importer.maxTextureSize = 4096; // the port's full-resolution maps: 2K read pixelated in first person
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        private const int EmissiveSize = 1024;

        /// <summary>
        /// BO3's glow maps are small (the grille sections' is 128x128) and compressed they read as a grid of pixels up
        /// close. Imported as a smooth copy instead: upscaled bilinearly to <see cref="EmissiveSize"/>, blurred by
        /// about one source pixel so no texel edges remain, and stored uncompressed with trilinear filtering.
        /// </summary>
        private static Texture2D ImportEmissive(string source)
        {
            if (source == null || !File.Exists(source))
                return null;
            string assetPath = $"{RgPaths.Textures}/{Path.GetFileNameWithoutExtension(source).Replace("&", "_").Replace("$", "_").Replace("~", "_")}_smooth.png";
            if (!File.Exists(assetPath))
            {
                var src = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                src.LoadImage(File.ReadAllBytes(source));
                int size = Mathf.Max(EmissiveSize, Mathf.Max(src.width, src.height));
                int radius = Mathf.Max(1, size / Mathf.Max(src.width, src.height) / 2);
                var up = new Color[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        up[y * size + x] = src.GetPixelBilinear((x + 0.5f) / size, (y + 0.5f) / size);
                Color[] smooth = BoxBlur(BoxBlur(up, size, radius, true), size, radius, false);
                var dst = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
                dst.SetPixels(smooth);
                File.WriteAllBytes(assetPath, dst.EncodeToPNG());
                AssetDatabase.ImportAsset(assetPath);
            }
            var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
            if (importer.textureCompression != TextureImporterCompression.Uncompressed || importer.filterMode != FilterMode.Trilinear)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;
                importer.maxTextureSize = 4096;
                importer.mipmapEnabled = true;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.filterMode = FilterMode.Trilinear;
                importer.anisoLevel = 4;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        /// <summary>One pass of a box blur, across rows or down columns (edges clamp).</summary>
        private static Color[] BoxBlur(Color[] pixels, int size, int radius, bool horizontal)
        {
            var result = new Color[pixels.Length];
            float weight = 1f / (radius * 2 + 1);
            for (int a = 0; a < size; a++)
            {
                for (int b = 0; b < size; b++)
                {
                    Color sum = Color.clear;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int c = Mathf.Clamp(b + k, 0, size - 1);
                        sum += horizontal ? pixels[a * size + c] : pixels[c * size + a];
                    }
                    result[horizontal ? a * size + b : b * size + a] = sum * weight;
                }
            }
            return result;
        }

        /// <summary>How a surface reads, on top of BO3's maps: colour tint, specular scale, gloss curve and scale.</summary>
        private struct Finish
        {
            public Color Tint;
            public float Spec, Gloss, GlossScale;
            public bool DeepRed;
        }

        /// <summary>
        /// The user's look (2026-10-27): the body a deeper, matte red; the silver parts shiny. BO3's bright specular
        /// map washed the red out (in the specular workflow it reflects grey over the colour), so the plastic gets a
        /// fraction of it and little gloss; the metal keeps its specular, brighter, with a lifted gloss curve.
        /// </summary>
        private static Finish FinishOf(string material)
        {
            if (material.EndsWith("_plastic") || material.Contains("_camo"))
                return new Finish { Tint = Color.white, Spec = 0.25f, Gloss = 1f, GlossScale = 0.35f, DeepRed = true };
            if (material.EndsWith("_metal"))
                return new Finish { Tint = new Color(1.15f, 1.15f, 1.15f), Spec = 1.5f, Gloss = 0.55f, GlossScale = 1f };
            return new Finish { Tint = Color.white, Spec = 1f, Gloss = 1f, GlossScale = 1f };
        }

        /// <summary>
        /// The body colour, deeper: only red-dominated texels are darkened and saturated, so the same texture's other
        /// prints (the cells' olive label, the dial, the grey grip) keep their colours.
        /// </summary>
        private static Texture2D ImportDeepRed(string source, string specSource)
        {
            string assetPath = $"{RgPaths.Textures}/{Path.GetFileNameWithoutExtension(source)}_deepred2.png";
            if (!File.Exists(assetPath))
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                tex.LoadImage(File.ReadAllBytes(source));
                float[] gold = GoldMask(specSource, tex.width, tex.height);
                Color[] pixels = tex.GetPixels();
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color c = pixels[i];
                    float other = Mathf.Max(c.g, c.b);
                    // 0 for anything not clearly red, up to 1 for the body's paint: darker and richer (user, 2026-10-27)
                    float red = Mathf.Clamp01((c.r - other * 1.35f) / Mathf.Max(0.02f, c.r) * 2f);
                    c = Color.Lerp(c, new Color(c.r * 0.72f, c.g * 0.36f, c.b * 0.38f, c.a), red);
                    // The gold print is metal: its colour comes from the specular map, so the paint under it goes dark.
                    // Alpha carries the print for the gold overlay (opaque materials ignore it), a pixel wider so its
                    // thin lines survive the cutout
                    float g = gold[i];
                    c = Color.Lerp(c, c * 0.2f, g);
                    c.a = Widened(gold, i, tex.width);
                    pixels[i] = c;
                }
                tex.SetPixels(pixels);
                File.WriteAllBytes(assetPath, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(assetPath);
            }
            var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
            // The overlay's cutout keeps the print's coverage at every mip, or the lines vanish at a distance
            if (importer.maxTextureSize != 4096 || !importer.mipMapsPreserveCoverage)
            {
                importer.maxTextureSize = 4096;
                importer.mipMapsPreserveCoverage = true;
                importer.alphaTestReferenceValue = GoldCutoff;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        /// <summary>
        /// The Pack-a-Punched dial: every coloured zone of the scale becomes a deep red, by brightness, so the black
        /// print (lines, numbers, needle) stays as dark and readable as before.
        /// </summary>
        private static Texture2D ImportRedDial(string source)
        {
            // A deep red like the upgraded tube, a little darker, so the black numbers stand out (user, 2026-10-27)
            string assetPath = $"{RgPaths.Textures}/{Path.GetFileNameWithoutExtension(source)}_reddial2.png";
            if (!File.Exists(assetPath))
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                tex.LoadImage(File.ReadAllBytes(source));
                Color[] pixels = tex.GetPixels();
                var red = new Color(0.85f, 0.1f, 0.07f);
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color c = pixels[i];
                    float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b)), min = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                    // Coloured texels (the zones, the dark red one too) go one bright red; the black print and greys
                    // (no colour) stay as they are
                    float w = Mathf.Clamp01((max - min) * 4f) * Mathf.Clamp01((max - 0.12f) * 6f);
                    Color recoloured = red * Mathf.Lerp(0.85f, 1f, max);
                    recoloured.a = c.a;
                    pixels[i] = Color.Lerp(c, recoloured, w);
                }
                tex.SetPixels(pixels);
                File.WriteAllBytes(assetPath, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(assetPath);
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        /// <summary>
        /// The plain dial, bolder: its red, yellow and green zones more saturated, the board around them darker, the
        /// print untouched (it read washed out under its backlight).
        /// </summary>
        private static Texture2D ImportVividDial(string source)
        {
            string assetPath = $"{RgPaths.Textures}/{Path.GetFileNameWithoutExtension(source)}_vivid.png";
            if (!File.Exists(assetPath))
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                tex.LoadImage(File.ReadAllBytes(source));
                Color[] pixels = tex.GetPixels();
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color c = pixels[i];
                    Color.RGBToHSV(c, out float h, out float s, out float v);
                    // The zones are bright and coloured; the board is dark red
                    float zone = Mathf.Clamp01((v - 0.35f) * 5f) * Mathf.Clamp01(s * 3f);
                    Color vivid = Color.HSVToRGB(h, Mathf.Clamp01(s * 1.35f), Mathf.Clamp01(v * 1.05f));
                    Color board = c * 0.7f;
                    Color result = Color.Lerp(board, vivid, zone);
                    // The black print stays as it is
                    result = Color.Lerp(c, result, Mathf.Clamp01((v - 0.12f) * 6f));
                    result.a = c.a;
                    pixels[i] = result;
                }
                tex.SetPixels(pixels);
                File.WriteAllBytes(assetPath, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(assetPath);
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        private const float GoldCutoff = 0.4f;

        /// <summary>The largest of a mask texel and its eight neighbours.</summary>
        private static float Widened(float[] mask, int i, int w)
        {
            int x = i % w, y = i / w, h = mask.Length / w;
            float best = 0f;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && ny >= 0 && nx < w && ny < h)
                        best = Mathf.Max(best, mask[ny * w + nx]);
                }
            return best;
        }

        /// <summary>How much a specular texel is the gold print (lightning bolts, Blast-O-Matic, dots): 0 grey, 1 gold.</summary>
        private static float GoldOf(Color spec) => Mathf.Clamp01((Mathf.Min(spec.r, spec.g) - spec.b - 0.12f) * 5f);

        /// <summary>The gold print of a specular map, resampled to <paramref name="w"/> x <paramref name="h"/>.</summary>
        private static float[] GoldMask(string specSource, int w, int h)
        {
            var mask = new float[w * h];
            if (specSource == null)
                return mask;
            var spec = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            spec.LoadImage(File.ReadAllBytes(specSource));
            bool same = spec.width == w && spec.height == h;
            Color[] pixels = same ? spec.GetPixels() : null;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    mask[y * w + x] = GoldOf(same ? pixels[y * w + x] : spec.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h));
            Object.DestroyImmediate(spec);
            return mask;
        }

        /// <summary>
        /// Standard (Specular setup) reads smoothness from the specular map's alpha, so pack _g into _s.a; the specular
        /// is scaled by <paramref name="specScale"/> and the gloss raised to <paramref name="glossPower"/> (below 1 is shinier).
        /// With <paramref name="keepGold"/> the gold print is left out of the scaling, brighter and polished, so it stands out.
        /// </summary>
        private static Texture2D BuildSpecGloss(string specSource, string glossSource, float specScale = 1f, float glossPower = 1f, bool keepGold = false)
        {
            string tag = (specScale == 1f && glossPower == 1f ? "" : $"_{specScale:0.##}_{glossPower:0.##}".Replace('.', 'p')) + (keepGold ? "_gold" : "");
            string assetPath = $"{RgPaths.Textures}/{Path.GetFileNameWithoutExtension(specSource).Replace("&", "_").Replace("$", "_").Replace("~", "_")}_sg{tag}.png";
            if (!File.Exists(assetPath))
            {
                var spec = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                spec.LoadImage(File.ReadAllBytes(specSource));
                Texture2D gloss = null;
                if (glossSource != null)
                {
                    gloss = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                    gloss.LoadImage(File.ReadAllBytes(glossSource));
                }
                int w = spec.width, h = spec.height;
                Color[] pixels = spec.GetPixels();
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        float g = gloss != null ? gloss.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h).r : 0.4f;
                        Color c = pixels[i] * specScale;
                        float smooth = Mathf.Pow(Mathf.Clamp01(g), glossPower);
                        if (keepGold)
                        {
                            float gold = GoldOf(pixels[i]);
                            c = Color.Lerp(c, pixels[i] * 1.3f, gold);
                            smooth = Mathf.Lerp(smooth, Mathf.Max(smooth, 0.8f), gold);
                        }
                        pixels[i] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), smooth);
                    }
                }
                spec.SetPixels(pixels);
                File.WriteAllBytes(assetPath, spec.EncodeToPNG());
                Object.DestroyImmediate(spec);
                if (gloss != null)
                    Object.DestroyImmediate(gloss);
                AssetDatabase.ImportAsset(assetPath);
                var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
                importer.maxTextureSize = 4096;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        // ---------- Copying the vanilla gun's component values ----------

        /// <summary>The dumped fields of the vanilla component of this class whose owner path contains <paramref name="ownerPart"/>.</summary>
        private static object ComponentFields(string cls, string ownerPart, string file = null)
        {
            file = file ?? $"{RgPaths.BaseGun}_components.json";
            object all = MiniJson.Parse(File.ReadAllText(RgPaths.Reference(file)));
            object entry = all.Arr().FirstOrDefault(c => c.Str("class") == cls && c.Str("owner").Contains(ownerPart));
            if (entry == null)
                throw new InvalidOperationException($"No {cls} on {ownerPart} in {file}");
            return entry.Get("fields");
        }

        /// <summary>Vanilla rig paths are "Transform:weapon_..._model.generated/a/b"; ours are the same below the root.</summary>
        private static Object ResolveInRig(string reference, Transform root)
        {
            if (reference == null || !reference.StartsWith("Transform:"))
                return null;
            string path = reference.Substring("Transform:".Length);
            int slash = path.IndexOf('/');
            if (slash < 0)
                return path.EndsWith(".generated") ? root : null;
            return root.Find(path.Substring(slash + 1));
        }

        /// <summary>
        /// Fills a stub's serialized fields from a dumped component: values by name, nested classes and lists
        /// recursively, object references through <paramref name="resolve"/> (unknown ones become null).
        /// </summary>
        public static void CopyFields(object target, object json, Func<string, Object> resolve)
        {
            foreach (FieldInfo field in target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object value = json.Get(field.Name);
                if (value == null && !(json is Dictionary<string, object> d && d.ContainsKey(field.Name)))
                    continue;
                field.SetValue(target, Convert(value, field.FieldType, resolve));
            }
        }

        private static object Convert(object value, Type type, Func<string, Object> resolve)
        {
            if (typeof(Object).IsAssignableFrom(type))
            {
                Object o = value is Dictionary<string, object> r ? resolve(r.Str("ref")) : null;
                return o != null && type.IsInstanceOfType(o) ? o : null;
            }
            if (value == null)
                return type.IsValueType ? Activator.CreateInstance(type) : null;
            if (type == typeof(string)) return value as string ?? "";
            if (type == typeof(float)) return (float)(double)value;
            if (type == typeof(double)) return (double)value;
            if (type == typeof(int)) return (int)(long)(double)value;
            if (type == typeof(uint)) return (uint)(long)(double)value;
            if (type == typeof(long)) return (long)(double)value;
            if (type == typeof(short)) return (short)(double)value;
            if (type == typeof(ushort)) return (ushort)(double)value;
            if (type == typeof(byte)) return value is bool b ? (byte)(b ? 1 : 0) : (byte)(double)value;
            if (type == typeof(sbyte)) return (sbyte)(double)value;
            if (type == typeof(bool)) return value is bool bb ? bb : (double)value != 0;
            if (type == typeof(Vector2)) return new Vector2(value.Num("x"), value.Num("y"));
            if (type == typeof(Vector3)) return new Vector3(value.Num("x"), value.Num("y"), value.Num("z"));
            if (type == typeof(Vector4)) return new Vector4(value.Num("x"), value.Num("y"), value.Num("z"), value.Num("w"));
            if (type == typeof(Quaternion)) return new Quaternion(value.Num("x"), value.Num("y"), value.Num("z"), value.Num("w"));
            if (type == typeof(Color)) return new Color(value.Num("r"), value.Num("g"), value.Num("b"), value.Num("a"));
            if (type == typeof(LayerMask)) return (LayerMask)value.Int("m_Bits");
            if (type == typeof(AnimationCurve))
            {
                var keys = (value.Get("m_Curve") as List<object> ?? new List<object>())
                    .Select(k => new Keyframe(k.Num("time"), k.Num("value"), k.Num("inSlope"), k.Num("outSlope"), k.Num("inWeight"), k.Num("outWeight"))).ToArray();
                return new AnimationCurve(keys);
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                Type element = type.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(type);
                if (value is List<object> items)
                    foreach (object item in items)
                        list.Add(Convert(item, element, resolve));
                return list;
            }
            if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
            {
                object instance = Activator.CreateInstance(type);
                if (value is Dictionary<string, object>)
                    CopyFields(instance, value, resolve);
                return instance;
            }
            return null;
        }
    }
}
