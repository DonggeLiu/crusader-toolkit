# 聪明射手

Smart Shooters

集中有效火力，争取尽早消灭敌人，减少敌方持续输出。

- 默认档位（1 最先打）：1 投石车、巨弩和射石机；2 远程部队；3 工程师；4 近战部队；5 伐木工等强壮工人和猛兽；6 其他平民和牲畜；7 坑道兵等支援；8 领主；9 冲车、攻城塔和梯兵；10 防护盾。
- 同一档内先打更近的；档位更高的目标即使稍远也会优先。开启火力分配时，还会按预计击杀与威胁调整选择。
- 作用于弓箭手、弩手、投石兵、骑射手、掷火兵和贝都因远程单位；我方器械保持原版。
- 我方、盟友、敌人分别有开关，默认只对自己生效；狮子和鬣狗和原版一样，攻击时才打。
- 默认开启火力分配：计入空中箭矢及同伴准备中的射击，估算致死所需火力；规划射击在平地和塔上启用，结合命中机会选择预期击杀收益较高的一箭。迫近近战优先，同档内极难打伤的目标后排，移动目标降低命中估计。
- Mod Options 的“按致死所需分配火力”同时控制火力分配与规划射击；关闭后仍保留类别优先级。抛物线判定也可在设置页关闭。
- `[General] ShootWildAnimals` 可开启对未攻击的狮子和鬣狗射击；`[Fire]` 提供火力分配参数。
- 抛物线判定：原版只看直线有没有被城墙挡住，所以墙下的弓箭手不会主动射墙后、主堡顶上的敌人（手动下令却射得中）。现在按原版判断找不到可射的敌人时，弓箭手也会自动射这些敌人；直线看得到的敌人仍然优先。`ShootOverWalls = false` 可关闭。

默认开启。十个类别的档位可在 Mod Options 里用 −/+ 调整，也可在配置 [Priority] 部分设置（1 最先，10 最后）。启动时核对游戏代码，不符则不启用。

为单人游戏设计。联机未经测试，可能导致不同步，风险自负。

需要 Script Extender 2.10.1+ 和 BepInEx Loader，先安装 Loader、再安装扩展器。工坊订阅后按提示重启。手动安装：将 ZIP 中 BepInEx 合并到游戏目录。

[Script Extender](https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender/-/releases)

Mod Options → Smart Shooters · `BepInEx/config/TargetPriority_Dongge.cfg`

中文及英文回退；修改语言后重启游戏。反馈请附 mod、游戏、扩展器版本和复现步骤。

Dongge · MIT. Thanks to Rawra, BepInEx and Harmony contributors.

[Ko-fi](https://ko-fi.com/donggeliu) · [爱发电 / Afdian](https://afdian.com/a/donggeliu)

[Workshop / Feedback](https://steamcommunity.com/sharedfiles/filedetails/?id=3810645122)
