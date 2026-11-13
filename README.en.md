# Dongge’s Crusader Toolkit

[中文](README.md)

Source code for 13 quality-of-life mods for Stronghold Crusader: Definitive Edition.

**Less repetitive clicking, a clearer view of your battles and economy.** Each mod does one specific job and works on its own, so install only the ones you want.

Players do not need this repository: install the prerequisites, then subscribe on the Steam Workshop. The collection is [Dongge’s Crusader Toolkit](https://steamcommunity.com/sharedfiles/filedetails/?id=3809713875).

## Mods

### Combat

| Mod | What it does | Workshop |
| --- | --- | --- |
| [Smart Shooters](TargetPriority/README.en.md) | Ranged troops coordinate effective fire to remove enemies sooner and reduce the fire they return. When the original game finds no target, they also shoot enemies behind walls or on top of a keep. | [Subscribe](https://steamcommunity.com/sharedfiles/filedetails/?id=3810645122) |
| [Artful Dodger](SmartDodge/README.en.md) | Horse archers stand and shoot as usual, sidestep when an arrow, bolt or stone is about to land on them, then return to their position. | [Subscribe](https://steamcommunity.com/sharedfiles/filedetails/?id=3811169708) |
| [Range Display](RangeDisplay/README.en.md) | Preview tower ranges while placing a tower, and see range rings for selected ranged units and siege engines, whether your own, allied or enemy. | [Subscribe](https://steamcommunity.com/sharedfiles/filedetails/?id=3808768637) |
| [Health Bar Colors](HealthBarColors/README.en.md) | Colors health bars by side: own units green, allies blue, enemies red. Lords get a larger bar with a crown. | [Subscribe](https://steamcommunity.com/sharedfiles/filedetails/?id=3809525668) |
| [Unit Stats](UnitStats/README.en.md) | Compare health, damage, range and speed directly above recruitment and siege construction menus, without hovering over each unit. | [Subscribe](https://steamcommunity.com/sharedfiles/filedetails/?id=3809728007) |

### Economy

| Mod | What it does | Workshop |
| --- | --- | --- |
| [Continuous Recruitment](ContinuousRecruit/README.en.md) | Keeps recruiting the troop types you choose, without repeated clicks. | [Subscribe](https://steamcommunity.com/sharedfiles/filedetails/?id=3810027038) |
| [Auto Tax](AutoTax/README.en.md) | Adjusts the tax rate to your actual food and other popularity effects, aiming to keep popularity near a target band (99–99.5 by default). It never pays subsidies on its own. | [Subscribe](https://steamcommunity.com/sharedfiles/filedetails/?id=3816416634) |
| [Trade Presets](TradePresets/README.en.md) | Reuses your saved market buy and sell thresholds in each new game. Auto-trade stays off until you turn it on in the market. | [Subscribe](https://steamcommunity.com/sharedfiles/filedetails/?id=3810027335) |
| [Stock Delta](StockDelta/README.en.md) | Shows net changes for last month and this month so far below stockpile, granary and armoury counts, so you can see whether stocks are growing or shrinking. | [Subscribe](https://steamcommunity.com/sharedfiles/filedetails/?id=3809944613) |
| [Granary Overview](GranaryOverview/README.en.md) | Combines food switches, stock, monthly changes and ration controls in one granary panel, with an estimated monthly balance for each ration level. | [Subscribe](https://steamcommunity.com/sharedfiles/filedetails/?id=3809944812) |
| [Building Production](BuildingProduction/README.en.md) | Shows what each production building actually consumes and delivers, inside its building panel. | [Subscribe](https://steamcommunity.com/sharedfiles/filedetails/?id=3810026973) |

### Building

| Mod | What it does | Workshop |
| --- | --- | --- |
| [Optimal Resource Layout](ResourceLayout/README.en.md) | Press a hotkey to plan farms, quarries, iron mines and pitch rigs around a limited resource area. Layouts appear as ghost buildings, and you decide whether to build them. | [Subscribe](https://steamcommunity.com/sharedfiles/filedetails/?id=3810027826) |
| [Build Grid](BuildGrid/README.en.md) | Shows a tile grid around the cursor while placing a building, to help align buildings and leave paths. | [Subscribe](https://steamcommunity.com/sharedfiles/filedetails/?id=3809944416) |

Hotkeys, settings, scope and limitations are described in each mod’s own README.

## Installation

1. Install the [SHCDE BepInEx Loader](https://gitlab.com/rawra-stronghold-crusader/shcde-bepinex/-/releases).
2. Install the [SHCDE Script Extender](https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender/-/releases), version 2.10.1 or newer.
3. Subscribe to the mods you want on the Workshop and restart the game when prompted.

## Multiplayer

These mods are designed for single-player, and none has been tested in multiplayer. Smart Shooters and Artful Dodger change unit behavior and may cause desyncs in multiplayer. Auto Tax and Trade Presets do nothing in multiplayer. See each mod’s README for details.

## Building from source

You need a .NET SDK and a local installation of the game, the BepInEx Loader and the Script Extender. The projects reference assemblies in the game folder; those files are not part of this repository.

```
dotnet build RangeDisplay/RangeDisplay.csproj -c Release -p:GameDir="your game folder"
```

- The output goes to the mod’s `bin/Release/` folder and does not touch the game.
- Add `-p:InstallToGame=true` to copy the plugin into the game’s `BepInEx/plugins/`. Close the game first.
- `GameDir` can be omitted when the game is in the default Steam location.

`tests/` contains unit tests that run without the game and need the .NET 8 SDK:

```
dotnet run --project tests/AutoTax/AutoTax.Tests.csproj
```

## Feedback and support

Report problems and suggestions on the mod’s Workshop page or in this repository’s Issues. Please include the mod, game and Script Extender versions and steps to reproduce.

If these mods help you, optional support is appreciated: [Ko-fi](https://ko-fi.com/donggeliu) · [Afdian](https://afdian.com/a/donggeliu).

## License and credits

Original code and documentation are released under the [MIT License](LICENSE). Thanks to Rawra for the Script Extender and Workshop tools, and to the BepInEx and Harmony contributors. Third-party software and game assets remain the property of their owners; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
