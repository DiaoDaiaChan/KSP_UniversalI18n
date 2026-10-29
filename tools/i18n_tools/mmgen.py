"""mmgen —— 原生语言节点生成器（里程碑 M1-a）

名字与产物
----------
里程碑写的是「MM patch 生成」（docs/00 §11），实现刻意改成纯 KSP 语言节点 cfg，
不用 ModuleManager 的 @ 语法。理由三条（docs/11 §4 M1-a）：

  1. 生态里自带 Localization/*.cfg 的 mod 不装 MM 也能生效 —— 说明 KSP 会把同一批
     Localization 节点合并，我们不需要 MM 来注入。
  2. netkan 现在只依赖 Harmony2（v1.7 Fix-4）。用 @ 语法等于把 ModuleManager 反向变成
     强制依赖，而本产品的卖点正是不改原 mod、不要求装别的东西。
  3. 纯 cfg 是静态数据：出错整文件删掉就回滚干净；@ patch 是改别人的节点，回滚不干净。

产物：build/native/<lang>/<Mod>.cfg（生成物，不入库；.gitignore 已含 build/），
发布时由 build-release.ps1 拷进包里的 Packs/<lang>/Native/。运行期不需要任何新代码：
KSP 自己在我们的 Harmony 钩子之前就把键解析成中文了。

数据从哪来
----------
  * packs/pool.jsonl 里 src=loc 的行 = 游戏内抽到的本地化键（key）+ 英文原文（text）+ 所属 mod；
  * packs/zh-cn/*.jsonl = 已有的中文记忆，两种形式：
      - s 是键引用（#LOC_x）的行 → 按 key 直接配对，优先级最高；
      - s 是英文原文的行 → 按 h 配对（pool 的 h 就是 normalize(text) 的 FNV-1a）。
两者连接得到 key → 中文，发射成一个 mod 一个文件；没有条目就不写文件。
同一 key 出现两种不同中文时整条丢弃（歧义守卫：宁可让原生通道漏一条，
也不能把两条不同的中文绑到同一个键上）。

用法
----
    python -m i18n_tools.mmgen                      # 生成到 build/native/zh-cn
    python -m i18n_tools.mmgen --stats              # 只统计覆盖率，不写文件
    python -m i18n_tools.mmgen --check              # 校验磁盘产物是否最新（CI 用）
    python -m i18n_tools.mmgen --report reports/mmgen-coverage.md
    python -m i18n_tools.mmgen --selftest

幂等：每次按 key 全集重算、按 key 排序，跑两遍字节完全相同（selftest 里钉着）。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import tempfile
from collections import Counter, OrderedDict

from . import cfgparse, ensure_stdout
from .fnv import hash_text
from .textnorm import normalize

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PACKS_ROOT = os.path.join(REPO, "packs")
POOL_PATH = os.path.join(PACKS_ROOT, "pool.jsonl")
BUILD_ROOT = os.path.join(REPO, "build", "native")
DEFAULT_LANG = "zh-cn"

LOCALIZATION_NODE = "Localization"

# 本地化键：与 textnorm.is_key_like / resolve_keyrefs._KEY_REF_RE 同形。
# 键里出现空格或斜杠说明是半解析串，那种不是键，不能当原生节点发出去。
_KEY_RE = re.compile(r"^#[A-Za-z][A-Za-z0-9_.\-]*$")

# 占位符三写法，与 textnorm 同口径（KSP 的 <<1>>、string.Format 的 {0}、printf 的 %s）。
_PLACEHOLDER_RE = re.compile(r"<<\d+>>|\{\d+\}|%[sdif]")

# cfg 注释风险：值里出现行首或空白后的双斜线会被 cfgparse.strip_comment（以及 KSP 自己）
# 当注释吃掉。中文里确实会写「站点 // 区域」，必须拦。
_COMMENT_RISK_RE = re.compile(r"(?:^|\s)//")

# 文件名里不允许的字符（Windows 最严，生成物要能被打包脚本按名字取）。
_BAD_FILENAME_RE = re.compile(r"[<>:\"/\\|?*\x00-\x1f]")

AMBIGUOUS_KEY_REASON = "ambiguous_key"
UNATTRIBUTED_FILE = "_unattributed"

BT = chr(96)
FENCE = BT + BT + BT


def _code(text):
    """markdown 行内代码（用 chr(96) 拼，源码里就不出现反引号）。"""
    return BT + text + BT


def is_loc_key(value) -> bool:
    """整串就是一个本地化键引用。"""
    return isinstance(value, str) and bool(_KEY_RE.match(value))


def mod_file_name(mod) -> str:
    """mod 名 → 产物文件名（永不为空，永不非法）。"""
    name = mod if isinstance(mod, str) else ""
    name = name.strip()
    if not name:
        return UNATTRIBUTED_FILE
    name = _BAD_FILENAME_RE.sub("_", name)
    name = re.sub(r"\s+", "_", name).strip(" .")
    if not name or name in (".", ".."):
        return UNATTRIBUTED_FILE
    return name[:100]


def value_problem(key, en, zh):
    """一条 key 到中文的映射能不能安全写进 cfg？返回原因字符串，None 表示可以用。

    每一条都是「写进去会让游戏读到别的东西」或「写进去也没有用」：
      empty          中文是空的
      same_as_source 中文与英文一模一样（等于没翻，白占一条）
      newline        值里有换行（cfg 的值只到行尾，后半截会变成垃圾键）
      brace          值里有花括号（节点结构会被截断）
      comment        值里有空白加双斜线（会被当注释吃掉）
      key_ref        值本身像个键引用（KSP 会把 # 开头再解析一次）
      placeholder    占位符多重集与英文不一致（运行时会少参数）
    """
    if not isinstance(zh, str):
        return "empty"
    text = zh.strip()
    if not text:
        return "empty"
    en_text = en.strip() if isinstance(en, str) else ""
    if en_text and text == en_text:
        return "same_as_source"
    if "\n" in text or "\r" in text:
        return "newline"
    if "{" in text or "}" in text:
        return "brace"
    if _COMMENT_RISK_RE.search(text):
        return "comment"
    if text.startswith("#"):
        return "key_ref"
    if Counter(_PLACEHOLDER_RE.findall(en_text)) != Counter(_PLACEHOLDER_RE.findall(text)):
        return "placeholder"
    return None


class Translations:
    """中文记忆的两条索引：按 key、按 hash。"""

    __slots__ = ("by_key", "by_hash")

    def __init__(self, by_key=None, by_hash=None):
        self.by_key = by_key if by_key is not None else {}
        self.by_hash = by_hash if by_hash is not None else {}

    def lookup(self, key, row_hash=None, text=None):
        if key is not None:
            hit = self.by_key.get(key)
            if hit:
                return hit
        if isinstance(row_hash, str) and row_hash:
            hit = self.by_hash.get(row_hash.lower())
            if hit:
                return hit
        if isinstance(text, str) and text:
            return self.by_hash.get(hash_text(normalize(text)))
        return None


def iter_jsonl(path):
    """逐行读 JSONL，坏行跳过（坏行已由 packindex 记账，这里不重复报）。"""
    try:
        handle = open(path, "r", encoding="utf-8-sig", newline="")
    except OSError:
        return
    with handle:
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


def load_pool(path=POOL_PATH):
    """读候选池（只读，不改）。"""
    return list(iter_jsonl(path))


def load_translations(packs_dir):
    """把语言包目录里的中文记忆装进两条索引。

    s 是键引用（#LOC_x）→ 按 key 索引：这是最硬的配对，说明我们已经知道这个键的中文。
    s 是英文原文 → 按 h 索引：h 缺了就自己算一遍 normalize(s) 的哈希（与 Core 同算法）。
    """
    by_key = {}
    by_hash = {}
    if not os.path.isdir(packs_dir):
        return Translations(by_key, by_hash)
    for name in sorted(os.listdir(packs_dir)):
        if not name.lower().endswith(".jsonl"):
            continue
        for row in iter_jsonl(os.path.join(packs_dir, name)):
            zh = row.get("t")
            if not isinstance(zh, str) or not zh.strip():
                continue
            src = row.get("s")
            if is_loc_key(src):
                by_key.setdefault(src, zh)
                continue
            digest = row.get("h")
            if not isinstance(digest, str) or not digest.strip():
                if isinstance(src, str) and src.strip():
                    digest = hash_text(normalize(src))
                else:
                    continue
            by_hash.setdefault(digest.lower(), zh)
    return Translations(by_key, by_hash)


def is_location_row(row) -> bool:
    """池里这一行是不是本地化键行（cfg 文本行不参与原生节点生成）。"""
    return row.get("src") == "loc" and is_loc_key(row.get("key"))


def build_rows(pool_rows, translations):
    """池加中文记忆 → (patches, unresolved, skip, ambiguous)。

    patches: OrderedDict mod → [(key, 中文)]（按 key 排序，保证幂等）
    统计计数用来解释「池里 2,948 条，为什么只发出 2,785 条」。
    """
    unresolved = []
    skip = Counter()
    first_zh = {}
    ambiguous = set()
    keys_by_mod = OrderedDict()

    for row in pool_rows:
        if not isinstance(row, dict):
            continue
        if row.get("src") != "loc":
            skip["cfg_row"] += 1
            continue
        key = row.get("key")
        if not is_loc_key(key):
            # src 标了 loc 但 key 不是键形态（半解析串），既不能发也不能算未解析
            skip["bad_key"] += 1
            continue
        text = row.get("text")
        mod = mod_file_name(row.get("mod"))
        zh = translations.lookup(key, row.get("h"), text)
        if zh is None:
            unresolved.append((mod, key, text if isinstance(text, str) else ""))
            continue
        reason = value_problem(key, text, zh)
        if reason is not None:
            skip[reason] += 1
            continue
        stripped = zh.strip()
        previous = first_zh.get(key)
        if previous is None:
            first_zh[key] = stripped
        elif previous != stripped:
            ambiguous.add(key)
            continue
        keys_by_mod.setdefault(mod, OrderedDict())[key] = stripped

    for key in ambiguous:
        for keyed in keys_by_mod.values():
            if key in keyed:
                del keyed[key]
                break
    skip[AMBIGUOUS_KEY_REASON] += len(ambiguous)

    patches = OrderedDict()
    for mod in sorted(keys_by_mod):
        entries = [(key, keys_by_mod[mod][key]) for key in sorted(keys_by_mod[mod])]
        if entries:
            patches[mod] = entries
    return patches, unresolved, skip, ambiguous


def render_cfg(mod, entries, lang, source_note="packs/pool.jsonl + packs/zh-cn/*.jsonl"):
    """一个 mod 的 cfg 文本。entries 是 (key, 中文) 列表且已排序。"""
    one_line_mod = (mod or "").replace(chr(10), " ").replace(chr(13), " ")
    lines = [
        "// UI18n · 原生语言节点（KSP Localization）—— 生成物，别手改",
        "// 生成: python -m i18n_tools.mmgen    数据: " + source_note,
        "// mod: " + one_line_mod + "    条目: " + str(len(entries)),
        LOCALIZATION_NODE,
        "{",
        chr(9) + lang,
        chr(9) + "{",
    ]
    for key, zh in entries:
        lines.append(chr(9) + chr(9) + key + " = " + zh)
    lines.append(chr(9) + "}")
    lines.append("}")
    return chr(10).join(lines) + chr(10)


def read_back(text, lang):
    """把生成的 cfg 读回来，返回 {key: 值}。"""
    root = cfgparse.parse_text(text)
    node = root.child(LOCALIZATION_NODE)
    if node is None:
        return {}
    subtree = node.child(lang)
    if subtree is None:
        return {}
    entries = {}
    for key, value, _lineno in subtree.values:
        entries[key] = value
    return entries


def verify_cfg(text, lang, entries):
    """往返校验：写出去再读回来，逐条比对。返回 (ok, message)。"""
    got = read_back(text, lang)
    if len(got) != len(entries):
        return False, "read back {0} entries, wrote {1}".format(len(got), len(entries))
    for key, zh in entries:
        if got.get(key) != zh:
            return False, "key {0!r}: got {1!r} want {2!r}".format(key, got.get(key), zh)
    return True, ""


def build_files(pool_rows, translations, lang):
    """内存里算出全部产物：返回 (files, info)。files 是 OrderedDict 文件名 → 文本。"""
    patches, unresolved, skip, ambiguous = build_rows(pool_rows, translations)
    files = OrderedDict()
    for mod, entries in patches.items():
        text = render_cfg(mod, entries, lang)
        ok, message = verify_cfg(text, lang, entries)
        if not ok:
            raise ValueError("roundtrip failed for " + mod + ": " + message)
        files[mod_file_name(mod) + ".cfg"] = text
    info = {
        "lang": lang,
        "patches": patches,
        "unresolved": unresolved,
        "skip": skip,
        "ambiguous": ambiguous,
        "keys": sum(len(entries) for entries in patches.values()),
        "mods": len(patches),
    }
    return files, info


def write_files(outdir, files):
    """落盘（先建目录，逐个覆盖写）。返回 [(路径, 字节数)]。"""
    os.makedirs(outdir, exist_ok=True)
    written = []
    for name, text in files.items():
        path = os.path.join(outdir, name)
        with open(path, "w", encoding="utf-8", newline=chr(10)) as handle:
            handle.write(text)
        written.append((path, len(text.encode("utf-8"))))
    return written


def compare_with_disk(outdir, files):
    """--check 用：产物与磁盘现状的差异。返回 (missing, stale, extra)。"""
    missing = []
    stale = []
    extra = []
    existing = set()
    if os.path.isdir(outdir):
        existing = set(name for name in os.listdir(outdir) if name.lower().endswith(".cfg"))
    for name, text in files.items():
        path = os.path.join(outdir, name)
        if name not in existing:
            missing.append(name)
            continue
        try:
            with open(path, "r", encoding="utf-8-sig", newline="") as handle:
                current = handle.read()
        except OSError:
            missing.append(name)
            continue
        if current != text:
            stale.append(name)
    for name in sorted(existing - set(files)):
        extra.append(name)
    return missing, stale, extra


def coverage(pool_rows, patches):
    """覆盖率：每个 mod 池里有几个 loc key、发出了几个。"""
    totals = Counter()
    for row in pool_rows:
        if isinstance(row, dict) and is_location_row(row):
            totals[mod_file_name(row.get("mod"))] += 1
    got = dict((mod, len(entries)) for mod, entries in patches.items())
    per_mod = [(mod, totals[mod], got.get(mod, 0)) for mod in sorted(totals)]
    total = sum(totals.values())
    covered = sum(got.values())
    return {"total": total, "covered": covered,
            "ratio": (covered / float(total)) if total else 1.0,
            "per_mod": per_mod}


def resolved_ratio(cov, info):
    """能桥到中文的比例（发出 + 中英相同的都算桥到了）。

    为什么与覆盖率分开：「池里 187 条中文与英文一模一样」意味着那条我们已经有记忆、
    只是翻出来等于没翻。它们是已解析的，不是缺口，不该算进未解析里。
    """
    resolved = cov["covered"] + info["skip"].get("same_as_source", 0)
    total = cov["total"]
    return resolved, (resolved / float(total)) if total else 1.0


def render_report(path, info, cov, limit=40):
    """把覆盖率与缺口写成 markdown（reports/mmgen-coverage.md）。"""
    lines = []
    add = lines.append
    add("# 原生语言节点覆盖率（mmgen）")
    add("")
    add("生成物：" + _code("build/native/" + info["lang"] + "/*.cfg") + "，不入库，随整包发布到 Packs/<lang>/Native/。")
    add("")
    add("数据：" + _code("packs/pool.jsonl") + " 的 " + _code("src=loc") + " 行，与 "
        + _code("packs/zh-cn/*.jsonl") + " 按 key 或按 h 连接。")
    add("")
    add("## 1. 总览")
    add("")
    add("| 项 | 数量 |")
    add("| --- | --- |")
    add("| 池里的 loc 行 | {:,} |".format(cov["total"]))
    add("| 已发出（有中文且能安全写入 cfg） | {:,} |".format(cov["covered"]))
    add("| 覆盖率（发出 / 池） | {0:.1f}% |".format(cov["ratio"] * 100.0))
    resolved, ratio_resolved = resolved_ratio(cov, info)
    add("| 能桥到中文（发出 + 中英相同 {0:,}） | {1:,}（{2:.1f}%） |".format(
        info["skip"].get("same_as_source", 0), resolved, ratio_resolved * 100.0))
    add("| 产物文件数 | {:,} |".format(info["mods"]))
    add("| 未解析（桥不到中文） | {:,} |".format(len(info["unresolved"])))
    add("")
    add("## 2. 按 mod")
    add("")
    add("| mod | 池里 key | 已发出 | 覆盖率 |")
    add("| --- | --- | --- | --- |")
    for mod, total, got in cov["per_mod"]:
        share = (got / float(total) * 100.0) if total else 0.0
        add("| " + _code(mod) + " | {0:,} | {1:,} | {2:.1f}% |".format(total, got, share))
    add("")
    add("## 3. 跳过原因")
    add("")
    add("每一条都是「池里有这个键、也知道中文，但不能这么写」，判据见 " + _code("mmgen.value_problem") + "。")
    add("")
    add("| 原因 | 次数 |")
    add("| --- | --- |")
    for reason, count in sorted(info["skip"].items(), key=lambda kv: (-kv[1], kv[0])):
        add("| " + _code(reason) + " | {:,} |".format(count))
    add("")
    add("## 4. 未解析样例（最多 " + str(limit) + " 条）")
    add("")
    pending = info["unresolved"]
    if pending:
        add("| mod | key | 英文原文 |")
        add("| --- | --- | --- |")
        for mod, key, text in pending[:limit]:
            sample = (text or "").replace("|", "\\|")
            if len(sample) > 80:
                sample = sample[:77] + "..."
            add("| " + _code(mod) + " | " + _code(key) + " | " + sample + " |")
        add("")
        by_mod = Counter(mod for mod, _key, _text in pending)
        add("缺口最多的 mod：" + "、".join(
            _code(mod) + " {0:,}".format(count) for mod, count in by_mod.most_common(10)) + "。")
    else:
        add("（没有未解析的行）")
    add("")
    add("## 5. 复现")
    add("")
    add(FENCE)
    add("cd tools")
    add("python -m i18n_tools.mmgen --stats")
    add("python -m i18n_tools.mmgen --report reports/mmgen-coverage.md")
    add(FENCE)
    add("")
    target = os.path.dirname(os.path.abspath(path))
    if target:
        os.makedirs(target, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline=chr(10)) as handle:
        handle.write(chr(10).join(lines) + chr(10))
    return os.path.getsize(path)


def selftest():
    """不读真实数据、不写仓库目录的自检。返回 (失败项, 总项数)。"""
    checks = []

    def check(name, ok, detail=""):
        checks.append((name, bool(ok), detail))

    def eq(name, got, want):
        checks.append((name, got == want, "got={0!r} want={1!r}".format(got, want)))

    # 键形态
    eq("合法键通过", is_loc_key("#LOC_aje_part_title"), True)
    eq("带空格的半解析串不算键", is_loc_key("#Sol_Mercury_desc//The first"), False)
    eq("非键文本不算键", is_loc_key("Engine thrust"), False)
    eq("None 不算键", is_loc_key(None), False)

    # 值门禁
    eq("正常中文可用", value_problem("#K", "Low power", "电量低"), None)
    eq("空中文被拦", value_problem("#K", "Low power", "   "), "empty")
    eq("与英文相同被拦", value_problem("#K", "Low power", "Low power"), "same_as_source")
    eq("换行被拦", value_problem("#K", "Low power", "第一行\n第二行"), "newline")
    eq("花括号被拦", value_problem("#K", "Low power", "结构 {x}"), "brace")
    eq("行内注释被拦", value_problem("#K", "Low power", "站点 // 区域"), "comment")
    eq("URL 里的双斜线不算注释", value_problem("#K", "See https://x", "见 https://x"), None)
    eq("值是键引用被拦", value_problem("#K", "Low power", "#LOC_other"), "key_ref")
    eq("占位符一致可用", value_problem("#K", "Charge <<1>> of <<2>>", "充能 <<1>> / <<2>>"), None)
    eq("占位符丢失被拦", value_problem("#K", "Charge <<1>> of <<2>>", "充能完成"), "placeholder")

    # 文件名清洗
    eq("空 mod 名用固定文件名", mod_file_name(""), UNATTRIBUTED_FILE)
    eq("非法字符被替换", mod_file_name("A/B:C"), "A_B_C")
    eq("空白折成下划线", mod_file_name("Real Fuels"), "Real_Fuels")

    # 连接与顺序
    pool = [
        {"id": "1", "h": "aaaa", "text": "Part Title", "src": "loc", "mod": "ModA", "key": "#LOC_a_title"},
        {"id": "2", "h": "bbbb", "text": "Part Desc", "src": "loc", "mod": "ModA", "key": "#LOC_a_desc"},
        {"id": "3", "h": "cccc", "text": "Cfg Field", "src": "cfg", "mod": "ModA"},
    ]
    translations = Translations(by_key={"#LOC_a_title": "零件标题"}, by_hash={"bbbb": "零件描述"})
    patches, unresolved, skip, _ambiguous = build_rows(pool, translations)
    eq("按 key 连接成功", dict(patches.get("ModA") or []).get("#LOC_a_title"), "零件标题")
    eq("按 h 连接成功", dict(patches.get("ModA") or []).get("#LOC_a_desc"), "零件描述")
    eq("cfg 行不参与", skip.get("cfg_row"), 1)
    bad_key_pool = [{"h": "7", "text": "x", "src": "loc", "mod": "ModD", "key": "#a b"}]
    _p4, _u4, skip4, _a4 = build_rows(bad_key_pool, Translations(by_hash={"7": "甲"}))
    eq("非键形态的 loc 行单记 bad_key", skip4.get("bad_key"), 1)
    eq("没有未解析时列表为空", len(unresolved), 0)
    eq("条目按 key 排序", [key for key, _zh in patches["ModA"]], ["#LOC_a_desc", "#LOC_a_title"])

    # 歧义守卫
    ambiguous_pool = [
        {"h": "1", "text": "Alpha", "src": "loc", "mod": "ModB", "key": "#LOC_b"},
        {"h": "2", "text": "Beta", "src": "loc", "mod": "ModB", "key": "#LOC_b"},
    ]
    patches2, _u2, skip2, ambiguous2 = build_rows(ambiguous_pool, Translations(by_hash={"1": "甲", "2": "乙"}))
    eq("同一 key 两种中文时整条丢弃", patches2.get("ModB"), None)
    eq("歧义被计数", skip2.get(AMBIGUOUS_KEY_REASON), 1)
    eq("歧义 key 被记录", sorted(ambiguous2), ["#LOC_b"])

    # 未解析计数
    miss_pool = [{"h": "9", "text": "No translation", "src": "loc", "mod": "ModC", "key": "#LOC_c"}]
    patches3, unresolved3, _s3, _a3 = build_rows(miss_pool, Translations())
    eq("桥不到中文记未解析", len(unresolved3), 1)
    eq("未解析不产生文件", patches3.get("ModC"), None)

    # 往返校验
    entries = [("#LOC_x_title", "引擎推力（真空）"), ("#LOC_x_dyn", "充能 <<1>> / <<2>>")]
    text = render_cfg("ModX", entries, "zh-cn")
    ok, message = verify_cfg(text, "zh-cn", entries)
    check("生成的 cfg 能逐条读回", ok, message)
    eq("读回来的取值与写入一致", read_back(text, "zh-cn")["#LOC_x_dyn"], "充能 <<1>> / <<2>>")
    eq("节点结构是 Localization 加语言码", cfgparse.parse_text(text).child("Localization") is not None, True)
    eq("值里的等号被完整保留", read_back(render_cfg("M", [("#K", "a = b")], "zh-cn"), "zh-cn")["#K"], "a = b")

    # 落盘与幂等
    base = tempfile.mkdtemp(prefix="ui18n_mmgen_")
    try:
        outdir = os.path.join(base, "native", "zh-cn")
        files, info = build_files(pool, translations, "zh-cn")
        eq("内存产物只有一个文件", sorted(files), ["ModA.cfg"])
        eq("info 里的键数正确", info["keys"], 2)
        write_files(outdir, files)
        with open(os.path.join(outdir, "ModA.cfg"), "r", encoding="utf-8") as handle:
            first = handle.read()
        write_files(outdir, files)
        with open(os.path.join(outdir, "ModA.cfg"), "r", encoding="utf-8") as handle:
            second = handle.read()
        eq("跑两遍字节相同（幂等）", first, second)
        eq("--check 对最新产物无误报", compare_with_disk(outdir, files), ([], [], []))
        with open(os.path.join(outdir, "ModA.cfg"), "a", encoding="utf-8") as handle:
            handle.write("// 手改一行\n")
        eq("--check 能发现被手改的文件", compare_with_disk(outdir, files)[1], ["ModA.cfg"])
        with open(os.path.join(outdir, "Extra.cfg"), "w", encoding="utf-8") as handle:
            handle.write("Localization\n{\n}\n")
        eq("--check 能发现多余文件", compare_with_disk(outdir, files)[2], ["Extra.cfg"])
        os.remove(os.path.join(outdir, "ModA.cfg"))
        eq("--check 能发现缺文件", compare_with_disk(outdir, files)[0], ["ModA.cfg"])
    finally:
        shutil.rmtree(base, ignore_errors=True)

    # 覆盖率
    cov = coverage(pool, patches)
    eq("覆盖率分母只数 loc 行", cov["total"], 2)
    eq("覆盖率分子是发出的条目", cov["covered"], 2)
    eq("按 mod 汇总", cov["per_mod"], [("ModA", 2, 2)])

    failed = [row for row in checks if not row[1]]
    for name, ok, detail in checks:
        print(("  ok   " if ok else "  FAIL ") + name + ("" if ok else "  " + detail))
    print("[mmgen-selftest] " + str(len(checks)) + " 项，失败 " + str(len(failed)))
    # 与其它工具的 selftest 约定一致：第一个返回值是「失败项清单」，第二个是总项数。
    # 门禁（selftest.test_extra_tools）直接拿 len(清单) 报数，返回计数会让它崩在 len(int) 上。
    return (failed, len(checks))


def main(argv=None):
    ensure_stdout()
    parser = argparse.ArgumentParser(
        description="mmgen: 把池里的本地化键与中文记忆发射成 KSP 原生语言节点 cfg")
    parser.add_argument("--lang", default=DEFAULT_LANG, help="目标语言目录名（默认 zh-cn）")
    parser.add_argument("--pool", default=POOL_PATH, help="候选池（默认 packs/pool.jsonl）")
    parser.add_argument("--packs-dir", default=None, help="语言包目录（默认 packs/<lang>）")
    parser.add_argument("--out", default=None, help="产物目录（默认 build/native/<lang>）")
    parser.add_argument("--stats", action="store_true", help="只统计，不写文件")
    parser.add_argument("--check", action="store_true", help="只校验磁盘产物是否最新（CI 用）")
    parser.add_argument("--report", default=None, help="额外写覆盖率报告 md")
    parser.add_argument("--min-cover", type=float, default=None, help="覆盖率下限（0~1），低于则退出码 2")
    parser.add_argument("--quiet", action="store_true")
    parser.add_argument("--selftest", action="store_true")
    args = parser.parse_args(argv)

    if args.selftest:
        failures, _total = selftest()
        return 1 if failures else 0

    packs_dir = args.packs_dir or os.path.join(PACKS_ROOT, args.lang)
    outdir = args.out or os.path.join(BUILD_ROOT, args.lang)
    if not os.path.isfile(args.pool):
        parser.error("候选池不存在: " + args.pool)
    if not os.path.isdir(packs_dir):
        parser.error("语言包目录不存在: " + packs_dir)

    pool_rows = load_pool(args.pool)
    translations = load_translations(packs_dir)
    files, info = build_files(pool_rows, translations, args.lang)
    cov = coverage(pool_rows, info["patches"])

    if not args.quiet:
        print("[mmgen] lang=" + args.lang + " pool=" + args.pool)
        resolved, ratio_resolved = resolved_ratio(cov, info)
        print("[mmgen] 池 loc 行 {:,} / 已发出 {:,} ({:.1f}%) / 能桥到中文 {:,} ({:.1f}%) / 未解析 {:,}".format(
            cov["total"], cov["covered"], cov["ratio"] * 100.0,
            resolved, ratio_resolved * 100.0, len(info["unresolved"])))
        skip_text = ", ".join("{0}={1}".format(k, v) for k, v in sorted(info["skip"].items()))
        print("[mmgen] 跳过: " + (skip_text or "（无）"))
        print("[mmgen] 文件 {:,} 个 / 条目 {:,} 条".format(info["mods"], info["keys"]))

    if args.report:
        size = render_report(args.report, info, cov)
        if not args.quiet:
            print("[mmgen] report: {:,} B -> {}".format(size, args.report))

    below = args.min_cover is not None and cov["ratio"] < args.min_cover
    if below and not args.quiet:
        print("[mmgen] 覆盖率 {0:.1f}% 低于下限 {1:.1f}%".format(
            cov["ratio"] * 100.0, args.min_cover * 100.0))

    if args.check:
        missing, stale, extra = compare_with_disk(outdir, files)
        problems = []
        if missing:
            problems.append("缺 {0} 个（{1}）".format(len(missing), ", ".join(missing[:5])))
        if stale:
            problems.append("过期 {0} 个（{1}）".format(len(stale), ", ".join(stale[:5])))
        if extra:
            problems.append("多余 {0} 个（{1}）".format(len(extra), ", ".join(extra[:5])))
        if problems:
            print("[mmgen] 产物与数据不一致: " + "；".join(problems))
            print("[mmgen] 重新生成: python -m i18n_tools.mmgen")
            return 1
        if not args.quiet:
            print("[mmgen] 产物最新: " + outdir)
        return 0

    if not args.stats:
        written = write_files(outdir, files)
        if not args.quiet:
            total_bytes = sum(size for _path, size in written)
            print("[mmgen] 写出 {:,} 个文件 / {:,} B -> {}".format(len(written), total_bytes, outdir))
        return 2 if below else 0

    if not args.quiet:
        for mod, total, got in cov["per_mod"][:20]:
            print("  {0:<32} {1:>5,} / {2:>5,}".format(mod, got, total))
        if len(cov["per_mod"]) > 20:
            print("  ... 另有 {0} 个 mod".format(len(cov["per_mod"]) - 20))
    return 2 if below else 0


if __name__ == "__main__":
    sys.exit(main())
