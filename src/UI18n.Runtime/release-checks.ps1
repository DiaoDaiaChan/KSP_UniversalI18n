# UI18n 发布包的硬断言（被 build-release.ps1 点源加载）。
# 单独抽成文件是为了能在不真正出包的前提下，用合成的 stage 树验证每一条规则。
#
# 两个入口：
#   Assert-ReleaseProvenance  —— 来源断言（第 8、9 条），跑在最前面
#   Assert-ReleaseTree        —— 内容断言（第 1-7 条），跑在来源断言之后
#
# 规则（命中任意一条就 throw，且一次把全部违规报出来）：
#   1. UI18n.Probe.dll      取证插件，绝不能发布
#   2. *_smoke.jsonl        联调用的冒烟字典
#   3. *.pdb                调试符号
#   4. *.old-<hex>          deploy 留下的回滚备份
#   5. build\ / _scratch\ / _audit\ / dist\ 路径
#   6. packs\pool.jsonl、packs\glossary.jsonl（生产端输入，不是运行时字典）
#   7. Plugins\ 里只能有 UI18n.Core.dll 与 UI18n.Runtime.dll
#  10. UI18n.cfg 的版本行必须是完整的一行「Version = <version>」（恰好 1 行、值等于 -Version），
#      且不许出现「裸版本号」这种畸形行（键名丢失）
#  11. UI18n.version（KSP-AVC）的 VERSION 必须等于 -Version
#
# 下面两条由 Assert-ReleaseProvenance 执行，顺序在最前面（脏树/来源不对就没必要看内容）：
#   8. 工作树必须干净（git status --porcelain 为空；-AllowDirty 显式放行并打 WARN）
#   9. staging 里两个 DLL 内嵌的 1.0.0+<commit> 必须等于 git rev-parse HEAD

function Assert-ReleaseTree {
    param(
        [Parameter(Mandatory = $true)][string]$StageRoot,
        [Parameter(Mandatory = $true)][string]$StageMod,
        # 发布版本号：第 10、11 条要拿它比对 staging 里两个版本文件的内容。空串则只做「形状」检查。
        [string]$Version = ''
    )

    $StageRoot = [System.IO.Path]::GetFullPath($StageRoot)
    $StageMod = [System.IO.Path]::GetFullPath($StageMod)

    $NL = [Environment]::NewLine
    $stagedFiles = @(Get-ChildItem $StageRoot -Recurse -File -ErrorAction SilentlyContinue)
    $violations = New-Object System.Collections.Generic.List[string]

    foreach ($f in $stagedFiles) {
        $rel = $f.FullName.Substring($StageRoot.Length + 1)
        if ($f.Name -ieq 'UI18n.Probe.dll') { $violations.Add("probe dll: $rel（取证插件，绝不能发布）") }
        if ($f.Name -like '*_smoke.jsonl') { $violations.Add("smoke pack: $rel（_smoke.jsonl 只用于联调）") }
        if ($f.Name -like '*.pdb') { $violations.Add("symbols: $rel（*.pdb 不进包）") }
        if ($f.Name -match '\.old-[0-9a-fA-F]+') { $violations.Add("backup dll: $rel（*.old-* 是回滚备份）") }
        if ($rel -match '(^|\\)(build|_scratch|_audit|dist)(\\|$)') { $violations.Add("internal path: $rel（build/_scratch/_audit/dist 不进包）") }
        if ($rel -match '(^|\\)packs\\(pool|glossary)\.jsonl$') { $violations.Add("pipeline input: $rel（packs 根的 pool/glossary 是生产端输入）") }
    }

    $pluginsDir = Join-Path $StageMod 'Plugins'
    $stagedPlugins = @(Get-ChildItem $pluginsDir -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name } | Sort-Object)
    $expectedPlugins = @('UI18n.Core.dll', 'UI18n.Runtime.dll')
    if (($stagedPlugins -join ',') -ne ($expectedPlugins -join ',')) {
        $violations.Add(("plugins: Plugins\ 里应只有 {0}，实际 {1}" -f ($expectedPlugins -join ', '), ($stagedPlugins -join ', ')))
    }

    # 第 10 条：UI18n.cfg 的版本行必须是完整的一行「Version = x.y.z」。
    # 为什么必须查内容：第 1-7 条只查「有没有多余文件」，查不出「文件被改写坏」。
    # 2026-10-23 的事故：staging 把「Version = 1.0.0」改写成裸的「11.0.0」（键名整段丢失），
    # zip 里就多了一条 KSP ConfigNode 解析不了的畸形行；所有自动化断言都放过了，靠人核 zip 才发现。
    $modCfgPath = Join-Path $StageMod 'UI18n.cfg'
    if (-not (Test-Path $modCfgPath)) {
        $violations.Add('cfg missing: UI18n.cfg（包里必须有 Mod 元数据）')
    }
    else {
        $cfgLines = @(Get-Content -LiteralPath $modCfgPath -Encoding UTF8)
        $versionLines = @($cfgLines | Where-Object { $_ -match '^\s*Version\s*=' })
        $bareVersionLines = @($cfgLines | Where-Object { $_ -match '^\s*[0-9]+\.[0-9]+\.[0-9]+\s*$' })

        if ($versionLines.Count -ne 1) {
            $shown = ($versionLines | ForEach-Object { '[' + $_.Trim() + ']' }) -join ' / '
            $violations.Add(("cfg version: UI18n.cfg 的版本行应恰好 1 行（形如 Version = 1.0.0），实际 " + $versionLines.Count + " 行" + $(if ($shown) { "：$shown" } else { '（一行都没有）' })))
        }
        elseif ($Version) {
            $actual = ''
            if ($versionLines[0] -match '^\s*Version\s*=\s*(\S+)\s*$') { $actual = $Matches[1] }
            if ($actual -ne $Version) {
                $violations.Add(("cfg version: UI18n.cfg 的 Version = " + $actual + " 与 -Version " + $Version + " 不一致（实际行：[" + $versionLines[0].Trim() + "]）"))
            }
        }

        if ($bareVersionLines.Count -gt 0) {
            $shown = ($bareVersionLines | ForEach-Object { '[' + $_.Trim() + ']' }) -join ' / '
            $violations.Add(("cfg malformed: UI18n.cfg 有畸形行（只有裸版本号、键名丢失）：$shown"))
        }
    }

    # 第 11 条：UI18n.version（KSP-AVC）的 VERSION 必须等于 -Version（模板里的 1.0.0 只是默认值）。
    $avcPath = Join-Path $StageMod 'UI18n.version'
    if (-not (Test-Path $avcPath)) {
        $violations.Add('avc missing: UI18n.version（KSP-AVC 读它做版本提示）')
    }
    elseif ($Version) {
        try {
            $avc = Get-Content -LiteralPath $avcPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $avcVersion = ('{0}.{1}.{2}' -f $avc.VERSION.MAJOR, $avc.VERSION.MINOR, $avc.VERSION.PATCH)
            if ($avcVersion -ne $Version) {
                $violations.Add(("avc version: UI18n.version 的 VERSION = " + $avcVersion + " 与 -Version " + $Version + " 不一致"))
            }
        }
        catch {
            $violations.Add(("avc parse: UI18n.version 不是合法 JSON：" + $_.Exception.Message))
        }
    }

    if ($violations.Count -gt 0) {
        throw ("[release] 硬断言失败 " + $violations.Count + " 条，拒绝出包：" + $NL + "  " + ($violations -join ($NL + "  ")))
    }

    return $stagedFiles
}

# ---------------------------------------------------------------------------
# 来源断言（第 8、9 条）：发布物必须来自「已提交的、且就是当前 HEAD 的」源码。
#
# 为什么要有这两条：.NET SDK 把 SourceRevisionId 追加进 InformationalVersion，
# 于是每个 commit 编出来的 DLL 字节都不同（Deterministic 保证的是同一 commit 内可复现）。
# 脏工作树编出来的二进制会内嵌一个并不包含它源码的 commit（1.0.0+fc1c2b2 那次事故）。
# 详见 README §10.8。
# ---------------------------------------------------------------------------

# 从 DLL 字节里读内嵌的 1.0.0+<commit> 来源标记。
# DLL 的元数据字符串是 UTF-8，而版本串本身全是 ASCII（数字点号 + 40 位 hex），
# 所以整份字节按 ASCII 解码后正则找就够了，不用解析 PE/CLI 头。
function Get-EmbeddedInformationalVersion {
    param([Parameter(Mandatory = $true)][string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $text = [System.Text.Encoding]::ASCII.GetString($bytes)
    $pattern = '[0-9]+\.[0-9]+\.[0-9]+\+[0-9a-fA-F]{40}'
    return @([regex]::Matches($text, $pattern) | ForEach-Object { $_.Value } | Select-Object -Unique)
}

# 未跟踪文件是否落在「构建输入」目录之下：这些目录里的东西会进构建（src/tests/tools）或进包（packs）。
# Windows 路径分隔符先统一成正斜杠；前缀比较用 OrdinalIgnoreCase（与 Windows 文件系统一致）。
function Test-BuildInputPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $p = $Path.Replace('\', '/')
    foreach ($root in @('src/', 'tests/', 'tools/', 'packs/')) {
        if ($p.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Assert-ReleaseProvenance {
    param(
        [Parameter(Mandatory = $true)][string]$StageMod,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        # 发布版本号：DLL 内嵌标记的 + 前必须等于它（发布版本 == DLL 版本）
        [string]$Version = '',
        [switch]$AllowDirty
    )

    $NL = [Environment]::NewLine
    $violations = New-Object System.Collections.Generic.List[string]
    $notes = New-Object System.Collections.Generic.List[string]

    # 8) 工作树判据（定稿口径，见 README §10.8）：一次 git status --porcelain -z 拿全量，按状态码判断
    #    a. 已跟踪文件有任何改动 / 删除 / 暂存未提交                    -> 阻断（脏树编出来的二进制与已提交源码不对应）
    #    b. 未跟踪文件落在构建输入目录（src/ tests/ tools/ packs/）之下  -> 阻断（会进构建或进包）
    #    c. 其余未跟踪文件（reports/ build/ dist/ _scratch/ …）          -> 仅告警（每轮验证都会产生，不该挡住重建）
    # -z 不做路径转义（中文路径不会被引号包起来）；重命名/复制的旧路径作为下一个 NUL 字段跟随。
    $rawStatus = & git -C $RepoRoot status --porcelain -z 2>&1
    $statusExit = $LASTEXITCODE
    $entries = @()
    if ($statusExit -eq 0) {
        $flat = if ($rawStatus -is [string]) { $rawStatus } else { ($rawStatus | ForEach-Object { "$_" }) -join '' }
        $fields = @($flat.Split([char]0))
        $i = 0
        while ($i -lt $fields.Count) {
            $field = $fields[$i]
            $i++
            if ($null -eq $field -or $field.Length -lt 3) { continue }
            $code = $field.Substring(0, 2)
            $path = $field.Substring(3)
            if ($code.StartsWith('R') -or $code.StartsWith('C')) { $i++ }   # 旧路径紧跟其后，跳过
            $entries += [pscustomobject]@{ Code = $code; Path = $path }
        }
    }

    $trackedChanges = @($entries | Where-Object { $_.Code -ne '??' })
    $untrackedAll = @($entries | Where-Object { $_.Code -eq '??' })
    $untrackedInputs = @($untrackedAll | Where-Object { Test-BuildInputPath -Path $_.Path })
    $untrackedOther = @($untrackedAll | Where-Object { -not (Test-BuildInputPath -Path $_.Path) })
    $blockingCount = $trackedChanges.Count + $untrackedInputs.Count

    $headSha = ''
    if ($statusExit -ne 0) {
        $violations.Add("git: git status --porcelain -z 失败（exit " + $statusExit + "）（本脚本必须在 git 工作树里跑，见 README §10.8）")
    } else {
        $rawHead = & git -C $RepoRoot rev-parse HEAD 2>&1
        if ($LASTEXITCODE -ne 0) {
            $violations.Add("git: git rev-parse HEAD 失败：" + (($rawHead | ForEach-Object { "$_" }) -join ' / '))
        } else {
            $headSha = (($rawHead | ForEach-Object { "$_" }) -join '').Trim().ToLowerInvariant()
        }

        if ($blockingCount -gt 0) {
            if ($AllowDirty) {
                $notes.Add("WARN: working tree is dirty (" + $trackedChanges.Count + " tracked change(s), " + $untrackedInputs.Count + " untracked build input(s)), release built with -AllowDirty")
            } else {
                if ($trackedChanges.Count -gt 0) {
                    $preview = (@($trackedChanges | Select-Object -First 5 | ForEach-Object { $_.Path }) -join ', ')
                    $more = ''
                    if ($trackedChanges.Count -gt 5) { $more = ' (+' + ($trackedChanges.Count - 5) + ' more)' }
                    $violations.Add("dirty working tree: tracked changes: " + $preview + $more + "（见 README §10.8；确需脏树出包显式传 -AllowDirty）")
                }
                foreach ($u in $untrackedInputs) {
                    $violations.Add("untracked build input: " + $u.Path + "（未跟踪的源码/数据会进构建或进包：先 git add 或删掉）")
                }
            }
        }

        if ($untrackedOther.Count -gt 0) {
            $preview = (@($untrackedOther | Select-Object -First 3 | ForEach-Object { $_.Path }) -join ', ')
            $notes.Add("WARN: " + $untrackedOther.Count + " untracked file(s) outside build inputs (ignored by this assertion): " + $preview)
        }
    }

    # 9) 来源一致性：staging 里两个 DLL 内嵌的 commit 必须等于 HEAD
    $embedded = @{}
    $pluginsDir = Join-Path $StageMod 'Plugins'
    foreach ($dll in @('UI18n.Core.dll', 'UI18n.Runtime.dll')) {
        $p = Join-Path $pluginsDir $dll
        if (-not (Test-Path $p)) {
            $violations.Add("provenance: 缺少 staging 文件 Plugins" + $dll + "（来源标记无从核对）")
            continue
        }
        $found = @(Get-EmbeddedInformationalVersion -Path $p)
        if ($found.Count -eq 0) {
            $violations.Add("provenance: " + $dll + " 里找不到 1.0.0+<commit> 来源标记（SourceRevisionId 是不是被关掉了？见 README §10.8）")
            continue
        }
        if ($found.Count -gt 1) {
            $violations.Add("provenance: " + $dll + " 里有 " + $found.Count + " 个不同的来源标记（" + ($found -join ', ') + "），无法判定")
            continue
        }
        $marker = $found[0]
        $embedded[$dll] = $marker
        $plus = $marker.IndexOf('+')
        $markerVersion = $marker.Substring(0, $plus)
        $markerSha = $marker.Substring($plus + 1).ToLowerInvariant()

        # 判据 1：+ 前的版本号必须等于 -Version（发布版本 == DLL 版本）
        if ($Version -and $markerVersion -ne $Version) {
            $violations.Add("provenance: " + $dll + " 版本号 " + $markerVersion + " 与 -Version " + $Version + " 不一致（构建时要把 -Version 传进 /p:Version=，见 README §10.8）")
        }

        # 判据 2：+ 后的 commit 必须等于当前 HEAD
        if ($headSha.Length -eq 40 -and $markerSha -ne $headSha) {
            $violations.Add("provenance: " + $dll + " 内嵌 " + $marker + " / HEAD " + $headSha + " —— 二进制声称的 commit 不是当前 HEAD，发布物必须由 HEAD 的源码构建（见 README §10.8）")
        }
    }

    foreach ($n in $notes) { Write-Host ("[release] " + $n) -ForegroundColor Yellow }

    if ($violations.Count -gt 0) {
        throw ("[release] 发布来源断言失败 " + $violations.Count + " 条，拒绝出包：" + $NL + "  " + ($violations -join ($NL + "  ")))
    }

    return [pscustomobject]@{
        HeadSha           = $headSha
        Version           = $Version
        DirtyCount        = $blockingCount
        TrackedChanges    = $trackedChanges.Count
        UntrackedInputs   = $untrackedInputs.Count
        UntrackedIgnored  = $untrackedOther.Count
        Embedded          = $embedded
    }
}

