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
    {"h","n","s","sink","caller","count","firstSeen","bucket","reason","mods"}
排序 (bucket 序, h)；无 BOM、LF；同一输入连跑两遍逐字节相同。

用法（在 tools 目录下）
-----------------------
    python -m i18n_tools.capture_ingest
    python -m i18n_tools.capture_ingest --dir "D:\Kerbal Space Program_newmod\PluginData\UI18n"
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
              "bucket", "reason", "mods")

BUCKETS = ("candidate", "template", "never")
BUCKET_ORDER = {"candidate": 0, "template": 1, "never": 2}

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
        "declared_used": 0, "computed": 0,
        "out_rows": 0, "out_bytes": 0,
        "reasons": {},
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


def merge_rows(rows, stats):
    """按 h 合并：count 累加、firstSeen 取最早、sink/caller 取众数、n/s 取首见。"""
    agg = {}
    for row in rows:
        h = row["h"].strip().lower()
        rec = agg.get(h)
        if rec is None:
            rec = agg[h] = {"h": h, "n": row["n"], "s": row.get("s") or row["n"],
                            "sinks": {}, "callers": {}, "count": 0,
                            "firstSeen": "", "declared": {}}
        try:
            rec["count"] += int(row.get("count") or 0)
        except (TypeError, ValueError):
            rec["count"] += 0
        fs = row.get("firstSeen")
        if isinstance(fs, str) and fs:
            if not rec["firstSeen"] or fs < rec["firstSeen"]:
                rec["firstSeen"] = fs
        sink = row.get("sink")
        if isinstance(sink, str) and sink:
            rec["sinks"][sink] = rec["sinks"].get(sink, 0) + 1
        caller = row.get("caller")
        if isinstance(caller, str) and caller:
            rec["callers"][caller] = rec["callers"].get(caller, 0) + 1
        dec = declared_bucket(row)
        if dec is not None:
            key = dec[0] + "|" + dec[1]
            rec["declared"][key] = rec["declared"].get(key, 0) + 1
    stats["distinct"] = len(agg)
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
def build_rows(agg, pack_h, pool_h, stats):
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
        dec = _mode(rec["declared"])
        if dec:
            bucket, _, reason = dec.partition("|")
            stats["declared_used"] += 1
        else:
            bucket, reason = classify_capture(rec["n"])
            stats["computed"] += 1
        stats[bucket] = stats.get(bucket, 0) + 1
        reasons[reason] = reasons.get(reason, 0) + 1
        if bucket == "never":
            continue
        rows.append({"h": h, "n": rec["n"], "s": rec["s"],
                     "sink": _mode(rec["sinks"]), "caller": _mode(rec["callers"]),
                     "count": rec["count"], "firstSeen": rec["firstSeen"],
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


def ingest(files, packs_dir=None, pool_path=None, out_path=None):
    """读 files -> 合并 -> 剔已覆盖 -> 分桶 -> 落盘。返回 (stats, rows)。"""
    stats = new_stats()
    rows_in = []
    for path in files:
        rows_in.extend(read_capture(path, stats))
    agg = merge_rows(rows_in, stats)
    packs_dir = packs_dir or default_packs_dir()
    if pool_path is None:
        pool_path = os.path.join(packs_dir, "pool.jsonl")
    pack_h, pool_h = load_covered(packs_dir, pool_path)
    rows = build_rows(agg, pack_h, pool_h, stats)
    if out_path:
        write_rows(out_path, rows)
        stats["out_bytes"] = os.path.getsize(out_path)
    return stats, rows


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
    parser.add_argument("--dir", help="捕获目录，读其中的 capture-*.jsonl"
                                      "（默认 UI18N_CAPTURE_DIR / KSP 根\\PluginData\\UI18n）")
    parser.add_argument("--packs-dir", default=default_packs_dir(),
                        help="packs 目录（默认仓库 packs/）")
    parser.add_argument("--pool", default=None,
                        help="pool.jsonl（默认 packs/pool.jsonl）")
    parser.add_argument("--out", default=None,
                        help="输出（默认 packs/capture-candidates.jsonl）")
    parser.add_argument("--selftest", action="store_true", help="跑本模块的断言")
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
    if not args.quiet:
        for path in files:
            print("[capture] 输入 %s" % path)
    stats, rows = ingest(files, args.packs_dir, args.pool, out_path)
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
    return out


if __name__ == "__main__":
    raise SystemExit(main())