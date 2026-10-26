#!/usr/bin/env python3
"""golden_gate.py —— golden set 门禁：用户报过的英文一条都不许静默丢失（docs/08 §1 D-T5）。

为什么必须有这道门
  1.4 的干活方式是「用户报一段 → 补一轮翻译」的补丁式循环：报一个补一个，而系统里没有任何东西
  保证「下一个人改过滤器时不会把已经补好的那条又丢掉」。docs/08 §0 取证的第 2 条结构性来源正是
  「无度量的布尔过滤」——capture_ingest 一串 return ("never", reason) 级联，判错的串连正文都不留，
  系统里没有任何痕迹。这道门把用户报过的每一条原文钉成回归用例：只要它被过滤器丢掉、没进候选表、
  或者字典里没有译文，门禁立刻红，并且**直接说是第几步、哪一条挂的**。

三步判定（顺序固定，方便一眼看出是谁挂的）
  ① capture_ingest.classify_capture(原文) → (bucket, reason)
     bucket 必须是可留档三档 candidate / template / never（docs/08 D-T3：bucket 是**优先级**不是去留，
     never 也照样留档），且不得是 dropped 之类的「丢弃」词；classify 抛异常、返回值认不出，同样算挂。
  ② packs/candidates.jsonl（五条通道唯一的候选表）里有这一个 h。
  ③ packs/zh-cn/*.jsonl 里同一个 h 有非空 t（keep 直通 t == 原文，也算有译文）。

h 一律现算
  tests/golden/rendered-strings.txt 里**只放原文、不放 h**：这样有人改了 textnorm.normalize 口径时
  门禁会响（现算的 h 对不上表里的 h = 数据契约变了），而不是照着旧 h 继续通过。

纪律（docs/08 D-T5 原文）
  任何过滤器/阈值改动使 golden set 变红 → **改过滤器，不改清单**。清单只随「用户又报了一条」增长。

CLI
  python -m i18n_tools.golden_gate                 # 正式跑
  python -m i18n_tools.golden_gate --list          # 只列清单与现算的 h，不判定
  python -m i18n_tools.golden_gate --strict        # 输入缺失也算挂（CI 想更硬时用）
  python -m i18n_tools.golden_gate --json reports/golden.json
  python -m i18n_tools.golden_gate --selftest      # 自检：假数据走通 / 走红两条路径

退出码
  0 = 三步全过
  1 = 有挂（金标准被丢 / 没进候选表 / 没有译文），或输入文件本身缺失/损坏
  2 = 跳过：packs/candidates.jsonl 还不存在（T-B 的候选单一表未落地）。--strict 时 2 变 1。
      2 是**明确的跳过**，不是成功：run-tests.ps1 与 CI 都会把 skip 原因单独打印出来。
"""

from __future__ import annotations

import argparse
import glob
import io
import json
import os
import sys

from . import ensure_stdout, fnv, textnorm

GOLDEN_REL = os.path.join("tests", "golden", "rendered-strings.txt")
CANDIDATES_REL = os.path.join("packs", "candidates.jsonl")
ZHCN_REL = os.path.join("packs", "zh-cn")

# docs/08 D-T3：bucket 是优先级，不是「要不要留」。三档都留档，所以三步里的 ① 全部算过。
RETAINED_BUCKETS = ("candidate", "template", "never")
# 出现这些词 = 有东西被丢掉了正文，正是 D-T2 明令禁止的那类行为（1.4 的旧逻辑 / 有人又加了分支）。
DROP_WORDS = ("drop", "discard", "overflow", "lost", "truncat")

EXIT_OK = 0
EXIT_FAIL = 1
EXIT_SKIP = 2

STEP_NAMES = {1: "① ingest", 2: "② candidates", 3: "③ zh-cn"}


class GoldenError(Exception):
    """golden 清单本身有问题（文件没了 / 出了一句归一化后为空的「用例」）。直接判挂，不静默跳过。"""


# ---------------------------------------------------------------------------
# 读输入
# ---------------------------------------------------------------------------
def repo_root() -> str:
    """仓库根：本文件在 <repo>/tools/i18n_tools/ 下，向上三级。"""
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def load_golden(path):
    """读清单 → (cases, stats)。cases 保持文件顺序，去重保留首次出现。

    容错到什么程度是刻意的：BOM、CRLF、末尾没换行都要能吃（有人用记事本改过就长这样）；
    注释行与空行忽略；但**归一化后为空的「用例」不当空行跳过**，直接抛 GoldenError ——
    一条全是不可见字符的「用例」在 ① 会判 never/empty、②③ 必定找不到，红得毫无信息量，
    报「第 N 行归一化后是空串」才查得动。
    """
    if not os.path.isfile(path):
        raise GoldenError("清单文件不存在：{}".format(path))
    cases = []
    stats = {"lines": 0, "comments": 0, "blanks": 0, "dups": 0}
    seen = set()
    with open(path, encoding="utf-8-sig", newline="") as handle:
        for lineno, raw in enumerate(handle, 1):
            line = raw.rstrip("\n").rstrip("\r")
            stats["lines"] += 1
            if not line.strip():
                stats["blanks"] += 1
                continue
            if line.lstrip().startswith("#"):
                stats["comments"] += 1
                continue
            norm = textnorm.normalize(line)
            if not norm:
                raise GoldenError(
                    "清单第 {} 行归一化后是空串（只有不可见字符？）：{!r}".format(lineno, line))
            if norm in seen:
                stats["dups"] += 1
                continue
            seen.add(norm)
            cases.append(line)
    if not cases:
        raise GoldenError("清单里一条用例都没有：{}".format(path))
    return cases, stats


def load_candidates(path):
    """packs/candidates.jsonl → (by_h, by_norm, stats)。

    by_norm（归一化原文 → h）只为诊断：能把「压根没进候选表」与「进表了但 h 口径漂移」分开报，
    这两种情况的修法完全不同（前者补捕获，后者是 normalize 契约被改了）。

    坏行不当没看见：stats 里记行号，run_gate 见到坏行直接判挂 —— 候选表被写坏了本身就是事故。
    """
    by_h, by_norm = {}, {}
    stats = {"exists": bool(path and os.path.isfile(path)), "rows": 0,
             "bad_lines": 0, "bad_line_nos": [], "empty_h": 0}
    if not stats["exists"]:
        return by_h, by_norm, stats
    with open(path, encoding="utf-8") as handle:
        for lineno, raw in enumerate(handle, 1):
            line = raw.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except Exception:
                row = None
            if not isinstance(row, dict):
                stats["bad_lines"] += 1
                if len(stats["bad_line_nos"]) < 5:
                    stats["bad_line_nos"].append(lineno)
                continue
            h = row.get("h")
            if not isinstance(h, str) or not h:
                stats["empty_h"] += 1
                continue
            stats["rows"] += 1
            by_h.setdefault(h, row)
            src = row.get("s")
            if isinstance(src, str):
                by_norm.setdefault(textnorm.normalize(src), h)
    return by_h, by_norm, stats


def load_zh_pack(directory):
    """packs/zh-cn/*.jsonl → (by_h, stats)；by_h: h → (t, 文件名)。t 为空白视作没有译文。"""
    by_h = {}
    stats = {"files": 0, "rows": 0, "empty_t": 0, "bad_lines": 0}
    for path in sorted(glob.glob(os.path.join(directory, "*.jsonl"))):
        stats["files"] += 1
        with open(path, encoding="utf-8") as handle:
            for raw in handle:
                line = raw.strip()
                if not line:
                    continue
                try:
                    row = json.loads(line)
                except Exception:
                    stats["bad_lines"] += 1
                    continue
                if not isinstance(row, dict):
                    stats["bad_lines"] += 1
                    continue
                h = row.get("h")
                if not isinstance(h, str) or not h:
                    continue
                stats["rows"] += 1
                text = row.get("t")
                if not isinstance(text, str) or not text.strip():
                    stats["empty_t"] += 1
                    continue
                by_h.setdefault(h, (text, os.path.basename(path)))
    return by_h, stats


# ---------------------------------------------------------------------------
# ①：classify_capture 的返回值口径
# ---------------------------------------------------------------------------
def default_classifier():
    """真 · capture_ingest.classify_capture。

    延迟导入（不在模块顶部 import）是为了让 --selftest 在 capture_ingest 被人改到一半时照样能跑：
    自检用桩函数，只有一条集成检查才碰真模块。
    """
    from . import capture_ingest

    return capture_ingest.classify_capture


def call_classifier(classify_fn, text):
    """把 classify_capture 的返回值统一成 (bucket, reason, error)。

    T-B 正在把「bool 过滤」改成「只打标签」，返回形状历史上出现过三种：元组 (bucket, reason)、
    纯 bucket 字符串、dict{"bucket","reason"}。这里全都认；认不出就报错，**绝不猜**。
    """
    try:
        out = classify_fn(text)
    except Exception as exc:                     # classify 自己炸了也算 ① 挂
        return None, None, "classify_capture 抛异常：{!r}".format(exc)
    bucket = reason = None
    if isinstance(out, str):
        bucket = out
    elif isinstance(out, dict):
        bucket = out.get("bucket")
        reason = out.get("reason")
    elif isinstance(out, (tuple, list)) and out:
        bucket = out[0]
        reason = out[1] if len(out) > 1 else None
    if not isinstance(bucket, str) or not bucket.strip():
        return None, None, "认不出 classify_capture 的返回值：{!r}".format(out)
    bucket = bucket.strip().lower()
    reason = reason.strip().lower() if isinstance(reason, str) else ""
    return bucket, reason, None


def judge_step1(bucket, reason, error):
    """① 的判据 → (ok, detail)。"""
    if error:
        return False, error
    lowered = (bucket + "/" + reason).lower()
    for word in DROP_WORDS:
        if word in lowered:
            return False, "bucket={} reason={} 含丢弃语义（{}）→ 正文会被丢掉".format(
                bucket, reason or "-", word)
    if bucket not in RETAINED_BUCKETS:
        return False, "bucket={} 不在可留档三档 {}/{}，reason={}".format(
            bucket, RETAINED_BUCKETS[0], RETAINED_BUCKETS[1], reason or "-")
    return True, ""


# ---------------------------------------------------------------------------
# 主判定
# ---------------------------------------------------------------------------
def run_gate(golden_path, repo, classify_fn=None, strict=False,
             candidates_path=None, zh_dir=None):
    """跑三步门禁，返回结果 dict（不打印、不 exit，方便 selftest 与 CI 各自消费）。"""
    candidates_path = candidates_path or os.path.join(repo, CANDIDATES_REL)
    zh_dir = zh_dir or os.path.join(repo, ZHCN_REL)

    result = {"golden": golden_path, "repo": repo, "strict": bool(strict),
              "rows": [], "failures": [], "errors": [], "skipped": False, "skip_reason": "",
              "counts": {}, "exit": EXIT_OK}

    try:
        cases, gstats = load_golden(golden_path)
    except GoldenError as exc:
        result["errors"].append(str(exc))
        result["exit"] = EXIT_FAIL
        return result
    result["golden_stats"] = gstats

    cand_by_h, cand_by_norm, cstats = load_candidates(candidates_path)
    zh_by_h, zstats = load_zh_pack(zh_dir)
    result["candidate_stats"] = cstats
    result["zh_stats"] = zstats
    if cstats["bad_lines"]:
        result["errors"].append(
            "{} 有 {} 行不是合法 JSON（行号示例 {}）—— 候选表被写坏了，先修数据再谈 golden".format(
                CANDIDATES_REL, cstats["bad_lines"], cstats["bad_line_nos"]))
    if zstats["bad_lines"]:
        result["errors"].append(
            "{}/*.jsonl 有 {} 行不是合法 JSON".format(ZHCN_REL, zstats["bad_lines"]))

    classify_fn = classify_fn or default_classifier()
    step2_known = cstats["exists"]

    for idx, raw in enumerate(cases, 1):
        norm = textnorm.normalize(raw)
        h = fnv.hash_text(norm)
        bucket, reason, error = call_classifier(classify_fn, raw)
        s1, s1_detail = judge_step1(bucket, reason, error)

        s2 = None
        s2_detail = ""
        cand_row = None
        if not step2_known:
            s2_detail = "n/a（{} 不存在）".format(CANDIDATES_REL)
        else:
            cand_row = cand_by_h.get(h)
            if cand_row is None:
                other = cand_by_norm.get(norm)
                if other:
                    s2, s2_detail = False, "候选表里有这条原文但 h 对不上（现算 {} / 表里 {}）→ normalize 口径漂移".format(h, other)
                else:
                    s2, s2_detail = False, "候选表里没有 h={}".format(h)
            else:
                src = cand_row.get("s")
                if isinstance(src, str) and textnorm.normalize(src) != norm:
                    s2, s2_detail = False, "h 命中但行内 s 与归一化原文不一致 → 数据契约漂移"
                else:
                    s2, s2_detail = True, "ok（{}）".format(cand_row.get("channel") or cand_row.get("sink") or "?")

        zh = zh_by_h.get(h)
        if zh is None:
            s3, s3_detail, keep = False, "字典里没有 h={} 或 t 为空".format(h), False
        else:
            keep = zh[0].strip() == norm
            s3, s3_detail = True, "{}（{}）".format("ok keep" if keep else "ok", zh[1])

        fails = []
        if not s1:
            fails.append((1, s1_detail))
        if s2 is False:
            fails.append((2, s2_detail))
        if not s3:
            fails.append((3, s3_detail))
        row = {"idx": idx, "text": raw, "norm": norm, "h": h,
               "channel": (cand_row or {}).get("channel") or (cand_row or {}).get("sink") or "",
               "bucket": bucket, "reason": reason, "s1": s1, "s1_detail": s1_detail,
               "s2": s2, "s2_detail": s2_detail, "s3": s3, "s3_detail": s3_detail,
               "keep": keep, "fails": fails}
        result["rows"].append(row)
        for step, detail in fails:
            result["failures"].append({"idx": idx, "step": step, "step_name": STEP_NAMES[step],
                                       "text": raw, "h": h, "detail": detail})

    counts = {"cases": len(result["rows"]), "s1_ok": 0, "s1_fail": 0,
              "s2_ok": 0, "s2_fail": 0, "s2_unknown": 0,
              "s3_ok": 0, "s3_fail": 0, "keep": 0}
    for row in result["rows"]:
        counts["s1_ok" if row["s1"] else "s1_fail"] += 1
        if row["s2"] is None:
            counts["s2_unknown"] += 1
        else:
            counts["s2_ok" if row["s2"] else "s2_fail"] += 1
        counts["s3_ok" if row["s3"] else "s3_fail"] += 1
        if row["keep"]:
            counts["keep"] += 1
    result["counts"] = counts

    if result["failures"] or result["errors"]:
        result["exit"] = EXIT_FAIL
    elif not step2_known:
        result["skipped"] = True
        result["skip_reason"] = "{} 不存在（T-B 的候选单一表未落地）→ ② 无法判定".format(CANDIDATES_REL)
        result["exit"] = EXIT_FAIL if strict else EXIT_SKIP
    else:
        result["exit"] = EXIT_OK
    return result


# ---------------------------------------------------------------------------
# 打印
# ---------------------------------------------------------------------------
def _clip(text, width):
    """按显示宽度截断（宽字符按 2 算），避免中文把表格撑歪。"""
    out, used = [], 0
    for ch in text:
        room = 2 if ord(ch) > 0x2E80 else 1
        if used + room > width:
            return "".join(out) + "…"
        out.append(ch)
        used += room
    return "".join(out)


def _step_cell(row, step):
    """(mark, label)。mark 只有 ok / FAIL / skip 三种；详细原因留给下面的失败清单，
    表格只做「一眼看出卡在第几步」，不要在这儿塞长句（塞了就被 _clip 截成乱码）。"""
    if step == 1:
        if not row["s1"]:
            return "FAIL", ""
        return "ok", "{}{}".format(row["bucket"] or "?", "/" + row["reason"] if row["reason"] else "")
    if step == 2:
        if row["s2"] is None:
            return "skip", ""
        if not row["s2"]:
            return "FAIL", ""
        return "ok", _clip(row.get("channel") or "", 10)
    if row["s3"]:
        return "ok", "keep" if row["keep"] else ""
    return "FAIL", ""


def print_table(result, max_print=None):
    print("")
    print("  {}  {}  {}  {}  {}  {}".format(
        "#".ljust(2), "1 ingest".ljust(17), "2 candidates".ljust(13),
        "3 zh-cn".ljust(9), "h".ljust(16), "原文"))
    shown = 0
    for row in result["rows"]:
        if max_print is not None and shown >= max_print:
            print("  … 还有 {} 条未列出（--max-print）".format(len(result["rows"]) - shown))
            break
        cells = []
        for step in (1, 2, 3):
            mark, label = _step_cell(row, step)
            cells.append(("{} {}".format(mark, label) if label else mark).ljust(17))
        print("  {}  {}  {}  {}  {}  {}".format(
            str(row["idx"]).ljust(2), cells[0], cells[1].ljust(13), cells[2].ljust(9),
            row["h"], _clip(row["text"], 58)))
        shown += 1


def print_summary(result):
    counts = result.get("counts") or {}
    gstats = result.get("golden_stats") or {}
    cstats = result.get("candidate_stats") or {}
    zstats = result.get("zh_stats") or {}
    print("")
    print("[golden] 清单 : {}（{} 条用例；注释 {} 行 / 空行 {} 行 / 重复 {} 行{})".format(
        os.path.relpath(result["golden"], result["repo"]) if result.get("repo") else result["golden"],
        counts.get("cases", 0), gstats.get("comments", 0), gstats.get("blanks", 0),
        gstats.get("dups", 0),
        ("，共 {} 行".format(gstats.get("lines"))) if gstats.get("lines") else ""))
    if cstats.get("exists"):
        print("[golden] 候选 : {}（{} 行，坏行 {}）".format(
            CANDIDATES_REL, cstats.get("rows", 0), cstats.get("bad_lines", 0)))
    else:
        print("[golden] 候选 : {} **不存在** → ② 本次无法判定".format(CANDIDATES_REL))
    print("[golden] 字典 : {}（{} 个包文件 / {} 条非空译文 / 空 t {} 条）".format(
        ZHCN_REL, zstats.get("files", 0), zstats.get("rows", 0) - zstats.get("empty_t", 0),
        zstats.get("empty_t", 0)))
    print("[golden] 汇总 : ① {} 过 {} 挂 | ② {} 过 {} 挂 {} 未判定 | ③ {} 过 {} 挂 | keep {} 条".format(
        counts.get("s1_ok", 0), counts.get("s1_fail", 0),
        counts.get("s2_ok", 0), counts.get("s2_fail", 0), counts.get("s2_unknown", 0),
        counts.get("s3_ok", 0), counts.get("s3_fail", 0), counts.get("keep", 0)))
    for message in result.get("errors", []):
        print("[golden] 错误 : " + message)
    if result.get("failures"):
        print("[golden] 挂的用例（谁挂在第几步）：")
        for item in result["failures"]:
            print("  #{:02d} {} {}  {!r}".format(item["idx"], item["step_name"],
                                                item["detail"], _clip(item["text"], 50)))
    if result["exit"] == EXIT_OK:
        print("[golden] 结果 : PASS（三步全过）")
    elif result["exit"] == EXIT_SKIP:
        print("[golden] 结果 : SKIP —— {}".format(result.get("skip_reason", "")))
        print("[golden]        （这是明确的跳过，不是通过；T-B 落地 packs/candidates.jsonl 后复查）")
    else:
        print("[golden] 结果 : FAIL")


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------
def main(argv=None):
    ensure_stdout()
    parser = argparse.ArgumentParser(
        prog="golden_gate", description="golden set 门禁：用户报过的串三步判定（docs/08 D-T5）")
    parser.add_argument("--golden", default=None, help="清单路径（默认 <repo>/tests/golden/rendered-strings.txt）")
    parser.add_argument("--repo", default=None, help="仓库根（默认从本文件位置推断）")
    parser.add_argument("--candidates", default=None, help="候选表路径（默认 <repo>/packs/candidates.jsonl）")
    parser.add_argument("--zh-dir", default=None, help="字典目录（默认 <repo>/packs/zh-cn）")
    parser.add_argument("--json", default=None, help="把完整结果写到这个路径")
    parser.add_argument("--max-print", type=int, default=None, help="状态表最多打几条")
    parser.add_argument("--list", action="store_true", dest="list_only", help="只列清单与现算的 h，不判定")
    parser.add_argument("--strict", action="store_true", help="输入缺失（exit 2）也当失败")
    parser.add_argument("--quiet", action="store_true")
    parser.add_argument("--selftest", action="store_true")
    args = parser.parse_args(argv)

    if args.selftest:
        failures, total = selftest()
        if failures:
            print("[golden] selftest 失败 {}/{}".format(len(failures), total))
            return EXIT_FAIL
        print("[golden] selftest 全部通过（{} 项）".format(total))
        return EXIT_OK

    repo = os.path.abspath(args.repo) if args.repo else repo_root()
    golden = args.golden or os.path.join(repo, GOLDEN_REL)

    if args.list_only:
        try:
            cases, stats = load_golden(golden)
        except GoldenError as exc:
            print("[golden] " + str(exc))
            return EXIT_FAIL
        print("[golden] {}（{} 条用例 / 注释 {} / 空行 {} / 重复 {}）".format(
            golden, len(cases), stats["comments"], stats["blanks"], stats["dups"]))
        for idx, raw in enumerate(cases, 1):
            print("  #{:02d} h={}  len={:4d}  {!r}".format(
                idx, fnv.hash_text(textnorm.normalize(raw)), len(raw), raw))
        return EXIT_OK

    result = run_gate(golden, repo, strict=args.strict,
                      candidates_path=args.candidates, zh_dir=args.zh_dir)
    if not args.quiet:
        print_table(result, args.max_print)
        print_summary(result)
    if args.json:
        target = os.path.abspath(args.json)
        os.makedirs(os.path.dirname(target) or ".", exist_ok=True)
        with open(target, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    return result["exit"]


# ---------------------------------------------------------------------------
# 自检：假数据走通 / 走红两条路径（不进 CI 就没人跑，坏了也永远绿着）
# ---------------------------------------------------------------------------
def selftest():
    """返回 (failures, total)。构造最小仓库，把三步的绿/红、跳过、坏输入全走一遍。"""
    import shutil
    import tempfile

    failures = []
    total = [0]

    def check(name, ok, detail=""):
        total[0] += 1
        if ok:
            print("  [ok]   " + name)
        else:
            print("  [FAIL] " + name + ("  " + detail if detail else ""))
            failures.append(name)

    def eq(name, got, want):
        check(name, got == want, "got={!r} want={!r}".format(got, want))

    base = tempfile.mkdtemp(prefix="ui18n_golden_")
    try:
        # ---- 1. 清单解析：BOM / CRLF / 注释 / 空行 / 重复 / 原文逐字保留 --------------
        gpath = os.path.join(base, "parse", "rendered-strings.txt")
        os.makedirs(os.path.dirname(gpath), exist_ok=True)
        with open(gpath, "wb") as handle:
            handle.write("\ufeff# 注释\r\n\r\n  Load the vessel.  \r\nLoad the vessel.\r\nMass: <<1>> t\r\n"
                         .encode("utf-8"))
        cases, stats = load_golden(gpath)
        eq("BOM/CRLF/注释/空行/重复 都能吃", (len(cases), stats["comments"], stats["blanks"], stats["dups"]),
           (2, 1, 1, 1))
        eq("原文逐字保留（首尾空格不 trim）", cases[0], "  Load the vessel.  ")
        eq("h 在 normalize 之后算（= 不带首尾空格的同一条）",
           fnv.hash_text(textnorm.normalize(cases[0])), fnv.hash_text("Load the vessel."))

        only_blank = os.path.join(base, "parse", "blank.txt")
        with open(only_blank, "w", encoding="utf-8", newline="\n") as handle:
            handle.write("# 只有注释\n\n\n")
        try:
            load_golden(only_blank)
            check("空清单必须报错（不能静默 0 条通过）", False, "没抛异常")
        except GoldenError as exc:
            check("空清单必须报错（不能静默 0 条通过）", "一条用例都没有" in str(exc), str(exc))

        # 只有空白的行按空行算（记事本编辑留下的尾随空白不该变成「用例」），整份清单因此为空 → 必须报错
        ws_only = os.path.join(base, "parse", "ws_only.txt")
        with open(ws_only, "w", encoding="utf-8", newline="\n") as handle:
            handle.write("   \t\n")
        try:
            load_golden(ws_only)
            check("只有空白行的清单算空 → 报错", False, "没抛异常")
        except GoldenError as exc:
            check("只有空白行的清单算空 → 报错", "一条用例都没有" in str(exc), str(exc))

        # 守底分支：某条「用例」归一化后成了空串，必须报行号（不然 ①②③ 全找不到、红得没信息量）。
        # 现实中只有 normalize 口径被改坏才走得到，所以自检里临时把它换掉。
        weird = os.path.join(base, "parse", "weird.txt")
        with open(weird, "w", encoding="utf-8", newline="\n") as handle:
            handle.write("keeps this\n\u200b\n")
        original_normalize = textnorm.normalize
        try:
            textnorm.normalize = lambda text: "" if "\u200b" in text else original_normalize(text)
            try:
                load_golden(weird)
                check("归一化后为空的用例要报行号", False, "没抛异常")
            except GoldenError as exc:
                check("归一化后为空的用例要报行号", "第 2 行" in str(exc), str(exc))
        finally:
            textnorm.normalize = original_normalize

        try:
            load_golden(os.path.join(base, "parse", "nope.txt"))
            check("清单文件缺失必须报错", False, "没抛异常")
        except GoldenError as exc:
            check("清单文件缺失必须报错", "不存在" in str(exc), str(exc))

        # ---- 2. classify 返回值口径：元组 / 字符串 / dict / 认不出 / 抛异常 -----------
        eq("元组返回值", call_classifier(lambda t: ("candidate", "none"), "x"), ("candidate", "none", None))
        eq("字符串返回值", call_classifier(lambda t: "template", "x"), ("template", "", None))
        eq("dict 返回值", call_classifier(lambda t: {"bucket": "never", "reason": "path"}, "x"),
           ("never", "path", None))
        bad = call_classifier(lambda t: 42, "x")
        check("认不出的返回值要报错（不猜）", bad[2] is not None and "认不出" in bad[2], repr(bad))
        boom = call_classifier(lambda t: (_ for _ in ()).throw(RuntimeError("boom")), "x")
        check("classify 抛异常要报错", boom[2] is not None and "抛异常" in boom[2], repr(boom))
        check("dropped 一律判挂", not judge_step1("dropped", "noise", None)[0])
        check("含 drop 语义的 reason 判挂", not judge_step1("never", "dropped-by-filter", None)[0])
        check("未知 bucket 判挂", not judge_step1("maybe", "", None)[0])
        check("never 算过 ①（D-T3：never 也留档）", judge_step1("never", "path", None)[0])

        # ---- 3. 全绿 fixture ---------------------------------------------------------
        def make_repo(name, texts, cand_rows=None, zh_rows=None, write_candidates=True):
            repo = os.path.join(base, name)
            gpath = os.path.join(repo, "tests", "golden", "rendered-strings.txt")
            os.makedirs(os.path.dirname(gpath), exist_ok=True)
            with open(gpath, "w", encoding="utf-8", newline="\n") as handle:
                handle.write("# comment\n" + "\n".join(texts) + "\n")
            os.makedirs(os.path.join(repo, "packs", "zh-cn"), exist_ok=True)
            if write_candidates:
                rows = cand_rows if cand_rows is not None else [
                    {"h": fnv.hash_text(textnorm.normalize(t)), "s": t} for t in texts]
                with open(os.path.join(repo, "packs", "candidates.jsonl"), "w",
                          encoding="utf-8", newline="\n") as handle:
                    for row in rows:
                        handle.write(json.dumps(row, ensure_ascii=False) + "\n")
            zrows = zh_rows if zh_rows is not None else [
                {"h": fnv.hash_text(textnorm.normalize(t)), "s": t, "t": "译文"} for t in texts]
            with open(os.path.join(repo, "packs", "zh-cn", "Fake.jsonl"), "w",
                      encoding="utf-8", newline="\n") as handle:
                for row in zrows:
                    handle.write(json.dumps(row, ensure_ascii=False) + "\n")
            return repo, gpath

        texts = ["Load the vessel.", "Mass: <<1>> t", "RD-0410"]
        stub = lambda t: ("candidate", "")
        repo, gpath = make_repo("green", texts)
        result = run_gate(gpath, repo, classify_fn=stub)
        eq("全绿：exit 0", result["exit"], EXIT_OK)
        eq("全绿：0 条失败", len(result["failures"]), 0)
        eq("全绿：三条都在表里", result["counts"]["cases"], 3)
        eq("全绿：② 全部判定过（没有未判定）", result["counts"]["s2_unknown"], 0)

        # ---- 4. ② 挂：候选表少一条 ---------------------------------------------------
        repo, gpath = make_repo("red2", texts, cand_rows=[
            {"h": fnv.hash_text(textnorm.normalize(t)), "s": t} for t in texts[:2]])
        result = run_gate(gpath, repo, classify_fn=stub)
        eq("② 缺一条：exit 1", result["exit"], EXIT_FAIL)
        eq("② 缺一条：只挂那一条", [(f["idx"], f["step"]) for f in result["failures"]], [(3, 2)])
        check("② 缺一条：报的是「候选表里没有 h」",
              "候选表里没有" in result["failures"][0]["detail"], result["failures"][0]["detail"])

        # ---- 5. ② 诊断：h 口径漂移（表里有原文、h 不同）------------------------------
        wrong = [{"h": "0" * 16, "s": t} for t in texts]
        repo, gpath = make_repo("drift", texts, cand_rows=wrong)
        result = run_gate(gpath, repo, classify_fn=stub)
        check("h 与表里不一致时报「口径漂移」而不是「没有」",
              all("口径漂移" in f["detail"] for f in result["failures"]), str(result["failures"][:1]))

        # ---- 6. ③ 挂：t 为空 / 缺行 ------------------------------------------------
        repo, gpath = make_repo("red3", texts, zh_rows=[
            {"h": fnv.hash_text(textnorm.normalize(texts[0])), "s": texts[0], "t": "   "}])
        result = run_gate(gpath, repo, classify_fn=stub)
        eq("③ t 空白：三条全挂 ③", [(f["idx"], f["step"]) for f in result["failures"]],
           [(1, 3), (2, 3), (3, 3)])

        # ---- 7. keep 直通（t == 原文）仍算有译文 -------------------------------------
        keep_texts = ["RD-0410"]
        repo, gpath = make_repo("keep", keep_texts, zh_rows=[
            {"h": fnv.hash_text("RD-0410"), "s": "RD-0410", "t": "RD-0410"}])
        result = run_gate(gpath, repo, classify_fn=stub)
        eq("keep（t == 原文）算过 ③", result["exit"], EXIT_OK)
        eq("keep 在表里标出来", result["counts"]["keep"], 1)

        # ---- 8. ① 挂：过滤器又把人丢掉 ---------------------------------------------
        repo, gpath = make_repo("red1", texts)
        result = run_gate(gpath, repo, classify_fn=lambda t: ("dropped", "noise"))
        eq("① dropped：exit 1 且只挂 ①", sorted({f["step"] for f in result["failures"]}), [1])

        # ---- 9. 跳过语义：候选表不存在 → exit 2；--strict → exit 1 -------------------
        repo, gpath = make_repo("nopack", texts, write_candidates=False)
        result = run_gate(gpath, repo, classify_fn=stub)
        eq("候选表缺失：exit 2（明确跳过，不是通过）", result["exit"], EXIT_SKIP)
        eq("候选表缺失：② 记成未判定", result["counts"]["s2_unknown"], 3)
        check("候选表缺失：跳过原因写清楚",
              "候选单一表未落地" in result.get("skip_reason", ""), result.get("skip_reason"))
        strict_result = run_gate(gpath, repo, classify_fn=stub, strict=True)
        eq("--strict 时候选表缺失算挂", strict_result["exit"], EXIT_FAIL)

        # ---- 10. 候选表被写坏（坏行）→ 直接判挂 ------------------------------------
        repo, gpath = make_repo("badlines", texts)
        with open(os.path.join(repo, "packs", "candidates.jsonl"), "a", encoding="utf-8") as handle:
            handle.write("{ not json\n")
        result = run_gate(gpath, repo, classify_fn=stub)
        eq("候选表有坏行：exit 1", result["exit"], EXIT_FAIL)
        check("候选表有坏行：错误里给出行号",
              any("不是合法 JSON" in e for e in result["errors"]), str(result["errors"]))

        # ---- 11. --list 只列不判定 -------------------------------------------------
        repo, gpath = make_repo("listonly", texts)
        buffer = io.StringIO()
        stdout, sys.stdout = sys.stdout, buffer
        try:
            code = main(["--golden", gpath, "--repo", repo, "--list"])
        finally:
            sys.stdout = stdout
        eq("--list exit 0", code, EXIT_OK)
        check("--list 打出每条现算的 h", buffer.getvalue().count("h=") == 3, buffer.getvalue()[:200])

        # ---- 12. 真 classify_capture 集成（只锁「可留档」，不锁具体 reason）----------
        real_bucket, real_reason, real_error = call_classifier(default_classifier(), "Load the vessel.")
        check("真 capture_ingest.classify_capture 可用且正常英文可留档",
              real_error is None and judge_step1(real_bucket, real_reason, None)[0],
              "bucket={!r} reason={!r} err={!r}".format(real_bucket, real_reason, real_error))
    finally:
        shutil.rmtree(base, ignore_errors=True)

    return failures, total[0]


if __name__ == "__main__":
    sys.exit(main())
