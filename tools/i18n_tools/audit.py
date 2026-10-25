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

用法:
    python -m i18n_tools.audit                 # 审计 packs/zh-cn
    python -m i18n_tools.audit --lang en-us
    python -m i18n_tools.audit --packs-dir <dir> --quiet
    python -m i18n_tools.audit --selftest      # 自检（含 8 条负例）

退出码: 0 = PASS，1 = 有违规（CI 用它当门禁）。
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


def _multiset(text: str, pattern) -> collections.Counter:
    return collections.Counter(pattern.findall(text))


def audit_dir(packs_dir: str) -> dict:
    """审计一个语言目录，返回结果字典（不打印、不退出）。"""
    files = sorted(f for f in os.listdir(packs_dir) if f.endswith(".jsonl"))
    counts = dict.fromkeys(CHECK_NAMES, 0)
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
    ]
    if result["problems"] and not quiet:
        lines.append("--- problems (%d shown) ---" % min(len(result["problems"]), limit))
        for file_name, line_no, kind, detail in result["problems"][:limit]:
            lines.append("  %s:%d [%s] %s" % (file_name, line_no, kind, detail))
    lines.append("VERDICT: %s (bad_total=%d)" % (result["verdict"], result["bad_total"]))
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

    result = audit_dir(packs_dir)
    if args.as_json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    else:
        print(format_report(result, quiet=args.quiet))
    return 0 if result["bad_total"] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
