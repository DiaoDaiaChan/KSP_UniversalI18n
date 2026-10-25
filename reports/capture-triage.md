# 运行时捕获分诊报告（task-41）

> 只读分析。本报告**不写** `packs/zh-cn/`、不翻译、不出包；产物只有本文件与 `packs/capture-candidates.jsonl`（v2 跨会话合并版）。

| 项 | 值 |
|---|---|
| 输入会话 | **23 个** `capture-*.jsonl`（25.99 MB） |
| 时间跨度 | `2026-10-23T06:01:28Z` → `2026-10-25T09:46:33Z` |
| 读入行数 | 92932（坏行 0 / 坏记录 0 / h 与 n 不一致 0）
| 去重后 h | **50968** |
| 产物 | 49658 行 / 18.73 MB → `packs/capture-candidates.jsonl` |
| 复现命令 | `python -m i18n_tools.capture_ingest --all D:\Kerbal Space Program_newmod\PluginData\UI18n --report reports/capture-triage.md` |

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

单会话 distinct 上限 5,000（采集器到顶就换新文件），所以「文件数 × 5,000」≈ 上界；真正的重复率看 §2 的 `sessions>=2`。

## 2. 跨会话合并结果

按 `h` 合并：`count` 求和、`firstSeen` 最早、`lastSeen` 最晚、`sessions` 记会话数、`callers` 去重（最多 5 个、非 unknown 优先）、同 h 不同 bucket **取更保守**（never > template > candidate）。
下表是**合并口径**（去重后的全部 h）；与产物的差量见下面「口径对账」。

| bucket | 条数（h） | 其中 sessions>=2 | 占全部 | count 合计 | 进产物 | 估体积 |
|---|---:|---:|---:|---:|---|---:|
| `candidate` | 507 | 276 | 1.0% | 6167697 | 是 | 175.8 KB |
| `template` | 49171 | 10498 | 96.5% | 5132325 | 是 | 18.54 MB |
| `never` | 1290 | 1091 | 2.5% | 678110 | 否（剔除） | - |

- 口径对账：去重 **50968** 个 h = 已覆盖剔除 **23**（zh-cn 14 / pool 9）+ never 剔除 **1287** + 产物 **49658** 行（candidate 492 / template 49166）。
- **跨会话反复 miss（`sessions>=2`）共 11865 条**：candidate 276 / template 10498 / never 1091 —— 这一档优先级最高，`sessions` 字段可直接排序。
- reason 分布：path 1244 / already-cjk 24 / progress 7603 / counter 41563 / punct 1 / identifier 12 / too-long 6 / 无 492
- 声明字段采信 50473 条 h / 全靠离线自算 472 条；**离线重算判 never 而采集器声明不是 never 的有 37 条（占 0.1%）**（按保守口径一律剔除，见模块 docstring）。
- 产物排序是 **(bucket 序, h)**，不是全局 h 升序：`selftest.py:664` 对真实产物有这条既有断言，改序会把它打红；下游要别的顺序请自行排序。

## 3. 与已发布 89 个包按 `h` 去重

- 合并后 50968 个 h 中，**23 条已被已发布包覆盖**（`packs/zh-cn/*.jsonl` 14 条 / `packs/pool.jsonl` 9 条），已在 `build_rows` 里剔除、不进产物。
- 判读：这些串**包里有、运行时仍然 miss**，问题不在「没翻」而在「没匹配上」。三个已知可能：① 该包没部署/没加载（`ConfigLoader` 只读语言目录顶层 `*.jsonl`）；② 运行时 `SourceMatches` 拿到的显示文本与包里的 `s` 不一致（键引用已解析、格式串已填数字、拼接后的整句）；③ 该 mod 版本与包不同。这批是**部署/匹配诊断**素材，不需要再翻。

| h | sessions | bucket | 所在包 | 文本 |
|---|---:|---|---|---|
| `14a0b83337e7d77b` | 1 | candidate | `Waterfall.il.jsonl` | Texture Path |
| `2bd3e6e7fc10fe53` | 1 | candidate | `KSPWheel.il.jsonl` | Basic Options |
| `32d0e4a3d143f417` | 1 | candidate | `KSPTextureLoader.il.jsonl` | Search textures... |
| `355009218fdc971e` | 1 | candidate | `KSPWheel.il.jsonl` | Damage Options |
| `3797803e753044a5` | 1 | candidate | `TestFlightCore.il.jsonl` | Flight Data Engineer Multiplier |
| `74e6a7197c63c255` | 1 | candidate | `RCSBuildAid.il.jsonl` | Vessel mass |
| `96d25466c737e015` | 1 | candidate | `KSPTextureLoader.il.jsonl` | e.g., mymod_assets |
| `a1e87de9158f9755` | 1 | candidate | `KSPWheel.il.jsonl` | High Speed Limit |
| `ac7be3c8f5e71b7d` | 1 | candidate | `ParallaxContinued.il.jsonl` | Texture Exporter |
| `cb9d7d82dc6a4d27` | 1 | candidate | `Assembly-CSharp.il.jsonl` | Option A |
| `daf212946590f229` | 1 | candidate | `KSPTextureLoader.il.jsonl` | Search CPU textures... |
| `ef1dddfa832bde80` | 1 | candidate | `ClickThroughBlocker.il.jsonl` | All Input Locks Cleared |
| `f3b0405c5ce5dd79` | 1 | candidate | `KSPWheel.il.jsonl` | Dump Wheel Debug Data |
| `fba4bffc46708e43` | 1 | candidate | `Assembly-CSharp.il.jsonl` | Debug Toolbar |

## 4. caller top-30（含 mod 归属）

归属方法：拿 caller 的类型名去 `GameData/**/*.dll` 与 `D:\Kerbal Space Program_newmod\KSP_x64_Data\Managed` 的 TypeDef 反查宿主（ilmeta 只解元数据）。精确全名匹配不到时按「类型名最后一段」后缀匹配；落到多个 mod 记 `ambiguous`，不猜。共扫 223 个 dll / 32567 个类型（2 个原生 dll 跳过）。

| # | caller | 行数 | candidate | 宿主 asm | mod | 匹配方式 |
|---:|---|---:|---:|---|---|---|
| 1 | `unknown` | 7440 | 24 | - | - | unknown-caller |
| 2 | `PerformanceProbe.DrawPerformanceTab` | 5160 | 2 | ksp_plugin_adapter.dll | Principia | suffix |
| 3 | `MechJebModuleAscentMenu.ShowStatusGUIElements` | 4667 | 0 | MechJeb2.dll | MechJeb2 | suffix |
| 4 | `NavBallBurnVector.UpdateBurnUIText` | 4563 | 0 | Assembly-CSharp.dll | KSP | suffix |
| 5 | `HUDProfilerOverlay.RefreshOverlayData` | 4397 | 0 | ModularFlightPanel.dll | ModularFlightPanel | suffix |
| 6 | `SmartUIExtensions.SetTextSafe` | 4126 | 0 | ModularFlightPanel.dll | ModularFlightPanel | suffix |
| 7 | `LoadingScreen.UpdateProgressBar` | 3182 | 12 | Assembly-CSharp.dll | KSP | exact |
| 8 | `MechJebStageStatsHelper.DrawStageStatsColumn` | 2896 | 1 | MechJeb2.dll | MechJeb2 | suffix |
| 9 | `PrincipiaPluginAdapter.RenderNavballAndAccessories` | 2301 | 0 | ksp_plugin_adapter.dll | Principia | suffix |
| 10 | `HUDProfilerOverlay.RefreshOverviewTab` | 2111 | 0 | ModularFlightPanel.dll | ModularFlightPanel | suffix |
| 11 | `TMP_Text.TMPro.TMP_Text.set_text_Patch1` | 1982 | 53 | - | - | ui18n-patch |
| 12 | `ValueInfoItem.DrawItem` | 1676 | 0 | MechJeb2.dll | MechJeb2 | suffix |
| 13 | `BaseFlightWidget.SetTextIfChanged` | 1370 | 9 | ModularFlightPanel.dll | ModularFlightPanel | suffix |
| 14 | `MechJebModuleAscentPVGSettingsMenu.WindowGUI` | 1212 | 1 | MechJeb2.dll | MechJeb2 | suffix |
| 15 | `RLVDebugGUI.DrawMetricCard` | 1175 | 2 | RealisticLaunchVisuals.dll | RealisticLaunchVisuals | suffix |
| 16 | `UIPartActionLabel.UpdateItem` | 758 | 6 | Assembly-CSharp.dll | KSP | exact |
| 17 | `ProfilerUI.DrawPluginsContent` | 456 | 139 | KSPPerformanceProfiler.dll | KSPPerformanceProfiler | suffix |
| 18 | `UIPartActionWindow.CreatePartList` | 335 | 0 | Assembly-CSharp.dll | KSP | exact |
| 19 | `ProfilerUI.DrawHeaderHUD` | 245 | 1 | KSPPerformanceProfiler.dll | KSPPerformanceProfiler | suffix |
| 20 | `ProfilerUI.DrawTimeAndBarCell` | 245 | 0 | KSPPerformanceProfiler.dll | KSPPerformanceProfiler | suffix |
| 21 | `RLVDebugGUI.DrawLogs` | 120 | 0 | RealisticLaunchVisuals.dll | RealisticLaunchVisuals | suffix |
| 22 | `ConfigurationGUI.renderSubsurfaceScatteringSettings` | 98 | 49 | TUFX.dll | TUFX | suffix |
| 23 | `ProfilerUI.DrawHeroKillerCard` | 86 | 0 | KSPPerformanceProfiler.dll | KSPPerformanceProfiler | suffix |
| 24 | `ProfilerUI.DrawSpikeSnifferCard` | 77 | 0 | KSPPerformanceProfiler.dll | KSPPerformanceProfiler | suffix |
| 25 | `RLVDebugGUI.DrawTelemetry` | 64 | 0 | RealisticLaunchVisuals.dll | RealisticLaunchVisuals | suffix |
| 26 | `SettingsControlBase.SetTitleText` | 35 | 0 | Assembly-CSharp.dll | KSP | suffix |
| 27 | `ManeuverNodeEditorTabOrbitBasic.UpdateUIElements` | 34 | 0 | Assembly-CSharp.dll | KSP | exact |
| 28 | `UIFactory.CreateText` | 23 | 17 | ModularFlightPanel.dll | ModularFlightPanel | suffix |
| 29 | `DebugUIManager.CreateLabel` | 22 | 22 | - | - | ambiguous |
| 30 | `ProfilerUI.DrawTimelineGraphCard` | 21 | 0 | KSPPerformanceProfiler.dll | KSPPerformanceProfiler | suffix |

- 归属统计（按 caller 个数）：suffix 79 / exact 26 / ui18n-patch 9 / ambiguous 5 / broken-caller 5 / unknown-caller 1

## 5. 样例（40 行）

### 5.1 candidate（该翻，按 count 降序，取前 24 条）

| # | count | sessions | reason | caller | 文本 |
|---:|---:|---:|---|---|---|
| 1 | 1078836 | 13 | - | `LoadingScreen.UpdateProgressBar` | ModuleManager: Waiting for patching to finish |
| 2 | 1006960 | 1 | - | `ConfigurationGUI.renderSubsurface…` | RAPTOR (Instance) [KSP/Bumped Specular] |
| 3 | 282440 | 1 | - | `ConfigurationGUI.renderSubsurface…` | bluedog_TitanSolids_Misc (Instance) [KSP/Bumped Specular] |
| 4 | 106382 | 4 | - | `RLVDebugGUI.DrawMetricCard` | N/A |
| 5 | 80033 | 17 | - | `UIFactory.CreateText` | m/s |
| 6 | 78958 | 3 | - | `GUI.UnityEngine.GUI.DoModalWindow…` | Console |
| 7 | 78868 | 3 | - | `GUILayout.UnityEngine.GUILayout.D…` | Reload Settings |
| 8 | 61902 | 3 | - | `GUI.UnityEngine.GUI.Box_Patch1` | Database |
| 9 | 61454 | 17 | - | `MechJebStageStatsHelper.DrawStage…` | Stage |
| 10 | 61454 | 17 | - | `MechJebStageStatsHelper.AllStageS…` | dhms |
| 11 | 49228 | 17 | - | `RLVDebugGUI.DrawWindow` | English |
| 12 | 49120 | 1 | - | `ConfigurationGUI.renderSubsurface…` | ST-GEN-DSP-ISS-Mat (Instance) (Instance) [TU/Metallic] |
| 13 | 49120 | 1 | - | `ConfigurationGUI.renderSubsurface…` | restock-engine-glow-RCS (Instance) (Instance) [KSP/Particles/Additive] |
| 14 | 49076 | 17 | - | `GuiUtils.ToggledTextBox` | Burnout Altitude: |
| 15 | 49072 | 17 | - | `GuiUtils+ComboBox.Box` | ↓ Primer Vector Guidance (RSS/RO) ↓ |
| 16 | 49042 | 17 | - | `GuiUtils.ToggledTextBox` | Unguided Stages: |
| 17 | 49042 | 17 | - | `GuiUtils.SimpleTextBox` | Last Stage: |
| 18 | 49042 | 17 | - | `GuiUtils.SimpleTextBox` | Spinup lead time: |
| 19 | 49042 | 17 | - | `GuiUtils.SimpleTextBox` | Ullage lead time: |
| 20 | 49042 | 17 | - | `GuiUtils.ToggledTextBox` | Fixed Coast Length: |
| 21 | 49042 | 17 | - | `GuiUtils.SimpleTextBox` | Pa-rad |
| 22 | 49042 | 17 | - | `GuiUtils.ToggledTextBox` | Coast Stage: |
| 23 | 49042 | 17 | - | `GuiUtils.ToggledTextBox` | Early Shutoff Stage: |
| 24 | 49042 | 17 | - | `GuiUtils.SimpleTextBox` | Q Trigger: |

### 5.2 template（含数字/占位符，取前 12 条）

| # | count | sessions | reason | caller | 文本 |
|---:|---:|---:|---|---|---|
| 1 | 233320 | 1 | counter | `ConfigurationGUI.renderSubsurface…` | bluedog_Titan2_SecondStageTank (Instance) [KSP/Bumped Specular] |
| 2 | 227180 | 1 | counter | `ConfigurationGUI.renderSubsurface…` | bluedog_Centaur_RL10 (Instance) [KSP/Emissive/Bumped Specular] |
| 3 | 95170 | 1 | counter | `ConfigurationGUI.renderSubsurface…` | model_vab_exterior_props_01 (Instance) [KSP/Scenery/Specular] |
| 4 | 95170 | 1 | counter | `ConfigurationGUI.renderSubsurface…` | model_vab_exterior_lines_01_decal (Instance) [KSP/Scenery/Diffuse] |
| 5 | 82994 | 17 | counter | `ValueInfoItem.DrawItem` | 0, 0 m/s |
| 6 | 76750 | 1 | counter | `ConfigurationGUI.renderSubsurface…` | model_vab_exterior_details_01 (Instance) [KSP/Scenery/Emissive/Specular] |
| 7 | 59070 | 11 | counter | `unknown` | ModuleManager: 27195 patches applied (60 %) :BEFORE[REALISMOVERHAULENGINESPOST] |
| 8 | 58330 | 1 | counter | `ConfigurationGUI.renderSubsurface…` | model_vab_exterior_tile_00 (Instance) [KSP/Scenery/Specular] |
| 9 | 58330 | 1 | counter | `ConfigurationGUI.renderSubsurface…` | model_vab_exterior_tile_01 (Instance) [KSP/Scenery/Specular] |
| 10 | 52190 | 1 | counter | `ConfigurationGUI.renderSubsurface…` | model_vab_exterior_tile_04_roof (Instance) [KSP/Scenery/Specular] |
| 11 | 49040 | 17 | counter | `GuiUtils.ToggledTextBox` | m/s² |
| 12 | 49040 | 17 | counter | `GuiUtils.SimpleTextBox` | W/m² |

### 5.3 never（永远不该进包，展示判据，取前 4 条）

| # | count | sessions | 判据 | 文本 |
|---:|---:|---:|---|---|
| 1 | 49042 | 17 | punct | ω: |
| 2 | 34478 | 6 | path | <b><color=#79C0FF>Cinematic</color></b> |
| 3 | 34394 | 6 | path | <b><color=#79C0FF>Integrated</color></b> |
| 4 | 34394 | 6 | path | <b><color=#79C0FF>Off</color></b> |

## 6. 建议：哪些该进包、哪些不要

| 子集 | 条数 | 建议 | 预估体积 |
|---|---:|---|---:|
| `bucket=candidate` | 507 | **现在就该进包**：不带数字的行，翻了不会错位 | 175.8 KB |
| `template` + `progress` | 7603 | **永远不进**：`12/345` 这类进度，数字由游戏填，翻了只会错位 | - |
| `template` + `counter` | 41568 | **暂不进**：计数/表格串，`s` 里数字位置不固定，翻成中文要重排；要翻必须逐条人看 | 18.54 MB |
| `bucket=never` | 1290 | **永远不进**：路径 / 已含中文 / 标识符 / 纯标点 / 过长 | - |

- 全量 v2 产物 = candidate + template 共 49658 行 / 18.73 MB。**只给打包用时请加 `--buckets candidate`**，产物缩到 507 行 / 175.8 KB。
- 翻译成本参考：candidate 的 507 条按 IL 总线同口径（~1,100 条 / 81k tokens）约需 38k tokens；template 那 49171 条不建议整体送翻（多为数字串，翻了大概率被门禁退回）。

## 7. mod 归属结论

| mod（GameData 顶层目录 / KSP） | caller 数 | 行数 |
|---|---:|---:|
| `ModularFlightPanel` | 9 | 12038 |
| `MechJeb2` | 11 | 10478 |
| `(未定)` | 20 | 9480 |
| `KSP` | 45 | 9093 |
| `Principia` | 5 | 7466 |
| `RealisticLaunchVisuals` | 7 | 1373 |
| `KSPPerformanceProfiler` | 7 | 1141 |
| `TUFX` | 11 | 147 |
| `RealFuels` | 5 | 27 |
| `KSPCommunityFixes` | 1 | 8 |
| `B9PartSwitch` | 1 | 4 |
| `Kerbalism` | 1 | 2 |
| `000_ClickThroughBlocker` | 1 | 1 |
| `BurstPQS` | 1 | 1 |

- **能反查出宿主**：105 / 125 个 caller，这些行可以落到各自的 `<modId>.il.jsonl`。
- **反查不出**：20 个 caller（ambiguous / broken-caller / ui18n-patch / unknown-caller）。包括我们自己的补丁帧、采集器把程序集限定名截断的残片（形如 `32706, Culture=neutral, PublicKeyToken=null]].…`）、以及同名类型落在两个 mod 的 `ambiguous`。

**结论：反查不出的这部分（连同 `caller=unknown` 的行）统一落一个共享包 `packs/zh-cn/_captured.jsonl`，行内 `mod` 一律填 `captured`。**

- 为什么能共用一个文件：`ConfigLoader` 是按目录读 `*.jsonl` 的，`mod` 只决定 `DictEntry.Level` 是 Site（Site 级不参与过滤，不会因为 mod 名不对而查不到）；运行时是 `hash -> 条目`，同一个 h 只要有一条就命中。
- 为什么不硬猜 mod：猜错会把同一个 h 写进两个包、给出两份不同译文（`il_conflict`），比放共享包更糟；共享包天然一个 h 一份译文。
- `ambiguous` 的候选类型名在 `attribute_callers` 的 `cands` 里，落地时人工看一眼就能定。
- 口径提醒：本报告的 `mod` 是 **GameData 顶层目录名**（与 harvest.py 一致），IL 包用的是 **asm 名**（`ClickThroughBlocker.il.jsonl` vs `000_ClickThroughBlocker`）。落地时按 asm 命名建文件、行内 `mod` 才用 asm 名该有的值。

## 8. 已知问题与下一步

1. `count` 是会话内 miss 次数，不是「屏幕上出现过几次」：采集器按 h 累加，跨会话求和只用于排序。
2. **采集器缺陷**：仍有 caller 是 `32706, Culture=neutral, PublicKeyToken=null]].RenderWindowContents` 这种残片（取 caller 时对程序集限定名做了字符串截断），这些行只能进共享包。
3. **运行时分类器把富文本判成 path**：35 个 h 的 declared reason 是 `path`，但它们不是路径，而是带 `</color>` 的富文本标签 —— 运行时的路径判定把 `</` 里的斜杠当成了分隔符。样例：`<b><color=#79C0FF>Cinematic</color></b>`（34478 次）、`<b><color=#79C0FF>Integrated</color></b>`（34394 次）、`<b><color=#79C0FF>Off</color></b>`（34394 次）、`<b><color=#79C0FF>CMAA2</color></b>`（32388 次）、`<b><color=#79C0FF>NativeEdgeAA</color></b>`（32152 次）、`<b><color=#79C0FF>Temporal_FSR2</color></b>`（32152 次）。这批多是显卡/抗锯齿/画质术语（Legacy / Auto / ACES / AgX / Custom / Medium …），暂时不翻没有观感损失，但 **v1.2 应修运行时的 path 判定（先剥掉 TMP 标签再判路径）**，否则以后任何带 `<b>` / `<color>` 的标签都会被静默判 never。
4. **下一轮**（不在本任务）：candidate 的 507 条走 `translate_il` 同一套门禁翻一遍 → 按 §7 归到 `<modId>.il.jsonl` 或 `_captured.jsonl` → 重跑 `packindex` 与 `selftest`。

