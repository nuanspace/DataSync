[CmdletBinding()]
param(
    [string]$BaseRef,
    [string[]]$ChangedFiles,
    [switch]$FailOnMissingUpdate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'KnowledgeTools.psm1') -Force

$root = Get-DataSyncRepositoryRoot
$map = Get-DataSyncKnowledgeMap -RepositoryRoot $root
$changedFiles = if ($PSBoundParameters.ContainsKey('ChangedFiles')) {
    @($ChangedFiles | ForEach-Object { ConvertTo-DataSyncRepoPath $_ } | Sort-Object -Unique)
}
else {
    @(Get-DataSyncChangedFiles -RepositoryRoot $root -BaseRef $BaseRef)
}

$impacts = foreach ($domain in $map.domains) {
    $sourceMatches = @($changedFiles | Where-Object {
        $changed = $_
        @($domain.source_paths | Where-Object { Test-DataSyncPathPattern -Path $changed -Pattern $_ }).Count -gt 0
    })
    if ($sourceMatches.Count -eq 0) { continue }

    $referenceMatches = @($changedFiles | Where-Object { $domain.references -contains $_ })
    # 保守门禁：review record 只用于审计，不具备放行能力。
    # 只有受影响领域的精确 reference 发生变化，才能证明稳定知识已同步处理。
    $knowledgeUpdated = $referenceMatches.Count -gt 0

    [pscustomobject]@{
        id = $domain.id
        title = $domain.title
        source_changes = $sourceMatches
        knowledge_updated = $knowledgeUpdated
        knowledge_changes = $referenceMatches
        expected_references = @($domain.references)
        reason = "检测到 $($sourceMatches.Count) 个领域源码或契约路径变化"
    }
}

$missing = @($impacts | Where-Object { -not $_.knowledge_updated })
$result = [pscustomobject]@{
    base_ref = $BaseRef
    changed_files = $changedFiles
    impacted_domains = @($impacts)
    missing_updates = $missing
    knowledge_update_required = $missing.Count -gt 0
}
$result | ConvertTo-Json -Depth 8

if ($FailOnMissingUpdate -and $missing.Count -gt 0) {
    Write-Error "业务高影响路径已变化，但有 $($missing.Count) 个领域未同步更新知识库。"
    exit 1
}
