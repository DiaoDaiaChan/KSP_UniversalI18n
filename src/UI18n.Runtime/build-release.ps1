<#
.SYNOPSIS
    打发布包：dist\UI18n-<version>.zip + dist\manifest.json + dist\UI18n.netkan

.DESCRIPTION
    zip 内布局 = 直接解压进 KSP 根：
        GameData/UI18n/UI18n.cfg
        GameData/UI18n/Config.cfg
        GameData/UI18n/UI18n.version
        GameData/UI18n/Plugins/UI18n.Core.dll
        GameData/UI18n/Plugins/UI18n.Runtime.dll
        GameData/UI18n/Packs/<lang>/*.jsonl
        GameData/UI18n/Fonts/...

    硬断言（命中即 throw，绝不进包）：UI18n.Probe.dll、*.old-*、_smoke.jsonl、*.pdb、
    build\、_scratch\、_audit\、packs 根下的 pool.jsonl / glossary.jsonl。
    Packs 只从仓库 packs\<lang>\ 取（那是运行时字典），不取 packs\ 根下的生产端输入。

.EXAMPLE
    pwsh -File .\src\UI18n.Runtime\build-release.ps1 -Version 1.0.0

.EXAMPLE
    pwsh -File .\src\UI18n.Runtime\build-release.ps1 -Version 1.0.0 -SkipBuild   # 复用已有 build\bin
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Lang = 'zh-cn',
    [string]$Configuration = 'Release',
    # 输出目录，默认 <repo>\dist
    [string]$OutDir = '',
    # netkan 里的仓库 slug 与许可（默认值是占位，正式提交 CKAN 前请确认）
    [string]$RepoSlug = 'UniversalI18n/UniversalI18n',
    [string]$License = 'MIT',
    [string]$Author = 'UI18n contributors',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$NL = [Environment]::NewLine

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "[release] -Version 必须是 x.y.z（例如 1.0.0），收到: '$Version'"
}

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptDir)
$templates = Join-Path $scriptDir 'GameData\UI18n'
$sln = Join-Path $repoRoot 'UI18n.sln'
$binOut = Join-Path $repoRoot ("build\bin\{0}\net472" -f $Configuration)
$langNorm = $Lang.Trim().Replace('_', '-').ToLowerInvariant()
$packSource = Join-Path (Join-Path $repoRoot 'packs') $langNorm
if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'dist' }
$distOut = $OutDir
$stageRoot = Join-Path $repoRoot ("build\release\UI18n-{0}" -f $Version)
$stageMod = Join-Path $stageRoot 'GameData\UI18n'
$zipPath = Join-Path $distOut ("UI18n-{0}.zip" -f $Version)

Write-Host "[release] version   : $Version"
Write-Host "[release] repo      : $repoRoot"
Write-Host "[release] language  : $langNorm"
Write-Host "[release] stage     : $stageRoot"
Write-Host "[release] output    : $distOut"
if ($License -eq 'MIT') {
    Write-Host "[release] note      : netkan license = MIT (占位值，正式提交 CKAN 前请确认；用 -License 覆盖)" -ForegroundColor Yellow
}

# 1) 构建（默认重新编；-SkipBuild 复用 build\bin）
if (-not $SkipBuild) {
    & dotnet build $sln -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "[release] dotnet build failed (exit $LASTEXITCODE)" }
}

foreach ($dll in @('UI18n.Core.dll', 'UI18n.Runtime.dll')) {
    $p = Join-Path $binOut $dll
    if (-not (Test-Path $p)) { throw "[release] 缺少构建产物: $p（先跑 dotnet build 或去掉 -SkipBuild）" }
}

# 2) staging 出一棵「直接解压进 KSP 根」的树
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $stageMod 'Plugins') -Force | Out-Null

foreach ($dll in @('UI18n.Core.dll', 'UI18n.Runtime.dll')) {
    Copy-Item (Join-Path $binOut $dll) (Join-Path $stageMod 'Plugins') -Force
    Write-Host "[release] staged  : Plugins\$dll"
}

foreach ($cfg in @('UI18n.cfg', 'Config.cfg')) {
    $src = Join-Path $templates $cfg
    if (-not (Test-Path $src)) { throw "[release] 缺少模板: $src" }
    Copy-Item $src $stageMod -Force
    Write-Host "[release] staged  : $cfg"
}

# UI18n.version：把模板里的 VERSION 换成这次的 -Version（KSP-AVC）
$versionTemplate = Join-Path $templates 'UI18n.version'
if (-not (Test-Path $versionTemplate)) { throw "[release] 缺少模板: $versionTemplate" }
$versionParts = $Version.Split('.')
$versionJson = Get-Content -LiteralPath $versionTemplate -Raw -Encoding UTF8
$versionJson = $versionJson -replace '"VERSION":\s*\{[^}]*\}', ('"VERSION": { "MAJOR": ' + $versionParts[0] + ', "MINOR": ' + $versionParts[1] + ', "PATCH": ' + $versionParts[2] + ' }')
Set-Content -LiteralPath (Join-Path $stageMod 'UI18n.version') -Value $versionJson -Encoding UTF8
Write-Host "[release] staged  : UI18n.version (VERSION=$Version)"

# Fonts：模板里若有内容就整棵带过去（只有占位说明也算，保证目录结构稳定）
$fontsTemplate = Join-Path $templates 'Fonts'
if (Test-Path $fontsTemplate) {
    Copy-Item $fontsTemplate (Join-Path $stageMod 'Fonts') -Recurse -Force
    Write-Host "[release] staged  : Fonts\"
}

# Packs：只从 packs\<lang>\ 取（运行时字典）；packs\ 根下的 pool.jsonl / glossary.jsonl 是生产端输入，永不进包
if (-not (Test-Path $packSource)) { throw "[release] 找不到语言包目录: $packSource" }
$packFiles = @(Get-ChildItem $packSource -Filter *.jsonl -File | Sort-Object Name)
if ($packFiles.Count -eq 0) { throw "[release] $packSource 下没有 *.jsonl，不能出发布包" }
$packDest = Join-Path (Join-Path $stageMod 'Packs') $langNorm
New-Item -ItemType Directory -Path $packDest -Force | Out-Null
foreach ($f in $packFiles) {
    Copy-Item $f.FullName $packDest -Force
}
Write-Host ("[release] staged  : Packs\{0}\ ({1} file(s), {2:N0} B)" -f $langNorm, $packFiles.Count, (($packFiles | Measure-Object -Property Length -Sum).Sum))

# 3) 硬断言：命中即 throw（规则在 release-checks.ps1 里，便于单测每一条）
. (Join-Path $scriptDir 'release-checks.ps1')
$stagedFiles = Assert-ReleaseTree -StageRoot $stageRoot -StageMod $stageMod

# 4) 打 zip（不加外层目录：解压即得 GameData/UI18n/...）
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
if (-not (Test-Path $distOut)) { New-Item -ItemType Directory -Path $distOut -Force | Out-Null }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stageRoot, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$zipItem = Get-Item $zipPath
$zipSha = (Get-FileHash $zipPath -Algorithm SHA256).Hash

# 5) manifest.json
$fileEntries = @()
foreach ($f in $stagedFiles) {
    $rel = $f.FullName.Substring($stageRoot.Length + 1).Replace('\', '/')
    $fileEntries += [pscustomobject]@{
        path   = $rel
        bytes  = $f.Length
        sha256 = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
    }
}
$fileEntries = @($fileEntries | Sort-Object path)

$packStaged = @($stagedFiles | Where-Object { $_.FullName -match '\\Packs\\' })
$packRows = 0
foreach ($f in $packStaged) {
    $packRows += @(Get-Content -LiteralPath $f.FullName | Where-Object { $_.Trim().Length -gt 0 }).Count
}

$manifest = [ordered]@{
    version    = $Version
    builtAt    = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    kspVersion = '1.12.5'
    language   = $langNorm
    zip        = [ordered]@{ name = $zipItem.Name; bytes = $zipItem.Length; sha256 = $zipSha }
    files      = $fileEntries
    packFiles  = $packStaged.Count
    packRows   = $packRows
    packBytes  = [int](($packStaged | Measure-Object -Property Length -Sum).Sum)
}
$manifestPath = Join-Path $distOut 'manifest.json'
($manifest | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $manifestPath -Encoding UTF8

# 6) netkan（从模板生成，填版本 / sha256 / download；depends 的 identifier 已按本机 CKAN 元数据核对：Harmony2 / ModuleManager）
$netkanTemplate = Join-Path $scriptDir 'ckan\UI18n.netkan.template'
if (-not (Test-Path $netkanTemplate)) { throw "[release] 缺少模板: $netkanTemplate" }
$downloadUrl = 'https://github.com/{0}/releases/download/v{1}/UI18n-{1}.zip' -f $RepoSlug, $Version
$netkan = Get-Content -LiteralPath $netkanTemplate -Raw -Encoding UTF8
$netkan = $netkan.Replace('@VERSION@', $Version)
$netkan = $netkan.Replace('@KSP_VERSION@', '1.12')
$netkan = $netkan.Replace('@SHA256@', $zipSha)
$netkan = $netkan.Replace('@DOWNLOAD_URL@', $downloadUrl)
$netkan = $netkan.Replace('@LICENSE@', $License)
$netkan = $netkan.Replace('@AUTHOR@', $Author)
$netkan = $netkan.Replace('@HOMEPAGE@', ('https://github.com/' + $RepoSlug))
$netkan = $netkan.Replace('@REPOSITORY@', ('https://github.com/' + $RepoSlug))
$netkanPath = Join-Path $distOut 'UI18n.netkan'
Set-Content -LiteralPath $netkanPath -Value $netkan -Encoding UTF8

# 7) 汇总
Write-Host ''
Write-Host "[release] zip      : $zipPath"
Write-Host ("[release] zip bytes: {0:N0}" -f $zipItem.Length)
Write-Host "[release] zip sha256: $zipSha"
Write-Host ("[release] manifest : {0} (files={1}, packFiles={2}, packRows={3}, packBytes={4:N0})" -f $manifestPath, $fileEntries.Count, $manifest.packFiles, $manifest.packRows, $manifest.packBytes)
Write-Host "[release] netkan   : $netkanPath"
Write-Host "[release] contents :"
foreach ($e in $fileEntries) {
    Write-Host ("[release]   {0}  ({1:N0} B)" -f $e.path, $e.bytes)
}
Write-Host '[release] done'
