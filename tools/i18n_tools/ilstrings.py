r"""ilstrings.py -- IL 字面量 -> UI 渲染 sink 可达性盘点（task-29）。

口径（写死在这里，报告与 selftest 都按它验）：
  * 取源：KSP_x64_Data\Managed\Assembly-CSharp.dll（mod = KSP） + GameData\**\*.dll（mod = GameData 下第一段目录）；
  * 只认「ldstr 直接或经 字符串拼接 / string.Format / GUIContent 构造 流向 sink 参数」，
    方法体内深度上限 1 层，**不跨方法**；允许穿过 string.Concat / string.Format /
    GUIContent..ctor / stloc+ldloc / dup / box；不允许静态字段、返回值、数组/字典、ToString()；
  * sink 白名单 = src/UI18n.Runtime/Patches/PatchRegistry.cs 的 16 个目标 + GUIContent..ctor；
  * Debug.Log* / KSPLog / 异常消息**不是** sink（那是日志不是 UI），单独计数做对照；
  * 真解 IL：ilmeta.decode_il 按操作码 + 操作数长度解码，解不出的方法计入 stats['decode_fail']。

产物：
  packs/il-candidates.jsonl  {asm,type,method,ilOffset,sink,text,h,n}（asm 内按 n 去重、按 asm+n 排序）
  reports/il-reachability.md 漏斗 / 逐 mod / 噪声 / 格式串 / 与 pool 交集 / token 估算 / API 普查

CLI：
  python -m i18n_tools.ilstrings [--gamedata DIR] [--managed DIR] [--out FILE] [--report FILE]
                                 [--only NAME] [--limit N] [--no-report] [--quiet]
"""

from __future__ import annotations

import json
import os
import re
import sys
import time

from . import ensure_stdout, fnv, ilmeta, textnorm

# ---------------------------------------------------------------------------
# 文本承载 API 表：(声明类型, 方法名) -> (组, 规范名)
#   A = PatchRegistry 已覆盖的 16 个 sink + GUIContent..ctor（"已覆盖"）
#   B = 疑似盲区（只统计与取证，不进候选）
#   LOG = 日志（对照，证明噪声规模）
#   REFL = 反射写字段（B 组里单列，靠"同方法里有 text/m_text 字面量"判定）
# ---------------------------------------------------------------------------
API_TABLE = {
    # --- A：已覆盖 ---
    ("UnityEngine.UI.Text", "set_text"): ("A", "UnityEngine.UI.Text.set_text"),
    ("TMPro.TMP_Text", "set_text"): ("A", "TMPro.TMP_Text.set_text"),
    ("TMPro.TextMeshPro", "set_text"): ("A", "TMPro.TMP_Text.set_text"),
    ("TMPro.TextMeshProUGUI", "set_text"): ("A", "TMPro.TMP_Text.set_text"),
    ("UnityEngine.GUI", "Label"): ("A", "GUI.Label"),
    ("UnityEngine.GUI", "Box"): ("A", "GUI.Box"),
    ("UnityEngine.GUI", "Button"): ("A", "GUI.Button"),
    ("UnityEngine.GUI", "Toggle"): ("A", "GUI.Toggle"),
    ("UnityEngine.GUI", "RepeatButton"): ("A", "GUI.RepeatButton"),
    ("UnityEngine.GUI", "Window"): ("A", "GUI.Window"),
    ("UnityEngine.GUI", "DoWindow"): ("A", "GUI.DoWindow"),
    ("UnityEngine.GUI", "DoModalWindow"): ("A", "GUI.DoModalWindow"),
    # GUILayout 的公开前门：mod 实际调的是 GUILayout.Label/Box/Window，
    # 运行时内部才转进 DoLabel/DoButton/DoWindow，所以静态调用点必须算进 A 组。
    ("UnityEngine.GUILayout", "Label"): ("A", "GUILayout.DoLabel"),
    ("UnityEngine.GUILayout", "Box"): ("A", "GUILayout.DoLabel"),
    ("UnityEngine.GUILayout", "Button"): ("A", "GUILayout.DoButton"),
    ("UnityEngine.GUILayout", "RepeatButton"): ("A", "GUILayout.DoRepeatButton"),
    ("UnityEngine.GUILayout", "Toggle"): ("A", "GUILayout.DoToggle"),
    ("UnityEngine.GUILayout", "Window"): ("A", "GUILayout.DoWindow"),
    ("UnityEngine.GUILayout", "DoLabel"): ("A", "GUILayout.DoLabel"),
    ("UnityEngine.GUILayout", "DoButton"): ("A", "GUILayout.DoButton"),
    ("UnityEngine.GUILayout", "DoRepeatButton"): ("A", "GUILayout.DoRepeatButton"),
    ("UnityEngine.GUILayout", "DoToggle"): ("A", "GUILayout.DoToggle"),
    ("UnityEngine.GUILayout", "DoWindow"): ("A", "GUILayout.DoWindow"),
    ("ScreenMessages", "PostMessage"): ("A", "ScreenMessages.PostMessage"),
    ("UnityEngine.GUIContent", ".ctor"): ("A", "GUIContent..ctor"),
    # --- B：疑似盲区 ---
    ("UnityEngine.TextMesh", "set_text"): ("B", "UnityEngine.TextMesh.set_text"),
    ("TMPro.TMP_Text", "SetText"): ("B", "TMPro.TMP_Text.SetText"),
    ("TMPro.TextMeshPro", "SetText"): ("B", "TMPro.TMP_Text.SetText"),
    ("TMPro.TextMeshProUGUI", "SetText"): ("B", "TMPro.TMP_Text.SetText"),
    ("TMPro.TMP_Text", "SetCharArray"): ("B", "TMPro.TMP_Text.SetCharArray"),
    ("TMPro.TMP_InputField", "set_text"): ("B", "TMPro.TMP_InputField.set_text"),
    ("UnityEngine.UI.InputField", "set_text"): ("B", "UnityEngine.UI.InputField.set_text"),
    ("UnityEngine.UI.Text", "set_richText"): ("B", "UnityEngine.UI.Text.set_richText"),
    ("UnityEngine.GUIStyle", "Draw"): ("B", "GUIStyle.Draw"),
    ("UnityEngine.GUIStyle", "Draw2D"): ("B", "GUIStyle.Draw2D"),
    # 额外发现（Lead 的 B 清单外，但确实是一条未覆盖的 GUIContent 写入路径）：
    ("UnityEngine.GUIContent", "set_text"): ("B", "UnityEngine.GUIContent.set_text"),
}

# 扩展 sink 表：默认空，保证 il-candidates.jsonl 逐字节可复现。
# iltranslatable.py 在 task-32 里往这里补 sink 缺口（例如
# ScreenMessages.PostScreenMessage(string, float) —— 它没登记时
# ClickThroughBlocker 的 "All Input Locks Cleared" 连普查都不计）。
EXTRA_API_TABLE = {}

# 扩展判定函数：默认 None（= task-29 口径）。iltranslatable.py 用它把
# 「KSP 对话框框架 DialogGUI* 的构造函数」也算成文本入口——它们是 UI 文本的
# 承载对象（和 GUIContent..ctor 同一类），但不在 PatchRegistry 的 16 个 sink 里。
EXTRA_CLASSIFY = None

# 本项目的自身程序集不计入（UI18n.Core.dll / UI18n.Runtime.dll / 测试期临时部署的
# UI18n.Probe.dll）：它们是产品与探针，不是 mod；而且探针 DLL 会随测试装卸，纳入会让
# 候选集不可复现。
SKIP_ASM_PREFIXES = ("ui18n.",)

# 反射写字段时，mod 里出现的属性名字面量（判定"这条反射是写 UI 文本"）
REFL_FIELD_NAMES = frozenset(("text", "m_text", "m_Text", "Text", "m_textMesh", "_text"))


def classify(tname, mname):
    """(声明类型, 方法名) -> (组, 规范名) 或 None。"""
    got = API_TABLE.get((tname, mname))
    if got is not None:
        return got
    if EXTRA_API_TABLE:
        got = EXTRA_API_TABLE.get((tname, mname))
        if got is not None:
            return got
    if EXTRA_CLASSIFY is not None:
        got = EXTRA_CLASSIFY(tname, mname)
        if got is not None:
            return got
    if tname.startswith("UnityEngine.UIElements.") and mname in ("set_text", "set_title"):
        return ("B", tname + "." + mname)
    if tname == "UnityEngine.Debug" and (mname.startswith("Log") or mname.startswith("Assert")):
        return ("LOG", "UnityEngine.Debug." + mname)
    if tname in ("System.Reflection.PropertyInfo", "System.Reflection.FieldInfo") and mname in (
            "SetValue", "SetValueDirect"):
        return ("REFL", tname + "." + mname)
    return None


def api_family(group, api):
    """矩阵行标签（把同一族的重载/子类并成一行）。"""
    if group == "A":
        return api
    if group == "LOG":
        return "UnityEngine.Debug.Log*（对照：日志，非 UI）"
    if group == "REFL":
        return "System.Reflection.*.SetValue（反射写字段）"
    if api.startswith("UnityEngine.UIElements."):
        return "UnityEngine.UIElements.*.set_text / set_title"
    return api


def short_type(t):
    """签名里的类型描述串 -> 短名（写进 method 字段用）。"""
    if not t:
        return "?"
    return t.rsplit(".", 1)[-1]


# ---------------------------------------------------------------------------
# 签名解析（只到"参数类型描述串"这一层，够判 sink 用）
# ---------------------------------------------------------------------------
_PRIMITIVE = {
    0x01: "System.Void", 0x02: "System.Boolean", 0x03: "System.Char", 0x04: "System.SByte",
    0x05: "System.Byte", 0x06: "System.Int16", 0x07: "System.UInt16", 0x08: "System.Int32",
    0x09: "System.UInt32", 0x0A: "System.Int64", 0x0B: "System.UInt64",
    0x0C: "System.Single", 0x0D: "System.Double", 0x0E: "System.String",
    0x16: "System.TypedReference", 0x18: "System.IntPtr", 0x19: "System.UIntPtr",
    0x1C: "System.Object",
}


class Resolver:
    """把元数据 token 解析成 {type, name, params, ret}。"""

    sig_fail = 0
    # 嵌套 TypeRef 名解析开关：默认 False = task-29 的口径（跨程序集继承链对不上，
    # 但 il-candidates.jsonl 因此仍可逐字节复现）。iltranslatable.py 扫描前置 True，
    # 用来让 KSPWheel 的 KSPWheelWearSettings 之类正确连到
    # GameParameters+CustomParameterNode 这条基类链上。
    NESTED_TYPEREF = False

    def __init__(self, img):
        self.img = img
        self.us = img.us_literals()
        self._cache = {}
        self._owner = None
        self._nested = None
        self._type_names = {}

    # -- 类型名 ------------------------------------------------------------
    def _build_owner(self):
        td = self.img.rows("TypeDef")
        n = len(self.img.rows("MethodDef"))
        owner = [0] * (n + 1)
        for i, row in enumerate(td):
            start = row["MethodList"]
            end = td[i + 1]["MethodList"] if i + 1 < len(td) else n + 1
            for rid in range(start, min(end, n + 1)):
                owner[rid] = i + 1
        self._owner = owner

    def type_of_method(self, rid):
        if self._owner is None:
            self._build_owner()
        return self.typedef_name(self._owner[rid]) if self._owner[rid] else "?"

    def nested_map(self):
        if self._nested is None:
            self._nested = {}
            for row in self.img.rows("NestedClass"):
                self._nested[row["NestedClass"]] = row["EnclosingClass"]
        return self._nested

    def typedef_name(self, rid):
        got = self._type_names.get(rid)
        if got is not None:
            return got
        row = self.img.row("TypeDef", rid)
        ns = self.img.string_at(row["Namespace"])
        nm = self.img.string_at(row["Name"])
        enclosing = self.nested_map().get(rid)
        if enclosing:
            name = self.typedef_name(enclosing) + "+" + nm
        else:
            name = (ns + "." + nm) if ns else nm
        self._type_names[rid] = name
        return name

    def class_name(self, cls):
        if not cls:
            return "?"
        table, rid = cls
        if table == "TypeRef":
            row = self.img.row("TypeRef", rid)
            ns = self.img.string_at(row["Namespace"])
            nm = self.img.string_at(row["Name"])
            # 嵌套类型（ResolutionScope 是 TypeRef/TypeDef）要拼成 "外层+内层"，
            # 否则跨程序集继承链对不上：KSPWheel 的 KSPWheelWearSettings 基类是
            # GameParameters+CustomParameterNode（Assembly-CSharp 里的嵌套类型），
            # TypeRef 侧只给出裸名 "CustomParameterNode"，is_derived 就永远假。
            scope = row.get("ResolutionScope")
            if (self.NESTED_TYPEREF and isinstance(scope, tuple)
                    and scope[0] in ("TypeRef", "TypeDef")):
                return self.class_name(scope) + "+" + nm
            return (ns + "." + nm) if ns else nm
        if table == "TypeDef":
            return self.typedef_name(rid)
        if table == "TypeSpec":
            blob = self.img.blob_at(self.img.row("TypeSpec", rid)["Signature"])
            try:
                return self._parse_type(blob, 0)[0]
            except (ilmeta.IlError, IndexError):
                return "TypeSpec"
        if table == "ModuleRef":
            return "<ModuleRef>"
        return "?"

    # -- 类型 / 签名 -------------------------------------------------------
    def _parse_type(self, blob, pos):
        b = blob[pos]
        pos += 1
        if b in _PRIMITIVE:
            return _PRIMITIVE[b], pos
        if b in (0x0F, 0x10, 0x1D, 0x45):
            inner, pos = self._parse_type(blob, pos)
            return {0x0F: inner + "*", 0x10: inner + "&", 0x1D: inner + "[]",
                    0x45: inner}[b], pos
        if b in (0x11, 0x12):
            # ECMA-335 II.23.2.8：CLASS / VALUETYPE 后面跟的是压缩的 TypeDefOrRefEncoded
            # （2 位 tag + rid），不是 4 字节元数据 token。早期按 4 字节读会让后面所有参数
            # 错位、甚至 IndexError，从而把 GUILayout.Width(float) 这类签名解析成「无参数」。
            enc, used = ilmeta.read_compressed_uint(blob, pos)
            pos += used
            return self._coded_type_name(enc & 0x3, enc >> 2), pos
        if b in (0x13, 0x1E):
            idx, used = ilmeta.read_compressed_uint(blob, pos)
            return ("!" if b == 0x13 else "!!") + str(idx), pos + used
        if b == 0x14:
            inner, pos = self._parse_type(blob, pos)
            rank, used = ilmeta.read_compressed_uint(blob, pos)
            pos += used
            n_sizes, used = ilmeta.read_compressed_uint(blob, pos)
            pos += used
            for _ in range(n_sizes):
                _, used = ilmeta.read_compressed_uint(blob, pos)
                pos += used
            n_lo, used = ilmeta.read_compressed_uint(blob, pos)
            pos += used
            for _ in range(n_lo):
                _, used = ilmeta.read_compressed_uint(blob, pos)
                pos += used
            return inner + "[" + "," * (rank - 1) + "]", pos
        if b == 0x15:
            # GENERICINST = 0x15, CLASS/VALUETYPE, 压缩的 TypeDefOrRefEncoded, 参数个数, 参数类型*
            pos += 1  # CLASS / VALUETYPE
            enc, used = ilmeta.read_compressed_uint(blob, pos)
            pos += used
            name = self._coded_type_name(enc & 0x3, enc >> 2)
            argc, used = ilmeta.read_compressed_uint(blob, pos)
            pos += used
            args = []
            for _ in range(argc):
                a, pos = self._parse_type(blob, pos)
                args.append(a)
            return name + "<" + ",".join(args) + ">", pos
        if b in (0x1F, 0x20):
            # ECMA-335 II.23.2.7：CMOD_REQD(0x1F)/CMOD_OPT(0x20) 后面跟的同样是压缩的
            # TypeDefOrRefEncoded，不是 4 字节元数据 token。按 4 字节读会把后面的类型
            # 整体读歪：含 modreq/modopt 的签名（BurstPQS 的 job 接口、ModularFlightPanel
            # 的 modopt 参数、0Harmony 的委托）整条解析失败。
            _, used = ilmeta.read_compressed_uint(blob, pos)
            pos += used
            return self._parse_type(blob, pos)
        if b == 0x1B:  # FNPTR：递归解析方法签名，语义上不关心
            _, pos = self._parse_method_sig(blob, pos)
            return "fnptr", pos
        raise ilmeta.IlError("签名里的未知类型 0x%02X" % b)

    def _parse_method_sig(self, blob, pos):
        cc = blob[pos]
        pos += 1
        if cc & 0x10:
            _, used = ilmeta.read_compressed_uint(blob, pos)
            pos += used
        pcount, used = ilmeta.read_compressed_uint(blob, pos)
        pos += used
        ret, pos = self._parse_type(blob, pos)
        params = []
        for _ in range(pcount):
            t, pos = self._parse_type(blob, pos)
            params.append(t)
        return (params, ret), pos

    def _coded_type_name(self, tag, rid):
        """TypeDefOrRefEncoded：0=TypeDef 1=TypeRef 2=TypeSpec。"""
        if tag == 0:
            return self.typedef_name(rid)
        if tag == 1:
            return self.class_name(("TypeRef", rid))
        if tag == 2:
            return self.class_name(("TypeSpec", rid))
        return "coded%d:%d" % (tag, rid)

    def _token_name(self, tok):
        table = (tok >> 24) & 0xFF
        rid = tok & 0xFFFFFF
        if table == 0x01:
            return self.class_name(("TypeRef", rid))
        if table == 0x02:
            return self.typedef_name(rid)
        if table == 0x1B:
            return self.class_name(("TypeSpec", rid))
        return "token%08X" % tok

    def sig(self, blob):
        if not blob:
            return [], "?"
        cc = blob[0]
        if (cc & 0x0F) == 0x06:      # FIELD 签名（引用字段而不是方法）
            return [], "field"
        try:
            return self._parse_method_sig(blob, 0)[0]
        except (ilmeta.IlError, IndexError):
            self.sig_fail += 1
            return [], "?"

    # -- token 解析 --------------------------------------------------------
    def resolve(self, token):
        if token in self._cache:
            return self._cache[token]
        out = None
        table = (token >> 24) & 0xFF
        rid = token & 0xFFFFFF
        try:
            if table == 0x06:
                row = self.img.row("MethodDef", rid)
                params, ret = self.sig(self.img.blob_at(row["Signature"]))
                out = {"type": self.type_of_method(rid),
                       "name": self.img.string_at(row["Name"]),
                       "params": params, "ret": ret}
            elif table == 0x0A:
                row = self.img.row("MemberRef", rid)
                params, ret = self.sig(self.img.blob_at(row["Signature"]))
                out = {"type": self.class_name(row["Class"]),
                       "name": self.img.string_at(row["Name"]),
                       "params": params, "ret": ret}
            elif table == 0x2B:
                inner = self.img.row("MethodSpec", rid)["Method"]
                byte = {"MethodDef": 0x06, "MemberRef": 0x0A}.get(inner[0])
                if byte:
                    out = self.resolve((byte << 24) | inner[1])
        except (ilmeta.IlError, IndexError):
            out = None
        self._cache[token] = out
        return out


# ---------------------------------------------------------------------------
# 方法体内的抽象解释（栈 + 局部变量，深度 1 层，不跨方法）
# ---------------------------------------------------------------------------
class Analyzer:
    """对单个方法体做数据流分析，收集流进 sink 的字面量。"""

    STEP_LIMIT = 200000

    def __init__(self, res, us, asm, mod, decl, method_label, census, hits, stats):
        self.res = res
        self.us = us
        self.asm = asm
        self.mod = mod
        self.decl = decl
        self.method_label = method_label
        self.census = census
        self.hits = hits
        self.stats = stats

    # -- 值的合并 ----------------------------------------------------------
    @staticmethod
    def _merge_val(a, b):
        if a == b:
            return a
        if a and b and a[0] == "lit" and b[0] == "lit" and a[1] == b[1]:
            return a
        return None

    def _merge_state(self, a, b):
        sa, la = a
        sb, lb = b
        if len(sa) != len(sb):
            return None
        stack = tuple(self._merge_val(x, y) for x, y in zip(sa, sb))
        loc = {}
        for k in set(la) | set(lb):
            loc[k] = self._merge_val(la.get(k), lb.get(k))
        return (stack, loc)

    # -- 主循环 ------------------------------------------------------------
    def run(self, insns):
        n = len(insns)
        by_off = {}
        nexts = [0] * n
        for i, (off, _name, _op) in enumerate(insns):
            by_off[off] = i
            nexts[i] = insns[i + 1][0] if i + 1 < n else off
        states = {}
        work = [(0, (), ())]
        steps = 0
        while work:
            off, stack, locals_ = work.pop()
            st = states.get(off)
            if st is not None:
                if st[0] == stack and st[1] == locals_:
                    continue
                merged = self._merge_state(st, (stack, locals_))
                if merged is None:
                    continue
                stack, locals_ = merged
            states[off] = (stack, locals_)
            stack = list(stack)
            locals_ = dict(locals_)
            cur = off
            while True:
                steps += 1
                if steps > self.STEP_LIMIT:
                    self.stats["aborted"] += 1
                    ops = self.stats.setdefault("abort_ops", {})
                    ops["<step-limit>"] = ops.get("<step-limit>", 0) + 1
                    return
                i = by_off.get(cur)
                if i is None:
                    return
                ioff, name, operand = insns[i]
                nxt = nexts[i]
                keep_going = self._step(insns, i, ioff, name, operand, nxt, stack,
                                        locals_, work, by_off, nexts)
                if keep_going is None:
                    return                      # 中止整个方法
                if not keep_going:
                    break                       # 本块结束（分支/终止）
                cur = nxt

    def _step(self, insns, i, ioff, name, operand, nxt, stack, locals_, work,
              by_off, nexts):
        """执行一条指令。返回 True=继续下一条，False=本块结束，None=中止方法。"""
        if name == "ldstr":
            text = self.us.get(operand & 0xFFFFFF)
            stack.append(("lit", text if text is not None else "", ioff))
            return True
        if name.startswith("ldc.") or name == "ldnull":
            stack.append(("num",))
            return True
        if name.startswith("ldarg") or name.startswith("ldarga"):
            stack.append(None)
            return True
        if name.startswith("ldloc") or name.startswith("ldloca"):
            stack.append(locals_.get(_loc_index(name, operand)))
            return True
        if name.startswith("stloc"):
            locals_[_loc_index(name, operand)] = stack.pop() if stack else None
            return True
        if name.startswith("starg"):
            if stack:
                stack.pop()
            return True
        if name == "dup":
            stack.append(stack[-1] if stack else None)
            return True
        if name == "pop":
            if stack:
                stack.pop()
            return True
        if name in ("nop", "break"):
            return True
        if name in ("call", "callvirt", "newobj"):
            return self._call(name, operand, stack)
        if name == "box" or name == "castclass" or name == "isinst":
            return True                          # 值原样穿过
        if name in ("unbox", "unbox.any"):
            if stack:
                stack.pop()
            stack.append(None)
            return True
        if name in ("ldfld", "ldflda", "ldobj"):
            if stack:
                stack.pop()
            stack.append(None)
            return True
        if name in ("stfld", "stobj", "cpobj"):
            for _ in range(2):
                if stack:
                    stack.pop()
            return True
        if name in ("ldsfld", "ldsflda", "ldtoken", "sizeof", "ldftn", "arglist"):
            # 扩展点：静态字段的初值就是字面量时（ldstr X; stsfld F），ldsfld 把字面量带上栈
            stack.append(self._field_lit(operand) if name == "ldsfld" else None)
            return True
        if name in ("stsfld", "initobj"):
            if stack:
                stack.pop()
            return True
        if name in ("cpblk", "initblk"):
            for _ in range(3):
                if stack:
                    stack.pop()
            return True
        if name == "newarr":
            if stack:
                stack.pop()
            stack.append(("arr", []))
            return True
        if name == "stelem.ref":
            val = stack.pop() if stack else None
            if stack:
                stack.pop()
            arr = stack.pop() if stack else None
            if arr and arr[0] == "arr":
                arr[1].append(val)
            return True
        if name in ("ldelem", "stelem", "ldelema", "ldvirtftn", "refanyval", "mkrefany",
                    "refanytype", "localloc"):
            pops = {"ldelem": 2, "stelem": 3, "ldelema": 2, "ldvirtftn": 1,
                    "refanyval": 1, "mkrefany": 1, "refanytype": 1, "localloc": 1}[name]
            for _ in range(pops):
                if stack:
                    stack.pop()
            if name != "stelem":
                stack.append(None)
            return True
        if name in ilmeta._BINARY:
            for _ in range(2):
                if stack:
                    stack.pop()
            stack.append(None)
            return True
        if name in ilmeta._UNARY or name in ilmeta._LOAD_IND:
            if stack:
                stack.pop()
            stack.append(None)
            return True
        if name in ilmeta._STORE_IND:
            for _ in range(2):
                if stack:
                    stack.pop()
            return True
        if name in ilmeta._LOAD_ELEM:
            for _ in range(2):
                if stack:
                    stack.pop()
            stack.append(None)
            return True
        if name in ilmeta._STORE_ELEM:
            for _ in range(3):
                if stack:
                    stack.pop()
            return True
        if name in ilmeta._PREFIX:
            return True
        if name == "calli":
            self.stats["aborted"] += 1
            ops = self.stats.setdefault("abort_ops", {})
            ops["calli"] = ops.get("calli", 0) + 1
            return None
        if name in ("jmp", "ret", "throw", "rethrow", "endfinally", "endfilter"):
            return False
        if name == "switch":
            if stack:
                stack.pop()
            for t in operand:
                self._push_branch(work, nxt + _signed32(t), stack, locals_)
            return True
        if name in ilmeta._BRANCH_POPS:
            pops = ilmeta._BRANCH_POPS[name]
            for _ in range(pops):
                if stack:
                    stack.pop()
            delta = operand
            if name in ilmeta._SHORT_BRANCH:
                delta = operand - 256 if operand >= 128 else operand
            else:
                delta = _signed32(operand)
            target = nxt + delta
            if name in ("leave", "leave.s"):
                self._push_branch(work, target, [], locals_)
                return False
            self._push_branch(work, target, stack, locals_)
            return True
        # 未覆盖的指令：保守中止（宁可漏报也不误报）
        self.stats["aborted"] += 1
        ops = self.stats.setdefault("abort_ops", {})
        ops[name] = ops.get(name, 0) + 1
        return None

    @staticmethod
    def _push_branch(work, target, stack, locals_):
        work.append((target, tuple(stack), dict(locals_)))

    # -- 调用 --------------------------------------------------------------
    def _call(self, kind, token, stack):
        target = self.res.resolve(token)
        if target is None:
            self.stats["aborted"] += 1
            ops = self.stats.setdefault("abort_ops", {})
            ops["<unresolved-token>"] = ops.get("<unresolved-token>", 0) + 1
            return None
        params = target["params"]
        nparams = len(params)
        if len(stack) < nparams:
            stack[:] = [None] * (nparams - len(stack)) + stack
        args = stack[len(stack) - nparams:] if nparams else []
        if nparams:
            del stack[len(stack) - nparams:]
        cls = classify(target["type"], target["name"])
        if cls is not None:
            group, api = cls
            rec = self.census.get(api)
            if rec is not None:
                for pi, pt in enumerate(params):
                    if pt != "System.String":
                        continue
                    val = args[pi] if pi < len(args) else None
                    if not val:
                        continue
                    if val[0] == "litm":
                        # 扩展点：一个调用点解析出多条候选字面量（同族虚属性/方法的 override）
                        for text0, off0, org0 in val[1]:
                            if not text0.strip():
                                continue
                            rec["lits"].add(text0)
                            if group == "A":
                                self.hits.append(self._hit(api, text0, off0, org0))
                        continue
                    if val[0] == "lit" and val[1].strip():
                        rec["lits"].add(val[1])
                        if group == "A":
                            self.hits.append(self._hit(api, val[1], val[2],
                                                       val[3] if len(val) > 3 else None))
        # 返回值建模
        if kind == "newobj":
            stack.append(("gc",) if target["type"] == "UnityEngine.GUIContent" else None)
            return True
        ret = target["ret"]
        if ret == "System.Void":
            return True
        if target["type"] == "System.String" and target["name"] == "Concat":
            stack.append(_concat(args))
            return True
        if target["type"] == "System.String" and target["name"] == "Format":
            stack.append(_first_lit(args, params))
            return True
        lit = self._resolve_return_lit(target, kind, args)
        if lit is not None:
            stack.append(lit)
            return True
        stack.append(None)
        return True

    # -- 扩展点（基类一律不解析，保证 il-candidates.jsonl 口径不变）-------------
    def _resolve_return_lit(self, target, kind, args):
        """返回值就是字面量时返回 ("lit", text, off, origin) 或 ("litm", [...], off)。"""
        return None

    def _field_lit(self, operand):
        """静态字段初值是字面量时返回 ("lit", text, off, origin)。"""
        return None

    def _hit(self, api, text, off, origin):
        """构造命中记录；origin=(asm, type, method, via) 表示字面量其实在别的类型/方法里。"""
        if origin:
            asm0, type0, method0 = origin[0], origin[1], origin[2]
            via = origin[3] if len(origin) > 3 else "direct"
        else:
            asm0, type0, method0, via = self.asm, self.decl, self.method_label, "direct"
        return {"asm": asm0, "type": type0, "method": method0, "ilOffset": off,
                "sink": api, "text": text, "via": via}


def _loc_index(name, operand):
    if name.endswith(".s") or name in ("ldloc", "stloc", "ldloca"):
        return operand if operand is not None else 0
    tail = name.rsplit(".", 1)[-1]
    return int(tail) if tail.isdigit() else (operand or 0)


def _signed32(v):
    return v - 0x100000000 if v >= 0x80000000 else v


def _concat(args):
    """string.Concat 的常量折叠：全部是字面量才合成一条，否则 unknown。"""
    parts = []
    first = None
    for a in args:
        if a and a[0] == "arr":
            for e in a[1]:
                if not (e and e[0] == "lit"):
                    return None
                parts.append(e[1])
                if first is None:
                    first = e[2]
            continue
        if not (a and a[0] == "lit"):
            return None
        parts.append(a[1])
        if first is None:
            first = a[2]
    if first is None:
        return None
    return ("lit", "".join(parts), first)


def _first_lit(args, params):
    """string.Format：取第一个 string 参数（格式串本身就是要翻的文本）。"""
    for pi, pt in enumerate(params):
        if pt == "System.String" and pi < len(args):
            a = args[pi]
            if a and a[0] == "lit":
                return a
            return None
    return None


# ---------------------------------------------------------------------------
# 单个 DLL 的扫描
# ---------------------------------------------------------------------------
def scan_dll(path, asm, mod, analyzer_cls=None, need_no_ldstr=False):
    """返回 (stats, hits, census)。stats 里带漏斗用的字面量统计。

    analyzer_cls / need_no_ldstr 只给 iltranslatable.py 用来插扩展点（默认值 = 本模块
    的 Analyzer + 只跑「本方法里有 ldstr」的方法，行为与 task-29 完全一致，
    il-candidates.jsonl 因此仍可逐字节复现）。

    need_no_ldstr=True 时把「本方法没有 ldstr 但有文本 API 调用」的方法也做数据流：
    扩展采集要抓的字面量常常不在调用点所在的方法里（RCSBuildAid 的 buttonTitle 属性
    getter 里才有 "Vessel mass"），按原来的预筛会整条漏掉。
    """
    img = ilmeta.Image(path)
    res = Resolver(img)
    us = res.us
    stats = {"asm": asm, "mod": mod, "path": path, "methods": 0, "decode_fail": 0,
             "aborted": 0, "dataflow": 0, "us": len(us), "sig_fail": 0}
    stats.update(funnel_stats(us.values()))
    hits = []
    census = {}
    methods = img.rows("MethodDef")
    sig_fail0 = res.sig_fail
    for rid, m in enumerate(methods, 1):
        stats["methods"] += 1
        rva = m["RVA"]
        if not rva:
            continue
        try:
            code, _ = img.method_body(rva)
            insns = ilmeta.decode_il(code)
        except (ilmeta.IlError, IndexError):
            stats["decode_fail"] += 1
            continue
        # 第一遍：只做线性扫描，数调用点（不需要数据流，便宜）
        has_ldstr = False
        has_text_api = False
        lit_texts = set()
        refl_apis = set()
        for ioff, name, operand in insns:
            if name == "ldstr":
                has_ldstr = True
                got = us.get(operand & 0xFFFFFF)
                if got:
                    lit_texts.add(got.strip())
            elif name in ("call", "callvirt", "newobj"):
                target = res.resolve(operand)
                if target is None:
                    continue
                cls = classify(target["type"], target["name"])
                if cls is None:
                    continue
                group, api = cls
                rec = census.get(api)
                if rec is None:
                    rec = census[api] = {"group": group, "family": api_family(group, api),
                                         "calls": 0, "methods": set(), "lits": set()}
                rec["calls"] += 1
                rec["methods"].add(rid)
                if group in ("A", "B"):
                    has_text_api = True
                elif group == "REFL":
                    refl_apis.add(api)
        # 反射写字段的判定：本方法里有 PropertyInfo/FieldInfo.SetValue，
        # 且本方法的 ldstr 里出现了 text / m_text 这类属性名字面量。
        if refl_apis and (lit_texts & REFL_FIELD_NAMES):
            for api in refl_apis:
                census[api]["reflection_text"] = True
        if not (has_text_api and (has_ldstr or need_no_ldstr)):
            continue
        # 第二遍：只有「既有字面量又有文本 API 调用」的方法才做数据流
        stats["dataflow"] += 1
        mname = img.string_at(m["Name"])
        label = "%s(%s)" % (mname, ",".join(short_type(t) for t in res.sig(
            img.blob_at(m["Signature"]))[0]))
        (analyzer_cls or Analyzer)(res, us, asm, mod, res.type_of_method(rid), label,
                                   census, hits, stats).run(insns)
    stats["sig_fail"] = res.sig_fail - sig_fail0
    return stats, hits, census


# ---------------------------------------------------------------------------
# 噪声分类（互斥，顺序即优先级）
# ---------------------------------------------------------------------------
NOISE_ORDER = ("键引用", "URL", "路径", "XML/富文本", "纯数字/单位", "标识符", "日志/错误串",
               "其他（含待译句子）")

_EXT_RE = re.compile(r"\.(?:cfg|dll|mu|dds|png|txt|json|ksp|asset|prefab)\b", re.I)
_SEG_RE = re.compile(r"^[A-Za-z0-9_.\-]+(?:[/\\][A-Za-z0-9_.\-]+){2,}$")
_TAG_RE = re.compile(r"</?[A-Za-z][^<>]*>|&#?\w+;")
_IDENT_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_.]*$")
_LOG_RE = re.compile(r"(?i)(exception|error[:：]|failed|failure|warning|stack ?trace|"
                     r"nullref|not found|traceback|assert)")


def noise_class(text):
    """把一条文本归到噪声类别；"其他（含待译句子）"才是真正要翻的。"""
    if text.startswith("#"):
        return "键引用"
    low = text.lower()
    if "http://" in low or "https://" in low or low.startswith("www."):
        return "URL"
    if "\\" in text or "/GameData" in text or _EXT_RE.search(text) or _SEG_RE.match(text):
        return "路径"
    if _TAG_RE.search(text):
        return "XML/富文本"
    if textnorm.is_pure_number(text):
        return "纯数字/单位"
    if _IDENT_RE.match(text) and ("_" in text or "." in text or text.islower()
                                  or text.isupper()):
        return "标识符"
    if _LOG_RE.search(text):
        return "日志/错误串"
    return "其他（含待译句子）"


# ---------------------------------------------------------------------------
# 全量收集
# ---------------------------------------------------------------------------
def iter_dlls(gamedata, managed):
    """按确定顺序产出 (path, asm, mod)：Assembly-CSharp 先，然后 GameData 按路径升序。"""
    out = []
    if managed:
        acs = os.path.join(managed, "Assembly-CSharp.dll")
        if os.path.isfile(acs):
            out.append((acs, "Assembly-CSharp.dll", "KSP"))
    if gamedata and os.path.isdir(gamedata):
        found = []
        for root, dirs, files in os.walk(gamedata):
            dirs.sort()
            for fn in sorted(files):
                low = fn.lower()
                if low.startswith(SKIP_ASM_PREFIXES):
                    continue
                if low.endswith(".dll"):
                    full = os.path.join(root, fn)
                    rel = os.path.relpath(full, gamedata)
                    mod = rel.split(os.sep)[0]
                    found.append((full, fn, mod))
        found.sort(key=lambda x: (x[0].lower(), x[0]))
        out.extend(found)
    return out


def collect(gamedata, managed, only=None, limit=None, quiet=False):
    dlls = iter_dlls(gamedata, managed)
    if only:
        want = only.lower()
        dlls = [d for d in dlls if want in d[1].lower() or want in d[0].lower()]
    if limit:
        dlls = dlls[:limit]
    per_dll = []
    all_hits = []
    census = {}
    skipped = []
    t0 = time.time()
    for path, asm, mod in dlls:
        try:
            stats, hits, cen = scan_dll(path, asm, mod)
        except ilmeta.IlError as exc:
            skipped.append((asm, str(exc)))
            if not quiet:
                print("  [skip] %-40s %s" % (asm, exc))
            continue
        stats["census"] = {api: {"group": r["group"], "family": r["family"],
                                "calls": r["calls"], "lits": r["lits"]}
                          for api, r in cen.items()}
        per_dll.append(stats)
        all_hits.extend(hits)
        for api, rec in cen.items():
            agg = census.get(api)
            if agg is None:
                agg = census[api] = {"group": rec["group"], "family": rec["family"],
                                     "calls": 0, "methods": set(), "lits": set(),
                                     "dlls": {}, "reflection_text": False}
            agg["calls"] += rec["calls"]
            agg["methods"] |= rec["methods"]
            agg["dlls"][asm] = agg["dlls"].get(asm, 0) + rec["calls"]
            if rec.get("reflection_text"):
                agg["reflection_text"] = True
            agg["lits"] |= rec["lits"]
        if not quiet:
            print("  [ok]   %-44s us=%-6d df=%-6d ab=%-4d sf=%-4d hits=%-5d"
                  % (asm, stats["us"], stats["dataflow"], stats["aborted"],
                     stats["sig_fail"], len(hits)))
    elapsed = time.time() - t0
    return {"per_dll": per_dll, "hits": all_hits, "census": census,
            "skipped": skipped, "elapsed": elapsed}


def build_candidates(hits):
    """按 (asm, n) 去重（同一 DLL 同一文本只留一条，取排序最前者），返回行列表。"""
    rows = []
    for h in hits:
        text = h["text"]
        n = textnorm.normalize(text)
        if not n:
            continue
        rows.append({"asm": h["asm"], "type": h["type"], "method": h["method"],
                     "ilOffset": h["ilOffset"], "sink": h["sink"], "text": text,
                     "h": fnv.hash_text(n), "n": n})
    rows.sort(key=lambda r: (r["asm"], r["n"], r["type"], r["method"], r["ilOffset"]))
    out = []
    seen = set()
    for r in rows:
        key = (r["asm"], r["n"])
        if key in seen:
            continue
        seen.add(key)
        out.append(r)
    return out


def write_jsonl(path, rows):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        for r in rows:
            fh.write(json.dumps({"asm": r["asm"], "type": r["type"], "method": r["method"],
                                 "ilOffset": r["ilOffset"], "sink": r["sink"],
                                 "text": r["text"], "h": r["h"], "n": r["n"]},
                                ensure_ascii=False, separators=(",", ":")) + "\n")
    return len(rows)


# ---------------------------------------------------------------------------
# 漏斗（字面量侧，与 harvest 同口径的级联，去掉 is-key / search-tags / already-covered）
# ---------------------------------------------------------------------------
def funnel_stats(values):
    """#US 字面量 -> 漏斗计数。返回 dict(us_distinct, filtered, ge4, ui)。"""
    seen = set()
    filtered = 0
    ge4 = 0
    ui = 0
    for text in values:
        n = textnorm.normalize(text)
        if not n:
            continue
        if textnorm.has_cjk(n):
            continue
        if textnorm.is_pure_number(n):
            continue
        if len(n) < 2:
            continue
        if textnorm.is_url_only(n):
            continue
        h = fnv.hash_text(n)
        if h in seen:
            continue
        seen.add(h)
        filtered += 1
        if len(n) >= 4:
            ge4 += 1
            if ui_shape(n):
                ui += 1
    return {"us_distinct": len(set(values)), "filtered": filtered, "ge4": ge4, "ui": ui}


def ui_shape(n):
    """「像 UI 文本」的粗判据：>=2 个词，或单个首字母大写的纯字母词（长度 >=4）。"""
    if not any(ch.isalpha() for ch in n):
        return False
    if len(n.split()) >= 2:
        return True
    return len(n) >= 4 and n.isalpha() and n[0].isupper()


def _md(text, limit=64):
    """表格里的样例：截断 + 转义竖线。"""
    t = text.replace("|", "\\|").replace("\n", " ")
    return t if len(t) <= limit else t[:limit - 1] + "…"


def _pct(a, b):
    return ("%.2f%%" % (100.0 * a / b)) if b else "-"


def _num(n):
    return "{:,}".format(n)


# ---------------------------------------------------------------------------
# 报告
# ---------------------------------------------------------------------------
GROUP_ORDER = {"A": 0, "B": 1, "REFL": 2, "LOG": 3}


def _fam_agg(census):
    """把逐 (api) 的 census 聚到「API 族」上，供 §9 的两张表用。"""
    fam = {}
    for api, rec in census.items():
        f = fam.setdefault(rec["family"], {"group": rec["group"], "calls": 0,
                                          "methods": 0, "lits": set(), "dlls": {},
                                          "reflection_text": False})
        f["calls"] += rec["calls"]
        f["methods"] += len(rec["methods"])
        f["lits"] |= set(rec["lits"])
        for asm, cnt in rec["dlls"].items():
            f["dlls"][asm] = f["dlls"].get(asm, 0) + cnt
        f["reflection_text"] = f["reflection_text"] or rec.get("reflection_text", False)
    return fam


def build_report(res, rows, pool_h, meta):
    per_dll = res["per_dll"]
    census = res["census"]
    total = {k: sum(d[k] for d in per_dll) for k in
             ("us", "us_distinct", "filtered", "ge4", "ui", "methods", "decode_fail",
              "aborted", "dataflow")}
    total["sig_fail"] = sum(d.get("sig_fail", 0) for d in per_dll)
    abort_ops = {}
    for d in per_dll:
        for k, v in (d.get("abort_ops") or {}).items():
            abort_ops[k] = abort_ops.get(k, 0) + v
    dlls_scanned = len(per_dll)

    distinct_n = sorted({r["n"] for r in rows})
    by_mod = {}
    for r in rows:
        mod = meta["asm2mod"].get(r["asm"], r["asm"])
        by_mod.setdefault(mod, []).append(r)
    dll_by_mod = {}
    for d in per_dll:
        dll_by_mod.setdefault(d["mod"], []).append(d)

    noise = {}
    for n in distinct_n:
        noise[noise_class(n)] = noise.get(noise_class(n), 0) + 1
    trans = noise.get("其他（含待译句子）", 0)
    ph_distinct = {r["n"] for r in rows if textnorm.features(r["n"])["ph"]}
    pool_hit = [r for r in rows if r["h"] in pool_h]
    pool_hit_distinct = {r["n"] for r in pool_hit}
    trans_pool = {r["n"] for r in pool_hit
                  if noise_class(r["n"]) == "其他（含待译句子）"}

    fam = _fam_agg(census)
    fam_rows = sorted(fam.items(),
                      key=lambda kv: (GROUP_ORDER.get(kv[1]["group"], 9),
                                      -kv[1]["calls"], kv[0]))
    a_calls = sum(f["calls"] for f in fam.values() if f["group"] == "A")
    b_calls = sum(f["calls"] for f in fam.values() if f["group"] == "B")
    refl_calls = sum(f["calls"] for f in fam.values()
                     if f["group"] == "REFL" and f["reflection_text"])
    log_calls = sum(f["calls"] for f in fam.values() if f["group"] == "LOG")
    a_lits = sum(len(f["lits"]) for f in fam.values() if f["group"] == "A")
    b_lits = sum(len(f["lits"]) for f in fam.values() if f["group"] == "B")

    L = []
    A = L.append
    A("# IL 字符串 sink 可达性盘点（T29）")
    A("")
    A("> 目标：把「mod DLL 里硬编码的 UI 字符串」变成可翻译候选集，并给出可信量级。"
      "本轮**只出候选与证据**：未改 `packs/pool.jsonl`、未改 `packs/zh-cn/**`、"
      "未改 `packs/index.json`、未改 `src/**` / `tests/**` / `docs/**`。")
    A("> 扫描范围：`KSP_x64_Data\\Managed\\Assembly-CSharp.dll` + `GameData\\**\\*.dll`；"
      "共 %s 个 DLL、%s 个方法体、耗时 %.1fs。"
      % (_num(dlls_scanned), _num(total["methods"]), res["elapsed"]))
    A("> 产物：`packs/il-candidates.jsonl`（%s 行）与本报告。" % _num(len(rows)))
    A("")
    A("## 0 结论摘要")
    A("")
    A("1. **真解 IL 的终止率**：%s 个方法体按操作码 + 操作数长度逐条解码，"
      "落到方法体末尾之外（即解码失败）的 **%s** 个；数据流分析中止 %s 个"
      "（遇到未覆盖指令），其余全部完成。"
      % (_num(total["methods"]), _num(total["decode_fail"]), _num(total["aborted"])))
    A("2. **字面量漏斗**（分 DLL 口径相加，非全局去重）：#US 去重字面量 **%s** → "
      "过滤后 **%s** → ≥4 字符 **%s** → 像 UI 文本 **%s**。"
      % (_num(total["us_distinct"]), _num(total["filtered"]), _num(total["ge4"]),
         _num(total["ui"])))
    A("3. **sink 可达候选**：`packs/il-candidates.jsonl` **%s** 行（按 (asm, n) 去重），"
      "跨 DLL 去重后 **%s** 条 distinct 文本。"
      % (_num(len(rows)), _num(len(distinct_n))))
    A("4. **噪声**：%s 条 distinct 里真正「像句子」的 **%s** 条（%s）；"
      "其余 %s 是键引用 / 路径 / 标识符 / 日志串等。"
      % (_num(len(distinct_n)), _num(trans), _pct(trans, len(distinct_n)),
         _num(len(distinct_n) - trans)))
    A("5. **与既有池的交集**：%s 条 distinct（%s）的 h 已出现在 `packs/pool.jsonl` 里，"
      "不该重复翻译。" % (_num(len(pool_hit_distinct)),
                     _pct(len(pool_hit_distinct), len(distinct_n))))
    A("6. **加权覆盖率（回答「怎么才算通用任意 mod」）**：按调用点数加权，"
      "PatchRegistry 的 16 个 sink + `GUIContent..ctor` 覆盖 **%s** 的文本写入路径"
      "（%s / (%s + %s + %s)）；按去重字面量加权 **%s**。剩下的大头是 %s。"
      % (_pct(a_calls, a_calls + b_calls + refl_calls), _num(a_calls), _num(a_calls),
         _num(b_calls), _num(refl_calls), _pct(a_lits, a_lits + b_lits),
         _top_blind(fam)))
    A("")
    A("## 1 口径与方法")
    A("")
    A("### 1.1 什么算「sink 可达」")
    A("")
    A("- 只认 `ldstr` 直接、或经 `string.Concat` / `string.Format` / `GUIContent..ctor` / "
      "`stloc`+`ldloc` / `dup` / `box` 流向 sink 参数的常量；**方法体内深度 1 层，不跨方法**。")
    A("- 不允许：先存静态字段再读、经返回值传出、经数组/字典、经 `ToString()` 生成。")
    A("- sink 白名单 = `src/UI18n.Runtime/Patches/PatchRegistry.cs` 的 16 个目标 + "
      "`GUIContent..ctor`（IMGUI 文本的唯一入口）。")
    A("- **`Debug.Log*` / 异常消息不是 sink**，单独计数只做噪声对照（§9.3）。")
    A("- 取实参时按「参数声明类型是 `System.String`」定位，因此同一 API 的所有重载自动覆盖。")
    A("- **本项目的自身程序集不计入**（`UI18n.Core.dll` / `UI18n.Runtime.dll` / "
      "测试期临时部署的 `UI18n.Probe.dll`）：它们是产品与探针，不是 mod；探针 DLL 会随测试装卸，"
      "纳入会让候选集不可复现。")
    A("")
    A("### 1.2 为什么必须真解 IL")
    A("")
    A("正则扫字节码会把操作数里的字节当成指令。本实现按 ECMA-335 操作码表逐条解码"
      "（含 `0xFE` 双字节前缀、`switch` 变长操作数），解码必须正好落到方法体末尾，"
      "否则计入 `decode_fail`。实测 %s 个方法里只有 %s 个失败，"
      "这条数字就是「操作码表没写错」的证据。"
      % (_num(total["methods"]), _num(total["decode_fail"])))
    A("")
    A("签名（参数类型）解析失败 %s 个 —— 这个数必须是 0：签名解析一失败，调用点的实参就会"
      "整体错位，sink 参数取不到字面量（`GUILayout.Width(float)` 曾被读成「无参数」，"
      "导致 `GUILayout.Label(string)` 的文本全部丢失）。"
      % _num(total.get("sig_fail", 0)))
    A("")
    A("数据流中止 %s 个方法体，原因分布：%s（`<step-limit>` = 同一方法内状态合并次数"
      "超上限，通常是巨型 switch 的状态爆炸；`calli` 与未解析 token 也走同一保守策略："
      "宁可漏报不误报）。"
      % (_num(total["aborted"]),
         "、".join("`%s` %s" % (k, _num(v)) for k, v in sorted(abort_ops.items(),
                                                              key=lambda kv: -kv[1])) or "无"))
    A("")
    A("## 2 漏斗表")
    A("")
    A("全量（所有 DLL 相加；`#US 去重` 是每个 DLL 内部去重后相加，不是全局去重）：")
    A("")
    A("| 阶段 | 条数 |")
    A("|---|---|")
    A("| #US 字面量（DLL 内去重后相加） | %s |" % _num(total["us_distinct"]))
    A("| 过滤后（empty / already-cjk / pure-number / too-short / url-only / dup 全去掉） | %s |"
      % _num(total["filtered"]))
    A("| ≥4 字符 | %s |" % _num(total["ge4"]))
    A("| 像 UI 文本（≥2 词，或 ≥4 字母首字母大写单词） | %s |" % _num(total["ui"]))
    A("| sink 可达（按 (asm, n) 去重 = 候选行数） | %s |" % _num(len(rows)))
    A("| 其中「其他（含待译句子）」= 真该翻的 | %s |" % _num(trans))
    A("")
    A("> 「像 UI 文本」与「sink 可达」是同一批字面量上的两个不同视角，**不是嵌套关系**："
      "sink 可达里既有 `OK` 这种 2 字符文本，也有 `%s` 这种纯格式串。")
    A("")
    mods = sorted(by_mod.items(),
                  key=lambda kv: (-len({r["n"] for r in kv[1]}), kv[0].lower()))
    A("逐 mod（按候选 distinct 降序，只列前 25 个；`mod` 取 GameData 下第一段目录，"
      "`KSP` 指 Assembly-CSharp）：")
    A("")
    A("| mod | DLL | #US | 过滤后 | ≥4 字符 | 候选 distinct |")
    A("|---|---|---|---|---|---|")
    for mod, items in mods[:25]:
        ds = dll_by_mod.get(mod, [])
        A("| %s | %d | %s | %s | %s | %s |"
          % (mod, len(ds), _num(sum(x["us_distinct"] for x in ds)),
             _num(sum(x["filtered"] for x in ds)), _num(sum(x["ge4"] for x in ds)),
             _num(len({r["n"] for r in items}))))
    rest = mods[25:]
    if rest:
        A("| （其余 %d 个 mod） | - | - | - | - | %s |"
          % (len(rest),
             _num(sum(len({r["n"] for r in items}) for _m, items in rest))))
    A("")
    A("## 3 distinct 总数与去重口径")
    A("")
    A("- `packs/il-candidates.jsonl` 每行一条候选，**同一 DLL 内同一 `n` 只留一条**"
      "（保留排序最前的 `(type, method, ilOffset)`），所以行数 = Σ per-DLL distinct。")
    A("- 跨 DLL 按 `n` 去重后 **%s** 条；按 `h` 去重同值（`h` 是 `n` 的 FNV-1a64，"
      "与 `packs/pool.jsonl` 同一契约）。" % _num(len(distinct_n)))
    A("- 排序：`asm` 升序 → `n` 升序 → `type` → `method` → `ilOffset`；"
      "`ilOffset` 是 `ldstr` 在方法体内的 IL 偏移，**十进制**。")
    A("- 落盘：UTF-8 无 BOM、LF、紧凑 JSON（`separators=(\",\",\":\")`）；"
      "同一输入跑两遍逐字节相同。")
    A("")
    A("## 4 逐 mod top-20 明细")
    A("")
    A("| mod | 候选 distinct | 样例 1 | 样例 2 | 样例 3 |")
    A("|---|---|---|---|---|")
    for mod, items in mods[:20]:
        texts = sorted({r["n"] for r in items}, key=lambda t: (len(t), t))[:3]
        while len(texts) < 3:
            texts.append("")
        A("| %s | %s | %s | %s | %s |"
          % (mod, _num(len({r["n"] for r in items})), _md(texts[0]), _md(texts[1]),
             _md(texts[2])))
    A("")
    A("## 5 噪声分类")
    A("")
    A("对 **%s** 条 distinct 候选按互斥优先级归类（顺序即优先级）：" % _num(len(distinct_n)))
    A("")
    A("| 类别 | 条数 | 占比 |")
    A("|---|---|---|")
    for cat in NOISE_ORDER:
        cnt = noise.get(cat, 0)
        A("| %s | %s | %s |" % (cat, _num(cnt), _pct(cnt, len(distinct_n))))
    A("| **合计** | **%s** | **100.00%%** |" % _num(len(distinct_n)))
    A("")
    A("判据：`键引用` = 以 `#` 开头（`#autoLOC_*` / `#LOC_*`，运行时由字典解析，不该翻）；"
      "`URL` = 含协议头或 `www.`；`路径` = 含反斜杠 / 资源扩展名 / ≥3 段斜杠路径；"
      "`XML/富文本` = 有标签或实体；`纯数字/单位` = `textnorm.is_pure_number`；"
      "`标识符` = 无空格的 `A-Za-z0-9_.` 且含 `_`/`.` 或全大写/全小写；"
      "`日志/错误串` = 含 exception / error / failed / warning 等词。")
    A("")
    A("## 6 格式串占比")
    A("")
    A("占位符判据用 `textnorm.features(n)['ph']`（`<<n>>` / `{n}` / `%%s|%%d|%%i|%%f`）："
      "**%s / %s = %s** 的候选含占位符。这些必须保留占位符原样翻译，"
      "不能整句机翻（`{0}` 被翻掉运行时会抛 `FormatException`）。"
      % (_num(len(ph_distinct)), _num(len(distinct_n)),
         _pct(len(ph_distinct), len(distinct_n))))
    A("")
    A("## 7 与 packs/pool.jsonl 的 h 交集")
    A("")
    A("- `packs/pool.jsonl` 12,792 行、h 集合 %s 个；候选里 **%s** 条 distinct 文本的 h "
      "已在池中（%s）——这些是 cfg 与 IL 两路都采到的同一文本，翻译时按 h 去重即可。"
      % (_num(len(pool_h)), _num(len(pool_hit_distinct)),
         _pct(len(pool_hit_distinct), len(distinct_n))))
    A("- 剩下 %s 条是 IL 独有的新增量（其中「其他」类 %s 条）。"
      % (_num(len(distinct_n) - len(pool_hit_distinct)), _num(trans)))
    A("")
    A("## 8 翻译成本估算")
    A("")
    A("按现网实测口径 ~71 prompt + ~33 completion tokens/条"
      "（`tools/i18n_tools/translate.py` 的实测均值）：")
    A("")
    A("| 口径 | 条数 | prompt tokens | completion tokens | 合计 |")
    A("|---|---|---|---|---|")
    for label, cnt in (("全部 sink 可达 distinct", len(distinct_n)),
                       ("只翻「其他（含待译句子）」子集", trans),
                       ("再扣掉与 pool 交集", trans - len(trans_pool))):
        A("| %s | %s | %s | %s | %s |"
          % (label, _num(cnt), _num(cnt * 71), _num(cnt * 33), _num(cnt * 104)))
    A("")
    A("> 单价随模型变化，这里只给 token 量；成本 = tokens × 当前单价。"
      "含占位符的文本会略微抬高 completion（要保住占位符），未单独加系数。")
    A("")
    A("## 9 文本承载 API 调用点普查")
    A("")
    A("同一趟扫描顺带统计（`call` / `callvirt` / `newobj` 计数，含 mod 自己的重载）：")
    A("")
    A("### 9.1 API × DLL 矩阵")
    A("")
    A("| 组 | API（族） | 用到它的 DLL 数 | 调用点 | 去重方法数 | 流入字面量 distinct |")
    A("|---|---|---|---|---|---|")
    for name, f in fam_rows:
        A("| %s | %s | %d | %s | %s | %s |"
          % (f["group"], name, len(f["dlls"]), _num(f["calls"]), _num(f["methods"]),
             _num(len(f["lits"]))))
    A("")
    A("组说明：A = 已覆盖（PatchRegistry 的 16 个 + `GUIContent..ctor`）；"
      "B = 疑似盲区；REFL = 反射写字段；LOG = 日志（对照，不是 UI）。")
    A("")
    A("### 9.2 盲区清单（B 组里调用点 > 0 的，逐 mod）")
    A("")
    blind = [(n, f) for n, f in fam_rows if f["group"] == "B" and f["calls"] > 0]
    if not blind:
        A("（无：B 组没有任何调用点）")
    for name, f in blind:
        A("")
        A("#### %s" % name)
        A("")
        A("| mod | DLL | 调用点 | 该 DLL 上这条路径的字面量 distinct | 样例 |")
        A("|---|---|---|---|---|")
        for asm, calls in sorted(f["dlls"].items(), key=lambda kv: (-kv[1], kv[0])):
            mod = meta["asm2mod"].get(asm, asm)
            own = meta["asm_lits"].get(asm, {}).get(name, set())
            lits = sorted(own, key=lambda t: (len(t), t))[:2]
            A("| %s | %s | %s | %s | %s |"
              % (mod, asm, _num(calls), _num(len(own)),
                 _md("; ".join(lits)) if lits else ""))
        A("")
        A("该 API 合计：%d 个 DLL、%s 个调用点、%s 条 distinct 字面量。"
          % (len(f["dlls"]), _num(f["calls"]), _num(len(f["lits"]))))
    A("")
    A("### 9.3 反射写字段与日志对照")
    A("")
    refl_rows = [f for _n, f in fam_rows if f["group"] == "REFL"]
    refl_hit = sum(1 for f in refl_rows if f["reflection_text"])
    for name, f in [(n, f) for n, f in fam_rows if f["group"] == "REFL"]:
        A("- `%s`：%d 个 DLL、%s 个调用点；**同方法里有 `text`/`m_text` 字面量**"
          "（即真的在写 UI 文本）的族数：%d。这类写入我们的 patch 打不到"
          "（`PropertyInfo.SetValue`），属于真实盲区，但本轮只计数、不进候选。"
          % (name, len(f["dlls"]), _num(f["calls"]), refl_hit))
    A("- `UnityEngine.Debug.Log*`：%s 个调用点（**不是 sink**，只作噪声规模对照——"
      "若把日志也当 UI 文本，误报量会是 sink 候选的 %.1f 倍）。"
      % (_num(log_calls), (log_calls / len(rows)) if rows else 0.0))
    A("")
    A("### 9.4 加权覆盖率结论")
    A("")
    A("- 按**调用点数**加权：A 组 %s / (A %s + B %s + 反射 %s) = **%s**。"
      % (_num(a_calls), _num(a_calls), _num(b_calls), _num(refl_calls),
         _pct(a_calls, a_calls + b_calls + refl_calls)))
    A("- 按**流入字面量 distinct** 加权：A 组 %s / (A %s + B %s) = **%s**。"
      % (_num(a_lits), _num(a_lits), _num(b_lits), _pct(a_lits, a_lits + b_lits)))
    A("- 剩下的大头：%s。" % _top_blind(fam))
    A("- 判断：**16 个 sink 是「够用」而不是「通用」**。要往「任意 mod」走，"
      "按性价比依次补 %s。补法不改现有 sink 契约——它们同样是「改传进来的值」，"
      "只是目标方法多几个。" % _blind_order(fam))
    A("")
    A("## 10 复现命令与产物校验")
    A("")
    A("```")
    A("cd tools")
    A("python -m i18n_tools.ilstrings                    # 全量扫描 + 写 jsonl + 写报告")
    A("python -m i18n_tools.ilstrings --only KSPWheel     # 单 DLL 调试")
    A("python -m i18n_tools.selftest                     # 自检（含 IL 解码 / sink / 幂等断言）")
    A("```")
    A("")
    A("- `packs/il-candidates.jsonl`：%s 行 / %s 字节 / sha256 `%s`"
      % (_num(len(rows)), _num(meta["out_bytes"]), meta["out_sha256"]))
    A("- 幂等：`packs/il-candidates.jsonl` 同一输入连续跑两遍逐字节相同（selftest 有断言）；"
      "本报告里的「耗时」是当次实测值，所以 md 每次跑不完全逐字节相同。")
    A("")
    A("## 11 已知限制")
    A("")
    A("1. **不跨方法**：`GetTitle()` 把字面量返回给调用方再进 sink 的情况抓不到。")
    A("2. **不跟字段**：`private static string Title = \"...\"` 再 `GUI.Label(r, Title)` 抓不到"
      "（规则明确不允许经静态字段）。")
    A("3. **不解析异常处理子句**：`try/catch` 的 handler 内代码不参与数据流（正常路径照常）。")
    A("4. **重载匹配靠参数类型**：`params object[]` 形式的调用只能看到数组；"
      "`string.Format(\"{0}\", x)` 能拿到格式串，`Concat(new[]{a,b})` 的非常量部分会丢。")
    A("5. **反射写字段只做启发式计数**（同方法里有 `text`/`m_text` 字面量），不进候选。")
    A("6. **跨程序集继承链不展开**：`class MyText : TMPro.TMP_Text` 的 `set_text` 调用点"
      "按 MemberRef 的声明类型匹配，若编译器把声明类型写成子类就会漏（实测未出现）。")
    A("")
    return "\n".join(L) + "\n"


def _top_blind(fam):
    """盲区里调用点最多的两三个 API，用来写结论。"""
    items = [(n, f) for n, f in fam.items() if f["group"] == "B" and f["calls"] > 0]
    items.sort(key=lambda kv: -kv[1]["calls"])
    if not items:
        return "B 组没有调用点"
    return "、".join("`%s`（%s 次）" % (n, _num(f["calls"])) for n, f in items[:3])


def _blind_order(fam):
    items = [(n, f) for n, f in fam.items() if f["group"] == "B" and f["calls"] > 0]
    items.sort(key=lambda kv: -kv[1]["calls"])
    if not items:
        return "无（B 组没有调用点）"
    return "、".join("`%s`" % n for n, _f in items)


# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------
def repo_root():
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def default_managed():
    gd = os.environ.get("UI18N_GAMEDATA")
    if not gd:
        from . import harvest
        gd = str(harvest.default_gamedata())
    return os.path.join(os.path.dirname(gd.rstrip("\\/")), "KSP_x64_Data", "Managed")


def _sha256_file(path):
    import hashlib
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def load_pool_hashes(path):
    out = set()
    if not os.path.isfile(path):
        return out
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                out.add(json.loads(line)["h"])
            except (ValueError, KeyError):
                continue
    return out


def main(argv=None):
    ensure_stdout()
    from . import harvest
    args = list(sys.argv[1:] if argv is None else argv)
    gamedata = str(harvest.default_gamedata())
    managed = None
    out = os.path.join(repo_root(), "packs", "il-candidates.jsonl")
    report = os.path.join(repo_root(), "reports", "il-reachability.md")
    pool = os.path.join(repo_root(), "packs", "pool.jsonl")
    only = None
    limit = None
    quiet = False
    no_report = False
    i = 0
    while i < len(args):
        a = args[i]
        if a == "--gamedata":
            i += 1
            gamedata = args[i]
        elif a == "--managed":
            i += 1
            managed = args[i]
        elif a == "--out":
            i += 1
            out = args[i]
        elif a == "--report":
            i += 1
            report = args[i]
        elif a == "--pool":
            i += 1
            pool = args[i]
        elif a == "--only":
            i += 1
            only = args[i]
        elif a == "--limit":
            i += 1
            limit = int(args[i])
        elif a == "--quiet":
            quiet = True
        elif a == "--no-report":
            no_report = True
        elif a in ("-h", "--help"):
            print(__doc__)
            return 0
        else:
            print("[ERR] 未知参数 " + a)
            return 2
        i += 1
    if managed is None:
        managed = default_managed()
    if not quiet:
        print("[il] GameData = %s" % gamedata)
        print("[il] Managed  = %s" % managed)
    res = collect(gamedata, managed, only=only, limit=limit, quiet=quiet)
    rows = build_candidates(res["hits"])
    n = write_jsonl(out, rows)
    asm2mod = {d["asm"]: d["mod"] for d in res["per_dll"]}
    asm_lits = {}
    for d in res["per_dll"]:
        for api, rec in d.get("census", {}).items():
            asm_lits.setdefault(d["asm"], {}).setdefault(rec["family"], set()).update(
                rec["lits"])
    meta = {"asm2mod": asm2mod, "asm_lits": asm_lits,
            "out_bytes": os.path.getsize(out), "out_sha256": _sha256_file(out)}
    if not quiet:
        print("[il] 候选 %d 行 -> %s (%d 字节)" % (n, out, meta["out_bytes"]))
    if not no_report:
        pool_h = load_pool_hashes(pool)
        text = build_report(res, rows, pool_h, meta)
        os.makedirs(os.path.dirname(os.path.abspath(report)), exist_ok=True)
        with open(report, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        if not quiet:
            print("[il] 报告 -> %s (%d 字节)" % (report, len(text.encode("utf-8"))))
    if not quiet:
        print("[il] 扫描 %d 个 DLL / %d 个方法 / %.2fs"
              % (len(res["per_dll"]), sum(d["methods"] for d in res["per_dll"]),
                 res["elapsed"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
