# 运行时捕获的日志行过滤（task-58）

结论：`capture_ingest` 现在在**骨架塌缩之前**就把 mod 自己打的 Debug 日志判成 `never/logline`，
重跑后交付产物 `packs/capture-candidates.jsonl` 从 951 行 / 436,517 B 降到 **900 行 / 406,204 B**，
少掉的**全是刷屏日志**（同输入 A/B：65 行、10,560 条实例、9,511 个正文字符），
一条可翻译的 UI 串都没丢（新-旧差集 = 0 行）。

## 1. 判据

| 位置 | 内容 |
| --- | --- |
| `tools/i18n_tools/capture_ingest.py:147` | `_LOGLINE_RE = re.compile(r"^\[(?:\d{1,2}:\d{2}:\d{2}|#:#:#)\]")` |
| `tools/i18n_tools/capture_ingest.py:168` | `_looks_logline(n)`：归一化后整串是否以日志时间戳开头 |
| `tools/i18n_tools/capture_ingest.py:192-193` | `classify_capture()` 里 `if _looks_logline(n): return ("never", "logline")` |
| `tools/i18n_tools/capture_ingest.py:112,114` | 原因码表新增 `logline`（`REASONS` 按短路优先级、`REASON_ORDER` 供报告展示） |
| `tools/i18n_tools/capture_ingest.py:1790` | `selftest_cases()` 新增 4 条 logline 断言 |

只认「**第一个 `]` 内是时钟**」，两种形态都算：

| 形态 | 例子 | 判定 |
| --- | --- | --- |
| 原始时间戳 | `[20:00:15][Debug] [AeroState] provider=Stock, M=0.000` | `never/logline` |
| 塌缩后的骨架（骨架把数字换成 `#`） | `[#:#:#][Debug] [Aero] x` | `never/logline` |
| 方括号开头但不是时钟 | `[Not a clock] Something` | 不判（走正常分类 → `candidate`） |
| 不含方括号的普通串 | `Loading texture assets...` | 不判（→ `candidate`） |

**为什么必须判在塌缩之前**：时钟 `01:06:42` 在骨架里是 3 个数字段（`[#:#:#]`），一行日志的数字段
很容易超过 `skeleton.py` 的 `MAX_SLOTS=8`，那类行会被判「塌不成骨架」→ 降级 `candidate` **原样落盘**
（下面 47 条 candidate 噪声就是这么来的，占 candidate 档的 11%）；判在塌缩之后，产物里已经是
聚合过的骨架行、还得反查 `exact` 才知道要不要删，不稳。两种形态都认，是因为回放骨架或重新摄入
已塌缩产物时会遇到 `[#:#:#]` 形态。

## 2. 同输入 A/B（隔离过滤本身的效果）

输入固定为同一批 30 个 `capture-*.jsonl`（116,466 行、去重 60,540 个 h，其中日志行 146 个 h，
被已发布包覆盖 0 条）。「加过滤前」= 把 `_looks_logline` 临时打回 `False` 重跑同一份输入，
其余参数完全相同（都不做 dll 反查，避免 mods 口径差异污染字节数）。

| 指标 | 加过滤前 | 加过滤后 | 差 |
| --- | ---: | ---: | ---: |
| 产物行数 | 965 | **900** | **-65** |
| 产物字节 | 447,958 | **407,524** | **-40,434** |
| `bucket=candidate` 行 | 473 | 426 | -47 |
| `bucket=pattern` 骨架 | 492 | 474 | -18 |
| 实例 `count` 合计 | 10,985,870 | 10,975,310 | -10,560 |
| 正文字符（`sum(len(n))`） | 47,504 | 37,993 | -9,511 |

- 被去掉的 65 行 = candidate 47 + pattern 18，**新增 0 行**（新-旧差集为空），即一档都没被误杀。
- `pattern` 侧 474 个幸存骨架的 `count` **一个都没变**（0 个骨架缩水）：146 个日志 h 要么整体落在
  这 18 个「纯日志骨架」里（99 个 h），要么塌不成骨架、以 candidate 落盘（47 个 h），不会和正常骨架混。

## 3. 交付产物前后（`packs/capture-candidates.jsonl`）

| | 行数 | 字节 | 分桶 | sha256 |
| --- | ---: | ---: | --- | --- |
| 重跑前 | 951 | 436,517 | candidate 459 + pattern 492 | `B9EA763DFE34F5E1…` |
| 重跑后 | **900** | **406,204** | candidate 426 + pattern 474 | `3F3CE12275F307AF5EB283782C0928C37EAC87CC4D98E1624D1B4AC8DA4FA0C4` |
| 差 | -51 | -30,313 | -33 / -18 | — |

说明：-51 行不等于 A/B 的 -65 行，因为这次输入比上一批多了一局捕获（task-45 记的是 29 个文件 /
114,123 行 / 58,513 个 h，本次是 30 个 / 116,466 行 / 60,540 个 h），新数据补回了 14 行。
**要隔离过滤效应请只看第 2 节的同输入 A/B。**

幂等：同一条命令连跑两遍，sha256 都是 `3F3CE122…`，逐字节相同。

## 4. 五条样例（被去掉的串）

| # | 串（截断） | 旧 bucket | 实例数 |
| ---: | --- | --- | ---: |
| 1 | `[01:06:42][Debug] [Transients] starts=0, separations=0, impacts=0, frostFragments=0, live=0, dropped=0` | candidate | 112 |
| 2 | `[#:#:#][Info] [Vapor] Continuous volumes=#, particle emitters=#, steps=#` | pattern | 1,104 |
| 3 | `[#:#:#][Debug] [VaporState] situation=ORBITING, eligible=False, source=#, fill=#, volumes=#, pad=False, overheadHitsRejected=#` | pattern | 288 |
| 4 | `[#:#:#][Info] [Aero] FAR optional API bound: telemetry=True, atmosphere=True, wind=True` | pattern | 220 |
| 5 | `[#:#:#][Warning] [Vapor] Nearby solar caster broadphase reached its finite capacity; some distant scenery may be omitted.` | pattern | 56 |

这 5 条全是 Vapor / Aero / Transients 的调试刷屏：玩家永远看不到它们，翻了也没有观感收益，
却各占一个骨架/候选位。

## 5. 去掉的 18 个骨架（旧产物 `bucket=pattern`，现在整条消失）

| 骨架 | 实例数 |
| --- | ---: |
| `[#:#:#][Info] [Vapor] Continuous volumes=#, particle emitters=#, steps=#` | 1,104 |
| `[#:#:#][Debug] [VaporCamera] name=ScattererReflectionProbeScaledSpaceCamera, mask=#, depth=None, path=DeferredShading, clip=#..#` | 572 |
| `[#:#:#][Debug] [VaporCamera] name=GalaxyCamera, mask=#, depth=None, path=Forward, clip=#..#` | 572 |
| `[#:#:#][Debug] [VaporCamera] name=Camera #, mask=#, depth=Depth, DepthNormals, MotionVectors, path=DeferredShading, clip=#..#` | 572 |
| `[#:#:#][Debug] [VaporCamera] name=markerCam, mask=#, depth=None, path=Forward, clip=#..#` | 572 |
| `[#:#:#][Debug] [VaporCamera] name=Reflection Probes Camera, mask=#, depth=None, path=DeferredShading, clip=#..#` | 572 |
| `[#:#:#][Debug] [VaporCamera] name=UIMainCamera, mask=#, depth=None, path=Forward, clip=-#..#` | 572 |
| `[#:#:#][Debug] [VaporCamera] name=UIVectorCamera, mask=#, depth=None, path=Forward, clip=#..#` | 572 |
| `[#:#:#][Debug] [VaporState] situation=ORBITING, eligible=False, source=#, fill=#, volumes=#, pad=False, overheadHitsRejected=#` | 288 |
| `[#:#:#][Debug] [VaporCamera] name=Camera ScaledSpace, mask=-#, depth=Depth, MotionVectors, path=DeferredShading, clip=#..#E+#` | 288 |
| `[#:#:#][Debug] [VaporCamera] name=Camera ScaledSpace, mask=#, depth=Depth, MotionVectors, path=DeferredShading, clip=#..#E+#` | 284 |
| `[#:#:#][Info] [Aero] Attached, enabled=True` | 220 |
| `[#:#:#][Info] [Aero] FAR optional API bound: telemetry=True, atmosphere=True, wind=True` | 220 |
| `[#:#:#][Info] [Transients] staging-transients-# enabled: accumulated #D mist, solid tracers, frost shock and surface wash.` | 220 |
| `[#:#:#][Debug] [VaporPad] anchor=(#, -#, #), supports=#, radius=#` | 148 |
| `[#:#:#][Debug] [VaporState] situation=PRELAUNCH, eligible=False, source=#, fill=#, volumes=#, pad=True, overheadHitsRejected=#` | 148 |
| `[#:#:#][Debug] [VaporState] situation=PRELAUNCH, eligible=True, source=#, fill=#, volumes=#, pad=True, overheadHitsRejected=#` | 136 |
| `[#:#:#][Warning] [Vapor] Nearby solar caster broadphase reached its finite capacity; some distant scenery may be omitted.` | 56 |
| **合计** | **7,116** |

## 6. 另外 47 条是从 `candidate` 档清掉的（塌不成骨架的长日志）

这些行的数字段超过 `MAX_SLOTS=8`（时钟就占 3 段），旧逻辑把它们当「模板塌缩失败」降级
**原样落盘**，于是几百条时间戳不同的日志各占一行。实例数合计 **3,444**，正文字符 **7,647**。

| 串（截断） | 实例数 |
| --- | ---: |
| `[01:06:42][Debug] [Transients] starts=0, separations=0, impacts=0, frostFragments=0, live=0, dropped=0` | 112 |
| `[17:00:37][Debug] [Transients] starts=0, separations=0, impacts=0, frostFragments=0, live=0, dropped=0` | 92 |
| `[17:00:49][Debug] [VaporGPU] pad, simSeconds=9.67, renderer=True, visible=True, layer=0, body=Center: (0.0, 0.0, 0.0), …` | 92 |
| `[17:00:49][Debug] [VaporGPU] tank=2718401181, situation=PRELAUNCH, velocity=(0.0, 0.0, 0.0), fill=1.000, simSeconds=9.67, …` | 92 |
| `[17:00:36][Debug] [VaporGPU] pad, simSeconds=0.00, renderer=False, visible=False, layer=0, body=Center: (0.0, 0.0, 0.0), …` | 92 |

（上表只是 count 最高的 5 条；47 条里绝大多数是同一条日志的不同时间戳/不同坐标变体。）

## 7. 误伤检查

| 检查 | 结果 |
| --- | --- |
| 以 `[` 开头但**不是**时钟的串（全部 30 个输入文件） | **1 条**，且是中文串 `[KSPPerformanceProfiler] 💡 堆内存垫高器就绪:…`，本来就被判 `never/already-cjk` |
| 新产物里残留的日志行 | **0** 行（逐行 `_looks_logline` 复查 900 行） |
| 新-旧行差集方向 | 只有删除、**没有新增**（0 行），说明没有串被误判成别的档 |
| 幸存的 474 个骨架 `count` | 无一个变化（日志行没混进可译骨架） |

## 8. selftest 新增的 4 条断言

位置：`tools/i18n_tools/capture_ingest.py` 的 `selftest_cases()`（分类断言区首 4 条）。
`python -m i18n_tools.selftest` 的 `[11]` 段直接遍历这个生成器，所以 4 条自动进总线：
总数从 282 项变 **286 项**。

| 断言 | 期望 |
| --- | --- |
| `classify: 真实时间戳日志行 -> never/logline` | `[20:00:15][Debug] [AeroState] provider=Stock, M=0.000, q=1.000` → `("never", "logline")` |
| `classify: 方括号但不是时钟 -> 不判 logline` | `[Not a clock] Something` → `("candidate", "")` |
| `classify: 塌缩后的 [#:#:#] 日志行也认 -> never/logline` | `[#:#:#][Debug] [Aero] x` → `("never", "logline")` |
| `classify: 正常句子不受 logline 影响 -> candidate` | `Loading texture assets...` → `("candidate", "")` |

## 9. 报告侧的落点（`reports/capture-triage.md`）

| 行 | 内容 |
| ---: | --- |
| :68 | `- reason 分布：logline 146 / path 1272 / already-cjk 24 / progress 8823 / counter 49478 / punct 1 / identifier 18 / 无 408` |
| :69 | **logline 单独一行记账**（条数、占比、样例、判据、为什么判在塌缩之前） |
| :204 | 分桶汇总表的 `bucket=never` 行补上「日志行」 |

这一行不是硬编码：`n_log = stats["reasons"].get("logline", 0)`，没有日志行时不打印。

## 10. 重跑命令

```
cd tools
python -m i18n_tools.capture_ingest --all 'D:\Kerbal Space Program_newmod\PluginData\UI18n' \
  --out '..\packs\capture-candidates.jsonl' \
  --report '..\reports\capture-triage.md' \
  --patterns '..\reports\capture-patterns.md'
```

对照跑（隔离过滤效应）的做法：同一个 `ingest()` 入口把模块级 `_looks_logline` 临时打回
`False` 再跑一遍，产物写到临时路径，其它参数与正式跑完全一致；两边按 `h` 求差集。
