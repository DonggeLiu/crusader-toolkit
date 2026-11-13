# 建筑实际产出

查看每座生产建筑实际消耗和交付的物资。

- 用物资图标显示消耗（红色）和产出（绿色）。
- 大字为最近最多 12 个完整游戏月的月平均，小字为本月至今；历史不足时显示横线。
- 保留作坊产品切换按钮；采石场石料堆及没有皮革作坊时的牛栏显示存量条。
- 采石场存量达到 90% 时显示琥珀色及牛运输提示，提醒检查运输；不计算精确缺少多少系牛桩。

默认开启，只统计本地玩家建筑。新局/读档重新统计，建筑建成当月不计入完整月份平均。Debug.LogEvents 默认关闭。

纯客户端显示，联机未测试。

需要 Script Extender 2.10.1+ 和 BepInEx Loader，先安装 Loader、再安装扩展器。工坊订阅后按提示重启。手动安装：将 ZIP 中 BepInEx 合并到游戏目录；旧版短名称插件目录须先移出 plugins，保留配置。

[Script Extender](https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender/-/releases)

Mod Options → Building Production · `BepInEx/config/BuildingProduction_Dongge.cfg`

中文及英文回退；修改语言后重启游戏。反馈请附 mod、游戏、扩展器版本和复现步骤。

Dongge · MIT. Thanks to Rawra, BepInEx and Harmony contributors.

[Ko-fi](https://ko-fi.com/donggeliu) · [爱发电 / Afdian](https://afdian.com/a/donggeliu)

[Workshop / Feedback](https://steamcommunity.com/sharedfiles/filedetails/?id=3810026973)
