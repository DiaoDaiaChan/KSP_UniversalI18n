<#
.SYNOPSIS
    打 packs-only 更新包（游戏内联网更新用）。

.DESCRIPTION
    产物布局（与 UpdateBaseUrl 约定一致：<UpdateBaseUrl>/<UpdateChannel>/manifest.json）：
        <OutDir>/<Channel>/manifest.json
        <OutDir>/<Channel>/UI18n-<version>-<lang>-packs.zip

    zip 内布局（与发布包同一套路径口径，运行时用 ZipSafety.TryGetPackTarget 识别）：
        GameData/UI18n/Packs/<lang>/<modId>.jsonl

    与 build-release.ps1 的区别：那是「发行包」（DLL + cfg + version + 字体 + 语言包，整体替换安装）；
    这是「更新包」（只有语言包，运行时下载后只覆盖 Packs/<lang>/ 下的文件）。两者绝不能混。

    硬断言（一次收齐再 throw，绝不半写）：
      1. 条目只准是 GameData/UI18n/Packs/<lang>/*.jsonl：出现 Plugins/、PluginData/、 *.dll、*.pdb、
         *.cfg、*.version、别的语言目录、下划线开头的开发期产物（_smoke.jsonl）一律拒绝；
      2. 每条 zip 内容的字节数与 sha256 必须与源文件一致，且与 manifest 里登记的完全一致；
      3. manifest 的 version / zip.name / zip.bytes / zip.sha256 / files 必须与实际产物一致；
      4. 可复现：同一次运行内重建一遍 zip，字节与 sha256 必须完全相同（固定条目顺序 + 固定时间戳）；
      5. mods[]（按 mod 聚合，v1.6 M3）必须与 files[] 严格对齐：并集 == 条目集合（不漏不重）、
         每个包恰好属于一个 modId、modId 必须等于从包文件名重新派生的结果（不一致就报错，绝不静默）。
         理由：运行时 PackInstallFilter 把 mods[] 当权威表（玩家关掉某个 mod 时靠它跳过安装），
         表错一条 = 玩家的开关静默失效，而那在游戏里根本看不出来。

.EXAMPLE
    pwsh -File .\src\UI18n.Runtime\build-update-package.ps1 -Version 1.2.0

.EXAMPLE
    pwsh -File .\src\UI18n.Runtime\build-update-package.ps1 -Version 1.2.0 -Channel beta -OutDir D:\tmp\update
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Lang = "zh-cn",
    [ValidateSet("stable", "beta")][string]$Channel = "stable",
    # 输出根目录，默认 <repo>\dist\update；实际写 <OutDir>\<Channel>\
    [string]$OutDir = "",
    # 语言包源目录，默认 <repo>\packs（只取其下的 <lang>\ 子目录）
    [string]$PacksDir = "",
    # 临时工作目录（每次清空重建），默认 <repo>\build\update-package；并发跑多份时用它错开
    [string]$WorkDir = "",
    # 跳过 reader round-trip（用运行时同一个 C# 读取器解这份清单）。只有在机器没有 .NET SDK 时才该用，
    # 而且这种产物**不该发布**：1.6.0 就是"出包脚本与读取器形状不一致"导致联网更新整整五个版本没生效。
    [switch]$SkipReaderCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$LF = [string][char]10

# 清单形状契约的唯一真源是 C#：UI18n.Core/Update/UpdateManifest.cs 的 ReaderContract 常量。
# 这里从源码里读出来写进清单（helper 与发布包第 12 条断言共用，见 release-checks.ps1）——
# 两边各抄一份字符串迟早会漂，而漂了就是整份清单被拒（1.6.0 事故）。
. (Join-Path $PSScriptRoot "release-checks.ps1")
$ReaderContract = Get-ManifestReaderContract -RepoRoot (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))

if ($Version -notmatch "^([0-9]+)\.([0-9]+)\.([0-9]+)$") {
    throw ("[update] -Version 必须匹配 ^(\d+)\.(\d+)\.(\d+)$，收到: " + $Version)
}

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptDir)
if (-not $PacksDir) { $PacksDir = Join-Path $repoRoot "packs" }
if (-not $OutDir) { $OutDir = Join-Path $repoRoot "dist\update" }
# 相对路径一律按仓库根解析（否则从别的 cwd 调用会把产物写进意料之外的目录）
if (-not $WorkDir) { $WorkDir = Join-Path $repoRoot "build\update-package" }
if (-not [System.IO.Path]::IsPathRooted($PacksDir)) { $PacksDir = Join-Path $repoRoot $PacksDir }
if (-not [System.IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $repoRoot $OutDir }
if (-not [System.IO.Path]::IsPathRooted($WorkDir)) { $WorkDir = Join-Path $repoRoot $WorkDir }
$PacksDir = [System.IO.Path]::GetFullPath($PacksDir)
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$WorkDir = [System.IO.Path]::GetFullPath($WorkDir)

$langNorm = $Lang.Trim().Replace("_", "-").ToLowerInvariant()
# 与 UI18n.Core.Update.ZipSafety.IsValidLanguageTag 同口径；那边是唯一安全闸门，这里只为提前给出人话报错
if ($langNorm.Length -lt 2 -or $langNorm.Length -gt 16 -or $langNorm -notmatch "^[A-Za-z][A-Za-z0-9]*(-[A-Za-z0-9]+)*$") {
    throw ("[update] -Lang 不是合法语言目录名（2~16 字符，字母开头，只含字母/数字/连字符）: " + $Lang)
}

$packSource = Join-Path $PacksDir $langNorm
$entryPrefix = "GameData/UI18n/Packs/" + $langNorm + "/"
$zipName = "UI18n-" + $Version + "-" + $langNorm + "-packs.zip"
$outChannelDir = Join-Path $OutDir $Channel
$workDir = $WorkDir

Write-Host ("[update] version   : " + $Version)
Write-Host ("[update] language  : " + $langNorm)
Write-Host ("[update] channel   : " + $Channel)
Write-Host ("[update] packs src : " + $packSource)
Write-Host ("[update] output    : " + $outChannelDir)

if (-not (Test-Path -LiteralPath $packSource)) {
    throw ("[update] 找不到语言包目录: " + $packSource)
}

# 条目顺序必须固定（Ordinal 排序），否则 zip 字节每次都不一样，可复现性就没了
$names = New-Object System.Collections.Generic.List[string]
foreach ($f in @(Get-ChildItem -LiteralPath $packSource -Filter *.jsonl -File)) { $names.Add($f.Name) }
$names.Sort([System.StringComparer]::Ordinal)

# 与 UI18n.Core.Update.ZipSafety.IsValidPackFileName 逐条同口径（后缀忽略大小写、总长 7~128、不以 . 开头、
# 不含 ..、主干只含字母数字 _ - .）。不一致的话脚本会放过一个运行时必然装不进去的包，或者反过来。
function Test-PackFileName {
    param([string]$Name)
    $packExt = ".jsonl"
    if ([string]::IsNullOrEmpty($Name)) { return $false }
    if ($Name.Length -le $packExt.Length -or $Name.Length -gt 128) { return $false }
    if (-not $Name.EndsWith($packExt, [System.StringComparison]::OrdinalIgnoreCase)) { return $false }
    if ($Name.StartsWith(".") -or $Name.IndexOf("..") -ge 0) { return $false }
    $stem = $Name.Substring(0, $Name.Length - $packExt.Length)
    return ($stem -match "^[A-Za-z0-9._-]+$")
}

$problems = New-Object System.Collections.Generic.List[string]
$included = New-Object System.Collections.Generic.List[string]
foreach ($name in $names) {
    if ($name.StartsWith("_")) {
        $problems.Add("reserved-name: " + $name + "（下划线开头是开发期产物，例如 _smoke.jsonl，绝不进更新包）")
        continue
    }
    if (-not (Test-PackFileName -Name $name)) {
        $problems.Add("bad-file-name: " + $name + "（不满足 .jsonl / 7~128 字符 / 主干只含字母数字 _ - . / 不含 ..）")
        continue
    }
    $included.Add($name)
}
if ($included.Count -eq 0) {
    $problems.Add("empty-packs: " + $packSource + " 下没有可发布的 *.jsonl")
}

# 与 UI18n.Core.Dict.PackFilter.ModIdFromFileName 逐字同口径：
#   1) 去掉末尾 .jsonl（忽略大小写）
#   2) 若剩下部分以 .il 或 .cfg 结尾，只去掉这一个后缀（这两个 channel 是已知全集）
#   3) 其余原样就是 modId —— modId 允许含点号（AtmosphereAutopilot.UI），
#      所以绝不能"剥掉最后一个点号段"。
function Get-ModIdFromFileName {
    param([string]$Name)
    if ([string]::IsNullOrEmpty($Name)) { return "" }
    $text = $Name
    if ($text.EndsWith(".jsonl", [System.StringComparison]::OrdinalIgnoreCase)) {
        $text = $text.Substring(0, $text.Length - ".jsonl".Length)
    }
    foreach ($suffix in @(".il", ".cfg")) {
        if ($text.Length -gt $suffix.Length -and $text.EndsWith($suffix, [System.StringComparison]::OrdinalIgnoreCase)) {
            $text = $text.Substring(0, $text.Length - $suffix.Length)
            break
        }
    }
    return $text
}

function Get-BytesSha256 {
    param([byte[]]$Data)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($Data))).Replace("-", "") }
    finally { $sha.Dispose() }
}

function New-PacksZip {
    param([string]$Path, [string[]]$EntryNames, [string[]]$Sources)
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    # 固定时间戳（zip 的 DOS 时间最早 1980-01-01）：不固定则每次建出来的字节都不同
    $fixed = [System.DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            for ($i = 0; $i -lt $EntryNames.Count; $i++) {
                $entry = $archive.CreateEntry($EntryNames[$i], [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $fixed
                $src = [System.IO.File]::OpenRead($Sources[$i])
                try {
                    $dst = $entry.Open()
                    try { $src.CopyTo($dst) } finally { $dst.Dispose() }
                }
                finally { $src.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

function New-ManifestJson {
    param(
        [string]$Ver, [string]$ChannelArg, [string]$Language,
        [string]$ZipNameArg, [long]$ZipBytesArg, [string]$ZipShaArg,
        [object[]]$Files, [object[]]$Mods, [int]$FileCount, [long]$Rows, [long]$BytesSum
    )
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append("{" + $LF)
    # 形状契约标记：读取器只接受它认识的值（见 UI18n.Core/Update/UpdateManifest.cs 的 ReaderContract）。
    # 出包脚本自己断言不了这一条 —— 断言在 tools/manifest-roundtrip（用真正的 C# 读取器解这份清单）。
    [void]$sb.Append("  " + [char]34 + "readerContract" + [char]34 + ": " + [char]34 + $ReaderContract + [char]34 + "," + $LF)
    [void]$sb.Append("  " + [char]34 + "version" + [char]34 + ": " + [char]34 + $Ver + [char]34 + "," + $LF)
    [void]$sb.Append("  " + [char]34 + "channel" + [char]34 + ": " + [char]34 + $ChannelArg + [char]34 + "," + $LF)
    [void]$sb.Append("  " + [char]34 + "language" + [char]34 + ": " + [char]34 + $Language + [char]34 + "," + $LF)
    [void]$sb.Append("  " + [char]34 + "kspVersion" + [char]34 + ": " + [char]34 + "1.12.5" + [char]34 + "," + $LF)
    [void]$sb.Append("  " + [char]34 + "zip" + [char]34 + ": { " + [char]34 + "name" + [char]34 + ": " + [char]34 + $ZipNameArg + [char]34 + ", " + [char]34 + "bytes" + [char]34 + ": " + $ZipBytesArg + ", " + [char]34 + "sha256" + [char]34 + ": " + [char]34 + $ZipShaArg + [char]34 + " }," + $LF)
    [void]$sb.Append("  " + [char]34 + "files" + [char]34 + ": [" + $LF)
    for ($i = 0; $i -lt $Files.Count; $i++) {
        $f = $Files[$i]
        $comma = ""
        if ($i -lt $Files.Count - 1) { $comma = "," }
        $row = "    { " + [char]34 + "path" + [char]34 + ": " + [char]34 + $f.path + [char]34 + ", " + [char]34 + "bytes" + [char]34 + ": " + $f.bytes + ", " + [char]34 + "sha256" + [char]34 + ": " + [char]34 + $f.sha256 + [char]34 + ", " + [char]34 + "rows" + [char]34 + ": " + $f.rows + " }" + $comma
        [void]$sb.Append($row + $LF)
    }
    [void]$sb.Append("  ]," + $LF)
    # mods[]：运行时 PackInstallFilter 的权威表（按 mod 聚合）。顺序固定 = modId Ordinal 升序、files 亦为 Ordinal 升序，
    # 否则同输入两次打包的 manifest 字节会不同，可复现断言就废了。
    [void]$sb.Append("  " + [char]34 + "mods" + [char]34 + ": [" + $LF)
    for ($i = 0; $i -lt $Mods.Count; $i++) {
        $m = $Mods[$i]
        $commaM = ""
        if ($i -lt $Mods.Count - 1) { $commaM = "," }
        $fileList = ""
        for ($k = 0; $k -lt $m.files.Count; $k++) {
            if ($k -gt 0) { $fileList += ", " }
            $fileList += ([char]34 + $m.files[$k] + [char]34)
        }
        $rowM = "    { " + [char]34 + "modId" + [char]34 + ": " + [char]34 + $m.modId + [char]34 + ", " + [char]34 + "files" + [char]34 + ": [" + $fileList + "], " + [char]34 + "rows" + [char]34 + ": " + $m.rows + ", " + [char]34 + "bytes" + [char]34 + ": " + $m.bytes + ", " + [char]34 + "updatedAt" + [char]34 + ": " + [char]34 + $m.updatedAt + [char]34 + " }" + $commaM
        [void]$sb.Append($rowM + $LF)
    }
    [void]$sb.Append("  ]," + $LF)
    [void]$sb.Append("  " + [char]34 + "packFiles" + [char]34 + ": " + $FileCount + "," + $LF)
    [void]$sb.Append("  " + [char]34 + "packRows" + [char]34 + ": " + $Rows + "," + $LF)
    [void]$sb.Append("  " + [char]34 + "packBytes" + [char]34 + ": " + $BytesSum + $LF)
    [void]$sb.Append("}" + $LF)
    return $sb.ToString()
}

Add-Type -AssemblyName System.IO.Compression | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

if (Test-Path -LiteralPath $workDir) { Remove-Item -LiteralPath $workDir -Recurse -Force }
New-Item -ItemType Directory -Path $workDir -Force | Out-Null

$entryNames = @()
$sources = @()
foreach ($name in $included) {
    $entryNames += ($entryPrefix + $name)
    $sources += (Join-Path $packSource $name)
}

$zipPath = Join-Path $workDir $zipName
New-PacksZip -Path $zipPath -EntryNames $entryNames -Sources $sources

# ===== 断言 1-3：zip 条目内容 / 只准有 packs / 与密钥源一致 =====
$zipItem = Get-Item -LiteralPath $zipPath
$zipSha = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$fileRecords = @()
$rowsTotal = [long]0
$bytesTotal = [long]0

$read = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    foreach ($entry in $read.Entries) {
        $full = $entry.FullName
        if ($full -match "(?i)(^|/)(Plugins|PluginData)/" -or $full -match "(?i)\.(dll|pdb|cfg|version)$") {
            $problems.Add("forbidden-entry: " + $full + "（更新包只准有 " + $entryPrefix + "**）")
            continue
        }
        if (-not $full.StartsWith($entryPrefix, [System.StringComparison]::Ordinal)) {
            $problems.Add("entry-outside-packs: " + $full + "（更新包只准有 " + $entryPrefix + "**）")
            continue
        }

        $name = $full.Substring($entryPrefix.Length)
        $srcPath = Join-Path $packSource $name
        if (-not (Test-Path -LiteralPath $srcPath)) {
            $problems.Add("entry-without-source: " + $full)
            continue
        }

        $buffer = New-Object System.IO.MemoryStream
        try {
            $es = $entry.Open()
            try { $es.CopyTo($buffer) } finally { $es.Dispose() }
            $data = $buffer.ToArray()
        }
        finally { $buffer.Dispose() }

        $srcBytes = [System.IO.File]::ReadAllBytes($srcPath)
        $srcSha = Get-BytesSha256 -Data $srcBytes
        $dataSha = Get-BytesSha256 -Data $data
        if ($data.Length -ne $srcBytes.Length) {
            $problems.Add("entry-size-mismatch: " + $full + "（zip " + $data.Length + " B != 源 " + $srcBytes.Length + " B）")
        }
        if ($dataSha -ne $srcSha) {
            $problems.Add("entry-sha256-mismatch: " + $full + "（zip " + $dataSha + " != 源 " + $srcSha + "）")
        }

        $lineCount = @(Get-Content -LiteralPath $srcPath | Where-Object { $_.Trim().Length -gt 0 }).Count
        # rows / updatedAt 只为 mods[] 聚合与报告用；运行时只认 path / bytes / sha256
        $fileRecords += [ordered]@{
            path = $full
            name = $name
            bytes = [long]$data.Length
            sha256 = $dataSha
            rows = [long]$lineCount
            updatedAt = [System.IO.File]::GetLastWriteTimeUtc($srcPath).ToString("yyyy-MM-ddTHH:mm:ssZ", [System.Globalization.CultureInfo]::InvariantCulture)
        }
        $bytesTotal += [long]$data.Length
        $rowsTotal += [long]$lineCount
    }
}
finally { $read.Dispose() }

if ($fileRecords.Count -ne $included.Count) {
    $problems.Add("entry-count: zip 有 " + $fileRecords.Count + " 条，源目录有 " + $included.Count + " 个包")
}

# ===== 断言 5：按 modId 聚合出 mods[]（v1.6 M3），并与 files[] 严格对齐 =====
$modBuckets = @{}
$modIds = New-Object System.Collections.Generic.List[string]
foreach ($r in $fileRecords) {
    $modId = Get-ModIdFromFileName -Name $r.name
    if ([string]::IsNullOrEmpty($modId)) {
        $problems.Add("mods-empty-mod-id: " + $r.path)
        continue
    }
    if (-not $modBuckets.ContainsKey($modId)) {
        $modBuckets[$modId] = New-Object System.Collections.Generic.List[object]
        $modIds.Add($modId)
    }
    $modBuckets[$modId].Add($r)
}
$modIds.Sort([System.StringComparer]::Ordinal)

$mods = @()
foreach ($modId in $modIds) {
    $modFiles = New-Object System.Collections.Generic.List[string]
    $modRows = [long]0
    $modBytes = [long]0
    $modUpdated = ""
    foreach ($e in $modBuckets[$modId]) {
        $modFiles.Add($e.path)
        $modRows += [long]$e.rows
        $modBytes += [long]$e.bytes
        # updatedAt 取名下最新的那个包（ISO-8601 定长 UTC 串，按 ordinal 比较即时间序）
        if ([string]::CompareOrdinal($e.updatedAt, $modUpdated) -gt 0) { $modUpdated = $e.updatedAt }
    }
    $mods += [ordered]@{
        modId = $modId
        files = $modFiles.ToArray()
        rows = $modRows
        bytes = $modBytes
        updatedAt = $modUpdated
    }
}
if ($fileRecords.Count -gt 0 -and $mods.Count -eq 0) {
    $problems.Add("mods-empty: " + $fileRecords.Count + " 个包聚合出 0 个 modId")
}

# 并集必须等于条目集合（不漏不重）；每个包恰好属于一个 modId；
# modId 必须等于从包文件名重新派生的结果（聚合键算错就报错，绝不静默出包）
$recordPaths = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($r in $fileRecords) { [void]$recordPaths.Add($r.path) }
$fileToMod = New-Object 'System.Collections.Generic.Dictionary[string,string]'
foreach ($m in $mods) {
    if (@($m.files).Count -lt 1) {
        $problems.Add("mods-no-files: " + $m.modId + "（mods[] 里出现没有任何文件的条目）")
    }
    foreach ($path in @($m.files)) {
        $leaf = $path
        $cut = $path.LastIndexOf("/")
        if ($cut -ge 0) { $leaf = $path.Substring($cut + 1) }
        $derived = Get-ModIdFromFileName -Name $leaf
        if ($derived -ne $m.modId) {
            $problems.Add("mods-mod-id-mismatch: " + $path + "（文件名派生 " + $derived + " != 聚合 " + $m.modId + "）")
        }
        if ($fileToMod.ContainsKey($path)) {
            $problems.Add("mods-file-duplicated: " + $path + "（同时属于 " + $fileToMod[$path] + " 与 " + $m.modId + "）")
        }
        else { $fileToMod[$path] = $m.modId }
        if (-not $recordPaths.Contains($path)) {
            $problems.Add("mods-files-union-extra: " + $path + "（mods[] 里有，files[] 里没有）")
        }
    }
}
foreach ($r in $fileRecords) {
    if (-not $fileToMod.ContainsKey($r.path)) {
        $problems.Add("mods-files-union-missing: " + $r.path + "（files[] 里有，mods[] 里没有）")
    }
}

$manifestText = New-ManifestJson -Ver $Version -ChannelArg $Channel -Language $langNorm -ZipNameArg $zipName -ZipBytesArg ([long]$zipItem.Length) -ZipShaArg $zipSha -Files $fileRecords -Mods $mods -FileCount $fileRecords.Count -Rows $rowsTotal -BytesSum $bytesTotal
$manifestPath = Join-Path $workDir "manifest.json"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($manifestPath, $manifestText, $utf8NoBom)

# ===== 断言 4：manifest 内容与产物一致 =====
$parsed = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($parsed.version -ne $Version) { $problems.Add("manifest-version: " + $parsed.version + " != " + $Version) }
if ($parsed.zip.name -ne $zipName) { $problems.Add("manifest-zip-name: " + $parsed.zip.name + " != " + $zipName) }
if ([long]$parsed.zip.bytes -ne [long]$zipItem.Length) { $problems.Add("manifest-zip-bytes: " + $parsed.zip.bytes + " != " + $zipItem.Length) }
if ($parsed.zip.sha256 -ne $zipSha) { $problems.Add("manifest-zip-sha256: " + $parsed.zip.sha256 + " != " + $zipSha) }
if (@($parsed.files).Count -ne $fileRecords.Count) { $problems.Add("manifest-file-count: " + @($parsed.files).Count + " != " + $fileRecords.Count) }
# mods[] 必须真的写进了清单，且逐条的 rows / bytes 等于名下各包之和（读回来核对，不复用内存里的数）
if (-not $parsed.PSObject.Properties.Name.Contains("mods")) {
    $problems.Add("manifest-mods-missing: 清单里没有 mods[]（运行时只能退回文件名兜底，玩家开关会受影响）")
}
else {
    if (@($parsed.mods).Count -ne $mods.Count) {
        $problems.Add("manifest-mods-count: 清单 " + @($parsed.mods).Count + " 条 != 聚合 " + $mods.Count + " 条")
    }
    $pathRows = @{}
    $pathBytes = @{}
    foreach ($pf in @($parsed.files)) {
        $pathRows[[string]$pf.path] = [long]$pf.rows
        $pathBytes[[string]$pf.path] = [long]$pf.bytes
    }
    foreach ($pm in @($parsed.mods)) {
        $sumRows = [long]0
        $sumBytes = [long]0
        foreach ($path in @($pm.files)) {
            if (-not $pathRows.ContainsKey([string]$path)) {
                $problems.Add("manifest-mods-file-unknown: " + $pm.modId + " -> " + $path + "（files[] 里没有这条）")
                continue
            }
            $sumRows += [long]$pathRows[[string]$path]
            $sumBytes += [long]$pathBytes[[string]$path]
        }
        if ($sumRows -ne [long]$pm.rows) {
            $problems.Add("manifest-mods-rows: " + $pm.modId + " 登记 " + $pm.rows + " 行，名下各包合计 " + $sumRows)
        }
        if ($sumBytes -ne [long]$pm.bytes) {
            $problems.Add("manifest-mods-bytes: " + $pm.modId + " 登记 " + $pm.bytes + " B，名下各包合计 " + $sumBytes)
        }
    }
}

# ===== 断言 5：可复现（同输入重建一遍，字节与 sha256 必须相同） =====
$secondZip = Join-Path (Join-Path $workDir "repro") $zipName
New-PacksZip -Path $secondZip -EntryNames $entryNames -Sources $sources
$secondSha = (Get-FileHash -LiteralPath $secondZip -Algorithm SHA256).Hash
if ($secondSha -ne $zipSha) { $problems.Add("not-reproducible: 重建 zip 的 sha256 " + $secondSha + " != " + $zipSha) }
$secondManifest = New-ManifestJson -Ver $Version -ChannelArg $Channel -Language $langNorm -ZipNameArg $zipName -ZipBytesArg ([long](Get-Item -LiteralPath $secondZip).Length) -ZipShaArg $secondSha -Files $fileRecords -Mods $mods -FileCount $fileRecords.Count -Rows $rowsTotal -BytesSum $bytesTotal
if ($secondManifest -ne $manifestText) { $problems.Add("not-reproducible: 重建的 manifest 文本不同") }

# ===== 断言 6：reader round-trip =====
# 前面的断言全是"PowerShell 读回自己写的 JSON"，永远发现不了"读取器不认这个形状"。
# 这里把清单交给运行时同一个 C# 读取器（tools/manifest-roundtrip）真解一遍，并核对 mods[]/files[] 一一对应。
if ($SkipReaderCheck) {
    Write-Host "[update] reader    : SKIPPED (-SkipReaderCheck)；这种产物不要发布"
}
else {
    $probeProj = Join-Path $repoRoot "tools\manifest-roundtrip\ManifestRoundtrip.csproj"
    if (-not (Test-Path -LiteralPath $probeProj)) {
        $problems.Add("reader-roundtrip-missing: 找不到 " + $probeProj)
    }
    else {
        $probeCode = -1
        try {
            $probeOut = @(& dotnet run --project $probeProj -c Release -- $manifestPath 2>&1)
            $probeCode = $LASTEXITCODE
            foreach ($ln in $probeOut) { Write-Host ("[update] reader    : " + $ln) }
        }
        catch {
            $probeCode = -1
            Write-Host ("[update] reader    : 无法运行 dotnet - " + $_.Exception.Message)
        }
        if ($probeCode -ne 0) {
            $problems.Add("reader-roundtrip: 运行时读取器拒绝这份清单（exit " + $probeCode + "）；" +
                          "出包脚本与 UI18n.Core 的清单形状不一致，发出去玩家一定装不上（见 1.6.0 事故）")
        }
    }
}

if ($problems.Count -gt 0) {
    $lines = @("[update] 硬断言失败 " + $problems.Count + " 条，拒绝出包：")
    foreach ($p in $problems) { $lines += ("  - " + $p) }
    throw ($lines -join $LF)
}

# ===== 落地（全部断言通过后才写 OutDir） =====
if (-not (Test-Path -LiteralPath $outChannelDir)) { New-Item -ItemType Directory -Path $outChannelDir -Force | Out-Null }
$finalZip = Join-Path $outChannelDir $zipName
$finalManifest = Join-Path $outChannelDir "manifest.json"
Copy-Item -LiteralPath $zipPath -Destination $finalZip -Force
Copy-Item -LiteralPath $manifestPath -Destination $finalManifest -Force
$manifestItem = Get-Item -LiteralPath $finalManifest
$manifestSha = (Get-FileHash -LiteralPath $finalManifest -Algorithm SHA256).Hash

Write-Host ""
Write-Host ("[update] zip       : " + $finalZip)
Write-Host ("[update] zip bytes : " + ("{0:N0}" -f $zipItem.Length))
Write-Host ("[update] zip sha256: " + $zipSha)
Write-Host ("[update] entries   : " + $fileRecords.Count + " (" + ("{0:N0}" -f $rowsTotal) + " row(s), " + ("{0:N0}" -f $bytesTotal) + " B uncompressed)")
Write-Host ("[update] mods      : " + $mods.Count + " modId(s) over " + $fileRecords.Count + " file(s)")
Write-Host ("[update] manifest  : " + $finalManifest + " (" + ("{0:N0}" -f $manifestItem.Length) + " B, sha256 " + $manifestSha + ")")
Write-Host ("[update] reproducible: rebuilt zip sha256 identical (" + $secondSha + ")")
Write-Host "[update] contents  :"
foreach ($r in $fileRecords) { Write-Host ("[update]   " + $r.path + "  (" + ("{0:N0}" -f $r.bytes) + " B)") }
Write-Host "[update] mods      :"
foreach ($m in $mods) {
    Write-Host ("[update]   " + $m.modId + "  (" + @($m.files).Count + " file(s), " + ("{0:N0}" -f $m.rows) + " row(s), " + ("{0:N0}" -f $m.bytes) + " B, updatedAt " + $m.updatedAt + ")")
}
Write-Host "[update] OK"