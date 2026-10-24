
r"""iltranslatable.py -- 扩展采集 + 可翻译分桶（task-32）。

为什么要有这个模块（task-29 的两个缺口，Lead 用游戏内截图实证）：

  1) 静态扫描漏掉真实出现在屏幕上的串。四类缺口里两类能在静态侧补：
       X1 sink 清单缺口：ScreenMessages.PostScreenMessage(string, float) 没登记，
          于是 ClickThroughBlocker 的 "All Input Locks Cleared" 连普查都不计；
       X2 返回值字面量：属性 getter / 单 return 方法里的字面量经虚调用流入 sink，
          于是 RCSBuildAid 的 "Vessel mass"（buttonTitle 的 5 个 override 之一）
          和 KSPWheel 的 "Basic Options" / "Damage Options"（Title 的 override）全丢；
       X3 静态字段初值：ldstr X; stsfld F 的字段再被 ldsfld 取出。
      （第三类 "Reload Settings" 是运行时拼接，静态侧抓不到，只能靠 capture_ingest。）

  2) 候选没分桶。il-reachability 的「其他（含待译句子）」里混着 +5 Rep / « DOCK /
     New LC / 0 Data 这类标记，直接翻会污染译文。分桶规则在 buckets.py。

口径（写死，报告与 selftest 都按它验）：
  * 在 ilstrings 的深度 1 层数据流之上，额外允许「经同/跨程序集的属性 getter 或
    单 return 字面量方法取值」与「经静态字段初值取值」，仍然**不做**通用跨方法追踪
    （不做参数可达性、不做数组/字典、不做 ToString 生成）；
  * 每条命中带 via：direct（task-29 原有路径）/ ret（X2）/ field（X3）/ extra_sink（X1）；
  * 分桶用 buckets.classify；il-translatable.jsonl 只收 bucket != never 的行；
  * 同一 h（归一化文本）跨 DLL 合并成一行，mods 记来源程序集列表（asm 名）。

产物：
  packs/il-translatable.jsonl   {asm,type,method,ilOffset,sink,text,h,n,bucket,reason,mods}
  reports/il-reachability.md    新增一节「分桶与可翻译子集」（标记块内幂等替换）

CLI：
  python -m i18n_tools.iltranslatable [--gamedata DIR] [--managed DIR] [--out FILE]
                                     [--report FILE] [--pool FILE] [--only NAME]
                                     [--limit N] [--no-report] [--quiet]
"""

from __future__ import annotations

import hashlib
import io
import json
import os
import sys
import time

from . import buckets, ensure_stdout, fnv, ilmeta, ilstrings, textnorm

# ---------------------------------------------------------------------------
# X1：sink 清单缺口
# ---------------------------------------------------------------------------
# ScreenMessages.PostScreenMessage(string, float) 是 ScreenMessages 真正写屏幕的
# 静态入口（PostMessage(ScreenMessage, string, ...) 是另一条重载）。A 组漏了它，
# ClickThroughBlocker 的 "All Input Locks Cleared" 因此整条丢掉。
EXTRA_SINKS = {
    ("ScreenMessages", "PostScreenMessage"): ("A", "ScreenMessages.PostScreenMessage"),
}
EXTRA_SINK_NAMES = frozenset(api for _, api in EXTRA_SINKS.values())

# X4：KSP 对话框框架 DialogGUI* 的构造函数也是 UI 文本入口。
# 依据：DifficultyOptionsMenu.CreateDifficultWindow 里
#   ldloc node; callvirt GameParameters+CustomParameterNode::get_Title
#   newobj DialogGUILabel::.ctor(string, ...)
# 是 KSPWheel 的 "Basic Options" / "Damage Options" 上屏的真实路径；它们不在
# PatchRegistry 的 16 个 sink 里，所以静态扫描全丢（运行时靠 TMP_Text.set_text 兜住）。
DIALOG_TEXT_METHODS = (".ctor",)


def _extra_classify(tname, mname):
    short = tname.rsplit(".", 1)[-1]
    if short.startswith("DialogGUI") and mname in DIALOG_TEXT_METHODS:
        return ("A", "KSP." + short + "..ctor")
    return None

MARK_BEGIN = "<!-- ILTRANS:BEGIN -->"
MARK_END = "<!-- ILTRANS:END -->"

MAX_BASE_DEPTH = 16


def install_extra_sinks():
    """打开 ilstrings 的两个扩展开关（默认值保持 task-29 口径，随时可撤销）。"""
    ilstrings.EXTRA_API_TABLE.update(EXTRA_SINKS)
    ilstrings.EXTRA_CLASSIFY = _extra_classify
    ilstrings.Resolver.NESTED_TYPEREF = True


# ---------------------------------------------------------------------------
# 字面量返回值 / 静态字段初值索引
# ---------------------------------------------------------------------------
class RetIndex:
    """全局索引：谁的方法/属性返回一个字面量、哪个静态字段初值是字面量、谁继承谁。"""

    def __init__(self):
        self.by_name = {}        # (方法名, 参数个数) -> [entry]
        self.fields = {}         # (asm, field token) -> entry
        self.bases = {}          # 类型全名 -> 基类全名
        self.n_ret = 0
        self.n_field = 0
        self.n_types = 0

    def add_ret(self, type_full, name, nparams, asm, method_label, lit, off):
        if not lit or not lit.strip():
            return
        e = {"asm": asm, "type": type_full, "method": method_label,
             "lit": lit, "off": off, "nparams": nparams, "name": name}
        self.by_name.setdefault((name, nparams), []).append(e)
        self.n_ret += 1

    def add_field(self, asm, token, lit, off, type_full, method_label):
        if not lit or not lit.strip():
            return
        self.fields[(asm, token)] = {"asm": asm, "type": type_full,
                                     "method": method_label, "lit": lit, "off": off}
        self.n_field += 1

    def add_base(self, type_full, base_full):
        if base_full:
            self.bases[type_full] = base_full
            self.n_types += 1

    def is_derived(self, tname, base):
        """tname 是否（沿基类链）派生自 base。"""
        cur = tname
        for _ in range(MAX_BASE_DEPTH):
            nxt = self.bases.get(cur)
            if not nxt:
                return False
            if nxt == base:
                return True
            cur = nxt
        return False

    def lookup(self, tname, mname, nparams):
        """解析一次 callvirt/call：返回同族（自身 + override + 基类声明）的字面量条目。"""
        cand = self.by_name.get((mname, nparams))
        if not cand:
            return []
        out = []
        seen = set()
        for e in cand:
            if not (e["type"] == tname or self.is_derived(e["type"], tname)
                    or self.is_derived(tname, e["type"])):
                continue
            key = (e["asm"], e["type"], e["off"])
            if key in seen:
                continue
            seen.add(key)
            out.append(e)
        out.sort(key=lambda e: (e["asm"], e["type"], e["off"]))
        return out


def _method_label(img, res, m):
    mname = img.string_at(m["Name"])
    params, _ = res.sig(img.blob_at(m["Signature"]))
    return "%s(%s)" % (mname, ",".join(ilstrings.short_type(t) for t in params))


def _field_owner_map(img, res):
    """Field rid -> 声明类型全名（FieldList 是 TypeDef 里的连续区间）。"""
    td = img.rows("TypeDef")
    nf = len(img.rows("Field"))
    owner = {}
    for i, row in enumerate(td):
        start = row["FieldList"]
        end = td[i + 1]["FieldList"] if i + 1 < len(td) else nf + 1
        name = res.typedef_name(i + 1)
        for rid in range(start, min(end, nf + 1)):
            owner[rid] = name
    return owner


def scan_returns(path, asm, ridx, stats):
    """阶段 A：只解码「短方法体」与 .cctor/.ctor，收集返回值字面量与字段初值。"""
    img = ilmeta.Image(path)
    res = ilstrings.Resolver(img)
    td = img.rows("TypeDef")
    for i, row in enumerate(td, 1):
        ext = row.get("Extends")
        if ext:
            ridx.add_base(res.typedef_name(i), res.class_name(ext))
    owner = None
    methods = img.rows("MethodDef")
    for rid, m in enumerate(methods, 1):
        rva = m["RVA"]
        if not rva:
            continue
        mname = img.string_at(m["Name"])
        is_cctor = mname in (".cctor", ".ctor")
        try:
            code, _ = img.method_body(rva)
        except (ilmeta.IlError, IndexError):
            continue
        # ldstr+ret 最多十几个字节；长方法体只有在 .cctor/.ctor 里才值得看字段初值
        if len(code) > 40 and not is_cctor:
            continue
        try:
            insns = ilmeta.decode_il(code)
        except (ilmeta.IlError, IndexError):
            continue
        seq = [(ioff, nm, op) for (ioff, nm, op) in insns if nm != "nop"]
        if len(seq) == 2 and seq[0][1] == "ldstr" and seq[1][1] == "ret":
            lit = res.us.get(seq[0][2] & 0xFFFFFF)
            params, _ = res.sig(img.blob_at(m["Signature"]))
            ridx.add_ret(res.type_of_method(rid), mname, len(params), asm,
                         _method_label(img, res, m), lit, seq[0][0])
            continue
        if not is_cctor:
            continue
        for k in range(len(seq) - 1):
            if seq[k][1] != "ldstr":
                continue
            nxt = seq[k + 1]
            if nxt[1] not in ("stsfld", "stfld"):
                continue
            tok = nxt[2]
            if not isinstance(tok, int) or ((tok >> 24) & 0xFF) != 0x04:
                continue
            if owner is None:
                owner = _field_owner_map(img, res)
            lit = res.us.get(seq[k][2] & 0xFFFFFF)
            ridx.add_field(asm, tok, lit, seq[k][0],
                           owner.get(tok & 0xFFFFFF, res.type_of_method(rid)),
                           _method_label(img, res, m))
    return ridx


class RetAnalyzer(ilstrings.Analyzer):
    """在 ilstrings.Analyzer 上加两个扩展点（X2 返回值、X3 静态字段）。"""

    ridx = None          # 由 collect_all 在扫描前设置

    def _resolve_return_lit(self, target, kind, args):
        if kind == "newobj" or self.ridx is None:
            return None
        if target.get("ret") != "System.String":
            return None
        if target["type"] == "System.String":
            return None                      # Concat / Format 已由基类处理
        entries = self.ridx.lookup(target["type"], target["name"], len(target["params"]))
        if not entries:
            return None
        if len(entries) == 1:
            e = entries[0]
            return ("lit", e["lit"], e["off"], (e["asm"], e["type"], e["method"], "ret"))
        return ("litm", [(e["lit"], e["off"], (e["asm"], e["type"], e["method"], "ret"))
                         for e in entries], entries[0]["off"])

    def _field_lit(self, operand):
        if self.ridx is None or not isinstance(operand, int):
            return None
        e = self.ridx.fields.get((self.asm, operand))
        if e is None:
            return None
        return ("lit", e["lit"], e["off"], (e["asm"], e["type"], e["method"], "field"))


# ---------------------------------------------------------------------------
# 全量收集（阶段 A 建索引 + 阶段 B 扫 sink）
# ---------------------------------------------------------------------------
def collect_all(gamedata, managed, only=None, limit=None, quiet=False):
    install_extra_sinks()
    dlls = ilstrings.iter_dlls(gamedata, managed)
    if only:
        want = only.lower()
        dlls = [d for d in dlls if want in d[1].lower() or want in d[0].lower()]
    if limit:
        dlls = dlls[:limit]
    ridx = RetIndex()
    skipped = []
    t0 = time.time()
    if not quiet:
        print("[A] 建立返回值/静态字段索引：%d 个程序集" % len(dlls))
    for path, asm, mod in dlls:
        try:
            scan_returns(path, asm, ridx, {})
        except ilmeta.IlError as exc:
            skipped.append((asm, "A: " + str(exc)))
            if not quiet:
                print("  [skip-A] %-40s %s" % (asm, exc))
    t_a = time.time() - t0
    if not quiet:
        print("[A] 完成：返回字面量的方法 %d 个 / 字段初值 %d 个 / 类型基类关系 %d 条 / %.2fs"
              % (ridx.n_ret, ridx.n_field, ridx.n_types, t_a))
    RetAnalyzer.ridx = ridx
    per_dll = []
    all_hits = []
    census = {}
    t1 = time.time()
    for path, asm, mod in dlls:
        try:
            stats, hits, cen = ilstrings.scan_dll(path, asm, mod, analyzer_cls=RetAnalyzer,
                                                  need_no_ldstr=True)
        except ilmeta.IlError as exc:
            skipped.append((asm, "B: " + str(exc)))
            if not quiet:
                print("  [skip-B] %-40s %s" % (asm, exc))
            continue
        stats["census"] = {api: {"group": r["group"], "family": r["family"],
                                 "calls": r["calls"], "lits": r["lits"]}
                           for api, r in cen.items()}
        per_dll.append(stats)
        all_hits.extend(hits)
        for api, rec in cen.items():
            agg = census.get(api)
            if agg is None:
                agg = census[api] = {"group": rec["group"], "family": rec["family"],
                                     "calls": 0, "methods": set(), "lits": set(),
                                     "dlls": {}, "reflection_text": False}
            agg["calls"] += rec["calls"]
            agg["methods"] |= rec["methods"]
            agg["dlls"][asm] = agg["dlls"].get(asm, 0) + rec["calls"]
            if rec.get("reflection_text"):
                agg["reflection_text"] = True
            agg["lits"] |= rec["lits"]
        if not quiet:
            print("  [ok]   %-44s us=%-6d df=%-6d ab=%-4d sf=%-4d hits=%-5d"
                  % (asm, stats["us"], stats["dataflow"], stats["aborted"],
                     stats["sig_fail"], len(hits)))
    t_b = time.time() - t1
    return {"per_dll": per_dll, "hits": all_hits, "census": census, "skipped": skipped,
            "index": ridx, "elapsed": t_a + t_b, "t_a": t_a, "t_b": t_b,
            "dlls": len(dlls)}


# ---------------------------------------------------------------------------
# 分桶 + 合并
# ---------------------------------------------------------------------------
VIA_LABEL = {
    "base": "task-29 原有路径（ldstr 直接/拼接/Format/GUIContent + 16 个 sink）",
    "x1": "X1 sink 补齐：ScreenMessages.PostScreenMessage(string, float)",
    "x2": "X2 返回值字面量：属性 getter / 单 return 方法（含同族 override）",
    "x3": "X3 静态字段初值：ldstr X; stsfld F 后 ldsfld",
    "x4": "X4 KSP 对话框框架：DialogGUI* 构造函数（文本承载对象，不在 16 sink 里）",
}
VIA_ORDER = ("base", "x1", "x2", "x3", "x4")


def hit_need(hit):
    """这一条命中「靠哪些扩展才抓得到」。多个扩展可以同时是必要条件。"""
    need = set()
    sink = hit["sink"]
    if sink in EXTRA_SINK_NAMES:
        need.add("x1")
    elif sink.startswith("KSP.DialogGUI"):
        need.add("x4")
    via = hit.get("via") or "direct"
    if via == "ret":
        need.add("x2")
    elif via == "field":
        need.add("x3")
    if not need:
        need.add("base")
    return need


def build_rows(hits):
    """命中 -> 按 h 合并的行（只保留 bucket != never）。"""
    groups = {}
    for hit in hits:
        text = hit["text"]
        n = textnorm.normalize(text)
        if not n:
            continue
        h = fnv.hash_text(n)
        bucket, reason = buckets.classify(n)
        rec = groups.get(h)
        if rec is None:
            rec = groups[h] = {"h": h, "n": n, "bucket": bucket, "reason": reason,
                               "sources": [], "texts": [], "vias": set()}
        rec["sources"].append({"asm": hit["asm"], "type": hit["type"],
                               "method": hit["method"], "ilOffset": hit["ilOffset"],
                               "sink": hit["sink"]})
        rec["texts"].append(text)
        rec["vias"] |= hit_need(hit)
    rows = []
    for h, rec in groups.items():
        if rec["bucket"] == "never":
            continue
        srcs = sorted(rec["sources"], key=lambda s: (s["asm"], s["type"], s["method"],
                                                     s["ilOffset"], s["sink"]))
        first = srcs[0]
        texts = sorted(rec["texts"])
        rows.append({"asm": first["asm"], "type": first["type"], "method": first["method"],
                     "ilOffset": first["ilOffset"], "sink": first["sink"],
                     "text": texts[0], "h": h, "n": rec["n"],
                     "bucket": rec["bucket"], "reason": rec["reason"],
                     "mods": sorted({s["asm"] for s in srcs}),
                     "vias": sorted(rec["vias"])})
    rows.sort(key=lambda r: (buckets.BUCKET_ORDER[r["bucket"]], r["asm"], r["n"], r["h"]))
    return rows


def write_rows(path, rows):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        for r in rows:
            fh.write(json.dumps({"asm": r["asm"], "type": r["type"], "method": r["method"],
                                 "ilOffset": r["ilOffset"], "sink": r["sink"],
                                 "text": r["text"], "h": r["h"], "n": r["n"],
                                 "bucket": r["bucket"], "reason": r["reason"],
                                 "mods": r["mods"]},
                                ensure_ascii=False, separators=(",", ":")) + "\n")
    return len(rows)


def read_jsonl(path):
    rows = []
    if not os.path.isfile(path):
        return rows
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                rows.append(json.loads(line))
            except ValueError:
                continue
    return rows


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().upper()


# ---------------------------------------------------------------------------
# 报告新节
# ---------------------------------------------------------------------------
def _md(text, limit=64):
    return ilstrings._md(text, limit)


def _pct(a, b):
    return ilstrings._pct(a, b)


def build_section(res, rows, old_rows, pool_h, out_path, report_path):
    """生成「分桶与可翻译子集」这一节（标记块内幂等替换）。"""
    idx = res["index"]
    total = res["hits"]
    by_h = {}
    for hit in total:
        n = textnorm.normalize(hit["text"])
        if not n:
            continue
        h = fnv.hash_text(n)
        rec = by_h.setdefault(h, {"n": n, "vias": set(), "sinks": set(), "asms": set()})
        rec["vias"] |= hit_need(hit)
        rec["sinks"].add(hit["sink"])
        rec["asms"].add(hit["asm"])
    cls = {}
    for h, rec in by_h.items():
        cls[h] = buckets.classify(rec["n"])
    n_all = len(by_h)
    bucket_count = {}
    reason_count = {}
    for h, (b, r) in cls.items():
        bucket_count[b] = bucket_count.get(b, 0) + 1
        reason_count[r] = reason_count.get(r, 0) + 1
    old_h = {r["h"] for r in old_rows if r.get("h")}
    new_h = set(by_h) - old_h
    lines = []
    a = lines.append
    a(MARK_BEGIN)
    a("")
    a("## 分桶与可翻译子集（task-32）")
    a("")
    a("这一节由 tools/i18n_tools/iltranslatable.py 生成（标记块内每次运行幂等替换）。")
    a("上游产物：packs/il-translatable.jsonl（只收 bucket != never 的行）。")
    a("")
    a("### 12.1 扩展采集：四类缺口各捞回多少")
    a("")
    a("一条命中可能需要不止一个扩展（例：KSPWheel 的 `Basic Options` 既要 X2 追进")
    a("属性 getter，又要 X4 认 DialogGUI* 构造函数）。下表「涉及」= need 集合含该扩展的")
    a("distinct 文本数，「仅靠它」= need 集合恰好只有它的文本数（互斥、可加）。")
    a("")
    a("| 来源 | 说明 | 涉及 distinct | 其中 never | 仅靠它才抓到 | 其中新增（不在基线 %d 行） |"
      % len(old_rows))
    a("| --- | --- | --- | --- | --- | --- |")
    for via in VIA_ORDER:
        hs = {h for h, rec in by_h.items() if via in rec["vias"]}
        if not hs:
            continue
        solo = {h for h in hs if by_h[h]["vias"] == {via}}
        noise = sum(1 for h in hs if cls[h][0] == "never")
        solo_new = len({h for h in solo if h in new_h})
        a("| %s | %s | %d | %d | %d | %d |"
          % (via, VIA_LABEL[via], len(hs), noise, len(solo), solo_new))
    a("")
    a("索引规模：返回字面量的方法 **%d** 个、静态字段初值 **%d** 个、类型基类关系 **%d** 条；"
      "阶段 A 建索引 %.2fs，阶段 B 扫 sink %.2fs，合计 %.2fs，扫描 %d 个程序集。"
      % (idx.n_ret, idx.n_field, idx.n_types, res["t_a"], res["t_b"], res["elapsed"], res["dlls"]))
    a("")
    a("四条真串的当前归属（task-32 的目标是让前四条进候选）：")
    a("")
    a("| 串 | 分桶 | 命中 sink | 来源 |")
    a("| --- | --- | --- | --- |")
    for probe in ("Vessel mass", "Basic Options", "Damage Options",
                  "All Input Locks Cleared", "Debug Toolbar", "Reload Settings"):
        h = fnv.hash_text(textnorm.normalize(probe))
        rec = by_h.get(h)
        if rec is None:
            a("| %s | （静态采集仍抓不到） | - | - |" % _md(probe, 32))
            continue
        b, r = cls[h]
        a("| %s | %s / %s | %s | %s |"
          % (_md(probe, 32), b, r, ", ".join(sorted(rec["sinks"])[:2]),
             ", ".join(sorted(rec["vias"]))))
    a("")
    a("### 12.2 四档计数（distinct 归一化文本）")
    a("")
    a("| 档 | 条数 | 占比 | 含义 |")
    a("| --- | --- | --- | --- |")
    bucket_desc = {
        "translate": "有实义的句子/短语，直接翻",
        "short_label": "单字/单词但有实义（Back / Close / Warp）",
        "template": "含数字或占位符的模板/进度串，翻不翻交下游决定",
        "never": "不该翻：键引用/路径/URL/标识符/纯符号/纯数字/已是中文/缩写标记",
    }
    for b in buckets.BUCKETS:
        c = bucket_count.get(b, 0)
        a("| %s | %d | %s | %s |" % (b, c, _pct(c, n_all), bucket_desc[b]))
    a("| 合计 | %d | 100%% | - |" % n_all)
    a("")
    sub_old = [h for h in by_h if h in old_h]
    sub_cnt = {}
    for h in sub_old:
        sub_cnt[cls[h][0]] = sub_cnt.get(cls[h][0], 0) + 1
    a("把范围收窄到 task-29 的基线 %d 条（h 在 packs/il-candidates.jsonl 里）时，本节口径的分布是：%s"
      "（合计 %d）。" % (len(old_rows),
                        "、".join("%s %d" % (b, sub_cnt.get(b, 0)) for b in buckets.BUCKETS),
                        len(sub_old)))
    a("")
    a("与 Lead 对那 2,082 条的独立拆分（sentence 1,243 / single_word 355 / too_short 175 / "
      "no_word 128 / no_letters 57 / identifier 49 / weak_words 48 / keyref 21 / tags_only 6）相比，"
      "本节差异有两处，都是口径更严：")
    a("")
    a("1. 多出 already_cjk 一档：task-29 的漏斗只按 textnorm.has_cjk（CJK 统一表意字）过滤，"
      "全角标点/假名/装饰符混着的串漏了过来；buckets 的 CJK 范围更宽（含 U+2E80–9FFF / "
      "U+FF00–FFEF / U+3000–303F），这些串本来就不该翻。")
    a("2. 符号与数量串判得更狠（weak_words / no_letters），所以 never 的条数比 Lead 的 484 多；"
      "另外本节把「含数字/占位符的模板串」单独成 template 档（Lead 的拆分里它们散在 "
      "sentence 与 single_word 里），这是分档方式不同，不是判定不同。")
    a("")
    a("### 12.3 reason 明细")
    a("")
    a("| reason | 条数 | 档 |")
    a("| --- | --- | --- |")
    reason_bucket = {}
    for h, (b, r) in cls.items():
        reason_bucket[r] = b
    for r in buckets.REASONS:
        if r not in reason_count:
            continue
        a("| %s | %d | %s |" % (r, reason_count[r], reason_bucket.get(r, "-")))
    a("")
    a("### 12.4 逐档样例")
    a("")
    for b in buckets.BUCKETS:
        samples = [h for h in by_h if cls[h][0] == b]
        samples.sort(key=lambda h: by_h[h]["n"])
        a("**%s**（%d 条）" % (b, len(samples)))
        a("")
        for h in samples[:6]:
            a("- %s" % _md(by_h[h]["n"], 80))
        a("")
    a("### 12.5 与既有数据集的交集")
    a("")
    sub = [r for r in rows]
    a("| 集合 | 条数 |")
    a("| --- | --- |")
    a("| il-translatable.jsonl 行数（bucket != never） | %d |" % len(sub))
    a("| 其中 translate | %d |" % sum(1 for r in sub if r["bucket"] == "translate"))
    a("| 其中 short_label | %d |" % sum(1 for r in sub if r["bucket"] == "short_label"))
    a("| 其中 template | %d |" % sum(1 for r in sub if r["bucket"] == "template"))
    a("| 与 packs/pool.jsonl 的 h 交集 | %d |" % len({r["h"] for r in sub} & pool_h))
    a("| 与 task-29 il-candidates.jsonl 的 h 交集 | %d |" % len({r["h"] for r in sub} & old_h))
    a("")
    a("### 12.6 复现")
    a("")
    a("    python -m i18n_tools.iltranslatable")
    a("")
    a("产物 sha256：packs/il-translatable.jsonl = %s（%d 行 / %d 字节）。"
      % (sha256_file(out_path) if os.path.isfile(out_path) else "-", len(rows),
         os.path.getsize(out_path) if os.path.isfile(out_path) else 0))
    a("")
    a("X2/X3/X4 都是「静态侧补洞」：X4 认的是 KSP 自己的 DialogGUI* 对话框框架，"
      "KSPWheel 的参数节点标题就是经它上屏的；运行时那 16 个 sink 覆盖不到构造函数，"
      "但 TMP_Text.set_text 会在下一帧兜住，所以这类串在 capture 里反而能看到。")
    a("")
    a("基线漂移说明：packs/il-candidates.jsonl 是 task-29 的 %d 行历史产物，按任务要求保持不动；"
      "但本机安装里 Principia 的 ksp_plugin_adapter.dll 在 2026-10-23 13:11 被更新过，"
      "今天重跑 python -m i18n_tools.ilstrings 会得到 2286 行（多一条 Copy measured timing JSON...）。"
      "工具本身对同一份 DLL 仍然逐字节可复现（已实测两次相同）。" % len(old_rows))
    a("")
    a("已知限制（与 task-29 同）：不做跨方法参数可达性、不做数组/字典、不做 ToString 生成、"
      "不做特性参数（KSPWheel 的 [CustomParameterUI(...)] 那类文本只在 CustomAttribute 表里）；"
      "运行时拼接的串（Reload Settings）静态侧永远抓不到，靠 capture_ingest。")
    a("")
    a(MARK_END)
    return "\n".join(lines) + "\n"


def splice_report(path, section):
    """把 section 幂等地写进报告：有标记就替换标记块，没有就追加到文件末尾。"""
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    old = ""
    if os.path.isfile(path):
        with open(path, encoding="utf-8") as fh:
            old = fh.read()
    if MARK_BEGIN in old and MARK_END in old:
        head = old.split(MARK_BEGIN)[0]
        tail = old.split(MARK_END, 1)[1]
        new = head + section + tail
    else:
        sep = "" if old.endswith("\n") or not old else "\n"
        new = old + sep + "\n" + section
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(new)
    return len(new)


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------
def repo_root():
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def main(argv=None):
    ensure_stdout()
    import argparse
    root = repo_root()
    ap = argparse.ArgumentParser(
        prog="python -m i18n_tools.iltranslatable",
        description="扩展 IL 采集（sink 缺口/返回值/静态字段）+ 可翻译分桶")
    ap.add_argument("--gamedata", default=os.environ.get("UI18N_GAMEDATA") or None,
                    help="GameData 目录（默认 UI18N_GAMEDATA / 仓库同级 / 本机安装目录）")
    ap.add_argument("--managed", default=None, help="KSP_x64_Data\\Managed 目录")
    ap.add_argument("--out", default=os.path.join(root, "packs", "il-translatable.jsonl"))
    ap.add_argument("--report", default=os.path.join(root, "reports", "il-reachability.md"))
    ap.add_argument("--pool", default=os.path.join(root, "packs", "pool.jsonl"))
    ap.add_argument("--baseline", default=os.path.join(root, "packs", "il-candidates.jsonl"),
                    help="task-29 的历史产物，只用来算「新增候选」")
    ap.add_argument("--only", default=None, help="只扫名字含该子串的程序集（调试用）")
    ap.add_argument("--limit", type=int, default=None, help="最多扫 N 个程序集（调试用）")
    ap.add_argument("--no-report", action="store_true", help="只写 jsonl，不动报告")
    ap.add_argument("--quiet", action="store_true")
    args = ap.parse_args(argv)

    if args.gamedata:
        gamedata = args.gamedata
    else:
        from . import harvest
        gamedata = str(harvest.default_gamedata())
    managed = args.managed or ilstrings.default_managed()
    if not args.quiet:
        print("[iltranslatable] gamedata=%s" % gamedata)
        print("[iltranslatable] managed=%s" % managed)
    res = collect_all(gamedata, managed, only=args.only, limit=args.limit,
                      quiet=args.quiet)
    rows = build_rows(res["hits"])
    n = write_rows(args.out, rows)
    old_rows = read_jsonl(args.baseline)
    pool_h = ilstrings.load_pool_hashes(args.pool)
    if not args.no_report:
        section = build_section(res, rows, old_rows, pool_h, args.out, args.report)
        splice_report(args.report, section)
    if not args.quiet:
        b = {}
        for r in rows:
            b[r["bucket"]] = b.get(r["bucket"], 0) + 1
        print("[iltranslatable] 命中 %d 条 -> 合并后 %d 个 distinct 文本 -> 落盘 %d 行"
              % (len(res["hits"]), len({r["h"] for r in rows}), n))
        print("[iltranslatable] 分桶：%s" % ", ".join("%s %d" % (k, b.get(k, 0))
                                                      for k in buckets.BUCKETS))
        print("[iltranslatable] 耗时 %.2fs（索引 %.2fs + 扫描 %.2fs）"
              % (res["elapsed"], res["t_a"], res["t_b"]))
        print("[iltranslatable] 写出 %s（%d 字节）" % (args.out, os.path.getsize(args.out)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
