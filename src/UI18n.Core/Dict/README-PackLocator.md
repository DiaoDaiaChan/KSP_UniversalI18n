# PackLocator —— API 契约与 Runtime 接线说明（T19）

> 位置：`src/UI18n.Core/Dict/PackLocator.cs`（实现）　测试：`tests/UI18n.Core.Tests/PackLocatorTests.cs`（81 例）
> 提案（含事故复盘）：`tests/UI18n.Core.Tests/PROPOSAL-PackLocator.md`

## 0. 为什么会有这个文件

D35 契约是 **`Packs/<lang>/<modId>.jsonl`**，但旧 `ConfigLoader.LoadPacks` 用
`Directory.GetFiles(Packs, "*.jsonl", TopDirectoryOnly)` 平铺读根目录 —— 于是**按契约放的包一条都读不到、
字典恒空、patch 恒 0**。这个 bug 是装进真游戏之后才发现的，因为那一层（net472 + 依赖游戏侧程序集）在本机没法单测。

`PackLocator` 把那几十行**纯路径计算**搬进 Core（纯 BCL、双目标 net472/net10.0、零游戏依赖），
Runtime 只负责把 `Localizer.CurrentLanguage` 传进来 + 打一行日志。于是优先级、回退、
「平铺文件永远不读」这三件事都能被单测穷举，**不会再退回到「装进游戏才发现」**。

## 1. API（`namespace UI18n.Core.Dict`）

| 成员 | 签名 | 语义 |
| --- | --- | --- |
| `NormalizeLanguage` | `static string (string raw)` | Trim → `_`→`-` → ToLowerInvariant。null/空/纯空白 → `string.Empty`（**永不返回 null**） |
| `LanguageCandidates` | `static IReadOnlyList<string> (string current)` | 完整标签在前、主语言在后：`zh-cn`→[zh-cn, zh]；多段只回退一次；无语言 → 空数组 |
| `SelectLanguageDir`（纯） | `static string (IReadOnlyList<string> existingDirNames, string language)` | **不碰磁盘**。逐候选：先 Ordinal 精确、再 OrdinalIgnoreCase；返回磁盘真实大小写；全不中 → null |
| `SelectLanguageDir`（I/O） | `static PackDirStatus (string packsRoot, IEnumerable<string> candidates, out string selectedDir)` | 扫根下子目录再按候选选；根不存在 → `PacksRootMissing` |
| `ListPackFiles` | `static IReadOnlyList<string> (string languageDir)` | 只取 `*.jsonl`、`TopDirectoryOnly`、跳过 0 字节、按**文件名 Ordinal 升序** |
| `ListPackFiles`（便捷） | `static IReadOnlyList<string> (string packsRoot, string language)` | `= Resolve(packsRoot, language).Files` |
| `Resolve` | `static PackResolution (string packsRoot, string language)` | 一次给全：状态 + 命中目录 + 文件 + 诊断 |

`PackResolution`（不可变，数组属性按约定视为只读）：
`PacksRoot` / `Language` / `NormalizedLanguage` / `Candidates` / `Status` /
`SelectedLanguage`（命中的**目录名**，磁盘真实大小写）/ `Directory`（命中目录路径）/
`Files` / `AvailableLanguageDirs`（诊断，Ordinal 升序，≤8）/ `FlatFileCount`（诊断，根下被忽略的平铺 jsonl 数）/
`HasPacks` / `Describe()`（一行日志）。

`enum PackDirStatus { Found = 0, PacksRootMissing = 1, LanguageDirMissing = 2 }`

## 2. 优先级规则（写死，不许猜）

1. 归一化：Trim → `_` 换 `-` → ToLowerInvariant（Invariant，避开土耳其 i）。
2. 候选：完整标签 → 主语言（第一个 `-` 之前），多段只回退一次（`zh-cn-x-foo`→[zh-cn-x-foo, zh]`）。
3. **单向回退**：语言 `zh-cn` 可以落到 `zh/`；语言 `zh` **不会**升到 `zh-cn/`（否则中文玩家被塞更具体的包，语义倒挂）。
4. 目录匹配：逐候选先 Ordinal 精确、再 OrdinalIgnoreCase；返回磁盘真实大小写。显式比较，**不依赖文件系统行为**（Windows 大小写不敏感、Linux 不是）。
5. 文件枚举：`*.jsonl`、不递归、按文件名 **Ordinal** 升序（顺序必须确定 —— DictPack 里后写覆盖先写）。
6. **平铺文件永不读**：根下的 `*.jsonl` 只进 `FlatFileCount`，永不进 `Files`。
7. **不抛异常**：根不存在 / 无权限 / 非法字符 / 语言为 null 一律返回状态枚举 + 诊断字段（红线 6：失败只 log 不抛，但必须能计数）。
8. 无副作用：只读，不建目录、不写文件、不改 mtime；同一输入两次调用结果相同。

## 3. 与提案 `PROPOSAL-PackLocator.md` 的差异（Lead 授权「可微调」）

| # | 提案 | 实现 | 原因 |
| --- | --- | --- | --- |
| 1 | `NormalizeLanguage` 空 → `null` | 空 → `string.Empty` | T19 任务书明确要求空串，调用方少一个 null 分支 |
| 2 | 文件排序 `OrdinalIgnoreCase` | `Ordinal` | T19 任务书明确要求 ordinal 升序。差异只影响大小写混排名（如 `C.jsonl` 与 `a.jsonl`）的先后；Core 现在是唯一真相 |
| 3 | 属性名 `ChosenDir` / `PackFiles` | `Directory` / `Files`（旧名保留为别名） | 按任务书；两个名字都测了，别名不会烂掉 |
| 4 | 命中目录是「绝对路径」 | 「传入根 + 真实目录名」的拼接 | 不做 `Path.GetFullPath`：它对非法字符会抛，与「绝不抛」冲突。调用方传绝对根即得绝对路径 |
| 5 | 只有纯函数版 `SelectLanguageDir` / 2 参 `ListPackFiles` | 纯函数版 + I/O 版都保留 | 纯函数是优先级规则的主要测试面；I/O 版是任务书要的接线口 |
| 6 | 无 `Describe()` | 新增 | Runtime 只要打一行日志，不必自己拼字符串 |

## 4. Runtime 接线示例（core-engineer 用，**本任务不改 Runtime**）

```csharp
// 替换 ConfigLoader.LoadPacks 里的目录发现部分（其余 MaxEntries / 计数逻辑不动）
PackResolution packs = PackLocator.Resolve(Path.Combine(ModFolder, ConfigLoader.PacksFolderName), language);

if (packs.FlatFileCount > 0)
{
    // 缺陷②的常驻自检：根下出现平铺 jsonl，几乎一定是 deploy 又拷错了
    Log.Warn(packs.Describe() + " (flat *.jsonl under Packs root are ignored; expected Packs/<lang>/<modId>.jsonl)");
}

if (packs.Status != PackDirStatus.Found)
{
    Log.Warn(packs.Describe());          // 一行说清「为什么没装包」：根没了？语言目录没了？
    return DictPack.LoadJsonLines(null); // 空包，调用方照旧
}

Log.Info(packs.Describe());
foreach (string file in packs.Files)     // 已排序，顺序确定
{
    // 按现有 MaxEntries 上限逐行 AddJsonLine（保留 ParseErrors 计数）
}
```

**不要**自己再拼 `Packs` 路径或判断 `Directory.Exists` —— 那就是这次事故的复发路径。

## 5. 测试覆盖（`PackLocatorTests.cs`，81 例，全绿）

- 归一化 11 例（大小写 / 下划线 / 前后空白 / Tab / NBSP / 空 / null / 文化无关）
- 候选 11 + 3 例（完整→主语言、多段只回退一次、空语言空数组）
- 纯函数选目录 17 例（精确优先 / 回退 / 不上升 / 大小写 / 真实大小写 / 越具体越优先 / null 与空）
- I/O 选目录 4 例（Found+真实大小写 / 根不存在 / 无匹配 / 候选 null 与空）
- 列文件 6 例（ordinal 序 / 非 jsonl / 0 字节 / 名字像文件的目录 / 坏入参 / 2 参重载一致）
- `Resolve` 命中与优先级 8 例（含 `ZH-CN` 目录、根尾分隔符、空语言目录、不递归）
- **事故回归锁 2 例**：
  `Resolve_FlatPacksUnderRoot_AreReportedNotSilentlyLoaded`（只有平铺包 → `LanguageDirMissing` + `Files` 空 + `FlatFileCount==2`）
  与 `Resolve_FlatPacksDoNotHideRealLanguageDirectory`（平铺包与真目录并存 → 只读真目录）
- 失败与异常输入 9 例（根不存在 / 根为 null 或空白 / 只有别的语言目录 / 空语言 / 路径里含 NUL 字符（U+0000）/ 路径含非法字符（冒号、竖线、UNC 前缀、不存在的盘符）/ 超长 300 字符根，全部断言不抛）
- 诊断与不变式 5 例（诊断目录排序且 ≤8 / `Describe` 内容 / 别名一致 / 幂等且无副作用 / 文化无关）

## 6. 明确不做的事

- 不做多语言合并（一次只读一个语言目录）。
- 不做热重载 / 文件监听。
- 不改 JSONL 解析（那是 `JsonLineReader` / `DictPack` 的事）。
- 不搬 `MaxEntries` 上限逻辑（仍在 Runtime）。
