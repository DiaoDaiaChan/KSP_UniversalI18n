# 提案：把「Packs 目录解析」抽成 Core 里的 `PackLocator`（可测化）

> 作者：test-engineer（T17）　状态：**提案，未实施**　不改 `src/`（避免与 core-engineer 撞车）
> 目标读者：Lead、core-engineer（落地实施者）、docs-engineer（文档同步）

## 0. 一句话

P1 修好的两件事（`Packs/<lang>/` 布局、deploy 不再平铺拷 `pool.jsonl`）**目前一行测试都没有**：
逻辑写在 `UI18n.Runtime` 里，而 Runtime 是 net472 + 引用了 `Assembly-CSharp`/`UnityEngine`，
`tests/UI18n.Core.Tests`（net10.0）**根本引用不了它**。本提案把这几十行纯路径逻辑搬进 Core
（`UI18n.Core.Dict.PackLocator`，纯 BCL、零游戏依赖），Runtime 只保留「读文件 + 打日志」，
于是优先级规则、回退规则、平铺文件被忽略这三件事都能被 16 条单测钉死。

## 1. 为什么现在测不了（事实）

| 障碍 | 证据 |
| --- | --- |
| Runtime 是 net472，且编译期引用游戏程序集 | `src/UI18n.Runtime/UI18n.Runtime.csproj:10` `<TargetFramework>net472</TargetFramework>`；`:34-60` 引用 `Assembly-CSharp`、`UnityEngine`、`UnityEngine.UI` 等 |
| 测试工程是 net10.0，只引用 Core | `tests/UI18n.Core.Tests/UI18n.Core.Tests.csproj`：`TargetFramework net10.0` + 单个 `ProjectReference ..\..\src\UI18n.Core\UI18n.Core.csproj` |
| 目标逻辑全是 internal static，且依赖 KSP 全局 | `src/UI18n.Runtime/ConfigLoader.cs:19` `internal static class ConfigLoader`；`:24-27` `ModPath` 依赖 `KSPUtil.ApplicationRootPath` |
| Core 有「零游戏依赖」硬守卫 | `src/UI18n.Core/UI18n.Core.csproj:26-33` `UI18nCore_RejectGameDependencies`：引用 UnityEngine/Assembly-CSharp/Harmony/KSP 或任何 PackageReference 直接编译失败 |

结论：**要可测，逻辑必须落在 Core**；Core 的守卫同时保证它永远不会偷偷长回游戏依赖。

## 2. P1 的两个真实缺陷（本提案要防的就是它们复发）

### 缺陷 ①：`TopDirectoryOnly` 平铺读 `Packs/*.jsonl`，按契约放的包一条都读不到

- 旧实现：`Directory.GetFiles(PacksPath, "*.jsonl", SearchOption.TopDirectoryOnly)` 直接读 Packs 根；
  契约是 `Packs/<lang>/<modId>.jsonl`（D35，`docs/03-进度追踪.md:165`、`docs/00-总体设计.md:362`、`docs/06-工程骨架与构建.md:374`）。
- 后果：按契约部署的包 **0 条被加载**，而根目录一旦有源产物反而「能读到垃圾」。
- 现状（已修，仍未测）：`src/UI18n.Runtime/ConfigLoader.cs:125-172` 新增 `NormalizeLanguage` / `LanguageCandidates` / `ResolvePackDir`，
  `:178-254` `LoadPacks` 改为只在 `ResolvePackDir` 命中的目录里 `TopDirectoryOnly` 读。

### 缺陷 ②：deploy 把 `packs/*.jsonl` 平铺拷进运行时 → 12,792 条 ParseError

- 源产物：`packs/pool.jsonl` **3,049,123 B**（采集池，12,792 行，**没有 `t` 字段**）、`packs/glossary.jsonl` **467,238 B**、`packs/inventory.json` **3,676 B**。
- 旧 `deploy.ps1` 把它们当字典拷进 `GameData/UI18n/Packs/` → 每行都解析失败 → `ParseErrors = 12,792`，
  真正的字典条目被噪声淹没（而且 3 MB 白读）。
- 现状（已修，仍未测）：`src/UI18n.Runtime/deploy.ps1:97-105` 只 stage `packs\<lang>\*.jsonl` 到 `Packs\<lang>\`；
  `src/UI18n.Runtime/README.md:178` 写明了「只拷 `packs\<lang>\*.jsonl`」。

两个缺陷的共同点：**都是「给定一个根目录 + 一个语言串，该读哪些文件」的纯逻辑，却藏在最不可测的层**。

## 3. 提案 API（`src/UI18n.Core/Dict/PackLocator.cs`，新增）

硬约束：只用 BCL（`System`/`System.IO`），**net472 与 net10.0 双目标都要编译通过**，C# 9，
不用 `Span`、不用 `System.IO.Enumeration`、不用 `string.Contains(char)`（net472 没有）。

```csharp
namespace UI18n.Core.Dict
{
    /// <summary>目录解析结果状态。Runtime 用它决定日志级别（Found 不吵，其它 Warn 一次）。</summary>
    public enum PackDirStatus
    {
        Found = 0,              // 命中语言目录（目录里可能没有 *.jsonl，见 PackFiles）
        PacksRootMissing = 1,   // Packs 根目录不存在（未安装字典 / 装错位置）
        LanguageDirMissing = 2, // 根存在但没有该语言的目录（含语言串为空）
    }

    /// <summary>一次解析的全部结果（不可变；诊断字段直接进日志，Runtime 不再自己拼文案）。</summary>
    public sealed class PackResolution
    {
        public string PacksRoot { get; }              // 传入的根（原样回显，便于日志对账）
        public string Language { get; }               // 传入的语言串（原样，可能 null）
        public string NormalizedLanguage { get; }     // 归一化后（小写、_ 换 -、Trim）；空/空白 → null
        public string[] Candidates { get; }           // 候选目录名，按优先级（如 ["zh-cn","zh"]）；不可变副本
        public PackDirStatus Status { get; }
        public string ChosenDir { get; }              // 命中的目录绝对路径（保留磁盘上的真实大小写）；未命中 null
        public string[] PackFiles { get; }            // 命中目录下的 *.jsonl 绝对路径，已排序；未命中为空
        public string[] AvailableLanguageDirs { get; }// 诊断：根下实际存在的语言目录名（排序，最多 8 个）
        public int FlatFileCount { get; }             // 诊断：根下被忽略的平铺 *.jsonl 个数（缺陷②的常驻自检）
        public bool HasPacks { get; }                 // PackFiles.Length > 0
    }

    public static class PackLocator
    {
        /// <summary>Trim → 下划线换连字符 → ToLowerInvariant；空/纯空白 → null。</summary>
        public static string NormalizeLanguage(string language);

        /// <summary>候选目录名，按优先级：完整标签 → 主语言；空/空白 → 空数组。</summary>
        public static string[] LanguageCandidates(string language);

        /// <summary>
        /// **纯函数，不碰磁盘**：给定根下已存在的目录名，返回该用哪个（返回目录名的真实写法）。
        /// 逐候选匹配：先 Ordinal 精确命中，再 OrdinalIgnoreCase 兜底；全不中返回 null。
        /// </summary>
        public static string SelectLanguageDir(string[] existingDirNames, string language);

        /// <summary>I/O 包装：扫根目录、选目录、列出 *.jsonl，任何异常都不抛，最差返回 Status 说明原因。</summary>
        public static PackResolution Resolve(string packsRoot, string language);

        /// <summary>便捷方法：Resolve(...).PackFiles。</summary>
        public static string[] ListPackFiles(string packsRoot, string language);
    }
}
```

拆成 `SelectLanguageDir`（纯）+ `Resolve`（I/O）的理由：优先级规则是**最该被穷举测的部分**，
纯函数版本连临时目录都不用建，测试里直接喂 `new[] { "zh", "zh-cn" }`；
`Resolve` 只负责把磁盘事实喂给它，再用临时目录做 4-5 条集成级断言。

## 4. 优先级规则（写死，不许「猜」）

1. **归一化**：`Trim()` → `_` 换成 `-` → `ToLowerInvariant()`。归一化后为空 → 候选为空 → `LanguageDirMissing`。
2. **候选顺序**：完整标签在前，主语言（第一个 `-` 之前）在后。`zh-cn` → `["zh-cn","zh"]`；
   `zh-hans` → `["zh-hans","zh"]`；`zh` → `["zh"]`；`zh-cn-x-foo` → `["zh-cn-x-foo","zh"]`（多段只回退一次，不逐级剥）。
3. **单向回退**：语言 `zh-cn` 可以落到 `zh/`；语言 `zh` **不会**升到 `zh-cn/`（否则中文玩家会被塞一个更具体的包，语义倒挂）。
4. **目录匹配**：逐候选，先 Ordinal 精确，再 OrdinalIgnoreCase；返回磁盘上的**真实大小写**。
   （Windows 文件系统本来就大小写不敏感，这条把行为显式化；Linux 上则让 `ZH-CN/` 也能被 `zh-cn` 命中。）
5. **文件枚举**：`*.jsonl`，`SearchOption.TopDirectoryOnly`（**不递归**），`OrdinalIgnoreCase` 升序（与 `ConfigLoader.cs:200-201` 现状一致）。
6. **平铺文件永不读**：根下的 `*.jsonl` 只计数（`FlatFileCount`）不进 `PackFiles`。这条就是缺陷②的守门人。
7. **不抛异常**：根不存在 / 无权限 / 非法字符 / 语言为 null 一律返回状态对象；红线 6 要求「失败只 log 不抛但必须计数」，
   所以失败原因必须能被 Runtime 计数与打印（`Status` + `AvailableLanguageDirs`）。
8. **无副作用**：只读，不创建目录、不写文件、不改 mtime；同一输入两次调用结果相同。

| # | 语言 | 根下已有目录 | 期望 | 规则 |
| --- | --- | --- | --- | --- |
| 1 | `zh-cn` | `zh-cn`, `zh` | `zh-cn` | 精确优先 |
| 2 | `zh-cn` | `zh` | `zh` | 主语言回退 |
| 3 | `zh-cn` | `en-us` | null + `LanguageDirMissing` | 不猜 |
| 4 | `zh` | `zh-cn` | null + `LanguageDirMissing` | 单向回退（不上升） |
| 5 | `zh_CN` | `zh-cn` | `zh-cn` | 归一化 `_` → `-` |
| 6 | `ZH-CN` | `zh-cn` | `zh-cn` | 大小写不敏感 |
| 7 | `zh-cn` | `ZH-CN` | `ZH-CN` | 返回真实大小写 |
| 8 | `zh-hans` | `zh-hans`, `zh` | `zh-hans` | 脚本标签按完整标签优先 |
| 9 | `en-us` | `en-us`, `en` | `en-us` | 语言无关（不做中文特判） |
| 10 | `null` / `""` / `"   "` | 任意 | null + `LanguageDirMissing`，`Candidates` 为空 | 空语言不猜 |

## 5. Runtime 侧要改什么（core-engineer 实施清单）

`src/UI18n.Runtime/ConfigLoader.cs`：

1. **删除** `NormalizeLanguage`（`:125-134`）、`LanguageCandidates`（`:140-156`）、`ResolvePackDir`（`:159-172`）、
   `DescribeCandidates`（`:261-281`）—— 全部搬进 Core，Runtime 一份都不留（否则两处规则迟早漂移）。
2. `LoadPacks`（`:178-254`）改成：
   ```csharp
   PackResolution res = PackLocator.Resolve(PacksPath, language);
   if (res.Status == PackDirStatus.PacksRootMissing) { Log.Info(...); return pack; }
   if (res.Status == PackDirStatus.LanguageDirMissing) { Log.Warn(... res.AvailableLanguageDirs ...); return pack; }
   if (res.FlatFileCount > 0) { Log.Warn("ignored " + res.FlatFileCount + " flat *.jsonl under Packs root (source artifacts, not dictionaries)"); }
   // 只遍历 res.PackFiles（已排序），MaxEntries 逻辑原样保留
   ```
3. `DescribeAvailablePackDirs`（`:284-`）删掉，改用 `res.AvailableLanguageDirs`。
4. `UI18nAddon.cs:275` 继续原样返回 `KSP.Localization.Localizer.CurrentLanguage`，**不要在这里归一化**（归一化是 PackLocator 的活）；
   `:143` 的 `LanguageGate.ShouldTranslate(language, config.ForceOverride)` 保持不动。
5. 日志新增一条 `packs: language=<raw> normalized=<norm> candidates=[a,b] status=<status> files=<n> flat=<n>`，
   这样玩家报「字典没生效」时一行日志就能定位（现在要翻好几行）。

`src/UI18n.Runtime/deploy.ps1`（可选加固，不属 Core，但同属缺陷②）：

- 现有 `:97-105` 已经是 `packs\<lang>\*.jsonl` → `Packs\<lang>\`，正确。
- 建议加一条**安装后自检**：若 `GameData\UI18n\Packs\*.jsonl`（平铺）存在则 `Write-Warning` 并列出文件名，
  这样「有人手工把 pool.jsonl 丢进去」也能被发现。

## 6. 必须存在的测试用例（16 条，落地时新建 `PackLocatorTests.cs`）

| # | 用例 | 断言要点 | 对应用户可见故障 |
| --- | --- | --- | --- |
| 1 | `NormalizeLanguage_TrimsLowercasesAndReplacesUnderscore` | `" zh_CN "`→`"zh-cn"`；`"ZH-CN"`→`"zh-cn"`；`""`/`null`/`"  "`→null | 语言串大小写/分隔符导致整包失效 |
| 2 | `LanguageCandidates_FullTagThenPrimary` | `zh-cn`→[`zh-cn`,`zh`]；`zh`→[`zh`]；`zh-hans`→[`zh-hans`,`zh`]；`zh-cn-x-foo`→[`zh-cn-x-foo`,`zh`]；null→[] | 回退顺序错 → 选错包 |
| 3 | `SelectLanguageDir_Pure_PriorityMatrix` | 纯函数直接喂目录名数组，覆盖 §4 表 10 行 | 同上（无需磁盘） |
| 4 | `SelectLanguageDir_ReturnsRealCaseFromDisk` | 已有 `["ZH-CN"]` + 语言 `zh-cn` → 返回 `"ZH-CN"` | 日志里路径与实际不符，排障走偏 |
| 5 | `Resolve_PacksRootMissing_ReportsStatus` | 根不存在 → `PacksRootMissing`、`ChosenDir` null、`PackFiles` 空、**不抛** | 字典目录装错位置时静默无包 |
| 6 | `Resolve_LanguageDirMissing_ListsAvailable` | 根下只有 `en-us`、`ja` → `LanguageDirMissing`、`AvailableLanguageDirs` 排序为 [`en-us`,`ja`] | 玩家把目录名拼错（`zh_CN` vs `zh-cn`）时无提示 |
| 7 | `Resolve_PrefersExactTag_OverPrimary` | 同时有 `zh-cn` 与 `zh` → 选 `zh-cn`，`PackFiles` 全部来自 `zh-cn` | 主语言包盖掉精确包 |
| 8 | `Resolve_FallsBackToPrimaryTag` | 只有 `zh`、语言 `zh-cn` → 选 `zh` | 只放 `zh/` 的玩家读不到 |
| 9 | `Resolve_DoesNotPromotePrimaryToFullTag` | 语言 `zh`、只有 `zh-cn` → `LanguageDirMissing` | 语义倒挂 |
| 10 | `Resolve_NormalizesUnderscoreLanguage` | 语言 `zh_CN`、目录 `zh-cn` → 命中 | 手改 settings.cfg 用下划线 |
| 11 | **`Resolve_IgnoresFlatJsonlAtRoot`** | 根下 `pool.jsonl`+`glossary.jsonl`+`zh-cn/a.jsonl` → `PackFiles` 只有 `a.jsonl`，`FlatFileCount==2` | **缺陷②：12,792 条 ParseError** |
| 12 | `Resolve_IsNotRecursive` | `zh-cn/sub/x.jsonl` 不出现在 `PackFiles` | 递归误读备份目录 |
| 13 | `Resolve_SortsPackFilesOrdinalIgnoreCase` | `b.jsonl`/`A.jsonl`/`_smoke.jsonl` → [`A.jsonl`,`b.jsonl`,`_smoke.jsonl`] | 加载顺序不确定 → 同 hash 条目胜负漂移 |
| 14 | `Resolve_EmptyLanguageDir_IsFoundButEmpty` | `zh-cn/` 里只有 `readme.txt` → `Found` + `PackFiles` 空 | 「装了包却没生效」的空目录 |
| 15 | `Resolve_IsIdempotent_AndReadOnly` | 连续两次结果相同；目录内文件数/mtime 不变 | 运行时被测试污染 |
| 16 | `Resolve_NeverThrows_OnHostileInput` | 语言含 `\0`、`*`、`..`、超长串、根传 null/空串 → 全部返回状态对象，不抛 | 启动期抛异常 = 整局起不来 |

> 注意用例 16 里 `..`：`Path.Combine(root, "..")` 会跳出根，实现必须**先校验候选只含 `[a-z0-9-]`**（归一化后），
> 不合法直接 `LanguageDirMissing`，绝不把越界路径交给 `Directory.Exists`。这条是安全边界，别省。

### 6.1 可编译的测试骨架（等 `PackLocator` 落地后另存为 `PackLocatorTests.cs`，现在放这里）

不现在写成 `.cs` 的原因：`PackLocator` 还不存在，`.cs` 会直接编译失败，把整个测试工程搞红。

```csharp
using System;
using System.IO;
using UI18n.Core.Dict;
using Xunit;

namespace UI18n.Core.Tests
{
    public class PackLocatorTests : IDisposable
    {
        private readonly string _root;

        public PackLocatorTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ui18n-packlocator-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private void Dir(params string[] parts)
        {
            Directory.CreateDirectory(Path.Combine(_root, Path.Combine(parts)));
        }

        private void FileIn(string dir, string name)
        {
            File.WriteAllText(Path.Combine(_root, dir, name), "{\"h\":1,\"t\":\"x\"}");
        }

        [Theory]
        [InlineData(" zh_CN ", "zh-cn")]
        [InlineData("ZH-CN", "zh-cn")]
        [InlineData("zh", "zh")]
        [InlineData("", null)]
        [InlineData(null, null)]
        [InlineData("   ", null)]
        public void NormalizeLanguage_TrimsLowercasesReplacesUnderscore(string input, string expected)
        {
            Assert.Equal(expected, PackLocator.NormalizeLanguage(input));
        }

        [Fact]
        public void LanguageCandidates_FullTagThenPrimary()
        {
            Assert.Equal(new[] { "zh-cn", "zh" }, PackLocator.LanguageCandidates("zh-cn"));
            Assert.Equal(new[] { "zh" }, PackLocator.LanguageCandidates("zh"));
            Assert.Equal(new[] { "zh-hans", "zh" }, PackLocator.LanguageCandidates("zh-hans"));
            Assert.Equal(new[] { "zh-cn-x-foo", "zh" }, PackLocator.LanguageCandidates("zh-cn-x-foo"));
            Assert.Empty(PackLocator.LanguageCandidates(null));
        }

        [Fact]
        public void SelectLanguageDir_PrefersExactThenPrimary_ThenCaseInsensitive()
        {
            Assert.Equal("zh-cn", PackLocator.SelectLanguageDir(new[] { "zh", "zh-cn" }, "zh-cn"));
            Assert.Equal("zh", PackLocator.SelectLanguageDir(new[] { "zh" }, "zh-cn"));
            Assert.Equal("ZH-CN", PackLocator.SelectLanguageDir(new[] { "ZH-CN" }, "zh-cn"));
            Assert.Null(PackLocator.SelectLanguageDir(new[] { "zh-cn" }, "zh"));
            Assert.Null(PackLocator.SelectLanguageDir(new[] { "en-us" }, "zh-cn"));
        }

        [Fact]
        public void Resolve_IgnoresFlatJsonlAtRoot()
        {
            Dir("zh-cn");
            FileIn("zh-cn", "Squad.jsonl");
            File.WriteAllText(Path.Combine(_root, "pool.jsonl"), "{}");
            File.WriteAllText(Path.Combine(_root, "glossary.jsonl"), "{}");

            PackResolution res = PackLocator.Resolve(_root, "zh-cn");

            Assert.Equal(PackDirStatus.Found, res.Status);
            Assert.Equal(2, res.FlatFileCount);
            Assert.Single(res.PackFiles);
            Assert.EndsWith("Squad.jsonl", res.PackFiles[0]);
        }

        [Fact]
        public void Resolve_PacksRootMissing_DoesNotThrow()
        {
            PackResolution res = PackLocator.Resolve(Path.Combine(_root, "nope"), "zh-cn");
            Assert.Equal(PackDirStatus.PacksRootMissing, res.Status);
            Assert.Null(res.ChosenDir);
            Assert.Empty(res.PackFiles);
        }

        [Fact]
        public void Resolve_HostileLanguage_IsRejectedWithoutThrowing()
        {
            Dir("zh-cn");
            FileIn("zh-cn", "Squad.jsonl");

            foreach (string hostile in new[] { "..", "../..", "zh/../../etc", "zh\u0000cn", "***", "" })
            {
                PackResolution res = PackLocator.Resolve(_root, hostile);
                Assert.NotNull(res);
            }
        }
    }
}
```

> 骨架里的 `Assert.Equal(new[] { ... }, ...)` 在 xunit.v3 下按序列比较，能直接断言候选顺序；
> 落地时把 16 条用例补齐（这里只给 5 条示范，避免提案里塞一份完整实现让实施者照抄出错）。

## 7. 为什么是 Core、为什么不是「给 ConfigLoader 加测试」

1. **依赖方向**：Core 不知道 KSP，Runtime 依赖 Core；把「路径规则」放 Core 是唯一不引入反向依赖的落点。
2. **双目标红利**：Core 是 `net472;net10.0`，同一份逻辑在 KSP 运行时与单测里跑的是**同一份 IL**，不存在「测的是 A、跑的是 B」。
3. **零依赖守卫**：`UI18nCore_RejectGameDependencies` 保证 PackLocator 不会悄悄长回 Unity 依赖。
4. **失败可计数**：红线 6 要求「失败只 log 不抛但必须计数」——`PackDirStatus` 就是那个「计数」的载体，比在 Runtime 里散落 `if (Directory.Exists)` 更不容易漏。

## 8. 明确不做的事（避免范围膨胀）

- 不做**多语言合并**（不同时读 `zh-cn` 与 `zh`）：现在语义是「最具体目录赢」，合并会让同 hash 冲突规则变复杂。
- 不做**热重载 / 目录监视**（`FileSystemWatcher`）：与「启动期一次性加载」的现有设计冲突。
- 不改 JSONL 格式、不改 `DictPack` 的任何语义（本提案只碰路径解析）。
- 不把 `MaxEntries` 逻辑搬进 Core：它是加载策略（`ConfigLoader.cs:209-233`），与路径解析无关。
- **现在不改 `src/`**：等 Lead 排期、由 core-engineer 落地，避免与其正在改的 `ConfigLoader` 撞车。

## 9. 验收标准（落地时照这个验）

1. `src/UI18n.Core/Dict/PackLocator.cs` 新增，`dotnet build UI18n.sln -c Release` 双目标 0 警告 0 错误；
2. `tests/UI18n.Core.Tests/PackLocatorTests.cs` 覆盖 §6 的 16 条，`pwsh tools/run-tests.ps1` 全绿（总数从 287 涨到 287+16 例量级）；
3. `grep -n "LanguageCandidates\|ResolvePackDir\|NormalizeLanguage" src/UI18n.Runtime` 只剩注释或 0 命中（逻辑单点）；
4. `LoadPacks` 的日志里有 `status=` 与 `flat=` 字段；
5. `deploy.ps1` 安装后自检能报出平铺 `*.jsonl`（可选，加分项）。

## 10. 附：本提案与已交付测试的关系

- `LanguageGateVariantMatrixTests.cs`（本轮新增）钉的是「语言串**是否**中文」，发生在 PackLocator **之前**；
  PackLocator 处理的是「**已经确定要翻译**之后，去哪读文件」，两者不重叠。
- `CorpusContractTests.cs` 钉的是「样本行能解析、hash 与文本一致」；PackLocator 只决定「读哪些文件」，不解析内容。
- 因此本提案落地不会改动任何现有断言，只会新增 `PackLocatorTests.cs`。

