<#
.SYNOPSIS
    收一份来自 GitHub Issue 的玩家投稿（下载附件 → api submit accept → 可选回评论）。

.DESCRIPTION
    v1 的投稿通道是零服务器（docs/10 §6.9）：玩家在游戏内导出
    ui18n-submission-<id>.json，拖进 .github/ISSUE_TEMPLATE/submit-texts.yml
    开的 issue，再由人或 CI 跑这个脚本收下。

    三件事严格按顺序做：
      1. 读 issue 正文，抽出附件 URL（只认 github.com/user-attachments/files/*.json）；
      2. 逐个下载并预检（必须是 JSON 且 schema 逐字等于 ui18n-submission/1）；
      3. 交给 python -m i18n_tools.api submit accept —— 校验、id 派生、幂等/冲突、
         按 mod 拆文件、归并 candidates、写 journal 全部由它负责，本脚本不重复实现。

    本脚本自己不做任何判定：api 的退出码原样透出（0 收下 / 4 门禁 / 5 schema 非法 /
    6 不存在 / 7 内部错误），因此可以安全地塞进 CI。

.EXAMPLE
    pwsh -File tools\accept-submission-issue.ps1 -Issue 42 -DryRun

.EXAMPLE
    pwsh -File tools\accept-submission-issue.ps1 -Issue 42 -Comment

.EXAMPLE
    # 没有 issue（例如玩家直接把文件发在 Discord 上）：跳过下载，直接收本地文件
    pwsh -File tools\accept-submission-issue.ps1 -Bundle D:\tmp\ui18n-submission-9f3c1a77b0e54d21.json
#>
[CmdletBinding(DefaultParameterSetName = "Issue")]
param(
    [Parameter(Mandatory = $true, ParameterSetName = "Issue")][int]$Issue,
    [Parameter(Mandatory = $true, ParameterSetName = "Bundle")][string]$Bundle,
    [string]$Repo = "DiaoDaiaChan/KSP_UniversalI18n",
    [string]$Root = "",
    [string]$Actor = "pipeline:submission",
    [string]$DownloadDir = "",
    [switch]$DryRun,
    [switch]$Comment
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Root) { $Root = $repoRoot }
if (-not $DownloadDir) {
    $DownloadDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ui18n-submit-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
}

function Write-Step([string]$Message) { Write-Host "[accept] $Message" }

$Schema = "ui18n-submission/1"

function Test-Bundle([string]$Path) {
    $raw = Get-Content -LiteralPath $Path -Raw -Encoding utf8
    $obj = $null
    try { $obj = $raw | ConvertFrom-Json } catch { throw "不是合法 JSON：$Path（$($_.Exception.Message)）" }
    if (-not ($obj.PSObject.Properties.Name -contains "schema")) { throw "缺 schema 字段：$Path" }
    if ($obj.schema -ne $Schema) { throw "schema 不对：$Path 里是 $($obj.schema)，要 $Schema" }
    $entries = @($obj.entries)
    if ($entries.Count -lt 1) { throw "entries 为空：$Path" }
    return $obj
}

# ---- 1. 拿到待收的本地文件清单 ----
$localFiles = @()
if ($PSCmdlet.ParameterSetName -eq "Bundle") {
    if (-not (Test-Path -LiteralPath $Bundle)) { throw "找不到投稿文件：$Bundle" }
    $localFiles = @($Bundle)
    Write-Step ("bundle : " + $Bundle)
} else {
    Write-Step ("issue  : " + $Repo + "#" + $Issue)
    $json = gh api ("repos/" + $Repo + "/issues/" + $Issue) 2>&1 | Out-String
    if (-not $?) { throw ("gh api 失败（先 gh auth login）：" + $json) }
    $issueObj = $json | ConvertFrom-Json
    $body = [string]$issueObj.body
    Write-Step ("title  : " + $issueObj.title)

    # 附件在正文里是 markdown 链接：[name.json](https://github.com/user-attachments/files/123456/name.json)
    $pattern = 'https://github\.com/user-attachments/files/[0-9]+/[^\s\)\]<>"'']+'
    $urls = [regex]::Matches($body, $pattern)
    if ($urls.Count -eq 0) {
        throw "issue 正文里没找到附件（要 https://github.com/user-attachments/files/… 形态；玩家可能只贴了文本，没拖文件）"
    }
    $null = New-Item -ItemType Directory -Force -Path $DownloadDir
    $seen = @{}
    foreach ($m in $urls) {
        $url = $m.Value
        if ($seen.ContainsKey($url)) { continue }
        $seen[$url] = $true
        $name = [System.IO.Path]::GetFileName([uri]::UnescapeDataString($url))
        if ($name -notmatch "\.json$") { $name = $name + ".json" }
        $dest = Join-Path $DownloadDir $name
        Write-Step ("GET    : " + $url)
        Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
        if (-not (Test-Path -LiteralPath $dest)) { throw "下载失败：$url" }
        $localFiles += $dest
    }
    Write-Step ("files  : " + $localFiles.Count + " 份，落在 " + $DownloadDir)
}

# ---- 2. 预检（只查 schema 与 entries，其余交给 api）----
$summaries = @()
foreach ($f in $localFiles) {
    $obj = Test-Bundle $f
    $modCount = @($obj.mods).Count
    $entryCount = @($obj.entries).Count
    $summaries += [pscustomobject]@{ File = $f; Id = [string]$obj.id; Mods = $modCount; Entries = $entryCount }
    Write-Step ("预检 ok : " + [System.IO.Path]::GetFileName($f) + "  id=" + $obj.id + " mods=" + $modCount + " entries=" + $entryCount)
}

# ---- 3. 交给 api（唯一判定者）----
$apiArgs = @("-m", "i18n_tools.api", "submit", "accept", "--root", $Root, "--actor", $Actor, "--json")
if ($DryRun) { $apiArgs += "--dry-run" }

$exit = 0
$accepted = @()
Push-Location (Join-Path $repoRoot "tools")
try {
    foreach ($s in $summaries) {
        $suffix = ""
        if ($DryRun) { $suffix = " (--dry-run)" }
        Write-Step ("api    : submit accept " + [System.IO.Path]::GetFileName($s.File) + $suffix)
        $out = & python @apiArgs --bundle $s.File 2>&1 | Out-String
        $code = $LASTEXITCODE
        Write-Host $out.TrimEnd()
        if ($code -ne 0) {
            Write-Step ("api 拒绝，退出码 " + $code + "（0 成功 / 2 用法 / 3 revision 冲突 / 4 门禁 / 5 schema / 6 不存在 / 7 内部）")
            $exit = $code
            continue
        }
        $payload = $null
        try { $payload = $out | ConvertFrom-Json } catch { }
        $acc = [pscustomobject]@{ Id = $s.Id; Already = $false; Rows = $s.Entries; Mods = @(); File = $s.File }
        if ($payload) {
            $acc.Id = [string]$payload.submission_id
            $acc.Already = [bool]$payload.already
            $acc.Rows = [int]$payload.rows
            $acc.Mods = @($payload.mods)
        }
        $accepted += $acc
    }
}
finally {
    Pop-Location
}

# ---- 4. 可选：回一行评论 ----
if ($Comment -and $PSCmdlet.ParameterSetName -eq "Issue" -and $accepted.Count -gt 0 -and -not $DryRun) {
    $lines = @("收下了，谢谢。", "")
    foreach ($a in $accepted) {
        if ($a.Already) {
            $lines += ("- **" + $a.Id + "** 之前已经收过（内容相同，没有重复写文件）")
        } else {
            $mods = ($a.Mods | Sort-Object) -join ", "
            $lines += ("- **" + $a.Id + "**：" + $a.Rows + " 条原文 / " + $a.Mods.Count + " 个 mod（" + $mods + "）")
        }
    }
    $lines += ""
    $lines += "已并入候选表，接下来由 CI 走翻译与门禁；进包后游戏里点更新能看到。"
    $bodyFile = Join-Path $DownloadDir "comment.md"
    Set-Content -LiteralPath $bodyFile -Value ($lines -join "`n") -Encoding utf8
    gh issue comment $Issue --repo $Repo --body-file $bodyFile | Out-Null
    if ($?) { Write-Step ("commented: " + $Repo + "#" + $Issue) }
    else { Write-Step "评论失败（不影响已收下的数据）" }
}

if ($exit -ne 0) { exit $exit }
Write-Step "done"
exit 0
