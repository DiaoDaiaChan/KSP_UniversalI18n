r"""capture_ingest.py -- 运行时捕获摄入：capture-*.jsonl -> packs/capture-candidates.jsonl（task-32 交付 3）

为什么有这个工具
----------------
静态 IL 采集（ilstrings.py / iltranslatable.py）只能看见「ldstr 直接流向 sink」的串；
运行时拼接（"Reload " + 名字）、特性参数、字典取值、跨程序集基类渲染这些形态静态侧抓不到。
所以 v1.1 的主数据源改成**运行时捕获**：插件把「屏幕上出现但字典里没有」的字符串写进
<KSP 根>\PluginData\UI18n\capture-yyyyMMdd-HHmmss.jsonl。本工具把那些文件合并、去重、
剔掉已经翻好的、按同一套规则分桶，产出可翻译候选。

输入 schema（两种形态都要能读）
------------------------------
旧（round 8 及以前，7 字段）：
    {"h","n","s","sink","caller","count","firstSeen"}
新（round 9 起，10 字段）：
    上面 7 个 + {"template":bool, "reason":string, "bucket":string}
    其中 reason in {path, progress, counter, ""}，bucket in {never, template, candidate}

**不许假设新文件一定带那三个字段**：带了且取值合法就采信（运行时与采集器看到的是同一份
上下文，比离线重算更准），缺了或非法就自己按同一套规则算一遍（见 classify_capture）。

三档 vocabulary
---------------
    never     -- 不该翻（路径 / 已含中文 / 纯标点 / 标识符 / 空 / 过短 / 过长）
    template  -- 含数字或占位符的进度、计数、格式串（保留数据，交下游决定怎么处理）
    candidate -- 其余，即真正可翻的
注意它和 IL 侧 buckets.py 的四档不是一套：那里的 translate + short_label 在这里合并成
candidate（运行时只看「要不要翻」，不区分句子和按钮词）。

输出 schema（固定字段顺序）
--------------------------
    {"h","n","s","sink","caller","count","firstSeen","lastSeen","sessions",
     "callers","bucket","reason","mods"}
    lastSeen  -- 该 h 在最晚一次会话里的时间戳（跨会话合并用）
    sessions  -- 出现过的会话文件数（>=2 就是「跨会话反复 miss」，优先级最高）
    callers   -- 去重后的 caller 列表，Ordinal 排序、最多 5 个，**非 unknown 优先**
    （count 跨会话求和，firstSeen 取最早，sink 取众数）
排序 (bucket 序, h)；同一 bucket 内按 h 升序。**没有改成一维 h 升序**：selftest.py:664 对真实产物
有「排序 = (bucket 序, h)」的既有断言，那条断言不在本模块 scope 内，改序会把它打红。
无 BOM、LF；同一批输入连跑两遍逐字节相同（幂等）。

跨会话合并的两条硬规则
----------------------
1. **同一 h 在不同会话被判成不同 bucket 时取更保守的**：never > template > candidate。
   一次判 never 就是一份「这一档不该翻」的实测证据，宁可少给翻译。
2. **离线重算判 never 的串一律不进产物**（offline_veto）：采集器声明与离线
   classify_capture 冲突时以保守的为准 —— 这正是 selftest.py:669 对真实产物的断言
   （"没有本该 never 的串混进产物"）要求的口径。

用法（在 tools 目录下）
-----------------------
    python -m i18n_tools.capture_ingest
    python -m i18n_tools.capture_ingest --all "D:\Kerbal Space Program_newmod\PluginData\UI18n"
    python -m i18n_tools.capture_ingest --dir <目录>        # --dir 与 --all 等价
    python -m i18n_tools.capture_ingest --all <目录> --report ..\reports\capture-triage.md
    python -m i18n_tools.capture_ingest --all <目录> --buckets candidate
    python -m i18n_tools.capture_ingest cap-a.jsonl cap-b.jsonl --out ..\packs\capture-candidates.jsonl
    python -m i18n_tools.capture_ingest --selftest
"""

from __future__ import annotations

import argparse
import glob
import hashlib
import json
import os
import re
import sys

from . import ensure_stdout, fnv, textnorm

SCHEMA_FIELDS = ("h", "n", "s", "sink", "caller", "count", "firstSeen")
DECLARED_FIELDS = ("template", "reason", "bucket")
OUT_FIELDS = ("h", "n", "s", "sink", "caller", "count", "firstSeen",
              "lastSeen", "sessions", "callers", "bucket", "reason", "mods")

BUCKETS = ("candidate", "template", "never")
BUCKET_ORDER = {"candidate": 0, "template": 1, "never": 2}
# 合并时「取更保守」的优先级（与 BUCKET_ORDER 相反：never 最保守、candidate 最宽）
CONSERVATIVE = {"never": 0, "template": 1, "candidate": 2}
MAX_CALLERS = 5          # 每个 h 最多保留几个 caller（够定位宿主，又不让行变胖）
OUTPUT_BUCKETS = ("candidate", "template")   # 允许进产物的档；never 一律不进

# 原因码：顺序即短路优先级（与运行时分类器的口径一致，见模块 docstring）
REASONS = ("empty", "too-short", "too-long", "blank", "path", "already-cjk",
           "progress", "counter", "punct", "identifier", "")
REASON_ORDER = ("path", "already-cjk", "progress", "counter", "punct",
                "identifier", "too-short", "too-long", "blank", "empty", "")

# 运行时允许出现在捕获文件里的 reason 取值；declared 字段只在这个集合里采信
DECLARED_REASONS = ("path", "progress", "counter", "")

MIN_LEN = 2          # 短于 2 个字符一律 never/too-short（与 harvest 的 too-short 同阈值）
MAX_LEN = 300        # 超长多半是整段日志/序列化数据，判 never/too-long

# 路径：整串必须是 a/b/c 形态（>=3 段）。段内允许空格/加号/逗号（mod 目录名里真的会有：
# "ROCapsules/PartConfigs/Apollo D2/D2Adapter/ROC-D2Adaptor"），但段首尾不许有空格
# （否则 "Cheats / Physics / Debug" 这类用斜杠分隔的标题会被误判成路径）。
# 与 core-engineer 的离线校准（path=562）差 16 条：那 16 条正是带空格的路径，他那边算
# candidate，我这边算 never —— 我选宁可少给翻译；要严格同口径就把段字符集里的空格去掉。
_SEG = r"[A-Za-z0-9_.\-+,](?:[A-Za-z0-9_.\-+, ]*[A-Za-z0-9_.\-+,])?"
_PATH_RE = re.compile(r"^" + _SEG + r"(?:[/\\]" + _SEG + r"){2,}$")
_PROGRESS_RE = re.compile(r"[0-9]+\s*/\s*[0-9]+")
_DIGIT_RE = re.compile(r"[0-9]")
_ALNUM_RE = re.compile(r"[0-9A-Za-z]")
_CJK_RE = re.compile(u"[\u2e80-\u9fff\uf900-\ufaff\uff00-\uffef\u3000-\u303f]")
_UNDERSCORE_RE = re.compile(r"^[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+$")
_DOTTED_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+$")
_CAMEL_RE = re.compile(r"^[a-z]+[A-Z][A-Za-z0-9]*$")
_CAPS_RE = re.compile(r"^[A-Z][A-Z0-9]{1,7}$")
_PATCH_SUFFIX_RE = re.compile(r"_Patch[0-9]+$")
# 这些是 Unity / KSP 自己的类型名，从 caller 首段取到的它们不是 mod
_API_ROOTS = frozenset(("gui", "guilayout", "guistyle", "tmpro", "tmp_text", "tmp_inputfield",
                        "screenmessages", "unityengine", "unity", "ksp", "ksp.ui", "system"))
_IDENT_SEG_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")


# ---------------------------------------------------------------------------
# 分类（与运行时同一套规则）
# ---------------------------------------------------------------------------
def _looks_identifier(n):
    """标识符形态：下划线连接 / 点分 / 小驼峰 / 全大写缩写（<=8 字符且无空格）。"""
    if _UNDERSCORE_RE.match(n) or _DOTTED_RE.match(n):
        return True
    if _CAMEL_RE.match(n):
        return True
    if " " not in n and _CAPS_RE.match(n):
        return True
    return False


def classify_capture(text):
    """运行时捕获文本 -> (bucket, reason)。短路顺序见 REASONS。

    顺序上有两处刻意的安排：
      1. path 排在 already-cjk 之前、也排在数字之前 —— 否则 ReStockPlus/Parts/... 这种
         含数字的路径会被判成 template/counter；
      2. progress（数字/数字）排在 counter（含数字）之前 —— 前者信息更具体。
    """
    n = textnorm.normalize(text)
    if not n:
        return ("never", "empty")
    if len(n) < MIN_LEN:
        return ("never", "too-short")
    if len(n) > MAX_LEN:
        return ("never", "too-long")
    if not n.strip():
        return ("never", "blank")
    if _PATH_RE.match(n):
        return ("never", "path")
    if _CJK_RE.search(n):
        return ("never", "already-cjk")
    if _PROGRESS_RE.search(n):
        return ("template", "progress")
    if _DIGIT_RE.search(n):
        return ("template", "counter")
    if not _ALNUM_RE.search(n):
        return ("never", "punct")
    if _looks_identifier(n):
        return ("never", "identifier")
    return ("candidate", "")


def declared_bucket(row):
    """读捕获文件自带的 bucket/reason/template；不可用返回 None（交给 classify_capture）。"""
    bucket = row.get("bucket")
    reason = row.get("reason")
    if isinstance(bucket, str) and bucket in BUCKETS:
        if not isinstance(reason, str) or reason not in DECLARED_REASONS:
            reason = "" if bucket != "never" else "path"
        return (bucket, reason)
    tpl = row.get("template")
    if isinstance(tpl, bool):
        return ("template" if tpl else "candidate", "")
    return None


# ---------------------------------------------------------------------------
# 读入 / 合并
# ---------------------------------------------------------------------------
def new_stats():
    return {
        "files": 0, "missing_files": 0, "bad_lines": 0, "bad_rows": 0,
        "h_mismatch": 0, "rows_in": 0, "distinct": 0,
        "covered_pack": 0, "covered_pool": 0, "covered": 0,
        "never": 0, "template": 0, "candidate": 0,
        "declared_used": 0, "computed": 0, "offline_veto": 0,
        "sessions": 0, "multi_session": 0,
        "out_rows": 0, "out_bytes": 0,
        "reasons": {},
        "per_file": [],
    }


def read_capture(path, stats):
    """读一个捕获文件；缺文件/坏行只记账，不抛。返回行列表（保持文件内顺序）。"""
    rows = []
    if not os.path.isfile(path):
        stats["missing_files"] += 1
        return rows
    stats["files"] += 1
    with open(path, encoding="utf-8", errors="replace", newline="") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except ValueError:
                stats["bad_lines"] += 1
                continue
            if not isinstance(obj, dict):
                stats["bad_rows"] += 1
                continue
            h = obj.get("h")
            n = obj.get("n")
            if not isinstance(h, str) or not h.strip() or not isinstance(n, str):
                stats["bad_rows"] += 1
                continue
            expect = fnv.hash_text(textnorm.normalize(n))
            if expect and h.strip().lower() != expect:
                # h 与 n 不同口径的行照样收（以文件里的 h 为准），只记账；
                # 全部 mismatch 说明采集器的 h 口径变了，必须让调用方看见。
                stats["h_mismatch"] += 1
            rows.append(obj)
    stats["rows_in"] += len(rows)
    return rows


def _mode(counter):
    """众数；平票取字典序最小（保证幂等，不依赖输入顺序）。"""
    if not counter:
        return ""
    best = sorted(counter.items(), key=lambda kv: (-kv[1], kv[0]))
    return best[0][0]


def callers_list(counter, limit=MAX_CALLERS):
    """caller 去重列表：非 unknown 优先，其次 Ordinal 排序，最多 limit 个。

    unknown 排最后而不是直接删：它出现在列表里说明「这一条至少有一次没抓到调用方」，
    是采集器健康度的信号；但真要知道宿主时它没有信息量，所以让位给真实 caller。
    """
    items = [c for c in counter if c]
    items.sort(key=lambda c: (c.strip().lower() == "unknown", c))
    return items[:limit]


def final_bucket(rec):
    """同一 h 的全部出现里最保守的那个 bucket（never > template > candidate）。"""
    return min(rec["pairs"], key=lambda k: (CONSERVATIVE[k[0]], k))[0]


def final_reason(rec, bucket):
    """在最终 bucket 的那些出现里取众数 reason（平票取字典序最小，保证幂等）。"""
    tally = {}
    for (b, reason), n in rec["pairs"].items():
        if b == bucket:
            tally[reason] = tally.get(reason, 0) + n
    return _mode(tally)


def effective_bucket(rec):
    """最终 bucket/reason，含 offline_veto —— 产物与报告必须走这一个函数，否则数字对不上。"""
    bucket = final_bucket(rec)
    veto, veto_reason = classify_capture(rec.get("n") or "")
    if veto == "never" and bucket != "never":
        return "never", veto_reason
    return bucket, final_reason(rec, bucket)


def merge_rows(rows, stats):
    """按 h 合并（跨会话）：count 求和、firstSeen 最早、lastSeen 最晚、sessions 计数、
    sink/caller 取众数、n/s 取首见、bucket 取保守、reason 跟着最终 bucket 走。

    rows 里的 "_session" 键（文件名）由 ingest() 打上；缺失时按单会话处理。
    """
    agg = {}
    for row in rows:
        h = row["h"].strip().lower()
        rec = agg.get(h)
        if rec is None:
            rec = agg[h] = {"h": h, "n": row["n"], "s": row.get("s") or row["n"],
                            "sinks": {}, "callers": {}, "count": 0,
                            "firstSeen": "", "lastSeen": "",
                            "sessions": set(), "pairs": {}, "declared_n": 0}
        try:
            rec["count"] += int(row.get("count") or 0)
        except (TypeError, ValueError):
            rec["count"] += 0
        session = row.get("_session")
        if isinstance(session, str) and session:
            rec["sessions"].add(session)
        fs = row.get("firstSeen")
        if isinstance(fs, str) and fs:
            if not rec["firstSeen"] or fs < rec["firstSeen"]:
                rec["firstSeen"] = fs
            if not rec["lastSeen"] or fs > rec["lastSeen"]:
                rec["lastSeen"] = fs
        sink = row.get("sink")
        if isinstance(sink, str) and sink:
            rec["sinks"][sink] = rec["sinks"].get(sink, 0) + 1
        caller = row.get("caller")
        if isinstance(caller, str) and caller:
            rec["callers"][caller] = rec["callers"].get(caller, 0) + 1
        dec = declared_bucket(row)
        if dec is not None:
            rec["declared_n"] += 1
        pair = dec if dec is not None else classify_capture(row.get("n") or "")
        rec["pairs"][pair] = rec["pairs"].get(pair, 0) + 1
    stats["distinct"] = len(agg)
    stats["multi_session"] = sum(1 for r in agg.values() if len(r["sessions"]) >= 2)
    stats["sessions"] = len({s for r in agg.values() for s in r["sessions"]})
    return agg


def infer_mods(callers):
    """从 caller 推断 mod（命名空间/类型的第一段）。

    round 8 的 caller 全部是我们自己补丁的名字（...set_text_Patch1），推不出 mod；
    round 9 修好之后才是真实调用方（如 RCSBuildAid.MenuMass.get_buttonTitle）。
    推不出就写 unknown —— 不许从 sink 名猜（sink 是 Unity API，跟 mod 无关）。
    """
    mods = set()
    for caller in callers:
        base = _PATCH_SUFFIX_RE.sub("", caller.strip())
        low = base.lower()
        if not base or low.startswith("ui18n") or ".ui18n" in low:
            continue
        # 我们的补丁名形如 "GUI.UnityEngine.GUI.Box_Patch1" / "TMP_Text.TMPro.TMP_Text.set_text_Patch1"
        # —— 里面一定夹着 Unity/KSP 的完整类型名，那不代表 mod，直接排除。
        if "UnityEngine." in base or "Unity." in base or "TMPro." in base:
            continue
        if low.endswith("set_text") or low.endswith(".dolabel") or low.endswith(".dobutton"):
            continue
        if low.endswith(".dotoggle") or low.endswith(".dowindow") or low.endswith(".dobox"):
            continue
        if low.endswith(".dorepeatbutton") or low.endswith(".postmessage"):
            continue
        seg = base.split(".")[0]
        if not seg or seg.lower() in _API_ROOTS or not _IDENT_SEG_RE.match(seg):
            continue
        mods.add(seg)
    return sorted(mods) if mods else ["unknown"]


# ---------------------------------------------------------------------------
# 已被覆盖的 h 集合
# ---------------------------------------------------------------------------
def load_h_set(path):
    hs = set()
    if not os.path.isfile(path):
        return hs
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except ValueError:
                continue
            h = obj.get("h") if isinstance(obj, dict) else None
            if isinstance(h, str) and h:
                hs.add(h.strip().lower())
    return hs


def load_covered(packs_dir, pool_path):
    """已翻好的 h（packs/zh-cn/*.jsonl）与已抽取的 h（packs/pool.jsonl）。"""
    pack_h = set()
    zh_dir = os.path.join(packs_dir, "zh-cn")
    if os.path.isdir(zh_dir):
        for path in sorted(glob.glob(os.path.join(zh_dir, "*.jsonl"))):
            pack_h |= load_h_set(path)
    pool_h = load_h_set(pool_path) if pool_path else set()
    return pack_h, pool_h


# ---------------------------------------------------------------------------
# 产物
# ---------------------------------------------------------------------------
def build_rows(agg, pack_h, pool_h, stats, buckets=None):
    """agg -> 产物行。buckets 是允许进产物的档（默认 candidate + template）。"""
    allowed = set(buckets or OUTPUT_BUCKETS)
    rows = []
    reasons = stats["reasons"]
    for h in sorted(agg):
        rec = agg[h]
        if h in pack_h:
            stats["covered_pack"] += 1
            continue
        if h in pool_h:
            stats["covered_pool"] += 1
            continue
        declared = final_bucket(rec)
        bucket, reason = effective_bucket(rec)
        if rec["declared_n"]:
            stats["declared_used"] += 1
        else:
            stats["computed"] += 1
        # 离线重算判 never 的串一律不进产物（保守；也是 selftest.py:669 的口径）
        if bucket != declared:
            stats["offline_veto"] += 1
        stats[bucket] = stats.get(bucket, 0) + 1
        reasons[reason] = reasons.get(reason, 0) + 1
        if bucket not in allowed:
            continue
        rows.append({"h": h, "n": rec["n"], "s": rec["s"],
                     "sink": _mode(rec["sinks"]), "caller": _mode(rec["callers"]),
                     "count": rec["count"], "firstSeen": rec["firstSeen"],
                     "lastSeen": rec["lastSeen"], "sessions": len(rec["sessions"]),
                     "callers": callers_list(rec["callers"]),
                     "bucket": bucket, "reason": reason,
                     "mods": infer_mods(rec["callers"])})
    rows.sort(key=lambda r: (BUCKET_ORDER[r["bucket"]], r["h"]))
    stats["covered"] = stats["covered_pack"] + stats["covered_pool"]
    stats["out_rows"] = len(rows)
    return rows


def write_rows(path, rows):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        for r in rows:
            fh.write(json.dumps({k: r[k] for k in OUT_FIELDS},
                                ensure_ascii=False, separators=(",", ":")) + "\n")
    return len(rows)


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().upper()


# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------
def repo_root():
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def default_packs_dir():
    return os.path.join(repo_root(), "packs")


def default_capture_dir():
    env = os.environ.get("UI18N_CAPTURE_DIR")
    if env:
        return env
    try:
        from .harvest import default_gamedata
        gamedata = default_gamedata()
    except Exception:
        gamedata = None
    if gamedata:
        return os.path.join(os.path.dirname(os.path.abspath(gamedata)),
                            "PluginData", "UI18n")
    return os.path.join(repo_root(), "_scratch", "capture")


def file_inventory(path, rows):
    """单个会话文件的清单行：行数 / distinct / candidate 行 / 首末时间戳 / 字节。"""
    hs = set()
    cand = 0
    lo = hi = ""
    for row in rows:
        hs.add(row["h"].strip().lower())
        dec = declared_bucket(row)
        pair = dec if dec is not None else classify_capture(row.get("n") or "")
        if pair[0] == "candidate":
            cand += 1
        fs = row.get("firstSeen")
        if isinstance(fs, str) and fs:
            lo = fs if not lo or fs < lo else lo
            hi = fs if not hi or fs > hi else hi
    return {"file": os.path.basename(path),
            "bytes": os.path.getsize(path) if os.path.isfile(path) else 0,
            "rows": len(rows), "distinct": len(hs), "candidate": cand,
            "first": lo, "last": hi}


def ingest(files, packs_dir=None, pool_path=None, out_path=None, buckets=None):
    """读 files -> 跨会话合并 -> 剔已覆盖 -> 分桶 -> 落盘。返回 (stats, rows)。

    每个文件的清单（行数/distinct/首末时间）记在 stats["per_file"]，供 --report 用。
    """
    stats = new_stats()
    rows_in = []
    for path in files:
        got = read_capture(path, stats)
        session = os.path.basename(path)
        for row in got:
            row["_session"] = session
        if os.path.isfile(path):
            stats["per_file"].append(file_inventory(path, got))
        rows_in.extend(got)
    agg = merge_rows(rows_in, stats)
    packs_dir = packs_dir or default_packs_dir()
    if pool_path is None:
        pool_path = os.path.join(packs_dir, "pool.jsonl")
    pack_h, pool_h = load_covered(packs_dir, pool_path)
    rows = build_rows(agg, pack_h, pool_h, stats, buckets)
    stats["_agg"] = agg                       # 报告要用完整合并结果（含 never 档）
    stats["_pack_h"] = pack_h
    if out_path:
        write_rows(out_path, rows)
        stats["out_bytes"] = os.path.getsize(out_path)
    return stats, rows


# ---------------------------------------------------------------------------
# 分诊报告（reports/capture-triage.md）
#
# 报告只读数据：除了这份 markdown 不写任何东西（不碰 packs/zh-cn/）。
# mod 归属靠扫描托管 dll 的 TypeDef 反查宿主（ilmeta 只解元数据，不解 IL）：capture 里的
# caller 只有 Type.Method、不带程序集名，所以只能拿类型名去各 dll 里找家。
# ---------------------------------------------------------------------------
BROKEN_CALLER_RE = re.compile(r"^[0-9]+, Culture=")  # 采集器截断程序集限定名后的残片
MANAGED_DIRS = ("KSP_x64_Data", "KSP_Data")


def default_managed(gamedata=""):
    """KSP 的 Managed 目录（托管程序集全在这里，caller 反查必须要它）。找不到返回空串。"""
    if not gamedata:
        return ""
    root = os.path.dirname(os.path.abspath(gamedata))
    for name in MANAGED_DIRS:
        cand = os.path.join(root, name, "Managed")
        if os.path.isdir(cand):
            return cand
    return ""


def _type_full_name(img, tds, nested, rid, cache):
    """TypeDef rid -> 全名（嵌套类型用 Outer+Inner，与反射口径一致）。"""
    got = cache.get(rid)
    if got is not None:
        return got
    row = tds[rid - 1]
    name = img.string_at(row["Name"]) or ""
    space = img.string_at(row["Namespace"]) or ""
    outer = nested.get(rid)
    if outer:
        value = _type_full_name(img, tds, nested, outer, cache) + "+" + name
    elif space:
        value = space + "." + name
    else:
        value = name
    cache[rid] = value
    return value


def build_type_index(gamedata, managed=""):
    """{类型全名 -> (dll 文件名, mod)}，以及扫描统计。

    mod 口径 = GameData 顶层条目名（与 harvest.py:135 一致，注意它与 IL 包用的 asm 名不同，
    例如 000_ClickThroughBlocker / ClickThroughBlocker）；KSP 本体的 dll 记 "KSP"。
    原生 dll（如 Principia/Windows/x64/principia.dll）解析失败只计数，不抛。
    """
    from . import ilmeta
    index = {}
    info = {"dlls": 0, "bad": 0, "types": 0}
    dlls = sorted(glob.glob(os.path.join(gamedata, "**", "*.dll"), recursive=True))
    core = sorted(glob.glob(os.path.join(managed, "*.dll"))) if managed else []
    for path in core + dlls:
        info["dlls"] += 1
        try:
            img = ilmeta.open_image(path)
            tds = img.rows("TypeDef")
        except Exception:
            info["bad"] += 1
            continue
        try:
            nested = {r["NestedClass"]: r["EnclosingClass"]
                      for r in img.rows("NestedClass")}
        except Exception:
            nested = {}
        if path in core:
            mod = "KSP"
        else:
            rel = os.path.relpath(path, gamedata)
            mod = rel.split(os.sep)[0]
        base = os.path.basename(path)
        cache = {}
        for rid in range(1, len(tds) + 1):
            full = _type_full_name(img, tds, nested, rid, cache)
            if not full:
                continue
            info["types"] += 1
            index.setdefault(full, (base, mod))
    return index, info


def attribute_callers(callers, index):
    """caller -> 宿主信息。四级匹配，判不出来就说判不出来（不猜）。"""
    byname = {}
    for full, val in index.items():
        byname.setdefault(full.split(".")[-1], []).append((full, val[0], val[1]))
    out = {}
    for caller in callers:
        rec = {"caller": caller, "type": "", "method": "", "asm": "", "mod": "",
               "how": "", "cands": []}
        if caller == "unknown":
            rec["how"] = "unknown-caller"
            out[caller] = rec
            continue
        if _PATCH_SUFFIX_RE.search(caller):
            rec["how"] = "ui18n-patch"
            out[caller] = rec
            continue
        if BROKEN_CALLER_RE.match(caller):
            rec["how"] = "broken-caller"
            out[caller] = rec
            continue
        base, _, method = caller.rpartition(".")
        if not base:
            base, method = caller, ""
        if method in ("ctor", "cctor"):
            method = "." + method
            base = base.rstrip(".")
        rec["type"], rec["method"] = base, method
        hit = index.get(base)
        if hit:
            rec["asm"], rec["mod"], rec["how"] = hit[0], hit[1], "exact"
            out[caller] = rec
            continue
        # 后缀匹配：capture 侧的 caller 常不带命名空间（只写了类型名）
        tail = base.split(".")[-1]
        cands = [c for c in byname.get(tail, [])
                 if c[0] == base or c[0].endswith("." + base)]
        mods = sorted({c[2] for c in cands})
        if len(cands) == 1:
            rec["asm"], rec["mod"], rec["how"] = cands[0][1], cands[0][2], "suffix"
        elif len(cands) > 1 and len(mods) == 1:
            rec["asm"], rec["mod"], rec["how"] = (cands[0][1], cands[0][2],
                                                  "suffix-same-mod")
        elif len(cands) > 1:
            rec["how"] = "ambiguous"
            rec["cands"] = ["%s@%s" % (c[0], c[2]) for c in cands[:6]]
        else:
            rec["how"] = "miss"
        out[caller] = rec
    return out


def _md_cell(text, limit=80):
    """markdown 单元格：压空白、转义竖线、截断（表格列宽不会被撑爆）。"""
    s = re.sub(r"\s+", " ", str(text or "")).strip()
    if len(s) > limit:
        s = s[:max(1, limit - 1)] + u"\u2026"
    return s.replace("|", "\\|")


def _kb(n):
    if n < 1024:
        return "%d B" % n
    if n < 1024 * 1024:
        return "%.1f KB" % (n / 1024.0)
    return "%.2f MB" % (n / (1024.0 * 1024.0))


def build_triage(stats, rows, pack_h, gamedata="", managed="", do_attrib=True):
    """把 stats/rows 整理成报告用数据（渲染见 render_triage）。"""
    agg = stats.get("_agg") or {}
    per_bucket = {}
    for bucket in BUCKETS:
        per_bucket[bucket] = {"items": 0, "multi": 0, "count": 0, "bytes": 0}
    for h, rec in agg.items():
        bucket = effective_bucket(rec)[0]
        slot = per_bucket.setdefault(bucket, {"items": 0, "multi": 0, "count": 0,
                                              "bytes": 0})
        slot["items"] += 1
        slot["count"] += rec["count"]
        if len(rec["sessions"]) >= 2:
            slot["multi"] += 1
    for row in rows:
        slot = per_bucket.setdefault(row["bucket"], {"items": 0, "multi": 0,
                                                     "count": 0, "bytes": 0})
        slot["bytes"] += len(json.dumps({k: row[k] for k in OUT_FIELDS},
                                        ensure_ascii=False,
                                        separators=(",", ":"))) + 1

    caller_rows = {}
    for row in rows:
        names = row.get("callers") or [row.get("caller") or "unknown"]
        for caller in names:
            slot = caller_rows.setdefault(caller, {"rows": 0, "candidate": 0,
                                                   "template": 0})
            slot["rows"] += 1
            slot[row["bucket"]] = slot.get(row["bucket"], 0) + 1

    attrib = {}
    info = {"dlls": 0, "bad": 0, "types": 0}
    if do_attrib and gamedata and os.path.isdir(gamedata):
        index, info = build_type_index(gamedata, managed)
        attrib = attribute_callers(list(caller_rows), index)

    owner = {}
    zh_dir = os.path.join(default_packs_dir(), "zh-cn")
    if os.path.isdir(zh_dir):
        for path in sorted(glob.glob(os.path.join(zh_dir, "*.jsonl"))):
            for h in load_h_set(path):
                owner.setdefault(h, os.path.basename(path))
    covered = []
    for h, rec in agg.items():
        if h in pack_h:
            covered.append({"h": h, "n": rec["n"], "pack": owner.get(h, ""),
                            "sessions": len(rec["sessions"]),
                            "bucket": effective_bucket(rec)[0]})
    covered.sort(key=lambda r: (-r["sessions"], r["h"]))

    # 运行时自述 reason=path、但按我们的路径规则根本不是路径的：全是带 </color> 的富文本。
    # 根因是运行时的路径判定把 "</" 里的斜杠当成了路径分隔符（见报告 §8）。
    path_rich = []
    for h, rec in agg.items():
        bucket, reason = effective_bucket(rec)
        if bucket != "never" or reason != "path":
            continue
        if _PATH_RE.match(textnorm.normalize(rec["n"])):
            continue
        path_rich.append((rec["count"], rec["n"]))
    path_rich.sort(key=lambda p: (-p[0], p[1]))

    return {"per_bucket": per_bucket, "caller_rows": caller_rows, "attrib": attrib,
            "dll_info": info, "covered": covered, "agg": agg, "managed": managed,
            "path_rich": {"items": len(path_rich),
                          "samples": [{"n": n, "count": c}
                                      for c, n in path_rich[:10]]}}


def render_triage(stats, rows, tri, argv_note=""):
    """渲染 reports/capture-triage.md 全文。"""
    out = []
    w = out.append
    agg = tri["agg"]
    per = stats.get("per_file") or []
    total_bytes = sum(p["bytes"] for p in per)
    stamps = [s for p in per for s in (p["first"], p["last"]) if s]
    first = min(stamps) if stamps else ""
    last = max(stamps) if stamps else ""
    total = max(1, stats["distinct"])
    per_bucket = tri["per_bucket"]

    w("# 运行时捕获分诊报告（task-41）")
    w("")
    w("> 只读分析。本报告**不写** `packs/zh-cn/`、不翻译、不出包；产物只有本文件与 "
      "`packs/capture-candidates.jsonl`（v2 跨会话合并版）。")
    w("")
    w("| 项 | 值 |")
    w("|---|---|")
    w("| 输入会话 | **%d 个** `capture-*.jsonl`（%s） |"
      % (len(per), _kb(total_bytes)))
    w("| 时间跨度 | `%s` → `%s` |" % (first or "-", last or "-"))
    w("| 读入行数 | %d（坏行 %d / 坏记录 %d / h 与 n 不一致 %d）"
      % (stats["rows_in"], stats["bad_lines"], stats["bad_rows"],
         stats["h_mismatch"]))
    w("| 去重后 h | **%d** |" % stats["distinct"])
    w("| 产物 | %d 行 / %s → `packs/capture-candidates.jsonl` |"
      % (stats["out_rows"], _kb(stats["out_bytes"])))
    if argv_note:
        w("| 复现命令 | `%s` |" % _md_cell(argv_note, 200))
    w("")

    w("## 1. 输入清单（逐会话）")
    w("")
    w("| # | 文件 | 行数 | distinct | candidate 行 | 字节 | 首条 | 末条 |")
    w("|---:|---|---:|---:|---:|---:|---|---|")
    for i, p in enumerate(per, 1):
        w("| %d | `%s` | %d | %d | %d | %d | %s | %s |"
          % (i, p["file"], p["rows"], p["distinct"], p["candidate"], p["bytes"],
             p["first"] or "-", p["last"] or "-"))
    w("")
    w("单会话 distinct 上限 5,000（采集器到顶就换新文件），所以「文件数 × 5,000」≈ 上界；"
      "真正的重复率看 §2 的 `sessions>=2`。")
    w("")

    w("## 2. 跨会话合并结果")
    w("")
    w("按 `h` 合并：`count` 求和、`firstSeen` 最早、`lastSeen` 最晚、"
      "`sessions` 记会话数、`callers` 去重（最多 5 个、非 unknown 优先）、"
      "同 h 不同 bucket **取更保守**（never > template > candidate）。")
    w("下表是**合并口径**（去重后的全部 h）；与产物的差量见下面「口径对账」。")
    w("")
    w("| bucket | 条数（h） | 其中 sessions>=2 | 占全部 | count 合计 | 进产物 | 估体积 |")
    w("|---|---:|---:|---:|---:|---|---:|")
    for bucket in BUCKETS:
        slot = per_bucket.get(bucket) or {"items": 0, "multi": 0, "count": 0,
                                          "bytes": 0}
        w("| `%s` | %d | %d | %.1f%% | %d | %s | %s |"
          % (bucket, slot["items"], slot["multi"],
             100.0 * slot["items"] / total, slot["count"],
             "是" if bucket in OUTPUT_BUCKETS else "否（剔除）",
             _kb(slot["bytes"]) if slot["bytes"] else "-"))
    w("")
    w("- 口径对账：去重 **%d** 个 h = 已覆盖剔除 **%d**（zh-cn %d / pool %d）+ "
      "never 剔除 **%d** + 产物 **%d** 行（candidate %d / template %d）。"
      % (stats["distinct"], stats["covered"], stats["covered_pack"],
         stats["covered_pool"], stats["never"], stats["out_rows"],
         stats["candidate"], stats["template"]))
    w("- **跨会话反复 miss（`sessions>=2`）共 %d 条**：candidate %d / template %d / "
      "never %d —— 这一档优先级最高，`sessions` 字段可直接排序。"
      % (stats["multi_session"], per_bucket["candidate"]["multi"],
         per_bucket["template"]["multi"], per_bucket["never"]["multi"]))
    w("- reason 分布：%s" % (_reason_text(stats) or "-"))
    w("- 声明字段采信 %d 条 h / 全靠离线自算 %d 条；**离线重算判 never 而采集器声明不是 "
      "never 的有 %d 条（占 %.1f%%）**（按保守口径一律剔除，见模块 docstring）。"
      % (stats["declared_used"], stats["computed"], stats["offline_veto"],
         100.0 * stats["offline_veto"] / max(1, stats["distinct"])))
    w("- 产物排序是 **(bucket 序, h)**，不是全局 h 升序：`selftest.py:664` 对真实产物"
      "有这条既有断言，改序会把它打红；下游要别的顺序请自行排序。")
    w("")

    w("## 3. 与已发布 89 个包按 `h` 去重")
    w("")
    w("- 合并后 %d 个 h 中，**%d 条已被已发布包覆盖**（`packs/zh-cn/*.jsonl` %d 条 / "
      "`packs/pool.jsonl` %d 条），已在 `build_rows` 里剔除、不进产物。"
      % (stats["distinct"], stats["covered"], stats["covered_pack"],
         stats["covered_pool"]))
    w("- 判读：这些串**包里有、运行时仍然 miss**，问题不在「没翻」而在「没匹配上」。"
      "三个已知可能：① 该包没部署/没加载（`ConfigLoader` 只读语言目录顶层 "
      "`*.jsonl`）；② 运行时 `SourceMatches` 拿到的显示文本与包里的 `s` "
      "不一致（键引用已解析、格式串已填数字、拼接后的整句）；③ 该 mod 版本与包不同。"
      "这批是**部署/匹配诊断**素材，不需要再翻。")
    if tri["covered"]:
        w("")
        w("| h | sessions | bucket | 所在包 | 文本 |")
        w("|---|---:|---|---|---|")
        for r in tri["covered"][:15]:
            w("| `%s` | %d | %s | `%s` | %s |"
              % (r["h"], r["sessions"], r["bucket"], r["pack"], _md_cell(r["n"])))
        if len(tri["covered"]) > 15:
            w("")
            w("（另有 %d 条未列出。）" % (len(tri["covered"]) - 15))
    w("")

    w("## 4. caller top-30（含 mod 归属）")
    w("")
    info = tri["dll_info"]
    if tri["attrib"]:
        w("归属方法：拿 caller 的类型名去 `GameData/**/*.dll` 与 "
          "`%s` 的 TypeDef 反查宿主（ilmeta 只解元数据）。精确全名匹配不到时按"
          "「类型名最后一段」后缀匹配；落到多个 mod 记 `ambiguous`，不猜。"
          "共扫 %d 个 dll / %d 个类型（%d 个原生 dll 跳过）。"
          % (_md_cell(tri.get("managed") or "-", 70),
             info["dlls"], info["types"], info["bad"]))
        w("")
    w("| # | caller | 行数 | candidate | 宿主 asm | mod | 匹配方式 |")
    w("|---:|---|---:|---:|---|---|---|")
    ranked = sorted(tri["caller_rows"].items(), key=lambda kv: (-kv[1]["rows"], kv[0]))
    for i, (caller, slot) in enumerate(ranked[:30], 1):
        rec = tri["attrib"].get(caller) or {}
        w("| %d | `%s` | %d | %d | %s | %s | %s |"
          % (i, _md_cell(caller, 70), slot["rows"], slot.get("candidate", 0),
             rec.get("asm") or "-", rec.get("mod") or "-", rec.get("how") or "-"))
    w("")
    if tri["attrib"]:
        how = {}
        for rec in tri["attrib"].values():
            how[rec["how"]] = how.get(rec["how"], 0) + 1
        w("- 归属统计（按 caller 个数）：" +
          " / ".join("%s %d" % (k, how[k]) for k in sorted(how, key=lambda x: -how[x])))
        w("")

    w("## 5. 样例（40 行）")
    w("")
    w("### 5.1 candidate（该翻，按 count 降序，取前 24 条）")
    w("")
    cand_rows = sorted([r for r in rows if r["bucket"] == "candidate"],
                       key=lambda r: (-r["count"], r["h"]))
    w("| # | count | sessions | reason | caller | 文本 |")
    w("|---:|---:|---:|---|---|---|")
    for i, r in enumerate(cand_rows[:24], 1):
        w("| %d | %d | %d | %s | `%s` | %s |"
          % (i, r["count"], r["sessions"], r["reason"] or "-",
             _md_cell((r.get("callers") or ["-"])[0], 34), _md_cell(r["s"], 90)))
    w("")
    w("### 5.2 template（含数字/占位符，取前 12 条）")
    w("")
    tmpl_rows = sorted([r for r in rows if r["bucket"] == "template"],
                       key=lambda r: (-r["count"], r["h"]))
    w("| # | count | sessions | reason | caller | 文本 |")
    w("|---:|---:|---:|---|---|---|")
    for i, r in enumerate(tmpl_rows[:12], 1):
        w("| %d | %d | %d | %s | `%s` | %s |"
          % (i, r["count"], r["sessions"], r["reason"] or "-",
             _md_cell((r.get("callers") or ["-"])[0], 34), _md_cell(r["s"], 90)))
    w("")
    w("### 5.3 never（永远不该进包，展示判据，取前 4 条）")
    w("")
    never_rows = sorted([(h, rec) for h, rec in agg.items()
                         if effective_bucket(rec)[0] == "never"],
                        key=lambda kv: (-kv[1]["count"], kv[0]))
    w("| # | count | sessions | 判据 | 文本 |")
    w("|---:|---:|---:|---|---|")
    for i, (h, rec) in enumerate(never_rows[:4], 1):
        reason = final_reason(rec, "never") or classify_capture(rec["n"])[1]
        w("| %d | %d | %d | %s | %s |"
          % (i, rec["count"], len(rec["sessions"]), reason or "-",
             _md_cell(rec["n"], 90)))
    w("")

    w("## 6. 建议：哪些该进包、哪些不要")
    w("")
    cand = per_bucket["candidate"]
    tmpl = per_bucket["template"]
    never = per_bucket["never"]
    progress = sum(1 for r in agg.values()
                   if effective_bucket(r) == ("template", "progress"))
    counter = sum(1 for r in agg.values()
                  if effective_bucket(r) == ("template", "counter"))
    w("| 子集 | 条数 | 建议 | 预估体积 |")
    w("|---|---:|---|---:|")
    w("| `bucket=candidate` | %d | **现在就该进包**：不带数字的行，翻了不会错位 | %s |"
      % (cand["items"], _kb(cand["bytes"]) if cand["bytes"] else "-"))
    w("| `template` + `progress` | %d | **永远不进**：`12/345` 这类进度，"
      "数字由游戏填，翻了只会错位 | - |" % progress)
    w("| `template` + `counter` | %d | **暂不进**：计数/表格串，`s` 里数字位置"
      "不固定，翻成中文要重排；要翻必须逐条人看 | %s |"
      % (counter, _kb(tmpl["bytes"]) if tmpl["bytes"] else "-"))
    w("| `bucket=never` | %d | **永远不进**：路径 / 已含中文 / 标识符 / 纯标点 / 过长 | - |"
      % never["items"])
    w("")
    w("- 全量 v2 产物 = candidate + template 共 %d 行 / %s。"
      "**只给打包用时请加 `--buckets candidate`**，产物缩到 %d 行 / %s。"
      % (stats["out_rows"], _kb(stats["out_bytes"]), cand["items"],
         _kb(cand["bytes"]) if cand["bytes"] else "-"))
    w("- 翻译成本参考：candidate 的 %d 条按 IL 总线同口径（~1,100 条 / 81k tokens）"
      "约需 %.0fk tokens；template 那 %d 条不建议整体送翻（多为数字串，翻了大概率被门禁退回）。"
      % (cand["items"], cand["items"] * 74.0 / 1000.0, tmpl["items"]))
    w("")

    w("## 7. mod 归属结论")
    w("")
    if tri["attrib"]:
        bymod = {}
        for caller, rec in tri["attrib"].items():
            key = rec.get("mod") or "(未定)"
            slot = bymod.setdefault(key, {"callers": 0, "rows": 0})
            slot["callers"] += 1
            slot["rows"] += tri["caller_rows"].get(caller, {}).get("rows", 0)
        w("| mod（GameData 顶层目录 / KSP） | caller 数 | 行数 |")
        w("|---|---:|---:|")
        for name, slot in sorted(bymod.items(), key=lambda kv: -kv[1]["rows"]):
            w("| `%s` | %d | %d |" % (name, slot["callers"], slot["rows"]))
        w("")
        undecided = [c for c, r in tri["attrib"].items() if not r.get("mod")]
        w("- **能反查出宿主**：%d / %d 个 caller，这些行可以落到各自的 "
          "`<modId>.il.jsonl`。" % (len(tri["attrib"]) - len(undecided),
                                        len(tri["attrib"])))
        kinds = sorted({tri["attrib"][c]["how"] for c in undecided})
        w("- **反查不出**：%d 个 caller（%s）。包括我们自己的补丁帧、采集器把程序集"
          "限定名截断的残片（形如 `32706, Culture=neutral, PublicKeyToken=null]].…`）、"
          "以及同名类型落在两个 mod 的 `ambiguous`。"
          % (len(undecided), " / ".join(kinds) or "-"))
        w("")
        w("**结论：反查不出的这部分（连同 `caller=unknown` 的行）统一落一个共享包 "
          "`packs/zh-cn/_captured.jsonl`，行内 `mod` 一律填 `captured`。**")
        w("")
        w("- 为什么能共用一个文件：`ConfigLoader` 是按目录读 `*.jsonl` 的，"
          "`mod` 只决定 `DictEntry.Level` 是 Site（Site 级不参与过滤，不会因为 "
          "mod 名不对而查不到）；运行时是 `hash -> 条目`，同一个 h 只要有一条就命中。")
        w("- 为什么不硬猜 mod：猜错会把同一个 h 写进两个包、给出两份不同译文"
          "（`il_conflict`），比放共享包更糟；共享包天然一个 h 一份译文。")
        w("- `ambiguous` 的候选类型名在 `attribute_callers` 的 `cands` "
          "里，落地时人工看一眼就能定。")
        w("- 口径提醒：本报告的 `mod` 是 **GameData 顶层目录名**（与 harvest.py 一致），"
          "IL 包用的是 **asm 名**（`ClickThroughBlocker.il.jsonl` vs "
          "`000_ClickThroughBlocker`）。落地时按 asm 命名建文件、行内 `mod` "
          "才用 asm 名该有的值。")
    else:
        w("（本次未做 dll 反查：没找到 GameData 目录，或显式 `--no-attrib`。）")
    w("")

    w("## 8. 已知问题与下一步")
    w("")
    w("1. `count` 是会话内 miss 次数，不是「屏幕上出现过几次」：采集器按 h 累加，"
      "跨会话求和只用于排序。")
    w("2. **采集器缺陷**：仍有 caller 是 `32706, Culture=neutral, "
      "PublicKeyToken=null]].RenderWindowContents` 这种残片（取 caller 时对程序集限定名"
      "做了字符串截断），这些行只能进共享包。")
    rich = tri.get("path_rich") or {}
    w("3. **运行时分类器把富文本判成 path**：%d 个 h 的 declared reason 是 `path`，"
      "但它们不是路径，而是带 `</color>` 的富文本标签 —— 运行时的路径判定把 `</` "
      "里的斜杠当成了分隔符。样例：%s。这批多是显卡/抗锯齿/画质术语（Legacy / Auto / "
      "ACES / AgX / Custom / Medium …），暂时不翻没有观感损失，但 **v1.2 应修运行时的 path "
      "判定（先剥掉 TMP 标签再判路径）**，否则以后任何带 `<b>` / `<color>` 的"
      "标签都会被静默判 never。"
      % (rich.get("items", 0),
         "、".join("`%s`（%d 次）" % (_md_cell(s["n"], 44), s["count"])
                  for s in (rich.get("samples") or [])[:6]) or "-"))
    w("4. **下一轮**（不在本任务）：candidate 的 %d 条走 `translate_il` 同一套门禁"
      "翻一遍 → 按 §7 归到 `<modId>.il.jsonl` 或 `_captured.jsonl` → 重跑 "
      "`packindex` 与 `selftest`。" % cand["items"])
    w("")
    return "\n".join(out) + "\n"


def write_triage(path, text):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    return len(text.encode("utf-8"))


def _reason_text(stats):
    parts = []
    for reason in REASON_ORDER:
        n = stats["reasons"].get(reason, 0)
        if n:
            parts.append("%s %d" % (reason or "无", n))
    return " / ".join(parts)


def main(argv=None):
    ensure_stdout()
    parser = argparse.ArgumentParser(
        prog="python -m i18n_tools.capture_ingest",
        description="运行时捕获 -> 可翻译候选（合并/去重/剔已覆盖/分桶）")
    parser.add_argument("files", nargs="*", help="capture-*.jsonl 路径（可多个）")
    parser.add_argument("--dir", "--all", dest="dir",
                        help="捕获目录，读其中的全部 capture-*.jsonl（--all 是它的别名；"
                             "默认 UI18N_CAPTURE_DIR / KSP 根\\PluginData\\UI18n）")
    parser.add_argument("--packs-dir", default=default_packs_dir(),
                        help="packs 目录（默认仓库 packs/）")
    parser.add_argument("--pool", default=None,
                        help="pool.jsonl（默认 packs/pool.jsonl）")
    parser.add_argument("--out", default=None,
                        help="输出（默认 packs/capture-candidates.jsonl）")
    parser.add_argument("--selftest", action="store_true", help="跑本模块的断言")
    parser.add_argument("--buckets", default=None,
                        help="只保留这些档（逗号分隔，默认 candidate,template；"
                             "never 永远不进产物）")
    parser.add_argument("--report", default=None,
                        help="写分诊报告 markdown（如 ..\\reports\\capture-triage.md）")
    parser.add_argument("--gamedata", default=None,
                        help="GameData 目录（报告做 caller 反查用；默认自动探测）")
    parser.add_argument("--managed", default=None,
                        help="KSP_x64_Data\\Managed 目录（同上；默认自动探测）")
    parser.add_argument("--no-attrib", dest="attrib", action="store_false",
                        default=True, help="报告跳过 dll 反查（快，但没有 mod 归属）")
    parser.add_argument("--quiet", action="store_true", help="不打印逐文件信息")
    args = parser.parse_args(argv)

    if args.selftest:
        import tempfile
        base = tempfile.mkdtemp(prefix="ui18n_capture_selftest_")
        try:
            cases = selftest_cases(base)
        finally:
            import shutil
            shutil.rmtree(base, ignore_errors=True)
        bad = 0
        for name, ok, detail in cases:
            if ok:
                print("  [ok]   " + name)
            else:
                bad += 1
                print("  [FAIL] " + name + ("  " + detail if detail else ""))
        print("[capture] 通过 %d / %d 项" % (len(cases) - bad, len(cases)))
        return 0 if bad == 0 else 1

    files = list(args.files)
    if args.dir:
        files.extend(sorted(glob.glob(os.path.join(args.dir, "capture-*.jsonl"))))
    if not files:
        files = sorted(glob.glob(os.path.join(default_capture_dir(), "capture-*.jsonl")))
    out_path = args.out or os.path.join(args.packs_dir, "capture-candidates.jsonl")
    if not files:
        print("[capture] 没找到 capture-*.jsonl（目录 %s）" % default_capture_dir())
        print("[capture] 提示：插件跑过一轮游戏后才会生成；也可以显式传文件名。")
        return 2
    buckets = None
    if args.buckets:
        buckets = [b.strip() for b in args.buckets.split(",") if b.strip()]
        bad = [b for b in buckets if b not in BUCKETS]
        if bad:
            print("[capture] --buckets 不认识：%s（可选 %s）"
                  % (",".join(bad), " / ".join(BUCKETS)))
            return 2
    if not args.quiet:
        for path in files:
            print("[capture] 输入 %s" % path)
    stats, rows = ingest(files, args.packs_dir, args.pool, out_path, buckets)
    print("[capture] 文件 %d 个（缺失 %d）/ 读入 %d 行（坏行 %d / 坏记录 %d / h 不一致 %d）"
          % (stats["files"], stats["missing_files"], stats["rows_in"],
             stats["bad_lines"], stats["bad_rows"], stats["h_mismatch"]))
    print("[capture] 去重后 %d 个 h；已被包覆盖 %d（zh-cn %d / pool %d）"
          % (stats["distinct"], stats["covered"],
             stats["covered_pack"], stats["covered_pool"]))
    print("[capture] 分桶 never %d / template %d / candidate %d（声明字段采信 %d / 自算 %d）"
          % (stats["never"], stats["template"], stats["candidate"],
             stats["declared_used"], stats["computed"]))
    print("[capture] reason: %s" % (_reason_text(stats) or "-"))
    print("[capture] 输出 %d 行 / %d 字节 -> %s"
          % (stats["out_rows"], stats["out_bytes"], out_path))
    if os.path.isfile(out_path):
        print("[capture] sha256 %s" % sha256_file(out_path))

    if args.report:
        gamedata = args.gamedata
        if not gamedata and args.attrib:
            try:
                from .harvest import default_gamedata
                gamedata = default_gamedata()
            except Exception:
                gamedata = None
        managed = args.managed or (default_managed(gamedata) if gamedata else "")
        note = " ".join(["python -m i18n_tools.capture_ingest"] + list(sys.argv[1:]))
        tri = build_triage(stats, rows, stats.get("_pack_h") or set(),
                           gamedata or "", managed, args.attrib)
        text = render_triage(stats, rows, tri, argv_note=note)
        written = write_triage(args.report, text)
        print("[capture] 报告 %d 字节 -> %s" % (written, args.report))
        if tri["attrib"]:
            known = sum(1 for r in tri["attrib"].values() if r["mod"])
            print("[capture] caller 归属 %d / %d 个能反查出宿主（扫 %d 个 dll / %d 个类型）"
                  % (known, len(tri["attrib"]), tri["dll_info"]["dlls"],
                     tri["dll_info"]["types"]))
    return 0


# ---------------------------------------------------------------------------
# 自检（也被 i18n_tools.selftest 调用）
# ---------------------------------------------------------------------------
def _write(path, text):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)


def _jsonl(rows):
    return "".join(json.dumps(r, ensure_ascii=False, separators=(",", ":")) + "\n"
                   for r in rows)


def selftest_cases(base):
    """返回 [(name, ok, detail)]；base 是临时目录。"""
    out = []

    def a(name, ok, detail=""):
        out.append((name, bool(ok), detail))

    # --- 分类：路径 / 中文 / 进度 / 计数 / 普通句子 / 标点 / 标识符 ---
    a("classify: 资源路径 -> never/path",
      classify_capture("ReStockPlus/Parts/FuelTank/x") == ("never", "path"))
    a("classify: 含中文 -> never/already-cjk",
      classify_capture(u"正在加载...") == ("never", "already-cjk"))
    a("classify: 数字/数字 -> template/progress",
      classify_capture("Loading sound asset 0/234") == ("template", "progress"))
    a("classify: 含数字 -> template/counter",
      classify_capture("ModuleManager: 8448 patches applied") == ("template", "counter"))
    a("classify: 普通句子 -> candidate",
      classify_capture("Vessel mass") == ("candidate", ""))
    a("classify: 纯标点 -> never/punct",
      classify_capture("===") == ("never", "punct"))
    a("classify: 标识符 -> never/identifier",
      classify_capture("m_text") == ("never", "identifier"))

    d = os.path.join(base, "cap")
    packs = os.path.join(base, "packs")
    h_vessel = fnv.hash_text(textnorm.normalize("Vessel mass"))
    h_load = fnv.hash_text(textnorm.normalize("Loading sound asset 0/234"))
    h_mm = fnv.hash_text(textnorm.normalize("ModuleManager: 8448 patches applied"))
    h_cjk = fnv.hash_text(textnorm.normalize(u"正在加载..."))
    h_db = fnv.hash_text(textnorm.normalize("Database"))
    h_path = fnv.hash_text(textnorm.normalize("ReStockPlus/Parts/FuelTank/x"))
    h_cov = fnv.hash_text(textnorm.normalize("Covered text"))
    h_pool = fnv.hash_text(textnorm.normalize("Pool text"))
    h_locks = fnv.hash_text(textnorm.normalize("All Input Locks Cleared"))

    # 旧 7 字段文件（a）：含坏行、坏记录、同 h 两行（累加 count）、h 与 n 不一致
    _write(os.path.join(d, "capture-a.jsonl"), _jsonl([
        {"h": h_vessel, "n": "Vessel mass", "s": "Vessel mass",
         "sink": "GUILayout.DoToggle", "caller": "RCSBuildAid.MenuMass.get_buttonTitle",
         "count": 3, "firstSeen": "2026-10-23T06:01:28Z"},
        {"h": h_vessel, "n": "Vessel mass", "s": "Vessel mass",
         "sink": "GUILayout.DoLabel", "caller": "RCSBuildAid.MenuMass.get_buttonTitle",
         "count": 5, "firstSeen": "2026-10-23T06:00:00Z"},
        {"h": h_load, "n": "Loading sound asset 0/234", "s": "Loading sound asset 0/234",
         "sink": "TMPro.TMP_Text.set_text", "caller": "KSP.SoundLoader.update",
         "count": 1, "firstSeen": "2026-10-23T06:02:00Z"},
        {"h": "deadbeefdeadbeef", "n": u"正在加载", "s": u"正在加载",
         "sink": "GUILayout.DoLabel", "caller": "X.Y.z", "count": 1,
         "firstSeen": "2026-10-23T06:03:00Z"},
    ]) + "{ this is not json\n" + _jsonl([{"h": "aaaabbbbccccdddd", "sink": "x"}]))

    # 新 10 字段文件（b）：声明字段被采信；bucket=never 被剔；已被覆盖的两条被剔
    _write(os.path.join(d, "capture-b.jsonl"), _jsonl([
        {"h": h_mm, "n": "ModuleManager: 8448 patches applied",
         "s": "ModuleManager: 8448 patches applied", "sink": "GUILayout.DoLabel",
         "caller": "ModuleManager.ModuleManager.Log", "count": 2,
         "firstSeen": "2026-10-23T07:00:00Z",
         "template": True, "reason": "counter", "bucket": "template"},
        {"h": h_cjk, "n": u"正在加载...", "s": u"正在加载...",
         "sink": "GUILayout.DoLabel", "caller": "X.Y.z", "count": 1,
         "firstSeen": "2026-10-23T07:01:00Z",
         "template": False, "reason": "", "bucket": "never"},
        {"h": h_db, "n": "Database", "s": "Database", "sink": "GUI.Box",
         "caller": "KSP.DebugToolbar.draw", "count": 1,
         "firstSeen": "2026-10-23T07:02:00Z",
         "template": False, "reason": "", "bucket": "candidate"},
        {"h": h_path, "n": "ReStockPlus/Parts/FuelTank/x",
         "s": "ReStockPlus/Parts/FuelTank/x", "sink": "GUILayout.DoLabel",
         "caller": "ReStockPlus.X.y", "count": 1, "firstSeen": "2026-10-23T07:03:00Z"},
        {"h": h_cov, "n": "Covered text", "s": "Covered text", "sink": "GUI.Label",
         "caller": "AJE.X.y", "count": 1, "firstSeen": "2026-10-23T07:04:00Z"},
        {"h": h_pool, "n": "Pool text", "s": "Pool text", "sink": "GUI.Label",
         "caller": "AJE.X.y", "count": 1, "firstSeen": "2026-10-23T07:05:00Z"},
        {"h": h_locks, "n": "All Input Locks Cleared", "s": "All Input Locks Cleared",
         "sink": "GUILayout.DoToggle",
         "caller": "GUILayout.UnityEngine.GUILayout.DoLabel_Patch1", "count": 7,
         "firstSeen": "2026-10-23T07:06:00Z"},
    ]))

    # 已翻好的包与已抽取的 pool（各含一条，用于验证剔除）
    _write(os.path.join(packs, "zh-cn", "AJE.jsonl"),
           _jsonl([{"h": h_cov, "s": u"已覆盖", "t": u"已覆盖", "mod": "AJE"}]))
    _write(os.path.join(packs, "pool.jsonl"),
           _jsonl([{"id": 1, "h": h_pool, "text": "Pool text", "src": "cfg",
                    "mod": "AJE", "field": "title", "ph": False, "rt": False,
                    "dg": False, "path": "", "url": "", "len": 9}]))

    out_path = os.path.join(base, "capture-candidates.jsonl")
    files = [os.path.join(d, "capture-a.jsonl"),
             os.path.join(d, "capture-b.jsonl"),
             os.path.join(d, "capture-missing.jsonl")]
    stats, rows = ingest(files, packs, os.path.join(packs, "pool.jsonl"), out_path)

    a("缺文件只记账不抛", stats["missing_files"] == 1, "got=%d" % stats["missing_files"])
    a("坏 JSON 行计数", stats["bad_lines"] == 1, "got=%d" % stats["bad_lines"])
    a("缺 n 的记录计数", stats["bad_rows"] == 1, "got=%d" % stats["bad_rows"])
    a("h 与 n 不一致计数", stats["h_mismatch"] == 1, "got=%d" % stats["h_mismatch"])
    a("7 字段与 10 字段都能读", stats["rows_in"] == 11, "got=%d" % stats["rows_in"])
    a("按 h 去重", stats["distinct"] == 10, "got=%d" % stats["distinct"])
    a("已被包覆盖的 h 被剔除", stats["covered_pack"] == 1, "got=%d" % stats["covered_pack"])
    a("已被 pool 覆盖的 h 被剔除", stats["covered_pool"] == 1, "got=%d" % stats["covered_pool"])
    a("bucket=never 的行不进产物", stats["never"] == 3, "got=%d" % stats["never"])
    a("声明字段被采信（10 字段）", stats["declared_used"] == 3,
      "got=%d" % stats["declared_used"])
    a("缺声明字段时自算", stats["computed"] == 5, "got=%d" % stats["computed"])
    a("产物行数 = candidate + template", stats["out_rows"] == 5,
      "got=%d" % stats["out_rows"])

    by_n = {r["n"]: r for r in rows}
    vessel = by_n.get("Vessel mass") or {}
    a("跨文件同 h 的 count 累加", vessel.get("count") == 8, "got=%r" % vessel.get("count"))
    a("firstSeen 取最早", vessel.get("firstSeen") == "2026-10-23T06:00:00Z",
      "got=%r" % vessel.get("firstSeen"))
    a("sink 取众数（平票取字典序最小）", vessel.get("sink") == "GUILayout.DoLabel",
      "got=%r" % vessel.get("sink"))
    a("caller 可用时 mod 从命名空间推断", vessel.get("mods") == ["RCSBuildAid"],
      "got=%r" % vessel.get("mods"))
    a("caller 是我们补丁名时 mods=unknown",
      (by_n.get("All Input Locks Cleared") or {}).get("mods") == ["unknown"],
      "got=%r" % (by_n.get("All Input Locks Cleared") or {}).get("mods"))
    a("template 行保留（reason 来自声明字段）",
      (by_n.get("ModuleManager: 8448 patches applied") or {}).get("reason") == "counter")
    a("输出字段顺序固定", bool(rows) and list(rows[0]) == list(OUT_FIELDS),
      "got=%r" % (list(rows[0]) if rows else None))
    a("排序 = (bucket 序, h)",
      [r["bucket"] for r in rows] == sorted([r["bucket"] for r in rows],
                                           key=lambda b: BUCKET_ORDER[b]))

    # --- 幂等 + 无 BOM + LF ---
    with open(out_path, "rb") as fh:
        first = fh.read()
    ingest(files, packs, os.path.join(packs, "pool.jsonl"), out_path)
    with open(out_path, "rb") as fh:
        second = fh.read()
    a("连跑两遍逐字节相同", first == second)
    a("无 BOM、只有 LF、末尾有换行",
      not first.startswith(b"\xef\xbb\xbf") and b"\r" not in first
      and first.endswith(b"\n"))
    a("h 与 n 同口径（h == fnv.hash_text(normalize(n))）",
      all(r["h"] == fnv.hash_text(textnorm.normalize(r["n"])) for r in rows))

    # --- 跨会话合并：sessions / lastSeen / callers / 取保守 / offline_veto ---
    d2 = os.path.join(base, "cap2")
    h_deep = fnv.hash_text(textnorm.normalize("ReStockPlus/Parts/Deep/x"))
    _write(os.path.join(d2, "capture-c.jsonl"), _jsonl([
        {"h": h_vessel, "n": "Vessel mass", "s": "Vessel mass",
         "sink": "GUILayout.DoLabel", "caller": "unknown", "count": 4,
         "firstSeen": "2026-10-24T10:00:00Z",
         "template": False, "reason": "", "bucket": "candidate"},
        {"h": h_deep, "n": "ReStockPlus/Parts/Deep/x", "s": "ReStockPlus/Parts/Deep/x",
         "sink": "GUILayout.DoLabel", "caller": "ReStockPlus.Deep.y", "count": 1,
         "firstSeen": "2026-10-24T10:05:00Z",
         "template": False, "reason": "", "bucket": "candidate"},
    ]))
    _write(os.path.join(d2, "capture-e.jsonl"), _jsonl([
        {"h": h_load, "n": "Loading sound asset 0/234",
         "s": "Loading sound asset 0/234", "sink": "TMPro.TMP_Text.set_text",
         "caller": "KSP.SoundLoader.update", "count": 2,
         "firstSeen": "2026-10-24T11:00:00Z",
         "template": False, "reason": "", "bucket": "candidate"},
    ]))
    files2 = [os.path.join(d, "capture-a.jsonl"),
              os.path.join(d2, "capture-c.jsonl"),
              os.path.join(d2, "capture-e.jsonl")]
    out2 = os.path.join(base, "cross-session.jsonl")
    stats2, rows2 = ingest(files2, packs, os.path.join(packs, "pool.jsonl"), out2)
    by_n2 = {r["n"]: r for r in rows2}
    v2 = by_n2.get("Vessel mass") or {}
    a("跨会话: sessions 计会话文件数", v2.get("sessions") == 2, "got=%r" % v2.get("sessions"))
    a("跨会话: multi_session 计「=2 个会话的 h 数」", stats2["multi_session"] == 2, "got=%d" % stats2["multi_session"])
    a("跨会话: lastSeen 取最晚", v2.get("lastSeen") == "2026-10-24T10:00:00Z", "got=%r" % v2.get("lastSeen"))
    a("跨会话: count 继续累加", v2.get("count") == 12, "got=%r" % v2.get("count"))
    a("跨会话: callers 去重、非 unknown 优先",
      v2.get("callers") == ["RCSBuildAid.MenuMass.get_buttonTitle", "unknown"],
      "got=%r" % (v2.get("callers"),))
    load2 = by_n2.get("Loading sound asset 0/234") or {}
    a("跨会话: 同 h 不同 bucket 取更保守", load2.get("bucket") == "template", "got=%r" % load2.get("bucket"))
    a("跨会话: reason 跟着最终 bucket 走", load2.get("reason") == "progress", "got=%r" % load2.get("reason"))
    a("offline_veto: 声明 candidate 但离线判 never 的串不进产物",
      "ReStockPlus/Parts/Deep/x" not in by_n2 and stats2["offline_veto"] >= 1,
      "veto=%d rows=%d" % (stats2["offline_veto"], len(rows2)))
    lst = callers_list({"unknown": 9, "A.x": 1, "B.y": 1, "C.z": 1, "D.w": 1, "E.v": 1})
    a("callers_list: 最多 5 个且 unknown 排最后",
      len(lst) == 5 and lst[0] != "unknown" and "unknown" not in lst, "got=%r" % (lst,))
    out3 = os.path.join(base, "only-candidate.jsonl")
    stats3, rows3 = ingest(files2, packs, os.path.join(packs, "pool.jsonl"), out3, ["candidate"])
    a("--buckets candidate: 产物只剩 candidate",
      bool(rows3) and all(r["bucket"] == "candidate" for r in rows3),
      "got=%r" % sorted({r["bucket"] for r in rows3}))
    a("--buckets candidate: 行数比两档少", len(rows3) < len(rows2), "got=%d vs %d" % (len(rows3), len(rows2)))
    tri = build_triage(stats2, rows2, stats2["_pack_h"], "", "", False)
    a("报告: per_bucket 有 candidate 且条数 > 0", tri["per_bucket"]["candidate"]["items"] > 0,
      "got=%r" % (tri["per_bucket"]["candidate"]["items"],))
    a("报告: never 档也统计（不进产物但要出现在报告里）", tri["per_bucket"]["never"]["items"] >= 2,
      "got=%r" % (tri["per_bucket"]["never"]["items"],))
    a("报告: caller_rows 覆盖产物里的 caller",
      "RCSBuildAid.MenuMass.get_buttonTitle" in tri["caller_rows"], "got=%r" % (sorted(tri["caller_rows"])[:4],))
    marks = ("# 运行时捕获分诊报告", "## 1. 输入清单", "## 2. 跨会话合并结果",
             "## 3. 与已发布 89 个包", "## 4. caller top-30", "## 5. 样例（40 行）",
             "## 6. 建议", "## 7. mod 归属结论", "## 8. 已知问题与下一步")
    text = render_triage(stats2, rows2, tri, argv_note="python -m x --all d")
    a("报告: 含全部 9 个关键小节", all(m in text for m in marks),
      "missing=%r" % [m for m in marks if m not in text])
    rp = os.path.join(base, "reports", "capture-triage.md")
    size = write_triage(rp, text)
    fh = open(rp, "rb")
    blob = fh.read()
    fh.close()
    a("报告: 落盘 UTF-8、无 BOM、以换行结尾",
      size > 500 and not blob.startswith(b"\xef\xbb\xbf") and blob.endswith(b"\n"), "size=%d" % size)
    fake = {"A.B.Main": ("Alpha.dll", "Alpha"), "Q.Only.One": ("Beta.dll", "Beta")}
    att = attribute_callers(["A.B.Main.Run", "Only.One.Draw", "A.B.Main", "Z.Nope.x",
                             "GUI.Label_Patch1",
                             "32706, Culture=neutral, PublicKeyToken=null]].Draw",
                             "unknown"], fake)
    a("归属: 精确命中类型全名", att["A.B.Main.Run"]["how"] == "exact" and att["A.B.Main.Run"]["mod"] == "Alpha",
      "got=%r" % att["A.B.Main.Run"]["how"])
    a("归属: 后缀命中（caller 省了命名空间）",
      att["Only.One.Draw"]["how"] == "suffix"
      and att["Only.One.Draw"]["mod"] == "Beta",
      "got=%r" % att["Only.One.Draw"]["how"])
    a("归属: 我们的补丁帧单列一类", att["GUI.Label_Patch1"]["how"] == "ui18n-patch")
    a("归属: 截断残片单列一类且不猜 mod",
      att["32706, Culture=neutral, PublicKeyToken=null]].Draw"]["how"] == "broken-caller"
      and not att["32706, Culture=neutral, PublicKeyToken=null]].Draw"]["mod"])
    a("归属: unknown 与查不到都不猜",
      att["unknown"]["how"] == "unknown-caller" and att["Z.Nope.x"]["how"] == "miss")
    a("归属: 同名类型落在两个 mod 记 ambiguous",
      attribute_callers(["Thing.Draw"], {"X.Thing": ("A.dll", "A"), "Y.Thing": ("B.dll", "B")})["Thing.Draw"]["how"] == "ambiguous")
    a("归属: 同名类型但同一 mod 不算歧义",
      attribute_callers(["Thing.Draw"], {"X.Thing": ("A.dll", "A"), "Y.Thing": ("A2.dll", "A")})["Thing.Draw"]["how"] == "suffix-same-mod")
    a("归属: 没给 GameData 时报告不做反查（不抛）",
      build_triage(stats2, rows2, stats2["_pack_h"], "", "", True)["attrib"] == {})
    return out



if __name__ == "__main__":
    raise SystemExit(main())