#!/usr/bin/env python3
"""check_overrides.py —— 译文门禁：把 overrides 行对候选清单逐条体检。

为什么需要它：translate_il.py 自带的 override_gate（:577）只查占位符与富文本标签的多重集，
不查长度比、不查「其实没翻」（英文原样抄回来）、不查首尾空白漂移。cfgcache 通道是整段零件描述，
这几类错误最容易发生，所以单立一个可复用的门禁工具，供人手翻 / 批量翻 / CI 三处共用。

CLI:
  python -m i18n_tools.check_overrides --candidates packs/cfgcache-candidates.jsonl --overrides a.jsonl b.jsonl
  python -m i18n_tools.check_overrides ... --require-all --json out.json
  python -m i18n_tools.check_overrides --selftest
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys

from . import ensure_stdout, textnorm

# KSP 占位符 <<1>>、C# string.Format 的 {0}、printf 的 %s / %d、TMP 富文本标签
_KSP_PLACEHOLDER_RE = re.compile(r"<<\d+>>")
_BRACE_PLACEHOLDER_RE = re.compile(r"\{\d+(?::[^}]*)?\}")
_PRINTF_PLACEHOLDER_RE = re.compile(r"%[0-9.\-+]*[sdfuxX]")
_TMP_TAG_RE = re.compile(r"</?(?:b|i|u|s|color|size|material|quad|sprite|link)(?:=[^>]*)?>", re.I)
_CJK_RE = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff]")
_WORD_RE = re.compile(r"[A-Za-z]{2,}")

MIN_RATIO = 0.20
MAX_RATIO = 1.80
RATIO_MIN_LEN = 40          # 短文本不查长度比（"OK" → "确定" 本来就短）


def _multiset(pattern, text):
    out = {}
    for match in pattern.findall(text):
        key = match if isinstance(match, str) else match[0]
        out[key] = out.get(key, 0) + 1
    return out


def check_row(en, zh, keep=False):
    """返回错误列表（空 = 通过）。"""
    errors = []
    if not isinstance(zh, str) or not zh.strip():
        return ["empty-zh"]
    if keep:
        if zh != en:
            errors.append("keep-but-changed")
        return errors
    for name, pattern in (("ksp-placeholder", _KSP_PLACEHOLDER_RE),
                          ("brace-placeholder", _BRACE_PLACEHOLDER_RE),
                          ("printf-placeholder", _PRINTF_PLACEHOLDER_RE),
                          ("tmp-tag", _TMP_TAG_RE)):
        if _multiset(pattern, en) != _multiset(pattern, zh):
            errors.append(name + "-mismatch")
    if en[:1].isspace() and not zh[:1].isspace():
        errors.append("lost-leading-space")
    if en[-1:].isspace() and not zh[-1:].isspace():
        errors.append("lost-trailing-space")
    if en.count(chr(10)) != zh.count(chr(10)):
        errors.append("newline-count-mismatch")
    if len(en) >= RATIO_MIN_LEN:
        ratio = len(zh) / float(len(en))
        if ratio < MIN_RATIO or ratio > MAX_RATIO:
            errors.append("length-ratio:{:.2f}".format(ratio))
    words = len(_WORD_RE.findall(en))
    if words >= 2 and not _CJK_RE.search(zh):
        errors.append("no-cjk")
    if zh.strip() == en.strip() and words >= 1:
        errors.append("identical")
    return errors


def load_candidates(paths):
    """返回 {normalize(en): {"en", "h", "field"}}。"""
    out = {}
    for path in paths:
        if not path:
            continue
        for line in open(path, encoding="utf-8"):
            line = line.strip()
            if not line:
                continue
            row = json.loads(line)
            # 三种候选行格式都认：packs/*-candidates.jsonl 用 "s"，采集器输出用 "text"，
            # 分片（_scratch/cc/i-*.jsonl）用 "en"。少认一种就会静默得出「候选 0 / 失败 0」的假绿。
            text = None
            for key_name in ("s", "text", "en"):
                if isinstance(row.get(key_name), str):
                    text = row[key_name]
                    break
            if not isinstance(text, str):
                continue
            key = textnorm.normalize(text)
            if key and key not in out:
                fields = row.get("fields")
                field = fields[0] if isinstance(fields, list) and fields else row.get("field", "")
                out[key] = {"en": text, "h": row.get("h", ""), "field": field or ""}
    return out


def load_overrides(paths):
    """返回 {normalize(en): {"en","zh","note","keep"}}；同一键后者覆盖前者。"""
    out = {}
    for path in paths:
        if not path:
            continue
        for line in open(path, encoding="utf-8"):
            line = line.strip()
            if not line:
                continue
            row = json.loads(line)
            en = row.get("en")
            zh = row.get("zh")
            if not isinstance(en, str) or not isinstance(zh, str):
                continue
            key = textnorm.normalize(en)
            out[key] = {"en": en, "zh": zh, "note": row.get("note", ""), "keep": bool(row.get("keep"))}
    return out


def run(candidate_paths, override_paths, require_all=False):
    cands = load_candidates(candidate_paths)
    overrides = load_overrides(override_paths)
    failures = []
    covered = 0
    for key, cand in cands.items():
        row = overrides.get(key)
        if row is None:
            continue
        covered += 1
        errors = check_row(row["en"], row["zh"], row["keep"])
        if errors:
            failures.append({"en": row["en"], "zh": row["zh"], "errors": errors,
                             "h": cand["h"], "field": cand["field"]})
    missing = [cand for key, cand in cands.items() if key not in overrides]
    report = {
        "candidates": len(cands),
        "overrides": len(overrides),
        "covered": covered,
        "missing": len(missing),
        "failures": failures,
        "missing_examples": [item["en"][:80] for item in missing[:10]],
    }
    report["ok"] = not failures and (covered == len(cands) or not require_all)
    return report


def selftest():
    checks = []

    def eq(name, got, want):
        checks.append((name, got == want, "got={!r} want={!r}".format(got, want)))

    eq("正常译文通过", check_row("Load the vessel.", "装载飞船。"), [])
    eq("占位符缺失被抓", check_row("Mass: <<1>> t", "质量：吨"), ["ksp-placeholder-mismatch"])
    eq("占位符保住则通过", check_row("Mass: <<1>> t", "质量：<<1>> 吨"), [])
    eq("富文本标签漂移被抓", check_row("<b>Bold</b> text", "粗体文本"), ["tmp-tag-mismatch"])
    eq("英文原样抄回来被抓", check_row("Press the button", "Press the button"), ["no-cjk", "identical"])
    ratio_errors = [row for row in check_row("A " * 40, "短") if row.startswith("length-ratio")]
    eq("长度比越界被抓", len(ratio_errors) >= 1, True)
    eq("keep 行要求原样", check_row("RD-0410", "RD-0410", keep=True), [])
    eq("keep 行被改动被抓", check_row("RD-0410", "RD-0410 发动机", keep=True), ["keep-but-changed"])
    eq("尾随空格必须保留", check_row("Mass: ", "质量："), ["lost-trailing-space"])

    failed = [row for row in checks if not row[1]]
    for name, ok, detail in checks:
        print(("  ok   " if ok else "  FAIL ") + name + ("" if ok else "  " + detail))
    print("[selftest] {} 项，失败 {}".format(len(checks), len(failed)))
    return 1 if failed else 0


def main(argv=None):
    ensure_stdout()
    parser = argparse.ArgumentParser(description="译文门禁：overrides x candidates")
    parser.add_argument("--candidates", nargs="+", default=[])
    parser.add_argument("--overrides", nargs="+", default=[])
    parser.add_argument("--require-all", action="store_true", help="候选里没译到的也算失败")
    parser.add_argument("--json", default=None, help="把完整结果写到这个路径")
    parser.add_argument("--max-print", type=int, default=20)
    parser.add_argument("--quiet", action="store_true")
    parser.add_argument("--selftest", action="store_true")
    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

    report = run(args.candidates, args.overrides, args.require_all)
    if args.json:
        os.makedirs(os.path.dirname(os.path.abspath(args.json)) or ".", exist_ok=True)
        with open(args.json, "w", encoding="utf-8", newline=chr(10)) as handle:
            handle.write(json.dumps(report, ensure_ascii=False, indent=2) + chr(10))
    if not args.quiet:
        print("[check] 候选 {} / 覆盖 {} / 缺 {} / 失败 {}".format(
            report["candidates"], report["covered"], report["missing"], len(report["failures"])))
        for item in report["failures"][:args.max_print]:
            print("  FAIL {} <- {}".format(",".join(item["errors"]), item["en"][:70]))
        if report["missing"] and not args.require_all:
            print("  (还缺 {} 条，示例：{})".format(report["missing"], report["missing_examples"][:3]))
    return 0 if report["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
