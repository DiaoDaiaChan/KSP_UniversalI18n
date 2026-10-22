"""glossary.py -- 官方双语术语表抽取（Squad autoLOC 键）。

为什么要这个产物：Core 侧翻译时要固定 KSP 官方术语（S1 SRB-KD25k、Kickback 之类的
零件名/单位名），不能每次自由发挥。这张表给出「英文官方原文 -> 中文官方译文」的成对样本，
作为翻译提示与术语一致性的锚点。

口径（全部对着真实 GameData 实测过，理由都写在下面，改之前先读 README「glossary 口径」）：

1. 中文来源：`GameData/Squad/Localization/dictionary.cfg` 的 Localization/zh-cn 节点。
   本安装这个文件**只有 zh-cn 一个语言节点**（12,031 行赋值 / 12,028 个唯一键，891,576 字节，
   BOM+CRLF），没有任何 en-us 节点 —— 这一点是实测的，不是假设（harvest 对 Squad 也得到
   en=0 / zh=12028）。所以英文绝不可能从 dictionary.cfg 里取。

2. 英文来源：Squad 的零件 cfg 里，值写成键引用、行尾用注释给出英文原文，例如

       title = #autoLOC_500460 //#autoLOC_500460 = S1 SRB-KD25k "Kickback" Solid Fuel Booster

   所以用 COMMENT_EN_RE 扫 GameData/Squad 下**非** Localization 目录的全部 cfg（533 个文件，
   其中只有 1 个是 dictionary.cfg），命中 3,290 处、2,126 个唯一键，同一键取首次出现
   （后出现的多为补丁文件复述）。

   【踩过的坑】如果把这 533 个文件里唯一的 Localization 文件也扫进来，dictionary.cfg 自己的
   12,031 行 `#autoLOC_N = 中文` 全部命中 COMMENT_EN_RE，于是「英文表」里 99% 是中文，
   唯一键数会变成 11,930（= 字典里 #autoLOC_ 形式的键数）。上一轮记录的「扫 533 个 cfg 得到
   11,930 条英文」就是这个错：11,930 其实是字典条目数，不是英文条数。本实现显式跳过 loc 文件，
   并在统计里把「字典键数 / 注释英文键数 / 配对键数」三个数分开打印，避免再混淆。

3. 配对：注释里的键是 `autoLOC_500460`（没有 #），dictionary.cfg 里是 `#autoLOC_500460`，
   **配对时必须补 #**，否则配对数为 0。配对率 100%（2,126/2,126）：注释里出现的键全部在字典里。
   反方向字典里另有一批键没有英文注释（其中 98 个是 `#autoLoc_` 小写 l 的驼峰变体），
   默认不写入 glossary.jsonl（双语表只收真有双语的）；加 --include-zh-only 可导出全部字典键，
   此时 en 为空串。

4. short：给「短标签」用的子集标记 —— 英文 <= 5 个词且长度 >= 4 字符（默认，可调），
   用于下游在磁贴/按钮这类窄控件里优先挑短词。实测 887 条。

输出：packs/glossary.jsonl，每行 {"key","en","zh","short"}，紧凑 JSON（无空格）、LF、末行无换行。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time

from . import cfgparse, ensure_stdout, harvest

COMMENT_EN_RE = re.compile(r"#(autoLOC_\d+)\s*=\s*([^\r\n]+)")
LANG_ZH = "zh-cn"
SHORT_MAX_WORDS = 5
SHORT_MIN_LEN = 4


def repo_root() -> str:
    r"""<repo>\tools\i18n_tools\glossary.py → <repo>"""
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def default_gamedata() -> str:
    return harvest.default_gamedata()


def default_out() -> str:
    return harvest.default_pack("glossary.jsonl")


def dictionary_path(gamedata: str) -> str:
    return os.path.join(gamedata, "Squad", "Localization", "dictionary.cfg")


def collect_en(gamedata: str) -> tuple[dict, dict]:
    """扫 Squad 非 Localization 目录的 cfg，返回 (en, stats)。en 按首次出现定序。"""
    squad_dir = os.path.join(gamedata, "Squad")
    groups, n_files = harvest.discover(squad_dir)
    en: dict[str, str] = {}
    hits = 0
    conflicts = 0
    scanned = 0
    for mod in sorted(groups, key=str.upper):
        for rec in groups[mod]:
            if rec["loc"]:
                # dictionary.cfg 自己的 `#autoLOC_N = 中文` 会被同一个正则命中，必须跳过。
                continue
            text = cfgparse.read_text(rec["path"])
            if "autoLOC_" not in text:
                continue
            scanned += 1
            for m in COMMENT_EN_RE.finditer(text):
                hits += 1
                key = "#" + m.group(1)
                value = m.group(2).strip()
                old = en.get(key)
                if old is None:
                    en[key] = value
                elif old != value:
                    conflicts += 1
    stats = {
        "squadCfgFiles": n_files,
        "cfgWithComment": scanned,
        "commentHits": hits,
        "commentUniqueKeys": len(en),
        "commentConflicts": conflicts,
    }
    return en, stats


def collect_zh(path: str) -> dict:
    """读 dictionary.cfg 的 Localization/zh-cn 节点，保持文档序。"""
    zh: dict[str, str] = {}
    if not os.path.isfile(path):
        return zh
    root = cfgparse.parse_file(path)
    for node in root.walk():
        if node.name.lower() != "localization":
            continue
        for lang in node.children:
            if lang.name.lower() != LANG_ZH:
                continue
            for key, value, _line in lang.values:
                name = key.strip()
                if not name.startswith("#"):
                    name = "#" + name
                zh[name] = value
    return zh


def is_short(text: str, max_words: int = SHORT_MAX_WORDS, min_len: int = SHORT_MIN_LEN) -> bool:
    if len(text) < min_len:
        return False
    return len(text.split()) <= max_words


def build_rows(
    en: dict,
    zh: dict,
    *,
    include_zh_only: bool = False,
    max_words: int = SHORT_MAX_WORDS,
    min_len: int = SHORT_MIN_LEN,
) -> tuple[list[dict], dict]:
    rows: list[dict] = []
    paired = 0
    short = 0
    zh_only = 0
    for key in zh:
        text = en.get(key)
        if text is None:
            zh_only += 1
            if not include_zh_only:
                continue
            text = ""
        else:
            paired += 1
        flag = bool(text) and is_short(text, max_words, min_len)
        if flag:
            short += 1
        rows.append({"key": key, "en": text, "zh": zh[key], "short": flag})
    en_only = [k for k in en if k not in zh]
    stats = {
        "dictKeys": len(zh),
        "paired": paired,
        "short": short,
        "zhOnly": zh_only,
        "enOnly": len(en_only),
        "rows": len(rows),
    }
    return rows, stats


def write_jsonl(path: str, rows: list[dict]) -> int:
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    body = "\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) for row in rows)
    # 末行不带换行：与既有产物（pool.jsonl / inventory.json）保持一致，sha256 才可比。
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(body)
    return len(body.encode("utf-8"))


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="python -m i18n_tools.glossary",
        description="抽 Squad 官方双语术语表 -> packs/glossary.jsonl",
    )
    p.add_argument("--gamedata", default=None, help="GameData 根目录（默认 UI18N_GAMEDATA / 仓库同级 / 本机安装目录）")
    p.add_argument("--out", default=None, help="输出文件（默认 packs/glossary.jsonl）")
    p.add_argument("--include-zh-only", action="store_true", help="同时导出只有中文、没有英文注释的字典键（en 为空串）")
    p.add_argument("--short-max-words", type=int, default=SHORT_MAX_WORDS, help="short 判据：最大词数（默认 5）")
    p.add_argument("--short-min-len", type=int, default=SHORT_MIN_LEN, help="short 判据：最短字符数（默认 4）")
    p.add_argument("--quiet", action="store_true", help="只输出一行结果")
    return p


def main(argv=None) -> int:
    ensure_stdout()
    args = build_parser().parse_args(argv)
    gamedata = os.path.abspath(args.gamedata or default_gamedata())
    out = os.path.abspath(args.out or default_out())
    dict_path = dictionary_path(gamedata)
    if not os.path.isdir(gamedata):
        print("[ERR] GameData 不存在: %s" % gamedata)
        return 2
    if not os.path.isfile(dict_path):
        print("[ERR] 找不到字典文件: %s" % dict_path)
        return 2

    t0 = time.perf_counter()
    en, en_stats = collect_en(gamedata)
    t1 = time.perf_counter()
    zh = collect_zh(dict_path)
    t2 = time.perf_counter()
    rows, st = build_rows(
        en,
        zh,
        include_zh_only=args.include_zh_only,
        max_words=args.short_max_words,
        min_len=args.short_min_len,
    )
    t3 = time.perf_counter()
    size = write_jsonl(out, rows)
    t4 = time.perf_counter()

    if args.quiet:
        print("[glossary] 行 %d / 配对 %d / short %d -> %s" % (st["rows"], st["paired"], st["short"], out))
        return 0
    print("[glossary] GameData = %s" % gamedata)
    print("[glossary] 字典    %s" % dict_path)
    print("[glossary] Squad cfg 文件 %d，其中有英文注释的 %d 个" % (en_stats["squadCfgFiles"], en_stats["cfgWithComment"]))
    print("[glossary] 注释命中 %d 处 -> 唯一英文键 %d（同键不同英文 %d 处，取首次出现）" % (
          en_stats["commentHits"], en_stats["commentUniqueKeys"], en_stats["commentConflicts"]))
    print("[glossary] 字典键 %d（zh-cn 单语言节点）" % st["dictKeys"])
    print("[glossary] 配对 %d / 只有中文 %d / 只有英文 %d" % (st["paired"], st["zhOnly"], st["enOnly"]))
    print("[glossary] short（英文 <=%d 词且 >=%d 字符）%d" % (args.short_max_words, args.short_min_len, st["short"]))
    print("[glossary] 输出 %d 行 / %d 字节 -> %s" % (st["rows"], size, out))
    print("[glossary] 耗时 英文 %.2fs + 中文 %.2fs + 组装 %.2fs + 落盘 %.2fs = %.2fs" % (
          t1 - t0, t2 - t1, t3 - t2, t4 - t3, t4 - t0))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
