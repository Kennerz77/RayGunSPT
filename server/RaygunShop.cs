using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.DI.Routing;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Common.Request;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.Models.Spt.Inventory;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Utils;

namespace RaygunServer;

/// <summary>Body of the client's purchase: "weapon" (the chalk wall) or "cell" (the ammo box).</summary>
public record RaygunBuyRequest : BaseInteractionRequestData
{
    [JsonPropertyName("item")]
    public string? Item { get; set; }

    /// <summary>False while the client's inventory is a copy (the hideout's weapons mode): cells then go to the stash.</summary>
    [JsonPropertyName("carry")]
    public bool Carry { get; set; }
}

/// <summary>
/// The hideout's Ray Gun wall buy and cell box, the mod's own way to get the gun (no Pack-a-Punch needed). Paid in
/// roubles from the stash; the bought items go into the stash (or the holster, for the gun) through the reply, so
/// the client sees them at once. Prices are in config.json.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class RaygunShop(ISptLogger<RaygunShop> logger, PaymentService paymentService, InventoryHelper inventoryHelper,
    ItemHelper itemHelper, HttpResponseUtil httpResponseUtil)
{
    public const string BuyAction = "RaygunBuy";
    private const string Roubles = "5449016a4bdc2d6f028b456f";
    private const string HolsterSlot = "Holster";

    /// <summary>The Emergency Wall level at which the hideout's side room is fully open.</summary>
    public const int SideRoomLevel = 6;

    private readonly RaygunConfig _config = RaygunConfig.Load(message => logger.Error(message));

    public ItemEventRouterResponse Buy(PmcData pmcData, RaygunBuyRequest body, MongoId sessionId, ItemEventRouterResponse output)
    {
        (List<Item> items, int price, string what) = body.Item switch
        {
            "weapon" => (Weapon(), _config.WallBuyPrice, "Ray Gun"),
            "cell" => (Cell(), _config.CellPrice, "Cold Fusion Cell"),
            _ => (new List<Item>(), 0, ""),
        };
        if (items.Count == 0)
            return httpResponseUtil.AppendErrorToOutput(output, "Nothing to buy there.");
        // The stations only exist once the hideout's side room is open (the Emergency Wall upgrades done)
        if (pmcData.Hideout?.Areas?.Any(a => a.Type == HideoutAreas.EmergencyWall && a.Level >= SideRoomLevel) != true)
            return httpResponseUtil.AppendErrorToOutput(output, "Nothing to buy there.");

        // Checked before any money moves: enough roubles, and room for it
        double roubles = pmcData.Inventory?.Items?.Where(i => i.Template.ToString() == Roubles).Sum(i => i.Upd?.StackObjectsCount ?? 1) ?? 0;
        if (roubles < price)
            return httpResponseUtil.AppendErrorToOutput(output, "Not enough funds.");
        // The gun goes straight into the holster when it's empty
        bool holster = body.Item == "weapon" && HolsterFree(pmcData);
        // A bought cell goes to the character first (a part-filled stack, else a free spot in pockets, vest or
        // backpack), and to the stash only when there's no room on them: a part-filled stack there, else a new one
        Item? stack = null;
        bool carried = false;
        if (body.Item == "cell")
        {
            if (body.Carry)
                stack = PartialStack(pmcData, carried: true);
            if (stack == null && body.Carry)
                carried = FindCarriedSpot(pmcData, items[0]);
            if (stack == null && !carried)
                stack = PartialStack(pmcData, carried: false);
        }
        if (!holster && stack == null && !carried && !inventoryHelper.CanPlaceItemsInInventory(sessionId, [items]))
            return httpResponseUtil.AppendErrorToOutput(output, "No room in your stash.");

        // The 5-argument overload exists on every SPT 4.1.x (the newer one with requestedStackIds may not); it only
        // passes null for those stack ids, so it behaves the same. Marked obsolete for removal in a later SPT.
#pragma warning disable CS0618
        paymentService.AddPaymentToOutput(pmcData, Money.ROUBLES, price, sessionId, output);
#pragma warning restore CS0618
        if (output.Warnings?.Count > 0)
            return output;
        if (holster)
            Equip(pmcData, items, sessionId, output);
        else if (stack != null)
            AddToStack(stack, sessionId, output);
        else if (carried)
            AddCarried(pmcData, items[0], sessionId, output);
        else
            inventoryHelper.AddItemsToStash(sessionId, new AddItemsDirectRequest { ItemsWithModsToAdd = [items], FoundInRaid = false }, pmcData, output);
        logger.Info($"[Raygun] {pmcData.Info?.Nickname} bought a {what} for {price} RUB{(holster ? ", equipped in the holster" : "")}");
        return output;
    }

    private static readonly string[] CarrySlots = ["Pockets", "TacticalVest", "Backpack"];

    /// <summary>The containers the character wears (pockets, vest, backpack).</summary>
    private static List<Item> CarryContainers(PmcData pmcData)
    {
        string? equipment = pmcData.Inventory?.Equipment?.ToString();
        if (equipment == null || pmcData.Inventory?.Items == null)
            return [];
        return CarrySlots.Select(s => pmcData.Inventory.Items.FirstOrDefault(i => i.ParentId == equipment && i.SlotId == s)).OfType<Item>().ToList();
    }

    /// <summary>A stack of full cells, on the character or in the stash, with room for one more.</summary>
    private static Item? PartialStack(PmcData pmcData, bool carried)
    {
        MongoId full = RaygunIds.Cell(RaygunIds.CellCharges);
        HashSet<string> parents = carried
            ? CarryContainers(pmcData).Select(c => c.Id.ToString()).ToHashSet()
            : pmcData.Inventory?.Stash?.ToString() is string stash ? [stash] : [];
        return pmcData.Inventory!.Items?.FirstOrDefault(i =>
            i.Template == full && i.ParentId != null && parents.Contains(i.ParentId) && (carried || i.SlotId == "hideout")
            && (i.Upd?.StackObjectsCount ?? 1) < RaygunIds.CellStack);
    }

    /// <summary>
    /// Finds a free spot for the cell in the character's containers (pockets, then vest, then backpack) and sets the
    /// cell's parent, grid and location to it. Nothing is added to the profile yet.
    /// </summary>
    private bool FindCarriedSpot(PmcData pmcData, Item cell)
    {
        foreach (Item container in CarryContainers(pmcData))
        {
            KeyValuePair<bool, TemplateItem?> template = itemHelper.GetItem(container.Template);
            foreach (Grid grid in template.Value?.Properties?.Grids ?? [])
            {
                if (grid.Name == null || grid.Properties?.CellsH == null || grid.Properties.CellsV == null)
                    continue;
                IEnumerable<Item> inGrid = pmcData.Inventory!.Items!.Where(i => i.ParentId == container.Id.ToString() && i.SlotId == grid.Name);
                int[,] map = inventoryHelper.GetContainerMap(grid.Properties.CellsH.Value, grid.Properties.CellsV.Value, inGrid, container.Id);
                FindSlotResult result = inventoryHelper.PlaceItemInContainer(map, [cell], container.Id.ToString(), grid.Name);
                if (result.Success == true)
                    return true;
            }
        }
        return false;
    }

    /// <summary>The cell, already given its spot on the character, added to the profile and to the reply.</summary>
    private static void AddCarried(PmcData pmcData, Item cell, MongoId sessionId, ItemEventRouterResponse output)
    {
        pmcData.Inventory!.Items!.Add(cell);
        if (output.ProfileChanges?.TryGetValue(sessionId, out ProfileChange? change) == true && change?.Items != null)
        {
            change.Items.NewItems ??= [];
            change.Items.NewItems.Add(cell);
        }
    }

    /// <summary>One more cell on the stack; the reply carries it as a changed item, which the client updates.</summary>
    private static void AddToStack(Item stack, MongoId sessionId, ItemEventRouterResponse output)
    {
        stack.Upd ??= new Upd();
        stack.Upd.StackObjectsCount = (stack.Upd.StackObjectsCount ?? 1) + 1;
        if (output.ProfileChanges?.TryGetValue(sessionId, out ProfileChange? change) == true && change?.Items != null)
        {
            change.Items.ChangedItems ??= [];
            change.Items.ChangedItems.Add(stack);
        }
    }

    private static bool HolsterFree(PmcData pmcData)
    {
        string? equipment = pmcData.Inventory?.Equipment?.ToString();
        return equipment != null && pmcData.Inventory?.Items != null
            && !pmcData.Inventory.Items.Any(i => i.ParentId == equipment && i.SlotId == HolsterSlot);
    }

    /// <summary>Into the holster; the reply carries it as new items under the equipment, which the client adds there.</summary>
    private static void Equip(PmcData pmcData, List<Item> items, MongoId sessionId, ItemEventRouterResponse output)
    {
        items[0].ParentId = pmcData.Inventory!.Equipment.ToString();
        items[0].SlotId = HolsterSlot;
        pmcData.Inventory.Items!.AddRange(items);
        if (output.ProfileChanges?.TryGetValue(sessionId, out ProfileChange? change) == true && change?.Items != null)
        {
            change.Items.NewItems ??= [];
            change.Items.NewItems.AddRange(items);
        }
    }

    /// <summary>The gun ready to fire: a full cold fusion cell in each chamber.</summary>
    private static List<Item> Weapon()
    {
        MongoId gun = new();
        MongoId full = RaygunIds.Cell(RaygunIds.CellCharges);
        return
        [
            new() { Id = gun, Template = RaygunIds.Weapon, Upd = new Upd { FireMode = new UpdFireMode { FireMode = "single" } } },
            new() { Id = new MongoId(), Template = full, ParentId = gun.ToString(), SlotId = "patron_in_weapon_000", Upd = new Upd { StackObjectsCount = 1 } },
            new() { Id = new MongoId(), Template = full, ParentId = gun.ToString(), SlotId = "patron_in_weapon_001", Upd = new Upd { StackObjectsCount = 1 } },
        ];
    }

    private static List<Item> Cell() =>
        [new() { Id = new MongoId(), Template = RaygunIds.Cell(RaygunIds.CellCharges), Upd = new Upd { StackObjectsCount = 1 } }];
}

/// <summary>Routes the client's RaygunBuy item event.</summary>
[Injectable(TypePriority = OnLoadOrder.Routers)]
public class RaygunShopRouter(RaygunShop shop) : ItemEventRouter([
    new ItemRouteAction<RaygunBuyRequest>(RaygunShop.BuyAction,
        (url, pmcData, body, sessionID, output, cancellationToken) => ValueTask.FromResult(shop.Buy(pmcData, body, sessionID, output))),
]) { }
