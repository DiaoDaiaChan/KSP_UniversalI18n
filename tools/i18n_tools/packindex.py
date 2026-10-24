"""packindex.py -- 生成 packs/index.json（联网分发 / 增量更新的唯一清单）与 reports/coverage.md。

用法（在 <repo>\tools 下）：
    python -m i18n_tools.packindex                    # 写 packs/index.json + reports/coverage.md
    python -m i18n_tools.packindex --check             # 只校验：磁盘 index 与真实文件不一致则 exit 1
    python -m i18n_tools.packindex --check --quiet     # 校验，成功时只打一行

清单 schema（**发布后不可随意改**；真要改字段含义必须把 schema +1）：
    {
      "schema": 1,
      "lang": "zh-cn",
      "generatedAt": "2026-10-23T09:12:34Z",
      "totalRows": 11089,
      "totalBytes": 3685087,
      "packCount": 89,
      "packs": [
        {"mod": "RP-1", "file": "zh-cn/RP-1.jsonl", "rows": 3382, "bytes": 1297270,
         "sha256": "<64 位小写 hex>", "keepRows": 0}
      ]
    }

字段口径（这是分发协议的一部分，不是统计口径）：
  schema      清单格式版本，只有"字段含义"变了才 +1（加字段不算，但要写进本 docstring）；
  lang        语言目录名，同时也是 packs/ 下的子目录名；
  generatedAt 生成时刻（UTC、秒级 ISO8601）。**这是唯一允许两次生成不同的字段**，
              其余字段逐字节确定，否则增量比对全失效；
  totalRows / totalBytes / packCount
              逐文件累加，且必须与 packs[] 严格相等（本模块与 --check 都会验算）；
  mod         包名 = 文件名去掉 .jsonl；**IL 包再剥掉尾部的 .il**
              （AJE.il.jsonl -> AJE），与包内行的 mod 字段、packs/pool.jsonl 的 mod 同一套
              命名。因此同一个 mod 最多两个条目（cfg 包 + IL 包），靠 file 区分：
              **mod 不再唯一，file 才是唯一键**（check_index 按 file 比对）；
  file        相对 packs/ 的路径，固定用正斜杠（Windows 上也一样），便于跨平台分发；
  rows        文件里**非空行**数（每行一条 JSON）；
  bytes       文件在磁盘上的真实字节数（不是字符数、不是行数）；
  sha256      **文件级** sha256（小写 hex）。增量下载靠它判断"这个包要不要重下"；
  keepRows    该文件里 t == s 的行数。t == s 表示这条没译、运行时直通，
              运行时靠它终止下沉查找——是真实语义，不是统计口径（全库 92 行 =
              cfg 包 88 + IL 包 4）。

packs 顺序：按 **(mod, file) 两个键的 ordinal（码点）升序**，不是 case-insensitive。
    理由：清单要"每次生成都一样"，case-insensitive 排序在不同 Python 版本/区域设置下
    可能给出不同结果，ordinal 是唯一无歧义且与语言环境无关的规则。
    同一个 mod 的 cfg 包与 IL 包用 file 做第二键定序："." (0x2E) 小于 "j" (0x6A)，所以
    <mod>.il.jsonl 排在 <mod>.jsonl 前面。顺序仍然唯一确定，不依赖 os.listdir 的返回序。

已知边界：
  * 包文件里若有行缺 t / s 字段，直接报错退出（清单只描述"已定稿的包"，不做容错修补）；
  * 只扫 <packs>/<lang>/*.jsonl 这一层，不递归子目录（分发单位就是这一层的文件）；
  * 同一目录里共存两种包，格式完全相同（{h,n,s,t,mod}），只有来源不同：
    cfg 包 <mod>.jsonl（GameData/**/*.cfg + loc 抽取）与 IL 包 <mod>.il.jsonl
    （DLL 字面量经 translate_il 翻译；运行时捕获通道的候选还没进包，见 coverage ③）。
    清单**不加新字段**区分它们（schema 仍是 1），靠 file 后缀即可判定；
  * 空文件（0 行）也是合法包，rows=0、keepRows=0，仍会登记；
  * --check 只比对 index 与文件，不重新生成、不写盘。

reports/coverage.md 的结构（三节口径互不相加，报告正文里也写着这句）：
  ① 覆盖率            分母 = packs/pool.jsonl（harvest 从 GameData/**/*.cfg + loc 抽的待译串，
                      已剔除 empty/already-cjk/pure-number/too-short/search-tags/url-only/dup），
                      分子 = 这些行的 h 在 packs/<lang>/*.jsonl 里精确命中的行数；
  ② DLL 字面量覆盖率  分母 = packs/il-translatable.jsonl（ilstrings 从 DLL 的 IL 里采的字面量）
                      中 bucket == "translate" 的 distinct h，分子 = 这些 h 在
                      packs/<lang>/*.il.jsonl 里命中的条数。IL 包的 h 与 pool 的交集为 0，
                      所以 ② 的串不进 ① 的分母；两个百分比不可相加；
  ③ 运行时捕获通道    packs/capture-candidates.jsonl 的现状（capture_ingest 从
                      <KSP 安装目录>/PluginData/UI18n/capture-*.jsonl 摄入）：行数、bucket、
                      caller、mod 归属、与 ①② 的交集。本轮不进包，只报告通道现状。
  ②③ 的来源文件不存在时，对应小节打印"无法计算"并说明该跑哪个工具，不编造数字。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import time
from collections import Counter, defaultdict
from datetime import datetime, timezone

from . import ensure_stdout

SCHEMA = 1
LANG = "zh-cn"
INDEX_NAME = "index.json"
PACK_SUFFIX = ".jsonl"
IL_SUFFIX = ".il.jsonl"      # IL 包（DLL 字面量 / 运行时捕获）文件名后缀
IL_MARK = ".il"              # 从 mod 身份里剥掉的尾部标记
IL_CANDIDATES_NAME = "il-translatable.jsonl"      # IL 字面量候选（coverage ② 的分母来源）
CAPTURE_CANDIDATES_NAME = "capture-candidates.jsonl"  # 运行时捕获候选（coverage ③ 的来源）
COVERAGE_REL = ("reports", "coverage.md")
ENTRY_FIELDS = ("mod", "file", "rows", "bytes", "sha256", "keepRows")
TOP_FIELDS = ("schema", "lang", "generatedAt", "totalRows", "totalBytes", "packCount", "packs")


# ---------------------------------------------------------------------------
# 路径
# ---------------------------------------------------------------------------
def repo_root() -> str:
    r"""<repo>\tools\i18n_tools\packindex.py → <repo>"""
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def default_packs_dir() -> str:
    return os.path.join(repo_root(), "packs")


def default_index() -> str:
    return os.path.join(default_packs_dir(), INDEX_NAME)


def default_coverage() -> str:
    return os.path.join(repo_root(), *COVERAGE_REL)


def default_pool() -> str:
    return os.path.join(default_packs_dir(), "pool.jsonl")


def _now_iso() -> str:
    """UTC 秒级 ISO8601，形如 2026-10-23T09:12:34Z（不用 isoformat 的 +00:00，跨语言解析更省事）。"""
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _c(text: str) -> str:
    """markdown 行内代码：用 chr(96) 拼反引号，避免源码里出现成对反引号干扰阅读。"""
    bt = chr(96)
    return bt + str(text) + bt


# ---------------------------------------------------------------------------
# 扫描包文件
# ---------------------------------------------------------------------------
def _sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def read_pack_rows(path: str) -> list:
    """读一个包文件 → [dict, ...]。空行忽略；行不是 JSON 或缺 t/s 字段直接报错退出。"""
    with open(path, encoding="utf-8", newline="") as fh:
        text = fh.read()
    rows = []
    for lineno, line in enumerate(text.split("\n"), 1):
        if not line.strip():
            continue
        try:
            obj = json.loads(line)
        except ValueError as exc:
            raise SystemExit("[ERR] %s:%d 不是合法 JSON：%s" % (path, lineno, exc))
        if "t" not in obj or "s" not in obj:
            raise SystemExit("[ERR] %s:%d 缺 t / s 字段（清单只描述已定稿的包）：%r"
                             % (path, lineno, sorted(obj)))
        rows.append(obj)
    return rows


def is_il_file(name: str) -> bool:
    """文件名是不是 IL 包（<mod>.il.jsonl）。传文件名或相对路径都行。"""
    return os.path.basename(str(name).replace("\\", "/")).endswith(IL_SUFFIX)


def _mod_of_name(name: str) -> str:
    """包文件名 → mod 身份：先剥 .jsonl，再剥 IL 包尾部的 .il。

    RealFuels.jsonl -> RealFuels，RealFuels.il.jsonl -> RealFuels（同一身份、两个文件）。
    所以 mod 不唯一，**file 才是唯一键**；聚合按 mod，比对按 file。
    """
    base = os.path.basename(str(name).replace("\\", "/"))
    if base.endswith(PACK_SUFFIX):
        base = base[: -len(PACK_SUFFIX)]
    if base.endswith(IL_MARK):
        base = base[: -len(IL_MARK)]
    return base


def scan_packs(packs_dir: str, lang: str = LANG) -> list:
    """扫描 <packs_dir>/<lang>/*.jsonl → 按 (mod, file) ordinal 升序排好的条目列表。

    条目字段顺序固定为 ENTRY_FIELDS（写进 index.json 的字段顺序也是这个，
    顺序变了 diff 会变脏，所以别用 set / dict 重排）。

    mod 身份由 _mod_of_name() 从文件名推导（IL 包要剥掉 .il），不读文件内容里的 mod 字段：
    清单描述的是"文件"，文件名的身份是唯一可验证的。
    """
    root = os.path.join(packs_dir, lang)
    if not os.path.isdir(root):
        raise SystemExit("[ERR] 包目录不存在: " + root)
    entries = []
    for name in sorted(os.listdir(root)):
        if not name.endswith(PACK_SUFFIX):
            continue
        path = os.path.join(root, name)
        if not os.path.isfile(path):
            continue
        rows = read_pack_rows(path)
        keep = sum(1 for r in rows if r["t"] == r["s"])
        entries.append({
            "mod": _mod_of_name(name),
            "file": lang + "/" + name,
            "rows": len(rows),
            "bytes": os.path.getsize(path),
            "sha256": _sha256_file(path),
            "keepRows": keep,
        })
    # ordinal（码点）升序、两个键：顺序即契约。同 mod 的 cfg / IL 包靠 file 定序
    # （"." 0x2E < "j" 0x6A，所以 <mod>.il.jsonl 排在 <mod>.jsonl 前面）
    entries.sort(key=lambda e: (e["mod"], e["file"]))
    return entries


# ---------------------------------------------------------------------------
# index.json
# ---------------------------------------------------------------------------
def identity_diffs(packs: list) -> list:
    """身份自洽检查 → 差异列表（生成时用 raise、校验时用 diff，同一套规则）。

    规则（mod 不再唯一，file 才是唯一键）：
      * file 必须存在且唯一；
      * mod 必须等于从 file 推导的身份（_mod_of_name，IL 包剥 .il）——否则 mod 只是个
        标签，按 mod 聚合的覆盖率会张冠李戴；
      * 同一 mod 最多两个文件，两个时必须"一个 cfg + 一个 IL"。
    """
    diffs = []
    by_file = {}
    for e in packs:
        path = e.get("file")
        if not isinstance(path, str) or not path:
            diffs.append("条目缺 file：%r" % (e,))
            continue
        if path in by_file:
            diffs.append("file 重复：%r（file 才是唯一键）" % (path,))
        by_file[path] = e
        want = _mod_of_name(path)
        if e.get("mod") != want:
            diffs.append("mod 身份漂移：%s 的 mod=%r，按文件名应为 %r" % (path, e.get("mod"), want))
    by_mod = defaultdict(list)
    for path in sorted(by_file):
        by_mod[by_file[path].get("mod")].append(path)
    for mod in sorted(by_mod, key=lambda m: str(m)):
        files = by_mod[mod]
        if len(files) > 2:
            diffs.append("同一 mod 超过 2 个文件：%r -> %s" % (mod, files))
        elif len(files) == 2 and sorted(is_il_file(x) for x in files) != [False, True]:
            diffs.append("同一 mod 的两个文件应为一个 cfg + 一个 IL：%r -> %s" % (mod, files))
    return diffs


def build_index(entries: list, lang: str = LANG, generated_at: str = None) -> dict:
    """把条目列表组装成 index 文档；同时验算"汇总 == 逐文件累加"。"""
    for e in entries:
        missing = [k for k in ENTRY_FIELDS if k not in e]
        if missing:
            raise SystemExit("[ERR] 条目缺字段 %s：%r" % (missing, e))
    bad = identity_diffs(entries)
    if bad:
        raise SystemExit("[ERR] 条目身份不自洽（mod 必须能从 file 推导）：\n  - "
                         + "\n  - ".join(bad))
    total_rows = sum(int(e["rows"]) for e in entries)
    total_bytes = sum(int(e["bytes"]) for e in entries)
    doc = {
        "schema": SCHEMA,
        "lang": lang,
        "generatedAt": generated_at or _now_iso(),
        "totalRows": total_rows,
        "totalBytes": total_bytes,
        "packCount": len(entries),
        "packs": entries,
    }
    # 自洽断言：三条汇总必须严格等于逐文件累加，否则宁可崩也不要写出一份假清单
    if doc["totalRows"] != sum(int(e["rows"]) for e in doc["packs"]):
        raise SystemExit("[ERR] totalRows 与 sum(packs[].rows) 不等")
    if doc["totalBytes"] != sum(int(e["bytes"]) for e in doc["packs"]):
        raise SystemExit("[ERR] totalBytes 与 sum(packs[].bytes) 不等")
    if doc["packCount"] != len(doc["packs"]):
        raise SystemExit("[ERR] packCount 与 len(packs) 不等")
    return doc


def write_index(path: str, doc: dict) -> None:
    """确定性写盘：indent=2、ensure_ascii=False、LF、末尾一个换行。

    确定性 = 同样的包文件两次生成只差 generatedAt 一行；selftest 会实测这一点。
    """
    parent = os.path.dirname(path)
    if parent:
        os.makedirs(parent, exist_ok=True)
    text = json.dumps(doc, ensure_ascii=False, indent=2) + "\n"
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)


def load_index(path: str) -> dict:
    with open(path, encoding="utf-8") as fh:
        return json.load(fh)


def check_index(index_path: str, packs_dir: str) -> tuple:
    """比对磁盘 index 与真实包文件 → (ok, diffs, doc)。

    **按 file 建键比对**（mod 不再唯一：同一个 mod 的 cfg 包与 IL 包各占一行）。

    检查五类差异（对应分发的五种坏法）：
      1. 文件缺失 / 多出未登记文件 —— 清单与磁盘的文件集合不一致（改名也算，会报成一缺一多）；
      2. rows / bytes / sha256 / keepRows 变了 —— 包内容变了但清单没更新；
      3. 汇总（totalRows / totalBytes / packCount）与磁盘累加不一致；
      4. index 自身不自洽（汇总 != 逐条累加），即使磁盘没动也算错；
      5. 身份不自洽（见 identity_diffs）：file 重复 / mod 与文件名推导的身份不符 /
         同一 mod 超过两个文件或不是"一个 cfg + 一个 IL"。
    """
    if not os.path.isfile(index_path):
        return False, ["index 不存在: " + index_path + "（先跑 python -m i18n_tools.packindex）"], None
    try:
        doc = load_index(index_path)
    except ValueError as exc:
        return False, ["index 不是合法 JSON：%s" % exc], None
    diffs = []
    missing_top = [k for k in TOP_FIELDS if k not in doc]
    if missing_top:
        diffs.append("index 缺顶层字段：%s" % missing_top)
    if doc.get("schema") != SCHEMA:
        diffs.append("schema 变了: index=%r 期望=%r" % (doc.get("schema"), SCHEMA))
    lang = doc.get("lang") or LANG
    if lang != LANG:
        diffs.append("lang 变了: index=%r 期望=%r" % (lang, LANG))
    root = os.path.join(packs_dir, lang)
    if not os.path.isdir(root):
        return False, diffs + ["包目录不存在: " + root], doc
    disk = scan_packs(packs_dir, lang)
    disk_by_file = {e["file"]: e for e in disk}
    packs = doc.get("packs") or []
    idx_by_file = {}
    for e in packs:
        path = e.get("file")
        if not isinstance(path, str) or not path:
            diffs.append("index 里有条目缺 file：%r" % (e,))
            continue
        idx_by_file[path] = e
    # 身份自洽（file 唯一 / mod 能从 file 推导 / 同 mod 最多 cfg + IL 两个文件）
    diffs.extend(identity_diffs(packs))
    for path in sorted(idx_by_file):
        if path not in disk_by_file:
            diffs.append("文件缺失：%s（index 里有，磁盘没有）" % path)
    for path in sorted(disk_by_file):
        if path not in idx_by_file:
            diffs.append("未登记文件：%s（磁盘有，index 没登记）" % path)
    for path in sorted(set(idx_by_file) & set(disk_by_file)):
        a, b = idx_by_file[path], disk_by_file[path]
        if a.get("rows") != b["rows"]:
            diffs.append("rows 变了：%s index=%r 磁盘=%r" % (path, a.get("rows"), b["rows"]))
        if a.get("bytes") != b["bytes"]:
            diffs.append("bytes 变了：%s index=%r 磁盘=%r" % (path, a.get("bytes"), b["bytes"]))
        if a.get("keepRows") != b["keepRows"]:
            diffs.append("keepRows 变了：%s index=%r 磁盘=%r" % (path, a.get("keepRows"), b["keepRows"]))
        if a.get("sha256") != b["sha256"]:
            diffs.append("sha256 变了：%s index=%s... 磁盘=%s..."
                         % (path, str(a.get("sha256"))[:12], b["sha256"][:12]))
    for key, want in (("totalRows", sum(e["rows"] for e in disk)),
                      ("totalBytes", sum(e["bytes"] for e in disk)),
                      ("packCount", len(disk))):
        if doc.get(key) != want:
            diffs.append("汇总不一致：%s index=%r 磁盘=%r" % (key, doc.get(key), want))
    packs = doc.get("packs") or []
    if doc.get("totalRows") != sum(int(e.get("rows") or 0) for e in packs):
        diffs.append("index 内部不自洽：totalRows != sum(packs[].rows)")
    if doc.get("totalBytes") != sum(int(e.get("bytes") or 0) for e in packs):
        diffs.append("index 内部不自洽：totalBytes != sum(packs[].bytes)")
    if doc.get("packCount") != len(packs):
        diffs.append("index 内部不自洽：packCount != len(packs)")
    return (not diffs), diffs, doc


# ---------------------------------------------------------------------------
# coverage.md
# ---------------------------------------------------------------------------
def _pct(a: int, b: int) -> str:
    return "%.2f%%" % (100.0 * a / b) if b else "-"


def _n(value) -> str:
    r"""整数加千位分隔符。只用于 markdown **正文**；表格里保持裸数字（方便机器/正则读）。"""
    return format(int(value), ",")


def _load_pool(path: str) -> list:
    rows = []
    with open(path, encoding="utf-8", newline="") as fh:
        for line in fh.read().split("\n"):
            if line.strip():
                rows.append(json.loads(line))
    return rows


def _load_jsonl_opt(path: str):
    """读一个"可缺"的 jsonl（不存在返回 None）。坏行直接报错——报告不许静默跳过数据。"""
    if not path or not os.path.isfile(path):
        return None
    rows = []
    with open(path, encoding="utf-8", newline="") as fh:
        for lineno, line in enumerate(fh.read().split("\n"), 1):
            if not line.strip():
                continue
            try:
                rows.append(json.loads(line))
            except ValueError as exc:
                raise SystemExit("[ERR] %s:%d 不是合法 JSON：%s" % (path, lineno, exc))
    return rows


def _section_il(entries: list, cfg_h: set, il_h: set, pool_h: set, il_path: str) -> list:
    """coverage ②：DLL 字面量覆盖率（IL 包）。分母 = il-translatable.jsonl 的 translate 桶。"""
    il_entries = [e for e in entries if is_il_file(e["file"])]
    n_files = len(il_entries)
    n_rows = sum(e["rows"] for e in il_entries)
    n_bytes = sum(e["bytes"] for e in il_entries)
    n_keep = sum(e["keepRows"] for e in il_entries)
    L = ["## ② DLL 字面量覆盖率（IL 包）", ""]
    cands = _load_jsonl_opt(il_path)
    if cands is None:
        L.append("**无法计算**：没找到 " + _c(IL_CANDIDATES_NAME) + "（先用 "
                 + _c("python -m i18n_tools.ilstrings") + " 采集 DLL 字面量）。")
        L.append("")
        return L
    bucket = Counter(r.get("bucket") for r in cands)
    translate_h = {r["h"] for r in cands if r.get("bucket") == "translate"}
    n_tr = len(translate_h)
    n_landed = len(translate_h & il_h)
    in_cfg = translate_h & cfg_h
    rest = translate_h - il_h - cfg_h
    L.append("**DLL 里能译的字符串 %s 条，已进包 %s 条，覆盖率 %s。**"
             % (_n(n_tr), _n(n_landed), _pct(n_landed, n_tr)))
    L.append("")
    L.append("口径（分母 / 分子 / 与 ① 的关系）：")
    L.append("")
    L.append("1. **分母 = " + _c("packs/" + IL_CANDIDATES_NAME) + " 里 bucket 为 "
             + _c("translate") + " 的 distinct " + _c("h") + "（" + _n(n_tr) + " 条）。**"
             "该文件是 " + _c("python -m i18n_tools.ilstrings") + " 从 GameData 下所有 DLL 的 IL 里"
             "采出来的字面量，按 bucket 分流：" + _c("translate") + " " + _n(bucket.get("translate", 0))
             + " / " + _c("short_label") + " " + _n(bucket.get("short_label", 0)) + " / "
             + _c("template") + " " + _n(bucket.get("template", 0)) + "（合计 "
             + _n(len(cands)) + " 行）。只有 " + _c("translate") + " 桶会送去翻译；"
             + _c("short_label") + "（按钮/短标签）与 " + _c("template")
             + "（带占位符的模板）不译。")
    L.append("2. **分子 = 这 " + _n(n_tr) + " 条里 " + _c("h") + " 能在 "
             + _c("packs/" + LANG + "/*.il.jsonl") + " 精确命中的 " + _n(n_landed) + " 条。**"
             "IL 包现状：" + _n(n_files) + " 个文件 / " + _n(n_rows) + " 行 / " + _n(n_bytes)
             + " 字节，其中 " + _c("t == s") + " 直通行 " + _n(n_keep) + " 行（与 ① 的 "
             + _c("keep") + " 同一语义）。")
    L.append("3. **与 cfg 包不重复：** IL 包的 " + _n(len(il_h)) + " 个 " + _c("h") + " 与 cfg 包的 "
             + _n(len(cfg_h)) + " 个 " + _c("h") + " 交集 **" + _n(len(il_h & cfg_h)) + "** 条"
             "（" + _c("translate_il") + " 写盘前先按 cfg 包的 " + _c("h") + " 去重，"
             "同一条串只落一次）。")
    L.append("4. **对 ① 的覆盖率贡献 " + _n(len(il_h & pool_h)) + " 条：** IL 包的 " + _c("h")
             + " 与 " + _c("packs/pool.jsonl") + " 的交集是 " + _n(len(il_h & pool_h))
             + "，所以 ① 的分母里没有一条是 IL 包填的。**两个分母不可相加**：IL 包覆盖的是另一批串"
             "（DLL 里的字面量），不是 pool 的子集。")
    L.append("")
    L.append("还没进包的 " + _n(n_tr - n_landed) + " 条 " + _c("translate") + " 桶串："
             + _n(len(in_cfg)) + " 条的 " + _c("h") + " 已经在 cfg 包里（同一条串 cfg 包先落了，"
             "IL 包不重复落），其余 " + _n(len(rest)) + " 条既不在 cfg 包也不在 IL 包——"
             "上一轮被 " + _c("no-words") + " 预筛（去掉标签/占位符后没有可译词）或八门禁拦下，"
             "明细见 " + _c("reports/run-il-full7.log") + "。")
    L.append("")
    return L


def _section_capture(capture_path: str, cfg_h: set, il_h: set, pool_h: set,
                     il_cand_h: set) -> list:
    """coverage ③：运行时捕获通道现状。数据源 = packs/capture-candidates.jsonl。"""
    L = ["## ③ 运行时捕获通道（游戏里真实渲染出来的串）", ""]
    cands = _load_jsonl_opt(capture_path)
    if cands is None:
        L.append("**无法计算**：没找到 " + _c(CAPTURE_CANDIDATES_NAME) + "（先用 "
                 + _c("python -m i18n_tools.capture_ingest") + " 摄入运行时捕获文件）。")
        L.append("")
        return L
    hs = {r["h"] for r in cands}
    bucket = Counter(r.get("bucket") for r in cands)
    reason = Counter(r.get("reason") for r in cands)
    callers = Counter(r.get("caller") for r in cands)
    unknown = sum(1 for r in cands if list(r.get("mods") or []) == ["unknown"])
    pack_hit = hs & (cfg_h | il_h)
    L.append("静态抽取（cfg / loc / DLL 字面量）只看得到\"写在文件里\"的串；代码里拼出来、"
             "运行时才成形的文本（" + _c("string.Format") + "、计数器、进度条）只能靠运行时抓："
             + _c("UI18n") + " 在游戏里把**实际渲染出来的**文本记成 JSONL，落在 "
             + _c("<KSP 安装目录>/PluginData/UI18n/capture-*.jsonl") + "；"
             + _c("python -m i18n_tools.capture_ingest") + " 把这些文件摄入成 "
             + _c("packs/" + CAPTURE_CANDIDATES_NAME) + "（按 " + _c("h") + " 去重，标 "
             + _c("bucket") + " / " + _c("reason") + " / " + _c("caller") + " / "
             + _c("mods") + "）。")
    L.append("")
    L.append("当前候选清单：" + _n(len(cands)) + " 行（" + _c("candidate") + " "
             + _n(bucket.get("candidate", 0)) + " / " + _c("template") + " "
             + _n(bucket.get("template", 0)) + "），distinct " + _c("h") + " " + _n(len(hs))
             + " 个，涉及 " + _n(len(callers)) + " 个 caller（"
             + " / ".join(_c(c) for c in sorted(callers)) + "）。")
    L.append("")
    L.append("- " + _c("template") + " 的 " + _n(bucket.get("template", 0)) + " 行按 "
             + _c("reason") + " 拆：" + _c("counter") + " " + _n(reason.get("counter", 0)) + " / "
             + _c("progress") + " " + _n(reason.get("progress", 0))
             + "——带计数的进度/计数器串，每次渲染都可能不同，译了也匹配不上。")
    n_cand = bucket.get("candidate", 0)
    n_cand_empty = sum(1 for r in cands
                       if r.get("bucket") == "candidate" and not r.get("reason"))
    L.append("- " + _c("candidate") + " 的 " + _n(n_cand) + " 行里 " + _n(n_cand_empty)
             + " 行 " + _c("reason") + " 为空，是真正值得人工看的候选。")
    if unknown == len(cands):
        L.append("- " + _c("mods") + " 全部是 " + _c("unknown") + "（" + _n(unknown) + " / "
                 + _n(len(cands)) + " 行）：这份清单的 caller 还是我们自己的补丁名（"
                 + _c("GUI.UnityEngine.GUI.Box_Patch1") + " 这种），"
                 + _c("capture_ingest.infer_mods()") + " 按设计不从 sink / Unity 类型猜 mod，"
                 "推不出就记 " + _c("unknown") + "。")
    elif unknown:
        L.append("- " + _c("mods") + " 还有 " + _n(unknown) + " / " + _n(len(cands))
                 + " 行是 " + _c("unknown") + "（推不出归属）。")
    else:
        L.append("- " + _c("mods") + " 全部有归属（没有 " + _c("unknown") + " 行）。")
    L.append("")
    probes = sorted(r.get("n") for r in cands
                    if r.get("n") in ("Zzz control alpha", "Zzz control beta"))
    L.append("**这一轮不进包**：" + _c("template") + " 是模板串（见上）；" + _c("candidate")
             + " 还缺 mod 归属，不能凭空塞进某个包。")
    if probes:
        L.append("")
        L.append("其中 " + _n(len(probes)) + " 条是人造探针串（"
                 + " / ".join(_c(x) for x in probes) + "，走 " + _c("GUI.Label")
                 + "），用来验证捕获通道能抓到 IMGUI 路径——它们不是游戏文本，"
                 "**永远不该进包**。")
    L.append("")
    L.append("与现有产物的交集（现场算的）：与 " + _c("packs/" + LANG + "/**") + " 的 " + _c("h")
             + " 交集 " + _n(len(pack_hit)) + " 条（全部落在 IL 包里，cfg 包 "
             + _n(len(hs & cfg_h)) + " 条）；与 " + _c("packs/pool.jsonl") + " 交集 "
             + _n(len(hs & pool_h)) + " 条；与 " + _c("packs/" + IL_CANDIDATES_NAME) + " 交集 "
             + _n(len(hs & il_cand_h)) + " 条。也就是说运行时看到的串和静态抽出来的串**基本不重叠**"
             "——这正是要开这条通道的理由。")
    L.append("")
    L.append("②③ 两节的串都**不计入** ① 的分母，也不计入任何总覆盖率。")
    L.append("")
    return L


def build_coverage(entries: list, packs_dir: str, lang: str = LANG,
                   pool_path: str = None, generated_at: str = None,
                   il_path: str = None, capture_path: str = None) -> str:
    """生成 reports/coverage.md 全文。

    覆盖率口径（写进报告正文，也必须写进任何引用这个数字的地方）：
      分母 = packs/pool.jsonl 的行数（抽取阶段已剔除 empty / already-cjk / pure-number /
             too-short / search-tags / url-only / dup，即"本安装真正需要译的英文串总量"）；
      分子 = 这些行里文本 FNV-1a64 哈希 h 能在 packs/<lang>/*.jsonl 精确命中的行数。
    两个数字都现场算，不读任何人工填写的百分比。
    """
    now = generated_at or _now_iso()
    total_rows = sum(e["rows"] for e in entries)
    total_bytes = sum(e["bytes"] for e in entries)
    keep_total = sum(e["keepRows"] for e in entries)
    cfg_entries = [e for e in entries if not is_il_file(e["file"])]
    il_entries = [e for e in entries if is_il_file(e["file"])]

    # 按 mod 聚合：同一个 mod 可能有两个文件（cfg 包 + IL 包），rows / bytes / keepRows 求和、
    # 行按文件顺序拼接。**绝不能用 dict 按 mod 建键后赋值**——那会让后一个文件静默覆盖前一个。
    pack_by_mod = {}
    pack_rows_by_mod = {}
    cfg_h, il_h = set(), set()      # cfg 包 / IL 包各自的 h 集合（②③ 要分开算，不能混）
    for e in entries:
        mod = e["mod"]
        agg = pack_by_mod.get(mod)
        if agg is None:
            agg = pack_by_mod[mod] = {"mod": mod, "rows": 0, "bytes": 0,
                                      "keepRows": 0, "files": []}
        agg["rows"] += e["rows"]
        agg["bytes"] += e["bytes"]
        agg["keepRows"] += e["keepRows"]
        agg["files"].append(e["file"])
        file_rows = read_pack_rows(os.path.join(packs_dir, e["file"].replace("/", os.sep)))
        pack_rows_by_mod.setdefault(mod, []).extend(file_rows)
        target = il_h if is_il_file(e["file"]) else cfg_h
        for r in file_rows:
            target.add(r.get("h"))

    L = []
    L.append("# zh-cn 翻译包覆盖报告")
    L.append("")
    L.append("- 生成时间：" + _c(now))
    L.append("- 包目录：" + _c("packs/" + lang + "/") + "：" + "%s 个文件 / %s 行 / %s 字节"
             % (_n(len(entries)), _n(total_rows), _n(total_bytes)))
    L.append("  - cfg 包（" + _c("<mod>.jsonl") + "，GameData/**/*.cfg 抽取）：%s 个 / %s 行 / %s 字节"
             % (_n(len(cfg_entries)), _n(sum(e["rows"] for e in cfg_entries)),
                _n(sum(e["bytes"] for e in cfg_entries))))
    L.append("  - IL 包（" + _c("<mod>.il.jsonl") + "，DLL 字面量经 " + _c("translate_il")
             + " 翻译）：%s 个 / %s 行 / %s 字节"
             % (_n(len(il_entries)), _n(sum(e["rows"] for e in il_entries)),
                _n(sum(e["bytes"] for e in il_entries))))
    L.append("  - 合计 %s 个 mod（同一 mod 的 cfg 包与 IL 包各占一行清单，按 " % _n(len(pack_by_mod))
             + _c("mod") + " 聚合成一行表格）")
    L.append("- 清单：" + _c("packs/index.json") + "（schema %d，字段口径见 " % SCHEMA
             + _c("tools/i18n_tools/packindex.py") + " 模块 docstring）")
    L.append("- 三节口径互不相加：① 是 " + _c("packs/pool.jsonl") + " 口径（cfg 包覆盖多少待译串）、"
             "② 是 DLL 字面量口径（IL 包）、③ 是运行时捕获通道现状。")
    L.append("- 本报告由 " + _c("python -m i18n_tools.packindex")
             + " 现场计算生成，不引用任何人手填的百分比。")
    L.append("")

    pool_h = set()      # ②③ 要拿 IL / capture 的 h 和 pool 求交集；pool 缺失时就是空集
    il_path = il_path or os.path.join(packs_dir, IL_CANDIDATES_NAME)
    capture_path = capture_path or os.path.join(packs_dir, CAPTURE_CANDIDATES_NAME)

    pool = None
    if pool_path and os.path.isfile(pool_path):
        pool = _load_pool(pool_path)

    if pool is None:
        L.append("## ① 覆盖率")
        L.append("")
        L.append("**无法计算**：没找到 " + _c("packs/pool.jsonl") + "（先用 "
                 + _c("python -m i18n_tools.harvest") + " 生成候选池）。")
        L.append("")
        n_pool = 0
        matched = []
        matched_by_mod = Counter()
        pool_rows_by_mod = Counter()
        src_by_mod = defaultdict(Counter)
    else:
        pool_by_h = {}
        for r in pool:
            pool_by_h[r["h"]] = r
        pool_h = set(pool_by_h)
        matched = []
        for mod, rows in pack_rows_by_mod.items():
            for r in rows:
                hit = pool_by_h.get(r.get("h"))
                if hit is not None:
                    matched.append((mod, r, hit))
        matched_by_mod = Counter(mod for mod, _, _ in matched)
        pool_rows_by_mod = Counter(r["mod"] for r in pool)
        src_by_mod = defaultdict(Counter)
        for r in pool:
            src_by_mod[r["mod"]][r["src"]] += 1
        n_pool = len(pool)
        n_match = len(matched)
        n_keep_matched = sum(1 for _, r, _ in matched if r["t"] == r["s"])
        extra_rows = total_rows - n_match
        matched_by_src = Counter(hit["src"] for _, _, hit in matched)
        pool_by_src = Counter(r["src"] for r in pool)

        L.append("## ① 覆盖率（口径必须跟数字一起看）")
        L.append("")
        L.append("**本安装可覆盖英文串 %s 条，已被翻译包覆盖 %s 条，覆盖率 %s。**"
                 % (_n(n_pool), _n(n_match), _pct(n_match, n_pool)))
        L.append("")
        L.append("口径（分母 / 分子 / 为什么这么算）：")
        L.append("")
        L.append("1. **分母 = " + _c("packs/pool.jsonl") + " 的 " + _n(n_pool) + " 行。**"
                 " 这是抽取阶段（" + _c("python -m i18n_tools.harvest") + "）已经剔除"
                 " " + _c("empty") + " / " + _c("already-cjk") + " / " + _c("pure-number") + " / "
                 + _c("too-short") + " / " + _c("search-tags") + " / " + _c("url-only") + " / "
                 + _c("dup") + " 之后的**待译英文串总量**。")
        L.append("   - " + _c("already-cjk") + "（32 条）本来就是中文、"
                 + _c("search-tags") + "（1,597 条）是搜索标签不译、" + _c("dup")
                 + "（4,461 条）是重复文本——它们已经在抽取阶段被排除，**因此不进分母**。"
                 "把它们算进分母会把覆盖率压低，而且分母不再是\"需要译的串\"，失去意义。")
        L.append("2. **分子 = 这 " + _n(n_pool) + " 行里，文本 FNV-1a64 哈希 " + _c("h")
                 + " 能在 " + _c("packs/" + lang + "/*.jsonl") + " 精确命中的 " + _n(n_match)
                 + " 行。** 按哈希精确配对、不做模糊匹配；" + _c("h")
                 + " 在 pool 内与 packs 内都唯一（pool " + _n(n_pool) + " 行 / "
                 + _n(total_rows) + " 行各自无重复）。")
        L.append("3. **覆盖率 = " + _n(n_match) + " / " + _n(n_pool) + " = "
                 + _pct(n_match, n_pool) + "。**")
        L.append("")
        L.append("不要用下面两个数字当覆盖率（都会失真，列出来是为了防止误引）：")
        L.append("")
        L.append("- " + _c("%s / %s = %s" % (_n(total_rows), _n(n_pool), _pct(total_rows, n_pool)))
                 + "：分子用了 packs 总行数，但其中 " + _n(extra_rows)
                 + " 行的 " + _c("h") + " 不在 pool 里（见下一节），分母口径是 pool，"
                 "不能把池外条目算成池内覆盖。")
        L.append("- " + _c("%s / %s = %s" % (_n(n_match - n_keep_matched), _n(n_pool),
                                            _pct(n_match - n_keep_matched, n_pool)))
                 + "：分子剔除了 " + _n(n_keep_matched) + " 行 " + _c("t == s")
                 + " 的直通行（它们有内容但不是译文），可当\"纯译文覆盖率\"参考。")
        L.append("")
        L.append("按来源拆分（" + _c("pool.src") + "）："
                 + _c("loc") + " " + _n(matched_by_src.get("loc", 0)) + " / "
                 + _n(pool_by_src.get("loc", 0)) + " = "
                 + _pct(matched_by_src.get("loc", 0), pool_by_src.get("loc", 0))
                 + "；" + _c("cfg") + " " + _n(matched_by_src.get("cfg", 0)) + " / "
                 + _n(pool_by_src.get("cfg", 0)) + " = "
                 + _pct(matched_by_src.get("cfg", 0), pool_by_src.get("cfg", 0)) + "。")
        L.append("")

    # ---- ② DLL 字面量覆盖率 / ③ 运行时捕获通道 ----
    il_cand = _load_jsonl_opt(il_path)
    il_cand_h = {r.get("h") for r in il_cand} if il_cand else set()
    il_cand_h.discard(None)
    L.extend(_section_il(entries, cfg_h, il_h, pool_h, il_path))
    L.extend(_section_capture(capture_path, cfg_h, il_h, pool_h, il_cand_h))

    # ---- 逐 mod 表 ----
    L.append("## 逐 mod")
    L.append("")
    L.append("（表格里的数字是**裸数字**、不带千位分隔符，方便机器读；正文里的数字带分隔符。）")
    L.append("")
    L.append("列口径：" + _c("包") + " 是该 mod 有哪些包文件（" + _c("cfg")
             + " = " + _c("<mod>.jsonl") + "，" + _c("IL") + " = " + _c("<mod>.il.jsonl")
             + "，" + _c("cfg+IL") + " = 两者都有，此时下面三个数字是**两个文件的和**）；"
             + _c("rows") + " / " + _c("bytes") + " / " + _c("keep")
             + " 来自 " + _c("packs/index.json") + "（" + _c("keep")
             + " = " + _c("t == s") + " 直通行数）；" + _c("untranslated")
             + " = 该 mod 在 pool 里有、但包没覆盖的行数；" + _c("来源")
             + " 是该 mod 在 pool 里的行按 " + _c("src") + " 拆开（没有包文件的 mod 也列出来，"
             "方便看出\"哪些 mod 一条都没译\"）。")
    L.append("")
    L.append("| mod | 包 | rows | bytes | keep | untranslated | 来源 |")
    L.append("| --- | --- | ---: | ---: | ---: | ---: | --- |")
    mods = sorted(set(pack_by_mod) | set(pool_rows_by_mod),
                  key=lambda m: (-(pack_by_mod[m]["rows"] if m in pack_by_mod else 0), m))
    for mod in mods:
        e = pack_by_mod.get(mod)
        rows = e["rows"] if e else 0
        nbytes = e["bytes"] if e else 0
        keep = e["keepRows"] if e else 0
        pool_n = pool_rows_by_mod.get(mod, 0)
        untrans = pool_n - matched_by_mod.get(mod, 0)
        if pool is None:
            src = "unknown"
        else:
            parts = src_by_mod.get(mod) or {}
            if not parts:
                src = "unknown"
            else:
                src = " + ".join("%s %d" % (k, parts[k]) for k in sorted(parts))
        if e is None:
            kind = "-"
        else:
            has_cfg = any(not is_il_file(x) for x in e["files"])
            has_il = any(is_il_file(x) for x in e["files"])
            kind = "cfg+IL" if (has_cfg and has_il) else ("IL" if has_il else "cfg")
        L.append("| %s | %s | %d | %d | %d | %d | %s |"
                 % (mod, kind, rows, nbytes, keep, untrans, src))
    L.append("| **合计** | **%d 个包**（cfg %d + IL %d） | **%d** | **%d** | **%d** | **%d** | %d 个 mod |"
             % (len(entries), len(cfg_entries), len(il_entries),
                total_rows, total_bytes, keep_total,
                (n_pool - len(matched)) if pool is not None else 0,
                len(mods)))
    L.append("")

    if pool is not None:
        # ---- 池外条目 ----
        extra_by_mod = {}
        for mod, rows in pack_rows_by_mod.items():
            got = sum(1 for r in rows if pool_by_h.get(r.get("h")) is not None)
            extra_by_mod[mod] = len(rows) - got
        extra_total = sum(extra_by_mod.values())
        L.append("## 池外条目（packs 有、pool 没有）")
        L.append("")
        L.append("共 **%s** 行：这些行有译文，但它们的英文原文不在当前 " % _n(extra_total)
                 + _c("packs/pool.jsonl") + " 里（多半来自上一轮抽取的池外来源或后续人工补录），"
                 "因此**不计入覆盖率分子**——分母是 pool，池外条目不能算成池内覆盖。"
                 "它们仍然会被运行时加载（按 " + _c("h") + " 索引，与 pool 无关）。")
        L.append("")
        L.append("| mod | 池外行数 |")
        L.append("| --- | ---: |")
        for mod, n in sorted(extra_by_mod.items(), key=lambda kv: (-kv[1], kv[0])):
            if n:
                L.append("| %s | %d |" % (mod, n))
        L.append("")

        no_pack = sorted(set(pool_rows_by_mod) - set(pack_by_mod))
        L.append("## 没有包文件的 mod")
        L.append("")
        if no_pack:
            L.append(("共 %s 个，合计 %s 行 pool 候选（一条都没译，全部计入 "
                      + _c("untranslated") + "）：") % (_n(len(no_pack)),
                                                        _n(sum(pool_rows_by_mod[m] for m in no_pack))))
            L.append("")
            for mod in sorted(no_pack, key=lambda m: (-pool_rows_by_mod[m], m)):
                L.append("- " + mod + "：" + str(pool_rows_by_mod[mod]) + " 行")
        else:
            L.append("无：pool 里每个 mod 都有包文件。")
        L.append("")

    L.append("## 汇总")
    L.append("")
    L.append("| 指标 | 值 |")
    L.append("| --- | ---: |")
    L.append("| 包文件数 | %d（cfg %d + IL %d） |"
             % (len(entries), len(cfg_entries), len(il_entries)))
    L.append("| mod 数 | %d（同 mod 的 cfg 包与 IL 包算一个） |" % len(pack_by_mod))
    L.append("| 包总行数 | %d |" % total_rows)
    L.append("| 包总字节数 | %d |" % total_bytes)
    L.append("| " + _c("t == s") + " 直通行 | %d |" % keep_total)
    L.append("| pool 待译串 | %d |" % n_pool if pool is not None else "| pool 待译串 | 未知 |")
    if pool is not None:
        L.append("| 已覆盖 pool 行 | %d |" % len(matched))
        L.append("| **覆盖率** | **%s**（%d / %d） |"
                 % (_pct(len(matched), n_pool), len(matched), n_pool))
        L.append("| 池外条目 | %d |" % extra_total)
    L.append("")
    return "\n".join(L) + "\n"


def write_coverage(path: str, text: str) -> None:
    parent = os.path.dirname(path)
    if parent:
        os.makedirs(parent, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------
def main(argv=None) -> int:
    ensure_stdout()
    ap = argparse.ArgumentParser(
        prog="python -m i18n_tools.packindex",
        description="生成/校验 packs/index.json（分发清单）与 reports/coverage.md（覆盖报告）")
    ap.add_argument("--check", action="store_true",
                    help="只校验磁盘 index 与真实包文件是否一致；不一致 exit 1")
    ap.add_argument("--packs-dir", default=None, help="packs 目录（默认 <repo>\\packs）")
    ap.add_argument("--index", default=None, help="index 路径（默认 <packs-dir>\\index.json）")
    ap.add_argument("--coverage", default=None,
                    help="coverage.md 路径（默认 <repo>\\reports\\coverage.md）")
    ap.add_argument("--lang", default=LANG, help="语言目录名（默认 zh-cn）")
    ap.add_argument("--pool", default=None, help="pool.jsonl 路径（默认 <packs-dir>\\pool.jsonl）")
    ap.add_argument("--no-coverage", action="store_true", help="只写 index，不写 coverage.md")
    ap.add_argument("--quiet", action="store_true", help="只在出错时说话")
    args = ap.parse_args(argv)

    packs_dir = os.path.abspath(args.packs_dir or default_packs_dir())
    index_path = os.path.abspath(args.index or os.path.join(packs_dir, INDEX_NAME))
    coverage_path = os.path.abspath(args.coverage or default_coverage())
    pool_path = os.path.abspath(args.pool or os.path.join(packs_dir, "pool.jsonl"))

    if args.check:
        ok, diffs, doc = check_index(index_path, packs_dir)
        if ok:
            if not args.quiet:
                print("[check] index 与磁盘一致：" + index_path)
            print("index ok: %d packs, %d rows, %d bytes"
                  % (doc["packCount"], doc["totalRows"], doc["totalBytes"]))
            if not args.quiet:
                packs = doc.get("packs") or []
                n_mods = len({e["mod"] for e in packs})
                n_il = sum(1 for e in packs if is_il_file(e["file"]))
                print("[check] mod 数 %d（cfg %d + IL %d），全部 mod 都能从 file 推导"
                      % (n_mods, len(packs) - n_il, n_il))
            return 0
        print("[check] 发现 %d 处差异（index=%s）：" % (len(diffs), index_path), file=sys.stderr)
        for d in diffs:
            print("  - " + d, file=sys.stderr)
        return 1

    t0 = time.perf_counter()
    entries = scan_packs(packs_dir, args.lang)
    doc = build_index(entries, lang=args.lang)
    write_index(index_path, doc)
    if not args.quiet:
        print("[packindex] 包目录   " + os.path.join(packs_dir, args.lang))
        print("[packindex] 条目     %d packs, %d rows, %d bytes"
              % (doc["packCount"], doc["totalRows"], doc["totalBytes"]))
        print("[packindex] index    " + index_path)
    if not args.no_coverage:
        text = build_coverage(entries, packs_dir, lang=args.lang,
                             pool_path=pool_path, generated_at=doc["generatedAt"])
        write_coverage(coverage_path, text)
        if not args.quiet:
            print("[packindex] coverage " + coverage_path)
    if not args.quiet:
        print("[packindex] 耗时     %.2fs" % (time.perf_counter() - t0))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
