"""translate_il.py -- IL 采集 / 运行时捕获候选 -> packs/zh-cn/<mod>.il.jsonl

输入（data-engineer task-32 产出；本工具也吃自造 fixture）：
  packs/il-translatable.jsonl    {asm,type,method,ilOffset,sink,text,h,n,bucket,reason,mods}
  packs/capture-candidates.jsonl {h,n,s,sink,caller,count,firstSeen,bucket,reason,mods}
两个文件字段名不同（text vs s），--source il|capture|auto 或按字段自动识别。

输出：packs/zh-cn/<modId>.il.jsonl，字段与 Core DictPack 完全一致 {h,n,s,t,mod,ctx}，
h = fnv1a64(normalize(s)) 的 16 位小写 hex（复用 i18n_tools/fnv.py，不自己实现）。

运行时契约（读源码确认）：
  src/UI18n.Runtime/Translator.cs:207 / :260 -> _pack.TryGet(hash, normalized, out hit, out level)
  src/UI18n.Core/Dict/DictPack.cs:118-129   -> 三参重载把 context 传成 null
  src/UI18n.Core/Dict/DictPack.cs:160-172   -> context 为空时跳过全部 ctx 级条目
  src/UI18n.Core/Dict/DictEntry.cs:24-27    -> ctx 非空 => Level = Ctx
所以 **ctx 非空的行运行时永远不命中**。默认 --ctx-mode drop（provenance 只写 sidecar
reports/il-provenance-<ts>.jsonl），--ctx-mode keep 保留 ctx 供未来带 context 的运行时使用，
报告里会写 runtime_reachable=false 并打印警告。

门禁与 translate.py 同口径（G1..G8 复用 translate.gate），另加两条：
  - bucket=never / 去标签后不足 2 个字母的候选直接拒（no-letters）；
  - short_label 允许短译，但 G6 下界仍生效，被拒记 gated_ratio 并按 untranslated 处理。
"""

from __future__ import annotations

import argparse
import concurrent.futures
import json
import re
import sys
import time
import traceback
from pathlib import Path

from . import llm as llmlib
from . import mask as masklib
from . import skeleton as SK
from . import translate as T
from .fnv import hash_text
from .textnorm import has_cjk, is_key_like, is_pure_number, is_url_only, normalize
from .report import write_report

REPO = Path(__file__).resolve().parents[2]
PACKS_ROOT = REPO / "packs"
OUT_DIR = PACKS_ROOT / "zh-cn"
GLOSSARY_PATH = PACKS_ROOT / "glossary.jsonl"
CONVENTIONS_PATH = PACKS_ROOT / "conventions.jsonl"
OVERRIDES_PATH = PACKS_ROOT / "overrides.jsonl"
IL_INPUT = PACKS_ROOT / "il-translatable.jsonl"
CAPTURE_INPUT = PACKS_ROOT / "capture-candidates.jsonl"
CACHE_PATH = REPO / "reports" / "cache" / "il-translations.jsonl"
REPORTS_DIR = REPO / "reports"
FIXTURE_PATH = REPORTS_DIR / "il-fixture.jsonl"

#: 缓存键里的 prompt 版本（与 translate.py 的 3 分开：IL 的 user message 多 role 提示，
#: 复用 cfg 缓存会让同一句在 cfg 与 IL 两个语境下拿到同一译法，但代价是可能不合适）
PROMPT_VERSION_IL = "il1"

DEFAULT_MAX_CHARS = 6000
OUT_SUFFIX = ".il.jsonl"
ALLOWED_FIELDS = ("h", "n", "s", "t", "mod", "ctx", "p")
MIN_LETTERS = 2

#: 捕获通道的骨架桶：s 是「数字换成 #」的骨架串，译文必须逐字保留 # 的个数。
#: 运行时仍按原样匹配（h = fnv1a(normalize(骨架))），p=1 只是审计标记。
PATTERN_BUCKET = "pattern"

#: 九门禁：G1..G8 复用 translate.GATES（不改那个文件——它属于 cfg 通道，
#: 口径一改 218 例单测跟着漂），G9（骨架占位符个数）只对 pattern 桶生效。
GATE_NAMES = tuple(T.GATES) + ("G9",)

#: 骨架批上限（比 short_label 的 120 小：一条骨架要判 # 个数，长批里模型更容易漏）。
PATTERN_BATCH_CAP = 60

#: 批内含骨架时追加到 system prompt 的额外约束（不改 T.system_prompt 的既有口径）。
PATTERN_PROMPT = "\n".join([
    "【本批含骨架字符串（数字占位符版）】",
    "1. 原文里的 # 是数字占位符，运行时会被真实数字（可能带单位）替换。"
    "译文里 # 的个数必须与原文逐个相同；位置可随中文语序移动，# 前后的空格可自行调整。",
    "2. 绝不把 # 替换成具体数字，绝不合并相邻的两个 #，绝不新造 #。",
    "3. 其余规则不变：<<1>> 一类编号、{0} %s %d、TMP 标签与属性值、哨兵 <<M0>> 一律逐字节保留。",
]) + "\n"

_LETTER_RE = re.compile(r"[A-Za-z]")
_TAG_RE = re.compile(r"<[^<>]{0,80}>")
_ESCAPE_RE = re.compile(r"\\(?:u[0-9a-fA-F]{4}|x[0-9a-fA-F]{2}|[nrtbfva0])")
_DLL_RE = re.compile(r"\.dll$", re.IGNORECASE)
# lead 裁定（m01857）：程序集名自带版本号要归一，规则写死 —— 尾部 >=2 段数字才剥
_VERSION_SUFFIX_RE = re.compile(r"(?:\.[0-9]+){2,}$")

CTX_MODES = ("drop", "keep", "sidecar")


# ---------------------------------------------------------------------------
# 读取候选
# ---------------------------------------------------------------------------

def iter_jsonl(path):
    path = Path(path)
    if not path.exists():
        return
    with path.open("r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except ValueError:
                continue
            if isinstance(row, dict):
                yield row


def asm_version_base(base):
    r"""剥掉尾部版本段：ModuleManager.4.2.3 -> ModuleManager。

    lead 裁定（m01857）：真实程序集叫 ModuleManager.4.2.3.dll，版本号留在 mod 身份里会让
    packs/index.json 出现版本号、下次升级就换名。规则固定：尾部匹配 (\.[0-9]+){2,} 才整段剥掉；
    Foo.2 只有一段数字 -> 保留（可能是真名，不能猜）。
    """
    stripped = _VERSION_SUFFIX_RE.sub("", base or "").strip()
    return stripped or (base or "")


def asm_base(name):
    """asm / mods 名 -> mod 身份：去目录、去 .dll 后缀、去尾部版本号。

    lead m01753 硬口径：输出文件名与行内 mod 必须是同一个 asmBase（TUFX 而不是 TUFX.dll），
    否则 packs/index.json 的 mod 排序会出现两套命名、mod 与文件名对不上。
    """
    text = str(name or "").strip()
    if not text:
        return ""
    text = text.replace("\\", "/").rsplit("/", 1)[-1]
    return asm_version_base(_DLL_RE.sub("", text).strip())


def mod_from_asm(asm):
    return asm_base(asm) or "unknown"


def kind_of(row):
    """il-translatable 用 text，capture-candidates 用 s。"""
    has_text = isinstance(row.get("text"), str)
    has_s = isinstance(row.get("s"), str)
    if has_text and not has_s:
        return "il"
    if has_s and not has_text:
        return "capture"
    if "ilOffset" in row or "asm" in row:
        return "il"
    if "sink" in row or "caller" in row:
        return "capture"
    return "il" if has_text else "capture"


def ctx_of(row, kind):
    if kind == "il":
        return "il:{}|{}|{}|{}".format(
            row.get("asm", ""), row.get("type", ""), row.get("method", ""), row.get("ilOffset", ""))
    return "capture:{}|{}".format(row.get("sink", ""), row.get("caller", ""))


def asm_normalized_list(rows, kind="il"):
    """报告用：列出被归一掉版本号的程序集（asm -> base -> mod）。

    只记录 base != mod 的，避免把 48 个正常程序集全塞进报告。
    """
    seen = {}
    for row in rows:
        values = []
        raw = row.get("mods")
        if isinstance(raw, list):
            values = [str(m) for m in raw if str(m).strip()]
        elif isinstance(raw, str) and raw.strip():
            values = [raw]
        elif isinstance(row.get("mod"), str) and row["mod"].strip():
            values = [row["mod"]]
        elif isinstance(row.get("asm"), str) and row["asm"].strip():
            values = [row["asm"]]
        for value in values:
            base = _DLL_RE.sub("", value.replace(chr(92), "/").rsplit("/", 1)[-1]).strip()
            mod = asm_version_base(base)
            if base and mod and base != mod:
                seen[base] = {"asm": value, "base": base, "mod": mod}
    return [seen[key] for key in sorted(seen)]


def mods_of(row, kind):
    raw = row.get("mods")
    if isinstance(raw, list):
        mods = [asm_base(m) for m in raw if str(m).strip()]
        mods = [m for m in mods if m]
        if mods:
            return mods
    elif isinstance(raw, str) and raw.strip():
        return [asm_base(raw)]
    single = row.get("mod")
    if isinstance(single, str) and single.strip():
        return [asm_base(single)]
    if kind == "il":
        return [mod_from_asm(row.get("asm"))]
    return ["capture"]


def bucket_of(row, text):
    bucket = (row.get("bucket") or "").strip().lower()
    if bucket in ("never", "translate", "short_label", PATTERN_BUCKET):
        return bucket
    # 兼容别名：task-45 的骨架行若标成 skeleton 也能进骨架通道（口径只有一个）
    if bucket == "skeleton":
        return PATTERN_BUCKET
    if bucket:
        return bucket
    return "short_label" if len(text) <= 24 else "translate"


def load_candidates(paths, source="auto"):
    """读一个或多个候选文件，返回统一结构。"""
    out = []
    for path in paths:
        if not path:
            continue
        for row in iter_jsonl(path):
            kind = kind_of(row)
            if source in ("il", "capture") and kind != source:
                continue
            text = row.get("text") if kind == "il" else row.get("s")
            if not isinstance(text, str):
                text = row.get("text") if isinstance(row.get("text"), str) else row.get("s")
            if not isinstance(text, str):
                continue
            bucket = bucket_of(row, text)
            pattern_no_slots = False
            if bucket == PATTERN_BUCKET:
                # 骨架通道的键是**骨架**本身：运行时先 TextSkeleton.TryBuild 再按骨架哈希查表
                # （DictPack.TryGetPattern(hash(skeleton), skeleton) → FillBack 填回数字），
                # 所以送翻与落库的原文必须是骨化后的文本，不是带真实数字的具体实例。
                skeleton_text, _slots = SK.skeleton(text)
                if skeleton_text is not None:
                    text = skeleton_text           # 具体实例（带真实数字）→ 归一成骨架
                elif SK.placeholders(text) > 0:
                    pass                           # 上游已骨化（task-45 起 capture_ingest 直接产骨架）：原样用
                else:
                    pattern_no_slots = True        # 既无数字段也无 #：不是骨架，运行时永不算骨架 → 不写死行
            out.append({
                "kind": kind,
                "text": text,
                "h_given": row.get("h"),
                "bucket": bucket,
                "pattern_no_slots": pattern_no_slots,
                "mods": mods_of(row, kind),
                "ctx": ctx_of(row, kind),
                "path": str(path),
            })
    return out


# ---------------------------------------------------------------------------
# 归一 + 去重（同一 h 全库只能一行）
# ---------------------------------------------------------------------------

def dedupe(cands):
    """按 h 分组。返回 (groups, conflicts)。

    conflicts 记「同 h 但原文不同字节」（归一化相同、原始写法不同），
    这类只能保留一行，全部候选写进报告。
    """
    groups = {}
    order = []
    for cand in cands:
        text = cand["text"]
        normalized = normalize(text)
        if not normalized:
            continue
        h = hash_text(normalized)
        group = groups.get(h)
        if group is None:
            group = groups[h] = {
                "h": h,
                "n": normalized,
                "text": text,
                "variants": [text],
                "mods": list(dict.fromkeys(cand["mods"])),
                "ctxs": [cand["ctx"]],
                "kinds": [cand["kind"]],
                "bucket": cand["bucket"],
                "pattern_no_slots": cand.get("pattern_no_slots", False),
                "count": 1,
                "h_given": cand["h_given"],
            }
            order.append(h)
            continue
        group["count"] += 1
        if text not in group["variants"]:
            group["variants"].append(text)
        for mod in cand["mods"]:
            if mod not in group["mods"]:
                group["mods"].append(mod)
        if len(group["ctxs"]) < 8 and cand["ctx"] not in group["ctxs"]:
            group["ctxs"].append(cand["ctx"])
        if cand["kind"] not in group["kinds"]:
            group["kinds"].append(cand["kind"])
        if cand.get("pattern_no_slots"):
            group["pattern_no_slots"] = True
        if cand["bucket"] == "translate":
            group["bucket"] = "translate"
        elif cand["bucket"] == "never":
            group["bucket"] = "never"

    conflicts = []
    for h in order:
        group = groups[h]
        if len(group["variants"]) > 1:
            conflicts.append({
                "h": h,
                "variants": group["variants"],
                "mods": group["mods"],
                "kept": group["variants"][0],
            })
        group["host"] = sorted(group["mods"])[0] if group["mods"] else "unknown"
    return groups, conflicts


# ---------------------------------------------------------------------------
# 预筛
# ---------------------------------------------------------------------------

def visible_letters(text):
    """去掉哨兵/占位符/格式符/标签/转义后剩下的 ASCII 字母数。"""
    body = masklib.SENTINEL_RE.sub(" ", text or "")
    body = masklib.PLACEHOLDER_RE.sub(" ", body)
    body = masklib.FORMAT_RE.sub(" ", body)
    body = _TAG_RE.sub(" ", body)
    body = _ESCAPE_RE.sub(" ", body)
    return len(_LETTER_RE.findall(body))


def prefilter(group, existing=None, override_keys=None):
    """返回拒绝原因（None = 送去翻译）。顺序即优先级。

    override_keys：人工覆盖命中的 h 集合。覆盖优先于后面所有启发式跳过 ——
    单 token 真 UI 文案（Iterations / Pathfinding / EarlyRebuild）会被 no-words 误杀，
    而人工表里已经有负责的译文，不能因为启发式规则丢掉（实测漏 8 条）。
    """
    text = group["text"]
    stripped = text.strip()
    if not stripped:
        return "empty"
    if has_cjk(text):
        return "already-cjk"
    # 人工覆盖豁免：但仍不许放过真正的键引用（#autoLOC_500 这类无空白的紧凑标识符）
    if override_keys and group["h"] in override_keys and not is_key_like(stripped):
        return None
    # 骨架天然可能以 # 开头（"# bit indices"、"#/#kN Thruster" 一类），
    # 那不是未解析的本地化键引用，key-ref 判定只对真实文案生效。
    if group["bucket"] != PATTERN_BUCKET and (stripped.startswith("#")
                                              or is_key_like(stripped)):
        return "key-ref"
    if group.get("pattern_no_slots"):
        return "pattern-no-slots"
    # 安全网：不管上游标了什么 bucket，去掉标签/占位符后不足 2 个字母就不该进翻译
    # （"--------------"、"x"、纯符号分隔线都走这条，比 bucket-never 更早）
    if visible_letters(text) < MIN_LETTERS:
        return "no-letters"
    if group["bucket"] == "never":
        return "bucket-never"
    if is_pure_number(text):
        return "pure-number"
    if is_url_only(text):
        return "url-only"
    if not T.has_translatable_word(text):
        return "no-words"
    if existing and group["h"] in existing:
        return "existing-h"
    return None


def existing_pack_hashes(out_dir=None, exclude_suffix=OUT_SUFFIX):
    """现有 cfg 包的 h 集合（跨包 h 唯一，已有译文的行不再写 IL 行）。"""
    out_dir = Path(out_dir or OUT_DIR)
    hashes = set()
    if not out_dir.exists():
        return hashes
    for path in sorted(out_dir.glob("*.jsonl")):
        if path.name.endswith(exclude_suffix):
            continue
        for row in iter_jsonl(path):
            h = row.get("h")
            if isinstance(h, str):
                hashes.add(h.lower())
    return hashes


# ---------------------------------------------------------------------------
# 分批
# ---------------------------------------------------------------------------

def limit_for(masked):
    if len(masked) > 200:
        return 20
    if len(masked) > 60:
        return 60
    return 120


def build_items(groups):
    items = []
    for index, group in enumerate(groups, 1):
        res = masklib.mask(group["text"])
        items.append({
            "id": index,
            "group": group,
            "row": {"text": group["text"], "mod": group["host"]},
            "masked": res.text,
            "res": res,
            "role": ("pattern" if group["bucket"] == PATTERN_BUCKET
                     else "label" if group["bucket"] == "short_label" else "description"),
            "bucket": group["bucket"],
        })
    return items


def build_batches(items, max_chars=DEFAULT_MAX_CHARS, group_by_mod=True):
    batches = []
    buckets = {}
    for item in items:
        key = (item["group"]["host"] if group_by_mod else "", item["bucket"])
        buckets.setdefault(key, []).append(item)
    for key in sorted(buckets):
        pending = sorted(buckets[key], key=lambda it: (-len(it["masked"]), it["id"]))
        current = []
        chars = 0
        for item in pending:
            cap = (PATTERN_BATCH_CAP if item["bucket"] == PATTERN_BUCKET
                   else limit_for(item["masked"]))
            size = len(item["masked"])
            if current and (len(current) >= cap or chars + size > max_chars):
                batches.append({"key": key, "bucket": key[1], "items": current})
                current = []
                chars = 0
            current.append(item)
            chars += size
        if current:
            batches.append({"key": key, "bucket": key[1], "items": current})
    return batches


def _sample(items, limit):
    """均匀取样（按输入顺序，确定）。"""
    total = len(items)
    if limit <= 0 or limit >= total:
        return list(items)
    step = total / float(limit)
    picked = []
    seen = set()
    for i in range(limit):
        index = min(total - 1, int(round(i * step)))
        if index in seen:
            continue
        seen.add(index)
        picked.append(items[index])
    return picked


# ---------------------------------------------------------------------------
# 执行器
# ---------------------------------------------------------------------------

class _OfflineResponse:
    finish_reason = "offline"
    usage = {}
    attempts = 0
    elapsed = 0.0

    def __init__(self, content):
        self.content = content


class _OfflineConfig:
    model = "offline"
    base = ""
    reasoning = ""
    key = ""
    timeout = 0

    def describe(self):
        return "offline"


def keep_no_cjk(source, restored) -> bool:
    """模型只把术语换成符号/缩写、整句没有中文可译时的 keep 判据。

    典型：`<b>Delta-V</b>: {0} m/s` → `<b>Δv</b>：{0} m/s`（Delta-V 的航天标准写法是 Δv）。
    这类串本来就没有"中文可写"，G8 拒是对的（不许放宽），但也不该算 failed：
    落成 keep（t == s）让运行时回退原文，比留一条英文坏行干净。
    负例：模型把英文整句抄回 -> has_translatable_word 为真 -> 不走这条。
    """
    if not restored or has_cjk(restored):
        return False
    if T.echo_keep(source, restored):
        return False
    return not T.has_translatable_word(restored)


def load_conventions(path=None) -> dict:
    """packs/conventions.jsonl -> {en: zh}。

    这是 G6 下界豁免的**唯一依据**（lead 决定：只走机器可读数据）。
    不用 glossary（它不含 质心/比冲/压心，且 electric charge 有两个变体），
    也不用 translate.py 里那份散文抄来的 CONVENTIONS（那是 prompt 用，不是判据）。
    逐字节比较，不做归一化、不折叠大小写、不合并变体。
    """
    path = Path(path or CONVENTIONS_PATH)
    table = {}
    if not path.is_file():
        return table
    for row in iter_jsonl(path):
        en, zh = row.get("en"), row.get("zh")
        if isinstance(en, str) and isinstance(zh, str) and en.strip() and zh.strip():
            table[en] = zh
    return table


def g6_convention_exempt(item, restored, conventions, lower=0.15):
    """--g6-convention-exempt 的判据（默认关闭）。五条必须全满足，返回命中明细或 None：

    1. 整串逐字节等于某个约定术语（`Center of Mass` 可以，`Center of Mass of the vessel` 不行）
    2. 译文逐字节等于该术语的约定译文（`质心` 可以，`重心` 不行——别名不在豁免里）
    3. len(译文) >= 2（单字不放行）
    4. 只处理**下界**违规（比值 >= 0.15 说明不是这条毛病，交回正常 G6 判）
    5. 其余门禁照旧：调用点只在 failed == {"G6"} 时才问这里
    依据只来自 packs/conventions.jsonl；表为空（文件缺失）则永不豁免。
    """
    if not conventions:
        return None
    source = (item.get("group", {}).get("text") or "").strip()
    zh = (restored or "").strip()
    want = conventions.get(source)
    if want is None or not zh or zh != want or len(zh) < 2:
        return None
    ratio = len(zh) / max(1, len(source))
    if ratio >= lower:
        return None
    return {"en": source, "zh": zh, "ratio": round(ratio, 3)}


def load_overrides(path=None) -> tuple:
    """packs/overrides.jsonl -> ({normalized_en: {"zh","note"}}, {"conflicts","bad"})。

    人工覆盖通道（lead 决定：渲染路径词这类极少数个案不建术语表，给一条人工纠错路径）。
    schema 每行 {"en": "<源文逐字节>", "zh": "<期望译文>", "note": "<为什么>"}。
    命中规则：normalize(源文) 与 en 逐字节相等（不做大小写折叠、不做变体合并）。
    同一 en 出现两次且 zh 不同 -> 只取第一行，计 conflicts（不猜）。
    文件不存在 -> 空表，不报错（默认读，缺失即跳过）。
    """
    path = Path(path or OVERRIDES_PATH)
    table, conflicts, bad = {}, 0, 0
    if not path.is_file():
        return table, {"conflicts": 0, "bad": 0}
    for row in iter_jsonl(path):
        en, zh = row.get("en"), row.get("zh")
        if not (isinstance(en, str) and isinstance(zh, str)) or not en.strip() or not zh.strip():
            bad += 1
            continue
        key = normalize(en)
        if key in table:
            if table[key]["zh"] != zh:
                conflicts += 1
            continue
        table[key] = {"zh": zh, "note": str(row.get("note") or "")}
    return table, {"conflicts": conflicts, "bad": bad}


def override_gate(source, zh, pattern=False) -> str:
    """人工译文的结构性门禁（与 G2/G4 同口径）：占位符多重集、富文本标签多重集。

    人工优先不等于免检：这两项一旦不符，运行时就是崩/串号/颜色错乱，所以不通过就整条跳过
    （记 override_rejected），绝不写坏行。长度比（G6）不查：人工译文的长短由人负责。
    """
    if not isinstance(zh, str) or not zh.strip():
        return "empty"
    if masklib.count_placeholders(source) != masklib.count_placeholders(zh):
        return "placeholder"
    if masklib.count_rich_tags(source) != masklib.count_rich_tags(zh):
        return "richtext"
    # G9 的人工通道口径：骨架的 # 个数必须一致（写错个数 = 运行时数字错位）
    if pattern and SK.placeholders(source) != SK.placeholders(zh):
        return "skeleton-slot"
    return ""


class Runner:
    def __init__(self, config, glossary, cache, jobs=4, max_chars=DEFAULT_MAX_CHARS,
                 log=print, temperature=0.2, dry_run=False, offline=False,
                 g6_exempt=False, conventions=None, overrides=None):
        self.config = config
        self.glossary = glossary
        self.cache = cache
        self.jobs = jobs
        self.max_chars = max_chars
        self.log = log
        self.temperature = temperature
        self.dry_run = dry_run
        self.offline = offline
        self.g6_exempt = g6_exempt
        self.conventions = conventions or {}
        self.overrides = overrides or {}
        self.client = None if offline else llmlib.LlmClient(config, log=log)
        self.stats = {
            "calls": 0,
            "batches": 0,
            "batches_failed": 0,
            "failed_batches_partial": 0,
            "splits": 0,
            "retried": 0,
            "items_total": 0,
            "items_ok": 0,
            "items_keep": 0,
            "items_failed": 0,
            "items_gated_ratio": 0,
            "gated_ratio_examples": [],
            "keeps_no_cjk": 0,
            "g6_exempt": 0,
            "gated_ratio_exempt": [],
            "overrides_applied": 0,
            "override_rejected": [],
            "bucket_status": {},
            "cache_hits": 0,
            "finish_reasons": {},
            "latency_total": 0.0,
            "latencies": [],
            "gates": {name: 0 for name in GATE_NAMES},
            "gate_examples": {name: [] for name in GATE_NAMES},
            "terms_injected": 0,
            "terms_distinct": set(),
            "locked_triggered": set(),
            "keep_examples": [],
            "failed_items": [],
            "gated_ratio_examples": [],
        }

    # -- 调用 -------------------------------------------------------------
    def _call(self, items, soft_pairs):
        if self.offline:
            payload = {"items": [{"id": item["id"], "zh": "【离线】" + item["masked"]}
                                 for item in items]}
            self.stats["calls"] += 1
            return _OfflineResponse(json.dumps(payload, ensure_ascii=False))
        system = T.system_prompt(soft_pairs)
        if any(item["bucket"] == PATTERN_BUCKET for item in items):
            system = system + "\n" + PATTERN_PROMPT
        messages = [
            {"role": "system", "content": system},
            {"role": "user", "content": T.user_message(items)},
        ]
        result = self.client.chat(messages, temperature=self.temperature)
        self.stats["calls"] += 1
        reason = result.finish_reason or "unknown"
        self.stats["finish_reasons"][reason] = self.stats["finish_reasons"].get(reason, 0) + 1
        elapsed = getattr(result, "elapsed", 0.0) or 0.0
        if elapsed:
            self.stats["latency_total"] = round(self.stats["latency_total"] + elapsed, 3)
            if len(self.stats["latencies"]) < 500:
                self.stats["latencies"].append(round(elapsed, 2))
        return result

    def _locked_pairs(self, item):
        pairs = T.locked_for(item["masked"], self.glossary)
        for en, _zh in pairs:
            self.stats["locked_triggered"].add(en)
        return pairs

    def _eval(self, item, zh_raw):
        """门禁 + （可选）约定术语的 G6 窄豁免。所有门禁调用点都必须走这里。"""
        pairs = self._locked_pairs(item)
        restored, failed, notes = T.gate(item, zh_raw, pairs)
        # G9（只对 pattern 桶）：骨架的 # 个数必须与原文一致。
        # 与 G2 分开的理由：# 不是 TMP/KSP 的富文本占位符，运行时替换语义不同
        # （骨架是数字段的抽象），混进 G2 会让报告看不出是骨架通道出的问题。
        if item["bucket"] == PATTERN_BUCKET:
            want_slots = SK.placeholders(item["group"]["text"])
            got_slots = SK.placeholders(restored or "")
            if want_slots != got_slots:
                failed = list(failed) + ["G9"]
                notes = dict(notes)
                notes["G9"] = "骨架占位符 期望{} 实际{}".format(want_slots, got_slots)
        if failed and set(failed) == {"G6"} and self.g6_exempt:
            hit = g6_convention_exempt(item, restored, self.conventions)
            if hit:
                self.stats["g6_exempt"] += 1
                if len(self.stats["gated_ratio_exempt"]) < 200:
                    self.stats["gated_ratio_exempt"].append({
                        "h": item["group"]["h"], "mod": item["group"]["host"],
                        "s": hit["en"], "zh": hit["zh"], "ratio": hit["ratio"],
                    })
                notes = dict(notes)
                notes["G6"] = notes.get("G6", "") + "（约定术语豁免）"
                return restored, [], notes
        return restored, failed, notes

    def _override(self, item):
        """人工覆盖命中则直接产出结果（不请求模型）；未命中返回 None。

        人工优先：覆盖发生在门禁之前 —— 模型压根不会被问（省 token），
        但人工译文仍要过 override_gate（占位符多重集 + 富文本标签多重集），
        不过就跳过该条并记 override_rejected 明细，随后照常走模型流程。
        """
        if not self.overrides:
            return None
        group = item["group"]
        text = group["text"]
        row = self.overrides.get(normalize(text)) or self.overrides.get(text)
        if row is None:
            return None
        why = override_gate(text, row["zh"], pattern=item["bucket"] == PATTERN_BUCKET)
        if why:
            if len(self.stats["override_rejected"]) < 100:
                self.stats["override_rejected"].append({
                    "h": group["h"], "mod": group["host"], "bucket": item["bucket"],
                    "s": text[:160], "zh": row["zh"][:160], "why": why,
                    "note": row.get("note", ""),
                })
            return None
        return self._finish(item, row["zh"], [], {"override": row.get("note", "")},
                            override=True)

    def _note_gate(self, name, item, restored, note):
        self.stats["gates"][name] += 1
        bucket = self.stats["gate_examples"][name]
        if len(bucket) < 5:
            bucket.append({
                "h": item["group"]["h"],
                "mod": item["group"]["host"],
                "src": item["group"]["text"][:160],
                "zh": (restored or "")[:160],
                "why": note,
            })

    # -- 单条收尾 ---------------------------------------------------------
    def _finish(self, item, restored, failed, notes, response=None,
                from_cache=False, failed_item=False, zh_raw=None, override=False):
        group = item["group"]
        bucket = item["bucket"]
        self.stats["items_total"] += 1
        status = "failed"
        entry = None
        if override:
            # 人工覆盖：不经过模型、不判 G6/G7/G8，但 override_gate 已校验占位符与富文本
            status = "override"
            entry = {"h": group["h"], "n": group["n"], "s": group["text"],
                     "t": restored, "mod": T.safe_mod_name(group["host"])}
            if bucket == PATTERN_BUCKET:
                entry["p"] = 1
        elif not failed:
            status = "ok"
            entry = {"h": group["h"], "n": group["n"], "s": group["text"],
                     "t": restored, "mod": T.safe_mod_name(group["host"])}
            if bucket == PATTERN_BUCKET:
                entry["p"] = 1
        elif set(failed) == {"G8"} and (T.echo_keep(group["text"], restored)
                                        or keep_no_cjk(group["text"], restored)):
            status = "keep"
            entry = {"h": group["h"], "n": group["n"], "s": group["text"],
                     "t": group["text"], "mod": T.safe_mod_name(group["host"])}
            if not T.echo_keep(group["text"], restored):
                self.stats["keeps_no_cjk"] += 1
            if len(self.stats["keep_examples"]) < 8:
                self.stats["keep_examples"].append({"h": group["h"], "s": group["text"][:120],
                                                    "mod": group["host"],
                                                    "zh": (restored or "")[:120]})
        elif set(failed) == {"G6"}:
            # 长度比不过 = 这对英中文本的信息密度差，重发模型也不会变 -> 不重试、不算 failed
            status = "gated_ratio"
        for name in failed:
            self._note_gate(name, item, restored, notes.get(name, ""))

        # 缓存"哨兵态"原始产出：ok / keep / gated_ratio 都是终态，重跑必须得到同一结论，
        # 否则同一份输入重跑会漂移（实测：gated_ratio 的 " From Now" 第二次被译成可通过的句子，
        # 包内容随之变化）。failed 不入缓存，留给下一轮重试。
        # 注意必须存 zh_raw（未还原）而不是 restored：命中缓存后会再走一次 gate，
        # 存已还原的文本会被二次还原吃掉哨兵，制造假 G5。
        if (not from_cache and not failed_item and isinstance(zh_raw, str) and zh_raw
                and status in ("ok", "keep", "gated_ratio") and item.get("cache_key")):
            self.cache.put(item["cache_key"], group["text"], zh_raw, status,
                           self.config.model)

        bucket_row = self.stats["bucket_status"].setdefault(bucket, {})
        bucket_row[status] = bucket_row.get(status, 0) + 1

        if status == "ok":
            self.stats["items_ok"] += 1
        elif status == "override":
            self.stats["overrides_applied"] += 1
        elif status == "keep":
            self.stats["items_keep"] += 1
        elif status == "gated_ratio":
            self.stats["items_gated_ratio"] += 1
            # lead m01753 要求「gated_ratio 全量清单（h/mod/s/zh/ratio）」：
            # 上限只作内存保险（2000 条远超实际规模），ratio 与 G6 的算法逐字一致。
            if len(self.stats["gated_ratio_examples"]) < 2000:
                src_len = len(group["text"])
                self.stats["gated_ratio_examples"].append({
                    "h": group["h"], "mod": asm_base(group["host"]), "bucket": bucket,
                    "s": group["text"][:300],
                    "zh": (restored or "")[:300],
                    "ratio": round(len(restored or "") / src_len, 3) if src_len else 0.0,
                    "why": notes.get("G6", ""),
                })
        else:
            self.stats["items_failed"] += 1
            if len(self.stats["failed_items"]) < 300:
                self.stats["failed_items"].append({
                    "h": group["h"],
                    "mod": group["host"],
                    "bucket": bucket,
                    "s": group["text"][:200],
                    "zh": (restored or "")[:200],
                    "gates": list(failed),
                    "why": " | ".join(notes.get(n, "") for n in failed),
                    "missing": bool(failed_item and not restored),
                })
        return {
            "h": group["h"],
            "n": group["n"],
            "s": group["text"],
            "mod": group["host"],
            "bucket": bucket,
            "status": status,
            "gates": list(failed),
            "notes": notes,
            "entry": entry,
            "from_cache": from_cache,
            # 模型原始产出（哨兵态）。不写库的 status（gated_ratio / failed / keep）也要留证，
            # 否则审计物里 zh 是空串，看不出模型到底给了什么。
            "zh_raw": restored or "",
        }

    # -- 批处理 -----------------------------------------------------------
    def process(self, batch, depth=0):
        items = batch["items"]
        soft_pairs = T.glossary_for([item["masked"] for item in items], self.glossary)
        self.stats["terms_injected"] += len(soft_pairs)
        self.stats["terms_distinct"].update(en for en, _zh in soft_pairs)
        self.stats["batches"] += 1

        def _eval(item, zh_raw):
            return self._eval(item, zh_raw)

        results = []
        pending = []
        for item in items:
            key = T.cache_key(item["group"]["text"], self.glossary.version,
                              PROMPT_VERSION_IL, self.config.model)
            item["cache_key"] = key
            manual = self._override(item)
            if manual is not None:
                results.append(manual)
                continue
            cached = self.cache.get(key)
            if cached and isinstance(cached.get("zh"), str) and cached.get("zh"):
                self.stats["cache_hits"] += 1
                restored, failed, notes = _eval(item, cached["zh"])
                results.append(self._finish(item, restored, failed, notes, from_cache=True,
                                            zh_raw=cached["zh"]))
            else:
                pending.append(item)

        if not pending:
            return results

        # 批内可能混着「人工覆盖命中」与「需要模型」的条目。模型调用一旦抛异常，
        # 早先已算出的覆盖/缓存结果若跟着整批丢掉，就会出现「人工表里有译文、包里没有行」
        # 的静默缺口（实测 v1.4 首跑：'Instructions:' / 'Destination:' 等 12 条单 token
        # 真 UI 文案就这样丢了，用户界面上直接看到英文）。所以这里必须就地兜住，
        # 只把真正没译的 pending 记 failed，已得结果原样返回。
        try:
            response = self._call(pending, soft_pairs)
        except Exception:
            self.stats["batches_failed"] += 1
            self.stats["failed_batches_partial"] += 1
            self.log("  !! 批异常（未译 {} 条，已保留覆盖/缓存 {} 条）：\n{}".format(
                len(pending), len(results), traceback.format_exc()))
            for item in pending:
                results.append(self._finish(item, None, ["G1"], {"G1": "批异常"},
                                            failed_item=True))
            return results
        parsed = T.extract_items(response.content) or []
        by_id = {}
        for row in parsed:
            raw_id = row.get("id")
            try:
                key = int(raw_id)
            except (TypeError, ValueError):
                key = raw_id
            if key not in by_id:
                by_id[key] = row.get("zh")

        retry = []
        for item in pending:
            zh_raw = by_id.get(item["id"])
            if not isinstance(zh_raw, str) or not zh_raw.strip():
                retry.append((item, None, ["G1"], {"G1": "模型未返回该 id"}))
                continue
            restored, failed, notes = _eval(item, zh_raw)
            if not failed:
                results.append(self._finish(item, restored, failed, notes, response=response,
                                            zh_raw=zh_raw))
                continue
            if set(failed) == {"G6"}:
                # 长度比是这对文本的固有属性，重发模型没有意义
                results.append(self._finish(item, restored, failed, notes, response=response,
                                            zh_raw=zh_raw))
                continue
            retry.append((item, zh_raw, failed, notes))

        if retry:
            results.extend(self._split(batch, retry, soft_pairs, depth, "gate"))
        return results

    def _split(self, batch, retry, soft_pairs, depth, reason, last_gates=None):
        """门禁失败 / 漏 id：整批拆半重试；单条时再给一次机会。"""
        self.stats["retried"] += len(retry)
        if depth >= 2 or len(retry) == 1:
            out = []
            for item, zh_raw, failed, notes in retry:
                if depth < 2 and len(retry) == 1:
                    out.extend(self._retry_single(batch, item, soft_pairs, depth))
                else:
                    out.append(self._finish(item, zh_raw, failed, notes, failed_item=True))
            return out

        self.stats["splits"] += 1
        half = max(1, len(retry) // 2)
        out = []
        for part in (retry[:half], retry[half:]):
            if not part:
                continue
            sub = {"key": batch["key"], "bucket": batch["bucket"],
                   "items": [entry[0] for entry in part]}
            out.extend(self.process(sub, depth + 1))
        return out

    def _retry_single(self, batch, item, soft_pairs, depth):
        """单条重发一次（模型漏 id / 单条门禁失败的最后一次机会）。"""
        sub = {"key": batch["key"], "bucket": batch["bucket"], "items": [item]}
        self.stats["batches"] += 1
        try:
            response = self._call([item], soft_pairs)
        except Exception:
            self.log("  !! 单条重发异常：" + traceback.format_exc().splitlines()[-1])
            return [self._finish(item, None, ["G1"], {"G1": "单条重发异常"}, failed_item=True)]
        parsed = T.extract_items(response.content) or []
        zh_raw = None
        for row in parsed:
            raw_id = row.get("id")
            try:
                key = int(raw_id)
            except (TypeError, ValueError):
                key = raw_id
            if key == item["id"] and isinstance(row.get("zh"), str):
                zh_raw = row["zh"]
                break
        if not isinstance(zh_raw, str) or not zh_raw.strip():
            return [self._finish(item, None, ["G1"], {"G1": "单条重发仍未返回该 id"},
                                 failed_item=True)]
        restored, failed, notes = self._eval(item, zh_raw)
        return [self._finish(item, restored, failed, notes, response=response, zh_raw=zh_raw)]


    def run(self, batches):
        started = time.time()
        results = []
        done = 0
        with concurrent.futures.ThreadPoolExecutor(max_workers=self.jobs) as pool:
            futures = {pool.submit(self.process, batch): batch for batch in batches}
            for future in concurrent.futures.as_completed(futures):
                batch = futures[future]
                done += 1
                try:
                    results.extend(future.result())
                except Exception:
                    self.log("  !! 批异常（{} 条）：\n{}".format(len(batch["items"]),
                                                              traceback.format_exc()))
                    self.stats["batches_failed"] += 1
                    for item in batch["items"]:
                        results.append(self._finish(item, None, ["G1"], {"G1": "批异常"},
                                                    failed_item=True))
                if done % 5 == 0 or done == len(batches):
                    used = self.client.tokens if self.client else {"prompt": 0, "completion": 0}
                    self.log("  [{}/{}] 批  ok={} keep={} failed={} tokens={}/{}".format(
                        done, len(batches), self.stats["items_ok"], self.stats["items_keep"],
                        self.stats["items_failed"], used.get("prompt", 0),
                        used.get("completion", 0)))
        self.stats["elapsed_s"] = round(time.time() - started, 1)
        self.stats["tokens"] = dict(self.client.tokens) if self.client else {"prompt": 0,
                                                                            "completion": 0}
        calls = self.stats["calls"] or 0
        self.stats["latency_avg_s"] = round(self.stats["latency_total"] / calls, 2) if calls else 0.0
        self.stats["latency_max_s"] = max(self.stats["latencies"]) if self.stats["latencies"] else 0.0
        self.stats["terms_distinct"] = len(self.stats["terms_distinct"])
        self.stats["locked_triggered"] = sorted(self.stats["locked_triggered"])
        return results


# ---------------------------------------------------------------------------
# 冲突裁决 + 写库
# ---------------------------------------------------------------------------

def build_entries(results, ctx_mode="drop"):
    """把结果落成行；同一 h 只留一行（先到先得），不同 t 记 il_conflict。"""
    entries = []
    seen = {}
    conflicts = []
    for result in results:
        entry = result.get("entry")
        if not entry:
            continue
        h = entry["h"]
        previous = seen.get(h)
        if previous is not None:
            if previous["t"] != entry["t"] or previous["s"] != entry["s"]:
                conflicts.append({
                    "h": h,
                    "kept": {"s": previous["s"], "t": previous["t"], "mod": previous["mod"]},
                    "dropped": {"s": entry["s"], "t": entry["t"], "mod": entry["mod"]},
                })
            continue
        row = {
            "h": entry["h"],
            "n": entry["n"],
            "s": entry["s"],
            "t": entry["t"],
            "mod": T.safe_mod_name(entry["mod"]),
        }
        if entry.get("p"):
            row["p"] = int(entry["p"])
        if ctx_mode == "keep":
            ctxs = result.get("ctxs") or []
            if ctxs:
                row["ctx"] = ctxs[0]
        seen[h] = row
        entries.append(row)
    return entries, conflicts


def write_packs(entries, out_dir=None, dry_run=False, prune=False, merge=False):
    """把条目写成 packs/zh-cn/<mod>.il.jsonl。

    merge=False（默认）：整文件覆盖本次条目（既有 selftest 期望的逐字节行为）。
    merge=True：先读回既有文件，按 h 用本次条目覆盖/追加后整文件写回 —— 既有行不丢。
    为什么需要：无密钥或限跑时只有人工覆盖的条目能出译文，整文件覆盖会把上千行既有
    IL 包清空（lead 核对 write_packs/prune 时发现的两个毁数据行为之一）。
    """
    out_dir = Path(out_dir or OUT_DIR)
    files = {}
    for entry in entries:
        files.setdefault(entry["mod"], []).append(entry)
    summary = {"files": {}, "total": 0, "written": False, "removed": [],
               "merged": {}, "added": {}, "existing_bad": 0}
    for mod, rows in files.items():
        rows.sort(key=lambda row: row["h"])
        summary["files"][mod] = len(rows)
    summary["total"] = sum(len(rows) for rows in files.values())
    if dry_run:
        return summary

    out_dir.mkdir(parents=True, exist_ok=True)
    for mod, rows in sorted(files.items()):
        path = out_dir / (mod + OUT_SUFFIX)
        if merge:
            kept = {}  # h -> row（既有行）
            if path.exists():
                with path.open("r", encoding="utf-8") as handle:
                    for line in handle:
                        line = line.strip()
                        if not line:
                            continue
                        try:
                            row = json.loads(line)
                            key = row["h"]
                        except (ValueError, TypeError, KeyError):
                            summary["existing_bad"] += 1
                            continue
                        kept[key] = row
            existing_rows = len(kept)
            added = 0
            for row in rows:
                if row["h"] not in kept:
                    added += 1
                kept[row["h"]] = row
            ordered = [kept[key] for key in sorted(kept)]
            with path.open("w", encoding="utf-8", newline="\n") as handle:
                for row in ordered:
                    handle.write(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n")
            summary["files"][mod] = len(ordered)
            summary["merged"][mod] = existing_rows
            summary["added"][mod] = added
        else:
            with path.open("w", encoding="utf-8", newline="\n") as handle:
                for row in rows:
                    handle.write(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n")
    if merge:
        summary["total"] = sum(summary["files"].values())
    if prune:
        for path in sorted(out_dir.glob("*" + OUT_SUFFIX)):
            stem = path.name[:-len(OUT_SUFFIX)]
            if stem not in files:
                path.unlink()
                summary["removed"].append(path.name)
    summary["written"] = True
    return summary


def write_sample(results, path):
    """逐条审计物（jsonl）：h/mod/bucket/status/gates/src/zh，供人工抽查。"""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    rows = 0
    with path.open("w", encoding="utf-8", newline="\n") as handle:
        for result in results:
            entry = result.get("entry") or {}
            handle.write(json.dumps({
                "h": result["h"],
                "mod": result["mod"],
                "bucket": result["bucket"],
                "pattern": result["bucket"] == PATTERN_BUCKET,
                "status": result["status"],
                "gates": result["gates"],
                "why": result.get("notes", {}),
                "src": result["s"],
                "zh": entry.get("t", "") if entry else result.get("zh_raw", ""),
            }, ensure_ascii=False) + "\n")
            rows += 1
    return rows


def write_provenance(results, path):
    rows = []
    for result in results:
        if not result.get("entry"):
            continue
        rows.append({"h": result["h"], "s": result["s"], "mod": result["mod"],
                     "status": result["status"]})
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as handle:
        for row in rows:
            handle.write(json.dumps(row, ensure_ascii=False) + "\n")
    return len(rows)


# ---------------------------------------------------------------------------
# fixture（自造 40+ 行，含占位符/富文本/单字符/长句/跨 mod 同 h/坏 h）
# ---------------------------------------------------------------------------

def fixture_rows():
    il = []

    def add_il(asm, tname, method, offset, sink, text, bucket="translate", h=None):
        row = {"asm": asm, "type": tname, "method": method, "ilOffset": offset,
               "sink": sink, "text": text, "bucket": bucket, "reason": "fixture"}
        row["n"] = normalize(text)
        row["h"] = h or hash_text(row["n"])
        il.append(row)

    add_il("MechJeb2.dll", "MechJeb2.Gui", "Draw(Int32)", 120, "GUILayout.Button", "Launch", "short_label")
    add_il("MechJeb2.dll", "MechJeb2.Gui", "Draw(Int32)", 168, "GUILayout.Button", "Abort", "short_label")
    add_il("MechJeb2.dll", "MechJeb2.Gui", "Draw(Int32)", 214, "GUILayout.Button", "Toggle <<1>>", "short_label")
    add_il("MechJeb2.dll", "MechJeb2.Gui", "DrawManeuver(Int32)", 402, "GUILayout.Label", "Warp to Next Maneuver")
    add_il("MechJeb2.dll", "MechJeb2.Gui", "DrawOrbit(Int32)", 512, "GUILayout.Label", "Apoapsis: {0} km")
    add_il("MechJeb2.dll", "MechJeb2.Gui", "DrawDeltaV(Int32)", 640, "GUILayout.Label", "<b>Delta-V</b>: {0} m/s")
    add_il("MechJeb2.dll", "MechJeb2.Gui", "DrawTWR(Int32)", 700, "GUILayout.Label", "TWR", "short_label")
    add_il("MechJeb2.dll", "MechJeb2.Gui", "DrawRCS(Int32)", 760, "GUILayout.Label", "RCS", "short_label")
    add_il("RP-1.dll", "RP1.EngineGui", "DrawConfig(Int32)", 88, "GUILayout.Label", "Reaction Wheel")
    add_il("RP-1.dll", "RP1.EngineGui", "DrawConfig(Int32)", 132, "GUILayout.Label", "Monopropellant")
    add_il("RP-1.dll", "RP1.EngineGui", "DrawConfig(Int32)", 176, "GUILayout.Label", "Oxidizer", "short_label")
    add_il("RP-1.dll", "RP1.EngineGui", "DrawConfig(Int32)", 220, "GUILayout.Label", "Specific Impulse")
    add_il("RP-1.dll", "RP1.EngineGui", "DrawConfig(Int32)", 264, "GUILayout.Label", "Center of Mass")
    add_il("RP-1.dll", "RP1.EngineGui", "DrawConfig(Int32)", 308, "GUILayout.Label", "Center of Pressure")
    add_il("RP-1.dll", "RP1.EngineGui", "DrawConfig(Int32)", 352, "GUILayout.Label", "Gimbal", "short_label")
    add_il("RP-1.dll", "RP1.EngineGui", "DrawConfig(Int32)", 396, "GUILayout.Label", "Booster", "short_label")
    add_il("RP-1.dll", "RP1.EngineGui", "DrawFeed(Int32)", 440, "GUILayout.Label",
           "The propellant feed system routes fuel from the tank to the engine through a "
           "turbine-driven pump, which raises the pressure above chamber pressure before "
           "injection into the combustion chamber.")
    add_il("RP-1.dll", "RP1.EngineGui", "DrawFeed(Int32)", 484, "GUILayout.Label", "Ore", "short_label")
    add_il("RP-1.dll", "RP1.ScienceGui", "Draw(Int32)", 528, "GUILayout.Label", "EVA Report")
    add_il("Waterfall.dll", "Waterfall.PlumeGui", "Draw(Int32)", 60, "GUILayout.Label", "Throttle", "short_label")
    add_il("Waterfall.dll", "Waterfall.PlumeGui", "Draw(Int32)", 104, "GUILayout.Label", "Plume Density: %s")
    add_il("Waterfall.dll", "Waterfall.PlumeGui", "Draw(Int32)", 148, "GUILayout.Label", "Engine Status: %d")
    add_il("Waterfall.dll", "Waterfall.PlumeGui", "Draw(Int32)", 192, "GUILayout.Label", "--------------", "never")
    add_il("Waterfall.dll", "Waterfall.PlumeGui", "Draw(Int32)", 236, "GUILayout.Label", "x", "never")
    add_il("Waterfall.dll", "Waterfall.PlumeGui", "Draw(Int32)", 280, "GUILayout.Label", "已翻译", "never")
    add_il("Waterfall.dll", "Waterfall.PlumeGui", "Draw(Int32)", 296, "GUILayout.Label", "Blur", "never")
    add_il("KSPCommunityFixes.dll", "KSPCF.Patches", "Draw(Int32)", 320, "GUILayout.Button", "Recover Vessel")
    add_il("KSPCommunityFixes.dll", "KSPCF.Patches", "Draw(Int32)", 364, "GUILayout.Label", "Warp to Next Maneuver")
    add_il("KSPCommunityFixes.dll", "KSPCF.Patches", "Draw(Int32)", 408, "GUILayout.Label", "EVA Report")
    add_il("ModularLaunchPads.dll", "MLP.TowerGui", "Draw(Int32)", 452, "GUILayout.Label", "Tower Height")
    add_il("ModularLaunchPads.dll", "MLP.TowerGui", "Draw(Int32)", 496, "GUILayout.Button", "Launch", "short_label")
    add_il("RealismOverhaul.dll", "RO.Gui", "Draw(Int32)", 540, "GUILayout.Label", "Prograde", "short_label")
    add_il("RealismOverhaul.dll", "RO.Gui", "Draw(Int32)", 584, "GUILayout.Label", "Retrograde", "short_label")
    add_il("RealismOverhaul.dll", "RO.Gui", "Draw(Int32)", 628, "GUILayout.Label", "Radial In", "short_label")
    add_il("RealismOverhaul.dll", "RO.Gui", "Draw(Int32)", 672, "GUILayout.Label", "Radial Out", "short_label")
    add_il("RealismOverhaul.dll", "RO.Gui", "Draw(Int32)", 716, "GUILayout.Label", "Docking Port")
    add_il("RealismOverhaul.dll", "RO.Gui", "Draw(Int32)", 760, "GUILayout.Label", "Solar Panel")
    add_il("RealismOverhaul.dll", "RO.Gui", "Draw(Int32)", 804, "GUILayout.Label", "Battery", "short_label")
    add_il("RealismOverhaul.dll", "RO.Gui", "Draw(Int32)", 848, "GUILayout.Label", "Antenna", "short_label")
    add_il("RealismOverhaul.dll", "RO.Gui", "Draw(Int32)", 892, "GUILayout.Label", "Radiator", "short_label")
    add_il("RealismOverhaul.dll", "RO.Gui", "Draw(Int32)", 936, "GUILayout.Label", "Landing Gear")
    add_il("RealismOverhaul.dll", "RO.Gui", "Draw(Int32)", 980, "GUILayout.Label", "Parachute", "short_label")
    # 坏 h：与 fnv1a64(normalize(text)) 不符，必须被拒
    add_il("RealismOverhaul.dll", "RO.Gui", "DrawBad(Int32)", 1024, "GUILayout.Label", "Science",
           "short_label", h="0000000000000000")

    capture = []

    def add_cap(sink, caller, text, bucket="translate", count=1, mods=None, h=None):
        row = {"s": text, "sink": sink, "caller": caller, "count": count,
               "firstSeen": "2026-10-23T00:00:00Z", "bucket": bucket, "reason": "fixture",
               "mods": mods or ["MechJeb2"]}
        row["n"] = normalize(text)
        row["h"] = h or hash_text(row["n"])
        capture.append(row)

    add_cap("ScreenMessage", "MechJeb2.MechJebCore:FixedUpdate", "Science")
    add_cap("ScreenMessages.PostScreenMessage", "KSPCommunityFixes.Patch:Postfix",
            "Insufficient electric charge", mods=["KSPCommunityFixes"])
    add_cap("GUILayout.Label", "RP1.TankGui:Draw", "Tank", "short_label", count=3, mods=["RP-1"])
    add_cap("GUILayout.Label", "RP1.OtherGui:Draw", "Tank", "short_label", count=2, mods=["RP-1"])
    add_cap("GUILayout.Label", "RP1.OtherGui:Draw", "Tank", "short_label", count=1,
            mods=["KSPCommunityFixes"])
    return il, capture


def write_fixture(path=None):
    path = Path(path or FIXTURE_PATH)
    il, capture = fixture_rows()
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as handle:
        for row in il:
            handle.write(json.dumps(row, ensure_ascii=False) + "\n")
        for row in capture:
            handle.write(json.dumps(row, ensure_ascii=False) + "\n")
    return path, len(il), len(capture)


# ---------------------------------------------------------------------------
# 报告
# ---------------------------------------------------------------------------

def print_summary(payload):
    counts = payload.get("counts", {})
    stats = payload.get("stats", {})
    total = counts.get("groups", 0)
    translated = stats.get("items_ok", 0) + stats.get("items_keep", 0)
    print("")
    print("[il] 候选 {} 行 → 去重 {} 组（合并 {} 组跨 mod）".format(
        counts.get("candidates", 0), total, counts.get("merged", 0)))
    print("[il] 跳过 {} 组：{}".format(counts.get("skipped", 0), counts.get("skip_reasons", {})))
    print("[il] 送翻 {} 组 / {} 批（含拆分与单条重发共 {} 次请求批）".format(
        counts.get("to_translate", 0), counts.get("batches", 0), stats.get("batches", 0)))
    if total:
        print("[il] 结果 ok {} / keep {} / failed {} / gated_ratio {}".format(
            stats.get("items_ok", 0), stats.get("items_keep", 0), stats.get("items_failed", 0),
            stats.get("items_gated_ratio", 0)))
    print("[il] 九门禁：{}".format(stats.get("gates", {})))
    print("[il] 调用 {} 次 / 拆分 {} / 单条重试 {} / 缓存命中 {} / {}s".format(
        stats.get("calls", 0), stats.get("splits", 0), stats.get("retried", 0),
        stats.get("cache_hits", 0), stats.get("elapsed_s", 0)))
    tokens = stats.get("tokens", {})
    print("[il] tokens: prompt {} + completion {} = {}".format(
        tokens.get("prompt", 0), tokens.get("completion", 0),
        tokens.get("prompt", 0) + tokens.get("completion", 0)))
    written = payload.get("written", {})
    print("[il] 写盘 {} 个文件 / {} 行{}".format(
        len(written.get("files", {})), written.get("total", 0),
        "" if written.get("written") else "（dry-run，未写）"))
    if written.get("merged"):
        print("[il] merge：既有保留 {} 行 / 本次新增 {} 行 / 既有坏行 {} 条（明细见报告 written.merged/added）".format(
            sum(written["merged"].values()), sum(written.get("added", {}).values()),
            written.get("existing_bad", 0)))
    if payload.get("il_conflict"):
        print("[il] il_conflict {} 条（同 h 不同译文/原文，只保留一行）".format(
            len(payload["il_conflict"])))
    if stats.get("g6_exempt"):
        print("[il] G6 约定术语豁免 {} 条（--g6-convention-exempt，明细见报告 gated_ratio_exempt）".format(
            stats["g6_exempt"]))
        for row in payload.get("gated_ratio_exempt", [])[:5]:
            print("    - [{}] {} -> {}（长度比 {}）".format(row.get("mod"), row.get("s"),
                                                        row.get("zh"), row.get("ratio")))
    if stats.get("keeps_no_cjk"):
        print("[il] keep（无中文可译，如 Delta-V→Δv）{} 条".format(stats["keeps_no_cjk"]))
    if payload.get("gated_ratio"):
        print("[il] gated_ratio {} 条（G6 长度比被拒，按 untranslated 处理，不写库、不重试）".format(
            len(payload["gated_ratio"])))
        for row in payload["gated_ratio"][:5]:
            print("    - [{}] {} -> {}（{}）".format(row.get("bucket"), row.get("s"),
                                                    row.get("zh"), row.get("why")))
    bucket_status = payload.get("bucket_status") or stats.get("bucket_status") or {}
    if bucket_status:
        print("[il] 分桶统计（每桶独立，不合并成一个总数）：")
        for name in sorted(bucket_status, key=lambda b: -sum(bucket_status[b].values())):
            row = bucket_status[name]
            total_b = sum(row.values())
            print("    - {} {} 条：{}".format(name, total_b,
                                         " / ".join("{} {}".format(k, row[k]) for k in
                                                    sorted(row, key=lambda k: -row[k]))))
    if payload.get("overrides_total"):
        print("[il] 人工覆盖 {} 条：应用 {} / 被拒 {}".format(
            payload["overrides_total"], payload.get("overrides_applied", 0),
            len(payload.get("override_rejected") or [])))
        for row in (payload.get("override_rejected") or [])[:3]:
            print("    - [{}] {} -> {}（{}）".format(row.get("why"), row.get("s"),
                                                    row.get("zh"), row.get("mod")))
    print("[il] ctx_mode={} 运行时可达={}".format(payload.get("ctx_mode"),
                                                payload.get("runtime_reachable")))


# ---------------------------------------------------------------------------
# 自测（不联网）
# ---------------------------------------------------------------------------

def _gate_probe(text, zh, locked=()):
    item = {"id": 1, "group": {"h": hash_text(normalize(text)), "n": normalize(text),
                               "text": text, "host": "Probe"},
            "row": {"text": text, "mod": "Probe"}, "masked": None, "res": None,
            "role": "description", "bucket": "translate"}
    res = masklib.mask(text)
    item["res"] = res
    item["masked"] = res.text
    restored, failed, notes = T.gate(item, zh, list(locked))
    return restored, failed, notes, item


def _pattern_probe(text):
    """构造一条 bucket=pattern 的探针 item（骨架串，不做 API 调用）。"""
    item = {"id": 1, "group": {"h": hash_text(normalize(text)), "n": normalize(text),
                               "text": text, "host": "Probe"},
            "row": {"text": text, "mod": "Probe"}, "role": "pattern",
            "bucket": PATTERN_BUCKET}
    res = masklib.mask(text)
    item["res"] = res
    item["masked"] = res.text
    return item


def selftest():
    checks = 0
    failures = []

    def check(name, ok, detail=""):
        nonlocal checks
        checks += 1
        if not ok:
            failures.append("{}: {}".format(name, detail))

    il, capture = fixture_rows()
    check("fixture.il 行数 30-50", 30 <= len(il) <= 50, len(il))
    check("fixture.capture 行数", len(capture) >= 5, len(capture))

    # 1. 输入解析：il 用 text、capture 用 s
    cands = load_candidates([FIXTURE_PATH if FIXTURE_PATH.exists() else None], "auto")
    if not cands:
        import tempfile
        tmpdir = Path(tempfile.mkdtemp())
        path, n_il, n_cap = write_fixture(tmpdir / "fixture.jsonl")
        cands = load_candidates([path], "auto")
    else:
        n_il = sum(1 for c in cands if c["kind"] == "il")
        n_cap = sum(1 for c in cands if c["kind"] == "capture")
    check("解析 il 行", n_il >= 30, n_il)
    check("解析 capture 行", n_cap >= 5, n_cap)
    check("il ctx 前缀", any(c["ctx"].startswith("il:") for c in cands))
    check("mod 身份去 .dll（asm -> asmBase）", asm_base("MechJeb2.dll") == "MechJeb2",
          asm_base("MechJeb2.dll"))
    check("mod 身份去目录", asm_base("GameData\\TUFX\\Plugins\\TUFX.dll") == "TUFX",
          asm_base("GameData\\TUFX\\Plugins\\TUFX.dll"))
    check("mods 列表里的 .dll 也被去掉",
          mods_of({"mods": ["TUFX.dll", "RealFuels.dll"]}, "il") == ["TUFX", "RealFuels"],
          mods_of({"mods": ["TUFX.dll", "RealFuels.dll"]}, "il"))
    check("mod 字段里的 .dll 也被去掉",
          mods_of({"mod": "Waterfall.dll"}, "il") == ["Waterfall"],
          mods_of({"mod": "Waterfall.dll"}, "il"))
    check("版本号归一：ModuleManager.4.2.3 -> ModuleManager",
          asm_version_base("ModuleManager.4.2.3") == "ModuleManager",
          asm_version_base("ModuleManager.4.2.3"))
    check("版本号归一：单段数字不剥（Foo.2 保持）",
          asm_version_base("Foo.2") == "Foo.2", asm_version_base("Foo.2"))
    check("版本号归一：三段数字剥成 Foo",
          asm_version_base("Foo.2.3.4") == "Foo", asm_version_base("Foo.2.3.4"))
    check("版本号归一后 asm_base 同时去 .dll",
          asm_base("ModuleManager.4.2.3.dll") == "ModuleManager",
          asm_base("ModuleManager.4.2.3.dll"))
    check("无版本号的程序集原样保留",
          asm_base("ksp_plugin_adapter") == "ksp_plugin_adapter"
          and asm_base("Assembly-CSharp") == "Assembly-CSharp"
          and asm_base("B9-PWings-Fork") == "B9-PWings-Fork",
          asm_base("ksp_plugin_adapter"))
    check("归一后输出文件名 = ModuleManager.il.jsonl",
          (T.safe_mod_name(mods_of({"mods": ["ModuleManager.4.2.3.dll"]}, "il")[0]) + OUT_SUFFIX)
          == "ModuleManager.il.jsonl",
          T.safe_mod_name("ModuleManager") + OUT_SUFFIX)
    check("报告记录 asm_normalized（base != mod 才记）",
          asm_normalized_list([{"mods": ["ModuleManager.4.2.3.dll"]},
                               {"mods": ["Foo.2"]}])
          == [{"asm": "ModuleManager.4.2.3.dll", "base": "ModuleManager.4.2.3",
               "mod": "ModuleManager"}],
          asm_normalized_list([{"mods": ["ModuleManager.4.2.3.dll"]}, {"mods": ["Foo.2"]}]))
    check("输出文件名 = asmBase.il.jsonl",
          (T.safe_mod_name(mods_of({"mods": ["TUFX.dll"]}, "il")[0]) + OUT_SUFFIX) == "TUFX.il.jsonl",
          T.safe_mod_name("TUFX") + OUT_SUFFIX)
    check("capture ctx 前缀", any(c["ctx"].startswith("capture:") for c in cands))

    # 2. 去重：同 h 只一组；跨 mod 合并
    groups, conflicts = dedupe(cands)
    check("去重后组数 < 候选数", len(groups) < len(cands), (len(groups), len(cands)))
    tank = [g for g in groups.values() if g["n"] == "Tank"]
    check("Tank 合并成一组", len(tank) == 1, len(tank))
    if tank:
        check("Tank 跨 mod 合并 3 个来源", tank[0]["count"] == 3, tank[0]["count"])
        check("Tank host 确定性", tank[0]["host"] == sorted(tank[0]["mods"])[0])
    launch = [g for g in groups.values() if g["n"] == "Launch"]
    check("Launch 跨 mod 合并", len(launch) == 1 and launch[0]["count"] == 2, launch)

    # 3. 预筛
    existing = {hash_text(normalize("Science"))}
    reasons = {}
    for group in groups.values():
        reason = prefilter(group, existing)
        if reason:
            reasons[reason] = reasons.get(reason, 0) + 1
    check("坏 h 不影响预筛（h 现算）", "h-mismatch" not in reasons)
    check("no-letters 拦下 2 个符号串", reasons.get("no-letters", 0) >= 2, reasons)
    check("already-cjk 拦下中文行", reasons.get("already-cjk", 0) >= 1, reasons)
    check("bucket-never 生效（含字母的 never 行）", reasons.get("bucket-never", 0) >= 1, reasons)
    check("existing-h 拦下 Science", reasons.get("existing-h", 0) >= 1, reasons)
    # 实测这 8 条曾被 no-words / key-ref 误杀（Cm/Cd/Cl/L_D 是 lead 点名必须覆盖的）：
    # "Cm (r,g,b):" / "L_D (r,g,b):" / "Cd (r,g,b):" / "Cl (r,g,b):" / "GPU ms" / "SAS: OFF" /
    # "MODULAR FLIGHT PANEL #" / "# of Engines:"
    rescue_group = {"text": "Cm (r,g,b):", "bucket": "candidate", "h": "abcdabcdabcdabcd"}
    base_reason = prefilter(rescue_group, set())
    check("真 UI 文案 'Cm (r,g,b):' 默认被 no-words 误杀（要有覆盖才能救）",
          base_reason == "no-words", base_reason)
    check("人工覆盖命中则绕过启发式跳过",
          prefilter(rescue_group, set(), {rescue_group["h"]}) is None)
    check("key-like 串即使有覆盖仍判 key-ref",
          prefilter({"text": "#autoLOC_500", "bucket": "candidate", "h": "1111111111111111"},
                    None, {"1111111111111111"}) == "key-ref")
    check("真文案 '# of Engines:' 有覆盖时不被 key-ref 误杀",
          prefilter({"text": "# of Engines:", "bucket": "candidate", "h": "2222222222222222"},
                    None, {"2222222222222222"}) is None)

    check("单字母 x 被拒", prefilter({"text": "x", "bucket": "translate",
                                     "h": hash_text("x")}, set()) == "no-letters")

    # 4. 坏 h：显式校验（候选自带 h 与现算不符 -> 拒绝）
    bad = [c for c in cands if c["kind"] == "il" and c["text"] == "Science"]
    check("坏 h 候选可识别", bad and bad[0]["h_given"] != hash_text(normalize("Science")),
          bad[0]["h_given"] if bad else None)
    check("h 现算忽略候选 h", hash_text(normalize("Science")) != "0000000000000000")

    # 5. 门禁负例
    restored, failed, notes, item = _gate_probe("Toggle <<1>>", "开关")
    check("占位符被改 -> G2", "G2" in failed, failed)
    restored, failed, notes, item = _gate_probe("Plume Density: %s", "羽流密度")
    check("格式符丢失 -> G3", "G3" in failed, failed)
    restored, failed, notes, item = _gate_probe("<b>Delta-V</b>: {0} m/s", "速度增量: {0} 米/秒")
    check("富文本标签丢失 -> G4", "G4" in failed, failed)
    restored, failed, notes, item = _gate_probe("Line one \\nLine two", "第一行 第二行")
    check("哨兵丢失 -> G5", "G5" in failed, failed)
    long_src = ("The propellant feed system routes fuel from the tank to the engine through a "
                "turbine-driven pump, which raises the pressure above chamber pressure.")
    restored, failed, notes, item = _gate_probe(long_src, "泵")
    check("长度比过小 -> G6", "G6" in failed, failed)
    restored, failed, notes, item = _gate_probe("Reaction Wheel", "反应轮",
                                                [("Reaction Wheel", "动量轮")])
    check("术语未落 -> G7", "G7" in failed, failed)
    restored, failed, notes, item = _gate_probe("Launch", "")
    check("空译文 -> G8", "G8" in failed, failed)
    restored, failed, notes, item = _gate_probe("Launch", "Launch")
    check("抄回原文 -> G8 且 echo_keep", "G8" in failed and T.echo_keep("Launch", "Launch"))

    # 6. short_label 的 G6：比值被拒 -> gated_ratio（不放宽）
    restored, failed, notes, item = _gate_probe(long_src, "泵")
    check("G6 未被放宽", "G6" in failed)

    # 7. 端到端（离线）：写盘 + 契约 + 确定性
    import tempfile
    tmpdir = Path(tempfile.mkdtemp())
    fixture = tmpdir / "fixture.jsonl"
    write_fixture(fixture)
    cands2 = load_candidates([fixture], "auto")
    groups2, _conf = dedupe(cands2)
    kept = [g for g in groups2.values() if prefilter(g, set()) is None]
    items = build_items(kept)
    batches = build_batches(items)
    check("离线分批非空", len(batches) >= 1, len(batches))
    glossary = T.Glossary([], "selftest", "no glossary")
    cache = T.Cache(tmpdir / "cache.jsonl", enabled=False)
    runner = Runner(_OfflineConfig(), glossary, cache, jobs=2, offline=True, log=lambda *_a: None)
    results = runner.run(batches)
    check("离线结果覆盖全部条目", len(results) == len(kept), (len(results), len(kept)))
    entries, conflicts = build_entries(results, "drop")
    check("写盘行数 > 0", len(entries) > 0, len(entries))
    check("无重复 h", len({e["h"] for e in entries}) == len(entries))
    check("字段集合正确", all(set(e.keys()) == {"h", "n", "s", "t", "mod"} for e in entries),
          entries[0].keys() if entries else None)
    check("h 16 位小写 hex", all(re.fullmatch(r"[0-9a-f]{16}", e["h"]) for e in entries))
    check("h == fnv1a64(normalize(s))",
          all(e["h"] == hash_text(normalize(e["s"])) for e in entries))
    check("n == normalize(s)", all(e["n"] == normalize(e["s"]) for e in entries))
    check("t 非空", all(e["t"] for e in entries))
    check("keep 行 t == s", all(e["t"] == e["s"] for e in entries if e["t"] == e["s"]))

    out_a = tmpdir / "out-a"
    out_b = tmpdir / "out-b"
    summary_a = write_packs(entries, out_a)
    summary_b = write_packs(entries, out_b)
    bytes_a = {p.name: p.read_bytes() for p in sorted(out_a.glob("*.jsonl"))}
    bytes_b = {p.name: p.read_bytes() for p in sorted(out_b.glob("*.jsonl"))}
    check("两遍写盘逐字节相同", bytes_a == bytes_b)
    check("文件后缀 .il.jsonl", all(name.endswith(".il.jsonl") for name in bytes_a), list(bytes_a))
    check("单文件按 h 升序", all(
        [json.loads(line)["h"] for line in data.decode("utf-8").strip().split("\n")] ==
        sorted(json.loads(line)["h"] for line in data.decode("utf-8").strip().split("\n"))
        for data in bytes_a.values()))
    check("紧凑 JSON（无空格）", all(b'": "' not in data and b'", "' not in data
                                     for data in bytes_a.values()))
    check("无 BOM 且 LF 结尾", all(not data.startswith(b"\xef\xbb\xbf") and data.endswith(b"\n")
                                  for data in bytes_a.values()))
    check("写盘行数一致", summary_a["total"] == summary_b["total"] == len(entries))

    # 7c. write_packs(merge=True)：既有行不丢 / 同 h 新值胜出 / added+merged+existing_bad 计数 / 坏行不炸
    head = [e for e in entries if e["mod"] == entries[0]["mod"]]
    mod0 = entries[0]["mod"]
    out_m = tmpdir / "out-merge"
    out_m.mkdir(parents=True, exist_ok=True)
    target = out_m / (mod0 + OUT_SUFFIX)
    old_only = {"h": "00000000000000aa", "n": "old", "s": "Old", "t": "旧", "mod": mod0}
    same_h = dict(entries[0])
    same_h["t"] = "旧译文（必须被本次新值覆盖）"
    with target.open("w", encoding="utf-8", newline="\n") as handle:
        for row in (old_only, same_h):
            handle.write(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n")
        handle.write("{坏行不是 JSON}\n")
    summary_m = write_packs(head, out_m, merge=True)
    got = [json.loads(line) for line in target.read_text(encoding="utf-8").strip().split("\n")]
    got_h = [row["h"] for row in got]
    check("merge 保留既有独有行", old_only["h"] in got_h, got_h[:3])
    check("merge 同 h 新值胜出",
          [row["t"] for row in got if row["h"] == entries[0]["h"]] == [entries[0]["t"]])
    check("merge 计数 added/merged/existing_bad",
          summary_m["added"].get(mod0) == len(head) - 1
          and summary_m["merged"].get(mod0) == 2 and summary_m["existing_bad"] == 1,
          (summary_m["added"], summary_m["merged"], summary_m["existing_bad"]))
    check("merge 后行数=既有保留+新增且按 h 升序",
          summary_m["files"][mod0] == len(got) == summary_m["total"]
          and got_h == sorted(got_h) and len(set(got_h)) == len(got_h), got_h)

    # 7d. prune 语义：True 删除本次未产出的既有包，False（--no-prune）保留
    out_p = tmpdir / "out-prune"
    out_p.mkdir(parents=True, exist_ok=True)
    stale = out_p / ("NoSuchMod" + OUT_SUFFIX)
    stale_row = '{"h":"00000000000000bb","n":"x","s":"X","t":"X","mod":"NoSuchMod"}\n'
    stale.write_text(stale_row, encoding="utf-8")
    write_packs(head, out_p, prune=True)
    check("prune=True 删除本次未产出的既有包", not stale.exists())
    stale.write_text(stale_row, encoding="utf-8")
    write_packs(head, out_p, prune=False)
    check("prune=False（--no-prune）保留本次未产出的既有包", stale.exists())
    parsed = _build_parser().parse_args(["--no-prune", "--merge"])
    check("CLI --no-prune / --merge 解析", parsed.no_prune is True and parsed.merge is True)
    check("CLI 默认不 prune 不 merge",
          _build_parser().parse_args([]).no_prune is False and _build_parser().parse_args([]).merge is False)

    # 7b. G8-only 但无中文可译 -> keep（G8 不放宽）；G6-only -> gated_ratio 不重试
    check("keep_no_cjk 正例（Delta-V->Δv）",
          keep_no_cjk("<b>Delta-V</b>: {0} m/s", "<b>Δv</b>：{0} m/s"))
    check("keep_no_cjk 负例（英文整句抄回）",
          not keep_no_cjk("Insufficient electric charge", "Insufficient electric charge"))
    check("keep_no_cjk 负例（有中文）", not keep_no_cjk("Launch", "发射"))
    probe_runner = Runner(_OfflineConfig(), glossary,
                          T.Cache(tmpdir / "probe-cache.jsonl", enabled=False),
                          jobs=1, offline=True, log=lambda *_a: None)
    _r, _f, _n, item_g8 = _gate_probe("<b>Delta-V</b>: {0} m/s", "<b>Δv</b>：{0} m/s")
    check("G8 仍在 gate 里被报（未放宽）", "G8" in _f, _f)
    res_g8 = probe_runner._finish(item_g8, "<b>Δv</b>：{0} m/s", ["G8"], {"G8": "译文无中文"})
    check("G8-only 无 CJK -> keep 且 t==s",
          res_g8["status"] == "keep" and res_g8["entry"]["t"] == res_g8["entry"]["s"],
          res_g8["status"])
    _r, _f, _n, item_g6 = _gate_probe("Center of Mass", "质心")
    check("G6 仍在 gate 里被报（未放宽）", "G6" in _f, _f)
    res_g6 = probe_runner._finish(item_g6, "质心", ["G6"], {"G6": "长度比 0.14"})
    check("G6-only -> gated_ratio 且不计 failed",
          res_g6["status"] == "gated_ratio" and probe_runner.stats["items_failed"] == 0,
          (res_g6["status"], probe_runner.stats["items_failed"]))
    check("gated_ratio 不写库（entry 为 None）", res_g6["entry"] is None)
    res_mix = probe_runner._finish(item_g6, "质心", ["G6", "G7"], {"G6": "x", "G7": "y"})
    check("G6 + 其它门禁 -> 仍 failed", res_mix["status"] == "failed", res_mix["status"])

    # 7c. --g6-convention-exempt（默认关）：五条全满足才豁免，依据只来自 conventions.jsonl
    conventions = load_conventions()
    check("conventions.jsonl 12 条", len(conventions) == 12, len(conventions))
    check("conventions 逐字节（Booster=助推器）", conventions.get("Booster") == "助推器",
          conventions.get("Booster"))
    check("conventions 不含散文（Center of Mass=质心）",
          conventions.get("Center of Mass") == "质心", conventions.get("Center of Mass"))
    _r, _f, _n, item_com = _gate_probe("Center of Mass", "质心")
    check("默认不豁免：Center of Mass->质心 仍 G6", "G6" in _f, _f)
    plain_runner = Runner(_OfflineConfig(), glossary,
                          T.Cache(tmpdir / "plain-cache.jsonl", enabled=False),
                          jobs=1, offline=True, log=lambda *_a: None, conventions=conventions)
    check("默认 Runner 的 _eval 不豁免",
          set(plain_runner._eval(item_com, "质心")[1]) == {"G6"},
          plain_runner._eval(item_com, "质心")[1])
    exempt_runner = Runner(_OfflineConfig(), glossary,
                           T.Cache(tmpdir / "exempt-cache.jsonl", enabled=False),
                           jobs=1, offline=True, log=lambda *_a: None,
                           g6_exempt=True, conventions=conventions)
    restored_x, failed_x, _notes_x = exempt_runner._eval(item_com, "质心")
    check("豁免开启：Center of Mass->质心 通过", not failed_x, failed_x)
    check("豁免计入 g6_exempt", exempt_runner.stats["g6_exempt"] == 1,
          exempt_runner.stats["g6_exempt"])
    check("豁免写明细（gated_ratio_exempt）",
          len(exempt_runner.stats["gated_ratio_exempt"]) == 1,
          exempt_runner.stats["gated_ratio_exempt"])
    _r, _f, _n, item_alias = _gate_probe("Center of Mass", "重心")
    check("豁免只认约定译文（重心 不豁免）",
          set(exempt_runner._eval(item_alias, "重心")[1]) == {"G6"},
          exempt_runner._eval(item_alias, "重心")[1])
    _r, _f, _n, item_long = _gate_probe("Center of Mass of the vessel", "质心")
    check("豁免要求整串等于术语（半译不放过）",
          set(exempt_runner._eval(item_long, "质心")[1]) == {"G6"},
          exempt_runner._eval(item_long, "质心")[1])
    _r, _f, _n, item_charge = _gate_probe("Insufficient electric charge", "电能不足")
    check("整句不是术语 -> 不豁免（电能不足 仍 G6）",
          set(exempt_runner._eval(item_charge, "电能不足")[1]) == {"G6"},
          exempt_runner._eval(item_charge, "电能不足")[1])
    empty_runner = Runner(_OfflineConfig(), glossary,
                          T.Cache(tmpdir / "empty-cache.jsonl", enabled=False),
                          jobs=1, offline=True, log=lambda *_a: None,
                          g6_exempt=True, conventions={})
    check("conventions 表为空 -> 永不豁免",
          set(empty_runner._eval(item_com, "质心")[1]) == {"G6"},
          empty_runner._eval(item_com, "质心")[1])
    _r, _f, _n, item_g7 = _gate_probe("Reaction Wheel", "反应轮", [("Reaction Wheel", "动量轮")])
    check("豁免不碰 G7", "G7" in _f, _f)

    # 8. 负例：同 h 两译文 -> 只写一行 + il_conflict
    dup = [
        {"h": "aaaa000000000001", "n": "Launch", "s": "Launch", "mod": "M1", "bucket": "short_label",
         "status": "ok", "gates": [], "notes": {},
         "entry": {"h": "aaaa000000000001", "n": "Launch", "s": "Launch", "t": "发射", "mod": "M1"}},
        {"h": "aaaa000000000001", "n": "Launch", "s": "Launch", "mod": "M2", "bucket": "short_label",
         "status": "ok", "gates": [], "notes": {},
         "entry": {"h": "aaaa000000000001", "n": "Launch", "s": "Launch", "t": "起飞", "mod": "M2"}},
    ]
    entries2, conflicts2 = build_entries(dup, "drop")
    check("同 h 两译文只写一行", len(entries2) == 1, len(entries2))
    check("同 h 两译文记 il_conflict", len(conflicts2) == 1 and conflicts2[0]["h"] == "aaaa000000000001")
    check("il_conflict 带两方译文", conflicts2 and conflicts2[0]["kept"]["t"] == "发射"
          and conflicts2[0]["dropped"]["t"] == "起飞")

    # 9. ctx 模式：drop 不写 ctx（运行时可达）；keep 写 ctx（Ctx 级，运行时不命中）
    with_ctx = [dict(dup[0], ctxs=["il:Asm.dll|Type|Method(Int32)|100"])]
    entries3, _ = build_entries(with_ctx, "keep")
    check("ctx_mode=keep 写 ctx", entries3[0].get("ctx", "").startswith("il:"), entries3[0].get("ctx"))
    entries4, _ = build_entries(with_ctx, "drop")
    check("ctx_mode=drop 不写 ctx", "ctx" not in entries4[0])
    check("ctx 模式下字段集合仍受约束",
          set(entries3[0].keys()) == {"h", "n", "s", "t", "mod", "ctx"}, entries3[0].keys())

    # 10. 现有包不参与 IL 输出（不写 .il.jsonl 的 h 进 existing 集合）
    tmp_packs = tmpdir / "packs"
    tmp_packs.mkdir()
    (tmp_packs / "Squad.jsonl").write_text(
        json.dumps({"h": "bbbb000000000001", "n": "Science", "s": "Science",
                    "t": "科学", "mod": "Squad"}, ensure_ascii=False) + "\n", encoding="utf-8")
    (tmp_packs / "Squad.il.jsonl").write_text(
        json.dumps({"h": "cccc000000000001", "n": "Tank", "s": "Tank",
                    "t": "储箱", "mod": "Squad"}, ensure_ascii=False) + "\n", encoding="utf-8")
    hashes = existing_pack_hashes(tmp_packs)
    check("existing 含 cfg 包 h", "bbbb000000000001" in hashes)
    check("existing 不含 .il.jsonl h", "cccc000000000001" not in hashes)

    # 7b. 人工覆盖通道（--overrides，lead m01628 裁定：渲染路径词不建术语表，给人纠错路径）
    ov_path = tmpdir / "overrides.jsonl"
    ov_path.write_text("".join(json.dumps(r, ensure_ascii=False) + "\n" for r in [
        {"en": "Toggle <<1>>", "zh": "开关 <<1>>", "note": "命中"},
        {"en": "Toggle <<1>>", "zh": "开关 <<2>>", "note": "同 en 冲突行"},
        {"en": "Boost <<1>>", "zh": "助推", "note": "占位符不符"},
        {"en": "<b>Warn</b>", "zh": "警告", "note": "富文本不符"},
        {"en": "Not In Corpus", "zh": "语料外", "note": "未命中"},
    ]), encoding="utf-8")
    ov_table, ov_meta = load_overrides(ov_path)
    check("overrides 读入 4 条（冲突行被丢弃）", len(ov_table) == 4, len(ov_table))
    check("overrides 同 en 冲突只取第一行", ov_meta["conflicts"] == 1, ov_meta)
    check("overrides 坏行 0", ov_meta["bad"] == 0, ov_meta)
    check("overrides 键是 normalize(en)", normalize("Toggle <<1>>") in ov_table)
    empty_ov = tmpdir / "overrides-empty.jsonl"
    empty_ov.write_text("", encoding="utf-8")
    check("overrides 空文件不报错", load_overrides(empty_ov) == ({}, {"conflicts": 0, "bad": 0}),
          load_overrides(empty_ov))
    check("overrides 文件缺失不报错", load_overrides(tmpdir / "nope.jsonl")[0] == {})
    ov_cache_path = tmpdir / "ov-cache.jsonl"
    ov_runner = Runner(_OfflineConfig(), glossary, T.Cache(ov_cache_path, enabled=True),
                       jobs=1, offline=True, log=lambda *_a: None, overrides=ov_table)
    _r, _f, _n, ov_item = _gate_probe("Toggle <<1>>", "开关 <<1>>")
    ov_res = ov_runner._override(ov_item)
    check("override 命中 -> status override", ov_res and ov_res["status"] == "override",
          ov_res and ov_res["status"])
    check("override 命中 -> t 是人工译文", ov_res and ov_res["entry"]["t"] == "开关 <<1>>",
          ov_res and ov_res["entry"])
    check("override 行字段集合仍是 h/n/s/t/mod",
          ov_res and set(ov_res["entry"].keys()) == {"h", "n", "s", "t", "mod"},
          ov_res and sorted(ov_res["entry"].keys()))
    check("override 计入 overrides_applied", ov_runner.stats["overrides_applied"] == 1,
          ov_runner.stats["overrides_applied"])
    ov_res2 = ov_runner._override(ov_item)
    check("override 幂等（第二遍同一译文）", ov_res2 and ov_res2["entry"]["t"] == ov_res["entry"]["t"])
    check("override 不入缓存（缓存文件 0 行）",
          not ov_cache_path.exists() or ov_cache_path.stat().st_size == 0,
          ov_cache_path.read_text(encoding="utf-8")[:200] if ov_cache_path.exists() else "")
    _r, _f, _n, ov_boost = _gate_probe("Boost <<1>>", "助推")
    check("override 占位符不符 -> 被拒（返回 None 走模型）", ov_runner._override(ov_boost) is None)
    _r, _f, _n, ov_rich = _gate_probe("<b>Warn</b>", "警告")
    check("override 富文本不符 -> 被拒", ov_runner._override(ov_rich) is None)
    check("override 拒绝明细 2 条且带原因",
          [r["why"] for r in ov_runner.stats["override_rejected"]] == ["placeholder", "richtext"],
          ov_runner.stats["override_rejected"])
    _r, _f, _n, ov_miss = _gate_probe("Not In Corpus At All", "语料外")
    check("override 未命中 -> 不动（None）", ov_runner._override(ov_miss) is None)
    check("override 未命中不计 applied", ov_runner.stats["overrides_applied"] == 2,
          ov_runner.stats["overrides_applied"])
    check("override 不改源文", ov_res["entry"]["s"] == "Toggle <<1>>", ov_res["entry"]["s"])
    check("override 的 h/n 与模型路径同源",
          ov_res["entry"]["h"] == hash_text(normalize("Toggle <<1>>")), ov_res["entry"]["h"])
    real_ov, real_meta = load_overrides()
    # 条数不写死：人工表会随翻译批次增长（v1.3 的 283 条 -> v1.4 的 3,736 条）。
    # 这条断言要守的是「表能读、无冲突、无坏行」，不是某个具体规模。
    check("packs/overrides.jsonl 可读 / >=283 条 / 冲突 0 / 坏行 0",
          len(real_ov) >= 283 and real_meta == {"conflicts": 0, "bad": 0},
          (len(real_ov), real_meta))
    # lead m01857 裁定一：Research & Development 走人工覆盖，短译「研发」是正确译法（不靠括号补英文骗 G6）
    check("R&D 覆盖成最短正确译法「研发」",
          real_ov.get(normalize("Research & Development"), {}).get("zh") == "研发",
          real_ov.get(normalize("Research & Development")))
    check("R&D 覆盖能过 override_gate（无占位符/标签）",
          override_gate("Research & Development", "研发") == "",
          override_gate("Research & Development", "研发"))
    check("overrides 渲染路径词按 lead 裁定（前向/延迟）",
          any("前向激活" in v["zh"] for v in real_ov.values())
          and any("延迟激活" in v["zh"] for v in real_ov.values()),
          [v["zh"] for v in real_ov.values()])

    # 11. 骨架通道（bucket=pattern）+ G9
    pat_runner = Runner(_OfflineConfig(), glossary,
                        T.Cache(tmpdir / "pattern-cache.jsonl", enabled=False),
                        jobs=1, offline=True, log=lambda *_a: None)
    check("G9 正例：# 个数一致 -> 无门禁",
          pat_runner._eval(_pattern_probe("Thrust Ratio: #"), "推力比：#")[1] == [],
          pat_runner._eval(_pattern_probe("Thrust Ratio: #"), "推力比：#")[1])
    check("G9 负例：译文漏掉 # -> G9",
          "G9" in pat_runner._eval(_pattern_probe("Thrust Ratio: #"), "推力比")[1],
          pat_runner._eval(_pattern_probe("Thrust Ratio: #"), "推力比")[1])
    check("G9 负例：译文多一个 # -> G9",
          "G9" in pat_runner._eval(_pattern_probe("Thrust Ratio: #"), "推力比：##")[1],
          pat_runner._eval(_pattern_probe("Thrust Ratio: #"), "推力比：##")[1])
    check("G9 正例：TMP 标签里的 # 不算占位符",
          pat_runner._eval(_pattern_probe("<color=#FFFFFF>#/#kN Thruster</color>"),
                           "<color=#FFFFFF>#/#kN 推进器</color>")[1] == [],
          pat_runner._eval(_pattern_probe("<color=#FFFFFF>#/#kN Thruster</color>"),
                           "<color=#FFFFFF>#/#kN 推进器</color>")[1])
    check("G9 计入 gates 统计（stats 里有 G9 键）", "G9" in pat_runner.stats["gates"],
          sorted(pat_runner.stats["gates"]))
    check("override_gate(pattern) 拦 # 个数不符",
          override_gate("Thrust Ratio: #", "推力比", pattern=True) == "skeleton-slot",
          override_gate("Thrust Ratio: #", "推力比", pattern=True))
    check("override_gate(pattern) 放行 # 个数正确",
          override_gate("Thrust Ratio: #", "推力比：#", pattern=True) == "",
          override_gate("Thrust Ratio: #", "推力比：#", pattern=True))
    check("override_gate 非 pattern 不看 #",
          override_gate("Thrust Ratio: #", "推力比") == "",
          override_gate("Thrust Ratio: #", "推力比"))
    check("bucket_of 认 pattern", bucket_of({"bucket": "pattern"}, "# x") == PATTERN_BUCKET,
          bucket_of({"bucket": "pattern"}, "# x"))
    check("prefilter 对 pattern 不判 key-ref",
          prefilter({"text": "# bit indices", "bucket": PATTERN_BUCKET, "h": "d0d0d0d0d0d0d0d0"})
          is None,
          prefilter({"text": "# bit indices", "bucket": PATTERN_BUCKET, "h": "d0d0d0d0d0d0d0d0"}))
    check("prefilter 对真实文案仍判 key-ref",
          prefilter({"text": "#autoLOC_123", "bucket": "translate", "h": "d0d0d0d0d0d0d0d1"})
          == "key-ref")
    pat_entries, _pc = build_entries([
        {"h": "e0e0e0e0e0e0e0e0", "n": "Thrust Ratio: #", "s": "Thrust Ratio: #",
         "mod": "TestFlight", "bucket": PATTERN_BUCKET, "status": "override", "gates": [],
         "notes": {}, "entry": {"h": "e0e0e0e0e0e0e0e0", "n": "Thrust Ratio: #",
                               "s": "Thrust Ratio: #", "t": "推力比：#",
                               "mod": "TestFlight", "p": 1}},
    ], "drop")
    check("pattern 落库写 p:1", pat_entries[0].get("p") == 1, pat_entries[0])
    check("p 在允许字段里", "p" in ALLOWED_FIELDS, ALLOWED_FIELDS)
    check("非 pattern 行不写 p",
          "p" not in build_entries(dup, "drop")[0][0], build_entries(dup, "drop")[0][0])
    pat_items = build_items([{"text": "Field #: #", "h": "%016x" % i, "n": "Field #: #",
                              "host": "M%d" % (i % 3), "bucket": PATTERN_BUCKET}
                             for i in range(70)])
    pat_batches = build_batches(pat_items)
    check("pattern 批上限 60（70 条 -> >=2 批）",
          len(pat_batches) >= 2 and max(len(b["items"]) for b in pat_batches) <= PATTERN_BATCH_CAP,
          [len(b["items"]) for b in pat_batches])
    check("pattern item 的 role 是 pattern", pat_items[0]["role"] == "pattern", pat_items[0]["role"])

    # 11.1 骨架通道的输入归一：具体实例 -> 骨架。
    # 运行时是 TextSkeleton.TryBuild(实时文本) -> DictPack.TryGetPattern(hash(骨架), 骨架)
    # -> FillBack 填回数字，所以落库的 s 必须是骨架、h 必须是骨架的哈希，否则一条都查不到。
    pat_capture = tmpdir / "pattern-capture.jsonl"
    pat_capture.write_text("\n".join([
        json.dumps({"s": "Aero: Stock | RH preset: 12 %", "bucket": "pattern",
                    "mod": "TestFlight", "h": "1111111111111111"}, ensure_ascii=False),
        json.dumps({"s": "Aero: Stock | RH preset: 33 %", "bucket": "pattern",
                    "mod": "TestFlight", "h": "1111111111111112"}, ensure_ascii=False),
        json.dumps({"s": "Aero: FAR | RH preset: 7.5 %", "bucket": "pattern",
                    "mod": "TestFlight", "h": "2222222222222222"}, ensure_ascii=False),
        json.dumps({"s": "Loading scenery", "bucket": "pattern",
                    "mod": "Kopernicus", "h": "3333333333333333"}, ensure_ascii=False),
    ]) + "\n", encoding="utf-8")
    pat_cands = load_candidates([pat_capture], "capture")
    pat_groups, _pat_conflicts = dedupe(pat_cands)
    pat_texts = sorted(g["text"] for g in pat_groups.values())
    check("pattern 实例先骨化成骨架再分组",
          pat_texts == sorted(["Aero: FAR | RH preset: # %", "Aero: Stock | RH preset: # %",
                               "Loading scenery"]),
          pat_texts)
    check("pattern 同骨架的多实例合并成一行（12%/33% -> 1 组 count=2）",
          sum(1 for g in pat_groups.values()
              if g["text"] == "Aero: Stock | RH preset: # %" and g["count"] == 2) == 1,
          [(g["text"], g["count"]) for g in pat_groups.values()])
    check("pattern 的 h 用骨架算（不是实例的 h）",
          any(g["h"] == hash_text(normalize("Aero: Stock | RH preset: # %"))
              for g in pat_groups.values()),
          sorted(g["h"] for g in pat_groups.values()))
    check("pattern 无数字段 -> prefilter 拦 pattern-no-slots",
          prefilter([g for g in pat_groups.values()
                     if g["text"] == "Loading scenery"][0]) == "pattern-no-slots",
          prefilter([g for g in pat_groups.values() if g["text"] == "Loading scenery"][0]))
    pat_skel_capture = tmpdir / "pattern-skel-capture.jsonl"
    pat_skel_capture.write_text(json.dumps(
        {"s": "<color=#FFE000><b># ms</b></color>", "bucket": "pattern",
         "mod": "HUD", "h": "4444444444444444"}, ensure_ascii=False) + "\n", encoding="utf-8")
    pat_skel_cands = load_candidates([pat_skel_capture], "capture")
    check("pattern 上游已骨化的行原样保留（不因无数字段被跳过）",
          pat_skel_cands[0]["text"] == "<color=#FFE000><b># ms</b></color>"
          and pat_skel_cands[0]["pattern_no_slots"] is False,
          (pat_skel_cands[0]["text"], pat_skel_cands[0]["pattern_no_slots"]))
    check("pattern 有骨架的组 prefilter 放行",
          prefilter([g for g in pat_groups.values()
                     if g["text"] == "Aero: Stock | RH preset: # %"][0]) is None,
          prefilter([g for g in pat_groups.values()
                     if g["text"] == "Aero: Stock | RH preset: # %"][0]))

    return failures, checks


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _build_parser():
    parser = argparse.ArgumentParser(
        prog="translate_il",
        description="IL 采集 / 运行时捕获候选 -> packs/zh-cn/<mod>.il.jsonl")
    parser.add_argument("--il", default=str(IL_INPUT), help="il-translatable.jsonl 路径")
    parser.add_argument("--capture", default=str(CAPTURE_INPUT),
                        help="capture 候选 jsonl 路径，多个用 ; 分隔"
                             "（例如 capture-candidates.jsonl;cfgcache-candidates.jsonl）")
    parser.add_argument("--source", choices=("auto", "il", "capture"), default="auto")
    parser.add_argument("--out", default=str(OUT_DIR), help="输出目录（默认 packs/zh-cn）")
    parser.add_argument("--glossary", default=str(GLOSSARY_PATH))
    parser.add_argument("--env-file", default=None)
    parser.add_argument("--reports", default=str(REPORTS_DIR))
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--jobs", type=int, default=4)
    parser.add_argument("--max-chars", type=int, default=DEFAULT_MAX_CHARS)
    parser.add_argument("--ctx-mode", choices=CTX_MODES, default="drop")
    parser.add_argument("--mods", default=None, help="只处理这些 mod（逗号分隔）")
    parser.add_argument("--prune", action="store_true",
                        help="即使限跑/单桶也删除本次结果里不存在的 <mod>.il.jsonl（程序集改名后清理旧文件用）")
    parser.add_argument("--no-prune", action="store_true",
                        help="强制不删除任何既有 <mod>.il.jsonl（整跑也保留；无密钥/限跑时必用）")
    parser.add_argument("--merge", action="store_true",
                        help="与既有 <mod>.il.jsonl 合并写回（按 h 覆盖，既有行不丢）")
    parser.add_argument("--only-bucket", default=None,
                        choices=("translate", "short_label", "template", PATTERN_BUCKET))
    parser.add_argument("--dry-run", action="store_true", help="不写 packs（仍会调 API）")
    parser.add_argument("--offline", action="store_true", help="桩响应，不发请求")
    parser.add_argument("--no-cache", action="store_true")
    parser.add_argument("--overrides", default=str(OVERRIDES_PATH),
                        help="人工覆盖 jsonl（默认 packs/overrides.jsonl，缺失即跳过）")
    parser.add_argument("--no-overrides", action="store_true", help="禁用人工覆盖")
    parser.add_argument("--g6-convention-exempt", action="store_true",
                        help="整条文本就是锁定约定术语时豁免 G6 长度比下界（默认关）")
    parser.add_argument("--sample-out", default=None, help="逐条审计 jsonl 路径")
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--write-fixture", action="store_true")
    parser.add_argument("--timeout", type=int, default=180)
    return parser


def main(argv=None):
    from . import ensure_stdout
    ensure_stdout()
    args = _build_parser().parse_args(argv)

    if args.write_fixture:
        path, n_il, n_cap = write_fixture()
        print("[fixture] {} 行（il {} + capture {}）→ {}".format(n_il + n_cap, n_il, n_cap, path))
        return 0

    if args.selftest:
        failures, checks = selftest()
        if failures:
            print("[FAIL] {} 项检查，{} 项失败：".format(checks, len(failures)))
            for item in failures:
                print("  - " + item)
            return 1
        print("[OK] {} 项检查，0 项失败".format(checks))
        return 0

    started = time.time()
    paths = []
    if args.source in ("auto", "il"):
        paths.append(Path(args.il))
    if args.source in ("auto", "capture"):
        # 多个候选文件用 ';' 分隔：采集通道（运行时缺命中）+ cfgcache 通道
        # （ModuleManager 补丁后的最终配置文本）是两条独立的候选来源，缺一不可。
        for part in str(args.capture).split(";"):
            if part.strip():
                paths.append(Path(part.strip()))
    paths = [p for p in paths if str(p).strip() not in ("", ".")]
    existing_paths = [p for p in paths if p.is_file()]
    missing = [str(p) for p in paths if not p.is_file()]
    for name in missing:
        print("[il] 输入不存在，跳过：" + name)

    cands = load_candidates(existing_paths, args.source)
    if args.mods:
        wanted = {m.strip().lower() for m in args.mods.split(",") if m.strip()}
        cands = [c for c in cands if any(m.lower() in wanted for m in c["mods"])]
    print("[il] 候选 {} 行（来自 {} 个文件）".format(len(cands), len(existing_paths)))

    # 注意：必须拿原始行（mods 里还是 ModuleManager.4.2.3.dll），cands 里的 mods 已被归一，
    # 在归一后的数据上算 asm_normalized 永远是空表（实测踩过）。
    raw_rows = []
    for path in existing_paths:
        raw_rows.extend(iter_jsonl(path))
    asm_normalized = asm_normalized_list(raw_rows)
    if asm_normalized:
        print("[il] 程序集名归一（去尾部版本号）{} 个：{}".format(
            len(asm_normalized),
            ", ".join("{} -> {}".format(row["base"], row["mod"]) for row in asm_normalized)))

    groups, input_conflicts = dedupe(cands)
    merged = sum(1 for g in groups.values() if len(g["mods"]) > 1)
    print("[il] 去重 {} 组；跨 mod 合并 {} 组；同 h 原文变体 {} 组".format(
        len(groups), merged, len(input_conflicts)))

    # 人工覆盖必须先加载：prefilter 需要它的 h 集合来豁免启发式跳过（no-words 会误杀单 token UI 文案）
    if args.no_overrides:
        overrides, override_meta = {}, {"conflicts": 0, "bad": 0}
    else:
        overrides, override_meta = load_overrides(args.overrides)
    if args.no_overrides:
        print("[il] 人工覆盖已禁用（--no-overrides）")
    elif overrides:
        print("[il] 人工覆盖 packs/overrides.jsonl {} 条（冲突 {} / 坏行 {}）".format(
            len(overrides), override_meta.get("conflicts", 0), override_meta.get("bad", 0)))
    else:
        print("[il] 人工覆盖：packs/overrides.jsonl 不存在或为空，跳过")
    override_hashes = {hash_text(key) for key in overrides}

    existing = existing_pack_hashes(args.out)
    print("[il] 现有 cfg 包 h {} 个（同 h 不再写 IL 行）".format(len(existing)))

    skip_reasons = {}
    kept = []
    for group in groups.values():
        reason = prefilter(group, existing, override_hashes)
        if reason:
            skip_reasons[reason] = skip_reasons.get(reason, 0) + 1
            continue
        if args.only_bucket and group["bucket"] != args.only_bucket:
            skip_reasons["other-bucket"] = skip_reasons.get("other-bucket", 0) + 1
            continue
        kept.append(group)
    print("[il] 跳过 {} 组：{}".format(sum(skip_reasons.values()), skip_reasons))

    kept.sort(key=lambda g: g["h"])
    if args.limit and args.limit < len(kept):
        kept = _sample(kept, args.limit)
        print("[il] --limit {} → 取样 {} 组".format(args.limit, len(kept)))

    items = build_items(kept)
    batches = build_batches(items, max_chars=args.max_chars)
    print("[il] 待翻 {} 组 / {} 批".format(len(items), len(batches)))
    for batch in batches[:6]:
        print("    - {} {} 条 {} 字符".format(batch["key"], len(batch["items"]),
                                              sum(len(i["masked"]) for i in batch["items"])))
    if len(batches) > 6:
        print("    - ... 共 {} 批".format(len(batches)))

    glossary = T.load_glossary(args.glossary)
    print("[il] 术语表：" + glossary.note)

    cache = T.Cache(CACHE_PATH, enabled=not args.no_cache and not args.offline)
    if args.offline:
        # 桩响应是假译文：① 绝不进缓存（否则真跑命中假数据）② 绝不写 packs（否则假译文进包）
        if not args.no_cache:
            print("[il] offline 模式：缓存已禁用（桩响应不入库）")
        if not args.dry_run:
            args.dry_run = True
            print("[il] offline 模式：已强制 --dry-run（桩响应绝不写 packs）")
        config = _OfflineConfig()
    else:
        config = llmlib.load_config(args.env_file, timeout=args.timeout)
        print("[il] 端点 " + config.describe())

    conventions = load_conventions()
    g6_exempt = args.g6_convention_exempt
    if g6_exempt:
        if conventions:
            print("[il] G6 约定术语豁免已启用：packs/conventions.jsonl {} 条".format(len(conventions)))
        else:
            g6_exempt = False
            print("[il] WARN --g6-convention-exempt 已忽略：packs/conventions.jsonl 不存在或为空")
    runner = Runner(config, glossary, cache, jobs=args.jobs, max_chars=args.max_chars,
                    dry_run=args.dry_run, offline=args.offline,
                    g6_exempt=g6_exempt, conventions=conventions, overrides=overrides)
    results = runner.run(batches)
    cache.close()

    entries, conflicts = build_entries(results, args.ctx_mode)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    provenance = None
    if args.ctx_mode == "drop":
        provenance = REPORTS_DIR / "il-provenance-{}.jsonl".format(stamp)
        if not args.dry_run:
            write_provenance(results, provenance)
    written = write_packs(entries, args.out, dry_run=args.dry_run,
                          prune=(args.prune or (not args.dry_run and not args.limit
                                                and not args.mods and not args.only_bucket))
                                and not args.no_prune,
                          merge=args.merge)
    sample_path = None
    if args.sample_out:
        sample_path = Path(args.sample_out)
        if not sample_path.is_absolute():
            sample_path = Path(args.reports) / sample_path.name
        write_sample(results, sample_path)

    payload = {
        "tool": "translate_il",
        "build": stamp,
        "dry_run": args.dry_run,
        "offline": args.offline,
        "source": args.source,
        "inputs": [str(p) for p in existing_paths],
        "ctx_mode": args.ctx_mode,
        "runtime_reachable": args.ctx_mode == "drop",
        "counts": {
            "candidates": len(cands),
            "groups": len(groups),
            "merged": merged,
            "skipped": sum(skip_reasons.values()),
            "skip_reasons": skip_reasons,
            "to_translate": len(items),
            "batches": len(batches),
            # lead m01753 要求单列的验收字段（其它跳过原因仍只在 skip_reasons 里）
            "existing_h_skipped": skip_reasons.get("existing-h", 0),
        },
        "stats": runner.stats,
        "glossary": {"version": glossary.version, "terms": len(glossary.pairs),
                     "locked": len(glossary.locked)},
        "written": written,
        "provenance": str(provenance) if provenance else None,
        "sample_out": str(sample_path) if sample_path else None,
        "il_conflict": conflicts,
        "input_conflicts": input_conflicts[:50],
        "gated_ratio": runner.stats.get("gated_ratio_examples", []),
        "g6_exempt": runner.stats.get("g6_exempt", 0),
        "existing_h_skipped": skip_reasons.get("existing-h", 0),
        "asm_normalized": asm_normalized,
        "keeps_no_cjk": runner.stats.get("keeps_no_cjk", 0),
        "overrides_total": len(overrides),
        "overrides_applied": runner.stats.get("overrides_applied", 0),
        "override_rejected": runner.stats.get("override_rejected", []),
        "bucket_status": runner.stats.get("bucket_status", {}),
        "gated_ratio_exempt": runner.stats.get("gated_ratio_exempt", []),
        "keep_examples": runner.stats.get("keep_examples", []),
        "failed_items": runner.stats.get("failed_items", []),
        "by_mod": written.get("files", {}),
        "elapsed_s": round(time.time() - started, 1),
        "model": getattr(config, "model", ""),
    }
    report_path = write_report(payload, args.reports)
    print_summary(payload)
    print("[il] 报告 " + str(report_path))
    if args.ctx_mode != "drop":
        print("[il][WARN] ctx_mode={} → ctx 非空的行是 Ctx 级，运行时 TryGet 传 context=null 会跳过"
              "（DictPack.cs:160-172），这些行不会命中。".format(args.ctx_mode))
    return 0


if __name__ == "__main__":
    sys.exit(main())
