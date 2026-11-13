# 单位属性（Unit Stats）

打开招募或攻城器械建造菜单，在面板上方直接对比单位生命、伤害、射程、箭/弩命中致死次数和速度，无需逐个悬停。单位名使用游戏翻译，自定义文字提供中文和英文回退。纯客户端显示，联机未测试。

## 支持菜单

兵营、雇佣兵营地、贝都因营地、工程师行会、坑道兵行会、大教堂；工程师的攻城器械建造菜单，以及塔楼器械菜单。可独立使用，不依赖其他 Dongge mod。

## 安装

先手动安装 [SHCDE Script Extender 2.10.1 或更新及 BepInEx Loader](https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender/-/releases)。订阅工坊物品后启动游戏，按提示重启。手动 ZIP：关闭游戏，将 BepInEx 文件夹合并进游戏目录。旧 `BepInEx/plugins/UnitStats` 副本请先移到 plugins 之外备份，保留配置，避免与新 GUID 目录重复加载。

## 设置

遭遇战/征程左下角 Mod Options → Unit Stats。默认开启。位置在 `BepInEx/config/UnitStats_Dongge.cfg` 的 Layout 部分调整（RightOffset、BottomOffset）。表格不拦截鼠标；英文名称和状态可换行，窄界面会缩小表格。

## 数值含义

- 生命：游戏默认最大生命 / 100。
- 近战伤害：对所采样可招募兵种的每击伤害众数；并非对所有目标相同。
- 远程伤害：默认弹种对上述目标的伤害众数 / 100。火弹、攻城石弹等未提供对应数据时显示 `-`，不代表零伤害。
- 射程（格）：默认攻击射程，直接显示数字。旧前缀“弩”“石”等是弹种标签，不是倍率或额外射程；当前每单位只查询一个默认射程，故移除前缀。
- 中箭/中弩致死：默认满血 ÷ 对该单位的箭/弩伤害，向上取整。是数据表估算，不是所有实战条件下的保证；伤害不大于零显示“免疫”。
- 速度：快、中、慢、很慢；(冲) 表示冲锋加成，固定表示塔楼器械。

伤害使用的采样目标不包括所有游戏实体，众数并列时取较大值。近战伤害与生命的单位对应仍未逐一实战验证。表格在菜单切换时重新读取，非实时战斗模拟；不涵盖所有克制、特殊效果或攻击模式。

## 反馈与支持

通过游戏内工坊按钮反馈，附 mod、游戏、扩展器版本及复现步骤。
自愿支持：[爱发电](https://afdian.com/a/donggeliu) · [Ko-fi](https://ko-fi.com/donggeliu)。
作者 Dongge，原创代码 MIT。感谢 Rawra、BepInEx 和 Harmony 贡献者，详见 LICENSE 和 THIRD_PARTY_NOTICES.md。

## 构建

`build.bat` 编译并安装。

[Workshop / Feedback](https://steamcommunity.com/sharedfiles/filedetails/?id=3809728007)
