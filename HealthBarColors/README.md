# 血条颜色（Health Bar Colors）

按阵营给单位血条着色：己方绿、友方蓝、敌方红。可独立开关着色和未选中时常驻显示。中立单位保持原样。仅修改客户端显示；联机未测试。

- 盾牌色模式：其他玩家使用各自盾牌色，己方保持绿色，其他绿盾玩家改用你的盾牌色；并非全局唯一配色。
- 领主标记：血条放大 1.2 倍，配游戏原生皇冠徽章。
- 阵营着色、常驻血条、领主标记默认开启，可分别关闭。地图编辑器默认显示血条，可用 `[General] ShowInMapEditor = false` 关闭。

## 安装

需要手动安装 [SHCDE Script Extender 2.10.1 或更新及其 BepInEx Loader](https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender/-/releases)。先安装 Loader，再安装扩展器。

工坊版本：订阅后启动游戏，按扩展器提示重启。手动版本：关闭游戏，将 ZIP 中的 BepInEx 文件夹合并进游戏目录。不要同时保留手动版和工坊版；旧版 `BepInEx/plugins/HealthBarColors` 应移到插件目录之外备份，配置文件可保留。新目录为 `BepInEx/plugins/HealthBarColors_Dongge`。

## 设置及射程联动

遭遇战/征程界面左下角 Mod Options → Health Bar Colors。游戏中文使用中文设置页，其他语言使用英文。

自定义颜色：关闭游戏，编辑 `BepInEx/config/HealthBarColors_Dongge.cfg` 的 `[Colors]`：`Self`、`Ally`、`Enemy`（RRGGBBAA），再启动游戏。纯黑保留原血条颜色。盾牌色模式下，其他玩家不使用 Ally/Enemy 配色。

可独立使用，无需安装其他 Dongge mod。若同时安装 [Range Display](https://steamcommunity.com/sharedfiles/filedetails/?id=3808768637)，射程圈读取这里的阵营颜色；即使关闭本 mod 的着色开关，射程圈仍使用这些颜色配置。盾牌色模式也共享给射程圈。

## 反馈、支持与许可

游戏内“创意工坊 / 反馈”可打开本物品页面。报告问题请附 mod、游戏及扩展器版本和复现步骤。

自愿支持：[爱发电](https://afdian.com/a/donggeliu) · [Ko-fi](https://ko-fi.com/donggeliu)。

作者 Dongge；原创代码 MIT。感谢 Rawra 提供脚本扩展器和工坊工具，感谢 BepInEx/Harmony 贡献者。详见包内 LICENSE 和 THIRD_PARTY_NOTICES.md。

## 从源码构建

.NET SDK 与游戏/扩展器引用文件齐备后，`build.bat` 编译并安装。配置键与 GUID 保持兼容。
