<#
.SYNOPSIS
    一把收走 UI18n 的现场：KSP.log 里的 [UI18n] 段、配置、语言包清单、捕获文件、更新状态 → 一个 zip。

.DESCRIPTION
    排障时最贵的是「让玩家去翻日志」。这个脚本把回答一个问题所需的全部材料收进单个 zip，
    不联网、不上传、不改动 KSP 目录里的任何文件（只读 + 在别处写 zip）。

    收什么（都能在 summary.txt 里看到成因）：
      * log/          KSP.log 里所有含 [UI18n] 的行 + 首尾上下文行（整份 KSP.log 太大，通常 20–60 MB）
      * config/       Config.cfg（原样）、UI18n.cfg、UI18n.version、settings.cfg 的 LANGUAGE 行、buildID.txt
      * plugins/      各 DLL 的文件名 / 字节 / sha256（不收 DLL 本体：哈希就足以定位版本，且体积大）
      * packs/        每种语言的文件数 / 总字节 / 行数 / 每文件 sha256（不收包本体，同样只留指纹）
      * runtime/      PluginData/UI18n 下的 capture-*.jsonl / shadow-*.jsonl / update-state.json（这些正是证据本体）
      * summary.txt   上述所有指纹 + 宿主环境 + 时间，key=value 段落可机读

    单个附件超过 -MaxFileMB 时只记大小与 sha256，不复制内容（并在 summary.txt 里标 skipped: too large）。

.PARAMETER KspRoot
    KSP 安装目录（含 KSP.log 与 GameData）。默认 D:\Kerbal Space Program_newmod。

.PARAMETER OutDir
    zip 的输出目录。默认 <KspRoot>\_diag（不存在则创建）。

.PARAMETER Tag
    可选标签，加进 zip 文件名，例如 -Tag 玩家A。

.PARAMETER MaxFileMB
    单个附件的复制上限（默认 32 MB）。capture-*.jsonl 单个会话通常几百 KB。

.PARAMETER MaxRuntimeFiles
    最多收几个 capture-*.jsonl / shadow-*.jsonl（默认 5，按修改时间取最新）。老玩家目录里可能有几十个，
    全收只会把 zip 撑到几十 MB，而排障通常只看最近一两局。

.EXAMPLE
    pwsh tools\collect-diagnostics.ps1
    pwsh tools\collect-diagnostics.ps1 -Tag after-update -OutDir C:\temp\ui18n
#>
[CmdletBinding()]
param(
    [string] $KspRoot = 'D:\Kerbal Space Program_newmod',
    [string] $OutDir = '',
    [string] $Tag = '',
    [int] $MaxFileMB = 32,
    [ValidateRange(1, 500)]
    [int] $MaxRuntimeFiles = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Invariant = [System.Globalization.CultureInfo]::InvariantCulture
$MaxFileBytes = [long]$MaxFileMB * 1MB
$Stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss', $Invariant)

function Write-Step([string] $Message) {
    Write-Host ('[diag] ' + $Message)
}

# 共享读打开：游戏正在跑时 KSP.log 与 capture-*.jsonl 都被写入方持有，独占读会失败。
function Read-SharedText([string] $Path) {
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8, $true)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-Sha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Copy-Capped([string] $Path, [string] $DestinationDir) {
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -gt $MaxFileBytes) {
        return ('skipped: too large (' + $item.Length + ' B > ' + $MaxFileBytes + ' B)')
    }

    if (-not (Test-Path -LiteralPath $DestinationDir)) {
        New-Item -ItemType Directory -Path $DestinationDir -Force | Out-Null
    }

    $target = Join-Path $DestinationDir $item.Name
    Copy-Item -LiteralPath $item.FullName -Destination $target -Force
    return ('copied ' + $item.Length + ' B')
}

if (-not (Test-Path -LiteralPath $KspRoot)) {
    throw ('KSP root not found: ' + $KspRoot)
}

$gameData = Join-Path $KspRoot 'GameData'
if (-not (Test-Path -LiteralPath $gameData)) {
    throw ('not a KSP install (no GameData): ' + $KspRoot)
}

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $KspRoot '_diag'
}

if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

$name = 'UI18n-diag-' + $Stamp
if (-not [string]::IsNullOrWhiteSpace($Tag)) {
    $safeTag = ($Tag -replace '[^\w.-]', '_')
    $name = $name + '-' + $safeTag
}

$stage = Join-Path $OutDir $name
if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}

New-Item -ItemType Directory -Path $stage -Force | Out-Null
Write-Step ('stage: ' + $stage)

$summary = New-Object System.Collections.Generic.List[string]
$summary.Add('# UI18n diagnostics')
$summary.Add('collectedAt=' + [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', $Invariant))
$summary.Add('collectedAtLocal=' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss', $Invariant))
$summary.Add('kspRoot=' + $KspRoot)
$summary.Add('host=' + [System.Environment]::OSVersion.VersionString)
$summary.Add('pwsh=' + $PSVersionTable.PSVersion.ToString())
$summary.Add('')

# ---------------------------------------------------------------- 1) 日志
$summary.Add('[log]')
$kspLog = Join-Path $KspRoot 'KSP.log'
if (Test-Path -LiteralPath $kspLog) {
    $logItem = Get-Item -LiteralPath $kspLog
    $text = Read-SharedText $kspLog
    $allLines = $text -split "`r?`n"
    $hitIndexes = New-Object System.Collections.Generic.List[int]
    for ($i = 0; $i -lt $allLines.Length; $i++) {
        if ($allLines[$i].Contains('[UI18n]')) { $hitIndexes.Add($i) }
    }

    $logDir = Join-Path $stage 'log'
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null

    $extracted = New-Object System.Collections.Generic.List[string]
    $extracted.Add('# KSP.log lines containing [UI18n] — ' + $hitIndexes.Count + ' hit(s), source ' + $logItem.Length + ' B')
    foreach ($i in $hitIndexes) {
        $extracted.Add($allLines[$i])
    }

    Set-Content -LiteralPath (Join-Path $logDir 'ksp-log-ui18n.txt') -Value $extracted -Encoding utf8

    # 尾段原样保留：崩溃/异常栈的行不带前缀，只有整段尾部能看出「最后发生了什么」。
    $tailCount = [Math]::Min(400, $allLines.Length)
    $tail = $allLines[($allLines.Length - $tailCount)..($allLines.Length - 1)]
    Set-Content -LiteralPath (Join-Path $logDir 'ksp-log-tail.txt') -Value $tail -Encoding utf8

    $summary.Add('kspLog.bytes=' + $logItem.Length)
    $summary.Add('kspLog.modified=' + $logItem.LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ssZ', $Invariant))
    $summary.Add('kspLog.lines=' + $allLines.Length)
    $summary.Add('kspLog.ui18nLines=' + $hitIndexes.Count)
    Write-Step ('log: ' + $hitIndexes.Count + ' [UI18n] line(s) from ' + $allLines.Length + ' 行 KSP.log')
}
else {
    $summary.Add('kspLog=missing')
    Write-Step 'log: KSP.log not found (game never started from this folder?)'
}

# ---------------------------------------------------------------- 2) 配置与版本
$summary.Add('')
$summary.Add('[config]')
$ui18nRoot = Join-Path $gameData 'UI18n'
$configDir = Join-Path $stage 'config'
New-Item -ItemType Directory -Path $configDir -Force | Out-Null

if (Test-Path -LiteralPath $ui18nRoot) {
    foreach ($fileName in @('Config.cfg', 'UI18n.cfg', 'UI18n.version')) {
        $path = Join-Path $ui18nRoot $fileName
        if (Test-Path -LiteralPath $path) {
            $item = Get-Item -LiteralPath $path
            Copy-Item -LiteralPath $path -Destination (Join-Path $configDir $fileName) -Force
            $summary.Add('config/' + $fileName + '=' + $item.Length + ' B, sha256 ' + (Get-Sha256 $path))
        }
        else {
            $summary.Add('config/' + $fileName + '=missing')
        }
    }
}
else {
    $summary.Add('gameData/UI18n=missing')
    Write-Step 'config: GameData/UI18n not found (mod not installed?)'
}

# settings.cfg 只摘 LANGUAGE：整份文件里有大量与本 Mod 无关的内容。
$settingsPath = Join-Path $KspRoot 'settings.cfg'
if (Test-Path -LiteralPath $settingsPath) {
    $settingsText = Read-SharedText $settingsPath
    $languageMatch = [regex]::Match($settingsText, '(?m)^\s*LANGUAGE\s*=\s*(\S+)')
    if ($languageMatch.Success) {
        $summary.Add('settings.LANGUAGE=' + $languageMatch.Groups[1].Value)
    }
    else {
        $summary.Add('settings.LANGUAGE=missing')
    }
}
else {
    $summary.Add('settings.cfg=missing')
}

$buildIdPath = Join-Path $KspRoot 'buildID.txt'
if (Test-Path -LiteralPath $buildIdPath) {
    Copy-Item -LiteralPath $buildIdPath -Destination (Join-Path $configDir 'buildID.txt') -Force
    $summary.Add('buildID.txt=' + ((Get-Content -LiteralPath $buildIdPath) -join ' | '))
}

# ---------------------------------------------------------------- 3) 插件指纹
$summary.Add('')
$summary.Add('[plugins]')
$pluginsDir = Join-Path $ui18nRoot 'Plugins'
if (Test-Path -LiteralPath $pluginsDir) {
    foreach ($dll in (Get-ChildItem -LiteralPath $pluginsDir -Filter '*.dll' -File | Sort-Object Name)) {
        $hash = Get-Sha256 $dll.FullName
        $written = $dll.LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ssZ', $Invariant)
        $summary.Add('plugins/' + $dll.Name + '=' + $dll.Length + ' B, sha256 ' + $hash + ', written ' + $written)
    }
}
else {
    $summary.Add('plugins=missing')
}

$harmonyDir = Join-Path $gameData '000_Harmony'
if (Test-Path -LiteralPath $harmonyDir) {
    foreach ($dll in (Get-ChildItem -LiteralPath $harmonyDir -Filter '*.dll' -File | Sort-Object Name)) {
        $summary.Add('harmony/' + $dll.Name + '=' + $dll.Length + ' B')
    }
}
else {
    $summary.Add('harmony=missing')
}

# ---------------------------------------------------------------- 4) 语言包指纹
$summary.Add('')
$summary.Add('[packs]')
$packsDir = Join-Path $ui18nRoot 'Packs'
if (Test-Path -LiteralPath $packsDir) {
    $langDirs = @(Get-ChildItem -LiteralPath $packsDir -Directory | Sort-Object Name)
    foreach ($dir in $langDirs) {
        $files = @(Get-ChildItem -LiteralPath $dir.FullName -Filter '*.jsonl' -File | Sort-Object Name)
        $totalBytes = 0L
        $totalRows = 0L
        foreach ($file in $files) {
            $totalBytes += $file.Length
            $totalRows += (Get-Content -LiteralPath $file.FullName | Measure-Object -Line).Lines
            $summary.Add('packs/' + $dir.Name + '/' + $file.Name + '=' + $file.Length + ' B, sha256 ' + (Get-Sha256 $file.FullName))
        }

        $summary.Add('packs/' + $dir.Name + '.files=' + $files.Count)
        $summary.Add('packs/' + $dir.Name + '.bytes=' + $totalBytes)
        $summary.Add('packs/' + $dir.Name + '.rows=' + $totalRows)
    }

    $flatFiles = @(Get-ChildItem -LiteralPath $packsDir -Filter '*.jsonl' -File)
    $summary.Add('packs/flatJsonl=' + $flatFiles.Count + ' (ignored by runtime by design)')
}
else {
    $summary.Add('packs=missing')
}

# ---------------------------------------------------------------- 5) 运行时证据
$summary.Add('')
$summary.Add('[runtime]')
$runtimeDir = Join-Path $KspRoot 'PluginData/UI18n'
$runtimeStage = Join-Path $stage 'runtime'
if (Test-Path -LiteralPath $runtimeDir) {
    $runtimeFiles = @(Get-ChildItem -LiteralPath $runtimeDir -File | Sort-Object Name)

    # capture-*.jsonl 每局一个、单局几百 KB 到 2 MB，老玩家目录里能有几十个；只收最新 N 个，
    # 其余只留指纹。update-state.json 这类小文件不进这个上限（它们才是「上次检查是否成功」的证据）。
    $sessionFiles = @($runtimeFiles | Where-Object { $_.Name -like 'capture-*' -or $_.Name -like 'shadow-*' } |
        Sort-Object LastWriteTimeUtc -Descending)
    $keepSessions = @($sessionFiles | Select-Object -First $MaxRuntimeFiles)
    $dropSessions = @($sessionFiles | Select-Object -Skip $MaxRuntimeFiles)
    $stateFiles = @($runtimeFiles | Where-Object { $_.Name -notlike 'capture-*' -and $_.Name -notlike 'shadow-*' })

    foreach ($file in (@($stateFiles) + @($keepSessions))) {
        $result = Copy-Capped $file.FullName $runtimeStage
        $row = 'runtime/' + $file.Name + '=' + $file.Length + ' B, ' + $result
        if ($result.StartsWith('copied')) {
            $hash = Get-Sha256 $file.FullName
            $row = $row + ', sha256 ' + $hash
        }

        $summary.Add($row)
    }

    foreach ($file in $dropSessions) {
        $note = 'skipped: older than newest ' + $MaxRuntimeFiles + ' session file(s) (use -MaxRuntimeFiles to include more)'
        $summary.Add('runtime/' + $file.Name + '=' + $file.Length + ' B, ' + $note)
    }

    $summary.Add('runtime.sessionFiles=' + $sessionFiles.Count + ', kept=' + $keepSessions.Count)
    if ($runtimeFiles.Count -eq 0) {
        $summary.Add('runtime=empty')
    }
}
else {
    $summary.Add('runtime=missing (没进过游戏，或没开过缺命中捕获)')
}

# ---------------------------------------------------------------- 6) 环境与 mod 规模
$summary.Add('')
$summary.Add('[mods]')
$modCount = @(Get-ChildItem -LiteralPath $gameData -Directory).Count
$dllCount = @(Get-ChildItem -LiteralPath $gameData -Recurse -Filter '*.dll' -File -ErrorAction SilentlyContinue).Count
$summary.Add('gameData.dirs=' + $modCount)
$summary.Add('gameData.dlls=' + $dllCount)
$mmCache = Join-Path $gameData 'ModuleManager.ConfigCache'
if (Test-Path -LiteralPath $mmCache) {
    $mmItem = Get-Item -LiteralPath $mmCache
    $summary.Add('moduleManager.configCache=' + $mmItem.Length + ' B, ' + $mmItem.LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ssZ', $Invariant))
}
else {
    $summary.Add('moduleManager.configCache=missing')
}

Set-Content -LiteralPath (Join-Path $stage 'summary.txt') -Value $summary -Encoding utf8

# ---------------------------------------------------------------- 7) 打包
$zipPath = Join-Path $OutDir ($name + '.zip')
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipItem = Get-Item -LiteralPath $zipPath
Remove-Item -LiteralPath $stage -Recurse -Force

Write-Step ('zip: ' + $zipItem.FullName)
Write-Step ('size: ' + $zipItem.Length + ' B, sha256 ' + (Get-Sha256 $zipPath))
Write-Step ('DONE: ' + $zipItem.FullName)
Write-Output $zipItem.FullName