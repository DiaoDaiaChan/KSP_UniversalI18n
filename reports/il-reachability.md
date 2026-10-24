# IL 字符串 sink 可达性盘点（T29）

> 目标：把「mod DLL 里硬编码的 UI 字符串」变成可翻译候选集，并给出可信量级。本轮**只出候选与证据**：未改 `packs/pool.jsonl`、未改 `packs/zh-cn/**`、未改 `packs/index.json`、未改 `src/**` / `tests/**` / `docs/**`。
> 扫描范围：`KSP_x64_Data\Managed\Assembly-CSharp.dll` + `GameData\**\*.dll`；共 129 个 DLL、177,169 个方法体、耗时 11.9s。
> 产物：`packs/il-candidates.jsonl`（2,285 行）与本报告。

## 0 结论摘要

1. **真解 IL 的终止率**：177,169 个方法体按操作码 + 操作数长度逐条解码，落到方法体末尾之外（即解码失败）的 **0** 个；数据流分析中止 11 个（遇到未覆盖指令），其余全部完成。
2. **字面量漏斗**（分 DLL 口径相加，非全局去重）：#US 去重字面量 **70,331** → 过滤后 **65,163** → ≥4 字符 **63,077** → 像 UI 文本 **31,795**。
3. **sink 可达候选**：`packs/il-candidates.jsonl` **2,285** 行（按 (asm, n) 去重），跨 DLL 去重后 **2,082** 条 distinct 文本。
4. **噪声**：2,082 条 distinct 里真正「像句子」的 **1,669** 条（80.16%）；其余 413 是键引用 / 路径 / 标识符 / 日志串等。
5. **与既有池的交集**：65 条 distinct（3.12%）的 h 已出现在 `packs/pool.jsonl` 里，不该重复翻译。
6. **加权覆盖率（回答「怎么才算通用任意 mod」）**：按调用点数加权，PatchRegistry 的 16 个 sink + `GUIContent..ctor` 覆盖 **97.09%** 的文本写入路径（8,004 / (8,004 + 240 + 0)）；按去重字面量加权 **99.32%**。剩下的大头是 `TMPro.TMP_InputField.set_text`（176 次）、`TMPro.TMP_Text.SetText`（26 次）、`UnityEngine.GUIContent.set_text`（13 次）。

## 1 口径与方法

### 1.1 什么算「sink 可达」

- 只认 `ldstr` 直接、或经 `string.Concat` / `string.Format` / `GUIContent..ctor` / `stloc`+`ldloc` / `dup` / `box` 流向 sink 参数的常量；**方法体内深度 1 层，不跨方法**。
- 不允许：先存静态字段再读、经返回值传出、经数组/字典、经 `ToString()` 生成。
- sink 白名单 = `src/UI18n.Runtime/Patches/PatchRegistry.cs` 的 16 个目标 + `GUIContent..ctor`（IMGUI 文本的唯一入口）。
- **`Debug.Log*` / 异常消息不是 sink**，单独计数只做噪声对照（§9.3）。
- 取实参时按「参数声明类型是 `System.String`」定位，因此同一 API 的所有重载自动覆盖。
- **本项目的自身程序集不计入**（`UI18n.Core.dll` / `UI18n.Runtime.dll` / 测试期临时部署的 `UI18n.Probe.dll`）：它们是产品与探针，不是 mod；探针 DLL 会随测试装卸，纳入会让候选集不可复现。

### 1.2 为什么必须真解 IL

正则扫字节码会把操作数里的字节当成指令。本实现按 ECMA-335 操作码表逐条解码（含 `0xFE` 双字节前缀、`switch` 变长操作数），解码必须正好落到方法体末尾，否则计入 `decode_fail`。实测 177,169 个方法里只有 0 个失败，这条数字就是「操作码表没写错」的证据。

签名（参数类型）解析失败 0 个 —— 这个数必须是 0：签名解析一失败，调用点的实参就会整体错位，sink 参数取不到字面量（`GUILayout.Width(float)` 曾被读成「无参数」，导致 `GUILayout.Label(string)` 的文本全部丢失）。

数据流中止 11 个方法体，原因分布：`<step-limit>` 11（`<step-limit>` = 同一方法内状态合并次数超上限，通常是巨型 switch 的状态爆炸；`calli` 与未解析 token 也走同一保守策略：宁可漏报不误报）。

## 2 漏斗表

全量（所有 DLL 相加；`#US 去重` 是每个 DLL 内部去重后相加，不是全局去重）：

| 阶段 | 条数 |
|---|---|
| #US 字面量（DLL 内去重后相加） | 70,331 |
| 过滤后（empty / already-cjk / pure-number / too-short / url-only / dup 全去掉） | 65,163 |
| ≥4 字符 | 63,077 |
| 像 UI 文本（≥2 词，或 ≥4 字母首字母大写单词） | 31,795 |
| sink 可达（按 (asm, n) 去重 = 候选行数） | 2,285 |
| 其中「其他（含待译句子）」= 真该翻的 | 1,669 |

> 「像 UI 文本」与「sink 可达」是同一批字面量上的两个不同视角，**不是嵌套关系**：sink 可达里既有 `OK` 这种 2 字符文本，也有 `%s` 这种纯格式串。

逐 mod（按候选 distinct 降序，只列前 25 个；`mod` 取 GameData 下第一段目录，`KSP` 指 Assembly-CSharp）：

| mod | DLL | #US | 过滤后 | ≥4 字符 | 候选 distinct |
|---|---|---|---|---|---|
| RP-1 | 4 | 3,048 | 2,919 | 2,818 | 357 |
| KSP | 1 | 18,457 | 18,096 | 17,730 | 257 |
| TUFX | 1 | 2,574 | 2,060 | 2,012 | 208 |
| Waterfall | 1 | 608 | 580 | 568 | 160 |
| MechJeb2 | 4 | 8,143 | 7,975 | 7,873 | 144 |
| Scatterer | 1 | 981 | 946 | 925 | 125 |
| RealFuels | 1 | 867 | 792 | 761 | 98 |
| TriggerTech | 2 | 1,403 | 1,316 | 1,286 | 95 |
| RCSBuildAid | 2 | 209 | 201 | 190 | 78 |
| RealAntennas | 1 | 394 | 374 | 352 | 64 |
| RealChute | 1 | 477 | 437 | 420 | 62 |
| ModularFlightPanel | 1 | 9,099 | 6,733 | 6,362 | 59 |
| ContractConfigurator | 3 | 1,968 | 1,911 | 1,807 | 50 |
| KSPPerformanceProfiler | 1 | 540 | 497 | 478 | 45 |
| KSPWheel | 1 | 306 | 294 | 271 | 42 |
| Principia | 1 | 1,080 | 1,006 | 967 | 40 |
| AtmosphereAutopilot | 2 | 276 | 254 | 229 | 37 |
| AdvancedPQSTools | 1 | 123 | 119 | 119 | 35 |
| TestFlight | 4 | 562 | 511 | 481 | 35 |
| 000_TexturesUnlimited | 2 | 502 | 456 | 443 | 33 |
| FerramAerospaceResearch | 3 | 1,016 | 965 | 925 | 29 |
| ParallaxContinued | 1 | 882 | 853 | 832 | 25 |
| Kopernicus | 3 | 1,446 | 1,377 | 1,327 | 21 |
| Skopos | 1 | 219 | 204 | 186 | 18 |
| SmokeScreen | 1 | 187 | 181 | 175 | 17 |
| （其余 20 个 mod） | - | - | - | - | 148 |

## 3 distinct 总数与去重口径

- `packs/il-candidates.jsonl` 每行一条候选，**同一 DLL 内同一 `n` 只留一条**（保留排序最前的 `(type, method, ilOffset)`），所以行数 = Σ per-DLL distinct。
- 跨 DLL 按 `n` 去重后 **2,082** 条；按 `h` 去重同值（`h` 是 `n` 的 FNV-1a64，与 `packs/pool.jsonl` 同一契约）。
- 排序：`asm` 升序 → `n` 升序 → `type` → `method` → `ilOffset`；`ilOffset` 是 `ldstr` 在方法体内的 IL 偏移，**十进制**。
- 落盘：UTF-8 无 BOM、LF、紧凑 JSON（`separators=(",",":")`）；同一输入跑两遍逐字节相同。

## 4 逐 mod top-20 明细

| mod | 候选 distinct | 样例 1 | 样例 2 | 样例 3 |
|---|---|---|---|---|
| RP-1 | 357 | * | + | - |
| KSP | 257 | ! | ( | + |
| TUFX | 208 | + | - | < |
| Waterfall | 160 | 0 | A | B |
| MechJeb2 | 144 | " | % | ' |
| Scatterer | 125 | < | > | B |
| RealFuels | 98 | % | + | - |
| TriggerTech | 95 | * | + | : |
| RCSBuildAid | 78 | R | s | -- |
| RealAntennas | 64 | Alt | Lat | Lon |
| RealChute | 62 | X | No | --- |
| ModularFlightPanel | 59 | « | » | ▲ |
| ContractConfigurator | 50 | + | - | No |
| KSPPerformanceProfiler | 45 | - | ▼ | ◆ |
| KSPWheel | 42 | < | > | fX |
| Principia | 40 | + | 0 | × |
| AtmosphereAutopilot | 37 | x | ?? | FPA |
| AdvancedPQSTools | 35 | Depth | hold: | Far Clip |
| TestFlight | 35 | X | ✔ | Next |
| 000_TexturesUnlimited | 33 | < | > | Main |

## 5 噪声分类

对 **2,082** 条 distinct 候选按互斥优先级归类（顺序即优先级）：

| 类别 | 条数 | 占比 |
|---|---|---|
| 键引用 | 21 | 1.01% |
| URL | 0 | 0.00% |
| 路径 | 7 | 0.34% |
| XML/富文本 | 255 | 12.25% |
| 纯数字/单位 | 20 | 0.96% |
| 标识符 | 96 | 4.61% |
| 日志/错误串 | 14 | 0.67% |
| 其他（含待译句子） | 1,669 | 80.16% |
| **合计** | **2,082** | **100.00%** |

判据：`键引用` = 以 `#` 开头（`#autoLOC_*` / `#LOC_*`，运行时由字典解析，不该翻）；`URL` = 含协议头或 `www.`；`路径` = 含反斜杠 / 资源扩展名 / ≥3 段斜杠路径；`XML/富文本` = 有标签或实体；`纯数字/单位` = `textnorm.is_pure_number`；`标识符` = 无空格的 `A-Za-z0-9_.` 且含 `_`/`.` 或全大写/全小写；`日志/错误串` = 含 exception / error / failed / warning 等词。

## 6 格式串占比

占位符判据用 `textnorm.features(n)['ph']`（`<<n>>` / `{n}` / `%s|%d|%i|%f`）：**154 / 2,082 = 7.40%** 的候选含占位符。这些必须保留占位符原样翻译，不能整句机翻（`{0}` 被翻掉运行时会抛 `FormatException`）。

## 7 与 packs/pool.jsonl 的 h 交集

- `packs/pool.jsonl` 12,792 行、h 集合 12,792 个；候选里 **65** 条 distinct 文本的 h 已在池中（3.12%）——这些是 cfg 与 IL 两路都采到的同一文本，翻译时按 h 去重即可。
- 剩下 2,017 条是 IL 独有的新增量（其中「其他」类 1,669 条）。

## 8 翻译成本估算

按现网实测口径 ~71 prompt + ~33 completion tokens/条（`tools/i18n_tools/translate.py` 的实测均值）：

| 口径 | 条数 | prompt tokens | completion tokens | 合计 |
|---|---|---|---|---|
| 全部 sink 可达 distinct | 2,082 | 147,822 | 68,706 | 216,528 |
| 只翻「其他（含待译句子）」子集 | 1,669 | 118,499 | 55,077 | 173,576 |
| 再扣掉与 pool 交集 | 1,612 | 114,452 | 53,196 | 167,648 |

> 单价随模型变化，这里只给 token 量；成本 = tokens × 当前单价。含占位符的文本会略微抬高 completion（要保住占位符），未单独加系数。

## 9 文本承载 API 调用点普查

同一趟扫描顺带统计（`call` / `callvirt` / `newobj` 计数，含 mod 自己的重载）：

### 9.1 API × DLL 矩阵

| 组 | API（族） | 用到它的 DLL 数 | 调用点 | 去重方法数 | 流入字面量 distinct |
|---|---|---|---|---|---|
| A | GUILayout.DoLabel | 44 | 3,324 | 568 | 1,053 |
| A | TMPro.TMP_Text.set_text | 18 | 1,549 | 709 | 85 |
| A | GUILayout.DoButton | 40 | 1,325 | 398 | 443 |
| A | GUILayout.DoToggle | 34 | 500 | 179 | 212 |
| A | GUIContent..ctor | 27 | 424 | 186 | 181 |
| A | UnityEngine.UI.Text.set_text | 5 | 311 | 151 | 62 |
| A | GUI.Label | 21 | 204 | 102 | 79 |
| A | GUI.Button | 21 | 176 | 90 | 43 |
| A | GUILayout.DoWindow | 30 | 72 | 57 | 19 |
| A | GUI.Box | 14 | 55 | 33 | 3 |
| A | GUI.Window | 16 | 37 | 30 | 9 |
| A | ScreenMessages.PostMessage | 1 | 11 | 11 | 0 |
| A | GUI.Toggle | 7 | 9 | 8 | 0 |
| A | GUILayout.DoRepeatButton | 3 | 5 | 4 | 2 |
| A | GUI.RepeatButton | 1 | 2 | 2 | 2 |
| B | TMPro.TMP_InputField.set_text | 7 | 176 | 117 | 4 |
| B | TMPro.TMP_Text.SetText | 7 | 26 | 26 | 4 |
| B | UnityEngine.GUIContent.set_text | 2 | 13 | 7 | 5 |
| B | UnityEngine.UI.InputField.set_text | 2 | 9 | 7 | 0 |
| B | GUIStyle.Draw | 2 | 7 | 3 | 0 |
| B | UnityEngine.TextMesh.set_text | 2 | 6 | 6 | 2 |
| B | TMPro.TMP_Text.SetCharArray | 1 | 3 | 3 | 0 |
| REFL | System.Reflection.*.SetValue（反射写字段） | 32 | 338 | 236 | 0 |
| LOG | UnityEngine.Debug.Log*（对照：日志，非 UI） | 81 | 4,350 | 2,755 | 0 |

组说明：A = 已覆盖（PatchRegistry 的 16 个 + `GUIContent..ctor`）；B = 疑似盲区；REFL = 反射写字段；LOG = 日志（对照，不是 UI）。

### 9.2 盲区清单（B 组里调用点 > 0 的，逐 mod）


#### TMPro.TMP_InputField.set_text

| mod | DLL | 调用点 | 该 DLL 上这条路径的字面量 distinct | 样例 |
|---|---|---|---|---|
| KSP | Assembly-CSharp.dll | 148 | 1 | 0 |
| KSPCommunityFixes | KSPCommunityFixes.dll | 14 | 1 | 0 |
| ConformalDecals | ConformalDecals.dll | 6 | 2 | {0:F1}; {0:x2}{1:x2}{2:x2} |
| ConformalDecals | Unity.TextMeshPro.dll | 3 | 0 |  |
| BurstPQS | BurstPQS.dll | 2 | 1 | 4096 |
| KSPTextureLoader | KSPTextureLoader.dll | 2 | 0 |  |
| RP-1 | RP0.dll | 1 | 0 |  |

该 API 合计：7 个 DLL、176 个调用点、4 条 distinct 字面量。

#### TMPro.TMP_Text.SetText

| mod | DLL | 调用点 | 该 DLL 上这条路径的字面量 distinct | 样例 |
|---|---|---|---|---|
| ConformalDecals | Unity.TextMeshPro.dll | 10 | 0 |  |
| KSP | Assembly-CSharp.dll | 9 | 3 | The count is <#0080ff>{0}</color>; The <#0050FF>count is: </col… |
| Kerbalism | Kerbalism.dll | 3 | 0 |  |
| FerramAerospaceResearch | FerramAerospaceResearch.dll | 1 | 0 |  |
| ProceduralFairings | ProceduralFairings.dll | 1 | 0 |  |
| RealAntennas | RealAntennas.dll | 1 | 1 | Signal (Tx/Rx) : {0:F0}%/{1:F0}%  -  {2} |
| Principia | ksp_plugin_adapter.dll | 1 | 0 |  |

该 API 合计：7 个 DLL、26 个调用点、4 条 distinct 字面量。

#### UnityEngine.GUIContent.set_text

| mod | DLL | 调用点 | 该 DLL 上这条路径的字面量 distinct | 样例 |
|---|---|---|---|---|
| RP-1 | RP0.dll | 9 | 3 | Apply (resize to fit); Additional tank volume:  |
| RealFuels | RealFuels.dll | 4 | 2 | Free; Owned |

该 API 合计：2 个 DLL、13 个调用点、5 条 distinct 字面量。

#### UnityEngine.UI.InputField.set_text

| mod | DLL | 调用点 | 该 DLL 上这条路径的字面量 distinct | 样例 |
|---|---|---|---|---|
| ModularFlightPanel | ModularFlightPanel.dll | 8 | 0 |  |
| KSP | Assembly-CSharp.dll | 1 | 0 |  |

该 API 合计：2 个 DLL、9 个调用点、0 条 distinct 字面量。

#### GUIStyle.Draw

| mod | DLL | 调用点 | 该 DLL 上这条路径的字面量 distinct | 样例 |
|---|---|---|---|---|
| TUFX | TUFX.dll | 5 | 0 |  |
| KSP | Assembly-CSharp.dll | 2 | 0 |  |

该 API 合计：2 个 DLL、7 个调用点、0 条 distinct 字面量。

#### UnityEngine.TextMesh.set_text

| mod | DLL | 调用点 | 该 DLL 上这条路径的字面量 distinct | 样例 |
|---|---|---|---|---|
| KSP | Assembly-CSharp.dll | 5 | 2 | !; @ |
| ProceduralParts | ProceduralParts.dll | 1 | 0 |  |

该 API 合计：2 个 DLL、6 个调用点、2 条 distinct 字面量。

#### TMPro.TMP_Text.SetCharArray

| mod | DLL | 调用点 | 该 DLL 上这条路径的字面量 distinct | 样例 |
|---|---|---|---|---|
| ConformalDecals | Unity.TextMeshPro.dll | 3 | 0 |  |

该 API 合计：1 个 DLL、3 个调用点、0 条 distinct 字面量。

### 9.3 反射写字段与日志对照

- `System.Reflection.*.SetValue（反射写字段）`：32 个 DLL、338 个调用点；**同方法里有 `text`/`m_text` 字面量**（即真的在写 UI 文本）的族数：0。这类写入我们的 patch 打不到（`PropertyInfo.SetValue`），属于真实盲区，但本轮只计数、不进候选。
- `UnityEngine.Debug.Log*`：4,350 个调用点（**不是 sink**，只作噪声规模对照——若把日志也当 UI 文本，误报量会是 sink 候选的 1.9 倍）。

### 9.4 加权覆盖率结论

- 按**调用点数**加权：A 组 8,004 / (A 8,004 + B 240 + 反射 0) = **97.09%**。
- 按**流入字面量 distinct** 加权：A 组 2,193 / (A 2,193 + B 15) = **99.32%**。
- 剩下的大头：`TMPro.TMP_InputField.set_text`（176 次）、`TMPro.TMP_Text.SetText`（26 次）、`UnityEngine.GUIContent.set_text`（13 次）。
- 判断：**16 个 sink 是「够用」而不是「通用」**。要往「任意 mod」走，按性价比依次补 `TMPro.TMP_InputField.set_text`、`TMPro.TMP_Text.SetText`、`UnityEngine.GUIContent.set_text`、`UnityEngine.UI.InputField.set_text`、`GUIStyle.Draw`、`UnityEngine.TextMesh.set_text`、`TMPro.TMP_Text.SetCharArray`。补法不改现有 sink 契约——它们同样是「改传进来的值」，只是目标方法多几个。

## 10 复现命令与产物校验

```
cd tools
python -m i18n_tools.ilstrings                    # 全量扫描 + 写 jsonl + 写报告
python -m i18n_tools.ilstrings --only KSPWheel     # 单 DLL 调试
python -m i18n_tools.selftest                     # 自检（含 IL 解码 / sink / 幂等断言）
```

- `packs/il-candidates.jsonl`：2,285 行 / 525,324 字节 / sha256 `5531f9f8ce1ebbc12b9187b64ec34885e47a67837101b40c6e171cc4a9946575`
- 幂等：`packs/il-candidates.jsonl` 同一输入连续跑两遍逐字节相同（selftest 有断言）；本报告里的「耗时」是当次实测值，所以 md 每次跑不完全逐字节相同。

## 11 已知限制

1. **不跨方法**：`GetTitle()` 把字面量返回给调用方再进 sink 的情况抓不到。
2. **不跟字段**：`private static string Title = "..."` 再 `GUI.Label(r, Title)` 抓不到（规则明确不允许经静态字段）。
3. **不解析异常处理子句**：`try/catch` 的 handler 内代码不参与数据流（正常路径照常）。
4. **重载匹配靠参数类型**：`params object[]` 形式的调用只能看到数组；`string.Format("{0}", x)` 能拿到格式串，`Concat(new[]{a,b})` 的非常量部分会丢。
5. **反射写字段只做启发式计数**（同方法里有 `text`/`m_text` 字面量），不进候选。
6. **跨程序集继承链不展开**：`class MyText : TMPro.TMP_Text` 的 `set_text` 调用点按 MemberRef 的声明类型匹配，若编译器把声明类型写成子类就会漏（实测未出现）。


<!-- ILTRANS:BEGIN -->

## 分桶与可翻译子集（task-32）

这一节由 tools/i18n_tools/iltranslatable.py 生成（标记块内每次运行幂等替换）。
上游产物：packs/il-translatable.jsonl（只收 bucket != never 的行）。

### 12.1 扩展采集：四类缺口各捞回多少

一条命中可能需要不止一个扩展（例：KSPWheel 的 `Basic Options` 既要 X2 追进
属性 getter，又要 X4 认 DialogGUI* 构造函数）。下表「涉及」= need 集合含该扩展的
distinct 文本数，「仅靠它」= need 集合恰好只有它的文本数（互斥、可加）。

| 来源 | 说明 | 涉及 distinct | 其中 never | 仅靠它才抓到 | 其中新增（不在基线 2285 行） |
| --- | --- | --- | --- | --- | --- |
| base | task-29 原有路径（ldstr 直接/拼接/Format/GUIContent + 16 个 sink） | 2090 | 572 | 2072 | 8 |
| x1 | X1 sink 补齐：ScreenMessages.PostScreenMessage(string, float) | 89 | 16 | 88 | 88 |
| x2 | X2 返回值字面量：属性 getter / 单 return 方法（含同族 override） | 41 | 5 | 19 | 19 |
| x3 | X3 静态字段初值：ldstr X; stsfld F 后 ldsfld | 5 | 1 | 1 | 1 |
| x4 | X4 KSP 对话框框架：DialogGUI* 构造函数（文本承载对象，不在 16 sink 里） | 231 | 84 | 195 | 195 |

索引规模：返回字面量的方法 **1333** 个、静态字段初值 **1729** 个、类型基类关系 **19570** 条；阶段 A 建索引 1.48s，阶段 B 扫 sink 23.21s，合计 24.70s，扫描 131 个程序集。

四条真串的当前归属（task-32 的目标是让前四条进候选）：

| 串 | 分桶 | 命中 sink | 来源 |
| --- | --- | --- | --- |
| Vessel mass | translate / sentence | GUILayout.DoToggle | x2 |
| Basic Options | translate / sentence | KSP.DialogGUIBox..ctor | x2, x4 |
| Damage Options | translate / sentence | KSP.DialogGUIBox..ctor | x2, x4 |
| All Input Locks Cleared | translate / sentence | ScreenMessages.PostScreenMessage | x1 |
| Debug Toolbar | translate / sentence | GUILayout.DoWindow | base |
| Reload Settings | （静态采集仍抓不到） | - | - |

### 12.2 四档计数（distinct 归一化文本）

| 档 | 条数 | 占比 | 含义 |
| --- | --- | --- | --- |
| translate | 1145 | 47.41% | 有实义的句子/短语，直接翻 |
| short_label | 429 | 17.76% | 单字/单词但有实义（Back / Close / Warp） |
| template | 172 | 7.12% | 含数字或占位符的模板/进度串，翻不翻交下游决定 |
| never | 669 | 27.70% | 不该翻：键引用/路径/URL/标识符/纯符号/纯数字/已是中文/缩写标记 |
| 合计 | 2415 | 100% | - |

把范围收窄到 task-29 的基线 2285 条（h 在 packs/il-candidates.jsonl 里）时，本节口径的分布是：translate 978、short_label 382、template 156、never 566（合计 2082）。

与 Lead 对那 2,082 条的独立拆分（sentence 1,243 / single_word 355 / too_short 175 / no_word 128 / no_letters 57 / identifier 49 / weak_words 48 / keyref 21 / tags_only 6）相比，本节差异有两处，都是口径更严：

1. 多出 already_cjk 一档：task-29 的漏斗只按 textnorm.has_cjk（CJK 统一表意字）过滤，全角标点/假名/装饰符混着的串漏了过来；buckets 的 CJK 范围更宽（含 U+2E80–9FFF / U+FF00–FFEF / U+3000–303F），这些串本来就不该翻。
2. 符号与数量串判得更狠（weak_words / no_letters），所以 never 的条数比 Lead 的 484 多；另外本节把「含数字/占位符的模板串」单独成 template 档（Lead 的拆分里它们散在 sentence 与 single_word 里），这是分档方式不同，不是判定不同。

### 12.3 reason 明细

| reason | 条数 | 档 |
| --- | --- | --- |
| keyref | 92 | never |
| path | 8 | never |
| already_cjk | 144 | never |
| tags_only | 38 | never |
| no_letters | 91 | never |
| format_only | 36 | never |
| identifier | 82 | never |
| too_short | 66 | never |
| no_word | 50 | never |
| weak_words | 62 | never |
| template_progress | 75 | template |
| template_format | 97 | template |
| single_word | 429 | short_label |
| sentence | 1145 | translate |

### 12.4 逐档样例

**translate**（1145 条）

- (part not purchased)
- (settings with a * are global and not cfgPoint dependent)
- (site reconstructing)
- * Game requires restart for changes to take effect *
- <b><color=orange>KSPCommunityFixes warning</color></b>
- <b>Effect Modifiers</b>

**short_label**（429 条）

- (autogenerated)
- <b>Configure :</b> <color=#B4B4B4></color>
- <b>Controller</b>
- <b>High:</b>
- <b>Low:</b>
- <b>Mode</b>

**template**（172 条）

- 1.9+ Directx11 flickering shadows fix (recommended for 1.9, 1.10)
- 16 bit indices
- 32 bit indices
- 6-DOF Camera Control:
- 6-DOF Device:
- 6-DOF Flight Control:

**never**（669 条）

- !
- "
- #
- # of Engines:
- #autoLOC_148071
- #autoLOC_148129

### 12.5 与既有数据集的交集

| 集合 | 条数 |
| --- | --- |
| il-translatable.jsonl 行数（bucket != never） | 1746 |
| 其中 translate | 1145 |
| 其中 short_label | 429 |
| 其中 template | 172 |
| 与 packs/pool.jsonl 的 h 交集 | 67 |
| 与 task-29 il-candidates.jsonl 的 h 交集 | 1516 |

### 12.6 复现

    python -m i18n_tools.iltranslatable

产物 sha256：packs/il-translatable.jsonl = 9261933E033262DBF762D51B4AFA4543305D00FAAD286FC2BA057C66384CF8FD（1746 行 / 539514 字节）。

X2/X3/X4 都是「静态侧补洞」：X4 认的是 KSP 自己的 DialogGUI* 对话框框架，KSPWheel 的参数节点标题就是经它上屏的；运行时那 16 个 sink 覆盖不到构造函数，但 TMP_Text.set_text 会在下一帧兜住，所以这类串在 capture 里反而能看到。

基线漂移说明：packs/il-candidates.jsonl 是 task-29 的 2285 行历史产物，按任务要求保持不动；但本机安装里 Principia 的 ksp_plugin_adapter.dll 在 2026-10-23 13:11 被更新过，今天重跑 python -m i18n_tools.ilstrings 会得到 2286 行（多一条 Copy measured timing JSON...）。工具本身对同一份 DLL 仍然逐字节可复现（已实测两次相同）。

已知限制（与 task-29 同）：不做跨方法参数可达性、不做数组/字典、不做 ToString 生成、不做特性参数（KSPWheel 的 [CustomParameterUI(...)] 那类文本只在 CustomAttribute 表里）；运行时拼接的串（Reload Settings）静态侧永远抓不到，靠 capture_ingest。

<!-- ILTRANS:END -->

<!-- T32B:BEGIN -->
## 13 静态采集的三类不可达（task-32 追加）

这一节回答一个边界问题：把 sink 面从 16 个扩到 22 个、把数据流从「本方法」扩到「返回值/静态字段/同族 override/对话框构造」之后，
**还有哪些 UI 文本在原理上就进不了静态候选集**。结论先写：三类，都不值得为它继续扩采集，交给运行时捕获。

### 13.1 判据表

| 类 | 判据（怎么认定它属于这一类） | 检测手段 | 实例 | 值得扩采集吗 |
|---|---|---|---|---|
| A 枚举成员名 | 串只存在于元数据 `#Strings` 堆，`#US` 堆里没有 | 解析 PE 的流表 + 逐堆搜 | `Database` / `Performance` / `Console` | 否 |
| B 运行时拼接 | ldstr 形态（UTF-16LE）全 DLL 0 命中，但拼接片段存在 | 全 DLL 字节扫 + 片段定位 | `Reload Settings` | 否 |
| C 第三方 / 资源路径 | 串是 UrlDir 路径或第三方程序集内的资源名 | 池内 `path` 特征 + 捕获分桶 | `ReStockPlus/Parts/FuelTank/x` | 否 |

### 13.2 A 类：枚举成员名（判据 = 串在 `#Strings` 堆而非 `#US` 堆）

`Assembly-CSharp.dll` 的 `#US` 堆里共 18,457 条字面量，`Database` / `Performance` / `Console` **一条都不在里面**；
但三者都能在 `#Strings` 堆里按 `\x00Name\x00` 形态找到（对照组 `Debug Toolbar` 在 `#US` 里，所以它能被抓到）。
流表实测（偏移 + 大小）：

| 堆 | 偏移 | 大小 |
|---|---|---|
| #Strings | 8,555,408 | — |
| #GUID | 9,540,824 | — |
| #Blob | 9,540,840 | 437,444 |
| #US | 9,978,284 | 810,756 |

源码侧：`Assembly-CSharp.full.cs:237200` 是 `windowToolbarStrings = Enum.GetNames(typeof(WindowToolbar));`，
237201–237205 是另外五个同构赋值（DatabaseToolbar / ContractsToolbar / MissionsToolbar / KerbalToolbar / PhysicsToolbar），
237679 `GUILayout.Toolbar((int)windowToolbarIndex, windowToolbarStrings)` 把它们整体喂给 IMGUI 工具栏，239880 是 database 版。

枚举成员名共 **34 个**（六个工具栏枚举，源码行号）：`WindowToolbar` 236964–236977（Cheats/Physics/Debug/Database/Contracts/Missions/Kerbals/Gameplay/Resources/RnD/Performance，11）、
`PhysicsToolbar` 236979（5）、`DatabaseToolbar` 236996–237004（6）、`ContractsToolbar` 237006（5）、`MissionsToolbar` 237015（3）、`KerbalToolbar` 237022（4）。
调用面：`Enum.GetNames(` 39 处、`Enum.GetName(` 3 处。

为什么静态采集原理上抓不到：这些名字**从来没有被 ldstr 加载过**，它们是反射从元数据读出来的；任何只读 `#US` 堆的分析器都看不见。
要抓就得把「某个 enum 类型的所有成员名」当作候选，判据只能退化成「这个 enum 被 Enum.GetNames 引用过」——这是启发式，违反 task-32 的硬约束（不许「方法名像 UI 就收」）。
**判定：不值得。运行时捕获能直接拿到上屏后的字符串（本机 round 8 捕获里 `Database` / `Performance` 都出现了）。**

### 13.3 B 类：运行时拼接（判据 = ldstr 形态 0 命中 + 只有拼接片段命中）

`Reload Settings` 的 ldstr 形态（UTF-16LE）在 222 个 DLL / 77,079,032 字节里 **0 命中**。
但它并非完全无痕：UTF-8 形态在 `Assembly-CSharp.dll` 命中 1 处，偏移 **9,779,358**，落在 **#Blob 堆**（CustomAttribute 载荷），
上下文是 `... <OnLevelLoaded>d__5 \x00\x00\x14\x01\x00\x0fReload Settings\x00\x00\x12\x01\x00\rSave Settings`，
对应源码 `Assembly-CSharp.full.cs:181703` `[ContextMenu("Reload Settings")] public void ReloadSettings()`（:181710 是 `[ContextMenu("Save Settings")]`）。
ContextMenu 是 Unity 编辑器右键菜单，玩家侧不上屏 —— 所以**屏幕上那份只能来自拼接**：`"Reload "` 的 UTF-16LE 字面量在 5 个 DLL 里
（Assembly-CSharp / ModuleManager.4.2.3 / ContractConfigurator / Scatterer / TUFX）。

因此判据必须写成「**ldstr 形态 0 命中 + 只有 #Blob / 拼接片段命中**」，不能写成「任何形态 0 命中」（后者在 `Assembly-CSharp.dll` 上就是错的）。
**判定：不值得。** 拼接是可执行的语义，静态侧要覆盖它就得做常量传播 + 运行期才知道的分支，成本远超收益；运行时捕获天然拿最终串。

#### 13.3.1 同族但不同堆：特性参数（顺带记录）

KSPWheel.cs:7585–7594 用 `[CustomParameterUI("Wear and Damage", toolTip=...)]` / `[CustomFloatParameterUI("Global Friction Multiplier", ...)]` 提供参数面板文本。
这些串在 CustomAttribute 表（#Blob 载荷）里，不在方法体 IL 里，所以数据流分析也看不见 —— 与 B 类同源（都是「非 ldstr」）。
本轮不动；如果以后要捞，做法是解析 CustomAttribute 表并只收已知 UI 特性名，属于「白名单特性」而不是启发式，可以接受但优先级低。

### 13.4 C 类：第三方 / 资源路径

round 8 捕获里 578 / 2,610 条是路径（`ReStockPlus/Parts/FuelTank/x`、`e.g., Squad/Parts/Engine/solidBoosterRT5/model000`）。
这类串**不是待译文本**，capture_ingest 已按 path 规则判 never（见 §15.2）。
池内 `withPath` 特征为 0、`withUrl` 为 1，说明 cfg 抽取阶段就已经基本没有路径类文本；路径主要出现在运行时（模型/贴图加载日志被渲染进 IMGUI）。
**判定：不值得。** 路径不需要翻译，只需要不误翻。

### 13.5 小结

| 类 | 静态可达 | 建议 |
|---|---|---|
| A 枚举成员名 | 否（在 #Strings 堆） | 交给运行时捕获 |
| B 运行时拼接 / 特性载荷 | 否（无 ldstr） | 交给运行时捕获 |
| C 路径 | 可达但无意义 | 分桶判 never |

**结论：静态采集的天花板是 5/8（见 §14），剩下 3 条全部属于「原理不可达」。v1.1 的主数据源必须是运行时捕获，IL 静态扫描降级为离线辅助（用于在没有游戏运行时也能给出候选、以及给捕获结果补 sink/provenance）。**

## 14 八串验收表（T32 验收证据）

口径：round 7 截图里屏幕上确定保持英文、且能在 DLL 字节里找到的 8 个串，看它们在不在 `packs/il-translatable.jsonl`。
判定依据是产物里该 `n` 是否存在（`"mods"` 给出 DLL、`"sink"` 给出上屏路径、`"type"` 给出声明类型）。

| # | 串 | 是否命中 | asm | sink | 未命中的类 |
|---|---|---|---|---|---|
| 1 | Vessel mass | 命中 | RCSBuildAid.dll | GUILayout.DoToggle | — |
| 2 | All Input Locks Cleared | 命中 | ClickThroughBlocker.dll | ScreenMessages.PostScreenMessage | — |
| 3 | Basic Options | 命中 | KSPWheel.dll | KSP.DialogGUIBox..ctor | — |
| 4 | Damage Options | 命中 | KSPWheel.dll | KSP.DialogGUIBox..ctor | — |
| 5 | Debug Toolbar | 命中 | Assembly-CSharp.dll | GUILayout.DoWindow | — |
| 6 | Database | **未命中** | Assembly-CSharp.dll（仅 #Strings 堆） | GUILayout.Toolbar（数组来自 Enum.GetNames） | A 枚举成员名 |
| 7 | Performance | **未命中** | Assembly-CSharp.dll（仅 #Strings 堆） | GUILayout.Toolbar（同上） | A 枚举成员名 |
| 8 | Reload Settings | **未命中** | 无 DLL 含 ldstr 形态 | 运行时拼接 | B 运行时拼接 |

**合计 5/8**（task-29 的 `packs/il-candidates.jsonl` 是 **1/8**，只有 `Debug Toolbar` 命中）。
5 条命中里有 3 条（1/3/4）是靠本轮新增的 X2（返回值/属性 getter + 同族 override）与 X4（`DialogGUI*` 构造）才抓到的，
1 条（2）靠 X1（补 `ScreenMessages.PostScreenMessage`）。这 5 条全部落在 `bucket=translate`。

## 15 捕获 vs 静态的重叠度（v1.1 净增量）

### 15.1 输入

- 捕获文件：`D:\Kerbal Space Program_newmod\PluginData\UI18n\capture-20261023-060127.jsonl`，**753,067 字节 / 2,610 行 / 2,610 个 distinct h**（缺文件 0 / 坏行 0 / 坏记录 0 / h 口径不一致 0）。
- 对照集合：`packs/pool.jsonl` 12,792 行；`packs/zh-cn/*.jsonl` 41 包 9,990 行；`packs/il-translatable.jsonl` 1,746 行；`packs/il-candidates.jsonl` 2,285 行 / 2,082 distinct。

### 15.2 分桶与 core 校准的逐项差异

`python -m i18n_tools.capture_ingest --dir D:\Kerbal Space Program_newmod\PluginData\UI18n` 实测：
读入 2,610 → 去重 2,610 → 已被包覆盖 **0**（zh-cn 0 / pool 0）→ 分桶 **never 602 / template 1,930 / candidate 78** → 产物 2,008 行。

| 项 | 我（capture_ingest） | core 离线校准 | 差 | 解释 |
|---|---|---|---|---|
| path | 578 | 562 | +16 | 16 条带空格路径（`ROCapsules/PartConfigs/Apollo D2/D2Adapter/ROC-D2Adaptor`、`e.g., Squad/Parts/Engine/solidBoosterRT5/model000`）。我选择「段内允许空格/加号/逗号、段首尾不许空格」，宁可多判 path 少给翻译；要严格同口径就把段字符集里的空格去掉，会精确得 562 |
| already-cjk | 24 | （不在 never 里） | +24 | core 的 never=562 恰好等于严格路径数，说明那 24 条含 CJK 的串不在 core 的 never 里 |
| progress | 253 | 253 | 0 | 一致 |
| counter | 1,677 | 1,689 | -12 | 9 条来自带空格路径且含数字（被我先判 path），剩 3 条未定位，不硬凑 |
| candidate | 78 | 106 | -28 | = -16（多判 path）-24（多判 cjk）+12（少判 counter），自洽 |

### 15.3 产物

`packs/capture-candidates.jsonl` = **2,008 行 / 695,659 字节 / sha256 1E1CA1135939902C40579DEE023AA1FB9E68C7F473913040E3D7F4F100F87672**（连跑两遍逐字节相同）。
字段顺序 `{h,n,s,sink,caller,count,firstSeen,bucket,reason,mods}`，排序 `(bucket 序, h)`，无 BOM、纯 LF、末尾有换行。
sink 分布：TMPro.TMP_Text.set_text 1,988 / GUILayout.DoLabel 4 / GUI.Label 3 / GUI.Box 2 / GUILayout.DoRepeatButton 2 / GUILayout.DoToggle 2 / GUI.DoWindow(title) 2 / GUILayout.DoButton 1。
`mods` 全部为 `unknown`（round 8 的 `caller` 全是补丁名 `TMP_Text.TMPro.TMP_Text.set_text_Patch1` 这类，推断不出 mod；按契约不猜）。

### 15.4 交集表（净增量）

| 对照集合 | 大小 | ∩ 捕获（2,610） | ∩ 产物（2,008） |
|---|---|---|---|
| packs/pool.jsonl | 12,792 | **0** | 0 |
| packs/zh-cn/*.jsonl | 9,990 | **0** | 0 |
| packs/il-translatable.jsonl | 1,746 | 18 | 17 |
| packs/il-candidates.jsonl | 2,285 | 14 | 13 |
| 三者并集（pool ∪ zh-cn ∪ il-translatable） | 15,386 | **18** | 17 |
| 四者并集（再加 il-candidates） | 15,947 | **18** | 17 |

→ **只有运行时捕获才看得到 2,592 / 2,610 = 99.31%**（满足 Lead 的 ≥2,000 预期）。
这个 99.31% 不是「捕获比静态强」，而是**数据源不同**：静态扫的是 DLL 里的字面量，捕获扫的是屏幕上真正被写进去的最终串，
所以它天然包含配置驱动（pool 已覆盖）、拼接、枚举名、第三方 mod 运行时生成的全部结果。两者是互补而非替代。

### 15.5 限制

1. `caller` 在 round 8 全部不可用（2,585 行是 `TMP_Text.TMPro.TMP_Text.set_text_Patch1`，其余是 `GUILayout.UnityEngine.GUILayout.DoLabel_Patch1`）→ `mods` 只能写 unknown；要拿到 mod 归属得让 Runtime 侧在捕获里记 Assembly 名。
2. `count` 是上屏次数，**不代表重要性**：`Zzz control beta` 的 count 16,966 是某个轮询 label 每帧刷新导致的。
3. 声明字段（`bucket`/`reason`/`template`）只在 round 9+ 的 10 字段文件里采信；round 8 的 7 字段文件全部自算（本文件即如此）。
4. 捕获只包含「本次运行跑到过」的路径 → 长尾（没进过的界面、没触发的分支）不在里面，这是采样而非穷举。
5. 与 core 校准的 counter -12 / candidate -28 中有 3 条未能逐条定位，已按「不硬凑」处理。

### 15.6 复现

```
python -m i18n_tools.capture_ingest --dir D:\Kerbal Space Program_newmod\PluginData\UI18n
python -m i18n_tools.capture_ingest --selftest
```

自测 30 项全绿；`python -m i18n_tools.selftest` 现为 **214 项全绿**（原 175 + capture_ingest 30 + 真实产物 9）。
<!-- T32B:END -->
