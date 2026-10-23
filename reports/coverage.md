# zh-cn 翻译包覆盖报告

- 生成时间：`2026-10-23T01:12:49Z`
- 包目录：`packs/zh-cn/`：41 个文件 / 9,990 行 / 3,504,392 字节
- 清单：`packs/index.json`（schema 1，字段口径见 `tools/i18n_tools/packindex.py` 模块 docstring）
- 本报告由 `python -m i18n_tools.packindex` 现场计算生成，不引用任何人手填的百分比。

## 覆盖率（口径必须跟数字一起看）

**本安装可覆盖英文串 12,792 条，已被翻译包覆盖 9,073 条，覆盖率 70.93%。**

口径（分母 / 分子 / 为什么这么算）：

1. **分母 = `packs/pool.jsonl` 的 12,792 行。** 这是抽取阶段（`python -m i18n_tools.harvest`）已经剔除 `empty` / `already-cjk` / `pure-number` / `too-short` / `search-tags` / `url-only` / `dup` 之后的**待译英文串总量**。
   - `already-cjk`（32 条）本来就是中文、`search-tags`（1,597 条）是搜索标签不译、`dup`（4,461 条）是重复文本——它们已经在抽取阶段被排除，**因此不进分母**。把它们算进分母会把覆盖率压低，而且分母不再是"需要译的串"，失去意义。
2. **分子 = 这 12,792 行里，文本 FNV-1a64 哈希 `h` 能在 `packs/zh-cn/*.jsonl` 精确命中的 9,073 行。** 按哈希精确配对、不做模糊匹配；`h` 在 pool 内与 packs 内都唯一（pool 12,792 行 / 9,990 行各自无重复）。
3. **覆盖率 = 9,073 / 12,792 = 70.93%。**

不要用下面两个数字当覆盖率（都会失真，列出来是为了防止误引）：

- `9,990 / 12,792 = 78.10%`：分子用了 packs 总行数，但其中 917 行的 `h` 不在 pool 里（见下一节），分母口径是 pool，不能把池外条目算成池内覆盖。
- `8,985 / 12,792 = 70.24%`：分子剔除了 88 行 `t == s` 的直通行（它们有内容但不是译文），可当"纯译文覆盖率"参考。

按来源拆分（`pool.src`）：`loc` 2,646 / 2,948 = 89.76%；`cfg` 6,427 / 9,844 = 65.29%。

## 逐 mod

（表格里的数字是**裸数字**、不带千位分隔符，方便机器读；正文里的数字带分隔符。）

列口径：`rows` / `bytes` / `keep` 来自 `packs/index.json`（`keep` = `t == s` 直通行数）；`untranslated` = 该 mod 在 pool 里有、但包没覆盖的行数；`来源` 是该 mod 在 pool 里的行按 `src` 拆开（没有包文件的 mod 也列出来，方便看出"哪些 mod 一条都没译"）。

| mod | rows | bytes | keep | untranslated | 来源 |
| --- | ---: | ---: | ---: | ---: | --- |
| RP-1 | 3382 | 1297270 | 0 | 21 | cfg 3399 + loc 4 |
| RealismOverhaul | 1774 | 750177 | 35 | 275 | cfg 921 + loc 1126 |
| Squad | 930 | 360369 | 0 | 981 | cfg 1004 |
| SXT | 526 | 157331 | 0 | 396 | cfg 399 + loc 523 |
| ModularLaunchPads | 498 | 296237 | 0 | 11 | cfg 509 |
| TriggerTech | 496 | 70030 | 10 | 31 | loc 527 |
| ROCapsules | 362 | 118038 | 1 | 148 | cfg 351 + loc 159 |
| ROTanks | 320 | 46801 | 1 | 98 | cfg 418 |
| ROEngines | 268 | 109891 | 3 | 35 | cfg 303 |
| VenStockRevamp | 229 | 80146 | 1 | 173 | cfg 170 + loc 232 |
| ROSolar | 181 | 35834 | 3 | 25 | cfg 206 |
| ProceduralParts | 145 | 24576 | 1 | 0 | cfg 145 |
| EditorExtensionsRedux | 141 | 19682 | 4 | 18 | loc 159 |
| KerbalismConfig | 131 | 18337 | 1 | 17 | cfg 145 + loc 3 |
| ConformalDecals | 124 | 18459 | 0 | 116 | cfg 113 + loc 127 |
| ProceduralFairings | 68 | 12789 | 0 | 6 | cfg 74 |
| Waterfall | 53 | 7953 | 27 | 0 | cfg 53 |
| KerbalFoundries | 49 | 15306 | 0 | 1 | cfg 50 |
| RealChute | 44 | 15206 | 0 | 1 | cfg 45 |
| ROHeatshields | 39 | 6747 | 1 | 0 | cfg 39 |
| RealAntennas | 37 | 6873 | 0 | 0 | cfg 37 |
| KerbalChangelog | 32 | 5347 | 0 | 5 | loc 37 |
| 000_TexturesUnlimited | 24 | 2970 | 0 | 0 | cfg 24 |
| AJE | 20 | 6914 | 0 | 10 | cfg 26 + loc 4 |
| ReStockPlus | 19 | 4839 | 0 | 249 | cfg 249 + loc 19 |
| ROLib | 14 | 1447 | 0 | 5 | cfg 19 |
| Parallax_StockTerrainTextures | 12 | 1452 | 0 | 0 | cfg 12 |
| Sol-Configs | 12 | 1311 | 0 | 521 | cfg 533 |
| Parallax_StockPlanetTextures | 11 | 1330 | 0 | 0 | cfg 11 |
| 001_ToolbarControl | 8 | 1216 | 0 | 1 | cfg 9 |
| ReStock | 8 | 961 | 0 | 146 | cfg 146 + loc 2 |
| MechJeb2 | 6 | 880 | 0 | 10 | cfg 10 + loc 6 |
| RealFuels | 6 | 1171 | 0 | 0 | cfg 6 |
| KSPCommunityFixes | 5 | 2096 | 0 | 8 | cfg 8 + loc 3 |
| B9_Aerospace_ProceduralWings | 4 | 633 | 0 | 6 | cfg 10 |
| ContractConfigurator | 3 | 399 | 0 | 45 | cfg 43 + loc 5 |
| Kopernicus | 3 | 1367 | 0 | 1 | cfg 1 + loc 3 |
| CommunityResourcePack | 2 | 212 | 0 | 173 | cfg 173 + loc 2 |
| KSCSwitcher | 2 | 630 | 0 | 0 | cfg 2 |
| BurstPQS | 1 | 1053 | 0 | 1 | loc 2 |
| Principia | 1 | 112 | 0 | 4 | loc 5 |
| B9PartSwitch | 0 | 0 | 0 | 4 | cfg 4 |
| SquadExpansion | 0 | 0 | 0 | 177 | cfg 177 |
| **合计** | **9990** | **3504392** | **88** | **3719** | 41 个包 / 43 个 mod |

## 池外条目（packs 有、pool 没有）

共 **917** 行：这些行有译文，但它们的英文原文不在当前 `packs/pool.jsonl` 里（多半来自上一轮抽取的池外来源或后续人工补录），因此**不计入覆盖率分子**——分母是 pool，池外条目不能算成池内覆盖。它们仍然会被运行时加载（按 `h` 索引，与 pool 无关）。

| mod | 池外行数 |
| --- | ---: |
| Squad | 907 |
| ReStock | 6 |
| KSPCommunityFixes | 2 |
| RealismOverhaul | 2 |

## 没有包文件的 mod

共 2 个，合计 181 行 pool 候选（一条都没译，全部计入 `untranslated`）：

- SquadExpansion：177 行
- B9PartSwitch：4 行

## 汇总

| 指标 | 值 |
| --- | ---: |
| 包文件数 | 41 |
| 包总行数 | 9990 |
| 包总字节数 | 3504392 |
| `t == s` 直通行 | 88 |
| pool 待译串 | 12792 |
| 已覆盖 pool 行 | 9073 |
| **覆盖率** | **70.93%**（9073 / 12792） |
| 池外条目 | 917 |

