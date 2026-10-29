<#
.SYNOPSIS
    验收 tools/publish-update.ps1 的版本单调判定（纯函数，无需联网 / git / 出包）。

.DESCRIPTION
    从 publish-update.ps1 里用 PowerShell 官方 AST 取出 Compare-ReleaseVersion 与
    ConvertTo-ReleaseVersionParts 两个函数的源码，dot-source 到当前作用域再逐例断言。
    这样测的是「真正跑在发布流程里的那份实现」，不是复制品；改了脚本却忘了改函数会被抓到。
    断言口径与 src/UI18n.Core/Update/ReleaseVersion.cs 对齐：忽略 +build / -prerelease、
    2~4 段、逐段数值比、任一侧解析不出来返回 $null。
#>
[CmdletBinding()]
param(
    [string]$ScriptPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $ScriptPath) {
    # 从脚本目录向上找 UI18n.sln，再拼 tools/publish-update.ps1，避免依赖固定的相对层级
    $root = $PSScriptRoot
    while ($root -and -not (Test-Path -LiteralPath (Join-Path $root "UI18n.sln"))) {
        $parent = Split-Path -Parent $root
        if (-not $parent -or $parent -eq $root) { $root = $null; break }
        $root = $parent
    }
    if (-not $root) { throw "[test] 找不到仓库根（向上找 UI18n.sln 失败）" }
    $ScriptPath = Join-Path $root "tools/publish-update.ps1"
}
$ScriptPath = (Resolve-Path -LiteralPath $ScriptPath).Path
Write-Host ("[test] script: " + $ScriptPath)

$text = [System.IO.File]::ReadAllText($ScriptPath, [System.Text.Encoding]::UTF8)
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors -and $parseErrors.Count -gt 0) {
    foreach ($e in $parseErrors) { Write-Host ("[test] parse error line " + $e.Extent.StartLineNumber + ": " + $e.Message) }
    throw "[test] publish-update.ps1 有语法错误"
}

$checks = 0
$failures = 0

function Assert-That {
    param([string]$What, [bool]$Ok)
    $script:checks++
    if ($Ok) { Write-Host ("  ok   : " + $What) }
    else { $script:failures++; Write-Host ("  FAIL : " + $What) }
}

# ---- 1. 参数表里有 -Force（单调检查的逃生门） ----
$paramNames = @()
if ($ast.ParamBlock) { $paramNames = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath }) }
Write-Host ("[test] params: " + ($paramNames -join ", "))
Assert-That "参数表仍有 -Version" ($paramNames -contains "Version")
Assert-That "参数表有 -Force 逃生门" ($paramNames -contains "Force")

# ---- 2. 从脚本里取出两个函数（AST，不复制实现） ----
$wanted = @("Compare-ReleaseVersion", "ConvertTo-ReleaseVersionParts")
$chunks = @()
foreach ($name in $wanted) {
    $found = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true))
    Assert-That ("脚本里定义了 " + $name) ($found.Count -eq 1)
    if ($found.Count -ge 1) { $chunks += $found[0].Extent.Text }
}

$fnBlock = [scriptblock]::Create(($chunks -join "`r`n`r`n"))
. $fnBlock

# ---- 3. 比较口径 ----
$cases = @(
    @("1.2.1", "1.2.0", 1,  "补丁号前进 -> 1"),
    @("1.2.0", "1.2.1", -1, "补丁号后退 -> -1"),
    @("1.2.0", "1.2.0", 0,  "同号 -> 0"),
    @("1.10.0", "1.9.0", 1, "按数值比而不是按字典序"),
    @("2.0", "2.0.0", 0,    "两段等价于三段（缺段补 0）"),
    @("1.2.3.4", "1.2.3", 0, "第四段忽略"),
    @("1.2.3+29ffa5c", "1.2.3", 0, "忽略 +build 后缀"),
    @("1.2.3-beta.1", "1.2.3", 0, "忽略 -prerelease 后缀"),
    @("1.3.0-beta", "1.2.9", 1,  "先比数字再看后缀"),
    @(" 1.2.1 ", "1.2.0", 1, "允许前后空白")
)

foreach ($case in $cases) {
    $left = [string]$case[0]
    $right = [string]$case[1]
    $expect = [int]$case[2]
    $why = [string]$case[3]
    $got = Compare-ReleaseVersion -Left $left -Right $right
    Assert-That ("compare " + $left + " vs " + $right + " = " + $expect + "（" + $why + "）") ($null -ne $got -and [int]$got -eq $expect)
}

# ---- 4. 解析不出来返回 $null（调用方据此拒绝发布） ----
$bad = @(
    @("abc", "1.0.0", "非数字"),
    @("", "1.0.0", "空串"),
    @("1", "1.0", "只有一段"),
    @("1.2.3.4.5", "1.0", "五段"),
    @("1.2.x", "1.0", "含非数字段"),
    @("v1.2.3", "1.0", "带前缀字母")
)

foreach ($case in $bad) {
    $left = [string]$case[0]
    $right = [string]$case[1]
    $why = [string]$case[2]
    $got = Compare-ReleaseVersion -Left $left -Right $right
    Assert-That ("compare " + $left + " vs " + $right + " -> null（" + $why + "）") ($null -eq $got)
}

Write-Host ""
Write-Host ("checks=" + $checks + " failures=" + $failures)
if ($failures -gt 0) { exit 1 }
exit 0