# -*- coding: utf-8 -*-
"""文本分桶：把候选串分成「该翻 / 短标签 / 永不翻」三档（task-32 交付 2 的规则本体）。

为什么要分桶：task-29 的 reports/il-reachability.md 第 5 节把 2,082 条 distinct 分成 7 类噪声，
最后一类「其他（含待译句子）」占了 1,669 条（80.16%）。那一桶里混着两种完全不同的东西：

  * 真待译的句子（"Searching assets to load..."）
  * 根本不是文本的短标记（"+5 Rep" / "« DOCK" / "0 Data" / "0%" / "*" / "+"）

把后一类丢进翻译队列，等于每一条都白烧 token，而且它们进不了任何术语表。

四档语义（原设计三档，Lead 在 task-32 追加要求把「带数字/进度的模板串」单独成档）：

  translate    有实义、需要翻成中文（句子、短语）
  short_label  单字/单词但有实义（Back / Done / Edit / Save / Close / Next / Warp / mass）
  template     含数字或占位符的模板/进度串（"ModuleManager: 8448 patches applied"、
               "Loading sound asset 0/234"）：保留数据，翻不翻由下游决定——
               直接当正文翻会把数字和单位写死，直接丢又会漏掉可翻的骨架
  never        不该翻：键引用、路径、URL、标识符、纯符号、纯数字、已经是中文、
               缩写/单位标记（"+5 Rep"、"AA Off"）

reason 是机器可读原因码，顺序即优先级（先命中先返回），这样同一份数据在不同时间跑
分桶结果稳定。规则只依赖文本本身，不依赖它来自哪个 DLL、哪个方法——不用
「方法名像 UI 就收」这类启发式，那样无法复现也无法解释。

用法：

    from i18n_tools.buckets import classify
    classify("Vessel mass")      # ("translate", "sentence")
    classify("+5 Rep")           # ("never", "weak_words")
    classify("Back")             # ("short_label", "single_word")
    classify("Loading sound asset 0/234")   # ("template", "template_progress")
"""

import re
import unicodedata

__all__ = ["BUCKETS", "REASONS", "classify", "strip_tags", "ascii_words", "count_words",
           "is_translatable", "BUCKET_ORDER"]

# 三档（顺序即报表输出顺序）
BUCKETS = ("translate", "short_label", "template", "never")
BUCKET_ORDER = {b: i for i, b in enumerate(BUCKETS)}

# 原因码：顺序即优先级。
REASONS = (
    "empty",          # 归一化后是空串
    "keyref",         # "#autoLOC_500460" 这类键引用
    "url",            # 整条是 URL / 含 http(s)
    "path",           # 文件路径 / 资源路径（GameData/... 、*.cfg）
    "already_cjk",    # 已经是中文（运行时捕获里大量存在）
    "tags_only",      # 去富文本标签后为空（"<b></b>"）
    "no_letters",     # 没有任何 ASCII 字母（"0%"、"9+"、"*"）
    "format_only",    # 去掉占位符后没有字母（"{0}/{1}"、"%s"）
    "identifier",     # 标识符形态（snake_case / dotted / ALLCAPS / camelCase）
    "too_short",      # 去标签后长度 <= 2
    "no_word",        # 没有长度 >= 3 的 ASCII 字母词
    "weak_words",     # 缩写/单位/数量标记（"+5 Rep"、"AA Off"、"« DOCK"、"0 Data"）
    "template_progress",  # 带数字/进度的模板串（"ModuleManager: 8448 patches applied"）
    "template_format",    # 带占位符但仍有实词的格式串（"Loading <<1>> of <<2>>"）
    "single_word",    # 单个有实义的词（Back / Close / Warp）
    "sentence",       # 句子/短语（>= 2 个词）
)

# ---------------------------------------------------------------------------
# 正则
# ---------------------------------------------------------------------------
# 富文本标签：<b> <color=red> </color> <sprite=3> 等；用白名单标签名，避免把
# "<no body>" 这类普通文本吃掉（task-29 标定时踩过这个坑）。
_TAG_RE = re.compile(
    r"</?(?:b|i|u|s|color|size|sprite|link|align|indent|line-height|lowercase|uppercase|"
    r"smallcaps|sup|sub|nobr|mspace|rotate|font|material|quad)(?:=[^<>]*)?>",
    re.I)

# 占位符：<<1>> / {0} / {0:F2} / %s %d %i %f / %1$s
_PLACEHOLDER_RE = re.compile(r"<<\d+>>|\{\d+(?:[:,][^{}]*)?\}|%\d*\$?[sdif]")

_URL_RE = re.compile(r"(?i)https?://|www\.")
_EXT_RE = re.compile(r"\.(?:cfg|dll|mu|dds|png|txt|json|ksp|asset|prefab|cs|xml|log)\b", re.I)
_PATH_SEG_RE = re.compile(r"^[A-Za-z0-9_.\-]+(?:[/\\][A-Za-z0-9_.\-]+){2,}$")
_IDENT_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_.]*$")
_WORD_RE = re.compile(r"[A-Za-z]+")
_DIGIT_RE = re.compile(r"[0-9]")
_DOTTED_RE = re.compile(r"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+\Z")
_CAMEL_RE = re.compile(r"[a-z][A-Z]")

_CJK_RE = re.compile(r"[\u2e80-\u9fff\uf900-\ufaff\uff00-\uffef\u3000-\u303f]")


def _norm(text):
    """局部归一化（不 import textnorm，保持本模块零依赖，便于被 selftest 单独喂样例）。

    与 textnorm.normalize 的差别：这里不做 NFKC 之外的空白折叠以外的处理，
    只把换行变空格再折叠连续空白——分桶要看词数，但不需要保留原始换行。
    """
    if not text:
        return ""
    text = unicodedata.normalize("NFKC", text)
    text = text.replace("\r\n", " ").replace("\r", " ").replace("\n", " ")
    return " ".join(text.split())


def strip_tags(text):
    """去掉富文本标签与占位符，返回剩下的「可读正文」。"""
    if not text:
        return ""
    out = _TAG_RE.sub(" ", text)
    out = _PLACEHOLDER_RE.sub(" ", out)
    return " ".join(out.split())


def ascii_words(text):
    """取所有 ASCII 字母词（连续字母），保持原序。"""
    return _WORD_RE.findall(text or "")


def count_words(text):
    """词数（按空白切分后非空片段数）。"""
    return len([t for t in (text or "").split() if t])


def is_translatable(bucket):
    """bucket != never 即算「要进候选集」（template 也进，但下游可以只取 translate）。"""
    return bucket in ("translate", "short_label", "template")


def _has_cjk(text):
    return _CJK_RE.search(text) is not None


def _looks_identifier(body):
    """标识符形态：snake_case / dotted / 全大写 / camelCase。

    注意 dotted 必须用 _DOTTED_RE（点号两侧都要有标识符字符）而不是裸的 "." in body：
    否则 "Loading..." 这类省略号结尾的普通词会被当成命名空间式标识符判进 never。
    """
    if not _IDENT_RE.match(body):
        return False
    if "_" in body:
        return True
    if _DOTTED_RE.match(body):
        return True
    if body.isupper() and len(body) >= 2:
        return True
    if _CAMEL_RE.search(body):
        return True
    return False


def classify(text):
    """文本 -> (bucket, reason)。顺序即优先级，先命中先返回。"""
    n = _norm(text)
    if not n:
        return ("never", "empty")
    if n.startswith("#"):
        return ("never", "keyref")
    if _URL_RE.search(n):
        return ("never", "url")
    if "\\" in n or "/GameData" in n or _EXT_RE.search(n) or _PATH_SEG_RE.match(n):
        return ("never", "path")
    body = strip_tags(n)
    if not body:
        return ("never", "tags_only")
    if _has_cjk(body):
        return ("never", "already_cjk")
    letters = _WORD_RE.findall(body)
    if not letters:
        # 去掉占位符后仍然没有字母：区分「纯符号」与「纯格式串」
        if _PLACEHOLDER_RE.search(n) or "{" in n or "}" in n:
            return ("never", "format_only")
        return ("never", "no_letters")
    if _looks_identifier(body):
        return ("never", "identifier")
    if len(body) <= 2:
        return ("never", "too_short")
    words = [w for w in letters if len(w) >= 3]
    if not words:
        return ("never", "no_word")
    # 缩写 / 单位 / 数量标记：
    #   1) 以非字母开头（"+5 Rep"、"% Hi"、"« DOCK"、"(free)"）且总字母数 <= 8
    #   2) 含全大写缩写词（"New LC"、"AA Off"）且总字母数 <= 12
    # 阈值是防误伤的护栏：真句子即使以符号开头，字母数也远超阈值
    # （"« Attention: reactor meltdown imminent »" 有 30 个字母）。
    total_letters = sum(len(w) for w in letters)
    if not body[0].isalpha() and total_letters <= 8:
        return ("never", "weak_words")
    # 2) 含全大写缩写词，且整串没有任何 >= 4 字母的实词（"AA Off" 是标记，
    #    "CPU Textures" 不是——Textures 要翻，所以加最长词 <= 3 这个护栏）
    if any(len(w) >= 2 and w.isupper() for w in letters) and total_letters <= 12:
        if max(len(w) for w in letters) <= 3:
            return ("never", "weak_words")
    # 模板/进度串：带数字或占位符，且仍有 >= 2 个词或 >= 2 个字母词。
    # 这类串翻译时必须保留数字与占位符，不能当普通句子处理，所以单列一档；
    # 上游 il-translatable.jsonl 会照收（bucket != never），但下游可以只取 translate。
    if _PLACEHOLDER_RE.search(n) and len(letters) >= 2:
        return ("template", "template_format")
    if _DIGIT_RE.search(body) and len(letters) >= 2:
        return ("template", "template_progress")
    if len(letters) == 1 and len(body) <= 24:
        return ("short_label", "single_word")
    return ("translate", "sentence")
