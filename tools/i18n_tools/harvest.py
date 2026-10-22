r"""P0 抽取 + P1 归一化 + P2 过滤：产出翻译候选池 packs/pool.jsonl 与统计 packs/inventory.json。

读：<游戏根>\GameData\**\*.cfg
写：<仓库>\packs\pool.jsonl、<仓库>\packs\inventory.json（都只写这两处）

两条来源（doc 04 §3）：
  源一 Localization 差集 —— 按 mod 目录把各语言文件求**并集**，再算 en-us 减 zh-cn 的差。
      坑：KSP 的本地化是"一个语言一个文件"，按文件做差集会得出"全都缺中文"的假结论。
      实测：先按 mod 求并集才能得到 8975 个 en 键 / 5644 个已覆盖 / 3331 个缺中文。
  源二 cfg 文本字段 —— 节点值里 title / description / displayName（大小写不敏感、保留原写法）。
      tags 算命中但不译（走 search-tags 跳过计数）。

用法（在 UniversalI18n\tools 下）：
    python -m i18n_tools.harvest
    python -m i18n_tools.harvest --gamedata "D:\Kerbal Space Program_newmod\GameData"
    python -m i18n_tools.harvest --verify ..\_scratch\corpus\pool.jsonl   # 与既有产物逐行比对
    python -m i18n_tools.harvest --quiet
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
from datetime import datetime, timezone

from . import cfgparse, ensure_stdout, fnv, textnorm

LOC_DIR_NAME = "localization"      # 目录段名（大小写不敏感）
LANG_EN = "en-us"
LANG_ZH = "zh-cn"
CORE_TEXT_FIELDS = ("title", "description", "displayname")
TAGS_FIELD = "tags"
SCAN_HINTS = ("title", "description", "displayname", "tags")
PATCH_PREFIX = "@$%!&"            # ModuleManager 的补丁前缀（@title / $title），匹配字段名时先剥掉
# 文本字段的**后缀变体**：doc 04 §3.1 明确写了"含 xxx_title 这类后缀变体"，真值里确实有
#   RP-1\Parts\Science\Assets\Saturn\bluedog_Pegasus_ElectronSpectrometer.cfg:21  real_title = Pegasus ...
#   ...:23                                                                        real_description = This ...
#   RealismOverhaul\RO_SuggestedMods\Bluedog_DB\Generic\RO_General_Stuff.cfg:30 !real_title = NULL
#   12 条 RP1_Chinese 的 "#rp0_..._Title = 中文"（值本身是中文，会被 already-cjk 过滤掉）
# 入池时字段名归一成后缀对应的基础名（真值里这 3 行的 field 就是 title / description）。
FIELD_VARIANTS = (("_title", "title"), ("_description", "description"), ("_displayname", "displayName"))
# Localization 的键必须是可打印 ASCII（见 collect_localization 里的说明）
_ASCII_KEY_RE = re.compile(r"[!-~]+$")
# 键里不能带圆括号：Principia 的 Localization\celestial_strings.cfg 用
# "#Principia_MapNode_PeriapsisHeader(Sun).en-us" 这种把天体名塞进键里的写法，既有产物的键
# 全部是 #LOC_* / #autoLOC_* / #ro* 形式、一条带括号的都没有，本实现按同一口径丢弃（19 条）。
_PAREN_KEY_RE = re.compile(r"[()]")

# doc 04 §4.2 的过滤顺序（顺序本身是契约：先 empty 再 is-key … 最后 dup）
FILTER_ORDER = ("empty", "is-key", "already-cjk", "pure-number", "too-short",
                "search-tags", "url-only", "already-covered", "dup")


# ---------------------------------------------------------------------------
# 路径
# ---------------------------------------------------------------------------
def repo_root() -> str:
    r"""<repo>\tools\i18n_tools\harvest.py → <repo>"""
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


# 仓库搬出游戏根之后，“仓库上一级 = 游戏根”的推断会失效（会指向一个不存在的 GameData），
# 所以默认解析加一级回退：本机已知的 KSP 安装目录（只在目录真实存在时生效）。
# 换机器 / 换安装位置时请用 --gamedata 或环境变量 UI18N_GAMEDATA 指定，不要改这张表。
KSP_GAMEDATA_FALLBACKS = (r"D:\Kerbal Space Program_newmod\GameData",)


def default_gamedata() -> str:
    r"""默认 GameData，按优先级解析：

    ① 环境变量 UI18N_GAMEDATA（显式指定，永远最高优先）
    ② <仓库上一级>\GameData —— 仓库还放在游戏根里时的传统布局
    ③ KSP_GAMEDATA_FALLBACKS 里第一个真实存在的目录（本机安装位置）

    历史：仓库原先在 <游戏根>\UniversalI18n 下，② 恒成立；2026-10 仓库迁到
    C:\Users\43701\Documents\github\UniversalI18n 后 ② 会得到 C:\Users\43701\Documents\github\GameData
    （不存在），因此必须有 ③，否则所有不带 --gamedata 的命令都会报找不到 GameData。
    解析不出来时返回 ②，让上层照常打印“找不到 GameData 目录”的错误。
    """
    from_env = os.environ.get("UI18N_GAMEDATA")
    if from_env:
        return os.path.abspath(from_env)
    beside_repo = os.path.join(os.path.dirname(repo_root()), "GameData")
    if os.path.isdir(beside_repo):
        return beside_repo
    for cand in KSP_GAMEDATA_FALLBACKS:
        if os.path.isdir(cand):
            return cand
    return beside_repo


def default_pack(name: str) -> str:
    return os.path.join(repo_root(), "packs", name)


# ---------------------------------------------------------------------------
# P0 发现
# ---------------------------------------------------------------------------
def _walk_in_order(root_dir, parts, out, depth=0):
    r"""中序深度优先：把"子目录"和"* .cfg 文件"放进**同一个列表**按名字大写序排，遇到文件
    就收、遇到目录就当场递归下去。等价于在资源管理器里展开 GameData 从上往下逐个双击。

    这是唯一同时满足下面三条实测事实的规则（三条缺一不可，改法都试过）：
      1) mod 序：B9PartSwitch 在 B9_Aerospace_ProceduralWings 之前——'B9_' 与 'B9P' 比，
         '_' 0x5F > 'P' 0x50；同理 Kopernicus 在 KSPCommunityFixes 之前。按 str.lower 排
         会得到相反结果，这正是最初 mod 序列错的原因。
      2) B9_Aerospace_ProceduralWings：根目录的 B9PWingsUnlimited.cfg 排在 Parts/… 之前
         （'B9PWINGSUNLIMITED.CFG' < 'PARTS'），所以不能"根文件一律最后收"。
      3) 但 ProceduralFairings 的 PF_Settings.cfg 又排在 Parts/、Patches/ 之后
         （'PA' < 'PF'），ROCapsules 里 PartConfigs/Apollo/… 也要在 PartConfigs/Apollo D2/…
         之前（比较的是两个目录名 'APOLLO' / 'APOLLO D2'，前缀短者在前；若按整条路径
         大写比较，空格 0x20 < '/' 0x2F 会把 'Apollo D2' 排前面）。
      → 只有"目录与文件混排 + 逐层按名字大写比较"能同时解释这三条。
    """
    try:
        entries = list(os.scandir(root_dir))
    except OSError:
        return
    entries.sort(key=lambda ent: ent.name.upper())
    for ent in entries:
        try:
            is_dir = ent.is_dir()
        except OSError:
            continue
        if is_dir:
            if depth < 64:
                _walk_in_order(ent.path, parts + [ent.name], out, depth + 1)
            continue
        if not ent.name.lower().endswith(".cfg"):
            continue
        mod = parts[0] if parts else ent.name   # GameData 根的游离 cfg：以文件名当 mod
        out.append({
            "path": ent.path,
            "rel": "/".join(parts + [ent.name]),
            "mod": mod,
            "loc": any(p.lower() == LOC_DIR_NAME for p in parts),
        })


def discover(gamedata: str):
    r"""遍历一遍 GameData，按 mod（顶层条目名）分组。

    返回 (groups, cfg_files)：
      groups = {mod: [{"path", "rel", "mod", "loc"}, ...]}，mod 内文件序见 _walk_in_order；
      mod 之间的顺序就是顶层目录名大写序（根目录游离 cfg 的最后出现）。
      loc 判定 = 相对 GameData 的路径里有任意一段目录名等于 Localization（大小写不敏感）。
      实测该判定精确命中 116 文件 / 28 mod；不能用"文件内容含 Localization 节点"（会多收
      PatchManager\Lang\*.cfg 与 *-Localization.cfg 之类，实测虚高到 181 文件 / 32 mod）。

    顺序决定 pool 行号与 id，必须与既有产物逐字节一致，所以这里刻意复刻"资源管理器里
    从上往下点"的次序（大小写不敏感 + 目录与文件混排），见 _walk_in_order 的注释。
    """
    entries: list[dict] = []
    _walk_in_order(gamedata, [], entries)
    groups: dict[str, list[dict]] = {}
    for rec in entries:
        groups.setdefault(rec["mod"], []).append(rec)
    return groups, len(entries)


# ---------------------------------------------------------------------------
# 源一：Localization 语言并集
# ---------------------------------------------------------------------------
def collect_localization(mod_names, groups, parse_stats):
    """按 mod 求语言并集，返回差集候选与统计。

    返回 (cands, mod_table, counters, zh_values)：
      cands      [{"mod", "key", "text"}]，只有"有 en 没 zh"的键，保持 mod/文件/行 顺序
      mod_table  {mod: {"en", "zh", "covered", "missing"}}
      counters   {"loc_files", "mods_with_loc", "zh_assign", "zh_unique"}
      zh_values  全部 zh-cn 译文（P2 的 already-covered 判据要用）
    """
    cands: list[dict] = []
    mod_table: dict[str, dict] = {}
    loc_files = 0
    mods_with_loc = 0
    zh_assign = 0
    zh_unique = 0
    non_ascii_keys = 0
    paren_keys = 0
    zh_values: list[str] = []

    for mod in mod_names:
        en: dict[str, str] = {}
        zh: dict[str, str] = {}
        has_loc = False
        for rec in groups[mod]:
            if not rec["loc"]:
                continue
            has_loc = True
            loc_files += 1
            root = cfgparse.parse_file(rec["path"], stats=parse_stats)
            for node in root.walk():
                if node.name.lower() != LOC_DIR_NAME:
                    continue
                for lang in node.children:
                    lname = lang.name.lower()
                    if lname == LANG_EN:
                        bucket = en
                    elif lname == LANG_ZH:
                        bucket = zh
                    else:
                        continue
                    for key, value, _line in lang.values:
                        # 键必须是可打印 ASCII。RealismOverhaul 的 en-us-Mfr.cfg 里有一条
                        # "#roMfrОКБ4 = ОКБ-4 (Bisnovat)"（键里直接写西里尔字母），既不是
                        # KSP Localizer 能引用的标识符，既有产物也一致不收，故丢弃并计数。
                        if _ASCII_KEY_RE.match(key) is None:
                            non_ascii_keys += 1
                            continue
                        if _PAREN_KEY_RE.search(key) is not None:
                            paren_keys += 1
                            continue
                        if lname == LANG_ZH:
                            zh_assign += 1
                        # 同语言多文件按发现序覆盖（赋值语义，后者赢）。实测依据：RealismOverhaul
                        # 的 #roBE3Title 在 en-us-Engines.cfg 里先出现 BE-3PM、后出现 BE-3U；
                        # 既有产物存的是 BE-3U，先到先得会存成 BE-3PM。
                        bucket[key] = value
        if not has_loc:
            continue
        mods_with_loc += 1
        covered = 0
        for key, value in en.items():
            if key in zh:
                # "已覆盖"要求 **zh 值非空**，而不只是键存在。依据：Principia 有 2 个键在 zh-cn.cfg
                # 里存在但值是空串（#Principia_FlightPlan_OptimizeInclinationNoText、
                # #Principia_BurnEditor_OptimizingOther），既有 inventory 把它们算作缺失
                # （Principia 224/226/7）；按"键存在"算会得 224/226/5，且全局 covered 会多 2。
                # 注意：**候选生成不能用这条判据**——按键存在判定才能让候选数与既有 pool 行数吻合
                # （用"值非空"会把这两个键当候选，loc 候选 +2 而 pool 行数不变，说明上一轮只把它
                # 用于统计，没有用于候选）。
                if textnorm.normalize(zh[key]):
                    covered += 1
            else:
                cands.append({"mod": mod, "key": key, "text": value})
        mod_table[mod] = {"en": len(en), "zh": len(zh), "covered": covered,
                          "missing": len(en) - covered}
        zh_unique += len(zh)
        zh_values.extend(zh.values())

    return cands, mod_table, {"loc_files": loc_files, "mods_with_loc": mods_with_loc,
                              "zh_assign": zh_assign, "zh_unique": zh_unique,
                              "non_ascii_keys": non_ascii_keys,
                              "paren_keys": paren_keys}, zh_values


# ---------------------------------------------------------------------------
# 源二：cfg 文本字段
# ---------------------------------------------------------------------------
def collect_cfg(mod_names, groups, parse_stats):
    r"""扫非 Localization 的 cfg，收集文本字段候选。

    **字段名必须原样匹配**：ModuleManager 的补丁写法（@title / $title / %title）不算命中。
    实测依据（这是本轮最关键的发现，靠"把既有产物 12,792 行按文本 hash 反查回我的赋值"得出）：
      - 严格匹配后 tags 命中恰好 1597 = 既有产物的 search-tags 跳过数，一位不差；
      - RealismOverhaul 里母节点为 @PART 的赋值中，裸键 title/description 命中 61 缺 9，
        带 @ 前缀的命中 196 缺 7,289 —— 补丁值不进池（补丁只覆盖文本，真文本在原定义处）；
      - 严格匹配后的 core 文本去重得 9,959 条，其中 9,941 条能在既有产物里找到，
        只有 18 条既不在池里也不在别处，说明候选集合与既有流水线吻合。
    早期版本用 lstrip("@$%!&") 剥前缀，导致多收 6,427 行、丢掉 3,107 行（键引用文本被 is-key 误杀）。

    返回 (cands, field_hits, field_hist, zh_stats)：
      cands      [{"mod", "field", "text", "tags"}]
      field_hits 精确命中数（含 tags）
      field_hist {原写法字段名: 次数}
      zh_stats   {"zh_assign", "zh_unique"} —— 值本身就是中文的赋值数。
                 口径：**剥掉补丁前缀**后再匹配字段名（与既有产物 2408 / 2224 对齐，
                 说明上一轮算这个统计时是剥前缀的，与候选集合的严格口径不同）。
    """
    cands: list[dict] = []
    field_hits = 0
    field_hist: dict[str, int] = {}
    zh_assign = 0
    zh_hashes: set[str] = set()

    for mod in mod_names:
        for rec in groups[mod]:
            if rec["loc"]:
                continue
            text = cfgparse.read_text(rec["path"])
            low = text.lower()
            # 廉价预筛：绝大多数 cfg 是零件/资源配置，一个文本字段都没有，不用进解析器
            if not any(hint in low for hint in SCAN_HINTS):
                continue
            root = cfgparse.parse_text(text, stats=parse_stats)
            # 文件内必须按**行号**（文档序）输出，不能按 root.walk() 的"先父节点值、后子节点"序：
            # 实测 VenStockRevamp\PartBin\Data\Engines.cfg 的 VARIANT 子节点 displayName 在第 18/35 行，
            # 而 PART 自己的 title/description 在第 55/57 行，既有产物把 18/35 排在前面（=文档序），
            # walk 序会把 55/57 提前。这个坑影响 234 行。
            staged: list[tuple[int, str, str, bool]] = []
            for node in root.walk():
                for key, value, line in node.values:
                    name = key.strip()
                    lname = name.lower()
                    loose = name.lstrip(PATCH_PREFIX).lower()
                    if loose in CORE_TEXT_FIELDS or loose == TAGS_FIELD:
                        norm = textnorm.normalize(value)
                        if norm and textnorm.has_cjk(norm):
                            zh_assign += 1
                            zh_hashes.add(fnv.hash_text(norm))
                    if lname in CORE_TEXT_FIELDS:
                        field_name = name
                        is_tags = False
                    elif lname == TAGS_FIELD:
                        field_name = name
                        is_tags = True
                    else:
                        # 后缀变体（real_title / !real_description / #rp0_..._Title）：
                        # 字段名归一成基础名，见 FIELD_VARIANTS 上的说明。
                        field_name = None
                        for suffix, base in FIELD_VARIANTS:
                            if lname.endswith(suffix):
                                field_name = base
                                break
                        if field_name is None:
                            continue
                        is_tags = False
                    field_hits += 1
                    field_hist[name] = field_hist.get(name, 0) + 1
                    staged.append((line, field_name, value, is_tags))
            staged.sort(key=lambda item: item[0])
            cands.extend({"mod": mod, "field": field_name, "text": value, "tags": is_tags}
                         for _line, field_name, value, is_tags in staged)

    return cands, field_hits, field_hist, {"zh_assign": zh_assign, "zh_unique": len(zh_hashes)}


# ---------------------------------------------------------------------------
# P1 + P2：归一化、过滤、去重、编号
# ---------------------------------------------------------------------------
def build_rows(loc_cands, cfg_cands, zh_values):
    """归一化 + 按 doc 04 §4.2 顺序过滤 + 按 FNV-1a64 去重 + 编号。

    loc 候选整体排在 cfg 候选之前（同一文本两边都出现时保留 loc 那条，因为它有 key 可回溯）。
    """
    skip = {key: 0 for key in FILTER_ORDER}

    # "已覆盖"（already-covered）槽位保留但**停用**，理由有二：
    #   1) doc 04 §4.2 与既有产物的 hit 都是 0，契约上它不该命中；
    #   2) 曾经按"文本等于某条 zh 译文"实现，实测误杀 55 条：KerbNet / Ap / Pe / AN / DN /
    #      UH25 / Default / ISRU / ECLSS / RCS 这些**英文专名本身就是合法待译文本**，
    #      恰好和某条 zh 译文完全同形（中文里保留了英文缩写）。其中 8 条在既有产物里确有。
    # 参数 zh_values 保留在签名里（collect_localization 的返回契约不变），此处不再使用。
    del zh_values

    seen: set[str] = set()
    rows: list[dict] = []
    for cand in list(loc_cands) + list(cfg_cands):
        text = textnorm.normalize(cand["text"])
        if not text:
            skip["empty"] += 1
            continue
        # 注意：**不能**做 is-key 过滤。既有产物里有 3,193 行 text 以 "#" 开头（Squad 的
        # "title = #autoLOC_501662"、Sol-Configs 的 "#Sol_Sun_name"、SXT 的 "#LOC.SXT_*"），
        # 键引用文本本身就是待译内容（由下游解析 key 后取译文）。上一轮把 is-key 打开，
        # 误杀 6,487 条，正是 Squad / Sol-Configs / ReStock / CRP 等 mod 缺行的主因。
        # 计数槽位保留（FILTER_ORDER 是契约），命中恒为 0 时不进 skip 输出。
        if textnorm.has_cjk(text):
            skip["already-cjk"] += 1
            continue
        if textnorm.is_pure_number(text):
            skip["pure-number"] += 1
            continue
        if len(text) < 2:
            skip["too-short"] += 1
            continue
        if cand.get("tags"):
            skip["search-tags"] += 1
            continue
        if textnorm.is_url_only(text):
            skip["url-only"] += 1
            continue
        h = fnv.hash_text(text)
        # 这里原本有 "if h in zh_hashes: already-covered" 的判断，已按上面的说明停用。
        if h in seen:
            skip["dup"] += 1
            continue
        seen.add(h)

        row = {
            "id": len(rows) + 1,
            "h": h,
            "text": text,
            "src": "loc" if "key" in cand else "cfg",
            "mod": cand["mod"],
        }
        # 键顺序与既有产物一致：loc 行是 key，cfg 行是 field，都在 mod 之后
        if "key" in cand:
            row["key"] = cand["key"]
        else:
            row["field"] = cand["field"]
        row.update(textnorm.features(text))
        rows.append(row)

    return rows, skip


# ---------------------------------------------------------------------------
# 统计
# ---------------------------------------------------------------------------
def build_inventory(rows, *, cfg_files, loc_files, mods_with_loc, mod_table,
                    cfg_field_hits, skip, counters, generated_at):
    """按 inventory.json 的既有 schema 出统计。"""
    loc_rows = sum(1 for r in rows if r["src"] == "loc")
    cfg_rows = len(rows) - loc_rows

    buckets = {name: 0 for name, _lo, _hi in textnorm.LEN_BUCKETS}
    for r in rows:
        buckets[textnorm.len_bucket(r["len"])] += 1

    counts: dict[str, int] = {}
    for r in rows:
        counts[r["mod"]] = counts.get(r["mod"], 0) + 1
    top_mods = sorted(counts.items(), key=lambda kv: (-kv[1], kv[0].lower()))[:20]

    missing_first = sorted(mod_table.items(), key=lambda kv: (-kv[1]["missing"], kv[0].lower()))
    mod_stats = [{"mod": m, "en": v["en"], "zh": v["zh"], "missing": v["missing"]}
                 for m, v in missing_first if v["missing"] > 0][:20]

    total_len = sum(r["len"] for r in rows)
    prompt = total_len // 2                     # 英文约 2 字符/token（保守估）
    output = total_len // 3                     # 中文译文约 3 字符/token

    inventory = {
        "generatedAt": generated_at,
        "cfgFiles": cfg_files,
        "locFiles": loc_files,
        "modsWithLocalization": mods_with_loc,
        "locKeysTotal": sum(v["en"] for v in mod_table.values()),
        "locKeysCovered": sum(v["covered"] for v in mod_table.values()),
        "locKeysMissingZh": sum(v["missing"] for v in mod_table.values()),
        "cfgFieldHits": cfg_field_hits,
        "zhCoveredAssignments": counters["zh_assign"],
        "zhCoveredUnique": counters["zh_unique"],
        "poolSize": len(rows),
        "skip": {k: v for k, v in sorted(skip.items(), key=lambda kv: (-kv[1], kv[0])) if v},
        "poolBreakdown": {
            "loc": loc_rows,
            "cfg": cfg_rows,
            "withPlaceholder": sum(1 for r in rows if r["ph"]),
            "withRichText": sum(1 for r in rows if r["rt"]),
            "withDigits": sum(1 for r in rows if r["dg"]),
            "withPath": sum(1 for r in rows if r["path"]),
            "withUrl": sum(1 for r in rows if r["url"]),
            "lenBuckets": buckets,
        },
        "modStats": mod_stats,
        "topMods": [[m, n] for m, n in top_mods],
        "tokenEstimate": {"prompt": prompt, "output": output, "total": prompt + output},
    }
    return inventory


# ---------------------------------------------------------------------------
# 落盘
# ---------------------------------------------------------------------------
def write_jsonl(path, rows):
    """紧凑 JSON + LF + 无 BOM，逐行一个对象（中间先写 .tmp 再原子替换）。

    细节：**最后一行不带换行**——既有产物就是这样的（12,792 行 / 12,791 个 LF，末尾是
    '"len":14}' 而不是 '\n'），差这一个字节 sha256 就对不上。
    """
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    tmp = path + ".tmp"
    body = "\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) for row in rows)
    with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(body)
    os.replace(tmp, path)


def write_json(path, obj):
    """indent=2 + LF + 无 BOM + 末尾无换行（与既有 inventory.json 逐字节一致）。"""
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(json.dumps(obj, ensure_ascii=False, indent=2))
    os.replace(tmp, path)


# ---------------------------------------------------------------------------
# 与既有产物逐行比对（验收用）
# ---------------------------------------------------------------------------
def _row_diff(line_a: str, line_b: str) -> str:
    try:
        a = json.loads(line_a)
    except json.JSONDecodeError:
        return "本行不是合法 JSON: " + line_a[:120]
    try:
        b = json.loads(line_b)
    except json.JSONDecodeError:
        return "对照行不是合法 JSON: " + line_b[:120]
    if list(a.keys()) != list(b.keys()):
        return "字段集/顺序不同 我=%s 对照=%s" % (list(a.keys()), list(b.keys()))
    parts = []
    for k in a:
        if a[k] != b[k]:
            va, vb = a[k], b[k]
            if isinstance(va, str) and len(va) > 60:
                va = va[:60] + "…"
            if isinstance(vb, str) and len(vb) > 60:
                vb = vb[:60] + "…"
            parts.append("%s: 我=%r 对照=%r" % (k, va, vb))
    return "; ".join(parts) if parts else "文本相同但字节不同（编码/转义差异）"


def verify(pool_path, ref_path, limit=10):
    """逐行逐字段比对两份 pool.jsonl，返回 (是否完全一致, 差异列表, 我的行数, 对照行数)。"""
    diffs = []
    n = 0
    with open(pool_path, encoding="utf-8") as fa, open(ref_path, encoding="utf-8") as fb:
        while True:
            la = fa.readline()
            lb = fb.readline()
            if not la and not lb:
                break
            n += 1
            if la != lb:
                if len(diffs) < limit:
                    diffs.append((n, _row_diff(la, lb)))
    mine = sum(1 for _ in open(pool_path, encoding="utf-8"))
    ref = sum(1 for _ in open(ref_path, encoding="utf-8"))
    return (mine == ref and not diffs), diffs, mine, ref


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------
def main(argv=None) -> int:
    ensure_stdout()
    ap = argparse.ArgumentParser(
        prog="python -m i18n_tools.harvest",
        description="从 GameData 抽取 KSP 文本候选池（标准库，零依赖，不联网）")
    ap.add_argument("--gamedata", default=None, help="GameData 目录（默认 UI18N_GAMEDATA / 仓库同级 / 本机安装目录）")
    ap.add_argument("--out", default=None, help="pool.jsonl 输出路径（默认 <repo>/packs/pool.jsonl）")
    ap.add_argument("--inventory", default=None, help="inventory.json 输出路径（默认 <repo>/packs/inventory.json）")
    ap.add_argument("--verify", default=None, help="与一份既有 pool.jsonl 逐行比对")
    ap.add_argument("--limit", type=int, default=10, help="--verify 时最多打印几条差异")
    ap.add_argument("--quiet", action="store_true", help="只写文件，不打印统计")
    args = ap.parse_args(argv)

    gamedata = args.gamedata or default_gamedata()
    out = args.out or default_pack("pool.jsonl")
    inv_out = args.inventory or default_pack("inventory.json")
    if not os.path.isdir(gamedata):
        print("找不到 GameData 目录: " + gamedata, file=sys.stderr)
        return 2

    t0 = time.perf_counter()
    groups, cfg_files = discover(gamedata)
    t_scan = time.perf_counter() - t0

    # mod 名排序 = **大写化后的 ASCII 序**，不是 str.lower 序。
    # 实测依据：既有产物里 B9PartSwitch 排在 B9_Aerospace_ProceduralWings 之前
    # （大写化后 "B9PARTSWITCH" < "B9_AEROSPACE_…"，因为 'P' 0x50 < '_' 0x5F），
    # 而 Kopernicus 又排在 KSPCommunityFixes 之前（"KOPERNICUS" < "KSPCOMMUNITYFIXES"）。
    # 这正是 NTFS 目录索引的序（大小写不敏感按大写比较），str.lower 会把 B9 那一对排错。
    mod_names = sorted(groups, key=str.upper)
    parse_stats = cfgparse.ParseStats()

    t1 = time.perf_counter()
    loc_cands, mod_table, counters, zh_values = collect_localization(mod_names, groups, parse_stats)
    t_loc = time.perf_counter() - t1

    t2 = time.perf_counter()
    cfg_cands, cfg_field_hits, field_hist, cfg_zh_stats = collect_cfg(mod_names, groups, parse_stats)
    t_cfg = time.perf_counter() - t2
    # inventory 的 zhCovered* 是"cfg 值本来就是中文"的统计口径（既有产物 2408 / 2224），
    # 不是 loc 文件里 zh-cn 的赋值数，所以这里用 cfg 侧的统计覆盖掉 collect_localization 的同名槽位。
    counters = dict(counters)
    counters.update(cfg_zh_stats)

    t3 = time.perf_counter()
    rows, skip = build_rows(loc_cands, cfg_cands, zh_values)
    t_filter = time.perf_counter() - t3

    generated_at = datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")
    inventory = build_inventory(rows, cfg_files=cfg_files, loc_files=counters["loc_files"],
                                mods_with_loc=counters["mods_with_loc"], mod_table=mod_table,
                                cfg_field_hits=cfg_field_hits, skip=skip, counters=counters,
                                generated_at=generated_at)

    t4 = time.perf_counter()
    write_jsonl(out, rows)
    write_json(inv_out, inventory)
    t_write = time.perf_counter() - t4
    total = time.perf_counter() - t0

    if not args.quiet:
        pb = inventory["poolBreakdown"]
        print("[harvest] GameData      " + gamedata)
        print("[harvest] cfg 文件      %d（Localization 文件 %d，含本地化的 mod %d）"
              % (cfg_files, counters["loc_files"], counters["mods_with_loc"]))
        print("[harvest] 本地化键      en %d / 已覆盖 %d / 缺 zh %d"
              % (inventory["locKeysTotal"], inventory["locKeysCovered"], inventory["locKeysMissingZh"]))
        print("[harvest] cfg 文本字段  %d 处命中（字段分布：%s）"
              % (cfg_field_hits, ", ".join("%s=%d" % kv for kv in sorted(field_hist.items(),
                                                                        key=lambda kv: (-kv[1], kv[0])))))
        print("[harvest] 过滤            " + "  ".join("%s=%d" % kv for kv in inventory["skip"].items()))
        print("[harvest] 候选池          %d 行（loc %d + cfg %d）"
              % (inventory["poolSize"], pb["loc"], pb["cfg"]))
        print("[harvest] 特征            占位符 %d / 富文本 %d / 含数字 %d / 路径 %d / URL %d"
              % (pb["withPlaceholder"], pb["withRichText"], pb["withDigits"], pb["withPath"], pb["withUrl"]))
        print("[harvest] 长度桶          " + "  ".join("%s=%d" % kv for kv in pb["lenBuckets"].items()))
        print("[harvest] 解析            %d 文件 / %d 节点 / %d 键值 / 畸形行 %d"
              % (parse_stats.files, parse_stats.nodes, parse_stats.values, parse_stats.bad_lines))
        print("[harvest] 耗时            walk %.2fs + loc %.2fs + cfg %.2fs + 过滤 %.2fs + 落盘 %.2fs = %.2fs"
              % (t_scan, t_loc, t_cfg, t_filter, t_write, total))
        print("[harvest] 输出            " + out)
        print("[harvest] 输出            " + inv_out)

    status = 0
    if args.verify:
        same, diffs, mine, ref = verify(out, args.verify, args.limit)
        print("[verify] 我的 %d 行 / 对照 %d 行 → %s" % (mine, ref, "完全一致" if same else "有差异"))
        for lineno, detail in diffs:
            print("[verify] 第 %d 行  %s" % (lineno, detail))
        status = 0 if same else 1
    return status


if __name__ == "__main__":
    raise SystemExit(main())
