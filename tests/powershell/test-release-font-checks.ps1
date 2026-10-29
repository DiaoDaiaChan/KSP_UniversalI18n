# Fake-package checks for release-checks.ps1 rule 16 (bundled font: fonts.json vs the real file).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tests/powershell/test-release-font-checks.ps1
#        -> exit 0 = every case matched its expectation.
$ErrorActionPreference = 'Stop'

$repo = $PSScriptRoot
while ($repo -and -not (Test-Path -LiteralPath (Join-Path $repo 'UI18n.sln'))) {
    $repo = Split-Path -Parent $repo
}
if (-not $repo) { throw '找不到仓库根（UI18n.sln）' }
. (Join-Path $repo 'src/UI18n.Runtime/release-checks.ps1')

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('ui18n-font-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$failures = 0
$checks = 0
function Note($name, $ok, $detail) {
    $script:checks++
    if ($ok) { Write-Host ('  ok   ' + $name) }
    else { Write-Host ('  FAIL ' + $name + '  ' + $detail); $script:failures++ }
}

function New-FakeStage {
    param([string]$Root)
    $mod = Join-Path $Root 'GameData\UI18n'
    New-Item -ItemType Directory -Path (Join-Path $mod 'Plugins') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $mod 'Packs\zh-cn') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $mod 'UI18n.cfg') -Value 'Version = 1.2.3' -Encoding UTF8
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
    }
    catch { return $_.Exception.Message }
}

function Write-Font {
    param([string]$Dir, [string]$Name, [byte[]]$Bytes)
    $path = Join-Path $Dir $Name
    [System.IO.File]::WriteAllBytes($path, $Bytes)
    return $path
}

function Get-Sha {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Write-Manifest {
    param(
        [string]$Dir, [string]$File, [string]$Sha, [long]$Bytes,
        [string]$LicenseSpdx = 'OFL-1.1', [string]$LicenseFile = 'OFL.txt',
        [string]$Schema = 'ui18n-font-bundle/1', [switch]$ArrayForm
    )
    $obj = [ordered]@{ schema = $Schema; file = $File; bytes = $Bytes; sha256 = $Sha }
    if ($LicenseSpdx) { $obj['license'] = $LicenseSpdx }
    if ($LicenseFile) { $obj['licenseFile'] = $LicenseFile }
    $payload = if ($ArrayForm) { @{ fonts = @($obj) } } else { $obj }
    Set-Content -LiteralPath (Join-Path $Dir 'fonts.json') -Value ($payload | ConvertTo-Json -Depth 6) -Encoding UTF8
}

function New-GoodFonts {
    param([string]$Root)
    $mod = New-FakeStage -Root $Root
    $dir = Join-Path $mod 'Fonts'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $bytes = [byte[]](0x55, 0x6E, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00, 0x01, 0x02)
    $path = Write-Font -Dir $dir -Name 'NotoSansSC.bundle' -Bytes $bytes
    Set-Content -LiteralPath (Join-Path $dir 'OFL.txt') -Value 'SIL Open Font License 1.1' -Encoding UTF8
    Write-Manifest -Dir $dir -File 'NotoSansSC.bundle' -Sha (Get-Sha $path) -Bytes $bytes.Length
    return $mod
}

try {
    # 1) no Fonts dir at all -> nothing to check (OsFont mode)
    $root = Join-Path $tmp 'c1'; $mod = New-FakeStage -Root $root
    $err = Try-Check -Root $root -Mod $mod
    Note 'no-fonts-dir-passes' ($err -eq '') $err

    # 2) placeholder-only Fonts dir (README.txt, no bundle) -> pass
    $root = Join-Path $tmp 'c2'; $mod = New-FakeStage -Root $root
    $dir = Join-Path $mod 'Fonts'; New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $dir 'README.txt') -Value 'placeholder' -Encoding UTF8
    $err = Try-Check -Root $root -Mod $mod
    Note 'placeholder-only-passes' ($err -eq '') $err

    # 3) healthy bundle + manifest + license -> pass
    $root = Join-Path $tmp 'c3'; $mod = New-GoodFonts -Root $root
    $err = Try-Check -Root $root -Mod $mod
    Note 'healthy-font-passes' ($err -eq '') $err

    # 4) bundle without manifest -> rejected
    $root = Join-Path $tmp 'c4'; $mod = New-GoodFonts -Root $root
    Remove-Item -LiteralPath (Join-Path $mod 'Fonts\fonts.json') -Force
    $err = Try-Check -Root $root -Mod $mod
    Note 'bundle-without-manifest-rejected' ($err -match 'font manifest') $err

    # 5) manifest that is not JSON -> rejected
    $root = Join-Path $tmp 'c5'; $mod = New-GoodFonts -Root $root
    Set-Content -LiteralPath (Join-Path $mod 'Fonts\fonts.json') -Value '{ not json' -Encoding UTF8
    $err = Try-Check -Root $root -Mod $mod
    Note 'broken-json-rejected' ($err -match 'font manifest') $err

    # 6) wrong schema -> rejected
    $root = Join-Path $tmp 'c6'; $mod = New-GoodFonts -Root $root
    $p = Join-Path $mod 'Fonts\NotoSansSC.bundle'
    Write-Manifest -Dir (Join-Path $mod 'Fonts') -File 'NotoSansSC.bundle' -Sha (Get-Sha $p) -Bytes (Get-Item -LiteralPath $p).Length -Schema 'ui18n-font-bundle/2'
    $err = Try-Check -Root $root -Mod $mod
    Note 'wrong-schema-rejected' ($err -match 'font manifest') $err

    # 7) unsafe file name -> rejected
    $root = Join-Path $tmp 'c7'; $mod = New-GoodFonts -Root $root
    Write-Manifest -Dir (Join-Path $mod 'Fonts') -File '../evil.bundle' -Sha '00' -Bytes 1
    $err = Try-Check -Root $root -Mod $mod
    Note 'unsafe-name-rejected' ($err -match 'font manifest') $err

    # 8) manifest points at a file that is not in the package -> rejected
    $root = Join-Path $tmp 'c8'; $mod = New-GoodFonts -Root $root
    Write-Manifest -Dir (Join-Path $mod 'Fonts') -File 'Missing.bundle' -Sha '00' -Bytes 1
    $err = Try-Check -Root $root -Mod $mod
    Note 'missing-file-rejected' ($err -match 'font manifest') $err

    # 9) byte count mismatch -> rejected
    $root = Join-Path $tmp 'c9'; $mod = New-GoodFonts -Root $root
    $p = Join-Path $mod 'Fonts\NotoSansSC.bundle'
    Write-Manifest -Dir (Join-Path $mod 'Fonts') -File 'NotoSansSC.bundle' -Sha (Get-Sha $p) -Bytes ((Get-Item -LiteralPath $p).Length + 1)
    $err = Try-Check -Root $root -Mod $mod
    Note 'byte-count-mismatch-rejected' ($err -match 'font bytes') $err

    # 10) sha256 mismatch -> rejected
    $root = Join-Path $tmp 'c10'; $mod = New-GoodFonts -Root $root
    $p = Join-Path $mod 'Fonts\NotoSansSC.bundle'
    Write-Manifest -Dir (Join-Path $mod 'Fonts') -File 'NotoSansSC.bundle' -Sha ('A' * 64) -Bytes (Get-Item -LiteralPath $p).Length
    $err = Try-Check -Root $root -Mod $mod
    Note 'sha256-mismatch-rejected' ($err -match 'font sha256') $err

    # 11) manifest without licenseFile -> rejected (OFL requires shipping the license text)
    $root = Join-Path $tmp 'c11'; $mod = New-GoodFonts -Root $root
    $p = Join-Path $mod 'Fonts\NotoSansSC.bundle'
    Write-Manifest -Dir (Join-Path $mod 'Fonts') -File 'NotoSansSC.bundle' -Sha (Get-Sha $p) -Bytes (Get-Item -LiteralPath $p).Length -LicenseFile ''
    $err = Try-Check -Root $root -Mod $mod
    Note 'missing-license-field-rejected' ($err -match 'font license') $err

    # 12) licenseFile declared but absent -> rejected
    $root = Join-Path $tmp 'c12'; $mod = New-GoodFonts -Root $root
    Remove-Item -LiteralPath (Join-Path $mod 'Fonts\OFL.txt') -Force
    $err = Try-Check -Root $root -Mod $mod
    Note 'missing-license-file-rejected' ($err -match 'font license') $err

    # 13) bundle over the 6 MB budget -> rejected
    $root = Join-Path $tmp 'c13'; $mod = New-FakeStage -Root $root
    $dir = Join-Path $mod 'Fonts'; New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $big = New-Object byte[] (6 * 1024 * 1024 + 1)
    $p = Write-Font -Dir $dir -Name 'Huge.bundle' -Bytes $big
    Set-Content -LiteralPath (Join-Path $dir 'OFL.txt') -Value 'SIL Open Font License 1.1' -Encoding UTF8
    Write-Manifest -Dir $dir -File 'Huge.bundle' -Sha (Get-Sha $p) -Bytes $big.Length
    $err = Try-Check -Root $root -Mod $mod
    Note 'over-size-budget-rejected' ($err -match 'font size') $err

    # 14) {"fonts":[...]} array form is accepted too
    $root = Join-Path $tmp 'c14'; $mod = New-GoodFonts -Root $root
    $p = Join-Path $mod 'Fonts\NotoSansSC.bundle'
    Write-Manifest -Dir (Join-Path $mod 'Fonts') -File 'NotoSansSC.bundle' -Sha (Get-Sha $p) -Bytes (Get-Item -LiteralPath $p).Length -ArrayForm
    $err = Try-Check -Root $root -Mod $mod
    Note 'array-form-passes' ($err -eq '') $err
}
finally {
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ('checks=' + $checks + ' failures=' + $failures)
if ($failures -gt 0) { exit 1 }
exit 0
