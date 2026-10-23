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
[UI18n] v1.0.0+9238f1acc43f8249be734ceb2e2dccf2d6920ad0 starting (harmony id com.universali18n.runtime)
[UI18n] language=zh-cn, enabled=True, forceOverride=False
[UI18n] font mode=OsFont, tmp mode=Auto, os font="Microsoft YaHei"
[UI18n] packs: lang=zh-cn, dir=zh-cn, files=42
[UI18n] pack loaded: zh-cn/KSP.jsonl, entries=+3120, 41 ms
[UI18n] pack loaded: zh-cn/RP-1.jsonl, entries=+5407, 63 ms
…
[UI18n] dictionary load: files=42, entries=10027, parseErrors=0, 137 ms
[UI18n] patch ok: UnityEngine.UI.Text.set_text(string value)
…
[UI18n] installed 16/16 patches
[UI18n] ready: patches=16/16, dict entries=10027, language=zh-cn, version=1.0.0+9238f1acc43f8249be734ceb2e2dccf2d6920ad0
```

> `version=` 后面是**程序集自带的完整版本串**（`AssemblyInformationalVersion`），格式 `<发布版本>+<构建时 HEAD 的 commit>`，
> 由 `UI18nAddon.Version` 从程序集读出来，不再硬编码 —— 玩家贴一行日志就能定位到确切 commit（见 10.8）。
> 本机联调从 `build\bin` 直接构建时，`+` 后就是当时 HEAD；正式发布包由 `build-release.ps1 -Version 1.0.0` 构建。

非中文（**预期行为**：0 个 patch）：

```
[UI18n] language=en-us, enabled=True, forceOverride=False
[UI18n] language=en-us, patches skipped (only zh* is translated; set ForceOverride=True to override)
```

没有语言包（`Packs` 目录都不存在）：

```
[UI18n] packs: status=PacksRootMissing language=zh-cn normalized=zh-cn candidates=[zh-cn,zh] dir=(none) files=0 flat=0 available=[], dictionary is empty
[UI18n] dictionary is empty, patches skipped (install a pack under GameData/UI18n/Packs/<lang>/)
```

`Packs` 目录存在、但没有当前语言的子目录（拼错 / 只装了别的语言）：

```
[UI18n] packs: status=LanguageDirMissing language=zh-cn normalized=zh-cn candidates=[zh-cn,zh] dir=(none) files=0 flat=0 available=[en-us,ja], dictionary is empty
[UI18n] dictionary is empty, patches skipped (install a pack under GameData/UI18n/Packs/<lang>/)
```

`Packs` 根下被塞了平铺 `*.jsonl`（流水线产物误拷 / 直接从仓库 `packs\` 根跑）：

```
[UI18n] packs: 2 flat *.jsonl file(s) directly under C:\…\packs are ignored (expected Packs/<lang>/<modId>.jsonl); packs: status=Found language=zh-cn normalized=zh-cn candidates=[zh-cn,zh] dir=C:\…\packs\zh-cn files=41 flat=2 available=[zh-cn]
```

这条是**告警不是故障**：平铺文件永不被读取，字典照样从 `Packs/<lang>/` 加载。

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

# 显式安装进 KSP（已存在的 Config.cfg 会保留，缺失的新键按默认值补进去）
pwsh -File .\src\UI18n.Runtime\deploy.ps1 -Install

# 用模板整体覆盖已存在的 Config.cfg（丢弃玩家改过的值）
pwsh -File .\src\UI18n.Runtime\deploy.ps1 -Install -ForceConfig

# 安装并带上内置冒烟字典（联调用；正式发布不带）
pwsh -File .\src\UI18n.Runtime\deploy.ps1 -Install -Smoke

# 换语言目录（默认 zh-cn）
pwsh -File .\src\UI18n.Runtime\deploy.ps1 -Install -Lang en-us

# 取证轮：探针 UI18n.Probe.dll 在位时 deploy 默认会 throw（防误发）；
# 只有显式 -AllowProbe 才放行，并打一行醒目警告（取证轮结束后必须摘掉探针再正式 deploy）
pwsh -File .\src\UI18n.Runtime\deploy.ps1 -Install -AllowProbe

# 打发布包（dist\UI18n-<version>.zip + manifest.json + netkan，见 §10）
pwsh -File .\src\UI18n.Runtime\build-release.ps1 -Version 1.0.0
```

### 5.1 安装目录布局（D34 / D35，06 §6.4）

```text
GameData/UI18n/
├─ Config.cfg                 ← 玩家配置（UI18N 节点；已存在则安装时保留，缺失的新键自动补默认值）
├─ UI18n.cfg                  ← 插件元数据（Name/Version/PacksDir）
├─ UI18n.version              ← KSP-AVC 版本文件（随包发布，deploy 与 build-release 都带）
├─ Plugins/
│  ├─ UI18n.Runtime.dll
│  └─ UI18n.Core.dll
├─ Fonts/                     ← FontMode=Bundled 时玩家放 AssetBundle 的位置（默认只有 README.txt）
└─ Packs/
   └─ <lang>/                 ← 语言目录，按 `Localizer.CurrentLanguage` 小写匹配（zh-cn → zh-cn，回落 zh）
      ├─ <modId>.jsonl        ← 运行时字典，唯一格式（D35）
      └─ _smoke.jsonl         ← 只有 -Smoke 才有，联调/演示用
```

**`packs` 根目录下的 `pool.jsonl` / `glossary.jsonl` / `inventory.json` 是流水线源产物，永远不装进运行时** ——
它们不是字典格式，装进去只会让每一行都 ParseError。`deploy.ps1` 只拷 `packs\<lang>\*.jsonl`。
万一它们真的出现在 `Packs` 根下，启动日志会报一条 `packs: N flat *.jsonl file(s) directly under … are ignored` 的 Warn，
运行期**仍然不读**（目录发现由 `PackLocator` 单点负责，见 §8）。

字典文件里每一行是一条 JSON（D35）：`{"h":"16 位小写 hex 的 Fnv1a64(utf8(归一化原文))","n":归一化原文,"s":原文,"t":译文,"mod":归属 mod,"ctx":可选上下文}`。
`h` 与 `s` 都要对得上才算命中（`s` 不匹配按 miss 处理，用来兜住 FNV 碰撞）；译文 == 源文记 `keep`，按 miss 计但单独计数。

**装完自检**：`deploy.ps1 -Install` 结束时逐文件比对 staging 与部署副本的 sha256，不一致直接 throw 并列出文件名
（规则在 `install-check.ps1` 的 `Assert-InstallTree`，可以喂合成目录单独测）。`Config.cfg` 例外 ——
它保留玩家设置、只补新键，内容本来就可能与模板不同；部署目录里多出来的文件（例如 `.old-*` 回滚备份）只告警、不动。

机器相关路径可用 `-p:KspManagedDir=…` / `-p:HarmonyDir=…` 覆盖（见 csproj）。
游戏侧程序集一律 `<Private>false</Private>`（不复制进产物，KSP 自己提供）。

## 6. 已知边界（不在本程序集处理）

- 字体：**uGUI 会在 `FontMode=OsFont/Bundled` 时注入系统 CJK 字体**（见 §9）；TMP 走「可判定降级」，
  找不到含汉字的 TMP 资产就保留原文而不是显示方框。`FontMode=Off` 时完全不碰字体（缺字即 □，玩家自担）。
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
- **字典目录发现只有一处实现：`UI18n.Core.Dict.PackLocator.Resolve`**（81 例单测覆盖）。`ConfigLoader.LoadPacks` 只按 `PackResolution.Files` 读文件，**不允许**自己拼 `Packs` 路径或判断 `Directory.Exists` —— 那正是 P1 那次「按契约放的包一条都读不到」事故的复发路径。规则：语言目录名按 `Localizer.CurrentLanguage` 小写 + 下划线转连字符，先精确（`zh-cn`）后主语言（`zh`），单向回退；`Packs/` 根下的平铺文件永不读（D35 / 06 §6.4），发现就报 Warn。
- **`deploy.ps1` 只写两处**：仓库内 `build\`（staging）与 `<KspRoot>\GameData\UI18n\`（`-Install` 时）。KSP 目录里其它 mod 一律不碰（红线 7）。
## 9. 字体注入与 TMP 三档（P2）

翻译出来的中文要能显示，必须解决字形：KSP 1.12 的 IMGUI 默认字体（`Arial`/`Calibri` 动态字面）与 TMP 图集都没有汉字，
直接换成中文只会得到一片 □。本节的三个开关就是「缺字时怎么办」的全部策略。

### 9.1 FontMode（uGUI / IMGUI 这条线）

| 值 | 行为 | 适用场景 |
| --- | --- | --- |
| `OsFont`（默认） | 从系统已装字体里挑一个含汉字的**动态字面**（`Font.CreateDynamicFontFromOSFont`），注入到 `UnityEngine.UI.Text.font`；拿不到就**保留原文** | 绝大多数玩家的正解 |
| `Bundled` | 先按 `BundledFontPath` 加载 AssetBundle 取 `Font`（或含汉字的 `TMP_FontAsset`）；路径不存在 / 加载失败 → 记 Warn 并**回落 `OsFont`** | 想做「自带字体」的整合包 |
| `Off` | 完全不碰字体，译文照常写入（缺字就是 □，玩家自担） | 玩家自己装了字库 / 只想看 IMGUI 以外的东西 |

字体可用性判据（**不赌**）：先 `Font.GetOSInstalledFontNames()` **精确名匹配**（不用注册表家族名，避免别名/本地化名对不上），
再 `CreateDynamicFontFromOSFont` 建出来，最后 `HasCharacter('中') && HasCharacter('推')` **实测**；三步任一失败就换下一个候选。
候选顺序：`OsFontName` → `Microsoft YaHei` → `Microsoft YaHei UI` → `SimHei` → `Noto Sans SC` → `DengXian` → `SimSun` → `Microsoft JhengHei`。
每个 `fontSize` 单独缓存一个 `Font` 实例（不同字号不共用）。全部失败时只记一条 Warn：

```
[UI18n] font: no usable CJK OS font (tried: …) → uGUI translations will be kept in English (set FontMode=Off to translate anyway)
```

### 9.2 TmpMode（TMP 这条线）

KSP 1.12 用的是 **TMP 1.2 位图图集版**：`TMP_FontAsset` 只有 `HasCharacter` / `AddGlyphInfo` / `ReadFontDefinition` 这类方法，
没有 `TMP_FontAssetUtilities`、没有 `atlasPopulationMode`、没有 `TryAddCharacters`，`FontEngine.LoadFontFace` 走的是 TextCore（TMP 1.2 不用它）
⇒ **没有运行时造字入口**。所以这里的策略是「可判定降级」，不是「尽力而为」：

| 值 | 行为 |
| --- | --- |
| `Auto`（默认） | 先用 `Resources.FindObjectsOfTypeAll<TMP_FontAsset>()` 找**已加载的含汉字 TMP 资产**：找到 → 追加进 `fallbackFontAssets` 并复核 `HasCharacter('中', true)`；找不到 → **保留原文**（不显示 □），并记一条 Warn 说明原因 |
| `KeepSource` | 一律不碰 TMP，TMP 文本永远保留原文（连资产扫描都跳过） |
| `Force` | 照常翻译，接受 □（调试 / 玩家自备 TMP 资产时用） |

扫描时机：**不在 `Startup.Instantly` 做**（那一阶段全场景 `FindObjectsOfTypeAll` 是启动期卡顿/被 OS 判无响应的风险点），
而是第一次真的遇到 TMP 文本、且 `HighLogic.LoadedScene == MAINMENU` 之后才惰性执行一次（另有 180 s 启动兜底）。
探针输出（`font probe: scene=…` / `tmp asset[i] …`）只在启动后打一次，运行期计数见 §9.4。

### 9.3 配置键表（`GameData/UI18n/Config.cfg`，全部有默认值）

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `Enabled` | `True` | 总开关。`False` = 一个 patch 都不装，行为与没装本 Mod 一致 |
| `ForceOverride` | `False` | 语言不是中文时也翻译（调试用，决策 D32 默认关） |
| `CheckForUpdates` | `True` | 语言包更新检查（数据层，不产生 hook） |
| `AutoDownload` | `False` | 自动下载语言包更新（默认只提示不下载） |
| `ReportOptIn` | `False` | 匿名上报未翻译文本（默认不上报任何东西） |
| `MaxEntries` | `60000` | 字典条目上限，防异常语言包吃光内存 |
| `FontMode` | `OsFont` | `Off` / `OsFont` / `Bundled`（§9.1）。非法值记 Warn 后用默认 |
| `OsFontName` | `Microsoft YaHei` | 首选系统字体名，精确匹配；不存在按内置候选回退 |
| `BundledFontPath` | 空 | `FontMode=Bundled` 的字体包路径，相对路径按 `GameData/UI18n` 解析 |
| `TmpMode` | `Auto` | `Auto` / `KeepSource` / `Force`（§9.2）。非法值记 Warn 后用默认 |
| `HitSampleLimit` | `64` | 退出时最多打印多少条「首次命中样本」（§9.4）。`0` = 关闭采样；**只影响日志条数**，`distinct hits` 计数永远精确 |

安装新版本时 `deploy.ps1 -Install` 会**保留玩家已改的值**，并把模板里有、玩家配置里缺的键按默认值补到 `UI18N` 节点末尾
（日志 `merged  : Config.cfg (kept player values, added N new key(s): …)`）；`-ForceConfig` 才整体覆盖。

### 9.4 运行期统计日志（每 20 s 一次，计数没变化就不打）

```
[UI18n] patch stats: UnityEngine.UI.Text.set_text=1234 hit=980 | TMPro.TMP_Text.set_text=8421 hit=3100 kept-src=5321 | GUI.Label=210 hit=205 | GUI.Box=0 hit=0 | … （16 个目标全在，含 0 命中）
[UI18n] distinct hits: 812
[UI18n] font stats: uGUI injected=87 already-ok=12 unavailable=0 off=0 | TMP injected=0 already-ok=0 kept-src=5321 unavailable=0 | mode=OsFont/Auto [os-font="Microsoft YaHei"]
```

- `hit` = 真的换成译文的次数；`kept-src` = 命中译文但因字体不可用而保留原文的次数（两者互斥）。
- **`hit` 不是「翻了多少句」**：它按调用计。同一句每帧重画就每帧 +1（IMGUI 每帧 Layout + Repaint 两趟），
  嵌套调用还会重复算（`GUILayout.DoLabel` → `GUI.Label`，同一句过两次前缀）。所以 `hit` 可以远大于条目数。
  **「翻了多少句」只看 `distinct hits`**（按原文去重，不受重画与嵌套影响）。
- `distinct hits` = 本局**不同原文**被翻译成功的条数（全量去重，不受 `HitSampleLimit` 影响）。它紧跟在 `patch stats` 之后打。
- 每个目标**第一次被调用**时打一行 `[UI18n] first call: <短名>` —— 即使进程后来被杀，也能证明该目标在本局是活的。
- 退出 / addon 销毁时打 `[UI18n] final dump (quit): calls=N, hits=N` + 完整 `patch stats` + `distinct hits` + `font stats`，
  **最后**再追加「首次命中样本」（默认最多 64 条，`HitSampleLimit` 控制）：

```
[UI18n] hit sample: distinct=812 (capped=false)
[UI18n] hit sample: [GUI.Label] Modular Launch Pads: General_Bases/... => 模块化发射台：基础/... (site)
[UI18n] hit sample: [TMPro.TMP_Text.set_text] ModuleManager: 81182 patches applied => ModuleManager：已应用 81182 个补丁 (global)
```

  表头固定是 `hit sample: distinct=<N> (capped=<bool>)`；每条样本一行 `hit sample: [<patch>] <原文> => <译文> (<层级>)`，
  原文 / 译文各截断到 80 字符（超出加 `...`），换行转义成 `\n`，所以一条样本永远只占一行。
  `capped=true` 表示去重后超过上限、后面的只计数不记样本（想全看就把 `HitSampleLimit` 调大，`0` = 关）。
  层级只会是 `site` / `global`：Runtime 这条路径没有位置信息，按 §4 的约定**不碰 ctx 级条目**。
- 计数是每个目标一个 `PatchCounter` 静态字段（热路径只有一次 `Calls++`），不做字典查询；
  样本只在「某个原文第一次翻译成功」时记一条（缓存未命中路径），热路径不增加分配。
- 一次真实读数（2026-10-23 round 2，新构建 43,520 B，只手动开了 Alt+F12 调试工具栏）：

  ```
  [UI18n] final dump (quit): calls=1571618, hits=745245
  [UI18n] distinct hits: 20
  [UI18n] hit sample: distinct=20 (capped=false)
  [UI18n] patch stats: … GUILayout.DoLabel=83864 hit=83864 | …
  ```

  1,571,618 次调用、745,245 次命中，但 `distinct hits` 只有 **20**：那 20 条全是 Alt+F12 调试工具栏的英文原文
  （`Debug Toolbar`→`调试工具栏`、`Console`→`控制台`、`Advanced & debug`→`高级与调试`、`Screenshot`→`截图`、
  `Vessel mass`→`飞行器质量`、`Flight Data Engineer Multiplier`→`飞行数据工程师倍率`、`Dump Wheel Debug Data`→`导出轮组调试数据`…），
  每帧重画。`GUILayout.DoLabel=83864 hit=83864` 这种 100% 命中率**不是计数器 bug**，是同一批 20 个串被反复画。
  20 条全部落在 `(site)` 层（Runtime 路径没有位置信息，见上）。同一局加载期（09:41:36–09:47:28）
  `distinct hits` 恒为 0，进主菜单才跳到 20 ⇒ 那是加载屏的覆盖缺口，不是插件没工作。

### 9.5 换上新构建后怎么验（3 步）+「主菜单轮通过 ≠ 全部通过」

1. 启动到主菜单，`grep "\[UI18n\]" KSP.log`：应看到 `font mode=OsFont, tmp mode=Auto, os font=…`、
   42 条 `pack loaded: zh-cn/…, entries=+N, N ms`、`dictionary load: files=42, entries=10077, parseErrors=0, N ms`、
   `installed 16/16 patches`、`ready: patches=16/16`。**任一条 `patch failed` / `prefix parameter name mismatch` 都算失败。**
   - `entries=10077` 是**离线预测值**（41 个正式包 10,043 行 + `_smoke.jsonl` 34 行；用 `_scratch/p1/predictload` 对部署目录跑真 `DictPack` 得到，
     耗时 146 ms）。实测差得远就是加载缺陷（先看有没有 `MaxEntries` 截断告警、有没有读错语言目录）。
2. 在主菜单停 60 s 以上：应出现 `patch stats: …`（16 个目标全在，含 `=0` 的），且 `font stats` 里
   `uGUI injected` 或 `already-ok` > 0（字体这条线通了）。
3. 正常退出游戏，日志末尾应有 `final dump (quit): calls=N, hits=N` + 完整两份 stats。
   **主菜单轮的通过标准**：`patches=16/16`、`parseErrors=0`、没有 `patch failed` / `prefix failed` / `font: … failed` 的 Error。

**主菜单轮只能证明三件事**：两个文本层目标活着（`UnityEngine.UI.Text.set_text` / `TMPro.TMP_Text.set_text` 的 `calls`>0）、
字体线通、字典加载正确。**它不能证明 IMGUI 那一半**（13 个目标），因为主菜单根本不是 IMGUI 界面。

主菜单停 60 s 的**预期量级**（用来一眼判断「正常」还是「patch 没生效」）：

| 目标 | 预期 calls | 预期 hit | 说明 |
|---|---|---|---|
| `TMPro.TMP_Text.set_text` | 10³–10⁵ | > 0（几十–几百） | 主菜单主体是 TMP；KSP 自带 zh-cn 字典已汉化的句子全 miss，只有 mod 英文残留命中 |
| `UnityEngine.UI.Text.set_text` | 10²–10⁴ | > 0（个位数–几十） | 版本号、提示、加载画面残留等 legacy 文本 |
| `GUI.Label` / `GUI.Box` / `GUI.Button` / `GUI.RepeatButton` / `GUI.Toggle` / `GUI.Toggle(int)` | **0** | 0 | 主菜单不是 IMGUI；要到 VAB/SPH、Debug Toolbar、mod 窗口才出现 |
| `GUILayout.DoLabel` / `DoButton` / `DoRepeatButton` / `DoToggle` / `DoWindow` | 0 | 0 | 同上 |
| `GUI.DoWindow(title)` / `GUI.DoModalWindow` | 0 | 0 | 同上 |
| `ScreenMessages.PostMessage` | **0（必然）** | 0 | 主菜单不产生屏幕提示（进场景 / 科研完成 / 成就才出现） |

判故障的口径：

- **patch 没装上**：文本层两个目标 `calls=0`，或日志里没有它们的 `first call: <短名>` 行。
- **字典没生效**：`calls`>0 但 `hit=0` 且 `kept-src=0`（查表全 miss，多半是语言目录没对上）。
- **字体没通**：`hit=0` 而 `kept-src`>0（命中译文但被字体门禁退回英文），且 `font stats` 里 `unavailable`>0。
- **IMGUI 是否活着**：只能靠 `first call: GUI.Label` 这类行 + 进场景后的 `calls`>0 证明，见 §9.7。

### 9.6 回滚（一行）

```powershell
cd D:\Kerbal Space Program_newmod\GameData\UI18n\Plugins; Move-Item -Force UI18n.Runtime.dll.old-9c712ab1 UI18n.Runtime.dll; Move-Item -Force UI18n.Core.dll.old-01f1c154 UI18n.Core.dll
```

旧的两个 DLL 在 `deploy` 时被改名成 `*.old-<sha256 前 8 位>` 留在 `Plugins\` 里（KSP 只加载 `*.dll`，改名文件不会被加载），
所以回滚不需要重新构建。

### 9.7 IMGUI 取证：`UI18n.Probe.dll`（只用于取证，不随包发布）

**为什么需要它**：主菜单轮证明不了 13 个 IMGUI 目标（见 §9.5 表）。KSP 的 Debug Toolbar 热键是
`Input.GetKeyDown(KeyCode.F12) && GameSettings.MODIFIER_KEY.GetKey()`（IL 证据：Assembly-CSharp 全局类
`DebugToolbar.Update()` 的 327/338 行；`GameSettings.MODIFIER_KEY = new KeyBinding(KeyCode.LeftAlt, KeyCode.RightAlt)`，
本机 `settings.cfg` 同值 `primary = LeftAlt / secondary = RightAlt`；`InputBinding.IsLocked()` 只看 `inputLockMask`，
该键为 0，永不上锁）。所以 Alt+F12 这个组合本身是对的 —— 用 `SendKeys` 合不出来是**注入侧**的问题：
Unity 旧 Input Manager 只认真实输入事件（`SendInput`/`keybd_event` 且窗口必须真前台），`SendKeys` 走合成/消息路径，
Alt 还可能被当成通用 `VK_MENU` 而不是 `KeyCode.LeftAlt`。**不要把「合成按键能开 Debug Toolbar」当成验证前提。**

**替代方案**：`_scratch/p1/imgui-probe/`（不在 sln 内、不随包发布）编出 `UI18n.Probe.dll`，
`[KSPAddon(KSPAddon.Startup.MainMenu, false)]`，在主菜单画两个 IMGUI 窗口并调用全部 IMGUI 目标：
`GUI.Window(id, rect, func, "Debug Toolbar")`、`GUILayout.Window(id, rect, func, "Settings")`、
`GUI.Label/Box/Button/RepeatButton/Toggle/Toggle(int)`、`GUILayout.Label/Box/Button/RepeatButton/Toggle`，
外加一条 `ScreenMessages.PostScreenMessage("Flight Data Engineer Multiplier", …)` 与两行硬编码中文
（`推力 中文测试 UI18n` / `Ascii 对照 0123456789`）。

**用法**（三步）：

```powershell
dotnet build _scratch\p1\imgui-probe\UI18n.Probe.csproj -c Release
pwsh -File _scratch\p1\probe.ps1 -Action Install     # 拷进 GameData\UI18n\Plugins\
# … 启动 KSP（zh-cn），主菜单停 60 s，截图 + 抓日志 …
pwsh -File _scratch\p1\probe.ps1 -Action Remove      # 必须移除，否则会随包发出去
```

**预期证据**：日志里出现 13 条 IMGUI 目标的 `first call: <短名>`；`patch stats` 里它们每帧增长、`hit` > 0
（探针用的字符串全在 `_smoke.jsonl` 里：`Debug Toolbar`/`Settings`/`Performance`/`Database`/`Console`/
`Reload Settings`/`Apply`/`Continue`/`Credits`/`All Input Locks Cleared`/`Vessel mass`/`Basic Options`/
`Damage Options`/`High Speed Limit`/`Dump Wheel Debug Data`/`Advanced & debug`/`Screenshot`/
`Flight Data Engineer Multiplier`）；`ScreenMessages.PostMessage` 的 `calls` 从 0 变 1。

**IMGUI 字体判据**（决定要不要加门禁）：探针首帧会打

```
[UI18n.Probe] imgui skin=<name>
[UI18n.Probe] imgui label.font = "<font>" dynamic=True hasCJK(中)=False hasCJK(推)=False
```

- 若两行中文在截图里是正常字形 ⇒ IMGUI 字体有汉字，**不需要**任何门禁。
- 若显示为 □ 且 `hasCJK=False` ⇒ 才考虑加 `ImGuiFontGate = Auto|Off`（Auto 档：命中前只读
  `GUI.skin.font.HasCharacter('中')`，false 就保留原文，日志 `imgui font gate: skin font lacks CJK → keep source (N times)`）。
  **不换字体、不碰 `GUI.skin`**（红线 3）。

**探针 auto-stop 会抛一次 GUILayout 组计数异常 —— 属预期，不是运行时缺陷**：

```
[ERR] [UI18n] [probe] window contents failed: System.ArgumentException: Getting control 6's position in a group with only 6 controls when doing repaint
```

探针 180 s 到点后**在帧中间**停止绘制（`enabled = false`），同一帧里 `GUILayout` 组已经开始布局、
却少了后续控件，于是 IMGUI 自己报这一句。它只发生在探针 DLL 内，运行时（`UI18n.Runtime.dll`）零影响：
两个程序集互不引用、探针不在 sln 里、取证后立刻从盘上移除。看到这一行不要当故障排查。

**安全边界**：探针只画自己的窗口、只读字体信息、不改任何游戏状态；不在 sln 里、不在 `deploy.ps1` 的拷贝清单里，
并且 `deploy.ps1` 在 `Plugins\` 里发现 `UI18n.Probe.dll` 时会直接报错停止（防误发）。

## 10. 发布打包（build-release.ps1）

### 10.1 入口与产物

```powershell
# 版本号必填（x.y.z），不给直接 throw；默认先 dotnet build 再出包
pwsh -File .\src\UI18n.Runtime\build-release.ps1 -Version 1.0.0

# 复用已有 build\bin（CI 里已经编过时）
pwsh -File .\src\UI18n.Runtime\build-release.ps1 -Version 1.0.0 -SkipBuild

# 脏工作树出包（默认会 throw；只在明确知道自己在干什么时用，日志会留 WARN）
pwsh -File .\src\UI18n.Runtime\build-release.ps1 -Version 1.0.0 -AllowDirty
```

| 产物 | 说明 |
| --- | --- |
| `dist/UI18n-<version>.zip` | 玩家下载的包，**直接解压进 KSP 根** |
| `dist/manifest.json` | 版本 / 构建时间 / 逐文件 bytes+sha256 / zip 自身 sha256 / 字典统计 |
| `dist/UI18n.netkan` | CKAN 元数据（从 `ckan/UI18n.netkan.template` 生成） |

`dist/` 在 `.gitignore` 里：包与 manifest 都是产物，不入库；入库的是脚本与模板。

### 10.2 zip 内布局（= 直接解压进 KSP 根）

```text
GameData/UI18n/
├─ UI18n.cfg
├─ Config.cfg
├─ UI18n.version              ← VERSION 被替换成 -Version 的值
├─ Plugins/UI18n.Core.dll
├─ Plugins/UI18n.Runtime.dll
├─ Fonts/README.txt
└─ Packs/zh-cn/*.jsonl        ← 只来自仓库 packs\zh-cn\
```

`Packs/` **只从仓库 `packs/<lang>/` 取**（那是运行时字典）。`packs/pool.jsonl` 与 `packs/glossary.jsonl` 是翻译流水线的
生产端输入，不是运行时格式，进包只会让每一行都 ParseError —— 断言会拦（见 10.3 第 6 条）。

### 10.3 硬断言（`release-checks.ps1`）

出包前逐条检查，**命中任意一条就 throw，并且一次把所有违规列出来**。两个入口，顺序固定：

- `Assert-ReleaseProvenance`（第 8、9 条，**先跑**）：来源不对就不必看内容了；
- `Assert-ReleaseTree`（第 1-7 条，**后跑**）：staging 树里不该有的东西。

| # | 规则 | 为什么 |
| --- | --- | --- |
| 8a | 已跟踪文件不许有改动/删除/暂存未提交（`git status --porcelain --untracked-files=no` 为空） | 脏树编出来的二进制不对应任何已提交源码 |
| 8b | 未跟踪文件不许落在 `src/` `tests/` `tools/` `packs/` 之下 | 这些是构建输入：会进构建或进包，未提交就等于不可复现 |
| 8c | 其余未跟踪文件（`reports/` `build/` `dist/` `_scratch/` …）**只告警不阻断** | 每轮验证都会产生 `reports/verify-*.log`；若把它当脏，重建永远报脏 |
| 9a | staging 里两个 DLL 内嵌标记的 `+` 前必须等于 `-Version` | 发布版本 == DLL 版本（构建时用 `-p:Version=` 写进程序集） |
| 9b | 内嵌标记的 `+` 后必须等于 `git rev-parse HEAD` | 二进制必须能指认自己的源码（见 10.8） |
| 1 | 不许有 `UI18n.Probe.dll` | 取证插件，只在本机联调用 |
| 2 | 不许有 `*_smoke.jsonl` | 冒烟字典只随 `deploy.ps1 -Smoke` 装 |
| 3 | 不许有 `*.pdb` | 调试符号 |
| 4 | 不许有 `*.old-<hex>` | deploy 留下的回滚备份 |
| 5 | 路径不许含 `build/` `_scratch/` `_audit/` `dist/` | 内部目录 |
| 6 | 不许有 `packs/pool.jsonl`、`packs/glossary.jsonl` | 生产端输入 |
| 7 | `Plugins/` 里只能有 `UI18n.Core.dll` + `UI18n.Runtime.dll` | 防止夹带 |

抽成独立文件是为了能在**不出包**的前提下用合成 stage 树逐条验证（见 `_scratch` 里的 relcheck 脚本）。

### 10.4 manifest.json

```json
{ "version": "1.0.0", "builtAt": "…", "kspVersion": "1.12.5", "language": "zh-cn",
  "zip":   { "name": "UI18n-1.0.0.zip", "bytes": 993009, "sha256": "…" },
  "files": [ { "path": "GameData/UI18n/…", "bytes": 0, "sha256": "…" } ],
  "packFiles": 41, "packRows": 9990, "packBytes": 3504392 }
```

`packRows` 按字典文件的非空行数统计，用来和运行时日志 `dictionary load: … entries=N` 对账（两者应相等）。

### 10.5 netkan 里必须人工确认的占位值

| 字段 | 当前值 | 状态 |
| --- | --- | --- |
| `license` | `MIT`（`-License` 可覆盖） | **占位，发布前必须确认** |
| `resources.homepage` / `repository` / `download` | `https://github.com/UniversalI18n/UniversalI18n`（`-RepoSlug` 可覆盖） | **占位，发布前必须确认** |
| `author` | `UI18n contributors`（`-Author` 可覆盖） | 占位 |
| `ksp_version` | `1.12` | 按 KSP 1.12.5 实测环境填 |
| `depends` | `Harmony2`、`ModuleManager` | 已按本机 CKAN 元数据（`identifier`）核对，不是猜的 |
| `release_status` | `testing` | 首发可改 `stable` |

### 10.6 一次真实出包的数字（1.0.0，2026-10-23）

| 项 | 值 |
| --- | --- |
| `dist/UI18n-1.0.0.zip` | 996,170 B |
| zip sha256 | `BBE56FF737E6BE788FDCA921DE88418263E70C37D2AF314E39414869E5619524` |
| zip 内条目 | 47（6 个元数据/插件/字体 + 41 个字典） |
| manifest `files` | 47 |
| `packFiles` / `packRows` / `packBytes` | 41 / 9,990 / 3,504,392 |

> 这一组数字来自 2026-10-23 的**脏树构建**（DLL 内嵌 `fc1c2b2`，见 10.8 的事故复盘），只作量级参考；
> 正式出包必须按 10.8 从干净树重建，届时以 `dist/manifest.json` 为准。

**zip 的字节数与 sha256 每次出包都会变**（zip 条目里存了文件时间戳，`UI18n.version` 每次重写），
所以对外公布的值一律以当次生成的 `dist/manifest.json` 的 `zip.sha256` 为准，不要抄 README 里的历史值。
`packFiles/packRows/packBytes` 与 `files` 数量是稳定的，可用作回归断言。

### 10.7 版本号只在一个地方写

`-Version` 同时决定：zip 文件名、`manifest.version`、zip 内 `UI18n.version` 的 `VERSION`、`UI18n.cfg` 的 `Version`、netkan 的 `version`/`download`/`sha256`，以及**两个 DLL 内嵌的发布版本**（`-p:Version=`）。

模板里的 `1.0.0`（`GameData/UI18n/UI18n.version` 与 `GameData/UI18n/UI18n.cfg`）只是给 `deploy.ps1`（本机联调）用的默认值：
出包时 `build-release.ps1` 会把 staging 里这两处都改写成 `-Version`（日志 `[release] staged  : UI18n.cfg (Version=1.0.0)` / `UI18n.version (VERSION=1.0.0)`），
所以**发布包里不会留模板默认值**；若发现包里版本号与 zip 名不一致，说明有人手工 stage 过而不是走 `build-release.ps1`。

运行时日志里的 `version=` 打的是程序集版本串（`<version>+<commit>`），来源同上（见 10.8）。

### 10.8 发布来源标记（DLL 内嵌 commit）与干净工作树

三句话说完：

1. **发布必须从干净工作树构建 —— 判据是三档，不是裸 `git status`。** 裸判据会把每轮验证产生的 `reports/verify-*.log` 当成脏，让重建永远报脏，所以 `Assert-ReleaseProvenance` 这样分档：
   - **阻断**：已跟踪文件有改动/删除/暂存未提交 → `dirty working tree: tracked changes: <前 5 条> (+N more)`；
   - **阻断**：未跟踪文件落在 `src/` `tests/` `tools/` `packs/` 之下 → 每条一个 `untracked build input: <path>`（这些是构建输入，未提交就不可复现）；
   - **只告警**：其余未跟踪文件 → `WARN: N untracked file(s) outside build inputs (ignored by this assertion): …`。

   确需脏树出包只能显式传 `-AllowDirty`，此时前两档降级为 `WARN: working tree is dirty (N tracked change(s), M untracked build input(s)), release built with -AllowDirty`。
2. **DLL 内嵌的 `<version>+<commit>` 是有意的来源标记，不是巧合。** .NET SDK 把 `SourceRevisionId`（= 构建时的 `git rev-parse HEAD`）追加进 `AssemblyInformationalVersion`，所以拿到任何一个 UI18n 的 DLL 都能反查它出自哪个 commit。脚本把 staging 里两个 DLL 的这个串**拆成两半分别判**：
   - `+` 前必须等于 `-Version`（文案「版本号 1.0.0 与 -Version 2.0.0 不一致」）；
   - `+` 后必须等于当前 HEAD（文案「DLL 内嵌 <x> / HEAD <y>」）。

   **`-Version` 必须真的传进编译**：构建命令行是 `dotnet build <sln> -c Release -p:Version=<version>`；不传的话 DLL 里永远是 SDK 默认版本号，9a 那条就永远过不了。
   运行时日志 `version=` 打的也是这个完整串（`UI18nAddon.Version` 优先读 `AssemblyInformationalVersionAttribute`，读不到回落 `AssemblyName.Version`），所以玩家贴的日志能直接对到 commit。
3. **因此「同一 commit 重建 == 发布字节」成立，跨 commit 一定不同** —— 那是来源标记在变，不是构建不确定性。`Directory.Build.props` 的 `Deterministic=true` 保证同一 commit、同一 SDK、同一输入编出逐字节相同的 DLL；而 `SourceRevisionId` 本身就是输入的一部分，commit 变了字节必然变。**不要为了「字节稳定」去关 `Deterministic` 或 `SourceRevisionId`**，那会把来源可追溯性一起关掉。

**为什么加这两条（事故复盘）**：`dist/UI18n-1.0.0.zip` 第一次是用**脏工作树**出的包，里面两个 DLL 内嵌 `1.0.0+fc1c2b2`，而 `HitSampleLimit`、README 那批改动当时还没提交 —— 二进制声称的 commit 并不包含它自己的源码，等于发布物无法溯源。随后提交 `9238f1a` 再构建，字节就变了（Core `3C33CB55…` → `BBD022FB…`、Runtime `D906F956…` → `4357C25E…`，大小不变）。所以流程固定为：

```text
改代码 → 提交（干净树）→ 从该 commit 构建 → deploy → 出包
```

**自查（不构建也能看任何一个 DLL 声称的 commit）**：

```powershell
$p = 'GameData\UI18n\Plugins\UI18n.Runtime.dll'
$t = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($p))
[regex]::Match($t, '[0-9]+\.[0-9]+\.[0-9]+\+[0-9a-f]{40}').Value   # 例：1.0.0+9238f1acc43f8249be734ceb2e2dccf2d6920ad0
git rev-parse HEAD                                                    # 两者必须相同
```

实测（2026-10-23，本机）：部署中的 `GameData\UI18n\Plugins\UI18n.Runtime.dll` 内嵌 `1.0.0+fc1c2b20…`，而当时 HEAD 已是 `9238f1a…` —— 正是这条断言要拦的形态；按上面的流程重建 + 重新 deploy 后两者一致。


