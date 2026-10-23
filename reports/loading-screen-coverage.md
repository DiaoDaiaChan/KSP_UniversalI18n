# 加载屏字符串覆盖调查报告（T24）

> 调查对象：KSP 1.12.5 加载屏（`LoadingScreen`）与 KSP / mod 硬编码字符串的采集覆盖缺口。
> 约束：本轮**只出报告**——未改 `packs/**`、未改 `tools/i18n_tools/` 任何采集器代码（冻结期，改采集器必须重跑 packindex）。
> 证据基准：`packs/pool.jsonl` 12,792 行（3,049,123 字节 / sha256 `FEF515EABDC017BBA2D9ADFE0E157E0B604F287FEC1DC53AB9905211A9DADD4C`）、`KSP_x64_Data\Managed\Assembly-CSharp.dll`（10,790,912 B）、`KSP_x64_Data\level0`（47,400 B）、`GameData\Squad\Localization\dictionary.cfg`。

## 0 结论摘要

1. 加载屏可见文本只有 4 类来源：① `Localizer.Format("#autoLOC_…")` 键（进度条标题）② `level0` 场景里 `LoadingScreenState.tips` 的键数组 ③ **DLL 里的 IL 字面量**（`"Caching Assembly info"`、KSPBurst / RP-1 的 `ProgressTitle()`）④ 资源路径（`UrlDir.UrlFile.url`，非文本）。**没有一条来自 cfg 的 title/description 字段。**
2. 池内/池外：Squad 的 95 条 tips 键、RP-1 的 80 条 tips 键、`Caching Assembly info` 在 `pool.jsonl` 里 **0 命中**；不是被 `already-cjk/dup/too-short/search-tags` 吃掉，而是**根本没进入候选**——loc 源只收「有 en-us 没 zh-cn 的键」，cfg 源只收 title/description/displayName/tags 字段值，两者都够不着加载屏。
3. 这套安装里 **Squad tips 的英文原文根本不存在**：全盘 20,478 文件 / 27,328,923,732 B 扫 58.34 s，`#autoLOC_165067` 只在 `GameData\Squad\Localization\dictionary.cfg` 命中，而该文件只有 zh-cn 节点。所以即使把 tips 键采进池，也**没有英文基准可译**。
4. harvest 的取源边界是 `GameData/**/*.cfg`（`tools/i18n_tools/harvest.py:133-134` 只收 `.cfg`），**任何 `.dll` 都不读**——`KSP_x64_Data\Managed\*.dll` 与 `GameData\**\*.dll` 一样在覆盖之外。Lead 给的 5 个调试串里，只有 `Debug Toolbar` 在 Assembly-CSharp，其余 4 个在 mod 插件 DLL（§3.3）。
5. 量级：Assembly-CSharp 的 `#US` 堆共 **18,456** 条 IL 字面量，按 harvest 同口径过滤后 18,096 条，其中「像句子」的 3,001 条，而**直接出现在 IMGUI 调用参数位置的只有 271 条 distinct**（45 条还是 `#autoLOC_*` 键）。→ 全量采集噪声 >80%，**不值得**；按 IMGUI 调用点 / 运行期观测取白名单（约 226 条英文）**值得**，约 0.5–1 人日，且**不要动 pool**（§5）。

## 1 加载屏上的字符串从哪来

### 1.1 四类来源总表

| # | 来源 | 载体 / 代码位置 | 例子 | 现有 harvest 能采到吗 |
|---|---|---|---|---|
| A | `Localizer.Format("#autoLOC_…")` 键 | `Assembly-CSharp.dll` 的 `GameDatabase` / `TextureInfo` / `ExpansionsLoader` | `#autoLOC_158078` → 中文「正在加载资源」 | 否（值不在 cfg，键在 DLL） |
| B | `level0` 场景的 tips 键数组 | `KSP_x64_Data\level0`（Unity 序列化） | `#autoLOC_7001100`…`7001194` | 否（harvest 只读 `.cfg`） |
| C | **IL 字面量**（硬编码英文） | `Assembly-CSharp.dll`、mod 插件 DLL | `"Caching Assembly info"`、`"KSPBurst: waiting for Burst compiler..."` | 否（harvest 不读 DLL） |
| D | 资源路径（非文本） | `UrlDir.UrlFile.url`（`Assembly-CSharp.full.cs:212724`） | `Squad/Parts/…` | 否（且不该采） |

### 1.2 进度条标题：`LoadingSystem.ProgressTitle()`

`LoadingScreen.LoadSystems()`（`%TEMP%\t24\LoadingScreen.decompiled.cs:778`）在 `while (!current.IsReady())` 里每帧调用一次：

```csharp
UpdateProgressBar(current.ProgressTitle(), loadStep / totalWeight + current.ProgressFraction() * loadDelta);
```

`UpdateProgressBar`（同文件 841–846 行）把 title 同时写进 `scrollBarText.text` 与 `scrollBarTextMasked.text`——**一帧 2 次 `TMP_Text.set_text`**，这正是加载期 `set_text` 计数疯涨（20 s 涨 3 万次）的来源。

`ProgressTitle()` 的 5 个实现（行号为 `%TEMP%\t24\Assembly-CSharp.full.cs`）：

| 类 | 行号 | 返回什么 | 实际渲染 |
|---|---|---|---|
| `LoadingSystem`（基类） | 223147 | `"None"` | 英文常量（子类未覆盖时） |
| `GameDatabase` 内嵌 `TextureInfo` | 208084 | `progressTitle` 字段 | `#autoLOC_158078/158082/158089/158628/158641` → 中文 |
| `FontLoader` | 221997 | `progressTitle` | `definitions[i].Name`（字体资源名，222063） |
| `PartLoader` | 223388 | `progressTitle` | 空串（223440 `progressTitle = ""`） |
| `ExpansionsLoader` 内嵌 `ExpansionInfo` | 1295849 | `progressTitle` | `#autoLOC_8003147/8003148/8003149/8003150/8003151` → 中文 |

`GameDatabase` 协程里唯一的英文硬编码：`Assembly-CSharp.full.cs:205052` → `gameDatabase.progressTitle = "Caching Assembly info";`；其余 4 处（205129 / 205636 / 205860 / 206034）是 `gameDatabase.progressTitle = <file>.url;`（资源路径）。

`dictionary.cfg` 对这些键的中文值：`158078 正在加载资源`、`158082 正在加载经验系统`、`158089 正在加载部件升级`、`158628 正在加载资源包定义`、`158641 正在加载<<1>>个资源包对象`、`8003147 正在加载扩展包...`、`8003148 扩展包加载完成！`、`8003149 正在加载扩展包文件：<<1>>`、`8003150 正在验证扩展包：<<1>>`、`8003151 正在加载扩展包内容：<<1>> (<<2>>/<<3>>)`、`165067 正在加载…`。

### 1.3 tips 数组：在 `level0` 场景里，不在 cfg

`LoadingScreenState` 有 `public string[] tips;`；`SetTip()`（858–879 行）：

```csharp
string text = Localizer.Format("#autoLOC_165067");
if (current.tips.Length != 0) text = Localizer.Format(current.tips[UnityEngine.Random.Range(0, current.tips.Length)]);
tipsText.text = text;
```

tips 数组的键序列化在启动场景 `KSP_x64_Data\level0`（47,400 B）。按 Unity 序列化字符串布局（int32 长度 + UTF-8 + 4 字节对齐）扫出 536 条字符串，其中 131 条是 `#autoLOC_`、去重 **108 键**：

- `#autoLOC_7001100` … `#autoLOC_7001194`：95 条连续（前 3 条都是 `7001100`）
- `#autoLOC_8003370`–`8003378`：9 条
- `#autoLOC_6011177`–`6011179`：3 条
- `#autoLOC_8012050`：1 条

`dictionary.cfg` 对这 108 键 **108/108 全覆盖**，值全是中文：`7001100 = 正在加载…`、`7001101 = 正在给每个词加上K…`、`7001102 = 正在赞美固推神教…`、`6011177 = 灰色和黑色`、`6011178 = 灰色和橙色`、`6011179 = 灰色和蓝色`。

时序上也成立：`Localizer.Init()` 在 `Assembly-CSharp.full.cs:205049`，紧接 `LoadingScreen.StartLoadingScreens()`（205050）——字典先就绪，tips 渲染出来就是中文。`D:\Kerbal Space Program_newmod\settings.cfg` 里 `LANGUAGE = zh-cn`。

### 1.4 场景自带的标签文本

`level0` 里的 GameObject 名：`LoadingScreens` / `TipsText` / `ScreenImage` / `ProgressBar` / `Progress Text`（×2）/ `Version` / `Text` / `ExpansionsLoader` / `BundleLoader` / `TooltipCanvas`。其中 `Progress Text`、`Version` 是场景内 uGUI 对象的初始文本（不是 Localizer 键），在 `pool.jsonl` 里同样 0 命中。

### 1.5 mod 侧加载屏

| mod | 机制 | 证据 | 是否英文硬编码 |
|---|---|---|---|
| RP-1 | `LoadingScreenChanger` 覆盖 tips | `RP0.decompiled.cs:6961` `val.tips = new string[80]` + `string.Format("#rp0_loading_tip_{0}{1}", …)`，`tipTime = float.MaxValue`；`:6969` 前 3 屏 tips 固定为 `new string[1] { "#autoLOC_7001100" }` | 否（键，且 zh 80/80 全覆盖） |
| RP-1 | `ProgressTitle()` | `RP0.decompiled.cs:28621` `return "RP-1 Initialization & Setup";` | **是** |
| KSPBurst | `ProgressTitle()` | `KSPBurst.decompiled.cs:522` `return "KSPBurst: waiting for Burst compiler...";` | **是** |
| Kopernicus | `ProgressTitle()` 返回字段 | `Kopernicus.decompiled.cs:11160` `return _progressTitle;` | 动态（由代码/cfg 决定） |
| ROLoadingImages | 只换图，不改文本 | `ROLoadingImages.decompiled.cs:96` `LoadingScreenState obj = LoadingScreen.Instance.Screens[3];`（`MainLoadingScreenIdx = 3`） | 否 |
| AtmosphereAutopilot | 只往 `loaders` 里加 | `:1906` `List<LoadingSystem> loaders = LoadingScreen.Instance.loaders;` | 否（走基类 `"None"`） |

### 1.6 关键事实：Squad tips 的英文原文不在这套安装里

- 全盘扫描（`D:\Kerbal Space Program_newmod`，20,478 文件 / 27,328,923,732 B / 58.34 s）：`#autoLOC_165067` **只命中** `GameData\Squad\Localization\dictionary.cfg`。
- `dictionary.cfg` 只有 zh-cn 一个语言节点（12,028 条键），没有 en-us。
- 结论：加载屏 tips 的英文基准文本**在本安装中不存在**；能拿到的只有键名与中文译文。

## 2 为什么不在 `packs/pool.jsonl`

### 2.1 池内 / 池外计数（实测）

| 对象 | 条数 | 在 pool 里的命中 |
|---|---|---|
| Squad tips 键（95 条 `#autoLOC_70011xx`） | 95 | 键 0 条 / 中文值 0 条 |
| RP-1 tips 键（`#rp0_loading_tip_001`…`_080`） | 80（en 80 / zh 80 / 缺 0） | 键 0 条 / 英文值 0 条 |
| `Caching Assembly info`（IL 字面量） | 1 | 0 |
| `None`（基类 ProgressTitle） | 1 | **1**（巧合：某 mod 的 cfg 文本恰好是 `None`，与加载屏无关） |
| `Loading` / `Progress Text` / `Version`（场景标签） | 3 | 0 |
| 进度条 11 个 `#autoLOC_*` 键 | 11 | 键 0 条（中文值在 dictionary.cfg，不在 pool） |

### 2.2 逐类根因

1. **loc 源（`collect_localization`，`harvest.py:168`）只收「有 en-us 没 zh-cn」的键**。Squad 的 `dictionary.cfg` 只有 zh-cn 节点 → 该 mod 的 en 集合为空 → 差集为空 → 0 候选；RP-1 的 80 条 tips 键 en/zh 都有 → 差集为空 → 0 候选。**加载屏 tips 在数据层就是「已覆盖」，天然不进池。**
2. **cfg 源（`collect_cfg`，`harvest.py:255`）只认 `title` / `description` / `displayName`（含 `_title` 等后缀变体）与 `tags` 字段**。加载屏的键与文案都不在这些字段里（它们在 DLL 常量、场景序列化、或 `Localizer.Format` 参数里）。
3. **`Caching Assembly info` 是 IL 字面量**，harvest 不读 `.dll`（§3），所以连候选都算不上。
4. **`Progress Text` / `Version` 是场景里的 uGUI 初始文本**（`level0`），同样不在两个来源里。

→ **结论：不是被过滤器吃掉，而是 100% 没进候选。** 本仓库 harvest 的 skip 计数（dup 4461 / search-tags 1597 / already-cjk 32 / pure-number 8 / too-short 6 / url-only 1 / already-covered 0 / empty 13）里没有一条来自加载屏文本。

### 2.3 为什么 round 3 加载期 `distinct hits` 一直是 0

`reports/shot-round3-095703.log`：14 条 `[UI18n] distinct hits: 0`（09:57:58–10:02:40），同期 `TMPro.TMP_Text.set_text` 从 4,452 涨到 307,480，`patch stats` 里 16 个 patch 全部 `hit=0`；日志第 104 行 `[UI18n] font probe: scene=LOADING (fallback after 180s)` 说明 10:00:38 时仍在加载场景（加载期 > 3 分钟），10:03:21 才进主菜单。

原因：hook 统计的是「命中词典并替换」的次数；加载屏渲染出来的文本**本身就是中文**（dictionary.cfg zh-cn + RP-1 的 80 条中文 tips），而词典里存的是**英文**键 → 没有一条能命中。**这不是缺陷，是「数据层已覆盖」的正确表现。**

## 3 harvest 的取源边界（追加问题 1）

### 3.1 代码证据（`tools/i18n_tools/harvest.py`）

| 行号 | 代码 | 含义 |
|---|---|---|
| 3 | `读：<游戏根>\GameData\**\*.cfg` | 模块 docstring 明写唯一来源 |
| 103 | `def _walk_in_order(root_dir, parts, out, depth=0)` | 唯一遍历入口（`:120` 用 `os.scandir` 递归） |
| 133-134 | `if not ent.name.lower().endswith(".cfg"): continue` | **只收 `.cfg`，`.dll` 一律跳过** |
| 135 | `mod = parts[0] if parts else ent.name` | mod = GameData 顶层条目名 |
| 140 | `"loc": any(p.lower() == LOC_DIR_NAME for p in parts)` | Localization 目录判定 |
| 144-162 | `def discover(gamedata)` → `_walk_in_order(gamedata, [], entries)` | 唯一输入是 CLI `--gamedata`（`:537-538`） |
| 32 / 35 / 36 / 45 | `LOC_DIR_NAME` / `CORE_TEXT_FIELDS` / `TAGS_FIELD` / `FIELD_VARIANTS` | 两个源的全部判定常量 |
| 351-383 | `build_rows` 过滤链 | normalize → empty → has_cjk → is_pure_number → len<2 → tags → is_url_only → dup |

全文件 grep：**没有** `Managed`、没有 `.dll`、没有 `Assembly-CSharp`。

### 3.2 结论

harvest 的覆盖范围 = **`GameData/**/*.cfg`**：

- `KSP_x64_Data\Managed\*.dll`（Assembly-CSharp / UnityEngine.*）→ 完全不在覆盖内（连 `GameData` 都不在同一目录树下）。
- `GameData\**\*.dll`（mod 插件，如 `RP-1\Plugins\RP0.dll`、`KSPBurst.dll`）→ **同样不读**（`:133` 只认 `.cfg`）。

所以「KSP 自身硬编码文本没被采集」成立，但更准确的说法是：**所有 DLL 的 IL 字面量都没被采集**，KSP 本体与 mod 插件一视同仁。

### 3.3 更正一处前提：5 个调试串里 4 个不在 Assembly-CSharp

逐串定位（`%TEMP%\t24\Assembly-CSharp.full.cs` 全文 + 全盘 DLL 的 UTF-16LE 字面量搜索，扫 222 个 DLL）：

| 字符串 | 所属程序集 | pool 命中 | 备注 |
|---|---|---|---|
| `Debug Toolbar` | `KSP_x64_Data\Managed\Assembly-CSharp.dll` | 0 | 反编译全文第 237436 行：`windowRect = GUILayout.Window(9999, windowRect, Window, "Debug Toolbar", GUILayout.ExpandHeight(expand: true));` |
| `Vessel mass` | `GameData\RCSBuildAid\Plugins\RCSBuildAid.dll` | 0 | mod 插件 |
| `Dump Wheel Debug Data` | `GameData\KSPWheel\Plugin\KSPWheel.dll` | 0 | mod 插件 |
| `All Input Locks Cleared` | `GameData\000_ClickThroughBlocker\Plugins\ClickThroughBlocker.dll` | 0 | mod 插件 |
| `Flight Data Engineer Multiplier` | `GameData\TestFlight\Plugins\TestFlightCore.dll` | 0 | mod 插件 |

这 5 条确实全是 POOL MISS，但只有 1 条来自 KSP 本体；另外 4 条是 **GameData 下的 mod 插件 DLL**。这反而说明：只补 Assembly-CSharp 仍然覆盖不到 mod 插件的 UI 文本（RP-1、KSPBurst 的加载屏标题正是这类）。

## 4 把 IL 字面量纳入采集的量级（追加问题 2）

### 4.1 方法

直接解析 PE/CLI 元数据的 `#US`（UserString）堆：压缩 uint 长度 + UTF-16LE + 1 字节 flag。`#US` 堆按唯一性存储，所以「堆内条数 = distinct 字面量数」。脚本 `%TEMP%\ui18n_t24_us.py`（`us_literals()`，约 60 行，纯标准库），过滤口径复用 `i18n_tools.textnorm` 与 `fnv.hash_text`（与 harvest 完全同口径）。

### 4.2 Assembly-CSharp.dll 的逐级数字

文件 10,790,912 B，`#US` 堆 810,756 B：

| 阶段 | 条数 |
|---|---|
| IL 字面量总数（= distinct） | **18,456** |
| 过 harvest 同口径过滤后（`normalize` / empty / already-cjk / pure-number / len<2 / url-only / dup） | **18,096** |
| 其中 ≥4 字符（Lead 追加口径） | **17,730** |
| tierA：再剔 `#…` 键、`[` 日志、纯标识符、含 `/` `\`、XML 标签、含 `=` | **3,997** |
| tierB：tierA 且含空格、含小写字母（「像句子」） | **3,001** |
| tierC：tierB 且长度 ≤160 | **2,971** |
| 与 `pool.jsonl` 重叠（按 `h`） | tierA 11 / tierB 9 → **净新增 ≈ 3,988 / 2,992** |

过滤明细（18,456 → 18,096）：empty 19、already-cjk 2、pure-number 107、too-short 134、url-only 7、dup 91（normalize 后才相同的重复）。

### 4.3 噪声分类（18,096 条内部，类别可重叠）

| 类别 | 条数 | 例子 |
|---|---|---|
| `#autoLOC_<数字>` 键引用 | **5,043** | `#autoLOC_6002406`（运行时由 Localizer 解析，不该当文本翻） |
| 其他 `#` 开头（颜色 / 小写键变体 / 格式模板） | 1,276 | `#ffffff`、`#autoLoc_6003018`、`#{0:X2}{1:X2}` |
| 日志 / 异常消息（`[` 开头） | 985 | `[ROCEmitter]: Cant play idle - no emitters found` |
| 纯标识符（字段名、枚举名） | **6,094** | `weightedDrag`、`ScanType`、`unitName` |
| snake_case 配置键 | 265 | `geyserVFXSizeFromDistance` |
| 路径 / 资源名（含 `/` `\`） | 474 | `Squad/Flags/B612_Foundation_flag` |
| 含 `{` `}` 的格式串 | 422 | `{0}` 模板 |
| 含 XML / 富文本标签 | 204 | `<color=#ffffff>` |
| **合计噪声（粗算、类别间有重叠）** | **≈ 14,000** | 占 18,096 的 77% 以上 |

### 4.4 真正贴着 UI 的字面量：IMGUI 调用点

对反编译全文做调用点扫描（`GUI.*` / `GUILayout.*` 的 Label/Button/Toggle/Box/TextField/TextArea/Window/SelectionGrid/Toolbar/RepeatButton）：

- 调用点 **450 处**
- 参数里出现的字面量 **368 次 / 271 distinct**（其中 269 条能在 `#US` 堆里对上，说明扫描没漏）
- 271 条里 **45 条是 `#autoLOC_*` 键**（KSP 自己已经走 Localizer），**226 条是硬编码英文**，例如：
  - `Allow Part Clipping in Editors [Bug Hazard!]`
  - `Aero Forces Display Scale: `
  - `Add up all the Science in the Universe`
  - `+10k Funds`、`+50 Science`、`-25 Rep`
  - `Debug Toolbar`（就是 Lead 举的那条）

→ **KSP 本体真正「硬编码在 UI 上」的英文文本约 226 条**（集中在调试 / 气动 / 作弊窗口），不是 18,456 条。这也解释了为什么「全量 IL 采集」性价比极低：**有效信号占 1.2%**。

### 4.5 如果把所有 DLL 都算进来

`KSP_x64_Data\Managed` + `GameData` 全树 222 个 `.dll`，196 个解析成功（26 个 Unity 模块没有 `#US` 堆）：

| 指标 | 数值 |
|---|---|
| IL 字面量总数 | **92,855** |
| 过滤后（kept） | 86,605 |
| 其中 ≥4 字符 | 82,722 |
| 跨 DLL 去重后的并集 | 76,677 |
| 与 pool 重叠（按 `h`） | 296 |
| 「像 UI 文本」启发式（剔键 / 日志 / 标识符 / 路径 / 格式 / XML，需含空格） | 24,828（仍含大量碎片，如 `" (vel:`、`" Link Text: "`） |
| 加载相关字面量（含 load / initializ / prepar / compil / setup，需含空格） | 709 次 / **680 distinct**，绝大多数是日志 |

Top 大户（≥4 字符条数）：`Assembly-CSharp.dll` 17,730、`ModularFlightPanel.dll` 6,362、`alglib.dll` 6,216、`System.dll` 5,557、`mscorlib.dll` 4,655、`System.Xml.dll` 3,263、`Kerbalism.dll` 2,973、`RP0.dll` 2,688、`TUFX.dll` 2,012。

## 5 值得做吗（判断 + 方案，不实施）

### 5.1 判断

| 方案 | 新增候选 | 噪声 | 工作量 | 结论 |
|---|---|---|---|---|
| A. 全量采 Assembly-CSharp 的 `#US` | 17,730（≥4 字符） | ≈77% | 解析器已有；集成 + 过滤 + 人工核对 ≈ 2–3 人日 | **不值得**：18k 条里真 UI 文本约 226 条，翻译成本与误翻风险都不可接受 |
| B. 只采 IMGUI 调用点字面量（编译期白名单） | **226** | 低（仍有少量调试窗口文案） | 调用点扫描脚本已有；集成 ≈ 0.5–1 人日 | **值得**：规模可控，正好补上 `Debug Toolbar` 这类 |
| C. 运行期观测（推荐） | 取决于实际渲染 | 最低 | 现有 hook 已统计 `distinct hits`，改成 dump 文本 ≈ 0.5 人日 | **最值得**：只收真正渲染过的字符串，带出现次数，可先翻高频 |
| D. 专为加载屏加一路采集 | KSPBurst 1 + RP-1 1 + `Caching Assembly info` 1 + `None` 1 ≈ 4 条 | 极低 | 半天 | **不值得单独做**：4 条字符串手工进词典更快；且 Squad tips 的英文原文不存在（§1.6），采了也译不了 |

### 5.2 建议路径（如果 Lead 决定要做）

1. **不要动 `packs/pool.jsonl`**：它是下游 `h` 契约的基准（12,792 行 / 3,049,123 字节 / sha256 `FEF515EA…D4C`），加来源会改 id/h 集合，`packindex` 的 `totalRows / totalBytes / coverage` 全部要重算，冻结期不做。
2. 新增**独立产物** `packs/il-strings.jsonl`（字段 `h / text / mod / asm / kind`，`kind ∈ {imgui, progresstitle}`），与 pool 解耦。
3. 译文进**独立包**（例如 `packs/zh-cn/_il-hardcoded.jsonl`，先不登记进 `index.json`，或单列 `schema` 字段），避免污染现有 41 个包 / 9,990 行的统计。
4. 优先级：先做 B（226 条 IMGUI）+ D 的 4 条加载屏硬编码 → 再观察运行期 `distinct hits` 是否真的需要 C。
5. 任何一步都要配 `selftest` 断言 + `packindex --check` 回归。

### 5.3 风险

- **契约风险**：pool 变更 → 下游 Core 的 `h` 索引、coverage 分母、`selftest` 128 项断言全部受影响。
- **误翻风险**：把字段名 / 日志 / 资源路径当 UI 文本翻译会污染词典，而且 hook 会真的替换掉（例：`ScanType`、`Squad/Flags/…`）。
- **资产依赖风险**：`level0` 场景与 `#US` 堆都随 KSP 版本变化（1.12.5 专用），升级要重跑。
- **合规/体积**：IL 字面量含 KSP 版权文案，产物只宜作本地翻译输入，不进公开仓库。

## 6 复现命令与脚本

| 目的 | 命令 / 脚本 |
|---|---|
| 全量反编译 | `$env:DOTNET_ROLL_FORWARD='LatestMajor'; & "C:\Users\43701\.dotnet\tools\ilspycmd.exe" "D:\Kerbal Space Program_newmod\KSP_x64_Data\Managed\Assembly-CSharp.dll" > $env:TEMP\t24\Assembly-CSharp.full.cs`（29,950,114 B，约 105 s） |
| 加载屏反编译 | 同上，只反编译 `LoadingScreen` / `LoadingSystem` 两个类型 |
| `#US` 字面量提取 | `python %TEMP%\ui18n_t24_us.py`（总量、过滤明细、噪声分类、样例） |
| 全 DLL 普查 | `python %TEMP%\ui18n_t24_us4.py`（196/222 解析成功；92,855 条） |
| IMGUI 调用点字面量 | `python %TEMP%\ui18n_t24_us5.py`（450 调用点 / 271 distinct） |
| level0 字符串 | `python %TEMP%\ui18n_t24_l0str.py`（536 条 / 108 个 `#autoLOC_` 键） |
| 池内交叉核对 | `python %TEMP%\ui18n_t24_pool.py` |
| 加载屏相关字面量 | `python %TEMP%\ui18n_t24_loadlit.py`（680 distinct） |

## 7 未做的事 / 限制

- 未改 `packs/**`、未改 `tools/i18n_tools/**`（本轮只读）。
- 未实施任何修法（§5 只是评估）。
- 未做 1.11 / 1.12 其他版本的横向对比。
- IMGUI 调用点扫描基于反编译 C# 的正则 + 括号配平（近似），可能与真实 IL 调用点有少量出入；但 271 条字面量里 269 条能在 `#US` 堆对上，误差 < 1%。
- `#US` 堆只能拿到「字符串字面量」，拿不到「哪个方法用了它」；§1.2 的类归属靠反编译全文交叉定位（已给行号）。

## 8 与 Lead 前提的两处更正

1. **「这 5 个字符串都在 Assembly-CSharp.dll」** → 只有 `Debug Toolbar` 在；`Vessel mass` / `Dump Wheel Debug Data` / `All Input Locks Cleared` / `Flight Data Engineer Multiplier` 分别在 `RCSBuildAid.dll` / `KSPWheel.dll` / `ClickThroughBlocker.dll` / `TestFlightCore.dll`（都在 `GameData` 下）。所以「补 Assembly-CSharp」解决不了 mod 插件文本。
2. **「加载期 0 hits 是覆盖缺口」** → 加载屏文本本身已是中文（dictionary.cfg zh-cn + RP-1 中文 tips），0 hits 是「词典里只有英文键、渲染的是中文」的必然结果；真正的缺口只有 3–4 条硬编码英文（KSPBurst / RP-1 的 `ProgressTitle`、`Caching Assembly info`、基类 `None`）。
