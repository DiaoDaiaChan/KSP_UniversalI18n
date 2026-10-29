#!/usr/bin/env python3
"""影子语义编译的机器检查（tests/runtime-compile）。

为什么要有这个检查：src/UI18n.Runtime 只 net472 且引用真实 KSP 安装的 Managed/*.dll，
没有 KSP 的机器上编不了，于是 8,000 行 Runtime 长期只受 csc 的「纯语法」校验 ——
成员名写错（CS0117 / CS1061）、参数对不上（CS1503）这类语义错一律漏过。
RuntimeCompileCheck.csproj + stubs/ 把 Unity / KSP / Harmony 里**我们真正用到的那部分
形状**顶住，从而能在这里做真正的语义编译。

本文件守四件事：

  1. **覆盖**：影子工程实际参与的 .cs 集合必须恰好等于 src/UI18n.Runtime 下全部 .cs
     （多一个 = 把 obj/bin 卷进来了；少一个 = glob 被改窄、新增文件没人编）。
  2. **编译**：dotnet build 必须 0 错误 —— 这就是那 8,000 行的语义编译本身。
  3. **反向**（不会红的门禁等于没有）：临时目录里故意写一句 Unity API 打错，
     必须编红；编不红说明桩把语义错误吞掉了。
  4. **边界**：真实发布工程 src/UI18n.Runtime/UI18n.Runtime.csproj 绝不能包含
     tests/runtime-compile 的桩源码（桩混进发布 DLL 会顶掉真实类型）。

它不证明什么：桩不是 Unity / KSP 的真实签名。桩写错了、真机 API 变了，这里都看不出来 ——
最终以真机编译为准（docs/06 §构建契约）。所以这个门禁的定位是「抓我们自己的笔误」，
不是「替代真机编译」。

用法：
    python tests/runtime-compile/test-shadow-compile.py
    python tests/runtime-compile/test-shadow-compile.py --tfm net10.0
退出码：0 = 全过；1 = 有失败；2 = 跳过（本机没有 dotnet，或没有 .NET Framework 参考程序集）。
"""

import argparse
import json
import os
import platform
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
PROJECT = os.path.join(HERE, "RuntimeCompileCheck.csproj")
STUBS = os.path.join(HERE, "stubs")
RUNTIME_DIR = os.path.join(REPO, "src", "UI18n.Runtime")
RUNTIME_PROJECT = os.path.join(RUNTIME_DIR, "UI18n.Runtime.csproj")
NET472_REFS = r"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2"

SKIP = 2


class Checker(object):
    def __init__(self, quiet=False):
        self.checks = 0
        self.failures = 0
        self.quiet = quiet

    def expect(self, what, ok, detail=""):
        self.checks += 1
        if ok:
            if not self.quiet:
                print("  ok   : " + what)
        else:
            self.failures += 1
            print("  FAIL : " + what + (("  <-- " + detail) if detail else ""))


def run(cmd, cwd=None, timeout=300):
    try:
        p = subprocess.Popen(cmd, cwd=cwd or REPO, stdout=subprocess.PIPE,
                             stderr=subprocess.STDOUT)
        out, _ = p.communicate(timeout=timeout)
        return p.returncode, out.decode("utf-8", "replace")
    except subprocess.TimeoutExpired:
        p.kill()
        p.communicate()
        return -1, "timeout after %ds: %s" % (timeout, " ".join(cmd))
    except OSError as e:
        return -1, str(e)


def runtime_sources():
    """src/UI18n.Runtime 下全部 .cs（跳过 obj/bin），规范化绝对路径。"""
    out = set()
    for dirpath, dirnames, filenames in os.walk(RUNTIME_DIR):
        dirnames[:] = [d for d in dirnames if d not in ("obj", "bin")]
        for name in filenames:
            if name.endswith(".cs"):
                out.add(os.path.normcase(os.path.abspath(os.path.join(dirpath, name))))
    return out


def compile_items(tfm):
    """问 MSBuild 要 Compile 项清单（-getItem 输出 JSON）。"""
    rc, out = run(["dotnet", "msbuild", PROJECT, "-p:TargetFramework=" + tfm,
                   "-getItem:Compile", "-nologo"])
    if rc != 0:
        return None, out
    start = out.find("{")
    end = out.rfind("}")
    if start < 0 or end < start:
        return None, out
    try:
        data = json.loads(out[start:end + 1])
    except ValueError as e:
        return None, "JSON 解析失败: %s" % (e,)
    items = data.get("Items", {}).get("Compile", [])
    paths = set()
    for it in items:
        full = it.get("FullPath") or it.get("Identity") or ""
        if full:
            paths.add(os.path.normcase(os.path.abspath(full)))
    return paths, out


def pick_tfm(requested):
    if requested:
        return requested
    env = os.environ.get("UI18N_SHADOW_TFM")
    if env:
        return env
    if platform.system() == "Windows" and os.path.isdir(NET472_REFS):
        return "net472"
    return "net10.0"


def negative_control(tfm="net10.0"):
    """桩 + 一句打错的 Unity API，必须编红（证明语义检查真的在跑）。"""
    tmp = tempfile.mkdtemp(prefix="ui18n-shadow-neg-")
    try:
        shutil.copytree(STUBS, os.path.join(tmp, "stubs"))
        with open(os.path.join(tmp, "Broken.cs"), "w", encoding="utf-8", newline="\n") as f:
            f.write("internal static class Broken\n{\n"
                    "    internal static void Touch()\n    {\n"
                    "        UnityEngine.GUI.NoSuchMemberHere();\n"
                    "        UnityEngine.Texture2D t = new UnityEngine.Texture2D(1, 2, 3, 4);\n"
                    "    }\n}\n")
        with open(os.path.join(tmp, "Neg.csproj"), "w", encoding="utf-8", newline="\n") as f:
            f.write('<Project Sdk="Microsoft.NET.Sdk">\n'
                    "  <PropertyGroup>\n"
                    "    <TargetFramework>" + tfm + "</TargetFramework>\n"
                    "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>\n"
                    "    <ImplicitUsings>disable</ImplicitUsings>\n"
                    "    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>\n"
                    "  </PropertyGroup>\n"
                    "  <ItemGroup>\n"
                    '    <Compile Include="stubs\\**\\*.cs" />\n'
                    '    <Compile Include="Broken.cs" />\n'
                    "  </ItemGroup>\n"
                    "</Project>\n")
        rc, out = run(["dotnet", "build", os.path.join(tmp, "Neg.csproj"),
                       "-v", "q", "-nologo"], cwd=tmp)
        codes = [c for c in ("CS0117", "CS1061", "CS1503", "CS1729") if c in out]
        return rc, codes, out
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main(argv=None):
    ap = argparse.ArgumentParser(description="影子语义编译检查（Runtime 在没有 KSP 的机器上编译）")
    ap.add_argument("--tfm", default=None,
                    help="目标框架（默认：Windows 且参考程序集在 → net472，否则 net10.0）")
    ap.add_argument("--quiet", action="store_true")
    args = ap.parse_args(argv)

    if shutil.which("dotnet") is None:
        print("[shadow-compile] SKIP：本机没有 dotnet（这一条只在有 SDK 的机器上跑）")
        return SKIP
    if not os.path.isfile(PROJECT):
        print("[shadow-compile] 找不到影子工程: " + PROJECT)
        return 1

    tfm = pick_tfm(args.tfm)
    c = Checker(args.quiet)
    print("[shadow-compile] tfm=" + tfm + "  project=" + os.path.relpath(PROJECT, REPO))

    # ---- 1. 覆盖：影子工程编的 .cs 集合 == src/UI18n.Runtime 下全部 .cs ----
    items, raw = compile_items(tfm)
    if items is None:
        tail = raw.strip().splitlines()[-1] if raw.strip() else ""
        c.expect("能向 MSBuild 问到 Compile 项清单", False, tail)
    else:
        stub_items = set(p for p in items if os.path.normcase(STUBS) in p)
        runtime_items = set(p for p in items if os.path.normcase(RUNTIME_DIR) in p)
        expected = runtime_sources()
        diff = (expected - runtime_items) | (runtime_items - expected)
        c.expect("影子工程编到 src/UI18n.Runtime 的每一个 .cs（%d 个）" % len(expected),
                 runtime_items == expected,
                 "缺 %d / 多 %d: %s" % (len(expected - runtime_items),
                                       len(runtime_items - expected),
                                       ", ".join(sorted(os.path.basename(p) for p in diff)[:6])))
        c.expect("有桩源码参与编译（%d 个文件）" % len(stub_items), len(stub_items) >= 4,
                 "stubs/ 下 .cs 少于 4 个：桩被删了？")
        c.expect("没有把 obj/ 或 bin/ 卷进来",
                 not any(os.sep + "obj" + os.sep in p or os.sep + "bin" + os.sep in p
                         for p in items),
                 "Compile 项里出现 obj/bin")

    # ---- 2. 编译：0 错误 ----
    rc, out = run(["dotnet", "build", PROJECT, "-f", tfm, "-c", "Debug", "-v", "q", "-nologo"])
    errs = [l for l in out.splitlines() if "error CS" in l or "error MSB" in l]
    c.expect("语义编译 0 错误", rc == 0 and not errs,
             ("rc=%d " % rc) + " | ".join(errs[:3]))

    # ---- 3. 反向：桩必须能编红一句打错的 Unity API ----
    nrc, codes, nout = negative_control()
    c.expect("反向对照编红（桩不会吞掉语义错）", nrc != 0 and bool(codes),
             "rc=%d codes=%s" % (nrc, ",".join(codes) or "none"))
    c.expect("反向对照的错误指向被改坏的那个文件", "Broken.cs" in nout,
             "输出里没有 Broken.cs")

    # ---- 4. 边界：桩不许混进真实发布工程 ----
    runtime_proj = ""
    try:
        with open(RUNTIME_PROJECT, "r", encoding="utf-8-sig") as f:
            runtime_proj = f.read()
    except OSError as e:
        c.expect("能读真实发布工程 UI18n.Runtime.csproj", False, str(e))
    if runtime_proj:
        c.expect("发布工程不包含 tests/runtime-compile 的桩",
                 "runtime-compile" not in runtime_proj and "stubs" not in runtime_proj,
                 "UI18n.Runtime.csproj 里出现了 runtime-compile / stubs 字样")

    print("")
    print("[shadow-compile] checks=%d failures=%d" % (c.checks, c.failures))
    return 0 if c.failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
