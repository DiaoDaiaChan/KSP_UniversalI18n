# 02 · Debug 探针系统设计

> 状态：**设计草案（调研阶段）**。本文只定"观测什么、怎么观测、产出什么"，不承诺实现排期。
> 前置：00-总体设计.md、01-RP1GUIZh-缺陷审查.md
> 本文所有签名结论均为对本机 KSP 1.12.5.3190 的实测，证据见附录 A。
>
> **v0.12（2026-10-23）**：「缺命中采集」这件事已经换道 —— 运行时有捕获通道（`CaptureMisses=True` / `CaptureMaxEntries=5000`，
> 落 `PluginData/UI18n/capture-<时间戳>.jsonl`，字段与过滤码见 04 §3.7.2），它比探针更贴近真实路径（round 9 一局抓到 4,040 条字典外 distinct）。
> 本文的探针仍然只做两件事：IMGUI 取证（把目标画上屏）与调用/命中计数；**不要再用探针当缺命中采集的主通道**。

---

## 0. 这份文档解决什么问题

整个项目最大的风险不是"翻译得不好"，而是**在错误的前提上把架构搭完了**。

00-总体设计 里已经有四层架构，但那四层里有三个关键参数是猜的：

| 猜的参数 | 猜错会怎样 | 谁来定这个参数 |
|---|---|---|
| 屏幕上真正出现多少条文本 | LLM 预算估错一个数量级；词典结构选错 | 运行时探针 |
| 每条文本是谁发的 | ctx 键设计失效；per-mod 冲突无法仲裁 | 运行时探针 + 静态探针 |
| 系统里到底有没有中文字体 | L3 字体兜底做晚了，整个 mod 上线即豆腐块 | 环境探针 |

所以**先有探针，再有实现**。探针不是调试工具，是设计输入。

一句话定位：**探针是本项目唯一允许"只看不做"的组件，也是唯一必须在写翻译代码之前完成的东西。**

---

## 1. 探针要回答的 9 个问题

每个问题都直接绑定一个后续设计决策。答不上来就不能开工。

| # | 问题 | 绑定的决策 | 数据来源 |
|---|---|---|---|
| Q1 | 一个场景里屏幕上真实出现多少条去重文本？全流程累计多少？ | LLM 预算、词典规模、是否需要分片 | L2 |
| Q2 | 每条文本是哪个 assembly / 哪个类发的？ | ctx 键是否可用、per-mod 归属、冲突仲裁 | L2 + L1 |
| Q3 | 哪些是静态字面量，哪些是运行时拼出来的（含数字、含玩家命名）？ | 精确匹配 vs 模板匹配 vs 正则 | L2 + L1 |
| Q4 | 其中多少已经含中文？多少含 KSP 的 #autoLOC_？ | 是否需要"已翻译标记"、是否与原生 i18n 打架 | L2 + L1 |
| Q5 | 富文本标签和占位符的真实形态是什么？ | CI 七条门禁的规则怎么写 | L2 |
| Q6 | 同一个英文字符串在不同 mod 里含义是否冲突？ | global 层能不能存在、要不要 per-mod 覆盖表 | L2（按 caller 分组比对） |
| Q7 | 当前环境有没有能渲染中文的字体？TMP fallback 链是什么样？ | L3 是否必须进 M0 | L3 |
| Q8 | 探针自身的开销是多少 ns？ | L2 拦截层能有多重、能不能做实时翻译 | L2 自测 |
| Q9 | 未打开的界面看不到——静态扫描能补多少？ | 两条采集线的配比 | L1 vs L2 对比 |

> Q9 是这套系统里最容易被忽略的一条：运行时探针只能看到"玩家走到过的界面"。装配厂房里的一个报错弹窗，如果没人触发就永远采不到。所以必须有一条离线的静态线兜底。

---

## 2. 设计红线（六条，全部来自 RP1GUIZh 的教训）

01 文档查出来的坑，逐条转成探针的硬约束：

1. **只读。** 任何探针不得写入参、返回值、静态字段、游戏状态。`GUIContent.text` 只读不写。
2. **Prefix 必须是 void。** C# 语言层面就保证它不可能跳过原方法——这是免费的防呆。
3. **禁止 transpiler。** RP1GUIZh 的 Contract.Save transpiler 是"现在侥幸没炸"级别的东西（01 文档 §1）。探针一行 IL 都不改。
4. **只碰渲染层。** 观测点白名单只允许 Unity 引擎的 UI 漏斗类型。不 patch Contract、不 patch KSC、不 patch 任何玩法类型。
5. **异常自吞并计数。** 探针体内任何异常都在探针内部终止，绝不冒泡进 Unity 渲染循环，且计入 `exceptions` 供事后核查。
6. **必须有预算上限。** 每帧调用数、去重条目数、调用栈抓取次数三道上限，超限只计数不记录。

第 7 条隐含约束：**探针必须可一键关闭**。发布版不带探针。

---

## 3. 三层探针

三层是正交的，不能互相替代。

### 3.1 L1 静态探针（离线 IL 扫描）

**做什么**：不启动游戏，用 Mono.Cecil 遍历 GameData 下所有 DLL 的 IL，找"字符串字面量 → 是否流向已知 UI sink"的调用链。

**为什么必须离线**：

- 能覆盖玩家永远走不到的界面（报错分支、罕见配置、Debug 面板）。
- 能给出**完整的**调用点清单，不受采样影响。
- 能在 CI 里跑，不依赖人的手工操作。

**输出**：`sites.jsonl`，每行一个 `(assembly, type, method, ilOffset, textLiteral, sinkChain)`。

**已知限制**（来自 00 文档的实测）：

- 纯字符串字面量启发式筛"像 UI 文本"的精度只有 5,423 / 157,268 ≈ 3.4%，**噪声太大不可用**。必须做真正的调用点回溯：字面量 → ldstr → 是否被 push 给已知 sink 的参数位。
- 拼接字符串（`"Cost: " + n`）在 IL 里是 `ldstr` + `String.Concat`，静态只能拿到片段，拿不到运行时形态。
- 反射调用、从 cfg 读的文本、从 Localization 读的文本，静态看不到。

### 3.2 L2 运行时探针（Harmony 只观测）

**做什么**：在渲染漏斗上装 void Prefix，记录**真实流过屏幕**的文本。

**这是唯一能回答 Q1/Q2/Q5/Q6/Q8 的一层。** 静态扫描给的是"可能出现的文本"，运行时给的是"确实出现的文本"，两者差集本身就是重要信息（说明静态有假阳性）。

**核心机制**：

- Prefix 只声明需要的那一个参数（`__0` / `__1` / `__instance`），不碰别的。
- 热路径只做：空判 → 一次 FNV-1a 哈希 → 一次字典查找。命中即返回。
- **只在首次见到某字符串时**才做三件贵的事：保存原文、分类、抓调用栈。
- 自带耗时采样，直接给出 p50/p99，用来验证性能预算。

### 3.3 L3 环境探针（一次性快照）

**做什么**：把"这台机器当前是什么状态"完整拍下来。

内容：当前语言、`settings.cfg` 里写的 LANGUAGE、`buildID.txt`、已加载程序集清单、所有 TMP 字体资产及其汉字覆盖、fallback 链、屏幕分辨率。

**为什么单独一层**：Q7 的答案不在任何一次 UI 调用里。字体是全局状态，拍一次就够了。而且这一层是**唯一能在不玩游戏的情况下判断"中文能不能显示"**的手段。

### 3.4 三层缺一不可

| 场景 | L1 静态 | L2 运行时 | L3 环境 |
|---|---|---|---|
| 装配厂房里 10 个按钮的文本 | 能 | 能（去过就有） | 不能 |
| 某个只在报错时出现的弹窗 | 能 | 大概率不能 | 不能 |
| 运行时拼出来的 "Cost: 1234" | 只能拿到片段 | 能 | 不能 |
| 这条文本是哪个 mod 发的 | 能（精确到 IL 偏移） | 能（调用栈） | 不能 |
| 中文字体到底有没有 | 不能 | 不能 | 能 |
| 探针自己的开销 | 不能 | 能 | 不能 |

---

## 4. 观测点清单

### 4.1 为什么选这些点

判据只有一条：**它是"分发漏斗"吗？**

`GUILayout.Label(string)` 有 3 个公开重载，但它们**全部**汇入 `GUILayout.DoLabel(GUIContent, GUIStyle, GUILayoutOption[])`。打一个点，覆盖三个重载。这就是漏斗。

反过来，`GUI.Label(Rect, string, GUIStyle)` 和 `GUI.Label(Rect, GUIContent, GUIStyle)` 是两个独立方法，各自有实现，必须分别打——虽然实测 2 参重载会委派给 3 参，但 string 重载和 GUIContent 重载之间没有委派关系。

### 4.2 候选 sink 表

| ID | 目标方法 | 实测签名 | 文本位置 | 覆盖 | 状态 |
|---|---|---|---|---|---|
| imgui.guilayout.label | `UnityEngine.GUILayout.DoLabel` | `private static void (GUIContent content, GUIStyle style, GUILayoutOption[] options)` | `#0` | `GUILayout.Label` 的 string / GUIContent / Texture 全部重载 | 已实测确认 |
| imgui.guilayout.button | `UnityEngine.GUILayout.DoButton` | `private static bool (GUIContent, GUIStyle, GUILayoutOption[])` | `#0` | `GUILayout.Button` 全部重载 | 已实测确认 |
| imgui.guilayout.repeatbutton | `UnityEngine.GUILayout.DoRepeatButton` | `private static bool (GUIContent, GUIStyle, GUILayoutOption[])` | `#0` | `GUILayout.RepeatButton` 全部重载 | 已实测确认 |
| imgui.guilayout.toggle | `UnityEngine.GUILayout.DoToggle` | `private static bool (bool value, GUIContent content, GUIStyle style, GUILayoutOption[] options)` | `#1` | `GUILayout.Toggle` 全部重载 | 已实测确认 |
| imgui.guilayout.window | `UnityEngine.GUILayout.DoWindow` | `private static Rect (int id, Rect screenRect, GUI.WindowFunction func, GUIContent content, GUIStyle style, GUILayoutOption[] options)` | `#3` | 所有 GUILayout 窗口标题 | 已实测确认 |
| imgui.gui.label | `UnityEngine.GUI.Label` | `public static void (Rect position, GUIContent content, GUIStyle style)` | `#1` | 2 参重载委派到此 | 已实测确认 |
| imgui.gui.button | `UnityEngine.GUI.Button` | `public static bool (Rect position, GUIContent content, GUIStyle style)` | `#1` | 同上 | 已实测确认 |
| imgui.gui.box | `UnityEngine.GUI.Box` | `public static void (Rect position, GUIContent content, GUIStyle style)` | `#1` | 同上 | 已实测确认 |
| imgui.gui.toggle | `UnityEngine.GUI.Toggle` | `public static bool (Rect position, bool value, GUIContent content, GUIStyle style)` | `#2` | **漏 5 参重载，见 4.3** | 部分 |
| imgui.gui.toggle.id | `UnityEngine.GUI.Toggle` | `public static bool (Rect position, int id, bool value, GUIContent content, GUIStyle style)` | `#3` | 带 id 的 toggle | 待加 |
| imgui.gui.window | `UnityEngine.GUI.Window` | `public static Rect Window(int, Rect, WindowFunction, string/GUIContent/Texture [, GUIStyle])` ×6 重载 | **文本在 #3**（string 或 GUIContent） | uGUI 之前的窗口标题 | 只打 6 个公开重载即可；公共漏斗是 `private static Rect DoWindow(int id, Rect clientRect, WindowFunction func, GUIContent title, GUIStyle style, GUISkin skin, bool forceRectOnLayout)`，但它 private 且多一个 GUISkin 参数，不如直接打公开重载 |
| ugui.text | `UnityEngine.UI.Text.set_text` | 属性 `public virtual string text` 的 setter | `#0` | 所有 uGUI 传统文本 | 已实测确认（属性形态） |
| tmp.text | `TMPro.TMP_Text.set_text` | Harmony 报错原文：`System.Void TMPro.TMP_Text::set_text(System.String value)` | `#0` | 所有 TMP 文本（含 TextMeshProUGUI） | 已实测确认（间接） |
| screen.message | `ScreenMessages.PostScreenMessage` | **10 个重载，参数 #0 恒为 string message** | **文本在 #0** | 屏幕中央飘字（“Throttle up”、“Science collected”） | **无公共漏斗**：10 个重载各有独立方法体（反编译能看到各自的编译器 switch 尾巴），不互相委派 → 全覆盖必须逐个 patch。调用点实测 153 处，优先级低于 IMGUI 与两个 setter；M0 先打 `(string, float)` 与 `(string, float, ScreenMessageStyle)` 两个最常用的 |

### 4.3 三个必须记住的实测结论

**① GUILayout 的漏斗是 private，不是 internal。**

`DoLabel` / `DoButton` / `DoRepeatButton` / `DoToggle` / `DoWindow` 全部是 `private static`。反射查找必须带 `BindingFlags.NonPublic | BindingFlags.Static`，且 `DeclaredOnly` 更稳。用 `AccessTools.Method` 默认能拿到 private，但自己写查找时容易漏。

**② `GUIContent.Temp` 复用静态实例 —— 这是"绝不能原地写 content.text"的直接证据。**

反编译显示 `GUILayout.Label(string text)` 的实现是：

```
DoLabel(GUIContent.Temp(text), GUI.skin.label, options);
```

`GUIContent.Temp()` 返回的是 Unity 内部的**共享缓存实例**，不是新建对象。这意味着：任何"读 content.text → 翻译 → 写回 content.text"的实现，都会把翻译结果永久写进一个全游戏共用的对象里。下一次别的 mod 传进来一个英文串，可能读到的还是上一次的中文。

01 文档 §8 提出的"GUIContent 改为构造新对象不原地写"由此从"建议"升级为**硬约束**。

**③ `GUI.Toggle` 有两个重载会漏。**

- `Toggle(Rect, bool, GUIContent, GUIStyle)` —— 4 参，文本在 `#2`
- `Toggle(Rect, int id, bool value, GUIContent, GUIStyle)` —— 5 参，文本在 `#3`

只按"参数个数 = 4"匹配会漏掉带 id 的那个。探针的 sink 定义必须**按"第几个参数是 GUIContent"来匹配，而不是按参数个数**——参数个数只是辅助过滤。

### 4.4 明确不碰的点

| 目标 | 为什么不做 |
|---|---|
| `GUI.TextField` / `GUILayout.DoTextField` | 输入框，玩家正在打的字。采集价值低、隐私风险高。L3 之外一律跳过。 |
| `UnityEngine.UI.InputField.set_text` | 同上。 |
| `Contract.Save` / `KCT_GUI` / 任何玩法类型 | RP1GUIZh 的四个玩法 hack。绝对红线。 |
| `OnGUI` 本身 | 粒度太粗，拿不到具体文本。 |
| `Localizer.Format` / `GetString` | 那是**已经支持 i18n 的 mod** 走的路径，属于"优先原生"分支，不是探针目标。 |

---

## 5. 数据契约

### 5.1 键：为什么用 hash 而不是原文

热路径上不能分配内存，也不能把整个字符串当字典键做比较（长度不定，比较成本高）。所以：

- 用 **FNV-1a 64 位** 对原文求哈希，作为去重键。
- 哈希冲突用"保存首次见到的原文 + 后续按 `StringComparison.Ordinal` 比对"来兜底，冲突次数单独计数。
- 哈希值同时就是未来字典的 `textHash` 键——探针产出的东西能直接进采集流水线，不需要二次转换。

> 为什么不用 `string.GetHashCode()`：.NET 的字符串哈希在不同进程/不同运行时版本下可能不同（尤其随机化哈希种子的实现），不能作为跨机器持久化的键。FNV-1a 是自己实现的，稳定。

> **已知限制（字典模型层面）**：文本以 `#` 开头的行是 **Localization 键引用**（`#autoLOC_*` 等）。
> 探针与 harvest 拿到的都是 IL 里的字符串字面量，而 `KSP.Localization.Localizer` 在我们之前就把键解析成显示文本了，
> 所以这类行在运行时匹配不到 —— 处理办法是后处理两步（追加「解析行」→ 零覆盖损失证明通过后删掉键行本身），
> 见 [04 §3.4](04-翻译流水线与预处理.md) 与 §3.5。

### 5.2 运行时探针输出（JSONL，一行一条）

| 字段 | 类型 | 含义 |
|---|---|---|
| `h` | string | FNV-1a 64 位哈希，16 位十六进制 |
| `n` | int | 本次采集窗口内出现次数 |
| `sink` | string | 观测点 ID，如 `imgui.guilayout.label` |
| `text` | string | 原文（首次出现时捕获） |
| `len` | int | 字符长度 |
| `rt` | bool | 含富文本标签 `<b>` `<color=...>` |
| `ph` | bool | 含占位符 `{0}` `%s` `<<1>>` |
| `dg` | bool | 含数字 |
| `cjk` | bool | 含中日韩字符 |
| `na` | bool | 含非 ASCII |
| `num` | bool | 纯数字/单位（不可译） |
| `asm` | string | 调用方 assembly |
| `type` | string | 调用方类型全名 |
| `m` | string | 调用方方法名 |
| `inst` | string | 触发 patch 的实例类型（uGUI/TMP 路径） |
| `f0` / `f1` | int | 首次/末次出现的帧号 |
| `coll` | bool | 出现过哈希冲突（仅异常时出现） |

### 5.3 汇总输出（summary.json）

除 totals 外，最重要的是 `sinks[]` 和 `callers[]`：

- `sinks[]`：每个观测点是否打上、调用次数、空串次数、去重条数、异常次数。**"打上没打上"比"打了多少"更重要**——RP1GUIZh 的 TMP 从头到尾没打上，但日志让人以为打上了。
- `callers[]`：按 assembly 聚合的去重文本数。这张表直接就是"翻译工作量 per mod 分布"。

### 5.4 环境快照（inventory.json）

- `localizerCurrentLanguage` / `localizerLanguages` / `settingsCfgLanguage` / `buildIdTxt` —— 四个来源互相印证，历史上这四个值不一致过（本机 `settings.cfg` 写 `en-us`，`buildID.txt` 写 `zh-cn`）。
- `fonts[]`：每个 TMP 字体资产的 `name` / `glyphs` / `cjkProbeHit` / `cjkProbeTotal` / `fallbacks` / `populationMode`。
  - `cjkProbeHit` 用 20 个高频汉字做探针（的 一 是 在 不 了 有 和 人 这 中 大 为 上 个 国 我 以 要 他），命中 0 就说明这个字体一个汉字都没有。
- `assemblies[]`：已加载的 mod 程序集清单。

---

## 6. 归因机制（Q2 的核心）

**问题**：`GUILayout.DoLabel` 是被 Unity 引擎调用的，调用栈上直接相邻的帧没有 mod 信息。mod 的帧在更深的位置。

**做法**：`new StackTrace(2, false)` 之后向下遍历，跳过这些帧：

- 探针自己的 assembly
- `mscorlib` / `System` / `System.Core`
- `UnityEngine*` / `Unity.*`
- `0Harmony`
- `Assembly-CSharp`（KSP 本体，不算 mod）

第一个剩下的就是 mod。

**成本控制**：

- 只在**首次见到某字符串**时抓一次，之后缓存在条目里。
- 全局预算（默认 4,000 次），用完就停，剩余条目标记为 `(engine)` 或留空。
- `StackTrace` 单次开销在微秒级，4,000 次约 4–40ms，摊在启动阶段可接受；但绝不能放在热路径上。

**已知盲区**：反射调用、委托调用、协程会让调用栈失真。这类条目会落进 `(engine)` 或错误的 assembly，需要在分析阶段人工抽查。

---

## 7. 性能预算与自测

00 文档定的目标是：命中 < 50ns、未命中 < 15ns、整帧增量 < 0.05ms。**这个目标现在是假设，必须由探针自测验证。**

**自测方法**：探针自己按 1/64 采样，记录每次 `OnText` 的耗时，输出 p50 / p99 / max。

- 如果 p99 远超 50ns，说明"命中路径零分配"没做到，或者字典查找比预期贵——那么 L2 的拦截密度必须下调（例如只打 3 个漏斗而不是 11 个）。
- 如果 p50 在 20ns 以内，说明预算还有余量，可以考虑加"已翻译标记"缓存。

**必须单独测的两种情况**：

1. 冷启动（字典为空，全是未命中）。
2. 稳态（字典已加载，大部分命中）。

这两种情况的开销差一个数量级，只测一种会得出错误结论。

---

## 8. 开关、配置与热键

| 配置项 | 默认 | 说明 |
|---|---|---|
| `Enabled` | true | 总开关 |
| `CaptureCallers` | true | 是否做调用栈归因 |
| `CallerBudget` | 4000 | 调用栈抓取上限 |
| `MaxEntries` | 60000 | 去重条目上限，超限只计数 |
| `MaxCallsPerFrame` | 40000 | 每帧调用上限，防病态卡顿 |
| `SkipInputFields` | true | 跳过输入框（隐私） |
| `DumpOnSceneChange` | true | 场景切换时落盘并清零 |
| `DumpOnDestroy` | true | 退出时落盘 |
| `HudEnabled` | true | 屏幕角标 |
| `HotkeyDump` / `HotkeyToggle` / `HotkeyClear` | f10 / f11 / f9 | 热键 |
| `TimingSampleEvery` | 64 | 耗时采样率 |
| `OutputDir` | 空（默认 `PluginData/UI18n/probe`，与插件自身的数据目录同名，D34） | 输出目录 |
| `SinkWhitelist` | 空（全开） | 逗号分隔，只启用指定观测点 |

**自噪声问题**：探针自己的 HUD 也调 `GUI.Label`，会被自己观测到。必须用一个 `InProbeGui` 标志在 HUD 的绘制期间短路掉采集，否则 summary 里会出现大量 "UI18nProbe" 自己的文本。

---

## 9. 隐私与安全

1. **输入框一律不采**（默认）。玩家的存档名、飞船名、聊天内容都不应该出现在探针产物里。
2. **产物含原文**，所以 `PluginData/UI18n/probe/` 下的文件在上传前必须人工过一遍。后续流水线要有 `--scrub` 开关。
3. **不写游戏状态**。探针不产生存档副作用，删掉整个目录即可完全回滚。
4. **不联网**。探针本身零网络行为，采集产物靠人工/CKAN 之外的手段流转。

---

## 10. 采集作业单（怎么用）

探针有了之后，需要按固定路线跑一遍，否则数据不可比。

| 阶段 | 场景 | 停留时间 | 目的 |
|---|---|---|---|
| 1 | 主菜单 → 设置 → 语言 | 2 min | 主菜单与设置页文本 |
| 2 | 新建游戏 → 航天中心 | 5 min | KSC 主界面、建筑提示、首次运行引导 |
| 3 | 装配厂房（VAB） | 10 min | 部件列表、部件信息、编辑器工具栏、PartList 的 title/description |
| 4 | 发射台 → 飞行 | 5 min | 飞行 HUD、右键菜单、资源面板 |
| 5 | 跟踪站 / 任务控制中心 | 3 min | 任务文本、合同文本 |
| 6 | 触发一次报错（故意装错一个 mod） | 1 min | **报错弹窗文本——静态线的主要价值区** |

每个阶段结束按热键落盘，产物按 `阶段号-场景名` 归档。

> 第 6 阶段是刻意设计的：报错路径的文本玩家一辈子见不到一次，但它恰恰是最需要翻译的（用户看不懂报错就只能来问你）。

---

## 11. 验收标准

探针系统"可用"的判据，全部可量化：

1. 11 个候选 sink 里 **≥ 9 个成功打上**，且失败项有明确的失败原因（不是"没有原因"）。
2. 跑完 §10 的六个阶段，产出 **≥ 3 个场景**的 JSONL + summary + 一份 inventory。
3. `summary.json` 里 `exceptions` = 0，`hashCollisions` = 0。
4. 探针自测 p50 **< 50ns**，p99 **< 500ns**。
5. inventory 能明确回答 Q7：**这台机器上有没有能显示中文的 TMP 字体**。
6. 关掉探针（`Enabled = false`）后，游戏行为与未安装时**逐帧一致**（人工目视 + 帧率对比）。

第 6 条是最终的安全判据，也是最容易被跳过的一条。

---

## 12. 探针产物怎么变成字典

闭环的接口（对应 00 文档的字典三层键）：

```
L2 产物 (sites.jsonl + summary)
    │  asm + type + method  → ctx {asm}|{typeFullName}|{member}|{ordinal}
    │  textHash             → site / global 层
    ▼
采集器归一化（去重、合并同一 textHash 的多 caller）
    ▼
packs/pool.jsonl  →  LLM 翻译  →  packs/zh-cn/<modId>.jsonl
    ▼
CI 八条门禁  →  publish  →  GameData/UI18n/Packs/zh-cn/*.jsonl（游戏内加载，D34/D35）
```

关键点：**探针的 `textHash` 必须和运行时字典的键是同一个算法**。否则采集到的和运行时查的不是一个键，闭环断在中间。这就是 §5.1 选 FNV-1a 而不是 `GetHashCode()` 的原因。

一个必须记住的例外：如果字面量本身就是键引用（`#autoLOC_500832` 这种），它在运行时会被 KSP 先解析成当前语言的显示文本再送进 UI，
所以「`s` = 键」的那一行**永远匹配不到**。这不是 bug、是 harvest 阶段拿不到解析后文本的结构性限制；
补法是后处理 `resolve_keyrefs`：先为这些行追加一条解析行，再在零覆盖损失证明通过后把键行删掉
（v0.5 策略，见 [04 §3.4](04-翻译流水线与预处理.md)）。

---

## 13. 风险

| 风险 | 影响 | 缓解 |
|---|---|---|
| 运行时探针只能看到走过的界面 | 覆盖率虚高，以为采全了 | L1 静态线兜底；§10 作业单强制跑报错路径 |
| 调用栈归因在委托/协程下失真 | per-mod 归属错误 | 人工抽查；对 `(engine)` 占比设阈值告警 |
| 探针开销被低估 | 加探针后游戏明显掉帧 | 自测 p50/p99 + 稳态/冷启动双测 |
| 产物含玩家输入 | 隐私 | 输入框默认跳过 + 上传前人工过 |
| 探针自己成了新的"玩法 hack" | 重蹈 RP1GUIZh | 六条红线 + 只碰渲染层 + 发布版不带 |
| 观测点漏（如 GUI.Toggle 5 参重载） | 数据不完整 | 按"第几个参数是 GUIContent"匹配，不按参数个数 |

---

## 附录 A · 本次实测的工具链事实

记下来省得下次重查：

1. **ilspycmd** 在 `C:\Users\43701\.dotnet\tools\ilspycmd.exe`，是 net6.0 应用。本机只有 .NET 10，直接跑会报 `You must install or update .NET`。解法：设 `DOTNET_ROLL_FORWARD=Major`。
2. `ilspycmd -t <类型全名> <dll>` 对 **Unity 引擎程序集工作正常**（GUILayout / GUI 的签名就是这么拿到的）。
3. `ilspycmd -t` 对 **Assembly-CSharp 里的类型不可靠**：对 `TMPro.TMP_Text` 返回的是 64,000 字符截断的无关输出，对 `TMPro.TextMeshProUGUI` 同理。查这类类型要么用 `-il` 全量导，要么直接用 Mono.Cecil。
4. `GUILayout.DoLabel` 等漏斗是 **private static**，反射需要 `BindingFlags.NonPublic | BindingFlags.Static`。
5. `GUILayout.Label(string)` 的实现体是 `DoLabel(GUIContent.Temp(text), GUI.skin.label, options)`。
6. `GUI.Label(Rect, GUIContent)` 的实现体是 `Label(position, content, s_Skin.label)` —— 2 参委派给 3 参。
7. `UnityEngine.UI.Text.text` 是 `public virtual string text` 属性，IL 层对应 `set_text` / `get_text`（所以按源码文本搜 `set_text` 搜不到）。
8. TMP 的 setter 全名由 Harmony 报错原文确认：`System.Void TMPro.TMP_Text::set_text(System.String value)`。**不能** patch `TextMeshProUGUI.set_text`（继承来的方法，Harmony 会抛 `You can only patch implemented methods/constructors`）。

## 附录 B · 术语

| 术语 | 含义 |
|---|---|
| sink | 观测点 / 分发漏斗：一个"文本从这里流向屏幕"的方法 |
| 漏斗 | 多个公开重载最终汇入的同一个内部方法 |
| ctx 键 | 由 asm + 类型 + 方法 + 序号构成的上下文键 |
| textHash | FNV-1a 64 位哈希，跨机器稳定的文本键 |
| 自噪声 | 探针观测到自己的 UI 文本 |
| 归因 | 判断某条文本是哪个 assembly 发出的 |
