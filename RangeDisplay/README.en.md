# Range Display

Displays dashed range rings for selected ranged units and siege engines, a dotted search range for selected Healers, and tower ranges while you place a tower, including allied and enemy units. Client-side display; designed for single-player, with multiplayer untested and synchronization compatibility not guaranteed. Does not change unit range or issue unit orders.

## Installation

Install the [SHCDE BepInEx loader](https://gitlab.com/rawra-stronghold-crusader/shcde-bepinex/-/releases) and [SHCDE Script Extender](https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender/-/releases) first, following their installation instructions. This mod requires Script Extender 2.10.1 or newer; use an extender release compatible with your game version.

- Recommended: subscribe to [Range Display on Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3808768637), launch the game and accept the extender's restart prompt.
- Manual download: extract the release ZIP into the game directory, preserving `BepInEx/plugins/RangeDisplay_Dongge/`. Do not install both a manual copy and a Workshop copy. Older manual versions in `BepInEx/plugins/RangeDisplay/` should be backed up outside the plugins directory and removed from that directory before switching.
- Workshop updates are handled by the extender. To remove a Workshop installation, unsubscribe and restart the game. For a manual installation, close the game and remove only `BepInEx/plugins/RangeDisplay_Dongge/`. Configuration is separate in `BepInEx/config/RangeDisplay_Dongge.cfg`.

## Controls and settings

- Select your own units normally. Click or drag to select allied/enemy units for viewing; hold Shift to add. Empty-ground clicks, right-click or Esc clear this additional selection.
- F8 toggles rings by default; the key is configurable.
- Mod Options → Range Display controls rings, siege-engine ranges, the Healer search range and the tower placement preview.
- The settings page uses Chinese for Chinese game languages and English otherwise. Restart after changing game language if the settings page remains in the previous language.
- Colors, opacity, line width, dash length and ring limit are in the configuration file. Health Bar Colors is optional; if installed, its faction colors and optional shield-color mode are used even while health-bar recoloring is disabled.

Rings show horizontal range from the game's range API, not a guarantee of an unobstructed shot. Towers do not extend range, despite the in-game tooltip: the native range checks compare horizontal distance only, and in-game tests showed archers on every tower type opening fire at the same distance as on the ground. Idle units open fire on their own only within about 50 tiles (the engage radius); the ring shows the full 54-tile attack range used when ordered to attack or already fighting. Units sharing a tile, range and side share a ring; the default drawing limit is 80 rings.

The Healer ring is the area a Healer searches for wounded: 4 tiles on Stand Ground, 7 on Defensive and 10 on Aggressive stance (4 when not in a group), drawn at exactly that distance. A Healer on a tower outlines the whole tower instead, because it heals your wounded anywhere on the same tower. A Healer cannot heal units on towers from the ground, units behind walls or allies, and never heals itself.

While you place a Lookout Tower, Perimeter Turret, Defense Turret, Square Tower or Round Tower, the dashed ring shows the range of archers on it (54 tiles). Square and Round Towers can mount engines, so they also show the mangonel (70 tiles, inner) and ballista (85 tiles, outer) ranges as long dashes; turn these off with the siege-engine option. Archers can stand anywhere on the tower top, so the ring covers the range from every top tile. A semi-transparent archer, mangonel or ballista icon, taken from the game's own UI atlas, sits at the middle of each ring's visible arc to show whose range it is.

## Feedback and credits

Use the Workshop comments or discussions. Include mod, game and Script Extender versions, reproduction steps, relevant BepInEx log errors and screenshots where useful. Remove personal information before sharing logs.

Created by Dongge. Thanks to Rawra for Script Extender and Workshop tools, and the BepInEx and Harmony contributors. Original mod code is MIT licensed; third-party software and game assets retain their respective rights. See the included LICENSE and THIRD_PARTY_NOTICES.md.

If you enjoy this mod, optional support is welcome on [Ko-fi](https://ko-fi.com/donggeliu) or [Afdian](https://afdian.com/a/donggeliu).

## Building

Requires a .NET SDK and the locally installed game and extender assemblies. Build without modifying the game:

```powershell
dotnet build RangeDisplay.csproj -c Release -p:InstallToGame=false
```

Override `GameDir` and `ExtenderDir` with MSBuild properties if needed. `build.bat` retains the local development behavior of installing after building.
