"""ilmeta.py -- 最小可用的 PE/CLI 元数据读取器 + IL 指令解码器（只用标准库）。

为什么自己写：本项目硬约束是「必须真解 IL 指令，不许正则扫字节码」——正则扫字节码会把
操作数里的字节当成指令，误报会让翻译流水线白烧钱（见 reports/il-reachability.md §0）。
这里实现到「够用即止」：

  * PE：DOS 头 -> PE 签名 -> COFF 头 -> 可选头（PE32 / PE32+）-> 节表 -> RVA 到文件偏移；
  * CLI：CLI header（数据目录第 14 项）-> 元数据根 -> 流表（#~ #Strings #US #Blob #GUID）；
  * 表流：Valid/Sorted 位图 -> 每张表行数 -> 按 ECMA-335 II.22 的字段 schema 算行宽 ->
    定位每张表数据区 -> 逐行解码（堆索引按 2/4 字节自适应，coded index 按 tag 位数自适应）；
  * 方法体：tiny / fat 头（fat 头按 headerSize 跳过额外数据）-> 字节码；
  * IL：一字节 + 0xFE 双字节操作码表，含操作数长度（u1/i1/u2/i4/i8/r4/r8/token/switch 变长）。

刻意不做（本项目用不到，写出来只增加出错面）：
  * 泛型实例化展开、TypeSpec 语义、异常处理子句（handler 内代码不参与数据流分析）；
  * 签名语义（只给「参数个数 / 参数类型描述串」，够判 sink 用；语义解析在 ilstrings.py）。

解码正确性自检：decode_il 必须正好落到 codeSize（多一个字节或少一个字节都抛 IlError）。
全量扫描时 stats['decode_fail'] 就是「操作码表没写错」的实测证据。
"""

from __future__ import annotations

import struct

# ---------------------------------------------------------------------------
# 元数据表 schema（ECMA-335 II.22）。顺序即表号 0x00..0x2C，不能改。
# 字段类型：u8/u16/u32/u64 定长；str/blob/guid 走堆索引；idx:表名 直接索引；
#          coded:编码名 编码索引（tag 位数见 CODED）。
# ---------------------------------------------------------------------------
TABLES = (
    ("Module", (("Generation", "u16"), ("Name", "str"), ("Mvid", "guid"),
                ("EncId", "guid"), ("EncBaseId", "guid"))),
    ("TypeRef", (("ResolutionScope", "coded:ResolutionScope"), ("Name", "str"),
                 ("Namespace", "str"))),
    ("TypeDef", (("Flags", "u32"), ("Name", "str"), ("Namespace", "str"),
                 ("Extends", "coded:TypeDefOrRef"), ("FieldList", "idx:Field"),
                 ("MethodList", "idx:MethodDef"))),
    ("FieldPtr", (("Field", "idx:Field"),)),
    ("Field", (("Flags", "u16"), ("Name", "str"), ("Signature", "blob"))),
    ("MethodPtr", (("Method", "idx:MethodDef"),)),
    ("MethodDef", (("RVA", "u32"), ("ImplFlags", "u16"), ("Flags", "u16"),
                   ("Name", "str"), ("Signature", "blob"), ("ParamList", "idx:Param"))),
    ("ParamPtr", (("Param", "idx:Param"),)),
    ("Param", (("Flags", "u16"), ("Sequence", "u16"), ("Name", "str"))),
    ("InterfaceImpl", (("Class", "idx:TypeDef"), ("Interface", "coded:TypeDefOrRef"))),
    ("MemberRef", (("Class", "coded:MemberRefParent"), ("Name", "str"),
                   ("Signature", "blob"))),
    ("Constant", (("Type", "u8"), ("Padding", "u8"), ("Parent", "coded:HasConstant"),
                  ("Value", "blob"))),
    ("CustomAttribute", (("Parent", "coded:HasCustomAttribute"),
                         ("Type", "coded:CustomAttributeType"), ("Value", "blob"))),
    ("FieldMarshal", (("Parent", "coded:HasFieldMarshal"), ("NativeType", "blob"))),
    ("DeclSecurity", (("Action", "u16"), ("Parent", "coded:HasDeclSecurity"),
                      ("PermissionSet", "blob"))),
    ("ClassLayout", (("PackingSize", "u16"), ("ClassSize", "u32"),
                     ("Parent", "idx:TypeDef"))),
    ("FieldLayout", (("Offset", "u32"), ("Field", "idx:Field"))),
    ("StandAloneSig", (("Signature", "blob"),)),
    ("EventMap", (("Parent", "idx:TypeDef"), ("EventList", "idx:Event"))),
    ("EventPtr", (("Event", "idx:Event"),)),
    ("Event", (("EventFlags", "u16"), ("Name", "str"),
               ("EventType", "coded:TypeDefOrRef"))),
    ("PropertyMap", (("Parent", "idx:TypeDef"), ("PropertyList", "idx:Property"))),
    ("PropertyPtr", (("Property", "idx:Property"),)),
    ("Property", (("Flags", "u16"), ("Name", "str"), ("Type", "blob"))),
    ("MethodSemantics", (("Semantics", "u16"), ("Method", "idx:MethodDef"),
                         ("Association", "coded:HasSemantics"))),
    ("MethodImpl", (("Class", "idx:TypeDef"), ("MethodBody", "coded:MethodDefOrRef"),
                    ("MethodDeclaration", "coded:MethodDefOrRef"))),
    ("ModuleRef", (("Name", "str"),)),
    ("TypeSpec", (("Signature", "blob"),)),
    ("ImplMap", (("MappingFlags", "u16"), ("MemberForwarded", "coded:MemberForwarded"),
                 ("ImportName", "str"), ("ImportScope", "idx:ModuleRef"))),
    ("FieldRVA", (("RVA", "u32"), ("Field", "idx:Field"))),
    ("ENCLog", (("Token", "u32"), ("FuncCode", "u32"))),
    ("ENCMap", (("Token", "u32"),)),
    ("Assembly", (("HashAlgId", "u32"), ("MajorVersion", "u16"), ("MinorVersion", "u16"),
                  ("BuildNumber", "u16"), ("RevisionNumber", "u16"), ("Flags", "u32"),
                  ("PublicKey", "blob"), ("Name", "str"), ("Culture", "str"))),
    ("AssemblyProcessor", (("Processor", "u32"),)),
    ("AssemblyOS", (("OSPlatformID", "u32"), ("OSMajorVersion", "u32"),
                    ("OSMinorVersion", "u32"))),
    ("AssemblyRef", (("MajorVersion", "u16"), ("MinorVersion", "u16"),
                     ("BuildNumber", "u16"), ("RevisionNumber", "u16"), ("Flags", "u32"),
                     ("PublicKeyOrToken", "blob"), ("Name", "str"), ("Culture", "str"),
                     ("HashValue", "blob"))),
    ("AssemblyRefProcessor", (("Processor", "u32"), ("AssemblyRef", "idx:AssemblyRef"))),
    ("AssemblyRefOS", (("OSPlatformID", "u32"), ("OSMajorVersion", "u32"),
                       ("OSMinorVersion", "u32"), ("AssemblyRef", "idx:AssemblyRef"))),
    ("File", (("Flags", "u32"), ("Name", "str"), ("HashValue", "blob"))),
    ("ExportedType", (("Flags", "u32"), ("TypeDefId", "u32"), ("TypeName", "str"),
                      ("TypeNamespace", "str"), ("Implementation", "coded:Implementation"))),
    ("ManifestResource", (("Offset", "u32"), ("Flags", "u32"), ("Name", "str"),
                          ("Implementation", "coded:Implementation"))),
    ("NestedClass", (("NestedClass", "idx:TypeDef"), ("EnclosingClass", "idx:TypeDef"))),
    ("GenericParam", (("Number", "u16"), ("Flags", "u16"),
                      ("Owner", "coded:TypeOrMethodDef"), ("Name", "str"))),
    ("MethodSpec", (("Method", "coded:MethodDefOrRef"), ("Instantiation", "blob"))),
    ("GenericParamConstraint", (("Owner", "idx:GenericParam"),
                                ("Constraint", "coded:TypeDefOrRef"))),
)

TABLE_INDEX = {name: i for i, (name, _) in enumerate(TABLES)}

# coded index：编码名 -> (tag 位数, 各 tag 对应的表名)。None 表示该 tag 保留不用。
CODED = {
    "TypeDefOrRef": (2, ("TypeDef", "TypeRef", "TypeSpec")),
    "HasConstant": (2, ("Field", "Param", "Property")),
    "HasCustomAttribute": (5, (
        "MethodDef", "Field", "TypeRef", "TypeDef", "Param", "InterfaceImpl",
        "MemberRef", "Module", "DeclSecurity", "Property", "Event", "StandAloneSig",
        "ModuleRef", "TypeSpec", "Assembly", "AssemblyRef", "File", "ExportedType",
        "ManifestResource", "GenericParam", "GenericParamConstraint", "MethodSpec")),
    "HasFieldMarshal": (1, ("Field", "Param")),
    "HasDeclSecurity": (2, ("TypeDef", "MethodDef", "Assembly")),
    "MemberRefParent": (3, ("TypeDef", "TypeRef", "ModuleRef", "MethodDef", "TypeSpec")),
    "HasSemantics": (1, ("Event", "Property")),
    "MethodDefOrRef": (1, ("MethodDef", "MemberRef")),
    "MemberForwarded": (1, ("Field", "MethodDef")),
    "Implementation": (2, ("File", "AssemblyRef", "ExportedType")),
    "CustomAttributeType": (3, (None, None, "MethodDef", "MemberRef", None)),
    "ResolutionScope": (2, ("Module", "ModuleRef", "AssemblyRef", "TypeRef")),
    "TypeOrMethodDef": (1, ("TypeDef", "MethodDef")),
}


class IlError(Exception):
    """元数据 / 字节码结构错误（调用方决定是整文件放弃还是单方法跳过）。"""


# ---------------------------------------------------------------------------
# 小工具
# ---------------------------------------------------------------------------
def _u16(d, off):
    return struct.unpack_from("<H", d, off)[0]


def _u32(d, off):
    return struct.unpack_from("<I", d, off)[0]


def _u64(d, off):
    return struct.unpack_from("<Q", d, off)[0]


def read_compressed_uint(d, off):
    """ECMA-335 II.23.2 压缩无符号整数。返回 (值, 占用字节数)。"""
    b0 = d[off]
    if b0 & 0x80 == 0:
        return b0, 1
    if b0 & 0xC0 == 0x80:
        return ((b0 & 0x3F) << 8) | d[off + 1], 2
    if b0 & 0xE0 == 0xC0:
        return (((b0 & 0x1F) << 24) | (d[off + 1] << 16)
                | (d[off + 2] << 8) | d[off + 3]), 4
    raise IlError("非法压缩整数前缀 0x%02x @%d" % (b0, off))


# ---------------------------------------------------------------------------
# IL 操作码表
# ---------------------------------------------------------------------------
_OPERAND_SIZE = {"u1": 1, "i1": 1, "u2": 2, "i2": 2, "i4": 4, "u4": 4, "tok": 4,
                 "i8": 8, "r8": 8, "r4": 4}


def _build_opcodes():
    one = {}
    two = {}

    def add(table, code, name, kind=""):
        table[code] = (name, kind)

    t = one
    add(t, 0x00, "nop")
    add(t, 0x01, "break")
    for i in range(4):
        add(t, 0x02 + i, "ldarg." + str(i))
    for i in range(4):
        add(t, 0x06 + i, "ldloc." + str(i))
    for i in range(4):
        add(t, 0x0A + i, "stloc." + str(i))
    add(t, 0x0E, "ldarg.s", "u1")
    add(t, 0x0F, "ldarga.s", "u1")
    add(t, 0x10, "starg.s", "u1")
    add(t, 0x11, "ldloc.s", "u1")
    add(t, 0x12, "ldloca.s", "u1")
    add(t, 0x13, "stloc.s", "u1")
    add(t, 0x14, "ldnull")
    add(t, 0x15, "ldc.i4.m1")
    for i in range(9):
        add(t, 0x16 + i, "ldc.i4." + str(i))
    add(t, 0x1F, "ldc.i4.s", "i1")
    add(t, 0x20, "ldc.i4", "i4")
    add(t, 0x21, "ldc.i8", "i8")
    add(t, 0x22, "ldc.r4", "r4")
    add(t, 0x23, "ldc.r8", "r8")
    add(t, 0x25, "dup")
    add(t, 0x26, "pop")
    add(t, 0x27, "jmp", "tok")
    add(t, 0x28, "call", "tok")
    add(t, 0x29, "calli", "tok")
    add(t, 0x2A, "ret")
    for i, nm in enumerate(("br.s", "brfalse.s", "brtrue.s", "beq.s", "bge.s", "bgt.s",
                            "ble.s", "blt.s", "bne.un.s", "bge.un.s", "bgt.un.s",
                            "ble.un.s", "blt.un.s")):
        add(t, 0x2B + i, nm, "i1")
    for i, nm in enumerate(("br", "brfalse", "brtrue", "beq", "bge", "bgt", "ble", "blt",
                            "bne.un", "bge.un", "bgt.un", "ble.un", "blt.un")):
        add(t, 0x38 + i, nm, "i4")
    add(t, 0x45, "switch", "switch")
    for i, nm in enumerate(("ldind.i1", "ldind.u1", "ldind.i2", "ldind.u2", "ldind.i4",
                            "ldind.u4", "ldind.i8", "ldind.i", "ldind.r4", "ldind.r8",
                            "ldind.ref")):
        add(t, 0x46 + i, nm)
    for i, nm in enumerate(("stind.ref", "stind.i1", "stind.i2", "stind.i4", "stind.i8",
                            "stind.r4", "stind.r8")):
        add(t, 0x51 + i, nm)
    for i, nm in enumerate(("add", "sub", "mul", "div", "div.un", "rem", "rem.un", "and",
                            "or", "xor", "shl", "shr", "shr.un", "neg", "not")):
        add(t, 0x58 + i, nm)
    for i, nm in enumerate(("conv.i1", "conv.i2", "conv.i4", "conv.i8", "conv.r4",
                            "conv.r8", "conv.u4", "conv.u8")):
        add(t, 0x67 + i, nm)
    add(t, 0x6F, "callvirt", "tok")
    add(t, 0x70, "cpobj", "tok")
    add(t, 0x71, "ldobj", "tok")
    add(t, 0x72, "ldstr", "tok")
    add(t, 0x73, "newobj", "tok")
    add(t, 0x74, "castclass", "tok")
    add(t, 0x75, "isinst", "tok")
    add(t, 0x76, "conv.r.un")
    add(t, 0x79, "unbox", "tok")
    add(t, 0x7A, "throw")
    add(t, 0x7B, "ldfld", "tok")
    add(t, 0x7C, "ldflda", "tok")
    add(t, 0x7D, "stfld", "tok")
    add(t, 0x7E, "ldsfld", "tok")
    add(t, 0x7F, "ldsflda", "tok")
    add(t, 0x80, "stsfld", "tok")
    add(t, 0x81, "stobj", "tok")
    for i, nm in enumerate(("conv.ovf.i1.un", "conv.ovf.i2.un", "conv.ovf.i4.un",
                            "conv.ovf.i8.un", "conv.ovf.u1.un", "conv.ovf.u2.un",
                            "conv.ovf.u4.un", "conv.ovf.u8.un", "conv.ovf.i.un",
                            "conv.ovf.u.un")):
        add(t, 0x82 + i, nm)
    add(t, 0x8C, "box", "tok")
    add(t, 0x8D, "newarr", "tok")
    add(t, 0x8E, "ldlen")
    add(t, 0x8F, "ldelema", "tok")
    for i, nm in enumerate(("ldelema", "ldelema", "ldelema", "ldelema")):
        pass
    for i, nm in enumerate(("ldelem.i1", "ldelem.u1", "ldelem.i2", "ldelem.u2",
                            "ldelem.i4", "ldelem.u4", "ldelem.i8", "ldelem.i",
                            "ldelem.r4", "ldelem.r8", "ldelem.ref")):
        add(t, 0x90 + i, nm)
    for i, nm in enumerate(("stelem.i", "stelem.i1", "stelem.i2", "stelem.i4", "stelem.i8",
                            "stelem.r4", "stelem.r8", "stelem.ref")):
        add(t, 0x9B + i, nm)
    add(t, 0xA3, "ldelem", "tok")
    add(t, 0xA4, "stelem", "tok")
    add(t, 0xA5, "unbox.any", "tok")
    for i, nm in enumerate(("conv.ovf.i1", "conv.ovf.i2", "conv.ovf.i4", "conv.ovf.i8",
                            "conv.ovf.u1", "conv.ovf.u2", "conv.ovf.u4", "conv.ovf.u8")):
        add(t, 0xB3 + i, nm)
    add(t, 0xC2, "refanyval", "tok")
    add(t, 0xC3, "ckfinite")
    add(t, 0xC6, "mkrefany", "tok")
    add(t, 0xD0, "ldtoken", "tok")
    add(t, 0xD1, "conv.u2")
    add(t, 0xD2, "conv.u1")
    add(t, 0xD3, "conv.i")
    add(t, 0xD4, "conv.ovf.i")
    add(t, 0xD5, "conv.ovf.u")
    add(t, 0xD6, "add.ovf")
    add(t, 0xD7, "add.ovf.un")
    add(t, 0xD8, "mul.ovf")
    add(t, 0xD9, "mul.ovf.un")
    add(t, 0xDA, "sub.ovf")
    add(t, 0xDB, "sub.ovf.un")
    add(t, 0xDC, "endfinally")
    add(t, 0xDD, "leave", "i4")
    add(t, 0xDE, "leave.s", "i1")
    add(t, 0xDF, "stind.i")
    add(t, 0xE0, "conv.u")

    u = two
    add(u, 0x00, "arglist")
    add(u, 0x01, "ceq")
    add(u, 0x02, "cgt")
    add(u, 0x03, "cgt.un")
    add(u, 0x04, "clt")
    add(u, 0x05, "clt.un")
    add(u, 0x06, "ldftn", "tok")
    add(u, 0x07, "ldvirtftn", "tok")
    add(u, 0x09, "ldarg", "u2")
    add(u, 0x0A, "ldarga", "u2")
    add(u, 0x0B, "starg", "u2")
    add(u, 0x0C, "ldloc", "u2")
    add(u, 0x0D, "ldloca", "u2")
    add(u, 0x0E, "stloc", "u2")
    add(u, 0x0F, "localloc")
    add(u, 0x11, "endfilter")
    add(u, 0x12, "unaligned.", "u1")
    add(u, 0x13, "volatile.")
    add(u, 0x14, "tail.")
    add(u, 0x15, "initobj", "tok")
    add(u, 0x16, "constrained.", "tok")
    add(u, 0x17, "cpblk")
    add(u, 0x18, "initblk")
    add(u, 0x19, "no.", "u1")
    add(u, 0x1A, "rethrow")
    add(u, 0x1C, "sizeof", "tok")
    add(u, 0x1D, "refanytype")
    add(u, 0x1E, "readonly.")
    return one, two


_OP1, _OP2 = _build_opcodes()

# 栈效果分类：把「只影响栈、不影响我们关心的值」的操作码分组，解释器按组处理。
_BINARY = frozenset(("add", "sub", "mul", "div", "div.un", "rem", "rem.un", "and", "or",
                     "xor", "shl", "shr", "shr.un", "add.ovf", "add.ovf.un", "mul.ovf",
                     "mul.ovf.un", "sub.ovf", "sub.ovf.un", "ceq", "cgt", "cgt.un", "clt",
                     "clt.un"))
_UNARY = frozenset(("neg", "not", "conv.i1", "conv.i2", "conv.i4", "conv.i8", "conv.r4",
                    "conv.r8", "conv.u4", "conv.u8", "conv.r.un", "conv.ovf.i1.un",
                    "conv.ovf.i2.un", "conv.ovf.i4.un", "conv.ovf.i8.un", "conv.ovf.u1.un",
                    "conv.ovf.u2.un", "conv.ovf.u4.un", "conv.ovf.u8.un", "conv.ovf.i.un",
                    "conv.ovf.u.un", "conv.ovf.i1", "conv.ovf.i2", "conv.ovf.i4",
                    "conv.ovf.i8", "conv.ovf.u1", "conv.ovf.u2", "conv.ovf.u4",
                    "conv.ovf.u8", "conv.ovf.i", "conv.ovf.u", "conv.u2", "conv.u1",
                    "conv.i", "conv.u", "ckfinite", "ldlen", "refanytype"))
_LOAD_IND = frozenset(("ldind.i1", "ldind.u1", "ldind.i2", "ldind.u2", "ldind.i4",
                       "ldind.u4", "ldind.i8", "ldind.i", "ldind.r4", "ldind.r8",
                       "ldind.ref"))
_STORE_IND = frozenset(("stind.ref", "stind.i1", "stind.i2", "stind.i4", "stind.i8",
                        "stind.r4", "stind.r8", "stind.i"))
_LOAD_ELEM = frozenset(("ldelem.i1", "ldelem.u1", "ldelem.i2", "ldelem.u2", "ldelem.i4",
                        "ldelem.u4", "ldelem.i8", "ldelem.i", "ldelem.r4", "ldelem.r8",
                        "ldelem.ref"))
_STORE_ELEM = frozenset(("stelem.i", "stelem.i1", "stelem.i2", "stelem.i4", "stelem.i8",
                         "stelem.r4", "stelem.r8", "stelem.ref"))
_PREFIX = frozenset(("unaligned.", "volatile.", "tail.", "readonly.", "constrained.",
                     "no."))
_TERMINATOR = frozenset(("ret", "throw", "rethrow", "endfinally", "endfilter", "jmp"))

# 分支操作码 -> 需要先弹几个操作数（br 不弹，条件跳转弹 1 或 2）
_BRANCH_POPS = {
    "br": 0, "br.s": 0, "leave": 0, "leave.s": 0,
    "brtrue": 1, "brtrue.s": 1, "brfalse": 1, "brfalse.s": 1,
    "beq": 2, "beq.s": 2, "bge": 2, "bge.s": 2, "bgt": 2, "bgt.s": 2, "ble": 2,
    "ble.s": 2, "blt": 2, "blt.s": 2, "bne.un": 2, "bne.un.s": 2, "bge.un": 2,
    "bge.un.s": 2, "bgt.un": 2, "bgt.un.s": 2, "ble.un": 2, "ble.un.s": 2, "blt.un": 2,
    "blt.un.s": 2,
}
_SHORT_BRANCH = frozenset(("br.s", "brtrue.s", "brfalse.s", "beq.s", "bge.s", "bgt.s",
                           "ble.s", "blt.s", "bne.un.s", "bge.un.s", "bgt.un.s",
                           "ble.un.s", "blt.un.s", "leave.s"))


def decode_il(code):
    """把方法体字节码解成 [(offset, 操作码名, 操作数)]。解不出/落到 codeSize 之外抛 IlError。"""
    insns = []
    pos = 0
    n = len(code)
    while pos < n:
        start = pos
        op = code[pos]
        pos += 1
        if op == 0xFE:
            if pos >= n:
                raise IlError("0xFE 前缀后没有第二个字节 @%d" % start)
            op2 = code[pos]
            pos += 1
            entry = _OP2.get(op2)
            if entry is None:
                raise IlError("未知双字节操作码 0xFE%02X @%d" % (op2, start))
        else:
            entry = _OP1.get(op)
            if entry is None:
                raise IlError("未知操作码 0x%02X @%d" % (op, start))
        name, kind = entry
        operand = None
        if kind == "switch":
            cnt = _u32(code, pos)
            pos += 4
            if pos + 4 * cnt > n:
                raise IlError("switch 目标表越界 @%d" % start)
            operand = [_u32(code, pos + 4 * i) for i in range(cnt)]
            pos += 4 * cnt
        elif kind:
            size = _OPERAND_SIZE[kind]
            if pos + size > n:
                raise IlError("操作数越界 @%d (%s)" % (start, name))
            if kind in ("i4", "u4", "tok", "r4"):
                operand = _u32(code, pos)
            elif kind in ("i8", "r8"):
                operand = _u64(code, pos)
            elif kind in ("i1", "u1"):
                operand = code[pos]
            elif kind == "u2":
                operand = _u16(code, pos)
            pos += size
        insns.append((start, name, operand))
    return insns


class Image:
    """一个托管程序集（.dll）的只读视图。"""

    def __init__(self, path):
        self.path = path
        with open(path, "rb") as fh:
            self.data = fh.read()
        self._strings = {}
        self._row_cache = {}
        self._parse_pe()
        self._parse_cli()
        self._parse_tables()

    # -- PE ---------------------------------------------------------------
    def _parse_pe(self):
        d = self.data
        if len(d) < 0x40 or d[:2] != b"MZ":
            raise IlError("不是 PE 文件（缺 MZ）: " + self.path)
        pe = _u32(d, 0x3C)
        if d[pe:pe + 4] != b"PE\0\0":
            raise IlError("PE 签名不对: " + self.path)
        coff = pe + 4
        nsec = _u16(d, coff + 2)
        opt_size = _u16(d, coff + 16)
        opt = coff + 20
        magic = _u16(d, opt)
        if magic == 0x10B:
            dd = opt + 96
        elif magic == 0x20B:
            dd = opt + 112
        else:
            raise IlError("未知可选头 magic 0x%04X" % magic)
        self.cli_rva = _u32(d, dd + 14 * 8)
        self.cli_size = _u32(d, dd + 14 * 8 + 4)
        if self.cli_rva == 0:
            raise IlError("不是托管程序集（数据目录 14 = 0）: " + self.path)
        sec = opt + opt_size
        self.sections = []
        for i in range(nsec):
            base = sec + i * 40
            self.sections.append((_u32(d, base + 12), _u32(d, base + 8),
                                  _u32(d, base + 20), _u32(d, base + 16)))
            # (VirtualAddress, VirtualSize, PointerToRawData, SizeOfRawData)

    def rva_to_off(self, rva):
        for va, vsize, rawptr, rawsize in self.sections:
            if va <= rva < va + max(vsize, rawsize):
                return rawptr + (rva - va)
        raise IlError("RVA 0x%X 不在任何节里: %s" % (rva, self.path))

    # -- CLI 头 + 元数据根 -------------------------------------------------
    def _parse_cli(self):
        d = self.data
        cli = self.rva_to_off(self.cli_rva)
        md_rva = _u32(d, cli + 8)
        md = self.rva_to_off(md_rva)
        if _u32(d, md) != 0x424A5342:
            raise IlError("元数据签名不对: " + self.path)
        ver_len = _u32(d, md + 12)
        off = md + 16 + ((ver_len + 3) & ~3)
        nstreams = _u16(d, off + 2)
        off += 4
        self.streams = {}
        for _ in range(nstreams):
            s_off = _u32(d, off)
            s_size = _u32(d, off + 4)
            off += 8
            name_end = d.index(b"\0", off)
            name = d[off:name_end].decode("ascii", "replace")
            off = name_end + 1
            off += (-off) % 4
            self.streams[name] = (md + s_off, s_size)

    def _parse_tables(self):
        d = self.data
        key = "#~" if "#~" in self.streams else "#-"
        if key not in self.streams:
            raise IlError("没有表流（#~ / #-）: " + self.path)
        base, size = self.streams[key]
        heap_sizes = d[base + 6]
        self.heap_sizes = heap_sizes
        valid = _u64(d, base + 8)
        p = base + 24
        counts = []
        for i in range(64):
            if (valid >> i) & 1:
                counts.append(_u32(d, p))
                p += 4
            else:
                counts.append(0)
        self.rowcounts = counts
        self.rowfields = []
        self.row_sizes = []
        for i, (_, schema) in enumerate(TABLES):
            off = 0
            fields = []
            for fname, kind in schema:
                fsize = self._field_size(kind)
                fields.append((fname, kind, off, fsize))
                off += fsize
            self.rowfields.append(fields)
            self.row_sizes.append(off)
        self.table_off = []
        data = p
        for i in range(len(TABLES)):
            self.table_off.append(data)
            if counts[i]:
                data += self.row_sizes[i] * counts[i]
        self.strings_off = self.streams.get("#Strings", (0, 0))[0]
        self.us_off, self.us_size = self.streams.get("#US", (0, 0))
        self.blob_off = self.streams.get("#Blob", (0, 0))[0]

    def _field_size(self, kind):
        if kind == "u8":
            return 1
        if kind == "u16":
            return 2
        if kind == "u32":
            return 4
        if kind == "u64":
            return 8
        if kind == "str":
            return 4 if self.heap_sizes & 0x01 else 2
        if kind == "guid":
            return 4 if self.heap_sizes & 0x02 else 2
        if kind == "blob":
            return 4 if self.heap_sizes & 0x04 else 2
        if kind.startswith("idx:"):
            return 4 if self.rowcounts[TABLE_INDEX[kind[4:]]] >= 0x10000 else 2
        if kind.startswith("coded:"):
            bits, names = CODED[kind[6:]]
            biggest = 0
            for nm in names:
                if nm is not None:
                    biggest = max(biggest, self.rowcounts[TABLE_INDEX[nm]])
            return 4 if biggest >= (1 << (16 - bits)) else 2
        raise IlError("未知字段类型 " + kind)

    # -- 堆访问 ------------------------------------------------------------
    def string_at(self, off):
        got = self._strings.get(off)
        if got is not None:
            return got
        if not off:
            return ""
        start = self.strings_off + off
        end = self.data.index(b"\0", start)
        text = self.data[start:end].decode("utf-8", "replace")
        self._strings[off] = text
        return text

    def blob_at(self, off):
        if not off:
            return b""
        start = self.blob_off + off
        length, used = read_compressed_uint(self.data, start)
        return self.data[start + used:start + used + length]

    def us_literals(self):
        """#US 堆 -> {堆内偏移: 文本}。用户字符串字面量全在这里（ldstr 的操作数即 0x70|偏移）。"""
        out = {}
        d = self.data
        if not self.us_off:
            return out
        pos = self.us_off
        end = self.us_off + self.us_size
        while pos < end:
            off = pos - self.us_off
            try:
                length, used = read_compressed_uint(d, pos)
            except IlError:
                break
            if length == 0:
                pos += used
                continue
            pos += used
            if pos + length > end:
                break
            raw = d[pos:pos + length]
            pos += length
            out[off] = raw[:-1].decode("utf-16-le", "replace")
        return out

    # -- 表访问 ------------------------------------------------------------
    def rows(self, name):
        cached = self._row_cache.get(name)
        if cached is not None:
            return cached
        i = TABLE_INDEX[name]
        count = self.rowcounts[i]
        base = self.table_off[i]
        size = self.row_sizes[i]
        fields = self.rowfields[i]
        d = self.data
        out = []
        for rid in range(1, count + 1):
            pos = base + (rid - 1) * size
            row = {}
            for fname, kind, foff, fsize in fields:
                p = pos + foff
                if kind == "u8":
                    v = d[p]
                elif kind == "u16":
                    v = _u16(d, p)
                elif kind == "u32":
                    v = _u32(d, p)
                elif kind == "u64":
                    v = _u64(d, p)
                elif kind in ("str", "blob", "guid"):
                    v = _u16(d, p) if fsize == 2 else _u32(d, p)
                elif kind.startswith("idx:"):
                    v = _u16(d, p) if fsize == 2 else _u32(d, p)
                elif kind.startswith("coded:"):
                    raw = _u16(d, p) if fsize == 2 else _u32(d, p)
                    bits, names = CODED[kind[6:]]
                    tag = raw & ((1 << bits) - 1)
                    tn = names[tag] if tag < len(names) else None
                    v = (tn, raw >> bits)
                else:
                    raise IlError(kind)
                row[fname] = v
            out.append(row)
        self._row_cache[name] = out
        return out

    def row(self, name, rid):
        return self.rows(name)[rid - 1]

    # -- 方法体 ------------------------------------------------------------
    def method_body(self, rva):
        """返回 (字节码 bytes, 局部变量签名 token)。tiny / fat 头都处理。"""
        d = self.data
        off = self.rva_to_off(rva)
        b0 = d[off]
        if b0 & 0x03 == 0x02:
            code_size = b0 >> 2
            return d[off + 1:off + 1 + code_size], 0
        if b0 & 0x03 == 0x03:
            flags_size = _u16(d, off)
            header = (flags_size >> 12) * 4
            code_size = _u32(d, off + 4)
            local_sig = _u32(d, off + 8)
            return d[off + header:off + header + code_size], local_sig
        raise IlError("方法头保留位 0x%02X @0x%X" % (b0, off))


def open_image(path):
    """打开一个 DLL；不是托管程序集时抛 IlError（调用方计入 skipped）。"""
    return Image(path)
