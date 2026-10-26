#!/usr/bin/env python3
"""candidates.py —— 真相层总表：把五条通道合并成一张 packs/candidates.jsonl。

为什么要有它（docs/08-真相层-设计.md 的 D-T3）：1.4 之前每条通道各有一份候选清单、
各有各的分母 —— pool 只知道 cfg/loc 抽到多少、il 只知道 IL 指令里抽到多少、cfgcache 与
capture 各写各的 jsonl，谁也没法回答「到这一刻还有多少英文没翻」。这张表按同一套 h
（fnv1a64(normalize(s))）把五条通道去重成一份：每条 distinct h 恰好一行，covered 直接
告诉你它在 packs/zh-cn 里有没有；bucket 不再决定去留，只决定送翻顺序
（candidate > pattern > template > never，never 也要留档）。

口径（与 capture_ingest 一致，别另发明）：
  h        fnv1a64(normalize(s))，16 位小写十六进制（tools/i18n_tools/fnv.py）
  s        归一化原文
  src      原样原文（没归一化的那份；静态通道两者常常只差尾部空白）
  channel  cfg / loc（取自 packs/pool.jsonl 的 src 字段）/ il / cfgcache / capture
  bucket   candidate / template / never —— 捕获端的 pattern 是塌缩后的骨架，
           不属于本表（骨架的真身仍是 template 行，本表留真身）
  reason   源行的 reason；空串统一写成 "none"
  covered  h 是否已在 packs/zh-cn/*.jsonl
  count    源行频次；pool / il 是静态抽取、没有频次，记 1

去重规则：同一个 h 可能同时出现在多条通道（运行时捕获到了、静态抽取也抽到了），按
capture > il > cfgcache > loc > cfg 取优先级最高的那一行，整行采用、**count 不合并**
—— 频次只在同一通道内部有意义（一个是渲染次数，一个是文件里出现次数），跨通道相加
会得到一个谁也解释不了的数。

确定性：输入相同 -> 输出逐字节相同（按 (bucket 顺序, h) 排序，不写 generatedAt）。
本表不是「送翻清单」：送翻仍由 translate_il 读 packs/capture-candidates.jsonl 那一份
（只含 candidate/pattern），两者分工不同。

CLI:
  python -m i18n_tools.candidates
  python -m i18n_tools.candidates --out packs/candidates.jsonl --report reports/candidates.md
  python -m i18n_tools.candidates --no-capture          # 只并静态四条通道
  python -m i18n_tools.candidates --selftest
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import re
import sys

from . import capture_ingest, ensure_stdout, fnv, harvest_cfgcache, skeleton, textnorm

SCHEMA = 1
OUT_NAME = "candidates.jsonl"
REPORT_REL = ("reports", "candidates.md")

# 字段顺序固定（D-T3）。加字段要改设计契约，不在这层自己长。
OUT_FIELDS = ("h", "s", "src", "bucket", "reason", "channel", "caller", "sink",
              "count", "mod", "firstSeen", "covered")
CHANNELS = ("cfg", "loc", "il", "cfgcache", "capture", "submit")
# submit = 玩家投稿行（api submit accept 直接写进本表，见 docs/10 §6）。它不是从
# 任何输入文件里派生的，只能从上一版本表里回读 —— 见 carry_rows。
SUBMIT_CHANNEL = "submit"
# 同一个 h 撞车时的取舍顺序：越靠前越优先（捕获端见过渲染出口，信息量最大）。
# submit 仅次于 capture：同样来自「游戏里真的渲染过」，但没有 sink/caller/count 细节；
# 它排在 il 之前，因为 IL 里的字符串是静态猜的，投稿行是玩家实测命中。
CHANNEL_PRIORITY = {"capture": 0, "submit": 1, "il": 2, "cfgcache": 3, "loc": 4, "cfg": 5}
TARGET_BUCKETS = ("candidate", "template", "never")
BUCKET_ORDER = capture_ingest.BUCKET_ORDER      # candidate 0 / pattern 1 / template 2 / never 3
IL_BUCKETS = {"translate": "candidate", "short_label": "candidate", "template": "template"}
# 纯本地化 key 引用（运行时由 Localizer 解析成别的文本，按 key 建字典命不中）。
# 判据与 harvest_cfgcache 同源：它的 _KEYREF_RE 只覆盖下划线式 #LOC_CRP_X_Title，而 cfg 里
# 真实还存在点分/连字符/斜杠式（#LOC.SXT_xxx / #LOC_Restock_variant-xxx /
# #Sol_Jupiter_name//Jupiter），这几种由下面的宽松式兜住 —— 两条判据都是「# 开头且不含空白」。
# 两条不是冗余：严格式管常规 key，宽松式专管带点/连字符/斜杠的变体，删掉任一条都会让 956 行漏判。
KEYREF_RE = harvest_cfgcache._KEYREF_RE
KEYREF_LOOSE_RE = re.compile(r"^#[^\s]+$")
KEY_REF_REASON = "keyref"


def _n(value):
    if value is None:
        return 0
    try:
        return int(value)
    except (TypeError, ValueError):
        return 0


def _mk(h, s, src, bucket, reason, channel, caller, sink, count, mod, first_seen):
    """按 OUT_FIELDS 顺序造一行（covered 先占位 False，后面统一打标）。"""
    if bucket not in TARGET_BUCKETS:
        bucket = "candidate"
    return {"h": h, "s": s, "src": src or s, "bucket": bucket,
            "reason": (reason or "none"), "channel": channel,
            "caller": caller or "", "sink": sink or "",
            "count": _n(count), "mod": mod or "", "firstSeen": first_seen or "",
            "covered": False}


def _is_keyref(text):
    """纯本地化 key 引用：下划线式走同源正则，点分/连字符/斜杠式走宽松式。"""
    text = text or ""
    return bool(KEYREF_RE.match(text) or KEYREF_LOOSE_RE.match(text))


def _has_words(text):
    """有没有可翻的字：至少一个 ASCII 字母或一个汉字（占位符/纯符号行没有）。"""
    for ch in text or "":
        if ch.isascii() and ch.isalpha():
            return True
        if "\u4e00" <= ch <= "\u9fff":
            return True
    return False


def _first_mod(mods):
    if isinstance(mods, list):
        for item in mods:
            if isinstance(item, str) and item:
                return item
        return ""
    return mods if isinstance(mods, str) else ""


def read_jsonl(path, stats=None):
    """逐行读 jsonl；坏行只记账不抛（输入来自别的工具，不能因为它挂掉）。"""
    if not path or not os.path.isfile(path):
        return
    with open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except ValueError:
                if stats is not None:
                    stats["bad_lines"] = stats.get("bad_lines", 0) + 1
                continue
            if isinstance(obj, dict):
                yield obj


def pool_rows(path, stats=None):
    """packs/pool.jsonl -> cfg / loc 两条通道（channel 取行里的 src）。

    pool 行只有 text（已归一化）没有 s/n，也没有 count/firstSeen；bucket 现算 ——
    静态抽取和运行时捕获必须用同一套分类规则（capture_ingest.classify_capture），
    否则同一句英文在两张表里会是两个桶。
    """
    rows = []
    for obj in read_jsonl(path, stats):
        text = obj.get("text")
        if not isinstance(text, str) or not text.strip():
            if stats is not None:
                stats["bad_rows"] = stats.get("bad_rows", 0) + 1
            continue
        s = textnorm.normalize(text)
        if not s:
            continue
        h = obj.get("h") or fnv.hash_text(s)
        bucket, reason = capture_ingest.classify_capture(s)
        channel = obj.get("src") if obj.get("src") in ("cfg", "loc") else "cfg"
        rows.append(_mk(h, s, text, bucket, reason, channel, "", "", 1,
                       obj.get("mod"), ""))
    return rows


def il_rows(path, stats=None):
    """packs/il-translatable.jsonl -> il 通道（translate/short_label 都是 candidate）。"""
    rows = []
    for obj in read_jsonl(path, stats):
        src = obj.get("text") if isinstance(obj.get("text"), str) else ""
        s = obj.get("n") if isinstance(obj.get("n"), str) and obj.get("n") else ""
        if not s:
            s = textnorm.normalize(src)
        if not s:
            continue
        h = obj.get("h") or fnv.hash_text(s)
        bucket = IL_BUCKETS.get(obj.get("bucket") or "", "candidate")
        rows.append(_mk(h, s, src or s, bucket, obj.get("reason"), "il",
                       "", obj.get("sink"), 1, _first_mod(obj.get("mods")), ""))
    return rows


def cfgcache_rows(path, stats=None):
    """packs/cfgcache-candidates.jsonl -> cfgcache 通道（配置缓存里的字段名/描述）。"""
    rows = []
    for obj in read_jsonl(path, stats):
        src = obj.get("s") if isinstance(obj.get("s"), str) else ""
        s = obj.get("n") if isinstance(obj.get("n"), str) and obj.get("n") else ""
        if not s:
            s = textnorm.normalize(src)
        if not s:
            continue
        h = obj.get("h") or fnv.hash_text(s)
        rows.append(_mk(h, s, src or s, obj.get("bucket") or "candidate",
                       obj.get("reason"), "cfgcache", obj.get("caller"),
                       obj.get("sink"), obj.get("count"),
                       _first_mod(obj.get("mods")), obj.get("firstSeen")))
    return rows


def capture_rows(files, packs_dir, gamedata="", managed="", do_attrib=False):
    """运行时捕获 -> capture 通道。

    取数口径是 capture_ingest 的真相层口径：buckets=ALL_INPUT_BUCKETS（never 也要）、
    raw_templates=False（不要 pattern 骨架，要 template 真身）、skip_covered=False
    （已覆盖的 h 也出行，covered 由本模块自己打标）。
    """
    if not files:
        return [], {}
    pool_path = os.path.join(packs_dir, "pool.jsonl")
    stats, got = capture_ingest.ingest(
        files, packs_dir, pool_path, None,
        list(capture_ingest.ALL_INPUT_BUCKETS), False,
        gamedata, managed, do_attrib, skip_covered=False)
    rows = []
    for obj in got:
        src = obj.get("s") if isinstance(obj.get("s"), str) else ""
        s = obj.get("n") if isinstance(obj.get("n"), str) and obj.get("n") else ""
        if not s:
            s = textnorm.normalize(src)
        if not s:
            continue
        h = obj.get("h") or fnv.hash_text(s)
        rows.append(_mk(h, s, src or s, obj.get("bucket") or "candidate",
                       obj.get("reason"), "capture", obj.get("caller"),
                       obj.get("sink"), obj.get("count"),
                       _first_mod(obj.get("mods")), obj.get("firstSeen")))
    info = {"files": len(files), "rows_in": stats.get("rows_in", 0),
            "distinct": stats.get("distinct", 0),
            "candidate": stats.get("candidate", 0),
            "template": stats.get("template", 0),
            "never": stats.get("never", 0),
            "truncated": stats.get("truncated", 0)}
    return rows, info


def dedup(rows):
    """同 h 只留优先级最高的那条（整行采用，count 不合并）。"""
    keep = {}
    for row in rows:
        h = row["h"]
        old = keep.get(h)
        if old is None:
            keep[h] = row
            continue
        new_p = CHANNEL_PRIORITY.get(row["channel"], 99)
        old_p = CHANNEL_PRIORITY.get(old["channel"], 99)
        if new_p < old_p:
            keep[h] = row
    return list(keep.values())


def carry_rows(path, stats=None):
    """上一版本表里的投稿行（channel=submit）原样带过来。

    投稿行只住在 packs/candidates.jsonl 里：五条通道的输入（pool / il-translatable /
    cfgcache-candidates / capture-*）都不含它们。所以只要不回读，重跑一次
    candidates 就会把玩家投稿整批抹掉 —— 这是 M3 落地前真实存在的静默丢数据事故。
    其他通道的旧行**故意不**带：它们由输入文件重新派生，带过来只会让删掉的串复活。
    """
    rows = []
    if not path or not os.path.isfile(path):
        return rows
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except ValueError:
                if stats is not None:
                    stats["bad_lines"] = stats.get("bad_lines", 0) + 1
                continue
            if obj.get("channel") != SUBMIT_CHANNEL:
                continue
            rows.append(_mk(obj.get("h"), obj.get("s"), obj.get("src"),
                            obj.get("bucket"), obj.get("reason"), SUBMIT_CHANNEL,
                            obj.get("caller"), obj.get("sink"), obj.get("count"),
                            obj.get("mod"), obj.get("firstSeen")))
    return rows


def sort_rows(rows):
    return sorted(rows, key=lambda r: (BUCKET_ORDER.get(r["bucket"], 9), r["h"]))


def load_pack_h(packs_dir):
    """packs/zh-cn/*.jsonl 里已有的 h 集合（covered 的真身）。"""
    pack_h, _pool_h = capture_ingest.load_covered(packs_dir, None)
    return pack_h


def collect(packs_dir=None, pool_path=None, il_path=None, cfgcache_path=None,
            capture_files=None, with_capture=True, gamedata="", managed="",
            do_attrib=False, carry_path=None):
    """五条通道 -> (rows, stats)。rows 已去重、已打 covered、已排序。"""
    packs_dir = packs_dir or capture_ingest.default_packs_dir()
    pool_path = pool_path or os.path.join(packs_dir, "pool.jsonl")
    il_path = il_path or os.path.join(packs_dir, "il-translatable.jsonl")
    cfgcache_path = cfgcache_path or os.path.join(packs_dir, "cfgcache-candidates.jsonl")
    stats = {"packs_dir": packs_dir, "inputs": [], "channels": {},
             "capture_info": {}, "bad_lines": 0, "bad_rows": 0}
    flat = []
    # pool 一次读两条通道：分开读会把同一个文件解析两遍（12,792 行 ×2 不值当）
    pool_now = pool_rows(pool_path, stats) if os.path.isfile(pool_path) else []
    if os.path.isfile(pool_path):
        stats["inputs"].append({"channel": "cfg/loc", "path": pool_path,
                                "bytes": os.path.getsize(pool_path),
                                "rows": len(pool_now)})
    for channel in ("cfg", "loc"):
        hits = [r for r in pool_now if r["channel"] == channel]
        stats["channels"][channel] = len(hits)
        flat.extend(hits)
    for channel, path, reader in (("il", il_path, il_rows),
                                  ("cfgcache", cfgcache_path, cfgcache_rows)):
        got = reader(path, stats) if os.path.isfile(path) else []
        if os.path.isfile(path):
            stats["inputs"].append({"channel": channel, "path": path,
                                    "bytes": os.path.getsize(path),
                                    "rows": len(got)})
        stats["channels"][channel] = len(got)
        flat.extend(got)
    if with_capture:
        files = list(capture_files or [])
        got, info = capture_rows(files, packs_dir, gamedata, managed, do_attrib)
        stats["capture_info"] = info
        stats["channels"]["capture"] = len(got)
        # 30 个 capture 文件各列一行没有信息量（每行的 rows 还得单读一遍），聚合成一行
        stats["inputs"].append({
            "channel": "capture",
            "path": "capture-*.jsonl × {}".format(len(files)),
            "bytes": sum(os.path.getsize(p) for p in files if os.path.isfile(p)),
            "rows": info.get("rows_in", 0),
        })
        flat.extend(got)
    else:
        stats["channels"]["capture"] = 0
    carried = carry_rows(carry_path, stats)
    stats["channels"][SUBMIT_CHANNEL] = len(carried)
    if carried:
        stats["inputs"].append({"channel": SUBMIT_CHANNEL, "path": carry_path,
                                "bytes": os.path.getsize(carry_path),
                                "rows": len(carried)})
    flat.extend(carried)
    stats["flat"] = len(flat)
    rows = dedup(flat)
    stats["deduped"] = len(rows)
    # 纯 key 引用不是正文：bucket 降到 never、reason 记 keyref（因此也从覆盖率分母里剔除）。
    # 运行时这些串会被 Localizer 解析成别的文本，按 key 建字典命不中，翻它没有意义。
    n_keyref = 0
    for row in rows:
        if _is_keyref(row["s"]):
            row["bucket"] = "never"
            row["reason"] = KEY_REF_REASON
            n_keyref += 1
    stats["keyref"] = n_keyref
    rows = sort_rows(rows)
    pack_h = load_pack_h(packs_dir)
    stats["pack_h"] = len(pack_h)
    for row in rows:
        row["covered"] = row["h"] in pack_h
    stats["out_rows"] = len(rows)
    stats["covered"] = sum(1 for r in rows if r["covered"])
    stats["missing"] = stats["out_rows"] - stats["covered"]
    denom = [r for r in rows if _translatable(r)]
    stats["placeholder"] = sum(1 for r in rows
                               if r["reason"] != KEY_REF_REASON and not _has_words(r["s"]))
    stats["denominator"] = len(denom)
    stats["denom_covered"] = sum(1 for r in denom if r["covered"])
    stats["denom_missing"] = len(denom) - stats["denom_covered"]
    return rows, stats


def row_as_json(row):
    return {key: row[key] for key in OUT_FIELDS}


def write_rows(path, rows):
    parent = os.path.dirname(os.path.abspath(path))
    if parent:
        os.makedirs(parent, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline=chr(10)) as handle:
        for row in rows:
            handle.write(json.dumps(row_as_json(row), ensure_ascii=False,
                                    separators=(",", ":")) + chr(10))
    return len(rows)


def _pct(part, whole):
    if not whole:
        return "0.00%"
    return "{:.2f}%".format(100.0 * part / whole)


def _cell(text):
    return str(text).replace("|", "\\|")


# ---------------------------------------------------------------------------
# 报告（reports/candidates.md）
# ---------------------------------------------------------------------------
def _bucket_of(rows, bucket):
    return [r for r in rows if r["bucket"] == bucket]


def _translatable(row):
    """这一行算不算进覆盖率分母：不是 keyref，且去掉数字/标点后还有可翻的字。"""
    return row["reason"] != KEY_REF_REASON and _has_words(row["s"])


def summary(rows):
    """所有报告数字都从这里现算，别在模板里写死。"""
    out = {"rows": len(rows), "covered": 0, "missing": 0,
           "channels": {}, "channel_bucket": {}, "caller": {}, "bucket": {}}
    for channel in CHANNELS:
        got = [r for r in rows if r["channel"] == channel]
        keep = [r for r in got if _translatable(r)]
        out["channels"][channel] = {
            "rows": len(got),
            "covered": sum(1 for r in got if r["covered"]),
            "missing": sum(1 for r in got if not r["covered"]),
            "missing_count": sum(r["count"] for r in got if not r["covered"]),
            "translatable": len(keep),
            "translatable_covered": sum(1 for r in keep if r["covered"]),
            "translatable_missing": sum(1 for r in keep if not r["covered"]),
        }
    for bucket in TARGET_BUCKETS:
        got = _bucket_of(rows, bucket)
        keep = [r for r in got if _translatable(r)]
        out["bucket"][bucket] = {
            "rows": len(got),
            "covered": sum(1 for r in got if r["covered"]),
            "missing": sum(1 for r in got if not r["covered"]),
            "translatable": len(keep),
            "translatable_covered": sum(1 for r in keep if r["covered"]),
            "translatable_missing": sum(1 for r in keep if not r["covered"]),
        }
    for channel in CHANNELS:
        for bucket in TARGET_BUCKETS:
            got = [r for r in rows if r["channel"] == channel and r["bucket"] == bucket]
            if not got:
                continue
            keep = [r for r in got if _translatable(r)]
            out["channel_bucket"][(channel, bucket)] = {
                "rows": len(got),
                "covered": sum(1 for r in got if r["covered"]),
                "missing": sum(1 for r in got if not r["covered"]),
                "missing_count": sum(r["count"] for r in got if not r["covered"]),
                "translatable_missing": sum(1 for r in keep if not r["covered"]),
                "translatable_missing_count": sum(r["count"] for r in keep if not r["covered"]),
            }
    for row in rows:
        if row["covered"] or not _translatable(row):
            continue
        # 静态通道没有 caller（没经过渲染出口），退回按 mod 归堆，否则整张 top 表就一行
        key = row["caller"] or ("mod:" + row["mod"] if row["mod"] else "（无 caller）")
        slot = out["caller"].setdefault(key, {"rows": 0, "count": 0,
                                              "channels": set()})
        slot["rows"] += 1
        slot["count"] += row["count"]
        slot["channels"].add(row["channel"])
    # template 档若按骨架塌缩，还剩多少个「真正不同的形状」（与出包口径同一个 skeleton）
    sk = {"rows": 0, "skeletons": 0, "skeleton_rows": 0, "no_skeleton": 0}
    shapes = {}
    for row in rows:
        if row["bucket"] != "template" or row["covered"] or not _translatable(row):
            continue
        sk["rows"] += 1
        got = skeleton.skeleton(textnorm.normalize(row["s"]))[0]
        if not got:
            sk["no_skeleton"] += 1
            continue
        shapes[got] = shapes.get(got, 0) + 1
    sk["skeletons"] = len(shapes)
    sk["skeleton_rows"] = sum(shapes.values())
    out["template_skeletons"] = sk
    out["covered"] = sum(1 for r in rows if r["covered"])
    out["missing"] = out["rows"] - out["covered"]
    out["missing_count"] = sum(r["count"] for r in rows if not r["covered"])
    # 三个互斥类：keyref / 占位符纯符号 / 可翻（分母）。分母 = 行数 - keyref - 占位符。
    keyref = [r for r in rows if r["reason"] == KEY_REF_REASON]
    placeholder = [r for r in rows if r["reason"] != KEY_REF_REASON and not _has_words(r["s"])]
    denom = [r for r in rows if _translatable(r)]
    out["keyref"] = {"rows": len(keyref),
                     "covered": sum(1 for r in keyref if r["covered"])}
    out["placeholder"] = {"rows": len(placeholder),
                          "covered": sum(1 for r in placeholder if r["covered"])}
    out["denominator"] = {
        "rows": len(denom),
        "covered": sum(1 for r in denom if r["covered"]),
        "missing": sum(1 for r in denom if not r["covered"]),
        "missing_count": sum(r["count"] for r in denom if not r["covered"]),
    }
    # candidate 档未覆盖 = 下一批派活的清单：给 channel 与 mod 分布（两组行数各自可加）
    cand_unc = [r for r in rows if r["bucket"] == "candidate"
                and not r["covered"] and _translatable(r)]
    cu_channels = {}
    for channel in CHANNELS:
        got = [r for r in cand_unc if r["channel"] == channel]
        if not got:
            continue
        cu_channels[channel] = {"rows": len(got),
                                "count": sum(r["count"] for r in got)}
    cu_mods = {}
    for row in cand_unc:
        key = row["mod"] or "（无 mod）"
        slot = cu_mods.setdefault(key, {"rows": 0, "count": 0, "channels": set()})
        slot["rows"] += 1
        slot["count"] += row["count"]
        slot["channels"].add(row["channel"])
    out["candidate_uncovered"] = {"rows": len(cand_unc),
                                  "count": sum(r["count"] for r in cand_unc),
                                  "channels": cu_channels, "mods": cu_mods}
    return out


def render_report(rows, stats, out_path="", report_path="", argv_note="", top=20):
    """reports/candidates.md。数字全部现算，不引用任何人手填的百分比。

    只有一个分母：「可翻行数」= 表里行数 - 纯 key 引用 - 占位符/纯符号行。下文所有百分比
    都按它算。上一版三节报告各算各的分母、互相不可加，就是没有统一下场的后果，本版不再留。
    """
    s = summary(rows)
    den = s["denominator"]
    lines = []
    add = lines.append
    add("# 候选单一表（packs/candidates.jsonl）")
    add("")
    add("> 由 tools/i18n_tools/candidates.py 生成；本文件不要手改，重跑即覆盖。")
    add("")
    add("## 1. 一句话")
    add("")
    add("全表 **{:,}** 行（五条通道按 h 去重后，每条 h 恰好一行）。扣掉纯 key 引用 {:,} 行与"
        "占位符/纯符号 {:,} 行，**可翻行数（本报告唯一分母）= {:,}**；其中已覆盖 **{:,}** 条"
        "（**{}**），仍未覆盖 **{:,}** 条（渲染次数之和 {:,}）。".format(
            s["rows"], s["keyref"]["rows"], s["placeholder"]["rows"], den["rows"],
            den["covered"], _pct(den["covered"], den["rows"]), den["missing"],
            den.get("missing_count", 0)))
    add("")
    add("这张表**不是**送翻清单：送翻仍读 packs/capture-candidates.jsonl（只含 candidate/pattern）；"
        "本表给「还剩多少英文」一个统一分母，并让 never 档留档可查。")
    add("")
    if out_path:
        add("- 产物：" + chr(96) + _cell(out_path) + chr(96))
    if report_path:
        add("- 报告：" + chr(96) + _cell(report_path) + chr(96))
    if argv_note:
        add("- 生成命令：" + chr(96) + _cell(argv_note) + chr(96))
    add("")
    add("## 2. 各通道的输入与去重键（五条派生 + 一条回读）")
    add("")
    add("| 通道 | 读入行数 | 自己的 distinct h | 去重键 | 进表行数 | 被更高优先级通道抢走 |")
    add("| --- | ---: | ---: | --- | ---: | ---: |")
    keys = {
        "cfg": "h = fnv1a64(normalize(text))",
        "loc": "h = fnv1a64(normalize(text))",
        "il": "h（il-translatable.jsonl 的 h，基于归一化原文）",
        "cfgcache": "h（cfgcache-candidates.jsonl 的 h）",
        "capture": "h（采集器算的，离线按 normalize(s) 复核，不一致 0 例）",
        "submit": "h（api submit accept 写进来的；从上一版本表回读，不是派生）",
    }
    for channel in CHANNELS:
        if channel == "capture":
            raw = stats.get("capture_info", {}).get("rows_in", 0)
        else:
            raw = stats.get("channels", {}).get(channel, 0)
        distinct = stats.get("channels", {}).get(channel, 0)
        kept = s["channels"][channel]["rows"]
        add("| {} | {:,} | {:,} | {} | {:,} | {:,} |".format(
            channel, raw, distinct, keys[channel], kept, max(distinct - kept, 0)))
    add("")
    add("去重键统一是 h = fnv1a64(normalize(原文))（tools/i18n_tools/fnv.py + textnorm.py），"
        "静态通道的 h 由抽取侧写好、本表复核，capture 通道的 h 是采集器算好后离线再算一遍对比。")
    add("")
    add("去重规则：同一个 h 撞多通道时按 capture > submit > il > cfgcache > loc > cfg 取优先级最高的"
        "**一整行**，count 不合并 —— 跨通道相加没有意义（一个是运行时渲染次数，一个是文件里"
        "出现次数）。")
    add("")
    add("## 3. 分母构成（下文所有百分比都按它算）")
    add("")
    add("| 类别 | 行数 | 已覆盖 | 说明 |")
    add("| --- | ---: | ---: | --- |")
    bt = chr(96)
    keyref_note = ("`#` 开头且不含空白（`#LOC_xxx` / `#LOC.SXT_xxx` / `#Sol_Jupiter_name//Jupiter`）："
                   "运行时由 Localizer 解析成别的文本，按 key 建字典命不中；"
                   "已降为 bucket=never、reason=keyref")
    placeholder_note = ("去掉数字与标点后既没有 ASCII 字母也没有汉字（`###`、`[%s]`、`**` 之类）："
                        "没有可翻的文字")
    add("| 纯 key 引用 | {:,} | {:,} | {} |".format(
        s["keyref"]["rows"], s["keyref"]["covered"], keyref_note))
    add("| 占位符/纯符号 | {:,} | {:,} | {} |".format(
        s["placeholder"]["rows"], s["placeholder"]["covered"], placeholder_note))
    add("| **可翻行数（分母）** | **{:,}** | **{:,}** | 其余全部；第 1、4、5、6、7 节的"
        "百分比与「还剩多少」都按这一行算 |".format(den["rows"], den["covered"]))
    add("")
    add("- 可翻行数 = 表行数 {:,} - keyref {:,} - 占位符 {:,} = **{:,}**".format(
        s["rows"], s["keyref"]["rows"], s["placeholder"]["rows"], den["rows"]))
    add("- 可翻里已覆盖 **{:,}** 条（{}），未覆盖 **{:,}** 条".format(
        den["covered"], _pct(den["covered"], den["rows"]), den["missing"]))
    add("")
    add("| bucket | 行数 | 其中可翻 | 可翻里已覆盖 | 可翻里未覆盖 |")
    add("| --- | ---: | ---: | ---: | ---: |")
    for bucket in TARGET_BUCKETS:
        slot = s["bucket"][bucket]
        add("| {} | {:,} | {:,} | {:,} | {:,} |".format(
            bucket, slot["rows"], slot["translatable"], slot["covered"],
            slot["translatable_missing"]))
    add("| **合计** | **{:,}** | **{:,}** | **{:,}** | **{:,}** |".format(
        s["rows"], den["rows"], den["covered"], den["missing"]))
    add("")
    add("## 4. channel × bucket")
    add("")
    add("| channel | bucket | 行数 | 已覆盖 | 未覆盖 | 未覆盖的 count 之和 |")
    add("| --- | --- | ---: | ---: | ---: | ---: |")
    for channel in CHANNELS:
        for bucket in TARGET_BUCKETS:
            slot = s["channel_bucket"].get((channel, bucket))
            if not slot:
                continue
            add("| {} | {} | {:,} | {:,} | {:,} | {:,} |".format(
                channel, bucket, slot["rows"], slot["covered"], slot["missing"],
                slot["missing_count"]))
    add("| **合计** | | **{:,}** | **{:,}** | **{:,}** | **{:,}** |".format(
        s["rows"], s["covered"], s["missing"], s.get("missing_count", 0)))
    add("")
    add("本表是**行数盘点**（含 keyref / 占位符行，所以合计与第 3 节的分母不同）；"
        "覆盖率请只看第 5 节。")
    add("")
    add("## 5. covered（按可翻分母算的覆盖率）")
    add("")
    add("| channel | 行数 | 可翻 | 可翻里已覆盖 | 可翻里未覆盖 | 覆盖率 |")
    add("| --- | ---: | ---: | ---: | ---: | ---: |")
    for channel in CHANNELS:
        slot = s["channels"][channel]
        add("| {} | {:,} | {:,} | {:,} | {:,} | {} |".format(
            channel, slot["rows"], slot["translatable"], slot["translatable_covered"],
            slot["translatable_missing"], _pct(slot["translatable_covered"],
                                               slot["translatable"])))
    add("| **合计** | **{:,}** | **{:,}** | **{:,}** | **{:,}** | **{}** |".format(
        s["rows"], den["rows"], den["covered"], den["missing"],
        _pct(den["covered"], den["rows"])))
    add("")
    add("## 6. 仍未覆盖的 top {} caller".format(top))
    add("")
    add("静态通道（cfg/loc/il/cfgcache）没有 caller —— 它们没经过渲染出口、记不出调用栈 ——"
        "按 " + chr(96) + "mod:<modId>" + chr(96) + " 归堆；capture 通道的是真实 Type.Method。")
    add("")
    ranked = sorted(s["caller"].items(),
                    key=lambda kv: (-kv[1]["count"], -kv[1]["rows"], kv[0]))
    if ranked:
        add("| caller | 通道 | 行数 | count 之和 |")
        add("| --- | --- | ---: | ---: |")
        for caller, slot in ranked[:top]:
            add("| {} | {} | {:,} | {:,} |".format(
                _cell(caller), _cell("/".join(sorted(slot["channels"]))),
                slot["rows"], slot["count"]))
    else:
        add("（没有未覆盖的行）")
    add("")
    add("## 7. 可翻行里仍未覆盖多少")
    add("")
    add("- 可翻行数 **{:,}**，已覆盖 {:,}，未覆盖 **{:,}** 条（这条是第 1 节的分母链，"
        "与第 3 节完全一致）".format(den["rows"], den["covered"], den["missing"]))
    add("- 未覆盖行的 count 之和：**{:,}**（运行时渲染次数；静态通道每条记 1）".format(
        den.get("missing_count", 0)))
    for bucket in TARGET_BUCKETS:
        slot = s["bucket"][bucket]
        add("- bucket={}：可翻 {:,} 条，其中未覆盖 {:,} 条（共 {:,} 条，含 {:,} 行不计入分母）"
            .format(bucket, slot["translatable"], slot["translatable_missing"], slot["rows"],
                    slot["rows"] - slot["translatable"]))
    sk = s.get("template_skeletons") or {}
    if sk.get("rows"):
        add("")
        add("template 档那 {:,} 条未覆盖行按骨架塌缩（tools/i18n_tools/skeleton.py，和出包用同一个"
            "函数）：**{:,} 个骨架**，覆盖 {:,} 条；另 {:,} 条塌不出骨架"
            "（数字段 0 个或超过 8 个），出包时会被降级成 candidate 原样送翻。".format(
                sk["rows"], sk["skeletons"], sk["skeleton_rows"], sk["no_skeleton"]))
        add("")
        cand_unc = s["bucket"]["candidate"]["translatable_missing"]
        units = cand_unc + sk["skeletons"] + sk["no_skeleton"]
        add("所以「还剩多少英文」不能按 {:,} 条算：真正可翻的 distinct 单位是 candidate 未覆盖 "
            "{:,} 条 + template 骨架 {:,} 个 + 塌不出骨架的 {:,} 条 = **{:,}** 个单位；"
            "never {:,} 条留档不送翻。".format(
                den["missing"], cand_unc, sk["skeletons"], sk["no_skeleton"], units,
                s["bucket"]["never"]["translatable_missing"]))
    add("")
    cu = s["candidate_uncovered"]
    add("## 8. candidate 档未覆盖的 {:,} 条（下一批翻译的活）".format(cu["rows"]))
    add("")
    add("这 {:,} 条是**已经判成该翻、但 packs/zh-cn 里还没有对应译文**的行（bucket=candidate、"
        "未覆盖、且计入第 3 节的分母）；未覆盖的 count 之和 {:,}。template 档的 {:,} 条要先塌成骨架"
        "（第 7 节）再派，never 留档不送翻 —— 派活只派这一节。".format(
            cu["rows"], cu["count"], s["bucket"]["template"]["translatable_missing"]))
    add("")
    add("| channel | 未覆盖行数 | count 之和 |")
    add("| --- | ---: | ---: |")
    for channel in CHANNELS:
        slot = cu["channels"].get(channel)
        if not slot:
            continue
        add("| {} | {:,} | {:,} |".format(channel, slot["rows"], slot["count"]))
    add("| **合计** | **{:,}** | **{:,}** |".format(cu["rows"], cu["count"]))
    add("")
    cu_top = sorted(cu["mods"].items(), key=lambda kv: (-kv[1]["rows"], -kv[1]["count"], kv[0]))
    add("按 mod 分布（前 {} 个，按未覆盖行数降序；整表 {:,} 个 mod 有未覆盖 candidate 行）：".format(
        min(top, len(cu_top)), len(cu_top)))
    add("")
    add("| mod | 未覆盖行数 | count 之和 | 来源 channel |")
    add("| --- | ---: | ---: | --- |")
    for mod, slot in cu_top[:top]:
        add("| {} | {:,} | {:,} | {} |".format(
            _cell(mod), slot["rows"], slot["count"], "/".join(sorted(slot["channels"]))))
    if len(cu_top) > top:
        add("| （其余 {:,} 个 mod 略） | | | |".format(len(cu_top) - top))
    add("")
    add("同一行只记一个 mod（多归属取 mods[0]，完整归属见 reports/capture-triage.md）；"
        "这张表和第 9 节的榜是同一个候选池，只是这里按 mod 切、那里按 channel×bucket 切。")
    add("")
    add("## 9. 最该先翻的 3 个 channel×bucket")
    add("")
    # never 不送翻（留档），不进这张榜 —— 否则它会挤掉真正该翻的 template
    combos = sorted(((k, v) for k, v in s["channel_bucket"].items()
                     if v["translatable_missing"] and k[1] != "never"),
                    key=lambda kv: (-kv[1]["translatable_missing_count"],
                                    -kv[1]["translatable_missing"], kv[0]))
    for rank, (key, slot) in enumerate(combos[:3], 1):
        add("{}. **{} × {}**：可翻里未覆盖 {:,} 条 / count 之和 {:,}".format(
            rank, key[0], key[1], slot["translatable_missing"],
            slot["translatable_missing_count"]))
    if not combos:
        add("（没有未覆盖的组合）")
    add("")
    add("排序依据是「未覆盖行的 count 之和」而不是行数：count 大的串在游戏里真的被反复渲染，"
        "翻一条顶一片；静态通道 count 恒为 1，所以同分时按行数看。never 档不进这张榜"
        "（它留档不送翻），只在第 3、4、7 节露面。")
    add("")
    add("## 10. 口径与已知问题")
    add("")
    add("- **只有一个分母**：可翻行数 = 行数 - keyref - 占位符/纯符号（见第 3 节）；"
        "别拿全表行数去算覆盖率。")
    add("- **keyref 的口径与 harvest_cfgcache 同源**：" + chr(96) + "harvest_cfgcache._KEYREF_RE" +
        chr(96) + "（" + chr(96) + "^#[A-Za-z0-9_]+$" + chr(96) + "）覆盖下划线式；cfg 里真实还存在"
        "点分/连字符/斜杠式（" + chr(96) + "#LOC.SXT_xxx" + chr(96) + "、" + chr(96) +
        "#LOC_Restock_variant-xxx" + chr(96) + "、" + chr(96) + "#Sol_Jupiter_name//Jupiter" +
        chr(96) + "），这几种由本模块的宽松式 " + chr(96) + "^#[^\\s]+$" + chr(96) +
        " 兜住，判据都是「# 开头且不含空白」。")
    add("- **bucket 是优先级不是去留**：never（日志行/路径/中文/纯符号/标识符/keyref…）也留档，"
        "只是排在最后；送翻清单仍然只取 candidate/pattern。")
    add("- **pattern 不在本表**：捕获端的骨架行是 template 的塌缩视图，本表留 template 真身，"
        "骨架清单见 reports/capture-patterns.md。")
    add("- **静态通道没有频次**：pool / il 的 count 记 1，别把它当渲染次数用。")
    add("- **pool 行只有归一化文本**：packs/pool.jsonl 的 text 已归一化（实测 12,792/12,792 "
        "满足 h == fnv1a64(normalize(text))），所以 pool 的 s 与 src 往往相同。")
    add("- **capture 通道的 mods 来自 dll 反查**：反查不出统一记 UI18n-Unattributed；"
        "本表只取 mods[0]，多归属的行在 reports/capture-triage.md 里有完整列表。")
    add("- **truncated 不在本表**：被 MaxTextLength 截断的 h 在捕获产物行尾带 truncated:true，"
        "本表 schema 严格按设计契约 D-T3，不掺这个字段。")
    add("- **covered 是生成时刻的快照**：packs/zh-cn 会随翻译继续增长，本表只有重跑才更新；"
        "想知道现在的数就再跑一次，别引用上次报告里的百分比。")
    add("- **这不是渲染覆盖率**：本表分母是「机器看得见的串」，真正的渲染覆盖率由运行时写 "
        "PluginData/UI18n/render-<stamp>.json 与日志 [UI18n] render coverage 一行给出（D-T4）。")
    add("")
    return chr(10).join(lines)
def write_report(path, text):
    parent = os.path.dirname(os.path.abspath(path))
    if parent:
        os.makedirs(parent, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline=chr(10)) as handle:
        handle.write(text)
    return len(text)


# ---------------------------------------------------------------------------
# 自检
# ---------------------------------------------------------------------------
def _write_jsonl(path, rows):
    parent = os.path.dirname(os.path.abspath(path))
    if parent:
        os.makedirs(parent, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline=chr(10)) as handle:
        for row in rows:
            handle.write(json.dumps(row, ensure_ascii=False,
                                    separators=(",", ":")) + chr(10))


def selftest():
    import shutil
    import tempfile

    checks = []

    def a(name, ok, detail=""):
        checks.append((name, bool(ok), detail))

    def eq(name, got, want):
        a(name, got == want, "got={!r} want={!r}".format(got, want))

    base = tempfile.mkdtemp(prefix="ui18n_candidates_selftest_")
    try:
        packs = os.path.join(base, "packs")
        caps = os.path.join(base, "caps")
        os.makedirs(os.path.join(packs, "zh-cn"))
        os.makedirs(caps)
        h = lambda text: fnv.hash_text(textnorm.normalize(text))  # noqa: E731
        _write_jsonl(os.path.join(packs, "pool.jsonl"), [
            {"id": 1, "h": h("Pool text"), "text": "Pool text", "src": "cfg",
             "mod": "AJE", "field": "title", "ph": False, "rt": False,
             "dg": True, "path": "", "url": "", "len": 9},
            {"id": 2, "h": h("Loc text"), "text": "Loc text", "src": "loc",
             "mod": "KSP", "field": "title", "ph": False, "rt": False,
             "dg": False, "path": "", "url": "", "len": 8},
            {"id": 3, "h": h("正在装载"), "text": "正在装载", "src": "cfg",
             "mod": "AJE", "field": "title", "ph": False, "rt": False,
             "dg": True, "path": "", "url": "", "len": 4},
            {"id": 4, "h": h("Shared text"), "text": "Shared text", "src": "cfg",
             "mod": "AJE", "field": "title", "ph": False, "rt": False,
             "dg": True, "path": "", "url": "", "len": 11},
            {"id": 5, "h": h("#LOC_AJE_title"), "text": "#LOC_AJE_title",
             "src": "cfg", "mod": "AJE", "field": "title", "ph": False,
             "rt": False, "dg": True, "path": "", "url": "", "len": 13},
        ])
        _write_jsonl(os.path.join(packs, "il-translatable.jsonl"), [
            {"h": h("Ignition sequence"), "n": "Ignition sequence",
             "text": "Ignition sequence ", "asm": "RealFuels.dll",
             "type": "RealFuels.EngineConfig", "method": "GetInfo",
             "ilOffset": 12, "sink": "GUID", "bucket": "translate",
             "reason": "sentence", "mods": ["RealFuels"]},
            {"h": h("Count: 3"), "n": "Count: 3", "text": "Count: 3",
             "asm": "X.dll", "type": "X.Y", "method": "M", "ilOffset": 3,
             "sink": "LABEL", "bucket": "template",
             "reason": "template_format", "mods": ["RealFuels"]},
        ])
        _write_jsonl(os.path.join(packs, "cfgcache-candidates.jsonl"), [
            {"h": h("Config cache title"), "n": "Config cache title",
             "s": "Config cache title", "sink": "configcache",
             "caller": "ConfigCache:title", "count": 7,
             "firstSeen": "2026-10-20T00:00:00Z", "lastSeen": "", "sessions": 0,
             "callers": [], "bucket": "candidate", "reason": "",
             "mods": ["UI18n-ConfigCache"], "field": "title", "fields": ["title"]},
        ])
        _write_jsonl(os.path.join(caps, "capture-x.jsonl"), [
            {"h": h("Capture only"), "n": "Capture only", "s": "Capture only",
             "sink": "GUI.Label", "caller": "AJE.Y.z", "count": 4,
             "firstSeen": "2026-10-23T06:00:00Z", "bucket": "never",
             "reason": "punct"},
            {"h": h("Shared text"), "n": "Shared text", "s": "Shared text",
             "sink": "GUILayout.DoLabel", "caller": "AJE.Q.w", "count": 9,
             "firstSeen": "2026-10-23T07:00:00Z"},
        ])
        _write_jsonl(os.path.join(packs, "zh-cn", "AJE.jsonl"), [
            {"h": h("Pool text"), "s": "池文本", "t": "池文本", "mod": "AJE"},
        ])
        cap_file = os.path.join(caps, "capture-x.jsonl")
        rows, stats = collect(packs_dir=packs, capture_files=[cap_file],
                              do_attrib=False)
        by_s = {r["s"]: r for r in rows}

        eq("字段顺序固定 = 设计契约 D-T3", list(rows[0]), list(OUT_FIELDS))
        a("每条 distinct h 恰好一行", len({r["h"] for r in rows}) == len(rows),
          "rows=%d distinct=%d" % (len(rows), len({r["h"] for r in rows})))
        eq("排序 = (bucket 序, h)",
           [(BUCKET_ORDER[r["bucket"]], r["h"]) for r in rows],
           sorted((BUCKET_ORDER[r["bucket"]], r["h"]) for r in rows))
        a("bucket 只出现 candidate/template/never（pattern 不进本表）",
          sorted({r["bucket"] for r in rows}) == ["candidate", "never", "template"],
          "got=%r" % (sorted({r["bucket"] for r in rows}),))
        a("il: translate -> candidate、template -> template",
          by_s.get("Ignition sequence", {}).get("bucket") == "candidate"
          and by_s.get("Count: 3", {}).get("bucket") == "template",
          "got=%r/%r" % (by_s.get("Ignition sequence", {}).get("bucket"),
                         by_s.get("Count: 3", {}).get("bucket")))
        a("静态通道也走 capture 同一套分类（中文行 -> never/already-cjk）",
          (by_s.get("正在装载") or {}).get("bucket") == "never"
          and (by_s.get("正在装载") or {}).get("reason") == "already-cjk",
          "got=%r/%r" % ((by_s.get("正在装载") or {}).get("bucket"),
                         (by_s.get("正在装载") or {}).get("reason")))
        eq("reason 空串统一写成 none",
           (by_s.get("Config cache title") or {}).get("reason"), "none")
        a("covered 按 packs/zh-cn 的 h 集合打标",
          (by_s.get("Pool text") or {}).get("covered") is True
          and (by_s.get("Loc text") or {}).get("covered") is False
          and stats["covered"] == 1,
          "covered=%r" % ([k for k, v in by_s.items() if v["covered"]],))
        a("同 h 撞多通道时 capture 优先（整行采用、count 不合并）",
          (by_s.get("Shared text") or {}).get("channel") == "capture"
          and (by_s.get("Shared text") or {}).get("count") == 9
          and stats["flat"] == 10 and len(rows) == 9,
          "channel=%r count=%r flat=%d rows=%d" % (
              (by_s.get("Shared text") or {}).get("channel"),
              (by_s.get("Shared text") or {}).get("count"),
              stats["flat"], len(rows)))
        a("动态通道保留源行 count（cfgcache 7），静态通道记 1",
          (by_s.get("Config cache title") or {}).get("count") == 7
          and (by_s.get("Pool text") or {}).get("count") == 1)
        a("mod 取 mods[0]（il/cfgcache）与 pool 的 mod",
          (by_s.get("Ignition sequence") or {}).get("mod") == "RealFuels"
          and (by_s.get("Config cache title") or {}).get("mod") == "UI18n-ConfigCache"
          and (by_s.get("Loc text") or {}).get("mod") == "KSP")
        out_a = os.path.join(base, "candidates-a.jsonl")
        out_b = os.path.join(base, "candidates-b.jsonl")
        write_rows(out_a, rows)
        write_rows(out_b, collect(packs_dir=packs, capture_files=[cap_file],
                                  do_attrib=False)[0])
        with open(out_a, "rb") as handle:
            raw_a = handle.read()
        with open(out_b, "rb") as handle:
            raw_b = handle.read()
        a("连跑两次逐字节相同（确定性）", raw_a == raw_b,
          "bytes=%d/%d" % (len(raw_a), len(raw_b)))
        a("落盘 UTF-8 无 BOM、LF 结尾、末尾有换行",
          not raw_a.startswith(b"\xef\xbb\xbf") and chr(13).encode("utf-8") not in raw_a
          and raw_a.endswith(chr(10).encode("utf-8")) and raw_a.count(chr(10).encode("utf-8")) == len(rows))
        first = json.loads(raw_a.decode("utf-8").splitlines()[0])
        eq("落盘行字段顺序也是 OUT_FIELDS", list(first), list(OUT_FIELDS))

        rows2, _stats2 = collect(packs_dir=packs, with_capture=False)
        by2 = {r["s"]: r for r in rows2}
        a("--no-capture: 捕获行消失、撞车的那条退回 cfg 通道",
          "Capture only" not in by2
          and (by2.get("Shared text") or {}).get("channel") == "cfg",
          "got=%r/%r" % ("Capture only" in by2,
                         (by2.get("Shared text") or {}).get("channel")))

        text = render_report(rows, stats, out_a, os.path.join(base, "candidates.md"))
        marks = ("## 1. 一句话", "## 2. 各通道的输入与去重键（五条派生 + 一条回读）",
                 "## 3. 分母构成", "## 4. channel × bucket", "## 5. covered",
                 "## 6. 仍未覆盖的 top", "## 7. 可翻行里仍未覆盖多少",
                 "## 8. candidate 档未覆盖的", "## 9. 最该先翻的 3 个",
                 "## 10. 口径与已知问题")
        # 投稿行（submit）不是派生通道，报告第 2 节要如实说「一条回读」。
        a("报告: 第 2 节明确区分派生通道与回读通道",
          "一条回读" in text and "| submit |" in text,
          "got=%r" % ([ln for ln in text.splitlines() if ln.startswith("## 2.")],))
        a("报告: 含全部 10 个小节", all(m in text for m in marks),
          "missing=%r" % [m for m in marks if m not in text])
        s = summary(rows)
        den = s["denominator"]
        a("报告: 数字现算（合计行、未覆盖数、覆盖率都在）",
          "{:,}".format(s["rows"]) in text
          and "{:,}".format(den["missing"]) in text
          and _pct(den["covered"], den["rows"]) in text
          and den["missing"] == sum(1 for r in rows if not r["covered"] and _translatable(r))
          and den["missing_count"] == sum(r["count"] for r in rows
                                          if not r["covered"] and _translatable(r)),
          "got rows=%d den=%r" % (s["rows"], den))
        a("分母 = 行数 - keyref - 占位符，且 covered 可加",
          den["rows"] == s["rows"] - s["keyref"]["rows"] - s["placeholder"]["rows"]
          and den["covered"] == (s["covered"] - s["keyref"]["covered"]
                                 - s["placeholder"]["covered"]),
          "got den=%r keyref=%r placeholder=%r covered=%d" % (
              den, s["keyref"], s["placeholder"], s["covered"]))
        a("报告: 分母构成三行都在（keyref / 占位符 / 可翻分母）",
          "纯 key 引用" in text and "占位符/纯符号" in text and "可翻行数（分母）" in text)
        a("纯 key 引用 -> bucket=never + reason=keyref（不进覆盖率分母）",
          (by_s.get("#LOC_AJE_title") or {}).get("bucket") == "never"
          and (by_s.get("#LOC_AJE_title") or {}).get("reason") == "keyref"
          and s["keyref"]["rows"] == 1 and not _translatable(by_s.get("#LOC_AJE_title") or {}),
          "got=%r keyref=%r" % ((by_s.get("#LOC_AJE_title") or {}), s["keyref"]))
        a("报告: 明写 bucket 是优先级不是去留、pattern 不在本表",
          "bucket 是优先级不是去留" in text and "pattern 不在本表" in text)
        caps_in = [i for i in stats["inputs"] if i["channel"] == "capture"]
        a("报告: 捕获输入聚合成一行（不逐文件铺开）",
          len(caps_in) == 1 and caps_in[0]["rows"] == stats["capture_info"]["rows_in"]
          and "× 1" in caps_in[0]["path"],
          "got=%r" % (caps_in,))
        s7 = text.split("## 7.")[1].split("## 8.")[0]
        a("报告: 第 7 节不列 never（never 留档不送翻）", "× never" not in s7,
          "got=%r" % (s7.splitlines()[:6],))
        tk = s.get("template_skeletons") or {}
        n_tmpl_missing = sum(1 for r in rows
                             if r["bucket"] == "template" and not r["covered"]
                             and _translatable(r))
        a("报告: template 未覆盖行按骨架塌缩、给出可翻单位数",
          tk.get("rows") == n_tmpl_missing and "个骨架" in text
          and tk.get("skeletons", 0) + tk.get("no_skeleton", 0) > 0,
          "got=%r" % (tk,))
        cu = s["candidate_uncovered"]
        s8 = text.split("## 8.")[1].split("## 9.")[0]
        a("报告: candidate 未覆盖单列一节，channel/mod 分布与合计对得上",
          cu["rows"] == s["bucket"]["candidate"]["translatable_missing"]
          and sum(v["rows"] for v in cu["channels"].values()) == cu["rows"]
          and sum(v["rows"] for v in cu["mods"].values()) == cu["rows"]
          and "{:,}".format(cu["rows"]) in s8
          and "| **合计** | **{:,}** |".format(cu["rows"]) in s8,
          "got cu=%d/%r" % (cu["rows"], cu["channels"]))
        empty, _ = collect(packs_dir=os.path.join(base, "nope"),
                           pool_path=os.path.join(base, "no-pool.jsonl"),
                           il_path=os.path.join(base, "no-il.jsonl"),
                           cfgcache_path=os.path.join(base, "no-cc.jsonl"),
                           with_capture=False, do_attrib=False)
        eq("输入文件全缺时不抛、出 0 行", empty, [])

        # ---- 投稿行回读（M3）----
        # 投稿行只住在本表里，五条通道的输入都不含它们；不回读 = 重跑一次就整批抹掉。
        eq("submit 在 CHANNELS 里（否则统计与报告漏一列）",
           SUBMIT_CHANNEL in CHANNELS, True)
        order = [CHANNEL_PRIORITY[c] for c in
                 ("capture", SUBMIT_CHANNEL, "il", "cfgcache", "loc", "cfg")]
        eq("优先级：capture < submit < il < cfgcache < loc < cfg", order, sorted(order))
        submit_h = h("Voxels Controllable Vessel: ")
        prev = os.path.join(base, "prev-candidates.jsonl")
        _write_jsonl(prev, [
            {"h": submit_h, "s": "Voxels Controllable Vessel: ",
             "src": "submission/d503c37fc9f3a6e4", "bucket": "candidate",
             "reason": "none", "channel": SUBMIT_CHANNEL, "caller": "", "sink": "",
             "count": 1, "mod": "FerramAerospaceResearch",
             "firstSeen": "2026-10-26T00:00:00Z", "covered": False},
            {"h": h("stale cfg row"), "s": "stale cfg row", "src": "cfg",
             "bucket": "candidate", "reason": "none", "channel": "cfg", "caller": "",
             "sink": "", "count": 1, "mod": "AJE", "firstSeen": "", "covered": False},
        ])
        with open(prev, "a", encoding="utf-8", newline=chr(10)) as fh:
            fh.write("{bad json" + chr(10))
        carried = carry_rows(prev)
        eq("carry_rows 只带 channel=submit 的行、坏行只记账不抛",
           [(r["channel"], r["s"]) for r in carried],
           [(SUBMIT_CHANNEL, "Voxels Controllable Vessel: ")])
        kept, kept_stats = collect(packs_dir=packs, capture_files=[cap_file],
                                   do_attrib=False, carry_path=prev)
        eq("重跑 candidates：投稿行被带过来（这就是修复挡住的事故）",
           any(r["h"] == submit_h for r in kept), True)
        eq("重跑 candidates：其他通道的旧行不复活",
           any(r["s"] == "stale cfg row" for r in kept), False)
        eq("重跑 candidates：submit 记进 stats['channels']",
           kept_stats["channels"].get(SUBMIT_CHANNEL), 1)
        gone, _ = collect(packs_dir=packs, capture_files=[cap_file], do_attrib=False)
        eq("carry_path=None 时投稿行确实会丢（反证本条断言不是恒真）",
           any(r["h"] == submit_h for r in gone), False)
    finally:
        shutil.rmtree(base, ignore_errors=True)

    bad = [row for row in checks if not row[1]]
    for name, ok, detail in checks:
        print(("  [ok]   " if ok else "  [FAIL] ") + name
              + ("" if ok else "  " + detail))
    print("[candidates] 通过 {} / {} 项".format(len(checks) - len(bad), len(checks)))
    return 0 if not bad else 1


def main(argv=None):
    ensure_stdout()
    parser = argparse.ArgumentParser(
        description="五通道候选单一表（packs/candidates.jsonl）")
    parser.add_argument("--packs-dir", default=None, help="packs 目录（默认 <repo>/packs）")
    parser.add_argument("--pool", default=None, help="pool.jsonl（默认 <packs>/pool.jsonl）")
    parser.add_argument("--il", default=None,
                        help="il-translatable.jsonl（默认 <packs>/il-translatable.jsonl）")
    parser.add_argument("--cfgcache", default=None,
                        help="cfgcache-candidates.jsonl（默认 <packs>/cfgcache-candidates.jsonl）")
    parser.add_argument("--capture-dir", default=None,
                        help="运行时捕获目录（默认 PluginData/UI18n）")
    parser.add_argument("--capture", nargs="+", default=[],
                        help="显式指定 capture-*.jsonl（给了就不扫目录）")
    parser.add_argument("--no-capture", dest="with_capture", action="store_false",
                        default=True, help="只并静态四条通道（手机/CI 上没有捕获数据时用）")
    parser.add_argument("--out", default=None, help="输出（默认 packs/candidates.jsonl）")
    parser.add_argument("--report", default=None, help="报告（默认 reports/candidates.md）")
    parser.add_argument("--gamedata", default=None, help="GameData 目录（caller 反查用）")
    parser.add_argument("--managed", default=None, help="KSP_x64_Data\\Managed 目录")
    parser.add_argument("--no-attrib", dest="attrib", action="store_false",
                        default=True, help="跳过 dll 反查（mods 退回 caller 类型名根）")
    parser.add_argument("--top", type=int, default=20, help="报告里 top N 个 caller")
    parser.add_argument("--quiet", action="store_true")
    parser.add_argument("--selftest", action="store_true", help="跑本模块的断言")
    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

    packs_dir = args.packs_dir or capture_ingest.default_packs_dir()
    files = list(args.capture)
    if args.with_capture and not files:
        cdir = args.capture_dir or capture_ingest.default_capture_dir()
        files = sorted(glob.glob(os.path.join(cdir, "capture-*.jsonl")))
    gamedata = args.gamedata or ""
    if not gamedata and args.attrib:
        try:
            from .harvest import default_gamedata
            gamedata = default_gamedata() or ""
        except Exception:
            gamedata = ""
    managed = args.managed or (capture_ingest.default_managed(gamedata)
                               if gamedata else "")
    # out_path 必须在 collect 之前定：投稿行是从上一版本表里回读的（carry_rows）。
    out_path = (capture_ingest.resolve_repo_path(args.out)
                or os.path.join(packs_dir, OUT_NAME))
    rows, stats = collect(packs_dir, args.pool, args.il, args.cfgcache, files,
                          args.with_capture, gamedata, managed, args.attrib,
                          carry_path=out_path)
    report_path = (capture_ingest.resolve_repo_path(args.report)
                   or os.path.join(capture_ingest.repo_root(), *REPORT_REL))
    write_rows(out_path, rows)
    text = render_report(rows, stats, out_path, report_path, top=args.top,
                         argv_note="python -m i18n_tools.candidates")
    write_report(report_path, text)
    if not args.quiet:
        print("[candidates] 各通道合计 {} 行 -> 去重 {} 行".format(
            stats.get("flat", 0), len(rows)))
        print("[candidates] 分母：行数 {} - keyref {} - 占位符 {} = 可翻 {} 行，"
              "其中 covered {} / 未覆盖 {}（全表 covered {} / 未覆盖 {}）".format(
                  len(rows), stats.get("keyref", 0), stats.get("placeholder", 0),
                  stats.get("denominator", 0), stats.get("denom_covered", 0),
                  stats.get("denom_missing", 0),
                  stats.get("covered", 0), stats.get("missing", 0)))
        for channel in CHANNELS:
            print("[candidates]   {} {:>7,}".format(channel,
                                                   stats["channels"].get(channel, 0)))
        print("[candidates] 产物 {}  {} 字节  sha256 {}".format(
            out_path, os.path.getsize(out_path),
            capture_ingest.sha256_file(out_path)))
        print("[candidates] 报告 {}".format(report_path))
    return 0


if __name__ == "__main__":
    sys.exit(main())
