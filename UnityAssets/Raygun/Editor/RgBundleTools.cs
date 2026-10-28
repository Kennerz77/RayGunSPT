using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Raygun.Editor
{
    /// <summary>
    /// Unity 2022.3.43f1 editor helpers for the Ray Gun.
    /// Menu: Raygun > 1. Import Ray Gun, then Raygun > 2. Build bundles.
    /// Batch: Unity.exe -batchmode -quit -projectPath ... -executeMethod Raygun.Editor.RgBundleTools.RunAllBatch
    /// </summary>
    public static class RgBundleTools
    {
        private const string BuildDir = "AssetBundles";
        // Your SPT install: the built bundles are copied here.
        private const string ServerModDir = @"D:\OTHER LAUNCHERS\BUTTERSTATE\SPT_Runtime\user\mods\Raygun";
        // Bundle keys are paths under the server mod's bundles folder; the item templates point at the same keys
        private const string KeyFolder = "Raygun";
        // The client plugin's folder: the effects bundle goes beside Raygun.dll
        private const string PluginDir = @"D:\OTHER LAUNCHERS\BUTTERSTATE\BepInEx\plugins\Raygun";

        private static readonly (string bundle, string asset)[] Bundles =
        {
            ($"{RgPaths.ContainerName}.bundle", RgImporter.ContainerPrefabPath),
            ($"{RgPaths.CellName}.bundle", RgImporter.CellPrefabPath),
        };

        [MenuItem("Raygun/2. Build bundles")]
        public static void BuildMenu()
        {
            if (Build())
                EditorUtility.RevealInFinder(Path.Combine(ServerModDir, "bundles"));
        }

        public static bool Build()
        {
            foreach (var (_, asset) in Bundles)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(asset) == null)
                {
                    Debug.LogError($"[Raygun] No prefab at {asset}. Run the import first.");
                    return false;
                }
            }

            Directory.CreateDirectory(BuildDir);
            // EFT finds the prefab by name: the asset whose name is the bundle's file name without the extension.
            // The bolt and impact effects are a bundle of their own, which the client mod loads from its folder
            AssetBundleBuild[] builds = Bundles.Select(b => new AssetBundleBuild { assetBundleName = b.bundle, assetNames = new[] { b.asset } })
                .Append(new AssetBundleBuild { assetBundleName = RgFx.BundleName, assetNames = RgFx.Prefabs.Select(RgFx.PrefabPath).ToArray() })
                .ToArray();
            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(BuildDir, builds, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
            if (manifest == null)
            {
                Debug.LogError("[Raygun] Bundle build failed; see console.");
                return false;
            }

            string dest = Path.Combine(ServerModDir, "bundles", KeyFolder);
            Directory.CreateDirectory(dest);
            foreach (var (bundle, _) in Bundles)
            {
                File.Copy(Path.Combine(BuildDir, bundle), Path.Combine(dest, bundle), true);
                Debug.Log($"[Raygun] {bundle}: {new FileInfo(Path.Combine(dest, bundle)).Length / 1024} KB");
            }

            // SPT loads a mod's bundles from bundles.json next to its DLL. Each bundle is self-contained (its shaders
            // and textures are inside), so nothing depends on the game's own bundles.
            string entries = string.Join(",\n", Bundles.Select(b => $"    {{ \"key\": \"{KeyFolder}/{b.bundle}\", \"dependencyKeys\": [] }}"));
            File.WriteAllText(Path.Combine(ServerModDir, "bundles.json"), "{\n  \"manifest\": [\n" + entries + "\n  ]\n}\n");
            Directory.CreateDirectory(PluginDir);
            File.Copy(Path.Combine(BuildDir, RgFx.BundleName), Path.Combine(PluginDir, RgFx.BundleName), true);
            Debug.Log($"[Raygun] {RgFx.BundleName}: {new FileInfo(Path.Combine(PluginDir, RgFx.BundleName)).Length / 1024} KB -> {PluginDir}");
            Debug.Log($"[Raygun] Built and copied bundles -> {dest}");
            return true;
        }

        public static void RunAllBatch()
        {
            try
            {
                RgImporter.ImportAll();
                RgPreview.RenderBatch();
                RgIcons.RenderAll();
                if (!Build())
                    EditorApplication.Exit(1);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>Import and previews only (no bundles), for checking the retarget.</summary>
        public static void PreviewBatch()
        {
            try
            {
                RgImporter.ImportAll();
                RgPreview.RenderBatch();
                RgIcons.RenderAll();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }
    }
}
