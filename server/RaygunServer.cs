using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Services.Items;
using SPTarkov.Server.Core.Services.Modding.Custom;
using Path = System.IO.Path;

namespace RaygunServer;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.kennerz.raygun"; // same GUID as the client plugin (Forge rule)
    public string Name { get; init; } = "Raygun";
    public string Author { get; init; } = "Kennerz";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.1.1");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public List<string>? Incompatibilities { get; init; }
    /// <summary>Hideout Uncensored provides weapons in the hideout, where the wall buy is (the client depends on it too).</summary>
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        ["com.trappuss.hideoutuncensored"] = new SemanticVersioning.Range(">=1.3.0"),
    };
    public string? Url { get; init; } = "https://github.com/Kennerz77/RayGunSPT";
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; } = false;
}

/// <summary>Fixed template ids. They share a prefix so the profile store can spot them in any item list.</summary>
internal static class RaygunIds
{
    public const string Prefix = "6a7e5a71d9a11600000000";

    public static readonly MongoId Weapon = new(Prefix + "01");
    // 0.1.0's magazine-style cell and its charge: no longer items, migrated out of profiles (RaygunMigration)
    public const string OldCell = Prefix + "02";
    public const string OldCharge = Prefix + "03";

    /// <summary>
    /// The cold fusion cell holding <paramref name="charges"/> (1-10) shots: one stackable ammo template per charge
    /// level, ids ...c1 to ...ca. A shot from a chambered cell swaps it for the next level down (client mod).
    /// </summary>
    public static MongoId Cell(int charges) => new(CellId(charges));

    public static string CellId(int charges) => Prefix + (0xC0 + charges).ToString("x2");

    public static IEnumerable<MongoId> AllCells => Enumerable.Range(1, CellCharges).Select(Cell);

    // Vanilla templates the Ray Gun items are cloned from: the sawed-off MP-43 (two chambers, loaded like shells)
    public static readonly MongoId SawedOff = new("64748cb8de82c85eaf0a273a");
    public static readonly MongoId Slug = new("58820d1224597753c90aeb13");
    public static readonly MongoId DefaultInventory = new("55d7217a4bdc2d86028b456d");
    public static readonly MongoId Roubles = new("5449016a4bdc2d6f028b456f");

    public const string WeaponParent = "5447b6094bdc2dc3278b4567";   // Shotgun
    public const string MagazineParent = "5448bc234bdc2d3c308b4569"; // Magazine
    public const string AmmoParent = "5485a8684bdc2da71d8b4567";     // Ammo
    public const string HandbookPistols = "5b5f792486f77447ed5636b3";
    public const string HandbookMagazines = "5b5f754a86f774094242f19b";
    public const string HandbookRounds = "5b47574386f77428ca22b33b";

    // Bundle keys: paths under the mod's bundles folder (bundles.json), built by build_bundle.ps1
    public const string BundleFolder = "Raygun";
    public const string WeaponBundle = "weapon_raygun_container.bundle";
    public const string CellBundle = "mag_raygun_cell.bundle";

    public const int CellCharges = 10; // two cells, one per chamber: BO's 20-shot Ray Gun
    public const int CellStack = 6;

    public static bool IsOurs(string? templateId) => templateId != null && templateId.StartsWith(Prefix);
}

/// <summary>Settings read from config.json next to the DLL. Never shipped, so an update can't overwrite it.</summary>
public record RaygunConfig
{
    /// <summary>The Ray Gun's handbook value, in roubles.</summary>
    public int GunPrice { get; init; } = 935_000;

    /// <summary>The hideout chalk wall's price for a loaded Ray Gun, in roubles.</summary>
    public int WallBuyPrice { get; init; } = 935_000;

    /// <summary>One full cell (ten shots): at the trader, and at the hideout ammo box.</summary>
    public int CellPrice { get; init; } = 10_000;

    internal static RaygunConfig Load(Action<string> logError)
    {
        string path = Path.Combine(Path.GetDirectoryName(typeof(RaygunConfig).Assembly.Location)!, "config.json");
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<RaygunConfig>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception e)
        {
            logError($"[Raygun] Could not read {path}, using defaults: {e.Message}");
        }
        return new();
    }
}

/// <summary>
/// Adds the Ray Gun (a sawed-off MP-43 clone: two chambers) and its cold fusion cells, and puts the gun and cells on a
/// trader. Items must exist before profiles load, hence Preload. Until the Ray Gun bundles exist the clones keep the
/// vanilla models.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public class RaygunItemLoader(
    ISptLogger<RaygunItemLoader> logger,
    CustomItemService customItemService,
    TemplateTable templateTable,
    LocaleTable localeTable,
    ItemConfig itemConfig,
    ItemFilterService itemFilterService) : IOnLoad
{

    /// <summary>
    /// Wall-buys and ammo boxes only: out of random loot, bot gear, flea and daily rewards. (A Pack-a-Punched enemy gun
    /// is PaP's own 1-in-200 roll, not this item showing up.)
    /// </summary>
    private void KeepOutOfTheWorld(IEnumerable<MongoId> ids)
    {
        List<MongoId> all = ids.ToList();
        foreach (MongoId id in all)
        {
            itemConfig.Blacklist.Add(id);
            itemConfig.LootableItemBlacklist.Add(id);
            itemConfig.RewardItemBlacklist.Add(id);
        }
        itemFilterService.AddItemToBlacklistCache(all);
        itemFilterService.AddItemToLootableBlacklistCache(all);
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var items = templateTable.Items;
        RaygunConfig config = RaygunConfig.Load(m => logger.Error(m));

        TemplateItem? weapon = Clone(RaygunIds.SawedOff, RaygunIds.WeaponParent, RaygunIds.Weapon, "weapon_group935_raygun",
            RaygunIds.HandbookPistols, config.GunPrice, "Ray Gun", "Ray Gun",
            "A Group 935 wonder weapon. Fires bolts of green energy. Takes two cold fusion cells, ten shots each, and fits a holster.");
        if (weapon?.Properties == null)
            return Task.CompletedTask;

        var cells = new List<TemplateItem>();
        for (int n = RaygunIds.CellCharges; n >= 1; n--)
        {
            bool full = n == RaygunIds.CellCharges;
            TemplateItem? cell = Clone(RaygunIds.Slug, RaygunIds.AmmoParent, RaygunIds.Cell(n), $"patron_raygun_cell_{n}",
                RaygunIds.HandbookRounds, config.CellPrice * n / RaygunIds.CellCharges,
                $"Cold Fusion Cell ({n}/{RaygunIds.CellCharges})", full ? "Cell" : $"Cell {n}",
                $"A Group 935 cold fusion cell with {n} of its {RaygunIds.CellCharges} shots left. Loads into a Ray Gun's chamber like a shell. " +
                (full ? "Full cells stack." : "Only full cells stack."));
            if (cell?.Properties == null)
                return Task.CompletedTask;
            Cell(cell.Properties);
            // Only full cells stack: a part-used one is a cell of its own
            cell.Properties.StackMaxSize = full ? RaygunIds.CellStack : 1;
            cells.Add(cell);
        }
        HashSet<MongoId> cellsOnly = [.. RaygunIds.AllCells];

        TemplateItemProperties gun = weapon.Properties;
        gun.AmmoCaliber = Caliber;
        // 1 kg empty, 1.15 kg with its two cells
        gun.Weight = 1.0;
        gun.DurabilityBurnRatio = 0;
        // A pistol's footprint
        gun.Width = 2;
        gun.Height = 1;
        gun.Foldable = false;
        // No barrel slot (the MP-43's barrel is a part we don't have); the two chambers take cells only
        gun.Slots = [];
        gun.Chambers = gun.Chambers?.Select(c => WithFilter(c, cellsOnly)).ToList();
        gun.DefAmmo = RaygunIds.Cell(RaygunIds.CellCharges);
        // "single": the game fires one bolt a pull; the client (RaygunAuto) keeps firing while the trigger is held. The game's
        // own automatic mode uses a fire operation with no multi-chamber support and throws on every shot
        gun.WeapFireType = ["single"];
        // One shot per pull: a click during the shot isn't queued as a second shot
        gun.CanQueueSecondShot = false;

        // Never jams, misfires or wears out
        gun.AllowJam = false;
        gun.AllowFeed = false;
        gun.AllowMisfire = false;
        gun.AllowSlide = false;
        gun.BaseMalfunctionChance = 0;
        gun.AllowOverheat = false;

        // No EFT recoil: the kick is BO3's fire animation
        gun.RecoilForceUp = 0;
        gun.RecoilForceBack = 0;
        gun.RecoilCamera = 0;
        if (gun.WeaponRecoilSettings != null)
            gun.WeaponRecoilSettings.Enable = false;
        // Held trigger keeps firing (client RaygunAuto) at the pace of BO3's shot animation
        gun.BFirerate = 120;
        gun.SingleFireRate = 120;

        foreach (TemplateItem item in cells.Append(weapon))
            items[item.Id] = item;
        AllowInSlots(items);
        UseOwnModels(weapon.Properties, cells);
        AddCaliberName();
        KeepOutOfTheWorld(RaygunIds.AllCells.Append(RaygunIds.Weapon));
        logger.Success($"[Raygun] Items added: Ray Gun, Cold Fusion Cell (1-{RaygunIds.CellCharges} charges)");
        return Task.CompletedTask;
    }

    /// <summary>The bolt: one slow, bright green projectile with real stopping power, from a cell that never fails.</summary>
    private static void Cell(TemplateItemProperties cell)
    {
        cell.Width = 1;
        cell.Height = 1;
        cell.Weight = 0.075;
        cell.StackMaxSize = RaygunIds.CellStack;
        cell.ProjectileCount = 1;
        cell.InitialSpeed = 140;
        cell.BulletMassGram = 30;
        cell.BulletDiameterMilimeters = 20;
        cell.BallisticCoeficient = 0.35;
        cell.Damage = 115;
        // Explodes where it hits (the game's explosive-round handling): a ball of gas, no shrapnel. The client mod
        // draws it (RaygunBolts): the game has no effect by this name, so it shows nothing of its own
        cell.HasGrenaderComponent = true;
        cell.ExplosionType = ExplosionName;
        cell.FuzeArmTimeSec = 0;
        cell.ExplosionStrength = 60;
        cell.MinExplosionDistance = 0.7;
        cell.MaxExplosionDistance = 2.5;
        cell.FragmentsCount = 0;
        cell.ShowHitEffectOnExplode = false;
        cell.Blindness = new() { X = 0, Y = 0, Z = 0 };
        cell.Contusion = new() { X = 0.5f, Y = 2f, Z = 2f };
        cell.ArmorDistanceDistanceDamage = new() { X = 0.7f, Y = 2.5f, Z = 15f };
        cell.PenetrationPower = 40;
        cell.ArmorDamage = 70;
        cell.Tracer = true;
        cell.TracerColor = "green";
        cell.TracerDistance = 0;
        cell.DurabilityBurnModificator = 0;
        cell.HeatFactor = 0;
        cell.MalfMisfireChance = 0;
        cell.MisfireChance = 0;
        cell.MalfFeedChance = 0;
        cell.Caliber = Caliber;
        cell.CanSellOnRagfair = false;
    }

    private const string Caliber = "CaliberColdFusion";

    /// <summary>The bolt's explosion: no effect of the game's goes by this name; the client mod shows its own.</summary>
    private const string ExplosionName = "raygun_bolt";

    /// <summary>The calibre's name in the stats, every language: the game looks it up both with and without "Caliber".</summary>
    private void AddCaliberName()
    {
        foreach (var locale in localeTable.Global.Values)
        {
            locale.AddTransformer(dictionary =>
            {
                if (dictionary != null)
                {
                    dictionary[Caliber] = "Cold Fusion";
                    dictionary[Caliber.Replace("Caliber", "")] = "Cold Fusion";
                }
                return dictionary;
            });
        }
    }

    private TemplateItem? Clone(MongoId from, string parent, MongoId id, string name, string handbookParent, int price,
        string locName, string shortName, string description)
    {
        CreateItemResult result = customItemService.CreateItemFromClone(new NewItemFromCloneDetails
        {
            ItemTplToClone = from,
            ParentId = parent,
            NewId = id,
            NewItemName = name,
            AddToHandbook = true,
            HandbookParentId = handbookParent,
            HandbookPriceRoubles = price,
            AddToFleaPriceDb = false,
            AddToWeaponShelf = false,
            Locales = new Dictionary<string, LocaleDetails>
            {
                ["en"] = new() { Name = locName, ShortName = shortName, Description = description },
            },
        });
        if (result.Success != true || !templateTable.Items.TryGetValue(id, out TemplateItem? item))
        {
            logger.Error($"[Raygun] Could not add {name}: {string.Join("; ", result.Errors ?? [])}");
            return null;
        }
        return item;
    }

    /// <summary>
    /// Points the items at the Ray Gun's own bundles (bundles/Raygun/, served through bundles.json). Without them the
    /// clones keep the vanilla models, so the items still work while the bundles are being built.
    /// </summary>
    private void UseOwnModels(TemplateItemProperties gun, List<TemplateItem> cells)
    {
        string dir = Path.Combine(Path.GetDirectoryName(typeof(RaygunItemLoader).Assembly.Location)!, "bundles", RaygunIds.BundleFolder);
        bool Has(string bundle) => File.Exists(Path.Combine(dir, bundle));
        if (!Has(RaygunIds.WeaponBundle) || !Has(RaygunIds.CellBundle))
        {
            logger.Warning($"[Raygun] Bundles missing in {dir}; using the vanilla models");
            return;
        }
        gun.Prefab = new Prefab { Path = $"{RaygunIds.BundleFolder}/{RaygunIds.WeaponBundle}", Rcid = "" };
        foreach (TemplateItem cell in cells)
            cell.Properties!.Prefab = new Prefab { Path = $"{RaygunIds.BundleFolder}/{RaygunIds.CellBundle}", Rcid = "" };
    }

    /// <summary>
    /// The player's weapon slots list shotguns one template at a time (not the Shotgun class), so a clone is
    /// "incompatible" until it's added by id. Only the holster takes it, like the sawed-off MP-43: it's a pistol-slot gun.
    /// </summary>
    private void AllowInSlots(Dictionary<MongoId, TemplateItem> items)
    {
        if (!items.TryGetValue(RaygunIds.DefaultInventory, out TemplateItem? inventory) || inventory.Properties?.Slots == null)
        {
            logger.Error("[Raygun] Default inventory template not found; the Ray Gun can't be equipped");
            return;
        }
        foreach (Slot slot in inventory.Properties.Slots.Where(s => s.Name == "Holster"))
        {
            foreach (SlotFilter filter in slot.Properties?.Filters ?? [])
                filter.Filter?.Add(RaygunIds.Weapon);
        }
    }

    // New records all the way down, so the vanilla template's filter lists are never touched
    private static Slot WithFilter(Slot slot, HashSet<MongoId> filter) => slot with
    {
        Properties = new SlotProperties { Filters = [new SlotFilter { Filter = [.. filter], Shift = 0 }] },
    };
}
