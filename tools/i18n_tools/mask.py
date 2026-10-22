"""P3 遮蔽（04 §5）：把"看起来像代码"的片段换成哨兵 <<M0>>，译完再还原。

两类处理（**不要混**，04 §5.2 / 决策 D18）：

  A 类 = KSP 占位符 "<<" n ">>"、TMP 富文本标签 <b> <color=...>、printf "%s" 与 "{0}"
          → **不遮蔽，只校验**（模型必须看见它们才能排中文语序），靠 G2/G3/G4 门禁兜底。
  B 类 = URL / 文件路径 / 字面转义序列 (\\ n \\ t ...) / 反引号代码 / 正则片段 /
          类名与配置键（ModuleEnginesFX、BurstPQS）/ <模板变量> / 型号代码（CH-47）
          → **遮蔽**，让模型根本看不见原文，杜绝"顺手美化"（改大小写、把 / 换成全角）。

哨兵形状 <<M0>> 的三个理由（04 §5.3）：与 KSP 自己的 "<<" n ">>" 同形状（实测保留率 100%）、
M 前缀避开真占位符、纯 ASCII 不被 NFKC 改动、一条正则就能清点。

还原约定（04 §5.4）：转义序列这类"粘连片段"遮蔽时**补一个空格**（a\\nb → a <<M0>> b），
还原时空格连同哨兵一起换回，否则"第一行<<M0>>第二行"会粘成一张脸。

不变式：restore(mask(x)) == x，对全池 12,792 条必须成立（translate.py --selftest 验）。

遮蔽宁可少、不可多（04 §5.5）：每个哨兵都是模型有一次机会抄错的成本，
所以 _is_code_ident 的取向是"普通英文单词绝不遮蔽"，型号代码单列一条精确规则。
"""

from __future__ import annotations

import re

#: 哨兵正则（04 §5.3：一条正则清点全部哨兵）
SENTINEL_RE = re.compile(r"<<M(\d+)>>")

#: KSP 占位符（A 类，不遮蔽）
PLACEHOLDER_RE = re.compile(r"<<\d+>>")

#: 格式符（A 类，不遮蔽）：C# string.Format 的 {0} 与 printf 风格
FORMAT_RE = re.compile(r"\{\d+\}|%[sdif]")

#: Unity / TMP 真认得的标签名（与 textnorm._RICH_TEXT_RE 保持一致的口径）
TMP_TAGS = frozenset((
    "b", "i", "u", "s", "color", "size", "sprite", "link", "align", "indent",
    "line-height", "lowercase", "uppercase", "smallcaps", "sup", "sub", "nobr",
    "mspace", "rotate", "font",
))

BACKTICK = chr(96)

# ---------------------------------------------------------------------------
# 规则表（顺序即优先级；同一位置长匹配优先在 _resolve() 里处理）
# ---------------------------------------------------------------------------

#: 1) URL：到空白或明显分隔符为止（尾部的 , . ; : 由 _trim_url() 去掉）
_URL_RE = re.compile(r"(?:https?://|www\.)[^\s<>\"'()\[\]{}]+", re.IGNORECASE)

#: 2) 文件路径：以资源类扩展名收尾的最硬信号（cfg/dll/mu/dds/png/...）
_PATH_RE = re.compile(
    r"[A-Za-z0-9_.\-/\\]*[A-Za-z0-9_\-]\.(?:cfg|dll|mu|dds|png|txt|json|ksp|cs|log|craft|sav)\b",
    re.IGNORECASE,
)

#: 3) 字面转义序列：反斜杠 + n/r/t/b/f/v/a/0/\\/引号，或 \\uXXXX / \\xNN；连续一段算一个哨兵
_ESCAPE_RE = re.compile(r"(?:\\(?:u[0-9a-fA-F]{4}|x[0-9a-fA-F]{2}|[nrtbfva0\\\"']|u))+")

#: 3b) 换行标签 <br> / <br/> / <BR>：不是 TMP 标签（textnorm._RICH_TEXT_RE 不认），
#:     但功能等价于字面换行，和转义序列一样按"粘连片段"补空格遮蔽。
_BREAK_RE = re.compile(r"<br\s*/?>", re.IGNORECASE)

#: 需要"两侧补一个空格、还原时空格一并吃掉"的规则（04 §5.4）
PAD_RULES = ("escape", "break")

#: 4) 反引号代码片段
_CODE_RE = re.compile(BACKTICK + "[^" + BACKTICK + r"\n]{1,160}" + BACKTICK)

#: 5) 正则片段（TriggerTech 那三条 "(?<=Phase Angle:)\\S+(?=...)" 就是这类）
_LOOKAROUND_RE = re.compile(r"\(\?[=!<][^()]{0,160}\)")

#: 6) 型号代码：CH-47 / Mi-17 / RD-254 / VK-2500PS-03（1–4 个字母 + 连字符 + 数字段）
_MODEL_RE = re.compile(r"\b[A-Z][A-Za-z]{0,3}-[0-9][A-Za-z0-9]*(?:-[A-Za-z0-9]+)*\b")

#: 7) 类名 / 配置键候选（是否真遮蔽由 _is_code_ident() 判定，避免把普通英文单词吃掉）
_IDENT_RE = re.compile(r"[A-Za-z][A-Za-z0-9_]{2,}")

#: 8) <模板变量>（<Kerman name> 这种；TMP 标签与 KSP 占位符排除在外）
_TEMPLATE_RE = re.compile(r"<(?=[A-Za-z_])[A-Za-z0-9_ .]{0,30}>")

#: 尖括号区段（用于把标签属性里的 FF8000 / color 排除在 ident 规则之外）
_MARKUP_SPAN_RE = re.compile(r"<[^<>]{1,80}>")

#: 类名/配置键的"看起来就是代码"后缀（KSP 插件里最常见的类型命名）
_CODE_SUFFIXES = (
    "FX", "Module", "Manager", "Handler", "Controller", "Renderer", "Addon",
    "Adapter", "Parser", "Loader", "Utils", "Util", "Config", "Patch", "Window",
    "Drawer", "Widget", "Behaviour", "Component", "System",
)

#: 出现在文本里也不该被当成代码的常见缩写（宁可不遮蔽，避免丢掉上下文）
_IDENT_WHITELIST = frozenset((
    "KSP", "NASA", "ESA", "USAF", "USSR", "OK", "ID", "UI", "API", "RTG", "SRB",
    "TWR", "RCS", "SAS", "EVA", "IVA", "VAB", "SPH", "KSC", "CTV", "LEM", "CSM",
    "Mk1", "Mk2", "Mk3", "Mk4", "Mk5",
))

#: URL 尾部允许被剔掉的中性标点（吃进去会让还原后的句子标点错位）
_URL_TAIL = ".,;:!?"

#: ident 前一个字符是这些 → 说明在标记语法里（<color=、#LOC、a=b），不是散文
_MARKUP_PREV = "<=\"'#/:"
#: ident 后一个字符是这些 → 同上（<b>、color=）
_MARKUP_NEXT = "=\">/"


def _trim_url(text: str, start: int, end: int) -> int:
    """URL 匹配尾部剔除中性标点与不成对右括号。"""
    while end > start and text[end - 1] in _URL_TAIL:
        end -= 1
    while end > start and text[end - 1] == ")" and text[start:end].count("(") < text[start:end].count(")"):
        end -= 1
    return end


def _is_code_ident(token: str) -> bool:
    """单个标识符是否属于"代码/类名/配置键"（B 类）。宁可少遮蔽，不要吃掉普通单词。"""
    if token in _IDENT_WHITELIST:
        return False

    length = len(token)
    upper = sum(1 for ch in token if ch.isupper())
    digit = any(ch.isdigit() for ch in token)
    camel = any(token[i].islower() and token[i + 1].isupper() for i in range(length - 1))
    # 词内有第二个大写（PartConfig / RealFuels）—— 普通英文单词不会这样
    inner_upper = any(ch.isupper() for ch in token[1:])

    # (a) KSP 类名：Module* / *FX / *Manager ...
    if token.startswith(("Module", "Part", "Vessel", "Celestial", "Game")) and (camel or inner_upper):
        return True
    # 后缀规则必须配合词内大写或数字，否则 "Config" "Util" 这种普通词会被吃掉
    if length >= 6 and token.endswith(_CODE_SUFFIXES) and (inner_upper or digit):
        return True

    # (b) 真 camelCase（BurstPQS / CryoVault / crewRequired）：要有小写→大写跃变
    if camel and length >= 5:
        return True

    # (c) 紧凑型号代码（XLR41 / SR73 / 2500PS / IR02）：数字 + 大写，且长度足够
    if digit and upper >= 1 and length >= 4:
        return True

    return False


def _is_template(text: str, start: int, end: int) -> bool:
    """<...> 是否是模板变量（B 类）而不是 TMP 标签 / KSP 占位符（A 类）。"""
    inner = text[start + 1:end - 1]
    if not inner:
        return False
    name = inner.strip().lower()
    if name.startswith("/") or name.startswith("#"):
        return False                       # 闭合标签 / 颜色字面量
    head = re.split(r"[= ]", name, maxsplit=1)[0]
    if head in TMP_TAGS:
        return False                       # TMP 标签，A 类
    if head in ("br", "br/"):
        return False                       # 换行标签走 _BREAK_RE（要补空格），这里让位
    return True


def _in_markup(text: str, start: int, end: int) -> bool:
    """ident 是否落在 <...> 标签区段内或紧贴标记符号（FF8000、color=、#LOC）。"""
    for match in _MARKUP_SPAN_RE.finditer(text):
        if match.start() <= start and end <= match.end():
            return True
        if match.start() > end:
            break
    if start > 0 and text[start - 1] in _MARKUP_PREV:
        return True
    if end < len(text) and text[end] in _MARKUP_NEXT:
        return True
    return False


def _candidates(text: str):
    """收集所有 B 类候选项：(start, end, rule, padded)。"""
    spans = []

    for match in _URL_RE.finditer(text):
        spans.append((match.start(), _trim_url(text, match.start(), match.end()), "url", False))

    for match in _PATH_RE.finditer(text):
        spans.append((match.start(), match.end(), "path", False))

    for match in _ESCAPE_RE.finditer(text):
        spans.append((match.start(), match.end(), "escape", True))

    for match in _BREAK_RE.finditer(text):
        spans.append((match.start(), match.end(), "break", True))

    for match in _CODE_RE.finditer(text):
        spans.append((match.start(), match.end(), "code", False))

    for match in _LOOKAROUND_RE.finditer(text):
        spans.append((match.start(), match.end(), "regex", False))

    for match in _MODEL_RE.finditer(text):
        spans.append((match.start(), match.end(), "model", False))

    for match in _IDENT_RE.finditer(text):
        if _is_code_ident(match.group(0)) and not _in_markup(text, match.start(), match.end()):
            spans.append((match.start(), match.end(), "ident", False))

    for match in _TEMPLATE_RE.finditer(text):
        if _is_template(text, match.start(), match.end()):
            spans.append((match.start(), match.end(), "template", False))

    return spans


def _resolve(spans):
    """左到右 + 长匹配优先，丢掉重叠项（04 §5.5 的顺序要求）。"""
    spans.sort(key=lambda item: (item[0], -(item[1] - item[0])))
    kept = []
    cursor = 0
    for start, end, rule, padded in spans:
        if end <= start:
            continue
        if start < cursor:
            continue                        # 与前一个重叠：短的那个丢掉
        kept.append((start, end, rule, padded))
        cursor = end
    return kept


class MaskResult:
    """一条文本的遮蔽结果。"""

    __slots__ = ("original", "text", "table", "padded", "hits")

    def __init__(self, original, text, table, padded, hits):
        self.original = original            # 原文
        self.text = text                    # 遮蔽后的文本（送模型）
        self.table = table                  # {哨兵: 原文片段}
        self.padded = padded                # 需要连空格一起还原的哨兵名集合
        self.hits = hits                    # {"url": 1, "escape": 2, ...} 便于报告

    @property
    def masked_count(self) -> int:
        return len(self.table)

    def __repr__(self) -> str:              # pragma: no cover
        return "MaskResult(hits={}, text={!r})".format(self.hits, self.text[:60])


def mask(text: str) -> MaskResult:
    """把 B 类片段换成 <<M0>> 哨兵。A 类原样保留。"""
    spans = _resolve(_candidates(text))

    table = {}
    padded = set()
    hits = {}
    pieces = []
    cursor = 0
    reverse = {}

    for start, end, rule, pad in spans:
        original = text[start:end]
        name = reverse.get(original)
        if name is None:
            name = "<<M{}>>".format(len(table))
            table[name] = original
            reverse[original] = name

        if rule in PAD_RULES:
            pad = True
        if pad:
            padded.add(name)

        pieces.append(text[cursor:start])
        if pad:
            # 无条件两侧各补一个空格（04 §5.4）：还原时各吃回一个。
            # 不能"邻居已是空格就不补"——同一片段可能在一条文本里既紧贴又带空格
            # （实测 pool 行 6608 的 "\n" 就是这种），全局 padded 标志会把真空格吃掉。
            pieces.append(" " + name + " ")
        else:
            pieces.append(name)
        hits[rule] = hits.get(rule, 0) + 1
        cursor = end

    pieces.append(text[cursor:])
    return MaskResult(text, "".join(pieces), table, padded, hits)


def restore(masked_text: str, result: MaskResult) -> str:
    """把哨兵换回原文。padded 的哨兵连同两侧各一个空格一起换回（04 §5.4）。"""
    out = masked_text
    for name, original in result.table.items():
        if name in result.padded:
            out = re.sub(" ?" + re.escape(name) + " ?", lambda _match, value=original: value, out)
        else:
            out = out.replace(name, original)
    return out


def sentinel_leaks(text: str) -> int:
    """残留哨兵数（G5 门禁：还原后必须为 0）。"""
    return len(SENTINEL_RE.findall(text))


def sentinel_multiset(text: str) -> dict:
    """哨兵多重集：{"<<M0>>": 1, "<<M1>>": 2}。

    G5 的双向判定之一：**模型漏抄**哨兵时 sentinel_leaks 是 0（没残留），但内容已经丢了，
    所以必须比对"原文哨兵多重集 == 译文哨兵多重集"，而不是只看残留。
    """
    table = {}
    for match in SENTINEL_RE.finditer(text):
        name = match.group(0)
        table[name] = table.get(name, 0) + 1
    return table


def unknown_sentinels(text: str, result: MaskResult):
    """译文里出现了表外的哨兵（模型自己编的编号），返回去重列表。"""
    known = set(result.table)
    return sorted({"<<M{}>>".format(number) for number in SENTINEL_RE.findall(text)
                   if "<<M{}>>".format(number) not in known})


def is_all_sentinel(masked_text: str) -> bool:
    """遮蔽后整条只剩哨兵与空白（04 §5.5：这种直接标 keep，不送翻）。"""
    rest = SENTINEL_RE.sub("", masked_text).strip()
    return bool(SENTINEL_RE.search(masked_text)) and rest == ""


# ---------------------------------------------------------------------------
# A 类元素清点（供 G2 / G3 / G4 门禁用）
# ---------------------------------------------------------------------------

def count_placeholders(text: str) -> dict:
    """KSP 占位符 <<n>> 的多重集：{"1": 1, "2": 1}。"""
    table = {}
    for match in PLACEHOLDER_RE.finditer(text):
        key = match.group(0)[2:-2]
        table[key] = table.get(key, 0) + 1
    return table


def count_formats(text: str) -> dict:
    """格式符多重集：{0} / %s / %d / %i / %f。"""
    table = {}
    for match in FORMAT_RE.finditer(text):
        token = match.group(0)
        key = token if token.startswith("%") else "{" + token[1:-1] + "}"
        table[key] = table.get(key, 0) + 1
    return table


def rich_tags(text: str) -> list:
    """富文本标签序列（逐字节，含属性值与引号；顺序保留供比对）。"""
    from .textnorm import _RICH_TEXT_RE

    return [match.group(0) for match in _RICH_TEXT_RE.finditer(text)]


def count_rich_tags(text: str) -> dict:
    """富文本标签多重集（G4：标签名与属性值逐字节一致）。"""
    table = {}
    for tag in rich_tags(text):
        table[tag] = table.get(tag, 0) + 1
    return table


# ---------------------------------------------------------------------------
# 自测：python -m i18n_tools.mask
# ---------------------------------------------------------------------------

def _self_test() -> int:
    from . import ensure_stdout

    ensure_stdout()
    failures = []

    def check(label, actual, expected):
        if actual != expected:
            failures.append("{}: 期望 {!r}，实际 {!r}".format(label, expected, actual))

    # 04 §5.4 案例一：文件路径
    result = mask("Config: GameData/RealFuels/Resources.cfg")
    check("路径.遮蔽", result.text, "Config: <<M0>>")
    check("路径.还原", restore(result.text, result), "GameData/RealFuels/Resources.cfg".join(("Config: ", "")))

    # 04 §5.4 案例二：字面转义序列（粘连片段必须补空格）
    source = "Line one\\nLine two"
    result = mask(source)
    check("转义.遮蔽", result.text, "Line one <<M0>> Line two")
    check("转义.还原", restore(result.text, result), source)

    # A 类一个字节都不许动（04 §5.2）
    for keep in (
        "Apoapsis: <<1>> m (<<2>>)",
        "<color=#FF8000><b>Warning:</b></color> Overheat",
        "{0} units of Liquid Fuel remaining",
        "Cannot activate %s while in atmosphere",
        "See https://github.com/KSP-RO/RealFuels for details",
        "Stage 3 (SRB) - TWR 1.42",
    ):
        result = mask(keep)
        check("往返." + keep[:24], restore(result.text, result), keep)

    # A 类里占位符/格式符/标签必须仍然可见
    result = mask("Apoapsis: <<1>> m (<<2>>)")
    check("A类.占位符不遮蔽", result.text, "Apoapsis: <<1>> m (<<2>>)")
    result = mask("<color=#FF8000><b>Warning:</b></color>")
    check("A类.标签不遮蔽", result.text, "<color=#FF8000><b>Warning:</b></color>")

    # 型号代码遮蔽、普通单词不遮蔽
    result = mask("Boeing CH-47 Chinook is equipped with 2 rotors")
    check("型号.CH47被遮蔽", "<<M" in result.text, True)
    check("普通词.Chinook未被遮蔽", "Chinook" in result.text, True)
    check("普通词.Config未被遮蔽", mask("Config").table, {})
    check("普通词.Util未被遮蔽", mask("Pump Util ratio").table, {})

    # 半遮蔽（整条只剩哨兵）
    check("纯哨兵", is_all_sentinel(mask("GameData/x.cfg").text), True)
    check("非纯哨兵", is_all_sentinel(mask("Load GameData/x.cfg now").text), False)

    # 多哨兵与还原
    source = "BurstPQS could not find any burst-compiled methods!\\n\\nThis is not a hard error."
    result = mask(source)
    check("多重哨兵.还原", restore(result.text, result), source)

    # 换行标签 <br>：按粘连片段补空格遮蔽（textnorm 不认它是 TMP 标签，但必须逐字节保住）
    result = mask("Line one<br>Line two")
    check("br.遮蔽", result.text, "Line one <<M0>> Line two")
    check("br.还原", restore(result.text, result), "Line one<br>Line two")
    result = mask("Line one<br/>Line two <BR> Line three")
    check("br.变体还原", restore(result.text, result), "Line one<br/>Line two <BR> Line three")

    # 回归（pool 行 6608）：同一个转义片段在一行里既紧贴空格又紧贴标签，
    # 全局 padded 标志曾把真空格吃掉 —— 现在补空格无条件，还原各吃一个
    source = "<b>Solar panels TL0</b> \\n <color=white>Mass: 12kg/m2</color> \\n <color=green>Cost: 1000/m2</color>"
    result = mask(source)
    check("混合上下文.还原", restore(result.text, result), source)

    # 哨兵多重集（G5 双向判定用）
    result = mask("GameData/a.cfg and GameData/a.cfg")
    check("哨兵多重集.原文", sentinel_multiset(result.text), {"<<M0>>": 2})
    check("哨兵多重集.漏抄可检出", sentinel_multiset("<<M0>> and") != sentinel_multiset(result.text), True)

    # 同一片段复用同一哨兵、编号不跳号
    result = mask("GameData/a.cfg and GameData/a.cfg")
    check("同片段复用", (result.text, len(result.table)), ("<<M0>> and <<M0>>", 1))

    # 哨兵残留清点
    source = "see https://a.b/c.d and GameData/x.cfg"
    result = mask(source)
    check("哨兵清点", sentinel_leaks(restore(result.text, result)), 0)
    check("表外哨兵", unknown_sentinels("<<M0>> 与 <<M9>>", result), ["<<M9>>"])

    # A 类清点器
    check("占位符多重集", count_placeholders("<<1>> m (<<2>>) <<1>>"), {"1": 2, "2": 1})
    check("格式符多重集", count_formats("%s has {0} and {1}"), {"%s": 1, "{0}": 1, "{1}": 1})
    check("富文本多重集", count_rich_tags("<b>a</b><color=green>b</color>"),
          {"<b>": 1, "</b>": 1, "<color=green>": 1, "</color>": 1})

    if failures:
        for line in failures:
            print("[FAIL]", line)
        print("{} 条断言未通过".format(len(failures)))
        return 1
    print("[OK] mask 自测全部通过")
    return 0


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(_self_test())
