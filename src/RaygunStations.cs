using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.Communications;
using EFT.Hideout;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Utils;
using UnityEngine;

namespace Raygun
{
    /// <summary>
    /// The mod's own way to get the Ray Gun, no other mod needed: a chalk outline of it on a hideout wall (BO's wall
    /// buy) sells the gun, loaded with two cells, and an ammo case beside it sells full cold fusion cells, both for
    /// roubles from the stash. A bought gun sits in its outline until the next raid; the wall offers it again on the
    /// first hideout visit after one. The two stations are root objects named <see cref="WallName"/> and
    /// <see cref="BoxName"/>, at fixed spots in the side room (<see cref="Positions"/>).
    /// </summary>
    internal class RaygunStations : MonoBehaviour
    {
        public const string WallName = "Raygun_WallBuy";
        public const string BoxName = "Raygun_AmmoBox";
        private const string AmmoCaseTpl = "5aafbde786f774389d0cbc0f"; // the green "Ammunition case"
        private const string BuyAction = "RaygunBuy";
        // Shown in the prompts; the server's config.json has the prices it charges (keep them in step)
        private const int WeaponPrice = 935_000, CellPrice = 10_000;
        private const float UseDistance = 2.5f;

        private static HideoutPlayerOwner _owner;
        private static float _ownerSince;
        private static bool _bought, _raidSinceBuy, _busy;

        private readonly RaygunInteraction _wallMenu = new RaygunInteraction();
        private readonly RaygunInteraction _boxMenu = new RaygunInteraction();
        private Transform _wall, _box;
        private GameObject _wallGun;

        public static void Enable(Harmony harmony, GameObject host)
        {
            harmony.Patch(AccessTools.Method(typeof(HideoutPlayerOwner), nameof(HideoutPlayerOwner.Init)),
                postfix: new HarmonyMethod(typeof(RaygunStations).GetMethod(nameof(OwnerInitPostfix), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
            host.AddComponent<RaygunStations>();
        }

        // Every hideout visit: a gun bought before the last raid is on sale again
        private static void OwnerInitPostfix(HideoutPlayerOwner __instance)
        {
            _owner = __instance;
            _ownerSince = Time.unscaledTime;
            if (_raidSinceBuy)
            {
                _bought = false;
                _raidSinceBuy = false;
            }
        }

        /// <summary>Told by the tracker while the player is in a raid.</summary>
        public static void RaidSeen()
        {
            if (_bought)
                _raidSinceBuy = true;
        }

        private void Update()
        {
            HideoutPlayerOwner owner = _owner;
            if (owner == null || owner.HideoutPlayer == null)
            {
                _wallMenu.Reset();
                _boxMenu.Reset();
                return;
            }
            // Only once the hideout's side room is open; shut again (a guest's hideout, say), the stations go quiet
            bool open = SideRoomOpen();
            if (_wall != null && _wall.gameObject.activeSelf != open)
            {
                _wall.gameObject.SetActive(open);
                _box.gameObject.SetActive(open);
            }
            // A moment for the player to be placed before the stations are set out the first time
            if (_wall == null && open && Time.unscaledTime - _ownerSince > 2f)
                SetUp(owner, Positions[WallName], Positions[BoxName]);
            if (_wall == null || !open)
            {
                _wallMenu.Reset();
                _boxMenu.Reset();
                return;
            }
            ShowWallGun(_bought);
            _wallMenu.Tick(owner, _wall, UseDistance, WallOptions);
            _boxMenu.Tick(owner, _box, UseDistance, BoxOptions);
        }

        private const int SideRoomLevel = 6; // the Emergency Wall level that opens the side room

        /// <summary>The player's own hideout has its Emergency Wall upgraded to the side room opening.</summary>
        private static bool SideRoomOpen()
        {
            try
            {
                if (!Singleton<HideoutRepresentation>.Instantiated)
                    return false;
                AreaData wall = Singleton<HideoutRepresentation>.Instance.AreaDatas.FirstOrDefault(a => a.Template.Type == EAreaType.EmergencyWall);
                return wall != null && !wall.IsGuestData && wall.CurrentLevel >= SideRoomLevel;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private IEnumerable<(string, Action)> WallOptions()
        {
            if (_bought)
                yield return ("Ray Gun bought", null);
            else
                yield return ($"Buy Ray Gun ({WeaponPrice:N0} ₽)", _busy ? (Action)null : () => Buy("weapon"));
        }

        private IEnumerable<(string, Action)> BoxOptions()
        {
            yield return ($"Buy Cold Fusion Cell ({CellPrice:N0} ₽)", _busy ? (Action)null : () => Buy("cell"));
        }

        // ---------- Buying ----------

        private void Buy(string what)
        {
            ClientBackendSession session = ClientAppUtils.GetMainApp()?.GetClientBackEndSession() as ClientBackendSession;
            if (session == null)
                return;
            _busy = true;
            bool holsterEmpty = what == "weapon" && RaygunDraw.HolsterEmpty(_owner);
            // With a weapon out in the hideout the game works on a copy of the gear (its containers have other ids),
            // which a cell placed on the character wouldn't reach: then it goes to the stash
            bool carry = _owner.HideoutPlayer != null && _owner.HideoutPlayer.OriginalInventory == null && !_owner.InShootingRange;
            session.SendOperationRightNow(new BuyOperation { Action = BuyAction, item = what, carry = carry }, result =>
            {
                _busy = false;
                if (result == null || result.Failed)
                {
                    StationSounds.Play("deny", RaygunSettings.DenyVolume.Value);
                    NotificationManager.DisplayWarningNotification(string.IsNullOrEmpty(result?.Error) ? "The purchase didn't go through." : result.Error);
                    return;
                }
                StationSounds.Play("purchase_accept", RaygunSettings.PurchaseVolume.Value);
                if (what == "weapon")
                {
                    // BO's wall buy: the cha-ching and the gun coming off the wall together (the wall sound runs longer)
                    StationSounds.Play("wallbuy", RaygunSettings.WallBuyVolume.Value);
                    RaygunPickup.Play();
                    _bought = true;
                    _raidSinceBuy = false;
                    _slideOut = true;
                    // The server equips it when the holster is free (otherwise it goes to the stash): then it's drawn
                    if (!RaygunDraw.TryDrawBought(_owner, holsterEmpty))
                        NotificationManager.DisplayMessageNotification("Ray Gun bought.");
                }
                else
                {
                    NotificationManager.DisplayMessageNotification("Cold Fusion Cell bought. It's in your stash.");
                }
            });
        }

        /// <summary>Item event body; field names match the server's RaygunBuyRequest.</summary>
        private class BuyOperation
        {
            public string Action;
            public string item;
            public bool carry;
        }

        // ---------- Setting out ----------

        private void SetUp(HideoutPlayerOwner owner, (Vector3 position, Quaternion rotation) wallAt, (Vector3 position, Quaternion rotation) boxAt)
        {
            _wall = Root(WallName, wallAt);
            Transform chalk = BuildChalk(_wall);
            // Drawn life size: the gun's real length, measured from its model once
            StartCoroutine(Model(RaygunIds.Weapon, _wall, model =>
            {
                Bounds bounds = WorldBounds(model);
                float measured = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
                // A pistol is somewhere between 15 cm and a metre: anything else is a bad measurement
                float length = measured > 0.15f && measured < 1f ? measured : 0.3f;
                RaygunPlugin.Log.LogInfo($"Wall buy chalk sized to the gun: {measured:F2} m measured, {length:F2} m used");
                model.transform.SetParent(null, false);
                AssetPoolObject.ReturnToPool(model, true);
                if (chalk != null)
                    SizeChalk(chalk, length);
            }, loaded: true));
            _box = Root(BoxName, boxAt);
            StartCoroutine(Model(AmmoCaseTpl, _box, model => FitCollider(_box, model)));
            RaygunPlugin.Log.LogInfo("Hideout stations set out");
        }

        private static Transform Root(string name, (Vector3 position, Quaternion rotation) at)
        {
            GameObject old = GameObject.Find(name);
            if (old != null)
                Destroy(old);
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(at.position, at.rotation);
            return go.transform;
        }

        /// <summary>The outline: a flat chalk drawing of the gun, its forward face (+Z) out of the wall.</summary>
        private static Transform BuildChalk(Transform wall)
        {
            Texture2D chalk = RaygunChalk.Make();
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "chalk";
            Destroy(quad.GetComponent<Collider>());
            quad.transform.SetParent(wall, false);
            // A hair off the wall so it never flickers into it
            quad.transform.localPosition = new Vector3(0f, 0f, ChalkDepth);
            var material = new Material(Shader.Find("Sprites/Default")) { mainTexture = chalk };
            quad.GetComponent<Renderer>().sharedMaterial = material;
            wall.gameObject.AddComponent<BoxCollider>();
            SizeChalk(quad.transform, 0.3f);
            return quad.transform;
        }

        private const float ChalkDepth = 0.005f;

        /// <summary>
        /// Sized so the drawn gun (its outline, not the picture around it) is exactly the real gun's length, with the
        /// outline's middle on the wall's origin where the model is hung, and mirrored: the picture is the gun's left
        /// side, muzzle left, and the bought model hangs the other way round, so the drawing is flipped to match.
        /// </summary>
        private static void SizeChalk(Transform quad, float gunLength)
        {
            Texture2D chalk = quad.GetComponent<Renderer>().sharedMaterial.mainTexture as Texture2D;
            float width = gunLength / RaygunChalk.OutlineWidth, height = chalk != null ? width * chalk.height / chalk.width : width * 0.4f;
            quad.localScale = new Vector3(-width, height, 1f);
            quad.localPosition = new Vector3(RaygunChalk.OutlineCentre.x * width, -RaygunChalk.OutlineCentre.y * height, ChalkDepth);
            BoxCollider box = quad.parent.GetComponent<BoxCollider>();
            if (box != null)
                box.size = new Vector3(gunLength * 1.05f, height * RaygunChalk.OutlineHeight * 1.05f, 0.05f);
        }

        /// <summary>
        /// A bought gun hangs inside its outline until the next raid, loaded with two cells. Just bought, it slides
        /// slowly out of the wall as in BO; on a later visit it's simply there.
        /// </summary>
        private void ShowWallGun(bool show)
        {
            if (show && _wallGun == null && !_loadingGun)
            {
                _loadingGun = true;
                bool slide = _slideOut;
                _slideOut = false;
                StartCoroutine(Model(RaygunIds.Weapon, _wall, model =>
                {
                    _loadingGun = false;
                    _wallGun = model;
                    LayAlong(model, _wall);
                    if (slide)
                        StartCoroutine(SlideOut(model.transform));
                }, loaded: true));
            }
            else if (!show && _wallGun != null)
            {
                // Back to the game's model pool, as it came out
                _wallGun.transform.SetParent(null, false);
                AssetPoolObject.ReturnToPool(_wallGun, true);
                _wallGun = null;
            }
        }

        private bool _loadingGun;
        private static bool _slideOut;

        private const float SlideDepth = 0.12f, SlideSeconds = 1.6f;

        /// <summary>From inside the wall out to its place, easing to a stop.</summary>
        private static IEnumerator SlideOut(Transform gun)
        {
            Vector3 end = gun.localPosition, start = end - new Vector3(0f, 0f, SlideDepth);
            for (float t = 0f; t < SlideSeconds && gun != null; t += Time.deltaTime)
            {
                float k = t / SlideSeconds;
                gun.localPosition = Vector3.Lerp(start, end, 1f - (1f - k) * (1f - k) * (1f - k));
                yield return null;
            }
            if (gun != null)
                gun.localPosition = end;
        }

        /// <summary>
        /// The model hung on the wall as its chalk shows it: muzzle to the viewer's left, grip down, its left side out.
        /// Which way the model's own axes run is read off the model: the muzzle is the way from the rear hoop to the
        /// ring sight, and down is away from the barrel towards the bulk of the gun (the grip). Its middle sits on the
        /// wall's surface, so it's half in the wall.
        /// </summary>
        private static void LayAlong(GameObject model, Transform wall)
        {
            model.transform.SetParent(null, false);
            model.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Bounds bounds = WorldBounds(model);
            Quaternion lay = Quaternion.identity;
            Transform hoop = null, sight = null;
            foreach (Transform t in model.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "rg_hoop")
                    hoop = t;
                else if (t.name == "rg_sight")
                    sight = t;
            }
            if (hoop != null && sight != null)
            {
                Vector3 forward = (sight.position - hoop.position).normalized;
                Vector3 down = Vector3.ProjectOnPlane(bounds.center - hoop.position, forward);
                if (down.sqrMagnitude > 1e-8f)
                {
                    Vector3 up = -down.normalized, left = Vector3.Cross(forward, up);
                    // The wall's X is the viewer's left (the wall faces them along +Z), Y up, Z out
                    lay = Quaternion.Inverse(Quaternion.LookRotation(left, up));
                }
            }
            else
            {
                RaygunPlugin.Log.LogWarning("Wall gun: no sight markers on the model, hung as it came");
            }
            model.transform.SetParent(wall, false);
            model.transform.localRotation = lay;
            model.transform.localPosition = -(lay * bounds.center);
        }

        private static void FitCollider(Transform root, GameObject model)
        {
            model.transform.SetParent(root, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            Bounds bounds = WorldBounds(model);
            var box = root.gameObject.GetComponent<BoxCollider>() ?? root.gameObject.AddComponent<BoxCollider>();
            box.center = root.InverseTransformPoint(bounds.center);
            box.size = bounds.size;
        }

        /// <summary>
        /// A display model of an item, built the way the hideout's weapon stands build theirs: its bundles loaded, made
        /// by the game's factory, and purely visual (no physics; the colliders off, so only the station's box is hit).
        /// </summary>
        private static IEnumerator Model(string template, Transform parent, Action<GameObject> done, bool loaded = false)
        {
            ItemFactory factory = Singleton<ItemFactory>.Instance;
            Item item = factory.CreateItem(MongoID.Generate(), template, null);
            if (item == null)
                yield break;
            // A display gun with a full cell in each chamber
            if (loaded && item is Weapon weapon)
            {
                foreach (Slot chamber in weapon.Chambers)
                {
                    Item cell = factory.CreateItem(MongoID.Generate(), RaygunIds.CellId(RaygunIds.FullCell), null);
                    if (cell != null)
                        chamber.AddWithoutRestrictions(cell);
                }
            }
            Task bundles = EasyAssetsExtensions.WaitForAllBundlesJob(item.GetAllBundleTokens(), () => { }, CancellationToken.None, null);
            while (!bundles.IsCompleted)
                yield return null;
            if (bundles.IsFaulted || parent == null)
                yield break;
            GameObject model = Singleton<ObjectsFactory>.Instance?.CreateItem(item, false);
            if (model == null)
                yield break;
            foreach (Collider c in model.GetComponentsInChildren<Collider>(true))
                c.enabled = false;
            foreach (Rigidbody rb in model.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;
                rb.detectCollisions = false;
            }
            model.SetActive(true);
            done(model);
        }

        /// <summary>
        /// The visible model only: active meshes that draw. Inactive parts, particles and hidden parts report bounds
        /// that can sit at the world's origin and would stretch it.
        /// </summary>
        private static Bounds WorldBounds(GameObject go)
        {
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(false)
                .Where(r => r.enabled && !r.forceRenderingOff && (r is MeshRenderer || r is SkinnedMeshRenderer)).ToArray();
            if (renderers.Length == 0)
                return new Bounds(go.transform.position, Vector3.one * 0.3f);
            Bounds bounds = renderers[0].bounds;
            foreach (Renderer r in renderers.Skip(1))
                bounds.Encapsulate(r.bounds);
            return bounds;
        }

        // ---------- Positions ----------

        // Where they stand in the side room (placed in game, 2026-10-28)
        private static readonly Dictionary<string, (Vector3, Quaternion)> Positions = new Dictionary<string, (Vector3, Quaternion)>
        {
            [WallName] = (new Vector3(8.6439f, 1.4778f, 0.3169f), Quaternion.Euler(0f, 0.1211f, 0f)),
            [BoxName] = (new Vector3(9.3447f, 0.0002f, 0.7054f), Quaternion.Euler(0f, 0.1211f, 0f)),
        };
    }

    /// <summary>
    /// The stations' BO3 sounds (sounds\*.wav beside the plugin): "purchase_accept" (the cha-ching), "wallbuy" (the gun
    /// coming off the wall) and "deny" (refused). Played in the player's head like BO's, at their F12 volumes.
    /// </summary>
    internal static class StationSounds
    {
        private static readonly Dictionary<string, AudioClip> Clips = new Dictionary<string, AudioClip>();
        private static AudioSource _source;

        public static void Play(string name, float volume)
        {
            try
            {
                if (!Clips.TryGetValue(name, out AudioClip clip))
                    Clips[name] = clip = Wav.Load(Path.Combine(RaygunPlugin.Dir, "sounds", name + ".wav"));
                if (clip == null || volume <= 0f)
                    return;
                if (_source == null)
                {
                    var go = new GameObject("rg_station_sounds");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _source = go.AddComponent<AudioSource>();
                    _source.spatialBlend = 0f;
                }
                // One source for all: one-shots overlap, so the cha-ching and the wall sound play together
                _source.PlayOneShot(clip, volume);
            }
            catch (Exception e)
            {
                RaygunPlugin.Log.LogWarning($"Station sound {name} failed: {e.Message}");
            }
        }
    }

    /// <summary>
    /// The chalk outline: drawn from the gun's own inventory picture (icons\raygun.png, rendered at build time).
    /// Its silhouette's edge as a thick chalk line, the bigger shapes inside as fainter strokes, all broken up a little
    /// like chalk on a rough wall.
    /// </summary>
    internal static class RaygunChalk
    {
        private static Texture2D _made;

        /// <summary>The drawn outline's width and height as a share of the picture, and its middle's offset from the picture's centre (share of the picture, y up).</summary>
        public static float OutlineWidth = 1f, OutlineHeight = 1f;
        public static Vector2 OutlineCentre;

        public static Texture2D Make()
        {
            if (_made != null)
                return _made;
            string path = Path.Combine(RaygunPlugin.Dir, "icons", "raygun.png");
            if (!File.Exists(path))
            {
                RaygunPlugin.Log.LogWarning($"No {path}: the wall buy has no chalk outline");
                return null;
            }
            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            source.LoadImage(File.ReadAllBytes(path), false);
            const int w = 768;
            int h = Mathf.Max(32, w * source.height / source.width);

            var solid = new bool[w * h];
            var lum = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = source.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h);
                    solid[y * w + x] = c.a > 0.3f;
                    lum[y * w + x] = (c.r + c.g + c.b) / 3f;
                }
            }
            UnityEngine.Object.Destroy(source);

            int minX = w, maxX = -1, minY = h, maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (solid[y * w + x])
                    {
                        minX = Mathf.Min(minX, x);
                        maxX = Mathf.Max(maxX, x);
                        minY = Mathf.Min(minY, y);
                        maxY = Mathf.Max(maxY, y);
                    }
            if (maxX >= minX)
            {
                OutlineWidth = (maxX - minX + 1) / (float)w;
                OutlineHeight = (maxY - minY + 1) / (float)h;
                OutlineCentre = new Vector2((minX + maxX + 1) * 0.5f / w - 0.5f, (minY + maxY + 1) * 0.5f / h - 0.5f);
            }

            var random = new System.Random(935);
            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    // Silhouette edge: anything solid and empty within 2 px of each other
                    bool any = false, all = true;
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            bool s = xx >= 0 && yy >= 0 && xx < w && yy < h && solid[yy * w + xx];
                            any |= s;
                            all &= s;
                        }
                    }
                    float a = any && !all ? 0.95f : 0f;
                    // Inner detail: strong shading changes inside the gun, as thinner, fainter strokes
                    if (a == 0f && solid[i] && x > 0 && y > 0 && x < w - 1 && y < h - 1)
                    {
                        float gx = lum[i + 1] - lum[i - 1], gy = lum[i + w] - lum[i - w];
                        if (gx * gx + gy * gy > 0.035f)
                            a = 0.55f;
                    }
                    if (a > 0f)
                    {
                        // Chalk on a rough wall: uneven pressure and the odd skipped grain
                        a *= 0.65f + 0.35f * Mathf.PerlinNoise(x * 0.09f, y * 0.09f);
                        if (random.NextDouble() < 0.14)
                            a *= 0.25f;
                    }
                    pixels[i] = new Color32(238, 242, 255, (byte)(a * 255));
                }
            }
            _made = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "rg_chalk", wrapMode = TextureWrapMode.Clamp };
            _made.SetPixels32(pixels);
            _made.Apply(true, true);
            return _made;
        }
    }
}
