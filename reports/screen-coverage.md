# 屏幕覆盖率（screen coverage）

> 分母是**运行时捕获到并真的渲染过的文本**，分子是**游戏真的会加载的字典**。
> 与 `reports/coverage.md`（分母 = 抽取出来的 pool）互补：那个看「我们抽了多少」，这个看「玩家看到多少变中文了」。

| 项 | 值 |
| --- | --- |
| 生成时间 | 2026-10-26 12:31:12 |
| 会话文件 | 29 个 / 32.3 MB |
| 捕获行 | 115989（坏行 0） |
| distinct 文本 | **60379** |
| 可译分母（排除 never + 运行时标注的 path/progress/counter/identifier 等非文案类别） | **3129** |
| 字典条目 | exact 11459 / pattern 102 |
| pool 交集（运行时不加载，仅供参考） | 10 |
| 归一化契约核对（重算 h 与落盘 h 不一致） | 0 |

## 1. 覆盖率

| 覆盖方式 | 命中 distinct | 其中在可译范围 | 占全部 60379 | 占可译 3129 |
| --- | --- | --- | --- | --- |
| exact（字典里有同一原文） | 360 | 261 | 0.43% | 8.34% |
| pattern（骨架行命中） | 1165 | 4 | 0.01% | 0.13% |
| **合计** | **1525** | **265** | **0.44%** | **8.47%** |
| pool-only（运行时**不**加载） | 10 | — | — | — |
| 未覆盖 missing（可译范围内） | 2860 | 2860 | 4.74% | 91.40% |
| 不适配 / 不该翻（never + 非文案） | 57250 | — | 94.82% | — |

**可译覆盖率 = 8.47%**（分子 265 / 分母 3129；分子 = exact 261 + pattern 4，两者都只数落在可译范围内的命中）。

读法：`命中 distinct` 与 `其中在可译范围` 两列必须分开看 —— pattern 命中里绝大多数是进度条 / 计数器（运行时已标 `progress` / `counter`），翻它们没有意义，所以百分比只按可译范围内的那部分算。想知道骨架通道救回多少真文案，看 `pattern` 行的 `其中在可译范围` 那一列。

## 2. 按捕获分桶

| bucket | distinct | exact | pattern | missing |
| --- | --- | --- | --- | --- |
| template | 56574 | 99 | 1161 | 55308 |
| (旧会话未声明) | 2610 | 66 | 4 | 2540 |
| never | 676 | 0 | 0 | 676 |
| candidate | 519 | 195 | 0 | 320 |

## 3. 未覆盖最多的调用点（前 25）

这些就是「屏幕上还挂着英文」的地方，按调用次数降序。`caller` 直接对应代码里的方法名，可以据此定位 mod。

| # | 调用次数 | distinct 串 | caller | sink | 样例 |
| --- | --- | --- | --- | --- | --- |
| 1 | 1713060 | 49 | `ConfigurationGUI.renderSubsurfaceScatteringSettings` | `GUILayout.DoLabel` | `Custom/Parallax(Clone)  [Custom/Parallax]` |
| 2 | 1461538 | 704 | `LoadingScreen.UpdateProgressBar` | `TMPro.TMP_Text.set_text` | `Searching assets to load...` |
| 3 | 317406 | 18 | `ConfigurationGUI.renderSelectionWindow` | `GUILayout.DoButton` | `Beyond-Integrated-Cinematic` |
| 4 | 234463 | 2535 | `TMP_Text.TMPro.TMP_Text.set_text_Patch1` | `TMPro.TMP_Text.set_text` | `正在加载…` |
| 5 | 225447 | 1413 | `unknown` | `TMPro.TMP_Text.set_text` | `ModuleManager: 0 patches applied (0 %) :INSERT (initial)` |
| 6 | 181398 | 3 | `GuiUtils.SimpleTextBox` | `GUILayout.DoLabel` | `ω: ` |
| 7 | 116354 | 1 | `RLVDebugGUI.DrawMetricCard` | `GUILayout.DoLabel` | `N/A` |
| 8 | 103627 | 23 | `UIFactory.CreateText` | `UnityEngine.UI.Text.set_text` | `IDLE` |
| 9 | 72880 | 1 | `MechJebStageStatsHelper.AllStageStats` | `GUILayout.DoButton` | `dhms` |
| 10 | 70852 | 140 | `ProfilerUI.DrawPluginsContent` | `GUILayout.DoLabel` | `RealisticLaunchVisuals` |
| 11 | 68050 | 5 | `TUFXWidgets.ParamSlider` | `GUILayout.DoToggle` | `HueShift` |
| 12 | 54440 | 4 | `ConfigurationGUI.DrawParamToggle` | `GUILayout.DoToggle` | `HueVsHueCurve` |
| 13 | 50898 | 3 | `GUI.UnityEngine.GUI.Label_Patch1` | `GUI.Label` | `推力 中文测试 UI18n` |
| 14 | 49250 | 1 | `BaseWindowRenderer.RenderWindow` | `GUILayout.DoWindow` | `Principia` |
| 15 | 39210 | 1 | `MechJebModuleAscentPVGSettingsMenu.WindowGUI` | `GUILayout.DoLabel` | ` (optimize)` |
| 16 | 25204 | 1 | `ClickThruBlocker.GUIWindow` | `GUI.DoWindow(title)` | `TUFX: Beyond ★ [Modern Suite]` |
| 17 | 16966 | 1 | `GUI.UnityEngine.GUI.DoWindow_Patch1` | `GUI.DoWindow(title)` | `设置` |
| 18 | 14560 | 8 | `FerramAerospaceResearch.FARDebugAndSettings.AeroDataTab` | `GUILayout.DoToggle` | `Venus` |
| 19 | 13610 | 1 | `TUFXWidgets.Vector4Picker` | `GUILayout.DoToggle` | `Gain` |
| 20 | 7284 | 4 | `RLVDebugGUI.PerfCell` | `GUILayout.DoLabel` | `Telemetry` |
| 21 | 5446 | 1 | `principia.ksp_plugin_adapter.BaseWindowRenderer.RenderWindow` | `GUILayout.DoWindow` | `Principia` |
| 22 | 3840 | 12 | `TextureReplacer.TRGui.ShowRoster` | `GUILayout.DoButton` | `Jebediah Kerman` |
| 23 | 3565 | 1 | `KsmGuiText..ctor` | `TMPro.TMP_Text.set_text` | `R&D` |
| 24 | 3312 | 18 | `TUFX.ConfigurationGUI.renderSelectionWindow` | `GUILayout.DoButton` | `Beyond-Integrated-Cinematic` |
| 25 | 2472 | 6 | `FerramAerospaceResearch.FARDebugAndSettings.AeroStressTab` | `GUILayout.DoToggle` | `structural` |

## 4. 未覆盖的高频文本（前 10）

| # | 调用次数 | bucket | 文本 |
| --- | --- | --- | --- |
| 1 | 1526226 | (空) | `ModuleManager: Waiting for patching to finish` |
| 2 | 1006960 | candidate | `RAPTOR (Instance)  [KSP/Bumped Specular]` |
| 3 | 282440 | candidate | `bluedog_TitanSolids_Misc (Instance)  [KSP/Bumped Specular]` |
| 4 | 119996 | candidate | `N/A` |
| 5 | 102884 | candidate | `m/s` |
| 6 | 72880 | candidate | `dhms` |
| 7 | 60466 | candidate | `Pa-rad` |
| 8 | 60466 | candidate | `°/s` |
| 9 | 60466 | candidate | `ω: ` |
| 10 | 54696 | candidate | `Principia` |

## 5. 怎么读

- **可译覆盖率**是唯一该拿来对玩家承诺的数字；`reports/coverage.md` 的 pool 覆盖率只说明抽取端健康。
- `pattern` 列在 1.3 之前恒为 0（字典里没有 `p:1` 行），出现非零即骨架通道生效。
- `pool-only` 不算覆盖：`packs/pool.jsonl` 是生产端源产物，运行时不读（`src/UI18n.Runtime/ConfigLoader.cs:15`）。
- 归一化契约核对不为 0 说明 C# `TextNormalizer` 与 `i18n_tools/textnorm.py` 已经漂移，必须立即修。

