# zh-cn 翻译包覆盖报告

- 生成时间：`2026-10-26T10:26:35Z`
- 包目录：`packs/zh-cn/`：97 个文件 / 14,954 行 / 5,172,317 字节
  - cfg 包（`<mod>.jsonl`，GameData/**/*.cfg 抽取）：41 个 / 9,990 行 / 3,504,392 字节
  - IL 包（`<mod>.il.jsonl`，DLL 字面量经 `translate_il` 翻译）：56 个 / 4,964 行 / 1,667,925 字节
  - 合计 82 个 mod（同一 mod 的 cfg 包与 IL 包各占一行清单，按 `mod` 聚合成一行表格）
- 清单：`packs/index.json`（schema 1，字段口径见 `tools/i18n_tools/packindex.py` 模块 docstring）
- 三节口径互不相加：① 是 `packs/pool.jsonl` 口径（cfg 包覆盖多少待译串）、② 是 DLL 字面量口径（IL 包）、③ 是运行时捕获通道现状。
- 本报告由 `python -m i18n_tools.packindex` 现场计算生成，不引用任何人手填的百分比。

## ① 覆盖率（口径必须跟数字一起看）

**本安装可覆盖英文串 12,792 条，已被翻译包覆盖 9,397 条，覆盖率 73.46%。**

口径（分母 / 分子 / 为什么这么算）：

1. **分母 = `packs/pool.jsonl` 的 12,792 行。** 这是抽取阶段（`python -m i18n_tools.harvest`）已经剔除 `empty` / `already-cjk` / `pure-number` / `too-short` / `search-tags` / `url-only` / `dup` 之后的**待译英文串总量**。
   - `already-cjk`（32 条）本来就是中文、`search-tags`（1,597 条）是搜索标签不译、`dup`（4,461 条）是重复文本——它们已经在抽取阶段被排除，**因此不进分母**。把它们算进分母会把覆盖率压低，而且分母不再是"需要译的串"，失去意义。
2. **分子 = 这 12,792 行里，文本 FNV-1a64 哈希 `h` 能在 `packs/zh-cn/*.jsonl` 精确命中的 9,397 行。** 按哈希精确配对、不做模糊匹配；`h` 在 pool 内与 packs 内都唯一（pool 12,792 行 / 14,954 行各自无重复）。
3. **覆盖率 = 9,397 / 12,792 = 73.46%。**

不要用下面两个数字当覆盖率（都会失真，列出来是为了防止误引）：

- `14,954 / 12,792 = 116.90%`：分子用了 packs 总行数，但其中 5,557 行的 `h` 不在 pool 里（见下一节），分母口径是 pool，不能把池外条目算成池内覆盖。
- `8,999 / 12,792 = 70.35%`：分子剔除了 398 行 `t == s` 的直通行（它们有内容但不是译文），可当"纯译文覆盖率"参考。

按来源拆分（`pool.src`）：`loc` 2,785 / 2,948 = 94.47%；`cfg` 6,612 / 9,844 = 67.17%。

## ② DLL 字面量覆盖率（IL 包）

**DLL 里能译的字符串 1,145 条，已进包 1,099 条，覆盖率 95.98%。**

口径（分母 / 分子 / 与 ① 的关系）：

1. **分母 = `packs/il-translatable.jsonl` 里 bucket 为 `translate` 的 distinct `h`（1,145 条）。**该文件是 `python -m i18n_tools.ilstrings` 从 GameData 下所有 DLL 的 IL 里采出来的字面量，按 bucket 分流：`translate` 1,145 / `short_label` 429 / `template` 172（合计 1,746 行）。只有 `translate` 桶会送去翻译；`short_label`（按钮/短标签）与 `template`（带占位符的模板）不译。
2. **分子 = 这 1,145 条里 `h` 能在 `packs/zh-cn/*.il.jsonl` 精确命中的 1,099 条。**IL 包现状：56 个文件 / 4,964 行 / 1,667,925 字节，其中 `t == s` 直通行 1,339 行（与 ① 的 `keep` 同一语义）。
3. **与 cfg 包不重复：** IL 包的 4,955 个 `h` 与 cfg 包的 9,990 个 `h` 交集 **0** 条（`translate_il` 写盘前先按 cfg 包的 `h` 去重，同一条串只落一次）。
4. **对 ① 的覆盖率贡献 324 条：** IL 包的 `h` 与 `packs/pool.jsonl` 的交集是 324，所以 ① 的分母里没有一条是 IL 包填的。**两个分母不可相加**：IL 包覆盖的是另一批串（DLL 里的字面量），不是 pool 的子集。

还没进包的 46 条 `translate` 桶串：15 条的 `h` 已经在 cfg 包里（同一条串 cfg 包先落了，IL 包不重复落），其余 31 条既不在 cfg 包也不在 IL 包——上一轮被 `no-words` 预筛（去掉标签/占位符后没有可译词）或八门禁拦下，明细见 `reports/run-il-full7.log`。

## ③ 运行时捕获通道（游戏里真实渲染出来的串）

静态抽取（cfg / loc / DLL 字面量）只看得到"写在文件里"的串；代码里拼出来、运行时才成形的文本（`string.Format`、计数器、进度条）只能靠运行时抓：`UI18n` 在游戏里把**实际渲染出来的**文本记成 JSONL，落在 `<KSP 安装目录>/PluginData/UI18n/capture-*.jsonl`；`python -m i18n_tools.capture_ingest` 把这些文件摄入成 `packs/capture-candidates.jsonl`（按 `h` 去重，标 `bucket` / `reason` / `caller` / `mods`）。

当前候选清单：900 行（`candidate` 426 / `pattern` 474 个数字骨架），distinct `h` 900 个，来自 30 个游戏会话，涉及 136 个 caller（出现最多的 8 个：`unknown` / `TMP_Text.TMPro.TMP_Text.set_text_Patch1` / `ProfilerUI.DrawPluginsContent` / `ConfigurationGUI.renderSubsurfaceScatteringSettings` / `KSPPerformanceProfiler.ProfilerUI.DrawPluginsContent` / `RP0.Crew.TrainingGUI.RenderCourseSelector` / `UIPartActionLabel.UpdateItem` / `UIFactory.CreateText`，其余 128 个略）。
`unknown` 不是真实 caller：`callers` 列表里带 `unknown` 的 216 行，是**至少有一次没抓到调用方**的串（`CaptureCaller` 在托管栈里找不到宿主方法时记 `unknown`；`callers` 里非 `unknown` 的排前面、最多 5 个）。

- `template`（带数字的模板串）在 1.3 已**塌缩成骨架**：落盘的 `template` 行数为 0，取而代之的是 474 个 `pattern` 骨架行（`skeleton.skeleton()` 把数字换成 `#`，行上带 `p` 字段），覆盖 6,147,877 条模板实例；运行期由 `TextSkeleton` 做骨架查表 + 数字填回（`Mass Flow: 2.2 ton/s` → `Mass Flow: # ton/s`）。原因：这类串每帧数字都不同，精确匹配永远不命中，逐条翻译等于给每个可能数值都存一条。
- `candidate` 的 426 行里 408 行 `reason` 为空，是真正值得人工看的候选。
- `mods`：678 / 900 行能反查到宿主 mod（22 个 dll 名），其余 222 行记 `UI18n-Unattributed`——它们的 caller 是我们自己的补丁名或 Unity 类型，`capture_ingest.attrib_mod()` 只按 caller 的宿主程序集名反查 dll，查不出就记 `UI18n-Unattributed`。

**入包口径（1.3 起）**：`candidate` 里真 UI 文案走 `packs/overrides.jsonl` 人工通道先落地；`pattern` 的 474 个骨架走同一条翻译流水线（新门禁 `G9` 保证译文的 `#` 个数与骨架一致，否则填回会错位）。当前语言包里带 `p` 的骨架行：486 条。

与现有产物的交集（现场算的）：与 `packs/zh-cn/**` 的 `h` 交集 894 条（其中 cfg 包 0 条）；与 `packs/pool.jsonl` 交集 0 条；与 `packs/il-translatable.jsonl` 交集 11 条。取并集看：这 900 条运行时串里只有 894 条静态侧已经采到过（99.33%），其余 6 条**只有运行时才看得到**——这正是要开这条通道的理由。

注意「以屏幕覆盖率判成败」：上表的交集分母是**全部**捕获串（含进度条、日志、计数器），所以数字天然很小。真正该看的是 `reports/screen-coverage.md`（`python -m i18n_tools.screen_coverage` 生成）：分母收敛到「该翻的文本」，并同时统计精确命中与骨架命中。

②③ 两节的串都**不计入** ① 的分母，也不计入任何总覆盖率。

## 逐 mod

（表格里的数字是**裸数字**、不带千位分隔符，方便机器读；正文里的数字带分隔符。）

列口径：`包` 是该 mod 有哪些包文件（`cfg` = `<mod>.jsonl`，`IL` = `<mod>.il.jsonl`，`cfg+IL` = 两者都有，此时下面三个数字是**两个文件的和**）；`rows` / `bytes` / `keep` 来自 `packs/index.json`（`keep` = `t == s` 直通行数）；`untranslated` = 该 mod 在 pool 里有、但包没覆盖的行数；`来源` 是该 mod 在 pool 里的行按 `src` 拆开（没有包文件的 mod 也列出来，方便看出"哪些 mod 一条都没译"）。

| mod | 包 | rows | bytes | keep | untranslated | 来源 |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| RP-1 | cfg | 3382 | 1297270 | 0 | 21 | cfg 3399 + loc 4 |
| UI18n-ConfigCache | IL | 2598 | 1216178 | 905 | -324 | unknown |
| RealismOverhaul | cfg+IL | 1775 | 750297 | 35 | 275 | cfg 921 + loc 1126 |
| Squad | cfg | 930 | 360369 | 0 | 981 | cfg 1004 |
| SXT | cfg | 526 | 157331 | 0 | 396 | cfg 399 + loc 523 |
| ModularLaunchPads | cfg | 498 | 296237 | 0 | 11 | cfg 509 |
| TriggerTech | cfg | 496 | 70030 | 10 | 31 | loc 527 |
| ROCapsules | cfg | 362 | 118038 | 1 | 148 | cfg 351 + loc 159 |
| Assembly-CSharp | IL | 356 | 62262 | 32 | 0 | unknown |
| ROTanks | cfg | 320 | 46801 | 1 | 98 | cfg 418 |
| RP0 | IL | 298 | 48711 | 0 | 0 | unknown |
| ROEngines | cfg | 268 | 109891 | 3 | 35 | cfg 303 |
| UI18n-Unattributed | IL | 251 | 62915 | 6 | 0 | unknown |
| VenStockRevamp | cfg | 229 | 80146 | 1 | 173 | cfg 170 + loc 232 |
| KSPPerformanceProfiler | IL | 213 | 44321 | 178 | 0 | unknown |
| ROSolar | cfg | 181 | 35834 | 3 | 25 | cfg 206 |
| MechJeb2 | cfg+IL | 175 | 57757 | 15 | 10 | cfg 10 + loc 6 |
| TUFX | IL | 155 | 32026 | 112 | 0 | unknown |
| EditorExtensionsRedux | cfg+IL | 147 | 20503 | 4 | 18 | loc 159 |
| ProceduralParts | cfg+IL | 146 | 24750 | 1 | 0 | cfg 145 |
| Waterfall | cfg+IL | 143 | 19514 | 27 | 0 | cfg 53 |
| KerbalismConfig | cfg | 131 | 18337 | 1 | 17 | cfg 145 + loc 3 |
| ConformalDecals | cfg | 124 | 18459 | 0 | 116 | cfg 113 + loc 127 |
| RealChute | cfg+IL | 91 | 21806 | 0 | 1 | cfg 45 |
| Scatterer | IL | 73 | 10717 | 0 | 0 | unknown |
| ProceduralFairings | cfg+IL | 70 | 13208 | 0 | 6 | cfg 74 |
| RealAntennas | cfg+IL | 68 | 10819 | 1 | 0 | cfg 37 |
| ModularFlightPanel | IL | 60 | 7380 | 51 | 0 | unknown |
| FerramAerospaceResearch | IL | 58 | 8287 | 0 | 0 | unknown |
| RealFuels | cfg+IL | 55 | 7378 | 5 | 0 | cfg 6 |
| TransferWindowPlanner | IL | 50 | 9097 | 1 | 0 | unknown |
| KerbalFoundries | cfg | 49 | 15306 | 0 | 1 | cfg 50 |
| ksp_plugin_adapter | IL | 48 | 12902 | 2 | 0 | unknown |
| RCSBuildAid | IL | 45 | 5203 | 0 | 0 | unknown |
| ROHeatshields | cfg | 39 | 6747 | 1 | 0 | cfg 39 |
| RealisticLaunchVisuals | IL | 34 | 9810 | 3 | 0 | unknown |
| KerbalChangelog | cfg | 32 | 5347 | 0 | 5 | loc 37 |
| ContractConfigurator | cfg+IL | 29 | 4761 | 0 | 45 | cfg 43 + loc 5 |
| KSPTextureLoader | IL | 28 | 3202 | 1 | 0 | unknown |
| ROLib | cfg+IL | 26 | 3181 | 0 | 5 | cfg 19 |
| 000_TexturesUnlimited | cfg | 24 | 2970 | 0 | 0 | cfg 24 |
| KSPCommunityFixes | cfg+IL | 23 | 4570 | 7 | 8 | cfg 8 + loc 3 |
| Kopernicus | cfg+IL | 21 | 8085 | 0 | 1 | cfg 1 + loc 3 |
| AJE | cfg | 20 | 6914 | 0 | 10 | cfg 26 + loc 4 |
| TexturesUnlimited | IL | 20 | 2820 | 1 | 0 | unknown |
| ReStockPlus | cfg | 19 | 4839 | 0 | 249 | cfg 249 + loc 19 |
| TextureReplacer | IL | 19 | 2172 | 13 | 0 | unknown |
| ParallaxContinued | IL | 18 | 3426 | 0 | 0 | unknown |
| TestFlightCore | IL | 18 | 3532 | 0 | 0 | unknown |
| BurstPQS | cfg+IL | 16 | 3010 | 0 | 1 | loc 2 |
| ClickThroughBlocker | IL | 16 | 3487 | 3 | 0 | unknown |
| KSPWheel | IL | 15 | 1590 | 0 | 0 | unknown |
| AtmosphereAutopilot | IL | 14 | 1660 | 0 | 0 | unknown |
| LunarTransferPlanner | IL | 13 | 1940 | 1 | 0 | unknown |
| Parallax_StockTerrainTextures | cfg | 12 | 1452 | 0 | 0 | cfg 12 |
| Sol-Configs | cfg | 12 | 1311 | 0 | 521 | cfg 533 |
| Telecom | IL | 12 | 1824 | 0 | 0 | unknown |
| AdvancedPQSTools | IL | 11 | 1393 | 0 | 0 | unknown |
| Parallax_StockPlanetTextures | cfg | 11 | 1330 | 0 | 0 | cfg 11 |
| SmokeScreen | IL | 10 | 1370 | 0 | 0 | unknown |
| 001_ToolbarControl | cfg | 8 | 1216 | 0 | 1 | cfg 9 |
| ReStock | cfg | 8 | 961 | 0 | 146 | cfg 146 + loc 2 |
| Utils | IL | 7 | 696 | 2 | 0 | unknown |
| KerbalAlarmClock | IL | 5 | 609 | 0 | 0 | unknown |
| B9PartSwitch | IL | 4 | 348 | 0 | 4 | cfg 4 |
| B9_Aerospace_ProceduralWings | cfg | 4 | 633 | 0 | 6 | cfg 10 |
| KSCSwitcher | cfg+IL | 4 | 916 | 0 | 0 | cfg 2 |
| KerbalJointReinforcement | IL | 4 | 545 | 0 | 0 | unknown |
| ModuleManager | IL | 3 | 380 | 0 | 0 | unknown |
| SolverEngines | IL | 3 | 401 | 0 | 0 | unknown |
| ToolbarControl | IL | 3 | 784 | 0 | 0 | unknown |
| AtmosphereAutopilot.UI | IL | 2 | 238 | 0 | 0 | unknown |
| B9-PWings-Fork | IL | 2 | 196 | 0 | 0 | unknown |
| CommunityResourcePack | cfg | 2 | 212 | 0 | 173 | cfg 173 + loc 2 |
| EVEManager | IL | 2 | 220 | 0 | 0 | unknown |
| Firespitter | IL | 2 | 226 | 0 | 0 | unknown |
| HangerExtenderExtended | IL | 2 | 270 | 0 | 0 | unknown |
| Kerbalism | IL | 2 | 178 | 0 | 0 | unknown |
| HarmonyInstallChecker | IL | 1 | 102 | 0 | 0 | unknown |
| Principia | cfg | 1 | 112 | 0 | 4 | loc 5 |
| Renamer | IL | 1 | 133 | 0 | 0 | unknown |
| TestFlight | IL | 1 | 118 | 0 | 0 | unknown |
| SquadExpansion | - | 0 | 0 | 0 | 177 | cfg 177 |
| **合计** | **97 个包**（cfg 41 + IL 56） | **14954** | **5172317** | **1427** | **3395** | 83 个 mod |

## 池外条目（packs 有、pool 没有）

共 **5,557** 行：这些行有译文，但它们的英文原文不在当前 `packs/pool.jsonl` 里（多半来自上一轮抽取的池外来源或后续人工补录），因此**不计入覆盖率分子**——分母是 pool，池外条目不能算成池内覆盖。它们仍然会被运行时加载（按 `h` 索引，与 pool 无关）。

| mod | 池外行数 |
| --- | ---: |
| UI18n-ConfigCache | 2274 |
| Squad | 907 |
| Assembly-CSharp | 356 |
| RP0 | 298 |
| UI18n-Unattributed | 251 |
| KSPPerformanceProfiler | 213 |
| MechJeb2 | 169 |
| TUFX | 155 |
| Waterfall | 90 |
| Scatterer | 73 |
| ModularFlightPanel | 60 |
| FerramAerospaceResearch | 58 |
| TransferWindowPlanner | 50 |
| RealFuels | 49 |
| ksp_plugin_adapter | 48 |
| RealChute | 47 |
| RCSBuildAid | 45 |
| RealisticLaunchVisuals | 34 |
| RealAntennas | 31 |
| KSPTextureLoader | 28 |
| ContractConfigurator | 26 |
| KSPCommunityFixes | 20 |
| TexturesUnlimited | 20 |
| TextureReplacer | 19 |
| Kopernicus | 18 |
| ParallaxContinued | 18 |
| TestFlightCore | 18 |
| ClickThroughBlocker | 16 |
| BurstPQS | 15 |
| KSPWheel | 15 |
| AtmosphereAutopilot | 14 |
| LunarTransferPlanner | 13 |
| ROLib | 12 |
| Telecom | 12 |
| AdvancedPQSTools | 11 |
| SmokeScreen | 10 |
| Utils | 7 |
| EditorExtensionsRedux | 6 |
| ReStock | 6 |
| KerbalAlarmClock | 5 |
| B9PartSwitch | 4 |
| KerbalJointReinforcement | 4 |
| ModuleManager | 3 |
| RealismOverhaul | 3 |
| SolverEngines | 3 |
| ToolbarControl | 3 |
| AtmosphereAutopilot.UI | 2 |
| B9-PWings-Fork | 2 |
| EVEManager | 2 |
| Firespitter | 2 |
| HangerExtenderExtended | 2 |
| KSCSwitcher | 2 |
| Kerbalism | 2 |
| ProceduralFairings | 2 |
| HarmonyInstallChecker | 1 |
| ProceduralParts | 1 |
| Renamer | 1 |
| TestFlight | 1 |

## 没有包文件的 mod

共 1 个，合计 177 行 pool 候选（一条都没译，全部计入 `untranslated`）：

- SquadExpansion：177 行

## 汇总

| 指标 | 值 |
| --- | ---: |
| 包文件数 | 97（cfg 41 + IL 56） |
| mod 数 | 82（同 mod 的 cfg 包与 IL 包算一个） |
| 包总行数 | 14954 |
| 包总字节数 | 5172317 |
| `t == s` 直通行 | 1427 |
| pool 待译串 | 12792 |
| 已覆盖 pool 行 | 9397 |
| **覆盖率** | **73.46%**（9397 / 12792） |
| 池外条目 | 5557 |

