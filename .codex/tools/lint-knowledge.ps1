[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'KnowledgeTools.psm1') -Force

$root = Get-DataSyncRepositoryRoot
$map = Get-DataSyncKnowledgeMap -RepositoryRoot $root
$errors = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()

if ($map.schema_version -ne 1) { $errors.Add('knowledge-map.yaml 的 schema_version 必须为 1。') }
if (-not $map.domains -or $map.domains.Count -eq 0) { $errors.Add('knowledge-map.yaml 没有领域。') }

$duplicateIds = @($map.domains | Group-Object id | Where-Object Count -gt 1)
foreach ($group in $duplicateIds) { $errors.Add("领域 ID 重复：$($group.Name)") }

Push-Location $root
try {
    $solutionProjects = @(& dotnet sln DataSync.sln list | Where-Object { $_ -match '\.csproj$' })
    if ($LASTEXITCODE -ne 0) { $errors.Add("dotnet sln DataSync.sln list 失败，退出码 $LASTEXITCODE。") }
}
finally { Pop-Location }
$productRoots = @($solutionProjects | ForEach-Object { ConvertTo-DataSyncRepoPath $_ } | Where-Object { $_ -notmatch '(?i)tests?/' } | ForEach-Object { ($_ -split '/')[0] } | Sort-Object -Unique)
foreach ($productRoot in $productRoots) {
    $covered = @($map.domains.source_paths | Where-Object { (ConvertTo-DataSyncRepoPath $_).StartsWith("$productRoot/", [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
    if (-not $covered) { $errors.Add("解决方案产品项目尚无知识映射：$productRoot") }
}

$mappedReferences = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($domain in $map.domains) {
    foreach ($required in @('id', 'title', 'skill', 'keywords', 'source_paths', 'references', 'review_record', 'impact_types', 'last_verified_commit')) {
        if (-not $domain.PSObject.Properties.Name.Contains($required) -or -not $domain.$required) {
            $errors.Add("领域 $($domain.id) 缺少 $required。")
        }
    }

    $skillPath = Join-Path $root ".agents/skills/$($domain.skill)/SKILL.md"
    if (-not (Test-Path -LiteralPath $skillPath -PathType Leaf)) {
        $errors.Add("领域 $($domain.id) 的 Skill 不存在：$skillPath")
    }

    foreach ($reference in $domain.references) {
        [void]$mappedReferences.Add((ConvertTo-DataSyncRepoPath $reference))
        $referencePath = Join-Path $root $reference
        if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) {
            $errors.Add("领域 $($domain.id) 的 reference 不存在：$reference")
        }
    }

    $reviewRecordPath = Join-Path $root $domain.review_record
    if (-not (Test-Path -LiteralPath $reviewRecordPath -PathType Leaf)) {
        $errors.Add("领域 $($domain.id) 的 review record 不存在：$($domain.review_record)")
    }
    else {
        try {
            $reviewRecord = Get-Content -LiteralPath $reviewRecordPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($reviewRecord.domain -ne $domain.id) { $errors.Add("领域 $($domain.id) 的 review record 域名不匹配。") }
            if (-not $reviewRecord.reviewed_source_commit) { $errors.Add("领域 $($domain.id) 的 review record 缺少 reviewed_source_commit。") }
            elseif ($reviewRecord.reviewed_source_commit -ne $domain.last_verified_commit) {
                $errors.Add("领域 $($domain.id) 的 review record 未绑定 last_verified_commit。")
            }
            else {
                & git -C $root cat-file -e "$($reviewRecord.reviewed_source_commit)^{commit}" 2>$null
                if ($LASTEXITCODE -ne 0) { $errors.Add("领域 $($domain.id) 的 review record 绑定了无效 Git commit。") }
            }
        }
        catch { $errors.Add("领域 $($domain.id) 的 review record 无效：$($_.Exception.Message)") }
    }

    foreach ($pattern in $domain.source_paths) {
        $prefix = (ConvertTo-DataSyncRepoPath $pattern).Split('*')[0].TrimEnd('/')
        if ($prefix -and -not (Test-Path -LiteralPath (Join-Path $root $prefix))) {
            $warnings.Add("领域 $($domain.id) 的源码映射前缀当前不存在：$pattern")
        }
    }

    Push-Location $root
    try {
        & git cat-file -e "$($domain.last_verified_commit)^{commit}" 2>$null
        if ($LASTEXITCODE -ne 0) { $errors.Add("领域 $($domain.id) 的 last_verified_commit 无效。") }
    }
    finally { Pop-Location }
}

$skillFiles = @(Get-ChildItem -LiteralPath (Join-Path $root '.agents/skills') -Filter SKILL.md -Recurse -File)
foreach ($skillFile in $skillFiles) {
    $text = Get-Content -LiteralPath $skillFile.FullName -Raw -Encoding UTF8
    $lines = @(Get-Content -LiteralPath $skillFile.FullName -Encoding UTF8)
    if ($text -notmatch '(?s)^---\r?\nname: [a-z0-9-]+\r?\ndescription: .+?\r?\n---') {
        $errors.Add("Skill frontmatter 无效：$($skillFile.FullName)")
    }
    if ($lines.Count -gt 150) { $errors.Add("Skill 超过 150 行：$($skillFile.FullName) ($($lines.Count))") }
}

$referenceFiles = @(Get-ChildItem -LiteralPath (Join-Path $root '.agents/skills') -Filter *.md -Recurse -File | Where-Object { $_.FullName -match '[\\/]references[\\/]' })
foreach ($referenceFile in $referenceFiles) {
    $relative = ConvertTo-DataSyncRepoPath ([IO.Path]::GetRelativePath($root, $referenceFile.FullName))
    if (-not $mappedReferences.Contains($relative)) { $warnings.Add("reference 未登记到知识映射：$relative") }
}

$agentMetadataFiles = @(Get-ChildItem -LiteralPath (Join-Path $root '.agents/skills') -Filter openai.yaml -Recurse -File)
$reviewRecordFiles = @(Get-ChildItem -LiteralPath (Join-Path $root '.agents/knowledge-reviews') -Filter *.json -File)
$configFiles = @((Get-Item -LiteralPath (Join-Path $root '.codex/config.toml')))
$toolFiles = @(Get-ChildItem -LiteralPath (Join-Path $root '.codex/tools') -File -Recurse)
$workflowFiles = @(Get-ChildItem -LiteralPath (Join-Path $root '.github') -File -Recurse)
$mapFile = Get-Item -LiteralPath (Join-Path $root '.agents/knowledge-map.yaml')
$timelineFiles = @($skillFiles) + $referenceFiles + $agentMetadataFiles + $reviewRecordFiles + @($mapFile)
$secretFiles = $timelineFiles + $configFiles + $toolFiles + $workflowFiles
$forbiddenTimeline = '(?im)^#{1,4}\s*(recent sync|更新记录|变更流水)|^更新时间：|当前.*数量快照|通过\s*\d+\s*项测试'
foreach ($file in $timelineFiles) {
    $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    if ($text -match $forbiddenTimeline) { $errors.Add("知识文件包含易过期流水或动态测试数字：$($file.FullName)") }
}
foreach ($file in $secretFiles) {
    $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    if (Test-DataSyncSecretText $text) { $errors.Add("知识文件疑似包含凭据：$($file.FullName)") }
}

foreach ($warning in $warnings) { Write-Warning $warning }
if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output "知识校验通过：$($map.domains.Count) 个领域，$($skillFiles.Count) 个 Skill，$($referenceFiles.Count) 个 reference；警告 $($warnings.Count) 项。"
