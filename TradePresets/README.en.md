# Trade Presets

Reuse your market thresholds in each new game.

- Applies saved buy/sell thresholds on a new game; auto-trade remains off until you turn it on in the market.
- Samples threshold changes during play and saves changed values when leaving the map or game. The last few seconds may not be captured.
- Loading a save preserves that save’s thresholds instead of overwriting them.
- Defaults include buying wood below 30 and hops below 2; review all values in the config before use.

New-game application is enabled by default. Buy and Sell sections store presets (0 = never buy, 1000 = never sell). Disabling application does not stop learning threshold changes. Disabled in multiplayer.

Single-player only; disabled in multiplayer.

Requires Script Extender 2.10.1+ and BepInEx Loader (install loader first). Subscribe and restart when prompted. Manual install: merge the ZIP’s BepInEx folder into the game folder; move an old short-name plugin folder outside plugins first, preserving config.

[Script Extender](https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender/-/releases)

Mod Options → Trade Presets · `BepInEx/config/TradePresets_Dongge.cfg`

Chinese and English fallback; restart after changing language. Feedback: include mod, game and extender versions and reproduction steps.

Dongge · MIT. Thanks to Rawra, BepInEx and Harmony contributors.

[Ko-fi](https://ko-fi.com/donggeliu) · [爱发电 / Afdian](https://afdian.com/a/donggeliu)

[Workshop / Feedback](https://steamcommunity.com/sharedfiles/filedetails/?id=3810027335)
