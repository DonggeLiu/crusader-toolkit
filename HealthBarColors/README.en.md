# Health Bar Colors

Colors unit health bars by side: own units green, allies blue, enemies red. Recoloring and showing bars without selection can be toggled independently. Neutral units keep their original appearance. Client-side display only; multiplayer untested.

- Optional shield colors: others use their shield colors; yours stay green, and another green-shield player uses your shield color. This does not assign globally unique colors.
- Lord markers: 1.2x bars and the game’s own crown emblem.
- Faction recoloring, always-visible bars and lord markers default on with separate switches. Editor bars default on; disable with `[General] ShowInMapEditor = false`.

## Installation

Install [SHCDE Script Extender 2.10.1 or newer and its BepInEx Loader](https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender/-/releases) manually first. Install the Loader before the extender.

Workshop: subscribe, start the game and restart when prompted by the extender. Manual ZIP: close the game and merge its BepInEx folder into the game folder. Keep only one installation, either manual or Workshop. Move the old `BepInEx/plugins/HealthBarColors` folder outside the plugin tree as a backup; retain your config file. The new folder is `BepInEx/plugins/HealthBarColors_Dongge`.

## Settings and Range Display

Skirmish / trail screen → Mod Options → Health Bar Colors. Chinese game languages use Chinese settings; other languages use English.

To customize colors, close the game and edit `[Colors]` in `BepInEx/config/HealthBarColors_Dongge.cfg`: `Self`, `Ally`, `Enemy` (RRGGBBAA), then restart. Pure black keeps original bar colors. Shield-color mode replaces Ally/Enemy colors for other players.

Works independently; no other Dongge mod is required. If [Range Display](https://steamcommunity.com/sharedfiles/filedetails/?id=3808768637) is installed, its rings read these faction colors, even while health-bar recoloring is disabled. Shield-color mode also applies to the rings.

## Feedback, support and license

Use the in-game Workshop / Feedback button. Reports should include mod, game and extender versions plus reproduction steps.

Optional support: [Ko-fi](https://ko-fi.com/donggeliu) · [Afdian](https://afdian.com/a/donggeliu).

Created by Dongge. Original code: MIT. Thanks to Rawra for SHCDE Script Extender and Workshop tools, and the BepInEx/Harmony contributors. See LICENSE and THIRD_PARTY_NOTICES.md.

## Build from source

With the .NET SDK and game/extender references available, `build.bat` builds and installs. Existing GUID and config keys are preserved.
