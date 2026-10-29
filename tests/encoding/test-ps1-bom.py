#!/usr/bin/env python3
r"""仓库里的 .ps1 必须带 UTF-8 BOM（本文件只守这一件事）。

为什么不是「风格问题」：Windows PowerShell 5.1 的 `-File` 用系统 ANSI 码页（简体中文机器是 GBK）
读脚本。没有 BOM 时，脚本里的中文会被按 GBK 解码，一个汉字的双字节序列可能吃掉
紧随其后的引号/括号，于是整份文件 parse 失败 —— 报错位置还落在毫不相干的别处
（例如 `Unexpected token '^\s*Version\s*=\s*'` + 一大片 Missing closing '}'）。

这个坑在本仓库已经踩过两次：
  1. `src/UI18n.Runtime/release-checks.ps1` 从建库起就没有 BOM，一直没暴露，
     直到给它加第 16 条断言时 `dump-release-checks` 才炸；
  2. `tools.edit`（以及多数编辑器另存）会剥掉 BOM —— 改一次普通脚本就可能复发。
所以钉成测试，而不是写在文档里让人记住。

跑法：
    python tests/encoding/test-ps1-bom.py
退出码 0 = 全部带 BOM；1 = 有文件缺 BOM（会逐条列出，并给出修复命令）。
"""

import os
import sys

BOM = b"\xef\xbb\xbf"
SKIP_DIRS = {".git", "bin", "obj", "_scratch", "_audit", "build", "dist", "node_modules", "Library", "Temp"}


def repo_root():
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.dirname(os.path.dirname(here))


def iter_scripts(root):
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS and not d.startswith("."))
        for name in sorted(filenames):
            if name.lower().endswith(".ps1"):
                yield os.path.join(dirpath, name)


def check_file(path):
    """返回 None 表示合格，否则返回人话原因。"""
    with open(path, "rb") as handle:
        data = handle.read()
    if not data.startswith(BOM):
        return "缺少 UTF-8 BOM（首字节 %s）" % data[:3].hex()
    try:
        data[3:].decode("utf-8")
    except UnicodeDecodeError as error:
        return "BOM 之后不是合法 UTF-8：%s" % error
    return None


def main():
    root = repo_root()
    checks = 0
    failures = 0
    for path in iter_scripts(root):
        checks += 1
        reason = check_file(path)
        relative = os.path.relpath(path, root).replace(os.sep, "/")
        if reason is None:
            continue
        failures += 1
        print("[fail] %s: %s" % (relative, reason))
    print("[ps1-bom] checks=%d failures=%d" % (checks, failures))
    if failures:
        print("")
        print("修复：给每个 [fail] 文件开头补 EF BB BF，例如")
        print(r'  python -c "import io;p=r\'<file>.ps1\';d=open(p,\'rb\').read();open(p,\'wb\').write(b\'\xef\xbb\xbf\'+d if not d.startswith(b\'\xef\xbb\xbf\') else d)"')
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
