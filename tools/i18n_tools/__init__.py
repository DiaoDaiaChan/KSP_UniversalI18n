r"""UI18n 数据流水线工具包（python 3.14 标准库，零三方依赖）。

模块：
    fnv        FNV-1a 64 文本哈希（与 C# 侧 T1 的 Fnv1a 逐字节一致）
    cfgparse   KSP cfg 轻量解析（读节点/键值，够用就好）
    textnorm   归一化与文本特征位（ph / rt / dg / path / url / len）
    harvest    P0 抽取 + P1 归一化 + P2 过滤 → packs/pool.jsonl
    glossary   Squad 官方双语术语表 → packs/glossary.jsonl
    selftest   自测（python -m i18n_tools.selftest）

命令（在 UniversalI18n\tools 目录下执行）：
    python -m i18n_tools.selftest                 # 自测，退出码 0 即通过
    python -m i18n_tools.harvest                   # 抽取候选池 → ..\packs\pool.jsonl
    python -m i18n_tools.glossary                  # 术语表 → ..\packs\glossary.jsonl
"""

from __future__ import annotations

import sys

__all__ = ["fnv", "cfgparse", "textnorm", "harvest", "glossary", "ensure_stdout"]
__version__ = "0.1.0"


if sys.platform == "win32":
    import os as _os
    _orig_mkdir = _os.mkdir
    def _win_safe_mkdir(path, mode=0o777, *args, **kwargs):
        # Python 3.12+ Windows: 0o700 causes WinError 5 when creating child dirs in tempfile
        if mode == 0o700:
            mode = 0o777
        return _orig_mkdir(path, mode, *args, **kwargs)
    _os.mkdir = _win_safe_mkdir


def ensure_stdout() -> None:
    """Windows 控制台默认 GBK，打印中文/emoji 会 UnicodeEncodeError：统一改成 utf-8。"""
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):  # 被重定向成非 TextIOWrapper 时忽略
            pass
