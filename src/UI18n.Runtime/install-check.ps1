# 安装自检（被 deploy.ps1 点源加载）。
# 目的：装完逐文件比对「staging（= 仓库产物）」与「部署副本」的 sha256，
# 不一致就 throw 并列出文件名。这条原来只存在于 _scratch\p1\round.ps1 的前置断言 B/C 里。
#
# 例外：Config.cfg 不比对 —— 安装时保留玩家已改的值、只补新键，内容本来就可能与模板不同。
# 单独抽成文件是为了能用合成目录验证「通过 / 不一致 / 缺文件 / 多余文件」四条路径。

function Assert-InstallTree {
    param(
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$Target
    )

    # 统一成绝对路径：调用方可能传相对路径（例如 -KspRoot 用相对目录），
    # 而 Get-ChildItem 的 FullName 永远是绝对路径，不归一化会把每个文件都误判成「多余」。
    $Stage = [System.IO.Path]::GetFullPath($Stage)
    $Target = [System.IO.Path]::GetFullPath($Target)

    $NL = [Environment]::NewLine
    $mismatch = New-Object System.Collections.Generic.List[string]
    $checked = 0
    $stagedFiles = @(Get-ChildItem -Path $Stage -Recurse -File)

    foreach ($f in $stagedFiles) {
        $rel = $f.FullName.Substring($Stage.Length + 1)
        if ($rel -ieq 'Config.cfg') { continue }

        $dest = Join-Path $Target $rel
        if (-not (Test-Path $dest)) {
            $mismatch.Add("missing after install: $rel")
            continue
        }

        $checked++
        $hashStaged = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
        $hashInstalled = (Get-FileHash $dest -Algorithm SHA256).Hash
        if ($hashStaged -ne $hashInstalled) {
            $mismatch.Add(("sha256 mismatch: {0} (staged {1} != installed {2})" -f $rel, $hashStaged.Substring(0, 16), $hashInstalled.Substring(0, 16)))
        }
    }

    if ($mismatch.Count -gt 0) {
        throw ("[deploy] 自检失败 " + $mismatch.Count + " 条（安装结果与仓库产物不一致）：" + $NL + "  " + ($mismatch -join ($NL + "  ")))
    }

    $stagedRel = @{}
    foreach ($f in $stagedFiles) { $stagedRel[$f.FullName.Substring($Stage.Length + 1)] = $true }
    $extra = @()
    if (Test-Path $Target) {
        $extra = @(Get-ChildItem -Path $Target -Recurse -File | Where-Object { -not $stagedRel.ContainsKey($_.FullName.Substring($Target.Length + 1)) } | ForEach-Object { $_.FullName.Substring($Target.Length + 1) })
    }

    return [pscustomobject]@{
        Checked = $checked
        Extra   = $extra
    }
}
