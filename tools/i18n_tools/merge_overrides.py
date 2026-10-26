#!/usr/bin/env python3
"""merge_overrides.py —— 把分片译文（ov-*.jsonl）合并进 packs/overrides.jsonl。

为什么需要：长描述这类活只能分片并行（每片 80 KB 左右），各片产物是同 schema 的 ov 文件；
合并必须做三件事，缺一件就会把坏数据带进流水线：

  1. 逐行过 check_overrides 的硬门禁（占位符 / 富文本标签 / 首尾空白 / 换行数 / 长度比 / 没翻）；
  2. 按 normalize(en) 去重，**既有 overrides 优先**（人工改过的译法不许被分片覆盖）；
  3. 分片之间同一 en 出现两份不同译文时记冲突，取先出现的那个，冲突必须报出来。

输出仍是 packs/overrides.jsonl 的原格式：{"en","zh","note"[,"keep":true]}，
既有行顺序不动，新行按 en 排序追加（两次相同的输入必然产出逐字节相同的文件）。

CLI:
  python -m i18n_tools.merge_overrides --shards "_scratch/cc/ov-*.jsonl" --check
  python -m i18n_tools.merge_overrides --shards a.jsonl b.jsonl --out packs/overrides.jsonl
  python -m i18n_tools.merge_overrides --selftest
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import sys

from . import check_overrides, ensure_stdout, textnorm

PACKS_ROOT = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__)))), "packs")
BASE_PATH = os.path.join(PACKS_ROOT, "overrides.jsonl")
ZH_DIR = os.path.join(PACKS_ROOT, "zh-cn")

MAX_EXAMPLES = 8


def expand(paths):
    """把带通配符的字符串展开成文件清单（按名字排序，保证合并结果可复现）。"""
    out = []
    for raw in paths:
        if not raw:
            continue
        if "*" in raw or "?" in raw:
            out.extend(sorted(p for p in glob.glob(raw) if os.path.isfile(p)))
        else:
            out.append(raw)
    seen, uniq = set(), []
    for p in out:
        key = os.path.normcase(os.path.abspath(p))
        if key not in seen:
            seen.add(key)
            uniq.append(p)
    return uniq


def iter_rows(path):
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except ValueError:
                yield None
                continue
            yield row if isinstance(row, dict) else None


def load_base(path):
    """读既有 overrides：返回 (rows, index)。坏行跳过（不动它，原样写回）。"""
    rows, index = [], {}
    if not path or not os.path.isfile(path):
        return rows, index
    for row in iter_rows(path):
        if not isinstance(row, dict):
            continue
        en, zh = row.get("en"), row.get("zh")
        if not isinstance(en, str) or not isinstance(zh, str):
            continue
        rows.append(row)
        index.setdefault(textnorm.normalize(en), len(rows) - 1)
    return rows, index


def pack_hashes(zh_dir=None):
    """已进字典的 h 集合（用来报「这条覆盖其实是空操作」，不参与判定）。"""
    hashes = set()
    folder = zh_dir or ZH_DIR
    if not os.path.isdir(folder):
        return hashes
    for name in os.listdir(folder):
        if not name.endswith(".jsonl"):
            continue
        for row in iter_rows(os.path.join(folder, name)):
            if isinstance(row, dict) and isinstance(row.get("h"), str):
                hashes.add(row["h"].lower())
    return hashes


def merge(shard_paths, base_rows=None, base_index=None, covered=None):
    """返回 (新 overrides 行清单, 统计字典)。纯函数，selftest 直接调它。"""
    base_rows = list(base_rows or [])
    base_index = dict(base_index or {})
    covered = covered if covered is not None else set()

    stats = {
        "shards": len(shard_paths),
        "shard_rows": 0,
        "bad_line": 0,
        "bad_row_key": 0,
        "rejected": {},          # 门禁原因 -> 条数
        "dup_base": 0,           # 既有 overrides 已有，保留既有
        "dup_base_diff": 0,      # 已有但译文不同（既有优先）
        "dup_shard": 0,          # 分片间重复
        "dup_shard_diff": 0,     # 分片间重复且译文不同
        "pack_covered": 0,       # 字典里已经有这条英文的译文（覆盖是空操作）
        "added": 0,
        "keep": 0,
    }
    examples = {"rejected": [], "dup_base_diff": [], "dup_shard_diff": [], "bad_row_key": []}

    new_rows, new_index = [], {}
    for path in shard_paths:
        for row in iter_rows(path):
            if row is None:
                stats["bad_line"] += 1
                continue
            stats["shard_rows"] += 1
            en, zh = row.get("en"), row.get("zh")
            keep = bool(row.get("keep"))
            if not isinstance(en, str) or not en.strip() or not isinstance(zh, str):
                stats["bad_row_key"] += 1
                if len(examples["bad_row_key"]) < MAX_EXAMPLES:
                    examples["bad_row_key"].append({"file": os.path.basename(path),
                                                    "en": str(en)[:60]})
                continue

            errors = check_overrides.check_row(en, zh, keep)
            if errors:
                tag = errors[0].split(":")[0]
                stats["rejected"][tag] = stats["rejected"].get(tag, 0) + 1
                if len(examples["rejected"]) < MAX_EXAMPLES:
                    examples["rejected"].append({"file": os.path.basename(path), "en": en[:60],
                                                 "zh": zh[:60], "errors": errors})
                continue

            key = textnorm.normalize(en)
            if key in base_index:
                stats["dup_base"] += 1
                existing = base_rows[base_index[key]]
                if existing.get("zh") != zh:
                    stats["dup_base_diff"] += 1
                    if len(examples["dup_base_diff"]) < MAX_EXAMPLES:
                        examples["dup_base_diff"].append({"en": en[:60], "base": str(existing.get("zh"))[:60],
                                                          "shard": zh[:60]})
                continue
            if key in new_index:
                stats["dup_shard"] += 1
                first = new_rows[new_index[key]]
                if first.get("zh") != zh:
                    stats["dup_shard_diff"] += 1
                    if len(examples["dup_shard_diff"]) < MAX_EXAMPLES:
                        examples["dup_shard_diff"].append({"file": os.path.basename(path), "en": en[:60],
                                                           "first": first.get("zh", "")[:60], "later": zh[:60]})
                continue

            out = {"en": en, "zh": zh}
            note = row.get("note")
            if isinstance(note, str) and note:
                out["note"] = note
            if keep:
                out["keep"] = True
                stats["keep"] += 1
            new_rows.append(out)
            new_index[key] = len(new_rows) - 1
            if textnorm.normalize(en) and _hash_of(key) in covered:
                stats["pack_covered"] += 1

    stats["added"] = len(new_rows)
    new_rows.sort(key=lambda r: r["en"])
    return base_rows + new_rows, stats, examples


def _hash_of(normalized):
    from . import fnv
    return fnv.hash_text(normalized)


def write_rows(path, rows):
    folder = os.path.dirname(os.path.abspath(path))
    if folder and not os.path.isdir(folder):
        os.makedirs(folder)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as handle:
        for row in rows:
            handle.write(json.dumps(row, ensure_ascii=False, sort_keys=False) + "\n")
    os.replace(tmp, path)


def selftest():
    import tempfile
    failures, checks = [], [0]

    def check(name, ok, detail=""):
        checks[0] += 1
        if not ok:
            failures.append("{} {}".format(name, detail).strip())

    base = [{"en": "Alpha", "zh": "甲", "note": "既有"},
            {"en": "Beta", "zh": "乙", "note": "既有"}]
    base_rows, base_index = base, {textnorm.normalize(r["en"]): i for i, r in enumerate(base)}

    tmp = tempfile.mkdtemp(prefix="ui18n_merge_")
    try:
        good = os.path.join(tmp, "ov-a.jsonl")
        with open(good, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps({"en": "Gamma", "zh": "丙", "note": "cc:x"}, ensure_ascii=False) + "\n")
            handle.write(json.dumps({"en": "Alpha", "zh": "甲甲", "note": "conflict"}, ensure_ascii=False) + "\n")
            handle.write(json.dumps({"en": "Delta", "zh": "Delta", "keep": True}, ensure_ascii=False) + "\n")
            handle.write(json.dumps({"en": "Epsilon", "zh": "", "note": "empty"}, ensure_ascii=False) + "\n")
            handle.write(json.dumps({"en": "Zeta Ray", "zh": "zeta ray", "note": "no-cjk"}, ensure_ascii=False) + "\n")
            handle.write(json.dumps({"en": "Eta", "zh": "伊塔", "keep": True}, ensure_ascii=False) + "\n")
            handle.write("{not json}\n")
        dup = os.path.join(tmp, "ov-b.jsonl")
        with open(dup, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps({"en": "Gamma", "zh": "丙丙"}, ensure_ascii=False) + "\n")
        rows, stats, examples = merge([good, dup], base_rows, base_index)
    finally:
        import shutil
        shutil.rmtree(tmp, ignore_errors=True)

    got = {r["en"]: r for r in rows}
    check("既有行原样保留", rows[0] == base[0] and rows[1] == base[1], rows[:2])
    check("新行被追加", "Gamma" in got and "Delta" in got, sorted(got))
    check("既有优先，不吃分片的重译", got["Alpha"]["zh"] == "甲", got["Alpha"])
    check("既有译文冲突被计数", stats["dup_base_diff"] == 1, stats["dup_base_diff"])
    check("分片间冲突被计数", stats["dup_shard_diff"] == 1, stats["dup_shard_diff"])
    check("空译被门禁拒绝", stats["rejected"].get("empty-zh") == 1, stats["rejected"])
    check("没翻（无 CJK）被门禁拒绝", stats["rejected"].get("no-cjk", 0) >= 1, stats["rejected"])
    check("keep 但改了原文被门禁拒绝", stats["rejected"].get("keep-but-changed") == 1, stats["rejected"])
    check("坏行被计数", stats["bad_line"] == 1, stats["bad_line"])
    check("keep 行保留 keep 标记", got.get("Delta", {}).get("keep") is True, got.get("Delta"))
    check("note 被保留", got["Gamma"].get("note") == "cc:x", got.get("Gamma"))
    check("新行按 en 排序", [r["en"] for r in rows[2:]] == sorted(r["en"] for r in rows[2:]),
          [r["en"] for r in rows[2:]])
    check("examples 有内容", bool(examples["rejected"]) or stats["rejected"], examples["rejected"])
    return failures, checks[0]


def main(argv=None):
    ensure_stdout()
    parser = argparse.ArgumentParser(prog="merge_overrides",
                                     description="分片 ov-*.jsonl -> packs/overrides.jsonl")
    parser.add_argument("--shards", nargs="*", default=[], help="分片文件或通配符（可多个）")
    parser.add_argument("--base", default=BASE_PATH, help="既有 overrides（默认 packs/overrides.jsonl）")
    parser.add_argument("--out", default=BASE_PATH, help="输出路径（默认写回 --base）")
    parser.add_argument("--check", action="store_true", help="只体检不写文件")
    parser.add_argument("--json", dest="json_out", default=None, help="把统计写成 json")
    parser.add_argument("--selftest", action="store_true")
    args = parser.parse_args(argv)

    if args.selftest:
        failures, total = selftest()
        if failures:
            print("[FAIL] {} 项检查，{} 项失败：".format(total, len(failures)))
            for item in failures:
                print("  - " + item)
            return 1
        print("[OK] {} 项检查，0 项失败".format(total))
        return 0

    shards = expand(args.shards)
    if not shards:
        print("[merge] 没有分片文件（--shards 为空或通配符没命中）")
        return 2
    base_rows, base_index = load_base(args.base)
    covered = pack_hashes()
    rows, stats, examples = merge(shards, base_rows, base_index, covered)

    print("[merge] 分片 {} 个 / 读入 {} 行；既有 overrides {} 行；字典已有 {} 条 h".format(
        len(shards), stats["shard_rows"], len(base_rows), len(covered)))
    print("[merge] 新增 {} 条（其中 keep {} 条）；跳过：既有 {}（其中译文不同 {}）/ 分片间重复 {}（译文不同 {}）".format(
        stats["added"], stats["keep"], stats["dup_base"], stats["dup_base_diff"],
        stats["dup_shard"], stats["dup_shard_diff"]))
    if stats["rejected"]:
        print("[merge] 门禁拒绝 {} 条：{}".format(
            sum(stats["rejected"].values()),
            ", ".join("{}={}".format(k, v) for k, v in sorted(stats["rejected"].items()))))
    if stats["bad_line"] or stats["bad_row_key"]:
        print("[merge] 坏行 {} / 缺字段 {}（已跳过）".format(stats["bad_line"], stats["bad_row_key"]))
    if stats["pack_covered"]:
        print("[merge] 其中 {} 条的英文在字典里已有译文（覆盖会顶掉既有译文，注意抽检）".format(
            stats["pack_covered"]))
    for tag in ("rejected", "dup_base_diff", "dup_shard_diff"):
        for item in examples[tag][:3]:
            print("[merge]   {}: {}".format(tag, json.dumps(item, ensure_ascii=False)[:200]))

    if args.json_out:
        payload = dict(stats)
        payload["examples"] = examples
        payload["shards"] = [os.path.abspath(p) for p in shards]
        payload["out_rows"] = len(rows)
        with open(args.json_out, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(payload, handle, ensure_ascii=False, indent=2)
            handle.write("\n")
        print("[merge] 统计 -> " + args.json_out)

    if args.check:
        print("[merge] --check：未写文件")
        return 0
    if stats["added"] == 0:
        print("[merge] 没有新增，未写文件")
        return 0
    write_rows(args.out, rows)
    print("[merge] 写入 {} 行 -> {}".format(len(rows), args.out))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
