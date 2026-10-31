using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Raygun
{
    /// <summary>A tuned value fixed in the mod (the numbers settled in playtesting), read like a setting.</summary>
    internal sealed class Fixed<T>
    {
        public readonly T Value;

        public Fixed(T value) => Value = value;
    }

    /// <summary>
    /// The Ray Gun's numbers, fixed at the values tuned in playtesting (2026-10-28): how it aims and how the menu
    /// character holds it. The volumes are in the F12 menu; the size and fit are in <see cref="RaygunFit"/>.
    /// </summary>
    internal static class RaygunSettings
    {
        // The menu character's hold: the gun hung off the right hand, moved and turned about it (RaygunFit)
        public static readonly Fixed<bool> MenuLeftHand = new Fixed<bool>(false), MenuLeftPlaced = new Fixed<bool>(true);

        // Aiming: shots leave for the screen centre (through the ring sight when aiming); the eye looks through the rear hoop
        public static readonly Fixed<bool> AimAtCentre = new Fixed<bool>(true), AimThroughSight = new Fixed<bool>(true), AimThroughHoop = new Fixed<bool>(true);
        public static readonly Fixed<float> HoopUp = new Fixed<float>(0f);
        public static readonly Fixed<float> SightShotRight = new Fixed<float>(0f), SightShotUp = new Fixed<float>(0f);

        public static ConfigEntry<float> ShotVolume, FoleyVolume, ImpactVolume, PickupVolume, PurchaseVolume, WallBuyVolume, DenyVolume;

        public static void Bind(ConfigFile config)
        {
            const string audio = "Volume";
            ShotVolume = config.Bind(audio, "Shot", 1f, Volume("The Ray Gun's shot."));
            FoleyVolume = config.Bind(audio, "Reload and handling", 1f, Volume("Drawing, reloading, the cells going in and out, the dry click."));
            ImpactVolume = config.Bind(audio, "Bolt impact", 1f, Volume("The burst where a bolt lands."));
            PickupVolume = config.Bind(audio, "Pickup", 0.8f, Volume("BO3's pickup sound when you take the Ray Gun off the ground."));
            PurchaseVolume = config.Bind(audio, "Purchase", 0.8f, Volume("BO3's \"cha-ching\" when you buy from the wall or the ammo box."));
            WallBuyVolume = config.Bind(audio, "Wall buy", 0.8f, Volume("The sound of the gun coming out of the chalk wall."));
            DenyVolume = config.Bind(audio, "Deny", 0.8f, Volume("BO3's buzz when a purchase is refused (not enough money, no room)."));
            RaygunFit.Bind(config);
            DropOldEntries(config);
        }

        /// <summary>
        /// Settings from earlier builds (the tuning sliders) stay in the file as orphans; they're dropped so the file,
        /// and F12, hold the volumes only.
        /// </summary>
        private static void DropOldEntries(ConfigFile config)
        {
            try
            {
                if (AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(config) is System.Collections.IDictionary orphans && orphans.Count > 0)
                {
                    orphans.Clear();
                    config.Save();
                }
            }
            catch (System.Exception e)
            {
                RaygunPlugin.Log?.LogWarning($"Couldn't tidy old settings: {e.Message}");
            }
        }

        private static ConfigDescription Volume(string description) => new ConfigDescription(description, new AcceptableValueRange<float>(0f, 1f));

        /// <summary>Where the placed left hand goes, from the left shoulder in the character's frame, metres.</summary>
        public static Vector3 LeftHandOffset => RaygunFit.MenuLeftHand;

        /// <summary>The view offset in the weapon root's space (EFT's weapon space: +X right, -Y forward, +Z up), metres.</summary>
        public static Vector3 ViewOffset => RaygunFit.ViewOffset;

        public static Quaternion ViewRotation => RaygunFit.ViewRotation;

        /// <summary>The menu hold's offset in the character's frame (x right, y up, z forward), metres.</summary>
        public static Vector3 MenuShift => RaygunFit.MenuShift;

        /// <summary>The menu hold's turn in the character's frame.</summary>
        public static Quaternion MenuTurn => RaygunFit.MenuTurn;
    }

    /// <summary>The menu character's body hold (the game's weapon types for its animator: 0 rifle, 1 pistol).</summary>
    internal enum MenuBodyPose
    {
        Rifle = 0,
        Pistol = 1,
    }
}
