# T11 翻译流水线 — 交付说明

> 状态：四步流水线跑完、连跑两遍字节一致。最终包 41 文件 / **9,990 行** / 3,504,392 字节，
> `s` 以 `#` 开头的行 **22**（插入守卫拒绝的键行，故意保留，见 §9.2.1），契约校验 0 问题
> （Lead 的 audit_packs + 本侧加严探针）。
> 本文件只记录"怎么跑、怎么算、哪些是已知边界"，细节见 reports/translation-<ts>.json。
> §1–§10 是 41 包（translate.py）的口径；**§11 是 IL / 捕获候选流水线（translate_il.py）**，两条流水线产物不同、G6 口径暂时不同（见 §11.5）。

## 1. 调用范式（唯一正确姿势）

cwd 必须是**仓库根**，模块在 `tools/` 下，所以要走 PYTHONPATH：

```powershell
$env:PYTHONPATH='<repo>\tools'
Set-Location '<repo>'
python -m i18n_tools.translate --keyref-rescue --sample-out reports\sample-full.jsonl   # ①
python -m i18n_tools.resolve_keyrefs                                                  # ②③④（默认全开）
python -m i18n_tools.resolve_keyrefs --prefer-official                                # ③ 单独跑 = 幂等空操作
python -m i18n_tools.resolve_keyrefs --drop-keyrefs                                   # ④ 单独跑 = 幂等空操作
```

- **流水线是四步，顺序不能颠倒**：
  ① `translate --keyref-rescue`：翻译并**整文件重写** `packs/zh-cn/*.jsonl`（这一步会把后三步的行冲掉）；
  ② `resolve_keyrefs`（补行）：**只追加**键引用解析行（`s = glossary.en` / `t = 官方中文`，本轮 +917，见 §9）；
  ③ `--prefer-official`：**官方译文优先**，把已有行里 `s` 与官方 `en` 逐字节相同、官方 `zh` 过门禁的
     那几条 `t` 覆盖成官方值（本轮 1 条，见 §9.1）；
  ④ `--drop-keyrefs`：**删除** `s` 以 `#` 开头的键引用行（本轮 −970，删前做零覆盖损失硬门禁；
     被插入守卫拒掉的 22 条保留原行，见 §9.2 / §9.2.1）。
  ②③④ 是**同一个命令的三个开关**，默认全开，所以正常只需跑一条 `python -m i18n_tools.resolve_keyrefs`。
  单独跑 ③ 或 ④ 都是**幂等空操作**（`official_overrides = 0` / `dropped = 0`，字节不变），列在这里是为了让顺序可独立验证。
  关闭开关：`--no-prefer-official` / `--no-drop-keyrefs`。
- `--dry-run` **只抑制写 packs**（不抑制请求）；不发请求要用 `--offline`（桩响应，验证链路）。
- 报告/缓存/未译清单全部落在**仓库根** `reports/`；不要在 `tools/` 下跑，否则会造出 `tools/reports/`。
- 密钥只从 `.env.local`（或同名环境变量）读，日志里一律 `sk-***`。

## 2. 输出契约（以 Core 为准，不是 docs/04 §11.2）

每行 `{"h","n","s","t","mod"}`：
- `h = hash_text(normalize(s))`（FNV-1a 64，16 位小写 hex，来自 data-engineer 的 `i18n_tools/fnv.py`）
- `n = normalize(s)`、`s` = 原文、`t` = 译文、`mod` = 站点层
- keep 行写成 **t == s**（逐字节），运行时 `DictEntry.IsKeep` 命中后终止下沉
- 同 mod 合并成**单文件**（ConfigLoader 只读 `*.jsonl` 顶层，一个 mod 拆两文件会被当两个包）

## 3. 本轮统计（packs/zh-cn）

| status | 条数 | 说明 |
|---|---|---|
| ok | 8,929 | 模型翻译 |
| rescue | 992 | 键引用套用官方中文记忆（零 token，非模型输出） |
| keep | 88 | 7 条遮蔽后只剩不可译物 + 81 条 keep-echo（包内 `t == s` 恰 88 行，与审计 `keep_rows` 一致） |
| official | 34 | 整句命中官方译文记忆 |
| failed | 6 | 见 §6 |
| 合计 | 10,049 | 写盘 10,043（failed 不写） |
| resolved | +917 | §9 的键引用解析行（后处理追加，非模型输出；插入守卫另拒 22 条） |
| official_overrides | 1 | §9.1 官方译文优先覆盖的模型行（SXT `J-90 "Goliath" Turbofan Engine`） |
| 补行后行数 | 10,960 | 41 文件 / 3,695,878 字节 |
| dropped | −970 | §9.2 删除的键引用行（覆盖证明通过后删；守卫保留 22 行） |
| **包内最终** | **9,990** | **41 文件 / 3,504,392 字节**；`s` 以 `#` 开头 **22** 行（守卫保留，见 §9.2.1） |

八门禁（全量口径）：G2/G3/G5 从未触发；G1 只来自"模型漏 id"（已由单条重发兜住）；G4 2、G6 2、G7 2 是最终残留。

## 4. keep-echo（本轮新增）

模型把**短专名/型号/标识符原样抄回**时，旧实现记 G8「译文等于原文」判死。实际这是 00 §4.1 的 keep：
判据三重收紧，避免把"整句不译"也放过——
1. strip 后相等或仅大小写差异（`rcs` → `RCS`）；
2. ≤80 字符且 ≤4 词（80 是为了容纳 51 字符的 lookaround 正则与 41 字符的配置键）；
3. 不像散文：按空白切分后**没有相邻两个纯小写单词**（`parts list`、`Throttle up please` 因此仍判失败；
   而 `fx-katniss-noise-2-blurred` 这类连字符标识符算一个词，正确判 keep）。

命中后落 `t == s` 的 keep 行，stats 记 `keeps_echo`。

## 5. 缓存播种说明（重放，不是伪造）

上一轮 81 条 echo 的模型返回**没有进缓存**（failed 不写缓存）。为了让全量重跑零调用地复用它们，
用 `i18n_tools.translate.cache_key` + `Cache.put` 把"该条源文 + 模型当时返回的遮蔽态文本"写进
`reports/cache/translations.jsonl`。遮蔽态由 `mask(src).text` 重建，靠 mask 的 round-trip 不变式
（`restore(mask(s)) == s`，全池 12,792 条自测 0 失败）保证与模型当时的输入同形。
**这是重放已记录的模型输出，不是生成新译文**；缓存键含 model/promptV/glossaryV，换模型或改 prompt 自动失效。

## 6. 残留 6 条（0.06%，标 untranslated，运行时回退原文）

| id | mod | 门禁 | 原因 |
|---|---|---|---|
| 7116 | RP-1 | G4 | **源文自身标签不对称**（`<b>`×3 但 `</b>`×2），模型"顺手修正"了闭合 → 与源不符 |
| 7124 | RP-1 | G4 | 同上 |
| 10489 | Sol-Configs | G6 | `Mare Serenitatis` → 澄海（长度比 0.10，中文比英文短，G6 下限 0.15） |
| 10490 | Sol-Configs | G6 | `Mare Tranquillitatis` → 静海（同上） |
| 2761 | VenStockRevamp | G7 | 关键词堆（`fueltank mono monopropellant…`），`Monopropellant` 未落成"单组元推进剂" |
| 2947 | VenStockRevamp | G7 | 同上（短版） |

其中 10489/10490 的译文其实是对的，是 G6 下限对短专名过严；为 2 条放松门禁不划算，故保留为未译。

## 7. G7 义务必须在"模型看得见"的文本上算（本轮修的 bug）

旧实现用**原文**算 G7 义务，但术语可能有一段被 mask 成哨兵（`J-90` 整段遮蔽 → 模型只看到
`<<M0>> "Goliath" Turbofan Engine`），模型不可能写出那段中文 → 误杀 7 条。改为在 `item["masked"]` 上算。

## 8. 半译（mixed_lang）两桶口径

旧数字 594 是**未分类的原始残留**，含 `<color=white>` 标签内的 color/white、字面 `\n` 后的 nDSN、
`@/missionPayload.Print()` 表达式、rpm/hp/km/dBi 等单位。现拆为：
- `brand_residue`：源文中以大写/含数字形式出现过的词（Falcon 9、Bluedog Design Bureau、Delta IV）→ **不算缺陷**
- `true_mixed`：其余 → 真半译

全量重算：残留 ≥3 词 **241 条 = brand 102 + true 139（1.41%）**。不要再引用 594。

## 9. 键引用解析行（resolve_keyrefs，常驻后处理步骤）

**问题**：`--keyref-rescue` 产出的 992 行 `s` 是**未解析的键**（`#autoLOC_500832`），而运行时钩子拿到的是
KSP 已解析后的显示文本（`KSP.Localization.Localizer` 在 UGUI 钩子之前就替换完了，src 里没有键解析逻辑）
→ 这些行在正常安装里几乎匹配不到。

**解法**（`tools/i18n_tools/resolve_keyrefs.py`）：**不改任何已有行**，只按 glossary 追加"解析行"：
`s = glossary.en`（官方英文原文）、`t = glossary.zh`（官方中文）、`n = normalize(s)`、
`h = fnv1a(n)`、`mod`/`ctx` 沿用原键引用行。键引用行本身在本步**原样保留**，随后由第 ④ 步删除
（见 §9.2：它们 100% 冗余——992 个键的官方 en+zh 都已以解析行形式在包里）。

补行前逐条过门禁（与 Lead 侧 `_scratch/audit_packs.py` 的 tag_re **逐字节同口径**）：
占位符 `<<n>>` 多重集、TMP 标签多重集、`{n}`/`%s` 多重集、`t != s`、`t` 非空。

**本轮结果**：键引用行 992 → 新增解析行 **917**；`unresolvable 0`、`existing 29`（该英文串的 h 已在包里，
多是模型早先译过同一句，按"不改行"原则跳过）、`duplicate 24`（同一英文串由多个键指向，只补一次）、
`gated 0`、**插入守卫拒掉 22**（歧义 14 + 长度比 8，见 §9.2.1）。总行数 10,043 → **10,960**；
`s` 以 `#` 开头的行数本步仍是 **992**（本步只增不减），随后由第 ④ 步归 **22**（被守卫拒绝的那些保留原行，见 §9.2）。
幂等：连跑两遍，第二遍数字全等、文件字节一致。统计落 `reports/keyref-resolve-<ts>.json`。

**体积**：992 条键引用行 = **195,738 字节（191.2 KB），占包体 5.29%**（补行后总 3,695,878 B）。
其中 970 条由第 ④ 步删除，22 条因守卫保留 → 最终包 3,504,392 B（删除后审计见 §9.2）。

## 9.1 官方译文优先（--prefer-official，默认开）

设计原则第一条是"官方数据优先、模型只填空缺"。同一句英文出现两个中文时留模型版会制造术语漂移
（`白` vs `白色`、`涡轮风扇发动机` vs `涡轮风扇引擎`），所以本步骤把**已有行**的 `t` 对齐到官方值。

- **触发条件**（三者同时成立）：① 该行 `s` 与 `glossary.en` **逐字节相等**（不做归一化、不折叠大小写，
  `white` ≠ `White`）；② 官方 `zh` 过全部门禁（非空、`t != s`、`<<n>>` 多重集、TMP 标签多重集、
  `{n}`/`%s` 多重集）；③ 该 `en` 在官方记忆里只有**唯一**中文。
- **只改 `t` 的值**：不重新序列化整行（`_replace_t_value` 按 JSON 字符串扫描定位顶层 `"t"`），
  `h/n/s/mod/ctx` 与行序、行尾字节一律不动 → `git diff` 里就是一行 `-旧/+新`。
  每条改完立刻 `json.loads` 复核 `t == 官方` 且其余字段全等，不等则计 `errors` 并放弃该条。
- **歧义不猜**：同一 `en` 在官方记忆里有多个中文（`Toggle <<1>>` = 开关<<1>>/收放<<1>>、
  `Ore` = 矿物/矿石、`Lights On/Off` …共 16 组）时**跳过**，只统计现值是否已是官方变体
  （`ambiguous_ok` / `ambiguous_diff`）。实测 4 组在包内有行，全部已是官方变体，`ambiguous_diff 0`。
- **幂等**：第二遍 `official_overrides = 0`（已等于官方值计入 `already_official`），文件字节不变。
- **歧义 16 组不做统一，理由：官方自身上下文相关，盲选必然引入错误**（`Toggle <<1>>` 在"开关/收放"两种语境里
  都是官方用法，`Ore` = 矿物/矿石同理）。`--ambiguous-policy` 故意不实现，免得以后被当漏项。

**本轮实测**（`reports/keyref-resolve-20261023-083154.json`）：`official_overrides 1` / `skipped_gate 0` /
`already_official 947` / `ambiguous 16（ok 4, diff 0）` / `errors 0`。
覆盖后包内 `s` 与官方 `en` 逐字节相同、且官方映射唯一的行共 **948** 条，`t` **全部**等于官方值
（独立探针复核 `t_differs 0`）；4 条歧义行的 `t` 也全是官方变体之一。
首轮跑时唯一不一致的那条是 SXT `J-90 "Goliath" Turbofan Engine`：

| 位置 | 覆盖前（模型） | 覆盖后（官方） |
|---|---|---|
| `packs/zh-cn/SXT.jsonl:450` | J-90“歌利亚”涡轮风扇**发动机** | J-90“歌利亚”涡轮风扇**引擎** |

`git diff -U0` 形态（在 `%TEMP%\po_demo` 复现的 before/after 对照）：

```diff
@@ -450 +450 @@
-{"h": "dd60f463a99ea33c", …, "t": "J-90“歌利亚”涡轮风扇发动机", "mod": "SXT"}
+{"h": "dd60f463a99ea33c", …, "t": "J-90“歌利亚”涡轮风扇引擎", "mod": "SXT"}
```

## 9.2 删除键引用行（--drop-keyrefs，默认开）

**为什么删**：992 条键引用行里 **970 条**的官方英文+中文已经以解析行形式存在于包里
（`s = glossary.en` / `t = 官方中文`）。键引用行唯一可能命中的场景是
「某个 mod 把未解析的键直接塞进 GUI」，而那时它给出的译文与解析行完全一样 → 纯冗余副本，不是后备。
剩下的 **22 条**（被插入守卫拒绝，见 §9.2.1）在包里没有任何等价行，删掉就是真丢官方中文 → **保留原行**。
`Squad.jsonl` 1,900 → **919 行**（981 条键行 = 959 删除 + 22 保留）。

**零覆盖损失硬门禁**（`drop_keyrefs_pass`）：删前对**每一条**待删行验证，任一条不满足就**整体放弃、一个文件都不写**
（CLI 返回码 2）。它的官方中文 `zh` 必须能在剩余行里找到：

| 类别 | 判据 | 本轮 |
|---|---|---|
| ① 同文件 | 同一文件的剩余行里有某行 `t == zh` | 941 |
| ② 全局 | 全局剩余行里有某行 `t == zh` | 29 |
| ③ en 等价 | 全局剩余行里有某行 `s == glossary.en`（逐字节） | **0**（守卫已更严，见下） |

第 ③ 类的理由：`DictPack` 以 `h = fnv1a(normalize(s))` 为键，**同一 `en` 只能存活一种译法**，
官方记忆里 1-en-多-zh 的重复条目在运行时结构上不可达；删掉键行不会让任何「可命中的译法」消失。
本轮它**一条都没用到**：会落到它头上的 6 条（`The Big-S…` × 4、`Description Creation Pending` × 5）现在全部
被插入守卫判为歧义而保留原行（§9.2.1）—— 守卫比这条兜底更严。开关留着，是为了守卫被关掉时仍能安全删除。
`--no-coverage-en-equiv` 实测（HEAD 副本）：输出 `覆盖证明失败：6 条，已整体放弃（一个文件都没写）`，
行数与 `s#` 全不变 —— 硬门禁是 all-or-nothing，不是部分删除。

**实测**（`reports/keyref-resolve-20261023-083154.json`）：`rows_before 10960` / `dropped 970` / `rows_after 9990` /
`bytes_before 3695878` / `bytes_after 3504392` / `coverage_verified true`（941 + 29 + 0）/ `retained 22` /
`by_mod {Squad 959, ReStock 6, KSPCommunityFixes 2, RealismOverhaul 2, ProceduralFairings 1}`。
幂等：第二遍 `dropped 0`、**41 个文件 SHA256 前后一致**（3,504,392 B）；`--dry-run` 不写盘。
只删键行、不碰非键行：第 ④ 步本身只删不增，在 **index 视角**可见 —— `git diff --cached --numstat HEAD -- packs/zh-cn`
全部是 `+N/−0`（`Squad.jsonl` 918/0、`RealismOverhaul.jsonl` 97/0、`ReStock.jsonl` 6/0 …）。
worktree-vs-HEAD 的 `git diff --numstat` 里会看到少量 `+N/−M`（`RealismOverhaul` 97/2、`ReStock` 6/6、
`KSPCommunityFixes` 2/2、`ProceduralFairings` 0/1），那是**第 ① 步 translate 整文件重写**造成的，不是第 ④ 步。

**审计（删除后最终态，连跑两遍一致）**：
- Lead 的 `_scratch/audit_packs.py` 原样跑：`{"files":41,"rows":9990,"keep_rows":88,"bad_h":0,"empty_t":0,"bad_placeholder":0,"bad_richtext":0,"bad_samples":[]}`
- 本侧加严探针：`{"files":41,"rows":9990,"bytes_total":3504392,"fields_used":["h","mod","n","s","t"],"problems":0,"s_hash_rows":22}`
  （字段集合恒 `{h,n,s,t,mod}`、**全局无重复 h**、`n==normalize(s)`、`h==fnv1a(n)`、`t` 非空、无控制字符、占位符多重集）
- 缺陷复查：包里 `s == "Description Creation Pending"` 的行 **0 条**（`t` 里也没有该串）。

### 9.2.1 插入守卫：歧义 + 长度比（本轮修的缺陷）

**缺陷**（Lead m01164 抓到）：`Squad.jsonl:441` 曾有一行 `s = "Description Creation Pending"`
（KSP 内部占位串，意思是「描述待撰写」），`t` 却是 **TS-06** 这个零件的真实描述。成因：官方记忆里这一个 `en`
对应 **7 个不同官方中文**（每个键的中文是那个零件自己的描述），逐键插入时第一个成功、其余按 `duplicate` 跳过
→ 通用占位串被永久绑到某一个零件的描述上。后果：任何「描述待撰写」的零件都会显示 TS-06 的描述，比显示英文更糟。

**两道守卫**（都在 `build_entry` 里、插入前判定）：
1. **歧义守卫**（`--ambiguous-guard`，默认开）：`en -> 去重后的官方 zh 集合`，**size > 1 一律不插入**，
   记 `ambiguous_skipped`。原则与 §9.1「歧义不猜」同一条：官方自己按上下文区分，盲选必错。
2. **长度比门禁**（`--ratio-gate`，默认开）：`zh/en` 字符长度比按 G6 同口径 **0.15–1.8**（`len(en) < 9` 豁免），
   越界记 `gated_ratio`。第二道网：该串 27 → 90+ 字符、比值 3.3，即使没有歧义守卫也会被拦。

**被拒的键怎么办**：它们在包里没有任何等价行，删掉就真丢官方中文 → 第 ④ 步对它们**保留原行**
（`retained`，日志打印 `[保留]`），所以最终 `s` 以 `#` 开头 **22 行**（不是 0）。这 22 行只在
「mod 把未解析的键直接显示在 GUI 上」时才可能命中，不在正常路径上。

**本轮被拒的 22 条**（`ambiguous_skipped 14` + `gated_ratio 8`）：

| 类型 | 键 | 官方 `en` | 官方变体 / 比值 |
|---|---|---|---|
| 歧义 | `#autoLOC_500206` `500209` `500212` `500218` | `The Big-S line of wings are C7's largest lifting surfaces to date…` | 2 个（`…是C7迄今为止生产的…` / `…是迄今为止C7生产的…`，措辞微差） |
| 歧义 | `#autoLOC_501782` `501785` `501788` `501791` `501794` `501800` `501803` | `Description Creation Pending` | **7 个**（TD-06 / TD-12 / O.M.B. / 爆炸螺栓 / TS-06 / TS-25 / 危险切分器，各自是那个零件的描述） |
| 歧义 | `#autoLOC_501675` | `Moving Parts Experts Group` | 2 个（运动部件专家小组 / 运动部件专家组） |
| 歧义 | `#autoLOC_501007` | `Ore` | 2 个（矿物 / 矿石） |
| 歧义 | `#autoLOC_501010` | `EVA Report` | 2 个（EVA报告 / 舱外报告） |
| 长度比 | `#autoLOC_500100` | `Record gravitational forces` | 0.148（记录重力） |
| 长度比 | `#autoLOC_500083` `500040` | `electric charge` | 0.133（电能） |
| 长度比 | `#autoLOC_501004` | `Electric Charge` | 0.133（电量） |
| 长度比 | `#autoLOC_501656` | `Public Relations` | 0.125（公关） |
| 长度比 | `#autoLOC_501679` | `Research & Development Department` | 0.121（研发部门） |
| 长度比 | `#autoLOC_501019` | `Asteroid Sample` | 0.133（样本） |
| 长度比 | `#autoLOC_6002506` | `Usually, when you want to improve a part…`（300+ 字符） | **0.021**（官方中文只有「小型高级抓取单元」= 标题） |

`#autoLOC_6002506` 说明长度比门禁不只是「再拦一遍占位串」：它是官方记忆里**英文与中文不是同一字段**的证据
（en 是整段描述、zh 是标题），插进包里会让描述变成标题。

**已知限制（正确行为，不是缺陷）**：`Description Creation Pending` 这句串不在 `pool.jsonl` 里（0 行），
只可能从键解析进来；守卫跳过它之后，运行时对「描述待撰写」会**显示英文原文** —— 这比显示 TS-06 的描述正确得多。
同理，上表 22 个键对应的显示文本若真以未解析键形式出现，也会回退英文/键原文。

**selftest**（`python -m i18n_tools.resolve_keyrefs --selftest` = **49 项 0 失败**）含 6 条守卫断言：
歧义不插入 2 条 / 长度比拦截 1 条 / 占位串不进包 / 被拒键保留原行（`retained` 3、`s#` 3）/ 歧义明细带全部变体 /
**关掉守卫会复现缺陷**（`--allow-ambiguous-en --no-ratio-gate` 时占位串行重新出现）。

要严格区分这些同 `en` 的键只能改成**按 ctx 分层**（`ctx = 键名`），代价是运行时必须传 ctx 才能命中，
而当前 Runtime 不传 ctx（`DictPack.TryGet` 在 context 为空时跳过全部 ctx 级条目）→ 不做。


## 10. 20 条游戏内截图核对清单

见 `reports/verify-20.csv`（mod / s / t / h 四列）。

## 11. IL / 捕获候选流水线（translate_il.py，T33）

41 个 `packs/zh-cn/<mod>.jsonl`（9,990 行，1.0.0 已发布）由 `translate.py` 产出；
IL / 运行时捕获候选走**另一条**流水线 `translate_il.py`，产物是 `packs/zh-cn/<mod>.il.jsonl`（独立文件，不与 41 包混写）。

### 11.1 调用范式

```powershell
$env:PYTHONPATH='<repo>\tools'
Set-Location '<repo>'
python -m i18n_tools.translate_il --il packs\il-translatable.jsonl --capture packs\capture-candidates.jsonl --ctx-mode drop --g6-convention-exempt
```

- 输入：`packs/il-candidates.jsonl`（首版 2,285 行）或 task-32 产出的 `packs/il-translatable.jsonl` + `packs/capture-candidates.jsonl`。
- 输出：`packs/zh-cn/<mod>.il.jsonl`，字段 `{"h","n","s","t","mod"}`（与 41 包逐字段一致，见 §2）。
- `--dry-run` 只抑制写盘；`--offline` 桩响应不发请求；`--limit N` 均匀取样；`--mods a,b` 单 mod 试点。
- 去重与合并：跨 mod 同文本合并成一行（`mod` 记首个宿主），同 `h` 不同原文变体单独统计；provenance 走 sidecar `reports/il-provenance-<ts>.jsonl`。

### 11.2 ctx 决策：默认 `--ctx-mode drop`（Lead 已批准）

四处源码证据 → 运行时拿不到 ctx：

1. `src/UI18n.Core/Dict/DictPack.cs:148-201` `TryGet(hash, source, context, out translation, out level)`：**context 为空时跳过全部 Ctx 级条目**。
2. `src/UI18n.Core/Dict/DictEntry.cs:24-35`：`Context` 非空 → Level.Ctx；否则 `Mod` 非空 → Level.Site。
3. Runtime hook 侧一律 `context: null` 调用 `TryGet`（没有调用方传 ctx）。
4. 已发布的 41 个包**根本没有 `ctx` 键**（`packs/zh-cn/TriggerTech.jsonl` 前 200 行实测 `NOCTXKEY x200`）→ drop 产出的 schema 与已发布包逐字段一致，零运行时风险。

`--ctx-mode keep` 保留：行里写 `ctx = "il:<host>"`，但报告写 `runtime_reachable: false` 并打印
`[il][WARN] ctx_mode=keep → ctx 非空的行是 Ctx 级，运行时 TryGet 传 context=null 会跳过`。默认 drop。

### 11.3 状态分类（不靠放宽门禁来提通过率）

| status | 含义 | 写库 | 重试 |
| --- | --- | --- | --- |
| `ok` | 八门禁全过 | 是 | — |
| `keep` | 无需翻译（专名/型号/标识符，或模型只把 `Delta-V` 换成 `Δv` 后无中文可译）→ `t == s` 逐字节 | 是（keep 行） | 否 |
| `gated_ratio` | **只有 G6 长度比**不过。长度比是这对文本的固有属性，重发不会变好 | **否**（按 untranslated，运行时回退英文） | **否** |
| `failed` | 其余门禁（含 G1 漏 id 重发后仍失败） | 否 | 最多拆半/单条一次 |

`keep` 的判定是 `T.echo_keep(...) or keep_no_cjk(...)`：`keep_no_cjk` 要求「译文无中文 **且** 剥掉哨兵/标签后已无含小写字母的可译词」，
所以 `<b>Delta-V</b>: {0} m/s → <b>Δv</b>：{0} m/s` 落 keep，而「模型把英文抄回来但仍有可译词」不会误判成 keep。

### 11.4 G6 下界豁免：只走机器可读数据（Lead 已批准）

依据 `packs/conventions.jsonl`（12 行 `{"en","zh"}`，逐字节取自 docs/04 §8.2【专有约定】）：
Booster=助推器 / Oxidizer=氧化剂 / Throttle=节流阀 / Gimbal=万向节 / Specific Impulse=比冲 / Apoapsis=远地点 /
Periapsis=近地点 / Delta-V=Δv / Center of Mass=质心 / Center of Pressure=压心 / Reaction Wheel=动量轮 / Monopropellant=单组元推进剂

`--g6-convention-exempt` 下**五条全满足**才豁免，否则照旧判 G6：

1. 源串 `.strip()` 后**逐字节等于**某条 `en`（`Center of Mass of the vessel` 不算）；
2. 译文 `.strip()` 后**逐字节等于**该条的 `zh`（`重心` 等别名不豁免）；
3. `len(zh) >= 2`；
4. 只处理**下界**违规（比值 ≥ 0.15 时交回正常 G6，上界 1.8 不受影响）；
5. `packs/conventions.jsonl` 缺失或为空 → 永不豁免（并打印 WARN 关闭该开关）。

豁免条数计入 `g6_exempt`，明细进报告 `gated_ratio_exempt`（h/mod/s/zh/ratio）。
**`Insufficient electric charge → 电能不足` 不豁免**（整句不是术语），仍记 `gated_ratio` 回退英文 —— Lead 明确要求。

### 11.5 分歧记录：两条流水线对 G6 的处理不同（v1.2 重跑时统一）

- `translate_il.py`（IL 包）：**启用** §11.4 的约定术语豁免（`--g6-convention-exempt`）。
- `translate.py`（41 包，1.0.0 已发布）：**未启用**，仍是纯 G6 下界 0.15。原因：1.0.0 的 9,990 行已发布并做过游戏内验证，
  为几十条术语改口径会重写已验收产物，收益不足；且 41 包的 G6 命中只有 6 条残留（§6），风险面极小。
- 计划：**v1.2 重跑时统一**为带 `packs/conventions.jsonl` 豁免的口径，届时两条流水线共用同一张表。

### 11.6 fixture 实测（`reports/il-fixture.jsonl`，48 行自造语料）

- 48 候选 → 去重 42 组 → 跳过 16（existing-h 10 / no-words 2 / no-letters 2 / already-cjk 1 / bucket-never 1）→ 送翻 26 组 / 11 批。
- 首轮（无豁免）：ok 21 / failed 5（G6 4 + G8 1）。
- 加 `--g6-convention-exempt`：**ok 24 / keep 1 / failed 0 / gated_ratio 1**；豁免 3 条 = Center of Pressure→压心(0.111)、
  Specific Impulse→比冲(0.125)、Center of Mass→质心(0.143)；仍被拒的 1 条 = Insufficient electric charge→电能不足(0.14)。
- 质量样例：`Tank`→储罐、`Apoapsis: {0} km`→远地点: {0} km、`Plume Density: %s`→羽流密度：%s、`Engine Status: %d`→发动机状态：%d、
  `Warp to Next Maneuver`→快进至下一机动点、`Recover Vessel`→回收载具、`Radial Out`→径向外、`Retrograde`→逆行、`EVA Report`→EVA报告。
- 真实数据预演（`packs/il-candidates.jsonl` 2,285 行，离线）：去重 2,082 组（跨 mod 合并 96）→ 跳过 579
  （no-letters 192 / no-words 167 / existing-h 62 / key-ref 21 / already-cjk 137）→ **送翻 1,503 组 / 79 批 / 44 个 mod 文件**。
- 全量数字与 `gated_ratio` 全量清单**待 task-32 输入到位后回填**。
- selftest：`python -m i18n_tools.translate_il --selftest` = **95 项 0 失败**（含豁免五条的正负例、`ctx_mode` 字段集合、预筛顺序、
  `--capture ""` 空路径，以及 §11.9 人工覆盖的 21 条断言）。

### 11.7 缓存与可复现性（本轮修的坑）

- 缓存文件 `reports/cache/il-translations.jsonl`（append-only），键 = `fnv1a(text)|glossaryV|il1|model`。
- **`ok` / `keep` / `gated_ratio` 三种终态都入缓存**，存的是**哨兵态**模型原始产出（不是已还原文本 —— 命中后会再走一次 gate，
  存已还原文本会被二次还原吃掉哨兵，制造假 G5，这是 41 包流水线踩过的同一个坑）。`failed` **不入缓存**，留给下一轮重试。
  为什么 keep/gated_ratio 也要缓存：它们不写库/写 keep 行，但**重跑时若重新问模型会拿到不同结论**（实测 `" From Now"` 第一次判 gated_ratio(0.11)、
  第二次模型给出可通过的译文 → 包内容随重跑漂移）。缓存后重跑结论固定。
- `--offline`：**强制 `--dry-run` + 禁用缓存**（桩响应是假译文，绝不能进包或进缓存）。开发期曾误产出 6 个
  `t = "【离线】Abort"` 的桩包与 1,501 条 `model=offline` 的桩缓存行，已全部清除。
- `--sample-out` 审计物对**不写库**的 status 也记模型原始产出（`zh` 取 `entry.t`，没有 entry 时取模型输出），否则审计物里是空串。

### 11.8 真实数据 `--limit 50` 预演（基于 `packs/il-candidates.jsonl`，非 task-32 新输入）

- 取样 50 组 / 28 批 → **ok 46 / keep 3 / failed 0 / gated_ratio 1**；G6 1、G8 3（3 条 G8 全落成 keep = 模型只回抄标识符）。
- 冷跑 33 次调用 / 29.3s / prompt 20,810 + completion 988；重跑缓存命中 45 → 9 次调用 / 13.4s。
- 写盘演练（`--out %TEMP%\il-out2`）：18 文件 / 49 行，契约审计 **0 问题**（字段恒 `{h,n,s,t,mod}`、`h == fnv1a(normalize(s))`、
  `n == normalize(s)`、t 非空、占位符多重集一致、无重复 h、无控制字符）；修复缓存后连跑两遍**字节完全相同**（第二遍 0 次调用）。
- keep 3 条全对（`ReactionWheel` / `minDistance:` / `VertexDefineCoastLine`）。
- 质量抽查 12 条（节选）：`Display Aero Data in Action Menus`→在动作菜单中显示气动数据、
  `Tip: You can press Alt + 7 in the tracking station to display the shadow map`→提示：在追踪站中按 Alt + 7 可显示阴影贴图、
  `Vessels: {0}`→载具：{0}、`Mode: Time → %`→模式：时间 → %、`Part flight data meets or exceeds maximum lab R&D amount`→部件飞行数据已达到或超过实验室研发上限。
- **发现的 3 个质量问题**（均未放宽门禁，待 Lead 决策）：
  1. 渲染路径词被意译：TUFX `Status: Forward Active (SSSR Mode)` → `状态：前进激活`（Forward = 前向渲染，应作「前向」）。同类风险词 Forward / Deferred / Forward+。
  2. RP0 `Invalid Integration Rate` → `总装速率无效`（integration 更接近「集成/组装速率」），glossary 无机器依据，暂记不改。
  3. 源文内部连续空格被折叠（`IconRefs in total:   Nodes:` → `总计 IconRefs：节点：`）；IMGUI label 无影响，建议不做。
- `gated_ratio` 1/50（2%）：RP0 `" From Now"` → `后`（比值 0.11，源文带前导空格）。样本太小，**全量后给全量清单**再判是否值得动态下界。

### 11.9 人工覆盖通道 `--overrides`（lead m01628 裁定后新增）

- 文件 `packs/overrides.jsonl`，每行 `{"en": "<源文逐字节>", "zh": "<期望译文>", "note": "<为什么>"}`。
  当前 3 行（TUFX 渲染路径词，`Forward`=前向 / `Deferred`=延迟，业界标准译法）：
  `<color=yellow>Forward Active (SSSR Mode)</color><color=green>Deferred Active (Full PBR SSR)</color>`、
  `[SSR Pipeline] Status: <color=green>Deferred Active (Full PBR SSR)</color>`、
  `[SSR Pipeline] Status: <color=yellow>Forward Active (SSSR Mode)</color>`（前两行由 lead 给译文，第三行是语料里
  「两条状态粘在一行」的同一约定组合，来源 `packs/il-translatable.jsonl:909`）。
- 命中规则：`normalize(源文)` 与 `en` 逐字节相等（不折叠大小写、不合并变体）；同 en 多行不同 zh 只取第一行并计 `conflicts`。
- **位置：模型之前**（命中即不请求模型、也不看缓存）—— 人工优先同时省 token；缓存里的旧模型译文不会盖掉人工译文。
- **人工优先 ≠ 免检**：`override_gate` 仍校验**占位符 `<<n>>` 多重集**与**富文本标签多重集**，不符则跳过该条并记
  `override_rejected`（带 why=h/empty/placeholder/richtext），随后照常走模型流程，绝不写坏行。
  长度比（G6）不查：人工译文的长短由人负责。
- 报告字段：`overrides_total` / `overrides_applied` / `override_rejected` / `bucket_status`（每桶状态表）。
- 实测（`--mods TUFX.dll`，真实语料 17 组）：**override 3 / ok 13 / keep 1 / failed 0**，`overrides_applied 3`、`override_rejected 0`；
  3 条覆盖行**零模型调用**（该 run 共 5 次调用，全部用于其余 14 条）。写盘两遍 **SHA256 完全相同**（`TUFX.dll.il.jsonl` = E916EF36…737AF）。
- selftest 21 条新断言（命中改 t / 字段集合仍是 h-n-s-t-mod / 占位符不符被拒 / 富文本不符被拒 / 未命中不动 / 冲突取首行 /
  空文件与缺失文件不报错 / 第二遍幂等 / override 不入缓存 / `packs/overrides.jsonl` 3 条且译文含「前向」「延迟」）；
  总计 **95 项 0 失败**。

### 11.10 lead 对三个质量问题的裁定（记录，勿再改）

| 问题 | 裁定 | 落地 |
| --- | --- | --- |
| 渲染路径词被意译（Forward Active → 前进激活） | **不建术语表**（全库 Forward 5 条含 2 条 `warp forwards`、Deferred 2 条、Forward+ 0 条，只影响 TUFX 2 行；建表+G7+文档属过度工程） | 走 §11.9 人工覆盖通道 |
| RP0 `Invalid Integration Rate` → 总装速率无效 | **记录不改**（glossary 无机器依据） | 留在本节，作为已知问题 |
| 源文连续空格被折叠 | **不做**（中文排版不保留英文双空格，逐字节保真无收益） | 无 |
| `" From Now"` → `后`（gated_ratio 0.11） | **保持 gated**（丢了 From，判断正确） | 全量后若 `gated_ratio` > 送翻 1% 再由 lead 定动态下界 |

### 11.11 分桶统计口径 + v1.1 最终口径（只翻 translate）

- `packs/il-translatable.jsonl` 1,746 行 = `translate 1145` + `short_label 429` + `template 172`（`reason`：sentence 1145 /
  single_word 429 / template_format 97 / template_progress 75）。
- 报告与日志用 `bucket_status` 给出**每桶独立的 ok/keep/override/gated_ratio/failed**；摘要打印一段「分桶统计」，
  不再出现一个把三桶相加的总数。
- **v1.1 最终口径（lead m01753 GO）：只翻 `bucket=translate`**；`short_label` 429 与 `template` 172 一律不翻。
  `--only-bucket` 支持 `translate|short_label|template` 三值，下一版可单独开桶。
  - `short_label` 不翻的理由：短词离开上下文必错。实证是 MechJeb2 `Omega`——同一批里 `Rendezvous`/`Alt`/`Lon` 是正常缩写，
    而 `Omega` 在轨道力学是升交点赤经、在 PID 控制器面板里是自然频率 ω（同屏邻居是 `Pos PTerm/ITerm/DTerm`、`Deadband`、
    `Desired`、`Current`、`Smooth Torque`、`Maximum Stopping Time`、`Minimum Flip Time`）。v1.1 不翻 ⇒ 保持英文上屏，风险为零。
  - `template` 不翻的理由：含 `{0}`/`{1:N0}` 之类占位符，必须与模板匹配后按槽位译，逐句翻会把整句语序锁死。
- 本轮真实全量（`--only-bucket translate --g6-convention-exempt`）：
  候选 1,746 → 去重 1,746 组（跨 mod 合并 49）→ 跳过 646（`no-words 32` / `existing-h 69` / `other-bucket 545`）→
  **送翻 1,100 组 / 52 批**；冷跑结果 **ok 1,091 / keep 4 / override 4 / gated_ratio 1 / failed 0**；
  八门禁 `G1 0 / G2 0 / G3 0 / G4 0 / G5 0 / G6 1 / G7 0 / G8 4`（G8 的 4 条全落 keep）；
  53 次调用 / 66.7s / **prompt 64,403 + completion 16,659 = 81,062 tokens**；
  写盘 **48 个文件 / 1,099 行**（`packs/zh-cn/<asmBase>.il.jsonl`，行内 `mod` = 同一个 `<asmBase>`，不带 `.dll`）。
  （`override` 由 3 增到 4 是 lead 裁定一新增的 R&D 行；`gated_ratio` 由 2 减到 1，见 §11.13。）
- 幂等（最终态复核）：紧接着再跑一遍 → **0 次调用 / 缓存命中 1,096 / 48 个文件 SHA256 全部相同 / 1.2s**
  （`override` 行在查缓存之前命中，所以命中数比 1,100 少 4；改名/加覆盖之前的同一命令是 1,097）。
- 严格审计（口径对齐 lead 的 `_scratch/audit_packs2.py`，同时覆盖 41 个 cfg 包 + 48 个 IL 包）：
  `89 文件 / 11,088 行 / 字段集合恒 {h,mod,n,s,t}（无 ctx 键）/ h==fnv1a64(normalize(s)) / n==normalize(s) /
  t 非空 / <<n>> 多重集相等 / 富文本标签多重集相等 / 跨文件同 h 同 t（0 冲突）/ s 不含 CJK / 无控制字符`
  → **problems 0、cross_file_h_conflicts 0、文件名与 mod 字段不一致 0**；`t==s` 92 行（cfg 88 + IL 4）。
- 质量抽查（节选）：`Select a mission to load`→选择要加载的任务、`A <#0080ff>simple</color> line of text.`→一行<#0080ff>简单</color>的文本。、
  `Cost: √{0:N0}`→成本：√{0:N0}、`Antennas/vessel: {0:F1}`→天线/载具：{0:F1}、`Time for Proficiency training varies\nbased on …`→
  专精训练耗时取决于\n航天员已有的专精熟练度、`dry masswet mass`→干重/湿重（源文自身缺分隔符）。
- 4 条 keep（G8 不过但无需求译，按 `t==s` 落库，运行时回退原文）：`Track IR`（Assembly-CSharp）、
  `Click-Through-Blocker`（ClickThroughBlocker）、`KSP Community Fixes`（KSPCommunityFixes）、`Textures Unlimited`（TexturesUnlimited）。
- `gated_ratio` 1 条（G6 下界 0.15，按 untranslated 处理：不写库、不重试、不放松门禁）：

  | h | mod | s | zh | ratio |
  |---|---|---|---|---|
  | `4c34b236aee63b39` | RP0 | `" From Now"` | 后 | 0.111 |

  1/1,100 = **0.09%**，远低于 lead 定的 1% 阈值 ⇒ 不启用动态下界。原先还有一条
  `Research & Development → 研发`（0.091），**已按 lead 裁定一走人工覆盖通道**（不靠括号补英文骗 G6，见 §11.13）；
  `" From Now"` **保持英文上屏**：它是带前导空格的拼接片段，`" 后"` 会拼出「3 后」这类残句，宁可不翻。
- `existing_h_skipped 69`（这些 `h` 已由 41 个 cfg 包覆盖，重复写 IL 行只会制造两份同 h 同 t 的冗余）/
  `keeps_no_cjk 0` / `il_conflict 0`（同 h 跨 mod 不同 t 的冲突为 0）/ `overrides_applied 4` / `override_rejected 0`。
- 最终态全仓（41 cfg + 48 IL）：**89 文件 / 11,089 行 / 3,685,087 B**；lead 的 `_scratch/audit_packs2.py`
  `files=89 (il=48) rows=11089 bytes=3685087 keep_rows=92 unique_h=11089 dup_h=0 dup_h_diff_t=0 bad_h=0 bad_n=0
  empty_t=0 bad_placeholder=0 bad_richtext=0 cjk_in_source=0 VERDICT: PASS`。

### 11.12 v1.2 待办（v1.1 不做的三件事，别忘）

| 桶 | v1.1 处置 | v1.2 必须补的条件 |
|---|---|---|
| `short_label` 429 | 不翻，保持英文 | **先按 `ilOffset`/`type`/`sink` 回查 IL 上下文再翻**；`Omega` 就是「短词离开上下文必错」的样本（轨道力学=升交点赤经 / PID 面板=自然频率 ω） |
| `template` 172 | 不翻 | 需模板匹配：先抽出 `{0}`/`{1:N0}` 等槽位骨架，再按槽位语义译，禁止整句自由翻译 |
| 捕获通道 `packs/capture-candidates.jsonl`（2,008 行） | 不翻，只交付工具 + 契约 + 一轮实证 | 等**真实游玩会话**的捕获数据；lead 探针那一轮含大量假串（`Zzz control alpha/beta`、`II`、`U`、`N`） |
| 带版本号的程序集名（`ModuleManager.4.2.3`） | **v1.1 已实现归一**（`asm_version_base`，见 §11.13） | 规则写死、不许靠猜：`asmBase` 去目录去 `.dll` 后，**尾部匹配 `(\.[0-9]+){2,}` 才整段剥掉**（`ModuleManager.4.2.3`→`ModuleManager`，`Foo.2` 保留）；换采集器 / 换语言 / 换 mod 集合时都必须沿用，否则 `packs/index.json` 会出现两套 mod 名 |

另：v1.2 重跑 41 个 cfg 包时应统一两条流水线对 G6 的处理（见 §11.5 分歧记录）。

### 11.13 程序集名归一规则 + 人工覆盖两处裁定（lead m01857）

**① 带版本号的程序集名归一到无版本（已实现，规则写死，不靠猜）**

- 规则：`asmBase` 取出后（去目录、去 `.dll`），**若尾部匹配 `(\.[0-9]+){2,}` 则整段剥掉**——≥2 段数字才算版本号，
  避免误伤 `Foo.2` 这种真名。
- 例：`ModuleManager.4.2.3.dll` → base `ModuleManager.4.2.3` → mod **`ModuleManager`**，文件 `ModuleManager.il.jsonl`；
  `ksp_plugin_adapter` / `Assembly-CSharp` / `B9-PWings-Fork` 一律原样。
- 实现：`asm_version_base()` + `_VERSION_SUFFIX_RE = re.compile(r"(?:\.[0-9]+){2,}$")`；`asm_base()` 末尾套用它，
  所以输出文件名、行内 `mod`、`by_mod` 三处自动一致。
- 报告字段 `asm_normalized`：`[{"asm":"ModuleManager.4.2.3.dll","base":"ModuleManager.4.2.3","mod":"ModuleManager"}]`
  （只记 base != mod 的，避免把 48 个正常程序集全塞进报告）。
  坑：必须在**原始行**上算，`load_candidates` 之后的 `mods` 已被归一，在归一后数据上算永远是空表（实测踩过）。
- 旧文件清理：新增 `--prune`（限跑/单桶时也允许删除本次结果里不存在的 `<mod>.il.jsonl`），
  否则改名后 `ModuleManager.4.2.3.il.jsonl` 会留在盘上变成两份同 h 包。本轮已删旧文件、新文件 3 行、行数不变。
- selftest 断言（7 条）：`ModuleManager.4.2.3`→`ModuleManager`、`Foo.2`→`Foo.2`（不剥）、`Foo.2.3.4`→`Foo`、
  `asm_base("ModuleManager.4.2.3.dll")`、无版本号原样、文件名 `ModuleManager.il.jsonl`、`asm_normalized` 只记 base != mod。

**② `Research & Development` 走人工覆盖，译法用「研发」**

- `packs/overrides.jsonl` 第 4 行：`{"en":"Research & Development","zh":"研发","note":"TestFlightCore 分类标签；G6 下界误拒（0.091），人工裁定短译正确，lead m01857 裁定"}`。
- 落点 `packs/zh-cn/TestFlightCore.il.jsonl:1`：`t=研发`，`mod=TestFlightCore`；`overrides_applied 4 / override_rejected 0`。
- **不写「研发（R&D）」**：括号补英文是为了骗过 G6 的字符比值，那是把门禁当敌人；overrides 通道的设计前提就是
  「长度比由人负责」，所以直接写最短正确译法。

**③ `" From Now"` 保持 gated_ratio（不覆盖）**

- 它是带前导空格的拼接片段，`" 后"` 会拼出「3 后」这类残句 ⇒ 宁可不翻、留英文上屏。





## 12. IL 候选分桶 + 捕获摄入（task-32）

### 12.1 改了什么文件

| 文件 | 变更 | 说明 |
|---|---|---|
| `tools/i18n_tools/buckets.py` | **新增** 7,130 B | 四档分桶 `translate / short_label / template / never` + 机器可读 `reason`（15 个原因码，顺序即优先级） |
| `tools/i18n_tools/iltranslatable.py` | **新增** ~26.7 KB | 四类扩展采集 X1–X4，产出 `packs/il-translatable.jsonl` |
| `tools/i18n_tools/ilstrings.py` | 修改（**默认行为不变**） | 加 `EXTRA_API_TABLE` / `EXTRA_CLASSIFY` / `Resolver.NESTED_TYPEREF` / `Analyzer` 三个扩展点（`_resolve_return_lit` / `_field_lit` / `_hit`）/ `scan_dll(need_no_ldstr=True)` |
| `tools/i18n_tools/capture_ingest.py` | **新增** ~25.4 KB | 捕获摄入 + 去重 + 剔覆盖 + 分桶，产出 `packs/capture-candidates.jsonl` |
| `tools/i18n_tools/selftest.py` | 修改 | **175 → 214 项**（ilstrings 合成 fixture + 真实产物 9 项 + capture_ingest 30 项 + 真实捕获产物 9 项） |
| `packs/il-translatable.jsonl` | **新增** | 1,746 行 / 539,514 B |
| `packs/capture-candidates.jsonl` | **新增** | 2,008 行 / 695,659 B |
| `reports/il-reachability.md` | 追加 §12（296–427 行）与 §13–15（431–582 行） | §12 分桶与可翻译子集、§13 三类不可达、§14 八串验收、§15 捕获 vs 静态重叠度 |
| `packs/il-candidates.jsonl` | **未动** | task-29 历史产物，按契约保留 |

### 12.2 实测数字

**钩子中性回归（先做，确保 task-29 可复现）**：加完所有扩展点后重跑 `python -m i18n_tools.ilstrings`，
输出与加钩子前**逐字节相同**（525,668 B）。与仓库里 `packs/il-candidates.jsonl` 只差 1 行，原因是
`GameData\Principia\ksp_plugin_adapter.dll` 的 LastWriteTime = 2026-10-23 13:11:04（在 task-29 扫描之后被更新过），不是代码变化（已写进报告 §12.6）。

**iltranslatable（静态扩展）**：131 个程序集；索引「单 return 字面量」方法 1,333 / 静态字段初值 1,729 / 类型基类关系 19,570；
阶段 A 1.33s + 阶段 B 23.96s = **25.29s**；命中 66,511 条 → 合并 **2,415 个 distinct 文本** → 产物 **1,746 行 / 539,514 字节 /
sha256 `9261933E033262DBF762D51B4AFA4543305D00FAAD286FC2BA057C66384CF8FD`**（连跑两次相同 = 幂等）。

四档（2,415）：`translate 1,145` / `short_label 429` / `template 172` / `never 669`。
reason 明细：sentence 1145、single_word 429、template_format 97、template_progress 75、already_cjk 144、keyref 92、no_letters 91、
identifier 82、too_short 66、weak_words 62、no_word 50、tags_only 38、format_only 36、path 8。

扩展归因（涉及 / 其中 never / 仅靠它才抓到 / 其中新增）：base 2,090 / 572 / 2,072 / 8；x1 89 / 16 / 88 / 88；x2 41 / 5 / 19 / 19；
x3 5 / 1 / 1 / 1；x4 231 / 84 / 195 / 195。**x1（补 `ScreenMessages.PostScreenMessage`）与 x4（`DialogGUI*` 构造）是主要增量。**

**八串验收（真机保持英文的串）**：**5/8 命中**（task-29 的 il-candidates 是 1/8）：
`Vessel mass`（RCSBuildAid.dll / GUILayout.DoToggle，靠 X2）、`All Input Locks Cleared`（ClickThroughBlocker.dll / ScreenMessages.PostScreenMessage，靠 X1）、
`Basic Options` + `Damage Options`（KSPWheel.dll / KSP.DialogGUIBox..ctor，靠 X2+X4）、`Debug Toolbar`（Assembly-CSharp.dll / GUILayout.DoWindow，base 就有）。
未命中 3 条属**原理不可达**：`Database` / `Performance`（枚举成员名，只活在 `#Strings` 堆）、`Reload Settings`（运行时拼接，ldstr 形态全 DLL 0 命中）。

**capture_ingest（round 8 真实捕获）**：读入 2,610 行（缺文件 0 / 坏行 0 / 坏记录 0 / h 不一致 0）→ 去重 2,610 → 已被包覆盖 **0** →
分桶 `never 602 / template 1,930 / candidate 78`（reason：path 578 / already-cjk 24 / progress 253 / counter 1,677 / 无 78）→
产物 **2,008 行 / 695,659 字节 / sha256 `1E1CA1135939902C40579DEE023AA1FB9E68C7F473913040E3D7F4F100F87672`**（连跑两遍逐字节相同）。

**重叠度（v1.1 净增量）**：捕获 2,610 ∩ pool 0、∩ zh-cn 包 0、∩ il-translatable 18、∩ il-candidates 14
→ **只有运行时捕获才看得到 2,592 / 2,610 = 99.31%**。产物 2,008 行 ∩ il-translatable 17、∩ il-candidates 13。

**自测**：`python -m i18n_tools.selftest` = **214 项全绿 EXIT=0**；`python -m i18n_tools.capture_ingest --selftest` = 30 项全绿。

### 12.3 已知限制（写下来，别当没有）

1. **`mods` 全 unknown**：round 8 捕获的 `caller` 全是补丁名（2,585 行 `TMP_Text.TMPro.TMP_Text.set_text_Patch1`），按契约不猜。要拿到 mod 归属，需 Runtime 侧在捕获里记 Assembly 名。
2. **`count` 不代表重要性**：`Zzz control beta` count 16,966 是某轮询 label 每帧刷新所致。
3. **path 口径差 16**：core 校准 path 562，我 578 —— 16 条带空格路径（`ROCapsules/PartConfigs/Apollo D2/D2Adapter/ROC-D2Adaptor`）。我选「宁可多判 path 少给翻译」；要严格同口径就把段字符集里的空格去掉。
4. **声明字段只在 10 字段捕获文件里采信**：round 8 的 7 字段文件全部自算。
5. **静态采集天花板 5/8**：枚举成员名、运行时拼接、特性载荷三类原理不可达（报告 §13），v1.1 主数据源必须是运行时捕获，IL 扫描降级为离线辅助。
6. **基线漂移**：Principia `ksp_plugin_adapter.dll` 在 2026-10-23 13:11 更新过，今天重跑 ilstrings 得 2,286 行（历史产物 2,285 行保持不动）。
7. **未 commit、未跑 packindex**：按 Lead 要求等统一跑（新产物 `packs/il-translatable.jsonl` 与 `packs/capture-candidates.jsonl` 一旦入包索引会改变 `packs/index.json` 的 totalRows/totalBytes）。
