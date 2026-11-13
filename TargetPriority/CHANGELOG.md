# Changelog

## 1.5.0

- Planned fire: each archer picks the shot that adds the most expected kills, counting the arrows already in the air and the shots other archers are about to loose. On towers it uses the measured miss pattern (stray arrows mostly hit whoever stands in front of the target) and falls back to spreading when no one stands in front. Turn off in Mod Options.
- Fixed double-counting that sent extra arrows at enemies already about to die (archers drawing their bow were not counted; the 20% safety margin is now 0).
- Mod Options: switch for the arc-aware range check.
- 规划射击：每名弓手选“期望击杀增量”最大的一箭，空中的箭和同伴即将射出的箭都计入。塔上按实测的射偏规律（射偏的箭多落在目标正前方的人身上）计算，前面没人挡时改为分散。可在 Mod Options 关闭。
- 修正多射：正在拉弓的弓手原来没被计入，且有 20% 余量，导致向快死的敌人多射；余量改为 0。
- Mod Options：增加抛物线判定开关。

## 1.4.0

- Arc-aware range check: when the original straight-line check finds no target, your archers also shoot enemies behind walls or on a keep on their own, the way a manual attack order does. Targets in plain sight still come first; moving units keep the original behaviour. Turn off with `ShootOverWalls = false`.
- 抛物线判定：按原版直线判断找不到可射的敌人时，弓箭手也会自动射墙后、主堡顶上的敌人，效果与手动下令攻击相同；直线看得到的敌人仍然优先。可用 `ShootOverWalls = false` 关闭。

## 1.3.0

- Renamed from Target Priority to Smart Shooters (聪明射手). Settings and subscriptions are unchanged.
- Ten categories with ten separate tiers (1–10): siege engines, ranged troops, engineers, melee troops, armed workers and beasts, other civilians, tunnelers and support, lords, rams/towers/laddermen, mantlets.

## 1.2.0

- Eight categories, each with its own tier (1–6) in Mod Options: engineers are separate from rams, siege towers and laddermen (tier 5 with the lord), mantlets have their own tier, and hard-hitting workers (woodcutters, stonemasons, quarry workers, blacksmiths, hunters) have their own category.
- Separate switches for your own, allied and enemy ranged units.
- Lions and hyenas are shot only when they attack, as in the original game (config option to shoot them anyway).

## 1.1.0

- Change the shooting order in Mod Options: move categories up or down, or reset to the default order.

## 1.0.0

- First release: ranged units pick new targets by threat (ranged and siege first, then melee, civilians, the lord, and mantlets last), adjustable per category.
- Computer players keep vanilla targeting unless enabled; optional shooting of lions and hyenas.
