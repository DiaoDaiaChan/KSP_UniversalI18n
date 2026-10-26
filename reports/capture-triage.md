# 运行时捕获分诊报告（task-41）

> 只读分析。本报告**不写** `packs/zh-cn/`、不翻译、不出包；产物是本文件、`packs/capture-candidates.jsonl`（v3 骨架塌缩版）与 `reports/capture-patterns.md`。

| 项 | 值 |
|---|---|
| 输入会话 | **30 个** `capture-*.jsonl`（32.40 MB） |
| 时间跨度 | `2026-10-23T06:01:28Z` → `2026-10-26T06:17:03Z` |
| 读入行数 | 116466（坏行 0 / 坏记录 0 / h 与 n 不一致 0）
| 去重后 h | **60540** |
| 产物 | 900 行 / 396.7 KB → `packs/capture-candidates.jsonl` |
| 复现命令 | `python -m i18n_tools.capture_ingest --all D:\Kerbal Space Program_newmod\PluginData\UI18n --out C:\Users\43701\Documents\github\UniversalI18n\packs\capture-candidates.jsonl --report C:\Users\43701\Do…` |

## 1. 输入清单（逐会话）

| # | 文件 | 行数 | distinct | candidate 行 | 字节 | 首条 | 末条 |
|---:|---|---:|---:|---:|---:|---|---|
| 1 | `capture-20261023-060127.jsonl` | 2610 | 2610 | 78 | 753067 | 2026-10-23T06:01:28Z | 2026-10-23T06:04:33Z |
| 2 | `capture-20261023-064227.jsonl` | 4040 | 4040 | 80 | 1293558 | 2026-10-23T06:42:28Z | 2026-10-23T06:50:01Z |
| 3 | `capture-20261023-071744.jsonl` | 1986 | 1986 | 23 | 640185 | 2026-10-23T07:17:45Z | 2026-10-23T07:19:54Z |
| 4 | `capture-20261023-072805.jsonl` | 5000 | 5000 | 306 | 1336151 | 2026-10-23T07:28:08Z | 2026-10-23T07:37:26Z |
| 5 | `capture-20261023-084144.jsonl` | 5000 | 5000 | 166 | 1424882 | 2026-10-23T08:41:46Z | 2026-10-23T08:45:56Z |
| 6 | `capture-20261023-090332.jsonl` | 564 | 564 | 88 | 171718 | 2026-10-23T09:03:34Z | 2026-10-23T09:07:46Z |
| 7 | `capture-20261023-091232.jsonl` | 5000 | 5000 | 158 | 1287932 | 2026-10-23T09:12:34Z | 2026-10-23T09:18:47Z |
| 8 | `capture-20261023-102525.jsonl` | 570 | 570 | 52 | 174706 | 2026-10-23T10:25:27Z | 2026-10-23T10:27:59Z |
| 9 | `capture-20261023-102851.jsonl` | 5000 | 5000 | 165 | 1411918 | 2026-10-23T10:28:53Z | 2026-10-23T10:33:22Z |
| 10 | `capture-20261023-115509.jsonl` | 5000 | 5000 | 139 | 1444940 | 2026-10-23T11:55:11Z | 2026-10-23T12:00:46Z |
| 11 | `capture-20261023-131342.jsonl` | 5000 | 5000 | 122 | 1925644 | 2026-10-23T13:13:45Z | 2026-10-23T13:20:43Z |
| 12 | `capture-20261023-134332.jsonl` | 5000 | 5000 | 123 | 1529565 | 2026-10-23T13:43:34Z | 2026-10-23T14:00:39Z |
| 13 | `capture-20261023-155205.jsonl` | 5000 | 5000 | 149 | 1443171 | 2026-10-23T15:52:07Z | 2026-10-23T16:00:19Z |
| 14 | `capture-20261023-235337.jsonl` | 5000 | 5000 | 132 | 1321125 | 2026-10-23T23:53:39Z | 2026-10-24T00:09:15Z |
| 15 | `capture-20261024-085816.jsonl` | 5000 | 5000 | 136 | 1315849 | 2026-10-24T08:58:19Z | 2026-10-24T09:02:09Z |
| 16 | `capture-20261024-131443.jsonl` | 2674 | 2674 | 79 | 849165 | 2026-10-24T13:14:45Z | 2026-10-24T13:18:23Z |
| 17 | `capture-20261024-132525.jsonl` | 5000 | 5000 | 195 | 1446670 | 2026-10-24T13:25:27Z | 2026-10-24T13:34:46Z |
| 18 | `capture-20261024-143626.jsonl` | 5000 | 5000 | 324 | 1447958 | 2026-10-24T14:36:28Z | 2026-10-24T14:48:12Z |
| 19 | `capture-20261024-155242.jsonl` | 5000 | 5000 | 133 | 1426118 | 2026-10-24T15:52:44Z | 2026-10-24T15:57:38Z |
| 20 | `capture-20261024-163422.jsonl` | 4055 | 4055 | 169 | 1109963 | 2026-10-24T16:34:24Z | 2026-10-24T17:00:05Z |
| 21 | `capture-20261025-034603.jsonl` | 4318 | 4318 | 172 | 1299099 | 2026-10-25T03:46:05Z | 2026-10-25T04:07:45Z |
| 22 | `capture-20261025-054658.jsonl` | 3373 | 3373 | 202 | 1046549 | 2026-10-25T05:47:01Z | 2026-10-25T05:52:44Z |
| 23 | `capture-20261025-093901.jsonl` | 3742 | 3742 | 156 | 1153705 | 2026-10-25T09:39:03Z | 2026-10-25T09:46:33Z |
| 24 | `capture-20261025-123552.jsonl` | 642 | 642 | 59 | 198832 | 2026-10-25T12:35:54Z | 2026-10-25T12:38:25Z |
| 25 | `capture-20261025-163125.jsonl` | 5000 | 5000 | 129 | 1426365 | 2026-10-25T16:31:28Z | 2026-10-25T16:35:40Z |
| 26 | `capture-20261025-170425.jsonl` | 5000 | 5000 | 133 | 1301746 | 2026-10-25T17:04:27Z | 2026-10-25T17:07:41Z |
| 27 | `capture-20261025-230747.jsonl` | 2415 | 2415 | 66 | 772801 | 2026-10-25T23:07:50Z | 2026-10-25T23:11:24Z |
| 28 | `capture-20261026-003203.jsonl` | 5000 | 5000 | 127 | 1471999 | 2026-10-26T00:32:05Z | 2026-10-26T00:38:49Z |
| 29 | `capture-20261026-025138.jsonl` | 5000 | 5000 | 160 | 1407863 | 2026-10-26T02:51:40Z | 2026-10-26T03:20:45Z |
| 30 | `capture-20261026-052308.jsonl` | 477 | 477 | 270 | 138012 | 2026-10-26T05:23:10Z | 2026-10-26T06:17:03Z |

单会话 distinct 上限 5,000（采集器到顶就换新文件），所以「文件数 × 5,000」≈ 上界；真正的重复率看 §2 的 `sessions>=2`。

## 2. 跨会话合并结果

按 `h` 合并：`count` 求和、`firstSeen` 最早、`lastSeen` 最晚、`sessions` 记会话数、`callers` 去重（最多 5 个、非 unknown 优先）、同 h 不同 bucket **取更保守**（never > template > candidate）。
下表是**合并口径**（去重后的全部 h）；与产物的差量见下面「口径对账」。

| bucket | 条数（h） | 其中 sessions>=2 | 占全部 | count 合计 | 进产物 | 估体积 |
|---|---:|---:|---:|---:|---|---:|
| `candidate` | 670 | 311 | 1.1% | 7402108 | 是 | 172.0 KB |
| `pattern` | 474（骨架） | 333 | - | 6147877 | 是（骨架） | 224.0 KB |
| `template` | 58406 | 12829 | 96.5% | 6314028 | 否（塌缩成 pattern） | - |
| `never` | 1464 | 1141 | 2.4% | 732216 | 否（剔除） | - |

- 口径对账（合并侧）：去重 **60540** 个 h = 上表三档合计（candidate 670 + template 58406 + never 1464，**含已被包覆盖的 370 条**）；扣掉覆盖（candidate 262 / template 105 / never 3）后进判定的是 **60170** 条，其中 never 1461 全部剔除、待出 **58709** 条（candidate 408 原样 / template 58301 待塌缩）。
- 口径对账（产物侧）：落盘 **900** 行 = candidate 426（其中骨架判不出、从 template 降级的 18）+ pattern 474 个骨架（由 55573 条 template 实例塌缩而来）。
- **模板塌缩**：本次 **30 个会话 / 60540 条 distinct 原文**，模板档 **58301 条 → 474 个数字骨架**（覆盖 55573 条实例，另 18 条判不出骨架、降级 candidate，2710 条落在纯符号骨架上丢弃 —— 见下一条）；一个骨架的译文覆盖它全部实例（count 合计 6147877）。为什么塌：模板档每条原文的数字都不同（`Mass Flow: 2.2 ton/s`），直译不可能覆盖；把数字换成 `#` 后几万条塌成几百条，翻几百条骨架就能覆盖全部实例，运行时按骨架查表再把数字填回。
- **pattern 纯符号丢弃**：**3 个骨架 / 2710 条实例**不含任何 ASCII 字母（`−#`、`#<零宽空格>` 这类：骨架把数字换成 `#` 后只剩符号），送翻没有意义，运行时也不会拿它当原文匹配，已丢弃；丢的不是隐形，骨架数与实例数就在这里记账。
- **跨会话反复 miss（`sessions>=2`）共 14281 条**：candidate 311 / template 12829 / never 1141 —— 这一档优先级最高，`sessions` 字段可直接排序。
- reason 分布：logline 146 / path 1272 / already-cjk 24 / progress 8823 / counter 49478 / punct 1 / identifier 18 / 无 408
- **日志行（`logline`）单独一行记账：146 条**（占去重后 0.2%）：归一化后整串以 `[HH:MM:SS]` 或塌缩后的 `[#:#:#]` 开头，是 mod 自己打的 Debug 输出（`[20:00:15][Debug] [AeroState] provider=Stock, M=0.000 ...`）。判在模板塌缩**之前**，所以它们既不进产物、也不占骨架位 —— 这些串不会显示给玩家，翻它没有观感收益，还会把骨架预算花光。判据是 `_LOGLINE_RE`（只认第一个 `]` 内是时钟，`[Not a clock] Something` 这类正常串不受影响）。
- 声明字段采信 59749 条 h / 全靠离线自算 421 条；**离线重算判 never 而采集器声明不是 never 的有 183 条（占 0.3%）**（按保守口径一律剔除，见模块 docstring）。
- 产物排序是 **(bucket 序, h)**，不是全局 h 升序：`selftest.py:664` 对真实产物有这条既有断言，改序会把它打红；下游要别的顺序请自行排序。

## 3. 与已发布 89 个包按 `h` 去重

- 合并后 60540 个 h 中，**370 条已被已发布包覆盖**（`packs/zh-cn/*.jsonl` 360 条 / `packs/pool.jsonl` 10 条），已在 `build_rows` 里剔除、不进产物。
- 判读：这些串**包里有、运行时仍然 miss**，问题不在「没翻」而在「没匹配上」。三个已知可能：① 该包没部署/没加载（`ConfigLoader` 只读语言目录顶层 `*.jsonl`）；② 运行时 `SourceMatches` 拿到的显示文本与包里的 `s` 不一致（键引用已解析、格式串已填数字、拼接后的整句）；③ 该 mod 版本与包不同。这批是**部署/匹配诊断**素材，不需要再翻。

| h | sessions | bucket | 所在包 | 文本 |
|---|---:|---|---|---|
| `66dfd5b90e8f54d7` | 29 | candidate | `UI18n-Unattributed.il.jsonl` | Checking Cache |
| `084c09297eda70ab` | 28 | candidate | `KSPTextureLoader.il.jsonl` | CPU Textures |
| `2293b71fd35fee2a` | 28 | candidate | `KSPTextureLoader.il.jsonl` | Statistics |
| `2eeb52982b450285` | 28 | candidate | `KSPTextureLoader.il.jsonl` | Dump Textures Layout |
| `3b02a0d6510d4473` | 28 | candidate | `BurstPQS.il.jsonl` | Export All Planets |
| `3d2a4065165d609b` | 28 | candidate | `KSPTextureLoader.il.jsonl` | Debug Mode |
| `417b90913e05bc17` | 28 | candidate | `KSPTextureLoader.il.jsonl` | Preview |
| `463b8c4e92751ba5` | 28 | candidate | `KSPTextureLoader.il.jsonl` | Options |
| `49730f3d5751a433` | 28 | candidate | `KSPTextureLoader.il.jsonl` | Loading... |
| `6b0c9cab2acc0ef8` | 28 | candidate | `KSPTextureLoader.il.jsonl` | Load Cubemap |
| `7d440132c2dca54b` | 28 | candidate | `KSPTextureLoader.il.jsonl` | Async Upload Buffer Size (MB) |
| `844c23e861cbfbd8` | 28 | candidate | `KSPTextureLoader.il.jsonl` | Loaded Textures |
| `8e953bb418f42eea` | 28 | candidate | `BurstPQS.il.jsonl` | Export Color Map |
| `900be1cc5535d043` | 28 | candidate | `KSPTextureLoader.il.jsonl` | Dump Prefab |
| `9441612585e47a86` | 28 | candidate | `KSPTextureLoader.il.jsonl` | Loaded CPU Textures |

（另有 345 条未列出。）

## 4. caller top-30（含 mod 归属）

归属方法：拿 caller 的类型名去 `GameData/**/*.dll` 与 `D:\Kerbal Space Program_newmod\KSP_x64_Data\Managed` 的 TypeDef 反查宿主（ilmeta 只解元数据）。精确全名匹配不到时按「类型名最后一段」后缀匹配；落到多个 mod 记 `ambiguous`，不猜。共扫 223 个 dll / 32619 个类型（2 个原生 dll 跳过）。

| # | caller | 行数 | candidate | 宿主 asm | mod | 匹配方式 |
|---:|---|---:|---:|---|---|---|
| 1 | `unknown` | 216 | 1 | - | - | unknown-caller |
| 2 | `TMP_Text.TMPro.TMP_Text.set_text_Patch1` | 206 | 12 | - | - | ui18n-patch |
| 3 | `ProfilerUI.DrawPluginsContent` | 154 | 150 | KSPPerformanceProfiler.dll | KSPPerformanceProfiler | suffix |
| 4 | `ConfigurationGUI.renderSubsurfaceScatteringSettings` | 92 | 49 | TUFX.dll | TUFX | suffix |
| 5 | `KSPPerformanceProfiler.ProfilerUI.DrawPluginsContent` | 58 | 58 | KSPPerformanceProfiler.dll | KSPPerformanceProfiler | exact |
| 6 | `RP0.Crew.TrainingGUI.RenderCourseSelector` | 48 | 48 | RP0.dll | RP-1 | exact |
| 7 | `UIPartActionLabel.UpdateItem` | 37 | 0 | Assembly-CSharp.dll | KSP | exact |
| 8 | `UIFactory.CreateText` | 22 | 16 | ModularFlightPanel.dll | ModularFlightPanel | suffix |
| 9 | `PerformanceProbe.DrawPerformanceTab` | 21 | 1 | ksp_plugin_adapter.dll | Principia | suffix |
| 10 | `BaseFlightWidget.SetTextIfChanged` | 20 | 10 | ModularFlightPanel.dll | ModularFlightPanel | suffix |
| 11 | `ConfigurationGUI.renderSelectionWindow` | 20 | 18 | TUFX.dll | TUFX | suffix |
| 12 | `TUFX.ConfigurationGUI.renderSelectionWindow` | 20 | 18 | TUFX.dll | TUFX | exact |
| 13 | `LoadingScreen.UpdateProgressBar` | 17 | 12 | Assembly-CSharp.dll | KSP | exact |
| 14 | `ModularFlightPanel.UI.UIFactory.CreateText` | 16 | 16 | ModularFlightPanel.dll | ModularFlightPanel | exact |
| 15 | `ProfilerUI.DrawSpikeSnifferCard` | 14 | 0 | KSPPerformanceProfiler.dll | KSPPerformanceProfiler | suffix |
| 16 | `TextureReplacer.TRGui.ShowRoster` | 12 | 12 | TextureReplacer.dll | TextureReplacer | exact |
| 17 | `ProfilerUI.DrawTimelineGraphCard` | 10 | 1 | KSPPerformanceProfiler.dll | KSPPerformanceProfiler | suffix |
| 18 | `MechJebModuleAscentMenu.ShowStatusGUIElements` | 9 | 0 | MechJeb2.dll | MechJeb2 | suffix |
| 19 | `FerramAerospaceResearch.FARDebugAndSettings.AeroDataTab` | 8 | 8 | FerramAerospaceResearch.dll | FerramAerospaceResearch | exact |
| 20 | `ProfilerUI.DrawHeaderHUD` | 8 | 0 | KSPPerformanceProfiler.dll | KSPPerformanceProfiler | suffix |
| 21 | `ProfilerUI.DrawPartsTab` | 7 | 1 | KSPPerformanceProfiler.dll | KSPPerformanceProfiler | suffix |
| 22 | `SmartUIExtensions.SetTextSafe` | 7 | 0 | ModularFlightPanel.dll | ModularFlightPanel | suffix |
| 23 | `HUDProfilerOverlay.RefreshOverviewTab` | 6 | 0 | ModularFlightPanel.dll | ModularFlightPanel | suffix |
| 24 | `KSP.UI.Screens.Editor.PartListTooltipController.OnTooltipSpawned` | 6 | 6 | Assembly-CSharp.dll | KSP | exact |
| 25 | `KSP.UI.Screens.Editor.PartListTooltipWidget.Setup` | 6 | 6 | Assembly-CSharp.dll | KSP | exact |
| 26 | `PAWItemsOrder.UIPartActionWindow_AddGroup_1_Prefix` | 6 | 6 | KSPCommunityFixes.dll | KSPCommunityFixes | suffix |
| 27 | `RealAntennas.RealAntennasUI.WindowGUI` | 6 | 1 | RealAntennas.dll | RealAntennas | exact |
| 28 | `ProfilerUI.DrawHeroKillerCard` | 5 | 0 | KSPPerformanceProfiler.dll | KSPPerformanceProfiler | suffix |
| 29 | `RLVDebugGUI.PerfCell` | 5 | 4 | RealisticLaunchVisuals.dll | RealisticLaunchVisuals | suffix |
| 30 | `TUFX.TUFXWidgets.ParamSlider` | 5 | 5 | TUFX.dll | TUFX | exact |

- 归属统计（按 caller 个数）：exact 93 / suffix 85 / ui18n-patch 12 / broken-caller 6 / ambiguous 5 / miss 4 / unknown-caller 1

## 5. 样例（40 行）

### 5.1 candidate（该翻，按 count 降序，取前 24 条）

| # | count | sessions | reason | caller | 文本 |
|---:|---:|---:|---|---|---|
| 1 | 1526226 | 17 | - | `LoadingScreen.UpdateProgressBar` | ModuleManager: Waiting for patching to finish |
| 2 | 1006960 | 1 | - | `ConfigurationGUI.renderSubsurface…` | RAPTOR (Instance) [KSP/Bumped Specular] |
| 3 | 282440 | 1 | - | `ConfigurationGUI.renderSubsurface…` | bluedog_TitanSolids_Misc (Instance) [KSP/Bumped Specular] |
| 4 | 119996 | 5 | - | `RLVDebugGUI.DrawMetricCard` | N/A |
| 5 | 105145 | 21 | - | `ModularFlightPanel.UI.UIFactory.C…` | m/s |
| 6 | 74010 | 21 | - | `MechJebStageStatsHelper.AllStageS…` | dhms |
| 7 | 61596 | 21 | - | `GuiUtils.SimpleTextBox` | Pa-rad |
| 8 | 61596 | 21 | - | `GuiUtils.SimpleTextBox` | °/s |
| 9 | 60466 | 20 | counter | `GuiUtils.ToggledTextBox` | m/s² |
| 10 | 60466 | 20 | counter | `GuiUtils.SimpleTextBox` | W/m² |
| 11 | 57352 | 25 | - | `BaseWindowRenderer.RenderWindow` | Principia |
| 12 | 49120 | 1 | - | `ConfigurationGUI.renderSubsurface…` | ST-GEN-DSP-ISS-Mat (Instance) (Instance) [TU/Metallic] |
| 13 | 49120 | 1 | - | `ConfigurationGUI.renderSubsurface…` | restock-engine-glow-RCS (Instance) (Instance) [KSP/Particles/Additive] |
| 14 | 40340 | 15 | - | `MechJebModuleAscentPVGSettingsMen…` | (optimize) |
| 15 | 27662 | 3 | - | `KSPPerformanceProfiler.ProfilerUI…` | Assembly-CSharp |
| 16 | 26670 | 15 | - | `ClickThroughFix.ClickThruBlocker.…` | TUFX: Beyond ★ [Modern Suite] |
| 17 | 24560 | 1 | - | `ConfigurationGUI.renderSubsurface…` | ksp_pad_concrete_mat (Instance) [KSP/Scenery/Diffuse] |
| 18 | 24560 | 1 | - | `ConfigurationGUI.renderSubsurface…` | bluedog_TitanSolids_Striped (Instance) [KSP/Specular] |
| 19 | 24414 | 15 | - | `ConfigurationGUI.renderSelectionW…` | Beyond-Integrated-Cinematic |
| 20 | 24414 | 15 | - | `ConfigurationGUI.renderSelectionW…` | Cinematic-NextGen-Flight |
| 21 | 24408 | 15 | - | `ConfigurationGUI.renderSelectionW…` | Cinematic-NextGen-MainMenu |
| 22 | 24398 | 15 | - | `ConfigurationGUI.renderSelectionW…` | Default-Empty |
| 23 | 24396 | 15 | - | `ConfigurationGUI.renderSelectionW…` | Default-MainMenu |
| 24 | 24396 | 15 | - | `ConfigurationGUI.renderSelectionW…` | Default-KSC |

### 5.2 pattern（数字骨架，取 count 最大的 12 个；`样本` 是该骨架下 count 最大的一条真实原文）

| # | count | sessions | 噪声 | caller | 骨架 | 样本 |
|---:|---:|---:|---|---|---|---|
| 1 | 638638 | 20 | - | `MechJebStageStatsHelper.Dra…` | # m/s | 0 m/s |
| 2 | 338852 | 15 | - | `LunarTransferPlanner.LunarT…` | #h #m #s | 4h 29m 54.3s |
| 3 | 315909 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Text.set…` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL] | ModuleManager: 12728 patches applied (37 %) :FOR[REALISMOVE… |
| 4 | 233320 | 1 | - | `ConfigurationGUI.renderSubs…` | bluedog_Titan#_SecondStageTank (Instance) [KSP/Bumped Specular] | bluedog_Titan2_SecondStageTank (Instance) [KSP/Bumped Specu… |
| 5 | 227180 | 1 | - | `ConfigurationGUI.renderSubs…` | bluedog_Centaur_RL# (Instance) [KSP/Emissive/Bumped Specular] | bluedog_Centaur_RL10 (Instance) [KSP/Emissive/Bumped Specul… |
| 6 | 183022 | 20 | - | `ValueInfoItem.DrawItem` | #, # m/s | 0, 0 m/s |
| 7 | 169598 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Text.set…` | ModuleManager: # patches applied (# %) :FOR[XXXRP#] | ModuleManager: 50726 patches applied (92 %) :FOR[XXXRP0] |
| 8 | 131900 | 14 | - | `MechJebModuleAscentPVGSetti…` | # # m/s | 2 2,016 m/s |
| 9 | 130676 | 14 | - | `MechJebStageStatsHelper.Dra…` | #m #s | 6m 54.5s |
| 10 | 116660 | 1 | - | `ConfigurationGUI.renderSubs…` | model_vab_exterior_tile_# (Instance) [KSP/Scenery/Specular] | model_vab_exterior_tile_00 (Instance) [KSP/Scenery/Specular] |
| 11 | 113958 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Text.set…` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAULENGINES] | ModuleManager: 24258 patches applied (51 %) :FOR[REALISMOVE… |
| 12 | 103100 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Text.set…` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAULENGINES] | ModuleManager: 25090 patches applied (52 %) :AFTER[REALISMO… |

（全部 474 个骨架按 count 降序排在 `reports/capture-patterns.md`。）

### 5.3 never（永远不该进包，展示判据，取前 4 条）

| # | count | sessions | 判据 | 文本 |
|---:|---:|---:|---|---|
| 1 | 61596 | 21 | punct | ω: |
| 2 | 36206 | 7 | path | <b><color=#79C0FF>Cinematic</color></b> |
| 3 | 36122 | 7 | path | <b><color=#79C0FF>Integrated</color></b> |
| 4 | 36122 | 7 | path | <b><color=#79C0FF>Off</color></b> |

## 6. 建议：哪些该进包、哪些不要

| 子集 | 条数 | 建议 | 预估体积 |
|---|---:|---|---:|
| `bucket=candidate` | 670（产物里 426 行：原样候选 + 骨架判不出降级的 18） | **现在就该进包**：不带数字的行，翻了不会错位 | 172.0 KB |
| `bucket=pattern` | 474（骨架）<br>覆盖 55573 条实例 | **送翻的正是这一档**：可译骨架 132 个值得翻，噪声骨架 262 个翻它没用；一条骨架的译文覆盖全部实例，运行时把数字填回 | 224.0 KB |
| `template` + `progress` | 8823 | **永远不进**：`12/345` 这类进度，数字由游戏填，翻了只会错位 | - |
| `template` + `counter` | 49583 | **已塌缩进 pattern**：数字位置由骨架固定，翻骨架即可，不再逐条翻 | 计入上行 |
| `bucket=never` | 1464 | **永远不进**：路径 / 已含中文 / 标识符 / 纯标点 / 过长 / 日志行 | - |

- 全量 v3 产物 = candidate + pattern 共 900 行 / 396.7 KB（v2 的 template 行不再原样落盘）。**只给打包用时请加 `--buckets candidate`**，产物缩到 426 行 / 172.0 KB。
- 翻译成本参考：candidate 的 670 条 + 可译骨架 132 个，按 IL 总线同口径（~1,100 条 / 81k tokens）约需 59k tokens。
- 噪声骨架（ModuleManager 212 个 / Profiler HUD 43 个 / LoadingScreen 4 个 / RDR 3 个）不建议送翻：加载屏进度、ModuleManager 诊断、Profiler HUD、RDR 遥测这些即使有英文单词，翻了也没有观感收益，还白占配额。

## 7. mod 归属结论

| mod（GameData 顶层目录 / KSP） | caller 数 | 行数 |
|---|---:|---:|
| `(未定)` | 28 | 434 |
| `KSPPerformanceProfiler` | 9 | 258 |
| `TUFX` | 19 | 161 |
| `KSP` | 54 | 110 |
| `ModularFlightPanel` | 15 | 85 |
| `RP-1` | 5 | 52 |
| `MechJeb2` | 14 | 28 |
| `Principia` | 8 | 26 |
| `FerramAerospaceResearch` | 8 | 18 |
| `RealisticLaunchVisuals` | 7 | 13 |
| `TextureReplacer` | 3 | 13 |
| `RealFuels` | 5 | 9 |
| `TriggerTech` | 4 | 9 |
| `RealAntennas` | 2 | 7 |
| `KSPCommunityFixes` | 1 | 6 |
| `000_ClickThroughBlocker` | 3 | 4 |
| `EnvironmentalVisualEnhancements` | 4 | 3 |
| `Kerbalism` | 2 | 2 |
| `BurstPQS` | 6 | 1 |
| `KSPTextureLoader` | 4 | 1 |
| `LunarTransferPlanner` | 2 | 1 |
| `RCSBuildAid` | 1 | 1 |
| `TestFlight` | 1 | 0 |
| `B9PartSwitch` | 1 | 0 |

- **能反查出宿主**：178 / 206 个 caller，这些行可以落到各自的 `<modId>.il.jsonl`。
- **反查不出**：28 个 caller（ambiguous / broken-caller / miss / ui18n-patch / unknown-caller）。包括我们自己的补丁帧、采集器把程序集限定名截断的残片（形如 `32706, Culture=neutral, PublicKeyToken=null]].…`）、以及同名类型落在两个 mod 的 `ambiguous`。

**结论：反查不出的这部分（连同 `caller=unknown` 的行）统一落一个共享包 `packs/zh-cn/_captured.jsonl`，行内 `mod` 一律填 `captured`。**

- 为什么能共用一个文件：`ConfigLoader` 是按目录读 `*.jsonl` 的，`mod` 只决定 `DictEntry.Level` 是 Site（Site 级不参与过滤，不会因为 mod 名不对而查不到）；运行时是 `hash -> 条目`，同一个 h 只要有一条就命中。
- 为什么不硬猜 mod：猜错会把同一个 h 写进两个包、给出两份不同译文（`il_conflict`），比放共享包更糟；共享包天然一个 h 一份译文。
- `ambiguous` 的候选类型名在 `attribute_callers` 的 `cands` 里，落地时人工看一眼就能定。
- 口径提醒：本报告的 `mod` 是 **GameData 顶层目录名**（与 harvest.py 一致），IL 包用的是 **asm 名**（`ClickThroughBlocker.il.jsonl` vs `000_ClickThroughBlocker`）。落地时按 asm 命名建文件、行内 `mod` 才用 asm 名该有的值。

### 产物行 `mods` 的取值口径

- **dll 名优先**：`attribute_callers()` 在托管 dll 里找到宿主类型时，取 `rec['asm']`（dll 文件名）去掉扩展名 —— `MechJeb2.dll` → `MechJeb2`、`TUFX.dll` → `TUFX`、`Assembly-CSharp.dll` → `Assembly-CSharp`。这与 `packs/zh-cn/*.il.jsonl` 的建文件口径一致：出包时不会再冒出 `GuiUtils.il.jsonl` / `ConfigurationGUI.il.jsonl` 这种以类型名命名的假 mod 包（本版 23 个 mod 取值，全部是 dll 名或共享包 UI18n-Unattributed）。
- **反查不出统一 `UI18n-Unattributed`**：`caller=unknown`、我们自己的补丁帧（`*_Patch1`）、被截断的程序集限定名残片、同名类型落在两个 mod（`ambiguous`）都归这一档，出包时落一个共享包；不再拿 caller 的类型名根冒充 mod，也不再写 `unknown`。
- **`mods` 是列表**：跨 mod 合并的行会有多个值（去重排序）；出包落地时取字典序最小的那个建 `<mod>.il.jsonl`。
- 口径来源：ingest 阶段同一份反查结果（本报告不重复扫 dll）。
- 产出统计：678 行有归属 / 共 900 行；其中 782 行与 caller 类型名根不同（改归到真 mod），222 行只有 `UI18n-Unattributed`。

| `mods` 取值（产物行） | 行数 |
|---|---:|
| `UI18n-Unattributed` | 222 |
| `KSPPerformanceProfiler` | 208 |
| `TUFX` | 131 |
| `Assembly-CSharp` | 100 |
| `ModularFlightPanel` | 62 |
| `RP0` | 52 |
| `MechJeb2` | 24 |
| `ksp_plugin_adapter` | 24 |
| `FerramAerospaceResearch` | 18 |
| `TextureReplacer` | 13 |
| `RealisticLaunchVisuals` | 11 |
| `RealFuels` | 9 |
| `TransferWindowPlanner` | 9 |
| `RealAntennas` | 7 |
| `KSPCommunityFixes` | 6 |
| …（其余 8 个值） | - |

## 8. 已知问题与下一步

1. `count` 是会话内 miss 次数，不是「屏幕上出现过几次」：采集器按 h 累加，跨会话求和只用于排序。
2. **采集器缺陷**：仍有 caller 是 `32706, Culture=neutral, PublicKeyToken=null]].RenderWindowContents` 这种残片（取 caller 时对程序集限定名做了字符串截断），这些行只能进共享包。
3. **运行时分类器把富文本判成 path**：35 个 h 的 declared reason 是 `path`，但它们不是路径，而是带 `</color>` 的富文本标签 —— 运行时的路径判定把 `</` 里的斜杠当成了分隔符。样例：`<b><color=#79C0FF>Cinematic</color></b>`（36206 次）、`<b><color=#79C0FF>Integrated</color></b>`（36122 次）、`<b><color=#79C0FF>Off</color></b>`（36122 次）、`<b><color=#79C0FF>NativeEdgeAA</color></b>`（33880 次）、`<b><color=#79C0FF>Auto</color></b>`（33664 次）、`<b><color=#79C0FF>CMAA2</color></b>`（32388 次）。这批多是显卡/抗锯齿/画质术语（Legacy / Auto / ACES / AgX / Custom / Medium …），暂时不翻没有观感损失，但 **v1.2 应修运行时的 path 判定（先剥掉 TMP 标签再判路径）**，否则以后任何带 `<b>` / `<color>` 的标签都会被静默判 never。
4. **下一轮**（不在本任务）：candidate 的 670 条 **+ pattern 的可译骨架 132 个**一起走 `translate_il` 同一套门禁翻一遍 → 按 §7 归到 `<modId>.il.jsonl` 或 `_captured.jsonl` → 重跑 `packindex` 与 `selftest`。翻译侧要按 `bucket` 分流：`pattern` 行存的是骨架（`#` 占位），译文也必须保留同样数量的 `#`，运行时才能把数字填回（`skeleton.fill_back`）。
5. **下游注意**：`translate_il.py` 目前把 `bucket` 原样透传，读到 `bucket="pattern"` 的行时按「骨架 + `#` 占位」处理，不能当成普通 template。

