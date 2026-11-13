# 射程显示（Range Display）

[English installation guide](README.en.md) · [更新记录](CHANGELOG.md)

选中远程部队或攻城器械时，在地面上画出它的攻击射程圈。己方、友方、敌方的显示条件相同，都是“选中才显示”，鼠标悬停不显示：己方部队用游戏自带的框选/点选；游戏本身不能选中友方、敌方单位，所以由本 mod 按游戏选己方部队的手感实现：左键点选、左键拖框框选、按住 Shift 加选，可同时选中多个单位、各画一个圈；左键点空地、右键、Esc 取消，单位死亡自动移除。命中判定用单位精灵在屏幕上的实际位置。只改变本地显示；联机未测试，不能保证同步兼容。

- 远程部队：弓箭手、弩手、阿拉伯弓箭手、投石手、弓骑、火焰投手、伏击者、游击兵、骆驼重骑
- 攻城器械：投石车、投石机、散射机、弩炮、火弩车
- 军医：点线画出搜索伤员的范围，按所在部队姿态原地防守 4 格、防御姿态 7 格、进攻姿态 10 格（不在部队里按 4 格），圈画在正好 R 格处（原生判定是连续距离，不像射程那样按整格）。站在塔上（TOWER1–5）时改为框出整座塔：同塔己方伤员不受距离限制。圈内也治不到塔上的人（高度差 ≥ 65）、隔墙的人（视线遮挡）和盟友；军医不治自己。塔上的框画在塔顶高度
- 放置塔楼时预览射程：虚线为塔上弓箭手射程（54 格）；方塔、圆塔可部署器械，另用长虚线画散射机（70 格，内）和弩炮（85 格，外）。塔顶各格都可能站人，所以画的是塔顶每格射程圈的外包络。只在放置瞭望塔、角塔、防御塔、方塔、圆塔时出现，器械圈受“显示攻城器械射程”开关控制。每个圈在屏幕可见圆弧的中点贴一个 60% 不透明的图标（弓箭手、散射机、弩炮，取自游戏界面图集 UI-MasterAtlas），标明是谁的射程，放下塔后和圈一起消失
- 圈为半透明虚线，颜色按阵营区分，与 Health Bar Colors 的血条颜色一致：己方绿色、友方蓝色、敌方红色
- 默认 F8 开关；同一格、同射程、同阵营的单位只画一个圈

## 前提

1. SHCDE 的 BepInEx 加载器：https://gitlab.com/rawra-stronghold-crusader/shcde-bepinex/-/releases
2. 脚本扩展器 **2.10.1 或更新**：https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender/-/releases
   （ModDB、N 网上的版本落后，没有射程接口。）
3. 编译需要 .NET SDK（6 或更新）。

## 编译安装

1. 修改 `build.bat` 里的 `GAME_DIR` 为你的游戏目录。
2. 关闭游戏，运行 `build.bat`。编译成功后会自动复制到 `BepInEx\plugins\RangeDisplay_Dongge\`（文件夹名与 GUID 相同，与创意工坊安装的位置一致；1.2.0 及更早装在 `plugins\RangeDisplay\` 的请删掉，否则会加载两次）。

## 创意工坊

订阅 Steam 创意工坊里的 [“[SE] Range Display”](https://steamcommunity.com/sharedfiles/filedetails/?id=3808768637) 即可安装、自动更新（仍需先手动装好 BepInEx 加载器和脚本扩展器）。打包方式见扩展器的 [Workshop Mod Creation Guide](https://rawra-stronghold-crusader.gitlab.io/shcde-script-extender/docs/guides/workshop-mod-creation-guide.html)：`workshop-package/BepInEx/plugins/RangeDisplay_Dongge/` + 根目录 `info.json`，用 `SHCDESE.WorkshopPackager` 打成 `RangeDisplay_Dongge.map` 上传。
- Mod Options 面板有中英两版（`Settings.xaml` / `Settings.zh.xaml`），按游戏语言选择。
- 扩展器主菜单菱形里的图标：`Override/Assets/GUI/Sprites/RangeDisplay_Dongge.png`（64×64，文件名 = GUID）。

## 设置

默认开启。在遭遇战/征程界面左下角的 **Mod Options → Range Display** 里可以开关射程显示和攻城器械射程；游戏内按 F8 也能切换，状态会保存。

更多设置在 `BepInEx\config\RangeDisplay_Dongge.cfg`：开关键、不透明度、线宽、虚线长度、最多绘制的圈数。装了 Health Bar Colors 时圈的颜色直接用它的 `[Colors]` 配置，否则用本 mod 的 `SelfColor` / `AllyColor` / `EnemyColor`。血条 Mod 开启盾牌色模式时，其他玩家射程圈也按盾牌色显示；关闭血条着色不影响共享配置。

1.3.1 在设置页增加“创意工坊 / 反馈”入口、致谢，以及 [Ko-fi](https://ko-fi.com/donggeliu) 和 [爱发电](https://afdian.com/a/donggeliu) 自愿打赏入口。

原创代码采用 MIT 许可证。感谢 Rawra 的脚本扩展器和工坊工具，以及 BepInEx、Harmony 的贡献者。第三方软件和游戏素材保留各自权利，详见仓库的 LICENSE 和 THIRD_PARTY_NOTICES.md。

仅构建而不安装：`dotnet build RangeDisplay.csproj -c Release -p:InstallToGame=false`。

## 射程的计算方式

射程半径直接读取扩展器的 `GetUnitAttackRange`（单位：格），与游戏原生判定一致：两格中心的整格差满足 dx² + dy² ≤ R² 即在射程内。圈画在 R + 0.5 格处，即被覆盖格子的外沿；圈沿地形高度绘制。站在塔上、城墙上的单位画面上比它所在的地面格高，圈的圆心在它正下方的塔基处——原生判定用的正是这一格的水平距离。

### 高度不影响射程

反汇编 `CrusaderDE.dll` 核对过两处原生射程判定：选择目标（RVA 0x18E760）和“目标仍在射程内”（RVA 0x19B630）。两处都只比较水平格距 dx² + dy² 与射程表值的平方，没有高度项。选目标时虽然会读取双方高度（射手高度 + 30/45/60，目标高度 + 26），但那是在水平距离判定**通过之后**才调用的弹道/遮挡检查，只能排除目标，不能把射程延长。扩展器文档也说明高度差只影响抛射初速（`GetLaunchSpeedForRange`）。所以站在塔上、城墙上与平地的射程圈一样大。

游戏说明称防御塔、方塔、圆塔“可为远程部队提供射程加成”，与代码不符。2026-10-07 实机测试（测试构建，每次一名射手、一名敌人，敌人从 62 格起每 4 秒靠近 1 格）：地面、圆塔、方塔上的弓箭手和防御塔上的弩手都在 49 格开始射击（瞭望塔 48 格），50–62 格一箭未射。塔楼没有射程加成。

开火距离 49 而不是 54，是因为有两个距离：`GetUnitAttackRange` 54 格是攻击射程（选目标、换目标、继续射击用）；`GetEngageRange` 400 世界单位 = 50 格是闲置单位自动开火的距离。没有命令时敌人要进入 50 格以内射手才会自己开火；下令攻击或已在交战时按 54 格。射程圈画的是 54 格。

### 不需要扇形

单位朝向只有 8 个方向。原生转向函数（RVA 0x197A10）每次调用朝目标转 45°，但攻击流程调用它之后不检查返回值，同一步就继续攻击；选目标和射程判定也都不看朝向。也就是说转身只是动画，不存在“不用转身才能射到”的扇形区域，整圆都是可攻击范围。
