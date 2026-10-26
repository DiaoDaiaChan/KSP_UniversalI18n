# 运行时捕获：模板骨架清单（pattern）

> 由 `python -m i18n_tools.capture_ingest --patterns` 生成，只读分析、不写包。骨架算法见 `tools/i18n_tools/skeleton.py`（与 C# 侧同口径），本文件只列产物里 `bucket="pattern"` 的行。

| 项 | 值 |
|---|---|
| 输入会话 | 30 个 `capture-*.jsonl` |
| 去重后 distinct 原文 | 60540 |
| 模板档（合并口径实例数） | 58301 |
| **塌缩成的骨架** | **474 个**（一条骨架覆盖多条实例） |
| 骨架覆盖的实例行 | 55573 |
| 判不出骨架、降级 candidate | 18 |
| 纯符号骨架丢弃（不含 ASCII 字母） | 3 个骨架 / 2710 条实例 |
| **可译骨架**（≥2 英文词且非噪声） | **132 个**（占骨架 27.8%） |
| 噪声骨架 | 262 个 |
| └ ModuleManager | 212 个 |
| └ Profiler HUD | 43 个 |
| └ LoadingScreen | 4 个 |
| └ RDR | 3 个 |
| 落盘体积 | 224.0 KB |
| 复现命令 | `python -m i18n_tools.capture_ingest --all D:\Kerbal Space Program_newmod\PluginData\UI18n --out C:\Users\43701\Documents\github\UniversalI18n\packs\capture-candidates.jsonl --report C:\Users\43701\Do…` |

## 1. 可译骨架（建议送翻，按 count 降序）

判据：剥掉 TMP 标签后仍有 **≥2 个英文词**，且不姓已知噪声类。翻译时**必须原样保留 `#` 的个数与顺序** —— 运行时按顺序把数字填回（`skeleton.fill_back`）。



| # | count | 实例 | sessions | callers | 骨架 | 样本 |
|---:|---:|---:|---:|---|---|---|
| 1 | 233320 | 1 | 1 | `ConfigurationGUI.rend…` | bluedog_Titan#_SecondStageTank (Instance) [KSP/Bumped Specular] | bluedog_Titan2_SecondStageTank (Instance) [KSP/Bumped Specu… |
| 2 | 227180 | 1 | 1 | `ConfigurationGUI.rend…` | bluedog_Centaur_RL# (Instance) [KSP/Emissive/Bumped Specular] | bluedog_Centaur_RL10 (Instance) [KSP/Emissive/Bumped Specul… |
| 3 | 116660 | 2 | 1 | `ConfigurationGUI.rend…` | model_vab_exterior_tile_# (Instance) [KSP/Scenery/Specular] | model_vab_exterior_tile_00 (Instance) [KSP/Scenery/Specular] |
| 4 | 95170 | 1 | 1 | `ConfigurationGUI.rend…` | model_vab_exterior_lines_#_decal (Instance) [KSP/Scenery/Diffuse] | model_vab_exterior_lines_01_decal (Instance) [KSP/Scenery/D… |
| 5 | 95170 | 1 | 1 | `ConfigurationGUI.rend…` | model_vab_exterior_props_# (Instance) [KSP/Scenery/Specular] | model_vab_exterior_props_01 (Instance) [KSP/Scenery/Specula… |
| 6 | 76750 | 1 | 1 | `ConfigurationGUI.rend…` | model_vab_exterior_details_# (Instance) [KSP/Scenery/Emissive/Specular] | model_vab_exterior_details_01 (Instance) [KSP/Scenery/Emiss… |
| 7 | 53016 | 1 | 23 | `MainWindow.RenderWind…`, `principia.ksp_plugin_…` | #-Lévy-#-gf#c#a#b#f#-x#_AVX_FMA | 2026091103-Lévy-162-gf1c108102414a84b1f73-x64_AVX_FMA |
| 8 | 52190 | 1 | 1 | `ConfigurationGUI.rend…` | model_vab_exterior_tile_#_roof (Instance) [KSP/Scenery/Specular] | model_vab_exterior_tile_04_roof (Instance) [KSP/Scenery/Spe… |
| 9 | 47678 | 2 | 20 | `RLVDebugGUI.DrawTelem…` | Aero: Stock \| RH preset: # % | Aero: Stock \| RH preset: 0 % |
| 10 | 43930 | 62 | 14 | `RLVDebugGUI.DrawTelem…` | Aero: FAR \| RH preset: # % | Aero: FAR \| RH preset: 71 % |
| 11 | 39446 | 1 | 3 | `GUI.UnityEngine.GUI.D…`, `unknown` | Real pack samples #-# / control #-# | Real pack samples 01-20 / control 21-25 |
| 12 | 33770 | 1 | 1 | `ConfigurationGUI.rend…` | model_vab_exterior_lines_# (Instance) [KSP/Specular] | model_vab_exterior_lines_01 (Instance) [KSP/Specular] |
| 13 | 31380 | 9 | 1 | `RealAntennas.RealAnte…` | Net: # nodes. # links. # occluders. | Net: 82 nodes. 33 links. 51 occluders. |
| 14 | 27630 | 1 | 1 | `ConfigurationGUI.rend…` | model_tracking_exterior_# (Instance) [KSP/Scenery/Specular] | model_tracking_exterior_01 (Instance) [KSP/Scenery/Specular] |
| 15 | 21520 | 130 | 10 | `MechJebModuleAscentMe…` | znorm: #E-# | znorm: 5.0638E-10 |
| 16 | 21490 | 1 | 1 | `ConfigurationGUI.rend…` | model_vab_exterior_lines_#_decal (Instance) (Instance) [KSP/Scenery/Diffuse] | model_vab_exterior_lines_01_decal (Instance) (Instance) [KS… |
| 17 | 18420 | 1 | 1 | `ConfigurationGUI.rend…` | ksc_exterior_terrain_tile_#_diffuse (Instance) (Instance) [KSP/Scenery/Diffuse] | ksc_exterior_terrain_tile_01_diffuse (Instance) (Instance) … |
| 18 | 12280 | 1 | 1 | `ConfigurationGUI.rend…` | ends.# (Instance) (Instance) [TU/Metallic] | ends.001 (Instance) (Instance) [TU/Metallic] |
| 19 | 12280 | 4 | 1 | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_runwaySec#Lvl#_UltraQualityGrass [KSP/Scenery/Diffus… | ksc_exterior_terrain_grass_runwaySec01Lvl03_UltraQualityGra… |
| 20 | 12280 | 1 | 1 | `ConfigurationGUI.rend…` | sides.# (Instance) (Instance) [TU/Metallic] | sides.001 (Instance) (Instance) [TU/Metallic] |
| 21 | 12280 | 1 | 1 | `ConfigurationGUI.rend…` | restock-rcs-blocks-# (Instance) (Instance) [KSP/Bumped Specular] | restock-rcs-blocks-2 (Instance) (Instance) [KSP/Bumped Spec… |
| 22 | 12280 | 1 | 1 | `ConfigurationGUI.rend…` | bluedog_TitanSolids_Misc # (Instance) [KSP/Emissive/Bumped Specular] | bluedog_TitanSolids_Misc 1 (Instance) [KSP/Emissive/Bumped … |
| 23 | 12280 | 1 | 1 | `ConfigurationGUI.rend…` | Material ## (Instance) (Instance) [DepthMask] | Material #326 (Instance) (Instance) [DepthMask] |
| 24 | 12280 | 1 | 1 | `ConfigurationGUI.rend…` | restock-decouplers-# (Instance) [KSP/Bumped Specular] | restock-decouplers-2 (Instance) [KSP/Bumped Specular] |
| 25 | 9210 | 1 | 1 | `ConfigurationGUI.rend…` | phong# (Instance) [Legacy Shaders/Diffuse] | phong1 (Instance) [Legacy Shaders/Diffuse] |
| 26 | 9210 | 1 | 1 | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_# (Instance) [KSP/Scenery/Diffuse Ground KSC] | ksc_exterior_terrain_grass_02 (Instance) [KSP/Scenery/Diffu… |
| 27 | 6478 | 1 | 3 | `ConfigurationGUI.rend…` | <size=10><color=grey>Non-temporal morphological filters (CMAA # / SMAA) produce… | <size=10><color=grey>Non-temporal morphological filters (CM… |
| 28 | 6478 | 1 | 3 | `ConfigurationGUI.rend…` | Intel CMAA # (No Ghosting) | Intel CMAA 2 (No Ghosting) |
| 29 | 6140 | 1 | 1 | `ConfigurationGUI.rend…` | ksc_exterior_terrain_tile_#_diffuse (Instance) [KSP/Scenery/Diffuse] | ksc_exterior_terrain_tile_01_diffuse (Instance) [KSP/Scener… |
| 30 | 6140 | 2 | 1 | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_runwayEnd#Lvl#_UltraQualityGrass [KSP/Scenery/Diffus… | ksc_exterior_terrain_grass_runwayEnd09Lvl03_UltraQualityGra… |
| 31 | 6140 | 1 | 1 | `ConfigurationGUI.rend…` | model_vab_exterior_tile_# (Instance) [KSP/Scenery/Emissive/Specular] | model_vab_exterior_tile_02 (Instance) [KSP/Scenery/Emissive… |
| 32 | 6140 | 2 | 1 | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_fillerx#Lvl#_ultraQualityGrass [KSP/Scenery/Diffuse … | ksc_exterior_terrain_grass_fillerx1Lvl03_ultraQualityGrass … |
| 33 | 6140 | 1 | 1 | `ConfigurationGUI.rend…` | fairing# (Instance) (Instance) [KSP/Bumped Specular] | fairing1 (Instance) (Instance) [KSP/Bumped Specular] |
| 34 | 6140 | 1 | 1 | `ConfigurationGUI.rend…` | model_vab_exterior_tile_#_glass (Instance) [KSP/Scenery/Emissive/Specular] | model_vab_exterior_tile_05_glass (Instance) [KSP/Scenery/Em… |
| 35 | 6020 | 1 | 1 | `FerramAerospaceResear…` | Mesh: use # bit indices: | Mesh: use 32 bit indices: |
| 36 | 6020 | 1 | 1 | `FerramAerospaceResear…` | # bit indices | 16 bit indices |
| 37 | 5716 | 1 | 1 | `FerramAerospaceResear…` | FAR Debug Options, v#.# 'Marangoni' | FAR Debug Options, v0.16.1.2 'Marangoni' |
| 38 | 3070 | 1 | 1 | `ConfigurationGUI.rend…` | restock-relays-# (Instance) (Instance) [KSP/Bumped Specular] | restock-relays-1 (Instance) (Instance) [KSP/Bumped Specular] |
| 39 | 3070 | 1 | 1 | `ConfigurationGUI.rend…` | SaturnStandPad#_Cube#_auvMat (Instance) [KSP/Diffuse] | SaturnStandPad4250_Cube70_auvMat (Instance) [KSP/Diffuse] |
| 40 | 3070 | 1 | 1 | `ConfigurationGUI.rend…` | Monolith# [Legacy Shaders/Bumped Specular] | Monolith00 [Legacy Shaders/Bumped Specular] |
| 41 | 3070 | 1 | 1 | `ConfigurationGUI.rend…` | RSBpslvSpecular.# (Instance) (Instance) [KSP/Specular] | RSBpslvSpecular.001 (Instance) (Instance) [KSP/Specular] |
| 42 | 3070 | 1 | 1 | `ConfigurationGUI.rend…` | restock-antenna-# [KSP/ScreenSpaceMaskBumped] | restock-antenna-1 [KSP/ScreenSpaceMaskBumped] |
| 43 | 3070 | 1 | 1 | `ConfigurationGUI.rend…` | SC-GEN-FR-Mat.# (Instance) [TU/Metallic] | SC-GEN-FR-Mat.004 (Instance) [TU/Metallic] |
| 44 | 3070 | 1 | 1 | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_MCLvl#_UltraQualityGrass [KSP/Scenery/Diffuse Ground… | ksc_exterior_terrain_grass_MCLvl03_UltraQualityGrass [KSP/S… |
| 45 | 3070 | 1 | 1 | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_r&dLvl#_UltraQualityGrass [KSP/Scenery/Diffuse Groun… | ksc_exterior_terrain_grass_r&dLvl03_UltraQualityGrass [KSP/… |
| 46 | 3070 | 1 | 1 | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_launchPadLvl#_UltraQualityGrass [KSP/Scenery/Diffuse… | ksc_exterior_terrain_grass_launchPadLvl03_UltraQualityGrass… |
| 47 | 3070 | 1 | 1 | `ConfigurationGUI.rend…` | restock-relays-#-Cu (Instance) (Instance) [KSP/Bumped Specular] | restock-relays-1-Cu (Instance) (Instance) [KSP/Bumped Specu… |
| 48 | 3070 | 1 | 1 | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_VABLvl#_UltraQualityGrass [KSP/Scenery/Diffuse Groun… | ksc_exterior_terrain_grass_VABLvl03_UltraQualityGrass [KSP/… |
| 49 | 3070 | 1 | 1 | `ConfigurationGUI.rend…` | #d#caa-#a-#-b#-e#d#ba# [EVE/ScreenSpacePlanetLight] | 59d36caa-323a-4493-b399-e4d25385ba27 [EVE/ScreenSpacePlanet… |
| 50 | 3070 | 1 | 1 | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_TSLvl#_UltraQualityGrass [KSP/Scenery/Diffuse Ground… | ksc_exterior_terrain_grass_TSLvl03_UltraQualityGrass [KSP/S… |
| 51 | 3070 | 1 | 1 | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_adminLvl#_ultraQualityGrass [KSP/Scenery/Diffuse Gro… | ksc_exterior_terrain_grass_adminLvl03_ultraQualityGrass [KS… |
| 52 | 3070 | 1 | 1 | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_AClvl#_ultraQualityGrass [KSP/Scenery/Diffuse Ground… | ksc_exterior_terrain_grass_AClvl03_ultraQualityGrass [KSP/S… |
| 53 | 3070 | 1 | 1 | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_sphLvl#_UltraQualityGrass [KSP/Scenery/Diffuse Groun… | ksc_exterior_terrain_grass_sphLvl03_UltraQualityGrass [KSP/… |
| 54 | 3070 | 1 | 1 | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_launchPadLvl#_UltraQualityGrass# [KSP/Scenery/Diffus… | ksc_exterior_terrain_grass_launchPadLvl03_UltraQualityGrass… |
| 55 | 2932 | 1 | 1 | `RLVDebugGUI.DrawPerfo…` | D:/Kerbal Space Program_newmod/KSP_x#_Data/../GameData/RealisticLaunchVisuals/L… | D:/Kerbal Space Program_newmod/KSP_x64_Data/../GameData/Rea… |
| 56 | 2494 | 1 | 29 | `LoadingScreen.UpdateP…`, `TMP_Text.TMPro.TMP_Te…` | RP-# Initialization & Setup | RP-1 Initialization & Setup |
| 57 | 1455 | 3 | 1 | `EngineConfigInfoPanel…` | at <color=#90EE90>#m #s</color> # in <color=#FF6666>#</color> burns fail | at <color=#90EE90>1m 40s</color> 1 in <color=#FF6666>105.3<… |
| 58 | 949 | 1 | 1 | `EVEManager.GlobalEVEM…` | EVE #.# Config Editor | EVE 1.11.7.2 Config Editor |
| 59 | 890 | 1 | 1 | `ConfigurationGUI.rend…` | Applied rule: # matching materials / # surfaces. Save to retain it across resta… | Applied rule: 946 matching materials / 2173 surfaces. Save … |
| 60 | 826 | 94 | 6 | `UIPartActionLabel.Upd…` | Flight Data: # du / # du | Flight Data: 862 du / 10000 du |
| 61 | 643 | 4 | 1 | `UIPartActionLabel.Upd…` | cost: Dry: # Wet: # | cost: Dry: 0 Wet: 0 |
| 62 | 596 | 39 | 5 | `UIPartActionLabel.Upd…` | Total Run Time: #s/#s | Total Run Time: 0s/135s |
| 63 | 596 | 46 | 5 | `UIPartActionLabel.Upd…` | Thrust Ratio: # | Thrust Ratio: 1 |
| 64 | 596 | 39 | 5 | `UIPartActionLabel.Upd…` | Current Run Time: #s/#s | Current Run Time: 0s/135s |
| 65 | 500 | 3 | 1 | `UIPartActionLabel.Upd…` | Largest Diameter: #m | Largest Diameter: 7.0000m |
| 66 | 500 | 1 | 1 | `UIPartActionLabel.Upd…` | Total Length: #m | Total Length: 21.0000m |
| 67 | 485 | 1 | 1 | `EngineConfigInfoPanel…` | Set to Current (#du) | Set to Current (862du) |
| 68 | 476 | 434 | 4 | `UIPartActionLabel.Upd…` | Mass Flow: # ton/s | Mass Flow: 1.78795 ton/s |
| 69 | 468 | 1 | 1 | `RealAntennas.RealAnte…` | Antennas/vessel: # | Antennas/vessel: 1.5 |
| 70 | 397 | 2 | 5 | `UIPartActionLabel.Upd…` | Current Throttle: #% | Current Throttle: 100.00% |
| 71 | 378 | 1 | 1 | `RLVDebugGUI.DrawPerfo…` | D:/Kerbal Space Program_newmod/KSP_x#_Data/../GameData/RealisticLaunchVisuals/L… | D:/Kerbal Space Program_newmod/KSP_x64_Data/../GameData/Rea… |
| 72 | 342 | 8 | 5 | `UIPartActionLabel.Upd…` | Time Since Shutdown: #s | Time Since Shutdown: 0.00s |
| 73 | 342 | 1 | 5 | `UIPartActionLabel.Upd…` | Ignition Penalty for Q: # % | Ignition Penalty for Q: 0.00 % |
| 74 | 342 | 3 | 5 | `UIPartActionLabel.Upd…` | Ignition Chance: # % | Ignition Chance: 99.90 % |
| 75 | 342 | 1 | 5 | `UIPartActionLabel.Upd…` | Thrust Modifier: # % | Thrust Modifier: 100.00 % |
| 76 | 342 | 1 | 5 | `UIPartActionLabel.Upd…` | Restart Ignition Penalty: # % | Restart Ignition Penalty: 0.00 % |
| 77 | 334 | 3 | 5 | `UIPartActionLabel.Upd…` | Mean Time Between Failures: #h | Mean Time Between Failures: 2.60h |
| 78 | 318 | 1 | 4 | `UIPartActionLabel.Upd…` | Eng. Internal Temp: NaN K / # K | Eng. Internal Temp: NaN K / 2,430 K |
| 79 | 301 | 6 | 5 | `UIPartActionLabel.Upd…` | Last Restart: Roll: # %, Chance: # % | Last Restart: Roll: 83.08 %, Chance: 99.90 % |
| 80 | 254 | 2 | 2 | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>TimingPre</b> -> <color=#88bbdd>Fi… | <b>#1</b> <color=#a29bfe>[Plugin]</color> <b>TimingPre</b> … |
| 81 | 254 | 4 | 2 | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>Kerbalism</b> -> <color=#88bbdd>Fi… | <b>#6</b> <color=#a29bfe>[Plugin]</color> <b>Kerbalism</b> … |
| 82 | 254 | 4 | 2 | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>ModularFlightIntegrator</b> -> <co… | <b>#4</b> <color=#a29bfe>[Plugin]</color> <b>ModularFlightI… |
| 83 | 250 | 5 | 2 | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>Timing#</b> -> <color=#88bbdd>Fixe… | <b>#5</b> <color=#a29bfe>[Plugin]</color> <b>Timing1</b> ->… |
| 84 | 248 | 1 | 1 | `ProfilerUI.DrawPartsT…` | <color=#FFFFFF>Angled RCS Block [ #/# N Class]</color> | <color=#FFFFFF>Angled RCS Block [ 28/45 N Class]</color> |
| 85 | 232 | 4 | 3 | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>PostProcessLayer</b> -> <color=#88… | <b>#2</b> <color=#a29bfe>[Plugin]</color> <b>PostProcessLay… |
| 86 | 230 | 1 | 1 | `UIPartActionLabel.Upd…` | Max Ignition Chance: # % | Max Ignition Chance: 100.00 % |
| 87 | 230 | 1 | 1 | `UIPartActionLabel.Upd…` | Current Reliability: # %, MTBF: #h | Current Reliability: 98.73 %, MTBF: 2.93h |
| 88 | 230 | 1 | 1 | `UIPartActionLabel.Upd…` | Reliability at Max Data: # %, MTBF: #h | Reliability at Max Data: 99.71 %, MTBF: 13.02h |
| 89 | 230 | 1 | 1 | `UIPartActionLabel.Upd…` | Current Ignition Chance: # % | Current Ignition Chance: 99.91 % |
| 90 | 230 | 1 | 1 | `UIPartActionLabel.Upd…` | Rated Burn Time: #s (#m #s) | Rated Burn Time: 135s (2m 15s) |
| 91 | 196 | 3 | 3 | `ProfilerUI.DrawTimeli…` | <b>🗑️ Mono:</b> <color=#FF3333>#MB</color> GC: <color=#33FF33>#/s</color> | <b>🗑️ Mono:</b> <color=#FF3333>3469MB</color> GC: <color=#3… |
| 92 | 175 | 73 | 5 | `UIPartActionLabel.Upd…` | Mass Flow: # kg/s | Mass Flow: 0.00000 kg/s |
| 93 | 158 | 4 | 2 | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>ReflectionProbeFixer</b> -> <color… | <b>#3</b> <color=#a29bfe>[Plugin]</color> <b>ReflectionProb… |
| 94 | 156 | 3 | 1 | `UIPartActionLabel.Upd…` | Mass: #kg | Mass: 496.4kg |
| 95 | 124 | 1 | 1 | `ProfilerUI.DrawPartsT…` | <color=#FFFFFF>UA# Nosecone & Separation Motor</color> | <color=#FFFFFF>UA1205 Nosecone & Separation Motor</color> |
| 96 | 88 | 1 | 1 | `UIPartActionLabel.Upd…` | Mass: #t (both) | Mass: 0.608t (both) |
| 97 | 88 | 1 | 1 | `UIPartActionLabel.Upd…` | Cost: # (both) | Cost: 547 (both) |
| 98 | 82 | 1 | 1 | `ProfilerUI.DrawPlugin…` | UI#n.Runtime | UI18n.Runtime |
| 99 | 82 | 1 | 1 | `ProfilerUI.DrawPlugin…` | ▶ <color=#FFFFFF>UI#nAddon</color> | ▶ <color=#FFFFFF>UI18nAddon</color> |
| 100 | 79 | 16 | 2 | `UIPartActionLabel.Upd…` | Eng. Internal Temp: # K / # K | Eng. Internal Temp: 317 K / 3,600 K |
| 101 | 78 | 2 | 1 | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>Part</b> -> <color=#88bbdd>FixedUp… | <b>#2</b> <color=#a29bfe>[Plugin]</color> <b>Part</b> -> <c… |
| 102 | 78 | 1 | 2 | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>FlightGUI</b> -> <color=#88bbdd>Fi… | <b>#6</b> <color=#a29bfe>[Plugin]</color> <b>FlightGUI</b> … |
| 103 | 62 | 1 | 1 | `ProfilerUI.DrawPartsT…` | <color=#FFFFFF>#/#kN Thruster</color> | <color=#FFFFFF>1.1/1.78kN Thruster</color> |
| 104 | 62 | 1 | 1 | `ProfilerUI.DrawPartsT…` | <color=#FFFFFF>RL-# Series (Non-Extendable Nozzle)</color> | <color=#FFFFFF>RL-10 Series (Non-Extendable Nozzle)</color> |
| 105 | 50 | 1 | 1 | `ProfilerUI.DrawTimeli…` | <b>🗑️ Mono:</b> <color=#FFFF33>#MB</color> GC: <color=#33FF33>#/s</color> | <b>🗑️ Mono:</b> <color=#FFFF33>3537MB</color> GC: <color=#3… |
| 106 | 48 | 2 | 2 | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>MaterialPipelineManager</b> -> <co… | <b>#3</b> <color=#a29bfe>[Plugin]</color> <b>MaterialPipeli… |
| 107 | 42 | 1 | 2 | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>RLVAddon</b> -> <color=#88bbdd>Upd… | <b>#1</b> <color=#a29bfe>[Plugin]</color> <b>RLVAddon</b> -… |
| 108 | 32 | 1 | 1 | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>PrincipiaPluginAdapter</b> -> <col… | <b>#6</b> <color=#a29bfe>[Plugin]</color> <b>PrincipiaPlugi… |
| 109 | 28 | 1 | 28 | `BurstPQS.UI.DebugUI.D…`, `DebugUIManager.Create…` | #-bit Height (R# DDS) | 16-bit Height (R16 DDS) |
| 110 | 28 | 1 | 28 | `TMP_Text.TMPro.TMP_Te…`, `VersionReadout.Start` | #.# (WindowsPlayer x#) zh-cn #.# Breaking Ground | 1.12.5.3190 (WindowsPlayer x64) zh-cn 1.7.1 Breaking Ground |
| 111 | 28 | 1 | 28 | `TMP_Text.TMPro.TMP_Te…`, `VersionReadout.Start` | #.# (WindowsPlayer x#) zh-cn | 1.12.5.3190 (WindowsPlayer x64) zh-cn |
| 112 | 26 | 1 | 20 | `UIFactory.CreateText` | RP-#/maintecost | RP-1/maintecost |
| 113 | 26 | 1 | 20 | `UIFactory.CreateText` | EngineGroupController/ToolbarIcon# | EngineGroupController/ToolbarIcon4 |
| 114 | 20 | 1 | 20 | `WorkbenchCanvasView.B…` | MODULAR FLIGHT PANEL # | MODULAR FLIGHT PANEL 3.0 |
| 115 | 20 | 1 | 1 | `ProfilerUI.DrawTimeli…` | <b>🗑️ Mono:</b> <color=#FFFF33>#MB</color> GC: <color=#ff8800>#/s</color> | <b>🗑️ Mono:</b> <color=#FFFF33>3537MB</color> GC: <color=#f… |
| 116 | 20 | 1 | 1 | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>FlightInputHandler</b> -> <color=#… | <b>#4</b> <color=#a29bfe>[Plugin]</color> <b>FlightInputHan… |
| 117 | 16 | 1 | 1 | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>ToolbarReskinWatcher</b> -> <color… | <b>#3</b> <color=#a29bfe>[Plugin]</color> <b>ToolbarReskinW… |
| 118 | 11 | 1 | 1 | `UIPartActionLabel.Upd…` | Tech Level: # | Tech Level: 10 |
| 119 | 11 | 1 | 1 | `UIPartActionLabel.Upd…` | Power (Idle): # Watts | Power (Idle): 9.50 Watts |
| 120 | 11 | 1 | 1 | `UIPartActionLabel.Upd…` | Transmit Power (dBm): # dBm | Transmit Power (dBm): 30.0 dBm |
| 121 | 11 | 1 | 1 | `UIPartActionLabel.Upd…` | Power (Active): # Watts | Power (Active): 11.54 Watts |
| 122 | 11 | 1 | 1 | `UIPartActionLabel.Upd…` | Gain: # dBi | Gain: 1.5 dBi |
| 123 | 8 | 3 | 1 | `UIPartActionLabel.Upd…` | Mean Time Between Failures: #m | Mean Time Between Failures: 17.19m |
| 124 | 4 | 1 | 1 | `MapNode.UpdateCaption…` | SSC - Santiago VHF-Band: # dBi UHF-Band: # dBi S-Band: # dBi #° S, #° W | SSC - Santiago VHF-Band: 1.0 dBi UHF-Band: 3.0 dBi S-Band: … |
| 125 | 4 | 1 | 1 | `MainWindow.RenderWind…` | #-Lévy-#-g#caad#abcbc#b#fe#aa-x#_AVX_FMA | 2026091103-Lévy-157-g2caad1abcbc5b0fe52aa-x64_AVX_FMA |
| 126 | 3 | 1 | 1 | `ScreenMessages.PostMe…` | Insufficient Avionics, Locking Controls (supports #t, vessel #t) | Insufficient Avionics, Locking Controls (supports 0.000t, v… |
| 127 | 2 | 1 | 1 | `MapNode.UpdateCaption…` | DSA # - Malargüe VHF-Band: # dBi UHF-Band: # dBi #° S, #° W | DSA 3 - Malargüe VHF-Band: 1.0 dBi UHF-Band: 3.0 dBi 35.78°… |
| 128 | 2 | 1 | 1 | `BaseFlightWidget.SetT…` | DSS # - GOLDSTONE | DSS 14 - GOLDSTONE |
| 129 | 2 | 1 | 1 | `MapNode.UpdateCaption…` | STDN - Quito VHF-Band: # dBi UHF-Band: # dBi #° S, #° W | STDN - Quito VHF-Band: 1.0 dBi UHF-Band: 3.0 dBi 0.62° S, 7… |
| 130 | 2 | 1 | 1 | `BaseFlightWidget.SetT…` | DSS # - CANBERRA | DSS 43 - CANBERRA |
| 131 | 2 | 1 | 1 | `BaseFlightWidget.SetT…` | NIP # - USSURIYSK | NIP 15 - USSURIYSK |
| 132 | 2 | 1 | 1 | `MapNode.UpdateCaption…` | DSS # - Goldstone VHF-Band: # dBi UHF-Band: # dBi S-Band: # dBi X-Band: # dBi K… | DSS 14 - Goldstone VHF-Band: 20.0 dBi UHF-Band: 34.0 dBi S-… |

## 2. 噪声骨架（不建议翻）

这些即使含英文单词也不该进包：加载屏进度、ModuleManager 诊断、Profiler HUD、RDR 遥测 —— 全是游戏内部诊断串，翻了没有观感收益，还白占翻译配额。



| # | count | 实例 | sessions | 噪声类 | callers | 骨架 | 样本 |
|---:|---:|---:|---:|---|---|---|---|
| 1 | 315909 | 1074 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL] | ModuleManager: 12728 patches applied (37 %) :FOR[REALISMOVE… |
| 2 | 169598 | 1298 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[XXXRP#] | ModuleManager: 50726 patches applied (92 %) :FOR[XXXRP0] |
| 3 | 113958 | 428 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAULENGINES] | ModuleManager: 24258 patches applied (51 %) :FOR[REALISMOVE… |
| 4 | 103100 | 1339 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAULENGINES] | ModuleManager: 25090 patches applied (52 %) :AFTER[REALISMO… |
| 5 | 98450 | 7 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[REALISMOVERHAULENGINESPOST] | ModuleManager: 27195 patches applied (60 %) :BEFORE[REALISM… |
| 6 | 86673 | 201 | 20 | Profiler HUD | `HUDProfilerOverlay.Re…` | <color=#00E5FF><b># ms</b></color> | <color=#00E5FF><b>0.001 ms</b></color> |
| 7 | 76703 | 101 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[RP-#-KERBALISM] | ModuleManager: 46425 patches applied (71 %) :AFTER[RP-0-KER… |
| 8 | 66790 | 2132 | 20 | Profiler HUD | `HUDProfilerOverlay.Cr…`, `ProfilerUI.DrawPlugin…` | # ms | 0.00 ms |
| 9 | 63124 | 16 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZTESTFLIGHT] | ModuleManager: 59543 patches applied (96 %) :FOR[ZTESTFLIGH… |
| 10 | 55384 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZZZZZZZZZZZZZZZZZZZZZRP#] | ModuleManager: 75264 patches applied (99 %) :FOR[ZZZZZZZZZZ… |
| 11 | 45357 | 68 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZZZTAGCLEANUP] | ModuleManager: 71560 patches applied (99 %) :BEFORE[ZZZTAGC… |
| 12 | 42582 | 102 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[RP-#-KERBALISM] | ModuleManager: 39252 patches applied (71 %) :FOR[RP-0-KERBA… |
| 13 | 42023 | 49 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZREALPLUME] | ModuleManager: 52141 patches applied (95 %) :AFTER[ZREALPLU… |
| 14 | 40758 | 1 | 12 | ModuleManager | `unknown` | ModuleManager: # patches loaded from cache | ModuleManager: 81181 patches loaded from cache |
| 15 | 40651 | 80 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[KERBALISM] | ModuleManager: 76746 patches applied (99 %) :LAST[KERBALISM] |
| 16 | 37748 | 147 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[RP-#] | ModuleManager: 35728 patches applied (69 %) :FOR[RP-0] |
| 17 | 34717 | 96 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[REALPLUME] | ModuleManager: 28530 patches applied (62 %) :BEFORE[REALPLU… |
| 18 | 33158 | 696 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | <b>FPS:</b> # \| <b>Frame Time:</b> # ms \| <b>Jitter (p#−p#):</b> # ms \| <b>Heap… | <b>FPS:</b> 42.5 \| <b>Frame Time:</b> 23.50 ms \| <b>Jitter … |
| 19 | 32942 | 690 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Late Sync (BetterLate / Motion): <b><color=#55ff55># ms</color></b> (Peak: #… | 4. Late Sync (BetterLate / Motion): <b><color=#55ff55>0.303… |
| 20 | 32890 | 678 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Adapter FixedUpdate: <b><color=#55ff55># ms</color></b> (Peak: # ms / Budget… | 2. Adapter FixedUpdate: <b><color=#55ff55>0.043 ms</color><… |
| 21 | 32778 | 684 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Markers & Geometry (LateUpdate / Markers): <b><color=#55ff55># ms</color></b… | 5. Markers & Geometry (LateUpdate / Markers): <b><color=#55… |
| 22 | 32473 | 113 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAUL] | ModuleManager: 14337 patches applied (48 %) :AFTER[REALISMO… |
| 23 | 32202 | 669 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Precalculation (Precalc / Bodies): <b><color=#55ff55># ms</color></b> (Peak:… | 1. Precalculation (Precalc / Bodies): <b><color=#55ff55>1.1… |
| 24 | 31770 | 569 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | • <b>Frame minus stage sum:</b> <b># ms</b> [Unattributed estimate; not measure… | • <b>Frame minus stage sum:</b> <b>6.81 ms</b> [Unattribute… |
| 25 | 31089 | 40 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZREALISMOVERHAUL] | ModuleManager: 65190 patches applied (98 %) :FOR[ZZZREALISM… |
| 26 | 30958 | 643 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Post-physics callback (after yield): <b><color=#55ff55># ms</color></b> (Pea… | 3. Post-physics callback (after yield): <b><color=#55ff55>0… |
| 27 | 30339 | 109 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZKERBALISM] | ModuleManager: 62931 patches applied (97 %) :FOR[ZZZKERBALI… |
| 28 | 28096 | 222 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ROWATERFALL] | ModuleManager: 30348 patches applied (66 %) :BEFORE[ROWATER… |
| 29 | 27010 | 10 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZTESTFLIGHT] | ModuleManager: 57222 patches applied (96 %) :BEFORE[ZTESTFL… |
| 30 | 25830 | 179 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | Measured stage sum (incomplete): <b># ms</b> Smoothed per-call estimates; overl… | Measured stage sum (incomplete): <b>2.74 ms</b> Smoothed pe… |
| 31 | 25686 | 175 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | • <b>Stage sum minus native scopes:</b> <b># ms</b> [Estimate; not measured C# … | • <b>Stage sum minus native scopes:</b> <b>2.59 ms</b> [Est… |
| 32 | 23850 | 79 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | • <b>Native call scopes:</b> <b># ms</b> [Physics: #ms \| Plotting: #ms; include… | • <b>Native call scopes:</b> <b>0.21 ms</b> [Physics: 0.19m… |
| 33 | 23490 | 3 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Celestial Trajectory (Celestial Subtree): <b><color=#55ff55># ms</color></b>… | 7. Celestial Trajectory (Celestial Subtree): <b><color=#55f… |
| 34 | 23490 | 3 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Vessel Trajectory (Vessel Trajectory): <b><color=#55ff55># ms</color></b> (P… | 6. Vessel Trajectory (Vessel Trajectory): <b><color=#55ff55… |
| 35 | 21646 | 243 | 18 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied | ModuleManager: 81181 patches applied |
| 36 | 17230 | 112 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[REALISMOVERHAUL] | ModuleManager: 11548 patches applied (18 %) :BEFORE[REALISM… |
| 37 | 17224 | 73 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALFUELS] | ModuleManager: 8770 patches applied (15 %) :FOR[REALFUELS] |
| 38 | 15680 | 11 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[KOP_MULTI_STAR] | ModuleManager: 77073 patches applied (99 %) :LAST[KOP_MULTI… |
| 39 | 15618 | 60 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZZZKERBALISM] | ModuleManager: 63202 patches applied (97 %) :AFTER[ZZZKERBA… |
| 40 | 14855 | 580 | 20 | Profiler HUD | `HUDProfilerOverlay.Re…` | <color=#FFE000><b># ms</b></color> | <color=#FFE000><b>0.227 ms</b></color> |
| 41 | 13737 | 12 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZREALFUELS] | ModuleManager: 63933 patches applied (98 %) :FOR[ZZZREALFUE… |
| 42 | 13511 | 18 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALPLUME] | ModuleManager: 28729 patches applied (64 %) :AFTER[REALPLUM… |
| 43 | 13017 | 28 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZZZKERBALISM] | ModuleManager: 62482 patches applied (97 %) :BEFORE[ZZZKERB… |
| 44 | 12518 | 22 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FINAL | ModuleManager: 79323 patches applied (100 %) :FINAL |
| 45 | 11970 | 38 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[RP-#] | ModuleManager: 47810 patches applied (71 %) :AFTER[RP-1] |
| 46 | 11722 | 39 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_AUDIO] | ModuleManager: 54842 patches applied (96 %) :FOR[ZROWATERFA… |
| 47 | 9422 | 10 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAULENGINESPOST] | ModuleManager: 28405 patches applied (60 %) :AFTER[REALISMO… |
| 48 | 9032 | 3059 | 30 | LoadingScreen | `LoadingScreen.UpdateP…`, `TMP_Text.TMPro.TMP_Te…` | Loading texture asset #/# (buffer=#MB) | Loading texture asset 2223/5736 (buffer=34MB) |
| 49 | 8591 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZELECTRICITYUNITS] | ModuleManager: 62159 patches applied (97 %) :FOR[ZZZELECTRI… |
| 50 | 8422 | 37 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL_MATERIALS] | ModuleManager: 20913 patches applied (49 %) :FOR[REALISMOVE… |
| 51 | 8413 | 39 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[SOLSYSTEM] | ModuleManager: 48376 patches applied (73 %) :AFTER[SOLSYSTE… |
| 52 | 6974 | 19 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZZZREALISMOVERHAUL] | ModuleManager: 70651 patches applied (98 %) :AFTER[ZZZREALI… |
| 53 | 6856 | 14 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZTAGCLEANUP] | ModuleManager: 71756 patches applied (99 %) :FOR[ZZZTAGCLEA… |
| 54 | 6822 | 117 | 20 | Profiler HUD | `HUDProfilerOverlay.Cr…`, `HUDProfilerOverlay.Re…` | FPS: # | FPS: 20 |
| 55 | 6792 | 17 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[REALISMOVERHAUL] | ModuleManager: 77687 patches applied (100 %) :LAST[REALISMO… |
| 56 | 6687 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZREALPLUME] | ModuleManager: 51974 patches applied (95 %) :BEFORE[ZREALPL… |
| 57 | 6660 | 13 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAULENGINESPOST] | ModuleManager: 28105 patches applied (60 %) :FOR[REALISMOVE… |
| 58 | 6481 | 63 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[SOLSYSTEM] | ModuleManager: 48069 patches applied (71 %) :FOR[SOLSYSTEM] |
| 59 | 6311 | 35 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[REALISMOVERHAULENGINES] | ModuleManager: 23542 patches applied (50 %) :BEFORE[REALISM… |
| 60 | 6016 | 1 | 5 | Profiler HUD | `RLVDebugGUI.PerfCell`, `RLVDebugGUI.PerfHeade…` | P# ms | P95 ms |
| 61 | 5974 | 962 | 18 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LEGACY (default) | ModuleManager: 2968 patches applied (9 %) :LEGACY (default) |
| 62 | 5964 | 9 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_CONTROLLERS] | ModuleManager: 56036 patches applied (96 %) :FOR[ZROWATERFA… |
| 63 | 5773 | 151 | 20 | Profiler HUD | `HUDProfilerOverlay.Re…` | <color=#34C759><b># ms</b></color> | <color=#34C759><b>0.054 ms</b></color> |
| 64 | 5742 | 36 | 3 | Profiler HUD | `RLVDebugGUI.DrawMetri…` | # FPS | 20 FPS |
| 65 | 5431 | 3 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_MAINPLUMEVARIANTS] | ModuleManager: 55671 patches applied (96 %) :FOR[ZROWATERFA… |
| 66 | 5350 | 6 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[RP-#] | ModuleManager: 31645 patches applied (68 %) :BEFORE[RP-0] |
| 67 | 5330 | 30 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[RP-#-KERBALISM] | ModuleManager: 39062 patches applied (71 %) :BEFORE[RP-0-KE… |
| 68 | 4849 | 18 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZREALPLUME] | ModuleManager: 62140 patches applied (96 %) :FOR[ZZREALPLUM… |
| 69 | 4795 | 8 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZREALANTENNAS] | ModuleManager: 51700 patches applied (95 %) :AFTER[ZREALANT… |
| 70 | 4614 | 42 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[FERRAMAEROSPACERESEARCH] | ModuleManager: 6256 patches applied (13 %) :AFTER[FERRAMAER… |
| 71 | 4405 | 45 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[FERRAMAEROSPACERESEARCH] | ModuleManager: 4134 patches applied (13 %) :FOR[FERRAMAEROS… |
| 72 | 4298 | 24 | 15 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[AJE] | ModuleManager: 3443 patches applied (12 %) :FOR[AJE] |
| 73 | 4184 | 1176 | 29 | LoadingScreen | `LoadingScreen.UpdateP…`, `TMP_Text.TMPro.TMP_Te…` | Loading model asset #/# (buffer=#MB) | Loading model asset 2832/3042 (buffer=40MB) |
| 74 | 4162 | 48 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ROTANKS] | ModuleManager: 30098 patches applied (65 %) :FOR[ROTANKS] |
| 75 | 4094 | 3 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_GLOWRESIZE] | ModuleManager: 55089 patches applied (96 %) :FOR[ZROWATERFA… |
| 76 | 4093 | 10 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZREALANTENNAS] | ModuleManager: 51222 patches applied (95 %) :FOR[ZREALANTEN… |
| 77 | 4073 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZTESTFLIGHT] | ModuleManager: 61790 patches applied (96 %) :AFTER[ZTESTFLI… |
| 78 | 3954 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZZZREALFUELS] | ModuleManager: 64389 patches applied (98 %) :AFTER[ZZZREALF… |
| 79 | 3887 | 6 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ROWATERFALL] | ModuleManager: 31088 patches applied (68 %) :FOR[ROWATERFAL… |
| 80 | 3863 | 142 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[#_RESTOCK] | ModuleManager: 3411 patches applied (11 %) :FOR[000_RESTOCK] |
| 81 | 3842 | 20 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[RO-KERBALISMHARDDRIVES] | ModuleManager: 29378 patches applied (65 %) :FOR[RO-KERBALI… |
| 82 | 3615 | 6 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZZZREALISMOVERHAUL] | ModuleManager: 64747 patches applied (98 %) :BEFORE[ZZZREAL… |
| 83 | 3248 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_MAINEFFECT] | ModuleManager: 53388 patches applied (95 %) :FOR[ZROWATERFA… |
| 84 | 3092 | 24 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[ZZZKERBALISM] | ModuleManager: 79028 patches applied (100 %) :LAST[ZZZKERBA… |
| 85 | 3013 | 23 | 15 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALANTENNAS] | ModuleManager: 8739 patches applied (14 %) :FOR[REALANTENNA… |
| 86 | 2865 | 30 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[RESTOCKPLUS] | ModuleManager: 29040 patches applied (64 %) :FOR[RESTOCKPLU… |
| 87 | 2816 | 16 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL_HEATSHIELD] | ModuleManager: 19466 patches applied (49 %) :FOR[REALISMOVE… |
| 88 | 2697 | 22 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ROWATERFALL] | ModuleManager: 31478 patches applied (68 %) :AFTER[ROWATERF… |
| 89 | 2676 | 18 | 15 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[REALANTENNAS] | ModuleManager: 8693 patches applied (14 %) :BEFORE[REALANTE… |
| 90 | 2592 | 13 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZRP-#] | ModuleManager: 70682 patches applied (98 %) :FOR[ZZZRP-0] |
| 91 | 2400 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZROWATERFALL_#_MAINPLUMEVARIANTS] | ModuleManager: 55558 patches applied (96 %) :BEFORE[ZROWATE… |
| 92 | 2337 | 1329 | 20 | Profiler HUD | `HUDProfilerOverlay.Re…` | <color=#FF5555><b># ms</b></color> | <color=#FF5555><b>0.554 ms</b></color> |
| 93 | 2156 | 6 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL_SOLARPANELS] | ModuleManager: 23455 patches applied (49 %) :FOR[REALISMOVE… |
| 94 | 2146 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_FINALIZE] | ModuleManager: 56780 patches applied (96 %) :FOR[ZROWATERFA… |
| 95 | 1999 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[XXXRP#] | ModuleManager: 50849 patches applied (95 %) :AFTER[XXXRP0] |
| 96 | 1970 | 6 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZROWATERFALL_#_HYBRIDPLUMES] | ModuleManager: 55910 patches applied (96 %) :BEFORE[ZROWATE… |
| 97 | 1920 | 11 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL_REACTIONWHEEL] | ModuleManager: 23407 patches applied (49 %) :FOR[REALISMOVE… |
| 98 | 1898 | 8 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[RO-RCS] | ModuleManager: 29579 patches applied (65 %) :AFTER[RO-RCS] |
| 99 | 1864 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZZZZ-B#PARTSWITCH] | ModuleManager: 74841 patches applied (99 %) :FOR[ZZZZZZ-B9P… |
| 100 | 1855 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_LATEINITIALIZE] | ModuleManager: 52644 patches applied (95 %) :FOR[ZROWATERFA… |
| 101 | 1845 | 15 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAULTANKTYPES] | ModuleManager: 28413 patches applied (60 %) :FOR[REALISMOVE… |
| 102 | 1771 | 9 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL_MATERIALS_LATE] | ModuleManager: 21806 patches applied (49 %) :FOR[REALISMOVE… |
| 103 | 1745 | 16 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZROWATERFALL_POST] | ModuleManager: 57201 patches applied (96 %) :AFTER[ZROWATER… |
| 104 | 1716 | 45 | 3 | Profiler HUD | `ProfilerUI.DrawSpikeS…` | <color=#ff5555><b># ms</b></color> (#%) | <color=#ff5555><b>0.6 ms</b></color> (0.9%) |
| 105 | 1650 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_AUTOCONFIG] | ModuleManager: 52575 patches applied (95 %) :FOR[ZROWATERFA… |
| 106 | 1640 | 24 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZPOSTREALPLUMEROENGINES] | ModuleManager: 62113 patches applied (96 %) :FOR[ZZPOSTREAL… |
| 107 | 1637 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_HYBRIDPLUMES] | ModuleManager: 55962 patches applied (96 %) :FOR[ZROWATERFA… |
| 108 | 1635 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_GLOWPREPROCESS] | ModuleManager: 54874 patches applied (96 %) :FOR[ZROWATERFA… |
| 109 | 1626 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_INITIALIZE] | ModuleManager: 52203 patches applied (95 %) :FOR[ZROWATERFA… |
| 110 | 1554 | 12 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAUL_MATERIALS] | ModuleManager: 21573 patches applied (49 %) :AFTER[REALISMO… |
| 111 | 1552 | 37 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Post-physics callback (after yield): <b><color=#ffaa55># ms</color></b> (Pea… | 3. Post-physics callback (after yield): <b><color=#ffaa55>1… |
| 112 | 1544 | 5 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[RO-KERBALISMHARDDRIVES] | ModuleManager: 29258 patches applied (65 %) :BEFORE[RO-KERB… |
| 113 | 1504 | 8 | 14 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[PROCEDURALFAIRINGS] | ModuleManager: 8661 patches applied (14 %) :FOR[PROCEDURALF… |
| 114 | 1443 | 19 | 15 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[KERBALISM] | ModuleManager: 8441 patches applied (13 %) :FOR[KERBALISM] |
| 115 | 1416 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_GLOW] | ModuleManager: 55304 patches applied (96 %) :FOR[ZROWATERFA… |
| 116 | 1371 | 1308 | 12 | RDR | `BaseFlightWidget.SetT…` | RDR RDR # km | RDR RDR 1.04 km |
| 117 | 1368 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_VALIDATEREQUIREDKEYS] | ModuleManager: 53016 patches applied (95 %) :FOR[ZROWATERFA… |
| 118 | 1330 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[XXXRP#] | ModuleManager: 48413 patches applied (74 %) :BEFORE[XXXRP0] |
| 119 | 1262 | 3 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZROWATERFALL_#_AUDIO] | ModuleManager: 54132 patches applied (95 %) :BEFORE[ZROWATE… |
| 120 | 1255 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZREALPLUME] | ModuleManager: 51976 patches applied (95 %) :FOR[ZREALPLUME] |
| 121 | 1242 | 6 | 14 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[AJE] | ModuleManager: 3472 patches applied (12 %) :AFTER[AJE] |
| 122 | 1219 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[XXXRP-#] | ModuleManager: 51025 patches applied (95 %) :FOR[XXXRP-0] |
| 123 | 1124 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[REALANTENNAS] | ModuleManager: 77153 patches applied (100 %) :LAST[REALANTE… |
| 124 | 1099 | 17 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ROCAPSULES] | ModuleManager: 29171 patches applied (64 %) :AFTER[ROCAPSUL… |
| 125 | 1000 | 30 | 3 | Profiler HUD | `ProfilerUI.DrawHeader…` | <b>FPS:</b> <color=#FF3333><b>#</b></color> (#) | <b>FPS:</b> <color=#FF3333><b>22</b></color> (17) |
| 126 | 959 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[#_KSP-RECALL] | ModuleManager: 76551 patches applied (99 %) :LAST[999_KSP-R… |
| 127 | 890 | 38 | 3 | Profiler HUD | `ProfilerUI.DrawHeroKi…` | <b>##</b> [Plugin] <b>RLVAddon</b> (<color=#55FF88>#ms, #%</color>) | <b>#2</b> [Plugin] <b>RLVAddon</b> (<color=#55FF88>2.2ms, 4… |
| 128 | 888 | 12 | 15 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[KERBALISM] | ModuleManager: 8576 patches applied (14 %) :AFTER[KERBALISM] |
| 129 | 867 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZROWATERFALL_#_HYBRIDPLUMES] | ModuleManager: 55998 patches applied (96 %) :AFTER[ZROWATER… |
| 130 | 843 | 14 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[SXT] | ModuleManager: 48376 patches applied (74 %) :AFTER[SXT] |
| 131 | 796 | 3 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[RO-KERBALISMHARDDRIVES] | ModuleManager: 29538 patches applied (65 %) :AFTER[RO-KERBA… |
| 132 | 780 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL_SOLARPANELS_LATE] | ModuleManager: 23493 patches applied (49 %) :FOR[REALISMOVE… |
| 133 | 777 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZREALISMOVERHAUL_RAPATCH] | ModuleManager: 70667 patches applied (98 %) :FOR[ZZZREALISM… |
| 134 | 750 | 7 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAUL_HEATSHIELD] | ModuleManager: 19608 patches applied (49 %) :AFTER[REALISMO… |
| 135 | 741 | 11 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[RP-#-KERBALISMHARDDRIVES] | ModuleManager: 47798 patches applied (71 %) :AFTER[RP-0-KER… |
| 136 | 713 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ROSOLAR] | ModuleManager: 29661 patches applied (65 %) :FOR[ROSOLAR] |
| 137 | 711 | 423 | 20 | RDR | `BaseFlightWidget.SetT…` | RDR RDR # m | RDR RDR 0 m |
| 138 | 669 | 8 | 16 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALFUELS] | ModuleManager: 8918 patches applied (17 %) :AFTER[REALFUELS] |
| 139 | 664 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_EXTRATEMPLATES] | ModuleManager: 55519 patches applied (96 %) :FOR[ZROWATERFA… |
| 140 | 654 | 64 | 30 | LoadingScreen | `LoadingScreen.UpdateP…`, `TMP_Text.TMPro.TMP_Te…` | Loading sound asset #/# | Loading sound asset 0/234 |
| 141 | 649 | 4 | 13 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[KERBALISMDEFAULT] | ModuleManager: 8576 patches applied (14 %) :FOR[KERBALISMDE… |
| 142 | 582 | 6 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL_LIFESUPPORT] | ModuleManager: 19658 patches applied (49 %) :FOR[REALISMOVE… |
| 143 | 568 | 14 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Post-physics callback (after yield): <b><color=#ff5555># ms</color></b> (Pea… | 3. Post-physics callback (after yield): <b><color=#ff5555>2… |
| 144 | 558 | 18 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Precalculation (Precalc / Bodies): <b><color=#ffaa55># ms</color></b> (Peak:… | 1. Precalculation (Precalc / Bodies): <b><color=#ffaa55>1.5… |
| 145 | 512 | 6 | 13 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[KERBALISMDEFAULT] | ModuleManager: 8600 patches applied (14 %) :AFTER[KERBALISM… |
| 146 | 507 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[REALISMOVERHAUL_SOLARPANELS] | ModuleManager: 23417 patches applied (49 %) :BEFORE[REALISM… |
| 147 | 506 | 19 | 2 | Profiler HUD | `ProfilerUI.DrawHeroKi…` | <b>##</b> [Plugin] <b>TimingPre</b> (<color=#55FF88>#ms, #%</color>) | <b>#3</b> [Plugin] <b>TimingPre</b> (<color=#55FF88>2.0ms, … |
| 148 | 473 | 4 | 14 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[FERRAMAEROSPACERESEARCH] | ModuleManager: 4044 patches applied (12 %) :BEFORE[FERRAMAE… |
| 149 | 471 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[RSSEADRAGON] | ModuleManager: 47995 patches applied (71 %) :FOR[RSSEADRAGO… |
| 150 | 468 | 13 | 12 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[#_RESTOCK] | ModuleManager: 3411 patches applied (11 %) :AFTER[000_RESTO… |
| 151 | 451 | 2 | 14 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ENGINEGROUPCONTOLLER] | ModuleManager: 3695 patches applied (12 %) :FOR[ENGINEGROUP… |
| 152 | 436 | 13 | 1 | Profiler HUD | `ProfilerUI.DrawHeroKi…` | <b>##</b> [TUFX] <b>PostProcessLayer (TUFX)</b> (<color=#55FF88>#ms, #%</color>) | <b>#2</b> [TUFX] <b>PostProcessLayer (TUFX)</b> (<color=#55… |
| 153 | 434 | 9 | 13 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[KSPCOMMUNITYFIXES] | ModuleManager: 8617 patches applied (14 %) :FOR[KSPCOMMUNIT… |
| 154 | 426 | 8 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[RESTOCK] | ModuleManager: 29030 patches applied (64 %) :AFTER[RESTOCK] |
| 155 | 408 | 27 | 1 | Profiler HUD | `ProfilerUI.DrawHeader…` | <b>FPS:</b> <color=#FFFF33><b>#</b></color> (#) | <b>FPS:</b> <color=#FFFF33><b>35</b></color> (22) |
| 156 | 398 | 9 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[RESTOCKPLUS] | ModuleManager: 29160 patches applied (64 %) :AFTER[RESTOCKP… |
| 157 | 389 | 8 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZROWATERFALL_#_FINALIZE] | ModuleManager: 57190 patches applied (96 %) :AFTER[ZROWATER… |
| 158 | 388 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REMOTETECH-ANTENNAS] | ModuleManager: 29030 patches applied (64 %) :FOR[REMOTETECH… |
| 159 | 381 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAUL_SOLARPANELS_LATE] | ModuleManager: 23519 patches applied (49 %) :AFTER[REALISMO… |
| 160 | 371 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL_HEATSHIELD_LATE] | ModuleManager: 19622 patches applied (49 %) :FOR[REALISMOVE… |
| 161 | 357 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[RP-#-KERBALISM] | ModuleManager: 78999 patches applied (100 %) :LAST[RP-0-KER… |
| 162 | 355 | 3 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZRORESCALEDRAGCUBES] | ModuleManager: 52188 patches applied (95 %) :FOR[ZRORESCALE… |
| 163 | 354 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[RO-RCS] | ModuleManager: 29545 patches applied (65 %) :FOR[RO-RCS] |
| 164 | 349 | 2 | 15 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ATMOSPHEREAUTOPILOT] | ModuleManager: 3478 patches applied (12 %) :FOR[ATMOSPHEREA… |
| 165 | 338 | 8 | 1 | Profiler HUD | `PerformanceProbe.Draw…` | #. Precalculation (Precalc / Bodies): <b><color=#ff5555># ms</color></b> (Peak:… | 1. Precalculation (Precalc / Bodies): <b><color=#ff5555>12.… |
| 166 | 334 | 3 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZRP-#] | ModuleManager: 57220 patches applied (96 %) :FOR[ZRP-0] |
| 167 | 321 | 12 | 14 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[KERBALFOUNDRIES] | ModuleManager: 8401 patches applied (13 %) :FOR[KERBALFOUND… |
| 168 | 312 | 14 | 1 | Profiler HUD | `ProfilerUI.DrawHeroKi…` | <b>##</b> [Plugin] <b>EditorLogic</b> (<color=#55FF88>#ms, #%</color>) | <b>#3</b> [Plugin] <b>EditorLogic</b> (<color=#55FF88>0.7ms… |
| 169 | 278 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZZZ-RP#TREE] | ModuleManager: 74840 patches applied (99 %) :FOR[ZZZZZ-RP0T… |
| 170 | 266 | 4 | 3 | Profiler HUD | `ProfilerUI.DrawTimeli…` | <b><color=#00e5ff>TUFX:</color></b> # ms | <b><color=#00e5ff>TUFX:</color></b> 0.01 ms |
| 171 | 261 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAUL_SOLARPANELS] | ModuleManager: 23493 patches applied (49 %) :AFTER[REALISMO… |
| 172 | 260 | 37 | 18 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FIRST | ModuleManager: 134 patches applied (0 %) :FIRST |
| 173 | 249 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAULRC] | ModuleManager: 28413 patches applied (60 %) :FOR[REALISMOVE… |
| 174 | 248 | 7 | 11 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :FOR[AAA_AJE] | ModuleManager: 3435 patches applied (11 %) :FOR[AAA_AJE] |
| 175 | 242 | 6 | 1 | Profiler HUD | `PerformanceProbe.Draw…` | #. Markers & Geometry (LateUpdate / Markers): <b><color=#ff5555># ms</color></b… | 5. Markers & Geometry (LateUpdate / Markers): <b><color=#ff… |
| 176 | 240 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[KOP_SCATTER_MASK] | ModuleManager: 77150 patches applied (100 %) :LAST[KOP_SCAT… |
| 177 | 225 | 5 | 10 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[A_ROTANKS] | ModuleManager: 3426 patches applied (11 %) :AFTER[A_ROTANKS] |
| 178 | 217 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[REALISMOVERHAULENGINES] | ModuleManager: 78936 patches applied (100 %) :LAST[REALISMO… |
| 179 | 197 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[VABORGANIZER] | ModuleManager: 48407 patches applied (74 %) :AFTER[VABORGAN… |
| 180 | 196 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZZREALPLUME] | ModuleManager: 62140 patches applied (96 %) :BEFORE[ZZREALP… |
| 181 | 195 | 5 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ROHEATSHIELDS] | ModuleManager: 29199 patches applied (64 %) :FOR[ROHEATSHIE… |
| 182 | 168 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ROSOLAR] | ModuleManager: 29658 patches applied (65 %) :BEFORE[ROSOLAR] |
| 183 | 168 | 5 | 3 | Profiler HUD | `ProfilerUI.DrawTimeli…` | <color=#888888>#ms</color> | <color=#888888>149ms</color> |
| 184 | 167 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[SOLSYSTEM] | ModuleManager: 79014 patches applied (100 %) :LAST[SOLSYSTE… |
| 185 | 167 | 4 | 13 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALANTENNAS] | ModuleManager: 8764 patches applied (15 %) :AFTER[REALANTEN… |
| 186 | 163 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZZREALPLUME] | ModuleManager: 62159 patches applied (97 %) :AFTER[ZZREALPL… |
| 187 | 148 | 2 | 13 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALCHUTE] | ModuleManager: 8770 patches applied (15 %) :AFTER[REALCHUTE] |
| 188 | 143 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZAFTERUNIVERSALSTORAGE] | ModuleManager: 62159 patches applied (97 %) :FOR[ZZZAFTERUN… |
| 189 | 140 | 3 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZZZROENGINES] | ModuleManager: 74838 patches applied (99 %) :FOR[ZZZZZROENG… |
| 190 | 137 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAULTANKTYPES] | ModuleManager: 28527 patches applied (61 %) :AFTER[REALISMO… |
| 191 | 136 | 4 | 1 | Profiler HUD | `PerformanceProbe.Draw…` | #. Late Sync (BetterLate / Motion): <b><color=#ff5555># ms</color></b> (Peak: #… | 4. Late Sync (BetterLate / Motion): <b><color=#ff5555>5.965… |
| 192 | 129 | 2 | 16 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ROCAPSULES] | ModuleManager: 29168 patches applied (64 %) :FOR[ROCAPSULES] |
| 193 | 122 | 2 | 7 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :FOR[AAA] | ModuleManager: 3432 patches applied (11 %) :FOR[AAA] |
| 194 | 117 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ROENGINES] | ModuleManager: 29197 patches applied (64 %) :AFTER[ROENGINE… |
| 195 | 108 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[REALISMOVERHAUL_HEATSHIELD] | ModuleManager: 19418 patches applied (49 %) :BEFORE[REALISM… |
| 196 | 108 | 4 | 11 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[KERBALISM] | ModuleManager: 8419 patches applied (13 %) :BEFORE[KERBALIS… |
| 197 | 104 | 3 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[ROCAPSULES] | ModuleManager: 78936 patches applied (100 %) :LAST[ROCAPSUL… |
| 198 | 97 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZZZRP-#] | ModuleManager: 70667 patches applied (98 %) :BEFORE[ZZZRP-0] |
| 199 | 96 | 2 | 9 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[B#_AEROSPACE_WINGSTUFF] | ModuleManager: 3689 patches applied (12 %) :FOR[B9_AEROSPAC… |
| 200 | 95 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[RO-KERBALISM] | ModuleManager: 29256 patches applied (65 %) :AFTER[RO-KERBA… |
| 201 | 94 | 3 | 10 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[PROCEDURALPARTS] | ModuleManager: 8684 patches applied (14 %) :FOR[PROCEDURALP… |
| 202 | 91 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[RP-#-KERBALISMHARDDRIVES] | ModuleManager: 47796 patches applied (71 %) :FOR[RP-0-KERBA… |
| 203 | 88 | 2 | 9 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :FOR[A_ROTANKS] | ModuleManager: 3424 patches applied (11 %) :FOR[A_ROTANKS] |
| 204 | 87 | 1 | 18 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :INSERT (initial) | ModuleManager: 0 patches applied (0 %) :INSERT (initial) |
| 205 | 86 | 2 | 16 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[VABORGANIZER] | ModuleManager: 48404 patches applied (74 %) :FOR[VABORGANIZ… |
| 206 | 82 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[VENSTOCKREVAMP] | ModuleManager: 48412 patches applied (74 %) :FOR[VENSTOCKRE… |
| 207 | 79 | 1 | 3 | Profiler HUD | `ProfilerUI.DrawTimeli…` | <color=#33ff55>#ms (#F)</color> | <color=#33ff55>16.7ms (60F)</color> |
| 208 | 79 | 1 | 3 | Profiler HUD | `ProfilerUI.DrawTimeli…` | <color=#ff5555>#ms (#F)</color> | <color=#ff5555>50.0ms (20F)</color> |
| 209 | 79 | 2 | 16 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ROENGINES] | ModuleManager: 29190 patches applied (64 %) :FOR[ROENGINES] |
| 210 | 79 | 1 | 3 | Profiler HUD | `ProfilerUI.DrawTimeli…` | <color=#ffaa22>#ms (#F)</color> | <color=#ffaa22>33.3ms (30F)</color> |
| 211 | 78 | 2 | 14 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[Z_SOLSYSTEM] | ModuleManager: 51027 patches applied (95 %) :FOR[Z_SOLSYSTE… |
| 212 | 70 | 2 | 7 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :BEFORE[AAA_AJE] | ModuleManager: 3433 patches applied (11 %) :BEFORE[AAA_AJE] |
| 213 | 64 | 2 | 16 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZREALFUELSVOLUMES] | ModuleManager: 51702 patches applied (95 %) :FOR[ZREALFUELS… |
| 214 | 62 | 2 | 16 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ROSOLAR] | ModuleManager: 29665 patches applied (65 %) :AFTER[ROSOLAR] |
| 215 | 59 | 35 | 5 | RDR | `BaseFlightWidget.SetT…` | RDR RDR # Mm | RDR RDR 1.33 Mm |
| 216 | 57 | 2 | 15 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[PROCEDURALFAIRINGS] | ModuleManager: 77151 patches applied (100 %) :LAST[PROCEDUR… |
| 217 | 54 | 2 | 15 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZZZRP-#] | ModuleManager: 71146 patches applied (98 %) :AFTER[ZZZRP-0] |
| 218 | 49 | 2 | 13 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[RO-KERBALISM] | ModuleManager: 29255 patches applied (65 %) :FOR[RO-KERBALI… |
| 219 | 46 | 2 | 1 | Profiler HUD | `ProfilerUI.DrawHeroKi…` | <b>##</b> [Plugin] <b>RLVAddon</b> (<color=#FFAA22>#ms, #%</color>) | <b>#2</b> [Plugin] <b>RLVAddon</b> (<color=#FFAA22>4.7ms, 1… |
| 220 | 45 | 2 | 16 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[ZKOPERNICUS] | ModuleManager: 79023 patches applied (100 %) :LAST[ZKOPERNI… |
| 221 | 44 | 1 | 1 | Profiler HUD | `PerformanceProbe.Draw…` | #. Markers & Geometry (LateUpdate / Markers): <b><color=#ffaa55># ms</color></b… | 5. Markers & Geometry (LateUpdate / Markers): <b><color=#ff… |
| 222 | 43 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[RP-#] | ModuleManager: 78939 patches applied (100 %) :LAST[RP-0] |
| 223 | 43 | 2 | 16 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[WATERFALL] | ModuleManager: 48412 patches applied (74 %) :AFTER[WATERFAL… |
| 224 | 41 | 2 | 10 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[PARALLAXSTOCK] | ModuleManager: 8653 patches applied (14 %) :FOR[PARALLAXSTO… |
| 225 | 40 | 2 | 20 | Profiler HUD | `HUDProfilerOverlay.Cr…` | #ms | 0.00ms |
| 226 | 39 | 37 | 14 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) | ModuleManager: 12728 patches applied (35 %) |
| 227 | 38 | 2 | 9 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[PROCEDURALPARTS] | ModuleManager: 8692 patches applied (14 %) :AFTER[PROCEDURA… |
| 228 | 37 | 2 | 14 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[REALFUELS] | ModuleManager: 77253 patches applied (100 %) :LAST[REALFUEL… |
| 229 | 36 | 1 | 2 | Profiler HUD | `PerformanceProbe.Draw…` | Principia Net: <color=#888888># ms (Offline)</color> | Principia Net: <color=#888888>0.00 ms (Offline)</color> |
| 230 | 27 | 2 | 9 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[KSPCOMMUNITYFIXES] | ModuleManager: 8643 patches applied (14 %) :AFTER[KSPCOMMUN… |
| 231 | 22 | 1 | 11 | LoadingScreen | `LoadingScreen.UpdateP…` | Kopernicus: Loading RealisticLaunchVisuals\Native\Win#\RLVGpuTiming.bin | Kopernicus: Loading RealisticLaunchVisuals\Native\Win64\RLV… |
| 232 | 20 | 1 | 20 | Profiler HUD | `WorkbenchCanvasView.C…` | # FPS \| #ms | 60 FPS \| 0.1ms |
| 233 | 18 | 1 | 13 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZZZZZZZZZZZZZZZZZZZZZZZRP#] | ModuleManager: 76551 patches applied (99 %) :AFTER[ZZZZZZZZ… |
| 234 | 16 | 1 | 14 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patch applied (# %) :FIRST | ModuleManager: 1 patch applied (0 %) :FIRST |
| 235 | 14 | 2 | 7 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[KOPERNICUS] | ModuleManager: 8614 patches applied (14 %) :AFTER[KOPERNICU… |
| 236 | 13 | 2 | 9 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_POST] | ModuleManager: 57200 patches applied (96 %) :FOR[ZROWATERFA… |
| 237 | 13 | 2 | 10 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[RP#] | ModuleManager: 31507 patches applied (68 %) :FOR[RP0] |
| 238 | 6 | 2 | 5 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[PARALLAX] | ModuleManager: 8648 patches applied (14 %) :FOR[PARALLAX] |
| 239 | 5 | 2 | 5 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[BURSTPQS] | ModuleManager: 76551 patches applied (99 %) :LAST[BURSTPQS] |
| 240 | 4 | 2 | 4 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :FOR[HARDREMOVENONRO] | ModuleManager: 8399 patches applied (13 %) :FOR[HARDREMOVEN… |
| 241 | 4 | 2 | 4 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[HARDREMOVENONRP#] | ModuleManager: 8400 patches applied (13 %) :FOR[HARDREMOVEN… |
| 242 | 3 | 2 | 3 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[KOPERNICUS] | ModuleManager: 8613 patches applied (14 %) :FOR[KOPERNICUS] |
| 243 | 3 | 2 | 3 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[PARALLAXCONTINUED] | ModuleManager: 8650 patches applied (14 %) :FOR[PARALLAXCON… |
| 244 | 3 | 1 | 3 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :FOR[KOPERNICUS_VERSION] | ModuleManager: 8616 patches applied (14 %) :FOR[KOPERNICUS_… |
| 245 | 2 | 2 | 2 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :FOR[NONONRO] | ModuleManager: 8646 patches applied (14 %) :FOR[NONONRO] |
| 246 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZZZAFTERUNIVERSALSTORAGE] | ModuleManager: 62159 patches applied (97 %) :AFTER[ZZZAFTER… |
| 247 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZROWATERFALL_#_AUTOCONFIG] | ModuleManager: 52644 patches applied (95 %) :AFTER[ZROWATER… |
| 248 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :FOR[NONONRP#] | ModuleManager: 8647 patches applied (14 %) :FOR[NONONRP0] |
| 249 | 1 | 1 | 1 | ModuleManager | `TMP_Text.TMPro.TMP_Te…` | ModuleManager: # patches applied (# %) :AFTER[ZZZZZ-RP#TREE] | ModuleManager: 74842 patches applied (99 %) :AFTER[ZZZZZ-RP… |
| 250 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZROWATERFALL_#_MAINEFFECT] | ModuleManager: 54132 patches applied (95 %) :AFTER[ZROWATER… |
| 251 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[PARALLAX_STOCKPLANETTEXTURES] | ModuleManager: 8650 patches applied (14 %) :AFTER[PARALLAX_… |
| 252 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAUL_MATERIALS_LATE] | ModuleManager: 23297 patches applied (49 %) :AFTER[REALISMO… |
| 253 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :FOR[TEXTURESUNLIMITED] | ModuleManager: 48404 patches applied (74 %) :FOR[TEXTURESUN… |
| 254 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[_BUILDMANAGER] | ModuleManager: 3161 patches applied (10 %) :AFTER[_BUILDMAN… |
| 255 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[UNITY.BURST] | ModuleManager: 48404 patches applied (74 %) :AFTER[UNITY.BU… |
| 256 | 1 | 1 | 1 | ModuleManager | `TMP_Text.TMPro.TMP_Te…` | ModuleManager: # patches applied (# %) :AFTER[SOL-VISUALS] | ModuleManager: 48377 patches applied (74 %) :AFTER[SOL-VISU… |
| 257 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZRORESCALEDRAGCUBES] | ModuleManager: 52203 patches applied (95 %) :AFTER[ZRORESCA… |
| 258 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALHEAT] | ModuleManager: 8939 patches applied (17 %) :AFTER[REALHEAT] |
| 259 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[PROFILEREALISMOVERHAUL] | ModuleManager: 8693 patches applied (14 %) :AFTER[PROFILERE… |
| 260 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :BEFORE[REALISMOVERHAUL_REACTIONWHEEL] | ModuleManager: 23297 patches applied (49 %) :BEFORE[REALISM… |
| 261 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :FOR[#_MULTIPLEMODULEINPARTAPI] | ModuleManager: 3161 patches applied (10 %) :FOR[0_MULTIPLEM… |
| 262 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[AAA_AJE] | ModuleManager: 3443 patches applied (12 %) :AFTER[AAA_AJE] |

## 3. 全部骨架（按 count 降序，共 474 个）

其余（既不到 2 个英文词、也不姓噪声类的 80 个）多为 `#` 加单位/符号的碎片，按需人工扫一眼即可。



| # | count | 实例 | sessions | 噪声类 | callers | 骨架 | 样本 |
|---:|---:|---:|---:|---|---|---|---|
| 1 | 638638 | 5186 | 20 | - | `MechJebStageStatsHelp…`, `PrincipiaPluginAdapte…` | # m/s | 0 m/s |
| 2 | 338852 | 2509 | 15 | - | `LunarTransferPlanner.…`, `MechJebStageStatsHelp…` | #h #m #s | 4h 29m 54.3s |
| 3 | 315909 | 1074 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL] | ModuleManager: 12728 patches applied (37 %) :FOR[REALISMOVE… |
| 4 | 233320 | 1 | 1 | - | `ConfigurationGUI.rend…` | bluedog_Titan#_SecondStageTank (Instance) [KSP/Bumped Specular] | bluedog_Titan2_SecondStageTank (Instance) [KSP/Bumped Specu… |
| 5 | 227180 | 1 | 1 | - | `ConfigurationGUI.rend…` | bluedog_Centaur_RL# (Instance) [KSP/Emissive/Bumped Specular] | bluedog_Centaur_RL10 (Instance) [KSP/Emissive/Bumped Specul… |
| 6 | 183022 | 2349 | 20 | - | `ValueInfoItem.DrawItem` | #, # m/s | 0, 0 m/s |
| 7 | 169598 | 1298 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[XXXRP#] | ModuleManager: 50726 patches applied (92 %) :FOR[XXXRP0] |
| 8 | 131900 | 1411 | 14 | - | `MechJebModuleAscentPV…` | # # m/s | 2 2,016 m/s |
| 9 | 130676 | 369 | 14 | - | `MechJebStageStatsHelp…` | #m #s | 6m 54.5s |
| 10 | 116660 | 2 | 1 | - | `ConfigurationGUI.rend…` | model_vab_exterior_tile_# (Instance) [KSP/Scenery/Specular] | model_vab_exterior_tile_00 (Instance) [KSP/Scenery/Specular] |
| 11 | 113958 | 428 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAULENGINES] | ModuleManager: 24258 patches applied (51 %) :FOR[REALISMOVE… |
| 12 | 103100 | 1339 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAULENGINES] | ModuleManager: 25090 patches applied (52 %) :AFTER[REALISMO… |
| 13 | 98450 | 7 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[REALISMOVERHAULENGINESPOST] | ModuleManager: 27195 patches applied (60 %) :BEFORE[REALISM… |
| 14 | 95170 | 1 | 1 | - | `ConfigurationGUI.rend…` | model_vab_exterior_lines_#_decal (Instance) [KSP/Scenery/Diffuse] | model_vab_exterior_lines_01_decal (Instance) [KSP/Scenery/D… |
| 15 | 95170 | 1 | 1 | - | `ConfigurationGUI.rend…` | model_vab_exterior_props_# (Instance) [KSP/Scenery/Specular] | model_vab_exterior_props_01 (Instance) [KSP/Scenery/Specula… |
| 16 | 86673 | 201 | 20 | Profiler HUD | `HUDProfilerOverlay.Re…` | <color=#00E5FF><b># ms</b></color> | <color=#00E5FF><b>0.001 ms</b></color> |
| 17 | 79924 | 11 | 20 | - | `MechJebModuleAscentMe…` | n: #(#) | n: 0(0) |
| 18 | 78640 | 8 | 20 | - | `MechJebModuleAscentMe…` | heading: # | heading: 0.0 |
| 19 | 76750 | 1 | 1 | - | `ConfigurationGUI.rend…` | model_vab_exterior_details_# (Instance) [KSP/Scenery/Emissive/Specular] | model_vab_exterior_details_01 (Instance) [KSP/Scenery/Emiss… |
| 20 | 76703 | 101 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[RP-#-KERBALISM] | ModuleManager: 46425 patches applied (71 %) :AFTER[RP-0-KER… |
| 21 | 74562 | 301 | 14 | - | `MechJebStageStatsHelp…` | #s | 00.0s |
| 22 | 72370 | 1157 | 20 | - | `MechJebModuleAscentMe…` | tgo: # | tgo: 0.000 |
| 23 | 72298 | 1074 | 20 | - | `MechJebModuleAscentMe…` | vgo: # | vgo: 0.0 |
| 24 | 72074 | 175 | 20 | - | `MechJebModuleAscentMe…` | pitch: # | pitch: 0.0 |
| 25 | 66790 | 2132 | 20 | Profiler HUD | `HUDProfilerOverlay.Cr…`, `ProfilerUI.DrawPlugin…` | # ms | 0.00 ms |
| 26 | 63124 | 16 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZTESTFLIGHT] | ModuleManager: 59543 patches applied (96 %) :FOR[ZTESTFLIGH… |
| 27 | 55562 | 1 | 20 | - | `MechJebModuleAscentMe…` | znorm: # | znorm: 0 |
| 28 | 55384 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZZZZZZZZZZZZZZZZZZZZZRP#] | ModuleManager: 75264 patches applied (99 %) :FOR[ZZZZZZZZZZ… |
| 29 | 53016 | 1 | 23 | - | `MainWindow.RenderWind…`, `principia.ksp_plugin_…` | #-Lévy-#-gf#c#a#b#f#-x#_AVX_FMA | 2026091103-Lévy-162-gf1c108102414a84b1f73-x64_AVX_FMA |
| 30 | 52190 | 1 | 1 | - | `ConfigurationGUI.rend…` | model_vab_exterior_tile_#_roof (Instance) [KSP/Scenery/Specular] | model_vab_exterior_tile_04_roof (Instance) [KSP/Scenery/Spe… |
| 31 | 47678 | 2 | 20 | - | `RLVDebugGUI.DrawTelem…` | Aero: Stock \| RH preset: # % | Aero: Stock \| RH preset: 0 % |
| 32 | 45357 | 68 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZZZTAGCLEANUP] | ModuleManager: 71560 patches applied (99 %) :BEFORE[ZZZTAGC… |
| 33 | 43930 | 62 | 14 | - | `RLVDebugGUI.DrawTelem…` | Aero: FAR \| RH preset: # % | Aero: FAR \| RH preset: 71 % |
| 34 | 42582 | 102 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[RP-#-KERBALISM] | ModuleManager: 39252 patches applied (71 %) :FOR[RP-0-KERBA… |
| 35 | 42023 | 49 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZREALPLUME] | ModuleManager: 52141 patches applied (95 %) :AFTER[ZREALPLU… |
| 36 | 40758 | 1 | 12 | ModuleManager | `unknown` | ModuleManager: # patches loaded from cache | ModuleManager: 81181 patches loaded from cache |
| 37 | 40651 | 80 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[KERBALISM] | ModuleManager: 76746 patches applied (99 %) :LAST[KERBALISM] |
| 38 | 39446 | 1 | 3 | - | `GUI.UnityEngine.GUI.D…`, `unknown` | Real pack samples #-# / control #-# | Real pack samples 01-20 / control 21-25 |
| 39 | 38305 | 76 | 26 | - | `EditorLaunchPadItem.C…`, `KSP.UI.MessageSystemA…` | #autoLOC_# | #autoLOC_6003004 |
| 40 | 37748 | 147 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[RP-#] | ModuleManager: 35728 patches applied (69 %) :FOR[RP-0] |
| 41 | 35278 | 2723 | 10 | - | `MechJebModuleAscentMe…` | burn: # #s #m/s (#m/s) | burn: 2 163.3s 692.0m/s (1324.4m/s) |
| 42 | 34828 | 1 | 9 | - | `ConfigurationGUI.rend…` | Tracking: <color=#88FF88># m</color> | Tracking: <color=#88FF88>10.0 m</color> |
| 43 | 34717 | 96 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[REALPLUME] | ModuleManager: 28530 patches applied (62 %) :BEFORE[REALPLU… |
| 44 | 33770 | 1 | 1 | - | `ConfigurationGUI.rend…` | model_vab_exterior_lines_# (Instance) [KSP/Specular] | model_vab_exterior_lines_01 (Instance) [KSP/Specular] |
| 45 | 33158 | 696 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | <b>FPS:</b> # \| <b>Frame Time:</b> # ms \| <b>Jitter (p#−p#):</b> # ms \| <b>Heap… | <b>FPS:</b> 42.5 \| <b>Frame Time:</b> 23.50 ms \| <b>Jitter … |
| 46 | 32942 | 690 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Late Sync (BetterLate / Motion): <b><color=#55ff55># ms</color></b> (Peak: #… | 4. Late Sync (BetterLate / Motion): <b><color=#55ff55>0.303… |
| 47 | 32890 | 678 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Adapter FixedUpdate: <b><color=#55ff55># ms</color></b> (Peak: # ms / Budget… | 2. Adapter FixedUpdate: <b><color=#55ff55>0.043 ms</color><… |
| 48 | 32778 | 684 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Markers & Geometry (LateUpdate / Markers): <b><color=#55ff55># ms</color></b… | 5. Markers & Geometry (LateUpdate / Markers): <b><color=#55… |
| 49 | 32473 | 113 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAUL] | ModuleManager: 14337 patches applied (48 %) :AFTER[REALISMO… |
| 50 | 32202 | 669 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Precalculation (Precalc / Bodies): <b><color=#55ff55># ms</color></b> (Peak:… | 1. Precalculation (Precalc / Bodies): <b><color=#55ff55>1.1… |
| 51 | 32148 | 4509 | 20 | - | `HUDProfilerOverlay.Re…` | # MB (#/#/#) | 0.0 MB (0/0/0) |
| 52 | 31770 | 569 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | • <b>Frame minus stage sum:</b> <b># ms</b> [Unattributed estimate; not measure… | • <b>Frame minus stage sum:</b> <b>6.81 ms</b> [Unattribute… |
| 53 | 31380 | 9 | 1 | - | `RealAntennas.RealAnte…` | Net: # nodes. # links. # occluders. | Net: 82 nodes. 33 links. 51 occluders. |
| 54 | 31089 | 40 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZREALISMOVERHAUL] | ModuleManager: 65190 patches applied (98 %) :FOR[ZZZREALISM… |
| 55 | 30958 | 643 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Post-physics callback (after yield): <b><color=#55ff55># ms</color></b> (Pea… | 3. Post-physics callback (after yield): <b><color=#55ff55>0… |
| 56 | 30339 | 109 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZKERBALISM] | ModuleManager: 62931 patches applied (97 %) :FOR[ZZZKERBALI… |
| 57 | 28096 | 222 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ROWATERFALL] | ModuleManager: 30348 patches applied (66 %) :BEFORE[ROWATER… |
| 58 | 27670 | 1 | 10 | - | `MechJebModuleMenu.Dra…` | MechJeb #.# | MechJeb 2.15.3 |
| 59 | 27630 | 1 | 1 | - | `ConfigurationGUI.rend…` | model_tracking_exterior_# (Instance) [KSP/Scenery/Specular] | model_tracking_exterior_01 (Instance) [KSP/Scenery/Specular] |
| 60 | 27010 | 10 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZTESTFLIGHT] | ModuleManager: 57222 patches applied (96 %) :BEFORE[ZTESTFL… |
| 61 | 25830 | 179 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | Measured stage sum (incomplete): <b># ms</b> Smoothed per-call estimates; overl… | Measured stage sum (incomplete): <b>2.74 ms</b> Smoothed pe… |
| 62 | 25686 | 175 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | • <b>Stage sum minus native scopes:</b> <b># ms</b> [Estimate; not measured C# … | • <b>Stage sum minus native scopes:</b> <b>2.59 ms</b> [Est… |
| 63 | 24272 | 36 | 20 | - | `BaseFlightWidget.SetT…`, `MechJebStageStatsHelp…` | # M | 0.0 M |
| 64 | 24205 | 6 | 3 | - | `MechJebStageStatsHelp…`, `UIPartActionFloatEdit…` | # m | 0.000 m |
| 65 | 23850 | 79 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | • <b>Native call scopes:</b> <b># ms</b> [Physics: #ms \| Plotting: #ms; include… | • <b>Native call scopes:</b> <b>0.21 ms</b> [Physics: 0.19m… |
| 66 | 23490 | 3 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Celestial Trajectory (Celestial Subtree): <b><color=#55ff55># ms</color></b>… | 7. Celestial Trajectory (Celestial Subtree): <b><color=#55f… |
| 67 | 23490 | 3 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Vessel Trajectory (Vessel Trajectory): <b><color=#55ff55># ms</color></b> (P… | 6. Vessel Trajectory (Vessel Trajectory): <b><color=#55ff55… |
| 68 | 21992 | 1 | 14 | - | `ConfigurationGUI.rend…`, `TUFX.ConfigurationGUI…` | Cinematic-NextGen-CMAA# | Cinematic-NextGen-CMAA2 |
| 69 | 21986 | 1 | 14 | - | `ConfigurationGUI.rend…`, `TUFX.ConfigurationGUI…` | Vant-# | Vant-1960 |
| 70 | 21646 | 243 | 18 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied | ModuleManager: 81181 patches applied |
| 71 | 21520 | 130 | 10 | - | `MechJebModuleAscentMe…` | znorm: #E-# | znorm: 5.0638E-10 |
| 72 | 21490 | 1 | 1 | - | `ConfigurationGUI.rend…` | model_vab_exterior_lines_#_decal (Instance) (Instance) [KSP/Scenery/Diffuse] | model_vab_exterior_lines_01_decal (Instance) (Instance) [KS… |
| 73 | 18420 | 1 | 1 | - | `ConfigurationGUI.rend…` | ksc_exterior_terrain_tile_#_diffuse (Instance) (Instance) [KSP/Scenery/Diffuse] | ksc_exterior_terrain_tile_01_diffuse (Instance) (Instance) … |
| 74 | 17230 | 112 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[REALISMOVERHAUL] | ModuleManager: 11548 patches applied (18 %) :BEFORE[REALISM… |
| 75 | 17224 | 73 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALFUELS] | ModuleManager: 8770 patches applied (15 %) :FOR[REALFUELS] |
| 76 | 15680 | 11 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[KOP_MULTI_STAR] | ModuleManager: 77073 patches applied (99 %) :LAST[KOP_MULTI… |
| 77 | 15618 | 60 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZZZKERBALISM] | ModuleManager: 63202 patches applied (97 %) :AFTER[ZZZKERBA… |
| 78 | 14855 | 580 | 20 | Profiler HUD | `HUDProfilerOverlay.Re…` | <color=#FFE000><b># ms</b></color> | <color=#FFE000><b>0.227 ms</b></color> |
| 79 | 13737 | 12 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZREALFUELS] | ModuleManager: 63933 patches applied (98 %) :FOR[ZZZREALFUE… |
| 80 | 13511 | 18 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALPLUME] | ModuleManager: 28729 patches applied (64 %) :AFTER[REALPLUM… |
| 81 | 13017 | 28 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZZZKERBALISM] | ModuleManager: 62482 patches applied (97 %) :BEFORE[ZZZKERB… |
| 82 | 12954 | 952 | 10 | - | `MechJebModuleAscentMe…` | burn: # #s #m/s (-#m/s) | burn: 4 112.5s 4834.2m/s (-4834.2m/s) |
| 83 | 12518 | 22 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FINAL | ModuleManager: 79323 patches applied (100 %) :FINAL |
| 84 | 12280 | 1 | 1 | - | `ConfigurationGUI.rend…` | ends.# (Instance) (Instance) [TU/Metallic] | ends.001 (Instance) (Instance) [TU/Metallic] |
| 85 | 12280 | 4 | 1 | - | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_runwaySec#Lvl#_UltraQualityGrass [KSP/Scenery/Diffus… | ksc_exterior_terrain_grass_runwaySec01Lvl03_UltraQualityGra… |
| 86 | 12280 | 1 | 1 | - | `ConfigurationGUI.rend…` | sides.# (Instance) (Instance) [TU/Metallic] | sides.001 (Instance) (Instance) [TU/Metallic] |
| 87 | 12280 | 1 | 1 | - | `ConfigurationGUI.rend…` | restock-rcs-blocks-# (Instance) (Instance) [KSP/Bumped Specular] | restock-rcs-blocks-2 (Instance) (Instance) [KSP/Bumped Spec… |
| 88 | 12280 | 1 | 1 | - | `ConfigurationGUI.rend…` | bluedog_TitanSolids_Misc # (Instance) [KSP/Emissive/Bumped Specular] | bluedog_TitanSolids_Misc 1 (Instance) [KSP/Emissive/Bumped … |
| 89 | 12280 | 1 | 1 | - | `ConfigurationGUI.rend…` | Material ## (Instance) (Instance) [DepthMask] | Material #326 (Instance) (Instance) [DepthMask] |
| 90 | 12280 | 1 | 1 | - | `ConfigurationGUI.rend…` | restock-decouplers-# (Instance) [KSP/Bumped Specular] | restock-decouplers-2 (Instance) [KSP/Bumped Specular] |
| 91 | 11970 | 38 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[RP-#] | ModuleManager: 47810 patches applied (71 %) :AFTER[RP-1] |
| 92 | 11722 | 39 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_AUDIO] | ModuleManager: 54842 patches applied (96 %) :FOR[ZROWATERFA… |
| 93 | 9422 | 10 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAULENGINESPOST] | ModuleManager: 28405 patches applied (60 %) :AFTER[REALISMO… |
| 94 | 9210 | 1 | 1 | - | `ConfigurationGUI.rend…` | phong# (Instance) [Legacy Shaders/Diffuse] | phong1 (Instance) [Legacy Shaders/Diffuse] |
| 95 | 9210 | 1 | 1 | - | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_# (Instance) [KSP/Scenery/Diffuse Ground KSC] | ksc_exterior_terrain_grass_02 (Instance) [KSP/Scenery/Diffu… |
| 96 | 9032 | 3059 | 30 | LoadingScreen | `LoadingScreen.UpdateP…`, `TMP_Text.TMPro.TMP_Te…` | Loading texture asset #/# (buffer=#MB) | Loading texture asset 2223/5736 (buffer=34MB) |
| 97 | 8591 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZELECTRICITYUNITS] | ModuleManager: 62159 patches applied (97 %) :FOR[ZZZELECTRI… |
| 98 | 8422 | 37 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL_MATERIALS] | ModuleManager: 20913 patches applied (49 %) :FOR[REALISMOVE… |
| 99 | 8413 | 39 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[SOLSYSTEM] | ModuleManager: 48376 patches applied (73 %) :AFTER[SOLSYSTE… |
| 100 | 6974 | 19 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZZZREALISMOVERHAUL] | ModuleManager: 70651 patches applied (98 %) :AFTER[ZZZREALI… |
| 101 | 6856 | 14 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZTAGCLEANUP] | ModuleManager: 71756 patches applied (99 %) :FOR[ZZZTAGCLEA… |
| 102 | 6822 | 117 | 20 | Profiler HUD | `HUDProfilerOverlay.Cr…`, `HUDProfilerOverlay.Re…` | FPS: # | FPS: 20 |
| 103 | 6792 | 17 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[REALISMOVERHAUL] | ModuleManager: 77687 patches applied (100 %) :LAST[REALISMO… |
| 104 | 6687 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZREALPLUME] | ModuleManager: 51974 patches applied (95 %) :BEFORE[ZREALPL… |
| 105 | 6660 | 13 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAULENGINESPOST] | ModuleManager: 28105 patches applied (60 %) :FOR[REALISMOVE… |
| 106 | 6481 | 63 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[SOLSYSTEM] | ModuleManager: 48069 patches applied (71 %) :FOR[SOLSYSTEM] |
| 107 | 6478 | 1 | 3 | - | `ConfigurationGUI.rend…` | <size=10><color=grey>Non-temporal morphological filters (CMAA # / SMAA) produce… | <size=10><color=grey>Non-temporal morphological filters (CM… |
| 108 | 6478 | 1 | 3 | - | `ConfigurationGUI.rend…` | Intel CMAA # (No Ghosting) | Intel CMAA 2 (No Ghosting) |
| 109 | 6311 | 35 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[REALISMOVERHAULENGINES] | ModuleManager: 23542 patches applied (50 %) :BEFORE[REALISM… |
| 110 | 6140 | 1 | 1 | - | `ConfigurationGUI.rend…` | ksc_exterior_terrain_tile_#_diffuse (Instance) [KSP/Scenery/Diffuse] | ksc_exterior_terrain_tile_01_diffuse (Instance) [KSP/Scener… |
| 111 | 6140 | 2 | 1 | - | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_runwayEnd#Lvl#_UltraQualityGrass [KSP/Scenery/Diffus… | ksc_exterior_terrain_grass_runwayEnd09Lvl03_UltraQualityGra… |
| 112 | 6140 | 1 | 1 | - | `ConfigurationGUI.rend…` | model_vab_exterior_tile_# (Instance) [KSP/Scenery/Emissive/Specular] | model_vab_exterior_tile_02 (Instance) [KSP/Scenery/Emissive… |
| 113 | 6140 | 2 | 1 | - | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_fillerx#Lvl#_ultraQualityGrass [KSP/Scenery/Diffuse … | ksc_exterior_terrain_grass_fillerx1Lvl03_ultraQualityGrass … |
| 114 | 6140 | 1 | 1 | - | `ConfigurationGUI.rend…` | fairing# (Instance) (Instance) [KSP/Bumped Specular] | fairing1 (Instance) (Instance) [KSP/Bumped Specular] |
| 115 | 6140 | 1 | 1 | - | `ConfigurationGUI.rend…` | model_vab_exterior_tile_#_glass (Instance) [KSP/Scenery/Emissive/Specular] | model_vab_exterior_tile_05_glass (Instance) [KSP/Scenery/Em… |
| 116 | 6096 | 4561 | 3 | - | `NavBallBurnVector.Upd…` | #m/s | 4.2m/s |
| 117 | 6020 | 1 | 1 | - | `FerramAerospaceResear…` | Mesh: use # bit indices: | Mesh: use 32 bit indices: |
| 118 | 6020 | 1 | 1 | - | `FerramAerospaceResear…` | # bit indices | 16 bit indices |
| 119 | 6016 | 1 | 5 | Profiler HUD | `RLVDebugGUI.PerfCell`, `RLVDebugGUI.PerfHeade…` | P# ms | P95 ms |
| 120 | 5974 | 962 | 18 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LEGACY (default) | ModuleManager: 2968 patches applied (9 %) :LEGACY (default) |
| 121 | 5964 | 9 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_CONTROLLERS] | ModuleManager: 56036 patches applied (96 %) :FOR[ZROWATERFA… |
| 122 | 5773 | 151 | 20 | Profiler HUD | `HUDProfilerOverlay.Re…` | <color=#34C759><b># ms</b></color> | <color=#34C759><b>0.054 ms</b></color> |
| 123 | 5742 | 36 | 3 | Profiler HUD | `RLVDebugGUI.DrawMetri…` | # FPS | 20 FPS |
| 124 | 5716 | 1 | 1 | - | `FerramAerospaceResear…` | FAR Debug Options, v#.# 'Marangoni' | FAR Debug Options, v0.16.1.2 'Marangoni' |
| 125 | 5437 | 11 | 20 | - | `HUDProfilerOverlay.Re…` | <color=#00E5FF><b># / # (#%)</b></color> | <color=#00E5FF><b>11 / 12 (92%)</b></color> |
| 126 | 5431 | 3 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_MAINPLUMEVARIANTS] | ModuleManager: 55671 patches applied (96 %) :FOR[ZROWATERFA… |
| 127 | 5350 | 6 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[RP-#] | ModuleManager: 31645 patches applied (68 %) :BEFORE[RP-0] |
| 128 | 5330 | 30 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[RP-#-KERBALISM] | ModuleManager: 39062 patches applied (71 %) :BEFORE[RP-0-KE… |
| 129 | 4849 | 18 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZREALPLUME] | ModuleManager: 62140 patches applied (96 %) :FOR[ZZREALPLUM… |
| 130 | 4795 | 8 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZREALANTENNAS] | ModuleManager: 51700 patches applied (95 %) :AFTER[ZREALANT… |
| 131 | 4730 | 344 | 8 | - | `UIPartActionResourceE…`, `UIPartActionWindow.Ad…` | # kL | 170 kL |
| 132 | 4614 | 42 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[FERRAMAEROSPACERESEARCH] | ModuleManager: 6256 patches applied (13 %) :AFTER[FERRAMAER… |
| 133 | 4600 | 10 | 1 | - | `FerramAerospaceResear…` | Custom# | Custom01 |
| 134 | 4405 | 45 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[FERRAMAEROSPACERESEARCH] | ModuleManager: 4134 patches applied (13 %) :FOR[FERRAMAEROS… |
| 135 | 4298 | 24 | 15 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[AJE] | ModuleManager: 3443 patches applied (12 %) :FOR[AJE] |
| 136 | 4184 | 1176 | 29 | LoadingScreen | `LoadingScreen.UpdateP…`, `TMP_Text.TMPro.TMP_Te…` | Loading model asset #/# (buffer=#MB) | Loading model asset 2832/3042 (buffer=40MB) |
| 137 | 4162 | 48 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ROTANKS] | ModuleManager: 30098 patches applied (65 %) :FOR[ROTANKS] |
| 138 | 4094 | 3 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_GLOWRESIZE] | ModuleManager: 55089 patches applied (96 %) :FOR[ZROWATERFA… |
| 139 | 4093 | 10 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZREALANTENNAS] | ModuleManager: 51222 patches applied (95 %) :FOR[ZREALANTEN… |
| 140 | 4073 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZTESTFLIGHT] | ModuleManager: 61790 patches applied (96 %) :AFTER[ZTESTFLI… |
| 141 | 3954 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZZZREALFUELS] | ModuleManager: 64389 patches applied (98 %) :AFTER[ZZZREALF… |
| 142 | 3887 | 6 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ROWATERFALL] | ModuleManager: 31088 patches applied (68 %) :FOR[ROWATERFAL… |
| 143 | 3863 | 142 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[#_RESTOCK] | ModuleManager: 3411 patches applied (11 %) :FOR[000_RESTOCK] |
| 144 | 3842 | 20 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[RO-KERBALISMHARDDRIVES] | ModuleManager: 29378 patches applied (65 %) :FOR[RO-KERBALI… |
| 145 | 3615 | 6 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZZZREALISMOVERHAUL] | ModuleManager: 64747 patches applied (98 %) :BEFORE[ZZZREAL… |
| 146 | 3248 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_MAINEFFECT] | ModuleManager: 53388 patches applied (95 %) :FOR[ZROWATERFA… |
| 147 | 3092 | 24 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[ZZZKERBALISM] | ModuleManager: 79028 patches applied (100 %) :LAST[ZZZKERBA… |
| 148 | 3070 | 1 | 1 | - | `ConfigurationGUI.rend…` | restock-relays-# (Instance) (Instance) [KSP/Bumped Specular] | restock-relays-1 (Instance) (Instance) [KSP/Bumped Specular] |
| 149 | 3070 | 1 | 1 | - | `ConfigurationGUI.rend…` | SaturnStandPad#_Cube#_auvMat (Instance) [KSP/Diffuse] | SaturnStandPad4250_Cube70_auvMat (Instance) [KSP/Diffuse] |
| 150 | 3070 | 1 | 1 | - | `ConfigurationGUI.rend…` | Monolith# [Legacy Shaders/Bumped Specular] | Monolith00 [Legacy Shaders/Bumped Specular] |
| 151 | 3070 | 1 | 1 | - | `ConfigurationGUI.rend…` | RSBpslvSpecular.# (Instance) (Instance) [KSP/Specular] | RSBpslvSpecular.001 (Instance) (Instance) [KSP/Specular] |
| 152 | 3070 | 1 | 1 | - | `ConfigurationGUI.rend…` | restock-antenna-# [KSP/ScreenSpaceMaskBumped] | restock-antenna-1 [KSP/ScreenSpaceMaskBumped] |
| 153 | 3070 | 1 | 1 | - | `ConfigurationGUI.rend…` | SC-GEN-FR-Mat.# (Instance) [TU/Metallic] | SC-GEN-FR-Mat.004 (Instance) [TU/Metallic] |
| 154 | 3070 | 1 | 1 | - | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_MCLvl#_UltraQualityGrass [KSP/Scenery/Diffuse Ground… | ksc_exterior_terrain_grass_MCLvl03_UltraQualityGrass [KSP/S… |
| 155 | 3070 | 1 | 1 | - | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_r&dLvl#_UltraQualityGrass [KSP/Scenery/Diffuse Groun… | ksc_exterior_terrain_grass_r&dLvl03_UltraQualityGrass [KSP/… |
| 156 | 3070 | 1 | 1 | - | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_launchPadLvl#_UltraQualityGrass [KSP/Scenery/Diffuse… | ksc_exterior_terrain_grass_launchPadLvl03_UltraQualityGrass… |
| 157 | 3070 | 1 | 1 | - | `ConfigurationGUI.rend…` | restock-relays-#-Cu (Instance) (Instance) [KSP/Bumped Specular] | restock-relays-1-Cu (Instance) (Instance) [KSP/Bumped Specu… |
| 158 | 3070 | 1 | 1 | - | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_VABLvl#_UltraQualityGrass [KSP/Scenery/Diffuse Groun… | ksc_exterior_terrain_grass_VABLvl03_UltraQualityGrass [KSP/… |
| 159 | 3070 | 1 | 1 | - | `ConfigurationGUI.rend…` | #d#caa-#a-#-b#-e#d#ba# [EVE/ScreenSpacePlanetLight] | 59d36caa-323a-4493-b399-e4d25385ba27 [EVE/ScreenSpacePlanet… |
| 160 | 3070 | 1 | 1 | - | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_TSLvl#_UltraQualityGrass [KSP/Scenery/Diffuse Ground… | ksc_exterior_terrain_grass_TSLvl03_UltraQualityGrass [KSP/S… |
| 161 | 3070 | 1 | 1 | - | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_adminLvl#_ultraQualityGrass [KSP/Scenery/Diffuse Gro… | ksc_exterior_terrain_grass_adminLvl03_ultraQualityGrass [KS… |
| 162 | 3070 | 1 | 1 | - | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_AClvl#_ultraQualityGrass [KSP/Scenery/Diffuse Ground… | ksc_exterior_terrain_grass_AClvl03_ultraQualityGrass [KSP/S… |
| 163 | 3070 | 1 | 1 | - | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_sphLvl#_UltraQualityGrass [KSP/Scenery/Diffuse Groun… | ksc_exterior_terrain_grass_sphLvl03_UltraQualityGrass [KSP/… |
| 164 | 3070 | 1 | 1 | - | `ConfigurationGUI.rend…` | ksc_exterior_terrain_grass_launchPadLvl#_UltraQualityGrass# [KSP/Scenery/Diffus… | ksc_exterior_terrain_grass_launchPadLvl03_UltraQualityGrass… |
| 165 | 3013 | 23 | 15 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALANTENNAS] | ModuleManager: 8739 patches applied (14 %) :FOR[REALANTENNA… |
| 166 | 2932 | 1 | 1 | - | `RLVDebugGUI.DrawPerfo…` | D:/Kerbal Space Program_newmod/KSP_x#_Data/../GameData/RealisticLaunchVisuals/L… | D:/Kerbal Space Program_newmod/KSP_x64_Data/../GameData/Rea… |
| 167 | 2865 | 30 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[RESTOCKPLUS] | ModuleManager: 29040 patches applied (64 %) :FOR[RESTOCKPLU… |
| 168 | 2816 | 16 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL_HEATSHIELD] | ModuleManager: 19466 patches applied (49 %) :FOR[REALISMOVE… |
| 169 | 2697 | 22 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ROWATERFALL] | ModuleManager: 31478 patches applied (68 %) :AFTER[ROWATERF… |
| 170 | 2676 | 18 | 15 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[REALANTENNAS] | ModuleManager: 8693 patches applied (14 %) :BEFORE[REALANTE… |
| 171 | 2592 | 13 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZRP-#] | ModuleManager: 70682 patches applied (98 %) :FOR[ZZZRP-0] |
| 172 | 2494 | 1 | 29 | - | `LoadingScreen.UpdateP…`, `TMP_Text.TMPro.TMP_Te…` | RP-# Initialization & Setup | RP-1 Initialization & Setup |
| 173 | 2400 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZROWATERFALL_#_MAINPLUMEVARIANTS] | ModuleManager: 55558 patches applied (96 %) :BEFORE[ZROWATE… |
| 174 | 2392 | 4 | 2 | - | `ProfilerUI.DrawPlugin…` | ▶ <color=#FFFFFF>Timing#</color> | ▶ <color=#FFFFFF>Timing0</color> |
| 175 | 2337 | 1329 | 20 | Profiler HUD | `HUDProfilerOverlay.Re…` | <color=#FF5555><b># ms</b></color> | <color=#FF5555><b>0.554 ms</b></color> |
| 176 | 2156 | 6 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL_SOLARPANELS] | ModuleManager: 23455 patches applied (49 %) :FOR[REALISMOVE… |
| 177 | 2146 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_FINALIZE] | ModuleManager: 56780 patches applied (96 %) :FOR[ZROWATERFA… |
| 178 | 1999 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[XXXRP#] | ModuleManager: 50849 patches applied (95 %) :AFTER[XXXRP0] |
| 179 | 1970 | 6 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZROWATERFALL_#_HYBRIDPLUMES] | ModuleManager: 55910 patches applied (96 %) :BEFORE[ZROWATE… |
| 180 | 1920 | 11 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL_REACTIONWHEEL] | ModuleManager: 23407 patches applied (49 %) :FOR[REALISMOVE… |
| 181 | 1898 | 8 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[RO-RCS] | ModuleManager: 29579 patches applied (65 %) :AFTER[RO-RCS] |
| 182 | 1864 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZZZZ-B#PARTSWITCH] | ModuleManager: 74841 patches applied (99 %) :FOR[ZZZZZZ-B9P… |
| 183 | 1855 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_LATEINITIALIZE] | ModuleManager: 52644 patches applied (95 %) :FOR[ZROWATERFA… |
| 184 | 1845 | 15 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAULTANKTYPES] | ModuleManager: 28413 patches applied (60 %) :FOR[REALISMOVE… |
| 185 | 1771 | 9 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL_MATERIALS_LATE] | ModuleManager: 21806 patches applied (49 %) :FOR[REALISMOVE… |
| 186 | 1745 | 16 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZROWATERFALL_POST] | ModuleManager: 57201 patches applied (96 %) :AFTER[ZROWATER… |
| 187 | 1716 | 45 | 3 | Profiler HUD | `ProfilerUI.DrawSpikeS…` | <color=#ff5555><b># ms</b></color> (#%) | <color=#ff5555><b>0.6 ms</b></color> (0.9%) |
| 188 | 1690 | 1609 | 20 | - | `SmartUIExtensions.Set…` | T+ #y, #d, #:#:# | T+ 0y, 0d, 00:00:00 |
| 189 | 1650 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_AUTOCONFIG] | ModuleManager: 52575 patches applied (95 %) :FOR[ZROWATERFA… |
| 190 | 1640 | 24 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZPOSTREALPLUMEROENGINES] | ModuleManager: 62113 patches applied (96 %) :FOR[ZZPOSTREAL… |
| 191 | 1637 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_HYBRIDPLUMES] | ModuleManager: 55962 patches applied (96 %) :FOR[ZROWATERFA… |
| 192 | 1635 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_GLOWPREPROCESS] | ModuleManager: 54874 patches applied (96 %) :FOR[ZROWATERFA… |
| 193 | 1626 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_INITIALIZE] | ModuleManager: 52203 patches applied (95 %) :FOR[ZROWATERFA… |
| 194 | 1554 | 12 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAUL_MATERIALS] | ModuleManager: 21573 patches applied (49 %) :AFTER[REALISMO… |
| 195 | 1552 | 37 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Post-physics callback (after yield): <b><color=#ffaa55># ms</color></b> (Pea… | 3. Post-physics callback (after yield): <b><color=#ffaa55>1… |
| 196 | 1544 | 5 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[RO-KERBALISMHARDDRIVES] | ModuleManager: 29258 patches applied (65 %) :BEFORE[RO-KERB… |
| 197 | 1504 | 8 | 14 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[PROCEDURALFAIRINGS] | ModuleManager: 8661 patches applied (14 %) :FOR[PROCEDURALF… |
| 198 | 1455 | 3 | 1 | - | `EngineConfigInfoPanel…` | at <color=#90EE90>#m #s</color> # in <color=#FF6666>#</color> burns fail | at <color=#90EE90>1m 40s</color> 1 in <color=#FF6666>105.3<… |
| 199 | 1455 | 3 | 1 | - | `EngineConfigInfoPanel…` | Ignition: #% | Ignition: 100.00% |
| 200 | 1443 | 19 | 15 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[KERBALISM] | ModuleManager: 8441 patches applied (13 %) :FOR[KERBALISM] |
| 201 | 1416 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_GLOW] | ModuleManager: 55304 patches applied (96 %) :FOR[ZROWATERFA… |
| 202 | 1408 | 6 | 3 | - | `ProfilerUI.DrawHeader…` | <b>#% Low:</b> <color=#FF3333><b>#</b></color> | <b>1% Low:</b> <color=#FF3333><b>10</b></color> |
| 203 | 1371 | 1308 | 12 | RDR | `BaseFlightWidget.SetT…` | RDR RDR # km | RDR RDR 1.04 km |
| 204 | 1368 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_VALIDATEREQUIREDKEYS] | ModuleManager: 53016 patches applied (95 %) :FOR[ZROWATERFA… |
| 205 | 1330 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[XXXRP#] | ModuleManager: 48413 patches applied (74 %) :BEFORE[XXXRP0] |
| 206 | 1262 | 3 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZROWATERFALL_#_AUDIO] | ModuleManager: 54132 patches applied (95 %) :BEFORE[ZROWATE… |
| 207 | 1255 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZREALPLUME] | ModuleManager: 51976 patches applied (95 %) :FOR[ZREALPLUME] |
| 208 | 1242 | 6 | 14 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[AJE] | ModuleManager: 3472 patches applied (12 %) :AFTER[AJE] |
| 209 | 1219 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[XXXRP-#] | ModuleManager: 51025 patches applied (95 %) :FOR[XXXRP-0] |
| 210 | 1176 | 139 | 3 | - | `ProfilerUI.DrawHeader…` | <b>PTR:</b> <color=#FF3333><b>#%</b></color> | <b>PTR:</b> <color=#FF3333><b>44.5%</b></color> |
| 211 | 1144 | 4 | 3 | - | `ProfilerUI.DrawHeader…` | <color=#aaaaaa>GC: #</color> | <color=#aaaaaa>GC: 366</color> |
| 212 | 1124 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[REALANTENNAS] | ModuleManager: 77153 patches applied (100 %) :LAST[REALANTE… |
| 213 | 1099 | 17 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ROCAPSULES] | ModuleManager: 29171 patches applied (64 %) :AFTER[ROCAPSUL… |
| 214 | 1000 | 30 | 3 | Profiler HUD | `ProfilerUI.DrawHeader…` | <b>FPS:</b> <color=#FF3333><b>#</b></color> (#) | <b>FPS:</b> <color=#FF3333><b>22</b></color> (17) |
| 215 | 959 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[#_KSP-RECALL] | ModuleManager: 76551 patches applied (99 %) :LAST[999_KSP-R… |
| 216 | 949 | 1 | 1 | - | `EVEManager.GlobalEVEM…` | EVE #.# Config Editor | EVE 1.11.7.2 Config Editor |
| 217 | 890 | 1 | 1 | - | `ConfigurationGUI.rend…` | Applied rule: # matching materials / # surfaces. Save to retain it across resta… | Applied rule: 946 matching materials / 2173 surfaces. Save … |
| 218 | 890 | 38 | 3 | Profiler HUD | `ProfilerUI.DrawHeroKi…` | <b>##</b> [Plugin] <b>RLVAddon</b> (<color=#55FF88>#ms, #%</color>) | <b>#2</b> [Plugin] <b>RLVAddon</b> (<color=#55FF88>2.2ms, 4… |
| 219 | 888 | 12 | 15 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[KERBALISM] | ModuleManager: 8576 patches applied (14 %) :AFTER[KERBALISM] |
| 220 | 867 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZROWATERFALL_#_HYBRIDPLUMES] | ModuleManager: 55998 patches applied (96 %) :AFTER[ZROWATER… |
| 221 | 843 | 14 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[SXT] | ModuleManager: 48376 patches applied (74 %) :AFTER[SXT] |
| 222 | 826 | 94 | 6 | - | `UIPartActionLabel.Upd…` | Flight Data: # du / # du | Flight Data: 862 du / 10000 du |
| 223 | 796 | 3 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[RO-KERBALISMHARDDRIVES] | ModuleManager: 29538 patches applied (65 %) :AFTER[RO-KERBA… |
| 224 | 780 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL_SOLARPANELS_LATE] | ModuleManager: 23493 patches applied (49 %) :FOR[REALISMOVE… |
| 225 | 777 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZREALISMOVERHAUL_RAPATCH] | ModuleManager: 70667 patches applied (98 %) :FOR[ZZZREALISM… |
| 226 | 750 | 7 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAUL_HEATSHIELD] | ModuleManager: 19608 patches applied (49 %) :AFTER[REALISMO… |
| 227 | 741 | 11 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[RP-#-KERBALISMHARDDRIVES] | ModuleManager: 47798 patches applied (71 %) :AFTER[RP-0-KER… |
| 228 | 719 | 2 | 20 | - | `HUDProfilerOverlay.Re…` | <color=#888888><b># / # (#%)</b></color> | <color=#888888><b>0 / 12 (0%)</b></color> |
| 229 | 713 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ROSOLAR] | ModuleManager: 29661 patches applied (65 %) :FOR[ROSOLAR] |
| 230 | 711 | 423 | 20 | RDR | `BaseFlightWidget.SetT…` | RDR RDR # m | RDR RDR 0 m |
| 231 | 669 | 8 | 16 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALFUELS] | ModuleManager: 8918 patches applied (17 %) :AFTER[REALFUELS] |
| 232 | 664 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_#_EXTRATEMPLATES] | ModuleManager: 55519 patches applied (96 %) :FOR[ZROWATERFA… |
| 233 | 654 | 64 | 30 | LoadingScreen | `LoadingScreen.UpdateP…`, `TMP_Text.TMPro.TMP_Te…` | Loading sound asset #/# | Loading sound asset 0/234 |
| 234 | 649 | 4 | 13 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[KERBALISMDEFAULT] | ModuleManager: 8576 patches applied (14 %) :FOR[KERBALISMDE… |
| 235 | 643 | 4 | 1 | - | `UIPartActionLabel.Upd…` | cost: Dry: # Wet: # | cost: Dry: 0 Wet: 0 |
| 236 | 596 | 39 | 5 | - | `UIPartActionLabel.Upd…` | Total Run Time: #s/#s | Total Run Time: 0s/135s |
| 237 | 596 | 46 | 5 | - | `UIPartActionLabel.Upd…` | Thrust Ratio: # | Thrust Ratio: 1 |
| 238 | 596 | 39 | 5 | - | `UIPartActionLabel.Upd…` | Current Run Time: #s/#s | Current Run Time: 0s/135s |
| 239 | 582 | 6 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL_LIFESUPPORT] | ModuleManager: 19658 patches applied (49 %) :FOR[REALISMOVE… |
| 240 | 568 | 14 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Post-physics callback (after yield): <b><color=#ff5555># ms</color></b> (Pea… | 3. Post-physics callback (after yield): <b><color=#ff5555>2… |
| 241 | 558 | 18 | 3 | Profiler HUD | `PerformanceProbe.Draw…` | #. Precalculation (Precalc / Bodies): <b><color=#ffaa55># ms</color></b> (Peak:… | 1. Precalculation (Precalc / Bodies): <b><color=#ffaa55>1.5… |
| 242 | 512 | 6 | 13 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[KERBALISMDEFAULT] | ModuleManager: 8600 patches applied (14 %) :AFTER[KERBALISM… |
| 243 | 507 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[REALISMOVERHAUL_SOLARPANELS] | ModuleManager: 23417 patches applied (49 %) :BEFORE[REALISM… |
| 244 | 506 | 19 | 2 | Profiler HUD | `ProfilerUI.DrawHeroKi…` | <b>##</b> [Plugin] <b>TimingPre</b> (<color=#55FF88>#ms, #%</color>) | <b>#3</b> [Plugin] <b>TimingPre</b> (<color=#55FF88>2.0ms, … |
| 245 | 504 | 3 | 3 | - | `ProfilerUI.DrawTimeli…` | #F (~#s) | 100F (~1.5s) |
| 246 | 500 | 3 | 1 | - | `UIPartActionLabel.Upd…` | Largest Diameter: #m | Largest Diameter: 7.0000m |
| 247 | 500 | 1 | 1 | - | `UIPartActionLabel.Upd…` | Total Length: #m | Total Length: 21.0000m |
| 248 | 485 | 1 | 1 | - | `EngineConfigGUI.DrawC…` | <color=#FFB380>#</color> / <color=#80D9FF>#</color> / <color=#80E680># %</color> | <color=#FFB380>98.9</color> / <color=#80D9FF>99.0</color> /… |
| 249 | 485 | 1 | 1 | - | `EngineConfigGUI.DrawC…` | #t | 39.757t |
| 250 | 485 | 1 | 1 | - | `EngineConfigInfoPanel…` | Set to Current (#du) | Set to Current (862du) |
| 251 | 485 | 1 | 1 | - | `EngineConfigGUI.DrawH…` | Surv@#s | Surv@100s |
| 252 | 485 | 1 | 1 | - | `EngineConfigGUI.DrawC…` | # kN | 6,227 kN |
| 253 | 476 | 434 | 4 | - | `UIPartActionLabel.Upd…` | Mass Flow: # ton/s | Mass Flow: 1.78795 ton/s |
| 254 | 473 | 4 | 14 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[FERRAMAEROSPACERESEARCH] | ModuleManager: 4044 patches applied (12 %) :BEFORE[FERRAMAE… |
| 255 | 471 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[RSSEADRAGON] | ModuleManager: 47995 patches applied (71 %) :FOR[RSSEADRAGO… |
| 256 | 468 | 1 | 1 | - | `RealAntennas.RealAnte…` | Vessels: # | Vessels: 51 |
| 257 | 468 | 1 | 1 | - | `RealAntennas.RealAnte…` | Antennas/vessel: # | Antennas/vessel: 1.5 |
| 258 | 468 | 1 | 1 | - | `RealAntennas.RealAnte…` | GroundStations: # | GroundStations: 31 |
| 259 | 468 | 13 | 12 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[#_RESTOCK] | ModuleManager: 3411 patches applied (11 %) :AFTER[000_RESTO… |
| 260 | 468 | 1 | 1 | - | `RealAntennas.RealAnte…` | RealAntennas v#.# | RealAntennas v2.12.0.0 |
| 261 | 451 | 2 | 14 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ENGINEGROUPCONTOLLER] | ModuleManager: 3695 patches applied (12 %) :FOR[ENGINEGROUP… |
| 262 | 436 | 13 | 1 | Profiler HUD | `ProfilerUI.DrawHeroKi…` | <b>##</b> [TUFX] <b>PostProcessLayer (TUFX)</b> (<color=#55FF88>#ms, #%</color>) | <b>#2</b> [TUFX] <b>PostProcessLayer (TUFX)</b> (<color=#55… |
| 263 | 434 | 9 | 13 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[KSPCOMMUNITYFIXES] | ModuleManager: 8617 patches applied (14 %) :FOR[KSPCOMMUNIT… |
| 264 | 426 | 8 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[RESTOCK] | ModuleManager: 29030 patches applied (64 %) :AFTER[RESTOCK] |
| 265 | 408 | 27 | 1 | Profiler HUD | `ProfilerUI.DrawHeader…` | <b>FPS:</b> <color=#FFFF33><b>#</b></color> (#) | <b>FPS:</b> <color=#FFFF33><b>35</b></color> (22) |
| 266 | 398 | 9 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[RESTOCKPLUS] | ModuleManager: 29160 patches applied (64 %) :AFTER[RESTOCKP… |
| 267 | 397 | 2 | 5 | - | `UIPartActionLabel.Upd…` | Current Throttle: #% | Current Throttle: 100.00% |
| 268 | 389 | 8 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZROWATERFALL_#_FINALIZE] | ModuleManager: 57190 patches applied (96 %) :AFTER[ZROWATER… |
| 269 | 388 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REMOTETECH-ANTENNAS] | ModuleManager: 29030 patches applied (64 %) :FOR[REMOTETECH… |
| 270 | 381 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAUL_SOLARPANELS_LATE] | ModuleManager: 23519 patches applied (49 %) :AFTER[REALISMO… |
| 271 | 378 | 1 | 1 | - | `RLVDebugGUI.DrawPerfo…` | D:/Kerbal Space Program_newmod/KSP_x#_Data/../GameData/RealisticLaunchVisuals/L… | D:/Kerbal Space Program_newmod/KSP_x64_Data/../GameData/Rea… |
| 272 | 371 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAUL_HEATSHIELD_LATE] | ModuleManager: 19622 patches applied (49 %) :FOR[REALISMOVE… |
| 273 | 357 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[RP-#-KERBALISM] | ModuleManager: 78999 patches applied (100 %) :LAST[RP-0-KER… |
| 274 | 355 | 3 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZRORESCALEDRAGCUBES] | ModuleManager: 52188 patches applied (95 %) :FOR[ZRORESCALE… |
| 275 | 354 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[RO-RCS] | ModuleManager: 29545 patches applied (65 %) :FOR[RO-RCS] |
| 276 | 349 | 2 | 15 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ATMOSPHEREAUTOPILOT] | ModuleManager: 3478 patches applied (12 %) :FOR[ATMOSPHEREA… |
| 277 | 342 | 8 | 5 | - | `UIPartActionLabel.Upd…` | Time Since Shutdown: #s | Time Since Shutdown: 0.00s |
| 278 | 342 | 1 | 5 | - | `UIPartActionLabel.Upd…` | Ignition Penalty for Q: # % | Ignition Penalty for Q: 0.00 % |
| 279 | 342 | 3 | 5 | - | `UIPartActionLabel.Upd…` | Ignition Chance: # % | Ignition Chance: 99.90 % |
| 280 | 342 | 1 | 5 | - | `UIPartActionLabel.Upd…` | Thrust Modifier: # % | Thrust Modifier: 100.00 % |
| 281 | 342 | 1 | 5 | - | `UIPartActionLabel.Upd…` | Restart Ignition Penalty: # % | Restart Ignition Penalty: 0.00 % |
| 282 | 338 | 8 | 1 | Profiler HUD | `PerformanceProbe.Draw…` | #. Precalculation (Precalc / Bodies): <b><color=#ff5555># ms</color></b> (Peak:… | 1. Precalculation (Precalc / Bodies): <b><color=#ff5555>12.… |
| 283 | 334 | 3 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZRP-#] | ModuleManager: 57220 patches applied (96 %) :FOR[ZRP-0] |
| 284 | 334 | 3 | 5 | - | `UIPartActionLabel.Upd…` | Mean Time Between Failures: #h | Mean Time Between Failures: 2.60h |
| 285 | 321 | 12 | 14 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[KERBALFOUNDRIES] | ModuleManager: 8401 patches applied (13 %) :FOR[KERBALFOUND… |
| 286 | 318 | 1 | 4 | - | `UIPartActionLabel.Upd…` | Eng. Internal Temp: NaN K / # K | Eng. Internal Temp: NaN K / 2,430 K |
| 287 | 312 | 14 | 1 | Profiler HUD | `ProfilerUI.DrawHeroKi…` | <b>##</b> [Plugin] <b>EditorLogic</b> (<color=#55FF88>#ms, #%</color>) | <b>#3</b> [Plugin] <b>EditorLogic</b> (<color=#55FF88>0.7ms… |
| 288 | 301 | 6 | 5 | - | `UIPartActionLabel.Upd…` | Last Restart: Roll: # %, Chance: # % | Last Restart: Roll: 83.08 %, Chance: 99.90 % |
| 289 | 278 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZZZ-RP#TREE] | ModuleManager: 74840 patches applied (99 %) :FOR[ZZZZZ-RP0T… |
| 290 | 266 | 4 | 3 | Profiler HUD | `ProfilerUI.DrawTimeli…` | <b><color=#00e5ff>TUFX:</color></b> # ms | <b><color=#00e5ff>TUFX:</color></b> 0.01 ms |
| 291 | 261 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAUL_SOLARPANELS] | ModuleManager: 23493 patches applied (49 %) :AFTER[REALISMO… |
| 292 | 260 | 37 | 18 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FIRST | ModuleManager: 134 patches applied (0 %) :FIRST |
| 293 | 254 | 2 | 2 | - | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>TimingPre</b> -> <color=#88bbdd>Fi… | <b>#1</b> <color=#a29bfe>[Plugin]</color> <b>TimingPre</b> … |
| 294 | 254 | 4 | 2 | - | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>Kerbalism</b> -> <color=#88bbdd>Fi… | <b>#6</b> <color=#a29bfe>[Plugin]</color> <b>Kerbalism</b> … |
| 295 | 254 | 4 | 2 | - | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>ModularFlightIntegrator</b> -> <co… | <b>#4</b> <color=#a29bfe>[Plugin]</color> <b>ModularFlightI… |
| 296 | 250 | 5 | 2 | - | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>Timing#</b> -> <color=#88bbdd>Fixe… | <b>#5</b> <color=#a29bfe>[Plugin]</color> <b>Timing1</b> ->… |
| 297 | 249 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[REALISMOVERHAULRC] | ModuleManager: 28413 patches applied (60 %) :FOR[REALISMOVE… |
| 298 | 248 | 1 | 1 | - | `ProfilerUI.DrawPartsT…` | <color=#FFFFFF>Angled RCS Block [ #/# N Class]</color> | <color=#FFFFFF>Angled RCS Block [ 28/45 N Class]</color> |
| 299 | 248 | 7 | 11 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :FOR[AAA_AJE] | ModuleManager: 3435 patches applied (11 %) :FOR[AAA_AJE] |
| 300 | 247 | 47 | 20 | - | `SmartUIExtensions.Set…`, `UIFactory.CreateText` | #G | 1.0G |
| 301 | 245 | 223 | 3 | - | `SmartUIExtensions.Set…` | Δv # | Δv 186 |
| 302 | 242 | 6 | 1 | Profiler HUD | `PerformanceProbe.Draw…` | #. Markers & Geometry (LateUpdate / Markers): <b><color=#ff5555># ms</color></b… | 5. Markers & Geometry (LateUpdate / Markers): <b><color=#ff… |
| 303 | 240 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[KOP_SCATTER_MASK] | ModuleManager: 77150 patches applied (100 %) :LAST[KOP_SCAT… |
| 304 | 232 | 4 | 3 | - | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>PostProcessLayer</b> -> <color=#88… | <b>#2</b> <color=#a29bfe>[Plugin]</color> <b>PostProcessLay… |
| 305 | 230 | 1 | 1 | - | `UIPartActionLabel.Upd…` | Max Ignition Chance: # % | Max Ignition Chance: 100.00 % |
| 306 | 230 | 1 | 1 | - | `UIPartActionLabel.Upd…` | Current Reliability: # %, MTBF: #h | Current Reliability: 98.73 %, MTBF: 2.93h |
| 307 | 230 | 1 | 1 | - | `UIPartActionLabel.Upd…` | Reliability at Max Data: # %, MTBF: #h | Reliability at Max Data: 99.71 %, MTBF: 13.02h |
| 308 | 230 | 1 | 1 | - | `UIPartActionLabel.Upd…` | Current Ignition Chance: # % | Current Ignition Chance: 99.91 % |
| 309 | 230 | 1 | 1 | - | `UIPartActionLabel.Upd…` | Rated Burn Time: #s (#m #s) | Rated Burn Time: 135s (2m 15s) |
| 310 | 225 | 5 | 10 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[A_ROTANKS] | ModuleManager: 3426 patches applied (11 %) :AFTER[A_ROTANKS] |
| 311 | 217 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[REALISMOVERHAULENGINES] | ModuleManager: 78936 patches applied (100 %) :LAST[REALISMO… |
| 312 | 208 | 34 | 1 | - | `ProfilerUI.DrawHeader…` | <b>PTR:</b> <color=#FFFF33><b>#%</b></color> | <b>PTR:</b> <color=#FFFF33><b>67.0%</b></color> |
| 313 | 206 | 2 | 1 | - | `ProfilerUI.DrawHeader…` | <color=#33FF33>GC: #</color> | <color=#33FF33>GC: 366</color> |
| 314 | 197 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[VABORGANIZER] | ModuleManager: 48407 patches applied (74 %) :AFTER[VABORGAN… |
| 315 | 196 | 3 | 3 | - | `ProfilerUI.DrawTimeli…` | <b>🗑️ Mono:</b> <color=#FF3333>#MB</color> GC: <color=#33FF33>#/s</color> | <b>🗑️ Mono:</b> <color=#FF3333>3469MB</color> GC: <color=#3… |
| 316 | 196 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZZREALPLUME] | ModuleManager: 62140 patches applied (96 %) :BEFORE[ZZREALP… |
| 317 | 195 | 5 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ROHEATSHIELDS] | ModuleManager: 29199 patches applied (64 %) :FOR[ROHEATSHIE… |
| 318 | 175 | 73 | 5 | - | `UIPartActionLabel.Upd…` | Mass Flow: # kg/s | Mass Flow: 0.00000 kg/s |
| 319 | 168 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ROSOLAR] | ModuleManager: 29658 patches applied (65 %) :BEFORE[ROSOLAR] |
| 320 | 168 | 5 | 3 | Profiler HUD | `ProfilerUI.DrawTimeli…` | <color=#888888>#ms</color> | <color=#888888>149ms</color> |
| 321 | 167 | 4 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[SOLSYSTEM] | ModuleManager: 79014 patches applied (100 %) :LAST[SOLSYSTE… |
| 322 | 167 | 4 | 13 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALANTENNAS] | ModuleManager: 8764 patches applied (15 %) :AFTER[REALANTEN… |
| 323 | 163 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZZREALPLUME] | ModuleManager: 62159 patches applied (97 %) :AFTER[ZZREALPL… |
| 324 | 159 | 80 | 14 | - | `BaseFlightWidget.SetT…` | # atm | 0.99 atm |
| 325 | 158 | 4 | 2 | - | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>ReflectionProbeFixer</b> -> <color… | <b>#3</b> <color=#a29bfe>[Plugin]</color> <b>ReflectionProb… |
| 326 | 156 | 3 | 1 | - | `UIPartActionLabel.Upd…` | Mass: #kg | Mass: 496.4kg |
| 327 | 148 | 2 | 13 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALCHUTE] | ModuleManager: 8770 patches applied (15 %) :AFTER[REALCHUTE] |
| 328 | 143 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZAFTERUNIVERSALSTORAGE] | ModuleManager: 62159 patches applied (97 %) :FOR[ZZZAFTERUN… |
| 329 | 140 | 3 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZZZZZROENGINES] | ModuleManager: 74838 patches applied (99 %) :FOR[ZZZZZROENG… |
| 330 | 137 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAULTANKTYPES] | ModuleManager: 28527 patches applied (61 %) :AFTER[REALISMO… |
| 331 | 136 | 4 | 1 | Profiler HUD | `PerformanceProbe.Draw…` | #. Late Sync (BetterLate / Motion): <b><color=#ff5555># ms</color></b> (Peak: #… | 4. Late Sync (BetterLate / Motion): <b><color=#ff5555>5.965… |
| 332 | 129 | 2 | 16 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ROCAPSULES] | ModuleManager: 29168 patches applied (64 %) :FOR[ROCAPSULES] |
| 333 | 124 | 1 | 1 | - | `ProfilerUI.DrawPartsT…` | <color=#FFFFFF>UA#</color> | <color=#FFFFFF>UA1206</color> |
| 334 | 124 | 1 | 1 | - | `ProfilerUI.DrawPartsT…` | <color=#FFFFFF>LR#</color> | <color=#FFFFFF>LR129</color> |
| 335 | 124 | 1 | 1 | - | `ProfilerUI.DrawPartsT…` | <color=#FFFFFF>UA# Nosecone & Separation Motor</color> | <color=#FFFFFF>UA1205 Nosecone & Separation Motor</color> |
| 336 | 122 | 2 | 7 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :FOR[AAA] | ModuleManager: 3432 patches applied (11 %) :FOR[AAA] |
| 337 | 117 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ROENGINES] | ModuleManager: 29197 patches applied (64 %) :AFTER[ROENGINE… |
| 338 | 111 | 1 | 1 | - | `UIPartActionWindow.Ad…` | # L | 80 L |
| 339 | 108 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[REALISMOVERHAUL_HEATSHIELD] | ModuleManager: 19418 patches applied (49 %) :BEFORE[REALISM… |
| 340 | 108 | 4 | 11 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[KERBALISM] | ModuleManager: 8419 patches applied (13 %) :BEFORE[KERBALIS… |
| 341 | 104 | 3 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[ROCAPSULES] | ModuleManager: 78936 patches applied (100 %) :LAST[ROCAPSUL… |
| 342 | 104 | 11 | 3 | - | `unknown` | <color=#000000># kL</color> | <color=#000000>113 kL</color> |
| 343 | 97 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :BEFORE[ZZZRP-#] | ModuleManager: 70667 patches applied (98 %) :BEFORE[ZZZRP-0] |
| 344 | 96 | 2 | 9 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[B#_AEROSPACE_WINGSTUFF] | ModuleManager: 3689 patches applied (12 %) :FOR[B9_AEROSPAC… |
| 345 | 95 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[RO-KERBALISM] | ModuleManager: 29256 patches applied (65 %) :AFTER[RO-KERBA… |
| 346 | 94 | 3 | 10 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[PROCEDURALPARTS] | ModuleManager: 8684 patches applied (14 %) :FOR[PROCEDURALP… |
| 347 | 91 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[RP-#-KERBALISMHARDDRIVES] | ModuleManager: 47796 patches applied (71 %) :FOR[RP-0-KERBA… |
| 348 | 88 | 1 | 1 | - | `UIPartActionLabel.Upd…` | Mass: #t (both) | Mass: 0.608t (both) |
| 349 | 88 | 2 | 9 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :FOR[A_ROTANKS] | ModuleManager: 3424 patches applied (11 %) :FOR[A_ROTANKS] |
| 350 | 88 | 1 | 1 | - | `UIPartActionLabel.Upd…` | Cost: # (both) | Cost: 547 (both) |
| 351 | 87 | 1 | 18 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :INSERT (initial) | ModuleManager: 0 patches applied (0 %) :INSERT (initial) |
| 352 | 86 | 2 | 16 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[VABORGANIZER] | ModuleManager: 48404 patches applied (74 %) :FOR[VABORGANIZ… |
| 353 | 82 | 1 | 1 | - | `ProfilerUI.DrawPlugin…` | UI#n.Runtime | UI18n.Runtime |
| 354 | 82 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[VENSTOCKREVAMP] | ModuleManager: 48412 patches applied (74 %) :FOR[VENSTOCKRE… |
| 355 | 82 | 1 | 1 | - | `ProfilerUI.DrawPlugin…` | ▶ <color=#FFFFFF>UI#nAddon</color> | ▶ <color=#FFFFFF>UI18nAddon</color> |
| 356 | 79 | 1 | 3 | Profiler HUD | `ProfilerUI.DrawTimeli…` | <color=#33ff55>#ms (#F)</color> | <color=#33ff55>16.7ms (60F)</color> |
| 357 | 79 | 1 | 3 | Profiler HUD | `ProfilerUI.DrawTimeli…` | <color=#ff5555>#ms (#F)</color> | <color=#ff5555>50.0ms (20F)</color> |
| 358 | 79 | 2 | 16 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ROENGINES] | ModuleManager: 29190 patches applied (64 %) :FOR[ROENGINES] |
| 359 | 79 | 16 | 2 | - | `UIPartActionLabel.Upd…` | Eng. Internal Temp: # K / # K | Eng. Internal Temp: 317 K / 3,600 K |
| 360 | 79 | 1 | 3 | Profiler HUD | `ProfilerUI.DrawTimeli…` | <color=#ffaa22>#ms (#F)</color> | <color=#ffaa22>33.3ms (30F)</color> |
| 361 | 78 | 2 | 1 | - | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>Part</b> -> <color=#88bbdd>FixedUp… | <b>#2</b> <color=#a29bfe>[Plugin]</color> <b>Part</b> -> <c… |
| 362 | 78 | 1 | 2 | - | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>FlightGUI</b> -> <color=#88bbdd>Fi… | <b>#6</b> <color=#a29bfe>[Plugin]</color> <b>FlightGUI</b> … |
| 363 | 78 | 2 | 14 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[Z_SOLSYSTEM] | ModuleManager: 51027 patches applied (95 %) :FOR[Z_SOLSYSTE… |
| 364 | 75 | 67 | 5 | - | `SmartUIExtensions.Set…` | #x | 1.2x |
| 365 | 70 | 2 | 7 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :BEFORE[AAA_AJE] | ModuleManager: 3433 patches applied (11 %) :BEFORE[AAA_AJE] |
| 366 | 66 | 1 | 3 | - | `unknown` | <color=#000000>(#)</color> | <color=#000000>(0)</color> |
| 367 | 64 | 2 | 16 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZREALFUELSVOLUMES] | ModuleManager: 51702 patches applied (95 %) :FOR[ZREALFUELS… |
| 368 | 62 | 2 | 16 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ROSOLAR] | ModuleManager: 29665 patches applied (65 %) :AFTER[ROSOLAR] |
| 369 | 62 | 1 | 1 | - | `ProfilerUI.DrawPartsT…` | <color=#FFFFFF>#/#kN Thruster</color> | <color=#FFFFFF>1.1/1.78kN Thruster</color> |
| 370 | 62 | 1 | 1 | - | `ProfilerUI.DrawPartsT…` | <color=#FFFFFF>RL-# Series (Non-Extendable Nozzle)</color> | <color=#FFFFFF>RL-10 Series (Non-Extendable Nozzle)</color> |
| 371 | 59 | 35 | 5 | RDR | `BaseFlightWidget.SetT…` | RDR RDR # Mm | RDR RDR 1.33 Mm |
| 372 | 58 | 1 | 1 | - | `ProfilerUI.DrawHeader…` | <color=#ffaa22>🗑️ GC: #/s</color> | <color=#ffaa22>🗑️ GC: 1.0/s</color> |
| 373 | 57 | 2 | 15 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[PROCEDURALFAIRINGS] | ModuleManager: 77151 patches applied (100 %) :LAST[PROCEDUR… |
| 374 | 54 | 2 | 15 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZZZRP-#] | ModuleManager: 71146 patches applied (98 %) :AFTER[ZZZRP-0] |
| 375 | 50 | 1 | 1 | - | `ProfilerUI.DrawTimeli…` | <b>🗑️ Mono:</b> <color=#FFFF33>#MB</color> GC: <color=#33FF33>#/s</color> | <b>🗑️ Mono:</b> <color=#FFFF33>3537MB</color> GC: <color=#3… |
| 376 | 49 | 2 | 13 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[RO-KERBALISM] | ModuleManager: 29255 patches applied (65 %) :FOR[RO-KERBALI… |
| 377 | 48 | 2 | 2 | - | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>MaterialPipelineManager</b> -> <co… | <b>#3</b> <color=#a29bfe>[Plugin]</color> <b>MaterialPipeli… |
| 378 | 46 | 2 | 1 | Profiler HUD | `ProfilerUI.DrawHeroKi…` | <b>##</b> [Plugin] <b>RLVAddon</b> (<color=#FFAA22>#ms, #%</color>) | <b>#2</b> [Plugin] <b>RLVAddon</b> (<color=#FFAA22>4.7ms, 1… |
| 379 | 45 | 30 | 20 | - | `ManeuverNodeEditorTab…` | T + #d, #h, #m, #s | T + 56d, 18h, 13m, 25s |
| 380 | 45 | 2 | 16 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[ZKOPERNICUS] | ModuleManager: 79023 patches applied (100 %) :LAST[ZKOPERNI… |
| 381 | 44 | 7 | 20 | - | `BaseFlightWidget.SetT…`, `UIFactory.CreateText` | # Kbps | 15.8 Kbps |
| 382 | 44 | 1 | 1 | Profiler HUD | `PerformanceProbe.Draw…` | #. Markers & Geometry (LateUpdate / Markers): <b><color=#ffaa55># ms</color></b… | 5. Markers & Geometry (LateUpdate / Markers): <b><color=#ff… |
| 383 | 43 | 2 | 17 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[RP-#] | ModuleManager: 78939 patches applied (100 %) :LAST[RP-0] |
| 384 | 43 | 2 | 16 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[WATERFALL] | ModuleManager: 48412 patches applied (74 %) :AFTER[WATERFAL… |
| 385 | 42 | 1 | 2 | - | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>RLVAddon</b> -> <color=#88bbdd>Upd… | <b>#1</b> <color=#a29bfe>[Plugin]</color> <b>RLVAddon</b> -… |
| 386 | 41 | 2 | 10 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[PARALLAXSTOCK] | ModuleManager: 8653 patches applied (14 %) :FOR[PARALLAXSTO… |
| 387 | 40 | 1 | 20 | - | `InputFieldHandler.Upd…` | #y, #d, #:#:#​ | 0y, 0d, 00:00:00​ |
| 388 | 40 | 2 | 20 | Profiler HUD | `HUDProfilerOverlay.Cr…` | #ms | 0.00ms |
| 389 | 39 | 37 | 14 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) | ModuleManager: 12728 patches applied (35 %) |
| 390 | 38 | 2 | 9 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[PROCEDURALPARTS] | ModuleManager: 8692 patches applied (14 %) :AFTER[PROCEDURA… |
| 391 | 37 | 2 | 14 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[REALFUELS] | ModuleManager: 77253 patches applied (100 %) :LAST[REALFUEL… |
| 392 | 36 | 1 | 2 | Profiler HUD | `PerformanceProbe.Draw…` | Principia Net: <color=#888888># ms (Offline)</color> | Principia Net: <color=#888888>0.00 ms (Offline)</color> |
| 393 | 32 | 1 | 1 | - | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>PrincipiaPluginAdapter</b> -> <col… | <b>#6</b> <color=#a29bfe>[Plugin]</color> <b>PrincipiaPlugi… |
| 394 | 28 | 1 | 28 | - | `BurstPQS.UI.DebugUI.D…`, `DebugUIManager.Create…` | #-bit Height (R# DDS) | 16-bit Height (R16 DDS) |
| 395 | 28 | 1 | 28 | - | `TMP_Text.TMPro.TMP_Te…`, `VersionReadout.Start` | #.# (WindowsPlayer x#) zh-cn #.# Breaking Ground | 1.12.5.3190 (WindowsPlayer x64) zh-cn 1.7.1 Breaking Ground |
| 396 | 28 | 1 | 1 | - | `UIPartActionLabel.Upd…` | Mass: #t | Mass: 0.343t |
| 397 | 28 | 1 | 9 | - | `StageIconInfoBox.SetM…`, `UIPartActionWindow.Ad…` | CooledLCH# | CooledLCH4 |
| 398 | 28 | 1 | 1 | - | `UIPartActionLabel.Upd…` | Cost: # | Cost: 34 |
| 399 | 28 | 1 | 28 | - | `TMP_Text.TMPro.TMP_Te…`, `VersionReadout.Start` | #.# (WindowsPlayer x#) zh-cn | 1.12.5.3190 (WindowsPlayer x64) zh-cn |
| 400 | 27 | 2 | 9 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[KSPCOMMUNITYFIXES] | ModuleManager: 8643 patches applied (14 %) :AFTER[KSPCOMMUN… |
| 401 | 26 | 1 | 20 | - | `UIFactory.CreateText` | RP-#/maintecost | RP-1/maintecost |
| 402 | 26 | 1 | 20 | - | `UIFactory.CreateText` | EngineGroupController/ToolbarIcon# | EngineGroupController/ToolbarIcon4 |
| 403 | 23 | 1 | 20 | - | `KbAppFrame.Setup` | #autoLoc_# | #autoLoc_6002189 |
| 404 | 23 | 2 | 1 | - | `UIPartActionWindow.Ad…`, `UIPartActionWindow.Cr…` | # MJ | 9.67 MJ |
| 405 | 22 | 12 | 3 | - | `EngineersReport+<Upda…` | <color=#b4d455>#t</color> | <color=#b4d455>660.978t</color> |
| 406 | 22 | 1 | 11 | LoadingScreen | `LoadingScreen.UpdateP…` | Kopernicus: Loading RealisticLaunchVisuals\Native\Win#\RLVGpuTiming.bin | Kopernicus: Loading RealisticLaunchVisuals\Native\Win64\RLV… |
| 407 | 20 | 1 | 20 | Profiler HUD | `WorkbenchCanvasView.C…` | # FPS \| #ms | 60 FPS \| 0.1ms |
| 408 | 20 | 1 | 20 | - | `UIFactory.CreateText` | X: # Y: # \| #x | X: 0 Y: 0 \| 1.00x |
| 409 | 20 | 1 | 20 | - | `WorkbenchCanvasView.B…` | MODULAR FLIGHT PANEL # | MODULAR FLIGHT PANEL 3.0 |
| 410 | 20 | 1 | 20 | - | `UIFactory.CreateText` | UT #y, #d, #:#:# | UT 0y, 0d, 00:00:00 |
| 411 | 20 | 1 | 20 | - | `BaseFlightWidget.SetT…` | # bps | 0.0 bps |
| 412 | 20 | 1 | 1 | - | `ProfilerUI.DrawTimeli…` | <b>🗑️ Mono:</b> <color=#FFFF33>#MB</color> GC: <color=#ff8800>#/s</color> | <b>🗑️ Mono:</b> <color=#FFFF33>3537MB</color> GC: <color=#f… |
| 413 | 20 | 1 | 1 | - | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>FlightInputHandler</b> -> <color=#… | <b>#4</b> <color=#a29bfe>[Plugin]</color> <b>FlightInputHan… |
| 414 | 18 | 1 | 13 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZZZZZZZZZZZZZZZZZZZZZZZRP#] | ModuleManager: 76551 patches applied (99 %) :AFTER[ZZZZZZZZ… |
| 415 | 16 | 1 | 1 | - | `ProfilerUI.DrawSpikeS…` | <b>##</b> <color=#a29bfe>[Plugin]</color> <b>ToolbarReskinWatcher</b> -> <color… | <b>#3</b> <color=#a29bfe>[Plugin]</color> <b>ToolbarReskinW… |
| 416 | 16 | 1 | 14 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patch applied (# %) :FIRST | ModuleManager: 1 patch applied (0 %) :FIRST |
| 417 | 14 | 1 | 3 | - | `unknown` | <color=#000000># L</color> | <color=#000000>160 L</color> |
| 418 | 14 | 8 | 5 | - | `SmartUIExtensions.Set…` | M # | M 0.2 |
| 419 | 14 | 2 | 7 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :AFTER[KOPERNICUS] | ModuleManager: 8614 patches applied (14 %) :AFTER[KOPERNICU… |
| 420 | 14 | 1 | 3 | - | `unknown` | <color=#000000># MJ</color> | <color=#000000>76.6 MJ</color> |
| 421 | 14 | 1 | 8 | - | `GenericCascadingList.…` | <color=#b8f4d1>#h, #m, #s</color> | <color=#b8f4d1>23h, 56m, 4s</color> |
| 422 | 13 | 2 | 9 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[ZROWATERFALL_POST] | ModuleManager: 57200 patches applied (96 %) :FOR[ZROWATERFA… |
| 423 | 13 | 2 | 10 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[RP#] | ModuleManager: 31507 patches applied (68 %) :FOR[RP0] |
| 424 | 11 | 1 | 1 | - | `UIPartActionLabel.Upd…` | Tech Level: # | Tech Level: 10 |
| 425 | 11 | 1 | 1 | - | `UIPartActionLabel.Upd…` | Power (Idle): # Watts | Power (Idle): 9.50 Watts |
| 426 | 11 | 1 | 1 | - | `UIPartActionLabel.Upd…` | Transmit Power (dBm): # dBm | Transmit Power (dBm): 30.0 dBm |
| 427 | 11 | 1 | 1 | - | `UIPartActionLabel.Upd…` | Power: # W | Power: 16.4 W |
| 428 | 11 | 1 | 1 | - | `UIPartActionLabel.Upd…` | Power (Active): # Watts | Power (Active): 11.54 Watts |
| 429 | 11 | 1 | 1 | - | `UIPartActionLabel.Upd…` | Gain: # dBi | Gain: 1.5 dBi |
| 430 | 11 | 1 | 1 | - | `UIPartActionLabel.Upd…` | Controllable: #T | Controllable: 1.00T |
| 431 | 8 | 1 | 8 | - | `KbApp_VesselInfo.Crea…` | <color=#c8d782>#</color> | <color=#c8d782>10</color> |
| 432 | 8 | 7 | 8 | - | `ManeuverNodeEditorTab…` | #h, #m, #s | 1h, 46m, 27s |
| 433 | 8 | 3 | 1 | - | `UIPartActionLabel.Upd…` | Mean Time Between Failures: #m | Mean Time Between Failures: 17.19m |
| 434 | 8 | 1 | 8 | - | `ManeuverNodeEditorTab…` | #m, #s | 29m, 49s |
| 435 | 6 | 1 | 6 | - | `SmartUIExtensions.Set…` | Ap #M | Ap 1.5M |
| 436 | 6 | 2 | 5 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[PARALLAX] | ModuleManager: 8648 patches applied (14 %) :FOR[PARALLAX] |
| 437 | 6 | 1 | 3 | - | `DirectoryActionGroup.…` | Steam (#) | Steam (0) |
| 438 | 5 | 2 | 5 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :LAST[BURSTPQS] | ModuleManager: 76551 patches applied (99 %) :LAST[BURSTPQS] |
| 439 | 4 | 1 | 1 | - | `MapNode.UpdateCaption…` | SSC - Santiago VHF-Band: # dBi UHF-Band: # dBi S-Band: # dBi #° S, #° W | SSC - Santiago VHF-Band: 1.0 dBi UHF-Band: 3.0 dBi S-Band: … |
| 440 | 4 | 1 | 1 | - | `MainWindow.RenderWind…` | #-Lévy-#-g#caad#abcbc#b#fe#aa-x#_AVX_FMA | 2026091103-Lévy-157-g2caad1abcbc5b0fe52aa-x64_AVX_FMA |
| 441 | 4 | 2 | 4 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :FOR[HARDREMOVENONRO] | ModuleManager: 8399 patches applied (13 %) :FOR[HARDREMOVEN… |
| 442 | 4 | 2 | 4 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[HARDREMOVENONRP#] | ModuleManager: 8400 patches applied (13 %) :FOR[HARDREMOVEN… |
| 443 | 3 | 2 | 3 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[KOPERNICUS] | ModuleManager: 8613 patches applied (14 %) :FOR[KOPERNICUS] |
| 444 | 3 | 1 | 1 | - | `ScreenMessages.PostMe…` | Insufficient Avionics, Locking Controls (supports #t, vessel #t) | Insufficient Avionics, Locking Controls (supports 0.000t, v… |
| 445 | 3 | 2 | 3 | ModuleManager | `TMP_Text.TMPro.TMP_Te…`, `unknown` | ModuleManager: # patches applied (# %) :FOR[PARALLAXCONTINUED] | ModuleManager: 8650 patches applied (14 %) :FOR[PARALLAXCON… |
| 446 | 3 | 1 | 3 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :FOR[KOPERNICUS_VERSION] | ModuleManager: 8616 patches applied (14 %) :FOR[KOPERNICUS_… |
| 447 | 2 | 1 | 1 | - | `UIPartActionFloatEdit…` | #m | 3.000m |
| 448 | 2 | 2 | 1 | - | `SmartUIExtensions.Set…` | +#k | +1.0k |
| 449 | 2 | 1 | 1 | - | `MapNode.UpdateCaption…` | DSA # - Malargüe VHF-Band: # dBi UHF-Band: # dBi #° S, #° W | DSA 3 - Malargüe VHF-Band: 1.0 dBi UHF-Band: 3.0 dBi 35.78°… |
| 450 | 2 | 2 | 2 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :FOR[NONONRO] | ModuleManager: 8646 patches applied (14 %) :FOR[NONONRO] |
| 451 | 2 | 1 | 1 | - | `BaseFlightWidget.SetT…` | DSS # - GOLDSTONE | DSS 14 - GOLDSTONE |
| 452 | 2 | 1 | 1 | - | `MapNode.UpdateCaption…` | STDN - Quito VHF-Band: # dBi UHF-Band: # dBi #° S, #° W | STDN - Quito VHF-Band: 1.0 dBi UHF-Band: 3.0 dBi 0.62° S, 7… |
| 453 | 2 | 1 | 1 | - | `BaseFlightWidget.SetT…` | DSS # - CANBERRA | DSS 43 - CANBERRA |
| 454 | 2 | 1 | 1 | - | `BaseFlightWidget.SetT…` | NIP # - USSURIYSK | NIP 15 - USSURIYSK |
| 455 | 2 | 1 | 1 | - | `MapNode.UpdateCaption…` | DSS # - Goldstone VHF-Band: # dBi UHF-Band: # dBi S-Band: # dBi X-Band: # dBi K… | DSS 14 - Goldstone VHF-Band: 20.0 dBi UHF-Band: 34.0 dBi S-… |
| 456 | 2 | 1 | 1 | - | `UIPartActionChooseOpt…` | #x-Kerolox | 3.0x-Kerolox |
| 457 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZZZAFTERUNIVERSALSTORAGE] | ModuleManager: 62159 patches applied (97 %) :AFTER[ZZZAFTER… |
| 458 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZROWATERFALL_#_AUTOCONFIG] | ModuleManager: 52644 patches applied (95 %) :AFTER[ZROWATER… |
| 459 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :FOR[NONONRP#] | ModuleManager: 8647 patches applied (14 %) :FOR[NONONRP0] |
| 460 | 1 | 1 | 1 | ModuleManager | `TMP_Text.TMPro.TMP_Te…` | ModuleManager: # patches applied (# %) :AFTER[ZZZZZ-RP#TREE] | ModuleManager: 74842 patches applied (99 %) :AFTER[ZZZZZ-RP… |
| 461 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZROWATERFALL_#_MAINEFFECT] | ModuleManager: 54132 patches applied (95 %) :AFTER[ZROWATER… |
| 462 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[PARALLAX_STOCKPLANETTEXTURES] | ModuleManager: 8650 patches applied (14 %) :AFTER[PARALLAX_… |
| 463 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALISMOVERHAUL_MATERIALS_LATE] | ModuleManager: 23297 patches applied (49 %) :AFTER[REALISMO… |
| 464 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :FOR[TEXTURESUNLIMITED] | ModuleManager: 48404 patches applied (74 %) :FOR[TEXTURESUN… |
| 465 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[_BUILDMANAGER] | ModuleManager: 3161 patches applied (10 %) :AFTER[_BUILDMAN… |
| 466 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[UNITY.BURST] | ModuleManager: 48404 patches applied (74 %) :AFTER[UNITY.BU… |
| 467 | 1 | 1 | 1 | - | `ManeuverNodeEditorTab…` | T + #d, #h, #s | T + 56d, 22h, 51s |
| 468 | 1 | 1 | 1 | ModuleManager | `TMP_Text.TMPro.TMP_Te…` | ModuleManager: # patches applied (# %) :AFTER[SOL-VISUALS] | ModuleManager: 48377 patches applied (74 %) :AFTER[SOL-VISU… |
| 469 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[ZRORESCALEDRAGCUBES] | ModuleManager: 52203 patches applied (95 %) :AFTER[ZRORESCA… |
| 470 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[REALHEAT] | ModuleManager: 8939 patches applied (17 %) :AFTER[REALHEAT] |
| 471 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[PROFILEREALISMOVERHAUL] | ModuleManager: 8693 patches applied (14 %) :AFTER[PROFILERE… |
| 472 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :BEFORE[REALISMOVERHAUL_REACTIONWHEEL] | ModuleManager: 23297 patches applied (49 %) :BEFORE[REALISM… |
| 473 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :FOR[#_MULTIPLEMODULEINPARTAPI] | ModuleManager: 3161 patches applied (10 %) :FOR[0_MULTIPLEM… |
| 474 | 1 | 1 | 1 | ModuleManager | `unknown` | ModuleManager: # patches applied (# %) :AFTER[AAA_AJE] | ModuleManager: 3443 patches applied (12 %) :AFTER[AAA_AJE] |

## 4. 口径与判据

- **骨架**：把每段数字（`\d+(?:[.,]\d+)?`）换成 `#`；TMP 标签里的数字（`<color=#FF0000>`）不骨化。数字段为 0 或超过 8 段时判「无骨架」→ 该行降级 `bucket=candidate` 原样落盘，不丢。
- **h**：`fnv.hash_text(textnorm.normalize(骨架))`，与包内 `h` 同口径，所以一条骨架天然对应一个包内条目。
- **count**：实例 count 之和（会话内 miss 次数累加，不是屏幕上出现次数）；`sessions`：出现过该骨架的会话文件数。
- **callers/mods**：该骨架全部实例的并集（前 5 个、非 unknown 优先）。
- **样本（exact）**：该骨架下 count 最大的一条真实原文，用来核对「译了骨架能不能覆盖它」；平票取字典序最小，保证重跑逐字节相同。
- **纯符号骨架**：骨架一个 ASCII 字母都不含（`−#`、`#` 后跟零宽空格这类）时丢弃，不进产物也不送翻；丢弃的骨架数与实例数在总览表里记账，不会无声消失。
- **噪声类**：加载屏（`loading`）、ModuleManager、Profiler HUD（`profiler`/`fps`/`# ms`/`frame`）、RDR。只看剥掉 TMP 标签后的正文 —— 否则`<color=#FF0000>` 里的十六进制色值会误伤判定。
- 本文件全部数字由 `capture_ingest` 现算（读 capture 目录与产物行），无硬编码；骨架为 0 时本文件仍会生成，但表里会是「无」。

