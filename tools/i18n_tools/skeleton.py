"""数字骨架（pattern）通道的唯一实现 —— 与 C# 侧 UI18n.Core/Text/TextSkeleton.cs 逐字同口径。

为什么需要骨架：捕获回来的模板档（含数字的串，如 `Mass Flow: 2.2 ton/s`）每一条原文都不同，
直译不可能覆盖 —— 屏幕上每帧数字都在变。把数字换成占位符 `#` 后，几万条原文塌成几百条骨架，
一条骨架译文覆盖它的全部实例；运行时按骨架查表，再把数字按顺序填回去。

规则（与 C# 必须完全一致，靠 tests/UI18n.Core.Tests/Data/skeleton-golden.jsonl 钉死）：

1. 数字段 = 正则 `\\d+(?:[.,]\\d+)?` 的匹配：连续 ASCII 数字，后面允许**最多一次** `.` 或 `,`
   紧跟至少一位数字（只吃一层分隔符：`1.2.3` 得到 `1.2` 与 `3`，骨架 `#.#`）。
2. 每个数字段替换成**一个** `#`。译文侧同样用 `#` 表示「这里原样填回捕获到的数字」，
   门禁只查 `#` 的个数是否相等。
3. **TMP 富文本标签内部不骨化**：`</?(?:b|i|u|s|color|size|material|quad|sprite|link)(?:=[^>]*)?>`
   （与 resolve_keyrefs.py:60 和 CaptureClassify.TmpTagPattern 逐字相同的白名单）匹配到的整段原样拷贝，
   里面的十六进制色值 `#FF00AA` 不许被拆成 `#FF#AA`。未知尖括号内容（`a < b`）照常骨化。
4. 数字段少于 1 个 -> 不是模板，返回 `(None, [])`。
5. 数字段多于 MAX_SLOTS 个 -> 放弃（长日志/序列化数据，运行时填回成本与误伤风险都不值），返回 `(None, [])`。
6. 其余字符一个不动（空格、冒号、KSP 占位符 `<<1>>` 都原样保留）。

铁律：骨架是**查表键**，不是译文槽位；除数字换成 `#` 外不做任何改写（大小写/空白一律不动）。
"""

from __future__ import annotations

import argparse
import json
import re
import sys

MAX_SLOTS = 8

_NUMBER_RE = re.compile(r"\d+(?:[.,]\d+)?")
TMP_TAG_PATTERN = r"</?(?:b|i|u|s|color|size|material|quad|sprite|link)(?:=[^>]*)?>"
_TMP_TAG_RE = re.compile(TMP_TAG_PATTERN)


def skeleton(text):
    """原文 -> (骨架, 数字段列表)；不是模板/超限时返回 (None, [])。

    >>> skeleton('Mass Flow: 2.20917 ton/s')
    ('Mass Flow: # ton/s', ['2.20917'])
    """
    if not isinstance(text, str) or not text:
        return None, []

    out = []
    numbers = []
    pos = 0
    length = len(text)
    while pos < length:
        ch = text[pos]
        if ch == "<":
            tag = _TMP_TAG_RE.match(text, pos)
            if tag:
                out.append(tag.group(0))
                pos = tag.end()
                continue
        if "0" <= ch <= "9":
            match = _NUMBER_RE.match(text, pos)
            out.append("#")
            numbers.append(match.group(0))
            pos = match.end()
            continue
        out.append(ch)
        pos += 1

    if not numbers or len(numbers) > MAX_SLOTS:
        return None, []
    return "".join(out), numbers


def fill_back(skeleton_text, numbers):
    """骨架 + 数字段 -> 原样还原（自检/单测用；运行时填回在 C# 里做，逻辑必须一致）。

    **`#` 不都是占位符**：TMP 标签内部的 `#`（如 `<color=#FF00AA>` 的色值分隔）是字面量，
    只有标签**外面**的 `#` 才吃一个数字段 —— 与 <see cref="skeleton"/> 的骨化规则互逆。
    """
    if not isinstance(skeleton_text, str) or not isinstance(numbers, (list, tuple)):
        return None
    out = []
    used = 0
    pos = 0
    length = len(skeleton_text)
    while pos < length:
        ch = skeleton_text[pos]
        if ch == "<":
            tag = _TMP_TAG_RE.match(skeleton_text, pos)
            if tag:
                out.append(tag.group(0))
                pos = tag.end()
                continue
        if ch == "#":
            if used >= len(numbers):
                return None
            out.append(str(numbers[used]))
            used += 1
            pos += 1
            continue
        out.append(ch)
        pos += 1
    if used != len(numbers):
        return None
    return "".join(out)


def placeholders(skeleton_text):
    """骨架里占位符 `#` 的个数（TMP 标签内部的不算）—— 门禁与运行期填回的共同口径。"""
    if not isinstance(skeleton_text, str):
        return -1
    count = 0
    pos = 0
    length = len(skeleton_text)
    while pos < length:
        ch = skeleton_text[pos]
        if ch == "<":
            tag = _TMP_TAG_RE.match(skeleton_text, pos)
            if tag:
                pos = tag.end()
                continue
        if ch == "#":
            count += 1
        pos += 1
    return count


def has_digits(text):
    return isinstance(text, str) and any("0" <= ch <= "9" for ch in text)


HANDCRAFTED = [
    "Mass Flow: 2.20917 ton/s",
    "Vessels: 51",
    "1.2.3",
    "0",
    "",
    "no digits here",
    "TWR 1.5",
    "Delta-v: 3,456.7 m/s",
    "A 1 B 2 C 3 D 4 E 5 F 6 G 7 H 8",
    "1 2 3 4 5 6 7 8 9",
    "1e5",
    "1.",
    ".5",
    "x-y-42",
    "Line 1\nLine 2",
    "<color=#FF00AA>12 ms</color>",
    "<b><color=#79C0FF>Cinematic</color></b>",
    "<color=#FF00AA>Cinematic</color>",
    "<size=14>Alt: 1,234 m</size>",
    "Toggle <<1>> on part 3",
    "Loading texture asset 1280/5736 (buffer=1536MB)",
    "ModuleManager: 42 patches applied (3.7 %)\n:AFTER[Squad]",
    "Total Run Time: 1234s",
    "Orbit: 100 km x 120 km",
    "a < b and 7",
    "<quad width=2>x</quad>",
    "-12 (x)",
    "50%",
    "1,000,000",
    "1,2,3,4,5,6,7,8",
    "1,2,3,4,5,6,7,8,9",
]


def _load_corpus(path, limit=300):
    rows = []
    try:
        with open(path, "r", encoding="utf-8") as handle:
            for line in handle:
                line = line.strip()
                if not line:
                    continue
                try:
                    row = json.loads(line)
                except ValueError:
                    continue
                text = row.get("s")
                if isinstance(text, str) and text:
                    rows.append(text)
                if len(rows) >= limit:
                    break
    except OSError:
        return []
    return rows


def write_golden(path, corpus_path=None, corpus_limit=300):
    """写门禁语料：(text, skeleton|null, numbers)。C# 侧单测逐条断言同口径。"""
    cases = []
    seen = set()
    for text in HANDCRAFTED:
        sk, nums = skeleton(text)
        cases.append({"t": text, "sk": sk, "nums": nums})
        seen.add(text)
    for text in _load_corpus(corpus_path, corpus_limit) if corpus_path else []:
        if text in seen:
            continue
        seen.add(text)
        sk, nums = skeleton(text)
        cases.append({"t": text, "sk": sk, "nums": nums})
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        for case in cases:
            handle.write(json.dumps(case, ensure_ascii=False, separators=(",", ":")) + "\n")
    return len(cases)


def selftest():
    checks = []

    def check(name, ok, detail=None):
        checks.append((name, bool(ok), detail))

    sk, nums = skeleton("Mass Flow: 2.20917 ton/s")
    check("单数字骨化", sk == "Mass Flow: # ton/s" and nums == ["2.20917"], (sk, nums))
    check("填回还原", fill_back(sk, nums) == "Mass Flow: 2.20917 ton/s", fill_back(sk, nums))
    sk, nums = skeleton("1.2.3")
    check("只吃一层分隔符", sk == "#.#" and nums == ["1.2", "3"], (sk, nums))
    sk, nums = skeleton("no digits here")
    check("无数字 -> None", sk is None and nums == [], (sk, nums))
    sk, nums = skeleton("1 2 3 4 5 6 7 8")
    check("8 段可行", sk == "# # # # # # # #" and len(nums) == 8, (sk, nums))
    sk, nums = skeleton("1 2 3 4 5 6 7 8 9")
    check("9 段放弃", sk is None and nums == [], (sk, nums))
    sk, nums = skeleton("<color=#FF00AA>12 ms</color>")
    check("标签内数字不骨化", sk == "<color=#FF00AA># ms</color>" and nums == ["12"], (sk, nums))
    check("标签内色值填回", fill_back(sk, nums) == "<color=#FF00AA>12 ms</color>", fill_back(sk, nums))
    sk, nums = skeleton("<b><color=#79C0FF>Cinematic</color></b>")
    check("纯标签无数字 -> None", sk is None and nums == [], (sk, nums))
    sk, nums = skeleton("a < b and 7")
    check("未知尖括号不吞数字", sk == "a < b and #" and nums == ["7"], (sk, nums))
    check("不符槽位的填回", fill_back("x # y", ["1", "2"]) is None, None)
    failed = [c for c in checks if not c[1]]
    for name, ok, detail in checks:
        if not ok:
            print("[selftest] FAIL {}: {}".format(name, detail))
    print("[selftest] {}/{} ok".format(len(checks) - len(failed), len(checks)))
    return not failed


def main(argv=None):
    parser = argparse.ArgumentParser(description="数字骨架（pattern）通道")
    parser.add_argument("--golden", default="", help="写门禁语料 jsonl 的路径")
    parser.add_argument("--corpus", default="", help="额外取语料的候选文件（packs/capture-candidates.jsonl）")
    parser.add_argument("--corpus-limit", type=int, default=300)
    parser.add_argument("--selftest", action="store_true")
    args = parser.parse_args(argv)

    ok = True
    if args.selftest or not (args.golden or args.corpus):
        ok = selftest() and ok
    if args.golden:
        count = write_golden(args.golden, args.corpus or None, args.corpus_limit)
        print("[skeleton] 门禁语料 {} 条 -> {}".format(count, args.golden))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())