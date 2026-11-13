# 资源区智能排布

在有限资源区规划农场和工业建筑布局。

- Shift+X 规划农场，Shift+N 规划鼠标附近的采石场、铁矿或沥青坑。
- 点击图标选择农场；Shift+1…4 增加最少座数，Alt+Shift+1…4 减少。
- 左键固定，右键取消并清除已固定布局；单机中双击固定幽灵可建造，照常校验位置并扣资源。
- 明确区分已证明最优与限时内找到的最好布局；设置的最少座数不一定放得下。
- 采石场规划附带估算的系牛桩位置；实际效果受距离、拥堵及工人通行影响。新局/读档会清除固定布局。

默认开启，General.TimeLimitMs 默认 5000 毫秒。FarmSelection / FarmMinimums 保存选择。Debug.DebugTools 默认关闭，开发建造/删除键不属于日常操作。多人模式禁用双击直接建造及开发工具；规划功能联机未测试。

双击直接建造及开发工具仅限单机；规划功能联机未测试。

需要 Script Extender 2.10.1+ 和 BepInEx Loader，先安装 Loader、再安装扩展器。工坊订阅后按提示重启。手动安装：将 ZIP 中 BepInEx 合并到游戏目录；旧版短名称插件目录须先移出 plugins，保留配置。

[Script Extender](https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender/-/releases)

Mod Options → Resource Layout · `BepInEx/config/ResourceLayout_Dongge.cfg`

中文及英文回退；修改语言后重启游戏。反馈请附 mod、游戏、扩展器版本和复现步骤。

Dongge · MIT. Thanks to Rawra, BepInEx and Harmony contributors.

[Ko-fi](https://ko-fi.com/donggeliu) · [爱发电 / Afdian](https://afdian.com/a/donggeliu)

[Workshop / Feedback](https://steamcommunity.com/sharedfiles/filedetails/?id=3810027826)
