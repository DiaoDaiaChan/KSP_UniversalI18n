"""FNV-1a 64 位哈希。

约定（跨语言唯一真相，改这里等于改数据契约）：
  * 输入是 UTF-8 字节，不是 UTF-16 也不是字符序号。
  * offset basis = 0xcbf29ce484222325，prime = 0x100000001b3，模 2^64。
  * 对外文本形式是 16 位小写十六进制（doc 02 §5.2 的 h 字段）。

C# 侧（UI18n.Core / probe）必须是同一算法，否则运行时按 textHash 查不到译文。
参考向量见 VECTORS 与 selftest。
"""

from __future__ import annotations

OFFSET_BASIS = 0xCBF29CE484222325
PRIME = 0x100000001B3
MASK64 = 0xFFFFFFFFFFFFFFFF

# (输入, 期望值)：空串即 offset basis，"a" / "foobar" 是 FNV 官方测试向量
VECTORS: tuple[tuple[str, int], ...] = (
    ("", 0xCBF29CE484222325),
    ("a", 0xAF63DC4C8601EC8C),
    ("foobar", 0x85944171F73967E8),
)


def fnv1a64_bytes(data: bytes, basis: int = OFFSET_BASIS) -> int:
    """对字节序列求 FNV-1a 64。"""
    h = basis
    for b in data:
        h = ((h ^ b) * PRIME) & MASK64
    return h


def hash_int(text: str) -> int:
    """文本 → 64 位整数哈希。UTF-8 编码后逐字节。"""
    return fnv1a64_bytes(text.encode("utf-8"))


def hash_text(text: str) -> str:
    """文本 → 16 位小写十六进制（落盘/日志用，也是 C# 侧的 h 字段）。"""
    return format(hash_int(text), "016x")


def hash_file(path: str) -> str:
    """文件内容 → 16 位小写十六进制（增量流水线判断产物是否过期用）。"""
    with open(path, "rb") as f:
        return format(fnv1a64_bytes(f.read()), "016x")
