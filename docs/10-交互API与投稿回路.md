# 10 · 交互 API 标准与投稿回路（v1.6 设计，含待拍板项）

> 本文是**契约**，不是说明。两件事在这里被定死：
> ① 任何人（人或 agent）改译文只能走 `python -m i18n_tools.api`，JSONL 是产物、不是工作面；
> ② 玩家实例 → 投稿 → 自动翻译 → 按 mod 推送 → 玩家自选哪些 mod 用中文，这条回路的每一跳格式都写死。
> 落地状态见 §11。数字与命令以仓库实际产物为准，改动前先 `cd tools && python -m i18n_tools.api pack list`。

## 1. 为什么必须把「改 JSON」这条路封掉

过去 40 多个提交里，`packs/zh-cn/*.jsonl` 是直接被文本编辑器和 agent 手写的。代价都真的发生过：

| 事故 | 现象 | 手改为什么防不住 |
|---|---|---|
| 哈希漂移 | 改 `s` 之后忘了重算 `h`，运行期永远匹配不上 | 无字段联动校验 |
| 占位符丢失 | `<<1>>` 少一个，进游戏直接 `IndexOutOfRange` 崩 UI | 门禁在**生成**时才跑，手改绕过了生成 |
| 重复行 | 同一 `h` 在同一包里出现两次，最后一行赢，行为不确定 | 无唯一性约束 |
| 无审计 | 说不清哪条是模型译的、哪条是人改的、依据是什么 | 无写入口 = 无事件 |
| 并发覆盖 | 两个 agent 各读整文件再写回，后写的吃掉前一个 | 无 revision 校验 |
| 门禁绕过 | 报告说 PASS，实际文件被手改过 | 报告与文件不同源 |

所以本层的原则只有一句：**只承认带审计事件的写入。**

## 2. 资源模型

### 2.1 冻结的运行时 schema（不许为了新功能动它）

运行期字典行 = 6 个固定字段 + 1 个可选：

```text
{"h":"16 位小写 hex","n":"归一化原文","s":"原文","t":"译文","mod":"modId","ctx":"上下文（可选）","p":true（可选，骨架 pattern）}
```

- `h = fnv1a64(normalize(s))`，唯一权威实现 `tools/i18n_tools/fnv.py`（C# 侧 `src/UI18n.Core/Hashing/Fnv1a.cs`，两边逐字节同结果）。
- 键层级由字段推出，不写进文件：有 `ctx` → ctx 层；否则有 `mod` → site 层；否则 global 层（`src/UI18n.Core/Dict/KeyLevel.cs`）。
- `mod` = **modId**，见 §8 注册表；包文件名 `<modId>[.<channel>].jsonl`（`channel` ∈ `il` / `cfg` / 空）。
- `p` 有**两种等值表示**：现行产物写整数 `"p":1`（`tools/i18n_tools/translate_il.py`，C# 侧 `JsonLineReader.IsPatternFlag` 按原始字符串判），契约的规范形式是布尔 `"p":true`。**读取一律接受 `true` / `1` / `"1"` / `"true"`，新写入一律布尔**；存量旧表示由 `api validate` 报 notice（不是 error），`api apply --normalize-p` 走 put-batch 一笔事务归一（幂等，只改表示不改 `h/s/t/mod`）。

### 2.2 资源

| 资源 | 落盘 | 写权限 | 说明 |
|---|---|---|---|
| pack | `packs/<lang>/<modId>[.<ch>].jsonl` | 只有 api | 译文主载体，按 mod 分文件 |
| override | `packs/overrides.jsonl` | 只有 api | `{en,zh,note}`；人工译文，优先于模型 |
| glossary | `packs/glossary.jsonl` | 只有 api | `{key,en,zh,short}`；G7 术语来源 |
| conventions | `packs/conventions.jsonl` | 只有 api | 12 条专有约定 |
| candidates | `packs/candidates.jsonl` | 只有 api / 采集器 | 待译清单（五通道合并），**只读消费** |
| mods | `packs/mods.json` | 只有 api | modId ↔ 程序集/文件夹/显示名/CKAN id |
| submission | `packs/submission/<id>/meta.json` + `<modId>.jsonl` + `index.json` | 只有 api | 玩家投稿（脱敏后，按 mod 拆） |
| journal | `packs/journal/<YYYY-MM-DD>.jsonl` | 只有 api，只追加 | 审计事件流 |
| index | `packs/index.json` | 只有 `api apply` | 由上面这些算出来的清单 |

`packs/candidates.jsonl`、`packs/pool.jsonl`、`packs/il-translatable.jsonl` 这类是**采集产物**，由 `i18n_tools.candidates` 等工具生成；它们也走 api 的 `apply` 收口，不允许手写。

## 3. 操作清单（v1 冻结）

入口：`python -m i18n_tools.api <verb> [flags]`，一律支持 `--json` 输出 envelope（默认给人看的文本，`--json` 给程序）。

### 3.1 读

> **本节的 flag 表按 `tools/i18n_tools/api.py` 的实现冻结**（2026-10-26 以 `api --selftest` 113 项为口径）。全局 flag `--json` / `--root <dir>` / `--actor <who>` 可以写在任意层级（`api --root X entry put …` 与 `api entry put … --root X` 等价）；写动词缺 `--actor` 一律 exit 2。
> `entry get` **没有 `--mod`**：`h` 是跨包键，`entry get --h <h> --all` 会列出所有提供该键的包。

```text
pack list        必需 —            可选 --lang --mod
pack show        必需 --mod       可选 --lang --channel il|cfg --limit N --grep TEXT
entry get        必需 —           可选 --h <h> --text "<原文>" --lang --all
entry search     必需 --text      可选 --lang --limit N
mod list         必需 —            可选 —
mod resolve      必需 --assembly <X.dll> 或 --pack <packId>（至少一个）
journal tail     必需 —            可选 --n N
validate         必需 —            可选 --lang --mod --strict --strict-p
```

### 3.2 写（全部要求 `--actor`，全部幂等）

```text
entry put        必需 --mod        可选 --lang --h <h> --text "<原文>" --s "<用于校验 h 的原文>"
                                   --t "<译文>" --ctx --p --note "<依据>" --expect-revision <rev> --dry-run
entry put-batch  必需 —（读 stdin） 可选 --lang --note --expect-revision --dry-run
                                   # NDJSON，一文件一笔事务：全过才写，任一不过整批不写
entry delete     必需 --mod --h    可选 --lang --reason "<why>" --expect-revision --dry-run
override put     必需 --en --zh    可选 --note --dry-run
glossary put     必需 --en --zh    可选 --short --dry-run
mod put          必需 --mod        可选 --display --assembly --folder --alias --ckan --note --dry-run
apply            必需 —            可选 --lang --normalize-p --dry-run
                                   # 重算 packs/index.json 与报告，跑全量门禁
submit accept    必需 --file <bundle.json>   # 口径见 §6：id 由内容派生、consent 必须为 true、按 mod 拆文件
submit list      必需 —           可选 --limit N
submit show      必需 --id        可选 —      # 不存在即 exit 6
```

`--p` 只给骨架行（含 `#` 占位符的模板文本）；**落盘写布尔 `true`**，读取容错 `true|1|"1"|"true"`，旧的 `p:1` 由 `validate` 报 notice、由 `apply --normalize-p` 一次性归一（幂等、走 put-batch 同一条 commit 路径、journal `op=apply.normalize-p`）。2026-10-26 真仓库归一 486 行 / 14 个 `.il.jsonl`（Assembly-CSharp 356、UI18n-Unattributed 251、KSPPerformanceProfiler 213、MechJeb2 169、TUFX 155、ModularFlightPanel 60、FAR 58、RealFuels 49、ksp_plugin_adapter 48、RealisticLaunchVisuals 34、RealAntennas 31、BurstPQS 15、LunarTransferPlanner 13、EVEManager 2），归一后 `validate --strict-p` 绿、97 文件 `h→(s,t,mod)` 逐条零差异。

### 3.3 envelope（`--json` 时的唯一输出形态）

```json
{"ok": true, "op": "entry.put", "lang": "zh-cn", "mod": "TUFX",
 "revision_before": "9f2c1a0b7d34@312", "revision_after": "1b7e55c0a2f9@313",
 "changed": [{"file": "packs/zh-cn/TUFX.il.jsonl", "h": "3ff885233413de88", "action": "insert"}],
 "gates": ["G2", "G4", "G6", "G7", "G8"], "warnings": [],
 "journal": "packs/journal/2026-10-26.jsonl#L412"}
```

失败时 `{"ok": false, "error": {"code": "...", "message": "...", "details": [...]}}`，`exit code` 见 §3.4。

### 3.4 退出码（程序靠它分流，不许改语义）

| code | 含义 |
|---|---|
| 0 | 成功（含 `--dry-run` 全部通过） |
| 2 | 用法错误 / 参数缺失 |
| 3 | revision 冲突（`revision_before` 不匹配）→ 重读再试 |
| 4 | 门禁拒绝（§4）→ 译文有问题，不是工具坏了 |
| 5 | schema 非法（字段缺失/多余、h 与 s 不符、packs 行坏） |
| 6 | 目标不存在（mod/包/条目） |
| 7 | 内部错误（IO、解析器自爆） |

## 4. 门禁（写入时逐条跑，和 CI 同口径，缺一不写）

沿用 `tools/i18n_tools/translate.py:796 gate()` 的语义，编号不变（**不许新编号**）：

| 编号 | 内容 | 出处 |
|---|---|---|
| G1 | 批级：模型未返回 / 批解析失败 | `translate.py`（批级判定） |
| G2 | KSP 占位符 `<<n>>` 多重集必须一致 | `masklib.count_placeholders` |
| G3 | 格式符 `{n}` / `%s` 多重集一致 | `masklib.count_formats` |
| G4 | TMP 富文本标签逐字节多重集一致（含 `<color=#RRGGBB>` 色值） | `masklib.count_rich_tags` |
| G5 | 哨兵 `<<M0>>` 多重集一致、无表外哨兵、还原后无残留 | `masklib.sentinel_multiset` |
| G6 | 长度比 0.15–1.8（源 < 9 字符或 `#` 开头的键引用不判） | `translate.py:847` |
| G7 | 术语表锁定译法逐字出现（12 条约定 + 短语级官方术语） | `translate.py` `locked_for` |
| G8 | 译文 == 原文 → 不写，记 `keep` | `translate.py` `echo_keep` |
| G9 | 骨架 pattern 的 `#` 个数一致（只对骨架行：`p:true` 或旧表示 `p:1`） | `translate_il.py:589` |

api 额外三条**自身**约束（不属于 G 编号，属于 schema）：

1. `h` 必须等于 `fnv1a64(normalize(s))`；传了 `--h` 又传 `--s` 且不符 → exit 5。
2. 同一包内 `h` 唯一；`entry put` 命中已存在行是**替换**，不是追加。
3. `t` 为空 → 视为删除该行并记事件（不许留空译文行）。

## 5. 并发、原子性、审计

- **revision** = `sha256(文件字节)[:12] + "@" + 行数`。写操作会带 `revision_before`；不匹配 → exit 3，并回传当前 revision。多 agent 并行时这条是唯一防覆盖手段。
- **原子写**：内存里改完 → 全量校验 → 写 `<file>.tmp` → `os.replace` → 追加 journal。journal 追加失败视为写失败（下一次写会因 revision 不同而要求重读，不会静默）。
- **journal 事件**（`packs/journal/<date>.jsonl`，一行一事件，只追加）：

```json
{"ts":"2026-10-26T18:04:11+08:00","actor":"llm:deepseek-chat","op":"entry.put",
 "lang":"zh-cn","mod":"TUFX","h":"3ff885233413de88","action":"insert",
 "file":"packs/zh-cn/TUFX.il.jsonl","revision_before":"9f2c1a0b7d34@312",
 "revision_after":"1b7e55c0a2f9@313","file_sha256_after":"…","bytes_after":31879,
 "gates":["G2","G4","G6","G7","G8"],"note":"术语表命中：Diffraction→衍射"}
```

- **CI 硬门禁 `journal_gate.py`**：一个提交只要动了 `packs/zh-cn/**`、`packs/overrides.jsonl`、`packs/glossary.jsonl`、`packs/mods.json`，就必须同时有 journal 事件，且**每个被改文件的 `file_sha256_after` 必须等于提交后文件的 sha256**。手改 JSON 的提交会在这里红掉，报错文案直接给出应该跑的那条 `api` 命令。
- **journal 只许追加**：`journal_gate` 同时校验「历史行未被改写」（比对被删/被改行的 hash），想伪造就得整段删日期文件——那也会红。

## 6. 玩家投稿契约（bundle）

> **本节 2026-10-26 冻结（M3 前置口径，m07488 四项问题的回答）。** 改这里任何一条都要走新的决策记录：bundle 是玩家端产物，一旦发出去就必须永远能收，不能靠「更新插件」来纠正格式。

### 6.1 bundle 形态 `ui18n-submission/1`（冻结）

```json
{"schema":"ui18n-submission/1",
 "id":"9f3c1a77b0e54d21",
 "createdAt":"2026-10-26T18:20:00+08:00",
 "language":"zh-cn",
 "actor":{"kind":"player","installId":"0123456789abcdef0123456789abcdef","ui18nVersion":"1.6.0","kspVersion":"1.12.5"},
 "consent":{"granted":true,"policyVersion":1,"grantedAt":"2026-10-26T18:19:12+08:00"},
 "mods":[{"modId":"FerramAerospaceResearch","fileName":"FerramAerospaceResearch.il.jsonl","version":"1.5.10"}],
 "entries":[{"h":"3ff885233413de88","s":"Aerodynamic Failure","mod":"FerramAerospaceResearch",
             "channel":"capture","sink":"TMPro.TMP_Text.set_text","caller":"…FARDebugAndSettings.debugWindow","count":37}],
 "redactions":{"paths":0,"userNames":0,"absolutePaths":0,"urls":0},  // 第五键 userNamesDropped 只在 >0 时出现
 "note":""}
```

**必填**：`schema`（必须逐字等于 `ui18n-submission/1`）、`id`、`createdAt`、`language`、`actor.installId`、`consent.granted`（必须为 `true`）、`entries`（≥1 行）。
**每行 `entries[]` 必填**：`h`（16 位小写 hex）、`s`（非空、≤ 8000 字符）、`mod`；`channel`/`sink`/`caller`/`count` 可选。
**约束**：`entries[].mod` ⊆ `mods[].modId`（出现未声明的 modId 直接拒收，防止 bundle 里夹带别的 mod 文本）。
**`actor.installId` 的形态**：uuid4 的 **32 位小写 hex，无横线**（`Guid.NewGuid().ToString` 的 `N` 格式）。首次生成后永不改（换机、换存档都不换），只用于本地「这批是不是我上次投的那批」去重；不进日志、不进 `meta.json`、不进仓库。
**`id` 的算法是硬的**：把 bundle 去掉 `id` 字段后 `json.dumps(sort_keys=True, separators=(',',':'), ensure_ascii=False)` → sha256 → 取前 16 位小写 hex。accept 时重算，不等就 exit 5 `submission-id-mismatch`（`id` 由内容派生，正常永远不会「同 id 不同内容」）。

只允许这四类数据：**mod 列表**、**可译原文**、**游戏语言与版本**、**（可选）调用点**。玩家的存档名 / 飞行器名 / Kerbal 名 / 目录名一律不出现。

### 6.2 同意怎么拿（客户端配置，不是 CLI flag）

- 默认 **不投稿**：`Config.cfg` 的 `SubmissionConsent = None`；玩家在游戏内点「同意」才写 `prefs.json` 的 `submission.consent.granted = true`（§10 规范形态）。
- 每次实际投稿**还要一次确认**：显示「本次将上传 N 个 mod 的 M 条原文」+「打开文件夹」按钮；bundle 永远先落盘到 `PluginData/UI18n/submissions/`，玩家能自己看、自己删。
- `api submit accept` **不提供任何跳过同意的 flag**：bundle 里 `consent.granted` 不是 `true` 就 exit 5。拒收无同意的数据是连服务端也能复核的硬线，不做「调试用例外」。
- 撤回：`SubmissionConsent` 改回 `None` + 删目录；仓库侧不保留可追溯到人的标识（`installId` 只用于本地去重，投稿时不进仓库——§6.4 落盘形态里不写 `actor`）。

### 6.3 三步脱敏（落盘前必做，顺序固定）

1. 丢掉已判定为非正文的档（`path` / `progress` / `counter` / `identifier` / `too-long`，判据同 `CaptureClassify`）；
2. 私有标识按**只替换值**做（不是整条丢 —— 正文本身有翻译价值）：命中存档名 `HighLogic.SaveFolder` / 飞行器名 / Kerbal 名 / 玩家目录名 → 值替换成 `<save>` / `<vessel>` / `<kerbal>`；**替换后整串再无可见字母**（只剩占位符、数字、标点）→ 整条丢；
3. 单次会话只出现 1 次且长度 > 120 的串默认不投（多是调试/日志噪声），同意界面可手动放开。

脱敏计数写进 bundle 的 `redactions`，accept 时原样抄进 meta（可审计：这条串是「因为像路径被丢」还是「本来就没有」）。计数分**两类**：`replaced`（值换成占位符、正文留下）与 `dropped`（整条丢）。只记一个总数就分不清「玩家的话被保留了多少」，也分不清脱敏规则是不是太激进。落地形状：`paths` / `userNames` / `absolutePaths` / `urls` 四键恒写，`userNamesDropped` **只在 >0 时写** —— 没有私有值的 bundle 字节完全不变，canonical id 不会因为「多一个 0」而漂移；服务端缺键按 0 读。

### 6.4 落盘形态：按 mod 拆文件

```text
packs/submission/<id>/meta.json          # 规范化后的 bundle 头（去掉 entries；不含 actor.installId）
packs/submission/<id>/<modId>.jsonl      # 一行一原文：{h,s,mod,channel?,sink?,caller?,src?,firstSeen?}
packs/submission/index.json              # 受管汇总：id → {files, rows, bytes, sha256, createdAt, language, policyVersion}
```

理由（三条，都要记住）：
- **按 mod 拆**：一个 mod 的稿件坏掉不牵连整批；将来回复投稿人也能按 mod 说话（「TUFX 的 34 条已进包」）。
- **`meta.json` 不带 `installId`**：`installId` 只为客户端本地去重存在，进仓库就变成伪标识符。
- **`index.json` 是受管文件**（与 `packs/zh-cn` 同级受 journal_gate 管），否则投稿目录可以手改而不留痕。

投稿目录**不是**语言包：`packs/submission/**` 里没有 `t` 字段，永远不进 `GameData`、不进 `packs/index.json`（后者只扫 `packs/<lang>/`）。

### 6.5 candidates 归并规则（不新建表）

投稿行并入已有的 `packs/candidates.jsonl`（12 列：`bucket/caller/channel/count/covered/firstSeen/h/mod/reason/s/sink/src`），**不新建 `submission-candidates.jsonl`**——五通道已经是一张表，第六条通道没有理由另开账：

| 情形 | 动作 |
|---|---|
| `h` 已被 `packs/zh-cn` 覆盖 | 仍写行，`covered: true` / `reason: "covered"`（现有语义不变），不产生翻译工作量 |
| `h` 已在 candidates 里 | **不重复插行**：`count` 累加、`sink`/`caller` 取首个非空、`src` 保留首次值、`channel` 保留首次发现它的通道 |
| 新 `h` | 插入，`channel: "submit"`、`src: "submission/<id>"`、`bucket` 按现有分类器给 |

排序口径不动（发现顺序 / 现有排序由工具负责），避免整表洗牌产生巨大假 diff。

**第六条通道是「回读」而不是「派生」——这一点必须在流水线里显式实现。** 前五条通道（cfg / loc / il / cfgcache / capture）每次跑 python -m i18n_tools.candidates 都从输入文件重新派生；而 submit 行只住在 packs/candidates.jsonl 自己里面（输入文件里没有它们）。所以：

- carry_rows()（tools/i18n_tools/candidates.py）在 collect 阶段回读上一版本表，**只带 channel == "submit" 的行**；其他通道的旧行故意不带（它们由输入重新派生，带过来只会让删掉的串复活）。
- 不回读 = 重跑一次 candidates 就把玩家投稿整批抹掉。这是 M3 落地前真实存在的静默丢数据事故，candidates --selftest 里有五条断言钉住（含一条反证：carry_path=None 时投稿行确实会丢）。
- SUBMIT_CHANNEL 已进 CHANNELS 与 CHANNEL_PRIORITY = {capture:0, submit:1, il:2, cfgcache:3, loc:4, cfg:5}：投稿行证据强度仅次于捕获端（同样来自「游戏里真的渲染过」，但只有 h/s，没有 sink/caller/count 细节），排在 IL 静态猜测之前。撞车时整行取优先级最高的那条，与原有语义一致。
- 报告第 2 节的表头如实写成「五条派生 + 一条回读」，submit 列给出实际回读行数。

### 6.6 退出码、幂等与冲突

`submit accept` 完全复用 §3.4 那一组，不新增码：

| 情形 | 退出码 | 说明 |
|---|---|---|
| 首次收下 | 0 | `changed` 列出 `packs/submission/<id>/*` 与 `candidates.jsonl` |
| 同 `id` 且内容 sha256 相同 | 0 | 幂等：一个文件都不写、**不写 journal 事件**（避免噪声），envelope 里 `"already": true` |
| 同 `id` 但内容不同 | 4 | `error.code = "submission-id-conflict"`，附双方 sha256 与 mods 差异摘要（理论上只在有人手改 id 时出现） |
| 缺字段 / 行 schema 坏 / `consent.granted != true` / `id` 与内容不符 | 5 | 一律 schema 非法，报第一条具体问题与字段路径 |
| `entries` 为空 / `mods` 与 `entries[].mod` 不自洽 | 4 | 门禁式拒绝（不是格式坏，是内容不合格） |
| 目标不存在（`submit show --id` 查不到） | 6 | |
| IO / 解析自爆 | 7 | |

**submission 不走 G1–G9**：它没有 `t`（译文），长度比、术语表、echo keep、骨架 `#` 数都无从判定。G 门禁只在 `entry put` 与翻译流水线出现。把 G 门禁套到 submission 上是把两种东西混为一谈。

### 6.7 journal 事件

受管范围扩到 `packs/submission/**`（含 `index.json`）。事件形状：

```json
{"at":"2026-10-26T18:41:02+08:00","op":"submit.accept","actor":"pipeline:submission",
 "submission_id":"9f3c1a77b0e54d21","bundle_sha256":"…","rows":37,"mods":["TUFX","FAR"],
 "files":[{"path":"packs/submission/9f3c…/TUFX.jsonl","rows":34,"file_sha256_after":"…"}],
 "candidates":{"changed":true,"file_sha256_after":"…"}}
```

### 6.8 三条已知边界（M3 的取舍，不是缺陷）

| 边界 | 现状 | 为什么这样定 |
|---|---|---|
| h 与 fnv1a64(normalize(s)) 重算不一致 | **只 warning，不拒收**（meta.json 的 warnings 里留一句「按投稿给的值收录」） | 客户端归一化是 C# 实现的，与 python textnorm.normalize 可能有细微差异（全角 / 零宽 / 组合符）。拒收的代价是「玩家真的看到的串被丢掉」，而收录一份可能对不上的行只是白占一行候选——两害取轻。若将来契约出现成批不一致，应在 screen_coverage 的 mismatch 计数上报警，而不是在这里拒收 |
| 同 id 并行 accept 无锁 | 单写者假设（本地或 CI 单进程） | 投稿的并发度天然是 1：一台玩家机器一次一份 bundle，CI 一次一个 issue。真并发由 git 冲突兜住（packs/submission/index.json 是同一个文件），不需要文件锁 |
| 同一条原文被多个 mod 声明 | 候选表按 h 去重只留一行，meta.json 记一条 warning | h 是内容哈希，同文同译；per-mod 归属留在 packs/submission/<id>/<modId>.jsonl 里，不丢信息 |

### 6.9 传输通道（v1：零服务器）

插件不持有任何凭据，所以 v1 是：**bundle 落盘 + 打开预填 GitHub Issue 页面**（`.github/ISSUE_TEMPLATE/submit-texts.yml`，玩家把 json 拖进去），仓库侧 CI 跑 `api submit accept`，收完在 issue 回一行「已收入 N 条，进入候选表」。

被否掉的备选：插件直连 GitHub API（要往客户端塞 token，等于公开泄露）、插件 POST 自建端点（要运维服务器，留作 §11 M4）。

**两端都已落盘**（2026-10-26）：

| 端 | 文件 | 干什么 |
|---|---|---|
| 玩家端 | 插件面板「投稿」区块（`src/UI18n.Runtime/UI/PrefsPanel.cs`）+ `SubmissionExporter` | 同意 → 导出 `PluginData/UI18n/submissions/ui18n-submission-<id>.json` → 「打开文件夹」/「打开投稿页面」 |
| 入口模板 | `.github/ISSUE_TEMPLATE/submit-texts.yml` | 要求拖入 json、填 UI18n 版本与环境、勾两条确认；附件在正文里是 `https://github.com/user-attachments/files/…` 链接 |
| 仓库端 | `tools/accept-submission-issue.ps1` | `-Issue <号>`：抽附件 URL → 下载 → 预检 schema → `api submit accept`（唯一判定者）→ `-Comment` 回一行「已收入 N 条」；没有 issue 时可 `-Bundle <本地文件>` 直收；`-DryRun` 全程不写盘 |

`accept-submission-issue.ps1` **不重复实现任何判定**：退出码就是 `api` 的退出码（0 收下 / 4 门禁 / 5 schema 非法 / 6 不存在 / 7 内部），因此可以原样塞进 CI。实测（`%TEMP%` 副本、`tools/i18n_tools/fixtures/submission-sample.json`）：`-DryRun` exit 0 且零写盘 → 真收 exit 0（5 文件 + journal `#L97`，candidates 77,265 → 77,268）→ 二次收 exit 0 `already:true`、`changed: []`。

## 7. 流水线：投稿 → 译文

```text
submit accept      →  packs/submission/<id>/meta.json + <modId>.jsonl + index.json（脱敏后留档）
                   →  candidates：新 h 插入（channel=submit）/ 已有 h 只累加 count（§6.5）
translate（LLM）    →  逐条过 §4 门禁 → api entry put --actor llm:<model>
override（人工）    →  api override put --actor human:<who>（优先于模型结果）
apply              →  packs/index.json + reports/*.md + 覆盖率三口径刷新
```

只有 `candidate` 桶进 LLM；`template` 桶要先经骨架通道（`p:true` + `#` 槽位，G9 管），`never` 桶不进。

## 8. mod 身份（玩家看到的「mod 名称」= 这里的 displayName）

`packs/mods.json`：

```json
{"schema":1,"mods":[
 {"modId":"FerramAerospaceResearch","displayName":"FAR","ckan":"FerramAerospaceResearch",
  "assemblies":["FerramAerospaceResearch.dll"],"folders":["FerramAerospaceResearch"],"aliases":["FAR"]}
]}
```

- `modId` 是**稳定键**（包文件名、`mod` 字段、开关状态都用它），一旦发布不许改名（改名 = 迁移脚本 + journal 事件）。
- `displayName` 是给玩家看的名字，可改。
- 解析优先级：程序集名 → 文件夹名 → 别名 → CKAN id。采集端（`CaptureCaller.CleanTypeName`）拿到的类型全名 → 程序集 → `modId` 就靠这张表；表里没有的新 mod 先落 `modId = 程序集名去扩展名`，并在报告里列「未登记 mod」，等 `session` 里人工补 displayName。

## 9. 分发：按 mod 推送（manifest v2）

现在 `stable/manifest.json` 只有整包 zip。v2 增加每 mod 一条。

**实际发出去的形状**（`build-update-package.ps1` 逐字节拼出来，97 个包聚合为 82 条 modId；15 个 modId 名下有两个 channel 文件，所以是 `files[]` 而不是单个 `file`）：

```json
{"readerContract":"ui18n-manifest-reader/2",
 "version":"1.6.0","channel":"beta","language":"zh-cn","kspVersion":"1.12.5",
 "zip":{"name":"UI18n-1.6.0-zh-cn-packs.zip","bytes":1408313,"sha256":"…"},
 "files":[{"path":"GameData/UI18n/Packs/zh-cn/TUFX.il.jsonl","bytes":32026,"sha256":"…","rows":155}],
 "mods":[{"modId":"TUFX","files":["GameData/UI18n/Packs/zh-cn/TUFX.il.jsonl"],
          "rows":155,"bytes":32026,"updatedAt":"2026-10-26T10:26:32Z"}]}
```

**`readerContract` 是形状协商键，不是装饰**：读取器只接受自己认识的那个值（`UpdateManifest.ReaderContract`），不认识就报 `unsupported-reader-contract`；清单不带这个键 = 旧清单，照旧解析（不因缺键拒绝）。

为什么需要它 —— 1.6.0 事故：出包脚本自 v1.2 起就写 `files[]` 聚合形态，而当时的读取器只认单文件 `file`，游戏里每份清单都被 `bad-mod-entry:0: missing-file` 整份拒掉，**联网更新从 v1.2 到 v1.6 一次都没生效**。两边的断言各在各的语言里（PowerShell 用 `ConvertFrom-Json` 读回自己写的 JSON；C# 用手写 fixture 喂自己），谁也不校验对方。现在三处闭合：

1. 出包脚本把契约写进清单，并在出包前用**运行时同一个 C# 读取器**真解一遍（`tools/manifest-roundtrip`，断言 6；`-SkipReaderCheck` 只能在机器没 SDK 时用，且这种产物不该发布）。
2. 发布包的 `release-checks.ps1` 第 12 条核对包内 `UI18n.Core.dll` 里有没有当前契约的能力标记 —— 挡住「出包脚本比 DLL 新」这种版本错位。
3. 读取器自己做协商，错位时给的是 `unsupported-reader-contract: <对方声明> (this build reads <本读取器>)`，不是 `missing-file`。

契约值只有一个真源（C# 的常量），两个 PowerShell 脚本都从源码里读它（`Get-ManifestReaderContract`），不各抄一份。改形状 = 提版本号 + 同步本节。

运行期语义：

1. 只下载 **玩家启用且本地缺失/过期** 的 mod 文件（v1 仍下整包，但只装启用的条目；v2 才做单文件下载）。
2. 装之前逐文件校验 sha256/bytes（`UpdateVerify` 已有）。
3. 「根据 mod 名称判断」= 用 `modId`（`displayName` 只用于界面展示），不对文件名做模糊匹配。

## 10. 运行时按 mod 开关

`PluginData/UI18n/prefs.json`（不是 `Config.cfg`：这是玩家状态，不该被 mod 更新覆盖）：

```json
{"schema":"ui18n-prefs/1","language":"zh-cn","defaultMode":"translated",
 "mods":{"FerramAerospaceResearch":{"enabled":true},"TUFX":{"enabled":false}},
 "submission":{"consent":{"granted":false,"grantedAt":"…"},"policyVersion":1,"installId":"0123456789abcdef0123456789abcdef","lastSubmittedAt":"…"}}
```

**规范形态就是上面这份**（`schema` 是字符串 `"ui18n-prefs/1"`，`submission.consent` 是对象 `{"granted": bool}`）。早期草案里的 `{"schema":1}` + `"consent":"granted"` **只读兼容**：实现会认，但写回一律用规范形态（`PrefsFile.Save` 只产出规范形态）。`submission.installId` 是客户端首次生成后**永不改**的本地去重键（32 位小写 hex），`grantedAt` 记同意时间；两者都只服务于本地审计，不进 bundle 之外的任何产物、不进日志。缺文件 = 全开；坏 JSON = 不抛、用默认值、文件不动；未知字段原样保留（向前兼容由后来者写的新键不会被旧版吞掉）。

语义（**必须在文档里写清，否则会被当 bug 报**）：

- 开关在**加载期过滤**：关掉的 mod 的包文件不进内存字典，运行期零额外开销（不进 per-call 路径）。
- 同一 `h` 出现在多个包里时，选择顺序：(1) ctx 层 > site 层 > global 层；(2) 同层按 `modId` 字典序。**因此「关掉 A」不等于「A 的这句话一定变回英文」**——如果 B 也提供同一句，仍显示中文。这是共享文本的固有语义，不是 bug。
- `UI18n-Unattributed` / `UI18n-ConfigCache` / `Squad` 作为 pseudo-mod 出现在列表里（显示名「未归属文本」「配置缓存」「本体」），也可单独关。
- 进游戏后第一次打开开关面板时，用 render 覆盖率的 `byCaller` 给出每个 mod 的命中/未命中数，让玩家知道关掉会损失什么。

## 11. 里程碑

| 里程碑 | 内容 | 验收 |
|---|---|---|
| M1（本轮） | `api.py` 的读 + `entry put/put-batch` + `validate` + journal + `journal_gate` CI + `mods.json` 引导 | `api --selftest` 绿；手改 JSON 的提交在 CI 红；`packs` 零实际译文变化（除 journal） |
| M2 | manifest v2（每 mod 一条）+ 只装启用 mod + `prefs.json` + 游戏内开关面板 | 装机后关掉一个 mod，该 mod 的串回到英文，其余不变 |
| M3 | 采集端 bundle + 同意 UI + Issue 模板 + `api submit accept`（契约见 §6，已冻结） | 一次真实 bundle → accept → `packs/submission/<id>/` 落盘 + candidates 行数/累加对得上 + `validate` 与 `journal_gate` 仍绿 |
| M4 | 中继端点（可选）自动开 PR | 端到端无人值守 |

## 12. 已拍板（2026-10-26，m07292「默认执行」：六条全部按默认值）

下表由「提议」转为「决定」。改动其中任何一条都要走新的决策记录，不要在实现里悄悄偏离。

| # | 问题 | 默认 |
|---|---|---|
| D1 | 投稿通道 | 零服务器：bundle 落盘 + 预填 Issue（M4 再谈中继） |
| D2 | 隐私边界 | 只投 mod 列表 + 可译原文 + 语言/版本；存档名/飞行器名/Kerbal 名/路径一律脱敏 —— 只替换值（保留正文），替换后整串再无可见字母才整条不投 |
| D3 | 开关粒度 | 按 `modId`（程序集→注册表映射），共享文本按 §10 语义，不承诺「关掉即全英文」 |
| D4 | 匿名与去重 | 不收集玩家标识；重复投稿只累加 `hits` |
| D5 | API 形态 | Python CLI + JSON envelope + CI journal 门禁；不上 HTTP（M4 视情况） |
| D6 | 手工修译文的入口 | `api entry put --actor human:*`（覆盖模型结果）或 `override put`；两者都要 journal |

**D3 的代价已知并接受**：按 `modId` 关包只能保证「这个 mod 不再提供译文」。若同一个 `h` 被别的包（`UI18n-Unattributed`、`Squad` 或另一个 mod）也提供，那句话仍然是中文。玩家问「我关掉了怎么还是中文」时，答复固定为这条语义，不要承诺调用点级屏蔽——那要求每个调用点带 mod 归属、字典体积与查表成本都会上去，v1.6 不做。

**因此游戏内面板的自述文案必须写清**：开关的作用是「不再从该 mod 的包里取译文」，不是「该 mod 的界面全部回到英文」。
