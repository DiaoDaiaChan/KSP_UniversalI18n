# render 覆盖率（运行时自证）

本文件由 `python -m i18n_tools.render_coverage` 生成，**不要手改**。

生成时间：2026-10-26 17:31:11
数据来源：`<KSP 根>/PluginData/UI18n/render-*.json`（每份 = 一次游戏会话）
本次读到 **6** 份会话文件（另有 0 份无法解析、0 份因 stamp 重复被丢弃）。

## 结论

**本次会话期间，玩家屏幕上出现的 13,705 条不同英文文本中，有 7,258 条当场变成了中文，render 覆盖率 0.5296（52.96%）。**

- hit = 7,258（首次见到就有译文）
- miss = 6,447（首次见到没译文，进未命中捕获通道）
- distinct = hit + miss = 13,705
- ratio = hit / distinct = 7,258 / 13,705 = 0.5296

> 跨会话合并：distinct 是各会话 distinct **之和**（不是去重并集）—— 文件里只有计数没有哈希，跨会话无法去重。所以上面的 ratio 是按 total distinct 加权的平均，不是某一局的读数。

## 与另外两个覆盖率口径的关系

| 口径 | 分母 | 分子 | 值 | 来源 |
|---|---|---|---|---|
| pool 覆盖率 | 12,792 | 9,397 | 73.46% | reports\coverage.md（抽出来的候选池） |
| 屏幕覆盖率 | 捕获里可译的 distinct | 字典能接住的 | 见报告 | reports/screen-coverage.md（依赖捕获开关） |
| render 覆盖率 | 13,705 | 7,258 | 52.96% | 本报告（运行时自己数，唯一可对玩家承诺的口径） |

三个分母**不一样，不能相加也不能互相除**：

- pool 的分母是「我们抽出来的候选池」，里面含大量玩家一辈子看不到的 cfg 文本；
- 屏幕覆盖率的分母是「捕获到并判为可译的文本」，依赖捕获开关与采样；
- render 的分母是「本次会话真的渲染过、且走了 Translator 的不同原文」——它最小，也最接近体感。

## 逐会话

| stamp (UTC) | version | hit | miss | distinct | ratio | 文件 |
|---|---|---|---|---|---|---|
| 20261026-084845 | 1.0.0+093d02a232a535840ddc1e0460bf024863dcd3f6 | 1,009 | 12 | 1,021 | 0.9882 | render-20261026-084845.json |
| 20261026-085157 | 1.0.0+093d02a232a535840ddc1e0460bf024863dcd3f6 | 3,033 | 442 | 3,475 | 0.8728 | render-20261026-085157.json |
| 20261026-085826 | 1.0.0+093d02a232a535840ddc1e0460bf024863dcd3f6 | 263 | 486 | 749 | 0.3511 | render-20261026-085826.json |
| 20261026-090049 | 1.0.0+093d02a232a535840ddc1e0460bf024863dcd3f6 | 245 | 4 | 249 | 0.9839 | render-20261026-090049.json |
| 20261026-091111 | 1.0.0+093d02a232a535840ddc1e0460bf024863dcd3f6 | 539 | 3,653 | 4,192 | 0.1286 | render-20261026-091111.json |
| 20261026-091928 | 1.5.0+e72f79454592d90935b184696be90f4b86d81a58 | 2,169 | 1,850 | 4,019 | 0.5397 | render-20261026-091928.json |

## 按渲染出口（bySink）

| sink | hit | miss | distinct | ratio | 覆盖率 |
|---|---|---|---|---|---|
| TMPro.TMP_Text.set_text | 7,137 | 3,052 | 10,189 | 0.7005 | 70.05% |
| UnityEngine.UI.Text.set_text | 43 | 1,943 | 1,986 | 0.0217 | 2.17% |
| GUILayout.DoLabel | 37 | 665 | 702 | 0.0527 | 5.27% |
| GUI.Box | 0 | 331 | 331 | 0.0000 | 0.00% |
| GUILayout.DoToggle | 23 | 203 | 226 | 0.1018 | 10.18% |
| GUILayout.DoButton | 14 | 142 | 156 | 0.0897 | 8.97% |
| GUI.Label | 0 | 37 | 37 | 0.0000 | 0.00% |
| GUI.Toggle | 0 | 23 | 23 | 0.0000 | 0.00% |
| GUI.Button | 0 | 18 | 18 | 0.0000 | 0.00% |
| ScreenMessages.PostMessage | 0 | 14 | 14 | 0.0000 | 0.00% |
| GUILayout.DoWindow | 4 | 8 | 12 | 0.3333 | 33.33% |
| GUI.DoWindow(title) | 0 | 6 | 6 | 0.0000 | 0.00% |
| GUI.DoButtonGrid(GUIContent[]) | 0 | 4 | 4 | 0.0000 | 0.00% |
| GUILayout.DoRepeatButton | 0 | 1 | 1 | 0.0000 | 0.00% |

`(none)` = 补丁没带 sink 名的旧路径；`(other)` = byCaller 超出上限后的合并桶。

## 未命中 top 20（按调用点）

| # | 调用点 | miss | 占全部未命中 |
|---|---|---|---|
| 1 | `LoadingScreen.UpdateProgressBar` | 1,975 | 30.6% |
| 2 | `ModularFlightPanel.UI.HUD.HUDProfilerOverlay.RefreshOverviewTab` | 1,069 | 16.6% |
| 3 | `ModularFlightPanel.UI.HUD.HUDProfilerOverlay.RefreshOverlayData` | 478 | 7.4% |
| 4 | `RealisticLaunchVisuals.RLVDebugGUI.Row` | 381 | 5.9% |
| 5 | `LunarTransferPlanner.LunarTransferPlanner.MakeMainWindow` | 345 | 5.4% |
| 6 | `ContractConfigurator.Util.MissionControlUI.SetContractTitle` | 314 | 4.9% |
| 7 | `KERBALISM.KsmGui.KsmGuiText..ctor` | 240 | 3.7% |
| 8 | `TUFX.TUFXWidgets.ParamSlider` | 118 | 1.8% |
| 9 | `KSP.UI.UIPlanetariumDateTime.SetTime` | 108 | 1.7% |
| 10 | `ModularFlightPanel.UI.UIFactory.CreateText` | 98 | 1.5% |
| 11 | `ModularFlightPanel.UI.Workbench.WorkbenchControls.CreateWidgetThumbnailCard` | 92 | 1.4% |
| 12 | `ModularFlightPanel.UI.BaseFlightWidget.SetTextIfChanged` | 83 | 1.3% |
| 13 | `ModularFlightPanel.UI.Framework.SmartUIExtensions.SetTextSafe` | 58 | 0.9% |
| 14 | `LoadingScreen.SetTip` | 54 | 0.8% |
| 15 | `ContractConfigurator.Util.MissionControlUI.SetupGroupItem` | 40 | 0.6% |
| 16 | `TUFX.ConfigurationGUI.renderSelectionWindow` | 40 | 0.6% |
| 17 | `TUFX.TUFXHelper.DrawTooltipWindowContent` | 38 | 0.6% |
| 18 | `MuMech.GuiUtils.SimpleTextBox` | 32 | 0.5% |
| 19 | `TUFX.TUFXWidgets.Toggle` | 32 | 0.5% |
| 20 | `MuMech.MechJebStageStatsHelper.DrawStageStatsColumn` | 31 | 0.5% |

这些调用点就是下一轮最该补的字典条目来源（配合 `reports/screen-coverage.md` 的原文列表一起看）。

## 已知边界

1. 只统计走了 `Translator.Translate` 的渲染出口；直接读字典或未接入补丁的出口看不见。
2. 主菜单与加载界面也算（它们同样渲染），所以「刚进游戏还没读档」就有读数，属正常。
3. 判据是**首次**：某串第一次没翻出来、后来补上也不会改判（同一会话内字典不热更新）。
4. 上限：distinct 65,536 条、byCaller 200 个；到顶时运行时会打一行 `[UI18n] render coverage: ... 已达上限` 的警告，之后的读数仍然有效但不再增长。
5. 跨会话 distinct 是相加，不是并集（见上）。
6. 读数与「补丁装上的时刻」有关：本表里有会话在加载屏阶段就结束，加载屏的进度串（`Loading texture asset 1950/5740 (buffer=45MB)` 这类，见 `reports/loading-screen-coverage.md`）一局就能贡献几百条 distinct，所以整局 ratio 会被拉低；只统计主菜单/飞行阶段才是玩家实际体感。这也是同一版本不同会话 ratio 能从 0.35 跳到 0.99 的原因，别把某一次的 ratio 当成版本结论。
7. 未命中里有一大类是**动态文本**：每帧刷新的数字/进度/调试行（如进度条上的 `1950/5740`、HUD 性能面板的读数行、调试 GUI 的数值格）。本工具把它们照实算进 miss，但它们不是「缺译文」——静态字典按定义覆盖不了，要靠模板/模式通道（同 capture 通道的 template 桶）。所以本表 top 调用点里排在前面的一长串数字类调用点，先看是不是动态文本，再决定要不要补字典。

## 自洽检查

全部会话文件自洽：distinct == hit + miss、ratio 与 hit/miss 一致、bySink/byCaller 合计与 hit/miss 一致。

