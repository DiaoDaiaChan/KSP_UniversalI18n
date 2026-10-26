# 1.3 根因报告：为什么屏幕上的英文翻不出来

> 结论先行：**不是匹配算法不够聪明，是供给断了两处、且从来没有一个指标在盯着「屏幕上真正出现的文本」**。
> 运行期匹配层的全部技巧（剥装饰符 / 分段 / 词窗口 / 数字骨架）加起来只值 **0.2%**，
> 而字典对实测屏幕文本的覆盖率是 **0.45%**（可译范围内 3,129 条里只有 14 条；放到全部 60,379 条里是 0.02%）。
> 1.3 的修复方向因此不是「更聪明的匹配」，而是「把采集到的文本真正喂进翻译流水线」+「为数字模板开一条运行期通道」。

本报告的全部数字都可以用 §6 的命令重放。

---

## 0. 症状（用户侧观察）

用户在 FAR / EVE / RealAntennas 的实测里报告：

| 现象 | 例证 | 说明 |
| --- | --- | --- |
| 大量窗口标签仍是英文 | `Aerodynamic Failure`、`Atm Composition`、`Pathfinding`、`ShadowManager` | 这些串**全都在**捕获产物里（`bucket="candidate"`），只是从没被翻译 |
| 带数值的读数行仍是英文 | `Mass Flow: 2.2 ton/s`、`Voxels Debris: 12` | 这些串在 `bucket="template"`，运行期没有对应通道 |
| 部分零件名 / 描述变成了中文 | `Blue Paint => 蓝色涂装` | 这一层（配置文本）一直是好的 |

即：**能变中文的部分一直能变，用户真正盯着看的部分从来没进过字典。**

---

## 1. 取证

### 1.1 匹配层只值 0.2%（`_scratch/probe_layers.py`）

对 29 局实测捕获的 **58,192 条不同原文**，逐层尝试用现有字典（`packs/zh-cn/*.jsonl`，10,997 条有效条目）命中：

| 策略 | 命中数 | 占 58,192 |
| --- | --- | --- |
| exact（原文精确匹配） | **14** | 0.02% |
| affix-peel（剥前后装饰符） | 0 | 0% |
| segment（按分隔符切段匹配） | 1 | 0% |
| word-window（滑窗找最长命中子串） | 9 | 0.02% |
| skeleton（数字骨架） | 76 | 0.13% |
| 未命中 | 57,992 | 99.66% |

而且这 100 条命中里，**真正有意义的只有 exact 的 14 条**：

- skeleton 的 76 条全是 `#autoLOC_*` 形态的噪声（命中的是键名骨架，不是人话）；
- word-window 的 9 条是程序化拼出来的零件名（`RDR RDR 300 km` 之类）。

> **判定：匹配侧已经到顶。** 再投入剥符/分段/模糊匹配，收益上限是 0.2%，且每加一层就多一份误伤风险（把不该翻的色值、路径、标识符翻掉）。

### 1.2 采集通道从未接进出包（决定性）

| 环节 | 现状 | 证据 |
| --- | --- | --- |
| 采集（运行时） | 正常。29 局会话、114,123 行、坏行 0 | `PluginData/UI18n/capture-*.jsonl` |
| 归并（`capture_ingest`） | 正常。去重 **58,513** 个 hash，分桶 `never` 1,318 / `template` **56,632** / `candidate` **539** | `reports/capture-triage.md` |
| 入包产物 | **只到位一半**：仓库里的 `packs/capture-candidates.jsonl` 是 **v2（23 会话 / 49,658 行 / 19,636,794 B）**，最新一局（FAR/EVE 实测那局，`capture-20261026-025138.jsonl`，3,031 行）**从未被摄入** | `packs/capture-candidates.jsonl` 行数 |
| 翻译 | **从未发生**。`translate_il.py` 的 `--capture` 通道本来就写好了，但**从没跑过真数据** | §1.5 |
| 覆盖结果 | 58,513 条实测原文里，**只有 14 条**被运行期真正加载的字典覆盖（`packs/zh-cn`）；另有 10 条落在 `packs/pool.jsonl`，但 `src/UI18n.Runtime/ConfigLoader.cs:15` 明确规定「Packs 根下平铺的 jsonl 运行期永不读」，这 10 条**不算覆盖** ⇒ **0.02%**（可译范围内 0.45%） | `tools/i18n_tools/screen_coverage.py` |

上游其实留过一次明确的延后决策，但没人回头兑现。`reports/DELIVERY.md:446`：

> `| 捕获通道 packs/capture-candidates.jsonl（2,008 行） | 不翻，只交付工具 + 契约 + 一轮实证 | 等真实游玩会话的捕获数据；lead 探针那一轮含大量假串 |`

「等真实游玩会话」——现在真实会话有 **29 局**了，`packs/capture-candidates.jsonl` 也已经从 2,008 行长到 49,658 行，但那一句「不翻」没有人回来改。

### 1.3 字典的供给面是错的（不是数量不够，是种类不对）

`packs/zh-cn/*.jsonl`：**11,089 行 / 74 个 mod 文件**。

| 度量 | 值 | 含义 |
| --- | --- | --- |
| `s` 以 `#` 开头（autoLOC 键） | 22 行（0.2%） | 字典**不是**键表，是「英文原文 → 中文」的普通文本对 |
| `len(s) > 40` | **41.0%** | 大头是零件描述这类长文本 |
| mod 分布 | RP-1 3,382 / RealismOverhaul 1,775 / Squad 930 / SXT 526 / ModularLaunchPads 498 / TriggerTech 496 / ROCapsules 362 / ROTanks 320 / ROEngines 268 / RP0 244 | 集中在少数大 mod 的**配置文本** |

样例（`000_TexturesUnlimited`）：`Blue Paint => 蓝色涂装`、`White Paint => 白色涂装`、`Stock White => 原厂白色`。

> 也就是说：现有字典服务的是 **L0/L1（原生 key + 配置文本）**，而用户在屏幕上看到的 IMGUI / uGUI 标签是**硬编码在代码里的字符串**，两者在数据上完全不重叠。这不是「字典太小」，是「字典装的是另一种东西」。

### 1.4 模板档没有运行期通道

`template` 档占去重后的 **96.8%**（56,632 / 58,513）：这些串带数字（`Mass Flow: 2.2 ton/s`、`Total Run Time: 123s`、`Orbit: 85 km x 90 km`），逐字翻译等于要给每个可能数值都存一条，物理上不可能。

骨架化（数字 → `#`）后：**526 个唯一骨架**，其中含 ≥2 个英文词的 453 个。按出现次数排序的头部几乎全是噪声族（`Loading texture asset #/# (buffer=#MB)` 3,028 行、`ModuleManager: # patches applied` 系列合计 15,000+、Profiler HUD 各数百），真正有价值的低频骨架如：

- `Mass Flow: # ton/s`（434）、`Mass Flow: # kg/s`（73）
- `Flight Data: # du / # du`（94）、`Thrust Ratio: #`（46）
- `Orbit: # km x # km`（95）、`Aero: FAR | RH preset: # %`（62）
- `Total/Current Run Time: #s/#s`（39）

运行期没有任何「骨架查表 + 数字填回」的代码路径 —— 1.2 的 `Translator` 只做精确 hash 查表。

### 1.5 CI 的翻译 job 从来没有执行过一次

`.github/workflows/i18n.yml:87`：

```yaml
if: ${{ github.event_name == 'workflow_dispatch' && inputs.translate }}
```

`:92` 再要求 `secrets.UI18N_LLM_KEY` 存在。至今所有 run 都是 push 触发 → 该 job 一律 **skipped**（设计如此，但它意味着**流水线里「翻译」这一环在真实历史中执行次数 = 0**）。仓库里 `packs/zh-cn/*.jsonl` 的 11,089 条全部来自更早的、人工/半自动的批次，与捕获数据无关。

### 1.6 还有一个更上游的缺口：没有人在量「屏幕覆盖率」

`reports/coverage.md` 量的是 **pool 覆盖率**（9,073/12,792 = 70.93%）—— 分母是「我们从 cfg/Localization 里抽出来的池子」。这个指标在 1.2 是 70.93%，而同一时刻用户屏幕上能翻出来的比例接近 0。**指标与目标脱钩**，所以 1.2 能一路绿灯发布，用户却在游戏里看着英文。

> 这个缺口在 1.3 已补上：`tools/i18n_tools/screen_coverage.py`（只读，selftest 22/22）产出的 `reports/screen-coverage.md` 是第一个「分母 = 玩家真正看到的文本」的指标，带 `--fail-under` 可挂 CI 门禁，报告里附「未覆盖最多的调用点 top25」与「未覆盖高频文本 top10」。

---

## 2. 根因

| 编号 | 根因 | 性质 |
| --- | --- | --- |
| **R0** | 唯一的覆盖率指标（pool coverage）与用户体感（屏幕文本）无关，没有「屏幕覆盖率」这个指标 | 观测缺失 → 其它三条都能长期潜伏 |
| **R1** | 捕获 → 翻译 → 出包这一环**从未接通**（工具全在，命令从没执行；`DELIVERY.md:446` 的「不翻」延后决策无人回收） | 流程断裂 |
| **R2** | 字典供给面错位：11,089 条服务的是配置文本（L0/L1），硬编码 UI 串（L2 渲染层）0 覆盖 | 数据面错位 |
| **R3** | 数字模板（96.8% 的实测文本）没有运行期通道，骨架化在管道侧也没做 | 机制缺层 |

**不属于根因、且不应再投入的**：匹配算法。见 §1.1，上限 0.2%。

---

## 3. 1.3 的修复 ↔ 根因映射

| 根因 | 修复 | 落地位置 |
| --- | --- | --- |
| R1 | 把 `packs/capture-candidates.jsonl` 真正喂给 `translate_il --capture`；用 `packs/overrides.jsonl` 作人工通道（`translate_il.py:781` 在缓存与 API 调用之前短路，命中即不联网），先给「用户报错的那批真标签」写死译文，把闭环**跑通一次** | task-46 |
| R1（卫生） | `capture_ingest` 的模板行不再原样落盘，塌缩成骨架行（`bucket="pattern"`，每条附一个 `exact` 原样样本），产物从 56,632 行降到 526 行量级 | task-45 |
| R2 | 同上：先把 `candidate` 档里真 UI 文案（约 130~150 条）翻掉，让 L2 层第一次真正持有屏幕标签 | task-46 |
| R3 | 新增**骨架通道**：`tools/i18n_tools/skeleton.py`（已落盘，11/11 selftest）定义骨架语义；C# 侧 `UI18n.Core/Text/TextSkeleton.cs` + `DictPack.TryGetPattern` + `Translator` 精确未命中后的骨架兜底（`FillBack` 把数字填回） | task-44 |
| R3（门禁） | `translate_il` 新增 **G9**：译文骨架的 `#` 个数必须与原文一致（否则填回时数字会错位） | task-46 |
| R0 | **已落地**：`tools/i18n_tools/screen_coverage.py`（selftest 22/22）+ `reports/screen-coverage.md`。分母 = 实测会话里「该翻」的去重原文（当前 3,129；排除 `never` 桶与运行期标注的 path/url/progress/counter/identifier/too-long/punct），分子 = exact 命中（14）+ 骨架命中（当前 0），按捕获分桶 / 调用点 / 文本分列；`--fail-under` 可作发布门禁 | 已完成（1.3） |
| R1（采集卫生） | 每 caller 配额 `CaptureOptions.MaxPerCaller = 60`：1.2 的 `MaxEntries=5000` 被少数高频 caller（`ModuleManager` 日志、`LoadingScreen` 进度条）吃掉，同一局里真正的新 UI 标签挤不进来 | task-44 |

---

## 4. 预期效果与验证方法（诚实版）

1.3 **不是**覆盖率终点，是**通路第一次贯通**。定量预期：

| 指标 | 1.2 实测 | 1.3 目标 | **1.3 实测（出包后）** | 验证方式 |
| --- | --- | --- | --- | --- |
| 实测原文被字典覆盖（可译范围） | 14 / 3,129（0.45%） | ≥150 条精确 + 骨架通道带来的读数行 | **265 / 3,129（8.47%）**（精确 261 + 骨架 4） | `python -m i18n_tools.screen_coverage` |
| 屏幕覆盖率（全量口径） | 14 / 60,379（0.02%） | 同上，并用 `--fail-under` 挂 CI | **1,525 / 60,379（2.53%）** | 同上 |
| 用户报错的 22 个串 | 0 命中 | **全部**有中文 | **21 / 22 有中文**（`Sol-Visuals/Sol-Shadows` 按「mod 名不译」保留）；23 条探针串按 h 回查全部 EXACT | 字典回查 + 游戏内 A/B（round 11 待跑） |
| `template` 档产物行数 | 56,632 | ≤600（骨架行） | **492 个骨架行**（覆盖 55,609 实例），产物总行 860 | `packs/capture-candidates.jsonl` 行数 |
| 骨架运行期命中 | 无此路径 | `pattern: entries=N` 日志 + `PatternHits` 计数 | 代码与日志就位（`pattern: entries=102, enabled=True`），真游戏读数待 round 11 | `UI18n.log` |
| 匹配层投入 | 0.2% 上限 | 不再投入 | 未投入（匹配侧一行未改） | 本报告 §1.1 |

后续（1.4）才是覆盖率的主升段：骨架通道 + LLM 一次批量翻译（57,171 行候选全部可送）后，屏幕覆盖率才能从 0.04% 进到两位数。

---

## 5. 本轮不解决的问题（避免误读）

- **噪声筛不掉的部分仍然是噪声**：`ProfilerUI.DrawPluginsContent` 的插件类型名（`▶ <color=#FFFFFF>Kerbalism</color>`）不该翻，`ModuleManager: 1234 patches applied` 这种日志不该翻。1.3 靠规则筛，一定会有漏网的与误杀的。
- **模板的数字语义**：`Mass Flow: # ton/s` 填回后是 `质量流量：2.2 吨/秒`，中文量纲词由译者在骨架里写死，是可接受的近似，不是真单位换算。
- **三条机制不可达边界**（1.1 起就写在 README 里，本轮不变）：贴图内文字、预烘焙进 asset bundle 的 UI、运行期拼装但从不经过任何 hook 出口的字符串。
- **TMP 字体**：`GameData` 下无 CJK 字体，TMP 走 fallback 仍可能显示方块（1.3 未处理，属 L3）。

---

## 6. 复现命令

均在 `tools` 目录下执行（`python -m i18n_tools.*` 在仓库根会 `ModuleNotFoundError`）。

```powershell
# 1.1 匹配层上限
python ..\_scratch\probe_layers.py

# 1.2 归并（默认会覆写 packs\capture-candidates.jsonl，纯测量时务必显式 --out 到 _scratch）
python -m i18n_tools.capture_ingest --all 'D:\Kerbal Space Program_newmod\PluginData\UI18n' --out '..\_scratch\capture-candidates-v3.jsonl'

# 1.3 字典画像
python ..\_scratch\probe_cand.py

# 1.4 骨架
python -m i18n_tools.skeleton --selftest
python ..\_scratch\probe_skel.py

# 1.6 屏幕覆盖率（1.3 新增的唯一「分母 = 玩家看到的文本」指标）
python -m i18n_tools.screen_coverage --all 'D:/Kerbal Space Program_newmod/PluginData/UI18n' --packs ../packs/zh-cn --out ../reports/screen-coverage.md
```

## 7. 数字口径备忘

- 「58,513」= `capture_ingest` 对 29 局会话按 `fnv1a64(normalize(s))` 去重、再按长度 2 到 300 过滤后的 hash 数；`screen_coverage.py` 不做长度过滤，所以它看到的是 **60,379**（其中「该翻」的 3,129）。
- 「覆盖 14 条」而不是「24 条」：`capture_ingest` 的 covered 统计把 `packs/pool.jsonl` 也算进去了（+10 条），而 pool 运行期不加载，见 §1.2。
- 三桶之和 1,318 + 56,632 + 539 = 58,489，余 24 即「已被字典覆盖」；这 24 条以 `reason=already-cjk` 记入 `never` 桶，故 `never` 的 1,318 里包含它们。
- 「526 骨架」= 对 56,632 行模板跑 `skeleton.skeleton()` 去重；`skeleton()` 返回 `None`（0 个或 >8 个数字段）的行在 1.3 会**降级成 candidate**，不丢。
- 「用户报错的 22 个串」= 用户在 1.3 取证期贴出的 FAR / RealAntennas / Utils.GUIHelper / GUIDropDown 四组标签，共 22 条，全部在 `_scratch/capture-candidates-v3.jsonl` 的 candidate 桶里。
