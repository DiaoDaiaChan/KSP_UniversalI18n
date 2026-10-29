#!/usr/bin/env python3
r"""硬编码门禁：本仓库是通用 i18n mod，不允许把某个具体 mod 的行为写进代码。

为什么要有这个文件：2026-10 做过一次全量硬编码审计（_audit/hardcode/），结论是 P0/P1 清零、
src/ 非注释代码里一个 mod 名都不剩。但「审计过一次」不是「永远干净」—— 只要没有门禁，
下一个人想在代码里写 if (modId == "SomeMod") 时没人拦。这个脚本把那次审计的核心不变量变成
可重复执行的检查，进 CI。

四条不变量（每条都对应审计里发现过的一类真实风险）：
  A mod 名不进代码：src/ 与 tools/ 的代码（去注释 / 去 docstring 后）不得出现任何已知 modId
    或已知知名 mod 名。数据层（packs/*.jsonl、packs/mods.json）当然可以有 mod 名 —— 那些是数据。
  B 机器路径不进代码：不得写死 C:\Users\某人 / D:\Kerbal Space Program / D:\Games 这类装机路径
    （审计里抓到的 harvest.py KSP_GAMEDATA_FALLBACKS 就是这一类）。
  C 热路径不允许每次 new Regex：src/ 的 new Regex(...) 必须落在 static 字段上，否则每次文本出口
    调用都会分配一个 Regex 对象（审计的性能调研把这一类归为「可避免的 GC」）。
  D 自造的 UI18n-* 字面量必须登记：本产品会用伪 mod 名表示「查不出宿主」（UI18n-Unattributed）与
    「配置缓存通道」（UI18n-ConfigCache）这类自造桶。它们不是第三方 mod 名，但同一个名字会在
    C# 与 3 个 py 文件里各写一遍（审计存疑项 S3：改名要改 5 处）。所以新增一个桶名必须在这里
    登记一次，让下一个人看到「这个名字出现在两门语言里」这件事。

豁免方式：行尾写注释「硬编码豁免: <原因>」（原因至少 6 个字），只对该行生效。
豁免必须能被下一个读者判断「为什么这里可以有」—— 没有原因就是没有豁免。

用法：
    python tests/hardcode/test-no-mod-hardcode.py            # 跑全部检查
    python tests/hardcode/test-no-mod-hardcode.py --verbose  # 打印探针规模与统计

退出码：0 = 全过；1 = 有失败。
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys

SKIP_DIRS = {
    ".git", "obj", "bin", "_scratch", "_audit", "build", "dist", "node_modules",
    "Library", "Temp", "Pods", "__pycache__", ".venv", "venv",
}
CODE_EXTS = {".cs", ".cfg", ".csproj", ".py", ".ps1", ".template"}
EXEMPT_MARK = "硬编码豁免"

# 平台名：这些不是「某个 mod」，而是游戏本体与运行时（KSP 的 Squad 程序集、Assembly-CSharp、
# Unity、Harmony）。src/UI18n.Core 的 csproj 里明确写了「不许引用它们」，所以它们会出现在
# 错误信息里；数据层里它们也确实是 pack 名（packs/zh-cn/Squad.jsonl）。
PLATFORM_NAMES = {
    "Assembly-CSharp", "Squad", "UnityEngine", "Unity", "TMPro", "HarmonyLib",
    "0Harmony", "Harmony", "KSP", "Kerbal",
}
# 本产品自造的伪 mod：这不是外部 mod 的名字，而是我们自己的命名空间。
PRODUCT_PREFIX = "UI18n-"

# 不变量 D 的登记表：代码里允许出现的 UI18n-* 字面量（注释与 docstring 不算，已剥）。
#   bucket  = 伪 mod 桶名，会被当成 mod 身份写进数据、包名、GUI 行；改名要同时改 C# 与 py。
#   product = 产品自己的字符串（User-Agent、诊断包文件名前缀），不参与 mod 身份。
# 新增一个桶名 = 一次跨语言改名风险，所以必须在这里登记（一行），并在改动说明里写清「两边都改了」。
PSEUDO_PREFIX_LITERALS = {
    "UI18n-Unattributed": "bucket",
    "UI18n-ConfigCache": "bucket",
    "UI18n-Updater": "product",
    "UI18n-diag": "product",
}
PSEUDO_LITERAL_RE = re.compile(r"UI18n-[A-Za-z][A-Za-z0-9_]*")

# 当「已知知名 mod」还不在 packs/mods.json 里时用的探针样本（防止有人写一个我们还没收录的
# mod 名）。这份清单只用于查找，不参与任何运行时行为，也不进任何产物。
FAMOUS_PROBE = (
    "MechJeb", "MechJeb2", "RealFuels", "RealismOverhaul", "Kopernicus", "Waterfall",
    "TUFX", "B9PartSwitch", "Kerbalism", "Scatterer", "TweakScale", "InfernalRobotics",
    "RestockPlus", "TexturesUnlimited", "KSPWheel", "RCSBuildAid", "BurstPQS",
    "AtmosphereAutopilot", "ModuleManager", "NearFuture", "CryoEngines",
    "KerbalAtomics", "SystemHeat", "ProceduralParts", "FerramAerospaceResearch",
    "Trajectories", "DockingPortAlignmentIndicator", "ToolbarControl",
)

# 允许的绝对路径：.NET Framework 参考程序集是 SDK 自带的标准路径，不是「谁的装机路径」。
PATH_ALLOW = (
    re.compile(r"[A-Za-z]:\\Program Files \(x86\)\\Reference Assemblies", re.I),
    re.compile(r"[A-Za-z]:\\Program Files\\dotnet", re.I),
)
MACHINE_PATH = (
    re.compile(r"[A-Za-z]:[\\/]+Users[\\/]", re.I),
    re.compile(r"[A-Za-z]:[\\/]+Kerbal Space Program", re.I),
    re.compile(r"[A-Za-z]:[\\/]+Games[\\/]", re.I),
    re.compile(r"[A-Za-z]:[\\/]+SteamLibrary", re.I),
)



def repo_root() -> str:
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.dirname(os.path.dirname(here))


def walk_code(root: str, sub: str):
    base = os.path.join(root, sub)
    if not os.path.isdir(base):
        return
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for name in filenames:
            ext = os.path.splitext(name)[1].lower()
            if ext in CODE_EXTS or name.endswith(".netkan.template"):
                yield os.path.join(dirpath, name)


def _strip_c_like(text: str) -> str:
    """去掉 // 与 /* */ 注释，字符串/字符字面量原样保留（换行保留以维持行号）。

    为什么保留字符串内容：mod 名与装机路径几乎总是以字符串字面量出现，清空字面量正好
    把要抓的东西擦掉。注释则相反 —— 注释里举的例子（「比如 ModX 的命名空间是 MuMech」）
    是合法文档，不该算硬编码。
    """
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if c == "/" and nxt == "/":
            while i < n and text[i] != "\n":
                i += 1
            continue
        if c == "/" and nxt == "*":
            i += 2
            while i < n and not (text[i] == "*" and i + 1 < n and text[i + 1] == "/"):
                if text[i] == "\n":
                    out.append("\n")
                i += 1
            i += 2
            continue
        if c == "@" and nxt == '"':
            i += 2
            while i < n:
                if text[i] == '"' and i + 1 < n and text[i + 1] == '"':
                    out.append(text[i])
                    i += 2
                    continue
                if text[i] == '"':
                    i += 1
                    break
                out.append(text[i])
                i += 1
            out.append('"')
            continue
        if c == '"':
            out.append(c)
            i += 1
            while i < n:
                if text[i] == "\\":
                    if i + 1 < n:
                        out.append(text[i : i + 2])
                    i += 2
                    continue
                out.append(text[i])
                if text[i] == '"':
                    i += 1
                    break
                i += 1
            continue
        if c == "'":
            out.append(c)
            i += 1
            while i < n:
                if text[i] == "\\":
                    if i + 1 < n:
                        out.append(text[i : i + 2])
                    i += 2
                    continue
                out.append(text[i])
                if text[i] == "'":
                    i += 1
                    break
                i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)



PS_ESCAPE = chr(96)  # PowerShell 的转义符（反引号）；用 chr() 写，免得源码里出现怪引号


def _strip_ps1(text: str) -> str:
    """PowerShell：去掉 # 行注释与 <# #> 块注释，字符串原样保留。"""
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if c == "<" and nxt == "#":
            i += 2
            while i < n and not (text[i] == "#" and i + 1 < n and text[i + 1] == ">"):
                if text[i] == "\n":
                    out.append("\n")
                i += 1
            i += 2
            continue
        if c == "#":
            while i < n and text[i] != "\n":
                i += 1
            continue
        if c == "@" and nxt in ("'", '"'):
            quote = nxt
            out.append(quote)
            i += 2
            while i < n:
                if text[i] == quote and i + 1 < n and text[i + 1] == "@":
                    i += 2
                    break
                out.append(text[i])
                i += 1
            out.append(quote)
            continue
        if c in ('"', "'"):
            quote = c
            out.append(quote)
            i += 1
            while i < n:
                if quote == '"' and text[i] == PS_ESCAPE:
                    if i + 1 < n:
                        out.append(text[i : i + 2])
                    i += 2
                    continue
                if text[i] == quote:
                    out.append(quote)
                    i += 1
                    break
                out.append(text[i])
                i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


def _strip_py(text: str) -> str:
    """Python：去掉 # 注释与三引号 docstring（docstring 里的用法示例属文档），
    单双引号字符串原样保留。"""
    out = []
    i, n = 0, len(text)
    triples = ('"""', "'''")
    while i < n:
        c = text[i]
        if c == "#":
            while i < n and text[i] != "\n":
                i += 1
            continue
        triple = text[i : i + 3]
        if triple in triples:
            i += 3
            while i < n and text[i : i + 3] != triple:
                if text[i] == "\n":
                    out.append("\n")
                i += 1
            i += 3
            continue
        if c in ('"', "'"):
            quote = c
            out.append(quote)
            i += 1
            while i < n:
                if text[i] == "\\":
                    if i + 1 < n:
                        out.append(text[i : i + 2])
                    i += 2
                    continue
                out.append(text[i])
                if text[i] == quote:
                    i += 1
                    break
                i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


def strip_for(path: str, text: str) -> str:
    ext = os.path.splitext(path)[1].lower()
    if ext in (".cs", ".cfg"):
        return _strip_c_like(text)
    if ext == ".ps1":
        return _strip_ps1(text)
    if ext == ".py":
        return _strip_py(text)
    return re.sub(r"<!--.*?-->", lambda m: "\n" * m.group(0).count("\n"), text, flags=re.S)


def load_probe_names(root: str):
    names = set()
    mods_path = os.path.join(root, "packs", "mods.json")
    if os.path.isfile(mods_path):
        with open(mods_path, "r", encoding="utf-8-sig") as handle:
            doc = json.load(handle)
        for entry in doc.get("mods") or []:
            mid = str(entry.get("modId") or "").strip()
            if mid:
                names.add(mid)
    packs = os.path.join(root, "packs", "zh-cn")
    if os.path.isdir(packs):
        for name in os.listdir(packs):
            if not name.endswith(".jsonl"):
                continue
            stem = name[: -len(".jsonl")]
            if stem.endswith(".il"):
                stem = stem[: -len(".il")]
            names.add(stem)
    names.update(FAMOUS_PROBE)
    return sorted(n for n in names if not n.startswith(PRODUCT_PREFIX))


def build_patterns(names):
    out = []
    for name in names:
        if name in PLATFORM_NAMES or name.startswith(PRODUCT_PREFIX):
            continue
        out.append((name, re.compile(r"(?<![A-Za-z0-9_])" + re.escape(name) + r"(?![A-Za-z0-9_])", re.I)))
    return out


def line_exempt(raw_lines, index: int) -> bool:
    """该行（或紧邻上一行）带豁免标记、且标记后面写了原因，才豁免。"""
    for probe in (index, index - 1):
        if 0 <= probe < len(raw_lines) and EXEMPT_MARK in raw_lines[probe]:
            tail = raw_lines[probe].split(EXEMPT_MARK, 1)[1]
            if len(tail.strip(" :#-*/	")) >= 6:
                return True
    return False



def _logical_statements(lines):
    """把物理行合成「逻辑语句」：括号/引号没闭合就继续往下拼。

    为什么需要：add_argument(...) 与多行比较常跨好几行，只按物理行扫会漏掉
    default= 里的 mod 名。返回 [(起始行下标, 语句文本)]。
    """
    statements = []
    buffer = []
    start = None
    depth = 0
    for index, text in enumerate(lines):
        if start is None:
            if not text.strip():
                continue
            start = index
        buffer.append(text)
        depth += text.count("(") + text.count("[") + text.count("{")
        depth -= text.count(")") + text.count("]") + text.count("}")
        if depth <= 0 or text.rstrip().endswith("\\"):
            statements.append((start, "\n".join(buffer)))
            buffer = []
            start = None
            depth = 0
    if buffer:
        statements.append((start if start is not None else 0, "\n".join(buffer)))
    return statements


def _depth_before(lines):
    """每行语句执行前的花括号深度（近似：不处理字符串里的花括号，代码里没有）。"""
    depths = []
    depth = 0
    for text in lines:
        depths.append(depth)
        depth += text.count("{") - text.count("}")
    return depths


def _statement_at(lines, index):
    """返回与第 index 行同一层、同一段的语句文本（用于判断它是不是静态字段）。

    为什么不看「上下几行里有没有 static」：那样会把
    public static string P() { var r = new Regex("x"); } 也放过去 —— 外层签名里的
    static 跟这个 Regex 是否被复用毫无关系。这里按花括号深度切段：深度相同的相邻行
    算同一段（多行初始化因此仍能被认出来），遇到深度变化即止。只有同一段里含 static
    （即它是静态字段/静态初始化）才算「只分配一次」。
    """
    if not lines or index < 0 or index >= len(lines):
        return ""
    depths = _depth_before(lines)
    level = depths[index]
    start = index
    while start > 0 and depths[start - 1] == level:
        start -= 1
    end = index
    while end + 1 < len(lines) and depths[end + 1] == level:
        end += 1
        if lines[end].rstrip().endswith(";"):
            break
    return "\n".join(lines[start:end + 1])



def scan(root: str, verbose: bool):
    """返回 (checks, failures)。四类不变量分别扫，报告里分门别类。"""
    failures = []
    checks = 0
    names = load_probe_names(root)
    patterns = build_patterns(names)
    if verbose:
        print("[no-mod-hardcode] 探针名 %d 个（packs/mods.json + packs/zh-cn/*.jsonl + 内置样本）" % len(patterns))

    regex_new = re.compile(r"new\s+Regex\s*\(")
    branch = re.compile(r"^\s*(if|elif)\b")

    # ---- 不变量 C：热路径不得每次 new Regex（src/*.cs）----
    for path in walk_code(root, "src"):
        rel = os.path.relpath(path, root).replace(os.sep, "/")
        if not rel.endswith(".cs"):
            continue
        with open(path, "r", encoding="utf-8", errors="replace") as handle:
            raw = handle.read()
        raw_lines = raw.splitlines()
        stripped_c = strip_for(path, raw)
        stripped_lines = stripped_c.splitlines()
        for index, text in enumerate(stripped_lines):
            checks += 1
            if not regex_new.search(text) or line_exempt(raw_lines, index):
                continue
            if "static" not in _statement_at(stripped_lines, index):
                failures.append("%s:%d [C 每次 new Regex] 必须落在 static 字段上：%s"
                                % (rel, index + 1, text.strip()[:110]))

    for sub in ("src", "tools"):
        for path in walk_code(root, sub):
            rel = os.path.relpath(path, root).replace(os.sep, "/")
            with open(path, "r", encoding="utf-8", errors="replace") as handle:
                raw = handle.read()
            raw_lines = raw.splitlines()
            stripped_lines = strip_for(path, raw).splitlines()

            for index, text in enumerate(stripped_lines):
                checks += 1
                if not text.strip() or line_exempt(raw_lines, index):
                    continue
                # ---- 不变量 B：机器私有路径（src 与 tools 都管）----
                if not any(allow.search(text) for allow in PATH_ALLOW):
                    for pattern in MACHINE_PATH:
                        if pattern.search(text):
                            failures.append("%s:%d [B 机器路径] 命中 %s：%s"
                                            % (rel, index + 1, pattern.pattern, text.strip()[:110]))
                            break

                # ---- 不变量 D：自造 UI18n-* 字面量必须登记（src 与 tools 都管）----
                for literal in PSEUDO_LITERAL_RE.findall(text):
                    if literal not in PSEUDO_PREFIX_LITERALS:
                        failures.append("%s:%d [D 未登记的 UI18n-* 字面量] %r："
                                        "新增伪 mod 桶 / 产品字符串必须登记到 PSEUDO_PREFIX_LITERALS"
                                        "（同一个桶名会同时出现在 C# 与 py，改名要改 N 处）"
                                        % (rel, index + 1, literal))

            # ---- 不变量 A：src/ 里一个 mod 名都不许有（严格）----
            if sub == "src":
                for index, text in enumerate(stripped_lines):
                    checks += 1
                    if not text.strip() or line_exempt(raw_lines, index):
                        continue
                    for name, pattern in patterns:
                        if pattern.search(text):
                            failures.append("%s:%d [A mod 名] 出现 %r：%s"
                                            % (rel, index + 1, name, text.strip()[:110]))
                            break
                continue

            # ---- 不变量 A2：tools/ 只管「会改行为」的两类位置 ----
            # 一是不进夹具的 if/elif 分支（写死某个 mod 的行为就是从这里长出来的）；
            # 二是 add_argument(...) 里的默认值/候选（一个写死的 default 会悄悄改所有人的结果）。
            for index, text in enumerate(stripped_lines):
                checks += 1
                if not text.strip() or line_exempt(raw_lines, index):
                    continue
                if not branch.match(text):
                    continue
                for name, pattern in patterns:
                    if pattern.search(text):
                        failures.append("%s:%d [A2 tools 分支] if/elif 里出现 %r：%s"
                                        % (rel, index + 1, name, text.strip()[:110]))
                        break
            for start, statement in _logical_statements(stripped_lines):
                checks += 1
                if "add_argument(" not in statement or line_exempt(raw_lines, start):
                    continue
                for name, pattern in patterns:
                    if pattern.search(statement):
                        failures.append("%s:%d [A2 tools 默认值] add_argument 里出现 %r：%s"
                                        % (rel, start + 1, name, statement.strip()[:110]))
                        break

    return checks, failures


# ---------------------------------------------------------------- 自检样本
# 门禁的红/绿必须能被证明。这里合成两棵最小仓库树：一棵故意违规（每类不变量各一条），
# 一棵干净但含「带豁免注释」的行 —— 前者必须被全部抓出来，后者必须一条都不报。
# 样本里的 mod 名用 FAMOUS_PROBE 里的老牌 mod（不依赖 packs/mods.json，临时目录里没有它）。
_SELF_DIRTY_SRC = """namespace N {
    public static class Probe {
        private static readonly Regex Good = new Regex("keep");
        public static bool IsSpecial(string modId) {
            if (modId == "ModuleManager") { return true; }
            return false;
        }
        public static string Path() {
            var bad = new Regex("each call");
            return "D:\\Kerbal Space Program\\GameData";
        }
    }
}
"""

_SELF_DIRTY_TOOLS_PY = """import argparse


def build(parser):
    parser.add_argument("--mod", default="Kopernicus")
    parser.add_argument("--other", default=None)


def use(mod):
    if mod == "TUFX":
        return 1
    return 0
"""

_SELF_DIRTY_TOOLS_PS1 = """param([string]$ModId)
if ($ModId -eq 'Waterfall') {
    Write-Host 'special'
}
"""

_SELF_CLEAN_SRC = """namespace N {
    public static class Probe {
        private static readonly Regex Cache = new Regex("[0-9]+");
        public static bool Known(string modId) {
            return modId != null && modId.Length > 0;
        }
    }
}
"""

_SELF_CLEAN_TOOLS_PY = """import argparse


def build(parser):
    parser.add_argument("--mod", default=None)
    parser.add_argument("--other", default=None)


def use(mod, index):
    if index == 2:  # 硬编码豁免: 自检样本故意留一行豁免注释
        return mod == "SomeMod"
    return mod
"""


def _self_write(root, rel, text):
    target = os.path.join(root, rel.replace("/", os.sep))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def _self_tree(root, files):
    for rel, text in files.items():
        _self_write(root, rel, text)
    return root


def selftest() -> int:
    """跑合成样本，断言门禁「该红的红、该绿的绿」。返回 0 = 自检通过。"""
    import tempfile

    results = []

    def expect(label, ok, detail=""):
        results.append((label, bool(ok), detail))

    with tempfile.TemporaryDirectory(prefix="no-mod-hardcode-selftest-") as tmp:
        dirty = _self_tree(os.path.join(tmp, "dirty"), {
            "src/N/Probe.cs": _SELF_DIRTY_SRC,
            "tools/some_tool.py": _SELF_DIRTY_TOOLS_PY,
            "tools/some_tool.ps1": _SELF_DIRTY_TOOLS_PS1,
        })
        checks, failures = scan(dirty, verbose=False)
        joined = "\n".join(failures)
        expect("违规树：扫描行数 > 0", checks > 0, "checks=%d" % checks)
        expect("违规树：被抓出至少 5 条", len(failures) >= 5, "failures=%d" % len(failures))
        expect("违规树：报出 A（src 里的 mod 名）", "[A mod 名]" in joined, joined[:300])
        expect("违规树：报出 A2 分支（tools 的 if 里的 mod 名）",
               "[A2 tools 分支]" in joined, joined[:300])
        expect("违规树：报出 A2 默认值（add_argument default 里的 mod 名）",
               "[A2 tools 默认值]" in joined, joined[:300])
        expect("违规树：报出 B（机器私有路径）", "[B 机器路径]" in joined, joined[:300])
        expect("违规树：报出 C（非 static 的 new Regex）",
               "[C 每次 new Regex]" in joined, joined[:300])
        expect("违规树：main() 退出码为 1",
               main(["--root", dirty]) == 1, "main 未返回 1")

        clean = _self_tree(os.path.join(tmp, "clean"), {
            "src/N/Probe.cs": _SELF_CLEAN_SRC,
            "tools/some_tool.py": _SELF_CLEAN_TOOLS_PY,
        })
        checks, failures = scan(clean, verbose=False)
        expect("干净树：扫描行数 > 0", checks > 0, "checks=%d" % checks)
        expect("干净树：零失败", not failures, "\n".join(failures)[:400])
        expect("干净树：main() 退出码为 0",
               main(["--root", clean]) == 0, "main 未返回 0")

        # 豁免只作用于自己被标注的那一行：同一文件里另一行的违规仍要报。
        exempt = _self_tree(os.path.join(tmp, "exempt"), {
            "tools/t.py": (
                'def pick(mod):\n'
                '    if mod == "Kopernicus":  # 硬编码豁免: 一次性迁移脚本的固定入口\n'
                '        return 1\n'
                '    if mod == "Kopernicus":\n'
                '        return 2\n'
                '    return 0\n'
            ),
        })
        _checks, failures = scan(exempt, verbose=False)
        expect("豁免：只放行被标注的那一行", len(failures) == 1,
               "failures=%d (%s)" % (len(failures), "; ".join(failures)[:300]))
        # 豁免没有写原因（或原因太短）就不算豁免 —— 否则它会变成静音开关。
        vague = _self_tree(os.path.join(tmp, "vague"), {
            "tools/t.py": (
                'def pick(mod):\n'
                '    if mod == "Kopernicus":  # 硬编码豁免: 方便\n'
                '        return 1\n'
                '    return 0\n'
            ),
        })
        _checks, failures = scan(vague, verbose=False)
        expect("豁免：没写清原因的不放行", len(failures) == 1,
               "failures=%d" % len(failures))

        # 不变量 D：自造 UI18n-* 字面量必须登记（同一个桶名会在 C# 与 py 里各写一遍 = 改名风险）。
        pseudo_dirty = _self_tree(os.path.join(tmp, "pseudo-dirty"), {
            "tools/p.py": 'UNATTRIBUTED = "UI18n-Mystery"\n',
        })
        _checks, failures = scan(pseudo_dirty, verbose=False)
        expect("未登记的 UI18n-* 字面量：被报出",
               len(failures) == 1 and "[D 未登记的 UI18n-* 字面量]" in failures[0],
               "failures=%s" % "; ".join(failures)[:300])
        pseudo_clean = _self_tree(os.path.join(tmp, "pseudo-clean"), {
            "tools/p.py": 'UNATTRIBUTED = "UI18n-Unattributed"\n',
        })
        _checks, failures = scan(pseudo_clean, verbose=False)
        expect("已登记的 UI18n-* 字面量：不报", not failures,
               "failures=%s" % "; ".join(failures)[:300])

    bad = [item for item in results if not item[1]]
    for label, ok, detail in results:
        print("[no-mod-hardcode-selftest] %s %s%s"
              % ("ok  " if ok else "FAIL", label, "" if ok else "  -> " + detail))
    print("[no-mod-hardcode-selftest] checks=%d failures=%d" % (len(results), len(bad)))
    return 0 if not bad else 1


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="硬编码门禁（通用 i18n mod 不许写死某个 mod）")
    parser.add_argument("--root", default=None, help="仓库根（默认自动向上两级）")
    parser.add_argument("--verbose", action="store_true", help="打印探针规模与统计")
    parser.add_argument("--self-test", action="store_true",
                        help="跑合成样本，证明门禁确实会红/会绿")
    args = parser.parse_args(argv)
    if args.self_test:
        return selftest()
    root = os.path.abspath(args.root or repo_root())
    checks, failures = scan(root, args.verbose)
    if args.verbose:
        print("[no-mod-hardcode] 检查行数 %d，失败 %d" % (checks, len(failures)))
    if failures:
        for item in failures:
            print("[no-mod-hardcode] FAIL " + item)
        print("[no-mod-hardcode] checks=%d failures=%d" % (checks, len(failures)))
        return 1
    print("[no-mod-hardcode] checks=%d failures=0" % checks)
    return 0


if __name__ == "__main__":
    sys.exit(main())
