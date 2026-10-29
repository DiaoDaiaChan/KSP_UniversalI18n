# -*- coding: utf-8 -*-
"""字典包内容审计（分发前的最后一道硬门禁）。

这不是「翻译质量」检查，而是「运行时会炸/会错位」的机械检查。口径全部来自
同一份实现，避免审计脚本与生产脚本各自漂移:

  h == fnv1a64(normalize(s))        # tools/i18n_tools/fnv.py + textnorm.py
  n == normalize(s)
  t 非空                            # 空译文会把英文变成空按钮
  <<n>> 占位符多重集相等            # 丢了就是格式化崩溃/错位
  富文本标签多重集相等              # 丢了就是 UI 破版
  {n} / %s 格式符多重集相等         # 同上（TMP 与 string.Format 两条路）
  跨文件同 h 必须同 t               # 运行时只按 h 查表，同 h 不同 t = 输出不确定
  s 不含 CJK（键引用行 s 以 # 开头，允许）

另外读 packs/terms.jsonl 做**消歧术语**检查（docs/10 的受管术语库）：
  domain 命中的包里，原文命中 term 的行：
    - 译文含 forbidden 禁译词       → term_forbidden
    - 译文不含该 domain 的标准 zh   → term_mismatch
  这两类默认只报 **notice**，不计入 bad_total、不 exit 1：存量包里的译法是否真的错，
  需要人工逐条裁决；裁决完再统一用 --strict-terms 升为 error。

用法:
    python -m i18n_tools.audit                 # 审计 packs/zh-cn（含术语 notice）
    python -m i18n_tools.audit --lang en-us
    python -m i18n_tools.audit --packs-dir <dir> --quiet
    python -m i18n_tools.audit --terms <file> --strict-terms
    python -m i18n_tools.audit --selftest      # 自检（含 8 条负例）

退出码: 0 = PASS，1 = 有违规（CI 用它当门禁；术语 notice 默认不阻断）。
"""
from __future__ import annotations

import argparse
import collections
import json
import os
import re
import sys
import tempfile

from .fnv import hash_text
from .textnorm import normalize
from .resolve_keyrefs import _PLACEHOLDER_RE, _TMP_TAG_RE, is_key_ref

_FORMAT_RE = re.compile(r"\{\d+\}|%[sdif]")
_CJK_RE = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff]")

CHECK_NAMES = (
    "bad_h", "bad_n", "empty_t", "bad_placeholder", "bad_richtext",
    "bad_format", "cjk_in_source", "dup_h_diff_t", "bad_row",
)

#: 术语检查的两类 notice（不进 CHECK_NAMES：存量包变红要人工裁决，不能直接阻断）
TERM_CHECK_NAMES = ("term_forbidden", "term_mismatch")

#: term → 预编译正则。术语只有十几条，缓存一次即可，避免每行都 re.compile。
_TERM_PATTERN_CACHE = {}


def _multiset(text: str, pattern) -> collections.Counter:
    return collections.Counter(pattern.findall(text))


def _pack_mod(name: str) -> str:
    """包文件名 → modId，规则与 api.pack_mod / packindex._mod_of_name 同源。

    先剥 .jsonl，再剥精确后缀 .il / .cfg；其余原样（modId 本身可以带点号）。
    """
    base = os.path.basename(str(name).replace("\\", "/"))
    for suffix in (".jsonl", ".il", ".cfg"):
        if base.endswith(suffix):
            base = base[: -len(suffix)]
    return base


def default_terms_path(packs_dir: str):
    """<packs>/zh-cn → <packs>/terms.jsonl；非 zh-cn 目录不套用中文术语库。"""
    lang = os.path.basename(os.path.normpath(packs_dir))
    candidate = os.path.join(os.path.dirname(os.path.abspath(packs_dir)), "terms.jsonl")
    if lang == "zh-cn" and os.path.isfile(candidate):
        return candidate
    return None


def load_terms(path):
    """读 packs/terms.jsonl，返回 (规则列表, 错误列表)。

    坏行只记错误、不抛异常：术语库坏了不该让整个 audit 自爆；错误会在报告里单列。
    """
    rules = []
    errors = []
    if not path or not os.path.isfile(path):
        return rules, errors
    with open(path, "r", encoding="utf-8") as handle:
        for line_no, line in enumerate(handle, 1):
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except ValueError as exc:
                errors.append("%s 第 %d 行不是合法 JSON：%s" % (path, line_no, exc))
                continue
            if not isinstance(row, dict):
                errors.append("%s 第 %d 行不是 JSON 对象" % (path, line_no))
                continue
            if not isinstance(row.get("term"), str) or not row.get("term").strip():
                errors.append("%s 第 %d 行缺 term 字段" % (path, line_no))
                continue
            rules.append(row)
    return rules, errors


_MOD_METADATA_CACHE: dict[str, set[str]] | None = None


def get_mod_domains(mod: str) -> set[str]:
    """从 packs/mods.json 加载并缓存 mod 的领域与别名元数据（解耦具体 mod 硬编码）。"""
    global _MOD_METADATA_CACHE
    if _MOD_METADATA_CACHE is None:
        _MOD_METADATA_CACHE = {}
        candidate_paths = [
            os.path.join("packs", "mods.json"),
            os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(__file__))), "packs", "mods.json"),
        ]
        for p in candidate_paths:
            if os.path.isfile(p):
                try:
                    with open(p, "r", encoding="utf-8") as f:
                        data = json.load(f)
                    for item in data.get("mods") or []:
                        m_id = str(item.get("modId") or "").strip().lower()
                        if not m_id:
                            continue
                        known = {m_id}
                        disp = str(item.get("displayName") or "").strip().lower()
                        if disp:
                            known.add(disp)
                        for key in ("domains", "tags", "categories", "aliases", "assemblies", "folders"):
                            for val in item.get(key) or []:
                                v = str(val).strip().lower()
                                if v:
                                    known.add(v)
                        _MOD_METADATA_CACHE[m_id] = known
                    break
                except Exception:
                    pass
    return _MOD_METADATA_CACHE.get(str(mod).strip().lower(), set())


def _domain_matches(rule: dict, mod: str) -> bool:
    """术语规则是否覆盖这个 mod。

    domain 里可以写概念领域（Parachute / Rendering）或具体 modId（RealChute / MechJeb2）。
    判定规则：
    1. domain 缺省或包含 general -> 对所有包生效；
    2. domain 与 modId 互相包含（原样兼容）；
    3. mod 在 packs/mods.json 中登记的 domains/tags/categories/aliases 命中 rule domain。
    """
    domains = rule.get("domain") or []
    if isinstance(domains, str):
        domains = [domains]
    names = [str(item).strip().lower() for item in domains if str(item).strip()]
    if not names or "general" in names:
        return True
    current = str(mod).lower()
    if any(name == current or name in current or current in name for name in names):
        return True
    meta = get_mod_domains(mod)
    if meta and any(name in meta for name in names):
        return True
    return False


def _term_pattern(term: str):
    """整词匹配（Arm 不命中 Arming / Armed），术语里允许连字符等非单词字符。"""
    pattern = _TERM_PATTERN_CACHE.get(term)
    if pattern is None:
        pattern = re.compile(
            r"(?<![A-Za-z0-9_])" + re.escape(term) + r"(?![A-Za-z0-9_])",
            re.IGNORECASE)
        _TERM_PATTERN_CACHE[term] = pattern
    return pattern


def check_row_terms(row: dict, terms, mod: str, file_name: str, line_no: int) -> list:
    """检查一行译文是否违反消歧术语规则，返回违规列表（可能为空）。

    判定顺序（一条行最多报一类，避免同一处既报禁词又报不匹配）：
      1. 原文整词命中某个 term，且该规则的 domain 覆盖这个 mod；
      2. 译文含任一命中规则的 forbidden → term_forbidden（禁译词优先）；
      3. 否则译文不含任一命中规则的标准 zh → term_mismatch。
    """
    source = row.get("s")
    target = row.get("t")
    if not isinstance(source, str) or not isinstance(target, str) or not source or not target:
        return []

    applicable = []
    for rule in terms:
        term = rule.get("term")
        if not isinstance(term, str) or not term:
            continue
        if not _domain_matches(rule, mod):
            continue
        if not _term_pattern(term).search(source):
            continue
        applicable.append(rule)
    if not applicable:
        return []

    hits = []
    forbidden_seen = False
    for rule in applicable:
        forbidden = rule.get("forbidden") or []
        if isinstance(forbidden, str):
            forbidden = [forbidden]
        for word in forbidden:
            word = str(word)
            if not word or word not in target:
                continue
            forbidden_seen = True
            hits.append({
                "file": file_name,
                "line": line_no,
                "mod": mod,
                "h": row.get("h") or "",
                "term": rule.get("term"),
                "source": source,
                "target": target,
                "violation": "term_forbidden",
                "word": word,
                "expected": rule.get("zh"),
            })
    if forbidden_seen:
        return hits

    expected = [rule["zh"] for rule in applicable
                if isinstance(rule.get("zh"), str) and rule.get("zh")]
    if not expected:
        return hits
    if any(item in target for item in expected):
        return hits
    hits.append({
        "file": file_name,
        "line": line_no,
        "mod": mod,
        "h": row.get("h") or "",
        "term": applicable[0].get("term"),
        "source": source,
        "target": target,
        "violation": "term_mismatch",
        "word": "",
        "expected": " / ".join(expected),
    })
    return hits


def collect_term_violations(packs_dir: str, terms) -> list:
    """扫描一个语言目录，返回全部术语违规（给 api term audit 复用同一套判定）。"""
    if not terms or not os.path.isdir(packs_dir):
        return []
    violations = []
    names = sorted(name for name in os.listdir(packs_dir) if name.endswith(".jsonl"))
    for name in names:
        path = os.path.join(packs_dir, name)
        mod = _pack_mod(name)
        with open(path, "r", encoding="utf-8") as handle:
            for line_no, line in enumerate(handle, 1):
                line = line.strip()
                if not line:
                    continue
                try:
                    row = json.loads(line)
                except ValueError:
                    continue
                if not isinstance(row, dict):
                    continue
                violations.extend(check_row_terms(row, terms, mod, name, line_no))
    return violations


def audit_dir(packs_dir: str, terms_path=None, strict_terms: bool = False) -> dict:
    """审计一个语言目录，返回结果字典（不打印、不退出）。

    术语违规默认只进 term_violations / term_counts，不参与 verdict 与退出码；
    只有 strict_terms=True 时才把 term_verdict 判为 FAIL。
    """
    files = sorted(f for f in os.listdir(packs_dir) if f.endswith(".jsonl"))
    counts = dict.fromkeys(CHECK_NAMES, 0)
    term_counts = dict.fromkeys(TERM_CHECK_NAMES, 0)
    term_violations = []
    if terms_path is None:
        terms_path = default_terms_path(packs_dir)
    terms, term_errors = load_terms(terms_path)
    total_rows = 0
    keep_rows = 0
    total_bytes = 0
    by_file = []
    hmap = {}
    problems = []

    def note(name, where, detail):
        counts[name] += 1
        if len(problems) < 80:
            problems.append((where[0], where[1], name, detail))

    for name in files:
        path = os.path.join(packs_dir, name)
        rows = 0
        with open(path, "r", encoding="utf-8") as handle:
            for line_no, line in enumerate(handle, 1):
                line = line.rstrip("\n")
                if not line.strip():
                    continue
                rows += 1
                total_rows += 1
                where = (name, line_no)
                try:
                    row = json.loads(line)
                except Exception as exc:  # noqa: BLE001 - 报告原文即可
                    note("bad_row", where, "json: %s" % str(exc)[:70])
                    continue
                if not isinstance(row, dict):
                    note("bad_row", where, "not an object")
                    continue
                s, t, h, n = row.get("s"), row.get("t"), row.get("h"), row.get("n")
                if not (isinstance(s, str) and isinstance(t, str) and isinstance(h, str)):
                    note("bad_row", where, "type: %r" % ((s, t, h),)[:1])
                    continue

                want = hash_text(normalize(s))
                if h != want:
                    note("bad_h", where, "%s != %s  s=%s" % (h, want, s[:40]))
                if isinstance(n, str) and n != normalize(s):
                    note("bad_n", where, "n=%r  norm(s)=%r" % (n[:40], normalize(s)[:40]))
                if t == "":
                    note("empty_t", where, "s=%s" % s[:40])
                if t == s:
                    keep_rows += 1
                if _multiset(s, _PLACEHOLDER_RE) != _multiset(t, _PLACEHOLDER_RE):
                    note("bad_placeholder", where, "s=%s  t=%s" % (s[:40], t[:40]))
                if _multiset(s, _TMP_TAG_RE) != _multiset(t, _TMP_TAG_RE):
                    note("bad_richtext", where, "s=%s  t=%s" % (s[:40], t[:40]))
                if _multiset(s, _FORMAT_RE) != _multiset(t, _FORMAT_RE):
                    note("bad_format", where, "s=%s  t=%s" % (s[:40], t[:40]))
                if _CJK_RE.search(s) and not is_key_ref(s):
                    note("cjk_in_source", where, "s=%s" % s[:40])
                for violation in check_row_terms(row, terms, _pack_mod(name), name, line_no):
                    term_counts[violation["violation"]] += 1
                    term_violations.append(violation)
                hmap.setdefault(h, []).append((name, line_no, t, s))
        total_bytes += os.path.getsize(path)
        by_file.append((name, rows, os.path.getsize(path)))

    dup_h = 0
    for h, bucket in hmap.items():
        if len(bucket) > 1:
            dup_h += 1
            variants = {item[2] for item in bucket}
            if len(variants) > 1:
                counts["dup_h_diff_t"] += 1
                if len(problems) < 80:
                    problems.append((
                        bucket[0][0], bucket[0][1], "dup_h_diff_t",
                        "h=%s 有 %d 行、%d 种译文: %s" % (
                            h, len(bucket), len(variants),
                            " | ".join("%s:%d=%s" % (b[0], b[1], b[2][:20]) for b in bucket[:4])),
                    ))

    bad_total = sum(counts[name] for name in CHECK_NAMES)
    term_total = sum(term_counts[name] for name in TERM_CHECK_NAMES)
    if strict_terms and (term_total or term_errors):
        term_verdict = "FAIL"
    elif term_total or term_errors:
        term_verdict = "NOTICE"
    else:
        term_verdict = "PASS"
    return {
        "packs_dir": packs_dir,
        "files": len(files),
        "il_files": len([f for f in by_file if f[0].endswith(".il.jsonl")]),
        "rows": total_rows,
        "bytes": total_bytes,
        "keep_rows": keep_rows,
        "unique_h": len(hmap),
        "dup_h": dup_h,
        "counts": counts,
        "problems": problems,
        "bad_total": bad_total,
        "verdict": "PASS" if bad_total == 0 else "FAIL",
        "terms_path": terms_path,
        "terms_count": len(terms),
        "term_counts": term_counts,
        "term_total": term_total,
        "term_violations": term_violations,
        "term_errors": term_errors,
        "term_verdict": term_verdict,
        "strict_terms": bool(strict_terms),
    }


def format_report(result: dict, quiet: bool = False, limit: int = 60) -> str:
    lines = [
        "=== audit ===",
        "dir=%s" % result["packs_dir"],
        "files=%d (il=%d)  rows=%d  bytes=%d" % (
            result["files"], result["il_files"], result["rows"], result["bytes"]),
        "keep_rows=%d  unique_h=%d  dup_h=%d  dup_h_diff_t=%d" % (
            result["keep_rows"], result["unique_h"], result["dup_h"],
            result["counts"]["dup_h_diff_t"]),
        "bad_h=%d  bad_n=%d  empty_t=%d  bad_placeholder=%d  bad_richtext=%d  bad_format=%d  cjk_in_source=%d  bad_row=%d" % (
            result["counts"]["bad_h"], result["counts"]["bad_n"], result["counts"]["empty_t"],
            result["counts"]["bad_placeholder"], result["counts"]["bad_richtext"],
            result["counts"]["bad_format"], result["counts"]["cjk_in_source"],
            result["counts"]["bad_row"]),
        "term_rules=%d  term_forbidden=%d  term_mismatch=%d  term_errors=%d  (%s)" % (
            result.get("terms_count", 0),
            result.get("term_counts", {}).get("term_forbidden", 0),
            result.get("term_counts", {}).get("term_mismatch", 0),
            len(result.get("term_errors") or []),
            "术语违规计入 error" if result.get("strict_terms") else "术语违规仅 notice，不阻断"),
    ]
    if result["problems"] and not quiet:
        lines.append("--- problems (%d shown) ---" % min(len(result["problems"]), limit))
        for file_name, line_no, kind, detail in result["problems"][:limit]:
            lines.append("  %s:%d [%s] %s" % (file_name, line_no, kind, detail))
    term_violations = result.get("term_violations") or []
    if term_violations and not quiet:
        lines.append("--- term notices (%d shown) ---" % min(len(term_violations), limit))
        for item in term_violations[:limit]:
            if item["violation"] == "term_forbidden":
                detail = "禁译词=%s" % item["word"]
            else:
                detail = "期望含=%s" % item["expected"]
            lines.append("  %s:%d [%s] %s  %s  s=%s  t=%s" % (
                item["file"], item["line"], item["violation"], item["term"], detail,
                item["source"][:40], item["target"][:40]))
    for message in result.get("term_errors") or []:
        lines.append("  [term_rule] " + message)
    verdict_note = ""
    if result.get("term_verdict") == "FAIL":
        verdict_note = "  term strict=FAIL（--strict-terms）"
    elif result.get("term_total") or result.get("term_errors"):
        verdict_note = "  term notices=%d（需人工裁决后才能升为 error）" % result.get("term_total", 0)
    lines.append("VERDICT: %s (bad_total=%d)%s" % (
        result["verdict"], result["bad_total"], verdict_note))
    return "\n".join(lines)


def _write_pack(path: str, rows) -> None:
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        for source, target in rows:
            handle.write(json.dumps({
                "h": hash_text(normalize(source)),
                "n": normalize(source),
                "s": source,
                "t": target,
                "mod": "Probe",
            }, ensure_ascii=False) + "\n")


def selftest() -> int:
    """自检：1 条正例 + 8 条负例，每条都要被对应检查抓到。"""
    checks = 0
    failures = []

    def eq(label, got, want):
        nonlocal checks
        checks += 1
        if got != want:
            failures.append("%s: got=%r want=%r" % (label, got, want))

    with tempfile.TemporaryDirectory() as tmp:
        # 正例：干净包（含一条 keep 行、一条键引用行）
        clean = os.path.join(tmp, "clean")
        os.makedirs(clean)
        _write_pack(os.path.join(clean, "A.jsonl"), [
            ("Settings", "设置"),
            ("Delta-V", "Delta-V"),
            ("#autoLOC_6001234", "官方译文"),
            ("Target: <<1>>", "目标：<<1>>"),
            ("<b>Bold</b> text", "<b>粗体</b>文本"),
            ("Speed {0} m/s", "速度 {0} m/s"),
        ])
        good = audit_dir(clean)
        eq("干净包 verdict", good["verdict"], "PASS")
        eq("干净包 keep_rows", good["keep_rows"], 1)
        eq("干净包 rows", good["rows"], 6)

        # 负例 1：h 与 s 不对应
        d = os.path.join(tmp, "bad_h")
        os.makedirs(d)
        with open(os.path.join(d, "B.jsonl"), "w", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps({"h": "0000000000000000", "n": "Settings", "s": "Settings",
                                     "t": "设置", "mod": "Probe"}, ensure_ascii=False) + "\n")
        eq("bad_h 被抓", audit_dir(d)["counts"]["bad_h"], 1)

        # 负例 2：空译文
        d = os.path.join(tmp, "empty_t")
        os.makedirs(d)
        _write_pack(os.path.join(d, "C.jsonl"), [("Close", "")])
        eq("empty_t 被抓", audit_dir(d)["counts"]["empty_t"], 1)

        # 负例 3：占位符丢失
        d = os.path.join(tmp, "ph")
        os.makedirs(d)
        _write_pack(os.path.join(d, "D.jsonl"), [("Target: <<1>>", "目标：")])
        eq("bad_placeholder 被抓", audit_dir(d)["counts"]["bad_placeholder"], 1)

        # 负例 4：富文本标签数量变了
        d = os.path.join(tmp, "rt")
        os.makedirs(d)
        _write_pack(os.path.join(d, "E.jsonl"), [("<b>A</b>", "粗体")])
        eq("bad_richtext 被抓", audit_dir(d)["counts"]["bad_richtext"], 1)

        # 负例 5：格式符丢失
        d = os.path.join(tmp, "fmt")
        os.makedirs(d)
        _write_pack(os.path.join(d, "F.jsonl"), [("Speed {0} m/s", "速度 m/s")])
        eq("bad_format 被抓", audit_dir(d)["counts"]["bad_format"], 1)

        # 负例 6：源文含 CJK（漏过滤）
        d = os.path.join(tmp, "cjk")
        os.makedirs(d)
        _write_pack(os.path.join(d, "G.jsonl"), [("设置面板", "设置面板")])
        eq("cjk_in_source 被抓", audit_dir(d)["counts"]["cjk_in_source"], 1)

        # 负例 7：跨文件同 h 不同 t（运行时输出不确定）
        d = os.path.join(tmp, "dup")
        os.makedirs(d)
        _write_pack(os.path.join(d, "H.jsonl"), [("Settings", "设置")])
        _write_pack(os.path.join(d, "I.jsonl"), [("Settings", "设定")])
        result = audit_dir(d)
        eq("dup_h 计数", result["dup_h"], 1)
        eq("dup_h_diff_t 被抓", result["counts"]["dup_h_diff_t"], 1)

        # 负例 8：坏 JSON 行
        d = os.path.join(tmp, "junk")
        os.makedirs(d)
        with open(os.path.join(d, "J.jsonl"), "w", encoding="utf-8", newline="\n") as handle:
            handle.write("{not json}\n")
        eq("bad_row 被抓", audit_dir(d)["counts"]["bad_row"], 1)

    print("[audit] selftest: %d 项检查，失败 %d 项" % (checks, len(failures)))
    for item in failures:
        print("  [fail] " + item)
    return 0 if not failures else 1


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        prog="python -m i18n_tools.audit",
        description="字典包内容审计（h 契约 / 占位符 / 富文本 / 跨文件一致性）")
    parser.add_argument("--packs-dir", default=None,
                        help="语言包目录（默认 <repo>/packs/<lang>）")
    parser.add_argument("--lang", default="zh-cn", help="语言目录名（默认 zh-cn）")
    parser.add_argument("--quiet", action="store_true", help="只打汇总，不打前 60 条明细")
    parser.add_argument("--json", dest="as_json", action="store_true", help="输出 JSON 结果")
    parser.add_argument("--terms", default=None,
                        help="术语库路径（默认 <packs>/terms.jsonl；不存在则跳过术语检查）")
    parser.add_argument("--strict-terms", dest="strict_terms", action="store_true",
                        help="把术语违规升为 error（默认只报 notice，不 exit 1）")
    parser.add_argument("--selftest", action="store_true", help="跑自检（1 正例 + 8 负例）")
    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

    packs_dir = args.packs_dir
    if packs_dir is None:
        repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
        packs_dir = os.path.join(repo, "packs", args.lang)
    if not os.path.isdir(packs_dir):
        print("[audit] 目录不存在: %s" % packs_dir, file=sys.stderr)
        return 1

    result = audit_dir(packs_dir, terms_path=args.terms, strict_terms=args.strict_terms)
    if args.as_json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    else:
        print(format_report(result, quiet=args.quiet))
    if result["verdict"] != "PASS":
        return 1
    if result["term_verdict"] == "FAIL":
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
