#!/usr/bin/env python3
"""结构化校验 .github/workflows/i18n.yml（纯标准库，不起 GitHub、不装 PyYAML）。

为什么要有这个测试：本机没有 PyYAML（`pip` 都没有），GitHub Actions 的 YAML 语法错了
只能等推上去以后 CI 报错；而这个文件的 job 组合是**门禁契约**的一部分——「每周刷新」
如果不经过 PR 直接写 main，或者校验步骤被删掉，光看 diff 很容易漏。所以这里用
缩进扫描（本文件的 job 都是 2 空格缩进、步骤都是 6 空格 `- name:`）把契约钉住：

  1. `jobs:` 下的 job 集合就是预期的 5 个，多一个少一个都红；
  2. 每个 job 都有 runs-on 与 timeout-minutes（没有超时的 job 会挂满 6 小时）；
  3. `refresh`（M4-e 每周刷新）必须：定时触发 + 有开关变量 + 申请 pull-requests 权限
     + 只 `git push -f origin <bot 分支>`（绝不直推 main）+ 用 `gh pr create` 开 PR
     + 步骤顺序固定（送翻 → IL → 清单 → 复审 → 开 PR）；
  4. `translate`（手工触发）仍是直写 main 的那条，两者不能混；
  5. 文件里不许出现制表符（YAML 禁止 tab 缩进，肉眼看不见但会让整份工作流失效）。

用法：python tests/workflows/test-i18n-workflow.py [--workflow <path>]
退出码：0 = 全过，1 = 有失败。
"""

import argparse
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
DEFAULT_WORKFLOW = os.path.join(REPO, ".github", "workflows", "i18n.yml")

EXPECTED_JOBS = ["gates", "code", "translate", "refresh", "release-check"]

REFRESH_STEPS = [
    "检查密钥存在",
    "cfg/字典流水线（translate --keyref-rescue → resolve_keyrefs）",
    "IL 流水线（il-translatable → <asm>.il.jsonl）",
    "清单与覆盖报告（packs/index.json + reports/coverage.md）",
    "回写后复审（audit --strict-terms，必须仍然 PASS）",
    "有变更才开 PR（人审合并）",
]


class Checker(object):
    def __init__(self):
        self.checks = 0
        self.failures = 0

    def expect(self, what, ok, detail=""):
        self.checks += 1
        if ok:
            print("  ok   : " + what)
        else:
            self.failures += 1
            print("  FAIL : " + what + (("  <-- " + detail) if detail else ""))


def load_lines(path):
    with open(path, "r", encoding="utf-8-sig", newline="") as f:
        text = f.read()
    return text, text.replace("\r\n", "\n").split("\n")


def job_blocks(lines):
    """返回 {job 名: (起始行号, [该 job 的行])}；只认 `jobs:` 之后、下一个顶格键之前的 2 空格键。"""
    start = None
    for i, line in enumerate(lines):
        if line.rstrip() == "jobs:":
            start = i + 1
            break
    if start is None:
        return {}, None
    end = len(lines)
    for i in range(start, len(lines)):
        line = lines[i]
        if line and not line[0].isspace() and not line.startswith("#"):
            end = i
            break
    names = []
    for i in range(start, end):
        line = lines[i]
        if line.startswith("  ") and not line.startswith("   ") and line.rstrip().endswith(":"):
            name = line.strip()[:-1]
            if name and not name.startswith("#"):
                names.append((name, i))
    blocks = {}
    for idx, (name, at) in enumerate(names):
        stop = names[idx + 1][1] if idx + 1 < len(names) else end
        blocks[name] = (at + 1, lines[at:stop])
    return blocks, (start, end)


def step_names(block):
    out = []
    for line in block:
        stripped = line.strip()
        if stripped.startswith("- name: ") and line.startswith("      "):
            out.append(stripped[len("- name: "):].strip())
    return out


def main(argv=None):
    ap = argparse.ArgumentParser(description="结构化校验 i18n 工作流")
    ap.add_argument("--workflow", default=DEFAULT_WORKFLOW)
    args = ap.parse_args(argv)

    path = args.workflow
    if not os.path.isfile(path):
        print("[workflow] 找不到文件: " + path)
        return 2
    text, lines = load_lines(path)
    c = Checker()
    print("[workflow] " + path)

    # ---- 0. 基本卫生 ----
    c.expect("文件里没有制表符（YAML 禁止 tab 缩进）", "\t" not in text)
    c.expect("有 jobs: 段", any(l.rstrip() == "jobs:" for l in lines))
    blocks, span = job_blocks(lines)
    c.expect("能解析出 job 列表", bool(blocks), "job_blocks 返回空")
    if not blocks:
        print("")
        print("checks=%d failures=%d" % (c.checks, c.failures))
        return 1

    # ---- 1. job 集合 ----
    c.expect("job 集合 == " + ", ".join(EXPECTED_JOBS),
             sorted(blocks.keys()) == sorted(EXPECTED_JOBS),
             "实际: " + ", ".join(sorted(blocks.keys())))

    # ---- 2. 每个 job 都要有 runs-on 与超时 ----
    for name in sorted(blocks.keys()):
        at, block = blocks[name]
        body = "\n".join(block)
        c.expect(name + " 有 runs-on", "    runs-on: " in body)
        c.expect(name + " 有 timeout-minutes（没有超时的 job 会挂满 6 小时）", "    timeout-minutes: " in body)

    # ---- 3. refresh = 每周刷新，只开 PR ----
    at, refresh = blocks.get("refresh", (0, []))
    body = "\n".join(refresh)
    c.expect("refresh 只在 schedule 事件跑", "github.event_name == 'schedule'" in body)
    c.expect("refresh 有开关变量 UI18N_WEEKLY_TRANSLATE（别让定时任务默认烧额度）",
             "vars.UI18N_WEEKLY_TRANSLATE" in body)
    c.expect("refresh 依赖 gates 与 code（门禁先绿再送翻）", "    needs: [gates, code]" in body)
    c.expect("refresh 申请 pull-requests: write（开 PR 必需）", "pull-requests: write" in body)
    c.expect("refresh 用 gh pr create 开 PR", "gh pr create" in body)
    c.expect("refresh 只推 bot 分支（git push -f origin \"$branch\"）", "git push -f origin \"$branch\"" in body)
    c.expect("refresh 绝不直推 main", "git push origin main" not in body and "git push\n" not in body)
    c.expect("refresh 无变更时不开 PR（git diff --cached --quiet）", "git diff --cached --quiet" in body)
    for secret in ("UI18N_LLM_BASE", "UI18N_LLM_KEY", "UI18N_LLM_MODEL"):
        c.expect("refresh 引用 secrets." + secret, "secrets." + secret in body)
    got = step_names(refresh)
    c.expect("refresh 的步骤顺序固定（送翻 → IL → 清单 → 复审 → 开 PR）",
             got == REFRESH_STEPS,
             "实际: " + " | ".join(got))
    c.expect("refresh 在 translate 之后（同一份文件里顺序即阅读顺序）",
             blocks["translate"][0] < blocks["refresh"][0])

    # ---- 4. translate 仍是手工触发、直写 main ----
    at, translate = blocks["translate"]
    tbody = "\n".join(translate)
    c.expect("translate 仍是 workflow_dispatch + inputs.translate 触发",
             "inputs.translate" in tbody)
    c.expect("translate 保留直写 main 的 push（手工触发走这条）", "git push" in tbody)
    c.expect("translate 不用 gh pr create（两条回写路径不能混）", "gh pr create" not in tbody)

    # ---- 5. 门禁 job 里那几道门还在 ----
    at, gates = blocks["gates"]
    gbody = "\n".join(gates)
    for cmd in ("i18n_tools.selftest", "i18n_tools.packindex --check",
                "i18n_tools.mmgen --stats --min-cover 0.85", "i18n_tools.netkan --template",
                "i18n_tools.policy --check", "i18n_tools.fontbundle --check",
                "i18n_tools.fontcov --check",
                "tests/encoding/test-ps1-bom.py",
                "tests/hardcode/test-no-mod-hardcode.py",
                "i18n_tools.golden_gate", "i18n_tools.journal_gate"):
        c.expect("gates 仍包含 " + cmd, cmd in gbody)

    # ---- 6. code job 里那五件事还在（Core 编译 / 单测工程 / 跑 dll / Runtime 影子编译 / 行为场景）----
    # 影子编译这一步最容易被当成「反正真机才编」删掉：删了以后 8,000 行 Runtime 又回到
    # 「只做 csc 纯语法校验」，而 CI 全绿 —— 所以它必须是被钉住的契约的一部分。
    # 同理，行为场景（tests/runtime-behavior）证明的是「那 29 个 .cs 真跑起来会按设计工作」，
    # 删掉它 Runtime 就又变成「能编译、但从没被执行过」。
    at, code = blocks["code"]
    cbody = "\n".join(code)
    for cmd in ("src/UI18n.Core/UI18n.Core.csproj",
                "tests/UI18n.Core.Tests/UI18n.Core.Tests.csproj",
                "UI18n.Core.Tests/bin/Release/net10.0/UI18n.Core.Tests.dll",
                "tests/runtime-compile/test-shadow-compile.py",
                "tests/runtime-behavior/test-runtime-behavior.py"):
        c.expect("code 仍包含 " + cmd, cmd in cbody)
    # 注意：不能写成 "dotnet test" not in cbody —— "run: dotnet tests/UI18n.Core.Tests/…dll"
    # 以 "dotnet test" 开头，会把自己的正确写法判成违规（第一版就栽在这）。
    bad_run = [l.strip() for l in code
               if l.strip().startswith("run:") and re.search(r"dotnet test(?![s\w])", l)]
    c.expect("code 不用 dotnet test 起 testhost（容器里起不来，工程里 error UI18N001 拦着）",
             not bad_run, " | ".join(bad_run))
    c.expect("code job 名里点明 Runtime 影子语义编译（别让它被静默降级）",
             "影子语义编译" in cbody)

    print("")
    print("checks=%d failures=%d" % (c.checks, c.failures))
    return 0 if c.failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
