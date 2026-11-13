# Unit Stats

Compare unit health, damage, range, arrows/bolts to kill and speed directly above recruitment and siege construction menus, without hovering over each unit. Unit names use the game's translations. Custom text uses Chinese or English fallback. Client-side display only; multiplayer untested.

## Supported menus

Barracks, Mercenary Post, Bedouin Stockade, Engineers Guild, Tunnelers Guild, Cathedral, engineer siege construction and tower engine menus. Works independently; no other Dongge mod is required.

## Installation

Install [SHCDE Script Extender 2.10.1 or newer and its BepInEx Loader](https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender/-/releases) first. Subscribe and restart when prompted by the extender. Manual ZIP: close the game and merge BepInEx into the game folder. Move an old `BepInEx/plugins/UnitStats` installation outside plugins first; keep your configuration and avoid duplicate installations.

## Settings

Skirmish / trail screen → Mod Options → Unit Stats. Enabled by default. Adjust RightOffset and BottomOffset under Layout in `BepInEx/config/UnitStats_Dongge.cfg`. The table does not intercept mouse clicks. Long labels wrap; narrow HUDs scale the table down.

## Reading the values

- Health: default maximum health / 100.
- Melee damage: most common per-hit value across sampled recruitable troop targets, not universal damage against every target.
- Ranged damage: most common default-projectile damage across those targets / 100. A `-` means no applicable attack or unavailable data, not zero damage. Some fire and siege projectiles lack a corresponding damage table.
- Range (tiles): the default attack range, shown as a number. Former Chinese prefixes identified the projectile, not a multiplier or extra range. Only one default range is queried per unit; alternate attack modes are not enumerated.
- Arrows / bolts to kill: default full health divided by the relevant damage to that unit, rounded up. An estimate from tables, not a guarantee under every combat condition. Nonpositive damage is shown as Immune.
- Speed: Fast, Medium, Slow or Very slow; +charge denotes a charge bonus; Fixed denotes a stationary tower engine.

Damage samples do not include every game entity; ties choose the larger modal value. Melee damage scaling relative to health has not been exhaustively verified in combat. Values are refreshed when switching menus; the table is not a combat simulator and does not cover every matchup, special effect or attack mode.

## Feedback, support and license

Use the in-game Workshop button; include mod, game and extender versions and reproduction steps.
Optional support: [Ko-fi](https://ko-fi.com/donggeliu) · [Afdian](https://afdian.com/a/donggeliu).
Created by Dongge. Original code: MIT. Thanks to Rawra and the BepInEx/Harmony contributors. See LICENSE and THIRD_PARTY_NOTICES.md.

## Build

`build.bat` builds and installs.

[Workshop / Feedback](https://steamcommunity.com/sharedfiles/filedetails/?id=3809728007)
