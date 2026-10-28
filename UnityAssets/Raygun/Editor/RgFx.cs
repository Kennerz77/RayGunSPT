using System.IO;
using UnityEditor;
using UnityEngine;

namespace Raygun.Editor
{
    /// <summary>
    /// The Ray Gun's bolt and impact effects, built as prefabs for raygun_fx.bundle (loaded by the client from its
    /// plugin folder): a glowing bolt drawing a bright line behind it, rings left along its path (the client emits
    /// them, BO3 style) and a thin smoky trail, and on impact a ball of gas that bursts and splashes outward with
    /// BO3's ray explosion sound. Green for the Ray Gun, red for Porter's X2 ("_up"). Also the pickup sound
    /// ("rg_pickup"), played when a Ray Gun is taken off the floor or out of a container.
    /// Particles use Unity's legacy particle shaders (proven in EFT).
    /// </summary>
    public static class RgFx
    {
        public const string BundleName = "raygun_fx.bundle";
        public static readonly string[] Prefabs = { "rg_bolt", "rg_bolt_up", "rg_impact", "rg_impact_up", "rg_pickup" };
        public static string PrefabPath(string name) => $"{RgPaths.Root}/Fx/{name}.prefab";

        private static readonly Color Green = new Color(0.35f, 1f, 0.3f);
        private static readonly Color Red = new Color(1f, 0.2f, 0.15f);

        public static void BuildAll(System.Collections.Generic.Dictionary<string, AudioClip> sounds)
        {
            Directory.CreateDirectory($"{RgPaths.Root}/Fx");
            Texture2D glow = SaveTexture("rg_fx_glow", 64, GlowPixel);
            Texture2D puff = SaveTexture("rg_fx_puff", 128, PuffPixel);
            Texture2D ring = SaveTexture("rg_fx_ring", 128, RingPixel);
            Texture2D streak = SaveTexture("rg_fx_streak", 64, StreakPixel);
            foreach (bool up in new[] { false, true })
            {
                Color c = up ? Red : Green;
                string tag = up ? "_up" : "";
                Material glowMat = SaveMaterial($"rg_fx_glow{tag}", "Legacy Shaders/Particles/Additive", glow, c);
                Material smokeMat = SaveMaterial($"rg_fx_smoke{tag}", "Legacy Shaders/Particles/Alpha Blended", puff, Color.white);
                Material ringMat = SaveMaterial($"rg_fx_ring{tag}", "Legacy Shaders/Particles/Additive", ring, c);
                Material lineMat = SaveMaterial($"rg_fx_line{tag}", "Legacy Shaders/Particles/Additive", streak, c);
                sounds.TryGetValue("wpn_ray_exp", out AudioClip boom);
                // No hum on the bolt: it rang over the shot and the hit (user, 2026-10-27)
                BuildBolt("rg_bolt" + tag, c, glowMat, smokeMat, ringMat, lineMat);
                BuildImpact("rg_impact" + tag, c, glowMat, smokeMat, boom);
            }
            BuildPickup();
            AssetDatabase.SaveAssets();
            Debug.Log("[Raygun] FX prefabs built");
        }

        // ---------- The bolt ----------

        private static void BuildBolt(string name, Color c, Material glowMat, Material smokeMat, Material ringMat, Material lineMat)
        {
            var root = new GameObject(name);
            try
            {
                // The glowing core: a few big, bright sprites held on the bolt
                ParticleSystem core = Particles(root, "core", glowMat, local: true);
                var main = core.main;
                main.loop = true;
                main.startLifetime = 0.06f;
                main.startSpeed = 0f;
                main.startSize = new ParticleSystem.MinMaxCurve(0.16f, 0.22f);
                main.startColor = c * 2f;
                main.maxParticles = 20;
                var emission = core.emission;
                emission.rateOverTime = 120f;

                // The trail: thin, see-through green smoke left behind in the world, swelling and fading (user: the
                // smoke is good, thinner and more transparent on the tracer)
                ParticleSystem trail = Particles(root, "trail", smokeMat, local: false);
                main = trail.main;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.4f, 0.7f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0f, 0.15f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.07f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.startColor = new Color(c.r, c.g, c.b, 0.22f);
                main.maxParticles = 400;
                emission = trail.emission;
                emission.rateOverTime = 0f;
                emission.rateOverDistance = 30f;
                Grow(trail, 3f);
                Fade(trail, c, 0.22f);

                // The line: a bright streak that chases the bolt (BO3's)
                var line = root.AddComponent<TrailRenderer>();
                line.sharedMaterial = lineMat;
                line.time = 0.06f;
                line.minVertexDistance = 0.1f;
                line.widthCurve = new AnimationCurve(new Keyframe(0f, 0.07f), new Keyframe(1f, 0f));
                line.colorGradient = Fading(c * 2f);
                line.textureMode = LineTextureMode.Stretch;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.alignment = LineAlignment.View;
                line.emitting = true;

                // The rings: left along the path facing the way it flies, swelling and fading. The client emits them
                // (one every so far, turned to the flight), so the system emits nothing itself
                ParticleSystem rings = Particles(root, "rings", ringMat, local: false);
                main = rings.main;
                main.loop = true;
                main.startLifetime = 0.45f;
                main.startSpeed = 0f;
                main.startSize = 0.22f;
                main.startRotation3D = true;
                main.startColor = c * 1.6f;
                main.maxParticles = 200;
                emission = rings.emission;
                emission.enabled = false;
                SizeCurve(rings, new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(1f, 1.6f)));
                Fade(rings, c, 1f);
                rings.GetComponent<ParticleSystemRenderer>().alignment = ParticleSystemRenderSpace.World;

                // A green spark trail inside the smoke
                ParticleSystem sparks = Particles(root, "sparks", glowMat, local: false);
                main = sparks.main;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.15f, 0.3f);
                main.startSpeed = 0f;
                main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.12f);
                main.startColor = c * 1.5f;
                main.maxParticles = 300;
                emission = sparks.emission;
                emission.rateOverTime = 0f;
                emission.rateOverDistance = 25f;
                Fade(sparks, c * 1.5f, 1f);

                Light light = new GameObject("light").AddComponent<Light>();
                light.transform.SetParent(root.transform, false);
                light.type = LightType.Point;
                light.color = c;
                light.range = 4f;
                light.intensity = 2.5f;
                light.shadows = LightShadows.None;

                Save(root, name);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---------- The impact ----------

        private static void BuildImpact(string name, Color c, Material glowMat, Material smokeMat, AudioClip boom)
        {
            var root = new GameObject(name);
            try
            {
                // The ball: a bright gas sphere that swells and fades where the bolt hit (about the blast's size)
                ParticleSystem ball = Particles(root, "ball", glowMat, local: false);
                var main = ball.main;
                main.loop = false;
                main.duration = 0.6f;
                main.startLifetime = 0.55f;
                main.startSpeed = 0f;
                main.startSize = 2.2f;
                main.startColor = c * 2.2f;
                main.maxParticles = 4;
                Burst(ball, 2);
                SizeCurve(ball, new AnimationCurve(new Keyframe(0f, 0.35f), new Keyframe(0.25f, 1f), new Keyframe(1f, 1.25f)));
                Fade(ball, c * 2.2f, 1f);

                // The splash: gas bursting out of the ball, slowing and drifting up as it thins
                ParticleSystem splash = Particles(root, "splash", smokeMat, local: false);
                main = splash.main;
                main.loop = false;
                main.duration = 0.3f;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.7f, 1.4f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(2.5f, 6f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.25f, 0.5f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.startColor = new Color(c.r, c.g, c.b, 0.7f);
                main.gravityModifier = -0.05f;
                main.maxParticles = 60;
                Burst(splash, 36);
                var shape = splash.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.25f;
                Grow(splash, 3f);
                Fade(splash, c, 0.7f);
                Drag(splash, 4f);

                // Embers: bright specks thrown out with the splash
                ParticleSystem embers = Particles(root, "embers", glowMat, local: false);
                main = embers.main;
                main.loop = false;
                main.duration = 0.2f;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.7f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(3f, 8f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.12f);
                main.startColor = c * 2f;
                main.gravityModifier = 0.4f;
                main.maxParticles = 40;
                Burst(embers, 24);
                shape = embers.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.1f;
                Fade(embers, c * 2f, 1f);
                Drag(embers, 3f);

                // The lingering gas: slow, wide puffs that hang where it hit
                ParticleSystem mist = Particles(root, "mist", smokeMat, local: false);
                main = mist.main;
                main.loop = false;
                main.duration = 0.4f;
                main.startLifetime = new ParticleSystem.MinMaxCurve(1.6f, 2.6f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.8f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.8f, 1.4f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.startColor = new Color(c.r, c.g, c.b, 0.35f);
                main.gravityModifier = -0.03f;
                main.maxParticles = 20;
                Burst(mist, 10);
                shape = mist.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.6f;
                Grow(mist, 1.8f);
                Fade(mist, c, 0.35f);

                Light light = new GameObject("light").AddComponent<Light>();
                light.transform.SetParent(root.transform, false);
                light.type = LightType.Point;
                light.color = c;
                light.range = 7f;
                light.intensity = 5f;
                light.shadows = LightShadows.None;

                if (boom != null)
                {
                    var audio = root.AddComponent<AudioSource>();
                    audio.clip = boom;
                    audio.loop = false;
                    audio.playOnAwake = true;
                    audio.spatialBlend = 1f;
                    audio.volume = 1f;
                    audio.rolloffMode = AudioRolloffMode.Linear;
                    audio.minDistance = 2f;
                    audio.maxDistance = 70f;
                }
                Save(root, name);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---------- Pickup ----------

        /// <summary>BO3's Ray Gun pickup sound (sounds_bo3\wpn\energy\raygun\pick up sound.mp3) on a 2D source.</summary>
        private static void BuildPickup()
        {
            string source = Path.Combine(RgPaths.Bo3SoundDir, "wpn", "energy", "raygun", "pick up sound.mp3");
            string asset = $"{RgPaths.Root}/Fx/rg_pickup.mp3";
            if (File.Exists(source))
            {
                File.Copy(source, asset, true);
                AssetDatabase.ImportAsset(asset);
            }
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(asset);
            if (clip == null)
                Debug.LogWarning($"[Raygun] No pickup sound at {source}");
            var root = new GameObject("rg_pickup");
            try
            {
                var audio = root.AddComponent<AudioSource>();
                audio.clip = clip;
                audio.playOnAwake = true;
                audio.spatialBlend = 0f;
                audio.volume = 0.8f;
                Save(root, "rg_pickup");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---------- Building blocks ----------

        private static Gradient Fading(Color c)
        {
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(c, 0f), new GradientColorKey(c, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            return g;
        }

        private static ParticleSystem Particles(GameObject root, string name, Material mat, bool local)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.playOnAwake = true;
            main.simulationSpace = local ? ParticleSystemSimulationSpace.Local : ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            var shape = ps.shape;
            shape.enabled = false;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        private static void Burst(ParticleSystem ps, int count)
        {
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });
        }

        private static void Grow(ParticleSystem ps, float grow) =>
            SizeCurve(ps, new AnimationCurve(new Keyframe(0f, 1f / grow), new Keyframe(1f, 1f)), grow);

        private static void SizeCurve(ParticleSystem ps, AnimationCurve curve, float scale = 1f)
        {
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(scale, curve);
        }

        private static void Fade(ParticleSystem ps, Color c, float alpha)
        {
            var colour = ps.colorOverLifetime;
            colour.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.3f), new GradientAlphaKey(0f, 1f) });
            colour.color = new ParticleSystem.MinMaxGradient(g);
        }

        private static void Drag(ParticleSystem ps, float drag)
        {
            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = 0f;
            limit.dampen = 0f;
            limit.drag = drag;
        }

        private static void Save(GameObject root, string name)
        {
            string path = PrefabPath(name);
            AssetDatabase.DeleteAsset(path);
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }

        private delegate Color Pixel(float x, float y);

        // A soft round glow: bright middle, smooth falloff
        private static Color GlowPixel(float x, float y)
        {
            float d = Mathf.Clamp01(Mathf.Sqrt(x * x + y * y));
            float a = Mathf.Pow(1f - d, 2.2f);
            return new Color(1f, 1f, 1f, a);
        }

        // A thin bright ring with soft edges
        private static Color RingPixel(float x, float y)
        {
            float d = Mathf.Sqrt(x * x + y * y);
            float a = Mathf.Clamp01(1f - Mathf.Abs(d - 0.8f) / 0.12f);
            return new Color(1f, 1f, 1f, a * a);
        }

        // Across a line: bright in the middle, soft to the edges (V runs across a trail)
        private static Color StreakPixel(float x, float y)
        {
            float a = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(y)), 1.6f);
            return new Color(1f, 1f, 1f, a);
        }

        // A smoke puff: a soft disc broken up by noise
        private static Color PuffPixel(float x, float y)
        {
            float d = Mathf.Clamp01(Mathf.Sqrt(x * x + y * y));
            float n = Mathf.PerlinNoise(x * 3.1f + 5f, y * 3.1f + 9f) * 0.6f + Mathf.PerlinNoise(x * 7.3f + 2f, y * 7.3f + 4f) * 0.4f;
            float a = Mathf.Clamp01((1f - d) * 1.6f - 0.25f) * Mathf.Lerp(0.45f, 1f, n);
            return new Color(1f, 1f, 1f, a);
        }

        private static Texture2D SaveTexture(string name, int size, Pixel pixel)
        {
            string path = $"{RgPaths.Root}/Fx/{name}.png";
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    tex.SetPixel(x, y, pixel((x + 0.5f) / size * 2f - 1f, (y + 0.5f) / size * 2f - 1f));
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Material SaveMaterial(string name, string shader, Texture2D tex, Color tint)
        {
            string path = $"{RgPaths.Root}/Fx/{name}.mat";
            AssetDatabase.DeleteAsset(path);
            var mat = new Material(Shader.Find(shader)) { name = name, mainTexture = tex };
            if (mat.HasProperty("_TintColor"))
                mat.SetColor("_TintColor", new Color(tint.r * 0.5f, tint.g * 0.5f, tint.b * 0.5f, 0.5f));
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }
    }
}
