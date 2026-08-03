[CmdletBinding()]
param(
    [string]$Query = '',
    [string[]]$Paths = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'KnowledgeTools.psm1') -Force

$map = Get-DataSyncKnowledgeMap
$normalizedQuery = $Query.ToLowerInvariant()
$normalizedPaths = @($Paths | ForEach-Object { ConvertTo-DataSyncRepoPath $_ })

$candidates = foreach ($domain in $map.domains) {
    $score = 0
    $reasons = [System.Collections.Generic.List[string]]::new()

    foreach ($path in $normalizedPaths) {
        foreach ($pattern in $domain.source_paths) {
            if (Test-DataSyncPathPattern -Path $path -Pattern $pattern) {
                $score += 8
                $reasons.Add("路径命中 $pattern")
                break
            }
        }
    }

    foreach ($keyword in $domain.keywords) {
        if ($normalizedQuery -and $normalizedQuery.Contains(([string]$keyword).ToLowerInvariant())) {
            $score += 3
            $reasons.Add("关键词 $keyword")
        }
    }
    foreach ($symbol in $domain.symbols) {
        if ($normalizedQuery -and $normalizedQuery.Contains(([string]$symbol).ToLowerInvariant())) {
            $score += 2
            $reasons.Add("符号 $symbol")
        }
    }
    foreach ($workflow in $domain.workflows) {
        if ($normalizedQuery -and $normalizedQuery.Contains(([string]$workflow).ToLowerInvariant())) {
            $score += 2
            $reasons.Add("流程 $workflow")
        }
    }

    $confidence = if ($score -ge 8) { 0.9 } elseif ($score -ge 5) { 0.75 } elseif ($score -ge 3) { 0.55 } elseif ($score -gt 0) { 0.35 } else { 0.0 }
    [pscustomobject]@{
        id = $domain.id
        title = $domain.title
        skill = $domain.skill
        score = $score
        confidence = $confidence
        reasons = @($reasons | Select-Object -Unique)
        references = @($domain.references)
    }
}

$ranked = @($candidates | Sort-Object -Property @{ Expression = 'score'; Descending = $true }, @{ Expression = 'id'; Descending = $false })
$top = $ranked | Select-Object -First 1
if ($top.confidence -ge 0.75) {
    $recommended = @($ranked | Where-Object { $_.score -eq $top.score } | Select-Object -First 2)
    $strategy = '直接读取最高置信度领域；同分时最多读取两个领域。'
}
elseif ($top.confidence -ge 0.45) {
    $recommended = @($ranked | Where-Object { $_.score -gt 0 } | Select-Object -First 2)
    $strategy = '先检查候选入口，再选定一个主要领域。'
}
else {
    $recommended = @($ranked | Where-Object { $_.id -eq 'architecture' })
    $strategy = '领域不明确，先读取架构路由，再根据代码定位扩展。'
}

[pscustomobject]@{
    query = $Query
    paths = $normalizedPaths
    strategy = $strategy
    recommended = $recommended
    candidates = @($ranked | Where-Object { $_.score -gt 0 } | Select-Object -First 3)
} | ConvertTo-Json -Depth 8
