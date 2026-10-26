# zh-cn 翻译包覆盖报告

- 生成时间：`2026-10-26T01:25:26Z`
- 包目录：`packs/zh-cn/`：89 个文件 / 11,089 行 / 3,685,087 字节
  - cfg 包（`<mod>.jsonl`，GameData/**/*.cfg 抽取）：41 个 / 9,990 行 / 3,504,392 字节
  - IL 包（`<mod>.il.jsonl`，DLL 字面量经 `translate_il` 翻译）：48 个 / 1,099 行 / 180,695 字节
  - 合计 74 个 mod（同一 mod 的 cfg 包与 IL 包各占一行清单，按 `mod` 聚合成一行表格）
- 清单：`packs/index.json`（schema 1，字段口径见 `tools/i18n_tools/packindex.py` 模块 docstring）
- 三节口径互不相加：① 是 `packs/pool.jsonl` 口径（cfg 包覆盖多少待译串）、② 是 DLL 字面量口径（IL 包）、③ 是运行时捕获通道现状。
- 本报告由 `python -m i18n_tools.packindex` 现场计算生成，不引用任何人手填的百分比。

## ① 覆盖率（口径必须跟数字一起看）

**本安装可覆盖英文串 12,792 条，已被翻译包覆盖 9,073 条，覆盖率 70.93%。**

口径（分母 / 分子 / 为什么这么算）：

1. **分母 = `packs/pool.jsonl` 的 12,792 行。** 这是抽取阶段（`python -m i18n_tools.harvest`）已经剔除 `empty` / `already-cjk` / `pure-number` / `too-short` / `search-tags` / `url-only` / `dup` 之后的**待译英文串总量**。
   - `already-cjk`（32 条）本来就是中文、`search-tags`（1,597 条）是搜索标签不译、`dup`（4,461 条）是重复文本——它们已经在抽取阶段被排除，**因此不进分母**。把它们算进分母会把覆盖率压低，而且分母不再是"需要译的串"，失去意义。
2. **分子 = 这 12,792 行里，文本 FNV-1a64 哈希 `h` 能在 `packs/zh-cn/*.jsonl` 精确命中的 9,073 行。** 按哈希精确配对、不做模糊匹配；`h` 在 pool 内与 packs 内都唯一（pool 12,792 行 / 11,089 行各自无重复）。
3. **覆盖率 = 9,073 / 12,792 = 70.93%。**

不要用下面两个数字当覆盖率（都会失真，列出来是为了防止误引）：

- `11,089 / 12,792 = 86.69%`：分子用了 packs 总行数，但其中 2,016 行的 `h` 不在 pool 里（见下一节），分母口径是 pool，不能把池外条目算成池内覆盖。
- `8,985 / 12,792 = 70.24%`：分子剔除了 88 行 `t == s` 的直通行（它们有内容但不是译文），可当"纯译文覆盖率"参考。

按来源拆分（`pool.src`）：`loc` 2,646 / 2,948 = 89.76%；`cfg` 6,427 / 9,844 = 65.29%。

## ② DLL 字面量覆盖率（IL 包）

**DLL 里能译的字符串 1,145 条，已进包 1,099 条，覆盖率 95.98%。**

口径（分母 / 分子 / 与 ① 的关系）：

1. **分母 = `packs/il-translatable.jsonl` 里 bucket 为 `translate` 的 distinct `h`（1,145 条）。**该文件是 `python -m i18n_tools.ilstrings` 从 GameData 下所有 DLL 的 IL 里采出来的字面量，按 bucket 分流：`translate` 1,145 / `short_label` 429 / `template` 172（合计 1,746 行）。只有 `translate` 桶会送去翻译；`short_label`（按钮/短标签）与 `template`（带占位符的模板）不译。
2. **分子 = 这 1,145 条里 `h` 能在 `packs/zh-cn/*.il.jsonl` 精确命中的 1,099 条。**IL 包现状：48 个文件 / 1,099 行 / 180,695 字节，其中 `t == s` 直通行 4 行（与 ① 的 `keep` 同一语义）。
3. **与 cfg 包不重复：** IL 包的 1,099 个 `h` 与 cfg 包的 9,990 个 `h` 交集 **0** 条（`translate_il` 写盘前先按 cfg 包的 `h` 去重，同一条串只落一次）。
4. **对 ① 的覆盖率贡献 0 条：** IL 包的 `h` 与 `packs/pool.jsonl` 的交集是 0，所以 ① 的分母里没有一条是 IL 包填的。**两个分母不可相加**：IL 包覆盖的是另一批串（DLL 里的字面量），不是 pool 的子集。

还没进包的 46 条 `translate` 桶串：15 条的 `h` 已经在 cfg 包里（同一条串 cfg 包先落了，IL 包不重复落），其余 31 条既不在 cfg 包也不在 IL 包——上一轮被 `no-words` 预筛（去掉标签/占位符后没有可译词）或八门禁拦下，明细见 `reports/run-il-full7.log`。

## ③ 运行时捕获通道（游戏里真实渲染出来的串）

静态抽取（cfg / loc / DLL 字面量）只看得到"写在文件里"的串；代码里拼出来、运行时才成形的文本（`string.Format`、计数器、进度条）只能靠运行时抓：`UI18n` 在游戏里把**实际渲染出来的**文本记成 JSONL，落在 `<KSP 安装目录>/PluginData/UI18n/capture-*.jsonl`；`python -m i18n_tools.capture_ingest` 把这些文件摄入成 `packs/capture-candidates.jsonl`（按 `h` 去重，标 `bucket` / `reason` / `caller` / `mods`）。

当前候选清单：49,658 行（`candidate` 492 / `template` 49,166），distinct `h` 49,658 个，来自 23 个游戏会话，涉及 125 个 caller（出现最多的 8 个：`unknown` / `PerformanceProbe.DrawPerformanceTab` / `MechJebModuleAscentMenu.ShowStatusGUIElements` / `NavBallBurnVector.UpdateBurnUIText` / `HUDProfilerOverlay.RefreshOverlayData` / `SmartUIExtensions.SetTextSafe` / `LoadingScreen.UpdateProgressBar` / `MechJebStageStatsHelper.DrawStageStatsColumn`，其余 117 个略）。
`unknown` 不是真实 caller：`callers` 列表里带 `unknown` 的 7,440 行，是**至少有一次没抓到调用方**的串（`CaptureCaller` 在托管栈里找不到宿主方法时记 `unknown`；`callers` 里非 `unknown` 的排前面、最多 5 个）。

- `template` 的 49,166 行按 `reason` 拆：`counter` 41,563 / `progress` 7,603——带计数的进度/计数器串，每次渲染都可能不同，译了也匹配不上。
- `candidate` 的 492 行 `reason` 全为空，是真正值得人工看的候选。
- `mods`：41,757 / 49,658 行能反查到宿主 mod（62 个），其余 7,901 行仍是 `unknown`——它们的 caller 是我们自己的补丁名或 Unity 类型，`capture_ingest.infer_mods()` 不从 sink / Unity 类型猜 mod，推不出就记 `unknown`。

**这一轮不进包**：`template` 是模板串（见上），不译；`candidate` 的 492 条里 463 条已经有了宿主 mod 归属、剩下 29 条还是 `unknown`，本轮只做分诊、不动包。

其中 2 条是人造探针串（`Zzz control alpha` / `Zzz control beta`，走 `GUI.Label`），用来验证捕获通道能抓到 IMGUI 路径——它们不是游戏文本，**永远不该进包**。

与现有产物的交集（现场算的）：与 `packs/zh-cn/**` 的 `h` 交集 0 条（其中 cfg 包 0 条）；与 `packs/pool.jsonl` 交集 0 条；与 `packs/il-translatable.jsonl` 交集 22 条。取并集看：这 49,658 条运行时串里只有 22 条静态侧已经采到过（0.04%），其余 49,636 条**只有运行时才看得到**——这正是要开这条通道的理由。

②③ 两节的串都**不计入** ① 的分母，也不计入任何总覆盖率。

## 逐 mod

（表格里的数字是**裸数字**、不带千位分隔符，方便机器读；正文里的数字带分隔符。）

列口径：`包` 是该 mod 有哪些包文件（`cfg` = `<mod>.jsonl`，`IL` = `<mod>.il.jsonl`，`cfg+IL` = 两者都有，此时下面三个数字是**两个文件的和**）；`rows` / `bytes` / `keep` 来自 `packs/index.json`（`keep` = `t == s` 直通行数）；`untranslated` = 该 mod 在 pool 里有、但包没覆盖的行数；`来源` 是该 mod 在 pool 里的行按 `src` 拆开（没有包文件的 mod 也列出来，方便看出"哪些 mod 一条都没译"）。

| mod | 包 | rows | bytes | keep | untranslated | 来源 |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| RP-1 | cfg | 3382 | 1297270 | 0 | 21 | cfg 3399 + loc 4 |
| RealismOverhaul | cfg+IL | 1775 | 750297 | 35 | 275 | cfg 921 + loc 1126 |
| Squad | cfg | 930 | 360369 | 0 | 981 | cfg 1004 |
| SXT | cfg | 526 | 157331 | 0 | 396 | cfg 399 + loc 523 |
| ModularLaunchPads | cfg | 498 | 296237 | 0 | 11 | cfg 509 |
| TriggerTech | cfg | 496 | 70030 | 10 | 31 | loc 527 |
| ROCapsules | cfg | 362 | 118038 | 1 | 148 | cfg 351 + loc 159 |
| ROTanks | cfg | 320 | 46801 | 1 | 98 | cfg 418 |
| ROEngines | cfg | 268 | 109891 | 3 | 35 | cfg 303 |
| RP0 | IL | 244 | 41669 | 0 | 0 | unknown |
| VenStockRevamp | cfg | 229 | 80146 | 1 | 173 | cfg 170 + loc 232 |
| ROSolar | cfg | 181 | 35834 | 3 | 25 | cfg 206 |
| Assembly-CSharp | IL | 180 | 36939 | 1 | 0 | unknown |
| EditorExtensionsRedux | cfg+IL | 147 | 20503 | 4 | 18 | loc 159 |
| ProceduralParts | cfg+IL | 146 | 24750 | 1 | 0 | cfg 145 |
| Waterfall | cfg+IL | 142 | 19427 | 27 | 0 | cfg 53 |
| KerbalismConfig | cfg | 131 | 18337 | 1 | 17 | cfg 145 + loc 3 |
| ConformalDecals | cfg | 124 | 18459 | 0 | 116 | cfg 113 + loc 127 |
| RealChute | cfg+IL | 91 | 21806 | 0 | 1 | cfg 45 |
| Scatterer | IL | 73 | 10717 | 0 | 0 | unknown |
| ProceduralFairings | cfg+IL | 70 | 13208 | 0 | 6 | cfg 74 |
| RealAntennas | cfg+IL | 55 | 9244 | 0 | 0 | cfg 37 |
| KerbalFoundries | cfg | 49 | 15306 | 0 | 1 | cfg 50 |
| RCSBuildAid | IL | 42 | 4943 | 0 | 0 | unknown |
| TransferWindowPlanner | IL | 41 | 6065 | 0 | 0 | unknown |
| ROHeatshields | cfg | 39 | 6747 | 1 | 0 | cfg 39 |
| MechJeb2 | cfg+IL | 37 | 4294 | 0 | 10 | cfg 10 + loc 6 |
| RealFuels | cfg+IL | 34 | 4778 | 0 | 0 | cfg 6 |
| KerbalChangelog | cfg | 32 | 5347 | 0 | 5 | loc 37 |
| ContractConfigurator | cfg+IL | 28 | 4672 | 0 | 45 | cfg 43 + loc 5 |
| ROLib | cfg+IL | 26 | 3181 | 0 | 5 | cfg 19 |
| 000_TexturesUnlimited | cfg | 24 | 2970 | 0 | 0 | cfg 24 |
| FerramAerospaceResearch | IL | 22 | 3816 | 0 | 0 | unknown |
| Kopernicus | cfg+IL | 21 | 8085 | 0 | 1 | cfg 1 + loc 3 |
| AJE | cfg | 20 | 6914 | 0 | 10 | cfg 26 + loc 4 |
| TexturesUnlimited | IL | 20 | 2820 | 1 | 0 | unknown |
| ReStockPlus | cfg | 19 | 4839 | 0 | 249 | cfg 249 + loc 19 |
| ksp_plugin_adapter | IL | 19 | 3374 | 0 | 0 | unknown |
| ParallaxContinued | IL | 18 | 3426 | 0 | 0 | unknown |
| TestFlightCore | IL | 18 | 3532 | 0 | 0 | unknown |
| KSPCommunityFixes | cfg+IL | 15 | 3623 | 1 | 8 | cfg 8 + loc 3 |
| KSPWheel | IL | 15 | 1590 | 0 | 0 | unknown |
| AtmosphereAutopilot | IL | 14 | 1660 | 0 | 0 | unknown |
| ClickThroughBlocker | IL | 13 | 3074 | 1 | 0 | unknown |
| Parallax_StockTerrainTextures | cfg | 12 | 1452 | 0 | 0 | cfg 12 |
| Sol-Configs | cfg | 12 | 1311 | 0 | 521 | cfg 533 |
| Telecom | IL | 12 | 1824 | 0 | 0 | unknown |
| AdvancedPQSTools | IL | 11 | 1393 | 0 | 0 | unknown |
| Parallax_StockPlanetTextures | cfg | 11 | 1330 | 0 | 0 | cfg 11 |
| LunarTransferPlanner | IL | 10 | 1628 | 0 | 0 | unknown |
| SmokeScreen | IL | 10 | 1370 | 0 | 0 | unknown |
| TUFX | IL | 9 | 2134 | 0 | 0 | unknown |
| 001_ToolbarControl | cfg | 8 | 1216 | 0 | 1 | cfg 9 |
| ReStock | cfg | 8 | 961 | 0 | 146 | cfg 146 + loc 2 |
| KerbalAlarmClock | IL | 5 | 609 | 0 | 0 | unknown |
| TextureReplacer | IL | 5 | 668 | 0 | 0 | unknown |
| B9_Aerospace_ProceduralWings | cfg | 4 | 633 | 0 | 6 | cfg 10 |
| KSCSwitcher | cfg+IL | 4 | 916 | 0 | 0 | cfg 2 |
| KerbalJointReinforcement | IL | 4 | 545 | 0 | 0 | unknown |
| KSPTextureLoader | IL | 3 | 385 | 0 | 0 | unknown |
| ModuleManager | IL | 3 | 380 | 0 | 0 | unknown |
| SolverEngines | IL | 3 | 401 | 0 | 0 | unknown |
| ToolbarControl | IL | 3 | 784 | 0 | 0 | unknown |
| AtmosphereAutopilot.UI | IL | 2 | 238 | 0 | 0 | unknown |
| BurstPQS | cfg+IL | 2 | 1174 | 0 | 1 | loc 2 |
| CommunityResourcePack | cfg | 2 | 212 | 0 | 173 | cfg 173 + loc 2 |
| Firespitter | IL | 2 | 226 | 0 | 0 | unknown |
| HangerExtenderExtended | IL | 2 | 270 | 0 | 0 | unknown |
| B9-PWings-Fork | IL | 1 | 105 | 0 | 0 | unknown |
| HarmonyInstallChecker | IL | 1 | 102 | 0 | 0 | unknown |
| Principia | cfg | 1 | 112 | 0 | 4 | loc 5 |
| Renamer | IL | 1 | 133 | 0 | 0 | unknown |
| TestFlight | IL | 1 | 118 | 0 | 0 | unknown |
| Utils | IL | 1 | 98 | 0 | 0 | unknown |
| B9PartSwitch | - | 0 | 0 | 0 | 4 | cfg 4 |
| SquadExpansion | - | 0 | 0 | 0 | 177 | cfg 177 |
| **合计** | **89 个包**（cfg 41 + IL 48） | **11089** | **3685087** | **92** | **3719** | 76 个 mod |

## 池外条目（packs 有、pool 没有）

共 **2,016** 行：这些行有译文，但它们的英文原文不在当前 `packs/pool.jsonl` 里（多半来自上一轮抽取的池外来源或后续人工补录），因此**不计入覆盖率分子**——分母是 pool，池外条目不能算成池内覆盖。它们仍然会被运行时加载（按 `h` 索引，与 pool 无关）。

| mod | 池外行数 |
| --- | ---: |
| Squad | 907 |
| RP0 | 244 |
| Assembly-CSharp | 180 |
| Waterfall | 89 |
| Scatterer | 73 |
| RealChute | 47 |
| RCSBuildAid | 42 |
| TransferWindowPlanner | 41 |
| MechJeb2 | 31 |
| RealFuels | 28 |
| ContractConfigurator | 25 |
| FerramAerospaceResearch | 22 |
| TexturesUnlimited | 20 |
| ksp_plugin_adapter | 19 |
| Kopernicus | 18 |
| ParallaxContinued | 18 |
| RealAntennas | 18 |
| TestFlightCore | 18 |
| KSPWheel | 15 |
| AtmosphereAutopilot | 14 |
| ClickThroughBlocker | 13 |
| KSPCommunityFixes | 12 |
| ROLib | 12 |
| Telecom | 12 |
| AdvancedPQSTools | 11 |
| LunarTransferPlanner | 10 |
| SmokeScreen | 10 |
| TUFX | 9 |
| EditorExtensionsRedux | 6 |
| ReStock | 6 |
| KerbalAlarmClock | 5 |
| TextureReplacer | 5 |
| KerbalJointReinforcement | 4 |
| KSPTextureLoader | 3 |
| ModuleManager | 3 |
| RealismOverhaul | 3 |
| SolverEngines | 3 |
| ToolbarControl | 3 |
| AtmosphereAutopilot.UI | 2 |
| Firespitter | 2 |
| HangerExtenderExtended | 2 |
| KSCSwitcher | 2 |
| ProceduralFairings | 2 |
| B9-PWings-Fork | 1 |
| BurstPQS | 1 |
| HarmonyInstallChecker | 1 |
| ProceduralParts | 1 |
| Renamer | 1 |
| TestFlight | 1 |
| Utils | 1 |

## 没有包文件的 mod

共 2 个，合计 181 行 pool 候选（一条都没译，全部计入 `untranslated`）：

- SquadExpansion：177 行
- B9PartSwitch：4 行

## 汇总

| 指标 | 值 |
| --- | ---: |
| 包文件数 | 89（cfg 41 + IL 48） |
| mod 数 | 74（同 mod 的 cfg 包与 IL 包算一个） |
| 包总行数 | 11089 |
| 包总字节数 | 3685087 |
| `t == s` 直通行 | 92 |
| pool 待译串 | 12792 |
| 已覆盖 pool 行 | 9073 |
| **覆盖率** | **70.93%**（9073 / 12792） |
| 池外条目 | 2016 |

