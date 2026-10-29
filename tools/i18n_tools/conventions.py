"""conventions.py -- 约定术语表与合法别名的**唯一**加载器。

为什么单独一个模块：这两张表曾在 `translate.py`（喂 prompt 用）和 `engine.py`（G7 门禁用）
各抄一份逐条字面量（12 条约定 + 11 条别名）。两份副本会漂：改一处忘一处，就会出现
「prompt 教模型写『远地点』、门禁却按『远拱点』放行」这种自相矛盾的状态。
现在两个模块都从这里读数据，代码里不再出现任何一条术语字面量。

数据（逐行一个 JSON 对象，UTF-8）：

    packs/conventions.jsonl         {"en": "Booster", "zh": "助推器"}
    packs/convention-aliases.jsonl  {"en": "Booster", "aliases": ["助推"], "why": "..."}

约定表**缺失或为空直接抛错**，不静默降级：G7 拿空表等于门禁失效，比缺文件更危险。
唯一例外是 `missing_ok=True`（translate_il 的 G6 豁免用它，语义是"没有表就永不豁免"）。
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Dict, Optional, Tuple, Union

_REPO = Path(__file__).resolve().parents[2]
PACKS_ROOT = _REPO / "packs"
CONVENTIONS_PATH = PACKS_ROOT / "conventions.jsonl"
ALIASES_PATH = PACKS_ROOT / "convention-aliases.jsonl"

PathArg = Optional[Union[str, Path]]


def _iter_rows(path: Path):
    with Path(path).open("r", encoding="utf-8-sig") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except ValueError:
                continue
            if isinstance(row, dict):
                yield row


def load_conventions(path: PathArg = None, missing_ok: bool = False) -> Dict[str, str]:
    """packs/conventions.jsonl -> {en: zh}，保持文件顺序（prompt 逐条列出，顺序有影响）。"""
    p = Path(path) if path is not None else CONVENTIONS_PATH
    if not p.is_file():
        if missing_ok:
            return {}
        raise FileNotFoundError(
            "约定术语表缺失: " + str(p) + " —— G7 门禁与 prompt 都依赖它，空跑等于放行所有术语漂移")
    table: Dict[str, str] = {}
    for row in _iter_rows(p):
        en, zh = row.get("en"), row.get("zh")
        if isinstance(en, str) and isinstance(zh, str) and en.strip() and zh.strip():
            table[en] = zh
    if not table and not missing_ok:
        raise ValueError("约定术语表为空: " + str(p))
    return table


def convention_pairs(path: PathArg = None) -> Tuple[Tuple[str, str], ...]:
    """{(en, zh)} 有序元组，直接喂给 prompt 拼装与 G7。"""
    return tuple(load_conventions(path).items())


def load_aliases(path: PathArg = None, missing_ok: bool = False) -> Dict[str, Tuple[str, ...]]:
    """packs/convention-aliases.jsonl -> {en: (合法同义译法, ...)}。

    别名是「消假失败」用的白名单，**不要为某个短语级术语临时加一条**：那样每遇新术语就要补，
    等于把门禁的错误编码成白名单（2026-10-28 试过，36 行里 25 行都能靠加别名"修好"）。
    短语级漂移交给 backend.sync_pack 的 term_drift 放行。
    """
    p = Path(path) if path is not None else ALIASES_PATH
    if not p.is_file():
        if missing_ok:
            return {}
        raise FileNotFoundError("约定别名表缺失: " + str(p))
    table: Dict[str, Tuple[str, ...]] = {}
    for row in _iter_rows(p):
        en, aliases = row.get("en"), row.get("aliases")
        if isinstance(en, str) and en.strip() and isinstance(aliases, list):
            cleaned = tuple(str(a).strip() for a in aliases if isinstance(a, str) and a.strip())
            if cleaned:
                table[en] = cleaned
    return table
