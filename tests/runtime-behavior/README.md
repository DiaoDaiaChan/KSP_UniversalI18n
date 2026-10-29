# Runtime 行为门禁（tests/runtime-behavior）

R13 起的第三层 Runtime 验证。三层各管一件事，缺一层都会留下「CI 全绿但运行时是错的」窗口：

| 层 | 位置 | 回答的问题 |
| --- | --- | --- |
| 单测 | `tests/UI18n.Core.Tests`（1,067 例） | Core 的纯逻辑对不对（哈希 / 归一化 / 三层查找 / 骨架 / 捕获） |
| 影子语义编译 | `tests/runtime-compile` | `src/UI18n.Runtime` 那 29 个 `.cs` **能不能编译**（成员名、参数类型） |
| **行为场景**（本目录） | `tests/runtime-behavior` | 那 29 个 `.cs` **跑起来**会不会按设计工作 |

## 做法

`RuntimeBehavior.csproj` 把三样东西编进**同一个可执行程序集**：

- `tests/runtime-compile/stubs/**` —— 与影子编译同一批最小桩（Unity / KSP / Harmony）；
- `src/UI18n.Runtime/**/*.cs` —— 真实的 29 个 Runtime 源文件，一行不改；
- 本目录的 `Program.cs` / `Scenarios.cs` —— 断言与场景。

同程序集 ⇒ `internal` 成员（`Translator` / `ConfigLoader` / `MissCapture`）直接可调，**不需要**任何
`InternalsVisibleTo`。这是刻意的：产品侧不为测试开后门，`test-runtime-behavior.py` 会断言
`src/UI18n.Runtime` 里没有 `InternalsVisibleTo`。

每个场景把 `KSPUtil.ApplicationRootPath` 指到自己的临时目录，在
`<root>/GameData/UI18n/Packs/zh-cn/*.jsonl` 里写下真的字典行（`h` 用产品自己的
`UI18n.Core.Hashing.Fnv1a.Hash` 算，`s` 全用 ASCII 所以 `n == s`），然后调用产品的真入口。

> 因为 `KSPUtil.ApplicationRootPath` 是全局静态字段，**场景必须串行跑**（`Program.Main` 里就是顺序调用）。
> 加并行会互相改到别人的 KSP 根。

## 九个场景（75 条断言）

| 场景 | 钉住的行为 |
| --- | --- |
| S1 包加载 + 命中 + 缓存 | `ConfigLoader.LoadPacks` 读 JSONL；`Translator.Translate` 命中 / 重复命中走缓存 / 真未命中返回 null；命中与未命中都进缓存（未命中存空串）；`DistinctHits` / `SampleCount` 只记首次 |
| S2 三层优先级 | 同一个 `h` 的 global / site / ctx 三条行：无位置信息的查询拿 site、带 ctx 的拿 ctx、别人的 ctx 落回 site（T8 契约） |
| S3 骨架填回 | 骨架只进骨架索引（精确索引看不到它）；`Crew: 7` → `乘员：7`；没有数字的串不骨化 |
| S4 keep 行 | `t == s` 的条目算未命中、翻译返回 null，且**不进**缺命中捕获（`04 §6.3 门禁 5`） |
| S5 per-mod 策略 | `policies.json` 里 NATIVE 的包在加载期被滤掉；策略表损坏时退化成「全部加载」，不连累翻译 |
| S6 缺命中捕获 | 未命中 → `FilePath` → `Flush` → 文件一行、字段齐（`h`/`n`/`s`/`sink`/`firstSeen`）；同一串第二次只 `CountMiss`，不写第二行 |
| S7 `Enabled=false` | 全短路：不翻译、不缓存、不捕获；重新打开立刻恢复 |
| S8 `MaxEntries` | 条目上限是硬的（包文件按名排序，超出的整段不吃） |
| S9 没有包目录 | 空包、不抛异常、`Translator` 全短路 |

## 跑法

```bash
python tests/runtime-behavior/test-runtime-behavior.py     # 门禁：编 + 跑 + 反向对照 + 边界，13 项
dotnet build tests/runtime-behavior/RuntimeBehavior.csproj -c Release
dotnet tests/runtime-behavior/bin/Release/net10.0/UI18n.Runtime.Behavior.dll        # 直接看场景输出
dotnet tests/runtime-behavior/bin/Release/net10.0/UI18n.Runtime.Behavior.dll --keep # 保留临时 KSP 根
```

退出码：`0` 全过 / `1` 有失败 / `2` SKIP（本机没有 dotnet）。`tools/run-tests.ps1` 的 4d 步与 CI 的
`code` job 都把 `2` 当失败 —— 那时 dotnet 刚用过，报 SKIP 说明环境坏了。

## 它证明什么 / 不证明什么

**证明**：Runtime 的胶水层（包加载顺序、优先级、骨架、keep、策略、捕获、开关、上限）在真数据上按设计工作；
断言真的会失败（脚本会把一份场景源码的期望译文改错，编译必须成功、运行必须退出非 0 且打出 `FAIL:`）。

**不证明**：

- 桩不是 Unity / KSP 的真实签名 —— 真机 API 变了这里看不出来（那一层是 `tests/runtime-compile` 与真机编译）；
- 没有任何 Unity 对象参与：`GameObject` / `GUIContent` / 字体 / 帧循环都不在范围内；
- `Config.cfg` 的解析不在范围内（桩的 `ConfigNode.Load` 恒返回 `null`，所以 `ConfigLoader.Load()` 测不了）；
- 只编 `net10.0`：行为语义与 TFM 无关，`net472` 的签名权威性由影子编译负责。

## 加场景的纪律

1. 一个场景一个方法，签名固定为 `internal static void Xxx(string gameRoot)`，第一句必须是
   `Env.Begin(gameRoot);`（它建目录并改 KSP 根）。
2. 数据一律从磁盘造（`Env.WritePack` / `Env.Row`），**不要**在测试里手工 new `DictPack` ——
   那会把 JSONL schema 与解析路径排除在外，等于只测了字典的一半。
3. 期望值写字符串常量，不要复算产品逻辑（例如骨架：用 `TextSkeleton.TryBuild` 生成骨架行，
   但断言里的译文是字面量）。
4. 新场景要同步三处：`Program.cs` 的场景表、`test-runtime-behavior.py` 的 `MIN_SCENARIOS`、
   本 README 的表格。
5. 别在场景里留静态状态：`Translator.Initialize` 会清缓存/样本/捕获器，`MissCapture` 用完要 `Shutdown()`。
