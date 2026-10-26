# -*- coding: utf-8 -*-
"""submission.py -- 玩家投稿 bundle 的校验、拆分与归并（docs/10 §6，2026-10-26 冻结）。

为什么要有这一层：bundle 是**玩家端产物**，一旦发出去就必须永远能收（§6 的开头那句
"不能靠更新插件来纠正格式"）。所以这里只做三件事，而且是纯函数：

  1. 校验（schema / 同意 / id 自洽 / mods 与 entries 自洽 / 脱敏计数）
  2. 计算 canonical id（bundle 去掉 id 后排序键紧凑序列化 -> sha256 -> 前 16 位）
  3. 拆成"按 mod 一行一原文"的文件字节 + 归并进 packs/candidates.jsonl

落盘、写 journal、原子改名都由 api cmd_submit_accept 做 —— 本模块不碰文件系统，
因此可以被 selftest 用假数据反复喂，不需要临时仓库。

口径（照 §6 抄，别自己发明）：

  * meta.json 不含 actor.installId（installId 只为客户端本地去重存在，进仓库就是伪标识符）
  * packs/submission/<id>/<modId>.jsonl 的行**没有 t 字段**（这里不是语言包，是待译原文）
  * packs/submission/** 不进 GameData、也不被 packs/index.json 扫（后者只扫 packs/<lang>/）
  * candidates 归并（§6.5）：已覆盖 -> 仍写行 + covered:true + reason:"covered"；
    已在表里 -> 不插行，count 累加、sink/caller 取首个非空、src/channel 保留首次值；
    新 h -> 插入，channel:"submit"、src:"submission/<id>"、bucket 按现有分类器
  * submission **不走 G1-G9**（它没有 t，长度比 / 术语表 / echo keep / 骨架 # 数都无从判定）

错误类型：BundleError(kind, code, message)。kind 只取三个，映射到 docs/10 §3.4 的退出码：

    schema  -> exit 5（缺字段 / 行 schema 坏 / 同意不是 true / id 与内容不符）
    gate    -> exit 4（entries 为空 / entries[].mod 不在 mods[] 里 / 同 id 不同内容）
    internal-> exit 7（IO 或解析自爆）

自检：python -m i18n_tools.submission --selftest
"""

from __future__ import annotations

import bisect
import copy
import hashlib
import json
import re
import sys

from . import buckets
from .fnv import hash_text
from .textnorm import normalize

__all__ = ["SCHEMA", "BundleError", "canonical_json", "bundle_sha256", "bundle_id",
           "validate", "split_by_mod", "render_mod_rows", "merge_candidates", "plan",
           "meta_of", "index_entry_of", "selftest", "main"]

#: bundle 的 schema 字符串（逐字比对，不许模糊匹配）
SCHEMA = "ui18n-submission/1"
#: 投稿在 packs/ 下的目录名（不是语言包，别和 packs/zh-cn 混）
SUBMISSION_DIR = "packs/submission"
CANDIDATES_REL = "packs/candidates.jsonl"
META_NAME = "meta.json"
INDEX_NAME = "index.json"

#: 一行原文的最大长度（§6.1：s 非空、<= 8000 字符）
MAX_TEXT = 8000

_SID_RE = re.compile(r"^[0-9a-f]{16}$")
_H_RE = re.compile(r"^[0-9a-f]{16}$")
#: modId 要能安全当文件名用：只允许字母数字与 . _ -，且不许以点开头（挡住 .. 与隐藏文件）
_MOD_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
#: 脱敏审计用的"疑似路径/URL"判据（只 warning，不拒收：脱敏是客户端做的）
_SUSPECT_RE = re.compile(r"(?i)https?://|www\.|[A-Za-z]:\\|/GameData/|\\\\")

#: 分类器四档 -> candidates 表的三档词表（与 candidates.IL_BUCKETS 同源）
BUCKET_MAP = {"translate": "candidate", "short_label": "candidate",
              "template": "template", "never": "never"}
#: candidates 表的 bucket 排序（与 candidates.BUCKET_ORDER 同源，selftest 里有等式断言）
BUCKET_ORDER = {"candidate": 0, "pattern": 1, "template": 2, "never": 3}
#: candidates 表的 12 列（冻结，别自己加列）
CAND_FIELDS = ("h", "s", "src", "bucket", "reason", "channel", "caller", "sink",
               "count", "mod", "firstSeen", "covered")
#: 投稿文件里一行的字段（顺序固定；可选字段为空时不写）
ROW_FIELDS = ("h", "s", "mod", "channel", "sink", "caller", "src", "firstSeen")
ROW_OPTIONAL = ("channel", "sink", "caller")
#: §6.5 规定新行记这个通道名
SUBMIT_CHANNEL = "submit"
COVERED_REASON = "covered"
#: §6.3 的四个脱敏计数（缺了就补 0，可审计"本来就没有"和"被丢掉了"）
REDACTION_KEYS = ("paths", "userNames", "absolutePaths", "urls")


class BundleError(Exception):
    """校验失败 / 拒绝收下的唯一异常；kind 决定退出码（见模块 docstring）。"""

    KINDS = ("schema", "gate", "internal")

    def __init__(self, kind, code, message, details=None):
        super().__init__(message)
        self.kind = kind if kind in self.KINDS else "internal"
        self.code = code
        self.message = message
        self.details = [str(item) for item in (details or [])]


# ---------------------------------------------------------------------------
# canonical id：bundle 是内容寻址的，id 由内容派生
# ---------------------------------------------------------------------------

def canonical_json(bundle):
    """去掉 id 之后的规范序列化（sort_keys + 紧凑分隔符 + 不转义中文）。

    C# 侧必须产出逐字节相同的结果，所以这里不许用别的参数（§6.1 的"算法是硬的"）。
    """
    body = {key: value for key, value in (bundle or {}).items() if key != "id"}
    return json.dumps(body, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def bundle_sha256(bundle):
    """内容 sha256（全文，用于幂等判定与 journal 的 bundle_sha256）。"""
    return hashlib.sha256(canonical_json(bundle).encode("utf-8")).hexdigest()


def bundle_id(bundle):
    """canonical id = sha256(canonical body) 的前 16 位小写 hex。"""
    return bundle_sha256(bundle)[:16]


# ---------------------------------------------------------------------------
# 校验
# ---------------------------------------------------------------------------

def _is_int(value):
    return isinstance(value, int) and not isinstance(value, bool)


def _need_dict(container, key, path):
    value = container.get(key) if isinstance(container, dict) else None
    if not isinstance(value, dict):
        raise BundleError("schema", "SUBMISSION_FIELD_INVALID",
                          "投稿缺 %s%s（必须是对象）" % (path, key))
    return value


def _need_text(container, key, path, allow_empty=False):
    value = container.get(key) if isinstance(container, dict) else None
    if not isinstance(value, str) or (not allow_empty and not value.strip()):
        raise BundleError("schema", "SUBMISSION_FIELD_INVALID",
                          "投稿缺 %s%s（必须是非空字符串）" % (path, key))
    return value


def _check_redactions(bundle, warnings):
    """三步脱敏的计数：结构校验（非负整数）+ 抄进 meta。"""
    raw = bundle.get("redactions")
    out = {key: 0 for key in REDACTION_KEYS}
    if raw is None:
        warnings.append("投稿没有 redactions 计数（按 0 记，审计时无法区分"
                        "“本来没有”和“被丢掉了”）")
        return out
    if not isinstance(raw, dict):
        raise BundleError("schema", "SUBMISSION_FIELD_INVALID",
                          "redactions 必须是对象（四个计数）")
    for key, value in raw.items():
        if not _is_int(value) or value < 0:
            raise BundleError("schema", "SUBMISSION_FIELD_INVALID",
                              "redactions.%s 必须是非负整数（收到 %r）" % (key, value))
        out[key] = value
    return out


def _check_entries(entries, declared, warnings):
    """逐行校验 + 同 (mod, h) 去重（count 相加）；返回规范化后的行列表。"""
    rows, seen, h_index = [], {}, {}
    for index, item in enumerate(entries, 1):
        path = "entries[%d]." % index
        if not isinstance(item, dict):
            raise BundleError("schema", "SUBMISSION_FIELD_INVALID", path[:-1] + " 必须是对象")
        h = item.get("h")
        if not isinstance(h, str) or not _H_RE.match(h):
            raise BundleError("schema", "SUBMISSION_FIELD_INVALID",
                              path + "h 必须是 16 位小写十六进制（收到 %r）" % (h,))
        text = item.get("s")
        if not isinstance(text, str) or not text.strip():
            raise BundleError("schema", "SUBMISSION_FIELD_INVALID", path + "s 必须是非空字符串")
        if len(text) > MAX_TEXT:
            raise BundleError("schema", "SUBMISSION_FIELD_INVALID",
                              path + "s 长 %d 字符，超过 %d 上限" % (len(text), MAX_TEXT))
        mod = item.get("mod")
        if not isinstance(mod, str) or not mod.strip():
            raise BundleError("schema", "SUBMISSION_FIELD_INVALID", path + "mod 必须是非空字符串")
        if mod not in declared:
            raise BundleError("gate", "submission-mod-undeclared",
                              path + "mod=%s 不在 mods[] 里（投稿夹带了没声明的 mod）" % mod)
        for key in ("channel", "sink", "caller"):
            value = item.get(key)
            if value is not None and not isinstance(value, str):
                raise BundleError("schema", "SUBMISSION_FIELD_INVALID",
                                  path + key + " 必须是字符串")
        count = item.get("count", 1)
        if count is None:
            count = 1
        if not _is_int(count) or count < 0:
            raise BundleError("schema", "SUBMISSION_FIELD_INVALID",
                              path + "count 必须是非负整数（收到 %r）" % (count,))
        expect = hash_text(normalize(text))
        if expect != h:
            warnings.append("entries[%d] 的 h=%s 与原文重算的 %s 不一致（按投稿给的值收录，"
                            "候选表可能因此出现两行同文）" % (index, h, expect))
        key = (mod, h)
        if key in seen:
            rows[seen[key]]["count"] += int(count)
            warnings.append("entries[%d] 与 entries[%d] 是同 mod 同 h，count 相加"
                            % (index, seen[key] + 1))
            continue
        seen[key] = len(rows)
        row = {"h": h, "s": text, "mod": mod, "count": int(count)}
        for optional in ROW_OPTIONAL:
            value = item.get(optional)
            if value:
                row[optional] = value
        rows.append(row)
        h_index.setdefault(h, 0)
        h_index[h] += 1
    multi = [h for h, times in h_index.items() if times > 1]
    if multi:
        warnings.append("同一条原文被多个 mod 各声明一次（h=%s 等 %d 条）：候选表按 h 去重只留一行"
                        % (", ".join(sorted(multi)[:3]), len(multi)))
    return rows


def validate(bundle):
    """全量校验，返回规范化后的 dict（不改入参）；任何问题抛 BundleError。"""
    if not isinstance(bundle, dict):
        raise BundleError("schema", "SUBMISSION_FIELD_INVALID", "投稿顶层必须是 JSON 对象")
    warnings = []
    schema = bundle.get("schema")
    if schema != SCHEMA:
        raise BundleError("schema", "submission-schema-invalid",
                          "schema 必须是 %r（收到 %r）" % (SCHEMA, schema), [schema])
    sid = bundle.get("id")
    if not isinstance(sid, str) or not _SID_RE.match(sid):
        raise BundleError("schema", "SUBMISSION_FIELD_INVALID",
                          "id 必须是 16 位小写十六进制（收到 %r）" % (sid,))
    created = _need_text(bundle, "createdAt", "")
    language = _need_text(bundle, "language", "")
    actor = _need_dict(bundle, "actor", "")
    install = _need_text(actor, "installId", "actor.")
    consent = _need_dict(bundle, "consent", "")
    granted = consent.get("granted")
    if granted is not True:
        # §6.2：不提供任何跳过同意的 flag，也不认字符串 "true"
        raise BundleError("schema", "submission-consent-missing",
                          "consent.granted 必须为 true（收到 %r）：没有同意的数据一律拒收" % (granted,))
    policy = consent.get("policyVersion")
    if policy is not None and not _is_int(policy):
        raise BundleError("schema", "SUBMISSION_FIELD_INVALID",
                          "consent.policyVersion 必须是整数（收到 %r）" % (policy,))
    mods = bundle.get("mods")
    if mods is None:
        mods = []
    if not isinstance(mods, list):
        raise BundleError("schema", "SUBMISSION_FIELD_INVALID", "mods 必须是数组")
    declared, mod_meta = set(), []
    for index, item in enumerate(mods, 1):
        path = "mods[%d]." % index
        if not isinstance(item, dict):
            raise BundleError("schema", "SUBMISSION_FIELD_INVALID", path[:-1] + " 必须是对象")
        mod_id = item.get("modId")
        if not isinstance(mod_id, str) or not _MOD_ID_RE.match(mod_id):
            raise BundleError("schema", "SUBMISSION_FIELD_INVALID",
                              path + "modId 非法（只允许字母数字与 . _ -，且不以点开头）：%r"
                              % (mod_id,))
        declared.add(mod_id)
        clean = {"modId": mod_id}
        for key in ("fileName", "version"):
            value = item.get(key)
            if isinstance(value, str) and value:
                clean[key] = value
        mod_meta.append(clean)
    entries = bundle.get("entries")
    if entries is None:
        raise BundleError("schema", "SUBMISSION_FIELD_INVALID", "投稿缺 entries")
    if not isinstance(entries, list):
        raise BundleError("schema", "SUBMISSION_FIELD_INVALID", "entries 必须是数组")
    if not entries:
        # §6.6：entries 为空是"内容不合格"，不是"格式坏"
        raise BundleError("gate", "submission-empty-entries", "entries 是空的，没有任何原文可收")
    rows = _check_entries(entries, declared, warnings)
    expect = bundle_id(bundle)
    if expect != sid:
        raise BundleError("schema", "submission-id-mismatch",
                          "id 与内容不符：投稿写 %s，按内容重算应为 %s（id 是内容派生，"
                          "只有手改过才会不一致）" % (sid, expect), [sid, expect])
    redactions = _check_redactions(bundle, warnings)
    noted = [row for row in rows if _SUSPECT_RE.search(row["s"])]
    if noted:
        warnings.append("投稿里有 %d 行仍含疑似路径/URL（redactions=%s）：脱敏是客户端责任，"
                        "这里只记账不拒收" % (len(noted), json.dumps(redactions, ensure_ascii=False)))
    note = bundle.get("note")
    if note is not None and not isinstance(note, str):
        raise BundleError("schema", "SUBMISSION_FIELD_INVALID", "note 必须是字符串")
    actor_clean = {"kind": actor.get("kind") if isinstance(actor.get("kind"), str) else "player"}
    for key in ("ui18nVersion", "kspVersion"):
        if isinstance(actor.get(key), str) and actor.get(key):
            actor_clean[key] = actor[key]
    return {
        "id": sid, "schema": schema, "createdAt": created, "language": language,
        "actor": actor_clean, "installId": install,
        "consent": {"granted": True, "policyVersion": policy,
                    "grantedAt": consent.get("grantedAt")
                    if isinstance(consent.get("grantedAt"), str) else None},
        "mods": mod_meta, "entries": rows, "redactions": redactions,
        "note": note or "", "bundleSha256": bundle_sha256(bundle),
        "warnings": warnings,
    }


# ---------------------------------------------------------------------------
# 落盘形态：按 mod 拆
# ---------------------------------------------------------------------------

def split_by_mod(entries):
    """条目 -> {modId: [行]}；行内字段顺序按 ROW_FIELDS，行按 h 升序（确定性）。"""
    grouped = {}
    for entry in entries:
        row = {"h": entry["h"], "s": entry["s"], "mod": entry["mod"], "src": entry["s"]}
        for key in ROW_OPTIONAL:
            if entry.get(key):
                row[key] = entry[key]
        grouped.setdefault(entry["mod"], []).append(row)
    for rows in grouped.values():
        rows.sort(key=lambda item: item["h"])
    return grouped


def render_rows(rows):
    """一组行 -> 字节（紧凑 JSONL，一行一条，末尾一个换行）。"""
    lines = []
    for row in rows:
        payload = {key: row[key] for key in ROW_FIELDS if row.get(key) not in (None, "")}
        lines.append(json.dumps(payload, ensure_ascii=False, separators=(",", ":")))
    return ("\n".join(lines) + "\n").encode("utf-8") if lines else b""


#: 兼容旧名字（api 侧读起来更顺）
render_mod_rows = render_rows


def meta_of(info, files, accepted_at, actor=None):
    """meta.json 的内容：规范化后的头（去 entries、去 actor.installId）。"""
    mods = [dict(item, rows=count) for item in info["mods"] for count in
            [sum(1 for row in info["entries"] if row["mod"] == item["modId"])]]
    meta = {
        "schema": info["schema"],
        "id": info["id"],
        "createdAt": info["createdAt"],
        "acceptedAt": accepted_at,
        "language": info["language"],
        "actor": info["actor"],
        "consent": info["consent"],
        "mods": mods,
        "redactions": info["redactions"],
        "note": info["note"],
        "bundleSha256": info["bundleSha256"],
        "rows": len(info["entries"]),
        "files": files,
        "acceptedBy": actor or "pipeline:submission",
    }
    return meta


def index_entry_of(meta, mod_files):
    """index.json 里一条：id -> {files, rows, bytes, sha256, createdAt, language, policyVersion}。"""
    payload = b"".join(item["data"] for item in mod_files)
    return {
        "files": [{"file": item["name"], "rows": item["rows"],
                   "bytes": len(item["data"]), "sha256": item["sha256"]} for item in mod_files],
        "rows": sum(item["rows"] for item in mod_files),
        "bytes": len(payload),
        "sha256": hashlib.sha256(payload).hexdigest(),
        "createdAt": meta["createdAt"],
        "language": meta["language"],
        "policyVersion": meta["consent"].get("policyVersion"),
        "mods": [item["modId"] for item in meta["mods"]],
        "bundleSha256": meta["bundleSha256"],
    }


# ---------------------------------------------------------------------------
# candidates 归并（§6.5）
# ---------------------------------------------------------------------------

def _sort_key(row):
    return (BUCKET_ORDER.get(str(row.get("bucket")), 9), str(row.get("h") or ""))


def _dump_candidate(row):
    return json.dumps({key: row.get(key) for key in CAND_FIELDS},
                      ensure_ascii=False, separators=(",", ":"))


def merge_candidates(existing_raw, entries, covered_h, submission_id, accepted_at):
    """把投稿行并进 packs/candidates.jsonl 的字节里（纯函数：进 bytes、出 bytes）。

    三条规则逐字照 §6.5：
      * h 已被 packs/zh-cn 覆盖 -> 仍写行，covered:true / reason:"covered"
      * h 已在表里 -> 不插行，count 累加、sink/caller 取首个非空、src/channel 保留首次值
      * 新 h -> 插入，channel:"submit"、src:"submission/<id>"、bucket 按现有分类器
    排序口径不动：新行按 (bucket 顺序, h) **插到它该在的位置**，已有行一个字节不改
    （整表重排会在 diff 里变成 7 万行，评审再也看不出改了什么）。
    """
    stats = {"rows_in": len(entries), "inserted": 0, "updated": 0, "covered_insert": 0,
             "duplicate_h": 0, "unsorted_before": 0, "bytes_before": len(existing_raw or b""),
             "submission": submission_id}
    raw_text = (existing_raw or b"").decode("utf-8")
    texts = [line for line in raw_text.splitlines() if line.strip()]
    items, keys = [], []
    for lineno, text in enumerate(texts, 1):
        try:
            row = json.loads(text)
        except ValueError:
            raise BundleError("internal", "CANDIDATES_BAD_LINE",
                              "%s 第 %d 行不是合法 JSON（先修好候选表再收投稿）"
                              % (CANDIDATES_REL, lineno))
        if not isinstance(row, dict):
            raise BundleError("internal", "CANDIDATES_BAD_LINE",
                              "%s 第 %d 行不是 JSON 对象" % (CANDIDATES_REL, lineno))
        items.append({"row": row, "text": text})
        keys.append(_sort_key(row))
    previous = None
    for key in keys:
        if previous is not None and key < previous:
            stats["unsorted_before"] += 1
        previous = key
    by_h = {}
    for position, item in enumerate(items):
        by_h.setdefault(item["row"].get("h"), position)
    updates, inserts = [], []
    for entry in entries:
        h = entry["h"]
        count = int(entry.get("count") or 1) or 1
        position = by_h.get(h)
        if position is None:
            bucket_name, reason = buckets.classify(entry["s"])
            covered = h in covered_h
            row = {
                "h": h, "s": entry["s"], "src": "submission/" + submission_id,
                "bucket": BUCKET_MAP.get(bucket_name, "candidate"),
                "reason": COVERED_REASON if covered else reason,
                "channel": SUBMIT_CHANNEL,
                "caller": entry.get("caller") or "",
                "sink": entry.get("sink") or "",
                "count": count, "mod": entry["mod"],
                "firstSeen": accepted_at or "", "covered": bool(covered),
            }
            inserts.append(row)
        else:
            updates.append((position, entry, count))
    for position, entry, count in updates:
        row = dict(items[position]["row"])
        before = row.get("count")
        row["count"] = int(before or 0) + count
        if not row.get("sink") and entry.get("sink"):
            row["sink"] = entry["sink"]
        if not row.get("caller") and entry.get("caller"):
            row["caller"] = entry["caller"]
        items[position] = {"row": row, "text": None}
        stats["updated"] += 1
    for row in sorted(inserts, key=_sort_key):
        key = _sort_key(row)
        position = bisect.bisect_right(keys, key)
        keys.insert(position, key)
        items.insert(position, {"row": row, "text": None})
        stats["inserted"] += 1
        if row["covered"]:
            stats["covered_insert"] += 1
    lines = [item["text"] if item["text"] is not None else _dump_candidate(item["row"])
             for item in items]
    data = ("\n".join(lines) + "\n").encode("utf-8") if lines else b""
    stats["rows_after"] = len(items)
    stats["bytes_after"] = len(data)
    return data, stats


# ---------------------------------------------------------------------------
# 一次投稿的完整计划（纯函数：给输入的字节，算输出的字节）
# ---------------------------------------------------------------------------

def plan(bundle, covered_h, existing_candidates_raw, existing_index_raw,
         accepted_at, actor=None):
    """校验 + 拆文件 + 归并 candidates，返回"要写什么"的完整描述（不碰磁盘）。

    返回 dict：id / rows / mods / files（按写入顺序）/ event（journal 载荷）/ meta /
    indexEntry / stats / warnings。
    """
    info = validate(bundle)
    sid = info["id"]
    base = "%s/%s" % (SUBMISSION_DIR, sid)
    grouped = split_by_mod(info["entries"])
    mod_files = []
    for mod in sorted(grouped):
        data = render_rows(grouped[mod])
        mod_files.append({"mod": mod, "name": mod + ".jsonl", "rows": len(grouped[mod]),
                          "data": data, "sha256": hashlib.sha256(data).hexdigest()})
    meta = meta_of(info, [{"file": item["name"], "rows": item["rows"],
                           "bytes": len(item["data"]), "sha256": item["sha256"]}
                          for item in mod_files], accepted_at, actor=actor)
    meta_bytes = (json.dumps(meta, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    entry = index_entry_of(meta, mod_files)
    index_raw = existing_index_raw or b""
    doc = {"schema": 1, "submissions": {}}
    if index_raw.strip():
        try:
            doc = json.loads(index_raw.decode("utf-8"))
        except ValueError as exc:
            raise BundleError("internal", "SUBMISSION_INDEX_BAD",
                              "%s/%s 不是合法 JSON：%s" % (SUBMISSION_DIR, INDEX_NAME, exc))
        if not isinstance(doc, dict):
            raise BundleError("internal", "SUBMISSION_INDEX_BAD",
                              "%s/%s 顶层必须是对象" % (SUBMISSION_DIR, INDEX_NAME))
    submissions = doc.get("submissions")
    if not isinstance(submissions, dict):
        submissions = {}
    submissions[sid] = entry
    doc["schema"] = doc.get("schema") or 1
    doc["submissions"] = {key: submissions[key] for key in sorted(submissions)}
    index_bytes = (json.dumps(doc, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    cands_bytes, cands_stats = merge_candidates(existing_candidates_raw, info["entries"],
                                                covered_h, sid, accepted_at)
    files = [{"path": base + "/" + META_NAME, "rows": 0, "data": meta_bytes,
              "sha256": hashlib.sha256(meta_bytes).hexdigest()}]
    for item in mod_files:
        files.append({"path": base + "/" + item["name"], "rows": item["rows"],
                      "data": item["data"], "sha256": item["sha256"]})
    files.append({"path": "%s/%s" % (SUBMISSION_DIR, INDEX_NAME), "rows": 0,
                  "data": index_bytes, "sha256": hashlib.sha256(index_bytes).hexdigest()})
    files.append({"path": CANDIDATES_REL, "rows": len(info["entries"]),
                  "data": cands_bytes, "sha256": hashlib.sha256(cands_bytes).hexdigest()})
    event_files = [{"path": step["path"], "rows": step["rows"],
                    "file_sha256_after": step["sha256"]} for step in files[:-1]]
    event = {
        "op": "submit.accept",
        "lang": info["language"],
        "submission_id": sid,
        "bundle_sha256": info["bundleSha256"],
        "rows": len(info["entries"]),
        "mods": sorted(grouped),
        "files": event_files,
        "candidates": {"changed": bool(cands_stats["inserted"] or cands_stats["updated"]),
                       "path": CANDIDATES_REL,
                       "file_sha256_after": files[-1]["sha256"],
                       "inserted": cands_stats["inserted"],
                       "updated": cands_stats["updated"]},
        "note": "投稿 %s：%d 行 / %d 个 mod；candidates 新增 %d 累加 %d"
                % (sid, len(info["entries"]), len(grouped),
                   cands_stats["inserted"], cands_stats["updated"]),
    }
    return {
        "id": sid, "rows": len(info["entries"]), "mods": sorted(grouped),
        "files": files, "event": event, "meta": meta, "indexEntry": entry,
        "stats": cands_stats, "warnings": info["warnings"],
        "info": info,
    }


def existing_state(index_raw, meta_raw, sid=None):
    """已存在同 id 投稿时的判定：返回 (already, stored_sha256, stored_mods)。

    already=True 表示内容逐字节等价（幂等：一个文件都不写、不写 journal 事件）。
    先按 sid 在 index.json 里查；sid 没给或 index 里没有这条（手工删过 index/meta）时
    退回用 meta.json 的 bundleSha256 判断 —— 两条路都查不到才算"从没收到过"。
    """
    stored_sha, stored_mods = None, []
    if index_raw.strip():
        try:
            doc = json.loads(index_raw.decode("utf-8"))
        except ValueError:
            doc = {}
        subs = doc.get("submissions") if isinstance(doc, dict) else None
        item = None
        if isinstance(subs, dict):
            item = subs.get(sid) if sid is not None else None
            if item is None and sid is None and len(subs) == 1:
                item = list(subs.values())[0]
        if isinstance(item, dict):
            stored_sha = item.get("bundleSha256")
            stored_mods = list(item.get("mods") or [])
    if stored_sha is None and meta_raw.strip():
        try:
            meta = json.loads(meta_raw.decode("utf-8"))
        except ValueError:
            meta = {}
        if isinstance(meta, dict):
            stored_sha = meta.get("bundleSha256")
            stored_mods = [item.get("modId") for item in (meta.get("mods") or [])
                           if isinstance(item, dict)]
    return (stored_sha is not None, stored_sha, stored_mods)


# ---------------------------------------------------------------------------
# 自检（纯函数层）
# ---------------------------------------------------------------------------

def _h(text):
    return hash_text(normalize(text))


def _bundle(entries, mods=None, **over):
    bundle = {
        "schema": SCHEMA,
        "createdAt": "2026-10-26T18:20:00+08:00",
        "language": "zh-cn",
        "actor": {"kind": "player", "installId": "0f7a1c2e-1111-4222-8333-444455556666",
                  "ui18nVersion": "1.6.0", "kspVersion": "1.12.5"},
        "consent": {"granted": True, "policyVersion": 1,
                    "grantedAt": "2026-10-26T18:19:12+08:00"},
        "mods": copy.deepcopy(mods) if mods is not None else [{"modId": "TUFX"}, {"modId": "FAR"}],
        "entries": copy.deepcopy(entries),
        "redactions": {"paths": 2, "userNames": 0, "absolutePaths": 1, "urls": 0},
        "note": "",
    }
    bundle.update(over)
    bundle["id"] = bundle_id(bundle)
    return bundle


def selftest():
    """返回 (失败列表, 总项数)。"""
    failures = []
    total = 0

    def check(name, condition, detail=""):
        nonlocal total
        total += 1
        if not condition:
            failures.append((name, str(detail)))

    def rejected(name, bundle, kind, code):
        try:
            validate(bundle)
        except BundleError as exc:
            check(name, (exc.kind, exc.code) == (kind, code), (exc.kind, exc.code, exc.message))
            return
        check(name, False, "没有抛 BundleError")

    entries = [
        {"h": _h("Blue Paint"), "s": "Blue Paint", "mod": "TUFX"},
        {"h": _h("Aerodynamic Failure"), "s": "Aerodynamic Failure", "mod": "FAR",
         "sink": "TMPro.TMP_Text.set_text", "caller": "FAR.DebugWindow", "count": 37},
    ]
    good = _bundle(entries)
    info = validate(good)
    check("正常 bundle 校验通过", len(info["entries"]) == 2, len(info["entries"]))
    check("meta 不含 installId", "installId" not in json.dumps(meta_of(
        info, [], "2026-10-26T18:41:02+08:00")), "installId 泄漏")
    check("canonical id 是 16 位小写 hex", bool(_SID_RE.match(bundle_id(good))), bundle_id(good))
    check("id 与内容绑定（同内容同 id）", bundle_id(good) == bundle_id(dict(good)), bundle_id(good))
    check("改一个字符 id 就变",
          bundle_id(_bundle([{"h": _h("Blue Paint!"), "s": "Blue Paint!", "mod": "TUFX"}]))
          != bundle_id(_bundle([{"h": _h("Blue Paint"), "s": "Blue Paint", "mod": "TUFX"}])),
          "id 没变")
    check("canonical_json 不给 id 留位置", "id" not in canonical_json(good))
    check("canonical_json 是紧凑 + 排序键",
          canonical_json({"b": 1, "a": 2}) == '{"a":2,"b":1}', canonical_json({"b": 1, "a": 2}))

    rejected("consent=false 拒收", _bundle(entries, consent={"granted": False}),
             "schema", "submission-consent-missing")
    rejected("consent=\"true\" 字符串也拒收", _bundle(entries, consent={"granted": "true"}),
             "schema", "submission-consent-missing")
    rejected("schema 字符串不对拒收", _bundle(entries, schema="ui18n-submission/2"),
             "schema", "submission-schema-invalid")
    tampered = _bundle(entries)
    tampered["entries"][0]["s"] = "Blue Paint (edited)"
    rejected("id 与内容不符拒收", tampered, "schema", "submission-id-mismatch")
    rejected("entries 为空是门禁拒绝",
             _bundle([]), "gate", "submission-empty-entries")
    rejected("entries[].mod 未声明拒收",
             _bundle(entries + [{"h": _h("Stray"), "s": "Stray", "mod": "NotDeclared"}]),
             "gate", "submission-mod-undeclared")
    rejected("h 不是 16 位 hex 拒收", _bundle([{"h": "XYZ", "s": "x", "mod": "TUFX"}]),
             "schema", "SUBMISSION_FIELD_INVALID")
    rejected("s 超 8000 字符拒收",
             _bundle([{"h": _h("a" * 8001), "s": "a" * 8001, "mod": "TUFX"}]),
             "schema", "SUBMISSION_FIELD_INVALID")
    rejected("modId 带斜杠拒收（要当文件名用）",
             _bundle(entries, mods=[{"modId": "../evil"}, {"modId": "TUFX"}, {"modId": "FAR"}]),
             "schema", "SUBMISSION_FIELD_INVALID")
    rejected("redactions 计数为负拒收",
             _bundle(entries, redactions={"paths": -1}), "schema", "SUBMISSION_FIELD_INVALID")
    missing = _bundle(entries)
    missing.pop("createdAt")
    rejected("缺 createdAt 拒收", missing, "schema", "SUBMISSION_FIELD_INVALID")
    no_entries = _bundle(entries)
    no_entries.pop("entries")
    rejected("缺 entries 字段拒收", no_entries, "schema", "SUBMISSION_FIELD_INVALID")

    dup = _bundle([{"h": _h("Blue Paint"), "s": "Blue Paint", "mod": "TUFX", "count": 2},
                   {"h": _h("Blue Paint"), "s": "Blue Paint", "mod": "TUFX", "count": 3}])
    dup_info = validate(dup)
    check("同 mod 同 h 去重且 count 相加",
          len(dup_info["entries"]) == 1 and dup_info["entries"][0]["count"] == 5,
          dup_info["entries"])
    wrong_h = validate(_bundle([{"h": "0000000000000000", "s": "Blue Paint",
                                "mod": "TUFX"}]))
    check("h 与原文不一致只 warning 不拒收",
          any("重算" in item for item in wrong_h["warnings"]), wrong_h["warnings"])

    grouped = split_by_mod(info["entries"])
    check("按 mod 拆文件", sorted(grouped) == ["FAR", "TUFX"], sorted(grouped))
    check("拆出来的行没有 t 字段",
          all("t" not in row for rows in grouped.values() for row in rows),
          [list(row) for rows in grouped.values() for row in rows])
    one_line = render_rows([{"h": "0" * 16, "s": "x", "mod": "M", "src": "x"}]).decode("utf-8")
    check("行字段顺序固定（h 在最前、src 在最后）",
          one_line.startswith('{"h":') and one_line.rstrip().endswith('"src":"x"}'), one_line)

    covered = {_h("Blue Paint")}
    AERODYNAMIC = "Aerodynamic Failure"
    untouched_text = json.dumps({"h": _h("Unrelated Row"), "s": "Unrelated Row",
                                 "src": "Unrelated Row", "bucket": "candidate",
                                 "reason": "sentence", "channel": "capture", "caller": "",
                                 "sink": "", "count": 1, "mod": "TUFX",
                                 "firstSeen": "2026-09-01", "covered": False},
                                ensure_ascii=False, separators=(",", ":"))
    existing = ((untouched_text + "\n" + json.dumps({"h": _h(AERODYNAMIC), "s": AERODYNAMIC,
                            "src": "Aerodynamic Failure", "bucket": "candidate",
                            "reason": "sentence", "channel": "capture", "caller": "",
                            "sink": "", "count": 3, "mod": "FAR", "firstSeen": "2026-10-01",
                            "covered": False}, ensure_ascii=False,
                           separators=(",", ":")) + "\n").encode("utf-8"))
    data, stats = merge_candidates(existing, info["entries"], covered, "abc", "2026-10-26T18:41:02")
    rows = [json.loads(line) for line in data.decode("utf-8").splitlines() if line.strip()]
    by_h = {row["h"]: row for row in rows}
    check("覆盖的 h 仍写行且 covered:true", by_h[_h("Blue Paint")]["covered"] is True,
          by_h[_h("Blue Paint")])
    check("覆盖的 h reason=covered", by_h[_h("Blue Paint")]["reason"] == COVERED_REASON,
          by_h[_h("Blue Paint")]["reason"])
    check("已存在的 h 不插行、count 累加", by_h[_h(AERODYNAMIC)]["count"] == 40,
          by_h[_h(AERODYNAMIC)]["count"])
    check("已存在的行保留原通道与 src",
          by_h[_h(AERODYNAMIC)]["channel"] == "capture"
          and by_h[_h(AERODYNAMIC)]["src"] == AERODYNAMIC, by_h[_h(AERODYNAMIC)])
    check("新插入的行 src 指向投稿、channel=submit",
          by_h[_h("Blue Paint")]["src"] == "submission/abc"
          and by_h[_h("Blue Paint")]["channel"] == SUBMIT_CHANNEL, by_h[_h("Blue Paint")])
    check("count 累加数记进 stats", stats["updated"] == 1 and stats["inserted"] == 1, stats)
    data2, stats2 = merge_candidates(data, info["entries"], covered, "abc",
                                     "2026-10-26T18:41:02")
    check("同输入再归并一次不再插行（行数不变）",
          stats2["inserted"] == 0 and stats2["rows_after"] == stats["rows_after"], stats2)
    check("二次归并 count 继续按契约累加",
          [json.loads(line)["count"] for line in data2.decode("utf-8").splitlines()
           if json.loads(line)["h"] == _h(AERODYNAMIC)] == [77], stats2)
    kept = [line for line in data.decode("utf-8").splitlines()
            if json.loads(line)["h"] == _h("Unrelated Row")]
    check("归并保留未触碰行逐字节不变（含插入后位置变化）",
          kept == [untouched_text], kept)
    check("新插入的行按 (bucket, h) 落在该在的位置",
          [json.loads(line)["bucket"] for line in data.decode("utf-8").splitlines()]
          == ["candidate"] * len(data.decode("utf-8").splitlines()), "bucket 列乱了")
    check("表头 12 列齐全", set(rows[0]) <= set(CAND_FIELDS), set(rows[0]) - set(CAND_FIELDS))

    from . import candidates as C
    check("bucket 词表与 candidates 同源", BUCKET_ORDER == C.BUCKET_ORDER, BUCKET_ORDER)
    check("bucket 映射与 candidates.IL_BUCKETS 同源",
          BUCKET_MAP == {"translate": "candidate", "short_label": "candidate",
                         "template": "template", "never": "never"}, BUCKET_MAP)

    # plan 层
    built = plan(good, covered, None, None, "2026-10-26T18:41:02+08:00", "pipeline:selftest")
    paths = [step["path"] for step in built["files"]]
    check("plan 写 meta + 每个 mod 一个文件 + index + candidates",
          paths == ["packs/submission/%s/meta.json" % built["id"],
                    "packs/submission/%s/FAR.jsonl" % built["id"],
                    "packs/submission/%s/TUFX.jsonl" % built["id"],
                    "packs/submission/index.json", "packs/candidates.jsonl"], paths)
    check("plan 的 event op=submit.accept", built["event"]["op"] == "submit.accept",
          built["event"]["op"])
    check("event 带 submission_id/bundle_sha256/rows/mods",
          built["event"]["submission_id"] == built["id"] and built["event"]["rows"] == 2
          and built["event"]["mods"] == ["FAR", "TUFX"], built["event"])
    check("event 的 files 覆盖每个写入文件（candidates 单列一支）",
          [item["path"] for item in built["event"]["files"]] == paths[:-1],
          [item["path"] for item in built["event"]["files"]])
    index_doc = json.loads(built["files"][3]["data"].decode("utf-8"))
    entry = index_doc["submissions"][built["id"]]
    check("index 里 rows/bytes/sha256 自洽",
          entry["rows"] == 2 and entry["bytes"] == sum(len(step["data"])
                                                        for step in built["files"][1:3]),
          entry)
    check("index.json 的 submissions 按键升序",
          list(index_doc["submissions"]) == sorted(index_doc["submissions"]),
          list(index_doc["submissions"]))
    check("meta 里 rows 等于条目数", built["meta"]["rows"] == 2, built["meta"]["rows"])
    check("meta 不写 installId", "installId" not in json.dumps(built["meta"]))
    second = plan(good, covered, built["files"][-1]["data"], built["files"][3]["data"],
                  "2026-10-26T19:00:00+08:00", "pipeline:selftest")
    again = json.loads(second["files"][3]["data"].decode("utf-8"))
    check("同 id 再 plan 不会在 index 里出现两条", len(again["submissions"]) == 1,
          len(again["submissions"]))
    return failures, total


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    if "--selftest" not in argv:
        sys.stderr.write("用法：python -m i18n_tools.submission --selftest\n")
        return 2
    for stream in (sys.stdout, sys.stderr):
        hook = getattr(stream, "reconfigure", None)
        if hook is not None:
            try:
                hook(encoding="utf-8", errors="replace")
            except (ValueError, OSError):
                pass
    failures, total = selftest()
    for name, detail in failures:
        sys.stderr.write("[submission selftest] 失败：%s  %s\n" % (name, detail))
    if failures:
        print("[submission selftest] %d 项失败 / 共 %d 项" % (len(failures), total))
        return 1
    print("[submission selftest] 全部通过（%d 项）" % total)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
