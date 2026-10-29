#!/usr/bin/env python3
"""Runtime 行为门禁的机器检查（tests/runtime-behavior）。

为什么要有这个检查：tests/runtime-compile 只证明 src/UI18n.Runtime 那 29 个 .cs **能被编译**。
真正会出事的是胶水层的行为 —— 包加载顺序、三层优先级、骨架填回、keep、per-mod 策略、
缺命中捕获、开关短路、条目上限 —— 这些一行都没有被执行过，只能靠 Core 单测间接覆盖。

RuntimeBehavior.csproj 把 stubs 的桩与 src/UI18n.Runtime 的真实源码编进同一个 Exe，
本文件守六件事：

  1. **能编**：dotnet build 必须成功（桩 + 真实 Runtime + 真 Core）。
  2. **能跑**：程序退出码 0，汇总行 >0 个场景、0 失败。
  3. **断言数**：至少 MIN_CHECKS 条断言真的执行了（防止有人把场景清空还留着绿）。
  4. **反向**（不会红的门禁等于没有）：把一份场景源码改坏（期望译文改成错译）后必须退出非 0，
     并且打出 "FAIL:"。编不红 / 跑不红说明断言根本没在起作用。
  5. **边界**：发布工程 src/UI18n.Runtime/UI18n.Runtime.csproj 不能包含本目录的源码
     （测试代码混进发布 DLL 会把 Main 也带进去）。
  6. **不改产品**：src/UI18n.Runtime 里不许出现 InternalsVisibleTo —— 本门禁靠「同程序集编译」
     拿到 internal 访问权，一旦产品侧为此开后门就说明这条路被绕开了。

它不证明什么：桩不是 Unity / KSP 的真实签名，真机行为仍以游戏内实测为准（docs/06 §构建契约）。

用法：
    python tests/runtime-behavior/test-runtime-behavior.py
    python tests/runtime-behavior/test-runtime-behavior.py --keep   # 保留临时工程
退出码：0 = 全过；1 = 有失败；2 = 跳过（本机没有 dotnet）。
"""

import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
PROJECT = os.path.join(HERE, "RuntimeBehavior.csproj")
RUNTIME_PROJECT = os.path.join(REPO, "src", "UI18n.Runtime", "UI18n.Runtime.csproj")
RUNTIME_DIR = os.path.join(REPO, "src", "UI18n.Runtime")
STUBS = os.path.join(REPO, "tests", "runtime-compile", "stubs")
CORE_PROJECT = os.path.join(REPO, "src", "UI18n.Core", "UI18n.Core.csproj")
SCENARIOS = os.path.join(HERE, "Scenarios.cs")
PROGRAM = os.path.join(HERE, "Program.cs")

MIN_SCENARIOS = 9
MIN_CHECKS = 60

SKIP = 2

SUMMARY_RE = re.compile(r"behavior:\s*(\d+)\s*scenarios?,\s*(\d+)\s*checks?,\s*(\d+)\s*failures?")


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
            print("  FAIL : " + what + ((" - " + detail) if detail else ""))
        return ok

    def done(self):
        print("[runtime-behavior] checks={0} failures={1}".format(self.checks, self.failures))
        return 1 if self.failures else 0


def run(cmd, cwd=None, timeout=600):
    proc = subprocess.run(cmd, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=timeout)
    text = proc.stdout.decode("utf-8", "replace")
    return proc.returncode, text


def tail(text, lines=40):
    parts = text.strip().splitlines()
    return "\n".join(parts[-lines:])


def build(project, extra=None):
    cmd = ["dotnet", "build", project, "-c", "Release", "-v", "q", "-nologo"]
    if extra:
        cmd.extend(extra)
    return run(cmd)


def behavior_dll(project_dir):
    return os.path.join(project_dir, "bin", "Release", "net10.0", "UI18n.Runtime.Behavior.dll")


def negative_control(checker, keep):
    """把一份场景源码改坏，必须编得过但跑红 —— 证明断言真的会失败。"""
    temp = tempfile.mkdtemp(prefix="ui18n-behavior-neg-")
    try:
        with open(SCENARIOS, "r", encoding="utf-8-sig") as handle:
            source = handle.read()
        broken = source.replace('Check.Eq("命中", "你好"', 'Check.Eq("命中", "故意写错"', 1)
        if broken == source:
            checker.expect("反向对照：找得到要改坏的断言", False, 'Scenarios.cs 里没有 Check.Eq("命中", "你好"')
            return
        with open(os.path.join(temp, "Scenarios.cs"), "w", encoding="utf-8") as handle:
            handle.write(broken)
        shutil.copyfile(PROGRAM, os.path.join(temp, "Program.cs"))
        with open(os.path.join(temp, "Negative.csproj"), "w", encoding="utf-8") as handle:
            handle.write(
                '<Project Sdk="Microsoft.NET.Sdk">\n'
                "  <PropertyGroup>\n"
                "    <TargetFramework>net10.0</TargetFramework>\n"
                "    <AssemblyName>UI18n.Runtime.Behavior</AssemblyName>\n"
                "    <OutputType>Exe</OutputType>\n"
                "    <ImplicitUsings>disable</ImplicitUsings>\n"
                "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>\n"
                "    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>\n"
                "    <Nullable>disable</Nullable>\n"
                "    <LangVersion>latest</LangVersion>\n"
                "  </PropertyGroup>\n"
                "  <ItemGroup>\n"
                '    <Compile Include="{0}/**/*.cs" />\n'
                '    <Compile Include="{1}/**/*.cs" Exclude="{1}/obj/**;{1}/bin/**" />\n'
                '    <Compile Include="{2}/*.cs" />\n'
                "  </ItemGroup>\n"
                '  <ItemGroup>\n    <ProjectReference Include="{3}" />\n  </ItemGroup>\n'
                "</Project>\n".format(
                    STUBS.replace("\\", "/"),
                    RUNTIME_DIR.replace("\\", "/"),
                    temp.replace("\\", "/"),
                    CORE_PROJECT.replace("\\", "/"),
                )
            )

        project = os.path.join(temp, "Negative.csproj")
        code, text = build(project)
        if not checker.expect("反向对照：改坏的断言编得过", code == 0, tail(text)):
            return
        code, text = run(["dotnet", behavior_dll(temp)])
        checker.expect("反向对照：改坏的断言必须跑红（exit != 0）", code != 0, "exit={0}".format(code))
        checker.expect("反向对照：失败信息点得出是哪一句", "FAIL:" in text, tail(text))
    finally:
        if not keep:
            shutil.rmtree(temp, ignore_errors=True)


def main(argv=None):
    parser = argparse.ArgumentParser(description="Runtime 行为门禁自检")
    parser.add_argument("--keep", action="store_true", help="保留反向对照的临时工程")
    parser.add_argument("--quiet", action="store_true")
    args = parser.parse_args(argv)

    checker = Checker(args.quiet)

    if shutil.which("dotnet") is None:
        print("[runtime-behavior] SKIP: 本机没有 dotnet")
        return SKIP

    checker.expect("工程文件存在", os.path.isfile(PROJECT), PROJECT)

    code, text = build(PROJECT)
    checker.expect("dotnet build 0 错误", code == 0, tail(text))
    if code != 0:
        return checker.done()

    dll = behavior_dll(HERE)
    checker.expect("产物存在", os.path.isfile(dll), dll)
    if not os.path.isfile(dll):
        return checker.done()

    code, text = run(["dotnet", dll])
    checker.expect("9 个场景全过（exit 0）", code == 0, tail(text))

    match = SUMMARY_RE.search(text)
    if checker.expect("汇总行可解析", match is not None, tail(text, 5)):
        scenarios = int(match.group(1))
        checks = int(match.group(2))
        failures = int(match.group(3))
        checker.expect("场景数 >= {0}".format(MIN_SCENARIOS), scenarios >= MIN_SCENARIOS, "got {0}".format(scenarios))
        checker.expect("失败数 = 0", failures == 0, "got {0}".format(failures))
        checker.expect("断言数 >= {0}".format(MIN_CHECKS), checks >= MIN_CHECKS, "got {0}".format(checks))

    negative_control(checker, args.keep)

    with open(RUNTIME_PROJECT, "r", encoding="utf-8-sig") as handle:
        publish_text = handle.read()
    checker.expect(
        "发布工程不含本目录源码",
        "runtime-behavior" not in publish_text.replace("\\", "/"),
        "src/UI18n.Runtime/UI18n.Runtime.csproj 里出现了 runtime-behavior",
    )

    backdoor = []
    for root, dirs, files in os.walk(RUNTIME_DIR):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        for name in files:
            if not name.endswith(".cs"):
                continue
            path = os.path.join(root, name)
            with open(path, "r", encoding="utf-8-sig", errors="replace") as handle:
                if "InternalsVisibleTo" in handle.read():
                    backdoor.append(os.path.relpath(path, REPO))
    checker.expect("产品侧没有为测试开后门（InternalsVisibleTo）", not backdoor, ", ".join(backdoor))

    return checker.done()


if __name__ == "__main__":
    sys.exit(main())
