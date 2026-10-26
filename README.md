# KSP_UniversalI18n

> 给**任何** KSP mod 加中文的运行时本地化层 —— 不改原 mod 一个字节，不要求作者配合，不要求作者写过 `Localization/`。

- 游戏内目录 / 程序集 / 配置节点：`GameData/UI18n`、`UI18n.Core.dll` + `UI18n.Runtime.dll`、`UI18N`（内部代号 **UI18n**，不改）
- 支持：KSP 1.12.x（实测 1.12.5.3190 zh-cn）
- 依赖：[Harmony2](https://github.com/KSPModdingLibs/HarmonyKSP)（CKAN identifier `Harmony2`）、[ModuleManager](https://github.com/sarbian/ModuleManager)

## 它解决什么问题

KSP 生态里有大量 mod 直接把英文字符串写进代码：

```csharp
GUILayout.Label("Vessel mass");
GUI.Window(id, rect, DrawWindow, "Debug Toolbar");
text.text = "All Input Locks Cleared";
```

这些串既不在 `Localization/` 里，也不是 `#autoLOC_` 键，任何「配置文件替换」类汉化都碰不到它们。本模组换一条路：**在文本渲染的出口处拦截**，把屏幕上要显示的字符串查字典，命中就替换成译文，未命中就当没装过。

## 与其他汉化方案的区别

| | 传统汉化补丁 | 本模组 |
|---|---|---|
| 改动对象 | ModuleManager 补丁改写 cfg 里的 `title` / `description` | 运行时拦截 uGUI / TMP / IMGUI 的文本写入 |
| 覆盖范围 | `PART` 等配置文本 | 任意 UI 文本，**包括 mod DLL 里硬编码的** |
| 换语言 | 硬编码中文 | 按 `Packs/<lang>/` 目录切换，字典与代码分离 |
| 需要作者配合 | 是（要作者把文本抽成键） | 否 |
| 出问题时的形态 | 词条错位、错翻译被写进存档 | 未命中即原文，永不产生「翻错但改不回来」的状态 |

## 当前状态（v1.3.0 出包）

| 项目 | 数字 |
|---|---|
| 拦截点（hook 目标） | **17** 个：`UI.Text.set_text`、`TMP_Text.set_text`、`GUILayout.DoLabel/DoButton/DoRepeatButton/DoToggle/DoWindow`、`GUI.Label/Box/Button/RepeatButton/Toggle(4 参)/Toggle(5 参)/DoWindow(title)/DoModalWindow/DoButtonGrid`、`ScreenMessages.PostMessage` |
| 字典 | **96** 个包 / **11,561** 条（cfg 级 41 包 9,990 条 + DLL 字面量级 55 包 1,571 条；其中 **102** 行是数字骨架） |
| 屏幕覆盖率（29 局实测） | 该翻的 3,129 条里 **265** 条已中文 = **8.47%**（v1.2 出包那一代是 14 条 / 0.45%）；全量口径 2.53% |
| 单测 | C# **637** 例全绿；python 工具链 selftest **280** 项全绿 |
| 游戏内验证 | round 10（2026-10-24，v1.1.0 那对 DLL）：`installed 17/17 patches`、`parseErrors=0`、`entries=11089`、`error lines: 0` |

## 安装

1. 确保已装 Harmony2 与 ModuleManager。
2. 把发行包里的 `GameData/UI18n/` 整个复制进 `<KSP>/GameData/`。
3. 启动游戏。日志里应出现：

```
[UI18n] v1.1.0+<commit> starting (harmony id com.universali18n.runtime)
[UI18n] dictionary load: files=89, entries=11089, parseErrors=0, 65 ms
[UI18n] installed 17/17 patches
[UI18n] ready: patches=17/17, dict entries=11089, language=zh-cn, version=1.1.0+<commit>
```

排查「这句英文为什么没翻」看 `src/UI18n.Runtime/README.md` §7.3 的顺序表；一句都看不到 `[UI18n]` 就是没装上。

## 配置

改 `GameData/UI18n/Config.cfg`，重启生效（插件在 `Startup.Instantly` 只读一次）。文件里每个键都有中文注释，按用途分四组：

- **开关**：`Enabled`、`ForceOverride`（非中文环境也翻）
- **字典**：`MaxEntries`、`CheckForUpdates` / `AutoDownload` / `UpdateBaseUrl` / `UpdateChannel` / `UpdateCheckDays`（联网更新，v1.2）
- **字体**：`FontMode`（`Off` / `OsFont` 默认 / `Bundled`）、`OsFontName`、`BundledFontPath`、`TmpMode`（`Auto` / `KeepSource` / `Force`）
- **诊断**：`LogLevel`（`Error` / `Warn` / `Info` 默认 / `Verbose`，v1.3）、`HitSampleLimit`、`CaptureMisses`、`CaptureMaxEntries`、`CaptureMaxTemplateEntries`（模板档单独限额，v1.3）、`ShadowStyleDraw`、`PatternMatching`（数字骨架通道，默认 `True`；关掉就没有第二级查表，v1.3）

`CaptureMisses = True` 会把字典里没有的 UI 文本聚合写到 `<KSP>/PluginData/UI18n/capture-<时间戳>.jsonl`，**只写本地文件、绝不上传**（上传只受 `ReportOptIn` 控制，且当前版本没有任何上传代码）。

日志被刷屏就设 `LogLevel = Warn`（只影响日志，不影响翻译）。要报障时一条命令收齐现场：

```powershell
pwsh tools\collect-diagnostics.ps1     # → 单个 zip：KSP.log 的 [UI18n] 段 + 配置 + 语言包指纹 + capture/更新状态 + summary.txt
```

启动日志里有两行 `diag:`（`session=… key=value`），第一条在**语言门禁之前**，所以「装了但没翻译」时也会出现。

## 工作原理

```
mod 代码 ──► GUILayout.Label / GUI.Window / TMP_Text.set_text / …（17 个出口）
                    │
                    ▼  Harmony 前缀：ref 参数（ref string / ref GUIContent）
              Translator.Translate(原文)
                    │  ① 归一化（NFKC + 折叠空白）
                    ▼
              FNV-1a 64 位哈希 → DictPack 查表（第一级：精确）
                    │  ② ctx → site → global 三层回退，命中 keep 即终止
                    ▼
        未命中 → TextSkeleton 把数字塌缩成 <<n>> 骨架再查一次
                    │  ③ 第二级：pattern（KeyLevel.Pattern），默认开
                    ▼
        命中 → 就地改写参数/内容（并记录样本）
        未命中 → 什么都不做（+ 可选：写进 capture 文件）
```

字典行是 JSONL，一行一条：

```json
{"h":"6c8a4b0b1f0e2d3c","n":"Normalized source","s":"原文","t":"译文","mod":"Squad"}
```

`h` = `fnv1a64(normalize(s))` 的 16 位小写 hex，`n` = 归一化后的源文。运行时不解析原文、不拼路径，只用一个整数查表 —— 这是能做到「每帧几千次调用仍无感」的原因。

骨架行多一个 `"p":1` 字段，键是塌缩后的骨架（如 `<<n>> kg`），槽位数上限 8；译文用 `<<1>>…<<8>>` 回填，填不上就按未命中处理（v1.3）。

### 三层优先级

| 层 | 键的构成 | 用途 |
|---|---|---|
| ctx | 程序集 + 类型 + 成员 + 序号 + 文本哈希 | 同一个词在不同控件里要翻成不同意思时用 |
| site | 程序集 + 文本哈希 | 只在某个 mod 里成立的译法（默认层） |
| global | 纯文本哈希 | 通用词，任何 mod 都适用 |

骨架（pattern）不算第四层 —— 它就是同一套查表在「精确未命中」之后再跑一遍，只查 pattern 档（v1.3，见 `docs/06 §5.5`）。

**永远优先原生 i18n**：作者自己写过 `Localization/` 的 mod，本模组只填空缺，不覆盖它的键。

## 字典是怎么来的

```
harvest（本地，需要真实 KSP 安装）
  ├─ 扫 GameData 下所有 .cfg 的 title/description/displayName/tags
  ├─ 反编译 DLL，按调用点回溯 IL 字面量（只取流向 UI sink 的）
  └─ 官方 Localization/*.cfg 提取为「官方译文记忆」（2,126 条，优先于模型）
        │
        ▼
capture_ingest（把玩家会话的缺命中 capture 归并：去重 → 分档 never/template/candidate → 抽骨架）
        │  纯数字模板进 pattern 档，塌缩成骨架后和静态串一起进包（v1.3）
        ▼
translate（CI，LLM 批量翻译 + 断点续跑缓存）
        │  八道硬门禁：占位符/富文本/格式串多重集、长度比、术语锁、keep 判定、非法字符、空译文
        ▼
resolve_keyrefs（把 #autoLOC_* 键引用解析成官方中英对照行，带歧义守卫与长度比门禁）
        ▼
drop/packindex/audit（删冗余键行、生成清单与覆盖率、审计 89 个包）
        ▼
packs/<lang>/<mod>.jsonl  ──►  GitHub Actions 四道门禁 ──►  发行包
```

CI 见 `.github/workflows/i18n.yml`（`gates` 四道门禁每次 push 都跑，不联网不写仓库；`translate` 只在手动触发时跑）。

## 能力边界

**能翻**：配置文件文本、官方语言包里已有的键、DLL 里能静态回溯到 UI 调用点的字面量；再加一级**骨架** —— 把 `"Mass: 12.5 t"` 这类数字模板塌缩成骨架后翻译、运行时回填数字（v1.3）。

**翻不了（机制上不可达，不是没做）**：

1. **枚举成员名** —— `Enum.GetNames(typeof(Toolbar))` 得到的是元数据里的名字，字节码堆里根本没有这几条字符串，任何静态扫描都看不见，运行时才知道（实测 KSP 调试工具栏 11 个页签里 9 个属于此类）。
2. **运行时拼接** —— `"Reload " + noun` 这类在调用点才成句的文本，任何一端单独拿出来都不是屏幕上那句话。
3. **贴图/网格/音频里的文字**，以及玩家自己起的名字（飞船名、存档名）。

**已知降级**：KSP 1.12 的 TMP 是位图图集版，运行时无法从 .ttf 造字 —— 找不到含汉字的 TMP 字体资产时，`TmpMode=Auto` 会让那部分文本**保留英文**而不是显示方框。uGUI 走系统字体注入（`FontMode=OsFont`），正常出中文。

## 从源码构建

需要 .NET SDK 10 与 KSP 的托管程序集（路径在 `src/UI18n.Runtime/UI18n.Runtime.csproj` 的 `KspManagedDir`）。

```powershell
dotnet build UI18n.sln -c Release          # Core 双目标（net472 + net10.0）+ Runtime（net472）
pwsh tools\run-tests.ps1                    # C# 单测（本机 dotnet test 的 testhost 会崩，见 docs/06 §4.2）
cd tools; python -m i18n_tools.selftest     # python 工具链自检
python -m i18n_tools.audit                  # 字典审计（89 个包）
pwsh src\UI18n.Runtime\deploy.ps1 -Install  # 装进本机 KSP（会逐文件校验 sha256）
pwsh src\UI18n.Runtime\build-release.ps1 -Version 1.1.0   # 出包到 dist/（11 条硬断言 + 来源标记）
```

## 目录

| 路径 | 内容 |
|---|---|
| `src/UI18n.Core/` | 纯 .NET 逻辑：哈希、归一化、字典、语言门禁、包发现、缺命中捕获、数字骨架塌缩（`Text/TextSkeleton.cs`，v1.3）（无 Unity 依赖，可单测） |
| `src/UI18n.Runtime/` | KSP 插件本体：Harmony patch、字体注入、配置读取、联网更新 |
| `packs/` | 字典源：`pool.jsonl`（候选池）、`glossary.jsonl`（官方译文记忆）、`zh-cn/*.jsonl`（成品包，含 `"p":1` 骨架行）、`index.json`（清单）、`capture-candidates.jsonl`（会话捕获归并出的骨架候选，v1.3） |
| `tools/i18n_tools/` | 采集 / 翻译 / 解析 / 审计 / 清单的 python 工具链；v1.3 加 `capture_ingest`（会话捕获归并）、`skeleton`（骨架抽取）、`screen_coverage`（屏幕覆盖率） |
| `tools/collect-diagnostics.ps1` | 排障一把梭：把一个玩家的现场收成单个 zip（v1.3） |
| `tests/UI18n.Core.Tests/` | xunit v3 单测：18 个测试类 / **637** 例；夹具在 `Data/`（`pool-sample.jsonl`、`skeleton-golden.jsonl` 331 行） |
| `docs/` | 设计文档：总体设计、翻译流水线、原生性与性能、工程骨架与构建、进度追踪 |

## 许可

MIT，见 [LICENSE](LICENSE)。

第三方：Harmony（MIT）、ModuleManager（MIT）、KSP 与 Squad 的官方本地化文本版权归 Squad / Private Division，本仓库仅以「键 → 官方译文」的对照形式引用，用于避免重复翻译同一句话。
