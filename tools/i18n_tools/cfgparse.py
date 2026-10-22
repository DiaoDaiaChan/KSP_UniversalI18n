"""KSP cfg 轻量解析器（标准库，无依赖）。

只做流水线需要的两件事：按节点名找节点、拿键值对（含行号）。
不实现 ModuleManager 语义（不执行 @ / + / ! 补丁）—— 我们读的是磁盘原文。

已踩过的坑（本模块专门处理）：
  1. 节点名可能单独占一行，左大括号在下一行：
         Localization
         {
             en-us { ... }
         }
     所以节点名必须能"挂起"跨行等左大括号。
  2. 注释符会吃掉 URL：url = https://x 里的双斜线不是注释。
     规则：只在行首或空白之后出现的双斜线才是注释（见 strip_comment）。

解析器对畸形输入（括号不配对、缺值）不抛异常，只尽力而为地继续 ——
GameData 里确实有 mod 写坏的 cfg，流水线不能因为一个文件崩掉。
"""

from __future__ import annotations

import re

_WS = " \t"
_WORD_STOP = set(" \t{}=;,\r")


def strip_comment(line: str) -> str:
    """剥掉行内注释，但保护 https:// 这类协议前缀。

    判定：双斜线在行首，或前一个字符是空白 → 注释起点；否则是正文（URL）。
    """
    i = 0
    while True:
        j = line.find("//", i)
        if j < 0:
            return line
        if j == 0 or line[j - 1] in _WS:
            return line[:j]
        i = j + 2


class Node:
    """cfg 节点。values / children 都保序（抽取按出现顺序编号，顺序必须稳定）。"""

    __slots__ = ("name", "values", "children", "parent", "lineno")

    def __init__(self, name: str, lineno: int = 0, parent: "Node | None" = None):
        self.name = name
        self.values: list[tuple[str, str, int]] = []
        self.children: list["Node"] = []
        self.parent = parent
        self.lineno = lineno

    # -- 查询 ---------------------------------------------------------------
    def child(self, name: str) -> "Node | None":
        """按名字取第一个子节点（大小写不敏感，KSP 的 ConfigNode 就是这样）。"""
        low = name.lower()
        for c in self.children:
            if c.name.lower() == low:
                return c
        return None

    def children_named(self, name: str):
        low = name.lower()
        for c in self.children:
            if c.name.lower() == low:
                yield c

    def get(self, key: str, default: "str | None" = None) -> "str | None":
        low = key.lower()
        for k, v, _ in self.values:
            if k.lower() == low:
                return v
        return default

    def walk(self):
        """深度优先遍历自身与全部后代（先序遍历，顺序与递归版一致）。

        用显式栈而不是递归：GameData 里确实有嵌套上千层的畸形 cfg（括号不配对 accumulated），
        递归版会 RecursionError。
        """
        stack = [self]
        while stack:
            node = stack.pop()
            yield node
            for c in reversed(node.children):
                stack.append(c)

    def __repr__(self) -> str:  # pragma: no cover - 调试用
        return "<Node %r values=%d children=%d>" % (self.name, len(self.values), len(self.children))


# 每行按顺序尝试："键=值" → "名 {" → "}" → "{" → "名"
_RE_KV = re.compile(r"^[ \t]*(?P<key>[^\s{}=;]+)[ \t]*=(?P<value>.*)$")
_RE_OPEN = re.compile(r"^[ \t]*(?P<name>[^\s{}=;]+)[ \t]*\{")
_RE_LBRACE = re.compile(r"^[ \t]*\{")
_RE_CLOSE = re.compile(r"^[ \t]*\}")
_RE_NAME = re.compile(r"^[ \t]*(?P<name>[^\s{}=;]+)[ \t]*$")


class ParseStats:
    """解析计数：用来解释"为什么这个 mod 少了几个 key"。"""

    __slots__ = ("files", "nodes", "values", "bad_lines", "inline_skipped")

    def __init__(self):
        self.files = 0
        self.nodes = 0
        self.values = 0
        self.bad_lines = 0
        self.inline_skipped = 0


def parse_text(text: str, *, stats: "ParseStats | None" = None) -> Node:
    """把 cfg 文本解析成 Node 树，返回名为空串的根节点。"""
    root = Node("")
    stack: list[Node] = [root]
    pending: "str | None" = None  # 已读到节点名、还没等到左大括号

    # 先统一换行：CRLF 文件若只按 "\n" 切，行尾会留 "\\r"，节点名行
    # （如 "Localization\\r"）就匹配不上 _RE_NAME，整个节点被吞掉。
    for lineno, raw in enumerate(normalize_newlines(text).split("\n"), 1):
        line = strip_comment(raw)
        if not line.strip():
            continue
        rest = line
        # 本行是否已经出现过"节点开括号"（Node { 或单独 {）。见 _RE_KV 分支里的说明。
        inline_node = False
        while rest:
            m = _RE_KV.match(rest)
            if m:
                # 一行的第一个结构是"节点开括号"时，其后的 key = value 不再当正文抽取。
                # 依据：ContractConfigurator\patches\Agent_Title.cfg 的
                #   @AGENT[*]:HAS[~title] { title = #$name$ }
                # 是单行写的 ModuleManager 补丁节点，值是补丁覆盖值（同 @title 不该进池），
                # 而且照原样解析会把行尾的 "}" 一起吃进值里（得到 "#$name$ }" 这种垃圾）。
                if inline_node:
                    if stats is not None:
                        stats.inline_skipped += 1
                    break
                stack[-1].values.append((m.group("key"), m.group("value").strip(), lineno))
                if stats is not None:
                    stats.values += 1
                pending = None
                break  # 值吃到行尾，本行结束
            m = _RE_OPEN.match(rest)
            if m:
                inline_node = True
                n = Node(m.group("name"), lineno, stack[-1])
                stack[-1].children.append(n)
                stack.append(n)
                if stats is not None:
                    stats.nodes += 1
                pending = None
                rest = rest[m.end():]
                continue
            m = _RE_CLOSE.match(rest)
            if m:
                if len(stack) > 1:
                    stack.pop()
                pending = None
                rest = rest[m.end():]
                continue
            m = _RE_LBRACE.match(rest)
            if m:
                inline_node = True
                n = Node(pending if pending is not None else "", lineno, stack[-1])
                stack[-1].children.append(n)
                stack.append(n)
                if stats is not None:
                    stats.nodes += 1
                pending = None
                rest = rest[m.end():]
                continue
            m = _RE_NAME.match(rest)
            if m:
                pending = m.group("name")
                break  # 名字挂起，等下一行的左大括号
            # 认不出的残渣：整行不再往下猜。
            # 原来是"跳过一段再继续扫本行"，会让 "adapter   #LOC.SXT_x_title = FAT-55 ..."
            # 这种排版错乱的行也被当成一条键值（前一个词 adapter 被跳过、后半截被当成正文），
            # 既有产物里没有这类行，所以改成放弃本行剩余部分。
            if stats is not None:
                stats.bad_lines += 1
            break
    return root


def parse_file(path: str, *, stats: "ParseStats | None" = None) -> Node:
    """读文件并解析。吃掉 BOM；非 UTF-8 用替换语义（KSP 自己也不校验编码）。"""
    text = read_text(path)
    if stats is not None:
        stats.files += 1
    return parse_text(text, stats=stats)


def read_text(path: str) -> str:
    """统一的读文本口径，newline="" 避免 python 替我们把 CRLF 改掉。"""
    with open(path, "r", encoding="utf-8-sig", errors="replace", newline="") as f:
        return f.read()


def normalize_newlines(text: str) -> str:
    """CRLF / CR → LF（doc 04 §4.1 归一化第 2 条）。"""
    return text.replace("\r\n", "\n").replace("\r", "\n")
