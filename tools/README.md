# UI18n 数据流水线（tools/i18n_tools）

把 `GameData` 下 101 个 mod 的可译文本抽成候选池 `packs/pool.jsonl`，同时产出统计清单
`packs/inventory.json` 与官方双语术语表 `packs/glossary.jsonl`。

纯 python 3.14 标准库（re / json / os / sys / unicodedata / argparse / tempfile / hashlib / time），
**不联网、不装任何第三方包**；全量 6,212 个 cfg 抽取耗时约 3 s，术语表约 0.1 s。

## 0 环境与命令

依赖：python ≥ 3.10（本机 3.14.7）。GameData 默认由仓库位置推断（`tools/i18n_tools/` 往上四层 = 游戏根，
再拼 `GameData`），也可用环境变量 `UI18N_GAMEDATA` 或命令行 `--gamedata DIR` 覆盖。

仓库根已迁到 `C:\Users\43701\Documents\github\UniversalI18n`；旧的 `D:\Kerbal Space Program_newmod\UniversalI18n`
现在是 junction（仍能用，但脚本一律按新根写）。**KSP 安装目录仍是 `D:\Kerbal Space Program_newmod`**，与本仓库位置无关，
`GameData` 采样路径属环境事实，不要跟着仓库搬家改。

在 `C:\Users\43701\Documents\github\UniversalI18n\tools` 目录下执行：

| 目的 | 命令 | 期望 |
| --- | --- | --- |
| 自测（279 项断言） | `python -m i18n_tools.selftest` | 退出码 0，末行 `[selftest] 全部通过（279 项）`（干净工作树；本地若有 `_scratch/corpus/pool.jsonl` 真值则多一条逐字节比对，成 280 项） |
| 抽候选池 | `python -m i18n_tools.harvest` | 写 `..\packs\pool.jsonl` + `..\packs\inventory.json`，退出码 0 |
| 抽池并逐行校验既有真值 | `python -m i18n_tools.harvest --verify ..\_scratch\corpus\pool.jsonl` | 末行 `[verify] 我的 12792 行 / 对照 12792 行 → 完全一致`，退出码 0 |
| 双语术语表 | `python -m i18n_tools.glossary` | 写 `..\packs\glossary.jsonl`，2,126 行，退出码 0 |
| 术语表含只有中文的键 | `python -m i18n_tools.glossary --include-zh-only` | 12,028 行（en 为空串） |

通用选项：`--gamedata DIR`、`--out FILE`、`--quiet`；harvest 另有 `--inventory FILE`、`--verify FILE`、`--limit N`；
glossary 另有 `--short-max-words`、`--short-min-len`。

Windows 控制台是 GBK，若直接重定向输出中文可能 `UnicodeEncodeError`：各模块 main 已
`sys.stdout.reconfigure(encoding="utf-8", errors="replace")`，脚本外部建议另设 `$env:PYTHONIOENCODING='utf-8'`。

仓库里另有一个 **非 python** 的脚本 `tools/run-tests.ps1`（C# 单测入口，不属数据流水线）：
`pwsh tools\run-tests.ps1` 先构建再跑 `UI18n.Core.Tests.dll`，退出码原样透传。

排障还有一个 **非 python** 脚本 `tools/collect-diagnostics.ps1`（v1.3）：把一个玩家的现场收成单个 zip ——
KSP.log 里含 `[UI18n]` 的行 + 末 400 行、`GameData/UI18n` 的配置与版本、DLL 与语言包的逐文件 sha256、
`PluginData/UI18n` 下最新 5 个 `capture-*.jsonl` 与 `update-state.json`，外加机读的 `summary.txt`。
**只读 KSP 目录、不联网、不上传**；不收 DLL/语言包本体（哈希足以定位版本）。

```powershell
pwsh tools\collect-diagnostics.ps1                       # 默认 KSP 根 D:\Kerbal Space Program_newmod，输出到 <KSP>\_diag
pwsh tools\collect-diagnostics.ps1 -Tag after-update -MaxRuntimeFiles 10
```
**本机不要用 `dotnet test`**——VSTest testhost 启动即崩 `Win32Exception (5): 拒绝访问`（parent-exit 监视打不开父进程句柄），
一条用例都不会执行；详见 `docs/06-工程骨架与构建.md` §4.2。

## 1 产物与真实数字（本机实测，可复现）

### 1.1 packs/pool.jsonl —— 候选池

12,792 行 / 3,049,123 字节，sha256 `FEF515EABDC017BBA2D9ADFE0E157E0B604F287FEC1DC53AB9905211A9DADD4C`；
紧凑 JSON（`separators=(",", ":")`、`ensure_ascii=False`）、LF 换行、**末行不带换行**。

| 字段 | 含义 |
| --- | --- |
| `id` | 1..12792 连续编号（loc 行 1..2948，cfg 行 2949..12792） |
| `h` | `FNV-1a64(text)` 的 **16 位小写 hex** 字符串（见下“顺序即契约”） |
| `text` | 归一化后的英文原文 |
| `src` | `loc`（来自 Localization 文件）或 `cfg`（来自 cfg 文本字段） |
| `mod` | 相对 GameData 的第一段路径（GameData 根下的游离 cfg 用文件名当 mod） |
| `key` | **仅 loc 行**：`#LOC_xxx` 形式的本地化键 |
| `field` | **仅 cfg 行**：`title` / `description` / `displayName`（保留文件里的原始大小写写法） |
| `ph` | 含占位符（`<<1>>`、`{0}`、`%s` 这类） |
| `rt` | 含富文本标签（Unity 的 `<b>` / `<color=…>` 等） |
| `dg` | 含数字 |
| `path` | 含文件路径（扩展名 .cfg/.dll/.mu/.dds/.png） |
| `url` | 含 URL |
| `len` | **字符**长度，等于 `len(text)` |

实测组成：loc 2,948 + cfg 9,844 = 12,792；特征 占位符 **35** / 富文本 **436** / 含数字 **7,214** / 路径 **0** / URL **1**；
长度桶 `2-10` 1,208、`11-30` 5,374、`31-80` 3,470、`81-200` 1,603、`200+` 1,137（合计 12,792）。

### 1.2 packs/inventory.json —— 统计清单（3,676 字节）

| 键 | 实测值 | 含义 |
| --- | --- | --- |
| `generatedAt` | 运行时刻 | ISO 时间戳 |
| `cfgFiles` | 6,212 | GameData 下全部 `*.cfg` |
| `locFiles` | 116 | 其中 Localization 文件 |
| `modsWithLocalization` | 28 | 含 Localization 文件的 mod 数 |
| `locKeysTotal` | 8,975 | en-us 键总数 |
| `locKeysCovered` | 5,644 | 有非空 zh-cn 译文 |
| `locKeysMissingZh` | 3,331 | 缺 zh（= loc 候选） |
| `cfgFieldHits` | 15,581 | cfg 文本字段命中数 |
| `zhCoveredAssignments` | 2,408 | 值本身已是中文的 cfg 赋值（按出现次数） |
| `zhCoveredUnique` | 2,224 | 同上按唯一文本 |
| `poolSize` | 12,792 | 候选池行数 |
| `skip` | `dup 4461 / search-tags 1597 / already-cjk 32 / empty 13 / pure-number 8 / too-short 6 / url-only 1` | 各过滤器命中 |
| `poolBreakdown` | 同 1.1 的组成与特征位 | —— |
| `modStats` | 20 条 | 缺 zh 最多的 mod（`mod/en/zh/missing`，按 missing 降序），RealismOverhaul 1138/0/1138、SXT 802/0/802、TriggerTech 550/0/550 … |
| `topMods` | 20 条 | 池内条数 Top20，RP-1 3403、RealismOverhaul 2047、Squad 1004、SXT 922、Sol-Configs 533 … |
| `tokenEstimate` | prompt 479,026 / output 319,350 / total 798,376 | 自定口径（见 §4） |

`skip` 合计 6,118，与候选数完全自洽：候选 18,910 = 池 12,792 + skip 6,118。

### 1.3 packs/glossary.jsonl —— 官方双语术语表

2,126 行 / 467,238 字节，每行 `{"key","en","zh","short"}`，按 `dictionary.cfg` 的文档序输出，紧凑 JSON + LF + 末行无换行。

```
{"key":"#autoLOC_168875","en":"Found lying by the side of the road","zh":"从路边捡来的","short":false}
{"key":"#autoLOC_238018","en":"Take Data","zh":"取走数据","short":true}
```

实测统计：Squad 共 533 个 cfg，其中 **443** 个带行尾英文注释；注释命中 3,290 处 → **唯一英文键 2,126**（同键不同英文 12 处，取首次出现）；
`dictionary.cfg` 的 zh-cn 有 **12,028** 个键；**配对 2,126 / 只有中文 9,902 / 只有英文 0**；`short`（英文 ≤5 词且 ≥4 字符）**887** 条。

## 2 口径（每条都说为什么这么做）

### 2.1 什么算 Localization 文件

判据：**相对 GameData 的路径里存在目录段名为 `Localization`（大小写不敏感）**；mod 名 = 路径第一段。
该规则精确给出 116 文件 / 28 mod，与真值一致。反例（都不算 loc 文件）：
`GameData\PatchManager\Lang\*.cfg`（9 个，也是合法语言文件）、
`GameData\Sol-Configs\Configs\**\*-Localization.cfg`（53 个，文件名带 Localization 但不在 Localization 目录）。
若改成“文件内容里出现 Localization 节点”判定会得 181 文件 / 32 mod，与真值不符。

### 2.2 按 mod 目录求语言并集（坑 1）

KSP 的语言是**按语言分文件**的：`en-us.cfg` / `zh-cn.cfg`，还会拆成 `en-us-Engines.cfg`、`en-us-Squad.cfg` 这类。
所以必须先按 mod 目录把同语言的所有文件并成一个 dict（**后出现的赋值覆盖前面的**：`bucket[key] = value`；
依据 `#roBE3Title` 的真值是后出现的 `BE-3U` 而不是 `BE-3PM`），再算“en 中存在而 zh 中不存在的键”。
RealismOverhaul 的 7 个 Localization 文件、Principia 的 5 个文件都必须并集后才能得到真值。

覆盖判定（只用于 `locKeysCovered` 统计）要求 **键存在且 zh 值归一化后非空**：
Principia 的 `#Principia_FlightPlan_OptimizeInclinationNoText` 与 `#Principia_BurnEditor_OptimizingOther`
在 `zh-cn.cfg` 里存在但值为空串，按“键存在”算会多算 2 条。**候选生成仍只按键存在判定**。

### 2.3 归一化（4 步，不做别的）

`NFKC` → CRLF/CR 统一成 LF → 连续空白（含 NBSP `U+00A0`）折成单空格 → 首尾 trim。
**不折叠大小写、不统一引号**：会改写原文，也会让 `dup` 判定与下游翻译记忆失效。

### 2.4 过滤顺序与命中（`FILTER_ORDER`）

`empty` 13 → `is-key` 0 → `already-cjk` 32 → `pure-number` 8 → `too-short` 6 → `search-tags` 1,597 → `url-only` 1 → `already-covered` 0 → `dup` 4,461。

- **`is-key` 保留槽位但停用**（命中 0）：池里有 3,193 行 `text` 以 `#` 开头（`title = #autoLOC_501662`、`#Sol_Sun_name`、`#LOC.SXT_*`），键引用本身就是待译内容，不能当“这是键不是文本”丢掉。
- **`already-covered` 停用**（命中 0）：按“文本等于某条 zh 译文”判会误杀 55 条真待译文本（`KerbNet` / `Ap` / `Pe` / `AN` / `DN` / `UH25` / `Default` / `ISRU` / `ECLSS` / `RCS` 这类专名恰好等于某条译文的英文部分），其中 8 条真值里确实保留。
- **`pure-number` 必须收紧**：只杀“整条只由数字与分隔符（`0-9` 空白 `. , : % + - / ° ( )`）组成且含数字”的文本；否则 `<<"1>>.<<"2>>".<<"3>>"` 这类占位符文本会被吞掉（真值全部保留）。
- **`empty` 命中 13**（明细）：loc RP-1 `#autoLOC_456391`、`#autoLOC_460808`；cfg RealismOverhaul `description`×4、RP-1 `description`×4、ReStockPlus `tags`×3。
- **`dup` 按 `h`（FNV-1a64）去重**，loc 候选先于 cfg 候选（同文本同时出现在两处时保留 loc 行）。

### 2.5 cfg 文本字段怎么认（最贵的一条）

只认 `title` / `description` / `displayName` 三个字段名，**大小写不敏感匹配、但保留原始写法入库**（所以分布里 `title` 6,991、`description` 4,565、`displayName` 2,369、`Description` 29、`Title` 11 各自计数）；
另加后缀变体 `_title` / `_description` / `_displayname`（命中 `!real_title`、`!real_description`、`real_title`、`real_description` 各 1 次，共 16 处）。合计 `cfgFieldHits` = **15,581**。
`tags` 只计数、收进候选后由 `search-tags` 跳过（1,597）。

**绝不能剥掉 `@ $ % ! &` 前缀再匹配字段名**：那些是 ModuleManager 补丁节点上的赋值（`@PART[...]` 里再写一次 `title`），只覆盖文本，真文本在原定义处。
剥前缀的后果：RealismOverhaul / RP-1 会把 `@PART` 里成千上万条补丁赋值当候选，池立刻从 12,792 涨到 16,112。

### 2.6 glossary 的英文只能从配置文件行尾注释取

本安装的 `GameData\Squad\Localization\dictionary.cfg` **只有 `zh-cn` 一个语言节点**
（12,031 条 value / 12,028 个唯一键 / 891,576 字节 / BOM+CRLF）——原版 `en-us` 节点不在这个文件里，
所以英文字典没有现成来源。英文只能取 Squad 各 cfg 的行尾注释：

```
title = #autoLOC_500460 //#autoLOC_500460 = S1 SRB-KD25k "Kickback" Solid Fuel Booster
```

正则 `#(autoLOC_\d+)\s*=\s*([^\r\n]+)`，**只扫非 Localization 文件**（把 `dictionary.cfg` 自己扫进去会把它 12,031 行中文当成英文），
配对时**给注释键补 `#`**（注释里是 `autoLOC_500460`，字典里是 `#autoLOC_18284`）。

“只有中文 9,902 条”不是 bug：Squad cfg 里被引用到的不同 `#autoLOC` 键只有 2,181 个
（2,126 个有注释，剩下 55 个是 `Squad\Controls\Azerty.cfg:3 name = #autoLOC_6001233` 这类没写注释的），
其余 9,847 个键是游戏代码里的 UI 字符串，本地没有任何英文来源。想连这些一起导出用 `--include-zh-only`。

## 3 顺序即契约（改了就与既有产物不一致）

- `id` 从 1 连续编号，行序 = **文件遍历序 × 文件内行号序**，不是字典序、不是 mod 名字典序。
- 文件遍历用**中序 DFS（目录与文件混排）**：`_walk_in_order()` 每层 `os.scandir` 后按 `ent.name.upper()` 排序，
  **同一个循环里**遇目录当场递归（限深 64）、遇 `*.cfg` 收下。三条实测都要求这个顺序：
  1) mod 序 `B9PartSwitch` 在 `B9_Aerospace_ProceduralWings` 之前（`B9P` < `B9_`，`P`=0x50 < `_`=0x5F，用 `str.lower` 会排反）；
  2) `B9_Aerospace_ProceduralWings\B9PWingsUnlimited.cfg` 排在 `Parts\…` 之前；
  3) `ProceduralFairings\PF_Settings.cfg` 又排在 `Parts\`、`Patches\` 之后（`PA` < `PF`）。
  按整条相对路径大写排序会剩 234 行差；后序 DFS（先子目录再本目录文件）会错 3,012 行。
- 文件内按**行号（文档序）**输出：先 `staged.sort(key=lambda item: item[0])` 再进候选，因为 `Node.walk()` 是先父节点值后子节点值
  （`VenStockRevamp\PartBin\Data\Engines.cfg` 的 `VARIANT` 子节点 `displayName` 在第 18/35 行，`PART` 自己的 `title`/`description` 在第 55/57 行）。
- `pool.jsonl` 与 `inventory.json` **都不写尾部换行**、`pool.jsonl` 用 LF：差 1 字节 sha256 就不等。
- `h` 是 **16 位小写 hex 字符串**，不要改成整数（其中 12 行的 hex 恰好全是数字字符，Core 侧按“16 位全 hex 字符优先按 hex 解析”处理）。

## 4 与 `docs/04-翻译流水线与预处理.md` 的已知差异

| 项 | doc 04 | 实测 | 结论 |
| --- | --- | --- | --- |
| glossary 英文条数 | 11,930 | **2,126** | 11,930 实际是 `dictionary.cfg` 里 `#autoLOC_` 形式键的个数：上一轮把字典自己的 12,031 行中文注释也算进了“英文命中”。真实英文只存在于 Squad cfg 的 3,290 处行尾注释里。 |
| glossary 配对数 / short | 11,841 / 8,318 | **2,126 / 887** | 建立在上一条错口径上，不可复现。`--include-zh-only` 可导出全部 12,028 个字典键（en 为空）。 |
| `skip.empty` | 4 | **13** | 13 条明细见 §2.4。另：真值 inventory 自身记账不自洽（18,912 − 6,109 = 12,803 ≠ 12,792），故以 `pool.jsonl` 逐字节一致为准。 |
| `tokenEstimate` | prompt 476,875 / output 324,451 | 479,026 / 319,350 | doc 用过的公式反推不出来（`sum(len)`=958,052；`len//2`=475,748、`len//3`=315,414 都对不上），改自定口径：prompt = `sum(len)//2`、output = `sum(len)//3`、total = 两者之和。 |
| Principia | 224 / 226 / 7 | **224 / 226 / 7** ✓ | 差别来自“zh 值空串不该算覆盖”（§2.2）。 |
| RealismOverhaul / SXT | 1138 / 802 | **1138 / 802** ✓ | 靠两个键守卫：loc 键必须是可打印 ASCII（丢 `RealismOverhaul\Localization\en-us-Mfr.cfg` 的 `#roMfrОКБ4`）、键里不许有圆括号（丢 Principia `celestial_strings.cfg` 的 19 条 `#Principia_MapNode_…(Sun).en-us`）；SXT 的 `#LOC.SXT_SXTengineattachment2_title` 是一行排版错乱的 cfg 被旧解析器当成的键值，修“每行只认一个结构”后消失。 |

`cfgFiles` / `locFiles` / `locKeysTotal` / `locKeysCovered` / `locKeysMissingZh` / `cfgFieldHits` / `zhCoveredAssignments` / `zhCoveredUnique` / `poolSize` / `skip`（除 empty）/ `poolBreakdown` / `modStats` / `topMods` **均与真值逐项一致**。

**结论：inventory 记账不自洽时，一律以 `packs/pool.jsonl` 逐字节一致为准。**

真值 inventory 自身对不上账：`cfgFieldHits 15,581` + `locKeysMissingZh 3,331` − `skip 6,109` = 12,803，而 `poolSize` = 12,792，差 11 条——说明上一轮清单的 `skip` 计数漏记了 11 条。本实现的账是平的：候选 `18,910 = 池 12,792 + skip 6,118`。

因此本工具链的硬验收只有一条：**`packs/pool.jsonl` 与既有真值逐字节一致**（12,792 行 / 3,049,123 字节 / sha256 `FEF515EA…D4C`）；`inventory.json` 的 `skip` 只是过程计数器，`skip.empty` 取 **13**（而非真值的 4，明细见 §2.4）也以本条为准，不再为对齐 12,803 去调整任何过滤器——调了会让池行数偏离 12,792。

## 5 已知坑清单（都踩过）

1. **Localization 按语言分文件** → 必须按 mod 目录求并集，否则 `en-us-Engines.cfg` 这类整块漏掉。
2. **cfg 节点名可能单独一行、左大括号在下一行** → 解析器要用 pending 名跨行等 `{`。
3. **CRLF 文件**：只按 `\n` 切行会留 `\r`，`Localization\r` 匹配不上节点名（`parse_text` 入口先 `normalize_newlines`）；打开文件用 `encoding="utf-8-sig"` + `newline=""`，否则 BOM 会成为节点名第一个字符。
4. **深嵌套畸形 cfg**：`Node.walk()` 用显式栈（递归会 `RecursionError: maximum recursion depth exceeded`），目录递归也限深 64。
5. **单行补丁节点** `@AGENT[*]:HAS[~title] { title = #$name$ }`：一行只认一个结构，否则行尾 `}` 会被吃进值里变成 `#$name$ }`。
6. **注释剥离**只把行首/空白后的 `//` 当注释（保护 `https://`）；`//` 前无空格不算注释——`#Sol_Mercury_desc//The first planet…` 是真值里的文本。
7. **别剥 `@ $ % ! &` 前缀**做字段名匹配（§2.5）。
8. **`dup` 是赋值语义（后赢）**，不是先赢。
9. **覆盖判定要“键存在且值非空”**，否则 Principia 两个空值键会多算。
10. **写盘**：`json.dumps(..., ensure_ascii=False, separators=(",", ":"))`、LF、末行不写换行。
11. **Windows 控制台 GBK**：打印中文会 `UnicodeEncodeError: 'gbk' codec can't encode character` → `sys.stdout.reconfigure(encoding="utf-8", errors="replace")`。
12. **PowerShell here-string 传多行 python 有时静默失败**（stdout/stderr 全空、退出码 0，极易误判成“统计结果是 0”）→ 复杂脚本先写临时 `.py` 再 `python <path>`。
13. **本安装 Squad 字典没有 `en-us` 节点**，不要指望从 `dictionary.cfg` 读英文。

## 6 验收

```
cd C:\Users\43701\Documents\github\UniversalI18n\tools
python -m i18n_tools.selftest                      # 279 项全绿，退出码 0，约 0.4 s
python -m i18n_tools.harvest --verify ..\_scratch\corpus\pool.jsonl   # 完全一致，退出码 0，约 3 s
python -m i18n_tools.glossary                      # 2,126 行 / 467,238 字节，退出码 0
```

**四道 CI 门禁**（与 `.github/workflows/i18n.yml` 的 `gates` job 逐条对应，本地跑同样四条即可复现 CI 结论）：

```
cd tools
python -m i18n_tools.selftest            # 工具链自检 279 项
python -m i18n_tools.audit --selftest    # 审计器自检 12 项（1 正例 + 8 负例）
python -m i18n_tools.audit               # 真审计 packs/zh-cn：h 契约 / 占位符 / 富文本 / 格式串 / 跨文件同 h 同 t
python -m i18n_tools.packindex --check   # packs/index.json 与磁盘逐文件 sha256 一致
```

四条的期望输出末尾分别是 `[selftest] 全部通过（279 项）`、`[audit] selftest: 12 检查，失败 0 项`、
`VERDICT: PASS (bad_total=0)`、`[check] mod 数 74（cfg 41 + IL 48），全部 mod 都能从 file 推导`，退出码全 0。
**任何一条红都表示字典被改坏了，不要当环境问题跳过。**

`selftest` 只在 `tempfile.mkdtemp()` 造出的临时 GameData 里做测试，**不写仓库**；若存在
`..\_scratch\corpus\pool.jsonl`，还会断言 `packs\pool.jsonl` 与它 **sha256 相同**（真值缺失时打印 `[skip]` 跳过）。

`packs\pool.jsonl` 的验收硬指标：12,792 行 / 3,049,123 字节 / sha256 `FEF515EA…D4C`。

## 7 代码结构与耗时

```
tools/
  README.md                 ← 本文件
  i18n_tools/
    __init__.py             包说明、__version__、ensure_stdout()
    fnv.py                  FNV-1a64（UTF-8 字节输入，16 位小写 hex）
    cfgparse.py             KSP cfg 词法/语法：注释剥离、节点树、显式栈 walk、ParseStats
    textnorm.py             归一化、CJK 检测、纯数字/URL/键判定、特征位、长度桶
    harvest.py              扫 GameData → 候选池 + inventory（含 --verify）
    glossary.py             Squad 注释英文 × dictionary.cfg 中文 → 术语表
    packindex.py            包索引 packs/index.json + 覆盖报告 reports/coverage.md（含 --check）
    ilstrings.py            DLL 的 IL 字面量采集 → packs/il-candidates.jsonl
    ilmeta.py               反编译 .cs 里声明的 sink 元数据（给 ilstrings 判可达性）
    buckets.py              字面量分流 buckets（translate / short_label / template / never）
    iltranslatable.py       il-candidates → packs/il-translatable.jsonl（送译清单）
    translate_il.py         IL 串翻译（八门禁 + conventions/overrides 两条人工通道）→ packs/zh-cn/<mod>.il.jsonl
    capture_ingest.py       运行时捕获 capture-*.jsonl → packs/capture-candidates.jsonl
                            （--all 跨会话合并；--report 出分诊报告；--buckets 选要进产物的档）
    resolve_keyrefs.py      键引用解析 + --prefer-official + --drop-keyrefs（+--ambiguous-guard）
    translate.py            主翻译流水线（缓存/门禁/断点续跑）
    audit.py                产物审计（h 契约 / 占位符 / 富文本 / 格式串 / 跨文件一致性），CI 门禁与本地都用它
    selftest.py             279 项自测（FNV/归一化/cfg 解析/假 GameData 流水线/术语表/包索引/IL/捕获/产物校验、含 audit 的 12 项）
packs/
  pool.jsonl                12,792 行候选池（sha256 FEF515EA…D4C）
  inventory.json            统计清单
  glossary.jsonl            2,126 行双语术语表
  index.json                分发清单（schema 1：96 个包 / 11,561 行 / 3,794,336 字节；81 个 mod 身份）
  conventions.jsonl         12 条专有约定（G6 长度比下界豁免的唯一依据）
  overrides.jsonl           人工覆盖（283 条，模型之前生效、零 token）
  il-candidates.jsonl       DLL IL 字面量候选（2,285 行）
  il-translatable.jsonl     送译清单（1,746 行：translate 1,145 / short_label 429 / template 172）
  capture-candidates.jsonl  运行时捕获候选 **v3 骨架塌缩**（29 个会话 / 60,379 distinct 原文 → 1,206 行 /
                            534 KB：candidate 714 = 原样 558 + 判不出骨架降级 156，pattern 492 = 58,475 条
                            template 实例塌成 492 个数字骨架，覆盖 55,609 条实例；纯符号骨架 3 个 / 2,710 条
                            实例按卫生门丢弃；产物里**没有 raw template 行**；v1.1 不进包）
                            生成：python -m i18n_tools.capture_ingest --all <捕获目录> --report reports/capture-triage.md --patterns reports/capture-patterns.md
                            pattern 行字段：n == s == 骨架、h = fnv.hash_text(normalize(骨架))、count = 实例 count
                            之和、exact = 该骨架下 count 最大的真实原文；译文照骨架翻，运行时按骨架查表填数字
                            行的 mods = 归属 mod id：dll 名优先（MechJeb2.dll → MechJeb2，与 *.il.jsonl 建文件同一口径），反查不出
                            （unknown caller / 我们的补丁帧 / 截断的程序集名 / ambiguous）统一 UI18n-Unattributed，不再是 caller 类型名根
                            （GuiUtils / ConfigurationGUI / unknown 这类假 mod），出包时落共享包
reports/
  coverage.md               zh-cn 覆盖报告（三节：pool 覆盖率 70.93% / IL 覆盖率 95.98% / 捕获通道现状）
```

harvest 各阶段耗时（本机 SSD，6,212 个 cfg）：`walk 0.10 s + loc 0.16 s + cfg 2.22 s + 过滤 0.27 s + 落盘 0.06 s = 2.83 s`
（反复跑在 2.4–5.2 s 之间波动，主要是文件系统缓存）；解析规模 116 个 Localization 文件 / 119,332 节点 / 508,818 键值 / 畸形行 7,617。
glossary 耗时 `英文 0.05 s + 中文 0.02 s + 组装 0.00 s + 落盘 0.01 s = 0.08 s`。

## 8 给下游（Core / Prompt 侧）的接口约定

- 键 = `h`（16 位小写 hex，FNV-1a64 of `text`）；`id` 只是本文件的顺序号，**不要**当稳定标识（改了抽取规则就会移位）。
- 归一化后的 `text` 已经是“翻译单元”本体；`len`/`ph`/`rt`/`dg`/`path`/`url` 是探针特征位（与 `docs/02` §5.2 同名同义），
  用来选判据（长文本 >200 走长度+首尾判据，见决策 D26；占位符 `ph` 的门禁优先级最高，见 D19）。
- `src=loc` 的行带 `key`，命中游戏原生本地化表时可直接按 `key` 回填；`src=cfg` 的行带 `field`，
  回填时必须保持“构造新对象、绝不原地写 `content.text`”的红线（Unity `GUIContent.Temp` 返回共享实例）。
- `glossary.jsonl` 的 `short=true`（≤5 词且 ≥4 字符，887 条）可作术语约束注入；`en` 与 `zh` 均为原文，未做任何改写。

## 9 包索引与覆盖报告（`packindex.py`）

```powershell
# 生成：packs/index.json + reports/coverage.md（秒级）
python -m i18n_tools.packindex

# 校验：磁盘 index 与真实包文件是否一致；一致 exit 0，不一致 exit 1 并列差异
python -m i18n_tools.packindex --check
#   index ok: 89 packs, 11089 rows, 3685087 bytes
#   [check] mod 数 74（cfg 41 + IL 48），全部 mod 都能从 file 推导

# 只写 index、不写报告；或换目录/换语言
python -m i18n_tools.packindex --no-coverage
python -m i18n_tools.packindex --packs-dir <dir> --index <file> --coverage <file> --pool <file> --lang zh-cn
```

`packs/index.json` 的 schema（**已发布，字段名与顺序都不许随便改**）：

- 顶层顺序：`schema`（=1）、`lang`（`"zh-cn"`）、`generatedAt`（ISO8601，秒级 UTC）、`totalRows`、`totalBytes`、`packCount`、`packs`
- `packs[]` 每项顺序：`mod`、`file`（`zh-cn/<mod>.jsonl`，正斜杠）、`rows`、`bytes`、`sha256`（**文件级**）、`keepRows`
- **`mod` 是 `mod` 身份，不是唯一键**：IL 包文件名尾部有 `.il`（`RealFuels.il.jsonl`），`mod` 身份要剥掉它（`RealFuels`）；同一个 mod 最多两条（cfg 包 + IL 包），**`file` 才是唯一键**，`--check` 按 `file` 比对，按 `mod` 聚合的地方必须合并计算
- **`packs` 按 `(mod, file)` ordinal（码点）升序**——顺序即契约：不用 case-insensitive，因为不同 Python/区域设置会给出不同结果，而增量分发靠“同一份清单逐行比对”，顺序一变就全失效。同一个 mod 的两条靠 `file` 定序（`"." (0x2E) < "j" (0x6A)`，所以 `X.il.jsonl` 在 `X.jsonl` 之前）
- `keepRows` = 该文件里 `t == s` 的行数（直通行：译文等于原文）。它不是统计口径，是**运行时语义**：下沉查找靠它终止
- 三条汇总与逐文件累加强制相等（`build_index` 里有断言，宁可崩也不写假清单）
- 幂等：同样输入连跑两次，除 `generatedAt` 那一行外**逐字节相同**（`selftest` 的 [7] 实测这一点）

`reports/coverage.md` 的覆盖率口径（引用这个数字时必须连口径一起说）：

- **分母** = `packs/pool.jsonl` 的行数（12,792）。这是抽取阶段已经剔除 `empty` / `already-cjk` / `pure-number` / `too-short` / `search-tags` / `url-only` / `dup` 之后的**待译英文串总量**；被剔除的本来就不需要译，算进分母只会把覆盖率压低且失去意义
- **分子** = 这 12,792 行里 `h` 能在 `packs/zh-cn/*.jsonl` 精确命中的行数（9,073）
- **覆盖率 = 9,073 / 12,792 = 70.93%**（按来源：`loc` 2,646/2,948 = 89.76%、`cfg` 6,427/9,844 = 65.29%）
- 两个**失真**数字列在报告里防止误引：`11,089 / 12,792 = 86.69%`（分子混进 2,016 条池外条目，含全部 IL 包）、`8,985 / 12,792 = 70.24%`（剔除 88 行直通；全库 92 行直通里有 4 行在 IL 包里、不在 pool 交集内）
- **报告是三节，三节的分母互不相加**：① pool 口径（cfg 包覆盖多少待译串，9,073 / 12,792）；② DLL 字面量口径（`il-translatable.jsonl` 的 `translate` 桶 1,145 条 → IL 包 1,099 条 = 95.98%，与 ① 的 `h` 交集 0，对 ① 贡献 0）；③ 运行时捕获通道现状（`capture-candidates.jsonl` **v3 骨架塌缩 = 1,206 行 = candidate 714 + pattern 492**，来自 29 个游戏会话 / 60,379 条 distinct 原文，58,475 条 template 实例塌成 492 个骨架并覆盖其中 55,609 条，与 ①② 的 `h` 交集 24 条 = 1.99%，其余 1,182 条只有运行时看得到，v1.1 不进包；**这一节的数字随 capture 产物变，改完产物要重跑 `packindex` 才会跟进**）
- 三节数字**全部由 `packindex` 现算**（`_section_il` / `_section_capture` 读 `il-translatable.jsonl` / `capture-candidates.jsonl`），报告里没有任何硬编码的候选清单数字：换一版 capture 产物只要重跑一次 `packindex`，③ 就会自动跟着变；`il-translatable.jsonl` 或 `capture-candidates.jsonl` 不存在时那一节打「无法计算」，不编造数字

改动 `packs/zh-cn/**` 之后**必须重跑 `python -m i18n_tools.packindex`**：`selftest` 的 [8] 会拿清单与现场扫描逐项比对，
一旦漂移就会报 `清单条目与现场扫描逐项一致（清单不能与磁盘漂移）` 失败——这是故意的护栏，不是噪声。
[8] 还钉住三个真值（`packCount == 89` / `totalRows == 11089` / `totalBytes == 3685087`）与两个构成（`cfg 41 / IL 48`、`唯一 mod 74`）：
加包后这三行必须同步改，否则失败信息就是提醒你“包变了、清单没跟上”。
