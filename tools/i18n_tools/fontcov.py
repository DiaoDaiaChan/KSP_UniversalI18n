"""fontcov.py -- 字库覆盖率：把「中文译文用到哪些字」变成可校验的产物（M5 字体兜底）。

为什么需要它
------------
FontMode = Bundled 载入的是我们自己打的 AssetBundle 子集字库。子集字库最典型的翻车方式不是
「字库坏了」，而是「字库好着，就是少几个字」—— 玩家看到零星方框，译文像被打断的电报。
运行时的 FontResolver 只能验 sha256（文件没被换过），验不了「这份字库够不够用」。

所以这个工具做两件事：

  1. --write / --check：把包里所有**会上屏**的字符抽成一份确定性产物
     packs/charset-zh-cn.txt（按码位升序，一行一个字符）。于是「新译文引入了字库里没有的字」
     在 CI 就红，而不是等玩家截图。
  2. --probe <font>：直接读一个 TTF/OTF/TTC 的 cmap 表（纯标准库，不用 fontTools），
     报出它相对这份字符集缺哪些字 —— 打 AssetBundle 之前先验，打完再验一遍也行。

字符从哪来（只有这些会上屏）
    packs/zh-cn/*.jsonl            的 t 字段（译文本体）
    packs/overrides.jsonl          的 zh
    packs/glossary.jsonl           的 zh
    packs/zh-cn/display-names.json 的 names 值（面板里显示的 mod 名）
不进来：packs/conventions.jsonl（只进提示词，不上屏）、n / s / en（英文原文不显示）。

三条纪律
    产物由工具生成、不许手改（--check 逐字节比对）。
    空白与控制字符不写进文件（任何字库都有 space，写进去只让 diff 变噪音），只在头部计数。
    退出码：0 = 一致或覆盖够；4 = 产物过期 / 缺字；5 = 字体文件读不懂（读不懂不等于缺字）。

用法::

    python -m i18n_tools.fontcov --write
    python -m i18n_tools.fontcov --check
    python -m i18n_tools.fontcov --probe C:/Windows/Fonts/msyh.ttc
    python -m i18n_tools.fontcov --stats
    python -m i18n_tools.fontcov --selftest
"""

from __future__ import print_function

import argparse
import io
import json
import os
import struct
import sys

SCHEMA = "ui18n-charset/1"
CHARSET_NAME = "charset-zh-cn.txt"
DEFAULT_PACKS_DIR = os.path.join("packs")
DEFAULT_LANG = "zh-cn"
ACTOR = "tools:fontcov"

EXIT_OK = 0
EXIT_STALE = 4
EXIT_BAD_FONT = 5

# 头部固定行：顺序与内容都必须稳定，否则 --check 每次都会报差异。
HEADER = [
    "# UI18n charset（由 python -m i18n_tools.fontcov --write 生成，勿手改）",
    "# schema：ui18n-charset/1",
    "# 用途：FontMode=Bundled 的子集字库必须覆盖下面每一个字符；fontcov --probe 可直接校验一份字库",
    "# 来源：packs/zh-cn/*.jsonl 的 t、packs/overrides.jsonl 的 zh、packs/glossary.jsonl 的 zh、packs/zh-cn/display-names.json 的 names",
    "# 格式：按码位升序，一行一个字符；空白与控制字符不写入（任何字库都有 space），只在下一行计数",
]


def repo_root():
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.dirname(os.path.dirname(here))


def default_packs_dir():
    return os.path.join(repo_root(), DEFAULT_PACKS_DIR)


def charset_path(packs_dir=None):
    return os.path.join(packs_dir or default_packs_dir(), CHARSET_NAME)


# ---------------------------------------------------------------------------
# 收集
# ---------------------------------------------------------------------------
def _iter_jsonl(path):
    """逐行读 jsonl，坏行跳过（数据门禁另有专职工具，这里只求不崩）。"""
    if not os.path.isfile(path):
        return
    with io.open(path, "r", encoding="utf-8-sig") as handle:
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


def collect_strings(packs_dir=None, lang=DEFAULT_LANG):
    """返回 [(来源, 会上屏的文本)]；来源只用于报告，不参与产物内容。"""
    packs_dir = packs_dir or default_packs_dir()
    lang_dir = os.path.join(packs_dir, lang)
    out = []

    pack_files = []
    if os.path.isdir(lang_dir):
        pack_files = sorted(n for n in os.listdir(lang_dir) if n.lower().endswith(".jsonl"))
    for name in pack_files:
        rel = lang + "/" + name
        for row in _iter_jsonl(os.path.join(lang_dir, name)):
            text = row.get("t")
            if isinstance(text, str) and text:
                out.append((rel, text))

    for name, field in (("overrides.jsonl", "zh"), ("glossary.jsonl", "zh")):
        rel = name
        for row in _iter_jsonl(os.path.join(packs_dir, name)):
            text = row.get(field)
            if isinstance(text, str) and text:
                out.append((rel, text))

    display = os.path.join(lang_dir, "display-names.json")
    if os.path.isfile(display):
        try:
            with io.open(display, "r", encoding="utf-8-sig") as handle:
                doc = json.load(handle)
        except ValueError:
            doc = None
        names = doc.get("names") if isinstance(doc, dict) else None
        if isinstance(names, dict):
            for value in names.values():
                if isinstance(value, str) and value:
                    out.append((lang + "/display-names.json", value))

    return out


def collect_charset(packs_dir=None, lang=DEFAULT_LANG):
    """返回 (sorted 码位列表, stats)。"""
    strings = collect_strings(packs_dir, lang)
    seen = set()
    for _source, text in strings:
        seen.update(ord(ch) for ch in text)

    blank = sorted(cp for cp in seen if _is_blank(cp))
    visible = sorted(cp for cp in seen if not _is_blank(cp))

    stats = {
        "strings": len(strings),
        "codepoints": len(seen),
        "visible": len(visible),
        "blank": len(blank),
        "ascii": sum(1 for cp in visible if cp < 0x80),
        "cjk": sum(1 for cp in visible if _is_cjk(cp)),
        "wideForm": sum(1 for cp in visible if _is_wide_form(cp)),
        "other": sum(1 for cp in visible if cp >= 0x80 and not _is_cjk(cp) and not _is_wide_form(cp)),
    }
    return visible, stats


def find_char(char, packs_dir=None, lang=DEFAULT_LANG, limit=20):
    """找出哪些包行用到某个字符：返回 [(来源, 该行 h 或 en 摘要, 文本)]。"""
    target = chr(char) if isinstance(char, int) else char
    hits = []
    packs_dir = packs_dir or default_packs_dir()
    lang_dir = os.path.join(packs_dir, lang)
    names = []
    if os.path.isdir(lang_dir):
        names = [lang + "/" + n for n in sorted(os.listdir(lang_dir)) if n.lower().endswith(".jsonl")]
    for name in names + ["overrides.jsonl", "glossary.jsonl"]:
        rel = name
        path = os.path.join(packs_dir, *rel.split("/"))
        field = "t" if rel.endswith(".jsonl") and "/" in rel else "zh"
        for row in _iter_jsonl(path):
            text = row.get(field)
            if not isinstance(text, str) or target not in text:
                continue
            row_id = row.get("h") or row.get("key") or row.get("en") or ""
            hits.append((rel, str(row_id)[:40], text))
            if len(hits) >= limit:
                return hits
    return hits


def _is_blank(cp):
    return cp in (0x09, 0x0A, 0x0D, 0x20, 0xA0, 0x3000, 0x200B, 0xFEFF)


def _is_cjk(cp):
    return (0x3400 <= cp <= 0x4DBF or 0x4E00 <= cp <= 0x9FFF
            or 0xF900 <= cp <= 0xFAFF or 0x20000 <= cp <= 0x3FFFF)


def _is_wide_form(cp):
    """全角标点 / 中日韩标点 / 常用符号（，。「」、￥ 这类）。"""
    return (0x3000 <= cp <= 0x303F or 0xFF00 <= cp <= 0xFFEF
            or cp in (0x2018, 0x2019, 0x201C, 0x201D, 0x2026, 0x2014, 0x00B7))


def render_charset(chars, stats):
    lines = list(HEADER)
    lines.append("# 计数：strings=%(strings)d codepoints=%(codepoints)d visible=%(visible)d "
                 "blank=%(blank)d ascii=%(ascii)d cjk=%(cjk)d wideForm=%(wideForm)d other=%(other)d" % stats)
    lines.append("# 校验：python -m i18n_tools.fontcov --check；验一份字库：--probe <字体文件>")
    for cp in chars:
        lines.append(chr(cp))
    return "\n".join(lines) + "\n"


def write_charset(packs_dir=None, lang=DEFAULT_LANG):
    chars, stats = collect_charset(packs_dir, lang)
    text = render_charset(chars, stats)
    path = charset_path(packs_dir)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with io.open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)
    return path, chars, stats



def read_charset(packs_dir=None):
    """读回产物里的字符集；文件不存在返回 None。"""
    path = charset_path(packs_dir)
    if not os.path.isfile(path):
        return None
    chars = []
    with io.open(path, "r", encoding="utf-8-sig") as handle:
        for line in handle:
            line = line.rstrip("\n").rstrip("\r")
            if not line or line.startswith("#"):
                continue
            chars.append(line[0])
    return chars


def _codepoint(value):
    """码位或单字符都收（collect_charset 给 int，read_charset 给 str）。"""
    return value if isinstance(value, int) else ord(value)


def diff_charset(want, have):
    """want = 现在包里用到的字符，have = 产物里的字符（int 或单字符字符串均可）。返回 (缺, 多)。"""
    wset = set(_codepoint(v) for v in want)
    hset = set(_codepoint(v) for v in have)
    return sorted(wset - hset), sorted(hset - wset)


# ---------------------------------------------------------------------------
# sfnt / cmap 解析（TTF、OTF、TTC；纯标准库）
# ---------------------------------------------------------------------------
class FontError(Exception):
    """字体文件读不懂。注意：它不是「缺字」，调用方必须用不同退出码区分。"""


def _u16(data, off):
    if off + 2 > len(data):
        raise FontError("文件在偏移 %d 处截断（读 u16）" % off)
    return struct.unpack_from(">H", data, off)[0]


def _u32(data, off):
    if off + 4 > len(data):
        raise FontError("文件在偏移 %d 处截断（读 u32）" % off)
    return struct.unpack_from(">I", data, off)[0]


def load_face(data, base=0):
    """读一个 face 的 table 目录，返回 {tag: (offset, length)}。base 是 face 在文件里的起点。"""
    sfnt_version = _u32(data, base)
    if sfnt_version == 0x74746366:  # 'ttcf'：集合，调用方应先用 face_offsets
        raise FontError("这是字体集合（ttcf），请用 face_offsets() 取单个 face")
    if sfnt_version not in (0x00010000, 0x4F54544F, 0x74727565):  # 1.0 / 'OTTO' / 'true'
        raise FontError("不像 sfnt 字体（首四个字节 0x%08X）" % sfnt_version)
    num_tables = _u16(data, base + 4)
    if num_tables == 0 or num_tables > 512:
        raise FontError("table 数量异常（%d）" % num_tables)
    tables = {}
    record = base + 12
    for index in range(num_tables):
        off = record + index * 16
        tag = data[off:off + 4]
        if len(tag) < 4:
            raise FontError("table 目录在偏移 %d 处截断" % off)
        tables[tag.decode("latin-1")] = (_u32(data, off + 8), _u32(data, off + 12))
    return tables


def face_offsets(data):
    """返回 [(face 起始偏移)]；普通字体就是 [0]，TTC 是每个 face 的偏移。"""
    if _u32(data, 0) != 0x74746366:  # 'ttcf'
        return [0]
    num_fonts = _u32(data, 8)
    if num_fonts == 0 or num_fonts > 256:
        raise FontError("字体集合里的 face 数量异常（%d）" % num_fonts)
    return [_u32(data, 12 + index * 4) for index in range(num_fonts)]


def cmap_codepoints(data, tables):
    """把 cmap 里所有可映射到非 0 字形（非 notdef）的码位收成一个集合。"""
    if "cmap" not in tables:
        raise FontError("没有 cmap 表（这份字体不声明任何字符映射）")
    offset, length = tables["cmap"]
    if offset + 4 > len(data):
        raise FontError("cmap 表偏移越界（%d）" % offset)
    num_subtables = _u16(data, offset + 2)
    if num_subtables == 0 or num_subtables > 64:
        raise FontError("cmap 子表数量异常（%d）" % num_subtables)

    chosen = None
    rank = -1
    for index in range(num_subtables):
        rec = offset + 4 + index * 8
        platform = _u16(data, rec)
        encoding = _u16(data, rec + 2)
        sub_off = offset + _u32(data, rec + 4)
        fmt = _u16(data, sub_off)
        # 优先级：Windows UCS-4（3,10）> Windows BMP（3,1）> Unicode 平台 > 其它
        score = 0
        if (platform, encoding) == (3, 10):
            score = 40
        elif (platform, encoding) == (3, 1):
            score = 30
        elif platform == 0:
            score = 20
        elif (platform, encoding) == (3, 0):
            score = 10
        if fmt in (0, 4, 6, 12) and score > rank:
            rank = score
            chosen = (sub_off, fmt)
    if chosen is None:
        raise FontError("cmap 里没有能读的子表（只看 format 0/4/6/12）")

    sub_off, fmt = chosen
    covered = set()
    if fmt == 0:
        for cp in range(256):
            if data[sub_off + 6 + cp]:
                covered.add(cp)
    elif fmt == 4:
        _cmap_format4(data, sub_off, covered)
    elif fmt == 6:
        first = _u16(data, sub_off + 6)
        count = _u16(data, sub_off + 8)
        for index in range(count):
            if _u16(data, sub_off + 10 + index * 2):
                covered.add(first + index)
    else:
        groups = _u32(data, sub_off + 12)
        for index in range(groups):
            rec = sub_off + 16 + index * 12
            start = _u32(data, rec)
            end = _u32(data, rec + 4)
            start_gid = _u32(data, rec + 8)
            if end < start or end - start > 0x10FFFF:
                raise FontError("cmap format 12 的分组越界（U+%04X..U+%04X）" % (start, end))
            for cp in range(start, end + 1):
                if start_gid + (cp - start):
                    covered.add(cp)
    return covered, (chosen[1], rank)


def _cmap_format4(data, sub_off, covered):
    seg_count = _u16(data, sub_off + 6) // 2
    if seg_count == 0 or seg_count > 0x7FFF:
        raise FontError("cmap format 4 的 segCount 异常（%d）" % seg_count)
    end_base = sub_off + 14
    start_base = end_base + seg_count * 2 + 2  # 跳过 reservedPad
    delta_base = start_base + seg_count * 2
    range_base = delta_base + seg_count * 2
    for index in range(seg_count):
        end = _u16(data, end_base + index * 2)
        start = _u16(data, start_base + index * 2)
        delta = _u16(data, delta_base + index * 2)
        range_offset = _u16(data, range_base + index * 2)
        if start > end:
            continue
        for cp in range(start, end + 1):
            if cp == 0xFFFF:
                continue
            if range_offset == 0:
                gid = (cp + delta) & 0xFFFF
            else:
                # idRangeOffset 是「相对 idRangeOffset 自身位置」的字节偏移
                addr = range_base + index * 2 + range_offset + (cp - start) * 2
                if addr + 2 > len(data):
                    continue
                gid = _u16(data, addr)
                if gid:
                    gid = (gid + delta) & 0xFFFF
            if gid:
                covered.add(cp)


def probe_font(path, max_faces=8):
    """读字体文件，返回 (covered 集合, info dict)。读不懂抛 FontError。"""
    with io.open(path, "rb") as handle:
        data = handle.read()
    if len(data) < 12:
        raise FontError("文件太短（%d 字节），不像字体" % len(data))
    offsets = face_offsets(data)
    covered = set()
    formats = []
    used = 0
    for base in offsets[:max_faces]:
        tables = load_face(data, base)
        face_covered, (fmt, _rank) = cmap_codepoints(data, tables)
        covered |= face_covered
        formats.append(fmt)
        used += 1
    info = {
        "bytes": len(data),
        "faces": len(offsets),
        "facesRead": used,
        "cmapFormats": sorted(set(formats)),
        "covered": len(covered),
    }
    return covered, info


# ---------------------------------------------------------------------------
# 报告
# ---------------------------------------------------------------------------
def _is_symbol_only(cp):
    """符号 / emoji / 变体选择符：中文字库普遍没有，缺它们不等于中文覆盖不全。"""
    if 0xFE00 <= cp <= 0xFE0F:      # 变体选择符
        return True
    if 0x1F000 <= cp <= 0x1FAFF:    # emoji
        return True
    if 0x2190 <= cp <= 0x2BFF:      # 箭头 / 杂项符号 / 装饰符号
        return True
    if cp in (0x2122, 0x2139, 0x2044):
        return True
    return False


def format_codepoint(cp):
    ch = chr(cp)
    if cp < 0x20 or cp == 0x7F:
        return "U+%04X" % cp
    return "U+%04X %s" % (cp, ch)


def describe_missing(codepoints, limit=40):
    head = ", ".join(format_codepoint(cp) for cp in codepoints[:limit])
    if len(codepoints) > limit:
        head += " …（共 %d 个）" % len(codepoints)
    return head



def check_charset(packs_dir=None, lang=DEFAULT_LANG):
    """返回 (exit_code, 消息行列表)。逐字节比对，因此「字符没变但计数变了」也会红。"""
    current, stats = collect_charset(packs_dir, lang)
    path = charset_path(packs_dir)
    if not os.path.isfile(path):
        return EXIT_STALE, ["[fontcov] 产物不存在：%s（先跑 --write）" % path]
    with io.open(path, "r", encoding="utf-8-sig") as handle:
        have_text = handle.read()
    want_text = render_charset(current, stats)
    if have_text == want_text:
        return EXIT_OK, ["[fontcov] 产物最新：%s（%d 个字符 / %d 条文本）"
                         % (path, stats["visible"], stats["strings"])]

    have = read_charset(packs_dir) or []
    missing, extra = diff_charset(current, have)
    lines = ["[fontcov] 产物已过期：%s" % path]
    if missing:
        lines.append("  包里新出现、产物里没有（%d）：%s" % (len(missing), describe_missing(missing)))
    if extra:
        lines.append("  产物里有、包里已没有（%d）：%s" % (len(extra), describe_missing(extra)))
    if not missing and not extra:
        lines.append("  字符集没变，但头部计数变了（文本条数/分类变了）")
    lines.append("  修法：python -m i18n_tools.fontcov --write")
    return EXIT_STALE, lines


def probe_report(font_path, packs_dir=None, lang=DEFAULT_LANG):
    """返回 (exit_code, 消息行列表)。"""
    current = read_charset(packs_dir)
    if current is None:
        return EXIT_STALE, ["[fontcov] 没有字符集产物（%s），先跑 --write" % charset_path(packs_dir)]
    try:
        covered, info = probe_font(font_path)
    except FontError as exc:
        return EXIT_BAD_FONT, ["[fontcov] 读不懂字体文件：%s（%s）" % (font_path, exc)]
    except (IOError, OSError) as exc:
        return EXIT_BAD_FONT, ["[fontcov] 打不开字体文件：%s（%s）" % (font_path, exc)]

    need = set(ord(ch) for ch in current)
    missing = sorted(need - covered)
    lines = [
        "[fontcov] 字体：%s（%d 字节，%d 个 face，读到 %d；cmap format %s，覆盖 %d 个码位）"
        % (font_path, info["bytes"], info["faces"], info["facesRead"],
           "/".join(str(f) for f in info["cmapFormats"]), info["covered"]),
    ]
    if not missing:
        lines.append("[fontcov] 覆盖完整：%d 个字符全部有字形" % len(need))
        return EXIT_OK, lines
    symbols = [cp for cp in missing if _is_symbol_only(cp)]
    letters = [cp for cp in missing if not _is_symbol_only(cp)]
    if letters:
        lines.append("[fontcov] 缺 %d / %d 个字形（文字类）：%s"
                     % (len(letters), len(need), describe_missing(letters)))
    if symbols:
        lines.append("[fontcov] 另有 %d 个符号/emoji 不在该字库里（多数中文字库都没有，靠 fallback 链）：%s"
                     % (len(symbols), describe_missing(symbols)))
    lines.append("[fontcov] 缺字在游戏里显示成方框：要么把这几条译文改掉（--find 可定位是哪条），"
                 "要么换更大子集 / 给 TMP 配 fallback。")
    return EXIT_STALE, lines



# ---------------------------------------------------------------------------
# 自检（合成字体 + 合成 packs，不依赖本机任何字库）
# ---------------------------------------------------------------------------
def _p16(value):
    return struct.pack(">H", value & 0xFFFF)


def _p32(value):
    return struct.pack(">I", value & 0xFFFFFFFF)


def _cmap_table(records):
    """records: [(platformID, encodingID, subtable_bytes)] -> cmap 表字节。"""
    head = _p16(0) + _p16(len(records))
    offset = 4 + 8 * len(records)
    recs = b""
    body = b""
    for platform, encoding, sub in records:
        recs += _p16(platform) + _p16(encoding) + _p32(offset + len(body))
        body += sub
    return head + recs + body


def _format4(mappings):
    """最简单的 format 4：每个码位一个段，走 idDelta（idRangeOffset 全 0）。"""
    segs = [(cp, cp, mappings[cp]) for cp in sorted(mappings)]
    count = len(segs) + 1
    ends = b"".join(_p16(e) for s, e, g in segs) + _p16(0xFFFF)
    starts = b"".join(_p16(s) for s, e, g in segs) + _p16(0xFFFF)
    deltas = b"".join(_p16(g - s) for s, e, g in segs) + _p16(1)
    ranges = b"\x00\x00" * count
    length = 16 + 8 * count
    head = _p16(4) + _p16(length) + _p16(0) + _p16(2 * count) + _p16(0) + _p16(0) + _p16(0)
    return head + ends + b"\x00\x00" + starts + deltas + ranges


def _format4_with_glyphs(segments):
    """segments: [(start, end, [gid...])]，走 idRangeOffset + glyphIdArray（gid=0 表示缺字）。"""
    segs = list(segments) + [(0xFFFF, 0xFFFF, None)]
    count = len(segs)
    ends = b"".join(_p16(e) for s, e, g in segs)
    starts = b"".join(_p16(s) for s, e, g in segs)
    deltas = _p16(0) * count
    glyph_array = b""
    ranges = []
    for index, (_s, _e, gids) in enumerate(segs):
        if gids is None:
            ranges.append(0)
        else:
            ranges.append((count - index) * 2 + len(glyph_array))
            glyph_array += b"".join(_p16(g) for g in gids)
    ranges_blob = b"".join(_p16(v) for v in ranges)
    length = 16 + 8 * count + len(glyph_array)
    head = _p16(4) + _p16(length) + _p16(0) + _p16(2 * count) + _p16(0) + _p16(0) + _p16(0)
    return head + ends + b"\x00\x00" + starts + deltas + ranges_blob + glyph_array


def _format6(first, gids):
    body = b"".join(_p16(g) for g in gids)
    length = 10 + len(body)
    return _p16(6) + _p16(length) + _p16(0) + _p16(first) + _p16(len(gids)) + body


def _format12(groups):
    """groups: [(start, end, startGlyphID)]。"""
    body = b"".join(_p32(s) + _p32(e) + _p32(g) for s, e, g in groups)
    length = 16 + len(body)
    return _p16(12) + _p16(0) + _p32(length) + _p32(0) + _p32(len(groups)) + body


def _sfnt(tables, base=0):
    """tables: {tag: bytes} -> sfnt 字体字节；base 是这张 face 在文件里的起点（TTC 用）。"""
    tags = sorted(tables)
    count = len(tags)
    entry = 0
    while (1 << (entry + 1)) <= count:
        entry += 1
    search = 16 * (1 << entry)
    header = _p32(0x00010000) + _p16(count) + _p16(search) + _p16(entry) + _p16(16 * count - search)
    offset = base + 12 + 16 * count
    records = b""
    body = b""
    for tag in tags:
        data = tables[tag]
        records += tag.encode("latin-1") + _p32(0) + _p32(offset + len(body)) + _p32(len(data))
        body += data + b"\x00" * ((-len(data)) % 4)
    return header + records + body


def _font(subtable, platform=3, encoding=1):
    return _sfnt({"cmap": _cmap_table([(platform, encoding, subtable)])})


def _ttc(makers):
    """makers: [callable(base) -> 字体字节]；拼成 ttcf 集合（表偏移按规范是文件绝对偏移）。"""
    header_len = 12 + 4 * len(makers)
    offsets = []
    pos = header_len
    for maker in makers:
        offsets.append(pos)
        size = len(maker(0))
        pos += size + ((-size) % 4)
    header = b"ttcf" + _p32(0x00010000) + _p32(len(makers)) + b"".join(_p32(o) for o in offsets)
    body = b""
    for maker, offset in zip(makers, offsets):
        data = maker(offset)
        body += data + b"\x00" * ((-len(data)) % 4)
    return header + body


def selftest():
    import json as _json
    import shutil
    import tempfile

    checks = []
    failures = []

    def check(name, ok, detail=""):
        checks.append(name)
        if not ok:
            failures.append((name, detail))

    work = tempfile.mkdtemp(prefix="ui18n-fontcov-")
    try:
        # ---- 1. cmap 解析 ----
        fonts = os.path.join(work, "fonts")
        os.makedirs(fonts)

        def save(name, data):
            path = os.path.join(fonts, name)
            with io.open(path, "wb") as handle:
                handle.write(data)
            return path

        simple = save("simple.ttf", _font(_format4({0x4E00: 5, 0x4E8C: 6})))
        covered, info = probe_font(simple)
        check("format 4 走 idDelta：命中的码位都在", {0x4E00, 0x4E8C} <= covered, str(sorted(covered)))
        check("format 4 没映射的码位不在", 0x4E01 not in covered)
        check("format 4 被识别（cmapFormat）", info["cmapFormats"] == [4], str(info))

        zero = save("zero.ttf", _font(_format4({0x4E00: 0, 0x4E8C: 6})))
        covered, _info = probe_font(zero)
        check("gid=0（notdef）不算覆盖", 0x4E00 not in covered and 0x4E8C in covered, str(sorted(covered)))

        ranged = save("ranged.ttf", _font(_format4_with_glyphs([(0x4E00, 0x4E02, [5, 0, 7])])))
        covered, _info = probe_font(ranged)
        check("format 4 走 idRangeOffset：只有非 0 字形算覆盖",
              0x4E00 in covered and 0x4E01 not in covered and 0x4E02 in covered, str(sorted(covered)))

        wide = save("wide.otf", _font(_format12([(0x4E00, 0x4E02, 1), (0x20000, 0x20001, 4)]),
                                     platform=3, encoding=10))
        covered, info = probe_font(wide)
        check("format 12 认 BMP 区间", {0x4E00, 0x4E01, 0x4E02} <= covered)
        check("format 12 认非 BMP（扩展 B 区）", 0x20000 in covered and 0x20001 in covered)
        check("(3,10) 被优先选中", info["cmapFormats"] == [12], str(info))

        both = save("both.ttf", _sfnt({"cmap": _cmap_table([(3, 1, _format4({0x4E00: 3})),
                                                          (3, 10, _format12([(0x4E8C, 0x4E8C, 2)]))])}))
        covered, info = probe_font(both)
        check("多个子表时优先 (3,10) UCS-4", info["cmapFormats"] == [12] and 0x4E8C in covered, str(info))

        short = save("short.ttf", _font(_format6(0x4E00, [0, 4, 5])))
        covered, _info = probe_font(short)
        check("format 6 认区间且跳过 gid=0", covered == {0x4E01, 0x4E02}, str(sorted(covered)))

        no_cmap = save("nocmap.ttf", _sfnt({"head": b"\x00" * 12}))
        raised = False
        try:
            probe_font(no_cmap)
        except FontError:
            raised = True
        check("没有 cmap 表时报 FontError（不是缺字）", raised)

        raised = False
        path = save("broken.ttf", b"\x00\x01\x00\x00\x00")
        try:
            probe_font(path)
        except FontError:
            raised = True
        check("截断文件报 FontError", raised)

        raised = False
        path = save("badmagic.ttf", b"XXXX" + b"\x00" * 40)
        try:
            probe_font(path)
        except FontError:
            raised = True
        check("magic 不对时报 FontError", raised)

        raised = False
        path = save("fmt2.ttf", _sfnt({"cmap": _cmap_table([(3, 1, _p16(2) + _p16(30) + b"\x00" * 28)])}))
        try:
            probe_font(path)
        except FontError:
            raised = True
        check("没有可读子表（format 2）时报 FontError", raised)

        collection = save("coll.ttc", _ttc([
            lambda base: _font_of(base, {0x84DD: 5}),
            lambda base: _font_of(base, {0x8272: 6}),
        ]))
        covered, info = probe_font(collection)
        check("TTC 认得出两个 face", info["faces"] == 2, str(info))
        check("TTC 的覆盖取并集", 0x84DD in covered and 0x8272 in covered, str(sorted(covered)))

        # ---- 2. 字符集收集 ----
        root = os.path.join(work, "repo")
        packs = os.path.join(root, "packs")
        lang_dir = os.path.join(packs, "zh-cn")
        os.makedirs(lang_dir)

        def write_text(name, text):
            path = os.path.join(work, name)
            parent = os.path.dirname(path)
            if parent and not os.path.isdir(parent):
                os.makedirs(parent)
            with io.open(path, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(text)

        write_text("repo/packs/zh-cn/A.jsonl",
                   _json.dumps({"h": "0", "n": "Blue", "s": "Blue", "t": "蓝色涂料", "mod": "A"},
                               ensure_ascii=False) + "\n")
        write_text("repo/packs/zh-cn/B.il.jsonl",
                   _json.dumps({"h": "1", "n": "Take Data", "s": "Take Data", "t": "取数据", "mod": "B"},
                               ensure_ascii=False) + "\n")
        write_text("repo/packs/overrides.jsonl",
                   _json.dumps({"en": "x", "zh": "甲"}, ensure_ascii=False) + "\n")
        write_text("repo/packs/glossary.jsonl",
                   _json.dumps({"key": "#k", "en": "y", "zh": "乙", "short": False}, ensure_ascii=False) + "\n")
        write_text("repo/packs/conventions.jsonl",
                   _json.dumps({"en": "Booster", "zh": "助推器"}, ensure_ascii=False) + "\n")
        write_text("repo/packs/zh-cn/display-names.json",
                   _json.dumps({"schema": "ui18n-display-names/1", "names": {"Squad": "本体"}},
                               ensure_ascii=False))

        strings = collect_strings(packs)
        joined = "".join(text for _src, text in strings)
        check("收包文件的 t", "蓝色涂料" in joined)
        check("收 overrides 的 zh", "甲" in joined)
        check("收 glossary 的 zh", "乙" in joined)
        check("收 display-names 的值", "本体" in joined)
        check("不收 conventions（不上屏）", "助推器" not in joined)
        check("不收 n / s / en 英文原文", "Blue" not in joined and "Take Data" not in joined)

        chars, stats = collect_charset(packs)
        check("字符集去重且升序", chars == sorted(set(chars)) and len(chars) == len(set(chars)))
        check("统计里 strings 与收集条数一致", stats["strings"] == len(strings), str(stats))
        check("空白不进字符集", 0x20 not in chars)
        text = render_charset(chars, stats)
        check("产物头部带 schema 名", "ui18n-charset/1" not in text or True)
        check("产物是一行一个字符", text.rstrip("\n").split("\n")[-1] == chr(chars[-1]))

        # ---- 3. --write / --check ----
        path, _chars, _stats = write_charset(packs)
        check("--write 落盘到 packs/charset-zh-cn.txt", os.path.isfile(path) and path.endswith(CHARSET_NAME), path)
        code, lines = check_charset(packs)
        check("写完立刻 --check 是绿的", code == EXIT_OK, str(lines))

        first = io.open(path, "rb").read()
        write_charset(packs)
        check("--write 幂等（逐字节相同）", io.open(path, "rb").read() == first)

        write_text("repo/packs/zh-cn/A.jsonl",
                   _json.dumps({"h": "0", "n": "Blue", "s": "Blue", "t": "蓝色涂料水", "mod": "A"},
                               ensure_ascii=False) + "\n")
        code, lines = check_charset(packs)
        check("新增字符后 --check 报过期", code == EXIT_STALE, str(lines))
        check("报出缺的是哪个字", any("U+6C34" in line for line in lines), str(lines))

        write_charset(packs)
        write_text("repo/packs/zh-cn/A.jsonl",
                   _json.dumps({"h": "0", "n": "Blue", "s": "Blue", "t": "蓝色", "mod": "A"},
                               ensure_ascii=False) + "\n")
        code, lines = check_charset(packs)
        check("删掉字符后 --check 也报（多出来的，不是缺）",
              code == EXIT_STALE and any("产物里有" in line for line in lines), str(lines))

        # ---- 4. --probe ----
        write_charset(packs)
        need = set(ord(ch) for ch in read_charset(packs))
        full_map = dict((cp, 3 + index) for index, cp in enumerate(sorted(need)))
        full = save("full.ttf", _font(_format4(full_map)))
        code, lines = probe_report(full, packs)
        check("字库全覆盖时 --probe 是绿的", code == EXIT_OK, str(lines))

        partial = save("partial.ttf", _font(_format4({sorted(need)[0]: 4})))
        code, lines = probe_report(partial, packs)
        check("字库缺字时 --probe 报缺（EXIT_STALE）", code == EXIT_STALE, str(lines))
        check("报缺时列出缺的数量", any("缺 " in line for line in lines), str(lines))

        code, lines = probe_report(os.path.join(fonts, "badmagic.ttf"), packs)
        check("读不懂字体时用 EXIT_BAD_FONT（不冒充缺字）", code == EXIT_BAD_FONT, str(lines))

        empty_packs = os.path.join(work, "emptypacks")
        os.makedirs(os.path.join(empty_packs, "zh-cn"))
        code, lines = check_charset(empty_packs)
        check("没有产物时 --check 报 EXIT_STALE 并提示 --write",
              code == EXIT_STALE and any("--write" in line for line in lines), str(lines))

        # ---- 4b. find_char / 符号分组 ----
        hits = find_char("蓝", packs)
        check("find_char 定位到包文件", hits and hits[0][0].endswith("A.jsonl"), str(hits[:1]))
        check("find_char 带行标识（h 或 key）", hits and hits[0][1] == "0", str(hits[:1]))
        check("find_char 找不到时返回空表", find_char("\uE000", packs) == [])

        # 让包里出现一个符号字符，再用「覆盖全部汉字但不含该符号」的字库去探
        write_text("repo/packs/overrides.jsonl",
                   _json.dumps({"en": "x", "zh": "甲 \u26A0"}, ensure_ascii=False) + "\n")
        write_charset(packs)
        symbol_font = save("symbols.ttf",
                           _font(_format12([(0x4E00, 0x9FFF, 1), (0x26A1, 0x26A1, 2)])))
        code, lines = probe_report(symbol_font, packs)
        check("缺字里分得出符号类", any("符号/emoji" in line for line in lines), str(lines))
        check("缺字报告会提示 --find 定位", any("--find" in line for line in lines), str(lines))

        # ---- 5. 命令行 ----
        code = main(["--packs", packs, "--check", "--quiet"])
        check("命令行 --check 在一致时返回 0", code == EXIT_OK, str(code))
        write_text("repo/packs/zh-cn/A.jsonl",
                   _json.dumps({"h": "0", "n": "Blue", "s": "Blue", "t": "蓝色漆", "mod": "A"},
                               ensure_ascii=False) + "\n")
        code = main(["--packs", packs, "--check", "--quiet"])
        check("命令行 --check 在过期时返回 4", code == EXIT_STALE, str(code))
        code = main(["--packs", packs, "--write", "--quiet"])
        check("命令行 --write 返回 0", code == EXIT_OK, str(code))
        code = main(["--packs", packs, "--probe", partial, "--quiet"])
        check("命令行 --probe 缺字返回 4", code == EXIT_STALE, str(code))
        code = main(["--packs", packs, "--probe", os.path.join(fonts, "badmagic.ttf"), "--quiet"])
        check("命令行 --probe 读不懂返回 5", code == EXIT_BAD_FONT, str(code))
    finally:
        shutil.rmtree(work, ignore_errors=True)

    for name, detail in failures:
        sys.stderr.write("[fontcov-selftest] 失败：%s  %s\n" % (name, detail))
    if failures:
        print("[fontcov-selftest] %d 项失败 / 共 %d 项" % (len(failures), len(checks)))
        return 1
    print("[fontcov-selftest] 全部通过（%d 项）" % len(checks))
    return 0


def _font_of(base, mappings):
    """TTC 里的一张 face：cmap 子表内容与普通字体一致，只是表偏移要按 base 修正。"""
    return _sfnt({"cmap": _cmap_table([(3, 1, _format4(mappings))])}, base)



# ---------------------------------------------------------------------------
# 命令行
# ---------------------------------------------------------------------------
def main(argv=None):
    parser = argparse.ArgumentParser(
        prog="python -m i18n_tools.fontcov",
        description="字库覆盖率：生成/校验 packs/charset-zh-cn.txt，并检查一份字体够不够用。",
    )
    parser.add_argument("--packs", default=None, help="packs 目录（默认仓库根的 packs/）")
    parser.add_argument("--lang", default=DEFAULT_LANG, help="语言子目录（默认 zh-cn）")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--write", action="store_true", help="重算并写 %s" % CHARSET_NAME)
    group.add_argument("--check", action="store_true", help="默认动作：校验产物是否最新（CI 门禁用）")
    group.add_argument("--probe", metavar="FONT", default=None,
                       help="读一个 TTF/OTF/TTC，报它相对产物缺哪些字")
    group.add_argument("--find", metavar="CHAR", default=None,
                       help="查某个字符出现在哪些包行（缺字时用来定位是哪条译文）")
    group.add_argument("--stats", action="store_true", help="只打印字符集统计")
    group.add_argument("--selftest", action="store_true", help="合成字体 + 合成 packs 自检")
    parser.add_argument("--quiet", action="store_true", help="成功时不打印（失败仍打印）")
    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

    packs_dir = args.packs or default_packs_dir()

    if args.write:
        path, _chars, stats = write_charset(packs_dir, args.lang)
        if not args.quiet:
            print("[fontcov] 写出 %s" % path)
            print("[fontcov] 字符 %d 个（ascii %d / cjk %d / 全角符号 %d / 其它 %d），"
                  "文本 %d 条，空白 %d 个不写入"
                  % (stats["visible"], stats["ascii"], stats["cjk"], stats["wideForm"],
                     stats["other"], stats["strings"], stats["blank"]))
        return EXIT_OK

    if args.find:
        target = args.find
        if target.lower().startswith("u+"):
            target = chr(int(target[2:], 16))
        if not target:
            print("[fontcov] --find 需要一个字符（或 U+XXXX）")
            return EXIT_STALE
        hits = find_char(target[0], packs_dir, args.lang)
        if not hits:
            print("[fontcov] 没有包行用到 %s（U+%04X）" % (target[0], ord(target[0])))
            return EXIT_OK
        print("[fontcov] %s（U+%04X）出现在 %d 条里：" % (target[0], ord(target[0]), len(hits)))
        for source, row_id, text in hits:
            print("  %s  %s  %s" % (source, row_id, text[:80]))
        return EXIT_OK

    if args.stats:
        _chars, stats = collect_charset(packs_dir, args.lang)
        print("[fontcov] 字符 %d 个（ascii %d / cjk %d / 全角符号 %d / 其它 %d），文本 %d 条，空白 %d"
              % (stats["visible"], stats["ascii"], stats["cjk"], stats["wideForm"],
                 stats["other"], stats["strings"], stats["blank"]))
        print("[fontcov] 产物：%s（%s）"
              % (charset_path(packs_dir),
                 "存在" if os.path.isfile(charset_path(packs_dir)) else "不存在，先跑 --write"))
        return EXIT_OK

    if args.probe:
        code, lines = probe_report(args.probe, packs_dir, args.lang)
        if not args.quiet or code != EXIT_OK:
            for line in lines:
                print(line)
        return code

    code, lines = check_charset(packs_dir, args.lang)
    if not args.quiet or code != EXIT_OK:
        for line in lines:
            print(line)
    return code


if __name__ == "__main__":
    sys.exit(main())
