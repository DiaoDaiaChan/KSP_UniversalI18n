# Fake-package checks for release-checks.ps1 rules 13-15 (native Localization cfg shape).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tests/powershell/test-release-native-checks.ps1   -> exit 0 = all pass
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
while ($repo -and -not (Test-Path -LiteralPath (Join-Path $repo 'UI18n.sln'))) {
    $repo = Split-Path -Parent $repo
}
if (-not $repo) { throw '找不到仓库根（UI18n.sln）' }
. (Join-Path $repo 'src\UI18n.Runtime\release-checks.ps1')

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('ui18n-native-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$failures = 0
function Note($name, $ok, $detail) {
    if ($ok) { Write-Host ("  ok   " + $name) } else { Write-Host ("  FAIL " + $name + "  " + $detail); $script:failures++ }
}

function New-FakeStage {
    param([string]$Root)
    $mod = Join-Path $Root 'GameData\UI18n'
    New-Item -ItemType Directory -Path (Join-Path $mod 'Plugins') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $mod 'Packs\zh-cn') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $mod 'UI18n.cfg') -Value "Version = 1.2.3" -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $mod 'UI18n.version') -Value '{"VERSION":{"MAJOR":1,"MINOR":2,"PATCH":3}}' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $mod 'Plugins\UI18n.Core.dll') -Value 'core' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $mod 'Plugins\UI18n.Runtime.dll') -Value 'runtime' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $mod 'Packs\zh-cn\Foo.jsonl') -Value '{"h":"1"}' -Encoding UTF8
    return $mod
}

function Try-Check {
    param([string]$Root, [string]$Mod)
    try {
        Assert-ReleaseTree -StageRoot $Root -StageMod $Mod -Version '1.2.3' -ReaderContract '' | Out-Null
        return ''
    } catch { return $_.Exception.Message }
}

function New-Cfg {
    param([string]$Path, [string[]]$Lines)
    Set-Content -LiteralPath $Path -Value $Lines -Encoding UTF8
}

$T = [char]9

# 1) no Native dir -> no violation
$root = Join-Path $tmp 's1'; $mod = New-FakeStage -Root $root
$err = Try-Check -Root $root -Mod $mod
Note 'no-native-dir-passes' ($err -eq '') $err

# 2) two healthy files -> pass
$root = Join-Path $tmp 's2'; $mod = New-FakeStage -Root $root
$nd = Join-Path $mod 'Packs\zh-cn\Native'; New-Item -ItemType Directory -Path $nd -Force | Out-Null
New-Cfg (Join-Path $nd 'A.cfg') @('// gen', 'Localization', '{', ($T + 'zh-cn'), ($T + '{'), ($T + $T + '#kA = aaa'), ($T + '}'), '}')
New-Cfg (Join-Path $nd 'B.cfg') @('Localization', '{', ($T + 'zh-cn'), ($T + '{'), ($T + $T + '#kB = bbb'), ($T + '}'), '}')
$err = Try-Check -Root $root -Mod $mod
Note 'healthy-files-pass' ($err -eq '') $err

# 3) missing close brace -> native brace
$root = Join-Path $tmp 's3'; $mod = New-FakeStage -Root $root
$nd = Join-Path $mod 'Packs\zh-cn\Native'; New-Item -ItemType Directory -Path $nd -Force | Out-Null
New-Cfg (Join-Path $nd 'A.cfg') @('Localization', '{', ($T + 'zh-cn'), ($T + '{'), ($T + $T + '#kA = aaa'))
$err = Try-Check -Root $root -Mod $mod
Note 'unclosed-brace-rejected' ($err -match 'native brace') $err

# 4) extra close brace -> native brace
$root = Join-Path $tmp 's4'; $mod = New-FakeStage -Root $root
$nd = Join-Path $mod 'Packs\zh-cn\Native'; New-Item -ItemType Directory -Path $nd -Force | Out-Null
New-Cfg (Join-Path $nd 'A.cfg') @('Localization', '{', ($T + 'zh-cn'), ($T + '{'), ($T + $T + '#kA = aaa'), '}', '}', '}')
$err = Try-Check -Root $root -Mod $mod
Note 'stray-close-brace-rejected' ($err -match 'native brace') $err

# 5) duplicate key across files -> native dup
$root = Join-Path $tmp 's5'; $mod = New-FakeStage -Root $root
$nd = Join-Path $mod 'Packs\zh-cn\Native'; New-Item -ItemType Directory -Path $nd -Force | Out-Null
New-Cfg (Join-Path $nd 'A.cfg') @('Localization', '{', ($T + 'zh-cn'), ($T + '{'), ($T + $T + '#dup = aaa'), ($T + '}'), '}')
New-Cfg (Join-Path $nd 'B.cfg') @('Localization', '{', ($T + 'zh-cn'), ($T + '{'), ($T + $T + '#dup = bbb'), ($T + '}'), '}')
$err = Try-Check -Root $root -Mod $mod
Note 'duplicate-key-rejected' ($err -match 'native dup') $err

# 6) empty Native dir -> native empty
$root = Join-Path $tmp 's6'; $mod = New-FakeStage -Root $root
New-Item -ItemType Directory -Path (Join-Path $mod 'Packs\zh-cn\Native') -Force | Out-Null
$err = Try-Check -Root $root -Mod $mod
Note 'empty-native-dir-rejected' ($err -match 'native empty') $err

# 7) over 256 KB -> native size
$root = Join-Path $tmp 's7'; $mod = New-FakeStage -Root $root
$nd = Join-Path $mod 'Packs\zh-cn\Native'; New-Item -ItemType Directory -Path $nd -Force | Out-Null
$lines = @('Localization', '{', ($T + 'zh-cn'), ($T + '{'))
for ($i = 0; $i -lt 12000; $i++) { $lines += ($T + $T + '#k' + $i + ' = ' + ('x' * 20)) }
$lines += ($T + '}')
$lines += '}'
New-Cfg (Join-Path $nd 'Big.cfg') $lines
$err = Try-Check -Root $root -Mod $mod
Note 'oversize-file-rejected' ($err -match 'native size') $err

# 8) the real generated files (build\native\zh-cn) inside a fake package -> pass
$realNative = Join-Path $repo 'build\native\zh-cn'
if (Test-Path -LiteralPath $realNative) {
    $root = Join-Path $tmp 's8'; $mod = New-FakeStage -Root $root
    $nd = Join-Path $mod 'Packs\zh-cn\Native'; New-Item -ItemType Directory -Path $nd -Force | Out-Null
    Get-ChildItem -LiteralPath $realNative -Filter *.cfg -File | ForEach-Object { Copy-Item $_.FullName $nd -Force }
    $err = Try-Check -Root $root -Mod $mod
    Note 'real-generated-files-pass' ($err -eq '') $err
} else {
    Write-Host '  skip real generated files missing (run mmgen first)'
}

Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ''
if ($failures -gt 0) { Write-Host ("[native-checks] failed " + $failures); exit 1 }
Write-Host '[native-checks] all passed'
exit 0
