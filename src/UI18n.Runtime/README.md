# UI18n.Runtime

KSP 侧插件（net472）：**只做渲染层拦截**。数据能解决的文本一律不在这里处理（红线 7）。
本程序集引用 UnityEngine / Assembly-CSharp / 0Harmony，但**没有任何 transpiler**，也**绝不写 `GUIContent.text`。

## 1. 启动顺序（UI18nAddon，Startup.Instantly）

| 步骤 | 行为 | 失败时 |
| --- | --- | --- |
| 1 读配置 | `GameData/UI18n/Config.cfg` | 文件缺失 / 值非法 → 用默认值，只 warn |
| 2 读语言 | `KSP.Localization.Localizer.CurrentLanguage` | 读不到 → 当作非中文，只 warn |
| 3 **语言门禁** | `LanguageGate.ShouldTranslate(lang, ForceOverride)` | 判假 → **装 0 个 patch** 并 return |
| 4 读字典 | `GameData/UI18n/Packs/<lang>/*.jsonl`（D35 布局，见 §5.1） | 目录/文件缺失 → 字典为空，不算错误 |
| 5 字体探测 | `Fonts/FontProbe`（只探测不注入） | 异常 → warn 后继续 |
| 6 装 patch | `Patches/PatchRegistry.InstallAll`（16 个，逐个 try/catch） | 单个失败 → 记日志并继续下一个 |
| 7 汇总 | 一行 ready 日志 | — |

**红线 5**：语言不是 `zh*` 时一个 patch 都不装。
**红线 6**：任何失败只 `Debug.Log*`，不抛给 KSP。

## 2. 16 个 hook 目标（05 §2 的 12 个 + T5b2 补的 4 个）

> T5b 曾补过 `GUILayout.DoBox`，T5b2 已**撤**：它的方法体只有一句 `GUI.Box(...)`，而 `GUI.Box` 本来就在 hook 面里，多这一层只多一次必然 miss 的查表（05 §2 的 12 个里也不含它）。撤掉后 `PatchRegistry.ExpectedPatchCount` 必须与本节数字一致，不一致会打一条 `patch table has N entries` 警告。

### 2.1 文本层（2 个）

| 目标 | 文本参数位 | 前缀 | 说明 |
| --- | --- | --- | --- |
| `UnityEngine.UI.Text.set_text(string value)` | #0 `value` | `TextPatches.UnityTextPrefix` | uGUI 文本 |
| `TMPro.TMP_Text.set_text(string value)` | #0 `value` | `TextPatches.TmpTextPrefix` | 只能打**声明方法**；打 `TextMeshProUGUI` 会抛 `You can only patch implemented methods` |

### 2.2 IMGUI（13 个）

12 个目标的文本参数叫 `content`，共用一个前缀 `ImGuiPatches.ContentPrefix(ref GUIContent)`；`GUI.Window` 的文本参数叫 `title`，单独用 `ImGuiPatches.TitlePrefix(ref GUIContent)`（Harmony 按参数名绑定 ref 参数，名字写错不会抛，而是静默不生效）。

| 目标（精确签名） | 文本参数位 |
| --- | --- |
| `GUILayout.DoLabel(GUIContent content, GUIStyle style, GUILayoutOption[] options)` | #0 |
| `GUILayout.DoButton(GUIContent content, GUIStyle style, GUILayoutOption[] options)` | #0 |
| `GUILayout.DoRepeatButton(GUIContent content, GUIStyle style, GUILayoutOption[] options)` | #0 |
| `GUILayout.DoToggle(bool value, GUIContent content, GUIStyle style, GUILayoutOption[] options)` | **#1** |
| `GUILayout.DoWindow(int id, Rect screenRect, GUI.WindowFunction func, GUIContent content, GUIStyle style, GUILayoutOption[] options)` | **#3** |
| `GUI.Label(Rect position, GUIContent content, GUIStyle style)` | #1 |
| `GUI.Box(Rect position, GUIContent content, GUIStyle style)` | #1 |
| `GUI.Button(Rect position, GUIContent content, GUIStyle style)` | #1 |
| `GUI.Toggle(Rect position, bool value, GUIContent content, GUIStyle style)` | **#2** |
| `GUI.Toggle(Rect position, int id, bool value, GUIContent content, GUIStyle style)` | **#3** |
| `GUI.RepeatButton(Rect position, GUIContent content, GUIStyle style)` | #1（T5b2 补） |
| `GUI.DoWindow(int id, Rect clientRect, WindowFunction func, GUIContent title, GUIStyle style, GUISkin skin, bool forceRectOnLayout)` | **#3**（T5b2 补；私有漏斗，参数名 `title` → `TitlePrefix`） |
| `GUI.DoModalWindow(int id, Rect clientRect, WindowFunction func, GUIContent content, GUIStyle style, GUISkin skin)` | **#3**（T5b2 补；私有漏斗，参数名 `content` → `ContentPrefix`） |

`GUI.RepeatButton` 是 `GUILayout.DoRepeatButton` 的下游（所以 GUILayout 那条链上会两层都命中，见 §7.2），但 mod `直接调 GUI.RepeatButton` 的路径只有它。

**窗口标题为什么打私有漏斗而不是公开重载**：`GUI.Window(int, Rect, WindowFunction, **string**, GUIStyle)` 的 IL 是
`return DoWindow(id, clientRect, func, GUIContent.Temp(text), skin.window, skin, forceRectOnLayout: true);` —— 它**绕过**公开的
`Window(GUIContent title, GUIStyle)` 直连 `DoWindow`；`ModalWindow(string)` 同理直连 `DoModalWindow`。只打公开 `GUIContent` 重载会漏掉
`GUI.Window(id, rect, func, "标题")` 这种 KSP mod 里最常见的写法，所以改打两个漏斗（数量不变，仍是 2 个钩，覆盖反而更全）。

### 2.3 KSP 屏幕消息（1 个）

| 目标（精确签名） | 文本参数位 | 前缀 |
| --- | --- | --- |
| `ScreenMessages.PostMessage(ScreenMessage current, string msg, ScreenMessagesText textPrefab, float duration, ScreenMessageStyle style, Color color)` | #1 `msg` | `ScreenMessagesPatches.MessagePrefix(ref string msg)` |

10 个公开 `PostScreenMessage(...)` 重载全部转发到两个私有重载，而 5 参版（`…ScreenMessageStyle style`）的函数体就是 6 参版（补 `defaultColor`）——**只打 6 参重载即覆盖 100% 屏幕消息**，5 参版再打一遍是纯冗余（多一次必然 miss 的查表）。这是本项目**唯一**允许改 `string` 参数的目标（其余一律只换 `GUIContent` 对象）。

各目标的类型都用 `AccessTools.TypeByName` + `AccessTools.Method(type, name, signature)` 精确定位（按签名匹配，不靠名字猜重载）。

## 3. 前缀里只允许做一件事

`content` 命中 → **构造新的 GUIContent** 写回 ref 参数：

```csharp
// 正确
GUIContent translated = Translator.TranslateContent(content);  // 内部 new GUIContent(...)
if (translated != null) { content = translated; }

// 绝对禁止（GUIContent.Temp 返回共享实例，就地改会污染所有同文本控件）
content.text = ...
```

前缀里**不允许**：改游戏静态状态、调玩法 API、装 / 卸 patch、抛异常、transpiler。

唯一的例外形态是 `ScreenMessagesPatches.MessagePrefix(ref string msg)`：目标方法签名里根本没有 `GUIContent`，所以它改的是 `string` 实参。它同样只改**传进来的值**，不碰 `ScreenMessage` 实例、不碰 `ScreenMessages` 的静态状态。**除这个目标外，任何 prefix 都不许改 `string` 参数。**
命中路径缓存 GUIContent 实例（决策 D25），未命中零分配；`Translator.HasAnyTranslation` 是全局短路（05 §6.2）。

## 4. 首次进游戏看哪几行日志（KSP.log，grep `[UI18n]`）

正常（中文 + 有语言包）：

```
[UI18n] v0.1.0 starting (harmony id com.universali18n.runtime)
[UI18n] language=zh-cn, enabled=True, forceOverride=False
[UI18n] pack loaded: zh-cn/pool.jsonl, entries=+12792
[UI18n] dictionary: entries=12792, parseErrors=0
[UI18n] font probe: TMP faces=… (with CJK …), legacy Font faces=… (with CJK …), sample="中推。"
[UI18n] patch ok: UnityEngine.UI.Text.set_text(string value)
…
[UI18n] installed 16/16 patches
[UI18n] ready: patches=16/16, dict entries=12792, language=zh-cn, version=0.1.0
```

非中文（**预期行为**：0 个 patch）：

```
[UI18n] language=en-us, enabled=True, forceOverride=False
[UI18n] language=en-us, patches skipped (only zh* is translated; set ForceOverride=True to override)
```

没有语言包（`Packs` 目录都不存在）：

```
[UI18n] packs dir not found: …\GameData/UI18n/Packs, dictionary is empty
[UI18n] dictionary is empty, patches skipped (install a pack under GameData/UI18n/Packs/<lang>/)
```

`Packs` 目录存在、但没有当前语言的子目录（拼错 / 只装了别的语言）：

```
[UI18n] no pack dir for language=zh-cn (looked for Packs/zh-cn or Packs/zh under …\GameData/UI18n\Packs), dictionary is empty; available: en-us, ja
[UI18n] dictionary is empty, patches skipped (install a pack under GameData/UI18n/Packs/<lang>/)
```

有 patch 装不上（**每次出现都要查**）：

```
[UI18n] patch failed: GUILayout.DoWindow(int, Rect, GUI.WindowFunction, GUIContent #3, GUIStyle, GUILayoutOption[]) → method not found on UnityEngine.GUILayout with the expected signature (KSP/Unity version mismatch?)
[UI18n] patch failed: TMPro.TMP_Text.set_text(string value) → You can only patch implemented methods
[UI18n] installed 14/16 patches (2 failed, see [UI18n] patch failed lines above)
```

前缀运行期异常（每局每种只记一次，不影响游戏）：

```
[UI18n] prefix failed: IMGUI.GUIContent → NullReferenceException: … （同类错误每局只记一次）
```

## 5. 构建与部署

```powershell
# 只编译（产物在 build\bin\Release\net472\）
dotnet build .\src\UI18n.Runtime\UI18n.Runtime.csproj -c Release

# staging 到 build\GameData\UI18n\（默认不动 KSP）
pwsh -File .\src\UI18n.Runtime\deploy.ps1

# 显式安装进 KSP（已存在的 Config.cfg 会保留）
pwsh -File .\src\UI18n.Runtime\deploy.ps1 -Install

# 安装并带上内置冒烟字典（联调用；正式发布不带）
pwsh -File .\src\UI18n.Runtime\deploy.ps1 -Install -Smoke

# 换语言目录（默认 zh-cn）
pwsh -File .\src\UI18n.Runtime\deploy.ps1 -Install -Lang en-us
```

### 5.1 安装目录布局（D34 / D35，06 §6.4）

```text
GameData/UI18n/
├─ Config.cfg                 ← 玩家配置（UI18N 节点；已存在则安装时保留）
├─ UI18n.cfg                  ← 插件元数据（Name/Version/PacksDir）
├─ Plugins/
│  ├─ UI18n.Runtime.dll
│  └─ UI18n.Core.dll
└─ Packs/
   └─ <lang>/                 ← 语言目录，按 `Localizer.CurrentLanguage` 小写匹配（zh-cn → zh-cn，回落 zh）
      ├─ <modId>.jsonl        ← 运行时字典，唯一格式（D35）
      └─ _smoke.jsonl         ← 只有 -Smoke 才有，联调/演示用
```

**`packs` 根目录下的 `pool.jsonl` / `glossary.jsonl` / `inventory.json` 是流水线源产物，永远不装进运行时** ——
它们不是字典格式，装进去只会让每一行都 ParseError。`deploy.ps1` 只拷 `packs\<lang>\*.jsonl`。

字典文件里每一行是一条 JSON（D35）：`{"h":"16 位小写 hex 的 Fnv1a64(utf8(归一化原文))","n":归一化原文,"s":原文,"t":译文,"mod":归属 mod,"ctx":可选上下文}`。
`h` 与 `s` 都要对得上才算命中（`s` 不匹配按 miss 处理，用来兜住 FNV 碰撞）；译文 == 源文记 `keep`，按 miss 计但单独计数。

机器相关路径可用 `-p:KspManagedDir=…` / `-p:HarmonyDir=…` 覆盖（见 csproj）。
游戏侧程序集一律 `<Private>false</Private>`（不复制进产物，KSP 自己提供）。

## 6. 已知边界（不在本程序集处理）

- 字体：**不注入、不替换**。没装 CJK 字库时中文显示为 □，`font probe` 日志会明确提示。
- 数据层文本（A 类 cfg 字段 9,844 条、B 类 Localization key 2,948 条）：走语言包 + 数据字典，**不装 hook**（决策 D23）。
- 长文本（>200 字符）：按 D26 用「长度 + 首尾 8 字符」判据，不在本骨架范围内（后续优化项）。

## 7. 覆盖矩阵与有意放弃的目标

判断口诀：**某句英文没被翻译 → 先在下表定位它属于哪一行，再决定是「我们漏打」还是「本来就放弃」。**

| 目标签名 | 已 patch | 文本参数位 | 说明 / 未打的原因 |
| --- | --- | --- | --- |
| `UnityEngine.UI.Text.set_text(string value)` | ✅ | #0 `value` | uGUI |
| `TMPro.TMP_Text.set_text(string value)` | ✅ | #0 `value` | 只能打**声明方法**；打 `TextMeshProUGUI` 会抛 `You can only patch implemented methods` |
| `GUILayout.DoLabel(GUIContent content, GUIStyle, GUILayoutOption[])` | ✅ | #0 | 私有漏斗；`GUILayout.Label` 的 6 个公开重载（含 `Label(string)`）全走它 |
| `GUILayout.DoButton(GUIContent content, GUIStyle, GUILayoutOption[])` | ✅ | #0 | 私有漏斗；`GUILayout.Button` 的 6 个重载全走它 |
| `GUILayout.DoRepeatButton(GUIContent content, GUIStyle, GUILayoutOption[])` | ✅ | #0 | 私有漏斗；`GUILayout.RepeatButton` 的 6 个重载全走它。它再调 `GUI.RepeatButton`（**也已打** → 两层重叠，见 §7.2） |
| `GUILayout.DoBox(GUIContent content, GUIStyle, GUILayoutOption[])` | ❌ **T5b2 已撤** | #0 | 反编译实测其方法体只有一句 `GUI.Box(GUILayoutUtility.GetRect(content, style, options), content, style)` —— 它只是转发层，而 `GUI.Box` 本来就在 hook 面里；保留只多一次必然 miss 的查表。`GUILayout.Box` 的 6 个重载仍由 `GUI.Box` 覆盖 |
| `GUILayout.DoToggle(bool value, GUIContent content, GUIStyle, GUILayoutOption[])` | ✅ | **#1** | 私有漏斗 |
| `GUILayout.DoWindow(int id, Rect screenRect, GUI.WindowFunction func, GUIContent content, GUIStyle, GUILayoutOption[])` | ✅ | **#3** | 窗口标题栏。它再调 `GUI.Window` → `GUI.DoWindow`（**也已打** → 两层重叠，见 §7.2） |
| `GUI.Label(Rect position, GUIContent content, GUIStyle style)` | ✅ | #1 | 公开入口 |
| `GUI.Box(Rect position, GUIContent content, GUIStyle style)` | ✅ | #1 | 公开入口 |
| `GUI.Button(Rect position, GUIContent content, GUIStyle style)` | ✅ | #1 | 公开入口 |
| `GUI.Toggle(Rect position, bool value, GUIContent content, GUIStyle style)` | ✅ | **#2** | 公开入口（4 参重载） |
| `GUI.Toggle(Rect position, int id, bool value, GUIContent content, GUIStyle style)` | ✅ | **#3** | 公开入口（5 参重载，也打了） |
| `GUILayout.Label(string text, …)` 等 6 个公开重载 | ❌ | — | 全部委派给 `DoLabel(GUIContent.Temp(text))`，已覆盖；再打等于同一句查两次表 |
| `GUILayout.TextField` / `TextArea`（各 4 个重载） | ❌ **有意放弃** | — | 输入框内容是可编辑数据：翻译会污染玩家输入/存档，并让玩家没法编辑。它们只在渲染前 `new GUIContent(text)`，我们不碰 |
| `GUI.RepeatButton(Rect position, GUIContent content, GUIStyle style)` | ✅（T5b2 补） | #1 | 公开入口；`DoRepeatButton` 的下游，mod 直接调它也翻 |
| `GUI.Window(int id, Rect clientRect, WindowFunction func, GUIContent **title**, GUIStyle style)` | ➖ 由漏斗覆盖 | **#3** | 公开入口，没单独打：它的函数体就是 `return DoWindow(…, title, …)`，被下面的 `DoWindow` 钩住 |
| `GUI.ModalWindow(int id, Rect clientRect, WindowFunction func, GUIContent content, GUIStyle style)` | ➖ 由漏斗覆盖 | **#3** | 公开入口，没单独打：它的函数体是 `return DoModalWindow(…, content, …)` |
| `GUI.Window(int, … **string text**, GUIStyle)` / `GUI.ModalWindow(int, … **string text**, GUIStyle)`（string 重载） | ✅ **已用私有漏斗闭合**（T5b2） | **#3** | IL 证据（`gui.cs:1874`）：`public static Rect Window(int id, Rect clientRect, WindowFunction func, string text, GUIStyle style) { return DoWindow(id, clientRect, func, GUIContent.Temp(text), skin.window, skin, forceRectOnLayout: true); }` —— string 重载**绕过**公开的 `Window(GUIContent title, GUIStyle)` 直连 `DoWindow`（`ModalWindow(string)` 同理直连 `DoModalWindow`），所以只打公开 GUIContent 重载会漏掉 `GUI.Window(id, rect, func, "标题")`。改打两个私有漏斗后，string / GUIContent 两种重载都在面内 |
| `GUI.DoWindow(int, Rect, WindowFunction, GUIContent **title**, GUIStyle, GUISkin, bool)` | ✅（T5b2 补） | **#3** | 私有漏斗，参数名 `title` → `TitlePrefix`。它同时收 `Window(GUIContent)` 与 `Window(string)` 两路 |
| `GUI.DoModalWindow(int, Rect, WindowFunction, GUIContent **content**, GUIStyle, GUISkin)` | ✅（T5b2 补） | **#3** | 私有漏斗，参数名 `content`（**不是** `title`，已用 `System.Reflection.Metadata` 实测）→ `ContentPrefix`。经 `Internal_DoModalWindow`，与 `DoWindow` 是两条并列漏斗 |
| `GUI.DoLabel` / `GUI.DoRepeatButton` / `GUI.DoToggle` / `GUI.DoBox`（GUI 内部私有漏斗） | ❌ **有意放弃** | — | 公开入口（`GUI.Label/Button/Toggle/Box`）已经打了，且它们正是这些内部漏斗的调用者；再打会让**一次调用命中两次前缀**（多一次查表 + 语义变复杂），T5b 已按此结论排除 |
| `GUI.Internal_DoWindow(int, int, Rect, WindowFunction, GUIContent **title**, GUIStyle, object skin, bool)` / `GUI.Internal_DoModalWindow(…, GUIContent **content**, GUIStyle, object skin)` | ❌ **有意放弃** | — | 再往下一层已无新覆盖（它们的唯一调用方 `DoWindow` / `DoModalWindow` 已经打了），且 `skin` 参数是 `object`（跨版本更脆） |
| `ScreenMessages.PostScreenMessage(…)`（10 个公开重载） | ❌ **有意放弃** | — | 9 个收 `string message` + 1 个收 `ScreenMessage`。反编译确认它们**全部转发**到 2 个私有 `PostMessage`，而 5 参版又转发 6 参版 ⇒ 已改打 6 参版 1 个钩，10 个公开重载无需逐个打 |
| `ScreenMessages.PostMessage(…)` 6 参重载 | ✅（T5b2 补） | #1 `msg` | 唯一允许改 `string` 的目标；漏斗底，覆盖全部屏幕提示 |

### 7.1 调用链（谁调谁）

```text
GUILayout.Box(string)            → GUI.Box(✅)                       （DoBox 已撤，转发层无新覆盖）
GUILayout.Label(string)          → GUILayout.DoLabel(✅)             → GUI.Label(✅)
GUILayout.Button(string)         → GUILayout.DoButton(✅)            → GUI.Button(✅)
GUILayout.RepeatButton(string)   → GUILayout.DoRepeatButton(✅)      → GUI.RepeatButton(✅)
GUILayout.Toggle(…)              → GUILayout.DoToggle(✅)            → GUI.Toggle(✅)
GUILayout.Window(…GUIContent…)   → GUILayout.DoWindow(✅)            → GUI.Window(GUIContent) → GUI.DoWindow(✅ title)
GUILayout.Window(…string…)       → GUILayout.DoWindow(✅)            → GUI.Window(string)     → GUI.DoWindow(✅ title)
GUI.Window(…GUIContent…)         → GUI.Window(公开，未单独打)         → GUI.DoWindow(✅ title)
GUI.Window(…string…)             → GUI.DoWindow(✅ title)            ← 绕过公开 GUIContent 重载，靠漏斗闭合
GUI.ModalWindow(…GUIContent/string…) → GUI.ModalWindow(公开，未单独打) → GUI.DoModalWindow(✅ content)   （与 DoWindow 并列的独立漏斗）
GUI.RepeatButton(…GUIContent…)   → GUI.RepeatButton(✅)              （公开入口，无更深的 GUIContent 层）
ScreenMessages.PostScreenMessage(…) ×10 → PostMessage 5 参 → PostMessage 6 参(✅ msg)
```

### 7.2 重叠链为什么无害

`GUILayout.Label/Button/Toggle`、`GUILayout.RepeatButton`、`GUILayout.Window` 这几条链上前后两层都在 hook 面里。第二层看到的是**已经替换过的中文 `GUIContent`**，再查一次表必然 miss（25 ns 级），不会二次翻译 —— 除非某条译文恰好等于另一条的英文源文本（概率极低，且当前语料是 12,792 条中英一一对应）。保留两层的理由：mod 直接调 `GUI.*` 与直接调 `GUILayout.*` 的调用点都要覆盖，而这两条路径互不包含。

`GUI.DoWindow` / `GUI.DoModalWindow` 这两个漏斗与公开 `GUI.Window(…GUIContent…)` 的重叠是**刻意**的：`GUI.Window(…string…)` 绕过公开 GUIContent 重载直连漏斗，只有打漏斗才能覆盖 string 标题 —— 一次 window 绘制多一次必然 miss 的查表（~25 ns，一帧几十个窗口量级可忽略），换来 string / GUIContent 两种重载全覆盖。

### 7.3 「这句英文没翻」的排查顺序

0. **先搜日志里有没有 `patch failed: … → prefix parameter name mismatch: …`** —— 这是「patch 表写对了但 Harmony 没注入」的唯一显式信号。Harmony 按参数名绑 `ref`：名字对不上**不抛异常**，只静默失效，所以 `PatchRegistry.Install` 在 `harmony.Patch` 之前自己核对前缀参数名与目标参数名，对不上就记这条并计入失败数。出现它 = 前缀参数名（`content` / `title` / `msg`）或目标签名被改过。
1. 是输入框（`TextField`/`TextArea`）→ **本来就不翻**（§7 表中「有意放弃」）。
2. 是屏幕提示（`ScreenMessages`）→ 在 hook 面内（6 参重载），翻不出来就按第 4 步查。
3. 是窗口标题（`GUI.Window` / `GUI.ModalWindow`，含 **string 重载**）→ 在 hook 面内（走 `GUI.DoWindow` / `GUI.DoModalWindow` 漏斗），翻不出来同样按第 4 步查。
4. 是 `GUILayout` 的 label/button/box/toggle/repeatbutton/window 或 uGUI/TMP 文本 → 应该在 hook 面内，查三件事：日志 `patch ok` 是否 16/16、`dictionary: entries` 是否 >0、字典里这条的 `s` 是否与运行时原文逐字相等（`s` 不匹配同样算 miss）。

## 8. 设计约束（为什么这么写，别随手改）

- **配置只走 `ConfigNode.Load(GameData/UI18n/Config.cfg)`，不碰 `HighLogic.CurrentGame` / `GameParameters`。** 原因有两条：① `[KSPAddon(Startup.Instantly)]` 阶段 `CurrentGame` 还不存在，读了必 NRE；② 存档参数属于玩法状态，红线 3 禁止 patch 侧读写玩法状态。任务书里写作「GlobalParameters/ConfigNode」的那半句即指 ConfigNode 这一路。
- **Harmony 按参数名绑定 `ref` 实参。** 前缀方法参数名必须与目标参数名逐字一致 —— 这就是为什么 12 个 IMGUI 目标能共用一个 `ContentPrefix(ref GUIContent content)`，而 `GUI.Window` 的 `title`、`ScreenMessages.PostMessage` 的 `msg` 必须另起前缀。名字对不上时 Harmony **不抛异常**、而是注入静默失效，所以 `PatchRegistry.Install` 在 `harmony.Patch` 之前先自己核对参数名，对不上就记一条 `patch failed: … → prefix parameter name mismatch: …` 并计入失败数。
- **只允许 `ScreenMessagesPatches.MessagePrefix` 改 `string` 参数**（目标签名里没有 `GUIContent`）；其余前缀一律只换 `GUIContent` 对象，绝不原地改 `.text`。
- **`private static` 目标不靠「名字 + 类型」猜重载**，一律 `AccessTools.Method(type, name, exactSignature)`；类型缺失/签名不符时只记一条 `patch failed` 并继续装下一个。
- **没有 transpiler，也不把游戏方法体包进 try/catch。** `try/catch` 只包我们自己 prefix 里的逻辑，每类错误每局只记一条（`prefix failed`）。
- **`GUIContent` 只 `new` 新对象写回 `ref`**；`GUIContent.Temp` 返回共享实例，就地写 `content.text` 会污染所有同文本控件（红线 4）。
- **命中路径缓存 `GUIContent` 实例**（决策 D25），未命中零分配；`Translator.HasAnyTranslation` 是全局短路（05 §6.2）。
- **语言不是 `zh*` 时一个 patch 都不装**（红线 5），日志同时给出跳过原因与 `ForceOverride=True` 的开关提示。
- **字典只从 `Packs/<lang>/` 读，不读 `Packs/` 根下的平铺文件**（D35 / 06 §6.4）。语言目录名按 `Localizer.CurrentLanguage` 小写 + 下划线转连字符后匹配，先精确（`zh-cn`）后主语言（`zh`）。这样 `pool.jsonl`、`glossary.jsonl` 这类流水线源产物即使被误拷进 `Packs/` 也不会污染运行期。
- **`deploy.ps1` 只写两处**：仓库内 `build\`（staging）与 `<KspRoot>\GameData\UI18n\`（`-Install` 时）。KSP 目录里其它 mod 一律不碰（红线 7）。

