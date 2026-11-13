# Smart Shooters

Coordinate effective fire to remove enemies sooner and reduce the fire they can return.

- Default tiers (1 = first): 1 catapults and ballistas; 2 ranged troops; 3 engineers; 4 melee troops; 5 armed workers (woodcutters etc.) and beasts; 6 other civilians and animals; 7 tunnelers and support; 8 lords; 9 rams, siege towers and laddermen; 10 mantlets.
- Within the same priority the nearer target wins; a higher priority wins even when somewhat farther. Enabled fire allocation further considers expected kills and threats.
- Applies to archers, crossbowmen, slingers, horse archers, fire throwers and Bedouin ranged units; catapults, mangonels and ballistas keep vanilla targeting.
- Separate switches for your own, allied and enemy ranged units (default: yours only); lions and hyenas are shot when they attack, as in vanilla.
- Fire allocation is on by default: account for airborne arrows and prepared shots, then allocate the estimated fire needed for kills. Planned shots operate on flat ground and towers using hit chances and expected kills. Approaching melee take priority; very tough targets move back within their tier, and moving targets have lower estimated hit chances.
- “Allocate fire by what it takes to kill” controls allocation and planned shots together; disabling it retains category priorities. Mod Options also has the arc-aware check switch.
- `[General] ShootWildAnimals` enables shooting non-attacking lions and hyenas; `[Fire]` contains allocation settings.
- Arc-aware range check: the original game only checks the straight line to an enemy, so archers below a wall ignore enemies behind it or on a keep (a manual order still hits them). Now, when the original check finds no target, archers shoot these enemies on their own; enemies in plain sight still come first. Turn off with `ShootOverWalls = false`.

Enabled by default. Set each of the ten categories to a tier with −/+ in Mod Options or in the [Priority] config section (1 first, 10 last). Checks the game code at start and stays inactive if it differs.

Designed for single-player. Multiplayer is untested and may cause desyncs; use at your own risk.

Requires Script Extender 2.10.1+ and BepInEx Loader (install loader first). Subscribe and restart when prompted. Manual install: merge the ZIP’s BepInEx folder into the game folder.

[Script Extender](https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender/-/releases)

Mod Options → Smart Shooters · `BepInEx/config/TargetPriority_Dongge.cfg`

Chinese and English fallback; restart after changing language. Feedback: include mod, game and extender versions and reproduction steps.

Dongge · MIT. Thanks to Rawra, BepInEx and Harmony contributors.

[Ko-fi](https://ko-fi.com/donggeliu) · [爱发电 / Afdian](https://afdian.com/a/donggeliu)

[Workshop / Feedback](https://steamcommunity.com/sharedfiles/filedetails/?id=3810645122)
