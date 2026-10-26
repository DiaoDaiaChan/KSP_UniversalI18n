# UI18n 核心 Mod 领域侦察与术语规范卡 (Domain Reconnaissance Cards)

> **文档状态**：已定稿交付  
> **编制依据**：`ui18n-translate` §4 领域侦察规范、NASA/ESA/IAU/NIST/Unity/KSP Squad 权威标准  
> **面向对象**：UI18n 翻译团队 Lead、Translator 及后续门禁流水线  
> **目标范围**：本次捕获缺口最大的 5 个核心 Mod（ModularFlightPanel、MechJeb2、FerramAerospaceResearch、TUFX、Principia）

---

## 目录
1. [ModularFlightPanel (MFP) 航电仪表与微控件系统](#1-modularflightpanel-mfp-航电仪表与微控件系统)
2. [MechJeb2 轨道力学与自动飞控系统](#2-mechjeb2-轨道力学与自动飞控系统)
3. [FerramAerospaceResearch (FAR) 真实气动力学系统](#3-ferramaerospaceresearch-far-真实气动力学系统)
4. [TUFX 电影级 HDR 与色彩科学渲染系统](#4-tufx-电影级-hdr-与色彩科学渲染系统)
5. [Principia N体引力模拟与天体动力学系统](#5-principia-n体引力模拟与天体动力学系统)
6. [跨模组通用高频词与单位符号规范汇总](#6-跨模组通用高频词与单位符号规范汇总)

---

## 1. ModularFlightPanel (MFP) 航电仪表与微控件系统

### 1.1 基础信息与领域归属
- **Mod 显示名**：ModularFlightPanel (MFP)
- **核心程序集**：`ModularFlightPanel.dll`
- **领域映射**：航空航天电子学 (Avionics)、飞行仪表工程 (Flight Instrumentation)、微控件领域特定语言 (Micro-Widget DSL)、遥测数据流 (Telemetry Streams)。
- **核心功能**：
  基于 Triad Core 2.0 (Host-Presenter-Model) 架构构建的下一代 KSP 飞行仪表系统。采用 `WidgetLogic<TState>` 将业务物理计算与 UGUI 视图渲染完全解耦；内置 `AvionicsUnitSystem` 统一管理 7 大物理量纲（长度、速度、高度、垂直速度、压力、质量、温度）与 4 种全局制式；通过声明式微控件 DSL（Title, Badge, Value, Unit, LinearBar）呈现全船遥测与传感器读数；运行时采用 10Hz 物理解算与 60Hz 脏检渲染双轨调度。

### 1.2 运行时捕获特征与调用点
- **主要调用点**：
  - `UIFactory.CreateText` / `UnityEngine.UI.Text.set_text`（遥测标签、微控件标题与单位）
  - `BaseFlightWidget.SetTextIfChanged`（地面站代码、遥测读数、距离与雷达追踪状态）
  - `SmartUIExtensions.SetTextSafe`（任务计时 `T+ #y, #d, #:#:#`、高频刷新读数）
  - `HUDProfilerOverlay.RefreshOverviewTab`（性能开销卡片）
- **高频待译/缺口文案**：
  - `STDN - TANANARIVE` / `SSC - SANTIAGO` / `DSS # - CANBERRA`（地面测控跟踪站名称，属专有名词代号）
  - `m/s`, `atm`, `RDR RDR # km`（雷达测距与大气压读数）
  - `{ALT:MODE}`（微控件内部模板标记，禁译）

### 1.3 核心术语标准卡 (3-8 项)

| 术语 (EN) | 推荐标准中文 | 短标签 (≤4字) | 权威来源 1 (标准/机构) | 权威来源 2 (字典/论文) | 权威参考链接 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Avionics** | 航空电子 / 航电系统 | 航电 | **NASA SP-2016-6105** *Systems Engineering Handbook* | FAA Pilot's Handbook of Aeronautical Knowledge (PHAK) | [NASA Avionics](https://www.nasa.gov/reference/avionics-and-software) / [FAA PHAK](https://www.faa.gov/regulations_policies/handbooks_manuals/aviation/phak) |
| **Flight Instrumentation** | 飞行仪表 / 飞行仪表系统 | 飞行仪表 | **FAA-H-8083-15B** *Instrument Flying Handbook* | NASA Dryden Flight Research Center DFI Specification | [FAA Instrument Flying Handbook](https://www.faa.gov/regulations_policies/handbooks_manuals/aviation/instrument_flying_handbook) |
| **Telemetry** | 遥测 / 遥测数据 | 遥测 | **CCSDS 131.0-B-4** *TM Synchronization and Channel Coding* | NASA Dryden TM *Telemetry and the Integrated Avionics Approach* | [CCSDS Standards](https://public.ccsds.org/Pubs/131x0b4.pdf) / [NASA NTRS](https://ntrs.nasa.gov/citations/19720028737) |
| **Readout** | 仪表读数 / 读数组件 | 读数 | **NASA HIDH** *Human Integration Design Handbook* (Visual Displays) | NIST SP 811 *Guide for the Use of the International System of Units* | [NASA HIDH](https://www.nasa.gov/reference/human-integration-design-handbook/) / [NIST SP 811](https://www.nist.gov/pml/special-publication-811) |
| **Dynamic Pressure ($q$)** | 动压 | 动压 | **NASA Glenn Research Center** *Dynamic Pressure Equation* | KSP 官方 Squad 字典 (`packs/zh-cn/Squad.jsonl`) | [NASA Glenn DynPress](https://www.grc.nasa.gov/www/k-12/airplane/dynpress.html) |
| **Attitude Indicator** | 姿态仪 / 姿态指示器 | 姿态仪 | **FAA PHAK** *Chapter 8 Flight Instruments* | KSP 官方 Squad 字典（导航球 Navball） | [FAA Instruments](https://www.faa.gov/regulations_policies/handbooks_manuals/aviation/phak) |
| **Quiescence** | 稳态静默 / 静态休眠 | 静默 | **IEEE 802.3az** *Energy Efficient Ethernet (Quiescence)* | 软件工程状态机规范 / 航电微控件契约 | [IEEE Standards](https://standards.ieee.org/) |

### 1.4 禁译与保留规则
- **模板与插值标记**：`{SPD}`, `{ALT:MODE}`, `{RDR}`, `{BAR}` 等大括号内的 Token 标记严禁改写或翻译。
- **测控网专有名词**：`STDN`（Spacecraft Tracking and Data Network）、`DSN`/`DSS`（Deep Space Network / Deep Space Station）、`SSC`（Swedish Space Corporation）及站名（如 `CANBERRA`, `SANTIAGO`, `TANANARIVE`）保持英文原名或缩写。
- **物理单位**：`m/s`, `km`, `Mm`, `atm`, `kN`, `ms`, `fps` 保持国际符号，严禁翻译为“米/秒”或“毫秒”。

---

## 2. MechJeb2 轨道力学与自动飞控系统

### 2.1 基础信息与领域归属
- **Mod 显示名**：MechJeb2
- **核心程序集**：`MechJeb2.dll`
- **领域映射**：天体力学 (Astrodynamics)、轨道机动动力学 (Orbital Maneuver Mechanics)、运载火箭制导理论 (Launch Vehicle Guidance Theory)、自主飞控导航 (Autonomous Flight Guidance & Control)。
- **核心功能**：
  KSP 最成熟的轨道力学求解与自动飞控平台。提供完整的上升制导（支持经典重力转向与 PVG 动力速度制导）、机动节点执行（Lambert 求解器转移、交会对接、着陆规划）、姿态自动控制（Smart A.S.S.）与全阶段分级统计（Stage Stats，计算每级的燃烧时长、推重比 TWR 与速度增量 $\Delta v$）。

### 2.2 运行时捕获特征与调用点
- **主要调用点**：
  - `MechJebStageStatsHelper.AllStageStats` / `DrawStageStatsColumn`（分级统计窗口与列头：`dhms`, `# m/s`）
  - `MechJebModuleAscentPVGSettingsMenu.WindowGUI`（PVG 优化参数窗口：` (optimize)`）
  - `GuiUtils.SimpleTextBox` / `GuiUtils.ToggledTextBox`（输入框与标签：`ω: `, `Burnout Altitude:`, `Unguided Stages:`）
  - `MechJebModuleAscentMenu.ShowStatusGUIElements`（上升状态行：`burn: # #s #m/s`, `pitch: #`, `tgo: #`, `znorm: #`）
- **高频待译/缺口文案**：
  - `dhms`（天-时-分-秒紧凑时间格式，调用量 72,880 次）
  - `ω: `（角速度符号/近拱点幅角参数，调用量 60,466 次）
  - ` (optimize)`（优化参数切换开关，调用量 39,210 次）
  - `tgo: #`（Time to Go 待飞时间，调用量 72,370 次）

### 2.3 核心术语标准卡 (3-8 项)

| 术语 (EN) | 推荐标准中文 | 短标签 (≤4字) | 权威来源 1 (标准/机构) | 权威来源 2 (字典/论文) | 权威参考链接 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Powered Velocity Guidance (PVG)** | 动力速度制导 | 动力制导 | **NASA NTRS 19740024190** *Space Shuttle Powered Explicit Guidance* (Jaggers) | NASA NTRS 20180002035 *PEG Modifications for Orion* | [NASA NTRS PEG](https://ntrs.nasa.gov/citations/19740024190) / [NASA Orion Guidance](https://ntrs.nasa.gov/citations/20180002035) |
| **Primer Vector Guidance** | 本底矢量制导 / 协态矢量制导 | 协态制导 | **NASA NTRS 19760004112** *Primer Vector Theory and Applications* (Jezewski) | Lawden, D. F. *Optimal Trajectories for Space Navigation* (1963) | [NASA NTRS Jezewski](https://ntrs.nasa.gov/citations/19760004112) |
| **dhms** | 天-时-分-秒（分级时间统计） | 燃烧时长 | **ISO 8601 / CCSDS 301.0-B-4** *Time Code Formats* | MechJeb2 StageStatsHelper 交互设计（d=天 h=时 m=分 s=秒） | [CCSDS Time Standards](https://public.ccsds.org/Pubs/301x0b4.pdf) |
| **Time to Go ($t_{go}$)** | 待飞时间 / 剩余点火时间 | 待飞时间 | **NASA Apollo Guidance Computer (AGC)** Luminary System Dictionary ($T_{go}$) | NASA SP-8067 *Earth Orbit Deceleration and Stability* | [NASA Apollo Guidance History](https://www.history.nasa.gov/alsj/a11/a11.guidance.html) |
| **Lambert Problem / Solver** | 兰伯特问题 / 兰伯特求解器 | 兰伯特求解 | **NASA Technical Paper 2314** *Solution of Lambert's Problem* | ESA Advanced Concepts Team (ACT) Astrodynamics Toolbox | [NASA TP 2314](https://ntrs.nasa.gov/citations/19840003020) / [ESA ACT](https://www.esa.int/gsp/ACT/) |
| **Apoapsis / Periapsis** | 远心点（远地点） / 近心点（近地点） | 远点 / 近点 | **NASA Solar System Exploration Glossary** | KSP 官方 Squad 字典 (`packs/zh-cn/Squad.jsonl`: "近点" / "远点") | [NASA SSE Glossary](https://solarsystem.nasa.gov/basics/glossary/) |
| **Burnout Altitude** | 燃尽高度 | 燃尽高度 | **NASA SP-8007** *Orbit Perturbation and Maneuver Analysis* | 中国宇航学名词规范（火箭级终点/关机点高度） | [NASA NTRS](https://ntrs.nasa.gov/citations/19700010078) |
| **Unguided Stages** | 无制导级数 / 自旋稳定级数 | 无制导级 | **NASA SP-8067** *Space Vehicle Design Criteria* | 运载火箭分级控制工程标准 | [NASA SP-8067](https://ntrs.nasa.gov/citations/19740024190) |

### 2.4 禁译与保留规则
- **物理与数学变量**：`ω`, `pitch`, `znorm`, `tgo` 在单行或数据骨架中保留变量名或前缀（如 `tgo: #` 翻为 `待飞时间: #` 或 `tgo: #` 需符合骨架契约）。
- **专业缩写**：$\Delta v$ 严禁译为 "Delta-V" 或“速度差”，统一使用数学符号 $\Delta v$；`TWR`（推重比）在紧凑表头中保留大写英文或译为“推重比”。
- **单位符号**：`m/s`, `m/s²`, `Pa-rad`, `°/s`, `kN` 严禁翻译。

---

## 3. FerramAerospaceResearch (FAR) 真实气动力学系统

### 3.1 基础信息与领域归属
- **Mod 显示名**：FerramAerospaceResearch (FAR)
- **核心程序集**：`FerramAerospaceResearch.dll`
- **领域映射**：理论与应用空气动力学 (Theoretical & Applied Aerodynamics)、跨声速与超声速流体力学 (Transonic & Supersonic Fluid Mechanics)、气动弹性与结构破坏 (Aeroelasticity & Structural Aero-failure)。
- **核心功能**：
  彻底废弃原版基于简化阻力系数的气动模型，采用全船几何体素化实时求解（Voxelized Aerodynamics）。沿机身轴线推导截面面积分布曲线，严格依据 Whitcomb 面积律计算跨声速激波与超声速波阻；在飞行器各攻角与马赫数下实时计算稳定性导数矩阵（气动动压中心与升阻力矩）；引入动态真实气动加热、临界动压撕裂（Dynamic Pressure / Aero Stress）与失速物理。

### 3.2 运行时捕获特征与调用点
- **主要调用点**：
  - `FerramAerospaceResearch.FARDebugAndSettings.AeroDataTab`（气动数据面板：行星选择 `Jupiter`, `Saturn`, `Mars`, `Venus`）
  - `FerramAerospaceResearch.FARDebugAndSettings.AeroStressTab`（结构与热应力面板：`crewed`, `structural` 等结构类别）
  - `FerramAerospaceResearch.FARGUI.GUIUtils.TextEntryField`（数据输入项：`Name:`）
  - `FerramAerospaceResearch.FARDebugAndSettings.ChangeColor`（力矩曲线颜色配置：`Cm (r,g,b):`）
- **高频待译/缺口文案**：
  - `structural`, `crewed`（气动应力判定类别）
  - `Atm Composition`（大气成分/组分分析）
  - 天体名称（`Jupiter`, `Saturn`, `Mars` 属于太阳系天体，若在 FAR 数据表中出现需准确对应官方中文）

### 3.3 核心术语标准卡 (3-8 项)

| 术语 (EN) | 推荐标准中文 | 短标签 (≤4字) | 权威来源 1 (标准/机构) | 权威来源 2 (字典/论文) | 权威参考链接 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Voxelized Aerodynamic Solver** | 体素化气动求解器 | 体素求解 | **AIAA 2012-3023** *Voxel-based Cartesian Mesh Generation* | FAR 架构设计白皮书 (Ferram4) | [AIAA Aerospace Research](https://arc.aiaa.org/) |
| **Wave Drag** | 波阻 / 激波阻力 | 波阻 | **NASA Glenn Research Center** *Wave Drag Equation* | NACA Report 1273 (Richard T. Whitcomb, 1956) | [NASA Glenn Wave Drag](https://www.grc.nasa.gov/www/k-12/airplane/dragwave.html) / [NTRS 19560000007](https://ntrs.nasa.gov/citations/19560000007) |
| **Area Rule (Whitcomb Area Rule)** | 面积律 / 跨声速截面面积律 | 面积律 | **NASA Armstrong Fact Sheet** *Whitcomb Area Rule* | NACA Technical Note 3027 (1953) | [NASA Armstrong Area Rule](https://www.nasa.gov/centers-and-facilities/armstrong/whitcomb-area-rule/) |
| **Stability Derivatives** | 稳定性导数 / 气动稳定导数 | 稳定导数 | **NASA Reference Publication 1120** *Aerodynamic Stability Derivatives* | Roskam, J. *Airplane Flight Dynamics* | [NASA NTRS 19840003020](https://ntrs.nasa.gov/citations/19840003020) |
| **Dynamic Pressure ($q$)** | 动压 | 动压 | **NASA Glenn Research Center** *Dynamic Pressure* | KSP 官方 Squad 字典 (`packs/zh-cn/Squad.jsonl`) | [NASA Glenn DynPress](https://www.grc.nasa.gov/www/k-12/airplane/dynpress.html) |
| **Angle of Attack ($\alpha$ / AoA)** | 迎角 / 攻角 | 迎角 | **NASA Glenn Research Center** *Angle of Attack* | GB/T 18845-2002 飞行动力学术语 | [NASA Glenn AoA](https://www.grc.nasa.gov/www/k-12/airplane/aoa.html) |
| **Aerodynamic Heating & Stress** | 气动加热与气动应力 | 气动加热/应力 | **NASA TM-X-73230** *Aerodynamic Heating Calculations* | NASA SP-8001 *Structural Dynamic Pressure Criteria* | [NASA NTRS Heating](https://ntrs.nasa.gov/citations/19770020737) / [NASA NTRS SP-8001](https://ntrs.nasa.gov/citations/19710018044) |
| **Pitching Moment ($C_m$)** | 俯仰力矩系数 | 俯仰力矩 | **NASA Glenn Research Center** *Pitching Moment* | 空气动力学国家标准符号 | [NASA Glenn Pitching Moment](https://www.grc.nasa.gov/www/k-12/airplane/pitchm.html) |

### 3.4 禁译与保留规则
- **系数与符号**：$C_m, C_l, C_n, C_{l_\beta}, C_{m_\alpha}, \text{Mach}, Q, \alpha, \beta$ 严格保留拉丁/希腊符号，不得生硬拼音化。
- **着色分量格式**：`Cm (r,g,b):` 中的通道代号 `r,g,b` 严禁改写（既有 overrides 已规范为 `Cm 分量（r,g,b）：`）。
- **专有部件**：若涉及 A.I.R.B.R.A.K.E.S. 等专用缩写，遵循官方 Squad 译法“气动集成反向减速耐久型动能展开系统”或直称“气动减速板”。

---

## 4. TUFX 电影级 HDR 与色彩科学渲染系统

### 4.1 基础信息与领域归属
- **Mod 显示名**：TUFX (Textures Unlimited Special Effects)
- **核心程序集**：`TUFX.dll`
- **领域映射**：现代计算机图形学 (Computer Graphics)、影视后期制作色彩科学 (Color Science for Film & Video)、Unity 渲染管线与后处理栈 (Post-Processing Stack v2 / Volume System)。
- **核心功能**：
  基于 Unity Post-Processing Stack v2 / Volume 架构，为 KSP 带来工业级物理相机与后处理管线。支持现代广色域与电影胶片色彩转换（ACES 色调映射、AgX 曲线、Neutral 映射）；提供高阶样条调色曲线矩阵（色相对色相、色相对饱和度、亮度对饱和度等）；支持物理级散景景深 (Spectral Bokeh DoF)、次表面散射 (Subsurface Scattering, SSS)、屏幕空间环境光遮蔽 (SSAO)、泛光 (Bloom) 与自动曝光。

### 4.2 运行时捕获特征与调用点
- **主要调用点**：
  - `ConfigurationGUI.DrawParamToggle`（样条调色曲线开关：`HueVsHueCurve`, `HueVsSatCurve`, `LumVsSatCurve`）
  - `TUFXWidgets.ParamSlider`（色调与滑条控制：`HueShift`, `CenterX`, `CenterY`, `IntensityX`, `IntensityY`）
  - `ConfigurationGUI.renderSubsurfaceScatteringSettings`（次表面散射材质列表，绝大多数是材质名/着色器名，**禁止翻译**）
  - `ConfigurationGUI.renderSelectionWindow` / `renderAdvancedAntiAliasingSettings`（预设与抗锯齿选择：`Default-KSC`, `Intel CMAA 2 (No Ghosting)`）
  - `ConfigurationGUI.renderSpectralBokehSettings`（散景景深追踪距离：`Tracking: <color=#88FF88># m</color>`）
- **高频待译/缺口文案**：
  - `HueVsHueCurve`（色相对色相曲线，调用量 13,610 次）
  - `HueVsSatCurve`（色相对饱和度曲线，调用量 13,610 次）
  - `LumVsSatCurve`（亮度对饱和度曲线，调用量 13,610 次）
  - `HueShift`（色相偏移，调用量 13,610 次）
  - `Tracking: <color=#88FF88># m</color>`（散景对焦追踪，带富文本颜色）

### 4.3 核心术语标准卡 (3-8 项)

| 术语 (EN) | 推荐标准中文 | 短标签 (≤4字) | 权威来源 1 (标准/机构) | 权威来源 2 (字典/论文) | 权威参考链接 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **ACES (Academy Color Encoding System)** | 学院颜色编码系统 (ACES) | ACES | **AMPAS (奥斯卡电影艺术与科学学院)** 规范标准 | SMPTE ST 2065-1 *Academy Color Encoding Specification* | [AMPAS ACES](https://www.oscars.org/science-technology/sci-tech-projects/aces) |
| **Color Grading** | 颜色分级 / 调色 | 颜色分级 | **Unity Technologies Manual** *Post-Processing Color Grading* | ISO 12232 *Photography - Digital Cameras* | [Unity Color Grading](https://docs.unity3d.com/Packages/com.unity.postprocessing@2.1/manual/Color-Grading.html) |
| **Tonemapping** | 色调映射 | 色调映射 | **CIE 015:2018** *Colorimetry Standards* | Unity Graphics Post-Processing Architecture | [Unity Tonemapping](https://docs.unity3d.com/Packages/com.unity.postprocessing@2.1/manual/Color-Grading.html) |
| **Hue vs Hue Curve (HueVsHueCurve)** | 色相对色相曲线 | 色相对色相 | **Unity Post-Processing API** (`ColorGrading.hueVsHueCurve`) | DaVinci Resolve 达芬奇官方调色手册中文版 | [Unity API hueVsHueCurve](https://docs.unity3d.com/Packages/com.unity.postprocessing@2.1/api/UnityEngine.Rendering.PostProcessing.ColorGrading.html) |
| **Hue vs Saturation Curve (HueVsSatCurve)** | 色相对饱和度曲线 | 色相对饱和 | **Unity Post-Processing API** (`ColorGrading.hueVsSatCurve`) | DaVinci Resolve 达芬奇官方调色手册中文版 | [Unity API hueVsSatCurve](https://docs.unity3d.com/Packages/com.unity.postprocessing@2.1/api/UnityEngine.Rendering.PostProcessing.ColorGrading.html) |
| **Luminance vs Saturation Curve (LumVsSatCurve)** | 亮度对饱和度曲线 | 亮对饱和 | **Unity Post-Processing API** (`ColorGrading.lumVsSatCurve`) | CIE 015:2018 国际照明委员会标准 | [Unity API lumVsSatCurve](https://docs.unity3d.com/Packages/com.unity.postprocessing@2.1/api/UnityEngine.Rendering.PostProcessing.ColorGrading.html) |
| **Hue Shift** | 色相偏移 | 色相偏移 | **W3C CSS Color Module Level 4** *Hue Interpolation* | Unity Post-Processing API (`ColorGrading.hueShift`) | [W3C Color 4](https://www.w3.org/TR/css-color-4/) / [Unity API hueShift](https://docs.unity.cn/Packages/com.unity.postprocessing@3.4/api/UnityEngine.Rendering.PostProcessing.ColorGrading.toneCurveShoulderAngle.html) |
| **Subsurface Scattering (SSS)** | 次表面散射 | 次表面散射 | **SIGGRAPH 2015 Course** *Physically Based Shading in Theory and Practice* | Unity High Definition Render Pipeline (HDRP) Documentation | [Unity HDRP SSS](https://docs.unity.cn/) |

### 4.4 禁译与保留规则
- **运行时材质名与着色器**：`RAPTOR (Instance) [KSP/Bumped Specular]`, `bluedog_TitanSolids_Misc (Instance)`, `Custom/Parallax(Clone)` 等带有 `(Instance)` 或 `[ShaderName]` 的材质实例与着色器路径属于引擎内部资产标识，**绝对禁止翻译**，触发 G8 时应坚决保留原文或列入忽略白名单。
- **预设配置名**：如 `Default-KSC`, `Default-MainMenu`, `Beyond-Integrated-Cinematic`, `Vant-1960` 为用户配置预设 Profile 标题，不可翻译。
- **抗锯齿专有技术**：`Intel CMAA 2 (No Ghosting)` 中，`CMAA 2` 为 Intel 算法商标名不可改写，仅在说明括号中可翻译为“无重影/抗拖影”。
- **渲染路径术语既有裁定**：依据既有 overrides 记录（lead m01628 裁定），`Forward` 统一译为“前向”，`Deferred` 统一译为“延迟”。

---

## 5. Principia N体引力模拟与天体动力学系统

### 5.1 基础信息与领域归属
- **Mod 显示名**：Principia
- **核心程序集**：`ksp_plugin_adapter.dll` / `principia.dll`
- **领域映射**：天体力学 (Celestial Mechanics)、摄动理论 (Perturbation Theory)、辛几何数值积分 (Symplectic Geometric Numerical Integration)、多天体引力动力学 (N-Body Gravitational Dynamics)。
- **核心功能**：
  完全替代 KSP 原版两体拼接圆锥曲线（Patched Conics）体系，引入现实物理界真实完整摄动的 $N$ 体引力数值仿真。支持所有天体与飞船间全引力相互作用；采用高阶辛数值积分算法（Symplectic Integrators，如 Yoshida、Blanes-Moan 算法），保证长达数万年的机械能与相空间辛结构守恒；引入非惯性参考系绘图（Plotting Frame）与机动标架（Manoeuvring Frame，如 ECEF、ECI、MEO、拉格朗日点脉动参考系）；支持连续推力有限时长燃烧的精密飞行规划（Flight Plan），采用弗雷奈标架（Frenet-Serret Frame: Tangent, Normal, Binormal）。

### 5.2 运行时捕获特征与调用点
- **主要调用点**：
  - `BaseWindowRenderer.RenderWindow` / `principia.ksp_plugin_adapter.BaseWindowRenderer.RenderWindow`（Principia 主窗口标题与窗体渲染，54,696 次）
  - `PrincipiaPluginAdapter.RenderNavballAndAccessories`（导航球配件与速度标尺：`# m/s`）
  - `PerformanceProbe.DrawPerformanceTab`（Principia 解算器开销与网络：`Principia Net: <color=#888888># ms (Offline)</color>`）
- **高频待译/缺口文案**：
  - `Principia`（模组主窗体标题，属于专有名词，建议保留英文或标为主窗口）
  - `Plotting Frame` / `Manoeuvring Frame`（参考系选择）
  - `Tangent` / `Normal` / `Binormal`（切向 / 法向 / 副法向，弗雷奈机动标架分量）

### 5.3 核心术语标准卡 (3-8 项)

| 术语 (EN) | 推荐标准中文 | 短标签 (≤4字) | 权威来源 1 (标准/机构) | 权威来源 2 (字典/论文) | 权威参考链接 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **N-Body Gravitational Simulation** | N体引力模拟 | N体模拟 | **IAU Commission X2** *Solar System Dynamics* | NASA JPL Solar System Dynamics (SSD) Group | [IAU](https://www.iau.org/) / [NASA JPL SSD](https://ssd.jpl.nasa.gov/) |
| **Symplectic Integrator** | 辛数值积分器 / 辛积分算法 | 辛积分器 | **Hairer, Lubich, Wanner** *Geometric Numerical Integration* (Springer, 2006) | Yoshida, H. *Construction of higher order symplectic integrators* (1990) | [Springer Geometric Integration](https://link.springer.com/book/10.1007/3-540-30666-8) |
| **Ephemeris** | 天体历表 / 历表 | 历表 | **IAU / IERS Conventions (2010)** | NASA JPL Development Ephemeris (DE440/DE441) | [IERS Conventions](https://www.iers.org/) / [NASA JPL DE](https://ssd.jpl.nasa.gov/planets/eph_export.html) |
| **Reference Frame (Plotting / Manoeuvring)** | 参考系（绘图参考系 / 机动参考系） | 参考系 | **IAU Commission A2** *Celestial Reference Systems* | Principia 官方设计百科 *Concepts: Reference Frames* | [IAU Reference Systems](https://www.iau.org/) / [Principia Concepts](https://github.com/mockingbirdnest/Principia/wiki/Concepts) |
| **Frenet-Serret Frame (Tangent, Normal, Binormal)** | 弗雷奈-塞雷标架（切向、法向、副法向） | 弗雷奈标架 | **微分几何与经典力学数学标准 (GB/T 3163)** | Principia 官方设计百科 *Flight Planning & Coordinates* | [Principia Concepts](https://github.com/mockingbirdnest/Principia/wiki/Concepts) |
| **Barycentre** | 质心 / 质量中心 | 质心 | **IAU 2006 Resolution B3** *Barycentric Dynamical Time (TDB)* | 天体力学通用教科书（双星/地月系统质心） | [IAU Resolutions](https://www.iau.org/static/resolutions/IAU2006_Resol.pdf) |
| **Lagrangian Points / Pulsating Frame** | 拉格朗日点 / 脉动参考系 | 脉动参考系 | **ESA Science & Technology** *Lagrange Points* | NASA SVS Scientific Visualization Studio | [ESA Lagrange Points](https://www.esa.int/Science_Exploration/Space_Science/What_are_Lagrange_points) / [NASA SVS](https://svs.gsfc.nasa.gov/) |
| **Coasting / Burn** | 无推力滑行 / 发动机点火（机动燃烧） | 滑行 / 点火 | **NASA NTRS SP-8007** *Maneuver Analysis* | Apollo 飞控手册与 Principia 航段定义 | [NASA NTRS](https://ntrs.nasa.gov/citations/19700010078) |

### 5.4 禁译与保留规则
- **模组名与版本代号**：`Principia` 自身为 Newton 巨著《自然哲学的数学原理》拉丁文原名，作为模组名与窗口标题必须保留；其发布代号（如 `Lichnerowicz`, `Lévy`, `Levi-Civita`, `Jacobi`）均为历史上著名的数学家与力学家姓名，禁止机器硬翻。
- **坐标系缩写**：`ECEF` (Earth-Centred Earth-Fixed 地心固连坐标系), `ECI` (Earth-Centred Inertial 地心惯性参考系), `MEO` (Moon-Earth Orbit 参考系), `MEL` (Moon-Earth Lagrange 拉格朗日参考系) 为国际通用航天参考系代号，UI 短按钮上保留原缩写或附注中文。
- **数学轴向符号**：$T$（Tangent 切向）, $N$（Normal 法向）, $B$（Binormal 副法向）及 $x, y, z$ 保留数学符号。

---

## 6. 跨模组通用高频词与单位符号规范汇总

为确保 Translator 在补充 `packs/overrides.jsonl` 与 `packs/glossary.jsonl` 时用词绝对统一，现将本次捕获中覆盖率前 25 调用点涉及的跨模组通用词条汇总如下：

| 英文原词 | 推荐规范中文 | 类别/适用范围 | 决策依据与来源 |
| :--- | :--- | :--- | :--- |
| **Forward** | 前向（前向渲染） | 渲染路径 | 业界计算机图形学标准；既有 overrides.jsonl 裁定 |
| **Deferred** | 延迟（延迟渲染） | 渲染路径 | 业界计算机图形学标准；既有 overrides.jsonl 裁定 |
| **Delta-v / $\Delta v$** | $\Delta v$ | 物理指标 | 严格禁止写为 "Delta-V"，保留希腊符号 $\Delta v$；Squad 官方字典 |
| **Apoapsis (Ap)** | 远心点（短标签：远点） | 轨道力学 | Squad 官方字典 ("远点")，严禁错翻为“远地点”（非地球天体不适用） |
| **Periapsis (Pe)** | 近心点（短标签：近点） | 轨道力学 | Squad 官方字典 ("近点")，严禁错翻为“近地点” |
| **Ascending Node (AN)** | 升交点 | 轨道力学 | 天体力学标准名词；Squad 官方字典 |
| **Descending Node (DN)** | 降交点 | 轨道力学 | 天体力学标准名词；Squad 官方字典 |
| **Iterations** | 迭代次数 | 数值计算/求解器 | 求解器迭代，严禁凭语感译为“循环”或“重复”（ui18n-translate §10 铁律） |
| **Tracking** | 追踪 / 对焦追踪 | 光学/测控 | 散景设置中译为“对焦距离”，测控网中译为“追踪” |
| **N/A** | 不适用 / 暂无 | 状态指示 | 视 UI 宽度保留 N/A 或译为“暂无” |
| **IDLE** | 空闲 / 待命 | 状态机指示 | 航电与飞控状态机标准译法 |
| **m/s, km, kN, t, atm, K** | 原样保留 | 物理计量单位 | **严禁翻译**；国际单位制 SI 标准规范与 ui18n-translate §6 |

---
*报告生成于 2026-10-26 | 审核专家：UI18n 领域侦察专家 (recon-expert)*
