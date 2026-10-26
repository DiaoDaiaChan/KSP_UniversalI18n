#!/usr/bin/env python3
"""harvest_cfgcache.py —— 从 ModuleManager.ConfigCache 抽「运行时最终文本」建候选清单。

为什么要有这个工具（1.4 的 R0）：
  harvest.py 的源二是**原始 cfg 文本字段**，而且明确规定「补丁写法（@title / @description）不算命中」
  （见 harvest.py:258-265 的实测依据）。这条规则对「替换原文」的老式汉化补丁成立，但对本 mod 不成立：
  我们在**运行时**按文本 hash 查表，而运行时的字符串是 ModuleManager 打完补丁之后的合成结果。
  于是 RO / RP-1 这类重度补丁的整合包里，玩家看到的 description 是补丁值，池子里却是原始值 —— 对不上。

真凭实据（KSP 1.12.5 + RO/RP-1 实装）：
  GameData/RealismOverhaul/RO_RealFuels_PartConfigs.cfg:225  @description = Specialized tanks for batteries. ...
  这一行被 harvest 明确排除，而 GameData/ModuleManager.ConfigCache:640600 里它就是零件的最终 description。

ModuleManager.ConfigCache 是 MM 打完所有补丁后写下的完整 ConfigNode 树（本机 27.7 MB），
**它里面的 title / description / displayName / manufacturer 就是游戏真正读到的字符串**，离线可扫、可复现。

产物：packs/cfgcache-candidates.jsonl，行格式与 packs/capture-candidates.jsonl 同构（translate_il 直接吃）：
  {"h","n","s","sink","caller","count","bucket","reason","mods","fields"}
输出行只含**字典里还没有的**（--include-covered 可全量导出用于对账）。

v1.4 新增跳过原因 `keyref`：值形如 `#LOC_CRP_X_Title` / `#autoLOC_500100` / `#Sol_Site_xxx_desc`
（归一化后整串匹配 `^#[A-Za-z0-9_]+$`）时跳过。理由：运行时 KSP Localizer 会把这个 key 解析成
**另一条**字符串，按 key 原文建的字典条目永远命不中，还会白占翻译量。整串匹配保证
「# 开头但含空格/中文」的真正文不受影响。

CLI:
  python -m i18n_tools.harvest_cfgcache --gamedata <GameData> [--out ...] [--report ...]
  python -m i18n_tools.harvest_cfgcache --cache <path/to/ModuleManager.ConfigCache>
  python -m i18n_tools.harvest_cfgcache --selftest
"""

from __future__ import annotations

import argparse
import datetime
import os
import re
import sys

from . import ensure_stdout, fnv, textnorm

FIELDS = ("title", "description", "displayname", "manufacturer")
FIELD_LABEL = {"displayname": "displayName"}

# 键值行：值里可以有 = 号（ConfigNode 允许多个 = 出现在值中），所以只切第一个
_KV_RE = re.compile(r"^[ \t]*[@$%!&]*([A-Za-z_][A-Za-z0-9_]*)[ \t]*=[ \t]*(.*)$")
_LETTER_RE = re.compile(r"[A-Za-z]")
_URL_RE = re.compile(r"[a-z]+://", re.I)
_WINPATH_RE = re.compile(r"^[A-Za-z]:[\/]")
_PURE_PUNCT_RE = re.compile(r"^[^0-9A-Za-z]+$")
# 纯本地化 key 引用：`#LOC_x_Title` / `#autoLOC_500100` / `#Sol_Site_xxx_desc`。
# 运行时这些值会被 KSP Localizer 解析成**别的**字符串，按 key 原文建字典永远命中不了，
# 还会白占翻译量。整串匹配（不容空格/标点），所以「#」开头但带空格的真正文不会被误伤。
_KEYREF_RE = re.compile(r"^#[A-Za-z0-9_]+$")

# 报告里列多少条 keyref 样例
KEYREF_SAMPLE_LIMIT = 5

MOD_LABEL = "UI18n-ConfigCache"


def _repo_root():
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def default_cache_path(gamedata):
    return os.path.join(gamedata, "ModuleManager.ConfigCache")


def iter_fields(path, fields=None, stats=None):
    """流式读 ConfigCache，产出 (field, raw_value)。

    不做完整 ConfigNode 解析：这份文件 2,700 万字符、600 万行以上，
    而我们要的四个文本字段都是**叶子键值**，逐行正则既快又不会因格式怪异整棵树失败。
    """
    fields = tuple(fields or FIELDS)
    with open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            if "=" not in line:
                continue
            match = _KV_RE.match(line.rstrip(chr(10)))
            if match is None:
                continue
            key = match.group(1).lower()
            if key not in fields:
                continue
            value = match.group(2).strip()
            if stats is not None:
                stats["assign"][key] = stats["assign"].get(key, 0) + 1
            if value:
                yield key, value


def classify(field, raw, seen, opts):
    """返回 (reason, normalized) —— reason 为 None 表示可入池。"""
    if not isinstance(raw, str):
        return "not-text", ""
    norm = textnorm.normalize(raw)
    if not norm:
        return "empty", ""
    # 顺序有讲究：keyref 必须在 already-cjk / too-short / too-long 之前判定。
    # 否则 `#autoLOC_500100` 这种短 key 会被 too-short 记成另一个原因，统计就对不上了。
    if _KEYREF_RE.match(norm):
        return "keyref", norm
    if textnorm.has_cjk(norm):
        return "already-cjk", norm
    if len(norm) < opts["min_len"]:
        return "too-short", norm
    if len(norm) > opts["max_len"]:
        return "too-long", norm
    if not _LETTER_RE.search(norm):
        return "no-letter", norm
    if _PURE_PUNCT_RE.match(norm):
        return "punct", norm
    if _URL_RE.search(norm):
        return "url", norm
    if _WINPATH_RE.match(norm):
        return "path", norm
    if textnorm.is_pure_number(norm):
        return "number", norm
    return None, norm


def load_pack_hashes(packs_dir):
    """语言包里已有的 h 集合（含骨架行）——已被覆盖的文本不用再排队。"""
    import json
    hashes = set()
    if not packs_dir or not os.path.isdir(packs_dir):
        return hashes
    for name in sorted(os.listdir(packs_dir)):
        if not name.endswith(".jsonl"):
            continue
        for line in open(os.path.join(packs_dir, name), encoding="utf-8"):
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except ValueError:
                continue
            h = row.get("h")
            if isinstance(h, str) and h:
                hashes.add(h.lower())
    return hashes


def collect(cache_path, opts, pack_hashes=None, stats=None):
    """扫一遍 ConfigCache，返回按 h 去重的条目列表。"""
    stats = stats if stats is not None else {}
    stats.setdefault("assign", {})
    stats.setdefault("skip", {})
    stats.setdefault("keyref_samples", [])
    stats.setdefault("lines", 0)
    rows = {}
    order = []
    for field, raw in iter_fields(cache_path, FIELDS, stats):
        stats["lines"] += 1
        reason, norm = classify(field, raw, rows, opts)
        if reason is not None:
            stats["skip"][reason] = stats["skip"].get(reason, 0) + 1
            if reason == "keyref":
                samples = stats["keyref_samples"]
                if len(samples) < KEYREF_SAMPLE_LIMIT and norm not in samples:
                    samples.append(norm)
            continue
        h = fnv.hash_text(norm)
        label = FIELD_LABEL.get(field, field)
        if h in rows:
            row = rows[h]
            if label not in row["fields"]:
                row["fields"].append(label)
            row["assigns"] += 1
            continue
        rows[h] = {"h": h, "n": norm, "s": raw, "fields": [label], "assigns": 1}
        order.append(h)
    items = [rows[h] for h in order]
    stats["unique"] = len(items)
    if pack_hashes is not None:
        covered = [r for r in items if r["h"] in pack_hashes]
        items = [r for r in items if r["h"] not in pack_hashes]
        stats["covered"] = len(covered)
        stats["missing"] = len(items)
    return items


def to_candidate_rows(items, mod_label=MOD_LABEL):
    rows = []
    for item in items:
        field = item["fields"][0].lower()
        rows.append({
            "h": item["h"],
            "n": item["n"],
            "s": item["s"],
            "sink": "configcache",
            "caller": "ConfigCache:" + field,
            "count": item["assigns"],
            "firstSeen": "",
            "lastSeen": "",
            "sessions": 1,
            "callers": 1,
            "bucket": "candidate",
            "reason": "",
            "mods": [mod_label],
            "field": field,
            "fields": item["fields"],
        })
    return rows


def write_jsonl(path, rows):
    import json
    os.makedirs(os.path.dirname(os.path.abspath(path)) or ".", exist_ok=True)
    with open(path, "w", encoding="utf-8", newline=chr(10)) as handle:
        for row in rows:
            handle.write(json.dumps(row, ensure_ascii=False, sort_keys=False) + chr(10))
    return os.path.getsize(path)

def build_report(path, cache_path, items, stats, covered_total=None):
    """写 reports/cfgcache-gap.md。"""
    import collections
    st = os.stat(cache_path)
    mtime = datetime.datetime.fromtimestamp(st.st_mtime).strftime("%Y-%m-%d %H:%M:%S")
    by_field = collections.Counter(item["fields"][0] for item in items)
    by_len = collections.Counter()
    for item in items:
        n = len(item["s"])
        key = chr(8804) + "40" if n <= 40 else ("41-100" if n <= 100 else ("101-300" if n <= 300 else ">300"))
        by_len[key] += 1
    chars = sum(len(item["s"]) for item in items)
    lines = []
    add = lines.append
    add("# ConfigCache 文本缺口")
    add("")
    add("> 自动生成，勿手改：`python -m i18n_tools.harvest_cfgcache --gamedata <GameData>`")
    add("")
    add("| 项 | 值 |")
    add("| --- | --- |")
    add("| 输入 | `" + cache_path.replace(chr(92), "/") + "` |")
    add("| 大小 | {:,} B |".format(st.st_size))
    add("| 修改时间 | " + mtime + " |")
    add("| 键值行（四个字段） | {:,} |".format(stats.get("lines", 0)))
    add("| 唯一文本 | {:,} |".format(stats.get("unique", 0)))
    if covered_total is not None:
        add("| 已被字典覆盖 | {:,} |".format(covered_total))
    add("| **本次要翻（缺）** | **{:,}** |".format(len(items)))
    add("| 缺失总字符 | {:,} |".format(chars))
    add("")
    add("## 1. 赋值分布（去重前）")
    add("")
    add("| 字段 | 赋值数 |")
    add("| --- | --- |")
    for field in FIELDS:
        add("| `" + FIELD_LABEL.get(field, field) + "` | {:,} |".format(stats.get("assign", {}).get(field, 0)))
    add("")
    add("## 2. 缺失按字段")
    add("")
    add("| 字段 | 条数 |")
    add("| --- | --- |")
    for field, count in by_field.most_common():
        add("| `" + field + "` | {:,} |".format(count))
    add("")
    add("## 3. 缺失按长度")
    add("")
    add("| 长度 | 条数 |")
    add("| --- | --- |")
    for key in (chr(8804) + "40", "41-100", "101-300", ">300"):
        if by_len.get(key):
            add("| " + key + " | {:,} |".format(by_len[key]))
    add("")
    add("## 4. 跳过明细")
    add("")
    add("| 原因 | 次数 |")
    add("| --- | --- |")
    for reason, count in sorted(stats.get("skip", {}).items(), key=lambda kv: -kv[1]):
        add("| `" + reason + "` | {:,} |".format(count))
    add("")
    add("## 5. `keyref` 跳过（纯本地化 key 引用）")
    add("")
    keyref_skips = stats.get("skip", {}).get("keyref", 0)
    add("本次命中 **{:,}** 次，全部跳过、不进候选清单。".format(keyref_skips))
    add("")
    add("判据：值归一化后整串匹配 `^#[A-Za-z0-9_]+$`（例如 `#LOC_CRP_X_Title`、")
    add("`#autoLOC_500100`、`#Sol_Site_esrange_desc`）。这类原文在运行时会被 KSP `Localizer`")
    add("解析成**别的**字符串，按 key 原文建字典永远命中不了，还会白占翻译量。")
    add("整串匹配意味着「#」开头但含空格/中文的真正文不受影响（它们照常按正常规则判）。")
    add("")
    keyref_samples = stats.get("keyref_samples") or []
    if keyref_samples:
        add("被跳过的样例（最多 " + str(KEYREF_SAMPLE_LIMIT) + " 条）：")
        add("")
        for sample in keyref_samples:
            add("- `" + sample + "`")
    else:
        add("（本次没有命中 keyref）")
    add("")
    add("## 6. 为什么这些字符串以前进不了池")
    add("")
    add("两条独立的原因，缺一不可：")
    add("")
    add("1. `harvest.py` 只收**原始 cfg** 的文本字段，且明确规定补丁写法（`@title` / `@description`）不算命中")
    add("   （依据见 `tools/i18n_tools/harvest.py:258-265`）。可是运行时字符串是 MM 打完补丁之后的合成结果，")
    add("   于是 RO / RP-1 这类整合包里，池子里的原文与玩家看到的文本对不上 hash。")
    add("2. 采集通道（`capture_ingest.py:121 MAX_LEN = 300`、C# `CaptureOptions.MaxTextLength = 400`）把长文本当噪声丢掉，")
    add("   而零件 description 长度的 p90 是 204、p99 是 577 字符 —— 最需要翻的那批正好落在上限外面。")
    add("")
    add("ModuleManager.ConfigCache 是 MM 打完所有补丁后写下的完整 ConfigNode 树，")
    add("里面这几个文本字段**就是游戏真正读到的字符串**，所以它才是本 mod 该用的正文来源。")
    add("")
    add("## 7. 复现")
    add("")
    add("```")
    add("cd tools")
    add("python -m i18n_tools.harvest_cfgcache --gamedata " + chr(34) + "D:/Kerbal Space Program_newmod/GameData" + chr(34))
    add("```")
    add("")
    os.makedirs(os.path.dirname(os.path.abspath(path)) or ".", exist_ok=True)
    with open(path, "w", encoding="utf-8", newline=chr(10)) as handle:
        handle.write(chr(10).join(lines) + chr(10))
    return os.path.getsize(path)


def selftest():
    import tempfile
    checks = []

    def eq(name, got, want):
        checks.append((name, got == want, "got={!r} want={!r}".format(got, want)))

    cache_text = chr(10).join([
        "PART",
        "{",
        chr(9) + "name = probePart",
        chr(9) + "title = Probe Part",
        chr(9) + "description = A short description of the probe.",
        chr(9) + "manufacturer = Generic",
        chr(9) + "tags = probe part",
        chr(9) + "MODULE",
        chr(9) + "{",
        chr(9) + chr(9) + "displayName = Signal Mode",
        chr(9) + "}",
        "}",
        "PART",
        "{",
        chr(9) + "name = dupPart",
        chr(9) + "title = Probe Part",
        chr(9) + "description = " + chr(36825) + chr(37324) + chr(26159) + chr(20013) + chr(25991),
        chr(9) + "manufacturer = https://example.com",
        "}",
        "PART",
        "{",
        chr(9) + "name = longPart",
        chr(9) + "title = #LOC_CRP_Long_Title",
        chr(9) + "description = " + ("very long text " * 200),
        "}",
    ])
    handle = tempfile.NamedTemporaryFile("w", suffix=".ConfigCache", delete=False, encoding="utf-8", newline=chr(10))
    handle.write(cache_text)
    handle.close()
    try:
        opts = {"min_len": 2, "max_len": 2000}
        stats = {}
        items = collect(handle.name, opts, None, stats)
        texts = sorted(item["s"] for item in items)
        eq("四个字段都抽到（不含 tags）", texts, sorted(["Probe Part", "A short description of the probe.", "Generic", "Signal Mode"]))
        eq("同一文本出现两次只留一行", len(items), 4)
        eq("中文值被 already-cjk 跳过", stats["skip"].get("already-cjk"), 1)
        eq("URL 值被 url 跳过", stats["skip"].get("url"), 1)
        eq("超长值被 too-long 跳过", stats["skip"].get("too-long"), 1)
        eq("tags 字段不进池", any("probe part" == item["s"] for item in items), False)
        eq("纯本地化 key 引用判 keyref（#LOC_..._Title）",
           classify("title", "#LOC_CRP_X_Title", {}, opts)[0], "keyref")
        eq("# 开头但含空格/中文的真正文不判 keyref",
           classify("title", "#1 " + chr(21495) + chr(32592) + chr(20307), {}, opts)[0], "already-cjk")
        eq("# 开头但含空格的英文正文照常入池",
           classify("title", "#1 fuel tank", {}, opts)[0], None)
        eq("keyref 计入跳过统计", stats["skip"].get("keyref"), 1)
        eq("keyref 跳过样例被记录", stats.get("keyref_samples"), ["#LOC_CRP_Long_Title"])
        eq("keyref 不进候选清单", any(item["s"].startswith("#") for item in items), False)
        rows = to_candidate_rows(items)
        eq("产物 bucket 都是 candidate", sorted({row["bucket"] for row in rows}), ["candidate"])
        eq("产物带 capture schema 的 s/caller/sink", (sorted(rows[0].keys())[:2], rows[0]["sink"]), (["bucket", "caller"], "configcache"))
        pack = {items[0]["h"]}
        missing = collect(handle.name, opts, pack, {})
        eq("已被字典覆盖的文本不再排队", len(missing), len(items) - 1)
    finally:
        os.unlink(handle.name)

    failed = [row for row in checks if not row[1]]
    for name, ok, detail in checks:
        print(("  ok   " if ok else "  FAIL ") + name + ("" if ok else "  " + detail))
    print("[selftest] " + str(len(checks)) + " " + chr(39033) + chr(65292) + chr(22833) + chr(36133) + " " + str(len(failed)))
    return 1 if failed else 0


def main(argv=None):
    global FIELDS
    ensure_stdout()
    parser = argparse.ArgumentParser(description="cong ModuleManager.ConfigCache chou yun xing shi zui zhong wen ben")
    parser.add_argument("--cache", help="ModuleManager.ConfigCache path")
    parser.add_argument("--gamedata", help="GameData path")
    parser.add_argument("--packs-dir", default=None, help="language pack dir (default <repo>/packs/zh-cn)")
    parser.add_argument("--out", default=None, help="output jsonl (default <repo>/packs/cfgcache-candidates.jsonl)")
    parser.add_argument("--report", default=None, help="report md (default <repo>/reports/cfgcache-gap.md)")
    parser.add_argument("--fields", default=",".join(FIELDS), help="fields to harvest, comma separated")
    parser.add_argument("--min-len", type=int, default=2)
    parser.add_argument("--max-len", type=int, default=2000)
    parser.add_argument("--include-covered", action="store_true", help="also export texts already covered")
    parser.add_argument("--mod-label", default=MOD_LABEL, help="mods label for output rows")
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--quiet", action="store_true")
    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

    FIELDS = tuple(part.strip().lower() for part in args.fields.split(",") if part.strip())

    cache_path = args.cache
    if not cache_path and args.gamedata:
        cache_path = default_cache_path(args.gamedata)
    if not cache_path:
        parser.error("need --cache or --gamedata (file lives in the game dir, not in repo)")
    if not os.path.isfile(cache_path):
        parser.error("ConfigCache not found: " + cache_path)

    repo = _repo_root()
    packs_dir = args.packs_dir or os.path.join(repo, "packs", "zh-cn")
    out_path = args.out or os.path.join(repo, "packs", "cfgcache-candidates.jsonl")
    report_path = args.report or os.path.join(repo, "reports", "cfgcache-gap.md")

    pack_hashes = None if args.include_covered else load_pack_hashes(packs_dir)
    stats = {}
    items = collect(cache_path, {"min_len": args.min_len, "max_len": args.max_len}, pack_hashes, stats)
    rows = to_candidate_rows(items, args.mod_label)
    size = write_jsonl(out_path, rows)
    report_size = build_report(report_path, cache_path, items, stats, stats.get("covered"))

    if not args.quiet:
        print("[cfgcache] cached file: " + cache_path)
        print("[cfgcache] unique {:,} / covered {:,} / todo {:,} ({:,} chars)".format(
            stats.get("unique", 0), stats.get("covered", 0) or 0, len(rows),
            sum(len(row["s"]) for row in rows)))
        skip_text = ", ".join("{0}={1}".format(k, v) for k, v in sorted(stats.get("skip", {}).items()))
        print("[cfgcache] skipped: " + skip_text)
        if stats.get("keyref_samples"):
            print("[cfgcache] keyref samples: " + ", ".join(stats["keyref_samples"]))
        print("[cfgcache] out: {:,} B -> {}".format(size, out_path))
        print("[cfgcache] report: {:,} B -> {}".format(report_size, report_path))
    return 0


if __name__ == "__main__":
    sys.exit(main())
