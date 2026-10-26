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
    & dotnet build $project -c $Configuration --nologo
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

# ---- 5. 退出码原样返回（0=全过；1=有失败/有错，已用故意失败的临时用例验证过） ----
if ($code -eq 0) {
    Write-Host '[run-tests] PASS (exit 0)'
} else {
    Write-Host "[run-tests] FAIL (exit $code)" -ForegroundColor Red
}
exit $code
