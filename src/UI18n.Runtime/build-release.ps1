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

    另外两道「来源断言」跑在内容断言之前（见 README §10.8）：
      8. 工作树判据（三档）：已跟踪文件有改动 -> 阻断；未跟踪文件落在 src/ tests/ tools/ packs/
         之下 -> 阻断；其余未跟踪文件（reports/ build/ dist/ _scratch/ …）只告警不阻断。
         -AllowDirty 把前两档降级成 WARN。
      9. staging 里两个 DLL 内嵌的 <version>+<commit>：+ 前必须等于 -Version，
         + 后必须等于 git rev-parse HEAD（构建时用 -p:Version=<version> 写进程序集）。

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
    [string]$RepoSlug = 'DiaoDaiaChan/KSP_UniversalI18n',
    [string]$License = 'MIT',
    [string]$Author = 'DiaoDaiaChan',
    [switch]$SkipBuild,
    # 默认要求工作树干净（发布物必须来自已提交源码）；确需脏树出包时显式传本开关，会打 WARN
    [switch]$AllowDirty
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
#    -Version 必须传进编译（-p:Version=），否则 DLL 里永远是默认版本号，
#    来源断言第 9 条的「发布版本 == DLL 版本」就永远过不了（见 README §10.8）。
if (-not $SkipBuild) {
    Write-Host ("[release] build     : dotnet build -c {0} -p:Version={1}" -f $Configuration, $Version)
    & dotnet build $sln -c $Configuration -p:Version=$Version
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

# UI18n.cfg：模板里的 Version = x.y.z 是给本机 deploy 用的默认值，发布包必须改成这次的 -Version
# （否则包里会留一个和 zip 名、manifest、DLL 都不一致的版本号）。
#
# 坑（2026-10-23 实际踩到，zip 里才发现）：不要用 -replace 的替换串去拼版本号。
#   - '$1' + $Version 会被当成 '$11'（组 11 不存在）；
#   - 把 ${1} 写进外层模板语言（例如 JS 模板串）会直接变成字面 '1'。
#   上一版就是这样把「Version = 1.0.0」改成了裸的「11.0.0」：键名整段丢失，
#   KSP 的 ConfigNode 解析器会把这一行当畸形行。原有断言只查「有没有多余文件」，
#   查不出「文件内容被改写坏」，所以这次同时加了 Assert-ReleaseTree 第 10 条。
# 所以这里按 Match 的索引做纯字符串拼接，完全不涉及替换串语义。
$modCfgPath = Join-Path $stageMod 'UI18n.cfg'
$modCfg = Get-Content -LiteralPath $modCfgPath -Raw -Encoding UTF8
$modCfgPattern = '(?m)^([ \t]*Version[ \t]*=[ \t]*)(\S+)[ \t]*\r?$'
$modCfgMatches = [regex]::Matches($modCfg, $modCfgPattern)
if ($modCfgMatches.Count -ne 1) {
    throw ('[release] staging 的 UI18n.cfg 里 Version = x.y.z 应恰好 1 行，实际 ' + $modCfgMatches.Count + ' 行（模板被改坏了？）')
}
$modCfgMatch = $modCfgMatches[0]
$modCfg = $modCfg.Substring(0, $modCfgMatch.Index) + $modCfgMatch.Groups[1].Value + $Version + $modCfg.Substring($modCfgMatch.Index + $modCfgMatch.Length)
$modCfg = $modCfg.TrimEnd([char[]]@(13, 10))
Set-Content -LiteralPath $modCfgPath -Value $modCfg -Encoding UTF8
Write-Host ('[release] staged  : UI18n.cfg (Version=' + $Version + ')')

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

# 3a) 来源断言先跑（第 8、9 条）：脏树或来源不符时，内容再干净也不该出包
$provenance = Assert-ReleaseProvenance -StageMod $stageMod -RepoRoot $repoRoot -Version $Version -AllowDirty:$AllowDirty
Write-Host ("[release] provenance: HEAD={0}, dirty={1} entry(ies), untracked-ignored={2}, Core={3}, Runtime={4}" -f $provenance.HeadSha, $provenance.DirtyCount, $provenance.UntrackedIgnored, $provenance.Embedded['UI18n.Core.dll'], $provenance.Embedded['UI18n.Runtime.dll'])

# 3b) 内容断言（第 1-7 条）
$stagedFiles = Assert-ReleaseTree -StageRoot $stageRoot -StageMod $stageMod -Version $Version

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
