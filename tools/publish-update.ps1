<#
.SYNOPSIS
    一条命令发布语言包更新通道（build → update 分支 → 线上自检）。

.DESCRIPTION
    把「出包」和「托管」串起来，避免手抄 manifest 或漏推 zip：
      1. 调 src/UI18n.Runtime/build-update-package.ps1 出 <OutDir>/<Channel>/manifest.json + zip；
      2. 在 update 分支的 worktree 里整目录替换同名通道；
      3. 有差异才 commit + push（内容没变就是空跑，不制造空提交）；
      4. 线上自检：GET <RawBase>/<Channel>/manifest.json 与 zip 的 HEAD，比对 version / bytes。

    整目录替换意味着删掉的包也会从线上消失，不会留旧文件。

.EXAMPLE
    pwsh -File tools\publish-update.ps1 -Version 1.2.1 -Channel beta

.EXAMPLE
    pwsh -File tools\publish-update.ps1 -Version 1.2.0 -Channel stable -SkipVerify
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Lang = "zh-cn",
    [ValidateSet("stable", "beta")][string]$Channel = "stable",
    [string]$RawBase = "https://raw.githubusercontent.com/DiaoDaiaChan/KSP_UniversalI18n/update",
    [string]$Worktree = "",
    [switch]$SkipVerify,
    # 允许发布一个「不比线上新」的版本（默认禁止：客户端只在远端更新时才装，发旧号等于让它永远看不到）
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

<#
.SYNOPSIS
    比较两个发布版本号（三段数字，忽略 +build 与 -prerelease 后缀）。

.DESCRIPTION
    与运行时 src/UI18n.Core/Update/ReleaseVersion.cs 的口径保持一致：
    在 + 或 - 处截断、按 . 分段、至少两段最多四段、逐段按数值比、缺段补 0。
    返回 1（左新）/ 0（相等）/ -1（左旧）；任一侧解析不出来返回 $null（调用方自己决定怎么办）。
#>
function Compare-ReleaseVersion {
    param(
        [string]$Left,
        [string]$Right
    )

    $lv = ConvertTo-ReleaseVersionParts -Text $Left
    $rv = ConvertTo-ReleaseVersionParts -Text $Right
    if ($null -eq $lv -or $null -eq $rv) { return $null }

    for ($i = 0; $i -lt 3; $i++) {
        if ($lv[$i] -gt $rv[$i]) { return 1 }
        if ($lv[$i] -lt $rv[$i]) { return -1 }
    }
    return 0
}

<#
.SYNOPSIS
    把版本串拆成三段整数；解析失败返回 $null。
#>
function ConvertTo-ReleaseVersionParts {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }

    $s = $Text.Trim()
    $cut = $s.Length
    $plus = $s.IndexOf("+")
    if ($plus -ge 0 -and $plus -lt $cut) { $cut = $plus }
    $minus = $s.IndexOf("-")
    if ($minus -ge 0 -and $minus -lt $cut) { $cut = $minus }
    $s = $s.Substring(0, $cut).Trim()
    if ($s.Length -eq 0) { return $null }

    $parts = $s.Split(".")
    if ($parts.Length -lt 2 -or $parts.Length -gt 4) { return $null }

    $numbers = @(0, 0, 0)
    for ($i = 0; $i -lt $parts.Length; $i++) {
        if ($parts[$i] -notmatch "^[0-9]+$") { return $null }
        if ($i -lt 3) { $numbers[$i] = [int]$parts[$i] }
    }
    return $numbers
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Worktree) { $Worktree = Join-Path (Split-Path -Parent $repoRoot) "ui18n-update-branch" }
$builder = Join-Path $repoRoot "src\UI18n.Runtime\build-update-package.ps1"
$outRoot = Join-Path $repoRoot "dist\update"
$channelDir = Join-Path $outRoot $Channel

Write-Host "[publish] channel : $Channel"
Write-Host "[publish] version : $Version"
Write-Host "[publish] lang    : $Lang"

# ---- 1. 出包（五条硬断言在脚本里，失败直接 throw）----
& $builder -Version $Version -Lang $Lang -Channel $Channel -OutDir $outRoot
if (-not $?) { throw "[publish] build-update-package.ps1 failed" }
$manifestPath = Join-Path $channelDir "manifest.json"
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "[publish] 没生成 manifest: $manifestPath" }

# ---- 2. update 分支的 worktree ----
Push-Location $repoRoot
try {
    $null = git rev-parse --verify refs/heads/update 2>$null
    if ($LASTEXITCODE -ne 0) { throw "[publish] 本地没有 update 分支，先 git fetch origin update:update" }

    # update 分支可能已经挂在一个 worktree 上（默认路径或历史遗留）：找到它并复用，绝不重复 add
    $wtForUpdate = ""
    $curPath = ""
    foreach ($ln in @(git worktree list --porcelain)) {
        if ($ln.StartsWith("worktree ")) { $curPath = $ln.Substring(9) }
        elseif ($ln -eq "branch refs/heads/update") { $wtForUpdate = $curPath }
    }
    if ($wtForUpdate) {
        $Worktree = $wtForUpdate
    }
    else {
        if (Test-Path -LiteralPath $Worktree) { Remove-Item -LiteralPath $Worktree -Recurse -Force }
        git worktree prune | Out-Null
        git worktree add $Worktree update | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "[publish] git worktree add 失败" }
    }
    Write-Host "[publish] worktree: $Worktree"

    # ---- 2b. 版本单调：update 分支上已有的版本不能被「更旧/相同」覆盖 ----
    # 客户端只在「远端比本地新」时才装（UpdateWorkflow: remote.IsNewerThan(local)），
    # 所以把旧号推上线等于让所有已更新过的人永远停在原地，而线上 zip 与版本号也对不上账。
    $channelTarget = Join-Path $Worktree $Channel
    $previousManifest = Join-Path $channelTarget "manifest.json"
    if (Test-Path -LiteralPath $previousManifest) {
        $previousVersion = [string]((Get-Content -LiteralPath $previousManifest -Raw -Encoding UTF8 | ConvertFrom-Json).version)
        $cmp = Compare-ReleaseVersion -Left $Version -Right $previousVersion
        Write-Host ("[publish] previous: " + $previousVersion + " (channel " + $Channel + ")")
        if ($null -eq $cmp) {
            throw ("[publish] 版本号无法比较：new=" + $Version + ", published=" + $previousVersion)
        }
        if ($cmp -le 0 -and -not $Force) {
            throw ("[publish] 版本没有前进：" + $Version + " <= 已发布 " + $previousVersion +
                "。客户端只在远端更新时才装，发旧号/同号等于让这次更新永远装不上；" +
                "确实要覆盖请加 -Force。")
        }
        if ($cmp -le 0) {
            Write-Host ("[publish] WARN: -Force 覆盖已发布版本 " + $previousVersion + " -> " + $Version)
        }
    }
    else {
        Write-Host ("[publish] previous: (none, channel " + $Channel + " first publish)")
    }

    # ---- 3. 整目录替换 + 有差异才推 ----
    $target = Join-Path $Worktree $Channel
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item (Join-Path $channelDir "*") $target -Force

    Push-Location $Worktree
    try {
        git add -A | Out-Null
        $staged = @(git diff --cached --name-only)
        if ($staged.Count -eq 0) {
            Write-Host "[publish] no content change, nothing to push"
        }
        else {
            Write-Host "[publish] staged:"
            $staged | ForEach-Object { Write-Host ("[publish]   " + $_) }
            git -c user.name="DiaoDaiaChan" -c user.email="diaodaiachan@qq.com" commit -q -m ("update: " + $Channel + " 通道发布 " + $Version + " (" + $Lang + ")")
            if ($LASTEXITCODE -ne 0) { throw "[publish] commit failed" }
            git push origin update
            if ($LASTEXITCODE -ne 0) { throw "[publish] push failed" }
        }
    }
    finally { Pop-Location }
}
finally { Pop-Location }

if ($SkipVerify) { Write-Host "[publish] done (verify skipped)"; return }

# ---- 4. 线上自检 ----
$rawChannel = $RawBase.TrimEnd("/") + "/" + $Channel
$m = Invoke-WebRequest -Uri ($rawChannel + "/manifest.json") -TimeoutSec 60 -UseBasicParsing
if ($m.StatusCode -ne 200) { throw "[publish] manifest HTTP $($m.StatusCode)" }
$remote = $m.Content | ConvertFrom-Json
if ($remote.version -ne $Version) { throw "[publish] remote version $($remote.version) != $Version" }
$zipUrl = $rawChannel + "/" + $remote.zip.name
$head = Invoke-WebRequest -Uri $zipUrl -Method Head -TimeoutSec 60 -UseBasicParsing
$len = [long](@($head.Headers["Content-Length"])[0])
if ($len -ne [long]$remote.zip.bytes) { throw ("[publish] remote zip length " + $len + " != manifest " + $remote.zip.bytes) }
Write-Host ("[publish] online ok: version=" + $remote.version + ", zip=" + $remote.zip.bytes + " B, files=" + $remote.files.Count)
Write-Host ("[publish] manifest: " + $rawChannel + "/manifest.json")
