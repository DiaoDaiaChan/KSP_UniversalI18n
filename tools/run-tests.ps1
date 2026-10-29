<#
.SYNOPSIS
    UI18n.Core 单元测试执行入口（本机唯一可靠路径）。

.DESCRIPTION
    为什么不用 dotnet test（本机实测）：
      VSTest 的 testhost 在启动阶段就崩：
        Unhandled exception. System.ComponentModel.Win32Exception (5): 拒绝访问。
          at System.Diagnostics.ProcessManager.OpenProcess(Int32, Int32, Boolean)
          at System.Diagnostics.Process.EnsureWatchingForExit()
          at Microsoft.VisualStudio.TestPlatform.TestHost.DefaultEngineInvoker.SetParentProcessExitCallback(...)
      即 testhost 打不开父进程（vstest.console）的句柄，一条用例都执行不到；dotnet vstest <dll> 同样崩。
      这不是管理员权限问题（非 admin 下单独给父进程挂 EnableRaisingEvents 是成功的），也无法在测试工程里规避。
      因此测试工程已迁到 xunit.v3：测试程序集自身是可执行文件、自带 in-proc runner，
      用 dotnet <dll> 直接跑，完全绕过 VSTest testhost；退出码由 runner 给出（0=全过，1=有失败/有错）。
      本工程另外加了 MSBuild 守卫：谁敲 dotnet test 会立刻收到 error UI18N001 并退出 1，绝不静默 0 条通过。

.PARAMETER Configuration
    Debug 或 Release，默认 Release。

.PARAMETER NoBuild
    跳过 dotnet build（假定已经构建过）。

.PARAMETER RunnerArgs
    透传给 xunit.v3 runner 的额外参数，例如 '-class','UI18n.Core.Tests.DictPackTests'
    （xunit.v3 支持的过滤器：-class / -class- / -method / -namespace / -trait / -filter "query" 等，
     完整清单用 dotnet <dll> -? 查看）。
    注意：pwsh -File 不会做 PowerShell 的数组解析，逗号与引号会原样传进来，脚本内部会按逗号拆分并剥引号。

.EXAMPLE
    pwsh tools\run-tests.ps1

.EXAMPLE
    pwsh tools\run-tests.ps1 -Configuration Debug -NoBuild

.EXAMPLE
    pwsh tools\run-tests.ps1 -RunnerArgs '-class','UI18n.Core.Tests.Fnv1aTests'
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$NoBuild,

    [string[]]$RunnerArgs = @()
)

$ErrorActionPreference = 'Stop'

# ---- 0. 容错：pwsh -File 会把 '-class','X' 当成一个含引号与逗号的字面量字符串传进来
#         （pwsh -File 不走 PowerShell 的参数解析），这里按逗号拆分并剥掉成对引号，
#         使 `pwsh -File tools\run-tests.ps1 -RunnerArgs '-class','X'` 与交互式 `& .\tools\run-tests.ps1 -RunnerArgs '-class','X'` 都能用。
$flatRunnerArgs = @()
foreach ($rawArg in $RunnerArgs) {
    foreach ($piece in ($rawArg -split ',')) {
        $t = $piece.Trim()
        if ($t.Length -ge 2 -and ((($t[0] -eq [char]39) -and ($t[$t.Length - 1] -eq [char]39)) -or (($t[0] -eq [char]34) -and ($t[$t.Length - 1] -eq [char]34)))) {
            $t = $t.Substring(1, $t.Length - 2)
        }
        if ($t.Length -gt 0) { $flatRunnerArgs += $t }
    }
}

# ---- 1. 定位仓库根：从脚本目录向上找 UI18n.sln（脚本放在 <repo>\tools\ 下） ----
$root = $PSScriptRoot
while ($root -and -not (Test-Path -LiteralPath (Join-Path $root 'UI18n.sln'))) {
    $parent = Split-Path -Parent $root
    if (-not $parent -or $parent -eq $root) { $root = $null; break }
    $root = $parent
}
if (-not $root) {
    Write-Host '[run-tests] 找不到仓库根（向上找 UI18n.sln 失败）。本脚本应位于 <repo>\tools\ 下。' -ForegroundColor Red
    exit 2
}

$project = Join-Path $root 'tests\UI18n.Core.Tests\UI18n.Core.Tests.csproj'
$dll = Join-Path $root ('tests\UI18n.Core.Tests\bin\' + $Configuration + '\net10.0\UI18n.Core.Tests.dll')

Write-Host "[run-tests] repo    : $root"
Write-Host "[run-tests] project : $project"

# ---- 2. 构建（dotnet build 不受影响；受影响的只有 VSTest testhost 那条运行路径） ----
if (-not $NoBuild) {
    Write-Host "[run-tests] building ($Configuration) ..."
    & dotnet build $project -c $Configuration --nologo --no-restore
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[run-tests] BUILD FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

if (-not (Test-Path -LiteralPath $dll)) {
    Write-Host "[run-tests] 找不到测试程序集：$dll" -ForegroundColor Red
    Write-Host "[run-tests] 去掉 -NoBuild 再跑一次。" -ForegroundColor Red
    exit 2
}

# ---- 3. 用 xunit.v3 的 in-proc runner 直接执行（不经过 VSTest testhost） ----
if ($flatRunnerArgs.Count -gt 0) {
    Write-Host "[run-tests] extra args: $($flatRunnerArgs -join ' ')"
}
& dotnet $dll @flatRunnerArgs
$code = $LASTEXITCODE

# ---- 4. golden 门禁（docs/08 D-T5）：用户报过的英文一条都不许静默丢 ----
#         退出码语义：0 全过 / 1 有挂（被过滤器丢、没进候选表、字典没译文）/ 2 跳过。
#         2 的来由是 packs/candidates.jsonl 还不存在（候选单一表未落地）：这里只打 skip 原因、不判红，
#         否则在数据侧还没就绪时测试入口永远红着，红惯了就没人看。exit 1 才是真红。
$goldenCode = 0
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) {
    Write-Host '[run-tests] golden : SKIP（找不到 python，跳过 golden 门禁）' -ForegroundColor Yellow
} else {
    Push-Location -LiteralPath (Join-Path $root 'tools')
    try {
        & python -m i18n_tools.golden_gate
        $goldenCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($goldenCode -eq 0) {
        Write-Host '[run-tests] golden : PASS (exit 0)'
    } elseif ($goldenCode -eq 2) {
        Write-Host '[run-tests] golden : SKIP —— packs/candidates.jsonl 不存在（候选单一表未落地），第 2 步未判定；这不是通过' -ForegroundColor Yellow
    } else {
        Write-Host "[run-tests] golden : FAIL (exit $goldenCode) —— 用户报过的串被丢掉/没进候选表/字典没译文；纪律是改过滤器，不改清单" -ForegroundColor Red
        if ($code -eq 0) { $code = $goldenCode }
    }
}

# ---- 4b. journal 门禁（docs/10 M1）：packs 里每一笔改动都必须能在 packs/journal 里找到背书 ----
#         为什么要这道门：M1 之后译文的唯一写入口是 api，人（或 agent）直接手改 packs/*.jsonl 会绕过
#         revision 与八门禁，事后谁都查不出「这行是谁、什么时候、为什么改的」。这里用 sha256 对账：
#         受管文件当前字节必须等于某条 journal 事件的 file_sha256_after，且 journal 只许追加。
#         退出码语义：0 通过 / 1 有违规（手改、或 journal 被删被改） / 2 用法错或 git 不可用（不是通过）。
$journalCode = 0
if (-not $python) {
    Write-Host '[run-tests] journal: SKIP（找不到 python，跳过 journal 门禁）' -ForegroundColor Yellow
} else {
    Push-Location -LiteralPath (Join-Path $root 'tools')
    try {
        & python -m i18n_tools.journal_gate --worktree
        $journalCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($journalCode -eq 0) {
        Write-Host '[run-tests] journal: PASS (exit 0)'
    } elseif ($journalCode -eq 2) {
        Write-Host '[run-tests] journal: FAIL (exit 2) —— 用法错或 git 不可用，这一道门没判定；这不是通过' -ForegroundColor Red
        if ($code -eq 0) { $code = $journalCode }
    } else {
        Write-Host "[run-tests] journal: FAIL (exit $journalCode) —— packs 里的改动没有 journal 事件背书；用 api 重做，别手改" -ForegroundColor Red
        if ($code -eq 0) { $code = $journalCode }
    }
}

# ---- 4c. Runtime 影子语义编译（M5-d）：真工程要真实 KSP 的 Managed 程序集，本机没有也编不了；
#          影子工程（tests/runtime-compile/）用最小桩把 src/UI18n.Runtime 的 8,000 行在 net10.0 上真编一遍，
#          成员名/参数类型这类语义错当场红。它还会做一次反向对照（故意写错一句 Unity API，必须编红），
#          再断言 Runtime 的每一个 .cs 都参与了编译（少一个 = glob 被改窄，等于门禁悄悄失效）。
#          退出码：0 全过 / 1 有失败 / 2 SKIP（本机没有 dotnet）。这里把 2 当**失败**：
#          上面刚用 dotnet 编过单测，dotnet 必然在；这时报 SKIP 说明是环境坏了，不是「无事可做」。
$shadowCode = 0
if (-not $python) {
    Write-Host '[run-tests] shadow : SKIP（找不到 python，跳过影子语义编译）' -ForegroundColor Yellow
} else {
    Push-Location -LiteralPath $root
    try {
        & python (Join-Path $root 'tests\runtime-compile\test-shadow-compile.py')
        $shadowCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($shadowCode -eq 0) {
        Write-Host '[run-tests] shadow : PASS (exit 0) —— Runtime 语义编译 0 错误，反向对照已编红'
    } else {
        Write-Host "[run-tests] shadow : FAIL (exit $shadowCode) —— Runtime 源码或桩有问题；这是真语义编译，不是纯语法校验" -ForegroundColor Red
        if ($code -eq 0) { $code = $shadowCode }
    }
}

# ---- 4d. Runtime 行为场景（R13）：4c 只证明那 29 个 .cs 编得过 —— 它们一行都没被执行过。
#          这一步把同一批源码 + 同样的桩 + 真 Core 编成 Exe 真跑：包加载 → 三层优先级（ctx>site>global）
#          → 骨架填回 → keep 行 → per-mod 策略（NATIVE 包加载期跳过）→ 缺命中捕获 → 开关短路 → 条目上限。
#          场景数据是磁盘上的真 JSONL（h 用产品自己的 Fnv1a 算），KSP 根指向临时目录。
#          退出码：0 全过 / 1 有失败 / 2 SKIP。同 4c，2 在这里也按失败算（dotnet 刚用过，SKIP 说明环境坏了）。
$behaviorCode = 0
if (-not $python) {
    Write-Host '[run-tests] behavior: SKIP（找不到 python，跳过 Runtime 行为场景）' -ForegroundColor Yellow
} else {
    Push-Location -LiteralPath $root
    try {
        & python (Join-Path $root 'tests/runtime-behavior/test-runtime-behavior.py')
        $behaviorCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($behaviorCode -eq 0) {
        Write-Host '[run-tests] behavior: PASS (exit 0) —— 9 个场景 / 75 条断言全过，反向对照已跑红'
    } else {
        Write-Host "[run-tests] behavior: FAIL (exit $behaviorCode) —— Runtime 胶水层行为不对（包加载 / 优先级 / 骨架 / 策略 / 捕获），详见 tests/runtime-behavior/README.md" -ForegroundColor Red
        if ($code -eq 0) { $code = $behaviorCode }
    }
}

# ---- 5. 退出码原样返回（0=全过；1=有失败/有错，已用故意失败的临时用例验证过） ----
if ($code -eq 0) {
    Write-Host '[run-tests] PASS (exit 0)'
} else {
    Write-Host "[run-tests] FAIL (exit $code)" -ForegroundColor Red
}
exit $code
