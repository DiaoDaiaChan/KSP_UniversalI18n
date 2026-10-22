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
    3) 只有显式加 -Install 才会写 KSP 的 GameData（已存在的 Config.cfg 默认保留，避免覆盖玩家设置）
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
    [string]$Lang = 'zh-cn',
    [string]$Configuration = 'Release',
    [string]$KspRoot = 'D:\Kerbal Space Program_newmod'
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
& dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed (exit $LASTEXITCODE)"
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

foreach ($cfg in @('Config.cfg', 'UI18n.cfg')) {
    $src = Join-Path $templates $cfg
    if (-not (Test-Path $src)) { throw "missing template: $src" }
    Copy-Item $src $stage -Force
    Write-Host "[deploy] staged  : $cfg"
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

# 5) 可选安装进 KSP（默认不碰游戏目录；只写 <KspRoot>\GameData\UI18n，红线 7）
if (-not $Install) {
    Write-Host "[deploy] done (staging only). 加 -Install 才会写 $KspRoot\GameData\UI18n"
    exit 0
}

$target = Join-Path $KspRoot 'GameData\UI18n'
New-Item -ItemType Directory -Path $target -Force | Out-Null

$keepConfig = Test-Path (Join-Path $target 'Config.cfg')
Get-ChildItem -Path $stage -Force | ForEach-Object {
    if ($keepConfig -and $_.Name -eq 'Config.cfg') {
        Write-Host "[deploy] kept    : Config.cfg (existing player settings)"
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
Write-Host "[deploy] tree:"
Get-ChildItem -Path $target -Recurse -File | ForEach-Object {
    Write-Host ("[deploy]   {0}  ({1:N0} B)" -f $_.FullName.Substring($target.Length + 1), $_.Length)
}
