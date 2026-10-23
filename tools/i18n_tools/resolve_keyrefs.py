"""resolve_keyrefs —— 键引用解析行（常驻流水线步骤，P6 之后收尾）

为什么需要它
------------
harvest 阶段拿不到游戏内解析后的文本：cfg 里写的是 #autoLOC_500832 这样的键引用，
而运行时 UGUI 钩子拿到的是 KSP 已经解析成显示文本的字符串
（KSP.Localization.Localizer 在我们的钩子之前就替换完了）。所以 s 以 # 开头的行
在正常安装里几乎永远匹配不到。

本步骤**不改任何已有行**，只按 packs/glossary.jsonl 的官方译文记忆**追加**一批"解析行"：

    s = 官方英文原文（glossary.en）
    t = 官方中文（glossary.zh）
    n = normalize(s)，h = hash_text(n)
    mod / ctx 沿用原来那条键引用行

键引用行原样保留：极少数 mod 真的把未解析的键直接塞进 GUI，那时只有键引用行能命中。

用法
----
    python -m i18n_tools.resolve_keyrefs              # 原地补行（幂等）
    python -m i18n_tools.resolve_keyrefs --dry-run    # 只算不写
    python -m i18n_tools.resolve_keyrefs --selftest   # 不联网自测

顺序：必须在 translate --keyref-rescue **之后**跑——translate 会整文件重写，把它当收尾步骤。
幂等：新增行按 h 全局去重，第二遍 resolved = 0、行数不变。
"""

from __future__ import annotations

import argparse
import collections
import json
import re
import sys
import tempfile
import time
from pathlib import Path

from . import ensure_stdout
from .fnv import hash_text
from .textnorm import normalize

REPO = Path(__file__).resolve().parents[2]
PACKS_ROOT = REPO / "packs"
GLOSSARY_PATH = PACKS_ROOT / "glossary.jsonl"
REPORTS_DIR = REPO / "reports"
DEFAULT_LANG = "zh-cn"

# 运行时 schema 只有这几个字段（Core DictPack / JsonLineReader），绝不许扩
ALLOWED_FIELDS = ("h", "n", "s", "t", "mod", "ctx")

# 纯键引用：与 textnorm.is_key_like 同形（# 开头 + 标识符）。带空格/斜杠的
# 半解析串（如 #Sol_Mercury_desc//The first planet...）不算，保持原样不动。
_KEY_REF_RE = re.compile(r"^#[A-Za-z][A-Za-z0-9_.\-]*$")

_PLACEHOLDER_RE = re.compile(r"<<\d+>>")
# 与 Lead 侧审计脚本 _scratch/audit_packs.py 的 tag_re **逐字节一致**：
# 审计只认这些标签名与属性写法，门禁必须同口径，否则补进去的行会被判 bad_richtext。
_TMP_TAG_RE = re.compile(r"</?(?:b|i|u|s|color|size|material|quad|sprite|link)(?:=[^>]*)?>")
_FORMAT_RE = re.compile(r"\{\d+\}|%[sdif]")


def is_key_ref(text) -> bool:
    """是不是一条"未解析的本地化键引用"（整条就是一个键，没有别的字）。"""
    return isinstance(text, str) and bool(_KEY_REF_RE.match(text))


def _same_multiset(left: str, right: str, pattern) -> bool:
    return collections.Counter(pattern.findall(left)) == collections.Counter(pattern.findall(right))


def _ratio_ok(en: str, zh: str) -> bool:
    """与 translate.gate 的 G6 **同口径**：源文长度 < 9 时比值噪声太大，不判。

    27 字符的 `Description Creation Pending` → 90 字符的中文描述，比值 3.3，会被这里拦下。
    """
    if len(en) < 9:
        return True
    ratio = len(zh) / float(len(en))
    return 0.15 <= ratio <= 1.8


def build_entry(row, glossary, en_variants=None, ambiguous_guard=True, ratio_gate=True):
    """键引用行 → 解析行。返回 (entry, None) 或 (None, reason)。

    reason 取值：not_key_ref / unresolvable / gated_same / gated_placeholder /
    gated_richtext / gated_format / gated_ratio / ambiguous_en。
    只有 unresolvable 表示"glossary 里没有这条键"，其余都是"有键但套用会破坏门禁或产生歧义"，
    在报告里分开计数。

    **ambiguous_en（歧义守卫）**：同一句官方英文对应多个不同官方中文时一律不插入。
    KSP 的 `Description Creation Pending` 就是典型：7 个键共用这一句英文，各自的中文是
    那个零件自己的描述。插一个进去 = 把通用占位串永久绑到某个零件的描述上，运行时任何
    "描述待撰写"的零件都会显示那句具体描述，比显示英文更糟。
    **gated_ratio（长度比门禁）**：zh/en 字符长度比越界（G6 同口径），是第二道网。
    """
    key = row.get("s") if isinstance(row, dict) else None
    if not is_key_ref(key):
        return None, "not_key_ref"
    en = glossary.keyref_en.get(key)
    zh = glossary.keyref.get(key)
    if not isinstance(en, str) or not en.strip() or not isinstance(zh, str) or not zh.strip():
        return None, "unresolvable"
    en = en.strip()
    zh = zh.strip()
    normalized = normalize(en)
    if not normalized:
        return None, "unresolvable"
    if zh == en:
        return None, "gated_same"
    if not _same_multiset(en, zh, _PLACEHOLDER_RE):
        return None, "gated_placeholder"
    if not _same_multiset(en, zh, _TMP_TAG_RE):
        return None, "gated_richtext"
    if not _same_multiset(en, zh, _FORMAT_RE):
        return None, "gated_format"
    if ambiguous_guard and en_variants is not None and len(en_variants.get(en, ())) > 1:
        return None, "ambiguous_en"
    if ratio_gate and not _ratio_ok(en, zh):
        return None, "gated_ratio"
    entry = {"h": hash_text(normalized), "n": normalized, "s": en, "t": zh,
             "mod": row.get("mod") or ""}
    ctx = row.get("ctx")
    if isinstance(ctx, str) and ctx.strip():
        entry["ctx"] = ctx
    return entry, None


def load_rows(path):
    """读一个包文件的全部行（跳过空行）。"""
    rows = []
    with Path(path).open("r", encoding="utf-8") as handle:
        for line in handle:
            if not line.strip():
                continue
            rows.append(json.loads(line))
    return rows


def plan_file(path, glossary, seen, en_variants=None, ambiguous_guard=True, ratio_gate=True):
    """算一个包文件要追加的行，不碰磁盘。

    seen 是**全局** h 集合（跨文件）：同一个英文串在多个 mod 里出现时只补一行，
    避免同一 h 落在两个文件里、装载顺序决定谁生效。
    en_variants 是 `build_en_variants(glossary)` 的结果，供歧义守卫使用。
    """
    rows = load_rows(path)
    stats = {"rows_before": len(rows), "key_rows": 0, "resolved": 0, "existing": 0,
             "duplicate": 0, "unresolvable": 0, "gated": 0, "not_key_ref": 0,
             "ambiguous_skipped": 0, "gated_ratio": 0,
             "ambiguous_details": [], "gated_ratio_details": [], "skipped_keys": []}
    entries = []
    conflicts = []
    local_new = set()
    for row in rows:
        stats["key_rows"] += 1 if is_key_ref(row.get("s")) else 0
        entry, reason = build_entry(row, glossary, en_variants=en_variants,
                                    ambiguous_guard=ambiguous_guard, ratio_gate=ratio_gate)
        if entry is None:
            if reason == "unresolvable":
                stats["unresolvable"] += 1
            elif reason == "gated":
                stats["gated"] += 1
            elif reason == "ambiguous_en":
                stats["ambiguous_skipped"] += 1
                en = glossary.keyref_en.get(row.get("s")) or ""
                stats["ambiguous_details"].append(
                    {"key": row.get("s"), "en": en, "variants": en_variants.get(en, []),
                     "mod": row.get("mod")})
                stats["skipped_keys"].append(row.get("s"))
            elif reason == "gated_ratio":
                stats["gated_ratio"] += 1
                en = glossary.keyref_en.get(row.get("s")) or ""
                zh = glossary.keyref.get(row.get("s")) or ""
                stats["gated_ratio_details"].append(
                    {"key": row.get("s"), "en": en, "zh": zh,
                     "ratio": round(len(zh) / float(len(en)), 3) if en else None,
                     "mod": row.get("mod")})
                stats["skipped_keys"].append(row.get("s"))
            elif reason.startswith("gated"):
                stats["gated"] += 1
            elif reason == "not_key_ref":
                stats["not_key_ref"] += 1
            continue
        digest = entry["h"]
        if digest in seen:
            stats["existing"] += 1
            conflicts.append({"h": digest, "key": row.get("s"), "mod": row.get("mod"),
                              "official": entry["t"], "reason": "h 已在包内（跳过，不改已有行）"})
            continue
        if digest in local_new:
            stats["duplicate"] += 1
            continue
        local_new.add(digest)
        entries.append(entry)
        stats["resolved"] += 1
    stats["rows_after"] = stats["rows_before"] + len(entries)
    return entries, stats, conflicts


def _append_rows(path, entries):
    """追加写：原文件字节一个不改，只在末尾续行。"""
    path = Path(path)
    needs_newline = False
    if path.exists() and path.stat().st_size:
        with path.open("rb") as handle:
            handle.seek(-1, 2)
            needs_newline = handle.read(1) != b"\n"
    with path.open("a", encoding="utf-8", newline="\n") as handle:
        if needs_newline:
            handle.write("\n")
        for entry in entries:
            handle.write(json.dumps(entry, ensure_ascii=False) + "\n")


def build_en_index(glossary):
    """官方英文原文（逐字节）→ 官方中文。

    只在"整条英文串完全相同"时才认，**不做归一化、不做大小写折叠**：
    White 与 white 是两句不同的话，官方记忆只对前者负责。
    同一 en 对应多个不同 zh 时记入 ambiguous，宁可不改也不猜。
    """
    index = {}
    ambiguous = {}
    for key, en in glossary.keyref_en.items():
        zh = glossary.keyref.get(key)
        if not isinstance(en, str) or not en or not isinstance(zh, str) or not zh:
            continue
        current = index.get(en)
        if current is None:
            index[en] = zh
        elif current != zh:
            ambiguous.setdefault(en, set()).update({current, zh})
    for en in list(ambiguous):
        index.pop(en, None)
    return index, {en: sorted(zhs) for en, zhs in ambiguous.items()}


def build_en_variants(glossary):
    """官方英文原文（逐字节）→ 去重后的官方中文变体列表（只保留 >1 的）。

    插入侧的歧义守卫用它：一句英文对应多个官方中文说明官方自己按上下文区分，
    盲选一个必然在某些上下文里错 → 一律不插入（`Description Creation Pending` 7 个变体）。
    """
    variants = {}
    for key, en in glossary.keyref_en.items():
        zh = glossary.keyref.get(key)
        if not isinstance(en, str) or not en.strip():
            continue
        if not isinstance(zh, str) or not zh.strip():
            continue
        variants.setdefault(en.strip(), set()).add(zh.strip())
    return {en: sorted(zhs) for en, zhs in variants.items() if len(zhs) > 1}


def _replace_t_value(line, new_value):
    """只替换这一行里**顶层 "t" 的值**，其余字节一个不动。返回 (新行, 是否找到)。

    不整行重新序列化：json.dumps 整行会动字段顺序/空格，git diff 就看不清覆盖点。
    这里按 JSON 字符串扫描定位键名，避开 s/t 值里可能出现的引号与转义。
    """
    i = 0
    size = len(line)
    depth = 0
    while i < size:
        ch = line[i]
        if ch == '"':
            j = i + 1
            while j < size:
                c = line[j]
                if c == chr(92):
                    j += 2
                    continue
                if c == '"':
                    break
                j += 1
            token = line[i:j + 1]
            if depth == 1 and token == '"t"':
                k = j + 1
                while k < size and line[k] in " " + chr(9):
                    k += 1
                if k < size and line[k] == ":":
                    k += 1
                    while k < size and line[k] in " " + chr(9):
                        k += 1
                    if k < size and line[k] == '"':
                        v = k + 1
                        while v < size:
                            c = line[v]
                            if c == chr(92):
                                v += 2
                                continue
                            if c == '"':
                                break
                            v += 1
                        return (line[:k] + json.dumps(new_value, ensure_ascii=False)
                                + line[v + 1:]), True
                return line, False
            i = j + 1
            continue
        if ch in "{[":
            depth += 1
        elif ch in "}]":
            depth -= 1
        i += 1
    return line, False


def _gate_reason(source, official):
    """官方 zh 能不能安全套到这条 s 上。返回原因字符串，空串表示通过。"""
    if not isinstance(official, str) or not official.strip():
        return "empty"
    if official == source:
        return "same"
    if not _same_multiset(source, official, _PLACEHOLDER_RE):
        return "placeholder"
    if not _same_multiset(source, official, _TMP_TAG_RE):
        return "richtext"
    if not _same_multiset(source, official, _FORMAT_RE):
        return "format"
    return ""


def prefer_official_pass(lang_dir, files, glossary, dry_run=False):
    """官方译文优先：s 与 glossary.en 逐字节相同且官方 zh 过全部门禁时，覆盖 t。

    只改 t 的值（其余字节与行序完全不动），幂等：第二遍 official_overrides = 0。
    """
    index, ambiguous = build_en_index(glossary)
    stats = {"official_overrides": 0, "skipped_gate": 0, "ambiguous": len(ambiguous),
             "already_official": 0, "errors": 0, "details": [], "skipped": [],
             "ambiguous_ok": 0, "ambiguous_diff": 0, "ambiguous_details": [],
             "ambiguous_examples": [{"en": en, "zh": zhs} for en, zhs in list(ambiguous.items())[:10]]}
    if not index:
        return stats
    for name in files:
        path = Path(lang_dir) / name
        text = path.read_text(encoding="utf-8", newline="")
        lines = text.splitlines(keepends=True)
        changed = 0
        for position, line in enumerate(lines, 1):
            body = line.rstrip(chr(13) + chr(10))
            if not body.strip():
                continue
            row = json.loads(body)
            source = row.get("s")
            if not isinstance(source, str):
                continue
            official = index.get(source)
            if official is None:
                # 官方记忆里同一句英文有多个中文：宁可不改也不猜，只统计现状
                variants = ambiguous.get(source)
                if variants:
                    if row.get("t") in variants:
                        stats["ambiguous_ok"] += 1
                    else:
                        stats["ambiguous_diff"] += 1
                        if len(stats["ambiguous_details"]) < 20:
                            stats["ambiguous_details"].append(
                                {"file": name, "line": position, "mod": row.get("mod"),
                                 "s": source[:60], "t": (row.get("t") or "")[:40],
                                 "official_variants": [z[:40] for z in variants]})
                continue
            if row.get("t") == official:
                stats["already_official"] += 1
                continue
            reason = _gate_reason(source, official)
            if reason:
                stats["skipped_gate"] += 1
                if len(stats["skipped"]) < 20:
                    stats["skipped"].append({"file": name, "line": position, "mod": row.get("mod"),
                                             "s": source[:60], "old_t": (row.get("t") or "")[:40],
                                             "official": official[:40], "reason": reason})
                continue
            new_line, found = _replace_t_value(line, official)
            if not found:
                stats["errors"] += 1
                continue
            probe = json.loads(new_line.rstrip(chr(13) + chr(10)))
            if probe.get("t") != official or any(probe.get(k) != row.get(k)
                                                 for k in ("h", "n", "s", "mod", "ctx")):
                stats["errors"] += 1
                continue
            lines[position - 1] = new_line
            changed += 1
            stats["official_overrides"] += 1
            if len(stats["details"]) < 200:
                stats["details"].append({"file": name, "line": position, "mod": row.get("mod"),
                                         "s": source, "old_t": row.get("t"), "new_t": official})
        if changed and not dry_run:
            path.write_text("".join(lines), encoding="utf-8", newline="")
    return stats


def drop_keyrefs_pass(lang_dir, files, glossary, dry_run=False, allow_en_equiv=True,
                      retain_keys=None):
    """第 ④ 步：删除 s 以 # 开头的键引用行（零覆盖损失硬门禁）。

    retain_keys：本步**故意保留**的键（插入侧被歧义守卫 / 长度比门禁拒掉的键）。
    它们的官方中文没有任何行代表，删掉就是真丢内容 → 保留原行并计入 `retained`，
    既不删也不报错。

    删除前对**每一条**待删行做覆盖证明，官方中文 zh 必须能在剩余行里找到：
      ① 同一文件的剩余行里有某行 t == zh，或
      ② 全局剩余行里有某行 t == zh，或
      ③ allow_en_equiv 且全局剩余行里有某行 s == glossary.en（逐字节）——
         DictPack 以 h = fnv1a(normalize(s)) 为键，同一 en 只能存活一种译法，
         这类 1-en-多-zh 的官方重复在运行时结构上不可达，故按"en 已被代表"计覆盖。
    找不到 → 整体放弃（一个文件都不写）并返回错误，不是跳过、不是警告。

    只在内存里删，全部校验通过才落盘，所以"回滚"是天然的：出错即零写入。
    """
    lang_dir = Path(lang_dir)
    buffers = {}
    for name in files:
        path = lang_dir / name
        text = path.read_text(encoding="utf-8", newline="")
        buffers[name] = {"path": path, "text": text, "lines": text.splitlines(keepends=True), "kept": []}

    retain_keys = set(retain_keys or ())
    dropped = []
    retained = []
    rows_before = 0
    global_t = set()
    global_s = set()
    for name in files:
        for position, line in enumerate(buffers[name]["lines"], 1):
            body = line.rstrip(chr(13) + chr(10))
            if not body.strip():
                buffers[name]["kept"].append(line)
                continue
            rows_before += 1
            row = json.loads(body)
            source = row.get("s")
            if isinstance(source, str) and source.startswith("#"):
                if source in retain_keys:
                    buffers[name]["kept"].append(line)
                    retained.append({"file": name, "line": position, "s": source,
                                     "t": row.get("t"), "mod": row.get("mod")})
                    continue
                dropped.append({"file": name, "line": position, "s": source,
                                "t": row.get("t"), "mod": row.get("mod")})
                continue
            buffers[name]["kept"].append(line)
            value = row.get("t")
            if isinstance(value, str):
                global_t.add(value)
            source = row.get("s")
            if isinstance(source, str):
                global_s.add(source)

    file_t = {}
    for name in files:
        values = set()
        for line in buffers[name]["kept"]:
            body = line.rstrip(chr(13) + chr(10))
            if not body.strip():
                continue
            value = json.loads(body).get("t")
            if isinstance(value, str):
                values.add(value)
        file_t[name] = values

    # 覆盖证明：一条不满足就整体放弃
    errors = []
    same_file = 0
    global_hit = 0
    en_equiv = []
    for item in dropped:
        zh = glossary.keyref.get(item["s"])
        if not isinstance(zh, str) or not zh:
            errors.append({"file": item["file"], "line": item["line"], "mod": item["mod"],
                           "s": item["s"], "reason": "unresolvable",
                           "detail": "glossary 里查不到该键的官方中文"})
            continue
        if zh in file_t[item["file"]]:
            same_file += 1
        elif zh in global_t:
            global_hit += 1
        elif allow_en_equiv:
            en = glossary.keyref_en.get(item["s"]) or ""
            if en and en in global_s:
                en_equiv.append({"file": item["file"], "line": item["line"], "mod": item["mod"],
                                 "s": item["s"], "en": en, "zh": zh,
                                 "kept_s": en, "kept_t": item.get("t")})
                continue
            errors.append({"file": item["file"], "line": item["line"], "mod": item["mod"],
                           "s": item["s"], "reason": "no_coverage", "zh": zh, "en": en})
        else:
            errors.append({"file": item["file"], "line": item["line"], "mod": item["mod"],
                           "s": item["s"], "reason": "no_coverage", "zh": zh})

    bytes_before = sum(len(buffers[name]["text"].encode("utf-8")) for name in files)
    bytes_after = sum(len("".join(buffers[name]["kept"]).encode("utf-8")) for name in files)
    by_mod = {}
    for item in dropped:
        key = item["mod"] or "?"
        by_mod[key] = by_mod.get(key, 0) + 1

    stats = {"rows_before": rows_before, "dropped": 0, "rows_after": rows_before,
             "bytes_before": bytes_before, "bytes_after": bytes_after,
             "by_mod": by_mod, "coverage_verified": False,
             "coverage_same_file": same_file, "coverage_global": global_hit,
             "coverage_en_equiv": len(en_equiv), "en_equiv_details": en_equiv[:20],
             "retained": len(retained), "retained_samples": retained[:12],
             "errors": errors, "dry_run": bool(dry_run),
             "samples": [{"file": item["file"], "line": item["line"], "mod": item["mod"],
                          "s": item["s"]} for item in dropped[:12]]}
    if errors:
        stats["bytes_after"] = bytes_before
        return stats

    stats["coverage_verified"] = True
    stats["dropped"] = len(dropped)
    stats["rows_after"] = rows_before - len(dropped)
    if dry_run or not dropped:
        return stats

    for name in files:
        buffers[name]["path"].write_text("".join(buffers[name]["kept"]), encoding="utf-8", newline="")
    return stats


def run(lang=DEFAULT_LANG, packs_root=None, glossary_path=None, dry_run=False, log=print,
        prefer_official=True, drop_keyrefs=True, allow_en_equiv=True,
        ambiguous_guard=True, ratio_gate=True):
    """扫 packs/<lang>/*.jsonl，补键引用解析行。返回 payload（也写报告）。

    ambiguous_guard：同一官方英文对应多个官方中文时**不插入**（`Description Creation Pending`）。
    ratio_gate：zh/en 长度比越界（G6 同口径 0.15–1.8）时**不插入**，第二道网。
    两者拒掉的键在 ④ 删除时**保留原行**（否则官方中文就真丢了）。
    """
    packs_root = Path(packs_root or PACKS_ROOT)
    lang_dir = packs_root / lang
    if not lang_dir.is_dir():
        raise SystemExit("[resolve_keyrefs] 找不到目录：{}".format(lang_dir))

    from .translate import load_glossary          # 复用字段宽容的 glossary 解析
    glossary = load_glossary(glossary_path or GLOSSARY_PATH)
    if not glossary.keyref:
        log("[resolve_keyrefs] 警告：{} 里没有可用的键引用表（keyref 为空），本次只会统计 unresolvable"
            .format(glossary_path or GLOSSARY_PATH))

    en_variants = build_en_variants(glossary)
    files = sorted(p.name for p in lang_dir.glob("*.jsonl"))
    seen = set()
    for name in files:
        for row in load_rows(lang_dir / name):
            digest = row.get("h")
            if isinstance(digest, str):
                seen.add(digest)

    hash_before = 0
    for name in files:
        for row in load_rows(lang_dir / name):
            if isinstance(row.get("s"), str) and row["s"].startswith("#"):
                hash_before += 1

    payload = {"lang": lang, "dry_run": bool(dry_run), "packs_dir": str(lang_dir),
               "glossary": str(glossary_path or GLOSSARY_PATH),
               "glossary_version": glossary.version, "files": len(files),
               "keyref_total": 0, "resolved": 0, "unresolvable": 0, "existing": 0,
               "duplicate": 0, "gated": 0, "not_key_ref": 0,
               "ambiguous_guard": bool(ambiguous_guard), "ratio_gate": bool(ratio_gate),
               "ambiguous_en_total": len(en_variants), "ambiguous_skipped": 0,
               "gated_ratio": 0, "ambiguous_skipped_details": [], "gated_ratio_details": [],
               "retained_key_rows": 0, "retained_samples": [],
               "append_rows_before": 0, "append_rows_after": 0,
               "rows_before": 0, "dropped": 0, "rows_after": 0,
               "bytes_before": 0, "bytes_after": 0, "by_mod": {},
               "coverage_verified": False, "coverage_same_file": 0, "coverage_global": 0,
               "drop_errors": [], "drop_samples": [], "drop_keyrefs": bool(drop_keyrefs),
               "ok": True,
               "hash_s_rows_before": hash_before, "hash_s_rows_after": None,
               "prefer_official": bool(prefer_official),
               "by_file": {}, "conflicts": [], "samples": [], "build": time.strftime("%Y-%m-%dT%H:%M:%S")}

    retain_keys = set()
    for name in files:
        path = lang_dir / name
        entries, stats, conflicts = plan_file(path, glossary, seen, en_variants=en_variants,
                                              ambiguous_guard=ambiguous_guard, ratio_gate=ratio_gate)
        payload["ambiguous_skipped"] += stats["ambiguous_skipped"]
        payload["gated_ratio"] += stats["gated_ratio"]
        for detail in stats["ambiguous_details"]:
            payload["ambiguous_skipped_details"].append(dict(detail, file=name))
        for detail in stats["gated_ratio_details"]:
            payload["gated_ratio_details"].append(dict(detail, file=name))
        retain_keys.update(stats["skipped_keys"])
        payload["keyref_total"] += stats["key_rows"]
        payload["resolved"] += stats["resolved"]
        payload["unresolvable"] += stats["unresolvable"]
        payload["existing"] += stats["existing"]
        payload["duplicate"] += stats["duplicate"]
        payload["gated"] += stats["gated"]
        payload["not_key_ref"] += stats["not_key_ref"]
        payload["append_rows_before"] += stats["rows_before"]
        payload["append_rows_after"] += stats["rows_after"]
        payload["conflicts"].extend(conflicts)
        if stats["resolved"]:
            payload["by_file"][name] = stats
            for entry in entries:
                if len(payload["samples"]) < 12:
                    payload["samples"].append({"h": entry["h"], "s": entry["s"],
                                               "t": entry["t"], "mod": entry["mod"]})
            if not dry_run:
                _append_rows(path, entries)
                for entry in entries:
                    seen.add(entry["h"])

    # 官方译文优先：只覆盖 t，不增删行、不重排（在补行之后跑，新补的行本来就是官方值）
    if prefer_official:
        official = prefer_official_pass(lang_dir, files, glossary, dry_run=dry_run)
    else:
        official = {"official_overrides": 0, "skipped_gate": 0, "ambiguous": 0,
                    "already_official": 0, "errors": 0, "details": [], "skipped": [],
                    "ambiguous_ok": 0, "ambiguous_diff": 0, "ambiguous_details": [],
                    "ambiguous_examples": []}
    payload["official_overrides"] = official["official_overrides"]
    payload["official_skipped_gate"] = official["skipped_gate"]
    payload["official_already"] = official["already_official"]
    payload["official_ambiguous"] = official["ambiguous"]
    payload["official_ambiguous_ok"] = official["ambiguous_ok"]
    payload["official_ambiguous_diff"] = official["ambiguous_diff"]
    payload["official_ambiguous_details"] = official["ambiguous_details"]
    payload["official_errors"] = official["errors"]
    payload["official_details"] = official["details"]
    payload["official_skipped"] = official["skipped"]
    payload["official_ambiguous_examples"] = official["ambiguous_examples"]

    # ④ 删除键引用行：零覆盖损失硬门禁（在补行与官方覆盖之后跑，解析行此时已在盘上）
    if drop_keyrefs:
        drop = drop_keyrefs_pass(lang_dir, files, glossary, dry_run=dry_run,
                                 allow_en_equiv=allow_en_equiv, retain_keys=retain_keys)
    else:
        total_bytes = sum((lang_dir / name).stat().st_size for name in files)
        drop = {"rows_before": payload["append_rows_after"],
                "dropped": 0, "rows_after": payload["append_rows_after"],
                "bytes_before": total_bytes, "bytes_after": total_bytes, "by_mod": {},
                "coverage_verified": False, "coverage_same_file": 0, "coverage_global": 0,
                "coverage_en_equiv": 0, "en_equiv_details": [],
                "retained": 0, "retained_samples": [],
                "errors": [], "dry_run": bool(dry_run), "samples": []}
    payload["rows_before"] = drop["rows_before"]
    payload["dropped"] = drop["dropped"]
    payload["rows_after"] = drop["rows_after"]
    payload["bytes_before"] = drop["bytes_before"]
    payload["bytes_after"] = drop["bytes_after"]
    payload["by_mod"] = drop["by_mod"]
    payload["coverage_verified"] = drop["coverage_verified"]
    payload["coverage_same_file"] = drop["coverage_same_file"]
    payload["coverage_global"] = drop["coverage_global"]
    payload["coverage_en_equiv"] = drop["coverage_en_equiv"]
    payload["en_equiv_details"] = drop["en_equiv_details"]
    payload["retained_key_rows"] = drop["retained"]
    payload["retained_samples"] = drop["retained_samples"]
    payload["drop_errors"] = drop["errors"]
    payload["drop_samples"] = drop["samples"]
    payload["hash_s_rows_after"] = payload["hash_s_rows_before"] - drop["dropped"]
    payload["rows_added"] = 0 if dry_run else payload["resolved"]
    payload["ok"] = not drop["errors"]

    log("[resolve_keyrefs] 键引用行 {} 条 → 解析行新增 {} 条（dry_run={}）"
        .format(payload["keyref_total"], payload["resolved"], bool(dry_run)))
    log("[resolve_keyrefs] unresolvable {} / existing {}（h 已在包内，跳过）/ duplicate {}（同英文串多键）/ gated {}"
        .format(payload["unresolvable"], payload["existing"], payload["duplicate"], payload["gated"]))
    if payload["ambiguous_skipped"] or payload["gated_ratio"]:
        log("[resolve_keyrefs] 插入守卫：歧义跳过 {} 条（{} 个多义 en）/ 长度比拦截 {} 条；"
            "这些键在 ④ 保留原行（否则官方中文真丢）"
            .format(payload["ambiguous_skipped"],
                    len({d["en"] for d in payload["ambiguous_skipped_details"]}),
                    payload["gated_ratio"]))
        for detail in payload["ambiguous_skipped_details"][:6]:
            log("    [歧义] {} {} → 官方变体 {} 个，不插入"
                .format(detail["file"], detail["key"], len(detail["variants"])))
        for detail in payload["gated_ratio_details"][:6]:
            log("    [长度比] {} {} 比值 {} 越界（0.15–1.8），不插入"
                .format(detail["file"], detail["key"], detail["ratio"]))
    log("[resolve_keyrefs] 补行：行数 {} → {}"
        .format(payload["append_rows_before"], payload["append_rows_after"]))
    if drop_keyrefs:
        log("[resolve_keyrefs] ④ 删除键引用行：{} 行 → {} 行（删 {} 条，{} 字节 → {} 字节）；"
            "覆盖证明 {}（同文件命中 {} / 全局命中 {} / en 等价 {}）；守卫保留原行 {} 条"
            .format(payload["rows_before"], payload["rows_after"], payload["dropped"],
                    payload["bytes_before"], payload["bytes_after"],
                    "通过" if payload["coverage_verified"] else "未通过",
                    payload["coverage_same_file"], payload["coverage_global"],
                    payload["coverage_en_equiv"], payload["retained_key_rows"]))
        for item in payload["retained_samples"][:8]:
            log("    [保留] {}:{} {} → {}（插入被守卫拒掉，官方中文只在键行里）"
                .format(item["file"], item["line"], item["s"], (item["t"] or "")[:24]))
        for item in payload["en_equiv_details"]:
            log("    [en 等价] {}:{} {} → en {!r} 的官方中文有多个变体，运行时按 h=fnv1a(normalize(s)) 只存活一条"
                .format(item["file"], item["line"], item["s"], item["en"][:40]))
        for name, count in sorted(payload["by_mod"].items(), key=lambda kv: -kv[1])[:8]:
            log("    {:<32} 删除 {:>4}".format(name, count))
        if payload["drop_errors"]:
            log("[resolve_keyrefs] 覆盖证明失败：{} 条，已整体放弃（一个文件都没写）"
                .format(len(payload["drop_errors"])))
            for item in payload["drop_errors"][:12]:
                log("    [覆盖不足] {}:{} {} {} → {}".format(
                    item["file"], item["line"], item["mod"], item["s"], item["reason"]))
        else:
            log("[resolve_keyrefs] s 以 # 开头的行数 {} → {}"
                .format(payload["hash_s_rows_before"], payload["hash_s_rows_after"]))
    else:
        log("[resolve_keyrefs] ④ 删除键引用行：已关闭（--no-drop-keyrefs），s 以 # 开头 {} 行保留"
            .format(payload["hash_s_rows_before"]))
    if payload["by_file"]:
        for name, stats in sorted(payload["by_file"].items(), key=lambda kv: -kv[1]["resolved"])[:8]:
            log("    {:<32} 键引用 {:>4} → 新增 {:>4}".format(name, stats["key_rows"], stats["resolved"]))
    if prefer_official:
        log("[resolve_keyrefs] 官方译文优先：覆盖 t {} 条 / 门禁不过保持模型译文 {} 条 / 已是官方值 {} 条 / 歧义 {} 条（其中现值为官方变体 {} 条、非官方变体 {} 条）"
            .format(payload["official_overrides"], payload["official_skipped_gate"],
                    payload["official_already"], payload["official_ambiguous"],
                    payload["official_ambiguous_ok"], payload["official_ambiguous_diff"]))
        for item in payload["official_ambiguous_details"][:6]:
            log("    [歧义] {}:{} {}  {} → {} 官方变体 {}".format(
                item["file"], item["line"], item["mod"], item["s"], item["t"], item["official_variants"]))
        for item in payload["official_details"][:12]:
            log("    {}:{} [{}] {}  {} → {}".format(item["file"], item["line"], item["mod"],
                                                    item["s"][:38], item["old_t"], item["new_t"]))
    return payload


def write_report(payload, reports_dir=None):
    """统计落盘：reports/keyref-resolve-<ts>.json（同秒重跑加后缀，不覆盖）。"""
    target_dir = Path(reports_dir or REPORTS_DIR)
    target_dir.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    path = target_dir / "keyref-resolve-{}.json".format(stamp)
    serial = 1
    while path.exists():
        path = target_dir / "keyref-resolve-{}-{}.json".format(stamp, serial)
        serial += 1
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    return path


# ---------------------------------------------------------------------------
# 自测（不联网、不碰真包：全部在临时目录里造样例）
# ---------------------------------------------------------------------------

def selftest():
    """返回 (failures, checks)。负例覆盖：非键引用不动、en/zh 缺失、门禁不符、幂等。"""
    failures = []
    checks = 0

    def check(name, condition, detail=""):
        nonlocal checks
        checks += 1
        if not condition:
            failures.append((name, detail))

    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        packs = tmp / "packs"
        lang_dir = packs / "zh-cn"
        lang_dir.mkdir(parents=True)
        glossary_path = packs / "glossary.jsonl"

        glossary_rows = [
            {"key": "#autoLOC_1", "en": "LightYear Tire Company", "zh": "光年轮胎公司", "short": False},
            {"key": "#autoLOC_2", "en": "Toggle <<1>>", "zh": "开关<<1>>", "short": True},
            {"key": "#autoLOC_3", "en": "Dropped <<1>>", "zh": "丢了", "short": True},          # 占位符不齐
            {"key": "#autoLOC_4", "en": "<b>Bold</b> text", "zh": "粗体文本", "short": False},  # 标签不齐
            {"key": "#autoLOC_5", "en": "", "zh": "无英文", "short": True},                      # en 为空
            {"key": "#autoLOC_6", "en": "No Chinese", "zh": "", "short": True},                  # zh 为空
            {"key": "#autoLOC_7", "en": "White", "zh": "白色", "short": True},
            # 官方 zh 自己占位符不齐：套上去会崩 UI，必须保持模型译文并计 skipped_gate
            {"key": "#autoLOC_8", "en": "Gate <<1>> fail", "zh": "闸门失败", "short": False},
            # 同一句英文在官方记忆里有两个中文 → 歧义，不许猜
            {"key": "#autoLOC_9", "en": "Deploy <<1>>", "zh": "启动<<1>>", "short": False},
            {"key": "#autoLOC_10", "en": "Deploy <<1>>", "zh": "展开<<1>>", "short": False},
            # 官方记忆里同一 en 对应多个 zh（KSP 官方自身重复条目）→ 覆盖证明第 ③ 类
            {"key": "#autoLOC_11", "en": "Big wing", "zh": "大机翼甲", "short": True},
        ]
        glossary_path.write_text("".join(json.dumps(r, ensure_ascii=False) + "\n"
                                        for r in glossary_rows), encoding="utf-8")

        original = [
            {"h": hash_text(normalize("#autoLOC_1")), "n": normalize("#autoLOC_1"),
             "s": "#autoLOC_1", "t": "光年轮胎公司", "mod": "Squad"},
            {"h": hash_text(normalize("Hello world")), "n": normalize("Hello world"),
             "s": "Hello world", "t": "你好世界", "mod": "Squad"},          # 非键引用，绝不动
            {"h": hash_text(normalize("description = #OscarEtank_Part_description")),
             "n": normalize("description = #OscarEtank_Part_description"),
             "s": "description = #OscarEtank_Part_description",
             "t": "描述 = #OscarEtank_Part_description", "mod": "Squad"},   # 半解析串，不动
            {"h": hash_text(normalize("White")), "n": normalize("White"),
             "s": "White", "t": "白", "mod": "Squad"},                      # h 已存在 → existing
            {"h": hash_text(normalize("#autoLOC_3")), "n": normalize("#autoLOC_3"),
             "s": "#autoLOC_3", "t": "丢了", "mod": "Squad"},               # 门禁不符
            {"h": hash_text(normalize("#autoLOC_5")), "n": normalize("#autoLOC_5"),
             "s": "#autoLOC_5", "t": "无英文", "mod": "Squad"},             # unresolvable（en 为空）
            {"h": hash_text(normalize("#autoLOC_6")), "n": normalize("#autoLOC_6"),
             "s": "#autoLOC_6", "t": "No Chinese", "mod": "Squad"},         # unresolvable（zh 为空）
            {"h": hash_text(normalize("#autoLOC_7")), "n": normalize("#autoLOC_7"),
             "s": "#autoLOC_7", "t": "白色", "mod": "Squad"},               # h 已在包内 → existing
            {"h": hash_text(normalize("#autoLOC_2")), "n": normalize("#autoLOC_2"),
             "s": "#autoLOC_2", "t": "开关<<1>>", "mod": "Squad"},          # 可解析（带占位符）
            # ---- 以下三条专测官方译文优先（--prefer-official）----
            {"h": hash_text(normalize("white")), "n": normalize("white"),
             "s": "white", "t": "白", "mod": "Squad"},                      # 大小写不同 → 不算命中
            {"h": hash_text(normalize("Gate <<1>> fail")), "n": normalize("Gate <<1>> fail"),
             "s": "Gate <<1>> fail", "t": "闸门 <<1>> 失败", "mod": "Squad"},  # 官方 zh 门禁不过
            {"h": hash_text(normalize("Deploy <<1>>")), "n": normalize("Deploy <<1>>"),
             "s": "Deploy <<1>>", "t": "启动<<1>>", "mod": "Squad"},       # 歧义：现值已是官方变体
        ]
        pack_path = lang_dir / "Squad.jsonl"
        raw_lines = [json.dumps(r, ensure_ascii=False) for r in original]
        pack_path.write_text("\n".join(raw_lines) + "\n", encoding="utf-8")

        quiet = lambda *a, **k: None
        first = run(lang="zh-cn", packs_root=packs, glossary_path=glossary_path,
                    dry_run=False, log=quiet, drop_keyrefs=False)
        after_first = pack_path.read_text(encoding="utf-8").splitlines()

        check("键引用计数", first["keyref_total"] == 6, first["keyref_total"])
        check("解析行数", first["resolved"] == 2, first["resolved"])
        check("unresolvable", first["unresolvable"] == 2, first["unresolvable"])   # en 空 + zh 空
        check("existing(h 已在包内)", first["existing"] == 1, first["existing"])
        check("门禁拦截", first["gated"] == 1, first["gated"])
        # 原行一个不丢、顺序不变；唯一允许的字节变化是官方优先覆盖的那一条 t
        kept_prefix = after_first[:len(raw_lines)]
        changed_lines = [i for i, (a, b) in enumerate(zip(kept_prefix, raw_lines)) if a != b]
        white_index = [i for i, row in enumerate(original) if row["s"] == "White"]
        check("原行保留且顺序不变（仅官方覆盖行变字节）",
              len(kept_prefix) == len(raw_lines)
              and [json.loads(x)["s"] for x in kept_prefix] == [row["s"] for row in original]
              and changed_lines == white_index,
              changed_lines)
        check("行数增加", len(after_first) == len(raw_lines) + 2, len(after_first))

        new_rows = [json.loads(line) for line in after_first[len(raw_lines):]]
        by_s = {row["s"]: row for row in new_rows}
        check("解析行 s=官方英文", set(by_s) == {"LightYear Tire Company", "Toggle <<1>>"}, sorted(by_s))
        tire = by_s.get("LightYear Tire Company", {})
        check("解析行 t=官方中文", tire.get("t") == "光年轮胎公司", tire.get("t"))
        check("解析行 mod 沿用", tire.get("mod") == "Squad", tire.get("mod"))
        check("解析行 n=normalize(s)", tire.get("n") == normalize("LightYear Tire Company"), tire.get("n"))
        check("解析行 h=fnv1a(n)", tire.get("h") == hash_text(normalize("LightYear Tire Company")),
              tire.get("h"))
        check("解析行字段不越界", all(set(row) <= set(ALLOWED_FIELDS) for row in new_rows),
              [sorted(row) for row in new_rows])
        check("占位符行原样保留 <<1>>", by_s.get("Toggle <<1>>", {}).get("t") == "开关<<1>>",
              by_s.get("Toggle <<1>>"))
        check("s 以 # 开头行数不变",
              first["hash_s_rows_before"] == first["hash_s_rows_after"] == 6,
              (first["hash_s_rows_before"], first["hash_s_rows_after"]))

        # ---- 官方译文优先（--prefer-official）----
        after_rows = [json.loads(line) for line in after_first]
        by_s_all = {}
        for row in after_rows:
            by_s_all.setdefault(row["s"], []).append(row)
        white_row = (by_s_all.get("White") or [{}])[0]
        lower_row = (by_s_all.get("white") or [{}])[0]
        gated_row = (by_s_all.get("Gate <<1>> fail") or [{}])[0]
        check("官方优先 覆盖 t", white_row.get("t") == "白色", white_row.get("t"))
        check("官方优先 覆盖计数", first["official_overrides"] == 1, first["official_overrides"])
        check("官方优先 门禁不过保持模型译文",
              gated_row.get("t") == "闸门 <<1>> 失败" and first["official_skipped_gate"] == 1,
              (gated_row.get("t"), first["official_skipped_gate"]))
        check("官方优先 大小写不同不命中", lower_row.get("t") == "白", lower_row.get("t"))
        check("官方优先 已是官方值计数", first["official_already"] == 2, first["official_already"])
        check("官方优先 只改 t、字段与行序不变",
              len(after_rows) >= len(original) and all(
                  set(after_rows[i]) == set(original[i])
                  and all(after_rows[i][k] == original[i][k] for k in set(original[i]) - {"t"})
                  for i in range(len(original))),
              "字段或行序被改动")
        deploy_row = (by_s_all.get("Deploy <<1>>") or [{}])[0]
        check("官方优先 歧义不猜（现值已是官方变体）",
              first["official_ambiguous"] == 1 and first["official_ambiguous_ok"] == 1
              and first["official_ambiguous_diff"] == 0 and deploy_row.get("t") == "启动<<1>>",
              (first["official_ambiguous"], first["official_ambiguous_ok"],
               first["official_ambiguous_diff"], deploy_row.get("t")))
        check("官方优先 明细含行号与新旧译文",
              bool(first["official_details"])
              and first["official_details"][0]["old_t"] == "白"
              and first["official_details"][0]["new_t"] == "白色"
              and first["official_details"][0]["line"] > 0,
              first["official_details"][:1])

        # 幂等：第二遍 resolved = 0，文件一个字节都不变
        second = run(lang="zh-cn", packs_root=packs, glossary_path=glossary_path,
                     dry_run=False, log=quiet, drop_keyrefs=False)
        after_second = pack_path.read_text(encoding="utf-8")
        check("幂等 resolved=0", second["resolved"] == 0, second["resolved"])
        check("幂等 official_overrides=0", second["official_overrides"] == 0,
              second["official_overrides"])
        check("幂等 文件不变", after_second.splitlines() == after_first, len(after_second))

        # --no-prefer-official：模型译文保留，一条都不覆盖
        nopref_dir = tmp / "packs_nopref" / "zh-cn"
        nopref_dir.mkdir(parents=True)
        nopref_text = ("\n").join(raw_lines) + "\n"
        (nopref_dir / "Squad.jsonl").write_text(nopref_text, encoding="utf-8")
        nopref = run(lang="zh-cn", packs_root=tmp / "packs_nopref", glossary_path=glossary_path,
                     dry_run=False, log=quiet, prefer_official=False, drop_keyrefs=False)
        nopref_rows = [json.loads(line) for line in
                       (nopref_dir / "Squad.jsonl").read_text(encoding="utf-8").splitlines()]
        nopref_white = [row for row in nopref_rows if row["s"] == "White"]
        check("--no-prefer-official 不覆盖",
              nopref["official_overrides"] == 0 and bool(nopref_white)
              and nopref_white[0]["t"] == "白",
              (nopref["official_overrides"], nopref_white[:1]))

        # dry-run 不写盘
        third = run(lang="zh-cn", packs_root=packs, glossary_path=glossary_path,
                    dry_run=True, log=quiet, drop_keyrefs=False)
        check("dry-run 不写盘", pack_path.read_text(encoding="utf-8") == after_second, third["resolved"])

        # 空 glossary：一条都不加，全部记 unresolvable
        empty_glossary = packs / "empty.jsonl"
        empty_glossary.write_text("", encoding="utf-8")
        fourth = run(lang="zh-cn", packs_root=packs, glossary_path=empty_glossary,
                     dry_run=True, log=quiet, drop_keyrefs=False)
        check("空 glossary 不加行", fourth["resolved"] == 0 and fourth["unresolvable"] == 6,
              (fourth["resolved"], fourth["unresolvable"]))

        # ---- ④ 删除键引用行（--drop-keyrefs）：零覆盖损失硬门禁 ----
        def write_pack(directory, rows):
            directory.mkdir(parents=True, exist_ok=True)
            path = directory / "Squad.jsonl"
            path.write_text("".join(json.dumps(r, ensure_ascii=False) + ("\n") for r in rows),
                            encoding="utf-8")
            return path

        covered = [
            {"h": hash_text(normalize("#autoLOC_1")), "n": normalize("#autoLOC_1"),
             "s": "#autoLOC_1", "t": "光年轮胎公司", "mod": "Squad"},
            {"h": hash_text(normalize("LightYear Tire Company")),
             "n": normalize("LightYear Tire Company"),
             "s": "LightYear Tire Company", "t": "光年轮胎公司", "mod": "Squad"},
            {"h": hash_text(normalize("#autoLOC_2")), "n": normalize("#autoLOC_2"),
             "s": "#autoLOC_2", "t": "开关<<1>>", "mod": "Squad"},
            {"h": hash_text(normalize("Toggle <<1>>")), "n": normalize("Toggle <<1>>"),
             "s": "Toggle <<1>>", "t": "开关<<1>>", "mod": "Squad"},
            {"h": hash_text(normalize("Hello world")), "n": normalize("Hello world"),
             "s": "Hello world", "t": "你好世界", "mod": "Squad"},
        ]
        keep_text = [json.dumps(covered[i], ensure_ascii=False) for i in (1, 3, 4)]

        drop_root = tmp / "packs_drop"
        drop_pack = write_pack(drop_root / "zh-cn", covered)
        drop_bytes = drop_pack.stat().st_size
        dropped = run(lang="zh-cn", packs_root=drop_root, glossary_path=glossary_path,
                      dry_run=False, log=quiet, prefer_official=False, drop_keyrefs=True)
        drop_lines = drop_pack.read_text(encoding="utf-8").splitlines()
        check("④删行 计数",
              (dropped["dropped"], dropped["rows_before"], dropped["rows_after"]) == (2, 5, 3),
              (dropped["dropped"], dropped["rows_before"], dropped["rows_after"]))
        check("④删行 覆盖证明通过", dropped["coverage_verified"] is True
              and dropped["coverage_same_file"] == 2 and dropped["coverage_global"] == 0
              and not dropped["drop_errors"],
              (dropped["coverage_verified"], dropped["coverage_same_file"], dropped["drop_errors"]))
        check("④删行 非键行逐字节保留", drop_lines == keep_text, drop_lines)
        check("④删行 s# 归零", dropped["hash_s_rows_after"] == 0
              and not any(json.loads(x)["s"].startswith("#") for x in drop_lines),
              dropped["hash_s_rows_after"])
        check("④删行 字节减少", dropped["bytes_before"] == drop_bytes
              and dropped["bytes_after"] == drop_pack.stat().st_size < drop_bytes,
              (dropped["bytes_before"], dropped["bytes_after"]))
        check("④删行 by_mod", dropped["by_mod"] == {"Squad": 2}, dropped["by_mod"])

        equiv_rows = [
            {"h": hash_text(normalize("#autoLOC_11")), "n": normalize("#autoLOC_11"),
             "s": "#autoLOC_11", "t": "大机翼甲", "mod": "Squad"},
            {"h": hash_text(normalize("Big wing")), "n": normalize("Big wing"),
             "s": "Big wing", "t": "大机翼乙", "mod": "Squad"},
        ]
        equiv_root = tmp / "packs_drop_equiv"
        equiv_pack = write_pack(equiv_root / "zh-cn", equiv_rows)
        equiv = run(lang="zh-cn", packs_root=equiv_root, glossary_path=glossary_path,
                    dry_run=False, log=quiet, prefer_official=False, drop_keyrefs=True)
        equiv_lines = equiv_pack.read_text(encoding="utf-8").splitlines()
        check("④删行 en 等价覆盖（同 en 多 zh）", equiv["dropped"] == 1
              and equiv["coverage_verified"] is True and equiv["coverage_en_equiv"] == 1
              and equiv["coverage_same_file"] == 0
              and equiv_lines == [json.dumps(equiv_rows[1], ensure_ascii=False)],
              (equiv["dropped"], equiv["coverage_en_equiv"], equiv["coverage_same_file"]))

        strict_root = tmp / "packs_drop_strict"
        strict_pack = write_pack(strict_root / "zh-cn", equiv_rows)
        strict_text = strict_pack.read_text(encoding="utf-8")
        strict = run(lang="zh-cn", packs_root=strict_root, glossary_path=glossary_path,
                     dry_run=False, log=quiet, prefer_official=False, drop_keyrefs=True,
                     allow_en_equiv=False)
        check("④删行 --no-coverage-en-equiv 整体放弃", strict["dropped"] == 0
              and [e["reason"] for e in strict["drop_errors"]] == ["no_coverage"]
              and strict_pack.read_text(encoding="utf-8") == strict_text,
              (strict["dropped"], strict["drop_errors"][:1]))

        again = run(lang="zh-cn", packs_root=drop_root, glossary_path=glossary_path,
                    dry_run=False, log=quiet, prefer_official=False, drop_keyrefs=True)
        check("④删行 幂等 dropped=0 且字节不变", again["dropped"] == 0 and again["rows_after"] == 3
              and drop_pack.read_text(encoding="utf-8").splitlines() == drop_lines,
              (again["dropped"], again["rows_after"]))

        dry_root = tmp / "packs_drop_dry"
        dry_pack = write_pack(dry_root / "zh-cn", covered)
        dry_payload = run(lang="zh-cn", packs_root=dry_root, glossary_path=glossary_path,
                          dry_run=True, log=quiet, prefer_official=False, drop_keyrefs=True)
        check("④删行 dry-run 不写盘", dry_payload["dropped"] == 2
              and dry_pack.stat().st_size == drop_bytes, dry_payload["dropped"])

        off_root = tmp / "packs_drop_off"
        off_pack = write_pack(off_root / "zh-cn", covered)
        kept_payload = run(lang="zh-cn", packs_root=off_root, glossary_path=glossary_path,
                           dry_run=False, log=quiet, prefer_official=False, drop_keyrefs=False)
        off_lines = off_pack.read_text(encoding="utf-8").splitlines()
        check("④删行 --no-drop-keyrefs 不删行", kept_payload["dropped"] == 0
              and kept_payload["rows_after"] == 5
              and sum(1 for x in off_lines if json.loads(x)["s"].startswith("#")) == 2,
              (kept_payload["dropped"], kept_payload["rows_after"]))

        neg_rows = covered[:2] + [
            {"h": hash_text(normalize("#autoLOC_3")), "n": normalize("#autoLOC_3"),
             "s": "#autoLOC_3", "t": "孤儿键", "mod": "Squad"}]
        neg_root = tmp / "packs_drop_neg"
        neg_pack = write_pack(neg_root / "zh-cn", neg_rows)
        neg_text = neg_pack.read_text(encoding="utf-8")
        negative = run(lang="zh-cn", packs_root=neg_root, glossary_path=glossary_path,
                       dry_run=False, log=quiet, prefer_official=False, drop_keyrefs=True)
        check("④删行 覆盖不足整体放弃", negative["dropped"] == 0
              and negative["coverage_verified"] is False
              and [e["reason"] for e in negative["drop_errors"]] == ["no_coverage"]
              and neg_pack.read_text(encoding="utf-8") == neg_text,
              (negative["dropped"], negative["coverage_verified"], negative["drop_errors"][:1]))

        unk_root = tmp / "packs_drop_unk"
        unk_rows = covered[:2] + [
            {"h": hash_text(normalize("#autoLOC_99")), "n": normalize("#autoLOC_99"),
             "s": "#autoLOC_99", "t": "未知键", "mod": "Squad"}]
        unk_pack = write_pack(unk_root / "zh-cn", unk_rows)
        unk_text = unk_pack.read_text(encoding="utf-8")
        unknown = run(lang="zh-cn", packs_root=unk_root, glossary_path=glossary_path,
                      dry_run=False, log=quiet, prefer_official=False, drop_keyrefs=True)
        check("④删行 glossary 查不到即放弃", unknown["dropped"] == 0
              and [e["reason"] for e in unknown["drop_errors"]] == ["unresolvable"]
              and unk_pack.read_text(encoding="utf-8") == unk_text,
              (unknown["dropped"], unknown["drop_errors"][:1]))

        # ---- 插入守卫：歧义（同一官方英文多个官方中文）与长度比（G6 同口径）----
        guard_glossary = tmp / "guard_glossary.jsonl"
        guard_rows = [
            {"key": "#autoLOC_20", "en": "Description Creation Pending",
             "zh": "TD-06分级分离器是小型火箭使用的分离装置"},
            {"key": "#autoLOC_21", "en": "Description Creation Pending",
             "zh": "TS-25是用来将最大的火箭分成两半的装置"},
            {"key": "#autoLOC_22", "en": "Short phrase",
             "zh": "非常非常长的中文描述超过比值上限非常非常长的中文描述超过比值上限"},
        ]
        guard_glossary.write_text("".join(json.dumps(r, ensure_ascii=False) + chr(10)
                                            for r in guard_rows), encoding="utf-8")
        guard_root = tmp / "packs_guard"
        guard_pack = write_pack(guard_root / "zh-cn", [
            {"h": hash_text(normalize("#autoLOC_20")), "n": normalize("#autoLOC_20"),
             "s": "#autoLOC_20", "t": "TD-06分级分离器是小型火箭使用的分离装置", "mod": "Squad"},
            {"h": hash_text(normalize("#autoLOC_21")), "n": normalize("#autoLOC_21"),
             "s": "#autoLOC_21", "t": "TS-25是用来将最大的火箭分成两半的装置", "mod": "Squad"},
            {"h": hash_text(normalize("#autoLOC_22")), "n": normalize("#autoLOC_22"),
             "s": "#autoLOC_22", "t": "短", "mod": "Squad"},
        ])
        guarded = run(lang="zh-cn", packs_root=guard_root, glossary_path=guard_glossary,
                      dry_run=False, log=quiet, prefer_official=False, drop_keyrefs=True)
        guarded_lines = guard_pack.read_text(encoding="utf-8").splitlines()
        guarded_s = [json.loads(x)["s"] for x in guarded_lines]
        check("守卫 歧义不插入", guarded["ambiguous_skipped"] == 2 and guarded["resolved"] == 0,
              (guarded["ambiguous_skipped"], guarded["resolved"]))
        check("守卫 长度比拦截", guarded["gated_ratio"] == 1, guarded["gated_ratio"])
        check("守卫 通用占位串没有进包", "Description Creation Pending" not in guarded_s, guarded_s)
        check("守卫 被拒键保留原行", guarded["retained_key_rows"] == 3
              and guarded["hash_s_rows_after"] == 3 and guarded["rows_after"] == 3,
              (guarded["retained_key_rows"], guarded["hash_s_rows_after"], guarded["rows_after"]))
        check("守卫 歧义明细带变体", bool(guarded["ambiguous_skipped_details"])
              and len(guarded["ambiguous_skipped_details"][0]["variants"]) == 2,
              guarded["ambiguous_skipped_details"][:1])
        # 守卫关掉 → 复现"通用占位串被绑到某个零件的描述上"的缺陷（证明守卫不是可选项）
        off_guard_root = tmp / "packs_guard_off"
        off_guard_pack = write_pack(off_guard_root / "zh-cn", [
            {"h": hash_text(normalize("#autoLOC_20")), "n": normalize("#autoLOC_20"),
             "s": "#autoLOC_20", "t": "TD-06分级分离器是小型火箭使用的分离装置", "mod": "Squad"},
        ])
        unguarded = run(lang="zh-cn", packs_root=off_guard_root, glossary_path=guard_glossary,
                        dry_run=False, log=quiet, prefer_official=False, drop_keyrefs=False,
                        ambiguous_guard=False, ratio_gate=False)
        unguarded_s = [json.loads(x)["s"] for x in
                       off_guard_pack.read_text(encoding="utf-8").splitlines()]
        check("守卫 关掉后会插入占位串行（缺陷可复现）",
              unguarded["resolved"] == 1 and "Description Creation Pending" in unguarded_s,
              (unguarded["resolved"], unguarded_s))

        # 报告落盘
        report_path = write_report(first, tmp / "reports")
        check("报告落盘", report_path.exists() and "keyref_total" in report_path.read_text(encoding="utf-8"),
              str(report_path))

    return failures, checks


def main(argv=None):
    ensure_stdout()
    parser = argparse.ArgumentParser(prog="i18n_tools.resolve_keyrefs",
                                     description="键引用解析行后处理（常驻流水线步骤）")
    parser.add_argument("--lang", default=DEFAULT_LANG, help="语言目录名（默认 zh-cn）")
    parser.add_argument("--packs-dir", default=None, help="packs 根目录（默认仓库 packs/）")
    parser.add_argument("--glossary", default=None, help="glossary.jsonl 路径")
    parser.add_argument("--dry-run", action="store_true", help="只算不写")
    parser.add_argument("--prefer-official", dest="prefer_official", action="store_true",
                        default=True, help="官方译文优先：s 与 glossary.en 逐字节相同时覆盖 t（默认开）")
    parser.add_argument("--no-prefer-official", dest="prefer_official", action="store_false",
                        help="关闭官方译文优先，保留模型译文")
    parser.add_argument("--drop-keyrefs", dest="drop_keyrefs", action="store_true",
                        default=True,
                        help="删除 s 以 # 开头的键引用行（默认开；删前做零覆盖损失硬门禁）")
    parser.add_argument("--no-drop-keyrefs", dest="drop_keyrefs", action="store_false",
                        help="保留键引用行（默认会删）")
    parser.add_argument("--coverage-en-equiv", dest="allow_en_equiv", action="store_true",
                        default=True,
                        help="覆盖证明第 ③ 类：官方 en 已在包内（同 en 多 zh 的官方重复，默认接受）")
    parser.add_argument("--no-coverage-en-equiv", dest="allow_en_equiv", action="store_false",
                        help="覆盖证明只认 zh 逐字命中，同 en 多 zh 的 6 条会导致整体放弃")
    parser.add_argument("--ambiguous-guard", dest="ambiguous_guard", action="store_true",
                        default=True,
                        help="歧义守卫：同一官方英文对应多个官方中文时不插入解析行（默认开）")
    parser.add_argument("--allow-ambiguous-en", dest="ambiguous_guard", action="store_false",
                        help="关闭歧义守卫（仅用于复核；会把通用占位串绑到某个零件的描述上）")
    parser.add_argument("--ratio-gate", dest="ratio_gate", action="store_true", default=True,
                        help="长度比门禁：zh/en 比值越界（G6 同口径 0.15–1.8）时不插入（默认开）")
    parser.add_argument("--no-ratio-gate", dest="ratio_gate", action="store_false",
                        help="关闭长度比门禁")
    parser.add_argument("--no-report", action="store_true", help="不写 reports/keyref-resolve-*.json")
    parser.add_argument("--selftest", action="store_true", help="跑自测（不联网、不碰真包）")
    args = parser.parse_args(argv)

    if args.selftest:
        failures, checks = selftest()
        for item in failures:
            print("[FAIL] {}: {}".format(item[0], item[1:]))
        print("[selftest] {} 项检查，{} 项失败".format(checks, len(failures)))
        return 1 if failures else 0

    payload = run(lang=args.lang, packs_root=args.packs_dir, glossary_path=args.glossary,
                  dry_run=args.dry_run, prefer_official=args.prefer_official,
                  drop_keyrefs=args.drop_keyrefs, allow_en_equiv=args.allow_en_equiv,
                  ambiguous_guard=args.ambiguous_guard, ratio_gate=args.ratio_gate)
    if not args.no_report:
        print("[resolve_keyrefs] 报告 → {}".format(write_report(payload)))
    if not payload["ok"]:
        print("[resolve_keyrefs] 失败：覆盖证明未通过，键引用行一条都没删（详见报告 drop_errors）")
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
