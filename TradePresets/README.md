# 交易阈值预设

在新对局沿用保存的市场交易阈值。

- 新局载入保存的买卖阈值，自动交易仍关闭，需要时在市场手动开启。
- 对局中定期记录阈值，在离开地图或退出游戏时保存改动；最后几秒的改动可能来不及记录。
- 读档保留存档中的阈值，不用预设覆盖。
- 初始预设包括木材低于 30 买入、啤酒花低于 2 买入；使用前可在配置中检查所有数值。

默认开启新局套用。Buy / Sell 保存预设，0 表示不买、1000 表示不卖。关闭套用开关仍会记录阈值改动。多人游戏中不生效。

仅单机；多人游戏中不生效。

需要 Script Extender 2.10.1+ 和 BepInEx Loader，先安装 Loader、再安装扩展器。工坊订阅后按提示重启。手动安装：将 ZIP 中 BepInEx 合并到游戏目录；旧版短名称插件目录须先移出 plugins，保留配置。

[Script Extender](https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender/-/releases)

Mod Options → Trade Presets · `BepInEx/config/TradePresets_Dongge.cfg`

中文及英文回退；修改语言后重启游戏。反馈请附 mod、游戏、扩展器版本和复现步骤。

Dongge · MIT. Thanks to Rawra, BepInEx and Harmony contributors.

[Ko-fi](https://ko-fi.com/donggeliu) · [爱发电 / Afdian](https://afdian.com/a/donggeliu)

[Workshop / Feedback](https://steamcommunity.com/sharedfiles/filedetails/?id=3810027335)
