# Dongge’s Crusader Toolkit

[English](README.en.md)

《要塞：十字军东征 决定版》（Stronghold Crusader: Definitive Edition）的 13 个便利性 Mod 的源码。

**少做重复操作，多看清战场和经济。** 每个 Mod 解决一件具体的事，彼此独立，可以只装需要的。

玩家不需要下载这个仓库：装好前置后，在 Steam 创意工坊订阅即可，合集见 [Dongge’s Crusader Toolkit](https://steamcommunity.com/sharedfiles/filedetails/?id=3809713875)。

## Mod 列表

### 战斗

| Mod | 作用 | 创意工坊 |
| --- | --- | --- |
| [Smart Shooters 聪明射手](TargetPriority/README.md) | 远程部队集中有效火力，争取尽早消灭敌人，减少敌方持续输出；原版找不到可射的目标时，也会射墙后、主堡顶上的敌人。 | [订阅](https://steamcommunity.com/sharedfiles/filedetails/?id=3810645122) |
| [Artful Dodger 神骑妙躲](SmartDodge/README.md) | 弓骑兵照常原地射击，敌方箭矢、弩箭或石头快要落到身上时自动躲开，躲完回到原位。 | [订阅](https://steamcommunity.com/sharedfiles/filedetails/?id=3811169708) |
| [Range Display 射程显示](RangeDisplay/README.md) | 放置塔楼时预览塔上射程；选中己方、友方或敌方的远程部队和攻城器械时画出射程圈。 | [订阅](https://steamcommunity.com/sharedfiles/filedetails/?id=3808768637) |
| [Health Bar Colors 血条颜色](HealthBarColors/README.md) | 按阵营给血条着色：己方绿、友方蓝、敌方红；领主血条更大并带皇冠标记。 | [订阅](https://steamcommunity.com/sharedfiles/filedetails/?id=3809525668) |
| [Unit Stats 单位属性](UnitStats/README.md) | 在招募和攻城器械建造菜单上方直接对比生命、伤害、射程和速度，不用逐个悬停。 | [订阅](https://steamcommunity.com/sharedfiles/filedetails/?id=3809728007) |

### 经济

| Mod | 作用 | 创意工坊 |
| --- | --- | --- |
| [Continuous Recruitment 连续招募](ContinuousRecruit/README.md) | 持续招募选定的兵种，减少重复点击。 | [订阅](https://steamcommunity.com/sharedfiles/filedetails/?id=3810027038) |
| [Auto Tax 自动调税](AutoTax/README.md) | 按食物等实际民心加成自动调整税率，让民心保持在目标区间（默认 99–99.5）附近；不会自动发补贴。 | [订阅](https://steamcommunity.com/sharedfiles/filedetails/?id=3816416634) |
| [Trade Presets 交易阈值预设](TradePresets/README.md) | 新对局沿用上次保存的市场买卖阈值；自动交易仍由你在市场里手动开启。 | [订阅](https://steamcommunity.com/sharedfiles/filedetails/?id=3810027335) |
| [Stock Delta 库存净变化](StockDelta/README.md) | 在库房、粮仓和武器库的数量下方显示上月和本月至今的净变化，看出库存在增还是在减。 | [订阅](https://steamcommunity.com/sharedfiles/filedetails/?id=3809944613) |
| [Granary Overview 粮仓总览](GranaryOverview/README.md) | 把食物开关、库存、月度变化和配给合并到同一个粮仓面板，并估算各配给等级的月度收支。 | [订阅](https://steamcommunity.com/sharedfiles/filedetails/?id=3809944812) |
| [Building Production 建筑实际产出](BuildingProduction/README.md) | 在建筑面板里显示每座生产建筑实际消耗和交付的物资。 | [订阅](https://steamcommunity.com/sharedfiles/filedetails/?id=3810026973) |

### 建造

| Mod | 作用 | 创意工坊 |
| --- | --- | --- |
| [Optimal Resource Layout 资源区智能排布](ResourceLayout/README.md) | 按快捷键为有限的资源区规划农场、采石场、铁矿和沥青坑的布局，以幽灵建筑显示，由你决定是否照着建。 | [订阅](https://steamcommunity.com/sharedfiles/filedetails/?id=3810027826) |
| [Build Grid 建筑对齐网格](BuildGrid/README.md) | 放置建筑时在鼠标周围显示网格，方便对齐建筑和预留通道。 | [订阅](https://steamcommunity.com/sharedfiles/filedetails/?id=3809944416) |

快捷键、设置项、适用范围和限制写在各 Mod 自己的 README 里。

## 安装

1. 安装 [SHCDE BepInEx Loader](https://gitlab.com/rawra-stronghold-crusader/shcde-bepinex/-/releases)。
2. 安装 [SHCDE Script Extender](https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender/-/releases) 2.10.1 或更新版本。
3. 在创意工坊订阅需要的 Mod，按提示重启游戏。

## 联机

这些 Mod 为单人游戏设计，联机均未测试。Smart Shooters 和 Artful Dodger 会改变单位行为，联机可能导致不同步；Auto Tax 和 Trade Presets 在联机中不生效。其余说明见各 Mod 的 README。

## 从源码构建

需要 .NET SDK，以及本机已安装的游戏、BepInEx Loader 和 Script Extender。项目直接引用游戏目录里的程序集，仓库不包含这些文件。

```
dotnet build RangeDisplay/RangeDisplay.csproj -c Release -p:GameDir="你的游戏目录"
```

- 编译结果在该 Mod 的 `bin/Release/` 下，不会改动游戏。
- 加上 `-p:InstallToGame=true` 会把插件复制到游戏的 `BepInEx/plugins/`；请先关闭游戏。
- 游戏装在 Steam 默认路径时可以省略 `GameDir`。

`tests/` 里是不依赖游戏的单元测试，需要 .NET 8 SDK：

```
dotnet run --project tests/AutoTax/AutoTax.Tests.csproj
```

## 反馈与支持

问题和建议可以提在对应 Mod 的创意工坊页面，或本仓库的 Issues。请附上 Mod、游戏和 Script Extender 的版本，以及复现步骤。

如果这些 Mod 对你有帮助，欢迎自愿支持：[爱发电](https://afdian.com/a/donggeliu) · [Ko-fi](https://ko-fi.com/donggeliu)。

## 许可与致谢

原创代码和文档采用 [MIT 许可证](LICENSE)。感谢 Rawra 的 Script Extender 和工坊工具，以及 BepInEx、Harmony 的贡献者；第三方软件和游戏素材的权利归各自所有者，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
