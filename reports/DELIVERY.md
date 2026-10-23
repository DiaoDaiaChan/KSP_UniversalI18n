# T11 翻译流水线 — 交付说明

> 状态：四步流水线跑完、连跑两遍字节一致。最终包 41 文件 / **9,990 行** / 3,504,392 字节，
> `s` 以 `#` 开头的行 **22**（插入守卫拒绝的键行，故意保留，见 §9.2.1），契约校验 0 问题
> （Lead 的 audit_packs + 本侧加严探针）。
> 本文件只记录"怎么跑、怎么算、哪些是已知边界"，细节见 reports/translation-<ts>.json。

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
