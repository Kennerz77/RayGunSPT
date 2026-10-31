# Ray Gun

The **Ray Gun** from Call of Duty Zombies, in SPT. The BO3 HD model with its real first-person animations and sounds, fed by cold fusion cells, small enough for your holster, with a chalk wall buy in your hideout to get it. Pack-a-Punch it into **Porter's X2 Ray Gun**.

Version 1.1.2, for **SPT 4.1.6**. Made by **Kennerz**.

## Requirements

| Mod | Version | Why |
|---|---|---|
| SPT | 4.1.x (made on 4.1.6) | |
| [Hideout Uncensored](https://sp-mod.com/mod/3167/hideout-uncensored) by **trappuss** ([GitHub](https://github.com/trappuss/SPT-Mods-Release)) | 1.3.0 or newer, **both** its client and server parts | Weapons anywhere in the hideout: the wall buy hands you the Ray Gun straight away, outside the shooting range |

Optional:

| Mod | What it adds |
|---|---|
| [Kennerz-PaPHideoutModule](https://github.com/Kennerz77/papHideoutModule) **2.0.0 or newer**, 2.1.0 recommended (by Kennerz) | Pack-a-Punch the Ray Gun into **Porter's X2 Ray Gun**: red glow, twenty shots a cell (forty a load), a faster fire rate and red bolts. Older versions of it are ignored. |

## Installing

1. Close the game and the SPT server.
2. Extract the zip into your SPT folder (the one with `EscapeFromTarkov.exe`). You should end up with:
   - `BepInEx\plugins\Raygun\` (`Raygun.dll`, `raygun_fx.bundle`, `icons\`, `sounds\`)
   - `SPT_Runtime\user\mods\Raygun\` (`RaygunServer.dll`, `bundles.json`, `bundles\`)
3. Start the server, then the game.

## Getting it

The mod gives you nothing for free and mails you nothing. Once your hideout's **side room is open** (the Emergency Wall upgrades done), there's a **chalk outline of the Ray Gun** on a wall in your hideout, with a green **ammo case** beside it.

- **The wall:** look at it and buy the Ray Gun for **935,000 ₽** (from your stash). It slides out of the wall, BO style, loaded with two full cells. If your holster is free it's equipped and drawn; otherwise it goes to your stash. The wall sells it again on your first hideout visit after a raid.
- **The ammo case:** buy a full **Cold Fusion Cell** for **10,000 ₽**. It goes onto a part-filled stack of cells on you first, then into a free spot in your pockets, vest or backpack, and only then into your stash. (While a weapon is out in the hideout, it goes to the stash.)
- Not enough money or no room: nothing is charged.

The wall and the ammo case are the **only** way to get the Ray Gun and its cells: they're never on the flea market, at traders, in loot, on bots or in daily rewards.

## The gun

- **Holster only:** a 2x1 sidearm, 1 kg empty and 1.15 kg loaded. Built on the sawed-off MP-43, so it has two chambers, a cell in each.
- **Firing:** one bolt per pull; hold the trigger to keep firing at the pace of BO3's own shot animation. The fire-mode key does nothing: it has one mode, automatic. No EFT recoil: the kick is BO3's. It never jams, misfires or wears out.
- **The bolt:** a green tracer on a slow arc, with a rings-and-smoke trail in BO3's style. Where it lands it bursts into a small cloud of gas that splashes outward (about 2.5 m, full strength within 0.7 m, no shrapnel), with BO3's ray explosion. A direct hit does 115.
- **Aiming:** shots go to the middle of the screen from the hip, and through the ring sight when aimed. Aiming tips the gun so you look through the rear hoop into the ring.
- **Power:** the tube, the dial's light and the sight come on when the power switch flicks on in the draw.
- **Cold Fusion Cells:** 1x1, ten shots each. A cell's charge is in its name (`Cold Fusion Cell (7/10)`). Only **full** cells stack (up to 6); part-used ones are a cell of their own. The Sort button in your inventory pours full cells together into stacks of six.
- **Reloading:** any time. Both chambers are swapped for the fullest cells you can reach. Part-used cells are thrown on the floor with their charges left, to pick up again; spent ones are gone.
- **Checking ammo:** shows "N charges left", with no animation.
- **Picking it up:** BO3's pickup sound plays when you take the Ray Gun off the ground, and again when you take it from the Pack-a-Punch machine.

## Porter's X2 Ray Gun (with Kennerz-PaPHideoutModule)

- It goes into the machine as a plain Ray Gun and comes out as Porter's X2 once you take it: red tube, sight and gauge, red bolts, a black needle and camo on the body that leaves the gold print alone.
- **Twenty shots a cell**, forty a load. A charge is spent every second shot. Only while a cell sits in the X2 does it say so: its name and description show out of 20. Taken out, it's an ordinary ten-shot cell.
- A faster fire rate (x1.35).
- Upgraded in PaP's **Origins** machine:
  - the red body turns to blue crystal
  - the dark parts and the dial face turn to metal
  - the tube and sight glow deep red, and the tube keeps its moving plasma, in red
- On the default machine the dial keeps its normal colours.
- With PaP 2.1.0 you can re-pack it in the other machine to swap between the two looks.
- Stripping its power makes it a Ray Gun again.

## Selling

Traders pay a flat **935,000 ₽** for the Ray Gun (plus the Pack-a-Punch fee, 500,000 or 5,000 depending on PaP's mode, when infused). A cell sells for 1,000 ₽ per charge left, and nothing once empty.

## Settings (F12)

- **Size:** **Default** is a little smaller and fitted to sit like EFT's own guns: the hand wraps the grip and the gun sits lower in view. **Large** is BO3's full size, as the Ray Gun first shipped. Both apply to the menu character too.
- **Volume:** **Shot**, **Reload and handling**, **Bolt impact**, **Pickup**, **Purchase**, **Wall buy** and **Deny**.

Prices are in `SPT_Runtime\user\mods\Raygun\config.json` (create it to change them):

```json
{ "WallBuyPrice": 935000, "CellPrice": 10000, "GunPrice": 935000 }
```

`GunPrice` is the Ray Gun's handbook value; `WallBuyPrice` and `CellPrice` are what the wall and the ammo case charge.

## Uninstalling

Delete `BepInEx\plugins\Raygun\` and `SPT_Runtime\user\mods\Raygun\`. Your profile stays healthy: the mod keeps its items (and anything pointing at them: wishlist, fast panel, favourites, insurance, builds) out of your profile file and adds them back when the server loads. Without the mod they're simply gone.

## Building

- `server\` and `src\`: `dotnet build -c Release`. They copy into the SPT install named by `GamePath` / `SptRuntime` in the `.csproj` files (pass `-p:GamePath=...` and `-p:SptRuntime=...` to point elsewhere).
- `build_bundle.ps1`: imports the model, animations and sounds from the Greyhound exports into `UnityProject` and builds the asset bundles (Unity 2022.3.43f1; Unity Hub must be running for the licence).
- `sounds_bo3\`: the Ray Gun's sounds, pulled from BO3's sound banks. `tools\` holds the helper scripts.

## Credits

- **Ray Gun, its model, animations and sounds:** Call of Duty: Black Ops III Zombies (Treyarch / Activision). This is a free, non-commercial fan mod; all Call of Duty assets belong to their owners. The BO3 HD model, animations and sounds were exported with Greyhound.
- **Arms rig** (the skeleton the first-person animations are retargeted onto): from TheAllNightFall's BO1 Thundergun port for the BO3 mod tools. That port's own credits: TheAllNightFall (weapon files, animations, sounds, textures, rigging), Humphrey (ripping the models and animations), prov3ntus, slimey, MJPW, Collie and Aszry, and the original Treyarch developers.
- **Hideout Uncensored** by trappuss ([page](https://sp-mod.com/mod/3167/hideout-uncensored), [GitHub](https://github.com/trappuss/SPT-Mods-Release)): holding the gun anywhere in the hideout, beyond the shooting range. Required; not included, install it separately.
- Made by Kennerz.

## Changelog

- **1.1.2:** a Size option in F12. Default is a little smaller with a better hand fit, and Large is the old size. The hand stays on the grip while you inspect the gun. Shots from the hip now land in the middle of the screen (they went slightly high).
- **1.1.1:** Origins look: the tube shows its moving plasma in deep red and the sight glows plain deep red (the dial face turns to metal with PaP 2.1.0); the glow no longer swaps back and forth.
- **1.1.0:** the Ray Gun and its cells only come from the wall buy and the ammo case (never flea, traders, loot, bots or daily rewards); Porter's X2 fires faster; Pack-a-Punch camo works on the Ray Gun again; Origins camo support and its own inventory picture; the dial keeps its colours when upgraded; Sort joins full cells into stacks of six reliably; cold fusion cells never take a camo. Needs PaP Hideout Module 2.0.0 or newer for the Pack-a-Punch features.
- **1.0.0:** first release.
