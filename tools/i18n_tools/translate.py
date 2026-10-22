"""P4 打包 · P5 并发调用 · P6 校验门禁 · 写 packs/zh-cn（04 §6/§7/§11）。

用法（在 UniversalI18n\tools 目录下）：

    python -m i18n_tools.translate --plan                 # 只统计批次，不联网
    python -m i18n_tools.translate --selftest             # 全池遮蔽往返 + 门禁自测，不联网
    python -m i18n_tools.translate --dry-run --limit 100  # 真调 API，只译 100 条，不写 packs/
    python -m i18n_tools.translate                        # 全量（**先拿到 lead 的 go**）

设计要点（与文档逐条对应）：

- 分批 §6.2：占位符批 40 / 富文本批 60 / 长文本批 20 / 中文本批 120 / 短文本批 200，
  再叠一层字符预算（max_chars，默认 6000）——因为实测 max_tokens **不强制执行**（04 §1.3），
  控制批量的唯一有效手段是"把输入本身压小"。
- 并发 4（§6.4），退避 1s/4s/16s 在 llm.py 里；整批解析失败先修（剥围栏/补括号）再拆半。
- 八门禁 §7 逐条落地，G2（占位符多重集）与 G5（哨兵多重集）是"会崩 UI"的两条，最严格。
- 输出字段与 Core DictPack 严格一致（h / n / s / t / mod，ctx 可选）：
  h = fnv1a64(**n**)（DictEntry 注释：「源文本哈希；等于 n 的哈希」），
  n = textnorm.normalize(source)，t 绝不为空、绝不为 null（JsonLineReader 会判坏行）。
- keep（译文==原文）的条目不写库：DictEntry.IsKeep 在运行时按 miss 计，写进去只是白占内存。
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

from . import ensure_stdout
from . import mask as masklib
from .fnv import hash_text
from .llm import LlmClient, LlmConfigError, load_config, redact
from .report import print_summary, write_report
from .textnorm import has_cjk, is_key_like, is_pure_number, is_url_only, normalize

# ---------------------------------------------------------------------------
# 路径与常量
# ---------------------------------------------------------------------------

REPO = Path(__file__).resolve().parents[2]          # UniversalI18n/
POOL_PATH = REPO / "packs" / "pool.jsonl"
GLOSSARY_PATH = REPO / "packs" / "glossary.jsonl"
OUT_DIR = REPO / "packs" / "zh-cn"
CACHE_PATH = REPO / "reports" / "cache" / "translations.jsonl"
REPORTS_DIR = REPO / "reports"

#: prompt 版本（04 §11.3 缓存键的组成部分；改 system prompt / 门禁语义就必须 +1）
PROMPT_VERSION = 3

#: 单批字符预算上限（含遮蔽后文本），压住不可控的 max_tokens
DEFAULT_MAX_CHARS = 6000

#: 批大小（04 §6.2），顺序即优先级：占位符最贵，先挑出来
BUCKET_LIMITS = (
    ("ph", 40),
    ("rt", 60),
    ("long", 20),
    ("mid", 120),
    ("short", 200),
)

#: 04 §8.2 的十二条专有约定。**优先级高于 glossary**（官方字典里 Apoapsis 记的是"远拱点"，
#: 与我们的约定冲突，所以 G7 判定时这 12 条覆盖 glossary 的同名项）。
CONVENTIONS = (
    ("Booster", "助推器"),
    ("Oxidizer", "氧化剂"),
    ("Throttle", "节流阀"),
    ("Gimbal", "万向节"),
    ("Specific Impulse", "比冲"),
    ("Apoapsis", "远地点"),
    ("Periapsis", "近地点"),
    ("Delta-V", "Δv"),
    ("Center of Mass", "质心"),
    ("Center of Pressure", "压心"),
    ("Reaction Wheel", "动量轮"),
    ("Monopropellant", "单组元推进剂"),
)

#: G7 的"合法同义译法"白名单：只给中文里确实存在标准复合/简称写法的少数几条。
#: 实测教训（dry-run --limit 200）："2014-2018 Oxidizer-Rich Staged Combustion Engines"
#: 被译成"富氧分级燃烧发动机"（航天界标准译法），但 G7 只看"氧化剂"三个字 → 判死 →
#: 单条重试再烧一次 token，模型原样再答一遍。这类假失败必须靠白名单消掉。
CONVENTION_ALIASES = {
    "Booster": ("助推",),
    "Oxidizer": ("富氧", "氧化"),
    "Throttle": ("节流", "油门"),
    "Gimbal": ("万向",),
    "Apoapsis": ("远拱点",),
    "Periapsis": ("近拱点",),
    "Delta-V": ("ΔV", "速度增量"),
    "Center of Mass": ("重心",),
    "Center of Pressure": ("压力中心",),
    "Reaction Wheel": ("反作用轮",),
    "Monopropellant": ("单组元", "单元推进剂"),
}

#: 遮蔽后"只剩不可翻译物"的判定用（04 §5.5：只剩哨兵 → 直接标 keep，不送翻）
_UNTOUCHABLE_KEEP_RE = re.compile(r"[^0-9A-Za-z\u4e00-\u9fff]+")

#: system prompt（04 §8.2 正式版原文；只把术语块换成 __GLOSSARY__ 占位）
SYSTEM_PROMPT = """你是一位资深航天工程师（航天推进与飞行力学方向），同时是 Kerbal Space Program 的资深玩家。
你正在为 KSP 的中文汉化项目做英译中。

【输出格式】
只输出 JSON：{"items":[{"id":<id>,"zh":"<译文>"}]}
不输出解释、不输出 markdown 代码围栏、不在 JSON 前后加任何字符。
id 必须与输入一一对应，不增不减。

【输入格式】
输入是 JSON：{"batch":[{"id":1,"t":"原文","role":"按钮/部件名/描述等","mod":"来源 mod"}]}
t 是要翻译的英文原文，role 与 mod 只用于判断语境，**不要翻译也不要回显**。

【绝对不可改动】
- KSP 占位符 <<1>> <<2>>：数量和编号必须完全一致，位置可随中文语序调整。
- 格式符 {0} {1} %s %d：数量和顺序不可变。
- 富文本标签 <b></b> <i></i> <color=...></color> <size=...>：标签与属性值逐字节保留。
- 哨兵 <<M0>> <<M1>>：原样保留，不得翻译、不得删除、不得改编号。它代表路径、类名或换行。
- 缩写保持英文：EVA、IVA、KSC、VAB、SPH、RCS、SAS、TWR、Δv。
- 单位保持原样：m/s、kN、kg、atm、km、Mm。

【术语（必须逐字使用，优先级高于你的个人习惯）】
__GLOSSARY__

【风格】
- 面向玩家，简洁、专业、不口语化，不加感叹号。
- 按钮与标签尽量不超过 6 个汉字。
- 部件名称保留型号数字与字母（如 OX-4M、RV-105），只译描述性部分。
- 已经是专有名词的 mod 名、公司名、人名不译。

【专有约定】
- Booster=助推器，Oxidizer=氧化剂，Throttle=节流阀，Gimbal=万向节，
  Specific Impulse=比冲，Apoapsis=远地点，Periapsis=近地点，
  Delta-V=Δv，Center of Mass=质心，Center of Pressure=压心，
  Reaction Wheel=动量轮，Monopropellant=单组元推进剂。"""

#: 没有 glossary 时的降级提示（不报错，只降级并在报告里写明）
_NO_GLOSSARY = "（本批无强制术语表，按上述专有约定与航天惯例处理）"

GATES = ("G1", "G2", "G3", "G4", "G5", "G6", "G7", "G8")

#: 反引号只能用 chr(96) 拼（本文件由脚本生成，源码里不出现字面反引号）
BT1 = chr(96)
BT3 = BT1 * 3

_WORD_RE = re.compile(r"[A-Za-z][A-Za-z0-9'\-]*")
_TRANSLATABLE_WORD_RE = re.compile(r"[A-Za-z][A-Za-z'\-]{2,}")


# ---------------------------------------------------------------------------
# 读入
# ---------------------------------------------------------------------------

def load_pool(path=None):
    """读 pool.jsonl。返回 list[dict]（按文件顺序，id 不连续也没关系）。"""
    path = Path(path or POOL_PATH)
    rows = []
    with path.open("r", encoding="utf-8") as handle:
        for number, line in enumerate(handle, 1):
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except ValueError as exc:
                raise SystemExit("pool.jsonl 第 {} 行不是合法 JSON：{}".format(number, exc))
            if "text" not in row or "mod" not in row:
                raise SystemExit("pool.jsonl 第 {} 行缺 text/mod 字段".format(number))
            rows.append(row)
    return rows


#: 单条 glossary 条目能不能当"术语"注入：短、无中文、≤4 词、不是单字母/泛义词
_TERM_MAX_CHARS = 48
_TERM_STOP = frozenset("""
r i ii iii iv v vi x a b c d e f g n up down left right arm small large big
on off in out open close yes no max min all any next prev more less
km m s t h kg kN atm Mm dps kpa
""".split())

#: 十二条专有约定的小写名（判断"这是不是已锁术语"用）
CONVENTION_EN = tuple(en for en, _zh in CONVENTIONS)
_CONVENTION_LOWER = frozenset(en.lower() for en in CONVENTION_EN)

_TERM_PATTERN_CACHE = {}


def _term_pattern(term):
    """术语的整体词匹配正则（大小写不敏感）。缓存避免 225 批 × 830 条重复编译。"""
    pattern = _TERM_PATTERN_CACHE.get(term)
    if pattern is None:
        body = r"\s+".join(re.escape(part) for part in term.split())
        pattern = re.compile(r"(?<![A-Za-z])" + body + r"(?![A-Za-z])", re.IGNORECASE)
        _TERM_PATTERN_CACHE[term] = pattern
    return pattern


def _is_term_candidate(en, zh):
    """能否当术语注入 prompt（04 §8.4）。"""
    en = (en or "").strip()
    zh = (zh or "").strip()
    if not en or not zh:
        return False
    if len(en) > _TERM_MAX_CHARS or re.search(r"[\u4e00-\u9fff]", en):
        return False        # 太长（整句译文）或本来就是中文
    words = en.split()
    if not words or len(words) > 4:
        return False
    if len(en) < 3 or en.lower().rstrip(".") in _TERM_STOP:
        return False        # 单字母 R / 泛义词 Up 之类：注入只会诱导模型硬套
    return True


def _is_locked_term(en):
    """G7 硬判定的范围：短语级官方术语（≥2 词且 ≥8 字符）。

    单个通用词不进 G7——模型把 Arm 意译成"启用"是合理的，锁死会制造假失败
    （每次假失败都要单条重试，白烧 token）。术语漂移走 §9 的软统计 stats["term_drift"]。
    """
    en = (en or "").strip()
    return len(en) >= 8 and len(en.split()) >= 2


class Glossary:
    """packs/glossary.jsonl 的四张表。

    实测 data-engineer 产出的是**官方译文记忆**（键 #autoLOC_*，字段 key/en/zh/short），
    不是"English=中文"术语表。所以拆开用：

    1. pairs  —— 可注入 prompt 的术语（短条目），供 §8.4 批内注入；
    2. locked —— 其中短语级的，G7 逐字判定；
    3. exact  —— normalize(en) → 官方 zh：源文本整条命中就直用官方译文（零 token）；
    4. keyref —— "#autoLOC_*" → 官方 zh：--keyref-rescue 时救回未解析的键引用。
    """

    def __init__(self, pairs=None, source=None, note="", keyref=None, exact=None,
                 total=None, bad=0):
        self.pairs = list(pairs or [])          # [(en, zh)]，保持文件顺序
        self.map = {}                           # 小写 en -> (en, zh)
        for en, zh in self.pairs:
            key = en.strip().lower()
            if key:
                self.map.setdefault(key, (en.strip(), zh.strip()))
        self.locked = {}
        for en, zh in self.pairs:
            if _is_locked_term(en):
                self.locked.setdefault(en.strip(), zh.strip())
        self.exact = dict(exact or {})
        self.keyref = dict(keyref or {})
        self.total = total if total is not None else len(self.pairs)
        self.bad = bad
        self.source = source
        self.note = note
        self.version = self._version()

    def _version(self) -> str:
        """术语表版本 = 四张表排序后的 FNV-1a 64 前 8 位（04 §11.3 缓存键的一部分）。"""
        if not self.pairs and not self.exact and not self.keyref:
            return "none"
        blob = "\n".join("{}={}".format(en, zh) for en, zh in sorted(self.pairs))
        blob += "\n" + "\n".join("{}={}".format(en, zh) for en, zh in sorted(self.exact.items()))
        blob += "\n" + "\n".join("{}={}".format(key, zh) for key, zh in sorted(self.keyref.items()))
        return hash_text(blob)[:8]

    def __len__(self):
        return len(self.pairs)

    def lookup(self, key):
        return self.map.get(key.lower())


def load_glossary(path=None) -> Glossary:
    """读 packs/glossary.jsonl。文件不存在 → 返回空表并在 note 里说明（不报错退出）。

    字段名宽容：en|english|src|source|term|from × zh|cn|zh-cn|chinese|target|translation|to。
    带 key 字段（#autoLOC_*）的行同时进 keyref 表，供 --keyref-rescue 使用。
    """
    path = Path(path or GLOSSARY_PATH)
    if not path.exists():
        return Glossary(note="packs/glossary.jsonl 不存在，本次不注入术语表")
    pairs = []
    keyref = {}
    exact = {}
    bad = 0
    total = 0
    with path.open("r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            total += 1
            try:
                row = json.loads(line)
            except ValueError:
                bad += 1
                continue
            if not isinstance(row, dict):
                bad += 1
                continue
            en = _pick(row, ("en", "english", "src", "source", "term", "from"))
            zh = _pick(row, ("zh", "cn", "zh-cn", "chinese", "target", "translation", "to"))
            if not en or not zh:
                bad += 1
                continue
            key = _pick(row, ("key", "id", "loc"))
            if key and key.startswith("#"):
                keyref.setdefault(key, zh)
            cleaned = normalize(en)
            if cleaned:
                exact.setdefault(cleaned, zh)
            if _is_term_candidate(en, zh):
                pairs.append((en, zh))
    note = ("官方译文记忆 {} 条：术语 {}（注入）/ {}（短语级锁 G7）/ 键引用 {} / 整句直用 {}（坏行 {}）"
            .format(total, len(pairs), len([1 for en, _ in pairs if _is_locked_term(en)]),
                    len(keyref), len(exact), bad))
    return Glossary(pairs, source=str(path), note=note, keyref=keyref, exact=exact,
                    total=total, bad=bad)


def _pick(row, keys):
    for key in keys:
        value = row.get(key)
        if isinstance(value, str) and value.strip():
            return value.strip()
    return None


# ---------------------------------------------------------------------------
# 分类：哪些条根本不送翻（keep），哪些要译
# ---------------------------------------------------------------------------

def _strip_markup(text):
    """去掉占位符/标签/哨兵，只看"人话"部分。"""
    text = masklib.SENTINEL_RE.sub(" ", text)
    text = masklib.PLACEHOLDER_RE.sub(" ", text)
    text = masklib.FORMAT_RE.sub(" ", text)
    text = re.sub(r"<[^<>]*>", " ", text)
    return text


def has_translatable_word(text) -> bool:
    """有没有"值得译的英文词"。

    全大写缩写（EVA/KSC/RCS）、单位（m/s、kN）、型号（RD-107）都不值得送模型：
    模型只能原样抄回，白白占 token，还会踩 G8（译文==原文）。
    """
    for word in _TRANSLATABLE_WORD_RE.findall(_strip_markup(text)):
        if any(ch.islower() for ch in word):
            return True
    return False


def classify(row):
    """返回 (action, reason)。action ∈ {"skip", "translate"}。顺序即优先级。"""
    text = row["text"]
    stripped = text.strip()
    if text.startswith("#") or stripped.startswith("#"):
        return "skip", "key-ref"            # 未解析的本地化键引用（实测 3,193 条）
    if is_key_like(stripped):
        return "skip", "is-key"
    if not stripped:
        return "skip", "empty"
    if has_cjk(stripped):
        return "skip", "already-cjk"
    if is_pure_number(stripped):
        return "skip", "pure-number"
    if is_url_only(stripped):
        return "skip", "url-only"
    if len(stripped) < 2:
        return "skip", "too-short"
    if not has_translatable_word(stripped):
        return "skip", "no-words"           # EVA / Δv / 3.5 m/s / RD-107
    if masklib.is_all_sentinel(masklib.mask(stripped).text):
        return "skip", "all-sentinel"       # 遮蔽后只剩哨兵（04 §5.5）
    return "translate", ""


_ROLE_BY_SUFFIX = (
    ("_title", "part_title"),
    ("_titledesc", "part_description"),
    ("_desc", "part_description"),
    ("_description", "part_description"),
    ("_name", "name"),
    ("_text", "text"),
    ("_label", "label"),
    ("_button", "button"),
)

_ROLE_BY_FIELD = {
    "title": "part_title",
    "titledesc": "part_description",
    "description": "part_description",
    "name": "name",
    "text": "text",
    "label": "label",
    "buttonname": "button",
    "guiname": "window_title",
}


def role_of(row) -> str:
    """由 key / field 推断语境（04 §6.1：只给 t/role/mod，不给完整 key、不给路径）。"""
    key = (row.get("key") or "").lower()
    field = (row.get("field") or "").lower()
    if field in _ROLE_BY_FIELD:
        return _ROLE_BY_FIELD[field]
    for suffix, role in _ROLE_BY_SUFFIX:
        if key.endswith(suffix):
            return role
    return "label" if len(row["text"]) <= 24 else "description"


def bucket_of(row) -> str:
    """分批桶（04 §6.2）。占位符最优先，其次富文本，再按长度。"""
    text = row["text"]
    if row.get("ph"):
        return "ph"
    if row.get("rt") or masklib._BREAK_RE.search(text):
        return "rt"
    length = len(text)
    if length > 200:
        return "long"
    if length > 30:
        return "mid"
    return "short"


# ---------------------------------------------------------------------------
# 打包
# ---------------------------------------------------------------------------

def _curated_zh(row, glossary, keyref_rescue=False):
    """零 token 直用官方译文的两种情形：整句命中官方译文记忆 / --keyref-rescue 的键引用。"""
    if glossary is None:
        return None
    text = row["text"].strip()
    if text.startswith("#"):
        if keyref_rescue and text in glossary.keyref:
            return glossary.keyref[text]
        return None
    return glossary.exact.get(normalize(row["text"]))


def build_batches(pool_rows, max_chars=DEFAULT_MAX_CHARS, glossary=None, keyref_rescue=False,
                  group_by_mod=True):
    """把待译行打成批。返回 (batches, items, stats)。

    group_by_mod=False 只用于 --limit 取样跑：取样后每 mod 只剩几条，按 mod 分组会把批拆成
    一堆 1–2 条的小批，系统提示被重复付费，测出的单位成本严重偏高。全量跑永远按 mod 分组。

    先试"官方译文直用"（整句命中译文记忆 / --keyref-rescue 救回键引用）：官方译文过不了
    G2–G8 就退回正常翻译流程，退回的条目不静默丢弃——计入 stats["curated"]["rejected"]。
    """
    glossary = glossary if glossary is not None else Glossary()
    items = []
    skipped = {}
    curated = {"used": 0, "rejected": 0, "keyref_rescue": 0, "exact_used": 0,
               "rescue_examples": [], "exact_examples": []}
    for row in pool_rows:
        official = _curated_zh(row, glossary, keyref_rescue)
        if official is not None:
            res = masklib.mask(row["text"])
            probe = {"id": row.get("id"), "row": row, "res": res, "masked": res.text,
                     "role": role_of(row)}
            restored, failed, _notes = gate(probe, official, locked_for(row["text"], glossary))
            if not failed:
                probe["curated"] = restored
                probe["curated_from"] = "keyref" if row["text"].strip().startswith("#") else "exact"
                curated["used"] += 1
                if row["text"].strip().startswith("#"):
                    curated["keyref_rescue"] += 1
                    if len(curated["rescue_examples"]) < 10:
                        curated["rescue_examples"].append({
                            "id": row.get("id"), "mod": row["mod"],
                            "key": row["text"].strip(), "zh": restored,
                            "how": "套用官方中文记忆（glossary.jsonl），非模型输出"})
                else:
                    curated["exact_used"] += 1
                    if len(curated["exact_examples"]) < 5:
                        curated["exact_examples"].append({
                            "id": row.get("id"), "mod": row["mod"],
                            "en": row["text"][:80], "zh": restored})
                items.append(probe)
                continue
            curated["rejected"] += 1
        action, reason = classify(row)
        if action == "skip":
            skipped[reason] = skipped.get(reason, 0) + 1
            continue
        res = masklib.mask(row["text"])
        item = {
            "id": row.get("id"),
            "row": row,
            "res": res,
            "masked": res.text,
            "role": role_of(row),
        }
        # 04 §5.5：遮蔽后只剩不可翻译物（如 "dblTest:" → "<<M0>>:"）→ 标 keep，零 token 不送翻
        if _only_untouchable(res.text):
            item["keep_auto"] = True
        items.append(item)

    keeps_auto = sum(1 for item in items if item.get("keep_auto"))
    groups = {}
    for item in items:
        if item.get("keep_auto"):
            continue
        bucket = bucket_of(item["row"])
        key = (item["row"]["mod"] if group_by_mod else "*", bucket)
        groups.setdefault(key, []).append(item)

    limits = dict(BUCKET_LIMITS)
    batches = []
    for (mod, bucket), group in sorted(groups.items()):
        if mod == "*":
            mod = None
        limit = limits[bucket]
        chunk, chars = [], 0
        for item in group:
            size = len(item["masked"])
            if chunk and (len(chunk) >= limit or chars + size > max_chars):
                batches.append({"mod": mod, "bucket": bucket, "items": chunk})
                chunk, chars = [], 0
            chunk.append(item)
            chars += size
        if chunk:
            batches.append({"mod": mod, "bucket": bucket, "items": chunk})

    stats = {"skipped": skipped, "items": len(items), "batches": len(batches),
             "keeps_auto": keeps_auto, "curated": curated}
    return batches, items, stats


#: 单批注入的术语上限（04 §8.4：30–60 条）
MAX_TERMS_PER_BATCH = 60


def glossary_for(texts, glossary):
    """只挑"本批真的出现过"的术语（04 §8.4：30–60 条，不注入全表）。

    实现改为"拿 830 条术语去批文本里找"（比 n-gram 命中更准，也不会把 Up/Arm 这种
    半句词拆出来）。十二条专有约定永远覆盖 glossary 的同名项（§8.2 的优先级声明）。
    """
    blob = "\n".join(texts)
    found = {}
    for en, (term_en, zh) in glossary.map.items():
        if _term_pattern(en).search(blob):
            found[term_en] = zh
            if len(found) >= MAX_TERMS_PER_BATCH:
                break
    for term, zh in CONVENTIONS:
        if _term_pattern(term).search(blob):
            found[term] = zh
    return [(en, zh) for en, zh in found.items()]


def locked_for(text, glossary=None):
    """G7 的锁定集合（逐条实例化）：12 条专有约定 + 短语级官方术语，只取本文出现的。"""
    out = []
    for en, zh in CONVENTIONS:
        if _term_pattern(en).search(text):
            out.append((en, zh))
    if glossary is not None:
        for en, zh in glossary.locked.items():
            if en.lower() in _CONVENTION_LOWER:
                continue        # 约定优先，已在上面加过
            if _term_pattern(en).search(text):
                out.append((en, zh))
    return out


def drifts_of(item, restored, soft_pairs):
    """术语漂移（§9）：本文出现了某个官方术语，但译文里没有落成官方译法。

    不拦截、不重试——只计数并留样例，供人复核（§9 明确说漂移走复核不走丢弃）。
    返回**全部**漂移项（lead 要"同一术语多种译法的 top 10 冲突对"，只要第一条算不出冲突对）。
    """
    if not restored:
        return []
    source = item["row"]["text"]
    out = []
    for en, zh in soft_pairs:
        if zh in restored:
            continue
        if _term_pattern(en).search(source):
            out.append({"id": item["id"], "term": en, "want": zh,
                        "src": source[:120], "zh": restored[:120]})
    return out


def drift_of(item, restored, soft_pairs):
    """兼容旧调用：只返回第一条漂移。"""
    found = drifts_of(item, restored, soft_pairs)
    return found[0] if found else None


def system_prompt(glossary_pairs) -> str:
    if glossary_pairs:
        block = "\n".join("{}={}".format(en, zh) for en, zh in glossary_pairs)
    else:
        block = _NO_GLOSSARY
    return SYSTEM_PROMPT.replace("__GLOSSARY__", block)


def user_message(batch) -> str:
    payload = {
        "batch": [
            {
                "id": item["id"],
                "t": item["masked"],
                "role": item["role"],
                "mod": item["row"]["mod"],
            }
            for item in batch
        ]
    }
    return json.dumps(payload, ensure_ascii=False)


# ---------------------------------------------------------------------------
# 解析模型输出
# ---------------------------------------------------------------------------

def strip_fence(content: str) -> str:
    """剥掉 markdown 围栏（04 §1.3：模型偶尔会把 JSON 包进三反引号）。"""
    text = content.strip()
    if BT3 in text:
        text = text.replace(BT3 + "json", "").replace(BT3 + "JSON", "").replace(BT3, "")
    if "~~~" in text:
        text = text.replace("~~~", "")
    return text.strip()


def repair_json(text: str):
    """把"差不多能修"的 JSON 修好：截断补括号、去掉尾逗号。修不了返回 None。"""
    text = strip_fence(text)
    start = text.find("{")
    if start < 0:
        return None
    body = text[start:]
    body = re.sub(r",\s*([}\]])", r"\1", body)      # 尾逗号
    try:
        return json.loads(body)
    except ValueError:
        pass
    for end in range(len(body) - 1, 0, -1):
        if body[end] not in "}]":
            continue
        candidate = re.sub(r",\s*$", "", body[:end + 1])
        for tail in ("", "]}", "}]}", "}}"):
            try:
                parsed = json.loads(candidate + tail)
            except ValueError:
                continue
            if isinstance(parsed, dict):
                return parsed
    return None


def extract_items(content: str):
    """从返回体里取 [{id, zh}]。解析不出来返回 None（调用方据此拆半重试）。"""
    parsed = repair_json(content)
    if parsed is None:
        rows = []
        for match in re.finditer(r"\{[^{}]*\"id\"[^{}]*\}", content):
            try:
                rows.append(json.loads(match.group(0)))
            except ValueError:
                continue
        return rows or None

    if isinstance(parsed, list):
        rows = parsed
    elif isinstance(parsed, dict):
        rows = None
        for key in ("items", "translations", "batch", "data", "result", "results"):
            value = parsed.get(key)
            if isinstance(value, list):
                rows = value
                break
        if rows is None:
            rows = [parsed] if ("id" in parsed or "zh" in parsed or "translation" in parsed) else []
    else:
        return None

    out = []
    for row in rows:
        if not isinstance(row, dict):
            continue
        raw_id = row.get("id", row.get("ID"))
        zh = _pick(row, ("zh", "translation", "t", "target", "text", "cn"))
        if raw_id is None or zh is None:
            continue
        try:
            item_id = int(raw_id)
        except (TypeError, ValueError):
            item_id = raw_id
        out.append({"id": item_id, "zh": zh})
    return out


# ---------------------------------------------------------------------------
# 八门禁（04 §7）
# ---------------------------------------------------------------------------

def _g7_accept(en, zh, restored):
    """G7 单条判定：逐字出现即可；允许大小写差异（Δv/ΔV）与白名单里的同义写法。"""
    if not restored:
        return False
    if zh in restored or zh.lower() in restored.lower():
        return True
    for alt in CONVENTION_ALIASES.get(en, ()):
        if alt in restored or alt.lower() in restored.lower():
            return True
    return False


def _only_untouchable(masked_text):
    """遮蔽后是否只剩不可翻译物（哨兵/占位符/格式符/标签/标点/空白）。

    04 §5.5：遮蔽后只剩哨兵 → 直接标 keep，不送翻。实测（--limit 200）漏了这条：
    id=2210 "dblTest:" 被整体遮蔽成 "<<M0>>:"，模型照抄回来，G8 判"译文等于原文"→
    白烧一次调用 + 一次单条重试。凡"整条都被遮蔽"的文本都该走 keep。
    """
    rest = masklib.SENTINEL_RE.sub(" ", masked_text or "")
    rest = masklib.PLACEHOLDER_RE.sub(" ", rest)
    rest = masklib.FORMAT_RE.sub(" ", rest)
    for tag in masklib.rich_tags(rest):
        rest = rest.replace(tag, " ")
    return not _UNTOUCHABLE_KEEP_RE.sub("", rest).strip()


def gate(item, zh_raw, locked_pairs):
    """对一条译文跑 G2–G8（G1 在批级判定）。返回 (restored, failed_gates, notes)。

    locked_pairs 由 locked_for() 逐条算出来（12 条约定 + 短语级官方术语），
    不在这里做全表扫描：G7 只判"本文真的出现了的锁定术语"。
    """
    row = item["row"]
    res = item["res"]
    source = row["text"]
    failed = []
    notes = {}
    if not isinstance(zh_raw, str):
        zh_raw = "" if zh_raw is None else str(zh_raw)

    # G5：哨兵多重集必须一致（漏抄不产生"残留"，只会静默丢内容），且不得出现表外哨兵
    want = masklib.sentinel_multiset(res.text) if res else {}
    got = masklib.sentinel_multiset(zh_raw)
    if want != got:
        failed.append("G5")
        notes["G5"] = "哨兵多重集 期望{} 实际{}".format(want, got)
    if res is not None:
        invented = masklib.unknown_sentinels(zh_raw, res)
        if invented:
            failed.append("G5")
            notes.setdefault("G5", "表外哨兵 {}".format(invented))

    restored = masklib.restore(zh_raw, res) if res is not None else zh_raw
    if masklib.sentinel_leaks(restored):
        failed.append("G5")
        notes.setdefault("G5", "还原后残留哨兵")

    # G2：KSP 占位符 <<n>> 多重集（最高优先级，不过直接崩 UI）
    if masklib.count_placeholders(source) != masklib.count_placeholders(restored):
        failed.append("G2")
        notes["G2"] = "占位符 期望{} 实际{}".format(
            masklib.count_placeholders(source), masklib.count_placeholders(restored))

    # G3：格式符 {n} / %s
    if masklib.count_formats(source) != masklib.count_formats(restored):
        failed.append("G3")
        notes["G3"] = "格式符 期望{} 实际{}".format(
            masklib.count_formats(source), masklib.count_formats(restored))

    # G4：富文本标签（逐字节，含 <color=...> 的色值与引号写法）
    if masklib.count_rich_tags(source) != masklib.count_rich_tags(restored):
        failed.append("G4")
        notes["G4"] = "标签 期望{} 实际{}".format(
            masklib.count_rich_tags(source), masklib.count_rich_tags(restored))

    # G6：长度比 0.15–1.8（源短于 9 字符时比值噪声太大，不判；
    # 键引用行（#autoLOC_* 16 字符 → 译文两三个字）比值天然偏小，也不判）
    if len(source) >= 9 and not source.lstrip().startswith("#"):
        ratio = len(restored) / len(source)
        if not (0.15 <= ratio <= 1.8):
            failed.append("G6")
            notes["G6"] = "长度比 {:.2f}".format(ratio)

    # G7：与已锁定译法逐字一致（十二条约定 + 短语级官方术语）
    for en, zh in locked_pairs:
        if len(en) < 2 or not _term_pattern(en).search(source):
            continue
        if not _g7_accept(en, zh, restored):
            failed.append("G7")
            notes["G7"] = "术语 {}={} 未出现在译文".format(en, zh)
            break

    # G8：非空、非原文，且译文必须含中文——模型把英文抄回来不算译
    if not restored.strip():
        failed.append("G8")
        notes["G8"] = "空译文"
    elif restored.strip() == source.strip():
        failed.append("G8")
        notes["G8"] = "译文等于原文"
    elif not has_cjk(restored):
        failed.append("G8")
        notes["G8"] = "译文无中文"

    return restored, failed, notes


# ---------------------------------------------------------------------------
# 缓存（04 §11.3）
# ---------------------------------------------------------------------------

def cache_key(source, glossary_version, prompt_version, model) -> str:
    """key = FNV1a(text) + '|' + glossaryV + '|' + promptV + '|' + model"""
    return "{}|{}|{}|{}".format(hash_text(source), glossary_version, prompt_version, model)


class Cache:
    """append-only 的 JSONL 缓存，用于断点续跑（同 key 直接复用，不重复付钱）。"""

    def __init__(self, path=None, enabled=True):
        self.path = Path(path or CACHE_PATH)
        self.enabled = enabled
        self.rows = {}
        self.written = 0
        self._handle = None
        if enabled and self.path.exists():
            with self.path.open("r", encoding="utf-8") as handle:
                for line in handle:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        row = json.loads(line)
                    except ValueError:
                        continue
                    if row.get("k") and isinstance(row.get("zh"), str):
                        self.rows[row["k"]] = row

    def get(self, key):
        return self.rows.get(key)

    def put(self, key, source, zh, status="ok", model=""):
        if not self.enabled:
            return
        row = {"k": key, "s": source, "zh": zh, "st": status, "m": model}
        self.rows[key] = row
        if self._handle is None:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            self._handle = self.path.open("a", encoding="utf-8")
        self._handle.write(json.dumps(row, ensure_ascii=False) + "\n")
        self._handle.flush()
        self.written += 1

    def close(self):
        if self._handle is not None:
            self._handle.close()
            self._handle = None


# ---------------------------------------------------------------------------
# 执行器
# ---------------------------------------------------------------------------

class _OfflineResponse:
    """离线桩响应：只提供 _call 返回后链路需要的字段。"""

    finish_reason = "offline"
    usage = {}
    attempts = 0
    elapsed = 0.0

    def __init__(self, content):
        self.content = content


class Runner:
    def __init__(self, config, glossary, cache, jobs=4, max_chars=DEFAULT_MAX_CHARS,
                 log=print, temperature=0.2, dry_run=False, offline=False):
        self.config = config
        self.glossary = glossary
        self.cache = cache
        self.jobs = jobs
        self.max_chars = max_chars
        self.log = log
        self.temperature = temperature
        self.dry_run = dry_run
        self.offline = offline
        self.client = LlmClient(config, log=log)
        self.stats = {
            "batches": 0,
            "batches_failed": 0,
            "splits": 0,
            "calls": 0,
            "cache_hits": 0,
            "items_requested": 0,
            "items_ok": 0,
            "items_keep": 0,
            "items_failed": 0,
            "items_official": 0,        # 整句命中官方译文记忆（零 token）
            "items_rescue": 0,          # 键引用套用官方中文记忆（零 token，非模型输出）
            "curated_rejected": 0,      # 官方译文过不了门禁，退回翻译
            "term_drift": 0,            # §9 术语漂移出现次数（只计数，不拦截）
            "mixed_lang": 0,            # 半译：译文残留 ≥3 个英文单词
            "mixed_lang_examples": [],
            "mixed_lang_by_mod": {},    # 半译按 mod 分布（lead 要的 top 5）
            "keep_examples": [],        # keep 行样例（t == s，运行时靠它终止下沉）
            "drift_examples": [],
            "drift_counts": {},         # "term -> want" → {count, zh_samples}（lead 要的 top 10 冲突对）
            "terms_injected": 0,        # 术语注入总量（每批注入条数累加）
            "terms_distinct": set(),    # 注入过的术语种类
            "locked_triggered": set(),  # 触发过的短语级锁术语
            "items_locked": set(),      # 有 G7 判定对象的条目 id
            "gates": {name: 0 for name in GATES},
            "gate_examples": {name: [] for name in GATES},
            "failed": [],
            "retried": 0,
            "finish_reasons": {},
            "latencies": [],
            "latency_total": 0.0,
        }

    # -- 单批调用 ---------------------------------------------------------
    # 离线模式用桩响应替换真实请求（--offline），供 CI / 无网环境跑通链路
    def _call(self, items, glossary_pairs):
        if self.offline:
            # 离线桩：不发任何请求，只验证 掩码→分批→解析→门禁→缓存 全链路
            payload = {"items": [{"id": item["id"], "zh": "【离线】" + item["masked"]}
                                 for item in items]}
            self.stats["calls"] += 1
            return _OfflineResponse(json.dumps(payload, ensure_ascii=False))
        messages = [
            {"role": "system", "content": system_prompt(glossary_pairs)},
            {"role": "user", "content": user_message(items)},
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

    def _note_gate(self, name, item, restored, note):
        self.stats["gates"][name] += 1
        bucket = self.stats["gate_examples"][name]
        if len(bucket) < 5:
            bucket.append({
                "id": item["id"],
                "mod": item["row"]["mod"],
                "src": item["row"]["text"][:160],
                "zh": (restored or "")[:160],
                "why": note,
            })

    def process(self, batch, depth=0):
        """处理一批：官方直用 → 缓存 → 调模型 → 解析 → 门禁 → 失败拆分。返回 list[result]。"""
        items = batch["items"]
        soft_pairs = glossary_for([item["masked"] for item in items], self.glossary)
        self.stats["terms_injected"] += len(soft_pairs)
        self.stats["terms_distinct"].update(en for en, _zh in soft_pairs)
        self.stats["batches"] += 1

        def _locked(item):
            """G7 的判定对象（12 条约定 + 短语级锁），顺手统计触发情况。"""
            pairs = locked_for(item["row"]["text"], self.glossary)
            for en, _zh in pairs:
                self.stats["locked_triggered"].add(en)
            if pairs:
                self.stats["items_locked"].add(item["id"])
            return pairs

        results = []
        pending = []
        for item in items:
            if item.get("curated") is not None:
                restored, failed, notes = gate(
                    item, item["curated"], _locked(item))
                if not failed:
                    results.append(self._finish(item, restored, [], {}, batch["bucket"],
                                                soft_pairs=soft_pairs, official=True))
                    continue
                self.stats["curated_rejected"] += 1   # 退回翻译流程，不静默丢弃
            key = cache_key(item["row"]["text"], self.glossary.version, PROMPT_VERSION, self.config.model)
            hit = self.cache.get(key)
            if hit and hit.get("st") == "ok":
                self.stats["cache_hits"] += 1
                restored, failed, notes = gate(
                    item, hit["zh"], _locked(item))
                results.append(self._finish(item, restored, failed, notes, batch["bucket"],
                                            soft_pairs=soft_pairs))
            else:
                pending.append(item)

        if not pending:
            return results
        self.stats["items_requested"] += len(pending)

        try:
            response = self._call(pending, soft_pairs)
        except Exception as exc:                      # 网络/接口层失败：拆半
            self.log("  ! API 失败（{}）：{} 条 depth={}".format(redact(str(exc))[:160], len(pending), depth))
            return results + self._split(batch, pending, soft_pairs, depth, "api")

        parsed = extract_items(response.content)
        if parsed is None:
            self.stats["batches_failed"] += 1
            self.log("  ! 解析失败（finish={}）：{} 条 depth={}".format(
                response.finish_reason, len(pending), depth))
            return results + self._split(batch, pending, soft_pairs, depth, "parse")

        by_id = {}
        for row in parsed:
            by_id.setdefault(row["id"], []).append(row["zh"])
        duplicates = [key for key, value in by_id.items() if len(value) > 1]
        if duplicates:
            self.log("  ! 重复 id（取第一条）：{}".format(duplicates[:5]))

        retry = []      # (item, 上一次的门禁名, 上一次的门禁说明)：单条到底时用于报出真实原因
        for item in pending:
            zh_list = by_id.get(item["id"])
            if not zh_list:
                retry.append((item, ["G1"], {"why": "模型未返回该 id"}))
                continue
            restored, failed, notes = gate(item, zh_list[0],
                                           _locked(item))
            if failed:
                retry.append((item, failed, notes))
                continue
            results.append(self._finish(item, restored, [], {}, batch["bucket"],
                                        response=response, soft_pairs=soft_pairs,
                                        raw_zh=zh_list[0]))

        # 门禁不过 / 缺失 → 单条重试一次；单条到底仍失败 → 标 failed，不阻塞其他条
        for item, last_gates, last_notes in retry:
            if depth >= 2 or len(pending) <= 2:
                zh_raw = (by_id.get(item["id"]) or [""])[0]
                restored, failed, notes = gate(item, zh_raw,
                                               _locked(item))
                if not failed:
                    results.append(self._finish(item, restored, [], {}, batch["bucket"],
                                                response=response, soft_pairs=soft_pairs,
                                                raw_zh=zh_raw))
                else:
                    results.append(self._finish(item, restored or None, failed or last_gates,
                                                notes or last_notes, batch["bucket"],
                                                response=response, failed_item=True,
                                                soft_pairs=soft_pairs))
            else:
                self.stats["retried"] += 1
                results.extend(self._split(batch, [item], soft_pairs, depth + 1, "gate",
                                           last_gates=last_gates))
        return results

    def _split(self, batch, items, soft_pairs, depth, reason, last_gates=None):
        """拆半重试；单条到底仍失败 → 记 failed（保留最后一次真实门禁名，不一律标 G1）。"""
        if len(items) == 1:
            item = items[0]
            labels = list(last_gates or ["G1"])
            return [self._finish(item, None, labels,
                                 {"why": "重试后仍失败（{}）".format(reason)},
                                 batch["bucket"], failed_item=True, soft_pairs=soft_pairs)]
        self.stats["splits"] += 1
        middle = len(items) // 2
        out = []
        for part in (items[:middle], items[middle:]):
            if not part:
                continue
            sub = {"mod": batch["mod"], "bucket": batch["bucket"], "items": part}
            out.extend(self.process(sub, depth + 1))
        return out

    def _finish(self, item, restored, failed, notes, bucket, response=None,
                from_cache=False, failed_item=False, soft_pairs=(), official=False,
                raw_zh=None):
        """落一条结果。status ∈ ok / official（官方译文直用）/ keep / failed。

        写库字段严格对齐 Core DictPack：h = FNV1a64(normalize(source))，n/s/t/mod。
        keep 是合法产物（00 §4.1）：写成 t == s，运行时 DictEntry.IsKeep 命中后按 miss 下沉
        终止，从而挡住 Global 层不适合本站点的通用译法；failed 不写库，由报告复核。
        """
        row = item["row"]
        source = row["text"]
        normalized = normalize(source)
        entry = None
        drift = None
        failed = list(failed or [])
        if not failed:
            if restored is not None and restored.strip() == source.strip():
                status = "keep"
                self.stats["items_keep"] += 1
                if len(self.stats["keep_examples"]) < 5:
                    self.stats["keep_examples"].append({
                        "id": item["id"], "mod": row["mod"],
                        "s": source[:120], "t": source[:120],
                        "why": "遮蔽后只剩不可翻译物" if item.get("keep_auto") else "模型判定无需翻译"})
                # t 必须与 s 逐字节相同，否则 DictEntry.IsKeep（Ordinal 比较）不成立
                entry = {
                    "h": hash_text(normalized),
                    "n": normalized,
                    "s": source,
                    "t": source,
                    "mod": row["mod"],
                }
            elif restored:
                if official:
                    status = "rescue" if item.get("curated_from") == "keyref" else "official"
                else:
                    status = "ok"
                if status == "rescue":
                    self.stats["items_rescue"] += 1
                elif status == "official":
                    self.stats["items_official"] += 1
                else:
                    self.stats["items_ok"] += 1
                entry = {
                    "h": hash_text(normalized),
                    "n": normalized,
                    "s": source,
                    "t": restored,
                    "mod": row["mod"],
                }
                if _residual_english(restored) >= 3:
                    self.stats["mixed_lang"] += 1
                    by_mod = self.stats["mixed_lang_by_mod"]
                    by_mod[row["mod"]] = by_mod.get(row["mod"], 0) + 1
                    if len(self.stats["mixed_lang_examples"]) < 20:
                        self.stats["mixed_lang_examples"].append({
                            "id": item["id"], "mod": row["mod"],
                            "src": source[:120], "zh": restored[:160],
                            "words": _residual_english_words(restored)[:8],
                        })
                if not official:
                    drifts = drifts_of(item, restored, soft_pairs)
                    for one in drifts:
                        label = "{} -> {}".format(one["term"], one["want"])
                        bucket = self.stats["drift_counts"].get(label)
                        if bucket is None:
                            bucket = {"term": one["term"], "want": one["want"],
                                      "count": 0, "zh_samples": []}
                            self.stats["drift_counts"][label] = bucket
                        bucket["count"] += 1
                        if len(bucket["zh_samples"]) < 3:
                            bucket["zh_samples"].append(one["zh"])
                        if len(self.stats["drift_examples"]) < 20:
                            self.stats["drift_examples"].append(one)
                    if drifts:
                        self.stats["term_drift"] += len(drifts)
            else:
                status = "failed"
                failed = ["G8"]
                self.stats["items_failed"] += 1
                for name in failed:
                    self._note_gate(name, item, "", "空译文")
        else:
            status = "failed"
            self.stats["items_failed"] += 1
            for name in failed:
                if name in self.stats["gates"]:
                    self._note_gate(name, item, restored, notes.get(name, ""))
            if len(self.stats["failed"]) < 300:
                self.stats["failed"].append({
                    "id": item["id"], "mod": row["mod"], "gates": failed,
                    "src": source[:200], "zh": (restored or "")[:200],
                    "notes": {key: value for key, value in notes.items() if isinstance(value, str)},
                })
        if not from_cache and not official and response is not None and status in ("ok", "keep"):
            key = cache_key(source, self.glossary.version, PROMPT_VERSION, self.config.model)
            # 存"哨兵态"译文：命中时还要再走一次 restore + 门禁，存还原态会被二次还原吃掉哨兵
            self.cache.put(key, source, raw_zh if raw_zh is not None else (restored or ""),
                           status, self.config.model)
        return {
            "id": item["id"],
            "mod": row["mod"],
            "bucket": bucket,
            "status": status,
            "gates": failed,
            "entry": entry,
            "source": source,
            "restored": restored,
            "drift": drift,
        }

    # -- 遮蔽后不可译（04 §5.5）-------------------------------------------
    def keep_results(self, items):
        """遮蔽后只剩不可翻译物的条目：直接标 keep（t == s），零 token、不占批次。"""
        out = []
        for item in items or ():
            out.append(self._finish(item, item["row"]["text"], [], {},
                                    bucket_of(item["row"])))
        return out

    def _finalize_counters(self):
        """把内部集合转成可 JSON 序列的统计（含 lead 要的 top 排名）。可重复调用。"""
        stats = self.stats
        if isinstance(stats.get("terms_distinct"), set):
            stats["terms_distinct"] = len(stats["terms_distinct"])
        if isinstance(stats.get("items_locked"), set):
            stats["items_locked"] = len(stats["items_locked"])
        if isinstance(stats.get("locked_triggered"), set):
            names = sorted(stats["locked_triggered"])
            stats["locked_triggered"] = len(names)
            stats["locked_triggered_names"] = names[:40]
        buckets = stats.get("drift_counts") or {}
        top_drift = sorted(buckets.items(), key=lambda kv: -kv[1]["count"])[:10]
        stats["drift_top"] = [dict(value, label=label) for label, value in top_drift]
        by_mod = stats.get("mixed_lang_by_mod") or {}
        stats["mixed_lang_top_mods"] = [
            {"mod": mod, "count": count}
            for mod, count in sorted(by_mod.items(), key=lambda kv: -kv[1])[:5]]

    # -- 全量 -------------------------------------------------------------
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
                    self.log("  !! 批异常（{} 条）：\n{}".format(len(batch["items"]), traceback.format_exc()))
                    self.stats["batches_failed"] += 1
                    for item in batch["items"]:
                        results.append(self._finish(item, None, ["G1"], {"why": "批异常"},
                                                    batch["bucket"], failed_item=True))
                if done % 5 == 0 or done == len(batches):
                    used = self.client.tokens
                    self.log("  [{}/{}] 批  ok={} failed={} tokens={}/{}".format(
                        done, len(batches), self.stats["items_ok"], self.stats["items_failed"],
                        used["prompt"], used["completion"]))
        self._finalize_counters()
        self.stats["elapsed_s"] = round(time.time() - started, 1)
        self.stats["tokens"] = dict(self.client.tokens)
        calls = self.stats["calls"] or 0
        self.stats["latency_avg_s"] = round(self.stats["latency_total"] / calls, 2) if calls else 0.0
        self.stats["latency_max_s"] = max(self.stats["latencies"]) if self.stats["latencies"] else 0.0
        return results


# ---------------------------------------------------------------------------
# 写库
# ---------------------------------------------------------------------------

_SAFE_NAME_RE = re.compile(r"[^A-Za-z0-9._\-]+")


def safe_mod_name(mod: str) -> str:
    name = _SAFE_NAME_RE.sub("_", mod or "unknown").strip("._-")
    return name or "unknown"


def write_packs(results, out_dir=None, dry_run=False):
    """按 mod 分文件写 packs/zh-cn/<modId>.jsonl。keep/failed 不写（运行时按 miss 处理）。"""
    out_dir = Path(out_dir or OUT_DIR)
    files = {}
    for result in results:
        entry = result.get("entry")
        if not entry:
            continue
        files.setdefault(safe_mod_name(result["mod"]), []).append(entry)

    if dry_run:
        return {"files": {mod: len(rows) for mod, rows in sorted(files.items())},
                "total": sum(len(rows) for rows in files.values()), "written": False}

    out_dir.mkdir(parents=True, exist_ok=True)
    total = 0
    for mod, rows in sorted(files.items()):
        rows.sort(key=lambda row: row["h"])
        path = out_dir / "{}.jsonl".format(mod)
        with path.open("w", encoding="utf-8", newline="\n") as handle:
            for row in rows:
                handle.write(json.dumps(row, ensure_ascii=False) + "\n")
                total += 1
    return {"files": {mod: len(rows) for mod, rows in sorted(files.items())},
            "total": total, "written": True}


# ---------------------------------------------------------------------------
# 自测（不联网）
# ---------------------------------------------------------------------------

def selftest(pool_rows=None):
    """全池遮蔽往返 + 门禁正负例 + 输出字段契约。返回 (failures, checks)。"""
    failures = []
    checks = 0
    rows = pool_rows if pool_rows is not None else load_pool()

    for row in rows:
        checks += 1
        res = masklib.mask(row["text"])
        back = masklib.restore(res.text, res)
        if back != row["text"]:
            failures.append(("往返", row["id"], row["text"][:80], back[:80]))

    glossary = Glossary([("Apoapsis", "远地点"), ("Gimbal", "万向节")])
    probes = [
        ({'id': 1, 'mod': 'X', 'text': 'Apoapsis: <<1>> m'}, "Apoapsis: <<1>> m", {"G7", "G8"}),
        ({'id': 2, 'mod': 'X', 'text': 'Apoapsis: <<1>> m'}, "远地点: <<1>> m", set()),
        ({'id': 3, 'mod': 'X', 'text': 'Apoapsis: <<1>> m'}, "远地点: <<1>> <<2>> m", {"G2"}),
        ({'id': 4, 'mod': 'X', 'text': 'Cannot activate %s now'}, "无法在此时激活", {"G3"}),
        ({'id': 5, 'mod': 'X', 'text': '<b>Gimbal</b> free'}, "<b>万向节</b> 自由", set()),
        ({'id': 6, 'mod': 'X', 'text': 'Gimbal free'}, "Gimbal free", {"G7", "G8"}),
        ({'id': 7, 'mod': 'X', 'text': 'Open GameData/RealFuels/x.cfg now'},
         "立即打开 GameData/RealFuels/x.cfg", {"G5"}),
        ({'id': 8, 'mod': 'X', 'text': 'Open GameData/RealFuels/x.cfg now'},
         "立即打开 <<M0>>", set()),
        # 真换行在 JSONL 语料里不可能出现（_ESCAPE_RE 只吃字面反斜杠序列），
        # 所以这里用字面 \n 才测得到"模型漏抄哨兵 → 静默丢内容"这条
        ({'id': 9, 'mod': 'X', 'text': 'Line one ' + chr(92) + 'nLine two'},
         "第一行", {"G5"}),
        ({'id': 10, 'mod': 'X', 'text': 'Throttle up'}, "节流阀上调", set()),
        # G7 别名：Oxidizer-Rich 的标准译法"富氧"必须放行（实测假失败案例）
        ({'id': 11, 'mod': 'X', 'text': '2014-2018 Oxidizer-Rich Staged Combustion Engines'},
         "2014-2018 年富氧分级燃烧发动机", set()),
        # Delta-V 的大小写变体 ΔV / Δv 都不能算漂移
        ({'id': 12, 'mod': 'X', 'text': 'Total Delta-V required'}, "所需总 ΔV", set()),
    ]
    for raw, zh, expect in probes:
        res = masklib.mask(raw["text"])
        item = {"id": raw["id"], "row": raw, "res": res, "masked": res.text, "role": "label"}
        restored, failed, notes = gate(item, zh, locked_for(raw["text"], glossary))
        checks += 1
        if set(failed) != expect:
            failures.append(("门禁", raw["id"], zh, sorted(failed), sorted(expect), notes))

    # 04 §5.5：遮蔽后只剩不可翻译物 → 直接 keep（不送翻），不能白烧调用
    for probe, expect in (
        ("dblTest:", True),
        ("maxCompletions", True),
        ("RB.162 Turbojet", False),
        ("Throttle up", False),
        ("Solar panels TLn", False),
    ):
        checks += 1
        got = _only_untouchable(masklib.mask(probe).text)
        if got != expect:
            failures.append(("keep_auto", probe, got, expect, masklib.mask(probe).text))

    # 术语分层：短语级才锁 G7，单词/泛义词只做软参考（否则 Arm→启用 会被误判漂移）
    checks += 1
    if not _is_locked_term("Reaction Wheel") or _is_locked_term("Arm") or _is_locked_term("R"):
        failures.append(("术语分层", _is_locked_term("Reaction Wheel"),
                         _is_locked_term("Arm"), _is_locked_term("R")))
    checks += 1
    if locked_for("Arm the parachute", glossary) or \
            ("Reaction Wheel", "动量轮") not in locked_for("Reaction wheel torque", glossary):
        failures.append(("G7 集合", locked_for("Arm the parachute", glossary),
                         locked_for("Reaction wheel torque", glossary)))

    # 官方译文直用：整句命中 exact、键引用命中 keyref 且需显式开关
    tm = Glossary([("Close", "关闭")], keyref={"#autoLOC_501653": "光年轮胎公司"},
                  exact={normalize("Close"): "关闭"})
    checks += 1
    if _curated_zh({"text": "Close"}, tm) != "关闭" or \
            _curated_zh({"text": "#autoLOC_501653"}, tm, True) != "光年轮胎公司" or \
            _curated_zh({"text": "#autoLOC_501653"}, tm, False) is not None:
        failures.append(("官方直用", _curated_zh({"text": "Close"}, tm),
                         _curated_zh({"text": "#autoLOC_501653"}, tm, True),
                         _curated_zh({"text": "#autoLOC_501653"}, tm, False)))

    checks += 1
    first = cache_key("Apoapsis", "abc", PROMPT_VERSION, "m")
    if first != cache_key("Apoapsis", "abc", PROMPT_VERSION, "m") or first.count("|") != 3:
        failures.append(("缓存键", first))

    checks += 1
    entry = {"h": hash_text(normalize("Throttle up")), "n": normalize("Throttle up"),
             "s": "Throttle up", "t": "节流阀上调", "mod": "X"}
    if not re.fullmatch(r"[0-9a-f]{16}", entry["h"]) or not entry["t"]:
        failures.append(("输出字段", entry))

    checks += 1
    if set(normalize("Apoapsis  <<1>>\r\n\tm").split()) != set("Apoapsis <<1>> m".split()):
        failures.append(("归一化", normalize("Apoapsis  <<1>>\r\n\tm")))

    return failures, checks


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _write_sample(results, target, runner):
    """逐条审计物：id/mod/bucket/status/gates/源/译/命中的术语。默认 reports/sample-<ts>.jsonl。"""
    path = Path(target) if target else (REPORTS_DIR / "sample-{}.jsonl".format(
        time.strftime("%Y%m%d-%H%M%S")))
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as handle:
        for result in results:
            row = {
                "id": result["id"], "mod": result["mod"], "bucket": result["bucket"],
                "status": result["status"], "gates": result["gates"],
                "src": result["source"], "zh": result["entry"]["t"] if result["entry"] else result["restored"],
                "keep": bool(result["entry"]) and result["entry"]["t"] == result["entry"]["s"],
                "terms": [en for en, _zh in locked_for(result["source"], runner.glossary)],
                "drift": result.get("drift"),
            }
            handle.write(json.dumps(row, ensure_ascii=False) + "\n")
    return path


class _OfflineConfig:
    """离线模式的假配置：不读 .env，不持有密钥。"""

    base = "(offline)"
    model = "offline-stub"
    reasoning = "none"

    def describe(self):
        return "离线模式"


def _sample(items, limit):
    """限流时均匀取样，保证跨 mod 覆盖（不是只取前 N 条同一 mod）。"""
    if limit is None or limit >= len(items):
        return items
    step = max(1, len(items) // limit)
    return items[::step][:limit]


def main(argv=None):
    ensure_stdout()
    parser = argparse.ArgumentParser(prog="i18n_tools.translate", description="KSP 英译中流水线（P4–P6）")
    parser.add_argument("--pool", default=None, help="pool.jsonl 路径")
    parser.add_argument("--glossary", default=None, help="glossary.jsonl 路径")
    parser.add_argument("--out", default=None, help="输出目录（默认 packs/zh-cn）")
    parser.add_argument("--env-file", default=None, help=".env.local 路径")
    parser.add_argument("--limit", type=int, default=None, help="只译前 N 条（均匀取样）")
    parser.add_argument("--jobs", type=int, default=4, help="并发批数（默认 4）")
    parser.add_argument("--max-chars", type=int, default=DEFAULT_MAX_CHARS, help="单批字符预算")
    parser.add_argument("--dry-run", action="store_true", help="真调 API 但不写 packs/zh-cn")
    parser.add_argument("--plan", action="store_true", help="只统计分批，不联网")
    parser.add_argument("--selftest", action="store_true", help="全池遮蔽往返 + 门禁自测，不联网")
    parser.add_argument("--no-cache", action="store_true", help="不读也不写缓存")
    parser.add_argument("--keyref-rescue", action="store_true",
                        help="用 glossary 的官方中文救回 #autoLOC_* 键引用（零 token）")
    parser.add_argument("--offline", action="store_true",
                        help="桩响应，不发任何请求（验证掩码/分批/解析/门禁/缓存链路）")
    parser.add_argument("--only-bucket", default=None, choices=["short", "mid", "long", "rt", "ph"],
                        help="只跑某个桶（用于定向抽检，如 ph 占位符桶）")
    parser.add_argument("--mods", default=None,
                        help="只跑指定 mod（逗号分隔，单 mod 试点用，如 --mods RP-1）")
    parser.add_argument("--sample-out", default=None,
                        help="把本次逐条结果写成 jsonl（审计用，默认 reports/sample-<ts>.jsonl）")
    args = parser.parse_args(argv)

    pool_rows = load_pool(args.pool)
    glossary = load_glossary(args.glossary)
    pool_total = len(pool_rows)

    if args.only_bucket:
        pool_rows = [row for row in pool_rows
                     if bucket_of(row) == args.only_bucket and classify(row)[0] == "translate"]
        print("[translate] --only-bucket {}：{} 行".format(args.only_bucket, len(pool_rows)))

    if args.mods:
        wanted = [part.strip() for part in args.mods.split(",") if part.strip()]
        missing = [mod for mod in wanted
                   if not any(row["mod"] == mod for row in pool_rows)]
        pool_rows = [row for row in pool_rows if row["mod"] in wanted]
        print("[translate] --mods {}：{} 行{}".format(
            ",".join(wanted), len(pool_rows),
            "（pool 里没找到：{}）".format(",".join(missing)) if missing else ""))

    if args.selftest:
        failures, checks = selftest(pool_rows)
        print("[selftest] {} 项检查，{} 项失败".format(checks, len(failures)))
        for failure in failures[:20]:
            print("  [FAIL]", failure)
        return 1 if failures else 0

    started = time.time()
    batches, items, build_stats = build_batches(pool_rows, max_chars=args.max_chars,
                                                glossary=glossary,
                                                keyref_rescue=args.keyref_rescue)
    sample_stats = None

    if args.limit:
        # 先在全池上取样，再用这些行重新分批：否则每批只剩 1–2 条，
        # 系统提示（约 1.5k tokens）会被重复付 200 次，测出来的单位成本全错。
        keep_ids = {item["id"] for item in _sample(items, args.limit)}
        subset = [row for row in pool_rows if row["id"] in keep_ids]
        batches, items, sample_stats = build_batches(subset, max_chars=args.max_chars,
                                                     glossary=glossary,
                                                     keyref_rescue=args.keyref_rescue,
                                                     group_by_mod=False)

    total_items = sum(len(batch["items"]) for batch in batches)
    keep_items = [item for item in items if item.get("keep_auto")]

    if args.plan:
        per_bucket = {}
        per_mod = {}
        for batch in batches:
            per_bucket[batch["bucket"]] = per_bucket.get(batch["bucket"], 0) + len(batch["items"])
            per_mod[batch["mod"]] = per_mod.get(batch["mod"], 0) + len(batch["items"])
        print("[plan] pool {} 行 → 待译 {} 条 / 跳过 {} 条 / {} 批".format(
            len(pool_rows), total_items, sum(build_stats["skipped"].values()), len(batches)))
        print("[plan] 跳过原因：", json.dumps(build_stats["skipped"], ensure_ascii=False, sort_keys=True))
        print("[plan] 桶分布：", json.dumps(per_bucket, ensure_ascii=False, sort_keys=True))
        print("[plan] mod 数 {}，术语表：{}".format(len(per_mod), glossary.note))
        curated = build_stats["curated"]
        print("[plan] 官方译文直用 {} 条（其中键引用救回 {}），门禁不过退回翻译 {} 条".format(
            curated["used"], curated["keyref_rescue"], curated["rejected"]))
        if build_stats.get("keeps_auto"):
            print("[plan] 遮蔽后只剩不可译物 → 直接标 keep {} 条（零 token）：{}".format(
                build_stats["keeps_auto"],
                [item["row"]["text"][:40] for item in keep_items[:3]]))
        if not args.keyref_rescue:
            rescue = [row["text"].strip() for row in pool_rows
                      if row["text"].lstrip().startswith("#")
                      and row["text"].strip() in glossary.keyref]
            print("[plan] 可救回键引用 {} 条（加 --keyref-rescue 启用）：{}".format(
                len(rescue), [(key, glossary.keyref[key]) for key in rescue[:3]]))
        print("[plan] 批样例：", [(b["mod"], b["bucket"], len(b["items"])) for b in batches[:6]])
        return 0

    print("[translate] pool={} 待译={} 跳过={} 批={} jobs={} dry_run={}".format(
        len(pool_rows), total_items, sum(build_stats["skipped"].values()),
        len(batches), args.jobs, args.dry_run))
    print("[translate] 跳过原因：", json.dumps(build_stats["skipped"], ensure_ascii=False, sort_keys=True))
    if sample_stats is not None:
        print("[translate] 取样 {} 行 → 可译 {} 条 / 跳过 {} 条".format(
            len(subset), total_items, sum(sample_stats["skipped"].values())))
    print("[translate] 术语表：{}（版本 {}）".format(glossary.note, glossary.version))

    if args.offline:
        config = _OfflineConfig()
        print("[translate] 离线模式（桩响应，不发请求）")
    else:
        try:
            config = load_config(args.env_file)
        except LlmConfigError as exc:
            print("[translate] 配置错误：{}".format(exc))
            return 2
        print("[translate] 端点 {}，model={}，reasoning={}".format(
            config.base, config.model, config.reasoning))

    cache = Cache(enabled=not args.no_cache)
    runner = Runner(config, glossary, cache, jobs=args.jobs, max_chars=args.max_chars,
                    dry_run=args.dry_run, offline=args.offline)
    try:
        results = runner.keep_results(keep_items) + runner.run(batches)
    finally:
        cache.close()

    written = write_packs(results, args.out, dry_run=args.dry_run)
    payload = {
        "kind": "translation",
        "started": time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(started)),
        "dry_run": bool(args.dry_run),
        "offline": bool(args.offline),
        "limit": args.limit,
        "model": config.model,
        "promptV": PROMPT_VERSION,
        "glossaryV": glossary.version,
        "glossary": glossary.note,
        "pool_rows": len(pool_rows),
        "build": build_stats,
        "sample_build": sample_stats,
        "batches": len(batches),
        "jobs": args.jobs,
        "max_chars": args.max_chars,
        "stats": runner.stats,
        "curated": build_stats["curated"],
        "keyref_rescue": bool(args.keyref_rescue),
        "mods": args.mods,
        "only_bucket": args.only_bucket,
        "glossary_detail": {"total": glossary.total, "terms": len(glossary.pairs),
                            "locked": len(glossary.locked), "exact": len(glossary.exact),
                            "keyref": len(glossary.keyref), "bad": glossary.bad},
        "written": written,
        "elapsed_s": round(time.time() - started, 1),
        "by_mod": _by_mod(results),
    }
    if args.sample_out or args.limit or args.only_bucket:
        sample_path = _write_sample(results, args.sample_out, runner)
        payload["sample_out"] = str(sample_path)

    path = write_report(payload, REPORTS_DIR)
    print_summary(payload)
    print("[translate] 报告 → {}".format(path))
    return 0


_ASCII_WORD_RE = re.compile(r"[A-Za-z][A-Za-z'\-]{1,}")
_ASCII_OK = {
    "eva", "iva", "ksc", "vab", "sph", "rcs", "sas", "twr", "rtg", "apu", "ec", "kpa",
    "dps", "isp", "tWR", "dx", "science", "kerbal", "kerbin", "mun", "duna", "eve", "jool",
    "moho", "ike", "gilly", "laythe", "vall", "tylo", "bop", "pol", "eeloo", "dres",
}


def _residual_english_words(text):
    """译文里残留的英文单词（含小写字母、非已知缩写/单位）。"""
    words = []
    for token in _ASCII_WORD_RE.findall(text or ""):
        if token.upper() == token and len(token) <= 5:      # EVA / KSC / RCS / TWR
            continue
        if token.lower() in _ASCII_OK:
            continue
        if token.lower() in {word.lower() for word in _ASCII_OK}:
            continue
        words.append(token)
    return words


def _residual_english(text):
    return len(_residual_english_words(text))


def _by_mod(results):
    table = {}
    for result in results:
        row = table.setdefault(result["mod"], {"ok": 0, "keep": 0, "failed": 0, "chars": 0})
        row[result["status"]] = row.get(result["status"], 0) + 1
        if result["entry"]:
            row["chars"] += len(result["entry"]["t"])
    return table


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
