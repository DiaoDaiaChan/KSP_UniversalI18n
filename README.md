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

## 当前状态（开发快照 1.5.0 · **尚无发行版**）

> **发行状态：GitHub 上没有任何 Release，也没有任何版本 tag。** 2026-10-26 按维护者判断，删除了 v1.0.0–v1.5.0 的全部 Release 与 tag —— 现阶段完成度还不够称为「发行版」，版本号只描述内部开发快照，不代表可发布质量。要装请按下一节从仓库取文件，**不要找 release 下载链接**（已经不存在）。

| 项目 | 数字 |
|---|---|
| 拦截点（hook 目标） | **17** 个：`UI.Text.set_text`、`TMP_Text.set_text`、`GUILayout.DoLabel/DoButton/DoRepeatButton/DoToggle/DoWindow`、`GUI.Label/Box/Button/RepeatButton/Toggle(4 参)/Toggle(5 参)/DoWindow(title)/DoModalWindow/DoButtonGrid`、`ScreenMessages.PostMessage` |
| 字典 | **97** 个包 / **14,954** 条（cfg 级 41 包 9,990 条 + DLL/配置缓存级 56 包 4,964 条；其中 **486** 行是数字骨架） |
| 配置文本覆盖率（`packs/pool.jsonl` 口径） | 待译 12,792 条里 **9,397** 条已覆盖 = **73.46%**（v1.3.0 是 70.93%）。**这是源侧待译串口径，v1.5 起不再当验收数字**（见下面 render 一行） |
| 屏幕覆盖率（29 局实测） | 该翻的 3,129 条里 **265** 条已中文 = **8.47%**（v1.2 出包那一代是 14 条 / 0.45%）；全量口径 2.53%——这一指标要等下一轮游戏内实测才会更新 |
| render 覆盖率（v1.5 真相层口径，**唯一承诺**） | 运行时自报 `[UI18n] render coverage: hit=… miss=… distinct=… ratio=…`。**尚未有 1.5 出包的实测值** —— 要装 1.5 真跑一局；明细落 `<KSP>/PluginData/UI18n/render-<UTC stamp>.json`，报告由 `python -m i18n_tools.render_coverage` 出 |
| 人工译文表 | `packs/overrides.jsonl` **3,736** 条（v1.4 一夜之间从 283 条涨上来，覆盖零件标题 / 零件描述 / 制造商 / 捕获到的屏幕标签） |
| 单测 | C# **658** 例全绿；python 工具链 selftest **298** 项全绿 |
| 游戏内验证 | round 10（2026-10-24，v1.1.0 那对 DLL）：`installed 17/17 patches`、`parseErrors=0`、`entries=11089`、`error lines: 0` |

## 安装

1. 确保已装 Harmony2 与 ModuleManager。
2. 把 `src/UI18n.Runtime/GameData/UI18n/` 整个复制进 `<KSP>/GameData/`，再把构建出的两个 DLL 放进 `<KSP>/GameData/UI18n/Plugins/`（构建见文末；`dist/UI18n-<ver>.zip` 是本地出包产物，不对外发布）。
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
harvest_cfgcache（本地，读 ModuleManager.ConfigCache，v1.4）
        │  运行时看到的是「打完 MM 补丁后的合成文本」，正文常在 @description 里；
        │  所以这条通道按字段（title/description/displayName/manufacturer）直读补丁结果，
        │  与上面的原定义口径互为补充，不重复（已覆盖的 h 直接剔除）
        ▼
capture_ingest（把玩家会话的缺命中 capture 归并：去重 → 抽骨架 → **只打优先级标签，不再丢正文**，v1.5）
        │  `bucket` 是优先级 candidate > template > never，三档**都留档**：判错的串还捞得回来复查
        │  纯数字模板塌缩成骨架后和静态串一起进包（v1.3）；时间戳开头的日志行标 never（v1.4 的判据）
        ▼
packs/candidates.jsonl（v1.5：五条通道并成**一张**候选表 —— 静态四路 + 运行时捕获都往这里写）
        │  行 schema 固定、排序确定（`bucket` 序 → `h`），同输入同字节；`covered` 标出「字典里已有译文」
        ▼
packs/overrides.jsonl（人工译文表，v1.4 扩到 3,736 条）
        │  命中即直出、永不联网：术语一致的短标签（Instructions: / Destination:）、
        │  零件标题与描述、制造商名都从这里走；模型只在没命中时才被调用
        ▼
translate（CI，LLM 批量翻译 + 断点续跑缓存）
        │  九道硬门禁：占位符/富文本/格式串多重集、长度比、术语锁、keep 判定、非法字符、
        │  空译文、骨架 # 个数一致；批异常时已命中的覆盖项不丢（v1.4）
        ▼
resolve_keyrefs（把 #autoLOC_* 键引用解析成官方中英对照行，带歧义守卫与长度比门禁）
        ▼
drop/packindex/audit（删冗余键行、生成清单与覆盖率、审计 89 个包）
        ▼
packs/<lang>/<mod>.jsonl  ──►  GitHub Actions 六道门禁 ──►  发行包
```

CI 见 `.github/workflows/i18n.yml`，每次 push 跑三个 job：`gates`（python 侧六道门禁：单测 + golden + journal + packindex + audit + selftest，不联网不写仓库）、`code`（Core 编译 + 811 例单测；Runtime 要真实 KSP 安装，进不了 CI）、`translate`（只在手动触发时跑）。

## 你也可以投稿（把你这台机器上的英文串交回来）

字典不是凭空想的：**玩家机器上真的渲染过的英文串**是最可靠的输入。所以插件允许你把自己那份交回来，全程自愿、可撤回：

1. 游戏里点 AppLauncher 的 **UI18n** 按钮 → 勾「同意把未翻译的原文与 mod 列表投给翻译仓库」；
2. 点「导出投稿包」→ 生成 `PluginData/UI18n/submissions/ui18n-submission-<id>.json`，你可以自己先打开看（里面只有 mod 名、原文、游戏语言；存档名 / 飞船名 / Kerbal 名 / 路径在导出前三步脱敏就被丢掉了，丢弃条数写在 `redactions` 字段里）；
3. 点「打开投稿页面」→ 把那个 json **拖进** issue 模板的框里，提交；
4. 仓库侧跑 `tools/accept-submission-issue.ps1 -Issue <号>` 收下（`api submit accept` 负责校验 / 算 id / 幂等 / 按 mod 拆文件 / 归并候选表 / 写审计 journal），回一行评论；
5. 之后由 CI 走翻译与门禁 → 进包 → 你下次启动点更新就能看到。

想知道某个 mod 的中文是哪个包给的、想关掉它：面板里按 mod 勾选（`PluginData/UI18n/prefs.json`），关掉的包连解析都不做。

投稿契约（bundle 形态、id 算法、脱敏三步、落盘路径、六种退出码、三条已知边界）冻结在 [docs/10-交互API与投稿回路.md](docs/10-交互API与投稿回路.md) §6 —— 发出去的产物必须永远能收，所以这里的格式不以「下次更新插件」为转移。

## 为什么还会看到英文

**能翻的**：配置文件文本、官方语言包已有的键、DLL 里能静态回溯到 UI 调用点的字面量，以及一级**骨架** —— 把 `"Mass: 12.5 t"` 这类数字模板塌缩成骨架后翻译、运行时回填数字（v1.3）。

装了这个包还看到英文，先分清是哪一类：一类是我们以前真漏了（v1.5 已改到不再静默丢失），一类是机制上碰不到（产品范围，不算遗漏）。

### A. 以前会漏的三种情况：v1.5 已改为「不再静默丢失」

| 结构性来源 | 以前是怎么漏的 | v1.5 的改法 |
|---|---|---|
| 字典键对不上屏幕上的串 | 字典键是**源文件原文**的哈希，屏幕上是 ModuleManager 打完补丁后的**合成文本** —— 一句被 `@description` 改写过就查不到 | 以「渲染出口真实出现过的串」为准收集，静态抽取降级为预热 |
| 判错就永久消失 | 捕获端一串「像噪声就丢」的布尔判决，判错的串连正文都不留，只留一个计数器 | 捕获端只打**优先级标签**（candidate / template / never），正文一律留档 |
| 五条通道各自为政 | cfg / loc / IL / ConfigCache / capture 各有分母，没人答得出「这台机器上还剩多少英文」 | 并成**一张** `packs/candidates.jsonl`，覆盖率只认运行时自报的 render 口径 |

这一类正是以前被写成「翻不了」的东西 —— **枚举成员名**（`Enum.GetNames(typeof(Toolbar))` 拿到的是元数据里的名字，静态扫描一个字节都看不见）和**运行时拼接**（`"Reload " + noun` 在调用点才成句）：现在它们由运行时捕获兜住，兜不住也能在候选表里查到。

自己就能查的判据是启动日志那一行：

```text
[UI18n] render coverage: hit=<n> miss=<n> distinct=<n> ratio=<0.0000>
```

`ratio` = 这次渲染过的 distinct 文本里命中译文的占比。它只统计**渲染出口真实交出、长度 ≥ 2 的文本**，所以是唯一对得上的数字；`packs/pool.jsonl` 的 73.46% 是**源侧待译串**口径，两者不能互相换算。明细落 `<KSP>/PluginData/UI18n/render-<UTC stamp>.json`，`pwsh tools/collect-diagnostics.ps1` 会一起带走。

### B. 机制上碰不到的：产品范围，不进 render 覆盖率的分母

| 机制 | 为什么翻不到 |
|---|---|
| **TMP 逐段拼接** | TMP 的显示文本常由多段（不同字体 / 材质 / 富文本节点）拼成，一次 `set_text` 只拿到其中一段，段与段之间没有可译的整句；拼出来的完整句子在源码里不存在 |
| **纹理内文字** | 零件贴图、界面图标、行星贴图上的字是**像素**，不经过任何字符串通道 |
| **逐帧程序化读数** | 每帧用数值拼出来的读数（`Mass Flow: 2.2 ton/s`）在源码里没有「一句话」；只有骨架通道能覆盖其中模板化的那部分，骨架都没命中时保留英文 —— 这是取舍 |
| 玩家自己起的名字 | 飞船名、存档名：翻了会改到玩家的数据 |

这三类**不进 render 覆盖率的分母**，所以它们不会把 ratio 压低到看起来像「还在漏」。一句话判断法：这句英文**能完整复制出来、下次出现还一模一样** → 属于 A，值得报；**是多段拼的 / 印在贴图里的 / 每帧都在变的** → 属于 B，不在修的范围。

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
| `packs/` | 字典源：`pool.jsonl`（候选池）、`glossary.jsonl`（官方译文记忆）、`zh-cn/*.jsonl`（成品包，含 `"p":1` 骨架行）、`index.json`（清单）、`capture-candidates.jsonl`（会话捕获归并出的骨架候选，v1.3）、`candidates.jsonl`（**v1.5：五条通道唯一的候选表**，只打优先级标签、不丢正文） |
| `tools/i18n_tools/` | 采集 / 翻译 / 解析 / 审计 / 清单的 python 工具链；v1.3 加 `capture_ingest`（会话捕获归并）、`skeleton`（骨架抽取）、`screen_coverage`（屏幕覆盖率）；v1.5 加 `candidates`（候选单一表）、`golden_gate`（golden set 门禁）、`render_coverage`（render 覆盖率报告） |
| `tools/collect-diagnostics.ps1` | 排障一把梭：把一个玩家的现场收成单个 zip（v1.3） |
| `tests/UI18n.Core.Tests/` | xunit v3 单测：19 个测试类 / **647** 例；夹具在 `Data/`（`pool-sample.jsonl`、`skeleton-golden.jsonl` 331 行） |
| `tests/golden/` | golden set：`rendered-strings.txt` 一行一条**用户报过的英文原文**（现 19 条）。过滤器或阈值一改，任一条被丢掉 / 没进候选表 / 没有译文就直接红（v1.5，见 `tools/i18n_tools/golden_gate.py`） |
| `docs/` | 设计文档：总体设计（00）、缺陷审查（01）、探针（02）、进度追踪（03）、翻译流水线（04）、原生性与性能（05）、工程骨架与构建（06）、真相层设计契约（08）与实现说明（09） |

## 许可

MIT，见 [LICENSE](LICENSE)。

第三方：Harmony（MIT）、ModuleManager（MIT）、KSP 与 Squad 的官方本地化文本版权归 Squad / Private Division，本仓库仅以「键 → 官方译文」的对照形式引用，用于避免重复翻译同一句话。
