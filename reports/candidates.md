# 候选单一表（packs/candidates.jsonl）

> 由 tools/i18n_tools/candidates.py 生成；本文件不要手改，重跑即覆盖。

## 1. 一句话

全表 **77,265** 行（五条通道按 h 去重后，每条 h 恰好一行）。扣掉纯 key 引用 3,187 行与占位符/纯符号 2,719 行，**可翻行数（本报告唯一分母）= 71,359**；其中已覆盖 **13,522** 条（**18.95%**），仍未覆盖 **57,837** 条（渲染次数之和 6,797,824）。

这张表**不是**送翻清单：送翻仍读 packs/capture-candidates.jsonl（只含 candidate/pattern）；本表给「还剩多少英文」一个统一分母，并让 never 档留档可查。

- 产物：`C:\Users\43701\Documents\github\UniversalI18n\packs\candidates.jsonl`
- 报告：`C:\Users\43701\Documents\github\UniversalI18n\reports\candidates.md`
- 生成命令：`python -m i18n_tools.candidates`

## 2. 五通道各自的输入与去重键

| 通道 | 读入行数 | 自己的 distinct h | 去重键 | 进表行数 | 被更高优先级通道抢走 |
| --- | ---: | ---: | --- | ---: | ---: |
| cfg | 9,844 | 9,844 | h = fnv1a64(normalize(text)) | 9,649 | 195 |
| loc | 2,948 | 2,948 | h = fnv1a64(normalize(text)) | 2,751 | 197 |
| il | 1,746 | 1,746 | h（il-translatable.jsonl 的 h，基于归一化原文） | 1,696 | 50 |
| cfgcache | 2,605 | 2,605 | h（cfgcache-candidates.jsonl 的 h） | 2,588 | 17 |
| capture | 117,395 | 60,581 | h（采集器算的，离线按 normalize(s) 复核，不一致 0 例） | 60,581 | 0 |

去重键统一是 h = fnv1a64(normalize(原文))（tools/i18n_tools/fnv.py + textnorm.py），静态通道的 h 由抽取侧写好、本表复核，capture 通道的 h 是采集器算好后离线再算一遍对比。

去重规则：同一个 h 撞多通道时按 capture > il > cfgcache > loc > cfg 取优先级最高的**一整行**，count 不合并 —— 跨通道相加没有意义（一个是运行时渲染次数，一个是文件里出现次数）。

## 3. 分母构成（下文所有百分比都按它算）

| 类别 | 行数 | 已覆盖 | 说明 |
| --- | ---: | ---: | --- |
| 纯 key 引用 | 3,187 | 22 | `#` 开头且不含空白（`#LOC_xxx` / `#LOC.SXT_xxx` / `#Sol_Jupiter_name//Jupiter`）：运行时由 Localizer 解析成别的文本，按 key 建字典命不中；已降为 bucket=never、reason=keyref |
| 占位符/纯符号 | 2,719 | 0 | 去掉数字与标点后既没有 ASCII 字母也没有汉字（`###`、`[%s]`、`**` 之类）：没有可翻的文字 |
| **可翻行数（分母）** | **71,359** | **13,522** | 其余全部；第 1、4、5、6、7 节的百分比与「还剩多少」都按这一行算 |

- 可翻行数 = 表行数 77,265 - keyref 3,187 - 占位符 2,719 = **71,359**
- 可翻里已覆盖 **13,522** 条（18.95%），未覆盖 **57,837** 条

| bucket | 行数 | 其中可翻 | 可翻里已覆盖 | 可翻里未覆盖 |
| --- | ---: | ---: | ---: | ---: |
| candidate | 9,210 | 9,210 | 8,722 | 488 |
| template | 62,789 | 60,072 | 4,253 | 55,819 |
| never | 5,266 | 2,077 | 569 | 1,530 |
| **合计** | **77,265** | **71,359** | **13,522** | **57,837** |

## 4. channel × bucket

| channel | bucket | 行数 | 已覆盖 | 未覆盖 | 未覆盖的 count 之和 |
| --- | --- | ---: | ---: | ---: | ---: |
| cfg | candidate | 2,783 | 2,725 | 58 | 58 |
| cfg | template | 3,215 | 3,153 | 62 | 62 |
| cfg | never | 3,651 | 540 | 3,111 | 3,111 |
| loc | candidate | 1,634 | 1,585 | 49 | 49 |
| loc | template | 1,077 | 977 | 100 | 100 |
| loc | never | 40 | 26 | 14 | 14 |
| il | candidate | 1,530 | 1,154 | 376 | 376 |
| il | template | 166 | 0 | 166 | 166 |
| cfgcache | candidate | 2,588 | 2,588 | 0 | 0 |
| capture | candidate | 675 | 670 | 5 | 4,171 |
| capture | template | 58,331 | 123 | 58,208 | 6,114,103 |
| capture | never | 1,575 | 3 | 1,572 | 783,186 |
| **合计** | | **77,265** | **13,544** | **63,721** | **6,905,396** |

本表是**行数盘点**（含 keyref / 占位符行，所以合计与第 3 节的分母不同）；覆盖率请只看第 5 节。

## 5. covered（按可翻分母算的覆盖率）

| channel | 行数 | 可翻 | 可翻里已覆盖 | 可翻里未覆盖 | 覆盖率 |
| --- | ---: | ---: | ---: | ---: | ---: |
| cfg | 9,649 | 6,539 | 6,396 | 143 | 97.81% |
| loc | 2,751 | 2,744 | 2,588 | 156 | 94.31% |
| il | 1,696 | 1,696 | 1,154 | 542 | 68.04% |
| cfgcache | 2,588 | 2,588 | 2,588 | 0 | 100.00% |
| capture | 60,581 | 57,792 | 796 | 56,996 | 1.38% |
| **合计** | **77,265** | **71,359** | **13,522** | **57,837** | **18.95%** |

## 6. 仍未覆盖的 top 20 caller

静态通道（cfg/loc/il/cfgcache）没有 caller —— 它们没经过渲染出口、记不出调用栈 ——按 `mod:<modId>` 归堆；capture 通道的是真实 Type.Method。

| caller | 通道 | 行数 | count 之和 |
| --- | --- | ---: | ---: |
| unknown | capture | 7,298 | 1,760,909 |
| ConfigurationGUI.renderSubsurfaceScatteringSettings | capture | 49 | 1,191,160 |
| MechJebStageStatsHelper.DrawStageStatsColumn | capture | 3,148 | 832,720 |
| MechJebModuleAscentMenu.ShowStatusGUIElements | capture | 6,231 | 500,620 |
| PerformanceProbe.DrawPerformanceTab | capture | 5,157 | 352,518 |
| TUFXWidgets.EnumSelector | capture | 19 | 325,060 |
| LunarTransferPlanner.LunarTransferPlanner.MakeMainWindow | capture | 2,507 | 287,288 |
| ConfigurationGUI.AddEnumField | capture | 13 | 206,254 |
| ValueInfoItem.DrawItem | capture | 2,349 | 183,022 |
| TMP_Text.TMPro.TMP_Text.set_text_Patch1 | capture | 1,231 | 136,954 |
| MechJebModuleAscentPVGSettingsMenu.WindowGUI | capture | 1,411 | 131,900 |
| HUDProfilerOverlay.RefreshOverviewTab | capture | 2,274 | 115,794 |
| RLVDebugGUI.DrawTelemetry | capture | 64 | 91,608 |
| LoadingScreen.UpdateProgressBar | capture | 5,406 | 78,962 |
| PrincipiaPluginAdapter.RenderNavballAndAccessories | capture | 2,710 | 62,720 |
| MainWindow.RenderWindowContents | capture | 2 | 53,020 |
| ProfilerUI.DrawPluginsContent | capture | 308 | 51,967 |
| GUI.UnityEngine.GUI.Label_Patch1 | capture | 3 | 50,898 |
| ConfigurationGUI.renderSelectionWindow | capture | 2 | 43,978 |
| HUDProfilerOverlay.RefreshOverlayData | capture | 4,625 | 38,896 |

## 7. 可翻行里仍未覆盖多少

- 可翻行数 **71,359**，已覆盖 13,522，未覆盖 **57,837** 条（这条是第 1 节的分母链，与第 3 节完全一致）
- 未覆盖行的 count 之和：**6,797,824**（运行时渲染次数；静态通道每条记 1）
- bucket=candidate：可翻 9,210 条，其中未覆盖 488 条（共 9,210 条，含 0 行不计入分母）
- bucket=template：可翻 60,072 条，其中未覆盖 55,819 条（共 62,789 条，含 2,717 行不计入分母）
- bucket=never：可翻 2,077 条，其中未覆盖 1,530 条（共 5,266 条，含 3,189 行不计入分母）

template 档那 55,819 条未覆盖行按骨架塌缩（tools/i18n_tools/skeleton.py，和出包用同一个函数）：**729 个骨架**，覆盖 55,810 条；另 9 条塌不出骨架（数字段 0 个或超过 8 个），出包时会被降级成 candidate 原样送翻。

所以「还剩多少英文」不能按 57,837 条算：真正可翻的 distinct 单位是 candidate 未覆盖 488 条 + template 骨架 729 个 + 塌不出骨架的 9 条 = **1,226** 个单位；never 1,530 条留档不送翻。

## 8. candidate 档未覆盖的 488 条（下一批翻译的活）

这 488 条是**已经判成该翻、但 packs/zh-cn 里还没有对应译文**的行（bucket=candidate、未覆盖、且计入第 3 节的分母）；未覆盖的 count 之和 4,654。template 档的 55,819 条要先塌成骨架（第 7 节）再派，never 留档不送翻 —— 派活只派这一节。

| channel | 未覆盖行数 | count 之和 |
| --- | ---: | ---: |
| cfg | 58 | 58 |
| loc | 49 | 49 |
| il | 376 | 376 |
| capture | 5 | 4,171 |
| **合计** | **488** | **4,654** |

按 mod 分布（前 20 个，按未覆盖行数降序；整表 45 个 mod 有未覆盖 candidate 行）：

| mod | 未覆盖行数 | count 之和 | 来源 channel |
| --- | ---: | ---: | --- |
| Assembly-CSharp.dll | 68 | 68 | il |
| RP0.dll | 66 | 66 | il |
| Sol-Configs | 54 | 54 | cfg |
| Waterfall.dll | 35 | 35 | il |
| TriggerTech | 25 | 25 | loc |
| ContractConfigurator.dll | 23 | 23 | il |
| MechJeb2.dll | 23 | 23 | il |
| RealFuels.dll | 20 | 20 | il |
| Scatterer.dll | 18 | 18 | il |
| RCSBuildAid.dll | 17 | 17 | il |
| AdvancedPQSTools.dll | 16 | 16 | il |
| KSPWheel.dll | 16 | 16 | il |
| RealAntennas.dll | 16 | 16 | il |
| EditorExtensionsRedux | 14 | 14 | loc |
| AtmosphereAutopilot.dll | 13 | 13 | il |
| RealChute.dll | 6 | 6 | il |
| RealismOverhaul | 6 | 6 | cfg/loc |
| TexturesUnlimited.dll | 6 | 6 | il |
| TextureReplacer.dll | 4 | 4 | il |
| TransferWindowPlanner.dll | 4 | 4 | il |
| （其余 25 个 mod 略） | | | |

同一行只记一个 mod（多归属取 mods[0]，完整归属见 reports/capture-triage.md）；这张表和第 9 节的榜是同一个候选池，只是这里按 mod 切、那里按 channel×bucket 切。

## 9. 最该先翻的 3 个 channel×bucket

1. **capture × template**：可翻里未覆盖 55,498 条 / count 之和 6,109,551
2. **capture × candidate**：可翻里未覆盖 5 条 / count 之和 4,171
3. **il × candidate**：可翻里未覆盖 376 条 / count 之和 376

排序依据是「未覆盖行的 count 之和」而不是行数：count 大的串在游戏里真的被反复渲染，翻一条顶一片；静态通道 count 恒为 1，所以同分时按行数看。never 档不进这张榜（它留档不送翻），只在第 3、4、7 节露面。

## 10. 口径与已知问题

- **只有一个分母**：可翻行数 = 行数 - keyref - 占位符/纯符号（见第 3 节）；别拿全表行数去算覆盖率。
- **keyref 的口径与 harvest_cfgcache 同源**：`harvest_cfgcache._KEYREF_RE`（`^#[A-Za-z0-9_]+$`）覆盖下划线式；cfg 里真实还存在点分/连字符/斜杠式（`#LOC.SXT_xxx`、`#LOC_Restock_variant-xxx`、`#Sol_Jupiter_name//Jupiter`），这几种由本模块的宽松式 `^#[^\s]+$` 兜住，判据都是「# 开头且不含空白」。
- **bucket 是优先级不是去留**：never（日志行/路径/中文/纯符号/标识符/keyref…）也留档，只是排在最后；送翻清单仍然只取 candidate/pattern。
- **pattern 不在本表**：捕获端的骨架行是 template 的塌缩视图，本表留 template 真身，骨架清单见 reports/capture-patterns.md。
- **静态通道没有频次**：pool / il 的 count 记 1，别把它当渲染次数用。
- **pool 行只有归一化文本**：packs/pool.jsonl 的 text 已归一化（实测 12,792/12,792 满足 h == fnv1a64(normalize(text))），所以 pool 的 s 与 src 往往相同。
- **capture 通道的 mods 来自 dll 反查**：反查不出统一记 UI18n-Unattributed；本表只取 mods[0]，多归属的行在 reports/capture-triage.md 里有完整列表。
- **truncated 不在本表**：被 MaxTextLength 截断的 h 在捕获产物行尾带 truncated:true，本表 schema 严格按设计契约 D-T3，不掺这个字段。
- **covered 是生成时刻的快照**：packs/zh-cn 会随翻译继续增长，本表只有重跑才更新；想知道现在的数就再跑一次，别引用上次报告里的百分比。
- **这不是渲染覆盖率**：本表分母是「机器看得见的串」，真正的渲染覆盖率由运行时写 PluginData/UI18n/render-<stamp>.json 与日志 [UI18n] render coverage 一行给出（D-T4）。
