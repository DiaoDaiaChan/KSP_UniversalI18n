# KSP_UniversalI18n

> 适用于 KSP 模组的运行时本地化层。无需修改模组二进制或配置文件，不依赖原作者提供 `Localization/` 配置。

- 游戏内目录 / 程序集 / 配置节点：`GameData/UI18n`、`UI18n.Core.dll` + `UI18n.Runtime.dll`、`UI18N`（内部代号 `UI18n`）
- 支持版本：KSP 1.12.x（实测 1.12.5.3190 zh-cn）
- 依赖项：[Harmony2](https://github.com/KSPModdingLibs/HarmonyKSP)（CKAN identifier `Harmony2`）、[ModuleManager](https://github.com/sarbian/ModuleManager)

## 解决的问题

KSP 生态中有大量模组直接在代码中硬编码英文字符串：

```csharp
GUILayout.Label("Vessel mass");
GUI.Window(id, rect, DrawWindow, "Debug Toolbar");
text.text = "All Input Locks Cleared";
```

这些字符串未注册在 `Localization/` 中，也不是 `#autoLOC_` 键，传统基于配置文件替换的汉化方案无法覆盖。本模组在文本渲染出口处进行拦截：将待显示的字符串与词典匹配，命中时替换为译文，未命中时保持原样显示。

## 与其他汉化方案的区别

| | 传统汉化补丁 | 本模组 |
|---|---|---|
| 改动对象 | ModuleManager 补丁改写 cfg 中的 `title` / `description` | 运行时拦截 uGUI / TMP / IMGUI 的文本写入 |
| 覆盖范围 | `PART` 等配置文本 | 任意 UI 文本，包括模组 DLL 中硬编码的内容 |
| 多语言切换 | 需替换中文文件 | 按 `Packs/<lang>/` 目录切换，词典与代码分离 |
| 需要作者配合 | 是（需作者提取词条键） | 否 |
| 异常表现 | 词条错位、错误翻译写入存档 | 未命中保持原文，不修改存档数据 |

## 当前状态（开发快照 1.7.0）

> 当前尚未发布正式 Release 或版本 Tag。版本号仅表示内部开发快照状态。如需体验，请参考下一节说明从源码构建或获取文件。

| 项目 | 数据 |
|---|---|
| 拦截点（hook 目标） | 17 个：`UI.Text.set_text`、`TMP_Text.set_text`、`GUILayout.DoLabel/DoButton/DoRepeatButton/DoToggle/DoWindow`、`GUI.Label/Box/Button/RepeatButton/Toggle(4 参)/Toggle(5 参)/DoWindow(title)/DoModalWindow/DoButtonGrid`、`ScreenMessages.PostMessage` |
| 词典规模 | 97 个包 / 14,991 条（cfg 级 41 包 10,015 条，DLL/配置缓存级 56 包 4,976 条；其中 492 行为数字骨架） |
| 配置文本覆盖率（`packs/pool.jsonl` 口径） | 待译 12,792 条中已覆盖 9,397 条，覆盖率 73.46%（源侧静态文本口径） |
| 屏幕覆盖率（29 局实测） | 可译 3,129 条中已汉化 265 条，覆盖率 8.47%（全量口径 2.53%） |
| render 覆盖率（实际渲染口径） | 运行时上报 `[UI18n] render coverage: hit=… miss=… distinct=… ratio=…`。单局读数参考：hit=245 / miss=4 / distinct=249 / ratio=0.9839（2026-10-26 样本，数据落盘于 `render-20261026-090049.json`）；明细保存至 `<KSP>/PluginData/UI18n/render-<UTC stamp>.json`，由 `python -m i18n_tools.render_coverage` 生成报告 |
| 人工译文表 | `packs/overrides.jsonl` 共 3,736 条，覆盖零件标题、零件描述、制造商及屏幕捕获标签 |
| 单元测试 | C# 单测 1084 例全部通过（Runtime 需真实 KSP 依赖）；Python 工具链自检通过 |
| 游戏内实测 | round 10（2026-10-24）：`installed 17/17 patches`、`parseErrors=0`、`entries=11089`、`error lines: 0` |

## 安装

1. 确保已安装 Harmony2 与 ModuleManager。
2. 将 `src/UI18n.Runtime/GameData/UI18n/` 复制至 `<KSP>/GameData/`，将构建出的两个 DLL 放至 `<KSP>/GameData/UI18n/Plugins/`（构建说明见文末）。
3. 启动游戏，日志中应包含以下内容：

```
[UI18n] v1.1.0+<commit> starting (harmony id com.universali18n.runtime)
[UI18n] dictionary load: files=89, entries=11089, parseErrors=0, 65 ms
[UI18n] installed 17/17 patches
[UI18n] ready: patches=17/17, dict entries=11089, language=zh-cn, version=1.1.0+<commit>
```

排查未翻译文本时，可参考 `src/UI18n.Runtime/README.md` §7.3 的顺序表；若未出现 `[UI18n]` 相关日志，说明模组未成功加载。

## 配置

配置文件位于 `GameData/UI18n/Config.cfg`，修改后重启生效（插件在 `Startup.Instantly` 读取一次）。主要配置项如下：

- **开关**：`Enabled`、`ForceOverride`（非中文语言环境下是否启用翻译）
- **词典与更新**：`MaxEntries`、`CheckForUpdates` / `AutoDownload` / `UpdateBaseUrl` / `UpdateChannel` / `UpdateCheckDays`（在线更新配置）
- **字体**：`FontMode`（`Off` / `OsFont` 默认 / `Bundled`）、`OsFontName`、`BundledFontPath`、`TmpMode`（`Auto` / `KeepSource` / `Force`）
- **诊断**：`LogLevel`（`Error` / `Warn` / `Info` 默认 / `Verbose`）、`HitSampleLimit`、`CaptureMisses`、`CaptureMaxEntries`、`CaptureMaxTemplateEntries`、`ShadowStyleDraw`、`PatternMatching`（数字骨架匹配通道，默认 `True`）

若开启 `CaptureMisses = True`，词典中未命中的 UI 文本将聚合写入 `<KSP>/PluginData/UI18n/capture-<时间戳>.jsonl`，仅保存在本地，不会自动上传。

如需收集排障日志，可运行诊断脚本打包环境信息：

```powershell
pwsh tools\collect-diagnostics.ps1     # 输出单个 zip：包含 KSP.log [UI18n] 日志段、配置、语言包指纹、捕获及更新状态
```

启动日志包含两行 `diag:` 诊断记录（`session=… key=value`），其中首条在语言门禁校验前输出，用于排查未生效原因。

## 工作原理

```
mod 代码 ──► GUILayout.Label / GUI.Window / TMP_Text.set_text / …（17 个出口）
                    │
                    ▼  Harmony 前缀：ref 参数（ref string / ref GUIContent）
              Translator.Translate(原文)
                    │  ① 归一化（NFKC + 折叠空白）
                    ▼
              FNV-1a 64 位哈希 → DictPack 查表（第一级：精确匹配）
                    │  ② ctx → site → global 三层回退，命中 keep 即终止
                    ▼
        未命中 → TextSkeleton 将数字归约为 <<n>> 骨架并进行二次查询
                    │  ③ 第二级：骨架匹配（KeyLevel.Pattern），默认开启
                    ▼
        命中 → 就地改写参数/内容（记录命中样本）
        未命中 → 保持原文（可选：写入本地 capture 记录）
```

词典存储为 JSONL 格式，单行数据结构如下：

```json
{"h":"6c8a4b0b1f0e2d3c","n":"Normalized source","s":"原文","t":"译文","mod":"Squad"}
```

字段说明：`h` 为 `fnv1a64(normalize(s))` 计算出的 16 位小写十六进制哈希值，`n` 为归一化后的英文原文。运行时直接通过 64 位整数哈希查表，不进行文本解析或路径拼接，降低每帧拦截的开销。

骨架行包含 `"p":1` 字段，以归约后的骨架为键（例如 `<<n>> kg`，槽位上限为 8）；译文使用 `<<1>>…<<8>>` 占位回填，回填失败则按未命中处理。

### 词典优先级

| 层级 | 键构成 | 适用场景 |
|---|---|---|
| ctx | 程序集 + 类型 + 成员 + 序号 + 文本哈希 | 同一英文词在不同控件上下文中需对应不同译法 |
| site | 程序集 + 文本哈希 | 特定模组内的专属译法（默认层级） |
| global | 文本哈希 | 通用词条，对所有模组生效 |

骨架匹配（pattern）在精确匹配未命中后触发，仅检索带有 pattern 标识的词典项（详见 `docs/06 §5.5`）。

优先保留原生本地化：对于已自带 `Localization/` 配置的模组，本模组仅补充缺失项，不覆盖已有键值。

## 词典生成流水线

```
harvest（本地环境，需 KSP 安装）
  ├─ 扫描 GameData 下所有 .cfg 的 title/description/displayName/tags
  ├─ 反编译 DLL，提取流向 UI 调用点的 IL 字符串字面量
  └─ 提取官方 Localization/*.cfg 作为术语记忆表（优先于模型生成）
        │
        ▼
harvest_cfgcache（读取 ModuleManager.ConfigCache）
        │  按字段读取打完 MM 补丁后的合成文本，与原始定义互为补充
        ▼
capture_ingest（合并客户端会话的未命中捕获文本）
        │  统一标记优先级（candidate / template / never），正文完整留档；
        │  纯数字模板归约为骨架条目
        ▼
packs/candidates.jsonl（统一候选表，汇总静态提取与运行时捕获）
        │  确定性排序（bucket 优先级 → h），记录词典覆盖状态
        ▼
packs/overrides.jsonl（人工译文表，直接命中输出，无需模型处理）
        │  覆盖规范短标签、零件标题与描述、制造商名称等
        ▼
translate（CI 流程，LLM 批量翻译与断点缓存）
        │  格式门禁：占位符/富文本/格式串多重集一致性、长度比、术语表约束等
        ▼
resolve_keyrefs（将 #autoLOC_* 键引用解析为官方双语对照项）
        │
        ▼
drop/packindex/audit（清理冗余条目、生成索引与覆盖率报告、审计语言包）
        │
        ▼
packs/<lang>/<mod>.jsonl  ──►  GitHub Actions 门禁校验  ──►  生成分发包
```

CI 流程见 `.github/workflows/i18n.yml`：
- `gates`：Python 质量门禁（自检、术语审计、包索引校验、cfg 导出校验、netkan 规范校验等，离线运行）。
- `code`：Core 编译与单元测试。
- `translate`：基于 `workflow_dispatch` 手动触发批量翻译任务。
- `refresh`：定期触发翻译刷新并通过 PR 提交审阅。

## 词条反馈与投稿

运行时渲染捕获的文本可用于补充和完善词典。插件提供本地导出与反馈功能：

1. 打开游戏内 AppLauncher 的 **UI18n** 面板，勾选「同意将未翻译的原文与模组列表导出用于完善词典」；
2. 点击「导出投稿包」，生成本地文件 `PluginData/UI18n/submissions/ui18n-submission-<id>.json`（仅包含模组名、原文与游戏语言；存档名、载具名、乘员名及本地路径在导出前会自动脱敏排除，并在 `redactions` 字段记录排除项数）；
3. 点击「打开投稿页面」，将导出的 JSON 文件附加在 Issue 模版中提交；
4. 维护端通过 `tools/accept-submission-issue.ps1 -Issue <编号>` 提取校验并纳入候选池；
5. 经 CI 翻译与门禁校验后并入语言包，在后续更新中分发。

如需停用特定模组的汉化，可在设置面板中按模组取消勾选（保存于 `PluginData/UI18n/prefs.json`），已停用的包在运行时不进行加载解析。

投稿契约、脱敏规则与接口规范详见 [docs/10-交互API与投稿回路.md](docs/10-交互API与投稿回路.md) §6。

## 未翻译文本的分类说明

系统支持翻译的内容包括：配置文件文本、官方语言包词条、DLL 中可静态分析至 UI 调用点的字面量，以及数字骨架模板（例如将 `"Mass: 12.5 t"` 归约为骨架后回填数值）。

若在游戏中仍出现未翻译英文，可对照以下分类：

### A. 动态或补丁合成文本（属于可收录范围）

| 表现来源 | 产生机制 | 处理方案 |
|---|---|---|
| 文本被 ModuleManager 补丁改写 | 静态哈希基于原始配置文件，而界面显示的是 `@description` 等补丁改写后的合成文本 | 运行时在渲染出口进行捕获并收录至候选表 |
| 规则过滤失误 | 早期捕获端对过滤条目直接丢弃，无正文留档 | 统一保留正文并标记优先级标签（candidate / template / never） |
| 通道数据分散 | 各通道统计基数不一致 | 统一汇集至 `packs/candidates.jsonl`，覆盖率以实际渲染数据为准 |

包括通过反射获取的枚举名称（如 `Enum.GetNames(typeof(Toolbar))`）与运行时动态拼接语句，此类文本通过运行时出口捕获收录。

运行时渲染覆盖率可通过启动日志查看：

```text
[UI18n] render coverage: hit=<n> miss=<n> distinct=<n> ratio=<0.0000>
```

指标说明：`ratio` 表示本次会话中渲染过的独立非空文本（长度 ≥ 2）命中译文的比例。明细数据记录在 `<KSP>/PluginData/UI18n/render-<UTC stamp>.json`，可由 `pwsh tools/collect-diagnostics.ps1` 收集。

### B. 渲染机制限制（不计入未翻译覆盖率）

| 机制类型 | 限制说明 |
|---|---|
| **TMP 逐段拼接** | TextMeshPro 的显示文本常由多个富文本片段或不同样式节点组合，单次 `set_text` 仅接收单一片段，缺乏完整上下文 |
| **纹理内置文字** | 零件贴图、界面图标或天体贴图上的文字属于贴图像素，不经过字符串渲染接口 |
| **逐帧程序化读数** | 实时计算的瞬态读数（如 `Mass Flow: 2.2 ton/s`）无静态完整语句，未命中数字骨架时保留英文 |
| 玩家自定义名称 | 载具名、存档名等用户数据，不作替换以避免污染存档 |

判定方式：若英文文本为固定完整的字符串，属于可反馈补充的范围；若为实时拼装片段、绘制于贴图中或每帧变化的瞬态数值，则属于底层渲染机制限制。

**TMP 降级表现**：KSP 1.12 的 TextMeshPro 采用位图图集机制，无法在运行时直接加载 .ttf 生成字形。若未找到包含中文字符的 TMP 字体资产，在 `TmpMode=Auto` 模式下将保留英文以避免方块乱码。uGUI 文本则通过系统字体注入正常渲染中文。

## 从源码构建

环境要求：.NET SDK 10 与 KSP 托管程序集（路径配置于 `src/UI18n.Runtime/UI18n.Runtime.csproj` 中的 `KspManagedDir`）。

```powershell
dotnet build UI18n.sln -c Release          # 构建 Core（net472 + net10.0）与 Runtime（net472）
pwsh tools\run-tests.ps1                    # 运行 C# 单测与门禁检查
cd tools; python -m i18n_tools.selftest     # 运行 Python 工具链自检
python -m i18n_tools.audit                  # 词典完整性审计
pwsh src\UI18n.Runtime\deploy.ps1 -Install  # 安装至本机 KSP 环境
pwsh src\UI18n.Runtime\build-release.ps1 -Version 1.1.0   # 输出发布包至 dist/
```

## 目录结构

| 路径 | 内容 |
|---|---|
| `src/UI18n.Core/` | 核心通用逻辑：哈希计算、文本归一化、词典加载、语言门禁、包发现、捕获过滤与数字骨架归约（无 Unity 依赖） |
| `src/UI18n.Runtime/` | KSP 模组运行时：Harmony 补丁、字体解析、配置读取与在线更新 |
| `packs/` | 词典数据：`pool.jsonl`（候选池）、`glossary.jsonl`（术语对照）、`zh-cn/*.jsonl`（语言包，含骨架匹配项）、`index.json`（索引清单）、`candidates.jsonl`（统一候选表） |
| `tools/i18n_tools/` | 数据提取、机器翻译、校验审计工具链 |
| `tools/collect-diagnostics.ps1` | 本地环境排障信息打包脚本 |
| `tests/UI18n.Core.Tests/` | xUnit 单元测试集 |
| `tests/golden/` | 黄金用例集与回归验证测试 |
| `docs/` | 架构设计、实现细节、规范说明文档 |

## 许可证

本模组采用 MIT 许可证，详见 [LICENSE](LICENSE)。

第三方依赖遵循各自开源许可：Harmony（MIT）、ModuleManager（MIT）。KSP 与 Squad 官方本地化文本版权归 Squad / Private Division 所有，仓库中仅作为术语对照引用。
