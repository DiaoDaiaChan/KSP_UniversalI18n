# render 覆盖率（运行时自证）

本文件由 `python -m i18n_tools.render_coverage` 生成，**不要手改**。

生成时间：2026-10-26 17:09:26
数据来源：`<KSP 根>/PluginData/UI18n/render-*.json`（每份 = 一次游戏会话）
本次读到 **4** 份会话文件（另有 0 份无法解析、0 份因 stamp 重复被丢弃）。

## 结论

**本次会话期间，玩家屏幕上出现的 5,494 条不同英文文本中，有 4,550 条当场变成了中文，render 覆盖率 0.8282（82.82%）。**

- hit = 4,550（首次见到就有译文）
- miss = 944（首次见到没译文，进未命中捕获通道）
- distinct = hit + miss = 5,494
- ratio = hit / distinct = 4,550 / 5,494 = 0.8282

> 跨会话合并：distinct 是各会话 distinct **之和**（不是去重并集）—— 文件里只有计数没有哈希，跨会话无法去重。所以上面的 ratio 是按 total distinct 加权的平均，不是某一局的读数。

## 与另外两个覆盖率口径的关系

| 口径 | 分母 | 分子 | 值 | 来源 |
|---|---|---|---|---|
| pool 覆盖率 | 12,792 | 9,397 | 73.46% | reports\coverage.md（抽出来的候选池） |
| 屏幕覆盖率 | 捕获里可译的 distinct | 字典能接住的 | 见报告 | reports/screen-coverage.md（依赖捕获开关） |
| render 覆盖率 | 5,494 | 4,550 | 82.82% | 本报告（运行时自己数，唯一可对玩家承诺的口径） |

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

## 按渲染出口（bySink）

| sink | hit | miss | distinct | ratio | 覆盖率 |
|---|---|---|---|---|---|
| TMPro.TMP_Text.set_text | 4,550 | 940 | 5,490 | 0.8288 | 82.88% |
| ScreenMessages.PostMessage | 0 | 4 | 4 | 0.0000 | 0.00% |

`(none)` = 补丁没带 sink 名的旧路径；`(other)` = byCaller 超出上限后的合并桶。

## 未命中 top 12（按调用点）

| # | 调用点 | miss | 占全部未命中 |
|---|---|---|---|
| 1 | `LoadingScreen.UpdateProgressBar` | 894 | 94.7% |
| 2 | `LoadingScreen.SetTip` | 34 | 3.6% |
| 3 | `ScreenMessages.PostMessage` | 4 | 0.4% |
| 4 | `unknown` | 3 | 0.3% |
| 5 | `KSPTextureLoader.UI.DebugUIManager.CreateButton` | 2 | 0.2% |
| 6 | `BurstPQS.UI.DebugUI.DebugUIManager.CreateHelpButton` | 1 | 0.1% |
| 7 | `BurstPQS.UI.DebugUI.TextureExporterScreen.Build` | 1 | 0.1% |
| 8 | `KSP.UI.MessageSystemAppFrame.Awake` | 1 | 0.1% |
| 9 | `KSP.UI.Screens.MessageSystem.UpdateLauncherButtonCount` | 1 | 0.1% |
| 10 | `KSPTextureLoader.UI.DebugUIManager.ClonePrefab` | 1 | 0.1% |
| 11 | `KSPTextureLoader.UI.Screens.LoadTexture.LoadTextureScreenContent.BuildUI` | 1 | 0.1% |
| 12 | `VersionReadout.Start` | 1 | 0.1% |

这些调用点就是下一轮最该补的字典条目来源（配合 `reports/screen-coverage.md` 的原文列表一起看）。

## 已知边界

1. 只统计走了 `Translator.Translate` 的渲染出口；直接读字典或未接入补丁的出口看不见。
2. 主菜单与加载界面也算（它们同样渲染），所以「刚进游戏还没读档」就有读数，属正常。
3. 判据是**首次**：某串第一次没翻出来、后来补上也不会改判（同一会话内字典不热更新）。
4. 上限：distinct 65,536 条、byCaller 200 个；到顶时运行时会打一行 `[UI18n] render coverage: ... 已达上限` 的警告，之后的读数仍然有效但不再增长。
5. 跨会话 distinct 是相加，不是并集（见上）。
6. 读数与「补丁装上的时刻」有关：本表里有会话在加载屏阶段就结束，加载屏的进度串（`Loading texture asset 1950/5740 (buffer=45MB)` 这类，见 `reports/loading-screen-coverage.md`）一局就能贡献几百条 distinct，所以整局 ratio 会被拉低；只统计主菜单/飞行阶段才是玩家实际体感。这也是同一版本不同会话 ratio 能从 0.35 跳到 0.99 的原因，别把某一次的 ratio 当成版本结论。

## 自洽检查

全部会话文件自洽：distinct == hit + miss、ratio 与 hit/miss 一致、bySink/byCaller 合计与 hit/miss 一致。

