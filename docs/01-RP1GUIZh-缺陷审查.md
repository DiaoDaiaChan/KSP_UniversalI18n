# RP1GUIZh 缺陷审查（v1）

> 审查对象：`GameData/RP1GUIZh/RP1GUIZh.dll`（52,224 字节，AssemblyVersion 0.0.0.0，文件夹内仅此一个文件）
> 方法：ilspycmd 反编译全量 C#（994 行）+ KSP.log 运行时日志（22:20:16–22:20:32 段）+ 反编译 RP0.dll 中被它改写的成员
> 结论：**它不能作为我们 L2 层的参考实现**。它的 8 个 IMGUI patch 目标选得对，这部分可以抄；
> 其余（字典模型、翻译算法、初始化时机、异常策略、玩法侵入）几乎每一处都要反着做。

反编译产物：`UniversalI18n/_audit/dec/RP1GUIZh.decompiled.cs`（下文行号均指此文件）

---

## 0. 它的真实结构（先看清楚它是什么）

| 成员 | 行号 | 实际职责 |
|---|---|---|
| RP1GUIZhLoader.Start() | 19–205 | 打 8 个 IMGUI patch + 4 个 RP-1 玩法 patch + 2 个 uGUI patch |
| TryPatch() | 206–229 | 按精确签名 patch，失败返回 0 |
| TryPatchAny() | 230–255 | **死代码**，全文件只出现 1 次（定义处），从未被调用 |
| GuiTrans.TryTranslateDepth() | 263–342 | 翻译查找：Map 精确 → Prefixes → Substrings |
| GuiDict.Map | 345–747 | **399 条**硬编码词典 |
| GuiDict.Prefixes | 748–761 | 10 条前缀规则 |
| GuiDict.Substrings | 762–767 | 2 条子串规则 |
| Patches.* | 768–994 | 7 个 IMGUI prefix + 4 个玩法 patch + 1 个 transpiler |

初始化时机（IL 属性字节 `01 00 fe ff ff ff 01 00 00` → `KSPAddon(Startup.Instantly, once: true)`）：
**游戏启动瞬间**，早于主菜单。日志佐证：addon 实例化 22:20:20.563，patch 完成 22:20:32.514。

---

## 1. P0：它改了玩法，而它自称是汉化 mod

这是最不能接受的一类问题。一个"汉化"插件在玩家不知情的情况下改了 4 处游戏逻辑：

### 1.1 强制跳过 RP-1 首次运行引导

`Patches.FirstRunNotCompletePrefix`（行 835）：

```
public static bool FirstRunNotCompletePrefix(ref bool __result)
{
    __result = false;
    return false;   // 跳过原方法
}
```

RP0 原实现（`RP0.dll` → `RP0.SpaceCenterManagement.FirstRunNotComplete`）：

```
get {
    if (!StarterLCBuilding || !HiredStarterApplicants || !StartedProgram || !AcceptedContract)
        return !DontShowFirstRunAgain;
    return false;
}
```

即：**未建首发综合体 / 未雇初始求职者 / 未启动项目 / 未接合同** 时它返回 true，用来驱动首次运行窗口。
被 patch 成永远 false → 新手引导永久不出现。这是给老玩家的便利，对新玩家是"任务没给你就开始了"。

### 1.2 强行解除 KSC 输入锁

`Patches.UnlockKSCOverridePostfix`（行 841）：postfix `KCT_GUI.SetGUIPositions`，反射调用
`InputLockManager.RemoveControlLock("SCMKSCLock")`。
RP-1 生涯里 KSC 设施是要建/解锁的，这个锁就是那道进度门。这里直接拆了。

### 1.3 改私有静态字段摆窗口

`_firstRunPosField`（行 770）+ `FirstRunPositionPrefix`（行 819）：
把 `RP0.KCT_GUI._firstRunWindowPosition` 强制设为 `new Rect(20, 20, 540, 50)`。
RP0 原值：`new Rect((Screen.width - 540) / 2, Screen.height / 5, 540f, 50f)`。
动机大概是"窗口挡住了别的东西"，但这是拿汉化当外壳修 UI 布局。

### 1.4 给存档函数套全局 try-catch，吞掉所有异常

`Patches.ContractSaveTranspiler`（行 938–994）：把所有 `ret` 改成 `leave`，
方法体整体包进 `try { … } catch (Exception e) { Debug.Log(e); }`，作用于 `Contracts.Contract.Save(ConfigNode)`。

后果：
- 合同存档本来会抛的异常（空引用、序列化失败）被静默吞掉 → **存档缺字段而不报错**，坏档后无法定位；
- 手工构造 `ExceptionBlock` 极其脆弱：只要目标方法有多个 `ret`、`ret` 在已有 try 内、或将来 KSP 版本改了方法体，
  就会生成非法 IL → `InvalidProgramException`（本机日志 0 次，属于"现在侥幸没炸"）。

**对我们的意义**：这条直接印证了设计文档里"绝不碰玩法层"的硬约束。
L2 层的任何 patch 都必须满足：**只读入参、只改显示值、不调用任何游戏 API、不吞异常**。

---

## 2. P0：无语言判断，永远强制中文

全文件检索：`Localizer` 出现 **0** 次，`Language` 出现 **0** 次。
它从不读 `Localizer.CurrentLanguage` / `LanguageSettings`，装上就无条件把英文换成中文。

后果与"优先支持 i18n 的 mod"这条要求**正面冲突**：
- 一个已经有高质量 zh-cn 的 mod（Kerbalism 10,316 key、MechJeb2 5,912 key、TUFX、Principia…）
  如果它的某句 UI 文本恰好等于 RP-1 表里的某个键（"Save"/"Cancel"/"Close"/"Yes"/"No"/"Name"/"Max"…），
  就会被这张 399 条的 RP-1 机翻表**覆盖掉专业翻译**；
- 玩家把游戏语言切成 en-us 想看英文时，它照样出中文，没有任何开关。

---

## 3. P1：TMP 完全没翻，而且日志在骗人

`Start()` 行 174–191 用 `AccessTools.TypeByName("TMPro.TextMeshProUGUI")` + `AccessTools.PropertySetter(type, "text")`，
拿到的是**派生类型上继承来的** MethodInfo，Harmony 拒绝并抛：

```
System.ArgumentException: You can only patch implemented methods/constructors.
Patch the declared method System.Void TMPro.TMP_Text::set_text(System.String value) instead.
    at RP1GUIZh.RP1GUIZhLoader.Start () [0x0060e] in <4d4d51a5667d42c09c9fde271ef6e17c>:0
```

三个连带问题：

1. **正确做法**：patch `TMPro.TMP_Text::set_text`（声明方法）。KSP 1.12.5 的 TMP 编译进 `Assembly-CSharp.dll`。
2. **错误信息误导**：`Text.set_text` 的 patch 在同一个 try 块里**先成功**（日志 22:20:32.514
   "Patched UnityEngine.UI.Text.set_text (uGUI)"），TMP 随后才抛异常，catch 打的是
   "uGUI Text patch failed" → 任何人看日志都会以为 uGUI 也没打上。**实际是 uGUI 成功、TMP 失败**。
3. **覆盖面被高估**：KSP 1.12 的编辑器部件列表、KSC 建筑、越来越多的 mod 都用 TMP。
   它实际只能翻 IMGUI + 少量 uGUI Text。

---

## 4. P1：字典本身的三个结构性缺陷

### 4.1 25 条含格式模板的条目永远匹配不到

Map 里 25 条键长这样：

```
"BP Cost: {buildPoints:N0}"          => "建造点成本：{buildPoints:N0}"
"Hire {workers:N0}: √{_fundsCost:N0}" => "雇用 {workers:N0}：√{_fundsCost:N0}"
"EC: {SpaceCenterManagement.Instance.EditorVessel.effectiveCost:N0}" => …
```

这些是**C# 插值模板的源码形态**，不是运行时的字面量。实际 IL 里只有 `ldstr "BP Cost: "` + 格式化调用，
运行时文本是 `"BP Cost: 1,234"`。而 `Map` 是**精确匹配** → 这 25 条 100% 命中不了，是死条目。
反过来说明：字典是**照源码手抄的**，不是从 IL 采集的。

### 4.2 27 条键带首尾空格，靠"恰好相等"匹配

`"Body: "`、`"Description: "`、`"Altitude from SL: "` … 这类键靠"末尾恰好有一个空格"才能命中。
没有任何归一化（不 Trim、不折叠空格、不统一大小写）→ 上游多一个或少一个空格就永久失效。
另有 44 条 ≤6 字符的短键（`"Yes"`、`"hangar"`、`"Max"` …），见 6.1 的跨 mod 污染问题。

### 4.3 Prefixes 无条件 return true，Substrings 全局 Replace

`TryTranslateDepth`（行 263）的三段逻辑各自有坑：

| 段 | 代码 | 问题 |
|---|---|---|
| Map | `Map.TryGetValue(text)` | 精确匹配，动态文本（带数字/名字）永远打不中 |
| Prefixes | `if (text.StartsWith(k)) { … return true; }` | **命中即返回 true 并停止扫描**，后面更精确的条目永远不生效；剩余英文原样拼在中文前缀后 → "分配你的 Jebediah Kerman" |
| Substrings | `output = text.Replace(k, v)` | **对任意包含该子串的文本生效，且替换所有出现** |

Substrings 现在只有 2 条（`"infinity at "`、`" starting Applicants"`）所以还没炸，
但机制本身是定时炸弹：谁往表里加一个 "OK" / "Min" / "TWR"，立刻出现 `"BROKEN" → "BR确定EN"` 这种破坏。

### 4.4 uGUI 与 IMGUI 两条路径实现不一致

`TryTranslateUGUI`（行 884）是 `TryTranslateDepth` 的**复制粘贴残次版**：
- IMGUI 路径：Prefixes 命中后**递归**翻译剩余部分（深度上限 4）；
- uGUI 路径：Prefixes 命中后只做**一层** `Map.TryGetValue(text3)`；
- uGUI 路径**没有 Substrings 逻辑**。

→ 同一句话，IMGUI 渲染和 uGUI 渲染出来可能不一样。这是典型的"复制代码没同步"。

---

## 5. P1：性能与分配

每次 `GUI.Label` / `GUILayout.DoLabel` 调用（每帧、每个控件）走一遍：

1. 1 次 `Dictionary.TryGetValue`；
2. 最多 10 次 `string.StartsWith(…, Ordinal)`；
3. 最多 2 次 `string.IndexOf(…, Ordinal)`；
4. 未命中时若带数字前缀，再 1 次 `TryGetValue`。

命中路径**至少分配 1 个新 string**（`text2 + output` / `prefix.Value + text3` / `input.Substring(num + 2)`，共 4 处 `Substring` / 6 处 `+ "…"`）。
IMGUI 每帧重建 UI，KCT 一个窗口几十个 Label → 每帧几百个临时字符串。KSP 是 Unity 2019 + Mono，GC 尖峰直接掉帧。

**没有负缓存、没有"已翻译"标记、没有按帧预算、没有飞行场景降级开关。**

---

## 6. P2：工程与可维护性

| # | 问题 | 证据 |
|---|---|---|
| 1 | 词典硬编码在 DLL 里，改一个错字要重新编译发版 | Map 行 345–747 是 `static readonly` 初始化器 |
| 2 | 版本号 0.0.0.0 | 日志 "RP1GUIZh 0.0.0.0 0.0.0.0"；CKAN/依赖解析无法识别 |
| 3 | 无配置文件、无开关、无 per-mod 开关 | 文件夹内只有 RP1GUIZh.dll |
| 4 | 无 Harmony priority / after / before | 全文件 `Priority` 出现 0 次 → 与其他 mod 的 patch 顺序不确定 |
| 5 | 异常全 catch 只 log，patch 失败用户无感知 | TryPatch 行 206–229 |
| 6 | 死代码 TryPatchAny（遍历 AppDomain 找类型） | 行 230–255，全文件仅 1 次出现 |
| 7 | 无字体兜底（全文件 `Font` 出现 0 次） | en-us 环境下中文全是豆腐块 |
| 8 | 无版本/源文哈希 → 上游英文一改，译文静默失效且无统计 | Map 只有 src→dst 两列 |

### 6.1 全局 patch 导致跨 mod 污染

它 patch 的是 Unity 引擎级的 `GUI.Label` / `GUILayout.DoLabel` 等漏斗函数，
**对全游戏所有 mod 生效**，且无 per-mod 门控。而表里有 44 条 ≤6 字符的通用键：

```
Add / Amount / Budget / Build / Cancel / Clear / Close / Cost: / Edit / Empty / Fill / Launch /
Max / Max: / Min: / Modify / Name / Name: / Next: / No / Plans / Rate: / Remove / Rename /
Save / Scrap / Select / Sort: / Staff / Total / Upkeep / Warp / Yes / EVA / hangar / recon / repair
```

这些词几乎每个 mod 的 UI 都有。结果是"意外地像个通用汉化 mod"——
但这是**未设计的副作用**：既是它能覆盖别的 mod 的原因，也是它会误翻别人界面的原因。
（对我们的意义：global 层必须有、必须人工 curation、必须有冲突仲裁，不能靠撞。）

### 6.2 直接改写调用方对象

`Translate(ref GUIContent content)`（行 811）执行 `content.text = output`。
`GUIContent` 是**引用类型**，很多 mod 把它缓存在静态字段里（RP0 自己就有
`private static readonly GUIContent _gcSwitchToHangar = new GUIContent("Switch to Hangar", …)`）。
→ 汉化会**永久写脏 mod 的静态对象**，切回英文也不会恢复；若有代码比较 `content.text` 与英文字面量判断逻辑，直接坏。

---

## 7. 它做对了什么（可以抄的部分）

1. **patch 目标选对了**：不 patch 公开重载（`GUILayout.Label(string)` 打不了），
   而是 patch 内部分发漏斗 `GUILayout.DoLabel(GUIContent, GUIStyle, GUILayoutOption[])` —— 本机日志 8/8 成功。
2. **用 `ref GUIContent` 的 Prefix 就地替换**，不重写原方法、不碰返回值。
3. **patch 失败只 log 不中断**（方向对，实现要改：要能上报、要有 UI 提示）。
4. **带数字前缀的 IMGUI 文本处理**（`"12) Something"` 拆出 `"12) "` 再翻主体）—— 这个思路是真的踩出来的经验，值得保留。
5. `KSPAddon` + 一次性的 patch 装填模式。

---

## 8. 据此对总体设计的修订（8 条）

| # | 原设计 | 修订 |
|---|---|---|
| 1 | L2 只翻 UI | **加硬约束**：L2 的 Prefix 只允许改传入的 string/GUIContent 副本，禁止调用任何游戏 API、禁止 transpiler 改方法体、禁止 try-catch 包裹游戏方法 |
| 2 | 未明确 | **新增"玩法隔离"门禁**：任何 patch 目标必须能证明是渲染层（Unity 引擎/UI 类型），CI 里对 patch 清单做白名单校验，RP-1 这类领域类型一律拒绝 |
| 3 | 语言无关 | **强制语言门控**：`Localizer.CurrentLanguage` 不匹配目标语言时直接短路，不做任何查找（也顺带省掉全部开销） |
| 4 | 三层键 ctx/site/global | **强化 global 层**：global 只收人工审核过的通用词，且带 per-mod 冲突仲裁；不自动扩充 |
| 5 | 未明确 | **禁止子串替换**：Substrings 这类规则整条删掉；前缀规则必须"命中即继续扫描更精确规则"而不是 return true |
| 6 | 未明确 | **不修改调用方对象**：GUIContent 路径改为 `new GUIContent(translated, originalTooltip)` 或直接改 Harmony 的局部变量，绝不在 mod 的静态缓存上原地写 |
| 7 | 性能预算 | **补负缓存 + 已翻译标记 + 帧预算**：未命中路径 < 15 ns 靠 `HashSet<ulong>` 负缓存；命中路径复用池内 string 引用，0 分配 |
| 8 | 字体兜底 | **提升为 M0 第一优先级**：本机 GameData 下无任何 CJK 字体，RP1GUIZh 也 0 处字体代码 —— 这是它"在某些人机器上是方块"的根因 |

补充两条流程性修订：

- **字典不能手抄源码**：RP1GUIZh 25 条模板死条目就是"照源码手写"的代价。我们的采集器必须从 IL 取真实字面量。
- **必须有覆盖率指标**：每次发版统计 hit / miss / stale，否则像 RP1GUIZh 一样"以为翻了 399 条，实际能命中多少没人知道"。
