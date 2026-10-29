# 原生语言节点覆盖率（mmgen）

生成物：`build/native/zh-cn/*.cfg`，不入库，随整包发布到 Packs/<lang>/Native/。

数据：`packs/pool.jsonl` 的 `src=loc` 行，与 `packs/zh-cn/*.jsonl` 按 key 或按 h 连接。

## 1. 总览

| 项 | 数量 |
| --- | --- |
| 池里的 loc 行 | 2,905 |
| 已发出（有中文且能安全写入 cfg） | 2,560 |
| 覆盖率（发出 / 池） | 88.1% |
| 能桥到中文（发出 + 中英相同 187） | 2,747（94.6%） |
| 产物文件数 | 19 |
| 未解析（桥不到中文） | 158 |

## 2. 按 mod

| mod | 池里 key | 已发出 | 覆盖率 |
| --- | --- | --- | --- |
| `AJE` | 4 | 4 | 100.0% |
| `BurstPQS` | 2 | 1 | 50.0% |
| `CommunityResourcePack` | 2 | 2 | 100.0% |
| `ConformalDecals` | 127 | 124 | 97.6% |
| `ContractConfigurator` | 5 | 3 | 60.0% |
| `EditorExtensionsRedux` | 159 | 138 | 86.8% |
| `KSPCommunityFixes` | 3 | 3 | 100.0% |
| `KerbalismConfig` | 3 | 3 | 100.0% |
| `Kopernicus` | 3 | 2 | 66.7% |
| `MechJeb2` | 6 | 6 | 100.0% |
| `Principia` | 5 | 1 | 20.0% |
| `ROCapsules` | 159 | 159 | 100.0% |
| `RP-1` | 4 | 4 | 100.0% |
| `ReStock` | 2 | 2 | 100.0% |
| `ReStockPlus` | 19 | 19 | 100.0% |
| `RealismOverhaul` | 1,126 | 860 | 76.4% |
| `SXT` | 523 | 521 | 99.6% |
| `TriggerTech` | 527 | 486 | 92.2% |
| `VenStockRevamp` | 226 | 222 | 98.2% |

## 3. 跳过原因

每一条都是「池里有这个键、也知道中文，但不能这么写」，判据见 `mmgen.value_problem`。

| 原因 | 次数 |
| --- | --- |
| `cfg_row` | 9,844 |
| `same_as_source` | 187 |
| `bad_key` | 43 |
| `ambiguous_key` | 0 |

## 4. 未解析样例（最多 40 条）

| mod | key | 英文原文 |
| --- | --- | --- |
| `BurstPQS` | `#BurstPQS_UI_BurstError_Title` | BurstPQS |
| `ContractConfigurator` | `#cc.param.PartValidation.generic.moduleRoot` | <<1>> <<2>> |
| `ContractConfigurator` | `#cc.param.PartValidation.generic.moduleSub` | <<1>>: <<2>> |
| `EditorExtensionsRedux` | `#LOC_EEX_2` | \u |
| `EditorExtensionsRedux` | `#LOC_EEX_6` | DefaultSnap |
| `EditorExtensionsRedux` | `#LOC_EEX_22` | SymmetryLabel |
| `EditorExtensionsRedux` | `#LOC_EEX_53` | MM |
| `EditorExtensionsRedux` | `#LOC_EEX_62` | TextField |
| `EditorExtensionsRedux` | `#LOC_EEX_78` | up |
| `EditorExtensionsRedux` | `#LOC_EEX_86` | targetPosition |
| `EditorExtensionsRedux` | `#LOC_EEX_110` | am |
| `EditorExtensionsRedux` | `#LOC_EEX_112` | hc |
| `EditorExtensionsRedux` | `#LOC_EEX_114` | vs |
| `EditorExtensionsRedux` | `#LOC_EEX_116` | hs |
| `EditorExtensionsRedux` | `#LOC_EEX_123` | rc |
| `EditorExtensionsRedux` | `#LOC_EEX_125` | zs |
| `EditorExtensionsRedux` | `#LOC_EEX_127` | Up: |
| `EditorExtensionsRedux` | `#LOC_EEX_144` | StockSnap |
| `EditorExtensionsRedux` | `#LOC_EEX_156` | PRUNE |
| `EditorExtensionsRedux` | `#LOC_EEX_162` | ToString |
| `Kopernicus` | `#Kopernicus_UI_MissingTextures_NoBody` | <no body> |
| `Principia` | `#Principia_ReferenceFrameSelector_Abbreviation_BodyCentredNonRotating.en-us` | <<2>>CI |
| `Principia` | `#Principia_ReferenceFrameSelector_Abbreviation_BodyCentredParentDirection.en-us` | <<2>><<4>>O |
| `Principia` | `#Principia_ReferenceFrameSelector_Abbreviation_BodySurface.en-us` | <<2>>C<<2>>F |
| `Principia` | `#Principia_ReferenceFrameSelector_Abbreviation_RotatingPulsating.en-us` | <<4>><<2>>L |
| `RealismOverhaul` | `#ro15D13Title` | 15D13 |
| `RealismOverhaul` | `#roA1Title` | A-1 |
| `RealismOverhaul` | `#roA3Title` | A-3 |
| `RealismOverhaul` | `#roAMBRTitle` | AMBR |
| `RealismOverhaul` | `#roAR1Title` | AR-1 |
| `RealismOverhaul` | `#roAR2Title` | AR-1/2 |
| `RealismOverhaul` | `#roBE3Title` | BE-3U |
| `RealismOverhaul` | `#roCOBRATitle` | COBRA |
| `RealismOverhaul` | `#roCOBRAHTitle` | COBRA-H |
| `RealismOverhaul` | `#roDFMMHPETitle` | DFMMHPE |
| `RealismOverhaul` | `#roEAP-241Title` | P241/A EAP |
| `RealismOverhaul` | `#roF-1A_ETSTitle` | F-1A (ETS) |
| `RealismOverhaul` | `#roFW5Title` | FW-5 |
| `RealismOverhaul` | `#roGEM-63XLTitle` | GEM 63XL |
| `RealismOverhaul` | `#roINsTARTitle` | INsTAR |

缺口最多的 mod：`RealismOverhaul` 97、`TriggerTech` 31、`EditorExtensionsRedux` 17、`Principia` 4、`VenStockRevamp` 4、`ContractConfigurator` 2、`BurstPQS` 1、`Kopernicus` 1、`SXT` 1。

## 5. 复现

```
cd tools
python -m i18n_tools.mmgen --stats
python -m i18n_tools.mmgen --report reports/mmgen-coverage.md
```

