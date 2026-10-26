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

### 2.2 资源

| 资源 | 落盘 | 写权限 | 说明 |
|---|---|---|---|
| pack | `packs/<lang>/<modId>[.<ch>].jsonl` | 只有 api | 译文主载体，按 mod 分文件 |
| override | `packs/overrides.jsonl` | 只有 api | `{en,zh,note}`；人工译文，优先于模型 |
| glossary | `packs/glossary.jsonl` | 只有 api | `{key,en,zh,short}`；G7 术语来源 |
| conventions | `packs/conventions.jsonl` | 只有 api | 12 条专有约定 |
| candidates | `packs/candidates.jsonl` | 只有 api / 采集器 | 待译清单（五通道合并），**只读消费** |
| mods | `packs/mods.json` | 只有 api | modId ↔ 程序集/文件夹/显示名/CKAN id |
| submission | `packs/submissions/<id>.json` | 只有 api | 玩家投稿原件（脱敏后） |
| journal | `packs/journal/<YYYY-MM-DD>.jsonl` | 只有 api，只追加 | 审计事件流 |
| index | `packs/index.json` | 只有 `api apply` | 由上面这些算出来的清单 |

`packs/candidates.jsonl`、`packs/pool.jsonl`、`packs/il-translatable.jsonl` 这类是**采集产物**，由 `i18n_tools.candidates` 等工具生成；它们也走 api 的 `apply` 收口，不允许手写。

## 3. 操作清单（v1 冻结）

入口：`python -m i18n_tools.api <verb> [flags]`，一律支持 `--json` 输出 envelope（默认给人看的文本，`--json` 给程序）。

### 3.1 读

```text
pack list        [--lang zh-cn] [--mod TUFX] [--json]
pack show        --mod TUFX [--lang zh-cn] [--channel il|cfg] [--limit N] [--grep TEXT]
entry get        (--h <h> | --text "<英文原文>") [--lang zh-cn] [--all]
entry search     --text "<片段>" [--lang zh-cn] [--limit 50]
mod list         [--json]
mod resolve      --assembly TUFX.dll | --pack TUFX.il
journal tail     [--n 20]
validate         [--lang zh-cn] [--mod TUFX] [--strict]
```

### 3.2 写（全部要求 `--actor`，全部幂等）

```text
entry put        --mod <modId> --lang zh-cn (--h <h> | --text "<英文>") --t "<译文>"
                 [--s "<原文，用于校验 h>"] [--ctx <ctx>] [--p]
                 [--actor llm:<model>|human:<who>|pipeline:<tool>] [--note "<依据>"]
                 [--expect-revision <rev>] [--dry-run]
entry put-batch  --stdin            # NDJSON，一文件一笔事务：全过才写，任一不过整批不写
entry delete     --mod <modId> --lang zh-cn --h <h> --actor … [--reason "<why>"]
override put     --en "<英文>" --zh "<中文>" --actor … [--note "<依据>"]
glossary put     --en "<英文>" --zh "<中文>" [--short] --actor …
mod put          --mod <modId> --display "<显示名>" [--assembly X.dll]… [--folder Studio]… [--ckan <ident>]
submission accept --file <bundle.json> --actor pipeline:submission
apply            [--lang zh-cn]      # 重算 packs/index.json 与报告，跑全量门禁
```

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
| G9 | 骨架 pattern 的 `#` 个数一致（只对 `p:true`） | `translate_il.py:589` |

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

### 6.1 采集什么（白名单制，写死在代码里）

```json
{"schema":"ui18n-submission/1","createdAt":"2026-10-26T18:20:00+08:00",
 "game":{"ksp":"1.12.5","unity":"2019.4.18f1","language":"zh-cn","ui18n":"1.6.0"},
 "consent":{"grantedAt":"2026-10-26T18:19:12+08:00","scope":"modlist+texts","policyVersion":1},
 "mods":[{"modId":"FerramAerospaceResearch","displayName":"FAR","assembly":"FerramAerospaceResearch.dll","version":"1.5.10","packKnown":true}],
 "texts":[{"h":"…","s":"Aerodynamic Failure","modId":"FerramAerospaceResearch","caller":"…debugWindow","hits":37}]}
```

只允许这四类：**mod 列表**、**可译原文**、**游戏/Unity 语言与版本**、**（可选）调用点**。落盘前做三步脱敏：

1. 丢掉已判定为非正文的档（path / progress / counter / identifier / too-long，判据同 `CaptureClassify`）；
2. 丢掉含玩家私有标识的串：存档名（`HighLogic.SaveFolder`）、飞行器名、Kerbal 名、玩家目录名——按值替换成 `<vessel>` / `<kerbal>`，命中即降级为不投；
3. 单次会话只出现 1 次且长度 > 120 的串默认不投（这类多是调试/日志文本，噪声大于价值），可在同意界面手动放开。

### 6.2 同意怎么拿

- 默认 **不投稿**（`SubmissionConsent = None`）。第一次触发时弹一次说明（发什么、不发什么、发到哪个仓库、怎么撤回），玩家点「同意」才写 `prefs.json`。
- 每次实际投稿**还要一次确认**：显示「本次将上传 N 个 mod 的 M 条原文」，并给「打开文件夹」按钮——bundle 永远先落盘到 `PluginData/UI18n/submissions/`，玩家能自己看、自己删。
- 任何时候 `SubmissionConsent = None` + 删掉目录 = 彻底撤回；仓库侧不保留可追溯到人的标识（§6.3）。

### 6.3 传输通道（v1：零服务器）

插件不持有任何凭据，所以 v1 的通道是：**bundle 落盘 + 打开预填 GitHub Issue 页面**（`.github/ISSUE_TEMPLATE/submit-texts.yml`，玩家把 json 拖进去）。仓库侧 CI 用 `api submission accept` 收，收完在 issue 里回一行「已收入 N 条，进入候选表」。

- 去重键：`(h, modId)`；同一串被多个玩家提交只累加计数（`submissions/hits.json`? → 记进 `packs/candidates.jsonl` 的 `hits` 字段，不新建表）。
- 匿名：bundle 内**没有**玩家 ID；重复投稿靠客户端随机 `anonSalt` 去重（只影响本地，不上传）。

被否掉的备选：让插件直连 GitHub API（要往客户端塞 token，等于公开泄露）、让插件 POST 自建端点（要运维一台服务器）。后者留作 §11 M4。

## 7. 流水线：投稿 → 译文

```text
submission accept  →  packs/submissions/<id>.json（脱敏后原件，留档）
                   →  candidates：h+modId 新增/计数 +1，来源标 player
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

现在 `stable/manifest.json` 只有整包 zip。v2 增加每 mod 一条：

```json
{"version":"1.6.0","language":"zh-cn","kspVersion":"1.12.5",
 "mods":[{"modId":"TUFX","displayName":"TUFX","file":"Packs/zh-cn/TUFX.il.jsonl",
          "bytes":31879,"sha256":"…","rows":312,"updatedAt":"2026-10-26T18:30:00+08:00"}],
 "zip":{"name":"UI18n-1.6.0-zh-cn-packs.zip","bytes":1408263,"sha256":"…"}}
```

运行期语义：

1. 只下载 **玩家启用且本地缺失/过期** 的 mod 文件（v1 仍下整包，但只装启用的条目；v2 才做单文件下载）。
2. 装之前逐文件校验 sha256/bytes（`UpdateVerify` 已有）。
3. 「根据 mod 名称判断」= 用 `modId`（`displayName` 只用于界面展示），不对文件名做模糊匹配。

## 10. 运行时按 mod 开关

`PluginData/UI18n/prefs.json`（不是 `Config.cfg`：这是玩家状态，不该被 mod 更新覆盖）：

```json
{"schema":1,"language":"zh-cn","defaultMode":"translated",
 "mods":{"FerramAerospaceResearch":{"enabled":true},"TUFX":{"enabled":false}},
 "submission":{"consent":"granted","policyVersion":1,"lastSubmittedAt":"…"}}
```

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
| M3 | 采集端 bundle + 同意 UI + Issue 模板 + `api submission accept` | 一次真实投稿从 bundle 到 `candidates` 行数对得上 |
| M4 | 中继端点（可选）自动开 PR | 端到端无人值守 |

## 12. 待拍板（有默认方案，不回即按默认执行）

| # | 问题 | 默认 |
|---|---|---|
| D1 | 投稿通道 | 零服务器：bundle 落盘 + 预填 Issue（M4 再谈中继） |
| D2 | 隐私边界 | 只投 mod 列表 + 可译原文 + 语言/版本；存档名/飞行器名/Kerbal 名/路径一律脱敏，命中即不投 |
| D3 | 开关粒度 | 按 `modId`（程序集→注册表映射），共享文本按 §10 语义，不承诺「关掉即全英文」 |
| D4 | 匿名与去重 | 不收集玩家标识；重复投稿只累加 `hits` |
| D5 | API 形态 | Python CLI + JSON envelope + CI journal 门禁；不上 HTTP（M4 视情况） |
| D6 | 手工修译文的入口 | `api entry put --actor human:*`（覆盖模型结果）或 `override put`；两者都要 journal |
