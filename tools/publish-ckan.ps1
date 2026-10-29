<#
.SYNOPSIS
  把 build-release.ps1 的产物整理成可以直接提给 CKAN-meta 的分支内容。

.DESCRIPTION
  M3-b（docs/11）：build-release.ps1 只负责产出 zip + manifest + 渲染好的 .netkan；
  本脚本负责"发版之后那一步"——核对产物自洽（版本号、sha256、download 指向、依赖清单、
  zip 里的版本文件与插件/语言包），再把 .netkan 摆成 CKAN-meta 仓库的目录结构，并生成 PR 正文。

  这一步刻意是离线可跑的：不联网、不调 gh release upload、不创建 GitHub Release
  （仓库当前没有任何 Release，2026-10-26 按维护者判断删除）。它只做两件事：
  ① 把能在本地判定的错误全判掉；② 把人工提交时要贴的东西准备好。

.PARAMETER Version
  要提交的版本号，必须与 zip 文件名、netkan 里的 version、zip 内 UI18n.version 一致。

.PARAMETER DistDir
  build-release.ps1 的输出目录，默认 <repo>/dist。

.PARAMETER OutDir
  提交物输出目录，默认 <DistDir>/ckan。

.PARAMETER SkipPython
  跳过 tools/i18n_tools/netkan.py 的深度校验（本机没装 python 时用；会打印警告）。

.EXAMPLE
  powershell -File tools/publish-ckan.ps1 -Version 1.7.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$DistDir = "",
    [string]$OutDir = "",
    [switch]$SkipPython
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
if ([string]::IsNullOrWhiteSpace($DistDir)) { $DistDir = Join-Path $repoRoot "dist" }
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $DistDir "ckan" }
$NL = [Environment]::NewLine

$problems = New-Object System.Collections.Generic.List[string]
function Add-Problem([string]$Message) { $script:problems.Add($Message) }

# ---------- 1) 产物是否齐 ----------
$zipPath = Join-Path $DistDir ("UI18n-{0}.zip" -f $Version)
$netkanPath = Join-Path $DistDir "UI18n.netkan"
$manifestPath = Join-Path $DistDir "manifest.json"
foreach ($item in @(
    @{ Path = $zipPath; What = "zip 包" },
    @{ Path = $netkanPath; What = ".netkan" },
    @{ Path = $manifestPath; What = "manifest.json" })) {
    if (-not (Test-Path -LiteralPath $item.Path)) {
        Add-Problem ("缺少{0}：{1}（先跑 src/UI18n.Runtime/build-release.ps1）" -f $item.What, $item.Path)
    }
}
if ($problems.Count -gt 0) {
    foreach ($p in $problems) { Write-Host ("[ckan] 不合格: " + $p) }
    throw ("[ckan] 产物不齐，共 {0} 处问题" -f $problems.Count)
}

# ---------- 2) zip 与 manifest 自洽 ----------
$zipItem = Get-Item -LiteralPath $zipPath
$zipSha = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$manifestSha = [string]$manifest.zip.sha256
if ($manifestSha -and ($manifestSha.ToLowerInvariant() -ne $zipSha)) {
    Add-Problem ("manifest.json 的 zip.sha256 与磁盘不符：{0} != {1}" -f $manifestSha, $zipSha)
}
if ($manifest.zip.name -and ([string]$manifest.zip.name -ne $zipItem.Name)) {
    Add-Problem ("manifest.json 的 zip.name 与磁盘不符：{0} != {1}" -f $manifest.zip.name, $zipItem.Name)
}

# ---------- 3) .netkan 自洽（不依赖 python 的部分） ----------
$netkanText = Get-Content -LiteralPath $netkanPath -Raw -Encoding UTF8
$netkan = $null
try { $netkan = $netkanText | ConvertFrom-Json } catch { Add-Problem (".netkan 不是合法 JSON：" + $_.Exception.Message) }
if ($null -ne $netkan) {
    if (-not $netkan.spec_version) { Add-Problem ".netkan 缺 spec_version（CKAN 索引会拒）" }
    if ([string]$netkan.identifier -ne "UI18n") { Add-Problem (".netkan 的 identifier 应为 UI18n，实际 " + $netkan.identifier) }
    if ([string]$netkan.version -ne $Version) {
        Add-Problem (".netkan 的 version 与 -Version 不符：{0} != {1}" -f $netkan.version, $Version)
    }
    # spec_version v1.34 的写法是 download 为字符串、sha256 在顶层；旧写法是 download 为对象。
    # 两种都认，否则模板换写法时这段校验会静默失效（sha 变空串、检查被跳过）。
    $sha = ([string]$netkan.sha256).ToLowerInvariant()
    if (-not $sha -and $netkan.download.sha256) { $sha = ([string]$netkan.download.sha256).ToLowerInvariant() }
    if (-not $sha) {
        Add-Problem ".netkan 里没找到 sha256"
    } elseif ($sha -ne $zipSha) {
        Add-Problem (".netkan 的 sha256 与 zip 不符：{0} != {1}" -f $sha, $zipSha)
    }
    $url = [string]$netkan.download
    if (-not $url -and $netkan.download.url) { $url = [string]$netkan.download.url }
    if (-not ($url -like ("*/v{0}/UI18n-{0}.zip" -f $Version))) {
        Add-Problem (".netkan 的 download.url 不像本版本资产：" + $url)
    }
    if (-not $netkan.install) {
        Add-Problem ".netkan 缺 install 段"
    } else {
        $installText = ($netkan.install | ConvertTo-Json -Depth 6 -Compress)
        if (-not ($installText -like "*GameData*GameData*")) {
            Add-Problem ("install 段不是 GameData -> GameData：" + $installText)
        }
    }
    $depNames = @()
    if ($netkan.depends) { $depNames = @($netkan.depends | ForEach-Object { [string]$_.name }) }
    if ($depNames -notcontains "Harmony2") { Add-Problem ".netkan 的 depends 里没有 Harmony2" }
    $extra = @($depNames | Where-Object { $_ -ne "Harmony2" })
    if ($extra.Count -gt 0) {
        Add-Problem ("depends 里不该出现别的依赖：" + ($extra -join ", ") + "（UI18n 运行期只靠 Harmony，不写 cfg patch）")
    }
    if ([string]$netkan.ksp_version -ne "1.12") {
        Add-Problem (".netkan 的 ksp_version 应为 1.12，实际 " + $netkan.ksp_version)
    }
    if ($netkanText -match "@[A-Z0-9_]+@") { Add-Problem (".netkan 里还有没替换的占位符：" + $matches[0]) }
}

# ---------- 4) zip 内容与版本文件 ----------
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipArchive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entries = @($zipArchive.Entries | ForEach-Object { $_.FullName })
    $versionEntry = $entries | Where-Object { $_ -like "*UI18n.version" } | Select-Object -First 1
    if (-not $versionEntry) {
        Add-Problem "zip 里没有 UI18n.version"
    } else {
        $entry = $zipArchive.GetEntry($versionEntry)
        $reader = New-Object System.IO.StreamReader($entry.Open())
        try { $versionText = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $versionJson = $null
        try { $versionJson = $versionText | ConvertFrom-Json } catch {
            Add-Problem ("zip 内 UI18n.version 不是合法 JSON：" + $_.Exception.Message)
        }
        if ($null -ne $versionJson) {
            $inner = "{0}.{1}.{2}" -f $versionJson.VERSION.MAJOR, $versionJson.VERSION.MINOR, $versionJson.VERSION.PATCH
            if ($inner -ne $Version) { Add-Problem ("zip 内 UI18n.version 的 VERSION 是 {0}，与 -Version {1} 不符" -f $inner, $Version) }
        }
    }
    if (-not ($entries | Where-Object { $_ -like "*Plugins/UI18n.Runtime.dll" })) {
        Add-Problem "zip 里没有 Plugins/UI18n.Runtime.dll"
    }
    if (-not ($entries | Where-Object { $_ -like "*Packs/*/*.jsonl" })) {
        Add-Problem "zip 里没有任何语言包（Packs/<lang>/*.jsonl）"
    }
} finally {
    $zipArchive.Dispose()
}

# ---------- 5) python 深度校验（默认开） ----------
if (-not $SkipPython) {
    $python = $null
    foreach ($candidate in @("python", "python3", "py")) {
        $cmd = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($null -ne $cmd) { $python = $cmd.Source; break }
    }
    if ($null -eq $python) {
        Write-Warning "[ckan] 没找到 python，跳过 netkan.py 深度校验（装好 python 后重跑，别加 -SkipPython）"
    } else {
        $toolsDir = Join-Path $repoRoot "tools"
        Push-Location $toolsDir
        try {
            $env:PYTHONPATH = "."
            & $python -m i18n_tools.netkan $netkanPath --version $Version --sha256 $zipSha --zip $zipPath 2>&1 | ForEach-Object { Write-Host $_ }
            if ($LASTEXITCODE -ne 0) { Add-Problem ("netkan.py 校验失败（exit {0}）" -f $LASTEXITCODE) }
        } finally {
            Pop-Location
        }
    }
}

# ---------- 6) 汇总问题 ----------
if ($problems.Count -gt 0) {
    Write-Host ""
    foreach ($p in $problems) { Write-Host ("[ckan] 不合格: " + $p) }
    throw ("[ckan] 共 {0} 处问题，未生成提交物" -f $problems.Count)
}

# ---------- 7) 摆成 CKAN-meta 目录结构 + PR 正文 ----------
$targetDir = Join-Path (Join-Path $OutDir "NetKAN") "UI18n"
if (-not (Test-Path -LiteralPath $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }
$targetNetkan = Join-Path $targetDir ("UI18n-{0}.netkan" -f $Version)
Copy-Item -LiteralPath $netkanPath -Destination $targetNetkan -Force

$repoSlug = "DiaoDaiaChan/KSP_UniversalI18n"
$branch = "add-ui18n-{0}" -f $Version
$body = @()
$body += ("新增 UI18n {0}" -f $Version)
$body += ""
$body += "本 PR 由 tools/publish-ckan.ps1 生成，内容只有 NetKAN/UI18n/UI18n-$Version.netkan 一个文件。"
$body += ""
$body += "## 自检结果"
$body += ""
$body += ("- zip: UI18n-{0}.zip ({1:N0} B)" -f $Version, $zipItem.Length)
$body += ("- sha256: {0}（与 .netkan 的 download.sha256 一致）" -f $zipSha)
$body += "- depends: Harmony2（只有这一个：UI18n 不改别人的 cfg、不写 MM patch，运行期只靠 Harmony）"
$body += ("- install: GameData -> GameData；zip 内已核对 UI18n.version = {0}" -f $Version)
$body += "- 校验器: python -m i18n_tools.netkan（tools/i18n_tools/netkan.py）通过"
$body += ""
$body += "## 合并前请确认"
$body += ""
$body += ("- [ ] https://github.com/{0}/releases/download/v{1}/UI18n-{1}.zip 可以下载（本脚本不创建 Release）" -f $repoSlug, $Version)
$body += "- [ ] 下载后的 zip 用 sha256sum 复核，与上面的值一致"
$body += "- [ ] abstract / license 与仓库 README 的说法一致"
$body += ""
$body += "## 复现"
$body += ""
$body += ("1. git checkout -b {0}" -f $branch)
$body += ("2. python -m i18n_tools.netkan NetKAN/UI18n/UI18n-{0}.netkan --version {0}" -f $Version)
$prBodyPath = Join-Path $OutDir "PR-BODY.md"
Set-Content -LiteralPath $prBodyPath -Value ($body -join $NL) -Encoding UTF8

Write-Host ""
Write-Host ("[ckan] version   : {0}" -f $Version)
Write-Host ("[ckan] zip sha256: {0}" -f $zipSha)
Write-Host ("[ckan] netkan    : {0}" -f $targetNetkan)
Write-Host ("[ckan] pr body   : {0}" -f $prBodyPath)
Write-Host ("[ckan] 分支名建议: {0}" -f $branch)
Write-Host "[ckan] done"
