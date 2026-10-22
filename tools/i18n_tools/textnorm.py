"""P1 归一化 + P2 过滤判据 + 特征标记（纯字符串运算，不碰文件系统）。

对应文档：
  - doc 04 §4.1 归一化：NFKC → 换行统一 → 空白折叠（含 NBSP U+00A0）→ trim。
    刻意**不**做大小写折叠、**不**统一引号：大小写是筛选信号（cfg 里 Title/Title 变体要保留原写法），
    引号统一会让 "…" 与 "..." 在去重时被当成不同文本（KSP 里两者都出现，去重按原文更保守）。
  - doc 04 §4.2 过滤：empty / is-key / already-cjk / pure-number / too-short / search-tags / url-only / already-covered / dup。
    判据都在这里，harvest 只负责按顺序调用与计数。
  - doc 02 §5.2 特征标记：ph 占位符、rt 富文本、dg 含数字、path 路径、url 链接、len 长度。
"""

from __future__ import annotations

import re
import unicodedata

# 中日韩字符：CJK 部首/符号、假名、注音、谚文、CJK 扩展 A、CJK 统一表意、兼容汉字、半角片假名。
# 命中即认为"这条已经是中文（或日韩文）了"，不需要再翻 —— 也就是 already-cjk 判据。
_CJK_RE = re.compile(
    "[\u1100-\u11ff\u2e80-\u303f\u3040-\u30ff\u3400-\u4dbf"
    "\u4e00-\u9fff\uf900-\ufaff\uff66-\uff9f]"
)

# 占位符三种写法：KSP 自己的 <<1>>、C# string.Format 的 {0}、printf 风格 %s %d %i %f。
_PLACEHOLDER_RE = re.compile(r"<<\d+>>|\{\d+\}|%[sdif]")

# 富文本标签：只认 Unity/TMP 真认得的标签名。
# 不能简单写成 </?[a-zA-Z][^<>]*>：正文里的 "<no body>" 会被误吃（实测多算 3 条）；
# 也不能写成 <[^<>]{1,40}>：占位符 <<1>> 会被误吃（实测多算 38 条）。
_RICH_TEXT_RE = re.compile(
    r"<(?=[#/]|(?:b|i|u|s|color|size|sprite|link|align|indent|line-height|lowercase|"
    r"uppercase|smallcaps|sup|sub|nobr|mspace|rotate|font)[=> /])[^<>]*>",
    re.IGNORECASE,
)

_DIGIT_RE = re.compile(r"\d")
_URL_RE = re.compile(r"https?://|www\.", re.IGNORECASE)

# 路径判据：只看"文件名带资源扩展名"这种最硬的信号。
# 不用 (?:[\w.-]+/){2,}[\w.-]+\.cfg 这种带斜杠分段的正则：它会被 https:// 里的斜杠骗到，
# 把那条 URL 文本误判成 path（实测 0 条真 path，误判 1 条）。
_PATH_RE = re.compile(r"\.(?:cfg|dll|mu|dds|png)\b", re.IGNORECASE)

# （纯数字判据见 is_pure_number：无字母 + 含数字即可，不搞单位白名单。）

# 只含一个 URL 的文本（不是"含 URL"，是"整条就是 URL"）。
_URL_ONLY_RE = re.compile(r"^(?:https?://|www\.)\S+$", re.IGNORECASE)

# 本地化键引用："#autoLOC_500460" / "#LOC_xxx" —— 这是键不是文本，翻它没有意义。
_KEY_LIKE_RE = re.compile(r"^#[A-Za-z][A-Za-z0-9_.\-]*$")

# 空白：Python 的 \s 对 str 已经含 U+00A0、U+3000 等 Unicode 空白，这里显式列出便于阅读。
_WS_RE = re.compile(r"[\s\u00a0\u2000-\u200a\u202f\u205f\u3000]+")

# 长度桶：doc 04 §4.3 的 5 档，边界是闭区间（2-10 / 11-30 / 31-80 / 81-200 / 200+）。
LEN_BUCKETS = (("2-10", 2, 10), ("11-30", 11, 30), ("31-80", 31, 80), ("81-200", 81, 200), ("200+", 201, None))


def normalize(text: str) -> str:
    """归一化文本（doc 04 §4.1 的四步）。空输入返回空串。"""
    if not text:
        return ""
    text = unicodedata.normalize("NFKC", text)      # 全角转半角、兼容字符归并
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    text = _WS_RE.sub(" ", text)                    # 换行/制表/NBSP 全部折成一个空格
    return text.strip()


def has_cjk(text: str) -> bool:
    """是否含中日韩字符（already-cjk 判据）。"""
    return _CJK_RE.search(text) is not None


def cjk_count(text: str) -> int:
    """中日韩字符个数（统计/报表用）。"""
    return len(_CJK_RE.findall(text))


def is_key_like(text: str) -> bool:
    """整条是不是一个本地化键引用（"#autoLOC_500460"），是则不该当正文翻译。"""
    return _KEY_LIKE_RE.match(text) is not None


def is_pure_number(text: str) -> bool:
    """整条只有数字和分隔符（"3.5"、"10%"、"1-2"、"1,000"）：含数字且没有别的字符。

    判据必须收紧到"整条只由数字与分隔符组成"：早先写成"没有字母且含数字"，会把
    "<<1>> <<2>>"、"<<"1>>.<<"2>>".<<"3>>"" 这类**真待译的占位符文本**判成纯数字丢掉
    （既有产物里它们全部保留），故这里逐字符白名单——数字、空白、以及 . , : % + - / ° ( ) 这几个
    在数字串里常见的中性符号。
    """
    if not _DIGIT_RE.search(text):
        return False
    return _NUMERIC_CHARS_RE.fullmatch(text) is not None


# 纯数字文本允许出现的字符集（见 is_pure_number 的说明）
_NUMERIC_CHARS_RE = re.compile(r"[0-9\s.,:%+\-/°()]+")


def is_url_only(text: str) -> bool:
    """整条就是一个 URL（url-only 判据；注意与"含 URL"不同）。"""
    return _URL_ONLY_RE.match(text) is not None


def len_bucket(n: int) -> str:
    """按 doc 04 §4.3 归入长度桶；len<2 的落 "2-10"（这类在过滤阶段已被 too-short 拦掉）。"""
    for name, lo, hi in LEN_BUCKETS:
        if hi is None:
            if n >= lo:
                return name
        elif lo <= n <= hi:
            return name
    return LEN_BUCKETS[0][0]


def features(text: str) -> dict:
    """特征位（doc 02 §5.2 / doc 04 §4.3）。键顺序与 pool.jsonl 里的写出顺序一致。"""
    return {
        "ph": _PLACEHOLDER_RE.search(text) is not None,
        "rt": _RICH_TEXT_RE.search(text) is not None,
        "dg": _DIGIT_RE.search(text) is not None,
        "path": _PATH_RE.search(text) is not None,
        "url": _URL_RE.search(text) is not None,
        "len": len(text),
    }