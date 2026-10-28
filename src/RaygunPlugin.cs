using BepInEx;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace Raygun
{
    /// <summary>Client side of the Ray Gun: the cell rules, the weapon rig fix, the menu pose (F12 tuning) and the inventory pictures.</summary>
    [BepInPlugin(PluginGuid, "Kennerz-Raygun", "1.0.0")] // Forge rule: "Username-ModName"
    [BepInDependency("com.kennerz.paphideoutmodule", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(RaygunDraw.HideoutUncensoredGuid, "1.3.0")] // hard: the wall buy hands you the gun through it
    public class RaygunPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.kennerz.raygun"; // same GUID as the server mod (Forge rule)

        internal static ManualLogSource Log;

        /// <summary>Runs the mod's coroutines (drawing a bought gun).</summary>
        internal static MonoBehaviour Runner;

        /// <summary>The plugin's folder (icons\, sounds\).</summary>
        internal static readonly string Dir = System.IO.Path.GetDirectoryName(typeof(RaygunPlugin).Assembly.Location);

        private void Awake()
        {
            Log = Logger;
            Runner = this;
            RaygunSettings.Bind(Config);
            RaygunSounds.Enable();
            var harmony = new HarmonyLib.Harmony(PluginGuid);
            try
            {
                RaygunRootReset.Enable(harmony);
            }
            catch (System.Exception e)
            {
                Log.LogError($"Weapon root reset patch failed: {e}");
            }
            try
            {
                RaygunCells.Enable(harmony);
            }
            catch (System.Exception e)
            {
                Log.LogError($"Cold fusion cell patches failed: {e}");
            }
            try
            {
                RaygunMenuPose.Enable(harmony, gameObject);
                gameObject.AddComponent<RaygunPlasma>();
            }
            catch (System.Exception e)
            {
                Log.LogError($"Menu pose patches failed: {e}");
            }
            try
            {
                RaygunAim.Enable(harmony);
                RaygunAuto.Enable(harmony);
                RaygunAmmoCheck.Enable(harmony);
                RaygunPreview.Enable(harmony);
                RaygunBolts.Enable(harmony);
                RaygunPickup.Enable(harmony);
                gameObject.AddComponent<RaygunBolts>();
            }
            catch (System.Exception e)
            {
                Log.LogError($"Aim patch failed: {e}");
            }
            try
            {
                RaygunPaP.Setup(harmony, gameObject);
            }
            catch (System.Exception e)
            {
                Log.LogError($"Pack-a-Punch support failed: {e}");
            }
            try
            {
                RaygunIcons.Enable(harmony);
            }
            catch (System.Exception e)
            {
                Log.LogError($"Icon patches failed: {e}");
            }
            try
            {
                RaygunSellPrice.Enable(harmony);
                RaygunSort.Enable(harmony);
                RaygunX2.Enable(harmony);
                RaygunStations.Enable(harmony, gameObject);
                gameObject.AddComponent<RaygunTracker>();
            }
            catch (System.Exception e)
            {
                Log.LogError($"Hideout wall buy failed: {e}");
            }
            Log.LogInfo("Raygun loaded");
        }
    }

    /// <summary>Twice a second: a raid puts a bought wall gun back on sale for the next hideout visit.</summary>
    internal class RaygunTracker : MonoBehaviour
    {
        private float _next;

        private void Update()
        {
            if (Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 0.5f;
            Player player = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer as Player : null;
            if (player != null && !(player is HideoutPlayer))
                RaygunStations.RaidSeen();
            // A Ray Gun left in another mode (an earlier build's automatic) is put back to single as it's drawn
            if (player?.HandsController is Player.FirearmController hands && hands.Item is Weapon gun && RaygunIds.Is(gun)
                && gun.FireMode != null && gun.FireMode.FireMode != Weapon.EFireMode.single)
                gun.FireMode.SetFireMode(Weapon.EFireMode.single);
        }
    }

    internal static class RaygunIds
    {
        public const string Prefix = "6a7e5a71d9a11600000000";
        public const string Weapon = Prefix + "01";
        public const string UpgradedName = "Porter's X2 Ray Gun"; // BO's Pack-a-Punched Ray Gun
        public const int FullCell = 10;

        /// <summary>The cold fusion cell with this many charges (1-10): ids ...c1 to ...ca, as the server makes them.</summary>
        public static string CellId(int charges) => Prefix + (0xC0 + charges).ToString("x2");

        public static bool Is(Item item) => item != null && item.TemplateId.ToString() == Weapon;

        public static bool IsCell(Item item) => CellLevel(item) > 0;

        /// <summary>Charges left in a cold fusion cell (1-10), or 0 for anything else.</summary>
        public static int CellLevel(Item item)
        {
            string id = item?.TemplateId.ToString();
            if (id == null || id.Length != 24 || !id.StartsWith(Prefix))
                return 0;
            int level = System.Convert.ToInt32(id.Substring(22), 16) - 0xC0;
            return level >= 1 && level <= FullCell ? level : 0;
        }
    }
}
