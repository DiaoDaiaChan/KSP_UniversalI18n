# UI18n 发布包的硬断言（被 build-release.ps1 点源加载）。
# 单独抽成文件是为了能在不真正出包的前提下，用合成的 stage 树验证每一条规则。
# 规则（命中任意一条就 throw，且一次把全部违规报出来）：
#   1. UI18n.Probe.dll      取证插件，绝不能发布
#   2. *_smoke.jsonl        联调用的冒烟字典
#   3. *.pdb                调试符号
#   4. *.old-<hex>          deploy 留下的回滚备份
#   5. build\ / _scratch\ / _audit\ / dist\ 路径
#   6. packs\pool.jsonl、packs\glossary.jsonl（生产端输入，不是运行时字典）
#   7. Plugins\ 里只能有 UI18n.Core.dll 与 UI18n.Runtime.dll

function Assert-ReleaseTree {
    param(
        [Parameter(Mandatory = $true)][string]$StageRoot,
        [Parameter(Mandatory = $true)][string]$StageMod
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

    if ($violations.Count -gt 0) {
        throw ("[release] 硬断言失败 " + $violations.Count + " 条，拒绝出包：" + $NL + "  " + ($violations -join ($NL + "  ")))
    }

    return $stagedFiles
}
