<#
.SYNOPSIS
    构建 UI18n.Runtime 并 staging 到仓库内的 build\ 目录（**默认不动 KSP 的 GameData**）。

.DESCRIPTION
    1) dotnet build src\UI18n.Runtime（net472）
    2) 把 UI18n.Runtime.dll + UI18n.Core.dll + Config.cfg + UI18n.cfg + Packs\<lang>\*.jsonl 复制到
       build\GameData\UI18n\ —— 这是一个可直接拷进 KSP 的完整目录。
       字典布局严格按 D35 / 06 §6.4：Packs\<lang>\<modId>.jsonl（每语言一个子目录）。
       **packs\ 根下的平铺文件一律不装**：那里放的是流水线源产物（pool.jsonl / glossary.jsonl /
       inventory.json），装进运行时只会让每一行都 ParseError。运行时只认 Packs\<lang>\ 里的东西。
    3) 只有显式加 -Install 才会写 KSP 的 GameData。已存在的 Config.cfg 默认保留（避免覆盖玩家设置），
       但会把模板里有、玩家配置里缺的键**按默认值补到 UI18N 节点末尾**（新增键不会因为保留旧配置而丢失）；
       -ForceConfig 则直接用模板整体覆盖（丢弃玩家改过的值）
    4) -Smoke 额外装入仓库自带的冒烟字典 Packs\<lang>\_smoke.jsonl（联调/演示用，正式发布不带）

.EXAMPLE
    pwsh -File .\deploy.ps1                      # 只 stage 到 build\

.EXAMPLE
    pwsh -File .\deploy.ps1 -Install -Smoke      # 装进 KSP，并带上冒烟字典

.EXAMPLE
    pwsh -File .\deploy.ps1 -Install -Lang en-us # 换语言目录
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Smoke,
    [switch]$ForceConfig,
    # 取证插件 UI18n.Probe.dll 默认禁止随包发布；只有显式传这个开关才放行（用于「本轮需要 probe 在位 + 包要同步」）。
    [switch]$AllowProbe,
    [string]$Lang = 'zh-cn',
    [string]$Configuration = 'Release',
    [string]$KspRoot = 'D:\Kerbal Space Program_newmod',
    # 发布版本戳。给了就按 -p:Version 构建，使部署的 DLL 与 build-release.ps1 出的包逐字节一致；
    # 不给则用 Directory.Build.props 的默认版本（联调用）。缺了它，deploy 会静默把 build\bin 里
    # 带发布版本戳的产物覆盖成默认版本，于是「验的 DLL」与「发的 DLL」不是同一份字节。
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptDir)
$project = Join-Path $repoRoot 'src\UI18n.Runtime\UI18n.Runtime.csproj'
$binOut = Join-Path $repoRoot ("build\bin\{0}\net472" -f $Configuration)
$stage = Join-Path $repoRoot 'build\GameData\UI18n'
$templates = Join-Path $scriptDir 'GameData\UI18n'
$langNorm = $Lang.Trim().Replace('_', '-').ToLowerInvariant()

Write-Host "[deploy] repo    : $repoRoot"
Write-Host "[deploy] project : $project"
Write-Host "[deploy] language: $langNorm"

# 0) 取证插件守卫：UI18n.Probe.dll 只用于 IMGUI 取证（_scratch\p1\probe.ps1），绝不随包发布。
#    默认 throw（防误发）；显式 -AllowProbe 才放行，用于「本轮需要 probe 在位 + 包要同步」的合法场景。
$probeDirs = @((Join-Path $stage 'Plugins'))
if ($Install) { $probeDirs += (Join-Path $KspRoot 'GameData\UI18n\Plugins') }
foreach ($probeDir in $probeDirs) {
    if (-not (Test-Path $probeDir)) { continue }
    $probeHit = Join-Path $probeDir 'UI18n.Probe.dll'
    if (-not (Test-Path $probeHit)) { continue }
    if ($AllowProbe) {
        Write-Host "[deploy] WARN: probe dll present, deploy continues because -AllowProbe was passed ($probeHit)" -ForegroundColor Yellow
        continue
    }
    throw "[deploy] 发现取证插件 $probeHit —— 它只用于 IMGUI 取证，不能随包发布。先跑：pwsh -File _scratch\p1\probe.ps1 -Action Remove（若确需本轮同步包，加 -AllowProbe）"
}

# 递归复制一棵目录树（目标目录已存在时按文件覆盖，不嵌套）
function Copy-Tree {
    param([string]$Source, [string]$Dest)

    if (-not (Test-Path $Dest)) {
        New-Item -ItemType Directory -Path $Dest -Force | Out-Null
    }

    Get-ChildItem -Path $Source -Force | ForEach-Object {
        $target = Join-Path $Dest $_.Name
        if ($_.PSIsContainer) {
            Copy-Tree -Source $_.FullName -Dest $target
        }
        else {
            Copy-Item $_.FullName -Destination $target -Force
            Write-Host "[deploy] copied  : $($_.Name) -> $Dest"
        }
    }
}

# 1) 构建
$buildArgs = @($project, '-c', $Configuration)
if ($Version) {
    if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw "[deploy] -Version 必须是 X.Y.Z，实际 '$Version'" }
    $buildArgs += ('-p:Version={0}' -f $Version)
    Write-Host "[deploy] version : $Version (DLL 内嵌的发布版本戳)"
}
& dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed (exit $LASTEXITCODE)"
}
if ($Version) {
    $stamped = (Get-Item (Join-Path $binOut 'UI18n.Runtime.dll')).VersionInfo.ProductVersion
    Write-Host "[deploy] built    : ProductVersion=$stamped"
    if ($stamped -and -not $stamped.StartsWith($Version)) {
        throw "[deploy] 构建产物版本戳不符：期望 $Version，实际 $stamped"
    }
}

# 2) staging（build\ 在 .gitignore 里，可以随时删）
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$plugins = Join-Path $stage 'Plugins'
New-Item -ItemType Directory -Path $plugins -Force | Out-Null

foreach ($dll in @('UI18n.Runtime.dll', 'UI18n.Core.dll')) {
    $src = Join-Path $binOut $dll
    if (-not (Test-Path $src)) { throw "missing build output: $src" }
    Copy-Item $src $plugins -Force
    Write-Host "[deploy] staged  : Plugins\$dll"
}

foreach ($cfg in @('Config.cfg', 'UI18n.cfg', 'UI18n.version')) {
    $src = Join-Path $templates $cfg
    if (-not (Test-Path $src)) { throw "missing template: $src" }
    Copy-Item $src $stage -Force
    Write-Host "[deploy] staged  : $cfg"
}

# 模板里的 Version = 1.0.0 与 "VERSION": 1.0.0 是占位值。指定了 -Version 就必须当场盖章，
# 否则装出来的 UI18n.cfg / UI18n.version 会和 DLL 内嵌版本、zip 名、更新清单三处不一致
# （2026-10-26 实测踩到：-Version 1.3.0 装完，两个文件的 VERSION 还是 1.0.0，而 build-release.ps1
#  出包时是改写过的，于是「装机字节 == 发布资产字节」这条自检在文本文件上永远对不上）。
if ($Version) {
    $stampCfgPath = Join-Path $stage 'UI18n.cfg'
    $stampCfgRaw = Get-Content -LiteralPath $stampCfgPath -Raw -Encoding UTF8
    $stampCfgPattern = '(?m)^([ \t]*Version[ \t]*=[ \t]*)(\S+)[ \t]*\r?$'
    $stampCfgMatches = [regex]::Matches($stampCfgRaw, $stampCfgPattern)
    if ($stampCfgMatches.Count -ne 1) {
        throw ('[deploy] staging 的 UI18n.cfg 里 Version = x.y.z 应恰好 1 行，实际 ' + $stampCfgMatches.Count + ' 行（模板被改坏了？）')
    }
    $stampCfgMatch = $stampCfgMatches[0]
    $stampCfgRaw = $stampCfgRaw.Substring(0, $stampCfgMatch.Index) + $stampCfgMatch.Groups[1].Value + $Version + $stampCfgRaw.Substring($stampCfgMatch.Index + $stampCfgMatch.Length)
    Set-Content -LiteralPath $stampCfgPath -Value $stampCfgRaw.TrimEnd([char[]]@(13, 10)) -Encoding UTF8
    Write-Host ('[deploy] stamped : UI18n.cfg (Version=' + $Version + ')')

    $stampVerPath = Join-Path $stage 'UI18n.version'
    $stampParts = $Version.Split('.')
    $stampVerRaw = Get-Content -LiteralPath $stampVerPath -Raw -Encoding UTF8
    $stampVerRaw = $stampVerRaw -replace '"VERSION":\s*\{[^}]*\}', ('"VERSION": { "MAJOR": ' + $stampParts[0] + ', "MINOR": ' + $stampParts[1] + ', "PATCH": ' + $stampParts[2] + ' }')
    Set-Content -LiteralPath $stampVerPath -Value $stampVerRaw -Encoding UTF8
    Write-Host ('[deploy] stamped : UI18n.version (VERSION=' + $Version + ')')
}

# Fonts：FontMode=Bundled 时玩家放 AssetBundle 的位置；模板里只有一个 README.txt，
# 但目录必须存在，否则玩家不知道往哪放（见 GameData\UI18n\Fonts\README.txt）
$fontsTemplate = Join-Path $templates 'Fonts'
if (Test-Path $fontsTemplate) {
    Copy-Item $fontsTemplate (Join-Path $stage 'Fonts') -Recurse -Force
    Write-Host "[deploy] staged  : Fonts\"
}

# 3) 语言包：只装 packs\<lang>\ 这一层（D35 布局；packs\ 根下的源产物不装）
$packSource = Join-Path (Join-Path $repoRoot 'packs') $langNorm
$packDest = Join-Path (Join-Path $stage 'Packs') $langNorm

if (Test-Path $packSource) {
    $jsonl = Get-ChildItem -Path $packSource -Filter '*.jsonl' -File -ErrorAction SilentlyContinue
    if ($jsonl.Count -gt 0) {
        New-Item -ItemType Directory -Path $packDest -Force | Out-Null
        $jsonl | ForEach-Object {
            Copy-Item $_.FullName $packDest -Force
            Write-Host ("[deploy] staged  : Packs\{0}\{1} ({2:N0} B)" -f $langNorm, $_.Name, $_.Length)
        }
    }
    else {
        Write-Host "[deploy] no *.jsonl under packs\$langNorm\, dictionary will be empty until a pack ships"
    }
}
else {
    Write-Host "[deploy] packs\$langNorm\ not found (translation pipeline has not produced a pack yet)"
    Write-Host "[deploy]   note: packs\*.jsonl at the root are pipeline artifacts and are never installed"
}

# 4) 冒烟字典（联调用，只随 -Smoke 装）
if ($Smoke) {
    $smokeSrc = Join-Path (Join-Path $scriptDir 'smoke') '_smoke.jsonl'
    if (-not (Test-Path $smokeSrc)) { throw "missing smoke pack: $smokeSrc" }
    New-Item -ItemType Directory -Path $packDest -Force | Out-Null
    Copy-Item $smokeSrc (Join-Path $packDest '_smoke.jsonl') -Force
    $lines = (Get-Content $smokeSrc | Where-Object { $_.Trim().Length -gt 0 }).Count
    Write-Host ("[deploy] staged  : Packs\{0}\_smoke.jsonl ({1} entries, smoke only)" -f $langNorm, $lines)
}

Write-Host "[deploy] staged  : $stage"

# 把模板里存在、目标配置里缺失的键补到 UI18N 节点末尾（保留玩家已改的值与全部注释）。
# 返回补上的键名数组；目标文件不存在或已包含全部键时返回空数组。
function Merge-ConfigKeys {
    param([string]$Existing, [string]$Template)

    $templateKeys = New-Object System.Collections.Generic.List[string]
    $templateValues = @{}
    foreach ($line in Get-Content -LiteralPath $Template -Encoding UTF8) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$') {
            $key = $Matches[1]
            if (-not $templateValues.ContainsKey($key)) {
                $templateKeys.Add($key)
                $templateValues[$key] = $line.Trim()
            }
        }
    }

    $text = Get-Content -LiteralPath $Existing -Raw -Encoding UTF8
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($key in $templateKeys) {
        if ($text -notmatch ('(?m)^\s*' + [regex]::Escape($key) + '\s*=')) {
            $missing.Add($key)
        }
    }

    if ($missing.Count -eq 0) {
        return @()
    }

    $lastBrace = $text.LastIndexOf('}')
    if ($lastBrace -lt 0) {
        throw "existing Config.cfg has no closing brace, refusing to edit: $Existing"
    }

    $block = New-Object System.Text.StringBuilder
    [void]$block.AppendLine()
    [void]$block.AppendLine("`t// [deploy] 以下键在模板里存在但本文件缺失，已按模板默认值补上（可直接改）。")
    foreach ($key in $missing) {
        [void]$block.AppendLine("`t" + $templateValues[$key])
    }

    $merged = $text.Substring(0, $lastBrace) + $block.ToString() + $text.Substring($lastBrace)
    Set-Content -LiteralPath $Existing -Value $merged -Encoding UTF8 -NoNewline
    return $missing.ToArray()
}

# 5) 可选安装进 KSP（默认不碰游戏目录；只写 <KspRoot>\GameData\UI18n，红线 7）
if (-not $Install) {
    Write-Host "[deploy] done (staging only). 加 -Install 才会写 $KspRoot\GameData\UI18n"
    exit 0
}

$target = Join-Path $KspRoot 'GameData\UI18n'
New-Item -ItemType Directory -Path $target -Force | Out-Null

$installedConfig = Join-Path $target 'Config.cfg'
$keepConfig = (Test-Path $installedConfig) -and (-not $ForceConfig)

if ($keepConfig) {
    $added = Merge-ConfigKeys -Existing $installedConfig -Template (Join-Path $templates 'Config.cfg')
    if ($added.Count -gt 0) {
        Write-Host ("[deploy] merged  : Config.cfg (kept player values, added {0} new key(s): {1})" -f $added.Count, ($added -join ', '))
    }
    else {
        Write-Host "[deploy] kept    : Config.cfg (existing player settings, no new keys)"
    }
}

Get-ChildItem -Path $stage -Force | ForEach-Object {
    if ($keepConfig -and $_.Name -eq 'Config.cfg') {
        return
    }

    $dest = Join-Path $target $_.Name
    if ($_.PSIsContainer) {
        Copy-Tree -Source $_.FullName -Dest $dest
    }
    else {
        Copy-Item $_.FullName -Destination $target -Force
        Write-Host "[deploy] installed: $($_.Name)"
    }
}

Write-Host "[deploy] installed into $target"

# 6) 装完自检：逐文件比对 staging 与部署副本的 sha256（规则在 install-check.ps1 里，便于单测）
. (Join-Path $scriptDir 'install-check.ps1')
$check = Assert-InstallTree -Stage $stage -Target $target
Write-Host ("[deploy] verified: {0} file(s) sha256 match between staging and {1}" -f $check.Checked, $target)

# 部署目录里多出来的文件只告警（例如 .old-* 回滚备份、上一版残留）
if ($check.Extra.Count -gt 0) {
    Write-Host ("[deploy] WARN: {0} extra file(s) in {1} (not part of this build, left untouched):" -f $check.Extra.Count, $target) -ForegroundColor Yellow
    foreach ($e in $check.Extra) { Write-Host ("[deploy]   extra   : " + $e) }
}

Write-Host "[deploy] tree:"
Get-ChildItem -Path $target -Recurse -File | ForEach-Object {
    Write-Host ("[deploy]   {0}  ({1:N0} B)" -f $_.FullName.Substring($target.Length + 1), $_.Length)
}
