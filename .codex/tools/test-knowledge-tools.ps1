[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Equal {
    param([object]$Actual, [object]$Expected, [string]$Message)
    if ($Actual -ne $Expected) { throw "$Message；预期 '$Expected'，实际 '$Actual'。" }
}

function Invoke-Resolver {
    param([string]$Query, [string[]]$Paths = @())
    $json = & (Join-Path $PSScriptRoot 'resolve-knowledge.ps1') -Query $Query -Paths $Paths
    return ($json | ConvertFrom-Json)
}

Import-Module (Join-Path $PSScriptRoot 'KnowledgeTools.psm1') -Force
Assert-Equal (ConvertTo-DataSyncRepoPath '.agents/knowledge-map.yaml') '.agents/knowledge-map.yaml' '点目录路径不得丢失前导点'
Assert-Equal (ConvertTo-DataSyncRepoPath './.agents/knowledge-map.yaml') '.agents/knowledge-map.yaml' '只能移除精确的 ./ 前缀'
Assert-Equal (ConvertTo-DataSyncRepoPath '../outside') '../outside' '不得裁剪父目录路径中的点'
foreach ($secretSample in @(
    ('client_' + 'secret = "sample-sensitive-value"'),
    ('-----BEGIN ' + 'OPENSSH PRIVATE KEY-----'),
    ('-----BEGIN ' + 'ENCRYPTED PRIVATE KEY-----'),
    ('p' + 'wd=sample-sensitive-value'),
    ('github_' + 'pat_abcdefghijklmnop'),
    ('eyJabcdefghijk' + '.abcdefghijkl.abcdefghijkl')
)) {
    Assert-Equal (Test-DataSyncSecretText $secretSample) $true "秘密样例未被识别：$secretSample"
}
Assert-Equal (Test-DataSyncSecretText '文档仅说明 Token 不得泄露，不包含实际值。') $false '普通安全说明不应被误报'

$root = Get-DataSyncRepositoryRoot
$map = Get-DataSyncKnowledgeMap -RepositoryRoot $root
$architecture = $map.domains | Where-Object id -eq 'architecture'
$cyyyDomain = $map.domains | Where-Object id -eq 'cyyy-ingestion'
$lhyyDomain = $map.domains | Where-Object id -eq 'lhyy-esb'
foreach ($requiredPath in @('DataSync.Common/DataSync.Common.csproj', 'DataSync.CYYY/DataSync.CYYY.csproj', 'DataSync.CYYY/Dockerfile', 'DataSync.LHYY.V2/DataSync.LHYY.V2.csproj', 'DataSync.LHYY.V2/Dockerfile')) {
    if ($architecture.source_paths -notcontains $requiredPath) { throw "架构映射缺少高影响入口：$requiredPath" }
}
if ($cyyyDomain.source_paths -notcontains 'DataSync.CYYY/Scripts/**') { throw 'CYYY 映射缺少 Scripts。' }
if ($lhyyDomain.source_paths -notcontains 'DataSync.LHYY.V2/Data/**') { throw 'LHYY 映射缺少 Data/DbContext。' }
foreach ($domain in $map.domains) {
    $record = Get-Content -LiteralPath (Join-Path $root $domain.review_record) -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Equal $record.reviewed_source_commit $domain.last_verified_commit "领域 $($domain.id) 的 review record 未对应知识基线"
    & git -C $root cat-file -e "$($record.reviewed_source_commit)^{commit}" 2>$null
    Assert-Equal $LASTEXITCODE 0 "领域 $($domain.id) 的 review record Git commit 无效"
}

$hospitalContract = Get-Content -LiteralPath (Join-Path $root '.agents/skills/datasync-business-logic/references/followup-hospital-sync.md') -Raw -Encoding UTF8
foreach ($requiredContract in @('严格 64 字节密钥材料', '前 32 字节', '后 32 字节', 'IV 必须为 16 字节', 'IV + payload.bin 密文', '64 位十六进制字符串')) {
    if (-not $hospitalContract.Contains($requiredContract)) { throw "医院包稳定加密契约缺失：$requiredContract" }
}
$lhyyContract = Get-Content -LiteralPath (Join-Path $root '.agents/skills/datasync-business-logic/references/lhyy-esb.md') -Raw -Encoding UTF8
foreach ($requiredContract in @('FilterScope.MessageCheck = 0', 'FilterScope.RowFilter = 1')) {
    if (-not $lhyyContract.Contains($requiredContract)) { throw "过滤范围稳定语义缺失：$requiredContract" }
}

$cyyy = Invoke-Resolver -Query '排查 CYYY 数据湖主动采集补数据失败'
Assert-Equal $cyyy.recommended[0].id 'cyyy-ingestion' 'CYYY 采集任务路由错误'

$hospital = Invoke-Resolver -Query '医院数据包导入后附件恢复和 ACK 异常'
Assert-Equal $hospital.recommended[0].id 'hospital-sync' '医院回传任务路由错误'

$pathMatch = Invoke-Resolver -Query '检查改动影响' -Paths 'DataSync.LHYY.V2/Services/InterfaceRecognitionService.cs'
Assert-Equal $pathMatch.recommended[0].id 'lhyy-esb' '源码路径路由错误'

$unknown = Invoke-Resolver -Query '帮我了解这个仓库'
Assert-Equal $unknown.recommended[0].id 'architecture' '未知领域应回退架构入口'

$detector = Join-Path $PSScriptRoot 'detect-knowledge-impact.ps1'
$mapOnly = (& $detector -ChangedFiles @('DataSync.CYYY/Services/IngestionService.cs', '.agents/knowledge-map.yaml')) | ConvertFrom-Json
Assert-Equal $mapOnly.knowledge_update_required $true '全局 map 变化不得解除领域知识门禁'

$unrelatedSkill = (& $detector -ChangedFiles @('DataSync.CYYY/Services/IngestionService.cs', '.agents/skills/datasync-business-logic/SKILL.md')) | ConvertFrom-Json
Assert-Equal $unrelatedSkill.knowledge_update_required $true '同 Skill 任意文件变化不得解除领域知识门禁'

$exactReference = (& $detector -ChangedFiles @('DataSync.CYYY/Services/IngestionService.cs', '.agents/skills/datasync-business-logic/references/cyyy-ingestion.md')) | ConvertFrom-Json
Assert-Equal $exactReference.knowledge_update_required $false '精确领域 reference 应解除知识门禁'

$reviewRecord = (& $detector -ChangedFiles @('DataSync.CYYY/Services/IngestionService.cs', '.agents/knowledge-reviews/cyyy-ingestion.json')) | ConvertFrom-Json
Assert-Equal $reviewRecord.knowledge_update_required $true 'review record 不得解除知识门禁'

$rootChange = (& $detector -ChangedFiles @('DataSync.sln')) | ConvertFrom-Json
Assert-Equal $rootChange.knowledge_update_required $true '解决方案入口变化必须触发知识门禁'

& (Join-Path $PSScriptRoot 'lint-knowledge.ps1')
$drift = (& (Join-Path $PSScriptRoot 'audit-knowledge-drift.ps1')) | ConvertFrom-Json
Assert-Equal $drift.stale_domains.Count 0 '当前 HEAD 不应存在知识漂移'
Write-Output '知识治理脚本测试通过。'
