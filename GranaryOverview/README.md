# 粮仓总览

在同一粮仓面板比较配给与食物收支。

- 合并食物开关、库存、月度变化和配给按钮，沿用游戏图标。
- 估算各配给等级的月度收支和库存可维持时间。
- 预计产量不足时，提示补建农场或面包产业链建筑。
- 产量来自最近最多三个游戏月的工人送货，不将市场买入算作生产；读档后约需四分之一个月积累数据。

默认开启。补建座数和剩余时间均为估算，受运输距离及近期产量影响；没有样本时使用默认产量。Debug.LogFood 默认关闭。

纯客户端显示，联机未测试。

需要 Script Extender 2.10.1+ 和 BepInEx Loader，先安装 Loader、再安装扩展器。工坊订阅后按提示重启。手动安装：将 ZIP 中 BepInEx 合并到游戏目录；旧版短名称插件目录须先移出 plugins，保留配置。

[Script Extender](https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender/-/releases)

Mod Options → Granary Overview · `BepInEx/config/GranaryOverview_Dongge.cfg`

中文及英文回退；修改语言后重启游戏。反馈请附 mod、游戏、扩展器版本和复现步骤。

Dongge · MIT. Thanks to Rawra, BepInEx and Harmony contributors.

[Ko-fi](https://ko-fi.com/donggeliu) · [爱发电 / Afdian](https://afdian.com/a/donggeliu)

[Workshop / Feedback](https://steamcommunity.com/sharedfiles/filedetails/?id=3809944812)
