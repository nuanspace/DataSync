[CmdletBinding()]
param(
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'KnowledgeTools.psm1') -Force

$root = Get-DataSyncRepositoryRoot
$map = Get-DataSyncKnowledgeMap -RepositoryRoot $root
$results = [System.Collections.Generic.List[object]]::new()

Push-Location $root
try {
    foreach ($domain in $map.domains) {
        $commits = @(& git rev-list --reverse "$($domain.last_verified_commit)..HEAD")
        if ($LASTEXITCODE -ne 0) { throw "无法审计领域 $($domain.id) 的 Git 增量。" }

        $lastSourceIndex = -1
        $lastKnowledgeIndex = -1
        $sourceFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $knowledgeFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        for ($index = 0; $index -lt $commits.Count; $index++) {
            $files = @(& git diff-tree --no-commit-id --name-only -r $commits[$index])
            foreach ($file in $files) {
                $normalized = ConvertTo-DataSyncRepoPath $file
                if (@($domain.source_paths | Where-Object { Test-DataSyncPathPattern -Path $normalized -Pattern $_ }).Count -gt 0) {
                    $lastSourceIndex = $index
                    [void]$sourceFiles.Add($normalized)
                }
                if ($domain.references -contains $normalized) {
                    $lastKnowledgeIndex = $index
                    [void]$knowledgeFiles.Add($normalized)
                }
            }
        }

        $status = if ($lastSourceIndex -lt 0) { 'fresh' } elseif ($lastKnowledgeIndex -ge $lastSourceIndex) { 'review-recorded' } else { 'stale' }
        $results.Add([pscustomobject]@{
            id = $domain.id
            title = $domain.title
            status = $status
            last_verified_commit = $domain.last_verified_commit
            source_changes = @($sourceFiles)
            knowledge_changes = @($knowledgeFiles)
        })
    }
}
finally { Pop-Location }

$report = [pscustomobject]@{
    project = $map.project
    audited_head = (& git -C $root rev-parse HEAD)
    generated_at_utc = [DateTime]::UtcNow.ToString('O')
    stale_domains = @($results | Where-Object status -eq 'stale')
    domains = @($results)
}
$json = $report | ConvertTo-Json -Depth 8
if ($OutputPath) {
    $absoluteOutput = if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $root $OutputPath }
    $parent = Split-Path -Parent $absoluteOutput
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Set-Content -LiteralPath $absoluteOutput -Value $json -Encoding UTF8
}
$json

if ($report.stale_domains.Count -gt 0) {
    Write-Warning "检测到 $($report.stale_domains.Count) 个知识领域可能落后于源码。"
}
