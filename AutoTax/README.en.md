# Auto Tax 1.0.0

Single-player tax automation, on by default. Development version; not accepted in-game or uploaded. Requires BepInEx and Script Extender 2.10.1+.

The provisional target band is 99–99.5, adjustable in 0.25 steps. Lower must be below upper, and upper below 100. Invalid settings suspend adjustments.

Tax levels adapt to actual food and other popularity effects. At the lower target, select the highest tax that still restores popularity. At the upper target, select the first deficit tax. Hold a balanced tax when available. Reassess at 100 and anticipate known cap overflow. Checks run every 0.25 real seconds while unpaused, without waiting a full month or limiting changes to one tax step.

Automation never selects donations. Manual donations remain until the player selects no tax or taxation again. Recovery may still collect taxes when bonuses allow it. If no tax cannot restore popularity, show a status message; never spend gold automatically. Discrete changes and changing conditions mean the target band is not a guaranteed bound.

Use the controls inside the keep’s tax parchment to toggle automation, adjust this game’s targets and read its status. Buttons keep a valid band. Settings can be edited while paused. Mod Options only sets defaults for new games. Match settings are embedded in normal game saves and restored on load; older saves without them use the defaults. Match edits never change global defaults. Save the game normally to retain changes. Only the local single-player lord is managed; multiplayer, spectators, editing and special economy cases with population at most 3 are excluded. Disabling preserves the current tax. Levels 0–2 are donations, 3 is no tax, and 4–11 are taxation. Config: `BepInEx/config/AutoTax_Dongge.cfg`.

The keep tax parchment contains a wax-seal toggle, status/reason display, popularity and adjustment count, 0.25 target controls and animated tax-slider changes.

“Extra” compares tax income against holding the tax level present when automation took over. It is not total collected tax or a measured gain against a separately played game. Normal saves store settings, extra earnings, baseline and adjustment count; older formats restore supported settings with extra earnings starting at zero.

Install BepInEx Loader first, then Script Extender 2.10.1+ from the [extender releases](https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender/-/releases). Manual ZIP: merge BepInEx into the game directory. A future Workshop subscription installs after the extender restart prompt. A private Workshop item has been created; public release and in-game acceptance remain pending.

MIT. Thanks to Rawra, BepInEx, Harmony and R3 contributors.

[Workshop / Feedback](https://steamcommunity.com/sharedfiles/filedetails/?id=3816416634)
